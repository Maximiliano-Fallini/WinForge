using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// - Steam: appmanifest_*.acf en cada carpeta steamapps (libraryfolders.vdf) con
/// nombre + installdir; el ejecutable se busca en la carpeta del juego.
/// - Epic: Manifests/*.item (JSON) con DisplayName + LaunchExecutable.
/// - GOG: registro de desinstalación (Publisher "GOG.com") + clave GOG.com\Games.
/// - Xbox/Game Pass: paquetes MSIX de la Store con ejecutable grande.
/// Todo es best-effort: si un launcher no existe o un juego no tiene ejecutable,
/// simplemente no aparece (o aparece sin exe, que se usa solo para matchear).
/// </summary>
public sealed class InstalledGamesService : IInstalledGamesService
{
    private readonly ILoggingService _logging;
    private readonly ISettingsService _settings;

    /// <summary>Clave del settings donde viven las carpetas de emuladores portables (Capa 3).</summary>
    private const string EmulatorSearchFoldersKey = "games.emulatorSearchFolders";

    // Carpetas que no contienen instalaciones portables de usuario y pueden multiplicar
    // innecesariamente el coste del escaneo profundo.
    private static readonly HashSet<string> DeepScanExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "System32",
        "SysWOW64",
        "WinSxS",
        "SystemApps",
        "WindowsApps",
        "ProgramData",
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "MSOCache",
        "$WinREAgent",
        "Config.Msi",
        "Boot",
        "EFI"
    };

    // Caché de la biblioteca: la primera vez se escanea y se guarda (memoria + disco),
    // así abrir la página de juegos (o el escaneo de arranque de ProcessService) no
    // re-escanea los launchers en cada visita. Un escaneo nuevo solo ocurre con
    // refresh=true (el botón "Re-detectar" de la biblioteca).
    private readonly object _lock = new();
    private List<InstalledGame>? _cache;
    private Task<List<InstalledGame>>? _scanTask;

    /// <summary>True si hay una biblioteca cacheada en memoria (no hace falta re-escannear).</summary>
    public bool HasCachedResult
    {
        get { lock (_lock) return _cache != null; }
    }

    private static readonly string CacheDir = AppPaths.RootDir;
    private static readonly string CacheFile = Path.Combine(CacheDir, "gamescache.json");
    // Firma mínima: entradas del launcher de BlueStacks (Store/Updater/Installer)
    // usan nombres parecidos al emulador. Si hay ≥2 definiciones que "gustarían" de
    // reportar la misma carpeta, es family overlap → descartar (BlueStacks vs MSI App
    // Player vs Demul vs NullDC vs xboxdrv etc.).
    private static readonly int TYPICAL_ENTRIES_FOR_SHARED_DLL = 4;
    // Solo la build que escribió el caché puede reusarlo: escritura atómica tmp→move,
    // y re-escritura solo si el nombre corto del exe de la build actual coincide con
    // el que escribió el caché. Así un cambio de ruta (publish → carpeta plana, MSI,
    // o de bin\Debug a %LocalAppData%) obliga a un escaneo limpio sin borrar a fuerzas.
    private static readonly string? WritingBuildExeName = TryGetThisBuildExeName();

    /// <summary>Nombre del exe de esta build (sin extensión) para la firma del caché, o null si no se pudo leer.</summary>
    private static string? TryGetThisBuildExeName()
    {
        try
        {
            var path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
        }
        catch
        {
            return null;
        }
    }
    // Versión de formato del archivo de caché: cualquier cambio en InstalledGame o en
    // la forma en que se serializa obliga a reconstruir el caché.
    private const int CACHE_VERSION = 6;

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

    public InstalledGamesService(ILoggingService logging, ISettingsService settings)
    {
        _logging = logging;
        _settings = settings;
    }

    /// <summary>
    /// Carpetas donde buscar emuladores portables (Capa 3). Se descartan las que ya no
    /// existen (una carpeta borrada no puede aportar nada) y los duplicados.
    /// </summary>
    public IReadOnlyList<string> GetEmulatorSearchFolders()
    {
        var folders = _settings.Get(EmulatorSearchFoldersKey, new List<string>()) ?? new();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var candidate = folder?.Trim();
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { continue; }
            if (seen.Add(full)) result.Add(full);
        }
        return result;
    }

    /// <summary>Agrega una carpeta a la búsqueda de emuladores portables (Capa 3).</summary>
    public bool AddEmulatorSearchFolder(string folder)
    {
        var candidate = folder?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) return false;

        string full;
        try { full = Path.GetFullPath(candidate); }
        catch { return false; }

        var folders = _settings.Get(EmulatorSearchFoldersKey, new List<string>()) ?? new();
        if (folders.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)))
            return false;

        folders.Add(full);
        _settings.Set(EmulatorSearchFoldersKey, folders);
        _settings.Save();
        _logging.LogInfo($"InstalledGames: carpeta de emuladores agregada: {full}");
        return true;
    }

    public Task<List<InstalledGame>> GetInstalledGamesAsync()
        => GetInstalledGamesAsync(refresh: false);

    public Task<List<InstalledGame>> GetInstalledGamesAsync(bool refresh)
        => GetInstalledGamesAsync(refresh, deepEmulatorScan: false, progress: null);

    public Task<List<InstalledGame>> GetInstalledGamesAsync(
        bool refresh,
        bool deepEmulatorScan,
        IProgress<EmulatorScanProgress>? progress)
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
            //
            // El escaneo profundo NO se puede resolver con uno liviano que ya esté
            // corriendo (ni al revés): se encadena al anterior para no perder la
            // petición del usuario.
            var previous = deepEmulatorScan ? _scanTask : null;
            _scanTask = previous == null
                ? Task.Run(() => RunScan(deepEmulatorScan, progress))
                : previous.ContinueWith(_ => RunScan(deepEmulatorScan, progress));
            return _scanTask;
        }
    }

    /// <summary>
    /// ¿La entrada apunta a una carpeta que ya no está en disco? Solo cuentan las
    /// entradas que SÍ tenían carpeta: una que nunca la tuvo no es una ruta vencida.
    /// </summary>
    public static bool HasMissingInstallPath(InstalledGame game)
    {
        if (string.IsNullOrEmpty(game.InstallPath)) return false;
        try { return !Directory.Exists(game.InstallPath); }
        catch { return false; }
    }

    /// <summary>
    /// Repara rutas de instalación vencidas volviendo a preguntarles a los launchers
    /// dónde está cada entrada rota (ver <see cref="IInstalledGamesService.RepairMissingInstallPathsAsync"/>).
    ///
    /// Por qué existe: el enganche (reglas de modo juego) reconoce un proceso por el
    /// nombre del exe o por su ruta dentro de la carpeta de instalación. Los juegos de
    /// la Store tienen la versión dentro del nombre de la carpeta, así que cada
    /// actualización deja la ruta apuntando a una carpeta que ya no existe: el juego
    /// sigue en la biblioteca, pero un proceso hijo con otro nombre deja de matchear.
    /// Lo mismo pasa si el usuario mueve un juego, renombra una biblioteca o mueve un
    /// emulador portable.
    ///
    /// Nunca agrega ni quita juegos: solo actualiza la carpeta de entradas que ya
    /// estaban en la biblioteca, así que un juego que el usuario borró a propósito no
    /// reaparece (no se toca lo que no está en la lista) y un fallo momentáneo de un
    /// launcher no puede borrar un juego (nunca se elimina nada).
    /// </summary>
    public Task<List<(InstalledGame Broken, InstalledGame Fresh)>> RepairMissingInstallPathsAsync(
        IReadOnlyList<InstalledGame> library)
    {
        // Una sola reparación a la vez: la biblioteca y el monitor de procesos pueden
        // pedirla al mismo tiempo al arrancar, y cada reparación corre un escaneo.
        lock (_lock)
        {
            _repairTask ??= Task.Run(() => RepairCoreAsync(library));
            return _repairTask;
        }
    }

    private Task<List<(InstalledGame Broken, InstalledGame Fresh)>>? _repairTask;

    private async Task<List<(InstalledGame Broken, InstalledGame Fresh)>> RepairCoreAsync(
        IReadOnlyList<InstalledGame> library)
    {
        try
        {
            return await RepairMissingPathsCoreAsync(library).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _repairTask = null;
        }
    }

    private async Task<List<(InstalledGame Broken, InstalledGame Fresh)>> RepairMissingPathsCoreAsync(
        IReadOnlyList<InstalledGame> library)
    {
        var broken = library.Where(HasMissingInstallPath).ToList();
        if (broken.Count == 0) return new List<(InstalledGame, InstalledGame)>();

        List<InstalledGame> fresh;
        try
        {
            fresh = await GetInstalledGamesAsync(refresh: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"InstalledGames: reparación de rutas: {ex.Message}");
            return new List<(InstalledGame, InstalledGame)>();
        }

        // El par devuelve la entrada YA fusionada (carpeta nueva + datos viejos) para que
        // quien la reciba no tenga que reconstruirla ni perder banner/appid/favoritos.
        var repairs = new List<(InstalledGame Broken, InstalledGame Fresh)>();
        foreach (var old in broken)
        {
            var match = FindFreshPathFor(old, fresh);
            if (match != null) repairs.Add((old, RepairedEntry(old, match)));
        }

        if (repairs.Count == 0)
        {
            _logging.LogInfo($"InstalledGames: {broken.Count} ruta(s) vencida(s); ninguna se pudo re-resolver (¿desinstalados?)");
            return repairs;
        }

        // El resto de la entrada se conserva (nombre, banner, appid, favoritos): solo
        // se actualiza dónde vive. El exe se mantiene si ese nombre sigue existiendo en
        // la carpeta nueva, para no invalidar reglas guardadas con el nombre viejo.
        lock (_lock)
        {
            if (_cache != null)
            {
                foreach (var (old, merged) in repairs)
                {
                    var idx = _cache.FindIndex(g => ReferenceEquals(g, old)
                        || (string.Equals(g.Name, old.Name, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(g.Launcher, old.Launcher, StringComparison.OrdinalIgnoreCase)));
                    if (idx < 0) continue;
                    _cache[idx] = merged;
                }
                SaveCacheToDisk(_cache);
            }
        }

        _logging.LogInfo($"InstalledGames: {repairs.Count} ruta(s) reparada(s)");
        return repairs;
    }

    /// <summary>Copia la carpeta nueva sobre la entrada vieja conservando el resto de los datos.</summary>
    private static InstalledGame RepairedEntry(InstalledGame old, InstalledGame match)
    {
        var exe = old.ExeFileName;
        try
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(Path.Combine(match.InstallPath, exe)))
                exe = match.ExeFileName;
        }
        catch { exe = match.ExeFileName; }
        return old with { InstallPath = match.InstallPath, ExeFileName = exe };
    }

    /// <summary>
    /// Busca, en las entradas de un escaneo fresco, la que corresponde a una entrada
    /// vieja: mismo launcher y mismo nombre normalizado, o uno que sea prefijo del otro
    /// ("PCSX2" vs "PCSX2 1.7.2"). Se exige que la carpeta candidata EXISTA: si el
    /// escaneo devolvió la misma ruta rota, no sirve como reparación.
    /// </summary>
    internal static InstalledGame? FindFreshPathFor(InstalledGame broken, IReadOnlyList<InstalledGame> fresh)
    {
        if (string.IsNullOrEmpty(broken.Name)) return null;
        var wanted = NormalizeGameName(broken.Name);

        foreach (var candidate in fresh)
        {
            if (!SameLauncher(broken, candidate) || !HasUsablePath(candidate)) continue;
            if (NormalizeGameName(candidate.Name) == wanted) return candidate;
        }

        // Prefijo, siempre dentro del mismo launcher: evita cruzar un juego con otro
        // homónimo de otra tienda.
        foreach (var candidate in fresh)
        {
            if (!SameLauncher(broken, candidate) || !HasUsablePath(candidate)) continue;
            var other = NormalizeGameName(candidate.Name);
            if (wanted.Length < 4 || other.Length < 4) continue;
            if (other.StartsWith(wanted, StringComparison.Ordinal)
                || wanted.StartsWith(other, StringComparison.Ordinal))
                return candidate;
        }
        return null;
    }

    private static bool HasUsablePath(InstalledGame game)
    {
        if (string.IsNullOrEmpty(game.InstallPath)) return false;
        try { return Directory.Exists(game.InstallPath); }
        catch { return false; }
    }

    private static bool SameLauncher(InstalledGame a, InstalledGame b)
        => string.Equals(a.Launcher, b.Launcher, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombre comparable: sin marcas de marca, minúsculas y con un solo espacio.</summary>
    internal static string NormalizeGameName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim().TrimEnd('™', '®', '©');
        return string.Join(' ', trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private List<InstalledGame> RunScan(bool deepEmulatorScan, IProgress<EmulatorScanProgress>? progress)
    {
        // Caché previa (memoria o disco): de ahí salen los emuladores que este escaneo no
        // vuelve a ver (ver MergeStickyEmulators).
        List<InstalledGame>? previous;
        lock (_lock) previous = _cache ?? LoadCacheFromDisk();

        var games = MergeStickyEmulators(ScanAll(deepEmulatorScan, progress), previous);
        lock (_lock)
        {
            _cache = games;
            _scanTask = null;
            SaveCacheToDisk(games);
        }
        return games;
    }

    /// <summary>Launcher de las entradas de emulador: es lo que las distingue dentro de la
    /// biblioteca (y lo que usa el arrastre de "ya descubiertos").</summary>
    private const string EmulatorLauncherName = "Emulador";

    /// <summary>
    /// Conserva los emuladores YA descubiertos que este escaneo no volvió a encontrar.
    ///
    /// Por qué: el escaneo profundo (Capa 4) recorre las unidades enteras y es el ÚNICO que ve
    /// los emuladores portables guardados en carpetas arbitrarias; un "Re-detectar" corre el
    /// escaneo liviano (Capas 1-3), que no los ve. Como la caché se REESCRIBE con el resultado
    /// de cada escaneo, ese re-escaneo los borraba de la biblioteca (memoria y disco) y había
    /// que repetir el escaneo profundo para recuperarlos.
    ///
    /// El arrastre se valida contra el disco (si el emulador se borró o se movió, se descarta:
    /// no deja entradas muertas) y respeta el dedup por nombre de exe del escaneo nuevo. Es solo
    /// para emuladores: un juego de launcher que no aparezca en un escaneo puntual puede
    /// respondar a un launcher sin leer, no a un arrastre.
    /// </summary>
    private static List<InstalledGame> MergeStickyEmulators(List<InstalledGame> games, List<InstalledGame>? previous)
    {
        if (previous is not { Count: > 0 }) return games;

        var names = new HashSet<string>(games.Select(g => g.ExeFileName), StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(
            games.Select(FullExePathOf).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);

        foreach (var old in previous)
        {
            if (!string.Equals(old.Launcher, EmulatorLauncherName, StringComparison.OrdinalIgnoreCase)) continue;
            string full = FullExePathOf(old);
            if (full.Length == 0 || !File.Exists(full)) continue; // el emulador ya no está en disco
            if (!paths.Add(full)) continue;
            if (!names.Add(old.ExeFileName)) continue;            // el escaneo nuevo ya lo trae
            games.Add(old);
        }
        return games;
    }

    /// <summary>Ruta completa del exe de una entrada de la caché (vacía si falta algún dato).</summary>
    private static string FullExePathOf(InstalledGame game)
        => string.IsNullOrEmpty(game.InstallPath) || string.IsNullOrEmpty(game.ExeFileName)
            ? ""
            : Path.Combine(game.InstallPath, game.ExeFileName);

    private List<InstalledGame> ScanAll(bool deepEmulatorScan, IProgress<EmulatorScanProgress>? progress)
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
        try { games.AddRange(ScanItch()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Itch.io: {ex.Message}"); }
        try { games.AddRange(ScanAmazonGames()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Amazon Games: {ex.Message}"); }
        try { games.AddRange(ScanEmulators(deepEmulatorScan, progress)); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: emuladores: {ex.Message}"); }
        try { games.AddRange(ScanAdditionalLauncherGames()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: otros launchers: {ex.Message}"); }
        // Llave maestra: ningún launcher/instalador/anti-cheat puede ser un juego.
        // Si cualquier scanner resolviera el cliente de un launcher o un stub como exe
        // (ej. BlacksmithBootstrap.exe del launcher de Dark and Darker), se descarta acá,
        // pase lo que pase.
        return games
            .Where(g => !IsDetectedLauncher(g))
            .GroupBy(g => g.ExeFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    // ===================== Launchers adicionales =====================
    // Los clientes que no tienen un scanner propio (EA/Rockstar/HoYoverse/Wargaming/
    // Nexon/Gaijin/Paradox/Kakao/Pearl Abyss/NCSOFT/Gameforge/Jagex) se detectan por el
    // registro de desinstalación: cada entrada con su Publisher y su InstallLocation.

    /// <summary>
    /// Nombres de ejecutable que son SIEMPRE el cliente del launcher, nunca un juego. Se
    /// usan como filtro final para impedir que el cliente termine en la biblioteca.
    /// </summary>
    private static readonly HashSet<string> KnownLauncherExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper",
        "epicgameslauncher", "epicwebhelper",
        "battle.net",
        "galaxyclient", "galaxyclientservice", "goggalaxy",
        "ubisoftconnect", "ubisoftgamelauncher", "uplay", "uplaywebcore", "upc",
        "eadesktop", "ealauncher", "origin", "originwebhelperservice",
        "riotclientservices", "riotclientux", "riotclientuxrender", "riotclientcrashhandler",
        "rockstargameslauncher", "rockstarservice", "socialclubhelper",
        "hoyoplay", "hoyoplaylauncher",
        "wargaminggamecenter", "wgc",
        "nexonlauncher", "nexonclient",
        "gaijinlauncher", "paradox launcher", "jagexlauncher",
        "amazon games", "amazongames", "amazongameslauncher",
        "xbox", "xboxapp", "gamingapp", "gamingservices",
        "itch", "butler"
    };

    /// <summary>Un juego detectado es en realidad el cliente de un launcher o un stub.</summary>
    private static bool IsDetectedLauncher(InstalledGame game)
    {
        if (GameExeResolver.IsStubExeName(game.ExeFileName)
            || IsLauncherExecutable(game.ExeFileName))
            return true;

        var exeName = Path.GetFileNameWithoutExtension(game.ExeFileName ?? "");
        if (KnownLauncherExecutableNames.Contains(exeName)) return true;
        return IsLauncherDisplayName(game.Name);
    }

    /// <summary>Nombre visible que corresponde al cliente de un launcher, no a un juego.</summary>
    private static bool IsLauncherDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        var value = displayName.Trim();
        return value.Equals("EA app", StringComparison.OrdinalIgnoreCase)
            || value.Equals("EA Desktop", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Origin", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Rockstar Games", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Rockstar Games Launcher", StringComparison.OrdinalIgnoreCase)
            || value.Equals("HoYoPlay", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Nexon Launcher", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Wargaming Game Center", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Riot Client", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Gameforge Client", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Ubisoft Connect", StringComparison.OrdinalIgnoreCase)
            || value.Equals("GOG GALAXY", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Epic Games Launcher", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Steam", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Jagex Launcher", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Amazon Games", StringComparison.OrdinalIgnoreCase)
            || value.Equals("itch", StringComparison.OrdinalIgnoreCase)
            || value.Contains("launcher", StringComparison.OrdinalIgnoreCase)
            || value.Contains("game center", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Nexon Client", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Entrada de desinstalación que es un componente del launcher, no un juego.</summary>
    private static bool IsLauncherUninstallEntry(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        var value = displayName.Trim();
        return IsLauncherDisplayName(value)
            || value.Contains("updater", StringComparison.OrdinalIgnoreCase)
            || value.Contains("bootstrap", StringComparison.OrdinalIgnoreCase)
            || value.Contains("redistributable", StringComparison.OrdinalIgnoreCase)
            || value.Contains("setup", StringComparison.OrdinalIgnoreCase)
            || value.Contains("service", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(" launcher", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Entradas que los launchers registran como instaladas pero no son juegos:
    /// redistribuibles y herramientas (appid 228980 de Steam "Steamworks Common
    /// Redistributables", "VC_redist.x64.exe", instaladores de DirectX/.NET),
    /// servidores dedicados, bandas sonoras, SDKs y servicios del propio launcher
    /// ("Epic Online Services"). Se excluyen para que no ocupen una card de la
    /// biblioteca ni reciban reglas de modo juego: aplicar el enganche a un
    /// instalador de runtime no tiene sentido.
    /// </summary>
    private static bool IsNonGameEntry(string? name, string? exePath)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var token in NonGameNameTokens)
                if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var exe = Path.GetFileName(exePath);
            foreach (var token in NonGameExeTokens)
                if (exe.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    /// <summary>Rasgos de nombre que delatan una entrada que no es un juego.</summary>
    private static readonly string[] NonGameNameTokens =
    {
        "Steamworks",           // appid 228980: redistribuibles compartidos
        "Redistributable",
        "Dedicated Server",
        "Soundtrack",
        "Online Services",      // Epic Online Services
        "SDK"
    };

    /// <summary>Instaladores de dependencias que algunos launchers listan como juego.</summary>
    private static readonly string[] NonGameExeTokens =
    {
        "VC_redist", "dxsetup", "dxwebsetup", "dotnetfx", "oalinst", "UE4PrereqSetup", "DirectX"
    };

    /// <summary>El ejecutable es el cliente/actualizador de un launcher, no un juego.</summary>
    private static bool IsLauncherExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) return true;
        return name.Contains("launcher", StringComparison.OrdinalIgnoreCase)
            || name.Contains("updater", StringComparison.OrdinalIgnoreCase)
            || name.Contains("bootstrap", StringComparison.OrdinalIgnoreCase)
            || name.Contains("install", StringComparison.OrdinalIgnoreCase)
            || name.Contains("unins", StringComparison.OrdinalIgnoreCase)
            || name.Equals("client", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gamecenter", StringComparison.OrdinalIgnoreCase)
            || KnownLauncherExecutableNames.Contains(name);
    }

    /// <summary>Ruta del ejecutable dentro de una cadena de comando del registro.</summary>
    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var value = command.Trim();
        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            return endQuote > 1 ? value[1..endQuote] : null;
        }

        var extensions = new[] { ".exe", ".com", ".bat", ".cmd" };
        var end = -1;
        foreach (var extension in extensions)
        {
            var index = value.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (end < 0 || index < end)) end = index + extension.Length;
        }
        return end > 0 ? value[..end].Trim('"') : value;
    }

    /// <summary>
    /// Juegos de los launchers que no tienen scanner propio, leídos del registro de
    /// desinstalación. Se descartan las entradas que son el cliente del launcher, un
    /// actualizador, un redistribuible o un stub: la biblioteca lista juegos, no clientes.
    /// </summary>
    private List<InstalledGame> ScanAdditionalLauncherGames()
    {
        var games = new List<InstalledGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new (string Launcher, string[] Tokens)[]
        {
            ("EA", new[] { "Electronic Arts", "Origin" }),
            ("Rockstar", new[] { "Rockstar Games", "Rockstar" }),
            ("HoYoverse", new[] { "HoYoverse", "miHoYo", "HoYoPlay" }),
            ("Wargaming", new[] { "Wargaming.net", "Wargaming" }),
            ("Nexon", new[] { "Nexon" }),
            ("Gaijin", new[] { "Gaijin Entertainment", "Gaijin" }),
            ("Paradox", new[] { "Paradox Interactive" }),
            ("Kakao Games", new[] { "Kakao Games" }),
            ("Pearl Abyss", new[] { "Pearl Abyss" }),
            ("NCSOFT", new[] { "NCSOFT", "NCSoft" }),
            ("Gameforge", new[] { "Gameforge" }),
            ("Jagex", new[] { "Jagex" }),
            ("Riot", new[] { "Riot Games" })
        };
        string[] uninstallRoots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var root in uninstallRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var reg = baseKey.OpenSubKey(root);
                if (reg == null) continue;

                foreach (var sub in reg.GetSubKeyNames())
                {
                    try
                    {
                        using var app = reg.OpenSubKey(sub);
                        var displayName = (app?.GetValue("DisplayName") as string)?.Trim();
                        var publisher = (app?.GetValue("Publisher") as string)?.Trim() ?? "";
                        var uninstallString = (app?.GetValue("UninstallString") as string)?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(displayName)
                            && string.IsNullOrWhiteSpace(publisher)
                            && string.IsNullOrWhiteSpace(uninstallString)) continue;

                        var profile = profiles.FirstOrDefault(p =>
                            p.Tokens.Any(token =>
                                publisher.Contains(token, StringComparison.OrdinalIgnoreCase)
                                || displayName?.Contains(token, StringComparison.OrdinalIgnoreCase) == true
                                || uninstallString.Contains(token, StringComparison.OrdinalIgnoreCase)));
                        if (string.IsNullOrEmpty(profile.Launcher)) continue;
                        if (IsLauncherUninstallEntry(displayName)) continue;

                        var installPath = (app?.GetValue("InstallLocation") as string)?.Trim().Trim('"') ?? "";
                        installPath = Environment.ExpandEnvironmentVariables(installPath);
                        var iconPath = ExtractExecutablePath(app?.GetValue("DisplayIcon") as string);
                        if (string.IsNullOrEmpty(installPath) && !string.IsNullOrEmpty(iconPath))
                            installPath = Path.GetDirectoryName(iconPath) ?? "";
                        if (!Directory.Exists(installPath)) continue;

                        string? exePath = null;
                        if (!string.IsNullOrEmpty(iconPath)
                            && File.Exists(iconPath)
                            && string.Equals(Path.GetExtension(iconPath), ".exe", StringComparison.OrdinalIgnoreCase)
                            && !IsLauncherExecutable(iconPath))
                            exePath = iconPath;

                        if (exePath == null)
                        {
                            var exeName = FindMainExe(installPath, displayName ?? profile.Launcher);
                            if (!string.IsNullOrEmpty(exeName))
                                exePath = Path.Combine(installPath, exeName);
                        }
                        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)
                            || GameExeResolver.IsStubExe(exePath)
                            || IsLauncherExecutable(exePath)) continue;

                        if (IsNonGameEntry(displayName, exePath)) continue;

                        var identity = $"{profile.Launcher}|{installPath}|{exePath}";
                        if (!seen.Add(identity)) continue;
                        games.Add(new InstalledGame(
                            displayName ?? Path.GetFileName(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                            Path.GetFileName(exePath), profile.Launcher, installPath));
                    }
                    catch { }
                }
            }
            catch { }
        }

        return games;
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
            // Steam: una entrada sin exe no es navegable ni lanzable, y al marcarla
            // como favorita guardaba "" en el settings (el ítem sin nombre ni ícono
            // de la bandeja — el caso de CS2 con un escaneo a medias). El re-escaneo
            // re-deriva el exe desde el appmanifest, así que mejor botar la entrada
            // que mostrarla rota. Solo Steam: en Battle.net un exe vacío es legítimo
            // (se resuelve al iniciar desde la carpeta del juego).
            valid.RemoveAll(g => string.Equals(g.Launcher, "Steam", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(g.ExeFileName));
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
                    // Redistribuibles y herramientas de Steam (appid 228980 "Steamworks
                    // Common Redistributables", servidores dedicados, bandas sonoras):
                    // no son juegos, no deben ocupar una card ni recibir reglas.
                    if (IsNonGameEntry(name, null)) continue;
                    if (string.Equals(appId, "228980", StringComparison.Ordinal)) continue;
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
 // sub es el gameId de Ubisoft Connect (numérico): se guarda
 // como AppId para futuro lanzamiento vía uplay://launch/<id>/0.
                        string gameId = sub;
                        string name = Path.GetFileName(installDir.TrimEnd('\\'));
 // Mejorar el título: buscar "Uplay Install <gameId>" en el
 // registro de desinstalación, que trae el DisplayName real.
                        string? betterName = FindUbisoftGameName(gameId);
                        if (!string.IsNullOrEmpty(betterName)) name = betterName;
                        var exe = FindMainExe(installDir, name);
                        games.Add(new InstalledGame(name, exe ?? "", "Ubisoft", installDir, gameId));
                    }
                    catch { }
                }
            }
            catch { }
        }
        return games;
    }

    /// <summary>
    /// Busca el nombre real de un juego de Ubisoft por su gameId en el registro
    /// de desinstalación ("Uplay Install <gameId>" → DisplayName). Null si no
    /// hay entrada, en cuyo caso se usa el nombre de la carpeta.
    /// </summary>
    private static string? FindUbisoftGameName(string gameId)
    {
        try
        {
            string[] uninstallRoots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var root in uninstallRoots)
                {
                    using var reg = hive.OpenSubKey(root);
                    if (reg == null) continue;
    // Buscar "Uplay Install <gameId>" como nombre de subclave.
                    using var app = reg.OpenSubKey($"Uplay Install {gameId}");
                    if (app != null)
                    {
                        var dn = app.GetValue("DisplayName") as string;
                        if (!string.IsNullOrEmpty(dn)) return dn;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    // ===================== Juegos independientes (Roblox, Minecraft, Genshin, Warframe) =====================

    /// <summary>
    /// Detecta juegos que no pasan por Steam/Epic/Ubisoft/EA/Blizzard: instalan su
    /// propio launcher (Roblox, Minecraft, Genshin Impact, Warframe…). Usa dos fuentes:
    /// - Registro de desinstalación (Uninstall): DisplayName conocido + InstallLocation.
    /// - Rutas típicas de instalación para los que no se registran (Roblox/Minecraft).
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
        // escaneo de carpetas típicas (Hearthstone, WoW, Diablo, Overwatch…) y
        // con el registro de desinstalación (UninstallString con --uid=<code>).
        try { games.AddRange(ScanBlizzardFolders()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: carpetas Blizzard: {ex.Message}"); }
        try { games.AddRange(ScanBlizzardUninstall()); } catch (Exception ex) { _logging.LogWarning($"InstalledGames: Blizzard Uninstall: {ex.Message}"); }
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

 // Además de Program Files, buscar en la carpeta "Games" de cada unidad
 // (Battle.net permite instalar en otras unidades) y en la raíz.
        var allRoots = new List<string>(roots);
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            {
                var dr = drive.Name.TrimEnd('\\');
                allRoots.Add(dr);
                allRoots.Add(Path.Combine(dr, "Games"));
                allRoots.Add(Path.Combine(dr, "Battle.net"));
            }
        }
        catch { }

        foreach (var root in allRoots.Distinct(StringComparer.OrdinalIgnoreCase))
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

    /// <summary>
    /// Fallback de Battle.net: lee el registro de desinstalación buscando claves
    /// cuyo UninstallString contiene "Battle.net.exe --uid=<uid> --product=<code>".
    /// Cada juego de Battle.net registra su desinstalador así; el código de
    /// producto (ej. WTCG, Fen, ODIN) está embebido en el --product=<code>.
    /// En máquinas modernas esto encuentra juegos que product.db no expone.
    /// </summary>
    private List<InstalledGame> ScanBlizzardUninstall()
    {
        var games = new List<InstalledGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                            var uninstallStr = app?.GetValue("UninstallString") as string;
                            var installLoc = app?.GetValue("InstallLocation") as string;
                            if (string.IsNullOrEmpty(uninstallStr)) continue;
                            if (!uninstallStr.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
    // Extraer el código de producto de --product=<code>.
                            var codeMatch = System.Text.RegularExpressions.Regex.Match(
                                uninstallStr, "--product=([A-Za-z0-9]+)");
                            if (!codeMatch.Success) continue;
                            string code = codeMatch.Groups[1].Value;
    // Mapear código → nombre conocido (si el DisplayName
    // trae un nombre mejor, usarlo).
                            string name = displayName ?? "";
                            if (string.IsNullOrEmpty(name))
                            {
                                name = TryGetBlizzardName(code) ?? code;
                            }
    // Instalación: si hay InstallLocation, buscar exe;
    // sino, la card aparece sin exe (solo para matchear
    // el lanzamiento vía --exec="launch <code>").
                            string? exe = null;
                            string installPath = installLoc ?? "";
                            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                                exe = FindMainExe(installPath, name);
                            if (!seen.Add(name)) continue;
                            games.Add(new InstalledGame(name, exe ?? "", "Blizzard",
                                installPath, code, GetBlizzardBannerUrl(code)));
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        return games;
    }

    /// <summary>Nombre humano de un juego de Battle.net por su código (o null si no hay mapeo).</summary>
    private static string? TryGetBlizzardName(string code)
    {
    // Reutiliza el mapa BlizzardBoxArtIds (código → boxId) pero acá
    // necesitamos código → nombre. Mapeo explícito de los más comunes.
        return code.ToUpperInvariant() switch
        {
            "WTCG" => "Hearthstone",
            "FEN" => "Diablo IV",
            "D1" => "Diablo",
            "D3" => "Diablo III",
            "OSI" => "Diablo II: Resurrected",
            "ANBS" => "Diablo Immortal",
            "VIPR" => "Call of Duty: Black Ops 4",
            "ZEUS" => "Call of Duty: Black Ops Cold War",
            "FORE" => "Call of Duty: Vanguard",
            "ODIN" => "Call of Duty: Modern Warfare",
            "AUKS" => "Call of Duty",
            "PRO" => "Overwatch",
            "S2" => "StarCraft II",
            "S1" => "StarCraft",
            "WOW" => "World of Warcraft",
            "W3" => "Warcraft III",
            "HERO" => "Heroes of the Storm",
            _ => null
        };
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
    /// - Registro de desinstalación: GOG Galaxy registra cada juego instalado con
    /// Publisher "GOG.com"; DisplayIcon trae la ruta del exe principal.
    /// - Clave clásica HKLM\SOFTWARE\WOW6432Node\GOG.com\Games con gameName/path/exe.
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
            // que la del launcher (ej. D:\IRONMACE\Dark and Darker).
            if (gameDir == null)
                gameDir = FindDarkAndDarkerDirsGlobal().FirstOrDefault();

            if (gameDir == null) return games;

            var exe = FindDarkAndDarkerExe(gameDir);
            if (exe != null)
                games.Add(new InstalledGame("Dark and Darker", Path.GetFileName(exe), "Blacksmith", gameDir,
                    "", "https://cdn.cloudflare.steamstatic.com/steam/apps/2016590/header.jpg"));
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

    // ===================== Itch.io =====================

    /// <summary>
    /// Detecta juegos instalados con el cliente itch.io. El cliente usa una
    /// base SQLite (butler.db) en %APPDATA%\itch\db. Cada "cave" (instalación)
    /// referencia un game con título y cover_url (CDN público de itch). El exe
    /// real se busca en la carpeta de instalación con el resolver compartido.
    /// Sin itch instalado: no aparece nada (best-effort).
    /// </summary>
    private List<InstalledGame> ScanItch()
    {
        var games = new List<InstalledGame>();
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "itch", "db", "butler.db");
        if (!File.Exists(dbPath)) return games;

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var conn = new SqliteConnection(connStr);
            conn.Open();

    // Esquema de butler.db (confirmado en clientes modernos):
    // caves(id, install_location_id, install_folder_name, custom_install_folder, verdict)
    // games(id, title, cover_url)
    // install_locations(id, path)
    // verdict es JSON con el campo "candidates"[].path (exe candidato).
    // JOIN caves→games por game_id; resolver install dir desde install_locations
    // + install_folder_name/custom_install_folder.
            string sql = @"
                SELECT g.title, g.cover_url,
                       il.path AS loc_path,
                       c.install_folder_name, c.custom_install_folder,
                       c.verdict
                FROM caves c
                LEFT JOIN games g ON c.game_id = g.id
                LEFT JOIN install_locations il ON c.install_location_id = il.id";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    string? title = reader.IsDBNull(0) ? null : reader.GetString(0);
                    string? coverUrl = reader.IsDBNull(1) ? reader.GetString(1) : null;
                    string? locPath = reader.IsDBNull(2) ? null : reader.GetString(2);
                    string? folderName = reader.IsDBNull(3) ? null : reader.GetString(3);
                    string? customFolder = reader.IsDBNull(4) ? null : reader.GetString(5);
                    string? verdictJson = reader.IsDBNull(5) ? null : reader.GetString(5);
                    if (string.IsNullOrEmpty(title)) continue;

    // Resolver carpeta de instalación.
                    string installDir = "";
                    if (!string.IsNullOrEmpty(customFolder))
                        installDir = customFolder;
                    else if (!string.IsNullOrEmpty(locPath) && !string.IsNullOrEmpty(folderName))
                        installDir = Path.Combine(locPath, folderName);
                    else if (!string.IsNullOrEmpty(locPath))
                        installDir = locPath;

    // Buscar exe: primero el candidato del verdict (JSON),
    // sino el resolver compartido por tamaño.
                    string? exeName = null;
                    if (!string.IsNullOrEmpty(verdictJson))
                    {
                        try
                        {
                            using var vdoc = JsonDocument.Parse(verdictJson);
                            if (vdoc.RootElement.TryGetProperty("candidates", out var cands))
                            {
                                foreach (var cand in cands.EnumerateArray())
                                {
                                    if (cand.TryGetProperty("path", out var p))
                                    {
                                        var cp = p.GetString();
                                        if (!string.IsNullOrEmpty(cp))
                                        {
                                            var full = Path.Combine(installDir, cp);
                                            if (File.Exists(full)) { exeName = Path.GetFileName(full); break; }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    if (string.IsNullOrEmpty(exeName) && Directory.Exists(installDir))
                    {
                        var main = FindMainExe(installDir, title);
                        if (main != null) exeName = main;
                    }
                    if (string.IsNullOrEmpty(exeName)) continue;
                    games.Add(new InstalledGame(title, exeName, "itch.io", installDir, "", coverUrl ?? ""));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"InstalledGames: Itch.io butler.db: {ex.Message}");
        }
        return games;
    }

    // ===================== Amazon Games =====================

    /// <summary>
    /// Detecta juegos instalados con la app de Amazon Games. El cliente usa una
    /// base SQLite en %LOCALAPPDATA%\Amazon Games\Data\Games\Sql\GameInstallInfo.sqlite
    /// con tabla DbSet(Id, ProductTitle, InstallDirectory, Installed,
    /// ProductIconUrl, ProductLogoUrl). El exe real se busca en InstallDirectory
    /// con el resolver compartido. Lanzamiento vía URI amazon-games://play/<Id>.
    /// Sin Amazon Games instalado: no aparece nada (best-effort).
    /// </summary>
    private List<InstalledGame> ScanAmazonGames()
    {
        var games = new List<InstalledGame>();
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Amazon Games", "Data", "Games", "Sql", "GameInstallInfo.sqlite");
        if (!File.Exists(dbPath)) return games;

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var conn = new SqliteConnection(connStr);
            conn.Open();

    // DbSet tiene Installed (bit) y los campos de metadatos. Solo listar
    // los que Installed=1. ProductIconUrl/ProductLogoUrl son CDN público.
            string sql = "SELECT Id, ProductTitle, InstallDirectory, ProductIconUrl, ProductLogoUrl FROM DbSet WHERE Installed = 1";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    string? id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    string? title = reader.IsDBNull(1) ? null : reader.GetString(1);
                    string? installDir = reader.IsDBNull(2) ? null : reader.GetString(2);
                    string? iconUrl = reader.IsDBNull(3) ? reader.GetString(3) : null;
                    string? logoUrl = reader.IsDBNull(4) ? reader.GetString(4) : null;
                    if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(installDir)) continue;
                    if (!Directory.Exists(installDir)) continue;
                    var exe = FindMainExe(installDir, title);
                    if (string.IsNullOrEmpty(exe)) continue;
                    games.Add(new InstalledGame(title, exe, "Amazon",
                        installDir, id ?? "", iconUrl ?? logoUrl ?? ""));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"InstalledGames: Amazon Games sqlite: {ex.Message}");
        }
        return games;
    }

    // ===================== Emuladores =====================

    /// <summary>
    /// Detecta emuladores instalados en capas, todas alimentadas por EmulatorCatalog:
    /// Capa 1 (rápida): registro específico del emulador + rutas típicas.
    /// Capa 2: claves Uninstall del sistema matcheando DisplayName contra el catálogo.
    /// Capa 3: carpetas elegidas por el usuario para instalaciones portables.
    /// Capa 4 (optativa, solo con <paramref name="deepScan"/>): unidades fijas/removibles
    /// completas, bajo acción explícita del usuario ("Escaneo profundo" de la biblioteca).
    /// Cada emulador aparece como una card en la biblioteca (lanzable con un
    /// click), igual que BlueStacks: el exe es el proceso real que corre los
    /// juegos, así las reglas de prioridad/afinidad y el badge "En ejecución"
    /// funcionan igual que en los juegos de launchers.
    /// Best-effort: si no está instalado, no aparece.
    /// </summary>
    private List<InstalledGame> ScanEmulators(
        bool deepScan = false,
        IProgress<EmulatorScanProgress>? progress = null)
    {
        var games = new List<InstalledGame>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Carpeta de instalación de un emulador: la del registro si existe, y si no la
        // del propio ejecutable. Sin esto, los emuladores que solo se encuentran en
        // carpetas típicas o portables quedaban con InstallPath vacío y las reglas
        // perdían el match por ruta (solo funcionaba el match exacto por nombre de exe).
        string ResolveInstallPath(string? exePath, string installPath)
        {
            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                return installPath;
            return string.IsNullOrEmpty(exePath) ? "" : Path.GetDirectoryName(exePath) ?? "";
        }

        void Add(string name, string? exePath, string installPath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;
            var full = Path.GetFullPath(exePath);
            if (!found.Add(full)) return;
            // Dedup por nombre: la Capa 1 tiene prioridad sobre las capas 3/4 (el mismo
            // emulador puede encontrarse por caminos distintos).
            if (!foundNames.Add(name)) return;
            games.Add(new InstalledGame(name, Path.GetFileName(exePath), "Emulador", ResolveInstallPath(full, installPath)));
        }

        // Capa 3 y 4: catálogo (nombre canónico + SkipAutoDetect) en vez de la lista
        // hardcodeada de la Capa 1.
        void AddCatalog(EmulatorDefinition def, string exePath, string installPath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;
            if (def.SkipAutoDetect) return;
            var full = Path.GetFullPath(exePath);
            if (!found.Add(full)) return;
            if (!foundNames.Add(def.Name)) return;
            games.Add(new InstalledGame(def.Name, Path.GetFileName(full), "Emulador", ResolveInstallPath(full, installPath)));
        }

        string? FindInDirs(string exeName, params string[] dirs)
        {
            foreach (var d in dirs)
            {
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                // Raíz
                var f = Path.Combine(d, exeName);
                if (File.Exists(f)) return f;
                // Un nivel adentro (ej. subcarpeta bin, build, x64...)
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(d))
                    {
                        var sf = Path.Combine(sub, exeName);
                        if (File.Exists(sf)) return sf;
                    }
                }
                catch { }
            }
            return null;
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // RetroArch: portable (cualquier carpeta) o instalador. Se busca por
        // registro (InstallDir) y rutas típicas. El exe es retroarch.exe.
        var retroDir = GetRegValue(@"SOFTWARE\WOW6432Node\RetroArch", "InstallDir")
            ?? GetRegValue(@"SOFTWARE\RetroArch", "InstallDir");
        Add("RetroArch", FindInDirs("retroarch.exe",
            retroDir ?? "", pf, pf86,
            Path.Combine(pf, "RetroArch"), Path.Combine(pf86, "RetroArch"),
            Path.Combine(la, "RetroArch")),
            retroDir ?? "");

        // Dolphin (GameCube/Wii): portable. Busca Dolphin.exe.
        var dolphinDir = GetRegValue(@"SOFTWARE\Dolphin Emulator", "InstallPath")
            ?? GetRegValue(@"SOFTWARE\WOW6432Node\Dolphin", "InstallPath");
        Add("Dolphin", FindInDirs("Dolphin.exe",
            dolphinDir ?? "", pf, pf86,
            Path.Combine(pf, "Dolphin"), Path.Combine(pf86, "Dolphin"),
            Path.Combine(la, "Dolphin Emulator")),
            dolphinDir ?? "");

        // PCSX2 (PS2): instalador o portable. Busca pcsx2-qtx.exe (Qt) o pcsx2.exe.
        var pcsx2Dir = GetRegValue(@"SOFTWARE\PCSX2", "Install_Dir")
            ?? GetRegValue(@"SOFTWARE\WOW6432Node\PCSX2", "Install_Dir");
        var pcsx2Exe = FindInDirs("pcsx2-qtx.exe", pcsx2Dir ?? "", pf, pf86,
            Path.Combine(pf, "PCSX2"), Path.Combine(pf86, "PCSX2"))
            ?? FindInDirs("pcsx2.exe", pcsx2Dir ?? "", pf, pf86,
            Path.Combine(pf, "PCSX2"), Path.Combine(pf86, "PCSX2"));
        Add("PCSX2", pcsx2Exe, pcsx2Dir ?? "");

        // RPCS3 (PS3): portable. Busca rpcs3.exe.
        Add("RPCS3", FindInDirs("rpcs3.exe",
            pf, pf86,
            Path.Combine(pf, "RPCS3"), Path.Combine(pf86, "RPCS3"),
            Path.Combine(la, "RPCS3")), "");

        // Cemu (Wii U): portable. Busca Cemu.exe.
        Add("Cemu", FindInDirs("Cemu.exe",
            pf, pf86,
            Path.Combine(pf, "Cemu"), Path.Combine(pf86, "Cemu"),
            Path.Combine(la, "Cemu")), "");

        // MAME: instalador o portable. Busca mame.exe o mame64.exe.
        var mameExe = FindInDirs("mame64.exe", pf, pf86,
            Path.Combine(pf, "MAME"), Path.Combine(pf86, "MAME"))
            ?? FindInDirs("mame.exe", pf, pf86,
            Path.Combine(pf, "MAME"), Path.Combine(pf86, "MAME"));
        Add("MAME", mameExe, "");

        // PPSSPP (PSP): instalador o portable. Busca PPSSPPWindows.exe.
        Add("PPSSPP", FindInDirs("PPSSPPWindows.exe",
            pf, pf86,
            Path.Combine(pf, "PPSSPP"), Path.Combine(pf86, "PPSSPP"),
            Path.Combine(la, "PPSSPP")), "");

        // Ryujinx (Switch): portable. Busca Ryujinx.exe.
        Add("Ryujinx", FindInDirs("Ryujinx.exe",
            pf, pf86,
            Path.Combine(pf, "Ryujinx"), Path.Combine(pf86, "Ryujinx"),
            Path.Combine(la, "Ryujinx")), "");

        // Lime3DS (3DS, sucesor de Citra): portable. Busca lime3ds.exe o citra.exe.
        var limeExe = FindInDirs("lime3ds.exe", pf, pf86,
            Path.Combine(pf, "Lime3DS"), Path.Combine(pf86, "Lime3DS"),
            Path.Combine(la, "Lime3DS"))
            ?? FindInDirs("citra-qt.exe", pf, pf86,
            Path.Combine(pf, "Citra"), Path.Combine(pf86, "Citra"));
        Add("Lime3DS", limeExe, "");

        // Mesen (NES/SNES/etc.): portable. Busca Mesen.exe.
        Add("Mesen", FindInDirs("Mesen.exe",
            pf, pf86,
            Path.Combine(pf, "Mesen"), Path.Combine(pf86, "Mesen"),
            Path.Combine(la, "Mesen")), "");

        // bsnes (SNES): portable. Busca bsnes.exe.
        Add("bsnes", FindInDirs("bsnes.exe",
            pf, pf86,
            Path.Combine(pf, "bsnes"), Path.Combine(pf86, "bsnes"),
            Path.Combine(la, "bsnes")), "");

        // FBNeo (arcade): portable. Busca fbneo.exe o fbneo64.exe.
        var fbneoExe = FindInDirs("fbneo.exe", pf, pf86,
            Path.Combine(pf, "FBNeo"), Path.Combine(pf86, "FBNeo"),
            Path.Combine(la, "FBNeo"))
            ?? FindInDirs("fbneo64.exe", pf, pf86,
            Path.Combine(pf, "FBNeo"), Path.Combine(pf86, "FBNeo"));
        Add("FBNeo", fbneoExe, "");

 // ==== Emuladores móviles (Android) ====
 // BlueStacks 5 ya se detecta en ScanStandalone (HD-Player.exe); acá se
 // cubren los demás emuladores Android de escritorio. Todos usan su propio
 // exe como proceso real, así que las reglas de prioridad/afinidad y el
 // badge "En ejecución" funcionan igual que en los demás emuladores.

 // LDPlayer: busca ldnative.exe (>=9) o dnplayer.exe (legacy).
        var ldDir = GetRegValue(@"SOFTWARE\DnPlayer", "InstallDir")
            ?? GetRegValue(@"SOFTWARE\WOW6432Node\DnPlayer", "InstallDir");
        var ldExe = FindInDirs("ldnative.exe", ldDir ?? "", pf, pf86,
            Path.Combine(pf, "LDPlayer"), Path.Combine(pf86, "LDPlayer"),
            Path.Combine(pf, "LDPlayer9"), Path.Combine(pf86, "LDPlayer9"))
            ?? FindInDirs("dnplayer.exe", ldDir ?? "", pf, pf86,
            Path.Combine(pf, "LDPlayer"), Path.Combine(pf86, "LDPlayer"));
        Add("LDPlayer", ldExe, ldDir ?? "");

 // Nox: busca Nox.exe. Instala en "Bignox" o "Nox".
        var noxDir = GetRegValue(@"SOFTWARE\BigNox", "InstallPath")
            ?? GetRegValue(@"SOFTWARE\WOW6432Node\BigNox", "InstallPath");
        Add("NoxPlayer", FindInDirs("Nox.exe", noxDir ?? "", pf, pf86,
            Path.Combine(pf, "Bignox"), Path.Combine(pf86, "Bignox"),
            Path.Combine(pf, "Nox"), Path.Combine(pf86, "Nox"),
            Path.Combine(la, "Bignox")), noxDir ?? "");

 // MEmu: busca MEmu.exe. Registro MemuTop\InstallPath o carpeta MEmu.
        var memuDir = GetRegValue(@"SOFTWARE\MemuTop", "InstallPath")
            ?? GetRegValue(@"SOFTWARE\WOW6432Node\MemuTop", "InstallPath");
        Add("MEmu", FindInDirs("MEmu.exe", memuDir ?? "", pf, pf86,
            Path.Combine(pf, "Microvirt"), Path.Combine(pf86, "Microvirt"),
            Path.Combine(pf, "MEmu"), Path.Combine(pf86, "MEmu")), memuDir ?? "");

 // Genymotion: busca Genymotion.exe (uso personal/eval). Instala en
 // Genymobile/Genymotion.
        Add("Genymotion", FindInDirs("Genymotion.exe", pf, pf86,
            Path.Combine(pf, "Genymobile"), Path.Combine(pf86, "Genymobile"),
            Path.Combine(pf, "Genymotion"), Path.Combine(pf86, "Genymotion")), "");

 // MSI App Player: es un rebrand de BlueStacks 5, así que su exe real es
 // HD-Player.exe (no existe "MSIAppPlayer.exe"). La carpeta lo distingue de BlueStacks.
        Add("MSI App Player", FindInDirs("HD-Player.exe",
            Path.Combine(pf, "MSI", "MSI App Player"), Path.Combine(pf86, "MSI", "MSI App Player"),
            Path.Combine(pf, "MSI App Player"), Path.Combine(pf86, "MSI App Player"),
            Path.Combine(pf, "MSI"), Path.Combine(pf86, "MSI")), "");

 // MuMu Player (NetEase): versiones 12/6, la global y el MuMu viejo (Nemu).
        var mumuExe = FindInDirs("MuMuPlayer.exe",
            Path.Combine(pf, "Netease"), Path.Combine(pf86, "Netease"),
            Path.Combine(pf, "Netease", "MuMuPlayer-12.0"), Path.Combine(pf86, "Netease", "MuMuPlayer-12.0"),
            Path.Combine(pf, "MuMuPlayer-12.0"), Path.Combine(pf86, "MuMuPlayer-12.0"))
            ?? FindInDirs("MuMuPlayerGlobal.exe",
            Path.Combine(pf, "Netease"), Path.Combine(pf86, "Netease"),
            Path.Combine(pf, "Netease", "MuMuPlayerGlobal-12.0"), Path.Combine(pf86, "Netease", "MuMuPlayerGlobal-12.0"))
            ?? FindInDirs("NemuPlayer.exe", pf, pf86,
            Path.Combine(pf, "Nemu"), Path.Combine(pf86, "Nemu"));
        Add("MuMu Player", mumuExe, mumuExe == null ? "" : Path.GetDirectoryName(mumuExe) ?? "");

 // Tencent GameLoop: el launcher está en Tencent\GameLoop\Application.
        Add("GameLoop", FindInDirs("GameLoopLauncher.exe",
            Path.Combine(pf, "Tencent", "GameLoop", "Application"), Path.Combine(pf86, "Tencent", "GameLoop", "Application"),
            Path.Combine(pf, "Tencent", "GameLoop"), Path.Combine(pf86, "Tencent", "GameLoop"),
            Path.Combine(pf, "GameLoop"), Path.Combine(pf86, "GameLoop")), "");

 // Google Play Games para PC: Program Files\Google\Play Games.
        Add("Google Play Games", FindInDirs("GooglePlayGames.exe",
            Path.Combine(pf, "Google", "Play Games"), Path.Combine(pf86, "Google", "Play Games"),
            Path.Combine(pf, "Google"), Path.Combine(pf86, "Google")), "");

 // Waydroid no se detecta: solo corre en Linux/WSL, no en Windows nativo.

        // ---- Capa 3: carpetas elegidas por el usuario (emuladores portables) ----
        foreach (var root in GetEmulatorSearchFolders())
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    var def = EmulatorCatalog.MatchExeFileNameForPath(file);
                    if (def == null) continue;
                    AddCatalog(def, file, Path.GetDirectoryName(file) ?? root);
                }
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"InstalledGames: carpeta de emuladores '{root}': {ex.Message}");
            }
        }

        // ---- Capa 4: unidades completas (solo si el usuario pidió el escaneo profundo) ----
        if (deepScan) ScanEmulatorDrives(AddCatalog, progress);

        return games;
    }

    /// <summary>
    /// Recorre las unidades fijas y removibles buscando emuladores conocidos. Se camina
    /// directorio por directorio (y no con un recorrido recursivo) para poder excluir nombres
    /// concretos como Windows/System32 y no entrar en puntos de reanálisis.
    /// </summary>
    private void ScanEmulatorDrives(
        Action<EmulatorDefinition, string, string> add,
        IProgress<EmulatorScanProgress>? progress)
    {
        var drives = DriveInfo.GetDrives()
            .Where(drive =>
            {
                try
                {
                    return drive.IsReady
                        && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable);
                }
                catch { return false; }
            })
            .ToArray();

        if (drives.Length == 0)
        {
            progress?.Report(new EmulatorScanProgress(100, string.Empty));
            return;
        }

        progress?.Report(new EmulatorScanProgress(0, "Preparando unidades..."));
        for (var driveIndex = 0; driveIndex < drives.Length; driveIndex++)
        {
            var root = drives[driveIndex].RootDirectory.FullName;
            var segmentStart = driveIndex * 100 / drives.Length;
            var segmentEnd = (driveIndex + 1) * 100 / drives.Length;
            var lastPercent = segmentStart;
            progress?.Report(new EmulatorScanProgress(segmentStart, root));

            var pending = new Stack<string>();
            pending.Push(root);
            long inspectedDirectories = 0;
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (ShouldSkipDeepScanDirectory(current)) continue;

                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (!ShouldSkipDeepScanDirectory(directory)) pending.Push(directory);
                    }

                    // Todos los emuladores del catálogo se identifican por un .exe: el filtro
                    // evita enumerar documentos sin sacrificar subcarpetas portables.
                    foreach (var file in Directory.EnumerateFiles(current, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        var def = EmulatorCatalog.MatchExeFileNameForPath(file);
                        if (def != null) add(def, file, Path.GetDirectoryName(file) ?? root);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (Exception ex)
                {
                    _logging.LogWarning($"InstalledGames: escaneo profundo de '{current}': {ex.Message}");
                }

                inspectedDirectories++;
                if (progress != null && inspectedDirectories % 256 == 0)
                {
                    // No hay un total fiable sin recorrer dos veces la unidad: se informa un
                    // avance monotónico dentro de la unidad y se confirma el 100% al terminarla.
                    var segmentSize = Math.Max(1, segmentEnd - segmentStart);
                    var estimatedWithinDrive = (int)Math.Min(segmentSize - 1, inspectedDirectories / 256);
                    var percent = Math.Min(segmentEnd - 1, segmentStart + estimatedWithinDrive);
                    if (percent > lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(new EmulatorScanProgress(percent, root));
                    }
                }
            }

            progress?.Report(new EmulatorScanProgress(segmentEnd, root));
        }
    }

    /// <summary>True si la carpeta no debe recorrerse en un escaneo profundo (sistema, reparse points…).</summary>
    private static bool ShouldSkipDeepScanDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            var name = info.Name;
            return DeepScanExcludedDirectoryNames.Contains(name)
                || name.StartsWith("$WinRE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>Lee un valor string del registro HKLM\{keyPath}\{valueName} (null si no existe).</summary>
    private static string? GetRegValue(string keyPath, string valueName)
    {
        try
        {
            using var reg = Registry.LocalMachine.OpenSubKey(keyPath);
            return reg?.GetValue(valueName) as string;
        }
        catch { return null; }
    }
}
