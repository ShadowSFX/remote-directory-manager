using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Search;

namespace RDMan.ViewModels;

public sealed class PaletteResult
{
    public PaletteResult(ServerNodeViewModel server, string groupPath)
    {
        Server = server;
        GroupPath = groupPath;
    }

    public ServerNodeViewModel Server { get; }
    public string GroupPath { get; }
    public bool HasGroup => !string.IsNullOrEmpty(GroupPath);
}

/// <summary>Ctrl+K: jump to any server by name, IP, tag or group.</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public CommandPaletteViewModel(MainViewModel main) => _main = main;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteResult? _selected;

    public ObservableCollection<PaletteResult> Results { get; } = new();
    public bool HasNoResults => IsOpen && Results.Count == 0;

    public void Open()
    {
        Query = "";
        Update();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    partial void OnQueryChanged(string value) => Update();

    private void Update()
    {
        Results.Clear();
        foreach (var hit in ConnectionSearch.Search(_main.Services.State.Document.Root, Query, 40))
        {
            var vm = _main.FindServer(hit.Server.Id);
            if (vm is not null) Results.Add(new PaletteResult(vm, hit.GroupPath));
        }
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        i = Math.Clamp(i + delta, 0, Results.Count - 1);
        Selected = Results[i];
    }

    public void Accept(bool newTab)
    {
        var target = Selected;
        Close();
        if (target is not null) _main.OpenServer(target.Server, newTab ? OpenTarget.NewTab : OpenTarget.Current);
    }
}
