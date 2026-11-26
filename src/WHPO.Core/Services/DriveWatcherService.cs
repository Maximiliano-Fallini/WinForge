using System;
using System.IO;
using System.Management;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Detecta la conexión de unidades nuevas (pendrives, discos externos, etc.)
/// vía eventos WMI (__InstanceCreationEvent sobre Win32_LogicalDisk): Windows
/// avisa cuando aparece una unidad lógica nueva, sin escanear nada.
///
/// La query usa WITHIN 2 (WMI sondea cada 2 segundos internamente); es el mismo
/// mecanismo que usa ProcessEventWatcher para procesos y cuesta prácticamente
/// nada. No requiere permisos especiales más allá de los que ya tiene la app.
/// </summary>
public sealed class DriveWatcherService : IDriveWatcherService, IDisposable
{
    private readonly ILoggingService _logging;
    private readonly object _lock = new();
    private ManagementEventWatcher? _watcher;
    private bool _disposed;

    public DriveWatcherService(ILoggingService logging)
    {
        _logging = logging;
    }

    /// <summary>Se dispara con la raíz de la unidad conectada (ej: "E:\").</summary>
    public event Action<string>? DriveArrived;

    public void EnsureStarted()
    {
        lock (_lock)
        {
            if (_disposed || _watcher != null) return;

            try
            {
                // SELECT * obligatorio: la variante con columnas explícitas tira
                // WBEM_E_INVALID_PARAMETER en WqlEventQuery (ver ProcessEventWatcher).
                // WITHIN 2: intervalo de sondeo interno de WMI en segundos.
                _watcher = new ManagementEventWatcher(new WqlEventQuery(
                    "SELECT * FROM __InstanceCreationEvent WITHIN 2 " +
                    "WHERE TargetInstance ISA 'Win32_LogicalDisk'"));
                _watcher.EventArrived += OnEvent;
                _watcher.Start();
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"DriveWatcher: no se pudo suscribir a los eventos de unidades: {ex.Message}");
                try { _watcher?.Stop(); } catch { }
                _watcher?.Dispose();
                _watcher = null;
            }
        }
    }

    /// <summary>
    /// Corta la suscripción y libera el watcher (idempotente). No marca el servicio como
    /// disposed: un <see cref="EnsureStarted"/> posterior lo vuelve a levantar. Se usa al
    /// salir de la página de Limpieza o al ocultar la ventana en la bandeja.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_watcher == null) return;
            try { _watcher.EventArrived -= OnEvent; } catch { }
            try { _watcher.Stop(); } catch { }
            try { _watcher.Dispose(); } catch { }
            _watcher = null;
            _logging.LogInfo("DriveWatcher: suscripción de unidades detenida");
        }
    }

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            // El disco nuevo viene embebido como TargetInstance (Win32_LogicalDisk):
            // DeviceID es la letra ("E:"), lo convertimos a raíz ("E:\").
            if (e.NewEvent.Properties["TargetInstance"].Value is not ManagementBaseObject disk)
                return;

            var deviceId = Convert.ToString(disk.Properties["DeviceID"].Value);
            if (string.IsNullOrEmpty(deviceId)) return;

            var root = deviceId.EndsWith("\\") ? deviceId : deviceId + "\\";
            if (!Directory.Exists(root)) return; // aún montando / unidad fantasma

            DriveArrived?.Invoke(root);
        }
        catch
        {
            // Un evento malformado no debe romper el watcher.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _watcher?.Stop(); } catch { }
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
