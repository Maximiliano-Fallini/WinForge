using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Administración de inicio estilo Administrador de Tareas de Windows.
///
/// Fuentes (las mismas que usa Task Manager):
///  1. Registry Run keys (HKLM y HKCU, incluidas las subclaves de 32-bit en WOW6432Node).
///  2. Startup folders (usuario y común).
///  3. Apps empaquetadas (MSIX/UWP) con tarea de arranque declarada en su manifest
///     (startupTask) — el estado se guarda en AppModel\SystemAppData.
///
/// El estado enabled/disabled se lee de las claves StartupApproved del Explorador
/// (las mismas que mira Task Manager), no de un prefijo propio.
///
/// Toggle: escribe el estado en StartupApproved\Run (o StartupFolder) como hace
/// el shell de Windows — así Task Manager y esta app ven siempre lo mismo.
/// </summary>
public sealed class StartupManagerService : IStartupManagerService
{
    private const string RegRunHKLM   = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegRunHKLM32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string RegRunHKCU   = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // StartupApproved: clave que usa el Explorador/Task Manager para trackear
    // el estado enabled/disabled de cada entrada.
    private const string ApprovedRunHKLM = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRunHKLM32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedRunHKCU = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedFolderUser = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    // Estado de las tareas de arranque de apps empaquetadas (MSIX/UWP): la
    // clave que lee/escribe Task Manager (State: 3=habilitado, 1=deshabilitado).
    private const string AppStateKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private static readonly string StartupUser   = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    private static readonly string StartupCommon = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    private readonly ILoggingService _logging;

    public StartupManagerService(ILoggingService logging)
    {
        _logging = logging;
    }

    // =====================================================================
    // IStartupManagerService
    // =====================================================================

    public IReadOnlyList<StartupEntry> GetEntries()
    {
        // El estado que muestra el Administrador de tareas se guarda en la clave
        // StartupApproved del MISMO hive que la entrada Run: HKCU\Run → el
        // StartupApproved\Run de HKCU; HKLM\Run → el de HKLM; HKLM\Run32
        // (WOW6432Node) → el StartupApproved\Run32 de HKLM. Cada entrada tiene
        // su clave apropiada según el hive — no todo en HKCU.
        var approvedRunHklm   = ReadApproved(Registry.LocalMachine,  ApprovedRunHKLM);
        var approvedRunHklm32 = ReadApproved(Registry.LocalMachine,  ApprovedRunHKLM32);
        var approvedRunHkcu   = ReadApproved(Registry.CurrentUser,   ApprovedRunHKCU);
        var approvedFolder    = ReadApproved(Registry.CurrentUser,   ApprovedFolderUser);

        var list = new List<StartupEntry>();

        // ── Registry Run keys ──
        ReadRegRun(list, Registry.LocalMachine, RegRunHKLM,   "HKLM\\Run",   true,  approvedRunHklm);
        ReadRegRun(list, Registry.LocalMachine, RegRunHKLM32, "HKLM\\Run32", true,  approvedRunHklm32);
        ReadRegRun(list, Registry.CurrentUser,  RegRunHKCU,   "HKCU\\Run",   false, approvedRunHkcu);

        // ── Startup folders ──
        ReadStartupFolder(list, StartupUser,   "folder|user",   false, approvedFolder);
        ReadStartupFolder(list, StartupCommon, "folder|common", true,  approvedFolder);

        // ── Apps empaquetadas (MSIX/UWP) ──
        ReadStartupPackages(list);

        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Toggle(string id, bool enable)
    {
        try
        {
            if (id.StartsWith("reg|", StringComparison.Ordinal))
                return ToggleReg(id, enable);
            if (id.StartsWith("folder|", StringComparison.Ordinal))
                return ToggleFolder(id, enable);
            if (id.StartsWith("appx|", StringComparison.Ordinal))
                return TogglePackaged(id, enable);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"StartupManager: toggle {id}: {ex.Message}");
        }
        return false;
    }

    public bool Delete(string id)
    {
        try
        {
            if (id.StartsWith("reg|", StringComparison.Ordinal))
                return DeleteReg(id);
            if (id.StartsWith("folder|", StringComparison.Ordinal))
                return DeleteFolder(id);
            // Las apps empaquetadas no se "borran": deshabilitar la tarea de arranque
            // es lo único que permite Windows (lo mismo que hace Task Manager).
            if (id.StartsWith("appx|", StringComparison.Ordinal))
                return TogglePackaged(id, enable: false);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"StartupManager: delete {id}: {ex.Message}");
        }
        return false;
    }

    // =====================================================================
    // StartupApproved — estado enabled/disabled oficial de Windows
    // =====================================================================

        /// <summary>
    /// Lee la clave StartupApproved\Run (o StartupFolder) de un hive.
    /// Devuelve un diccionario: nombre_del_valor → true si está enabled.
    ///
    /// Formato oficial de Windows (REG_BINARY de 12 bytes): el BIT BAJO del
    /// primer byte decide — PAR (02/06) = enabled · IMPAR (01/03/07) = disabled.
    /// Los bytes restantes son un timestamp de cuándo se cambió.
    /// </summary>
    private static Dictionary<string, bool> ReadApproved(RegistryKey hive, string subkey)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = hive.OpenSubKey(subkey);
            if (key == null) return result;
            foreach (var name in key.GetValueNames())
            {
                var raw = key.GetValue(name) as byte[];
                if (raw == null || raw.Length < 1) continue;
                bool enabled = (raw[0] & 1) == 0;
                result[name] = enabled;
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Escribe o borra la entrada en StartupApproved.
    /// enable=true  → borra la clave (así Windows la trata como enabled por defecto).
    /// enable=false → escribe el blob [03 00 00 00 ...]: primer byte IMPAR = disabled,
    ///                que es lo que ve Task Manager al mostrar "Deshabilitado".
    /// </summary>
    private static void WriteApproved(RegistryKey hive, string subkey, string valueName, bool enable)
    {
        try
        {
            using var key = hive.OpenSubKey(subkey, writable: true);
            if (key == null)
            {
                // La clave no existe: crearla.
                using var created = hive.CreateSubKey(subkey);
                if (created == null) return;
                if (enable) return; // no hace falta escribir nada
                created.SetValue(valueName, DisabledBlob(), RegistryValueKind.Binary);
                return;
            }

            if (enable)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(valueName, DisabledBlob(), RegistryValueKind.Binary);
            }
        }
        catch { }
    }

    /// <summary>Blob "deshabilitado" estándar: primer byte impar (03).</summary>
    private static byte[] DisabledBlob() => new byte[]
        { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    // =====================================================================
    // Registry Run keys
    // =====================================================================

    private static void ReadRegRun(List<StartupEntry> list,
        RegistryKey hive, string subkey, string sourceTag, bool isSystem,
        Dictionary<string, bool> approved)
    {
        try
        {
            using var key = hive.OpenSubKey(subkey);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                var kind = key.GetValueKind(name);
                if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
                    continue;
                var command = key.GetValue(name)?.ToString();
                if (string.IsNullOrWhiteSpace(command)) continue;

                // Estado oficial desde StartupApproved de ESTE hive; sin entrada → habilitada.
                bool enabled = approved.TryGetValue(name, out bool a) ? a : true;

                // Nombre visible: el del ARCHIVO ejecutable, igual que Task Manager
                // ("Update.exe", "steam.exe", ...) — no el nombre del valor del registro
                // ("Discord", "electron.app.BlueStacks Services", ...).
                var exePath = ExtractExePathFromCommand(command);
                var displayName = !string.IsNullOrEmpty(exePath) ? Path.GetFileName(exePath) : name;

                list.Add(new StartupEntry(
                    $"reg|{hive.Name}|{subkey}|{name}",
                    displayName,
                    command,
                    StartupSource.Registry,
                    isSystem,
                    enabled));
            }
        }
        catch { }
    }

    /// <summary>
    /// Escribe el estado en la clave StartupApproved que corresponde al hive de la
    /// entrada, y borra cualquier residuo del MISMO nombre de las otras dos claves
    /// (para no dejar estados duplicados/contradictorios entre hives).
    /// </summary>
    private static void WriteApprovedCleaned(
        RegistryKey hive, string approvedSubkey, string valueName, bool enable)
    {
        var all = new[]
        {
            (Registry.CurrentUser,  ApprovedRunHKCU),
            (Registry.LocalMachine, ApprovedRunHKLM),
            (Registry.LocalMachine, ApprovedRunHKLM32)
        };
        foreach (var (h, s) in all)
        {
            bool isOwn = ReferenceEquals(h, hive) && s == approvedSubkey;
            WriteApproved(h, s, valueName, isOwn ? enable : true /* borra el resto */);
        }
    }

    private bool ToggleReg(string id, bool enable)
    {
        // id = "reg|HKEY_CURRENT_USER|Software\...\Run|valueName"
        var parts = id.Split('|');
        if (parts.Length < 4) return false;
        var hiveName = parts[1];
        var subkey = parts[2];
        var valueName = parts[3];

        // Escribir en StartupApproved del MISMO hive que la entrada (como Task
        // Manager) y limpiar las otras claves.
        var (hive, approvedSubkey) = hiveName.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
            ? (Registry.CurrentUser, ApprovedRunHKCU)
            : (Registry.LocalMachine, subkey.Contains("WOW6432Node") ? ApprovedRunHKLM32 : ApprovedRunHKLM);

        WriteApprovedCleaned(hive, approvedSubkey, valueName, enable);
        return true;
    }

    private static bool DeleteReg(string id)
    {
        // id = "reg|HKEY_CURRENT_USER|Software\...\Run|valueName"
        var parts = id.Split('|');
        if (parts.Length < 4) return false;
        var hiveName = parts[1];
        var subkey = parts[2];
        var valueName = parts[3];

        var hive = hiveName.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
            ? Registry.CurrentUser
            : Registry.LocalMachine;

        using var key = hive.OpenSubKey(subkey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);

        // Borrar el estado de StartupApproved de TODAS las claves (la de entrada y
        // cualquier residuo en las demás). Con enable:true WriteApproved solo
        // borra, así que la subclave pasada como "propia" no importa aquí.
        WriteApprovedCleaned(hive, ApprovedRunHKCU, valueName, enable: true);

        return true;
    }

    // =====================================================================
    // Startup folders
    // =====================================================================

    private static void ReadStartupFolder(List<StartupEntry> list, string dir, string sourceTag, bool isSystem,
        Dictionary<string, bool> approved)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                // Solo accesos directos y ejecutables.
                if (ext is not ".lnk" and not ".exe" and not ".url") continue;

                var fileName = Path.GetFileName(file);
                var name = Path.GetFileNameWithoutExtension(file);

                // Estado aprobado: Task Manager lo guarda en StartupApproved\StartupFolder
                // con el nombre del archivo (con o sin extensión según versión).
                // Sin entrada → enabled por defecto.
                bool enabled = approved.TryGetValue(fileName, out var a) ? a
                    : approved.TryGetValue(name, out a) ? a
                    : true;

                list.Add(new StartupEntry(
                    $"{sourceTag}|{file}",
                    name,
                    file,
                    StartupSource.StartupFolder,
                    isSystem,
                    enabled));
            }
        }
        catch { }
    }

    private static bool ToggleFolder(string id, bool enable)
    {
        // id = "folder|user|C:\...\app.lnk"
        var parts = id.Split('|');
        if (parts.Length < 3) return false;
        var path = parts[2];

        // Mismo mecanismo oficial que Task Manager: StartupApproved\StartupFolder.
        // (Antes se renombraba a .disabled, un hack no estándar que además hacía
        // desaparecer la entrada de la lista al refrescar.)
        WriteApproved(Registry.CurrentUser, ApprovedFolderUser, Path.GetFileName(path), enable);
        return true;
    }

    private static bool DeleteFolder(string id)
    {
        var parts = id.Split('|');
        if (parts.Length < 3) return false;
        var path = parts[2];
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch { return false; }
    }

    // =====================================================================
    // Apps empaquetadas (MSIX/UWP) — tarea de arranque del manifest
    // =====================================================================

    /// <summary>
    /// Enumerar las apps empaquetadas (MSIX/UWP) con tarea de arranque declarada
    /// (startupTask en su AppxManifest.xml) — las mismas que muestra el
    /// Administrador de tareas (ej: apps de la Store como "Terminal" o
    /// "HyperX NGENUITY").
    ///
    /// Cada paquete vive en %ProgramFiles%\WindowsApps\&lt;PFN&gt;_&lt;ver&gt;_&lt;arch&gt;_&lt;hash&gt;\
    /// (accesible para la app, que corre elevada). El estado enabled/disabled se
    /// guarda POR USUARIO en:
    ///   HKCU\...\AppModel\SystemAppData\&lt;PFN&gt;\&lt;TaskId&gt;\State
    /// con los valores del enum oficial StartupTaskState de Windows:
    ///   0=Disabled · 1=DisabledByUser · 2=DisabledByPolicy · 3=Enabled · 4=DisabledByManager
    /// Task Manager muestra "Habilitado" solo con State=3 (o si la clave no
    /// existe, vale el atributo Enabled del manifest).
    /// </summary>
    private void ReadStartupPackages(List<StartupEntry> list)
    {
        try
        {
            var appsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (!Directory.Exists(appsDir)) return;

            foreach (var pkgDir in Directory.EnumerateDirectories(appsDir))
            {
                var folder = Path.GetFileName(pkgDir);
                var manifestPath = Path.Combine(pkgDir, "AppxManifest.xml");
                if (!File.Exists(manifestPath)) continue;
                string xml;
                try { xml = File.ReadAllText(manifestPath); }
                catch { continue; }

                if (!xml.Contains("startupTask", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Identity Name (ej. "33C30B79.NGENUITY", "Microsoft.WindowsTerminal").
                var mIdent = Regex.Match(xml, @"<Identity\b[^>]*\bName=""([^""]+)""", RegexOptions.IgnoreCase);
                if (!mIdent.Success) continue;
                var identityName = mIdent.Groups[1].Value;

                // La extensión startupTask con su contenido (cualquier namespace:
                // uap5:, desktop:, ... — con o sin prefijo).
                var mExt = Regex.Match(xml,
                    @"<(?:\w+:)?Extension\b[^>]*?\bCategory=""windows\.startupTask""[^>]*>(?<body>.*?)</(?:\w+:)?Extension>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!mExt.Success) continue;
                var body = mExt.Groups["body"].Value;

                var mTaskId = Regex.Match(body, @"TaskId=""([^""]+)""", RegexOptions.IgnoreCase);
                if (!mTaskId.Success) continue;
                var taskId = mTaskId.Groups[1].Value;

                var mEnabled = Regex.Match(body, @"Enabled=""([^""]+)""", RegexOptions.IgnoreCase);
                bool manifestEnabled = !mEnabled.Success ||
                    mEnabled.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

                var mDisplay = Regex.Match(body, @"DisplayName=""([^""]+)""", RegexOptions.IgnoreCase);
                var displayAttr = mDisplay.Success ? mDisplay.Groups[1].Value : null;

                // PFN = Identity Name + hash del publicador (último segmento de la carpeta).
                var hash = folder.Split('_').LastOrDefault() ?? "";
                var pfn = identityName + "_" + hash;

                // Estado per-usuario: la clave exacta que lee/escribe Task Manager.
                bool enabled = manifestEnabled;
                var stateSubKey = AppStateKey + @"\" + pfn + @"\" + taskId;
                using (var k = Registry.CurrentUser.OpenSubKey(stateSubKey))
                {
                    if (k?.GetValue("State") is int st) enabled = st == 3;
                }

                // Nombre visible: DisplayName literal ("HyperX NGENUITY") o
                // ms-resource:... resuelto a través del caché MrtCache ("Terminal").
                string name = string.IsNullOrWhiteSpace(displayAttr) ? identityName : displayAttr;
                if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    name = ResolveMsResource(pfn, identityName, name) ?? identityName;

                list.Add(new StartupEntry(
                    $"appx|{pfn}|{taskId}",
                    name,
                    pkgDir, // carpeta del paquete: la UI saca de ahí el ícono (Assets)
                    StartupSource.PackagedApp,
                    IsSystem: true,
                    enabled));
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"StartupManager: leer apps empaquetadas: {ex.Message}");
        }
    }

    private static bool TogglePackaged(string id, bool enable)
    {
        // id = "appx|<PFN>|<TaskId>"
        var parts = id.Split('|');
        if (parts.Length < 3) return false;

        var stateSubKey = AppStateKey + "\\" + parts[1] + "\\" + parts[2];
        using var key = Registry.CurrentUser.CreateSubKey(stateSubKey);
        if (key == null) return false;

        // El mismo contrato que StartupTask de Windows: Enabled=3, DisabledByUser=1.
        key.SetValue("State", enable ? 3 : 1, RegistryValueKind.DWord);
        if (enable)
            key.DeleteValue("LastDisabledTime", throwOnMissingValue: false);
        else
            key.SetValue("LastDisabledTime",
                (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RegistryValueKind.DWord);
        return true;
    }

    /// <summary>
    /// Resuelve un nombre del tipo "ms-resource:AppName" al valor localizado que
    /// Windows ya dejó cacheado en HKCU\...\MrtCache: busca el valor cuyo nombre
    /// contiene "ms-resource://PFN/..." y coincide con el identificador del recurso.
    /// </summary>
    private static string? ResolveMsResource(string pfn, string identityName, string resourceRef)
    {
        try
        {
            var id = resourceRef.Substring("ms-resource:".Length).TrimStart('/');
            var mrtCache = @"Software\Classes\Local Settings\MrtCache";
            using var root = Registry.CurrentUser.OpenSubKey(mrtCache);
            if (root == null) return null;

            // El URI del recurso usa el NOMBRE del paquete (ej: Microsoft.WindowsTerminal),
            // no el PFN completo (con el hash del publicador al final).
            foreach (var pkgRef in new[] { pfn, identityName })
            {
                var found = FindMsResource(root, pkgRef, id);
                if (found != null) return found;
            }
        }
        catch { }
        return null;
    }

    private static string? FindMsResource(RegistryKey root, string pkgRef, string id)
    {
        // Los valores viven a 1..2 niveles bajo la clave del paquete
        // (ej: ...\resources.pri\<hash1>\<hash2> con los valores en <hash2>).
        foreach (var cacheKey in root.GetSubKeyNames())
        {
            if (!cacheKey.Contains("%5Cresources.pri", StringComparison.OrdinalIgnoreCase))
                continue;
            using var ck = root.OpenSubKey(cacheKey);
            if (ck == null) continue;

            foreach (var l1 in ck.GetSubKeyNames())
            {
                using var k1 = ck.OpenSubKey(l1);
                if (k1 == null) continue;
                foreach (var l2 in k1.GetSubKeyNames())
                {
                    using var k2 = k1.OpenSubKey(l2);
                    if (k2 == null) continue;
                    var hit = FindMsResourceValue(k2, pkgRef, id);
                    if (hit != null) return hit;
                }
                var hit1 = FindMsResourceValue(k1, pkgRef, id);
                if (hit1 != null) return hit1;
            }
        }
        return null;
    }

    private static string? FindMsResourceValue(RegistryKey key, string pfn, string id)
    {
        // Los nombres de valor tienen la forma "@{<PackageFullName>?ms-resource://<PFN>/.../Id}"
        // (terminan con '}' porque el URI va entre llaves).
        var suffix = "/" + id;
        foreach (var vn in key.GetValueNames())
        {
            if (!vn.Contains($"?ms-resource://{pfn}/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!vn.EndsWith(suffix + "}", StringComparison.OrdinalIgnoreCase) &&
                !vn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            var v = key.GetValue(vn)?.ToString();
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    /// <summary>
    /// Extrae la ruta del ejecutable de una línea de comandos (con o sin comillas,
    /// y con o sin espacios en la ruta) — para mostrar el nombre del archivo igual
    /// que Task Manager.
    /// </summary>
    private static string? ExtractExePathFromCommand(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var trimmed = commandLine.Trim();

        // Entre comillas: hasta la de cierre.
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = trimmed.IndexOf("\"", 1, StringComparison.Ordinal);
            return end > 0 ? trimmed[1..end] : trimmed.Trim('"');
        }

        trimmed = Environment.ExpandEnvironmentVariables(trimmed);

        // Sin comillas: probar prefijos cada vez más largos y quedarse con el
        // más largo que exista como archivo (ej: rutas con espacios sin comillas).
        string? best = null;
        int idx = 0;
        while (idx < trimmed.Length)
        {
            int nextSpace = trimmed.IndexOf(' ', idx);
            if (nextSpace < 0) break;
            var candidate = trimmed[..nextSpace];
            try { if (File.Exists(candidate)) best = candidate; } catch { }
            idx = nextSpace + 1;
        }
        if (best != null) return best;

        // La ruta completa también puede ser el archivo (cuando no quedan espacios
        // y el último candidato era una carpeta, ej: "C:\Program Files\...\Lightshot.exe").
        try { if (File.Exists(trimmed)) return trimmed; } catch { }

        // Nada existe: el primer token (mejor esfuerzo).
        int sp = trimmed.IndexOf(' ');
        return sp < 0 ? trimmed : trimmed[..sp];
    }

    // =====================================================================
    // Helpers
    // =====================================================================
}