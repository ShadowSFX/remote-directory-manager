using System.Windows;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class RdgImportWindow : DialogWindow
{
    public RdgImportWindow(RdgImportViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Accept();
}
