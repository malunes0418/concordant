namespace Concordant.Persistence;

/// <summary>
/// Normative durability rules for hosts that combine <c>Concordant.Core</c> with these persistence abstractions.
/// Core never claims durability; hosts own the append/checkpoint boundary.
/// </summary>
public static class DurabilityContract
{
    /// <summary>
    /// Local document commits mutate in-memory state only. They are not durable by themselves.
    /// </summary>
    public const string MemoryCommitIsNotDurable =
        "ConcordantDocument commits are in-memory only; durability requires a successful host append.";

    /// <summary>
    /// Opaque update payloads become durable only after <see cref="IConcordantAppendLog.AppendAsync"/> completes successfully.
    /// </summary>
    public const string DurableAfterSuccessfulAppend =
        "Opaque update payloads are durable only after AppendAsync completes successfully.";

    /// <summary>
    /// Failed appends must be retried with the same payload. Hosts must not roll back in-memory document state
    /// to compensate for an append failure.
    /// </summary>
    public const string FailedAppendRetriesWithoutMemoryRollback =
        "On append failure, keep the in-memory document and retry the same update bytes idempotently; do not roll back memory.";

    /// <summary>
    /// Duplicate appends from at-least-once retry are acceptable; document apply paths treat identical operations as duplicates.
    /// </summary>
    public const string AppendRetriesAreIdempotentAtDocumentLayer =
        "Retrying AppendAsync may produce duplicate log entries; ApplyUpdate/Apply must tolerate identical operations as Duplicate.";

    /// <summary>
    /// Recovery loads checkpoint + log tail into a new document with a fresh writer session.
    /// Checkpoints never restore writable clocks or session identity.
    /// </summary>
    public const string RecoveryUsesFreshWriterSession =
        "Open recovery with a new ConcordantDocument (fresh CSPRNG writer session); never restore writable identity from storage.";
}
