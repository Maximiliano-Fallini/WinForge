using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace WHPO.Core.Optimizations;

/// <summary>Resultado de aplicar (o revertir) un ajuste.</summary>
/// <param name="Success">True si terminó bien.</param>
/// <param name="Message">Mensaje para la UI.</param>
/// <param name="Commands">Comandos ejecutados, para la consola de la página.</param>
public record OptimizationApplyResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Commands)
{
    /// <summary>Resultado exitoso sin comandos.</summary>
    public static OptimizationApplyResult Ok(string message = "OK")
        => new(true, message, Array.Empty<string>());

    /// <summary>Resultado fallido.</summary>
    public static OptimizationApplyResult Fail(string message)
        => new(false, message, Array.Empty<string>());
}

/// <summary>
/// Aplica y revierte los ajustes del catálogo: claves de registro, tareas
/// programadas, planes de energía y scripts de PowerShell.
/// </summary>
public sealed class OptimizationApplier
{
    private readonly List<string> _commands = new();

    /// <summary>
    /// Aplica el ajuste en el estado pedido.
    /// </summary>
    /// <param name="setting">Ajuste a aplicar.</param>
    /// <param name="enabled">Estado deseado del toggle (null si no es un toggle).</param>
    /// <param name="optionIndex">Opción elegida (ajustes de tipo Selection).</param>
    /// <param name="numericValue">Valor numérico (ajustes con slider).</param>
    /// <param name="progress">Reporte de los comandos ejecutados (consola de la página).</param>
    public OptimizationApplyResult Apply(
        OptimizationSetting setting,
        bool? enabled,
        int? optionIndex,
        long? numericValue,
        IProgress<string>? progress = null)
    {
        _commands.Clear();
        var unsupported = OptimizationStateService.GetUnsupportedReason(setting);
        if (unsupported is not null)
            return OptimizationApplyResult.Fail($"No aplicable en este equipo: {unsupported}");

        try
        {
            // El tipo de control se deduce del DATO (ver OptimizationStateService.IsSelection):
            // el catálogo heredado marca todo como "Toggle".
            bool ok = OptimizationStateService.IsSelection(setting)
                ? ApplySelection(setting, optionIndex, progress)
                : OptimizationStateService.IsNumeric(setting)
                    ? ApplyNumeric(setting, numericValue, progress)
                    : ApplyToggle(setting, enabled ?? false, progress);

            if (!ok)
                return new OptimizationApplyResult(false, "No se pudo aplicar el ajuste.", _commands.ToArray());

            RunScripts(setting, enabled, progress);

            var message = _commands.Count > 0
                ? $"{_commands.Count} operación(es) aplicada(s)."
                : "OK";
            return new OptimizationApplyResult(true, message, _commands.ToArray());
        }
        catch (Exception ex)
        {
            return new OptimizationApplyResult(false, ex.Message, _commands.ToArray());
        }
    }

    /// <summary>
    /// Devuelve el ajuste al valor de fábrica de Windows (el "DefaultValue" del
    /// catálogo). Es el contrapeso de <see cref="Apply"/>: desde la misma fila
    /// se puede deshacer un cambio y volver a como lo trae el sistema de fábrica.
    /// </summary>
    public OptimizationApplyResult ApplyDefault(OptimizationSetting setting, IProgress<string>? progress = null)
    {
        _commands.Clear();
        var unsupported = OptimizationStateService.GetUnsupportedReason(setting);
        if (unsupported is not null)
            return OptimizationApplyResult.Fail($"No aplicable en este equipo: {unsupported}");

        try
        {
            bool ok = true;

            if (OptimizationStateService.IsSelection(setting))
            {
                // El valor de fábrica de un combo es UNA de sus opciones: borrar la
                // clave dejaría el ajuste sin valor (y el combo sin estado).
                var index = OptimizationStateService.DefaultOptionIndex(setting);
                if (index is null)
                    return OptimizationApplyResult.Fail("El catálogo no define la opción de fábrica de este ajuste.");
                ok &= ApplySelection(setting, index, progress);
            }
            else if (OptimizationStateService.IsNumeric(setting))
            {
                var value = OptimizationStateService.DefaultNumericValue(setting);
                if (value is null)
                    return OptimizationApplyResult.Fail("El catálogo no define el valor de fábrica de este ajuste.");
                ok &= ApplyNumeric(setting, value, progress);
            }
            else
            {
                foreach (var entry in setting.Registry)
                {
                    foreach (var target in ExpandTargets(entry))
                    {
                        if (target.DefaultValue.HasValue)
                            ok &= WriteRegistryValue(target, OptimizationStateService.JsonToObject(target.DefaultValue), progress);
                        else
                            ok &= DeleteRegistryValue(target, progress);
                    }
                }

                foreach (var task in setting.Tasks)
                {
                    ok &= SetTaskState(task, task.DefaultState ?? false, progress);
                }

                foreach (var power in setting.Power)
                {
                    ok &= SetPowerValue(power, power.DefaultAc ?? 0, power, progress);
                }
            }

            RunScripts(setting, null, progress);

            var message = _commands.Count > 0
                ? $"{_commands.Count} operación(es) restaurada(s)."
                : "OK";
            return new OptimizationApplyResult(true, message, _commands.ToArray());
        }
        catch (Exception ex)
        {
            return new OptimizationApplyResult(false, ex.Message, _commands.ToArray());
        }
    }

    /// <summary>
    /// Aplica el valor RECOMENDADO del catálogo (el mismo que marcan los perfiles
    /// preconfigurados). Es el contrapeso de <see cref="ApplyDefault"/> y lo que
    /// usa el botón de la estrella de cada fila: "ponelo como conviene", sin
    /// tener que buscar a mano qué valor propone el catálogo.
    /// </summary>
    public OptimizationApplyResult ApplyRecommended(OptimizationSetting setting, IProgress<string>? progress = null)
    {
        _commands.Clear();
        var unsupported = OptimizationStateService.GetUnsupportedReason(setting);
        if (unsupported is not null)
            return OptimizationApplyResult.Fail($"No aplicable en este equipo: {unsupported}");

        try
        {
            bool ok;
            bool? recommendedToggle = OptimizationStateService.RecommendedState(setting);

            if (OptimizationStateService.IsSelection(setting))
            {
                var index = OptimizationStateService.RecommendedOptionIndex(setting);
                if (index is null)
                    return OptimizationApplyResult.Fail("El catálogo no define la opción recomendada de este ajuste.");
                ok = ApplySelection(setting, index, progress);
            }
            else if (OptimizationStateService.IsNumeric(setting))
            {
                var value = OptimizationStateService.RecommendedNumericValue(setting);
                if (value is null)
                    return OptimizationApplyResult.Fail("El catálogo no define el valor recomendado de este ajuste.");
                ok = ApplyNumeric(setting, value, progress);
            }
            else
            {
                if (recommendedToggle is not bool enable)
                    return OptimizationApplyResult.Fail("El catálogo no define el estado recomendado de este ajuste.");
                ok = ApplyToggle(setting, enable, progress);
            }

            if (!ok)
                return new OptimizationApplyResult(false, "No se pudo aplicar el ajuste.", _commands.ToArray());

            // Los scripts de un ajuste de opciones/rango no saben de un "on": se
            // les pasa el estado recomendado cuando existe (igual que Apply).
            RunScripts(setting, recommendedToggle, progress);

            var message = _commands.Count > 0
                ? $"{_commands.Count} operación(es) aplicada(s)."
                : "OK";
            return new OptimizationApplyResult(true, message, _commands.ToArray());
        }
        catch (Exception ex)
        {
            return new OptimizationApplyResult(false, ex.Message, _commands.ToArray());
        }
    }

    // ====== Toggle ======

    private bool ApplyToggle(OptimizationSetting setting, bool enable, IProgress<string>? progress)
    {
        bool ok = true;

        foreach (var entry in setting.Registry)
        {
            foreach (var target in ExpandTargets(entry))
            {
                ok &= WriteRegistryEntry(target, enable, progress);
            }
        }

        foreach (var task in setting.Tasks)
        {
            ok &= SetTaskState(task, enable, progress);
        }

        foreach (var power in setting.Power)
        {
            ok &= SetPowerValue(power, enable, progress);
        }

        return ok;
    }

    // ====== Selection ======

    private bool ApplySelection(OptimizationSetting setting, int? optionIndex, IProgress<string>? progress)
    {
        if (optionIndex is not int index || index < 0 || index >= setting.Options.Count)
            return false;

        var option = setting.Options[index];
        bool ok = true;

        foreach (var entry in setting.Registry)
        {
            foreach (var target in ExpandTargets(entry))
            {
                var key = target.ValueName ?? "KeyExists";
                if (!option.ValueMappings.TryGetValue(key, out var mapped))
                {
                    // La opción no define esta clave: se deja como está.
                    continue;
                }

                ok &= WriteRegistryValue(target, OptimizationStateService.JsonToObject(mapped), progress);
            }
        }

        foreach (var power in setting.Power)
        {
            // El valor de powercfg puede venir como SimpleValue o dentro de
            // ValueMappings["PowerCfgValue"] (catálogo heredado).
            if (OptimizationStateService.PowerOptionValue(option) is int value)
                ok &= SetPowerValue(power, value, power, progress);
        }

        return ok;
    }

    // ====== Numeric ======

    private bool ApplyNumeric(OptimizationSetting setting, long? numericValue, IProgress<string>? progress)
    {
        if (numericValue is not long value) return false;

        bool ok = true;
        foreach (var entry in setting.Registry)
        {
            foreach (var target in ExpandTargets(entry))
            {
                ok &= WriteRegistryValue(target, value, progress);
            }
        }

        foreach (var power in setting.Power)
        {
            ok &= SetPowerValue(power, (int)value, power, progress);
        }

        return ok;
    }

    // ====== Registro ======

    /// <summary>
    /// Expande una entrada de registro a las claves concretas sobre las que
    /// hay que escribir (por interfaz de red o por monitor).
    /// </summary>
    private static IEnumerable<OptimizationRegistryEntry> ExpandTargets(OptimizationRegistryEntry entry)
    {
        if (!entry.ApplyPerNetworkInterface && !entry.ApplyPerMonitor)
        {
            yield return entry;
            yield break;
        }

        var (hive, subKey) = OptimizationStateService.SplitKeyPath(entry.KeyPath);
        if (hive is null)
        {
            yield return entry;
            yield break;
        }

        string[] subKeys;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            subKeys = key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch
        {
            subKeys = Array.Empty<string>();
        }

        if (subKeys.Length == 0)
        {
            yield return entry;
            yield break;
        }

        foreach (var sub in subKeys)
        {
            yield return new OptimizationRegistryEntry
            {
                KeyPath = entry.KeyPath + "\\" + sub,
                ValueName = entry.ValueName,
                RecommendedValue = entry.RecommendedValue,
                DefaultValue = entry.DefaultValue,
                EnabledValue = entry.EnabledValue,
                DisabledValue = entry.DisabledValue,
                ValueType = entry.ValueType,
                IsPrimary = entry.IsPrimary,
                BitMask = entry.BitMask,
                ModifyByteOnly = entry.ModifyByteOnly,
                CompositeStringKey = entry.CompositeStringKey,
                IsGroupPolicy = entry.IsGroupPolicy,
                BinaryByteIndex = entry.BinaryByteIndex,
                LockKeyAccess = entry.LockKeyAccess,
            };
        }
    }

    /// <summary>
    /// Escribe el valor que corresponde al estado pedido (activado o desactivado).
    /// </summary>
    private bool WriteRegistryEntry(OptimizationRegistryEntry entry, bool enable, IProgress<string>? progress)
    {
        var target = enable
            ? FirstNonNull(entry.EnabledValue)
            : FirstNonNull(entry.DisabledValue);

        // Sin valor concreto: la semántica es "la clave no debe existir".
        if (target is null)
            return DeleteRegistryValue(entry, progress);

        return WriteRegistryValue(entry, target, progress);
    }

    private static object? FirstNonNull(List<JsonElement?>? array)
    {
        if (array is null) return null;
        foreach (var item in array)
        {
            if (!item.HasValue || item.Value.ValueKind == JsonValueKind.Null) continue;
            return OptimizationStateService.JsonToObject(item.Value);
        }
        return null;
    }

    private bool WriteRegistryValue(OptimizationRegistryEntry entry, object? value, IProgress<string>? progress)
    {
        var (hive, subKey) = OptimizationStateService.SplitKeyPath(entry.KeyPath);
        if (hive is null)
        {
            Report(progress, $"Clave inválida: {entry.KeyPath}");
            return false;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(subKey);
            if (key is null)
            {
                Report(progress, $"No se pudo abrir la clave: {entry.KeyPath}");
                return false;
            }

            if (entry.ValueName is null)
            {
                // Toggle por existencia de la clave: no hay valor que escribir.
                return true;
            }

            if (entry.CompositeStringKey is not null)
            {
                var current = key.GetValue(entry.ValueName) as string ?? "";
                var updated = UpdateCompositeString(current, entry.CompositeStringKey, value?.ToString());
                key.SetValue(entry.ValueName, updated, RegistryValueKind.String);
                Report(progress, $"Set-ItemProperty -Path \"{entry.KeyPath}\" -Name \"{entry.ValueName}\" -Value \"{updated}\"");
                return true;
            }

            if (entry.ModifyByteOnly && entry.BitMask.HasValue)
            {
                var index = entry.BinaryByteIndex ?? 0;
                var current = key.GetValue(entry.ValueName) as byte[];
                var bytes = current?.ToArray() ?? Array.Empty<byte>();
                if (bytes.Length <= index)
                {
                    var grown = new byte[index + 1];
                    Array.Copy(bytes, grown, bytes.Length);
                    bytes = grown;
                }

                var mask = entry.BitMask.Value;
                var newByte = Convert.ToInt32(value ?? 0, CultureInfo.InvariantCulture) & mask;
                bytes[index] = (byte)((bytes[index] & ~mask) | newByte);
                key.SetValue(entry.ValueName, bytes, RegistryValueKind.Binary);
                Report(progress, $"Set-ItemProperty -Path \"{entry.KeyPath}\" -Name \"{entry.ValueName}\" -Value 0x{BitConverter.ToString(bytes).Replace("-", "")}");
                return true;
            }

            var kind = ResolveKind(entry.ValueType);
            key.SetValue(entry.ValueName, CoerceValue(value, kind, entry), kind);
            Report(progress, $"Set-ItemProperty -Path \"{entry.KeyPath}\" -Name \"{entry.ValueName}\" -Value {FormatValue(value)} -Type {kind}");
            return true;
        }
        catch (Exception ex)
        {
            Report(progress, $"Error en {entry.KeyPath}: {ex.Message}");
            return false;
        }
    }

    private bool DeleteRegistryValue(OptimizationRegistryEntry entry, IProgress<string>? progress)
    {
        var (hive, subKey) = OptimizationStateService.SplitKeyPath(entry.KeyPath);
        if (hive is null) return false;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: true);
            if (key is null) return true;

            if (entry.ValueName is null)
            {
                // La clave entera es el estado: se borra.
                baseKey.DeleteSubKeyTree(subKey);
                Report(progress, $"Remove-Item -Path \"{entry.KeyPath}\" -Recurse -Force");
                return true;
            }

            if (key.GetValue(entry.ValueName) is not null)
            {
                key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                Report(progress, $"Remove-ItemProperty -Path \"{entry.KeyPath}\" -Name \"{entry.ValueName}\"");
            }

            if (entry.LockKeyAccess) LockKey(key);
            return true;
        }
        catch (Exception ex)
        {
            Report(progress, $"Error al borrar en {entry.KeyPath}: {ex.Message}");
            return false;
        }
    }

    private static string UpdateCompositeString(string current, string subKey, string? value)
    {
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool replaced = false;

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i].Trim();
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            if (!part[..idx].Trim().Equals(subKey, StringComparison.OrdinalIgnoreCase)) continue;

            parts[i] = string.IsNullOrEmpty(value) ? "" : $"{subKey}={value}";
            replaced = true;
        }

        if (!replaced && !string.IsNullOrEmpty(value))
            parts.Add($"{subKey}={value}");

        return string.Join(";", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static RegistryValueKind ResolveKind(string valueType)
        => valueType.ToUpperInvariant() switch
        {
            "DWORD" => RegistryValueKind.DWord,
            "QWORD" => RegistryValueKind.QWord,
            "BINARY" => RegistryValueKind.Binary,
            "MULTISTRING" => RegistryValueKind.MultiString,
            "EXPANDSTRING" => RegistryValueKind.ExpandString,
            "NONE" => RegistryValueKind.Unknown,
            _ => RegistryValueKind.String,
        };

    private static object CoerceValue(object? value, RegistryValueKind kind, OptimizationRegistryEntry entry)
    {
        if (kind == RegistryValueKind.Binary)
        {
            if (value is byte[] bytes) return bytes;
            if (value is string s)
            {
                try { return Convert.FromBase64String(s); } catch { return Array.Empty<byte>(); }
            }
            return Array.Empty<byte>();
        }

        if (kind == RegistryValueKind.MultiString)
        {
            if (value is string[] arr) return arr;
            if (value is string s2) return new[] { s2 };
            return Array.Empty<string>();
        }

        if (kind == RegistryValueKind.DWord)
        {
            if (value is bool b) return b ? 1 : 0;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        if (kind == RegistryValueKind.QWord)
        {
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0L; }
        }

        return value?.ToString() ?? "";
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "$null",
        string s => $"\"{s}\"",
        byte[] b => $"0x{BitConverter.ToString(b).Replace("-", "")}",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>
    /// Bloquea la escritura de la clave para el usuario actual (lo usan algunos
    /// ajustes de Windows Update y de DirectX).
    /// </summary>
    private static void LockKey(RegistryKey key)
    {
        try
        {
            var security = key.GetAccessControl(AccessControlSections.Access);
            var rule = new RegistryAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                RegistryRights.SetValue | RegistryRights.CreateSubKey,
                AccessControlType.Deny);
            security.AddAccessRule(rule);
            key.SetAccessControl(security);
        }
        catch
        {
            // Sin permiso para cambiar la ACL: el ajuste igual quedó escrito.
        }
    }

    // ====== Tareas programadas ======

    private bool SetTaskState(OptimizationTaskEntry task, bool enable, IProgress<string>? progress)
    {
        var fullName = BuildTaskName(task);
        if (string.IsNullOrEmpty(fullName)) return true;

        var args = $"/Change /TN \"{fullName}\" /{(enable ? "ENABLE" : "DISABLE")}";
        Report(progress, $"schtasks.exe {args}");

        var output = OptimizationStateService.RunProcess("schtasks.exe", args);
        return output is null || !output.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTaskName(OptimizationTaskEntry task)
    {
        var path = task.TaskPath ?? "";
        if (path.Length == 0) return task.Id;
        if (!path.EndsWith('\\')) path += "\\";
        return path + task.Id;
    }

    // ====== Planes de energía ======

    private bool SetPowerValue(OptimizationPowerEntry power, bool enable, IProgress<string>? progress)
    {
        // En un toggle de powercfg el valor activo es el recomendado y el
        // desactivado es el de fábrica.
        var value = enable
            ? (power.RecommendedAc ?? power.DefaultAc ?? 0)
            : (power.DefaultAc ?? 0);
        return SetPowerValue(power, value, power, progress);
    }

    private bool SetPowerValue(OptimizationPowerEntry power, int value, OptimizationPowerEntry source, IProgress<string>? progress)
    {
        if (string.IsNullOrWhiteSpace(power.SubgroupGuid) || string.IsNullOrWhiteSpace(power.SettingGuid))
            return true;

        var acArgs = $"/setacvalueindex {power.SubgroupGuid} {power.SettingGuid} {value}";
        Report(progress, $"powercfg.exe {acArgs}");
        var acOutput = OptimizationStateService.RunProcess("powercfg.exe", acArgs);
        bool ok = acOutput is null || !acOutput.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(power.PowerModeSupport, "Separate", StringComparison.OrdinalIgnoreCase))
        {
            var dcValue = value;
            var dcArgs = $"/setdcvalueindex {power.SubgroupGuid} {power.SettingGuid} {dcValue}";
            Report(progress, $"powercfg.exe {dcArgs}");
            var dcOutput = OptimizationStateService.RunProcess("powercfg.exe", dcArgs);
            ok &= dcOutput is null || !dcOutput.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
        }

        var activeArgs = $"/setactive {power.SubgroupGuid}";
        Report(progress, $"powercfg.exe {activeArgs}");
        var activeOutput = OptimizationStateService.RunProcess("powercfg.exe", activeArgs);
        ok &= activeOutput is null || !activeOutput.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

        return ok;
    }

    // ====== Scripts ======

    private void RunScripts(OptimizationSetting setting, bool? enabled, IProgress<string>? progress)
    {
        foreach (var script in setting.Scripts)
        {
            var text = enabled == false ? script.DisabledScript : script.EnabledScript;
            if (string.IsNullOrWhiteSpace(text)) continue;

            Report(progress, "powershell.exe -NoProfile -NonInteractive -Command <script>");
            RunPowerShell(text, progress);
        }
    }

    /// <summary>
    /// Ejecuta un script de PowerShell. Los marcadores de plantilla (DNS) se
    /// sustituyen por valores vacíos: el script los trata como opcionales.
    /// </summary>
    private static void RunPowerShell(string script, IProgress<string>? progress)
    {
        try
        {
            var executable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell", "v1.0", "powershell.exe");

            if (!File.Exists(executable)) executable = "powershell.exe";

            var body = script
                .Replace("{{primary}}", "")
                .Replace("{{secondary}}", "")
                .Replace("{{dohtemplate}}", "auto");

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -Command \"" +
                            body.Replace("\"", "`\"") + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);

            if (!string.IsNullOrWhiteSpace(output))
                progress?.Report(output.Trim());
            if (!string.IsNullOrWhiteSpace(error))
                progress?.Report(error.Trim());
        }
        catch (Exception ex)
        {
            progress?.Report($"Error ejecutando el script: {ex.Message}");
        }
    }

    private static void Report(IProgress<string>? progress, string message)
    {
        if (progress is null) return;
        progress.Report(message);
    }
}
