using System.Globalization;
using System.Text;
using System.Text.Json;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Estado de una plantilla TCP (el "template" del stack: internet, custom,
/// datacenter, compat, automatic). Los valores se leen de forma estructurada con
/// Get-NetTCPSetting (independiente del idioma del sistema) y se escriben con
/// netsh int tcp set supplemental, que es la vía que expone TODAS las perillas:
/// RTO mínimo, ventana de congestión inicial, delayed ACK, RSC, RACK, tail-loss
/// probe y el proveedor de congestión (incluido BBR2 experimental en Windows 11).
/// </summary>
public sealed class TcpTemplateState
{
    /// <summary>Nombre para netsh: internet | custom | datacenter | compat | automatic.</summary>
    public string Name { get; set; } = "";
    /// <summary>Nombre tal como lo reporta Get-NetTCPSetting (InternetCustom, etc.).</summary>
    public string CmdletName { get; set; } = "";
    public string CongestionProvider { get; set; } = "";
    public int MinRto { get; set; }
    public int InitialCongestionWindow { get; set; }
    public int DelayedAckTimeout { get; set; }
    public int DelayedAckFrequency { get; set; }
    public int MaxSynRetransmissions { get; set; }
    public string AutoTuningLevel { get; set; } = "";
    public bool EcnEnabled { get; set; }
    public bool TimestampsEnabled { get; set; }
    public bool NonSackRttResiliency { get; set; }
    /// <summary>La plantilla global por defecto (la que aplica el stack sin filtros).</summary>
    public bool IsGlobalDefault { get; set; }
    /// <summary>Si el cmdlet devolvió valores reales ("Automatic" la administra Windows y va vacía).</summary>
    public bool HasData { get; set; }
    /// <summary>Solo legibles en la plantilla global (netsh show supplemental).</summary>
    public bool? CwndRestart { get; set; }
    public bool? Rack { get; set; }
    public bool? TailLossProbe { get; set; }
}

/// <summary>
/// Editor de plantillas TCP: lee el estado real de cada plantilla y aplica los
/// cambios seleccionados. Todo es reversible: "restaurar" vuelve a los valores
/// leídos antes de tocar nada (los guarda la UI como snapshot).
/// </summary>
public static class TcpTemplateService
{
    /// <summary>(nombre netsh, nombre cmdlet, etiqueta en español).</summary>
    public static readonly (string Netsh, string Cmdlet, string Label)[] Templates =
    {
        ("internet", "Internet", "Internet"),
        ("custom", "InternetCustom", "Internet personalizada"),
        ("datacenter", "DatacenterCustom", "Datacenter personalizada"),
        ("compat", "Compat", "Compatibilidad (aplicaciones viejas)"),
        ("automatic", "Automatic", "Automática"),
    };

    /// <summary>Proveedores que se pueden pedir por netsh (BBR2 es experimental y solo Win11).</summary>
    public static readonly string[] CongestionProviders = { "default", "ctcp", "dctcp", "bbr2" };

    public static async Task<List<TcpTemplateState>> GetTemplatesAsync()
    {
        var list = new List<TcpTemplateState>();
        try
        {
            var script =
                "$names = @('Internet','InternetCustom','DatacenterCustom','Compat','Automatic');" +
                "$ts = @(Get-NetTCPSetting -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.SettingName } | " +
                "ForEach-Object { [pscustomobject]@{ N=$_.SettingName; Cp=[string]$_.CongestionProvider; MinRto=$_.MinRto; Icw=$_.InitialCongestionWindow; " +
                "AckT=$_.DelayedAckTimeout; AckF=$_.DelayedAckFrequency; MaxSyn=$_.MaxSynRetransmissions; Auto=[string]$_.AutoTuningLevelLocal; " +
                "Ecn=[string]$_.EcnCapability; Ts=[string]$_.Timestamps; NonSack=[string]$_.NonSackRttResiliency } });" +
                "$sup = netsh int tcp show supplemental | Out-String;" +
                "[pscustomobject]@{ T=@($ts); Sup=$sup } | ConvertTo-Json -Compress -Depth 5";

            var (output, exit) = await PowerShellRunner.RunAsync(script);
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return list;

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            var byCmdlet = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("T", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.TryGetProperty("N", out var n) && n.GetString() is string name)
                        byCmdlet[name] = el;

            // La plantilla global por defecto: la primera línea de "show supplemental"
            // la nombra ("...default template is internet") y el nombre va al final;
            // las etiquetas están localizadas, pero el nombre de la plantilla sale en inglés.
            string? globalName = null;
            var supText = root.TryGetProperty("Sup", out var sup) ? sup.GetString() ?? "" : "";
            foreach (var raw in supText.Split('\n'))
            {
                var line = raw.Trim().TrimEnd('.', ':', ' ');
                if (line.Length == 0) continue;
                var last = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (last == null) continue;
                if (Templates.Any(t => string.Equals(t.Netsh, last, StringComparison.OrdinalIgnoreCase)))
                {
                    globalName = last.ToLowerInvariant();
                    break;
                }
            }

            var supValues = ParseSupplementalValues(supText);

            foreach (var (netsh, cmdlet, label) in Templates)
            {
                var state = new TcpTemplateState { Name = netsh, CmdletName = cmdlet };
                if (byCmdlet.TryGetValue(cmdlet, out var el))
                {
                    state.CongestionProvider = Str(el, "Cp");
                    state.MinRto = Int(el, "MinRto");
                    state.InitialCongestionWindow = Int(el, "Icw");
                    state.DelayedAckTimeout = Int(el, "AckT");
                    state.DelayedAckFrequency = Int(el, "AckF");
                    state.MaxSynRetransmissions = Int(el, "MaxSyn");
                    state.AutoTuningLevel = Str(el, "Auto");
                    state.EcnEnabled = IsEnabled(Str(el, "Ecn"));
                    state.TimestampsEnabled = IsEnabled(Str(el, "Ts"));
                    state.NonSackRttResiliency = IsEnabled(Str(el, "NonSack"));
                    state.HasData = !string.IsNullOrWhiteSpace(state.CongestionProvider) || state.MinRto > 0;
                    state.IsGlobalDefault = string.Equals(netsh, globalName, StringComparison.OrdinalIgnoreCase);
                    if (state.IsGlobalDefault)
                    {
                        if (supValues.Count >= 8)
                        {
                            state.CwndRestart = IsEnabled(supValues[3]);
                            state.Rack = IsEnabled(supValues[6]);
                            state.TailLossProbe = IsEnabled(supValues[7]);
                        }
                    }
                }
                list.Add(state);
            }
        }
        catch
        {
            // Sin estado: la UI muestra el error y el botón Reintentar.
        }
        return list;
    }

    /// <summary>Etiqueta visible de una plantilla (para el selector).</summary>
    public static string LabelFor(string netsh)
        => Templates.FirstOrDefault(t => string.Equals(t.Netsh, netsh, StringComparison.OrdinalIgnoreCase)).Label ?? netsh;

    /// <summary>Nombre cmdlet para una plantilla netsh.</summary>
    public static string CmdletNameFor(string netsh)
        => Templates.FirstOrDefault(t => string.Equals(t.Netsh, netsh, StringComparison.OrdinalIgnoreCase)).Cmdlet ?? "Internet";

    /// <summary>
    /// Aplica los valores deseados sobre la plantilla indicada. Devuelve (ok, mensaje).
    /// Solo escribe lo que se le pasa; los campos "no soportados" de la plantilla
    /// (p. ej. RACK en plantillas que no lo exponen) se reportan en el mensaje.
    /// </summary>
    public static async Task<(bool Ok, string Message)> ApplyAsync(
        string netshTemplate,
        string congestionProvider,
        int? minRto,
        int? icw,
        int? delayedAckTimeout,
        int? delayedAckFrequency,
        bool? ecn,
        bool? timestamps,
        string? autoTuningLevel,
        int? maxSynRetransmissions,
        bool? nonSackRttResiliency,
        bool? cwndRestart,
        bool? rack,
        bool? tailLossProbe)
    {
        try
        {
            var ps = new StringBuilder("$e = ''");
            var sup = new StringBuilder($"netsh int tcp set supplemental template={netshTemplate}");
            if (!string.IsNullOrWhiteSpace(congestionProvider))
                sup.Append($" congestionprovider={congestionProvider}");
            if (minRto is int mr) sup.Append($" minrto={mr}");
            if (icw is int i) sup.Append($" icw={i}");
            if (delayedAckTimeout is int at) sup.Append($" delayedacktimeout={at}");
            if (delayedAckFrequency is int af) sup.Append($" delayedackfrequency={af}");
            if (cwndRestart is bool cr) sup.Append($" enablecwndrestart={(cr ? "enabled" : "disabled")}");
            if (rack is bool rk) sup.Append($" rack={(rk ? "enabled" : "disabled")}");
            if (tailLossProbe is bool tp) sup.Append($" taillossprobe={(tp ? "enabled" : "disabled")}");

            // Solo corre netsh si hay algo que setear (evita un error con la cadena vacía).
            if (sup.ToString().Trim() != $"netsh int tcp set supplemental template={netshTemplate}")
            {
                ps.Append($"; {sup}; if ($LASTEXITCODE -ne 0) {{ $e += 'supplemental;' }}");
            }

            var cmdlet = CmdletNameFor(netshTemplate);
            var sets = new List<string>();
            if (ecn is bool ec) sets.Add($"-EcnCapability {(ec ? "Enabled" : "Disabled")}");
            if (timestamps is bool ts) sets.Add($"-Timestamps {(ts ? "Enabled" : "Disabled")}");
            if (!string.IsNullOrWhiteSpace(autoTuningLevel)) sets.Add($"-AutoTuningLevelLocal {autoTuningLevel}");
            if (maxSynRetransmissions is int ms) sets.Add($"-MaxSynRetransmissions {ms}");
            if (nonSackRttResiliency is bool ns) sets.Add($"-NonSackRttResiliency {(ns ? "Enabled" : "Disabled")}");
            if (sets.Count > 0)
            {
                ps.Append($"; Set-NetTCPSetting -SettingName {cmdlet} {string.Join(" ", sets)} -ErrorAction SilentlyContinue");
                ps.Append("; if (-not $?) { $e += 'tcpsetting;' }");
            }

            ps.Append("; if ($e) { 'ERRORS: ' + $e } else { 'OK' }");
            var (output, exit) = await PowerShellRunner.RunAsync(ps.ToString());
            if (exit != 0) return (false, output);
            if (output.StartsWith("ERRORS:", StringComparison.Ordinal)) return (false, output);
            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Valores de "netsh int tcp show supplemental" por ORDEN (las etiquetas están
    /// localizadas, pero el orden es fijo y los valores salen en inglés):
    /// 0 minRto, 1 icw, 2 provider, 3 cwndRestart, 4 ackTimeout, 5 ackFreq, 6 rack, 7 tailLoss.
    /// </summary>
    private static List<string> ParseSupplementalValues(string output)
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
        return values;
    }

    private static bool IsEnabled(string value)
        => value.Equals("enabled", StringComparison.OrdinalIgnoreCase)
           || value.Equals("on", StringComparison.OrdinalIgnoreCase)
           || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v.ToString() : "";

    private static int Int(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
