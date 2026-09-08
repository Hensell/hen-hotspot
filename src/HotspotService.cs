using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace HenHotspot;

public record AdapterInfo(string Name, string Description, string Status);
public record ClientInfo(string Mac, string Name, string[]? IpAddresses = null);
public record ProxyBinding(IPAddress Address, string Subnet, string InterfaceName);
public record HotspotStatus(string State, string Capability, string Connection, string Connectivity,
    uint MaxClients, uint ClientCount, string Ssid, string Band, List<ClientInfo> Clients,
    List<AdapterInfo> Adapters, string? Error);

public sealed class HotspotService
{
    private static ConnectionProfile GetProfile() => NetworkInformation.GetInternetConnectionProfile()
        ?? throw new InvalidOperationException("Windows no detecta una conexión de internet activa. Conecta la laptop al Wi-Fi y vuelve a intentar.");

    private static NetworkOperatorTetheringManager GetManager() =>
        NetworkOperatorTetheringManager.CreateFromConnectionProfile(GetProfile());

    public Task<HotspotStatus> ReadAsync() => Task.Run(() =>
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.NetworkInterfaceType != NetworkInterfaceType.Loopback && x.OperationalStatus != OperationalStatus.NotPresent &&
                !x.Name.Contains("-0000", StringComparison.Ordinal) && !x.Description.Contains("Tunneling", StringComparison.OrdinalIgnoreCase))
            .Select(x => new AdapterInfo(x.Name, x.Description, x.OperationalStatus.ToString())).ToList();
        string connection = "Sin conexión", connectivity = "None", capability = "Unknown";
        try
        {
            var profile = GetProfile();
            connection = profile.ProfileName;
            connectivity = profile.GetNetworkConnectivityLevel().ToString();
            capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile).ToString();
            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            var config = manager.GetCurrentAccessPointConfiguration();
            var clients = manager.GetTetheringClients().Select(c => new ClientInfo(c.MacAddress,
                string.Join(", ", c.HostNames.Select(h => h.DisplayName)),
                c.HostNames.Where(h => h.Type == Windows.Networking.HostNameType.Ipv4).Select(h => h.CanonicalName).ToArray())).ToList();
            return new HotspotStatus(manager.TetheringOperationalState.ToString(), capability, connection, connectivity,
                manager.MaxClientCount, manager.ClientCount, config.Ssid, config.Band.ToString(), clients, adapters, null);
        }
        catch (Exception ex)
        {
            return new HotspotStatus("Unknown", capability, connection, connectivity, 0, 0, "", "Auto", [], adapters, Friendly(ex));
        }
    });

    public async Task StartAsync()
    {
        var manager = GetManager();
        var result = await manager.StartTetheringAsync();
        CheckResult(result);
    }

    public static ProxyBinding GetProxyBinding()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) && !n.Name.Contains("-0000", StringComparison.Ordinal))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => new { n.Name, a.Address, a.PrefixLength })).ToList();
        if (candidates.Count != 1) throw new InvalidOperationException("No se pudo identificar una única interfaz IPv4 del hotspot. Enciéndelo y actualiza el diagnóstico.");
        var c = candidates[0]; var bytes = c.Address.GetAddressBytes();
        for (int i = 0; i < 4; i++) bytes[i] &= (byte)(0xff << Math.Clamp(8 - (c.PrefixLength - i * 8), 0, 8));
        return new(c.Address, $"{new IPAddress(bytes)}/{c.PrefixLength}", c.Name);
    }

    public async Task StopAsync()
    {
        var result = await GetManager().StopTetheringAsync();
        CheckResult(result);
    }

    public async Task ConfigureAsync(string ssid, string password, string band)
    {
        Validation.Network(ssid, password);
        var manager = GetManager();
        if (manager.TetheringOperationalState != TetheringOperationalState.Off)
            throw new InvalidOperationException("Apaga el hotspot antes de cambiar el nombre, la contraseña o la banda.");
        var config = manager.GetCurrentAccessPointConfiguration();
        config.Ssid = ssid.Trim();
        config.Passphrase = password;
        config.Band = band switch { "2.4 GHz" => TetheringWiFiBand.TwoPointFourGigahertz, "5 GHz" => TetheringWiFiBand.FiveGigahertz, _ => TetheringWiFiBand.Auto };
        await manager.ConfigureAccessPointAsync(config);
    }

    private static void CheckResult(NetworkOperatorTetheringOperationResult result)
    {
        if (result.Status != TetheringOperationStatus.Success)
            throw new InvalidOperationException($"Windows no pudo completar la operación ({result.Status}). {result.AdditionalErrorMessage}");
    }

    public static string Friendly(Exception ex) => ex is UnauthorizedAccessException
        ? "Windows denegó el acceso al hotspot. Abre la configuración de Windows para comprobar permisos y disponibilidad."
        : ex.Message;
}
