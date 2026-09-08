using System.IO;
using System.Text.Json;

namespace HenHotspot;

public record PolicyDraft(decimal DownloadMbps = 3, decimal UploadMbps = 1, string Mode = "Bloquear dominios", string[]? Domains = null);

public sealed class ProfileStore
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenHotspot", "policy-draft.json");
    public PolicyDraft Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<PolicyDraft>(File.ReadAllText(path)) ?? new() : new();

    public void Save(PolicyDraft draft)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
