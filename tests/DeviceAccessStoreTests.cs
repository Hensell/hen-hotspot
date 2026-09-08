using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HenHotspot;

public static class DeviceAccessStoreTests
{
    public static Task Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "HenHotspot.DeviceAccessTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string alice = "02:00:00:00:00:01";
            const string bob = "02:11:22:33:44:55";
            string path = Path.Combine(directory, "device-access.json");
            var store = new DeviceAccessStore(directory);
            check(store.LastError is null && store.Entries.Count == 0 && store.GetState(alice) == DeviceAccessState.Pending && !store.IsApproved(alice),
                "Device access starts with no approvals and unknown devices pending");
            check(!File.Exists(path) && store.Entries.Count == 0, "Device access lookups never register unknown MAC addresses automatically");
            check(DeviceAccessStore.NormalizeMac(" 02-00-00-00-00-01 ") == alice && DeviceAccessStore.NormalizeMac("00:11:22:33:44:55") == "00:11:22:33:44:55",
                "Device access normalizes colon and hyphen addresses while accepting valid global and private unicast MACs");
            foreach (string invalid in new[] { "00:00:00:00:00:00", "ff:ff:ff:ff:ff:ff", "01:00:5e:00:00:01", "33:33:00:00:00:01",
                "02:00:00:00:00", "020000000001", "02:00:00:00:00:gg", "02-00:00:00:00:01", "", "02:00:00:00:00:01:00" })
            {
                bool rejected = false;
                try { store.Set(invalid, "Invalid", DeviceAccessState.Approved); } catch (ArgumentException) { rejected = true; }
                check(rejected && !store.IsApproved(invalid), "Device access rejects invalid or non-unicast MAC " + invalid);
            }
            check(!store.IsApproved(null!), "Device access safely denies null MAC lookups");
            store.Set("02-00-00-00-00-01", "  Teléfono de Ana  ", DeviceAccessState.Approved);
            var first = store.Entries.Single();
            check(store.IsApproved(alice) && first is { Mac: alice, Name: "Teléfono de Ana", State: DeviceAccessState.Approved } &&
                first.UpdatedAt.Offset == TimeSpan.Zero && first.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1),
                "Device access stores explicit approval with a normalized MAC, trimmed alias and UTC timestamp");
            var reopened = new DeviceAccessStore(directory);
            check(reopened.LastError is null && reopened.IsApproved(alice) && reopened.Entries.Single() == first,
                "Device permissions and update timestamps survive reopening");
            var snapshot = store.Entries;
            store.Set(alice, "Ana bloqueada", DeviceAccessState.Blocked);
            check(!store.IsApproved(alice) && store.GetState(alice) == DeviceAccessState.Blocked && store.Entries.Single().UpdatedAt >= first.UpdatedAt &&
                snapshot.Single().State == DeviceAccessState.Approved,
                "Device access replaces decisions without duplicates and previously returned snapshots remain unchanged");
            check(new DeviceAccessStore(directory).GetState(alice) == DeviceAccessState.Blocked, "Device blocking persists after reopening");
            store.Set(alice, "", DeviceAccessState.Pending);
            check(store.Entries.Count == 0 && new DeviceAccessStore(directory).GetState(alice) == DeviceAccessState.Pending,
                "Setting a device to pending removes its persisted explicit permission");
            store.Set(alice, new string('a', 80), DeviceAccessState.Approved);
            bool longName = false, controlName = false, invalidState = false;
            try { store.Set(bob, new string('b', 81), DeviceAccessState.Approved); } catch (ArgumentException) { longName = true; }
            try { store.Set(bob, "Alias\nsegundo", DeviceAccessState.Approved); } catch (ArgumentException) { controlName = true; }
            try { store.Set(bob, "Alias", (DeviceAccessState)99); } catch (ArgumentException) { invalidState = true; }
            check(longName && controlName && invalidState && store.Entries.Single().Name.Length == 80 && !store.IsApproved(bob),
                "Device access validates alias length, control characters and enum states before applying a decision");

            byte[] previous = File.ReadAllBytes(path);
            using (var heldFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool saveFailed = false;
                try { store.Set(bob, "Bob", DeviceAccessState.Approved); } catch (IOException) { saveFailed = true; }
                check(saveFailed && store.LastError is not null && !store.IsApproved(bob) && store.IsApproved(alice),
                    "Device access surfaces failed saves and never grants an approval that was not persisted");
            }
            check(File.ReadAllBytes(path).SequenceEqual(previous) && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
                "Device access failed replacement preserves the previous complete file and cleans its temporary file");
            store.Set(bob, "Bob", DeviceAccessState.Approved);
            check(store.LastError is null && new DeviceAccessStore(directory).IsApproved(bob), "A later successful device save clears the previous storage error");

            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
            void WriteDocument(IEnumerable<DevicePermission> entries, int version = 1) => File.WriteAllText(path,
                JsonSerializer.Serialize(new { Version = version, Entries = entries.ToArray() }, jsonOptions));
            var permission = new DevicePermission(alice, "Ana", DeviceAccessState.Approved, DateTimeOffset.UtcNow);
            WriteDocument([permission, permission with { Mac = "02-00-00-00-00-01" }]);
            var duplicate = new DeviceAccessStore(directory);
            check(duplicate.LastError is not null && duplicate.Entries.Count == 0 && !duplicate.IsApproved(alice),
                "Device access rejects duplicate normalized identities instead of choosing a possibly unsafe decision");
            WriteDocument([permission, permission with { Mac = "01:11:22:33:44:55" }]);
            var invalidDocument = new DeviceAccessStore(directory);
            check(invalidDocument.LastError is not null && !invalidDocument.IsApproved(alice),
                "One invalid persisted MAC invalidates the entire permission document without partial approvals");
            WriteDocument([permission], version: 2);
            check(new DeviceAccessStore(directory) is { LastError: not null, Entries.Count: 0 }, "Device access fails closed on an unsupported document version");
            File.WriteAllText(path, "{\"version\":1,\"entries\":[");
            var corrupt = new DeviceAccessStore(directory);
            check(corrupt.LastError is not null && !corrupt.IsApproved(alice) && File.ReadAllText(path).EndsWith('['),
                "Device access exposes corrupt storage and neither grants access nor silently overwrites the damaged file on load");
            corrupt.Set(bob, "Bob autorizado de nuevo", DeviceAccessState.Approved);
            check(corrupt.LastError is null && corrupt.IsApproved(bob) && !corrupt.IsApproved(alice) && new DeviceAccessStore(directory).Entries.Count == 1,
                "An explicit new decision can rebuild damaged storage without recovering any untrusted old approval");

            string Mac(int i) => $"02:10:20:30:{i / 256:x2}:{i % 256:x2}";
            WriteDocument(Enumerable.Range(0, 127).Select(i => permission with { Mac = Mac(i), Name = "Device " + i }));
            var limited = new DeviceAccessStore(directory);
            limited.Set(Mac(127), "Last device", DeviceAccessState.Blocked);
            bool fullRejected = false;
            try { limited.Set(Mac(128), "Excess device", DeviceAccessState.Approved); } catch (InvalidOperationException) { fullRejected = true; }
            limited.Set(Mac(0), "Renamed within cap", DeviceAccessState.Blocked);
            check(fullRejected && limited.Entries.Count == 128 && !limited.IsApproved(Mac(128)) && limited.GetState(Mac(0)) == DeviceAccessState.Blocked,
                "Device access caps registered entries at 128 while allowing updates to existing devices");
            limited.Set(Mac(1), "", DeviceAccessState.Pending);
            limited.Set(Mac(128), "Replacement device", DeviceAccessState.Approved);
            check(new DeviceAccessStore(directory).Entries.Count == 128 && limited.IsApproved(Mac(128)), "Removing a registered device frees one slot for a new decision");
            WriteDocument(Enumerable.Range(0, 129).Select(i => permission with { Mac = Mac(i) }));
            check(new DeviceAccessStore(directory) is { LastError: not null, Entries.Count: 0 }, "Device access rejects oversized persisted permission collections");

            string blockedPath = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(blockedPath, "test");
            var unavailable = new DeviceAccessStore(blockedPath);
            bool unavailableFailed = false;
            try { unavailable.Set(alice, "Ana", DeviceAccessState.Approved); } catch (IOException) { unavailableFailed = true; }
            check(unavailableFailed && unavailable.LastError is not null && unavailable.Entries.Count == 0 && !unavailable.IsApproved(alice),
                "Device access storage failures remain visible and cannot approve devices in memory");
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("HenHotspot.DeviceAccessTests-", StringComparison.Ordinal)) Directory.Delete(resolved, recursive: true);
        }
        return Task.CompletedTask;
    }
}
