using System.Text.Json;
using Microsoft.Win32;

namespace WHPO.Core.Optimizations;

/// <summary>Operaciones revisadas del optimizador general. Solo se escriben los valores incluidos explícitamente aquí.</summary>
public sealed class GeneralOptimizerService
{
    // No agregar un ajuste solo porque el catálogo diga «recomendado»: revisarlo antes.
    private static readonly Dictionary<string, (string Path, string Name)> Approved = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notifications-welcome-experience"] = (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-310093Enabled"),
        ["notifications-system-setting-engagement"] = (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled"),
        ["notifications-tips-suggestions"] = (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled"),
        ["notifications-system-pane-suggestions"] = (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled"),
        ["start-show-suggestions"] = (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled"),
        ["start-menu-recommendations"] = (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations")
    };

    public static bool IsApproved(OptimizationSetting setting)
    {
        if (!Approved.TryGetValue(setting.Id, out var approved) || setting.Registry.Count != 1 ||
            setting.IsSubjectivePreference || setting.RequiresConfirmation || setting.Scripts.Count != 0 ||
            setting.Tasks.Count != 0 || setting.Power.Count != 0 || OptimizationStateService.IsSelection(setting) ||
            OptimizationStateService.IsNumeric(setting)) return false;
        var entry = setting.Registry[0];
        return string.Equals(entry.KeyPath, approved.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.ValueName, approved.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.ValueType, "DWord", StringComparison.OrdinalIgnoreCase) &&
            !entry.LockKeyAccess && !entry.ApplyPerNetworkInterface && !entry.ApplyPerMonitor &&
            !entry.ModifyByteOnly && entry.CompositeStringKey is null &&
            entry.RecommendedValue is JsonElement recommended && recommended.ValueKind == JsonValueKind.Number && recommended.TryGetInt32(out var r) && r == 0 &&
            entry.DefaultValue is JsonElement factory && factory.ValueKind == JsonValueKind.Number && factory.TryGetInt32(out var d) && d == 1;
    }

    /// <summary>
    /// El ajuste está aprobado y además aplica a la versión de Windows de este
    /// equipo. La aprobación por sí sola no alcanza: un ajuste puede ser seguro
    /// y aun así ser exclusivo de otra versión (por ejemplo, solo Windows 10).
    /// </summary>
    public static bool IsCompatible(OptimizationSetting setting)
        => OptimizationStateService.GetUnsupportedReason(setting) is null;

    private static void EnsureCompatible(OptimizationSetting setting)
    {
        var reason = OptimizationStateService.GetUnsupportedReason(setting);
        if (reason is not null)
            throw new InvalidOperationException($"Ajuste no compatible con esta versión de Windows ({reason}).");
    }

    private readonly IGeneralOptimizerRegistry _registry;
    private readonly string _directory;
    private readonly string _owner;

    public GeneralOptimizerService(IGeneralOptimizerRegistry registry, string directory, string owner)
    {
        _registry = registry;
        _directory = directory;
        _owner = owner;
    }

    public string SavedPath => Path.Combine(_directory, "general-optimizer-saved.json");
    public string AutomaticPath => Path.Combine(_directory, "general-optimizer-before-last-run.json");
    public bool HasSaved => File.Exists(SavedPath);
    public bool HasAutomatic => File.Exists(AutomaticPath);

    public GeneralOptimizerRegistryValue Read(OptimizationSetting setting)
    {
        if (!IsApproved(setting)) throw new InvalidOperationException("Ajuste no autorizado.");
        return _registry.Read(setting.Registry[0]);
    }

    public void Save(IReadOnlyList<OptimizationSetting> settings, bool automatic)
    {
        ValidateSettings(settings);
        var entries = settings.Select(s => new GeneralOptimizerSnapshotEntry(s.Id, _registry.Read(s.Registry[0]))).ToList();
        var snapshot = new GeneralOptimizerSnapshot(1, _owner, Environment.MachineName, DateTimeOffset.UtcNow, entries);
        Directory.CreateDirectory(_directory);
        var path = automatic ? AutomaticPath : SavedPath;
        var temp = Path.Combine(_directory, $".general-optimizer-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public GeneralOptimizerSnapshot Load(bool automatic, IReadOnlyList<OptimizationSetting> catalog)
    {
        var snapshot = JsonSerializer.Deserialize<GeneralOptimizerSnapshot>(File.ReadAllText(automatic ? AutomaticPath : SavedPath))
            ?? throw new InvalidDataException("La copia está vacía.");
        if (snapshot.Version != 1 || snapshot.Owner != _owner || snapshot.Machine != Environment.MachineName || snapshot.Entries is null || snapshot.Entries.Count == 0)
            throw new InvalidDataException("La copia no pertenece a este equipo y usuario o tiene un formato inválido.");
        var known = catalog.Where(IsApproved).ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot.Entries)
        {
            if (item is null || string.IsNullOrEmpty(item.Id) || !known.ContainsKey(item.Id) || !seen.Add(item.Id) || item.Value is null)
                throw new InvalidDataException("La copia contiene ajustes inválidos o duplicados.");
            _registry.Validate(item.Value);
        }
        return snapshot;
    }

    public void Apply(OptimizationSetting setting, bool recommended)
    {
        if (!IsApproved(setting)) throw new InvalidOperationException("Ajuste no autorizado.");
        EnsureCompatible(setting);
        var entry = setting.Registry[0];
        int value = (recommended ? entry.RecommendedValue : entry.DefaultValue)!.Value.GetInt32();
        _registry.Write(entry, new GeneralOptimizerRegistryValue(true, RegistryValueKind.DWord, JsonSerializer.SerializeToElement(value)));
    }

    public void Restore(OptimizationSetting setting, GeneralOptimizerRegistryValue value)
    {
        if (!IsApproved(setting)) throw new InvalidOperationException("Ajuste no autorizado.");
        EnsureCompatible(setting);
        _registry.Validate(value);
        _registry.Write(setting.Registry[0], value);
    }

    private static void ValidateSettings(IReadOnlyList<OptimizationSetting> settings)
    {
        if (settings.Count == 0 || settings.Any(s => !IsApproved(s)) || settings.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Count)
            throw new InvalidOperationException("La selección no contiene ajustes válidos.");
        foreach (var setting in settings)
            EnsureCompatible(setting);
    }
}

public sealed record GeneralOptimizerRegistryValue(bool Exists, RegistryValueKind Kind, JsonElement Value);
public sealed record GeneralOptimizerSnapshotEntry(string Id, GeneralOptimizerRegistryValue Value);
public sealed record GeneralOptimizerSnapshot(int Version, string Owner, string Machine, DateTimeOffset CreatedAt, List<GeneralOptimizerSnapshotEntry> Entries);

public interface IGeneralOptimizerRegistry
{
    GeneralOptimizerRegistryValue Read(OptimizationRegistryEntry entry);
    void Validate(GeneralOptimizerRegistryValue value);
    void Write(OptimizationRegistryEntry entry, GeneralOptimizerRegistryValue value);
}

/// <summary>Lee y escribe la vista de 64 bits sin confundir una lectura fallida con un valor ausente.</summary>
public sealed class WindowsGeneralOptimizerRegistry : IGeneralOptimizerRegistry
{
    public GeneralOptimizerRegistryValue Read(OptimizationRegistryEntry entry)
    {
        var (hive, subKey) = OptimizationStateService.SplitKeyPath(entry.KeyPath);
        if (hive != RegistryHive.CurrentUser || string.IsNullOrEmpty(subKey) || string.IsNullOrEmpty(entry.ValueName))
            throw new InvalidDataException("Ruta de registro inválida.");
        using var root = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
        using var key = root.OpenSubKey(subKey, writable: false);
        if (key is null || !key.GetValueNames().Contains(entry.ValueName, StringComparer.OrdinalIgnoreCase))
            return new GeneralOptimizerRegistryValue(false, RegistryValueKind.Unknown, JsonSerializer.SerializeToElement<object?>(null));
        var kind = key.GetValueKind(entry.ValueName);
        var value = key.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            ?? throw new InvalidDataException("No se pudo leer el valor del registro.");
        var result = new GeneralOptimizerRegistryValue(true, kind, JsonSerializer.SerializeToElement(value));
        Validate(result);
        return result;
    }

    public void Validate(GeneralOptimizerRegistryValue value)
    {
        if (!value.Exists) return;
        try
        {
            _ = value.Kind switch
            {
                RegistryValueKind.DWord => (object)value.Value.GetInt32(),
                RegistryValueKind.QWord => value.Value.GetInt64(),
                RegistryValueKind.String or RegistryValueKind.ExpandString => value.Value.GetString() ?? throw new InvalidDataException(),
                RegistryValueKind.Binary or RegistryValueKind.None => value.Value.GetBytesFromBase64(),
                RegistryValueKind.MultiString => value.Value.Deserialize<string[]>() ?? throw new InvalidDataException(),
                _ => throw new InvalidDataException("Tipo de registro no admitido.")
            };
        }
        catch (Exception ex) when (ex is not InvalidDataException) { throw new InvalidDataException("Valor de registro inválido.", ex); }
    }

    public void Write(OptimizationRegistryEntry entry, GeneralOptimizerRegistryValue value)
    {
        Validate(value);
        var (hive, subKey) = OptimizationStateService.SplitKeyPath(entry.KeyPath);
        if (hive != RegistryHive.CurrentUser || string.IsNullOrEmpty(subKey) || string.IsNullOrEmpty(entry.ValueName))
            throw new InvalidDataException("Ruta de registro inválida.");
        using var root = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
        if (!value.Exists)
        {
            using var existing = root.OpenSubKey(subKey, writable: true);
            existing?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            return;
        }
        using var key = root.CreateSubKey(subKey, writable: true) ?? throw new IOException("No se pudo abrir la clave de registro.");
        object data = value.Kind switch
        {
            RegistryValueKind.DWord => value.Value.GetInt32(),
            RegistryValueKind.QWord => value.Value.GetInt64(),
            RegistryValueKind.String or RegistryValueKind.ExpandString => value.Value.GetString()!,
            RegistryValueKind.Binary or RegistryValueKind.None => value.Value.GetBytesFromBase64(),
            RegistryValueKind.MultiString => value.Value.Deserialize<string[]>()!,
            _ => throw new InvalidDataException("Tipo de registro no admitido.")
        };
        key.SetValue(entry.ValueName, data, value.Kind);
    }
}
