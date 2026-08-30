using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Detecta juegos instalados leyendo las bibliotecas de los launchers:
///  - Steam: appmanifest_*.acf en cada carpeta steamapps (libraryfolders.vdf) con
///    nombre + installdir; el ejecutable se busca en la carpeta del juego.
///  - Epic: Manifests/*.item (JSON) con DisplayName + LaunchExecutable.
///  - GOG: registro de desinstalación (Publisher "GOG.com") + clave GOG.com\Games.
///  - Xbox/Game Pass: paquetes MSIX de la Store con ejecutable grande.
/// Todo es best-effort: si un launcher no existe o un juego no tiene ejecutable,
/// simplemente no aparece (o aparece sin exe, que se usa solo para matchear).
/// </summary>
public sealed class InstalledGamesService : IInstalledGamesService
{
    private readonly ILoggingService _logging;

    // Caché de la biblioteca: la primera vez se escanea y se guarda (memoria + disco),
    // así abrir la página de juegos (o el escaneo de arranque de ProcessService) no
    // re-escanea los launchers en cada visita. Un escaneo nuevo solo ocurre con
    // refresh=true (el botón "Re-detectar" de la biblioteca).
    private readonly object _lock = new();
    private List<InstalledGame>? _cache;
    private Task<List<InstalledGame>>? _scanTask;

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WHPO");
    private static readonly string CacheFile = Path.Combine(CacheDir, "gamescache.json");

    public bool HasCachedResult
    {
        get { lock (_lock) return _cache != null; }
    }

    /// <summary>
    /// Borra la caché de juegos instalados (memoria + archivo en disco): la próxima
    /// consulta re-escanea los launchers desde cero. Si hay un escaneo en curso, este
    /// termina y rellena la caché nueva (no se cancela: es una corrida compartida).
    /// </summary>
    public void ClearCache()
    {
        lock (_lock)
        {
            _cache = null;
        }
        try
        {
            if (File.Exists(CacheFile)) File.Delete(CacheFile);
            var tmp = CacheFile + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"InstalledGames: limpiar caché: {ex.Message}");
        }
    }

    public InstalledGamesService(ILoggingService logging)
    {
        _logging = logging;
    }

    public Task<List<InstalledGame>> GetInstalledGamesAsync()
        => GetInstalledGamesAsync(refresh: false);

    public Task<List<InstalledGame>> GetInstalledGamesAsync(bool refresh)
    {
        lock (_lock)
        {
            // Sin reescaneo: devolver lo que ya haya (memoria o disco).
            if (!refresh && _cache != null)
                return Task.FromResult(_cache);
            if (!refresh && _cache == null)
            {
                var disk = LoadCacheFromDisk();
                if (disk != null)
                {
                    _cache = disk;
                    return Task.FromResult(disk);
                }
            }

            // Reescaneo (o primera vez): una sola corrida compartida por todos los
            // llamadores (el escaneo de arranque de ProcessService y la página).
            if (_scanTask == null)
                _scanTask = Task.Run(RunScan);
            return _scanTask;
        }
    }

    private List<InstalledGame> RunScan()
    {
        var games = ScanAll();
        lock (_lock)
        {
            _cache = games;
            _scanTask = null;
            SaveCacheToDisk(games);
        }
        return games;
    }

    private List<InstalledGame> ScanAll()
    {
        var games = new List<InstalledGame>();
        try { games.AddRange(ScanSteam()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Steam: {ex.Message}"); }
        try { games.AddRange(ScanEpic()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Epic: {ex.Message}"); }
        try { games.AddRange(ScanUbisoft()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Ubisoft: {ex.Message}"); }
        try { games.AddRange(ScanEaGames()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: EA: {ex.Message}"); }
        try { games.AddRange(ScanBattleNet()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Battle.net: {ex.Message}"); }
        try { games.AddRange(ScanGog()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: GOG: {ex.Message}"); }
        try { games.AddRange(ScanXbox()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Xbox: {ex.Message}"); }
        try { games.AddRange(ScanRiot()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Riot: {ex.Message}"); }
        try { games.AddRange(ScanBlacksmith()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Blacksmith: {ex.Message}"); }
        try { games.AddRange(ScanStandalone()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: independientes: {ex.Message}"); }
        // Llave maestra: ningún launcher/instalador/anti-cheat puede ser un juego.
        // Si cualquier scanner resolviera un stub como exe (ej. BlacksmithBootstrap.exe
        // del launcher de Dark and Darker), se descarta acá, pase lo que pase.
        return games
            .Where(g => !GameExeResolver.IsStubExeName(g.ExeFileName))
            .GroupBy(g => g.ExeFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    // ===================== Caché en disco =====================

    private void SaveCacheToDisk(List<InstalledGame> games)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var json = JsonSerializer.Serialize(games);
            // Escritura atómica (tmp + move): un corte no deja el caché a medias.
            var tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"InstalledGames: guardar caché: {ex.Message}");
        }
    }

    private List<InstalledGame>? LoadCacheFromDisk()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var json = File.ReadAllText(CacheFile);
            var list = JsonSerializer.Deserialize<List<InstalledGame>>(json);
            if (list == null) return null;
            // Filtrar entradas basura por si el archivo está corrupto o quedó a medias.
            var valid = list.Where(g => !string.IsNullOrEmpty(g.Name)).ToList();
            // Caché vieja con una detección incorrecta del exe principal: si algún
            // juego quedó con un stub de anti-cheat/consola como exe (ej. SMITE 2 →
            // start_protected_game.exe de EAC, CS2 → vconsole2.exe), descartarla y
            // re-escannear UNA vez con el resolver de stubs para re-derivar el exe real.
            foreach (var g in valid)
            {
                if (string.IsNullOrEmpty(g.ExeFileName) || string.IsNullOrEmpty(g.InstallPath)) continue;
                try
                {
                    if (GameExeResolver.IsMisdetectedStubExe(Path.Combine(g.InstallPath, g.ExeFileName)))
                        return null;
                }
                catch { }
            }
            // Blacksmith: si el exe cacheado (ej. DarkAndDarker.exe de detecciones
            // viejas) ya no existe en la carpeta del juego, descartar la caché y
            // re-escannear: el exe real es DungeonCrawler.exe y el badge "En
            // ejecución" matchea por nombre exacto del proceso.
            if (valid.Any(g =>
                    string.Equals(g.Launcher, "Blacksmith", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(g.ExeFileName)
                    && !string.IsNullOrEmpty(g.InstallPath)
                    && !ExeExistsInTree(g.InstallPath, g.ExeFileName)))
                return null;
            // Quitar entradas viejas de "BlueStacks X" que hayan quedado en cachés
            // anteriores: es el cliente web/tienda (0,5 MB), no el emulador; no debe
            // aparecer en la biblioteca.
            valid.RemoveAll(g => string.Equals(g.ExeFileName, "BlueStacks X.exe", StringComparison.OrdinalIgnoreCase));
            // Llave maestra de la caché: descartar cualquier entrada cuyo exe sea un
            // stub/launcher (ej. BlacksmithBootstrap.exe). Aunque el archivo exista
            // en el árbol (el launcher sigue instalado), no es el juego.
            valid.RemoveAll(g => GameExeResolver.IsStubExeName(g.ExeFileName));
            // Migración de detección: la caché vieja no conoce BlueStacks (la
            // detección se agregó después). Si está instalado, sumarlo en memoria
            // sin re-escannear: aparece en la biblioteca aunque la caché sea vieja.
            var foundExes = new HashSet<string>(
                valid.Select(g => g.ExeFileName).Where(e => !string.IsNullOrEmpty(e)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var bs in new[]
            {
                (Name: "BlueStacks 5", Exe: "HD-Player.exe",
                 Keys: new[] { @"SOFTWARE\BlueStacks_nxt", @"SOFTWARE\WOW6432Node\BlueStacks_nxt" },
                 Dirs: new[] { "BlueStacks_nxt", "BlueStacks" })
            })
            {
                if (foundExes.Contains(bs.Exe)) continue;
                string? dir = FindBlueStacksDir(bs.Keys, bs.Dirs);
                if (dir == null) continue;
                string full = Path.Combine(dir, bs.Exe);
                if (!File.Exists(full)) continue;
                valid.Add(new InstalledGame(bs.Name, bs.Exe, "Independiente", dir));
            }
            return valid;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"InstalledGames: leer caché: {ex.Message}");
            return null;
        }
    }

    /// <summary>¿Existe un archivo con ese nombre en algún nivel de la carpeta de instalación?</summary>
    private static bool ExeExistsInTree(string installPath, string exeFileName)
    {
        try
        {
            return Directory.EnumerateFiles(installPath, exeFileName, SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    // ===================== Steam =====================

    private static readonly string[] SteamRegistryKeys =
    {
        @"SOFTWARE\WOW6432Node\Valve\Steam",
        @"SOFTWARE\Valve\Steam"
    };

    private List<InstalledGame> ScanSteam()
    {
        var games = new List<InstalledGame>();

        // Ruta de Steam desde el registro (o fallback típico).
        string? steamPath = null;
        foreach (var key in SteamRegistryKeys)
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                steamPath = reg?.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(steamPath)) break;
            }
            catch { }
        }
        if (string.IsNullOrEmpty(steamPath))
            steamPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (!Directory.Exists(steamPath)) return games;

        // Carpetas de biblioteca: la base de Steam + las de libraryfolders.vdf.
        var libraryRoots = new List<string> { steamPath };
        try
        {
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (var line in File.ReadAllLines(vdf))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, "\"path\"\\s+\"([^\"]+)\"");
                    if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                    {
                        var p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (Directory.Exists(p)) libraryRoots.Add(p);
                    }
                }
            }
        }
        catch { }

        foreach (var root in libraryRoots)
        {
            var steamapps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"); }
            catch { continue; }

            foreach (var manifest in manifests)
            {
                try
                {
                    var (name, installDir, appId) = ParseAcf(manifest);
                    if (string.IsNullOrEmpty(name)) continue;
                    string gameDir = string.IsNullOrEmpty(installDir)
                        ? ""
                        : Path.Combine(steamapps, "common", installDir);
                    var exe = string.IsNullOrEmpty(gameDir) ? null : FindMainExe(gameDir, installDir ?? "");
                    // Banner del juego desde el CDN público de Steam (header.jpg por appid).
                    string bannerUrl = string.IsNullOrEmpty(appId)
                        ? ""
                        : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                    games.Add(new InstalledGame(name, exe ?? "", "Steam", gameDir, appId ?? "", bannerUrl));
                }
                catch (Exception ex)
                {
                    _logging.LogWarning($"InstalledGames: Steam manifest {Path.GetFileName(manifest)}: {ex.Message}");
                }
            }
        }
        return games;
    }

    /// <summary>Parsea un appmanifest.acf (formato KeyValues simple) y devuelve name, installdir y appid.</summary>
    private static (string? name, string? installDir, string? appId) ParseAcf(string path)
    {
        string? name = null, installDir = null, appId = null;
        foreach (var line in File.ReadAllLines(path))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, "\"([^\"]+)\"\\s+\"([^\"]*)\"");
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Value;
            if (key == "name" && name == null) name = value;
            else if (key == "installdir" && installDir == null) installDir = value;
            else if (key == "appid" && appId == null) appId = value;
            if (name != null && installDir != null && appId != null) break;
        }
        return (name, installDir, appId);
    }

    /// <summary>
    /// Busca el ejecutable principal del juego en su carpeta: delega en el resolver
    /// compartido (GameExeResolver.FindMainExePath), que prefiere el exe cuyo nombre
    /// coincide con la carpeta del juego y, si no, el más grande que NO sea un stub
    /// (anti-cheat, consolas, instaladores, crash handlers…). Así biblioteca, bandeja
    /// e íconos resuelven SIEMPRE el mismo exe (antes la biblioteca y los íconos
    /// usaban lógicas distintas y terminaban con nombres diferentes para el mismo
    /// juego — ej. SMITE 2 → Hemingway.exe vs Hemingway-Win64-Shipping.exe).
    /// </summary>
    private static string? FindMainExe(string gameDir, string installDirName)
    {
        var p = GameExeResolver.FindMainExePath(gameDir);
        return p != null ? Path.GetFileName(p) : null;
    }

    // ===================== Ubisoft Connect =====================

    private List<InstalledGame> ScanUbisoft()
    {
        var games = new List<InstalledGame>();
        string[] keys =
        {
            @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs",
            @"SOFTWARE\Ubisoft\Launcher\Installs"
        };
        foreach (var key in keys)
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                if (reg == null) continue;
                foreach (var sub in reg.GetSubKeyNames())
                {
                    try
                    {
                        using var gameKey = reg.OpenSubKey(sub);
                        var installDir = gameKey?.GetValue("InstallDir") as string;
                        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) continue;
                        string name = Path.GetFileName(installDir.TrimEnd('\\'));
                        var exe = FindMainExe(installDir, name);
                        games.Add(new InstalledGame(name, exe ?? "", "Ubisoft", installDir));
                    }
                    catch { }
                }
            }
            catch { }
        }
        return games;
    }

    // ===================== Juegos independientes (Roblox, Minecraft, Genshin, Warframe) =====================

    /// <summary>
    /// Detecta juegos que no pasan por Steam/Epic/Ubisoft/EA/Blizzard: instalan su
    /// propio launcher (Roblox, Minecraft, Genshin Impact, Warframe…). Usa dos fuentes:
    ///  - Registro de desinstalación (Uninstall): DisplayName conocido + InstallLocation.
    ///  - Rutas típicas de instalación para los que no se registran (Roblox/Minecraft).
    /// El exe detectado coincide con el proceso real en ejecución, así las reglas de
    /// prioridad/afinidad se aplican igual que en los juegos de launchers.
    /// </summary>
    private List<InstalledGame> ScanStandalone()
    {
        var games = new List<InstalledGame>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string? exePath, string installPath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;
            if (!found.Add(exePath)) return;

            // Nunca listar binarios del launcher Blacksmith ni stubs como juego:
            // BlacksmithIM/Debris/etc. son el launcher de Dark and Darker, no un
            // juego, y el filtro de stubs evita cualquier otro helper (crash
            // handlers, instaladores…) que aparezca en las rutas conocidas.
            if (GameExeResolver.IsStubExe(exePath)) return;

            // Ignorar juegos que pertenecen a Blacksmith/Dark and Darker (ya detectados por ScanBlacksmith)
            var pathLower = (exePath ?? "").ToLowerInvariant();
            var nameLower = (name ?? "").ToLowerInvariant();
            if (pathLower.Contains("dark and darker") ||
                pathLower.Contains("darkanddarker") ||
                pathLower.Contains("ironmace") ||
                pathLower.Contains("blacksmith") ||
                pathLower.Contains("dungeoncrawler") ||
                nameLower.Contains("dark and darker") ||
                nameLower.Contains("darkanddarker") ||
                nameLower.Contains("ironmace") ||
                nameLower.Contains("blacksmith") ||
                nameLower.Contains("tavernworker"))
            {
                return;
            }

            games.Add(new InstalledGame(name, Path.GetFileName(exePath), "Independiente", installPath));
        }

        /// <summary>
        /// Detecta una variante de BlueStacks: primero el InstallDir del registro (ej.
        /// HKLM\SOFTWARE\BlueStacks_nxt\InstallDir), con respaldo de las rutas típicas
        /// de instalación en Program Files / Program Files (x86). El exe es el proceso
        /// real del jugador, así las reglas de prioridad/afinidad aplican igual que en
        /// los juegos de launchers y el badge "En ejecución" funciona por WMI.
        /// </summary>
        void AddBlueStacks(string name, string exeName, string[] regKeys, string[] fallbackDirs)
        {
            string? dir = FindBlueStacksDir(regKeys, fallbackDirs);
            if (dir == null) return;
            Add(name, Path.Combine(dir, exeName), dir);
        }

        // ==== 1) Registro de desinstalación (fuente más confiable para Genshin/Warframe) ====
        string[] uninstallRoots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in uninstallRoots)
            {
                try
                {
                    using var reg = hive.OpenSubKey(root);
                    if (reg == null) continue;
                    foreach (var sub in reg.GetSubKeyNames())
                    {
                        try
                        {
                            using var app = reg.OpenSubKey(sub);
                            var displayName = app?.GetValue("DisplayName") as string;
                            var installLoc = app?.GetValue("InstallLocation") as string;
                            if (string.IsNullOrEmpty(displayName)) continue;
                            string k = displayName.ToLowerInvariant();
                            if (k.Contains("roblox"))
                            {
                                // Roblox instala por usuario (%LOCALAPPDATA%\Roblox) o por
                                // máquina (Program Files). Si existen ambas, preferir la
                                // per-user: es la instalación moderna que el launcher usa
                                // de verdad; la de Program Files es la legada (MSI viejo).
                                string? exe = FindStandaloneExe(installLoc ?? "", "RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe");
                                string? useLoc = installLoc;
                                if (exe != null && !string.IsNullOrEmpty(installLoc) &&
                                    installLoc.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
                                {
                                    var laVersions = Path.Combine(
                                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        "Roblox", "Versions");
                                    var laExe = FindStandaloneExe(laVersions, "RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe");
                                    if (laExe != null) { exe = laExe; useLoc = Path.GetDirectoryName(laExe); }
                                }
                                Add("Roblox", exe, useLoc ?? "");
                            }
                            else if (k.Contains("minecraft"))
                                Add("Minecraft", FindStandaloneExe(installLoc ?? "", "MinecraftLauncher.exe", "Minecraft.exe"), installLoc ?? "");
                            else if (k.Contains("genshin"))
                                Add("Genshin Impact", FindStandaloneExe(installLoc ?? "", "GenshinImpact.exe", "launcher.exe"), installLoc ?? "");
                            else if (k.Contains("warframe"))
                                Add("Warframe", FindStandaloneExe(installLoc ?? "", "Warframe.x64.exe", "Warframe.exe", "Launcher.exe"), installLoc ?? "");
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // ==== 2) Rutas conocidas (juegos que no siempre se registran en Uninstall) ====

        // Roblox: %LOCALAPPDATA%\Roblox\Versions\<version>\RobloxPlayerBeta.exe
        // (y la instalación clásica en Program Files (x86)).
        foreach (var versionsRoot in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions")
        })
        {
            try
            {
                if (!Directory.Exists(versionsRoot)) continue;
                // Buscar en TODAS las versiones la que tenga RobloxPlayerBeta.exe
                // (puede haber carpetas solo de Studio); tomar la más reciente que sí.
                var verDir = Directory.EnumerateDirectories(versionsRoot)
                    .Where(d => File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe")))
                    .OrderByDescending(d =>
                    {
                        try { return new DirectoryInfo(d).LastWriteTimeUtc; } catch { return DateTime.MinValue; }
                    })
                    .FirstOrDefault();
                if (verDir != null)
                    Add("Roblox", Path.Combine(verDir, "RobloxPlayerBeta.exe"), verDir);
            }
            catch { }
        }

        // Minecraft launcher clásico (Java): %ProgramFiles(x86)%\Minecraft Launcher\MinecraftLauncher.exe
        var mcLauncher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe");
        Add("Minecraft", mcLauncher, Path.GetDirectoryName(mcLauncher) ?? "");

        // Genshin / Warframe en Program Files si no aparecieron por registro.
        foreach (var pf in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(pf))
                {
                    var n = Path.GetFileName(dir);
                    if (n.Contains("Genshin", StringComparison.OrdinalIgnoreCase))
                        Add("Genshin Impact", FindStandaloneExe(dir, "GenshinImpact.exe", "launcher.exe"), dir);
                    else if (n.Contains("Warframe", StringComparison.OrdinalIgnoreCase))
                        Add("Warframe", FindStandaloneExe(dir, "Warframe.x64.exe", "Warframe.exe", "Launcher.exe"), dir);
                }
            }
            catch { }
        }

        // ==== 3) BlueStacks (emulador Android) ====
        // No pasa por ningún launcher de juegos: instala su propio emulador. Se
        // detecta por el registro (HKLM\SOFTWARE\BlueStacks_nxt → InstallDir) con
        // respaldo de rutas típicas. Solo se detecta BlueStacks 5 (HD-Player.exe,
        // el emulador local: el proceso real que corre los juegos Android). La
        // variante "BlueStacks X" NO se detecta a propósito: su exe es en realidad
        // "BlueStacks Store" (un cliente web/tienda de ~0,5 MB), no un emulador.
        // Queda como "Independiente" (sin logo de launcher): la card lanza el exe
        // directo y el ícono del exe es el logo de BlueStacks.
        AddBlueStacks("BlueStacks 5", "HD-Player.exe",
            new[] { @"SOFTWARE\BlueStacks_nxt", @"SOFTWARE\WOW6432Node\BlueStacks_nxt" },
            new[] { "BlueStacks_nxt", "BlueStacks" });

        return games;
    }

    /// <summary>
    /// Carpeta de instalación de una variante de BlueStacks: primero el InstallDir
    /// del registro (ej. HKLM\SOFTWARE\BlueStacks_nxt), con respaldo de las rutas
    /// típicas en Program Files / Program Files (x86). Null si no está instalado.
    /// </summary>
    private static string? FindBlueStacksDir(string[] regKeys, string[] fallbackDirs)
    {
        foreach (var key in regKeys)
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                var v = reg?.GetValue("InstallDir") as string ?? reg?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(v) && Directory.Exists(v))
                    return v.TrimEnd('\\');
            }
            catch { }
        }
        foreach (var pf in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            foreach (var f in fallbackDirs)
            {
                var cand = Path.Combine(pf, f);
                if (Directory.Exists(cand)) return cand;
            }
        }
        return null;
    }

    /// <summary>Busca un exe específico (candidatos en orden) en la carpeta y un nivel adentro (ej. carpeta "Game").</summary>
    private static string? FindStandaloneExe(string dir, params string[] candidates)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        try
        {
            foreach (var c in candidates)
            {
                var f = Path.Combine(dir, c);
                if (File.Exists(f)) return f;
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                foreach (var c in candidates)
                {
                    var f = Path.Combine(sub, c);
                    if (File.Exists(f)) return f;
                }
            }
            return null;
        }
        catch { return null; }
    }

    // ===================== Blizzard / Battle.net =====================

    // Mapa código de producto Battle.net → id del box art del launcher, para armar la
    // URL oficial https://bnetxboxassets.akamaized.net/{id}/box-enUS.webp (CDN Akamai,
    // el mismo que usa el launcher para los tiles de la biblioteca). La mayoría
    // coincide con el código en minúsculas; los que difieren se mapean explícitamente.
    private static readonly Dictionary<string, string> BlizzardBoxArtIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WTCG"] = "hsb",      // Hearthstone (el código de lanzamiento es WTCG)
        ["Fen"] = "fenris",    // Diablo IV
        ["D1"] = "drtl",       // Diablo (clásico)
        ["VIPR"] = "viper",    // CoD: Black Ops 4
        ["WoW"] = "wow",
        ["W3"] = "w3",
        ["D3"] = "d3",
        ["OSI"] = "osi",
        ["ANBS"] = "anbs",
        ["Pro"] = "pro",
        ["S2"] = "s2",
        ["S1"] = "s1",
        ["Hero"] = "hero",
        ["ZEUS"] = "zeus",
        ["FORE"] = "fore",
        ["ODIN"] = "odin",
        ["AUKS"] = "auks"
    };

    /// <summary>URL del box art del launcher para un código de producto Battle.net (o null si no hay).</summary>
    private static string? GetBlizzardBannerUrl(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        if (!BlizzardBoxArtIds.TryGetValue(code, out var boxId)) return null;
        return $"https://bnetxboxassets.akamaized.net/{boxId}/box-enUS.webp";
    }

    private List<InstalledGame> ScanBattleNet()
    {
        var games = new List<InstalledGame>();
        var db = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Battle.net", "Agent", "product.db");
        if (!File.Exists(db)) return games;

        // Lectura de solo lectura: Battle.net puede estar corriendo con el archivo
        // abierto. Si está a mitad de una actualización, SQLite devuelve "file is
        // not a database" (SQLITE_NOTADB): se reintenta antes de descartar la base
        // (el escaneo por carpetas cubre el resto igual).
        List<InstalledGame> ReadDb()
        {
            var result = new List<InstalledGame>();
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            // Si la tabla tiene columna de código de producto, se usa para lanzar el
            // juego vía --exec="launch <código>" sin depender del mapeo por nombre.
            bool hasCode = false;
            try
            {
                using var schema = conn.CreateCommand();
                schema.CommandText = "PRAGMA table_info(product)";
                using var schemaReader = schema.ExecuteReader();
                while (schemaReader.Read())
                {
                    if (string.Equals(schemaReader.GetString(1), "code", StringComparison.OrdinalIgnoreCase))
                    {
                        hasCode = true;
                        break;
                    }
                }
            }
            catch { }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = hasCode
                ? "SELECT code, name, install_path FROM product WHERE install_path IS NOT NULL AND install_path != ''"
                : "SELECT name, install_path FROM product WHERE install_path IS NOT NULL AND install_path != ''";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (hasCode)
                    {
                        string? code = reader.IsDBNull(0) ? null : reader.GetString(0);
                        string? name = reader.IsDBNull(1) ? null : reader.GetString(1);
                        string? installPath = reader.IsDBNull(2) ? null : reader.GetString(2);
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
                            continue;
                        var exe = FindMainExe(installPath, name);
                        result.Add(new InstalledGame(name, exe ?? "", "Blizzard", installPath, code ?? "", GetBlizzardBannerUrl(code)));
                    }
                    else
                    {
                        string? name = reader.IsDBNull(0) ? null : reader.GetString(0);
                        string? installPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
                            continue;
                        var exe = FindMainExe(installPath, name);
                        result.Add(new InstalledGame(name, exe ?? "", "Blizzard", installPath));
                    }
                }
                catch { }
            }
            return result;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                games.AddRange(ReadDb());
                break;
            }
            catch (Exception ex)
            {
                if (attempt < 2)
                {
                    System.Threading.Thread.Sleep(400 * (attempt + 1));
                    continue;
                }
                _logging.LogWarning($"InstalledGames: Battle.net product.db: {ex.Message}");
            }
        }

        // Battle.net moderno ya no expone las instalaciones en product.db en muchas
        // máquinas (los datos van a CachedData.db sin rutas). Complemento con el
        // escaneo de carpetas típicas (Hearthstone, WoW, Diablo, Overwatch…).
        try { games.AddRange(ScanBlizzardFolders()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: carpetas Blizzard: {ex.Message}"); }
        return games;
    }

    /// <summary>
    /// Escanea Program Files / Program Files (x86) buscando carpetas de juegos de
    /// Blizzard (Hearthstone, World of Warcraft, Diablo, Overwatch, StarCraft…).
    /// Detecta el exe real (Hearthstone.exe, Wow.exe, SC2_x64.exe…) que coincide con
    /// el proceso en ejecución, así las reglas se aplican normal.
    /// </summary>
    private List<InstalledGame> ScanBlizzardFolders()
    {
        var games = new List<InstalledGame>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        // (fragmento de carpeta, nombre del juego, código de producto Battle.net, exes candidatos)
        // El código es case-sensitive y viaja como AppId: se usa para lanzar el juego
        // vía --exec="launch <código>". Orden: los fragmentos más específicos primero
        // ("StarCraft II" antes que "StarCraft"; "Diablo IV" antes que "Diablo").
        (string Folder, string Name, string Code, string[] Exes)[] known =
        {
            ("hearthstone", "Hearthstone", "WTCG",
                new[] { "Hearthstone.exe", "Hearthstone Beta Launcher.exe", "Hearthstone Launcher.exe" }),
            ("world of warcraft", "World of Warcraft", "WoW",
                new[] { "Wow.exe", "Wow-64.exe", "WowClassic.exe", "WowClassicT.exe" }),
            ("diablo iv", "Diablo IV", "Fen",
                new[] { "Diablo IV.exe", "Diablo4.exe" }),
            ("diablo iii", "Diablo III", "D3",
                new[] { "Diablo III.exe", "Diablo3.exe" }),
            ("diablo ii resurrected", "Diablo II: Resurrected", "OSI",
                new[] { "D2R.exe" }),
            ("diablo immortal", "Diablo Immortal", "ANBS",
                new[] { "Diablo Immortal.exe", "DiabloImmortal.exe" }),
            ("diablo", "Diablo", "D1",
                new[] { "Diablo.exe" }),
            ("overwatch", "Overwatch", "Pro",
                new[] { "Overwatch.exe", "Overwatch 2.exe", "Overwatch2.exe" }),
            ("starcraft ii", "StarCraft II", "S2",
                new[] { "SC2_x64.exe", "SC2.exe", "StarCraft II.exe" }),
            ("starcraft", "StarCraft", "S1",
                new[] { "StarCraft.exe", "Starcraft.exe" }),
            ("heroes of the storm", "Heroes of the Storm", "Hero",
                new[] { "HeroesOfTheStorm_x64.exe", "Heroes of the Storm.exe" }),
            // Call of Duty (Activision en Battle.net)
            ("call of duty black ops cold war", "Call of Duty: Black Ops Cold War", "ZEUS",
                new[] { "BlackOpsColdWar.exe" }),
            ("call of duty black ops 4", "Call of Duty: Black Ops 4", "VIPR",
                new[] { "BlackOps4.exe", "BlackOps.exe" }),
            ("call of duty vanguard", "Call of Duty: Vanguard", "FORE",
                new[] { "vanguard.exe", "Vanguard.exe" }),
            ("call of duty modern warfare", "Call of Duty: Modern Warfare", "ODIN",
                new[] { "ModernWarfare.exe" }),
            ("call of duty", "Call of Duty", "AUKS",
                new[] { "cod.exe" }),
            ("warcraft iii", "Warcraft III", "W3",
                new[] { "Warcraft III.exe", "War3.exe", "Warcraft III Launcher.exe" })
        };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var n = Path.GetFileName(dir);
                    foreach (var k in known)
                    {
                        if (!n.Contains(k.Folder, StringComparison.OrdinalIgnoreCase)) continue;
                        var exe = FindStandaloneExe(dir, k.Exes);
                        if (exe == null) break;
                        if (found.Add(Path.GetFileName(exe)))
                            games.Add(new InstalledGame(k.Name, Path.GetFileName(exe), "Blizzard", dir, k.Code, GetBlizzardBannerUrl(k.Code)));
                        break;
                    }
                }
            }
            catch { }
        }
        return games;
    }

    // ===================== EA App / Origin =====================

    private List<InstalledGame> ScanEaGames()
    {
        var games = new List<InstalledGame>();
        string[] keys =
        {
            @"SOFTWARE\WOW6432Node\EA Games",
            @"SOFTWARE\EA Games"
        };
        foreach (var key in keys)
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                if (reg == null) continue;
                foreach (var sub in reg.GetSubKeyNames())
                {
                    try
                    {
                        using var gameKey = reg.OpenSubKey(sub);
                        var installDir = gameKey?.GetValue("Install Dir") as string
                            ?? gameKey?.GetValue("InstallDir") as string
                            ?? gameKey?.GetValue("Path") as string;
                        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) continue;
                        var exe = FindMainExe(installDir, sub);
                        games.Add(new InstalledGame(sub, exe ?? "", "EA", installDir));
                    }
                    catch { }
                }
            }
            catch { }
        }
        return games;
    }

    // ===================== GOG Galaxy =====================

    /// <summary>
    /// Detecta juegos de GOG Galaxy. Dos fuentes:
    ///  - Registro de desinstalación: GOG Galaxy registra cada juego instalado con
    ///    Publisher "GOG.com"; DisplayIcon trae la ruta del exe principal.
    ///  - Clave clásica HKLM\SOFTWARE\WOW6432Node\GOG.com\Games con gameName/path/exe.
    /// Los juegos de GOG son DRM-free y el exe es el proceso real en ejecución, así
    /// que las reglas de prioridad/afinidad se aplican normal. El lanzamiento pide
    /// el launcher abierto (la UI decide eso), la detección solo arma la biblioteca.
    /// </summary>
    private List<InstalledGame> ScanGog()
    {
        var games = new List<InstalledGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string? exePath, string installPath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;
            if (!seen.Add(exePath)) return;
            games.Add(new InstalledGame(name, Path.GetFileName(exePath), "GOG", installPath));
        }

        // ==== 1) Registro de desinstalación (fuente principal en GOG Galaxy 2) ====
        string[] uninstallRoots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in uninstallRoots)
            {
                try
                {
                    using var reg = hive.OpenSubKey(root);
                    if (reg == null) continue;
                    foreach (var sub in reg.GetSubKeyNames())
                    {
                        try
                        {
                            using var app = reg.OpenSubKey(sub);
                            var publisher = app?.GetValue("Publisher") as string;
                            if (string.IsNullOrEmpty(publisher)
                                || !publisher.Contains("GOG.com", StringComparison.OrdinalIgnoreCase))
                                continue;
                            var displayName = app?.GetValue("DisplayName") as string;
                            var installLoc = app?.GetValue("InstallLocation") as string;
                            if (string.IsNullOrEmpty(displayName)) continue;
                            // DisplayIcon trae la ruta completa del exe (a veces con ",0").
                            string? exePath = null;
                            var icon = app?.GetValue("DisplayIcon") as string;
                            if (!string.IsNullOrEmpty(icon))
                            {
                                var p = icon.Split(',')[0].Trim();
                                if (File.Exists(p)) exePath = p;
                            }
                            // Si no hay exe directo, buscar el más grande de la carpeta.
                            if (exePath == null
                                && !string.IsNullOrEmpty(installLoc)
                                && Directory.Exists(installLoc))
                            {
                                var main = FindMainExe(installLoc, displayName);
                                if (main != null) exePath = Path.Combine(installLoc, main);
                            }
                            Add(displayName, exePath, installLoc ?? "");
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // ==== 2) Clave clásica GOG.com\Games (GOG Galaxy 1 / juegos offline) ====
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                if (reg == null) continue;
                foreach (var sub in reg.GetSubKeyNames())
                {
                    try
                    {
                        using var gameKey = reg.OpenSubKey(sub);
                        var name = gameKey?.GetValue("gameName") as string;
                        var path = gameKey?.GetValue("path") as string;
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path) || !Directory.Exists(path))
                            continue;
                        string? exeName = gameKey?.GetValue("exe") as string;
                        string? exePath = null;
                        if (!string.IsNullOrEmpty(exeName))
                        {
                            var full = Path.Combine(path, exeName);
                            if (File.Exists(full)) exePath = full;
                        }
                        if (exePath == null)
                        {
                            var main = FindMainExe(path, name);
                            if (main != null) exePath = Path.Combine(path, main);
                        }
                        Add(name, exePath, path);
                    }
                    catch { }
                }
            }
            catch { }
        }

        return games;
    }

    // ===================== Xbox / Game Pass (MSIX) =====================

    /// <summary>
    /// Detecta juegos de Xbox/Game Pass: paquetes MSIX de la Microsoft Store.
    /// Se listan con Get-AppxPackage (filtrando las piezas del ecosistema Xbox y
    /// exigiendo un ejecutable grande: los juegos reales tienen binarios de decenas
    /// de MB; las apps de la Store no). El AppId de la card lleva el AUMID completo
    /// (PackageFamilyName!ApplicationId) para lanzar vía shell:AppsFolder, como un
    /// acceso directo del menú Inicio.
    /// </summary>
    private List<InstalledGame> ScanXbox()
    {
        var games = new List<InstalledGame>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.SignatureKind -eq 'Store' } | Select-Object Name,InstallLocation,PackageFamilyName | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return games;
            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(20000))
            {
                try { proc.Kill(); } catch { }
                return games;
            }
            if (string.IsNullOrWhiteSpace(output)) return games;

            using var doc = JsonDocument.Parse(output);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            foreach (var item in items)
            {
                try
                {
                    string? name = item.TryGetProperty("Name", out var n) ? n.GetString() : null;
                    string? loc = item.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                    string? pfn = item.TryGetProperty("PackageFamilyName", out var p) ? p.GetString() : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(loc) || !Directory.Exists(loc))
                        continue;
                    // Piezas del propio ecosistema Xbox/apps de sistema: no son juegos.
                    if (name.StartsWith("Microsoft.Gaming", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Microsoft.Xbox", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Microsoft.GameBar", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? exe = FindXboxMainExe(loc);
                    if (exe == null) continue;
                    // AUMID para lanzar por shell:AppsFolder (como el acceso directo del menú Inicio).
                    string aumid = BuildAumid(loc, pfn);
                    games.Add(new InstalledGame(name, Path.GetFileName(exe), "Xbox", loc, aumid));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"InstalledGames: Xbox (Get-AppxPackage): {ex.Message}");
        }
        return games;
    }

    /// <summary>Devuelve el exe más grande de la carpeta del paquete (los juegos tienen binarios de decenas de MB).</summary>
    private static string? FindXboxMainExe(string dir)
    {
        try
        {
            var exes = new List<string>();
            int budget = 300;
            GameExeResolver.CollectExes(dir, exes, 0, 2, ref budget);
            string? biggest = null;
            long threshold = 20L * 1024 * 1024; // apps de la Store: exes de pocos MB
            foreach (var e in exes)
            {
                try
                {
                    var len = new FileInfo(e).Length;
                    if (len > threshold) { threshold = len; biggest = e; }
                }
                catch { }
            }
            return biggest;
        }
        catch { return null; }
    }

    /// <summary>Arma el AUMID (PackageFamilyName!ApplicationId) leyendo el AppxManifest.xml del paquete.</summary>
    private static string BuildAumid(string installLocation, string? packageFamilyName)
    {
        if (string.IsNullOrEmpty(packageFamilyName)) return "";
        string? appId = null;
        try
        {
            var manifest = Path.Combine(installLocation, "AppxManifest.xml");
            if (File.Exists(manifest))
            {
                var doc = System.Xml.Linq.XDocument.Load(manifest);
                var appEl = doc.Root?.Element("Applications")?.Element("Application");
                appId = appEl?.Attribute("Id")?.Value;
            }
        }
        catch { }
        return string.IsNullOrEmpty(appId) ? "" : $"{packageFamilyName}!{appId}";
    }

    // ===================== Riot =====================

    /// <summary>
    /// Riot Games (League of Legends, VALORANT, Legends of Runeterra…). La carpeta
    /// raíz sale del registro del Riot Client ("Riot Games Install Directory",
    /// default C:\Riot Games) y cada juego es una subcarpeta con su propio exe.
    /// Se usa como exe el del JUEGO real (no el client): así el ícono de la card
    /// y la detección "En ejecución" apuntan al proceso del juego. El lanzamiento
    /// lo hace el Riot Client con el id de producto (AppId) — ver BuildGameCard.
    /// </summary>
    private List<InstalledGame> ScanRiot()
    {
        var games = new List<InstalledGame>();
        string? riotRoot = FindRiotRoot();
        if (riotRoot == null) return games;

        // (nombre, carpeta, exe del juego real [relativo a la carpeta], producto del Riot Client)
        var known = new (string Name, string Folder, string[] GameExes, string Product)[]
        {
            ("League of Legends", "League of Legends", new[] { @"Game\League of Legends.exe", "League of Legends.exe" }, "league_of_legends"),
            ("VALORANT", "VALORANT", new[] { @"live\VALORANT.exe", "VALORANT.exe" }, "valorant"),
            ("Legends of Runeterra", "Legends of Runeterra", new[] { "LoR.exe" }, "lor"),
            ("Teamfight Tactics", "Teamfight Tactics", new[] { "TFT.exe" }, "tft"),
            ("League of Legends PBE", "League of Legends PBE", new[] { @"Game\League of Legends.exe", "League of Legends.exe" }, "league_of_legends_pbe")
        };

        foreach (var g in known)
        {
            try
            {
                var dir = Path.Combine(riotRoot, g.Folder);
                if (!Directory.Exists(dir)) continue;
                string? relExe = null;
                foreach (var rel in g.GameExes)
                {
                    if (File.Exists(Path.Combine(dir, rel))) { relExe = rel; break; }
                }
                if (relExe == null) continue;
                games.Add(new InstalledGame(g.Name, Path.GetFileName(relExe), "Riot", dir, g.Product));
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"InstalledGames: Riot {g.Name}: {ex.Message}");
            }
        }
        return games;
    }

    private static string? FindRiotRoot()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Riot Games", @"SOFTWARE\Riot Games" })
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                var dir = reg?.GetValue("Riot Games Install Directory") as string;
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
            catch { }
        }
        // Default típico (raíz del disco del sistema).
        var def = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Riot Games");
        return Directory.Exists(def) ? def : null;
    }

    // ===================== Blacksmith (Dark and Darker) =====================

    /// <summary>
    /// Detecta Dark and Darker instalado vía el launcher Blacksmith de Ironmace.
    /// El juego se instala en "IRONMACE\Dark and Darker" (raíz de una unidad o
    /// Program Files); el launcher en "IRONMACE\Blacksmith".
    /// El exe real del juego es DungeonCrawler.exe (proyecto Unreal del juego:
    /// carpeta DungeonCrawler\Binaries\Win64 y proceso con ese nombre en Task
    /// Manager — el badge "En ejecución" matchea por nombre exacto del proceso).
    /// "DarkAndDarker.exe" se mantiene como variante de builds viejos.
    /// </summary>
    private List<InstalledGame> ScanBlacksmith()
    {
        var games = new List<InstalledGame>();
        try
        {
            // 1) Por la ruta del launcher (IRONMACE suele tener el juego al lado).
            string? blacksmithRoot = FindBlacksmithRoot();
            string? gameDir = blacksmithRoot == null ? null : FindDarkAndDarkerSubdir(blacksmithRoot);

            // 2) Barrido global de unidades: el juego puede estar en otra unidad
            //    que la del launcher (ej. D:\IRONMACE\Dark and Darker).
            if (gameDir == null)
                gameDir = FindDarkAndDarkerDirsGlobal().FirstOrDefault();

            if (gameDir == null) return games;

            var exe = FindDarkAndDarkerExe(gameDir);
            if (exe != null)
                games.Add(new InstalledGame("Dark and Darker", Path.GetFileName(exe), "Blacksmith", gameDir));
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"InstalledGames: Blacksmith: {ex.Message}");
        }

        return games;
    }

    /// <summary>
    /// Carpeta del juego dentro de una raíz del launcher: la raíz misma si ya es
    /// la del juego (IRONMACE\Dark and Darker), o una subcarpeta "Dark and Darker"
    /// (también bajo IRONMACE/Blacksmith).
    /// </summary>
    private static string? FindDarkAndDarkerSubdir(string root)
    {
        try
        {
            if (IsDarkAndDarkerDirName(Path.GetFileName(root)))
                return root;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (IsDarkAndDarkerDirName(Path.GetFileName(dir)))
                    return dir;
            }
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (dir.Contains("Ironmace", StringComparison.OrdinalIgnoreCase)
                    || dir.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        if (IsDarkAndDarkerDirName(Path.GetFileName(sub)))
                            return sub;
                }
            }
        }
        catch { }
        return null;
    }

    private static bool IsDarkAndDarkerDirName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.Contains("Dark", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Darker", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Barrido global acotado: busca la carpeta del juego en la raíz de cada unidad
    /// fija y en subcarpetas IRONMACE/Blacksmith de cada raíz.
    /// </summary>
    private static IEnumerable<string> FindDarkAndDarkerDirsGlobal()
    {
        var result = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                var root = drive.Name.TrimEnd('\\');
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var name = Path.GetFileName(dir);
                        if (IsDarkAndDarkerDirName(name))
                        {
                            result.Add(dir);
                            continue;
                        }
                        if (name.Contains("Ironmace", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var sub in Directory.EnumerateDirectories(dir))
                                if (IsDarkAndDarkerDirName(Path.GetFileName(sub)))
                                    result.Add(sub);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Exe real de Dark and Darker: DungeonCrawler\Binaries\Win64\DungeonCrawler.exe
    /// (el proceso del juego en Task Manager se llama DungeonCrawler.exe). Con
    /// respaldo de variantes (DungeonCrawler-Win64-Shipping, DarkAndDarker*) y un
    /// barrido acotado que salta stubs (crash handlers, instaladores…).
    /// </summary>
    private static string? FindDarkAndDarkerExe(string gameDir)
    {
        string[] candidates =
        {
            Path.Combine(gameDir, "DungeonCrawler", "Binaries", "Win64", "DungeonCrawler.exe"),
            Path.Combine(gameDir, "DungeonCrawler", "Binaries", "Win64", "DungeonCrawler-Win64-Shipping.exe"),
            Path.Combine(gameDir, "DungeonCrawler.exe"),
            Path.Combine(gameDir, "DungeonCrawler-Win64-Shipping.exe"),
            Path.Combine(gameDir, "DarkAndDarker.exe"),
            Path.Combine(gameDir, "Binaries", "Win64", "DarkAndDarker.exe"),
            Path.Combine(gameDir, "Binaries", "Win64", "DarkAndDarker-Win64-Shipping.exe")
        };
        foreach (var p in candidates)
            if (File.Exists(p)) return p;

        // Barrido acotado: el proceso real es DungeonCrawler.exe (badge "En
        // ejecución" matchea por nombre exacto), así que se prioriza ese nombre.
        var exes = new List<string>();
        int budget = 800;
        GameExeResolver.CollectExes(gameDir, exes, 0, 4, ref budget);
        string? exactDc = null, shippingDc = null, otherDc = null, dad = null;
        foreach (var e in exes)
        {
            if (GameExeResolver.IsStubExe(e)) continue;
            var name = Path.GetFileNameWithoutExtension(e);
            if (name.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Worker", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals("DungeonCrawler", StringComparison.OrdinalIgnoreCase))
                exactDc ??= e;
            else if (name.Equals("DungeonCrawler-Win64-Shipping", StringComparison.OrdinalIgnoreCase))
                shippingDc ??= e;
            else if (name.StartsWith("DungeonCrawler", StringComparison.OrdinalIgnoreCase))
                otherDc ??= e;
            else if (name.StartsWith("DarkAndDarker", StringComparison.OrdinalIgnoreCase))
                dad ??= e;
        }
        return exactDc ?? shippingDc ?? otherDc ?? dad;
    }

    private static string? FindBlacksmithRoot()
    {
        // Rutas típicas donde se instala Blacksmith
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        // Agregar todas las unidades de disco disponibles
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            roots.Add(drive.Name.TrimEnd('\\'));
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Buscar carpeta IRONMACE directamente
                var ironmacePath = Path.Combine(root, "IRONMACE");
                if (Directory.Exists(ironmacePath))
                {
                    // Buscar Dark and Darker dentro de IRONMACE
                    foreach (var dir in Directory.EnumerateDirectories(ironmacePath))
                    {
                        var dirName = Path.GetFileName(dir);
                        if (dirName.Contains("Dark", StringComparison.OrdinalIgnoreCase) &&
                            dirName.Contains("Darker", StringComparison.OrdinalIgnoreCase))
                        {
                            return dir;
                        }
                    }
                    // Si no se encontró, devolver la carpeta IRONMACE
                    return ironmacePath;
                }

                var candidates = new[]
                {
                    Path.Combine(root, "Blacksmith"),
                    Path.Combine(root, "Dark and Darker"),
                    Path.Combine(root, "DarkAndDarker"),
                    Path.Combine(root, "Ironmace"),
                    Path.Combine(root, "Ironmace", "Blacksmith"),
                    Path.Combine(root, "Ironmace", "Dark and Darker"),
                    Path.Combine(root, "IRONMACE"),
                    Path.Combine(root, "IRONMACE", "Dark and Darker"),
                    Path.Combine(root, "IRONMACE", "Blacksmith"),
                    Path.Combine(root, "Games", "Dark and Darker"),
                    Path.Combine(root, "Games", "DarkAndDarker"),
                };
                foreach (var cand in candidates)
                {
                    if (Directory.Exists(cand)) return cand;
                }

                // Buscar en subdirectorios de Program Files (incluyendo IRONMACE)
                if (root.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var dirName = Path.GetFileName(dir);
                        if (dirName.Contains("Dark", StringComparison.OrdinalIgnoreCase) &&
                            dirName.Contains("Darker", StringComparison.OrdinalIgnoreCase))
                        {
                            return dir;
                        }
                        if (dirName.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Contains("Ironmace", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("IRONMACE", StringComparison.OrdinalIgnoreCase))
                        {
                            // Buscar subcarpetas dentro de IRONMACE
                            foreach (var subDir in Directory.EnumerateDirectories(dir))
                            {
                                var subDirName = Path.GetFileName(subDir);
                                if (subDirName.Contains("Dark", StringComparison.OrdinalIgnoreCase) &&
                                    subDirName.Contains("Darker", StringComparison.OrdinalIgnoreCase))
                                {
                                    return subDir;
                                }
                            }
                            return dir;
                        }
                    }
                }
            }
            catch { }
        }

        // Buscar en el registro de Windows
        try
        {
            foreach (var regPath in new[]
            {
                @"SOFTWARE\Ironmace\Blacksmith",
                @"SOFTWARE\WOW6432Node\Ironmace\Blacksmith",
                @"SOFTWARE\Ironmace\Dark and Darker",
                @"SOFTWARE\WOW6432Node\Ironmace\Dark and Darker",
                @"SOFTWARE\IRONMACE\Dark and Darker",
                @"SOFTWARE\WOW6432Node\IRONMACE\Dark and Darker"
            })
            {
                using var reg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
                var installDir = reg?.GetValue("InstallDir") as string;
                if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                    return installDir;

                var installLoc = reg?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
                    return installLoc;
            }
        }
        catch { }

        // Entrada de desinstalación del juego (DisplayName "Dark and Darker"): cubre
        // instalaciones en carpetas no estándar elegidas por el usuario.
        try
        {
            string[] uninstallRoots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var uninstallRoot in uninstallRoots)
                {
                    using var reg = hive.OpenSubKey(uninstallRoot);
                    if (reg == null) continue;
                    foreach (var sub in reg.GetSubKeyNames())
                    {
                        try
                        {
                            using var app = reg.OpenSubKey(sub);
                            var displayName = app?.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(displayName)
                                || !displayName.Contains("Dark and Darker", StringComparison.OrdinalIgnoreCase))
                                continue;
                            var loc = app?.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                                return loc.TrimEnd('\\');
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    // ===================== Epic =====================

    private List<InstalledGame> ScanEpic()
    {
        var games = new List<InstalledGame>();
        var manifestsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestsDir)) return games;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(manifestsDir, "*.item"); }
        catch { return games; }

        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                string? name = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                string? exe = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;
                string? install = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                // Catálogo de Epic: con estos dos campos la UI puede buscar el banner
                // en la API pública del catálogo (namespace + catalogItemId).
                string? catalogId = root.TryGetProperty("CatalogItemId", out var ci) ? ci.GetString() : null;
                string? catalogNs = root.TryGetProperty("CatalogNamespace", out var cn) ? cn.GetString() : null;
                // AppName: id que la URI del launcher usa para lanzar el juego
                // (com.epicgames.launcher://apps/{AppName}); sin él, el exe directo
                // abre el juego pero las funciones online no funcionan.
                string? appName = root.TryGetProperty("AppName", out var an) ? an.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(exe)) continue;
                games.Add(new InstalledGame(name, Path.GetFileName(exe), "Epic", install ?? "", catalogId ?? "", "", catalogNs ?? "", appName ?? ""));
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"InstalledGames: Epic manifest {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return games;
    }
}
