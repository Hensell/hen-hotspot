<img src="src/Assets/Brand/hen-mark-128.png" width="80" alt="Hen Hotspot logo">

# Hen Hotspot

A native Windows 11 app for sharing a Wi-Fi connection, managing connected devices, and filtering hotspot DNS requests. Built with WPF and .NET 10.

**Current version: 0.6.1.** The interface is currently in Spanish. DNS filtering and device access control are experimental.

## Features

- Turn the Windows mobile hotspot on or off and check its status from any screen.
- Configure the network name, password, and Wi-Fi band while the hotspot is off.
- View connected devices and save device approvals or blocks by MAC address.
- Choose a DNS blocklist or allowlist with up to 128 domains, including subdomains.
- Add a domain or paste a web URL, manage entries as rows, and confirm before deleting.
- Save drafts separately from the rules applied to a running filter.
- Review local DNS activity, filter records, pause recording, and choose a retention period.
- Inspect network adapters and export diagnostics.

The DNS filter runs on the laptop through WinDivert. Phones do not need a manual proxy configuration.

## Requirements

- Windows 11, x64.
- A wireless adapter and driver that support Windows mobile hotspot.
- An internet connection to share. Receiving and sharing internet over Wi-Fi depends on the adapter and its driver.
- Administrator approval when enabling the DNS filter or device access control. The main interface runs without elevation.
- The .NET 10 SDK to build from source. A self-contained published build includes its runtime.

Windows and the adapter determine the maximum number of connected devices.

## Build and run

Run these commands on Windows from the repository root:

```powershell
dotnet restore src/HenHotspot.csproj
dotnet build src/HenHotspot.csproj -c Release
dotnet run --project src/HenHotspot.csproj -c Release
```

Publish a self-contained Windows x64 build:

```powershell
dotnet publish src/HenHotspot.csproj -c Release -r win-x64 --self-contained true -o app
```

Open `app/HenHotspot.exe`. When moving the app to another laptop, copy the **entire published directory**, including `WinDivert.dll`, `WinDivert64.sys`, and `ThirdParty/WinDivert`. Copying the executable alone is insufficient.

The repository includes the unmodified native WinDivert files and corresponding source archive required by the project. No separate proxy application is needed.

## Using the app

1. Open **Resumen** (Overview) and turn on the hotspot.
2. Connect a phone or another device to the hotspot's Wi-Fi network.
3. In **Dominios** (Domains), choose **Bloquear dominios** (Block domains) or **Permitir solo estos dominios** (Allow only these domains).
4. Enter a domain or paste an HTTP/HTTPS URL, then press Enter or **Agregar** (Add). A pasted URL contributes its hostname, not its path.
5. Select **Activar filtro DNS** (Enable DNS filter) and approve the Windows administrator prompt.
6. After editing a running filter's list, select **Aplicar cambios** (Apply changes). **Guardar borrador** (Save draft) saves the list without changing the active policy. Deleting an entry requires confirmation.
7. Review allowed or blocked DNS requests in **Actividad** (Activity).

Device access control is a separate switch in **Dispositivos** (Devices). Saved approvals and blocks take effect only while that control is active. With control enabled, new and blocked devices are denied internet access until approved.

### Example: Wikipedia allowlist

Choose the allowlist mode and add:

```text
wikipedia.org
upload.wikimedia.org
```

The first entry includes Wikipedia's language subdomains. The second allows its image and media host without allowing all of `wikimedia.org`. See the [Wikimedia upload service documentation](https://wikitech.wikimedia.org/wiki/Upload.wikimedia.org).

This is an example configuration, not a profile automatically installed on every laptop.

## Filtering scope

**DNS filtering is not a strict firewall for all browsing or app traffic.** The current implementation intercepts classic IPv4 UDP DNS requests sent to port 53 of the hotspot gateway. Blocked queries receive NXDOMAIN; allowed queries continue to Windows' DNS service.

- External DNS, encrypted/private DNS, VPNs, cached addresses, direct IP connections, and existing connections can bypass these domain rules.
- TCP DNS to the same gateway is blocked while the filter is active because TCP DNS parsing is not implemented. Queries that require this fallback may fail.
- Allowlisted websites may need additional domains for images, sign-in, video, or other resources.
- Rules affect new DNS requests and do not close existing connections.
- The app does not decrypt HTTPS, install certificates on phones, or inspect page content, passwords, or messages.
- This version does **not** limit bandwidth. The earlier proxy-based implementation and its speed controls have been removed.

The filter is scoped to the hotspot interface and gateway DNS. It does not change the laptop's DNS settings or use WinDivert's forwarding layer, which has [documented limitations with Windows NAT](https://reqrypt.org/windivert-doc.html#known_issues).

## Device access and shutdown

Device control associates MAC addresses observed by Windows with their current IPv4 addresses. While enabled, it permits approved device addresses and blocks forwarded IPv6 traffic.

MAC addresses identify network interfaces, not people. This does not prevent password sharing or protect against all MAC/IP spoofing. A device that changes its private MAC address may appear as a new device.

- Turning off the hotspot from Hen stops its filtering components first.
- Closing Hen with device access control enabled turns off the hotspot.
- Closing Hen with only the DNS filter enabled removes that filter; the hotspot may remain on.
- On a filtering failure, Hen attempts to turn off the hotspot. An abrupt termination can leave an interval without its temporary filters.

Keep Hen open while using its filters. Verify the behavior on each laptop and adapter before relying on it.

## Local activity and privacy

DNS activity is a record of queries, **not proof of a website visit**. Background apps can generate queries. DNS records do not measure screen time, browsing duration, or webpage traffic; their duration and traffic columns display a dash. Older connection records retain their original metrics.

Recording can be paused, resumed, or cleared. Retention options are 1, 7, 30, or 90 days, with a maximum of 50,000 recent records. Reducing retention deletes records outside the new period. Under heavy load, the bounded recording queue may omit events even if filter counters increase.

Settings, device permissions, and history are stored locally in `%LOCALAPPDATA%/HenHotspot`. Hen does not upload the history to an external service. Local runtime data is not part of this repository.

## Tests

Run the automated suite on Windows:

```powershell
dotnet run --project tests/HenHotspot.Tests.csproj -c Release
```

Optionally include a direct HTTPS connectivity check from the laptop:

```powershell
dotnet run --project tests/HenHotspot.Tests.csproj -c Release -- --internet
```

The suite has passed **211 checks** with `--internet`, covering domain/URL validation, storage, device policies, IPC, DNS parsing, 6,000 malformed or mutated packets, native filter compilation, and independently verified checksums. UI checks covered adding entries, rejecting duplicates, canceling deletion, and confirming deletion.

Physical phone tests previously verified a DNS blocklist and the Wikipedia allowlist. A separate device-control test verified an approve → block → approve cycle. Combined operation of DNS filtering and device control still needs physical validation. Automated tests and the laptop's HTTPS check do not replace testing from a connected device.

The optional `--integration` test changes the Windows hotspot configuration and requires the hotspot to be off. Run it only in a controlled test session.

The elevated `--dns-self-test` app option opens a native filter that matches no packets, checks driver version and shutdown behavior, and writes its result to `%LOCALAPPDATA%/HenHotspot/windivert-self-test.json`. It is separate from the automated suite and does not prove that phone traffic is filtered.

## Repository layout

```text
src/                         WPF app, domain policies, and filtering components
src/Assets/Brand/            App icons and brand assets
src/Native/                  WinDivert x64 DLL and signed driver
src/ThirdParty/WinDivert/    License, attribution, and corresponding source
tests/                       Automated verification suite
```

Published builds, intermediate files, local shortcuts, and runtime data are excluded from version control.

## Third-party component

Hen includes **WinDivert 2.2.2**, an independent project by basil and contributors, not a Microsoft product. The unmodified x64 DLL and signed driver come from the official `WinDivert-2.2.2-A.zip` distribution.

WinDivert is distributed here under LGPL version 3 or later. Its license, attribution, and corresponding source archive are included in [src/ThirdParty/WinDivert](src/ThirdParty/WinDivert). See the [project](https://reqrypt.org/windivert.html), [documentation](https://reqrypt.org/windivert-doc.html), and [source](https://github.com/basil00/WinDivert/tree/v2.2.2).
