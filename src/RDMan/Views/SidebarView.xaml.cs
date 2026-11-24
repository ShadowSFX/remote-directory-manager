using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RDMan.Infrastructure;
using RDMan.ViewModels;

namespace RDMan.Views;

public partial class SidebarView : UserControl
{
    private const string NodeFormat = "RDMan.TreeNode";
    private Point _dragStart;
    private NodeViewModel? _dragCandidate;
    private bool _dragging;
    private NodeViewModel? _dropHighlight;

    public SidebarView() => InitializeComponent();

    private MainViewModel Vm => (MainViewModel)DataContext;

    private static NodeViewModel? NodeFrom(object source)
        => Ui.FindAncestor<TreeViewItem>(source as DependencyObject)?.DataContext as NodeViewModel;

    private static bool IsOnExpander(object source)
        => Ui.FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source as DependencyObject) is not null;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        Vm.TreeSelection = e.NewValue as NodeViewModel;
    }

    /// <summary>A node selected from code (new or imported node, the active tab's server) scrolls into view.</summary>
    private void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not TreeViewItem item) return;
        Dispatcher.BeginInvoke(() => item.BringIntoView(), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ───────────── Click to open ─────────────
    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragCandidate = IsOnExpander(e.OriginalSource) ? null : NodeFrom(e.OriginalSource);
        _dragging = false;
    }

    private void Tree_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || IsOnExpander(e.OriginalSource)) return;
        var node = NodeFrom(e.OriginalSource);
        if (node is null || !ReferenceEquals(node, _dragCandidate)) return;
        var target = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? OpenTarget.NewTab : OpenTarget.Current;
        Open(node, target);
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click on a group toggles it (TreeView default); on a server it would re-open — swallow it.
        if (NodeFrom(e.OriginalSource) is ServerNodeViewModel) e.Handled = true;
    }

    private void Tree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var node = NodeFrom(e.OriginalSource);
        if (node is null) return;
        Open(node, OpenTarget.NewTab);
        e.Handled = true;
    }

    private void Open(NodeViewModel node, OpenTarget target)
    {
        switch (node)
        {
            case ServerNodeViewModel s: Vm.OpenServer(s, target); break;
            case GroupNodeViewModel g: Vm.OpenGroup(g, target); break;
        }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        var node = Tree.SelectedItem as NodeViewModel;
        if (node is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Enter:
                Open(node, (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? OpenTarget.NewTab : OpenTarget.Current);
                e.Handled = true;
                break;
            case Key.F2:
                Vm.EditNode(node);
                e.Handled = true;
                break;
            case Key.Delete:
                Vm.DeleteCommand.Execute(node);
                e.Handled = true;
                break;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.Alt:
                Vm.MoveUpCommand.Execute(node);
                e.Handled = true;
                break;
            case Key.Down when Keyboard.Modifiers == ModifierKeys.Alt:
                Vm.MoveDownCommand.Execute(node);
                e.Handled = true;
                break;
        }
    }

    // ───────────── Context menu ─────────────
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = Ui.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var node = NodeFrom(e.OriginalSource) ?? (e.OriginalSource == Tree ? null : Tree.SelectedItem as NodeViewModel);
        var menu = Tree.ContextMenu!;
        menu.Items.Clear();
        MenuItem Item(string key, string glyph, ICommand cmd, object? param, string? gesture = null)
            => new() { Header = Loc.T(key), Tag = glyph, Command = cmd, CommandParameter = param, InputGestureText = gesture ?? "" };

        if (node is null)
        {
            menu.Items.Add(Item("Menu.NewServer", "\uE710", Vm.NewServerCommand, null, "Ctrl+N"));
            menu.Items.Add(Item("Menu.NewGroup", "\uE8F4", Vm.NewGroupCommand, null));
            return;
        }

        if (node is ServerNodeViewModel sv)
        {
            // Both views when available; the one a click opens comes first and in bold.
            var files = Item("Tree.Ctx.OpenFiles", "\uE8B7", Vm.OpenNodeFilesCommand, node);
            var desktop = Item("Tree.Ctx.OpenDesktop", "\uE7F4", Vm.OpenNodeDesktopCommand, node, sv.HasFiles ? null : "Enter");
            if (sv.DefaultAction == Core.Models.ServerAction.Desktop)
            {
                desktop.InputGestureText = "Enter";
                desktop.FontWeight = FontWeights.SemiBold;
                menu.Items.Add(desktop);
                if (sv.HasFiles) menu.Items.Add(files);
            }
            else
            {
                files.InputGestureText = "Enter";
                files.FontWeight = FontWeights.SemiBold;
                menu.Items.Add(files);
                if (sv.HasDesktop) menu.Items.Add(desktop);
            }
        }
        else menu.Items.Add(Item("Tree.Ctx.Open", "\uE8A7", Vm.OpenNodeCommand, node, "Enter"));
        menu.Items.Add(Item("Tree.Ctx.OpenNewTab", "\uECCD", Vm.OpenNodeInNewTabCommand, node, "Ctrl+Clic"));
        menu.Items.Add(Item("Tree.Ctx.OpenOtherPane", "\uE89F", Vm.OpenNodeInOtherPaneCommand, node));
        if (node is ServerNodeViewModel)
        {
            menu.Items.Add(Item("Tree.Ctx.OpenExplorer", "\uEC50", Vm.OpenNodeInExplorerCommand, node));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Tree.Ctx.CopyPath", "\uE8C8", Vm.CopyNodePathCommand, node));
            menu.Items.Add(Item("Tree.Ctx.CopyHost", "\uE8C8", Vm.CopyNodeHostCommand, node));
            menu.Items.Add(Item("Tree.Ctx.Disconnect", "\uE8CD", Vm.DisconnectNodeCommand, node));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Tree.Ctx.Duplicate", "\uE8F4", Vm.DuplicateCommand, node));
            var server = (ServerNodeViewModel)node;
            menu.Items.Add(Item(server.IsFavorite ? "Favorite.Remove" : "Favorite.Add", server.IsFavorite ? "\uE735" : "\uE734", Vm.ToggleFavoriteCommand, node));
        }
        else
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Tree.Ctx.NewServerHere", "\uE710", Vm.NewServerCommand, node));
            menu.Items.Add(Item("Tree.Ctx.NewGroupHere", "\uE8F4", Vm.NewGroupCommand, node));
            menu.Items.Add(Item("Tree.Ctx.BulkAddHere", "", Vm.BulkAddCommand, node));
            if (node is GroupNodeViewModel group)
            {
                var desktops = group.AllServers().Count(x => x.HasDesktop);
                var live = Vm.ConnectedDesktops(group);
                if (desktops > 0 || live > 0) menu.Items.Add(new Separator());
                if (desktops > 0)
                    menu.Items.Add(new MenuItem { Header = Loc.F("Tree.Ctx.ConnectDesktops", desktops), Tag = "", Command = Vm.ConnectGroupDesktopsCommand, CommandParameter = group });
                if (live > 0)
                    menu.Items.Add(new MenuItem { Header = Loc.F("Tree.Ctx.DisconnectDesktops", live), Tag = "", Command = Vm.DisconnectGroupDesktopsCommand, CommandParameter = group });
            }
        }
        menu.Items.Add(Item("Tree.Ctx.MoveUp", "\uE74A", Vm.MoveUpCommand, node, "Alt+↑"));
        menu.Items.Add(Item("Tree.Ctx.MoveDown", "\uE74B", Vm.MoveDownCommand, node, "Alt+↓"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Tree.Ctx.Properties", "\uE70F", Vm.EditCommand, node, "F2"));
        var del = Item("Tree.Ctx.Delete", "\uE74D", Vm.DeleteCommand, node, "Supr");
        del.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
        menu.Items.Add(del);
    }

    // ───────────── Drag & drop to reorganize ─────────────
    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null || _dragging) return;
        var pos = e.GetPosition(Tree);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = true;
        var data = new DataObject(NodeFormat, _dragCandidate);
        try
        {
            // Dropped on the panes, a server/group opens there (or in a new pane at an edge); in the tree it reorganizes.
            PaneDragSession.Run(Tree, _dragCandidate, data);
        }
        finally
        {
            ClearDropHighlight();
            _dragCandidate = null;
        }
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(NodeFormat) is not NodeViewModel dragged)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var over = NodeFrom(e.OriginalSource);
        var targetGroup = over as GroupNodeViewModel ?? over?.Parent;
        var valid = !ReferenceEquals(over, dragged)
                    && !(dragged is GroupNodeViewModel dg && targetGroup is not null && (ReferenceEquals(targetGroup, dg) || targetGroup.Ancestors().Contains(dg)));
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        SetDropHighlight(valid ? (NodeViewModel?)targetGroup ?? over : null);
        AutoScroll(e);
    }

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearDropHighlight();
        if (e.Data.GetData(NodeFormat) is not NodeViewModel dragged) return;
        var over = NodeFrom(e.OriginalSource);
        if (ReferenceEquals(over, dragged)) return;
        switch (over)
        {
            case GroupNodeViewModel g:
                Vm.Move(dragged, g, -1);
                break;
            case ServerNodeViewModel s:
                // Dropped on a server: place right after it, in the same group.
                var siblings = s.Parent?.Children ?? Vm.RootNodes;
                Vm.Move(dragged, s.Parent, siblings.IndexOf(s) + 1);
                break;
            default:
                Vm.Move(dragged, null, -1);
                break;
        }
        Vm.SelectInTree(dragged);
    }

    private void Tree_DragLeave(object sender, DragEventArgs e) => ClearDropHighlight();

    private void SetDropHighlight(NodeViewModel? node)
    {
        if (ReferenceEquals(node, _dropHighlight)) return;
        ClearDropHighlight();
        _dropHighlight = node;
        if (node is not null) node.IsDropTarget = true;
    }

    private void ClearDropHighlight()
    {
        if (_dropHighlight is not null) _dropHighlight.IsDropTarget = false;
        _dropHighlight = null;
    }

    private void AutoScroll(DragEventArgs e)
    {
        var sv = Ui.FindDescendant<ScrollViewer>(Tree);
        if (sv is null) return;
        var y = e.GetPosition(Tree).Y;
        if (y < 24) sv.ScrollToVerticalOffset(sv.VerticalOffset - 8);
        else if (y > Tree.ActualHeight - 24) sv.ScrollToVerticalOffset(sv.VerticalOffset + 8);
    }

    // ───────────── Files dropped on "This PC" places ─────────────
    private void Place_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? FileDropEffects.Decide(e, ((FrameworkElement)sender).Tag as string ?? "") : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Place_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || ((FrameworkElement)sender).Tag is not string dest) return;
        var effect = FileDropEffects.Decide(e, dest);
        e.Effects = FileDropEffects.ReportedToSource(effect);
        if (effect == DragDropEffects.None) return;
        var tab = Vm.ActiveTab;
        if (tab is not null) await tab.TransferAsync(files, dest, effect == DragDropEffects.Move);
    }

    // ───────────── Smart groups ─────────────
    private void SmartGroup_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { DataContext: SmartGroupViewModel g, ContextMenu: { } menu }) return;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = Loc.T("Tree.Ctx.Open"), Tag = "\uE8A7", Command = Vm.OpenSmartGroupCommand, CommandParameter = g });
        menu.Items.Add(new MenuItem { Header = Loc.T("Tree.Ctx.OpenNewTab"), Tag = "\uE8A7", Command = Vm.OpenSmartGroupInNewTabCommand, CommandParameter = g });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Loc.T("Smart.Edit"), Tag = "\uE70F", Command = Vm.EditSmartGroupCommand, CommandParameter = g });
        var del = new MenuItem { Header = Loc.T("Common.Delete"), Tag = "\uE74D", Command = Vm.DeleteSmartGroupCommand, CommandParameter = g };
        del.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
        menu.Items.Add(del);
    }

    /// <summary>Middle click opens the smart group in a new tab, like tree items.</summary>
    private void SmartGroup_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not Button { DataContext: SmartGroupViewModel g }) return;
        Vm.OpenSmartGroupInNewTabCommand.Execute(g);
        e.Handled = true;
    }
}

public static class FileDropEffects
{
    public static DragDropEffects Decide(DragEventArgs e, string destination)
    {
        var allowed = e.AllowedEffects;
        DragDropEffects wanted;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) wanted = DragDropEffects.Copy;
        else if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) wanted = DragDropEffects.Move;
        else
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var sameVolume = files is { Length: > 0 } && string.Equals(VolumeOf(files[0]), VolumeOf(destination), StringComparison.OrdinalIgnoreCase);
            wanted = sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
        }
        if ((allowed & wanted) != 0) return wanted;
        if ((allowed & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
        if ((allowed & DragDropEffects.Move) != 0) return DragDropEffects.Move;
        return DragDropEffects.None;
    }

    /// <summary>
    /// What Drop reports back to the OLE drag source. RDMan performs moves itself through the copy engine
    /// (an "optimized move"), so the source must not delete anything afterwards: report None for moves.
    /// </summary>
    public static DragDropEffects ReportedToSource(DragDropEffects performed)
        => performed == DragDropEffects.Move ? DragDropEffects.None : performed;

    /// <summary>"C:\" for local paths, "\\host\share" for UNC paths.</summary>
    public static string VolumeOf(string path)
    {
        if (Core.Smb.UncPath.TrySplit(path, out var t) && t is not null) return t.ShareRoot ?? t.ServerRoot;
        return System.IO.Path.GetPathRoot(path) ?? path;
    }
}
