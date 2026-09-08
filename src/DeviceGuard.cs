using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace HenHotspot;

public sealed record GuardCommand(string Operation, string[] ApprovedMacs);
public sealed record GuardReply(bool Active, string? Error, int FilterCount, string? InterfaceId, string[] ApprovedAddresses, List<ClientInfo> Clients);

public static class GuardWire
{
    public const int MaxLength = 65536;
    public static async Task SendAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > MaxLength) throw new InvalidDataException("El mensaje del control de acceso excede su límite.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(data, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReceiveAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxLength) throw new InvalidDataException("Mensaje de acceso no válido.");
        byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Mensaje de acceso vacío.");
    }
}

public sealed class DeviceGuardClient : IAsyncDisposable
{
    private NamedPipeServerStream? pipe;
    private Process? helper;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private Task? heartbeat;
    private volatile bool releasing;
    private string[] approved = [];
    private GuardReply snapshot = new(false, null, 0, null, [], []);
    public GuardReply Snapshot => Volatile.Read(ref snapshot);
    public bool Active => Snapshot.Active;

    public async Task StartAsync(IEnumerable<string> macs)
    {
        if (pipe is not null) throw new InvalidOperationException("El control ya se inició.");
        approved = Normalize(macs);
        string name = "HenHotspot.Guard." + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--device-guard"); start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString());
            helper = await Task.Run(() => Process.Start(start)) ?? throw new InvalidOperationException("No se abrió el permiso de administrador.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != helper.Id)
                throw new InvalidOperationException("No se pudo verificar el proceso del control de acceso.");
            await UpdateApprovedAsync(approved);
            if (!Active) throw new InvalidOperationException(Snapshot.Error ?? "El control no se activó.");
            heartbeat = HeartbeatAsync();
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task UpdateApprovedAsync(IEnumerable<string> macs)
    {
        var next = Normalize(macs);
        Volatile.Write(ref approved, next);
        await ExchangeAsync(new("apply", next));
        if (!Snapshot.Active) throw new InvalidOperationException(Snapshot.Error ?? "El control de acceso se detuvo.");
    }
    private static string[] Normalize(IEnumerable<string> values)
    {
        var result = values.Select(DeviceAccessStore.NormalizeMac).Distinct().Order().ToArray();
        if (result.Length > 128) throw new ArgumentException("Máximo 128 dispositivos autorizados.");
        return result;
    }
    private async Task ExchangeAsync(GuardCommand command)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (command.Operation == "apply" && releasing) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            if (command.Operation == "apply") command = command with { ApprovedMacs = Volatile.Read(ref approved) };
            await GuardWire.SendAsync(pipe ?? throw new ObjectDisposedException(nameof(DeviceGuardClient)), command, timeout.Token);
            var reply = await GuardWire.ReceiveAsync<GuardReply>(pipe, timeout.Token);
            Volatile.Write(ref snapshot, reply);
        }
        finally { gate.Release(); }
    }
    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                await ExchangeAsync(new("apply", Volatile.Read(ref approved)));
                if (!Active) break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (releasing) return;
            Volatile.Write(ref snapshot, Snapshot with { Active = false, Error = "Se perdió el control de acceso: " + ex.Message });
            // Prevent an app/helper failure from silently leaving the hotspot open.
            try { await new HotspotService().StopAsync(); } catch { }
            pipe?.Dispose();
        }
    }
    public async Task ReleaseAsync()
    {
        releasing = true;
        if (pipe?.IsConnected == true)
        {
            await ExchangeAsync(new("release", []));
            if (Snapshot.Error is not null) throw new InvalidOperationException(Snapshot.Error);
        }
        await DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); pipe?.Dispose();
        if (heartbeat is not null) { try { await heartbeat; } catch { } }
        helper?.Dispose();
        Volatile.Write(ref snapshot, Snapshot with { Active = false });
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}

public static class DeviceGuardHelper
{
    public static async Task<int> RunAsync(string pipeName, int parentId)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
        if (!pipeName.StartsWith("HenHotspot.Guard.", StringComparison.Ordinal) || pipeName.Length != 49 || parentId <= 0) return 2;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        NativeWfp? filter = null;
        bool released = false;
        try
        {
            await pipe.ConnectAsync(30000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != parentId) return 3;
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 3;
            GuardPlan? previous = null;
            while (true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(14));
                var command = await GuardWire.ReceiveAsync<GuardCommand>(pipe, timeout.Token);
                if (command.Operation == "release")
                {
                    filter?.Dispose(); filter = null; released = true;
                    await GuardWire.SendAsync(pipe, new GuardReply(false, null, 0, null, [], []), timeout.Token);
                    return 0;
                }
                if (command.Operation != "apply" || command.ApprovedMacs is null || command.ApprovedMacs.Length > 128)
                    throw new InvalidDataException("Solicitud de acceso no válida.");
                var status = await new HotspotService().ReadAsync();
                if (status.State != "On" || status.Error is not null) throw new InvalidOperationException("El hotspot debe estar encendido y disponible.");
                var network = DeviceGuardPolicy.FindInterface();
                if (previous is not null && previous.Network != network)
                    throw new InvalidOperationException("Cambió la interfaz del hotspot. Vuelve a activar el control.");
                var plan = DeviceGuardPolicy.Build(network, status.Clients, command.ApprovedMacs);
                filter ??= new NativeWfp();
                if (previous is null || !previous.ApprovedAddresses.SequenceEqual(plan.ApprovedAddresses)) filter.Apply(plan);
                filter.Verify(); previous = plan;
                await GuardWire.SendAsync(pipe, new GuardReply(true, null, filter.FilterCount, network.Id, plan.ApprovedAddresses, status.Clients), timeout.Token);
            }
        }
        catch (Exception ex)
        {
            if (filter is not null)
            {
                try { await new HotspotService().StopAsync(); } catch { }
                filter.Dispose(); filter = null;
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await GuardWire.SendAsync(pipe, new GuardReply(false, ex.Message, 0, null, [], []), timeout.Token);
            }
            catch { }
            return 1;
        }
        finally
        {
            if (filter is not null && !released) { try { await new HotspotService().StopAsync(); } catch { } }
            filter?.Dispose();
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
