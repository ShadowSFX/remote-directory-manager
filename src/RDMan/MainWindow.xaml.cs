using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RDMan.Infrastructure;
using RDMan.Interop;
using RDMan.ViewModels;

namespace RDMan;

public partial class MainWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_MOUSELEAVE = 0x02A3;
    private const int HTMAXBUTTON = 9;

    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        vm.Owner = this;
        DataContext = vm;
        InitializeComponent();
        RestorePlacement();
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateMaximizeState();
        Activated += (_, _) => App.Services.Clipboard.Revalidate();
        Closing += OnClosing;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && vm.Palette.IsOpen)
                Dispatcher.BeginInvoke(() => { PaletteInput.Focus(); Keyboard.Focus(PaletteInput); }, System.Windows.Threading.DispatcherPriority.Input);
            if (e.PropertyName == nameof(CommandPaletteViewModel.Selected) && vm.Palette.Selected is { } sel)
                PaletteList.ScrollIntoView(sel);
        };
        ThemeManager.Instance.ThemeChanged += (_, _) => ApplyBackdrop();
        // Give the keyboard a home at startup so shortcuts (Ctrl+K, Ctrl+T…) work before the first click.
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(FocusContent, System.Windows.Threading.DispatcherPriority.Input);
        SidebarColumn.Width = new GridLength(vm.State.Settings.SidebarWidth);
        UpdateSplitColumns();
        UpdateMaximizeState();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowEffects.Apply(this, ThemeManager.Instance.IsDark);
        ApplyBackdrop();
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    /// <summary>
    /// Native dark/light frame + rounded corners. The backdrop is a solid surface on purpose: extending the
    /// glass frame for Mica makes DWM paint its own caption buttons under ours.
    /// </summary>
    private void ApplyBackdrop()
    {
        WindowEffects.Apply(this, ThemeManager.Instance.IsDark);
        SetResourceReference(BackgroundProperty, "Bg.Base");
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSplit)) UpdateSplitColumns();
    }

    private void UpdateSplitColumns()
    {
        if (_vm.IsSplit)
        {
            PaneSplitterColumn.Width = new GridLength(5);
            RightPaneColumn.Width = new GridLength(1, GridUnitType.Star);
            RightPaneColumn.MinWidth = 320;
        }
        else
        {
            PaneSplitterColumn.Width = new GridLength(0);
            RightPaneColumn.MinWidth = 0;
            RightPaneColumn.Width = new GridLength(0);
        }
    }

    // ───────────── Window chrome ─────────────
    private void UpdateMaximizeState()
    {
        var maximized = WindowState == WindowState.Maximized;
        // WindowChrome draws outside the work area when maximized; inset by the resize frame.
        Root.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = maximized ? "" : "";
        MaxButton.ToolTip = Loc.T(maximized ? "Title.Restore" : "Title.Maximize");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Report the maximize button as HTMAXBUTTON so Windows 11 shows Snap Layouts on hover.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaxButton(lParam))
                {
                    MaxButton.Tag = "hover";
                    handled = true;
                    return HTMAXBUTTON;
                }
                MaxButton.Tag = null;
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                ToggleMaximize();
                break;
            case WM_NCMOUSELEAVE:
            case WM_MOUSELEAVE:
                MaxButton.Tag = null;
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible) return false;
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        try
        {
            var topLeft = MaxButton.PointToScreen(new Point(0, 0));
            var bottomRight = MaxButton.PointToScreen(new Point(MaxButton.ActualWidth, MaxButton.ActualHeight));
            return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Focuses the visible file list, or the connection tree when a dashboard is showing.</summary>
    public void FocusContent()
    {
        var list = FindVisible<ListView>(this);
        if (list is not null)
        {
            list.Focus();
            return;
        }
        var tree = FindVisible<TreeView>(this);
        if (tree is not null) tree.Focus();
        else Keyboard.Focus(this);
    }

    private static T? FindVisible<T>(DependencyObject root) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.IsVisible) return t;
            if (child is UIElement { IsVisible: false }) continue;
            var nested = FindVisible<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    public void BringToFront()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9 /* SW_RESTORE */);
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ───────────── Placement ─────────────
    private void RestorePlacement()
    {
        var p = _vm.State.Settings.Window;
        Width = p.Width;
        Height = p.Height;
        if (p.Left is { } left && p.Top is { } top && IsOnScreen(left, top, p.Width, p.Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Guards against restoring onto a monitor that is no longer connected.</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vRight = vLeft + SystemParameters.VirtualScreenWidth;
        var vBottom = vTop + SystemParameters.VirtualScreenHeight;
        return left + 120 > vLeft && top + 40 > vTop && left + 120 < vRight && top + 20 < vBottom && width > 0 && height > 0;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var p = _vm.State.Settings.Window;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            p.Left = bounds.Left;
            p.Top = bounds.Top;
            p.Width = bounds.Width;
            p.Height = bounds.Height;
        }
        p.Maximized = WindowState == WindowState.Maximized;
        ((App)Application.Current).PrepareShutdown();
    }

    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _vm.State.Settings.SidebarWidth = SidebarColumn.ActualWidth;
        _vm.State.MarkSettingsDirty();
    }

    // ───────────── Menu ─────────────
    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string key, string glyph, ICommand cmd, string? gesture = null)
            => new() { Header = Loc.T(key), Tag = glyph, Command = cmd, InputGestureText = gesture ?? "" };
        menu.Items.Add(Item("Menu.NewServer", "", _vm.NewServerCommand, "Ctrl+N"));
        menu.Items.Add(Item("Menu.NewGroup", "", _vm.NewGroupCommand, "Ctrl+Shift+G"));
        menu.Items.Add(Item("Menu.Profiles", "", _vm.ManageProfilesCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Menu.ImportNetUse", "", _vm.ImportFromWindowsCommand));
        menu.Items.Add(Item("Menu.ImportFile", "", _vm.ImportFileCommand));
        menu.Items.Add(Item("Menu.ExportFile", "", _vm.ExportFileCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Menu.OpenDataFolder", "", _vm.OpenDataFolderCommand));
        menu.Items.Add(Item("Menu.CheckUpdates", "", _vm.CheckUpdatesCommand));
        menu.Items.Add(Item("Menu.About", "", _vm.AboutCommand));
        menu.IsOpen = true;
    }

    // ───────────── Palette ─────────────
    private void PaletteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: _vm.Palette.Move(+1); e.Handled = true; break;
            case Key.Up: _vm.Palette.Move(-1); e.Handled = true; break;
            case Key.PageDown: _vm.Palette.Move(+8); e.Handled = true; break;
            case Key.PageUp: _vm.Palette.Move(-8); e.Handled = true; break;
            case Key.Enter:
                _vm.Palette.Accept(newTab: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.Palette.Close();
                e.Handled = true;
                break;
        }
    }

    private void PaletteScrim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PaletteLayer)) _vm.Palette.Close();
    }

    private void PaletteCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void PaletteList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null) return;
        _vm.Palette.Accept(newTab: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Mouse back/forward-style keyboard navigation for the active tab, unless typing in a text box.
        var tab = _vm.ActiveTab;
        var typing = Keyboard.FocusedElement is TextBox;
        if (tab is not null && !_vm.Palette.IsOpen)
        {
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (mods == ModifierKeys.Alt && key == Key.Left) { tab.BackCommand.Execute(null); e.Handled = true; }
            else if (mods == ModifierKeys.Alt && key == Key.Right) { tab.ForwardCommand.Execute(null); e.Handled = true; }
            // In the connection tree Alt+↑/↓ reorder nodes, so the tab's "up" only applies elsewhere.
            else if (mods == ModifierKeys.Alt && key == Key.Up && Ui.FindAncestor<TreeView>(Keyboard.FocusedElement as DependencyObject) is null)
            { tab.UpCommand.Execute(null); e.Handled = true; }
            else if (key == Key.F5 && mods == ModifierKeys.None) { tab.RefreshCommand.Execute(null); e.Handled = true; }
            else if (mods == ModifierKeys.Control && key == Key.L) { tab.EditPathCommand.Execute(null); e.Handled = true; }
            else if (mods == ModifierKeys.Alt && key == Key.D) { tab.EditPathCommand.Execute(null); e.Handled = true; }
            else if (mods == ModifierKeys.Control && key == Key.F && !typing) { tab.IsFilterOpen = true; e.Handled = true; }
            else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N) { tab.NewFolderCommand.Execute(null); e.Handled = true; }
        }
        if (!e.Handled) base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        // Mouse side buttons (XButton1/2) navigate like in Explorer.
        if (_vm.ActiveTab is { } tab)
        {
            if (e.ChangedButton == MouseButton.XButton1) { tab.BackCommand.Execute(null); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { tab.ForwardCommand.Execute(null); e.Handled = true; }
        }
        base.OnPreviewMouseDown(e);
    }
}
