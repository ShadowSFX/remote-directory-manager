using System.Security.Cryptography;
using System.Text.Json;

namespace RDMan.Core.Updates;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(string Tag, SemVersion Version, string Name, string Notes, string HtmlUrl, bool Prerelease, bool Draft, IReadOnlyList<ReleaseAsset> Assets);

public sealed record UpdatePlan(ReleaseInfo Release, ReleaseAsset Executable, ReleaseAsset Checksum);

public static class UpdatePlanner
{
    public const string ExecutableAssetName = "RDMan.exe";
    public const string ChecksumAssetName = "RDMan.exe.sha256";

    /// <summary>Parses a GitHub "release" JSON object (from /releases/latest). Returns null if it is not usable.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (!SemVersion.TryParse(tag, out var version)) return null;
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    var size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                        assets.Add(new ReleaseAsset(name, url, size));
                }
            }
            return new ReleaseInfo(
                tag!,
                version!,
                r.TryGetProperty("name", out var nm) ? nm.GetString() ?? tag! : tag!,
                r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True,
                r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
                assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decides whether <paramref name="release"/> should replace <paramref name="current"/>.
    /// Dev builds (prerelease "dev") never update; drafts/prereleases are ignored; both assets must exist.
    /// </summary>
    public static UpdatePlan? Plan(SemVersion current, ReleaseInfo? release)
    {
        if (release is null || release.Draft || release.Prerelease || release.Version.IsPrerelease) return null;
        if (current.Prerelease == "dev") return null;
        if (!(release.Version > current)) return null;
        var exe = release.Assets.FirstOrDefault(a => string.Equals(a.Name, ExecutableAssetName, StringComparison.OrdinalIgnoreCase));
        var sum = release.Assets.FirstOrDefault(a => string.Equals(a.Name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase));
        if (exe is null || sum is null) return null;
        return new UpdatePlan(release, exe, sum);
    }

    /// <summary>Reads the hex digest from a sha256 file ("&lt;hex&gt;" or "&lt;hex&gt;  RDMan.exe").</summary>
    public static string? ParseChecksum(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var token = content.Trim().Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        return token.ToLowerInvariant();
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
