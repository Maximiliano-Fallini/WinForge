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
    /// <summary>
    /// Claves persistidas que empiezan con el prefijo (ej. "fan.curve.autoStart.").
    /// Permite decidir sin conocer los IDs de antemano (los IDs de fans solo se
    /// saben abriendo el hardware). Solo lectura del diccionario en memoria.
    /// </summary>
    IReadOnlyList<string> GetKeys(string prefix);
    void Remove(string key);
    void Save();
    void Load();
}