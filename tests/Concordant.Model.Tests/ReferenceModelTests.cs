using System.Text.Json;
using Concordant.Model.Tests.ReferenceModel;
using Concordant.Model.Tests.Simulation;

namespace Concordant.Model.Tests;

public sealed class ReferenceModelTests
{
    [Fact]
    public void Concurrent_inserts_at_same_origin_order_by_opid()
    {
        var docA = new ReferenceDocument();
        var docB = new ReferenceDocument();
        var a = new LocalWriter(SessionId.FromSeed(1));
        var b = new LocalWriter(SessionId.FromSeed(2));

        RefBatch root = a.Transact(tx => tx.DeclareRoot("text", RootKind.Text));
        Assert.Equal(ApplyStatus.Integrated, docA.Apply(root).Status);
        Assert.Equal(ApplyStatus.Integrated, docB.Apply(root).Status);
        b.ObserveOp(root.Operations[0].Id, root.Operations[0].Lamport);

        RefBatch insertA = a.Transact(tx =>
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("A")));
        RefBatch insertB = b.Transact(tx =>
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("B")));

        Assert.Equal(ApplyStatus.Integrated, docA.Apply(insertA).Status);
        Assert.Equal(ApplyStatus.Integrated, docA.Apply(insertB).Status);
        Assert.Equal(ApplyStatus.Integrated, docB.Apply(insertB).Status);
        Assert.Equal(ApplyStatus.Integrated, docB.Apply(insertA).Status);

        Assert.Equal(docA.VisibleText(), docB.VisibleText());
        // Smaller OpId first: compare clocks then sessions. Both clock=2 after root on A only...
        // A root is clock 1 on session1; A insert clock 2; B insert clock 1 on session2.
        // B insert OpId (s2,1) vs A insert (s1,2): clock 1 < 2 so B then A => "BA"
        Assert.Equal("BA", docA.VisibleText());
    }

    [Fact]
    public void Map_last_writer_wins_by_lamport_then_opid()
    {
        var doc = new ReferenceDocument();
        var a = new LocalWriter(SessionId.FromSeed(10));
        var b = new LocalWriter(SessionId.FromSeed(20));

        RefBatch root = a.Transact(tx => tx.DeclareRoot("m", RootKind.Map));
        Assert.Equal(ApplyStatus.Integrated, doc.Apply(root).Status);
        b.ObserveOp(root.Operations[0].Id, root.Operations[0].Lamport);

        RefBatch setA = a.Transact(tx =>
            tx.MapSet("m", "k", RefScalar.StringScalar.Create("from-a")));
        // B observes A's set so its Lamport is causally later.
        b.ObserveOp(setA.Operations[0].Id, setA.Operations[0].Lamport);
        RefBatch setB = b.Transact(tx =>
            tx.MapSet("m", "k", RefScalar.StringScalar.Create("from-b")));

        Assert.Equal(ApplyStatus.Integrated, doc.Apply(setA).Status);
        Assert.Equal(ApplyStatus.Integrated, doc.Apply(setB).Status);

        Assert.Equal("from-b", ((RefScalar.StringScalar)doc.VisibleMap("m")["k"]).Value);
    }

    [Fact]
    public void Duplicate_delivery_is_idempotent()
    {
        var doc = new ReferenceDocument();
        var w = new LocalWriter(SessionId.FromSeed(3));
        RefBatch batch = w.Transact(tx =>
        {
            tx.DeclareRoot("text", RootKind.Text);
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("x"));
        });

        Assert.Equal(ApplyStatus.Integrated, doc.Apply(batch).Status);
        Assert.Equal(ApplyStatus.Duplicate, doc.Apply(batch).Status);
        Assert.Equal("x", doc.VisibleText());
    }

    [Fact]
    public void Delete_hides_item_but_origin_remains_addressable()
    {
        var doc = new ReferenceDocument();
        var w = new LocalWriter(SessionId.FromSeed(4));
        RefBatch insertBatch = w.Transact(tx =>
        {
            tx.DeclareRoot("text", RootKind.Text);
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("a"));
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("b"));
        });
        Assert.Equal(ApplyStatus.Integrated, doc.Apply(insertBatch).Status);
        Assert.Equal("ab", doc.VisibleText());

        OpId firstInsert = insertBatch.Operations.OfType<RefOperation.SeqInsert>().First().Id;
        RefBatch deleteBatch = w.Transact(tx => tx.Delete(firstInsert));
        Assert.Equal(ApplyStatus.Integrated, doc.Apply(deleteBatch).Status);
        Assert.Equal("b", doc.VisibleText());

        // Insert between deleted 'a' and 'b' using deleted origin as left.
        OpId secondInsert = insertBatch.Operations.OfType<RefOperation.SeqInsert>().Skip(1).First().Id;
        RefBatch mid = w.Transact(tx =>
            tx.Insert("text", firstInsert, secondInsert, RefScalar.StringScalar.Create("X")));
        Assert.Equal(ApplyStatus.Integrated, doc.Apply(mid).Status);
        Assert.Equal("Xb", doc.VisibleText());
    }

    [Fact]
    public void Root_kind_conflict_resolves_by_minimum_opid()
    {
        var doc = new ReferenceDocument();
        var a = new LocalWriter(SessionId.FromSeed(5));
        var b = new LocalWriter(SessionId.FromSeed(6));

        RefBatch mapRoot = a.Transact(tx => tx.DeclareRoot("content", RootKind.Map));
        RefBatch textRoot = b.Transact(tx => tx.DeclareRoot("content", RootKind.Text));

        Assert.Equal(ApplyStatus.Integrated, doc.Apply(mapRoot).Status);
        Assert.Equal(ApplyStatus.Integrated, doc.Apply(textRoot).Status);

        // Both clock 1: smaller session wins. Session 5 < 6 => Map wins.
        Assert.Equal(RootKind.Map, doc.TryGetRootKind("content"));
        Assert.True(doc.HasRootConflict("content"));
    }

    [Fact]
    public void Out_of_order_same_session_waits_then_integrates()
    {
        var doc = new ReferenceDocument();
        var w = new LocalWriter(SessionId.FromSeed(7));
        RefBatch first = w.Transact(tx => tx.DeclareRoot("text", RootKind.Text));
        RefBatch second = w.Transact(tx =>
            tx.Insert("text", null, null, RefScalar.StringScalar.Create("z")));

        Assert.Equal(ApplyStatus.PendingDependencies, doc.Apply(second).Status);
        Assert.Equal(ApplyStatus.Integrated, doc.Apply(first).Status);
        Assert.Equal("z", doc.VisibleText());
    }

    [Fact]
    public void Float_canonicalizes_negative_zero()
    {
        var a = RefScalar.Float64Scalar.Create(-0.0);
        var b = RefScalar.Float64Scalar.Create(0.0);
        Assert.Equal(a.CanonicalKey(), b.CanonicalKey());
    }
}

public sealed class SimulatorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(12345)]
    public void Random_workload_converges_under_shuffle_and_duplicates(int seed)
    {
        var sim = new ReplicaSimulator(replicaCount: 3, seed);
        sim.Commit(0, tx => tx.DeclareRoot("text", RootKind.Text));
        sim.Commit(0, tx => tx.DeclareRoot("m", RootKind.Map));

        // Share roots to other writers' observation by delivering early.
        sim.DeliverAll(duplicate: false);
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

        var rng = new Random(seed);
        var tips = new OpId?[sim.Replicas.Count];
        for (int step = 0; step < 40; step++)
        {
            int replica = rng.Next(sim.Replicas.Count);
            int action = rng.Next(4);
            switch (action)
            {
                case 0:
                    char ch = (char)('a' + rng.Next(26));
                    OpId? left = tips[replica];
                    sim.Commit(replica, tx =>
                        tx.Insert("text", left, null, RefScalar.StringScalar.Create(ch.ToString())));
                    tips[replica] = sim.AllBatches[^1].Operations.OfType<RefOperation.SeqInsert>().Last().Id;
                    break;
                case 1:
                    sim.Commit(replica, tx =>
                        tx.MapSet("m", "k", new RefScalar.Int64Scalar(rng.Next(100))));
                    break;
                case 2 when tips[replica] is OpId target:
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
    }

    [Fact]
    public void Fixture_seed_1_matches_golden_fingerprint()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "seed-1.json");
        Assert.True(File.Exists(fixturePath), $"Missing fixture at {fixturePath}");

        using FileStream stream = File.OpenRead(fixturePath);
        FixtureRecord? fixture = JsonSerializer.Deserialize<FixtureRecord>(stream);
        Assert.NotNull(fixture);

        var sim = new ReplicaSimulator(fixture.ReplicaCount, fixture.Seed);
        foreach (FixtureCommit commit in fixture.Commits)
        {
            sim.Commit(commit.Replica, tx =>
            {
                foreach (FixtureOp op in commit.Ops)
                {
                    switch (op.Kind)
                    {
                        case "RootDeclare":
                            tx.DeclareRoot(op.Name!, Enum.Parse<RootKind>(op.RootKind!));
                            break;
                        case "Insert":
                            tx.Insert(
                                op.Container!,
                                null,
                                null,
                                RefScalar.StringScalar.Create(op.Text!));
                            break;
                        case "MapSet":
                            tx.MapSet(op.Map!, op.Key!, RefScalar.StringScalar.Create(op.Text!));
                            break;
                        default:
                            throw new InvalidOperationException(op.Kind);
                    }
                }
            });

            // Make committed batches visible to other replicas before later commits.
            sim.DeliverAll(duplicate: false);
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

        sim.DeliverAll(duplicate: true);
        sim.AssertConverged();
        Assert.Equal(fixture.ExpectedFingerprint, sim.Fingerprint());
    }

    private sealed class FixtureRecord
    {
        public int Seed { get; set; }
        public int ReplicaCount { get; set; }
        public List<FixtureCommit> Commits { get; set; } = new();
        public string ExpectedFingerprint { get; set; } = "";
    }

    private sealed class FixtureCommit
    {
        public int Replica { get; set; }
        public List<FixtureOp> Ops { get; set; } = new();
    }

    private sealed class FixtureOp
    {
        public string Kind { get; set; } = "";
        public string? Name { get; set; }
        public string? RootKind { get; set; }
        public string? Container { get; set; }
        public string? Left { get; set; }
        public string? Right { get; set; }
        public string? Text { get; set; }
        public string? Map { get; set; }
        public string? Key { get; set; }
    }
}
