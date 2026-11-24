using System.Windows;
using System.Windows.Controls;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class NodeEditorWindow : DialogWindow
{
    private readonly NodeEditorViewModel _vm;

    public NodeEditorWindow(NodeEditorViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (vm.FocusCredentials)
            {
                // Opened from an error panel ("Edit credentials"): land where the problem is.
                vm.ShowSection(EditorSection.Files);
                FilesCredentials.BringIntoView();
            }
            else if (vm is ServerEditorViewModel { IsNew: true }) AddressBox.Focus();
            else
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NodeEditorViewModel.SelectedSection)) ContentScroll.ScrollToTop();
        };
        Closed += (_, _) =>
        {
            vm.Credentials.NewPassword = "";
            vm.DesktopCredentials.NewPassword = "";
            vm.Ssh.NewPassword = null;
        };
    }

    protected override void Accept()
    {
        if (_vm.TryApply()) DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Accept();

    /// <summary>Both credential blocks share one template: the PasswordBox's DataContext says which one it feeds.</summary>
    private void Password_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox { DataContext: CredentialEditorViewModel cred } box) return;
        box.Tag = box.SecurePassword.Length == 0 ? "empty" : null;
        cred.NewPassword = box.Password;
        if (_vm.ServerFields is { } s) s.ClearTestResults();
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

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
        => (AddressBox.DataContext as ServerEditorViewModel)?.CommitAddress();

    /// <summary>A pasted "\host\shareolder" or "host:port" is split right away (after the text lands in the box).</summary>
    private void AddressBox_Pasting(object sender, DataObjectPastingEventArgs e)
        => Dispatcher.BeginInvoke(() => (AddressBox.DataContext as ServerEditorViewModel)?.CommitAddress(), System.Windows.Threading.DispatcherPriority.Input);

    private void SshPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox { DataContext: ViewModels.Terminal.SshEditorViewModel ssh } box) return;
        box.Tag = box.SecurePassword.Length == 0 ? "empty" : null;
        ssh.NewPassword = box.Password;
    }

    private void GatewayPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: GatewayEditorViewModel gateway } box) gateway.NewPassword = box.Password;
    }
}
