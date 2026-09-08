# Architecture and invariants

Hen is a native WPF application with two optional administrative helper modes of the same executable. Windows owns the mobile hotspot and internet connection sharing. The application does not implement its own Wi-Fi access point or forward every browser payload through a managed proxy.

| Component | Responsibility |
| --- | --- |
| `MainWindow.*` | Navigation, localized presentation, user actions, and lifecycle coordination |
| `HotspotService` | Read and control the Windows tethering APIs |
| `RefreshPolicy` / `MainWindow.Refresh` | Adaptive status polling, network-change notifications, and window lifecycle |
| `DomainPolicy` / `DnsPacket` | Domain decisions and bounded DNS packet parsing |
| `DnsFilterClient` / `DnsFilterEngine` | Authenticated helper IPC and narrowly scoped WinDivert DNS filtering |
| `DeviceGuard` / `NativeWfp` | Device approvals and temporary Windows Filtering Platform rules |
| `ActivityStore` | Queued local SQLite persistence, queries, retention, and recording barriers |
| `ProfileStore` / `DeviceAccessStore` / `PreferencesStore` | Local JSON settings and atomic replacement |
| `L10n` | Resource completeness, language selection, and explicit date/number formatting |

## Scheduling

An active filter or an uncertain network state keeps the main safety poll at two seconds, including while minimized. An unfiltered active hotspot refreshes at two seconds when visible and ten seconds when minimized. A confirmed inactive hotspot refreshes at ten seconds when visible and thirty seconds when minimized.

Windows network-change notifications request a refresh without waiting for the next scheduled poll. The app also refreshes when brought back into focus. Notifications are coalesced on the WPF dispatcher. Handlers are detached when the window closes, and completed background reads do not update a closed window.

The DNS and device helpers retain their independent two-second heartbeats. Slower presentation updates do not delay packet decisions. Expensive visual updates are skipped while minimized. Duplicate device/filter rendering was removed from the common refresh path.

## History persistence

The network callback enqueues a small observation and never waits for SQLite. One worker owns the write connection and prepared insert command. It groups up to 128 consecutive queued records in one transaction without adding an artificial batching delay. A failed batch is rolled back and reported as incomplete history; it must not affect the network decision.

Settings, pause, clear, flush, and shutdown are ordered barriers. Batching must not consume records past a control command. The generation counter prevents stale observations from repopulating cleared or paused history. Closing the store drains accepted work before disposing its connection.

Readers use separate connections and SQLite WAL snapshots. The committed revision changes after writes, settings changes, or retention deletions; it does not change for an unchanged flush. The Activity view skips automatic database reads when its filters and the committed revision are unchanged. Manual refreshes and language/filter changes still reload it.

Composite indexes match history pagination and per-device ordering. Startup upgrades those indexes without deleting history. Retention cleanup runs every minute and after write batches or explicit flushes as needed, retaining the 50,000 newest records at cleanup boundaries. A small in-flight batch can temporarily exceed that limit.

## Privileges and failures

The main window stays unelevated. Helpers are launched through the Windows administrator prompt, validate the named-pipe peer process, and scope their filters to the hotspot. Rules are applied independently of history recording. Preserve these boundaries when changing IPC, shutdown, or scheduling.

The expected shutdown and bypass behavior is documented in the [README](../README.md). Automated fixtures exercise packet parsing and policies; real adapter behavior and combined DNS/device control require separate physical tests.
