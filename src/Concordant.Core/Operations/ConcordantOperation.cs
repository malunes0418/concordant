namespace Concordant;

/// <summary>Canonical validated operation. Immutable once constructed.</summary>
public abstract record ConcordantOperation
{
    private ConcordantOperation()
    {
    }

    public required OpId Id { get; init; }

    public required ulong Lamport { get; init; }

    public OpId? LamportSource { get; init; }

    public sealed record RootDeclare(string Name, RootKind Kind) : ConcordantOperation;

    public sealed record MapSet(ContainerRef Map, string Key, ConcordantContent Value) : ConcordantOperation;

    public sealed record SeqInsert(
        ContainerRef Container,
        OpId? LeftOrigin,
        OpId? RightOrigin,
        ConcordantContent Content) : ConcordantOperation;

    public sealed record SeqDelete(OpId TargetId) : ConcordantOperation;
}

/// <summary>Immutable batch of operations from one transaction or remote update.</summary>
/// <summary>Immutable, non-empty batch of canonical operations presented to the apply kernel.</summary>
public sealed class OperationBatch
{
    public OperationBatch(IReadOnlyList<ConcordantOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            throw new ArgumentException("Batch must contain at least one operation.", nameof(operations));
        }

        Operations = operations;
    }

    /// <summary>Operations in the batch (canonical order as supplied by the caller or codec).</summary>
    public IReadOnlyList<ConcordantOperation> Operations { get; }
}

/// <summary>High-level outcome of applying a batch or update payload.</summary>
public enum ApplyStatus
{
    /// <summary>One or more new operations were integrated.</summary>
    Integrated,

    /// <summary>Ops were stored pending missing causal dependencies.</summary>
    PendingDependencies,

    /// <summary>All ops were already known; document state unchanged.</summary>
    Duplicate,

    /// <summary>Validation or quota failure; zero partial mutation.</summary>
    Rejected,
}

/// <summary>Inclusive missing clock interval for a session (dependencies not yet integrated).</summary>
public readonly struct MissingClockRange : IEquatable<MissingClockRange>
{
    public MissingClockRange(SessionId session, ulong fromClockInclusive, ulong toClockInclusive)
    {
        if (fromClockInclusive == 0 || toClockInclusive == 0 || toClockInclusive < fromClockInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(toClockInclusive));
        }

        Session = session;
        FromClockInclusive = fromClockInclusive;
        ToClockInclusive = toClockInclusive;
    }

    public SessionId Session { get; }

    public ulong FromClockInclusive { get; }

    public ulong ToClockInclusive { get; }

    public bool Equals(MissingClockRange other) =>
        Session == other.Session
        && FromClockInclusive == other.FromClockInclusive
        && ToClockInclusive == other.ToClockInclusive;

    public override bool Equals(object? obj) => obj is MissingClockRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Session, FromClockInclusive, ToClockInclusive);

    public override string ToString() => $"{Session}[{FromClockInclusive}..{ToClockInclusive}]";
}

/// <summary>
/// Outcome of <see cref="ConcordantDocument.Apply"/> / <see cref="ConcordantDocument.ApplyUpdate"/>.
/// Rejected updates never partially mutate the document.
/// </summary>
public sealed class ApplyResult
{
    public ApplyResult(
        ApplyStatus status,
        string? detail = null,
        ApplyRejectReason reason = ApplyRejectReason.None,
        IReadOnlyDictionary<SessionId, ulong>? stateVector = null,
        IReadOnlyList<MissingClockRange>? missingRanges = null,
        IReadOnlyList<ConcordantWarning>? warnings = null,
        bool retryable = false,
        int? codecVersion = null,
        uint? requiredFeatures = null)
    {
        Status = status;
        Detail = detail;
        Reason = reason;
        StateVector = stateVector;
        MissingRanges = missingRanges;
        Warnings = warnings ?? Array.Empty<ConcordantWarning>();
        Retryable = retryable;
        CodecVersion = codecVersion;
        RequiredFeatures = requiredFeatures;
    }

    public ApplyStatus Status { get; }

    public string? Detail { get; }

    public ApplyRejectReason Reason { get; }

    /// <summary>Integrated frontier after the attempt (when available).</summary>
    public IReadOnlyDictionary<SessionId, ulong>? StateVector { get; }

    /// <summary>Known missing contiguous clock ranges when status is PendingDependencies.</summary>
    public IReadOnlyList<MissingClockRange>? MissingRanges { get; }

    public IReadOnlyList<ConcordantWarning> Warnings { get; }

    /// <summary>
    /// Whether the caller may usefully retry later (e.g. after delivering missing dependencies).
    /// Unsupported versions and forks are not retryable on the same bytes.
    /// </summary>
    public bool Retryable { get; }

    /// <summary>Native codec version parsed from the update, when applicable.</summary>
    public int? CodecVersion { get; }

    /// <summary>Required feature bitset from the update header, when applicable.</summary>
    public uint? RequiredFeatures { get; }

    public static ApplyResult Integrated(
        IReadOnlyDictionary<SessionId, ulong>? stateVector = null,
        IReadOnlyList<ConcordantWarning>? warnings = null,
        int? codecVersion = null,
        uint? requiredFeatures = null) =>
        new(
            ApplyStatus.Integrated,
            stateVector: stateVector,
            warnings: warnings,
            codecVersion: codecVersion,
            requiredFeatures: requiredFeatures);

    public static ApplyResult Pending(
        string detail,
        IReadOnlyDictionary<SessionId, ulong>? stateVector = null,
        IReadOnlyList<MissingClockRange>? missingRanges = null,
        IReadOnlyList<ConcordantWarning>? warnings = null,
        int? codecVersion = null,
        uint? requiredFeatures = null) =>
        new(
            ApplyStatus.PendingDependencies,
            detail,
            stateVector: stateVector,
            missingRanges: missingRanges,
            warnings: warnings,
            retryable: true,
            codecVersion: codecVersion,
            requiredFeatures: requiredFeatures);

    public static ApplyResult Duplicate(
        IReadOnlyDictionary<SessionId, ulong>? stateVector = null,
        int? codecVersion = null,
        uint? requiredFeatures = null) =>
        new(
            ApplyStatus.Duplicate,
            stateVector: stateVector,
            codecVersion: codecVersion,
            requiredFeatures: requiredFeatures);

    public static ApplyResult Rejected(
        string detail,
        ApplyRejectReason reason = ApplyRejectReason.Invalid,
        IReadOnlyDictionary<SessionId, ulong>? stateVector = null,
        bool retryable = false,
        int? codecVersion = null,
        uint? requiredFeatures = null) =>
        new(
            ApplyStatus.Rejected,
            detail,
            reason,
            stateVector: stateVector,
            retryable: retryable,
            codecVersion: codecVersion,
            requiredFeatures: requiredFeatures);
}

/// <summary>Why an apply/update attempt was rejected (when <see cref="ApplyStatus.Rejected"/>).</summary>
public enum ApplyRejectReason
{
    /// <summary>Not rejected.</summary>
    None = 0,

    /// <summary>Generic validation failure.</summary>
    Invalid = 1,

    /// <summary>Same OpId observed with conflicting payloads.</summary>
    ReplicaFork = 2,

    /// <summary>A configured document quota would be exceeded.</summary>
    QuotaExceeded = 3,

    /// <summary>Arithmetic or structural overflow.</summary>
    Overflow = 4,

    /// <summary>Concurrent or reentrant call on a caller-serialized document.</summary>
    ConcurrentCall = 5,

    /// <summary>Unsupported codec version or required feature bits.</summary>
    UnsupportedVersion = 6,

    /// <summary>Truncated, mistyped, or otherwise unparseable update bytes.</summary>
    MalformedUpdate = 7,
}
