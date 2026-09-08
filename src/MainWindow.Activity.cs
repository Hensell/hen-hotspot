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
    private long displayedStoreRevision = -1;
    private int displayedFilterRevision = -1;
    private const int ActivityPageSize = 50;

    private void InitializeActivity()
    {
        ActivityFromDate.SelectedDate = DateTime.Today;
        ActivityUntilDate.SelectedDate = DateTime.Today;
        ActivityDeviceBox.ItemsSource = new[] { new ActivityDeviceOption("", L10n.T("AllDevices")) };
        ActivityDeviceBox.SelectedIndex = 0;
        try { activityStore = new ActivityStore(); }
        catch (Exception ex) { ActivityErrorText.Text = L10n.T("HistoryIsUnavailable") + ex.Message; ActivityErrorText.Visibility = Visibility.Visible; }
        RetentionBox.SelectedIndex = (activityStore?.RetentionDays ?? 7) switch { 1 => 0, 30 => 2, 90 => 3, _ => 1 };
        CaptureActivityFilter();
        UpdateActivityState();
    }

    private async void Activity_Click(object sender, RoutedEventArgs e)
    {
        Navigate(ActivityPanel, ActivityNav);
        await RefreshActivityAsync();
    }

    private bool CaptureActivityFilter()
    {
        if (ActivityFromDate.SelectedDate is not DateTime from || ActivityUntilDate.SelectedDate is not DateTime until || until.Date < from.Date)
        {
            Notice(L10n.T("ChooseAStartDateAndAnEndDateOn"), true); return false;
        }
        if (until.Date >= DateTime.MaxValue.Date) { Notice(L10n.T("TheEndDateIsOutOfRange"), true); return false; }
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
        ActivityRecordButton.Content = enabled ? L10n.T("PauseRecording") : L10n.T("ResumeRecording");
        ActivityRecordButton.IsEnabled = activityStore is not null && !activitySettingsBusy;
        SaveRetentionButton.IsEnabled = ClearActivityStartButton.IsEnabled = activityStore is not null && !activitySettingsBusy;
        ActivityStateText.Text = activityStore is null ? L10n.T("HistoryUnavailable") : enabled ?
            (dnsFilter?.Snapshot.Active == true ? L10n.T("DNSQueryRecordingActive") : L10n.T("RecordingWaitingForDNSFilter")) : L10n.T("RecordingPaused");
        ActivityRetentionText.Text = activityStore is null ? "" : L10n.F("Retention01StoredOnThisLaptop", activityStore.RetentionDays, (activityStore.RetentionDays == 1 ? L10n.T("Day") : L10n.T("Days")));
        if (activityStore?.Error is string error)
        {
            ActivityErrorText.Text = error + L10n.T("FilteringMayStillWorkHistoryMayBeIncomplete");
            ActivityErrorText.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshActivityAsync(bool force = true)
    {
        UpdateActivityState();
        if (activityStore is null || activityFilter is null || windowClosed) return;
        if (activityLoading) { if (force) activityReload = true; return; }
        long storeRevision = activityStore.Revision;
        if (!force && activityStore.Error is null && storeRevision == displayedStoreRevision && activityRevision == displayedFilterRevision) return;
        activityLoading = true;
        ActivityPreviousButton.IsEnabled = ActivityNextButton.IsEnabled = false;
        var filter = activityFilter; int page = activityPageNumber, revision = activityRevision;
        try
        {
            var data = await Task.Run(() => (Page: activityStore.Query(filter, page, ActivityPageSize), Devices: activityStore.GetDevices()));
            if (windowClosed) return;
            if (revision != activityRevision) { activityReload = true; return; }
            int pages = Math.Max(1, (data.Page.TotalCount + ActivityPageSize - 1) / ActivityPageSize);
            if (activityPageNumber >= pages) { activityPageNumber = pages - 1; activityRevision++; activityReload = true; return; }
            var rows = data.Page.Entries.Select(entry => new ActivityRow(entry)).ToArray();
            if (ActivityGrid.ItemsSource is not ActivityRow[] previous || !previous.Select(x => x.Entry).SequenceEqual(rows.Select(x => x.Entry)))
                ActivityGrid.ItemsSource = rows;
            EmptyActivityBorder.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ActivityGrid.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyActivityText.Text = activityStore.RecordingEnabled ?
                L10n.T("NoQueriesMatchTheseFilters") :
                L10n.T("NoMatchesRecordingIsPaused");
            ActivitySummaryText.Text = L10n.F("Records0N0Blocked1N0", data.Page.TotalCount, data.Page.BlockedCount);
            ActivityPageText.Text = L10n.F("Page0Of1", activityPageNumber + 1, pages);
            ActivityPreviousButton.IsEnabled = activityPageNumber > 0;
            ActivityNextButton.IsEnabled = activityPageNumber + 1 < pages;
            ActivityPreviousButton.Visibility = ActivityNextButton.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            var devices = new[] { new ActivityDeviceOption("", L10n.T("AllDevices")) }
                .Concat(data.Devices.Select(d => new ActivityDeviceOption(d.Id, $"{(string.IsNullOrEmpty(d.Name) ? L10n.T("UnidentifiedDevice") : d.Name)} · {d.Ip}"))).ToArray();
            if (ActivityDeviceBox.ItemsSource is not ActivityDeviceOption[] old || !old.SequenceEqual(devices))
            {
                string selected = ActivityDeviceBox.SelectedValue as string ?? "";
                if (selected.Length > 0 && !devices.Any(d => d.Id == selected) && ActivityDeviceBox.SelectedItem is ActivityDeviceOption removed)
                    devices = devices.Append(removed).ToArray();
                ActivityDeviceBox.ItemsSource = devices; ActivityDeviceBox.SelectedValue = selected;
            }
            ActivityErrorText.Visibility = activityStore.Error is null ? Visibility.Collapsed : Visibility.Visible;
            if (activityStore.Error is null)
            {
                displayedStoreRevision = storeRevision;
                displayedFilterRevision = revision;
            }
            UpdateActivityState();
        }
        catch (Exception ex)
        {
            ActivityErrorText.Text = L10n.T("CouldNotQueryHistory") + ex.Message;
            ActivityErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            activityLoading = false;
            if (activityReload && !windowClosed) { activityReload = false; _ = RefreshActivityAsync(); }
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
            Notice(enabled ? L10n.T("RecordingResumedNewDNSQueriesWillBeSaved") : L10n.T("RecordingPausedTheFilterKeepsItsActiveRules"));
        }
        catch (Exception ex) { Notice(L10n.T("CouldNotChangeRecording") + ex.Message, true); }
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
            Notice(L10n.F("RetentionSaved01OlderRecordsAreDeletedAutomatically", days, (days == 1 ? L10n.T("Day") : L10n.T("Days"))));
        }
        catch (Exception ex) { Notice(L10n.T("CouldNotSaveRetention") + ex.Message, true); }
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
            Notice(L10n.T("HistoryClearedIfRecordingIsActiveItWillContinue"));
        }
        catch (Exception ex) { Notice(L10n.T("CouldNotClearHistory") + ex.Message, true); }
        finally { activitySettingsBusy = false; UpdateActivityState(); }
    }
}

public record ActivityDeviceOption(string Id, string Label);

public record ActivityRow(ActivityEntry Entry)
{
    public string Domain => Entry.Domain.Length == 0 ? L10n.T("NoValidDomain") : Entry.Domain;
    public string Device => string.IsNullOrEmpty(Entry.DeviceName) ? L10n.T("UnidentifiedDevice") : Entry.DeviceName;
    public string Ip => Entry.ClientIp;
    private bool IsDns => Entry.Protocol == "DNS";
    public string Protocol => IsDns ? L10n.T("DNSQuery") : Entry.Protocol == "Unknown" ? L10n.T("PreviousRecord") : Entry.Protocol + L10n.T("Historical");
    public string Date => Entry.StartedAt.ToLocalTime().ToString("T", L10n.Culture) + "\n" + Entry.StartedAt.ToLocalTime().ToString("d", L10n.Culture);
    public string Duration => IsDns ? "—" : Entry.DurationSeconds < 60 ? string.Create(L10n.Culture, $"{Entry.DurationSeconds:F1} s") :
        Entry.DurationSeconds < 3600 ? string.Create(L10n.Culture, $"{(int)(Entry.DurationSeconds / 60)} min {(int)(Entry.DurationSeconds % 60)} s") :
        string.Create(L10n.Culture, $"{(int)(Entry.DurationSeconds / 3600)} h {(int)(Entry.DurationSeconds % 3600 / 60)} min");
    public string Status => Entry.Outcome switch { "Connecting" => L10n.T("Connecting"), "Blocked" => L10n.T("Blocked"), "Error" => L10n.T("ErrorLabel"), "Interrupted" => L10n.T("Interrupted"), _ => Entry.EndedAt is null ? L10n.T("InProgress") : L10n.T("Allowed") };
    public string StatusColor => Entry.Outcome switch { "Blocked" or "Error" => "#FBE9E0", "Interrupted" => "#F0F1EE", _ => "#E8F5EE" };
    public string Traffic => IsDns ? "—" : $"↓ {Bytes(Entry.DownloadBytes)}\n↑ {Bytes(Entry.UploadBytes)}";
    public string Details => IsDns ? L10n.F("DnsRecordDetails", Domain, Device, Ip, Entry.StartedAt.ToLocalTime()) : L10n.F("LegacyRecordDetails", Domain, Device, Ip, Entry.StartedAt.ToLocalTime(), (Entry.EndedAt is null ? L10n.T("InProgress2") : Entry.EndedAt.Value.ToLocalTime().ToString("g", L10n.Culture)), (Entry.LastTrafficAt is null ? L10n.T("NoTransfer") : Entry.LastTrafficAt.Value.ToLocalTime().ToString("g", L10n.Culture)));
    public static string Bytes(long bytes) => bytes < 1000 ? string.Create(L10n.Culture, $"{bytes} B") : bytes < 1_000_000 ? string.Create(L10n.Culture, $"{bytes / 1000d:F1} KB") : bytes < 1_000_000_000 ? string.Create(L10n.Culture, $"{bytes / 1_000_000d:F2} MB") : string.Create(L10n.Culture, $"{bytes / 1_000_000_000d:F2} GB");
}
