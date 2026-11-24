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
    }
}
