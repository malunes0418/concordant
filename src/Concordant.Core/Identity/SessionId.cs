using System.Security.Cryptography;

namespace Concordant;

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

    public ulong High => _hi;

    public ulong Low => _lo;

    /// <summary>Creates a fresh session identity using a CSPRNG.</summary>
    public static SessionId CreateCryptographic()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        ulong hi = BinaryPrimitivesReadUInt64BigEndian(bytes);
        ulong lo = BinaryPrimitivesReadUInt64BigEndian(bytes.Slice(8));
        return new SessionId(hi, lo);
    }

    /// <summary>Deterministic helper for tests and fixtures. Not for production writers.</summary>
    public static SessionId FromSeed(ulong seed)
    {
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

    public void WriteBytes(Span<byte> destination)
    {
        if (destination.Length < 16)
        {
            throw new ArgumentException("Destination must be at least 16 bytes.", nameof(destination));
        }

        WriteUInt64BigEndian(destination, _hi);
        WriteUInt64BigEndian(destination.Slice(8), _lo);
    }

    public static bool operator ==(SessionId a, SessionId b) => a.Equals(b);

    public static bool operator !=(SessionId a, SessionId b) => !a.Equals(b);

    public static bool operator <(SessionId a, SessionId b) => a.CompareTo(b) < 0;

    public static bool operator >(SessionId a, SessionId b) => a.CompareTo(b) > 0;

    private static ulong BinaryPrimitivesReadUInt64BigEndian(ReadOnlySpan<byte> source) =>
        ((ulong)source[0] << 56)
        | ((ulong)source[1] << 48)
        | ((ulong)source[2] << 40)
        | ((ulong)source[3] << 32)
        | ((ulong)source[4] << 24)
        | ((ulong)source[5] << 16)
        | ((ulong)source[6] << 8)
        | source[7];

    private static void WriteUInt64BigEndian(Span<byte> destination, ulong value)
    {
        destination[0] = (byte)(value >> 56);
        destination[1] = (byte)(value >> 48);
        destination[2] = (byte)(value >> 40);
        destination[3] = (byte)(value >> 32);
        destination[4] = (byte)(value >> 24);
        destination[5] = (byte)(value >> 16);
        destination[6] = (byte)(value >> 8);
        destination[7] = (byte)value;
    }
}
