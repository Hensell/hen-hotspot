using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HenHotspot;

public sealed record GuardInterface(string Id, uint Index, ulong Luid, string Name, string Address, int PrefixLength);
public sealed record GuardPlan(GuardInterface Network, string[] ApprovedAddresses);

public static class DeviceGuardPolicy
{
    public static GuardInterface FindInterface()
    {
        var binding = HotspotService.GetProxyBinding();
        var adapter = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Name == binding.InterfaceName &&
            n.OperationalStatus == OperationalStatus.Up && n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) &&
            n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(binding.Address)));
        uint index = checked((uint)adapter.GetIPProperties().GetIPv4Properties().Index);
        if (index == 0 || NativeWfp.ConvertInterfaceIndexToLuid(index, out ulong luid) != 0)
            throw new InvalidOperationException(L10n.T("CouldNotIdentifyTheHotspotInterfaceForAccessControl"));
        int prefix = adapter.GetIPProperties().UnicastAddresses.Single(a => a.Address.Equals(binding.Address)).PrefixLength;
        if (prefix is < 16 or > 30) throw new InvalidOperationException(L10n.T("TheHotspotSubnetIsNotSupportedByAccessControl"));
        return new(adapter.Id, index, luid, adapter.Name, binding.Address.ToString(), prefix);
    }

    public static GuardPlan Build(GuardInterface network, IEnumerable<ClientInfo> clients, IEnumerable<string> approvedMacs)
    {
        if (network.Index == 0 || network.Luid == 0 || network.PrefixLength is < 16 or > 30 ||
            !IPAddress.TryParse(network.Address, out var localAddress) || localAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException(L10n.T("InvalidHotspotInterface"));
        var approved = approvedMacs.Select(DeviceAccessStore.NormalizeMac).ToHashSet(StringComparer.Ordinal);
        if (approved.Count > 128) throw new ArgumentException(L10n.T("UpTo128AuthorizedDevices"));
        uint local = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(network.Address).GetAddressBytes());
        uint mask = uint.MaxValue << (32 - network.PrefixLength);
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var client in clients)
        {
            string mac;
            try { mac = DeviceAccessStore.NormalizeMac(client.Mac); } catch { mac = "invalid"; }
            foreach (var address in client.IpAddresses ?? [])
            {
                if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) continue;
                uint value = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
                if ((value & mask) != (local & mask) || value == local || value == (local & mask) || value == (local | ~mask)) continue;
                string normalized = ip.ToString();
                if (!owners.TryGetValue(normalized, out var set)) owners[normalized] = set = [];
                set.Add(mac);
            }
        }
        // Ambiguous ownership never grants access. IPv6 forwarding stays closed in this version.
        return new(network, owners.Where(x => x.Value.Count == 1 && approved.Contains(x.Value.Single()))
            .Select(x => x.Key).Order(StringComparer.Ordinal).ToArray());
    }
}
