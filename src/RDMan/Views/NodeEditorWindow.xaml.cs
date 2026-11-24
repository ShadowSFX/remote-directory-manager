using System.Windows;
using System.Windows.Controls;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class NodeEditorWindow : DialogWindow
{
    private readonly NodeEditorViewModel _vm;
    private bool _syncingPassword;

    public NodeEditorWindow(NodeEditorViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (vm.FocusCredentials)
            {
                CredentialsSection.BringIntoView();
                if (vm.Credentials.IsCustom) Password.Focus();
            }
            else if (vm is ServerEditorViewModel { IsNew: true }) AddressBox.Focus();
            else
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
        };
        Closed += (_, _) => vm.Credentials.NewPassword = "";
    }

    protected override void Accept()
    {
        if (_vm.TryApply()) DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Accept();

    private void Password_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Password.Tag = Password.SecurePassword.Length == 0 ? "empty" : null;
        if (_syncingPassword) return;
        _vm.Credentials.NewPassword = Password.Password;
        if (_vm.ServerFields is { } s) s.TestResult = null;
    }

    private void PasswordPlain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPassword) return;
        _vm.Credentials.NewPassword = PasswordPlain.Text;
    }

    private void ShowPassword_Changed(object sender, RoutedEventArgs e)
    {
        _syncingPassword = true;
        try
        {
            if (ShowPassword.IsChecked == true)
            {
                PasswordPlain.Text = _vm.Credentials.NewPassword;
                PasswordPlain.Visibility = Visibility.Visible;
                Password.Visibility = Visibility.Hidden;
                PasswordPlain.Focus();
                PasswordPlain.CaretIndex = PasswordPlain.Text.Length;
            }
            else
            {
                Password.Password = _vm.Credentials.NewPassword;
                PasswordPlain.Text = "";
                PasswordPlain.Visibility = Visibility.Collapsed;
                Password.Visibility = Visibility.Visible;
                Password.Focus();
            }
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void Swatch_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb) rb.IsChecked = string.Equals(rb.Tag as string, _vm.ColorKey, StringComparison.OrdinalIgnoreCase);
    }

    private void Swatch_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb) _vm.ColorKey = rb.Tag as string;
    }

    private void ShareMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ServerFields is not { } s) return;
        var menu = new ContextMenu { PlacementTarget = ShareMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var share in s.DiscoveredShares)
        {
            var item = new MenuItem { Header = share, Tag = "" };
            item.Click += (_, _) => s.Share = share;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
