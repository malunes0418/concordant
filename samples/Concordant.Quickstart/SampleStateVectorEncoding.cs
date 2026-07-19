using System.Buffers.Binary;

namespace Concordant.Quickstart;

/// <summary>
/// Sample-local opaque encoding of a document state vector for checkpoint metadata.
/// Persistence abstractions treat these bytes as opaque; Core does not yet expose a dedicated
/// EncodeStateVector helper, so the sample owns this layout.
/// </summary>
internal static class SampleStateVectorEncoding
{
    // Layout: count:u32 LE, then sorted entries of (session:16 bytes BE, clock:u64 LE).

    public static byte[] Encode(IReadOnlyDictionary<SessionId, ulong> stateVector)
    {
        ArgumentNullException.ThrowIfNull(stateVector);
        List<KeyValuePair<SessionId, ulong>> ordered = stateVector.OrderBy(static kv => kv.Key).ToList();
        byte[] bytes = new byte[4 + (ordered.Count * (16 + 8))];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), (uint)ordered.Count);
        int offset = 4;
        foreach ((SessionId session, ulong clock) in ordered)
        {
            session.WriteBytes(bytes.AsSpan(offset, 16));
            offset += 16;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), clock);
            offset += 8;
        }

        return bytes;
    }
}
