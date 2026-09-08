using System.IO;
using System.Text.Json;
using System.Windows;

namespace HenHotspot;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 1 && e.Args[0] == "--dns-self-test")
        {
            Shutdown(await WinDivertSelfTest.RunAsync());
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--device-guard" && int.TryParse(e.Args[2], out int parentId))
        {
            Shutdown(await DeviceGuardHelper.RunAsync(e.Args[1], parentId));
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--dns-filter" && int.TryParse(e.Args[2], out int filterParentId))
        {
            Shutdown(await DnsFilterHelper.RunAsync(e.Args[1], filterParentId));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnose")
        {
            try
            {
                var service = new HotspotService();
                var status = await service.ReadAsync();
                await File.WriteAllTextAsync(e.Args[1], JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(e.Args[1], JsonSerializer.Serialize(new { error = ex.Message }));
                Shutdown(1);
            }
            return;
        }
        instanceMutex = new Mutex(true, "Local\\HenHotspot.MainWindow", out ownsInstance);
        if (!ownsInstance)
        {
            MessageBox.Show("Hen Hotspot ya está abierto. Usa la ventana existente.", "Hen Hotspot", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0); return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose(); base.OnExit(e);
    }
}
