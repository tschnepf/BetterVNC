using System.IO.Compression;
using BetterVnc.Protocol.Decoders;
using Xunit;

namespace BetterVnc.Protocol.Tests;

/// <summary>
/// Regression tests for the persistent inflate stream behind the Zlib and Tight decoders.
/// Real servers (QEMU, TigerVNC) end every rectangle with a zlib sync flush, so each chunk
/// carries a trailer that yields no pixels; the inflater has to swallow it or it rejects the
/// next chunk with "Old input was not completely processed".
/// </summary>
public class ZlibInflaterTests
{
    private static byte[] Payload(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(rng.Next(4) * 40 + i % 7);
        return data;
    }

    [Theory]
    [InlineData(48)]       // tiny: fits one deflate block
    [InlineData(4096)]
    [InlineData(40000)]    // larger than the 32 KiB inflate window
    [InlineData(200000)]
    public void Exact_size_inflate_accepts_consecutive_sync_flushed_chunks(int size)
    {
        var enc = new FakeRfbServer.ZlibStreamEncoder();
        var inflater = new ZlibInflater();
        for (int i = 0; i < 3; i++)
        {
            var expected = Payload(size, i);
            var compressed = enc.Compress(expected);
            var output = new byte[size];
            inflater.Inflate(compressed, 0, compressed.Length, output, 0, size);
            Assert.Equal(expected, output);
        }
    }

    [Fact]
    public void Exact_size_inflate_rejects_a_chunk_with_surplus_data()
    {
        var enc = new FakeRfbServer.ZlibStreamEncoder();
        var inflater = new ZlibInflater();
        var compressed = enc.Compress(Payload(100, 1));
        var ex = Assert.Throws<VncException>(() => inflater.Inflate(compressed, 0, compressed.Length, new byte[90], 0, 90));
        Assert.Contains("more data", ex.Message);
    }

    [Fact]
    public void Exact_size_inflate_reports_a_short_chunk()
    {
        var enc = new FakeRfbServer.ZlibStreamEncoder();
        var inflater = new ZlibInflater();
        var compressed = enc.Compress(Payload(100, 1));
        var ex = Assert.Throws<VncException>(() => inflater.Inflate(compressed, 0, compressed.Length, new byte[120], 0, 120));
        Assert.Contains("too short", ex.Message);
    }

    [Fact]
    public void InflateAll_accepts_consecutive_sync_flushed_chunks()
    {
        var enc = new FakeRfbServer.ZlibStreamEncoder();
        var inflater = new ZlibInflater();
        for (int i = 0; i < 3; i++)
        {
            var expected = Payload(5000, i);
            var compressed = enc.Compress(expected);
            Assert.Equal(expected, inflater.InflateAll(compressed, 0, compressed.Length));
        }
    }
}
