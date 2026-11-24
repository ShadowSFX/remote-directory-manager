using RDMan.Core.Models;

namespace RDMan.Core.Rdp;

/// <summary>Resolved value of one option and where it came from (null source = built-in default).</summary>
public readonly record struct Resolved<T>(T Value, TreeNode? Source)
{
    public bool IsDefault => Source is null;
}

public sealed record EffectiveRdpSettings(
    Resolved<bool> RedirectClipboard,
    Resolved<bool> RedirectDrives,
    Resolved<bool> RedirectPrinters,
    Resolved<bool> RedirectSmartCards,
    Resolved<RdpAudio> Audio,
    Resolved<bool> WindowsKeysToServer,
    Resolved<bool> NetworkLevelAuthentication,
    Resolved<RdpServerCheck> ServerCheck,
    Resolved<RdpSignInProtection> SignInProtection,
    Resolved<bool> AdminSession,
    Resolved<int> ColorDepth,
    /// <summary>The gateway in effect (null value = direct connection).</summary>
    Resolved<RdpGateway?> Gateway);

/// <summary>
/// Merges <see cref="RdpProfile"/> field by field from the node up to the root, keeping the origin of each value
/// so the editor can say "inherited from «Production»" next to it.
/// </summary>
public static class RdpSettingsResolver
{
    public static readonly RdpProfile Defaults = new()
    {
        RedirectClipboard = true,
        RedirectDrives = false,
        RedirectPrinters = false,
        RedirectSmartCards = false,
        Audio = RdpAudio.Local,
        WindowsKeysToServer = true,
        NetworkLevelAuthentication = true,
        ServerCheck = RdpServerCheck.Warn,
        SignInProtection = RdpSignInProtection.Standard,
        AdminSession = false,
        ColorDepth = 32,
    };

    public static EffectiveRdpSettings Resolve(TreeNode node, TreeIndex index) => Resolve(index.SelfAndAncestors(node).ToList());

    /// <summary>What the node would get if it defined nothing itself (the editor's "inherit" preview).</summary>
    public static EffectiveRdpSettings ResolveParent(TreeNode node, TreeIndex index)
        => index.ParentOf(node.Id) is { } parent ? Resolve(parent, index) : Resolve([]);

    /// <summary>Same as <see cref="ResolveParent"/> for a node that isn't in the tree yet (new server/group).</summary>
    public static EffectiveRdpSettings ResolveUnder(GroupNode? parent, TreeIndex index)
        => parent is null ? Resolve([]) : Resolve(parent, index);

    private static EffectiveRdpSettings Resolve(IReadOnlyList<TreeNode> chain)
    {
        Resolved<T> Pick<T>(Func<RdpProfile, T?> get) where T : struct
        {
            foreach (var n in chain)
                if (n.Rdp is not null && get(n.Rdp) is { } v) return new Resolved<T>(v, n);
            return new Resolved<T>(get(Defaults)!.Value, null);
        }

        var gateway = new Resolved<RdpGateway?>(null, null);
        foreach (var n in chain)
        {
            if (n.Rdp?.Gateway is not { } g) continue;
            // An explicit "off" (or a gateway without a host) means direct, overriding parents.
            gateway = new Resolved<RdpGateway?>(g.Usage == RdpGatewayUsage.Off || string.IsNullOrWhiteSpace(g.Host) ? null : g, n);
            break;
        }

        var depth = Pick(p => p.ColorDepth);
        if (depth.Value is not (16 or 24 or 32)) depth = new Resolved<int>(32, depth.Source);

        return new EffectiveRdpSettings(
            Pick(p => p.RedirectClipboard),
            Pick(p => p.RedirectDrives),
            Pick(p => p.RedirectPrinters),
            Pick(p => p.RedirectSmartCards),
            Pick(p => p.Audio),
            Pick(p => p.WindowsKeysToServer),
            Pick(p => p.NetworkLevelAuthentication),
            Pick(p => p.ServerCheck),
            Pick(p => p.SignInProtection),
            Pick(p => p.AdminSession),
            depth,
            gateway);
    }
}
