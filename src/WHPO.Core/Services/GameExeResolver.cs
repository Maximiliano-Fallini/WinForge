using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WHPO.Core.Services;

/// <summary>
/// Resuelve el ejecutable REAL de un juego dentro de su carpeta de instalación,
/// saltándose los stubs que no representan al juego. Sin esto, la detección de
/// "exe principal" elige al más grande y termina con basura:
/// - SMITE 2 → Windows\start_protected_game.exe (stub lanzador de Easy Anti-Cheat;
/// el juego real es Hemingway.exe) → el ícono de bandeja era el de EAC.
/// - CS2 → game\bin\win64\vconsole2.exe (consola de depuración, más grande que
/// cs2.exe) → el ícono de cs2.exe nunca se usaba.
/// Se comparte entre el escaneo de la biblioteca (InstalledGamesService) y la
/// extracción de íconos (bandeja + cards), para que todos resuelvan igual.
/// </summary>
public static class GameExeResolver
{
    // Nombres (sin extensión) que nunca son "el juego": stubs de anti-cheat,
    // consolas de depuración, desinstaladores y redistribuibles/instaladores.
    private static readonly HashSet<string> StubExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Easy Anti-Cheat
        "start_protected_game",
        "easyanticheat",
        "easyanticheat_eos",
        "easyanticheateos",
        "easyanticheat_eos_setup",
        "easyanticheat_service",
        "easyanticheatlauncher",
        // BattlEye
        "beservice",
        "battleye",
        "belauncher",
        // Anti-Cheat Expert (ACE)
        "anticheatexpert",
        "anticheatexpertservice",
        "acedriver",
        // Consolas / variantes de debug (CS2, Source 2…)
        "vconsole",
        "vconsole2",
        // Desinstaladores
        "unins000",
        "unins001",
        "unins002",
        "uninstall",
        "uninstaller",
        // Redistribuibles / instaladores (suelen ser los .exe más grandes de la carpeta)
        "ue4prereqsetup",
        "ueprereqsetup",
        "ue4redist",
        "vcredist",
        "vcredist_x64",
        "vcredist_x86",
        "vc_redist",
        "dotnetfx40_full_x86_x64",
        "dotnetfx35setup",
        "dxsetup",
        "setup",
        "installer",
        "redist",
        // Launcher de Dark and Darker (Ironmace / Blacksmith): los binarios del
        // launcher nunca son el juego (el juego real es DungeonCrawler.exe)
        "blacksmith",
        "blacksmithim",
        "blacksmithbootstrap",
        "blacksmithworker",
        "debris",
        // Boilerplate de Unreal Engine que viaja en TODOS los juegos UE
        "crashreportclient",
        "epicwebhelper",
        // Epic Online Services / instaladores (el logo de Epic no es el del juego)
        "epiconlineservices",
        "epiconlineservicesinstaller",
        "eosbootstrapper",
        // Crash handlers (Unity) y helpers que no representan al juego
        "unitycrashhandler",
        "unitycrashhandler32",
        "unitycrashhandler64",
        "crashpad_handler",
        // Crash reporters viejos (juegos antiguos: 16-bit, DOS, primeros Win32)
        "crashrpt",
        "crashrpt1400",
        "crashrpt1500",
        "crashreporter",
        // Autorun / autoplay de CD y utilidades de instalación
        "autorun",
        "autoplay",
        "install",
        // DOS4GW: extensor DOS de 16 bits (no es el juego; el juego lo invoca)
        "dos4gw"
    };

    // Fragmentos de ruta (carpeta) que identifican stubs/instaladores anidados.
    private static readonly string[] StubFolderFragments =
    {
        @"\_CommonRedist\",
        @"\EasyAntiCheat\",
        @"\BattlEye\",
        @"\Redist\",
        @"\Redistributables\",
        @"\EpicOnlineServices\",
        @"\Installers\",
        @"\DirectX\",
        // Crash reporters viejos anidados (juegos antiguos)
        @"\CrashRpt\",
        @"\CrashReport\",
        // Cualquier cosa bajo Engine\ de un juego UE (redist, crash reporter, helpers)
        @"\Engine\"
    };

    // Stubs que NUNCA son el exe principal de un juego real: si la detección los
    // eligió, la caché quedó mal y hay que re-escannear. Los instaladores y
    // desinstaladores quedan afuera: un juego-redistribuible puede tener solo esos.
    private static readonly HashSet<string> MisdetectedStubNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "start_protected_game",
        "easyanticheat", "easyanticheat_eos", "easyanticheateos", "easyanticheat_eos_setup",
        "easyanticheat_service", "easyanticheatlauncher",
        "beservice", "battleye", "belauncher",
        "anticheatexpert", "anticheatexpertservice", "acedriver",
        "vconsole", "vconsole2",
        // Launcher de Dark and Darker (Ironmace / Blacksmith): si la caché quedó
        // con el launcher como exe del juego, es una detección vieja incorrecta.
        "blacksmith", "blacksmithim", "blacksmithbootstrap", "blacksmithworker", "debris"
    };

    /// <summary>¿El archivo es un stub (anti-cheat/consola/desinstalador/instalador)?</summary>
    public static bool IsStubExe(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (StubExeNames.Contains(name)) return true;
            foreach (var frag in StubFolderFragments)
                if (path.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// ¿El archivo es un stub solo por su NOMBRE (anti-cheat, launcher, instalador)?
    /// No mira la carpeta: sirve para entradas manuales, cuya ruta elegida puede
    /// estar anidada y no debe depender de los fragmentos de carpeta.
    /// </summary>
    public static bool IsStubExeName(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return StubExeNames.Contains(Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// ¿El exe es un stub que delata una detección vieja INCORRECTA del exe principal
    /// (anti-cheat o consola de debug)? Si un juego quedó cacheado con este exe, hay
    /// que descartar la caché y re-escannear con el resolver de stubs.
    /// </summary>
    public static bool IsMisdetectedStubExe(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return IsStubExe(path)
            && MisdetectedStubNames.Contains(Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// Mejor exe del juego dentro de su carpeta de instalación. Misma selección que
    /// el escaneo de la biblioteca (FindMainExePath): prefiere el exe cuyo nombre
    /// coincide con la carpeta del juego y, si no, el más grande que NO sea un stub
    /// (anti-cheat, consolas, instaladores, crash handlers…). Sin esto, un juego
    /// podía resolver al logo genérico de otro exe — ej. Fall Guys →
    /// EpicOnlineServicesInstaller.exe (logo de Epic) y Phasmophobia →
    /// UnityCrashHandler64.exe (logo de Unity).
    /// </summary>
    public static string? FindBestGameExePath(string? installPath)
        => FindMainExePath(installPath ?? "");

    /// <summary>
    /// Exe principal del juego dentro de su carpeta de instalación (recorriendo hasta
    /// 4 niveles): prefiere coincidencia exacta con el nombre de la carpeta y, si no,
    /// el exe más grande que NO sea un stub. Si solo había stubs, devuelve el más
    /// grande (mejor que nada). Fuente única de verdad: biblioteca, bandeja y alias
    /// resuelven SIEMPRE el mismo exe.
    /// </summary>
    public static string? FindMainExePath(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return null;
        try
        {
            var exes = new List<string>();
            int budget = 800;
            CollectExes(gameDir, exes, 0, 4, ref budget);
            return SelectMainExe(exes, Path.GetFileName(gameDir.TrimEnd('\\')));
        }
        catch
        {
            return null;
        }
    }

    private static string? SelectMainExe(List<string> exes, string folderName)
    {
        if (exes.Count == 0) return null;
        if (exes.Count == 1) return exes[0];

        // Preferir coincidencia exacta con el nombre de la carpeta del juego,
        // siempre que no sea un stub (ej. Phasmophobia → Phasmophobia.exe y no
        // UnityCrashHandler64.exe, que es más grande).
        foreach (var e in exes)
            if (!IsStubExe(e)
                && Path.GetFileNameWithoutExtension(e).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                return e;

        // Si no, el más grande que NO sea un stub (los instaladores/crash handlers
        // suelen ser los más grandes y NO son el juego).
        string? biggest = null;
        long biggestLen = -1;
        foreach (var e in exes)
        {
            if (IsStubExe(e)) continue;
            try
            {
                var len = new FileInfo(e).Length;
                if (len > biggestLen) { biggestLen = len; biggest = e; }
            }
            catch { }
        }
        // Si solo había stubs, igual devolver el más grande (mejor que nada).
        if (biggest == null)
        {
            foreach (var e in exes)
            {
                try
                {
                    var len = new FileInfo(e).Length;
                    if (len > biggestLen) { biggestLen = len; biggest = e; }
                }
                catch { }
            }
        }
        return biggest ?? exes[0];
    }

    /// <summary>
    /// Rutas de los stubs presentes en la carpeta de instalación (anti-cheat,
    /// consolas, desinstaladores). Sirve para mapear favoritos viejos guardados con
    /// el nombre del stub al exe real y que sigan mostrando nombre e ícono correctos.
    /// </summary>
    public static List<string> FindStubExePaths(string? installPath)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) return result;
        try
        {
            var exes = new List<string>();
            int budget = 800;
            CollectExes(installPath, exes, 0, 4, ref budget);
            foreach (var e in exes)
                if (IsStubExe(e))
                    result.Add(e);
        }
        catch { }
        return result;
    }

    /// <summary>Recorre carpetas buscando .exe hasta `maxDepth` niveles, acotado por presupuesto.</summary>
    internal static void CollectExes(string dir, List<string> exes, int depth, int maxDepth, ref int budget)
    {
        if (depth > maxDepth || budget <= 0) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (budget-- <= 0) return;
                exes.Add(f);
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if (budget-- <= 0) return;
                CollectExes(d, exes, depth + 1, maxDepth, ref budget);
            }
        }
        catch { }
    }

    // ===================== Verificación de identidad de emuladores =====================

    // Tamaño mínimo (bytes) del build real de cada emulador, por nombre canónico.
    // Es un piso ANTI-FALSO-POSITIVO, no un chequeo de versión: busca descartar
    // wrappers/launchers/updaters que usan el nombre del emulador pero miden unos
    // pocos KB o MB porque apuntan a otro lado. Cada piso queda MUY por debajo del
    // tamaño real (que suele ser 5-20x mayor) para no falso-negativizar portables
    // recortados ni builds viejos.
    private static readonly Dictionary<string, long> KnownEmulatorMinSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Emuladores de consolas / arcade: los núcleos reales son pesados.
        ["RetroArch"] = 4_000_000,
        ["Dolphin"] = 8_000_000,
        ["PCSX2"] = 8_000_000,
        ["RPCS3"] = 10_000_000,
        ["Cemu"] = 8_000_000,
        ["DuckStation"] = 4_000_000,
        ["mGBA"] = 1_000_000,
        ["melonDS"] = 500_000,
        ["DeSmuME"] = 500_000,
        ["DOSBox"] = 500_000,
        ["ScummVM"] = 5_000_000,
        ["MAME"] = 2_000_000,
        ["PPSSPP"] = 2_000_000,
        ["Flycast"] = 2_000_000,
        ["Demul"] = 2_000_000,
        ["Supermodel"] = 1_000_000,
        ["Project64"] = 1_000_000,
        ["xemu"] = 8_000_000,
        ["Xenia"] = 8_000_000,
        ["Redream"] = 1_000_000,
        ["Ryujinx"] = 8_000_000,
        ["Lime3DS"] = 2_000_000,
        ["Citra"] = 2_000_000,
        ["Mesen"] = 1_000_000,
        ["bsnes"] = 1_000_000,
        ["FBNeo"] = 1_000_000,
        // Emuladores móviles: todos usan virtualización, decenas de MB como mínimo.
        ["BlueStacks 5"] = 10_000_000,
        ["LDPlayer"] = 10_000_000,
        ["NoxPlayer"] = 2_000_000,
        ["MEmu"] = 2_000_000,
        ["MuMu Player"] = 5_000_000,
        ["GameLoop"] = 5_000_000,
        ["MSI App Player"] = 10_000_000,
        ["Genymotion"] = 2_000_000,
    };

    /// <summary>
    /// Mapa emulador → (empresas, productos) aceptados según los metadatos de versión
    /// del exe (CompanyName/ProductName). Un exe CON metadatos solo pasa si delata a
    /// la familia del emulador; un exe SIN metadatos (portable típico) pasa sin este
    /// filtro. Las familias agrupan forks/ports legítimos (Citra dentro de Lime3DS,
    /// Bluestacks dentro de MSI App Player…) para no falso-negativizar herencias.
    /// </summary>
    private static readonly Dictionary<string, (string[] Companies, string[] Products)> EmulatorFamilies =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["RetroArch"] = (new[] { "libretro", "retroarch" }, new[] { "retroarch", "libretro" }),
        ["Dolphin"] = (new[] { "dolphin" }, new[] { "dolphin", "gamecube", "wii", "ishiruka" }),
        ["PCSX2"] = (new[] { "pcsx2" }, new[] { "pcsx2" }),
        ["RPCS3"] = (new[] { "rpcs3" }, new[] { "rpcs3" }),
        ["Cemu"] = (new[] { "cemu", "exzap" }, new[] { "cemu" }),
        ["DuckStation"] = (new[] { "duckstation" }, new[] { "duckstation" }),
        ["mGBA"] = (new[] { "mgba", "endrift", "pfau" }, new[] { "mgba" }),
        ["melonDS"] = (new[] { "melonds", "arisotura" }, new[] { "melonds", "melon ds" }),
        ["DeSmuME"] = (new[] { "desmume" }, new[] { "desmume" }),
        ["DOSBox"] = (new[] { "dosbox" }, new[] { "dosbox" }),
        ["ScummVM"] = (new[] { "scummvm" }, new[] { "scummvm" }),
        ["MAME"] = (new[] { "mamedev", "mame" }, new[] { "mame" }),
        ["PPSSPP"] = (new[] { "ppsspp", "henrik" }, new[] { "ppsspp" }),
        ["Flycast"] = (new[] { "flycast", "flyinghead" }, new[] { "flycast" }),
        ["Demul"] = (new[] { "demul" }, new[] { "demul" }),
        ["Supermodel"] = (new[] { "supermodel" }, new[] { "supermodel" }),
        ["Project64"] = (new[] { "project64" }, new[] { "project64" }),
        ["xemu"] = (new[] { "xemu" }, new[] { "xemu", "xbox" }),
        ["Xenia"] = (new[] { "xenia" }, new[] { "xenia", "xbox" }),
        ["Redream"] = (new[] { "redream", "inovation" }, new[] { "redream" }),
        ["Ryujinx"] = (new[] { "ryujinx" }, new[] { "ryujinx" }),
        ["Lime3DS"] = (new[] { "lime3ds", "citra" }, new[] { "lime3ds", "citra" }),
        ["Citra"] = (new[] { "citra" }, new[] { "citra" }),
        ["Mesen"] = (new[] { "mesen" }, new[] { "mesen" }),
        ["bsnes"] = (new[] { "bsnes", "byuu" }, new[] { "bsnes" }),
        ["FBNeo"] = (new[] { "fbneo", "finalburn" }, new[] { "fbneo", "finalburn" }),
        ["BlueStacks 5"] = (new[] { "bluestacks", "now.gg", "nowgg" }, new[] { "bluestacks", "now.gg", "nowgg" }),
        ["LDPlayer"] = (new[] { "ldplayer", "leidian", "xuanzhi" }, new[] { "ldplayer", "leidian" }),
        ["NoxPlayer"] = (new[] { "bignox", "nox" }, new[] { "nox" }),
        ["MEmu"] = (new[] { "microvirt", "memu" }, new[] { "memu", "microvirt" }),
        ["MuMu Player"] = (new[] { "netease", "mumu" }, new[] { "mumu", "netease" }),
        ["GameLoop"] = (new[] { "tencent", "gameloop" }, new[] { "gameloop", "tencent" }),
        ["MSI App Player"] = (new[] { "msi", "bluestacks" }, new[] { "msi", "bluestacks" }),
        ["Genymotion"] = (new[] { "genymobile", "genymotion" }, new[] { "genymotion" }),
    };

    /// <summary>
    /// Mapa emulador → nombres (sin extensión) de sus exes reales, incluidas las
    /// variantes de builds oficiales (pcsx2-qtx/-avx2, xenia_canary, mgba-qt…).
    /// NO incluye instaladores ni updaters. Es el espejo de las definiciones de
    /// detección de InstalledGamesService: sirve para verificar exes encontrados
    /// en búsquedas genéricas (ej. entradas viejas de la caché).
    /// </summary>
    private static readonly Dictionary<string, string[]> EmulatorCandidateExes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RetroArch"] = new[] { "retroarch" },
        ["Dolphin"] = new[] { "dolphin" },
        ["PCSX2"] = new[] { "pcsx2", "pcsx2-qtx", "pcsx2-avx2", "pcsx2-avx" },
        ["RPCS3"] = new[] { "rpcs3" },
        ["Cemu"] = new[] { "cemu" },
        ["DuckStation"] = new[] { "duckstation" },
        ["mGBA"] = new[] { "mgba", "mgba-qt", "mgba-sdl" },
        ["melonDS"] = new[] { "melonds" },
        ["DeSmuME"] = new[] { "desmume", "desmume-x64" },
        ["DOSBox"] = new[] { "dosbox", "dosbox-x", "dosbox_staging" },
        ["ScummVM"] = new[] { "scummvm" },
        ["MAME"] = new[] { "mame", "mame64", "mameui64" },
        ["PPSSPP"] = new[] { "ppsspp", "ppssppwindows", "ppssppwindows64" },
        ["Flycast"] = new[] { "flycast", "flycast-dojo" },
        ["Demul"] = new[] { "demul" },
        ["Supermodel"] = new[] { "supermodel" },
        ["Project64"] = new[] { "project64" },
        ["xemu"] = new[] { "xemu" },
        ["Xenia"] = new[] { "xenia", "xenia_canary", "xenia-canary" },
        ["Redream"] = new[] { "redream" },
        ["Ryujinx"] = new[] { "ryujinx", "ryujinx-ava" },
        ["Lime3DS"] = new[] { "lime3ds", "lime3ds-qt", "lime3ds-sdl" },
        ["Citra"] = new[] { "citra", "citra-qt" },
        ["Mesen"] = new[] { "mesen", "mesen-s" },
        ["bsnes"] = new[] { "bsnes" },
        ["FBNeo"] = new[] { "fbneo", "fbneo64" },
        ["BlueStacks 5"] = new[] { "hd-player" },
        ["LDPlayer"] = new[] { "ldnative", "dnplayer" },
        ["NoxPlayer"] = new[] { "nox" },
        ["MEmu"] = new[] { "memu" },
        ["MuMu Player"] = new[] { "mumuplayer", "mumumain", "mumuplayerglobal" },
        ["GameLoop"] = new[] { "gameloop" },
        ["MSI App Player"] = new[] { "msiappplayer" },
        ["Genymotion"] = new[] { "genymotion" },
    };

    /// <summary>
    /// Puerta de identidad para un exe candidato a emulador: tiene que pasar el
    /// triple filtro nombre + tamaño + metadatos. Devuelve false ante cualquier
    /// señal de que el archivo solo "parece" el emulador:
    /// - stub (anti-cheat, instalador, crash handler) según IsStubExe;
    /// - tamaño por debajo del piso del build real (wrapper, updater, instalador);
    /// - metadatos de versión de OTRA identidad (CompanyName/Product que no
    ///   corresponden a la familia del emulador).
    /// Un exe portable sin metadatos pasa solo si el nombre y el tamaño cuadran.
    /// </summary>
    public static bool CheckEmulatorIdentity(string emuName, string exePath)
    {
        if (string.IsNullOrEmpty(emuName) || string.IsNullOrEmpty(exePath)) return false;
        try
        {
            if (IsStubExe(exePath)) return false;
            long len = new FileInfo(exePath).Length;
            long min = KnownEmulatorMinSizes.TryGetValue(emuName, out var m) ? m : 2_000_000;
            if (len < min) return false;
            var (company, product) = ReadVersionInfo(exePath);
            return MatchesEmulatorFamily(company, product, emuName);
        }
        catch { return false; }
    }

    /// <summary>
    /// ¿Este exe (hallado en una búsqueda genérica, sin definición de emulador a
    /// mano) es un emulador conocido y pasa la verificación de identidad? Primero
    /// mapea el nombre del archivo al emulador dueño; si nadie lo reclama, no es
    /// emulador. Sirve para validar entradas viejas de caché y exes sueltos.
    /// </summary>
    public static bool IsKnownEmulatorExe(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        try
        {
            string name = Path.GetFileNameWithoutExtension(exePath);
            foreach (var kv in EmulatorCandidateExes)
                if (kv.Value.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return CheckEmulatorIdentity(kv.Key, exePath);
        }
        catch { }
        return false;
    }

    private static bool MatchesEmulatorFamily(string? company, string? product, string emuName)
    {
        if (!EmulatorFamilies.TryGetValue(emuName, out var fam))
            return true;                                       // sin familia declarada: sin filtro
        if (string.IsNullOrEmpty(company) && string.IsNullOrEmpty(product))
            return true;                                       // portable sin metadatos: sin filtro
        var c = (company ?? "").ToLowerInvariant();
        var p = (product ?? "").ToLowerInvariant();
        foreach (var k in fam.Companies) if (c.Contains(k)) return true;
        foreach (var k in fam.Products) if (p.Contains(k)) return true;
        return false;                                          // metadatos de otra identidad
    }

    [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileVersionInfoSizeW(string fileName, out uint handle);

    [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileVersionInfoW(string fileName, uint handle, uint dataSize, IntPtr data);

    [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool VerQueryValueW(IntPtr block, string subBlock, out IntPtr buffer, out uint len);

    /// <summary>CompanyName + ProductName del recurso VS_VERSION_INFO del exe ("" si no tiene).</summary>
    private static (string Company, string Product) ReadVersionInfo(string exePath)
    {
        try
        {
            uint size = GetFileVersionInfoSizeW(exePath, out _);
            if (size == 0) return ("", "");
            IntPtr data = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetFileVersionInfoW(exePath, 0, size, data)) return ("", "");
                if (!VerQueryValueW(data, "\\VarFileInfo\\Translation", out IntPtr transPtr, out uint transLen)
                    || transLen < 4 || transPtr == IntPtr.Zero)
                    return ("", "");
                int langId = Marshal.ReadInt16(transPtr);
                int codePage = Marshal.ReadInt16(transPtr, 2);
                string prefix = $"\\StringFileInfo\\{langId:X4}{codePage:X4}\\";
                return (QueryVersionString(data, prefix + "CompanyName"),
                        QueryVersionString(data, prefix + "ProductName"));
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        catch { return ("", ""); }
    }

    private static string QueryVersionString(IntPtr data, string path)
    {
        try
        {
            if (!VerQueryValueW(data, path, out IntPtr ptr, out uint len) || ptr == IntPtr.Zero || len == 0)
                return "";
            return Marshal.PtrToStringUni(ptr, (int)len - 1) ?? "";
        }
        catch { return ""; }
    }
}
