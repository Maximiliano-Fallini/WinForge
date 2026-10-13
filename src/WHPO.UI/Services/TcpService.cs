using System.Text;
using System.Text.Json;
using WHPO.Core.Services;

namespace WHPO_UI.Services;

/// <summary>
/// Ajustes TCP/IP globales (el "TCP Optimizer" curado): Nagle por interfaz,
/// algoritmo de congestión, ECN, timestamps RFC 1323, RSS y Fast Open.
///
/// Lectura de estado: "netsh int tcp show global" tiene las ETIQUETAS localizadas
/// pero los VALORES siempre en inglés, y el ORDEN de los parámetros es fijo
/// (documentado por Microsoft) — por eso se parsea por orden y no por etiqueta.
/// Nagle y MTU se leen aparte (registro por GUID de la interfaz activa /
/// Get-NetIPInterface). Todo corre con la app elevada, sin UAC extra.
/// </summary>
public static class TcpService
{
    /// <summary>Estado actual (lectura) o deseado (aplicación) de los ajustes TCP.</summary>
    public sealed class TcpState
    {
        public bool RssEnabled;
        /// <summary>default | cubic | ctcp | newreno | compound</summary>
        public string CongestionProvider = "";
        public bool EcnEnabled;
        public bool TimestampsEnabled;
        public bool FastOpenEnabled;
        /// <summary>True = Nagle desactivado del todo (TCPNoDelay + TcpAckFrequency = 1).</summary>
        public bool NagleDisabled;
        /// <summary>TCPNoDelay = 1 en la interfaz activa: por sí solo ya desactiva Nagle.</summary>
        public bool NoDelaySet;
        /// <summary>TcpAckFrequency = 1 en la interfaz activa (delayed ACK apagado).</summary>
        public bool AckFreqSet;
        /// <summary>
        /// Plantilla TCP global por defecto del stack (netsh: internet | custom | datacenter…):
        /// la que aplica sin filtros. La congestión se escribe SIEMPRE sobre esta plantilla,
        /// para que esta card y la sección de plantillas no se pisen entre sí.
        /// Vacío = no se pudo leer (se cae a "internet").
        /// </summary>
        public string GlobalTemplate = "";
        public string AutoTuningLevel = "";
        public string? ActiveInterfaceGuid;
        public List<(string Name, int Mtu)> MtuList = new();
    }

    private const string NagleKeyPrefix = @"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";

    /// <summary>Lee el estado TCP global + Nagle + MTU en una sola pasada de PowerShell.</summary>
    public static async Task<TcpState?> GetStateAsync()
    {
        try
        {
            var script =
                "$out = netsh int tcp show global | Out-String;" +
                "$sup = netsh int tcp show supplemental | Out-String;" +
                "$a = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1;" +
                "$n = $null; $f = $null; $guid = '';" +
                "if ($a) {" +
                "  $key = '" + NagleKeyPrefix + "' + $a.InterfaceGuid.ToString();" +
                "  $n = (Get-ItemProperty -Path $key -Name TCPNoDelay -ErrorAction SilentlyContinue).TCPNoDelay;" +
                "  $f = (Get-ItemProperty -Path $key -Name TcpAckFrequency -ErrorAction SilentlyContinue).TcpAckFrequency;" +
                "  $guid = $a.InterfaceGuid.ToString();" +
                "};" +
                "$mtus = @(Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.ConnectionState -eq 'Connected' -and $_.InterfaceAlias -notmatch 'Loopback' } | " +
                "ForEach-Object { [pscustomobject]@{ Name = $_.InterfaceAlias; Mtu = $_.NlMtu } });" +
                "[pscustomobject]@{ Global = $out; Supplemental = $sup; Guid = $guid; NoDelay = $n; AckFreq = $f; Mtus = $mtus } | ConvertTo-Json -Compress -Depth 4";

            var (output, exit) = await PowerShellRunner.RunAsync(script);
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return null;

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            var state = new TcpState();
            var values = ParseGlobalValues(root.TryGetProperty("Global", out var g) ? g.GetString() ?? "" : "");
            // Si el orden de netsh no es el documentado, se aborta la lectura COMPLETA: es
            // preferible un "no se pudo leer" visible a mostrar un valor corrido con la
            // etiqueta equivocada (RSS/ECN/timestamps quedarían mintiendo sin avisar).
            if (values.Length == 0) return null;
            // Orden fijo de "netsh int tcp show global" (documentado por Microsoft):
            // 0 RSS, 1 Autotuning, 2 Congestión, 3 ECN, 4 Timestamps RFC1323, 5 RTO inicial,
            // 6 RSC, 7 NonSackRtt, 8 MaxSynRetransmisiones, 9 Fast Open, 10 Fast Open Fallback...
            if (values.Length > 0) state.RssEnabled = IsOn(values[0]);
            if (values.Length > 1) state.AutoTuningLevel = values[1].ToLowerInvariant();
            if (values.Length > 3) state.EcnEnabled = IsOn(values[3]);
            if (values.Length > 4) state.TimestampsEnabled = !values[4].Equals("disabled", StringComparison.OrdinalIgnoreCase);
            if (values.Length > 9) state.FastOpenEnabled = IsOn(values[9]);

            // El provider de congestión real está en "show supplemental" (los valores
            // vienen en inglés en cualquier idioma del sistema); si no se encuentra,
            // cae al add-on de "show global" (índice 2).
            var sup = root.TryGetProperty("Supplemental", out var s) ? s.GetString() ?? "" : "";
            var congestion = ParseCongestionFromSupplemental(sup);
            if (congestion.Length == 0 && values.Length > 2) congestion = values[2].ToLowerInvariant();
            state.CongestionProvider = congestion;
            state.GlobalTemplate = TcpTemplateService.ParseGlobalTemplateName(sup) ?? "";

            if (root.TryGetProperty("Guid", out var guidEl)) state.ActiveInterfaceGuid = guidEl.GetString();
            // Los dos valores se leen por separado: el tweak clásico escribe TCPNoDelay Y
            // TcpAckFrequency, así que una interfaz con uno solo es un estado PARCIAL que la UI
            // tiene que poder nombrar (antes se veía como "no aplicado" aunque Nagle ya
            // estuviera desactivado).
            state.NoDelaySet = root.TryGetProperty("NoDelay", out var nd) && nd.ValueKind == JsonValueKind.Number && nd.GetInt32() == 1;
            state.AckFreqSet = root.TryGetProperty("AckFreq", out var af) && af.ValueKind == JsonValueKind.Number && af.GetInt32() == 1;
            state.NagleDisabled = state.NoDelaySet && state.AckFreqSet;

            if (root.TryGetProperty("Mtus", out var mtus) && mtus.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mtus.EnumerateArray())
                {
                    if (m.TryGetProperty("Name", out var nm) && m.TryGetProperty("Mtu", out var mt))
                        state.MtuList.Add((nm.GetString() ?? "", mt.GetInt32()));
                }
            }
            return state;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Aplica el estado deseado completo. includeAutoTuning=true además setea el
    /// autotuning en Normal (botón "recomendado" — nunca se toca en presets).
    /// Devuelve (éxito, mensaje con el detalle de errores si hubo).
    /// </summary>
    public static async Task<(bool Ok, string Message)> ApplyAsync(TcpState desired, bool includeAutoTuning = false)
    {
        var ps = new StringBuilder("$e = ''");
        // El proveedor de congestión vive en los parámetros supplemental (Win10 1709+) y se
        // escribe SOBRE UNA PLANTILLA. Se usa la plantilla GLOBAL del stack (la que aplica sin
        // filtros, y la MISMA que edita la sección de plantillas): antes se hardcodeaba
        // "internet" y el fallback del cmdlet escribía en InternetCustom, que es OTRA plantilla
        // — si el stack no usaba "internet", el ajuste no hacía nada y el error no lo explicaba.
        // "netsh set global" NO tiene congestionprovider: esta es la única vía para cambiarlo.
        var template = TcpTemplateService.ResolveNetshName(desired.GlobalTemplate);
        // netsh SÍ acepta el literal "cubic" (documentado: none|ctcp|dctcp|cubic|bbr2|default).
        var netshProvider = desired.CongestionProvider == "ctcp" ? "ctcp" : "cubic";
        // El cmdlet documenta Default|CTCP|DCTCP, no "cubic": el fallback pide "default", que es
        // lo que en Windows 10/11 cliente resuelve a CUBIC, y apunta a la MISMA plantilla que
        // netsh (netsh "internet" → SettingName "Internet").
        var cmdletProvider = desired.CongestionProvider == "ctcp" ? "CTCP" : "Default";
        ps.Append("; netsh int tcp set supplemental template=" + template + " congestionprovider=" + netshProvider);
        ps.Append("; if ($LASTEXITCODE -ne 0) { try { Set-NetTCPSetting -SettingName " + TcpTemplateService.CmdletNameFor(template)
                  + " -CongestionProvider " + cmdletProvider + " -ErrorAction Stop } catch { $e += 'congestion (plantilla " + template + ");' } }");;
        ps.Append("; ").Append(Netsh("int tcp set global ecncapability=" + (desired.EcnEnabled ? "enabled" : "disabled"), "ecn"));
        ps.Append("; ").Append(Netsh("int tcp set global timestamps=" + (desired.TimestampsEnabled ? "enabled" : "disabled"), "timestamps"));
        ps.Append("; ").Append(Netsh("int tcp set global rss=" + (desired.RssEnabled ? "enabled" : "disabled"), "rss"));
        ps.Append("; ").Append(Netsh("int tcp set global fastopen=" + (desired.FastOpenEnabled ? "enabled" : "disabled"), "fastopen"));
        if (includeAutoTuning)
            ps.Append("; ").Append(Netsh("int tcp set global autotuninglevel=normal", "autotuning"));

        // Nagle: TCPNoDelay + TcpAckFrequency en la interfaz activa.
        // Off (Nagle desactivado) = valores a 1; On = se borran (default de Windows).
        ps.Append("; $a = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1");
        ps.Append("; if ($a) { $key = '" + NagleKeyPrefix + "' + $a.InterfaceGuid.ToString()");
        if (desired.NagleDisabled)
        {
            ps.Append("; New-ItemProperty -Path $key -Name TCPNoDelay -Value 1 -PropertyType DWord -Force | Out-Null");
            ps.Append("; New-ItemProperty -Path $key -Name TcpAckFrequency -Value 1 -PropertyType DWord -Force | Out-Null");
        }
        else
        {
            ps.Append("; Remove-ItemProperty -Path $key -Name TCPNoDelay -ErrorAction SilentlyContinue");
            ps.Append("; Remove-ItemProperty -Path $key -Name TcpAckFrequency -ErrorAction SilentlyContinue");
        }
        ps.Append(" }");
        ps.Append("; if ($e) { 'WFERR:' + $e } else { 'WFOK' }");

        var (output, exit) = await PowerShellRunner.RunAsync(ps.ToString());
        if (exit != 0) return (false, output);
        // La marca se busca en TODA la salida: netsh escribe sus errores en stdout, así que
        // aparecen ANTES de la marca y un StartsWith daba un falso "aplicado".
        if (PowerShellRunner.TryParseResult(output, out var detail)) return (true, "OK");
        return (false, detail);
    }

    /// <summary>Un comando netsh con captura de fallo en el acumulador $e.</summary>
    private static string Netsh(string command, string tag)
        => $"netsh {command}; if ($LASTEXITCODE -ne 0) {{ $e += '{tag};' }}";

    private static bool IsOn(string value)
        => value.Equals("enabled", StringComparison.OrdinalIgnoreCase)
           || value.Equals("on", StringComparison.OrdinalIgnoreCase)
           || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Busca el provider de congestión en la salida de "netsh int tcp show supplemental":
    /// los valores vienen en inglés (cubic/ctcp/newreno/...) en cualquier idioma del SO.
    /// </summary>
    private static string ParseCongestionFromSupplemental(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            int idx = line.IndexOf(':');
            if (idx < 0) continue;
            var value = line[(idx + 1)..].Trim().ToLowerInvariant();
            if (value is "cubic" or "ctcp" or "newreno" or "compound" or "illinois" or "dctcp" or "bbr2" or "none" or "default")
                return value;
        }
        return "";
    }

    /// <summary>
    /// Extrae los valores (parte derecha del ":") de cada línea de netsh, en orden, y
    /// comprueba que la forma sea la documentada en los índices que se usan. Si Windows
    /// cambia el orden o suma un parámetro delante, devolver vacío es mejor que leer un
    /// valor corrido y etiquetarlo mal: quien llama lo trata como lectura fallida.
    ///
    /// Netsh de Windows 10/11 incluye "none" como proveedor de congestión: en ambos
    /// SO se parsea por orden, así que "none" ocupa la misma posición que "default"
    /// (global) o "cubic" (supplemental) sin romper el parseo.
    /// </summary>
    private static string[] ParseGlobalValues(string output)
    {
        var values = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            int idx = line.IndexOf(':');
            if (idx < 0) continue;
            var value = line[(idx + 1)..].Trim();
            if (value.Length > 0) values.Add(value);
        }

        // Índices que se usan: 0 RSS, 1 Autotuning, 3 ECN, 4 Timestamps RFC1323, 9 Fast Open.
        bool shapeOk = values.Count > 9
            && IsBool(values[0]) && IsAutoTuningLevel(values[1])
            && IsBool(values[3]) && IsBool(values[4]) && IsBool(values[9]);
        return shapeOk ? values.ToArray() : Array.Empty<string>();
    }

    private static bool IsBool(string value)
        => value.Equals("enabled", StringComparison.OrdinalIgnoreCase)
           || value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
           || value.Equals("on", StringComparison.OrdinalIgnoreCase)
           || value.Equals("off", StringComparison.OrdinalIgnoreCase);

    private static bool IsAutoTuningLevel(string value)
    {
        var v = value.ToLowerInvariant();
        return v is "normal" or "disabled" or "restricted" or "highlyrestricted" or "experimental";
    }
}
