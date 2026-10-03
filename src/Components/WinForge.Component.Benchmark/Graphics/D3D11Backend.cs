using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinForge.Component.Benchmark.Scenes;
using DxgiFormat = Vortice.DXGI.Format;
using DxgiUsage = Vortice.DXGI.Usage;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Backend Direct3D 11: valida el camino completo del componente (dispositivo, swapchain,
/// shaders compilados en runtime, buffers instanciados, timestamps de GPU).
///
/// Una instancia por corrida y NO es thread-safe a propósito: vive en el hilo de render
/// (ver SceneHost), que es el único que toca el dispositivo. El hilo de UI nunca entra acá.
/// </summary>
public sealed class D3D11Backend : IGraphicsBackend
{
    /// <summary>D3D11_SDK_VERSION.</summary>
    private const uint SdkVersion = 7;

    /// <summary>Bloque de constantes por frame: dos matrices, los vectores del frame, los estilos y las luces.</summary>
    // 2 matrices de 64 + 5 vectores de 16 + sombras + estilo + luces puntuales.
    private const int ConstantBufferSize =
        64 + 16 * 5 + 64 + 16 * Scenes.SceneDefinition.MaxShadowCasters + SceneStyleConstants.SizeInBytes
        + SceneLightConstants.SizeInBytes + SceneEnvironmentConstants.SizeInBytes;

    private const int VertexStride = 32;     // Vector3 posición + Vector3 normal + Vector2 UV
    private const int InstanceStride = 64;   // 4 × Vector4 (colocación, tipo y MATERIAL)

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGIFactory2 _factory = null!;
    private IDXGISwapChain1 _swapChain = null!;

    private ID3D11RenderTargetView _renderTarget = null!;

    /// <summary>Backbuffer para herramientas de verificación (el probador saca capturas). No tocar en el bucle.</summary>
    internal ID3D11Texture2D BackBufferForDiagnostics { get; private set; } = null!;
    private ID3D11DepthStencilView _depthView = null!;
    private ID3D11Texture2D _depthTexture = null!;
    private ID3D11DepthStencilState _skyDepthState = null!;

    /// <summary>
    /// Estado de la pasada con mezcla (el polen): prueba de profundidad SÍ y escritura NO. Sin la
    /// prueba, el polen de detrás de un árbol se dibujaría encima; con la escritura, dos motas que
    /// se cruzan se recortarían entre sí por el orden en que salieron.
    /// </summary>
    private ID3D11DepthStencilState _blendDepthState = null!;

    /// <summary>
    /// Estado de la PASADA DE SOMBRAS: profundidad probada y ESCRITA. Es explícito porque la pasada
    /// corre al PRINCIPIO del frame, cuando el estado del depth-stencil todavía es el que dejó el
    /// frame anterior (el de la franja de métricas, que prueba sin escribir): heredarlo dejaba el mapa
    /// vacío —sin ningún error a la vista— y Direct3D 11 dibujaba la escena sin una sola sombra
    /// mientras las otras tres APIs sí las tenían. Vulkan no puede heredarlo (lo fija en su PSO) y
    /// OpenGL lo pide en su propia pasada, así que este era el único de los cuatro que dependía del
    /// estado del frame anterior.
    /// </summary>
    private ID3D11DepthStencilState _shadowDepthState = null!;
    private ID3D11RasterizerState _rasterizerState = null!;

    private ID3D11VertexShader _geometryVertexShader = null!;
    private ID3D11PixelShader _geometryPixelShader = null!;
    private ID3D11VertexShader _skyVertexShader = null!;
    private ID3D11PixelShader _skyPixelShader = null!;

    /// <summary>Vertex shader de la franja: arma el quad desde el vértice 0 (ver SceneShaders).</summary>
    private ID3D11VertexShader _overlayVertexShader = null!;
    private ID3D11InputLayout _inputLayout = null!;

    // Una entrada por MALLA de la escena: cada una sube su vertex buffer y su instance buffer y se
    // dibuja con su propio DrawInstanced. El cañón usa dos (terreno y cazas) y el corredor una.
    private ID3D11Buffer[] _vertexBuffers = Array.Empty<ID3D11Buffer>();
    private ID3D11Buffer[] _instanceBuffers = Array.Empty<ID3D11Buffer>();
    private ID3D11Buffer _constants = null!;

    // ---- Franja de métricas DENTRO del frame (ver OverlayFrame) ----
    // Un uint por píxel (RGBA8 premultiplicado) en un buffer estructurado y dinámico: subirlo es
    // un Map + copia de memoria en el hilo de render, sin staging ni comandos de copia.
    private ID3D11Buffer? _overlayPixels;
    private ID3D11ShaderResourceView? _overlayView;
    private ID3D11BlendState? _overlayBlend;
    private int _overlayPixelCount;
    private int _overlayWidth;
    private int _overlayHeight;
    private int _overlayX;
    private int _overlayY;
    private long _overlayVersion = -1;
    private byte[]? _overlayPending;
    private bool _overlayDirty;

    /// <summary>Falso mientras la franja no tenga una imagen: sin ella no se dibuja el quad.</summary>
    private bool HasOverlay => _overlayPixels != null && _overlayWidth > 0 && _overlayHeight > 0;

    // ---- Tiempo de GPU: un ANILLO de juegos de queries, no uno solo ----
    // El dato de un conjunto de queries recién está listo cuando la placa terminó ESE frame, y el
    // CPU va varios frames adelante: con un solo juego, el 100 % de las muestras se descartaba por
    // "todavía no está listo" (el informe salía con 0 de 494) o había que esperar a la placa y
    // arruinar justo lo que se mide. Con el anillo se lee SIEMPRE el juego más viejo (nueve frames
    // atrás), que ya terminó, sin esperar nunca. Mismo criterio que D3D12 (ver FramesInFlight).
    private const int TimingSlots = 9;

    // ---- Textura de detalle de la escena (ver Scenes.SceneDetailTexture) ----
    private ID3D11Texture2D _detailTexture = null!;
    private ID3D11ShaderResourceView _detailView = null!;
    private ID3D11SamplerState _detailSampler = null!;

    // ---- Texturas de MATERIAL de la escena (ver Scenes.MaterialAtlas) ----
    // Un solo arreglo con todos los materiales de la escena (tres rebanadas por material) y su
    // sampler. Se ata al pixel shader una vez por frame, igual que el atlas de detalle: lo que cambia
    // por objeto es la RANURA, que viaja en la instancia.
    private ID3D11Texture2D? _materialTexture;
    private ID3D11ShaderResourceView? _materialView;
    private ID3D11SamplerState? _materialSampler;

    // ---- Shadow map (ver README-SOMBRAS.md) ----
    // La profundidad de la escena vista DESDE EL SOL, en UNA cascada cuadrada (el lado sale de la
    // configuración de la corrida, ver SceneGraphicsOptions.ShadowMapSize), más el
    // sampler de comparación que hace el PCF en la placa. Se crea una vez y no cambia con el tamaño
    // de la ventana: la pasada y el muestreo apuntan siempre al mismo recurso.
    private ID3D11Texture2D? _shadowTexture;
    private ID3D11ShaderResourceView? _shadowView;
    private ID3D11DepthStencilView? _shadowDepthView;
    private ID3D11SamplerState? _shadowSampler;

    /// <summary>Mundo → espacio de luz para la pasada de profundidad (ver
    /// <see cref="SceneEnvironmentConstants.LightViewProjectionFor"/>): la misma matriz que el shader
    /// tiene en su bloque, más el ajuste de orientación de la textura que le toca a Direct3D (ver
    /// <see cref="SceneEnvironmentConstants.ShadowMapAxis"/>). Si las dos no coinciden, las sombras
    /// salen corridas sin ningún error a la vista.</summary>
    private Matrix4x4 _lightViewProjection;

    /// <summary>False en una escena sin sombras (o sin geometría de la que deducir el volumen): no se
    /// corre la pasada y el shader ni toca el mapa.</summary>
    private bool _shadowEnabled;

    /// <summary>Qué mallas proyectan (ver <see cref="SceneDefinition.MeshCastsShadow"/>): las de
    /// partículas y las que el shader desplaza quedan afuera de la pasada.</summary>
    private bool[] _castsShadow = Array.Empty<bool>();

    private ID3D11Query[] _disjointTimers = Array.Empty<ID3D11Query>();
    private ID3D11Query[] _timestampStarts = Array.Empty<ID3D11Query>();
    private ID3D11Query[] _timestampEnds = Array.Empty<ID3D11Query>();
    private bool[] _timingPending = Array.Empty<bool>();
    private int _timingSlot;
    private bool _disposed;

    private SceneDefinition _scene = null!;

    /// <summary>Configuración gráfica de la corrida (ver SceneGraphicsOptions): el mismo objeto para
    /// los cuatro backends, porque las cuatro APIs tienen que dibujar exactamente lo mismo.</summary>
    private SceneGraphicsOptions _graphics = SceneGraphicsOptions.Default;
    private double _sceneTime;
    private int[] _meshVertexCounts = Array.Empty<int>();
    private int[] _meshInstanceCounts = Array.Empty<int>();
    private int _width;
    private int _height;
    private bool _vsync;

    /// <summary>
    /// True si el swapchain quedó en pantalla completa exclusiva. Manda en Present: la bandera
    /// de tearing NO es válida en exclusivo (ver <see cref="Present"/>).
    /// </summary>
    private bool _exclusiveFullscreen;
    private FeatureLevel _featureLevel;

    public GraphicsApi Api => GraphicsApi.D3D11;
    public string ApiName => "Direct3D 11";
    public string AdapterName { get; private set; } = "";
    public string AdapterDetail { get; private set; } = "";
    public bool SupportsExclusiveFullscreen => true;
    public bool HasGpuTiming => true;
    public double LastGpuFrameMs { get; private set; }

    /// <summary>La franja se dibuja dentro del frame (ver <see cref="OverlayFrame"/>).</summary>
    public bool OverlayInFrame => true;

    /// <summary>
    /// Direct3D 11 no espera a la GPU para reusar nada: la espera por la cola aparece en
    /// <see cref="Present"/> (la entrega), que es donde el informe la muestra.
    /// </summary>
    public double LastQueueWaitMs => 0;

    public bool DeviceLost { get; private set; }

    /// <summary>False si el monitor rechazó la pantalla completa exclusiva (el informe lo aclara).</summary>
    public bool ExclusiveFullscreenAccepted { get; private set; } = true;

    // =====================================================================
    // Sondeo (para el selector de API: solo se ofrece lo que existe)
    // =====================================================================

    /// <summary>
    /// ¿Se puede usar D3D11 en esta máquina? Se comprueba SIN crear dispositivo: se enumeran
    /// los adaptadores y se pregunta por el feature level mínimo de la escena (11_0).
    /// </summary>
    public static BackendAvailability Probe()
    {
        try
        {
            if (DxgiShared.ListAdapters().Count == 0)
            {
                return new BackendAvailability(GraphicsApi.D3D11, "Direct3D 11", false,
                    "Windows no reportó ningún adaptador de video.");
            }

            bool supported = DxgiShared.TryFindSupporting(
                adapter => D3D11.IsSupportedFeatureLevel(adapter, FeatureLevel.Level_11_0, DeviceCreationFlags.BgraSupport),
                out string name);
            if (!supported)
            {
                return new BackendAvailability(GraphicsApi.D3D11, "Direct3D 11", false,
                    "Ningún adaptador soporta Direct3D 11 (feature level 11_0).");
            }
            return new BackendAvailability(GraphicsApi.D3D11, "Direct3D 11", true, "", name);
        }
        catch (Exception ex)
        {
            return new BackendAvailability(GraphicsApi.D3D11, "Direct3D 11", false, ex.Message);
        }
    }

    // =====================================================================
    // Inicialización
    // =====================================================================

    public void Initialize(BackendInitOptions options)
    {
        _scene = options.Scene;
        _graphics = options.Graphics;
        // La PRIMERA consulta al atlas de materiales es la que decodifica las imágenes y le asigna a
        // cada malla su ranura (ver MaterialAtlas.Build): tiene que pasar ANTES de armar los buffers
        // de instancia, porque las instancias se hornean con esa ranura adentro. Sin esto las mallas
        // salen todas con la ranura neutra (blanco) y la escena se ve sin texturas.
        _ = _scene.Materials;
        _width = Math.Max(64, options.Width);
        _height = Math.Max(64, options.Height);
        _vsync = options.VSync;
        _meshVertexCounts = new int[_scene.Meshes.Count];
        _meshInstanceCounts = new int[_scene.Meshes.Count];
        for (int mesh = 0; mesh < _scene.Meshes.Count; mesh++)
        {
            _meshVertexCounts[mesh] = _scene.Meshes[mesh].Vertices.Length;
            _meshInstanceCounts[mesh] = _scene.Meshes[mesh].Instances.Length;
        }

        var adapters = DxgiShared.Enumerate();
        if (adapters.Count == 0) throw new InvalidOperationException("Windows no reportó ningún adaptador de video.");

        IDXGIAdapter1 chosen = DxgiShared.PickAdapter(adapters, options.AdapterIndex);
        foreach (var adapter in adapters)
        {
            if (!ReferenceEquals(adapter, chosen)) SafeDispose(adapter);
        }

        var chosenDescription = chosen.Description1;
        var levels = new[]
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1, FeatureLevel.Level_10_0
        };
        var result = D3D11.D3D11CreateDevice(
            chosen,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            levels,
            out var device,
            out var featureLevel,
            out var context);
        if (result.Failure || device == null || context == null)
        {
            throw new InvalidOperationException(
                $"No se pudo crear el dispositivo Direct3D 11 en '{chosenDescription.Description}': {result.Description}");
        }
        SafeDispose(chosen);

        _device = device;
        _context = context;
        _featureLevel = featureLevel;
        AdapterName = chosenDescription.Description;
        AdapterDetail = $"Feature level {DxgiShared.FeatureLevelName(featureLevel)} · " +
                        $"{(long)(ulong)chosenDescription.DedicatedVideoMemory / (1024 * 1024)} MB de VRAM dedicada";

        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        CreateSwapChain(options.WindowHandle);
        CreateDepthBuffer();
        CreatePipeline();
        CreateGeometryBuffers();
        CreateDetailTexture();
        CreateMaterialTextures();
        CreateTimingQueries();
        CreateShadowResources();
        ApplyViewport();
    }

    /// <summary>
    /// Recurso del shadow map, en Direct3D 11 (ver <c>README-SOMBRAS.md</c>): una textura de
    /// profundidad que la pasada ESCRIBE y el sombreado LEE, con el sampler de COMPARACIÓN que devuelve
    /// 0/1 ya filtrado (el mismo tipo de sampler que usan Vulkan y OpenGL, para que las cuatro APIs
    /// vean el mismo borde).
    ///
    /// El formato del recurso es <c>R32_Typeless</c> y no <c>D32_Float</c>: una textura de profundidad
    /// no se puede muestrear con su propio formato, así que la vista de profundidad la toma como
    /// <c>D32_Float</c> y la de sombreado como <c>R32_Float</c>. Es el camino documentado, y con los dos
    /// formatos bien puestos el muestreo por comparación funciona sin copias.
    ///
    /// OJO con el orden de las vistas: si la vista de recursos se crea con el formato de profundidad, la
    /// textura se crea bien y el error aparece al primer dibujo que la muestrea.
    /// </summary>
    private void CreateShadowResources()
    {
        // El volumen de sombra sale de la GEOMETRÍA de la escena (ver ResolveShadowVolume): una escena
        // que la enciende no tiene que configurar nada por objeto.
        // La pasada de sombras se corre solo si el usuario no la apagó Y la escena tiene algo que
        // proyectar: apagada desde la configuración, no hay mapa que escribir ni que muestrear.
        _shadowEnabled = _graphics.Shadows && _scene.ShadowStrength > 0f && _scene.ResolveShadowVolume() != null;
        _lightViewProjection = SceneEnvironmentConstants.LightViewProjectionFor(
            _scene, SceneEnvironmentConstants.ShadowMapAxis.FlippedY);

        _castsShadow = new bool[_scene.Meshes.Count];
        for (int mesh = 0; mesh < _castsShadow.Length; mesh++)
        {
            _castsShadow[mesh] = _scene.MeshCastsShadow(_scene.Meshes[mesh]);
        }

        uint size = (uint)_graphics.ShadowMapSize;
        _shadowTexture = _device.CreateTexture2D(
            DxgiFormat.R32_Typeless, size, size, 1, 1, null,
            BindFlags.DepthStencil | BindFlags.ShaderResource);
        _shadowDepthView = _device.CreateDepthStencilView(_shadowTexture, new DepthStencilViewDescription
        {
            Format = DxgiFormat.D32_Float,
            ViewDimension = Vortice.Direct3D11.DepthStencilViewDimension.Texture2D,
            Texture2D = new Texture2DDepthStencilView { MipSlice = 0 }
        });
        _shadowView = _device.CreateShaderResourceView(_shadowTexture, new ShaderResourceViewDescription
        {
            Format = DxgiFormat.R32_Float,
            // El enum de la dimensión vive en Vortice.Direct3D y no en el de Direct3D 11.
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
        });
        _shadowSampler = _device.CreateSamplerState(new SamplerDescription
        {
            // ComparisonMinMagMipLinear: cada muestra del PCF promedio 4 téxeles en el hardware, que es
            // el mismo ablandado de borde que el filtro lineal de Vulkan y OpenGL.
            Filter = Filter.ComparisonMinMagMipLinear,
            // ClampToEdge: el mapa no se repite. Si el filtrado llegara al borde, repetir traería la
            // profundidad del lado opuesto del mundo.
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            // 'Less': el píxel está ILUMINADO cuando su profundidad es menor que la del mapa. Es la
            // misma convención que el sampler de comparación de Vulkan y que GL_COMPARE_REF_TO_TEXTURE
            // con GL_LESS, así que las cuatro APIs resuelven el mismo caso igual.
            ComparisonFunc = ComparisonFunction.Less,
            MinLOD = 0f,
            MaxLOD = float.MaxValue
        });
    }

    /// <summary>
    /// Atlas de detalle de la escena (ver <see cref="Scenes.SceneDetailTexture"/>): RGBA8 de 256×256
    /// que se sube UNA vez al cargar, con filtrado lineal y wrap en las dos direcciones (la celda se
    /// repite sobre el mundo). Ocupa la ranura 0 del pixel shader: la 1 la sigue usando la franja de
    /// métricas.
    /// </summary>
    private void CreateDetailTexture()
    {
        var pixels = Scenes.SceneDetailTexture.Pixels;
        int size = Scenes.SceneDetailTexture.Size;
        unsafe
        {
            fixed (byte* data = pixels)
            {
                var initial = new SubresourceData((nint)data, (uint)(size * 4), (uint)(size * size * 4));
                _detailTexture = _device.CreateTexture2D(
                    DxgiFormat.R8G8B8A8_UNorm, (uint)size, (uint)size, 1, 1,
                    new[] { initial }, BindFlags.ShaderResource);
            }
        }
        _detailView = _device.CreateShaderResourceView(_detailTexture);
        _detailSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0f,
            MaxLOD = float.MaxValue
        });
    }

    /// <summary>
    /// Sube el atlas de materiales de la escena: un arreglo 2D con <c>MapsPerSlot</c> rebanadas por
    /// material y TODOS los mips (los genera el CPU, ver <see cref="Scenes.MaterialAtlas"/>). Una sola
    /// textura para toda la escena: lo que distingue un material de otro es la rebanada, y la rebanada
    /// viaja por instancia.
    ///
    /// Se crea con el arreglo COMPLETO de subrecursos de una vez (mip mayor a menor, una rebanada
    /// después de la otra), que es exactamente el orden en el que el atlas tiene sus píxeles.
    /// </summary>
    private void CreateMaterialTextures()
    {
        var atlas = _scene.Materials;
        var subresources = new SubresourceData[atlas.SliceCount * atlas.MipCount];

        unsafe
        {
            fixed (byte* pixels = atlas.Pixels)
            {
                for (int slice = 0; slice < atlas.SliceCount; slice++)
                {
                    for (int level = 0; level < atlas.MipCount; level++)
                    {
                        int side = atlas.MipSize(level);
                        byte* start = pixels + slice * atlas.SliceBytes + atlas.MipOffsets[level];
                        subresources[slice * atlas.MipCount + level] = new SubresourceData(
                            (nint)start, (uint)(side * 4), (uint)(side * side * 4));
                    }
                }

                // OJO con el orden: la sobrecarga de Vortice es (ancho, alto, arraySize, mipLevels) —
                // al revés que el struct de Direct3D, que los tiene al final y en el otro orden.
                _materialTexture = _device.CreateTexture2D(
                    DxgiFormat.R8G8B8A8_UNorm,
                    (uint)atlas.Size, (uint)atlas.Size,
                    (uint)atlas.SliceCount, (uint)atlas.MipCount,
                    subresources,
                    BindFlags.ShaderResource);
            }
        }

        // Vista por defecto: el recurso es un arreglo 2D con todos sus mips, así que la vista que
        // arma Direct3D sola es la que corresponde (dimensión Texture2DArray, todas las rebanadas).
        _materialView = _device.CreateShaderResourceView(_materialTexture);
        _materialSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0f,
            MaxLOD = float.MaxValue
        });
    }

    private void CreateSwapChain(nint windowHandle)
    {
        var description = new SwapChainDescription1(
            (uint)_width,
            (uint)_height,
            DxgiFormat.B8G8R8A8_UNorm,
            false,
            DxgiUsage.RenderTargetOutput,
            2,
            Scaling.Stretch,
            SwapEffect.FlipDiscard,
            AlphaMode.Ignore,
            // AllowModeSwitch: lo necesita la pantalla completa exclusiva.
            // AllowTearing: sin esto, Present(0) espera al compositor y el FPS queda topeado a
            // la frecuencia del monitor (medido: 165 FPS de "entrega" en un monitor de 165 Hz
            // aunque la placa daba más). El tearing es lo que permite MEDIR la placa real.
            SwapChainFlags.AllowModeSwitch | SwapChainFlags.AllowTearing);
        // Sin multisampling: el swapchain con flip model exige una sola muestra.
        description.SampleDescription = new SampleDescription(1, 0);

        _swapChain = _factory.CreateSwapChainForHwnd(_device, windowHandle, description);
        CreateRenderTarget();
    }

    private void CreateRenderTarget()
    {
        var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        try
        {
            _renderTarget = _device.CreateRenderTargetView(backBuffer);

            // Referencia propia para el probador (capturas de verificación): GetBuffer ya dio
            // una referencia nueva, acá solo se retiene. Se suelta en Resize y en Dispose.
            SafeDispose(BackBufferForDiagnostics);
            BackBufferForDiagnostics = backBuffer;
            backBuffer = null!;   // la propiedad es ahora la dueña
        }
        finally
        {
            // Si quedó una referencia sin dueño (error a mitad), se suelta igual.
            SafeDispose(backBuffer);
        }
    }

    private void CreateDepthBuffer()
    {
        _depthTexture = _device.CreateTexture2D(
            DxgiFormat.D24_UNorm_S8_UInt,
            (uint)_width,
            (uint)_height,
            1,
            1,
            null,
            BindFlags.DepthStencil);
        _depthView = _device.CreateDepthStencilView(_depthTexture);
    }

    private void CreatePipeline()
    {
        string common = SceneShaders.Common;
        byte[] geometryVs = Compile(common + SceneShaders.GeometryVertexShader, "VSMain", SceneShaders.VertexProfile);
        byte[] geometryPs = Compile(common + SceneShaders.GeometryVertexShader + SceneShaders.GeometryPixelShader, "PSMain", SceneShaders.PixelProfile);
        byte[] skyVs = Compile(common + SceneShaders.SkyVertexShader, "VSSky", SceneShaders.VertexProfile);
        byte[] overlayVs = Compile(common + SceneShaders.SkyOverlayVertexShader, "VSSkyOverlay", SceneShaders.VertexProfile);
        byte[] skyPs = Compile(common + SceneShaders.SkyVertexShader + SceneShaders.SkyPixelShader, "PSSky", SceneShaders.PixelProfile);

        _geometryVertexShader = _device.CreateVertexShader(geometryVs);
        _geometryPixelShader = _device.CreatePixelShader(geometryPs);
        _skyVertexShader = _device.CreateVertexShader(skyVs);
        _overlayVertexShader = _device.CreateVertexShader(overlayVs);
        _skyPixelShader = _device.CreatePixelShader(skyPs);

        var elements = new[]
        {
            new InputElementDescription("POSITION", 0, DxgiFormat.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, DxgiFormat.R32G32B32_Float, 12, 0),
            // UV del modelo (los tipos procedurales la dejan en cero: se texturan por posición).
            new InputElementDescription("TEXCOORD", 0, DxgiFormat.R32G32_Float, 24, 0),
            new InputElementDescription("INSTANCE", 0, DxgiFormat.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("INSTANCE", 1, DxgiFormat.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1),
            // Material del objeto: tinta/UV y fuerzas (ver SceneShaders.InstanceData).
            new InputElementDescription("INSTANCE", 2, DxgiFormat.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("INSTANCE", 3, DxgiFormat.R32G32B32A32_Float, 48, 1, InputClassification.PerInstanceData, 1)
        };
        _inputLayout = _device.CreateInputLayout(elements, geometryVs);

        // Fondo: sin prueba de profundidad y sin escritura. Se dibuja primero y la geometría
        // siempre queda encima (el estado por defecto de D3D11 ya prueba y escribe profundidad).
        _skyDepthState = _device.CreateDepthStencilState(DepthStencilDescription.None);
        _blendDepthState = _device.CreateDepthStencilState(DepthStencilDescription.Default with
        {
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.LessEqual
        });
        // La pasada de sombras prueba y escribe profundidad (ver _shadowDepthState): sin esto hereda
        // el estado del frame anterior y el mapa se queda en el valor del clear.
        _shadowDepthState = _device.CreateDepthStencilState(DepthStencilDescription.Default with
        {
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.LessEqual
        });

        // CullNone a propósito: el winding de la malla procedural no es contrato (las normales
        // se corrigen por cara en C#), y el rasterizador por defecto de D3D11 (front = horario,
        // cull back) dejó la escena ENTERA fuera de pantalla: cielo y geometría son horario
        // contra-reloj y el cull se los comió — pantalla negra con la GPU ocupada. Con CullNone
        // la escena siempre se ve y el costo de relleno queda determinista.
        _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            ScissorEnable = false
        });

        _constants = _device.CreateBuffer(
            ConstantBufferSize,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0);

        // Blend para la franja de métricas: los píxeles vienen premultiplicados, así que con
        // One / InverseSourceAlpha el panel opaco reemplaza lo que hay debajo y las bordas
        // transparentes dejan pasar el frame. Solo se activa para el quad de la franja (si se
        // dejara siempre activo, la escena quedaria igual —alpha 1— pero mediria otra cosa).
        _overlayBlend = _device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha));
    }

    private void CreateGeometryBuffers()
    {
        _vertexBuffers = new ID3D11Buffer[_scene.Meshes.Count];
        _instanceBuffers = new ID3D11Buffer[_scene.Meshes.Count];
        for (int mesh = 0; mesh < _scene.Meshes.Count; mesh++)
        {
            _vertexBuffers[mesh] = _device.CreateBuffer(_scene.Meshes[mesh].Vertices, BindFlags.VertexBuffer);
            // RenderInstances y no Instances: el material de la malla ya está horneado en cada
            // instancia (tinta, fuerzas y ranura del atlas). Ver SceneMesh.RenderInstances.
            _instanceBuffers[mesh] = _device.CreateBuffer(_scene.Meshes[mesh].RenderInstances, BindFlags.VertexBuffer);
        }
    }

    private void CreateTimingQueries()
    {
        _disjointTimers = new ID3D11Query[TimingSlots];
        _timestampStarts = new ID3D11Query[TimingSlots];
        _timestampEnds = new ID3D11Query[TimingSlots];
        _timingPending = new bool[TimingSlots];
        for (int slot = 0; slot < TimingSlots; slot++)
        {
            _disjointTimers[slot] = _device.CreateQuery(QueryType.TimestampDisjoint);
            _timestampStarts[slot] = _device.CreateQuery(QueryType.Timestamp);
            _timestampEnds[slot] = _device.CreateQuery(QueryType.Timestamp);
        }
    }

    /// <summary>
    /// Bytecode del shader, con el <c>d3dcompiler_47</c> del sistema: primero la caché en disco y,
    /// si no está, se compila y se guarda. Si falla, el error viaja ENTERO con el texto del
    /// compilador: un shader que no compila sin su línea no se arregla.
    /// </summary>
    private static byte[] Compile(string source, string entryPoint, string profile)
    {
        byte[]? cached = ShaderCache.TryRead(source, entryPoint, profile);
        if (cached != null) return cached;

        var result = Compiler.Compile(source, entryPoint, "escena", profile, out var blob, out var errors);
        if (result.Failure || blob == null)
        {
            string message = errors != null ? errors.AsString() : result.Description;
            SafeDispose(errors);
            SafeDispose(blob);
            throw new InvalidOperationException($"No se pudo compilar el shader '{entryPoint}': {message}");
        }
        SafeDispose(errors);

        byte[] bytecode;
        using (blob)
        {
            bytecode = blob.AsBytes().ToArray();
        }
        ShaderCache.Write(source, entryPoint, profile, bytecode);
        return bytecode;
    }

    // =====================================================================
    // Frame
    // =====================================================================

    private void ApplyViewport()
    {
        _context.RSSetViewport(new Viewport(0f, 0f, _width, _height, 0f, 1f));
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(64, width);
        height = Math.Max(64, height);
        if (width == _width && height == _height) return;

        _width = width;
        _height = height;

        // Soltar el render target ANTES de redimensionar: ResizeBuffers falla si el swapchain
        // todavía tiene referencias vivas.
        _context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), null!);
        SafeDispose(_renderTarget);
        SafeDispose(BackBufferForDiagnostics);
        SafeDispose(_depthView);
        SafeDispose(_depthTexture);

        // Mismos flags que la creación (AllowTearing incluido): con flags distintos ResizeBuffers falla.
        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, DxgiFormat.Unknown,
            SwapChainFlags.AllowModeSwitch | SwapChainFlags.AllowTearing);
        CreateRenderTarget();
        CreateDepthBuffer();
        ApplyViewport();
    }

    public void SetFullscreen(bool exclusive)
    {
        try
        {
            _swapChain.SetFullscreenState(exclusive);
            _exclusiveFullscreen = exclusive;
        }
        catch
        {
            // Si el monitor no lo acepta, la corrida sigue en ventana: el informe lo aclara.
            _exclusiveFullscreen = false;
            ExclusiveFullscreenAccepted = false;
        }
    }

    public void RenderFrame(double timeSeconds)
    {
        _sceneTime = timeSeconds;
        // Se lee el juego MÁS VIEJO (el que se va a reusar ahora): ese frame ya terminó en la placa.
        int timingSlot = _timingSlot;
        _timingSlot = (_timingSlot + 1) % TimingSlots;
        CollectGpuTiming(timingSlot);

        _context.Begin(_disjointTimers[timingSlot]);
        _context.End(_timestampStarts[timingSlot]);

        // ---- Pasada de sombras (ver README-SOMBRAS.md) ----
        // Va PRIMERO, antes del fondo: dibuja SOLO profundidad contra su propio recurso, así que el
        // resto del frame no se entera más que por el mapa que queda listo para muestrear. Todo el
        // estado que toca (render target, viewport, pixel shader y las constantes) lo repone al final,
        // porque la pasada principal se graba después y no puede heredar nada raro.
        if (_shadowEnabled) RenderShadowPass();

        // La textura de detalle se ata a TODO el frame: el fondo la usa para el algodón de las nubes
        // y la geometría para el pasto, la piedra, el agua y las hojas.
        _context.PSSetShaderResource(0, _detailView);
        _context.PSSetSampler(0, _detailSampler);
        // El shadow map (register t6) y su sampler de comparación (s3), con la MISMA atadura por frame
        // que el detalle y los materiales: ningún dibujo cambia un recurso.
        _context.PSSetShaderResource(6, _shadowView!);
        _context.PSSetSampler(3, _shadowSampler!);
        // Texturas de MATERIAL de la escena (register t4/s1): una sola atadura por frame para todos
        // los materiales (ver CreateMaterialTextures).
        _context.PSSetShaderResource(4, _materialView!);
        _context.PSSetSampler(1, _materialSampler!);

        _context.ClearRenderTargetView(_renderTarget, new Color4(0.02f, 0.02f, 0.03f, 1f));
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1f, 0);
        BindRenderTarget();

        _context.RSSetState(_rasterizerState);

        // Las constantes del frame (incluida la INVERSA de vista-proyección) se suben ANTES del
        // fondo: el cielo des-proyecta el rayo de cada píxel con ellas. Subirlas después deja al
        // fondo leyendo el bloque del frame anterior —y con Map/WriteDiscard, memoria reciclada
        // por el driver, o sea indefinida.
        UpdateConstants(timeSeconds);
        _context.VSSetConstantBuffer(0, _constants);
        // El pixel shader LEE EL MISMO cbuffer (dirección de la luz, posición de la
        // cámara y tiempo para el ruido): en D3D11 cada etapa tiene sus propios slots
        // y hay que bindearlo también al PS (en D3D12 el CBV de raíz es visible a
        // todas las etapas, por eso ahí andaba sin esto).
        _context.PSSetConstantBuffer(0, _constants);

        // ---- Fondo (sin profundidad) ----
        _context.OMSetDepthStencilState(_skyDepthState, 0);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_skyVertexShader);
        _context.PSSetShader(_skyPixelShader);
        _context.Draw(3, 0);

        // ---- Geometría instanciada ----
        _context.OMSetDepthStencilState(null, 0);   // estado por defecto: profundidad activa
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_geometryVertexShader);
        _context.PSSetShader(_geometryPixelShader);
        for (int mesh = 0; mesh < _vertexBuffers.Length; mesh++)
        {
            // Las mallas con mezcla (el polen) se dibujan SIEMPRE al final —el host las pone
            // últimas— y con el blend premultiplicado del mismo estado que usa la franja de
            // métricas: una mota no necesita un estado nuevo, solo encender el que ya existe.
            bool blend = _scene.Meshes[mesh].Blend;
            if (blend)
            {
                _context.OMSetDepthStencilState(_blendDepthState, 0);
                unsafe { _context.OMSetBlendState(_overlayBlend, null, uint.MaxValue); }
            }

            _context.IASetVertexBuffers(
                0,
                new[] { _vertexBuffers[mesh], _instanceBuffers[mesh] },
                new[] { (uint)VertexStride, (uint)InstanceStride },
                new[] { 0u, 0u });
            _context.DrawInstanced((uint)_meshVertexCounts[mesh], (uint)_meshInstanceCounts[mesh], 0, 0);

            if (blend)
            {
                _context.OMSetDepthStencilState(null, 0);
                unsafe { _context.OMSetBlendState(null, null, uint.MaxValue); }
            }
        }

        // ---- Franja de métricas (dentro del frame) ----
        // Última pasada: la franja va SOBRE la escena. Se reusa la pipeline del fondo (sin
        // profundidad) y el blend premultiplicado, así no hay pipeline nueva en ninguna API.
        if (HasOverlay)
        {
            UpdateOverlayBuffer();
            _context.OMSetDepthStencilState(_skyDepthState, 0);
            // El factor de blend de Vortice se toma como puntero (nullptr = {1,1,1,1} en D3D11),
            // por eso va en contexto no seguro; los factores no importan acá porque el estado
            // ya fija One/InverseSourceAlpha.
            unsafe { _context.OMSetBlendState(_overlayBlend, null, uint.MaxValue); }
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_overlayVertexShader);
            _context.PSSetShader(_skyPixelShader);
            // Ranura 1 (register(t1) en el HLSL): la 0 del PS la usa la escena si algún día
            // necesita una textura, y en Vulkan b0 y t0 compartirían binding.
            _context.PSSetShaderResource(1, _overlayView!);
            // 6 vértices desde el vértice 0: el quad lo arma VSSkyOverlay (ver SceneShaders).
            _context.DrawInstanced(6u, 1u, 0u, 0u);
            // null desata la ranura (el parámetro no acepta nulos en la firma de Vortice).
            _context.PSSetShaderResource(1, null!);
            unsafe { _context.OMSetBlendState(null, null, uint.MaxValue); }
        }

        _context.End(_timestampEnds[timingSlot]);
        _context.End(_disjointTimers[timingSlot]);
        _timingPending[timingSlot] = true;
    }

    /// <summary>
    /// La pasada de PROFUNDIDAD del shadow map: la escena vista desde el sol (ver `README-SOMBRAS.md`).
    ///
    /// No necesita shader nuevo: reusa el vertex shader de la geometría con la matriz de luz en el
    /// lugar de la de la cámara, y como no dibuja color el pixel shader va DESATADO (sin PS, la placa
    /// solo rasteriza y escribe profundidad: es lo que hace que esta pasada cueste mucho menos que la
    /// principal aun dibujando la misma geometría).
    ///
    /// En Direct3D 11 el bloque de constantes se sube con OTRO Map(WriteDiscard): eso pide memoria nueva
    /// y deja intacto el bloque que la pasada principal va a usar un rato después. Con una escritura
    /// parcial sobre el mismo bloque, las dos pasadas leerían la última matriz subida y las sombras
    /// saldrían del lugar.
    /// </summary>
    private void RenderShadowPass()
    {
        float size = _graphics.ShadowMapSize;

        // Sin render targets de color: la profundidad es lo único que se escribe.
        _context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), _shadowDepthView!);
        _context.RSSetViewport(new Viewport(0f, 0f, size, size, 0f, 1f));
        // Profundidad probada y escrita: el estado del frame anterior apagaría la escritura y el mapa
        // quedaría vacío (ver _shadowDepthState).
        _context.OMSetDepthStencilState(_shadowDepthState, 0);
        // El mismo cull que la pasada principal (CullNone): si acá se colara el estado por defecto,
        // media escena dejaría de proyectar y el mapa tendría agujeros que no se ven hasta que la
        // escena entera se mira de cerca.
        _context.RSSetState(_rasterizerState);
        _context.ClearDepthStencilView(_shadowDepthView!, DepthStencilClearFlags.Depth, 1f, 0);
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.PSSetShader(null!);
        _context.VSSetShader(_geometryVertexShader);

        UpdateConstants(_sceneTime, shadowPass: true);

        for (int mesh = 0; mesh < _vertexBuffers.Length; mesh++)
        {
            if (!_castsShadow[mesh]) continue;

            _context.IASetVertexBuffers(
                0,
                new[] { _vertexBuffers[mesh], _instanceBuffers[mesh] },
                new[] { (uint)VertexStride, (uint)InstanceStride },
                new[] { 0u, 0u });
            _context.DrawInstanced((uint)_meshVertexCounts[mesh], (uint)_meshInstanceCounts[mesh], 0, 0);
        }

        // Reponer el estado del frame: la pasada principal cuenta con el render target, el viewport de
        // la ventana y su pixel shader.
        BindRenderTarget();
        ApplyViewport();
        _context.PSSetShader(_geometryPixelShader);
    }

    /// <summary>
    /// Presenta y devuelve los ms que tardó. Con la cola de la GPU llena, Present espera al
    /// monitor: ese tiempo es de la ENTREGA, no del CPU, y por eso se mide aparte.
    /// </summary>
    public double Present()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // El flag de tearing solo vale con syncInterval 0 y EN VENTANA: DXGI lo rechaza con
        // DXGI_ERROR_INVALID_CALL si el swapchain está en exclusivo (por eso la corrida se caía
        // al primer Present con "Pantalla completa exclusiva"). Fuera del exclusivo se mantiene,
        // que es lo que permite medir la placa sin el freno del compositor.
        var flags = !_vsync && !_exclusiveFullscreen ? PresentFlags.AllowTearing : PresentFlags.None;
        var present = _swapChain.Present(_vsync ? 1u : 0u, flags);
        double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        if (present.Failure)
        {
            DeviceLost = true;
            throw new InvalidOperationException($"Direct3D 11 no pudo presentar el frame: {present.Description}");
        }
        return elapsedMs;
    }

    private void BindRenderTarget()
    {
        _context.OMSetRenderTargets(1, new[] { _renderTarget }, _depthView);
    }

    /// <summary>
    /// Sube el bloque del frame. Con <paramref name="shadowPass"/> en true la matriz que viaja como
    /// <c>ViewProjection</c> es la del SOL y no la de la cámara: es todo lo que hace falta para que el
    /// vertex shader de la geometría dibuje la misma escena desde la luz, sin un segundo par de shaders.
    /// </summary>
    private void UpdateConstants(double timeSeconds, bool shadowPass = false)
    {
        // El tiempo de la escena se pide a la ESCENA, no al host: la corrida es un viaje de N
        // segundos y el mundo completo es función de ese reloj.
        var (eye, target) = _scene.CameraAt(_sceneTime);
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
            // x = segundos de la escena. w quedó libre: el mundo ya no se elige con un interruptor
            // acá (ver Style, abajo).
            // y = CANTIDAD de luces puntuales de la escena: con cero el shader sale del bucle en la
            // primera vuelta, así que una escena de día no paga por el camino de las luces.
            // z = ALTO del render target, solo para las APIs que numeran las filas desde ABAJO
            // (OpenGL; ver ScreenPixel). Direct3D numera desde arriba, que es la convención del
            // shader: por eso acá va en 0.
            TimeAndParams = new Vector4((float)_sceneTime, _scene.PointLights.Count, 0f, 0f),
            OverlayRect = overlayRect,
            OverlaySize = overlaySize,
            InverseViewProjection = inverseViewProjection,
            // El LOOK de la escena viaja como dato: cielo, sol, atmósfera, nubes, exposición y
            // tinte del suelo. Es lo que reemplaza al viejo interruptor global de estilo, así que
            // tocar el cielo de una escena no puede cambiar el de otra.
            Style = SceneStyleConstants.From(_scene.Style),
            Lights = SceneLightConstants.From(_scene.PointLights),
            Environment = SceneEnvironmentConstants.From(_scene, _graphics)
        };
        // Esferas de sombra del primer plano (ver SceneDefinition.ShadowCasters): lo que no usa la
        // escena queda en cero y el shader lo saltea.
        for (int i = 0; i < _scene.ShadowCasters.Count && i < Scenes.SceneDefinition.MaxShadowCasters; i++)
        {
            constants.ShadowCasters[i] = _scene.ShadowCasters[i];
        }
        // Map/Unmap con WriteDiscard en vez de UpdateSubresource: el cbuffer es
        // DYNAMIC/WRITE y por frame. UpdateSubresource sobre un recurso dinámico que
        // la GPU todavía puede estar leyendo da copias perdidas/orden indefinido; el
        // Map con descarte pide un bloque nuevo y la escena usa siempre el último.
        MappedSubresource mapped = _context.Map(_constants, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                MemoryMarshal.Write(new Span<byte>((void*)mapped.DataPointer, ConstantBufferSize), in constants);
            }
        }
        finally
        {
            _context.Unmap(_constants, 0);
        }
    }

    /// <summary>
    /// Lee el tiempo de GPU del frame que usó ESA ranura del anillo. Se lee tardísimo a propósito
    /// (tantos frames como ranuras tenga el anillo): el dato del frame en curso todavía no está
    /// listo, y esperarlo frenaría justamente lo que se quiere medir. Si aún así no está, ese frame
    /// queda sin muestra (el informe dice cuántas hubo) — nunca se espera a la placa.
    /// </summary>
    private void CollectGpuTiming(int slot)
    {
        if (!_timingPending[slot]) return;
        _timingPending[slot] = false;

        // DoNotFlush: si el dato todavía no está listo se SALTA la muestra. Leerlo con el flag
        // por defecto obliga a la placa a terminar el frame en curso — es decir, el instrumento
        // frena lo que está midiendo, y así aparecían frames de más de un segundo.
        if (!_context.GetData(_disjointTimers[slot], AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint disjoint)) return;
        if (disjoint.Disjoint) return;
        if (!_context.GetData(_timestampStarts[slot], AsyncGetDataFlags.DoNotFlush, out ulong start)) return;
        if (!_context.GetData(_timestampEnds[slot], AsyncGetDataFlags.DoNotFlush, out ulong end)) return;
        if (disjoint.Frequency == 0 || end <= start) return;

        LastGpuFrameMs = (end - start) * 1000.0 / disjoint.Frequency;
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
        // ESTILO de la escena (ver SceneStyleConstants): cielo, sol, bruma, nubes y grading. Va al
        // final para no mover el offset de nada de arriba, y son todos float4 para que el bloque
        // mida y alinee igual en HLSL, en el std140 de GLSL y acá.
        public SceneStyleConstants Style;
        // LUCES PUNTUALES de la escena (ver SceneLightConstants): faroles, carteles, lámparas.
        public SceneLightConstants Lights;
        // ENTORNO IBL de la escena (ver SceneEnvironmentConstants): los armónicos esféricos del HDRI,
        // que reemplazan el ambiente inventado del estilo por la luz medida en el lugar.
        public SceneEnvironmentConstants Environment;
    }

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

        // El buffer se recrea SOLO si cambió el tamaño: el ancho de la franja se cuantiza y el
        // alto es fijo, así que en la práctica se crea una vez por corrida.
        if (frame.Width != _overlayWidth || frame.Height != _overlayHeight)
        {
            _overlayWidth = frame.Width;
            _overlayHeight = frame.Height;
            CreateOverlayBuffer();
        }
        _overlayX = frame.X;
        _overlayY = frame.Y;
        _overlayPending = frame.Pixels;
        _overlayVersion = frame.Version;
        _overlayDirty = true;
    }

    /// <summary>
    /// Buffer estructurado (un uint por píxel) con la vista de recursos. DYNAMIC + WRITE porque
    /// se reescribe desde el CPU cada vez que la franja cambia (4 veces por segundo): es el
    /// camino más corto entre los píxeles que dibuja GDI+ y la textura que lee el shader.
    /// </summary>
    private void CreateOverlayBuffer()
    {
        SafeDispose(_overlayView);
        SafeDispose(_overlayPixels);
        _overlayPixelCount = Math.Max(1, _overlayWidth * _overlayHeight);
        // Mismo overload que _constants (el único ya usado en este archivo): estructurado con
        // stride 4 = un uint por píxel, y DYNAMIC porque lo escribe el CPU en cada cambio.
        _overlayPixels = _device.CreateBuffer(
            (uint)(_overlayPixelCount * 4),
            BindFlags.ShaderResource,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.BufferStructured,
            4);
        // Vista por defecto sobre el buffer estructurado: Vortice deriva NumElements del
        // stride del recurso, así que no hace falta declarar la descripción a mano.
        _overlayView = _device.CreateShaderResourceView(_overlayPixels);
    }

    /// <summary>Copia los píxeles pendientes al buffer (una vez por cambio, no por frame).</summary>
    private void UpdateOverlayBuffer()
    {
        if (!_overlayDirty || _overlayPixels == null || _overlayPending == null) return;
        _overlayDirty = false;

        int bytes = Math.Min(_overlayPending.Length, _overlayPixelCount * 4);
        var mapped = _context.Map(_overlayPixels, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                fixed (byte* source = _overlayPending)
                {
                    Buffer.MemoryCopy(source, (void*)mapped.DataPointer, _overlayPixelCount * 4, bytes);
                }
            }
        }
        finally
        {
            _context.Unmap(_overlayPixels, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Liberar en orden inverso al de creación. Todo best-effort: una corrida abortada puede
        // dejar recursos a medio crear y no queremos que el Dispose tire otra excepción encima.
        SafeDispose(_skyVertexShader); SafeDispose(_skyPixelShader); SafeDispose(_overlayVertexShader);
        SafeDispose(_geometryVertexShader); SafeDispose(_geometryPixelShader);
        SafeDispose(_inputLayout);
        foreach (var buffer in _vertexBuffers) SafeDispose(buffer);
        foreach (var buffer in _instanceBuffers) SafeDispose(buffer);
        SafeDispose(_constants);
        SafeDispose(_overlayView); SafeDispose(_overlayPixels); SafeDispose(_overlayBlend);
        SafeDispose(_detailView); SafeDispose(_detailTexture); SafeDispose(_detailSampler);
        SafeDispose(_materialView); SafeDispose(_materialTexture); SafeDispose(_materialSampler);
        SafeDispose(_skyDepthState); SafeDispose(_blendDepthState); SafeDispose(_shadowDepthState); SafeDispose(_rasterizerState);
        SafeDispose(_shadowView); SafeDispose(_shadowDepthView); SafeDispose(_shadowTexture); SafeDispose(_shadowSampler);
        foreach (var query in _disjointTimers) SafeDispose(query);
        foreach (var query in _timestampStarts) SafeDispose(query);
        foreach (var query in _timestampEnds) SafeDispose(query);
        SafeDispose(_depthView); SafeDispose(_depthTexture); SafeDispose(_renderTarget); SafeDispose(BackBufferForDiagnostics);
        SafeDispose(_swapChain); SafeDispose(_context); SafeDispose(_device); SafeDispose(_factory);
    }

    private static void SafeDispose(IDisposable? resource)
    {
        try { resource?.Dispose(); } catch { }
    }
}
