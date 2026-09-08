using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HenHotspot;

// ABI checked against WinDivert v2.2.2 include/windivert.h. This app never uses
// NETWORK_FORWARD: WinDivert documents that layer as incompatible with Windows NAT.
public static class NativeWinDivert
{
    public const int NetworkLayer = 0;
    public const ulong Drop = 2;
    public const uint OutboundFlag = 1U << 17;
    public const int NoData = 232;
    public static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct Address
    {
        [FieldOffset(0)] public long Timestamp;
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public uint InterfaceIndex;
        [FieldOffset(20)] public uint SubInterfaceIndex;
    }

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertOpen", SetLastError = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    public static extern IntPtr Open(string filter, int layer, short priority, ulong flags);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertRecv", SetLastError = true, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Receive(IntPtr handle, [Out] byte[] packet, uint capacity, out uint received, out Address address);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertSend", SetLastError = true, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Send(IntPtr handle, byte[] packet, uint length, out uint sent, ref Address address);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertShutdown", SetLastError = true, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shutdown(IntPtr handle, int how);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertClose", SetLastError = true, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Close(IntPtr handle);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertSetParam", SetLastError = true, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetParam(IntPtr handle, int parameter, ulong value);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperCalcChecksums", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CalculateChecksums([In, Out] byte[] packet, uint length, ref Address address, ulong flags);

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperCompileFilter", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CompileFilter(string filter, int layer, IntPtr compiledObject, uint objectLength,
        out IntPtr errorText, out uint errorPosition);

    public static Win32Exception Failure(string operation, int? code = null)
    {
        int error = code ?? Marshal.GetLastWin32Error();
        string detail = error switch
        {
            2 => L10n.T("WinDivertDriverFilesAreMissingNextToHen"),
            5 => L10n.T("WindowsRequiresAdministratorPermissionToEnableTheFilter"),
            577 => L10n.T("WindowsDidNotAcceptTheWinDivertDriverSignature"),
            654 => L10n.T("AnotherWinDivertVersionIsLoadedRestartWindowsBeforeTrying"),
            1275 => L10n.T("WindowsOrSecuritySoftwareBlockedTheWinDivertDriver"),
            _ => new Win32Exception(error).Message
        };
        return new Win32Exception(error, L10n.F("CouldNot01WinDivert2", operation, detail, error));
    }
}
