using Concordant.Internal;
using Concordant.Shared;
using Concordant.Sync;
using Concordant.Sync.Native;
using Concordant.Transactions;

namespace Concordant;

/// <summary>
/// Caller-serialized, memory-only Concordant document. Concurrent or reentrant calls fail predictably.
/// </summary>
public sealed class ConcordantDocument : IDisposable
{
    private readonly ConcordantDocumentOptions _options;
    private readonly DocumentStore _store;
    private readonly LocalClock _clock;
    private readonly IUpdateCodec _codec;
    private readonly Dictionary<string, SharedMap> _rootMaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedArray> _rootArrays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedText> _rootTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<OpId, SharedMap> _nestedMaps = new();
    private readonly Dictionary<OpId, SharedArray> _nestedArrays = new();
    private readonly Dictionary<OpId, SharedText> _nestedTexts = new();

    private bool _inCall;
    private Transaction? _activeTransaction;
    private bool _disposed;
    private readonly List<Action<OperationBatch, object?>> _transactionObservers = new();

    public ConcordantDocument()
        : this(null)
    {
    }

    public ConcordantDocument(ConcordantDocumentOptions? options)
        : this(options, codec: null)
    {
    }

    /// <summary>
    /// Creates a document. When <paramref name="codec"/> is null, the native binary codec is used.
    /// Custom codecs are experimental and still subject to core revalidation.
    /// </summary>
    public ConcordantDocument(ConcordantDocumentOptions? options, IUpdateCodec? codec)
    {
        _options = options ?? new ConcordantDocumentOptions();
        SessionId = _options.WriterSession ?? SessionId.CreateCryptographic();
        _clock = new LocalClock(SessionId);
        _store = new DocumentStore(_options);
        _codec = codec ?? NativeUpdateCodec.Instance;
    }

    public SessionId SessionId { get; }

    public IReadOnlyDictionary<SessionId, ulong> StateVector => _store.StateVector;

    public int PendingOperationCount => _store.PendingCount;

    internal DocumentStore Store => _store;

    /// <summary>
    /// Creates a document from a full-state checkpoint. Requires an empty decode target;
    /// the writer session is always fresh (never restored from the checkpoint).
    /// </summary>
    public static ConcordantDocument CreateFromCheckpoint(
        ReadOnlySpan<byte> checkpointBytes,
        ConcordantDocumentOptions? options = null,
        IUpdateCodec? codec = null)
    {
        var doc = new ConcordantDocument(options, codec);
        ApplyResult result = doc.ApplyUpdate(checkpointBytes, requireCheckpoint: true);
        if (result.Status is not (ApplyStatus.Integrated or ApplyStatus.Duplicate))
        {
            doc.Dispose();
            throw new InvalidOperationException(
                result.Detail ?? $"Checkpoint apply failed with status {result.Status}.");
        }

        return doc;
    }

    /// <summary>
    /// Runs a local transaction. Returns the committed operation batch (also integrated locally),
    /// or <c>null</c> when the transaction made no mutations.
    /// </summary>
    /// <param name="build">Mutation callback.</param>
    /// <param name="origin">
    /// Optional origin tag for selective undo. <see cref="History.UndoManager"/> tracks
    /// <c>null</c> by default; pass a custom origin to filter stacks.
    /// </param>
    public OperationBatch? Transact(Action<ITransaction> build, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        Enter();
        try
        {
            if (_activeTransaction is not null)
            {
                throw CreateConcurrentException();
            }

            var tx = new Transaction(this, _clock);
            _activeTransaction = tx;
            try
            {
                build(tx);
                if (!tx.HasOperations)
                {
                    return null;
                }

                // Ops were eagerly integrated during Append for mid-transaction visibility.
                OperationBatch batch = tx.Complete();
                NotifyTransactionObservers(batch, origin);
                return batch;
            }
            finally
            {
                _activeTransaction = null;
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Registers a post-commit observer for local transactions (used by undo).</summary>
    internal void AddTransactionObserver(Action<OperationBatch, object?> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _transactionObservers.Add(observer);
    }

    /// <summary>Removes a previously registered transaction observer.</summary>
    internal void RemoveTransactionObserver(Action<OperationBatch, object?> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _ = _transactionObservers.Remove(observer);
    }

    private void NotifyTransactionObservers(OperationBatch batch, object? origin)
    {
        if (_transactionObservers.Count == 0)
        {
            return;
        }

        // Snapshot in case an observer mutates the list.
        Action<OperationBatch, object?>[] observers = _transactionObservers.ToArray();
        foreach (Action<OperationBatch, object?> observer in observers)
        {
            try
            {
                observer(batch, origin);
            }
            catch
            {
                // Observer exception isolation.
            }
        }
    }

    /// <summary>
    /// Encodes integrated operations newer than <paramref name="remoteStateVector"/> as a native update.
    /// Empty diffs are valid (header + zero ops).
    /// </summary>
    public byte[] EncodeUpdateSince(IReadOnlyDictionary<SessionId, ulong> remoteStateVector)
    {
        ArgumentNullException.ThrowIfNull(remoteStateVector);
        Enter();
        try
        {
            IReadOnlyList<ConcordantOperation> ops = _store.GetOperationsSince(remoteStateVector);
            return _codec.Encode(ops, UpdateEncodeKind.Update);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Encodes every integrated operation as a full-state checkpoint.</summary>
    public byte[] EncodeFullState()
    {
        Enter();
        try
        {
            IReadOnlyList<ConcordantOperation> ops = _store.GetIntegratedOperations();
            return _codec.Encode(ops, UpdateEncodeKind.Checkpoint);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Decodes update/checkpoint bytes via the document codec, revalidates the canonical batch,
    /// and merges. Never replaces document state. Rejected updates leave zero partial mutation.
    /// </summary>
    public ApplyResult ApplyUpdate(ReadOnlySpan<byte> updateBytes) =>
        ApplyUpdate(updateBytes, requireCheckpoint: false);

    /// <summary>Applies a canonical operation batch (remote or test path). Atomic; no partial mutation on rejection.</summary>
    public ApplyResult Apply(OperationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Enter();
        try
        {
            if (_activeTransaction is not null)
            {
                return ApplyResult.Rejected(
                    "Cannot apply while a transaction is active.",
                    ApplyRejectReason.ConcurrentCall,
                    _store.SnapshotStateVector());
            }

            return ApplyBatchCore(batch);
        }
        finally
        {
            Exit();
        }
    }

    private ApplyResult ApplyUpdate(ReadOnlySpan<byte> updateBytes, bool requireCheckpoint)
    {
        Enter();
        try
        {
            if (_activeTransaction is not null)
            {
                return ApplyResult.Rejected(
                    "Cannot apply while a transaction is active.",
                    ApplyRejectReason.ConcurrentCall,
                    _store.SnapshotStateVector());
            }

            if (requireCheckpoint && !_store.IsEmpty)
            {
                return ApplyResult.Rejected(
                    "CreateFromCheckpoint requires an empty document.",
                    ApplyRejectReason.Invalid,
                    _store.SnapshotStateVector());
            }

            var limits = new CodecDecodeLimits(
                _options.MaxUpdateBytes,
                _options.MaxOperations,
                _options.MaxContentUtf16Length);

            CodecDecodeResult decoded = _codec.Decode(updateBytes, limits);
            if (!decoded.Success)
            {
                return ApplyResult.Rejected(
                    decoded.Error ?? "Decode failed.",
                    decoded.RejectReason,
                    _store.SnapshotStateVector(),
                    retryable: decoded.RejectReason is ApplyRejectReason.QuotaExceeded,
                    codecVersion: decoded.Version == 0 ? null : decoded.Version,
                    requiredFeatures: decoded.Version == 0 && decoded.RequiredFeatures == 0
                        ? null
                        : decoded.RequiredFeatures);
            }

            if (requireCheckpoint && decoded.Kind != UpdateEncodeKind.Checkpoint)
            {
                return ApplyResult.Rejected(
                    "Expected a checkpoint payload.",
                    ApplyRejectReason.Invalid,
                    _store.SnapshotStateVector(),
                    codecVersion: decoded.Version,
                    requiredFeatures: decoded.RequiredFeatures);
            }

            if (decoded.Operations.Count == 0)
            {
                return WithCodecMeta(
                    ApplyResult.Duplicate(_store.SnapshotStateVector()),
                    decoded.Version,
                    decoded.RequiredFeatures);
            }

            ApplyResult result = ApplyBatchCore(new OperationBatch(decoded.Operations));
            return WithCodecMeta(result, decoded.Version, decoded.RequiredFeatures);
        }
        finally
        {
            Exit();
        }
    }

    private ApplyResult ApplyBatchCore(OperationBatch batch)
    {
        var warnings = new List<ConcordantWarning>();
        Action<ConcordantWarning>? original = _options.WarningHandler;

        ApplyResult result = _store.Apply(batch, warning =>
        {
            warnings.Add(warning);
            if (original is null)
            {
                return;
            }

            try
            {
                original(warning);
            }
            catch
            {
                // Observer exception isolation.
            }
        });

        if (result.Status is ApplyStatus.Integrated or ApplyStatus.Duplicate)
        {
            foreach (ConcordantOperation op in batch.Operations)
            {
                if (_store.Operations.ContainsKey(op.Id))
                {
                    _clock.ObserveOp(op.Id, op.Lamport);
                }
            }
        }

        if (warnings.Count == 0)
        {
            return result;
        }

        return new ApplyResult(
            result.Status,
            result.Detail,
            result.Reason,
            result.StateVector,
            result.MissingRanges,
            warnings,
            result.Retryable,
            result.CodecVersion,
            result.RequiredFeatures);
    }

    private static ApplyResult WithCodecMeta(ApplyResult result, int version, uint requiredFeatures) =>
        new(
            result.Status,
            result.Detail,
            result.Reason,
            result.StateVector,
            result.MissingRanges,
            result.Warnings,
            result.Retryable,
            version,
            requiredFeatures);

    public SharedMap GetOrCreateMap(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureReadable();
        RootKind? kind = _store.TryGetRootKind(name);
        if (kind == RootKind.Map)
        {
            return GetMapHandle(name);
        }

        if (kind is not null)
        {
            throw new InvalidOperationException($"Root '{name}' is {kind}, not Map.");
        }

        _ = Transact(tx => tx.GetOrCreateMap(name));
        return GetMapHandle(name);
    }

    public SharedArray GetOrCreateArray(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureReadable();
        RootKind? kind = _store.TryGetRootKind(name);
        if (kind == RootKind.Array)
        {
            return GetArrayHandle(name);
        }

        if (kind is not null)
        {
            throw new InvalidOperationException($"Root '{name}' is {kind}, not Array.");
        }

        _ = Transact(tx => tx.GetOrCreateArray(name));
        return GetArrayHandle(name);
    }

    public SharedText GetOrCreateText(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureReadable();
        RootKind? kind = _store.TryGetRootKind(name);
        if (kind == RootKind.Text)
        {
            return GetTextHandle(name);
        }

        if (kind is not null)
        {
            throw new InvalidOperationException($"Root '{name}' is {kind}, not Text.");
        }

        _ = Transact(tx => tx.GetOrCreateText(name));
        return GetTextHandle(name);
    }

    public SharedMap GetMap(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureReadable();
        RootKind? kind = _store.TryGetRootKind(name);
        if (kind is null)
        {
            throw new InvalidOperationException($"Root '{name}' does not exist.");
        }

        if (kind != RootKind.Map)
        {
            throw new InvalidOperationException($"Root '{name}' is {kind}, not Map.");
        }

        return GetMapHandle(name);
    }

    public SharedArray GetArray(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureReadable();
        RootKind? kind = _store.TryGetRootKind(name);
        if (kind is null)
        {
            throw new InvalidOperationException($"Root '{name}' does not exist.");
        }

        if (kind != RootKind.Array)
        {
            throw new InvalidOperationException($"Root '{name}' is {kind}, not Array.");
        }

        return GetArrayHandle(name);
    }

    public SharedText GetText(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureReadable();
        RootKind? kind = _store.TryGetRootKind(name);
        if (kind is null)
        {
            throw new InvalidOperationException($"Root '{name}' does not exist.");
        }

        if (kind != RootKind.Text)
        {
            throw new InvalidOperationException($"Root '{name}' is {kind}, not Text.");
        }

        return GetTextHandle(name);
    }

    public RootKind? TryGetRootKind(string name)
    {
        EnsureReadable();
        return _store.TryGetRootKind(name);
    }

    public bool HasRootConflict(string name)
    {
        EnsureReadable();
        return _store.HasRootConflict(name);
    }

    /// <summary>
    /// Runs always-safe pending compaction (dedupe of already-integrated pending entries).
    /// Does not remove tombstones or integrated history. Intended as a maintenance hook.
    /// </summary>
    public int Normalize()
    {
        Enter();
        try
        {
            return _store.NormalizePending();
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Visible-state fingerprint compatible with the Phase 1 reference oracle format.</summary>
    public string VisibleFingerprint()
    {
        EnsureReadable();
        return _store.VisibleFingerprint();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    internal void EnsureReadable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal Transaction RequireTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activeTransaction is null)
        {
            throw new InvalidOperationException("Mutations require an active Transact(...) scope.");
        }

        return _activeTransaction;
    }

    internal void EnsureRootDeclared(Transaction tx, string name, RootKind kind)
    {
        RootKind? existing = _store.TryGetRootKind(name);
        if (existing is null)
        {
            tx.DeclareRoot(name, kind);
            return;
        }

        if (existing != kind)
        {
            throw new InvalidOperationException($"Root '{name}' is {existing}, not {kind}.");
        }
    }

    internal SharedMap GetMapHandle(string name) => GetMapHandle(ContainerRef.Root(name));

    internal SharedMap GetMapHandle(ContainerRef container)
    {
        if (container.IsRoot)
        {
            if (!_rootMaps.TryGetValue(container.RootName!, out SharedMap? map))
            {
                map = new SharedMap(this, container);
                _rootMaps[container.RootName!] = map;
            }

            return map;
        }

        OpId id = container.NestedId!.Value;
        if (!_nestedMaps.TryGetValue(id, out SharedMap? nested))
        {
            nested = new SharedMap(this, container);
            _nestedMaps[id] = nested;
        }

        return nested;
    }

    internal SharedArray GetArrayHandle(string name) => GetArrayHandle(ContainerRef.Root(name));

    internal SharedArray GetArrayHandle(ContainerRef container)
    {
        if (container.IsRoot)
        {
            if (!_rootArrays.TryGetValue(container.RootName!, out SharedArray? array))
            {
                array = new SharedArray(this, container);
                _rootArrays[container.RootName!] = array;
            }

            return array;
        }

        OpId id = container.NestedId!.Value;
        if (!_nestedArrays.TryGetValue(id, out SharedArray? nested))
        {
            nested = new SharedArray(this, container);
            _nestedArrays[id] = nested;
        }

        return nested;
    }

    internal SharedText GetTextHandle(string name) => GetTextHandle(ContainerRef.Root(name));

    internal SharedText GetTextHandle(ContainerRef container)
    {
        if (container.IsRoot)
        {
            if (!_rootTexts.TryGetValue(container.RootName!, out SharedText? text))
            {
                text = new SharedText(this, container);
                _rootTexts[container.RootName!] = text;
            }

            return text;
        }

        OpId id = container.NestedId!.Value;
        if (!_nestedTexts.TryGetValue(id, out SharedText? nested))
        {
            nested = new SharedText(this, container);
            _nestedTexts[id] = nested;
        }

        return nested;
    }

    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inCall)
        {
            throw CreateConcurrentException();
        }

        _inCall = true;
    }

    private void Exit() => _inCall = false;

    private static InvalidOperationException CreateConcurrentException() =>
        new("ConcordantDocument is caller-serialized; concurrent or reentrant calls are not allowed.");
}
