using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HenHotspot;

public sealed record DnsObservation(DateTimeOffset At, string ClientIp, string Domain, bool Allowed);
public sealed record DnsFilterSnapshot(bool Active, long Queries, long Allowed, long Blocked, long Malformed, string? Error);

// Experimental DNS filter for the ICS resolver on the local hotspot gateway.
// It does not intercept forwarded packets, cached addresses, DoH, DoT or VPNs.
// TCP DNS to this gateway is blocked while active because stream parsing is not implemented.
public sealed class DnsFilterEngine : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly GuardInterface network;
    private readonly Action<DnsObservation>? observed;
    private readonly uint gateway, mask, subnet, broadcast;
    private DomainPolicy policy;
    private IntPtr udpHandle, tcpHandle;
    private Task? worker, stopTask;
    private bool started, stopping;
    private int active, releaseRequested;
    private string? error;
    private long queries, allowed, blocked, malformed;

    public DnsFilterEngine(GuardInterface network, DomainPolicy policy, Action<DnsObservation>? observed = null)
    {
        ArgumentNullException.ThrowIfNull(network); ArgumentNullException.ThrowIfNull(policy);
        ValidateNetwork(network);
        this.network = network; this.policy = policy; this.observed = observed;
        gateway = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(network.Address).GetAddressBytes());
        mask = uint.MaxValue << (32 - network.PrefixLength);
        subnet = gateway & mask; broadcast = gateway | ~mask;
    }

    public string? Error => Volatile.Read(ref error);
    public DnsFilterSnapshot Snapshot() => new(Volatile.Read(ref active) == 1, Interlocked.Read(ref queries),
        Interlocked.Read(ref allowed), Interlocked.Read(ref blocked), Interlocked.Read(ref malformed), Error);

    public static (string Udp, string Tcp) BuildFilters(GuardInterface network)
    {
        ValidateNetwork(network);
        string ip = IPAddress.Parse(network.Address).ToString();
        string index = network.Index.ToString(CultureInfo.InvariantCulture);
        // NETWORK inbound is local delivery to ICS, not its NAT forwarding path.
        string scope = $"inbound and !loopback and !impostor and ip and ifIdx == {index} and ip.DstAddr == {ip}";
        return ($"{scope} and udp and udp.DstPort == 53", $"{scope} and tcp and tcp.DstPort == 53");
    }

    public void Start()
    {
        lock (gate)
        {
            if (stopping) throw new ObjectDisposedException(nameof(DnsFilterEngine));
            if (started) throw new InvalidOperationException(L10n.T("TheDNSFilterHasAlreadyStartedCreateANew"));
            started = true;
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException(L10n.T("TheDNSFilterRequires64BitWindows"));
            try
            {
                var filters = BuildFilters(network);
                udpHandle = NativeWinDivert.Open(filters.Udp, NativeWinDivert.NetworkLayer, 90, 0);
                if (udpHandle == NativeWinDivert.InvalidHandle)
                {
                    udpHandle = IntPtr.Zero;
                    throw NativeWinDivert.Failure(L10n.T("EnableTheDNSFilter"));
                }
                SetQueueParameter(0, 512); SetQueueParameter(1, 1000); SetQueueParameter(2, 1024 * 1024);
                tcpHandle = NativeWinDivert.Open(filters.Tcp, NativeWinDivert.NetworkLayer, 90, NativeWinDivert.Drop);
                if (tcpHandle == NativeWinDivert.InvalidHandle)
                {
                    tcpHandle = IntPtr.Zero;
                    throw NativeWinDivert.Failure(L10n.T("CloseUnfilteredTCPDNS"));
                }
                IntPtr handle = udpHandle;
                Volatile.Write(ref active, 1);
                worker = Task.Factory.StartNew(() => ReceiveLoop(handle), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref active, 0);
                CloseOwnedHandles();
                var failure = ex is DllNotFoundException or BadImageFormatException
                    ? new InvalidOperationException(L10n.T("CouldNotLoadWinDivertReinstallHenWithItsFilter"), ex)
                    : ex;
                Volatile.Write(ref error, failure.Message);
                throw failure;
            }
        }
    }

    public void Apply(DomainPolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            if (stopping) throw new ObjectDisposedException(nameof(DnsFilterEngine));
            Volatile.Write(ref policy, value);
        }
    }

    private void SetQueueParameter(int parameter, ulong value)
    {
        if (!NativeWinDivert.SetParam(udpHandle, parameter, value))
            throw NativeWinDivert.Failure(L10n.T("ConfigureTheDNSQueue"));
    }

    private void ReceiveLoop(IntPtr handle)
    {
        byte[] packet = new byte[65535];
        try
        {
            while (true)
            {
                if (!NativeWinDivert.Receive(handle, packet, (uint)packet.Length, out uint length, out var address))
                {
                    int code = Marshal.GetLastWin32Error();
                    if (Volatile.Read(ref releaseRequested) == 1 && code is NativeWinDivert.NoData or 6 or 995) return;
                    throw NativeWinDivert.Failure(L10n.T("ReceiveADNSQuery"), code);
                }
                if (Volatile.Read(ref releaseRequested) == 1)
                {
                    // Explicit release lets already queued packets return to normal Windows handling.
                    SendPacket(handle, packet, length, ref address);
                    continue;
                }
                if (length > packet.Length || address.InterfaceIndex != network.Index ||
                    (address.Flags & ((1U << 17) | (1U << 18) | (1U << 19) | (1U << 20))) != 0 ||
                    !DnsPacket.TryReadQuestion(packet.AsSpan(0, (int)length), out var question) || question is null ||
                    question.ServerIp != network.Address || !IsClientAddress(question.ClientIp))
                {
                    Interlocked.Increment(ref malformed);
                    continue; // Never forward an unparsed query from this narrowly scoped handle.
                }
                Interlocked.Increment(ref queries);
                bool permit = Volatile.Read(ref policy).Allows(question.Domain);
                if (permit)
                {
                    SendPacket(handle, packet, length, ref address);
                    Interlocked.Increment(ref allowed);
                }
                else
                {
                    byte[] response = DnsPacket.CreateNameError(question);
                    var replyAddress = new NativeWinDivert.Address
                    {
                        Flags = NativeWinDivert.OutboundFlag,
                        InterfaceIndex = address.InterfaceIndex,
                        SubInterfaceIndex = address.SubInterfaceIndex
                    };
                    if (!NativeWinDivert.CalculateChecksums(response, (uint)response.Length, ref replyAddress, 0))
                        throw new InvalidOperationException(L10n.T("CouldNotPrepareTheDNSFilterResponse"));
                    SendPacket(handle, response, (uint)response.Length, ref replyAddress);
                    Interlocked.Increment(ref blocked);
                }
                // A recording failure must not change a packet decision. The callback should enqueue
                // small observations rather than perform database or UI work on the receive thread.
                try { observed?.Invoke(new(DateTimeOffset.UtcNow, question.ClientIp, question.Domain, permit)); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref releaseRequested) == 0) Volatile.Write(ref error, ex.Message);
            // Keep the owned handles until the helper observes Error, stops the hotspot,
            // and explicitly releases them. A worker fault must not silently fail open.
        }
        finally { Volatile.Write(ref active, 0); }
    }

    private bool IsClientAddress(string address)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(address).GetAddressBytes());
        return (value & mask) == subnet && value != subnet && value != broadcast && value != gateway;
    }

    private static void SendPacket(IntPtr handle, byte[] packet, uint length, ref NativeWinDivert.Address address)
    {
        if (!NativeWinDivert.Send(handle, packet, length, out uint sent, ref address))
            throw NativeWinDivert.Failure(L10n.T("DeliverADNSResponseOrQuery"));
        if (sent != length) throw new InvalidOperationException(L10n.T("WinDivertDidNotDeliverTheCompleteDNSPacket"));
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            if (stopTask is not null) return stopTask;
            stopping = true;
            Volatile.Write(ref active, 0); Volatile.Write(ref releaseRequested, 1);
            return stopTask = StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        IntPtr handle = udpHandle;
        try
        {
            if (handle == IntPtr.Zero) return;
            if (!NativeWinDivert.Shutdown(handle, 1))
            {
                Volatile.Write(ref error, NativeWinDivert.Failure(L10n.T("StopReceivingDNSQueries")).Message);
                // Closing also releases an outstanding blocking receive.
                CloseOwnedHandles();
            }
            if (worker is not null)
            {
                if (await Task.WhenAny(worker, Task.Delay(2000)).ConfigureAwait(false) != worker)
                    CloseOwnedHandles();
                await Task.WhenAny(worker, Task.Delay(1000)).ConfigureAwait(false);
            }
            if (udpHandle != IntPtr.Zero && (worker is null || worker.IsCompleted))
            {
                // The worker may have failed before shutdown. Drain only after it has exited.
                Task drain = Task.Run(() => DrainReleasedQueue(handle));
                await Task.WhenAny(drain, Task.Delay(1000)).ConfigureAwait(false);
            }
        }
        finally { CloseOwnedHandles(); }
    }

    private static void DrainReleasedQueue(IntPtr handle)
    {
        byte[] packet = new byte[65535];
        long start = Stopwatch.GetTimestamp();
        try
        {
            for (int count = 0; count < 512 && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(1); count++)
            {
                if (!NativeWinDivert.Receive(handle, packet, (uint)packet.Length, out uint length, out var address)) break;
                if (!NativeWinDivert.Send(handle, packet, length, out _, ref address)) break;
            }
        }
        catch { }
    }

    private void CloseOwnedHandles()
    {
        IntPtr udp = Interlocked.Exchange(ref udpHandle, IntPtr.Zero);
        IntPtr tcp = Interlocked.Exchange(ref tcpHandle, IntPtr.Zero);
        if (udp != IntPtr.Zero) NativeWinDivert.Close(udp);
        if (tcp != IntPtr.Zero) NativeWinDivert.Close(tcp);
    }

    private static void ValidateNetwork(GuardInterface network)
    {
        ArgumentNullException.ThrowIfNull(network);
        if (network.Index == 0 || network.Luid == 0 || network.PrefixLength is < 16 or > 30 ||
            !IPAddress.TryParse(network.Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            ip.ToString() != network.Address)
            throw new ArgumentException(L10n.T("TheHotspotIPv4InterfaceIsInvalidForTheDNS"));
        uint value = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        uint networkMask = uint.MaxValue << (32 - network.PrefixLength);
        if (value == (value & networkMask) || value == (value | ~networkMask) || (value >> 24) is 0 or 127 or >= 224)
            throw new ArgumentException(L10n.T("TheHotspotAddressIsNotAValidIPv4Host"));
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
