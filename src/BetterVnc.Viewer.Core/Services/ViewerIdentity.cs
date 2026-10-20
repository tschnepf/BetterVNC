using System.Text.Json;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Viewer.Core.Services;

/// <summary>
/// Who this viewer installation is to guest agents: a random id created once per user plus a display name (the machine
/// name) shown in the agent's approval dialog. Stored in viewer.json next to the profiles (%AppData%\BetterVNC); see
/// <see cref="ProfileStore.Identity"/>. The id is public; the secrets are the per-connection pair keys in the profiles.
/// </summary>
public sealed class ViewerIdentity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Reads <c>viewer.json</c> in <paramref name="directory"/>, creating it with a new id when missing or unreadable.</summary>
    public static ViewerIdentity Load(string directory)
    {
        var filePath = Path.Combine(directory, "viewer.json");
        ViewerIdentity? id = null;
        try { if (File.Exists(filePath)) id = JsonSerializer.Deserialize<ViewerIdentity>(File.ReadAllText(filePath)); }
        catch { }
        if (id == null || string.IsNullOrWhiteSpace(id.Id))
        {
            id = new ViewerIdentity { Id = AgentPairing.NewViewerId(), Name = Environment.MachineName };
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(filePath, JsonSerializer.Serialize(id, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(id.Name)) id.Name = Environment.MachineName;
        return id;
    }
}
