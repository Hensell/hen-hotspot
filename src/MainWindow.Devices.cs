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
        Navigate(DevicesPanel, DeviceNav);
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
        GlobalDeviceAccessText.Text = active ? L10n.T("ControlOnTrial") : deviceGuard is null ? L10n.T("ControlOff") : L10n.T("ControlUnconfirmed");
        GlobalDeviceAccessText.ToolTip = active
            ? L10n.T("InternetAccessLimitedToAuthorizedDevices")
            : deviceGuard is null ? L10n.T("SavedDecisionsAreNotRestrictingAccess")
            : L10n.T("WindowsDidNotConfirmAccessControlTurnOffThe");
        DeviceGuardStateText.Text = deviceBusy ? L10n.T("UpdatingAccessControl") : active
            ? L10n.T("DeviceAccessControlOn") : deviceGuard is null
            ? L10n.T("DeviceAccessControlOff") : L10n.T("ControlUnconfirmed");
        DeviceGuardDetailText.Text = active
            ? L10n.T("AuthorizedDevicesOnlyClosingHenTurnsOffTheHotspot")
            : deviceGuard is null
            ? L10n.T("SavedAuthorizationsWillApplyWhenYouEnableAccessControl")
            : L10n.T("WindowsDidNotConfirmInternetBlockingDisableAccessControl");
        DevicesPanel.IsEnabled = !working;
        DeviceGuardToggleButton.Content = deviceGuard is null ? L10n.T("EnableControl") : L10n.T("DisableControl");
        DeviceGuardToggleButton.IsEnabled = !working && (deviceGuard is not null || hotspotOn);
        DeviceGuardToggleButton.ToolTip = deviceGuard is not null
            ? L10n.T("RemoveThisControlAndRestoreAccessWithoutApprovalDecisions")
            : hotspotOn ? L10n.T("WindowsWillAskForAdministratorPermissionToInstallTemporary")
            : L10n.T("TurnOnTheHotspotToEnableAccessControlYou");

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
                clients?.Length > 0 ? ClientRow.From(clients[0]).DisplayName : L10n.T("UnnamedDevice");
            var addresses = (clients ?? []).SelectMany(c => c.IpAddresses ?? []).Distinct().ToArray();
            string address = clients is null ? L10n.T("Offline") : addresses.Length == 0 ? L10n.T("NoIPAssigned") : string.Join(", ", addresses);
            var state = permission?.State ?? DeviceAccessState.Pending;
            string label = !valid ? L10n.T("InvalidMAC") : state switch
            {
                DeviceAccessState.Approved when !active => L10n.T("AuthorizationSaved"),
                DeviceAccessState.Blocked when !active => L10n.T("BlockSaved"),
                DeviceAccessState.Approved when addresses.Length == 0 => L10n.T("WaitingForIP"),
                DeviceAccessState.Approved when !addresses.Any(a => guard!.ApprovedAddresses.Contains(a, StringComparer.Ordinal)) => L10n.T("NotYetApplied"),
                DeviceAccessState.Approved => L10n.T("Authorized"),
                DeviceAccessState.Blocked => L10n.T("Blocked2"),
                _ => L10n.T("Pending")
            };
            return new DeviceAccessRow(name, mac, address, label,
                valid && !working && state != DeviceAccessState.Approved,
                valid && !working && state != DeviceAccessState.Blocked, valid && state == DeviceAccessState.Pending);
        }).OrderBy(r => r.Pending ? 0 : 1).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (DeviceGrid.ItemsSource is not DeviceAccessRow[] previous || !previous.SequenceEqual(rows)) DeviceGrid.ItemsSource = rows;
        DeviceGrid.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DeviceEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceEmptyText.Text = hotspotOn ? L10n.T("ConnectADeviceToReviewItOrAddIts")
            : L10n.T("NoSavedAuthorizationsYetTurnOnTheHotspotTo");
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
            }, L10n.T("ControlDisabledThisInternetBlockHasBeenRemovedAuthorizations"));
            return;
        }
        await RunDeviceOperationAsync(async () =>
        {
            status = await service.ReadAsync();
            if (status.State != "On" || status.Error is not null)
                throw new InvalidOperationException(L10n.T("TurnOnTheHotspotAndConfirmItsStatusBefore"));
            deviceGuard = new DeviceGuardClient();
            deviceGuardUncertain = true;
            try
            {
                await deviceGuard.StartAsync(ApprovedDeviceMacs());
                deviceGuardUncertain = false;
            }
            catch { deviceGuardUncertain = true; throw; }
        }, L10n.T("FiltersInstalledUseYourPhoneToCheckBlockingAnd"));
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
            ? L10n.T("DecisionSavedAndSentToAccessControlCheckThe")
            : L10n.T("DecisionSavedItWillApplyWhenYouEnableDevice");
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
                            ? L10n.T("TheHotspotWasTurnedOffToPreventUncontrolledAccess")
                            : L10n.T("CouldNotConfirmThatTheHotspotIsOffTurn");
                    }
                    catch { stopped = L10n.T("CouldNotTurnOffTheHotspotTurnItOff"); }
                    throw new InvalidOperationException(L10n.T("TheDecisionWasSavedButItsApplicationWasNot") + stopped + " " + ex.Message, ex);
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
            throw new InvalidOperationException(L10n.T("CouldNotConfirmThatTheHotspotIsOffAccess"));
        try { await guard.ReleaseAsync(); }
        catch
        {
            // It is safe to dispose only now that Windows has confirmed the hotspot is off.
            await guard.DisposeAsync();
        }
        deviceGuard = null; deviceGuardUncertain = false; deviceError = null;
    }
}

public sealed record DeviceAccessRow(string Name, string Mac, string Address, string Status, bool ApproveEnabled, bool BlockEnabled, bool Pending);
