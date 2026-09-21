using System;
using System.Collections.Generic;

namespace WHPO.Core.Services;

/// <summary>
/// Definición de un emulador soportado por la biblioteca: cómo se llama para la
/// UI, qué ejecutables lo identifican (con variantes de build), dónde se busca
/// instalado (claves de registro + subcarpetas típicas) y si es emulador móvil
/// (Android) o de consola.
/// </summary>
/// <param name="Name">Nombre para mostrar en la biblioteca (ej. "DuckStation").</param>
/// <param name="ExeNames">Nombres de ejecutable que lo identifican (el primero
/// es el preferido; los demás son variantes de build/rama).</param>
/// <param name="RegistryKeys">Claves de registro específicas del emulador
/// (ruta + nombre del valor con la carpeta de instalación). Vacío si no tiene.</param>
/// <param name="TypicalDirs">Subcarpetas típicas relativas a Program Files,
/// Program Files (x86) y LocalAppData. Se busca también directamente en la raíz
/// de cada una.</param>
/// <param name="IsMobile">True = emulador de celular (Android): categoría
/// "mobile_emu" en la biblioteca. False = emulador de consola/arcade.</param>
/// <param name="SkipAutoDetect">True = NO se detecta automáticamente (otro
/// scanner se encarga; ej. BlueStacks lo detecta ScanStandalone). Sigue sirviendo
/// para clasificar altas manuales.</param>
/// <param name="ProcessNames">Procesos que también representan a esta instalación
/// aunque su nombre no sea el del ejecutable principal: las VM/orquestadores de
/// los emuladores Android (NoxVMHandle.exe en Nox, aow_exe.exe en GameLoop...).
/// El enganche (reglas de modo juego) los trata como el mismo juego y el mapa de
/// rutas los registra para que el match por carpeta funcione igual.</param>
public sealed record EmulatorDefinition(
    string Name,
    string[] ExeNames,
    (string Key, string ValueName)[] RegistryKeys,
    string[] TypicalDirs,
    bool IsMobile = false,
    bool SkipAutoDetect = false,
    string[]? ProcessNames = null)
{
    /// <summary>Alias de procesos normalizado (nunca null).</summary>
    public string[] HookProcessNames => ProcessNames ?? Array.Empty<string>();
}

/// <summary>
/// Catálogo central de emuladores soportados (F1): única fuente de verdad para
/// la detección (InstalledGamesService), la clasificación de la biblioteca
/// (GestionarProcesosPage) y el alta manual. Agregar un emulador = agregar una
/// entrada acá, sin tocar código de escaneo.
/// </summary>
public static class EmulatorCatalog
{
    public static readonly IReadOnlyList<EmulatorDefinition> All = new[]
    {
        // ===================== Consolas / arcade =====================

        // RetroArch: portable (cualquier carpeta) o instalador. El exe es retroarch.exe.
        new EmulatorDefinition("RetroArch",
            new[] { "retroarch.exe" },
            new[] { (@"SOFTWARE\WOW6432Node\RetroArch", "InstallDir"), (@"SOFTWARE\RetroArch", "InstallDir") },
            new[] { "RetroArch", "RetroArch-Win64" }),

        // Dolphin (GameCube/Wii).
        new EmulatorDefinition("Dolphin",
            new[] { "Dolphin.exe" },
            new[] { (@"SOFTWARE\Dolphin Emulator", "InstallPath"), (@"SOFTWARE\WOW6432Node\Dolphin", "InstallPath") },
            new[] { "Dolphin", "Dolphin Emulator", "Dolphin-x64" }),

        // PCSX2 (PS2): la rama Qt actual usa pcsx2-qt.exe; se mantienen
        // variantes históricas para instalaciones antiguas.
        new EmulatorDefinition("PCSX2",
            new[] { "pcsx2-qt.exe", "pcsx2-qtx.exe", "pcsx2-qtx64-avx2.exe", "pcsx2.exe", "pcsx2-avx2.exe" },
            new[] { (@"SOFTWARE\PCSX2", "Install_Dir"), (@"SOFTWARE\WOW6432Node\PCSX2", "Install_Dir") },
            new[] { "PCSX2" }),

        // RPCS3 (PS3): portable.
        new EmulatorDefinition("RPCS3",
            new[] { "rpcs3.exe" },
            Array.Empty<(string, string)>(),
            new[] { "RPCS3" }),

        // Cemu (Wii U): portable.
        new EmulatorDefinition("Cemu",
            new[] { "Cemu.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Cemu" }),

        // MAME (arcade): instalador o portable.
        new EmulatorDefinition("MAME",
            new[] { "mame64.exe", "mame.exe", "mameui64.exe" },
            Array.Empty<(string, string)>(),
            new[] { "MAME" }),

        // PPSSPP (PSP): instalador o portable.
        new EmulatorDefinition("PPSSPP",
            new[] { "PPSSPPWindows.exe", "PPSSPPWindows64.exe" },
            Array.Empty<(string, string)>(),
            new[] { "PPSSPP" }),

        // Ryujinx (Switch): portable. Ava = build Avalonia.
        new EmulatorDefinition("Ryujinx",
            new[] { "Ryujinx.exe", "Ryujinx.Ava.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Ryujinx" }),

        // Yuzu (Switch, descontinuado pero muy distribuido): portable.
        new EmulatorDefinition("Yuzu",
            new[] { "yuzu.exe", "yuzu-cmd.exe" },
            Array.Empty<(string, string)>(),
            new[] { "yuzu" }),

        // Sudachi / Suyu (forks de Yuzu).
        new EmulatorDefinition("Sudachi",
            new[] { "sudachi.exe" },
            Array.Empty<(string, string)>(),
            new[] { "sudachi" }),
        new EmulatorDefinition("Suyu",
            new[] { "suyu.exe" },
            Array.Empty<(string, string)>(),
            new[] { "suyu" }),

        // Lime3DS (3DS, sucesor de Citra). Las variantes de Citra clásico caen acá.
        new EmulatorDefinition("Lime3DS",
            new[] { "lime3ds.exe", "citra-qt.exe", "citra.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Lime3DS", "Citra" }),

        // Azahar (3DS, fork comunitario posterior a Lime3DS).
        new EmulatorDefinition("Azahar",
            new[] { "azahar.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Azahar", "azahar" }),

        // DuckStation (PS1): el instalador NSIS registra la versión en el
        // DisplayName (Capa 2 de detección) y hay variantes por optimización de CPU.
        new EmulatorDefinition("DuckStation",
            new[]
            {
                "duckstation-qt-x64-ReleaseLTCG.exe", "duckstation-qt-x64-ReleaseAVX2.exe", "duckstation-qt-x64-ReleaseSSE2.exe",
                "duckstation-qt-x64-Release.exe", "duckstation-qt.exe",
                "duckstation-nogui-x64-Release.exe", "duckstation-nogui.exe",
                "duckstation.exe"
            },
            Array.Empty<(string, string)>(),
            new[] { "DuckStation", "duckstation" }),

        // Xenia (Xbox 360): canary es la rama más distribuida.
        new EmulatorDefinition("Xenia",
            new[] { "xenia.exe", "xenia_canary.exe", "xenia_master.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Xenia", "xenia" }),

        // Xemu (Xbox original).
        new EmulatorDefinition("Xemu",
            new[] { "xemu.exe" },
            Array.Empty<(string, string)>(),
            new[] { "xemu", "Xemu" }),

        // mGBA (GBA/GB/GBC).
        new EmulatorDefinition("mGBA",
            new[] { "mgba-qt.exe", "mgba-sdl.exe", "mgba.exe" },
            Array.Empty<(string, string)>(),
            new[] { "mGBA" }),

        // VisualBoyAdvance-M (GBA/GB/GBC).
        new EmulatorDefinition("VisualBoyAdvance-M",
            new[] { "visualboyadvance-m.exe", "visualboyadvance-m-x64.exe", "VBA-M.exe", "vbam.exe" },
            Array.Empty<(string, string)>(),
            new[] { "visualboyadvance-m", "VBA-M" }),

        // Project64 (N64).
        new EmulatorDefinition("Project64",
            new[] { "Project64.exe", "Project64d.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Project64" }),

        // RMG (Rosalie's Mupen GUI, N64).
        new EmulatorDefinition("RMG",
            new[] { "RMG.exe" },
            Array.Empty<(string, string)>(),
            new[] { "RMG" }),

        // simple64 (N64).
        new EmulatorDefinition("simple64",
            new[] { "simple64-gui.exe", "simple64.exe" },
            Array.Empty<(string, string)>(),
            new[] { "simple64" }),

        // MelonDS (NDS).
        new EmulatorDefinition("melonDS",
            new[] { "melonDS.exe" },
            Array.Empty<(string, string)>(),
            new[] { "melonDS" }),

        // DeSmuME (NDS).
        new EmulatorDefinition("DeSmuME",
            new[] { "DeSmuME_x64.exe", "DeSmuME.exe" },
            Array.Empty<(string, string)>(),
            new[] { "DeSmuME", "desmume" }),

        // Redream (Dreamcast).
        new EmulatorDefinition("Redream",
            new[] { "redream.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Redream" }),

        // Demul (Dreamcast/NAOMI).
        new EmulatorDefinition("Demul",
            new[] { "demul.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Demul", "demul" }),

        // Flycast (Dreamcast/NAOMI).
        new EmulatorDefinition("Flycast",
            new[] { "flycast.exe" },
            Array.Empty<(string, string)>(),
            new[] { "flycast" }),

        // Supermodel (Sega Model 3).
        new EmulatorDefinition("Supermodel",
            new[] { "Supermodel.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Supermodel" }),

        // Play! (PS2, alternativa a PCSX2). "Play.exe" es un nombre genérico:
        // la detección requiere que viva en una carpeta típica del catálogo.
        new EmulatorDefinition("Play!",
            new[] { "Play.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Play!", "Play" }),

        // Ares (multi-sistema, de Near).
        new EmulatorDefinition("Ares",
            new[] { "ares.exe" },
            Array.Empty<(string, string)>(),
            new[] { "ares", "Ares" }),

        // Mednafen (multi-sistema, CLI).
        new EmulatorDefinition("Mednafen",
            new[] { "mednafen.exe" },
            Array.Empty<(string, string)>(),
            new[] { "mednafen" }),

        // Vita3K (PS Vita).
        new EmulatorDefinition("Vita3K",
            new[] { "Vita3K.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Vita3K" }),

        // ScummVM (aventuras gráficas).
        new EmulatorDefinition("ScummVM",
            new[] { "scummvm.exe" },
            Array.Empty<(string, string)>(),
            new[] { "ScummVM" }),

        // TeknoParrot (arcade modernos). La UI es el proceso real.
        new EmulatorDefinition("TeknoParrot",
            new[] { "TeknoParrotUi.exe" },
            Array.Empty<(string, string)>(),
            new[] { "TeknoParrot", "TeknoParrotUI" }),

        // Mesen (NES/SNES/etc.): Mesen2 es la segunda generación.
        new EmulatorDefinition("Mesen",
            new[] { "Mesen.exe", "Mesen2.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Mesen" }),

        // bsnes (SNES).
        new EmulatorDefinition("bsnes",
            new[] { "bsnes.exe" },
            Array.Empty<(string, string)>(),
            new[] { "bsnes" }),

        // FBNeo (arcade/FinalBurn Neo).
        new EmulatorDefinition("FBNeo",
            new[] { "fbneo.exe", "fbneo64.exe" },
            Array.Empty<(string, string)>(),
            new[] { "FBNeo", "FinalBurn Neo" }),

        // ===================== Móviles (Android) =====================
        // BlueStacks 5 NO va acá como detección (lo detecta ScanStandalone con
        // HD-Player.exe), pero está en el catálogo para clasificar altas manuales.

        new EmulatorDefinition("BlueStacks 5",
            new[] { "HD-Player.exe" },
            Array.Empty<(string, string)>(),
            Array.Empty<string>(),
            IsMobile: true,
            SkipAutoDetect: true,
            ProcessNames: new[] { "BlueStacksServices.exe", "BlueStacksHelper.exe" }),

        // LDPlayer: ldnative.exe (>=9) o dnplayer.exe (legacy).
        new EmulatorDefinition("LDPlayer",
            new[] { "ldnative.exe", "dnplayer.exe" },
            new[] { (@"SOFTWARE\DnPlayer", "InstallDir"), (@"SOFTWARE\WOW6432Node\DnPlayer", "InstallDir") },
            new[] { "LDPlayer", "LDPlayer9" },
            IsMobile: true,
            ProcessNames: new[] { "ld9boxheadless.exe", "LdVBoxHeadless.exe" }),

        // Nox: instala en "Bignox" o "Nox".
        // Nox: el juego no corre en Nox.exe (la UI) sino en NoxVMHandle.exe, la VM
        // que levanta el propio emulador; sin el alias, una regla guardada para
        // Nox.exe solo disparaba al abrir la ventana del emulador desde su exe.
        new EmulatorDefinition("NoxPlayer",
            new[] { "Nox.exe" },
            new[] { (@"SOFTWARE\BigNox", "InstallPath"), (@"SOFTWARE\WOW6432Node\BigNox", "InstallPath") },
            new[] { "Bignox", "Nox", "Nox\\bin" },
            IsMobile: true,
            ProcessNames: new[] { "NoxVMHandle.exe" }),

        // MEmu: registro MemuTop.
        new EmulatorDefinition("MEmu",
            new[] { "MEmu.exe" },
            new[] { (@"SOFTWARE\MemuTop", "InstallPath"), (@"SOFTWARE\WOW6432Node\MemuTop", "InstallPath") },
            new[] { "Microvirt", "Microvirt\\MEmu", "MEmu" },
            IsMobile: true,
            ProcessNames: new[] { "MEmuHeadless.exe" }),

        // Genymotion (uso personal/eval).
        new EmulatorDefinition("Genymotion",
            new[] { "Genymotion.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Genymobile", "Genymobile\\Genymotion", "Genymotion" },
            IsMobile: true),

        // MSI App Player: es un rebrand de BlueStacks 5, así que su ejecutable real
        // es HD-Player.exe (no existe ningún "MSIAppPlayer.exe" en las builds que
        // distribuye MSI). Como BlueStacks 5 comparte ese nombre de exe, la detección
        // lo desambigua por carpeta (MatchExeFileNameForPath): los TypicalDirs de acá
        // hacen que una instalación en la carpeta de MSI no se reporte como BlueStacks.
        new EmulatorDefinition("MSI App Player",
            new[] { "HD-Player.exe" },
            new[] { (@"SOFTWARE\BlueStacks_nxt", "InstallDir"), (@"SOFTWARE\WOW6432Node\BlueStacks_nxt", "InstallDir") },
            new[] { "MSI\\MSI App Player", "MSI App Player", "MSI\\BlueStacks" },
            IsMobile: true),

        // MuMu Player (NetEase): instalador propio (versiones 12/6 y la global) y el
        // MuMu viejo "Nemu". Rasgos verificados de la instalación:
        // C:\Program Files\Netease\MuMuPlayer-12.0 (global: MuMuPlayerGlobal-12.0).
        // El juego no corre en MuMuPlayer.exe sino en la VM del emulador, así que se
        // declaran esos procesos como alias para el enganche.
        new EmulatorDefinition("MuMu Player",
            new[] { "MuMuPlayer.exe", "MuMuPlayerGlobal.exe", "NemuPlayer.exe" },
            new[] { (@"SOFTWARE\Netease\MuMuPlayer", "InstallDir"), (@"SOFTWARE\WOW6432Node\Netease\MuMuPlayer", "InstallDir") },
            new[] { "Netease\\MuMuPlayer-12.0", "Netease\\MuMuPlayerGlobal-12.0", "Netease\\MuMuPlayer6", "MuMuPlayer-12.0", "MuMuPlayerGlobal-12.0", "MuMuPlayer6", "Nemu" },
            IsMobile: true,
            ProcessNames: new[] { "MuMuVMMHeadless.exe", "MuMuVMM.exe" }),

        // Tencent GameLoop (ex Tencent Gaming Buddy): el launcher vive en
        // C:\Program Files\Tencent\GameLoop\Application\GameLoopLauncher.exe y el juego
        // corre bajo el motor Android de Tencent (aow_exe.exe / AndroidEmulatorEx.exe),
        // que es exactamente el caso donde el proceso no se llama como el exe.
        new EmulatorDefinition("GameLoop",
            new[] { "GameLoopLauncher.exe", "AndroidEmulatorEx.exe" },
            new[] { (@"SOFTWARE\Tencent\GameLoop", "InstallPath"), (@"SOFTWARE\WOW6432Node\Tencent\GameLoop", "InstallPath") },
            new[] { "Tencent\\GameLoop\\Application", "Tencent\\GameLoop", "Tencent\\AndroidEmulator", "GameLoop" },
            IsMobile: true,
            ProcessNames: new[] { "aow_exe.exe", "AndroidEmulatorEn.exe" }),

        // Google Play Games para PC: app oficial de Google en
        // C:\Program Files\Google\Play Games (con subcarpetas versionadas adentro); el
        // juego corre en la VM crosvm que trae el propio paquete.
        new EmulatorDefinition("Google Play Games",
            new[] { "GooglePlayGames.exe" },
            new[] { (@"SOFTWARE\Google\Play Games", "InstallPath") },
            new[] { "Google\\Play Games", "Google\\Play Games\\current" },
            IsMobile: true,
            ProcessNames: new[] { "crosvm.exe" }),

        // Waydroid no se detecta: solo corre en Linux/WSL, no en Windows nativo.
    };

    /// <summary>
    /// Matchea un nombre de archivo (con o sin extensión, case-insensitive)
    /// contra el catálogo. Ej.: "duckstation-qt-x64-ReleaseSSE2.exe" → DuckStation.
    /// Null si no es un emulador conocido.
    /// </summary>
    /// <summary>
    /// Igual que <see cref="MatchExeFileName"/> pero desambigua por carpeta cuando
    /// varios emuladores comparten nombre de ejecutable: BlueStacks 5 y MSI App
    /// Player son el mismo motor y ambos usan HD-Player.exe, así que el nombre solo
    /// no alcanza. Se prefiere la definición cuya carpeta típica aparece en la ruta;
    /// si ninguna coincide (o hay una sola candidata) se mantiene el orden del catálogo.
    /// </summary>
    public static EmulatorDefinition? MatchExeFileNameForPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var fileName = System.IO.Path.GetFileName(filePath);
        var matches = new List<EmulatorDefinition>();
        foreach (var def in All)
            foreach (var exe in def.ExeNames)
                if (string.Equals(exe, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(def);
                    break;
                }
        if (matches.Count == 0) return MatchExeFileName(fileName);
        if (matches.Count == 1) return matches[0];

        var dir = System.IO.Path.GetDirectoryName(filePath) ?? string.Empty;
        foreach (var def in matches)
            foreach (var typical in def.TypicalDirs)
                if (dir.Contains(typical, StringComparison.OrdinalIgnoreCase))
                    return def;
        return matches[0];
    }

    /// <summary>
    /// Procesos que representan la instalación de un emulador más allá de su exe
    /// principal (VM/orquestadores Android). Vacío si el exe no es de un emulador
    /// del catálogo o si ese emulador no declara alias.
    /// </summary>
    public static string[] HookProcessNames(string? exeFileName)
        => MatchExeFileName(exeFileName)?.HookProcessNames ?? Array.Empty<string>();

    public static EmulatorDefinition? MatchExeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = fileName.Trim();
        foreach (var def in All)
            foreach (var exe in def.ExeNames)
                if (string.Equals(exe, name, StringComparison.OrdinalIgnoreCase))
                    return def;
        // Sin extensión (ej. "duckstation-qt-x64-ReleaseSSE2").
        var baseName = System.IO.Path.GetFileNameWithoutExtension(name);
        foreach (var def in All)
            foreach (var exe in def.ExeNames)
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(exe), baseName, StringComparison.OrdinalIgnoreCase))
                    return def;
        return null;
    }

    /// <summary>
    /// Matchea un DisplayName de una clave Uninstall contra el catálogo: exacto
    /// ("RetroArch") o con versión pegada ("PCSX2 1.7.1234", "DuckStation 0.1.7").
    /// Null si no matchea ningún emulador.
    /// </summary>
    public static EmulatorDefinition? MatchDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var dn = displayName.Trim();
        foreach (var def in All)
        {
            if (string.Equals(dn, def.Name, StringComparison.OrdinalIgnoreCase))
                return def;
            if (dn.StartsWith(def.Name + " ", StringComparison.OrdinalIgnoreCase))
                return def;
        }
        return null;
    }

    /// <summary>¿El exe corresponde a un emulador móvil? Atajo para clasificar.</summary>
    public static bool IsMobile(string? exeFileName) => MatchExeFileName(exeFileName)?.IsMobile ?? false;
}
