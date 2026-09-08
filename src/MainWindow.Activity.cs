using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;

namespace HenHotspot;

public partial class MainWindow
{
    private ActivityStore? activityStore;
    private ActivityFilter? activityFilter;
    private int activityPageNumber, activityRevision;
    private bool activityLoading, activityReload, activitySettingsBusy;
    private const int ActivityPageSize = 50;

    private void InitializeActivity()
    {
        ActivityFromDate.SelectedDate = DateTime.Today;
        ActivityUntilDate.SelectedDate = DateTime.Today;
        ActivityDeviceBox.ItemsSource = new[] { new ActivityDeviceOption("", "Todos los dispositivos") };
        ActivityDeviceBox.SelectedIndex = 0;
        try { activityStore = new ActivityStore(); }
        catch (Exception ex) { ActivityErrorText.Text = "El historial no está disponible: " + ex.Message; ActivityErrorText.Visibility = Visibility.Visible; }
        RetentionBox.SelectedIndex = (activityStore?.RetentionDays ?? 7) switch { 1 => 0, 30 => 2, 90 => 3, _ => 1 };
        CaptureActivityFilter();
        UpdateActivityState();
    }

    private async void Activity_Click(object sender, RoutedEventArgs e)
    {
        Navigate(ActivityPanel, ActivityNav, "Actividad", "Consultas DNS del hotspot.");
        await RefreshActivityAsync();
    }

    private bool CaptureActivityFilter()
    {
        if (ActivityFromDate.SelectedDate is not DateTime from || ActivityUntilDate.SelectedDate is not DateTime until || until.Date < from.Date)
        {
            Notice("Elige una fecha de inicio y otra de fin igual o posterior.", true); return false;
        }
        if (until.Date >= DateTime.MaxValue.Date) { Notice("La fecha de fin está fuera de rango.", true); return false; }
        var device = ActivityDeviceBox.SelectedValue as string;
        string? outcome = (ActivityOutcomeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        activityFilter = new ActivityFilter(new DateTimeOffset(from.Date), new DateTimeOffset(until.Date.AddDays(1)),
            string.IsNullOrWhiteSpace(device) ? null : device,
            string.IsNullOrWhiteSpace(ActivityDomainBox.Text) ? null : ActivityDomainBox.Text.Trim(),
            string.IsNullOrWhiteSpace(outcome) ? null : outcome);
        activityPageNumber = 0; activityRevision++;
        return true;
    }

    private async void ApplyActivityFilters_Click(object sender, RoutedEventArgs e)
    {
        if (CaptureActivityFilter()) await RefreshActivityAsync();
    }

    private async void ActivityPrevious_Click(object sender, RoutedEventArgs e)
    {
        activityPageNumber = Math.Max(0, activityPageNumber - 1); activityRevision++;
        await RefreshActivityAsync();
    }

    private async void ActivityNext_Click(object sender, RoutedEventArgs e)
    {
        activityPageNumber++; activityRevision++;
        await RefreshActivityAsync();
    }

    private void UpdateActivityState()
    {
        bool enabled = activityStore?.RecordingEnabled == true;
        ActivityRecordButton.Content = enabled ? "Pausar registro" : "Reanudar registro";
        ActivityRecordButton.IsEnabled = activityStore is not null && !activitySettingsBusy;
        SaveRetentionButton.IsEnabled = ClearActivityStartButton.IsEnabled = activityStore is not null && !activitySettingsBusy;
        ActivityStateText.Text = activityStore is null ? "Historial no disponible" : enabled ?
            (dnsFilter?.Snapshot.Active == true ? "Registro de consultas DNS activo" : "Registro en espera del filtro DNS") : "Registro pausado";
        ActivityRetentionText.Text = activityStore is null ? "" : $"Conservación: {activityStore.RetentionDays} {(activityStore.RetentionDays == 1 ? "día" : "días")} · guardado en esta laptop";
        if (activityStore?.Error is string error)
        {
            ActivityErrorText.Text = error + " El filtrado puede seguir funcionando; el historial puede estar incompleto.";
            ActivityErrorText.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshActivityAsync()
    {
        UpdateActivityState();
        if (activityStore is null || activityFilter is null) return;
        if (activityLoading) { activityReload = true; return; }
        activityLoading = true;
        ActivityPreviousButton.IsEnabled = ActivityNextButton.IsEnabled = false;
        var filter = activityFilter; int page = activityPageNumber, revision = activityRevision;
        try
        {
            var data = await Task.Run(() => (Page: activityStore.Query(filter, page, ActivityPageSize), Devices: activityStore.GetDevices()));
            if (revision != activityRevision) { activityReload = true; return; }
            int pages = Math.Max(1, (data.Page.TotalCount + ActivityPageSize - 1) / ActivityPageSize);
            if (activityPageNumber >= pages) { activityPageNumber = pages - 1; activityRevision++; activityReload = true; return; }
            var rows = data.Page.Entries.Select(entry => new ActivityRow(entry)).ToArray();
            if (ActivityGrid.ItemsSource is not ActivityRow[] previous || !previous.Select(x => x.Entry).SequenceEqual(rows.Select(x => x.Entry)))
                ActivityGrid.ItemsSource = rows;
            EmptyActivityBorder.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ActivityGrid.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyActivityText.Text = activityStore.RecordingEnabled ?
                "No hay consultas que coincidan con estos filtros." :
                "Sin coincidencias. El registro está pausado.";
            ActivitySummaryText.Text = $"Registros: {data.Page.TotalCount:N0} · Bloqueadas: {data.Page.BlockedCount:N0}";
            ActivityPageText.Text = $"Página {activityPageNumber + 1} de {pages}";
            ActivityPreviousButton.IsEnabled = activityPageNumber > 0;
            ActivityNextButton.IsEnabled = activityPageNumber + 1 < pages;
            ActivityPreviousButton.Visibility = ActivityNextButton.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            var devices = new[] { new ActivityDeviceOption("", "Todos los dispositivos") }
                .Concat(data.Devices.Select(d => new ActivityDeviceOption(d.Id, $"{d.Name} · {d.Ip}"))).ToArray();
            if (ActivityDeviceBox.ItemsSource is not ActivityDeviceOption[] old || !old.SequenceEqual(devices))
            {
                string selected = ActivityDeviceBox.SelectedValue as string ?? "";
                if (selected.Length > 0 && !devices.Any(d => d.Id == selected) && ActivityDeviceBox.SelectedItem is ActivityDeviceOption removed)
                    devices = devices.Append(removed).ToArray();
                ActivityDeviceBox.ItemsSource = devices; ActivityDeviceBox.SelectedValue = selected;
            }
            ActivityErrorText.Visibility = activityStore.Error is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateActivityState();
        }
        catch (Exception ex)
        {
            ActivityErrorText.Text = "No se pudo consultar el historial: " + ex.Message;
            ActivityErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            activityLoading = false;
            if (activityReload) { activityReload = false; _ = RefreshActivityAsync(); }
        }
    }

    private async void ToggleActivityRecording_Click(object sender, RoutedEventArgs e)
    {
        if (activityStore is null || activitySettingsBusy) return;
        activitySettingsBusy = true; UpdateActivityState();
        try
        {
            bool enabled = !activityStore.RecordingEnabled;
            if (enabled) Interlocked.Exchange(ref dnsRecordNotBefore, DateTimeOffset.UtcNow.UtcTicks);
            await Task.Run(() => activityStore.UpdateSettings(enabled, activityStore.RetentionDays));
            await RefreshActivityAsync();
            Notice(enabled ? "Registro reanudado. Se guardarán las consultas DNS nuevas." : "Registro pausado. El filtro sigue funcionando con sus reglas activas.");
        }
        catch (Exception ex) { Notice("No se pudo cambiar el registro: " + ex.Message, true); }
        finally { activitySettingsBusy = false; UpdateActivityState(); }
    }

    private async void SaveRetention_Click(object sender, RoutedEventArgs e)
    {
        if (activityStore is null || activitySettingsBusy) return;
        int days = int.Parse((string)((ComboBoxItem)RetentionBox.SelectedItem).Tag, CultureInfo.InvariantCulture);
        activitySettingsBusy = true; UpdateActivityState();
        try
        {
            await Task.Run(() => activityStore.UpdateSettings(activityStore.RecordingEnabled, days));
            await RefreshActivityAsync();
            Notice($"Conservación guardada: {days} {(days == 1 ? "día" : "días")}. Los registros anteriores al plazo se eliminan automáticamente.");
        }
        catch (Exception ex) { Notice("No se pudo guardar la conservación: " + ex.Message, true); }
        finally { activitySettingsBusy = false; UpdateActivityState(); }
    }

    private void ConfirmActivityClear_Click(object sender, RoutedEventArgs e) => ActivityClearConfirmation.Visibility = Visibility.Visible;
    private void CancelActivityClear_Click(object sender, RoutedEventArgs e) => ActivityClearConfirmation.Visibility = Visibility.Collapsed;
    private async void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        if (activityStore is null || activitySettingsBusy) return;
        activitySettingsBusy = true; UpdateActivityState();
        try
        {
            Interlocked.Exchange(ref dnsRecordNotBefore, DateTimeOffset.UtcNow.UtcTicks);
            await Task.Run(activityStore.Clear);
            ActivityClearConfirmation.Visibility = Visibility.Collapsed;
            activityPageNumber = 0; activityRevision++;
            await RefreshActivityAsync();
            Notice("Historial borrado. Si el registro está activo, continuará con las consultas DNS nuevas.");
        }
        catch (Exception ex) { Notice("No se pudo borrar el historial: " + ex.Message, true); }
        finally { activitySettingsBusy = false; UpdateActivityState(); }
    }
}

public record ActivityDeviceOption(string Id, string Label);

public record ActivityRow(ActivityEntry Entry)
{
    public string Domain => Entry.Domain.Length == 0 ? "Sin dominio válido" : Entry.Domain;
    public string Device => Entry.DeviceName;
    public string Ip => Entry.ClientIp;
    private bool IsDns => Entry.Protocol == "DNS";
    public string Protocol => IsDns ? "Consulta DNS" : Entry.Protocol == "Unknown" ? "Registro anterior" : Entry.Protocol + " · histórico";
    public string Date => Entry.StartedAt.ToLocalTime().ToString("HH:mm:ss\ndd/MM/yyyy");
    public string Duration => IsDns ? "—" : Entry.DurationSeconds < 60 ? $"{Entry.DurationSeconds:F1} s" :
        Entry.DurationSeconds < 3600 ? $"{(int)(Entry.DurationSeconds / 60)} min {(int)(Entry.DurationSeconds % 60)} s" :
        $"{(int)(Entry.DurationSeconds / 3600)} h {(int)(Entry.DurationSeconds % 3600 / 60)} min";
    public string Status => Entry.Outcome switch { "Connecting" => "Conectando", "Blocked" => "Bloqueada", "Error" => "Error", "Interrupted" => "Interrumpida", _ => Entry.EndedAt is null ? "En curso" : "Permitida" };
    public string StatusColor => Entry.Outcome switch { "Blocked" or "Error" => "#FBE9E0", "Interrupted" => "#F0F1EE", _ => "#E8F5EE" };
    public string Traffic => IsDns ? "—" : $"↓ {Bytes(Entry.DownloadBytes)}\n↑ {Bytes(Entry.UploadBytes)}";
    public string Details => IsDns ? $"{Domain}\n{Device} · {Ip}\nConsulta DNS: {Entry.StartedAt.ToLocalTime():g}\nNo confirma que se haya visitado la web ni mide tiempo de pantalla. No se cuentan bytes ni duración de navegación." : $"{Domain}\n{Device} · {Ip}\nInicio: {Entry.StartedAt.ToLocalTime():g}\nFin del registro: {(Entry.EndedAt is null ? "en curso" : Entry.EndedAt.Value.ToLocalTime().ToString("g"))}\nÚltimo tráfico: {(Entry.LastTrafficAt is null ? "sin transferencia" : Entry.LastTrafficAt.Value.ToLocalTime().ToString("g"))}\nDuración de conexión, no tiempo de pantalla. 'Interrumpida' puede indicar pausa del registro, reinicio o desconexión.";
    public static string Bytes(long bytes) => bytes < 1000 ? $"{bytes} B" : bytes < 1_000_000 ? $"{bytes / 1000d:F1} KB" : bytes < 1_000_000_000 ? $"{bytes / 1_000_000d:F2} MB" : $"{bytes / 1_000_000_000d:F2} GB";
}
