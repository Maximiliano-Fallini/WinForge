using System;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Services;

/// <summary>
/// Orquesta la reproducción de macros disparada por atajos globales a nivel de app:
/// el vigilante arranca al iniciar la aplicación (no depende de visitar la pestaña
/// "Teclado y Macros") y la reproducción sobrevive a la navegación entre páginas.
/// Es thread-safe: el watcher invoca desde un hilo de fondo y los suscriptores
/// (la página) hacen marshaling a la UI por su cuenta.
/// </summary>
public sealed class MacroHotkeyService
{
    private readonly IMacroService _macroService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly object _lock = new();

    private CancellationTokenSource? _playCts;
    private string? _playingName;
    private bool _started;

    /// <summary>Disparado cuando arranca la reproducción (desde el hilo del watcher).</summary>
    public event Action<MacroDefinition>? PlaybackStarted;

    /// <summary>Disparado cuando la reproducción termina o se detiene (macro, cancelada).</summary>
    public event Action<MacroDefinition, bool>? PlaybackFinished;

    public MacroHotkeyService(
        IMacroService macroService,
        ISettingsService settingsService,
        ILoggingService loggingService)
    {
        _macroService = macroService;
        _settingsService = settingsService;
        _loggingService = loggingService;
    }

    public bool Enabled
    {
        get
        {
            try { return _settingsService.Get("macrosEnabled", true); }
            catch { return true; }
        }
        set
        {
            try
            {
                _settingsService.Set("macrosEnabled", value);
                _settingsService.Save();
                if (!value) Stop();
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"MacroHotkeyService: no se pudo guardar el estado de macros: {ex.Message}");
            }
        }
    }

    public bool IsPlaying => _playingName != null;

    /// <summary>Nombre de la macro en reproducción (null si no hay ninguna).</summary>
    public string? CurrentPlayingName => _playingName;

    /// <summary>Arranca el vigilante de atajos una sola vez (idempotente).</summary>
    public void EnsureStarted()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }
        try
        {
            RefreshArmedMacros();
            // El vigilante solo pollea mientras las macros estén activadas (el
            // toggle): desactivadas, el hilo duerme y no consume CPU.
            _macroService.StartHotkeyWatcher(OnHotkeyTriggered, () => Enabled);
            _loggingService.LogInfo("MacroHotkeyService: vigilante de atajos iniciado.");
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"MacroHotkeyService: no se pudo iniciar el vigilante: {ex.Message}");
        }
    }

    /// <summary>Recarga las macros armadas desde disco (se llama también al guardar).</summary>
    public void RefreshArmedMacros()
    {
        try { _macroService.UpdateArmedMacros(_macroService.Load()); }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"MacroHotkeyService: no se pudieron actualizar las macros armadas: {ex.Message}");
        }
    }

    /// <summary>Detiene la reproducción si está corriendo esta macro (usado al borrarla).</summary>
    public void StopIfPlaying(string macroName)
    {
        lock (_lock)
        {
            if (_playingName == null || _playingName != macroName) return;
            _playCts?.Cancel();
        }
        // El estado se limpia cuando termina la tarea (PlaybackFinished).
    }

    public void Stop()
    {
        lock (_lock)
        {
            _playCts?.Cancel();
        }
    }

    private void OnHotkeyTriggered(MacroDefinition macro)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            // Toggle: si ya se está reproduciendo esta macro, se detiene.
            if (_playCts != null && _playingName == macro.Name)
            {
                _playCts.Cancel();
                return;
            }
            // Detiene la reproducción actual (si hay) y arranca la nueva.
            if (_playCts != null) _playCts.Cancel();

            var cts = new CancellationTokenSource();
            _playCts = cts;
            _playingName = macro.Name;
            try
            {
                PlaybackStarted?.Invoke(macro);
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"MacroHotkeyService: error en evento PlaybackStarted: {ex.Message}");
            }
            _ = PlayAsyncLoop(macro, cts);
        }
    }

    private async Task PlayAsyncLoop(MacroDefinition macro, CancellationTokenSource cts)
    {
        bool cancelled = false;
        try
        {
            await _macroService.PlayAsync(macro, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"MacroHotkeyService: error reproduciendo macro: {ex.Message}");
        }

        lock (_lock)
        {
            if (!ReferenceEquals(_playCts, cts)) return;
            _playCts = null;
            _playingName = null;
        }
        try
        {
            PlaybackFinished?.Invoke(macro, cancelled);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"MacroHotkeyService: error en evento PlaybackFinished: {ex.Message}");
        }
    }
}
