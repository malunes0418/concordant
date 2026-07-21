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
            YataSequence? seq = _document.Store.TryGetSequence(_container);
            return seq?.VisibleUtf16Length ?? 0;
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

        YataSequence? seq = _document.Store.TryGetSequence(_container);
        string current = seq?.BuildVisibleText() ?? string.Empty;
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

        YataSequence? seq = _document.Store.TryGetSequence(_container);
        if (seq is null)
        {
            throw new ArgumentOutOfRangeException(nameof(utf16Offset));
        }

        string current = seq.BuildVisibleText();
        Utf16Text.EnsureRangeNotSplittingSurrogate(current, utf16Offset, utf16Length);

        Transaction tx = _document.RequireTransaction();
        foreach (OpId id in seq.CollectUtf16DeleteTargets(utf16Offset, utf16Length))
        {
            tx.SeqDelete(id);
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
        YataSequence? seq = _document.Store.TryGetSequence(_container);
        if (seq is null)
        {
            if (utf16Offset != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(utf16Offset));
            }

            left = null;
            right = null;
            return;
        }

        seq.ResolveUtf16InsertOrigins(utf16Offset, out left, out right);
    }
}
