using System.Windows;

namespace HenHotspot;

public partial class DomainRemovalDialog : Window
{
    public DomainRemovalDialog(string domain, bool allowOnly)
    {
        InitializeComponent();
        DomainText.Text = domain;
        EffectText.Text = allowOnly
            ? L10n.T("ItWillBeRemovedFromTheAllowlistTheFilter")
            : L10n.T("ItWillBeRemovedFromTheBlocklistTheFilter");
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
