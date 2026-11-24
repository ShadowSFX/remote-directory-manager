using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RDMan.Services;

/// <summary>Service container built once at startup (the app is small enough not to need a DI framework).</summary>
public sealed class AppServices
{
    public required AppState State { get; init; }
    public required ConnectionService Connections { get; init; }
    public required HealthMonitor Health { get; init; }
    public required ShellOperations Operations { get; init; }
    public required FileClipboard Clipboard { get; init; }
    public required IconProvider Icons { get; init; }
    public required ShellContextMenu ShellMenu { get; init; }
    public required UpdateService Updates { get; init; }
    public required ToastService Toasts { get; init; }
}

public enum ToastKind { Info, Success, Error }

public sealed partial class ToastViewModel : ObservableObject
{
    public ToastViewModel(string message, ToastKind kind, string? actionText, Action? action)
    {
        Message = message;
        Kind = kind;
        ActionText = actionText;
        Action = action;
    }

    public string Message { get; }
    public ToastKind Kind { get; }
    public string? ActionText { get; }
    public Action? Action { get; }
    public bool HasAction => Action is not null && !string.IsNullOrEmpty(ActionText);

    public string Glyph => Kind switch
    {
        ToastKind.Success => "",
        ToastKind.Error => "",
        _ => "",
    };
}

/// <summary>Transient, non-blocking notifications (bottom-right). Errors stay longer.</summary>
public sealed partial class ToastService
{
    private readonly Dispatcher _dispatcher;

    public ToastService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public ObservableCollection<ToastViewModel> Items { get; } = new();

    public void Show(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Show(message, kind, actionText, action));
            return;
        }
        // Collapse identical consecutive messages instead of stacking duplicates.
        if (Items.Count > 0 && Items[^1].Message == message) return;
        var toast = new ToastViewModel(message, kind, actionText, action);
        Items.Add(toast);
        while (Items.Count > 4) Items.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 9 : action is null ? 3.5 : 7) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Items.Remove(toast);
        };
        timer.Start();
    }

    [RelayCommand]
    private void Dismiss(ToastViewModel toast) => Items.Remove(toast);

    [RelayCommand]
    private void Invoke(ToastViewModel toast)
    {
        Items.Remove(toast);
        toast.Action?.Invoke();
    }
}
