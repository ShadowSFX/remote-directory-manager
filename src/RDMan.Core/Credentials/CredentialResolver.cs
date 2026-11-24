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
}
