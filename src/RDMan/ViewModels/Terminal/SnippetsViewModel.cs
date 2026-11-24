using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Models;
using RDMan.Infrastructure;

namespace RDMan.ViewModels.Terminal;

/// <summary>Where a snippet is offered: everywhere, or only for servers under one group.</summary>
public sealed record SnippetScope(Guid? GroupId, string Label, int Depth)
{
    public override string ToString() => Label;
}

public sealed partial class SnippetItemViewModel : ObservableObject
{
    public SnippetItemViewModel(Snippet model, SnippetScope scope)
    {
        Model = model;
        _name = model.Name;
        _body = model.Body;
        _runImmediately = model.RunImmediately;
        _scope = scope;
    }

    public Snippet Model { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _body;
    [ObservableProperty] private bool _runImmediately;
    [ObservableProperty] private SnippetScope _scope;

    public string ScopeText => Scope.GroupId is null ? Loc.T("Snippets.Scope.All") : Scope.Label.Trim();

    partial void OnScopeChanged(SnippetScope value) => OnPropertyChanged(nameof(ScopeText));

    public void Apply()
    {
        Model.Name = Name.Trim();
        Model.Body = Body.Replace("\r\n", "\n");
        Model.RunImmediately = RunImmediately;
        Model.GroupId = Scope.GroupId;
    }
}

/// <summary>Saved commands: name, text with {{placeholders}}, scope and whether Enter is pressed after it.</summary>
public sealed partial class SnippetsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SnippetsViewModel(MainViewModel main)
    {
        _main = main;
        Scopes.Add(new SnippetScope(null, Loc.T("Snippets.Scope.All"), 0));
        foreach (var g in Walk(main.RootNodes, 0)) Scopes.Add(g);
        foreach (var s in main.State.Document.Snippets)
            Items.Add(new SnippetItemViewModel(s, Scopes.FirstOrDefault(x => x.GroupId == s.GroupId) ?? Scopes[0]));
        Selected = Items.FirstOrDefault();
    }

    private static IEnumerable<SnippetScope> Walk(IEnumerable<NodeViewModel> nodes, int depth)
    {
        foreach (var g in nodes.OfType<GroupNodeViewModel>())
        {
            yield return new SnippetScope(g.Model.Id, new string(' ', depth * 3) + g.Name, depth);
            foreach (var c in Walk(g.Children, depth + 1)) yield return c;
        }
    }

    public ObservableCollection<SnippetItemViewModel> Items { get; } = new();
    public ObservableCollection<SnippetScope> Scopes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SnippetItemViewModel? _selected;

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Items.Count == 0;

    [RelayCommand]
    private void Add()
    {
        var model = new Snippet { Name = Loc.T("Snippets.NewName"), Body = "" };
        var scope = _main.ActiveTab?.Server?.Parent is { } g ? Scopes.FirstOrDefault(s => s.GroupId == g.Model.Id) ?? Scopes[0] : Scopes[0];
        var item = new SnippetItemViewModel(model, scope);
        Items.Add(item);
        Selected = item;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { } item) return;
        var i = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(i, Items.Count - 1)];
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Writes the list back (empty names get a default, bodies keep their text).</summary>
    public void Save()
    {
        var list = new List<Snippet>();
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Name) && string.IsNullOrWhiteSpace(item.Body)) continue;
            if (string.IsNullOrWhiteSpace(item.Name)) item.Name = Loc.T("Snippets.NewName");
            item.Apply();
            list.Add(item.Model);
        }
        _main.State.Document.Snippets = list;
        _main.State.MarkDirty();
    }
}
