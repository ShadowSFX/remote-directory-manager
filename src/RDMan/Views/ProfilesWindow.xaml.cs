using System.Windows;
using System.Windows.Controls;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class ProfilesWindow : DialogWindow
{
    private readonly ProfilesViewModel _vm;

    public ProfilesWindow(ProfilesViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    protected override void Accept()
    {
        if (_vm.TryApply()) DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Accept();

    private void Password_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox { DataContext: ProfileItemViewModel item } box) return;
        box.Tag = box.SecurePassword.Length == 0 ? "empty" : null;
        item.NewPassword = box.Password;
    }
}
