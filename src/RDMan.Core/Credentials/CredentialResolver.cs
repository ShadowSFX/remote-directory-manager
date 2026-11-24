using RDMan.Core.Models;

namespace RDMan.Core.Credentials;

public enum EffectiveCredentialKind
{
    CurrentUser,
    Guest,
    Explicit,
}

/// <summary>The credential a server will actually use, plus where it came from (for the UI).</summary>
public sealed record EffectiveCredential(
    EffectiveCredentialKind Kind,
    string? Username,
    string? ProtectedPassword,
    TreeNode? SourceNode,
    CredentialProfile? Profile,
    /// <summary>True when the configuration is broken (missing profile, empty username) and we fell back.</summary>
    bool IsMisconfigured = false)
{
    public static readonly string GuestUserName = "guest";

    public bool IsInherited(TreeNode forNode) => SourceNode is not null && !ReferenceEquals(SourceNode, forNode);
}

public static class CredentialResolver
{
    /// <summary>
    /// Walks from <paramref name="node"/> up through its groups until a non-Inherit mode is found.
    /// The (hidden) root falls back to the current Windows user, mirroring how \\host behaves in Explorer.
    /// </summary>
    public static EffectiveCredential Resolve(TreeNode node, TreeIndex index, IReadOnlyList<CredentialProfile> profiles)
    {
        foreach (var current in index.SelfAndAncestors(node))
        {
            var c = current.Credentials ?? new CredentialSettings();
            switch (c.Mode)
            {
                case CredentialMode.Inherit:
                    continue;
                case CredentialMode.CurrentUser:
                    return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, current, null);
                case CredentialMode.Guest:
                    return new EffectiveCredential(EffectiveCredentialKind.Guest, EffectiveCredential.GuestUserName, null, current, null);
                case CredentialMode.Custom:
                    if (string.IsNullOrWhiteSpace(c.Username))
                        return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, current, null, IsMisconfigured: true);
                    return new EffectiveCredential(EffectiveCredentialKind.Explicit, c.Username.Trim(), c.ProtectedPassword, current, null);
                case CredentialMode.Profile:
                    var profile = c.ProfileId is { } pid ? profiles.FirstOrDefault(p => p.Id == pid) : null;
                    if (profile is null || string.IsNullOrWhiteSpace(profile.Username))
                        return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, current, profile, IsMisconfigured: true);
                    return new EffectiveCredential(EffectiveCredentialKind.Explicit, profile.Username.Trim(), profile.ProtectedPassword, current, profile);
            }
        }

        return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, null, null);
    }

    /// <summary>
    /// Credential for remote desktop: the nearest <see cref="TreeNode.DesktopCredentials"/> that isn't Inherit,
    /// otherwise the same credential the node uses for files (<see cref="Resolve"/>).
    /// For RDP, <see cref="EffectiveCredentialKind.CurrentUser"/> means "let Windows use its saved credential
    /// for this computer (Credential Manager / CredSSP), and ask if there is none".
    /// </summary>
    public static EffectiveCredential ResolveDesktop(TreeNode node, TreeIndex index, IReadOnlyList<CredentialProfile> profiles)
    {
        foreach (var current in index.SelfAndAncestors(node))
        {
            var c = current.DesktopCredentials;
            if (c is null || c.Mode == CredentialMode.Inherit) continue;
            return FromSettings(c, current, profiles);
        }
        return Resolve(node, index, profiles);
    }

    private static EffectiveCredential FromSettings(CredentialSettings c, TreeNode source, IReadOnlyList<CredentialProfile> profiles)
    {
        switch (c.Mode)
        {
            case CredentialMode.Guest:
                return new EffectiveCredential(EffectiveCredentialKind.Guest, EffectiveCredential.GuestUserName, null, source, null);
            case CredentialMode.Custom when !string.IsNullOrWhiteSpace(c.Username):
                return new EffectiveCredential(EffectiveCredentialKind.Explicit, c.Username.Trim(), c.ProtectedPassword, source, null);
            case CredentialMode.Profile:
                var profile = c.ProfileId is { } pid ? profiles.FirstOrDefault(p => p.Id == pid) : null;
                if (profile is null || string.IsNullOrWhiteSpace(profile.Username))
                    return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, source, profile, IsMisconfigured: true);
                return new EffectiveCredential(EffectiveCredentialKind.Explicit, profile.Username.Trim(), profile.ProtectedPassword, source, profile);
            case CredentialMode.Custom:
                return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, source, null, IsMisconfigured: true);
            default:
                return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, source, null);
        }
    }

    /// <summary>Splits "DOMAIN\user" or "user@domain" for protocols (RDP) that take them separately.</summary>
    public static (string User, string? Domain) SplitUser(string? username)
    {
        var u = (username ?? "").Trim();
        var slash = u.IndexOf('\\');
        if (slash > 0) return (u[(slash + 1)..], u[..slash]);
        return (u, null); // user@domain (UPN) is passed as-is; RDP accepts it in the user field
    }
}
