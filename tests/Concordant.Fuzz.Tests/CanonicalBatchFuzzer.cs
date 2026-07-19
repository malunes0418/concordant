using System.Diagnostics;
using Concordant.Sync;
using Concordant.Sync.Native;
using Concordant.Values;

namespace Concordant.Fuzz.Tests;

/// <summary>
/// Fuzzes custom-codec canonical batches. Core must revalidate; reject or converge; never crash.
/// </summary>
internal static class CanonicalBatchFuzzer
{
    public static FuzzReport Run(int seed, int iterations, TimeSpan timeBudget)
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
            var codec = new RandomBatchCodec(rng);
            using var doc = new ConcordantDocument(
                new ConcordantDocumentOptions
                {
                    WriterSession = SessionId.FromSeed((ulong)(seed + i + 100)),
                    MaxOperations = 128,
                    MaxPendingOperations = 32,
                    MaxPendingBytes = 32 * 1024,
                    MaxClockGap = 32,
                    MaxHistoricalSessions = 16,
                    MaxContentUtf16Length = 64,
                    MaxNestingDepth = 4,
                    MaxUpdateBytes = 64 * 1024,
                },
                codec);

            doc.Transact(tx => tx.GetOrCreateText("t").Insert(0, "s"));
            string before = doc.VisibleFingerprint();
            int pendingBefore = doc.PendingOperationCount;

            ApplyResult result;
            try
            {
                result = doc.ApplyUpdate(new byte[] { (byte)i, (byte)(i >> 8) });
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
                    report.Fail($"iter={i} rejected batch partially mutated document");
                }
            }
            else
            {
                try
                {
                    _ = doc.VisibleFingerprint();

                    // Twin empty replicas must converge on the same accepted batch.
                    if (result.Status == ApplyStatus.Integrated && codec.LastBatch is { Count: > 0 } batch)
                    {
                        using var twinA = new ConcordantDocument(new ConcordantDocumentOptions
                        {
                            WriterSession = SessionId.FromSeed((ulong)(seed + i + 9100)),
                            MaxOperations = 128,
                            MaxPendingOperations = 32,
                            MaxClockGap = 32,
                            MaxHistoricalSessions = 16,
                            MaxContentUtf16Length = 64,
                            MaxNestingDepth = 4,
                        });
                        using var twinB = new ConcordantDocument(new ConcordantDocumentOptions
                        {
                            WriterSession = SessionId.FromSeed((ulong)(seed + i + 9200)),
                            MaxOperations = 128,
                            MaxPendingOperations = 32,
                            MaxClockGap = 32,
                            MaxHistoricalSessions = 16,
                            MaxContentUtf16Length = 64,
                            MaxNestingDepth = 4,
                        });
                        ApplyResult a = twinA.Apply(new OperationBatch(batch));
                        ApplyResult b = twinB.Apply(new OperationBatch(batch));
                        if (a.Status == ApplyStatus.Integrated
                            && b.Status == ApplyStatus.Integrated
                            && twinA.VisibleFingerprint() != twinB.VisibleFingerprint())
                        {
                            report.Fail($"iter={i} twin divergence after integrated batch");
                        }
                    }
                }
                catch (Exception ex)
                {
                    report.Fail($"iter={i} post-apply threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return report;
    }

    private sealed class RandomBatchCodec : IUpdateCodec
    {
        private readonly Random _rng;

        public RandomBatchCodec(Random rng) => _rng = rng;

        public IReadOnlyList<ConcordantOperation>? LastBatch { get; private set; }

        public byte[] Encode(IReadOnlyList<ConcordantOperation> operations, UpdateEncodeKind kind) =>
            NativeUpdateCodec.Instance.Encode(operations, kind);

        public CodecDecodeResult Decode(ReadOnlySpan<byte> bytes, CodecDecodeLimits limits)
        {
            _ = bytes;
            List<ConcordantOperation> ops = GenerateBatch(_rng, limits);
            LastBatch = ops;
            return CodecDecodeResult.Ok(ops, UpdateEncodeKind.Update, 1, 0, 0);
        }

        private static List<ConcordantOperation> GenerateBatch(Random rng, CodecDecodeLimits limits)
        {
            int mode = rng.Next(7);
            SessionId session = SessionId.FromSeed((ulong)rng.Next(1, 50_000));
            var ops = new List<ConcordantOperation>();

            switch (mode)
            {
                case 0: // valid single root
                    ops.Add(new ConcordantOperation.RootDeclare("r", RootKind.Text)
                    {
                        Id = new OpId(session, 1),
                        Lamport = 1,
                    });
                    break;
                case 1: // fork pair
                    ops.Add(new ConcordantOperation.RootDeclare("a", RootKind.Text)
                    {
                        Id = new OpId(session, 1),
                        Lamport = 1,
                    });
                    ops.Add(new ConcordantOperation.RootDeclare("b", RootKind.Map)
                    {
                        Id = new OpId(session, 1),
                        Lamport = 1,
                    });
                    break;
                case 2: // sparse clock
                    ops.Add(new ConcordantOperation.RootDeclare("s", RootKind.Text)
                    {
                        Id = new OpId(session, (ulong)rng.Next(2, 20)),
                        Lamport = 1,
                    });
                    break;
                case 3: // extreme string
                    ops.Add(new ConcordantOperation.RootDeclare("m", RootKind.Map)
                    {
                        Id = new OpId(session, 1),
                        Lamport = 1,
                    });
                    ops.Add(new ConcordantOperation.MapSet(
                        ContainerRef.Root("m"),
                        "k",
                        ConcordantContent.Scalar(ConcordantScalar.String(new string('Z', (int)Math.Min(limits.MaxContentUtf16Length + 8, 10_000)))))
                    {
                        Id = new OpId(session, 2),
                        Lamport = 2,
                        LamportSource = new OpId(session, 1),
                    });
                    break;
                case 4: // duplicate identical
                    var op = new ConcordantOperation.RootDeclare("d", RootKind.Text)
                    {
                        Id = new OpId(session, 1),
                        Lamport = 1,
                    };
                    ops.Add(op);
                    ops.Add(op);
                    break;
                case 5: // non-contiguous batch
                    ops.Add(new ConcordantOperation.RootDeclare("a", RootKind.Text)
                    {
                        Id = new OpId(session, 1),
                        Lamport = 1,
                    });
                    ops.Add(new ConcordantOperation.RootDeclare("c", RootKind.Text)
                    {
                        Id = new OpId(session, 3),
                        Lamport = 2,
                        LamportSource = new OpId(session, 1),
                    });
                    break;
                default: // empty-ish: single delete of missing target
                    ops.Add(new ConcordantOperation.SeqDelete(new OpId(SessionId.FromSeed(1), 99))
                    {
                        Id = new OpId(session, 1),
                        Lamport = 1,
                    });
                    break;
            }

            return ops;
        }
    }
}
