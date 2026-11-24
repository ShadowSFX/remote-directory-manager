using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Bulk;
using RDMan.Core.Models;
using RDMan.Core.Rdp;
using RDMan.Core.Smb;
using RDMan.Infrastructure;

namespace RDMan.ViewModels;

/// <summary>"Add several servers": host patterns (web[1-5], {a,b}.corp…) with a live preview.</summary>
public sealed partial class BulkAddViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private IReadOnlyList<string> _newHosts = [];

    public BulkAddViewModel(MainViewModel main, GroupNodeViewModel? parent)
    {
        _main = main;
        Parents.Add(new GroupChoice(Loc.T("Group.Root"), null, 0));
        Walk(main.RootNodes, 0);
        _selectedParent = Parents.FirstOrDefault(p => ReferenceEquals(p.Group, parent)) ?? Parents[0];
        UpdatePreview();

        void Walk(IEnumerable<NodeViewModel> nodes, int depth)
        {
            foreach (var g in nodes.OfType<GroupNodeViewModel>())
            {
                Parents.Add(new GroupChoice(g.Name, g, depth + 1));
                Walk(g.Children, depth + 1);
            }
        }
    }

    public ObservableCollection<GroupChoice> Parents { get; } = new();
    [ObservableProperty] private GroupChoice _selectedParent;

    [ObservableProperty] private string _patterns = "";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private bool _offerFiles = true;
    [ObservableProperty] private bool _offerDesktop;
    [ObservableProperty] private string _desktopPort = DesktopEndpoint.DefaultPort.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewHosts = "";
    [ObservableProperty] private string? _previewNote;
    [ObservableProperty] private string? _previewError;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string _confirmText = "";

    public int Count => _newHosts.Count;

    partial void OnPatternsChanged(string value) => UpdatePreview();
    partial void OnOfferFilesChanged(bool value) => ValidationError = null;
    partial void OnOfferDesktopChanged(bool value) => ValidationError = null;

    private void UpdatePreview()
    {
        ValidationError = null;
        var result = HostPatternExpander.Expand(Patterns);
        PreviewError = result.ErrorKey is null ? null : Loc.F("Bulk.ErrorAt", result.ErrorLine, Loc.T(result.ErrorKey));
        // Hosts already in the tree (any share) are skipped: bulk add creates new servers only.
        var existing = _main.AllServers().Select(s => s.Host).ToList();
        _newHosts = result.Hosts.Where(h => !existing.Any(e => UncPath.SameHost(e, h))).ToList();
        var skipped = result.Hosts.Count - _newHosts.Count;
        PreviewTitle = _newHosts.Count switch
        {
            0 => Loc.T("Bulk.Preview.None"),
            1 => Loc.T("Bulk.Preview.One"),
            _ => Loc.F("Bulk.Preview.Many", _newHosts.Count),
        };
        const int shown = 10;
        var list = string.Join(", ", _newHosts.Take(shown));
        PreviewHosts = _newHosts.Count > shown ? Loc.F("Smart.Preview.More", list, _newHosts.Count - shown) : list;
        var notes = new List<string>();
        if (skipped > 0) notes.Add(skipped == 1 ? Loc.T("Bulk.Skipped.One") : Loc.F("Bulk.Skipped", skipped));
        if (result.Truncated) notes.Add(Loc.F("Bulk.Truncated", HostPatternExpander.MaxHosts));
        PreviewNote = notes.Count == 0 ? null : string.Join(" ", notes);
        ConfirmText = _newHosts.Count == 1 ? Loc.T("Bulk.Confirm.One") : Loc.F("Bulk.Confirm", _newHosts.Count);
        OnPropertyChanged(nameof(Count));
    }

    /// <summary>Creates the servers under the chosen group. Returns how many, or null (with a message) when something is missing.</summary>
    public int? TryApply()
    {
        if (PreviewError is not null) return Fail(PreviewError);
        if (_newHosts.Count == 0) return Fail(Loc.T("Bulk.Validation.None"));
        if (!OfferFiles && !OfferDesktop) return Fail(Loc.T("Server.Validation.NoCapability"));
        var port = DesktopEndpoint.DefaultPort;
        if (OfferDesktop && (!int.TryParse(DesktopPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || !HostPort.IsValidPort(port)))
            return Fail(Loc.T("Server.Validation.RdpPort"));
        var tags = Tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var nodes = _newHosts.Select(h => new ServerNode
        {
            Name = h,
            Host = h,
            Tags = tags.ToList(),
            Files = OfferFiles,
            Desktop = OfferDesktop ? new DesktopEndpoint { Enabled = true, Port = port } : null,
            DefaultAction = OfferFiles ? ServerAction.Files : ServerAction.Desktop,
        }).ToList();
        _main.AddServers(SelectedParent?.Group, nodes);
        return nodes.Count;

        int? Fail(string message)
        {
            ValidationError = message;
            return null;
        }
    }
}
