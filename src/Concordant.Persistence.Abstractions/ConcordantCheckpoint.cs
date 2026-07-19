namespace Concordant.Persistence;

/// <summary>
/// Opaque full-state checkpoint plus the state-vector encoding it covers and the append-log
/// sequence that may be truncated after a successful save.
/// </summary>
public sealed class ConcordantCheckpoint
{
    public ConcordantCheckpoint(
        ReadOnlyMemory<byte> fullState,
        ReadOnlyMemory<byte> stateVector,
        long coveredLogSequence)
    {
        if (coveredLogSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coveredLogSequence), "Covered sequence must be non-negative.");
        }

        FullState = fullState;
        StateVector = stateVector;
        CoveredLogSequence = coveredLogSequence;
    }

    /// <summary>Opaque full-state bytes (typically EncodeFullState / checkpoint codec output).</summary>
    public ReadOnlyMemory<byte> FullState { get; }

    /// <summary>
    /// Opaque state-vector bytes describing the integrated frontier covered by <see cref="FullState"/>.
    /// Encoding is defined by the document/codec layer, not by this package.
    /// </summary>
    public ReadOnlyMemory<byte> StateVector { get; }

    /// <summary>
    /// Inclusive append-log sequence covered by this checkpoint.
    /// Use <c>0</c> when no log entries are covered. Hosts may truncate through this sequence after save.
    /// </summary>
    public long CoveredLogSequence { get; }
}
