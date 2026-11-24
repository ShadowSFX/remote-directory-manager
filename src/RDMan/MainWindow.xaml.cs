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
            {
                // The palette is a topmost popup: never let it appear while another app is in front.
                if (!IsActive) Activate();
                if (!IsActive)
                {
                    vm.Palette.Close();
                    return;
                }
                Dispatcher.BeginInvoke(() => { PaletteInput.Focus(); Keyboard.Focus(PaletteInput); }, System.Windows.Threading.DispatcherPriority.Input);
            }
            if (e.PropertyName == nameof(CommandPaletteViewModel.Selected) && vm.Palette.Selected is { } sel)
                PaletteList.ScrollIntoView(sel);
        };
        ThemeManager.Instance.ThemeChanged += (_, _) => ApplyBackdrop();
        // Popups don't follow their owner by themselves: keep toasts/palette glued to the window.
        LocationChanged += (_, _) => RepositionPopups();
        SizeChanged += (_, _) => RepositionPopups();
        StateChanged += (_, _) => RepositionPopups();
        // Popups are topmost windows: hide them whenever RDMan isn't the active app, or they'd float over other programs.
        _edgeTimer.Tick += EdgeTimer_Tick;
        Deactivated += (_, _) =>
        {
            ToastPopup.IsOpen = false;
            FullScreenBar.IsOpen = false;
            if (!PalettePopup.IsKeyboardFocusWithin) _vm.Palette.Close();
        };
        Activated += (_, _) => UpdateToastPopup();
        vm.Toasts.Items.CollectionChanged += (_, _) => UpdateToastPopup();
        ToastList.SizeChanged += (_, _) => RepositionPopups();
        // Give the keyboard a home at startup so shortcuts (Ctrl+K, Ctrl+T…) work before the first click.
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(() => FocusPane(_vm.ActivePane), System.Windows.Threading.DispatcherPriority.Input);
        SidebarColumn.Width = new GridLength(vm.State.Settings.SidebarWidth);
        UpdateMaximizeState();
        // Editor grid + window-level session overlay.
        LayoutHost.Rebuilt += (_, _) => UpdateMinimumSize();
        LayoutHost.Attach(vm);
        vm.CanSplitPane = CanSplitPane;
        vm.RequestFocusPane += (_, pane) => Dispatcher.BeginInvoke(() => FocusPane(pane), System.Windows.Threading.DispatcherPriority.Input);
        LayoutUpdated += (_, _) => PositionSessions();
        InitPaneDrag();
    }

    // ───────────── Editor grid ─────────────

    /// <summary>
    /// Places every tab's remote-desktop surface over its pane's content area. The surfaces stay in one ItemsControl
    /// for their whole life (never re-parented); only their Canvas rectangle and visibility change. Rectangles are
    /// clipped to the panes area and snapped to device pixels: a native window ignores WPF clipping and would paint
    /// over splitters or the status bar.
    /// </summary>
    private void PositionSessions()
    {
        if (!SessionOverlay.IsLoaded) return;
        var bounds = new Rect(0, 0, SessionOverlay.ActualWidth, SessionOverlay.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var tab in _vm.LiveTabs)
        {
            if (SessionOverlay.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement container) continue;
            var rect = Rect.Empty;
            if (tab.Session is not null && LayoutHost.ViewFor(tab.Pane) is { IsLoaded: true } view && view.ContentArea is { IsLoaded: true, ActualWidth: > 0 } area)
            {
                try
                {
                    rect = new Rect(area.TranslatePoint(new Point(0, 0), SessionOverlay), new Size(area.ActualWidth, area.ActualHeight));
                    rect.Intersect(bounds);
                }
                catch (InvalidOperationException) { rect = Rect.Empty; } // not in the same tree (being rebuilt)
            }
            if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1)
            {
                if (container.Visibility != Visibility.Collapsed) container.Visibility = Visibility.Collapsed;
                continue;
            }
            double Snap(double v, double scale) => Math.Round(v * scale) / scale;
            var x = Snap(rect.X, dpi.DpiScaleX);
            var y = Snap(rect.Y, dpi.DpiScaleY);
            var w = Snap(rect.Right, dpi.DpiScaleX) - x;
            var h = Snap(rect.Bottom, dpi.DpiScaleY) - y;
            if (container.Visibility != Visibility.Visible) container.Visibility = Visibility.Visible;
            if (Canvas.GetLeft(container) != x) Canvas.SetLeft(container, x);
            if (Canvas.GetTop(container) != y) Canvas.SetTop(container, y);
            if (container.Width != w) container.Width = w;
            if (container.Height != h) container.Height = h;
        }
    }

    /// <summary>A split must leave both halves at least the minimum pane size.</summary>
    private bool CanSplitPane(PaneViewModel pane, Core.Layout.DropSide side)
    {
        if (LayoutHost.ViewFor(pane) is not { ActualWidth: > 0 } view) return true;
        return side is Core.Layout.DropSide.Left or Core.Layout.DropSide.Right
            ? (view.ActualWidth - Views.PaneLayoutHost.SplitterSize) / 2 >= MainViewModel.MinPaneWidth
            : (view.ActualHeight - Views.PaneLayoutHost.SplitterSize) / 2 >= MainViewModel.MinPaneHeight;
    }

    /// <summary>The window can't shrink below what the grid needs (panes keep their minimum size).</summary>
    private void UpdateMinimumSize()
    {
        var grid = LayoutHost.MinimumSize;
        var sidebar = _restore is not null ? 0 : SidebarColumn.Width.IsAbsolute ? SidebarColumn.Width.Value : SidebarColumn.ActualWidth;
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(area.Width, Math.Max(760, sidebar + 6 + grid.Width + 2));
        MinHeight = Math.Min(area.Height, Math.Max(460, 40 + 28 + grid.Height + 2));
    }

    /// <summary>Gives the keyboard to a pane's content (its file list, else the pane itself).</summary>
    private void FocusPane(PaneViewModel pane)
    {
        if (LayoutHost.ViewFor(pane) is not { } view) return;
        if (FindVisible<ListView>(view) is { } list) list.Focus();
        else if (pane.SelectedTab?.Session is { IsConnected: true } session) session.Focus();
        else if (!view.Focus()) Keyboard.Focus(this); // shortcuts keep working; the active pane stays as it is
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

    private void UpdateToastPopup()
    {
        // Popups are topmost windows: only show toasts while RDMan itself is the active app.
        ToastPopup.IsOpen = _vm.Toasts.Items.Count > 0 && IsActive && WindowState != WindowState.Minimized;
        RepositionPopups();
    }

    private void RepositionPopups()
    {
        if (!IsLoaded) return;
        ToastPopup.HorizontalOffset = Math.Max(0, Root.ActualWidth - ToastList.ActualWidth - 32);
        ToastPopup.VerticalOffset = Math.Max(0, Root.ActualHeight - ToastList.ActualHeight - 40);
        // Nudge the offsets so WPF recomputes the screen position after the window moved.
        foreach (var p in new[] { ToastPopup, PalettePopup })
        {
            if (!p.IsOpen) continue;
            var h = p.HorizontalOffset;
            p.HorizontalOffset = h + 0.01;
            p.HorizontalOffset = h;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FullScreenTab)) ApplyFullScreen();
    }

    // ───────────── Full screen (desktop sessions) ─────────────
    // RDMan itself covers the monitor and hides every piece of chrome around the session, so the remote resolution
    // follows the monitor through the normal resize path (the ActiveX's own full screen keeps the old resolution).
    private sealed record FullScreenRestore(WindowState State, Rect Bounds, GridLength Sidebar);

    private FullScreenRestore? _restore;
    private NativeMethods.RECT _monitorPx;
    private readonly System.Windows.Threading.DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private DateTime _barShownAt;
    private bool _barHint;

    private void ApplyFullScreen()
    {
        var tab = _vm.FullScreenTab;
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (tab is not null && _restore is null)
        {
            _restore = new FullScreenRestore(WindowState, WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds,
                SidebarColumn.Width);
            LayoutHost.Zoom(tab.Pane); // only the full-screen pane is shown
            TitleBar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            Root.RowDefinitions[0].Height = new GridLength(0);
            Root.RowDefinitions[2].Height = new GridLength(0);
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
            ContentSheet.BorderThickness = new Thickness(0);
            ContentSheet.CornerRadius = new CornerRadius(0);
            chrome.ResizeBorderThickness = new Thickness(0);
            chrome.CaptionHeight = 0;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            Root.Margin = new Thickness(0);
            CoverMonitor();
            _edgeTimer.Start();
            // After the window took the monitor (and is active again), introduce the bar once.
            Dispatcher.BeginInvoke(() => ShowFullScreenBar(hint: true), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        else if (tab is null && _restore is { } r)
        {
            _restore = null;
            _edgeTimer.Stop();
            FullScreenBar.IsOpen = false;
            TitleBar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            Sidebar.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
            Root.RowDefinitions[0].Height = new GridLength(40);
            Root.RowDefinitions[2].Height = new GridLength(28);
            SidebarColumn.MinWidth = 200;
            SidebarColumn.Width = r.Sidebar;
            SidebarSplitterColumn.Width = new GridLength(6);
            ContentSheet.BorderThickness = new Thickness(1, 1, 0, 0);
            ContentSheet.CornerRadius = new CornerRadius(8, 0, 0, 0);
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.CaptionHeight = 40;
            Left = r.Bounds.Left;
            Top = r.Bounds.Top;
            Width = r.Bounds.Width;
            Height = r.Bounds.Height;
            if (r.State == WindowState.Maximized) WindowState = WindowState.Maximized;
            UpdateMaximizeState();
            LayoutHost.Zoom(null);
            UpdateMinimumSize();
        }
    }

    /// <summary>Places the window exactly over its monitor (Windows then keeps the taskbar behind it).</summary>
    private void CoverMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info)) return;
        _monitorPx = info.rcMonitor;
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var tl = toDip.Transform(new Point(info.rcMonitor.Left, info.rcMonitor.Top));
        var br = toDip.Transform(new Point(info.rcMonitor.Right, info.rcMonitor.Bottom));
        Left = tl.X;
        Top = tl.Y;
        Width = br.X - tl.X;
        Height = br.Y - tl.Y;
    }

    private void ShowFullScreenBar(bool hint)
    {
        if (_restore is null || !IsActive) return;
        _barHint = hint;
        FullScreenBarHint.Visibility = hint ? Visibility.Visible : Visibility.Collapsed;
        _barShownAt = DateTime.UtcNow;
        if (!FullScreenBar.IsOpen) FullScreenBar.IsOpen = true;
        FullScreenBarCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var tl = toDip.Transform(new Point(_monitorPx.Left, _monitorPx.Top));
        var br = toDip.Transform(new Point(_monitorPx.Right, _monitorPx.Bottom));
        FullScreenBar.HorizontalOffset = tl.X + (br.X - tl.X - FullScreenBarCard.DesiredSize.Width) / 2;
        FullScreenBar.VerticalOffset = tl.Y;
    }

    private void EdgeTimer_Tick(object? sender, EventArgs e)
    {
        if (_restore is null) return;
        NativeMethods.GetCursorPos(out var pt);
        var onMonitor = pt.X >= _monitorPx.Left && pt.X < _monitorPx.Right && pt.Y >= _monitorPx.Top && pt.Y < _monitorPx.Bottom;
        if (onMonitor && pt.Y <= _monitorPx.Top + 1)
        {
            ShowFullScreenBar(hint: false);
            return;
        }
        if (!FullScreenBar.IsOpen || FullScreenBarCard.IsMouseOver) return;
        var linger = _barHint ? 2500 : 600;
        if ((DateTime.UtcNow - _barShownAt).TotalMilliseconds > linger) FullScreenBar.IsOpen = false;
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
        // Closing ends every embedded desktop session: ask once (not when Windows itself is signing out).
        if (!App.IsWindowsSessionEnding && !_vm.SessionsEndConfirmed)
        {
            if (!_vm.ConfirmEndSessions(_vm.AllTabs))
            {
                e.Cancel = true;
                return;
            }
            _vm.SessionsEndConfirmed = true;
        }
        _vm.ExitFullScreenCommand.Execute(null); // save the normal placement, not the monitor rectangle
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
        UpdateMinimumSize();
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
        menu.Items.Add(Item("Menu.ImportRdg", "", _vm.ImportRdgCommand));
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
                _vm.Palette.Accept(newTab: (Keyboard.Modifiers & ModifierKeys.Control) != 0,
                                   alternate: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
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
