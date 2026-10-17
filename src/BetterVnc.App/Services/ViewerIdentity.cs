using System.IO;
using System.Text.Json;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.App.Services;

/// <summary>
/// Who this viewer installation is to guest agents: a random id created once per Windows user plus a display name
/// (the machine name) shown in the agent's approval dialog. Stored in %AppData%\BetterVNC\viewer.json. The id is
/// public; the secrets are the per-connection pair keys in the profiles.
/// </summary>
public sealed class ViewerIdentity
{
    private static readonly object Lock = new();
    private static ViewerIdentity? _current;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public static string FilePath { get; } = Path.Combine(ProfileStore.Directory, "viewer.json");

    public static ViewerIdentity Current
    {
        get
        {
            lock (Lock)
            {
                if (_current != null) return _current;
                ViewerIdentity? id = null;
                try { if (File.Exists(FilePath)) id = JsonSerializer.Deserialize<ViewerIdentity>(File.ReadAllText(FilePath)); }
                catch { }
                if (id == null || string.IsNullOrWhiteSpace(id.Id))
                {
                    id = new ViewerIdentity { Id = AgentPairing.NewViewerId(), Name = Environment.MachineName };
                    try
                    {
                        Directory.CreateDirectory(ProfileStore.Directory);
                        File.WriteAllText(FilePath, JsonSerializer.Serialize(id, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(id.Name)) id.Name = Environment.MachineName;
                return _current = id;
            }
        }
    }
}
