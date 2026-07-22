using Concordant.Shared;
using Concordant.Sync;
using Concordant.Sync.Native;
using Concordant.Values;

namespace Concordant.Core.Tests;

public sealed class AdversarialTests
{
    [Fact]
    public void Sparse_clocks_stay_pending_with_missing_ranges()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        SessionId remote = SessionId.FromSeed(9);
        var op5 = new ConcordantOperation.RootDeclare("t", RootKind.Text)
        {
            Id = new OpId(remote, 5),
            Lamport = 1,
        };

        ApplyResult result = doc.Apply(new OperationBatch(new[] { op5 }));
        Assert.Equal(ApplyStatus.PendingDependencies, result.Status);
        Assert.NotNull(result.MissingRanges);
        Assert.Contains(result.MissingRanges!, r =>
            r.Session == remote && r.FromClockInclusive == 1 && r.ToClockInclusive == 4);
        Assert.Equal(1, doc.PendingOperationCount);
    }

    [Fact]
    public void Clock_gap_quota_rejects_without_mutation()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxClockGap = 3,
        });
        string before = doc.VisibleFingerprint();
        SessionId remote = SessionId.FromSeed(2);
        var op = new ConcordantOperation.RootDeclare("t", RootKind.Text)
        {
            Id = new OpId(remote, 10),
            Lamport = 1,
        };

        ApplyResult result = doc.Apply(new OperationBatch(new[] { op }));
        Assert.Equal(ApplyStatus.Rejected, result.Status);
        Assert.Equal(ApplyRejectReason.QuotaExceeded, result.Reason);
        Assert.Equal(0, doc.PendingOperationCount);
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Dependency_bomb_hits_pending_quota_atomically()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxPendingOperations = 4,
        });

        SessionId remote = SessionId.FromSeed(3);
        var batch1 = new List<ConcordantOperation>();
        for (ulong clock = 2; clock <= 5; clock++)
        {
            batch1.Add(new ConcordantOperation.RootDeclare($"r{clock}", RootKind.Text)
            {
                Id = new OpId(remote, clock),
                Lamport = clock - 1,
                LamportSource = clock == 2 ? null : new OpId(remote, clock - 1),
            });
        }

        Assert.Equal(ApplyStatus.PendingDependencies, doc.Apply(new OperationBatch(batch1)).Status);
        Assert.Equal(4, doc.PendingOperationCount);

        var extra = new ConcordantOperation.RootDeclare("overflow", RootKind.Text)
        {
            Id = new OpId(remote, 6),
            Lamport = 5,
            LamportSource = new OpId(remote, 5),
        };
        string before = doc.VisibleFingerprint();
        ApplyResult rejected = doc.Apply(new OperationBatch(new[] { extra }));
        Assert.Equal(ApplyStatus.Rejected, rejected.Status);
        Assert.Equal(ApplyRejectReason.QuotaExceeded, rejected.Reason);
        Assert.Equal(4, doc.PendingOperationCount);
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Deferred_max_operations_rejects_gap_pending_instead_of_starving()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxOperations = 1,
            MaxClockGap = 100,
        });

        SessionId remote = SessionId.FromSeed(40);
        string before = doc.VisibleFingerprint();

        // clock 2 would require clock 1 as well → eventual 2 ops > MaxOperations.
        ApplyResult rejected = doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.RootDeclare("t", RootKind.Text)
            {
                Id = new OpId(remote, 2),
                Lamport = 1,
            },
        }));

        Assert.Equal(ApplyStatus.Rejected, rejected.Status);
        Assert.Equal(ApplyRejectReason.QuotaExceeded, rejected.Reason);
        Assert.Equal(0, doc.PendingOperationCount);
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Deferred_historical_sessions_cannot_stick_pending()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxHistoricalSessions = 1,
            MaxClockGap = 100,
        });

        SessionId first = SessionId.FromSeed(41);
        Assert.Equal(
            ApplyStatus.Integrated,
            doc.Apply(new OperationBatch(new ConcordantOperation[]
            {
                new ConcordantOperation.RootDeclare("a", RootKind.Text)
                {
                    Id = new OpId(first, 1),
                    Lamport = 1,
                },
            })).Status);

        string before = doc.VisibleFingerprint();
        SessionId second = SessionId.FromSeed(42);
        ApplyResult rejected = doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.RootDeclare("b", RootKind.Text)
            {
                Id = new OpId(second, 2),
                Lamport = 1,
            },
        }));

        Assert.Equal(ApplyStatus.Rejected, rejected.Status);
        Assert.Equal(ApplyRejectReason.QuotaExceeded, rejected.Reason);
        Assert.Equal(0, doc.PendingOperationCount);
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Session_churn_exhausts_historical_session_cap()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxHistoricalSessions = 2,
        });

        for (ulong seed = 10; seed <= 11; seed++)
        {
            SessionId s = SessionId.FromSeed(seed);
            ApplyResult ok = doc.Apply(new OperationBatch(new ConcordantOperation[]
            {
                new ConcordantOperation.RootDeclare($"t{seed}", RootKind.Text)
                {
                    Id = new OpId(s, 1),
                    Lamport = 1,
                },
            }));
            Assert.Equal(ApplyStatus.Integrated, ok.Status);
        }

        string before = doc.VisibleFingerprint();
        SessionId third = SessionId.FromSeed(12);
        ApplyResult rejected = doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.RootDeclare("t12", RootKind.Text)
            {
                Id = new OpId(third, 1),
                Lamport = 1,
            },
        }));
        Assert.Equal(ApplyStatus.Rejected, rejected.Status);
        Assert.Equal(ApplyRejectReason.QuotaExceeded, rejected.Reason);
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Deep_nesting_rejects_beyond_cap()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxNestingDepth = 1,
        });

        SharedMap root = doc.GetOrCreateMap("root");
        SharedMap? level1 = null;
        doc.Transact(_ => level1 = root.CreateMap("child"));

        string before = doc.VisibleFingerprint();
        Assert.Throws<InvalidOperationException>(() =>
        {
            doc.Transact(_ => level1!.CreateMap("too-deep"));
        });
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Extreme_content_length_is_quota_rejected()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxContentUtf16Length = 8,
        });
        SharedMap map = doc.GetOrCreateMap("m");
        string before = doc.VisibleFingerprint();

        Assert.Throws<InvalidOperationException>(() =>
        {
            doc.Transact(_ => map.Set("k", ConcordantScalar.String(new string('a', 32))));
        });
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Lamport_chain_mismatch_stays_pending_not_integrated()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        SessionId remote = SessionId.FromSeed(4);

        // Correct declare.
        _ = doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.RootDeclare("t", RootKind.Text)
            {
                Id = new OpId(remote, 1),
                Lamport = 1,
            },
        }));

        // Wrong Lamport for clock 2.
        ApplyResult result = doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.SeqInsert(
                ContainerRef.Root("t"),
                null,
                null,
                ConcordantContent.Scalar(ConcordantScalar.String("x")))
            {
                Id = new OpId(remote, 2),
                Lamport = 99,
                LamportSource = new OpId(remote, 1),
            },
        }));

        Assert.Equal(ApplyStatus.PendingDependencies, result.Status);
        Assert.Equal(string.Empty, doc.GetText("t").ToString());
    }

    [Fact]
    public void Forked_ids_reject_without_poisoning()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        OperationBatch batch = doc.Transact(tx =>
        {
            tx.GetOrCreateMap("m").Set("k", ConcordantScalar.Int64(1));
        })!;

        ConcordantOperation original = batch.Operations[^1];
        var forked = new ConcordantOperation.MapSet(ContainerRef.Root("m"), "k", ConcordantContent.Scalar(ConcordantScalar.Int64(2)))
        {
            Id = original.Id,
            Lamport = original.Lamport,
            LamportSource = original.LamportSource,
        };

        string before = doc.VisibleFingerprint();
        ApplyResult result = doc.Apply(new OperationBatch(new[] { forked }));
        Assert.Equal(ApplyStatus.Rejected, result.Status);
        Assert.Equal(ApplyRejectReason.ReplicaFork, result.Reason);
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Observer_failures_do_not_abort_integration()
    {
        int throws = 0;
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            WarningHandler = _ =>
            {
                throws++;
                throw new InvalidOperationException("observer boom");
            },
        });

        SessionId a = SessionId.FromSeed(20);
        SessionId b = SessionId.FromSeed(21);
        Assert.Equal(ApplyStatus.Integrated, doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.RootDeclare("x", RootKind.Map) { Id = new OpId(a, 1), Lamport = 1 },
        })).Status);
        Assert.Equal(ApplyStatus.Integrated, doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.RootDeclare("x", RootKind.Text) { Id = new OpId(b, 1), Lamport = 1 },
        })).Status);

        Assert.Equal(1, throws);
        Assert.True(doc.HasRootConflict("x"));
        Assert.Equal(RootKind.Map, doc.TryGetRootKind("x")); // min OpId wins depending on seeds
    }

    [Fact]
    public void Cap_exhaustion_max_operations_rejects_atomically()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxOperations = 2,
        });

        doc.Transact(tx =>
        {
            tx.GetOrCreateText("t");
            tx.GetOrCreateMap("m");
        });

        string before = doc.VisibleFingerprint();
        SessionId remote = SessionId.FromSeed(50);
        ApplyResult rejected = doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.RootDeclare("u", RootKind.Text)
            {
                Id = new OpId(remote, 1),
                Lamport = 1,
            },
        }));
        Assert.Equal(ApplyStatus.Rejected, rejected.Status);
        Assert.Equal(ApplyRejectReason.QuotaExceeded, rejected.Reason);
        Assert.Equal(before, doc.VisibleFingerprint());
        Assert.Null(doc.TryGetRootKind("u"));
    }

    [Fact]
    public void Max_update_bytes_cannot_be_bypassed()
    {
        using var src = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        src.Transact(tx => tx.GetOrCreateText("t").Insert(0, "hello"));
        byte[] bytes = src.EncodeFullState();

        using var dst = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(2),
            MaxUpdateBytes = bytes.Length - 1,
        });
        string before = dst.VisibleFingerprint();
        ApplyResult result = dst.ApplyUpdate(bytes);
        Assert.Equal(ApplyStatus.Rejected, result.Status);
        Assert.Equal(ApplyRejectReason.QuotaExceeded, result.Reason);
        Assert.Equal(before, dst.VisibleFingerprint());
    }

    [Fact]
    public void Custom_codec_batches_are_still_revalidated()
    {
        var evil = new EvilCodec();
        using var doc = new ConcordantDocument(
            new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) },
            evil);

        string before = doc.VisibleFingerprint();
        ApplyResult result = doc.ApplyUpdate(new byte[] { 1, 2, 3 });
        Assert.Equal(ApplyStatus.Rejected, result.Status);
        Assert.Equal(ApplyRejectReason.ReplicaFork, result.Reason);
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    private sealed class EvilCodec : IUpdateCodec
    {
        public byte[] Encode(IReadOnlyList<ConcordantOperation> operations, UpdateEncodeKind kind) =>
            NativeUpdateCodec.Instance.Encode(operations, kind);

        public CodecDecodeResult Decode(ReadOnlySpan<byte> bytes, CodecDecodeLimits limits)
        {
            SessionId s = SessionId.FromSeed(99);
            // Conflicting fork pair with same OpId — core must reject.
            var a = new ConcordantOperation.RootDeclare("a", RootKind.Text)
            {
                Id = new OpId(s, 1),
                Lamport = 1,
            };
            var b = new ConcordantOperation.RootDeclare("b", RootKind.Map)
            {
                Id = new OpId(s, 1),
                Lamport = 1,
            };
            return CodecDecodeResult.Ok(new ConcordantOperation[] { a, b }, UpdateEncodeKind.Update, 1, 0, 0);
        }
    }
}
