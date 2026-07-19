namespace Concordant.Persistence.Abstractions.Tests;

/// <summary>In-memory checkpoint store used to exercise <see cref="IConcordantCheckpointStore"/> contracts.</summary>
internal sealed class InMemoryCheckpointStore : IConcordantCheckpointStore
{
    private readonly object _gate = new();
    private ConcordantCheckpoint? _checkpoint;

    public ValueTask SaveAsync(ConcordantCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _checkpoint = new ConcordantCheckpoint(
                checkpoint.FullState.ToArray(),
                checkpoint.StateVector.ToArray(),
                checkpoint.CoveredLogSequence);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ConcordantCheckpoint?> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_checkpoint is null)
            {
                return ValueTask.FromResult<ConcordantCheckpoint?>(null);
            }

            return ValueTask.FromResult<ConcordantCheckpoint?>(
                new ConcordantCheckpoint(
                    _checkpoint.FullState.ToArray(),
                    _checkpoint.StateVector.ToArray(),
                    _checkpoint.CoveredLogSequence));
        }
    }
}
