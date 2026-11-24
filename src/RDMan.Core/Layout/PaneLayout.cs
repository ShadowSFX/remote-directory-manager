using RDMan.Core.Persistence;

namespace RDMan.Core.Layout;

/// <summary>Horizontal = children side by side (columns); Vertical = stacked (rows).</summary>
public enum SplitDirection { Horizontal, Vertical }

/// <summary>Where a dragged tab lands on a pane: the middle moves it there, an edge opens a new pane on that side.</summary>
public enum DropSide { Center, Left, Right, Top, Bottom }

public abstract class LayoutNode
{
    /// <summary>Star weight relative to its siblings.</summary>
    public double Weight { get; set; } = 1;
    public LayoutSplit? Parent { get; internal set; }
}

public sealed class LayoutLeaf : LayoutNode
{
    public LayoutLeaf(string paneId) => PaneId = paneId;
    public string PaneId { get; }
}

public sealed class LayoutSplit : LayoutNode
{
    private readonly List<LayoutNode> _children = new();

    public LayoutSplit(SplitDirection direction) => Direction = direction;

    public SplitDirection Direction { get; }
    public IReadOnlyList<LayoutNode> Children => _children;

    internal void Insert(int index, LayoutNode child)
    {
        child.Parent = this;
        _children.Insert(index, child);
    }

    internal void Add(LayoutNode child) => Insert(_children.Count, child);

    internal void RemoveAt(int index)
    {
        _children[index].Parent = null;
        _children.RemoveAt(index);
    }

    internal int IndexOf(LayoutNode child) => _children.IndexOf(child);
}

/// <summary>
/// The editor grid (VS Code style): a tree of splits whose leaves are panes. Pure model — the WPF host renders it.
/// Invariants kept by every operation: no empty splits, no single-child splits, no split directly inside a split
/// of the same direction, every pane id appears once.
/// </summary>
public sealed class PaneLayout
{
    public const int MaxPanes = 8;
    private const int MaxDepth = 12;

    public PaneLayout(string firstPaneId) => Root = new LayoutLeaf(firstPaneId);

    private PaneLayout(LayoutNode root) => Root = root;

    public LayoutNode Root { get; private set; }

    public int Count => Leaves().Count;

    /// <summary>Pane ids in reading order (left→right, top→bottom).</summary>
    public IReadOnlyList<string> Leaves()
    {
        var list = new List<string>();
        Collect(Root, list);
        return list;

        static void Collect(LayoutNode n, List<string> acc)
        {
            if (n is LayoutLeaf l) acc.Add(l.PaneId);
            else foreach (var c in ((LayoutSplit)n).Children) Collect(c, acc);
        }
    }

    public LayoutLeaf? Find(string paneId) => Find(Root, paneId);

    private static LayoutLeaf? Find(LayoutNode n, string id)
    {
        if (n is LayoutLeaf l) return l.PaneId == id ? l : null;
        foreach (var c in ((LayoutSplit)n).Children)
            if (Find(c, id) is { } hit) return hit;
        return null;
    }

    /// <summary>Opens <paramref name="newPaneId"/> next to <paramref name="targetPaneId"/> on <paramref name="side"/>.</summary>
    public void Split(string targetPaneId, string newPaneId, DropSide side)
    {
        if (side == DropSide.Center) throw new ArgumentException("Center doesn't split.", nameof(side));
        if (Find(newPaneId) is not null) throw new InvalidOperationException($"Pane {newPaneId} is already in the layout.");
        var target = Find(targetPaneId) ?? throw new InvalidOperationException($"Pane {targetPaneId} isn't in the layout.");
        var direction = side is DropSide.Left or DropSide.Right ? SplitDirection.Horizontal : SplitDirection.Vertical;
        var after = side is DropSide.Right or DropSide.Bottom;
        var leaf = new LayoutLeaf(newPaneId);

        if (target.Parent is { } parent && parent.Direction == direction)
        {
            // Same direction: become a sibling and share the target's space (VS Code behaviour).
            var half = target.Weight / 2;
            target.Weight = half;
            leaf.Weight = half;
            parent.Insert(parent.IndexOf(target) + (after ? 1 : 0), leaf);
            return;
        }

        var split = new LayoutSplit(direction) { Weight = target.Weight };
        Replace(target, split);
        target.Weight = 1;
        leaf.Weight = 1;
        split.Add(after ? target : leaf);
        split.Add(after ? leaf : target);
    }

    /// <summary>Removes a pane; its space goes to the neighbour. The last pane can't be removed.</summary>
    public bool Remove(string paneId)
    {
        var leaf = Find(paneId);
        if (leaf?.Parent is not { } parent) return false;
        var index = parent.IndexOf(leaf);
        parent.RemoveAt(index);
        var heir = parent.Children[Math.Max(0, index - 1)];
        heir.Weight += leaf.Weight;
        if (parent.Children.Count == 1) Collapse(parent);
        return true;
    }

    /// <summary>A split left with one child is replaced by that child (merging into a same-direction grandparent).</summary>
    private void Collapse(LayoutSplit split)
    {
        var only = split.Children[0];
        split.RemoveAt(0);
        only.Weight = split.Weight;
        var grand = split.Parent;
        Replace(split, only);
        if (grand is not null && only is LayoutSplit inner && inner.Direction == grand.Direction) Merge(grand, inner);
    }

    /// <summary>Inlines <paramref name="inner"/>'s children into <paramref name="outer"/> (same direction), keeping proportions.</summary>
    private static void Merge(LayoutSplit outer, LayoutSplit inner)
    {
        var at = outer.IndexOf(inner);
        outer.RemoveAt(at);
        var total = inner.Children.Sum(c => c.Weight);
        var kids = inner.Children.ToList();
        for (var i = kids.Count - 1; i >= 0; i--) inner.RemoveAt(i);
        for (var i = 0; i < kids.Count; i++)
        {
            kids[i].Weight = total <= 0 ? inner.Weight / kids.Count : kids[i].Weight / total * inner.Weight;
            outer.Insert(at + i, kids[i]);
        }
    }

    private void Replace(LayoutNode old, LayoutNode replacement)
    {
        if (old.Parent is { } p)
        {
            var i = p.IndexOf(old);
            p.RemoveAt(i);
            p.Insert(i, replacement);
        }
        else
        {
            replacement.Parent = null;
            Root = replacement;
        }
    }

    // ───────────── Geometry ─────────────

    /// <summary>Smallest size the subtree can take with every pane at least <paramref name="minW"/>×<paramref name="minH"/>.</summary>
    public static (double Width, double Height) MinSize(LayoutNode node, double minW, double minH, double splitter)
    {
        if (node is LayoutLeaf) return (minW, minH);
        var split = (LayoutSplit)node;
        var sizes = split.Children.Select(c => MinSize(c, minW, minH, splitter)).ToList();
        var gaps = splitter * (sizes.Count - 1);
        return split.Direction == SplitDirection.Horizontal
            ? (sizes.Sum(s => s.Width) + gaps, sizes.Max(s => s.Height))
            : (sizes.Max(s => s.Width), sizes.Sum(s => s.Height) + gaps);
    }

    /// <summary>
    /// Which drop zone a point over a pane of <paramref name="width"/>×<paramref name="height"/> falls in: the nearest
    /// edge within <paramref name="edgeFraction"/> of the size, else the center. An edge is only offered when both
    /// halves would stay at least the minimum size. Null when the point is outside the pane.
    /// </summary>
    public static DropSide? Hit(double width, double height, double x, double y, double minW, double minH, double edgeFraction = 1.0 / 3)
    {
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x > width || y > height) return null;
        var canColumns = width / 2 >= minW;
        var canRows = height / 2 >= minH;
        var edges = new List<(DropSide Side, double Distance)>();
        if (canColumns)
        {
            edges.Add((DropSide.Left, x / width));
            edges.Add((DropSide.Right, (width - x) / width));
        }
        if (canRows)
        {
            edges.Add((DropSide.Top, y / height));
            edges.Add((DropSide.Bottom, (height - y) / height));
        }
        var nearest = edges.Where(e => e.Distance < edgeFraction).OrderBy(e => e.Distance).FirstOrDefault();
        return nearest == default ? DropSide.Center : nearest.Side;
    }

    // ───────────── Persistence ─────────────

    /// <summary>Saved form; leaves reference panes by their position in <paramref name="paneOrder"/>.</summary>
    public SavedLayoutNode ToSaved(IReadOnlyList<string> paneOrder) => Save(Root, paneOrder);

    private static SavedLayoutNode Save(LayoutNode n, IReadOnlyList<string> order) => n switch
    {
        LayoutLeaf l => new SavedLayoutNode { Pane = IndexOf(order, l.PaneId), Weight = l.Weight },
        LayoutSplit s => new SavedLayoutNode
        {
            Dir = s.Direction == SplitDirection.Horizontal ? "h" : "v",
            Weight = s.Weight,
            Children = s.Children.Select(c => Save(c, order)).ToList(),
        },
        _ => throw new InvalidOperationException(),
    };

    private static int IndexOf(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
            if (order[i] == id) return i;
        return -1;
    }

    /// <summary>
    /// Rebuilds a layout from settings, tolerating anything a hand-edited or older file can contain: leaves that point
    /// at missing or repeated panes, empty/single-child splits, bad weights, absurd depth. Leaf ids are the pane
    /// indexes as strings. Null when nothing usable remains.
    /// </summary>
    public static PaneLayout? FromSaved(SavedLayoutNode? saved, int paneCount)
    {
        if (saved is null || paneCount <= 0) return null;
        var used = new HashSet<int>();
        var root = Load(saved, paneCount, used, 0);
        if (root is null) return null;
        root.Parent = null;
        root.Weight = 1;
        return new PaneLayout(root);
    }

    private static LayoutNode? Load(SavedLayoutNode s, int paneCount, HashSet<int> used, int depth)
    {
        if (depth > MaxDepth || used.Count >= MaxPanes) return null;
        var weight = double.IsFinite(s.Weight) && s.Weight > 0 ? s.Weight : 1;
        if (s.Children is null || s.Children.Count == 0)
        {
            if (s.Pane is not { } p || p < 0 || p >= paneCount || !used.Add(p)) return null;
            return new LayoutLeaf(p.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Weight = weight };
        }
        var direction = s.Dir == "v" ? SplitDirection.Vertical : SplitDirection.Horizontal;
        var children = s.Children.Where(c => c is not null).Select(c => Load(c, paneCount, used, depth + 1)).Where(c => c is not null).ToList();
        if (children.Count == 0) return null;
        if (children.Count == 1)
        {
            children[0]!.Weight = weight;
            return children[0];
        }
        var split = new LayoutSplit(direction) { Weight = weight };
        foreach (var c in children)
        {
            if (c is LayoutSplit inner && inner.Direction == direction)
            {
                // Same-direction nesting: inline the grandchildren, keeping proportions.
                var total = inner.Children.Sum(k => k.Weight);
                foreach (var k in inner.Children.ToList())
                {
                    inner.RemoveAt(inner.IndexOf(k));
                    k.Weight = k.Weight / total * inner.Weight;
                    split.Add(k);
                }
            }
            else split.Add(c!);
        }
        return split;
    }

    /// <summary>
    /// The layout to restore from <paramref name="settings"/>, migrating 1.x files (two panes + SplitView flag).
    /// Leaves are pane indexes into <c>settings.Panes</c>.
    /// </summary>
    public static PaneLayout? FromSettings(AppSettings settings)
    {
        var count = settings.Panes.Count;
        if (count == 0) return null;
        if (settings.Layout is not null) return FromSaved(settings.Layout, count);
        // 1.x: [left, right] and a flag saying whether the right one was shown.
        if (settings.SplitView && count >= 2 && settings.Panes[1].Tabs.Count > 0)
            return FromSaved(new SavedLayoutNode
            {
                Dir = "h",
                Children = [new SavedLayoutNode { Pane = 0 }, new SavedLayoutNode { Pane = 1 }],
            }, count);
        return new PaneLayout("0");
    }
}
