# Windows installer

Hen Hotspot uses [Inno Setup](https://jrsoftware.org/) to package a self-contained Windows x64 publish into one offline installer. End users do not need the .NET SDK, the .NET runtime, or a separate WinDivert download.

## Build

Requirements: Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and [Inno Setup 6.7.3 or later](https://jrsoftware.org/isdl.php). The build script does not install or download the compiler. Inno Setup retains its own license.

From the repository root:

```powershell
.\installer\Build-Installer.ps1
```

For a compiler outside a standard installation directory or a custom output directory:

```powershell
.\installer\Build-Installer.ps1 `
    -CompilerPath 'C:\Tools\Inno Setup 6\ISCC.exe' `
    -OutputDirectory 'C:\Builds\Hen Hotspot'
```

The version is read from `src/HenHotspot.csproj`. Each invocation publishes into a new directory under `work/installer/`, using locked NuGet dependencies. Required native files, runtimes, and licenses are checked before compilation. Known local history and settings files are rejected. Outputs are copied to `dist/` only after a successful compile:

- `HenHotspot-Setup-<version>-win-x64.exe`
- `HenHotspot-Setup-<version>-win-x64.exe.sha256`

The compiler and .NET SDK version can affect the output hash. The checksum identifies a particular build; it does not authenticate its publisher. Release signing is not configured. Do not describe the app or installer as digitally signed.

## Installation behavior

- Windows 11 build 22000 or later, native x64 only. ARM64 emulation cannot run the included driver.
- Current-user installation in `%LOCALAPPDATA%\Programs\Hen Hotspot` by default, with a selectable destination.
- English, Spanish, and Brazilian Portuguese setup text. The app retains its independently saved language preference.
- Start menu shortcut, optional desktop shortcut, and a Windows uninstall entry.
- No downloads, startup entries, firewall changes, or automatic filter activation during setup.
- The stable `AppId` in `HenHotspot.iss` identifies updates. Never change it for a routine release.
- The main-window mutex and a Windows Management Instrumentation process check reject installation or removal while Hen or its helpers are running. Processes are not forcibly terminated. Close Hen normally and wait for its helpers to exit before continuing.
- A process-check failure stops installation or removal. Logs contain the underlying Windows error.
- Older app versions cannot replace a newer executable in the selected destination. Identical WinDivert files are preserved; changed native binaries use version-aware replacement. A changed driver that Windows still holds open can require a restart before retrying. Setup never stops a shared WinDivert service.
- Settings and history in `%LOCALAPPDATA%\HenHotspot` are not part of the payload and are never deleted by setup or uninstall.
- An existing portable copy is not moved or deleted. Its settings are reused when it belongs to the same Windows user.

## Release verification

See the [local 0.8.0 verification record](VERIFICATION.md) for completed checks and remaining coverage.

Run the [application verification suite](../README.md#tests) before packaging. Test installation on a disposable Windows 11 x64 account or virtual machine, including one without a separately installed .NET runtime.

1. Verify installer metadata, version, icon, all three setup languages, and the generated SHA-256 checksum.
2. With Hen open, verify setup refuses to proceed. Repeat with an active helper in a controlled hotspot session; closing Hen must use its normal filter shutdown behavior.
3. Install to a fresh directory containing spaces. Compare every installed payload file with the published files by SHA-256, including native libraries, licenses, and the WinDivert source archive.
4. Launch the app and verify its version, saved settings, and local activity. Setup must not enable the hotspot or filters.
5. Close Hen and run the same installer again. Verify it preserves settings and history and does not add duplicate uninstall entries. Repeat with the previous released installer to check an actual version upgrade before publishing future releases.
6. Attempt a downgrade and verify it is refused before files change.
7. Verify uninstall refuses to proceed while Hen is open. Close Hen, uninstall, and verify payload files, shortcuts, and the uninstall entry are removed while local settings and history remain.
8. Test DNS filtering from a physical phone separately. Installer success does not establish filtering behavior or adapter compatibility.

Silent installation can be useful in an isolated test environment. Always suppress automatic restarts explicitly:

```powershell
.\HenHotspot-Setup-0.8.0-win-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /LANG=english /TASKS="" /LOG="setup.log"
```

The post-install launch is skipped in silent mode. See the [Inno Setup command-line reference](https://jrsoftware.org/ishelp/topic_setupcmdline.htm) for logging, custom destinations, and exit codes.
