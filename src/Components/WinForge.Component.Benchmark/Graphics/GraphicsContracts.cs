using System;
using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// API gráfica con la que se corre la escena. La elige el usuario y la lista sale de
/// <see cref="BackendRegistry"/>, que solo ofrece lo que la máquina realmente soporta.
/// </summary>
public enum GraphicsApi
{
    D3D11,
    D3D12,
    Vulkan,
    OpenGL
}

/// <summary>
/// Por qué una API se puede (o no) usar en esta máquina. Se informa siempre: una opción
/// deshabilitada sin motivo es una opción que el usuario no puede arreglar.
/// </summary>
public sealed record BackendAvailability(
    GraphicsApi Api,
    string ApiName,
    bool Available,
    string Reason,
    string AdapterName = "");

/// <summary>Modo de presentación de la escena.</summary>
public enum PresentationMode
{
    /// <summary>Ventana con bordes, redimensionable. El modo más cómodo para mirar las métricas al lado.</summary>
    Windowed,

    /// <summary>Ventana sin bordes que cubre el monitor elegido, sin cambiar el modo de video.</summary>
    BorderlessFullscreen,

    /// <summary>Pantalla completa exclusiva del swapchain: el compositor de Windows queda afuera.</summary>
    ExclusiveFullscreen
}

/// <summary>Todo lo que un backend necesita para arrancar.</summary>
public sealed class BackendInitOptions
{
    public required nint WindowHandle { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required SceneDefinition Scene { get; init; }

    /// <summary>Índice del adaptador elegido (0 = el que el backend considere principal).</summary>
    public int AdapterIndex { get; init; }

    /// <summary>Presentar sincronizado con el monitor. Por defecto NO: el objetivo es medir la escena.</summary>
    public bool VSync { get; init; }
}

/// <summary>
/// Contrato de un backend gráfico. Cada API (D3D11, D3D12, Vulkan, OpenGL) lo implementa
/// con su propia API, pero todos renderizan la MISMA escena (<see cref="SceneDefinition"/>)
/// y reportan las mismas dos cosas que hacen útil la medición: el tiempo de GPU por frame
/// y el resultado de presentar.
///
/// La escena se define como datos (malla, instancias, recorrido de cámara) justamente para
/// que agregar una API no obligue a redefinir la escena: se implementa el backend, no la escena.
/// </summary>
public interface IGraphicsBackend : IDisposable
{
    GraphicsApi Api { get; }
    string ApiName { get; }

    /// <summary>Adaptador con el que se renderiza (nombre tal como lo reporta la API).</summary>
    string AdapterName { get; }

    /// <summary>Detalle del adaptador y de la API (feature level, VRAM, versión de driver).</summary>
    string AdapterDetail { get; }

    /// <summary>True si la API puede tomar el monitor en exclusivo.</summary>
    bool SupportsExclusiveFullscreen { get; }

    /// <summary>True si la API expone timestamps de GPU (para reportar ms de GPU por frame).</summary>
    bool HasGpuTiming { get; }

    /// <summary>
    /// Tiempo de GPU del frame anterior en ms (0 si la API no lo expone o el dato no está listo).
    /// Se lee siempre del frame YA presentado: esperar el dato del frame en curso frenaría
    /// justamente lo que se quiere medir.
    /// </summary>
    double LastGpuFrameMs { get; }

    /// <summary>
    /// Tiempo que el hilo de render pasó esperando a la cola de la GPU (valla/allocador) antes de
    /// poder armar el frame, en ms. NO es trabajo del CPU: es la placa viniendo atrasada. Va como
    /// columna aparte para que el informe no diga "CPU" donde en realidad dice "la GPU todavía no
    /// terminó". En Direct3D 11 esta espera aparece en <see cref="Present"/> (la entrega).
    /// </summary>
    double LastQueueWaitMs { get; }

    /// <summary>
    /// True si el dispositivo se cayó (driver reiniciado, GPU removida). La corrida se aborta:
    /// seguir midiendo sobre un dispositivo perdido daría números inventados.
    /// </summary>
    bool DeviceLost { get; }

    void Initialize(BackendInitOptions options);
    void Resize(int width, int height);
    void SetFullscreen(bool exclusive);

    /// <summary>
    /// Dibuja el frame y cierra sus timestamps. NO presenta: presentar es un paso aparte
    /// (<see cref="Present"/>) porque su costo es OTRA cosa — la entrega al monitor puede
    /// incluir la espera por la cola de la GPU, y mezclarla con el trabajo del CPU haría
    /// que el informe dijera "CPU" donde en realidad dice "la placa viene atrasada".
    /// </summary>
    void RenderFrame(double timeSeconds);

    /// <summary>Presenta el frame dibujado y devuelve cuántos milisegundos tardó la entrega.</summary>
    double Present();
}
