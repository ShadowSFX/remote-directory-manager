using System.Text;
using RDMan.Core.Import;
using RDMan.Core.Models;

namespace RDMan.Core.Tests;

public sealed class RdgImporterTests
{
    // Fake DPAPI: payloads decode to UTF-16 text; anything starting with byte 0xFF "belongs to another user".
    private static string? FakeUnprotect(byte[] b) => b.Length > 0 && b[0] == 0xFF ? null : Encoding.Unicode.GetString(b).TrimEnd('\0');
    private static string FakeProtect(string clear) => "prot:" + clear;
    private static string Pwd(string clear) => Convert.ToBase64String(Encoding.Unicode.GetBytes(clear + "\0"));
    private static readonly string Foreign = Convert.ToBase64String([0xFF, 1, 2, 3]);

    private static RdgImportResult Parse(string xml)
        => RdgImporter.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "fallback", FakeUnprotect, FakeProtect);

    private static string Schema3 => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <RDCMan programVersion="2.93" schemaVersion="3">
          <file>
            <credentialsProfiles>
              <credentialsProfile inherit="None">
                <profileName scope="Local">Admins</profileName>
                <userName>admin</userName>
                <password>{Pwd("s3cret")}</password>
                <domain>CORP</domain>
              </credentialsProfile>
            </credentialsProfiles>
            <properties><expanded>True</expanded><name>Datacenter</name></properties>
            <logonCredentials inherit="None">
              <profileName scope="File">Admins</profileName>
            </logonCredentials>
            <localResources inherit="None">
              <audioRedirection>NoSound</audioRedirection>
              <keyboardHook>Remote</keyboardHook>
              <redirectClipboard>True</redirectClipboard>
              <redirectDrives>True</redirectDrives>
            </localResources>
            <group>
              <properties><expanded>False</expanded><name>Web</name></properties>
              <server>
                <properties><displayName>Web 1</displayName><name>web1.corp.local</name><comment>front</comment></properties>
              </server>
              <server>
                <properties><name>10.0.0.9:3390</name></properties>
                <logonCredentials inherit="None">
                  <profileName scope="Local">Custom</profileName>
                  <userName>ops</userName>
                  <password>{Foreign}</password>
                  <domain />
                </logonCredentials>
                <connectionSettings inherit="None"><connectToConsole>True</connectToConsole><port>3391</port></connectionSettings>
                <remoteDesktop inherit="None"><colorDepth>16</colorDepth></remoteDesktop>
              </server>
            </group>
            <smartGroup>
              <properties><name>Webs</name></properties>
              <ruleGroup operator="Any">
                <rule><property>DisplayName</property><operator>Matches</operator><value>^web.*</value></rule>
                <rule><property>Comment</property><operator>DoesNotMatch</operator><value>legacy</value></rule>
              </ruleGroup>
            </smartGroup>
          </file>
          <connected /><favorites /><recentlyUsed />
        </RDCMan>
        """;

    [Fact]
    public void Reads_schema3_tree_servers_and_settings()
    {
        var r = Parse(Schema3);
        Assert.Equal("Datacenter", r.Root.Name);
        Assert.Equal(1, r.Groups);
        Assert.Equal(2, r.Servers);
        var web = (GroupNode)r.Root.Children[0];
        Assert.False(web.IsExpanded);
        var s1 = (ServerNode)web.Children[0];
        Assert.Equal("Web 1", s1.Name);
        Assert.Equal("web1.corp.local", s1.Host);
        Assert.Equal("front", s1.Description);
        Assert.True(s1.HasDesktop);
        Assert.False(s1.HasFiles);
        Assert.Equal(ServerAction.Desktop, s1.DefaultAction);
        Assert.Equal(CredentialMode.Inherit, s1.Credentials.Mode);
        // file-level settings land on the root group (inherited by everything)
        Assert.Equal(RdpAudio.Off, r.Root.Rdp.Audio);
        Assert.True(r.Root.Rdp.WindowsKeysToServer);
        Assert.True(r.Root.Rdp.RedirectDrives);
    }

    [Fact]
    public void Port_comes_from_connection_settings_or_the_name()
    {
        var s2 = (ServerNode)((GroupNode)Parse(Schema3).Root.Children[0]).Children[1];
        Assert.Equal("10.0.0.9", s2.Host);
        Assert.Equal(3391, s2.Desktop!.Port); // connectionSettings wins over "host:3390"
        Assert.True(s2.Rdp.AdminSession);
        Assert.Equal(16, s2.Rdp.ColorDepth);
    }

    [Fact]
    public void Credentials_map_to_profiles_and_custom_and_report_passwords()
    {
        var r = Parse(Schema3);
        var profile = Assert.Single(r.Profiles);
        Assert.Equal("Admins", profile.Name);
        Assert.Equal(@"CORP\admin", profile.Username);
        Assert.Equal("prot:s3cret", profile.ProtectedPassword);
        Assert.Equal(CredentialMode.Profile, r.Root.Credentials.Mode);
        Assert.Equal(profile.Id, r.Root.Credentials.ProfileId);
        var s2 = (ServerNode)((GroupNode)r.Root.Children[0]).Children[1];
        Assert.Equal(CredentialMode.Custom, s2.Credentials.Mode);
        Assert.Equal("ops", s2.Credentials.Username);
        Assert.Null(s2.Credentials.ProtectedPassword); // encrypted by another user: must be re-entered
        Assert.Equal(1, r.PasswordsRecovered);
        Assert.Equal(1, r.PasswordsToReenter);
    }

    [Fact]
    public void Smart_groups_become_plain_text_rules()
    {
        var r = Parse(Schema3);
        var g = Assert.Single(r.SmartGroups);
        Assert.Equal("Webs", g.Name);
        Assert.Equal(SmartMatch.Any, g.Match);
        Assert.Equal(SmartField.Name, g.Rules[0].Field);
        Assert.Equal("web", g.Rules[0].Value);
        Assert.Equal(SmartField.Notes, g.Rules[1].Field);
        Assert.Equal(SmartOp.NotContains, g.Rules[1].Op);
        Assert.Equal(1, r.RulesSimplified);
    }

    [Fact]
    public void Reads_the_old_2_2_layout()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan schemaVersion="1">
              <version>2.2</version>
              <file>
                <properties><name>Old</name></properties>
                <group>
                  <properties><name>G</name></properties>
                  <server><name>srv01</name><displayName>Server one</displayName><comment /></server>
                </group>
              </file>
            </RDCMan>
            """;
        var r = Parse(xml);
        Assert.Equal("Old", r.Root.Name);
        var s = (ServerNode)((GroupNode)r.Root.Children[0]).Children[0];
        Assert.Equal("Server one", s.Name);
        Assert.Equal("srv01", s.Host);
        Assert.Null(s.Description);
    }

    [Fact]
    public void Rejects_dtds_and_non_rdcman_files()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE RDCMan [ <!ENTITY x SYSTEM "file:///c:/windows/win.ini"> ]>
            <RDCMan><file><properties><name>&x;</name></properties></file></RDCMan>
            """;
        Assert.ThrowsAny<System.Xml.XmlException>(() => Parse(xxe));
        Assert.Throws<FormatException>(() => Parse("<root />"));
        Assert.Throws<FormatException>(() => Parse("<RDCMan />"));
    }

    [Fact]
    public void Unnamed_file_uses_the_fallback_name_and_skips_serverless_entries()
    {
        var r = Parse("<RDCMan><file><server><properties><displayName>no host</displayName></properties></server></file></RDCMan>");
        Assert.Equal("fallback", r.Root.Name);
        Assert.Equal(0, r.Servers);
    }

    [Theory]
    [InlineData("^web.*", "web")]
    [InlineData(@"db\.prod", "db.prod")]
    [InlineData(@"srv\d+", "srv")]
    [InlineData("plain", "plain")]
    public void Unregex_keeps_the_literal_text(string pattern, string expected) => Assert.Equal(expected, RdgImporter.Unregex(pattern));
}
