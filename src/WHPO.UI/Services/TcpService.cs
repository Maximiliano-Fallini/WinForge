using System.Text;
using System.Text.Json;

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
        /// <summary>True = Nagle desactivado (TCPNoDelay + TcpAckFrequency = 1).</summary>
        public bool NagleDisabled;
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

            if (root.TryGetProperty("Guid", out var guidEl)) state.ActiveInterfaceGuid = guidEl.GetString();
            state.NagleDisabled = root.TryGetProperty("NoDelay", out var nd) && nd.ValueKind == JsonValueKind.Number && nd.GetInt32() == 1
                                  && root.TryGetProperty("AckFreq", out var af) && af.ValueKind == JsonValueKind.Number && af.GetInt32() == 1;

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
        // El provider de congestión vive en los parámetros supplemental (Win10 1709+);
        // en sistemas viejos se configura con "set global". Se intenta supplemental y
        // se cae a global si no existe. "cubic" == el default de Windows.
        var provider = desired.CongestionProvider == "ctcp" ? "ctcp" : "cubic";
        ps.Append("; netsh int tcp set supplemental template=internet congestionprovider=" + provider);
        ps.Append("; if ($LASTEXITCODE -ne 0) { netsh int tcp set global congestionprovider=" + provider + " }");
        ps.Append("; if ($LASTEXITCODE -ne 0) { $e += 'congestion;' }");
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
        ps.Append("; if ($e) { 'ERRORS: ' + $e } else { 'OK' }");

        var (output, exit) = await PowerShellRunner.RunAsync(ps.ToString());
        if (exit != 0) return (false, output);
        if (output.StartsWith("ERRORS:", StringComparison.Ordinal))
            return (false, output);
        return (true, "OK");
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
            if (value is "cubic" or "ctcp" or "newreno" or "compound" or "illinois" or "dctcp")
                return value;
        }
        return "";
    }

    /// <summary>Extrae los valores (parte derecha del ":") de cada línea de netsh.</summary>
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
        return values.ToArray();
    }
}
