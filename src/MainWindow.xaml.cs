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
        InitializeLanguage();
        InitializeRefreshHandling();
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width - 24);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height - 16);
        Width = Math.Min(1280, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(860, SystemParameters.WorkArea.Height - 16);
        UpdateEnabled();
        Navigate(OverviewPanel, OverviewNav);
        try
        {
            var draft = store.Load();
            savedDraft = draft;
            ModeBox.SelectedIndex = draft.AllowOnly ? 1 : 0;
            foreach (var domain in (draft.Domains ?? []).Select(Validation.Domain).Distinct()) domainEntries.Add(domain);
        }
        catch { Notice(L10n.T("CouldNotReadTheSavedDraftYouCanCreate"), true); }
        Loaded += async (_, _) => { await RefreshAsync(); timer.Start(); };
        timer.Tick += async (_, _) => { if (!busy && !deviceBusy) await RefreshAsync(); };
        Closed += (_, _) => { StopRefreshHandling(); timer.Stop(); noticeTimer.Stop(); activityStore?.Dispose(); };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); if (!busy && !noticeIsError) NoticeBorder.Visibility = Visibility.Collapsed; };
        Closing += async (_, e) =>
        {
            if (busy || deviceBusy || activitySettingsBusy) { e.Cancel = true; Notice(L10n.T("WaitForTheOperationToFinish")); return; }
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
                    Notice(L10n.T("TheAppRemainsOpenBecauseSafeShutdownDidNot") + HotspotService.Friendly(ex), true);
                }
                finally { busy = false; UpdateEnabled(); if (!ready) timer.Start(); }
                if (ready) Close();
            }
        };
        ModeBox.SelectionChanged += (_, _) => MarkDraftChanged();
        UpdateDraftSummary();
    }

    private Button? currentNavigation;
    private void Navigate(StackPanel panel, Button nav)
    {
        foreach (var p in new[] { OverviewPanel, NetworkPanel, RulesPanel, DevicesPanel, ActivityPanel, DiagnosticsPanel, AboutPanel }) p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { OverviewNav, NetworkNav, RulesNav, DeviceNav, ActivityNav, DiagnosticsNav, AboutNav })
        {
            button.Background = new SolidColorBrush(button == nav ? Color.FromRgb(35, 60, 51) : Colors.Transparent);
            button.Foreground = new SolidColorBrush(button == nav ? Color.FromRgb(165, 234, 206) : Color.FromRgb(154, 173, 168));
        }
        currentNavigation = nav;
        UpdatePageHeading();
        PageScroll.ScrollToTop();
        if (!busy && !noticeIsError) NoticeBorder.Visibility = Visibility.Collapsed;
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => Navigate(OverviewPanel, OverviewNav);
    private void Network_Click(object sender, RoutedEventArgs e) => Navigate(NetworkPanel, NetworkNav);
    private void Rules_Click(object sender, RoutedEventArgs e) => Navigate(RulesPanel, RulesNav);
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => Navigate(DiagnosticsPanel, DiagnosticsNav);
    private void About_Click(object sender, RoutedEventArgs e) => Navigate(AboutPanel, AboutNav);
    private void Portfolio_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://hensell.dev/") { UseShellExecute = true }); }
        catch (Exception ex) { Notice(L10n.T("PortfolioOpenFailed") + ex.Message, true); }
    }
    private void AccessGuide_Click(object sender, RoutedEventArgs e) => Rules_Click(sender, e);
    private void DismissNotice_Click(object sender, RoutedEventArgs e) { NoticeBorder.Visibility = Visibility.Collapsed; noticeTimer.Stop(); noticeIsError = false; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { await RefreshAsync(); if (ActivityPanel.Visibility == Visibility.Visible) await RefreshActivityAsync(); if (status?.Error is null) Notice(L10n.T("StatusUpdatedFromWindows")); }

    private async Task RefreshAsync()
    {
        if (refreshing || windowClosed) return;
        refreshing = true;
        try
        {
            status = await service.ReadAsync();
            if (windowClosed) return;
            if ((status.State != "On" || status.Error is not null) && dnsFilter is not null)
            {
                string? previousFilterError = dnsFilter.Snapshot.Error ?? dnsFilterError;
                try
                {
                    await StopDnsFilterAsync();
                    if (previousFilterError is not null)
                    {
                        dnsFilterError = previousFilterError;
                        Notice(L10n.T("TheDNSFilterStopped") + previousFilterError, true);
                    }
                }
                catch (Exception ex)
                {
                    string closeError = HotspotService.Friendly(ex);
                    dnsFilterError = previousFilterError is not null && previousFilterError != closeError
                        ? previousFilterError + L10n.T("ShutdownPending") + closeError : closeError;
                    Notice(L10n.T("DNSFilterShutdownWasNotConfirmed") + dnsFilterError, true);
                }
            }
            UpdateDnsDeviceNames();
            if (WindowState != WindowState.Minimized) RenderStatus();
            if (WindowState != WindowState.Minimized && ActivityPanel.Visibility == Visibility.Visible && activityPageNumber == 0)
                await RefreshActivityAsync(force: false);
            if (!initialized)
            {
                SsidBox.Text = string.IsNullOrEmpty(status.Ssid) ? "Hen Hotspot" : status.Ssid;
                BandBox.SelectedIndex = status.Band switch { "TwoPointFourGigahertz" => 1, "FiveGigahertz" => 2, _ => 0 };
                if (status.Error is not null) Notice(status.Error, true);
                else if (status.Capability != "Enabled") Notice(L10n.F("WindowsReports0CheckDiagnosticsToContinue", status.Capability), true);
                initialized = true;
            }
            else if (status.Error is not null) Notice(status.Error, true);
        }
        catch (Exception ex) { status = null; UpdateEnabled(); Notice(HotspotService.Friendly(ex), true); }
        finally { refreshing = false; if (!windowClosed) UpdateRefreshInterval(); }
    }

    private void RenderStatus()
    {
        if (status is null) { UpdateEnabled(); return; }
        HeroSsidText.Text = string.IsNullOrWhiteSpace(status.Ssid) ? "Hen Hotspot" : status.Ssid;
        StateText.Text = status.State switch { "On" => L10n.F("SharingInternetBand0", BandLabel(status.Band)), "Off" => L10n.T("OffTurnItOnToShareInternet"), "InTransition" => L10n.T("WindowsIsChangingTheStatus"), _ => L10n.T("CouldNotConfirmTheStatus") };
        NetworkSummary.Text = status.Ssid.Length > 0 ? L10n.F("Network0Band1", status.Ssid, BandLabel(status.Band)) : L10n.T("SetUpYourNetworkToGetStarted");
        ToggleButton.Content = status.State == "On" ? L10n.T("TurnOffHotspot") : L10n.T("TurnOnHotspot");
        UpdateEnabled();
        CountText.Text = status.Error is null ? status.ClientCount.ToString(L10n.Culture) : "—";
        ConnectedCountLabel.Text = status.ClientCount == 1 ? L10n.T("Connected") : L10n.T("Connected2");
        CapacityText.Text = status.MaxClients > 0 ? L10n.F("CapacityUpTo0Devices", status.MaxClients) : L10n.T("CapacityUnavailable");
        SourceText.Text = status.Connection;
        InternetText.Text = status.Connectivity == "InternetAccess" ? L10n.T("ConnectionAvailable") : L10n.T("InternetAccessUnconfirmed");
        var clientRows = status.Clients.Select(ClientRow.From).ToArray();
        if (ClientsGrid.ItemsSource is not ClientRow[] previousRows || !previousRows.SequenceEqual(clientRows)) ClientsGrid.ItemsSource = clientRows;
        EmptyClientCard.Visibility = status.Clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyClients.Text = status.Error is not null || status.State == "Unknown" ? L10n.T("CouldNotReadTheDeviceListRefreshTheStatus") :
            status.State == "Off" ? L10n.T("TurnOnTheHotspotAndConnectADeviceIt") :
            status.State == "InTransition" ? L10n.T("WaitForWindowsToFinishChangingTheHotspotStatus") :
            L10n.T("NoDevicesConnectedYetConnectAPhoneToThis");
        ClientsGrid.Visibility = status.Clients.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticText.Text = L10n.F("DiagnosticSummary", Environment.OSVersion.Version, status.Connection, status.Connectivity, status.Capability, status.State, status.MaxClients) +
            string.Join("\n\n", status.Adapters.Select(a => $"{a.Name} · {a.Status}\n{a.Description}")) +
            (status.Error is null ? "" : L10n.F("DETAILS0", status.Error));
        UpdatedText.Text = L10n.F("LastSynced", DateTime.Now);
        UpdateActivityState();
    }

    private void UpdateEnabled()
    {
        bool working = busy || deviceBusy;
        var ui = HotspotUiState.From(status?.State, status?.Capability, status?.Error is not null, working);
        GlobalStateText.Text = ui.Label;
        GlobalStateDot.Fill = (Brush)new BrushConverter().ConvertFromString(ui.Color)!;
        GlobalStateText.ToolTip = status?.Error ?? L10n.T("StatusCheckedDirectlyWithWindows");
        ToggleButton.IsEnabled = ui.CanToggle && !(status?.State == "Off" && (deviceGuard is not null || dnsFilter is not null));
        ToggleButton.ToolTip = status?.State == "Off" && (deviceGuard is not null || dnsFilter is not null)
            ? L10n.T("CloseThePreviousFilterOrAccessControlBeforeTurning") : null;
        SaveNetworkButton.IsEnabled = ui.CanEditNetwork;
        SsidBox.IsEnabled = PasswordBox.IsEnabled = BandBox.IsEnabled = ui.CanEditNetwork;
        ClientsGrid.IsEnabled = ui.CanUseClients;
        ModeBox.IsEnabled = DomainInput.IsEnabled = DomainList.IsEnabled = SaveDraftButton.IsEnabled = !working;
        AddDomainButton.IsEnabled = !working && !string.IsNullOrWhiteSpace(DomainInput.Text);
        bool hasRequiredDomain = ModeBox.SelectedIndex != 1 || domainEntries.Count > 0 || !string.IsNullOrWhiteSpace(DomainInput.Text);
        FilterToggleButton.IsEnabled = !working && (dnsFilter is not null || (ui.CanApplyRules && hasRequiredDomain));
        ApplyRulesButton.IsEnabled = ui.CanApplyRules && hasRequiredDomain && dnsFilter?.Snapshot is { Active: true, Error: null } && !DraftMatchesActive();
        FilterToggleButton.ToolTip = dnsFilter is not null ? L10n.T("StopFilteringDNSQueries") : ui.CanApplyRules
            ? L10n.T("RequiresAdministratorPermission") : L10n.T("TurnOnTheHotspotToEnableTheDNSFilter");
        ApplyRulesButton.ToolTip = dnsFilter?.Snapshot.Active == true ? (DraftMatchesActive() ? L10n.T("TheseRulesAreAlreadyApplied") : L10n.T("RulesApplyToNewDNSQueriesCachedResponsesMay")) : L10n.T("TurnOnTheHotspotAndDNSFilterFirst");
        NetworkLockText.Text = ui.CanEditNetwork ? L10n.T("HotspotOffSettingsAvailable") :
            status?.State == "On" ? L10n.T("TurnOffTheHotspotToEditSettings") :
            working || status?.State == "InTransition" ? L10n.T("WaitForWindowsToFinishTheOperationBeforeEditing") :
            L10n.T("EditingLockedUntilWindowsConfirmsHotspotSettingsAreAvailable");
        RefreshButton.IsEnabled = !working;
        LanguageBox.IsEnabled = !working && !activitySettingsBusy;
        UpdateDraftSummary();
        UpdateDevices();
        UpdateFilterView();
    }
    private async Task RunAsync(Func<Task> operation, string success)
    {
        if (busy || deviceBusy) return;
        busy = true; UpdateEnabled(); Notice(L10n.T("WindowsIsProcessingTheOperation"));
        try { await operation(); await RefreshAsync(); Notice(success); }
        catch (Exception ex) { Notice(HotspotService.Friendly(ex), true); }
        finally { busy = false; UpdateEnabled(); if (!noticeIsError) noticeTimer.Start(); }
    }
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (status?.State == "On") await RunAsync(async () => { await StopDnsFilterAsync(); await CloseDeviceGuardAsync(); if (status?.State != "Off") await service.StopAsync(); }, L10n.T("HotspotDNSFilterAndDeviceAccessControlTurnedOff"));
        else await RunAsync(service.StartAsync, L10n.T("HotspotIsOnConnectAPhoneToTheNetwork"));
    }
    private async void SaveNetwork_Click(object sender, RoutedEventArgs e)
    {
        string ssid = SsidBox.Text, password = PasswordBox.Password, band = (string)((ComboBoxItem)BandBox.SelectedItem).Tag;
        await RunAsync(async () => { await service.ConfigureAsync(ssid, password, band); PasswordBox.Clear(); }, L10n.T("SettingsSavedInWindowsYouCanTurnOnThe"));
    }
    private void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitDomainInput()) return;
        try
        {
            var draft = ReadDraft(); store.Save(draft); savedDraft = draft;
            UpdateDraftSummary();
            Notice(L10n.T("DraftSaved"));
        }
        catch (Exception ex) { Notice(ex.Message, true); }
    }
    private void WindowsSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true }); }
        catch (Exception ex) { Notice(ex.Message, true); }
    }

    private PolicyDraft ReadDraft() => new(savedDraft?.DownloadMbps ?? 3, savedDraft?.UploadMbps ?? 1,
        ModeBox.SelectedIndex == 1 ? "allowlist" : "blocklist",
        domainEntries.ToArray());
    private static DomainPolicy DnsPolicy(PolicyDraft draft) => new(draft.AllowOnly, draft.Domains ?? []);
    private void MarkDraftChanged() { UpdateDraftSummary(); UpdateEnabled(); }
    private bool DraftMatchesActive()
    {
        if (!string.IsNullOrWhiteSpace(DomainInput.Text) || activePolicy is null || dnsFilter?.Snapshot is not { Active: true, Error: null }) return false;
        try
        {
            var draft = ReadDraft(); return draft.AllowOnly == activePolicy.AllowOnly &&
            (draft.Domains ?? []).Order().SequenceEqual(activePolicy.Domains.Order());
        }
        catch (ArgumentException) { return false; }
    }
    private void UpdateDraftSummary()
    {
        int count = domainEntries.Count;
        DomainCountText.Text = $"{count} {(count == 1 ? L10n.T("Domain") : L10n.T("Domains2"))}";
        DomainListTitle.Text = ModeBox.SelectedIndex == 1 ? L10n.T("AllowedDomains") : L10n.T("BlockedDomains");
        EmptyDomainsPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DomainList.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyDomainsText.Text = ModeBox.SelectedIndex == 1 ? L10n.T("AddAtLeastOneDomainToEnableThisList") : L10n.T("AddTheFirstDomainYouWantToBlock");
        if (!string.IsNullOrWhiteSpace(DomainInput.Text)) { DraftStatusText.Text = L10n.T("DomainWaitingToBeAdded"); return; }
        if (DraftMatchesActive()) { DraftStatusText.Text = L10n.T("AllChangesApplied"); return; }
        bool saved = false;
        try { var draft = ReadDraft(); saved = savedDraft is not null && draft.AllowOnly == savedDraft.AllowOnly && (draft.Domains ?? []).Order().SequenceEqual((savedDraft.Domains ?? []).Order()); } catch (ArgumentException) { }
        DraftStatusText.Text = dnsFilter?.Snapshot.Active == true ? (saved ? L10n.T("DraftSavedWaitingToApply") : L10n.T("ChangesNotAppliedToTheDNSFilter")) :
            saved ? L10n.T("DraftSaved") : L10n.T("UnsavedChanges");
    }

    private async void FilterToggle_Click(object sender, RoutedEventArgs e)
    {
        if (dnsFilter is not null) { await RunAsync(StopDnsFilterAsync, L10n.T("DNSFilterOffItsRulesNoLongerApplyTo")); return; }
        if (!CommitDomainInput()) return;
        await RunAsync(async () =>
        {
            var fresh = await service.ReadAsync();
            if (fresh.State != "On" || fresh.Error is not null) throw new InvalidOperationException(L10n.T("TurnOnTheHotspotToEnableTheDNSFilter"));
            var draft = ReadDraft(); var policy = DnsPolicy(draft);
            dnsFilterError = null;
            Interlocked.Exchange(ref dnsRecordNotBefore, DateTimeOffset.UtcNow.UtcTicks);
            var next = new DnsFilterClient(RecordDnsObservation); dnsFilter = next;
            try { await next.StartAsync(policy); activePolicy = policy; }
            catch (Exception ex)
            {
                string error = HotspotService.Friendly(ex);
                try { await StopDnsFilterAsync(); } catch (Exception stop) { error += L10n.T("ShutdownPending") + HotspotService.Friendly(stop); }
                dnsFilterError = error;
                throw;
            }
            try { store.Save(draft); savedDraft = draft; }
            catch (Exception ex) { dnsFilterError = L10n.T("FilterActiveButTheDraftCouldNotBeSaved") + ex.Message; }
        }, L10n.T("DNSFilterEnabled"));
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
                throw new InvalidOperationException(L10n.T("TheHotspotAndDNSFilterMustBeOn"));
            var draft = ReadDraft(); var policy = DnsPolicy(draft);
            store.Save(draft); savedDraft = draft;
            try { await dnsFilter.ApplyAsync(policy); activePolicy = policy; dnsFilterError = null; }
            catch (Exception ex) { dnsFilterError = L10n.T("ApplyingTheRulesWasNotConfirmed") + HotspotService.Friendly(ex); throw; }
        }, L10n.T("ChangesApplied"));
    }
    private void UpdateFilterView()
    {
        var snapshot = dnsFilter?.Snapshot;
        bool active = snapshot is { Active: true, Error: null } && status?.State == "On" && status.Error is null;
        string? error = snapshot?.Error ?? dnsFilterError;
        GlobalFilterText.Text = active ? L10n.T("DNSOn") : dnsFilter is null ? L10n.T("DNSOff") : L10n.T("DNSUnconfirmed");
        GlobalFilterText.ToolTip = error ?? L10n.T("OnlyDNSQueriesThatDevicesSendToTheHotspot");
        FilterStateDot.Fill = new SolidColorBrush(error is not null ? Color.FromRgb(176, 96, 56) : active ? Color.FromRgb(39, 139, 99) : Color.FromRgb(157, 172, 158));
        FilterStateText.Text = active ? L10n.T("DNSFilterOn") : dnsFilter is null ? L10n.T("DNSFilterOff") : L10n.T("DNSFilterUnconfirmed");
        FilterStateSummary.Text = active ? L10n.T("Active") : dnsFilter is null ? L10n.T("Off") : L10n.T("Unconfirmed");
        FilterToggleButton.Content = dnsFilter is null ? L10n.T("EnableDNSFilter") : active ? L10n.T("DisableFilter") : L10n.T("CloseFilter");
        FilterStatsSummary.Text = active ? L10n.F("FilterQueryCounts", snapshot!.Queries, snapshot.Blocked) : L10n.T("HotspotDomainRules");
        FilterUsageText.Visibility = snapshot is not null ? Visibility.Visible : Visibility.Collapsed;
        FilterUsageText.Text = snapshot is null ? "" : L10n.F("QueriesThisSession0N0Blocked1N0", snapshot.Queries, snapshot.Blocked);
        ActivePolicyText.Text = active && activePolicy is not null ?
            L10n.F("ActivePolicySummary", activePolicy.Domains.Count(), (activePolicy.AllowOnly ? L10n.T("AllowlistOnly") : L10n.T("Blocklist"))) :
            status?.State == "On" ? L10n.T("EnableTheFilterToApplyThisList") : L10n.T("TurnOnTheHotspotToEnableTheFilter");
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
            observation.ClientIp, device?.Name ?? "", device?.Id ?? observation.ClientIp,
            observation.Domain, "DNS", observation.Allowed ? "Allowed" : "Blocked", 0, 0, 0), generation);
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = L10n.T("JSONDiagnosticsJson"), FileName = "hen-hotspot-diagnostico.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
            Notice(L10n.T("DiagnosticsExportedIncludesNetworkNamesAndDeviceAddressesDoes"));
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
    private static string BandLabel(string band) => band switch { "TwoPointFourGigahertz" => "2.4 GHz", "FiveGigahertz" => "5 GHz", _ => L10n.T("Automatic") };
}

public record ClientRow(string DisplayName, string Address, string Mac)
{
    public static ClientRow From(ClientInfo client)
    {
        var parts = client.Name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string name = parts.FirstOrDefault(p => !System.Net.IPAddress.TryParse(p, out _)) ?? L10n.T("UnnamedDevice");
        string address = client.IpAddresses?.FirstOrDefault() ?? parts.FirstOrDefault(p => System.Net.IPAddress.TryParse(p, out _)) ?? client.Mac;
        return new(name, address, client.Mac);
    }
}
