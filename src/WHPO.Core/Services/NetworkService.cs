using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del servicio de red usando WMI y comandos del sistema (ipconfig, ping).
/// </summary>
public class NetworkService : INetworkService
{
    private readonly ILoggingService _loggingService;

    public NetworkService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public List<DnsServerInfo> GetDnsServers()
    {
        var dnsServers = new List<DnsServerInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
            foreach (ManagementObject obj in searcher.Get())
            {
                // Usar NetConnectionID (nombre de la interfaz en Conexiones de red) para netsh
                var adapterName = obj["NetConnectionID"]?.ToString()?.Trim() 
                               ?? obj["Description"]?.ToString()?.Trim() 
                               ?? "Unknown";
                var dnsArray = obj["DNSServerSearchOrder"] as string[];
                if (dnsArray != null && dnsArray.Length > 0)
                {
                    for (int i = 0; i < dnsArray.Length; i++)
                    {
                        dnsServers.Add(new DnsServerInfo(
                            AdapterName: adapterName,
                            ServerAddress: dnsArray[i],
                            IsPrimary: i == 0
                        ));
                    }
                }
            }
            _loggingService.LogInfo($"Servidores DNS detectados: {dnsServers.Count}");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo servidores DNS", ex);
        }
        return dnsServers;
    }

    /// <summary>
    /// Obtiene los servidores DNS reales que está usando el sistema (incluye DHCP)
    /// </summary>
    public List<string> GetActualDnsServers()
    {
        var dnsList = new List<string>();
        try
        {
            // Usar System.Net.NetworkInformation para obtener DNS reales
            var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in networkInterfaces)
            {
                if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    var ipProps = ni.GetIPProperties();
                    foreach (var dns in ipProps.DnsAddresses)
                    {
                        if (dns.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            dnsList.Add(dns.ToString());
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo DNS reales", ex);
        }
        return dnsList.Distinct().ToList();
    }

    public async Task<CommandResult> FlushDnsAsync()
    {
        try
        {
            var output = await RunCommandAsync("ipconfig", "/flushdns");
            var success = !output.ToLowerInvariant().Contains("no se pudo") && !output.ToLowerInvariant().Contains("failed");
            _loggingService.LogInfo($"Flush DNS ejecutado: {(success ? "OK" : "Error")}");
            return new CommandResult(success, output);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error ejecutando flush DNS", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    public async Task<CommandResult> SetDnsServersAsync(string adapterName, string primaryDns, string secondaryDns)
    {
        try
        {
            // Limpiar configuración DNS existente y establecer la nueva
            var setCommand = string.IsNullOrEmpty(secondaryDns)
                ? $"interface ip set dns \"{adapterName}\" static {primaryDns}"
                : $"interface ip set dns \"{adapterName}\" static {primaryDns} primary";
            var setOutput = await RunCommandAsync("netsh", setCommand);
            var setSuccess = !setOutput.Contains("error", StringComparison.OrdinalIgnoreCase) && !setOutput.Contains("no se pudo", StringComparison.OrdinalIgnoreCase);

            if (!setSuccess)
            {
                _loggingService.LogError("Error configurando DNS primario", new Exception(setOutput));
                return new CommandResult(false, setOutput);
            }

            var result = setOutput;

            if (!string.IsNullOrEmpty(secondaryDns))
            {
                var addCommand = $"interface ip add dns \"{adapterName}\" {secondaryDns} index=2";
                var addOutput = await RunCommandAsync("netsh", addCommand);
                result += "\n" + addOutput;
            }

            _loggingService.LogInfo($"DNS configurados para {adapterName}: {primaryDns} / {secondaryDns}");
            return new CommandResult(true, result);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error configurando servidores DNS", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    public async Task<double> TestDnsLatencyAsync(string dnsServer)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(dnsServer, 3000);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                return reply.RoundtripTime;
            }
            return -1; // No se pudo medir
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error midiendo latencia DNS {dnsServer}", ex);
            return -1;
        }
    }

    public async Task<NetworkBenchmarkResult> RunBenchmarkAsync()
    {
        const string target = "8.8.8.8";
        try
        {
            var output = await RunCommandAsync("ping", $"-n 10 {target}");
            return ParsePingOutput(output, target);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error ejecutando benchmark de red", ex);
            return new NetworkBenchmarkResult(0, 0, 0, 0, 0, 0, 100, target);
        }
    }

    public List<TcpIpInfo> GetTcpIpInfo()
    {
        var tcpIpList = new List<TcpIpInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
            foreach (ManagementObject obj in searcher.Get())
            {
                // Usar NetConnectionID (nombre de la interfaz en Conexiones de red) para netsh
                var adapterName = obj["NetConnectionID"]?.ToString()?.Trim() 
                               ?? obj["Description"]?.ToString()?.Trim() 
                               ?? "Unknown";
                var ipArray = obj["IPAddress"] as string[];
                var maskArray = obj["IPSubnet"] as string[];
                var gatewayArray = obj["DefaultIPGateway"] as string[];
                var dhcpEnabled = Convert.ToBoolean(obj["DHCPEnabled"] ?? false);
                var dhcpServer = obj["DHCPServer"]?.ToString()?.Trim() ?? "";

                var ip = ipArray != null && ipArray.Length > 0 ? ipArray[0] : "";
                var mask = maskArray != null && maskArray.Length > 0 ? maskArray[0] : "";
                var gateway = gatewayArray != null && gatewayArray.Length > 0 ? gatewayArray[0] : "";

                // Solo agregar si tiene al menos una IP (IPv4 o IPv6)
                if (!string.IsNullOrEmpty(ip))
                {
                    tcpIpList.Add(new TcpIpInfo(
                        AdapterName: adapterName,
                        IpAddress: ip,
                        SubnetMask: mask,
                        Gateway: gateway,
                        DhcpEnabled: dhcpEnabled,
                        DhcpServer: dhcpServer
                    ));
                }
            }
            
            // Si no se encontraron adaptadores con IPEnabled, buscar cualquier adaptador conectado (físico o virtual)
            if (tcpIpList.Count == 0)
            {
                using var searcher2 = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionStatus=2");
                foreach (ManagementObject obj in searcher2.Get())
                {
                    var adapterName = obj["NetConnectionID"]?.ToString()?.Trim() 
                                   ?? obj["Description"]?.ToString()?.Trim() 
                                   ?? "Unknown";
                    
                    tcpIpList.Add(new TcpIpInfo(
                        AdapterName: adapterName,
                        IpAddress: "",
                        SubnetMask: "",
                        Gateway: "",
                        DhcpEnabled: true,
                        DhcpServer: ""
                    ));
                }
            }
            _loggingService.LogInfo($"Configuraciones TCP/IP detectadas: {tcpIpList.Count}");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo info TCP/IP", ex);
        }
        return tcpIpList;
    }

    private async Task<string> RunCommandAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process == null) return "";
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output + error;
    }

    private NetworkBenchmarkResult ParsePingOutput(string output, string target)
    {
        // Patrones para ping en español e inglés
        var avgMatch = Regex.Match(output, @"(?:Promedio|Average)\s*=\s*(\d+)ms", RegexOptions.IgnoreCase);
        var minMatch = Regex.Match(output, @"(?:Mínimo|Minimum)\s*=\s*(\d+)ms", RegexOptions.IgnoreCase);
        var maxMatch = Regex.Match(output, @"(?:Máximo|Maximum)\s*=\s*(\d+)ms", RegexOptions.IgnoreCase);
        var sentMatch = Regex.Match(output, @"(?:Enviados|Sent)\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        var receivedMatch = Regex.Match(output, @"(?:Recibidos|Received)\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        var lostMatch = Regex.Match(output, @"(?:Perdidos|Lost)\s*=\s*(\d+)", RegexOptions.IgnoreCase);

        double avg = avgMatch.Success ? double.Parse(avgMatch.Groups[1].Value) : 0;
        double min = minMatch.Success ? double.Parse(minMatch.Groups[1].Value) : 0;
        double max = maxMatch.Success ? double.Parse(maxMatch.Groups[1].Value) : 0;
        int sent = sentMatch.Success ? int.Parse(sentMatch.Groups[1].Value) : 0;
        int received = receivedMatch.Success ? int.Parse(receivedMatch.Groups[1].Value) : 0;
        int lost = lostMatch.Success ? int.Parse(lostMatch.Groups[1].Value) : 0;
        double lossPercent = sent > 0 ? (double)lost / sent * 100 : 100;

        _loggingService.LogInfo($"Benchmark red: avg={avg}ms, min={min}ms, max={max}ms, perdidos={lost}/{sent}");
        return new NetworkBenchmarkResult(avg, min, max, sent, received, lost, lossPercent, target);
    }
}