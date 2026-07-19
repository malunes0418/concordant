namespace Concordant.Sync.Native;

/// <summary>Constants for the versioned native binary update format.</summary>
public static class NativeWireFormat
{
    /// <summary>ASCII "CNCR".</summary>
    public static ReadOnlySpan<byte> Magic => "CNCR"u8;

    public const ushort CurrentVersion = 1;

    /// <summary>Features this library can satisfy when marked required.</summary>
    public const NativeCodecFeatures SupportedRequiredFeatures = NativeCodecFeatures.None;

    /// <summary>Optional features this encoder may emit.</summary>
    public const NativeCodecFeatures SupportedOptionalFeatures = NativeCodecFeatures.None;

    public const int HeaderSize = 16;

    // Header layout (little-endian unless noted):
    // 0..3   magic "CNCR"
    // 4..5   version u16 LE
    // 6      kind u8 (Update=1, Checkpoint=2)
    // 7      reserved u8 (=0)
    // 8..11  requiredFeatures u32 LE
    // 12..15 optionalFeatures u32 LE
    // then:  opCount u32 LE
    // then:  ops...

    public const byte OpRootDeclare = 1;
    public const byte OpMapSet = 2;
    public const byte OpSeqInsert = 3;
    public const byte OpSeqDelete = 4;

    public const byte ContainerRoot = 0;
    public const byte ContainerNested = 1;

    public const byte ContentScalar = 0;
    public const byte ContentNested = 1;
}
