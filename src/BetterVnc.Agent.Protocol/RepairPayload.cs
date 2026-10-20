using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace BetterVnc.Agent.Protocol;

/// <summary>A rectangle of the picture in a <see cref="AgentMessageType.VideoRepair"/>, in frame pixels.</summary>
public readonly record struct RepairRect(int X, int Y, int Width, int Height);

/// <summary>
/// The payload of <see cref="AgentMessageType.VideoRepair"/>: the exact pixels of some rectangles of the picture.
/// <code>
/// u8 version (1) | u16 BE count | count × ( u16 BE x, y, width, height | u32 BE length | length bytes )
/// </code>
/// Each rectangle's bytes are a zlib stream (RFC 1950) of its pixels as B, G, R, row by row from the top. Rectangles are
/// at most <see cref="MaxRows"/> rows tall, so both sides compress and inflate them in parallel; on a LAN, compressing
/// fast matters more than compressing small.
/// </summary>
public static class RepairPayload
{
    public const byte Version = 1;
    /// <summary>Taller rectangles are sent as bands of this many rows.</summary>
    public const int MaxRows = 64;
    private const int HeaderBytes = 3, RectHeaderBytes = 12;

    /// <summary>
    /// The rectangles of a BGRA (or BGRX) picture of <paramref name="width"/>×<paramref name="height"/> pixels with
    /// <paramref name="stride"/> bytes per row, compressed on up to <paramref name="maxThreads"/> threads. Rectangles
    /// are clipped to the picture.
    /// </summary>
    public static byte[] Encode(byte[] bgra, int stride, int width, int height, IEnumerable<RepairRect> rects, int maxThreads = 1)
    {
        var bands = Bands(rects, width, height);
        if (bands.Count > ushort.MaxValue) throw new ArgumentException("Too many rectangles for one repair.", nameof(rects));
        var parts = new byte[bands.Count][];
        Parallel.For(0, bands.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxThreads) }, i =>
        {
            var r = bands[i];
            int rowBytes = r.Width * 3;
            byte[] bgr = ArrayPool<byte>.Shared.Rent(rowBytes * r.Height);
            try
            {
                for (int row = 0; row < r.Height; row++)
                {
                    var from = bgra.AsSpan((r.Y + row) * stride + r.X * 4, r.Width * 4);
                    var to = bgr.AsSpan(row * rowBytes, rowBytes);
                    for (int x = 0, s = 0, d = 0; x < r.Width; x++, s += 4, d += 3)
                    {
                        to[d] = from[s];
                        to[d + 1] = from[s + 1];
                        to[d + 2] = from[s + 2];
                    }
                }
                using var ms = new MemoryStream(rowBytes * r.Height / 8 + 64);
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    z.Write(bgr, 0, rowBytes * r.Height);
                parts[i] = ms.ToArray();
            }
            finally { ArrayPool<byte>.Shared.Return(bgr); }
        });

        var payload = new byte[HeaderBytes + bands.Count * RectHeaderBytes + parts.Sum(p => p.Length)];
        payload[0] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)bands.Count);
        int at = HeaderBytes;
        for (int i = 0; i < bands.Count; i++)
        {
            var r = bands[i];
            var head = payload.AsSpan(at, RectHeaderBytes);
            BinaryPrimitives.WriteUInt16BigEndian(head, (ushort)r.X);
            BinaryPrimitives.WriteUInt16BigEndian(head[2..], (ushort)r.Y);
            BinaryPrimitives.WriteUInt16BigEndian(head[4..], (ushort)r.Width);
            BinaryPrimitives.WriteUInt16BigEndian(head[6..], (ushort)r.Height);
            BinaryPrimitives.WriteUInt32BigEndian(head[8..], (uint)parts[i].Length);
            parts[i].CopyTo(payload.AsSpan(at + RectHeaderBytes));
            at += RectHeaderBytes + parts[i].Length;
        }
        return payload;
    }

    /// <summary>
    /// Paints a repair into a BGRX picture of <paramref name="width"/>×<paramref name="height"/> pixels (width*4 bytes per
    /// row; the X byte becomes 255), inflating on up to <paramref name="maxThreads"/> threads. Returns the rectangles it
    /// painted. Throws <see cref="AgentProtocolException"/> for a payload that does not fit the picture or its own header.
    /// </summary>
    public static RepairRect[] Decode(ReadOnlyMemory<byte> payload, byte[] bgrx, int width, int height, int maxThreads = 1)
    {
        if (bgrx.Length < (long)width * height * 4) throw new ArgumentException("Picture buffer too small.", nameof(bgrx));
        var span = payload.Span;
        if (span.Length < HeaderBytes || span[0] != Version) throw new AgentProtocolException("Unknown repair format.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(span[1..]);
        var rects = new RepairRect[count];
        var data = new (int Offset, int Length)[count];
        int at = HeaderBytes;
        for (int i = 0; i < count; i++)
        {
            if (span.Length - at < RectHeaderBytes) throw new AgentProtocolException("Repair cut short.");
            var head = span.Slice(at, RectHeaderBytes);
            var r = new RepairRect(BinaryPrimitives.ReadUInt16BigEndian(head), BinaryPrimitives.ReadUInt16BigEndian(head[2..]),
                BinaryPrimitives.ReadUInt16BigEndian(head[4..]), BinaryPrimitives.ReadUInt16BigEndian(head[6..]));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(head[8..]);
            if (r.Width == 0 || r.Height == 0 || r.X + r.Width > width || r.Y + r.Height > height)
                throw new AgentProtocolException($"Repair rectangle {r.X},{r.Y} {r.Width}x{r.Height} lies outside the {width}x{height} picture.");
            if (length > (uint)(span.Length - at - RectHeaderBytes)) throw new AgentProtocolException("Repair cut short.");
            rects[i] = r;
            data[i] = (at + RectHeaderBytes, (int)length);
            at += RectHeaderBytes + (int)length;
        }

        var failure = default(Exception);
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxThreads) }, (i, state) =>
        {
            var r = rects[i];
            int rowBytes = r.Width * 3, total = rowBytes * r.Height;
            byte[] bgr = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                using (var z = new ZLibStream(AsStream(payload.Slice(data[i].Offset, data[i].Length)), CompressionMode.Decompress))
                {
                    int got = 0, n;
                    while (got < total && (n = z.Read(bgr, got, total - got)) > 0) got += n;
                    if (got != total || z.ReadByte() >= 0) throw new AgentProtocolException($"Repair rectangle {r.X},{r.Y} {r.Width}x{r.Height} holds the wrong number of pixels.");
                }
                for (int row = 0; row < r.Height; row++)
                {
                    var from = bgr.AsSpan(row * rowBytes, rowBytes);
                    var to = bgrx.AsSpan(((r.Y + row) * width + r.X) * 4, r.Width * 4);
                    for (int x = 0, s = 0, d = 0; x < r.Width; x++, s += 3, d += 4)
                    {
                        to[d] = from[s];
                        to[d + 1] = from[s + 1];
                        to[d + 2] = from[s + 2];
                        to[d + 3] = 255;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or AgentProtocolException)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                state.Stop();
            }
            finally { ArrayPool<byte>.Shared.Return(bgr); }
        });
        if (failure is AgentProtocolException protocol) throw protocol;
        if (failure != null) throw new AgentProtocolException("Repair data is not valid zlib: " + failure.Message, failure);
        return rects;
    }

    /// <summary>The rectangles clipped to the picture and cut into bands of at most <see cref="MaxRows"/> rows.</summary>
    private static List<RepairRect> Bands(IEnumerable<RepairRect> rects, int width, int height)
    {
        var bands = new List<RepairRect>();
        foreach (var r in rects)
        {
            int x = Math.Max(0, r.X), y = Math.Max(0, r.Y);
            int right = Math.Min(width, r.X + r.Width), bottom = Math.Min(height, r.Y + r.Height);
            if (right <= x || bottom <= y) continue;
            for (int top = y; top < bottom; top += MaxRows)
                bands.Add(new RepairRect(x, top, right - x, Math.Min(MaxRows, bottom - top)));
        }
        return bands;
    }

    private static Stream AsStream(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Array != null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);
}
