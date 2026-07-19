namespace Concordant.Model.Tests.ReferenceModel;

/// <summary>128-bit writer session identity. Compared as unsigned big-endian bytes.</summary>
public readonly struct SessionId : IEquatable<SessionId>, IComparable<SessionId>
{
    private readonly ulong _hi;
    private readonly ulong _lo;

    public SessionId(ulong hi, ulong lo)
    {
        _hi = hi;
        _lo = lo;
    }

    public static SessionId FromSeed(ulong seed)
    {
        // Deterministic non-CSPRNG helper for tests/simulator only.
        ulong hi = seed * 0x9E3779B97F4A7C15UL;
        ulong lo = (seed + 1) * 0xBF58476D1CE4E5B9UL;
        return new SessionId(hi, lo);
    }

    public int CompareTo(SessionId other)
    {
        int c = _hi.CompareTo(other._hi);
        return c != 0 ? c : _lo.CompareTo(other._lo);
    }

    public bool Equals(SessionId other) => _hi == other._hi && _lo == other._lo;

    public override bool Equals(object? obj) => obj is SessionId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_hi, _lo);

    public override string ToString() => $"{_hi:X16}{_lo:X16}";

    public static bool operator ==(SessionId a, SessionId b) => a.Equals(b);

    public static bool operator !=(SessionId a, SessionId b) => !a.Equals(b);
}
