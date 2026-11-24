using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RDMan.Infrastructure;
using RDMan.Services.Ssh;

namespace RDMan.Views;

/// <summary>Terminal-only pieces of the tab view (port forward chips).</summary>
public partial class BrowserTabView
{
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
}
