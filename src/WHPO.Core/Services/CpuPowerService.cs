using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del servicio de gestión de planes de energía de Windows.
/// Usa la herramienta powercfg.exe (no requiere elevación para listar ni cambiar de plan).
/// </summary>
public class CpuPowerService : ICpuPowerService
{
    private readonly ILoggingService _loggingService;

    public CpuPowerService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    // powercfg escribe en la página de códigos OEM de la consola (p. ej. CP850 en español).
    // Leer como UTF-8 rompe los nombres con acentos, así que se decodifica con la OEM real.
    private static readonly Encoding OemEncoding = CreateOemEncoding();

    private static Encoding CreateOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    // ============ Catálogo de settings de energía (registro) ============
    // HKLM\...\Control\Power\PowerSettings contiene TODOS los settings conocidos de
    // energía, incluidos los ocultos (umbrales del procesador, modo boost, etc.) que
    // powercfg /q no muestra en planes reducidos. Es la misma fuente que usan Quick
    // CPU y el panel de Windows: nombre localizado, valores posibles con nombre,
    // rango y unidades.

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadString(IntPtr hInstance, uint id, StringBuilder lpBuffer, int cchBuffer);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr hLibModule);

    private sealed class CatalogSetting
    {
        public string Guid { get; set; } = "";
        public string Name { get; set; } = "";
        public List<(uint Value, string Name)>? PossibleValues;
        public bool HasRange;
        public string Units { get; set; } = "";
        // Valores predeterminados por tipo de plan (DefaultPowerSchemeValues\<GUID>):
        // lo que Windows usa cuando el plan no define el setting.
        public Dictionary<string, (uint Ac, uint Dc)>? Defaults;
    }

    private sealed class CatalogSubgroup
    {
        public string Guid { get; set; } = "";
        public string Name { get; set; } = "";
        public List<CatalogSetting> Settings { get; } = new();
    }

    // El catálogo no depende del plan: se carga una sola vez (Lazy).
    private static readonly Lazy<List<CatalogSubgroup>> PowerCatalog = new(LoadPowerCatalog);

    // El subgrupo "Configuración no perteneciente a ningún subgrupo" (SUB_NONE) no
    // existe como clave en el registro: sus settings viven planos bajo PowerSettings.
    // Se agrupan acá para que la UI los muestre juntos, como hace powercfg /q.
    private const string NoSubgroupGuid = "fea3413e-7e05-4911-9a71-700331f1c294";

    // Orden de importancia de los subgrupos en la comparación (de más a menos
    // importante; los no listados van al final en orden alfabético de GUID).
    private static readonly Dictionary<string, int> SubgroupPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        { "54533251-82be-4824-96c1-47b60b740d00", 0 },  // Administración de energía del procesador
        { "238c9fa8-0aad-41ed-83f4-97be242c8f20", 1 },  // Suspender
        { "7516b95f-f776-4464-8c53-06167f40cc99", 2 },  // Pantalla
        { NoSubgroupGuid, 3 },                            // Configuración no perteneciente a ningún subgrupo (incluye Tipo de plan de energía)
        { "0012ee47-9041-4b5d-9b77-535fba8b1442", 4 },  // Disco duro
        { "e73a048d-bf27-4f12-9731-8b2076e8891f", 5 },  // Batería
        { "2a737441-1930-4402-8d77-b2bebba308a3", 6 },  // USB
        { "501a4d13-42af-4429-9fd1-a8218c268e20", 7 },  // PCI Express
        { "4f971e89-eebd-4455-a8de-9e59040e7347", 8 },  // Botones de inicio/apagado y tapa
        { "9596fb26-9850-41fd-ac3e-f7c3c00afd4b", 9 },  // Configuración multimedia
        { "de830923-a562-41af-a086-e3a2c6bad2da", 10 }, // Configuración de ahorro de energía
        { "2e601130-5351-4d9d-8e04-252966bad054", 11 }, // Resistencia de inactividad
        { "48672f38-7a9a-4bb2-8bf8-3d85be19de4e", 12 }, // Configuración del control de interrupción
        { "8619b916-e004-4dd8-9b66-dae86f806698", 13 }  // Comportamiento de energía con reconocimiento de presencia
    };

    private static List<CatalogSubgroup> LoadPowerCatalog()
    {
        var result = new List<CatalogSubgroup>();
        var flatSettings = new List<CatalogSetting>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings");
            if (root == null) return result;

            foreach (var subName in root.GetSubKeyNames())
            {
                if (!Guid.TryParse(subName, out _)) continue;
                if (string.Equals(subName, NoSubgroupGuid, StringComparison.OrdinalIgnoreCase)) continue;
                using var subKey = root.OpenSubKey(subName);
                if (subKey == null) continue;

                // Los settings de SUB_NONE viven directo bajo PowerSettings: un subgrupo
                // tiene subclaves GUID (sus settings), un setting plano no las tiene.
                bool isSubgroup = subKey.GetSubKeyNames().Any(n => Guid.TryParse(n, out _));
                if (!isSubgroup)
                {
                    flatSettings.Add(BuildCatalogSetting(subKey));
                    continue;
                }

                var sg = new CatalogSubgroup { Guid = subName, Name = ResolveFriendlyName(subKey) };
                foreach (var settingName in subKey.GetSubKeyNames())
                {
                    if (!Guid.TryParse(settingName, out _)) continue;
                    using var setKey = subKey.OpenSubKey(settingName);
                    if (setKey == null) continue;
                    sg.Settings.Add(BuildCatalogSetting(setKey));
                }
                result.Add(sg);
            }

            // Agrupar los settings planos en SUB_NONE.
            var subNone = result.FirstOrDefault(s => string.Equals(s.Guid, NoSubgroupGuid, StringComparison.OrdinalIgnoreCase));
            if (subNone == null)
            {
                subNone = new CatalogSubgroup { Guid = NoSubgroupGuid, Name = "Configuración no perteneciente a ningún subgrupo" };
                result.Add(subNone);
            }
            foreach (var cs in flatSettings) subNone.Settings.Add(cs);

            // Ordenar de más importante a menos importante (procesador primero).
            result.Sort((a, b) =>
            {
                int pa = SubgroupPriority.TryGetValue(a.Guid, out var va) ? va : int.MaxValue;
                int pb = SubgroupPriority.TryGetValue(b.Guid, out var vb) ? vb : int.MaxValue;
                if (pa != pb) return pa.CompareTo(pb);
                return string.CompareOrdinal(a.Guid, b.Guid);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"No se pudo cargar el catálogo de energía: {ex.Message}");
        }
        return result;
    }

    private static CatalogSetting BuildCatalogSetting(RegistryKey setKey)
    {
        var cs = new CatalogSetting
        {
            Guid = setKey.Name.Substring(setKey.Name.LastIndexOf('\\') + 1),
            Name = ResolveFriendlyName(setKey)
        };
        if (string.IsNullOrWhiteSpace(cs.Name))
            cs.Name = $"Configuración {cs.Guid[..8]}";

        // Rango numérico (ValueMin/ValueMax/ValueIncrement) y unidades.
        if (setKey.GetValue("ValueMin") is int && setKey.GetValue("ValueMax") is int)
            cs.HasRange = true;
        if (setKey.GetValue("ValueUnits") is string units && !string.IsNullOrWhiteSpace(units))
            cs.Units = ResolveIndirectStringLocalized(units);

        // Valores posibles: subclaves numéricas ("0", "1", ...) con nombre y valor.
        var possible = new List<(uint Value, string Name)>();
        foreach (var pvName in setKey.GetSubKeyNames())
        {
            if (!int.TryParse(pvName, out _)) continue; // descarta DefaultPowerSchemeValues
            using var pvKey = setKey.OpenSubKey(pvName);
            if (pvKey == null) continue;
            if (pvKey.GetValue("SettingValue") is not int pvValue) continue;
            possible.Add(((uint)pvValue, ResolveFriendlyName(pvKey)));
        }
        if (possible.Count > 0)
            cs.PossibleValues = possible.OrderBy(p => p.Value).ToList();

        // Valores predeterminados por tipo de plan (AC/DC que Windows usa
        // cuando el plan no define este setting).
        using (var defKey = setKey.OpenSubKey("DefaultPowerSchemeValues"))
        {
            if (defKey != null)
            {
                var defaults = new Dictionary<string, (uint Ac, uint Dc)>(StringComparer.OrdinalIgnoreCase);
                foreach (var defName in defKey.GetSubKeyNames())
                {
                    if (!Guid.TryParse(defName, out _)) continue;
                    using var defTypeKey = defKey.OpenSubKey(defName);
                    if (defTypeKey == null) continue;
                    var ac = defTypeKey.GetValue("ACSettingIndex") as int?;
                    var dc = defTypeKey.GetValue("DCSettingIndex") as int?;
                    if (ac == null && dc == null) continue;
                    defaults[defName] = ((uint)(ac ?? dc ?? 0), (uint)(dc ?? ac ?? 0));
                }
                if (defaults.Count > 0) cs.Defaults = defaults;
            }
        }

        return cs;
    }

    private static string ResolveFriendlyName(RegistryKey key)
    {
        var raw = key.GetValue("FriendlyName") as string;
        if (!string.IsNullOrWhiteSpace(raw)) return ResolveIndirectStringLocalized(raw);
        // Algunos settings ocultos no tienen FriendlyName: se usa la descripción.
        var desc = key.GetValue("Description") as string;
        if (!string.IsNullOrWhiteSpace(desc))
        {
            var resolved = ResolveIndirectStringLocalized(desc);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
        }
        return string.Empty;
    }

    /// <summary>
    /// Resuelve una cadena indirecta ("@%SystemRoot%\...\powrprof.dll,-350,Texto") a su
    /// texto localizado. En vez de SHLoadIndirectString (que falla en algunos sistemas
    /// con shlwapi recortado), se carga el DLL como recurso y se lee la cadena con
    /// LoadString: devuelve el nombre en el idioma del sistema (p. ej. "Umbral de
    /// aumento de rendimiento de procesador"). Si no se puede, usa el texto en inglés
    /// que sigue a la segunda coma.
    /// </summary>
    private static string ResolveIndirectStringLocalized(string value)
    {
        if (!value.StartsWith("@")) return value;
        try
        {
            int comma1 = value.IndexOf(',');
            if (comma1 < 0) return value;
            // El separador tras el ID puede ser ',' (formato estándar "@dll,-id,Texto")
            // o ';' (formato mshtml/usbui "@dll,-id;Nombre").
            int sep2 = value.IndexOfAny(new[] { ',', ';' }, comma1 + 1);
            if (sep2 < 0) return value;
            var fallback = value.Substring(sep2 + 1).Trim();

            var path = Environment.ExpandEnvironmentVariables(value.Substring(1, comma1 - 1));
            if (!int.TryParse(value.Substring(comma1 + 1, sep2 - comma1 - 1),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                return fallback;

            // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE: leer recursos sin ejecutar código.
            var h = LoadLibraryEx(path, IntPtr.Zero, 0x00000022);
            if (h == IntPtr.Zero) return fallback;
            try
            {
                var sb = new StringBuilder(1024);
                if (LoadString(h, (uint)(id < 0 ? -id : id), sb, sb.Capacity) > 0)
                    return sb.ToString().Trim();
            }
            finally
            {
                FreeLibrary(h);
            }
            return fallback;
        }
        catch
        {
            return ResolveIndirectString(value);
        }
    }

    /// <summary>
    /// Traduce el índice crudo de powercfg (p. ej. "00000001" o "00000238") a texto
    /// legible usando el catálogo: nombre localizado si el setting tiene valores
    /// posibles, porcentaje o tiempo según sus unidades, o decimal. Un valor vacío
    /// significa que el plan no lo define (la UI muestra "--" = usa el de Windows).
    /// </summary>
    private static string FormatSettingValue(CatalogSetting setting, string rawHex)
    {
        if (string.IsNullOrWhiteSpace(rawHex)) return string.Empty;
        if (!uint.TryParse(rawHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            return rawHex;

        if (setting.PossibleValues != null)
        {
            foreach (var pv in setting.PossibleValues)
            {
                if (pv.Value == raw && !string.IsNullOrWhiteSpace(pv.Name))
                    return pv.Name;
            }
        }

        var units = setting.Units ?? string.Empty;
        var unitsNorm = units.Replace(" ", "").ToLowerInvariant();
        if (unitsNorm == "%" || unitsNorm.Contains("percent") || unitsNorm.Contains("porciento") || unitsNorm.Contains("porcent"))
            return $"{raw}%";
        if (unitsNorm.Contains("second") || unitsNorm.Contains("segundo"))
            return FormatSeconds(raw);
        // Algunos settings de "conteo" tienen ValueUnits que resuelve al propio
        // nombre del setting (no es una unidad real): se ignoran.
        if (!string.IsNullOrWhiteSpace(units) && !units.Equals(setting.Name, StringComparison.OrdinalIgnoreCase))
            return $"{raw} {units}";

        return raw.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatSeconds(uint seconds)
    {
        if (seconds == 0) return "Nunca";
        if (seconds < 60) return $"{seconds} seg";
        if (seconds < 3600) return $"{seconds / 60} min";
        int h = (int)(seconds / 3600);
        int m = (int)((seconds % 3600) / 60);
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    // GUID del plan Equilibrado: se usa como predeterminado genérico para planes
    // personalizados/overlay que no tienen entrada propia en DefaultPowerSchemeValues.
    private const string BalancedSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    /// <summary>
    /// Resuelve el valor predeterminado de un setting para el plan dado: primero el
    /// tipo del propio plan; si el plan es personalizado/overlay (sin defaults propios),
    /// cae al predeterminado del plan Equilibrado.
    /// </summary>
    private static (uint Ac, uint Dc)? ResolveDefaultValue(Dictionary<string, (uint Ac, uint Dc)>? defaults, string planGuid)
    {
        if (defaults == null || defaults.Count == 0) return null;
        if (defaults.TryGetValue(planGuid, out var own)) return own;
        if (defaults.TryGetValue(BalancedSchemeGuid, out var balanced)) return balanced;
        return defaults.Values.FirstOrDefault();
    }

    public List<PowerPlanInfo> GetPowerPlans()
    {
        var plans = new List<PowerPlanInfo>();
        try
        {
            // powercfg /l lista los planes. El plan activo viene marcado con un "*"
            var output = RunPowerCfg("/l");
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var activeGuid = GetActivePowerPlanGuid();

            foreach (var line in lines)
            {
                // Formato:  [GUID  (Nombre)  *]
                var match = Regex.Match(line, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)");
                if (!match.Success) continue;

                var guid = match.Groups[1].Value;
                var name = match.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(name)) continue;

                plans.Add(new PowerPlanInfo(guid, name, string.Equals(guid, activeGuid, StringComparison.OrdinalIgnoreCase)));
            }
            _loggingService.LogInfo($"Planes de energía detectados: {plans.Count}");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo planes de energía", ex);
        }
        return plans;
    }

    public string GetActivePowerPlanGuid()
    {
        try
        {
            var output = RunPowerCfg("/getactivescheme");
            var match = Regex.Match(output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
            if (match.Success) return match.Groups[1].Value;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo plan de energía activo", ex);
        }
        return string.Empty;
    }

    public async Task<CommandResult> SetActivePowerPlanAsync(string planGuid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(planGuid))
                return new CommandResult(false, "No se proporcionó un plan de energía válido.");

            var output = await Task.Run(() => RunPowerCfg($"/setactive {planGuid}"));
            // powercfg no devuelve nada en éxito; un error aparece en la consola
            if (output.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                _loggingService.LogWarning($"Error al cambiar plan de energía: {output.Trim()}");
                return new CommandResult(false, output.Trim());
            }

            _loggingService.LogInfo($"Plan de energía activado: {planGuid}");
            return new CommandResult(true, $"Plan de energía establecido correctamente.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error cambiando plan de energía", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    private string RunPowerCfg(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding
        };
        using var process = Process.Start(psi);
        if (process == null) return string.Empty;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output + error;
    }

    // ===================== Detalle del plan (powercfg /q) =====================

    public PowerPlanDetail? GetPowerPlanDetails(string planGuid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(planGuid)) return null;
            var output = RunPowerCfg($"/q {planGuid}");
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Valores AC/DC efectivos del plan (powercfg los resuelve con todas las
            // capas: plan base, overlay y políticas) + settings que no están en el catálogo.
            var values = new Dictionary<(string Sub, string Set), (string Ac, string Dc)>();
            var extras = new List<PowerSubgroupInfo>();
            string planName = planGuid;
            string? currentSubgroupGuid = null;
            string? currentSettingGuid = null;

            // Línea con GUID + nombre entre paréntesis: el nivel se deduce de la
            // indentación (0 = plan, 2 = subgrupo, 4 = configuración). El prefijo
            // localizado ("GUID de plan de energía:", "GUID de subgrupo:", etc.)
            // se ignora para no depender del idioma.
            var guidLine = new Regex(@"^(\s*)[^:]*:\s*([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)");

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();

                // Sección de directivas de grupo (solo aparece si hay override):
                // no tiene valores AC/DC y rompería la estructura, se corta ahí.
                if (line.Contains("Group Policies", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Directivas de grupo", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Opciones de directiva", StringComparison.OrdinalIgnoreCase))
                    break;

                var m = guidLine.Match(line);
                if (m.Success)
                {
                    int indent = m.Groups[1].Value.Length;
                    var guid = m.Groups[2].Value.ToUpperInvariant();
                    var name = m.Groups[3].Value.Trim();

                    if (indent < 2)
                    {
                        // Línea del plan
                        planName = name;
                        currentSubgroupGuid = null;
                        currentSettingGuid = null;
                    }
                    else if (indent < 4)
                    {
                        // Subgrupo (se guarda por si no está en el catálogo)
                        currentSubgroupGuid = guid;
                        currentSettingGuid = null;
                        extras.Add(new PowerSubgroupInfo(guid, name));
                    }
                    else
                    {
                        // Configuración individual
                        currentSettingGuid = guid;
                        extras[^1].Settings.Add(new PowerSettingInfo(guid, name));
                    }
                    continue;
                }

                if (currentSubgroupGuid == null || currentSettingGuid == null) continue;

                // Valores AC/DC actuales (español: "corriente alterna/continua";
                // inglés: "Current AC/DC Power Setting Index").
                if (line.Contains("alterna", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("AC Power", StringComparison.OrdinalIgnoreCase))
                {
                    var hex = Regex.Match(line, @"0x([0-9a-fA-F]+)");
                    if (hex.Success)
                    {
                        var key = (currentSubgroupGuid, currentSettingGuid);
                        values[key] = (hex.Groups[1].Value, values.TryGetValue(key, out var v) ? v.Dc : "");
                    }
                }
                else if (line.Contains("continua", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("DC Power", StringComparison.OrdinalIgnoreCase))
                {
                    var hex = Regex.Match(line, @"0x([0-9a-fA-F]+)");
                    if (hex.Success)
                    {
                        var key = (currentSubgroupGuid, currentSettingGuid);
                        values[key] = (values.TryGetValue(key, out var v) ? v.Ac : "", hex.Groups[1].Value);
                    }
                }
            }

            // Armar el detalle desde el catálogo completo: TODOS los settings conocidos
            // (incluidos los ocultos que powercfg no muestra en planes reducidos), con
            // el valor efectivo del plan cuando lo define y vacío ("--") cuando usa el
            // predeterminado de Windows.
            var catalog = PowerCatalog.Value;
            var catalogSubgroups = new Dictionary<string, CatalogSubgroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var csg in catalog) catalogSubgroups[csg.Guid] = csg;

            var detail = new PowerPlanDetail(planGuid, planName);
            foreach (var csg in catalog)
            {
                var sg = new PowerSubgroupInfo(csg.Guid, csg.Name);
                foreach (var cs in csg.Settings)
                {
                    var s = new PowerSettingInfo(cs.Guid, cs.Name);
                    if (values.TryGetValue((csg.Guid.ToUpperInvariant(), cs.Guid.ToUpperInvariant()), out var v))
                    {
                        s.AcValue = FormatSettingValue(cs, v.Ac);
                        s.DcValue = FormatSettingValue(cs, v.Dc);
                    }
                    else
                    {
                        // El plan no lo define: mostrar el valor predeterminado de Windows
                        // para ese tipo de plan (así no quedan hileras de "--" inútiles).
                        var def = ResolveDefaultValue(cs.Defaults, planGuid);
                        if (def != null)
                        {
                            s.AcValue = FormatSettingValue(cs, def.Value.Ac.ToString("X", CultureInfo.InvariantCulture));
                            s.DcValue = FormatSettingValue(cs, def.Value.Dc.ToString("X", CultureInfo.InvariantCulture));
                        }
                    }
                    sg.Settings.Add(s);
                }
                detail.Subgroups.Add(sg);
            }

            // Settings que powercfg muestra pero el catálogo no conoce (de terceros).
            foreach (var esg in extras)
            {
                if (catalogSubgroups.ContainsKey(esg.Guid)) continue;
                foreach (var es in esg.Settings)
                {
                    if (values.TryGetValue((esg.Guid, es.Guid), out var v))
                    {
                        var bare = new CatalogSetting { Guid = es.Guid, Name = es.Name };
                        es.AcValue = FormatSettingValue(bare, v.Ac);
                        es.DcValue = FormatSettingValue(bare, v.Dc);
                    }
                }
                detail.Subgroups.Add(esg);
            }

            return detail;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error obteniendo detalle del plan {planGuid}", ex);
            return null;
        }
    }

    // ===================== Descripción (registro) =====================

    public string GetPowerPlanDescription(string planGuid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(planGuid)) return string.Empty;
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{planGuid}");
            var desc = key?.GetValue("Description") as string;
            if (string.IsNullOrWhiteSpace(desc)) return string.Empty;
            return ResolveIndirectString(desc);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudo leer la descripción del plan {planGuid}: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Cadenas indirectas del registro ("@C:\...\powrprof.dll,-15,Texto de fallback"):
    /// la descripción legible es el texto que sigue a la SEGUNDA coma (puede contener
    /// comas internas, por eso no se corta en la última).
    /// </summary>
    private static string ResolveIndirectString(string value)
    {
        if (!value.StartsWith("@")) return value;
        int first = value.IndexOf(',');
        if (first < 0) return value;
        int second = value.IndexOf(',', first + 1);
        return second >= 0 ? value.Substring(second + 1).Trim() : value;
    }

    // ===================== Renombrar / borrar =====================

    public async Task<CommandResult> RenamePowerPlanAsync(string planGuid, string newName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(planGuid) || string.IsNullOrWhiteSpace(newName))
                return new CommandResult(false, "Se necesita el GUID y el nuevo nombre del plan.");

            var output = await Task.Run(() => RunPowerCfg($"/setname {planGuid} \"{newName}\""));
            if (ContainsPowerCfgError(output))
            {
                _loggingService.LogWarning($"Error renombrando plan {planGuid}: {output.Trim()}");
                return new CommandResult(false, output.Trim());
            }

            _loggingService.LogInfo($"Plan renombrado: {planGuid} -> {newName}");
            return new CommandResult(true, $"Plan renombrado a \"{newName}\".");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error renombrando plan de energía", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    public async Task<CommandResult> DeletePowerPlanAsync(string planGuid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(planGuid))
                return new CommandResult(false, "No se proporcionó un plan válido.");

            var output = await Task.Run(() => RunPowerCfg($"/delete {planGuid}"));
            if (ContainsPowerCfgError(output))
            {
                _loggingService.LogWarning($"Error borrando plan {planGuid}: {output.Trim()}");
                return new CommandResult(false, output.Trim());
            }

            _loggingService.LogInfo($"Plan eliminado: {planGuid}");
            return new CommandResult(true, "Plan eliminado correctamente.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error borrando plan de energía", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    private static bool ContainsPowerCfgError(string output)
    {
        return output.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("no válido", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("no se pudo", StringComparison.OrdinalIgnoreCase);
    }
}
