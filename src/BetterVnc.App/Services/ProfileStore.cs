using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterVnc.App.Models;

namespace BetterVnc.App.Services;

/// <summary>Persists connections and Unraid servers as JSON in %AppData%\BetterVNC with DPAPI-protected secrets.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%AppData%\BetterVNC, or the folder in BETTERVNC_PROFILE_DIR (for test runs that must not touch the real profiles).</summary>
    public static string Directory { get; } = Environment.GetEnvironmentVariable("BETTERVNC_PROFILE_DIR") is { Length: > 0 } dir
        ? Path.GetFullPath(dir)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterVNC");
    public static string FilePath { get; } = Path.Combine(Directory, "profiles.json");

    public ObservableCollection<ConnectionProfile> Connections { get; } = new();
    public ObservableCollection<UnraidServer> UnraidServers { get; } = new();

    public void Load()
    {
        Connections.Clear();
        UnraidServers.Clear();
        if (!File.Exists(FilePath)) return;
        try
        {
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
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not read {FilePath}:\n{ex.Message}", "BetterVNC", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
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

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
