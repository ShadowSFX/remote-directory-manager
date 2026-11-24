using System.Windows;
using RDMan.ViewModels.Terminal;

namespace RDMan.Views;

public partial class SshConfigImportWindow : DialogWindow
{
    private readonly SshConfigImportViewModel _vm;

    public SshConfigImportWindow(SshConfigImportViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    public int Imported { get; private set; }

    protected override void Accept()
    {
        Imported = _vm.Import();
        DialogResult = true;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Accept();
}
