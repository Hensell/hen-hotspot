using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace HenHotspot;

public sealed class DnsQuestion
{
    public string Domain { get; }
    public string ClientIp { get; }
    public string ServerIp { get; }
    public ushort ClientPort { get; }
    public ushort Id { get; }
    public ushort Type { get; }
    public ushort Flags { get; }
    internal byte[] EncodedName { get; }

    internal DnsQuestion(string domain, string clientIp, string serverIp, ushort port,
        ushort id, ushort type, ushort flags, byte[] encodedName)
    {
        Domain = domain; ClientIp = clientIp; ServerIp = serverIp; ClientPort = port;
        Id = id; Type = type; Flags = flags; EncodedName = encodedName;
    }
}

// Only classic, single-question IPv4 UDP DNS requests. The driver reassembles
// inbound IP fragments before capture; fragments reaching this parser are refused.
public static class DnsPacket
{
    public const int MaximumDnsQueryBytes = 4096;

    public static bool TryReadQuestion(ReadOnlySpan<byte> packet, out DnsQuestion? question)
    {
        question = null;
        if (packet.Length < 40 || packet.Length > 65535 || packet[0] >> 4 != 4) return false;
        int ipLength = (packet[0] & 15) * 4;
        if (ipLength < 20 || ipLength + 20 > packet.Length || U16(packet, 2) != packet.Length || packet[9] != 17 ||
            (U16(packet, 6) & 0xbfff) != 0) return false;
        int udpLength = U16(packet, ipLength + 4);
        ushort sourcePort = U16(packet, ipLength);
        if (sourcePort == 0 || U16(packet, ipLength + 2) != 53 || udpLength < 20 || udpLength != packet.Length - ipLength)
            return false;
        var dns = packet[(ipLength + 8)..];
        if (dns.Length > MaximumDnsQueryBytes) return false;
        ushort flags = U16(dns, 2);
        // Accept RD, AD and CD. Responses, other opcodes, truncation and reserved flags are not queries.
        if ((flags & ~0x0130) != 0 || U16(dns, 4) != 1 || U16(dns, 6) != 0 || U16(dns, 8) != 0)
            return false;
        int additionalCount = U16(dns, 10);
        if (additionalCount > 16 || !ReadName(dns, 12, out string name, out byte[] encoded, out int cursor) ||
            cursor + 4 > dns.Length || U16(dns, cursor) == 0 || U16(dns, cursor + 2) != 1)
            return false;
        string domain;
        try { domain = Validation.Domain(name); }
        catch (ArgumentException) { return false; }
        ushort type = U16(dns, cursor);
        cursor += 4;
        for (int i = 0; i < additionalCount; i++)
        {
            if (!ReadName(dns, cursor, out _, out _, out cursor) || cursor + 10 > dns.Length) return false;
            int dataLength = U16(dns, cursor + 8);
            cursor += 10;
            if (dataLength > dns.Length - cursor) return false;
            cursor += dataLength;
        }
        if (cursor != dns.Length) return false;
        question = new(domain, new IPAddress(packet.Slice(12, 4)).ToString(),
            new IPAddress(packet.Slice(16, 4)).ToString(), sourcePort, U16(dns, 0), type, flags, encoded);
        return true;
    }

    // Builds a minimal NXDOMAIN reply. The caller must calculate IPv4 and UDP
    // checksums before injection. Re-encoding the name removes compression references
    // to records omitted from the response; query case is preserved for DNS 0x20 clients.
    public static byte[] CreateNameError(DnsQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        byte[] reply = new byte[20 + 8 + 12 + question.EncodedName.Length + 4];
        reply[0] = 0x45; reply[8] = 64; reply[9] = 17;
        Put16(reply, 2, checked((ushort)reply.Length));
        IPAddress.Parse(question.ServerIp).GetAddressBytes().CopyTo(reply, 12);
        IPAddress.Parse(question.ClientIp).GetAddressBytes().CopyTo(reply, 16);
        Put16(reply, 20, 53); Put16(reply, 22, question.ClientPort);
        Put16(reply, 24, checked((ushort)(reply.Length - 20)));
        Put16(reply, 28, question.Id);
        // QR, RA, NXDOMAIN, original RD and CD. Never assert AA or authenticated data.
        Put16(reply, 30, (ushort)(0x8083 | (question.Flags & 0x0110)));
        Put16(reply, 32, 1);
        question.EncodedName.CopyTo(reply, 40);
        Put16(reply, reply.Length - 4, question.Type); Put16(reply, reply.Length - 2, 1);
        return reply;
    }

    private static bool ReadName(ReadOnlySpan<byte> dns, int offset, out string name, out byte[] encoded, out int next)
    {
        name = ""; encoded = []; next = offset;
        var labels = new List<string>();
        var bytes = new List<byte>(64);
        var visited = new HashSet<int>();
        bool jumped = false;
        int expandedLength = 1;
        // A legal name is at most 255 bytes and 127 one-byte labels. Bound pointer
        // hops independently, including cyclic/backward and out-of-bounds references.
        for (int steps = 0; steps < 256; steps++)
        {
            if (offset < 12 || offset >= dns.Length || !visited.Add(offset)) return false;
            int length = dns[offset];
            if ((length & 0xc0) == 0xc0)
            {
                if (offset + 1 >= dns.Length) return false;
                int pointer = ((length & 0x3f) << 8) | dns[offset + 1];
                if (pointer < 12 || pointer >= offset) return false;
                if (!jumped) next = offset + 2;
                jumped = true; offset = pointer; continue;
            }
            if ((length & 0xc0) != 0) return false;
            offset++;
            if (length == 0)
            {
                if (!jumped) next = offset;
                bytes.Add(0); encoded = bytes.ToArray(); name = string.Join('.', labels); return true;
            }
            expandedLength += length + 1;
            if (expandedLength > 255 || offset + length > dns.Length || labels.Count >= 127) return false;
            var label = dns.Slice(offset, length);
            // QNAME is used as a hostname, never rendered as arbitrary DNS label bytes.
            foreach (byte value in label)
                if (!((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                    (value >= '0' && value <= '9') || value == '-' || value == '_')) return false;
            labels.Add(Encoding.ASCII.GetString(label));
            bytes.Add((byte)length); bytes.AddRange(label.ToArray()); offset += length;
        }
        return false;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
    private static void Put16(Span<byte> bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], value);
}
