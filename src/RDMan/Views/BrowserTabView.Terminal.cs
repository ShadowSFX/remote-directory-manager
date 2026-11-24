using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RDMan.Infrastructure;
using RDMan.Services.Ssh;
using RDMan.ViewModels;
using RDMan.ViewModels.Terminal;

namespace RDMan.Views;

/// <summary>Terminal-only pieces of the tab view: port forward chips and the SFTP side panel.</summary>
public partial class BrowserTabView
{
    private SshSessionViewModel? _watchedTerminal;

    /// <summary>The element a session's native surface is placed over (the content area minus the SFTP panel).</summary>
    public FrameworkElement SessionArea => SessionSurface;

    private void ForwardChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ForwardViewModel forward } button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem
        {
            Header = Loc.T(forward.IsListening ? "Term.Forward.Stop" : "Term.Forward.Start"),
            Tag = forward.IsListening ? "" : "",
            Command = forward.ToggleCommand,
        });
        menu.Items.Add(new MenuItem { Header = Loc.F("Term.Forward.Copy", forward.LocalAddress), Tag = "", Command = forward.CopyAddressCommand });
        if (forward.LooksLikeWeb)
            menu.Items.Add(new MenuItem { Header = Loc.T("Term.Forward.Open"), Tag = "", Command = forward.OpenInBrowserCommand, IsEnabled = forward.IsListening });
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Follows the shown tab's terminal session to open/close the SFTP panel with it.</summary>
    private void WatchTerminal()
    {
        var next = (DataContext as BrowserTabViewModel) is { IsTerminal: true } tab ? tab.TerminalSession : null;
        if (!ReferenceEquals(next, _watchedTerminal))
        {
            if (_watchedTerminal is not null) _watchedTerminal.PropertyChanged -= OnTerminalChanged;
            _watchedTerminal = next;
            if (next is not null) next.PropertyChanged += OnTerminalChanged;
        }
        UpdateSftpPanel();
    }

    private void OnTerminalChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SshSessionViewModel.IsSftpOpen) or nameof(SshSessionViewModel.Sftp)) UpdateSftpPanel();
    }

    private void UpdateSftpPanel()
    {
        var open = _watchedTerminal is { IsSftpOpen: true, Sftp: not null };
        var width = App.Services.State.Settings.Terminal.SftpPanelWidth;
        // Never wider than what leaves the terminal a usable width.
        if (ContentArea.ActualWidth > 0) width = Math.Min(width, Math.Max(220, ContentArea.ActualWidth - 320));
        SftpColumn.Width = open ? new GridLength(width) : new GridLength(0);
        SftpSplitterColumn.Width = open ? new GridLength(5) : new GridLength(0);
        SftpSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SftpPanelView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SftpSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var settings = App.Services.State.Settings;
        settings.Terminal.SftpPanelWidth = SftpColumn.ActualWidth;
        settings.Terminal.Normalize();
        App.Services.State.MarkSettingsDirty();
    }

    private void SftpPanel_CloseRequested(object? sender, EventArgs e)
    {
        if (_watchedTerminal is { IsSftpOpen: true } t) t.ToggleSftp();
    }
}
