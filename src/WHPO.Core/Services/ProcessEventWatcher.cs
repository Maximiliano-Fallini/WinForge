using System;
using System.Management;

namespace WHPO.Core.Services;

/// <summary>
/// Suscripción a los eventos de WMI de creación/terminación de procesos
/// (Win32_ProcessStartTrace / Win32_ProcessStopTrace): Windows avisa cuándo nace o
/// muere CUALQUIER proceso del sistema, sin polling. El consumidor filtra por los
/// procesos que le interesan.
///
/// Requiere permisos de administrador (la app corre elevada); si la suscripción
/// falla (sin admin o servicio CIM caído), TryStart devuelve false y el consumidor
/// simplemente reporta que los juegos no están en ejecución (no hay polling).
///
/// IMPORTANTE: los eventos solo cubren lo que pasa DESPUÉS de suscribirse; el
/// estado inicial (procesos ya corriendo) lo siembra el consumidor con una única
/// enumeración al arrancar.
/// </summary>
internal sealed class ProcessEventWatcher : IDisposable
{
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;

    public bool IsActive { get; private set; }

    /// <summary>Se dispara cuando nace un proceso: pid + nombre (sin extensión).</summary>
    public event Action<int, string>? ProcessStarted;

    /// <summary>Se dispara cuando muere un proceso: pid + nombre (sin extensión).</summary>
    public event Action<int, string>? ProcessStopped;

    /// <summary>
    /// Intenta suscribirse a ambos eventos. Devuelve true si quedó activo; si algo
    /// falla, limpia los watchers, deja IsActive en false y devuelve false. El error
    /// real sale por out para que el consumidor lo registre (el motivo típico es
    /// WBEM_E_ACCESS_DENIED cuando la app no corre elevada; si no, puede ser el
    /// servicio CIM).
    /// </summary>
    public bool TryStart(out string? error)
    {
        error = null;
        try
        {
            // IMPORTANTE: la query va con SELECT * — la variante con lista explícita
            // de columnas ("SELECT ProcessID, ProcessName FROM ...") tira
            // WBEM_E_INVALID_PARAMETER ("Parámetro no válido") en el constructor de
            // WqlEventQuery, y por eso el WMI nunca se activaba (los eventos traen
            // todas las propiedades igual; las que interesan se leen en el handler).
            _startWatcher = new ManagementEventWatcher(new WqlEventQuery(
                "SELECT * FROM Win32_ProcessStartTrace"));
            _startWatcher.EventArrived += OnStartEvent;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(new WqlEventQuery(
                "SELECT * FROM Win32_ProcessStopTrace"));
            _stopWatcher.EventArrived += OnStopEvent;
            _stopWatcher.Start();

            IsActive = true;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { _startWatcher?.Stop(); _stopWatcher?.Stop(); } catch { }
            _startWatcher = null;
            _stopWatcher = null;
            IsActive = false;
            return false;
        }
    }

    private void OnStartEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var props = e.NewEvent.Properties;
            uint pid = Convert.ToUInt32(props["ProcessID"].Value);
            string? name = Convert.ToString(props["ProcessName"].Value);
            if (pid != 0 && !string.IsNullOrEmpty(name))
                ProcessStarted?.Invoke((int)pid, name);
        }
        catch { /* un evento malformado no debe romper el watcher */ }
    }

    private void OnStopEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var props = e.NewEvent.Properties;
            uint pid = Convert.ToUInt32(props["ProcessID"].Value);
            string? name = Convert.ToString(props["ProcessName"].Value);
            if (pid != 0 && !string.IsNullOrEmpty(name))
                ProcessStopped?.Invoke((int)pid, name);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _startWatcher?.Stop(); _stopWatcher?.Stop(); } catch { }
        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
        _startWatcher = null;
        _stopWatcher = null;
        IsActive = false;
    }
}
