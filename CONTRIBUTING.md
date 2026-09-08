# Contributing to Hen Hotspot

Hen is a Windows desktop application. Use Windows 11 x64 and a stable .NET 10 SDK; `global.json` selects the latest installed .NET 10 feature band. The Windows hotspot, WFP, and WinDivert components cannot be validated by a Linux-only build.

Open `HenHotspot.sln` in an IDE with .NET 10 support, or use the commands below.

## Development

```powershell
dotnet restore tests/HenHotspot.Tests.csproj --locked-mode
dotnet build tests/HenHotspot.Tests.csproj -c Release --no-restore -warnaserror
dotnet run --project tests/HenHotspot.Tests.csproj -c Release --no-build
dotnet run --project src/HenHotspot.csproj -c Release
```

The tests are an executable verification suite: run them with `dotnet run`, not `dotnet test`. The default suite uses temporary databases and in-memory packet fixtures. It does not enable the hotspot, install filters, request elevation, or contact external websites.

Use the opt-in `--internet`, `--integration`, and `--benchmark` modes only as described in the [README](README.md). Never enable `--integration` on a hotspot serving other people.

## Changes and pull requests

- Keep changes focused. Explain the problem, resulting behavior, and validation performed.
- Follow `.editorconfig`. Run `dotnet format whitespace` for both project files; CI verifies C# formatting.
- Add regression tests for behavior changes, especially parsing, persistence, lifecycle, and access decisions. Avoid tests that only repeat implementation details.
- Add UI messages to `src/Localization/Strings.json` in Spanish, English, and Brazilian Portuguese. Use stable values for configuration and IPC; never base policy decisions on translated labels.
- Preserve saved profiles and local history when evolving formats. Add migration tests and use temporary files plus atomic replacement for JSON settings.
- Keep disk work out of packet-processing callbacks. History failures must not change whether a DNS query is allowed.
- Keep the main process unelevated. Changes to the authenticated helper protocol, filter scope, timeouts, or shutdown behavior require particular care and physical-device verification.
- Do not commit real network names, MAC/IP addresses, browsing history, credentials, runtime settings, diagnostic exports, or personal screenshots. Documentation captures must be reviewed for private data.
- Keep vendored WinDivert distribution files unmodified. Review its license and source-distribution requirements before updating it.

When updating NuGet dependencies, regenerate and commit both `packages.lock.json` files, run the suite, and check `dotnet list src/HenHotspot.csproj package --vulnerable --include-transitive`. CI uses locked restores. GitHub Actions dependencies are pinned to commit hashes and tracked by Dependabot.

The Windows CI workflow builds, checks formatting, runs the offline suite, and verifies publishing. It does not prove that a physical phone is filtered correctly. Report manual test results separately, including Windows version, adapter, filter configuration, and the tested outcome without personal identifiers.

Contributions to original Hen code are provided under the repository's [MIT license](LICENSE). Third-party components retain their own licenses.
