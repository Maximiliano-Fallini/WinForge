using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinForge.Component.Benchmark.Scenes;
using DxgiFormat = Vortice.DXGI.Format;
using DxgiUsage = Vortice.DXGI.Usage;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Backend Direct3D 12: la misma escena que Direct3D 11, con los objetos explícitos de esta API
/// (cola, listas de comandos, allocadores, root signature y PSO) y el mismo tipo de medición.
///
/// Las dos diferencias que importan para el informe:
/// <list type="bullet">
/// <item>Los tiempos de GPU se leen con <c>EndQuery</c> sobre un query heap de timestamps, que la
/// GPU resuelve en un buffer de lectura: es el mecanismo propio de D3D12 y no hay versión
/// "disjoint" (D3D12 asume que el reloj de la GPU no salta; si el equipo tiene un overclock que
/// cambia la frecuencia EN MEDIO de una corrida, el dato se distorsiona).</item>
/// <item>El swapchain vive en la cola directa y los frames van en vuelo por ranura, así que el
/// tiempo de GPU de una ranura se lee dos frames después: la estadística del informe son
/// medianas y percentiles de una serie larga, así que ese corrimiento de un frame no la mueve.</item>
/// </list>
///
/// Una instancia por corrida y no es thread-safe: vive en el hilo de render (ver SceneHost).
/// </summary>
public sealed class D3D12Backend : IGraphicsBackend
{
    /// <summary>
    /// Frames en vuelo, y también cantidad de buffers del swapchain. Son 3 y no 2 por una razón
    /// MEDIDA: con 2, la barrera que devuelve el buffer del swapchain a render target espera a que
    /// el compositor de Windows lo suelte, la cola se frena y la placa queda medio ociosa (el
    /// reloj baja y el tiempo de GPU por frame SUBE: daba 6,5 ms contra los 4,1 ms de Direct3D 11
    /// con la misma escena). Con 3 el trabajo se encadena sin esperar al compositor.
    /// </summary>
    private const int FramesInFlight = 3;

    /// <summary>
    /// Tamaño del bloque de constantes por frame: 2 matrices de 64 bytes (vista-proyección y su
    /// inversa) + 5 vectores de 16 (cámara, luz, tiempo, rect y tamaño de la franja) + las esferas
    /// de sombra + el estilo de la escena (ver <see cref="SceneStyleConstants"/>).
    /// </summary>
    private const int ConstantBufferSize =
        64 + 16 * 5 + 64 + 16 * Scenes.SceneDefinition.MaxShadowCasters + SceneStyleConstants.SizeInBytes
        + SceneLightConstants.SizeInBytes + SceneEnvironmentConstants.SizeInBytes;

    /// <summary>
    /// Paso entre las constantes de dos frames en vuelo: múltiplo de 256 (lo exige la vista de
    /// constantes de D3D12) y NUNCA menor que el bloque. Con un paso fijo de 256 el frame siguiente
    /// pisaba la cola del anterior; el paso sale del tamaño real, redondeado para arriba.
    /// </summary>
    private const int ConstantBufferSlice = (ConstantBufferSize + 255) / 256 * 256;

    /// <summary>
    /// Rebanadas de constantes por frame en vuelo. Son DOS: la de la pasada de sombras y la de la
    /// pasada principal del mismo frame, que llevan matrices distintas (la del sol y la de la cámara).
    /// Con una sola, la segunda escritura del CPU pisaba el bloque que la pasada de sombras todavía
    /// iba a leer.
    /// </summary>
    private const int ConstantSlicesPerFrame = 2;
    private const int VertexStride = 32;     // Vector3 posición + Vector3 normal + Vector2 UV
    private const int InstanceStride = 64;   // 4 × Vector4 (colocación, tipo y MATERIAL)

    private const ulong InvalidFenceValue = 0;

    private IDXGIFactory4 _factory = null!;
    private ID3D12Device _device = null!;
    private ID3D12CommandQueue _queue = null!;
    private IDXGISwapChain3 _swapChain = null!;
    private ID3D12GraphicsCommandList _commandList = null!;
    private ID3D12CommandAllocator[] _allocators = null!;
    private ID3D12Fence _fence = null!;
    private readonly ulong[] _fenceValues = new ulong[FramesInFlight];
    private ulong _fenceCounter;
    private AutoResetEvent _fenceEvent = null!;

    private ID3D12DescriptorHeap _rtvHeap = null!;
    private ID3D12Resource[] _renderTargets = null!;
    private uint _rtvDescriptorSize;
    private ID3D12Resource _depthTexture = null!;
    private ID3D12DescriptorHeap _dsvHeap = null!;

    private ID3D12RootSignature _rootSignature = null!;
    private ID3D12PipelineState _geometryPipeline = null!;
    private ID3D12PipelineState _skyPipeline = null!;
    private ID3D12PipelineState _overlayPipeline = null!;

    /// <summary>
    /// Pipeline de la PASADA DE SOMBRAS (ver <c>README-SOMBRAS.md</c>): los MISMOS shaders de la
    /// geometría con CERO render targets, o sea que no hay nada que agregar al HLSL. El pixel shader va
    /// en <c>null</c> (sin él la placa solo rasteriza y escribe profundidad) y por eso en Direct3D 12
    /// —donde el pipeline es un objeto— hace falta esta PSO propia.
    /// </summary>
    private ID3D12PipelineState _shadowPipeline = null!;

    // ---- Shadow map (ver README-SOMBRAS.md) ----
    private ID3D12Resource? _shadowTexture;
    private ID3D12DescriptorHeap? _shadowDsvHeap;

    /// <summary>Mundo → espacio de luz de la pasada (ver
    /// <see cref="SceneEnvironmentConstants.LightViewProjectionFor"/>): la misma matriz que el shader
    /// tiene en su bloque, más el ajuste de orientación de la textura que le toca a Direct3D (ver
    /// <see cref="SceneEnvironmentConstants.ShadowMapAxis"/>).</summary>
    private Matrix4x4 _lightViewProjection;

    /// <summary>False en una escena sin sombras o sin geometría de la que deducir el volumen.</summary>
    private bool _shadowEnabled;

    /// <summary>Qué mallas proyectan (ver <see cref="SceneDefinition.MeshCastsShadow"/>).</summary>
    private bool[] _castsShadow = Array.Empty<bool>();

    /// <summary>Geometría con blend premultiplicado y sin escritura de profundidad: la pasada del polen.</summary>
    private ID3D12PipelineState _blendPipeline = null!;
    // ---- Franja de métricas DENTRO del frame (ver OverlayFrame) ----
    // Un uint por píxel (RGBA8 premultiplicado) en un buffer de subida: el CPU lo escribe con una
    // copia de memoria y el shader lo lee como StructuredBuffer (register t1 → SRV de raíz).
    private ID3D12Resource? _overlayBuffer;
    // Un uint por píxel: stride de la vista y del shader (el recurso no lo lleva).
    private const uint OverlayPixelStride = 4;
    // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING: identidad para las cuatro componentes.
    private const uint D3D12DefaultShader4ComponentMapping = 0x1688;
    private ID3D12DescriptorHeap? _overlaySrvHeap;
    // El heap CBV_SRV_UAV lleva TRES descriptores: el 0 es la franja, el 1 la textura de detalle y el
    // 2 el arreglo de materiales de la escena. No se puede atar un segundo heap del mismo tipo:
    // D3D12 admite UNO de cada tipo por frame.
    private uint _srvDescriptorSize;
    // Textura de detalle (register t0). Vive en heap Default: un SRV Texture2D NO puede apuntar
    // a un buffer de upload (medido: el descriptor se crea bien pero la placa se cae al usarlo).
    // La subida va por buffer de paso + CopyBufferRegion, igual que cualquier textura de D3D12.
    private ID3D12Resource? _detailTexture;
    private ID3D12Resource? _detailUpload;
    // El sampler (register s0) exige heap de tipo Sampler, que es de OTRO tipo que el CBV_SRV_UAV.
    private ID3D12DescriptorHeap? _detailSamplerHeap;
    private uint _samplerDescriptorSize;
    // Arreglo de materiales de la escena (register t4, ver Scenes.MaterialAtlas): una sola textura
    // con TODOS los materiales, y el material de cada objeto es un índice que viaja por instancia.
    private ID3D12Resource? _materialTexture;
    private ID3D12Resource? _materialUpload;
    private int _overlayPixelCount;
    private int _overlayWidth;
    private int _overlayHeight;
    private int _overlayX;
    private int _overlayY;
    private long _overlayVersion = -1;
    private byte[]? _overlayPending;
    private bool _overlayDirty;

    // Una entrada por MALLA de la escena (ver D3D11Backend): el cañón sube terreno y cazas.
    private ID3D12Resource[] _vertexBuffers = Array.Empty<ID3D12Resource>();
    private ID3D12Resource[] _instanceBuffers = Array.Empty<ID3D12Resource>();
    private ID3D12Resource _constantBuffer = null!;
    private ulong[] _vertexBufferSizes = Array.Empty<ulong>();
    private ulong[] _instanceBufferSizes = Array.Empty<ulong>();

    private ID3D12QueryHeap _queryHeap = null!;
    private ID3D12Resource _timestampReadback = null!;
    private ulong _timestampFrequency;
    private readonly bool[] _timingPending = new bool[FramesInFlight];

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
    /// de tearing NO es válida en exclusivo (ver <see cref="Present"/>), y en Direct3D 12 el
    /// exclusivo es emulado (cambia el modo de video y sigue siendo una ventana), así que el
    /// estado se consulta igual para no romper en sistemas donde sí sea exclusivo de verdad.
    /// </summary>
    private bool _exclusiveFullscreen;
    private uint _backBufferIndex;
    private FeatureLevel _featureLevel;
    private bool _disposed;

    public GraphicsApi Api => GraphicsApi.D3D12;
    public string ApiName => "Direct3D 12";
    public string AdapterName { get; private set; } = "";
    public string AdapterDetail { get; private set; } = "";
    public bool SupportsExclusiveFullscreen => true;
    public bool HasGpuTiming => true;
    public double LastGpuFrameMs { get; private set; }
    public double LastQueueWaitMs { get; private set; }
    public bool DeviceLost { get; private set; }

    /// <summary>La franja se dibuja dentro del frame (ver <see cref="OverlayFrame"/>): Direct3D 12
    /// entrega con flip model y vsync apagado por el camino de tearing, donde la ventana no se
    /// compone y una franja en ventana aparte no aparece.</summary>
    public bool OverlayInFrame => true;

    /// <summary>Falso mientras no haya píxeles: sin franja no se dibuja el quad.</summary>
    private bool HasOverlay => _overlayWidth > 0 && _overlayHeight > 0;

    /// <summary>
    /// Deja la franja lista para los próximos frames (ver <see cref="OverlayFrame"/>). Se llama desde
    /// el hilo de render: acá solo se guardan los píxeles y, si cambió el tamaño del panel, se
    /// rehace el buffer; la copia a memoria se hace al PRINCIPIO del frame, cuando ya se esperó a
    /// que la placa terminara el anterior.
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

        // El buffer se rehace SOLO si cambió el tamaño: el ancho de la franja se cuantiza y el alto
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

    /// <summary>False si el monitor rechazó la pantalla completa exclusiva (el informe lo aclara).</summary>
    public bool ExclusiveFullscreenAccepted { get; private set; } = true;

    // =====================================================================
    // Sondeo
    // =====================================================================

    /// <summary>
    /// ¿Se puede usar Direct3D 12 en esta máquina? Se pregunta por el feature level mínimo que
    /// pide el pipeline (11_0, que es lo que necesita el shader model 5.0 de la escena).
    /// </summary>
    public static BackendAvailability Probe()
    {
        try
        {
            if (DxgiShared.ListAdapters().Count == 0)
            {
                return new BackendAvailability(GraphicsApi.D3D12, "Direct3D 12", false,
                    "Windows no reportó ningún adaptador de video.");
            }

            if (!DxgiShared.TryFindSupporting(a => D3D12.IsSupported(a, FeatureLevel.Level_11_0), out string adapterName))
            {
                return new BackendAvailability(GraphicsApi.D3D12, "Direct3D 12", false,
                    "Ningún adaptador soporta Direct3D 12 (feature level 11_0).");
            }
            return new BackendAvailability(GraphicsApi.D3D12, "Direct3D 12", true, "", adapterName);
        }
        catch (Exception ex)
        {
            return new BackendAvailability(GraphicsApi.D3D12, "Direct3D 12", false, ex.Message);
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
        // de instancia, porque las instancias se hornean con esa ranura adentro.
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
            if (!ReferenceEquals(adapter, chosen)) DxgiShared.SafeDispose(adapter);
        }

        var chosenDescription = chosen.Description1;
        _featureLevel = FeatureLevel.Level_11_0;
        _device = D3D12.D3D12CreateDevice<ID3D12Device>(chosen, FeatureLevel.Level_11_0);
        DxgiShared.SafeDispose(chosen);

        AdapterName = chosenDescription.Description;
        AdapterDetail = $"Feature level {DxgiShared.FeatureLevelName(_featureLevel)} · " +
                        $"{(long)(ulong)chosenDescription.DedicatedVideoMemory / (1024 * 1024)} MB de VRAM dedicada";

        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        _queue = _device.CreateCommandQueue(CommandListType.Direct);
        CreateSwapChain(options.WindowHandle);
        CreateDepthBuffer();
        CreatePipelines();
        CreateGeometryBuffers();
        CreateFence();
        CreateTiming();
        // Buffer (mínimo) de la franja desde el arranque: los shaders del fondo declaran el t1 y los
        // root arguments se atan siempre, así que tiene que haber un descriptor válido aunque el
        // anfitrión nunca llame a SetOverlay. Va después de la valla: rehacer el buffer espera a que
        // la placa quede quieta.
        CreateOverlayBuffer(0);
        // Textura de detalle: SRV en la ranura 1 del heap de la franja + sampler en heap propio.
        CreateDetailTexture();
        // Texturas de MATERIAL de la escena (un arreglo 2D por escena, ver Scenes.MaterialAtlas):
        // SRV en la ranura 2 del mismo heap + sampler en la 1 del heap de samplers.
        CreateMaterialTextures();
        // Shadow map (ver README-SOMBRAS.md): recurso de profundidad + SRV en la ranura 3 del heap y
        // sampler de comparación en la 2 del heap de samplers. Va al final porque los dos heaps ya
        // existen (los crean la franja y el detalle).
        CreateShadowResources();
        UpdateViewport();
    }

    private void CreateSwapChain(nint windowHandle)
    {
        var description = new SwapChainDescription1(
            (uint)_width,
            (uint)_height,
            DxgiFormat.B8G8R8A8_UNorm,
            false,
            DxgiUsage.RenderTargetOutput,
            FramesInFlight,
            Scaling.Stretch,
            SwapEffect.FlipDiscard,
            AlphaMode.Ignore,
            // AllowModeSwitch: lo necesita la pantalla completa exclusiva.
            // AllowTearing: sin vsync el compositor de Windows no debe frenar la presentación. Sin
            // esto, cada barrera que devuelve el buffer a render target espera al compositor, la
            // placa queda medio ociosa y el tiempo de GPU por frame sale INFLADO (medido: 6,4 ms
            // contra 4,1 ms de Direct3D 11 con la misma escena). Con tearing la medición es la
            // misma en las dos APIs.
            SwapChainFlags.AllowModeSwitch | SwapChainFlags.AllowTearing);
        description.SampleDescription = new SampleDescription(1, 0);

        using var swapChain1 = _factory.CreateSwapChainForHwnd(_queue, windowHandle, description, null, null);
        _swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();

        _rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        _rtvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = FramesInFlight
        });

        _renderTargets = new ID3D12Resource[FramesInFlight];
        CreateRenderTargets();

        _allocators = new ID3D12CommandAllocator[FramesInFlight];
        for (int i = 0; i < FramesInFlight; i++) _allocators[i] = _device.CreateCommandAllocator(CommandListType.Direct);
        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _allocators[0], null);
        // CreateCommandList deja la lista GRABANDO: si no se cierra, el allocador queda en uso y
        // su primer Reset() falla con E_FAIL ("el allocador todavía está grabando").
        _commandList.Close();

        _backBufferIndex = _swapChain.CurrentBackBufferIndex;
    }

    private void CreateRenderTargets()
    {
        var start = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < FramesInFlight; i++)
        {
            _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);

            // El handle se calcula en una COPIA local: el ayudante Offset devuelve una REFERENCIA
            // (SharpGen) y usarlo directamente como argumento de la llamada nativa daba una
            // violación de acceso intermitente al crear la tercera vista.
            var handle = start;
            handle = handle.Offset(i, _rtvDescriptorSize);

            // La descripción va explícita (formato y dimensión) en vez de null: así la vista no
            // depende de cómo el puente a nativo interprete la ausencia de descripción.
            _device.CreateRenderTargetView(_renderTargets[i], new RenderTargetViewDescription
            {
                Format = DxgiFormat.B8G8R8A8_UNorm,
                ViewDimension = RenderTargetViewDimension.Texture2D
            }, handle);
        }
    }

    private void CreateDepthBuffer()
    {
        // D24S8 (no D32): mismo formato que Direct3D 11, así las dos APIs miden el MISMO trabajo
        // de raster sobre el mismo tipo de superficie.
        var description = new ResourceDescription(
            ResourceDimension.Texture2D,
            0,
            (ulong)_width,
            (uint)_height,
            1,
            1,
            DxgiFormat.D24_UNorm_S8_UInt,
            1,
            0,
            TextureLayout.Unknown,
            ResourceFlags.AllowDepthStencil);
        _depthTexture = _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            description,
            ResourceStates.DepthWrite,
            new ClearValue(DxgiFormat.D24_UNorm_S8_UInt, new DepthStencilValue(1f, 0)));

        _dsvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.DepthStencilView,
            DescriptorCount = 1
        });
        _device.CreateDepthStencilView(_depthTexture, new DepthStencilViewDescription
        {
            Format = DxgiFormat.D24_UNorm_S8_UInt,
            ViewDimension = DepthStencilViewDimension.Texture2D
        }, _dsvHeap.GetCPUDescriptorHandleForHeapStart());
    }

    private void CreatePipelines()
    {
        // Root signature: un CBV en b0 para las constantes del frame y una tabla de un descriptor
        // en t1 para la franja de métricas. La geometría por instancia va por el input layout,
        // igual que en Direct3D 11.
        // El SRV de la franja va por TABLA y no como descriptor de raíz: el stride de un buffer
        // estructurado vive en el descriptor (el root SRV es solo una dirección), y sin ese dato la
        // lectura queda indefinida. La tabla cuesta un descriptor en un heap y dos llamadas por
        // frame (atar el heap y atar la tabla).
        var parameters = new[]
        {
            new RootParameter(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            // El tipo lo deduce Vortice de la tabla (el constructor de tablas no lo pide).
            new RootParameter(
                new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 1)),
                ShaderVisibility.All),
            // Registro t0: la textura de detalle. Va como tabla porque una textura por descriptor
            // de raíz no existe — las texturas SIEMPRE viajan por tabla de descriptores. El registro
            // base es 0 (el HLSL dice register(t0); el corrimiento a bindings es cosa de Vulkan, no
            // de acá) y el descriptor vive en la ranura 1 del heap, después de la franja en la 0.
            new RootParameter(
                new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)),
                ShaderVisibility.All),
            // Registro s0: el sampler de la textura de detalle. Los samplers viajan en UN heap de
            // tipo Sampler atado junto al CBV_SRV_UAV (D3D12 permite uno de cada tipo por frame).
            new RootParameter(
                new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.Sampler, 1, 0)),
                ShaderVisibility.All),
            // Registro t4: el arreglo de materiales de la escena (albedo + normales + ARM de TODOS
            // los materiales, ver Scenes.MaterialAtlas). Es UNA textura para la escena entera y se
            // ata una vez por frame: el material de cada objeto es un índice que viaja por instancia,
            // así que no hace falta tocar nada por dibujo.
            //
            // OJO con el TERCER argumento: es el REGISTRO DEL SHADER (el t4 del HLSL), no la ranura
            // del heap. La ranura se elige al atar la tabla (el handle que se le pasa a
            // SetGraphicsRootDescriptorTable). Confundir los dos hace fallar la creación del pipeline
            // con E_INVALIDARG, porque el registro t4 queda sin ningún rango que lo cubra.
            new RootParameter(
                new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 4)),
                ShaderVisibility.All),
            // Registro s1: el sampler de ese arreglo (la ranura 1 del heap de samplers).
            new RootParameter(
                new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.Sampler, 1, 1)),
                ShaderVisibility.All),
            // Registro t6: el shadow map (ver README-SOMBRAS.md).
            //
            // El rango va SIEMPRE, aunque la escena no use sombras: la firma raíz tiene que cubrir
            // TODOS los registros que declara el shader compilado, porque eso se valida al crear el
            // pipeline. Si falta, la creación falla con E_INVALIDARG y el backend no dibuja NADA
            // (no es una advertencia: es un error).
            new RootParameter(
                new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 6)),
                ShaderVisibility.All),
            // Registro s3: el sampler de comparación de ese mapa.
            new RootParameter(
                new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.Sampler, 1, 3)),
                ShaderVisibility.All)
        };
        var rootSignatureDescription = new RootSignatureDescription(
            RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, null);
        // Versión 1.0 y no 1.1: la serialización la hace D3D12SerializeRootSignature, que solo
        // acepta 1.0 (con 1.1 tira "unsupported root signature version"). El root signature de
        // esta escena —un CBV de root— no necesita nada de 1.1.
        _rootSignature = _device.CreateRootSignature(rootSignatureDescription, RootSignatureVersion.Version10);

        string common = SceneShaders.Common;
        byte[] geometryVs = Compile(common + SceneShaders.GeometryVertexShader, "VSMain", SceneShaders.VertexProfile);
        byte[] geometryPs = Compile(common + SceneShaders.GeometryVertexShader + SceneShaders.GeometryPixelShader, "PSMain", SceneShaders.PixelProfile);
        byte[] skyVs = Compile(common + SceneShaders.SkyVertexShader, "VSSky", SceneShaders.VertexProfile);
        byte[] overlayVs = Compile(common + SceneShaders.SkyOverlayVertexShader, "VSSkyOverlay", SceneShaders.VertexProfile);
        byte[] skyPs = Compile(common + SceneShaders.SkyVertexShader + SceneShaders.SkyPixelShader, "PSSky", SceneShaders.PixelProfile);

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

        _geometryPipeline = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = geometryVs,
            PixelShader = geometryPs,
            InputLayout = new InputLayoutDescription(elements),
            BlendState = BlendDescription.Opaque,
            // CullNone: mismo motivo que en Direct3D 11 — el winding de la malla procedural no
            // es contrato, y el cull por defecto se comió la escena entera (pantalla negra).
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.Default,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { DxgiFormat.B8G8R8A8_UNorm },
            DepthStencilFormat = DxgiFormat.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0)
        });

        // PASADA DE SOMBRAS (ver README-SOMBRAS.md): los MISMOS shaders de la geometría, con CERO
        // render targets y SIN pixel shader —la placa solo rasteriza y escribe profundidad—, contra el
        // mapa de profundidad de 2048². Nada de esto toca el HLSL: la pasada cambia la MATRIZ del
        // bloque de constantes (la de la luz en vez de la de la cámara) y el resto es este objeto.
        //
        // DepthStencilFormat es el del MAPA (D32_Float) y no el de la ventana: la PSO y el recurso que
        // se ata tienen que coincidir, y el mapa es una textura aparte.
        _shadowPipeline = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = geometryVs,
            PixelShader = null,
            InputLayout = new InputLayoutDescription(elements),
            BlendState = BlendDescription.Opaque,
            // El mismo cull que la pasada principal: si acá se colara el estado por defecto, media
            // escena dejaría de proyectar.
            RasterizerState = RasterizerDescription.CullNone,
            // Por defecto: prueba "menor" y ESCRITURA de profundidad, que es todo lo que hace esta pasada.
            DepthStencilState = DepthStencilDescription.Default,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = Array.Empty<DxgiFormat>(),
            DepthStencilFormat = DxgiFormat.D32_Float,
            SampleDescription = new SampleDescription(1, 0)
        });

        // Fondo: sin prueba de profundidad ni escritura y sin culling (es un triángulo de pantalla
        // completa generado por SV_VertexID, sin input layout).
        _skyPipeline = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = skyVs,
            PixelShader = skyPs,
            BlendState = BlendDescription.Opaque,
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.None,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { DxgiFormat.B8G8R8A8_UNorm },
            DepthStencilFormat = DxgiFormat.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0)
        });

        // Franja de métricas: mismo pixel shader que el fondo pero con su propio vertex shader —el
        // quad se arma desde el vértice 0, sin depender del vértice base ni del instance id, que es
        // justo lo que cambia entre APIs— más el blend premultiplicado (One / InverseSourceAlpha, el
        // mismo de Direct3D 11). En Direct3D 12 el blend es estado del PIPELINE, así que además
        // necesita su propia pipeline para no mezclarlo con el fondo.
        _overlayPipeline = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = overlayVs,
            PixelShader = skyPs,
            BlendState = new BlendDescription(Blend.One, Blend.InverseSourceAlpha),
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.None,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { DxgiFormat.B8G8R8A8_UNorm },
            DepthStencilFormat = DxgiFormat.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0)
        });

        // Partículas (el polen): los MISMOS shaders que la geometría pero con blend premultiplicado
        // y sin escritura de profundidad. En Direct3D 12 el blend y la profundidad son estado del
        // pipeline, así que la pasada transparente necesita la suya (es el único costo real de
        // agregar partículas en esta API).
        _blendPipeline = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = geometryVs,
            PixelShader = geometryPs,
            InputLayout = new InputLayoutDescription(elements),
            BlendState = new BlendDescription(Blend.One, Blend.InverseSourceAlpha),
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.Default with
            {
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.LessEqual
            },
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { DxgiFormat.B8G8R8A8_UNorm },
            DepthStencilFormat = DxgiFormat.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0)
        });
    }

    /// <summary>
    /// Buffer de subida con los píxeles de la franja (un uint por píxel). Se rehace solo cuando
    /// cambia el tamaño del panel: la GPU tiene que estar quieta —el buffer anterior puede estar
    /// en uso por un frame ya enviado— y eso pasa una o dos veces por corrida.
    /// </summary>
    private void CreateOverlayBuffer(int pixelCount)
    {
        if (_device == null) return;
        WaitForGpuIdle();

        DxgiShared.SafeDispose(_overlayBuffer);
        _overlayPixelCount = Math.Max(1, pixelCount);
        _overlayBuffer = CreateUploadBuffer((ulong)_overlayPixelCount * OverlayPixelStride);

        // La vista tiene que declarar el stride (4 = un uint por píxel) y cuántos elementos hay:
        // es lo que el descriptor de raíz no podía decir y por eso el shader no podía indexar.
        if (_overlaySrvHeap == null)
        {
            // CUATRO ranuras: la franja (0), la textura de detalle (1, la escribe
            // CreateDetailTexture), el arreglo de materiales de la escena (2, la escribe
            // CreateMaterialTextures) y el shadow map (3, lo escribe CreateShadowResources).
            _overlaySrvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
            {
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                DescriptorCount = 4,
                Flags = DescriptorHeapFlags.ShaderVisible
            });
            _srvDescriptorSize = _device.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        }
        _device.CreateShaderResourceView(_overlayBuffer, new ShaderResourceViewDescription
        {
            // Obligatorio: Direct3D 12 valida este campo y el cero por defecto es una llamada
            // inválida (medido: la placa se cayó justo al crear la vista, sin mensaje). El valor
            // documental D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING es 0x1688 = componentes 0,1,2,3.
            Shader4ComponentMapping = D3D12DefaultShader4ComponentMapping,
            Format = DxgiFormat.Unknown,
            // Nombre completo: Vortice.Direct3D (el de los feature levels) tiene uno igual.
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = 0,
                NumElements = (uint)_overlayPixelCount,
                StructureByteStride = OverlayPixelStride,
                Flags = BufferShaderResourceViewFlags.None
            }
        }, _overlaySrvHeap.GetCPUDescriptorHandleForHeapStart());
        _overlayDirty = true;
    }

    /// <summary>
    /// Textura de detalle (el atlas de material de <see cref="Scenes.SceneDetailTexture"/>): imagen
    /// RGBA8 en el heap Upload — que ya nace en GENERIC_READ, estado de lectura de shader válido —
    /// con SRV en la ranura 1 del heap CBV_SRV_UAV de la franja y sampler en heap propio. Igual que
    /// en Direct3D 11 y Vulkan, se sube UNA vez al inicializar y no se vuelve a tocar.
    /// </summary>
    private void CreateDetailTexture()
    {
        if (_device == null) return;
        var pixels = Scenes.SceneDetailTexture.Pixels;
        int size = Scenes.SceneDetailTexture.Size;

        // Destino en heap Default, naciendo en CopyDest (estado de destino de copia).
        _detailTexture = _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Texture2D(DxgiFormat.R8G8B8A8_UNorm, (uint)size, (uint)size, 1, 1),
            ResourceStates.CopyDest,
            null);
        // Origen en el heap Upload: el CPU lo llena con un mapa (Write) y la GPU lo copia.
        _detailUpload = CreateUploadBuffer((ulong)(size * size * 4));
        Write(_detailUpload, pixels);

        // Ranura 1 del heap CBV_SRV_UAV (la 0 es la franja). La vista es TEXTURE2D (no BUFFER):
        // el HLSL declara Texture2D, no ByteAddressBuffer.
        _device.CreateShaderResourceView(_detailTexture, new ShaderResourceViewDescription
        {
            Shader4ComponentMapping = D3D12DefaultShader4ComponentMapping,
            Format = DxgiFormat.R8G8B8A8_UNorm,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                PlaneSlice = 0,
                ResourceMinLODClamp = 0f
            }
        }, _overlaySrvHeap!.GetCPUDescriptorHandleForHeapStart().Offset(1, _srvDescriptorSize));

        // El sampler vive en un heap de tipo Sampler: D3D12 exige UN heap de cada tipo atado por
        // frame, y el CBV_SRV_UAV ya está ocupado por la franja y el atlas.
        // TRES samplers: el detalle (0), el arreglo de materiales (1, lo escribe CreateMaterialTextures)
        // y el de COMPARACIÓN del shadow map (2, lo escribe CreateShadowResources).
        _detailSamplerHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.Sampler,
            DescriptorCount = 3,
            Flags = DescriptorHeapFlags.ShaderVisible
        });
        var detailSamplerDescription = new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0f,
            MaxLOD = float.MaxValue
        };
        // La firma de Vortice pide la descripción por referencia.
        _device.CreateSampler(ref detailSamplerDescription, _detailSamplerHeap.GetCPUDescriptorHandleForHeapStart());
        // Paso entre descriptores del heap de samplers: lo necesita el arreglo de materiales para
        // escribir el suyo en la ranura 1.
        _samplerDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);

        // Subida: copia por cola de comandos y barrera de CopyDest a lectura de shader. La lista ya
        // existe (CreateSwapChain la crea y la cierra) y en Initialize nadie la está usando.
        _commandList.Reset(_allocators[0], null);
        // La copia a una textura va por CopyTextureRegion con el footprint del subrecurso: el
        // stride 256 lo computa GetCopyableFootprints del driver (alineado, con margen). La firma
        // de Vortice pide la localización por referencia.
        // Destino: la textura, subrecurso 0. Origen: el buffer de paso, con el footprint del
        // subrecurso. RowPitch alineado a 256 (D3D12_TEXTURE_DATA_PITCH_ALIGNMENT): 256 px × 4 B
        // = 1024, ya alineado de fábrica. La firma de Vortice es (dst, x, y, z, src, rect).
        var destination = new TextureCopyLocation(_detailTexture, 0);
        var source = new TextureCopyLocation(_detailUpload, new PlacedSubresourceFootPrint
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(DxgiFormat.R8G8B8A8_UNorm, (uint)size, (uint)size, 1, (uint)(size * 4))
        });
        _commandList.CopyTextureRegion(destination, 0, 0, 0, source, null);
        _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            _detailTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource, 0, ResourceBarrierFlags.None));
        _commandList.Close();
        _queue.ExecuteCommandLists(new ID3D12CommandList[] { _commandList });
        WaitForGpuIdle();
    }

    /// <summary>
    /// Sube el atlas de materiales de la escena (ver <see cref="Scenes.MaterialAtlas"/>): un arreglo 2D
    /// con tres rebanadas por material y todos sus mips, generados en el CPU. La imagen nace en
    /// COPY_DEST, se copia subrecurso por subrecurso desde un buffer de paso y termina en lectura de
    /// shader. El sampler es el mismo del detalle (lineal, con wrap): una textura de material se repite
    /// sobre el mundo igual que el atlas de detalle.
    /// </summary>
    private void CreateMaterialTextures()
    {
        if (_device == null) return;
        var atlas = _scene.Materials;

        _materialTexture = _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Texture2D(
                DxgiFormat.R8G8B8A8_UNorm, (uint)atlas.Size, (uint)atlas.Size,
                (ushort)atlas.SliceCount, (ushort)atlas.MipCount),
            ResourceStates.CopyDest,
            null);

        // Buffer de paso con una entrada por subrecurso: la fila de una copia a textura tiene que
        // estar alineada a 256 bytes (D3D12_TEXTURE_DATA_PITCH_ALIGNMENT), y el atlas guarda los
        // mips de cada rebanada CONTIGUOS, así que acá se reacomodan con el relleno que pide la API.
        // Dos pasadas: la primera mide (el relleno de las filas de los mips chicos pesa más que los
        // propios píxeles), la segunda copia. Dimensionar el buffer con el tamaño del atlas a secas
        // deja la última rebanada fuera del arreglo: el relleno de CADA fila suma.
        const int pitchAlignment = 256;
        var footprints = new (int SourceOffset, int RowPitch, int Side)[atlas.SliceCount * atlas.MipCount];
        int total = 0;
        for (int slice = 0; slice < atlas.SliceCount; slice++)
        {
            for (int level = 0; level < atlas.MipCount; level++)
            {
                int side = atlas.MipSize(level);
                int rowPitch = (side * 4 + pitchAlignment - 1) / pitchAlignment * pitchAlignment;
                footprints[slice * atlas.MipCount + level] =
                    (slice * atlas.SliceBytes + atlas.MipOffsets[level], rowPitch, side);
                total += rowPitch * side;
            }
        }

        var staging = new byte[total];
        int offset = 0;
        for (int index = 0; index < footprints.Length; index++)
        {
            var (sliceSource, rowPitch, side) = footprints[index];
            int rowBytes = side * 4;
            for (int row = 0; row < side; row++)
            {
                atlas.Pixels.AsSpan(sliceSource + row * rowBytes, rowBytes)
                    .CopyTo(staging.AsSpan(offset + row * rowPitch, rowBytes));
            }
            offset += rowPitch * side;
        }

        _materialUpload = CreateUploadBuffer((ulong)total);
        Write(_materialUpload, staging);

        // SRV de la ranura 2 del heap: arreglo 2D con todas las rebanadas y todos los mips.
        _device.CreateShaderResourceView(_materialTexture, new ShaderResourceViewDescription
        {
            Shader4ComponentMapping = D3D12DefaultShader4ComponentMapping,
            Format = DxgiFormat.R8G8B8A8_UNorm,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = (uint)atlas.MipCount,
                FirstArraySlice = 0,
                ArraySize = (uint)atlas.SliceCount,
                PlaneSlice = 0,
                ResourceMinLODClamp = 0f
            }
        }, _overlaySrvHeap!.GetCPUDescriptorHandleForHeapStart().Offset(2, _srvDescriptorSize));

        var materialSamplerDescription = new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0f,
            MaxLOD = float.MaxValue
        };
        _device.CreateSampler(
            ref materialSamplerDescription,
            _detailSamplerHeap!.GetCPUDescriptorHandleForHeapStart().Offset(1, _samplerDescriptorSize));

        // Copia subrecurso por subrecurso con las barreras de estado.
        _commandList.Reset(_allocators[0], null);
        ulong source = 0;
        for (int slice = 0; slice < atlas.SliceCount; slice++)
        {
            for (int level = 0; level < atlas.MipCount; level++)
            {
                var (_, rowPitch, side) = footprints[slice * atlas.MipCount + level];
                uint subresource = (uint)(level + slice * atlas.MipCount);
                var destination = new TextureCopyLocation(_materialTexture, subresource);
                var origin = new TextureCopyLocation(_materialUpload, new PlacedSubresourceFootPrint
                {
                    Offset = source,
                    Footprint = new SubresourceFootPrint(
                        DxgiFormat.R8G8B8A8_UNorm, (uint)side, (uint)side, 1, (uint)rowPitch)
                });
                _commandList.CopyTextureRegion(destination, 0, 0, 0, origin, null);
                source += (ulong)(rowPitch * side);
            }
        }
        // Transición del arreglo COMPLETO: con el subrecurso en 0 y "un solo subrecurso" la barrera
        // es de la imagen entera, que es justo lo que hace falta después de copiar todos los mips.
        _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            _materialTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource, 0,
            ResourceBarrierFlags.None));
        _commandList.Close();
        _queue.ExecuteCommandLists(new ID3D12CommandList[] { _commandList });
        WaitForGpuIdle();
    }

    /// <summary>
    /// Recurso del shadow map en Direct3D 12 (ver <c>README-SOMBRAS.md</c>): el mapa de profundidad de
    /// 2048² con su vista de profundidad (para la pasada) y su vista de recursos (para el sombreado),
    /// más el sampler de COMPARACIÓN que devuelve 0/1 ya filtrado.
    ///
    /// El formato del recurso es <c>R32_Typeless</c>: una textura de profundidad no se puede muestrear
    /// con su propio formato, así que la vista de profundidad la toma como <c>D32_Float</c> y la de
    /// sombreado como <c>R32_Float</c>.
    ///
    /// El recurso NACE en <c>PixelShaderResource</c> a propósito: así la pasada hace la misma
    /// transición en todos los frames (lectura → escritura al empezar, escritura → lectura al
    /// terminar) y no hay un caso especial para el primero. Mientras no haya pasada (una escena sin
    /// sombras) el estado de lectura es también el que el shader necesita.
    /// </summary>
    private void CreateShadowResources()
    {
        if (_device == null) return;

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
        // La descripción va armada a mano —y no con el ayudante ResourceDescription.Texture2D— por
        // una sola razón: hace falta ResourceFlags.AllowDepthStencil. Sin esa bandera, el recurso que
        // lleva un valor de borrado de PROFUNDIDAD no es válido, y D3D12 lo rechaza con
        // E_INVALIDARG sin explicar cuál de todos los argumentos está mal (es el mismo error que
        // daba el mapa antes de que esta llamada lo declarara: el dispositivo no arrancaba).
        var shadowDescription = new ResourceDescription(
            ResourceDimension.Texture2D,
            0,
            size,
            size,
            1,
            1,
            DxgiFormat.R32_Typeless,
            1,
            0,
            TextureLayout.Unknown,
            ResourceFlags.AllowDepthStencil);
        _shadowTexture = _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            shadowDescription,
            ResourceStates.PixelShaderResource,
            new ClearValue(DxgiFormat.D32_Float, new DepthStencilValue(1f, 0)));

        _shadowDsvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.DepthStencilView,
            DescriptorCount = 1
        });
        _device.CreateDepthStencilView(_shadowTexture, new DepthStencilViewDescription
        {
            Format = DxgiFormat.D32_Float,
            ViewDimension = DepthStencilViewDimension.Texture2D
        }, _shadowDsvHeap.GetCPUDescriptorHandleForHeapStart());

        // Ranura 3 del heap CBV_SRV_UAV (las otras tres son la franja, el detalle y los materiales).
        _device.CreateShaderResourceView(_shadowTexture, new ShaderResourceViewDescription
        {
            Shader4ComponentMapping = D3D12DefaultShader4ComponentMapping,
            Format = DxgiFormat.R32_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                PlaneSlice = 0,
                ResourceMinLODClamp = 0f
            }
        }, _overlaySrvHeap!.GetCPUDescriptorHandleForHeapStart().Offset(3, _srvDescriptorSize));

        // Ranura 2 del heap de samplers (la 0 es el detalle y la 1 los materiales).
        var samplerDescription = new SamplerDescription
        {
            // ComparisonMinMagMipLinear: cada muestra del PCF promedia 4 téxeles en el hardware, el
            // mismo ablandado de borde que el filtro lineal de los otros tres backends.
            Filter = Filter.ComparisonMinMagMipLinear,
            // Clamp: el mapa no se repite. Si el filtrado llegara al borde, repetir traería la
            // profundidad del lado opuesto del mundo.
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            // 'Less': iluminado cuando la profundidad del píxel es MENOR que la del mapa. La misma
            // convención que Direct3D 11, Vulkan y GL_COMPARE_REF_TO_TEXTURE con GL_LESS.
            ComparisonFunction = ComparisonFunction.Less,
            MinLOD = 0f,
            MaxLOD = float.MaxValue
        };
        _device.CreateSampler(
            ref samplerDescription,
            _detailSamplerHeap!.GetCPUDescriptorHandleForHeapStart().Offset(2, _samplerDescriptorSize));
    }

    /// <summary>Copia los píxeles pendientes al buffer (una vez por cambio, no por frame).</summary>
    private void UpdateOverlayBuffer()
    {
        if (!_overlayDirty || _overlayPending == null || _overlayBuffer == null) return;
        _overlayDirty = false;

        int bytes = Math.Min(_overlayPending.Length, _overlayPixelCount * 4);
        // El mismo camino que la geometría: el buffer vive en el heap Upload, así que escribirlo
        // desde el CPU es un SetData (mapa + copia) del tramo que cambió.
        Write(_overlayBuffer, _overlayPending.AsSpan(0, bytes));
    }

    /// <summary>
    /// Bytecode del shader: primero la caché en disco (compartida con Direct3D 11, que compila con
    /// el mismo perfil contra el mismo d3dcompiler) y, si no está, se compila y se guarda.
    /// </summary>
    private static byte[] Compile(string source, string entryPoint, string profile)
    {
        byte[]? cached = ShaderCache.TryRead(source, entryPoint, profile);
        if (cached != null) return cached;

        var result = Compiler.Compile(source, entryPoint, "escena", profile, out var blob, out var errors);
        if (result.Failure || blob == null)
        {
            string message = errors != null ? errors.AsString() : result.Description;
            DxgiShared.SafeDispose(errors);
            DxgiShared.SafeDispose(blob);
            throw new InvalidOperationException($"No se pudo compilar el shader '{entryPoint}': {message}");
        }
        DxgiShared.SafeDispose(errors);

        byte[] bytecode;
        using (blob)
        {
            bytecode = blob.AsBytes().ToArray();
        }
        ShaderCache.Write(source, entryPoint, profile, bytecode);
        return bytecode;
    }

    private void CreateGeometryBuffers()
    {
        _vertexBuffers = new ID3D12Resource[_scene.Meshes.Count];
        _instanceBuffers = new ID3D12Resource[_scene.Meshes.Count];
        _vertexBufferSizes = new ulong[_scene.Meshes.Count];
        _instanceBufferSizes = new ulong[_scene.Meshes.Count];
        for (int mesh = 0; mesh < _scene.Meshes.Count; mesh++)
        {
            var geometry = _scene.Meshes[mesh];
            // RenderInstances y no Instances: el material de la malla ya está horneado en cada
            // instancia (tinta, fuerzas y ranura del atlas). Ver SceneMesh.RenderInstances.
            var instances = geometry.RenderInstances;
            _vertexBufferSizes[mesh] = (ulong)geometry.Vertices.Length * VertexStride;
            _instanceBufferSizes[mesh] = (ulong)instances.Length * InstanceStride;
            _vertexBuffers[mesh] = CreateUploadBuffer(_vertexBufferSizes[mesh]);
            _instanceBuffers[mesh] = CreateUploadBuffer(_instanceBufferSizes[mesh]);
            Write(_vertexBuffers[mesh], MemoryMarshal.AsBytes(geometry.Vertices.AsSpan()));
            Write(_instanceBuffers[mesh], MemoryMarshal.AsBytes(instances.AsSpan()));
        }

        // Constantes: dos rebanadas por frame en vuelo (sombras y pasada principal), alineadas a 256.
        _constantBuffer = CreateUploadBuffer((ulong)(ConstantBufferSlice * FramesInFlight * ConstantSlicesPerFrame));
    }

    // El tercer argumento de ResourceDescription.Buffer es la ALINEACIÓN del recurso (no el stride):
    // para un buffer solo vale 0 o 64 KB, y cualquier otro valor es E_INVALIDARG. El stride de un
    // buffer estructurado se declara SOLO en la vista (BufferShaderResourceView.StructureByteStride),
    // así que el recurso va siempre con alineación 0.
    private ID3D12Resource CreateUploadBuffer(ulong size) => _device.CreateCommittedResource(
        HeapType.Upload,
        HeapFlags.None,
        ResourceDescription.Buffer(size, ResourceFlags.None, 0),
        ResourceStates.GenericRead,
        null);

    private static void Write(ID3D12Resource resource, ReadOnlySpan<byte> data)
    {
        // El buffer vive en el heap Upload: escribirlo desde el CPU es un mapa+memcpy+unmap que
        // Vortice resuelve con SetData.
        resource.SetData(data, 0);
    }

    private void CreateFence()
    {
        _fence = _device.CreateFence(0, FenceFlags.None);
        _fenceEvent = new AutoResetEvent(false);
        for (int i = 0; i < FramesInFlight; i++) _fenceValues[i] = InvalidFenceValue;
    }

    private void CreateTiming()
    {
        // Dos timestamps por ranura (inicio y fin del trabajo de GPU del frame).
        _queryHeap = _device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, FramesInFlight * 2, 0));
        _timestampReadback = _device.CreateCommittedResource(
            HeapType.Readback,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)(sizeof(long) * FramesInFlight * 2), ResourceFlags.None, 0),
            ResourceStates.CopyDest,
            null);

        if (_queue.GetTimestampFrequency(out ulong frequency).Success && frequency > 0)
        {
            _timestampFrequency = frequency;
        }
        else
        {
            _timestampFrequency = 0;
        }
    }

    private void UpdateViewport() => _commandList?.RSSetViewport(new Viewport(0f, 0f, _width, _height, 0f, 1f));

    // =====================================================================
    // Frame
    // =====================================================================

    public void Resize(int width, int height)
    {
        width = Math.Max(64, width);
        height = Math.Max(64, height);
        if (width == _width && height == _height) return;

        WaitForGpuIdle();
        _width = width;
        _height = height;

        for (int i = 0; i < FramesInFlight; i++)
        {
            DxgiShared.SafeDispose(_renderTargets[i]);
            _renderTargets[i] = null!;
        }
        DxgiShared.SafeDispose(_depthTexture);

        // Mismos flags que la creación (AllowTearing incluido): con flags distintos ResizeBuffers
        // falla con DXGI_ERROR_INVALID_CALL.
        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, DxgiFormat.Unknown,
            SwapChainFlags.AllowModeSwitch | SwapChainFlags.AllowTearing);

        CreateRenderTargets();
        CreateDepthBuffer();
        _backBufferIndex = _swapChain.CurrentBackBufferIndex;
        UpdateViewport();
    }

    public void SetFullscreen(bool exclusive)
    {
        try
        {
            _swapChain.SetFullscreenState(exclusive, null);
            _exclusiveFullscreen = exclusive;
        }
        catch
        {
            _exclusiveFullscreen = false;
            ExclusiveFullscreenAccepted = false;
        }
    }

    public void RenderFrame(double timeSeconds)
    {
        if (DeviceLost) return;
        _sceneTime = timeSeconds;

        int slot = (int)(_backBufferIndex % FramesInFlight);

        // Un frame en vuelo: esperar a que la GPU TERMINE el frame anterior antes de grabar el
        // próximo. En un benchmark esto es a propósito: con 3 en vuelo el CPU se adelanta, el
        // present satura y los frametimes quedan en ráfagas (0,1 ms, 0,1 ms, 21 ms…): hitches
        // falsos y un FPS "máx" que no significa nada. Serializando, el frametime ES el tiempo
        // de GPU: mediana estable, hitches reales, y el límite detectado dice GPU cuando la
        // placa es el cuello. La espera se mide aparte (no es trabajo del CPU).
        long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        WaitForPreviousFrame();
        LastQueueWaitMs = (System.Diagnostics.Stopwatch.GetTimestamp() - waitStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        CollectGpuTiming(slot);

        // La franja: la placa terminó el frame anterior (la espera de arriba), así que copiar los
        // píxeles nuevos no puede pisar una lectura en curso.
        if (HasOverlay) UpdateOverlayBuffer();

        _allocators[slot].Reset();
        _commandList.Reset(_allocators[slot], null);
        _commandList.SetGraphicsRootSignature(_rootSignature);

        var renderTarget = _renderTargets[_backBufferIndex];
        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart().Offset((int)_backBufferIndex, _rtvDescriptorSize);
        var dsvHandle = _dsvHeap.GetCPUDescriptorHandleForHeapStart();

        // El buffer del swapchain está en Present entre frames: hay que devolverlo a render target.
        _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            renderTarget, ResourceStates.Present, ResourceStates.RenderTarget, 0, ResourceBarrierFlags.None));
        _commandList.OMSetRenderTargets(rtvHandle, dsvHandle);

        // Los timestamps de D3D12 se cierran con EndQuery (no hay versión "disjoint": D3D12 no
        // reporta saltos del reloj de la GPU, así que un cambio de frecuencia EN MEDIO de una
        // corrida no se puede detectar y el dato de ese frame queda distorsionado).
        if (_timestampFrequency > 0)
        {
            _commandList.EndQuery(_queryHeap, QueryType.Timestamp, (uint)(slot * 2));
        }

        // ---- Pasada de sombras (ver README-SOMBRAS.md) ----
        // Adentro de los timestamps (ENDQUERY de arriba) porque ES trabajo del frame. Deja el render
        // target apuntando a su propio mapa de profundidad, así que la pasada principal lo vuelve a
        // atar en la línea de abajo.
        if (_shadowEnabled) RecordShadowPass(slot);
        _commandList.OMSetRenderTargets(rtvHandle, dsvHandle);

        _commandList.ClearRenderTargetView(rtvHandle, new Color4(0.02f, 0.02f, 0.03f, 1f));
        _commandList.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1f, 0);
        _commandList.RSSetViewport(new Viewport(0f, 0f, _width, _height, 0f, 1f));
        // El scissor NO tiene estado heredado entre frames: después de un Reset de la lista el
        // rect por defecto es VACÍO y la GPU recorta TODOS los píxeles. Sin esta línea la escena
        // se ve negra con la placa trabajando al 99 % (fue el bug original del backend).
        _commandList.RSSetScissorRect(new RectI(0, 0, _width, _height));
        _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Los root arguments se atan ANTES del primer dibujo: el fondo ya lee las constantes (el
        // rectángulo de la franja) y su pixel shader declara el t1 de la franja. Atarlos después
        // del dibujo deja al cielo leyendo descriptores indefinidos (medido: la placa se cayó).
        UpdateConstants(slot, timeSeconds);
        // Rebanada de la pasada PRINCIPAL: la de la pasada de sombras (slot * 2) la escribió
        // RecordShadowPass y la leyó esa misma pasada.
        _commandList.SetGraphicsRootConstantBufferView(
            0, _constantBuffer.GPUVirtualAddress + (ulong)((slot * ConstantSlicesPerFrame + 1) * ConstantBufferSlice));
        if (_overlaySrvHeap != null)
        {
            // El heap tiene que estar ATADO a la lista de comandos antes de que una tabla apunte
            // adentro: sin esta línea la llamada nativa siguiente revienta con una violación de
            // acceso (medido: 0xC0000005 dentro de SetGraphicsRootDescriptorTable). El heap se
            // vuelve a atar por frame porque el estado no sobrevive al Reset de la lista.
            _commandList.SetDescriptorHeaps(new[] { _overlaySrvHeap, _detailSamplerHeap! });
            _commandList.SetGraphicsRootDescriptorTable(1, _overlaySrvHeap.GetGPUDescriptorHandleForHeapStart());
            // Tabla del registro t0: la ranura 1 del mismo heap (la franja está en la 0).
            _commandList.SetGraphicsRootDescriptorTable(2, _overlaySrvHeap.GetGPUDescriptorHandleForHeapStart().Offset(1, _srvDescriptorSize));
            // Tabla del registro s0: el heap de samplers.
            if (_detailSamplerHeap != null)
            {
                _commandList.SetGraphicsRootDescriptorTable(3, _detailSamplerHeap.GetGPUDescriptorHandleForHeapStart());
                // Tabla del registro t4: el arreglo de materiales (ranura 2 del heap de SRV).
                _commandList.SetGraphicsRootDescriptorTable(
                    4, _overlaySrvHeap.GetGPUDescriptorHandleForHeapStart().Offset(2, _srvDescriptorSize));
                // Tabla del registro s1: el sampler del arreglo (ranura 1 del heap de samplers).
                _commandList.SetGraphicsRootDescriptorTable(
                    5, _detailSamplerHeap.GetGPUDescriptorHandleForHeapStart().Offset(1, _samplerDescriptorSize));

                // Tabla 6 (t6, el shadow map): ranura 3 del heap, donde CreateShadowResources dejó su
                // vista de recursos. Una escena sin sombras la tiene igual (el descriptor apunta al
                // mapa aunque el shader no lo lea): un registro declarado en el HLSL tiene que estar
                // cubierto en TODO frame, y atarlo siempre es más simple que atarlo "si hace falta".
                _commandList.SetGraphicsRootDescriptorTable(
                    6, _overlaySrvHeap.GetGPUDescriptorHandleForHeapStart().Offset(3, _srvDescriptorSize));
                // Tabla 7 (s3, su sampler de comparación): ranura 2 del heap de samplers.
                _commandList.SetGraphicsRootDescriptorTable(
                    7, _detailSamplerHeap.GetGPUDescriptorHandleForHeapStart().Offset(2, _samplerDescriptorSize));
            }
        }

        // ---- Fondo ----
        _commandList.SetPipelineState(_skyPipeline);
        _commandList.DrawInstanced(3, 1, 0, 0);

        // ---- Geometría instanciada ----
        _commandList.SetPipelineState(_geometryPipeline);
        for (int mesh = 0; mesh < _vertexBuffers.Length; mesh++)
        {
            // La malla con mezcla (el polen) va ÚLTIMA y con su pipeline: blend premultiplicado y
            // sin escribir profundidad (ver SceneMesh.Blend).
            bool blend = _scene.Meshes[mesh].Blend;
            if (blend) _commandList.SetPipelineState(_blendPipeline);

            _commandList.IASetVertexBuffers(0, new[]
            {
                new VertexBufferView(_vertexBuffers[mesh].GPUVirtualAddress, (uint)_vertexBufferSizes[mesh], VertexStride),
                new VertexBufferView(_instanceBuffers[mesh].GPUVirtualAddress, (uint)_instanceBufferSizes[mesh], InstanceStride)
            });
            _commandList.DrawInstanced((uint)_meshVertexCounts[mesh], (uint)_meshInstanceCounts[mesh], 0, 0);

            if (blend) _commandList.SetPipelineState(_geometryPipeline);
        }

        // ---- Franja de métricas (dentro del frame) ----
        // Última pasada: va SOBRE la escena, con los mismos shaders del fondo, sin profundidad y
        // con el blend premultiplicado. Los píxeles llegan por la tabla de descriptores atada arriba.
        if (HasOverlay && _overlayBuffer != null)
        {
            _commandList.SetPipelineState(_overlayPipeline);
            // 6 vértices desde el 0: el quad lo arma VSSkyOverlay (ver SceneShaders).
            _commandList.DrawInstanced(6, 1, 0, 0);
        }

        if (_timestampFrequency > 0)
        {
            uint endIndex = (uint)(slot * 2 + 1);
            _commandList.EndQuery(_queryHeap, QueryType.Timestamp, endIndex);
            // La GPU escribe los dos timestamps de la ranura en el buffer de lectura: recién
            // después de que esta ranura termine se pueden leer desde el CPU.
            _commandList.ResolveQueryData(
                _queryHeap, QueryType.Timestamp, (uint)(slot * 2), 2, _timestampReadback, (ulong)(slot * 2 * sizeof(long)));
            _timingPending[slot] = true;
        }

        _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            renderTarget, ResourceStates.RenderTarget, ResourceStates.Present, 0, ResourceBarrierFlags.None));

        _commandList.Close();
        _queue.ExecuteCommandLists(new ID3D12CommandList[] { _commandList });
        _queue.Signal(_fence, ++_fenceCounter);
        _fenceValues[slot] = _fenceCounter;
    }

    public double Present()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // Igual que en Direct3D 11: el flag de tearing no es legal en exclusivo (con el
        // exclusivo emulado de Direct3D 12 no hace falta, y en un sistema donde el swapchain
        // sí entre en exclusivo evita el DXGI_ERROR_INVALID_CALL del primer Present).
        var flags = !_vsync && !_exclusiveFullscreen ? PresentFlags.AllowTearing : PresentFlags.None;
        var present = _swapChain.Present(_vsync ? 1u : 0u, flags);
        double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        if (present.Failure)
        {
            DeviceLost = true;
            throw new InvalidOperationException($"Direct3D 12 no pudo presentar el frame: {present.Description}");
        }

        _backBufferIndex = _swapChain.CurrentBackBufferIndex;
        return elapsedMs;
    }

    /// <summary>
    /// Graba la pasada de PROFUNDIDAD del shadow map (ver <c>README-SOMBRAS.md</c>): la escena vista
    /// desde el sol, en su propio render target y con el vertex shader de siempre.
    ///
    /// Los dos puntos delicados de Direct3D 12 están acá y no son opcionales:
    /// <list type="number">
    /// <item>Las BARRERAS de recurso. El mapa se escribe en esta pasada (DepthWrite) y se muestrea en la
    /// principal (PixelShaderResource): sin declarar las dos transiciones, la placa puede escribir y
    /// leer el mismo téxel a la vez —basura, o un cuelgue del dispositivo—.</item>
    /// <item>Las CONSTANTES. La pasada lleva la matriz del sol y la principal la de la cámara, así que
    /// cada una tiene su rebanada del búfer (slot * 2 y slot * 2 + 1): con una sola, la segunda
    /// escritura del CPU pisaría el bloque que esta pasada todavía iba a leer.</item>
    /// </list>
    /// </summary>
    private void RecordShadowPass(int slot)
    {
        float size = _graphics.ShadowMapSize;
        var dsvHandle = _shadowDsvHeap!.GetCPUDescriptorHandleForHeapStart();

        _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            _shadowTexture!, ResourceStates.PixelShaderResource, ResourceStates.DepthWrite, 0, ResourceBarrierFlags.None));

        // SIN render target de color: la profundidad es lo único que se escribe. El scissor se fija
        // explícito porque el de la pasada principal (y el VACÍO que deja el Reset de la lista) no
        // sirven para una pasada de otro tamaño — con el rect vacío la placa recorta todos los píxeles.
        _commandList.OMSetRenderTargets(ReadOnlySpan<CpuDescriptorHandle>.Empty, dsvHandle);
        _commandList.RSSetViewport(new Viewport(0f, 0f, size, size, 0f, 1f));
        _commandList.RSSetScissorRect(new RectI(0, 0, (int)size, (int)size));
        _commandList.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1f, 0);
        _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        UpdateConstants(slot, _sceneTime, shadowPass: true);
        _commandList.SetGraphicsRootConstantBufferView(
            0, _constantBuffer.GPUVirtualAddress + (ulong)((slot * ConstantSlicesPerFrame) * ConstantBufferSlice));
        _commandList.SetPipelineState(_shadowPipeline);

        for (int mesh = 0; mesh < _vertexBuffers.Length; mesh++)
        {
            if (!_castsShadow[mesh]) continue;

            _commandList.IASetVertexBuffers(0, new[]
            {
                new VertexBufferView(_vertexBuffers[mesh].GPUVirtualAddress, (uint)_vertexBufferSizes[mesh], VertexStride),
                new VertexBufferView(_instanceBuffers[mesh].GPUVirtualAddress, (uint)_instanceBufferSizes[mesh], InstanceStride)
            });
            _commandList.DrawInstanced((uint)_meshVertexCounts[mesh], (uint)_meshInstanceCounts[mesh], 0, 0);
        }

        // El mapa queda en LECTURA, que es el estado en el que lo muestrea la pasada principal (y el
        // que espera la barrera del principio de la próxima pasada, para que la primera vuelta no sea
        // un caso especial).
        _commandList.ResourceBarrier(ResourceBarrier.BarrierTransition(
            _shadowTexture!, ResourceStates.DepthWrite, ResourceStates.PixelShaderResource, 0, ResourceBarrierFlags.None));
    }

    /// <summary>
    /// Sube el bloque del frame a la rebanada <paramref name="slot"/> de la pasada principal. Con
    /// <paramref name="shadowPass"/> en true escribe la rebanada de SOMBRAS con la matriz del SOL como
    /// <c>ViewProjection</c>: es todo lo que hace falta para que el vertex shader de la geometría
    /// dibuje la misma escena desde la luz.
    /// </summary>
    private void UpdateConstants(int slot, double timeSeconds, bool shadowPass = false)
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
            // x = segundos de la escena; w libre (el look va en Style, abajo).
            // y = CANTIDAD de luces puntuales de la escena (con cero el shader no entra al bucle).
            // z = alto del render target para las APIs que numeran las filas desde ABAJO: Direct3D
            // las numera desde arriba, que es la convención del shader (ver ScreenPixel), así que acá
            // va en 0.
            TimeAndParams = new Vector4((float)_sceneTime, _scene.PointLights.Count, 0f, 0f),
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
        _constantBuffer.SetData(constants, (slot * ConstantSlicesPerFrame + (shadowPass ? 0 : 1)) * ConstantBufferSlice);
    }

    /// <summary>
    /// Espera a que la GPU termine TODO lo encolado (un frame en vuelo; ver RenderFrame).
    /// Con la espera global garantizada, la ranura a reusar quedó libre hace 3 frames: el Reset
    /// del allocador de esa ranura es seguro sin esperar su valla individual.
    /// </summary>
    private void WaitForPreviousFrame()
    {
        if (_fenceCounter == 0) return;
        if (_fence.CompletedValue >= _fenceCounter) return;

        _fence.SetEventOnCompletion(_fenceCounter, _fenceEvent);
        if (!_fenceEvent.WaitOne(TimeSpan.FromSeconds(5)))
        {
            // Un driver colgado no puede colgar la corrida para siempre: se marca la pérdida del
            // dispositivo y el bucle de SceneHost corta la medición.
            DeviceLost = true;
            throw new InvalidOperationException("Direct3D 12 dejó de responder: la GPU no terminó el frame a tiempo.");
        }
    }

    private void WaitForGpuIdle()
    {
        try
        {
            _queue.Signal(_fence, ++_fenceCounter);
            if (_fence.CompletedValue < _fenceCounter)
            {
                _fence.SetEventOnCompletion(_fenceCounter, _fenceEvent);
                _fenceEvent.WaitOne(TimeSpan.FromSeconds(5));
            }
            for (int i = 0; i < FramesInFlight; i++) _fenceValues[i] = InvalidFenceValue;
        }
        catch
        {
            // Si esperar falla, igual se intenta seguir: el dispose es best-effort.
        }
    }

    /// <summary>
    /// Lee los timestamps de la ranura, que ya están resueltos en el buffer de lectura (se llama
    /// después de esperar la valla de esa ranura). Los dos valores son de la MISMA ranura, o sea
    /// del frame que se dibujó dos frames atrás.
    /// </summary>
    private void CollectGpuTiming(int slot)
    {
        if (!_timingPending[slot] || _timestampFrequency == 0) return;
        _timingPending[slot] = false;

        long start = _timestampReadback.GetData<long>(slot * 2 * sizeof(long));
        long end = _timestampReadback.GetData<long>(slot * 2 * sizeof(long) + sizeof(long));
        if (end <= start) return;

        LastGpuFrameMs = (end - start) * 1000.0 / _timestampFrequency;
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
        // ESTILO de la escena (ver SceneStyleConstants): mismo bloque que suben las otras tres APIs.
        public SceneStyleConstants Style;
        // LUCES PUNTUALES de la escena (ver SceneLightConstants): mismo bloque que las otras tres APIs.
        public SceneLightConstants Lights;
        // ENTORNO IBL de la escena (ver SceneEnvironmentConstants): los armónicos esféricos del HDRI,
        // que reemplazan el ambiente inventado del estilo por la luz medida en el lugar.
        public SceneEnvironmentConstants Environment;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Si Initialize falló a mitad de camino, media docena de campos todavía son null: esperar
        // a la GPU con ellos tira una violación de acceso NATIVA, que no se puede atrapar y se
        // lleva puesto el proceso. El orden importa: primero la cola, después los recursos.
        if (_queue != null && _fence != null && _fenceEvent != null)
        {
            try { WaitForGpuIdle(); } catch { }
        }

        DxgiShared.SafeDispose(_skyPipeline);
        DxgiShared.SafeDispose(_geometryPipeline);
        DxgiShared.SafeDispose(_overlayPipeline);
        DxgiShared.SafeDispose(_blendPipeline);
        DxgiShared.SafeDispose(_shadowPipeline);
        DxgiShared.SafeDispose(_shadowTexture);
        DxgiShared.SafeDispose(_shadowDsvHeap);
        DxgiShared.SafeDispose(_overlayBuffer);
        DxgiShared.SafeDispose(_overlaySrvHeap);
        DxgiShared.SafeDispose(_detailTexture);
        DxgiShared.SafeDispose(_detailUpload);
        DxgiShared.SafeDispose(_detailSamplerHeap);
        DxgiShared.SafeDispose(_rootSignature);
        DxgiShared.SafeDispose(_queryHeap);
        DxgiShared.SafeDispose(_timestampReadback);
        foreach (var buffer in _vertexBuffers) DxgiShared.SafeDispose(buffer);
        foreach (var buffer in _instanceBuffers) DxgiShared.SafeDispose(buffer);
        DxgiShared.SafeDispose(_constantBuffer);
        DxgiShared.SafeDispose(_commandList);
        if (_allocators != null) foreach (var allocator in _allocators) DxgiShared.SafeDispose(allocator);
        if (_renderTargets != null) foreach (var target in _renderTargets) DxgiShared.SafeDispose(target);
        DxgiShared.SafeDispose(_depthTexture);
        DxgiShared.SafeDispose(_rtvHeap);
        DxgiShared.SafeDispose(_dsvHeap);
        DxgiShared.SafeDispose(_fence);
        DxgiShared.SafeDispose(_swapChain);
        DxgiShared.SafeDispose(_queue);
        DxgiShared.SafeDispose(_device);
        DxgiShared.SafeDispose(_factory);
        DxgiShared.SafeDispose(_fenceEvent);
    }
}
