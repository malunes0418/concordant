namespace Concordant;

/// <summary>Stable operation identity: contiguous clock within a session.</summary>
public readonly struct OpId : IEquatable<OpId>, IComparable<OpId>
{
    public OpId(SessionId session, ulong clock)
    {
        if (clock == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clock), "Clocks are 1-based.");
        }

        Session = session;
        Clock = clock;
    }

    public SessionId Session { get; }

    public ulong Clock { get; }

    public int CompareTo(OpId other)
    {
        int c = Clock.CompareTo(other.Clock);
        return c != 0 ? c : Session.CompareTo(other.Session);
    }

    public bool Equals(OpId other) => Session == other.Session && Clock == other.Clock;

    public override bool Equals(object? obj) => obj is OpId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Session, Clock);

    public override string ToString() => $"{Session}@{Clock}";

    public static bool operator ==(OpId a, OpId b) => a.Equals(b);

    public static bool operator !=(OpId a, OpId b) => !a.Equals(b);

    public static bool operator <(OpId a, OpId b) => a.CompareTo(b) < 0;

    public static bool operator >(OpId a, OpId b) => a.CompareTo(b) > 0;
}
