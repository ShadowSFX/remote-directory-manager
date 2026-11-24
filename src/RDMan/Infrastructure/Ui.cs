using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RDMan.Infrastructure;

/// <summary>Attached properties used by the control templates.</summary>
public static class Ui
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetHint(DependencyObject o) => (string?)o.GetValue(HintProperty);
    public static void SetHint(DependencyObject o, string? v) => o.SetValue(HintProperty, v);

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius v) => o.SetValue(CornerRadiusProperty, v);

    /// <summary>Visual "pressed/active" state for command buttons that reflect a view-model flag (split view, filter).</summary>
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    public static bool GetIsOn(DependencyObject o) => (bool)o.GetValue(IsOnProperty);
    public static void SetIsOn(DependencyObject o, bool v) => o.SetValue(IsOnProperty, v);

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetGlyph(DependencyObject o) => (string?)o.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject o, string? v) => o.SetValue(GlyphProperty, v);

    /// <summary>Selects all text when a TextBox receives keyboard focus (path bar, filter, palette).</summary>
    public static readonly DependencyProperty SelectAllOnFocusProperty = DependencyProperty.RegisterAttached(
        "SelectAllOnFocus", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnSelectAllOnFocusChanged));

    public static bool GetSelectAllOnFocus(DependencyObject o) => (bool)o.GetValue(SelectAllOnFocusProperty);
    public static void SetSelectAllOnFocus(DependencyObject o, bool v) => o.SetValue(SelectAllOnFocusProperty, v);

    private static void OnSelectAllOnFocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.GotKeyboardFocus -= SelectAll;
        tb.PreviewMouseLeftButtonDown -= FocusOnClick;
        if ((bool)e.NewValue)
        {
            tb.GotKeyboardFocus += SelectAll;
            tb.PreviewMouseLeftButtonDown += FocusOnClick;
        }
    }

    private static void SelectAll(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

    private static void FocusOnClick(object sender, MouseButtonEventArgs e)
    {
        var tb = (TextBox)sender;
        if (tb.IsKeyboardFocusWithin) return;
        e.Handled = true;
        tb.Focus();
    }

    /// <summary>Moves focus into a control when it becomes visible (inline rename box, dialogs).</summary>
    public static readonly DependencyProperty FocusOnLoadProperty = DependencyProperty.RegisterAttached(
        "FocusOnLoad", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnFocusOnLoadChanged));

    public static bool GetFocusOnLoad(DependencyObject o) => (bool)o.GetValue(FocusOnLoadProperty);
    public static void SetFocusOnLoad(DependencyObject o, bool v) => o.SetValue(FocusOnLoadProperty, v);

    private static void OnFocusOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;
        fe.Loaded += (_, _) => fe.Dispatcher.BeginInvoke(() =>
        {
            fe.Focus();
            Keyboard.Focus(fe);
        }, System.Windows.Threading.DispatcherPriority.Input);
        fe.IsVisibleChanged += (_, ev) =>
        {
            if ((bool)ev.NewValue)
                fe.Dispatcher.BeginInvoke(() => { fe.Focus(); Keyboard.Focus(fe); }, System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    public static T? FindDescendant<T>(DependencyObject? d) where T : DependencyObject
    {
        if (d is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is T t) return t;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
