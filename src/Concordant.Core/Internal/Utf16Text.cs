namespace Concordant.Internal;

internal static class Utf16Text
{
    public static void EnsureValidUnicode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValidUnicode(value))
        {
            throw new ArgumentException("String must be valid Unicode (no lone surrogates).", nameof(value));
        }
    }

    public static bool IsValidUnicode(string value)
    {
        for (int i = 0; i < value.Length;)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i += 2;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
            else
            {
                i++;
            }
        }

        return true;
    }

    public static void EnsureOffsetNotSplittingSurrogate(string text, int utf16Offset)
    {
        if (utf16Offset < 0 || utf16Offset > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(utf16Offset));
        }

        if (utf16Offset > 0
            && utf16Offset < text.Length
            && char.IsHighSurrogate(text[utf16Offset - 1])
            && char.IsLowSurrogate(text[utf16Offset]))
        {
            throw new ArgumentException(
                "UTF-16 offset must not split a surrogate pair.",
                nameof(utf16Offset));
        }
    }

    public static void EnsureRangeNotSplittingSurrogate(string text, int utf16Offset, int utf16Length)
    {
        EnsureOffsetNotSplittingSurrogate(text, utf16Offset);
        if (utf16Length < 0 || utf16Offset + utf16Length > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(utf16Length));
        }

        EnsureOffsetNotSplittingSurrogate(text, utf16Offset + utf16Length);
    }

    /// <summary>
    /// Enumerates Unicode scalar values as UTF-16 substrings (1 or 2 code units each).
    /// </summary>
    public static IEnumerable<string> EnumerateScalarChunks(string value)
    {
        EnsureValidUnicode(value);
        for (int i = 0; i < value.Length;)
        {
            int len = char.IsHighSurrogate(value[i]) ? 2 : 1;
            yield return value.Substring(i, len);
            i += len;
        }
    }
}
