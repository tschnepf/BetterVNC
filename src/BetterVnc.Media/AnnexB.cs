namespace BetterVnc.Media;

/// <summary>Minimal reader of an H.264 Annex B byte stream: finds NAL units and their types.</summary>
public static class AnnexB
{
    public const int NalSlice = 1, NalIdr = 5, NalSei = 6, NalSps = 7, NalPps = 8, NalAud = 9;
    public const int NalFiller = 12;

    /// <summary>One NAL unit: <see cref="Start"/> is the offset of its start code, <see cref="Payload"/> the offset of the NAL header byte.</summary>
    public readonly record struct Nal(int Start, int Payload, int End, int Type)
    {
        public int Length => End - Start;
    }

    public static List<Nal> Parse(ReadOnlySpan<byte> data)
    {
        var nals = new List<Nal>();
        int i = 0, n = data.Length;
        int start = -1, payload = -1;
        while (i + 2 < n)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                int codeStart = i;
                if (codeStart > 0 && data[codeStart - 1] == 0) codeStart--; // 4-byte start code
                if (start >= 0) nals.Add(new Nal(start, payload, codeStart, data[payload] & 0x1F));
                start = codeStart;
                payload = i + 3;
                i += 3;
                continue;
            }
            i++;
        }
        if (start >= 0 && payload < n) nals.Add(new Nal(start, payload, n, data[payload] & 0x1F));
        return nals;
    }

    /// <summary>Says which of the NAL types that matter for key-frame handling are present.</summary>
    public static void Inspect(ReadOnlySpan<byte> data, out bool idr, out bool sps, out bool pps)
    {
        idr = sps = pps = false;
        foreach (var nal in Parse(data))
        {
            switch (nal.Type)
            {
                case NalIdr: idr = true; break;
                case NalSps: sps = true; break;
                case NalPps: pps = true; break;
            }
        }
    }

    /// <summary>Returns the stream without NAL units of <paramref name="type"/> (the input itself when there are none).</summary>
    public static byte[] Strip(byte[] data, int type)
    {
        var nals = Parse(data);
        if (!nals.Any(n => n.Type == type)) return data;
        using var ms = new MemoryStream(data.Length);
        foreach (var nal in nals)
            if (nal.Type != type) ms.Write(data, nal.Start, nal.Length);
        return ms.ToArray();
    }

    /// <summary>The SPS and PPS NAL units of a frame (with their start codes), or null when either is missing.</summary>
    public static byte[]? ExtractParameterSets(ReadOnlySpan<byte> data)
    {
        var nals = Parse(data);
        if (!nals.Any(n => n.Type == NalSps) || !nals.Any(n => n.Type == NalPps)) return null;
        using var ms = new MemoryStream();
        foreach (var nal in nals)
        {
            if (nal.Type is NalSps or NalPps)
            {
                ms.Write(new byte[] { 0, 0, 0, 1 });
                ms.Write(data.Slice(nal.Payload, nal.End - nal.Payload));
            }
        }
        return ms.ToArray();
    }
}
