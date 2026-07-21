using Concordant.History;
using Concordant.Internal.Sequences;
using Concordant.Shared;
using Concordant.Values;

namespace Concordant.Core.Tests;

public sealed class UndoManagerTests
{
    private static ConcordantDocument NewDoc(ulong seed) =>
        new(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(seed) });

    private static UndoManager NewUndo(ConcordantDocument doc, UndoManagerOptions? options = null) =>
        new(
            doc,
            options ?? new UndoManagerOptions
            {
                CaptureTimeoutMilliseconds = 0,
            });

    [Fact]
    public void Undo_local_insert_deletes_exact_ids()
    {
        using ConcordantDocument doc = NewDoc(1);
        using UndoManager undo = NewUndo(doc);
        SharedText text = doc.GetOrCreateText("t");

        doc.Transact(_ => text.Insert(0, "abc"));
        Assert.Equal("abc", text.ToString());
        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.Equal(string.Empty, text.ToString());
        Assert.Equal(UndoStatus.Applied, undo.Redo().Status);
        Assert.Equal("abc", text.ToString());
    }

    [Fact]
    public void Remote_updates_are_never_stacked()
    {
        using ConcordantDocument remote = NewDoc(2);
        _ = remote.GetOrCreateText("t");
        remote.Transact(tx => tx.Text("t").Insert(0, "R"));
        byte[] update = remote.EncodeUpdateSince(new Dictionary<SessionId, ulong>());

        using ConcordantDocument local = NewDoc(3);
        using UndoManager undo = NewUndo(local);
        _ = local.GetOrCreateText("t");
        int stackBefore = undo.UndoStackCount;

        Assert.Equal(ApplyStatus.Integrated, local.ApplyUpdate(update).Status);
        Assert.Equal("R", local.GetText("t").ToString());
        Assert.Equal(stackBefore, undo.UndoStackCount);
        Assert.Equal(UndoStatus.Empty, undo.Undo().Status);
    }

    [Fact]
    public void Local_and_remote_interleaving_undoes_only_local()
    {
        using ConcordantDocument a = NewDoc(10);
        using ConcordantDocument b = NewDoc(20);
        using UndoManager undo = NewUndo(a);

        SharedText textA = a.GetOrCreateText("t");
        SharedText textB = b.GetOrCreateText("t");
        Sync(a, b);

        a.Transact(_ => textA.Insert(0, "A"));
        b.Transact(_ => textB.Insert(0, "B"));
        Assert.Equal(ApplyStatus.Integrated, a.ApplyUpdate(b.EncodeUpdateSince(a.StateVector)).Status);
        a.Transact(_ => textA.Insert(textA.Length, "C"));

        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.DoesNotContain("C", textA.ToString(), StringComparison.Ordinal);
        Assert.Contains("B", textA.ToString(), StringComparison.Ordinal);

        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.Equal("B", textA.ToString());
        Assert.Equal(UndoStatus.Empty, undo.Undo().Status);
    }

    [Fact]
    public void Origin_filter_tracks_only_matching_origins()
    {
        using ConcordantDocument doc = NewDoc(1);
        using var undo = new UndoManager(doc, new UndoManagerOptions
        {
            CaptureTimeoutMilliseconds = 0,
            TrackedOrigins = new object?[] { "editor" },
        });

        SharedText text = doc.GetOrCreateText("t");
        doc.Transact(_ => text.Insert(0, "x"), origin: "other");
        doc.Transact(_ => text.Insert(1, "y"), origin: "editor");
        Assert.Equal("xy", text.ToString());

        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.Equal("x", text.ToString());
        Assert.Equal(UndoStatus.Empty, undo.Undo().Status);
    }

    [Fact]
    public void Grouping_merges_within_capture_timeout()
    {
        using ConcordantDocument doc = NewDoc(1);
        using var undo = new UndoManager(doc, new UndoManagerOptions
        {
            CaptureTimeoutMilliseconds = 60_000,
        });

        SharedText text = doc.GetOrCreateText("t");
        doc.Transact(_ => text.Insert(0, "a"));
        doc.Transact(_ => text.Insert(1, "b"));
        Assert.Equal(1, undo.UndoStackCount);

        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.Equal(string.Empty, text.ToString());
        Assert.Equal(UndoStatus.Empty, undo.Undo().Status);
    }

    [Fact]
    public void StopCapturing_starts_new_stack_item()
    {
        using ConcordantDocument doc = NewDoc(1);
        using var undo = new UndoManager(doc, new UndoManagerOptions
        {
            CaptureTimeoutMilliseconds = 60_000,
        });

        SharedText text = doc.GetOrCreateText("t");
        doc.Transact(_ => text.Insert(0, "a"));
        undo.StopCapturing();
        doc.Transact(_ => text.Insert(1, "b"));
        Assert.Equal(2, undo.UndoStackCount);

        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.Equal("a", text.ToString());
    }

    [Fact]
    public void Restore_delete_uses_pinned_anchors_with_fragmented_neighbors()
    {
        using ConcordantDocument a = NewDoc(1);
        using ConcordantDocument b = NewDoc(2);
        using UndoManager undo = NewUndo(a);

        SharedText textA = a.GetOrCreateText("t");
        SharedText textB = b.GetOrCreateText("t");
        Sync(a, b);

        a.Transact(_ => textA.Insert(0, "ac"));
        a.Transact(_ => textA.Insert(1, "b"));
        Assert.Equal("abc", textA.ToString());
        a.Transact(_ => textA.Delete(1, 1));
        Assert.Equal("ac", textA.ToString());

        Assert.Equal(ApplyStatus.Integrated, b.ApplyUpdate(a.EncodeUpdateSince(b.StateVector)).Status);
        b.Transact(_ => textB.Insert(1, "X"));
        Assert.Equal(ApplyStatus.Integrated, a.ApplyUpdate(b.EncodeUpdateSince(a.StateVector)).Status);

        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.Contains("b", textA.ToString(), StringComparison.Ordinal);
        Assert.Contains("X", textA.ToString(), StringComparison.Ordinal);
        Assert.Contains("a", textA.ToString(), StringComparison.Ordinal);
        Assert.Contains("c", textA.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Map_redo_after_remote_winner_returns_RemoteWinner()
    {
        using ConcordantDocument a = NewDoc(1);
        using ConcordantDocument b = NewDoc(2);
        using UndoManager undo = NewUndo(a);

        SharedMap mapA = a.GetOrCreateMap("m");
        SharedMap mapB = b.GetOrCreateMap("m");
        Sync(a, b);

        a.Transact(_ => mapA.Set("k", ConcordantScalar.Int64(1)));
        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);

        // Remote must observe local clocks so its assignment can win the register.
        Assert.Equal(ApplyStatus.Integrated, b.ApplyUpdate(a.EncodeUpdateSince(b.StateVector)).Status);
        b.Transact(_ => mapB.Set("k", ConcordantScalar.Int64(99)));
        Assert.Equal(ApplyStatus.Integrated, a.ApplyUpdate(b.EncodeUpdateSince(a.StateVector)).Status);

        Assert.Equal(UndoStatus.RemoteWinner, undo.Redo().Status);
        Assert.True(mapA.TryGetScalar("k", out ConcordantScalar? v));
        Assert.Equal(99, ((ConcordantScalar.Int64Scalar)v!).Value);
    }

    [Fact]
    public void Bounded_eviction_drops_oldest_and_reports_HistoryEvicted()
    {
        using ConcordantDocument doc = NewDoc(1);
        using var undo = new UndoManager(doc, new UndoManagerOptions
        {
            CaptureTimeoutMilliseconds = 0,
            MaxStackTransactions = 2,
        });

        SharedText text = doc.GetOrCreateText("t");
        doc.Transact(_ => text.Insert(0, "1"));
        doc.Transact(_ => text.Insert(text.Length, "2"));
        doc.Transact(_ => text.Insert(text.Length, "3"));
        Assert.Equal(2, undo.UndoStackCount);

        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.Equal(UndoStatus.HistoryEvicted, undo.Undo().Status);
        Assert.Equal(UndoStatus.Empty, undo.Undo().Status);
    }

    [Fact]
    public void Nested_delete_restore_deep_copies_new_identities()
    {
        using ConcordantDocument doc = NewDoc(1);
        using UndoManager undo = NewUndo(doc);

        SharedArray array = doc.GetOrCreateArray("a");
        OpId? originalNestedId = null;
        doc.Transact(_ =>
        {
            SharedMap child = array.InsertMap(0);
            originalNestedId = child.Container.NestedId;
            child.Set("n", ConcordantScalar.Int64(7));
            SharedText body = child.CreateText("body");
            body.Insert(0, "hi");
        });

        Assert.NotNull(originalNestedId);
        doc.Transact(_ => array.Delete(0));
        Assert.Equal(0, array.Count);

        Assert.Equal(UndoStatus.Applied, undo.Undo().Status);
        Assert.Equal(1, array.Count);
        Assert.True(array[0] is ConcordantContent.NestedContent { Kind: RootKind.Map });

        OpId? restoredId = null;
        foreach (ConcordantOperation op in doc.Store.Operations.Values)
        {
            if (op is ConcordantOperation.SeqInsert { Content: ConcordantContent.NestedContent { Kind: RootKind.Map } } ins
                && ins.Id != originalNestedId
                && doc.Store.TryGetSeqItem(ins.Id, out _, out SeqItem item)
                && !item.Deleted)
            {
                restoredId = ins.Id;
            }
        }

        Assert.NotNull(restoredId);
        Assert.NotEqual(originalNestedId, restoredId);
        Assert.True(doc.Store.TryGetMapWinner(
            ContainerRef.Nested(restoredId!.Value),
            "n",
            out _,
            out ConcordantContent nVal));
        Assert.Equal("i:7", nVal.CanonicalKey());
        Assert.Equal("hi", doc.Store.VisibleText(ContainerRef.Nested(FindNestedTextId(doc, restoredId.Value))));
    }

    [Fact]
    public void Undo_history_is_ephemeral_across_CreateFromCheckpoint()
    {
        using ConcordantDocument doc = NewDoc(1);
        using UndoManager undo = NewUndo(doc);
        SharedText text = doc.GetOrCreateText("t");
        doc.Transact(_ => text.Insert(0, "hello"));
        Assert.True(undo.CanUndo);

        byte[] checkpoint = doc.EncodeFullState();
        using ConcordantDocument restored = ConcordantDocument.CreateFromCheckpoint(
            checkpoint,
            new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(99) });
        using UndoManager undo2 = NewUndo(restored);

        Assert.Equal("hello", restored.GetText("t").ToString());
        Assert.False(undo2.CanUndo);
        Assert.Equal(UndoStatus.Empty, undo2.Undo().Status);
        Assert.NotEqual(doc.SessionId, restored.SessionId);
    }

    [Fact]
    public void Empty_undo_returns_Empty()
    {
        using ConcordantDocument doc = NewDoc(1);
        using UndoManager undo = NewUndo(doc);
        Assert.Equal(UndoStatus.Empty, undo.Undo().Status);
        Assert.Equal(UndoStatus.Empty, undo.Redo().Status);
    }

    [Fact]
    public void Failed_local_transaction_does_not_push_undo_stack()
    {
        using var limited = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(21),
            MaxContentUtf16Length = 3,
        });
        using UndoManager limitedUndo = NewUndo(limited);
        int stackBefore = limitedUndo.UndoStackCount;

        Assert.Throws<InvalidOperationException>(() =>
        {
            limited.Transact(tx =>
            {
                SharedMap map = tx.GetOrCreateMap("m");
                map.Set("a", ConcordantScalar.String("ok"));
                map.Set("b", ConcordantScalar.String("fail"));
            });
        });

        Assert.Equal(stackBefore, limitedUndo.UndoStackCount);
        Assert.Null(limited.TryGetRootKind("m"));
        Assert.False(limitedUndo.CanUndo);

        // Successful retry is undoable.
        limited.Transact(tx => tx.GetOrCreateText("t").Insert(0, "ok"));
        Assert.True(limitedUndo.CanUndo);
        Assert.Equal(UndoStatus.Applied, limitedUndo.Undo().Status);
        Assert.Equal(string.Empty, limited.GetText("t").ToString());
    }

    private static OpId FindNestedTextId(ConcordantDocument doc, OpId mapId)
    {
        foreach (KeyValuePair<string, ConcordantContent> kv in doc.Store.VisibleMap(ContainerRef.Nested(mapId)))
        {
            if (kv.Value is ConcordantContent.NestedContent { Kind: RootKind.Text }
                && doc.Store.TryGetMapWinner(ContainerRef.Nested(mapId), kv.Key, out OpId id, out _))
            {
                return id;
            }
        }

        throw new InvalidOperationException("Nested text not found.");
    }

    private static void Sync(ConcordantDocument a, ConcordantDocument b)
    {
        byte[] aUpdate = a.EncodeUpdateSince(b.StateVector);
        byte[] bUpdate = b.EncodeUpdateSince(a.StateVector);
        _ = a.ApplyUpdate(bUpdate);
        _ = b.ApplyUpdate(aUpdate);
    }
}
