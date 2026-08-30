namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Una muestra de tiempo de frame: cuánto duró el frame (ms, delta entre Present()
/// consecutivos), el timestamp ETW del evento (reloj QPC del sistema, uniforme
/// entre presents — es el reloj correcto para el eje X) y el timestamp de LLEGADA
/// (reloj de pared, UTC ticks — el ancla que permite drenar las muestras viejas
/// cuando el juego deja de presentar).
/// </summary>
public readonly record struct FrametimeSample(long EtwTicks, long TicksUtc, double FrameMs);

/// <summary>
/// Contador de FPS por proceso basado en ETW (provider Microsoft-Windows-DXGI,
/// evento DXGI_Present_Start). La arquitectura está preparada para agregar la
/// correlación completa de DxgKrnl tipo PresentMon, necesaria para APIs que no
/// exponen eventos DXGI.
/// No inyecta nada en el juego: solo escucha los eventos de presentación que ya
/// emite el runtime DXGI, por lo que funciona con juegos protegidos por anti-cheat.
/// Requiere permisos de administrador (los tiene la app).
/// </summary>
public interface IFpsMonitor
{
    /// <summary>¿La sesión ETW está corriendo?</summary>
    bool IsRunning { get; }

    /// <summary>Inicia la sesión ETW (idempotente).</summary>
    void Start();

    /// <summary>Detiene la sesión ETW y libera los recursos.</summary>
    void Stop();

    /// <summary>
    /// FPS del proceso (mediana del tiempo entre frames de los últimos ~30 frames).
    /// Devuelve 0 si no hay datos recientes.
    /// </summary>
    double GetFps(int pid);

    /// <summary>1% low FPS del proceso (0 si no hay suficientes muestras).</summary>
    double GetLow1(int pid);

    /// <summary>0.1% low FPS del proceso (0 si no hay suficientes muestras).</summary>
    double GetLow01(int pid);

    /// <summary>
    /// Serie reciente de tiempos de frame del proceso, del más viejo al más nuevo
    /// (hasta <paramref name="maxSamples"/>), cada una con su timestamp de llegada
    /// (reloj de pared). Para el gráfico de latencia del overlay. Devuelve un array
    /// vacío si no hay datos.
    /// </summary>
    FrametimeSample[] GetFrametimeSeries(int pid, int maxSamples);

    /// <summary>Limpia el estado de procesos que ya no presentan frames.</summary>
    void Prune();

    /// <summary>
    /// El proceso con mayor tasa de presentación actual (excluyendo <paramref name="excludePid"/>).
    /// Devuelve (0, 0) si no hay ningún proceso presentando.
    /// </summary>
    (int Pid, double Fps) GetMostActiveProcess(int excludePid);
}
