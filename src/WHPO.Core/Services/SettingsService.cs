using System.Text.Json;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del servicio de configuración usando archivo JSON.
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly ILoggingService _logger;
    private readonly string _settingsFilePath;
    private Dictionary<string, JsonElement> _settings;

    public SettingsService(ILoggingService logger, string settingsDirectory)
    {
        _logger = logger;

        // Asegurar que el directorio existe
        if (!Directory.Exists(settingsDirectory))
        {
            Directory.CreateDirectory(settingsDirectory);
        }

        _settingsFilePath = Path.Combine(settingsDirectory, "settings.json");
        _settings = new Dictionary<string, JsonElement>();

        Load();
    }

    public T? Get<T>(string key)
    {
        if (_settings.TryGetValue(key, out var element))
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText());
        }
        return default;
    }

    public T? Get<T>(string key, T defaultValue)
    {
        if (_settings.TryGetValue(key, out var element))
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText()) ?? defaultValue;
        }
        return defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        _settings[key] = JsonDocument.Parse(json).RootElement.Clone();
        _logger.LogDebug($"Configuración establecida: {key}");
    }

    public bool Contains(string key)
    {
        return _settings.ContainsKey(key);
    }

    public void Remove(string key)
    {
        if (_settings.Remove(key))
        {
            _logger.LogDebug($"Configuración eliminada: {key}");
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
            _logger.LogInfo("Configuración guardada");
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al guardar la configuración", ex);
        }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                _settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new Dictionary<string, JsonElement>();
                _logger.LogInfo("Configuración cargada");
            }
            else
            {
                _settings = new Dictionary<string, JsonElement>();
                _logger.LogInfo("Archivo de configuración no existe, usando valores por defecto");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al cargar la configuración", ex);
            _settings = new Dictionary<string, JsonElement>();
        }
    }
}