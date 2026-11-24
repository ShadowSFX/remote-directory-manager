namespace RDMan.Core.Persistence;

public enum ThemePreference { System, Dark, Light }

public enum RowDensity { Comfortable, Compact }

public sealed class WindowPlacement
{
    /// <summary>Null until the window has been placed once (JSON cannot store NaN).</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1320;
    public double Height { get; set; } = 820;
    public bool Maximized { get; set; }
}

/// <summary>A tab to restore at startup. Exactly one of the location fields is meaningful, by <see cref="Kind"/>.</summary>
public sealed class SavedTab
{
    public string Kind { get; set; } = "home"; // home | group | folder
    public Guid? NodeId { get; set; }
    public string? Path { get; set; }
}

public sealed class SavedPane
{
    public List<SavedTab> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
}

/// <summary>
/// One node of the saved pane grid: a leaf (<see cref="Pane"/> = index into <see cref="AppSettings.Panes"/>) or a split
/// (<see cref="Dir"/> "h" = columns, "v" = rows, with <see cref="Children"/>). Plain strings/ints on purpose: an
/// unknown value must degrade to a default instead of failing the whole settings file.
/// </summary>
public sealed class SavedLayoutNode
{
    public int? Pane { get; set; }
    public string? Dir { get; set; }
    public double Weight { get; set; } = 1;
    public List<SavedLayoutNode>? Children { get; set; }
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Settings written by a newer RDMan: kept as-is so saving here never drops them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>"system", "es" or "en".</summary>
    public string Language { get; set; } = "system";
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public RowDensity Density { get; set; } = RowDensity.Comfortable;

    public int HealthCheckIntervalSeconds { get; set; } = 30;
    public bool ShowHiddenFiles { get; set; }
    public bool ShowHiddenShares { get; set; }
    public bool DisconnectOnExit { get; set; }
    public bool AutoUpdate { get; set; } = true;
    public bool RestoreTabs { get; set; } = true;
    public bool ThisPcExpanded { get; set; } = true;
    public bool ViewsExpanded { get; set; } = true;

    public double SidebarWidth { get; set; } = 272;
    /// <summary>1.x flag (two panes, right one shown). Still written so an older build degrades sensibly.</summary>
    public bool SplitView { get; set; }
    public WindowPlacement Window { get; set; } = new();
    /// <summary>Panes in reading order; <see cref="Layout"/> places them.</summary>
    public List<SavedPane> Panes { get; set; } = new();
    public SavedLayoutNode? Layout { get; set; }
    /// <summary>Index into <see cref="Panes"/> of the pane that had the keyboard.</summary>
    public int ActivePane { get; set; }

    /// <summary>Version that last ran; used to show "What's new" once after an update.</summary>
    public string? LastRunVersion { get; set; }

    /// <summary>SSH terminal defaults (servers and groups can override some of them).</summary>
    public TerminalSettings Terminal { get; set; } = new();

    public void Normalize()
    {
        if (Language is not ("system" or "es" or "en")) Language = "system";
        HealthCheckIntervalSeconds = Math.Clamp(HealthCheckIntervalSeconds, 10, 3600);
        if (double.IsNaN(SidebarWidth) || SidebarWidth < 200 || SidebarWidth > 640) SidebarWidth = 272;
        Window ??= new WindowPlacement();
        if (double.IsNaN(Window.Width) || Window.Width < 640) Window.Width = 1320;
        if (double.IsNaN(Window.Height) || Window.Height < 420) Window.Height = 820;
        if (Window.Left is { } l && (double.IsNaN(l) || double.IsInfinity(l))) Window.Left = null;
        if (Window.Top is { } t && (double.IsNaN(t) || double.IsInfinity(t))) Window.Top = null;
        Terminal ??= new TerminalSettings();
        Terminal.Normalize();
        Panes ??= new List<SavedPane>();
        if (Panes.Count > RDMan.Core.Layout.PaneLayout.MaxPanes) Panes = Panes.Take(RDMan.Core.Layout.PaneLayout.MaxPanes).ToList();
        ActivePane = Panes.Count == 0 ? 0 : Math.Clamp(ActivePane, 0, Panes.Count - 1);
        foreach (var p in Panes)
        {
            p.Tabs ??= new List<SavedTab>();
            if (p.Tabs.Count > 30) p.Tabs = p.Tabs.Take(30).ToList();
            p.ActiveIndex = p.Tabs.Count == 0 ? 0 : Math.Clamp(p.ActiveIndex, 0, p.Tabs.Count - 1);
        }
    }
}
