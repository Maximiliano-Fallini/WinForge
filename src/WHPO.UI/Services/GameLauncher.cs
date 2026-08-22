using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;

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

        // Optimización de procesos al iniciar un juego (BETA): no hace nada si el
        // switch está desactivado. Se ejecuta en background para no retrasar el juego.
        if (gameBoost != null) _ = gameBoost.ApplyAsync();

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
            StartProcess(fileName, arguments);
            if (!string.IsNullOrEmpty(exeFileName))
                processes.ApplyLaunchChainRule(exeFileName);
            return;
        }

        if (blizzardCode == null && !epic)
        {
            StartProcess(fileName, arguments);
            // Aplica la regla guardada mientras el juego arranca (los launchers con
            // anti-cheat viven poco y el proceso real está protegido: aplicarle la
            // prioridad de CPU al launcher hace que el juego la herede al nacer).
            if (!string.IsNullOrEmpty(exeFileName))
                processes.ApplyLaunchChainRule(exeFileName);
            return;
        }

        // Juegos de Battle.net y Epic: el launcher tiene que estar corriendo antes
        // de lanzar el juego. Blizzard necesita la sesión del launcher; Epic, el
        // servicio de Epic Online Services (sin él, el juego abre pero lo online
        // no funciona: "requiere iniciar Epic Games").
        var launcherPath = blizzardCode != null ? FindBattleNetLauncher() : FindEpicLauncher();
        string launcherName = blizzardCode != null ? "Battle.net" : "Epic Games";
        string launcherProc = blizzardCode != null ? "Battle.net" : "EpicGamesLauncher";
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

        if (blizzardCode != null)
        {
            // El lanzamiento lo hace el launcher. Si el comando se manda antes de
            // que el launcher esté listo, se pierde y solo abre la página del juego:
            // se reintenta hasta que el juego aparezca.
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                if (IsGameProcessRunning(processes, installPath, exeFileName))
                {
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
            return;
        }

        // Epic: el lanzamiento lo hace el launcher por su URI (como un acceso
        // directo de la biblioteca): así autentica el juego con EOS. Se reintenta
        // hasta que el juego aparezca (igual que Battle.net).
        var epDeadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < epDeadline)
        {
            if (IsGameProcessRunning(processes, installPath, exeFileName))
            {
                if (!string.IsNullOrEmpty(exeFileName))
                    processes.ApplyLaunchChainRule(exeFileName);
                SetStatus(status, "", LaunchStatusKind.Hide);
                return;
            }
            // Igual que Battle.net: si el usuario cerró el launcher, cancelar en
            // vez de seguir reintentando (el comando de la URI lo reabriría).
            if (!IsProcessRunning(launcherProc))
            {
                SetStatus(status, I18n.T("Se cerró el launcher de {0} — se cancela el lanzamiento.", launcherName), LaunchStatusKind.Warning);
                return;
            }
            StartProcess(fileName, arguments);
            await Task.Delay(10000);
        }
        SetStatus(status, I18n.T("Si el juego no abre, verificá que estés logueado en Epic Games y probá de nuevo."), LaunchStatusKind.Warning);
    }

    private static void SetStatus(Action<string, LaunchStatusKind>? status, string message, LaunchStatusKind kind)
        => status?.Invoke(message, kind);

    public static void StartProcess(string fileName, string arguments)
    {
        if (string.IsNullOrEmpty(fileName)) return;
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
        }
        catch (Exception ex)
        {
            // El llamador registra el error con su propio logger; acá se evita
            // que un fallo de lanzamiento reviente el flujo.
            System.Diagnostics.Debug.WriteLine($"GameLauncher: iniciar juego: {ex.Message}");
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
    /// </summary>
    public static bool IsLauncherRunning(IProcessService processes, string launcherProc)
    {
        return launcherProc switch
        {
            "Xbox" => processes.IsLauncherRunning("XboxStub")
                    || processes.IsLauncherRunning("Xbox")
                    || processes.IsLauncherRunning("GamingApp"),
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
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    if (p.MainWindowHandle != IntPtr.Zero) return true;
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
