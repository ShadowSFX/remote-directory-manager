using System.Windows;
using RDMan.Infrastructure;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class SmartGroupWindow : DialogWindow
{
    private readonly SmartGroupEditorViewModel _vm;

    public SmartGroupWindow(SmartGroupEditorViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.RequestClose += (_, _) => DialogResult = false;
        Loaded += (_, _) => NameBox.Focus();
    }

    protected override void Accept()
    {
        if (_vm.TryApply()) DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Accept();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.Confirm(this, Loc.T("Common.Delete"), Loc.F("Smart.ConfirmDelete", _vm.Name), Loc.T("Common.Delete"), danger: true))
            _vm.DeleteCommand.Execute(null);
    }
}
