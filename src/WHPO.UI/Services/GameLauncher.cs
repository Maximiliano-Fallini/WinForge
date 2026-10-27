using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace WHPO_UI.Services;

/// <summary>Severidad del mensaje de estado de un lanzamiento (para quien lo muestra).</summary>
public enum LaunchStatusKind
{
    Info,
    Warning,
    Hide
}

/// <summary>
/// Lanzamiento de juegos compartido entre la biblioteca y el menú de la bandeja.
///
/// Steam se lanza por su URI (maneja DRM y exes anidados); Epic por la URI del
/// launcher (autentica con Epic Online Services); Battle.net solo arranca con el
/// handshake del launcher (--exec="launch &lt;código&gt;", con reintentos hasta que
/// el juego aparezca); Riot por el Riot Client; GOG Galaxy y Xbox solo si su
/// launcher está abierto (no se abre solo); el resto por su exe directo.
/// </summary>
public static class GameLauncher
{
    /// <summary>
    /// Lanza el juego con la misma lógica que el botón "Iniciar" de la biblioteca.
    /// El callback <paramref name="status"/> recibe los mensajes de progreso/error
    /// (o Hide para ocultar el estado al lanzar bien).
    /// </summary>
    public static async Task LaunchGameAsync(
        IGameBoostService? gameBoost,
        IProcessService processes,
        ILoggingService log,
        string fileName,
        string arguments,
        string? blizzardCode,
        string? installPath,
        string? exeFileName,
        string? launcher,
        Action<string, LaunchStatusKind>? status = null)
    {
        bool epic = string.Equals(launcher, "Epic", StringComparison.OrdinalIgnoreCase);
        bool gog = string.Equals(launcher, "GOG", StringComparison.OrdinalIgnoreCase);
        bool xbox = string.Equals(launcher, "Xbox", StringComparison.OrdinalIgnoreCase);

        // Abre la sesión justo antes de lanzar (no al entrar acá): si el lanzamiento
        // se cancela —launcher cerrado, ejecutable faltante—, no queda una sesión
        // esperando un proceso que nunca va a nacer. Con un launcher de por medio el
        // proceso del juego puede nacer con otro nombre o dentro de una subcarpeta;
        // la sesión aporta la carpeta y el launcher para reconocerlo, sin aplicarle
        // la regla al launcher. Es idempotente: los ApplyLaunchChainRule de más
        // abajo reutilizan esta misma sesión.
        void BeginSession(string? launcherName = null)
        {
            if (!string.IsNullOrEmpty(exeFileName))
                processes.BeginLaunchSession(exeFileName, launcherName ?? launcher, installPath);
        }

        // Registrar el objetivo temporalmente en el overlay antes de lanzar: así el
        // detector prioriza este juego y no confunde un launcher/browser con él.
        try
        {
            var overlay = App.Services.GetService<IOverlayMetricsService>();
            if (overlay != null && !string.IsNullOrWhiteSpace(exeFileName))
                overlay.RegisterLaunchedGame(exeFileName, installPath);
        }
        catch { }

        // Modo juego de WinForge (BETA): se aplica cuando el juego ARRANCA de verdad
        // (proceso iniciado o detectado), nunca al principio del método. Antes se
        // aplicaba antes de validar nada: un lanzamiento cancelado —launcher cerrado,
        // ejecutable faltante, Epic que nunca abre— dejaba servicios detenidos y el
        // aviso «Modo juego activo» en la biblioteca sin ninguna partida.
        // No hace nada si el switch está desactivado; corre en background para no
        // retrasar el juego.
        void ApplyBoost()
        {
            if (gameBoost != null) _ = gameBoost.ApplyAsync();
        }

        // GOG Galaxy y Xbox: el launcher NO se abre solo. Si está cerrado, no se
        // lanza el juego (los de Xbox son paquetes MSIX que exigen el app activo;
        // los de GOG no garantizamos el lanzamiento sin el launcher).
        if (gog || xbox)
        {
            string procName = gog ? "GalaxyClient" : "Xbox";
            string displayName = gog ? "GOG Galaxy" : "Xbox";
            if (!IsLauncherRunning(processes, procName))
            {
                SetStatus(status, I18n.T("Abrí {0} y probá de nuevo.", displayName), LaunchStatusKind.Warning);
                return;
            }
            if (string.IsNullOrEmpty(fileName))
            {
                SetStatus(status, I18n.T("Ejecutable no encontrado"), LaunchStatusKind.Warning);
                return;
            }
            BeginSession();
            // Solo después de que el proceso/URI arrancó sin excepción: si el
            // lanzamiento falla, no hay partida que optimizar.
            if (StartProcess(fileName, arguments))
                ApplyBoost();
            if (!string.IsNullOrEmpty(exeFileName))
                processes.ApplyLaunchChainRule(exeFileName);
            return;
        }
        // Blacksmith (launcher oficial de Dark and Darker de Ironmace): el juego
        // NO se puede lanzar por su exe directo — el anti-cheat Tavern lo cierra
        // al instante (ExitCode -65535) si no lo lanza el launcher. El launcher no
        // expone un comando CLI para lanzar el juego, así que la única vía es:
        // 1) abrir el launcher (si no está corriendo), 2) el usuario aprieta Play
        // adentro. No se espera la ventana del launcher (tarda en cargar y no es
        // necesario para el flujo).
        if (string.Equals(launcher, "Blacksmith", StringComparison.OrdinalIgnoreCase))
        {
            string? blacksmithExe = FindBlacksmithLauncher();
            if (blacksmithExe == null)
            {
                SetStatus(status, I18n.T("No se encontró el launcher Blacksmith. Asegurate de que esté instalado."), LaunchStatusKind.Warning);
                return;
            }
            BeginSession("Blacksmith");
            if (!IsLauncherRunning(processes, "Blacksmith"))
            {
                SetStatus(status, I18n.T("Abriendo Blacksmith..."), LaunchStatusKind.Info);
                StartProcess(blacksmithExe, "");
            }
            // Sin boost: el juego lo lanza el usuario DESDE Blacksmith y desde acá no
            // se puede confirmar que haya arrancado; aplicarlo dejaba optimizaciones
            // activas con solo abrir el launcher.
            SetStatus(status, I18n.T("Abrí Blacksmith y apretá Play para iniciar Dark and Darker."), LaunchStatusKind.Info);
            if (!string.IsNullOrEmpty(exeFileName))
                processes.ApplyLaunchChainRule(exeFileName);
            return;
        }

        if (blizzardCode == null && !epic)
        {
            // Log de diagnóstico: registrar qué exe se intenta abrir y cualquier
            // error (StartProcess silenciaba todo con Debug.WriteLine).
            if (!string.IsNullOrEmpty(fileName)) BeginSession();
            bool started = StartProcessLogged(fileName, arguments, msg =>
            {
                try { log.LogWarning(msg); } catch { }
            });
            // Sigue la cadena de arranque: el proceso real puede nacer con otro
            // nombre (launcher de por medio), así que no se le aplica la regla al
            // launcher sino al proceso del juego cuando aparece.
            if (!string.IsNullOrEmpty(exeFileName))
                processes.ApplyLaunchChainRule(exeFileName);
            if (started) ApplyBoost();
            return;
        }

        // Epic: el lanzamiento lo hace el launcher a través de su URI (el mismo
        // mecanismo que el acceso directo que crea Epic). Va por un camino propio
        // porque la URI tiene que procesarla el shell del usuario, no este proceso.
        if (epic)
        {
            await LaunchEpicGameAsync(gameBoost, log, processes, fileName, installPath, exeFileName, status);
            return;
        }

        // Battle.net: el launcher tiene que estar corriendo antes de lanzar el juego
        // (los juegos de Blizzard necesitan su sesión).
        var launcherPath = FindBattleNetLauncher();
        const string launcherName = "Battle.net";
        const string launcherProc = "Battle.net";
        if (launcherPath == null)
        {
            SetStatus(status, I18n.T("No se encontró el launcher de {0}.", launcherName), LaunchStatusKind.Warning);
            return;
        }

        if (!IsProcessRunning(launcherProc))
        {
            // Arranque en frío: abrir el launcher y esperar a que levante su ventana.
            SetStatus(status, I18n.T("Abriendo el launcher de {0}...", launcherName), LaunchStatusKind.Info);
            StartProcess(launcherPath, "");
            if (!await WaitForProcessWindowAsync(launcherProc, TimeSpan.FromSeconds(30)))
            {
                SetStatus(status, I18n.T("El launcher de {0} no respondió a tiempo. Verificá que estés logueado y probá de nuevo.", launcherName), LaunchStatusKind.Warning);
                return;
            }
        }

        SetStatus(status, I18n.T("Iniciando el juego..."), LaunchStatusKind.Info);
        BeginSession("Battle.net");

        // El lanzamiento lo hace el launcher. Si el comando se manda antes de
        // que el launcher esté listo, se pierde y solo abre la página del juego:
        // se reintenta hasta que el juego aparezca.
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (IsGameProcessRunning(processes, installPath, exeFileName))
            {
                // Recién acá hay partida: el proceso del juego existe.
                if (gameBoost != null) _ = gameBoost.ApplyAsync();
                SetStatus(status, "", LaunchStatusKind.Hide);
                return;
            }
            // Si el usuario cerró el launcher mientras esperaba (cambió de idea,
            // eligió mal el juego...), NO reabrirlo: se cancela el lanzamiento.
            // Antes el loop reenviaba el comando a ciegas y reabría Battle.net
            // aunque el usuario lo hubiera cerrado en el medio.
            if (!IsProcessRunning(launcherProc))
            {
                SetStatus(status, I18n.T("Se cerró el launcher de {0} — se cancela el lanzamiento.", launcherName), LaunchStatusKind.Warning);
                return;
            }
            StartProcess(launcherPath, $"--exec=\"launch {blizzardCode}\"");
            await Task.Delay(10000);
        }

        SetStatus(status, I18n.T("El juego no arrancó. Verificá que estés logueado en Battle.net y probá de nuevo."), LaunchStatusKind.Warning);
    }

    /// <summary>
    /// Lanza un juego de Epic como lo haría su acceso directo: le pide al launcher
    /// que ejecute el título con la URI <c>com.epicgames.launcher://apps/...?action=launch</c>
    /// y espera a que aparezca el proceso del juego.
    ///
    /// La URI la entrega <c>explorer.exe</c> (el shell del usuario), no este proceso:
    /// la app corre como administrador y, si se lanzara desde acá, Windows abriría
    /// EpicGamesLauncher.exe ELEVADO. El launcher de Epic no funciona en ese contexto
    /// (queda sin sesión y el juego no arranca), que es justo la diferencia con Steam
    /// y Battle.net: esos sí toleran arrancar elevados. Enviándola al shell, el
    /// launcher la procesa con el mismo nivel que un doble clic en el escritorio.
    /// </summary>
    private static async Task LaunchEpicGameAsync(
        IGameBoostService? gameBoost,
        ILoggingService log,
        IProcessService processes,
        string uri,
        string? installPath,
        string? exeFileName,
        Action<string, LaunchStatusKind>? status)
    {
        if (string.IsNullOrEmpty(uri))
        {
            SetStatus(status, I18n.T("No se pudo armar la URI del launcher de Epic Games."), LaunchStatusKind.Warning);
            return;
        }

        // La sesión se abre acá, con la URI ya validada: si no hay nada que lanzar,
        // no queda esperando un proceso inexistente.
        if (!string.IsNullOrEmpty(exeFileName))
            processes.BeginLaunchSession(exeFileName, "Epic", installPath);

        // Manifest viejo sin AppName/datos del catálogo: no hay URI que pedirle al
        // launcher y la card manda el exe. Se abre directo como último recurso.
        if (!uri.StartsWith("com.epicgames.launcher:", StringComparison.OrdinalIgnoreCase))
        {
            if (StartProcessLogged(uri, "", msg => { try { log.LogWarning(msg); } catch { } })
                && gameBoost != null)
                _ = gameBoost.ApplyAsync();
            if (!string.IsNullOrEmpty(exeFileName))
                processes.ApplyLaunchChainRule(exeFileName);
            return;
        }

        // Launcher cerrado: se abre primero (también sin elevación) y se espera su
        // ventana. Si no se encuentra el exe, la URI igual lo abre por el protocolo
        // registrado, así que no es un error: se sigue con el envío.
        string? launcherPath = FindEpicLauncher();
        if (launcherPath != null && !IsProcessRunning("EpicGamesLauncher"))
        {
            SetStatus(status, I18n.T("Abriendo el launcher de {0}...", "Epic Games"), LaunchStatusKind.Info);
            StartProcessAtUserLevel(launcherPath);
            await WaitForProcessWindowAsync("EpicGamesLauncher", TimeSpan.FromSeconds(45));
        }

        SetStatus(status, I18n.T("Iniciando el juego..."), LaunchStatusKind.Info);
        try { log.LogInfo($"GameLauncher: Epic launch URI: {uri}"); } catch { }

        // Se reintenta hasta que aparezca el proceso del juego: si la URI llega antes
        // de que el launcher termine de arrancar/actualizar, se pierde sin aviso.
        var deadline = DateTime.UtcNow.AddSeconds(150);
        while (DateTime.UtcNow < deadline)
        {
            if (IsGameProcessRunning(processes, installPath, exeFileName))
            {
                if (!string.IsNullOrEmpty(exeFileName))
                    processes.ApplyLaunchChainRule(exeFileName);
                // El proceso del juego existe: recién ahora hay partida que optimizar.
                if (gameBoost != null) _ = gameBoost.ApplyAsync();
                SetStatus(status, "", LaunchStatusKind.Hide);
                return;
            }
            SendEpicLaunchUri(uri, log);
            await Task.Delay(10000);
        }

        SetStatus(status, I18n.T("Si el juego no abre, verificá que estés logueado en Epic Games y probá de nuevo."), LaunchStatusKind.Warning);
    }

    /// <summary>
    /// Entrega la URI de Epic al shell del usuario. <c>explorer.exe</c> reenvía el
    /// pedido a la instancia ya abierta del Explorer (que corre sin elevar) y se
    /// cierra: la URI nunca la procesa un proceso elevado. Si por algún motivo
    /// falla, se cae al lanzamiento directo (mejor intentarlo que no hacer nada).
    /// </summary>
    private static void SendEpicLaunchUri(string uri, ILoggingService log)
    {
        if (StartProcessAtUserLevel(uri)) return;
        StartProcessLogged(uri, "", msg =>
        {
            try { log.LogWarning(msg); } catch { }
        });
    }

    /// <summary>
    /// Lanza un target (exe o URI) a través de <c>explorer.exe</c>, para que lo abra
    /// el shell del usuario en vez de este proceso elevado. Devuelve false si ni
    /// siquiera se pudo iniciar explorer (el llamador decide el respaldo).
    /// </summary>
    private static bool StartProcessAtUserLevel(string target)
    {
        if (string.IsNullOrEmpty(target)) return false;
        try
        {
            // Ruta absoluta: no depende del PATH del proceso elevado.
            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new ProcessStartInfo(explorer, $"\"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SetStatus(Action<string, LaunchStatusKind>? status, string message, LaunchStatusKind kind)
        => status?.Invoke(message, kind);

    /// <summary>
    /// Inicia un exe o URI. Devuelve true solo si <see cref="Process.Start(ProcessStartInfo)"/>
    /// no lanzó excepción: es la señal que usa el boost para saber que hay partida.
    /// </summary>
    public static bool StartProcess(string fileName, string arguments)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = true,
                Arguments = arguments
            };
            // El working directory solo aplica a exes reales: para URIs de launcher
            // (steam://, com.epicgames.launcher://) y AUMIDs de Xbox
            // (shell:AppsFolder\...) no hay directorio real.
            if (string.IsNullOrEmpty(arguments) && File.Exists(fileName))
            {
                psi.WorkingDirectory = Path.GetDirectoryName(fileName) ?? "";
            }
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            // El llamador registra el error con su propio logger; acá se evita
            // que un fallo de lanzamiento reviente el flujo.
            System.Diagnostics.Debug.WriteLine($"GameLauncher: iniciar juego: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// ¿El fileName es una URI de protocolo (steam://, com.epicgames.launcher://,
    /// shell:AppsFolder\...) y NO una ruta de archivo local? Usado para validar
    /// lanzamientos sin rechazar URIs: File.Exists solo aplica a exes reales.
    /// </summary>
    private static bool IsProtocolUri(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        // El protocolo shell: de Windows (shell:AppsFolder\{AUMID} para juegos Xbox)
        // NO es una URI válida para Uri.TryCreate: se detecta por prefijo.
        if (fileName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(fileName, UriKind.Absolute, out var parsed) && !parsed.IsFile;
    }

    /// <summary>
    /// Versión con log para diagnóstico: registra qué exe se intenta abrir y el
    /// error si falla (el logging no existe en StartProcess).
    /// </summary>
    public static bool StartProcessLogged(string fileName, string arguments, Action<string>? logWarning)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            logWarning?.Invoke("GameLauncher: StartProcess recibió fileName vacío (el exe no se resolvió).");
            return false;
        }
        // Las URIs de launcher (steam://, com.epicgames.launcher://, shell:) NO son
        // archivos en disco: File.Exists las rechazaría y cortaría el lanzamiento
        // (bug: SMITE y otros juegos de Steam no abrían — "el exe no existe:
        // steam://rungameid/..."). Process.Start con UseShellExecute=true las
        // resuelve contra el registro de protocolos de Windows, igual que Steam.
        if (!IsProtocolUri(fileName) && !File.Exists(fileName))
        {
            logWarning?.Invoke($"GameLauncher: el exe no existe: {fileName}");
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = true,
                Arguments = arguments
            };
            if (string.IsNullOrEmpty(arguments) && !IsProtocolUri(fileName))
            {
                var dir = Path.GetDirectoryName(fileName) ?? "";
                if (Directory.Exists(dir)) psi.WorkingDirectory = dir;
            }
            Process.Start(psi);
            logWarning?.Invoke($"GameLauncher: lanzado OK: {fileName}");
            return true;
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"GameLauncher: ERROR al lanzar {fileName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>¿Hay algún proceso con ese nombre (p. ej. Battle.net) corriendo?</summary>
    public static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ¿El launcher indicado está corriendo? El app de Xbox cambió de nombre varias
    /// veces (XboxStub del app nuevo, Xbox del clásico, GamingApp de versiones
    /// intermedias): se aceptan todos para no quedar congelado en "no iniciado".
    /// Blacksmith también puede tener variaciones.
    /// </summary>
    public static bool IsLauncherRunning(IProcessService processes, string launcherProc)
    {
        return launcherProc switch
        {
            "Xbox" => processes.IsLauncherRunning("XboxStub")
                    || processes.IsLauncherRunning("Xbox")
                    || processes.IsLauncherRunning("GamingApp"),
            "Blacksmith" => processes.IsLauncherRunning("Blacksmith")
                    || processes.IsLauncherRunning("BlacksmithIM"),
            _ => processes.IsLauncherRunning(launcherProc)
        };
    }

    /// <summary>
    /// ¿Hay algún proceso corriendo desde la carpeta de instalación del juego (o
    /// con el nombre del exe detectado)? Sirve para saber si el launcher arrancó
    /// el juego de verdad y no quedó solo en "abriendo".
    /// </summary>
    public static bool IsGameProcessRunning(IProcessService processes, string? installPath, string? exeFileName)
    {
        string? procName = string.IsNullOrEmpty(exeFileName)
            ? null
            : Path.GetFileNameWithoutExtension(exeFileName);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (procName != null
                        && p.ProcessName.StartsWith(procName, StringComparison.OrdinalIgnoreCase))
                        return true;
                    string? f = processes.GetProcessPath(p);
                    if (!string.IsNullOrEmpty(installPath)
                        && !string.IsNullOrEmpty(f)
                        && f.StartsWith(installPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Espera hasta <paramref name="timeout"/> a que aparezca una ventana principal
    /// del proceso (señal de que el launcher terminó de levantar).
    /// </summary>
    public static async Task<bool> WaitForProcessWindowAsync(string processName, TimeSpan timeout)
    {
        // Blacksmith: la ventana del launcher la tiene BlacksmithIM.exe (el binario
        // real); "Blacksmith" es un nombre viejo/inexistente.
        var names = processName.Equals("Blacksmith", StringComparison.OrdinalIgnoreCase)
            ? new[] { "BlacksmithIM", "Blacksmith" }
            : new[] { processName };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var name in names)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) return true;
                    }
                }
            }
            catch { }
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>
    /// Código de producto de Battle.net para lanzar el juego con el launcher
    /// (--exec="launch &lt;código&gt;"): así el launcher se abre solo si no está
    /// corriendo y el juego arranca con su sesión (el exe directo cierra si no hay
    /// launcher activo). Códigos case-sensitive según el esquema oficial del launcher.
    /// </summary>
    public static string? GetBlizzardProductCode(string exeFileName)
    {
        return exeFileName.ToLowerInvariant() switch
        {
            "hearthstone.exe" or "hearthstone beta launcher.exe" or "hearthstone launcher.exe" => "WTCG",
            "wow.exe" or "wow-64.exe" or "wowclassic.exe" or "wowclassict.exe" => "WoW",
            "warcraft iii.exe" or "war3.exe" or "warcraft iii launcher.exe" => "W3",
            "diablo iv.exe" or "diablo4.exe" => "Fen",
            "diablo iii.exe" or "diablo3.exe" => "D3",
            "d2r.exe" => "OSI",
            "diablo immortal.exe" or "diabloimmortal.exe" => "ANBS",
            "diablo.exe" => "D1",
            "overwatch.exe" or "overwatch 2.exe" or "overwatch2.exe" => "Pro",
            "sc2_x64.exe" or "sc2.exe" or "starcraft ii.exe" => "S2",
            "starcraft.exe" => "S1",
            "heroesofthestorm_x64.exe" or "heroes of the storm.exe" => "Hero",
            "blackopscoldwar.exe" => "ZEUS",
            "blackops4.exe" or "blackops.exe" => "VIPR",
            "vanguard.exe" => "FORE",
            "modernwarfare.exe" => "ODIN",
            "cod.exe" => "AUKS",
            _ => null
        };
    }

    /// <summary>Busca el ejecutable del launcher de Battle.net en las rutas típicas de instalación.</summary>
    public static string? FindBattleNetLauncher()
    {
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        })
        {
            try
            {
                var exe = Path.Combine(root, "Battle.net", "Battle.net.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Busca el ejecutable del launcher de GOG Galaxy (registro o rutas típicas).</summary>
    public static string? FindGogLauncher()
    {
        // El registro del propio Galaxy apunta a su carpeta de instalación.
        try
        {
            using var reg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths");
            var dir = reg?.GetValue("Client") as string;
            if (!string.IsNullOrEmpty(dir))
            {
                var exe = Path.Combine(dir, "GalaxyClient.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        })
        {
            try
            {
                var exe = Path.Combine(root, "GOG Galaxy", "GalaxyClient.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// URI del launcher de Epic para lanzar un juego:
    /// <c>com.epicgames.launcher://apps/{namespace}%3A{catalogItemId}%3A{appName}?action=launch&amp;silent=true</c>.
    ///
    /// Es la forma que usan los accesos directos que crea Epic y la única que aceptan
    /// las versiones nuevas del launcher: la forma corta (apps/{AppName}) quedó
    /// obsoleta y el botón Iniciar no hacía nada. Si al manifest le faltan los datos
    /// del catálogo (instalaciones viejas) se cae a la forma corta, que sigue
    /// funcionando en algunas versiones, antes que no intentar nada.
    /// </summary>
    public static string BuildEpicLaunchUri(string? catalogNamespace, string? catalogItemId, string? appName)
    {
        string app = (appName ?? "").Trim();
        string ns = (catalogNamespace ?? "").Trim();
        string item = (catalogItemId ?? "").Trim();

        string apps = !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(item) && !string.IsNullOrEmpty(app)
            ? $"{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(item)}%3A{Uri.EscapeDataString(app)}"
            : Uri.EscapeDataString(app);

        return $"com.epicgames.launcher://apps/{apps}?action=launch&silent=true";
    }

    /// <summary>Busca el ejecutable del launcher de Epic Games en las rutas típicas de instalación.</summary>
    public static string? FindEpicLauncher()
    {
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            try
            {
                var exe = Path.Combine(root, "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Busca el ejecutable del Riot Client (RiotClientServices.exe). Sale del
    /// registro "Riot Games Install Directory" (default C:\Riot Games), igual que
    /// la detección de juegos en InstalledGamesService.
    /// </summary>
    public static string? FindRiotLauncher()
    {
        try
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Riot Games", @"SOFTWARE\Riot Games" })
            {
                using var reg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                var dir = reg?.GetValue("Riot Games Install Directory") as string;
                if (!string.IsNullOrEmpty(dir))
                {
                    var exe = Path.Combine(dir, "Riot Client", "RiotClientServices.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch { }
        // Default típico: raíz del disco del sistema.
        var def = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Riot Games", "Riot Client", "RiotClientServices.exe");
        return File.Exists(def) ? def : null;
    }

    /// <summary>
    /// Busca el ejecutable del launcher Blacksmith (launcher oficial de Dark and Darker
    /// de Ironmace). El binario real del launcher es BlacksmithIM.exe (el proceso que
    /// aparece en Task Manager); "Blacksmith.exe" se mantiene como variante clásica.
    /// Se instala en IRONMACE\Blacksmith (raíz de unidad, Program Files o LocalAppData);
    /// también se acepta lo que apunte el registro de desinstalación.
    /// </summary>
    public static string? FindBlacksmithLauncher()
    {
        var exeNames = new[] { "BlacksmithIM.exe", "Blacksmith.exe" };

        // Raíces a revisar: Program Files, Program Files (x86), LocalAppData y cada
        // unidad de disco fijo (el launcher suele ir en la raíz, ej. C:\IRONMACE\Blacksmith).
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed)
                    roots.Add(drive.Name.TrimEnd('\\'));
            }
        }
        catch { }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var dirs = new[]
                {
                    Path.Combine(root, "IRONMACE", "Blacksmith"),
                    Path.Combine(root, "Ironmace", "Blacksmith"),
                    Path.Combine(root, "IRONMACE"),
                    Path.Combine(root, "Blacksmith")
                };
                foreach (var dir in dirs)
                {
                    foreach (var exeName in exeNames)
                    {
                        var f = Path.Combine(dir, exeName);
                        if (File.Exists(f)) return f;
                    }
                }
            }
            catch { }
        }

        // Clave propia del launcher (si la deja).
        try
        {
            foreach (var keyPath in new[]
            {
                @"SOFTWARE\Ironmace\Blacksmith",
                @"SOFTWARE\WOW6432Node\Ironmace\Blacksmith"
            })
            {
                using var reg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
                var installDir = reg?.GetValue("InstallDir") as string
                    ?? reg?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installDir))
                {
                    foreach (var exeName in exeNames)
                    {
                        var f = Path.Combine(installDir, exeName);
                        if (File.Exists(f)) return f;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Detecta si un juego manual es Dark and Darker basándose en la ruta de instalación
    /// o el nombre del ejecutable. Dark and Darker requiere el launcher Blacksmith para funcionar.
    /// </summary>
    public static bool IsDarkAndDarker(string? installPath, string? exeFileName)
    {
        // Detectar por ruta de instalación
        if (!string.IsNullOrEmpty(installPath))
        {
            var pathLower = installPath.ToLowerInvariant();
            if (pathLower.Contains("dark and darker") ||
                pathLower.Contains("darkanddarker") ||
                pathLower.Contains("ironmace") ||
                pathLower.Contains("dungeoncrawler"))
            {
                return true;
            }
        }

        // Detectar por nombre del ejecutable
        if (!string.IsNullOrEmpty(exeFileName))
        {
            var exeLower = exeFileName.ToLowerInvariant();
            // El exe real del juego es DungeonCrawler.exe (proyecto Unreal);
            // "DarkAndDarker.exe" era el nombre de builds viejos.
            if (exeLower.Contains("dungeoncrawler") ||
                exeLower.Contains("darkanddarker") ||
                exeLower.Contains("dark_and_darker"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Busca el exe real del juego: primero directo en la carpeta de instalación y,
    /// si no, recursivo hasta 4 niveles (los exes modernos van anidados, ej.
    /// CS2\game\bin\win64\cs2.exe). Misma lógica que la biblioteca de juegos.
    /// </summary>
    public static string? FindExePath(string installPath, string exeFileName)
    {
        if (string.IsNullOrEmpty(installPath) || string.IsNullOrEmpty(exeFileName)) return null;
        string direct = Path.Combine(installPath, exeFileName);
        if (File.Exists(direct)) return direct;
        try
        {
            int budget = 800;
            return FindFileInTree(installPath, exeFileName, 0, ref budget);
        }
        catch { return null; }
    }

    private static string? FindFileInTree(string dir, string fileName, int depth, ref int budget)
    {
        if (depth > 4 || budget <= 0) return null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, fileName, SearchOption.TopDirectoryOnly))
            {
                if (budget-- <= 0) return null;
                return f;
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if (budget-- <= 0) return null;
                var r = FindFileInTree(d, fileName, depth + 1, ref budget);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }
}
