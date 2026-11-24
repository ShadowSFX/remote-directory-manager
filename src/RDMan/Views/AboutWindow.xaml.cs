using System.Diagnostics;
using System.Windows;
using RDMan.Services;

namespace RDMan.Views;

public partial class AboutWindow : DialogWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + UpdateService.CurrentVersionText;
    }

    private void Repo_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}") { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
