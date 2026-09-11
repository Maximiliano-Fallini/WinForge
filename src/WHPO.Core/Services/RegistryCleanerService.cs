using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Motor del limpiador de registro (estilo CCleaner) para la "Limpieza
/// personalizada". Todo es HKCU: no requiere admin y solo puede afectar al
/// usuario actual, nunca al sistema.
///
/// Reglas de seguridad:
/// - NUNCA se borra nada sin un backup .reg previo (AppPaths.RootDir
///   \registry-backups, con fecha en el nombre). Sin backup NO se limpia.
/// - Solo se borra lo que matchea patrones conocidos y acotados.
/// - Las extensiones huérfanas se borran ENTERAS vía reg.exe (con backup
///   jerárquico con reg.exe export); lo demás son valores individuales dentro
///   de claves que se conservan.
/// </summary>
public sealed class RegistryCleanerService : IRegistryCleanerService
{
    /// <summary>Carpeta de backups .reg (relativa a la raíz de datos de la app).</summary>
    public static string BackupDir => Path.Combine(AppPaths.RootDir, "registry-backups");

    public string BackupDirectory => BackupDir;

    // Raíces HKCU que escanea el limpiador (todas de usuario).
    private const string ClassesRoot = @"Software\Classes";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string MuiCacheKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";

    // Extensiones que NUNCA se consideran huérfanas aunque su ProgId falte
    // (Windows las usa internamente o las reconstruye al vuelo).
    private static readonly string[] ProtectedExtensions =
    [
        ".exe", ".dll", ".sys", ".ocx", ".cpl", ".msi", ".msp", ".mst",
        ".lnk", ".url", ".txt", ".ini", ".inf", ".bat", ".cmd", ".ps1",
        ".reg", ".xml", ".json", ".yml", ".yaml", ".vbs", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".iso", ".img", ".vhd", ".vhdx", ".cab",
        ".zip", ".7z", ".rar", ".gz", ".tar", ".psm1", ".psd1", ".cat",
        ".cer", ".pfx", ".p7b", ".der", ".wim", ".esd", ".mui", ".scr"
    ];

    // ProgIds que Windows registra por su cuenta: si una extensión los usa,
    // la clase no es basura aunque el "programa" no esté en Program Files.
    private static readonly string[] BuiltInProgIds =
    [
        "Applications", "SystemFileAssociations", "shbfile", "CLSID",
        "exefile", "dllfile", "txtfile", "inifile", "inffile", "batfile",
        "cmdfile", "regfile", "xmlfile", "javascript", "JSEFile", "VBSFile",
        "WSFFile", "WSHFile", "htafile", "zipfile", "comfile", "msifile",
        "librarylocation", "Folder", "Directory", "AllFilesystemObjects",
        "ShellObject", "Directory.Background", "Directory.ContextMenuEdit"
    ];

    private readonly ILoggingService _logging;

    public RegistryCleanerService(ILoggingService loggingService)
    {
        _logging = loggingService;
    }

    // =====================================================================
    // ICleanupService-compatible API
    // =====================================================================

    /// <summary>Escanea la categoría pedida; los errores por clave se loguean y se omiten.</summary>
    public IReadOnlyList<RegistryCleanupItem> Scan(RegistryCleanCategory category)
    {
        try
        {
            return category switch
            {
                RegistryCleanCategory.OrphanedFileExtensions => ScanOrphanedExtensions(),
                RegistryCleanCategory.UninstallLeftovers => ScanUninstallLeftovers(),
                RegistryCleanCategory.StaleMuiCache => ScanStaleMuiCache(),
                _ => []
            };
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"RegistryCleaner: error al escanear {category}: {ex.Message}");
            return [];
        }
    }

    // =====================================================================
    // Escáneres
    // =====================================================================

    /// <summary>
    /// Clases de extensión (HKCU\Software\Classes\.xyz) cuyo ProgId asociado ya
    /// no existe: el clásico "extensiones huérfanas" de CCleaner.
    /// </summary>
    private List<RegistryCleanupItem> ScanOrphanedExtensions()
    {
        var items = new List<RegistryCleanupItem>();
        using var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot, writable: false);
        if (classes == null) return items;

        foreach (var ext in classes.GetSubKeyNames())
        {
            if (!ext.StartsWith(".", StringComparison.Ordinal)) continue;
            if (ProtectedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

            try
            {
                using var extKey = classes.OpenSubKey(ext);
                if (extKey == null) continue;

                var progId = extKey.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(progId)) continue;                 // sin ProgId: no se toca
                if (BuiltInProgIds.Contains(progId, StringComparer.OrdinalIgnoreCase)) continue;

                // ¿Existe el ProgId al que apunta? Si existe, la extensión está viva.
                var progIdPath = Path.Combine(ClassesRoot, progId);
                using var progIdKey = Registry.CurrentUser.OpenSubKey(progIdPath);
                if (progIdKey != null) continue;

                // Huérfana confirmada: ProgId declarado que ya no está.
                int count = extKey.GetValueNames().Length + extKey.GetSubKeyNames().Length;
                items.Add(new RegistryCleanupItem(
                    Id: $"ext|{ext.ToLowerInvariant()}",
                    Category: RegistryCleanCategory.OrphanedFileExtensions,
                    KeyPath: Path.Combine(ClassesRoot, ext),
                    ValueName: null,
                    Detail: $"Apunta a '{progId}' que ya no existe",
                    ReferenceTarget: progId,
                    ValueCount: count));
            }
            catch (Exception ex)
            {
                _logging.LogDebug($"RegistryCleaner: no se pudo escanear la extensión {ext}: {ex.Message}");
            }
        }
        return items;
    }

    /// <summary>
    /// Restos de programas desinstalados (HKCU Uninstall): la entrada existe
    /// pero su UninstallString apunta a un ejecutable que ya no está.
    /// </summary>
    private List<RegistryCleanupItem> ScanUninstallLeftovers()
    {
        var items = new List<RegistryCleanupItem>();
        using var root = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: false);
        if (root == null) return items;

        foreach (var sub in root.GetSubKeyNames())
        {
            try
            {
                using var subKey = root.OpenSubKey(sub);
                if (subKey == null) continue;

                var displayName = subKey.GetValue("DisplayName") as string;
                var uninstallString = subKey.GetValue("UninstallString") as string;

                // Sin DisplayName no representa un programa visible: basura típica de GUIDs.
                if (string.IsNullOrWhiteSpace(displayName)) continue;

                if (string.IsNullOrWhiteSpace(uninstallString))
                {
                    // Sin UninstallString no se puede desinstalar: resto clásico.
                    items.Add(new RegistryCleanupItem(
                        Id: $"unins|{sub}",
                        Category: RegistryCleanCategory.UninstallLeftovers,
                        KeyPath: Path.Combine(UninstallKey, sub),
                        ValueName: null,
                        Detail: $"'{displayName}' no tiene desinstalador",
                        ReferenceTarget: null,
                        ValueCount: subKey.GetValueNames().Length + subKey.GetSubKeyNames().Length));
                    continue;
                }

                if (!UninstallTargetExists(uninstallString))
                {
                    items.Add(new RegistryCleanupItem(
                        Id: $"unins|{sub}",
                        Category: RegistryCleanCategory.UninstallLeftovers,
                        KeyPath: Path.Combine(UninstallKey, sub),
                        ValueName: null,
                        Detail: $"'{displayName}' ya no está instalado",
                        ReferenceTarget: uninstallString,
                        ValueCount: subKey.GetValueNames().Length + subKey.GetSubKeyNames().Length));
                }
            }
            catch (Exception ex)
            {
                _logging.LogDebug($"RegistryCleaner: no se pudo escanear {sub}: {ex.Message}");
            }
        }
        return items;
    }

    /// <summary>
    /// Entradas MuiCache (nombres amigables de apps ejecutadas) cuyo ejecutable
    /// ya no existe. Borrarlas solo limpia nombres viejos; la clave se conserva.
    /// </summary>
    private List<RegistryCleanupItem> ScanStaleMuiCache()
    {
        var items = new List<RegistryCleanupItem>();
        using var muiKey = Registry.CurrentUser.OpenSubKey(MuiCacheKey, writable: false);
        if (muiKey == null) return items;

        foreach (var valueName in muiKey.GetValueNames())
        {
            try
            {
                // El nombre del valor es la ruta del exe (o "F_<ruta>.FriendlyAppName").
                var exePath = valueName;
                if (exePath.StartsWith("F_", StringComparison.Ordinal)) exePath = exePath[2..];
                if (exePath.EndsWith(".FriendlyAppName", StringComparison.OrdinalIgnoreCase))
                    exePath = exePath[..^".FriendlyAppName".Length];

                if (string.IsNullOrWhiteSpace(exePath)) continue;

                var expanded = Environment.ExpandEnvironmentVariables(exePath);
                if (!IsAbsoluteWindowsPath(expanded)) continue; // rutas MUI especiales: no se tocan

                if (!File.Exists(expanded))
                {
                    items.Add(new RegistryCleanupItem(
                        Id: $"mui|{valueName}",
                        Category: RegistryCleanCategory.StaleMuiCache,
                        KeyPath: MuiCacheKey,
                        ValueName: valueName,
                        Detail: $"Ejecutable inexistente: {exePath}",
                        ReferenceTarget: exePath,
                        ValueCount: 1));
                }
            }
            catch (Exception ex)
            {
                _logging.LogDebug($"RegistryCleaner: no se pudo escanear MuiCache '{valueName}': {ex.Message}");
            }
        }
        return items;
    }

    // =====================================================================
    // Backup
    // =====================================================================

    /// <summary>
    /// Exporta un backup .reg de lo que se va a borrar. Los valores sueltos van
    /// como "nombre=valor"; las claves enteras (extensiones y entradas de
    /// desinstalación) se respaldan con reg.exe export (jerarquía completa).
    /// Devuelve null si el backup falló: el limpiador no debe limpiar en ese caso.
    /// </summary>
    public string? ExportBackup(IReadOnlyList<RegistryCleanupItem> items, string? suffix = null)
    {
        if (items.Count == 0) return null;
        try
        {
            Directory.CreateDirectory(BackupDir);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var file = Path.Combine(BackupDir, $"registro_{stamp}{(string.IsNullOrEmpty(suffix) ? "" : "_" + suffix)}.reg");

            var sb = new StringBuilder();
            sb.AppendLine("Windows Registry Editor Version 5.00");
            sb.AppendLine();

            // 1) Valores individuales (MuiCache): nombre=valor por clave.
            var valueGroups = items.Where(i => i.ValueName != null)
                .GroupBy(i => i.KeyPath, StringComparer.OrdinalIgnoreCase);
            foreach (var g in valueGroups)
            {
                using var k = Registry.CurrentUser.OpenSubKey(g.Key);
                if (k == null) continue;

                sb.AppendLine($"[{ToRegFormat(g.Key)}]");
                foreach (var item in g)
                {
                    var v = k.GetValue(item.ValueName!);
                    if (v == null) continue;
                    var kind = k.GetValueKind(item.ValueName!);
                    sb.AppendLine($"\"{EscapeRegValueName(item.ValueName!)}\"={FormatRegValue(v, kind)}");
                }
                sb.AppendLine();
            }

            // 2) Claves enteras: además del "encabezado" en el .reg (sirve para
            // borrar las claves recreadas al restaurar), se intenta reg.exe export
            // para guardar la jerarquía completa en un .reg aparte.
            var wholeKeyItems = items.Where(i => i.ValueName == null).ToList();
            foreach (var group in wholeKeyItems.GroupBy(i => i.KeyPath, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"[{ToRegFormat(group.Key)}]");
            }
            if (wholeKeyItems.Count > 0)
                sb.AppendLine();

            if (wholeKeyItems.Count > 0)
            {
                var sidecar = TryRegExport(wholeKeyItems.Select(i => i.KeyPath).ToList(), stamp);
                if (sidecar == null)
                {
                    _logging.LogWarning("RegistryCleaner: reg.exe no pudo exportar las claves — NO se limpia (falla segura).");
                    return null;
                }
                if (sidecar != file)
                    _logging.LogInfo($"RegistryCleaner: backup jerárquico guardado en {sidecar}.");
            }

            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _logging.LogInfo($"RegistryCleaner: backup de registro exportado a {file} ({items.Count} elementos).");
            return file;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"RegistryCleaner: no se pudo crear el backup: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Exporta la jerarquía completa de las claves indicadas con reg.exe export.
    /// Devuelve la ruta del .reg generado, o null si falló. Si es un solo árbol,
    /// genera un .reg dedicado; si son varias, genera uno por clave (concatenados
    /// en un solo archivo para que restaurar sea importar 1 archivo).
    /// </summary>
    private string? TryRegExport(List<string> keyPaths, string stamp)
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            var outFile = Path.Combine(BackupDir, $"registro_claves_{stamp}.reg");
            var tmpFiles = new List<string>();

            foreach (var kp in keyPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tmp = Path.Combine(BackupDir, $"_tmp_{Guid.NewGuid():N}.reg");
                // reg.exe está en System32; UseShellExecute=false con ruta completa
                // evita sorpresas de PATH. HKCU\<ruta> es el formato que reg export espera.
                var psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "reg.exe"),
                    $"export \"HKCU\\{kp}\" \"{tmp}\" /y")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) { CleanupTmp(tmpFiles); return null; }
                p.WaitForExit(10_000);
                if (p.ExitCode != 0 || !File.Exists(tmp))
                {
                    CleanupTmp(tmpFiles);
                    return null;
                }
                tmpFiles.Add(tmp);
            }

            // Concatenar todos los .reg en uno solo (el header una vez).
            using (var outStream = new StreamWriter(outFile, append: false, new UTF8Encoding(false)))
            {
                bool headerWritten = false;
                foreach (var tmp in tmpFiles)
                {
                    foreach (var line in File.ReadLines(tmp))
                    {
                        if (!headerWritten)
                        {
                            outStream.WriteLine(line); // el header del primero
                            headerWritten = true;
                            continue;
                        }
                        if (line.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase))
                            continue; // headers sucesivos: omitir
                        outStream.WriteLine(line);
                    }
                }
            }
            CleanupTmp(tmpFiles);

            _logging.LogInfo($"RegistryCleaner: reg.exe exportó {keyPaths.Count} claves a {outFile}.");
            return outFile;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"RegistryCleaner: reg.exe export falló: {ex.Message}");
            return null;
        }
    }

    private static void CleanupTmp(List<string> files)
    {
        foreach (var f in files)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    // =====================================================================
    // Limpieza
    // =====================================================================

    /// <summary>Borra lo indicado; exporta el backup antes y aborta si falla.</summary>
    public RegistryCleanResult Clean(IReadOnlyList<RegistryCleanupItem> items)
    {
        if (items.Count == 0)
            return new RegistryCleanResult(0, 0, null, []);

        var warnings = new List<string>();

        // Falla segura: si el backup no se pudo crear, no se borra nada.
        var backup = ExportBackup(items);
        if (backup == null)
        {
            warnings.Add("No se pudo crear el backup del registro: no se borró nada.");
            return new RegistryCleanResult(0, 0, null, warnings);
        }

        int deletedKeys = 0, deletedValues = 0;

        // 1) Valores individuales (MuiCache).
        foreach (var g in items.Where(i => i.ValueName != null).GroupBy(i => i.KeyPath, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(g.Key, writable: true);
                if (key == null)
                {
                    warnings.Add($"No se pudo abrir {g.Key} (¿permisos?).");
                    continue;
                }
                foreach (var item in g)
                {
                    try
                    {
                        key.DeleteValue(item.ValueName!, throwOnMissingValue: false);
                        deletedValues++;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"No se pudo borrar el valor '{item.ValueName}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"No se pudo abrir la clave {g.Key}: {ex.Message}");
            }
        }

        // 2) Claves enteras (extensiones huérfanas, entradas de desinstalación):
        //    reg.exe delete, que soporta árboles completos.
        var regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
        foreach (var item in items.Where(i => i.ValueName == null))
        {
            try
            {
                var psi = new ProcessStartInfo(regExe, $"delete \"HKCU\\{item.KeyPath}\" /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    warnings.Add($"No se pudo lanzar reg.exe para {item.KeyPath}.");
                    continue;
                }
                p.WaitForExit(10_000);
                if (p.ExitCode == 0) deletedKeys++;
                else warnings.Add($"reg.exe no pudo borrar HKCU\\{item.KeyPath} (exit {p.ExitCode}).");
            }
            catch (Exception ex)
            {
                warnings.Add($"Error al borrar HKCU\\{item.KeyPath}: {ex.Message}");
            }
        }

        _logging.LogInfo($"RegistryCleaner: limpieza completa — {deletedKeys} claves y {deletedValues} valores borrados (backup: {backup}).");
        return new RegistryCleanResult(deletedKeys, deletedValues, backup, warnings);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>¿Parece una ruta absoluta de Windows (C:\..., %VAR%\..., \\server\share)?</summary>
    private static bool IsAbsoluteWindowsPath(string path)
        => path.Length >= 3 && (char.IsLetter(path[0]) && path[1] == ':' ||
           path.StartsWith(@"\\", StringComparison.Ordinal) ||
           path.StartsWith('%'));

    /// <summary>
    /// ¿El objetivo del UninstallString existe? Extrae el exe citado o sin
    /// citar del comando (p. ej. "C:\...\unins000.exe" /S o MsiExec.exe /X{GUID}).
    /// </summary>
    private static bool UninstallTargetExists(string uninstallString)
    {
        var s = uninstallString.Trim();

        // MsiExec.exe /X{GUID}: siempre "existe" (Windows Installer lo resuelve).
        if (s.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase)) return true;

        // Quitar comillas del exe si empieza con comillas.
        string exe;
        if (s.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = s.IndexOf('"', 1);
            if (end < 0) return true; // malformado: no marcar como resto
            exe = s[1..end];
        }
        else
        {
            var space = s.IndexOf(' ');
            exe = space < 0 ? s : s[..space];
        }

        if (string.IsNullOrWhiteSpace(exe)) return true;
        try { exe = Environment.ExpandEnvironmentVariables(exe); } catch { }
        return File.Exists(exe);
    }

    /// <summary>Convierte "Software\Classes\.xyz" a "HKEY_CURRENT_USER\Software\Classes\.xyz".</summary>
    private static string ToRegFormat(string hkcuRelativePath)
        => $"HKEY_CURRENT_USER\\{hkcuRelativePath.Replace("/", "\\")}";

    /// <summary>Escapa comillas y backslashes del nombre de valor para el .reg.</summary>
    private static string EscapeRegValueName(string name)
        => name.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Formatea el valor para el .reg según su tipo.</summary>
    private static string FormatRegValue(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString
            => $"\"{EscapeRegValueName(value.ToString() ?? "")}\"",
        RegistryValueKind.DWord => $"dword:{(uint)(int)value:x8}",
        RegistryValueKind.QWord => $"qword:{(ulong)(long)value:x16}",
        RegistryValueKind.Binary => "hex:" + BitConverter.ToString((byte[])value).Replace("-", ","),
        _ => "hex(0):" // Unknown/None: no representable — no se exportará
    };
}
