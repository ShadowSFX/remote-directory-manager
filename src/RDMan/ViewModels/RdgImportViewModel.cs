using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Import;
using RDMan.Core.Models;
using RDMan.Infrastructure;

namespace RDMan.ViewModels;

/// <summary>Preview of an RDCMan (.rdg) file before it's merged into the tree.</summary>
public sealed partial class RdgImportViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    private RdgImportViewModel(MainViewModel main, string path, RdgImportResult result)
    {
        _main = main;
        FileName = Path.GetFileName(path);
        Result = result;
    }

    public RdgImportResult Result { get; }
    public string FileName { get; }
    public string GroupName => Result.Root.Name;

    public string ServersText => Result.Servers == 1 ? Loc.T("Rdg.Servers.One") : Loc.F("Rdg.Servers", Result.Servers);
    public string GroupsText => Result.Groups == 1 ? Loc.T("Rdg.Groups.One") : Loc.F("Rdg.Groups", Result.Groups);
    public bool HasProfiles => Result.Profiles.Count > 0;
    public string ProfilesText => (Result.Profiles.Count == 1 ? Loc.T("Rdg.Profiles.One") : Loc.F("Rdg.Profiles", Result.Profiles.Count));
    public bool HasSmartGroups => Result.SmartGroups.Count > 0;
    public string SmartGroupsText => (Result.SmartGroups.Count == 1 ? Loc.T("Rdg.SmartGroups.One") : Loc.F("Rdg.SmartGroups", Result.SmartGroups.Count));
    public bool HasPasswords => Result.PasswordsRecovered > 0;
    public string PasswordsText => (Result.PasswordsRecovered == 1 ? Loc.T("Rdg.Passwords.One") : Loc.F("Rdg.Passwords", Result.PasswordsRecovered));
    public bool HasLostPasswords => Result.PasswordsToReenter > 0;
    public string LostPasswordsText => (Result.PasswordsToReenter == 1 ? Loc.T("Rdg.PasswordsLost.One") : Loc.F("Rdg.PasswordsLost", Result.PasswordsToReenter));
    public bool HasNotes => Result.RulesSimplified > 0 || Result.SettingsSkipped > 0 || Result.Truncated;
    public string NotesText => Result.Truncated ? Loc.T("Rdg.Notes") + " " + Loc.T("Rdg.Truncated") : Loc.T("Rdg.Notes");

    /// <summary>RDCMan only knows remote desktops; RDMan can also offer each server's shared folders.</summary>
    [ObservableProperty] private bool _offerFiles;

    /// <summary>Reads the file; null (after telling the user) when it isn't a readable RDCMan file.</summary>
    public static RdgImportViewModel? Load(MainViewModel main, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > RdgImporter.MaxFileBytes) throw new InvalidDataException(Loc.T("Rdg.TooBig"));
            using var stream = File.OpenRead(path);
            var result = RdgImporter.Parse(stream, Path.GetFileNameWithoutExtension(path), Unprotect, main.State.Secrets.Protect);
            return new RdgImportViewModel(main, path, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or System.Xml.XmlException)
        {
            main.Toasts.Show(Loc.F("Rdg.Unreadable", Path.GetFileName(path), ex.Message), Services.ToastKind.Error);
            return null;
        }
        catch (Exception ex)
        {
            // Anything else (e.g. re-protecting a password failed): never crash the import, say what happened.
            App.Log(ex);
            main.Toasts.Show(Loc.F("Rdg.Unreadable", Path.GetFileName(path), ex.Message), Services.ToastKind.Error);
            return null;
        }
    }

    /// <summary>RDCMan protects passwords with DPAPI for the current Windows user and stores the UTF-16 text.</summary>
    private static string? Unprotect(byte[] blob)
    {
        try
        {
            var clear = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            try { return Encoding.Unicode.GetString(clear).TrimEnd('\0'); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (CryptographicException) { return null; }
    }

    public int Apply()
    {
        if (OfferFiles)
            foreach (var s in TreeIndex.AllServers(Result.Root)) s.Files = true;
        _main.State.Document.Profiles.AddRange(Result.Profiles);
        _main.AddImportedGroup(Result.Root, Result.SmartGroups);
        return Result.Servers;
    }
}
