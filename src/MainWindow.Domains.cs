using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HenHotspot;

public partial class MainWindow
{
    private readonly ObservableCollection<string> domainEntries = [];

    private void InitializeDomainEditor()
    {
        DomainList.ItemsSource = domainEntries;
        domainEntries.CollectionChanged += (_, _) => MarkDraftChanged();
        DomainInput.TextChanged += (_, _) =>
        {
            DomainInputError.Visibility = Visibility.Collapsed;
            DomainPlaceholder.Visibility = DomainInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            MarkDraftChanged();
        };
    }

    private bool CommitDomainInput()
    {
        if (string.IsNullOrWhiteSpace(DomainInput.Text)) return true;
        try
        {
            string domain = DomainEntryInput.Normalize(DomainInput.Text);
            if (domainEntries.Contains(domain, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(L10n.T("ThatDomainIsAlreadyOnTheList"));
            if (domainEntries.Count >= 128)
                throw new ArgumentException(L10n.T("TheListSupportsUpTo128Domains"));
            domainEntries.Add(domain);
            DomainInput.Clear();
            DomainList.ScrollIntoView(domain);
            return true;
        }
        catch (ArgumentException ex)
        {
            DomainInputError.Text = ex.Message;
            DomainInputError.Visibility = Visibility.Visible;
            DomainInput.Focus();
            return false;
        }
    }

    private void AddDomain_Click(object sender, RoutedEventArgs e)
    {
        if (busy || deviceBusy) return;
        if (CommitDomainInput()) DomainInput.Focus();
    }

    private void DomainInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddDomain_Click(sender, e);
    }

    private void RemoveDomain_Click(object sender, RoutedEventArgs e)
    {
        if (busy || deviceBusy || sender is not Button { Tag: string domain }) return;
        var dialog = new DomainRemovalDialog(domain, ModeBox.SelectedIndex == 1) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        domainEntries.Remove(domain);
        DomainInput.Focus();
    }
}
