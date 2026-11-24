using System.Windows;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class BulkAddWindow : DialogWindow
{
    private readonly BulkAddViewModel _vm;

    public BulkAddWindow(BulkAddViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => PatternsBox.Focus();
    }

    /// <summary>Number of servers added (0 when cancelled).</summary>
    public int Added { get; private set; }

    protected override void Accept()
    {
        if (_vm.TryApply() is { } count)
        {
            Added = count;
            DialogResult = true;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Accept();
}
