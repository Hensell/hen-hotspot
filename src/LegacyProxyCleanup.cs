namespace HenHotspot;

// One-way migration of the rule created by Hen 0.5.x. This is run only inside
// Hen's authenticated elevated helper; no new firewall rule is needed for DNS.
internal static class LegacyProxyCleanup
{
    public static void RemoveOwnedRule()
    {
        dynamic firewall = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        foreach (dynamic rule in firewall.Rules)
        {
            if ((string)rule.Name == "Hen Hotspot - Proxy local" && rule.Protocol == 6 &&
                (string)rule.LocalPorts == "8877" && rule.Direction == 1 && rule.Action == 1 &&
                string.Equals((string)rule.ApplicationName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
                ((string)rule.Description).StartsWith("Permite el proxy de Hen Hotspot", StringComparison.Ordinal))
            {
                firewall.Rules.Remove("Hen Hotspot - Proxy local");
                return;
            }
        }
    }
}
