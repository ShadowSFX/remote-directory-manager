using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Models;
using RDMan.Core.Search;
using RDMan.Infrastructure;

namespace RDMan.ViewModels;

public sealed record SmartOption(string Key, string Label);

/// <summary>One rule row: field · operator · value.</summary>
public sealed partial class SmartRuleRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public SmartRuleRowViewModel(SmartRule rule, Action changed)
    {
        _changed = changed;
        _field = rule.Field;
        _op = rule.Op;
        _value = rule.Value;
    }

    [ObservableProperty] private string _field;
    [ObservableProperty] private string _op;
    [ObservableProperty] private string _value;

    partial void OnFieldChanged(string value) => _changed();
    partial void OnOpChanged(string value) => _changed();
    partial void OnValueChanged(string value) => _changed();

    public SmartRule ToRule() => new() { Field = Field, Op = Op, Value = Value.Trim() };
}

/// <summary>Create / edit a smart group with a live preview of the servers it matches.</summary>
public sealed partial class SmartGroupEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SmartGroup _model;

    public SmartGroupEditorViewModel(MainViewModel main, SmartGroup model, bool isNew)
    {
        _main = main;
        _model = model;
        IsNew = isNew;
        _name = model.Name;
        _matchAll = model.Match != SmartMatch.Any;
        Scopes.Add(new GroupChoice(Loc.T("Smart.ScopeAll"), null, 0));
        AddScopes(main.RootNodes, 0);
        _selectedScope = Scopes.FirstOrDefault(s => s.Group?.Model.Id == model.ScopeId) ?? Scopes[0];
        foreach (var r in model.Rules) Rules.Add(new SmartRuleRowViewModel(r, UpdatePreview));
        if (Rules.Count == 0) Rules.Add(new SmartRuleRowViewModel(new SmartRule(), UpdatePreview));
        Rules.CollectionChanged += (_, _) => { UpdatePreview(); OnPropertyChanged(nameof(CanRemoveRule)); };
        UpdatePreview();
    }

    public bool IsNew { get; }
    public string Title => IsNew ? Loc.T("Smart.TitleNew") : Loc.T("Smart.TitleEdit");
    public bool Deleted { get; private set; }

    public IReadOnlyList<SmartOption> Fields { get; } = SmartField.All.Select(f => new SmartOption(f, Loc.T("Smart.Field." + f))).ToList();
    public IReadOnlyList<SmartOption> Ops { get; } = SmartOp.All.Select(o => new SmartOption(o, Loc.T("Smart.Op." + o))).ToList();

    public ObservableCollection<SmartRuleRowViewModel> Rules { get; } = new();

    /// <summary>Where the group searches: everything, or one group (and its subgroups).</summary>
    public ObservableCollection<GroupChoice> Scopes { get; } = new();
    [ObservableProperty] private GroupChoice _selectedScope;
    partial void OnSelectedScopeChanged(GroupChoice value) => UpdatePreview();

    private void AddScopes(IEnumerable<NodeViewModel> nodes, int depth)
    {
        foreach (var g in nodes.OfType<GroupNodeViewModel>())
        {
            Scopes.Add(new GroupChoice(g.Name, g, depth + 1));
            AddScopes(g.Children, depth + 1);
        }
    }
    public bool CanAddRule => Rules.Count < SmartGroup.MaxRules;
    public bool CanRemoveRule => Rules.Count > 1;

    [ObservableProperty] private string _name;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MatchAny))] private bool _matchAll;
    public bool MatchAny => !MatchAll;
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewNames = "";
    [ObservableProperty] private string? _validationError;

    partial void OnMatchAllChanged(bool value) => UpdatePreview();
    partial void OnNameChanged(string value) => ValidationError = null;

    [RelayCommand] private void SetMatchAll() => MatchAll = true;
    [RelayCommand] private void SetMatchAny() => MatchAll = false;

    [RelayCommand]
    private void AddRule()
    {
        if (!CanAddRule) return;
        Rules.Add(new SmartRuleRowViewModel(new SmartRule(), UpdatePreview));
        OnPropertyChanged(nameof(CanAddRule));
    }

    [RelayCommand]
    private void RemoveRule(SmartRuleRowViewModel? row)
    {
        if (row is null || Rules.Count <= 1) return;
        Rules.Remove(row);
        OnPropertyChanged(nameof(CanAddRule));
    }

    [RelayCommand]
    private void Delete()
    {
        Deleted = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? RequestClose;

    private SmartGroup Draft() => new()
    {
        Name = Name.Trim(),
        Match = MatchAll ? SmartMatch.All : SmartMatch.Any,
        Rules = Rules.Select(r => r.ToRule()).ToList(),
        ScopeId = SelectedScope?.Group?.Model.Id,
    };

    private void UpdatePreview()
    {
        ValidationError = null;
        var hits = SmartGroupEvaluator.Evaluate(_main.State.Document.Root, Draft());
        PreviewTitle = hits.Count switch
        {
            0 => Loc.T("Smart.Preview.None"),
            1 => Loc.T("Smart.Preview.One"),
            _ => Loc.F("Smart.Preview.Many", hits.Count),
        };
        const int shown = 6;
        var names = string.Join(", ", hits.Take(shown).Select(h => h.Server.Name));
        PreviewNames = hits.Count > shown ? Loc.F("Smart.Preview.More", names, hits.Count - shown) : names;
    }

    public bool TryApply()
    {
        var draft = Draft();
        if (draft.Name.Length == 0)
        {
            ValidationError = Loc.T("Smart.Validation.Name");
            return false;
        }
        if (draft.Rules.All(r => r.Value.Length == 0))
        {
            ValidationError = Loc.T("Smart.Validation.Rule");
            return false;
        }
        _model.Name = draft.Name;
        _model.Match = draft.Match;
        _model.ScopeId = draft.ScopeId;
        _model.Rules = draft.Rules.Where(r => r.Value.Length > 0).ToList();
        return true;
    }
}
