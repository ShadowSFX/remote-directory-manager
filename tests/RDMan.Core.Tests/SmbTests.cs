using System.ComponentModel;
using RDMan.Core.Smb;

namespace RDMan.Core.Tests;

public class SmbTests
{
    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5", null, null)]
    [InlineData(@"\\10.0.0.5", "10.0.0.5", null, null)]
    [InlineData(@"\\10.0.0.5\www", "10.0.0.5", "www", null)]
    [InlineData(@"\\10.0.0.5\www\a\b\", "10.0.0.5", "www", @"a\b")]
    [InlineData("//files.local/backups", "files.local", "backups", null)]
    [InlineData("smb://nas/media/movies", "nas", "media", "movies")]
    [InlineData("  \"\\\\vps-1\\data\"  ", "vps-1", "data", null)]
    [InlineData("10.0.0.5\\share", "10.0.0.5", "share", null)]
    [InlineData("[fe80::1]", "fe80::1", null, null)]
    [InlineData("fe80::1", "fe80::1", null, null)]
    [InlineData(@"\\fe80--1.ipv6-literal.net\x", "fe80::1", "x", null)]
    public void Parses_common_inputs(string input, string host, string? share, string? sub)
    {
        Assert.True(UncPath.TryParse(input, out var t, out var err), err.ToString());
        Assert.Equal(host, t!.Host);
        Assert.Equal(share, t.Share);
        Assert.Equal(sub, t.SubPath);
    }

    [Theory]
    [InlineData("", UncPath.ParseError.Empty)]
    [InlineData(@"\\", UncPath.ParseError.Empty)]
    [InlineData("10.0.0.5:445", UncPath.ParseError.PortNotSupported)]
    [InlineData("[fe80::1]:445", UncPath.ParseError.PortNotSupported)]
    [InlineData("300.1.1.1", UncPath.ParseError.InvalidHost)]
    [InlineData("bad host", UncPath.ParseError.InvalidHost)]
    [InlineData(@"\\host\sh*re", UncPath.ParseError.InvalidShare)]
    [InlineData(@"\\host\share\..\x", UncPath.ParseError.InvalidPath)]
    public void Rejects_invalid_inputs(string input, UncPath.ParseError expected)
    {
        Assert.False(UncPath.TryParse(input, out _, out var err));
        Assert.Equal(expected, err);
    }

    [Fact]
    public void Builds_paths_including_ipv6_literal_form()
    {
        var t = new SmbTarget("fe80::1", "data", @"a\b");
        Assert.Equal(@"\\fe80--1.ipv6-literal.net\data\a\b", t.FullPath);
        var v4 = new SmbTarget("10.0.0.5", null, null);
        Assert.Equal(@"\\10.0.0.5", v4.FullPath);
        Assert.Null(v4.ShareRoot);
    }

    [Fact]
    public void TrySplit_ignores_device_and_local_paths()
    {
        Assert.False(UncPath.TrySplit(@"C:\Users", out _));
        Assert.False(UncPath.TrySplit(@"\\?\C:\x", out _));
        Assert.True(UncPath.TrySplit(@"\\h\s\x", out var t));
        Assert.Equal("s", t!.Share);
    }

    [Fact]
    public void SameHost_is_case_and_ip_insensitive()
    {
        Assert.True(UncPath.SameHost("NAS", "nas"));
        Assert.True(UncPath.SameHost("fe80::0:1", "fe80::1"));
        Assert.False(UncPath.SameHost("10.0.0.1", "10.0.0.2"));
    }

    [Theory]
    [InlineData(1219, ErrorAction.CloseExistingSessions)]
    [InlineData(1272, ErrorAction.EnableGuestOrUseCredentials)]
    [InlineData(1326, ErrorAction.EditCredentials)]
    [InlineData(53, ErrorAction.Retry)]
    [InlineData(67, ErrorAction.CheckHost)]
    [InlineData(99999, ErrorAction.Retry)]
    public void Translates_errors_to_actions(int code, ErrorAction action)
    {
        Assert.Equal(action, SmbErrorTranslator.Translate(code).PrimaryAction);
    }

    [Fact]
    public void Every_translation_key_exists_in_strings()
    {
        foreach (var code in new[] { 2, 3, 5, 53, 59, 64, 65, 67, 86, 121, 1203, 1219, 1222, 1231, 1232, 1244, 1272, 1311, 1326, 1327, 1330, 1331, 1385, 1909, -445, 12345 })
        {
            var t = SmbErrorTranslator.Translate(code);
            Assert.True(Localization.Strings.Contains(t.TitleKey), t.TitleKey);
            Assert.True(Localization.Strings.Contains(t.DetailKey), t.DetailKey);
        }
    }

    [Fact]
    public void Guest_blocked_code_is_the_one_windows_reports()
    {
        // Verifies our constant against the OS message table rather than trusting memory.
        var msg = new Win32Exception(SmbErrorTranslator.ErrorGuestLogonBlocked).Message;
        Assert.False(string.IsNullOrWhiteSpace(msg));
        Assert.DoesNotContain("Unknown error", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HResult_is_unwrapped()
    {
        Assert.Equal(53, SmbErrorTranslator.Win32CodeFromHResult(unchecked((int)0x80070035)));
        Assert.Equal(1219, SmbErrorTranslator.Win32CodeFromHResult(1219));
    }
}
