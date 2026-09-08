using HenHotspot;
using System.Security.Cryptography;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine($"PASS {name}"); }
void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { Check(true, name); return; } throw new Exception($"Accepted invalid input: {name}"); }
Validation.Network("Hen Hotspot", "test-pass-123");
var onUi = HotspotUiState.From("On", "Enabled", false, false);
Check(onUi.Label == "Encendido" && onUi.CanUseClients && onUi.CanApplyRules && !onUi.CanEditNetwork, "On state exposes online actions and locks network editing");
var offUi = HotspotUiState.From("Off", "Enabled", false, false);
Check(offUi.Label == "Apagado" && !offUi.CanUseClients && !offUi.CanApplyRules && offUi.CanEditNetwork, "Off state locks online actions and permits configuration");
foreach (var ui in new[] { HotspotUiState.From("InTransition", "Enabled", false, false), HotspotUiState.From("On", "Enabled", true, false), HotspotUiState.From(null, null, false, false), HotspotUiState.From("On", "Enabled", false, true) })
    Check(!ui.CanToggle && !ui.CanEditNetwork && !ui.CanApplyRules && !ui.CanUseClients, "Uncertain or busy state disables network actions");
Check(Validation.Domain("*.YouTube.COM.") == "youtube.com", "Normalize domain and wildcard");
Check(Validation.Domain("mañana.com") == "xn--maana-pta.com", "IDN domains");
Check(DomainEntryInput.Normalize(" https://es.wikipedia.org/wiki/Internet?ref=test#Historia ") == "es.wikipedia.org", "Pasted article URL keeps its exact host without path or query");
Check(DomainEntryInput.Normalize("HTTP://EXAMPLE.COM:8080/page") == "example.com", "Web URL host is normalized without its port");
Check(DomainEntryInput.Normalize("*.WIKIPEDIA.org.") == "wikipedia.org", "Editor keeps existing wildcard domain behavior");
Check(DomainEntryInput.Normalize("https://mañana.com/articulo") == "xn--maana-pta.com", "Pasted international URL is normalized");
foreach (var input in new[] { "ftp://example.com", "https://user:pass@example.com", "https://1.1.1.1", "https://localhost", "https://", "example.com/path" })
    Reject(() => DomainEntryInput.Normalize(input), "Editor rejects unsupported or ambiguous input: " + input);
Check(Validation.Speed("0,5") == 0.5m, "Spanish decimal speed");
Reject(() => Validation.Network(new string('é', 17), "test12345"), "SSID UTF-8 byte limit");
Reject(() => Validation.Network("Hen", "short"), "Short password");
Reject(() => Validation.Network("Hen", "áéíóú123"), "Unsupported password characters");
Reject(() => Validation.Network("Hen\nWiFi", "test12345"), "SSID control characters");
foreach (var invalid in new[] { "https://youtube.com", "youtube.com/path", "1.2.3.4", "localhost", "a..com", "-bad.com", "x.com:53", "user@x.com", "a.*.com" })
    Reject(() => Validation.Domain(invalid), $"Invalid domain {invalid}");
foreach (var invalid in new[] { "0", "-1", "10001", "NaN", "Infinity", "3 Mbps" })
    Reject(() => Validation.Speed(invalid), $"Invalid speed {invalid}");

if (args.Contains("--integration"))
{
    var service = new HotspotService();
    var before = await service.ReadAsync();
    Check(before.Error is null && before.Capability == "Enabled", "Windows hotspot API available");
    Check(before.State == "Off", "Hotspot initially off; no active users to interrupt");
    var profile = NetworkInformation.GetInternetConnectionProfile();
    var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
    var original = manager.GetCurrentAccessPointConfiguration();
    bool configurationChanged = false;
    try
    {
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await service.ConfigureAsync("Hen Hotspot - Prueba", password, "Automática");
        configurationChanged = true;
        Check((await service.ReadAsync()).Ssid == "Hen Hotspot - Prueba", "Write and read SSID configuration");
        await service.StartAsync();
        var active = await service.ReadAsync();
        Check(active.State == "On", "Hotspot starts on integrated Wi-Fi");
        Check(active.Connectivity == "InternetAccess", "Windows still reports host internet access");
        try { await service.ConfigureAsync("Invalid active change", password, "Automática"); throw new Exception("Changed active hotspot"); }
        catch (InvalidOperationException) { Check(true, "Reject configuration while hotspot active"); }
        await service.StopAsync();
        Check((await service.ReadAsync()).State == "Off", "Hotspot stops");
    }
    finally
    {
        if (configurationChanged)
        {
            if ((await service.ReadAsync()).State != "Off") await service.StopAsync();
            await manager.ConfigureAccessPointAsync(original);
            Check((await service.ReadAsync()).Ssid == before.Ssid, "Original hotspot configuration restored");
        }
    }
}
await ActivityStoreTests.Run(Check);
await DeviceAccessStoreTests.Run(Check);
await DeviceGuardTests.Run(Check);
await DnsPacketTests.Run(Check);
await DnsIntegrationTests.Run(Check);
if (args.Contains("--internet"))
{
    using var handler = new HttpClientHandler { UseProxy = false };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    var result = await client.GetStringAsync("https://example.com/");
    Check(result.Contains("Example Domain"), "Laptop direct HTTPS navigation remains available with normal certificate validation");
}
Console.WriteLine($"{passed} checks passed. Phone internet test requires a physical client.");
