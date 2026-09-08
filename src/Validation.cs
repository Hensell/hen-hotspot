using System.Globalization;
using System.Text;

namespace HenHotspot;

public static class Validation
{
    public static void Network(string ssid, string password)
    {
        if (string.IsNullOrWhiteSpace(ssid) || Encoding.UTF8.GetByteCount(ssid.Trim()) > 32 || ssid.Any(char.IsControl))
            throw new ArgumentException(L10n.T("TheNetworkNameMustUse132BytesAnd"));
        if (password.Length is < 8 or > 63 || password.Any(c => c < 32 || c > 126))
            throw new ArgumentException(L10n.T("UseAPasswordOf863CharactersUnaccentedLetters"));
    }

    public static string Domain(string input)
    {
        string value = input.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.StartsWith("*.")) value = value[2..];
        if (value.Length == 0 || value.Contains('/') || value.Contains(':') || value.Contains('@'))
            throw new ArgumentException(L10n.F("EnterOnlyTheDomainForExampleYoutubeCom0", input));
        try { value = new IdnMapping().GetAscii(value); }
        catch (ArgumentException) { throw new ArgumentException(L10n.F("InvalidDomain0", input)); }
        var labels = value.Split('.');
        if (value.Length > 253 || labels.Length < 2 || System.Net.IPAddress.TryParse(value, out _) ||
            labels.Any(l => l.Length is < 1 or > 63 || l[0] == '-' || l[^1] == '-' || l.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) ||
            labels[^1].All(char.IsDigit))
            throw new ArgumentException(L10n.F("InvalidDomain0", input));
        return value;
    }

    public static decimal Speed(string value)
    {
        if (!decimal.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var speed) || speed is < 0.1m or > 10000m)
            throw new ArgumentException(L10n.T("SpeedMustBeBetween01And10000Mbps"));
        return speed;
    }
}
