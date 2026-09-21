using System.Collections.Generic;

namespace WHPO.Core.Services.Interfaces;

/// <summary>Categorías del limpiador de registro (todas sobre HKCU).</summary>
public enum RegistryCleanCategory
{
    /// <summary>Clases de extensión de archivo que apuntan a un ProgId que ya no existe.</summary>
    OrphanedFileExtensions = 0,
    /// <summary>Entradas de desinstalación de programas que ya no están instalados.</summary>
    UninstallLeftovers = 1,
    /// <summary>Nombres amigables cacheados (MuiCache) de ejecutables inexistentes.</summary>
    StaleMuiCache = 2
}

/// <summary>
/// Un elemento de registro que el limpiador puede borrar. Si <see cref="ValueName"/>
/// es null se borra la clave ENTERA (<see cref="KeyPath"/>); si no, solo ese valor.
/// </summary>
public sealed record RegistryCleanupItem(
    string Id,
    RegistryCleanCategory Category,
    string KeyPath,          // ruta HKCU relativa, p.ej. @"Software\Classes\.xyz"
    string? ValueName,       // null = borrar la clave entera
    string Detail,           // descripción para la UI
    string? ReferenceTarget, // exe/ProgId inexistente al que apuntaba
    int ValueCount);         // tamaño relativo (valores + subclaves) para la UI

/// <summary>Resultado de la limpieza de registro.</summary>
public sealed record RegistryCleanResult(
    int DeletedKeys,
    int DeletedValues,
    string? BackupFile,     // null si no había nada que borrar o falló el backup
    IReadOnlyList<string> Warnings);

/// <summary>
/// Motor del limpiador de registro: escanea, respalda (.reg) y limpia.
/// Todo sobre HKCU (no requiere admin). Falla segura: sin backup no se limpia.
/// </summary>
public interface IRegistryCleanerService
{
    /// <summary>Carpeta donde se guardan los backups .reg.</summary>
    string BackupDirectory { get; }

    /// <summary>Escanea la categoría pedida y devuelve los elementos encontrados.</summary>
    IReadOnlyList<RegistryCleanupItem> Scan(RegistryCleanCategory category);

    /// <summary>
    /// Exporta un backup .reg de todo lo que se va a borrar. Devuelve null si
    /// el backup falló (en cuyo caso NO hay que limpiar: falla segura).
    /// </summary>
    string? ExportBackup(IReadOnlyList<RegistryCleanupItem> items, string? suffix = null);

    /// <summary>
    /// Borra los elementos indicados. Exporta el backup primero; si el backup
    /// falla, no borra nada y lo reporta en las advertencias.
    /// </summary>
    RegistryCleanResult Clean(IReadOnlyList<RegistryCleanupItem> items);
}
