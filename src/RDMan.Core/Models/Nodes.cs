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
    /// <summary>Properties written by a newer RDMan: kept as-is so saving here never drops them.</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Identity color key (see <c>TagColors</c>); null = neutral.</summary>
    public string? Color { get; set; }

    /// <summary>Credentials for file access (SMB). Remote desktop falls back to these.</summary>
    public CredentialSettings Credentials { get; set; } = new();

    /// <summary>
    /// Credentials only for remote desktop. <see cref="CredentialMode.Inherit"/> means "the parent's desktop
    /// credential, or — when nobody defines one — the same credential used for files".
    /// </summary>
    public CredentialSettings DesktopCredentials { get; set; } = new();

    /// <summary>Remote desktop options; every null field is inherited from the parent group.</summary>
    public RdpProfile Rdp { get; set; } = new();
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

    /// <summary>Shown in the Favorites view.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>Shared files (SMB) are offered for this server.</summary>
    public bool Files { get; set; } = true;

    /// <summary>Remote desktop (RDP) endpoint; null or disabled = no desktop for this server.</summary>
    public DesktopEndpoint? Desktop { get; set; }

    /// <summary>What a plain click opens when the server offers both.</summary>
    public ServerAction DefaultAction { get; set; } = ServerAction.Files;

    [JsonIgnore] public bool HasDesktop => Desktop is { Enabled: true };
    [JsonIgnore] public bool HasFiles => Files;

    /// <summary>The action a plain click performs, respecting which capabilities exist.</summary>
    [JsonIgnore]
    public ServerAction EffectiveDefaultAction => (DefaultAction, HasFiles, HasDesktop) switch
    {
        (_, true, false) => ServerAction.Files,
        (_, false, true) => ServerAction.Desktop,
        (var a, _, _) => a,
    };
}

public enum ServerAction { Files, Desktop }

public sealed class DesktopEndpoint
{
    public const int DefaultPort = 3389;

    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = DefaultPort;
}

public sealed class ConnectionDocument
{
    /// <summary>2: servers gained remote desktop (Desktop, DesktopCredentials, Rdp, Files, DefaultAction).</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Properties written by a newer RDMan: kept as-is so saving here never drops them.</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
    public GroupNode Root { get; set; } = new() { Name = "root" };
    public List<CredentialProfile> Profiles { get; set; } = new();

    /// <summary>Saved searches shown under Views (older builds ignore this property).</summary>
    public List<SmartGroup> SmartGroups { get; set; } = new();
}
