using System.Diagnostics.CodeAnalysis;

namespace Concordant.Sync;

/// <summary>
/// Experimental boundary: maps update/checkpoint bytes ↔ canonical <see cref="OperationBatch"/>.
/// Codecs must never mutate document store internals; core always revalidates decoded batches.
/// </summary>
[Experimental("CNCR001")]
public interface IUpdateCodec
{
    /// <summary>Encodes a canonical batch (possibly empty) into wire bytes.</summary>
    byte[] Encode(IReadOnlyList<ConcordantOperation> operations, UpdateEncodeKind kind);

    /// <summary>
    /// Decodes wire bytes into a canonical batch without integrating.
    /// Failures are returned; this method must not throw for malformed input within size bounds.
    /// </summary>
    CodecDecodeResult Decode(ReadOnlySpan<byte> bytes, CodecDecodeLimits limits);
}

/// <summary>Whether the payload is a delta update or a full-state checkpoint.</summary>
public enum UpdateEncodeKind : byte
{
    Update = 1,
    Checkpoint = 2,
}

/// <summary>Decode-time resource bounds supplied by the document options.</summary>
public readonly struct CodecDecodeLimits
{
    public CodecDecodeLimits(long maxBytes, long maxOperations, long maxContentUtf16Length)
    {
        MaxBytes = maxBytes;
        MaxOperations = maxOperations;
        MaxContentUtf16Length = maxContentUtf16Length;
    }

    public long MaxBytes { get; }

    public long MaxOperations { get; }

    public long MaxContentUtf16Length { get; }
}

/// <summary>Result of an <see cref="IUpdateCodec.Decode"/> attempt.</summary>
public sealed class CodecDecodeResult
{
    private CodecDecodeResult(
        bool success,
        IReadOnlyList<ConcordantOperation>? operations,
        UpdateEncodeKind kind,
        int version,
        uint requiredFeatures,
        uint optionalFeatures,
        string? error,
        ApplyRejectReason rejectReason)
    {
        Success = success;
        Operations = operations ?? Array.Empty<ConcordantOperation>();
        Kind = kind;
        Version = version;
        RequiredFeatures = requiredFeatures;
        OptionalFeatures = optionalFeatures;
        Error = error;
        RejectReason = rejectReason;
    }

    public bool Success { get; }

    public IReadOnlyList<ConcordantOperation> Operations { get; }

    public UpdateEncodeKind Kind { get; }

    public int Version { get; }

    public uint RequiredFeatures { get; }

    public uint OptionalFeatures { get; }

    public string? Error { get; }

    public ApplyRejectReason RejectReason { get; }

    public static CodecDecodeResult Ok(
        IReadOnlyList<ConcordantOperation> operations,
        UpdateEncodeKind kind,
        int version,
        uint requiredFeatures,
        uint optionalFeatures) =>
        new(true, operations, kind, version, requiredFeatures, optionalFeatures, null, ApplyRejectReason.None);

    public static CodecDecodeResult Fail(
        string error,
        ApplyRejectReason reason,
        int version = 0,
        uint requiredFeatures = 0) =>
        new(false, null, default, version, requiredFeatures, 0, error, reason);
}
