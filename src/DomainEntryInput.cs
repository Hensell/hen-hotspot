namespace HenHotspot;

public static class DomainEntryInput
{
    public static string Normalize(string input)
    {
        string value = input.Trim();
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
                (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) ||
                !string.IsNullOrEmpty(url.UserInfo))
                throw new ArgumentException("Usa un dominio o una dirección web http/https válida.");
            value = url.IdnHost;
        }
        return Validation.Domain(value);
    }
}
