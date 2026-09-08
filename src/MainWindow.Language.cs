using System.Windows;
using System.Windows.Controls;

namespace HenHotspot;

public partial class MainWindow
{
    private readonly PreferencesStore preferences = new();
    private bool languageReady;

    private void InitializeLanguage()
    {
        LanguageBox.SelectedValue = L10n.LanguageCode;
        languageReady = true;
    }

    private async void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!languageReady || LanguageBox.SelectedValue is not string selected || selected == L10n.LanguageCode) return;
        string previous = L10n.LanguageCode;
        try { preferences.SaveLanguage(selected); }
        catch (Exception ex)
        {
            LanguageBox.SelectedValue = previous;
            Notice(L10n.T("LanguageSaveFailed") + ex.Message, true);
            return;
        }
        var fromDate = ActivityFromDate.SelectedDate;
        var untilDate = ActivityUntilDate.SelectedDate;
        L10n.SetLanguage(selected);
        L10n.ApplyResources(Application.Current);
        L10n.RefreshDatePicker(ActivityFromDate, fromDate);
        L10n.RefreshDatePicker(ActivityUntilDate, untilDate);
        UpdatePageHeading();
        // Keep inputs, selections, drafts and active network sessions in place.
        NoticeText.Text = L10n.TranslateNotice(NoticeText.Text, previous);
        DomainInputError.Text = L10n.TranslateNotice(DomainInputError.Text, previous);
        if (dnsFilterError is not null) dnsFilterError = L10n.TranslateNotice(dnsFilterError, previous);
        if (deviceError is not null) deviceError = L10n.TranslateNotice(deviceError, previous);
        RenderStatus();
        UpdateActivityState();
        ActivityGrid.Items.Refresh();
        activityRevision++;
        if (ActivityPanel.Visibility == Visibility.Visible) await RefreshActivityAsync();
    }

    private void UpdatePageHeading()
    {
        string title = currentNavigation?.Name switch
        {
            "NetworkNav" => L10n.T("WiFiNetwork"),
            "RulesNav" => L10n.T("Domains"),
            "DeviceNav" => L10n.T("Devices"),
            "ActivityNav" => L10n.T("Activity"),
            "DiagnosticsNav" => L10n.T("Diagnostics"),
            "AboutNav" => L10n.T("AboutMe"),
            _ => L10n.T("Overview")
        };
        PageTitle.Text = BreadcrumbText.Text = title;
        PageSubtitle.Text = currentNavigation == ActivityNav ? L10n.T("HotspotDNSQueries") : "";
        PageSubtitle.Visibility = PageSubtitle.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = currentNavigation == AboutNav ? Visibility.Collapsed : Visibility.Visible;
    }
}
