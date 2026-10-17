namespace BetterVnc.Agent.Protocol;

/// <summary>
/// Packs files into a single payload (entries listed in the header, contents concatenated in order) and unpacks
/// them into a directory. Directories are flattened to relative names using '/'.
/// </summary>
public static class FileBundle
{
    public static (List<AgentFileEntry> Entries, byte[] Payload) Pack(IEnumerable<string> paths, long maxBytes = AgentFraming.DefaultMaxPayloadBytes)
    {
        var files = new List<(string name, string path)>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var root = Path.GetFileName(Path.TrimEndingDirectorySeparator(p));
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(p, f).Replace('\\', '/');
                    files.Add(($"{root}/{rel}", f));
                }
            }
            else if (File.Exists(p))
            {
                files.Add((Path.GetFileName(p), p));
            }
            else
            {
                throw new FileNotFoundException("File not found.", p);
            }
        }

        long total = 0;
        foreach (var (_, path) in files)
        {
            total += new FileInfo(path).Length;
            if (total > maxBytes) throw new InvalidOperationException($"The selected files exceed the transfer limit of {maxBytes / (1024 * 1024)} MB.");
        }

        var entries = new List<AgentFileEntry>();
        var payload = new byte[total];
        int offset = 0;
        foreach (var (name, path) in files)
        {
            using var fs = File.OpenRead(path);
            int size = 0, n;
            while (offset + size < payload.Length && (n = fs.Read(payload, offset + size, payload.Length - offset - size)) > 0)
                size += n;
            entries.Add(new AgentFileEntry { Name = name, Size = size });
            offset += size;
        }
        if (offset != payload.Length) Array.Resize(ref payload, offset); // a file shrank while we read it
        return (entries, payload);
    }

    /// <summary>Writes the bundle into <paramref name="targetDir"/> and returns the top-level paths created (files or directories).</summary>
    public static List<string> Unpack(IReadOnlyList<AgentFileEntry> entries, ReadOnlyMemory<byte> payload, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var fullTarget = Path.GetFullPath(targetDir);
        var topLevel = new List<string>();
        long offset = 0;
        foreach (var e in entries)
        {
            var name = SanitizeName(e.Name);
            if (e.Size < 0 || offset + e.Size > payload.Length) throw new AgentProtocolException("File bundle is shorter than its entries describe.");
            var dest = Path.GetFullPath(Path.Combine(fullTarget, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(fullTarget + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new AgentProtocolException($"File entry '{e.Name}' escapes the target directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var fs = File.Create(dest))
                fs.Write(payload.Span.Slice((int)offset, (int)e.Size));
            offset += e.Size;

            var first = name.Contains('/') ? name[..name.IndexOf('/')] : name;
            var top = Path.Combine(fullTarget, first);
            if (!topLevel.Contains(top)) topLevel.Add(top);
        }
        return topLevel;
    }

    /// <summary>Rejects absolute paths, drive letters and '..' segments; collapses separators.</summary>
    public static string SanitizeName(string name)
    {
        var n = name.Replace('\\', '/');
        if (n.StartsWith('/')) throw new AgentProtocolException($"Absolute path '{name}' in bundle.");
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new AgentProtocolException("Empty file name in bundle.");
        foreach (var part in parts)
        {
            if (part == "." || part == ".." || part.Contains(':') || part.IndexOfAny(InvalidChars) >= 0)
                throw new AgentProtocolException($"Invalid file name segment '{part}' in bundle.");
        }
        return string.Join('/', parts);
    }

    // The Windows set, so a bundle produced on any OS unpacks on any other.
    private static readonly char[] InvalidChars =
    {
        '"', '<', '>', '|', '\0', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007', '\u0008', '\u0009', '\u000a',
        '\u000b', '\u000c', '\u000d', '\u000e', '\u000f', '\u0010', '\u0011', '\u0012', '\u0013', '\u0014', '\u0015', '\u0016',
        '\u0017', '\u0018', '\u0019', '\u001a', '\u001b', '\u001c', '\u001d', '\u001e', '\u001f', ':', '*', '?', '\\', '/',
    };
}
