using Concordant.Values;

namespace Concordant;

/// <summary>Payload carried by map assignments and sequence inserts.</summary>
public abstract record ConcordantContent
{
    private ConcordantContent()
    {
    }

    /// <summary>Immutable scalar value.</summary>
    public sealed record ScalarContent(ConcordantScalar Value) : ConcordantContent;

    /// <summary>
    /// Attached nested shared type. The enclosing operation's <see cref="OpId"/> becomes the nested container id.
    /// </summary>
    public sealed record NestedContent(RootKind Kind) : ConcordantContent;

    public static ConcordantContent Scalar(ConcordantScalar value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ScalarContent(value);
    }

    public static ConcordantContent Nested(RootKind kind)
    {
        if (kind is not (RootKind.Map or RootKind.Array or RootKind.Text))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new NestedContent(kind);
    }

    public string CanonicalKey() => this switch
    {
        ScalarContent s => s.Value.CanonicalKey(),
        NestedContent n => $"nested:{n.Kind}",
        _ => throw new InvalidOperationException(),
    };
}
