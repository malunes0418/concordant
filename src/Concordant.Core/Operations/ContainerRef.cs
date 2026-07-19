namespace Concordant;

/// <summary>
/// Identifies a sequence or map container: either a named root or an attached nested node OpId.
/// </summary>
public readonly struct ContainerRef : IEquatable<ContainerRef>
{
    private readonly string? _rootName;
    private readonly OpId _nestedId;
    private readonly bool _isNested;

    private ContainerRef(string rootName)
    {
        _rootName = rootName;
        _nestedId = default;
        _isNested = false;
    }

    private ContainerRef(OpId nestedId)
    {
        _rootName = null;
        _nestedId = nestedId;
        _isNested = true;
    }

    public bool IsRoot => !_isNested;

    public bool IsNested => _isNested;

    public string? RootName => _isNested ? null : _rootName;

    public OpId? NestedId => _isNested ? _nestedId : null;

    public static ContainerRef Root(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new ContainerRef(name);
    }

    public static ContainerRef Nested(OpId id) => new(id);

    public string FingerprintKey() =>
        _isNested ? $"#{_nestedId}" : _rootName!;

    public bool Equals(ContainerRef other) =>
        _isNested == other._isNested
        && (_isNested
            ? _nestedId == other._nestedId
            : string.Equals(_rootName, other._rootName, StringComparison.Ordinal));

    public override bool Equals(object? obj) => obj is ContainerRef other && Equals(other);

    public override int GetHashCode() =>
        _isNested
            ? HashCode.Combine(1, _nestedId)
            : HashCode.Combine(0, _rootName);

    public override string ToString() => FingerprintKey();

    public static bool operator ==(ContainerRef a, ContainerRef b) => a.Equals(b);

    public static bool operator !=(ContainerRef a, ContainerRef b) => !a.Equals(b);
}
