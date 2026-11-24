using System.Globalization;

namespace RDMan.Core.Ssh;

/// <summary>One reading of a Linux server's /proc and df (see <see cref="ServerStats.ProbeCommand"/>).</summary>
public sealed record StatsSample(
    long CpuTotal,
    long CpuIdle,
    double Load1,
    double Load5,
    double Load15,
    long MemTotalKb,
    long MemAvailableKb,
    long DiskTotalKb,
    long DiskUsedKb,
    double UptimeSeconds)
{
    public double MemUsedPercent => MemTotalKb > 0 ? 100.0 * (MemTotalKb - MemAvailableKb) / MemTotalKb : 0;
    public double DiskUsedPercent => DiskTotalKb > 0 ? 100.0 * DiskUsedKb / DiskTotalKb : 0;
}

/// <summary>Live server statistics over one long-lived exec channel, parsed here so it can be tested.</summary>
public static class ServerStats
{
    public const string FrameMarker = "@@rdman-stats";
    public const string OsMarker = "@@rdman-os";
    public const int IntervalSeconds = 3;

    /// <summary>
    /// POSIX sh: prints the OS once, then a frame every few seconds until the channel closes. Exits right away
    /// (non-zero) on systems without /proc/stat so the UI can hide the stats.
    /// </summary>
    public static string ProbeCommand =>
        "LC_ALL=C; export LC_ALL; [ -r /proc/stat ] || exit 3; " +
        $"echo {OsMarker}; (. /etc/os-release 2>/dev/null && echo \"${{PRETTY_NAME:-$NAME}}\") || uname -sr; " +
        $"while :; do echo {FrameMarker}; head -n1 /proc/stat; cat /proc/loadavg; " +
        "grep -E '^(MemTotal|MemAvailable):' /proc/meminfo; df -Pk / | tail -n1; cat /proc/uptime; " +
        $"sleep {IntervalSeconds} || exit 0; done";

    /// <summary>Parses the lines of one frame (between two markers). Returns null when something is missing.</summary>
    public static StatsSample? ParseFrame(IReadOnlyList<string> lines)
    {
        long cpuTotal = -1, cpuIdle = -1, memTotal = -1, memAvail = -1, diskTotal = -1, diskUsed = -1;
        double l1 = -1, l5 = -1, l15 = -1, uptime = -1;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (f[0] == "cpu" && f.Length >= 5)
            {
                // user nice system idle iowait irq softirq steal (guest columns are already in user/nice).
                long total = 0;
                for (var i = 1; i < Math.Min(f.Length, 9); i++) total += L(f[i]);
                cpuTotal = total;
                cpuIdle = L(f[4]) + (f.Length > 5 ? L(f[5]) : 0);
            }
            else if (f[0] == "MemTotal:" && f.Length >= 2) memTotal = L(f[1]);
            else if (f[0] == "MemAvailable:" && f.Length >= 2) memAvail = L(f[1]);
            else if (f.Length >= 5 && f[3].Contains('/') && D(f[0]) >= 0 && D(f[1]) >= 0 && D(f[2]) >= 0)
            {
                // /proc/loadavg: "0.15 0.10 0.05 1/123 4567"
                l1 = D(f[0]); l5 = D(f[1]); l15 = D(f[2]);
            }
            else if (f.Length >= 6 && f[^2].EndsWith('%') && L(f[1]) >= 0)
            {
                // df -Pk: "Filesystem 1024-blocks Used Available Capacity Mounted"
                diskTotal = L(f[^5]);
                diskUsed = L(f[^4]);
            }
            else if (f.Length == 2 && D(f[0]) >= 0 && D(f[1]) >= 0 && uptime < 0)
            {
                uptime = D(f[0]);
            }
        }

        if (cpuTotal < 0 || memTotal <= 0 || memAvail < 0 || l1 < 0) return null;
        return new StatsSample(cpuTotal, cpuIdle, l1, l5, l15, memTotal, memAvail,
            Math.Max(diskTotal, 0), Math.Max(diskUsed, 0), Math.Max(uptime, 0));
    }

    /// <summary>CPU busy % between two samples (0-100), null when the counters didn't advance.</summary>
    public static double? CpuPercent(StatsSample? previous, StatsSample current)
    {
        if (previous is null) return null;
        var total = current.CpuTotal - previous.CpuTotal;
        var idle = current.CpuIdle - previous.CpuIdle;
        if (total <= 0) return null;
        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    /// <summary>"3d 4h", "5h 12m", "42m" (culture-neutral, short).</summary>
    public static string FormatUptime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
        return $"{Math.Max(1, t.Minutes)}m";
    }

    private static long L(string s) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1;

    private static double D(string s) => double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : -1;
}
