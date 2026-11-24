using System.Text.Json;
using RDMan.Core.Layout;
using RDMan.Core.Persistence;

namespace RDMan.Core.Tests;

public sealed class PaneLayoutTests
{
    private static string Shape(LayoutNode n) => n switch
    {
        LayoutLeaf l => l.PaneId,
        LayoutSplit s => (s.Direction == SplitDirection.Horizontal ? "h(" : "v(") + string.Join(",", s.Children.Select(Shape)) + ")",
        _ => "?",
    };

    private static void AssertInvariants(LayoutNode n, LayoutSplit? parent = null)
    {
        Assert.Same(parent, n.Parent);
        Assert.True(n.Weight > 0);
        if (n is not LayoutSplit s) return;
        Assert.True(s.Children.Count >= 2, "split with fewer than two children");
        foreach (var c in s.Children)
        {
            if (c is LayoutSplit inner) Assert.NotEqual(s.Direction, inner.Direction);
            AssertInvariants(c, s);
        }
    }

    [Theory]
    [InlineData(DropSide.Right, "h(a,b)")]
    [InlineData(DropSide.Left, "h(b,a)")]
    [InlineData(DropSide.Bottom, "v(a,b)")]
    [InlineData(DropSide.Top, "v(b,a)")]
    public void Split_on_each_side(DropSide side, string expected)
    {
        var l = new PaneLayout("a");
        l.Split("a", "b", side);
        Assert.Equal(expected, Shape(l.Root));
        AssertInvariants(l.Root);
    }

    [Fact]
    public void Same_direction_split_becomes_a_sibling_sharing_the_target_space()
    {
        var l = new PaneLayout("a");
        l.Split("a", "b", DropSide.Right);
        l.Split("a", "c", DropSide.Right);
        Assert.Equal("h(a,c,b)", Shape(l.Root));
        var s = (LayoutSplit)l.Root;
        Assert.Equal(0.5, s.Children[0].Weight);
        Assert.Equal(0.5, s.Children[1].Weight);
        Assert.Equal(1, s.Children[2].Weight);
        AssertInvariants(l.Root);
    }

    [Fact]
    public void Opposite_direction_split_nests_and_reading_order_follows()
    {
        var l = new PaneLayout("a");
        l.Split("a", "b", DropSide.Right);
        l.Split("b", "c", DropSide.Bottom);
        l.Split("a", "d", DropSide.Top);
        Assert.Equal("h(v(d,a),v(b,c))", Shape(l.Root));
        Assert.Equal(["d", "a", "b", "c"], l.Leaves());
        AssertInvariants(l.Root);
    }

    [Fact]
    public void Remove_collapses_and_merges_same_direction()
    {
        var l = new PaneLayout("a");
        l.Split("a", "b", DropSide.Right);   // h(a,b)
        l.Split("b", "c", DropSide.Bottom);  // h(a,v(b,c))
        l.Split("c", "d", DropSide.Right);   // h(a,v(b,h(c,d)))
        Assert.Equal("h(a,v(b,h(c,d)))", Shape(l.Root));
        Assert.True(l.Remove("b"));          // v collapses -> h(c,d) merges into root h
        Assert.Equal("h(a,c,d)", Shape(l.Root));
        AssertInvariants(l.Root);
        Assert.True(l.Remove("c"));
        Assert.True(l.Remove("d"));
        Assert.Equal("a", Shape(l.Root));
        Assert.False(l.Remove("a"));          // never removes the last pane
        Assert.False(l.Remove("zzz"));
    }

    [Fact]
    public void Remove_gives_the_space_to_the_neighbour()
    {
        var l = new PaneLayout("a");
        l.Split("a", "b", DropSide.Right);
        l.Split("b", "c", DropSide.Right);   // h(a:1, b:.5, c:.5)
        l.Remove("c");
        var s = (LayoutSplit)l.Root;
        Assert.Equal(1, s.Children[0].Weight);
        Assert.Equal(1, s.Children[1].Weight);
    }

    [Fact]
    public void Split_rejects_bad_requests()
    {
        var l = new PaneLayout("a");
        Assert.Throws<ArgumentException>(() => l.Split("a", "b", DropSide.Center));
        Assert.Throws<InvalidOperationException>(() => l.Split("x", "b", DropSide.Right));
        Assert.Throws<InvalidOperationException>(() => l.Split("a", "a", DropSide.Right));
    }

    [Fact]
    public void MinSize_sums_along_and_maxes_across()
    {
        var l = new PaneLayout("a");
        l.Split("a", "b", DropSide.Right);
        l.Split("b", "c", DropSide.Bottom);
        var (w, h) = PaneLayout.MinSize(l.Root, 280, 180, 4);
        Assert.Equal(280 * 2 + 4, w);
        Assert.Equal(180 * 2 + 4, h);
    }

    [Theory]
    [InlineData(500, 300, DropSide.Center)]
    [InlineData(20, 300, DropSide.Left)]
    [InlineData(980, 300, DropSide.Right)]
    [InlineData(500, 10, DropSide.Top)]
    [InlineData(500, 590, DropSide.Bottom)]
    [InlineData(60, 10, DropSide.Top)]      // corner: the nearer edge (relative to the size) wins
    [InlineData(30, 20, DropSide.Left)]
    public void Hit_picks_the_nearest_edge_or_center(double x, double y, DropSide expected)
        => Assert.Equal(expected, PaneLayout.Hit(1000, 600, x, y, 280, 180));

    [Fact]
    public void Hit_refuses_splits_that_would_be_too_small_and_points_outside()
    {
        // 500 wide: halves of 250 < 280, so no left/right; 600 high allows top/bottom.
        Assert.Equal(DropSide.Center, PaneLayout.Hit(500, 600, 10, 300, 280, 180));
        Assert.Equal(DropSide.Top, PaneLayout.Hit(500, 600, 250, 10, 280, 180));
        Assert.Null(PaneLayout.Hit(500, 600, -1, 10, 280, 180));
        Assert.Null(PaneLayout.Hit(0, 600, 0, 0, 280, 180));
    }

    [Fact]
    public void Saved_round_trip_keeps_shape_and_weights()
    {
        var l = new PaneLayout("a");
        l.Split("a", "b", DropSide.Right);
        l.Split("b", "c", DropSide.Bottom);
        var order = l.Leaves();
        var json = JsonSerializer.Serialize(l.ToSaved(order), JsonFileStore.Options);
        var back = PaneLayout.FromSaved(JsonSerializer.Deserialize<SavedLayoutNode>(json, JsonFileStore.Options), order.Count)!;
        Assert.Equal("h(0,v(1,2))", Shape(back.Root));
        AssertInvariants(back.Root);
    }

    [Fact]
    public void FromSaved_tolerates_garbage()
    {
        var saved = new SavedLayoutNode
        {
            Dir = "zz", Weight = double.NaN,
            Children =
            [
                new SavedLayoutNode { Pane = 0, Weight = -3 },
                new SavedLayoutNode { Pane = 0 },                       // duplicate
                new SavedLayoutNode { Pane = 9 },                       // missing pane
                new SavedLayoutNode { Dir = "v", Children = [] },       // empty split
                new SavedLayoutNode { Dir = "h", Children = [new SavedLayoutNode { Pane = 1 }, new SavedLayoutNode { Pane = 2 }] }, // same direction
                null!,
            ],
        };
        var l = PaneLayout.FromSaved(saved, 3)!;
        Assert.Equal("h(0,1,2)", Shape(l.Root));
        AssertInvariants(l.Root);
        Assert.Null(PaneLayout.FromSaved(new SavedLayoutNode { Pane = 5 }, 3));
        Assert.Null(PaneLayout.FromSaved(saved, 0));
    }

    [Fact]
    public void FromSaved_limits_depth_and_pane_count()
    {
        var deep = new SavedLayoutNode { Pane = 0 };
        for (var i = 0; i < 50; i++) deep = new SavedLayoutNode { Dir = i % 2 == 0 ? "h" : "v", Children = [deep, new SavedLayoutNode { Pane = i + 1 }] };
        var l = PaneLayout.FromSaved(deep, 60)!;
        Assert.True(l.Count <= PaneLayout.MaxPanes);
        AssertInvariants(l.Root);
    }

    [Theory]
    [InlineData(false, 2, "0")]
    [InlineData(true, 2, "h(0,1)")]
    [InlineData(true, 1, "0")]
    public void Migrates_1x_two_pane_settings(bool splitView, int panes, string expected)
    {
        var s = new AppSettings { SplitView = splitView };
        for (var i = 0; i < panes; i++) s.Panes.Add(new SavedPane { Tabs = { new SavedTab() } });
        Assert.Equal(expected, Shape(PaneLayout.FromSettings(s)!.Root));
        Assert.Null(PaneLayout.FromSettings(new AppSettings()));
    }

    [Fact]
    public void Settings_keep_up_to_eight_panes_and_clamp_the_active_one()
    {
        var s = new AppSettings { ActivePane = 40 };
        for (var i = 0; i < 12; i++) s.Panes.Add(new SavedPane());
        s.Normalize();
        Assert.Equal(PaneLayout.MaxPanes, s.Panes.Count);
        Assert.Equal(PaneLayout.MaxPanes - 1, s.ActivePane);
    }
}
