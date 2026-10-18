using System.Buffers.Binary;
using System.Security;

namespace BetterVnc.Agent;

/// <summary>
/// macOS: the agent is installed as an application, "BetterVNC Agent.app", not as a bare program. macOS keeps its privacy
/// permissions (Screen Recording for the stream, Accessibility for scrolling) per application, and System Settings lists
/// an application by name after its first request, ready to be switched on; a bare program is recorded by its path and
/// never shows up there. <c>install</c> builds the bundle around the single-file agent and the capture helper, with an
/// Info.plist (menu bar only, no Dock icon) and an icon, and then signs it ad hoc (see <see cref="Installer"/>).
/// </summary>
public static class MacAppBundle
{
    public const string Name = "BetterVNC Agent.app";
    public const string DisplayName = "BetterVNC Agent";
    public const string BundleId = "com.bettervnc.agent";
    public const string ExecutableName = "bettervnc-agent";
    private const string IconName = "BetterVNC";

    public static string ExecutablePath(string bundle) => Path.Combine(bundle, "Contents", "MacOS", ExecutableName);

    /// <summary>The bundle a program sits in (…/Name.app/Contents/MacOS/program), or null for a bare program.</summary>
    public static string? EnclosingBundle(string? executable)
    {
        if (string.IsNullOrEmpty(executable)) return null;
        var macos = Path.GetDirectoryName(Path.GetFullPath(executable));
        var contents = Path.GetDirectoryName(macos);
        var bundle = Path.GetDirectoryName(contents);
        if (bundle == null || !string.Equals(Path.GetFileName(macos), "MacOS", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal)
            || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
        return bundle;
    }

    public static string InfoPlist(string version)
    {
        var v = SecurityElement.Escape(version);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleIdentifier</key><string>{BundleId}</string>
              <key>CFBundleName</key><string>{DisplayName}</string>
              <key>CFBundleDisplayName</key><string>{DisplayName}</string>
              <key>CFBundleExecutable</key><string>{ExecutableName}</string>
              <key>CFBundleIconFile</key><string>{IconName}</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
              <key>CFBundleShortVersionString</key><string>{v}</string>
              <key>CFBundleVersion</key><string>{v}</string>
              <key>LSMinimumSystemVersion</key><string>12.0</string>
              <key>LSUIElement</key><true/>
              <key>NSHighResolutionCapable</key><true/>
            </dict>
            </plist>

            """;
    }

    /// <summary>An .icns file holding one 256-pixel PNG ("ic08"), which is enough for Finder and System Settings' lists.</summary>
    public static byte[] Icns(byte[] png256)
    {
        var icns = new byte[16 + png256.Length];
        "icns"u8.CopyTo(icns);
        BinaryPrimitives.WriteUInt32BigEndian(icns.AsSpan(4), (uint)icns.Length);
        "ic08"u8.CopyTo(icns.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(icns.AsSpan(12), (uint)(8 + png256.Length));
        png256.CopyTo(icns.AsSpan(16));
        return icns;
    }

    /// <summary>
    /// Writes a complete, unsigned bundle at <paramref name="bundle"/> (which must not exist yet): the agent, the capture
    /// helper when there is one, Info.plist and the icon. Returns the path of the agent inside it.
    /// </summary>
    public static string Write(string bundle, string agent, string? helper, string version, byte[]? iconPng)
    {
        var macos = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macos);
        var exe = ExecutablePath(bundle);
        File.Copy(agent, exe);
        if (helper != null) File.Copy(helper, Path.Combine(macos, Path.GetFileName(helper)));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), InfoPlist(version));
        if (iconPng != null)
        {
            var resources = Path.Combine(bundle, "Contents", "Resources");
            Directory.CreateDirectory(resources);
            File.WriteAllBytes(Path.Combine(resources, IconName + ".icns"), Icns(iconPng));
        }
        return exe;
    }

    /// <summary>The application icon embedded in the agent (a 256-pixel PNG); null when the build has none.</summary>
    public static byte[]? EmbeddedIcon()
    {
        using var s = typeof(MacAppBundle).Assembly.GetManifestResourceStream("BetterVNC.png");
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
