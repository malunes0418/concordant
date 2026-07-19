namespace Concordant.Model.Tests.ReferenceModel;

public enum RootKind
{
    Map = 1,
    Array = 2,
    Text = 3,
}

public enum ScalarKind
{
    Null = 0,
    Bool = 1,
    Int64 = 2,
    Float64 = 3,
    String = 4,
}

/// <summary>Canonical scalar domain for the reference oracle.</summary>
public abstract record RefScalar
{
    private RefScalar()
    {
    }

    public sealed record NullScalar : RefScalar
    {
        public static NullScalar Instance { get; } = new();
    }

    public sealed record BoolScalar(bool Value) : RefScalar;

    public sealed record Int64Scalar(long Value) : RefScalar;

    public sealed record Float64Scalar(double Value) : RefScalar
    {
        public static Float64Scalar Create(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Only finite binary64 values are allowed.");
            }

            // Canonicalize -0.0 to +0.0
            if (value == 0.0 && BitConverter.DoubleToInt64Bits(value) < 0)
            {
                value = 0.0;
            }

            return new Float64Scalar(value);
        }
    }

    public sealed record StringScalar(string Value) : RefScalar
    {
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

    public string CanonicalKey() => this switch
    {
        NullScalar => "null",
        BoolScalar b => b.Value ? "true" : "false",
        Int64Scalar i => $"i:{i.Value}",
        Float64Scalar f => $"f:{BitConverter.DoubleToInt64Bits(f.Value):X16}",
        StringScalar s => $"s:{s.Value}",
        _ => throw new InvalidOperationException(),
    };
}
