using RDMan.ViewModels;

namespace RDMan.Views;

public partial class SettingsWindow : DialogWindow
{
    public SettingsWindow(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }
}
