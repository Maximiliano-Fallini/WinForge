using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        using var process = Process.Start(psi);
        if (process == null) return string.Empty;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output + error;
    }
}
