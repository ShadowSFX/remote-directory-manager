<p align="center"><img src="src/RDMan/Assets/rdman.png" width="88" alt=""></p>

<h1 align="center">Remote Directory Manager</h1>

<p align="center">Like RDCMan, but for SMB shares. Give every server a name, group them, keep their credentials, and browse their files with real Windows copy &amp; paste.</p>

<p align="center"><a href="../../releases/latest"><b>Descargar RDMan.exe</b></a> · Windows 10/11 x64 · portable, sin instalador</p>

---

## Español

¿Entras a tus VPS con `\\ip` desde *Ejecutar* y tienes que memorizar qué IP es cuál? RDMan los ordena en un árbol con nombres que reconoces y abre cada uno en una pestaña.

- **Nombres en vez de IPs.** Cada servidor tiene nombre, color de identificación, etiquetas y notas. La franja superior de cada pestaña te dice siempre en qué servidor estás, con qué usuario y con qué latencia.
- **Grupos y subgrupos** (cliente, ambiente, datacenter) con **credenciales heredadas**, igual que RDCMan. También credenciales guardadas reutilizables, invitado o tu usuario de Windows.
- **Copiar y pegar idéntico a Windows.** Usa el mismo motor que el Explorador: el diálogo de progreso, la resolución de conflictos, la papelera, `Ctrl+C`/`Ctrl+X`/`Ctrl+V` entre RDMan y el Explorador en ambos sentidos, y arrastrar y soltar desde o hacia el escritorio.
- **Pestañas y doble panel** (`F3`) para copiar entre servidores o desde tu PC.
- **Estado en vivo.** Cada servidor muestra si responde en el puerto 445 y su latencia; los grupos se ven como tarjetas.
- **`Ctrl+K`** para saltar a cualquier servidor por nombre, IP, etiqueta o grupo.
- **Importa** las conexiones que Windows ya conoce (`net use`) o una colección `.rdm`.
- **Errores SMB explicados**: invitado bloqueado por Windows 11, sesión con otro usuario (error 1219), puerto 445 bloqueado, credenciales incorrectas… con la acción para resolverlos.
- **Portable.** Un solo `.exe`. La configuración vive en `RDMan.data\` junto al ejecutable (o en `%APPDATA%\RDMan` si la carpeta es de solo lectura). Las contraseñas se cifran con DPAPI y solo funcionan con tu usuario en ese equipo.
- **Se actualiza solo.** Descarga cada versión nueva desde GitHub Releases, verifica su SHA‑256 y la instala al reiniciar.
- Español e inglés, tema oscuro y claro.

### Atajos

| | |
|---|---|
| `Ctrl+K` | Buscar conexión |
| `Ctrl+T` / `Ctrl+W` | Nueva pestaña / cerrar pestaña |
| `F3` / `F6` | Doble panel / cambiar de panel |
| `Alt+←` `Alt+→` `Alt+↑` `Retroceso` | Atrás, adelante, subir |
| `Ctrl+L` | Escribir una ruta (o el nombre de un servidor) |
| `Ctrl+F` | Filtrar la carpeta |
| `F2` · `Supr` · `Shift+Supr` | Cambiar nombre · eliminar · eliminar definitivamente |
| `Ctrl+Shift+N` | Nueva carpeta |
| `Ctrl+N` | Nuevo servidor |

`RDMan.exe \\servidor\recurso` abre esa ruta en una pestaña (en la ventana que ya esté abierta).

### Notas

- Windows solo conecta SMB por el puerto 445.
- Windows permite **un usuario por servidor**. Si el Explorador ya abrió una sesión con otro usuario, RDMan te ofrece cerrarla.
- Windows 11 bloquea el acceso de invitado por defecto. Usa un usuario de Samba (`smbpasswd -a usuario`).
- El ejecutable no está firmado digitalmente. SmartScreen puede advertirlo la primera vez que lo ejecutes.

---

## English

RDMan organizes your SMB shares like RDCMan organizes RDP servers: a tree of named servers and groups, inherited credentials, live status, and a tabbed, split-pane file browser that uses the Windows copy engine (same progress dialog, conflict handling, recycle bin and clipboard format as File Explorer).

Download `RDMan.exe` from [Releases](../../releases/latest) and run it. No installer. It updates itself from GitHub Releases (SHA-256 verified, applied on restart).

## Build

```powershell
dotnet test tests/RDMan.Core.Tests
dotnet publish src/RDMan/RDMan.csproj -c Release -r win-x64 -o publish
```

Releases: push a tag `vX.Y.Z` and the `Release` workflow publishes `RDMan.exe` and `RDMan.exe.sha256`.

License: MIT.
