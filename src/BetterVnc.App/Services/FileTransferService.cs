using System.IO;
using System.Runtime.InteropServices;
using BetterVnc.App.Models;
using Renci.SshNet;

namespace BetterVnc.App.Services;

/// <summary>
/// Uploads files to the remote machine through the connection's configured side channel
/// (SFTP or an SMB share), since RFB itself carries no file data.
/// </summary>
public sealed class FileTransferService
{
    private readonly ConnectionProfile _profile;

    public FileTransferService(ConnectionProfile profile)
    {
        _profile = profile;
    }

    public bool IsConfigured => _profile.FileTransfer switch
    {
        FileTransferMode.Sftp => !string.IsNullOrEmpty(_profile.SftpHost ?? _profile.Host) && !string.IsNullOrEmpty(_profile.SftpUsername),
        FileTransferMode.Smb => !string.IsNullOrEmpty(_profile.SmbPath),
        _ => false,
    };

    public string Description => _profile.FileTransfer switch
    {
        FileTransferMode.Sftp => $"SFTP {_profile.SftpUsername}@{_profile.SftpHost ?? _profile.Host}:{_profile.RemoteDropFolder}",
        FileTransferMode.Smb => $"SMB {_profile.SmbPath}",
        _ => "not configured",
    };

    /// <summary>Uploads the given local files; returns the remote paths written.</summary>
    public Task<List<string>> UploadAsync(IReadOnlyList<string> localPaths, IProgress<(string file, double fraction)>? progress, CancellationToken ct)
    {
        return _profile.FileTransfer switch
        {
            FileTransferMode.Sftp => Task.Run(() => UploadSftp(localPaths, progress, ct), ct),
            FileTransferMode.Smb => Task.Run(() => UploadSmb(localPaths, progress, ct), ct),
            _ => throw new InvalidOperationException("File transfer is not configured for this connection."),
        };
    }

    private List<string> UploadSftp(IReadOnlyList<string> localPaths, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        var host = string.IsNullOrEmpty(_profile.SftpHost) ? _profile.Host : _profile.SftpHost!;
        var user = _profile.SftpUsername ?? "";
        var password = ProfileStore.Unprotect(_profile.SftpProtectedPassword);
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(_profile.SftpPrivateKeyPath) && File.Exists(_profile.SftpPrivateKeyPath))
        {
            var key = string.IsNullOrEmpty(password) ? new PrivateKeyFile(_profile.SftpPrivateKeyPath) : new PrivateKeyFile(_profile.SftpPrivateKeyPath, password);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        methods.Add(new PasswordAuthenticationMethod(user, password ?? ""));
        var info = new ConnectionInfo(host, _profile.SftpPort, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(10) };

        using var sftp = new SftpClient(info);
        sftp.Connect();
        var remoteDir = string.IsNullOrWhiteSpace(_profile.RemoteDropFolder) ? "." : _profile.RemoteDropFolder!.TrimEnd('/');
        if (remoteDir.StartsWith("~/")) remoteDir = sftp.WorkingDirectory.TrimEnd('/') + remoteDir[1..];
        if (remoteDir == "~") remoteDir = sftp.WorkingDirectory;
        if (remoteDir != "." && !sftp.Exists(remoteDir)) sftp.CreateDirectory(remoteDir);

        var written = new List<string>();
        foreach (var local in localPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(local))
            {
                foreach (var f in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(Path.GetDirectoryName(local) ?? local, f).Replace('\\', '/');
                    var remote = $"{remoteDir}/{rel}";
                    EnsureRemoteDirectory(sftp, remote[..remote.LastIndexOf('/')]);
                    UploadOne(sftp, f, remote, progress, ct);
                    written.Add(remote);
                }
            }
            else
            {
                var remote = $"{remoteDir}/{Path.GetFileName(local)}";
                UploadOne(sftp, local, remote, progress, ct);
                written.Add(remote);
            }
        }
        return written;
    }

    private static void EnsureRemoteDirectory(SftpClient sftp, string dir)
    {
        if (dir.Length == 0 || sftp.Exists(dir)) return;
        var parent = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
        if (parent.Length > 0 && parent != dir) EnsureRemoteDirectory(sftp, parent);
        sftp.CreateDirectory(dir);
    }

    private static void UploadOne(SftpClient sftp, string local, string remote, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        using var fs = File.OpenRead(local);
        long total = fs.Length;
        var name = Path.GetFileName(local);
        sftp.UploadFile(fs, remote, true, uploaded =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((name, total == 0 ? 1 : (double)uploaded / total));
        });
    }

    private List<string> UploadSmb(IReadOnlyList<string> localPaths, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        var target = _profile.SmbPath!.TrimEnd('\\');
        var password = ProfileStore.Unprotect(_profile.SmbProtectedPassword);
        if (!string.IsNullOrEmpty(_profile.SmbUsername))
            SmbSession.Connect(target, _profile.SmbUsername, password ?? "");
        Directory.CreateDirectory(target);
        var written = new List<string>();
        foreach (var local in localPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(local))
            {
                var destRoot = Path.Combine(target, Path.GetFileName(local));
                foreach (var f in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(destRoot, Path.GetRelativePath(local, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    CopyWithProgress(f, dest, progress, ct);
                    written.Add(dest);
                }
            }
            else
            {
                var dest = Path.Combine(target, Path.GetFileName(local));
                CopyWithProgress(local, dest, progress, ct);
                written.Add(dest);
            }
        }
        return written;
    }

    private static void CopyWithProgress(string src, string dest, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        using var input = File.OpenRead(src);
        using var output = File.Create(dest);
        var buffer = new byte[1 << 20];
        long total = input.Length, done = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, n);
            done += n;
            progress?.Report((Path.GetFileName(src), total == 0 ? 1 : (double)done / total));
        }
    }

    /// <summary>Establishes an SMB session with explicit credentials via WNetAddConnection2.</summary>
    private static class SmbSession
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NETRESOURCE
        {
            public int dwScope, dwType, dwDisplayType, dwUsage;
            public string? lpLocalName;
            public string lpRemoteName;
            public string? lpComment, lpProvider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, int flags);

        public static void Connect(string uncPath, string username, string password)
        {
            // Use the share root (\\server\share) as the resource name.
            var parts = uncPath.TrimStart('\\').Split('\\');
            if (parts.Length < 2) return;
            var share = $@"\\{parts[0]}\{parts[1]}";
            var res = new NETRESOURCE { dwType = 1, lpRemoteName = share };
            int rc = WNetAddConnection2(ref res, password, username, 0);
            // 1219 = credential conflict with an existing session; proceed with the existing one.
            if (rc != 0 && rc != 1219 && rc != 85)
                throw new IOException($"Could not connect to {share} (error {rc}).");
        }
    }
}
