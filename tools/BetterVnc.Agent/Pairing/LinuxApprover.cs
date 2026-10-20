using BetterVnc.Agent.Protocol;
using BetterVnc.Platform;

namespace BetterVnc.Agent.Pairing;

/// <summary>Approval dialog on Linux through zenity, kdialog or xmessage, whichever is installed.</summary>
public sealed class LinuxApprover : IPairingApprover
{
    private readonly Action<string> _log;

    public LinuxApprover(Action<string> log) => _log = log;

    public Task<bool> ApproveAsync(string viewerName, string viewerId, string peerAddress, CancellationToken ct) => Task.Run(() =>
    {
        const int seconds = 90;
        var text = $"BetterVNC on {viewerName} ({peerAddress}) asks to use this computer's clipboard (images, files, text) through the guest agent.\n\nAllow it?";
        try
        {
            if (ProcessRunner.Exists("zenity"))
            {
                var r = ProcessRunner.Run("zenity", new[] { "--question", "--title=BetterVNC guest agent", $"--text={text}", "--ok-label=Allow", "--cancel-label=Deny", $"--timeout={seconds}" }, timeoutMs: (seconds + 10) * 1000);
                return r.ExitCode == 0;
            }
            if (ProcessRunner.Exists("kdialog"))
            {
                var r = ProcessRunner.Run("kdialog", new[] { "--title", "BetterVNC guest agent", "--yes-label", "Allow", "--no-label", "Deny", "--yesno", text }, timeoutMs: (seconds + 10) * 1000);
                return r.ExitCode == 0;
            }
            if (ProcessRunner.Exists("xmessage"))
            {
                var r = ProcessRunner.Run("xmessage", new[] { "-center", "-buttons", "Allow:0,Deny:1", "-default", "Deny", "-timeout", seconds.ToString(), text }, timeoutMs: (seconds + 10) * 1000);
                return r.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            _log("Approval dialog failed: " + ex.Message);
            return false;
        }
        _log("No dialog tool found (install zenity, kdialog or xmessage); pairing denied. Set a token instead: bettervnc-agent token generate");
        return false;
    }, ct);
}
