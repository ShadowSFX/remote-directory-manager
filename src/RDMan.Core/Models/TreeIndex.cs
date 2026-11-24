namespace RDMan.Core.Models;

/// <summary>Parent lookup and traversal helpers for a <see cref="ConnectionDocument"/> tree (nodes do not store parents).</summary>
public sealed class TreeIndex
{
    private readonly Dictionary<Guid, GroupNode?> _parents = new();
    private readonly Dictionary<Guid, TreeNode> _nodes = new();

    public TreeIndex(GroupNode root)
    {
        Root = root;
        Walk(root, null);
    }

    public GroupNode Root { get; }

    private void Walk(TreeNode node, GroupNode? parent)
    {
        // Guard against duplicated ids (hand-edited files): first occurrence wins.
        if (!_nodes.TryAdd(node.Id, node)) return;
        _parents[node.Id] = parent;
        if (node is GroupNode g)
            foreach (var child in g.Children)
                Walk(child, g);
    }

    public TreeNode? Find(Guid id) => _nodes.GetValueOrDefault(id);

    public GroupNode? ParentOf(Guid id) => _parents.GetValueOrDefault(id);

    /// <summary>Node itself first, then each ancestor up to and including the root.</summary>
    public IEnumerable<TreeNode> SelfAndAncestors(TreeNode node)
    {
        TreeNode? current = node;
        var guard = 0;
        while (current is not null && guard++ < 512)
        {
            yield return current;
            current = ParentOf(current.Id);
        }
    }

    /// <summary>Human readable path of group names, excluding the hidden root ("Client A / Production").</summary>
    public string GroupPath(TreeNode node)
    {
        var names = SelfAndAncestors(node).Skip(1).Where(n => !ReferenceEquals(n, Root)).Select(n => n.Name).Reverse();
        return string.Join(" / ", names);
    }

    public static IEnumerable<ServerNode> AllServers(GroupNode root)
    {
        var stack = new Stack<TreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is ServerNode s) yield return s;
            else if (n is GroupNode g)
                for (var i = g.Children.Count - 1; i >= 0; i--) stack.Push(g.Children[i]);
        }
    }

    public static bool IsDescendantOf(GroupNode ancestor, TreeNode candidate)
    {
        foreach (var child in ancestor.Children)
        {
            if (ReferenceEquals(child, candidate)) return true;
            if (child is GroupNode g && IsDescendantOf(g, candidate)) return true;
        }
        return false;
    }
}
