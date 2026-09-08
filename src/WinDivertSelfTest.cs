using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace HenHotspot;

// Native smoke test: the filter is literally false, so it captures no network
// packets. This verifies loading and cancellation without simulating a phone.
public static class WinDivertSelfTest
{
    public static string ReportPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HenHotspot", "windivert-self-test.json");

    public static async Task<int> RunAsync()
    {
        var checks = new List<string>();
        string? error = null;
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Esta prueba requiere permiso de administrador.");
            handle = NativeWinDivert.Open("false", NativeWinDivert.NetworkLayer, 90, 0);
            if (handle == NativeWinDivert.InvalidHandle) { handle = IntPtr.Zero; throw NativeWinDivert.Failure("abrir la prueba sin tráfico"); }
            checks.Add("Signed driver opened a filter that matches no packets");
            if (!GetParam(handle, 3, out ulong major) || !GetParam(handle, 4, out ulong minor))
                throw NativeWinDivert.Failure("leer la versión del controlador");
            if (major != 2 || minor != 2) throw new InvalidOperationException($"Versión de controlador inesperada: {major}.{minor}");
            checks.Add($"Native driver version {major}.{minor}");
            IntPtr receiveHandle = handle;
            var receive = Task.Factory.StartNew(() =>
            {
                bool result = NativeWinDivert.Receive(receiveHandle, new byte[65535], 65535, out _, out _);
                return (Result: result, Code: Marshal.GetLastWin32Error());
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!NativeWinDivert.Shutdown(handle, 1)) throw NativeWinDivert.Failure("cerrar la recepción de prueba");
            var received = await receive.WaitAsync(TimeSpan.FromSeconds(3));
            if (received.Result || received.Code != NativeWinDivert.NoData)
                throw new Win32Exception(received.Code, "La cancelación nativa no devolvió el resultado esperado.");
            checks.Add("Blocking receive released by shutdown with ERROR_NO_DATA (232)");
            if (!NativeWinDivert.Close(handle)) throw NativeWinDivert.Failure("cerrar el filtro de prueba");
            handle = IntPtr.Zero;
            checks.Add("Native handle closed successfully");
            LegacyProxyCleanup.RemoveOwnedRule();
            checks.Add("Legacy proxy firewall permission removed if owned by this installation");
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            if (handle != IntPtr.Zero) NativeWinDivert.Close(handle);
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            await File.WriteAllTextAsync(ReportPath, JsonSerializer.Serialize(new
            { At = DateTimeOffset.UtcNow, Version = "0.6.0", Passed = error is null, Checks = checks, Error = error },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        return error is null ? 0 : 1;
    }

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertGetParam", SetLastError = true, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetParam(IntPtr handle, int parameter, out ulong value);
}
