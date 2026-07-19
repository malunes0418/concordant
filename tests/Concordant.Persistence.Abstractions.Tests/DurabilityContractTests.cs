namespace Concordant.Persistence.Abstractions.Tests;

public sealed class DurabilityContractTests
{
    [Fact]
    public void Contract_constants_document_memory_vs_durable_boundary()
    {
        Assert.Contains("in-memory only", DurabilityContract.MemoryCommitIsNotDurable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AppendAsync", DurabilityContract.DurableAfterSuccessfulAppend, StringComparison.Ordinal);
        Assert.Contains("do not roll back memory", DurabilityContract.FailedAppendRetriesWithoutMemoryRollback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Duplicate", DurabilityContract.AppendRetriesAreIdempotentAtDocumentLayer, StringComparison.Ordinal);
        Assert.Contains("fresh", DurabilityContract.RecoveryUsesFreshWriterSession, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Package_identity_is_stable()
    {
        Assert.Equal("Concordant.Persistence.Abstractions", PersistenceAbstractions.Name);
    }
}
