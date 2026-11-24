using System.Windows;
using System.Windows.Input;

namespace RDMan.Views;

/// <summary>Base for every modal window: shared chrome, Esc closes, Ctrl+Enter accepts.</summary>
public class DialogWindow : Window
{
    public DialogWindow()
    {
        SetResourceReference(StyleProperty, "Dialog");
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => Close()));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                // Let an open combo/menu close first.
                if (Keyboard.FocusedElement is System.Windows.Controls.ComboBox { IsDropDownOpen: true }) return;
                e.Handled = true;
                DialogResult = false;
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                Accept();
            }
        };
    }

    /// <summary>Override to validate before closing with DialogResult = true.</summary>
    protected virtual void Accept() => DialogResult = true;
}
