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
        T("Term.ConnectedAs", "{0} · con {1}", "{0} · with {1}");
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
