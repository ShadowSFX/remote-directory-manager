using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Models;
using RDMan.Core.Search;
using RDMan.Core.Ssh;
using RDMan.Infrastructure;

namespace RDMan.ViewModels;

public enum PaletteKind { Server, QuickConnect, Snippet, NewSnippet }

public sealed class PaletteResult
{
    public PaletteResult(ServerNodeViewModel server, string groupPath)
    {
        Kind = PaletteKind.Server;
        Server = server;
        GroupPath = groupPath;
        Title = server.Name;
    }

    private PaletteResult(PaletteKind kind, string title, string subtitle, string glyph)
    {
        Kind = kind;
        Title = title;
        GroupPath = subtitle;
        Glyph = glyph;
    }

    public static PaletteResult Quick(SshDestination destination, bool fromConfig)
        => new(PaletteKind.QuickConnect, Loc.F("Palette.Quick.Title", destination.ToString()),
               Loc.T(fromConfig ? "Palette.Quick.FromConfig" : "Palette.Quick.Subtitle"), "") { Destination = destination };

    public static PaletteResult ForSnippet(Snippet snippet)
        => new(PaletteKind.Snippet, snippet.Name, snippet.Body.ReplaceLineEndings(" ⏎ "), snippet.RunImmediately ? "" : "") { Snippet = snippet };

    public static PaletteResult NewSnippet() => new(PaletteKind.NewSnippet, Loc.T("Palette.Snippets.Manage"), Loc.T("Palette.Snippets.ManageHint"), "");

    public PaletteKind Kind { get; }
    public ServerNodeViewModel? Server { get; }
    public SshDestination? Destination { get; private init; }
    public Snippet? Snippet { get; private init; }
    public string Title { get; }
    public string GroupPath { get; }
    public string Glyph { get; } = "";
    public bool HasGroup => !string.IsNullOrEmpty(GroupPath);
    public bool IsServer => Server is not null;
    public bool HasGlyph => !IsServer;
    public Brush? ColorBrush => Server?.ColorBrush;
}

/// <summary>
/// Ctrl+K: jump to any server by name, IP, tag or group. Also: "user@host" / "ssh host -p 2222" opens a terminal
/// right away (quick connect), and "&gt;" lists snippets to type into the active terminal.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public CommandPaletteViewModel(MainViewModel main) => _main = main;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteResult? _selected;

    public ObservableCollection<PaletteResult> Results { get; } = new();
    public bool HasNoResults => IsOpen && Results.Count == 0;
    public bool IsSnippetMode => Query.StartsWith('>');

    /// <summary>The hint line under the results changes with the mode.</summary>
    public string Hint => IsSnippetMode ? Loc.T("Palette.Snippets.Hint") : Loc.T("Palette.Hint2");

    public void Open(string initial = "")
    {
        Query = initial;
        Update();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    partial void OnQueryChanged(string value)
    {
        Update();
        OnPropertyChanged(nameof(IsSnippetMode));
        OnPropertyChanged(nameof(Hint));
    }

    private void Update()
    {
        Results.Clear();
        if (IsSnippetMode) UpdateSnippets();
        else UpdateServers();
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
    }

    private void UpdateServers()
    {
        foreach (var hit in ConnectionSearch.Search(_main.Services.State.Document.Root, Query, 40))
        {
            var vm = _main.FindServer(hit.Server.Id);
            if (vm is not null) Results.Add(new PaletteResult(vm, hit.GroupPath));
        }

        // Quick connect: what you'd type after "ssh". Offered first when it looks like ssh, else after the servers.
        var q = Query.Trim();
        if (q.Length == 0) return;
        var looksLikeSsh = q.Contains('@') || q.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase) || q.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
        var configHost = Services.Ssh.SshEnvironment.SharedConfig.DeclaredHosts.FirstOrDefault(h => string.Equals(h, q, StringComparison.OrdinalIgnoreCase));
        if ((looksLikeSsh || configHost is not null) && SshDestination.TryParseCommandLine(q, out var d))
        {
            var exists = Results.Any(r => r.Server is { HasTerminal: true } s && string.Equals(s.Host, d.Host, StringComparison.OrdinalIgnoreCase));
            if (!exists) Results.Insert(looksLikeSsh ? 0 : Results.Count, PaletteResult.Quick(d, configHost is not null && !looksLikeSsh));
        }
    }

    private void UpdateSnippets()
    {
        var text = Query[1..].Trim();
        var server = _main.ActiveTab?.Server;
        var groups = server?.Ancestors().Select(g => g.Model.Id).ToHashSet() ?? [];
        foreach (var s in _main.State.Document.Snippets
                     .Where(s => s.GroupId is null || groups.Contains(s.GroupId.Value))
                     .Where(s => text.Length == 0 || s.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || s.Body.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                     .OrderBy(s => s.GroupId is null ? 1 : 0).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(40))
            Results.Add(PaletteResult.ForSnippet(s));
        Results.Add(PaletteResult.NewSnippet());
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        i = Math.Clamp(i + delta, 0, Results.Count - 1);
        Selected = Results[i];
    }

    /// <summary>Enter = the server's default view; Shift+Enter = its next view (files → desktop → terminal).</summary>
    public void Accept(bool newTab, bool alternate = false)
    {
        var target = Selected;
        Close();
        if (target is null) return;
        var where = newTab ? OpenTarget.NewTab : OpenTarget.Current;
        switch (target.Kind)
        {
            case PaletteKind.QuickConnect when target.Destination is { } d:
                _main.QuickConnect(d);
                return;
            case PaletteKind.Snippet when target.Snippet is { } snippet:
                _main.RunSnippet(snippet);
                return;
            case PaletteKind.NewSnippet:
                _main.ManageSnippetsCommand.Execute(null);
                return;
        }
        if (target.Server is not { } server) return;
        if (!alternate || server.CapabilityCount < 2)
        {
            _main.OpenServer(server, where);
            return;
        }
        ServerAction[] order = [ServerAction.Files, ServerAction.Desktop, ServerAction.Terminal];
        var start = Array.IndexOf(order, server.DefaultAction);
        var next = Enumerable.Range(1, order.Length).Select(i => order[(start + i) % order.Length]).First(a => server.Server.Offers(a));
        switch (next)
        {
            case ServerAction.Desktop: _main.OpenDesktop(server, where); break;
            case ServerAction.Terminal: _main.OpenTerminal(server, where, newShell: newTab); break;
            default: _main.OpenFiles(server, where); break;
        }
    }
}
