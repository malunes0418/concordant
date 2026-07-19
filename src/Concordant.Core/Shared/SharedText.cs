using Concordant.Internal;
using Concordant.Internal.Sequences;
using Concordant.Transactions;
using Concordant.Values;

namespace Concordant.Shared;

/// <summary>
/// Shared plain text. Stores Unicode scalar values as atomic sequence elements and exposes
/// all public lengths, offsets, ranges, and deltas in UTF-16 code units.
/// </summary>
public sealed class SharedText
{
    private readonly ConcordantDocument _document;
    private readonly ContainerRef _container;

    internal SharedText(ConcordantDocument document, ContainerRef container)
    {
        _document = document;
        _container = container;
    }

    public ContainerRef Container => _container;

    /// <summary>Visible text length in UTF-16 code units.</summary>
    public int Length
    {
        get
        {
            _document.EnsureReadable();
            return ToString().Length;
        }
    }

    public override string ToString()
    {
        _document.EnsureReadable();
        return _document.Store.VisibleText(_container);
    }

    /// <summary>
    /// Inserts <paramref name="text"/> at the given UTF-16 offset.
    /// Each Unicode scalar becomes one sequence item.
    /// </summary>
    public void Insert(int utf16Offset, string text)
    {
        Utf16Text.EnsureValidUnicode(text);
        if (text.Length == 0)
        {
            return;
        }

        string current = ToString();
        Utf16Text.EnsureOffsetNotSplittingSurrogate(current, utf16Offset);

        Transaction tx = _document.RequireTransaction();
        ResolveInsertOrigins(utf16Offset, out OpId? left, out OpId? right);

        OpId? prev = left;
        foreach (string chunk in Utf16Text.EnumerateScalarChunks(text))
        {
            ConcordantOperation op = tx.SeqInsert(
                _container,
                prev,
                right,
                ConcordantContent.Scalar(ConcordantScalar.String(chunk)));
            prev = op.Id;
        }
    }

    /// <summary>Deletes <paramref name="utf16Length"/> UTF-16 code units starting at <paramref name="utf16Offset"/>.</summary>
    public void Delete(int utf16Offset, int utf16Length)
    {
        if (utf16Length == 0)
        {
            return;
        }

        string current = ToString();
        Utf16Text.EnsureRangeNotSplittingSurrogate(current, utf16Offset, utf16Length);

        Transaction tx = _document.RequireTransaction();
        List<(OpId Id, int Utf16Len)> items = GetVisibleTextItems();
        int cursor = 0;
        int remaining = utf16Length;
        int start = utf16Offset;

        foreach ((OpId id, int len) in items)
        {
            if (remaining <= 0)
            {
                break;
            }

            int itemEnd = cursor + len;
            if (itemEnd <= start)
            {
                cursor = itemEnd;
                continue;
            }

            if (cursor >= start + utf16Length)
            {
                break;
            }

            // Item overlaps the deletion range. Because items are Unicode scalars,
            // they are never split by a valid UTF-16 range.
            if (cursor < start || itemEnd > start + utf16Length)
            {
                throw new ArgumentException("Delete range must align to Unicode scalar boundaries.");
            }

            tx.SeqDelete(id);
            remaining -= len;
            cursor = itemEnd;
        }

        if (remaining != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(utf16Length));
        }
    }

    public string Slice(int utf16Offset, int utf16Length)
    {
        string current = ToString();
        Utf16Text.EnsureRangeNotSplittingSurrogate(current, utf16Offset, utf16Length);
        return current.Substring(utf16Offset, utf16Length);
    }

    private void ResolveInsertOrigins(int utf16Offset, out OpId? left, out OpId? right)
    {
        List<(OpId Id, int Utf16Len)> items = GetVisibleTextItems();
        int cursor = 0;
        left = null;
        right = null;

        foreach ((OpId id, int len) in items)
        {
            if (cursor + len <= utf16Offset)
            {
                left = id;
                cursor += len;
                continue;
            }

            if (cursor == utf16Offset)
            {
                right = id;
                return;
            }

            throw new ArgumentException("UTF-16 offset must align to a Unicode scalar boundary.", nameof(utf16Offset));
        }

        if (cursor != utf16Offset)
        {
            throw new ArgumentOutOfRangeException(nameof(utf16Offset));
        }

        right = null;
    }

    private List<(OpId Id, int Utf16Len)> GetVisibleTextItems()
    {
        var result = new List<(OpId, int)>();
        YataSequence? seq = _document.Store.TryGetSequence(_container);
        if (seq is null)
        {
            return result;
        }

        foreach (SeqItem item in seq.VisibleItems())
        {
            if (item.Content is not ConcordantContent.ScalarContent { Value: ConcordantScalar.StringScalar s })
            {
                throw new InvalidOperationException("SharedText sequence contains a non-string item.");
            }

            result.Add((item.Id, s.Value.Length));
        }

        return result;
    }
}
