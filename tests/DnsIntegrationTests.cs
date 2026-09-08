using HenHotspot;
using System.IO;

public static class DnsIntegrationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string suffix = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63);
        string[] names = Enumerable.Range(0, 128).Select(i => i.ToString("D3") + new string('d', 56) + "." + suffix).ToArray();
        using var stream = new MemoryStream();
        var policy = new DomainPolicy(true, names);
        await GuardWire.SendAsync(stream, new DnsFilterCommand("apply", true, policy.Domains), CancellationToken.None);
        stream.Position = 0;
        var restored = await GuardWire.ReceiveAsync<DnsFilterCommand>(stream, CancellationToken.None);
        check(restored.Domains!.SequenceEqual(names), "DNS IPC carries all 128 maximum-length policy domains without truncation");

        string directory = Path.Combine(Path.GetTempPath(), "HenHotspot.DnsStoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var at = DateTimeOffset.UtcNow;
            using (var store = new ActivityStore(directory))
            {
                store.Record(new("dns-only", at, at, null, "192.168.137.2", "Phone", "02:11:22:33:44:55",
                    "example.com", "DNS", "Blocked", 0, 0, 0), store.Generation);
                await store.FlushAsync();
            }
            using (var store = new ActivityStore(directory))
            {
                var row = store.Query(new(at.AddSeconds(-1), at.AddSeconds(1))).Entries.Single();
                check(row.Protocol == "DNS" && row.Outcome == "Blocked" && row.StartedAt == row.EndedAt &&
                    row.DurationSeconds == 0 && row.UploadBytes == 0 && row.DownloadBytes == 0,
                    "DNS history survives reopening as a query without invented browsing duration or traffic");
            }
        }
        finally
        {
            string full = Path.GetFullPath(directory);
            string temp = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("HenHotspot.DnsStoreTests-"))
                throw new InvalidOperationException("Unexpected temporary test path");
            Directory.Delete(full, true);
        }
    }
}
