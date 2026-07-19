namespace Concordant.History;

/// <summary>Result of an undo or redo attempt.</summary>
public sealed class UndoResult
{
    public UndoResult(UndoStatus status, string? detail = null)
    {
        Status = status;
        Detail = detail;
    }

    public UndoStatus Status { get; }

    public string? Detail { get; }

    public static UndoResult Applied() => new(UndoStatus.Applied);

    public static UndoResult NoVisibleChange(string? detail = null) =>
        new(UndoStatus.NoVisibleChange, detail);

    public static UndoResult RemoteWinner(string? detail = null) =>
        new(UndoStatus.RemoteWinner, detail);

    public static UndoResult Empty() => new(UndoStatus.Empty);

    public static UndoResult HistoryEvicted(string? detail = null) =>
        new(UndoStatus.HistoryEvicted, detail);
}
