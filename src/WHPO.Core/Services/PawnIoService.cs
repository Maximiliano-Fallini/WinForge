using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using WHPO.Core.Services.Fan;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Instalación/actualización/desinstalación de PawnIO, compartida por todos los
/// componentes. El binario se toma de una copia local verificada o se descarga de
/// la release oficial pinneada; nunca se ejecuta un instalador con hash distinto.
/// </summary>
public sealed class PawnIoService : IPawnIoService
{
    // Versión pinneada: no usar el permalink "latest", que cambiaría de hash sin
    // que la app lo sepa.
    private const string InstallerVersion = "2.2.0";
    private const string SetupUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    private const string SetupSha256 =
        "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";

    private const string LogTag = "PawnIoService:";

    // Componentes que necesitan el driver para funcionar completos.
    private static readonly HashSet<string> RequiredBy = new(StringComparer.OrdinalIgnoreCase)
    {
        "sensores",
        "ventiladores"
    };

    private readonly ILoggingService _loggingService;

    public PawnIoService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public PawnIoDriverStatus GetStatus()
    {
        bool installed = PawnIoStatus.TryGetVersion(out var version);
        return new PawnIoDriverStatus(installed, version, InstallerVersion);
    }

    public bool IsRequiredBy(string componentId)
        => !string.IsNullOrWhiteSpace(componentId) && RequiredBy.Contains(componentId);

    public async Task<PawnIOInstallResult> InstallAsync(bool force = false)
    {
        try
        {
            var status = GetStatus();
            if (status.Installed && !force)
                return new PawnIOInstallResult(true, "PawnIO ya está instalado.");

            Directory.CreateDirectory(CacheDir);
            var cached = Path.Combine(CacheDir, "PawnIO_setup.exe");

            if (!HasValidHash(cached))
            {
                // Copia opcional junto al exe (instalación sin red). Solo se usa
                // si coincide con el hash oficial.
                if (HasValidHash(BundledSetupPath))
                {
                    File.Copy(BundledSetupPath, cached, overwrite: true);
                    _loggingService.LogInfo($"{LogTag} instalador resuelto desde {BundledSetupPath}");
                }
                else
                {
                    await DownloadInstallerAsync(cached);
                }
            }

            if (!HasValidHash(cached))
            {
                try { File.Delete(cached); } catch { }
                _loggingService.LogWarning($"{LogTag} el instalador no coincide con el hash oficial; no se ejecuta");
                return new PawnIOInstallResult(
                    false,
                    "El instalador descargado no coincide con el hash oficial de PawnIO (SHA-256). No se ejecutó: probá de nuevo más tarde.");
            }

            _loggingService.LogInfo(
                $"{LogTag} instalador verificado ({new FileInfo(cached).Length / 1024} KB), ejecutando silencioso");

            var psi = new ProcessStartInfo
            {
                FileName = cached,
                // PawnIO no es NSIS/Inno: su CLI propia pide "-install -silent".
                // Con "/S" el instalador abre la interfaz (o falla) y la app
                // reportaba que el driver no había quedado registrado.
                Arguments = "-install -silent",
                UseShellExecute = true,
                Verb = "runas" // fuerza elevación si la app no corre como admin
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new PawnIOInstallResult(false, "No se pudo iniciar el instalador de PawnIO.");

            await process.WaitForExitAsync();
            _loggingService.LogInfo($"{LogTag} instalador terminó con código {process.ExitCode}");

            await Task.Delay(500); // darle un momento al driver para quedar operativo

            if (!PawnIoStatus.IsInstalled)
                return new PawnIOInstallResult(
                    false,
                    $"El instalador terminó (código {process.ExitCode}) pero PawnIO no quedó registrado.");

            return new PawnIOInstallResult(
                true,
                force ? "PawnIO actualizado correctamente." : "PawnIO instalado correctamente.");
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} instalación falló: {ex.Message}");
            return new PawnIOInstallResult(
                false,
                NetworkError.IsConnectionFailure(ex)
                    ? "No se pudo descargar el instalador de PawnIO: revisá tu conexión a internet e intentá de nuevo."
                    : $"Instalación fallida: {ex.Message}");
        }
    }

    public async Task<PawnIOInstallResult> UninstallAsync()
    {
        try
        {
            if (!PawnIoStatus.IsInstalled)
                return new PawnIOInstallResult(true, "PawnIO no está instalado.");

            var uninstaller = Path.Combine(ProgramFiles, "PawnIO", "uninstall.exe");
            if (!File.Exists(uninstaller))
                return new PawnIOInstallResult(
                    false,
                    "No se encontró el desinstalador de PawnIO. Desinstalalo desde Configuración → Aplicaciones.");

            var psi = new ProcessStartInfo
            {
                FileName = uninstaller,
                Arguments = "-uninstall -silent",
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new PawnIOInstallResult(false, "No se pudo iniciar el desinstalador de PawnIO.");

            await process.WaitForExitAsync();
            _loggingService.LogInfo($"{LogTag} desinstalador terminó con código {process.ExitCode}");
            await Task.Delay(500);

            return PawnIoStatus.IsInstalled
                ? new PawnIOInstallResult(
                    false,
                    $"El desinstalador terminó (código {process.ExitCode}) pero PawnIO sigue registrado.")
                : new PawnIOInstallResult(true, "PawnIO desinstalado correctamente.");
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} desinstalación falló: {ex.Message}");
            return new PawnIOInstallResult(false, $"No se pudo desinstalar PawnIO: {ex.Message}");
        }
    }

    // =====================================================================
    // Instalador (mismo patrón que Overclock USB: hash pinneado)
    // =====================================================================

    private static string CacheDir => Path.Combine(AppPaths.RootDir, "pawnio");

    /// <summary>Copia opcional junto al exe; solo se acepta si el hash coincide.</summary>
    private static string BundledSetupPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "Drivers", "PawnIO_setup.exe");

    private static string ProgramFiles =>
        Environment.GetEnvironmentVariable("ProgramW6432")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static bool HasValidHash(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return hash.Equals(SetupSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task DownloadInstallerAsync(string destPath)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await http.GetAsync(SetupUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await response.Content.CopyToAsync(fs);
    }
}
