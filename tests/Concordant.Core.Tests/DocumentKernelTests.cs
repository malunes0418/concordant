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
