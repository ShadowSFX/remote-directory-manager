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

## Remote desktop
- Sessions: `ViewModels/RdpSessionViewModel.cs` wraps RoyalApps `RdpControl` (MsRdpEx legacy COM interop: `MsRdpExComInterop=Legacy`, `MsRdpExGeneratedWinForms=false` in the csproj) inside a `WindowsFormsHost`.
- The ActiveX dies if re-parented. Every tab's host lives in the window-level `SessionOverlay` (MainWindow) bound to `MainViewModel.LiveTabs`, which is append/remove only (never Move/Clear/sort/CollectionView, never re-template the overlay); `PositionSessions` places each host over its pane's content area. Moving tabs or rebuilding the pane grid must never touch that collection.
- A session HWND paints over WPF content (airspace). Anything that must appear above a session (palette, toasts) lives in a `Popup`.
- Undocked desktops (`Views/SessionWindow`, `MainViewModel.Detached.cs`) get a new session in their own window; moving between tab and window always reconnects. One-session-per-server checks and "live sessions" must use `MainViewModel.AllSessions` (tabs + windows), not `AllTabs`.
- The overlay hides background sessions with `Visibility.Hidden` (not Collapsed) so they keep their pane's size; `Connect` waits for a real control size (0x0 makes the server pick 200x200).
- RoyalApps raises OnConnected/OnDisconnected with the inner client as sender: bind handlers per control (see RdpSessionViewModel.Connect).
- Verify RDP changes against a real Windows server (unit tests can't cover the ActiveX).

## Pane grid
- `RDMan.Core/Layout/PaneLayout.cs` is the split tree (tested); `MainViewModel.Panes.cs` owns panes, MRU "other pane", split/close/move; `Views/PaneLayoutHost.cs` renders it and caches one `PaneView` per pane.
- Drag & drop of tabs/tree nodes: `Views/PaneDragSession.cs` + `MainWindow.PaneDrag.cs`. The drop overlay Popup is the drop target (works over sessions); actions run after `DoDragDrop` returns.

## Conventions
- Never edit source files through Windows PowerShell 5.1 `Get-Content/Set-Content` (it corrupts UTF-8). Use the editor tools or Python.
- UI text only via `{ui:Tr Key}` / `Loc.T/F`; the Core test checks both languages and placeholders.
- Copy/move/delete/rename must go through `ShellOperations` (Windows copy engine), never `File.Copy`/`File.Move`. Only "New folder" creates directly (it needs the final name back for inline rename).
- Drop handlers must set `e.Effects` via `FileDropEffects.ReportedToSource` (RDMan performs moves itself; the OLE source must never delete).
- Commit messages: no co-author trailer.
