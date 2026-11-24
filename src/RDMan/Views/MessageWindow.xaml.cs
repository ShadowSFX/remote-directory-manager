using System.Windows;

namespace RDMan.Views;

public partial class MessageWindow : DialogWindow
{
    public MessageWindow(string title, string message, string confirmText, string cancelText, bool danger)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        if (danger) ConfirmButton.SetResourceReference(StyleProperty, "Btn.Danger");
        // Destructive confirmations default to Cancel so a stray Enter never deletes anything.
        if (danger) CancelButton.IsDefault = true;
        else ConfirmButton.IsDefault = true;
        Loaded += (_, _) => (danger ? CancelButton : ConfirmButton).Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
