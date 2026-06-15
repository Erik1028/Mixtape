using System.Buffers.Binary;
using System.Text;

namespace iPodCommander;

/// <summary>
/// Little-endian primitive reads over an iTunesDB byte buffer at absolute offsets, with
/// bounds checking so a truncated/garbage file throws cleanly instead of reading wild memory.
/// All multi-byte values in the iTunesDB are little-endian.
/// </summary>
internal sealed class ChunkReader
{
    private readonly byte[] _buf;
    public int Length => _buf.Length;

    public ChunkReader(byte[] buffer) => _buf = buffer;

    private void Require(int offset, int count)
    {
        // Written as a subtraction so a near-int.MaxValue offset can't overflow the check
        // (offset + count would wrap negative and wrongly pass).
        if (offset < 0 || count < 0 || offset > _buf.Length - count)
            throw new InvalidDataException($"iTunesDB read out of range at {offset:X} (+{count}); file is {_buf.Length} bytes.");
    }

    public byte U8(int offset)
    {
        Require(offset, 1);
        return _buf[offset];
    }

    public ushort U16(int offset)
    {
        Require(offset, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(offset, 2));
    }

    public uint U32(int offset)
    {
        Require(offset, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(offset, 4));
    }

    public ulong U64(int offset)
    {
        Require(offset, 8);
        return BinaryPrimitives.ReadUInt64LittleEndian(_buf.AsSpan(offset, 8));
    }

    public int I32(int offset) => unchecked((int)U32(offset));

    /// <summary>The 4-char ASCII tag at the given offset (chunk preamble byte 0).</summary>
    public string Tag(int offset)
    {
        Require(offset, 4);
        return Encoding.ASCII.GetString(_buf, offset, 4);
    }

    /// <summary>A UTF-16LE string of <paramref name="byteLength"/> bytes (not null-terminated).</summary>
    public string Utf16(int offset, int byteLength)
    {
        if (byteLength <= 0) return string.Empty;
        Require(offset, byteLength);
        return Encoding.Unicode.GetString(_buf, offset, byteLength);
    }

    /// <summary>A UTF-8 string of <paramref name="byteLength"/> bytes (used by a few mhod types).</summary>
    public string Utf8(int offset, int byteLength)
    {
        if (byteLength <= 0) return string.Empty;
        Require(offset, byteLength);
        return Encoding.UTF8.GetString(_buf, offset, byteLength);
    }
}
