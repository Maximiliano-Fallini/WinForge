namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio para gestionar la configuración persistente de la aplicación.
/// </summary>
public interface ISettingsService
{
    T? Get<T>(string key);
    T? Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);
    bool Contains(string key);
    void Remove(string key);
    void Save();
    void Load();
}