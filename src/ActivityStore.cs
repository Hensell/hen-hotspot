using System.IO;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace HenHotspot;

public sealed record ActivityEntry(string Id, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    DateTimeOffset? LastTrafficAt, string ClientIp, string DeviceName, string DeviceId,
    string Domain, string Protocol, string Outcome, long UploadBytes, long DownloadBytes, double DurationSeconds);
public sealed record ActivityFilter(DateTimeOffset From, DateTimeOffset Until, string? DeviceId = null,
    string? Domain = null, string? Outcome = null);
public sealed record ActivityPage(IReadOnlyList<ActivityEntry> Entries, int TotalCount,
    long UploadBytes, long DownloadBytes, int BlockedCount);
public sealed record ActivityDevice(string Id, string Name, string Ip);

/// <summary>Local metadata only. Network threads enqueue snapshots without waiting for disk I/O.</summary>
public sealed class ActivityStore : IDisposable
{
    private const int MaxPending = 4096;
    private const int MaxRows = 50_000;
    private const int MaxBatchSize = 128;
    private static readonly ActivityPage EmptyPage = new([], 0, 0, 0, 0);
    private readonly string directory;
    private readonly string connectionString;
    private readonly object enqueueGate = new();
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new() { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource timerCancellation = new();
    private readonly Task worker;
    private readonly Task timer;
    private SqliteConnection? connection;
    private SqliteCommand? insertCommand;
    private volatile bool recordingEnabled = true;
    private volatile int retentionDays = 7;
    private volatile string? error;
    private bool disposed;
    private int pending;
    private long generation;
    private long revision;
    private long clearedBefore;
    private int writesSincePrune;

    public bool RecordingEnabled => recordingEnabled;
    public int RetentionDays => retentionDays;
    public string? Error => error;
    public long Generation => Interlocked.Read(ref generation);
    public long Revision => Interlocked.Read(ref revision);

    public ActivityStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenHotspot");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(this.directory, "activity.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString();
        try { Initialize(); }
        catch (Exception ex) { Fail(ex); }
        worker = Task.Run(ConsumeAsync);
        timer = Task.Run(MaintainPeriodicallyAsync);
    }

    public void Record(ActivityEntry entry) => Record(entry, Generation);

    public void Record(ActivityEntry entry, long generation)
    {
        // This lock protects only enqueue ordering with pause/clear. It never contains database work.
        try
        {
            lock (enqueueGate)
            {
                if (disposed || !recordingEnabled || generation != this.generation ||
                    entry.StartedAt.UtcTicks < clearedBefore ||
                    entry.StartedAt < DateTimeOffset.UtcNow.AddDays(-retentionDays)) return;
                if (pending >= MaxPending)
                {
                    error = L10n.T("HistoryIsReceivingTooManyConnectionsSomeRecordsWere");
                    return;
                }
                Interlocked.Increment(ref pending);
                queue.Writer.TryWrite(new Work(WorkKind.Record, Entry: entry));
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    public Task FlushAsync()
    {
        lock (enqueueGate)
        {
            if (disposed) return worker;
            var completion = NewCompletion();
            queue.Writer.TryWrite(new Work(WorkKind.Flush, Completion: completion));
            return completion.Task;
        }
    }

    public void UpdateSettings(bool enabled, int retentionDays)
    {
        if (retentionDays is not (1 or 7 or 30 or 90)) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        Task completed;
        lock (enqueueGate)
        {
            if (disposed) return;
            bool paused = recordingEnabled && !enabled;
            if (recordingEnabled != enabled) Interlocked.Increment(ref generation);
            recordingEnabled = enabled;
            this.retentionDays = retentionDays;
            var completion = NewCompletion();
            queue.Writer.TryWrite(new Work(WorkKind.Settings, Enabled: enabled, Days: retentionDays,
                At: paused ? DateTimeOffset.UtcNow : null, Completion: completion));
            completed = completion.Task;
        }
        completed.GetAwaiter().GetResult();
    }

    public void Clear()
    {
        Task completed;
        lock (enqueueGate)
        {
            if (disposed) return;
            Interlocked.Increment(ref generation);
            clearedBefore = DateTimeOffset.UtcNow.UtcTicks;
            var completion = NewCompletion();
            queue.Writer.TryWrite(new Work(WorkKind.Clear, Completion: completion));
            completed = completion.Task;
        }
        completed.GetAwaiter().GetResult();
    }

    public ActivityPage Query(ActivityFilter filter, int page = 0, int pageSize = 100)
    {
        if (filter.Until <= filter.From) return EmptyPage;
        page = Math.Clamp(page, 0, MaxRows);
        pageSize = Math.Clamp(pageSize, 1, 500);
        try
        {
            using var read = OpenRead();
            using var transaction = read.BeginTransaction(deferred: true);
            using var totals = FilterCommand(read, transaction, filter,
                "SELECT COUNT(*), COALESCE(SUM(upload_bytes),0), COALESCE(SUM(download_bytes),0), COALESCE(SUM(outcome='Blocked'),0) FROM activity");
            int count, blocked; long upload, download;
            using (var reader = totals.ExecuteReader())
            {
                reader.Read(); count = reader.GetInt32(0); upload = reader.GetInt64(1);
                download = reader.GetInt64(2); blocked = reader.GetInt32(3);
            }
            using var entries = FilterCommand(read, transaction, filter,
                "SELECT id,started_at,ended_at,last_traffic_at,client_ip,device_name,device_id,domain,protocol,outcome,upload_bytes,download_bytes,duration_seconds FROM activity");
            entries.CommandText += " ORDER BY started_at DESC,id DESC LIMIT $limit OFFSET $offset";
            entries.Parameters.AddWithValue("$limit", pageSize);
            entries.Parameters.AddWithValue("$offset", (long)page * pageSize);
            var result = new List<ActivityEntry>();
            using (var reader = entries.ExecuteReader())
                while (reader.Read()) result.Add(ReadEntry(reader));
            transaction.Commit();
            return new(result, count, upload, download, blocked);
        }
        catch (Exception ex) { Fail(ex); return EmptyPage; }
    }

    public IReadOnlyList<ActivityDevice> GetDevices()
    {
        try
        {
            using var read = OpenRead();
            using var command = read.CreateCommand();
            command.CommandText = """
                SELECT device_id,device_name,client_ip FROM
                (SELECT device_id,device_name,client_ip,
                    ROW_NUMBER() OVER (PARTITION BY device_id ORDER BY started_at DESC,id DESC) AS ordinal
                 FROM activity WHERE started_at >= $cutoff)
                WHERE ordinal=1 ORDER BY device_name COLLATE NOCASE,device_id
                """;
            command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcTicks);
            var result = new List<ActivityDevice>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return result;
        }
        catch (Exception ex) { Fail(ex); return []; }
    }

    private void Initialize()
    {
        Directory.CreateDirectory(directory);
        connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA auto_vacuum=INCREMENTAL;
            PRAGMA max_page_count=32768;
            PRAGMA journal_size_limit=4194304;
            PRAGMA wal_autocheckpoint=256;
            PRAGMA secure_delete=ON;
            CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY CHECK(id=1), enabled INTEGER NOT NULL, retention_days INTEGER NOT NULL);
            INSERT OR IGNORE INTO settings VALUES(1,1,7);
            CREATE TABLE IF NOT EXISTS activity (
                id TEXT PRIMARY KEY, started_at INTEGER NOT NULL, ended_at INTEGER, last_traffic_at INTEGER,
                client_ip TEXT NOT NULL, device_name TEXT NOT NULL, device_id TEXT NOT NULL,
                domain TEXT NOT NULL, protocol TEXT NOT NULL, outcome TEXT NOT NULL,
                upload_bytes INTEGER NOT NULL, download_bytes INTEGER NOT NULL, duration_seconds REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS activity_started_id ON activity(started_at DESC,id DESC);
            CREATE INDEX IF NOT EXISTS activity_device_started_id ON activity(device_id,started_at DESC,id DESC);
            DROP INDEX IF EXISTS activity_started;
            DROP INDEX IF EXISTS activity_device_started;
            """);
        using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "SELECT enabled,retention_days FROM settings WHERE id=1";
            using var reader = settings.ExecuteReader();
            if (reader.Read())
            {
                recordingEnabled = reader.GetInt32(0) != 0;
                int days = reader.GetInt32(1);
                retentionDays = days is 1 or 7 or 30 or 90 ? days : 7;
            }
        }
        // A process restart cannot prove that the remote connection remained open after its last snapshot.
        Execute("UPDATE activity SET outcome=CASE WHEN outcome IN ('Connecting','Allowed') THEN 'Interrupted' ELSE outcome END,ended_at=started_at+CAST(duration_seconds*10000000 AS INTEGER) WHERE ended_at IS NULL");
        Prune();
    }

    private async Task ConsumeAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (work.Kind == WorkKind.Record)
            {
                var batch = new List<ActivityEntry>(MaxBatchSize) { work.Entry! };
                Interlocked.Decrement(ref pending);
                // Never read past a pause, clear, settings or flush barrier.
                while (batch.Count < MaxBatchSize && queue.Reader.TryPeek(out var next) && next.Kind == WorkKind.Record)
                {
                    if (!queue.Reader.TryRead(out next)) break;
                    Interlocked.Decrement(ref pending);
                    batch.Add(next.Entry!);
                }
                try
                {
                    WriteBatch(batch);
                    if (writesSincePrune >= 256) Prune();
                }
                catch (Exception ex) { Fail(ex); }
                continue;
            }
            try
            {
                if (connection is null || connection.State != System.Data.ConnectionState.Open)
                    throw new IOException(L10n.T("LocalStorageIsUnavailableCloseAndReopenTheApp"));
                switch (work.Kind)
                {
                    case WorkKind.Settings:
                        using (var settings = connection.CreateCommand())
                        {
                            settings.CommandText = "UPDATE settings SET enabled=$enabled,retention_days=$days WHERE id=1";
                            settings.Parameters.AddWithValue("$enabled", work.Enabled ? 1 : 0);
                            settings.Parameters.AddWithValue("$days", work.Days);
                            settings.ExecuteNonQuery();
                        }
                        if (work.At is { } pausedAt) InterruptActive(pausedAt);
                        Prune();
                        Interlocked.Increment(ref revision);
                        break;
                    case WorkKind.Clear:
                        Execute("DELETE FROM activity;");
                        Interlocked.Increment(ref revision);
                        Execute("PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
                        break;
                    case WorkKind.Maintain:
                    case WorkKind.Flush:
                        Prune();
                        break;
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
                // Explicit user mutations must not report success if persistence failed.
                // Network snapshots and flush barriers remain fail-open.
                if (work.Kind is WorkKind.Settings or WorkKind.Clear) work.Completion?.TrySetException(ex);
            }
            finally { work.Completion?.TrySetResult(); }
        }
        insertCommand?.Dispose();
        connection?.Dispose();
    }

    private async Task MaintainPeriodicallyAsync()
    {
        using var clock = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await clock.WaitForNextTickAsync(timerCancellation.Token).ConfigureAwait(false))
                queue.Writer.TryWrite(new Work(WorkKind.Maintain));
        }
        catch (OperationCanceledException) { }
    }

    private void WriteBatch(IReadOnlyList<ActivityEntry> entries)
    {
        if (connection is null || connection.State != System.Data.ConnectionState.Open)
            throw new IOException(L10n.T("LocalStorageIsUnavailableCloseAndReopenTheApp"));
        insertCommand ??= CreateInsertCommand();
        using var transaction = connection.BeginTransaction();
        insertCommand.Transaction = transaction;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            int changed = 0;
            foreach (var entry in entries)
                if (entry.StartedAt >= cutoff) changed += Upsert(entry);
            transaction.Commit();
            // Readers invalidate their views only after the whole batch is committed.
            if (changed > 0) Interlocked.Increment(ref revision);
            writesSincePrune += changed;
        }
        finally { insertCommand.Transaction = null; }
    }

    private SqliteCommand CreateInsertCommand()
    {
        var command = connection!.CreateCommand();
        command.CommandText = """
            INSERT INTO activity VALUES($id,$start,$end,$last,$ip,$name,$device,$domain,$protocol,$outcome,$up,$down,$duration)
            ON CONFLICT(id) DO UPDATE SET ended_at=excluded.ended_at,last_traffic_at=excluded.last_traffic_at,
                device_name=excluded.device_name,domain=excluded.domain,protocol=excluded.protocol,outcome=excluded.outcome,
                upload_bytes=excluded.upload_bytes,download_bytes=excluded.download_bytes,duration_seconds=excluded.duration_seconds
            WHERE activity.ended_at IS NULL OR excluded.ended_at IS NOT NULL
            """;
        foreach (string name in new[] { "$id", "$ip", "$name", "$device", "$domain", "$protocol", "$outcome" })
            command.Parameters.Add(name, SqliteType.Text);
        foreach (string name in new[] { "$start", "$end", "$last", "$up", "$down" })
            command.Parameters.Add(name, SqliteType.Integer);
        command.Parameters.Add("$duration", SqliteType.Real);
        try { command.Prepare(); return command; }
        catch { command.Dispose(); throw; }
    }

    private int Upsert(ActivityEntry entry)
    {
        var command = insertCommand!;
        command.Parameters["$id"].Value = Clip(entry.Id, 128);
        command.Parameters["$start"].Value = entry.StartedAt.UtcTicks;
        command.Parameters["$end"].Value = (object?)entry.EndedAt?.UtcTicks ?? DBNull.Value;
        command.Parameters["$last"].Value = (object?)entry.LastTrafficAt?.UtcTicks ?? DBNull.Value;
        command.Parameters["$ip"].Value = Clip(entry.ClientIp, 64);
        command.Parameters["$name"].Value = Clip(entry.DeviceName, 128);
        command.Parameters["$device"].Value = Clip(string.IsNullOrWhiteSpace(entry.DeviceId) ? entry.ClientIp : entry.DeviceId, 128);
        var domain = entry.Domain.Trim().TrimEnd('.').ToLowerInvariant();
        // Defense in depth: a mistaken URL passed by a caller must never write paths or queries to history.
        if (domain.IndexOfAny(['/', '?', '#', '\\', '\r', '\n', '@']) >= 0) domain = "";
        command.Parameters["$domain"].Value = Clip(domain, 253);
        command.Parameters["$protocol"].Value = entry.Protocol is "HTTP" or "HTTPS" or "DNS" ? entry.Protocol : "Unknown";
        command.Parameters["$outcome"].Value = entry.Outcome is "Connecting" or "Allowed" or "Blocked" or "Error" or "Interrupted" ? entry.Outcome : "Error";
        // The per-row upper bound also keeps SUM over MaxRows within Int64.
        command.Parameters["$up"].Value = Math.Clamp(entry.UploadBytes, 0, 100_000_000_000_000);
        command.Parameters["$down"].Value = Math.Clamp(entry.DownloadBytes, 0, 100_000_000_000_000);
        command.Parameters["$duration"].Value = double.IsFinite(entry.DurationSeconds) ? Math.Clamp(entry.DurationSeconds, 0, 90 * 86400) : 0;
        return command.ExecuteNonQuery();
    }

    private void InterruptActive(DateTimeOffset at)
    {
        using var command = connection!.CreateCommand();
        command.CommandText = "UPDATE activity SET outcome=CASE WHEN outcome IN ('Connecting','Allowed') THEN 'Interrupted' ELSE outcome END,ended_at=MAX(started_at,$at),duration_seconds=MAX(0,($at-started_at)/10000000.0) WHERE ended_at IS NULL";
        command.Parameters.AddWithValue("$at", at.UtcTicks);
        command.ExecuteNonQuery();
    }

    private void Prune()
    {
        writesSincePrune = 0;
        using var command = connection!.CreateCommand();
        command.CommandText = "DELETE FROM activity WHERE started_at < $cutoff";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcTicks);
        int deleted = command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "DELETE FROM activity WHERE id IN (SELECT id FROM activity ORDER BY started_at DESC,id DESC LIMIT -1 OFFSET $max)";
        command.Parameters.AddWithValue("$max", MaxRows);
        deleted += command.ExecuteNonQuery();
        if (deleted > 0) Interlocked.Increment(ref revision);
        Execute("PRAGMA incremental_vacuum(256)");
    }

    private SqliteConnection OpenRead()
    {
        var read = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
        try { read.Open(); return read; }
        catch { read.Dispose(); throw; }
    }

    private SqliteCommand FilterCommand(SqliteConnection read, SqliteTransaction transaction, ActivityFilter filter, string select)
    {
        var command = read.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = select + " WHERE started_at >= $from AND started_at < $until";
        command.Parameters.AddWithValue("$from", Math.Max(filter.From.UtcTicks, DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcTicks));
        command.Parameters.AddWithValue("$until", filter.Until.UtcTicks);
        if (!string.IsNullOrWhiteSpace(filter.DeviceId))
        {
            command.CommandText += " AND device_id=$device";
            command.Parameters.AddWithValue("$device", filter.DeviceId);
        }
        if (!string.IsNullOrWhiteSpace(filter.Domain))
        {
            command.CommandText += " AND instr(lower(domain),$domain)>0";
            command.Parameters.AddWithValue("$domain", filter.Domain.Trim().ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(filter.Outcome))
        {
            command.CommandText += " AND outcome=$outcome";
            command.Parameters.AddWithValue("$outcome", filter.Outcome);
        }
        return command;
    }

    private static ActivityEntry ReadEntry(SqliteDataReader row) => new(row.GetString(0), At(row.GetInt64(1)),
        row.IsDBNull(2) ? null : At(row.GetInt64(2)), row.IsDBNull(3) ? null : At(row.GetInt64(3)),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9),
        row.GetInt64(10), row.GetInt64(11), row.GetDouble(12));
    private static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);
    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Execute(string sql) { using var command = connection!.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private void Fail(Exception ex) => error = L10n.T("CouldNotAccessLocalHistoryFilteringIsStillRunning") + ex.Message;

    public void Dispose()
    {
        lock (enqueueGate)
        {
            if (disposed) return;
            disposed = true;
            Interlocked.Increment(ref generation);
            timerCancellation.Cancel();
            queue.Writer.TryComplete();
        }
        worker.GetAwaiter().GetResult();
        timer.GetAwaiter().GetResult();
        timerCancellation.Dispose();
    }

    private enum WorkKind { Record, Flush, Settings, Clear, Maintain }
    private sealed record Work(WorkKind Kind, ActivityEntry? Entry = null, bool Enabled = true, int Days = 7,
        DateTimeOffset? At = null, TaskCompletionSource? Completion = null);
}
