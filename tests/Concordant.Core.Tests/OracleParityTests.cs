using Concordant.Model.Tests.ReferenceModel;
using Concordant.Model.Tests.Simulation;
using Concordant.Values;
using RefOpId = Concordant.Model.Tests.ReferenceModel.OpId;
using RefRootKind = Concordant.Model.Tests.ReferenceModel.RootKind;
using RefSessionId = Concordant.Model.Tests.ReferenceModel.SessionId;

namespace Concordant.Core.Tests;

public sealed class OracleParityTests
{
    [Fact]
    public void Concurrent_inserts_match_reference_fingerprint()
    {
        var refDoc = new ReferenceDocument();
        var prod = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        var a = new LocalWriter(RefSessionId.FromSeed(1));
        var b = new LocalWriter(RefSessionId.FromSeed(2));

        RefBatch root = a.Transact(tx => tx.DeclareRoot("text", RefRootKind.Text));
        ApplyBoth(refDoc, prod, root);
        b.ObserveOp(root.Operations[0].Id, root.Operations[0].Lamport);

        RefBatch insertA = a.Transact(tx =>
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("A")));
        RefBatch insertB = b.Transact(tx =>
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("B")));

        ApplyBoth(refDoc, prod, insertA);
        ApplyBoth(refDoc, prod, insertB);

        Assert.Equal(refDoc.VisibleFingerprint(), prod.VisibleFingerprint());
        Assert.Equal("BA", prod.GetText("text").ToString());
    }

    [Fact]
    public void Map_lww_matches_reference()
    {
        var refDoc = new ReferenceDocument();
        var prod = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(10) });
        var a = new LocalWriter(RefSessionId.FromSeed(10));
        var b = new LocalWriter(RefSessionId.FromSeed(20));

        RefBatch root = a.Transact(tx => tx.DeclareRoot("m", RefRootKind.Map));
        ApplyBoth(refDoc, prod, root);
        b.ObserveOp(root.Operations[0].Id, root.Operations[0].Lamport);

        RefBatch setA = a.Transact(tx =>
            tx.MapSet("m", "k", RefScalar.StringScalar.Create("from-a")));
        b.ObserveOp(setA.Operations[0].Id, setA.Operations[0].Lamport);
        RefBatch setB = b.Transact(tx =>
            tx.MapSet("m", "k", RefScalar.StringScalar.Create("from-b")));

        ApplyBoth(refDoc, prod, setA);
        ApplyBoth(refDoc, prod, setB);

        Assert.Equal(refDoc.VisibleFingerprint(), prod.VisibleFingerprint());
        Assert.True(prod.GetMap("m").TryGetScalar("k", out ConcordantScalar? value));
        Assert.Equal("from-b", ((ConcordantScalar.StringScalar)value!).Value);
    }

    [Fact]
    public void Duplicate_delivery_and_delete_origin_match_reference()
    {
        var refDoc = new ReferenceDocument();
        var prod = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(4) });
        var w = new LocalWriter(RefSessionId.FromSeed(4));

        RefBatch insertBatch = w.Transact(tx =>
        {
            tx.DeclareRoot("text", RefRootKind.Text);
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("a"));
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("b"));
        });
        ApplyBoth(refDoc, prod, insertBatch);
        Assert.Equal(ApplyStatus.Duplicate, prod.Apply(OracleBridge.ToCore(insertBatch)).Status);

        RefOpId firstInsert = insertBatch.Operations.OfType<RefOperation.SeqInsert>().First().Id;
        RefBatch deleteBatch = w.Transact(tx => tx.Delete(firstInsert));
        ApplyBoth(refDoc, prod, deleteBatch);

        RefOpId secondInsert = insertBatch.Operations.OfType<RefOperation.SeqInsert>().Skip(1).First().Id;
        RefBatch mid = w.Transact(tx =>
            tx.Insert("text", firstInsert, secondInsert, RefScalar.StringScalar.Create("X")));
        ApplyBoth(refDoc, prod, mid);

        Assert.Equal(refDoc.VisibleFingerprint(), prod.VisibleFingerprint());
        Assert.Equal("Xb", prod.GetText("text").ToString());
    }

    [Fact]
    public void Root_conflict_and_out_of_order_match_reference()
    {
        var refDoc = new ReferenceDocument();
        var prod = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(5) });
        var a = new LocalWriter(RefSessionId.FromSeed(5));
        var b = new LocalWriter(RefSessionId.FromSeed(6));

        RefBatch mapRoot = a.Transact(tx => tx.DeclareRoot("content", RefRootKind.Map));
        RefBatch textRoot = b.Transact(tx => tx.DeclareRoot("content", RefRootKind.Text));
        ApplyBoth(refDoc, prod, mapRoot);
        ApplyBoth(refDoc, prod, textRoot);
        Assert.Equal(refDoc.VisibleFingerprint(), prod.VisibleFingerprint());
        Assert.True(prod.HasRootConflict("content"));
        Assert.Equal(RootKind.Map, prod.TryGetRootKind("content"));

        var refDoc2 = new ReferenceDocument();
        var prod2 = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(7) });
        var w = new LocalWriter(RefSessionId.FromSeed(7));
        RefBatch first = w.Transact(tx => tx.DeclareRoot("text", RefRootKind.Text));
        RefBatch second = w.Transact(tx =>
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("z")));

        Assert.Equal(ApplyStatus.PendingDependencies, OracleBridge.ToCore(refDoc2.Apply(second).Status));
        Assert.Equal(ApplyStatus.PendingDependencies, prod2.Apply(OracleBridge.ToCore(second)).Status);
        ApplyBoth(refDoc2, prod2, first);
        Assert.Equal(refDoc2.VisibleFingerprint(), prod2.VisibleFingerprint());
        Assert.Equal("z", prod2.GetText("text").ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(12345)]
    public void Simulator_seeds_match_reference_fingerprints(int seed)
    {
        var sim = new ReplicaSimulator(replicaCount: 3, seed);
        var prodReplicas = sim.Replicas
            .Select(r => new ConcordantDocument(new ConcordantDocumentOptions
            {
                WriterSession = OracleBridge.ToCore(r.Session),
            }))
            .ToArray();

        sim.Commit(0, tx => tx.DeclareRoot("text", RefRootKind.Text));
        sim.Commit(0, tx => tx.DeclareRoot("m", RefRootKind.Map));
        sim.DeliverAll(duplicate: false);
        SyncObservations(sim);

        var rng = new Random(seed);
        var tips = new RefOpId?[sim.Replicas.Count];
        for (int step = 0; step < 40; step++)
        {
            int replica = rng.Next(sim.Replicas.Count);
            int action = rng.Next(4);
            switch (action)
            {
                case 0:
                    char ch = (char)('a' + rng.Next(26));
                    RefOpId? left = tips[replica];
                    sim.Commit(replica, tx =>
                        tx.Insert("text", left, null, RefScalar.StringScalar.Create(ch.ToString())));
                    tips[replica] = sim.AllBatches[^1].Operations.OfType<RefOperation.SeqInsert>().Last().Id;
                    break;
                case 1:
                    sim.Commit(replica, tx =>
                        tx.MapSet("m", "k", new RefScalar.Int64Scalar(rng.Next(100))));
                    break;
                case 2 when tips[replica] is RefOpId target:
                    sim.Commit(replica, tx => tx.Delete(target));
                    tips[replica] = null;
                    break;
                default:
                    sim.Commit(replica, tx =>
                        tx.MapSet("m", "note", RefScalar.StringScalar.Create($"s{step}")));
                    break;
            }
        }

        sim.DeliverAll(duplicate: true);
        sim.AssertConverged();

        for (int r = 0; r < prodReplicas.Length; r++)
        {
            foreach (RefBatch batch in sim.AllBatches)
            {
                _ = prodReplicas[r].Apply(OracleBridge.ToCore(batch));
            }

            // Drain pending like the simulator.
            for (int pass = 0; pass < sim.AllBatches.Count + 2; pass++)
            {
                foreach (RefBatch batch in sim.AllBatches)
                {
                    _ = prodReplicas[r].Apply(OracleBridge.ToCore(batch));
                }
            }
        }

        string expected = sim.Fingerprint();
        foreach (ConcordantDocument prod in prodReplicas)
        {
            Assert.Equal(expected, prod.VisibleFingerprint());
        }
    }

    private static void SyncObservations(ReplicaSimulator sim)
    {
        foreach (ReplicaSimulator.Replica replica in sim.Replicas)
        {
            foreach (RefBatch batch in sim.AllBatches)
            {
                foreach (RefOperation op in batch.Operations)
                {
                    replica.Writer.ObserveOp(op.Id, op.Lamport);
                }
            }
        }
    }

    private static void ApplyBoth(ReferenceDocument refDoc, ConcordantDocument prod, RefBatch batch)
    {
        Model.Tests.ReferenceModel.ApplyResult refResult = refDoc.Apply(batch);
        ApplyResult prodResult = prod.Apply(OracleBridge.ToCore(batch));
        Assert.Equal(OracleBridge.ToCore(refResult.Status), prodResult.Status);
    }
}
