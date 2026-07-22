using Concordant.Persistence;
using Concordant.Shared;

namespace Concordant.Quickstart;

/// <summary>
/// Demonstrates the host durability boundary:
/// local memory commit → durable append → checkpoint → log replay → open recovery with a fresh writer session.
/// Uses in-memory example stores only (no production adapter).
/// </summary>
public static class Program
{
    public static async Task Main()
    {
        Console.WriteLine($"Concordant Quickstart ({ConcordantAssembly.Name})");
        Console.WriteLine(DurabilityContract.MemoryCommitIsNotDurable);
        Console.WriteLine();

        var log = new ExampleAppendLog();
        var checkpoints = new ExampleCheckpointStore();

        SessionId originalSession;
        string visibleAfterCommit;
        Dictionary<SessionId, ulong> durableFrontier = new();

        // --- Local commit (in-memory only) + durable append ---
        using (var live = new ConcordantDocument())
        {
            originalSession = live.SessionId;
            Console.WriteLine($"Opened live writer session {originalSession}");

            _ = live.Transact(tx =>
            {
                SharedText text = tx.GetOrCreateText("notes");
                text.Insert(0, "hello");
            });

            // Memory is already mutated. Durability is a separate host step.
            visibleAfterCommit = live.GetText("notes").ToString();
            Console.WriteLine($"After in-memory commit: notes = \"{visibleAfterCommit}\"");

            byte[] update = live.EncodeUpdateSince(durableFrontier);
            long sequence = await AppendWithRetryAsync(log, update, failOnce: true);
            durableFrontier = live.StateVector.ToDictionary(static kv => kv.Key, static kv => kv.Value);
            Console.WriteLine($"Durable append succeeded at sequence {sequence} ({update.Length} bytes)");
            Console.WriteLine($"  ({DurabilityContract.FailedAppendRetriesWithoutMemoryRollback})");

            _ = live.Transact(tx =>
            {
                SharedText text = tx.GetOrCreateText("notes");
                text.Insert(text.Length, " world");
            });

            byte[] second = live.EncodeUpdateSince(durableFrontier);
            long sequence2 = await log.AppendAsync(second);
            durableFrontier = live.StateVector.ToDictionary(static kv => kv.Key, static kv => kv.Value);
            Console.WriteLine($"Second durable append at sequence {sequence2}");

            // --- Checkpoint (covers log through tip), then a post-checkpoint update for replay ---
            byte[] fullState = live.EncodeFullState();
            byte[] stateVectorBytes = live.EncodeStateVector();
            long tip = await log.GetTipSequenceAsync();
            var checkpoint = new ConcordantCheckpoint(fullState, stateVectorBytes, coveredLogSequence: tip);
            await checkpoints.SaveAsync(checkpoint);
            await log.TruncateThroughAsync(tip);
            Console.WriteLine($"Checkpoint saved ({fullState.Length} bytes), log truncated through {tip}");

            _ = live.Transact(tx =>
            {
                SharedText text = tx.GetOrCreateText("notes");
                text.Insert(text.Length, "!");
            });
            byte[] postCheckpoint = live.EncodeUpdateSince(durableFrontier);
            long sequence3 = await log.AppendAsync(postCheckpoint);
            Console.WriteLine($"Post-checkpoint append at sequence {sequence3} (will be replayed on recovery)");
            Console.WriteLine($"Live fingerprint: {live.VisibleFingerprint()}");
        }

        // --- Recovery: checkpoint + log tail, fresh writer session ---
        Console.WriteLine();
        Console.WriteLine(DurabilityContract.RecoveryUsesFreshWriterSession);

        ConcordantCheckpoint? saved = await checkpoints.TryLoadAsync()
            ?? throw new InvalidOperationException("Expected a checkpoint.");

        using ConcordantDocument recovered = ConcordantDocument.CreateFromCheckpoint(saved.FullState.Span);
        Console.WriteLine($"Recovered writer session {recovered.SessionId} (was {originalSession})");
        if (recovered.SessionId.Equals(originalSession))
        {
            throw new InvalidOperationException("Recovery must not reuse the prior writer session.");
        }

        await foreach (ConcordantLogEntry entry in log.ReadFromAsync(saved.CoveredLogSequence))
        {
            ApplyResult applied = recovered.ApplyUpdate(entry.Payload.Span);
            Console.WriteLine($"Replayed log sequence {entry.Sequence}: {applied.Status}");
            if (applied.Status is ApplyStatus.Rejected)
            {
                throw new InvalidOperationException(applied.Detail ?? "Log replay rejected.");
            }
        }

        string recoveredText = recovered.GetText("notes").ToString();
        Console.WriteLine($"Recovered notes = \"{recoveredText}\"");
        if (!string.Equals(recoveredText, "hello world!", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected recovered text: {recoveredText}");
        }

        if (!string.Equals(visibleAfterCommit, "hello", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Durability demo precondition failed.");
        }

        Console.WriteLine();
        Console.WriteLine("Persistence demo OK.");
    }

    /// <summary>
    /// Models an uncertain first append: memory stays committed; the host retries the same bytes.
    /// </summary>
    private static async Task<long> AppendWithRetryAsync(
        IConcordantAppendLog log,
        byte[] update,
        bool failOnce)
    {
        if (failOnce)
        {
            try
            {
                throw new IOException("Simulated durable append failure.");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Append failed once ({ex.Message}); retrying same payload (no memory rollback).");
            }
        }

        return await log.AppendAsync(update);
    }
}
