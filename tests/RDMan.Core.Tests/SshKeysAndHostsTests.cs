using RDMan.Core.Ssh;

namespace RDMan.Core.Tests;

/// <summary>Expected values were produced with the Windows OpenSSH ssh-keygen (-lv, -H) for these exact keys.</summary>
public class SshKeysAndHostsTests
{
    private const string Ed25519 = "AAAAC3NzaC1lZDI1NTE5AAAAIDzqF7nCBpzFV3/B8BvExHSt/AqgAQvCwCcLsu/Z+XGd";
    private const string Rsa3072 = "AAAAB3NzaC1yc2EAAAADAQABAAABgQDaF1hvG9J06WDjcLNMIKFmy3OXi5HuB5V0jugLbSH5bnxcZT7D1MsWzADfHdH0Q2bCXX8UwoQGRKYrruSsmEkWEBvyg1BK+zUjcFgW/A9PKbxXKNGVdFs+uSCAV0fNNZc40w9Ywrk+xqbpMvyj4QZq/dc0qNzYOvE+kW2COeCpS9TFVbGAkj3jNq+euQOAfd0ORbzgcTmY2ABINRrgpwPdA73xIleZBpVq6MiKXOjQjn8+N7axBWkIkkBpfFdQJzwBRJFmaVmn6utpc/GKe7fciXyesW5heW2tEUNfZ8ZO50WrF1EPduKZvdZ1EvdMJHBx8CiXDj76N/x5Lehc8xaiXI5PzRllyjHNjr0rHnrnuBoYLUJKbpqhyajuijivuIdjUJu1rszq5rUT1jqnGyE6gba3J81ZBlSF3adTJj+/0ZcUTi2X0Y6wn6srf//6SjOwyRcpCIKF0X+JUXj7EO4+qCFnKfdttQwiv6Brp/hV6r1H2r+OuSbGx1r3FQhYGUk=";

    private static byte[] Blob(string b64) => Convert.FromBase64String(b64);

    [Fact]
    public void Sha256_fingerprint_matches_ssh_keygen()
    {
        Assert.Equal("SHA256:4I1ySNXEgVET9jyDOEi8nplcUv6ORyTjCMBdTR0De0s", Fingerprint.Sha256(Blob(Ed25519)));
        Assert.Equal("SHA256:vwxjC8t/MJQuaYujvQgpzZ2r4sJCBUi4otWmQsc3kqQ", Fingerprint.Sha256(Blob(Rsa3072)));
    }

    [Fact]
    public void Key_type_and_size_come_from_the_blob()
    {
        Assert.Equal(("ssh-ed25519", 256), Fingerprint.Describe(Blob(Ed25519)));
        Assert.Equal(("ssh-rsa", 3072), Fingerprint.Describe(Blob(Rsa3072)));
        Assert.Equal(("unknown", 0), Fingerprint.Describe([1, 2]));
    }

    [Fact]
    public void Randomart_matches_ssh_keygen()
    {
        const string expected = """
            +--[ED25519 256]--+
            |. ..o.=XX=.      |
            |.. o.oo++*.      |
            | .  o++..E=      |
            |  ..oo==+ .o     |
            |   +oO+=S.       |
            |    Bo. o        |
            |       +         |
            |      . o        |
            |       .         |
            +----[SHA256]-----+
            """;
        Assert.Equal(expected.Replace("\r\n", "\n"), Fingerprint.RandomArt(Blob(Ed25519), "ssh-ed25519", 256));
    }

    // Lines produced by `ssh-keygen -H` for "example.com" and "[jump.example.com]:2222".
    private const string Hashed = """
        |1|VfJ+h8ub3fGfh8JrL/RFCoFbidk=|yw9Ej+XRfrmth4aZTsQiWIq7DSQ= ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDzqF7nCBpzFV3/B8BvExHSt/AqgAQvCwCcLsu/Z+XGd
        |1|7kpcoAMRuQgo46S5fTm+WtkoOHw=|2nwbCQUnoAbHBmW0a5pHLRhncxM= ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDzqF7nCBpzFV3/B8BvExHSt/AqgAQvCwCcLsu/Z+XGd
        """;

    [Fact]
    public void Hashed_entries_from_openssh_are_recognized()
    {
        var kh = KnownHosts.Parse(Hashed);
        Assert.Equal(HostKeyStatus.Trusted, kh.Check("example.com", 22, "ssh-ed25519", Blob(Ed25519)).Status);
        Assert.Equal(HostKeyStatus.Trusted, kh.Check("jump.example.com", 2222, "ssh-ed25519", Blob(Ed25519)).Status);
        Assert.Equal(HostKeyStatus.Unknown, kh.Check("jump.example.com", 22, "ssh-ed25519", Blob(Ed25519)).Status);
    }

    [Fact]
    public void Different_key_of_the_same_type_is_a_change()
    {
        var kh = KnownHosts.Parse($"10.0.0.5 ssh-ed25519 {Ed25519}\n");
        var other = Blob(Ed25519);
        other[^1] ^= 0xFF;
        var r = kh.Check("10.0.0.5", 22, "ssh-ed25519", other);
        Assert.Equal(HostKeyStatus.Changed, r.Status);
        Assert.Equal(1, r.LineNumber);
    }

    [Fact]
    public void Only_other_key_types_known_is_unknown_with_a_hint()
    {
        var kh = KnownHosts.Parse($"10.0.0.5 ssh-rsa {Rsa3072}\n");
        var r = kh.Check("10.0.0.5", 22, "ssh-ed25519", Blob(Ed25519));
        Assert.Equal(HostKeyStatus.Unknown, r.Status);
        Assert.True(r.OtherTypesKnown);
    }

    [Fact]
    public void Revoked_keys_are_refused_and_wildcards_negation_work()
    {
        var kh = KnownHosts.Parse($"""
            @revoked * ssh-rsa {Rsa3072}
            *.corp,!evil.corp ssh-ed25519 {Ed25519}
            @cert-authority *.corp ssh-rsa {Rsa3072}
            # comment
            garbage
            """);
        Assert.Equal(HostKeyStatus.Revoked, kh.Check("any", 22, "ssh-rsa", Blob(Rsa3072)).Status);
        Assert.Equal(HostKeyStatus.Trusted, kh.Check("web.corp", 22, "ssh-ed25519", Blob(Ed25519)).Status);
        Assert.Equal(HostKeyStatus.Unknown, kh.Check("evil.corp", 22, "ssh-ed25519", Blob(Ed25519)).Status);
    }

    [Fact]
    public void Add_and_replace_keep_unrelated_lines()
    {
        var kh = KnownHosts.Parse($"# mine\nother {Rsa3072.Insert(0, "ssh-rsa ")}\n10.0.0.5,web ssh-ed25519 {Ed25519}\n");
        var newer = Blob(Ed25519);
        newer[^1] ^= 0x01;
        kh.Add("10.0.0.5", 22, "ssh-ed25519", newer, hash: false, replace: true);

        Assert.Equal(HostKeyStatus.Trusted, kh.Check("10.0.0.5", 22, "ssh-ed25519", newer).Status);
        Assert.Equal(HostKeyStatus.Trusted, kh.Check("web", 22, "ssh-ed25519", Blob(Ed25519)).Status); // shared line keeps "web"
        var text = kh.ToString();
        Assert.StartsWith("# mine\n", text);
        Assert.Contains("other ssh-rsa", text);
    }

    [Fact]
    public void Hashed_add_round_trips_and_uses_bracket_form_for_other_ports()
    {
        var kh = KnownHosts.Parse("");
        kh.Add("Box.Example", 2222, "ssh-ed25519", Blob(Ed25519), hash: true);
        Assert.StartsWith("|1|", kh.ToString());
        var reread = KnownHosts.Parse(kh.ToString());
        Assert.Equal(HostKeyStatus.Trusted, reread.Check("box.example", 2222, "ssh-ed25519", Blob(Ed25519)).Status);
        Assert.Equal("[box.example]:2222", KnownHosts.HostEntryName("Box.Example", 2222));
        Assert.Equal("box.example", KnownHosts.HostEntryName("Box.Example", 22));
    }

    [Fact]
    public void Crlf_files_parse()
    {
        var kh = KnownHosts.Parse($"a ssh-ed25519 {Ed25519}\r\nb ssh-rsa {Rsa3072}\r\n");
        Assert.Equal(2, kh.Count);
        Assert.Equal(HostKeyStatus.Trusted, kh.Check("b", 22, "ssh-rsa", Blob(Rsa3072)).Status);
    }
}
