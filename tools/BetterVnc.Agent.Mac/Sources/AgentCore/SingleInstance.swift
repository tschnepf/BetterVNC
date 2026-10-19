import Darwin
import Foundation

/// One serving agent per config. "Quit & Reopen" after Screen Recording is switched on relaunches the app through
/// LaunchServices while launchd may start the LaunchAgent again too; the second copy would only fail to listen and put a
/// second item in the menu bar. The lock is a file next to the config held with flock, which the system lets go of when
/// the process ends, however it ends. The C# agent took the same lock.
public final class SingleInstance {
    private let fd: Int32

    private init(fd: Int32) { self.fd = fd }

    deinit { close(fd) }

    /// bettervnc-agent.json → bettervnc-agent.lock in the same folder.
    public static func lockPath(forConfig configPath: String) -> String {
        (configPath as NSString).deletingPathExtension + ".lock"
    }

    /// The lock, held as long as the returned object lives; nil when another process holds it.
    public static func acquire(_ lockPath: String) -> SingleInstance? {
        try? FileManager.default.createDirectory(atPath: (lockPath as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        let fd = open(lockPath, O_RDWR | O_CREAT | O_CLOEXEC, 0o600)
        if fd < 0 { return nil }
        if flock(fd, LOCK_EX | LOCK_NB) != 0 {
            close(fd)
            return nil
        }
        return SingleInstance(fd: fd)
    }
}
