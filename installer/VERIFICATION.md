# Installer verification: 0.8.0

Verified locally on September 8, 2026, using Windows 11 x64 build 26200,
.NET SDK 10.0.400, runtime 10.0.11, and Inno Setup 6.7.3.

| Check | Result |
| --- | --- |
| Build a self-contained offline installer | Passed; about 49 MiB compressed |
| Detect an open Hen main window | Refused before installation; the app stayed open |
| Detect a helper without the main-window mutex | Installation and uninstall refused; the stand-in process stayed alive |
| Install into a new directory containing spaces | Passed without elevation or a restart |
| Installed payload SHA-256 comparison | All 422 published files matched |
| Start menu shortcut and Windows uninstall entry | Correct executable path and version 0.8.0 |
| Same-version reinstall | Passed; local data and unchanged driver timestamp preserved |
| Refuse a downgrade | Passed with a harmless executable fixture reporting version 99.0.0.0 |
| Launch installed app | Passed; saved Spanish preference and hotspot-off state retained |
| Use bundled runtime | Loaded hostfxr, hostpolicy, coreclr, and SQLite from the installation directory |
| Uninstall test installation | App files, shortcut, and uninstall entry removed |
| Preserve data on uninstall | All four existing data files retained with identical SHA-256 hashes |
| Setup localization | English, Spanish, and Brazilian Portuguese compiled and exercised in setup runs |
| Visual setup review | Spanish destination page, Hen icon, and Hen wizard logo checked |
| Concurrent setup protection | A second installer refused to run while the preview wizard was open |

The helper and downgrade checks used a temporary executable fixture, not a live
network filter. No hotspot or filter was enabled during installer verification.
Tests used a separate installation directory and removed that test installation;
the original portable application was preserved.

A clean virtual machine without a separately installed .NET runtime, an upgrade
from a previous installer release, and physical-phone filtering from the new
installation location have not been tested. Run those checks before a broader
release. These local results are not a guarantee for every adapter or Windows setup.
