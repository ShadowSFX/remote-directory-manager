using System.Windows;
using System.Windows.Controls;
using RDMan.Core.Layout;
using RDMan.ViewModels;
using RDMan.Views;

namespace RDMan;

/// <summary>
/// VS Code-style drop zones for the editor grid. While a tab or a tree node is dragged, <c>DropPopup</c> covers the
/// panes and resolves, purely from geometry, where the drop lands: a tab strip (insert there), the middle of a pane
/// (move / open there) or one of its edges (new pane on that side).
/// </summary>
public partial class MainWindow
{
    private sealed record DropTarget(PaneViewModel Pane, DropSide Side, int? StripIndex, Rect Zone, double MarkerX, Rect Strip);

    private void InitPaneDrag()
    {
        PaneDragSession.Started += (_, _) => OpenDropOverlay();
        PaneDragSession.Ended += (_, _) => CloseDropOverlay();
    }

    private void OpenDropOverlay()
    {
        // Full screen shows a single session: there is no grid to drop on.
        if (!IsLoaded || _restore is not null || PanesArea.ActualWidth <= 0) return;
        DropCanvas.Width = PanesArea.ActualWidth;
        DropCanvas.Height = PanesArea.ActualHeight;
        HideDropVisuals();
        DropPopup.HorizontalOffset = 0;
        DropPopup.VerticalOffset = 0;
        DropPopup.IsOpen = true;
    }

    private void CloseDropOverlay()
    {
        DropPopup.IsOpen = false;
        HideDropVisuals();
    }

    private void HideDropVisuals()
    {
        DropZone.Visibility = Visibility.Collapsed;
        InsertMarker.Visibility = Visibility.Collapsed;
        DragChip.Visibility = Visibility.Collapsed;
    }

    private void Drop_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var point = e.GetPosition(PanesArea);
        var target = Resolve(point);
        ShowDropVisuals(target, point);
        e.Effects = target is null ? DragDropEffects.None : DragDropEffects.Move;
    }

    private void Drop_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideDropVisuals();
    }

    private void Drop_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var payload = PaneDragSession.Payload;
        var target = Resolve(e.GetPosition(PanesArea));
        HideDropVisuals();
        if (payload is null || target is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        // Runs after the drag loop ends (see PaneDragSession).
        PaneDragSession.Pending = () => ExecuteDrop(payload, target);
    }

    private void ExecuteDrop(object payload, DropTarget target)
    {
        if (!_vm.Panes.Contains(target.Pane)) return; // the grid changed meanwhile
        switch (payload)
        {
            case BrowserTabViewModel tab when tab.Pane.Tabs.Contains(tab):
                if (target.StripIndex is { } index) _vm.MoveTab(tab, target.Pane, index);
                else if (target.Side == DropSide.Center) _vm.MoveTab(tab, target.Pane, target.Pane.Tabs.Count);
                else _vm.MoveTabToNewPane(tab, target.Pane, target.Side);
                break;
            case NodeViewModel node:
                _vm.OpenNodeAt(node, target.Pane, target.StripIndex is null ? target.Side : DropSide.Center, target.StripIndex);
                break;
        }
    }

    /// <summary>Where a drop at <paramref name="p"/> (PanesArea coordinates) would land, or null for "nothing happens".</summary>
    private DropTarget? Resolve(Point p)
    {
        var payload = PaneDragSession.Payload;
        if (payload is not (BrowserTabViewModel or ServerNodeViewModel or GroupNodeViewModel)) return null;
        var tab = payload as BrowserTabViewModel;
        foreach (var (pane, view) in LayoutHost.VisibleViews)
        {
            if (!view.IsLoaded || view.ActualWidth <= 0) continue;
            Rect bounds;
            try { bounds = new Rect(view.TranslatePoint(new Point(0, 0), PanesArea), view.RenderSize); }
            catch (InvalidOperationException) { continue; }
            if (!bounds.Contains(p)) continue;

            var strip = new Rect(bounds.X, bounds.Y, bounds.Width, Math.Min(PaneView.StripHeight, bounds.Height));
            if (strip.Contains(p))
            {
                var (index, markerX) = InsertionPoint(view, pane, p, strip);
                return new DropTarget(pane, DropSide.Center, index, Rect.Empty, markerX, strip);
            }

            var content = new Rect(bounds.X, strip.Bottom, bounds.Width, Math.Max(0, bounds.Bottom - strip.Bottom));
            // A pane's only tab can't open a new pane next to itself (nothing would stay behind), and the grid has a cap.
            var canSplit = _vm.Panes.Count < PaneLayout.MaxPanes && !(tab is not null && ReferenceEquals(tab.Pane, pane) && pane.Tabs.Count == 1);
            var minW = canSplit ? MainViewModel.MinPaneWidth + PaneLayoutHost.SplitterSize / 2 : double.MaxValue;
            var minH = canSplit ? MainViewModel.MinPaneHeight + PaneLayoutHost.SplitterSize / 2 : double.MaxValue;
            if (PaneLayout.Hit(content.Width, content.Height, p.X - content.X, p.Y - content.Y, minW, minH) is not { } side) return null;
            // Dropping a tab in the middle of its own pane changes nothing.
            if (side == DropSide.Center && tab is not null && ReferenceEquals(tab.Pane, pane)) return null;
            var zone = side switch
            {
                DropSide.Left => new Rect(content.X, content.Y, content.Width / 2, content.Height),
                DropSide.Right => new Rect(content.X + content.Width / 2, content.Y, content.Width / 2, content.Height),
                DropSide.Top => new Rect(content.X, content.Y, content.Width, content.Height / 2),
                DropSide.Bottom => new Rect(content.X, content.Y + content.Height / 2, content.Width, content.Height / 2),
                _ => content,
            };
            return new DropTarget(pane, side, null, zone, 0, strip);
        }
        return null;
    }

    /// <summary>Insertion index in a tab strip (before the first tab whose middle is right of the pointer) and the marker position.</summary>
    private (int Index, double MarkerX) InsertionPoint(PaneView view, PaneViewModel pane, Point p, Rect strip)
    {
        var markerX = strip.X + 8;
        for (var i = 0; i < pane.Tabs.Count; i++)
        {
            if (view.Strip.ItemContainerGenerator.ContainerFromItem(pane.Tabs[i]) is not FrameworkElement item || !item.IsVisible) continue;
            Rect r;
            try { r = new Rect(item.TranslatePoint(new Point(0, 0), PanesArea), item.RenderSize); }
            catch (InvalidOperationException) { continue; }
            if (p.X < r.X + r.Width / 2) return (i, r.X);
            markerX = r.Right;
        }
        return (pane.Tabs.Count, markerX);
    }

    private void ShowDropVisuals(DropTarget? target, Point p)
    {
        // The dragged thing's name follows the pointer.
        DragChipText.Text = PaneDragSession.Payload switch
        {
            BrowserTabViewModel t => t.Title,
            NodeViewModel n => n.Name,
            _ => "",
        };
        DragChip.Visibility = DragChipText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DragChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(DragChip, Math.Clamp(p.X + 14, 0, Math.Max(0, DropCanvas.Width - DragChip.DesiredSize.Width)));
        Canvas.SetTop(DragChip, Math.Clamp(p.Y + 18, 0, Math.Max(0, DropCanvas.Height - DragChip.DesiredSize.Height)));

        if (target is null)
        {
            DropZone.Visibility = Visibility.Collapsed;
            InsertMarker.Visibility = Visibility.Collapsed;
            return;
        }
        if (target.StripIndex is not null)
        {
            DropZone.Visibility = Visibility.Collapsed;
            InsertMarker.Visibility = Visibility.Visible;
            InsertMarker.Height = Math.Max(0, target.Strip.Height - 12);
            Canvas.SetLeft(InsertMarker, target.MarkerX - InsertMarker.Width / 2);
            Canvas.SetTop(InsertMarker, target.Strip.Y + 6);
            return;
        }
        InsertMarker.Visibility = Visibility.Collapsed;
        DropZone.Visibility = Visibility.Visible;
        // Inset so the highlight reads as "this area" and never touches the splitters.
        var zone = target.Zone;
        zone.Inflate(-4, -4);
        Canvas.SetLeft(DropZone, zone.X);
        Canvas.SetTop(DropZone, zone.Y);
        DropZone.Width = Math.Max(0, zone.Width);
        DropZone.Height = Math.Max(0, zone.Height);
    }
}
