namespace Concordant.Persistence.Abstractions.Tests;

public sealed class CheckpointStoreContractTests
{
    [Fact]
    public async Task Save_then_load_round_trips_opaque_bytes()
    {
        var store = new InMemoryCheckpointStore();
        Assert.Null(await store.TryLoadAsync());

        var checkpoint = new ConcordantCheckpoint(
            fullState: new byte[] { 10, 20, 30 },
            stateVector: new byte[] { 1, 2 },
            coveredLogSequence: 4);

        await store.SaveAsync(checkpoint);

        ConcordantCheckpoint? loaded = await store.TryLoadAsync();
        Assert.NotNull(loaded);
        Assert.True(loaded!.FullState.Span.SequenceEqual(checkpoint.FullState.Span));
        Assert.True(loaded.StateVector.Span.SequenceEqual(checkpoint.StateVector.Span));
        Assert.Equal(4, loaded.CoveredLogSequence);
    }

    [Fact]
    public async Task Save_replaces_previous_checkpoint()
    {
        var store = new InMemoryCheckpointStore();
        await store.SaveAsync(new ConcordantCheckpoint(new byte[] { 1 }, new byte[] { 9 }, coveredLogSequence: 1));
        await store.SaveAsync(new ConcordantCheckpoint(new byte[] { 2, 2 }, new byte[] { 8, 8 }, coveredLogSequence: 5));

        ConcordantCheckpoint? loaded = await store.TryLoadAsync();
        Assert.NotNull(loaded);
        Assert.True(loaded!.FullState.Span.SequenceEqual(new byte[] { 2, 2 }));
        Assert.Equal(5, loaded.CoveredLogSequence);
    }

    [Fact]
    public async Task Checkpoint_plus_log_tail_supports_recovery_cursor()
    {
        var log = new InMemoryAppendLog();
        var checkpoints = new InMemoryCheckpointStore();

        long seq1 = await log.AppendAsync(new byte[] { 1 });
        long seq2 = await log.AppendAsync(new byte[] { 2 });
        await checkpoints.SaveAsync(new ConcordantCheckpoint(new byte[] { 100 }, new byte[] { 0 }, seq2));
        await log.TruncateThroughAsync(seq2);
        long seq3 = await log.AppendAsync(new byte[] { 3 });

        ConcordantCheckpoint? checkpoint = await checkpoints.TryLoadAsync();
        Assert.NotNull(checkpoint);
        Assert.Equal(seq2, checkpoint!.CoveredLogSequence);

        var tail = new List<ConcordantLogEntry>();
        await foreach (ConcordantLogEntry entry in log.ReadFromAsync(checkpoint.CoveredLogSequence))
        {
            tail.Add(entry);
        }

        Assert.Single(tail);
        Assert.Equal(seq3, tail[0].Sequence);
        Assert.True(tail[0].Payload.Span.SequenceEqual(new byte[] { 3 }));
        Assert.Equal(1, seq1);
    }

    [Fact]
    public void Covered_sequence_must_be_non_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConcordantCheckpoint(new byte[] { 1 }, ReadOnlyMemory<byte>.Empty, coveredLogSequence: -1));
    }
}
