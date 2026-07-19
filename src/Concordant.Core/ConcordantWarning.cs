namespace Concordant;

/// <summary>Non-fatal document warning (e.g. concurrent root kind conflict).</summary>
public sealed class ConcordantWarning
{
    public ConcordantWarning(ConcordantWarningKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    public ConcordantWarningKind Kind { get; }

    public string Message { get; }
}

public enum ConcordantWarningKind
{
    RootKindConflict = 1,
}
