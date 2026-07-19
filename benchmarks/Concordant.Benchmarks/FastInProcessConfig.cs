using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Concordant.Benchmarks;

/// <summary>Fast in-process config for release-gate baselines (avoids per-benchmark process spawn).</summary>
internal sealed class FastInProcessConfig : ManualConfig
{
    public FastInProcessConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(1)
            .WithIterationCount(12)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithToolchain(InProcessEmitToolchain.Instance));
        AddDiagnoser(MemoryDiagnoser.Default);
        AddLogger(ConsoleLogger.Default);
        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}
