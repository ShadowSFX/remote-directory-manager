using System.Globalization;

namespace RDMan.Core.Localization;

/// <summary>
/// All UI text, Spanish and English side by side so a key can never exist in only one language.
/// Values may contain string.Format placeholders ({0}).
/// </summary>
public static partial class Strings
{
    public static readonly IReadOnlyList<string> SupportedLanguages = ["es", "en"];

    private static readonly Dictionary<string, (string Es, string En)> Table = new(StringComparer.Ordinal);

    static Strings()
    {
        AddGeneral();
        AddTree();
        AddBrowser();
        AddDialogs();
        AddErrors();
        AddUpdates();
    }

    private static void T(string key, string es, string en) => Table.Add(key, (es, en));

    public static IEnumerable<string> Keys => Table.Keys;

    public static bool Contains(string key) => Table.ContainsKey(key);

    public static string Get(string key, string language)
    {
        if (!Table.TryGetValue(key, out var v)) return key;
        return language == "en" ? v.En : v.Es;
    }

    public static string Format(string key, string language, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key, language), args);

    /// <summary>
    /// "system" → Spanish when either the Windows display language or the regional format is Spanish
    /// (many Spanish-speaking users run an English display language with es-MX/es-ES formats), otherwise English.
    /// </summary>
    public static string ResolveLanguage(string preference, CultureInfo uiCulture, CultureInfo? regionalCulture = null)
    {
        if (preference is "es" or "en") return preference;
        if (uiCulture.TwoLetterISOLanguageName == "es") return "es";
        return regionalCulture?.TwoLetterISOLanguageName == "es" ? "es" : "en";
    }

    private static void AddGeneral()
    {
        T("App.Name", "Remote Directory Manager", "Remote Directory Manager");
        T("Common.Ok", "Aceptar", "OK");
        T("Common.Cancel", "Cancelar", "Cancel");
        T("Common.Save", "Guardar", "Save");
        T("Common.Close", "Cerrar", "Close");
        T("Common.Delete", "Eliminar", "Delete");
        T("Common.Edit", "Editar", "Edit");
        T("Common.Retry", "Reintentar", "Retry");
        T("Common.Browse", "Examinar…", "Browse…");
        T("Common.Yes", "Sí", "Yes");
        T("Common.No", "No", "No");
        T("Common.Loading", "Cargando…", "Loading…");
        T("Common.Optional", "Opcional", "Optional");
        T("Common.None", "Ninguno", "None");
        T("Common.Copied", "Copiado al portapapeles", "Copied to clipboard");

        T("Title.Search", "Buscar conexión", "Search connections");
        T("Title.Settings", "Ajustes", "Settings");
        T("Title.Minimize", "Minimizar", "Minimize");
        T("Title.Maximize", "Maximizar", "Maximize");
        T("Title.Restore", "Restaurar", "Restore");
        T("Title.Close", "Cerrar", "Close");
        T("Title.Split", "Dividir en dos paneles (F3)", "Split into two panes (F3)");
        T("Title.Unsplit", "Volver a un panel (F3)", "Back to one pane (F3)");
        T("Title.Menu", "Menú", "Menu");

        T("Menu.NewServer", "Nuevo servidor…", "New server…");
        T("Menu.NewGroup", "Nuevo grupo…", "New group…");
        T("Menu.Profiles", "Credenciales guardadas…", "Saved credentials…");
        T("Menu.ImportNetUse", "Importar conexiones de Windows…", "Import Windows connections…");
        T("Menu.ImportFile", "Importar colección .rdm…", "Import .rdm collection…");
        T("Menu.ExportFile", "Exportar colección .rdm…", "Export .rdm collection…");
        T("Menu.OpenDataFolder", "Abrir carpeta de datos", "Open data folder");
        T("Menu.CheckUpdates", "Buscar actualizaciones", "Check for updates");
        T("Menu.About", "Acerca de", "About");

        T("Status.Items", "{0} elementos", "{0} items");
        T("Status.Item", "1 elemento", "1 item");
        T("Status.Selected", "{0} seleccionados", "{0} selected");
        T("Status.SelectedSize", "{0} seleccionados · {1}", "{0} selected · {1}");
        T("Status.ConnectedAs", "Conectado como {0}", "Connected as {0}");
        T("Status.UsingCredential", "Credencial: {0}", "Credential: {0}");
        T("Status.WindowsUser", "tu usuario de Windows", "your Windows user");
        T("Status.Guest", "invitado", "guest");
        T("Status.Filtered", "{0} de {1} visibles", "{0} of {1} shown");
        T("Status.Portable", "Modo portable", "Portable mode");
        T("Status.AppData", "Datos en %APPDATA%", "Data in %APPDATA%");

        T("Toast.Saved", "Cambios guardados", "Changes saved");
        T("Toast.SaveFailed", "No se pudieron guardar los cambios: {0}", "Changes could not be saved: {0}");
        T("Toast.Recovered", "El archivo de conexiones estaba dañado; se restauró la copia de seguridad.", "The connections file was damaged; the backup copy was restored.");
        T("Toast.Corrupt", "El archivo de conexiones estaba dañado y se guardó aparte como .corrupt. Empiezas con una lista vacía.", "The connections file was damaged and was set aside as .corrupt. Starting with an empty list.");
        T("Toast.PathCopied", "Ruta copiada: {0}", "Path copied: {0}");
        T("Toast.Disconnected", "Sesión cerrada con {0}", "Session closed with {0}");
        T("Toast.Imported", "{0} conexiones importadas", "{0} connections imported");
        T("Toast.Exported", "Colección exportada", "Collection exported");
        T("Toast.NothingToPaste", "No hay archivos en el portapapeles", "There are no files on the clipboard");
        T("Toast.CannotPasteHere", "No se puede pegar aquí. Abre una carpeta primero.", "You can't paste here. Open a folder first.");
    }

    private static void AddTree()
    {
        T("Tree.ThisPc", "Este equipo", "This PC");
        T("Tree.Connections", "Conexiones", "Connections");
        T("Tree.Desktop", "Escritorio", "Desktop");
        T("Tree.Documents", "Documentos", "Documents");
        T("Tree.Downloads", "Descargas", "Downloads");
        T("Tree.Home", "Inicio", "Home");
        T("Tree.Empty.Title", "Aún no hay conexiones", "No connections yet");
        T("Tree.Empty.Body", "Agrega tu primer servidor con su IP y un nombre que reconozcas.", "Add your first server with its IP and a name you'll recognize.");
        T("Tree.Empty.Import", "O importa las que Windows ya conoce", "Or import the ones Windows already knows");

        T("Tree.Ctx.Open", "Abrir", "Open");
        T("Tree.Ctx.OpenNewTab", "Abrir en pestaña nueva", "Open in new tab");
        T("Tree.Ctx.OpenOtherPane", "Abrir en el otro panel", "Open in the other pane");
        T("Tree.Ctx.OpenExplorer", "Abrir en el Explorador de Windows", "Open in Windows Explorer");
        T("Tree.Ctx.CopyPath", "Copiar ruta UNC", "Copy UNC path");
        T("Tree.Ctx.CopyHost", "Copiar IP / host", "Copy IP / host");
        T("Tree.Ctx.Disconnect", "Cerrar sesión SMB", "Close SMB session");
        T("Tree.Ctx.NewServerHere", "Nuevo servidor aquí…", "New server here…");
        T("Tree.Ctx.NewGroupHere", "Nuevo subgrupo…", "New subgroup…");
        T("Tree.Ctx.Duplicate", "Duplicar", "Duplicate");
        T("Tree.Ctx.Properties", "Propiedades…", "Properties…");
        T("Tree.Ctx.Delete", "Eliminar", "Delete");
        T("Tree.Ctx.MoveUp", "Subir", "Move up");
        T("Tree.Ctx.MoveDown", "Bajar", "Move down");

        T("Tree.ConfirmDelete.Server", "¿Eliminar «{0}» de la lista? Los archivos del servidor no se tocan.", "Remove “{0}” from the list? Files on the server are not touched.");
        T("Tree.ConfirmDelete.Group", "¿Eliminar el grupo «{0}» y sus {1} servidores de la lista? Los archivos no se tocan.", "Remove the group “{0}” and its {1} servers from the list? Files are not touched.");
        T("Tree.CopySuffix", "{0} (copia)", "{0} (copy)");

        T("Health.Online", "En línea · {0} ms", "Online · {0} ms");
        T("Health.Offline", "Sin respuesta en el puerto 445", "No response on port 445");
        T("Health.Unknown", "Comprobando…", "Checking…");
        T("Health.Never", "Nunca", "Never");
        T("Health.OfflineShort", "sin respuesta", "offline");
    }

    private static void AddBrowser()
    {
        T("Home.Title", "Todas las conexiones", "All connections");
        T("Home.Subtitle", "{0} servidores · {1} en línea", "{0} servers · {1} online");
        T("Group.Subtitle", "{0} servidores · {1} en línea", "{0} servers · {1} online");
        T("Card.LastOpened", "Abierto {0}", "Opened {0}");
        T("Card.AllShares", "Todos los recursos", "All shares");
        T("Card.Open", "Abrir", "Open");

        T("Nav.Back", "Atrás (Alt+←)", "Back (Alt+←)");
        T("Nav.Forward", "Adelante (Alt+→)", "Forward (Alt+→)");
        T("Nav.Up", "Subir un nivel (Alt+↑)", "Up one level (Alt+↑)");
        T("Nav.Refresh", "Actualizar (F5)", "Refresh (F5)");
        T("Nav.Filter", "Filtrar esta carpeta (Ctrl+F)", "Filter this folder (Ctrl+F)");
        T("Nav.EditPath", "Escribe una ruta y pulsa Enter", "Type a path and press Enter");
        T("Nav.NewTab", "Pestaña nueva (Ctrl+T)", "New tab (Ctrl+T)");
        T("Nav.CloseTab", "Cerrar pestaña (Ctrl+W)", "Close tab (Ctrl+W)");
        T("Nav.CloseOthers", "Cerrar las demás pestañas", "Close other tabs");
        T("Nav.NewFolder", "Nueva carpeta (Ctrl+Shift+N)", "New folder (Ctrl+Shift+N)");
        T("Nav.Density", "Cambiar densidad de filas", "Change row density");
        T("Nav.OpenExplorer", "Abrir esta carpeta en el Explorador", "Open this folder in Explorer");

        T("Col.Name", "Nombre", "Name");
        T("Col.Modified", "Modificado", "Date modified");
        T("Col.Type", "Tipo", "Type");
        T("Col.Size", "Tamaño", "Size");
        T("Type.Folder", "Carpeta", "Folder");
        T("Type.Share", "Recurso compartido", "Shared folder");
        T("Type.Drive", "Unidad", "Drive");
        T("Type.DriveFree", "{0} libres", "{0} free");
        T("Type.FileExt", "Archivo {0}", "{0} file");
        T("Type.File", "Archivo", "File");

        T("Folder.Empty", "Esta carpeta está vacía", "This folder is empty");
        T("Folder.EmptyHint", "Arrastra archivos aquí o pega con Ctrl+V", "Drag files here or paste with Ctrl+V");
        T("Folder.NoMatches", "Nada coincide con «{0}»", "Nothing matches “{0}”");
        T("Folder.NoShares", "Este servidor no publica recursos compartidos visibles", "This server has no visible shared folders");
        T("Folder.Connecting", "Conectando con {0}…", "Connecting to {0}…");
        T("Folder.Reading", "Leyendo carpeta…", "Reading folder…");
        T("Folder.TooMany", "Mostrando {0} elementos", "Showing {0} items");

        T("File.Open", "Abrir", "Open");
        T("File.OpenNewTab", "Abrir en pestaña nueva", "Open in new tab");
        T("File.OpenOtherPane", "Abrir en el otro panel", "Open in the other pane");
        T("File.Cut", "Cortar", "Cut");
        T("File.Copy", "Copiar", "Copy");
        T("File.Paste", "Pegar", "Paste");
        T("File.Rename", "Cambiar nombre", "Rename");
        T("File.Delete", "Eliminar", "Delete");
        T("File.CopyPath", "Copiar ruta", "Copy path");
        T("File.CopyToOther", "Copiar al otro panel", "Copy to the other pane");
        T("File.MoveToOther", "Mover al otro panel", "Move to the other pane");
        T("File.MoreOptions", "Más opciones de Windows", "More Windows options");
        T("File.Properties", "Propiedades", "Properties");
        T("File.NewFolder", "Nueva carpeta", "New folder");
        T("File.NewFolderName", "Nueva carpeta", "New folder");
        T("File.SelectAll", "Seleccionar todo", "Select all");
        T("File.InvalidName", "Un nombre no puede contener \\ / : * ? \" < > |", "A name can't contain \\ / : * ? \" < > |");
        T("File.OpenFailed", "No se pudo abrir «{0}»: {1}", "Couldn't open “{0}”: {1}");
        T("File.OperationFailed", "La operación no se completó: {0}", "The operation didn't complete: {0}");
        T("File.NotInShare", "Abre un recurso compartido para poder pegar o crear carpetas.", "Open a shared folder to paste or create folders.");
        T("File.CannotDropOnSelf", "No puedes mover una carpeta dentro de sí misma.", "You can't move a folder into itself.");
    }

    private static void AddDialogs()
    {
        T("Server.TitleNew", "Nuevo servidor", "New server");
        T("Server.TitleEdit", "Editar servidor", "Edit server");
        T("Server.Name", "Nombre", "Name");
        T("Server.NameHint", "Cómo quieres reconocerlo, p. ej. «Producción web»", "How you'll recognize it, e.g. “Production web”");
        T("Server.Address", "Dirección", "Address");
        T("Server.AddressHint", "IP o host. También puedes pegar \\\\ip\\recurso", "IP or host. You can also paste \\\\ip\\share");
        T("Server.Share", "Recurso compartido", "Shared folder");
        T("Server.ShareHint", "Vacío = mostrar todos los recursos del servidor", "Empty = show every share on the server");
        T("Server.StartFolder", "Carpeta inicial", "Start folder");
        T("Server.Group", "Grupo", "Group");
        T("Server.Color", "Color de identificación", "Identity color");
        T("Server.Tags", "Etiquetas", "Tags");
        T("Server.TagsHint", "Separadas por comas: producción, cliente-a", "Comma separated: production, client-a");
        T("Server.Description", "Notas", "Notes");
        T("Server.Test", "Probar conexión", "Test connection");
        T("Server.Testing", "Probando…", "Testing…");
        T("Server.TestOk", "Conexión correcta · {0} recursos", "Connected · {0} shares");
        T("Server.TestOkShare", "Conexión correcta", "Connected");
        T("Server.PickShare", "Elegir recurso…", "Pick share…");
        T("Server.Validation.Name", "Escribe un nombre.", "Enter a name.");
        T("Server.Validation.Address", "La dirección no es válida. Usa una IP, un nombre de host o \\\\host\\recurso.", "The address isn't valid. Use an IP, a host name or \\\\host\\share.");
        T("Server.Validation.Port", "Windows solo conecta a SMB por el puerto 445; quita el «:puerto».", "Windows only connects to SMB on port 445; remove the “:port”.");
        T("Server.Validation.Share", "El nombre del recurso contiene caracteres no válidos.", "The share name contains invalid characters.");
        T("Server.Duplicate", "Ya tienes «{0}» apuntando a esta misma dirección.", "“{0}” already points to this address.");

        T("Group.TitleNew", "Nuevo grupo", "New group");
        T("Group.TitleEdit", "Editar grupo", "Edit group");
        T("Group.Name", "Nombre del grupo", "Group name");
        T("Group.NameHint", "Cliente, ambiente o datacenter", "Client, environment or datacenter");
        T("Group.Parent", "Dentro de", "Inside");
        T("Group.Root", "(Nivel principal)", "(Top level)");
        T("Group.CredentialsNote", "Los servidores de este grupo usarán estas credenciales salvo que definan las suyas.", "Servers in this group use these credentials unless they define their own.");

        T("Cred.Section", "Credenciales", "Credentials");
        T("Cred.Mode.Inherit", "Heredar del grupo", "Inherit from group");
        T("Cred.Mode.CurrentUser", "Mi usuario de Windows", "My Windows user");
        T("Cred.Mode.Guest", "Invitado (sin contraseña)", "Guest (no password)");
        T("Cred.Mode.Custom", "Usuario y contraseña", "Username and password");
        T("Cred.Mode.Profile", "Credencial guardada", "Saved credential");
        T("Cred.InheritsFrom", "Usará: {0} (de «{1}»)", "Will use: {0} (from “{1}”)");
        T("Cred.InheritsDefault", "Usará: tu usuario de Windows", "Will use: your Windows user");
        T("Cred.Username", "Usuario", "Username");
        T("Cred.UsernameHint", "usuario, DOMINIO\\usuario o usuario@dominio", "user, DOMAIN\\user or user@domain");
        T("Cred.Password", "Contraseña", "Password");
        T("Cred.PasswordKeep", "Deja vacío para conservar la contraseña guardada", "Leave empty to keep the saved password");
        T("Cred.PasswordUnreadable", "La contraseña guardada se cifró en otro equipo o usuario. Vuelve a escribirla.", "The saved password was encrypted on another PC or user. Type it again.");
        T("Cred.ShowPassword", "Mostrar", "Show");
        T("Cred.Profile", "Credencial", "Credential");
        T("Cred.ManageProfiles", "Administrar…", "Manage…");
        T("Cred.NoProfiles", "No tienes credenciales guardadas todavía.", "You have no saved credentials yet.");
        T("Cred.GuestNote", "Windows 11 bloquea por defecto el acceso de invitado. Si falla, usa un usuario de Samba.", "Windows 11 blocks guest access by default. If it fails, use a Samba user.");
        T("Cred.Misconfigured", "La credencial configurada no está completa; se usará tu usuario de Windows.", "The configured credential is incomplete; your Windows user will be used.");
        T("Cred.Stored", "Las contraseñas se cifran con tu cuenta de Windows (DPAPI) y solo funcionan en este equipo.", "Passwords are encrypted with your Windows account (DPAPI) and only work on this PC.");

        T("Profiles.Title", "Credenciales guardadas", "Saved credentials");
        T("Profiles.Subtitle", "Úsalas en varios servidores o grupos. Al cambiar una, cambia en todos.", "Use them across servers or groups. Change one and it changes everywhere.");
        T("Profiles.Add", "Nueva credencial", "New credential");
        T("Profiles.Name", "Nombre", "Name");
        T("Profiles.NameHint", "p. ej. «deploy en Cliente A»", "e.g. “deploy at Client A”");
        T("Profiles.InUse", "Usada por {0}", "Used by {0}");
        T("Profiles.Unused", "Sin usar", "Unused");
        T("Profiles.ConfirmDelete", "¿Eliminar la credencial «{0}»? {1} elementos pasarán a usar tu usuario de Windows.", "Delete the credential “{0}”? {1} items will fall back to your Windows user.");
        T("Profiles.Empty", "Crea una credencial para reutilizarla en varios servidores.", "Create a credential to reuse it across servers.");

        T("Settings.Appearance", "Apariencia", "Appearance");
        T("Settings.Theme", "Tema", "Theme");
        T("Settings.Theme.System", "Igual que Windows", "Same as Windows");
        T("Settings.Theme.Dark", "Oscuro", "Dark");
        T("Settings.Theme.Light", "Claro", "Light");
        T("Settings.Language", "Idioma", "Language");
        T("Settings.Language.System", "Igual que Windows", "Same as Windows");
        T("Settings.Density", "Densidad de filas", "Row density");
        T("Settings.Density.Comfortable", "Cómoda", "Comfortable");
        T("Settings.Density.Compact", "Compacta", "Compact");
        T("Settings.Browsing", "Navegación", "Browsing");
        T("Settings.ShowHiddenFiles", "Mostrar archivos ocultos", "Show hidden files");
        T("Settings.ShowHiddenShares", "Mostrar recursos ocultos ($)", "Show hidden shares ($)");
        T("Settings.RestoreTabs", "Reabrir mis pestañas al iniciar", "Reopen my tabs at startup");
        T("Settings.Connections", "Conexiones", "Connections");
        T("Settings.HealthInterval", "Comprobar estado cada", "Check status every");
        T("Settings.Seconds", "segundos", "seconds");
        T("Settings.DisconnectOnExit", "Cerrar las sesiones SMB abiertas por RDMan al salir", "Close SMB sessions opened by RDMan on exit");
        T("Settings.Updates", "Actualizaciones", "Updates");
        T("Settings.AutoUpdate", "Descargar actualizaciones automáticamente", "Download updates automatically");
        T("Settings.Version", "Versión {0}", "Version {0}");
        T("Settings.DataFolder", "Datos guardados en", "Data stored in");

        T("Palette.Placeholder", "Busca por nombre, IP, etiqueta o grupo…", "Search by name, IP, tag or group…");
        T("Palette.NoResults", "Ninguna conexión coincide", "No connection matches");
        T("Palette.Hint", "Enter abre · Ctrl+Enter abre en pestaña nueva · Esc cierra", "Enter opens · Ctrl+Enter opens in a new tab · Esc closes");

        T("Import.Title", "Importar conexiones de Windows", "Import Windows connections");
        T("Import.Subtitle", "Conexiones SMB abiertas o recordadas en este equipo (net use).", "SMB connections open or remembered on this PC (net use).");
        T("Import.None", "Windows no tiene conexiones SMB abiertas ni recordadas.", "Windows has no open or remembered SMB connections.");
        T("Import.AlreadyAdded", "Ya está en tu lista", "Already in your list");
        T("Import.Drive", "Unidad {0}", "Drive {0}");
        T("Import.Confirm", "Importar {0}", "Import {0}");
        T("Import.GroupName", "Importadas", "Imported");
        T("Import.ExportSecrets", "¿Incluir contraseñas cifradas? Solo funcionarán con tu usuario en este equipo.", "Include encrypted passwords? They only work for your user on this PC.");

        T("About.Body", "Organiza tus recursos compartidos SMB con nombres, grupos y credenciales, y navega sus archivos como en el Explorador.", "Organize your SMB shares with names, groups and credentials, and browse their files just like in Explorer.");
        T("About.Repo", "Código fuente y versiones en GitHub", "Source code and releases on GitHub");
        T("About.License", "Licencia MIT", "MIT License");

        T("Rel.JustNow", "justo ahora", "just now");
        T("Rel.Minutes", "hace {0} min", "{0} min ago");
        T("Rel.Hours", "hace {0} h", "{0} h ago");
        T("Rel.Days", "hace {0} d", "{0} d ago");
    }

    private static void AddErrors()
    {
        T("Err.AccessDenied.Title", "Acceso denegado", "Access denied");
        T("Err.AccessDenied.Detail", "El servidor rechazó la cuenta usada. Revisa las credenciales o los permisos del recurso en smb.conf.", "The server rejected the account used. Check the credentials or the share permissions in smb.conf.");
        T("Err.Logon.Title", "Usuario o contraseña incorrectos", "Wrong username or password");
        T("Err.Logon.Detail", "El servidor no aceptó estas credenciales. En Samba, el usuario debe existir en smbpasswd.", "The server didn't accept these credentials. In Samba, the user must exist in smbpasswd.");
        T("Err.Restriction.Title", "La cuenta no puede iniciar sesión", "The account can't sign in");
        T("Err.Restriction.Detail", "Windows no permite contraseñas vacías en red o la cuenta tiene restricciones. Usa un usuario con contraseña.", "Windows doesn't allow blank passwords over the network, or the account is restricted. Use a user with a password.");
        T("Err.Account.Title", "Problema con la cuenta", "Account problem");
        T("Err.Account.Detail", "La cuenta está deshabilitada, bloqueada o su contraseña expiró.", "The account is disabled, locked out, or its password expired.");
        T("Err.Conflict.Title", "Ya hay una sesión con otro usuario", "There's already a session with another user");
        T("Err.Conflict.Detail", "Windows solo permite un usuario por servidor. Cierra la sesión existente y vuelve a intentarlo. Si la abrió el Explorador, puede seguir activa hasta cerrar sesión en Windows.", "Windows only allows one user per server. Close the existing session and try again. If Explorer opened it, it may stay active until you sign out of Windows.");
        T("Err.Conflict.Action", "Cerrar sesión existente y reintentar", "Close existing session and retry");
        T("Err.GuestBlocked.Title", "Windows bloqueó el acceso de invitado", "Windows blocked guest access");
        T("Err.GuestBlocked.Detail", "Windows 11 no permite entrar como invitado por seguridad. Crea un usuario en Samba (smbpasswd -a) y úsalo aquí.", "Windows 11 doesn't allow guest sign-in for security. Create a Samba user (smbpasswd -a) and use it here.");
        T("Err.GuestBlocked.Action", "Usar usuario y contraseña", "Use username and password");
        T("Err.ShareNotFound.Title", "El recurso no existe", "The share doesn't exist");
        T("Err.ShareNotFound.Detail", "El servidor respondió, pero no tiene un recurso con ese nombre. Revisa el nombre o deja el campo vacío para ver todos.", "The server answered but has no share with that name. Check the name, or leave it empty to see them all.");
        T("Err.PathNotFound.Title", "La carpeta ya no existe", "The folder no longer exists");
        T("Err.PathNotFound.Detail", "Puede que se haya movido o eliminado.", "It may have been moved or deleted.");
        T("Err.Unreachable.Title", "No se pudo llegar al servidor", "Couldn't reach the server");
        T("Err.Unreachable.Detail", "No hubo respuesta en el puerto 445. Comprueba que el VPS esté encendido, que Samba esté activo y que el firewall o tu proveedor no bloqueen el puerto 445.", "No answer on port 445. Check the VPS is up, Samba is running, and no firewall or ISP blocks port 445.");
        T("Err.Dropped.Title", "Se perdió la conexión", "The connection dropped");
        T("Err.Dropped.Detail", "El servidor cerró la conexión. Vuelve a intentarlo.", "The server closed the connection. Try again.");
        T("Err.NoNetwork.Title", "Sin red", "No network");
        T("Err.NoNetwork.Detail", "Este equipo no tiene conexión de red.", "This PC has no network connection.");
        T("Err.NoLogonServers.Title", "Dominio no disponible", "Domain unavailable");
        T("Err.NoLogonServers.Detail", "La cuenta pertenece a un dominio que no responde. Usa SERVIDOR\\usuario para cuentas locales del servidor.", "The account belongs to a domain that isn't answering. Use SERVER\\user for local server accounts.");
        T("Err.Generic.Title", "No se pudo abrir", "Couldn't open");
        T("Err.Generic.Detail", "Windows devolvió un error inesperado.", "Windows returned an unexpected error.");
        T("Err.Code", "Código {0}: {1}", "Code {0}: {1}");
        T("Err.EditCredentials", "Editar credenciales", "Edit credentials");
    }

    private static void AddUpdates()
    {
        T("Update.Ready", "Versión {0} lista", "Version {0} ready");
        T("Update.ReadyBody", "Se instalará al reiniciar RDMan.", "It installs when RDMan restarts.");
        T("Update.Restart", "Reiniciar ahora", "Restart now");
        T("Update.Later", "Más tarde", "Later");
        T("Update.Busy", "Espera a que terminen las copias en curso.", "Wait for running copies to finish.");
        T("Update.Checking", "Buscando actualizaciones…", "Checking for updates…");
        T("Update.UpToDate", "Tienes la última versión ({0}).", "You're on the latest version ({0}).");
        T("Update.DevBuild", "Compilación de desarrollo: las actualizaciones automáticas están desactivadas.", "Development build: automatic updates are disabled.");
        T("Update.Failed", "No se pudo comprobar actualizaciones: {0}", "Couldn't check for updates: {0}");
        T("Update.NotWritable", "Hay una versión nueva ({0}) pero la carpeta de RDMan es de solo lectura. Descárgala desde GitHub.", "A new version ({0}) is available but RDMan's folder is read-only. Download it from GitHub.");
        T("Update.Download", "Abrir descarga", "Open download");
        T("Update.HashMismatch", "La descarga no coincide con su suma SHA-256 y se descartó.", "The download didn't match its SHA-256 checksum and was discarded.");
        T("Update.WhatsNew", "RDMan se actualizó a la versión {0}", "RDMan was updated to version {0}");
        T("Update.SeeNotes", "Ver novedades", "What's new");
    }
}
