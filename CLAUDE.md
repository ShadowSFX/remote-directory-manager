# RDMan — Remote Directory Manager

Portable WPF (.NET 9) app: RDCMan-style tree of SMB servers + tabbed/split file browser built on the Windows shell engines.

## Commands
- Build: `dotnet build RDMan.sln`
- Tests (Core only, xUnit): `dotnet test tests/RDMan.Core.Tests`
- Single-file exe: `dotnet publish src/RDMan/RDMan.csproj -c Release -r win-x64 -o publish` (csproj already sets self-contained + single-file)
- Release: push tag `vX.Y.Z` → `.github/workflows/release.yml` publishes `RDMan.exe` + `RDMan.exe.sha256`. Local builds are `0.0.0-dev` and never self-update.

## Layout
- `src/RDMan.Core` — pure logic, fully tested: models (`Models/Nodes.cs`), credential inheritance (`Credentials/CredentialResolver.cs`), DPAPI (`Security/`), atomic JSON stores (`Persistence/`), UNC parsing + Win32 error mapping (`Smb/`), updater planning (`Updates/`), all UI text es/en (`Localization/Strings.cs` — add both languages in one `T(...)` call).
- `src/RDMan` — WPF app. `Interop/` hand-written Win32/COM signatures (vtable order matters), `Services/` (SMB sessions, IFileOperation, clipboard, health, updater, single instance), `ViewModels/` (CommunityToolkit.Mvvm), `Views/`, `Themes/` (Dark/Light color dictionaries + Controls/Windows styles; always use `DynamicResource` for colors).

## Conventions
- Never edit source files through Windows PowerShell 5.1 `Get-Content/Set-Content` (it corrupts UTF-8). Use the editor tools or Python.
- UI text only via `{ui:Tr Key}` / `Loc.T/F`; the Core test checks both languages and placeholders.
- Copy/move/delete/rename must go through `ShellOperations` (Windows copy engine), never `File.Copy`/`File.Move`. Only "New folder" creates directly (it needs the final name back for inline rename).
- Drop handlers must set `e.Effects` via `FileDropEffects.ReportedToSource` (RDMan performs moves itself; the OLE source must never delete).
- Commit messages: no co-author trailer.
