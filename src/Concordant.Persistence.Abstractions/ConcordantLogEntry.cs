namespace Concordant.Persistence;

/// <summary>One durable append-log record: monotonic sequence plus opaque update payload bytes.</summary>
public readonly struct ConcordantLogEntry : IEquatable<ConcordantLogEntry>
{
    public ConcordantLogEntry(long sequence, ReadOnlyMemory<byte> payload)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be positive.");
        }

        Sequence = sequence;
        Payload = payload;
    }

    /// <summary>Monotonically increasing durable sequence assigned by the log.</summary>
    public long Sequence { get; }

    /// <summary>Opaque update bytes (typically a native delta from EncodeUpdateSince).</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    public bool Equals(ConcordantLogEntry other) =>
        Sequence == other.Sequence && Payload.Span.SequenceEqual(other.Payload.Span);

    public override bool Equals(object? obj) => obj is ConcordantLogEntry other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Sequence);
        hash.AddBytes(Payload.Span);
        return hash.ToHashCode();
    }

    public static bool operator ==(ConcordantLogEntry left, ConcordantLogEntry right) => left.Equals(right);

    public static bool operator !=(ConcordantLogEntry left, ConcordantLogEntry right) => !left.Equals(right);
}
