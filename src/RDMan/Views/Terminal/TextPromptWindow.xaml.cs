using System.Windows;

namespace RDMan.Views;

/// <summary>One line of text (a snippet's {{placeholder}}).</summary>
public partial class TextPromptWindow : DialogWindow
{
    public TextPromptWindow(string title, string body, string label, string okText, string initial = "")
    {
        InitializeComponent();
        Title = title;
        BodyText.Text = body;
        BodyText.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        LabelText.Text = label;
        OkButton.Content = okText;
        ValueBox.Text = initial;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    protected override void Accept() => DialogResult = true;

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();
}
