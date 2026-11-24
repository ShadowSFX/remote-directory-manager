using System.Windows;
using System.Windows.Threading;

namespace RDMan.Views;

/// <summary>
/// A drag that can rearrange the editor grid: a tab from a tab strip, or a server/group from the tree. While it runs,
/// the window shows a drop overlay above the panes (also above remote-desktop windows). The drop only records what
/// to do; it runs after <see cref="DragDrop.DoDragDrop"/> returns — moving a tab destroys the element the drag
/// started from, and opening a desktop may show a dialog, neither of which belongs inside the OLE drag loop.
/// </summary>
public static class PaneDragSession
{
    /// <summary>Private clipboard format for tab drags: Explorer and other apps don't understand it and refuse the drop.</summary>
    public const string TabFormat = "RDMan.Tab";

    /// <summary>The dragged tab or tree node while a drag is in progress.</summary>
    public static object? Payload { get; private set; }

    /// <summary>What the drop decided; executed once the drag loop has ended.</summary>
    public static Action? Pending { get; set; }

    public static event EventHandler? Started;
    public static event EventHandler? Ended;

    public static DragDropEffects Run(DependencyObject source, object payload, DataObject data, DragDropEffects allowed = DragDropEffects.Move)
    {
        Payload = payload;
        Pending = null;
        Started?.Invoke(null, EventArgs.Empty);
        try
        {
            return DragDrop.DoDragDrop(source, data, allowed);
        }
        finally
        {
            Ended?.Invoke(null, EventArgs.Empty);
            var pending = Pending;
            Payload = null;
            Pending = null;
            if (pending is not null) Dispatcher.CurrentDispatcher.BeginInvoke(pending, DispatcherPriority.Input);
        }
    }
}
