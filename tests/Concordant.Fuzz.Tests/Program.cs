using System.Diagnostics;

namespace Concordant.Fuzz.Tests;

/// <summary>
/// Hardening-phase fuzz entrypoint. Smoke runs are bounded by time and allocation ceilings.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--smoke"])
        {
            return RunSmoke();
        }

        Console.WriteLine("Usage: Concordant.Fuzz.Tests --smoke");
        return 1;
    }

    private static int RunSmoke()
    {
        var sw = Stopwatch.StartNew();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();

        FuzzReport native = NativeBytesFuzzer.Run(
            seed: 20260719,
            iterations: 256,
            maxBytes: 512,
            timeBudget: TimeSpan.FromSeconds(5));

        FuzzReport batches = CanonicalBatchFuzzer.Run(
            seed: 20260719,
            iterations: 128,
            timeBudget: TimeSpan.FromSeconds(5));

        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        long allocated = Math.Max(0, allocAfter - allocBefore);

        Console.WriteLine($"Concordant.Fuzz.Tests smoke");
        Console.WriteLine($"  native: iterations={native.Iterations} reject={native.Rejected} pending={native.Pending} integrated={native.Integrated} duplicate={native.Duplicate}");
        Console.WriteLine($"  batch:  iterations={batches.Iterations} reject={batches.Rejected} pending={batches.Pending} integrated={batches.Integrated} duplicate={batches.Duplicate}");
        Console.WriteLine($"  elapsed_ms={sw.ElapsedMilliseconds} alloc_bytes~={allocated}");
        Console.WriteLine($"  tfm={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

        if (native.Failures != 0 || batches.Failures != 0)
        {
            Console.Error.WriteLine($"FAIL: native_failures={native.Failures} batch_failures={batches.Failures}");
            if (native.FirstFailure is not null)
            {
                Console.Error.WriteLine($"  native: {native.FirstFailure}");
            }

            if (batches.FirstFailure is not null)
            {
                Console.Error.WriteLine($"  batch: {batches.FirstFailure}");
            }

            return 2;
        }

        // Soft allocation ceiling for smoke (not a hard CI fail unless pathological).
        const long softAllocCeiling = 256L * 1024 * 1024;
        if (allocated > softAllocCeiling)
        {
            Console.Error.WriteLine($"FAIL: allocation ceiling exceeded ({allocated} > {softAllocCeiling})");
            return 3;
        }

        if (sw.Elapsed > TimeSpan.FromSeconds(30))
        {
            Console.Error.WriteLine($"FAIL: smoke time ceiling exceeded ({sw.Elapsed})");
            return 4;
        }

        Console.WriteLine("Concordant.Fuzz.Tests smoke: OK");
        return 0;
    }
}
