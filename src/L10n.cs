using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

namespace HenHotspot;

/// <summary>Embedded message catalog shared by WPF, background work and elevated helpers.</summary>
public static class L10n
{
    public static IReadOnlyList<string> Languages { get; } = Array.AsReadOnly(new[] { "es", "en", "pt-BR" });
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> catalog = LoadCatalog();
    private static CultureInfo culture = CultureInfo.GetCultureInfo("es-NI");
    public static CultureInfo Culture => Volatile.Read(ref culture);
    public static string LanguageCode => Culture.Name switch { "en-US" => "en", "pt-BR" => "pt-BR", _ => "es" };
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog => catalog;

    public static string NormalizeLanguage(string? language) => language switch
    {
        "en" => "en",
        "pt-BR" => "pt-BR",
        _ => "es"
    };

    public static string T(string key) => catalog.TryGetValue(key, out var entry)
        ? entry[LanguageCode] : throw new KeyNotFoundException($"Missing localization key: {key}");

    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    // Uses a process-wide value so existing tasks also see the latest language.
    // Formatters use Culture explicitly because async execution contexts retain their own cultures.
    public static void SetLanguage(string? language)
    {
        var next = CultureInfo.GetCultureInfo(NormalizeLanguage(language) switch
        {
            "en" => "en-US",
            "pt-BR" => "pt-BR",
            _ => "es-NI"
        });
        Volatile.Write(ref culture, next);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = next;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = next;
    }

    public static void ApplyResources(Application app)
    {
        app.Dispatcher.VerifyAccess();
        foreach (var entry in catalog) app.Resources[entry.Key] = entry.Value[LanguageCode];
        app.Resources["AppLanguage"] = XmlLanguage.GetLanguage(Culture.Name);
    }

    internal static void RefreshDatePicker(System.Windows.Controls.DatePicker picker, DateTime? selectedDate)
    {
        // WPF retains the previously formatted Text when inherited Language changes.
        // Format the saved date explicitly, rather than reparsing the old display text.
        picker.Language = XmlLanguage.GetLanguage(Culture.Name);
        string format = picker.SelectedDateFormat == System.Windows.Controls.DatePickerFormat.Short ? "d" : "D";
        picker.SetCurrentValue(System.Windows.Controls.DatePicker.TextProperty, selectedDate?.ToString(format, Culture) ?? "");
        picker.SetCurrentValue(System.Windows.Controls.DatePicker.SelectedDateProperty, selectedDate);
    }

    /// <summary>Refreshes existing app-owned notices; unknown OS details stay untouched.</summary>
    public static string TranslateNotice(string text, string previousLanguage)
    {
        if (previousLanguage == LanguageCode || text.Length == 0) return text;
        var candidates = catalog.Values.Where(e => !e[previousLanguage].Contains('{'))
            .OrderByDescending(e => e[previousLanguage].Length);
        foreach (var entry in candidates)
        {
            string previous = entry[previousLanguage];
            if (text == previous) return entry[LanguageCode];
            // Only translate complete message prefixes, never arbitrary user data inside messages.
            if (previous.EndsWith(' ') && text.StartsWith(previous, StringComparison.Ordinal))
                return entry[LanguageCode] + TranslateNotice(text[previous.Length..], previousLanguage);
        }
        return text;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadCatalog()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("HenHotspot.Localization.Strings.json")
            ?? throw new InvalidOperationException("Missing embedded language catalog.");
        var entries = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(entries.ToDictionary(
            e => e.Key, e => (IReadOnlyDictionary<string, string>)new ReadOnlyDictionary<string, string>(e.Value)));
    }
}
