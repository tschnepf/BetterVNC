namespace BetterVnc.Agent;

/// <summary>
/// One serving agent per config: a second copy (a second logon task, a double click on the program) would only fail to
/// listen and put a second icon in the tray. The lock is a file next to the config, opened exclusively (flock on Linux, a
/// sharing lock on Windows), so the operating system lets go of it when the process ends, however it ends. The macOS
/// agent takes the same lock.
/// </summary>
public static class SingleInstance
{
    /// <summary>bettervnc-agent.json → bettervnc-agent.lock in the same folder.</summary>
    public static string LockPath(string configPath) => Path.ChangeExtension(Path.GetFullPath(configPath), ".lock");

    /// <summary>The lock, held until it is disposed or the process ends; null when another process holds it.</summary>
    public static IDisposable? TryAcquire(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
}
