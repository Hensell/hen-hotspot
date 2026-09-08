using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HenHotspot;

public static class LocalizationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string previous = L10n.LanguageCode;
        string directory = Path.Combine(Path.GetTempPath(), "hen-language-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var language in L10n.Languages)
            {
                L10n.SetLanguage(language);
                foreach (var entry in L10n.Catalog)
                {
                    if (!entry.Value.TryGetValue(language, out var translated) || string.IsNullOrWhiteSpace(translated))
                        throw new Exception($"Missing {language}: {entry.Key}");
                    var expected = Regex.Matches(entry.Value["es"], @"\{\d+(?:[^{}]*)\}").Select(m => m.Value).Order();
                    var actual = Regex.Matches(translated, @"\{\d+(?:[^{}]*)\}").Select(m => m.Value).Order();
                    if (!expected.SequenceEqual(actual)) throw new Exception($"Placeholder mismatch {language}: {entry.Key}");
                    CompositeFormat.Parse(translated);
                    if (L10n.T(entry.Key) != translated) throw new Exception($"Wrong lookup {language}: {entry.Key}");
                }
                check(true, $"All catalog messages and format placeholders are complete for {language}");

                var preferences = new PreferencesStore(directory);
                preferences.SaveLanguage(language);
                check(new PreferencesStore(directory).LoadLanguage() == language, $"Language {language} persists across instances");
                check(!Directory.GetFiles(directory, "*.tmp").Any(), "Preference write leaves no temporary file");

                foreach (string mode in new[] { "allowlist", "Permitir solo estos dominios", "blocklist", "Bloquear dominios" })
                {
                    var draft = JsonSerializer.Deserialize<PolicyDraft>(JsonSerializer.Serialize(new PolicyDraft(Mode: mode, Domains: ["wikipedia.org"])))!;
                    var policy = new DomainPolicy(draft.AllowOnly, draft.Domains!);
                    check(policy.Allows("es.wikipedia.org") == draft.AllowOnly && policy.Allows("example.org") != draft.AllowOnly,
                        $"{language} preserves {mode} behavior including legacy profiles");
                }
                var ui = HotspotUiState.From("On", "Enabled", false, false);
                check(ui.Label == L10n.T("On") && ui.CanApplyRules && !ui.CanEditNetwork, $"Localized {language} status preserves enabled controls");
                var entryRecord = new ActivityEntry("test", DateTimeOffset.Now, DateTimeOffset.Now, null, "192.168.137.10", "Teléfono de prueba", "02:00:00:00:00:01", "wikipedia.org", "DNS", "Blocked", 0, 0, 0);
                var row = new ActivityRow(entryRecord);
                check(row.Status == L10n.T("Blocked") && row.Protocol == L10n.T("DNSQuery") && row.Duration == "—" && row.Device == entryRecord.DeviceName,
                    $"{language} localizes history labels without changing stored data or user names");
                check(row.Date.EndsWith(entryRecord.StartedAt.ToLocalTime().ToString("d", L10n.Culture)), $"History date follows {language}");
                check(DomainEntryInput.Normalize("https://pt.wikipedia.org/wiki/Internet") == "pt.wikipedia.org", $"Domain normalization is unchanged in {language}");
                try { DomainEntryInput.Normalize("https://localhost"); throw new Exception("Invalid domain accepted"); }
                catch (ArgumentException ex) { check(ex.Message == L10n.F("InvalidDomain0", "localhost"), $"Validation errors use {language}"); }

                using var wire = new MemoryStream();
                var command = new DnsFilterCommand("apply", true, ["wikipedia.org"], language);
                await GuardWire.SendAsync(wire, command, CancellationToken.None); wire.Position = 0;
                var received = await GuardWire.ReceiveAsync<DnsFilterCommand>(wire, CancellationToken.None);
                check(received.Language == language && received.Operation == "apply" && received.AllowOnly && received.Domains!.SequenceEqual(command.Domains!),
                    $"DNS helper language travels independently of its {language} policy");
            }
            var store = new PreferencesStore(directory);
            File.WriteAllText(Path.Combine(directory, "preferences.json"), "{broken");
            check(store.LoadLanguage() == "es", "Corrupt preferences fall back to Spanish");
            File.WriteAllText(Path.Combine(directory, "preferences.json"), "{\"Language\":\"unknown\"}");
            check(store.LoadLanguage() == "es", "Unsupported persisted language falls back to Spanish");
            store.SaveLanguage("en");
            try { store.SaveLanguage("unknown"); throw new Exception("Accepted unsupported language"); }
            catch (ArgumentException) { check(store.LoadLanguage() == "en", "Invalid preference writes preserve the saved language"); }

            L10n.SetLanguage("en");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldContext = Task.Run(async () => { await release.Task; return ActivityRow.Bytes(1500); });
            L10n.SetLanguage("pt-BR"); release.SetResult();
            check(await oldContext == "1,5 KB", "Existing asynchronous tasks use the newly selected number format");
            check(L10n.TranslateNotice("That domain is already on the list.", "en") == "Esse domínio já está na lista.", "Visible validation notice can switch language");
            check(L10n.TranslateNotice("OS error 123", "en") == "OS error 123", "Unknown system details remain intact");
            await VerifyDatePickerSwitching();
            check(true, "Date picker language switches preserve dates and replace the previous display format");
        }
        finally { L10n.SetLanguage(previous); Directory.Delete(directory, true); }
    }

    private static Task VerifyDatePickerSwitching()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var date = new DateTime(2026, 9, 8);
                L10n.SetLanguage("en");
                var picker = new System.Windows.Controls.DatePicker
                {
                    Language = System.Windows.Markup.XmlLanguage.GetLanguage("en-US"),
                    SelectedDateFormat = System.Windows.Controls.DatePickerFormat.Short,
                    SelectedDate = date
                };
                foreach (string language in new[] { "pt-BR", "es", "en" })
                {
                    var saved = picker.SelectedDate;
                    L10n.SetLanguage(language);
                    L10n.RefreshDatePicker(picker, saved);
                    if (picker.SelectedDate != date || picker.Text != date.ToString("d", L10n.Culture))
                        throw new Exception($"Date or display changed incorrectly for {language}: {picker.Text}");
                }
                L10n.RefreshDatePicker(picker, null);
                if (picker.SelectedDate is not null || picker.Text.Length != 0)
                    throw new Exception("An empty date acquired a value during localization.");
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
