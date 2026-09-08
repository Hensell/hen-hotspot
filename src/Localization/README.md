# Localization

`Strings.json` is embedded in the executable. Each stable message key has `es`, `en`, and `pt-BR` translations. Keep keys stable when revising copy and preserve composite format placeholders, including format specifiers such as `{0:N0}`.

Use sentence case for interface labels. Buttons and short labels have no final period; instructions and notices use full punctuation. Use `…` for work in progress, `·` between compact status fields, and a colon before a value or error detail. Preserve intentional leading and trailing spaces in messages joined to other text. Use **Agregar** consistently in Spanish, natural US English, and Brazilian Portuguese. Write **Wi-Fi**, **DNS**, **MAC**, **HTTP**, and **HTTPS** consistently. Counter labels such as `Domains: {0}` avoid incorrect plural agreement when the value is one.

Use `L10n.T("Key")` for text, `L10n.F("Key", args)` for formatted messages, and `{DynamicResource Key}` in WPF. Windows bind their `Language` property to `{DynamicResource AppLanguage}`. Runtime formatting uses `L10n.Culture` explicitly, including background tasks started before a language change.

The sidebar saves its selection atomically in `%LOCALAPPDATA%/HenHotspot/preferences.json`. Invalid or missing preferences fall back to Spanish. This file is separate from network policies, device permissions, and history. Domain modes are stored as `allowlist` or `blocklist`; legacy Spanish mode values remain readable. Combo box display text must never determine network behavior.

Both elevated helpers receive the selected language through their existing authenticated command channel. Language changes do not restart helpers or the hotspot. Unknown Windows exception details and user-entered data are not translated.

`LocalizationTests` checks all catalog entries and placeholders, persistence, legacy policies, status behavior, history labels, asynchronous formatting, and the DNS command language field. Run the standard test executable from the repository root:

```powershell
dotnet run --project tests/HenHotspot.Tests.csproj -c Release
```
