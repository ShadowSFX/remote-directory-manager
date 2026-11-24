using System.Windows;
using System.Windows.Media;
using RDMan.Infrastructure;

namespace RDMan.Views;

public sealed record PromptedCredential(string Username, string Password, bool Remember);

/// <summary>"Sign in to …" — asked when a desktop has no usable credential, or Windows rejected the saved one.</summary>
public partial class CredentialPromptWindow : DialogWindow
{
    public CredentialPromptWindow(string serverName, string address, Brush? identity, string? username)
    {
        InitializeComponent();
        Title = Loc.F("CredPrompt.Title", serverName);
        ServerName.Text = serverName;
        ServerAddress.Text = address;
        BodyText.Text = Loc.F("CredPrompt.Body", serverName);
        if (identity is not null) IdentityBar.Background = identity;
        else IdentityBar.SetResourceReference(BackgroundProperty, "Line.Strong");
        UserBox.Text = username ?? "";
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus();
            else PasswordBox.Focus();
        };
        Closed += (_, _) => PasswordBox.Clear();
    }

    public PromptedCredential? Result { get; private set; }

    protected override void Accept()
    {
        var user = UserBox.Text.Trim();
        if (user.Length == 0)
        {
            UserBox.Focus();
            return;
        }
        Result = new PromptedCredential(user, PasswordBox.Password, RememberBox.IsChecked == true);
        DialogResult = true;
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => Accept();

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        => PasswordBox.Tag = PasswordBox.SecurePassword.Length == 0 ? "empty" : null;
}
