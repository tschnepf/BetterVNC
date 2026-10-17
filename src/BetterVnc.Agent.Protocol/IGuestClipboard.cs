namespace BetterVnc.Agent.Protocol;

/// <summary>
/// Access to the clipboard of the machine the agent runs on. Implementations are OS specific; the server calls them
/// one at a time from a worker thread, so they may block (spawn processes, marshal to an STA thread, and so on).
/// </summary>
public interface IGuestClipboard
{
    /// <summary>"windows", "linux" or "macos".</summary>
    string OsName { get; }

    /// <summary>How often the server should ask for the change token.</summary>
    TimeSpan PollInterval { get; }

    /// <summary>
    /// A cheap value that changes whenever the clipboard content changes (a sequence number, or a hash of the content
    /// when the OS offers nothing better). Null when the clipboard is empty or unavailable.
    /// </summary>
    string? GetChangeToken();

    /// <summary>Formats currently available, using the <see cref="AgentFormat"/> names.</summary>
    IReadOnlyList<string> GetFormats();

    /// <summary>Text, HTML (UTF-8) or PNG bytes; null when the format is not on the clipboard.</summary>
    byte[]? GetData(string format);

    /// <summary>Absolute paths of the files on the clipboard; null when there are none.</summary>
    IReadOnlyList<string>? GetFilePaths();

    /// <summary>Replaces the clipboard with text, HTML or a PNG.</summary>
    void SetData(string format, byte[] data);

    /// <summary>Replaces the clipboard with a file list (the files already exist at the given absolute paths).</summary>
    void SetFiles(IReadOnlyList<string> paths);
}
