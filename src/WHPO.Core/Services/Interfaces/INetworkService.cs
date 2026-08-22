using System.Collections.Generic;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio para el módulo Red: adaptadores, DNS, flush DNS, benchmark y TCP/IP.
/// </summary>
public interface INetworkService
{
    /// <summary>
    /// Obtiene los servidores DNS configurados en los adaptadores activos.
    /// </summary>
    List<DnsServerInfo> GetDnsServers();

    /// <summary>
    /// Ejecuta el flush de la caché DNS (ipconfig /flushdns).
    /// </summary>
    Task<CommandResult> FlushDnsAsync();

    /// <summary>
    /// Configura los servidores DNS de un adaptador de red.
    /// </summary>
    Task<CommandResult> SetDnsServersAsync(string adapterName, string primaryDns, string secondaryDns);

    /// <summary>
    /// Mide la latencia de un servidor DNS (ping).
    /// </summary>
    Task<double> TestDnsLatencyAsync(string dnsServer);

    /// <summary>
    /// Ejecuta un benchmark de red: latencia (ping) y velocidad aproximada.
    /// </summary>
    Task<NetworkBenchmarkResult> RunBenchmarkAsync();

    /// <summary>
    /// Obtiene información de configuración TCP/IP (IP, máscara, gateway, DHCP).
    /// </summary>
    List<TcpIpInfo> GetTcpIpInfo();

    /// <summary>
    /// Obtiene los servidores DNS reales que está usando el sistema (incluye DHCP).
    /// </summary>
    List<string> GetActualDnsServers();
}

/// <summary>
/// Información de un servidor DNS.
/// </summary>
public record DnsServerInfo(
    string AdapterName,
    string ServerAddress,
    bool IsPrimary
);

/// <summary>
/// Resultado de un comando del sistema.
/// Output es el mensaje legible (español, para logs y fallback). Si el mensaje
/// tiene valores interpolados, MessageTemplate/MessageArgs permiten traducirlo:
/// la UI llama I18n.T(MessageTemplate, MessageArgs) para mostrar el texto en el
/// idioma actual (la plantilla puede reordenar los marcadores libremente).
/// </summary>
public record CommandResult(
    bool Success,
    string Output,
    string? MessageTemplate = null,
    object?[]? MessageArgs = null
);

/// <summary>
/// Resultado del benchmark de red.
/// </summary>
public record NetworkBenchmarkResult(
    double AverageLatencyMs,
    double MinLatencyMs,
    double MaxLatencyMs,
    int PacketsSent,
    int PacketsReceived,
    int PacketsLost,
    double PacketLossPercent,
    string TargetHost
);

/// <summary>
/// Información de configuración TCP/IP de un adaptador.
/// </summary>
public record TcpIpInfo(
    string AdapterName,
    string IpAddress,
    string SubnetMask,
    string Gateway,
    bool DhcpEnabled,
    string DhcpServer
);
