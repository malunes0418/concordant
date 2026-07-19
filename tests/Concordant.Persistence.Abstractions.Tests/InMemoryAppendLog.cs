using System.Runtime.CompilerServices;

namespace Concordant.Persistence.Abstractions.Tests;

/// <summary>In-memory append log used to exercise <see cref="IConcordantAppendLog"/> contracts.</summary>
internal sealed class InMemoryAppendLog : IConcordantAppendLog
{
    private readonly object _gate = new();
    private readonly List<ConcordantLogEntry> _entries = new();
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
            byte[] copy = update.ToArray();
            _entries.Add(new ConcordantLogEntry(sequence, copy));
            return ValueTask.FromResult(sequence);
        }
    }

    public async IAsyncEnumerable<ConcordantLogEntry> ReadFromAsync(
        long afterSequenceExclusive = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (afterSequenceExclusive < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequenceExclusive));
        }

        ConcordantLogEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries
                .Where(e => e.Sequence > afterSequenceExclusive)
                .OrderBy(e => e.Sequence)
                .Select(e => new ConcordantLogEntry(e.Sequence, e.Payload.ToArray()))
                .ToArray();
        }

        foreach (ConcordantLogEntry entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
            await Task.Yield();
        }
    }

    public ValueTask TruncateThroughAsync(long inclusiveSequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (inclusiveSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inclusiveSequence));
        }

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
