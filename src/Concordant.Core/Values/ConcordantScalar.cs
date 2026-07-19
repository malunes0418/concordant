using System.Diagnostics.CodeAnalysis;

namespace Concordant.Values;

public enum ScalarKind : byte
{
    Null = 0,
    Bool = 1,
    Int64 = 2,
    Float64 = 3,
    String = 4,
}

/// <summary>
/// Canonical scalar domain: null, Boolean, Int64, finite binary64 with canonical +0,
/// and valid Unicode strings with ordinal equality.
/// </summary>
public abstract record ConcordantScalar
{
    private ConcordantScalar()
    {
    }

    public abstract ScalarKind Kind { get; }

    public sealed record NullScalar : ConcordantScalar
    {
        public static NullScalar Instance { get; } = new();

        public override ScalarKind Kind => ScalarKind.Null;
    }

    public sealed record BoolScalar(bool Value) : ConcordantScalar
    {
        public override ScalarKind Kind => ScalarKind.Bool;
    }

    public sealed record Int64Scalar(long Value) : ConcordantScalar
    {
        public override ScalarKind Kind => ScalarKind.Int64;
    }

    public sealed record Float64Scalar : ConcordantScalar
    {
        private Float64Scalar(double value) => Value = value;

        public double Value { get; }

        public override ScalarKind Kind => ScalarKind.Float64;

        public static Float64Scalar Create(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Only finite binary64 values are allowed.");
            }

            if (value == 0.0 && BitConverter.DoubleToInt64Bits(value) < 0)
            {
                value = 0.0;
            }

            return new Float64Scalar(value);
        }
    }

    public sealed record StringScalar : ConcordantScalar
    {
        private StringScalar(string value) => Value = value;

        public string Value { get; }

        public override ScalarKind Kind => ScalarKind.String;

        public static StringScalar Create(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!IsValidUnicode(value))
            {
                throw new ArgumentException("String must be valid Unicode (no lone surrogates).", nameof(value));
            }

            return new StringScalar(value);
        }

        private static bool IsValidUnicode(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    {
                        return false;
                    }

                    i++;
                }
                else if (char.IsLowSurrogate(c))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static ConcordantScalar Null => NullScalar.Instance;

    public static ConcordantScalar Bool(bool value) => new BoolScalar(value);

    public static ConcordantScalar Int64(long value) => new Int64Scalar(value);

    public static ConcordantScalar Float64(double value) => Float64Scalar.Create(value);

    public static ConcordantScalar String(string value) => StringScalar.Create(value);

    /// <summary>Canonical key used for fingerprints and equality traces.</summary>
    public string CanonicalKey() => this switch
    {
        NullScalar => "null",
        BoolScalar b => b.Value ? "true" : "false",
        Int64Scalar i => $"i:{i.Value}",
        Float64Scalar f => $"f:{BitConverter.DoubleToInt64Bits(f.Value):X16}",
        StringScalar s => $"s:{s.Value}",
        _ => throw new InvalidOperationException(),
    };

    public bool TryGetBool(out bool value)
    {
        if (this is BoolScalar b)
        {
            value = b.Value;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetInt64(out long value)
    {
        if (this is Int64Scalar i)
        {
            value = i.Value;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetFloat64(out double value)
    {
        if (this is Float64Scalar f)
        {
            value = f.Value;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetString([NotNullWhen(true)] out string? value)
    {
        if (this is StringScalar s)
        {
            value = s.Value;
            return true;
        }

        value = null;
        return false;
    }
}
