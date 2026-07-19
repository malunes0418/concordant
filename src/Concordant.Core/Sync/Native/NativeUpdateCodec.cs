using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Concordant.Values;

namespace Concordant.Sync.Native;

/// <summary>
/// Versioned native binary codec. Encodes/decodes canonical operations only;
/// never touches store internals. Core must revalidate every decoded batch.
/// </summary>
[Experimental("CNCR001")]
public sealed class NativeUpdateCodec : IUpdateCodec
{
    public static NativeUpdateCodec Instance { get; } = new();

    public byte[] Encode(IReadOnlyList<ConcordantOperation> operations, UpdateEncodeKind kind)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (kind is not (UpdateEncodeKind.Update or UpdateEncodeKind.Checkpoint))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var buffer = new ArrayBufferWriter<byte>(NativeWireFormat.HeaderSize + 4 + operations.Count * 48);
        Encode(operations, kind, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    public void Encode(IReadOnlyList<ConcordantOperation> operations, UpdateEncodeKind kind, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(output);
        if (kind is not (UpdateEncodeKind.Update or UpdateEncodeKind.Checkpoint))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var writer = new NativeBinaryWriter(output);
        writer.WriteBytes(NativeWireFormat.Magic);
        writer.WriteUInt16(NativeWireFormat.CurrentVersion);
        writer.WriteByte((byte)kind);
        writer.WriteByte(0); // reserved
        writer.WriteUInt32((uint)NativeWireFormat.SupportedRequiredFeatures);
        writer.WriteUInt32((uint)NativeWireFormat.SupportedOptionalFeatures);
        writer.WriteUInt32(checked((uint)operations.Count));

        foreach (ConcordantOperation op in operations)
        {
            WriteOperation(ref writer, op);
        }
    }

    public CodecDecodeResult Decode(ReadOnlySpan<byte> bytes, CodecDecodeLimits limits)
    {
        if (bytes.Length == 0)
        {
            return CodecDecodeResult.Fail("Update is empty.", ApplyRejectReason.MalformedUpdate);
        }

        if (bytes.Length > limits.MaxBytes)
        {
            return CodecDecodeResult.Fail(
                $"Update exceeds MaxUpdateBytes ({limits.MaxBytes}).",
                ApplyRejectReason.QuotaExceeded);
        }

        if (bytes.Length < NativeWireFormat.HeaderSize + 4)
        {
            return CodecDecodeResult.Fail("Update header truncated.", ApplyRejectReason.MalformedUpdate);
        }

        if (!bytes.Slice(0, 4).SequenceEqual(NativeWireFormat.Magic))
        {
            return CodecDecodeResult.Fail("Invalid magic.", ApplyRejectReason.MalformedUpdate);
        }

        var reader = new NativeBinaryReader(bytes, limits.MaxContentUtf16Length);
        _ = reader.TryReadExact(4, out _); // magic

        if (!reader.TryReadUInt16(out ushort version))
        {
            return CodecDecodeResult.Fail("Truncated version.", ApplyRejectReason.MalformedUpdate);
        }

        if (version != NativeWireFormat.CurrentVersion)
        {
            return CodecDecodeResult.Fail(
                $"Unsupported codec version {version}.",
                ApplyRejectReason.UnsupportedVersion,
                version);
        }

        if (!reader.TryReadByte(out byte kindByte)
            || kindByte is not ((byte)UpdateEncodeKind.Update or (byte)UpdateEncodeKind.Checkpoint))
        {
            return CodecDecodeResult.Fail("Invalid update kind.", ApplyRejectReason.MalformedUpdate, version);
        }

        var kind = (UpdateEncodeKind)kindByte;
        if (!reader.TryReadByte(out byte reserved) || reserved != 0)
        {
            return CodecDecodeResult.Fail("Invalid reserved header byte.", ApplyRejectReason.MalformedUpdate, version);
        }

        if (!reader.TryReadUInt32(out uint requiredFeaturesRaw)
            || !reader.TryReadUInt32(out uint optionalFeaturesRaw))
        {
            return CodecDecodeResult.Fail("Truncated feature negotiation.", ApplyRejectReason.MalformedUpdate, version);
        }

        var requiredFeatures = (NativeCodecFeatures)requiredFeaturesRaw;
        var optionalFeatures = (NativeCodecFeatures)optionalFeaturesRaw;
        _ = optionalFeatures; // unknown optional bits are ignored by design

        NativeCodecFeatures unsupportedRequired = requiredFeatures & ~NativeWireFormat.SupportedRequiredFeatures;
        if (unsupportedRequired != NativeCodecFeatures.None)
        {
            return CodecDecodeResult.Fail(
                $"Unsupported required features: 0x{(uint)unsupportedRequired:X8}.",
                ApplyRejectReason.UnsupportedVersion,
                version,
                requiredFeaturesRaw);
        }

        if (!reader.TryReadUInt32(out uint opCount))
        {
            return CodecDecodeResult.Fail("Truncated operation count.", ApplyRejectReason.MalformedUpdate, version, requiredFeaturesRaw);
        }

        if (opCount > (ulong)limits.MaxOperations)
        {
            return CodecDecodeResult.Fail(
                "Operation count exceeds MaxOperations.",
                ApplyRejectReason.QuotaExceeded,
                version,
                requiredFeaturesRaw);
        }

        var ops = new List<ConcordantOperation>(checked((int)opCount));
        for (uint i = 0; i < opCount; i++)
        {
            if (!TryReadOperation(ref reader, out ConcordantOperation? op) || op is null)
            {
                return CodecDecodeResult.Fail(
                    $"Malformed operation at index {i}.",
                    ApplyRejectReason.MalformedUpdate,
                    version,
                    requiredFeaturesRaw);
            }

            ops.Add(op);
        }

        if (reader.Remaining != 0)
        {
            return CodecDecodeResult.Fail(
                "Trailing bytes after operation stream.",
                ApplyRejectReason.MalformedUpdate,
                version,
                requiredFeaturesRaw);
        }

        return CodecDecodeResult.Ok(ops, kind, version, requiredFeaturesRaw, optionalFeaturesRaw);
    }

    private static void WriteOperation(ref NativeBinaryWriter writer, ConcordantOperation op)
    {
        switch (op)
        {
            case ConcordantOperation.RootDeclare root:
                writer.WriteByte(NativeWireFormat.OpRootDeclare);
                WriteCommon(ref writer, op);
                writer.WriteUtf8String(root.Name);
                writer.WriteByte((byte)root.Kind);
                break;
            case ConcordantOperation.MapSet mapSet:
                writer.WriteByte(NativeWireFormat.OpMapSet);
                WriteCommon(ref writer, op);
                WriteContainer(ref writer, mapSet.Map);
                writer.WriteUtf8String(mapSet.Key);
                WriteContent(ref writer, mapSet.Value);
                break;
            case ConcordantOperation.SeqInsert insert:
                writer.WriteByte(NativeWireFormat.OpSeqInsert);
                WriteCommon(ref writer, op);
                WriteContainer(ref writer, insert.Container);
                writer.WriteOptionalOpId(insert.LeftOrigin);
                writer.WriteOptionalOpId(insert.RightOrigin);
                WriteContent(ref writer, insert.Content);
                break;
            case ConcordantOperation.SeqDelete delete:
                writer.WriteByte(NativeWireFormat.OpSeqDelete);
                WriteCommon(ref writer, op);
                writer.WriteOpId(delete.TargetId);
                break;
            default:
                throw new InvalidOperationException($"Unknown operation type {op.GetType().Name}.");
        }
    }

    private static void WriteCommon(ref NativeBinaryWriter writer, ConcordantOperation op)
    {
        writer.WriteOpId(op.Id);
        writer.WriteUInt64(op.Lamport);
        writer.WriteOptionalOpId(op.LamportSource);
    }

    private static void WriteContainer(ref NativeBinaryWriter writer, ContainerRef container)
    {
        if (container.IsRoot)
        {
            writer.WriteByte(NativeWireFormat.ContainerRoot);
            writer.WriteUtf8String(container.RootName!);
            return;
        }

        writer.WriteByte(NativeWireFormat.ContainerNested);
        writer.WriteOpId(container.NestedId!.Value);
    }

    private static void WriteContent(ref NativeBinaryWriter writer, ConcordantContent content)
    {
        switch (content)
        {
            case ConcordantContent.ScalarContent scalar:
                writer.WriteByte(NativeWireFormat.ContentScalar);
                WriteScalar(ref writer, scalar.Value);
                break;
            case ConcordantContent.NestedContent nested:
                writer.WriteByte(NativeWireFormat.ContentNested);
                writer.WriteByte((byte)nested.Kind);
                break;
            default:
                throw new InvalidOperationException("Unknown content type.");
        }
    }

    private static void WriteScalar(ref NativeBinaryWriter writer, ConcordantScalar scalar)
    {
        writer.WriteByte((byte)scalar.Kind);
        switch (scalar)
        {
            case ConcordantScalar.NullScalar:
                break;
            case ConcordantScalar.BoolScalar b:
                writer.WriteByte(b.Value ? (byte)1 : (byte)0);
                break;
            case ConcordantScalar.Int64Scalar i:
                writer.WriteInt64(i.Value);
                break;
            case ConcordantScalar.Float64Scalar f:
                writer.WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(f.Value)));
                break;
            case ConcordantScalar.StringScalar s:
                writer.WriteUtf8String(s.Value);
                break;
            default:
                throw new InvalidOperationException("Unknown scalar kind.");
        }
    }

    private static bool TryReadOperation(ref NativeBinaryReader reader, out ConcordantOperation? op)
    {
        op = null;
        if (!reader.TryReadByte(out byte kind))
        {
            return false;
        }

        if (!TryReadCommon(ref reader, out OpId id, out ulong lamport, out OpId? lamportSource))
        {
            return false;
        }

        switch (kind)
        {
            case NativeWireFormat.OpRootDeclare:
                {
                    if (!reader.TryReadUtf8String(out string name)
                        || string.IsNullOrEmpty(name)
                        || !reader.TryReadByte(out byte rootKind)
                        || rootKind is not ((byte)RootKind.Map or (byte)RootKind.Array or (byte)RootKind.Text))
                    {
                        return false;
                    }

                    op = new ConcordantOperation.RootDeclare(name, (RootKind)rootKind)
                    {
                        Id = id,
                        Lamport = lamport,
                        LamportSource = lamportSource,
                    };
                    return true;
                }

            case NativeWireFormat.OpMapSet:
                {
                    if (!reader.TryReadContainer(out ContainerRef map)
                        || !reader.TryReadUtf8String(out string key)
                        || string.IsNullOrEmpty(key)
                        || !reader.TryReadContent(out ConcordantContent value))
                    {
                        return false;
                    }

                    op = new ConcordantOperation.MapSet(map, key, value)
                    {
                        Id = id,
                        Lamport = lamport,
                        LamportSource = lamportSource,
                    };
                    return true;
                }

            case NativeWireFormat.OpSeqInsert:
                {
                    if (!reader.TryReadContainer(out ContainerRef container)
                        || !reader.TryReadOptionalOpId(out OpId? left)
                        || !reader.TryReadOptionalOpId(out OpId? right)
                        || !reader.TryReadContent(out ConcordantContent content))
                    {
                        return false;
                    }

                    op = new ConcordantOperation.SeqInsert(container, left, right, content)
                    {
                        Id = id,
                        Lamport = lamport,
                        LamportSource = lamportSource,
                    };
                    return true;
                }

            case NativeWireFormat.OpSeqDelete:
                {
                    if (!reader.TryReadOpId(out OpId target))
                    {
                        return false;
                    }

                    op = new ConcordantOperation.SeqDelete(target)
                    {
                        Id = id,
                        Lamport = lamport,
                        LamportSource = lamportSource,
                    };
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryReadCommon(
        ref NativeBinaryReader reader,
        out OpId id,
        out ulong lamport,
        out OpId? lamportSource)
    {
        id = default;
        lamport = 0;
        lamportSource = null;
        return reader.TryReadOpId(out id)
            && reader.TryReadUInt64(out lamport)
            && reader.TryReadOptionalOpId(out lamportSource);
    }
}
