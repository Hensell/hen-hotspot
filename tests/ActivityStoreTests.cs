using System.Diagnostics;
using System.IO;
using HenHotspot;
using Microsoft.Data.Sqlite;

public static class ActivityStoreTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "HenHotspot.ActivityTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-2);
            var all = new ActivityFilter(now.AddDays(-100), now.AddDays(1));
            ActivityEntry Entry(string id, DateTimeOffset at, string domain = "example.com", string device = "phone-a",
                string outcome = "Allowed", long upload = 10, long download = 100) =>
                new(id, at, at.AddSeconds(10), at.AddSeconds(8), device == "phone-a" ? "192.168.137.2" : "192.168.137.3",
                    device == "phone-a" ? "Mi teléfono" : "Otro teléfono", device, domain, "HTTPS", outcome, upload, download, 10);

            using (var store = new ActivityStore(directory))
            {
                check(store.RecordingEnabled && store.RetentionDays == 7 && store.Error is null, "Activity defaults to enabled with seven-day retention");
                long emptyRevision = store.Revision;
                await store.FlushAsync();
                check(store.Revision == emptyRevision, "An unchanged history does not invalidate the UI cache");
                store.Record(Entry("first", now, upload: 10, download: 100));
                store.Record(Entry("blocked", now.AddSeconds(1), "blocked.example", "phone-b", "Blocked", 0, 0));
                store.Record(Entry("updated", now.AddSeconds(2), "video.example.com", upload: 3, download: 4) with { EndedAt = null });
                store.Record(Entry("updated", now.AddSeconds(2), "video.example.com", upload: 20, download: 300));
                await store.FlushAsync();
                var firstPage = store.Query(all, 0, 2);
                check(store.Revision > emptyRevision, "Committed activity invalidates the UI cache");
                check(firstPage.TotalCount == 3 && firstPage.Entries.Count == 2 && firstPage.Entries[0].Id == "updated", "Activity upserts IDs and returns newest first with pagination");
                check(firstPage.UploadBytes == 30 && firstPage.DownloadBytes == 400 && firstPage.BlockedCount == 1, "Activity totals cover the complete filter rather than the visible page");
                check(store.Query(all, 1, 2).Entries.Single().Id == "first", "Activity second page has remaining entry without duplicates");
                var boundaries = store.Query(new(now, now.AddSeconds(2)));
                check(boundaries.TotalCount == 2 && boundaries.Entries.All(e => e.Id != "updated"), "Activity date range includes start and excludes end");
                check(store.Query(all with { DeviceId = "phone-b" }).Entries.Single().Id == "blocked", "Activity filters by stable device ID");
                check(store.Query(all with { Outcome = "Blocked" }).BlockedCount == 1 && store.Query(all with { Outcome = "Error" }).TotalCount == 0, "Activity filters allowed and blocked outcomes accurately");
                check(store.Query(all with { Domain = "EXAMPLE.COM" }).TotalCount == 2, "Activity domain search is case-insensitive and supports partial literal names");
                check(store.Query(all with { Domain = "%" }).TotalCount == 0 && store.Query(all with { Domain = "_" }).TotalCount == 0 &&
                    store.Query(all with { Domain = "' OR 1=1 --" }).TotalCount == 0, "Activity domain filters treat percent, underscore and SQL as literal text");
                check(store.GetDevices().Count == 2 && store.GetDevices().Single(d => d.Id == "phone-a").Name == "Mi teléfono", "Activity device list deduplicates identities with readable labels");

                store.Record(Entry("bad-url", now.AddSeconds(3), "https://example.com/private?token=secret"));
                await store.FlushAsync();
                check(store.Query(all).Entries.Single(e => e.Id == "bad-url").Domain == "", "Activity never stores URL paths or query strings even if a caller passes a URL");

                store.Record(Entry("live-restart", now.AddSeconds(4)) with { EndedAt = null, DurationSeconds = 7 });
                store.Record(Entry("blocked-restart", now.AddSeconds(4), outcome: "Blocked") with { EndedAt = null });
                store.Record(Entry("error-restart", now.AddSeconds(4), outcome: "Error") with { EndedAt = null });
                await store.FlushAsync();
                check(store.Query(all).Entries.Single(e => e.Id == "live-restart").EndedAt is null, "Activity persists an in-progress session snapshot");
            }
            using (var store = new ActivityStore(directory))
            {
                var restored = store.Query(all);
                check(restored.TotalCount == 7 && restored.Entries.Single(e => e.Id == "updated").DownloadBytes == 300, "Activity history and byte counters survive reopening the store");
                var interrupted = restored.Entries.Single(e => e.Id == "live-restart");
                check(interrupted.Outcome == "Interrupted" && interrupted.EndedAt == interrupted.StartedAt.AddSeconds(7) && interrupted.DurationSeconds == 7,
                    "Activity startup ends abandoned sessions at their last recorded duration without inventing downtime");
                check(restored.Entries.Single(e => e.Id == "blocked-restart") is { Outcome: "Blocked", EndedAt: not null } &&
                    restored.Entries.Single(e => e.Id == "error-restart") is { Outcome: "Error", EndedAt: not null },
                    "Activity restart finalizes blocked and failed snapshots without changing their outcomes");

                long firstGeneration = store.Generation;
                store.Record(Entry("live-pause", now.AddSeconds(5)) with { EndedAt = null }, firstGeneration);
                store.Record(Entry("blocked-pause", now.AddSeconds(5), outcome: "Blocked") with { EndedAt = null }, firstGeneration);
                store.Record(Entry("error-pause", now.AddSeconds(5), outcome: "Error") with { EndedAt = null }, firstGeneration);
                store.UpdateSettings(false, 30);
                var pauseEntry = store.Query(all).Entries.Single(e => e.Id == "live-pause");
                check(!store.RecordingEnabled && store.Generation != firstGeneration && pauseEntry.Outcome == "Interrupted" && pauseEntry.EndedAt is not null &&
                    Math.Abs(pauseEntry.DurationSeconds - (pauseEntry.EndedAt.Value - pauseEntry.StartedAt).TotalSeconds) < .001,
                    "Activity pause changes generation and closes only the recorded session at the pause time");
                check(store.Query(all).Entries.Single(e => e.Id == "blocked-pause") is { Outcome: "Blocked", EndedAt: not null } &&
                    store.Query(all).Entries.Single(e => e.Id == "error-pause") is { Outcome: "Error", EndedAt: not null },
                    "Activity pause finalizes blocked and failed snapshots without changing their outcomes");
                store.Record(Entry("while-paused", now.AddSeconds(6)));
                long pausedRevision = store.Revision;
                await store.FlushAsync();
                check(store.Revision == pausedRevision, "Ignored observations do not trigger history reloads");
                check(!store.Query(all).Entries.Any(e => e.Id == "while-paused"), "Activity ignores new records while paused");
                store.UpdateSettings(true, 30);
                store.Record(Entry("stale-generation", now.AddSeconds(7)), firstGeneration);
                store.Record(Entry("fresh-generation", now.AddSeconds(8)), store.Generation);
                await store.FlushAsync();
                check(!store.Query(all).Entries.Any(e => e.Id == "stale-generation") && store.Query(all).Entries.Any(e => e.Id == "fresh-generation"),
                    "Activity resume rejects old session generations but records fresh connections");

                store.Record(Entry("older", now.AddDays(-8)));
                await store.FlushAsync();
                check(store.Query(all).Entries.Any(e => e.Id == "older"), "Activity accepts records within the configured longer retention");
                store.UpdateSettings(true, 7);
                store.Record(Entry("older", now.AddDays(-8)) with { DownloadBytes = 999 });
                await store.FlushAsync();
                check(!store.Query(all).Entries.Any(e => e.Id == "older"), "Activity retention deletes expired sessions and rejects later snapshots that could recreate them");

                for (int i = 0; i < 600; i++) store.Record(Entry("queued-" + i, now.AddSeconds(10)));
                long generationBeforeClear = store.Generation;
                store.Clear();
                store.Record(Entry("after-clear-old-session", DateTimeOffset.UtcNow), generationBeforeClear);
                await store.FlushAsync();
                check(store.Query(all).TotalCount == 0 && store.GetDevices().Count == 0 && store.Generation != generationBeforeClear,
                    "Activity clear removes queued data and devices and prevents stale generations from restoring history");
                store.Record(Entry("after-clear-new", DateTimeOffset.UtcNow));
                await store.FlushAsync();
                check(store.Query(all).TotalCount == 1, "Activity clear allows genuinely new connections afterwards");
                store.UpdateSettings(false, 90);
                store.Clear();
                check(!store.RecordingEnabled && store.RetentionDays == 90 && store.Query(all).TotalCount == 0,
                    "Activity clear while paused preserves recording and retention settings");
            }
            using (var reopened = new ActivityStore(directory))
            {
                check(!reopened.RecordingEnabled && reopened.RetentionDays == 90 && reopened.Query(all).TotalCount == 0,
                    "Activity clear and paused settings persist across app restart");
            }

            // Seed only this isolated test database to exercise the actual disk bound without queuing 50k network snapshots.
            using (var seed = new SqliteConnection("Data Source=" + Path.Combine(directory, "activity.db")))
            {
                seed.Open();
                using var command = seed.CreateCommand();
                command.CommandText = """
                    WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n+1 FROM numbers WHERE n<50004)
                    INSERT INTO activity
                    SELECT 'capacity-'||n,$start+n,$start+n,NULL,'192.168.137.2','Test','test-device','capacity.example','HTTP','Allowed',1,2,0 FROM numbers;
                    """;
                command.Parameters.AddWithValue("$start", now.UtcTicks);
                command.ExecuteNonQuery();
            }
            using (var bounded = new ActivityStore(directory))
            {
                var page = bounded.Query(all);
                check(page.TotalCount == 50_000 && page.Entries[0].Id == "capacity-50004" &&
                    bounded.Query(all, 499, 100).Entries[^1].Id == "capacity-5",
                    "Activity disk retention caps history at 50,000 newest records");
                bounded.Clear();
                check(new FileInfo(Path.Combine(directory, "activity.db")).Length < 1_000_000 && bounded.Query(all).TotalCount == 0,
                    "Activity clear compacts the database and releases disk space");
            }

            string blockedDirectory = Path.Combine(directory, "not-a-directory");
            string batchDirectory = Path.Combine(directory, "batched-shutdown");
            using (var batched = new ActivityStore(batchDirectory))
            {
                for (int index = 0; index < 1024; index++)
                    batched.Record(Entry("burst-" + index, now.AddTicks(index), outcome: index % 2 == 0 ? "Blocked" : "Allowed"));
                // Dispose must commit accepted work even without an explicit flush.
            }
            using (var reopenedBatch = new ActivityStore(batchDirectory))
            {
                var result = reopenedBatch.Query(all);
                check(result.TotalCount == 1024 && result.BlockedCount == 512 && result.UploadBytes == 10_240 && result.DownloadBytes == 102_400,
                    "Batched shutdown drains every accepted observation with accurate outcomes and totals");
            }

            File.WriteAllText(blockedDirectory, "test");
            using (var broken = new ActivityStore(blockedDirectory))
            {
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < 1000; i++) broken.Record(Entry("fail-" + i, now));
                check(clock.Elapsed < TimeSpan.FromSeconds(1), "Activity storage errors do not block proxy threads while recording");
                await broken.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                bool settingsFailed = false, clearFailed = false;
                try { broken.UpdateSettings(false, 7); } catch (IOException) { settingsFailed = true; }
                try { broken.Clear(); } catch (IOException) { clearFailed = true; }
                check(settingsFailed && clearFailed, "Activity failed settings and clear mutations throw so the UI cannot announce false success");
                check(broken.Error is not null && broken.Query(all).TotalCount == 0 && broken.GetDevices().Count == 0,
                    "Activity storage failures are surfaced without throwing into proxy or user queries");
            }
            using (var read = new SqliteConnection("Data Source=" + Path.Combine(directory, "activity.db")))
            {
                read.Open();
                using var command = read.CreateCommand();
                command.CommandText = "PRAGMA integrity_check";
                check((string?)command.ExecuteScalar() == "ok", "Activity database remains consistent after queued writes, settings changes and clear");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            string resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("HenHotspot.ActivityTests-", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
