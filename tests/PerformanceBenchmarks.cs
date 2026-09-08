using System.Diagnostics;
using System.Text.Json;
using HenHotspot;
using Microsoft.Data.Sqlite;

public static class PerformanceBenchmarks
{
    // Opt-in, local storage only. Never touches the hotspot or the user's database.
    public static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "HenHotspot.Benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new ActivityStore(directory);
            var now = DateTimeOffset.UtcNow;
            ActivityEntry Entry(int index) => new("sample-" + index, now.AddTicks(index), now.AddTicks(index), null,
                "192.168.137.2", "Example phone", "example-phone", "example.com", "DNS", "Allowed", 0, 0, 0);
            for (int index = 0; index < 250; index++) store.Record(Entry(index));
            await store.FlushAsync();
            store.Clear();
            now = DateTimeOffset.UtcNow;

            using var process = Process.GetCurrentProcess();
            double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
            var clock = Stopwatch.StartNew();
            const int records = 10_000;
            for (int offset = 0; offset < records; offset += 1000)
            {
                for (int index = offset; index < offset + 1000; index++) store.Record(Entry(index));
                await store.FlushAsync();
            }
            double writeMilliseconds = clock.Elapsed.TotalMilliseconds;
            double writeCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds - cpuBefore;
            var filter = new ActivityFilter(now.AddMinutes(-1), now.AddHours(1));
            if (store.Error is not null || store.Query(filter).TotalCount != records)
                throw new InvalidOperationException("Benchmark did not persist every record: " + store.Error);

            const int reads = 50;
            cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
            clock.Restart();
            for (int index = 0; index < reads; index++)
            {
                store.Query(filter, 0, 50);
                store.GetDevices();
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                runtime = Environment.Version.ToString(),
                logicalProcessors = Environment.ProcessorCount,
                records,
                writeMilliseconds = Math.Round(writeMilliseconds, 1),
                writeCpuMilliseconds = Math.Round(writeCpuMilliseconds, 1),
                reads,
                readMilliseconds = Math.Round(clock.Elapsed.TotalMilliseconds, 1),
                readCpuMilliseconds = Math.Round(process.TotalProcessorTime.TotalMilliseconds - cpuBefore, 1)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string resolved = Path.GetFullPath(directory);
            string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("HenHotspot.Benchmark-", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
