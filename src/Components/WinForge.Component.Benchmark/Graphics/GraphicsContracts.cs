using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Esferas de sombra del bloque por frame: hasta <see cref="SceneDefinition.MaxShadowCasters"/>
/// posiciones (xyz = centro, w = radio). El shader les hace un rayo-esfera contra la dirección del
/// sol para que los objetos grandes del primer plano se APOYEN en el suelo sin una pasada de
/// sombras aparte (que costaría pipeline nuevo en las cuatro APIs). Las que no se usan van en cero
/// y el shader las saltea.
///
/// Es un <c>InlineArray</c> y no ocho campos sueltos para que el bloque siga siendo UNA estructura
/// copiable con <c>MemoryMarshal</c> (Direct3D 11 y Vulkan suben el bloque de una sola escritura),
/// y para que el tamaño salga del mismo número que usa el layout del cbuffer.
/// </summary>
[InlineArray(SceneDefinition.MaxShadowCasters)]
internal struct ShadowCasterArray
{
    private Vector4 _element;
}

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

/// <summary>
/// Franja de métricas lista para dibujar DENTRO del frame (no como ventana aparte).
///
/// Por qué va adentro del frame: con vsync apagado el swapchain se entrega por el camino de
/// tearing (DXGI) o de presentación inmediata (Vulkan), y en ese camino Windows saca la ventana
/// de la composición del escritorio (direct scanout): una ventana aparte por encima NO se puede
/// componer, y por eso parpadeaba en Direct3D o directamente no aparecía en Vulkan. Dibujada en
/// el frame, la franja se ve SIEMPRE, en las cuatro APIs y en los tres modos de presentación.
/// </summary>
/// <param name="Pixels">
/// Píxeles <c>BGRA</c> PREMULTIPLICADOS por alfa, fila 0 = arriba (mismo orden que la textura).
/// </param>
/// <param name="Width">Ancho de la imagen en píxeles.</param>
/// <param name="Height">Alto de la imagen en píxeles.</param>
/// <param name="X">Posición horizontal del borde izquierdo, en píxeles del área de la escena.</param>
/// <param name="Y">Posición vertical del borde superior, en píxeles del área de la escena.</param>
/// <param name="Version">
/// Sube cuando cambia el contenido: el backend solo re-sube la textura cuando ve una versión nueva.
/// </param>
public sealed record OverlayFrame(byte[] Pixels, int Width, int Height, int X, int Y, long Version);

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

    /// <summary>
    /// Cómo se dibuja la ESCENA (sombras y lo que venga). Va acá y no en cada backend para que las
    /// cuatro APIs reciban la misma configuración por el mismo camino: ver <see cref="SceneGraphicsOptions"/>.
    /// </summary>
    public SceneGraphicsOptions Graphics { get; init; } = SceneGraphicsOptions.Default;
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

    /// <summary>
    /// False si se pidió pantalla completa exclusiva y el sistema NO la dio: el informe lo
    /// aclara en vez de presentar como exclusiva una corrida que salió en ventana.
    /// </summary>
    bool ExclusiveFullscreenAccepted { get; }

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
    /// True si la franja de métricas se dibuja DENTRO del frame (este backend la compone en su
    /// swapchain); false si el anfitrión tiene que usar la ventana siempre arriba (los backends
    /// que todavía no la integran). Ver <see cref="OverlayFrame"/>: dentro del frame es la única
    /// forma de que se vea con vsync apagado, donde Windows saca la ventana de la composición.
    /// </summary>
    bool OverlayInFrame { get; }

    /// <summary>
    /// Deja la franja de métricas lista para dibujar dentro del frame (null = sin franja). Se
    /// llama desde el hilo de render; el backend re-sube la textura solo cuando la
    /// <see cref="OverlayFrame.Version"/> cambia, así el costo por frame es el de un quad.
    /// </summary>
    void SetOverlay(OverlayFrame? frame);

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
