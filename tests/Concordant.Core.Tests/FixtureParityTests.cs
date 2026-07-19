using Concordant.Model.Tests.ReferenceModel;
using Concordant.Model.Tests.Simulation;
using RefRootKind = Concordant.Model.Tests.ReferenceModel.RootKind;

namespace Concordant.Core.Tests;

public sealed class FixtureParityTests
{
    // Golden fingerprint from tests/Concordant.Model.Tests/Fixtures/seed-1.json
    private const string ExpectedFingerprint =
        "text=iH|roots=m:Map.;text:Text.;|maps=m{title=s:world;}|ops=4522700C3989AA32AEDFD88034D00BD3@1;A6EAF652BA3F2E1DEF87911317EB261A@1;4522700C3989AA32AEDFD88034D00BD3@2;A6EAF652BA3F2E1DEF87911317EB261A@2;A6EAF652BA3F2E1DEF87911317EB261A@3;A6EAF652BA3F2E1DEF87911317EB261A@4;";

    [Fact]
    public void Fixture_seed_1_matches_golden_fingerprint_on_production_store()
    {
        var sim = new ReplicaSimulator(replicaCount: 2, seed: 1);
        var prodReplicas = sim.Replicas
            .Select(r => new ConcordantDocument(new ConcordantDocumentOptions
            {
                WriterSession = OracleBridge.ToCore(r.Session),
            }))
            .ToArray();

        void ObserveAll()
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

        sim.Commit(0, tx =>
        {
            tx.DeclareRoot("text", RefRootKind.Text);
            tx.DeclareRoot("m", RefRootKind.Map);
        });
        sim.DeliverAll(duplicate: false);
        ObserveAll();

        sim.Commit(0, tx => tx.Insert("text", null, null, RefScalar.StringScalar.Create("H")));
        sim.DeliverAll(duplicate: false);
        ObserveAll();

        sim.Commit(1, tx => tx.Insert("text", null, null, RefScalar.StringScalar.Create("i")));
        sim.DeliverAll(duplicate: false);
        ObserveAll();

        sim.Commit(0, tx => tx.MapSet("m", "title", RefScalar.StringScalar.Create("hello")));
        sim.DeliverAll(duplicate: false);
        ObserveAll();

        sim.Commit(1, tx => tx.MapSet("m", "title", RefScalar.StringScalar.Create("world")));
        sim.DeliverAll(duplicate: true);
        sim.AssertConverged();
        Assert.Equal(ExpectedFingerprint, sim.Fingerprint());

        foreach (ConcordantDocument prod in prodReplicas)
        {
            foreach (RefBatch batch in sim.AllBatches)
            {
                _ = prod.Apply(OracleBridge.ToCore(batch));
            }

            for (int pass = 0; pass < sim.AllBatches.Count + 2; pass++)
            {
                foreach (RefBatch batch in sim.AllBatches)
                {
                    _ = prod.Apply(OracleBridge.ToCore(batch));
                }
            }

            Assert.Equal(ExpectedFingerprint, prod.VisibleFingerprint());
        }
    }
}
