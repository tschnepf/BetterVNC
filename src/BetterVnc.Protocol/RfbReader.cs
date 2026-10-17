using System.Buffers.Binary;
using System.Text;

namespace BetterVnc.Protocol;

/// <summary>
/// Blocking big-endian reader over the RFB transport stream with an internal buffer.
/// All reads block until the requested number of bytes is available or the stream ends,
/// in which case an <see cref="EndOfStreamException"/> is thrown.
/// </summary>
public sealed class RfbReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _pos;
    private int _len;

    public long TotalBytesRead { get; private set; }

    public RfbReader(Stream stream, int bufferSize = 256 * 1024)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
    }

    private void Fill(int needed)
    {
        // Ensure at least `needed` bytes are buffered (needed <= buffer size).
        if (_len - _pos >= needed) return;
        if (_pos > 0)
        {
            Buffer.BlockCopy(_buffer, _pos, _buffer, 0, _len - _pos);
            _len -= _pos;
            _pos = 0;
        }
        while (_len < needed)
        {
            int n = _stream.Read(_buffer, _len, _buffer.Length - _len);
            if (n <= 0) throw new EndOfStreamException("Connection closed by the server.");
            _len += n;
            TotalBytesRead += n;
        }
    }

    public byte ReadU8()
    {
        Fill(1);
        return _buffer[_pos++];
    }

    public sbyte ReadS8() => (sbyte)ReadU8();

    public ushort ReadU16()
    {
        Fill(2);
        var v = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_pos, 2));
        _pos += 2;
        return v;
    }

    public short ReadS16() => (short)ReadU16();

    public uint ReadU32()
    {
        Fill(4);
        var v = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_pos, 4));
        _pos += 4;
        return v;
    }

    public int ReadS32() => (int)ReadU32();

    public void Skip(int count)
    {
        while (count > 0)
        {
            int chunk = Math.Min(count, _buffer.Length);
            Fill(chunk);
            _pos += chunk;
            count -= chunk;
        }
    }

    public void ReadBytes(Span<byte> destination)
    {
        // Serve what is buffered first, then read the remainder directly for large payloads.
        int fromBuffer = Math.Min(destination.Length, _len - _pos);
        if (fromBuffer > 0)
        {
            _buffer.AsSpan(_pos, fromBuffer).CopyTo(destination);
            _pos += fromBuffer;
            destination = destination[fromBuffer..];
        }
        if (destination.Length == 0) return;
        if (destination.Length < _buffer.Length / 2)
        {
            Fill(destination.Length);
            _buffer.AsSpan(_pos, destination.Length).CopyTo(destination);
            _pos += destination.Length;
            return;
        }
        while (destination.Length > 0)
        {
            int n = _stream.Read(destination);
            if (n <= 0) throw new EndOfStreamException("Connection closed by the server.");
            TotalBytesRead += n;
            destination = destination[n..];
        }
    }

    public byte[] ReadBytes(int count)
    {
        var data = new byte[count];
        ReadBytes(data);
        return data;
    }

    public string ReadString(int length, Encoding? encoding = null)
    {
        var bytes = ReadBytes(length);
        return (encoding ?? Encoding.Latin1).GetString(bytes);
    }
}
