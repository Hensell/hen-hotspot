using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;

namespace HenHotspot;

public partial class MainWindow
{
    private readonly DeviceAccessStore devicePermissions = new();
    private DeviceGuardClient? deviceGuard;
    private bool deviceBusy, deviceGuardUncertain;
    private string? deviceError;

    private void InitializeDevices()
    {
        DeviceNameBox.MaxLength = 80;
        UpdateDevices();
    }

    private void Devices_Click(object sender, RoutedEventArgs e)
    {
        Navigate(DevicesPanel, DeviceNav, "Dispositivos", "");
        UpdateDevices();
    }

    private string[] ApprovedDeviceMacs() => devicePermissions.Entries
        .Where(p => p.State == DeviceAccessState.Approved).Select(p => p.Mac).ToArray();

    private static string? NormalizedDeviceMac(string mac)
    {
        try { return DeviceAccessStore.NormalizeMac(mac); }
        catch (ArgumentException) { return null; }
    }

    private void UpdateDevices()
    {
        bool working = busy || deviceBusy;
        bool hotspotOn = status?.State == "On" && status.Error is null;
        var guard = deviceGuard?.Snapshot;
        bool active = guard?.Active == true && guard.Error is null && !deviceGuardUncertain && hotspotOn;
        GlobalDeviceAccessText.Text = active ? "Control activo · prueba" : deviceGuard is null ? "Control apagado" : "Control sin confirmar";
        GlobalDeviceAccessText.ToolTip = active
            ? "Acceso a internet limitado a los dispositivos autorizados."
            : deviceGuard is null ? "Las decisiones guardadas no están limitando el acceso."
            : "Windows no confirmó el control. Apaga el hotspot si necesitas impedir el acceso.";
        DeviceGuardStateText.Text = deviceBusy ? "Actualizando el control…" : active
            ? "Control de dispositivos activo" : deviceGuard is null
            ? "Control de dispositivos apagado" : "Control sin confirmar";
        DeviceGuardDetailText.Text = active
            ? "Solo dispositivos autorizados · al cerrar Hen se apaga el hotspot."
            : deviceGuard is null
            ? "Las autorizaciones guardadas se aplicarán al activar el control."
            : "Windows no confirmó el bloqueo de internet. Desactiva el control para retirar sus reglas o apaga el hotspot.";
        DevicesPanel.IsEnabled = !working;
        DeviceGuardToggleButton.Content = deviceGuard is null ? "Activar control" : "Desactivar control";
        DeviceGuardToggleButton.IsEnabled = !working && (deviceGuard is not null || hotspotOn);
        DeviceGuardToggleButton.ToolTip = deviceGuard is not null
            ? "Quita este control y vuelve al acceso sin aprobación. Las decisiones quedan guardadas."
            : hotspotOn ? "Windows pedirá permiso de administrador para instalar los filtros temporales."
            : "Enciende el hotspot para activar el control. Puedes preparar la lista mientras tanto.";

        string? error = deviceError ?? guard?.Error ?? devicePermissions.LastError;
        DeviceGuardErrorText.Text = error ?? "";
        DeviceGuardErrorText.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        var known = devicePermissions.Entries.ToDictionary(p => p.Mac, StringComparer.Ordinal);
        var connected = hotspotOn ? status!.Clients.ToArray() : [];
        var observations = connected.GroupBy(c => NormalizedDeviceMac(c.Mac) ?? "invalid:" + c.Mac, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var keys = known.Keys.Concat(observations.Keys).Distinct(StringComparer.Ordinal);
        var rows = keys.Select(key =>
        {
            known.TryGetValue(key, out var permission);
            observations.TryGetValue(key, out var clients);
            bool valid = !key.StartsWith("invalid:", StringComparison.Ordinal);
            string mac = valid ? key : clients![0].Mac;
            string name = !string.IsNullOrWhiteSpace(permission?.Name) ? permission.Name :
                clients?.Length > 0 ? ClientRow.From(clients[0]).DisplayName : "Dispositivo sin nombre";
            var addresses = (clients ?? []).SelectMany(c => c.IpAddresses ?? []).Distinct().ToArray();
            string address = clients is null ? "Sin conexión" : addresses.Length == 0 ? "Sin IP asignada" : string.Join(", ", addresses);
            var state = permission?.State ?? DeviceAccessState.Pending;
            string label = !valid ? "MAC no válida" : state switch
            {
                DeviceAccessState.Approved when !active => "Autorización guardada",
                DeviceAccessState.Blocked when !active => "Bloqueo guardado",
                DeviceAccessState.Approved when addresses.Length == 0 => "Esperando IP",
                DeviceAccessState.Approved when !addresses.Any(a => guard!.ApprovedAddresses.Contains(a, StringComparer.Ordinal)) => "Por aplicar",
                DeviceAccessState.Approved => "Autorizado",
                DeviceAccessState.Blocked => "Bloqueado",
                _ => "Pendiente"
            };
            return new DeviceAccessRow(name, mac, address, label,
                valid && !working && state != DeviceAccessState.Approved,
                valid && !working && state != DeviceAccessState.Blocked);
        }).OrderBy(r => r.Status == "Pendiente" ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (DeviceGrid.ItemsSource is not DeviceAccessRow[] previous || !previous.SequenceEqual(rows)) DeviceGrid.ItemsSource = rows;
        DeviceGrid.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DeviceEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceEmptyText.Text = hotspotOn ? "Conecta un dispositivo para revisarlo, o añade su MAC en la otra pestaña."
            : "Todavía no hay autorizaciones guardadas. Enciende el hotspot para detectar equipos o añade una MAC manualmente.";
    }

    private async Task RunDeviceOperationAsync(Func<Task> operation, string success)
    {
        if (busy || deviceBusy) return;
        deviceBusy = true; deviceError = null; UpdateEnabled();
        try
        {
            await operation();
            await RefreshAsync();
            Notice(success);
        }
        catch (Exception ex)
        {
            deviceError = HotspotService.Friendly(ex);
            Notice(deviceError, true);
        }
        finally { deviceBusy = false; UpdateEnabled(); }
    }

    private async void ToggleDeviceGuard_Click(object sender, RoutedEventArgs e)
    {
        if (deviceGuard is not null)
        {
            await RunDeviceOperationAsync(async () =>
            {
                await deviceGuard.ReleaseAsync();
                deviceGuard = null; deviceGuardUncertain = false;
            }, "Control desactivado. Este bloqueo de internet se ha retirado; las autorizaciones quedan guardadas.");
            return;
        }
        await RunDeviceOperationAsync(async () =>
        {
            status = await service.ReadAsync();
            if (status.State != "On" || status.Error is not null)
                throw new InvalidOperationException("Enciende el hotspot y confirma su estado antes de activar el control.");
            deviceGuard = new DeviceGuardClient();
            deviceGuardUncertain = true;
            try
            {
                await deviceGuard.StartAsync(ApprovedDeviceMacs());
                deviceGuardUncertain = false;
            }
            catch { deviceGuardUncertain = true; throw; }
        }, "Filtros instalados. Comprueba con tu teléfono que el bloqueo y la autorización funcionan. Al cerrar la app se apagará el hotspot.");
    }

    private async void ApproveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mac })
            await SaveDevicePermissionAsync(mac, DeviceAlias(mac), DeviceAccessState.Approved);
    }

    private async void BlockDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mac })
            await SaveDevicePermissionAsync(mac, DeviceAlias(mac), DeviceAccessState.Blocked);
    }

    private string DeviceAlias(string mac)
    {
        string name = (DeviceGrid.ItemsSource as DeviceAccessRow[])?.FirstOrDefault(r => r.Mac == mac)?.Name ?? "";
        return new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
    }

    private async void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        await SaveDevicePermissionAsync(DeviceMacBox.Text, DeviceNameBox.Text, DeviceAccessState.Approved, clearManual: true);
    }

    private async Task SaveDevicePermissionAsync(string mac, string name, DeviceAccessState state, bool clearManual = false)
    {
        bool hadGuard = deviceGuard is not null;
        string success = hadGuard
            ? "Decisión guardada y enviada al control de acceso. Comprueba el resultado en el teléfono."
            : "Decisión guardada. Se aplicará cuando actives el control de dispositivos.";
        await RunDeviceOperationAsync(async () =>
        {
            await Task.Run(() => devicePermissions.Set(mac, name, state));
            // Persist the decision before asking the Windows helper to apply it.
            if (deviceGuard is not null)
            {
                try
                {
                    await deviceGuard.UpdateApprovedAsync(ApprovedDeviceMacs());
                    deviceGuardUncertain = false;
                }
                catch (Exception ex)
                {
                    deviceGuardUncertain = true;
                    string stopped;
                    try
                    {
                        try { await StopDnsFilterAsync(); } catch (Exception filterError) { dnsFilterError = HotspotService.Friendly(filterError); }
                        await service.StopAsync();
                        status = await service.ReadAsync();
                        stopped = status.State == "Off" && status.Error is null
                            ? "El hotspot se apagó para evitar acceso sin control."
                            : "No se pudo confirmar el apagado del hotspot; apágalo desde Windows.";
                    }
                    catch { stopped = "No se pudo apagar el hotspot. Apágalo desde Windows para impedir acceso sin control."; }
                    throw new InvalidOperationException("La decisión quedó guardada, pero no se confirmó su aplicación. " + stopped + " " + ex.Message, ex);
                }
            }
            if (clearManual) { DeviceMacBox.Clear(); DeviceNameBox.Clear(); }
        }, success);
    }

    // Closing the app or explicitly switching off its hotspot must not release filters first.
    private async Task CloseDeviceGuardAsync()
    {
        var guard = deviceGuard;
        if (guard is null) return;
        var current = await service.ReadAsync();
        if (current.State != "Off" || current.Error is not null)
        {
            await service.StopAsync();
            current = await service.ReadAsync();
        }
        status = current;
        if (current.State != "Off" || current.Error is not null)
            throw new InvalidOperationException("No se pudo confirmar el apagado del hotspot. El control se conserva; apaga el hotspot desde Windows antes de cerrar.");
        try { await guard.ReleaseAsync(); }
        catch
        {
            // It is safe to dispose only now that Windows has confirmed the hotspot is off.
            await guard.DisposeAsync();
        }
        deviceGuard = null; deviceGuardUncertain = false; deviceError = null;
    }
}

public sealed record DeviceAccessRow(string Name, string Mac, string Address, string Status, bool ApproveEnabled, bool BlockEnabled);
