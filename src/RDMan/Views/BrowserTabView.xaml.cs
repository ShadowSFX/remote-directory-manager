using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RDMan.Infrastructure;
using RDMan.Interop;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class BrowserTabView : UserControl
{
    private Point _dragStart;
    private bool _dragArmed;
    private bool _isDragging;
    private FileItemViewModel? _dropHighlight;
    private FileItemViewModel? _pendingClickSelect;

    public BrowserTabView()
    {
        InitializeComponent();
        List.SizeChanged += (_, _) => AdjustColumns();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        ContentArea.SizeChanged += (_, e) => ApplyContentHeight(e.NewSize.Height);
        CrumbHost.SizeChanged += (_, _) => KeepCurrentCrumbVisible();
        Crumbs.SizeChanged += (_, _) => KeepCurrentCrumbVisible();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is BrowserTabViewModel old)
            {
                old.RequestRename -= OnRequestRename;
                old.RequestSelect -= OnRequestSelect;
                old.RequestFocusList -= OnRequestFocusList;
                old.SessionFocusReleased -= OnSessionFocusReleased;
                old.PropertyChanged -= OnTabPropertyChanged;
            }
            if (e.NewValue is BrowserTabViewModel tab)
            {
                tab.RequestRename += OnRequestRename;
                tab.RequestSelect += OnRequestSelect;
                tab.RequestFocusList += OnRequestFocusList;
                tab.SessionFocusReleased += OnSessionFocusReleased;
                tab.PropertyChanged += OnTabPropertyChanged;
            }
            ApplyFullScreen();
            WatchTerminal();
        };
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTabViewModel.IsFullScreen)) ApplyFullScreen();
        if (e.PropertyName is nameof(BrowserTabViewModel.TerminalSession) or nameof(BrowserTabViewModel.Session) or nameof(BrowserTabViewModel.IsTerminal))
            WatchTerminal();
    }

    /// <summary>Full screen: the content area (where the session sits) spans the identity band and toolbar too.</summary>
    private void ApplyFullScreen()
    {
        var full = Tab?.IsFullScreen == true;
        Grid.SetRow(ContentArea, full ? 0 : 3);
        Grid.SetRowSpan(ContentArea, full ? 4 : 1);
        Panel.SetZIndex(ContentArea, full ? 10 : 0);
    }

    private BrowserTabViewModel? Tab => DataContext as BrowserTabViewModel;

    // ───────────── Narrow panes ─────────────
    // Panes in the editor grid can be narrow: secondary things step aside so the server name, the switch and the
    // navigation stay usable instead of being squeezed until unreadable.
    private const double MediumWidth = 620;
    private const double NarrowWidth = 480;

    private void ApplyWidth(double width)
    {
        var medium = width < MediumWidth;
        var narrow = width < NarrowWidth;
        FilesLabel.Visibility = DesktopLabel.Visibility = medium ? Visibility.Collapsed : Visibility.Visible;
        FilesIcon.Margin = DesktopIcon.Margin = medium ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        IdentityUser.Visibility = medium ? Visibility.Collapsed : Visibility.Visible; // also in the status bar
        // These two have Visibility bindings of their own (connected / files view): squeeze them with MaxWidth instead.
        DesktopHint.MaxWidth = medium ? 0 : double.PositiveInfinity;
        IdentityExplorer.MaxWidth = narrow ? 0 : double.PositiveInfinity;
        LatencyText.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        FilterBox.Width = narrow ? 120 : 200;
    }

    /// <summary>Short panes: the "not connected" card drops its illustration, then its explanation, never the Connect button.</summary>
    private void ApplyContentHeight(double height)
    {
        IdleIcon.Visibility = height < 300 ? Visibility.Collapsed : Visibility.Visible;
        IdleBody.Visibility = height < 190 ? Visibility.Collapsed : Visibility.Visible;
        IdlePanel.Margin = height < 300 ? new Thickness(16) : new Thickness(32);
    }

    /// <summary>Like Explorer: when the path doesn't fit, show its end (the current folder), clipping the start.</summary>
    private void KeepCurrentCrumbVisible()
    {
        Crumbs.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var overflow = Crumbs.DesiredSize.Width > CrumbHost.ActualWidth;
        var align = overflow ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        if (Crumbs.HorizontalAlignment != align) Crumbs.HorizontalAlignment = align;
    }
    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    /// <summary>
    /// Name fills the available width; on narrow panes (split view) Type, then Modified, step aside
    /// instead of forcing a horizontal scrollbar.
    /// </summary>
    private void AdjustColumns()
    {
        if (List.View is not GridView gv || gv.Columns.Count < 4 || List.ActualWidth <= 0) return;
        var available = List.ActualWidth - 34; // row margins + vertical scrollbar
        double modified = 150, type = 160, size = 100;
        if (available < 640) type = 0;
        if (available < 470) modified = 0;
        gv.Columns[1].Width = modified;
        gv.Columns[2].Width = type;
        gv.Columns[3].Width = size;
        gv.Columns[0].Width = Math.Max(180, available - modified - type - size);
    }

    // ───────────── VM → view requests ─────────────
    private void OnRequestSelect(object? sender, FileItemViewModel item)
    {
        Dispatcher.BeginInvoke(() =>
        {
            List.SelectedItems.Clear();
            List.SelectedItem = item;
            List.ScrollIntoView(item);
            (List.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem)?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnRequestRename(object? sender, FileItemViewModel item)
    {
        Dispatcher.BeginInvoke(() => List.ScrollIntoView(item), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Ctrl+Alt+←/→ inside a session hands the keyboard back to RDMan: land on the desktop toolbar.</summary>
    private void OnSessionFocusReleased(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() =>
        {
            var target = DataContext is BrowserTabViewModel { IsTerminal: true } ? TerminalFocusTarget : DesktopFocusTarget;
            target.Focus();
            Keyboard.Focus(target);
        }, System.Windows.Threading.DispatcherPriority.Input);

    private void OnRequestFocusList(object? sender, EventArgs e) => Dispatcher.BeginInvoke(FocusList, System.Windows.Threading.DispatcherPriority.Input);

    private void FocusList()
    {
        if (List.SelectedItem is { } sel && List.ItemContainerGenerator.ContainerFromItem(sel) is ListViewItem c) c.Focus();
        else List.Focus();
    }

    // ───────────── Selection & opening ─────────────
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => Tab?.SetSelection(List.SelectedItems.Cast<FileItemViewModel>());

    private async void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Tab is null) return;
        if (Ui.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext is not FileItemViewModel item) return;
        e.Handled = true;
        await Tab.OpenItemAsync(item);
    }

    private void List_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click a folder → new tab. Click on empty space clears the selection like Explorer.
        var item = Ui.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as FileItemViewModel;
        if (e.ChangedButton == MouseButton.Middle && item is { IsContainer: true } && Tab is not null)
        {
            _ = Tab.OpenItemAsync(item, newTab: true);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left && item is null && Ui.FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is null)
        {
            List.SelectedItems.Clear();
            List.Focus();
        }
    }

    private async void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var tab = Tab;
        if (tab is null || Keyboard.FocusedElement is TextBox) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (tab.Selection.Count == 1 && ctrl && tab.Selection[0].IsContainer) await tab.OpenItemAsync(tab.Selection[0], newTab: true);
                else if (tab.Selection.Count == 1) await tab.OpenItemAsync(tab.Selection[0]);
                else await tab.OpenSelectedCommand.ExecuteAsync(null);
                break;
            case Key.Back:
                e.Handled = true;
                await tab.UpCommand.ExecuteAsync(null);
                break;
            case Key.C when ctrl && !shift:
                e.Handled = true;
                tab.CopyCommand.Execute(null);
                break;
            case Key.C when ctrl && shift:
                e.Handled = true;
                tab.CopyPathCommand.Execute(null);
                break;
            case Key.X when ctrl:
                e.Handled = true;
                tab.CutCommand.Execute(null);
                break;
            case Key.V when ctrl:
                e.Handled = true;
                await tab.Paste();
                break;
            case Key.A when ctrl:
                e.Handled = true;
                List.SelectAll();
                break;
            case Key.Delete:
                e.Handled = true;
                if (tab.CanWrite) await tab.DeleteCommand.ExecuteAsync(shift);
                break;
            case Key.System when e.SystemKey == Key.Enter:
                e.Handled = true;
                tab.PropertiesCommand.Execute(null);
                break;
            case Key.F2:
                e.Handled = true;
                if (tab.CanWrite) tab.RenameCommand.Execute(null);
                break;
            case Key.Apps:
            case Key.System when e.SystemKey == Key.F10 && shift:
                e.Handled = true;
                OpenContextMenuAtSelection();
                break;
            default:
                // Type-ahead: jump to the first item starting with the typed letters, like Explorer.
                TypeAhead(e);
                break;
        }
    }

    private string _typed = "";
    private DateTime _typedAt;

    private void TypeAhead(KeyEventArgs e)
    {
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        var ch = KeyToChar(e.Key);
        if (ch is null) return;
        if ((DateTime.Now - _typedAt).TotalMilliseconds > 900) _typed = "";
        _typedAt = DateTime.Now;
        _typed += ch;
        var match = List.Items.Cast<FileItemViewModel>().FirstOrDefault(i => i.Name.StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase));
        if (match is null) return;
        e.Handled = true;
        OnRequestSelect(this, match);
    }

    private static string? KeyToChar(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('a' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
        return key switch { Key.OemPeriod => ".", Key.OemMinus => "-", Key.Space => " ", _ => null };
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is GridViewColumnHeader { Tag: string column }) Tab?.SortByCommand.Execute(column);
    }

    // ───────────── Inline rename ─────────────
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox box || box.DataContext is not FileItemViewModel item) return;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.Select(0, Math.Min(item.RenameSelectionLength, box.Text.Length));
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItemViewModel item } || Tab is null) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await Tab.CommitRenameAsync(item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Tab.CancelRename(item);
        }
    }

    private async void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Explorer commits a rename when you click elsewhere.
        if (sender is TextBox { DataContext: FileItemViewModel { IsRenaming: true } item } && Tab is not null)
            await Tab.CommitRenameAsync(item);
    }

    // ───────────── Address bar ─────────────
    private void AddressBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking the empty part of the bar (not a crumb) switches to editing, like Explorer.
        if (Ui.FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        Tab?.EditPathCommand.Execute(null);
        e.Handled = true;
    }

    private void PathBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => { PathBox.Focus(); Keyboard.Focus(PathBox); PathBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void PathBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Tab is null) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await Tab.CommitPathCommand.ExecuteAsync(null);
            FocusList();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Tab.CancelPathCommand.Execute(null);
            FocusList();
        }
    }

    private void PathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Tab?.CancelPathCommand.Execute(null);

    private void FilterBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => { FilterBox.Focus(); Keyboard.Focus(FilterBox); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Tab is null) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Tab.ToggleFilterCommand.Execute(null);
            FocusList();
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            e.Handled = true;
            if (List.Items.Count > 0 && List.SelectedIndex < 0) List.SelectedIndex = 0;
            FocusList();
        }
    }

    // ───────────── Drag out (to Explorer, other panes, other tabs) ─────────────
    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(List);
        var row = Ui.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        _dragArmed = row is not null && Ui.FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is null;
        _pendingClickSelect = null;
        // Keep a multi-selection when pressing on an already-selected row so it can be dragged as a whole.
        if (row is { IsSelected: true } && List.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _pendingClickSelect = row.DataContext as FileItemViewModel;
            e.Handled = true;
            row.Focus();
        }
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingClickSelect is not null && !_isDragging)
        {
            List.SelectedItems.Clear();
            List.SelectedItem = _pendingClickSelect;
        }
        _pendingClickSelect = null;
        _dragArmed = false;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _isDragging || Tab is null) return;
        var pos = e.GetPosition(List);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = Tab.Selection.Where(s => s.Kind is FileItemKind.File or FileItemKind.Folder).Select(s => s.FullPath).ToArray();
        if (paths.Length == 0) return;
        _dragArmed = false;
        _isDragging = true;
        _pendingClickSelect = null;
        try
        {
            var data = new DataObject();
            var list = new StringCollection();
            list.AddRange(paths);
            data.SetFileDropList(list);
            DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _isDragging = false;
            ClearDropHighlight();
        }
    }

    // ───────────── Drop in (from Explorer, other panes, desktop) ─────────────
    private string? DropDestination(DragEventArgs e, out FileItemViewModel? overFolder)
    {
        overFolder = Ui.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as FileItemViewModel;
        if (overFolder is { Kind: FileItemKind.Folder or FileItemKind.Share or FileItemKind.Drive or FileItemKind.KnownFolder }) return overFolder.FullPath;
        overFolder = null;
        return Tab is { CanWrite: true, Folder: { } f } ? f.Path : null;
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        FileItemViewModel? overFolder = null;
        var dest = e.Data.GetDataPresent(DataFormats.FileDrop) ? DropDestination(e, out overFolder) : null;
        if (dest is null)
        {
            e.Effects = DragDropEffects.None;
            ClearDropHighlight();
            return;
        }
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        // Dropping a folder into itself (or a file onto its own folder without moving) is meaningless.
        if (files.Any(f => dest.IsUnder(f)) || (overFolder is null && files.All(f => string.Equals(f.ParentFolder(), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) && (e.KeyStates & DragDropKeyStates.ControlKey) == 0))
        {
            e.Effects = DragDropEffects.None;
            ClearDropHighlight();
            return;
        }
        e.Effects = FileDropEffects.Decide(e, dest);
        SetDropHighlight(overFolder);
        AutoScroll(e);
    }

    private void List_DragLeave(object sender, DragEventArgs e) => ClearDropHighlight();

    private async void List_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearDropHighlight();
        if (Tab is null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var dest = DropDestination(e, out _);
        if (dest is null) return;
        var effect = FileDropEffects.Decide(e, dest);
        e.Effects = FileDropEffects.ReportedToSource(effect);
        if (effect == DragDropEffects.None) return;
        Window.GetWindow(this)?.Activate();
        await Tab.TransferAsync(files, dest, effect == DragDropEffects.Move);
    }

    private void Crumb_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var dest = ((FrameworkElement)sender).Tag is FolderLocation { IsServerRoot: false } f ? f.Path : null;
        e.Effects = dest is not null && e.Data.GetDataPresent(DataFormats.FileDrop) ? FileDropEffects.Decide(e, dest) : DragDropEffects.None;
    }

    private async void Crumb_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Tab is null || ((FrameworkElement)sender).Tag is not FolderLocation { IsServerRoot: false } f) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var effect = FileDropEffects.Decide(e, f.Path);
        e.Effects = FileDropEffects.ReportedToSource(effect);
        if (effect != DragDropEffects.None) await Tab.TransferAsync(files, f.Path, effect == DragDropEffects.Move);
    }

    private void SetDropHighlight(FileItemViewModel? item)
    {
        if (ReferenceEquals(item, _dropHighlight)) return;
        ClearDropHighlight();
        _dropHighlight = item;
        if (item is not null) item.IsDropTarget = true;
    }

    private void ClearDropHighlight()
    {
        if (_dropHighlight is not null) _dropHighlight.IsDropTarget = false;
        _dropHighlight = null;
    }

    private void AutoScroll(DragEventArgs e)
    {
        var sv = Ui.FindDescendant<ScrollViewer>(List);
        if (sv is null) return;
        var y = e.GetPosition(List).Y;
        if (y < 40) sv.ScrollToVerticalOffset(sv.VerticalOffset - 12);
        else if (y > List.ActualHeight - 24) sv.ScrollToVerticalOffset(sv.VerticalOffset + 12);
    }

    // ───────────── Context menu ─────────────
    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = Ui.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (row is null)
        {
            List.SelectedItems.Clear();
            return;
        }
        if (!row.IsSelected)
        {
            List.SelectedItems.Clear();
            row.IsSelected = true;
        }
        row.Focus();
    }

    private void OpenContextMenuAtSelection()
    {
        var menu = List.ContextMenu!;
        BuildContextMenu(menu);
        if (List.SelectedItem is { } sel && List.ItemContainerGenerator.ContainerFromItem(sel) is ListViewItem row)
        {
            menu.PlacementTarget = row;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
        else
        {
            menu.PlacementTarget = List;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        }
        menu.IsOpen = true;
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = List.ContextMenu!;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.PlacementTarget = List;
        BuildContextMenu(menu);
        if (menu.Items.Count == 0) e.Handled = true;
    }

    private void BuildContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var tab = Tab;
        if (tab is null || tab.Mode != TabMode.Folder) return;
        var sel = tab.Selection;
        var real = sel.Where(s => s.Kind is FileItemKind.File or FileItemKind.Folder).ToList();
        MenuItem Item(string key, string glyph, Action action, string? gesture = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = Loc.T(key), Tag = glyph, InputGestureText = gesture ?? "", IsEnabled = enabled };
            mi.Click += (_, _) => action();
            return mi;
        }

        if (sel.Count > 0)
        {
            var open = Item("File.Open", "", () => _ = tab.OpenSelectedCommand.ExecuteAsync(null), "Enter");
            open.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(open);
            if (sel.Any(s => s.IsContainer))
            {
                menu.Items.Add(Item("File.OpenNewTab", "", () => tab.OpenSelectedInNewTabCommand.Execute(null), "Ctrl+Enter"));
                menu.Items.Add(Item("File.OpenOtherPane", "", () => tab.OpenSelectedInOtherPaneCommand.Execute(null)));
            }
            menu.Items.Add(new Separator());
            if (real.Count > 0)
            {
                menu.Items.Add(Item("File.Cut", "", () => tab.CutCommand.Execute(null), "Ctrl+X"));
                menu.Items.Add(Item("File.Copy", "", () => tab.CopyCommand.Execute(null), "Ctrl+C"));
            }
            if (sel.Count == 1 && sel[0].IsContainer && sel[0].Kind != FileItemKind.Drive && App.Services.Clipboard.HasFiles())
                menu.Items.Add(Item("File.Paste", "", async () => { var dest = sel[0].FullPath; var files = App.Services.Clipboard.Get(); if (files is not null) { var done = await tab.TransferAsync(files.Paths, dest, files.Intent == Services.ClipboardIntent.Move); if (files.Intent == Services.ClipboardIntent.Move && done) App.Services.Clipboard.ClearAfterMove(); } }, "Ctrl+V"));
            if (real.Count > 0 && tab.CanUseOtherPane)
            {
                // With several panes, name the target (the pane used before this one) so it's never a guess.
                var copy = Item("File.CopyToOther", "", () => _ = tab.CopyToOtherPaneCommand.ExecuteAsync(null));
                var move = Item("File.MoveToOther", "", () => _ = tab.MoveToOtherPaneCommand.ExecuteAsync(null));
                if (tab.OtherPaneTitle.Length > 0)
                {
                    copy.Header = Loc.F("File.CopyToPane", tab.OtherPaneTitle);
                    move.Header = Loc.F("File.MoveToPane", tab.OtherPaneTitle);
                }
                menu.Items.Add(copy);
                menu.Items.Add(move);
            }
            menu.Items.Add(Item("File.CopyPath", "", () => tab.CopyPathCommand.Execute(null), "Ctrl+Shift+C"));
            if (real.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("File.Rename", "", () => tab.RenameCommand.Execute(null), "F2", enabled: real.Count == 1));
                var del = Item("File.Delete", "", () => _ = tab.DeleteCommand.ExecuteAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0), "Supr");
                del.Foreground = (Brush)FindResource("Danger");
                menu.Items.Add(del);
            }
            menu.Items.Add(new Separator());
            if (real.Count > 0)
                menu.Items.Add(Item("File.MoreOptions", "", () => ShowShellMenu(real.Select(r => r.FullPath).ToList()), "Shift+F10"));
            menu.Items.Add(Item("File.Properties", "", () => tab.PropertiesCommand.Execute(null), "Alt+Enter", enabled: sel.Count == 1));
        }
        else
        {
            menu.Items.Add(Item("File.Paste", "", () => _ = tab.Paste(), "Ctrl+V", enabled: tab.CanWrite && App.Services.Clipboard.HasFiles()));
            menu.Items.Add(Item("File.NewFolder", "", () => tab.NewFolderCommand.Execute(null), "Ctrl+Shift+N", enabled: tab.CanWrite));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Nav.Refresh", "", () => tab.RefreshCommand.Execute(null), "F5"));
            menu.Items.Add(Item("File.SelectAll", "", () => List.SelectAll(), "Ctrl+A"));
            menu.Items.Add(Item("File.CopyPath", "", () => tab.CopyPathCommand.Execute(null)));
            if (tab.Folder is { } f)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Nav.OpenExplorer", "", () => tab.OpenInExplorerCommand.Execute(null)));
                if (!f.IsServerRoot) menu.Items.Add(Item("File.MoreOptions", "", () => ShowShellMenu([f.Path])));
            }
        }
    }

    private void ShowShellMenu(IReadOnlyList<string> paths)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        NativeMethods.GetCursorPos(out var pt);
        App.Services.ShellMenu.Show(window, paths, new Point(pt.X, pt.Y), extended: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    }

    // ───────────── Identity band & dashboard ─────────────
    private void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (Tab?.Server is { } s) Main?.EditNode(s);
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServerNodeViewModel s })
            Main?.OpenServer(s, (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? OpenTarget.NewTab : OpenTarget.Current);
    }

    private void CardFiles_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't also trigger the card's default action
        if (sender is FrameworkElement { Tag: ServerNodeViewModel s })
            Main?.OpenFiles(s, (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? OpenTarget.NewTab : OpenTarget.Current);
    }

    private void CardDesktop_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ServerNodeViewModel s })
            Main?.OpenDesktop(s, (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? OpenTarget.NewTab : OpenTarget.Current);
    }

    private void CardTerminal_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ServerNodeViewModel s })
            Main?.OpenTerminal(s, (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? OpenTarget.NewTab : OpenTarget.Current);
    }

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { Tag: ServerNodeViewModel s })
        {
            Main?.OpenServer(s, OpenTarget.NewTab);
            e.Handled = true;
        }
    }

    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { Tag: ServerNodeViewModel s, ContextMenu: { } menu } || Main is not { } main) return;
        menu.Items.Clear();
        MenuItem Item(string key, string glyph, ICommand cmd) => new() { Header = Loc.T(key), Tag = glyph, Command = cmd, CommandParameter = s };
        menu.Items.Add(Item("Tree.Ctx.Open", "", main.OpenNodeCommand));
        menu.Items.Add(Item("Tree.Ctx.OpenNewTab", "", main.OpenNodeInNewTabCommand));
        menu.Items.Add(Item("Tree.Ctx.OpenOtherPane", "", main.OpenNodeInOtherPaneCommand));
        menu.Items.Add(Item("Tree.Ctx.OpenExplorer", "", main.OpenNodeInExplorerCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Tree.Ctx.CopyPath", "", main.CopyNodePathCommand));
        menu.Items.Add(new MenuItem { Header = Loc.T(s.IsFavorite ? "Favorite.Remove" : "Favorite.Add"), Tag = s.IsFavorite ? "\uE735" : "\uE734",
            Command = main.ToggleFavoriteCommand, CommandParameter = s });
        menu.Items.Add(Item("Tree.Ctx.Properties", "", main.EditCommand));
    }

    /// <summary>Connected view: jump to the live desktop when there is one, otherwise open the server as usual.</summary>
    private void SessionCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerNodeViewModel s } || Main is not { } main) return;
        if (s.Thumbnail is not null && s.HasDesktop) main.OpenDesktop(s, OpenTarget.Current);
        else main.OpenServer(s, OpenTarget.Current);
    }

    private void CardStar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServerNodeViewModel s }) Main?.ToggleFavorite(s);
        e.Handled = true;
    }

    private void DashboardNewServer_Click(object sender, RoutedEventArgs e)
        => Main?.NewServerCommand.Execute(Tab?.Dashboard?.Group);

    private void DashboardEditGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Tab?.Dashboard?.Group is { } g) Main?.EditNode(g);
        else if (Tab?.Dashboard?.Smart is { } sg) Main?.EditSmartGroup(sg);
    }

    private void DashboardImport_Click(object sender, RoutedEventArgs e) => Main?.ImportFromWindowsCommand.Execute(null);
}
