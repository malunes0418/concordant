using Concordant.Internal.Normalization;
using Concordant.Internal.Sequences;
using Concordant.Shared;
using Concordant.Values;

namespace Concordant.Core.Tests;

public sealed class NormalizationTests
{
    [Fact]
    public void Batch_coalesces_identical_duplicate_op_ids()
    {
        SessionId s = SessionId.FromSeed(1);
        var op = new ConcordantOperation.RootDeclare("t", RootKind.Text)
        {
            Id = new OpId(s, 1),
            Lamport = 1,
        };

        NormalizeBatchResult result = AlwaysSafeNormalizer.NormalizeBatch(new[] { op, op });
        Assert.Equal(NormalizeBatchStatus.Normalized, result.Status);
        Assert.Equal(1, result.CoalescedDuplicates);
        Assert.Single(result.Operations);
    }

    [Fact]
    public void Batch_fork_on_conflicting_duplicate_op_ids()
    {
        SessionId s = SessionId.FromSeed(1);
        var a = new ConcordantOperation.RootDeclare("t", RootKind.Text)
        {
            Id = new OpId(s, 1),
            Lamport = 1,
        };
        var b = new ConcordantOperation.RootDeclare("u", RootKind.Text)
        {
            Id = new OpId(s, 1),
            Lamport = 1,
        };

        NormalizeBatchResult result = AlwaysSafeNormalizer.NormalizeBatch(new[] { a, b });
        Assert.Equal(NormalizeBatchStatus.ReplicaFork, result.Status);
        Assert.Equal(a.Id, result.ForkId);
    }

    [Fact]
    public void Apply_coalesces_duplicate_ops_in_batch_without_reject()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        SessionId remote = SessionId.FromSeed(2);
        var op = new ConcordantOperation.RootDeclare("t", RootKind.Text)
        {
            Id = new OpId(remote, 1),
            Lamport = 1,
        };

        ApplyResult result = doc.Apply(new OperationBatch(new[] { op, op }));
        Assert.Equal(ApplyStatus.Integrated, result.Status);
        Assert.Equal(RootKind.Text, doc.TryGetRootKind("t"));
    }

    [Fact]
    public void Normalize_does_not_remove_tombstones_or_history()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        SharedText text = doc.GetOrCreateText("t");
        doc.Transact(_ =>
        {
            text.Insert(0, "x");
            text.Delete(0, 1);
        });

        ConcordantOperation.SeqInsert inserted = doc.Store.GetIntegratedOperations()
            .OfType<ConcordantOperation.SeqInsert>()
            .Single();

        string before = doc.VisibleFingerprint();
        int removed = doc.Normalize();
        Assert.Equal(0, removed);
        Assert.Equal(before, doc.VisibleFingerprint());
        Assert.True(doc.Store.TryGetSeqItem(inserted.Id, out _, out SeqItem item));
        Assert.True(item.Deleted);
    }
}
