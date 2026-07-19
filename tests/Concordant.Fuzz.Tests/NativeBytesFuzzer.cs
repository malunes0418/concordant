using System.Diagnostics;
using Concordant.Sync;
using Concordant.Sync.Native;

namespace Concordant.Fuzz.Tests;

/// <summary>
/// Fuzzes native wire bytes. Outcomes must be deterministic reject/pending/integrate/duplicate —
/// never crash, hang, quota-bypass, or partially mutate on rejection.
/// </summary>
internal static class NativeBytesFuzzer
{
    public static FuzzReport Run(int seed, int iterations, int maxBytes, TimeSpan timeBudget)
    {
        var rng = new Random(seed);
        var report = new FuzzReport();
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            if (sw.Elapsed > timeBudget)
            {
                break;
            }

            report.Iterations++;
            byte[] payload = CreatePayload(rng, maxBytes);

            using var doc = new ConcordantDocument(new ConcordantDocumentOptions
            {
                WriterSession = SessionId.FromSeed((ulong)(seed + i + 1)),
                MaxUpdateBytes = maxBytes,
                MaxOperations = 256,
                MaxPendingOperations = 64,
                MaxPendingBytes = 64 * 1024,
                MaxClockGap = 64,
                MaxHistoricalSessions = 32,
                MaxContentUtf16Length = 256,
                MaxNestingDepth = 8,
            });

            // Seed a little real state so rejection atomicity is meaningful.
            doc.Transact(tx => tx.GetOrCreateText("t").Insert(0, "ok"));
            string before = doc.VisibleFingerprint();
            int pendingBefore = doc.PendingOperationCount;

            ApplyResult result;
            try
            {
                result = doc.ApplyUpdate(payload);
            }
            catch (Exception ex)
            {
                report.Fail($"iter={i} threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            report.Observe(result.Status);

            if (result.Status == ApplyStatus.Rejected)
            {
                if (doc.VisibleFingerprint() != before || doc.PendingOperationCount != pendingBefore)
                {
                    report.Fail($"iter={i} rejected update partially mutated document");
                }
            }
            else if (result.Status is ApplyStatus.Integrated or ApplyStatus.Duplicate or ApplyStatus.PendingDependencies)
            {
                // Valid path: document must remain readable and fingerprint computable.
                try
                {
                    _ = doc.VisibleFingerprint();
                    _ = doc.EncodeUpdateSince(doc.StateVector);
                }
                catch (Exception ex)
                {
                    report.Fail($"iter={i} post-apply threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return report;
    }

    private static byte[] CreatePayload(Random rng, int maxBytes)
    {
        int mode = rng.Next(6);
        return mode switch
        {
            0 => RandomBytes(rng, rng.Next(0, maxBytes + 1)),
            1 => TruncatedValid(rng),
            2 => ValidHeaderCorruptBody(rng),
            3 => OversizedClaim(rng, maxBytes),
            4 => RandomWithMagic(rng, maxBytes),
            _ => NativeUpdateCodec.Instance.Encode(Array.Empty<ConcordantOperation>(), UpdateEncodeKind.Update),
        };
    }

    private static byte[] RandomBytes(Random rng, int length)
    {
        byte[] bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    private static byte[] TruncatedValid(Random rng)
    {
        using var src = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed((ulong)rng.Next(1, 10_000)) });
        src.Transact(tx => tx.GetOrCreateText("n").Insert(0, "ab"));
        byte[] full = src.EncodeFullState();
        int keep = rng.Next(0, full.Length);
        return full.AsSpan(0, keep).ToArray();
    }

    private static byte[] ValidHeaderCorruptBody(Random rng)
    {
        using var src = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed((ulong)rng.Next(1, 10_000)) });
        src.Transact(tx => tx.GetOrCreateMap("m").Set("k", Concordant.Values.ConcordantScalar.Int64(1)));
        byte[] full = src.EncodeFullState();
        if (full.Length > 20)
        {
            full[rng.Next(20, full.Length)] ^= (byte)rng.Next(1, 256);
        }

        return full;
    }

    private static byte[] OversizedClaim(Random rng, int maxBytes)
    {
        byte[] bytes = new byte[Math.Min(32, maxBytes)];
        "CNCR"u8.CopyTo(bytes);
        bytes[4] = 1; // version
        bytes[5] = 0;
        bytes[6] = 1; // update
        // opCount huge
        bytes[16] = 0xFF;
        bytes[17] = 0xFF;
        bytes[18] = 0xFF;
        bytes[19] = 0x7F;
        rng.NextBytes(bytes.AsSpan(20));
        return bytes;
    }

    private static byte[] RandomWithMagic(Random rng, int maxBytes)
    {
        byte[] bytes = RandomBytes(rng, rng.Next(4, maxBytes + 1));
        "CNCR"u8.CopyTo(bytes);
        bytes[4] = 1;
        bytes[5] = 0;
        return bytes;
    }
}
