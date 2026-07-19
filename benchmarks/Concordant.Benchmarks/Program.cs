using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Concordant.Benchmarks;

/// <summary>
/// BenchmarkDotNet entry point plus optional one-shot limit workloads.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// dotnet run -c Release -f net8.0 -- --filter "*"
/// dotnet run -c Release -f net8.0 -- --limit
/// dotnet run -c Release -f net8.0 -- --filter "*Local*" --limit
/// </code>
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        bool runLimit = args.Any(static a =>
            string.Equals(a, "--limit", StringComparison.OrdinalIgnoreCase));

        string[] bdnArgs = args
            .Where(static a => !string.Equals(a, "--limit", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        int exit = 0;
        if (bdnArgs.Length > 0 || !runLimit)
        {
            // Default to all benchmarks when no BDN args are provided (unless --limit-only intent).
            if (bdnArgs.Length == 0)
            {
                bdnArgs = ["--filter", "*"];
            }

            IConfig config = new FastInProcessConfig();
            BenchmarkSwitcher switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);
            _ = switcher.Run(bdnArgs, config);
        }

        if (runLimit)
        {
            exit = LimitWorkloadRunner.Run();
        }

        return exit;
    }
}
