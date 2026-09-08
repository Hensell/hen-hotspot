using HenHotspot;

public static class ProfileStoreTests
{
    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "HenHotspot.ProfileTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ProfileStore(directory);
            check(store.Load() is { AllowOnly: false, Domains.Length: 0 }, "A missing domain profile starts with an empty blocklist");
            store.Save(new(Mode: "Permitir solo estos dominios", Domains: ["WIKIPEDIA.org", "wikipedia.org", "upload.wikimedia.org"]));
            check(store.Load() is { AllowOnly: true, Domains.Length: 2 } restored && restored.Domains[0] == "wikipedia.org",
                "Saved profiles normalize and deduplicate domains while preserving legacy allowlists");
            string path = Path.Combine(directory, "policy-draft.json");
            string previous = File.ReadAllText(path);
            bool rejected = false;
            try { store.Save(new(Domains: Enumerable.Repeat("example.com", 129).ToArray())); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == previous && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "An invalid draft cannot replace the saved profile or leave temporary files");
            store.Save(new(Mode: "allowlist", Domains: []));
            check(store.Load() is { AllowOnly: true, Domains.Length: 0 }, "An incomplete allowlist can still be saved as a draft");
            File.WriteAllText(path, new string(' ', 128 * 1024 + 1));
            bool oversized = false;
            try { store.Load(); } catch (InvalidDataException) { oversized = true; }
            check(oversized, "Oversized profile files are rejected before deserialization");
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("HenHotspot.ProfileTests-", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
