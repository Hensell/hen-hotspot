using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace HenHotspot;

// Windows SDK 10.0.26100: fwpmu.h, fwpmtypes.h, fwptypes.h. No driver or persistent rules.
public sealed class NativeWfp : IDisposable
{
    private IntPtr engine;
    private readonly Guid sublayer = Guid.NewGuid();
    private List<ulong> ids = [];
    public int FilterCount => ids.Count;
    public static readonly Guid Forward4 = new("a82acc24-4ee1-4ee1-b465-fd1d25cb10a4"), Forward6 = new("7b964818-19c7-493a-b71f-832c3684d28c");
    private static readonly Guid Inbound4 = new("c86fd1bf-21cd-497e-a0bb-17425c885c58");
    private static readonly Guid Receive4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), Receive6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");
    private static readonly Guid SourceInterface = new("2311334d-c92d-45bf-9496-edf447820e2d"), DestinationInterface = new("35cf6522-4139-45ee-a0d5-67b80949d879");
    private static readonly Guid LocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957"), DestinationAddress = new("2d79133b-b390-45c6-8699-acaceaafed33");
    private static readonly Guid LocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4"), RemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid LocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b"), Protocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");

    public NativeWfp()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException(L10n.T("AccessControlRequires64BitWindows"));
        using var memory = new NativeMemory();
        var session = new Session { Key = Guid.NewGuid(), Display = memory.Display(L10n.T("HenHotspotAccessSession")), Flags = 1, Timeout = 5000 };
        Check(FwpmEngineOpen0(null, 10, IntPtr.Zero, ref session, out engine), L10n.T("OpenTheWindowsFilter"));
        try
        {
            var layer = new Sublayer { Key = sublayer, Display = memory.Display(L10n.T("HenHotspotDevices")), Weight = 0x7000 };
            Check(FwpmSubLayerAdd0(engine, ref layer, IntPtr.Zero), L10n.T("CreateTheDeviceSession"));
        }
        catch { Dispose(); throw; }
    }

    public void Apply(GuardPlan plan)
    {
        if (engine == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeWfp));
        if (plan.Network.Index == 0 || plan.Network.Luid == 0) throw new ArgumentException(L10n.T("InvalidInterface"));
        var added = new List<ulong>();
        Check(FwpmTransactionBegin0(engine, 0), L10n.T("StartTheAccessChanges"));
        try
        {
            foreach (ulong id in ids) Check(FwpmFilterDeleteById0(engine, id), L10n.T("UpdateAnAccessRule"));
            using var memory = new NativeMemory();
            var incoming = Condition.UInt(SourceInterface, 3, plan.Network.Index);
            var outgoing = Condition.UInt(DestinationInterface, 3, plan.Network.Index);
            var local = Condition.Pointer(LocalInterface, 4, memory.UInt64(plan.Network.Luid));
            // ICS rewrites the source IPv4 before IPFORWARD on this Windows path.
            // Match the device at packet arrival, while its original address is still present.
            added.Add(Add(Inbound4, false, 1, [local], memory, L10n.T("UnauthorizedInboundBeforeNAT")));
            added.Add(Add(Forward4, false, 1, [outgoing], memory, L10n.T("UnauthorizedInternetReturn")));
            added.Add(Add(Forward6, false, 1, [incoming], memory, L10n.T("UnauthorizedOutboundIPv6")));
            added.Add(Add(Forward6, false, 1, [outgoing], memory, L10n.T("UnauthorizedIPv6Return")));
            // IPPACKET has no transport ports. Let locally addressed DHCP reach ALE,
            // where the existing UDP/67 exception and default deny still apply.
            uint gateway = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(plan.Network.Address).GetAddressBytes());
            uint broadcast = gateway | (uint.MaxValue >> plan.Network.PrefixLength);
            foreach (uint destination in new[] { gateway, broadcast, uint.MaxValue }.Distinct())
                added.Add(Add(Inbound4, true, 8, [local, Condition.UInt(LocalAddress, 3, destination)], memory, L10n.T("LocalTrafficCheckServices")));
            foreach (var layer in new[] { Receive4, Receive6 })
                added.Add(Add(layer, false, 1, [local], memory, L10n.T("UnauthorizedLaptopServices")));
            // DHCP remains available so new clients obtain an address and can be approved in the UI.
            added.Add(Add(Receive4, true, 8, [local, Condition.UInt(Protocol, 1, 17), Condition.UInt(LocalPort, 2, 67)], memory, L10n.T("DHCPAssignment")));
            foreach (string address in plan.ApprovedAddresses)
            {
                uint ip = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(address).GetAddressBytes());
                added.Add(Add(Inbound4, true, 8, [local, Condition.UInt(RemoteAddress, 3, ip)], memory, L10n.T("AuthorizedDeviceInboundBeforeNAT")));
                added.Add(Add(Forward4, true, 8, [outgoing, Condition.UInt(DestinationAddress, 3, ip)], memory, L10n.T("AuthorizedDeviceReturn")));
                added.Add(Add(Receive4, true, 8, [local, Condition.UInt(RemoteAddress, 3, ip)], memory, L10n.T("AuthorizedDeviceLocalServices")));
            }
            Check(FwpmTransactionCommit0(engine), L10n.T("ApplyTheAccessChanges"));
            ids = added;
        }
        catch { FwpmTransactionAbort0(engine); throw; }
    }

    public void Verify()
    {
        foreach (ulong id in ids)
        {
            Check(FwpmFilterGetById0(engine, id, out var pointer), L10n.T("CheckTheAccessFilter"));
            try { if (Marshal.PtrToStructure<Filter>(pointer).SubLayer != sublayer) throw new InvalidOperationException(L10n.T("TheAccessRuleChangedUnexpectedly")); }
            finally { FwpmFreeMemory0(ref pointer); }
        }
    }

    private ulong Add(Guid layer, bool allow, uint weight, Condition[] conditions, NativeMemory memory, string name)
    {
        var filter = new Filter
        {
            Key = Guid.NewGuid(),
            Display = memory.Display("Hen · " + name),
            Layer = layer,
            SubLayer = sublayer,
            Weight = new Value { Type = 1, Data = weight },
            Count = (uint)conditions.Length,
            Conditions = memory.Array(conditions),
            Action = new ActionValue { Type = allow ? 0x1002u : 0x1001u }
        };
        // Soft permits within our sublayer: never override another firewall provider's block.
        Check(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out ulong id), L10n.T("CreateTheRule") + name);
        return id;
    }

    public void Dispose() { if (engine != IntPtr.Zero) { FwpmEngineClose0(engine); engine = IntPtr.Zero; ids.Clear(); } }
    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new InvalidOperationException(L10n.F("WindowsCouldNot00x1X82", operation, result, new Win32Exception(unchecked((int)result)).Message));
    }

    [StructLayout(LayoutKind.Sequential)] public struct DisplayData { public IntPtr Name, Description; }
    [StructLayout(LayoutKind.Sequential)] public struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] public struct Value { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ulong Data; }
    [StructLayout(LayoutKind.Sequential)] public struct Session { public Guid Key; public DisplayData Display; public uint Flags, Timeout, ProcessId; public IntPtr Sid, User; public int Kernel; }
    [StructLayout(LayoutKind.Sequential)] public struct Sublayer { public Guid Key; public DisplayData Display; public uint Flags; public IntPtr Provider; public Blob Data; public ushort Weight; }
    [StructLayout(LayoutKind.Sequential)] public struct ActionValue { public uint Type; public Guid Key; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Condition
    {
        public Guid Key; public uint Match; public Value Value;
        public static Condition UInt(Guid key, uint type, uint value) => new() { Key = key, Value = new Value { Type = type, Data = value } };
        public static Condition Pointer(Guid key, uint type, IntPtr value) => new() { Key = key, Value = new Value { Type = type, Data = (ulong)value } };
    }
    [StructLayout(LayoutKind.Explicit, Size = 200)]
    public struct Filter
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public DisplayData Display;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public IntPtr Provider;
        [FieldOffset(48)] public Blob ProviderData;
        [FieldOffset(64)] public Guid Layer;
        [FieldOffset(80)] public Guid SubLayer;
        [FieldOffset(96)] public Value Weight;
        [FieldOffset(112)] public uint Count;
        [FieldOffset(120)] public IntPtr Conditions;
        [FieldOffset(128)] public ActionValue Action;
        [FieldOffset(152)] public Guid Context;
        [FieldOffset(168)] public IntPtr Reserved;
        [FieldOffset(176)] public ulong Id;
        [FieldOffset(184)] public Value EffectiveWeight;
    }
    private sealed class NativeMemory : IDisposable
    {
        private readonly List<IntPtr> owned = [];
        private IntPtr Own(IntPtr p) { owned.Add(p); return p; }
        public DisplayData Display(string name) => new() { Name = Own(Marshal.StringToHGlobalUni(name)) };
        public IntPtr UInt64(ulong value) { var p = Own(Marshal.AllocHGlobal(8)); Marshal.WriteInt64(p, unchecked((long)value)); return p; }
        public IntPtr Array(Condition[] values) { int size = Marshal.SizeOf<Condition>(); var p = Own(Marshal.AllocHGlobal(size * values.Length)); for (int i = 0; i < values.Length; i++) Marshal.StructureToPtr(values[i], p + i * size, false); return p; }
        public void Dispose() { foreach (var p in owned) Marshal.FreeHGlobal(p); }
    }
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmEngineOpen0(string? server, uint auth, IntPtr identity, ref Session session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerAdd0(IntPtr engine, ref Sublayer sublayer, IntPtr sd);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionAbort0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr sd, out ulong id);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDeleteById0(IntPtr engine, ulong id);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterGetById0(IntPtr engine, ulong id, out IntPtr filter);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr pointer);
    [DllImport("iphlpapi.dll")] public static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);
}
