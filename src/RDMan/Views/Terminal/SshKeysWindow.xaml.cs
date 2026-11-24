using System.Windows;
using RDMan.ViewModels.Terminal;

namespace RDMan.Views;

public partial class SshKeysWindow : DialogWindow
{
    private readonly SshKeysViewModel _vm;

    public SshKeysWindow(SshKeysViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Closing += (_, _) => _vm.Save(); // renames apply however the window closes
    }

    protected override void Accept() => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Accept();
}
