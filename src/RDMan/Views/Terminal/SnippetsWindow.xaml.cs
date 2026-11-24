using System.Windows;
using RDMan.ViewModels.Terminal;

namespace RDMan.Views;

public partial class SnippetsWindow : DialogWindow
{
    private readonly SnippetsViewModel _vm;

    public SnippetsWindow(SnippetsViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    // Enter belongs to the multi-line body here: only the button (or Ctrl+Enter) saves.
    protected override void Accept()
    {
        _vm.Save();
        DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Accept();
}
