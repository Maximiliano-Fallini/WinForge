using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using WinForge.Component.Benchmark.Scenes;
// Trae los grupos de constantes de VulkanApi (VkResult, VkFormat, VkStructureType…) sin repetir el
// prefijo en cada uso: los valores son los del spec y se buscan por su nombre de allá.
using static WinForge.Component.Benchmark.Graphics.VulkanApi;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Backend Vulkan: la MISMA escena que Direct3D 11 y 12, con los MISMOS shaders (el HLSL de
/// <see cref="SceneShaders"/> compilado a SPIR-V en <see cref="VulkanShaders"/>).
///
/// No usa ningún paquete de bindings: habla con <c>vulkan-1.dll</c> por P/Invoke
/// (<see cref="VulkanApi"/>). El componente ya sigue esa regla para Direct3D, así que el zip del
/// Workshop no suma megabytes de bindings para usar treinta funciones.
///
/// Decisiones que hay que conocer para leer el código:
///
/// <list type="bullet">
/// <item>Se pide Vulkan 1.1 y no 1.0 por una razón concreta: la altura NEGATIVA del viewport
/// (núcleo desde 1.1) es lo que hace que el NDC de Vulkan caiga en el framebuffer igual que en
/// Direct3D. Sin eso la escena y el degradado del cielo salen espejados respecto de las otras
/// APIs, y el informe estaría comparando dos imágenes distintas.</item>
/// <item>El HLSL se compiló con <c>-fvk-use-dx-layout</c>: la matriz va en el byte 0 y los tres
/// vectores en 64/80/96, exactamente como en Direct3D. Por eso el bloque de constantes es el
/// mismo <c>FrameConstants</c> de 112 bytes que sube D3D11.</item>
/// <item>Los buffers de geometría viven en memoria de la PLACA (se copian una vez con un buffer de
/// paso): dejarlos en memoria visible al host haría que cada frame los leyera por PCIe y eso se
/// mediría como trabajo de la placa. Solo las constantes son visibles al host, que es lo único que
/// cambia por frame.</item>
/// <item>2 frames en vuelo. Lo que el hilo espera a la valla (o a que se libere una imagen del
/// swapchain) antes de poder armar el frame se reporta como <see cref="LastQueueWaitMs"/>: es "la
/// placa todavía no terminó", no trabajo del CPU.</item>
/// <item>La pantalla completa exclusiva necesita <c>VK_EXT_full_screen_exclusive</c> y una ventana
/// de nivel superior; este backend todavía no la implementa, así que lo DICE
/// (<see cref="SupportsExclusiveFullscreen"/>) y el host lo avisa en el informe, en vez de
/// presentar como exclusiva una corrida que salió en ventana.</item>
/// </list>
///
/// Una instancia por corrida, igual que los otros backends: vive en el hilo de render y no es
/// thread-safe a propósito.
/// </summary>
public sealed class VulkanBackend : IGraphicsBackend
{
    /// <summary>Frames en vuelo: con uno solo el CPU y la placa se turnan y no se mide a ninguno.</summary>
    private const int FramesInFlight = 2;

    /// <summary>Matriz de 64 bytes + 5 vectores de 16 (144): los dos últimos son el rectángulo y el
    /// tamaño de la franja de métricas (idéntico a Direct3D 11 y 12).</summary>
    // 208 bytes: 2 matrices de 64 (vista-proyección y su inversa) + 5 vectores de 16.
    private const int ConstantBufferSize =
        64 + 16 * 5 + 64 + 16 * Scenes.SceneDefinition.MaxShadowCasters + SceneStyleConstants.SizeInBytes
        + SceneLightConstants.SizeInBytes + SceneEnvironmentConstants.SizeInBytes;

    private const int VertexStride = 32;     // Vector3 posición + Vector3 normal + Vector2 UV
    private const int InstanceStride = 64;   // 4 × Vector4 (colocación, tipo y MATERIAL)

    /// <summary>
    /// Bloques de constantes (y descriptor sets) por frame en vuelo. Son DOS: el de la pasada de
    /// sombras y el de la pasada principal del mismo frame, que llevan matrices distintas (la del sol
    /// y la de la cámara). Con uno solo, la segunda escritura del CPU pisaba el bloque que la pasada de
    /// sombras todavía iba a leer — la memoria es visible al host, así que la placa leería la última
    /// matriz escrita y las sombras saldrían del lugar sin ningún error.
    /// </summary>
    private const int ConstantSlicesPerFrame = 2;

    private const string SurfaceExtension = "VK_KHR_surface";
    private const string Win32SurfaceExtension = "VK_KHR_win32_surface";
    private const string SwapchainExtension = "VK_KHR_swapchain";

    /// <summary>VK_MAKE_API_VERSION(0, 1, 1, 0).</summary>
    private const uint ApiVersion11 = (1u << 22) | (1u << 12);

    /// <summary>VK_SUBPASS_EXTERNAL.</summary>
    private const uint SubpassExternal = 0xFFFFFFFF;

    /// <summary>VK_WHOLE_SIZE.</summary>
    private const ulong WholeSize = 0;

    // ---- Instancia y dispositivo ----
    private nint _instance;
    private nint _surface;
    private nint _physicalDevice;
    private nint _device;
    private nint _queue;
    private uint _queueFamily;
    private uint _timestampValidBits;
    private uint _requestedApiVersion = ApiVersion11;

    /// <summary>Nanosegundos por tick de timestamp (0 si la placa no expone tiempos de GPU).</summary>
    private double _timestampPeriodNs;

    // ---- Swapchain ----
    private nint _swapchain;
    private nint[] _swapchainViews = Array.Empty<nint>();
    private nint[] _framebuffers = Array.Empty<nint>();
    private nint[] _presentSemaphores = Array.Empty<nint>();
    private int _swapchainFormat;
    private uint _lastImageIndex;

    // ---- Profundidad ----
    private nint _depthImage;
    private nint _depthView;
    private nint _depthMemory;

    // ---- Pipeline ----
    private nint _renderPass;
    private nint _descriptorSetLayout;
    private nint _pipelineLayout;
    private nint _descriptorPool;
    private nint _skyPipeline;
    private nint _geometryPipeline;
    private nint _overlayPipeline;

    /// <summary>Geometría con blend premultiplicado y sin escritura de profundidad: la pasada del polen.</summary>
    private nint _blendPipeline;

    // ---- Shadow map (ver README-SOMBRAS.md) ----
    // El mapa, su render pass (SOLO profundidad) y la pipeline que lo llena. El render pass es propio
    // y no el de la ventana porque el adjunto es otro: un render pass declara sus formatos de adjunto,
    // así que el de la escena no sirve para dibujar solo profundidad.
    private nint _shadowRenderPass;
    private nint _shadowFramebuffer;
    private nint _shadowPipeline;

    /// <summary>Mundo → espacio de luz de la pasada (ver
    /// <see cref="SceneEnvironmentConstants.LightViewProjectionFor"/>): la misma matriz que el shader
    /// usa en <c>ShadowFactor</c>, más el ajuste de orientación de la textura que le toca a las APIs de
    /// estilo Direct3D (Vulkan dibuja con el viewport invertido justamente para caer en esa
    /// convención).</summary>
    private Matrix4x4 _lightViewProjection;

    /// <summary>False en una escena sin sombras o sin geometría de la que deducir el volumen.</summary>
    private bool _shadowEnabled;

    /// <summary>Qué mallas proyectan (ver <see cref="SceneDefinition.MeshCastsShadow"/>).</summary>
    private bool[] _castsShadow = Array.Empty<bool>();

    // ---- Textura de detalle (el atlas de material; register t0/s0 → bindings 2 y 4) ----
    private nint _detailImage;
    private nint _detailView;
    private nint _detailSampler;

    // ---- Texturas de MATERIAL de la escena (ver Scenes.MaterialAtlas) ----
    // UN arreglo 2D con todos los materiales (tres rebanadas por material) y sus mips: se sube una
    // vez al preparar la escena y se ata a TODOS los descriptor sets. El material de cada objeto es
    // un índice que viaja por instancia, así que no hace falta un descriptor por material ni por
    // dibujo (que es lo que costaría una textura por material en Vulkan).
    private nint _materialImage;
    private nint _materialMemory;
    private nint _materialView;
    private nint _materialSampler;
    private nint _detailMemory;

    // ---- Franja de métricas DENTRO del frame (ver OverlayFrame) ----
    // Un uint por píxel (RGBA8 premultiplicado) en un storage buffer visible al host: subirlo es
    // una copia de memoria desde el hilo de render, sin buffer de paso ni comandos de copia.
    private nint _overlayBuffer;
    private nint _overlayMemory;
    private nint _overlayMapped;
    private ulong _overlayBufferSize;
    private int _overlayPixelCount;
    private int _overlayWidth;
    private int _overlayHeight;
    private int _overlayX;
    private int _overlayY;
    private long _overlayVersion = -1;
    private byte[]? _overlayPending;
    private bool _overlayDirty;

    // ---- Geometría y constantes ----
    // Una entrada por MALLA de la escena (terreno y cazas en el cañón, rocas en el corredor).
    private nint[] _vertexBuffers = Array.Empty<nint>();
    private nint[] _vertexMemories = Array.Empty<nint>();
    private nint[] _instanceBuffers = Array.Empty<nint>();
    private nint[] _instanceMemories = Array.Empty<nint>();
    private nint[] _uniformBuffers = Array.Empty<nint>();
    private nint[] _uniformMemory = Array.Empty<nint>();
    private nint[] _uniformMapped = Array.Empty<nint>();
    private nint[] _descriptorSets = Array.Empty<nint>();

    // ---- Sincronización ----
    private nint _commandPool;
    private nint[] _commandBuffers = Array.Empty<nint>();
    private nint[] _frameFences = Array.Empty<nint>();
    private nint[] _imageAvailable = Array.Empty<nint>();
    private nint[] _queryPools = Array.Empty<nint>();
    private bool[] _timingPending = Array.Empty<bool>();

    // ---- Estado del frame ----
    private SceneDefinition _scene = null!;

    /// <summary>Configuración gráfica de la corrida (ver SceneGraphicsOptions): el mismo objeto para
    /// los cuatro backends, porque las cuatro APIs tienen que dibujar exactamente lo mismo.</summary>
    private SceneGraphicsOptions _graphics = SceneGraphicsOptions.Default;
    private int _frameIndex;
    private int[] _meshVertexCounts = Array.Empty<int>();
    private int[] _meshInstanceCounts = Array.Empty<int>();
    private int _width;
    private int _height;
    private bool _vsync;
    private bool _swapchainDirty;
    private bool _disposed;
    private bool _ownsDevice;

    public GraphicsApi Api => GraphicsApi.Vulkan;
    public string ApiName => "Vulkan";
    public string AdapterName { get; private set; } = "";
    public string AdapterDetail { get; private set; } = "";

    /// <summary>
    /// False: la pantalla completa exclusiva necesita <c>VK_EXT_full_screen_exclusive</c> y una
    /// ventana de nivel superior (esta escena es hija del escritorio). El host lo informa como
    /// aviso en vez de que el informe mienta.
    /// </summary>
    public bool SupportsExclusiveFullscreen => false;

    public bool ExclusiveFullscreenAccepted { get; private set; } = true;

    public bool HasGpuTiming => _timestampPeriodNs > 0 && _timestampValidBits > 0;

    /// <summary>
    /// Tiempo de GPU del frame YA presentado, en ms. Se lee de la ranura que corresponde (dos
    /// frames atrás): es cuando la valla de esa ranura garantiza que los timestamps están
    /// completos. Leer el del frame en curso obligaría a esperar a la placa, que es justo lo que no
    /// se puede hacer mientras se mide.
    /// </summary>
    public double LastGpuFrameMs { get; private set; }

    /// <summary>La franja se dibuja dentro del frame (ver <see cref="OverlayFrame"/>): con
    /// presentación inmediata la ventana no se compone y una franja en ventana aparte no aparece.</summary>
    public bool OverlayInFrame => true;

    /// <summary>Falso mientras no haya píxeles: sin franja no se dibuja el quad.</summary>
    private bool HasOverlay => _overlayWidth > 0 && _overlayHeight > 0;

    /// <summary>
    /// Deja la franja lista para los próximos frames (ver <see cref="OverlayFrame"/>). Se llama desde
    /// el hilo de render entre frames: acá solo se guardan los píxeles y, si cambió el tamaño del
    /// panel, se rehace el buffer; la copia a memoria se hace al PRINCIPIO del frame, cuando la
    /// valla de esa ranura ya garantizó que la placa no lo está leyendo.
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

    /// <summary>
    /// Ms que el hilo de render pasó esperando a la cola de la GPU antes de poder armar el frame:
    /// la valla de la ranura (la placa todavía no terminó los anteriores) más lo que tardó en
    /// liberarse una imagen del swapchain. El host lo descuenta del envío y el informe lo muestra
    /// como columna aparte.
    /// </summary>
    public double LastQueueWaitMs { get; private set; }

    public bool DeviceLost { get; private set; }

    // =====================================================================
    // Sondeo
    // =====================================================================

    /// <summary>
    /// ¿Se puede usar Vulkan en esta máquina? Se comprueba la versión del cargador y se crea una
    /// instancia SIN extensiones de ventana (alcanza para saber si hay placa). Cuando no se puede,
    /// se devuelve el motivo EXACTO: "falta el cargador" no es lo mismo que "no hay placa" ni que
    /// "el cargador es viejo".
    /// </summary>
    public static BackendAvailability Probe()
    {
        nint instance = 0;
        try
        {
            if (!VulkanApi.IsLoaderPresent)
            {
                return new BackendAvailability(GraphicsApi.Vulkan, "Vulkan", false,
                    "No hay un cargador de Vulkan instalado (falta vulkan-1.dll): instalá los drivers de la placa.");
            }

            uint loaderVersion;
            try
            {
                if (VulkanApi.vkEnumerateInstanceVersion(out loaderVersion) != VkResult.Success) loaderVersion = VulkanApi.ApiVersion10;
            }
            catch (EntryPointNotFoundException)
            {
                return new BackendAvailability(GraphicsApi.Vulkan, "Vulkan", false,
                    "El cargador de Vulkan es 1.0 y esta escena necesita 1.1 (para que la imagen no salga espejada): actualizá los drivers.");
            }

            if (loaderVersion < ApiVersion11)
            {
                return new BackendAvailability(GraphicsApi.Vulkan, "Vulkan", false,
                    $"El cargador de Vulkan es {FormatVersion(loaderVersion)} y esta escena necesita {FormatVersion(ApiVersion11)}: actualizá los drivers.");
            }

            if (CreateRawInstance(out instance, out _, needsSurface: false) != VkResult.Success)
            {
                return new BackendAvailability(GraphicsApi.Vulkan, "Vulkan", false,
                    "El cargador de Vulkan está pero no se pudo crear la instancia: revisá los drivers de la placa.");
            }

            uint count = 0;
            if (VulkanApi.vkEnumeratePhysicalDevices(instance, ref count, null) != VkResult.Success || count == 0)
            {
                return new BackendAvailability(GraphicsApi.Vulkan, "Vulkan", false,
                    "El cargador de Vulkan está pero no reportó ninguna placa de video.");
            }

            var devices = new nint[count];
            if (VulkanApi.vkEnumeratePhysicalDevices(instance, ref count, devices) != VkResult.Success || count == 0)
            {
                return new BackendAvailability(GraphicsApi.Vulkan, "Vulkan", false,
                    "El cargador de Vulkan no pudo enumerar las placas de video.");
            }

            return new BackendAvailability(GraphicsApi.Vulkan, "Vulkan", true, "", ReadDevice(devices[0]).Name);
        }
        catch (DllNotFoundException)
        {
            return new BackendAvailability(GraphicsApi.Vulkan, "Vulkan", false,
                "No se pudo cargar vulkan-1.dll: instalá los drivers de la placa.");
        }
        catch (Exception ex)
        {
            return new BackendAvailability(GraphicsApi.Vulkan, "Vulkan", false, ex.Message);
        }
        finally
        {
            if (instance != 0)
            {
                try { VulkanApi.vkDestroyInstance(instance, 0); } catch { }
            }
        }
    }

    private static string FormatVersion(uint version) => $"{version >> 22}.{(version >> 12) & 0x3FF}";

    // =====================================================================
    // Inicialización
    // =====================================================================

    public void Initialize(BackendInitOptions options)
    {
        _scene = options.Scene;
        _graphics = options.Graphics;
        _meshVertexCounts = new int[_scene.Meshes.Count];
        _meshInstanceCounts = new int[_scene.Meshes.Count];
        for (int mesh = 0; mesh < _scene.Meshes.Count; mesh++)
        {
            _meshVertexCounts[mesh] = _scene.Meshes[mesh].Vertices.Length;
            _meshInstanceCounts[mesh] = _scene.Meshes[mesh].Instances.Length;
        }
        _vsync = options.VSync;

        int windowWidth = Math.Max(64, options.Width);
        int windowHeight = Math.Max(64, options.Height);

        // 1) Instancia con las extensiones de superficie de Windows. Se comprueba que existan ANTES
        //    de pedirlas: pedir una extensión que no está es un error de instancia y el mensaje del
        //    driver no explica nada.
        uint result = CreateRawInstance(out _instance, out _requestedApiVersion, needsSurface: true);
        if (result != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan no pudo crear la instancia ({ResultName((int)result)}).");
        }

        // 2) Superficie sobre la ventana de la escena.
        var surfaceInfo = new VulkanApi.VkWin32SurfaceCreateInfoKHR
        {
            sType = VulkanApi.VkStructureType.Win32SurfaceCreateInfoKhr,
            hinstance = GetModuleHandle(null),
            hwnd = options.WindowHandle
        };
        Check(VulkanApi.vkCreateWin32SurfaceKHR(_instance, ref surfaceInfo, 0, out _surface), "crear la superficie de la ventana");

        // 3) Placa y familia de colas. Se crea la superficie ANTES de elegir la familia: la familia
        //    tiene que poder grabar y además presentar en ESTA superficie.
        PickPhysicalDevice(options.AdapterIndex);
        PickQueueFamily();
        CreateDevice();

        // 4) El resto, en orden de dependencia.
        // La PRIMERA consulta al atlas de materiales es la que decodifica las imágenes y le asigna a
        // cada malla su ranura (ver MaterialAtlas.Build): tiene que pasar ANTES de CreateGeometryBuffers,
        // porque las instancias se hornean con esa ranura adentro.
        _ = _scene.Materials;

        CreateSwapchain(windowWidth, windowHeight);
        CreateDepthBuffer();
        CreateDescriptorSetLayoutAndPool();
        CreateCommandResources();
        CreateGeometryBuffers();
        CreateRenderPassAndFramebuffers();
        CreatePipelines();
        CreateUniformBuffers();
        // Buffer (mínimo) de la franja desde el arranque: el binding 3 tiene que apuntar a algo
        // válido aunque el anfitrión nunca llame a SetOverlay.
        CreateOverlayBuffer(0);
        // Textura de detalle: bindings 2 (imagen) y 4 (sampler). Después de los descriptor sets:
        // los writes necesitan los sets ya reservados.
        CreateDetailTexture();
        // Texturas de MATERIAL de la escena: bindings 6 (arreglo) y 5 (sampler), ver MaterialAtlas.
        CreateMaterialTexture();
        CreateShadowResources();
    }

    /// <summary>
    /// Crea la instancia con la versión más nueva que el cargador soporte, sin pasar de 1.1 (que es
    /// lo que esta escena necesita) y, si se pide, con las extensiones de superficie de Windows.
    /// </summary>
    private static uint CreateRawInstance(out nint instance, out uint requestedVersion, bool needsSurface)
    {
        instance = 0;

        uint loaderVersion = VulkanApi.ApiVersion10;
        try { VulkanApi.vkEnumerateInstanceVersion(out loaderVersion); }
        catch (EntryPointNotFoundException) { loaderVersion = VulkanApi.ApiVersion10; }

        requestedVersion = loaderVersion >= ApiVersion11 ? ApiVersion11 : VulkanApi.ApiVersion10;

        var extensions = new List<string>(2);
        if (needsSurface)
        {
            var available = EnumerateInstanceExtensions();
            if (!available.Contains(SurfaceExtension) || !available.Contains(Win32SurfaceExtension))
            {
                throw new InvalidOperationException(
                    "El cargador de Vulkan no expone las extensiones de superficie de Windows (VK_KHR_surface/VK_KHR_win32_surface): no se puede mostrar la escena en una ventana.");
            }
            extensions.Add(SurfaceExtension);
            extensions.Add(Win32SurfaceExtension);
        }

        var allocated = new List<nint>(extensions.Count + 2);
        try
        {
            nint appName = Marshal.StringToCoTaskMemUTF8("WinForge Benchmark");
            nint engineName = Marshal.StringToCoTaskMemUTF8("WinForge");
            allocated.Add(appName);
            allocated.Add(engineName);

            var extensionPointers = new nint[extensions.Count];
            for (int i = 0; i < extensions.Count; i++)
            {
                nint pointer = Marshal.StringToCoTaskMemUTF8(extensions[i]);
                allocated.Add(pointer);
                extensionPointers[i] = pointer;
            }

            return CreateInstance(ref instance, appName, engineName, extensionPointers, requestedVersion);
        }
        finally
        {
            foreach (var pointer in allocated) Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static uint CreateInstance(ref nint instance, nint appName, nint engineName, nint[] extensions, uint requestedVersion)
    {
        var application = new VulkanApi.VkApplicationInfo
        {
            sType = VulkanApi.VkStructureType.ApplicationInfo,
            pApplicationName = appName,
            applicationVersion = 1,
            pEngineName = engineName,
            engineVersion = 1,
            apiVersion = requestedVersion
        };

        unsafe
        {
            fixed (nint* names = extensions)
            {
                var createInfo = new VulkanApi.VkInstanceCreateInfo
                {
                    sType = VulkanApi.VkStructureType.InstanceCreateInfo,
                    pApplicationInfo = (nint)(&application),
                    enabledExtensionCount = (uint)extensions.Length,
                    ppEnabledExtensionNames = extensions.Length == 0 ? 0 : (nint)names
                };
                nint created;
                uint result = (uint)VulkanApi.vkCreateInstance(ref createInfo, 0, out created);
                if (result == VkResult.Success) instance = created;
                return result;
            }
        }
    }

    private static List<string> EnumerateInstanceExtensions()
    {
        var names = new List<string>();
        try
        {
            uint count = 0;
            if (VulkanApi.vkEnumerateInstanceExtensionProperties(0, ref count, null) != VkResult.Success || count == 0) return names;
            var properties = new VulkanApi.VkExtensionProperties[count];
            if (VulkanApi.vkEnumerateInstanceExtensionProperties(0, ref count, properties) != VkResult.Success) return names;
            for (int i = 0; i < count; i++)
            {
                if (!string.IsNullOrEmpty(properties[i].extensionName)) names.Add(properties[i].extensionName);
            }
        }
        catch { }
        return names;
    }

    /// <summary>
    /// Nombre, tipo y capacidades de una placa. VkPhysicalDeviceProperties es enorme y Vulkan
    /// ESCRIBE el struct completo, así que se reserva un bloque con sobra y se leen por offset los
    /// pocos datos que se usan (los offsets están documentados en <see cref="VulkanApi"/>).
    /// </summary>
    private readonly record struct PhysicalDeviceInfo(
        string Name, int Type, uint VendorId, uint DeviceId, double TimestampPeriodNs, bool TimestampSupported);

    private static PhysicalDeviceInfo ReadDevice(nint physicalDevice)
    {
        nint buffer = Marshal.AllocHGlobal(VulkanApi.PropertiesBufferSize);
        try
        {
            VulkanApi.vkGetPhysicalDeviceProperties(physicalDevice, buffer);
            return new PhysicalDeviceInfo(
                (Marshal.PtrToStringUTF8(buffer + VulkanApi.DeviceNameOffset) ?? "").Trim(),
                Marshal.ReadInt32(buffer, 16),       // deviceType
                (uint)Marshal.ReadInt32(buffer, 8),  // vendorID
                (uint)Marshal.ReadInt32(buffer, 12), // deviceID
                Marshal.PtrToStructure<float>(buffer + VulkanApi.TimestampPeriodOffset),
                Marshal.ReadInt32(buffer, VulkanApi.TimestampComputeAndGraphicsOffset) != 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Elige la placa. Si la página pidió un adaptador de la lista de DXGI, se busca la MISMA placa
    /// por nombre: la enumeración de Vulkan no tiene el mismo orden que la de DXGI, y quedarse con
    /// "la primera" cambiaría la placa medida al cambiar de API, que es justo lo que no se quiere.
    /// </summary>
    private void PickPhysicalDevice(int requestedIndex)
    {
        uint count = 0;
        if (VulkanApi.vkEnumeratePhysicalDevices(_instance, ref count, null) != VkResult.Success || count == 0)
        {
            throw new InvalidOperationException("Vulkan no reportó ninguna placa de video.");
        }

        var devices = new nint[count];
        if (VulkanApi.vkEnumeratePhysicalDevices(_instance, ref count, devices) != VkResult.Success || count == 0)
        {
            throw new InvalidOperationException("Vulkan no pudo enumerar las placas de video.");
        }

        string? wanted = null;
        if (requestedIndex >= 0)
        {
            try { wanted = DxgiShared.ListAdapters().FirstOrDefault(a => a.Index == requestedIndex)?.Name; }
            catch { wanted = null; }
        }

        nint chosen = 0;
        PhysicalDeviceInfo chosenInfo = default;
        var infos = new PhysicalDeviceInfo[count];
        for (int i = 0; i < count; i++) infos[i] = ReadDevice(devices[i]);

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            for (int i = 0; i < count; i++)
            {
                if (!string.Equals(infos[i].Name, wanted.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                chosen = devices[i];
                chosenInfo = infos[i];
                break;
            }
        }

        if (chosen == 0)
        {
            // Sin coincidencia: la placa dedicada, que es lo que DXGI devuelve con "Automática".
            for (int i = 0; i < count; i++)
            {
                bool better = chosen == 0 ||
                              (infos[i].Type == VulkanApi.VkDeviceType.DiscreteGpu && chosenInfo.Type != VulkanApi.VkDeviceType.DiscreteGpu);
                if (!better) continue;
                chosen = devices[i];
                chosenInfo = infos[i];
            }
        }

        _physicalDevice = chosen;
        AdapterName = chosenInfo.Name;
        _timestampPeriodNs = chosenInfo.TimestampSupported ? chosenInfo.TimestampPeriodNs : 0;

        long vram = DeviceLocalMemoryBytes();
        AdapterDetail = $"Vulkan {FormatVersion(_requestedApiVersion)} · " +
                        $"dispositivo {chosenInfo.VendorId:X4}:{chosenInfo.DeviceId:X4} · " +
                        (vram > 0 ? $"{vram / (1024 * 1024)} MB de VRAM dedicada · " : "") +
                        (chosenInfo.TimestampSupported ? "con timestamps de GPU" : "sin timestamps de GPU");
    }

    /// <summary>VRAM dedicada: la suma de los montones de memoria marcados como locales al dispositivo.</summary>
    private long DeviceLocalMemoryBytes()
    {
        nint buffer = Marshal.AllocHGlobal(VulkanApi.MemoryPropertiesBufferSize);
        try
        {
            VulkanApi.vkGetPhysicalDeviceMemoryProperties(_physicalDevice, buffer);
            int count = Marshal.ReadInt32(buffer, VulkanApi.MemoryHeapCountOffset);
            long total = 0;
            for (int i = 0; i < count && i < 32; i++)
            {
                long offset = VulkanApi.MemoryHeapsOffset + (long)i * 16;
                int flags = Marshal.ReadInt32(buffer, (int)offset + VulkanApi.MemoryHeapFlagsOffset);
                if ((flags & VulkanApi.MemoryHeapDeviceLocal) == 0) continue;
                total += Marshal.ReadInt64(buffer, (int)offset + VulkanApi.MemoryHeapSizeOffset);
            }
            return total;
        }
        catch { return 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// Familia de colas que puede grabar Y presentar en esta superficie (Vulkan no garantiza que
    /// sea la misma). De paso se leen los bits válidos de timestamp de esa familia, que es lo que
    /// decide si el informe puede tener ms de GPU.
    /// </summary>
    private void PickQueueFamily()
    {
        uint count = 0;
        VulkanApi.vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, 0);
        if (count == 0) throw new InvalidOperationException("La placa de Vulkan no reportó ninguna familia de colas.");

        nint buffer = Marshal.AllocHGlobal((int)count * VulkanApi.QueueFamilyPropertiesSize);
        try
        {
            VulkanApi.vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, buffer);
            for (uint i = 0; i < count; i++)
            {
                int offset = (int)i * VulkanApi.QueueFamilyPropertiesSize;
                int flags = Marshal.ReadInt32(buffer, offset);
                uint queueCount = (uint)Marshal.ReadInt32(buffer, offset + 4);
                if ((flags & VulkanApi.VkQueueFlag.Graphics) == 0 || queueCount == 0) continue;

                if (VulkanApi.vkGetPhysicalDeviceSurfaceSupportKHR(_physicalDevice, i, _surface, out uint supported) != VkResult.Success || supported == 0) continue;

                _queueFamily = i;
                _timestampValidBits = (uint)Marshal.ReadInt32(buffer, offset + VulkanApi.QueueFamilyTimestampValidBitsOffset);
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        throw new InvalidOperationException("Ninguna familia de colas de la placa puede grabar y presentar en esta ventana.");
    }

    private void CreateDevice()
    {
        float[] priority = { 1f };
        nint swapchainName = Marshal.StringToCoTaskMemUTF8(SwapchainExtension);
        try
        {
            unsafe
            {
                fixed (float* priorities = priority)
                fixed (nint* names = new[] { swapchainName })
                {
                    var queueInfo = new VulkanApi.VkDeviceQueueCreateInfo
                    {
                        sType = VulkanApi.VkStructureType.DeviceQueueCreateInfo,
                        queueFamilyIndex = _queueFamily,
                        queueCount = 1,
                        pQueuePriorities = (nint)priorities
                    };
                    var createInfo = new VulkanApi.VkDeviceCreateInfo
                    {
                        sType = VulkanApi.VkStructureType.DeviceCreateInfo,
                        queueCreateInfoCount = 1,
                        pQueueCreateInfos = (nint)(&queueInfo),
                        enabledExtensionCount = 1,
                        ppEnabledExtensionNames = (nint)names
                    };

                    int result = VulkanApi.vkCreateDevice(_physicalDevice, ref createInfo, 0, out _device);
                    if (result != VkResult.Success)
                    {
                        throw new InvalidOperationException(
                            $"Vulkan no pudo crear el dispositivo gráfico ({ResultName(result)}): los drivers no soportan la extensión de swapchain.");
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(swapchainName);
        }

        _ownsDevice = true;
        VulkanApi.vkGetDeviceQueue(_device, _queueFamily, 0, out _queue);
        if (_queue == 0) throw new InvalidOperationException("Vulkan no entregó la cola gráfica del dispositivo.");
    }

    // =====================================================================
    // Swapchain
    // =====================================================================

    private void CreateSwapchain(int width, int height)
    {
        if (VulkanApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(_physicalDevice, _surface, out var capabilities) != VkResult.Success)
        {
            throw new InvalidOperationException("Vulkan no pudo leer las capacidades de la superficie de la ventana.");
        }

        var format = ChooseFormat();
        var extent = ChooseExtent(capabilities, width, height);
        _swapchainFormat = format.format;

        // minImageCount + 1: con el mínimo exacto el CPU se queda sin imagen libre y el acquire
        // bloquea; esa espera aparecería como "trabajo del CPU" en el informe.
        uint imageCount = capabilities.minImageCount + 1;
        if (capabilities.maxImageCount > 0 && imageCount > capabilities.maxImageCount) imageCount = capabilities.maxImageCount;

        int compositeAlpha = (capabilities.supportedCompositeAlpha & VulkanApi.VkCompositeAlpha.Opaque) != 0
            ? VulkanApi.VkCompositeAlpha.Opaque
            : VulkanApi.VkCompositeAlpha.Inherit;

        var createInfo = new VulkanApi.VkSwapchainCreateInfoKHR
        {
            sType = VulkanApi.VkStructureType.SwapchainCreateInfoKhr,
            surface = _surface,
            minImageCount = imageCount,
            imageFormat = format.format,
            imageColorSpace = format.colorSpace,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = VulkanApi.VkImageUsage.ColorAttachment,
            imageSharingMode = VulkanApi.VkSharingMode.Exclusive,
            preTransform = (capabilities.supportedTransforms & VulkanApi.VkSurfaceTransform.Identity) != 0
                ? VulkanApi.VkSurfaceTransform.Identity
                : (int)capabilities.currentTransform,
            compositeAlpha = compositeAlpha,
            presentMode = ChoosePresentMode(),
            clipped = 1
        };

        Check(VulkanApi.vkCreateSwapchainKHR(_device, ref createInfo, 0, out _swapchain), "crear el swapchain");

        uint count = 0;
        VulkanApi.vkGetSwapchainImagesKHR(_device, _swapchain, ref count, null);
        if (count == 0) throw new InvalidOperationException("El swapchain de Vulkan quedó sin imágenes.");
        var images = new nint[count];
        Check(VulkanApi.vkGetSwapchainImagesKHR(_device, _swapchain, ref count, images), "leer las imágenes del swapchain");

        _swapchainViews = new nint[count];
        for (int i = 0; i < count; i++) _swapchainViews[i] = CreateImageView(images[i], _swapchainFormat);

        // Un semáforo de "frame presentado" por imagen: el que se espera al presentar tiene que ser
        // el de ESA imagen, y se reutiliza cuando la imagen vuelve.
        _presentSemaphores = new nint[count];
        for (int i = 0; i < count; i++) _presentSemaphores[i] = CreateSemaphore();

        _width = (int)extent.width;
        _height = (int)extent.height;
    }

    private VulkanApi.VkSurfaceFormatKHR ChooseFormat()
    {
        uint count = 0;
        if (VulkanApi.vkGetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref count, null) != VkResult.Success || count == 0)
        {
            throw new InvalidOperationException("La superficie de Vulkan no reportó ningún formato de imagen.");
        }
        var formats = new VulkanApi.VkSurfaceFormatKHR[count];
        if (VulkanApi.vkGetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref count, formats) != VkResult.Success || count == 0)
        {
            throw new InvalidOperationException("La superficie de Vulkan no pudo leer sus formatos de imagen.");
        }

        // El mismo formato que pide Direct3D (B8G8R8A8 sin sRGB) para que la imagen y el costo de
        // escritura sean comparables; si la superficie no lo ofrece, el primero disponible.
        foreach (var candidate in formats)
        {
            if (candidate.format == VulkanApi.VkFormat.B8G8R8A8Unorm) return candidate;
        }
        foreach (var candidate in formats)
        {
            if (candidate.format == VulkanApi.VkFormat.R8G8B8A8Unorm) return candidate;
        }
        return formats[0];
    }

    /// <summary>
    /// Tamaño del swapchain. Si la superficie reporta 0xFFFFFFFF deja elegir: se usa el tamaño de
    /// la ventana, acotado a lo que la superficie permite.
    /// </summary>
    private static VulkanApi.VkExtent2D ChooseExtent(VulkanApi.VkSurfaceCapabilitiesKHR capabilities, int width, int height)
    {
        if (capabilities.currentExtent.width != uint.MaxValue && capabilities.currentExtent.height != uint.MaxValue)
        {
            return capabilities.currentExtent;
        }
        return new VulkanApi.VkExtent2D
        {
            width = (uint)Math.Clamp(width, (int)capabilities.minImageExtent.width, (int)capabilities.maxImageExtent.width),
            height = (uint)Math.Clamp(height, (int)capabilities.minImageExtent.height, (int)capabilities.maxImageExtent.height)
        };
    }

    /// <summary>
    /// Modo de presentación sin vsync si la superficie lo permite: con FIFO el FPS queda topeado
    /// por la frecuencia del monitor y se mediría al monitor, no a la placa (es la misma razón por
    /// la que Direct3D pide AllowTearing). IMMEDIATE es el que no espera a nadie.
    /// </summary>
    private int ChoosePresentMode()
    {
        if (_vsync) return VulkanApi.VkPresentMode.Fifo;

        uint count = 0;
        if (VulkanApi.vkGetPhysicalDeviceSurfacePresentModesKHR(_physicalDevice, _surface, ref count, null) != VkResult.Success || count == 0)
        {
            return VulkanApi.VkPresentMode.Fifo;
        }
        var modes = new int[count];
        if (VulkanApi.vkGetPhysicalDeviceSurfacePresentModesKHR(_physicalDevice, _surface, ref count, modes) != VkResult.Success)
        {
            return VulkanApi.VkPresentMode.Fifo;
        }

        if (Array.IndexOf(modes, VulkanApi.VkPresentMode.Immediate) >= 0) return VulkanApi.VkPresentMode.Immediate;
        if (Array.IndexOf(modes, VulkanApi.VkPresentMode.Mailbox) >= 0) return VulkanApi.VkPresentMode.Mailbox;
        return VulkanApi.VkPresentMode.Fifo;
    }

    private nint CreateImageView(nint image, int format)
    {
        var createInfo = new VulkanApi.VkImageViewCreateInfo
        {
            sType = VulkanApi.VkStructureType.ImageViewCreateInfo,
            image = image,
            viewType = VulkanApi.VkImageViewType.Type2D,
            format = format,
            subresourceRange = new VulkanApi.VkImageSubresourceRange
            {
                aspectMask = VulkanApi.VkImageAspect.Color,
                levelCount = 1,
                layerCount = 1
            }
        };
        Check(VulkanApi.vkCreateImageView(_device, ref createInfo, 0, out nint view), "crear la vista de una imagen");
        return view;
    }

    private nint CreateSemaphore()
    {
        var createInfo = new VulkanApi.VkSemaphoreCreateInfo { sType = VulkanApi.VkStructureType.SemaphoreCreateInfo };
        Check(VulkanApi.vkCreateSemaphore(_device, ref createInfo, 0, out nint semaphore), "crear un semáforo");
        return semaphore;
    }

    private void CreateDepthBuffer()
    {
        var createInfo = new VulkanApi.VkImageCreateInfo
        {
            sType = VulkanApi.VkStructureType.ImageCreateInfo,
            imageType = VulkanApi.VkImageType.Type2D,
            format = VulkanApi.VkFormat.D32Sfloat,
            extent = new VulkanApi.VkExtent3D { width = (uint)_width, height = (uint)_height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VulkanApi.VkSampleCount.Count1,
            tiling = VulkanApi.VkImageTiling.Optimal,
            usage = VulkanApi.VkImageUsage.DepthStencilAttachment,
            sharingMode = VulkanApi.VkSharingMode.Exclusive,
            initialLayout = VulkanApi.VkImageLayout.Undefined
        };
        Check(VulkanApi.vkCreateImage(_device, ref createInfo, 0, out _depthImage), "crear la imagen de profundidad");
        VulkanApi.vkGetImageMemoryRequirements(_device, _depthImage, out var requirements);
        _depthMemory = AllocateMemory(requirements, VulkanApi.VkMemoryProperty.DeviceLocal, "profundidad");
        Check(VulkanApi.vkBindImageMemory(_device, _depthImage, _depthMemory, 0), "atar la memoria de profundidad");

        var viewInfo = new VulkanApi.VkImageViewCreateInfo
        {
            sType = VulkanApi.VkStructureType.ImageViewCreateInfo,
            image = _depthImage,
            viewType = VulkanApi.VkImageViewType.Type2D,
            format = VulkanApi.VkFormat.D32Sfloat,
            subresourceRange = new VulkanApi.VkImageSubresourceRange
            {
                aspectMask = VulkanApi.VkImageAspect.Depth,
                levelCount = 1,
                layerCount = 1
            }
        };
        Check(VulkanApi.vkCreateImageView(_device, ref viewInfo, 0, out _depthView), "crear la vista de profundidad");
    }

    /// <summary>
    /// Render pass con color y profundidad. En los dos adjuntos el layout inicial es UNDEFINED y la
    /// carga es CLEAR: cada frame descarta lo anterior, así no hay que rastrear el layout entre las
    /// imágenes del swapchain (fuente clásica de imagen corrupta). El color termina en
    /// PRESENT_SRC_KHR, que es la transición que necesita el present.
    /// </summary>
    private void CreateRenderPassAndFramebuffers()
    {
        var attachments = new[]
        {
            new VulkanApi.VkAttachmentDescription
            {
                format = _swapchainFormat,
                samples = VulkanApi.VkSampleCount.Count1,
                loadOp = VulkanApi.VkAttachmentLoadOp.Clear,
                storeOp = VulkanApi.VkAttachmentStoreOp.Store,
                stencilLoadOp = VulkanApi.VkAttachmentLoadOp.DontCare,
                stencilStoreOp = VulkanApi.VkAttachmentStoreOp.DontCare,
                initialLayout = VulkanApi.VkImageLayout.Undefined,
                finalLayout = VulkanApi.VkImageLayout.PresentSrcKhr
            },
            new VulkanApi.VkAttachmentDescription
            {
                format = VulkanApi.VkFormat.D32Sfloat,
                samples = VulkanApi.VkSampleCount.Count1,
                loadOp = VulkanApi.VkAttachmentLoadOp.Clear,
                storeOp = VulkanApi.VkAttachmentStoreOp.DontCare,
                stencilLoadOp = VulkanApi.VkAttachmentLoadOp.DontCare,
                stencilStoreOp = VulkanApi.VkAttachmentStoreOp.DontCare,
                initialLayout = VulkanApi.VkImageLayout.Undefined,
                finalLayout = VulkanApi.VkImageLayout.DepthStencilAttachmentOptimal
            }
        };

        var colorReference = new VulkanApi.VkAttachmentReference { attachment = 0, layout = VulkanApi.VkImageLayout.ColorAttachmentOptimal };
        var depthReference = new VulkanApi.VkAttachmentReference { attachment = 1, layout = VulkanApi.VkImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new VulkanApi.VkSubpassDescription
        {
            pipelineBindPoint = VulkanApi.VkPipelineBindPoint.Graphics,
            colorAttachmentCount = 1,
            pColorAttachments = (nint)0,
            pDepthStencilAttachment = (nint)0
        };
        // Dependencia externa → subpaso 0: la escritura al color no puede empezar antes de que la
        // imagen esté realmente disponible (es el "estoy a punto de escribir" del acquire).
        var dependency = new VulkanApi.VkSubpassDependency
        {
            srcSubpass = SubpassExternal,
            dstSubpass = 0,
            srcStageMask = VulkanApi.VkPipelineStage.ColorAttachmentOutput,
            dstStageMask = VulkanApi.VkPipelineStage.ColorAttachmentOutput,
            srcAccessMask = 0,
            dstAccessMask = VulkanApi.VkAccess.ColorAttachmentWrite
        };

        unsafe
        {
            fixed (VulkanApi.VkAttachmentDescription* attachmentPointer = attachments)
            {
                subpass.pColorAttachments = (nint)(&colorReference);
                subpass.pDepthStencilAttachment = (nint)(&depthReference);

                var createInfo = new VulkanApi.VkRenderPassCreateInfo
                {
                    sType = VulkanApi.VkStructureType.RenderPassCreateInfo,
                    attachmentCount = (uint)attachments.Length,
                    pAttachments = (nint)attachmentPointer,
                    subpassCount = 1,
                    pSubpasses = (nint)(&subpass),
                    dependencyCount = 1,
                    pDependencies = (nint)(&dependency)
                };
                Check(VulkanApi.vkCreateRenderPass(_device, ref createInfo, 0, out _renderPass), "crear el render pass");
            }
        }

        _framebuffers = new nint[_swapchainViews.Length];
        for (int i = 0; i < _framebuffers.Length; i++)
        {
            var attachmentViews = new[] { _swapchainViews[i], _depthView };
            unsafe
            {
                fixed (nint* views = attachmentViews)
                {
                    var createInfo = new VulkanApi.VkFramebufferCreateInfo
                    {
                        sType = VulkanApi.VkStructureType.FramebufferCreateInfo,
                        renderPass = _renderPass,
                        attachmentCount = 2,
                        pAttachments = (nint)views,
                        width = (uint)_width,
                        height = (uint)_height,
                        layers = 1
                    };
                    Check(VulkanApi.vkCreateFramebuffer(_device, ref createInfo, 0, out _framebuffers[i]), "crear el framebuffer");
                }
            }
        }
    }

    // =====================================================================
    // Pipelines
    // =====================================================================

    private void CreateDescriptorSetLayoutAndPool()
    {
        var bindings = new[]
        {
            new VulkanApi.VkDescriptorSetLayoutBinding
            {
                binding = 0,
                descriptorType = VulkanApi.VkDescriptorType.UniformBuffer,
                descriptorCount = 1,
                // Las DOS etapas lo leen (el pixel shader usa la luz, la cámara y el tiempo), igual que
                // en D3D11, donde el mismo cbuffer hay que bindearlo al VS y al PS.
                stageFlags = VulkanApi.VkShaderStage.Vertex | VulkanApi.VkShaderStage.Fragment
            },
            new VulkanApi.VkDescriptorSetLayoutBinding
            {
                // Binding 3: el StructuredBuffer de la franja. Su número sale del MISMO registro del
                // HLSL (t1) más el corrimiento que le pide a dxc el generador de SPIR-V
                // (-fvk-t-shift 2): los registros t arrancan en 2 para dejarle el 0 al cbuffer y el 1
                // a la textura de detalle (dos descriptores de tipos distintos en el mismo binding
                // son inválidos en Vulkan).
                binding = 3,
                descriptorType = VulkanApi.VkDescriptorType.StorageBuffer,
                descriptorCount = 1,
                // Solo la etapa de fragmentos lo lee (el quad lo dibuja PSSky).
                stageFlags = VulkanApi.VkShaderStage.Fragment
            },
            new VulkanApi.VkDescriptorSetLayoutBinding
            {
                // Binding 2: la textura de detalle (register t0 + 2).
                binding = 2,
                descriptorType = VulkanApi.VkDescriptorType.SampledImage,
                descriptorCount = 1,
                stageFlags = VulkanApi.VkShaderStage.Fragment
            },                new VulkanApi.VkDescriptorSetLayoutBinding
                {
                    // Binding 4: el sampler de esa textura (-fvk-s-shift 4). En Vulkan la imagen y el
                    // sampler son DOS descriptores distintos: es el mismo modelo que usa el HLSL.
                    binding = 4,
                    descriptorType = VulkanApi.VkDescriptorType.Sampler,
                    descriptorCount = 1,
                    stageFlags = VulkanApi.VkShaderStage.Fragment
                },
                new VulkanApi.VkDescriptorSetLayoutBinding
                {
                    // Binding 6: el arreglo de materiales de la escena (register t4 + 2, ver
                    // Scenes.MaterialAtlas). UNA imagen con todos los materiales: el material de cada
                    // objeto es un índice que viaja por instancia, así que no hay un descriptor por
                    // material ni por dibujo.
                    binding = 6,
                    descriptorType = VulkanApi.VkDescriptorType.SampledImage,
                    descriptorCount = 1,
                    stageFlags = VulkanApi.VkShaderStage.Fragment
                },
                new VulkanApi.VkDescriptorSetLayoutBinding
                {
                    // Binding 5: su sampler (register s1 + 4).
                    binding = 5,
                    descriptorType = VulkanApi.VkDescriptorType.Sampler,
                    descriptorCount = 1,
                    stageFlags = VulkanApi.VkShaderStage.Fragment
                },
                new VulkanApi.VkDescriptorSetLayoutBinding
                {
                    // Binding 8: el SHADOW MAP (register t6 + 2, ver README-SOMBRAS.md).
                    //
                    // OJO con los números: el layout TIENE que declarar todo binding que el SPIR-V
                    // mencione, aunque la escena no use sombras. La referencia vive en el shader
                    // compilado, no en el camino de ejecución, así que sin esto falla la creación de
                    // la pipeline y no se dibuja NADA (no es un detalle de rendimiento).
                    binding = 8,
                    descriptorType = VulkanApi.VkDescriptorType.SampledImage,
                    descriptorCount = 1,
                    stageFlags = VulkanApi.VkShaderStage.Fragment
                },
                new VulkanApi.VkDescriptorSetLayoutBinding
                {
                    // Binding 7: su sampler de COMPARACIÓN (register s3 + 4). Es el que hace que la
                    // placa devuelva 0/1 en vez de la profundidad cruda.
                    binding = 7,
                    descriptorType = VulkanApi.VkDescriptorType.Sampler,
                    descriptorCount = 1,
                    stageFlags = VulkanApi.VkShaderStage.Fragment
                }
            };

        unsafe
        {
            fixed (VulkanApi.VkDescriptorSetLayoutBinding* bindingPointer = bindings)
            {
                var createInfo = new VulkanApi.VkDescriptorSetLayoutCreateInfo
                {
                    sType = VulkanApi.VkStructureType.DescriptorSetLayoutCreateInfo,
                    bindingCount = (uint)bindings.Length,
                    pBindings = (nint)bindingPointer
                };
                Check(VulkanApi.vkCreateDescriptorSetLayout(_device, ref createInfo, 0, out _descriptorSetLayout), "crear el layout de descriptors");
            }

            nint setLayout = _descriptorSetLayout;
            var layoutInfo = new VulkanApi.VkPipelineLayoutCreateInfo
            {
                sType = VulkanApi.VkStructureType.PipelineLayoutCreateInfo,
                setLayoutCount = 1,
                pSetLayouts = (nint)(&setLayout)
            };
            Check(VulkanApi.vkCreatePipelineLayout(_device, ref layoutInfo, 0, out _pipelineLayout), "crear el layout del pipeline");

            var poolSizes = new[]
            {
                new VulkanApi.VkDescriptorPoolSize
                {
                    // DOS por frame en vuelo: la pasada de sombras y la principal (ver
                    // ConstantSlicesPerFrame).
                    type = VulkanApi.VkDescriptorType.UniformBuffer,
                    descriptorCount = FramesInFlight * ConstantSlicesPerFrame
                },
                new VulkanApi.VkDescriptorPoolSize
                {
                    type = VulkanApi.VkDescriptorType.StorageBuffer,
                    descriptorCount = FramesInFlight * ConstantSlicesPerFrame
                },
                new VulkanApi.VkDescriptorPoolSize
                {
                    // TRES por set: el detalle (binding 2), el arreglo de materiales (binding 6) y el
                    // shadow map (binding 8).
                    type = VulkanApi.VkDescriptorType.SampledImage,
                    descriptorCount = FramesInFlight * ConstantSlicesPerFrame * 3
                },
                new VulkanApi.VkDescriptorPoolSize
                {
                    // TRES por set: el sampler del detalle (4), el del arreglo (5) y el de comparación
                    // del shadow map (7).
                    type = VulkanApi.VkDescriptorType.Sampler,
                    descriptorCount = FramesInFlight * ConstantSlicesPerFrame * 3
                }
            };
            fixed (VulkanApi.VkDescriptorPoolSize* poolPointer = poolSizes)
            {
                var poolInfo = new VulkanApi.VkDescriptorPoolCreateInfo
                {
                    sType = VulkanApi.VkStructureType.DescriptorPoolCreateInfo,
                    maxSets = FramesInFlight * ConstantSlicesPerFrame,
                    poolSizeCount = (uint)poolSizes.Length,
                    pPoolSizes = (nint)poolPointer
                };
                Check(VulkanApi.vkCreateDescriptorPool(_device, ref poolInfo, 0, out _descriptorPool), "crear el pool de descriptors");
            }
        }
    }

    /// <summary>
    /// Crea los dos pipelines. El del cielo no tiene entradas (el triángulo se genera con
    /// SV_VertexID, que en SPIR-V es gl_VertexIndex) y no toca la profundidad; el de la geometría es
    /// instanciado, con profundidad activa.
    ///
    /// CullNone a propósito, igual que en Direct3D: el winding de la malla procedural no es
    /// contrato (las normales se corrigen por cara en C#) y con back-face culling la escena entera
    /// desaparecía con la placa trabajando igual. Sin culling el costo de relleno es el mismo en las
    /// tres APIs y la comparación es válida.
    /// </summary>
    private void CreatePipelines()
    {
        _skyPipeline = CreatePipeline(VulkanShaders.SkyVertex, "VSSky", VulkanShaders.SkyPixel, "PSSky", geometry: false);
        _geometryPipeline = CreatePipeline(VulkanShaders.GeometryVertex, "VSMain", VulkanShaders.GeometryPixel, "PSMain", geometry: true);
        // La franja: mismo pixel shader que el fondo (sin profundidad, generado por vértice) pero con
        // su propio vertex shader —el quad se arma desde el vértice 0, sin depender del vértice base
        // ni del instance id, que es justo lo que cambia entre APIs— más el blend premultiplicado.
        // En Vulkan el blend es estado del PIPELINE (no dinámico), así que además necesita su propia
        // pipeline para no mezclarlo con el fondo.
        _overlayPipeline = CreatePipeline(VulkanShaders.SkyOverlayVertex, "VSSkyOverlay", VulkanShaders.SkyPixel, "PSSky",
            geometry: false, premultipliedBlend: true);
        // Partículas (el polen): los MISMOS shaders que la geometría, con el blend premultiplicado y
        // sin escribir profundidad. En Vulkan el blend y la profundidad son estado del PIPELINE (no
        // dinámicos), así que la pasada transparente necesita la suya — igual que en Direct3D 12.
        _blendPipeline = CreatePipeline(VulkanShaders.GeometryVertex, "VSMain", VulkanShaders.GeometryPixel, "PSMain",
            geometry: true, premultipliedBlend: true, depthWrite: false);
    }

    /// <summary>
    /// Crea una pipeline gráfica. Con <paramref name="renderPass"/> se puede apuntar a OTRO render pass
    /// (el del shadow map) y con <paramref name="depthOnly"/> se arma una pasada SIN etapa de
    /// fragmentos: la placa rasteriza y escribe profundidad nada más, que es exactamente lo que
    /// necesita la pasada de sombras (y es lo que la hace mucho más barata que la principal aun
    /// dibujando la misma geometría).
    /// </summary>
    private nint CreatePipeline(byte[] vertexSpirv, string vertexEntry, byte[] pixelSpirv, string pixelEntry, bool geometry,
        bool premultipliedBlend = false, bool depthWrite = true, nint renderPass = 0, bool depthOnly = false)
    {
        nint vertexModule = CreateShaderModule(vertexSpirv);
        nint pixelModule = depthOnly ? 0 : CreateShaderModule(pixelSpirv);
        nint vertexName = Marshal.StringToCoTaskMemUTF8(vertexEntry);
        nint pixelName = depthOnly ? 0 : Marshal.StringToCoTaskMemUTF8(pixelEntry);
        try
        {
            var stages = depthOnly
                ? new[]
                {
                    new VulkanApi.VkPipelineShaderStageCreateInfo
                    {
                        sType = VulkanApi.VkStructureType.PipelineShaderStageCreateInfo,
                        stage = VulkanApi.VkShaderStage.Vertex,
                        module = vertexModule,
                        pName = vertexName
                    }
                }
                : new[]
                {
                    new VulkanApi.VkPipelineShaderStageCreateInfo
                    {
                        sType = VulkanApi.VkStructureType.PipelineShaderStageCreateInfo,
                        stage = VulkanApi.VkShaderStage.Vertex,
                        module = vertexModule,
                        pName = vertexName
                    },
                    new VulkanApi.VkPipelineShaderStageCreateInfo
                    {
                        sType = VulkanApi.VkStructureType.PipelineShaderStageCreateInfo,
                        stage = VulkanApi.VkShaderStage.Fragment,
                        module = pixelModule,
                        pName = pixelName
                    }
                };

            // Locations 0..3 tal como los emite dxc: POSITION y NORMAL por vértice, y los dos
            // vectores por instancia (PositionScale y Orientation).
            var bindings = geometry
                ? new[]
                {
                    new VulkanApi.VkVertexInputBindingDescription { binding = 0, stride = VertexStride, inputRate = VulkanApi.VkVertexInputRate.Vertex },
                    new VulkanApi.VkVertexInputBindingDescription { binding = 1, stride = InstanceStride, inputRate = VulkanApi.VkVertexInputRate.Instance }
                }
                : Array.Empty<VulkanApi.VkVertexInputBindingDescription>();

            var attributes = geometry
                ? new[]
                {
                    // Las locations salen del ORDEN de la firma del vértice (dxc numera los inputs en
                    // el orden en que aparecen): POSITION 0, NORMAL 1, TEXCOORD 2, y después los cuatro
                    // vectores por instancia 3..6. Si se agrega un atributo al HLSL, esta tabla se
                    // corre igual — un location mal puesto no da error, da geometría deformada.
                    new VulkanApi.VkVertexInputAttributeDescription { location = 0, binding = 0, format = VulkanApi.VkFormat.R32G32B32Sfloat, offset = 0 },
                    new VulkanApi.VkVertexInputAttributeDescription { location = 1, binding = 0, format = VulkanApi.VkFormat.R32G32B32Sfloat, offset = 12 },
                    new VulkanApi.VkVertexInputAttributeDescription { location = 2, binding = 0, format = VulkanApi.VkFormat.R32G32Sfloat, offset = 24 },
                    new VulkanApi.VkVertexInputAttributeDescription { location = 3, binding = 1, format = VulkanApi.VkFormat.R32G32B32A32Sfloat, offset = 0 },
                    new VulkanApi.VkVertexInputAttributeDescription { location = 4, binding = 1, format = VulkanApi.VkFormat.R32G32B32A32Sfloat, offset = 16 },
                    new VulkanApi.VkVertexInputAttributeDescription { location = 5, binding = 1, format = VulkanApi.VkFormat.R32G32B32A32Sfloat, offset = 32 },
                    new VulkanApi.VkVertexInputAttributeDescription { location = 6, binding = 1, format = VulkanApi.VkFormat.R32G32B32A32Sfloat, offset = 48 }
                }
                : Array.Empty<VulkanApi.VkVertexInputAttributeDescription>();

            var dynamicStates = new[] { VulkanApi.VkDynamicState.Viewport, VulkanApi.VkDynamicState.Scissor };

            var inputAssembly = new VulkanApi.VkPipelineInputAssemblyStateCreateInfo
            {
                sType = VulkanApi.VkStructureType.PipelineInputAssemblyStateCreateInfo,
                topology = VulkanApi.VkPrimitiveTopology.TriangleList
            };
            var viewportState = new VulkanApi.VkPipelineViewportStateCreateInfo
            {
                sType = VulkanApi.VkStructureType.PipelineViewportStateCreateInfo,
                viewportCount = 1,
                scissorCount = 1
            };
            var rasterization = new VulkanApi.VkPipelineRasterizationStateCreateInfo
            {
                sType = VulkanApi.VkStructureType.PipelineRasterizationStateCreateInfo,
                polygonMode = VulkanApi.VkPolygonMode.Fill,
                cullMode = VulkanApi.VkCullMode.None,
                frontFace = VulkanApi.VkFrontFace.CounterClockwise,
                lineWidth = 1f
            };
            var multisample = new VulkanApi.VkPipelineMultisampleStateCreateInfo
            {
                sType = VulkanApi.VkStructureType.PipelineMultisampleStateCreateInfo,
                rasterizationSamples = VulkanApi.VkSampleCount.Count1
            };
            var keepStencil = new VulkanApi.VkStencilOpState
            {
                failOp = VulkanApi.VkStencilOp.Keep,
                passOp = VulkanApi.VkStencilOp.Keep,
                depthFailOp = VulkanApi.VkStencilOp.Keep,
                compareOp = VulkanApi.VkCompareOp.Always
            };
            var depthStencil = new VulkanApi.VkPipelineDepthStencilStateCreateInfo
            {
                sType = VulkanApi.VkStructureType.PipelineDepthStencilStateCreateInfo,
                depthTestEnable = geometry ? 1u : 0u,
                depthWriteEnable = geometry && depthWrite ? 1u : 0u,
                depthCompareOp = VulkanApi.VkCompareOp.Less,
                stencilTestEnable = 0,
                front = keepStencil,
                back = keepStencil
            };
            var blendAttachment = new VulkanApi.VkPipelineColorBlendAttachmentState
            {
                // El fondo y la geometría se dibujan opacos; la franja, premultiplicada: con
                // One / OneMinusSrcAlpha el panel opaco reemplaza lo que hay debajo y los bordes
                // transparentes dejan pasar la escena (el mismo blend que Direct3D 11).
                blendEnable = premultipliedBlend ? 1u : 0u,
                srcColorBlendFactor = VulkanApi.VkBlendFactor.One,
                dstColorBlendFactor = VulkanApi.VkBlendFactor.OneMinusSourceAlpha,
                colorBlendOp = VulkanApi.VkBlendOp.Add,
                colorWriteMask = VulkanApi.ColorComponentAll
            };
            var dynamicState = new VulkanApi.VkPipelineDynamicStateCreateInfo
            {
                sType = VulkanApi.VkStructureType.PipelineDynamicStateCreateInfo,
                dynamicStateCount = (uint)dynamicStates.Length
            };
            var vertexInput = new VulkanApi.VkPipelineVertexInputStateCreateInfo
            {
                sType = VulkanApi.VkStructureType.PipelineVertexInputStateCreateInfo,
                vertexBindingDescriptionCount = (uint)bindings.Length,
                vertexAttributeDescriptionCount = (uint)attributes.Length
            };

            unsafe
            {
                fixed (VulkanApi.VkPipelineShaderStageCreateInfo* stagePointer = stages)
                fixed (VulkanApi.VkVertexInputBindingDescription* bindingPointer = bindings)
                fixed (VulkanApi.VkVertexInputAttributeDescription* attributePointer = attributes)
                fixed (int* dynamicPointer = dynamicStates)
                {
                    vertexInput.pVertexBindingDescriptions = bindings.Length == 0 ? 0 : (nint)bindingPointer;
                    vertexInput.pVertexAttributeDescriptions = attributes.Length == 0 ? 0 : (nint)attributePointer;
                    dynamicState.pDynamicStates = (nint)dynamicPointer;

                    var blend = new VulkanApi.VkPipelineColorBlendStateCreateInfo
                    {
                        sType = VulkanApi.VkStructureType.PipelineColorBlendStateCreateInfo,
                        // Sin adjuntos de color no hay blend que declarar: la cuenta TIENE que ser la
                        // del subpaso (cero en el render pass de sombras), y una cuenta distinta falla
                        // al crear la pipeline.
                        attachmentCount = depthOnly ? 0u : 1u,
                        pAttachments = depthOnly ? (nint)0 : (nint)(&blendAttachment)
                    };

                    var createInfo = new VulkanApi.VkGraphicsPipelineCreateInfo
                    {
                        sType = VulkanApi.VkStructureType.GraphicsPipelineCreateInfo,
                        stageCount = (uint)stages.Length,
                        pStages = (nint)stagePointer,
                        pVertexInputState = (nint)(&vertexInput),
                        pInputAssemblyState = (nint)(&inputAssembly),
                        pViewportState = (nint)(&viewportState),
                        pRasterizationState = (nint)(&rasterization),
                        pMultisampleState = (nint)(&multisample),
                        pDepthStencilState = (nint)(&depthStencil),
                        pColorBlendState = (nint)(&blend),
                        pDynamicState = (nint)(&dynamicState),
                        layout = _pipelineLayout,
                        // El render pass de la ventana o el del shadow map: la pipeline tiene que ser
                        // compatible con el render pass en el que se va a usar (formatos de adjunto).
                        renderPass = renderPass != 0 ? renderPass : _renderPass,
                        subpass = 0,
                        basePipelineIndex = -1
                    };

                    var pipelines = new nint[1];
                    int result = VulkanApi.vkCreateGraphicsPipelines(_device, 0, 1, (nint)(&createInfo), 0, pipelines);
                    if (result != VkResult.Success)
                    {
                        throw new InvalidOperationException($"Vulkan no pudo crear el pipeline de la escena ({ResultName(result)}).");
                    }
                    return pipelines[0];
                }
            }
        }
        finally
        {
            VulkanApi.vkDestroyShaderModule(_device, vertexModule, 0);
            if (pixelModule != 0) VulkanApi.vkDestroyShaderModule(_device, pixelModule, 0);
            Marshal.FreeCoTaskMem(vertexName);
            if (pixelName != 0) Marshal.FreeCoTaskMem(pixelName);
        }
    }

    private nint CreateShaderModule(byte[] spirv)
    {
        unsafe
        {
            fixed (byte* code = spirv)
            {
                var createInfo = new VulkanApi.VkShaderModuleCreateInfo
                {
                    sType = VulkanApi.VkStructureType.ShaderModuleCreateInfo,
                    codeSize = (nuint)spirv.Length,
                    pCode = (nint)code
                };
                Check(VulkanApi.vkCreateShaderModule(_device, ref createInfo, 0, out nint module), "crear el módulo de shader");
                return module;
            }
        }
    }

    // =====================================================================
    // Memoria y recursos
    // =====================================================================

    /// <summary>Busca un tipo de memoria con TODAS las propiedades pedidas y compatible con el recurso.</summary>
    private uint FindMemoryType(uint typeBits, int properties)
    {
        nint buffer = Marshal.AllocHGlobal(VulkanApi.MemoryPropertiesBufferSize);
        try
        {
            VulkanApi.vkGetPhysicalDeviceMemoryProperties(_physicalDevice, buffer);
            int count = Marshal.ReadInt32(buffer, VulkanApi.MemoryTypeCountOffset);
            for (int i = 0; i < count && i < 32; i++)
            {
                if ((typeBits & (1u << i)) == 0) continue;
                int flags = Marshal.ReadInt32(buffer, VulkanApi.MemoryTypesOffset + i * 8);
                if ((flags & properties) == properties) return (uint)i;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        throw new InvalidOperationException("La placa no tiene un tipo de memoria con las propiedades que necesita la escena.");
    }

    private nint AllocateMemory(VulkanApi.VkMemoryRequirements requirements, int properties, string what)
    {
        var allocateInfo = new VulkanApi.VkMemoryAllocateInfo
        {
            sType = VulkanApi.VkStructureType.MemoryAllocateInfo,
            allocationSize = requirements.size,
            memoryTypeIndex = FindMemoryType(requirements.memoryTypeBits, properties)
        };
        Check(VulkanApi.vkAllocateMemory(_device, ref allocateInfo, 0, out nint memory), $"reservar memoria para {what}");
        return memory;
    }

    private nint CreateBuffer(ulong size, int usage, int properties, string what, out nint memory)
    {
        var createInfo = new VulkanApi.VkBufferCreateInfo
        {
            sType = VulkanApi.VkStructureType.BufferCreateInfo,
            size = size,
            usage = usage,
            sharingMode = VulkanApi.VkSharingMode.Exclusive
        };
        Check(VulkanApi.vkCreateBuffer(_device, ref createInfo, 0, out nint buffer), $"crear el buffer de {what}");
        VulkanApi.vkGetBufferMemoryRequirements(_device, buffer, out var requirements);
        memory = AllocateMemory(requirements, properties, what);
        Check(VulkanApi.vkBindBufferMemory(_device, buffer, memory, 0), $"atar la memoria de {what}");
        return buffer;
    }

    /// <summary>
    /// Sube la malla y las instancias a memoria de la PLACA en una sola copia. El buffer de paso
    /// (visible al host) se libera al terminar: la escena no lo vuelve a necesitar.
    /// </summary>
    private void CreateGeometryBuffers()
    {
        int meshCount = _scene.Meshes.Count;
        _vertexBuffers = new nint[meshCount];
        _vertexMemories = new nint[meshCount];
        _instanceBuffers = new nint[meshCount];
        _instanceMemories = new nint[meshCount];

        // Todas las mallas viajan en UN buffer de paso: la copia a la placa sale en un solo envío y
        // el buffer se libera al terminar (la escena no lo vuelve a necesitar).
        ulong vertexBytes = 0;
        ulong instanceBytes = 0;
        for (int mesh = 0; mesh < meshCount; mesh++)
        {
            vertexBytes += (ulong)_scene.Meshes[mesh].Vertices.Length * VertexStride;
            // RenderInstances y no Instances: el material de la malla ya está horneado en cada
            // instancia (tinta, fuerzas y ranura del atlas). Ver SceneMesh.RenderInstances.
            instanceBytes += (ulong)_scene.Meshes[mesh].RenderInstances.Length * InstanceStride;
        }

        nint staging = CreateBuffer(vertexBytes + instanceBytes, VulkanApi.VkBufferUsage.TransferSrc,
            VulkanApi.VkMemoryProperty.HostVisible | VulkanApi.VkMemoryProperty.HostCoherent, "paso", out nint stagingMemory);
        try
        {
            Check(VulkanApi.vkMapMemory(_device, stagingMemory, 0, vertexBytes + instanceBytes, 0, out nint mapped), "mapear el buffer de paso");
            try
            {
                unsafe
                {
                    // Los dos arreglos son de structs sin huecos y en el mismo orden que pide el input
                    // layout: se copian tal cual (D3D11 hace exactamente lo mismo al crear el buffer).
                    ulong vertexOffset = 0;
                    ulong instanceOffset = vertexBytes;
                    for (int mesh = 0; mesh < meshCount; mesh++)
                    {
                        var geometry = _scene.Meshes[mesh];
                        var instances = geometry.RenderInstances;
                        ulong meshVertexBytes = (ulong)geometry.Vertices.Length * VertexStride;
                        ulong meshInstanceBytes = (ulong)instances.Length * InstanceStride;
                        if (meshVertexBytes > 0)
                        {
                            fixed (SceneVertex* vertexPointer = geometry.Vertices)
                            {
                                Buffer.MemoryCopy(vertexPointer, (void*)(mapped + (nint)vertexOffset), (long)meshVertexBytes, (long)meshVertexBytes);
                            }
                        }
                        if (meshInstanceBytes > 0)
                        {
                            fixed (SceneInstance* instancePointer = instances)
                            {
                                Buffer.MemoryCopy(instancePointer, (void*)(mapped + (nint)instanceOffset), (long)meshInstanceBytes, (long)meshInstanceBytes);
                            }
                        }
                        vertexOffset += meshVertexBytes;
                        instanceOffset += meshInstanceBytes;
                    }
                }
            }
            finally
            {
                VulkanApi.vkUnmapMemory(_device, stagingMemory);
            }

            for (int mesh = 0; mesh < meshCount; mesh++)
            {
                var geometry = _scene.Meshes[mesh];
                ulong meshVertexBytes = (ulong)geometry.Vertices.Length * VertexStride;
                ulong meshInstanceBytes = (ulong)geometry.RenderInstances.Length * InstanceStride;
                _vertexBuffers[mesh] = CreateBuffer(meshVertexBytes, VulkanApi.VkBufferUsage.TransferDst | VulkanApi.VkBufferUsage.VertexBuffer,
                    VulkanApi.VkMemoryProperty.DeviceLocal, $"vértices de {geometry.Name}", out _vertexMemories[mesh]);
                _instanceBuffers[mesh] = CreateBuffer(meshInstanceBytes, VulkanApi.VkBufferUsage.TransferDst | VulkanApi.VkBufferUsage.VertexBuffer,
                    VulkanApi.VkMemoryProperty.DeviceLocal, $"instancias de {geometry.Name}", out _instanceMemories[mesh]);
            }

            RunOneShotCommandBuffer("copiar la geometría a la placa", command =>
            {
                unsafe
                {
                    ulong vertexOffset = 0;
                    ulong instanceOffset = vertexBytes;
                    for (int mesh = 0; mesh < meshCount; mesh++)
                    {
                        var geometry = _scene.Meshes[mesh];
                        ulong meshVertexBytes = (ulong)geometry.Vertices.Length * VertexStride;
                        ulong meshInstanceBytes = (ulong)geometry.RenderInstances.Length * InstanceStride;

                        if (meshVertexBytes > 0)
                        {
                            var vertexCopy = new VulkanApi.VkBufferCopy { srcOffset = vertexOffset, dstOffset = 0, size = meshVertexBytes };
                            VulkanApi.vkCmdCopyBuffer(command, staging, _vertexBuffers[mesh], 1, ref vertexCopy);
                        }
                        if (meshInstanceBytes > 0)
                        {
                            var instanceCopy = new VulkanApi.VkBufferCopy { srcOffset = instanceOffset, dstOffset = 0, size = meshInstanceBytes };
                            VulkanApi.vkCmdCopyBuffer(command, staging, _instanceBuffers[mesh], 1, ref instanceCopy);
                        }

                        // La copia tiene que terminar antes de que el pipeline lea los vértices.
                        var barrier = new VulkanApi.VkBufferMemoryBarrier
                        {
                            sType = VulkanApi.VkStructureType.BufferMemoryBarrier,
                            srcAccessMask = VulkanApi.VkAccess.TransferWrite,
                            dstAccessMask = VulkanApi.VkAccess.VertexAttributeRead,
                            srcQueueFamilyIndex = SubpassExternal,
                            dstQueueFamilyIndex = SubpassExternal,
                            buffer = _vertexBuffers[mesh],
                            offset = 0,
                            size = WholeSize
                        };
                        if (meshVertexBytes > 0)
                        {
                            VulkanApi.vkCmdPipelineBarrier(command, VulkanApi.VkPipelineStage.Transfer, VulkanApi.VkPipelineStage.VertexInput, 0,
                                0, 0, 1, ref barrier, 0, 0);
                        }
                        if (meshInstanceBytes > 0)
                        {
                            barrier.buffer = _instanceBuffers[mesh];
                            VulkanApi.vkCmdPipelineBarrier(command, VulkanApi.VkPipelineStage.Transfer, VulkanApi.VkPipelineStage.VertexInput, 0,
                                0, 0, 1, ref barrier, 0, 0);
                        }

                        vertexOffset += meshVertexBytes;
                        instanceOffset += meshInstanceBytes;
                    }
                }
            });
        }
        finally
        {
            VulkanApi.vkDestroyBuffer(_device, staging, 0);
            VulkanApi.vkFreeMemory(_device, stagingMemory, 0);
        }
    }

    // =====================================================================
    // Shadow map (ver README-SOMBRAS.md)
    // =====================================================================

    // Los campos van acá, junto a su creación, porque son de este bloque y ningún otro método los
    // toca salvo el que los crea y el que los ata a los descriptores.
    private nint _shadowImage;
    private nint _shadowMemory;
    private nint _shadowView;
    private nint _shadowSampler;

    /// <summary>
    /// El SHADOW MAP: UNA cascada, cuadrada, con el lado que pide la CONFIGURACIÓN de la corrida
    /// (<see cref="SceneGraphicsOptions.ShadowMapSize"/>). No se adapta a la cámara ni al contenido
    /// —eso es lo que hace comparable el costo por frame entre corridas—, pero sí puede cambiar entre
    /// corridas, y por eso el tamaño no es una constante de la escena.
    ///
    /// Es memoria de dispositivo que solo llena la pasada de profundidad: no se sube nada desde el CPU.
    /// Mientras la escena no encienda las sombras (<c>ShadowParams.x = 0</c>) el shader ni la muestrea,
    /// pero el recurso tiene que EXISTIR igual, porque el descriptor está escrito en todos los sets y
    /// el layout lo declara: un binding declarado sin descriptor es un error de validación en cada
    /// frame.
    ///
    /// El sampler es un sampler de COMPARACIÓN (<c>compareEnable</c>): la placa compara la profundidad
    /// del píxel contra el mapa y devuelve 0/1 ya filtrado. Con filtro lineal, eso da el promedio de 4
    /// téxeles por muestra, que es el mismo ablandado del borde que SampleCmpLevelZero en Direct3D —
    /// así las cuatro APIs ven el mismo tipo de borde sin hacer PCF de 4×4 a mano.
    /// </summary>
    private void CreateShadowResources()
    {
        if (_device == 0) return;

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
        var createInfo = new VulkanApi.VkImageCreateInfo
        {
            sType = VulkanApi.VkStructureType.ImageCreateInfo,
            imageType = VulkanApi.VkImageType.Type2D,
            format = VulkanApi.VkFormat.D32Sfloat,
            extent = new VulkanApi.VkExtent3D { width = size, height = size, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VulkanApi.VkSampleCount.Count1,
            tiling = VulkanApi.VkImageTiling.Optimal,
            // Sampled ADEMAS de DepthStencilAttachment: la pasada de profundidad la ESCRIBE y la
            // pasada principal la LEE. Sin el segundo flag la imagen no se puede atar como textura.
            usage = VulkanApi.VkImageUsage.DepthStencilAttachment | VulkanApi.VkImageUsage.Sampled,
            sharingMode = VulkanApi.VkSharingMode.Exclusive,
            initialLayout = VulkanApi.VkImageLayout.Undefined
        };
        Check(VulkanApi.vkCreateImage(_device, ref createInfo, 0, out _shadowImage), "crear la imagen del shadow map");
        VulkanApi.vkGetImageMemoryRequirements(_device, _shadowImage, out var requirements);
        _shadowMemory = AllocateMemory(requirements, VulkanApi.VkMemoryProperty.DeviceLocal, "el shadow map");
        Check(VulkanApi.vkBindImageMemory(_device, _shadowImage, _shadowMemory, 0), "atar la memoria del shadow map");

        var viewInfo = new VulkanApi.VkImageViewCreateInfo
        {
            sType = VulkanApi.VkStructureType.ImageViewCreateInfo,
            image = _shadowImage,
            viewType = VulkanApi.VkImageViewType.Type2D,
            format = VulkanApi.VkFormat.D32Sfloat,
            subresourceRange = new VulkanApi.VkImageSubresourceRange
            {
                aspectMask = VulkanApi.VkImageAspect.Depth,
                levelCount = 1,
                layerCount = 1
            }
        };
        Check(VulkanApi.vkCreateImageView(_device, ref viewInfo, 0, out _shadowView), "crear la vista del shadow map");

        var samplerInfo = new VulkanApi.VkSamplerCreateInfo
        {
            sType = VulkanApi.VkStructureType.SamplerCreateInfo,
            magFilter = VulkanApi.VkFilter.Linear,
            minFilter = VulkanApi.VkFilter.Linear,
            mipmapMode = VulkanApi.VkSamplerMipmapMode.Nearest,
            // ClampToEdge y no Repeat: el mapa NO se repite. Fuera de él el shader devuelve "sin
            // sombra" (ver ShadowFactor), pero si el filtrado llega al borde, repetir traería el
            // profundidad del lado opuesto del mundo.
            addressModeU = VulkanApi.VkSamplerAddressMode.ClampToEdge,
            addressModeV = VulkanApi.VkSamplerAddressMode.ClampToEdge,
            addressModeW = VulkanApi.VkSamplerAddressMode.ClampToEdge,
            anisotropyEnable = 0,
            // ACÁ está la diferencia con los otros samplers: comparación activada con "menor".
            compareEnable = 1,
            compareOp = VulkanApi.VkCompareOp.Less,
            minLod = 0f,
            maxLod = 0f
        };
        Check(VulkanApi.vkCreateSampler(_device, ref samplerInfo, 0, out _shadowSampler), "crear el sampler del shadow map");

        // El render pass, su framebuffer y la pipeline que llena el mapa.
        CreateShadowRenderPass();

        UpdateShadowDescriptor();
    }

    /// <summary>
    /// Render pass de la PASADA DE SOMBRAS (ver <c>README-SOMBRAS.md</c>): un único adjunto de
    /// profundidad, sin color, de 2048² y de resolución FIJA.
    ///
    /// Los dos layouts son la parte que importa y evitan barreras a mano:
    /// <list type="bullet">
    /// <item><c>initialLayout = Undefined</c> + <c>loadOp = Clear</c>: cada frame descarta el contenido
    /// anterior —el mapa se reescribe entero— así que no hay que rastrear en qué layout quedó.</item>
    /// <item><c>finalLayout = ShaderReadOnlyOptimal</c>: la transición de "destino de profundidad" a
    /// "textura que se muestrea" la hace el PROPIO render pass al terminar. Es la forma de que la
    /// pasada de sombras no necesite ni una <c>vkCmdPipelineBarrier</c> y de que la principal encuentre
    /// el mapa listo para leer con la profundidad ya visible.</item>
    /// </list>
    ///
    /// La dependencia es la que ordena las PASADAS ENTRE FRAMES: la escritura de profundidad de este
    /// frame tiene que esperar a que el frame anterior haya terminado de muestrear el mapa. Sin ella, la
    /// placa puede escribir un téxel que todavía se está leyendo (imagen rasgada de forma
    /// intermitente, típicamente solo en las sombras).
    /// </summary>
    private void CreateShadowRenderPass()
    {
        var attachments = new[]
        {
            new VulkanApi.VkAttachmentDescription
            {
                format = VulkanApi.VkFormat.D32Sfloat,
                samples = VulkanApi.VkSampleCount.Count1,
                loadOp = VulkanApi.VkAttachmentLoadOp.Clear,
                storeOp = VulkanApi.VkAttachmentStoreOp.Store,
                stencilLoadOp = VulkanApi.VkAttachmentLoadOp.DontCare,
                stencilStoreOp = VulkanApi.VkAttachmentStoreOp.DontCare,
                initialLayout = VulkanApi.VkImageLayout.Undefined,
                finalLayout = VulkanApi.VkImageLayout.ShaderReadOnlyOptimal
            }
        };

        var depthReference = new VulkanApi.VkAttachmentReference
        {
            attachment = 0,
            layout = VulkanApi.VkImageLayout.DepthStencilAttachmentOptimal
        };
        var subpass = new VulkanApi.VkSubpassDescription
        {
            pipelineBindPoint = VulkanApi.VkPipelineBindPoint.Graphics,
            // CERO adjuntos de color: es la razón de ser de este render pass.
            colorAttachmentCount = 0,
            pDepthStencilAttachment = (nint)0
        };
        var dependency = new VulkanApi.VkSubpassDependency
        {
            srcSubpass = SubpassExternal,
            dstSubpass = 0,
            // El que LEE el mapa es el shader de fragmentos de la pasada principal.
            srcStageMask = VulkanApi.VkPipelineStage.FragmentShader,
            srcAccessMask = VulkanApi.VkAccess.ShaderRead,
            // El que lo ESCRIBE es la etapa de profundidad de esta pasada.
            dstStageMask = VulkanApi.VkPipelineStage.EarlyFragmentTests | VulkanApi.VkPipelineStage.LateFragmentTests,
            dstAccessMask = VulkanApi.VkAccess.DepthStencilAttachmentWrite
        };

        unsafe
        {
            fixed (VulkanApi.VkAttachmentDescription* attachmentPointer = attachments)
            {
                subpass.pDepthStencilAttachment = (nint)(&depthReference);

                var createInfo = new VulkanApi.VkRenderPassCreateInfo
                {
                    sType = VulkanApi.VkStructureType.RenderPassCreateInfo,
                    attachmentCount = 1,
                    pAttachments = (nint)attachmentPointer,
                    subpassCount = 1,
                    pSubpasses = (nint)(&subpass),
                    dependencyCount = 1,
                    pDependencies = (nint)(&dependency)
                };
                Check(VulkanApi.vkCreateRenderPass(_device, ref createInfo, 0, out _shadowRenderPass),
                    "crear el render pass del shadow map");
            }
        }

        var attachmentViews = new[] { _shadowView };
        unsafe
        {
            fixed (nint* views = attachmentViews)
            {
                uint size = (uint)_graphics.ShadowMapSize;
                var createInfo = new VulkanApi.VkFramebufferCreateInfo
                {
                    sType = VulkanApi.VkStructureType.FramebufferCreateInfo,
                    renderPass = _shadowRenderPass,
                    attachmentCount = 1,
                    pAttachments = (nint)views,
                    width = size,
                    height = size,
                    layers = 1
                };
                Check(VulkanApi.vkCreateFramebuffer(_device, ref createInfo, 0, out _shadowFramebuffer),
                    "crear el framebuffer del shadow map");
            }
        }

        // La pipeline reusa el VERTEX SHADER de la geometría (con la matriz del sol en las constantes
        // dibuja la escena desde la luz) y no tiene etapa de fragmentos.
        _shadowPipeline = CreatePipeline(VulkanShaders.GeometryVertex, "VSMain", VulkanShaders.GeometryPixel, "PSMain",
            geometry: true, renderPass: _shadowRenderPass, depthOnly: true);
    }

    /// <summary>Apunta los bindings 8 (la imagen) y 7 (el sampler de comparación) de CADA descriptor
    /// set, igual que hace el atado del detalle y de los materiales: ningún dibujo cambia un
    /// descriptor.</summary>
    private void UpdateShadowDescriptor()
    {
        if (_shadowView == 0 || _shadowSampler == 0 || _descriptorSets.Length == 0) return;

        unsafe
        {
            for (int i = 0; i < _descriptorSets.Length; i++)
            {
                var imageInfo = new VulkanApi.VkDescriptorImageInfo
                {
                    sampler = 0,
                    imageView = _shadowView,
                    imageLayout = VulkanApi.VkImageLayout.ShaderReadOnlyOptimal
                };
                var imageWrite = new VulkanApi.VkWriteDescriptorSet
                {
                    sType = VulkanApi.VkStructureType.WriteDescriptorSet,
                    dstSet = _descriptorSets[i],
                    dstBinding = 8,
                    descriptorCount = 1,
                    descriptorType = VulkanApi.VkDescriptorType.SampledImage,
                    pImageInfo = (nint)(&imageInfo)
                };
                VulkanApi.vkUpdateDescriptorSets(_device, 1, (nint)(&imageWrite), 0, 0);

                var samplerInfo = new VulkanApi.VkDescriptorImageInfo
                {
                    sampler = _shadowSampler,
                    imageView = 0,
                    imageLayout = VulkanApi.VkImageLayout.Undefined
                };
                var samplerWrite = new VulkanApi.VkWriteDescriptorSet
                {
                    sType = VulkanApi.VkStructureType.WriteDescriptorSet,
                    dstSet = _descriptorSets[i],
                    dstBinding = 7,
                    descriptorCount = 1,
                    descriptorType = VulkanApi.VkDescriptorType.Sampler,
                    pImageInfo = (nint)(&samplerInfo)
                };
                VulkanApi.vkUpdateDescriptorSets(_device, 1, (nint)(&samplerWrite), 0, 0);
            }
        }
    }

    private void CreateUniformBuffers()
    {
        // DOS bloques por frame en vuelo: el de la pasada de sombras (índice par) y el de la pasada
        // principal (impar). Un solo bloque por frame no alcanza porque las dos pasadas del mismo
        // frame llevan matrices distintas (la del sol y la de la cámara) y la memoria es visible al
        // host: la segunda escritura pisaría la primera antes de que la placa la lea.
        int blockCount = FramesInFlight * ConstantSlicesPerFrame;
        _uniformBuffers = new nint[blockCount];
        _uniformMemory = new nint[blockCount];
        _uniformMapped = new nint[blockCount];

        for (int i = 0; i < blockCount; i++)
        {
            // Y un descriptor set por bloque: en Vulkan el binding 0 apunta a UN rango de UN búfer, así
            // que cambiar de matriz entre las dos pasadas es atar otro set (barato: un comando por
            // pasada y por frame).
            _uniformBuffers[i] = CreateBuffer(ConstantBufferSize, VulkanApi.VkBufferUsage.UniformBuffer,
                VulkanApi.VkMemoryProperty.HostVisible | VulkanApi.VkMemoryProperty.HostCoherent, "constantes", out _uniformMemory[i]);
            Check(VulkanApi.vkMapMemory(_device, _uniformMemory[i], 0, ConstantBufferSize, 0, out _uniformMapped[i]), "mapear las constantes");
        }

        var sets = new nint[blockCount];
        var layouts = new nint[blockCount];
        for (int i = 0; i < blockCount; i++) layouts[i] = _descriptorSetLayout;

        unsafe
        {
            fixed (nint* layoutPointer = layouts)
            {
                var allocateInfo = new VulkanApi.VkDescriptorSetAllocateInfo
                {
                    sType = VulkanApi.VkStructureType.DescriptorSetAllocateInfo,
                    descriptorPool = _descriptorPool,
                    descriptorSetCount = (uint)blockCount,
                    pSetLayouts = (nint)layoutPointer
                };
                Check(VulkanApi.vkAllocateDescriptorSets(_device, ref allocateInfo, sets), "reservar los descriptors");
            }

            for (int i = 0; i < blockCount; i++)
            {
                var bufferInfo = new VulkanApi.VkDescriptorBufferInfo
                {
                    buffer = _uniformBuffers[i],
                    offset = 0,
                    range = ConstantBufferSize
                };
                var write = new VulkanApi.VkWriteDescriptorSet
                {
                    sType = VulkanApi.VkStructureType.WriteDescriptorSet,
                    dstSet = sets[i],
                    dstBinding = 0,
                    descriptorCount = 1,
                    descriptorType = VulkanApi.VkDescriptorType.UniformBuffer,
                    pBufferInfo = (nint)(&bufferInfo)
                };
                VulkanApi.vkUpdateDescriptorSets(_device, 1, (nint)(&write), 0, 0);
            }
        }
        _descriptorSets = sets;

        // Los sets recién existen acá: el shadow map apunta a ellos DESPUÉS de reservarlos (y otra vez
        // cuando se crea el recurso, por si el orden cambia al reordenar la inicialización).
        UpdateShadowDescriptor();
    }

    /// <summary>
    /// Buffer de la franja (un uint por píxel, RGBA8 premultiplicado) en memoria visible al host:
    /// subirla es una copia de memoria desde el hilo de render. El shader lo lee como
    /// StructuredBuffer (register t1 → binding 1 del set 0).
    ///
    /// Rehacerlo exige esperar a la placa (vkDeviceWaitIdle): destruir un buffer que una cola
    /// todavía puede estar leyendo es un uso después de liberar, y la franja cambia de tamaño solo
    /// cuando el panel crece (una o dos veces por corrida), así que esperar sale gratis.
    /// </summary>
    private void CreateOverlayBuffer(int pixelCount)
    {
        if (_device == 0) return;
        VulkanApi.vkDeviceWaitIdle(_device);

        if (_overlayMapped != 0)
        {
            try { VulkanApi.vkUnmapMemory(_device, _overlayMemory); } catch { }
            _overlayMapped = 0;
        }
        if (_overlayBuffer != 0) { VulkanApi.vkDestroyBuffer(_device, _overlayBuffer, 0); _overlayBuffer = 0; }
        if (_overlayMemory != 0) { VulkanApi.vkFreeMemory(_device, _overlayMemory, 0); _overlayMemory = 0; }

        // Nunca cero bytes: el descriptor del binding 1 tiene que apuntar a un buffer válido incluso
        // antes de la primera franja.
        ulong bytes = (ulong)Math.Max(1, pixelCount) * 4;
        _overlayBuffer = CreateBuffer(bytes, VulkanApi.VkBufferUsage.StorageBuffer,
            VulkanApi.VkMemoryProperty.HostVisible | VulkanApi.VkMemoryProperty.HostCoherent,
            "la franja de métricas", out _overlayMemory);
        _overlayBufferSize = bytes;
        _overlayPixelCount = (int)(bytes / 4);
        Check(VulkanApi.vkMapMemory(_device, _overlayMemory, 0, bytes, 0, out _overlayMapped), "mapear la franja de métricas");
        UpdateOverlayDescriptor();
        _overlayDirty = true;
    }

    /// <summary>Apunta el binding 1 de CADA descriptor set al buffer de la franja.</summary>
    private void UpdateOverlayDescriptor()
    {
        if (_overlayBuffer == 0 || _descriptorSets.Length == 0) return;

        for (int i = 0; i < _descriptorSets.Length; i++)
        {
            var bufferInfo = new VulkanApi.VkDescriptorBufferInfo
            {
                buffer = _overlayBuffer,
                offset = 0,
                range = _overlayBufferSize
            };
            unsafe
            {
                var write = new VulkanApi.VkWriteDescriptorSet
                {
                    sType = VulkanApi.VkStructureType.WriteDescriptorSet,
                    dstSet = _descriptorSets[i],
                    dstBinding = 3,
                    descriptorCount = 1,
                    descriptorType = VulkanApi.VkDescriptorType.StorageBuffer,
                    pBufferInfo = (nint)(&bufferInfo)
                };
                VulkanApi.vkUpdateDescriptorSets(_device, 1, (nint)(&write), 0, 0);
            }
        }
    }

    /// <summary>
    /// Textura de detalle (el atlas de material de <see cref="Scenes.SceneDetailTexture"/>): imagen
    /// RGBA8 en memoria de dispositivo, subida con buffer de paso y barreras de layout. Igual que en
    /// Direct3D 11 y 12, se sube UNA vez al inicializar y se queda quieta el resto de la corrida.
    ///</summary>
    private void CreateDetailTexture()
    {
        if (_device == 0) return;
        var pixels = Scenes.SceneDetailTexture.Pixels;
        uint size = (uint)Scenes.SceneDetailTexture.Size;
        ulong bytes = (ulong)pixels.Length;

        var createInfo = new VulkanApi.VkImageCreateInfo
        {
            sType = VulkanApi.VkStructureType.ImageCreateInfo,
            imageType = VulkanApi.VkImageType.Type2D,
            format = VulkanApi.VkFormat.R8G8B8A8Unorm,
            extent = new VulkanApi.VkExtent3D { width = size, height = size, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VulkanApi.VkSampleCount.Count1,
            tiling = VulkanApi.VkImageTiling.Optimal,
            // TransferDst: la copia desde el buffer de paso. Sampled: la lectura del shader.
            usage = VulkanApi.VkImageUsage.TransferDst | VulkanApi.VkImageUsage.Sampled,
            sharingMode = VulkanApi.VkSharingMode.Exclusive,
            initialLayout = VulkanApi.VkImageLayout.Undefined
        };
        Check(VulkanApi.vkCreateImage(_device, ref createInfo, 0, out _detailImage), "crear la textura de detalle");
        VulkanApi.vkGetImageMemoryRequirements(_device, _detailImage, out var requirements);
        _detailMemory = AllocateMemory(requirements, VulkanApi.VkMemoryProperty.DeviceLocal, "la textura de detalle");
        Check(VulkanApi.vkBindImageMemory(_device, _detailImage, _detailMemory, 0), "atar la memoria de la textura de detalle");

        var viewInfo = new VulkanApi.VkImageViewCreateInfo
        {
            sType = VulkanApi.VkStructureType.ImageViewCreateInfo,
            image = _detailImage,
            viewType = VulkanApi.VkImageViewType.Type2D,
            format = VulkanApi.VkFormat.R8G8B8A8Unorm,
            subresourceRange = new VulkanApi.VkImageSubresourceRange
            {
                aspectMask = VulkanApi.VkImageAspect.Color,
                levelCount = 1,
                layerCount = 1
            }
        };
        Check(VulkanApi.vkCreateImageView(_device, ref viewInfo, 0, out _detailView), "crear la vista de la textura de detalle");

        var samplerInfo = new VulkanApi.VkSamplerCreateInfo
        {
            sType = VulkanApi.VkStructureType.SamplerCreateInfo,
            magFilter = VulkanApi.VkFilter.Linear,
            minFilter = VulkanApi.VkFilter.Linear,
            mipmapMode = VulkanApi.VkSamplerMipmapMode.Linear,
            addressModeU = VulkanApi.VkSamplerAddressMode.Repeat,
            addressModeV = VulkanApi.VkSamplerAddressMode.Repeat,
            addressModeW = VulkanApi.VkSamplerAddressMode.Repeat,
            // Sin anisotropía: necesita la feature samplerAnisotropy, que no se pide al crear el
            // dispositivo (el detalle es un patrón, no una foto).
            anisotropyEnable = 0,
            compareEnable = 0,
            compareOp = VulkanApi.VkCompareOp.Never,
            minLod = 0f,
            // Un solo mip: cualquier valor >= minLod sirve; 32 es el tope documental.
            maxLod = 32f
        };
        Check(VulkanApi.vkCreateSampler(_device, ref samplerInfo, 0, out _detailSampler), "crear el sampler de la textura de detalle");

        // Subida: buffer de paso visible al host → copia → barreras de layout. Es el MISMO camino
        // de la geometría (CreateGeometryBuffers), pero para una imagen.
        nint staging = CreateBuffer(bytes, VulkanApi.VkBufferUsage.TransferSrc,
            VulkanApi.VkMemoryProperty.HostVisible | VulkanApi.VkMemoryProperty.HostCoherent,
            "paso de la textura de detalle", out nint stagingMemory);
        try
        {
            Check(VulkanApi.vkMapMemory(_device, stagingMemory, 0, bytes, 0, out nint mapped), "mapear el paso de la textura de detalle");
            try
            {
                unsafe
                {
                    fixed (byte* source = pixels)
                    {
                        Buffer.MemoryCopy(source, (void*)mapped, (long)bytes, (long)bytes);
                    }
                }
            }
            finally
            {
                VulkanApi.vkUnmapMemory(_device, stagingMemory);
            }

            RunOneShotCommandBuffer("subir la textura de detalle", command =>
            {
                unsafe
                {
                    var region = new VulkanApi.VkBufferImageCopy
                    {
                        bufferOffset = 0,
                        bufferRowLength = 0,
                        bufferImageHeight = 0,
                        imageSubresource = new VulkanApi.VkImageSubresourceLayers
                        {
                            aspectMask = VulkanApi.VkImageAspect.Color,
                            mipLevel = 0,
                            baseArrayLayer = 0,
                            layerCount = 1
                        },
                        imageOffset = default,
                        imageExtent = new VulkanApi.VkExtent3D { width = size, height = size, depth = 1 }
                    };

                    // Undefined → TransferDstOptimal: la imagen todavía no tiene contenido válido.
                    var toTransfer = new VulkanApi.VkImageMemoryBarrier
                    {
                        sType = VulkanApi.VkStructureType.ImageMemoryBarrier,
                        srcAccessMask = 0,
                        dstAccessMask = VulkanApi.VkAccess.TransferWrite,
                        oldLayout = VulkanApi.VkImageLayout.Undefined,
                        newLayout = VulkanApi.VkImageLayout.TransferDstOptimal,
                        srcQueueFamilyIndex = SubpassExternal,
                        dstQueueFamilyIndex = SubpassExternal,
                        image = _detailImage,
                        subresourceRange = new VulkanApi.VkImageSubresourceRange
                        {
                            aspectMask = VulkanApi.VkImageAspect.Color,
                            levelCount = 1,
                            layerCount = 1
                        }
                    };
                    // El delegate pasa los tres arreglos en orden (memoria, buffers, imágenes):
                    // para una barrera de imagen, la de buffers viaja con count 0 y un dummy con
                    // ref (el delegado exige ref aunque no la lea con count 0).
                    var dummyBufferBarrier = new VulkanApi.VkBufferMemoryBarrier();
                    VulkanApi.vkCmdPipelineBarrier(command, VulkanApi.VkPipelineStage.TopOfPipe, VulkanApi.VkPipelineStage.Transfer, 0,
                        0, 0, 0, ref dummyBufferBarrier, 1, (nint)(&toTransfer));

                    VulkanApi.vkCmdCopyBufferToImage(command, staging, _detailImage, VulkanApi.VkImageLayout.TransferDstOptimal, 1, ref region);

                    // TransferDstOptimal → ShaderReadOnlyOptimal: el sampler la lee en el fragmento.
                    var toShader = new VulkanApi.VkImageMemoryBarrier
                    {
                        sType = VulkanApi.VkStructureType.ImageMemoryBarrier,
                        srcAccessMask = VulkanApi.VkAccess.TransferWrite,
                        dstAccessMask = VulkanApi.VkAccess.ShaderRead,
                        oldLayout = VulkanApi.VkImageLayout.TransferDstOptimal,
                        newLayout = VulkanApi.VkImageLayout.ShaderReadOnlyOptimal,
                        srcQueueFamilyIndex = SubpassExternal,
                        dstQueueFamilyIndex = SubpassExternal,
                        image = _detailImage,
                        subresourceRange = new VulkanApi.VkImageSubresourceRange
                        {
                            aspectMask = VulkanApi.VkImageAspect.Color,
                            levelCount = 1,
                            layerCount = 1
                        }
                    };
                    VulkanApi.vkCmdPipelineBarrier(command, VulkanApi.VkPipelineStage.Transfer, VulkanApi.VkPipelineStage.FragmentShader, 0,
                        0, 0, 0, ref dummyBufferBarrier, 1, (nint)(&toShader));
                }
            });
        }
        finally
        {
            VulkanApi.vkDestroyBuffer(_device, staging, 0);
            VulkanApi.vkFreeMemory(_device, stagingMemory, 0);
        }

        UpdateDetailDescriptor();
    }

    /// <summary>
    /// Arreglo de texturas de MATERIAL de la escena (ver <see cref="Scenes.MaterialAtlas"/>): una imagen
    /// 2D con <c>MapsPerSlot</c> rebanadas por material y TODOS los mips (los genera el CPU). La vista
    /// es de tipo arreglo 2D y el sampler es el mismo del detalle. El atlas guarda los mips de cada
    /// rebanada contiguos, que es exactamente el layout que pide <c>vkCmdCopyBufferToImage</c> cuando
    /// <c>bufferRowLength</c> va en cero: la subida es una imagen por subrecurso y nada más.
    /// </summary>
    private void CreateMaterialTexture()
    {
        if (_device == 0) return;
        var atlas = _scene.Materials;
        uint slices = (uint)atlas.SliceCount;
        uint mips = (uint)atlas.MipCount;
        ulong bytes = (ulong)atlas.Pixels.Length;

        var createInfo = new VulkanApi.VkImageCreateInfo
        {
            sType = VulkanApi.VkStructureType.ImageCreateInfo,
            imageType = VulkanApi.VkImageType.Type2D,
            format = VulkanApi.VkFormat.R8G8B8A8Unorm,
            extent = new VulkanApi.VkExtent3D { width = (uint)atlas.Size, height = (uint)atlas.Size, depth = 1 },
            mipLevels = mips,
            arrayLayers = slices,
            samples = VulkanApi.VkSampleCount.Count1,
            tiling = VulkanApi.VkImageTiling.Optimal,
            usage = VulkanApi.VkImageUsage.TransferDst | VulkanApi.VkImageUsage.Sampled,
            sharingMode = VulkanApi.VkSharingMode.Exclusive,
            initialLayout = VulkanApi.VkImageLayout.Undefined
        };
        Check(VulkanApi.vkCreateImage(_device, ref createInfo, 0, out _materialImage), "crear el arreglo de materiales");
        VulkanApi.vkGetImageMemoryRequirements(_device, _materialImage, out var requirements);
        _materialMemory = AllocateMemory(requirements, VulkanApi.VkMemoryProperty.DeviceLocal, "el arreglo de materiales");
        Check(VulkanApi.vkBindImageMemory(_device, _materialImage, _materialMemory, 0), "atar la memoria del arreglo de materiales");

        var viewInfo = new VulkanApi.VkImageViewCreateInfo
        {
            sType = VulkanApi.VkStructureType.ImageViewCreateInfo,
            image = _materialImage,
            viewType = VulkanApi.VkImageViewType.Type2DArray,
            format = VulkanApi.VkFormat.R8G8B8A8Unorm,
            subresourceRange = new VulkanApi.VkImageSubresourceRange
            {
                aspectMask = VulkanApi.VkImageAspect.Color,
                levelCount = mips,
                layerCount = slices
            }
        };
        Check(VulkanApi.vkCreateImageView(_device, ref viewInfo, 0, out _materialView), "crear la vista del arreglo de materiales");

        var samplerInfo = new VulkanApi.VkSamplerCreateInfo
        {
            sType = VulkanApi.VkStructureType.SamplerCreateInfo,
            magFilter = VulkanApi.VkFilter.Linear,
            minFilter = VulkanApi.VkFilter.Linear,
            mipmapMode = VulkanApi.VkSamplerMipmapMode.Linear,
            addressModeU = VulkanApi.VkSamplerAddressMode.Repeat,
            addressModeV = VulkanApi.VkSamplerAddressMode.Repeat,
            addressModeW = VulkanApi.VkSamplerAddressMode.Repeat,
            anisotropyEnable = 0,
            compareEnable = 0,
            compareOp = VulkanApi.VkCompareOp.Never,
            minLod = 0f,
            maxLod = 32f
        };
        Check(VulkanApi.vkCreateSampler(_device, ref samplerInfo, 0, out _materialSampler), "crear el sampler del arreglo de materiales");

        nint staging = CreateBuffer(bytes, VulkanApi.VkBufferUsage.TransferSrc,
            VulkanApi.VkMemoryProperty.HostVisible | VulkanApi.VkMemoryProperty.HostCoherent,
            "paso del arreglo de materiales", out nint stagingMemory);
        try
        {
            Check(VulkanApi.vkMapMemory(_device, stagingMemory, 0, bytes, 0, out nint mapped), "mapear el paso del arreglo de materiales");
            try
            {
                unsafe
                {
                    fixed (byte* source = atlas.Pixels)
                    {
                        Buffer.MemoryCopy(source, (void*)mapped, (long)bytes, (long)bytes);
                    }
                }
            }
            finally
            {
                VulkanApi.vkUnmapMemory(_device, stagingMemory);
            }

            RunOneShotCommandBuffer("subir el arreglo de materiales", command =>
            {
                unsafe
                {
                    var fullRange = new VulkanApi.VkImageSubresourceRange
                    {
                        aspectMask = VulkanApi.VkImageAspect.Color,
                        levelCount = mips,
                        layerCount = slices
                    };
                    var dummyBufferBarrier = new VulkanApi.VkBufferMemoryBarrier();

                    var toTransfer = new VulkanApi.VkImageMemoryBarrier
                    {
                        sType = VulkanApi.VkStructureType.ImageMemoryBarrier,
                        srcAccessMask = 0,
                        dstAccessMask = VulkanApi.VkAccess.TransferWrite,
                        oldLayout = VulkanApi.VkImageLayout.Undefined,
                        newLayout = VulkanApi.VkImageLayout.TransferDstOptimal,
                        srcQueueFamilyIndex = SubpassExternal,
                        dstQueueFamilyIndex = SubpassExternal,
                        image = _materialImage,
                        subresourceRange = fullRange
                    };
                    VulkanApi.vkCmdPipelineBarrier(command, VulkanApi.VkPipelineStage.TopOfPipe, VulkanApi.VkPipelineStage.Transfer, 0,
                        0, 0, 0, ref dummyBufferBarrier, 1, (nint)(&toTransfer));

                    for (int slice = 0; slice < atlas.SliceCount; slice++)
                    {
                        for (int level = 0; level < atlas.MipCount; level++)
                        {
                            int side = atlas.MipSize(level);
                            var region = new VulkanApi.VkBufferImageCopy
                            {
                                bufferOffset = (ulong)(slice * atlas.SliceBytes + atlas.MipOffsets[level]),
                                bufferRowLength = 0,
                                bufferImageHeight = 0,
                                imageSubresource = new VulkanApi.VkImageSubresourceLayers
                                {
                                    aspectMask = VulkanApi.VkImageAspect.Color,
                                    mipLevel = (uint)level,
                                    baseArrayLayer = (uint)slice,
                                    layerCount = 1
                                },
                                imageOffset = default,
                                imageExtent = new VulkanApi.VkExtent3D { width = (uint)side, height = (uint)side, depth = 1 }
                            };
                            VulkanApi.vkCmdCopyBufferToImage(command, staging, _materialImage,
                                VulkanApi.VkImageLayout.TransferDstOptimal, 1, ref region);
                        }
                    }

                    var toShader = new VulkanApi.VkImageMemoryBarrier
                    {
                        sType = VulkanApi.VkStructureType.ImageMemoryBarrier,
                        srcAccessMask = VulkanApi.VkAccess.TransferWrite,
                        dstAccessMask = VulkanApi.VkAccess.ShaderRead,
                        oldLayout = VulkanApi.VkImageLayout.TransferDstOptimal,
                        newLayout = VulkanApi.VkImageLayout.ShaderReadOnlyOptimal,
                        srcQueueFamilyIndex = SubpassExternal,
                        dstQueueFamilyIndex = SubpassExternal,
                        image = _materialImage,
                        subresourceRange = fullRange
                    };
                    VulkanApi.vkCmdPipelineBarrier(command, VulkanApi.VkPipelineStage.Transfer, VulkanApi.VkPipelineStage.FragmentShader, 0,
                        0, 0, 0, ref dummyBufferBarrier, 1, (nint)(&toShader));
                }
            });
        }
        finally
        {
            VulkanApi.vkDestroyBuffer(_device, staging, 0);
            VulkanApi.vkFreeMemory(_device, stagingMemory, 0);
        }

        // Los descriptors del arreglo se escriben ACÁ y no en CreateDetailTexture: cuando el detalle
        // los escribe, esta imagen todavía no existe (la vista es 0) y el binding 6 se queda sin
        // apuntar a nada — el shader muestrea un descriptor indefinido y la escena sale con el
        // material neutro (blanco), sin error ni advertencia.
        UpdateDetailDescriptor();
    }

    /// <summary>
    /// Apunta los bindings 2 (imagen del detalle), 4 (su sampler), 6 (arreglo de materiales) y 5 (su
    /// sampler) de CADA descriptor set. Se escribe una sola vez: el arreglo de materiales es UNO por
    /// escena y el material de cada objeto es un índice que viaja por instancia, así que ningún
    /// dibujo cambia un descriptor (que es lo que costaría una textura por material).
    /// </summary>
    private void UpdateDetailDescriptor()
    {
        if (_detailView == 0 || _detailSampler == 0 || _descriptorSets.Length == 0) return;

        unsafe
        {
            for (int i = 0; i < _descriptorSets.Length; i++)
            {
                // El arreglo de materiales, si la escena tiene alguno (ver MaterialAtlas).
                if (_materialView != 0)
                {
                    var materialImage = new VulkanApi.VkDescriptorImageInfo
                    {
                        sampler = 0,
                        imageView = _materialView,
                        imageLayout = VulkanApi.VkImageLayout.ShaderReadOnlyOptimal
                    };
                    var materialImageWrite = new VulkanApi.VkWriteDescriptorSet
                    {
                        sType = VulkanApi.VkStructureType.WriteDescriptorSet,
                        dstSet = _descriptorSets[i],
                        dstBinding = 6,
                        descriptorCount = 1,
                        descriptorType = VulkanApi.VkDescriptorType.SampledImage,
                        pImageInfo = (nint)(&materialImage)
                    };
                    VulkanApi.vkUpdateDescriptorSets(_device, 1, (nint)(&materialImageWrite), 0, 0);

                    var materialSampler = new VulkanApi.VkDescriptorImageInfo
                    {
                        sampler = _materialSampler,
                        imageView = 0,
                        imageLayout = VulkanApi.VkImageLayout.Undefined
                    };
                    var materialSamplerWrite = new VulkanApi.VkWriteDescriptorSet
                    {
                        sType = VulkanApi.VkStructureType.WriteDescriptorSet,
                        dstSet = _descriptorSets[i],
                        dstBinding = 5,
                        descriptorCount = 1,
                        descriptorType = VulkanApi.VkDescriptorType.Sampler,
                        pImageInfo = (nint)(&materialSampler)
                    };
                    VulkanApi.vkUpdateDescriptorSets(_device, 1, (nint)(&materialSamplerWrite), 0, 0);
                }

                // La imagen viaja en ShaderReadOnlyOptimal: es el layout que el descriptor espera.
                var imageInfo = new VulkanApi.VkDescriptorImageInfo
                {
                    sampler = 0,
                    imageView = _detailView,
                    imageLayout = VulkanApi.VkImageLayout.ShaderReadOnlyOptimal
                };
                var samplerInfo = new VulkanApi.VkDescriptorImageInfo
                {
                    sampler = _detailSampler,
                    imageView = 0,
                    imageLayout = VulkanApi.VkImageLayout.Undefined
                };

                var imageWrite = new VulkanApi.VkWriteDescriptorSet
                {
                    sType = VulkanApi.VkStructureType.WriteDescriptorSet,
                    dstSet = _descriptorSets[i],
                    dstBinding = 2,
                    descriptorCount = 1,
                    descriptorType = VulkanApi.VkDescriptorType.SampledImage,
                    pImageInfo = (nint)(&imageInfo)
                };
                VulkanApi.vkUpdateDescriptorSets(_device, 1, (nint)(&imageWrite), 0, 0);

                var samplerWrite = new VulkanApi.VkWriteDescriptorSet
                {
                    sType = VulkanApi.VkStructureType.WriteDescriptorSet,
                    dstSet = _descriptorSets[i],
                    dstBinding = 4,
                    descriptorCount = 1,
                    descriptorType = VulkanApi.VkDescriptorType.Sampler,
                    pImageInfo = (nint)(&samplerInfo)
                };
                VulkanApi.vkUpdateDescriptorSets(_device, 1, (nint)(&samplerWrite), 0, 0);
            }
        }
    }

    /// <summary>Copia los píxeles pendientes al buffer (una vez por cambio, no por frame).</summary>
    private void UpdateOverlayBuffer()
    {
        if (!_overlayDirty || _overlayMapped == 0 || _overlayPending == null) return;
        _overlayDirty = false;

        int bytes = Math.Min(_overlayPending.Length, _overlayPixelCount * 4);
        unsafe
        {
            fixed (byte* source = _overlayPending)
            {
                Buffer.MemoryCopy(source, (void*)_overlayMapped, _overlayPixelCount * 4, bytes);
            }
        }
    }

    private void CreateCommandResources()
    {
        var poolInfo = new VulkanApi.VkCommandPoolCreateInfo
        {
            sType = VulkanApi.VkStructureType.CommandPoolCreateInfo,
            // ResetCommandBuffer es obligatorio para poder reusar el mismo command buffer por frame.
            flags = VulkanApi.VkCommandPoolCreate.ResetCommandBuffer,
            queueFamilyIndex = _queueFamily
        };
        Check(VulkanApi.vkCreateCommandPool(_device, ref poolInfo, 0, out _commandPool), "crear el pool de comandos");

        var allocateInfo = new VulkanApi.VkCommandBufferAllocateInfo
        {
            sType = VulkanApi.VkStructureType.CommandBufferAllocateInfo,
            commandPool = _commandPool,
            level = VulkanApi.VkCommandBufferLevel.Primary,
            commandBufferCount = FramesInFlight
        };
        _commandBuffers = new nint[FramesInFlight];
        Check(VulkanApi.vkAllocateCommandBuffers(_device, ref allocateInfo, _commandBuffers), "reservar los command buffers");

        _frameFences = new nint[FramesInFlight];
        _imageAvailable = new nint[FramesInFlight];
        _timingPending = new bool[FramesInFlight];
        for (int i = 0; i < FramesInFlight; i++)
        {
            var fenceInfo = new VulkanApi.VkFenceCreateInfo
            {
                sType = VulkanApi.VkStructureType.FenceCreateInfo,
                // Nacen señaladas: el primer frame no tiene nada que esperar.
                flags = VulkanApi.VkFenceCreate.Signaled
            };
            Check(VulkanApi.vkCreateFence(_device, ref fenceInfo, 0, out _frameFences[i]), "crear una valla");
            _imageAvailable[i] = CreateSemaphore();
        }

        if (!HasGpuTiming) return;

        _queryPools = new nint[FramesInFlight];
        for (int i = 0; i < FramesInFlight; i++)
        {
            var queryInfo = new VulkanApi.VkQueryPoolCreateInfo
            {
                sType = VulkanApi.VkStructureType.QueryPoolCreateInfo,
                queryType = VulkanApi.VkQueryType.Timestamp,
                queryCount = 2
            };
            Check(VulkanApi.vkCreateQueryPool(_device, ref queryInfo, 0, out _queryPools[i]), "crear el pool de timestamps");
        }
    }

    /// <summary>Ejecuta un command buffer de una sola vez y espera a que termine (cargas iniciales).</summary>
    private void RunOneShotCommandBuffer(string what, Action<nint> record)
    {
        var allocateInfo = new VulkanApi.VkCommandBufferAllocateInfo
        {
            sType = VulkanApi.VkStructureType.CommandBufferAllocateInfo,
            commandPool = _commandPool,
            level = VulkanApi.VkCommandBufferLevel.Primary,
            commandBufferCount = 1
        };
        var buffers = new nint[1];
        Check(VulkanApi.vkAllocateCommandBuffers(_device, ref allocateInfo, buffers), $"reservar un command buffer para {what}");

        var fenceInfo = new VulkanApi.VkFenceCreateInfo { sType = VulkanApi.VkStructureType.FenceCreateInfo };
        Check(VulkanApi.vkCreateFence(_device, ref fenceInfo, 0, out nint fence), $"crear la valla de {what}");
        try
        {
            var beginInfo = new VulkanApi.VkCommandBufferBeginInfo
            {
                sType = VulkanApi.VkStructureType.CommandBufferBeginInfo,
                flags = VulkanApi.VkCommandBufferUsage.OneTimeSubmit
            };
            Check(VulkanApi.vkBeginCommandBuffer(buffers[0], ref beginInfo), $"empezar a grabar {what}");
            record(buffers[0]);
            Check(VulkanApi.vkEndCommandBuffer(buffers[0]), $"terminar de grabar {what}");

            unsafe
            {
                fixed (nint* bufferPointer = buffers)
                {
                    var submit = new VulkanApi.VkSubmitInfo
                    {
                        sType = VulkanApi.VkStructureType.SubmitInfo,
                        commandBufferCount = 1,
                        pCommandBuffers = (nint)bufferPointer
                    };
                    Check(VulkanApi.vkQueueSubmit(_queue, 1, ref submit, fence), $"enviar {what}");
                }

                nint* fences = stackalloc nint[1];
                fences[0] = fence;
                Check(VulkanApi.vkWaitForFences(_device, 1, (nint)fences, 1, VulkanApi.UInt64Max), $"esperar {what}");
            }
        }
        finally
        {
            VulkanApi.vkDestroyFence(_device, fence, 0);
            vkFreeCommandBuffers(_device, _commandPool, 1, buffers);
        }
    }

    // =====================================================================
    // Frame
    // =====================================================================

    /// <summary>El tamaño de la ventana cambió: el swapchain se rehace en el próximo frame.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(64, width);
        height = Math.Max(64, height);
        if (width == _width && height == _height && !_swapchainDirty) return;
        _width = width;
        _height = height;
        _swapchainDirty = true;
    }

    public void SetFullscreen(bool exclusive)
    {
        // No implementada en esta API: se informa en vez de fingir que sí (ver el comentario de la clase).
        ExclusiveFullscreenAccepted = !exclusive;
    }

    public void RenderFrame(double timeSeconds)
    {
        if (_swapchainDirty) RecreateSwapchain();

        int slot = _frameIndex;
        double waitStart = VulkanApi.NowMs();

        // 1) La valla de esta ranura. Todavía NO se reinicia: se reinicia recién cuando se sabe que
        //    el frame se va a enviar. Si se reiniciara acá y el acquire fallara, la valla quedaría
        //    sin señalizar y la espera del próximo frame no terminaría nunca.
        WaitForFrameFence(slot);

        // 1b) La franja de métricas: recién acá la valla de esta ranura garantizó que la placa
        //     terminó su último frame, así que copiar los píxeles no puede pisar una lectura viva.
        if (HasOverlay) UpdateOverlayBuffer();

        // 2) Imagen libre del swapchain. Si todas están en uso esto bloquea: es placa atrasada, no
        //    trabajo del CPU, y por eso entra en la espera de cola.
        int acquired = VulkanApi.vkAcquireNextImageKHR(_device, _swapchain, VulkanApi.UInt64Max, _imageAvailable[slot], 0, out uint imageIndex);
        if (acquired == VkResult.ErrorOutOfDateKhr || acquired == VkResult.SuboptimalKhr)
        {
            _swapchainDirty = true;
            LastQueueWaitMs = 0;
            return;
        }
        if (acquired == VkResult.ErrorDeviceLost || acquired == VkResult.ErrorSurfaceLostKhr)
        {
            DeviceLost = true;
            throw new InvalidOperationException("Vulkan perdió el dispositivo gráfico durante la corrida.");
        }
        Check(acquired, "tomar una imagen del swapchain");

        LastQueueWaitMs = VulkanApi.NowMs() - waitStart;
        _lastImageIndex = imageIndex;

        // 3) La muestra de GPU que ya está lista es la de esta ranura (dos frames atrás).
        ReadGpuTiming(slot);

        // 4) Grabar el frame.
        nint command = _commandBuffers[slot];
        VulkanApi.vkResetCommandBuffer(command, 0);
        var beginInfo = new VulkanApi.VkCommandBufferBeginInfo
        {
            sType = VulkanApi.VkStructureType.CommandBufferBeginInfo,
            flags = VulkanApi.VkCommandBufferUsage.OneTimeSubmit
        };
        Check(VulkanApi.vkBeginCommandBuffer(command, ref beginInfo), "empezar a grabar el frame");

        if (HasGpuTiming)
        {
            VulkanApi.vkCmdResetQueryPool(command, _queryPools[slot], 0, 2);
            VulkanApi.vkCmdWriteTimestamp(command, VulkanApi.VkPipelineStage.TopOfPipe, _queryPools[slot], 0);
        }

        RecordFrame(command, imageIndex, timeSeconds);

        if (HasGpuTiming)
        {
            VulkanApi.vkCmdWriteTimestamp(command, VulkanApi.VkPipelineStage.BottomOfPipe, _queryPools[slot], 1);
            _timingPending[slot] = true;
        }
        Check(VulkanApi.vkEndCommandBuffer(command), "terminar de grabar el frame");

        // 5) Recién ahora se reinicia la valla: el frame se envía sí o sí.
        ResetFrameFence(slot);
        Submit(command, slot, imageIndex);
        _frameIndex = (slot + 1) % FramesInFlight;
    }

    private void WaitForFrameFence(int slot)
    {
        unsafe
        {
            nint* fences = stackalloc nint[1];
            fences[0] = _frameFences[slot];
            Check(VulkanApi.vkWaitForFences(_device, 1, (nint)fences, 1, VulkanApi.UInt64Max), "esperar el frame anterior");
        }
    }

    private void ResetFrameFence(int slot)
    {
        unsafe
        {
            nint* fences = stackalloc nint[1];
            fences[0] = _frameFences[slot];
            Check(VulkanApi.vkResetFences(_device, 1, (nint)fences), "reiniciar la valla");
        }
    }

    private void RecordFrame(nint command, uint imageIndex, double timeSeconds)
    {
        // ---- Pasada de sombras (ver README-SOMBRAS.md) ----
        // Va PRIMERO y en su propio render pass: la ventana todavía no se tocó (su render pass abre
        // más abajo), y el mapa queda en ShaderReadOnlyOptimal —la transición la hizo el render pass al
        // terminar— listo para que lo muestree la pasada principal.
        if (_shadowEnabled) RecordShadowPass(command, timeSeconds);

        // ALTURA NEGATIVA: es lo que hace que el NDC caiga en el framebuffer igual que en Direct3D.
        // Sin esto la escena y el degradado del cielo salen espejados y el informe compararía dos
        // imágenes distintas. Es núcleo de Vulkan 1.1, y por eso el backend pide 1.1.
        var viewport = new VulkanApi.VkViewport
        {
            x = 0,
            y = _height,
            width = _width,
            height = -_height,
            minDepth = 0,
            maxDepth = 1
        };
        VulkanApi.vkCmdSetViewport(command, 0, 1, ref viewport);

        var renderArea = new VulkanApi.VkRect2D
        {
            offset = new VulkanApi.VkOffset2D { x = 0, y = 0 },
            extent = new VulkanApi.VkExtent2D { width = (uint)_width, height = (uint)_height }
        };
        VulkanApi.vkCmdSetScissor(command, 0, 1, ref renderArea);

        UpdateConstants(timeSeconds);

        // Mismo orden que en Direct3D: el fondo primero (sin profundidad) y la geometría encima. El
        // valor de limpieza de la profundidad es 1, el fondo lejano.
        var clearValues = new[]
        {
            new VulkanApi.VkClearValue { color = new VulkanApi.VkClearColorValue { r = 0.02f, g = 0.02f, b = 0.03f, a = 1f } },
            new VulkanApi.VkClearValue { depthStencil = new VulkanApi.VkClearDepthStencilValue { depth = 1f, stencil = 0 } }
        };

        unsafe
        {
            fixed (VulkanApi.VkClearValue* clears = clearValues)
            {
                var beginInfo = new VulkanApi.VkRenderPassBeginInfo
                {
                    sType = VulkanApi.VkStructureType.RenderPassBeginInfo,
                    renderPass = _renderPass,
                    framebuffer = _framebuffers[imageIndex],
                    renderArea = renderArea,
                    clearValueCount = 2,
                    pClearValues = (nint)clears
                };
                VulkanApi.vkCmdBeginRenderPass(command, ref beginInfo, VulkanApi.VkSubpassContents.Inline);
            }

            // El descriptor set se ata ANTES del primer dibujo: las pipelines comparten el mismo layout
            // (constantes en el binding 0, franja en el 1) y el cielo también lo lee.
            //
            // El set es el IMPAR de la ranura: cada frame tiene dos (la pasada de sombras usa el par y
            // la principal el impar) porque llevan bloques de constantes distintos.
            nint descriptorSet = _descriptorSets[_frameIndex * ConstantSlicesPerFrame + 1];
            VulkanApi.vkCmdBindDescriptorSets(command, VulkanApi.VkPipelineBindPoint.Graphics, _pipelineLayout, 0, 1, (nint)(&descriptorSet), 0, 0);

            VulkanApi.vkCmdBindPipeline(command, VulkanApi.VkPipelineBindPoint.Graphics, _skyPipeline);
            VulkanApi.vkCmdDraw(command, 3, 1, 0, 0);

            VulkanApi.vkCmdBindPipeline(command, VulkanApi.VkPipelineBindPoint.Graphics, _geometryPipeline);

            for (int mesh = 0; mesh < _vertexBuffers.Length; mesh++)
            {
                // La malla con mezcla (el polen) va ÚLTIMA y con su pipeline: blend premultiplicado
                // y sin escribir profundidad (ver SceneMesh.Blend).
                bool blend = _scene.Meshes[mesh].Blend;
                if (blend) VulkanApi.vkCmdBindPipeline(command, VulkanApi.VkPipelineBindPoint.Graphics, _blendPipeline);

                var buffers = new[] { _vertexBuffers[mesh], _instanceBuffers[mesh] };
                var offsets = new ulong[] { 0, 0 };
                fixed (nint* bufferPointer = buffers)
                fixed (ulong* offsetPointer = offsets)
                {
                    VulkanApi.vkCmdBindVertexBuffers(command, 0, 2, (nint)bufferPointer, (nint)offsetPointer);
                }

                VulkanApi.vkCmdDraw(command, (uint)_meshVertexCounts[mesh], (uint)_meshInstanceCounts[mesh], 0, 0);

                if (blend) VulkanApi.vkCmdBindPipeline(command, VulkanApi.VkPipelineBindPoint.Graphics, _geometryPipeline);
            }

            // ---- Franja de métricas (dentro del frame) ----
            // Última pasada: la franja va SOBRE la escena. Reusa los shaders del fondo, la misma
            // pipeline sin profundidad (con blend) y el descriptor set que ya está atado.
            if (HasOverlay)
            {
                VulkanApi.vkCmdBindPipeline(command, VulkanApi.VkPipelineBindPoint.Graphics, _overlayPipeline);
                // 6 vértices desde el 0: el quad lo arma VSSkyOverlay (ver SceneShaders).
                VulkanApi.vkCmdDraw(command, 6, 1, 0, 0);
            }
        }

        VulkanApi.vkCmdEndRenderPass(command);
    }

    private void Submit(nint command, int slot, uint imageIndex)
    {
        unsafe
        {
            nint waitSemaphore = _imageAvailable[slot];
            nint signalSemaphore = _presentSemaphores[imageIndex];
            int waitStage = VulkanApi.VkPipelineStage.ColorAttachmentOutput;

            var submit = new VulkanApi.VkSubmitInfo
            {
                sType = VulkanApi.VkStructureType.SubmitInfo,
                waitSemaphoreCount = 1,
                pWaitSemaphores = (nint)(&waitSemaphore),
                pWaitDstStageMask = (nint)(&waitStage),
                commandBufferCount = 1,
                pCommandBuffers = (nint)(&command),
                signalSemaphoreCount = 1,
                pSignalSemaphores = (nint)(&signalSemaphore)
            };
            Check(VulkanApi.vkQueueSubmit(_queue, 1, ref submit, _frameFences[slot]), "enviar el frame");
        }
    }

    /// <summary>
    /// Presenta y devuelve los ms que tardó la entrega. El semáforo de "frame presentado" se espera
    /// acá y no en el envío: la cola de presentación es la que espera al monitor.
    /// </summary>
    public double Present()
    {
        // Si el frame no llegó a enviarse (swapchain desactualizado) no hay nada que presentar: la
        // espera del semáforo se quedaría colgada para siempre.
        if (_swapchainDirty) return 0;

        uint imageIndex = _lastImageIndex;
        double start = VulkanApi.NowMs();
        int result;
        unsafe
        {
            nint swapchain = _swapchain;
            nint semaphore = _presentSemaphores[imageIndex];
            var presentInfo = new VulkanApi.VkPresentInfoKHR
            {
                sType = VulkanApi.VkStructureType.PresentInfoKhr,
                waitSemaphoreCount = 1,
                pWaitSemaphores = (nint)(&semaphore),
                swapchainCount = 1,
                pSwapchains = (nint)(&swapchain),
                pImageIndices = (nint)(&imageIndex)
            };
            result = VulkanApi.vkQueuePresentKHR(_queue, ref presentInfo);
        }
        double elapsed = VulkanApi.NowMs() - start;

        if (result == VkResult.ErrorOutOfDateKhr || result == VkResult.SuboptimalKhr)
        {
            _swapchainDirty = true;
            return elapsed;
        }
        if (result == VkResult.ErrorDeviceLost)
        {
            DeviceLost = true;
            throw new InvalidOperationException("Vulkan perdió el dispositivo gráfico al presentar el frame.");
        }
        Check(result, "presentar el frame");
        return elapsed;
    }

    /// <summary>
    /// Lee los timestamps de la ranura SIN esperar: si el dato no está listo, ese frame queda sin
    /// muestra de GPU (el informe dice cuántas hubo). Esperarlo obligaría a la placa a terminar el
    /// frame en curso, o sea el instrumento frenaría lo que está midiendo.
    /// </summary>
    private void ReadGpuTiming(int slot)
    {
        if (!HasGpuTiming || !_timingPending[slot]) return;
        _timingPending[slot] = false;

        unsafe
        {
            ulong* values = stackalloc ulong[2];
            int result = VulkanApi.vkGetQueryPoolResults(_device, _queryPools[slot], 0, 2,
                (nuint)(sizeof(ulong) * 2), (nint)values, sizeof(ulong), VulkanApi.VkQueryResult.QueryResult64Bit);
            if (result != VkResult.Success) return;
            if (values[1] <= values[0]) return;
            LastGpuFrameMs = (values[1] - values[0]) * _timestampPeriodNs / 1_000_000.0;
        }
    }

    /// <summary>
    /// Graba la pasada de PROFUNDIDAD del shadow map (ver <c>README-SOMBRAS.md</c>): la escena vista
    /// desde el sol, en su render pass solo-profundidad (que además deja el mapa en
    /// <c>ShaderReadOnlyOptimal</c> al terminar, así que no hace falta ninguna barrera a mano), con el
    /// VERTEX SHADER de la geometría y sin etapa de fragmentos.
    ///
    /// El viewport lleva la ALTURA NEGATIVA igual que la pasada principal: es lo que hace que el píxel
    /// que esta pasada escribe sea el mismo que el shader va a muestrear después. Sin el signo, en
    /// Vulkan el mapa queda espejado en Y y las sombras aparecen arriba de los objetos en lugar de
    /// abajo.
    /// </summary>
    private void RecordShadowPass(nint command, double timeSeconds)
    {
        uint size = (uint)_graphics.ShadowMapSize;

        var viewport = new VulkanApi.VkViewport
        {
            x = 0,
            y = size,
            width = size,
            height = -(float)size,
            minDepth = 0,
            maxDepth = 1
        };
        VulkanApi.vkCmdSetViewport(command, 0, 1, ref viewport);

        var renderArea = new VulkanApi.VkRect2D
        {
            offset = new VulkanApi.VkOffset2D { x = 0, y = 0 },
            extent = new VulkanApi.VkExtent2D { width = size, height = size }
        };
        VulkanApi.vkCmdSetScissor(command, 0, 1, ref renderArea);

        // Las constantes de ESTA pasada: el bloque par de la ranura, con la matriz del sol.
        UpdateConstants(timeSeconds, shadowPass: true);

        // Un solo valor de limpieza: el render pass tiene un único adjunto, la profundidad.
        var clearValues = new[]
        {
            new VulkanApi.VkClearValue { depthStencil = new VulkanApi.VkClearDepthStencilValue { depth = 1f, stencil = 0 } }
        };

        unsafe
        {
            fixed (VulkanApi.VkClearValue* clears = clearValues)
            {
                var beginInfo = new VulkanApi.VkRenderPassBeginInfo
                {
                    sType = VulkanApi.VkStructureType.RenderPassBeginInfo,
                    renderPass = _shadowRenderPass,
                    framebuffer = _shadowFramebuffer,
                    renderArea = renderArea,
                    clearValueCount = 1,
                    pClearValues = (nint)clears
                };
                VulkanApi.vkCmdBeginRenderPass(command, ref beginInfo, VulkanApi.VkSubpassContents.Inline);
            }

            nint descriptorSet = _descriptorSets[_frameIndex * ConstantSlicesPerFrame];
            VulkanApi.vkCmdBindDescriptorSets(command, VulkanApi.VkPipelineBindPoint.Graphics, _pipelineLayout, 0, 1, (nint)(&descriptorSet), 0, 0);
            VulkanApi.vkCmdBindPipeline(command, VulkanApi.VkPipelineBindPoint.Graphics, _shadowPipeline);

            for (int mesh = 0; mesh < _vertexBuffers.Length; mesh++)
            {
                if (!_castsShadow[mesh]) continue;

                var buffers = new[] { _vertexBuffers[mesh], _instanceBuffers[mesh] };
                var offsets = new ulong[] { 0, 0 };
                fixed (nint* bufferPointer = buffers)
                fixed (ulong* offsetPointer = offsets)
                {
                    VulkanApi.vkCmdBindVertexBuffers(command, 0, 2, (nint)bufferPointer, (nint)offsetPointer);
                }

                VulkanApi.vkCmdDraw(command, (uint)_meshVertexCounts[mesh], (uint)_meshInstanceCounts[mesh], 0, 0);
            }
        }

        VulkanApi.vkCmdEndRenderPass(command);
    }

    /// <summary>
    /// Sube el bloque del frame. Con <paramref name="shadowPass"/> en true escribe el bloque PAR de la
    /// ranura (el de la pasada de sombras) y con la matriz del SOL como <c>ViewProjection</c>: es todo
    /// lo que hace falta para que el vertex shader de la geometría dibuje la misma escena desde la luz.
    /// </summary>
    private void UpdateConstants(double timeSeconds, bool shadowPass = false)
    {
        // El tiempo y la cámara salen de la ESCENA (la corrida es un viaje de N segundos), igual que
        // en los otros backends: las cuatro APIs suben exactamente el mismo bloque por frame.
        var (eye, target) = _scene.CameraAt(timeSeconds);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, (float)_width / Math.Max(1, _height), 0.5f, _scene.FarPlane);

        var overlayRect = Vector4.Zero;
        var overlaySize = Vector4.Zero;
        if (HasOverlay)
        {
            // Rectángulo en NDC (xy = esquina mínima, zw = máxima). En NDC la Y crece hacia ARRIBA,
            // así que la esquina mínima es la de ABAJO; el viewport de Vulkan ya está "dado vuelta"
            // (altura negativa) y por eso las cuentas son las mismas que en Direct3D.
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
            // z = alto del render target para las APIs que numeran las filas desde ABAJO. Vulkan
            // declara el origen ARRIBA en su SPIR-V (dxc emite OriginUpperLeft) y el viewport va
            // invertido, así que su fragmento ya viene como el shader lo espera: va en 0.
            TimeAndParams = new Vector4((float)timeSeconds, _scene.PointLights.Count, 0f, 0f),
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

        unsafe
        {
            int block = _frameIndex * ConstantSlicesPerFrame + (shadowPass ? 0 : 1);
            MemoryMarshal.Write(new Span<byte>((void*)_uniformMapped[block], ConstantBufferSize), in constants);
        }
    }

    private void RecreateSwapchain()
    {
        _swapchainDirty = false;
        VulkanApi.vkDeviceWaitIdle(_device);

        DestroySwapchainResources();
        CreateSwapchain(_width, _height);
        CreateDepthBuffer();
        CreateRenderPassAndFramebuffers();
        _frameIndex = 0;
        _lastImageIndex = 0;
    }

    private void DestroySwapchainResources()
    {
        foreach (var framebuffer in _framebuffers)
        {
            if (framebuffer != 0) VulkanApi.vkDestroyFramebuffer(_device, framebuffer, 0);
        }
        _framebuffers = Array.Empty<nint>();

        if (_renderPass != 0)
        {
            VulkanApi.vkDestroyRenderPass(_device, _renderPass, 0);
            _renderPass = 0;
        }
        foreach (var view in _swapchainViews)
        {
            if (view != 0) VulkanApi.vkDestroyImageView(_device, view, 0);
        }
        _swapchainViews = Array.Empty<nint>();

        foreach (var semaphore in _presentSemaphores)
        {
            if (semaphore != 0) VulkanApi.vkDestroySemaphore(_device, semaphore, 0);
        }
        _presentSemaphores = Array.Empty<nint>();

        if (_depthView != 0)
        {
            VulkanApi.vkDestroyImageView(_device, _depthView, 0);
            _depthView = 0;
        }
        if (_depthImage != 0)
        {
            VulkanApi.vkDestroyImage(_device, _depthImage, 0);
            _depthImage = 0;
        }
        if (_depthMemory != 0)
        {
            VulkanApi.vkFreeMemory(_device, _depthMemory, 0);
            _depthMemory = 0;
        }
        if (_swapchain != 0)
        {
            VulkanApi.vkDestroySwapchainKHR(_device, _swapchain, 0);
            _swapchain = 0;
        }
    }

    // =====================================================================
    // Utilidades
    // =====================================================================

    private static void Check(int result, string what)
    {
        if (result == VkResult.Success) return;
        throw new InvalidOperationException($"Vulkan no pudo {what} ({ResultName(result)}).");
    }

    private static string ResultName(int result) => result switch
    {
        VkResult.Success => "sin error",
        VkResult.NotReady => "el resultado todavía no está listo",
        VkResult.Timeout => "se agotó el tiempo de espera",
        VkResult.SuboptimalKhr => "el swapchain quedó desactualizado",
        VkResult.ErrorOutOfHostMemory => "la memoria del sistema se agotó",
        VkResult.ErrorOutOfDateKhr => "el swapchain quedó desactualizado",
        VkResult.ErrorDeviceLost => "se perdió el dispositivo gráfico",
        VkResult.ErrorSurfaceLostKhr => "se perdió la superficie de la ventana",
        _ => $"error 0x{result:X8}"
    };

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    /// <summary>
    /// vkFreeCommandBuffers se declara acá y no en <see cref="VulkanApi"/> porque solo lo usa el
    /// command buffer de una sola vez de las cargas iniciales.
    /// </summary>
    [DllImport(VulkanApi.Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern void vkFreeCommandBuffers(nint device, nint commandPool, uint commandBufferCount, [In] nint[]? commandBuffers);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Todo best-effort y en orden inverso al de creación: una corrida abortada puede dejar
        // recursos a medio crear y no queremos que Dispose tire otra excepción encima.
        try
        {
            if (_device != 0) VulkanApi.vkDeviceWaitIdle(_device);

            for (int i = 0; i < _uniformBuffers.Length; i++)
            {
                if (_uniformMapped.Length > i && _uniformMapped[i] != 0)
                {
                    try { VulkanApi.vkUnmapMemory(_device, _uniformMemory[i]); } catch { }
                }
                if (_uniformBuffers[i] != 0) { try { VulkanApi.vkDestroyBuffer(_device, _uniformBuffers[i], 0); } catch { } }
                if (_uniformMemory[i] != 0) { try { VulkanApi.vkFreeMemory(_device, _uniformMemory[i], 0); } catch { } }
            }

            if (_overlayBuffer != 0 && _overlayMapped != 0)
            {
                try { VulkanApi.vkUnmapMemory(_device, _overlayMemory); } catch { }
                _overlayMapped = 0;
            }
            if (_overlayBuffer != 0) { try { VulkanApi.vkDestroyBuffer(_device, _overlayBuffer, 0); } catch { } }
            if (_overlayMemory != 0) { try { VulkanApi.vkFreeMemory(_device, _overlayMemory, 0); } catch { } }

            if (_detailSampler != 0) { try { VulkanApi.vkDestroySampler(_device, _detailSampler, 0); } catch { } }
            if (_detailView != 0) { try { VulkanApi.vkDestroyImageView(_device, _detailView, 0); } catch { } }
            if (_detailImage != 0) { try { VulkanApi.vkDestroyImage(_device, _detailImage, 0); } catch { } }
            if (_detailMemory != 0) { try { VulkanApi.vkFreeMemory(_device, _detailMemory, 0); } catch { } }

            if (_descriptorPool != 0) { try { VulkanApi.vkDestroyDescriptorPool(_device, _descriptorPool, 0); } catch { } }
            if (_pipelineLayout != 0) { try { VulkanApi.vkDestroyPipelineLayout(_device, _pipelineLayout, 0); } catch { } }
            if (_descriptorSetLayout != 0) { try { VulkanApi.vkDestroyDescriptorSetLayout(_device, _descriptorSetLayout, 0); } catch { } }

            // Shadow map (ver README-SOMBRAS.md). El render pass y el framebuffer del mapa NO se
            // destruyen en DestroySwapchainResources: no dependen del tamaño de la ventana.
            if (_shadowPipeline != 0) { try { VulkanApi.vkDestroyPipeline(_device, _shadowPipeline, 0); } catch { } }
            if (_shadowFramebuffer != 0) { try { VulkanApi.vkDestroyFramebuffer(_device, _shadowFramebuffer, 0); } catch { } }
            if (_shadowRenderPass != 0) { try { VulkanApi.vkDestroyRenderPass(_device, _shadowRenderPass, 0); } catch { } }
            if (_shadowSampler != 0) { try { VulkanApi.vkDestroySampler(_device, _shadowSampler, 0); } catch { } }
            if (_shadowView != 0) { try { VulkanApi.vkDestroyImageView(_device, _shadowView, 0); } catch { } }
            if (_shadowImage != 0) { try { VulkanApi.vkDestroyImage(_device, _shadowImage, 0); } catch { } }
            if (_shadowMemory != 0) { try { VulkanApi.vkFreeMemory(_device, _shadowMemory, 0); } catch { } }

            if (_blendPipeline != 0) { try { VulkanApi.vkDestroyPipeline(_device, _blendPipeline, 0); } catch { } }
            if (_skyPipeline != 0) { try { VulkanApi.vkDestroyPipeline(_device, _skyPipeline, 0); } catch { } }
            if (_geometryPipeline != 0) { try { VulkanApi.vkDestroyPipeline(_device, _geometryPipeline, 0); } catch { } }
            if (_overlayPipeline != 0) { try { VulkanApi.vkDestroyPipeline(_device, _overlayPipeline, 0); } catch { } }

            for (int mesh = 0; mesh < _vertexBuffers.Length; mesh++)
            {
                if (_vertexBuffers[mesh] != 0) { try { VulkanApi.vkDestroyBuffer(_device, _vertexBuffers[mesh], 0); } catch { } }
                if (_vertexMemories[mesh] != 0) { try { VulkanApi.vkFreeMemory(_device, _vertexMemories[mesh], 0); } catch { } }
                if (_instanceBuffers[mesh] != 0) { try { VulkanApi.vkDestroyBuffer(_device, _instanceBuffers[mesh], 0); } catch { } }
                if (_instanceMemories[mesh] != 0) { try { VulkanApi.vkFreeMemory(_device, _instanceMemories[mesh], 0); } catch { } }
            }

            foreach (var pool in _queryPools)
            {
                if (pool != 0) { try { VulkanApi.vkDestroyQueryPool(_device, pool, 0); } catch { } }
            }
            foreach (var fence in _frameFences)
            {
                if (fence != 0) { try { VulkanApi.vkDestroyFence(_device, fence, 0); } catch { } }
            }
            foreach (var semaphore in _imageAvailable)
            {
                if (semaphore != 0) { try { VulkanApi.vkDestroySemaphore(_device, semaphore, 0); } catch { } }
            }

            DestroySwapchainResources();

            if (_commandPool != 0) { try { VulkanApi.vkDestroyCommandPool(_device, _commandPool, 0); } catch { } }
        }
        catch { }

        if (_ownsDevice && _device != 0)
        {
            try { VulkanApi.vkDestroyDevice(_device, 0); } catch { }
        }
        if (_surface != 0)
        {
            try { VulkanApi.vkDestroySurfaceKHR(_instance, _surface, 0); } catch { }
        }
        if (_instance != 0)
        {
            try { VulkanApi.vkDestroyInstance(_instance, 0); } catch { }
        }
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
        // ESTILO de la escena (ver SceneStyleConstants): el mismo bloque que declara el cbuffer
        // del HLSL, en el mismo orden y con todos los campos de 16 bytes.
        public SceneStyleConstants Style;
        // LUCES PUNTUALES de la escena (ver SceneLightConstants): mismo bloque que las otras APIs.
        public SceneLightConstants Lights;
        // ENTORNO IBL de la escena (ver SceneEnvironmentConstants): los armónicos esféricos del HDRI,
        // que reemplazan el ambiente inventado del estilo por la luz medida en el lugar.
        public SceneEnvironmentConstants Environment;
    }
}
