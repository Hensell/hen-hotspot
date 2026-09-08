using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using HenHotspot;

public static class DeviceGuardTests
{
    public static async Task Run(Action<bool, string> check)
    {
        const string approvedMac = "02:00:00:00:00:01";
        const string pendingMac = "02:11:22:33:44:55";
        var network = new GuardInterface("test-interface", 42, 123456, "Test hotspot", "192.168.137.1", 24);
        ClientInfo Client(string mac, params string[] addresses) => new(mac, "Test client", addresses);
        var connected = new[] { Client(approvedMac, "192.168.137.10"), Client(pendingMac, "192.168.137.88") };

        check(DeviceGuardPolicy.Build(network, connected, []).ApprovedAddresses.Length == 0,
            "Device guard denies all connected devices when no MAC has explicit approval");
        var approved = DeviceGuardPolicy.Build(network, connected, [approvedMac]);
        check(approved.Network == network && approved.ApprovedAddresses.SequenceEqual(["192.168.137.10"]),
            "Device guard grants only the connected IPv4 address belonging to the approved MAC on the hotspot interface");
        check(DeviceGuardPolicy.Build(network, connected, ["02-00-00-00-00-01", approvedMac]).ApprovedAddresses.SequenceEqual(["192.168.137.10"]),
            "Device guard normalizes and deduplicates approved MAC identities");
        check(DeviceGuardPolicy.Build(network,
            [Client(approvedMac, "192.168.137.10", "10.0.0.2", "192.168.138.2", "8.8.8.8", "2001:db8::87", "::ffff:192.168.137.10", "not-an-address")],
            [approvedMac]).ApprovedAddresses.SequenceEqual(["192.168.137.10"]),
            "Device guard excludes other subnets, public addresses, malformed addresses and all IPv6 including mapped IPv4");
        check(DeviceGuardPolicy.Build(network,
            [Client(approvedMac, "192.168.137.0", "192.168.137.1", "192.168.137.255")], [approvedMac]).ApprovedAddresses.Length == 0,
            "Device guard never grants subnet network, laptop gateway or broadcast addresses");
        var narrow = network with { Address = "192.168.137.9", PrefixLength = 30 };
        check(DeviceGuardPolicy.Build(narrow,
            [Client(approvedMac, "192.168.137.8", "192.168.137.9", "192.168.137.10", "192.168.137.11", "192.168.137.12")],
            [approvedMac]).ApprovedAddresses.SequenceEqual(["192.168.137.10"]),
            "Device guard computes subnet and broadcast exclusions from the actual prefix instead of assuming slash 24");

        var ambiguous = new[] { Client(approvedMac, "192.168.137.10"), Client(pendingMac, "192.168.137.10") };
        check(DeviceGuardPolicy.Build(network, ambiguous, [approvedMac]).ApprovedAddresses.Length == 0,
            "Device guard denies an IP claimed by both approved and unapproved MACs");
        check(DeviceGuardPolicy.Build(network, ambiguous, [approvedMac, pendingMac]).ApprovedAddresses.Length == 0,
            "Device guard denies ambiguous IP ownership even when both MACs are approved");
        check(DeviceGuardPolicy.Build(network,
            [Client(approvedMac, "192.168.137.10", "192.168.137.10"), Client("02-00-00-00-00-01", "192.168.137.10")],
            [approvedMac]).ApprovedAddresses.SequenceEqual(["192.168.137.10"]),
            "Repeated observations of the same normalized MAC and IP do not create false ambiguity or duplicate grants");
        check(DeviceGuardPolicy.Build(network,
            [Client("00:00:00:00:00:00", "192.168.137.90"), Client("01:11:22:33:44:55", "192.168.137.91"), Client("invalid", "192.168.137.92")],
            [approvedMac]).ApprovedAddresses.Length == 0,
            "Device guard never grants IPv4 addresses reported by invalid or multicast client identities");
        check(DeviceGuardPolicy.Build(network,
            [Client(approvedMac, "192.168.137.10"), Client("invalid", "192.168.137.10")], [approvedMac]).ApprovedAddresses.Length == 0,
            "An invalid client identity sharing an approved IP still causes ownership to be denied");
        bool invalidApprovalRejected = false;
        try { DeviceGuardPolicy.Build(network, connected, [approvedMac, "invalid"]); }
        catch (ArgumentException) { invalidApprovalRejected = true; }
        check(invalidApprovalRejected, "Device guard rejects a corrupt approval list before producing any permit plan");
        check(DeviceGuardPolicy.Build(network, [new ClientInfo(approvedMac, "No address")], [approvedMac]).ApprovedAddresses.Length == 0,
            "Device guard grants nothing until an approved client has a usable observed IPv4 address");

        var revoked = DeviceGuardPolicy.Build(network, connected, []);
        check(approved.ApprovedAddresses.Contains("192.168.137.10") && revoked.ApprovedAddresses.Length == 0,
            "Revoking a MAC removes its previously permitted IP from the next complete plan");
        var changed = DeviceGuardPolicy.Build(network, [Client(approvedMac, "192.168.137.99")], [approvedMac]);
        check(changed.ApprovedAddresses.SequenceEqual(["192.168.137.99"]) && !changed.ApprovedAddresses.Contains("192.168.137.10"),
            "Device guard replaces an old IP grant when the approved device obtains a different observed address");
        check(DeviceGuardPolicy.Build(network, [], [approvedMac]).ApprovedAddresses.Length == 0,
            "Device guard removes grants for disconnected clients while keeping the saved MAC approval external to the plan");
        bool tooManyRejected = false;
        try { DeviceGuardPolicy.Build(network, connected, Enumerable.Range(0, 129).Select(i => $"02:10:20:30:{i / 256:x2}:{i % 256:x2}")); }
        catch (ArgumentException) { tooManyRejected = true; }
        check(tooManyRejected, "Device guard rejects more than 128 distinct approved devices");

        foreach (var invalidNetwork in new[] { network with { Address = "2001:db8::1" }, network with { Address = "invalid" },
            network with { PrefixLength = 0 }, network with { PrefixLength = 15 }, network with { PrefixLength = 31 },
            network with { Index = 0 }, network with { Luid = 0 } })
        {
            bool rejected = false;
            try { DeviceGuardPolicy.Build(invalidNetwork, connected, [approvedMac]); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Device guard rejects an invalid interface address, prefix or native identity before building permits");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var command = new GuardCommand("apply", [approvedMac, pendingMac]);
        using (var stream = new MemoryStream())
        {
            await GuardWire.SendAsync(stream, command, token);
            byte[] frame = stream.ToArray();
            check(BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4)) == frame.Length - 4,
                "Device guard wire prefixes serialized bytes with the actual little-endian payload length");
            stream.Position = 0;
            var restored = await GuardWire.ReceiveAsync<GuardCommand>(stream, token);
            check(restored.Operation == "apply" && restored.ApprovedMacs.SequenceEqual(command.ApprovedMacs) && stream.Position == stream.Length,
                "Device guard command roundtrip preserves the exact explicit approval list");
        }
        using (var stream = new MemoryStream())
        {
            var reply = new GuardReply(true, null, 10, network.Id, ["192.168.137.10"], [Client(approvedMac, "192.168.137.10")]);
            await GuardWire.SendAsync(stream, reply, token);
            await GuardWire.SendAsync(stream, new GuardCommand("release", []), token);
            using var segmented = new SegmentedMemoryStream(stream.ToArray());
            var restored = await GuardWire.ReceiveAsync<GuardReply>(segmented, token);
            var release = await GuardWire.ReceiveAsync<GuardCommand>(segmented, token);
            check(restored.Active && restored.Error is null && restored.FilterCount == 10 && restored.InterfaceId == network.Id &&
                restored.ApprovedAddresses.SequenceEqual(reply.ApprovedAddresses) && restored.Clients.Single().Mac == approvedMac &&
                restored.Clients.Single().IpAddresses!.SequenceEqual(["192.168.137.10"]),
                "Device guard reply roundtrip preserves interface, applied addresses and client metadata through short stream reads");
            check(release.Operation == "release" && release.ApprovedMacs.Length == 0 && segmented.Position == segmented.Length,
                "Device guard wire consumes one frame at a time without swallowing the following command");
        }

        byte[] Frame(int length, byte[]? payload = null)
        {
            byte[] bytes = new byte[4 + (payload?.Length ?? 0)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
            payload?.CopyTo(bytes, 4);
            return bytes;
        }
        async Task<bool> RejectFrame(byte[] bytes, Type expectedException)
        {
            using var input = new MemoryStream(bytes);
            try { await GuardWire.ReceiveAsync<GuardCommand>(input, token); return false; }
            catch (Exception ex) { return expectedException.IsInstanceOfType(ex); }
        }
        foreach (int invalidLength in new[] { 0, -1, int.MinValue, GuardWire.MaxLength + 1, int.MaxValue })
            check(await RejectFrame(Frame(invalidLength), typeof(InvalidDataException)), "Device guard wire rejects invalid declared length " + invalidLength);
        check(await RejectFrame([0, 0, 0], typeof(EndOfStreamException)) && await RejectFrame([], typeof(EndOfStreamException)),
            "Device guard wire rejects truncated or missing frame headers");
        check(await RejectFrame(Frame(8, [123, 125]), typeof(EndOfStreamException)),
            "Device guard wire rejects a body shorter than its declared length");
        check(await RejectFrame(Frame(1, Encoding.UTF8.GetBytes("{")), typeof(JsonException)),
            "Device guard wire rejects malformed JSON within a complete frame");
        check(await RejectFrame(Frame(4, Encoding.UTF8.GetBytes("null")), typeof(InvalidDataException)),
            "Device guard wire rejects a null command payload");
        using (var stream = new MemoryStream())
        {
            bool oversizedSendRejected = false;
            try { await GuardWire.SendAsync(stream, new string('x', GuardWire.MaxLength), token); }
            catch (InvalidDataException) { oversizedSendRejected = true; }
            check(oversizedSendRejected && stream.Length == 0, "Device guard wire rejects an oversized outgoing payload before writing any header or bytes");
            string boundary = new('x', GuardWire.MaxLength - 2);
            await GuardWire.SendAsync(stream, boundary, token);
            stream.Position = 0;
            check(await GuardWire.ReceiveAsync<string>(stream, token) == boundary && stream.Length == GuardWire.MaxLength + 4,
                "Device guard wire accepts a payload exactly at its documented byte limit");
        }
    }

    private sealed class SegmentedMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
}
