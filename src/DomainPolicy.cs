namespace HenHotspot;

public sealed class DomainPolicy
{
    private readonly string[] domains;
    public bool AllowOnly { get; }
    // Callers cannot mutate the policy while the packet worker is using it.
    public string[] Domains => (string[])domains.Clone();

    public DomainPolicy(bool allowOnly, IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        var values = domains.Take(129).ToArray();
        if (values.Length > 128) throw new ArgumentException("Máximo 128 dominios por lista.");
        if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512))
            throw new ArgumentException("La lista contiene un dominio vacío o demasiado largo.");
        this.domains = values.Select(Validation.Domain).Distinct(StringComparer.Ordinal).ToArray();
        if (allowOnly && this.domains.Length == 0)
            throw new ArgumentException("Agrega al menos un dominio a la lista de permitidos.");
        AllowOnly = allowOnly;
    }

    public bool Allows(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 512) return false;
        try { domain = Validation.Domain(domain); }
        catch (ArgumentException) { return false; }
        bool matches = domains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal));
        return AllowOnly ? matches : !matches;
    }
}
