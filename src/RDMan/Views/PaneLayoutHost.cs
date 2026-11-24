using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RDMan.Core.Layout;
using RDMan.ViewModels;

namespace RDMan.Views;

/// <summary>
/// Renders the editor grid: nested Grids with splitters built from <see cref="MainViewModel.Layout"/>. One
/// <see cref="PaneView"/> per pane is created once and re-attached on every rebuild (scroll position and column widths
/// survive). Pane views hold no remote-desktop surfaces — those live in the window-level session overlay — so
/// rebuilding never re-parents a live session.
/// </summary>
public sealed class PaneLayoutHost : Grid
{
    public const double SplitterSize = 5;

    private readonly Dictionary<PaneViewModel, PaneView> _views = new();
    private readonly List<(Grid Grid, LayoutSplit Split)> _built = new();
    private MainViewModel? _main;
    private PaneViewModel? _zoom;

    /// <summary>Raised after the grid was rebuilt (the window updates its minimum size).</summary>
    public event EventHandler? Rebuilt;

    public void Attach(MainViewModel main)
    {
        _main = main;
        // Splitter sizes must be read while the built grids still match the tree, i.e. before it changes.
        main.LayoutChanging += (_, _) => CaptureWeights();
        main.LayoutChanged += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>The view showing <paramref name="pane"/>, or null when it isn't on screen (another pane is zoomed).</summary>
    public PaneView? ViewFor(PaneViewModel pane)
        => (_zoom is null || ReferenceEquals(_zoom, pane)) && _views.TryGetValue(pane, out var v) ? v : null;

    /// <summary>Panes currently on screen with their views.</summary>
    public IEnumerable<(PaneViewModel Pane, PaneView View)> VisibleViews
        => _views.Where(kv => _zoom is null || ReferenceEquals(_zoom, kv.Key)).Select(kv => (kv.Key, kv.Value));

    /// <summary>Shows only <paramref name="pane"/> (full screen); null restores the grid.</summary>
    public void Zoom(PaneViewModel? pane)
    {
        if (ReferenceEquals(_zoom, pane)) return;
        CaptureWeights();
        _zoom = pane;
        Rebuild();
    }

    /// <summary>Smallest size the grid can take without squeezing a pane under its minimum.</summary>
    public Size MinimumSize
    {
        get
        {
            if (_main is null) return new Size(MainViewModel.MinPaneWidth, MainViewModel.MinPaneHeight);
            if (_zoom is not null) return new Size(MainViewModel.MinPaneWidth, MainViewModel.MinPaneHeight);
            var (w, h) = PaneLayout.MinSize(_main.Layout.Root, MainViewModel.MinPaneWidth, MainViewModel.MinPaneHeight, SplitterSize);
            return new Size(w, h);
        }
    }

    private void Rebuild()
    {
        if (_main is null) return;
        foreach (var v in _views.Values) Detach(v);
        Children.Clear();
        _built.Clear();
        foreach (var gone in _views.Keys.Where(p => !_main.Panes.Contains(p)).ToList()) _views.Remove(gone);

        if (_zoom is not null && !_main.Panes.Contains(_zoom)) _zoom = null;
        Children.Add(_zoom is not null ? ViewOf(_zoom) : Build(_main.Layout.Root));
        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    private PaneView ViewOf(PaneViewModel pane)
    {
        if (!_views.TryGetValue(pane, out var view))
        {
            view = new PaneView { DataContext = pane };
            _views[pane] = view;
        }
        return view;
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel panel) panel.Children.Remove(element);
    }

    private UIElement Build(LayoutNode node)
    {
        if (node is LayoutLeaf leaf)
            return _main!.PaneById(leaf.PaneId) is { } pane ? ViewOf(pane) : new Border();

        var split = (LayoutSplit)node;
        var columns = split.Direction == SplitDirection.Horizontal;
        var grid = new Grid();
        _built.Add((grid, split));
        for (var i = 0; i < split.Children.Count; i++)
        {
            var child = split.Children[i];
            var (minW, minH) = PaneLayout.MinSize(child, MainViewModel.MinPaneWidth, MainViewModel.MinPaneHeight, SplitterSize);
            if (i > 0)
            {
                if (columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitterSize) });
                else grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitterSize) });
                var splitter = new GridSplitter
                {
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                    ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Focusable = false,
                };
                splitter.SetResourceReference(StyleProperty, columns ? "Splitter.PaneColumns" : "Splitter.PaneRows");
                splitter.DragCompleted += Splitter_DragCompleted;
                Place(splitter, columns, grid);
            }
            if (columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(child.Weight, GridUnitType.Star), MinWidth = minW });
            else grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(child.Weight, GridUnitType.Star), MinHeight = minH });
            var element = Build(child);
            Place(element, columns, grid);
        }
        return grid;
    }

    private static void Place(UIElement element, bool columns, Grid grid)
    {
        if (columns) SetColumn(element, grid.ColumnDefinitions.Count - 1);
        else SetRow(element, grid.RowDefinitions.Count - 1);
        grid.Children.Add(element);
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        CaptureWeights();
        _main?.State.MarkSettingsDirty();
    }

    /// <summary>Reads the sizes the user set with the splitters back into the layout (as star weights).</summary>
    private void CaptureWeights()
    {
        foreach (var (grid, split) in _built)
        {
            var columns = split.Direction == SplitDirection.Horizontal;
            var slots = columns ? grid.ColumnDefinitions.Count : grid.RowDefinitions.Count;
            if (slots != split.Children.Count * 2 - 1 || !grid.IsLoaded) continue; // stale or never laid out
            for (var i = 0; i < split.Children.Count; i++)
            {
                var index = i * 2; // children sit at even positions, splitters in between
                var size = columns
                    ? (index < grid.ColumnDefinitions.Count ? grid.ColumnDefinitions[index].ActualWidth : 0)
                    : (index < grid.RowDefinitions.Count ? grid.RowDefinitions[index].ActualHeight : 0);
                if (size > 0) split.Children[i].Weight = size;
            }
        }
    }
}
