using Concordant;

namespace Concordant.Core.Tests;

public sealed class ScaffoldingTests
{
    [Fact]
    public void Assembly_exposes_package_name()
    {
        Assert.Equal("Concordant.Core", ConcordantAssembly.Name);
    }
}
