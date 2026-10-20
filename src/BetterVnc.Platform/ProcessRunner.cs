using System.Diagnostics;

namespace BetterVnc.Platform;

/// <summary>Runs clipboard helper tools (xclip, wl-copy, pbpaste, osascript, ...) with binary stdin/stdout.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, byte[] Stdout, string Stderr)
    {
        public bool Ok => ExitCode == 0;
        public string Text => System.Text.Encoding.UTF8.GetString(Stdout);
    }

    public static bool Exists(string tool)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir, tool))) return true; } catch { }
        }
        return false;
    }

    /// <summary>Runs a tool and captures stdout. Use for readers (the process must exit on its own).</summary>
    public static Result Run(string file, IEnumerable<string> args, byte[]? stdin = null, IDictionary<string, string>? env = null, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        var stdoutTask = ReadAllAsync(p.StandardOutput.BaseStream);
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (stdin != null)
        {
            try { p.StandardInput.BaseStream.Write(stdin); } catch (IOException) { }
            p.StandardInput.Close();
        }
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{file} did not finish within {timeoutMs} ms.");
        }
        return new Result(p.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Runs a tool that forks into the background to serve the clipboard (xclip -i, wl-copy). Stdout and stderr
    /// are not redirected, otherwise the forked child would keep the pipes open and we would wait forever.
    /// </summary>
    public static void RunDetached(string file, IEnumerable<string> args, byte[]? stdin = null, IDictionary<string, string>? env = null, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        if (stdin != null)
        {
            try { p.StandardInput.BaseStream.Write(stdin); } catch (IOException) { }
            p.StandardInput.Close();
        }
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{file} did not finish within {timeoutMs} ms.");
        }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} exited with code {p.ExitCode}.");
    }

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }
}
