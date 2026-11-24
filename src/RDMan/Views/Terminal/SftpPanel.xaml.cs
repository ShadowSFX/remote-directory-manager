using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RDMan.Infrastructure;
using RDMan.ViewModels.Terminal;

namespace RDMan.Views;

/// <summary>SFTP side panel next to a terminal: browse, drop files to upload, double-click to edit.</summary>
public partial class SftpPanel : UserControl
{
    public SftpPanel() => InitializeComponent();

    private SftpBrowserViewModel? Vm => DataContext as SftpBrowserViewModel;

    /// <summary>Raised by the close button; the tab view hides the panel.</summary>
    public event EventHandler? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.FollowTerminal = !vm.FollowTerminal;
    }

    private void Hidden_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.ShowHidden = !vm.ShowHidden;
    }

    private void Path_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm) return;
        vm.GoToPathCommand.Execute(null);
        e.Handled = true;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: SftpEntry entry })
            Vm?.OpenCommand.Execute(entry);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter: vm.OpenCommand.Execute(vm.Selected); e.Handled = true; break;
            case Key.Back: vm.UpCommand.Execute(null); e.Handled = true; break;
            case Key.Delete: vm.DeleteCommand.Execute(vm.Selected); e.Handled = true; break;
            case Key.F2: vm.RenameCommand.Execute(vm.Selected); e.Handled = true; break;
            case Key.F5: vm.RefreshCommand.Execute(null); e.Handled = true; break;
        }
    }

    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Ui.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item) item.IsSelected = true;
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Vm is not { } vm) return;
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        MenuItem Item(string key, string glyph, System.Windows.Input.ICommand cmd, object? param, string? gesture = null)
            => new() { Header = Loc.T(key), Tag = glyph, Command = cmd, CommandParameter = param, InputGestureText = gesture ?? "" };
        if (vm.Selected is { } entry)
        {
            if (entry.IsDirectory) menu.Items.Add(Item("Sftp.Open", "", vm.OpenCommand, entry, "Enter"));
            else
            {
                menu.Items.Add(Item("Sftp.Edit", "", vm.EditCommand, entry, "Enter"));
                menu.Items.Add(Item("Sftp.Download", "", vm.DownloadCommand, entry));
            }
            menu.Items.Add(Item("Sftp.Rename", "", vm.RenameCommand, entry, "F2"));
            menu.Items.Add(Item("Common.Delete", "", vm.DeleteCommand, entry, "Supr"));
            menu.Items.Add(Item("Sftp.CopyPath", "", vm.CopyPathCommand, entry));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Sftp.Upload", "", vm.UploadPickedCommand, null));
        menu.Items.Add(Item("Sftp.NewFolder", "", vm.NewFolderCommand, null));
        menu.Items.Add(Item("Sftp.CdHere", "", vm.CdHereCommand, null));
    }

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        // Upload: a copy as far as the source is concerned (RDMan never asks the source to delete anything).
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Panel_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Vm is not { } vm || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Effects = DragDropEffects.Copy;
        await vm.UploadAsync(files);
    }
}
