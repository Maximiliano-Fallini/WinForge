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
/// <param name="RomArgTemplate">Plantilla de argumentos para lanzar una ROM
/// concreta, con {rom} como marcador de la RUTA entrecomillada (ej. "-e {rom}")
/// o {set} como marcador del NOMBRE DE SET sin extensión (MAME/FBNeo: no aceptan
/// una ruta, solo el nombre corto del set). Null = sin CLI de ROM documentada: el
/// emulador se lanza vacío, como siempre. Solo se rellena con sintaxis verificada
/// en docs oficiales o frontends comprobados (auditoría de emuladores, Fase 0).</param>
/// <param name="RomExtensions">Extensiones de ROM confirmadas para el
/// emulador (con punto, ej. ".iso"). Null = la plantilla aplica a cualquier
/// ruta (CLI confirmada, extensiones no exhaustivas en la documentación).</param>
/// <param name="ChildProcessesInheritRule">Comportamiento de reglas ante un
/// proceso emulado separado: si el emulador ejecuta el juego en un proceso hijo
/// (núcleo/GUI separados, helper), ese hijo hereda la regla activa del emulador
/// (general o por ROM). True por defecto: la topología exacta (single vs child)
/// NO está verificada en la documentación de ningún emulador de consola, así que
/// la herencia es el superconjunto seguro (sin hijos, no cambia nada). Poner
/// False solo si se verifica que un emulador concreto no debe delegar reglas.</param>
public sealed record EmulatorDefinition(
    string Name,
    string[] ExeNames,
    (string Key, string ValueName)[] RegistryKeys,
    string[] TypicalDirs,
    bool IsMobile = false,
    bool SkipAutoDetect = false,
    string[]? ProcessNames = null,
    string? RomArgTemplate = null,
    string[]? RomExtensions = null,
    bool ChildProcessesInheritRule = true)
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

        // Dolphin (GameCube/Wii). CLI oficial: `-b -e <archivo>` (-b sin UI;
        // -e exige el archivo). El juego corre en el propio Dolphin.exe.
        new EmulatorDefinition("Dolphin",
            new[] { "Dolphin.exe" },
            new[] { (@"SOFTWARE\Dolphin Emulator", "InstallPath"), (@"SOFTWARE\WOW6432Node\Dolphin", "InstallPath") },
            new[] { "Dolphin", "Dolphin Emulator", "Dolphin-x64" },
            RomArgTemplate: "-b -e {rom}", RomExtensions: new[] { ".iso", ".gcz", ".wbfs", ".wia", ".rvz" }),

        // PCSX2 (PS2): la rama Qt actual usa pcsx2-qt.exe; se mantienen
        // variantes históricas para instalaciones antiguas. CLI oficial de la rama
        // Qt: `-batch` (sin UI) y `--` antes de la ruta (protege guiones/espacios).
        // El juego corre en el propio pcsx2-qt.exe (GS y EE son hilos, no procesos).
        new EmulatorDefinition("PCSX2",
            new[] { "pcsx2-qt.exe", "pcsx2.exe", "pcsx2-avx2.exe" },
            new[] { (@"SOFTWARE\PCSX2", "Install_Dir"), (@"SOFTWARE\WOW6432Node\PCSX2", "Install_Dir") },
            new[] { "PCSX2" },
            RomArgTemplate: "-batch -- {rom}", RomExtensions: new[] { ".iso", ".cso", ".chd" }),

        // RPCS3 (PS3): portable.
        new EmulatorDefinition("RPCS3",
            new[] { "rpcs3.exe" },
            Array.Empty<(string, string)>(),
            new[] { "RPCS3" },
            RomArgTemplate: "{rom}", RomExtensions: new[] { ".bin", ".pkg" }),

        // Cemu (Wii U): portable.
        new EmulatorDefinition("Cemu",
            new[] { "Cemu.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Cemu" },
            RomArgTemplate: "-g {rom}", RomExtensions: new[] { ".wud", ".wux", ".rpx", ".wua" }),

        // MAME (arcade): instalador o portable. NO acepta una ruta de ROM: recibe
        // el nombre corto del set (el zip sin extensión) y lo busca en -rompath.
        // Por eso la plantilla usa {set} y no {rom}.
        new EmulatorDefinition("MAME",
            new[] { "mame64.exe", "mame.exe", "mameui64.exe" },
            Array.Empty<(string, string)>(),
            new[] { "MAME" },
            RomArgTemplate: "{set}", RomExtensions: new[] { ".zip", ".7z" }),

        // PPSSPP (PSP): instalador o portable.
        new EmulatorDefinition("PPSSPP",
            new[] { "PPSSPPWindows.exe", "PPSSPPWindows64.exe" },
            Array.Empty<(string, string)>(),
            new[] { "PPSSPP" }),

        // Ryujinx (Switch): portable. Ava = build Avalonia.
        new EmulatorDefinition("Ryujinx",
            new[] { "Ryujinx.exe", "Ryujinx.Ava.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Ryujinx" },
            RomArgTemplate: "{rom}"),

        // Yuzu (Switch, descontinuado pero muy distribuido): portable.
        new EmulatorDefinition("Yuzu",
            new[] { "yuzu.exe", "yuzu-cmd.exe" },
            Array.Empty<(string, string)>(),
            new[] { "yuzu" },
            RomArgTemplate: "-g {rom}"),

        // Sudachi / Suyu (forks de Yuzu). Cada uno trae su binario -cmd.
        new EmulatorDefinition("Sudachi",
            new[] { "sudachi.exe", "sudachi-cmd.exe" },
            Array.Empty<(string, string)>(),
            new[] { "sudachi" }),
        new EmulatorDefinition("Suyu",
            new[] { "suyu.exe", "suyu-cmd.exe" },
            Array.Empty<(string, string)>(),
            new[] { "suyu" },
            RomArgTemplate: "-g {rom}"),

        // Sucesores activos de Yuzu (2024-2026): Eden, Citron y Torzu.
        // El juego corre dentro del exe del emulador (un solo proceso),
        // así que la regla por nombre + carpeta alcanza sin alias.
        new EmulatorDefinition("Eden",
            new[] { "eden.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Eden", "eden" },
            RomArgTemplate: "-g {rom}"),
        new EmulatorDefinition("Citron",
            new[] { "citron.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Citron", "citron" },
            RomArgTemplate: "-g {rom}"),
        new EmulatorDefinition("Torzu",
            new[] { "torzu.exe", "torzu-cmd.exe", "yuzu.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Torzu", "torzu" }),

        // shadPS4 (PS4): build Qt documentada como shadPS4.exe.
        new EmulatorDefinition("shadPS4",
            new[] { "shadPS4.exe" },
            Array.Empty<(string, string)>(),
            new[] { "shadPS4", "shadps4" },
            RomArgTemplate: "{rom}"),

        // DOSBox (DOS): core, fork X y Staging. CLI de un solo proceso.
        new EmulatorDefinition("DOSBox",
            new[] { "dosbox.exe", "dosbox-x.exe", "dosbox_staging.exe" },
            Array.Empty<(string, string)>(),
            new[] { "DOSBox", "DOSBox-X", "DOSBox Staging" }),

        // Lime3DS (3DS, sucesor de Citra). Build real documentada: lime-qt.exe;
        // se conserva lime3ds.exe por compatibilidad y las variantes de Citra clásico.
        new EmulatorDefinition("Lime3DS",
            new[] { "lime-qt.exe", "lime3ds.exe", "lime3ds-qt.exe", "citra-qt.exe", "citra.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Lime3DS", "Citra" }),

        // Azahar (3DS, fork comunitario posterior a Lime3DS).
        new EmulatorDefinition("Azahar",
            new[] { "azahar.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Azahar", "azahar" }),

        // DuckStation (PS1): el instalador NSIS registra la versión en el
        // DisplayName (Capa 2 de detección) y hay variantes por optimización de CPU.
        // CLI oficial: `-batch` (sin ventana principal) y `--` antes de la ruta.
        new EmulatorDefinition("DuckStation",
            new[]
            {
                "duckstation-qt-x64-ReleaseLTCG.exe", "duckstation-qt-x64-ReleaseAVX2.exe", "duckstation-qt-x64-ReleaseSSE2.exe",
                "duckstation-qt-x64-Release.exe", "duckstation-qt.exe",
                "duckstation-nogui-x64-Release.exe", "duckstation-nogui.exe",
                "duckstation.exe"
            },
            Array.Empty<(string, string)>(),
            new[] { "DuckStation", "duckstation" },
            RomArgTemplate: "-batch -- {rom}", RomExtensions: new[] { ".bin", ".cue", ".chd", ".ecm", ".mds", ".mdf", ".ccd", ".pbp" }),

        // Xenia (Xbox 360): canary es la rama más distribuida (guion y guion bajo).
        new EmulatorDefinition("Xenia",
            new[] { "xenia.exe", "xenia_canary.exe", "xenia-canary.exe", "xenia_master.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Xenia", "xenia" },
            RomArgTemplate: "{rom}", RomExtensions: new[] { ".xex" }),

        // Xemu (Xbox original).
        new EmulatorDefinition("Xemu",
            new[] { "xemu.exe" },
            Array.Empty<(string, string)>(),
            new[] { "xemu", "Xemu" },
            RomArgTemplate: "-dvd_path {rom}", RomExtensions: new[] { ".iso" }),

        // mGBA (GBA/GB/GBC).
        new EmulatorDefinition("mGBA",
            new[] { "mgba-qt.exe", "mgba-sdl.exe", "mgba.exe" },
            Array.Empty<(string, string)>(),
            new[] { "mGBA" },
            RomArgTemplate: "{rom}"),

        // VisualBoyAdvance-M (GBA/GB/GBC).
        new EmulatorDefinition("VisualBoyAdvance-M",
            new[] { "visualboyadvance-m.exe", "visualboyadvance-m-x64.exe", "VBA-M.exe", "vbam.exe" },
            Array.Empty<(string, string)>(),
            new[] { "visualboyadvance-m", "VBA-M" }),

        // Project64 (N64).
        new EmulatorDefinition("Project64",
            new[] { "Project64.exe", "Project64d.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Project64" },
            RomArgTemplate: "{rom}"),

        // RMG (Rosalie's Mupen GUI, N64).
        new EmulatorDefinition("RMG",
            new[] { "RMG.exe" },
            Array.Empty<(string, string)>(),
            new[] { "RMG" },
            RomArgTemplate: "{rom}"),

        // simple64 (N64).
        new EmulatorDefinition("simple64",
            new[] { "simple64-gui.exe", "simple64.exe" },
            Array.Empty<(string, string)>(),
            new[] { "simple64" }),

        // MelonDS (NDS).
        new EmulatorDefinition("melonDS",
            new[] { "melonDS.exe" },
            Array.Empty<(string, string)>(),
            new[] { "melonDS" },
            RomArgTemplate: "{rom}", RomExtensions: new[] { ".nds" }),

        // DeSmuME (NDS).
        new EmulatorDefinition("DeSmuME",
            new[] { "DeSmuME_x64.exe", "DeSmuME.exe" },
            Array.Empty<(string, string)>(),
            new[] { "DeSmuME", "desmume" }),

        // Redream (Dreamcast).
        new EmulatorDefinition("Redream",
            new[] { "redream.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Redream" },
            RomArgTemplate: "{rom}", RomExtensions: new[] { ".gdi", ".cdi", ".cue", ".chd" }),

        // Demul (Dreamcast/NAOMI).
        new EmulatorDefinition("Demul",
            new[] { "demul.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Demul", "demul" }),

        // Flycast (Dreamcast/NAOMI). Dojo es la variante netplay.
        new EmulatorDefinition("Flycast",
            new[] { "flycast.exe", "flycast-dojo.exe" },
            Array.Empty<(string, string)>(),
            new[] { "flycast" },
            RomArgTemplate: "{rom}", RomExtensions: new[] { ".cdi", ".gdi", ".chd", ".cue", ".bin", ".elf", ".zip", ".7z" }),

        // Supermodel (Sega Model 3).
        new EmulatorDefinition("Supermodel",
            new[] { "Supermodel.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Supermodel" },
            RomArgTemplate: "{rom}", RomExtensions: new[] { ".zip" }),

        // Play! (PS2, alternativa a PCSX2). "Play.exe" es un nombre genérico:
        // la detección requiere que viva en una carpeta típica del catálogo.
        new EmulatorDefinition("Play!",
            new[] { "Play.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Play!", "Play" },
            RomArgTemplate: "--disc {rom}", RomExtensions: new[] { ".iso", ".chd", ".cso", ".isz", ".cue", ".elf" }),

        // Ares (multi-sistema, de Near).
        new EmulatorDefinition("Ares",
            new[] { "ares.exe" },
            Array.Empty<(string, string)>(),
            new[] { "ares", "Ares" },
            RomArgTemplate: "{rom}"),

        // Mednafen (multi-sistema, CLI).
        new EmulatorDefinition("Mednafen",
            new[] { "mednafen.exe" },
            Array.Empty<(string, string)>(),
            new[] { "mednafen" },
            RomArgTemplate: "{rom}"),

        // Vita3K (PS Vita): `-r <TitleID>` corre un juego YA instalado; pasarle un
        // .vpk/.pkg lo instala y lo arranca (verificado en el FAQ oficial). Por eso
        // la plantilla es posicional con esas extensiones, no un title id.
        new EmulatorDefinition("Vita3K",
            new[] { "Vita3K.exe" },
            Array.Empty<(string, string)>(),
            new[] { "Vita3K" },
            RomArgTemplate: "{rom}", RomExtensions: new[] { ".vpk", ".pkg" }),

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
            new[] { "Mesen" },
            RomArgTemplate: "{rom}"),

        // bsnes (SNES).
        new EmulatorDefinition("bsnes",
            new[] { "bsnes.exe" },
            Array.Empty<(string, string)>(),
            new[] { "bsnes" }),

        // FBNeo (arcade/FinalBurn Neo): igual que MAME, toma el nombre del set,
        // no una ruta.
        new EmulatorDefinition("FBNeo",
            new[] { "fbneo.exe", "fbneo64.exe" },
            Array.Empty<(string, string)>(),
            new[] { "FBNeo", "FinalBurn Neo" },
            RomArgTemplate: "{set}", RomExtensions: new[] { ".zip", ".7z" }),

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

        // LDPlayer: la UI es dnplayer.exe (player); el juego corre en la VM
        // Ld9BoxHeadless.exe con su servicio Ld9BoxSVC.exe. ldnative.exe se
        // conserva por compatibilidad con instalaciones que lo exponen.
        new EmulatorDefinition("LDPlayer",
            new[] { "dnplayer.exe", "ldnative.exe" },
            new[] { (@"SOFTWARE\DnPlayer", "InstallDir"), (@"SOFTWARE\WOW6432Node\DnPlayer", "InstallDir") },
            new[] { "LDPlayer", "LDPlayer9", "LDPlayer\\LDPlayer9" },
            IsMobile: true,
            ProcessNames: new[] { "Ld9BoxHeadless.exe", "Ld9BoxSVC.exe", "LdVBoxHeadless.exe" }),

        // Nox: instala en "Bignox" o "Nox".
        // Nox: el juego no corre en Nox.exe (la UI) sino en NoxVMHandle.exe, la VM
        // que levanta el propio emulador; sin el alias, una regla guardada para
        // Nox.exe solo disparaba al abrir la ventana del emulador desde su exe.
        new EmulatorDefinition("NoxPlayer",
            new[] { "Nox.exe" },
            new[] { (@"SOFTWARE\BigNox", "InstallPath"), (@"SOFTWARE\WOW6432Node\BigNox", "InstallPath") },
            new[] { "Bignox", "Nox", "Nox\\bin" },
            IsMobile: true,
            ProcessNames: new[] { "NoxVMHandle.exe", "NOXVMHandle.exe", "NoxVMSVC.exe" }),

        // MEmu: registro MemuTop.
        new EmulatorDefinition("MEmu",
            new[] { "MEmu.exe" },
            new[] { (@"SOFTWARE\MemuTop", "InstallPath"), (@"SOFTWARE\WOW6432Node\MemuTop", "InstallPath") },
            new[] { "Microvirt", "Microvirt\\MEmu", "MEmu" },
            IsMobile: true,
            ProcessNames: new[] { "MEmuHeadless.exe", "MEmuSVC.exe" }),

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
            new[] { "NemuPlayer.exe", "MuMuPlayer.exe", "MuMuPlayerGlobal.exe" },
            new[] { (@"SOFTWARE\Netease\MuMuPlayer", "InstallDir"), (@"SOFTWARE\WOW6432Node\Netease\MuMuPlayer", "InstallDir") },
            new[] { "Netease\\MuMuPlayer-12.0", "Netease\\MuMuPlayerGlobal-12.0", "Netease\\MuMuPlayer6", "MuMuPlayer-12.0", "MuMuPlayerGlobal-12.0", "MuMuPlayer6", "Nemu" },
            IsMobile: true,
            ProcessNames: new[] { "NemuHeadless.exe", "MuMuVMMSVC.exe", "MuMuVMMHeadless.exe", "MuMuVMM.exe" }),

        // Tencent GameLoop (ex Tencent Gaming Buddy): el launcher vive en
        // C:\Program Files\Tencent\GameLoop\Application\GameLoopLauncher.exe y el juego
        // corre bajo el motor Android de Tencent (aow_exe.exe / AndroidEmulatorEx.exe),
        // que es exactamente el caso donde el proceso no se llama como el exe.
        new EmulatorDefinition("GameLoop",
            new[] { "AppMarket.exe", "AndroidEmulator.exe", "GameLoopLauncher.exe", "AndroidEmulatorEx.exe" },
            new[] { (@"SOFTWARE\Tencent\GameLoop", "InstallPath"), (@"SOFTWARE\WOW6432Node\Tencent\GameLoop", "InstallPath") },
            new[] { "TxGameAssistant", "TxGameAssistant\\AppMarket", "TxGameAssistant\\ui", "Tencent\\GameLoop\\Application", "Tencent\\GameLoop", "Tencent\\AndroidEmulator", "GameLoop" },
            IsMobile: true,
            ProcessNames: new[] { "aow_exe.exe", "AndroidEmulatorEx.exe", "AndroidEmulatorEn.exe" }),

        // Google Play Games para PC: app oficial de Google en
        // C:\Program Files\Google\Play Games (con subcarpetas versionadas adentro); el
        // juego corre en la VM crosvm que trae el propio paquete.
        new EmulatorDefinition("Google Play Games",
            new[] { "GooglePlayGames.exe" },
            new[] { (@"SOFTWARE\Google\Play Games", "InstallPath") },
            new[] { "Google\\Play Games\\current\\emulator", "Google\\Play Games\\current", "Google\\Play Games" },
            IsMobile: true,
            ProcessNames: new[] { "crosvm.exe", "GooglePlayGamesService.exe" }),

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

    /// <summary>
    /// Argumentos para lanzar una ROM concreta con este emulador, o "" si el
    /// emulador no declara plantilla (sin CLI de ROM documentada: se lanza
    /// vacío, como siempre) o la ruta no tiene una extensión confirmada.
    /// La plantilla usa {rom} como marcador: se reemplaza por la ruta
    /// entrecomillada. Ver <see cref="EmulatorDefinition.RomArgTemplate"/>.
    /// </summary>
    public static string BuildRomLaunchArgs(EmulatorDefinition? def, string? romPath)
    {
        if (def?.RomArgTemplate is not { } template || string.IsNullOrWhiteSpace(romPath))
            return "";
        if (def.RomExtensions is { } exts)
        {
            var ext = System.IO.Path.GetExtension(romPath);
            var known = false;
            foreach (var e in exts)
                if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            if (!known) return "";
        }
        // {set}: MAME/FBNeo reciben el nombre corto del set, sin ruta ni extensión
        // (la carpeta se resuelve con -rompath). {rom}: ruta completa entrecomillada.
        var set = System.IO.Path.GetFileNameWithoutExtension(romPath.Trim().Trim('"'));
        var quoted = "\"" + romPath.Replace("\"", "\\\"") + "\"";
        return template.Replace("{set}", set).Replace("{rom}", quoted);
    }

    /// <summary>Igual que <see cref="BuildRomLaunchArgs"/> pero resolviendo el
    /// emulador desde su ejecutable (ruta completa o solo nombre de archivo).</summary>
    public static string BuildRomLaunchArgsForExe(string? exePathOrName, string? romPath)
        => BuildRomLaunchArgs(MatchExeFileNameForPath(exePathOrName), romPath);

    /// <summary>¿El exe corresponde a un emulador móvil? Atajo para clasificar.</summary>
    public static bool IsMobile(string? exeFileName) => MatchExeFileName(exeFileName)?.IsMobile ?? false;

}
