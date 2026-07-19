using System.Runtime.CompilerServices;

namespace Concordant.Quickstart;

/// <summary>
/// Example in-memory append log. Not a production store — demonstrates <see cref="Concordant.Persistence.IConcordantAppendLog"/> only.
/// </summary>
internal sealed class ExampleAppendLog : Concordant.Persistence.IConcordantAppendLog
{
    private readonly object _gate = new();
    private readonly List<Concordant.Persistence.ConcordantLogEntry> _entries = new();
    private long _nextSequence = 1;

    public ValueTask<long> AppendAsync(ReadOnlyMemory<byte> update, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (update.IsEmpty)
        {
            throw new ArgumentException("Update payload must be non-empty.", nameof(update));
        }

        lock (_gate)
        {
            long sequence = _nextSequence++;
            _entries.Add(new Concordant.Persistence.ConcordantLogEntry(sequence, update.ToArray()));
            return ValueTask.FromResult(sequence);
        }
    }

    public async IAsyncEnumerable<Concordant.Persistence.ConcordantLogEntry> ReadFromAsync(
        long afterSequenceExclusive = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Concordant.Persistence.ConcordantLogEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries
                .Where(e => e.Sequence > afterSequenceExclusive)
                .OrderBy(e => e.Sequence)
                .Select(e => new Concordant.Persistence.ConcordantLogEntry(e.Sequence, e.Payload.ToArray()))
                .ToArray();
        }

        foreach (Concordant.Persistence.ConcordantLogEntry entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
            await Task.Yield();
        }
    }

    public ValueTask TruncateThroughAsync(long inclusiveSequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Sequence <= inclusiveSequence);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<long> GetTipSequenceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_entries.Count == 0 ? 0L : _entries[^1].Sequence);
        }
    }
}
