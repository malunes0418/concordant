using Concordant.Internal;
using Concordant.Internal.Sequences;
using Concordant.Transactions;
using Concordant.Values;

namespace Concordant.History;

/// <summary>
/// Session-local selective undo/redo. Remote updates are never stacked; history is ephemeral
/// and is not included in checkpoints.
/// </summary>
public sealed class UndoManager : IDisposable
{
    private readonly ConcordantDocument _document;
    private readonly HashSet<object?> _trackedOrigins;
    private readonly int _captureTimeoutMs;
    private readonly int _maxStackTransactions;
    private readonly long _maxStackBytes;
    private readonly Action<OperationBatch, object?> _observer;

    private readonly List<StackItem> _undo = new();
    private readonly List<StackItem> _redo = new();

    private long _undoBytes;
    private long _lastCaptureTimestamp;
    private bool _stopCapturing;
    private int _evictedUndoItems;
    private bool _disposed;
    private bool _applying;

    public UndoManager(ConcordantDocument document, UndoManagerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        options ??= new UndoManagerOptions();

        if (options.MaxStackTransactions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxStackTransactions must be at least 1.");
        }

        if (options.MaxStackBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxStackBytes must be at least 1.");
        }

        if (options.CaptureTimeoutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CaptureTimeoutMilliseconds must be non-negative.");
        }

        _captureTimeoutMs = options.CaptureTimeoutMilliseconds;
        _maxStackTransactions = options.MaxStackTransactions;
        _maxStackBytes = options.MaxStackBytes;

        _trackedOrigins = options.TrackedOrigins is null
            ? new HashSet<object?> { null }
            : new HashSet<object?>(options.TrackedOrigins);

        // Always treat this manager as a tracked origin for hosts that pass it explicitly,
        // but internal undo/redo transactions still skip stacking via <see cref="_applying"/>.
        _ = _trackedOrigins.Add(this);

        _observer = OnLocalTransaction;
        _document.AddTransactionObserver(_observer);
    }

    public bool CanUndo => !_disposed && _undo.Count > 0;

    public bool CanRedo => !_disposed && _redo.Count > 0;

    public int UndoStackCount => _undo.Count;

    public int RedoStackCount => _redo.Count;

    /// <summary>
    /// Forces the next tracked transaction to start a new stack item instead of merging
    /// with the capture-timeout group.
    /// </summary>
    public void StopCapturing()
    {
        EnsureNotDisposed();
        _stopCapturing = true;
    }

    /// <summary>Clears undo and redo stacks. Does not mutate document state.</summary>
    public void Clear()
    {
        EnsureNotDisposed();
        _undo.Clear();
        _redo.Clear();
        _undoBytes = 0;
        _evictedUndoItems = 0;
        _stopCapturing = false;
        _lastCaptureTimestamp = 0;
    }

    public UndoResult Undo() => PopStack(_undo, _redo, isUndo: true);

    public UndoResult Redo() => PopStack(_redo, _undo, isUndo: false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _document.RemoveTransactionObserver(_observer);
        _undo.Clear();
        _redo.Clear();
        _undoBytes = 0;
    }

    private UndoResult PopStack(List<StackItem> source, List<StackItem> destination, bool isUndo)
    {
        EnsureNotDisposed();

        if (source.Count == 0)
        {
            if (isUndo && _evictedUndoItems > 0)
            {
                _evictedUndoItems = 0;
                return UndoResult.HistoryEvicted("Older undo entries were dropped by the stack budget.");
            }

            return UndoResult.Empty();
        }

        StackItem item = source[^1];
        source.RemoveAt(source.Count - 1);
        if (isUndo)
        {
            _undoBytes -= item.ByteSize;
            if (_undoBytes < 0)
            {
                _undoBytes = 0;
            }
        }

        string before = _document.VisibleFingerprint();
        var redoChanges = new List<TrackedChange>();
        bool remoteWinner = false;
        bool attempted = false;

        _applying = true;
        try
        {
            _ = _document.Transact(
                tx =>
                {
                    var tr = (Transaction)tx;
                    // Apply reverse in reverse chronological order.
                    for (int i = item.Changes.Count - 1; i >= 0; i--)
                    {
                        TrackedChange change = item.Changes[i];
                        switch (change)
                        {
                            case TrackedChange.Inserted inserted:
                                attempted = true;
                                TrackedChange? redoDelete = ApplyUndoInsert(tr, inserted);
                                if (redoDelete is not null)
                                {
                                    redoChanges.Add(redoDelete);
                                }

                                break;

                            case TrackedChange.Deleted deleted:
                                attempted = true;
                                TrackedChange? redoInsert = ApplyUndoDelete(tr, deleted);
                                if (redoInsert is not null)
                                {
                                    redoChanges.Add(redoInsert);
                                }

                                break;

                            case TrackedChange.MapAssigned mapAssigned:
                                attempted = true;
                                MapUndoOutcome mapOutcome = isUndo
                                    ? ApplyMapUndo(tr, mapAssigned, out TrackedChange? redoMap)
                                    : ApplyMapRedo(tr, mapAssigned, out redoMap);
                                if (mapOutcome == MapUndoOutcome.RemoteWinner)
                                {
                                    remoteWinner = true;
                                }
                                else if (redoMap is not null)
                                {
                                    redoChanges.Add(redoMap);
                                }

                                break;
                        }
                    }
                },
                origin: this);
        }
        finally
        {
            _applying = false;
        }

        if (redoChanges.Count > 0)
        {
            var redoItem = new StackItem(redoChanges, EstimateChanges(redoChanges), item.Origin);
            destination.Add(redoItem);
            if (!isUndo)
            {
                // Redoing pushes a new undo item; enforce undo budgets only.
                _undoBytes += redoItem.ByteSize;
                EnforceBudgets();
            }
        }

        string after = _document.VisibleFingerprint();
        if (remoteWinner && before == after)
        {
            return UndoResult.RemoteWinner(
                isUndo
                    ? "Map undo skipped because a remote assignment currently wins."
                    : "Map redo skipped because a remote assignment currently wins.");
        }

        if (!attempted || before == after)
        {
            return UndoResult.NoVisibleChange();
        }

        return UndoResult.Applied();
    }

    private TrackedChange? ApplyUndoInsert(Transaction tx, TrackedChange.Inserted inserted)
    {
        if (!_document.Store.TryGetSeqItem(inserted.Id, out ContainerRef container, out SeqItem item))
        {
            return null;
        }

        if (item.Deleted)
        {
            return null;
        }

        // Pin structural anchors before tombstoning so redo can restore precisely.
        OpId? left = item.LeftOrigin;
        OpId? right = item.RightOrigin;
        ConcordantContent content = item.Content;
        tx.SeqDelete(inserted.Id);
        return new TrackedChange.Deleted(inserted.Id, container, left, right, content);
    }

    private TrackedChange? ApplyUndoDelete(Transaction tx, TrackedChange.Deleted deleted)
    {
        // Restore as a new insertion at pinned (possibly tombstoned) origins.
        if (deleted.Content is ConcordantContent.NestedContent nested)
        {
            ConcordantOperation op = tx.SeqInsert(
                deleted.Container,
                deleted.LeftOrigin,
                deleted.RightOrigin,
                ConcordantContent.Nested(nested.Kind));
            DeepCopyNested(tx, deleted.TargetId, op.Id, nested.Kind);
            return new TrackedChange.Inserted(op.Id, deleted.Container, ConcordantContent.Nested(nested.Kind));
        }

        ConcordantOperation restored = tx.SeqInsert(
            deleted.Container,
            deleted.LeftOrigin,
            deleted.RightOrigin,
            deleted.Content);
        return new TrackedChange.Inserted(restored.Id, deleted.Container, deleted.Content);
    }

    private MapUndoOutcome ApplyMapUndo(
        Transaction tx,
        TrackedChange.MapAssigned assigned,
        out TrackedChange? redoChange)
    {
        redoChange = null;
        if (!_document.Store.TryGetMapWinner(assigned.Map, assigned.Key, out OpId winnerId, out _))
        {
            return MapUndoOutcome.Skipped;
        }

        if (winnerId != assigned.AssignmentId)
        {
            if (winnerId.Session != _document.SessionId)
            {
                return MapUndoOutcome.RemoteWinner;
            }

            // A later local assignment owns the key; leave it alone.
            return MapUndoOutcome.Skipped;
        }

        ConcordantContent restoreValue = _document.Store.TryGetMapRunnerUp(
            assigned.Map,
            assigned.Key,
            assigned.AssignmentId,
            out _,
            out ConcordantContent? prior)
            ? prior!
            : ConcordantContent.Scalar(ConcordantScalar.Null);

        _ = tx.Append((id, lamport, source) => new ConcordantOperation.MapSet(
            assigned.Map,
            assigned.Key,
            restoreValue)
        {
            Id = id,
            Lamport = lamport,
            LamportSource = source,
        });

        // Redo re-applies the undone value when no remote assignment wins.
        redoChange = new TrackedChange.MapAssigned(
            assigned.Map,
            assigned.Key,
            assigned.AssignmentId,
            assigned.Value);
        return MapUndoOutcome.Applied;
    }

    private MapUndoOutcome ApplyMapRedo(
        Transaction tx,
        TrackedChange.MapAssigned assigned,
        out TrackedChange? undoChange)
    {
        undoChange = null;
        if (_document.Store.TryGetMapWinner(assigned.Map, assigned.Key, out OpId winnerId, out _)
            && winnerId.Session != _document.SessionId)
        {
            return MapUndoOutcome.RemoteWinner;
        }

        ConcordantOperation op = tx.Append((id, lamport, source) => new ConcordantOperation.MapSet(
            assigned.Map,
            assigned.Key,
            assigned.Value)
        {
            Id = id,
            Lamport = lamport,
            LamportSource = source,
        });

        undoChange = new TrackedChange.MapAssigned(
            assigned.Map,
            assigned.Key,
            op.Id,
            assigned.Value);
        return MapUndoOutcome.Applied;
    }

    private void DeepCopyNested(Transaction tx, OpId oldId, OpId newId, RootKind kind)
    {
        ContainerRef from = ContainerRef.Nested(oldId);
        ContainerRef to = ContainerRef.Nested(newId);

        switch (kind)
        {
            case RootKind.Map:
                foreach (KeyValuePair<string, ConcordantContent> kv in _document.Store.VisibleMap(from))
                {
                    CopyMapEntry(tx, from, to, kv.Key, kv.Value);
                }

                break;

            case RootKind.Array:
                CopySequence(tx, from, to, asText: false);
                break;

            case RootKind.Text:
                CopySequence(tx, from, to, asText: true);
                break;
        }
    }

    private void CopyMapEntry(
        Transaction tx,
        ContainerRef from,
        ContainerRef to,
        string key,
        ConcordantContent value)
    {
        if (value is ConcordantContent.ScalarContent)
        {
            tx.MapSet(to, key, value);
            return;
        }

        if (value is ConcordantContent.NestedContent nested
            && _document.Store.TryGetMapWinner(from, key, out OpId oldNestedId, out _))
        {
            ConcordantOperation op = tx.Append((id, lamport, source) => new ConcordantOperation.MapSet(
                to,
                key,
                ConcordantContent.Nested(nested.Kind))
            {
                Id = id,
                Lamport = lamport,
                LamportSource = source,
            });
            DeepCopyNested(tx, oldNestedId, op.Id, nested.Kind);
        }
    }

    private void CopySequence(Transaction tx, ContainerRef from, ContainerRef to, bool asText)
    {
        YataSequence? seq = _document.Store.TryGetSequence(from);
        if (seq is null)
        {
            return;
        }

        OpId? prev = null;
        foreach (SeqItem item in seq.VisibleItems())
        {
            if (item.Content is ConcordantContent.NestedContent nested)
            {
                ConcordantOperation op = tx.SeqInsert(to, prev, right: null, ConcordantContent.Nested(nested.Kind));
                DeepCopyNested(tx, item.Id, op.Id, nested.Kind);
                prev = op.Id;
            }
            else
            {
                if (asText && item.Content is not ConcordantContent.ScalarContent { Value: ConcordantScalar.StringScalar })
                {
                    continue;
                }

                ConcordantOperation op = tx.SeqInsert(to, prev, right: null, item.Content);
                prev = op.Id;
            }
        }
    }

    private void OnLocalTransaction(OperationBatch batch, object? origin)
    {
        if (_disposed || _applying)
        {
            return;
        }

        // Any non-undo local mutation invalidates redo (including untracked origins).
        _redo.Clear();

        if (!_trackedOrigins.Contains(origin))
        {
            return;
        }

        var changes = new List<TrackedChange>();
        foreach (ConcordantOperation op in batch.Operations)
        {
            TrackedChange? change = Describe(op);
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        if (changes.Count == 0)
        {
            return;
        }

        long size = EstimateChanges(changes);
        long now = Environment.TickCount64;
        bool merge = !_stopCapturing
            && _captureTimeoutMs > 0
            && _undo.Count > 0
            && _lastCaptureTimestamp != 0
            && now - _lastCaptureTimestamp <= _captureTimeoutMs;

        _stopCapturing = false;
        _lastCaptureTimestamp = now;

        if (merge)
        {
            StackItem current = _undo[^1];
            current.Changes.AddRange(changes);
            current.ByteSize += size;
            _undoBytes += size;
        }
        else
        {
            _undo.Add(new StackItem(changes, size, origin));
            _undoBytes += size;
        }

        EnforceBudgets();
    }

    private TrackedChange? Describe(ConcordantOperation op) =>
        op switch
        {
            ConcordantOperation.SeqInsert insert =>
                new TrackedChange.Inserted(insert.Id, insert.Container, insert.Content),

            ConcordantOperation.SeqDelete delete when _document.Store.TryGetSeqItem(
                delete.TargetId,
                out ContainerRef container,
                out SeqItem item) =>
                new TrackedChange.Deleted(
                    delete.TargetId,
                    container,
                    item.LeftOrigin,
                    item.RightOrigin,
                    item.Content),

            ConcordantOperation.MapSet mapSet =>
                new TrackedChange.MapAssigned(mapSet.Map, mapSet.Key, mapSet.Id, mapSet.Value),

            _ => null, // RootDeclare is not reversed by selective undo.
        };

    private void EnforceBudgets()
    {
        while (_undo.Count > _maxStackTransactions
               || (_undo.Count > 0 && _undoBytes > _maxStackBytes))
        {
            if (_undo.Count == 0)
            {
                _undoBytes = 0;
                break;
            }

            StackItem oldest = _undo[0];
            _undo.RemoveAt(0);
            _undoBytes -= oldest.ByteSize;
            _evictedUndoItems++;
        }

        if (_undoBytes < 0)
        {
            _undoBytes = 0;
        }
    }

    private static long EstimateChanges(IReadOnlyList<TrackedChange> changes)
    {
        long total = 0;
        foreach (TrackedChange change in changes)
        {
            total += change.EstimateBytes();
        }

        return total;
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private enum MapUndoOutcome
    {
        Applied,
        RemoteWinner,
        Skipped,
    }

    private sealed class StackItem
    {
        public StackItem(List<TrackedChange> changes, long byteSize, object? origin)
        {
            Changes = changes;
            ByteSize = byteSize;
            Origin = origin;
        }

        public List<TrackedChange> Changes { get; }

        public long ByteSize { get; set; }

        public object? Origin { get; }
    }

    private abstract record TrackedChange
    {
        private TrackedChange()
        {
        }

        public abstract long EstimateBytes();

        /// <summary>A local sequence insertion (undo = delete exact id).</summary>
        public sealed record Inserted(OpId Id, ContainerRef Container, ConcordantContent Content) : TrackedChange
        {
            public override long EstimateBytes() => 64 + ContentBytes(Content);
        }

        /// <summary>
        /// A local sequence deletion with pinned left/right anchors for restore.
        /// </summary>
        public sealed record Deleted(
            OpId TargetId,
            ContainerRef Container,
            OpId? LeftOrigin,
            OpId? RightOrigin,
            ConcordantContent Content) : TrackedChange
        {
            public override long EstimateBytes() => 80 + ContentBytes(Content);
        }

        /// <summary>A local map assignment (undo/redo conditional on remote winners).</summary>
        public sealed record MapAssigned(
            ContainerRef Map,
            string Key,
            OpId AssignmentId,
            ConcordantContent Value) : TrackedChange
        {
            public override long EstimateBytes() => 64 + (Key.Length * 2L) + ContentBytes(Value);
        }

        private static long ContentBytes(ConcordantContent content) => content switch
        {
            ConcordantContent.ScalarContent { Value: ConcordantScalar.StringScalar s } => s.Value.Length * 2L,
            ConcordantContent.ScalarContent => 16,
            ConcordantContent.NestedContent => 8,
            _ => 8,
        };
    }
}
