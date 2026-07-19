namespace Concordant.Persistence.Abstractions.Tests;

public sealed class AppendLogContractTests
{
    [Fact]
    public async Task Append_then_read_preserves_payload_and_order()
    {
        var log = new InMemoryAppendLog();
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5];

        long seq1 = await log.AppendAsync(first);
        long seq2 = await log.AppendAsync(second);

        Assert.Equal(1, seq1);
        Assert.Equal(2, seq2);
        Assert.Equal(2, await log.GetTipSequenceAsync());

        List<ConcordantLogEntry> entries = await ReadAllAsync(log);
        Assert.Equal(2, entries.Count);
        Assert.Equal(seq1, entries[0].Sequence);
        Assert.True(entries[0].Payload.Span.SequenceEqual(first));
        Assert.Equal(seq2, entries[1].Sequence);
        Assert.True(entries[1].Payload.Span.SequenceEqual(second));
    }

    [Fact]
    public async Task Empty_append_is_rejected()
    {
        var log = new InMemoryAppendLog();
        await Assert.ThrowsAsync<ArgumentException>(async () => await log.AppendAsync(ReadOnlyMemory<byte>.Empty));
        Assert.Equal(0, await log.GetTipSequenceAsync());
    }

    [Fact]
    public async Task Failed_append_leaves_tip_unchanged_and_retry_succeeds()
    {
        var inner = new InMemoryAppendLog();
        var flaky = new FlakyAppendLog(inner, failuresBeforeSuccess: 1);
        byte[] payload = [9, 9, 9];

        await Assert.ThrowsAsync<IOException>(async () => await flaky.AppendAsync(payload));
        Assert.Equal(0, await inner.GetTipSequenceAsync());

        // Durability contract: hosts keep in-memory commit and retry the same bytes.
        long sequence = await flaky.AppendAsync(payload);
        Assert.Equal(1, sequence);
        Assert.Equal(1, await inner.GetTipSequenceAsync());

        List<ConcordantLogEntry> entries = await ReadAllAsync(inner);
        Assert.Single(entries);
        Assert.True(entries[0].Payload.Span.SequenceEqual(payload));
    }

    [Fact]
    public async Task Idempotent_retry_may_append_duplicate_payloads()
    {
        var log = new InMemoryAppendLog();
        byte[] payload = [7, 7];

        long first = await log.AppendAsync(payload);
        // At-least-once retry after an uncertain success: duplicate log entry is allowed.
        long second = await log.AppendAsync(payload);

        Assert.Equal(1, first);
        Assert.Equal(2, second);

        List<ConcordantLogEntry> entries = await ReadAllAsync(log);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].Payload.Span.SequenceEqual(payload));
        Assert.True(entries[1].Payload.Span.SequenceEqual(payload));
        Assert.Contains("Duplicate", DurabilityContract.AppendRetriesAreIdempotentAtDocumentLayer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncate_through_drops_covered_prefix()
    {
        var log = new InMemoryAppendLog();
        _ = await log.AppendAsync(new byte[] { 1 });
        _ = await log.AppendAsync(new byte[] { 2 });
        _ = await log.AppendAsync(new byte[] { 3 });

        await log.TruncateThroughAsync(2);

        List<ConcordantLogEntry> entries = await ReadAllAsync(log, afterSequenceExclusive: 0);
        Assert.Single(entries);
        Assert.Equal(3, entries[0].Sequence);
        Assert.True(entries[0].Payload.Span.SequenceEqual(new byte[] { 3 }));

        List<ConcordantLogEntry> tail = await ReadAllAsync(log, afterSequenceExclusive: 2);
        Assert.Single(tail);
        Assert.Equal(3, tail[0].Sequence);
    }

    [Fact]
    public async Task Read_from_respects_exclusive_lower_bound()
    {
        var log = new InMemoryAppendLog();
        _ = await log.AppendAsync(new byte[] { 1 });
        long mid = await log.AppendAsync(new byte[] { 2 });
        _ = await log.AppendAsync(new byte[] { 3 });

        List<ConcordantLogEntry> tail = await ReadAllAsync(log, afterSequenceExclusive: mid);
        Assert.Single(tail);
        Assert.Equal(3, tail[0].Sequence);
    }

    private static async Task<List<ConcordantLogEntry>> ReadAllAsync(
        IConcordantAppendLog log,
        long afterSequenceExclusive = 0)
    {
        var list = new List<ConcordantLogEntry>();
        await foreach (ConcordantLogEntry entry in log.ReadFromAsync(afterSequenceExclusive))
        {
            list.Add(entry);
        }

        return list;
    }

    /// <summary>Wraps a log and fails the first N appends to model uncertain durability.</summary>
    private sealed class FlakyAppendLog : IConcordantAppendLog
    {
        private readonly IConcordantAppendLog _inner;
        private int _remainingFailures;

        public FlakyAppendLog(IConcordantAppendLog inner, int failuresBeforeSuccess)
        {
            _inner = inner;
            _remainingFailures = failuresBeforeSuccess;
        }

        public ValueTask<long> AppendAsync(ReadOnlyMemory<byte> update, CancellationToken cancellationToken = default)
        {
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                return ValueTask.FromException<long>(new IOException("Simulated append failure."));
            }

            return _inner.AppendAsync(update, cancellationToken);
        }

        public IAsyncEnumerable<ConcordantLogEntry> ReadFromAsync(
            long afterSequenceExclusive = 0,
            CancellationToken cancellationToken = default) =>
            _inner.ReadFromAsync(afterSequenceExclusive, cancellationToken);

        public ValueTask TruncateThroughAsync(long inclusiveSequence, CancellationToken cancellationToken = default) =>
            _inner.TruncateThroughAsync(inclusiveSequence, cancellationToken);

        public ValueTask<long> GetTipSequenceAsync(CancellationToken cancellationToken = default) =>
            _inner.GetTipSequenceAsync(cancellationToken);
    }
}
