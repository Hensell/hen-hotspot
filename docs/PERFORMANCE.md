# Performance notes

Version 0.8.0 reduces idle polling, avoids unchanged history queries, removes duplicate rendering, and batches SQLite writes using a reused prepared command. The storage approach follows [Microsoft.Data.Sqlite's bulk-insert guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/bulk-insert).

## Reproducing the storage measurement

```powershell
dotnet run --project tests/HenHotspot.Tests.csproj -c Release -- --benchmark
```

This opt-in benchmark creates an isolated temporary database. It warms the storage path, writes 10,000 synthetic DNS observations in ten bursts with flush barriers, and performs 50 history/device-list reads over those records. It checks that every observation persisted. It does not use the actual activity database, activate a filter, or change Windows networking.

Observed on the development laptop on September 8, 2026, with .NET 10.0.11 and eight logical processors:

| Operation | Before the changes | After batching and index changes |
| --- | ---: | ---: |
| Write 10,000 records: elapsed time | 2,681.5 ms | 286.2 ms |
| Write 10,000 records: process CPU time | 2,312.5 ms | 250.0 ms |
| 50 history/device reads: elapsed time | 2,064.5 ms | 1,395.1 ms |
| 50 history/device reads: process CPU time | 2,609.4 ms | 1,718.8 ms |

The write measurement used about 89% less process CPU time. These are individual development measurements, not a statistical benchmark or a prediction for other hardware. Process CPU time is summed across threads and can exceed elapsed time. The read benchmark deliberately forces every query; it does not measure the additional benefit of skipping unchanged reads in the UI.

## Measuring the running app

Compare equal-duration samples after warm-up, with the same screen, window state, database size, number of clients, and DNS load. Include the main `HenHotspot` process and both helpers when enabled. Windows hotspot/NAT, Wi-Fi driver, and native filtering work may also appear outside the main process.

Measure visible and minimized states separately. Include a real-phone test with recording on and off before drawing conclusions about overall CPU usage or battery life. Short idle samples are too variable to support a guaranteed CPU percentage.

Power-saving behavior and the unchanged safety checks are described in [ARCHITECTURE.md](ARCHITECTURE.md).
