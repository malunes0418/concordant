namespace Concordant.History;

/// <summary>Configuration for <see cref="UndoManager"/>.</summary>
public sealed class UndoManagerOptions
{
    /// <summary>
    /// Transaction origins that are captured onto the undo stack.
    /// When null, only the default <c>null</c> origin is tracked (local edits without an origin tag).
    /// </summary>
    public IEnumerable<object?>? TrackedOrigins { get; init; }

    /// <summary>
    /// Consecutive tracked transactions within this window are merged into one stack item.
    /// Use <c>0</c> to disable time-based grouping (each transaction is its own item).
    /// </summary>
    public int CaptureTimeoutMilliseconds { get; init; } = 500;

    /// <summary>Maximum undo stack items (transactions / merged groups). Default 100.</summary>
    public int MaxStackTransactions { get; init; } = 100;

    /// <summary>Approximate maximum retained undo metadata bytes. Default 8 MiB.</summary>
    public long MaxStackBytes { get; init; } = 8L * 1024 * 1024;
}
