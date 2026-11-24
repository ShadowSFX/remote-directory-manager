<p align="center"><img src="src/RDMan/Assets/rdman.png" width="88" alt=""></p>

<h1 align="center">Remote Directory Manager</h1>

<p align="center">RDCMan, rebuilt: every server's <b>files</b> and <b>remote desktop</b> in one modern app. Name your servers, group them, keep their credentials, browse with real Windows copy &amp; paste and work on their desktops in tabs.</p>

<p align="center"><a href="../../releases/latest"><b>Descargar RDMan.exe</b></a> · Windows 10/11 x64 · portable, sin instalador</p>

---

## Español

¿Entras a tus VPS con `\\ip` desde *Ejecutar* y por Escritorio remoto, y tienes que memorizar qué IP es cuál? RDMan los ordena en un árbol con nombres que reconoces y abre sus **archivos** o su **escritorio** en pestañas.

### Escritorio remoto (lo de RDCMan, sin sus fallas)

- **Sesiones RDP dentro de pestañas y del doble panel**, junto a los archivos del mismo servidor (selector **Archivos | Escritorio** en la franja de identidad).
- **Cambia de tamaño sin reconectar**: al redimensionar la ventana, dividir en dos paneles o maximizar, la resolución del servidor se ajusta al instante (en RDCMan hay que reconectar).
- **Cambiar de pestaña no desconecta**: las sesiones siguen vivas en segundo plano.
- **Credencial guardada de Windows**: usa la que ya guardó *Conexión a Escritorio remoto* para ese equipo, sin copiar el secreto; o usuario y contraseña, credenciales guardadas reutilizables, o igual que para archivos.
- **Opciones heredables en un solo lugar** (portapapeles, unidades, impresoras, sonido, atajos de Windows, NLA, verificación del servidor, Admin restringido / Remote Credential Guard, sesión de administración, colores): cada opción muestra qué valor heredará y de qué grupo, en vez de las casillas "heredar" de RDCMan en cada pestaña.
- **Errores explicados** (credenciales, cuenta bloqueada, contraseña vencida, sesión tomada por otro equipo, inactividad, licencias…) con la acción para resolverlos.
- **Escritorio en una ventana aparte** (botón de la barra, menú de la pestaña o del servidor): llévalo a otro monitor, con su propia pantalla completa; vuelve a una pestaña cuando quieras. Al moverse se reconecta, y lo que tenías abierto en el servidor sigue ahí.
- **Puerta de enlace (RD Gateway)** por grupo o servidor, heredable: siempre o salvo direcciones locales, iniciando sesión con la cuenta del servidor, tu usuario de Windows o una propia (también se importa desde RDCMan).
- Ctrl+Alt+Supr, pantalla completa (F11) y `Ctrl+Alt+←` para devolverle el teclado a RDMan.

### Archivos

- **Nombres en vez de IPs.** Cada servidor tiene nombre, color de identificación, etiquetas y notas. La franja superior de cada pestaña te dice siempre en qué servidor estás, con qué usuario y con qué latencia.
- **Grupos y subgrupos** (cliente, ambiente, datacenter) con **credenciales heredadas**, igual que RDCMan. También credenciales guardadas reutilizables, invitado o tu usuario de Windows.
- **Copiar y pegar idéntico a Windows.** Usa el mismo motor que el Explorador: el diálogo de progreso, la resolución de conflictos, la papelera, `Ctrl+C`/`Ctrl+X`/`Ctrl+V` entre RDMan y el Explorador en ambos sentidos, y arrastrar y soltar desde o hacia el escritorio.
- **Paneles como VS Code.** Arrastra una pestaña (o un servidor del árbol) al borde de un panel para dividirlo a ese lado, al centro para moverla ahí, o a una tira de pestañas para reordenar. Tantos paneles como quepan, en filas y columnas; un escritorio remoto se mueve entre paneles sin desconectarse. `F3` divide a la derecha y el diseño se recuerda al reabrir.
- **Estado en vivo.** Cada servidor muestra si responden sus archivos (puerto 445) y su escritorio (puerto RDP), con la latencia real; los grupos se ven como tarjetas.
- **`Ctrl+K`** para saltar a cualquier servidor por nombre, IP, etiqueta o grupo.
- **Agregar varios servidores a la vez** con patrones (`web[1-5]`, `db[01-10]`, `10.0.0.[20-24]`, `{api,auth}.corp.local`) y vista previa; **conectar o desconectar todos los escritorios de un grupo** desde su menú; la vista **Conectados** muestra una miniatura de cada escritorio.
- **Vistas**: Favoritos (marca la estrella de un servidor), Recientes y Conectados, más **grupos inteligentes**: búsquedas guardadas por nombre, dirección, etiqueta, grupo o notas que se actualizan solas.
- **Importa** tus archivos de **RDCMan (`.rdg`)** —grupos, servidores, credenciales (las contraseñas se recuperan si las guardaste con tu usuario), opciones y grupos inteligentes—, los escritorios remotos que Windows ya tiene guardados, las conexiones de `net use` o una colección `.rdm`.
- **Errores SMB explicados**: invitado bloqueado por Windows 11, sesión con otro usuario (error 1219), puerto 445 bloqueado, credenciales incorrectas… con la acción para resolverlos.
- **Portable.** Un solo `.exe`. La configuración vive en `RDMan.data\` junto al ejecutable (o en `%APPDATA%\RDMan` si la carpeta es de solo lectura). Las contraseñas se cifran con DPAPI y solo funcionan con tu usuario en ese equipo.
- **Se actualiza solo.** Descarga cada versión nueva desde GitHub Releases, verifica su SHA‑256 y la instala al reiniciar.
- Español e inglés, tema oscuro y claro.

### Atajos

| | |
|---|---|
| `Ctrl+K` | Buscar conexión |
| `Ctrl+T` / `Ctrl+W` | Nueva pestaña / cerrar pestaña |
| `F3` / `F6` | Dividir a la derecha / pasar al siguiente panel |
| `Alt+←` `Alt+→` `Alt+↑` `Retroceso` | Atrás, adelante, subir |
| `Ctrl+L` | Escribir una ruta (o el nombre de un servidor) |
| `Ctrl+F` | Filtrar la carpeta |
| `F2` · `Supr` · `Shift+Supr` | Cambiar nombre · eliminar · eliminar definitivamente |
| `Ctrl+Shift+N` | Nueva carpeta |
| `Ctrl+N` | Nuevo servidor |
| `Ctrl+Shift+D` | Abrir el escritorio del servidor actual |
| `Shift+Enter` en `Ctrl+K` | Abrir la otra vista (archivos ↔ escritorio) |
| `Ctrl+Alt+←` | Dentro de una sesión: devolver el teclado a RDMan |

`RDMan.exe \\servidor\recurso` abre esa ruta en una pestaña (en la ventana que ya esté abierta). `RDMan.exe /c Nombre` conecta al escritorio de ese servidor (por su nombre en RDMan o su dirección).

### Notas

- Windows solo conecta SMB por el puerto 445.
- Windows permite **un usuario por servidor**. Si el Explorador ya abrió una sesión con otro usuario, RDMan te ofrece cerrarla.
- Windows 11 bloquea el acceso de invitado por defecto. Usa un usuario de Samba (`smbpasswd -a usuario`).
- El ejecutable no está firmado digitalmente. SmartScreen puede advertirlo la primera vez que lo ejecutes.

---

## English

RDMan is RDCMan rebuilt for both sides of a server: a tree of named servers and groups with inherited credentials and live status, where each server opens its **remote desktop** (embedded RDP sessions in tabs and split panes, resized without reconnecting, Windows' saved credentials, per-option inheritance) and its **files** (a tabbed, split-pane browser on the Windows copy engine: same progress dialog, conflict handling, recycle bin and clipboard format as File Explorer).

Download `RDMan.exe` from [Releases](../../releases/latest) and run it. No installer. It updates itself from GitHub Releases (SHA-256 verified, applied on restart).

## Build

```powershell
dotnet test tests/RDMan.Core.Tests
dotnet publish src/RDMan/RDMan.csproj -c Release -r win-x64 -o publish
```

Releases: push a tag `vX.Y.Z` and the `Release` workflow publishes `RDMan.exe` and `RDMan.exe.sha256`.

License: MIT.
