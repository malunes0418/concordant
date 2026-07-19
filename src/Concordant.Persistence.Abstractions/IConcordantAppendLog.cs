namespace Concordant.Persistence;

/// <summary>
/// Host-owned append-only log of opaque Concordant update payloads.
/// </summary>
/// <remarks>
/// <para>
/// See <see cref="DurabilityContract"/>: memory commit ≠ durable append. Callers append after a successful
/// in-memory commit and must retry failed appends without rolling back document memory.
/// </para>
/// <para>
/// Payloads are opaque <see cref="ReadOnlyMemory{T}"/> byte buffers. This package does not depend on
/// codec types; hosts typically pass native update bytes from EncodeUpdateSince (or equivalent).
/// </para>
/// </remarks>
public interface IConcordantAppendLog
{
    /// <summary>
    /// Appends update bytes and returns the assigned durable sequence number (strictly increasing).
    /// </summary>
    /// <remarks>
    /// At-least-once retries may append duplicate payloads. That is acceptable: document apply paths
    /// treat identical operations as <c>Duplicate</c>. Implementations must not require hosts to roll
    /// back memory after a failed or uncertain append.
    /// </remarks>
    /// <param name="update">Opaque update payload. Empty updates are rejected.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Positive monotonic sequence for the durable record.</returns>
    ValueTask<long> AppendAsync(ReadOnlyMemory<byte> update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads durable entries with <see cref="ConcordantLogEntry.Sequence"/> greater than
    /// <paramref name="afterSequenceExclusive"/>, in ascending sequence order.
    /// </summary>
    /// <param name="afterSequenceExclusive">
    /// Exclusive lower bound. Pass <c>0</c> to read the entire log; pass a checkpoint's
    /// <see cref="ConcordantCheckpoint.CoveredLogSequence"/> to replay only the tail.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<ConcordantLogEntry> ReadFromAsync(
        long afterSequenceExclusive = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops durable entries with sequence less than or equal to <paramref name="inclusiveSequence"/>
    /// after a checkpoint covers them. No-op when the tip is already at or below that sequence.
    /// </summary>
    ValueTask TruncateThroughAsync(long inclusiveSequence, CancellationToken cancellationToken = default);

    /// <summary>Highest durable sequence number, or <c>0</c> when the log is empty.</summary>
    ValueTask<long> GetTipSequenceAsync(CancellationToken cancellationToken = default);
}
