namespace Concordant.History;

/// <summary>Explicit outcome of <see cref="UndoManager.Undo"/> / <see cref="UndoManager.Redo"/>.</summary>
public enum UndoStatus
{
    /// <summary>Inverse operations were applied and visible state changed.</summary>
    Applied = 0,

    /// <summary>
    /// Inverse operations ran (or were skipped as already satisfied) but visible state was unchanged.
    /// </summary>
    NoVisibleChange = 1,

    /// <summary>
    /// Map undo/redo was skipped because a remote assignment currently wins the register.
    /// </summary>
    RemoteWinner = 2,

    /// <summary>Undo/redo stack was empty.</summary>
    Empty = 3,

    /// <summary>
    /// Stack was empty because older entries were dropped by the count/byte budget.
    /// </summary>
    HistoryEvicted = 4,
}
