using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace WHPO_UI.Services;

/// <summary>
/// Propiedades avanzadas del adaptador de red: el mismo set que edita TCP Optimizer
/// (velocidad y duplex, control de flujo, moderación de interrupciones, modo
/// ecológico EEE y el permiso de ahorro de energía de Windows).
///
/// Los ajustes viven en el registro, en
///   HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-...}\<NNNN>
/// esa clase cubre TODOS los miniports NDIS —Ethernet y Wi-Fi por igual—: lo que
/// cambia por adaptador es qué valores expone el driver (los que no existen en el
/// registro usan el default de fábrica del driver; escribirlos los crea). Es
/// exactamente lo que hace TCP Optimizer. La app corre elevada (manifest
/// requireAdministrator): la escritura es directa, sin UAC extra.
///
/// Tipos de registro: las propiedades del panel "avanzado" del driver son REG_SZ
/// (cadenas "0"/"1"/...); PnPCapabilities es la excepción histórica y va como
/// REG_DWORD. La lectura acepta ambos por si algún driver usa DWORD.
/// </summary>
public static class AdapterAdvancedService
{
    /// <summary>Una propiedad avanzada (una fila del panel "avanzado" del administrador de dispositivos).</summary>
    public sealed class PropDef
    {
        public required string RegName;      // nombre exacto en el registro
        public required string Title;        // etiqueta legible (clave de traducción)
        public required string Tooltip;      // explicación (clave de traducción)
        public required string Fallback;     // default de fábrica (cuando el registro no trae el valor)
        public required bool AsDword;        // REG_DWORD (PnPCapabilities) o REG_SZ (el resto)
        public List<PropOption>? Options;    // null = booleano implícito 0/1
    }

    public sealed record PropOption(string Value, string LabelKey);

    /// <summary>Las propiedades que la app expone (mismo set que TCP Optimizer).</summary>
    public static readonly List<PropDef> Definitions = new()
    {
        new PropDef
        {
            RegName = "SpeedDuplex",
            Title = "Velocidad y duplex",
            Tooltip = "TtAdapterSpeedDuplex",
            Fallback = "0",
            AsDword = false,
            Options = new List<PropOption>
            {
                new("0", "Negociación automática"),
                new("1", "10 Mbps Half Duplex"),
                new("2", "10 Mbps Full Duplex"),
                new("3", "100 Mbps Half Duplex"),
                new("4", "100 Mbps Full Duplex"),
                new("6", "1.0 Gbps Full Duplex"),
                new("7", "2.5 Gbps Full Duplex"),
            }
        },
        new PropDef
        {
            RegName = "*SpeedDuplex",
            Title = "Velocidad y duplex (Wi-Fi / driver *)",
            Tooltip = "TtAdapterSpeedDuplex",
            Fallback = "0",
            AsDword = false,
            Options = new List<PropOption>
            {
                new("0", "Negociación automática"),
                new("1", "10 Mbps Half Duplex"),
                new("2", "10 Mbps Full Duplex"),
                new("3", "100 Mbps Half Duplex"),
                new("4", "100 Mbps Full Duplex"),
                new("6", "1.0 Gbps Full Duplex"),
                new("7", "2.5 Gbps Full Duplex"),
            }
        },
        new PropDef
        {
            RegName = "FlowControl",
            Title = "Control de flujo",
            Tooltip = "TtAdapterFlowControl",
            Fallback = "3",
            AsDword = false,
            Options = new List<PropOption>
            {
                new("0", "Desactivado"),
                new("1", "Tx y Rx"),
                new("2", "Solo Rx"),
                new("3", "Solo Tx"),
            }
        },
        new PropDef
        {
            RegName = "InterruptModeration",
            Title = "Moderación de interrupciones",
            Tooltip = "TtAdapterInterruptModeration",
            Fallback = "1",
            AsDword = false,
        },
        new PropDef
        {
            RegName = "EEE",
            Title = "Modo ecológico (EEE)",
            Tooltip = "TtAdapterEee",
            Fallback = "1",
            AsDword = false,
        },
        new PropDef
        {
            RegName = "EnableGreenEthernet",
            Title = "Ethernet verde (ahorro de energía)",
            Tooltip = "TtAdapterGreenEthernet",
            Fallback = "0",
            AsDword = false,
        },
        new PropDef
        {
            RegName = "PnPCapabilities",
            Title = "Ahorro de energía de Windows",
            Tooltip = "TtAdapterPnP",
            Fallback = "0",
            AsDword = true,
            Options = new List<PropOption>
            {
                new("0", "Permitir que Windows apague el dispositivo"),
                new("24", "Impedir que Windows apague el dispositivo"),
            }
        },
    };
    public sealed record AdapterIface(string Guid, string Alias, string Description);

    public static Task<List<AdapterIface>> GetInterfacesAsync()
    {
        try
        {
            var script = "Get-NetAdapter -Physical -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.Status -eq 'Up' -or $_.Status -eq 'Disconnected' } | " +
                "ForEach-Object { [pscustomobject]@{ Guid = $_.InterfaceGuid.ToString(); Alias = $_.Name; Desc = $_.InterfaceDescription } } | ConvertTo-Json -Compress";
            var (output, exit) = PowerShellRunner.RunAsync(script).GetAwaiter().GetResult();
            var list = new List<AdapterIface>();
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return Task.FromResult(list);
            using var doc = JsonDocument.Parse(output);
            void AddEl(JsonElement el) => list.Add(new AdapterIface(
                el.TryGetProperty("Guid", out var g) ? g.GetString() ?? "" : "",
                el.TryGetProperty("Alias", out var a) ? a.GetString() ?? "" : "",
                el.TryGetProperty("Desc", out var d) ? d.GetString() ?? "" : ""));
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var el in doc.RootElement.EnumerateArray()) AddEl(el);
            else if (doc.RootElement.ValueKind == JsonValueKind.Object) AddEl(doc.RootElement);
            return Task.FromResult(list.Where(i => Guid.TryParse(i.Guid, out _)).ToList());
        }
        catch { return Task.FromResult(new List<AdapterIface>()); }
    }


    /// <summary>Snapshot de una propiedad: valor actual del registro y qué significa.</summary>
    public sealed record PropState(
        PropDef Def,
        string? RawValue,        // null = el registro no tiene el valor (usa el default del driver)
        string CurrentLabel,     // etiqueta del valor actual (para mostrar)
        bool Supported);         // el driver ya expone la propiedad en el registro

    private const string ClassPsPath = @"HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-E325-11CE-BFC1-08002BE10318}";

    /// <summary>
    /// Resuelve el PSPath de registro del adaptador físico indicado (por defecto el
    /// primer Up físico; funciona igual para Ethernet y Wi-Fi). El match es por
    /// NetCfgInstanceId == InterfaceGuid del adaptador que reporta Windows, así no
    /// hay ambigüedad con WAN Miniports ni adaptadores virtuales. Null si no hay.
    /// </summary>
    private static async Task<string?> ResolveAdapterKeyAsync(string? ifaceGuid = null)
    {
        string guidExpr = string.IsNullOrEmpty(ifaceGuid)
            ? "$a.InterfaceGuid.ToString()"
            : $"'{ifaceGuid}'";
        var script =
            "$a = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1;" +
            "if (-not $a) { '[null]' } else {" +
            "$sub = Get-ChildItem '" + ClassPsPath + "' -ErrorAction SilentlyContinue | " +
            "Where-Object { (Get-ItemProperty $_.PSPath -Name NetCfgInstanceId -ErrorAction SilentlyContinue).NetCfgInstanceId -eq " + guidExpr + " } | " +
            "Select-Object -First 1;" +
            "if (-not $sub) { '[null]' } else { $sub.PSPath.Replace('Microsoft.PowerShell.Core\\Registry::','') } }";

        var (output, exit) = await PowerShellRunner.RunAsync(script);
        if (exit != 0 || string.IsNullOrWhiteSpace(output)) return null;
        var trimmed = output.Trim().Trim('"');
        return trimmed == "[null]" ? null : trimmed;
    }

    /// <summary>Lee el estado de todas las propiedades del adaptador activo (null si no hay adaptador).</summary>
    public static Task<List<PropState>?> GetStateAsync()
        => GetStateForGuidAsync(null);

    /// <summary>
    /// Lee el estado de las propiedades del adaptador indicado (guid de Get-NetAdapter).
    /// guid null = el activo (primer Up físico). Resultado null = sin adaptador.
    /// Las propiedades que el driver no expone quedan como "no soportadas"
    /// (RawValue null): el combo muestra el default de fábrica y aplicarlas las crea.
    /// </summary>
    public static async Task<List<PropState>?> GetStateForGuidAsync(string? ifaceGuid)
    {
        try
        {
            var key = await ResolveAdapterKeyAsync(ifaceGuid);
            if (key == null) return null;

            var script =
                "$p = Get-ItemProperty '" + key + "'; " +
                "[pscustomobject]@{ " +
                string.Join("; ", Definitions.Select(d => $"{d.RegName} = $p.{d.RegName}")) +
                " } | ConvertTo-Json -Compress";

            var (output, exit) = await PowerShellRunner.RunAsync(script);
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return null;

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            var result = new List<PropState>();
            foreach (var def in Definitions)
            {
                string? raw = null;
                if (root.TryGetProperty(def.RegName, out var v))
                {
                    // REG_SZ llega como string, REG_DWORD como número (aceptamos ambos).
                    if (v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                        raw = v.GetString();
                    else if (v.ValueKind == JsonValueKind.Number)
                        raw = v.GetInt32().ToString();
                }
                result.Add(MakeState(def, raw));
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Aplica los valores indicados (RegName → valor string). Devuelve la lista de
    /// nombres que fallaron (vacía = todo OK). No reinicia el adaptador: los cambios
    /// de registro los toma el stack NDIS al vuelo o al reconectar; el efecto
    /// completo (sobre todo SpeedDuplex) puede requerir re-conectar el cable/red.
    /// </summary>
    public static async Task<List<string>> ApplyAsync(Dictionary<string, string> values, string? ifaceGuid = null)
    {
        var failed = new List<string>();
        try
        {
            var key = await ResolveAdapterKeyAsync(ifaceGuid);
            if (key == null) return Definitions.Select(d => d.RegName).ToList();

            var ps = new System.Text.StringBuilder("$e = @()");
            foreach (var def in Definitions)
            {
                if (!values.TryGetValue(def.RegName, out var value)) continue;
                var type = def.AsDword ? "DWord" : "String";
                var dword = def.AsDword ? $" -Value ([int]'{value}')" : $" -Value '{value}'";
                ps.Append($"; New-ItemProperty -Path '{key}' -Name '{def.RegName}' -PropertyType {type}{dword} -Force | Out-Null; if ($LASTEXITCODE) {{ $e += '{def.RegName}' }}");
            }

            var (output, exit) = await PowerShellRunner.RunAsync(ps.ToString());
            if (exit != 0)
            {
                // Error general: marcamos todos los pedidos como fallidos.
                failed.AddRange(values.Keys);
                return failed;
            }

            // Los nombres que el script reportó con error.
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = line.Trim('\'', '"', ' ');
                if (Definitions.Any(d => d.RegName == name)) failed.Add(name);
            }
            return failed;
        }
        catch
        {
            failed.AddRange(values.Keys);
            return failed;
        }
    }

    /// <summary>
    /// Restaurar valores de fábrica: borra las propiedades escritas del registro.
    /// Sin valor en el registro, cada driver vuelve a su default de fábrica — es el
    /// equivalente a poner "todo en automático". No requiere backup previo.
    /// </summary>
    public static async Task<(bool Ok, string Message)> RestoreDefaultsAsync(string? ifaceGuid = null)
    {
        try
        {
            var key = await ResolveAdapterKeyAsync(ifaceGuid);
            if (key == null) return (false, "no adapter");

            var script = string.Join("; ", Definitions.Select(d =>
                $"Remove-ItemProperty -Path '{key}' -Name '{d.RegName}' -ErrorAction SilentlyContinue"));

            var (_, exit) = await PowerShellRunner.RunAsync(script);
            return (exit == 0, exit == 0 ? "ok" : $"exit {exit}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static PropState MakeState(PropDef def, string? raw)
    {
        var effective = raw ?? def.Fallback;
        var label = def.Options?.FirstOrDefault(o => o.Value == effective)?.LabelKey
                    ?? def.Options?.FirstOrDefault(o => o.Value == "0")?.LabelKey
                    ?? effective;
        return new PropState(def, raw, label, raw != null);
    }
}
