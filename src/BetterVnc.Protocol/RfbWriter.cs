using System.Buffers.Binary;

namespace BetterVnc.Protocol;

/// <summary>
/// Builds a single RFB client message in memory and writes it atomically to the stream.
/// Writes from multiple threads are serialised by the <see cref="VncClient"/> send lock.
/// </summary>
public sealed class RfbMessageBuilder
{
    private byte[] _buf = new byte[256];
    private int _len;

    public int Length => _len;

    public ReadOnlySpan<byte> Data => _buf.AsSpan(0, _len);

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        int size = Math.Max(_buf.Length * 2, _len + extra);
        Array.Resize(ref _buf, size);
    }

    public RfbMessageBuilder Reset()
    {
        _len = 0;
        return this;
    }

    public RfbMessageBuilder U8(byte v)
    {
        Ensure(1);
        _buf[_len++] = v;
        return this;
    }

    public RfbMessageBuilder U8(int v) => U8((byte)v);

    public RfbMessageBuilder Pad(int count)
    {
        Ensure(count);
        Array.Clear(_buf, _len, count);
        _len += count;
        return this;
    }

    public RfbMessageBuilder U16(ushort v)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buf.AsSpan(_len), v);
        _len += 2;
        return this;
    }

    public RfbMessageBuilder U16(int v) => U16((ushort)v);

    public RfbMessageBuilder U32(uint v)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buf.AsSpan(_len), v);
        _len += 4;
        return this;
    }

    public RfbMessageBuilder S32(int v) => U32((uint)v);

    public RfbMessageBuilder Bytes(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
        return this;
    }

    public void WriteTo(Stream stream)
    {
        stream.Write(_buf, 0, _len);
    }
}
