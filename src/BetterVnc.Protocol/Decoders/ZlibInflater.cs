using System.Buffers;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace BetterVnc.Protocol.Decoders;

/// <summary>
/// A persistent zlib inflate stream. RFB encodings (Zlib, Tight, ZRLE) keep one zlib stream open
/// for the whole connection and flush it after every rectangle, so the inflater state has to
/// survive between messages. SharpZipLib's <see cref="Inflater"/> exposes exactly that API.
/// </summary>
public sealed class ZlibInflater
{
    private readonly Inflater _inflater = new(noHeader: false);

    public void Reset() => _inflater.Reset();

    /// <summary>Inflates <paramref name="input"/> until exactly <paramref name="output"/>.Length bytes were produced.</summary>
    public void Inflate(byte[] input, int inputOffset, int inputLength, byte[] output, int outputOffset, int outputLength)
    {
        _inflater.SetInput(input, inputOffset, inputLength);
        int produced = 0;
        while (produced < outputLength)
        {
            int n = _inflater.Inflate(output, outputOffset + produced, outputLength - produced);
            if (n == 0)
            {
                if (_inflater.IsFinished)
                    throw new VncException("zlib stream ended unexpectedly");
                if (_inflater.IsNeedingInput)
                    throw new VncException($"zlib data too short: wanted {outputLength} bytes, got {produced}");
                if (_inflater.IsNeedingDictionary)
                    throw new VncException("zlib stream requires a preset dictionary");
            }
            produced += n;
        }
    }

    /// <summary>Inflates everything that <paramref name="input"/> yields; used when the output size is unknown (ZRLE, clipboard).</summary>
    public byte[] InflateAll(byte[] input, int inputOffset, int inputLength, int sizeHint = 65536)
    {
        _inflater.SetInput(input, inputOffset, inputLength);
        var result = new byte[Math.Max(sizeHint, 1024)];
        int produced = 0;
        while (true)
        {
            if (produced == result.Length) Array.Resize(ref result, result.Length * 2);
            int n = _inflater.Inflate(result, produced, result.Length - produced);
            if (n == 0)
            {
                if (_inflater.IsFinished || _inflater.IsNeedingInput) break;
                if (_inflater.IsNeedingDictionary) throw new VncException("zlib stream requires a preset dictionary");
            }
            produced += n;
        }
        Array.Resize(ref result, produced);
        return result;
    }
}

/// <summary>Reads big-endian values from an in-memory byte array (used for inflated ZRLE data).</summary>
public ref struct ByteCursor
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ByteCursor(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position => _pos;
    public int Remaining => _data.Length - _pos;

    public byte ReadU8()
    {
        if (_pos >= _data.Length) throw new VncException("ZRLE tile data truncated");
        return _data[_pos++];
    }

    public ReadOnlySpan<byte> ReadSpan(int count)
    {
        if (_pos + count > _data.Length) throw new VncException("ZRLE tile data truncated");
        var s = _data.Slice(_pos, count);
        _pos += count;
        return s;
    }
}

internal static class Pools
{
    public static byte[] Rent(int size) => ArrayPool<byte>.Shared.Rent(size);
    public static void Return(byte[] arr) => ArrayPool<byte>.Shared.Return(arr);
}
