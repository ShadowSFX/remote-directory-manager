namespace RDMan.Core.Localization;

public static partial class Strings
{
    /// <summary>SSH terminal: tabs, connection states, errors, editor, settings, SFTP, snippets.</summary>
    private static void AddTerminal()
    {
        // Errors (SshErrorTranslator)
        T("SshErr.HostNotFound.Title", "No se encontró el servidor", "Server not found");
        T("SshErr.HostNotFound.Detail", "El nombre no se resuelve en la red. Revisa la dirección del servidor o su entrada en ~/.ssh/config.", "The name doesn't resolve on the network. Check the server's address or its entry in ~/.ssh/config.");
        T("SshErr.Refused.Title", "El servidor rechazó la conexión", "The server refused the connection");
        T("SshErr.Refused.Detail", "Nada escucha en ese puerto. Comprueba que SSH esté activo y que el puerto sea el correcto.", "Nothing is listening on that port. Check that SSH is running and the port is right.");
        T("SshErr.Timeout.Title", "El servidor no responde", "The server isn't responding");
        T("SshErr.Timeout.Detail", "La conexión tardó demasiado. Puede estar apagado, detrás de un firewall o el puerto filtrado.", "The connection took too long. It may be off, behind a firewall, or the port may be filtered.");
        T("SshErr.Unreachable.Title", "No hay ruta hacia el servidor", "No route to the server");
        T("SshErr.Unreachable.Detail", "La red no llega a esa dirección. Revisa la VPN o la conexión de red.", "The network can't reach that address. Check your VPN or network connection.");
        T("SshErr.AuthFailed.Title", "El servidor no aceptó el inicio de sesión", "The server didn't accept the sign-in");
        T("SshErr.AuthFailed.Detail", "Probó {0} y ninguno funcionó. Revisa el usuario, la llave o la contraseña. En AWS el usuario suele ser ubuntu, ec2-user o admin.", "It tried {0} and none worked. Check the user, key or password. On AWS the user is usually ubuntu, ec2-user or admin.");
        T("SshErr.NoUser.Title", "Falta el usuario", "User missing");
        T("SshErr.NoUser.Detail", "Indica con qué usuario entrar en este servidor.", "Enter the user to sign in to this server with.");
        T("SshErr.KeyUnreadable.Title", "No se pudo leer la llave", "The key couldn't be read");
        T("SshErr.KeyUnreadable.Detail", "La llave privada falta, está dañada o tiene un formato no compatible. Vuelve a importarla.", "The private key is missing, damaged or in an unsupported format. Import it again.");
        T("SshErr.KeyPassphrase.Title", "La frase de la llave no es correcta", "Wrong key passphrase");
        T("SshErr.KeyPassphrase.Detail", "La llave está cifrada y la frase indicada no la abre.", "The key is encrypted and the passphrase given doesn't open it.");
        T("SshErr.HostKeyChanged.Title", "La identidad del servidor cambió", "The server's identity changed");
        T("SshErr.HostKeyChanged.Detail", "La llave que presenta no coincide con la guardada en known_hosts. Puede ser una reinstalación o alguien interceptando la conexión. No te conectes hasta confirmarlo con quien administra el servidor.", "The key it presents doesn't match the one saved in known_hosts. It may have been reinstalled, or someone may be intercepting the connection. Don't connect until whoever runs the server confirms it.");
        T("SshErr.HostKeyRevoked.Title", "La llave del servidor está revocada", "The server's key is revoked");
        T("SshErr.HostKeyRevoked.Detail", "known_hosts marca esta llave como @revoked. RDMan no se conectará con ella.", "known_hosts marks this key as @revoked. RDMan won't connect with it.");
        T("SshErr.HostKeyRejected.Title", "Conexión cancelada", "Connection canceled");
        T("SshErr.HostKeyRejected.Detail", "No se confió en la identidad del servidor.", "The server's identity wasn't trusted.");
        T("SshErr.Protocol.Title", "Error de protocolo SSH", "SSH protocol error");
        T("SshErr.Protocol.Detail", "El servidor y RDMan no acordaron cómo cifrar la conexión: {0}", "The server and RDMan couldn't agree on how to encrypt the connection: {0}");
        T("SshErr.ConnectionLost.Title", "Se perdió la conexión", "Connection lost");
        T("SshErr.ConnectionLost.Detail", "El servidor dejó de responder. El historial de la terminal se conserva.", "The server stopped responding. The terminal history is kept.");
        T("SshErr.ServerClosed.Title", "Sesión terminada", "Session ended");
        T("SshErr.ServerClosed.Detail", "El servidor cerró la sesión.", "The server closed the session.");
        T("SshErr.ShellRejected.Title", "El servidor no abrió una terminal", "The server didn't open a terminal");
        T("SshErr.ShellRejected.Detail", "Inició sesión, pero rechazó la terminal interactiva (cuentas solo SFTP o shell deshabilitado).", "Sign-in worked, but it refused an interactive terminal (SFTP-only accounts or a disabled shell).");
        T("SshErr.JumpFailed.Title", "Falló el servidor intermedio", "The jump host failed");
        T("SshErr.JumpFailed.Detail", "No se pudo pasar por {0}: {1}", "Couldn't go through {0}: {1}");
        T("SshErr.JumpInvalid.Title", "ProxyJump no válido", "Invalid ProxyJump");
        T("SshErr.JumpInvalid.Detail", "Usa el formato usuario@servidor:puerto, separado por comas para varios saltos.", "Use user@host:port, separated by commas for several hops.");
        T("SshErr.AgentUnavailable.Title", "No hay agente SSH", "No SSH agent");
        T("SshErr.AgentUnavailable.Detail", "Ni el agente de OpenSSH de Windows ni Pageant están en ejecución, o no tienen llaves cargadas.", "Neither the Windows OpenSSH agent nor Pageant is running, or they have no keys loaded.");
        T("SshErr.WebViewMissing.Title", "Falta WebView2", "WebView2 runtime required");
        T("SshErr.WebViewMissing.Detail", "La terminal usa Microsoft Edge WebView2, que no está instalado en este equipo.", "The terminal uses Microsoft Edge WebView2, which isn't installed on this computer.");
        T("SshErr.Unknown.Title", "No se pudo conectar", "Couldn't connect");
        T("SshErr.Unknown.Detail", "{0}", "{0}");

        AddTerminalSession();
        AddTerminalEditor();
    }

    private static void AddTerminalEditor()
    {
        T("Server.Section.Terminal", "Terminal SSH", "SSH terminal");
        T("Server.TestSshOk", "SSH (puerto {0}) responde en {1} ms", "SSH (port {0}) answers in {1} ms");
        T("Server.TestSshFail", "SSH (puerto {0}) no responde", "SSH (port {0}) doesn't answer");
        T("Term.Editor.Enable", "Abrir una terminal SSH en este servidor", "Open an SSH terminal on this server");
        T("Term.Editor.Port", "Puerto", "Port");
        T("Term.Editor.UseConfig", "Usar ~/.ssh/config, como el comando ssh", "Use ~/.ssh/config, like the ssh command");
        T("Term.Editor.Preview.Config", "Según ~/.ssh/config (Host {0}): {1}", "From ~/.ssh/config (Host {0}): {1}");
        T("Term.Editor.Preview.Plain", "Se conectará a {0}", "Will connect to {0}");
        T("Term.Editor.Preview.Key", "llave {0}", "key {0}");
        T("Term.Editor.Preview.Jump", "a través de {0}", "through {0}");
        T("Term.Editor.GroupNote", "Lo que definas aquí lo heredan las terminales de los servidores de este grupo (por ejemplo, la llave .pem de una cuenta de AWS).", "What you set here is inherited by the terminals of this group's servers (for example, the .pem key of an AWS account).");
        T("Term.Editor.Auth.Section", "Inicio de sesión", "Sign-in");
        T("Term.Editor.Auth.Inherit", "Heredar del grupo", "Inherit from the group");
        T("Term.Editor.Auth.InheritRoot", "Automático (nada configurado en los grupos)", "Automatic (nothing set in the groups)");
        T("Term.Editor.Auth.InheritFrom", "{0}, definido en «{1}»", "{0}, set in “{1}”");
        T("Term.Editor.Auth.Auto", "Automático", "Automatic");
        T("Term.Editor.Auth.AutoNote", "Como el comando ssh: el agente SSH, las llaves de ~/.ssh y de la configuración, y si hace falta, la contraseña.", "Like the ssh command: the SSH agent, the keys in ~/.ssh and the config, and a password if needed.");
        T("Term.Editor.Auth.Key", "Llave privada guardada en RDMan", "Private key saved in RDMan");
        T("Term.Editor.Auth.KeyNote", "Por ejemplo, el .pem de AWS. Se guarda cifrado; el archivo original ya no hace falta.", "For example, the AWS .pem. Stored encrypted; the original file is no longer needed.");
        T("Term.Editor.Auth.Describe.Key", "llave {0}", "key {0}");
        T("Term.Editor.Auth.Password", "Contraseña", "Password");
        T("Term.Editor.Auth.Agent", "Solo el agente SSH", "SSH agent only");
        T("Term.Editor.Auth.AgentNote", "Las llaves cargadas en el agente de OpenSSH de Windows o en Pageant.", "The keys loaded in the Windows OpenSSH agent or Pageant.");
        T("Term.Editor.Auth.Stored", "Contraseñas y llaves se guardan cifradas para tu usuario de Windows y no se exportan salvo que lo pidas.", "Passwords and keys are stored encrypted for your Windows user and aren't exported unless you ask.");
        T("Term.Editor.User.Hint", "Del ~/.ssh/config, o se preguntará", "From ~/.ssh/config, or asked when connecting");
        T("Term.Editor.InheritedHint", "{0} (heredado de «{1}»)", "{0} (inherited from “{1}”)");
        T("Term.Editor.Key.None", "Aún no hay llaves en RDMan.", "No keys in RDMan yet.");
        T("Term.Editor.Key.Import", "Importar llave…", "Import key…");
        T("Term.Editor.Key.ImportTitle", "Elegir una llave privada", "Choose a private key");
        T("Term.Editor.Key.Filter", "Llaves privadas (*.pem;*.key;*.ppk;id_*)|*.pem;*.key;*.ppk;id_*|Todos los archivos (*.*)|*.*", "Private keys (*.pem;*.key;*.ppk;id_*)|*.pem;*.key;*.ppk;id_*|All files (*.*)|*.*");
        T("Term.Editor.Key.Imported", "Llave {0} importada ({1})", "Key {0} imported ({1})");
        T("Term.Editor.Key.AlreadyImported", "Esa llave ya estaba en RDMan como {0}", "That key was already in RDMan as {0}");
        T("Term.Editor.Key.RememberPassphrase", "Recordar la frase (cifrada)", "Remember the passphrase (encrypted)");
        T("Term.Editor.Key.StoredNote", "Si no la recuerdas, RDMan la pedirá la primera vez que la uses en cada sesión.", "If you don't, RDMan asks for it the first time you use the key in each session.");
        T("Term.Editor.Forward.Section", "Reenvío de puertos", "Port forwarding");
        T("Term.Editor.Forward.Add", "Añadir", "Add");
        T("Term.Editor.Forward.Note", "Se abren con la terminal. Local: un puerto de este equipo llega a un destino visto desde el servidor. Remoto: al revés. SOCKS: un proxy por el servidor.", "Opened with the terminal. Local: a port on this PC reaches a destination as seen from the server. Remote: the other way round. SOCKS: a proxy through the server.");
        T("Term.Editor.Forward.Kind", "Tipo", "Type");
        T("Term.Editor.Forward.Local", "Local (-L)", "Local (-L)");
        T("Term.Editor.Forward.Remote", "Remoto (-R)", "Remote (-R)");
        T("Term.Editor.Forward.Dynamic", "SOCKS (-D)", "SOCKS (-D)");
        T("Term.Editor.Forward.PortHint", "Puerto", "Port");
        T("Term.Editor.Forward.TargetHint", "destino:puerto", "host:port");
        T("Term.Editor.Forward.Auto", "Al conectar", "On connect");
        T("Term.Editor.Forward.Invalid", "Revisa los reenvíos de puertos: cada uno necesita un puerto válido y, si no es SOCKS, un destino host:puerto.", "Check the port forwards: each needs a valid port and, unless it's SOCKS, a host:port destination.");
        T("Term.Editor.Options.Section", "Opciones de la terminal", "Terminal options");
        T("Term.Editor.ProxyJump", "Servidor intermedio (ProxyJump)", "Jump host (ProxyJump)");
        T("Term.Editor.ProxyJump.Hint", "usuario@bastion:22 — vacío: el del ~/.ssh/config", "user@bastion:22 — empty: the one in ~/.ssh/config");
        T("Term.Editor.Startup", "Comando al iniciar", "Command on start");
        T("Term.Editor.Startup.Hint", "Por ejemplo: cd /var/www", "For example: cd /var/www");
        T("Term.Editor.Validation.Port", "El puerto SSH debe ser un número entre 1 y 65535 (o vacío).", "The SSH port must be a number from 1 to 65535 (or empty).");
        T("Term.Editor.Validation.Key", "Importa o elige la llave privada para este servidor.", "Import or choose the private key for this server.");
        T("Term.Editor.Validation.User", "El usuario no puede tener espacios.", "The user can't contain spaces.");
        T("Term.Opt.Prompt", "Prompt powerline de RDMan (solo en esta sesión)", "RDMan powerline prompt (this session only)");
        T("Term.Opt.Scheme", "Colores", "Colors");
        T("Term.Opt.Stats", "Mostrar CPU, memoria y disco", "Show CPU, memory and disk");
        T("Term.Opt.Log", "Guardar la sesión en un archivo", "Save the session to a file");
        T("Term.Opt.KeepAlive", "Mantener viva la conexión", "Keep the connection alive");
        T("Term.Opt.KeepAlive.Config", "según ~/.ssh/config (30 s)", "per ~/.ssh/config (30 s)");
        T("Term.Opt.Off", "No", "Off");
        T("Term.Opt.Clipboard", "Programas del servidor copian al portapapeles", "Server programs copy to the clipboard");
        T("Term.Opt.Clipboard.Allow", "Permitir", "Allow");
        T("Term.Opt.Clipboard.Ask", "Preguntar", "Ask");
        T("Term.Scheme.Auto", "RDMan (sigue el tema)", "RDMan (follows the theme)");

        T("Settings.Terminal", "Terminal SSH", "SSH terminal");
        T("Settings.Terminal.Font", "Fuente", "Font");
        T("Settings.Terminal.Size", "Tamaño", "Size");
        T("Settings.Terminal.Colors", "Colores", "Colors");
        T("Settings.Terminal.Rendering", "Dibujo del texto", "Text rendering");
        T("Settings.Terminal.Gpu", "GPU", "GPU");
        T("Settings.Terminal.GpuTip", "WebGL: suavizado nítido, símbolos powerline y de cuadros perfectos, el más rápido.", "WebGL: crisp antialiasing, pixel-perfect powerline and box symbols, the fastest.");
        T("Settings.Terminal.ClearTypeTip", "Suavizado de subpíxel como el resto de Windows; algo más lento con mucha salida.", "Subpixel antialiasing like the rest of Windows; a bit slower with heavy output.");
        T("Settings.Terminal.LineHeight", "Altura de línea", "Line height");
        T("Settings.Terminal.Cursor", "Cursor", "Cursor");
        T("Settings.Terminal.Cursor.Bar", "Barra", "Bar");
        T("Settings.Terminal.Cursor.Block", "Bloque", "Block");
        T("Settings.Terminal.Cursor.Underline", "Subrayado", "Underline");
        T("Settings.Terminal.CursorBlink", "Cursor parpadeante", "Blinking cursor");
        T("Settings.Terminal.Scrollback", "Historial", "History");
        T("Settings.Terminal.Lines", "líneas", "lines");
        T("Settings.Terminal.RightClick", "Clic derecho", "Right click");
        T("Settings.Terminal.RightClick.Menu", "Menú", "Menu");
        T("Settings.Terminal.RightClick.CopyPaste", "Copiar / pegar", "Copy / paste");
        T("Settings.Terminal.Bell", "Campana", "Bell");
        T("Settings.Terminal.Bell.Visual", "Visual", "Visual");
        T("Settings.Terminal.Bell.Sound", "Sonido", "Sound");
        T("Settings.Terminal.Bell.Off", "No", "Off");
        T("Settings.Terminal.Tint", "Teñir el fondo con el color del servidor", "Tint the background with the server's color");
        T("Settings.Terminal.Contrast", "Asegurar contraste legible en cualquier color", "Ensure readable contrast on any color");
        T("Settings.Terminal.SmartCtrlC", "Ctrl+C copia si hay texto seleccionado", "Ctrl+C copies when text is selected");
        T("Settings.Terminal.CopyOnSelect", "Copiar al seleccionar", "Copy on select");
        T("Settings.Terminal.PasteWarning", "Avisar antes de pegar varias líneas", "Warn before pasting several lines");

        T("Palette.Quick.Title", "Conectar a {0}", "Connect to {0}");
        T("Palette.Quick.Subtitle", "Terminal SSH nueva · se guarda en tus conexiones", "New SSH terminal · saved to your connections");
        T("Palette.Quick.FromConfig", "Host de ~/.ssh/config · se guarda en tus conexiones", "Host from ~/.ssh/config · saved to your connections");
        T("Palette.Quick.Saved", "{0} se añadió a tus conexiones", "{0} was added to your connections");
        T("Palette.Quick.Undo", "Deshacer", "Undo");
        T("Palette.Snippets.Hint", "Enter escribe el fragmento en la terminal activa  ·  borra «>» para buscar servidores", "Enter types the snippet into the active terminal  ·  delete “>” to search servers");
        T("Palette.Snippets.Manage", "Gestionar fragmentos…", "Manage snippets…");
        T("Palette.Snippets.ManageHint", "Crear, editar y organizar comandos guardados", "Create, edit and organize saved commands");
        T("Snippets.Title", "Fragmentos", "Snippets");
        T("Snippets.List", "Comandos guardados", "Saved commands");
        T("Snippets.Add", "Nuevo fragmento", "New snippet");
        T("Snippets.NewName", "Nuevo fragmento", "New snippet");
        T("Snippets.Empty", "Guarda aquí los comandos que repites: reinicios, logs, despliegues…", "Keep the commands you repeat here: restarts, logs, deploys…");
        T("Snippets.Name", "Nombre", "Name");
        T("Snippets.Scope", "Disponible en", "Available in");
        T("Snippets.Scope.All", "Todos los servidores", "All servers");
        T("Snippets.Body", "Comando", "Command");
        T("Snippets.PlaceholderHelp", "{{host}}, {{user}} y {{server}} se rellenan solos; cualquier otro {{nombre}} se pregunta al usarlo.", "{{host}}, {{user}} and {{server}} are filled in; any other {{name}} is asked for when used.");
        T("Snippets.Run", "Pulsar Enter después (ejecutar)", "Press Enter afterwards (run it)");
        T("Snippets.Tip", "Úsalos con Ctrl+Mayús+P y «>» desde una terminal.", "Use them with Ctrl+Shift+P and “>” from a terminal.");
        T("Snippets.NeedsTerminal", "Abre una terminal conectada para usar fragmentos", "Open a connected terminal to use snippets");
        T("Snippets.Ask", "Valor para {{{0}}}", "Value for {{{0}}}");

        T("Term.Tip.Sftp", "Archivos del servidor (SFTP) · Ctrl+Mayús+E", "Server files (SFTP) · Ctrl+Shift+E");
        T("Sftp.Title", "Archivos (SFTP)", "Files (SFTP)");
        T("Sftp.Follow", "Seguir la carpeta de la terminal", "Follow the terminal's folder");
        T("Sftp.Hidden", "Mostrar ocultos", "Show hidden files");
        T("Sftp.Path", "Ruta", "Path");
        T("Sftp.Empty", "Carpeta vacía. Arrastra archivos aquí para subirlos.", "Empty folder. Drag files here to upload them.");
        T("Sftp.NotFound", "No existe {0}", "{0} doesn't exist");
        T("Sftp.Denied", "Sin permiso para abrir {0}", "No permission to open {0}");
        T("Sftp.DeniedShort", "Permiso denegado", "Permission denied");
        T("Sftp.NoSubsystem", "El servidor no ofrece SFTP.", "The server doesn't offer SFTP.");
        T("Sftp.Upload", "Subir archivos…", "Upload files…");
        T("Sftp.Download", "Descargar…", "Download…");
        T("Sftp.Downloaded", "{0} descargado", "{0} downloaded");
        T("Sftp.ShowInFolder", "Mostrar", "Show");
        T("Sftp.Uploaded", "{0} subido al servidor", "{0} uploaded to the server");
        T("Sftp.Editing", "Editando {0}: cada vez que guardes se subirá al servidor", "Editing {0}: every save is uploaded to the server");
        T("Sftp.TooBigToEdit", "El archivo es demasiado grande para editarlo (más de 64 MB): descárgalo", "The file is too big to edit (over 64 MB): download it instead");
        T("Sftp.Edit", "Editar", "Edit");
        T("Sftp.Open", "Abrir", "Open");
        T("Sftp.Rename", "Renombrar", "Rename");
        T("Sftp.CopyPath", "Copiar ruta", "Copy path");
        T("Sftp.NewFolder", "Nueva carpeta", "New folder");
        T("Sftp.Create", "Crear", "Create");
        T("Sftp.CdHere", "Ir a esta carpeta en la terminal", "Go to this folder in the terminal");
        T("Sftp.ConfirmDelete", "Se eliminará {0} del servidor.", "{0} will be deleted from the server.");
        T("Sftp.ConfirmDeleteFolder", "Se eliminará la carpeta {0} del servidor (debe estar vacía).", "The folder {0} will be deleted from the server (it must be empty).");
        T("Sftp.ConfirmOverwrite", "{0} ya existe en el servidor. ¿Reemplazarlo?", "{0} already exists on the server. Replace it?");
        T("Sftp.Overwrite", "Reemplazar", "Replace");
        T("Sftp.FolderNotSupported", "{0} es una carpeta: por ahora solo se suben archivos", "{0} is a folder: only files can be uploaded for now");
        T("Sftp.Cancelled", "Cancelado", "Canceled");
        T("Sftp.Stop", "Detener", "Stop");
        T("Term.Tip.Broadcast", "Escribir a la vez en varias terminales: activa este botón en cada una", "Type into several terminals at once: turn this on in each of them");
        T("Term.Broadcast.Banner", "Lo que escribas va a {0} terminales", "What you type goes to {0} terminals");
        T("Term.Broadcast.Alone", "Transmisión activada: actívala en otra terminal para escribir en ambas", "Broadcast on: turn it on in another terminal to type into both");
        T("Term.Broadcast.Leave", "Salir", "Leave");
        T("Term.Broadcast.StopAll", "Detener en todas", "Stop everywhere");

        T("Menu.SshKeys", "Llaves SSH…", "SSH keys…");
        T("Menu.Snippets", "Fragmentos de terminal…", "Terminal snippets…");
        T("Menu.ImportSshConfig", "Importar de ~/.ssh/config…", "Import from ~/.ssh/config…");
        T("SshKeys.Title", "Llaves SSH", "SSH keys");
        T("SshKeys.Intro", "Llaves privadas guardadas en RDMan (por ejemplo, los .pem de AWS). Se usan desde el inicio de sesión de cada servidor o grupo.", "Private keys saved in RDMan (for example AWS .pem files). Servers and groups use them from their sign-in settings.");
        T("SshKeys.Empty", "Aún no hay llaves. Importa un .pem, .key, .ppk o una llave de OpenSSH.", "No keys yet. Import a .pem, .key, .ppk or OpenSSH key.");
        T("SshKeys.Unused", "Sin usar", "Not used");
        T("SshKeys.UsedOne", "Usada en {0} lugar", "Used in {0} place");
        T("SshKeys.UsedMany", "Usada en {0} lugares", "Used in {0} places");
        T("SshKeys.PassphraseSaved", "frase guardada", "passphrase saved");
        T("SshKeys.Certificate", "con certificado", "with certificate");
        T("SshKeys.Missing", "sin la llave (exportada sin secretos): vuelve a importarla", "key missing (exported without secrets): import it again");
        T("SshKeys.ConfirmDelete", "Se eliminará la llave {0} de RDMan. El archivo original no se toca.", "The key {0} will be removed from RDMan. The original file isn't touched.");
        T("SshKeys.ConfirmDeleteUsed", "Se eliminará la llave {0}. Los {1} servidores o grupos que la usan volverán a heredar su inicio de sesión.", "The key {0} will be removed. The {1} servers or groups using it will go back to inheriting their sign-in.");
        T("SshImport.Title", "Importar de ~/.ssh/config", "Import from ~/.ssh/config");
        T("SshImport.Intro", "Crea una conexión de terminal por cada Host de tu configuración de OpenSSH, como si escribieras «ssh alias».", "Creates a terminal connection for each Host in your OpenSSH config, as if you typed “ssh alias”.");
        T("SshImport.Empty", "Tu ~/.ssh/config no declara hosts concretos (solo patrones con * o no existe).", "Your ~/.ssh/config declares no concrete hosts (only * patterns, or it doesn't exist).");
        T("SshImport.Exists", "ya está en tus conexiones", "already in your connections");
        T("SshImport.Group", "Añadir al grupo", "Add to group");
        T("SshImport.Live", "Usuario, llave, puerto y servidor intermedio se leen del archivo en cada conexión: si lo editas, RDMan lo sigue.", "User, key, port and jump host are read from the file on every connection: edit it and RDMan follows.");
        T("SshImport.Do", "Importar", "Import");
        T("SshImport.DoneOne", "Se importó {0} conexión", "{0} connection imported");
        T("SshImport.DoneMany", "Se importaron {0} conexiones", "{0} connections imported");

        T("Tree.Ctx.OpenTerminal", "Abrir terminal", "Open terminal");
        T("Tree.Ctx.NewTerminal", "Nueva terminal", "New terminal");
        T("Tree.Ctx.CopySsh", "Copiar comando ssh", "Copy ssh command");
    }

    private static void AddTerminalSession()
    {
        T("Cap.Terminal", "Terminal", "Terminal");
        T("Health.Terminal.Online", "SSH {0} ms", "SSH {0} ms");
        T("Health.Terminal.Offline", "SSH sin respuesta (puerto {0})", "SSH not responding (port {0})");

        // States, lines printed in the terminal itself
        T("Term.State.Connecting", "Conectando…", "Connecting…");
        T("Term.State.Connected", "Conectado", "Connected");
        T("Term.State.Reconnecting", "Reconectando…", "Reconnecting…");
        T("Term.State.Ended", "Desconectado", "Disconnected");
        T("Term.Idle.Body", "La terminal se abre al conectar. El historial se conserva aunque se corte la conexión.", "The terminal opens when you connect. Its history stays even if the connection drops.");
        T("Term.NotEnabled", "{0} no tiene terminal SSH. Actívala en sus propiedades.", "{0} has no SSH terminal. Turn it on in its properties.");
        T("Term.ConfirmLeave", "Se cerrará la terminal de {0}.", "The terminal on {0} will be closed.");
        T("Term.ConfirmReconnect", "La sesión actual de {0} se cerrará y se abrirá otra.", "The current session on {0} will be closed and a new one opened.");
        T("Term.SignedIn.Key", "con la llave {0}", "with the key {0}");
        T("Term.SignedIn.Agent", "con el agente SSH", "with the SSH agent");
        T("Term.SignedIn.Password", "con contraseña", "with a password");
        T("Term.SignedIn.Interactive", "con verificación del servidor", "with server verification");
        T("Term.Line.Connecting", "Conectando a {0}…", "Connecting to {0}…");
        T("Term.Line.Reconnecting", "Reconectando a {0}…", "Reconnecting to {0}…");
        T("Term.Line.Reconnected", "reconectado {0}", "reconnected {0}");
        T("Term.Line.Jump", "a través de {0}", "through {0}");
        T("Term.Line.HostKey", "identidad del servidor verificada ({0})", "server identity verified ({0})");
        T("Term.Line.SignedIn", "sesión iniciada como {0}", "signed in as {0}");
        T("Term.Line.AuthMisconfigured", "La llave o el perfil configurado ya no existe; se usará el método automático.", "The configured key or profile no longer exists; using the automatic method.");
        T("Term.Line.Cancelled", "Conexión cancelada. Pulsa Enter para volver a intentarlo.", "Connection canceled. Press Enter to try again.");
        T("Term.Line.Ended", "Sesión terminada · Enter para reconectar", "Session ended · Enter to reconnect");
        T("Term.Line.Disconnected", "Desconectado · Enter para reconectar", "Disconnected · Enter to reconnect");
        T("Term.Line.Lost", "Se perdió la conexión: {0}", "Connection lost: {0}");
        T("Term.Line.AttemptFailed", "Intento fallido: {0}", "Attempt failed: {0}");
        T("Term.Line.NeedsPassword", "El servidor pide contraseña.", "The server asks for a password.");
        T("Term.Line.UnknownHost", "Primera conexión a {0}: confirma su identidad.", "First connection to {0}: confirm its identity.");
        T("Term.Line.ChangedHost", "La identidad de {0} no coincide con la guardada.", "The identity of {0} doesn't match the saved one.");
        T("Term.Line.KnownHostsWriteFailed", "No se pudo guardar en known_hosts ({0}); se confía solo en esta sesión.", "Couldn't write known_hosts ({0}); trusted for this session only.");
        T("Term.Line.PromptUnavailable", "El prompt de RDMan no está disponible en este servidor; se abrió el shell normal.", "RDMan's prompt isn't available on this server; the normal shell opened.");

        // Banners
        T("Term.Banner.Reconnecting", "Conexión perdida. Reconectando en {0} s (intento {1} de {2})", "Connection lost. Reconnecting in {0} s (attempt {1} of {2})");
        T("Term.Banner.Trying", "Reconectando (intento {0} de {1})…", "Reconnecting (attempt {0} of {1})…");
        T("Term.Banner.Now", "Reconectar ahora", "Reconnect now");
        T("Term.Banner.GaveUp", "No se pudo reconectar.", "Couldn't reconnect.");
        T("Term.Banner.Clipboard", "Un programa del servidor quiere copiar texto a tu portapapeles.", "A program on the server wants to copy text to your clipboard.");
        T("Term.Banner.AllowOnce", "Permitir", "Allow");
        T("Term.Banner.AllowAlways", "Permitir siempre en este servidor", "Always allow on this server");
        T("Term.Banner.Ignore", "Ignorar", "Ignore");

        // Host key cards
        T("Term.HostKey.Unknown.Title", "Confirma la identidad del servidor", "Confirm the server's identity");
        T("Term.HostKey.Unknown.Body", "Es la primera vez que te conectas a {0}. Comprueba que la huella coincide con la del servidor (por ejemplo, en su consola o con su administrador) antes de confiar en él.", "This is your first connection to {0}. Check that the fingerprint matches the server's (for example in its console, or with its administrator) before trusting it.");
        T("Term.HostKey.Unknown.BodyOtherTypes", "{0} ya es conocido con otro tipo de llave, pero no con esta. Comprueba la huella antes de confiar en ella.", "{0} is already known with another key type, but not this one. Check the fingerprint before trusting it.");
        T("Term.HostKey.Host", "Servidor", "Server");
        T("Term.HostKey.Type", "Tipo de llave", "Key type");
        T("Term.HostKey.Fingerprint", "Huella", "Fingerprint");
        T("Term.HostKey.NewKey", "Llave presentada", "Key presented");
        T("Term.HostKey.File", "Guardada en", "Saved in");
        T("Term.HostKey.Once", "Conectar solo esta vez", "Connect this time only");
        T("Term.HostKey.Trust", "Confiar y conectar", "Trust and connect");
        T("Term.HostKey.Replace", "Reemplazar la llave guardada…", "Replace saved key…");
        T("Term.HostKey.ReplaceConfirm.Title", "¿Reemplazar la llave guardada?", "Replace the saved key?");
        T("Term.HostKey.ReplaceConfirm.Body", "Hazlo solo si quien administra {0} confirmó que la llave cambió (por ejemplo, porque se reinstaló). Si no, alguien podría estar interceptando la conexión.", "Only do this if whoever runs {0} confirmed the key changed (for example after a reinstall). Otherwise someone may be intercepting the connection.");
        T("Term.HostKey.ReplaceConfirm.Do", "Reemplazar y conectar", "Replace and connect");

        // Sign-in prompts (inside the terminal)
        T("Term.Prompt.Password.Title", "Iniciar sesión en {0}", "Sign in to {0}");
        T("Term.Prompt.Password.Body", "Contraseña de {0}.", "Password for {0}.");
        T("Term.Prompt.Password.BodyUser", "¿Con qué usuario entras en este servidor?", "Which user do you sign in to this server with?");
        T("Term.Prompt.User", "Usuario", "User");
        T("Term.Prompt.Password", "Contraseña", "Password");
        T("Term.Prompt.Remember", "Recordar en este servidor (cifrado)", "Remember on this server (encrypted)");
        T("Term.Prompt.SignIn", "Iniciar sesión", "Sign in");
        T("Term.Prompt.Passphrase.Title", "Desbloquear {0}", "Unlock {0}");
        T("Term.Prompt.Passphrase.Body", "Esta llave privada está protegida con una frase de contraseña.", "This private key is protected with a passphrase.");
        T("Term.Prompt.Passphrase.Label", "Frase de contraseña", "Passphrase");
        T("Term.Prompt.Unlock", "Desbloquear", "Unlock");
        T("Term.Prompt.Server.Title", "{0} pide un dato", "{0} is asking for something");
        T("Term.Prompt.Server.Body", "El servidor necesita esta información para iniciar sesión (por ejemplo, un código de verificación).", "The server needs this to sign you in (for example, a verification code).");
        T("Term.Prompt.Continue", "Continuar", "Continue");
        T("Term.Attention.Password", "{0} espera que inicies sesión", "{0} is waiting for you to sign in");
        T("Term.Attention.Passphrase", "{0} necesita la frase de tu llave", "{0} needs your key's passphrase");
        T("Term.Attention.HostKey", "{0}: confirma la identidad del servidor", "{0}: confirm the server's identity");
        T("Term.Attention.Show", "Mostrar", "Show");

        // Paste guard
        T("Term.Paste.Title", "¿Pegar varias líneas?", "Paste several lines?");
        T("Term.Paste.Body", "Vas a pegar {0} líneas ({1} caracteres). Cada línea puede ejecutarse como un comando.", "You're about to paste {0} lines ({1} characters). Each line may run as a command.");
        T("Term.Paste.Do", "Pegar {0} líneas", "Paste {0} lines");

        // Search, menu (page UI)
        T("Term.Search.Placeholder", "Buscar", "Find");
        T("Term.Search.Case", "Distinguir mayúsculas", "Match case");
        T("Term.Search.Word", "Palabra completa", "Whole word");
        T("Term.Search.Regex", "Expresión regular", "Regular expression");
        T("Term.Search.Prev", "Anterior (Mayús+Enter)", "Previous (Shift+Enter)");
        T("Term.Search.Next", "Siguiente (Enter)", "Next (Enter)");
        T("Term.Search.Close", "Cerrar (Esc)", "Close (Esc)");
        T("Term.Search.None", "Sin resultados", "No results");
        T("Term.Menu.Copy", "Copiar", "Copy");
        T("Term.Menu.Paste", "Pegar", "Paste");
        T("Term.Menu.SelectAll", "Seleccionar todo", "Select all");
        T("Term.Menu.Find", "Buscar", "Find");
        T("Term.Menu.Save", "Guardar salida…", "Save output…");
        T("Term.Menu.Clear", "Borrar historial", "Clear history");

        // Toolbar, actions, toasts
        T("Term.Tip.Search", "Buscar (Ctrl+Mayús+F)", "Find (Ctrl+Shift+F)");
        T("Term.Tip.CopyOutput", "Copiar la salida del último comando", "Copy the last command's output");
        T("Term.Tip.Save", "Guardar la salida en un archivo", "Save the output to a file");
        T("Term.Action.EditServer", "Editar servidor", "Edit server");
        T("Term.Action.InstallWebView", "Instalar WebView2", "Install WebView2");
        T("Term.Save.Filter", "Texto (*.txt)|*.txt|HTML con colores (*.html)|*.html", "Text (*.txt)|*.txt|HTML with colors (*.html)|*.html");
        T("Term.Toast.CopiedOutput", "Salida del último comando copiada", "Last command's output copied");
        T("Term.Toast.NoOutput", "No hay salida de comando para copiar (necesita el prompt de RDMan)", "No command output to copy (needs RDMan's prompt)");
        T("Term.Toast.Saved", "Guardado: {0}", "Saved: {0}");
        T("Term.Toast.CommandCopied", "Comando ssh copiado", "ssh command copied");

        // Port forwards
        T("Term.Forward.Listening", "Reenviando {0}", "Forwarding {0}");
        T("Term.Forward.Failed", "No se pudo reenviar {0}: {1}", "Couldn't forward {0}: {1}");
        T("Term.Forward.Stopped", "Detenido: {0}", "Stopped: {0}");
        T("Term.Forward.InUse", "el puerto local ya está en uso", "the local port is already in use");
        T("Term.Forward.Start", "Iniciar", "Start");
        T("Term.Forward.Stop", "Detener", "Stop");
        T("Term.Forward.Copy", "Copiar {0}", "Copy {0}");
        T("Term.Forward.Open", "Abrir en el navegador", "Open in browser");

        // Stats
        T("Term.Stats.Cpu", "Uso de CPU (último minuto)", "CPU usage (last minute)");
        T("Term.Stats.Load", "Carga media (1 minuto)", "Load average (1 minute)");
        T("Term.Stats.LoadShort", "carga", "load");
        T("Term.Stats.Uptime", "Tiempo encendido", "Uptime");
    }
}
