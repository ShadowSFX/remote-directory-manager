using System.IO;
using System.Windows;
using System.Windows.Threading;
using RDMan.Core.Persistence;
using RDMan.Core.Security;
using RDMan.Infrastructure;
using RDMan.Services;
using RDMan.ViewModels;

namespace RDMan;

public partial class App : Application
{
    private SingleInstance? _instance;
    private MainViewModel? _main;
    private bool _shutdownPrepared;

    public static AppServices Services { get; private set; } = null!;

    /// <summary>Set when "Restart now" already swapped the exe.</summary>
    public static bool SkipUpdateOnExit { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var updated = e.Args.Contains("--updated", StringComparer.OrdinalIgnoreCase);
        var connectArg = ConnectArgument(e.Args, out var rest);
        var pathArg = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        _instance = new SingleInstance();
        if (!_instance.TryAcquire(waitForPrevious: updated))
        {
            _instance.Forward(connectArg is not null ? ConnectPrefix + connectArg : pathArg ?? "");
            _instance = null;
            Shutdown();
            return;
        }
        _instance.Activated += path => Dispatcher.BeginInvoke(() => OnActivatedByAnotherLaunch(path));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log(ev.Exception); ev.SetObserved(); };

        base.OnStartup(e);
        UpdateService.CleanupLeftovers();

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var paths = AppPaths.Resolve(exeDir, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        _logDir = Path.Combine(paths.DataDirectory, "logs");

        var settings = JsonFileStore.Load(paths.SettingsFile, () => new AppSettings()).Value;
        settings.Normalize();
        Loc.Instance.Apply(settings.Language);
        ThemeManager.Instance.Apply(settings.Theme);
        ApplyDensity(settings.Density);

        var docResult = ConnectionStore.Load(paths.ConnectionsFile);
        var secrets = new DpapiSecretProtector();
        var state = new AppState(paths, docResult.Value, settings, secrets);
        var operations = new ShellOperations();
        Services = new AppServices
        {
            State = state,
            Connections = new ConnectionService(secrets),
            Health = new HealthMonitor(Dispatcher),
            Operations = operations,
            Clipboard = new FileClipboard(),
            Icons = new IconProvider(),
            ShellMenu = new ShellContextMenu(),
            Updates = new UpdateService(() => operations.RunningCount),
            Toasts = new ToastService(Dispatcher),
        };

        _main = new MainViewModel(Services);
        var window = new MainWindow(_main);
        MainWindow = window;
        window.Show();
        _main.RestoreTabs();

        // "RDMan.exe \\host\share" (or a local folder) opens it in a new tab.
        if (!string.IsNullOrWhiteSpace(pathArg))
            _main.OpenInNewTab(new FolderLocation(pathArg), _main.ActivePane);
        // "RDMan.exe /c Name" connects to that server (like mstsc /v, by RDMan name or address).
        // Once the window has laid out, so the session starts at its real size.
        if (connectArg is not null) Dispatcher.BeginInvoke(() => _main.ConnectByName(connectArg), DispatcherPriority.ContextIdle);

        Services.Health.Start(() => _main.AllServers().SelectMany(s => s.Endpoints()), settings.HealthCheckIntervalSeconds);
        Services.Updates.StartBackgroundChecks(() => state.Settings.AutoUpdate);

        switch (docResult.Outcome)
        {
            case LoadOutcome.RecoveredFromBackup: Services.Toasts.Show(Loc.T("Toast.Recovered"), ToastKind.Info); break;
            case LoadOutcome.Corrupt: Services.Toasts.Show(Loc.T("Toast.Corrupt"), ToastKind.Error); break;
        }

        var current = UpdateService.CurrentVersionText;
        if (settings.LastRunVersion is not null && settings.LastRunVersion != current && UpdateService.CurrentVersion.Prerelease is null)
        {
            Services.Toasts.Show(Loc.F("Update.WhatsNew", current), ToastKind.Success, Loc.T("Update.SeeNotes"),
                () => _main.OpenReleasesCommand.Execute(null), TimeSpan.FromSeconds(15));
        }
        if (settings.LastRunVersion != current)
        {
            settings.LastRunVersion = current;
            state.MarkSettingsDirty();
        }
    }

    public static void ApplyDensity(RowDensity density)
    {
        Current.Resources["Row.Height"] = density == RowDensity.Compact ? 26.0 : 32.0;
        Current.Resources["Tree.RowHeight"] = density == RowDensity.Compact ? 28.0 : 32.0;
    }

    private void OnActivatedByAnotherLaunch(string path)
    {
        (MainWindow as MainWindow)?.BringToFront();
        if (_main is null || string.IsNullOrWhiteSpace(path)) return;
        if (path.StartsWith(ConnectPrefix, StringComparison.Ordinal)) _main.ConnectByName(path[ConnectPrefix.Length..]);
        else _main.OpenInNewTab(new FolderLocation(path), _main.ActivePane);
    }

    // Not a valid path start, so a forwarded folder can never be mistaken for it.
    private const string ConnectPrefix = "\u0001connect:";

    /// <summary>The server name after /c (or /connect, --connect); <paramref name="rest"/> is every other argument.</summary>
    private static string? ConnectArgument(string[] args, out List<string> rest)
    {
        rest = new List<string>();
        string? name = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("/c", StringComparison.OrdinalIgnoreCase) || a.Equals("/connect", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--connect", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length) name = args[++i];
                continue;
            }
            if (a.StartsWith("/c:", StringComparison.OrdinalIgnoreCase)) { name = a[3..]; continue; }
            rest.Add(a);
        }
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>Called by MainWindow.Closing so tabs/settings are saved while the window still exists.</summary>
    public void PrepareShutdown()
    {
        if (_shutdownPrepared || _main is null) return;
        _shutdownPrepared = true;
        _main.PrepareShutdown();
    }

    /// <summary>True while Windows is signing out/shutting down: never block that with a confirmation.</summary>
    public static bool IsWindowsSessionEnding { get; private set; }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsWindowsSessionEnding = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PrepareShutdown();
        if (Services is not null)
        {
            if (!SkipUpdateOnExit) Services.Updates.TryApply(relaunch: false);
            Services.Updates.Dispose();
            Services.Health.Dispose();
        }
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static string? _logDir;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        // Keep the app alive for recoverable UI errors; the details go to the log for diagnosis.
        e.Handled = true;
        Services?.Toasts.Show(Loc.F("File.OperationFailed", e.Exception.Message), ToastKind.Error);
    }

    public static void Log(Exception ex)
    {
        try
        {
            if (_logDir is null) return;
            Directory.CreateDirectory(_logDir);
            var file = Path.Combine(_logDir, $"rdman-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file, $"[{DateTime.Now:O}] v{UpdateService.CurrentVersionText}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
