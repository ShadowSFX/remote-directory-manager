using System.Windows;
using RDMan.Infrastructure;
using RDMan.ViewModels;

namespace RDMan.Views;

/// <summary>Opens the app's modal windows with the right owner, theme and backdrop.</summary>
public static class Dialogs
{
    private static Window? DefaultOwner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static bool? Show(Window dialog, Window? owner)
    {
        owner ??= DefaultOwner;
        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.SourceInitialized += (_, _) => WindowEffects.Apply(dialog, ThemeManager.Instance.IsDark);
        return dialog.ShowDialog();
    }

    public static bool? ShowServerEditor(Window? owner, ServerEditorViewModel vm) => Show(new NodeEditorWindow(vm), owner);

    public static bool? ShowGroupEditor(Window? owner, GroupEditorViewModel vm) => Show(new NodeEditorWindow(vm), owner);

    public static void ShowSettings(Window? owner, SettingsViewModel vm) => Show(new SettingsWindow(vm), owner);

    public static bool? ShowProfiles(Window? owner, ProfilesViewModel vm) => Show(new ProfilesWindow(vm), owner);

    public static bool? ShowSmartGroupEditor(Window? owner, SmartGroupEditorViewModel vm) => Show(new SmartGroupWindow(vm), owner);

    public static bool? ShowRdgImport(Window? owner, RdgImportViewModel vm) => Show(new RdgImportWindow(vm), owner);

        public static int ShowBulkAdd(Window? owner, BulkAddViewModel vm)
    {
        var w = new BulkAddWindow(vm);
        return Show(w, owner) == true ? w.Added : 0;
    }

    public static bool? ShowImport(Window? owner, ImportViewModel vm) => Show(new ImportWindow(vm), owner);

    public static void ShowAbout(Window? owner) => Show(new AboutWindow(), owner);

    public static bool Confirm(Window? owner, string title, string message, string confirmText, bool danger, string? cancelText = null)
        => Show(new MessageWindow(title, message, confirmText, cancelText ?? Loc.T("Common.Cancel"), danger), owner) == true;
}
