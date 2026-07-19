using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Concordant.Values;

namespace Concordant.Sync.Native;

/// <summary>Bounded little-endian binary writer for the native codec.</summary>
internal ref struct NativeBinaryWriter
{
    private readonly IBufferWriter<byte> _output;
    private int _written;

    public NativeBinaryWriter(IBufferWriter<byte> output)
    {
        _output = output;
        _written = 0;
    }

    public int BytesWritten => _written;

    public void WriteByte(byte value)
    {
        Span<byte> span = _output.GetSpan(1);
        span[0] = value;
        _output.Advance(1);
        _written += 1;
    }

    public void WriteUInt16(ushort value)
    {
        Span<byte> span = _output.GetSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        _output.Advance(2);
        _written += 2;
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> span = _output.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        _output.Advance(4);
        _written += 4;
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> span = _output.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        _output.Advance(8);
        _written += 8;
    }

    public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> span = _output.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        _output.Advance(bytes.Length);
        _written += bytes.Length;
    }

    public void WriteSessionId(SessionId session)
    {
        Span<byte> span = _output.GetSpan(16);
        session.WriteBytes(span);
        _output.Advance(16);
        _written += 16;
    }

    public void WriteOpId(OpId id)
    {
        WriteSessionId(id.Session);
        WriteUInt64(id.Clock);
    }

    public void WriteOptionalOpId(OpId? id)
    {
        if (id is null)
        {
            WriteByte(0);
            return;
        }

        WriteByte(1);
        WriteOpId(id.Value);
    }

    public void WriteUtf8String(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteUInt32(checked((uint)byteCount));
        Span<byte> span = _output.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        _output.Advance(byteCount);
        _written += byteCount;
    }
}

/// <summary>Bounded binary reader that never overruns the input span.</summary>
internal ref struct NativeBinaryReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly long _maxContentUtf16Length;
    private int _offset;

    public NativeBinaryReader(ReadOnlySpan<byte> data, long maxContentUtf16Length)
    {
        _data = data;
        _maxContentUtf16Length = maxContentUtf16Length;
        _offset = 0;
    }

    public int Offset => _offset;

    public int Remaining => _data.Length - _offset;

    public bool TryReadByte(out byte value)
    {
        if (Remaining < 1)
        {
            value = 0;
            return false;
        }

        value = _data[_offset++];
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        if (Remaining < 2)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_offset));
        _offset += 2;
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset));
        _offset += 4;
        return true;
    }

    public bool TryReadUInt64(out ulong value)
    {
        if (Remaining < 8)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_offset));
        _offset += 8;
        return true;
    }

    public bool TryReadInt64(out long value)
    {
        if (!TryReadUInt64(out ulong raw))
        {
            value = 0;
            return false;
        }

        value = unchecked((long)raw);
        return true;
    }

    public bool TryReadExact(int length, out ReadOnlySpan<byte> slice)
    {
        if (length < 0 || Remaining < length)
        {
            slice = default;
            return false;
        }

        slice = _data.Slice(_offset, length);
        _offset += length;
        return true;
    }

    public bool TryReadSessionId(out SessionId session)
    {
        if (!TryReadExact(16, out ReadOnlySpan<byte> bytes))
        {
            session = default;
            return false;
        }

        ulong hi = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        ulong lo = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8));
        session = new SessionId(hi, lo);
        return true;
    }

    public bool TryReadOpId(out OpId id)
    {
        if (!TryReadSessionId(out SessionId session) || !TryReadUInt64(out ulong clock) || clock == 0)
        {
            id = default;
            return false;
        }

        id = new OpId(session, clock);
        return true;
    }

    public bool TryReadOptionalOpId(out OpId? id)
    {
        if (!TryReadByte(out byte present))
        {
            id = null;
            return false;
        }

        if (present == 0)
        {
            id = null;
            return true;
        }

        if (present != 1 || !TryReadOpId(out OpId value))
        {
            id = null;
            return false;
        }

        id = value;
        return true;
    }

    public bool TryReadUtf8String(out string value)
    {
        if (!TryReadUInt32(out uint byteCount))
        {
            value = string.Empty;
            return false;
        }

        if (byteCount > (uint)Remaining)
        {
            value = string.Empty;
            return false;
        }

        if (!TryReadExact((int)byteCount, out ReadOnlySpan<byte> utf8))
        {
            value = string.Empty;
            return false;
        }

        try
        {
            value = Encoding.UTF8.GetString(utf8);
        }
        catch (ArgumentException)
        {
            value = string.Empty;
            return false;
        }

        // UTF-16 length bound (surrogate pairs counted as 2).
        if (value.Length > _maxContentUtf16Length)
        {
            value = string.Empty;
            return false;
        }

        return true;
    }

    public bool TryReadContainer(out ContainerRef container)
    {
        container = default;
        if (!TryReadByte(out byte tag))
        {
            return false;
        }

        if (tag == NativeWireFormat.ContainerRoot)
        {
            if (!TryReadUtf8String(out string name) || string.IsNullOrEmpty(name))
            {
                return false;
            }

            container = ContainerRef.Root(name);
            return true;
        }

        if (tag == NativeWireFormat.ContainerNested)
        {
            if (!TryReadOpId(out OpId id))
            {
                return false;
            }

            container = ContainerRef.Nested(id);
            return true;
        }

        return false;
    }

    public bool TryReadContent(out ConcordantContent content)
    {
        content = null!;
        if (!TryReadByte(out byte tag))
        {
            return false;
        }

        if (tag == NativeWireFormat.ContentScalar)
        {
            if (!TryReadScalar(out ConcordantScalar scalar))
            {
                return false;
            }

            content = ConcordantContent.Scalar(scalar);
            return true;
        }

        if (tag == NativeWireFormat.ContentNested)
        {
            if (!TryReadByte(out byte kindByte)
                || kindByte is not ((byte)RootKind.Map or (byte)RootKind.Array or (byte)RootKind.Text))
            {
                return false;
            }

            content = ConcordantContent.Nested((RootKind)kindByte);
            return true;
        }

        return false;
    }

    public bool TryReadScalar(out ConcordantScalar scalar)
    {
        scalar = null!;
        if (!TryReadByte(out byte kind))
        {
            return false;
        }

        switch ((ScalarKind)kind)
        {
            case ScalarKind.Null:
                scalar = ConcordantScalar.Null;
                return true;
            case ScalarKind.Bool:
                if (!TryReadByte(out byte b) || b > 1)
                {
                    return false;
                }

                scalar = ConcordantScalar.Bool(b != 0);
                return true;
            case ScalarKind.Int64:
                if (!TryReadInt64(out long i))
                {
                    return false;
                }

                scalar = ConcordantScalar.Int64(i);
                return true;
            case ScalarKind.Float64:
                if (!TryReadUInt64(out ulong bits))
                {
                    return false;
                }

                double d = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    return false;
                }

                // Reject non-canonical negative zero on the wire.
                if (d == 0.0 && unchecked((long)bits) < 0)
                {
                    return false;
                }

                scalar = ConcordantScalar.Float64(d);
                return true;
            case ScalarKind.String:
                if (!TryReadUtf8String(out string s))
                {
                    return false;
                }

                try
                {
                    scalar = ConcordantScalar.String(s);
                }
                catch (ArgumentException)
                {
                    return false;
                }

                return true;
            default:
                return false;
        }
    }
}
