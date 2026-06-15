using System.Buffers.Binary;
using System.Text;

namespace iPodCommander;

/// <summary>
/// Little-endian primitive writes into a growable buffer, with back-patch helpers so a
/// container's total-length / child-count fields can be filled in after its children are
/// serialized. Milestone 1 uses this only to build a synthetic iTunesDB for the self-test;
/// the full database serializer (ITunesDbWriter) arrives in Milestone 2.
/// </summary>
internal sealed class ChunkWriter
{
    private byte[] _buf = new byte[1024];
    private int _len;

    public int Position => _len;
    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        int cap = _buf.Length;
        while (cap < _len + extra) cap *= 2;
        Array.Resize(ref _buf, cap);
    }

    public void Ascii(string tag)
    {
        if (tag.Length != 4) throw new ArgumentException("chunk tag must be 4 chars", nameof(tag));
        Ensure(4);
        Encoding.ASCII.GetBytes(tag, 0, 4, _buf, _len);
        _len += 4;
    }

    public void U8(byte v) { Ensure(1); _buf[_len++] = v; }

    public void U16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_len, 2), v); _len += 2; }

    public void U32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_len, 4), v); _len += 4; }

    public void U64(ulong v) { Ensure(8); BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(_len, 8), v); _len += 8; }

    public void Bytes(ReadOnlySpan<byte> data) { Ensure(data.Length); data.CopyTo(_buf.AsSpan(_len)); _len += data.Length; }

    public void Utf16(string s)
    {
        byte[] data = Encoding.Unicode.GetBytes(s);
        Bytes(data);
    }

    /// <summary>Append <paramref name="count"/> zero bytes (padding a chunk header out to its declared length).</summary>
    public void Zero(int count)
    {
        if (count <= 0) return;
        Ensure(count);
        _len += count; // backing array starts zeroed and Resize preserves zeros
    }

    /// <summary>Overwrite a previously-written u32 at an absolute position (length/count back-patch).</summary>
    public void PatchU32(int position, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(position, 4), v);
}
