using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterVnc.Platform;
using BetterVnc.Viewer.Core.Models;

namespace BetterVnc.Viewer.Core.Services;

/// <summary>
/// Persists connections and Unraid servers as JSON in a folder (%AppData%\BetterVNC by default), with the secrets in them
/// protected by the OS's secret store.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _identityLock = new();
    private ViewerIdentity? _identity;

    /// <param name="directory">Where profiles.json and viewer.json live; <see cref="DefaultDirectory"/> when null.</param>
    public ProfileStore(ISecretStore secrets, string? directory = null)
    {
        Secrets = secrets;
        Directory = directory ?? DefaultDirectory;
        FilePath = Path.Combine(Directory, "profiles.json");
    }

    /// <summary>%AppData%\BetterVNC on Windows; the same folder under the user's application data elsewhere.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterVNC");

    public string Directory { get; }
    public string FilePath { get; }
    public ISecretStore Secrets { get; }

    public ObservableCollection<ConnectionProfile> Connections { get; } = new();
    public ObservableCollection<UnraidServer> UnraidServers { get; } = new();

    /// <summary>Who this viewer is to guest agents, from viewer.json next to the profiles (created on first use).</summary>
    public ViewerIdentity Identity
    {
        get
        {
            lock (_identityLock) return _identity ??= ViewerIdentity.Load(Directory);
        }
    }

    /// <summary>Reads the profiles. Throws when the file exists but cannot be read; the store is then empty.</summary>
    public void Load()
    {
        Connections.Clear();
        UnraidServers.Clear();
        if (!File.Exists(FilePath)) return;
        var doc = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(FilePath), JsonOptions);
        if (doc == null) return;
        if (doc.Version < 2)
        {
            // Version 1 stored only the Tight JPEG quality; the picture mode is the same choice with the stream modes added.
            foreach (var c in doc.Connections) c.PictureMode = PictureModes.FromLegacy(c.JpegQuality);
        }
        foreach (var c in doc.Connections) Connections.Add(c);
        foreach (var s in doc.UnraidServers) UnraidServers.Add(s);
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var doc = new ProfileDocument { Connections = Connections.ToList(), UnraidServers = UnraidServers.ToList() };
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public UnraidServer? FindServer(string? id) => id == null ? null : UnraidServers.FirstOrDefault(s => s.Id == id);

    public string? Protect(string? plain) => Secrets.Protect(plain);

    public string? Unprotect(string? protectedValue) => Secrets.Unprotect(protectedValue);
}
