using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HenHotspot;

public sealed record DnsFilterCommand(string Operation, bool AllowOnly = false, string[]? Domains = null, string Language = "es");
public sealed record DnsFilterReply(bool Active, string? Error, long Queries = 0, long Blocked = 0,
    string? LastDomain = null, string? LastClient = null, DnsObservation[]? Observations = null,
    long Malformed = 0, long Unrecorded = 0);

public sealed class DnsFilterClient(Action<DnsObservation>? observer = null) : IAsyncDisposable
{
    private NamedPipeServerStream? pipe;
    private Process? helper;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private Task? heartbeat;
    private volatile bool stopping;
    private DnsFilterReply snapshot = new(false, null);
    public DnsFilterReply Snapshot => Volatile.Read(ref snapshot);

    public async Task StartAsync(DomainPolicy policy)
    {
        if (pipe is not null) throw new InvalidOperationException(L10n.T("TheFilterHasAlreadyStarted"));
        string name = "HenHotspot.Dns." + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--dns-filter"); start.ArgumentList.Add(name);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            helper = await Task.Run(() => Process.Start(start))
                ?? throw new InvalidOperationException(L10n.T("CouldNotOpenTheFilterWithAdministratorPermission"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != helper.Id)
                throw new InvalidOperationException(L10n.T("CouldNotVerifyTheFilterProcess"));
            await ApplyAsync(policy);
            heartbeat = HeartbeatAsync();
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task ApplyAsync(DomainPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (stopping) throw new InvalidOperationException(L10n.T("TheFilterIsStopping"));
        await ExchangeAsync(new("apply", policy.AllowOnly, policy.Domains));
        if (!Snapshot.Active) throw new InvalidOperationException(Snapshot.Error ?? L10n.T("TheFilterDidNotStart"));
    }

    private async Task ExchangeAsync(DnsFilterCommand command)
    {
        await gate.WaitAsync(lifetime.Token);
        DnsObservation[] received = [];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await GuardWire.SendAsync(pipe ?? throw new ObjectDisposedException(nameof(DnsFilterClient)), command with { Language = L10n.LanguageCode }, timeout.Token);
            var reply = await GuardWire.ReceiveAsync<DnsFilterReply>(pipe, timeout.Token);
            Volatile.Write(ref snapshot, reply with { Observations = null });
            received = reply.Observations ?? [];
        }
        catch (Exception ex)
        {
            // A timeout may leave a late response queued. Never reuse this stream
            // or present the old policy as active after an uncertain exchange.
            Volatile.Write(ref snapshot, Snapshot with { Active = false, Error = L10n.T("FilterConfirmationLost") + ex.Message });
            pipe?.Dispose(); lifetime.Cancel();
            try { await new HotspotService().StopAsync(); } catch { }
            throw;
        }
        finally { gate.Release(); }
        // Recording failures do not change the network decision or poison IPC.
        foreach (var observation in received) { try { observer?.Invoke(observation); } catch { } }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (stopping) return;
                await ExchangeAsync(new("status"));
                if (!Snapshot.Active) return;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (stopping) return;
            Volatile.Write(ref snapshot, Snapshot with { Active = false, Error = L10n.T("FilterConnectionLost") + ex.Message });
            try { await new HotspotService().StopAsync(); } catch { }
            pipe?.Dispose();
        }
    }

    public async Task StopAsync()
    {
        if (stopping) return;
        stopping = true;
        try
        {
            if (pipe?.IsConnected == true)
            {
                await ExchangeAsync(new("stop"));
                if (Snapshot.Error is not null) throw new InvalidOperationException(Snapshot.Error);
            }
        }
        finally { await DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        stopping = true;
        lifetime.Cancel(); pipe?.Dispose();
        if (heartbeat is not null) { try { await heartbeat; } catch { } }
        helper?.Dispose();
        Volatile.Write(ref snapshot, Snapshot with { Active = false });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}

public static class DnsFilterHelper
{
    public static async Task<int> RunAsync(string pipeName, int parentId)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
        if (!pipeName.StartsWith("HenHotspot.Dns.", StringComparison.Ordinal) || pipeName.Length != 47 || parentId <= 0) return 2;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        DnsFilterEngine? engine = null;
        GuardInterface? network = null;
        bool stopped = false;
        var observations = new ConcurrentQueue<DnsObservation>();
        DnsObservation? last = null;
        long unrecorded = 0;
        void Observe(DnsObservation observation)
        {
            Volatile.Write(ref last, observation);
            observations.Enqueue(observation);
            while (observations.Count > 256 && observations.TryDequeue(out _)) Interlocked.Increment(ref unrecorded);
        }
        DnsFilterReply Reply()
        {
            var data = engine?.Snapshot();
            var events = new List<DnsObservation>();
            while (events.Count < 24 && observations.TryDequeue(out var observation)) events.Add(observation);
            var latest = Volatile.Read(ref last);
            return new(data?.Active == true, data?.Error, data?.Queries ?? 0, data?.Blocked ?? 0,
                latest?.Domain, latest?.ClientIp, events.ToArray(), data?.Malformed ?? 0, Interlocked.Read(ref unrecorded));
        }
        try
        {
            await pipe.ConnectAsync(30000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != parentId) return 3;
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 3;
            LegacyProxyCleanup.RemoveOwnedRule();
            while (true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var command = await GuardWire.ReceiveAsync<DnsFilterCommand>(pipe, timeout.Token);
                L10n.SetLanguage(command.Language);
                if (command.Operation == "stop")
                {
                    if (engine is not null) await engine.StopAsync();
                    stopped = true;
                    await GuardWire.SendAsync(pipe, Reply() with { Active = false, Error = null }, timeout.Token);
                    return 0;
                }
                if (command.Operation is not ("apply" or "status")) throw new InvalidDataException(L10n.T("InvalidFilterRequest"));
                var status = await new HotspotService().ReadAsync();
                if (status.State != "On" || status.Error is not null) throw new InvalidOperationException(L10n.T("TheHotspotMustBeOnToFilterDomains"));
                var current = DeviceGuardPolicy.FindInterface();
                if (network is not null && network != current) throw new InvalidOperationException(L10n.T("TheHotspotInterfaceChangedEnableTheFilterAgain"));
                if (engine?.Error is string error) throw new InvalidOperationException(error);
                if (command.Operation == "apply")
                {
                    if (command.Domains is null || command.Domains.Length > 128) throw new InvalidDataException(L10n.T("UpTo128DomainsPerProfile"));
                    var policy = new DomainPolicy(command.AllowOnly, command.Domains);
                    if (engine is null)
                    {
                        network = current;
                        engine = new DnsFilterEngine(current, policy, Observe);
                        engine.Start();
                    }
                    else engine.Apply(policy);
                }
                if (engine is null || !engine.Snapshot().Active) throw new InvalidOperationException(L10n.T("TheFilterIsNotRunning"));
                await GuardWire.SendAsync(pipe, Reply(), timeout.Token);
            }
        }
        catch (Exception ex)
        {
            if (engine is not null)
            {
                try { await new HotspotService().StopAsync(); } catch { }
                try { await engine.StopAsync(); } catch { }
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await GuardWire.SendAsync(pipe, Reply() with { Active = false, Error = ex.Message }, timeout.Token);
            }
            catch { }
            return 1;
        }
        finally
        {
            if (engine is not null)
            {
                if (!stopped) { try { await new HotspotService().StopAsync(); } catch { } }
                await engine.DisposeAsync();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
