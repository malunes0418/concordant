namespace Concordant.Sync.Native;

/// <summary>
/// Feature bits negotiated in the native wire header.
/// Unknown <b>required</b> bits cause reject; unknown <b>optional</b> bits are ignored.
/// </summary>
[Flags]
public enum NativeCodecFeatures : uint
{
    None = 0,

    /// <summary>Reserved for future optional range compression on the wire.</summary>
    OptionalRangeHints = 1 << 0,
}
