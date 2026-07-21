using Concordant.Internal.Sequences;
using Concordant.Shared;
using Concordant.Values;

namespace Concordant.Core.Tests;

public sealed class DocumentKernelTests
{
    [Fact]
    public void Session_is_csprng_by_default_and_overrideable()
    {
        using var a = new ConcordantDocument();
        using var b = new ConcordantDocument();
        Assert.NotEqual(a.SessionId, b.SessionId);

        SessionId fixedSession = SessionId.FromSeed(99);
        using var c = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = fixedSession });
        Assert.Equal(fixedSession, c.SessionId);
    }

    [Fact]
    public void Reentrant_transact_is_rejected()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        Assert.Throws<InvalidOperationException>(() =>
        {
            doc.Transact(tx =>
            {
                tx.GetOrCreateText("t");
                doc.Transact(_ => { });
            });
        });
    }

    [Fact]
    public void Replica_fork_rejects_without_poisoning_document()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        OperationBatch batch = doc.Transact(tx =>
        {
            SharedText text = tx.GetOrCreateText("t");
            text.Insert(0, "a");
        })!;

        ConcordantOperation original = batch.Operations[^1];
        var forked = new ConcordantOperation.SeqInsert(
            ContainerRef.Root("t"),
            null,
            null,
            ConcordantContent.Scalar(ConcordantScalar.String("Z")))
        {
            Id = original.Id,
            Lamport = original.Lamport,
            LamportSource = original.LamportSource,
        };

        ApplyResult result = doc.Apply(new OperationBatch(new ConcordantOperation[] { forked }));
        Assert.Equal(ApplyStatus.Rejected, result.Status);
        Assert.Equal(ApplyRejectReason.ReplicaFork, result.Reason);
        Assert.Equal("a", doc.GetText("t").ToString());
    }

    [Fact]
    public void Pending_quota_rejects_atomically()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxPendingOperations = 1,
        });

        // Remote session ops with clock hole: clock 2 then would need clock 1.
        SessionId remote = SessionId.FromSeed(2);
        var op2 = new ConcordantOperation.RootDeclare("t", RootKind.Text)
        {
            Id = new OpId(remote, 2),
            Lamport = 1,
            LamportSource = null,
        };
        var op3 = new ConcordantOperation.RootDeclare("u", RootKind.Text)
        {
            Id = new OpId(remote, 3),
            Lamport = 2,
            LamportSource = new OpId(remote, 2),
        };

        // First pending op accepted.
        Assert.Equal(ApplyStatus.PendingDependencies, doc.Apply(new OperationBatch(new[] { op2 })).Status);

        // Second would exceed pending quota — reject without adding.
        ApplyResult rejected = doc.Apply(new OperationBatch(new[] { op3 }));
        Assert.Equal(ApplyStatus.Rejected, rejected.Status);
        Assert.Equal(ApplyRejectReason.QuotaExceeded, rejected.Reason);
        Assert.Equal(1, doc.PendingOperationCount);
    }

    [Fact]
    public void Observer_exceptions_are_isolated()
    {
        int calls = 0;
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            WarningHandler = _ =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            },
        });

        SessionId a = SessionId.FromSeed(5);
        SessionId b = SessionId.FromSeed(6);
        _ = doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.RootDeclare("content", RootKind.Map)
            {
                Id = new OpId(a, 1),
                Lamport = 1,
            },
        }));
        _ = doc.Apply(new OperationBatch(new ConcordantOperation[]
        {
            new ConcordantOperation.RootDeclare("content", RootKind.Text)
            {
                Id = new OpId(b, 1),
                Lamport = 1,
            },
        }));

        Assert.Equal(1, calls);
        Assert.True(doc.HasRootConflict("content"));
    }

    [Fact]
    public void Failed_multi_op_transaction_rolls_back_all_state()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(1),
            MaxContentUtf16Length = 4,
        });

        SharedMap map = doc.GetOrCreateMap("m");
        string before = doc.VisibleFingerprint();
        IReadOnlyDictionary<SessionId, ulong> beforeSv = doc.StateVector.ToDictionary(static kv => kv.Key, static kv => kv.Value);
        int beforePending = doc.PendingOperationCount;

        Assert.Throws<InvalidOperationException>(() =>
        {
            doc.Transact(tx =>
            {
                tx.Map("m").Set("a", ConcordantScalar.String("ok"));
                tx.Map("m").Set("b", ConcordantScalar.String("also"));
                // Third assignment exceeds MaxContentUtf16Length and must unwind a+b.
                tx.Map("m").Set("c", ConcordantScalar.String("too-long"));
            });
        });

        Assert.Equal(before, doc.VisibleFingerprint());
        Assert.Equal(beforePending, doc.PendingOperationCount);
        Assert.Equal(beforeSv, doc.StateVector);
        Assert.False(map.TryGet("a", out _));
        Assert.False(map.TryGet("b", out _));

        // Retry after failure must succeed.
        doc.Transact(tx => tx.Map("m").Set("a", ConcordantScalar.String("ok")));
        Assert.True(map.TryGetScalar("a", out ConcordantScalar? value));
        Assert.Equal("ok", ((ConcordantScalar.StringScalar)value!).Value);
    }

    [Fact]
    public void Failed_transaction_does_not_notify_undo_observers()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(2),
            MaxContentUtf16Length = 4,
        });

        int notifications = 0;
        doc.AddTransactionObserver((_, _) => notifications++);

        _ = doc.GetOrCreateMap("m");
        Assert.Equal(1, notifications);

        Assert.Throws<InvalidOperationException>(() =>
        {
            doc.Transact(tx =>
            {
                tx.Map("m").Set("a", ConcordantScalar.String("ok"));
                tx.Map("m").Set("b", ConcordantScalar.String("too-long"));
            });
        });

        Assert.Equal(1, notifications);
        Assert.False(doc.GetMap("m").TryGet("a", out _));
    }

    [Fact]
    public void Concurrent_enter_fails_predictably()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(3) });
        var ready = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        Exception? backgroundError = null;

        Thread background = new(() =>
        {
            try
            {
                ready.Wait();
                entered.Set();
                _ = doc.EncodeStateVector();
            }
            catch (Exception ex)
            {
                backgroundError = ex;
            }
        });
        background.Start();

        doc.Transact(_ =>
        {
            ready.Set();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Thread.Sleep(50);
        });

        background.Join(TimeSpan.FromSeconds(2));
        Assert.IsType<InvalidOperationException>(backgroundError);
        Assert.Contains("caller-serialized", backgroundError!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_state_vector_round_trips_and_rejects_malformed()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(4) });
        doc.Transact(tx =>
        {
            tx.GetOrCreateText("t").Insert(0, "x");
            tx.GetOrCreateMap("m").Set("k", ConcordantScalar.String("v"));
        });

        byte[] encoded = doc.EncodeStateVector();
        Assert.True(ConcordantDocument.TryDecodeStateVector(encoded, out IReadOnlyDictionary<SessionId, ulong>? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(doc.StateVector.Count, decoded!.Count);
        foreach (KeyValuePair<SessionId, ulong> kv in doc.StateVector)
        {
            Assert.Equal(kv.Value, decoded[kv.Key]);
        }

        // Deterministic regardless of dictionary insertion order.
        var shuffled = doc.StateVector.Reverse().ToDictionary(static kv => kv.Key, static kv => kv.Value);
        Assert.Equal(encoded, ConcordantDocument.EncodeStateVector(shuffled));

        Assert.False(ConcordantDocument.TryDecodeStateVector(encoded.AsSpan(0, Math.Max(0, encoded.Length - 1)), out _));
        Assert.False(ConcordantDocument.TryDecodeStateVector(new byte[] { 1, 0, 0, 0 }, out _));
    }

    [Fact]
    public void Sequence_ownership_index_resolves_nested_containers()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(5) });
        OpId nestedTextId = default;
        OpId nestedItemId = default;

        doc.Transact(tx =>
        {
            SharedMap root = tx.GetOrCreateMap("root");
            SharedText nested = root.CreateText("notes");
            nestedTextId = nested.Container.NestedId!.Value;
            nested.Insert(0, "ab");
            nestedItemId = doc.Store.TryGetSequence(nested.Container)!.GetVisibleUtf16Items()[0].Id;
        });

        Assert.True(doc.Store.TryGetSeqItem(nestedItemId, out ContainerRef container, out SeqItem item));
        Assert.Equal(ContainerRef.Nested(nestedTextId), container);
        Assert.False(item.Deleted);
        Assert.Equal("a", ((ConcordantContent.ScalarContent)item.Content).Value is ConcordantScalar.StringScalar s ? s.Value : null);
    }
}

public sealed class SharedTypesTests
{
    [Fact]
    public void SharedText_insert_delete_uses_utf16_offsets()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        SharedText text = doc.GetOrCreateText("t");
        doc.Transact(_ =>
        {
            text.Insert(0, "a😀b");
            Assert.Equal(4, text.Length);
            text.Delete(1, 2); // delete emoji (one scalar, 2 UTF-16 units)
            Assert.Equal("ab", text.ToString());
        });
    }

    [Fact]
    public void SharedText_rejects_surrogate_splitting_offset()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        SharedText text = doc.GetOrCreateText("t");
        doc.Transact(_ => text.Insert(0, "😀"));
        Assert.Throws<ArgumentException>(() => doc.Transact(_ => text.Insert(1, "x")));
    }

    [Fact]
    public void SharedMap_nested_create_is_attached_only()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        SharedMap root = doc.GetOrCreateMap("m");
        SharedText? nested = null;
        doc.Transact(_ =>
        {
            nested = root.CreateText("body");
            nested.Insert(0, "hi");
            root.Set("n", ConcordantScalar.Int64(7));
        });

        Assert.True(root.TryGetText("body", out SharedText? got));
        Assert.Equal("hi", got!.ToString());
        Assert.True(root.TryGetScalar("n", out ConcordantScalar? n));
        Assert.Equal(7, ((ConcordantScalar.Int64Scalar)n!).Value);
        Assert.NotNull(nested);
        Assert.True(nested!.Container.IsNested);
    }

    [Fact]
    public void SharedArray_supports_scalars_and_nested()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        SharedArray array = doc.GetOrCreateArray("a");
        doc.Transact(_ =>
        {
            array.Insert(0, ConcordantScalar.String("x"));
            SharedMap child = array.InsertMap(1);
            child.Set("k", ConcordantScalar.Bool(true));
            array.Add(ConcordantScalar.Int64(3));
        });

        Assert.Equal(3, array.Count);
        Assert.Equal("s:x", ((ConcordantContent.ScalarContent)array[0]).Value.CanonicalKey());
        Assert.True(array[1] is ConcordantContent.NestedContent { Kind: RootKind.Map });
        Assert.Equal("i:3", ((ConcordantContent.ScalarContent)array[2]).Value.CanonicalKey());
    }

    [Fact]
    public void Wrong_kind_root_access_throws()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        _ = doc.GetOrCreateMap("x");
        Assert.Throws<InvalidOperationException>(() => doc.GetText("x"));
    }

    [Fact]
    public void Scalar_float_canonicalizes_negative_zero()
    {
        Assert.Equal(
            ConcordantScalar.Float64(0.0).CanonicalKey(),
            ConcordantScalar.Float64(-0.0).CanonicalKey());
    }
}
