using System.Windows;

namespace HenHotspot;

public partial class DomainRemovalDialog : Window
{
    public DomainRemovalDialog(string domain, bool allowOnly)
    {
        InitializeComponent();
        DomainText.Text = domain;
        EffectText.Text = allowOnly
            ? "Se quitará de la lista de permitidos. El filtro cambiará cuando apliques las reglas."
            : "Se quitará de la lista de bloqueo. El filtro cambiará cuando apliques las reglas.";
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
