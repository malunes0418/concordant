namespace Concordant;

/// <summary>Configurable quotas and writer identity for <see cref="ConcordantDocument"/>.</summary>
public sealed class ConcordantDocumentOptions
{
    /// <summary>
    /// When set, uses this session instead of a CSPRNG identity.
    /// Intended for tests and deterministic fixtures; production hosts should leave this null.
    /// </summary>
    public SessionId? WriterSession { get; init; }

    /// <summary>Maximum integrated operations retained in memory.</summary>
    public long MaxOperations { get; init; } = 10_000_000;

    /// <summary>Maximum distinct historical sessions in the state vector.</summary>
    public long MaxHistoricalSessions { get; init; } = 100_000;

    /// <summary>Maximum pending operations waiting for dependencies.</summary>
    public long MaxPendingOperations { get; init; } = 100_000;

    /// <summary>Approximate maximum pending payload bytes (string content lengths).</summary>
    public long MaxPendingBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Maximum nesting depth for attached shared types.</summary>
    public int MaxNestingDepth { get; init; } = 64;

    /// <summary>Maximum UTF-16 length for a single string scalar or text insert chunk.</summary>
    public long MaxContentUtf16Length { get; init; } = 10_000_000;

    /// <summary>Maximum clock ahead of the integrated frontier accepted into pending.</summary>
    public long MaxClockGap { get; init; } = 100_000;

    /// <summary>
    /// Maximum accepted byte length for a single <c>ApplyUpdate</c> / decode attempt.
    /// Bounds adversarial payloads before parsing.
    /// </summary>
    public long MaxUpdateBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Optional warning sink (root conflicts, etc.). Exceptions are isolated.</summary>
    public Action<ConcordantWarning>? WarningHandler { get; init; }
}
