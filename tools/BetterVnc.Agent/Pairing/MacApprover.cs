using BetterVnc.Agent.Protocol;
using BetterVnc.Platform;

namespace BetterVnc.Agent.Pairing;

/// <summary>Approval dialog on macOS through osascript's <c>display dialog</c>, which works from a LaunchAgent in the user's session.</summary>
public sealed class MacApprover : IPairingApprover
{
    public Task<bool> ApproveAsync(string viewerName, string viewerId, string peerAddress, CancellationToken ct) => Task.Run(() =>
    {
        int seconds = 90;
        var message = $"BetterVNC on {viewerName} ({peerAddress}) asks to use this Mac's clipboard (images, files, text) and smooth scrolling through the guest agent.\n\nAllow it? You can remove it later from the BetterVNC menu bar icon.";
        var script =
            "set msg to system attribute \"BVNC_MSG\"\n" +
            $"display dialog msg with title \"BetterVNC guest agent\" buttons {{\"Deny\", \"Allow\"}} default button \"Allow\" cancel button \"Deny\" with icon caution giving up after {seconds}\n";
        var r = ProcessRunner.Run("osascript", new[] { "-e", script }, env: new Dictionary<string, string> { ["BVNC_MSG"] = message }, timeoutMs: (seconds + 10) * 1000);
        if (!r.Ok) return false; // Deny (exit code 1, "User canceled") or an error
        var text = r.Text;
        return text.Contains("button returned:Allow") && !text.Contains("gave up:true");
    }, ct);
}
