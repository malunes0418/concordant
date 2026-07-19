namespace Concordant.Quickstart;

/// <summary>
/// Example in-memory checkpoint store. Not a production store — demonstrates
/// <see cref="Concordant.Persistence.IConcordantCheckpointStore"/> only.
/// </summary>
internal sealed class ExampleCheckpointStore : Concordant.Persistence.IConcordantCheckpointStore
{
    private readonly object _gate = new();
    private Concordant.Persistence.ConcordantCheckpoint? _checkpoint;

    public ValueTask SaveAsync(Concordant.Persistence.ConcordantCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _checkpoint = new Concordant.Persistence.ConcordantCheckpoint(
                checkpoint.FullState.ToArray(),
                checkpoint.StateVector.ToArray(),
                checkpoint.CoveredLogSequence);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Concordant.Persistence.ConcordantCheckpoint?> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_checkpoint is null)
            {
                return ValueTask.FromResult<Concordant.Persistence.ConcordantCheckpoint?>(null);
            }

            return ValueTask.FromResult<Concordant.Persistence.ConcordantCheckpoint?>(
                new Concordant.Persistence.ConcordantCheckpoint(
                    _checkpoint.FullState.ToArray(),
                    _checkpoint.StateVector.ToArray(),
                    _checkpoint.CoveredLogSequence));
        }
    }
}
