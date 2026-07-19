namespace Concordant.Persistence;

/// <summary>
/// Host-owned store for opaque full-state checkpoints.
/// </summary>
/// <remarks>
/// Checkpoints compact history for recovery: load checkpoint, then replay
/// <see cref="IConcordantAppendLog"/> entries after <see cref="ConcordantCheckpoint.CoveredLogSequence"/>.
/// Opening recovery must use a fresh writer session; never restore writable clocks from a checkpoint.
/// See <see cref="DurabilityContract.RecoveryUsesFreshWriterSession"/>.
/// </remarks>
public interface IConcordantCheckpointStore
{
    /// <summary>Persists (replaces) the latest checkpoint blob.</summary>
    ValueTask SaveAsync(ConcordantCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Loads the latest checkpoint, or <c>null</c> when none has been saved.</summary>
    ValueTask<ConcordantCheckpoint?> TryLoadAsync(CancellationToken cancellationToken = default);
}
