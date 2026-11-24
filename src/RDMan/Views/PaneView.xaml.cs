using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using RDMan.Infrastructure;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class PaneView : UserControl
{
    public PaneView()
    {
        InitializeComponent();
        // The view is re-attached whenever the grid is rebuilt: subscribe while on screen only.
        Loaded += (_, _) =>
        {
            if (Main is { } main)
            {
                main.PropertyChanged -= OnMainPropertyChanged;
                main.PropertyChanged += OnMainPropertyChanged;
            }
            ApplyFullScreen();
            UpdateActiveMarker();
        };
        Unloaded += (_, _) =>
        {
            if (Main is { } main) main.PropertyChanged -= OnMainPropertyChanged;
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PaneViewModel old) old.PropertyChanged -= OnPanePropertyChanged;
            if (e.NewValue is PaneViewModel pane)
            {
                pane.PropertyChanged += OnPanePropertyChanged;
                UpdateActiveMarker();
            }
        };
    }

    private PaneViewModel? Pane => DataContext as PaneViewModel;

    private static MainViewModel? Main => Application.Current.MainWindow?.DataContext as MainViewModel;

    /// <summary>The active tab's content area: remote-desktop surfaces are placed over it by the window's session overlay.</summary>
    public FrameworkElement ContentArea => TabView.ContentArea;

    /// <summary>Height of the tab strip row (drop zone for reordering / moving tabs).</summary>
    public const double StripHeight = 40;

    /// <summary>The tab strip (drop target for reordering / moving tabs).</summary>
    public ListBox Strip => TabStrip;

    /// <summary>Full screen: the tab area covers the tab strip.</summary>
    private void ApplyFullScreen()
    {
        var full = Main?.FullScreenTab is { } t && ReferenceEquals(t.Pane, Pane);
        Grid.SetRow(TabArea, full ? 0 : 1);
        Grid.SetRowSpan(TabArea, full ? 2 : 1);
        Panel.SetZIndex(TabArea, full ? 10 : 0);
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FullScreenTab)) ApplyFullScreen();
    }

    private void OnPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.IsActive) or nameof(PaneViewModel.CanClose)) UpdateActiveMarker();
        if (e.PropertyName == nameof(PaneViewModel.SelectedTab) && Pane?.SelectedTab is { } tab)
            TabStrip.ScrollIntoView(tab);
    }

    /// <summary>In split view, a thin accent line marks which pane receives keyboard commands.</summary>
    private void UpdateActiveMarker()
    {
        var show = Pane is { IsActive: true } && Main?.HasMultiplePanes == true;
        ActiveMarker.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(120)));
    }

    private void Pane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Pane is not null) Main?.Activate(Pane);
        UpdateActiveMarker();
    }

    private void Pane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Pane is not null) Main?.Activate(Pane);
    }

    // ───────────── Dragging a tab (reorder, move to another pane, split) ─────────────
    private BrowserTabViewModel? _dragTab;
    private Point _dragStart;

    private void TabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragTab = null;
        var source = e.OriginalSource as DependencyObject;
        if (Ui.FindAncestor<Button>(source) is not null) return; // the close button
        if (Ui.FindAncestor<ListBoxItem>(source)?.DataContext is not BrowserTabViewModel tab) return;
        _dragTab = tab; // the tab under the pointer, not SelectedItem (selection may still change)
        _dragStart = e.GetPosition(TabStrip);
    }

    private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is not { } tab || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(TabStrip);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragTab = null;
        TabStrip.ReleaseMouseCapture(); // the ListBox captured the mouse for drag-selection
        PaneDragSession.Run(TabStrip, tab, new DataObject(PaneDragSession.TabFormat, tab.Title));
    }

    private void TabStrip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragTab = null;

    private void TabStrip_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is BrowserTabViewModel tab)
        {
            Pane?.CloseTab(tab);
            e.Handled = true;
        }
    }

    private void TabStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = Ui.FindDescendant<ScrollViewer>(TabStrip);
        if (sv is null) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void TabMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        var tab = (menu.PlacementTarget as FrameworkElement)?.InputHitTest(Mouse.GetPosition(menu.PlacementTarget as IInputElement)) is DependencyObject hit
            ? Ui.FindAncestor<ListBoxItem>(hit)?.DataContext as BrowserTabViewModel
            : null;
        tab ??= Pane?.SelectedTab;
        if (Pane is null || tab is null) return;
        MenuItem Item(string key, string glyph, ICommand cmd, string? gesture = null)
            => new() { Header = Loc.T(key), Tag = glyph, Command = cmd, CommandParameter = tab, InputGestureText = gesture ?? "" };
        menu.Items.Add(Item("Nav.NewTab", "", Pane.NewTabCommand, "Ctrl+T"));
        menu.Items.Add(new MenuItem { Header = Loc.T("Tree.Ctx.Duplicate"), Tag = "", Command = Pane.DuplicateTabCommand, CommandParameter = tab });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Nav.CloseTab", "", Pane.CloseTabCommand, "Ctrl+W"));
        menu.Items.Add(new MenuItem { Header = Loc.T("Nav.CloseOthers"), Tag = "", Command = Pane.CloseOtherTabsCommand, CommandParameter = tab, IsEnabled = Pane.Tabs.Count > 1 });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Loc.T("Pane.SplitRight"), Tag = "", Command = Pane.SplitRightCommand, CommandParameter = tab, InputGestureText = "F3" });
        menu.Items.Add(new MenuItem { Header = Loc.T("Pane.SplitDown"), Tag = "", Command = Pane.SplitDownCommand, CommandParameter = tab });
        if (Pane.CanClose) menu.Items.Add(new MenuItem { Header = Loc.T("Pane.Close"), Tag = "", Command = Pane.ClosePaneCommand });
    }

    // Hovering a dragged file over a tab switches to it, like Explorer/browsers.
    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is BrowserTabViewModel tab && Pane is not null && !ReferenceEquals(Pane.SelectedTab, tab))
            Pane.SelectedTab = tab;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Tab_Drop(object sender, DragEventArgs e) => e.Handled = true;
}
