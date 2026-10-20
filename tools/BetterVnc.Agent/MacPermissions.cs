using System.Runtime.InteropServices;
using BetterVnc.Platform;

namespace BetterVnc.Agent;

/// <summary>
/// The two macOS privacy permissions the agent needs, both granted to BetterVNC Agent (see <see cref="MacAppBundle"/>):
/// Screen Recording for the stream, which the capture helper records on the agent's behalf, and Accessibility for
/// posting scroll events. macOS only. The menu bar's permission wizard is built on this.
/// </summary>
public static class MacPermissions
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, long numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);

    /// <summary>System Settings, Privacy &amp; Security, Screen &amp; System Audio Recording (Screen Recording before macOS 15).</summary>
    public const string ScreenRecordingPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture";
    /// <summary>System Settings, Privacy &amp; Security, Accessibility.</summary>
    public const string AccessibilityPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    /// <summary>
    /// Whether macOS lets the agent record the screen. Asked in a new capture helper process when there is one, because a
    /// process that has already asked may keep its first answer after the switch is turned on.
    /// </summary>
    public static bool ScreenRecording
    {
        get
        {
            var helper = Video.MacScreenSource.FindHelper();
            if (helper != null)
            {
                try
                {
                    var r = ProcessRunner.Run(helper, new[] { "--permissions" }, timeoutMs: 5000);
                    if (r.Ok && r.Text.Contains("screenRecording=")) return r.Text.Contains("screenRecording=granted");
                }
                catch (Exception) { } // an older helper without --permissions: ask here instead
            }
            return CGPreflightScreenCaptureAccess();
        }
    }

    /// <summary>Whether macOS lets the agent post input events (scrolling).</summary>
    public static bool Accessibility => AXIsProcessTrusted();

    /// <summary>Asks macOS for Screen Recording: the first time it shows its prompt and lists BetterVNC Agent, switched off.</summary>
    public static void RequestScreenRecording() => CGRequestScreenCaptureAccess();

    /// <summary>Asks macOS for Accessibility, which shows its prompt and lists BetterVNC Agent; true when it is already allowed.</summary>
    public static bool RequestAccessibility()
    {
        if (AXIsProcessTrusted()) return true;
        IntPtr options = IntPtr.Zero;
        try
        {
            var appServices = NativeLibrary.Load(ApplicationServices);
            var cf = NativeLibrary.Load(CoreFoundation);
            var key = Marshal.ReadIntPtr(NativeLibrary.GetExport(appServices, "kAXTrustedCheckOptionPrompt"));
            var yes = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
            options = CFDictionaryCreate(IntPtr.Zero, new[] { key }, new[] { yes }, 1,
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
            return AXIsProcessTrustedWithOptions(options);
        }
        finally { if (options != IntPtr.Zero) CFRelease(options); }
    }

    /// <summary>Opens one of the panes above in System Settings.</summary>
    public static void OpenSettings(string pane) => ProcessRunner.Run("open", new[] { pane });
}
