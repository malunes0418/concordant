namespace Concordant.Model.Tests.ReferenceModel;

public abstract record RefOperation
{
    private RefOperation()
    {
    }

    public required OpId Id { get; init; }

    public required ulong Lamport { get; init; }

    public OpId? LamportSource { get; init; }

    public sealed record RootDeclare(string Name, RootKind Kind) : RefOperation;

    public sealed record MapSet(string MapName, string Key, RefScalar Value) : RefOperation;

    public sealed record SeqInsert(string ContainerName, OpId? LeftOrigin, OpId? RightOrigin, RefScalar Content) : RefOperation;

    public sealed record SeqDelete(OpId TargetId) : RefOperation;
}

/// <summary>Immutable batch of operations from one transaction/update.</summary>
public sealed class RefBatch
{
    public RefBatch(IReadOnlyList<RefOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            throw new ArgumentException("Batch must contain at least one operation.", nameof(operations));
        }

        Operations = operations;
    }

    public IReadOnlyList<RefOperation> Operations { get; }
}

public enum ApplyStatus
{
    Integrated,
    PendingDependencies,
    Duplicate,
    Rejected,
}

public sealed class ApplyResult
{
    public ApplyResult(ApplyStatus status, string? detail = null)
    {
        Status = status;
        Detail = detail;
    }

    public ApplyStatus Status { get; }

    public string? Detail { get; }

    public static ApplyResult Integrated() => new(ApplyStatus.Integrated);

    public static ApplyResult Pending(string detail) => new(ApplyStatus.PendingDependencies, detail);

    public static ApplyResult Duplicate() => new(ApplyStatus.Duplicate);

    public static ApplyResult Rejected(string detail) => new(ApplyStatus.Rejected, detail);
}
