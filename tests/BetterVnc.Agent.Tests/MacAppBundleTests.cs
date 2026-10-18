using System.Buffers.Binary;
using System.Xml.Linq;
using BetterVnc.Agent;
using Xunit;

namespace BetterVnc.Agent.Tests;

/// <summary>The layout of BetterVNC Agent.app that "install" builds on macOS (signing happens there, with codesign).</summary>
public class MacAppBundleTests
{
    [Fact]
    public void Info_plist_names_the_app_and_keeps_it_out_of_the_dock()
    {
        using var reader = System.Xml.XmlReader.Create(new StringReader(MacAppBundle.InfoPlist("0.0.4")),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore });
        var plist = XDocument.Load(reader);
        var dict = plist.Root!.Element("dict")!.Elements().ToList();
        string? Value(string key)
        {
            int i = dict.FindIndex(e => e.Name == "key" && e.Value == key);
            return i < 0 ? null : dict[i + 1].Name == "true" ? "true" : dict[i + 1].Value;
        }
        Assert.Equal("com.bettervnc.agent", Value("CFBundleIdentifier"));
        Assert.Equal("BetterVNC Agent", Value("CFBundleName"));
        Assert.Equal("bettervnc-agent", Value("CFBundleExecutable"));
        Assert.Equal("APPL", Value("CFBundlePackageType"));
        Assert.Equal("0.0.4", Value("CFBundleShortVersionString"));
        Assert.Equal("BetterVNC", Value("CFBundleIconFile"));
        Assert.Equal("true", Value("LSUIElement"));
    }

    [Fact]
    public void Icns_wraps_the_png_with_big_endian_lengths()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3 };
        var icns = MacAppBundle.Icns(png);
        Assert.Equal("icns"u8.ToArray(), icns[..4]);
        Assert.Equal((uint)icns.Length, BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(4)));
        Assert.Equal("ic08"u8.ToArray(), icns[8..12]);
        Assert.Equal((uint)(8 + png.Length), BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(12)));
        Assert.Equal(png, icns[16..]);
    }

    [Fact]
    public void Enclosing_bundle_is_found_only_for_a_program_in_Contents_MacOS()
    {
        var root = Path.Combine(Path.GetTempPath(), "x");
        var bundle = Path.Combine(root, "BetterVNC Agent.app");
        Assert.Equal(Path.GetFullPath(bundle), MacAppBundle.EnclosingBundle(MacAppBundle.ExecutablePath(bundle)));
        Assert.Null(MacAppBundle.EnclosingBundle(Path.Combine(root, "bettervnc-agent")));
        Assert.Null(MacAppBundle.EnclosingBundle(Path.Combine(root, "Contents", "MacOS", "bettervnc-agent")));
        Assert.Null(MacAppBundle.EnclosingBundle(null));
    }

    [Fact]
    public void Write_lays_out_the_bundle_with_agent_helper_plist_and_icon()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bettervnc-bundle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var agent = Path.Combine(dir, "bettervnc-agent");
            var helper = Path.Combine(dir, "bettervnc-capture");
            File.WriteAllText(agent, "agent");
            File.WriteAllText(helper, "helper");
            var bundle = Path.Combine(dir, "out", MacAppBundle.Name);
            var exe = MacAppBundle.Write(bundle, agent, helper, "1.2.3", new byte[] { 1, 2, 3 });

            Assert.Equal(MacAppBundle.ExecutablePath(bundle), exe);
            Assert.Equal("agent", File.ReadAllText(exe));
            Assert.Equal("helper", File.ReadAllText(Path.Combine(bundle, "Contents", "MacOS", "bettervnc-capture")));
            Assert.Contains("<string>1.2.3</string>", File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist")));
            Assert.Equal(19, new FileInfo(Path.Combine(bundle, "Contents", "Resources", "BetterVNC.icns")).Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
