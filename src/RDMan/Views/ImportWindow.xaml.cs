using System.Windows;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class ImportWindow : DialogWindow
{
    private readonly ImportViewModel _vm;

    public ImportWindow(ImportViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    protected override void Accept()
    {
        if (_vm.SelectedCount > 0) DialogResult = true;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Accept();
}
