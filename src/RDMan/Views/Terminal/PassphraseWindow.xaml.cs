using System.Windows;
using RDMan.Infrastructure;

namespace RDMan.Views;

/// <summary>"Unlock key …": asked when importing a private key protected with a passphrase.</summary>
public partial class PassphraseWindow : DialogWindow
{
    public PassphraseWindow(string keyName, bool wrong)
    {
        InitializeComponent();
        Title = Loc.F("Term.Prompt.Passphrase.Title", keyName);
        KeyName.Text = keyName;
        WrongText.Visibility = wrong ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PassBox.Focus();
        Closed += (_, _) => PassBox.Clear();
    }

    public string Passphrase { get; private set; } = "";
    public bool Remember { get; private set; }

    protected override void Accept()
    {
        if (PassBox.SecurePassword.Length == 0)
        {
            PassBox.Focus();
            return;
        }
        Passphrase = PassBox.Password;
        Remember = RememberBox.IsChecked == true;
        DialogResult = true;
    }

    private void Unlock_Click(object sender, RoutedEventArgs e) => Accept();

    private void PassBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        PassBox.Tag = PassBox.SecurePassword.Length == 0 ? "empty" : null;
        WrongText.Visibility = Visibility.Collapsed;
    }
}
