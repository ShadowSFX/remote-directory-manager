using System.Text.Json.Serialization;

namespace RDMan.Core.Models;

/// <summary>How a node obtains the credentials used to open an SMB session.</summary>
public enum CredentialMode
{
    /// <summary>Use whatever the parent group defines (root falls back to <see cref="CurrentUser"/>).</summary>
    Inherit = 0,
    /// <summary>Let Windows use the signed-in user / Credential Manager, exactly like typing \\host in Run.</summary>
    CurrentUser = 1,
    /// <summary>Anonymous/guest session ("guest" with an empty password).</summary>
    Guest = 2,
    /// <summary>Username + password stored on this node.</summary>
    Custom = 3,
    /// <summary>A reusable named credential profile.</summary>
    Profile = 4,
}

public sealed class CredentialSettings
{
    public CredentialMode Mode { get; set; } = CredentialMode.Inherit;
    public string? Username { get; set; }
    /// <summary>DPAPI-protected password (see <c>SecretProtector</c>). Never plaintext.</summary>
    public string? ProtectedPassword { get; set; }
    public Guid? ProfileId { get; set; }

    public CredentialSettings Clone() => (CredentialSettings)MemberwiseClone();
}

public sealed class CredentialProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string? ProtectedPassword { get; set; }

    public CredentialProfile Clone() => (CredentialProfile)MemberwiseClone();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GroupNode), "group")]
[JsonDerivedType(typeof(ServerNode), "server")]
public abstract class TreeNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Identity color key (see <c>TagColors</c>); null = neutral.</summary>
    public string? Color { get; set; }
    public CredentialSettings Credentials { get; set; } = new();
}

public sealed class GroupNode : TreeNode
{
    public List<TreeNode> Children { get; set; } = new();
    public bool IsExpanded { get; set; } = true;
}

public sealed class ServerNode : TreeNode
{
    /// <summary>Host name, IPv4 or IPv6 literal (no backslashes).</summary>
    public string Host { get; set; } = "";
    /// <summary>Optional share. Null/empty = list every share on the server.</summary>
    public string? Share { get; set; }
    /// <summary>Optional sub-folder inside the share to open by default.</summary>
    public string? StartFolder { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTimeOffset? LastConnected { get; set; }
}

public sealed class ConnectionDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GroupNode Root { get; set; } = new() { Name = "root" };
    public List<CredentialProfile> Profiles { get; set; } = new();
}
