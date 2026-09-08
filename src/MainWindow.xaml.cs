using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace HenHotspot;

public partial class MainWindow : Window
{
    private readonly HotspotService service = new();
    private readonly ProfileStore store = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private HotspotStatus? status;
    private DnsFilterClient? dnsFilter;
    private DomainPolicy? activePolicy;
    private IReadOnlyDictionary<string, ActivityDevice> dnsDeviceNames = new Dictionary<string, ActivityDevice>();
    private string? dnsFilterError;
    private long dnsRecordNotBefore = DateTimeOffset.UtcNow.UtcTicks;
    private bool noticeIsError;
    private PolicyDraft? savedDraft;
    private bool busy, refreshing, initialized;

    public MainWindow()
    {
        InitializeComponent();
        InitializeDomainEditor();
        InitializeActivity();
        InitializeDevices();
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width - 24);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height - 16);
        Width = Math.Min(1280, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(860, SystemParameters.WorkArea.Height - 16);
        UpdateEnabled();
        Navigate(OverviewPanel, OverviewNav, "Resumen", "");
        try
        {
            var draft = store.Load();
            savedDraft = draft;
            ModeBox.SelectedIndex = draft.Mode == "Permitir solo estos dominios" ? 1 : 0;
            foreach (var domain in (draft.Domains ?? []).Select(Validation.Domain).Distinct()) domainEntries.Add(domain);
        }
        catch { Notice("No se pudo leer el borrador guardado. Puedes crear uno nuevo.", true); }
        Loaded += async (_, _) => { await RefreshAsync(); timer.Start(); };
        timer.Tick += async (_, _) => { if (!busy && !deviceBusy) await RefreshAsync(); };
        Closed += (_, _) => { timer.Stop(); noticeTimer.Stop(); activityStore?.Dispose(); };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); if (!busy && !noticeIsError) NoticeBorder.Visibility = Visibility.Collapsed; };
        Closing += async (_, e) =>
        {
            if (busy || deviceBusy || activitySettingsBusy) { e.Cancel = true; Notice("Espera a que termine la operación."); return; }
            if (dnsFilter is not null || deviceGuard is not null)
            {
                e.Cancel = true; busy = true; timer.Stop(); UpdateEnabled();
                bool ready = false;
                try
                {
                    await StopDnsFilterAsync();
                    await CloseDeviceGuardAsync();
                    ready = true;
                }
                catch (Exception ex)
                {
                    Notice("La aplicación sigue abierta porque no terminó el cierre seguro. " + HotspotService.Friendly(ex), true);
                }
                finally { busy = false; UpdateEnabled(); if (!ready) timer.Start(); }
                if (ready) Close();
            }
        };
        ModeBox.SelectionChanged += (_, _) => MarkDraftChanged();
        UpdateDraftSummary();
    }

    private void Navigate(StackPanel panel, Button nav, string title, string subtitle)
    {
        foreach (var p in new[] { OverviewPanel, NetworkPanel, RulesPanel, DevicesPanel, ActivityPanel, DiagnosticsPanel }) p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { OverviewNav, NetworkNav, RulesNav, DeviceNav, ActivityNav, DiagnosticsNav })
        {
            button.Background = new SolidColorBrush(button == nav ? Color.FromRgb(35, 60, 51) : Colors.Transparent);
            button.Foreground = new SolidColorBrush(button == nav ? Color.FromRgb(165, 234, 206) : Color.FromRgb(154, 173, 168));
        }
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
        PageSubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        BreadcrumbText.Text = nav.Name switch { "OverviewNav" => "Resumen", "NetworkNav" => "Red Wi-Fi", "RulesNav" => "Dominios", "DeviceNav" => "Dispositivos", "ActivityNav" => "Actividad", _ => "Diagnóstico" };
        PageScroll.ScrollToTop();
        if (!busy && !noticeIsError) NoticeBorder.Visibility = Visibility.Collapsed;
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => Navigate(OverviewPanel, OverviewNav, "Resumen", "");
    private void Network_Click(object sender, RoutedEventArgs e) => Navigate(NetworkPanel, NetworkNav, "Red Wi-Fi", "");
    private void Rules_Click(object sender, RoutedEventArgs e) => Navigate(RulesPanel, RulesNav, "Dominios", "");
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => Navigate(DiagnosticsPanel, DiagnosticsNav, "Diagnóstico", "");
    private void AccessGuide_Click(object sender, RoutedEventArgs e) => Rules_Click(sender, e);
    private void DismissNotice_Click(object sender, RoutedEventArgs e) { NoticeBorder.Visibility = Visibility.Collapsed; noticeTimer.Stop(); noticeIsError = false; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { await RefreshAsync(); if (ActivityPanel.Visibility == Visibility.Visible) await RefreshActivityAsync(); if (status?.Error is null) Notice("Estado actualizado desde Windows."); }

    private async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            status = await service.ReadAsync();
            if ((status.State != "On" || status.Error is not null) && dnsFilter is not null)
            {
                string? previousFilterError = dnsFilter.Snapshot.Error ?? dnsFilterError;
                try
                {
                    await StopDnsFilterAsync();
                    if (previousFilterError is not null)
                    {
                        dnsFilterError = previousFilterError;
                        Notice("El filtro DNS se detuvo. " + previousFilterError, true);
                    }
                }
                catch (Exception ex)
                {
                    string closeError = HotspotService.Friendly(ex);
                    dnsFilterError = previousFilterError is not null && previousFilterError != closeError
                        ? previousFilterError + " Cierre pendiente: " + closeError : closeError;
                    Notice("No se confirmó el cierre del filtro DNS. " + dnsFilterError, true);
                }
            }
            UpdateDnsDeviceNames();
            HeroSsidText.Text = string.IsNullOrWhiteSpace(status.Ssid) ? "Hen Hotspot" : status.Ssid;
            StateText.Text = status.State switch { "On" => $"Compartiendo internet · Banda {BandLabel(status.Band)}", "Off" => "Apagado · Enciéndelo para compartir internet", "InTransition" => "Windows está cambiando el estado…", _ => "No se pudo confirmar el estado" };
            NetworkSummary.Text = status.Ssid.Length > 0 ? $"Red: {status.Ssid} · Banda: {BandLabel(status.Band)}" : "Configura tu red para comenzar.";
            ToggleButton.Content = status.State == "On" ? "Apagar hotspot" : "Encender hotspot";
            UpdateEnabled();
            CountText.Text = status.Error is null ? status.ClientCount.ToString() : "—";
            ConnectedCountLabel.Text = status.ClientCount == 1 ? "conectado" : "conectados";
            CapacityText.Text = status.MaxClients > 0 ? $"Capacidad: hasta {status.MaxClients} dispositivos" : "Capacidad no disponible";
            SourceText.Text = status.Connection;
            InternetText.Text = status.Connectivity == "InternetAccess" ? "Conexión disponible" : "Sin acceso confirmado";
            var clientRows = status.Clients.Select(ClientRow.From).ToArray();
            if (ClientsGrid.ItemsSource is not ClientRow[] previousRows || !previousRows.SequenceEqual(clientRows)) ClientsGrid.ItemsSource = clientRows;
            EmptyClientCard.Visibility = status.Clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyClients.Text = status.Error is not null || status.State == "Unknown" ? "No se pudo consultar la lista de dispositivos. Actualiza el estado para volver a intentar." :
                status.State == "Off" ? "Enciende el hotspot y conecta un dispositivo. Aparecerá aquí automáticamente." :
                status.State == "InTransition" ? "Espera a que Windows termine de cambiar el estado del hotspot." :
                "Todavía no hay dispositivos conectados. Conecta un teléfono a esta red para comenzar.";
            ClientsGrid.Visibility = status.Clients.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            DiagnosticText.Text = $"Windows: {Environment.OSVersion.Version}\nConexión: {status.Connection}\nAcceso: {status.Connectivity}\nCapacidad hotspot: {status.Capability}\nEstado: {status.State}\nMáximo de clientes: {status.MaxClients}\n\nADAPTADORES\n" +
                string.Join("\n\n", status.Adapters.Select(a => $"{a.Name} · {a.Status}\n{a.Description}")) +
                (status.Error is null ? "" : $"\n\nDETALLE\n{status.Error}");
            UpdatedText.Text = $"Sincronizado con Windows · {DateTime.Now:HH:mm:ss}";
            UpdateFilterView();
            UpdateDevices();
            UpdateActivityState();
            if (ActivityPanel.Visibility == Visibility.Visible && activityPageNumber == 0) await RefreshActivityAsync();
            if (!initialized)
            {
                SsidBox.Text = string.IsNullOrEmpty(status.Ssid) ? "Hen Hotspot" : status.Ssid;
                BandBox.SelectedIndex = status.Band switch { "TwoPointFourGigahertz" => 1, "FiveGigahertz" => 2, _ => 0 };
                if (status.Error is not null) Notice(status.Error, true);
                else if (status.Capability != "Enabled") Notice($"Windows reporta: {status.Capability}. Revisa Diagnóstico para continuar.", true);
                initialized = true;
            }
            else if (status.Error is not null) Notice(status.Error, true);
        }
        catch (Exception ex) { status = null; UpdateEnabled(); Notice(HotspotService.Friendly(ex), true); }
        finally { refreshing = false; }
    }

    private void UpdateEnabled()
    {
        bool working = busy || deviceBusy;
        var ui = HotspotUiState.From(status?.State, status?.Capability, status?.Error is not null, working);
        GlobalStateText.Text = ui.Label;
        GlobalStateDot.Fill = (Brush)new BrushConverter().ConvertFromString(ui.Color)!;
        GlobalStateText.ToolTip = status?.Error ?? "Estado consultado directamente en Windows cada 2 segundos.";
        ToggleButton.IsEnabled = ui.CanToggle && !(status?.State == "Off" && (deviceGuard is not null || dnsFilter is not null));
        ToggleButton.ToolTip = status?.State == "Off" && (deviceGuard is not null || dnsFilter is not null)
            ? "Cierra el filtro o el control anterior antes de volver a encender el hotspot." : null;
        SaveNetworkButton.IsEnabled = ui.CanEditNetwork;
        SsidBox.IsEnabled = PasswordBox.IsEnabled = BandBox.IsEnabled = ui.CanEditNetwork;
        ClientsGrid.IsEnabled = ui.CanUseClients;
        ModeBox.IsEnabled = DomainInput.IsEnabled = DomainList.IsEnabled = SaveDraftButton.IsEnabled = !working;
        AddDomainButton.IsEnabled = !working && !string.IsNullOrWhiteSpace(DomainInput.Text);
        bool hasRequiredDomain = ModeBox.SelectedIndex != 1 || domainEntries.Count > 0 || !string.IsNullOrWhiteSpace(DomainInput.Text);
        FilterToggleButton.IsEnabled = !working && (dnsFilter is not null || (ui.CanApplyRules && hasRequiredDomain));
        ApplyRulesButton.IsEnabled = ui.CanApplyRules && hasRequiredDomain && dnsFilter?.Snapshot is { Active: true, Error: null } && !DraftMatchesActive();
        FilterToggleButton.ToolTip = dnsFilter is not null ? "Detén el filtrado de consultas DNS." : ui.CanApplyRules
            ? "Requiere permiso de administrador." : "Enciende el hotspot para activar el filtro DNS.";
        ApplyRulesButton.ToolTip = dnsFilter?.Snapshot.Active == true ? (DraftMatchesActive() ? "Estas reglas ya están aplicadas." : "Se aplicarán a las próximas consultas DNS; las respuestas guardadas en caché pueden seguir funcionando.") : "Activa primero el hotspot y el filtro DNS.";
        NetworkLockText.Text = ui.CanEditNetwork ? "Hotspot apagado · configuración disponible." :
            status?.State == "On" ? "Apaga el hotspot para editar la configuración." :
            working || status?.State == "InTransition" ? "Espera a que Windows termine la operación para editar la red." :
            "Edición bloqueada hasta confirmar que Windows permite configurar el hotspot.";
        RefreshButton.IsEnabled = !working;
        UpdateDraftSummary();
        UpdateDevices();
        UpdateFilterView();
    }
    private async Task RunAsync(Func<Task> operation, string success)
    {
        if (busy || deviceBusy) return;
        busy = true; UpdateEnabled(); Notice("Windows está procesando la operación…");
        try { await operation(); await RefreshAsync(); Notice(success); }
        catch (Exception ex) { Notice(HotspotService.Friendly(ex), true); }
        finally { busy = false; UpdateEnabled(); if (!noticeIsError) noticeTimer.Start(); }
    }
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (status?.State == "On") await RunAsync(async () => { await StopDnsFilterAsync(); await CloseDeviceGuardAsync(); if (status?.State != "Off") await service.StopAsync(); }, "Hotspot, filtro DNS y control de dispositivos apagados.");
        else await RunAsync(service.StartAsync, "Hotspot encendido. Conecta un teléfono a la red para comprobar la navegación.");
    }
    private async void SaveNetwork_Click(object sender, RoutedEventArgs e)
    {
        string ssid = SsidBox.Text, password = PasswordBox.Password, band = ((ComboBoxItem)BandBox.SelectedItem).Content.ToString()!;
        await RunAsync(async () => { await service.ConfigureAsync(ssid, password, band); PasswordBox.Clear(); }, "Configuración guardada en Windows. Puedes encender el hotspot desde Resumen.");
    }
    private void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitDomainInput()) return;
        try
        {
            var draft = ReadDraft(); store.Save(draft); savedDraft = draft;
            UpdateDraftSummary();
            Notice("Borrador guardado.");
        }
        catch (Exception ex) { Notice(ex.Message, true); }
    }
    private void WindowsSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true }); }
        catch (Exception ex) { Notice(ex.Message, true); }
    }

    private PolicyDraft ReadDraft() => new(savedDraft?.DownloadMbps ?? 3, savedDraft?.UploadMbps ?? 1,
        ((ComboBoxItem)ModeBox.SelectedItem).Content.ToString()!,
        domainEntries.ToArray());
    private static DomainPolicy DnsPolicy(PolicyDraft draft) => new(draft.Mode == "Permitir solo estos dominios", draft.Domains ?? []);
    private void MarkDraftChanged() { UpdateDraftSummary(); UpdateEnabled(); }
    private bool DraftMatchesActive()
    {
        if (!string.IsNullOrWhiteSpace(DomainInput.Text) || activePolicy is null || dnsFilter?.Snapshot is not { Active: true, Error: null }) return false;
        try { var draft = ReadDraft(); return (draft.Mode == "Permitir solo estos dominios") == activePolicy.AllowOnly &&
            (draft.Domains ?? []).Order().SequenceEqual(activePolicy.Domains.Order()); }
        catch (ArgumentException) { return false; }
    }
    private void UpdateDraftSummary()
    {
        int count = domainEntries.Count;
        DomainCountText.Text = $"{count} {(count == 1 ? "dominio" : "dominios")}";
        DomainListTitle.Text = ModeBox.SelectedIndex == 1 ? "Dominios permitidos" : "Dominios bloqueados";
        EmptyDomainsPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DomainList.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyDomainsText.Text = ModeBox.SelectedIndex == 1 ? "Agrega al menos un dominio para activar esta lista." : "Agrega el primer dominio que quieras bloquear.";
        if (!string.IsNullOrWhiteSpace(DomainInput.Text)) { DraftStatusText.Text = "Dominio pendiente de agregar."; return; }
        if (DraftMatchesActive()) { DraftStatusText.Text = "Todos los cambios aplicados."; return; }
        bool saved = false;
        try { var draft = ReadDraft(); saved = savedDraft is not null && draft.Mode == savedDraft.Mode && (draft.Domains ?? []).Order().SequenceEqual((savedDraft.Domains ?? []).Order()); } catch (ArgumentException) { }
        DraftStatusText.Text = dnsFilter?.Snapshot.Active == true ? (saved ? "Borrador guardado · pendiente de aplicar." : "Cambios sin aplicar al filtro DNS.") :
            saved ? "Borrador guardado." : "Cambios sin guardar.";
    }

    private async void FilterToggle_Click(object sender, RoutedEventArgs e)
    {
        if (dnsFilter is not null) { await RunAsync(StopDnsFilterAsync, "Filtro DNS apagado. Sus reglas ya no se aplican a las consultas nuevas."); return; }
        if (!CommitDomainInput()) return;
        await RunAsync(async () =>
        {
            var fresh = await service.ReadAsync();
            if (fresh.State != "On" || fresh.Error is not null) throw new InvalidOperationException("Enciende el hotspot para activar el filtro DNS.");
            var draft = ReadDraft(); var policy = DnsPolicy(draft);
            dnsFilterError = null;
            Interlocked.Exchange(ref dnsRecordNotBefore, DateTimeOffset.UtcNow.UtcTicks);
            var next = new DnsFilterClient(RecordDnsObservation); dnsFilter = next;
            try { await next.StartAsync(policy); activePolicy = policy; }
            catch (Exception ex)
            {
                string error = HotspotService.Friendly(ex);
                try { await StopDnsFilterAsync(); } catch (Exception stop) { error += " Cierre pendiente: " + HotspotService.Friendly(stop); }
                dnsFilterError = error;
                throw;
            }
            try { store.Save(draft); savedDraft = draft; }
            catch (Exception ex) { dnsFilterError = "Filtro activo, pero no se pudo guardar el borrador: " + ex.Message; }
        }, "Filtro DNS activado.");
    }
    private async Task StopDnsFilterAsync()
    {
        var current = dnsFilter;
        if (current is not null)
        {
            try { await current.StopAsync(); await current.DisposeAsync(); }
            catch (Exception ex) { dnsFilterError = HotspotService.Friendly(ex); UpdateFilterView(); throw; }
        }
        dnsFilter = null; activePolicy = null; dnsFilterError = null;
        UpdateFilterView();
    }
    private async void ApplyRules_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitDomainInput()) return;
        await RunAsync(async () =>
        {
            var fresh = await service.ReadAsync();
            if (fresh.State != "On" || fresh.Error is not null || dnsFilter?.Snapshot.Active != true)
                throw new InvalidOperationException("El hotspot y el filtro DNS deben estar encendidos.");
            var draft = ReadDraft(); var policy = DnsPolicy(draft);
            store.Save(draft); savedDraft = draft;
            try { await dnsFilter.ApplyAsync(policy); activePolicy = policy; dnsFilterError = null; }
            catch (Exception ex) { dnsFilterError = "No se confirmó la aplicación de las reglas. " + HotspotService.Friendly(ex); throw; }
        }, "Cambios aplicados.");
    }
    private void UpdateFilterView()
    {
        var snapshot = dnsFilter?.Snapshot;
        bool active = snapshot is { Active: true, Error: null } && status?.State == "On" && status.Error is null;
        string? error = snapshot?.Error ?? dnsFilterError;
        GlobalFilterText.Text = active ? "DNS activo" : dnsFilter is null ? "DNS apagado" : "DNS sin confirmar";
        GlobalFilterText.ToolTip = error ?? "Sólo consultas DNS que los dispositivos envían al hotspot; no garantiza el bloqueo de toda la navegación.";
        FilterStateDot.Fill = new SolidColorBrush(error is not null ? Color.FromRgb(176, 96, 56) : active ? Color.FromRgb(39, 139, 99) : Color.FromRgb(157, 172, 158));
        FilterStateText.Text = active ? "Filtro DNS activo" : dnsFilter is null ? "Filtro DNS apagado" : "Filtro DNS sin confirmar";
        FilterStateSummary.Text = active ? "Activo" : dnsFilter is null ? "Apagado" : "Sin confirmar";
        FilterToggleButton.Content = dnsFilter is null ? "Activar filtro DNS" : active ? "Desactivar filtro" : "Cerrar filtro";
        FilterStatsSummary.Text = active ? $"{snapshot!.Queries:N0} consultas · {snapshot.Blocked:N0} bloqueadas" : "Reglas de dominios del hotspot";
        FilterUsageText.Visibility = snapshot is not null ? Visibility.Visible : Visibility.Collapsed;
        FilterUsageText.Text = snapshot is null ? "" : $"Consultas en esta sesión: {snapshot.Queries:N0} · Bloqueadas: {snapshot.Blocked:N0}";
        ActivePolicyText.Text = active && activePolicy is not null ?
            $"{activePolicy.Domains.Count()} dominios · {(activePolicy.AllowOnly ? "Solo permitidos" : "Lista de bloqueo")}" :
            status?.State == "On" ? "Activa el filtro para aplicar esta lista." : "Enciende el hotspot para activar el filtro.";
        FilterErrorText.Text = error ?? "";
        FilterErrorText.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        UpdateDraftSummary();
    }
    private void UpdateDnsDeviceNames()
    {
        var names = new Dictionary<string, ActivityDevice>(StringComparer.Ordinal);
        var aliases = devicePermissions.Entries.ToDictionary(p => p.Mac, p => p.Name, StringComparer.Ordinal);
        if (status?.State == "On" && status.Error is null)
        {
            var observations = status.Clients.SelectMany(c => (c.IpAddresses ?? []).Select(ip => (Ip: ip, Client: c)));
            foreach (var group in observations.GroupBy(x => x.Ip, StringComparer.Ordinal))
            {
                var clients = group.Select(x => x.Client).ToArray();
                var identities = clients.Select(c => NormalizedDeviceMac(c.Mac) ?? "").Distinct().ToArray();
                if (identities.Length != 1 || identities[0].Length == 0) continue;
                string id = identities[0];
                string name = aliases.TryGetValue(id, out var alias) && !string.IsNullOrWhiteSpace(alias) ? alias : ClientRow.From(clients[0]).DisplayName;
                names[group.Key] = new(id, name, group.Key);
            }
        }
        Volatile.Write(ref dnsDeviceNames, names);
    }
    private void RecordDnsObservation(DnsObservation observation)
    {
        var history = activityStore;
        if (history?.RecordingEnabled != true) return;
        long generation = history.Generation;
        if (observation.At.UtcTicks < Interlocked.Read(ref dnsRecordNotBefore)) return;
        var names = Volatile.Read(ref dnsDeviceNames);
        names.TryGetValue(observation.ClientIp, out var device);
        history.Record(new ActivityEntry(Guid.NewGuid().ToString("N"), observation.At, observation.At, null,
            observation.ClientIp, device?.Name ?? "Dispositivo sin identificar", device?.Id ?? observation.ClientIp,
            observation.Domain, "DNS", observation.Allowed ? "Allowed" : "Blocked", 0, 0, 0), generation);
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Diagnóstico JSON|*.json", FileName = "hen-hotspot-diagnostico.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
            Notice("Diagnóstico exportado. Incluye nombres de red y direcciones de dispositivos; no incluye contraseñas.");
        }
        catch (Exception ex) { Notice(ex.Message, true); }
    }
    private void Notice(string text, bool error = false)
    {
        NoticeText.Text = text; NoticeBorder.Visibility = Visibility.Visible; noticeIsError = error;
        NoticeBorder.Background = new SolidColorBrush(error ? Color.FromRgb(251, 233, 224) : Color.FromRgb(232, 245, 238));
        NoticeText.Foreground = new SolidColorBrush(error ? Color.FromRgb(157, 70, 47) : Color.FromRgb(33, 98, 76));
        noticeTimer.Stop(); if (!error && !busy) noticeTimer.Start();
    }
    private static string BandLabel(string band) => band switch { "TwoPointFourGigahertz" => "2.4 GHz", "FiveGigahertz" => "5 GHz", _ => "Automática" };
}

public record ClientRow(string DisplayName, string Address, string Mac)
{
    public static ClientRow From(ClientInfo client)
    {
        var parts = client.Name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string name = parts.FirstOrDefault(p => !System.Net.IPAddress.TryParse(p, out _)) ?? "Dispositivo sin nombre";
        string address = client.IpAddresses?.FirstOrDefault() ?? parts.FirstOrDefault(p => System.Net.IPAddress.TryParse(p, out _)) ?? client.Mac;
        return new(name, address, client.Mac);
    }
}
