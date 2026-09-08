using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using HenHotspot;

public static class DnsPacketTests
{
    public static async Task Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); } catch (ArgumentException) { check(true, name); return; }
            throw new Exception(name + " was accepted");
        }

        check(Marshal.SizeOf<NativeWinDivert.Address>() == 80 &&
            Marshal.OffsetOf<NativeWinDivert.Address>(nameof(NativeWinDivert.Address.Flags)).ToInt32() == 8 &&
            Marshal.OffsetOf<NativeWinDivert.Address>(nameof(NativeWinDivert.Address.InterfaceIndex)).ToInt32() == 16 &&
            NativeWinDivert.OutboundFlag == 1U << 17, "WinDivert address ABI matches the 2.2.2 native header");

        var block = new DomainPolicy(false, ["*.Example.COM."]);
        check(block.Domains.SequenceEqual(["example.com"]) && !block.Allows("example.com") &&
            !block.Allows("www.example.com") && block.Allows("notexample.com") && block.Allows("example.com.evil.test"),
            "DNS blocking follows domain boundaries and includes subdomains");
        var allow = new DomainPolicy(true, ["example.com", "EXAMPLE.com"]);
        check(allow.Domains.Length == 1 && allow.Allows("A.Example.com") && !allow.Allows("example.net") &&
            !allow.Allows("https://example.com") && !allow.Allows("1.1.1.1"), "DNS allowlist normalizes names and refuses non-domains");
        string[] copied = allow.Domains; copied[0] = "evil.test";
        check(allow.Allows("example.com") && !allow.Allows("evil.test"), "Published DNS policy cannot be mutated by callers");
        Reject(() => new DomainPolicy(true, []), "Empty DNS allowlist is rejected");
        Reject(() => new DomainPolicy(false, Enumerable.Range(0, 129).Select(i => $"d{i}.test")), "DNS rules are bounded to 128 entries");
        Reject(() => new DomainPolicy(false, ["invalid/path"]), "Invalid DNS policy entry is rejected");
        Reject(() => new DomainPolicy(false, [null!]), "Null DNS policy entry is rejected as invalid input");

        var network = new GuardInterface("test", 42, 123, "Test hotspot", "192.168.137.1", 24);
        var filter = DnsFilterEngine.BuildFilters(network);
        check(filter.Udp.Contains("inbound and !loopback and !impostor and ip") && filter.Udp.Contains("ifIdx == 42") &&
            filter.Udp.Contains("ip.DstAddr == 192.168.137.1") && filter.Udp.EndsWith("udp and udp.DstPort == 53") &&
            filter.Tcp.EndsWith("tcp and tcp.DstPort == 53") && NativeWinDivert.NetworkLayer == 0,
            "DNS capture is restricted to local gateway delivery and never uses NAT forwarding");
        // These helpers execute only DLL code: they never call Open or install a driver.
        foreach (string expression in new[] { filter.Udp, filter.Tcp })
        {
            bool compiled = NativeWinDivert.CompileFilter(expression, NativeWinDivert.NetworkLayer, IntPtr.Zero, 0,
                out IntPtr syntaxError, out uint errorPosition);
            if (!compiled) throw new Exception($"WinDivert filter syntax at {errorPosition}: {Marshal.PtrToStringAnsi(syntaxError)}");
            check(compiled, "Bundled WinDivert compiles the production gateway DNS filter without opening a handle");
        }
        Reject(() => DnsFilterEngine.BuildFilters(network with { Index = 0 }), "DNS filter rejects missing interface identity");
        Reject(() => DnsFilterEngine.BuildFilters(network with { Address = "192.168.137.1 or true" }), "DNS filter rejects injected filter syntax");
        Reject(() => DnsFilterEngine.BuildFilters(network with { Address = "192.168.137.255" }), "DNS filter rejects a broadcast gateway");
        Reject(() => DnsFilterEngine.BuildFilters(network with { Address = "::1" }), "DNS filter does not pretend to support IPv6");
        await using (var inactive = new DnsFilterEngine(network, block))
        {
            inactive.Apply(allow);
            check(!inactive.Snapshot().Active && inactive.Snapshot().Queries == 0, "Constructing a DNS engine does not install or open a driver");
            await inactive.StopAsync(); await inactive.StopAsync();
            bool stopped = false;
            try { inactive.Apply(block); } catch (ObjectDisposedException) { stopped = true; }
            check(stopped, "DNS engine shutdown before activation is safe and idempotent");
        }

        foreach (ushort type in new ushort[] { 1, 28, 65 })
        {
            byte[] packet = Query("ExAmPlE.com", type);
            check(DnsPacket.TryReadQuestion(packet, out var q) && q is not null && q.Domain == "example.com" &&
                q.Type == type && q.ClientIp == "192.168.137.10" && q.ServerIp == "192.168.137.1" &&
                q.ClientPort == 53001 && q.Id == 0xa15c, $"DNS parser reads type {type}, case, transaction and client identity");
            byte[] response = DnsPacket.CreateNameError(q!);
            check(response[0] == 0x45 && response[8] == 64 && response[9] == 17 &&
                U16(response, 2) == response.Length && U16(response, 24) == response.Length - 20 &&
                response.AsSpan(12, 4).SequenceEqual(packet.AsSpan(16, 4)) &&
                response.AsSpan(16, 4).SequenceEqual(packet.AsSpan(12, 4)) && U16(response, 20) == 53 && U16(response, 22) == 53001,
                $"DNS NXDOMAIN type {type} swaps IPv4 addresses and UDP ports with exact lengths");
            check(U16(response, 28) == 0xa15c && U16(response, 30) == 0x8183 && U16(response, 32) == 1 &&
                response.AsSpan(34, 6).SequenceEqual(new byte[6]) &&
                response.AsSpan(40).SequenceEqual(packet.AsSpan(40)),
                $"DNS NXDOMAIN type {type} preserves transaction and exact-case question without answer or extra records");
            var nativeAddress = new NativeWinDivert.Address { Flags = NativeWinDivert.OutboundFlag, InterfaceIndex = 42 };
            check(NativeWinDivert.CalculateChecksums(response, (uint)response.Length, ref nativeAddress, 0) &&
                Sum16(response.AsSpan(0, 20)) == 0xffff &&
                Fold(SumWords(response.AsSpan(12, 8)) + 17 + (uint)(response.Length - 20) + SumWords(response.AsSpan(20))) == 0xffff &&
                (nativeAddress.Flags & ((1U << 17) | (1U << 21) | (1U << 23))) == ((1U << 17) | (1U << 21) | (1U << 23)),
                $"Native NXDOMAIN type {type} has independently verified IPv4 and UDP pseudoheader checksums");
        }
        check(DnsPacket.TryReadQuestion(Query("xn--maana-pta.com", 1, true, true), out var idn) && idn!.Domain == "xn--maana-pta.com",
            "DNS parser accepts punycode, IPv4 options and a well-formed EDNS record");
        byte[] edns = Query("example.com", 1, false, true);
        check(DnsPacket.TryReadQuestion(edns, out var eq) && DnsPacket.CreateNameError(eq!).Length == edns.Length - 11,
            "NXDOMAIN safely omits EDNS and resets additional count");
        byte[] flags = Query("example.com"); Put16(flags, 30, 0x0030);
        check(DnsPacket.TryReadQuestion(flags, out var fq) && U16(DnsPacket.CreateNameError(fq!), 30) == 0x8093,
            "NXDOMAIN preserves CD, honors missing RD and clears the authenticated-data bit");

        var longName = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        check(DnsPacket.TryReadQuestion(Query(longName), out _), "DNS parser accepts the maximum 255-byte encoded hostname");
        check(!DnsPacket.TryReadQuestion(Query(longName + "d"), out _), "DNS parser rejects an expanded hostname over 255 bytes");

        var invalid = new List<(string, byte[])>();
        void Mutate(string name, Action<byte[]> change)
        {
            byte[] packet = Query("example.com"); change(packet); invalid.Add((name, packet));
        }
        Mutate("IPv6 version", b => b[0] = 0x65);
        Mutate("short IPv4 header", b => b[0] = 0x44);
        Mutate("inconsistent IPv4 total", b => Put16(b, 2, (ushort)(b.Length - 1)));
        Mutate("inconsistent UDP length", b => Put16(b, 24, 20));
        Mutate("non-UDP protocol", b => b[9] = 6);
        Mutate("other destination port", b => Put16(b, 22, 5353));
        Mutate("zero client port", b => Put16(b, 20, 0));
        Mutate("more fragments flag", b => Put16(b, 6, 0x2000));
        Mutate("nonzero fragment offset", b => Put16(b, 6, 1));
        Mutate("reserved IP fragment flag", b => Put16(b, 6, 0x8000));
        Mutate("DNS response", b => Put16(b, 30, 0x8100));
        Mutate("DNS unsupported opcode", b => Put16(b, 30, 0x0900));
        Mutate("DNS truncated query", b => Put16(b, 30, 0x0300));
        Mutate("DNS reserved flag", b => Put16(b, 30, 0x0140));
        Mutate("multiple questions", b => Put16(b, 32, 2));
        Mutate("unexpected answer section", b => Put16(b, 34, 1));
        Mutate("unexpected authority section", b => Put16(b, 36, 1));
        Mutate("too many additional records", b => Put16(b, 38, 17));
        Mutate("missing additional record", b => Put16(b, 38, 1));
        Mutate("non-IN question class", b => Put16(b, b.Length - 2, 3));
        Mutate("zero query type", b => Put16(b, b.Length - 4, 0));
        Mutate("compression pointer loop", b => { b[40] = 0xc0; b[41] = 12; });
        Mutate("compression pointer out of range", b => { b[40] = 0xff; b[41] = 0xff; });
        Mutate("compression pointer into DNS header", b => { b[40] = 0xc0; b[41] = 4; });
        Mutate("backward compression cycle", b => { b[40] = 1; b[41] = (byte)'a'; b[42] = 0xc0; b[43] = 12; });
        Mutate("unsupported label encoding", b => b[40] = 0x40);
        Mutate("control character in label", b => b[41] = 10);
        Mutate("non-ASCII wire hostname", b => b[41] = 0xff);
        Mutate("empty internal label", b => b[40] = 0);
        foreach (var (name, packet) in invalid)
            check(!DnsPacket.TryReadQuestion(packet, out _), "DNS parser rejects " + name);

        byte[] optTruncated = Query("example.com", 1, false, true);
        Put16(optTruncated, optTruncated.Length - 2, 100);
        check(!DnsPacket.TryReadQuestion(optTruncated, out _), "DNS parser rejects truncated EDNS data");
        byte[] valid = Query("example.com");
        bool everyTruncationRejected = true;
        for (int length = 0; length < valid.Length; length++)
        {
            byte[] cut = valid[..length];
            if (length >= 28) { Put16(cut, 2, (ushort)length); Put16(cut, 24, (ushort)(length - 20)); }
            everyTruncationRejected &= !DnsPacket.TryReadQuestion(cut, out _);
        }
        check(everyTruncationRejected, "DNS parser rejects every truncation even with adjusted outer lengths");
        byte[] oversized = new byte[28 + DnsPacket.MaximumDnsQueryBytes + 1];
        valid.CopyTo(oversized, 0); Put16(oversized, 2, (ushort)oversized.Length); Put16(oversized, 24, (ushort)(oversized.Length - 20));
        check(!DnsPacket.TryReadQuestion(oversized, out _), "DNS parser bounds query memory and ignores oversized payloads");
        var random = new Random(71839);
        for (int sample = 0; sample < 3000; sample++)
        {
            byte[] fuzz = new byte[random.Next(0, 1024)]; random.NextBytes(fuzz);
            DnsPacket.TryReadQuestion(fuzz, out _);
        }
        check(true, "DNS parser handles 3000 bounded malformed packets without throwing");
        for (int sample = 0; sample < 3000; sample++)
        {
            byte[] fuzz = (byte[])edns.Clone();
            for (int edit = 0; edit < 4; edit++) fuzz[random.Next(28, fuzz.Length)] = (byte)random.Next(256);
            DnsPacket.TryReadQuestion(fuzz, out _);
        }
        check(true, "DNS parser handles 3000 mutated DNS payloads behind valid IPv4 and UDP headers");
    }

    private static byte[] Query(string name, ushort type = 1, bool ipOptions = false, bool edns = false)
    {
        var encoded = new List<byte>();
        foreach (string label in name.Split('.')) { encoded.Add(checked((byte)label.Length)); encoded.AddRange(Encoding.ASCII.GetBytes(label)); }
        encoded.Add(0);
        int ipLength = ipOptions ? 24 : 20;
        byte[] result = new byte[ipLength + 8 + 12 + encoded.Count + 4 + (edns ? 11 : 0)];
        result[0] = (byte)(0x40 | ipLength / 4); result[8] = 64; result[9] = 17;
        Put16(result, 2, (ushort)result.Length); Put16(result, 6, 0x4000);
        IPAddress.Parse("192.168.137.10").GetAddressBytes().CopyTo(result, 12);
        IPAddress.Parse("192.168.137.1").GetAddressBytes().CopyTo(result, 16);
        Put16(result, ipLength, 53001); Put16(result, ipLength + 2, 53); Put16(result, ipLength + 4, (ushort)(result.Length - ipLength));
        int dns = ipLength + 8;
        Put16(result, dns, 0xa15c); Put16(result, dns + 2, 0x0100); Put16(result, dns + 4, 1);
        encoded.CopyTo(result, dns + 12); int cursor = dns + 12 + encoded.Count;
        Put16(result, cursor, type); Put16(result, cursor + 2, 1);
        if (edns)
        {
            Put16(result, dns + 10, 1); cursor += 4;
            Put16(result, cursor + 1, 41); Put16(result, cursor + 3, 1232);
        }
        return result;
    }

    private static ushort U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset));
    private static void Put16(byte[] b, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset), value);
    private static uint SumWords(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        for (int i = 0; i < bytes.Length; i += 2)
            sum += (uint)(bytes[i] << 8) | (i + 1 < bytes.Length ? bytes[i + 1] : 0U);
        return sum;
    }
    private static ushort Fold(uint sum)
    {
        while (sum > 0xffff) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)sum;
    }
    private static ushort Sum16(ReadOnlySpan<byte> bytes) => Fold(SumWords(bytes));
}
