using System.IO;
using System.Text.Json;

namespace HenHotspot;

public sealed class PreferencesStore
{
    private readonly string path;
    public PreferencesStore(string? directory = null) => path = Path.Combine(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenHotspot"), "preferences.json");

    public string LoadLanguage()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return "es";
            return L10n.NormalizeLanguage(JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path))?.Language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return "es"; }
    }

    public void SaveLanguage(string language)
    {
        if (!L10n.Languages.Contains(language)) throw new ArgumentException("Unsupported language.", nameof(language));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preferences(language)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record Preferences(string Language);
}
