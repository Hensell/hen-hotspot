using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HenHotspot;

public enum DeviceAccessState { Pending, Approved, Blocked }
public sealed record DevicePermission(string Mac, string Name, DeviceAccessState State, DateTimeOffset UpdatedAt);

/// <summary>Explicit local permissions. Unknown or invalid addresses never receive approval.</summary>
public sealed class DeviceAccessStore
{
    private const int MaximumEntries = 128;
    private const int MaximumFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private readonly object gate = new();
    private readonly string directory;
    private readonly string path;
    private Dictionary<string, DevicePermission> entries = new(StringComparer.Ordinal);
    private string? lastError;

    public string? LastError { get { lock (gate) return lastError; } }
    public IReadOnlyList<DevicePermission> Entries
    {
        get
        {
            lock (gate) return entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Mac, StringComparer.Ordinal).ToArray();
        }
    }

    public DeviceAccessStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenHotspot");
        path = Path.Combine(this.directory, "device-access.json");
        Load();
    }

    public DeviceAccessState GetState(string mac)
    {
        string normalized;
        try { normalized = NormalizeMac(mac); }
        catch (ArgumentException) { return DeviceAccessState.Pending; }
        lock (gate) return entries.TryGetValue(normalized, out var permission) ? permission.State : DeviceAccessState.Pending;
    }

    public bool IsApproved(string mac) => GetState(mac) == DeviceAccessState.Approved;

    /// <summary>Pending removes an explicit decision. The in-memory state changes only after a durable atomic save.</summary>
    public void Set(string mac, string name, DeviceAccessState state)
    {
        string normalized = NormalizeMac(mac);
        if (state is not (DeviceAccessState.Pending or DeviceAccessState.Approved or DeviceAccessState.Blocked))
            throw new ArgumentOutOfRangeException(nameof(state), "El estado de autorización no es válido.");
        string alias = ValidateName(name);
        lock (gate)
        {
            var next = new Dictionary<string, DevicePermission>(entries, StringComparer.Ordinal);
            if (state == DeviceAccessState.Pending)
            {
                if (!next.Remove(normalized)) return;
            }
            else
            {
                if (!next.ContainsKey(normalized) && next.Count >= MaximumEntries)
                    throw new InvalidOperationException("Puedes guardar hasta 128 dispositivos. Elimina una autorización anterior para agregar otro.");
                next[normalized] = new(normalized, alias, state, DateTimeOffset.UtcNow);
            }

            string temporary = Path.Combine(directory, "device-access." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new AccessDocument(1, next.Values.OrderBy(e => e.Mac, StringComparer.Ordinal).ToArray()), JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                // The temporary file is on the same volume; readers see either the old complete file or the new one.
                File.Move(temporary, path, overwrite: true);
                entries = next;
                lastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                lastError = "No se pudieron guardar las autorizaciones de dispositivos. " + ex.Message;
                throw new IOException(lastError, ex);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static string NormalizeMac(string mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        string value = mac.Trim();
        if (value.Length != 17 || value[2] is not (':' or '-')) throw InvalidMac();
        char separator = value[2];
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            if ((i < 5 && value[i * 3 + 2] != separator) ||
                !byte.TryParse(value.AsSpan(i * 3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                throw InvalidMac();
        }
        if (bytes.All(b => b == 0) || bytes.All(b => b == 255) || (bytes[0] & 1) != 0) throw InvalidMac();
        return string.Join(":", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private void Load()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes) throw new JsonException("El archivo de autorizaciones supera el tamaño permitido.");
            var document = JsonSerializer.Deserialize<AccessDocument>(stream, JsonOptions);
            if (document is null || document.Version != 1 || document.Entries is null || document.Entries.Length > MaximumEntries)
                throw new JsonException("El formato de autorizaciones no es válido.");
            var loaded = new Dictionary<string, DevicePermission>(StringComparer.Ordinal);
            foreach (var permission in document.Entries)
            {
                if (permission is null || permission.State is not (DeviceAccessState.Approved or DeviceAccessState.Blocked) || permission.UpdatedAt == default)
                    throw new JsonException("Hay una autorización incompleta o con un estado no válido.");
                string normalized = NormalizeMac(permission.Mac);
                string alias = ValidateName(permission.Name);
                if (!loaded.TryAdd(normalized, permission with { Mac = normalized, Name = alias, UpdatedAt = permission.UpdatedAt.ToUniversalTime() }))
                    throw new JsonException("El archivo contiene direcciones de dispositivo duplicadas.");
            }
            // Never partially accept a damaged document: one invalid entry invalidates the entire load.
            entries = loaded;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException ex)
        {
            if (File.Exists(directory)) lastError = "No se pudieron leer las autorizaciones. Ningún dispositivo se aprobará a partir de este archivo. " + ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            entries.Clear();
            lastError = "No se pudieron leer las autorizaciones. Ningún dispositivo se aprobará a partir de este archivo. " + ex.Message;
        }
    }

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim();
        if (trimmed.Length > 80 || trimmed.Any(char.IsControl))
            throw new ArgumentException("El nombre admite hasta 80 caracteres y no puede contener saltos de línea ni caracteres de control.", nameof(name));
        return trimmed;
    }
    private static ArgumentException InvalidMac() => new("Usa una dirección MAC unicast válida de seis pares hexadecimales separados por dos puntos o guiones.", "mac");
    private sealed record AccessDocument(int Version, DevicePermission[] Entries);
}
