using System.Collections.Generic;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Origen de una entrada de inicio.
/// </summary>
public enum StartupSource
{
    Registry,      // HKLM\Run o HKCU\Run
    StartupFolder, // shell:startup o common startup
    PackagedApp    // App empaquetada (MSIX/UWP) con tarea de arranque en el manifest
}

/// <summary>
/// Una entrada de inicio del sistema: un programa que se lanza al iniciar sesión.
/// </summary>
public sealed record StartupEntry(
    string Id,           // clave única: "reg|HKCU\Run|OneDrive" o "folder|user|miApp.lnk"
    string Name,         // nombre visible
    string Command,      // línea de comandos, ruta del acceso directo o carpeta del paquete
    StartupSource Source,
    bool IsSystem,       // true = HKLM o common startup (requiere admin)
    bool IsEnabled       // estado actual
);

/// <summary>
/// Servicio de administración de inicio: enumera, activa/desactiva y elimina
/// programas que se ejecutan al iniciar sesión en Windows.
/// </summary>
public interface IStartupManagerService
{
    /// <summary>Enumerar todas las entradas de inicio detectadas.</summary>
    IReadOnlyList<StartupEntry> GetEntries();

    /// <summary>Activar o desactivar una entrada.</summary>
    bool Toggle(string id, bool enable);

    /// <summary>Eliminar una entrada de inicio.</summary>
    bool Delete(string id);
}