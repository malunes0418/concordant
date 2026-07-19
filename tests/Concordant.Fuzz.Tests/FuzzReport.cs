namespace Concordant.Fuzz.Tests;

internal sealed class FuzzReport
{
    public int Iterations { get; set; }

    public int Rejected { get; set; }

    public int Pending { get; set; }

    public int Integrated { get; set; }

    public int Duplicate { get; set; }

    public int Failures { get; set; }

    public string? FirstFailure { get; set; }

    public void Observe(ApplyStatus status)
    {
        switch (status)
        {
            case ApplyStatus.Rejected:
                Rejected++;
                break;
            case ApplyStatus.PendingDependencies:
                Pending++;
                break;
            case ApplyStatus.Integrated:
                Integrated++;
                break;
            case ApplyStatus.Duplicate:
                Duplicate++;
                break;
        }
    }

    public void Fail(string message)
    {
        Failures++;
        FirstFailure ??= message;
    }
}
