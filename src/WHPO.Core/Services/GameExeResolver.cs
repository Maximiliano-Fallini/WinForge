using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WHPO.Core.Services;

/// <summary>
/// Resuelve el ejecutable REAL de un juego dentro de su carpeta de instalación,
/// saltándose los stubs que no representan al juego. Sin esto, la detección de
/// "exe principal" elige al más grande y termina con basura:
///   - SMITE 2 → Windows\start_protected_game.exe (stub lanzador de Easy Anti-Cheat;
///     el juego real es Hemingway.exe) → el ícono de bandeja era el de EAC.
///   - CS2 → game\bin\win64\vconsole2.exe (consola de depuración, más grande que
///     cs2.exe) → el ícono de cs2.exe nunca se usaba.
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
        "vconsole", "vconsole2"
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
}
