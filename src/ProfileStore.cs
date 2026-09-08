using System.IO;
using System.Text.Json;

namespace HenHotspot;

public record PolicyDraft(decimal DownloadMbps = 3, decimal UploadMbps = 1, string Mode = "blocklist", string[]? Domains = null)
{
    // Read profiles from earlier versions without tying behavior to UI language.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllowOnly => Mode is "allowlist" or "Permitir solo estos dominios";
}

public sealed class ProfileStore
{
    private const int MaxFileBytes = 128 * 1024;
    private readonly string path;

    public ProfileStore(string? directory = null) => path = Path.Combine(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenHotspot"), "policy-draft.json");

    public PolicyDraft Load()
    {
        if (!File.Exists(path)) return Normalize(new());
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("The saved domain profile exceeds its size limit.");
        return Normalize(JsonSerializer.Deserialize<PolicyDraft>(stream) ?? new());
    }

    public void Save(PolicyDraft draft)
    {
        var normalized = Normalize(draft);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, normalized, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static PolicyDraft Normalize(PolicyDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        string[] domains = draft.Domains ?? [];
        if (domains.Length > 128) throw new ArgumentException(L10n.T("UpTo128DomainsPerList"));
        if (domains.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException(L10n.T("TheListContainsAnEmptyOrOverlyLongDomain"));
        return draft with
        {
            Mode = draft.AllowOnly ? "allowlist" : "blocklist",
            Domains = domains.Select(Validation.Domain).Distinct(StringComparer.Ordinal).ToArray()
        };
    }
}
