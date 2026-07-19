using Concordant.Model.Tests.ReferenceModel;
using Concordant.Values;
using RefOpId = Concordant.Model.Tests.ReferenceModel.OpId;
using RefRootKind = Concordant.Model.Tests.ReferenceModel.RootKind;
using RefSessionId = Concordant.Model.Tests.ReferenceModel.SessionId;

namespace Concordant.Core.Tests;

internal static class OracleBridge
{
    public static SessionId ToCore(RefSessionId session)
    {
        string hex = session.ToString();
        ulong hi = Convert.ToUInt64(hex[..16], 16);
        ulong lo = Convert.ToUInt64(hex[16..], 16);
        return new SessionId(hi, lo);
    }

    public static OpId ToCore(RefOpId id) => new(ToCore(id.Session), id.Clock);

    public static RootKind ToCore(RefRootKind kind) => kind switch
    {
        RefRootKind.Map => RootKind.Map,
        RefRootKind.Array => RootKind.Array,
        RefRootKind.Text => RootKind.Text,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static ConcordantScalar ToCore(RefScalar scalar) => scalar switch
    {
        RefScalar.NullScalar => ConcordantScalar.Null,
        RefScalar.BoolScalar b => ConcordantScalar.Bool(b.Value),
        RefScalar.Int64Scalar i => ConcordantScalar.Int64(i.Value),
        RefScalar.Float64Scalar f => ConcordantScalar.Float64(f.Value),
        RefScalar.StringScalar s => ConcordantScalar.String(s.Value),
        _ => throw new ArgumentOutOfRangeException(nameof(scalar)),
    };

    public static ConcordantOperation ToCore(RefOperation op) => op switch
    {
        RefOperation.RootDeclare root => new ConcordantOperation.RootDeclare(root.Name, ToCore(root.Kind))
        {
            Id = ToCore(root.Id),
            Lamport = root.Lamport,
            LamportSource = root.LamportSource is RefOpId src ? ToCore(src) : null,
        },
        RefOperation.MapSet map => new ConcordantOperation.MapSet(
            ContainerRef.Root(map.MapName),
            map.Key,
            ConcordantContent.Scalar(ToCore(map.Value)))
        {
            Id = ToCore(map.Id),
            Lamport = map.Lamport,
            LamportSource = map.LamportSource is RefOpId src ? ToCore(src) : null,
        },
        RefOperation.SeqInsert insert => new ConcordantOperation.SeqInsert(
            ContainerRef.Root(insert.ContainerName),
            insert.LeftOrigin is RefOpId left ? ToCore(left) : null,
            insert.RightOrigin is RefOpId right ? ToCore(right) : null,
            ConcordantContent.Scalar(ToCore(insert.Content)))
        {
            Id = ToCore(insert.Id),
            Lamport = insert.Lamport,
            LamportSource = insert.LamportSource is RefOpId src ? ToCore(src) : null,
        },
        RefOperation.SeqDelete delete => new ConcordantOperation.SeqDelete(ToCore(delete.TargetId))
        {
            Id = ToCore(delete.Id),
            Lamport = delete.Lamport,
            LamportSource = delete.LamportSource is RefOpId src ? ToCore(src) : null,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    public static OperationBatch ToCore(RefBatch batch) =>
        new(batch.Operations.Select(ToCore).ToArray());

    public static ApplyStatus ToCore(Model.Tests.ReferenceModel.ApplyStatus status) => status switch
    {
        Model.Tests.ReferenceModel.ApplyStatus.Integrated => ApplyStatus.Integrated,
        Model.Tests.ReferenceModel.ApplyStatus.PendingDependencies => ApplyStatus.PendingDependencies,
        Model.Tests.ReferenceModel.ApplyStatus.Duplicate => ApplyStatus.Duplicate,
        Model.Tests.ReferenceModel.ApplyStatus.Rejected => ApplyStatus.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
