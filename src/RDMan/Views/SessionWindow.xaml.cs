using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using RDMan.Infrastructure;
using RDMan.ViewModels;

namespace RDMan.Views;

/// <summary>
/// A remote desktop in its own top-level window (another monitor, side by side with RDMan). The session's host is
/// created for this window and never moves: docking back closes it and reconnects in a tab.
/// </summary>
public partial class SessionWindow : Window
{
    private readonly MainViewModel _main;
    private bool _closingConfirmed;
    private bool _fullScreen;
    private (WindowState State, Rect Bounds)? _beforeFullScreen;
    private WindowState _lastShownState = WindowState.Normal;

    public SessionWindow(MainViewModel main, RdpSessionViewModel session, Size? contentSize, Window? near)
    {
        _main = main;
        Session = session;
        InitializeComponent();
        DataContext = session;
        UpdateTitle();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        session.PropertyChanged += OnSessionChanged;
        session.FocusReleased += OnFocusReleased;
        session.FullScreenRequested += OnFullScreenRequested;
        SourceInitialized += (_, _) => WindowEffects.Apply(this, ThemeManager.Instance.IsDark);
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastShownState = WindowState;
            UpdateFullScreenBar();
        };
        // The way-out bar floats only over this window while it's the one in use.
        Activated += (_, _) => UpdateFullScreenBar();
        Deactivated += (_, _) => UpdateFullScreenBar();
        Place(contentSize ?? new Size(1280, 760), near);
    }

    public RdpSessionViewModel Session { get; }

    /// <summary>Its close confirmation is open (or it's closing): it can't be shown or closed again meanwhile.</summary>
    public bool IsClosing { get; private set; }

    private void UpdateTitle() => Title = Loc.F("Undock.Title", Session.Server.Name);

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateTitle();

    /// <summary>Same content size the tab had, offset from RDMan's window and kept on that monitor's work area.</summary>
    private void Place(Size content, Window? near)
    {
        const double chrome = 47; // toolbar + its line
        // A narrow pane isn't why you undock: start at least at a comfortable desktop size (the work area caps it).
        Width = Math.Max(1024, content.Width + 16);
        Height = Math.Max(680, content.Height + chrome + 39);
        var anchor = near is { WindowState: WindowState.Normal } ? new Rect(near.Left, near.Top, near.ActualWidth, near.ActualHeight)
                   : near is not null ? near.RestoreBounds : Rect.Empty;
        var work = SystemParameters.WorkArea;
        if (!anchor.IsEmpty)
        {
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(anchor.Left + anchor.Width / 2), (int)(anchor.Top + anchor.Height / 2)));
            var dpi = near is not null ? System.Windows.Media.VisualTreeHelper.GetDpi(near).DpiScaleX : 1.0;
            work = new Rect(screen.WorkingArea.Left / dpi, screen.WorkingArea.Top / dpi, screen.WorkingArea.Width / dpi, screen.WorkingArea.Height / dpi);
        }
        Width = Math.Min(Width, work.Width);
        Height = Math.Min(Height, work.Height);
        var left = anchor.IsEmpty ? work.Left + (work.Width - Width) / 2 : anchor.Left + 48;
        var top = anchor.IsEmpty ? work.Top + (work.Height - Height) / 2 : anchor.Top + 48;
        Left = Math.Max(work.Left, Math.Min(left, work.Right - Width));
        Top = Math.Max(work.Top, Math.Min(top, work.Bottom - Height));
    }

    public void BringToFront()
    {
        if (IsClosing) return;
        if (WindowState == WindowState.Minimized) WindowState = _lastShownState;
        Show();
        Activate();
        Session.Focus();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RdpSessionViewModel.State)) return;
        if (Session.State is DesktopState.Connected or DesktopState.Idle or DesktopState.Error) _main.NotifyConnectionsChanged();
        if (_fullScreen && !Session.IsConnected) SetFullScreen(false); // show what happened
    }

    private void OnFocusReleased(object? sender, EventArgs e)
    {
        if (_fullScreen) SetFullScreen(false);
        FocusTarget.Focus();
    }

    private void OnFullScreenRequested(object? sender, bool on) => SetFullScreen(on);

    private void ExitFullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(false);

    /// <summary>Borderless over the whole monitor; the session follows the size like in a tab.</summary>
    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        Session.SyncFullScreen(on); // so Ctrl+Alt+Pause toggles back out
        if (on)
        {
            _beforeFullScreen = (WindowState, WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds);
            Toolbar.Visibility = Visibility.Collapsed;
            ToolbarLine.Visibility = Visibility.Collapsed;
            var hwnd = new WindowInteropHelper(this).Handle;
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Left = screen.Left / dpi.DpiScaleX;
            Top = screen.Top / dpi.DpiScaleY;
            Width = screen.Width / dpi.DpiScaleX;
            Height = screen.Height / dpi.DpiScaleY;
            FullScreenBar.Placement = PlacementMode.Absolute;
            Dispatcher.BeginInvoke(() =>
            {
                UpdateFullScreenBar();
                Session.Focus();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            FullScreenBar.IsOpen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Toolbar.Visibility = Visibility.Visible;
            ToolbarLine.Visibility = Visibility.Visible;
            if (_beforeFullScreen is { } before)
            {
                Left = before.Bounds.Left;
                Top = before.Bounds.Top;
                Width = before.Bounds.Width;
                Height = before.Bounds.Height;
                WindowState = before.State == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            }
        }
    }

    /// <summary>The exit bar shows only in full screen, while this window is active and not minimized.</summary>
    private void UpdateFullScreenBar()
    {
        var show = _fullScreen && IsActive && WindowState != WindowState.Minimized;
        if (show && FullScreenBar.Child is FrameworkElement bar)
        {
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            FullScreenBar.HorizontalOffset = Left + (Width - bar.DesiredSize.Width) / 2;
            FullScreenBar.VerticalOffset = Top;
        }
        if (FullScreenBar.IsOpen != show) FullScreenBar.IsOpen = show;
    }

    private void Dock_Click(object sender, RoutedEventArgs e) => _main.DockDesktop(this);

    /// <summary>Closes without asking (docking back, or the app exiting after its own confirmation).</summary>
    public bool CloseQuietly(bool force = false)
    {
        if (IsClosing && !force) return false;
        _closingConfirmed = true;
        Close();
        return force || !IsLoaded || !IsVisible;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        IsClosing = true;
        if (!_closingConfirmed && !App.IsWindowsSessionEnding && Session.IsConnected
            && !Dialogs.Confirm(this, Loc.T("Desktop.Disconnect"), Loc.F("Desktop.ConfirmLeave", Session.Server.Name), Loc.T("Desktop.Disconnect"), danger: false))
        {
            e.Cancel = true;
            IsClosing = false;
            return;
        }
        base.OnClosing(e);
        if (e.Cancel) IsClosing = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        FullScreenBar.IsOpen = false;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Session.PropertyChanged -= OnSessionChanged;
        Session.FocusReleased -= OnFocusReleased;
        Session.FullScreenRequested -= OnFullScreenRequested;
        Session.Dispose();
        base.OnClosed(e);
    }
}
