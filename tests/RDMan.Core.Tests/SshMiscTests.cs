using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using RDMan.Core.Localization;
using RDMan.Core.Models;
using RDMan.Core.Persistence;
using RDMan.Core.Ssh;

namespace RDMan.Core.Tests;

public class SshMiscTests
{
    // ── Server stats ──────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] Frame1 =
    [
        "cpu  1000 0 500 8000 100 0 0 0 0 0",
        "0.52 0.40 0.31 2/301 12345",
        "MemTotal:        2000000 kB",
        "MemAvailable:     500000 kB",
        "/dev/root        41152736  10288184  30847768      26% /",
        "86400.50 170000.00",
    ];

    [Fact]
    public void Stats_frame_parses()
    {
        var s = ServerStats.ParseFrame(Frame1)!;
        Assert.Equal(9600, s.CpuTotal);
        Assert.Equal(8100, s.CpuIdle);
        Assert.Equal(0.52, s.Load1);
        Assert.Equal(75, s.MemUsedPercent, 3);
        Assert.Equal(25, s.DiskUsedPercent, 0);
        Assert.Equal(86400.5, s.UptimeSeconds);
        Assert.Equal("1d 0h", ServerStats.FormatUptime(s.UptimeSeconds));
    }

    [Fact]
    public void Cpu_percent_comes_from_two_samples()
    {
        var a = ServerStats.ParseFrame(Frame1)!;
        var next = (string[])Frame1.Clone();
        next[0] = "cpu  1300 0 600 8500 100 0 0 0 0 0"; // +400 busy, +500 idle
        var b = ServerStats.ParseFrame(next)!;
        Assert.Null(ServerStats.CpuPercent(null, b));
        Assert.Equal(44.44, ServerStats.CpuPercent(a, b)!.Value, 2);
        Assert.Null(ServerStats.CpuPercent(b, b));
    }

    [Fact]
    public void Incomplete_frames_are_rejected()
        => Assert.Null(ServerStats.ParseFrame(["cpu 1 2 3 4", "garbage"]));

    [Fact]
    public void Probe_command_is_plain_sh()
    {
        Assert.Contains(ServerStats.FrameMarker, ServerStats.ProbeCommand);
        Assert.DoesNotContain("\n", ServerStats.ProbeCommand);
    }

    // ── ANSI stripper ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Strips_colors_titles_and_applies_carriage_returns()
    {
        var a = new AnsiStripper();
        var output = a.Feed("\x1b[1;32mok\x1b[0m done\r\n\x1b]0;title\x07pro 10%\rpro 100%\r\nab\bc\n");
        Assert.Equal("ok done\npro 100%\nac\n", output);
    }

    [Fact]
    public void Sequences_split_across_feeds_are_handled()
    {
        var a = new AnsiStripper(() => "> ");
        var o = a.Feed("he\x1b[3") + a.Feed("1mllo\x1b]8;;http://x\x1b") + a.Feed("\\link\n") + a.Feed("tail");
        Assert.Equal("> hellolink\n", o);
        Assert.Equal("> tail\n", a.Flush());
        Assert.Equal("", a.Flush());
    }

    // ── Agent protocol ────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeAgent(byte[] reply) : Stream
    {
        private readonly MemoryStream _out = new(reply);
        public readonly MemoryStream Written = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _out.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }

    private static byte[] Frame(params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var f = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(f, (uint)body.Length);
        body.CopyTo(f, 4);
        return f;
    }

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] Str(byte[] v) => [.. U32((uint)v.Length), .. v];

    [Fact]
    public async Task Agent_lists_identities()
    {
        var blob = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAIDzqF7nCBpzFV3/B8BvExHSt/AqgAQvCwCcLsu/Z+XGd");
        var reply = Frame([12], U32(1), Str(blob), Str(Encoding.UTF8.GetBytes("me@pc")));
        var agent = new FakeAgent(reply);
        var ids = await new AgentClient(agent).RequestIdentitiesAsync();
        var id = Assert.Single(ids);
        Assert.Equal("me@pc", id.Comment);
        Assert.Equal("ssh-ed25519", id.KeyType);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 11 }, agent.Written.ToArray());
    }

    [Fact]
    public async Task Agent_signs_with_flags_and_reports_refusals()
    {
        var sig = Encoding.ASCII.GetBytes("signature");
        var agent = new FakeAgent(Frame([14], Str(sig)));
        var result = await new AgentClient(agent).SignAsync([1, 2], [3], AgentSignFlags.RsaSha2_512);
        Assert.Equal(sig, result);
        var sent = agent.Written.ToArray();
        Assert.Equal(13, sent[4]);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(sent.AsSpan(sent.Length - 4)));

        await Assert.ThrowsAsync<AgentException>(() => new AgentClient(new FakeAgent(Frame([5]))).SignAsync([1], [2], AgentSignFlags.None));
    }

    [Fact]
    public async Task Truncated_agent_replies_throw_cleanly()
        => await Assert.ThrowsAsync<AgentException>(() => new AgentClient(new FakeAgent(Frame([12], U32(3)))).RequestIdentitiesAsync());

    // ── Prompt script ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prompt_rc_is_lf_only_and_keeps_names_out_of_prompt_expansion()
    {
        var rc = PromptScript.RcFile();
        Assert.DoesNotContain("\r", rc);
        Assert.Contains("PS1='${__rdman_ps}'", rc);
        Assert.Contains("133;A", rc);
        Assert.Contains("]7;file://", rc);
        Assert.Contains(". ~/.profile", rc);
    }

    [Fact]
    public void Remote_command_embeds_the_rc_as_base64()
    {
        var cmd = PromptScript.RemoteCommand();
        Assert.StartsWith("exec sh -c '", cmd);
        Assert.EndsWith("'", cmd);
        var b64 = cmd[(cmd.IndexOf("printf %s ", StringComparison.Ordinal) + 10)..];
        b64 = b64[..b64.IndexOf(' ')];
        Assert.Equal(PromptScript.RcFile(), Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        // Only one single-quoted layer: the payload never contains a quote that could close it.
        Assert.Equal(2, cmd.Count(c => c == '\''));
    }

    // ── Model / persistence ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Terminal_only_servers_keep_files_off_after_normalize()
    {
        var doc = new ConnectionDocument();
        var s = new ServerNode { Host = "vps", Files = false, Terminal = new SshEndpoint { Port = 70000 } };
        s.Terminal.Forwards.Add(new SshForward { BindPort = 5432, TargetHost = "db", TargetPort = 5432 });
        s.Terminal.Forwards.Add(new SshForward { BindPort = 5433 }); // no target: dropped
        s.Terminal.Forwards.Add(new SshForward { Kind = SshForwardKind.Dynamic, BindPort = 1080 });
        doc.Root.Children.Add(s);
        ConnectionStore.Normalize(doc);
        Assert.False(s.Files);
        Assert.Null(s.Terminal.Port);
        Assert.Equal(2, s.Terminal.Forwards.Count);
        Assert.Equal(ServerAction.Terminal, s.EffectiveDefaultAction);
    }

    [Fact]
    public void Default_action_falls_back_to_an_offered_capability()
    {
        var s = new ServerNode { Files = true, DefaultAction = ServerAction.Terminal };
        Assert.Equal(ServerAction.Files, s.EffectiveDefaultAction);
        s.Terminal = new SshEndpoint();
        Assert.Equal(ServerAction.Terminal, s.EffectiveDefaultAction);
        Assert.Equal(2, s.CapabilityCount);
        s.Terminal.Enabled = false;
        Assert.False(s.HasTerminal);
    }

    [Fact]
    public void Export_without_secrets_strips_keys_and_ssh_passwords()
    {
        var doc = new ConnectionDocument();
        doc.SshKeys.Add(new SshKeyEntry { Name = "k", ProtectedKey = "dpapi1:k", ProtectedPassphrase = "dpapi1:p" });
        doc.Root.Children.Add(new ServerNode { Host = "h", SshAuth = new SshAuthSettings { Mode = SshAuthMode.Password, ProtectedPassword = "dpapi1:x" } });
        var copy = ConnectionStore.CloneForExport(doc, includeSecrets: false);
        Assert.Null(copy.SshKeys[0].ProtectedKey);
        Assert.Null(copy.SshKeys[0].ProtectedPassphrase);
        Assert.Equal("k", copy.SshKeys[0].Name);
        Assert.Null(((ServerNode)copy.Root.Children[0]).SshAuth.ProtectedPassword);
        Assert.Equal("dpapi1:k", doc.SshKeys[0].ProtectedKey); // original untouched
    }

    [Fact]
    public void Reassign_ids_remaps_keys_profiles_and_snippet_groups()
    {
        var doc = new ConnectionDocument();
        var key = new SshKeyEntry { ProtectedKey = "x" };
        var group = new GroupNode { Name = "g", SshAuth = new SshAuthSettings { Mode = SshAuthMode.Key, KeyId = key.Id } };
        doc.SshKeys.Add(key);
        doc.Root.Children.Add(group);
        doc.Snippets.Add(new Snippet { Name = "s", Body = "ls", GroupId = group.Id });
        doc.Snippets.Add(new Snippet { Name = "orphan", Body = "ls", GroupId = Guid.NewGuid() });
        var oldKey = key.Id;
        var oldGroup = group.Id;

        ConnectionStore.ReassignIds(doc);
        Assert.NotEqual(oldKey, key.Id);
        Assert.Equal(key.Id, group.SshAuth.KeyId);
        Assert.NotEqual(oldGroup, group.Id);
        Assert.Equal(group.Id, doc.Snippets[0].GroupId);
        Assert.Null(doc.Snippets[1].GroupId);
    }

    [Fact]
    public void Merge_reuses_known_keys_restores_missing_material_and_adds_snippets()
    {
        var into = new ConnectionDocument();
        var mine = new SshKeyEntry { Name = "mine", Fingerprint = "SHA256:same" }; // lost its key after an export
        into.SshKeys.Add(mine);

        var imported = new ConnectionDocument();
        var same = new SshKeyEntry { Name = "copy", Fingerprint = "SHA256:same", ProtectedKey = "dpapi1:k" };
        var other = new SshKeyEntry { Name = "other", Fingerprint = "SHA256:other", ProtectedKey = "dpapi1:o" };
        imported.SshKeys.AddRange([same, other]);
        var server = new ServerNode { Host = "h", SshAuth = new SshAuthSettings { Mode = SshAuthMode.Key, KeyId = same.Id } };
        imported.Root.Children.Add(server);
        imported.Snippets.Add(new Snippet { Name = "s", Body = "uptime" });

        ConnectionStore.MergeSsh(into, imported);
        Assert.Equal(2, into.SshKeys.Count);
        Assert.Equal(mine.Id, server.SshAuth.KeyId);
        Assert.Equal("dpapi1:k", mine.ProtectedKey);
        Assert.Contains(other, into.SshKeys);
        Assert.Single(into.Snippets);
    }

    [Fact]
    public void Ssh_fields_round_trip_through_json()
    {
        var doc = new ConnectionDocument();
        var s = new ServerNode
        {
            Host = "h",
            Terminal = new SshEndpoint { Port = 2222, UseSshConfig = false },
            DefaultAction = ServerAction.Terminal,
            SshAuth = new SshAuthSettings { Mode = SshAuthMode.Agent, Username = "u" },
            Ssh = new SshProfile { ColorScheme = "dracula", ShowStats = false },
        };
        doc.Root.Children.Add(s);
        doc.Snippets.Add(new Snippet { Name = "n", Body = "b" });
        var json = JsonSerializer.Serialize(doc, JsonFileStore.Options);
        Assert.Contains("\"terminal\"", json);
        Assert.Contains("\"defaultAction\": \"terminal\"", json);
        Assert.DoesNotContain("hasTerminal", json);
        var back = JsonSerializer.Deserialize<ConnectionDocument>(json, JsonFileStore.Options)!;
        var b = (ServerNode)back.Root.Children[0];
        Assert.Equal(2222, b.Terminal!.Port);
        Assert.False(b.Terminal.UseSshConfig);
        Assert.Equal(SshAuthMode.Agent, b.SshAuth.Mode);
        Assert.Equal("dracula", b.Ssh.ColorScheme);
        Assert.Single(back.Snippets);
    }

    [Fact]
    public void Clone_settings_copies_every_capability_but_not_identity()
    {
        var s = new ServerNode
        {
            Name = "n", Host = "h", IsFavorite = true, Tags = ["a"],
            Desktop = new DesktopEndpoint { Port = 3390 },
            Terminal = new SshEndpoint { Port = 2200, Forwards = { new SshForward { BindPort = 1, TargetHost = "t", TargetPort = 2 } } },
            SshAuth = new SshAuthSettings { Username = "u" },
        };
        s.IsExpanded = true;
        var c = s.CloneSettings();
        Assert.NotEqual(s.Id, c.Id);
        Assert.False(c.IsFavorite);
        Assert.False(c.IsExpanded); // tree state, not a setting
        Assert.Equal(3390, c.Desktop!.Port);
        Assert.Equal(2200, c.Terminal!.Port);
        Assert.NotSame(s.Terminal.Forwards[0], c.Terminal.Forwards[0]);
        Assert.Equal("u", c.SshAuth.Username);
        c.Tags.Add("b");
        Assert.Single(s.Tags);
    }

    [Fact]
    public void Offered_actions_keep_files_desktop_terminal_order()
    {
        var all = new ServerNode { DefaultAction = ServerAction.Terminal, Desktop = new DesktopEndpoint(), Terminal = new SshEndpoint() };
        Assert.Equal([ServerAction.Files, ServerAction.Desktop, ServerAction.Terminal], all.OfferedActions);

        var noFiles = new ServerNode { Files = false, Desktop = new DesktopEndpoint { Enabled = false }, Terminal = new SshEndpoint() };
        Assert.Equal([ServerAction.Terminal], noFiles.OfferedActions);

        Assert.Empty(new ServerNode { Files = false }.OfferedActions);
    }

    [Fact]
    public void Server_expansion_round_trips_and_defaults_to_collapsed()
    {
        var json = JsonSerializer.Serialize<TreeNode>(new ServerNode { Host = "h", IsExpanded = true }, JsonFileStore.Options);
        Assert.True(Assert.IsType<ServerNode>(JsonSerializer.Deserialize<TreeNode>(json, JsonFileStore.Options)).IsExpanded);

        // Files written before the setting existed.
        var old = JsonSerializer.Deserialize<TreeNode>("""{"kind":"server","host":"h"}""", JsonFileStore.Options);
        Assert.False(Assert.IsType<ServerNode>(old).IsExpanded);
    }

    [Fact]
    public void Terminal_settings_are_clamped()
    {
        var t = new TerminalSettings { FontSize = 900, LineHeight = double.NaN, Scrollback = -5, FontFamily = " ", MinimumContrast = 0, FontWeight = 450 };
        t.Normalize();
        Assert.Equal(32, t.FontSize);
        Assert.Equal(1.15, t.LineHeight);
        Assert.Equal(0, t.Scrollback);
        Assert.Equal(TerminalSettings.DefaultFont, t.FontFamily);
        Assert.Equal(1, t.MinimumContrast);
        Assert.Equal(400, t.FontWeight);
    }

    // ── Schemes and errors ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Schemes_have_16_valid_colors_and_unique_ids()
    {
        Assert.Equal(TerminalSchemes.All.Count, TerminalSchemes.All.Select(s => s.Id).Distinct().Count());
        foreach (var s in TerminalSchemes.All)
        {
            Assert.Equal(16, s.Ansi.Count);
            foreach (var c in s.Ansi.Append(s.Background).Append(s.Foreground).Append(s.Cursor))
                Assert.Matches("^#[0-9A-F]{6}$", c);
            Assert.Matches("^#[0-9A-F]{8}$", s.Selection);
        }
        Assert.Same(TerminalSchemes.Night, TerminalSchemes.Resolve("auto", appIsDark: true));
        Assert.Same(TerminalSchemes.Day, TerminalSchemes.Resolve("missing", appIsDark: false));
        Assert.Equal("dracula", TerminalSchemes.Resolve("dracula", true).Id);
    }

    [Fact]
    public void Every_ssh_error_has_localized_text()
    {
        foreach (var f in Enum.GetValues<SshFailure>())
        {
            var e = SshErrorTranslator.Translate(f);
            Assert.True(Strings.Contains(e.TitleKey), e.TitleKey);
            Assert.True(Strings.Contains(e.DetailKey), e.DetailKey);
        }
    }
}
