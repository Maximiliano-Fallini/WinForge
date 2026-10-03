using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Backend OpenGL: la misma escena, el mismo bloque de constantes y el mismo shader que Direct3D
/// 11/12 y Vulkan —el GLSL no se escribe acá: se traduce del SPIR-V compartido al generar el
/// proyecto, ver <see cref="GlShaders"/>—.
///
/// Sin dependencias: habla con <c>opengl32.dll</c> y <c>gdi32.dll</c>, que están en Windows desde
/// siempre. El único trabajo extra que exige Windows es armar el contexto a mano (formato de
/// píxeles + WGL) y resolver las funciones posteriores a OpenGL 1.1, porque <c>opengl32.dll</c> solo
/// exporta las de 1.1.
///
/// Cosas que hay que saber para leer el código:
///
/// <list type="bullet">
/// <item>Se pide un contexto CORE 4.3. El GLSL no lo escribe este archivo: se traduce del MISMO
/// SPIR-V que usa Vulkan (ver <see cref="GlShaders"/> y tools/SpirvGen), y usa el binding por
/// <c>layout()</c> del bloque de constantes (core desde 4.2) y un storage buffer para los píxeles
/// de la franja (core desde 4.3). Antes esta API se conformaba con 3.3 porque el shader era un port
/// a mano; con 4.3 sigue cubriendo cualquier placa de los últimos doce años. Si el driver no lo da,
/// el backend lo dice en vez de dibujar mal.</item>
/// <item>NO se usa framebuffer propio: la escena dibuja en el framebuffer por defecto de la ventana,
/// que ya trae 24 bits de profundidad del formato de píxeles elegido. Redimensionar la ventana, por
/// eso, no exige recrear nada.</item>
/// <item>El bloque de constantes es el MISMO de 144 bytes que sube D3D11, con el calificador
/// <c>row_major</c> para que la matriz se lea con el orden de memoria de System.Numerics. Se sube
/// con <c>glBufferData</c> (huérfano del bloque anterior) y no con una escritura parcial: es la
/// forma de no frenar a la placa mientras el frame anterior todavía lo está leyendo.</item>
/// <item>La franja de métricas se dibuja DENTRO del frame (ver <see cref="OverlayFrame"/>): el
/// mismo programa del fondo, con blend premultiplicado, y los píxeles en un STORAGE BUFFER —el
/// mismo <c>StructuredBuffer</c> que leen Direct3D y Vulkan, porque el shader es el mismo—. Se sube
/// con una copia de memoria cada vez que el panel cambia de contenido (4 veces por segundo), no en
/// cada frame.</item>
/// <item>El tiempo de GPU sale de una query <c>GL_TIME_ELAPSED</c>. Se consulta SIEMPRE la de una
/// ranura atrás y sin esperar: si el dato no está listo, ese frame queda sin muestra (el informe
/// dice cuántas hubo) en vez de frenar justo lo que se está midiendo.</item>
/// <item>La espera de la cola aparece en <see cref="Present"/> (SwapBuffers), igual que en Direct3D 11:
/// OpenGL no expone una valla por frame, así que <see cref="LastQueueWaitMs"/> es 0 y el costo de la
/// entrega se mide donde realmente ocurre.</item>
/// <item>La pantalla completa exclusiva exigiría cambiar el modo de video del monitor
/// (<c>ChangeDisplaySettings</c>); este backend no la implementa y lo DICE
/// (<see cref="SupportsExclusiveFullscreen"/>) para que el informe lo aclare.</item>
/// </list>
///
/// Una instancia por corrida: vive en el hilo de render y no es thread-safe a propósito.
/// </summary>
public sealed unsafe class OpenGLBackend : IGraphicsBackend
{
    /// <summary>Frames en vuelo para las queries de tiempo (una ranura por frame, se leen atrasadas).</summary>
    private const int FramesInFlight = 2;

    /// <summary>Matriz de 64 bytes + 5 vectores de 16 (144): los dos últimos son el rectángulo y el
    /// tamaño de la franja de métricas (idéntico a las tres APIs de Direct3D y a Vulkan).</summary>
    // 208 bytes: 2 matrices de 64 (vista-proyección y su inversa) + 5 vectores de 16.
    private const int ConstantBufferSize =
        64 + 16 * 5 + 64 + 16 * Scenes.SceneDefinition.MaxShadowCasters + SceneStyleConstants.SizeInBytes
        + SceneLightConstants.SizeInBytes + SceneEnvironmentConstants.SizeInBytes;

    // Las ranuras de textura NO se eligen acá: son los binding que el GLSL declara con layout() (ver
    // GlShaders.g.cs), y los lee del shader la herramienta que lo genera (tools/SpirvGen). Escribir
    // el número a mano es lo que rompe en OpenGL sin decir nada: una unidad distinta de la que
    // declara el sampler no da un error, da una textura sin atar y una escena en negro.

    /// <summary>Ranura del atlas de detalle (ver Scenes.SceneDetailTexture): la que declara el GLSL.</summary>
    private const uint DetailTextureUnit = GlShaders.DetailTextureUnit;

    /// <summary>Ranura del arreglo de materiales (ver Scenes.MaterialAtlas): la que declara el GLSL.</summary>
    private const uint MaterialTextureUnit = GlShaders.MaterialTextureUnit;

    /// <summary>Ranura del shadow map (ver README-SOMBRAS.md): la que declara el GLSL. Es la MISMA
    /// que el binding del descriptor en Vulkan (t6), porque los dos shaders son el mismo.</summary>
    private const uint ShadowTextureUnit = GlShaders.ShadowTextureUnit;

    private const int VertexStride = 32;     // Vector3 posición + Vector3 normal + Vector2 UV
    private const int InstanceStride = 64;   // 4 × Vector4 (colocación, tipo y MATERIAL)

    /// <summary>Índice de bloque de uniformes: el mismo para todos los programas. El GLSL generado
    /// declara el bloque en ese índice con <c>layout(binding = 0)</c>.</summary>
    private const uint UniformBlockBindingIndex = 0;

    private nint _window;
    private nint _deviceContext;
    private nint _context;

    private uint _skyProgram;
    private uint _overlayProgram;
    private uint _geometryProgram;

    /// <summary>
    /// Programa de la PASADA DE SOMBRAS (ver <c>README-SOMBRAS.md</c>): el MISMO vertex shader de la
    /// geometría con un pixel shader VACÍO. Es lo más parecido que tiene OpenGL a las passadas sin
    /// etapa de fragmentos de Direct3D 12 y Vulkan —el FS tiene que existir para que el programa
    /// enlace— y es lo que hace que esta pasada no pague el sombreado de materiales por píxel.
    /// </summary>
    private uint _shadowProgram;

    // ---- Shadow map (ver README-SOMBRAS.md) ----
    private uint _shadowFramebuffer;
    private uint _shadowTexture;

    /// <summary>Mundo → espacio de luz de la pasada (ver
    /// <see cref="SceneEnvironmentConstants.LightViewProjectionFor"/>). En OpenGL va la versión
    /// CONVERTIDA a la convención de profundidad de esta API: el shader comparte la de Direct3D, pero
    /// el búfer de profundidad de OpenGL espera el otro rango (ver el método que la arma).</summary>
    private Matrix4x4 _lightViewProjection;

    /// <summary>False en una escena sin sombras o sin geometría de la que deducir el volumen.</summary>
    private bool _shadowEnabled;

    /// <summary>Qué mallas proyectan (ver <see cref="SceneDefinition.MeshCastsShadow"/>).</summary>
    private bool[] _castsShadow = Array.Empty<bool>();
    private uint _skyVertexArray;
    // Una entrada por MALLA de la escena: cada una tiene su VAO con su vertex buffer y su instance
    // buffer (el cañón usa terreno y cazas; el corredor, una sola malla de rocas).
    private uint[] _geometryVertexArrays = Array.Empty<uint>();
    private uint[] _vertexBuffers = Array.Empty<uint>();
    private uint[] _instanceBuffers = Array.Empty<uint>();
    private uint _uniformBuffer;
    private uint[] _queries = Array.Empty<uint>();

    /// <summary>Atlas de detalle de la escena (ver <see cref="Scenes.SceneDetailTexture"/>), unidad 1.</summary>
    private uint _detailTexture;

    /// <summary>Arreglo de materiales de la escena (ver <see cref="Scenes.MaterialAtlas"/>), unidad 2.
    /// Es UN arreglo para TODOS los materiales: el material de cada objeto es un índice que viaja por
    /// instancia, así que ningún dibujo cambia un binding.</summary>
    private uint _materialTexture;

    // ---- Franja de métricas DENTRO del frame (ver OverlayFrame) ----
    // Los píxeles van a un STORAGE BUFFER (un uint32 por píxel), que es lo que declara el shader:
    // el mismo StructuredBuffer<uint> que leen Direct3D 12 y Vulkan.
    private uint _overlayBuffer;
    private int _overlayPixelCount;
    private int _overlayWidth;
    private int _overlayHeight;
    private int _overlayX;
    private int _overlayY;
    private long _overlayVersion = -1;
    private byte[]? _overlayPending;
    private bool _overlayDirty;

    private SceneDefinition _scene = null!;

    /// <summary>Configuración gráfica de la corrida (ver SceneGraphicsOptions): el mismo objeto para
    /// los cuatro backends, porque las cuatro APIs tienen que dibujar exactamente lo mismo.</summary>
    private SceneGraphicsOptions _graphics = SceneGraphicsOptions.Default;
    private int _frameIndex;
    private int[] _meshVertexCounts = Array.Empty<int>();
    private int[] _meshInstanceCounts = Array.Empty<int>();
    private int _width;
    private int _height;
    private bool _disposed;
    private bool _hasSwapIntervalControl;
    private bool _vsync;

    public GraphicsApi Api => GraphicsApi.OpenGL;
    public string ApiName => "OpenGL";
    public string AdapterName { get; private set; } = "";
    public string AdapterDetail { get; private set; } = "";

    /// <summary>
    /// False: la pantalla completa exclusiva exigiría cambiar el modo de video del monitor. El host
    /// lo informa como aviso en vez de que el informe presente como exclusiva una corrida en ventana.
    /// </summary>
    public bool SupportsExclusiveFullscreen => false;

    public bool ExclusiveFullscreenAccepted { get; private set; } = true;

    /// <summary>OpenGL 4.3 tiene queries de tiempo por GPU; si el contexto no las da, se informa.</summary>
    public bool HasGpuTiming { get; private set; }

    public double LastGpuFrameMs { get; private set; }

    /// <summary>La franja se dibuja dentro del frame (ver <see cref="OverlayFrame"/>), con el mismo
    /// programa del fondo y blend premultiplicado: así no depende de que otra ventana se componga.</summary>
    public bool OverlayInFrame => true;

    /// <summary>Falso mientras no haya píxeles: sin franja no se dibuja el quad.</summary>
    private bool HasOverlay => _overlayWidth > 0 && _overlayHeight > 0;

    /// <summary>
    /// Deja la franja lista para los próximos frames (ver <see cref="OverlayFrame"/>). Se llama desde
    /// el hilo de render: acá solo se guardan los píxeles y, si cambió el tamaño del panel, se
    /// rehace el búfer; la subida real ocurre al dibujar (una vez por cambio, no por frame).
    /// </summary>
    public void SetOverlay(OverlayFrame? frame)
    {
        if (frame == null)
        {
            _overlayPending = null;
            _overlayDirty = true;
            _overlayWidth = 0;
            _overlayHeight = 0;
            return;
        }

        // El búfer se rehace SOLO si cambió el tamaño: el ancho de la franja se cuantiza y el alto
        // es fijo, así que en la práctica se crea una vez por corrida.
        if (frame.Width != _overlayWidth || frame.Height != _overlayHeight)
        {
            _overlayWidth = frame.Width;
            _overlayHeight = frame.Height;
            CreateOverlayBuffer(frame.Width * frame.Height);
        }
        _overlayX = frame.X;
        _overlayY = frame.Y;
        _overlayPending = frame.Pixels;
        _overlayVersion = frame.Version;
        _overlayDirty = true;
    }

    /// <summary>Siempre 0: en OpenGL la espera por la placa aparece en la entrega (ver <see cref="Present"/>).</summary>
    public double LastQueueWaitMs => 0;

    public bool DeviceLost { get; private set; }

    // =====================================================================
    // Sondeo
    // =====================================================================

    /// <summary>
    /// ¿Se puede usar OpenGL 4.3 en esta máquina? Se crea un contexto de verdad sobre una ventana
    /// oculta y se lee la versión: preguntar por el archivo alcanzaría para "hay opengl32.dll", pero
    /// no dice si el driver tiene el ICD instalado, que es de lo que depende que funcione.
    /// </summary>
    public static BackendAvailability Probe()
    {
        if (!GlApi.IsLibraryPresent)
        {
            return new BackendAvailability(GraphicsApi.OpenGL, "OpenGL", false,
                "No hay OpenGL en el sistema (falta opengl32.dll).");
        }

        nint window = 0;
        nint dc = 0;
        nint context = 0;
        try
        {
            // WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN: una ventana que nunca se ve y que puede
            // recibir un formato de píxeles como cualquier otra.
            const int style = unchecked((int)0x86000000);
            window = GlApi.CreateWindowEx(0, "STATIC", "WinForge OpenGL", style, 0, 0, 64, 64, 0, 0, 0, 0);
            if (window == 0)
            {
                return new BackendAvailability(GraphicsApi.OpenGL, "OpenGL", false,
                    "Windows no dejó crear una ventana de prueba para OpenGL.");
            }

            dc = GlApi.GetDC(window);
            if (dc == 0 || !SetPixelFormat(dc))
            {
                return new BackendAvailability(GraphicsApi.OpenGL, "OpenGL", false,
                    "Windows no pudo preparar el formato de píxeles de OpenGL.");
            }

            context = GlApi.wglCreateContext(dc);
            if (context == 0 || GlApi.wglMakeCurrent(dc, context) == 0)
            {
                return new BackendAvailability(GraphicsApi.OpenGL, "OpenGL", false,
                    "El driver de video no pudo crear un contexto de OpenGL: instalá los drivers de la placa.");
            }

            string? version = GlApi.QueryString(GlApi.GL_VERSION);
            string renderer = GlApi.QueryString(GlApi.GL_RENDERER) ?? "";
            if (!TryParseVersion(version, out int major, out int minor))
            {
                return new BackendAvailability(GraphicsApi.OpenGL, "OpenGL", false,
                    "El driver no reportó la versión de OpenGL.");
            }
            if (major * 10 + minor < 43)
            {
                return new BackendAvailability(GraphicsApi.OpenGL, "OpenGL", false,
                    $"El shader de la escena (GLSL 4.3, traducido del mismo SPIR-V que usa Vulkan) necesita OpenGL 4.3 o superior y la placa reporta {major}.{minor}: actualizá los drivers.");
            }

            return new BackendAvailability(GraphicsApi.OpenGL, "OpenGL", true, "", renderer);
        }
        catch (DllNotFoundException ex)
        {
            return new BackendAvailability(GraphicsApi.OpenGL, "OpenGL", false,
                $"No se pudo cargar la biblioteca de OpenGL: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new BackendAvailability(GraphicsApi.OpenGL, "OpenGL", false, ex.Message);
        }
        finally
        {
            try
            {
                if (context != 0)
                {
                    GlApi.wglMakeCurrent(0, 0);
                    GlApi.wglDeleteContext(context);
                }
                if (dc != 0 && window != 0) GlApi.ReleaseDC(window, dc);
                if (window != 0) GlApi.DestroyWindow(window);
            }
            catch { }
        }
    }

    /// <summary>Formato de píxeles de la ventana: 32 bits de color con 24 de profundidad, doble buffer.</summary>
    private static bool SetPixelFormat(nint dc)
    {
        var descriptor = new GlApi.PixelFormatDescriptor
        {
            nSize = (ushort)Marshal.SizeOf<GlApi.PixelFormatDescriptor>(),
            nVersion = 1,
            dwFlags = GlApi.PfdDrawToWindow | GlApi.PfdSupportOpenGl | GlApi.PfdDoubleBuffer,
            iPixelType = GlApi.PfdTypeRgba,
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8
        };

        int format = GlApi.ChoosePixelFormat(dc, ref descriptor);
        if (format == 0) return false;
        // Un formato de píxeles solo se puede fijar UNA vez por ventana: si ya estaba fijado (una
        // corrida anterior) la llamada falla y se sigue igual, porque es el mismo formato que pidió
        // este código. Lo que decide es que el contexto se pueda crear, y eso se comprueba después.
        GlApi.SetPixelFormat(dc, format, ref descriptor);
        return true;
    }

    private static bool TryParseVersion(string? text, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(' ', '.');
        return parts.Length >= 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor);
    }

    // =====================================================================
    // Inicialización
    // =====================================================================

    public void Initialize(BackendInitOptions options)
    {
        _scene = options.Scene;
        _graphics = options.Graphics;
        // La PRIMERA consulta al atlas de materiales es la que decodifica las imágenes y le asigna a
        // cada malla su ranura (ver MaterialAtlas.Build): tiene que pasar ANTES de CreateGeometryBuffers,
        // porque las instancias se hornean con esa ranura adentro.
        _ = _scene.Materials;
        _meshVertexCounts = new int[_scene.Meshes.Count];
        _meshInstanceCounts = new int[_scene.Meshes.Count];
        for (int mesh = 0; mesh < _scene.Meshes.Count; mesh++)
        {
            _meshVertexCounts[mesh] = _scene.Meshes[mesh].Vertices.Length;
            _meshInstanceCounts[mesh] = _scene.Meshes[mesh].Instances.Length;
        }
        _width = Math.Max(64, options.Width);
        _height = Math.Max(64, options.Height);
        _window = options.WindowHandle;

        _deviceContext = GlApi.GetDC(_window);
        if (_deviceContext == 0) throw new InvalidOperationException("Windows no entregó el contexto de dibujo de la ventana de la escena.");
        if (!SetPixelFormat(_deviceContext)) throw new InvalidOperationException("Windows no pudo preparar el formato de píxeles de OpenGL para la escena.");

        // 1) Contexto temporal con la API vieja: es el único que sabe resolver las funciones ARB
        //    (la dirección de wglCreateContextAttribsARB no existe hasta que hay un contexto actual).
        nint legacy = GlApi.wglCreateContext(_deviceContext);
        if (legacy == 0 || GlApi.wglMakeCurrent(_deviceContext, legacy) == 0)
        {
            throw new InvalidOperationException("El driver de video no pudo crear un contexto de OpenGL para la escena.");
        }
        GlApi.ResolveWglExtensions();

        _context = CreateCoreContext(_deviceContext);
        GlApi.wglMakeCurrent(_deviceContext, _context);

        // 2) Con el contexto core actual, WGL ya puede resolver las funciones modernas.
        if (!GlApi.Load())
        {
            throw new InvalidOperationException(
                "El driver no expone las funciones de OpenGL que necesita la escena (VAOs, instancing y queries de tiempo).");
        }

        // El contexto temporal ya no hace falta (el core queda actual).
        GlApi.wglDeleteContext(legacy);

        string? version = GlApi.QueryString(GlApi.GL_VERSION);
        string? renderer = GlApi.QueryString(GlApi.GL_RENDERER);
        string? vendor = GlApi.QueryString(GlApi.GL_VENDOR);
        string? extensions = GlApi.QueryString(GlApi.GL_EXTENSIONS);
        if (!TryParseVersion(version, out int major, out int minor) || major * 10 + minor < 43)
        {
            throw new InvalidOperationException(
                $"La placa no dio un contexto de OpenGL 4.3 o superior (reporta '{version}'): el shader de la " +
                "escena es GLSL 4.3, traducido del mismo SPIR-V que usa Vulkan (usa el binding por layout() " +
                "del bloque de constantes y un storage buffer para la franja de métricas).");
        }

        AdapterName = renderer ?? "OpenGL";
        // Las queries de tiempo son núcleo desde 3.3, así que con el contexto que se acaba de exigir
        // (4.3) siempre están: si el driver no responde a la query, el informe lo dice por frame.
        HasGpuTiming = true;

        // El sincronismo del monitor hay que apagarlo para medir: con él encendido el FPS queda
        // topeado por el monitor y se mediría al monitor, no a la placa (misma razón por la que
        // Direct3D pide AllowTearing). El pedido puede venir con vsync (presentación de escritorio),
        // así que se le pide al driver exactamente lo que pide la corrida.
        _vsync = options.VSync;
        _hasSwapIntervalControl = GlApi.wglSwapIntervalEXT != null && GlApi.wglSwapIntervalEXT(_vsync ? 1 : 0) != 0;

        string vram = "";
        if (GlApi.HasExtension(extensions, "GL_NVX_gpu_memory_info"))
        {
            uint totalKb = GlApi.GetInteger(GlApi.GL_GPU_MEMORY_INFO_TOTAL_AVAILABLE_MEMORY_NVX);
            if (totalKb > 0) vram = $" · {totalKb / 1024} MB de VRAM";
        }

        AdapterDetail = $"OpenGL {version} · GLSL {GlApi.QueryString(GlApi.GL_SHADING_LANGUAGE_VERSION)} · {vendor}{vram}" +
                        (_hasSwapIntervalControl
                            ? (_vsync ? " · presentación sincronizada con el monitor" : " · presentación sin sincronismo")
                            : " · el driver no expone wglSwapIntervalEXT: queda el sincronismo que fija el driver");

        CreatePrograms();
        CreateGeometryBuffers();
        CreateUniformBuffer();
        CreateOverlayBuffer(0);
        CreateDetailTexture();
        // Arreglo de materiales de la escena (ver Scenes.MaterialAtlas).
        CreateMaterialTexture();
        CreateQueries();
        // Shadow map (ver README-SOMBRAS.md): la textura de profundidad, su framebuffer y su unidad de
        // textura (la que declara el GLSL generado, ver GlShaders.ShadowTextureUnit).
        CreateShadowResources();
        ApplyViewport();

        // La escena arranca con el estado correcto y no lo cambia nunca más (no hay estados por
        // objeto): profundidad activa para la geometría, sin descarte de caras y sin mezcla.
        GlApi.glDepthFunc(GlApi.GL_LESS);
        GlApi.glDisable(GlApi.GL_CULL_FACE);
    }

    /// <summary>
    /// Pide un contexto CORE 4.3. Si el driver no lo da, prueba con las versiones siguientes (un
    /// driver que soporta 4.6 puede no aceptar el pedido exacto de 4.3) y, si ninguna, cae al
    /// heredado ya creado: es más útil intentar dibujar con lo que haya y fallar después con un
    /// mensaje que explique la versión, que no arrancar.
    /// </summary>
    private static nint CreateCoreContext(nint dc)
    {
        if (GlApi.wglCreateContextAttribsARB == null)
        {
            // Sin ARB no hay forma de pedir un perfil: se usa el contexto heredado, que en la
            // mayoría de los drivers reporta una versión moderna igual.
            return GlApi.wglCreateContext(dc);
        }

        const int majorAttribute = 0x2091;   // WGL_CONTEXT_MAJOR_VERSION_ARB
        const int minorAttribute = 0x2092;   // WGL_CONTEXT_MINOR_VERSION_ARB
        const int profileMaskAttribute = 0x9126;   // WGL_CONTEXT_PROFILE_MASK_ARB
        const int coreProfile = 0x00000001;        // WGL_CONTEXT_CORE_PROFILE_BIT_ARB

        // El stackalloc va FUERA del bucle: adentro se reservaría en cada vuelta y la pila crecería
        // sin liberarse hasta salir del método.
        int* attributes = stackalloc int[7];
        attributes[4] = profileMaskAttribute;
        attributes[5] = coreProfile;
        attributes[6] = 0;

        foreach (var (major, minor) in new[] { (4, 3), (4, 4), (4, 5), (4, 6) })
        {
            attributes[0] = majorAttribute;
            attributes[1] = major;
            attributes[2] = minorAttribute;
            attributes[3] = minor;

            nint context = GlApi.wglCreateContextAttribsARB(dc, 0, attributes);
            if (context != 0) return context;
        }

        return GlApi.wglCreateContext(dc);
    }

    private void CreatePrograms()
    {
        // El GLSL de las cuatro etapas sale del MISMO SPIR-V que usa Vulkan, traducido con
        // spirv-cross en el build (ver GlShaders.g.cs y tools/SpirvGen): acá no hay ningún shader
        // escrito a mano. Cada etapa declara sus propias in/out, así que se compilan tal cual, sin
        // encadenar el fuente del vertex shader delante del pixel shader.
        _skyProgram = CreateProgram(GlShaders.SkyVertex, GlShaders.SkyPixel, "el fondo");
        // La franja: el MISMO pixel shader que el fondo, con su vertex shader (el quad se arma
        // desde el vértice 0). Es un programa aparte porque en OpenGL el vertex shader va atado al
        // programa: no hay forma de cambiar solo el VS sin cambiar de programa.
        _overlayProgram = CreateProgram(GlShaders.SkyOverlayVertex, GlShaders.SkyPixel, "la franja de métricas");
        _geometryProgram = CreateProgram(GlShaders.GeometryVertex, GlShaders.GeometryPixel, "la geometría");
        // Pasada de sombras: el MISMO vertex shader (con la matriz del sol en las constantes dibuja la
        // escena desde la luz) y un pixel shader vacío: la profundidad la escriben las etapas fijas del
        // pipeline, así que el FS no tiene nada que hacer y no paga el sombreado de materiales.
        _shadowProgram = CreateProgram(GlShaders.GeometryVertex, GlShaders.EmptyPixelShader, "la pasada de sombras");

        // El bloque de constantes y las unidades de textura NO se atan acá: los cuatro programas ya
        // traen del GLSL el índice del bloque (layout(binding = 0)) y la unidad de cada sampler
        // (layout(binding = N)), que es lo que hace que no haya ningún nombre que buscar ni ningún
        // número que se pueda desincronizar. En cada frame alcanza con dejar las texturas y el búfer
        // en esas unidades (ver BindSceneTextures y CreateOverlayBuffer).
        BindUniformBlocks(_skyProgram);
        BindUniformBlocks(_overlayProgram);
        BindUniformBlocks(_geometryProgram);
        BindUniformBlocks(_shadowProgram);
    }

    /// <summary>
    /// Corrige el SIGNO de la derivada en Y del GLSL traducido. En OpenGL el origen de la ventana está
    /// abajo y la Y crece hacia ARRIBA; en Direct3D y Vulkan está arriba y la Y crece hacia ABAJO. La
    /// misma variable, entonces, deriva con el signo cambiado: el <c>dFdy</c> que spirv-cross emite para
    /// el <c>OpDPdy</c> del SPIR-V apunta al revés que el <c>ddy</c> de HLSL.
    ///
    /// Importa porque la escena arma el marco TANGENTE por derivadas (el marco de Mikkelsen de
    /// <c>ShadeTextured</c>, ver <see cref="SceneShaders"/>): con la bitangente invertida el mapa de
    /// normales perturba la normal al revés. Esa era la diferencia real de OpenGL con las otras tres
    /// APIs —la calle salía plana y más clara—, y costó encontrarla justamente porque no es un error de
    /// estado ni de datos: el atlas de materiales, las UVs y las normales de vértice daban idénticos en
    /// las cuatro APIs, y lo único distinto era el marco.
    ///
    /// Se renombran las LLAMADAS a <c>GlFdy</c> y el preámbulo define esa función negando el resultado.
    /// El renombre va ANTES de escribir el preámbulo, así las definiciones —que llaman al <c>dFdy</c> de
    /// verdad— no se renombran a sí mismas. Va acá y no en el generador porque este archivo es el dueño
    /// de lo que compila: el texto de <c>GlShaders.g.cs</c> se consume tal cual sale de spirv-cross, y la
    /// convención de ventana es una decisión de ESTE backend.
    ///
    /// <c>dFdx</c> no se toca: en las dos convenciones la X crece hacia la derecha.
    /// </summary>
    private static string GlslDerivativeY(string glsl)
    {
        if (!glsl.Contains("dFdy(", StringComparison.Ordinal)) return glsl;

        string renamed = glsl.Replace("dFdy(", "GlFdy(", StringComparison.Ordinal);
        const string preamble = """
// ---- Derivada en Y con el signo de Direct3D y Vulkan (ver OpenGLBackend.GlslDerivativeY) ----
// En OpenGL la Y de ventana crece hacia ARRIBA; en las otras APIs, hacia abajo. Negarla deja el
// mismo número que ve el HLSL, que es lo que el marco tangente por derivadas da por sentado.
float GlFdy(float value) { return -dFdy(value); }
vec2  GlFdy(vec2 value)  { return -dFdy(value); }
vec3  GlFdy(vec3 value)  { return -dFdy(value); }
vec4  GlFdy(vec4 value)  { return -dFdy(value); }
""";

        // El preámbulo va DESPUÉS de las directivas: un #extension tiene que aparecer antes que
        // cualquier otra cosa, y una definición de función no es una excepción.
        int insert = 0;
        foreach (var line in renamed.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('#')) break;
            insert += line.Length + 1;
        }
        return renamed.Insert(insert, preamble + "\n");
    }

    /// <summary>
    /// El shadow map en OpenGL: una textura de profundidad <c>GL_DEPTH_COMPONENT24</c> de 2048² atada a
    /// un framebuffer propio, con el COMPARADOR del hardware encendido
    /// (<c>GL_COMPARE_REF_TO_TEXTURE</c> + <c>GL_LESS</c>), que es lo que hace que el muestreo del shader
    /// devuelva 0/1 ya filtrado — el mismo comportamiento que el sampler de comparación de las otras tres
    /// APIs.
    ///
    /// Tres detalles que no son opcionales y que, si faltan, no dan un error sino una escena negra o sin
    /// sombras:
    /// <list type="number">
    /// <item><c>glDrawBuffer(GL_NONE)</c> y <c>glReadBuffer(GL_NONE)</c>: el mapa no tiene NINGÚN adjunto
    /// de color. Con el draw buffer por defecto (que apunta a <c>COLOR_ATTACHMENT0</c>) el framebuffer
    /// queda INCOMPLETO y todos los dibujos de la pasada fallan con <c>GL_INVALID_FRAMEBUFFER_OPERATION</c>.</item>
    /// <item>El filtro LINEAL: el ablandado del borde lo hace el hardware al comparar, promediando 4
    /// téxeles por muestra; con filtro NEAREST la sombra sale con escalones de 1 téxel.</item>
    /// <item><c>GL_CLAMP_TO_EDGE</c>: el mapa no se repite. Si el filtrado llegara al borde, repetir
    /// traería la profundidad del lado opuesto del mundo.</item>
    /// </list>
    /// </summary>
    private void CreateShadowResources()
    {
        // Si al driver le falta alguna de las funciones del framebuffer, la escena se corre SIN sombras
        // (que es como estaba hasta ahora) en vez de dibujarlas contra el framebuffer de la ventana: con
        // el framebuffer 0 el mapa no existe y la pasada escribiría su profundidad ENCIMA de la escena.
        bool available = GlApi.glGenFramebuffers != null && GlApi.glFramebufferTexture2D != null &&
                         GlApi.glCheckFramebufferStatus != null && GlApi.glDrawBuffer != null && GlApi.glReadBuffer != null;
        if (!available) return;

        // El volumen de sombra sale de la GEOMETRÍA de la escena (ver ResolveShadowVolume).
        // La pasada de sombras se corre solo si el usuario no la apagó Y la escena tiene algo que
        // proyectar: apagada desde la configuración, no hay mapa que escribir ni que muestrear.
        _shadowEnabled = _graphics.Shadows && _scene.ShadowStrength > 0f && _scene.ResolveShadowVolume() != null;
        // La versión CONVERTIDA a la convención de profundidad de OpenGL: el búfer de esta API guarda
        // (z_clip * 0.5 + 0.5), así que el shader de profundidad necesita z_clip = 2·z − 1 para escribir
        // EXACTAMENTE el valor que el shader de sombreado va a comparar (ver LightViewProjectionFor).
        _lightViewProjection = SceneEnvironmentConstants.LightViewProjectionFor(
            _scene, SceneEnvironmentConstants.ShadowMapAxis.OpenGl);

        _castsShadow = new bool[_scene.Meshes.Count];
        for (int mesh = 0; mesh < _castsShadow.Length; mesh++)
        {
            _castsShadow[mesh] = _scene.MeshCastsShadow(_scene.Meshes[mesh]);
        }

        int size = _graphics.ShadowMapSize;
        unsafe
        {
            uint texture = 0;
            GlApi.glGenTextures(1, &texture);
            _shadowTexture = texture;
            GlApi.glBindTexture(GlApi.GL_TEXTURE_2D, _shadowTexture);
            GlApi.glTexImage2D(GlApi.GL_TEXTURE_2D, 0, (int)GlApi.GL_DEPTH_COMPONENT24, size, size, 0,
                GlApi.GL_DEPTH_COMPONENT, GlApi.GL_FLOAT, null);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_MIN_FILTER, (int)GlApi.GL_LINEAR);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_MAG_FILTER, (int)GlApi.GL_LINEAR);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_WRAP_S, (int)GlApi.GL_CLAMP_TO_EDGE);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_WRAP_T, (int)GlApi.GL_CLAMP_TO_EDGE);
            // El comparador: sin GL_COMPARE_REF_TO_TEXTURE la textura se muestrea como profundidad cruda y
            // sampler2DShadow no la acepta (el muestreo devolvería 0 y la escena quedaría toda en sombra).
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_COMPARE_MODE, (int)GlApi.GL_COMPARE_REF_TO_TEXTURE);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_COMPARE_FUNC, (int)GlApi.GL_LESS);
            GlApi.glBindTexture(GlApi.GL_TEXTURE_2D, 0);

            uint framebuffer = 0;
            GlApi.glGenFramebuffers(1, &framebuffer);
            _shadowFramebuffer = framebuffer;
            GlApi.glBindFramebuffer(GlApi.GL_FRAMEBUFFER, _shadowFramebuffer);
            GlApi.glFramebufferTexture2D(GlApi.GL_FRAMEBUFFER, GlApi.GL_DEPTH_ATTACHMENT, GlApi.GL_TEXTURE_2D, _shadowTexture, 0);
            GlApi.glDrawBuffer(GlApi.GL_NONE);
            GlApi.glReadBuffer(GlApi.GL_NONE);

            // Un framebuffer incompleto no da un error en el momento: da dibujos descartados (la escena
            // se ve SIN sombras, como si la escena no las hubiera pedido). Mejor decirlo acá.
            uint status = GlApi.glCheckFramebufferStatus(GlApi.GL_FRAMEBUFFER);
            GlApi.glBindFramebuffer(GlApi.GL_FRAMEBUFFER, 0);
            if (status != GlApi.GL_FRAMEBUFFER_COMPLETE)
            {
                throw new InvalidOperationException(
                    $"OpenGL no pudo armar el framebuffer del shadow map (estado 0x{status:X4}).");
            }
        }
    }

    /// <summary>
    /// Arreglo de texturas de MATERIAL de la escena (ver <see cref="Scenes.MaterialAtlas"/>): un arreglo
    /// 2D con tres rebanadas por material (albedo, normales y ARM) y todos sus mips, que los genera el
    /// CPU. En OpenGL los mips de un arreglo se suben nivel por nivel con <c>glTexImage3D</c> —una
    /// rebanada por llamada, con la profundidad en 1— y el filtrado los interpola solo.
    /// </summary>
    private void CreateMaterialTexture()
    {
        if (GlApi.glGenTextures == null || GlApi.glTexImage3D == null || GlApi.glTexParameteri == null) return;

        var atlas = _scene.Materials;

        // El atlas guarda los mips de cada rebanada CONTIGUOS (rebanada mayor), pero glTexImage3D
        // sube un NIVEL completo de todas las rebanadas de una vez: la profundidad es la cantidad de
        // capas y no hay ningún "desde qué capa". Por eso los píxeles se reacomodan a nivel mayor —
        // sin relleno, que OpenGL no lo necesita— antes de subirlos, y cada nivel sale en UNA llamada.
        // Subir rebanada por rebanada con profundidad 1 dejaría todas las capas menos la primera sin
        // datos: la textura queda INCOMPLETA y el muestreo devuelve negro (sin ningún error).
        var staging = new byte[atlas.Pixels.Length];
        int offset = 0;
        for (int level = 0; level < atlas.MipCount; level++)
        {
            int side = atlas.MipSize(level);
            int levelBytes = side * side * 4;
            for (int slice = 0; slice < atlas.SliceCount; slice++)
            {
                int source = slice * atlas.SliceBytes + atlas.MipOffsets[level];
                Array.Copy(atlas.Pixels, source, staging, offset, levelBytes);
                offset += levelBytes;
            }
        }

        unsafe
        {
            uint texture = 0;
            GlApi.glGenTextures(1, &texture);
            _materialTexture = texture;

            GlApi.glActiveTexture(GlApi.GL_TEXTURE0 + MaterialTextureUnit);
            GlApi.glBindTexture(GlApi.GL_TEXTURE_2D_ARRAY, _materialTexture);
            int written = 0;
            for (int level = 0; level < atlas.MipCount; level++)
            {
                int side = atlas.MipSize(level);
                fixed (byte* data = &staging[written])
                {
                    GlApi.glTexImage3D(
                        GlApi.GL_TEXTURE_2D_ARRAY, level, (int)GlApi.GL_RGBA8,
                        side, side, atlas.SliceCount, 0,
                        GlApi.GL_RGBA, GlApi.GL_UNSIGNED_BYTE, data);
                }
                written += side * side * 4 * atlas.SliceCount;
            }
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D_ARRAY, GlApi.GL_TEXTURE_MIN_FILTER, (int)GlApi.GL_LINEAR_MIPMAP_LINEAR);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D_ARRAY, GlApi.GL_TEXTURE_MAG_FILTER, (int)GlApi.GL_LINEAR);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D_ARRAY, GlApi.GL_TEXTURE_WRAP_S, (int)GlApi.GL_REPEAT);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D_ARRAY, GlApi.GL_TEXTURE_WRAP_T, (int)GlApi.GL_REPEAT);
            // El wrap de la R no importa (se muestrea una rebanada entera), pero dejarlo en repeat es
            // el estado por defecto y no cuesta nada.
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D_ARRAY, GlApi.GL_TEXTURE_WRAP_R, (int)GlApi.GL_REPEAT);
            GlApi.glBindTexture(GlApi.GL_TEXTURE_2D_ARRAY, 0);
            GlApi.glActiveTexture(GlApi.GL_TEXTURE0);
        }
    }

    /// <summary>
    /// Textura de detalle de la escena (ver <see cref="Scenes.SceneDetailTexture"/>): un atlas de
    /// 256×256 en RGBA8 que se sube UNA vez al cargar. Sin mipmaps a propósito —el shader pide el
    /// nivel 0 explícito— y con wrap en las dos direcciones: la celda se repite sobre el mundo.
    /// </summary>
    private void CreateDetailTexture()
    {
        if (GlApi.glGenTextures == null || GlApi.glTexImage2D == null || GlApi.glTexParameteri == null) return;

        var pixels = Scenes.SceneDetailTexture.Pixels;
        unsafe
        {
            uint texture = 0;
            GlApi.glGenTextures(1, &texture);
            _detailTexture = texture;

            GlApi.glActiveTexture(GlApi.GL_TEXTURE0 + DetailTextureUnit);
            GlApi.glBindTexture(GlApi.GL_TEXTURE_2D, _detailTexture);
            fixed (byte* data = pixels)
            {
                GlApi.glTexImage2D(GlApi.GL_TEXTURE_2D, 0, (int)GlApi.GL_RGBA8,
                    Scenes.SceneDetailTexture.Size, Scenes.SceneDetailTexture.Size, 0,
                    GlApi.GL_RGBA, GlApi.GL_UNSIGNED_BYTE, data);
            }
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_MIN_FILTER, (int)GlApi.GL_LINEAR);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_MAG_FILTER, (int)GlApi.GL_LINEAR);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_WRAP_S, (int)GlApi.GL_REPEAT);
            GlApi.glTexParameteri(GlApi.GL_TEXTURE_2D, GlApi.GL_TEXTURE_WRAP_T, (int)GlApi.GL_REPEAT);
            GlApi.glBindTexture(GlApi.GL_TEXTURE_2D, 0);
            GlApi.glActiveTexture(GlApi.GL_TEXTURE0);
        }
    }

    /// <summary>
    /// Búfer de la franja: un uint32 por píxel (RGBA8 empaquetado), que es EXACTAMENTE lo que leen
    /// Direct3D y Vulkan del mismo <c>StructuredBuffer</c>: el shader es el mismo, así que los datos
    /// también. Se crea (o se re-especifica) cuando cambia el tamaño del panel; el contenido lo sube
    /// <see cref="UpdateOverlayTexture"/>, que es lo que pasa 4 veces por segundo.
    /// </summary>
    private void CreateOverlayBuffer(int pixelCount)
    {
        _overlayPixelCount = Math.Max(1, pixelCount);
        if (GlApi.glGenBuffers == null || GlApi.glBindBufferBase == null) return;

        if (_overlayBuffer == 0)
        {
            unsafe
            {
                uint buffer = 0;
                GlApi.glGenBuffers(1, &buffer);
                _overlayBuffer = buffer;
            }
        }

        GlApi.glBindBuffer(GlApi.GL_SHADER_STORAGE_BUFFER, _overlayBuffer);
        GlApi.glBufferData(GlApi.GL_SHADER_STORAGE_BUFFER, _overlayPixelCount * 4, null, GlApi.GL_DYNAMIC_DRAW);
        // El punto de enlace sale del GLSL (ver GlShaders.OverlayBufferBinding) y es estado del
        // CONTEXTO, no de un programa: se ata una vez acá y queda atado toda la corrida. Lo necesita
        // también el fondo, cuyo pixel shader declara el mismo búfer (es el que dibuja la franja
        // dentro del frame).
        GlApi.glBindBufferBase(GlApi.GL_SHADER_STORAGE_BUFFER, GlShaders.OverlayBufferBinding, _overlayBuffer);
        GlApi.glBindBuffer(GlApi.GL_SHADER_STORAGE_BUFFER, 0);
        _overlayDirty = true;
    }

    /// <summary>Copia los píxeles pendientes al búfer (una vez por cambio, no por frame).</summary>
    private void UpdateOverlayTexture()
    {
        if (!_overlayDirty || _overlayPending == null || _overlayBuffer == 0) return;
        _overlayDirty = false;

        // glBufferData y no una escritura parcial: el bloque anterior queda huérfano y el driver
        // puede seguir leyéndolo mientras la placa dibuja el frame anterior (mismo criterio que las
        // constantes y que el Map con descarte de Direct3D 11).
        GlApi.glBindBuffer(GlApi.GL_SHADER_STORAGE_BUFFER, _overlayBuffer);
        unsafe
        {
            fixed (byte* source = _overlayPending)
            {
                GlApi.glBufferData(GlApi.GL_SHADER_STORAGE_BUFFER, _overlayPixelCount * 4, source, GlApi.GL_DYNAMIC_DRAW);
            }
        }
        GlApi.glBindBuffer(GlApi.GL_SHADER_STORAGE_BUFFER, 0);
    }

    /// <summary>
    /// Franja de métricas DENTRO del frame (ver <see cref="OverlayFrame"/>): última pasada, sobre la
    /// escena, con el MISMO programa del fondo —el vertex shader distingue la franja por
    /// gl_VertexID— y el blend premultiplicado. En OpenGL el blend es estado dinámico, así que no
    /// hace falta un programa aparte (a diferencia de Vulkan y Direct3D 12, donde es del pipeline).
    /// </summary>
    private void DrawOverlay()
    {
        UpdateOverlayTexture();

        GlApi.glDisable(GlApi.GL_DEPTH_TEST);
        GlApi.glDepthMask(0);
        GlApi.glEnable(GlApi.GL_BLEND);
        GlApi.glBlendFunc(GlApi.GL_ONE, GlApi.GL_ONE_MINUS_SRC_ALPHA);
        GlApi.glUseProgram(_overlayProgram);
        GlApi.glBindVertexArray(_skyVertexArray);
        // Los píxeles no se atan acá: el búfer ya está en su punto de enlace desde que se creó
        // (ver CreateOverlayBuffer) y ese enlace es del contexto, no del programa.
        // 6 vértices desde el 0: el quad lo arma el vertex shader de la franja, igual que en
        // Direct3D y Vulkan (ver GlShaders.SkyOverlayVertex).
        GlApi.glDrawArrays(GlApi.GL_TRIANGLES, 0, 6);
        GlApi.glDisable(GlApi.GL_BLEND);
        GlApi.glDepthMask(1);
        GlApi.glEnable(GlApi.GL_DEPTH_TEST);
    }

    /// <summary>
    /// Ata las texturas de escena a sus unidades ANTES de dibujar. Las tres se desataron al subirlas
    /// (nadie tiene que poder muestrearlas a medio subir), pero en OpenGL el binding de unidad es
    /// estado del contexto y nadie lo repone: con la unidad vacía el muestreo no da un error, da
    /// negro, y la escena queda a oscuras por más que la geometría, las luces y el cbuffer estén
    /// bien. Es el único punto donde las cuatro APIs difieren de verdad —Direct3D y Vulkan atan sus
    /// descriptores al grabar el frame—, así que el re-ataje va acá, una vez por frame y para los
    /// tres programas: el fondo también muestrea el atlas de detalle. Las unidades son las que
    /// declara el GLSL generado (ver <see cref="GlShaders"/>), no una elección de este archivo.
    /// </summary>
    private void BindSceneTextures()
    {
        GlApi.glActiveTexture(GlApi.GL_TEXTURE0 + DetailTextureUnit);
        GlApi.glBindTexture(GlApi.GL_TEXTURE_2D, _detailTexture);
        GlApi.glActiveTexture(GlApi.GL_TEXTURE0 + MaterialTextureUnit);
        GlApi.glBindTexture(GlApi.GL_TEXTURE_2D_ARRAY, _materialTexture);
        // El shadow map: el shader lo usa para la sombra del sol.
        GlApi.glActiveTexture(GlApi.GL_TEXTURE0 + ShadowTextureUnit);
        GlApi.glBindTexture(GlApi.GL_TEXTURE_2D, _shadowTexture);
        GlApi.glActiveTexture(GlApi.GL_TEXTURE0);
    }

    /// <summary>
    /// Ata el bloque de constantes por frame al índice 0 en un programa. Se recorren los bloques
    /// ACTIVOS en vez de buscarlo por nombre: el nombre del bloque en el GLSL generado no es
    /// <c>FrameConstants</c> sino el que le pone dxc al tipo (<c>type_FrameConstants</c>), y un
    /// nombre que no coincide no da error —deja el bloque sin atar y la escena en negro—. Los cuatro
    /// programas declaran un solo bloque, así que atarlos todos al 0 es exactamente lo que se quiere.
    /// </summary>
    private static void BindUniformBlocks(uint program)
    {
        int count = 0;
        GlApi.glGetProgramiv(program, GlApi.GL_ACTIVE_UNIFORM_BLOCKS, &count);
        for (int block = 0; block < count; block++)
        {
            GlApi.glUniformBlockBinding(program, (uint)block, UniformBlockBindingIndex);
        }
    }

    private uint CreateProgram(string vertexSource, string pixelSource, string what)
    {
        vertexSource = GlslDerivativeY(vertexSource);
        pixelSource = GlslDerivativeY(pixelSource);
        uint vertex = CompileShader(GlApi.GL_VERTEX_SHADER, vertexSource, $"{what} (vertex)");
        uint pixel = CompileShader(GlApi.GL_FRAGMENT_SHADER, pixelSource, $"{what} (fragment)");
        try
        {
            uint program = GlApi.glCreateProgram();
            GlApi.glAttachShader(program, vertex);
            GlApi.glAttachShader(program, pixel);
            GlApi.glLinkProgram(program);

            int status = 0;
            GlApi.glGetProgramiv(program, GlApi.GL_LINK_STATUS, &status);
            if (status == 0)
            {
                string log = ReadProgramLog(program);
                GlApi.glDeleteProgram(program);
                throw new InvalidOperationException($"No se pudo enlazar el programa de {what}: {log}");
            }
            return program;
        }
        finally
        {
            GlApi.glDeleteShader(vertex);
            GlApi.glDeleteShader(pixel);
        }
    }

    /// <summary>
    /// Compila un shader. Si falla, el error viaja ENTERO con el texto del compilador y la línea:
    /// un shader que no compila sin su línea no se arregla.
    /// </summary>
    private static uint CompileShader(uint type, string source, string what)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        uint shader = GlApi.glCreateShader(type);
        fixed (byte* text = bytes)
        {
            nint* strings = stackalloc nint[1];
            strings[0] = (nint)text;
            int* lengths = stackalloc int[1];
            lengths[0] = bytes.Length;
            GlApi.glShaderSource(shader, 1, strings, lengths);
        }
        GlApi.glCompileShader(shader);

        int status = 0;
        GlApi.glGetShaderiv(shader, GlApi.GL_COMPILE_STATUS, &status);
        if (status == 0)
        {
            string log = ReadShaderLog(shader);
            GlApi.glDeleteShader(shader);
            throw new InvalidOperationException($"No se pudo compilar el shader de {what}: {log}");
        }
        return shader;
    }

    private static string ReadShaderLog(uint shader)
    {
        int length = 0;
        GlApi.glGetShaderiv(shader, GlApi.GL_INFO_LOG_LENGTH, &length);
        if (length <= 0) return "(el compilador no dio detalle)";
        byte[] log = new byte[length];
        fixed (byte* pointer = log)
        {
            int written = 0;
            GlApi.glGetShaderInfoLog(shader, log.Length, &written, pointer);
        }
        return Clean(log);
    }

    private static string ReadProgramLog(uint program)
    {
        int length = 0;
        GlApi.glGetProgramiv(program, GlApi.GL_INFO_LOG_LENGTH, &length);
        if (length <= 0) return "(el enlazador no dio detalle)";
        byte[] log = new byte[length];
        fixed (byte* pointer = log)
        {
            int written = 0;
            GlApi.glGetProgramInfoLog(program, log.Length, &written, pointer);
        }
        return Clean(log);
    }

    /// <summary>El log viene con terminador nulo: se corta ahí y se limpian los espacios.</summary>
    private static string Clean(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end).Trim();
    }

    private void CreateGeometryBuffers()
    {
        // El fondo no tiene atributos: el triángulo sale de gl_VertexID. En perfil core hay que
        // usar un VAO igual, porque dibujar con el VAO 0 (sin VAO) es un error.
        uint arrays = 0;
        GlApi.glGenVertexArrays(1, &arrays);
        _skyVertexArray = arrays;

        int meshCount = _scene.Meshes.Count;
        _geometryVertexArrays = new uint[meshCount];
        _vertexBuffers = new uint[meshCount];
        _instanceBuffers = new uint[meshCount];

        // Un solo stackalloc para todas las vueltas: el puntero se reusa como salida de glGenBuffers.
        uint* buffers = stackalloc uint[2];

        for (int mesh = 0; mesh < meshCount; mesh++)
        {
            var geometry = _scene.Meshes[mesh];

            GlApi.glGenVertexArrays(1, &arrays);
            _geometryVertexArrays[mesh] = arrays;

            GlApi.glGenBuffers(2, buffers);
            _vertexBuffers[mesh] = buffers[0];
            _instanceBuffers[mesh] = buffers[1];

            GlApi.glBindVertexArray(_geometryVertexArrays[mesh]);

            fixed (SceneVertex* vertexPointer = geometry.Vertices)
            {
                GlApi.glBindBuffer(GlApi.GL_ARRAY_BUFFER, _vertexBuffers[mesh]);
                GlApi.glBufferData(GlApi.GL_ARRAY_BUFFER, geometry.Vertices.Length * VertexStride, vertexPointer, GlApi.GL_STATIC_DRAW);
            }
            // RenderInstances y no Instances: el material de la malla ya está horneado en cada
            // instancia (tinta, fuerzas y ranura del atlas). Ver SceneMesh.RenderInstances.
            var instances = geometry.RenderInstances;
            fixed (SceneInstance* instancePointer = instances)
            {
                GlApi.glBindBuffer(GlApi.GL_ARRAY_BUFFER, _instanceBuffers[mesh]);
                GlApi.glBufferData(GlApi.GL_ARRAY_BUFFER, instances.Length * InstanceStride, instancePointer, GlApi.GL_STATIC_DRAW);
            }

            // Las locations las declara el vertex shader (ver GlShaders.g.cs): posición, normal y UV
            // por vértice, y los cuatro vectores por instancia (con divisor 1, que es lo que hace que
            // avancen por instancia). Los atributos son estado del VAO, así que se configuran una vez
            // por malla. Los números NO se escriben acá: son los mismos del SPIR-V de Vulkan, y si el
            // HLSL reordena sus entradas se mueven al regenerar — el VAO tiene que seguir a quien las
            // lee, no a un 0..6 a mano.
            GlApi.glBindBuffer(GlApi.GL_ARRAY_BUFFER, _vertexBuffers[mesh]);
            GlApi.glEnableVertexAttribArray(GlShaders.AttributePosition);
            GlApi.glVertexAttribPointer(GlShaders.AttributePosition, 3, GlApi.GL_FLOAT, 0, VertexStride, (void*)0);
            GlApi.glEnableVertexAttribArray(GlShaders.AttributeNormal);
            GlApi.glVertexAttribPointer(GlShaders.AttributeNormal, 3, GlApi.GL_FLOAT, 0, VertexStride, (void*)12);
            // La UV del vértice. Los tipos procedurales la llevan en cero y se texturan por posición
            // con el atlas de detalle.
            GlApi.glEnableVertexAttribArray(GlShaders.AttributeTexCoord);
            GlApi.glVertexAttribPointer(GlShaders.AttributeTexCoord, 2, GlApi.GL_FLOAT, 0, VertexStride, (void*)24);

            GlApi.glBindBuffer(GlApi.GL_ARRAY_BUFFER, _instanceBuffers[mesh]);
            GlApi.glEnableVertexAttribArray(GlShaders.AttributeInstance0);
            GlApi.glVertexAttribPointer(GlShaders.AttributeInstance0, 4, GlApi.GL_FLOAT, 0, InstanceStride, (void*)0);
            GlApi.glVertexAttribDivisor(GlShaders.AttributeInstance0, 1);
            GlApi.glEnableVertexAttribArray(GlShaders.AttributeInstance1);
            GlApi.glVertexAttribPointer(GlShaders.AttributeInstance1, 4, GlApi.GL_FLOAT, 0, InstanceStride, (void*)16);
            GlApi.glVertexAttribDivisor(GlShaders.AttributeInstance1, 1);
            // Los otros dos: el material del objeto (tinta/UV y fuerzas), también por instancia.
            GlApi.glEnableVertexAttribArray(GlShaders.AttributeInstance2);
            GlApi.glVertexAttribPointer(GlShaders.AttributeInstance2, 4, GlApi.GL_FLOAT, 0, InstanceStride, (void*)32);
            GlApi.glVertexAttribDivisor(GlShaders.AttributeInstance2, 1);
            GlApi.glEnableVertexAttribArray(GlShaders.AttributeInstance3);
            GlApi.glVertexAttribPointer(GlShaders.AttributeInstance3, 4, GlApi.GL_FLOAT, 0, InstanceStride, (void*)48);
            GlApi.glVertexAttribDivisor(GlShaders.AttributeInstance3, 1);
        }

        GlApi.glBindVertexArray(0);
        GlApi.glBindBuffer(GlApi.GL_ARRAY_BUFFER, 0);
    }

    private void CreateUniformBuffer()
    {
        uint buffer = 0;
        GlApi.glGenBuffers(1, &buffer);
        _uniformBuffer = buffer;
        GlApi.glBindBuffer(GlApi.GL_UNIFORM_BUFFER, _uniformBuffer);
        GlApi.glBufferData(GlApi.GL_UNIFORM_BUFFER, ConstantBufferSize, null, GlApi.GL_DYNAMIC_DRAW);
        GlApi.glBindBufferBase(GlApi.GL_UNIFORM_BUFFER, UniformBlockBindingIndex, _uniformBuffer);
        GlApi.glBindBuffer(GlApi.GL_UNIFORM_BUFFER, 0);
    }

    private void CreateQueries()
    {
        if (!HasGpuTiming) return;

        _queries = new uint[FramesInFlight];
        fixed (uint* queries = _queries)
        {
            GlApi.glGenQueries(FramesInFlight, queries);
        }
    }

    // =====================================================================
    // Frame
    // =====================================================================

    private void ApplyViewport() => GlApi.glViewport(0, 0, _width, _height);

    /// <summary>
    /// Ventana redimensionada. En OpenGL el framebuffer por defecto sigue a la ventana, así que no
    /// hay nada que recrear: alcanza con mover el viewport.
    /// </summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(64, width);
        height = Math.Max(64, height);
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;
        ApplyViewport();
    }

    public void SetFullscreen(bool exclusive)
    {
        // No implementada en esta API: se informa en vez de fingir que sí (ver la clase).
        ExclusiveFullscreenAccepted = !exclusive;
    }

    public void RenderFrame(double timeSeconds)
    {
        int slot = _frameIndex;

        // La muestra que ya está lista es la de esta ranura (una vuelta atrás): se pide antes de
        // volver a usar la query.
        ReadGpuTiming(slot);

        UpdateConstants(timeSeconds);

        if (HasGpuTiming)
        {
            GlApi.glBeginQuery(GlApi.GL_TIME_ELAPSED, _queries[slot]);
        }

        // ---- Pasada de sombras (ver README-SOMBRAS.md) ----
        // PRIMERO, y adentro de la query de tiempo (ES trabajo del frame). Al terminar deja las
        // constantes con la matriz de la cámara otra vez y el framebuffer de la ventana atado, así que
        // el resto del frame no se entera de que existió.
        if (_shadowEnabled) RenderShadowPass(timeSeconds);

        GlApi.glViewport(0, 0, _width, _height);
        GlApi.glClearColor(0.02f, 0.02f, 0.03f, 1f);
        GlApi.glClear(GlApi.GL_COLOR_BUFFER_BIT | GlApi.GL_DEPTH_BUFFER_BIT);

        BindSceneTextures();

        // ---- Fondo (sin profundidad) ----
        GlApi.glDisable(GlApi.GL_DEPTH_TEST);
        GlApi.glDepthMask(0);
        GlApi.glUseProgram(_skyProgram);
        GlApi.glBindVertexArray(_skyVertexArray);
        GlApi.glDrawArraysInstanced(GlApi.GL_TRIANGLES, 0, 3, 1);

        // ---- Geometría instanciada ----
        GlApi.glEnable(GlApi.GL_DEPTH_TEST);
        GlApi.glDepthMask(1);
        GlApi.glUseProgram(_geometryProgram);
        for (int mesh = 0; mesh < _geometryVertexArrays.Length; mesh++)
        {
            // La malla con mezcla (el polen) va ÚLTIMA: blend premultiplicado —el mismo de la franja
            // de métricas, que ya está— con la prueba de profundidad encendida y sin ESCRIBIRLA (dos
            // motas que se cruzan no se recortan entre sí). En OpenGL es estado, no pipeline: acá la
            // pasada transparente no cuesta ningún objeto nuevo.
            bool blend = _scene.Meshes[mesh].Blend;
            if (blend)
            {
                GlApi.glEnable(GlApi.GL_BLEND);
                GlApi.glBlendFunc(GlApi.GL_ONE, GlApi.GL_ONE_MINUS_SRC_ALPHA);
                GlApi.glDepthMask(0);
            }

            GlApi.glBindVertexArray(_geometryVertexArrays[mesh]);
            GlApi.glDrawArraysInstanced(GlApi.GL_TRIANGLES, 0, _meshVertexCounts[mesh], _meshInstanceCounts[mesh]);

            if (blend)
            {
                GlApi.glDisable(GlApi.GL_BLEND);
                GlApi.glDepthMask(1);
            }
        }

        // ---- Franja de métricas (dentro del frame) ----
        // Va dentro de la query de tiempo, igual que en Direct3D 11: la franja ES parte del frame
        // que se entrega, así que su costo no puede quedar afuera de la medición.
        if (HasOverlay) DrawOverlay();

        if (HasGpuTiming)
        {
            GlApi.glEndQuery(GlApi.GL_TIME_ELAPSED);
        }

        GlApi.glBindVertexArray(0);
        GlApi.glUseProgram(0);
        _frameIndex = (slot + 1) % FramesInFlight;
    }

    /// <summary>
    /// La pasada de PROFUNDIDAD del shadow map: la escena vista desde el sol (ver `README-SOMBRAS.md`).
    ///
    /// En OpenGL no hay "pasada sin pixel shader": el programa de esta vuelta lleva el MISMO vertex
    /// shader de la geometría (con la matriz del sol en las constantes, dibuja la escena desde la luz) y
    /// un pixel shader vacío, así que no paga el sombreado de materiales por píxel. La profundidad la
    /// escriben las etapas fijas del pipeline, que es lo que la vuelve barata.
    ///
    /// OJO con el estado que toca: el framebuffer (el del mapa, y después el de la ventana), el viewport
    /// (2048² y después el de la ventana) y las constantes (la matriz de la luz y después la de la
    /// cámara). Los tres se reponen antes de volver, y ninguno de los tres lo repone el resto del frame.
    /// </summary>
    private void RenderShadowPass(double timeSeconds)
    {
        int size = _graphics.ShadowMapSize;

        GlApi.glBindFramebuffer(GlApi.GL_FRAMEBUFFER, _shadowFramebuffer);
        GlApi.glViewport(0, 0, size, size);
        GlApi.glClear(GlApi.GL_DEPTH_BUFFER_BIT);
        // Las constantes de ESTA pasada: la matriz del sol como vista-proyección.
        UpdateConstants(timeSeconds, shadowPass: true);

        GlApi.glEnable(GlApi.GL_DEPTH_TEST);
        GlApi.glDepthMask(1);
        GlApi.glUseProgram(_shadowProgram);

        for (int mesh = 0; mesh < _geometryVertexArrays.Length; mesh++)
        {
            if (!_castsShadow[mesh]) continue;

            GlApi.glBindVertexArray(_geometryVertexArrays[mesh]);
            GlApi.glDrawArraysInstanced(GlApi.GL_TRIANGLES, 0, _meshVertexCounts[mesh], _meshInstanceCounts[mesh]);
        }

        GlApi.glBindVertexArray(0);
        // Volver al framebuffer de la ventana y a la matriz de la cámara: el resto del frame sigue igual
        // que si esta pasada no hubiera corrido.
        GlApi.glBindFramebuffer(GlApi.GL_FRAMEBUFFER, 0);
        UpdateConstants(timeSeconds);
    }

    /// <summary>
    /// Presenta y devuelve los ms que tardó la entrega. SwapBuffers es donde OpenGL espera al
    /// monitor (o a que la cola del driver tenga lugar): el mismo papel que tiene Present en Direct3D.
    /// </summary>
    public double Present()
    {
        double start = VulkanApi.NowMs();
        GlApi.SwapBuffers(_deviceContext);
        double elapsed = VulkanApi.NowMs() - start;

        uint error = GlApi.glGetError();
        if (error == GlApi.GL_CONTEXT_LOST)
        {
            // El driver reinició el contexto (una actualización de drivers o un TDR): seguir
            // midiendo sobre un contexto perdido daría números inventados.
            DeviceLost = true;
            throw new InvalidOperationException("El driver de video perdió el contexto de OpenGL durante la corrida.");
        }
        return elapsed;
    }

    /// <summary>
    /// Lee el tiempo de GPU de la ranura SIN esperar: si el dato todavía no está listo, ese frame
    /// queda sin muestra (el informe dice cuántas hubo). Esperarlo obligaría a la placa a terminar
    /// el frame en curso, es decir el instrumento frenaría lo que está midiendo.
    /// </summary>
    private void ReadGpuTiming(int slot)
    {
        if (!HasGpuTiming) return;

        int available = 0;
        GlApi.glGetQueryObjectiv(_queries[slot], GlApi.GL_QUERY_RESULT_AVAILABLE, &available);
        if (available == 0) return;

        ulong nanoseconds = 0;
        GlApi.glGetQueryObjectui64v(_queries[slot], GlApi.GL_QUERY_RESULT, &nanoseconds);
        LastGpuFrameMs = nanoseconds / 1_000_000.0;
    }

    /// <summary>
    /// Sube el bloque del frame. Con <paramref name="shadowPass"/> en true la matriz que viaja como
    /// <c>ViewProjection</c> es la del SOL y no la de la cámara (ver <see cref="RenderShadowPass"/>); el
    /// resto del bloque es idéntico, porque el vertex shader es el mismo.
    /// </summary>
    private void UpdateConstants(double timeSeconds, bool shadowPass = false)
    {
        // El tiempo y la cámara salen de la ESCENA (la corrida es un viaje de N segundos), igual que
        // en los otros backends: las cuatro APIs suben exactamente el mismo bloque por frame.
        var (eye, target) = _scene.CameraAt(timeSeconds);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, (float)_width / Math.Max(1, _height), 0.5f, _scene.FarPlane);

        // Franja de métricas: rectángulo en NDC (xy = esquina mínima, zw = máxima) y su tamaño en
        // píxeles. En NDC la Y crece hacia ARRIBA, así que la esquina mínima es la de ABAJO.
        var overlayRect = Vector4.Zero;
        var overlaySize = Vector4.Zero;
        if (HasOverlay)
        {
            float width = Math.Max(1, _width);
            float height = Math.Max(1, _height);
            overlayRect = new Vector4(
                2f * _overlayX / width - 1f,
                1f - 2f * (_overlayY + _overlayHeight) / height,
                2f * (_overlayX + _overlayWidth) / width - 1f,
                1f - 2f * _overlayY / height);
            overlaySize = new Vector4(_overlayWidth, _overlayHeight, 0f, 0f);
        }

        var viewProjection = Matrix4x4.Multiply(view, projection);
        // La inversa la necesita el fondo para reconstruir el rayo de cada píxel (cielo direccional
        // y nubes marcheras). Con una perspectiva válida siempre se puede invertir; si alguna vez
        // no, se sube la identidad y el fondo sale plano en vez de con NaN.
        var inverseViewProjection = Matrix4x4.Invert(viewProjection, out var inverted)
            ? inverted
            : Matrix4x4.Identity;

        var constants = new FrameConstants
        {
            ViewProjection = shadowPass ? _lightViewProjection : viewProjection,
            CameraPosition = new Vector4(eye, 0f),
            // El sol lo elige la ESCENA (el corredor lo tiene alto, el cañón naciente).
            LightDirection = new Vector4(_scene.LightDirection, 0f),
            // x = segundos de la escena; w libre (el look va en Style, abajo).
            // y = CANTIDAD de luces puntuales de la escena (con cero el shader no entra al bucle).
            // z = ALTO del render target: el rasterizador de OpenGL numera las filas desde ABAJO, así
            // que el shader no puede saber la Y de la imagen (el destello de suelo caía espejado
            // respecto de las otras tres APIs). Es el mismo desfase de origen que el de la derivada
            // en Y (ver GlslDerivativeY): acá se corrige con el dato, no con un shader aparte.
            TimeAndParams = new Vector4((float)timeSeconds, _scene.PointLights.Count, Math.Max(1, _height), 0f),
            OverlayRect = overlayRect,
            OverlaySize = overlaySize,
            InverseViewProjection = inverseViewProjection,
            // El LOOK de la escena viaja como dato (ver D3D11Backend).
            Style = SceneStyleConstants.From(_scene.Style),
            // LUCES PUNTUALES de la escena (faroles, carteles, lámparas).
            Lights = SceneLightConstants.From(_scene.PointLights),
            Environment = SceneEnvironmentConstants.From(_scene, _graphics)
        };
        // Esferas de sombra del primer plano (ver SceneDefinition.ShadowCasters): lo que no usa la
        // escena queda en cero y el shader lo saltea.
        for (int i = 0; i < _scene.ShadowCasters.Count && i < Scenes.SceneDefinition.MaxShadowCasters; i++)
        {
            constants.ShadowCasters[i] = _scene.ShadowCasters[i];
        }

        // glBufferData y no una escritura parcial: el bloque anterior queda "huérfano" y el driver
        // puede seguir leyéndolo mientras la placa trabaja, así que subir el nuevo no lo frena.
        GlApi.glBindBuffer(GlApi.GL_UNIFORM_BUFFER, _uniformBuffer);
        GlApi.glBufferData(GlApi.GL_UNIFORM_BUFFER, ConstantBufferSize, &constants, GlApi.GL_DYNAMIC_DRAW);
        GlApi.glBindBuffer(GlApi.GL_UNIFORM_BUFFER, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Todo best-effort: una corrida abortada puede dejar recursos a medio crear.
        try
        {
            if (_context != 0 && GlApi.glDeleteProgram != null)
            {
                if (_skyProgram != 0) GlApi.glDeleteProgram(_skyProgram);
                if (_overlayProgram != 0) GlApi.glDeleteProgram(_overlayProgram);
                if (_geometryProgram != 0) GlApi.glDeleteProgram(_geometryProgram);
                if (_shadowProgram != 0) GlApi.glDeleteProgram(_shadowProgram);
            }
            // Shadow map (ver README-SOMBRAS.md): el framebuffer del mapa y su textura de profundidad.
            if (GlApi.glDeleteFramebuffers != null)
            {
                uint* framebufferPointer = stackalloc uint[1];
                if (_shadowFramebuffer != 0)
                {
                    framebufferPointer[0] = _shadowFramebuffer;
                    GlApi.glDeleteFramebuffers(1, framebufferPointer);
                    _shadowFramebuffer = 0;
                }
            }
            if (GlApi.glDeleteVertexArrays != null)
            {
                // El puntero tiene que vivir en ESTE marco: si lo devolviera un ayudante, apuntaría
                // a una pila ya liberada.
                uint* arrayPointer = stackalloc uint[1];
                if (_skyVertexArray != 0)
                {
                    arrayPointer[0] = _skyVertexArray;
                    GlApi.glDeleteVertexArrays(1, arrayPointer);
                }
                for (int mesh = 0; mesh < _geometryVertexArrays.Length; mesh++)
                {
                    if (_geometryVertexArrays[mesh] != 0)
                    {
                        arrayPointer[0] = _geometryVertexArrays[mesh];
                        GlApi.glDeleteVertexArrays(1, arrayPointer);
                    }
                }
            }
            if (GlApi.glDeleteBuffers != null)
            {
                for (int mesh = 0; mesh < _vertexBuffers.Length; mesh++)
                {
                    var buffers = new[] { _vertexBuffers[mesh], _instanceBuffers[mesh] };
                    fixed (uint* pointer = buffers)
                    {
                        GlApi.glDeleteBuffers(2, pointer);
                    }
                }
                var uniformBuffers = new[] { _uniformBuffer };
                fixed (uint* pointer = uniformBuffers)
                {
                    GlApi.glDeleteBuffers(1, pointer);
                }
            }
            if (_queries.Length > 0 && GlApi.glDeleteQueries != null)
            {
                fixed (uint* pointer = _queries)
                {
                    GlApi.glDeleteQueries(_queries.Length, pointer);
                }
            }
            if (_detailTexture != 0 && GlApi.glDeleteTextures != null)
            {
                unsafe
                {
                    uint texture = _detailTexture;
                    GlApi.glDeleteTextures(1, &texture);
                }
                _detailTexture = 0;
            }
            if (_shadowTexture != 0 && GlApi.glDeleteTextures != null)
            {
                unsafe
                {
                    uint texture = _shadowTexture;
                    GlApi.glDeleteTextures(1, &texture);
                }
                _shadowTexture = 0;
            }
            if (_shadowTexture != 0)
            {
                unsafe
                {
                    uint texture = _shadowTexture;
                    GlApi.glDeleteTextures(1, &texture);
                }
                _shadowTexture = 0;
            }
            if (_overlayBuffer != 0 && GlApi.glDeleteBuffers != null)
            {
                unsafe
                {
                    uint buffer = _overlayBuffer;
                    GlApi.glDeleteBuffers(1, &buffer);
                }
            }

            GlApi.wglMakeCurrent(0, 0);
        }
        catch { }

        try
        {
            if (_context != 0) GlApi.wglDeleteContext(_context);
        }
        catch { }

        try
        {
            if (_deviceContext != 0 && _window != 0) GlApi.ReleaseDC(_window, _deviceContext);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 CameraPosition;
        public Vector4 LightDirection;
        public Vector4 TimeAndParams;
        public Vector4 OverlayRect;
        public Vector4 OverlaySize;
        // Inversa de ViewProjection, para des-proyectar el rayo de cada píxel del fondo.
        public Matrix4x4 InverseViewProjection;
        // Esferas de sombra (xyz = centro, w = radio); ver SceneDefinition.MaxShadowCasters.
        public ShadowCasterArray ShadowCasters;
        // ESTILO de la escena (ver SceneStyleConstants): es el mismo bloque de 256 bytes que
        // declara el UBO del GLSL, así que el std140 lo lee sin reglas de padding distintas.
        public SceneStyleConstants Style;
        // LUCES PUNTUALES de la escena (ver SceneLightConstants): mismo bloque que las otras APIs.
        public SceneLightConstants Lights;
        // ENTORNO IBL de la escena (ver SceneEnvironmentConstants): los armónicos esféricos del HDRI,
        // que reemplazan el ambiente inventado del estilo por la luz medida en el lugar.
        public SceneEnvironmentConstants Environment;
    }
}
