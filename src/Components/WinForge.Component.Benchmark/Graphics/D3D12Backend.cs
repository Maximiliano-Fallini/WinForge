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
    /// Tamaño del bloque de constantes por frame. D3D12 exige que la vista de constantes esté
    /// alineada a 256 bytes, así que cada ranura ocupa 256 aunque los datos usen 112.
    /// </summary>
    private const int ConstantBufferSlice = 256;

    /// <summary>Matriz de 64 bytes + 3 vectores de 16 (mismo layout que el cbuffer del HLSL).</summary>
    private const int ConstantBufferSize = 64 + 16 * 3;

    private const int VertexStride = 24;     // Vector3 posición + Vector3 normal
    private const int InstanceStride = 32;   // 2 × Vector4

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

    private ID3D12Resource _vertexBuffer = null!;
    private ID3D12Resource _instanceBuffer = null!;
    private ID3D12Resource _constantBuffer = null!;
    private ulong _vertexBufferSize;
    private ulong _instanceBufferSize;

    private ID3D12QueryHeap _queryHeap = null!;
    private ID3D12Resource _timestampReadback = null!;
    private ulong _timestampFrequency;
    private readonly bool[] _timingPending = new bool[FramesInFlight];

    private SceneDefinition _scene = null!;
    private double _sceneTime;
    private int _vertexCount;
    private int _instanceCount;
    private int _width;
    private int _height;
    private bool _vsync;
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
        _width = Math.Max(64, options.Width);
        _height = Math.Max(64, options.Height);
        _vsync = options.VSync;
        _vertexCount = _scene.Vertices.Length;
        _instanceCount = _scene.Instances.Length;

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
        // Root signature: un único CBV en b0 para las constantes del frame. La geometría por
        // instancia va por el input layout, igual que en Direct3D 11, así que no hace falta
        // descriptor heap ni tabla de descriptores.
        var parameters = new[]
        {
            new RootParameter(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All)
        };
        var rootSignatureDescription = new RootSignatureDescription(
            RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, null);
        // Versión 1.0 y no 1.1: la serialización la hace D3D12SerializeRootSignature, que solo
        // acepta 1.0 (con 1.1 tira "unsupported root signature version"). El root signature de
        // esta escena —un CBV de root— no necesita nada de 1.1.
        _rootSignature = _device.CreateRootSignature(rootSignatureDescription, RootSignatureVersion.Version10);

        string common = SceneShaders.Common;
        using var geometryVs = Compile(common + SceneShaders.GeometryVertexShader, "VSMain", SceneShaders.VertexProfile);
        using var geometryPs = Compile(common + SceneShaders.GeometryVertexShader + SceneShaders.GeometryPixelShader, "PSMain", SceneShaders.PixelProfile);
        using var skyVs = Compile(common + SceneShaders.SkyVertexShader, "VSSky", SceneShaders.VertexProfile);
        using var skyPs = Compile(common + SceneShaders.SkyVertexShader + SceneShaders.SkyPixelShader, "PSSky", SceneShaders.PixelProfile);

        var elements = new[]
        {
            new InputElementDescription("POSITION", 0, DxgiFormat.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, DxgiFormat.R32G32B32_Float, 12, 0),
            new InputElementDescription("INSTANCE", 0, DxgiFormat.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("INSTANCE", 1, DxgiFormat.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1)
        };

        _geometryPipeline = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = geometryVs.AsBytes(),
            PixelShader = geometryPs.AsBytes(),
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

        // Fondo: sin prueba de profundidad ni escritura y sin culling (es un triángulo de pantalla
        // completa generado por SV_VertexID, sin input layout).
        _skyPipeline = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = skyVs.AsBytes(),
            PixelShader = skyPs.AsBytes(),
            BlendState = BlendDescription.Opaque,
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.None,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { DxgiFormat.B8G8R8A8_UNorm },
            DepthStencilFormat = DxgiFormat.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0)
        });
    }

    private static Blob Compile(string source, string entryPoint, string profile)
    {
        var result = Compiler.Compile(source, entryPoint, "escena", profile, out var blob, out var errors);
        if (result.Failure || blob == null)
        {
            string message = errors != null ? errors.AsString() : result.Description;
            DxgiShared.SafeDispose(errors);
            DxgiShared.SafeDispose(blob);
            throw new InvalidOperationException($"No se pudo compilar el shader '{entryPoint}': {message}");
        }
        DxgiShared.SafeDispose(errors);
        return blob;
    }

    private void CreateGeometryBuffers()
    {
        _vertexBufferSize = (ulong)_scene.Vertices.Length * VertexStride;
        _instanceBufferSize = (ulong)_scene.Instances.Length * InstanceStride;
        _vertexBuffer = CreateUploadBuffer(_vertexBufferSize);
        _instanceBuffer = CreateUploadBuffer(_instanceBufferSize);
        Write(_vertexBuffer, MemoryMarshal.AsBytes(_scene.Vertices.AsSpan()));
        Write(_instanceBuffer, MemoryMarshal.AsBytes(_scene.Instances.AsSpan()));

        // Constantes: una ranura por frame en vuelo, alineadas a 256 bytes.
        _constantBuffer = CreateUploadBuffer((ulong)(ConstantBufferSlice * FramesInFlight));
    }

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
        }
        catch
        {
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

        _commandList.ClearRenderTargetView(rtvHandle, new Color4(0.02f, 0.02f, 0.03f, 1f));
        _commandList.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1f, 0);
        _commandList.RSSetViewport(new Viewport(0f, 0f, _width, _height, 0f, 1f));
        // El scissor NO tiene estado heredado entre frames: después de un Reset de la lista el
        // rect por defecto es VACÍO y la GPU recorta TODOS los píxeles. Sin esta línea la escena
        // se ve negra con la placa trabajando al 99 % (fue el bug original del backend).
        _commandList.RSSetScissorRect(new RectI(0, 0, _width, _height));
        _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // ---- Fondo ----
        _commandList.SetPipelineState(_skyPipeline);
        _commandList.DrawInstanced(3, 1, 0, 0);

        // ---- Geometría instanciada ----
        UpdateConstants(slot, timeSeconds);
        _commandList.SetGraphicsRootConstantBufferView(0, _constantBuffer.GPUVirtualAddress + (ulong)(slot * ConstantBufferSlice));
        _commandList.SetPipelineState(_geometryPipeline);
        _commandList.IASetVertexBuffers(0, new[]
        {
            new VertexBufferView(_vertexBuffer.GPUVirtualAddress, (uint)_vertexBufferSize, VertexStride),
            new VertexBufferView(_instanceBuffer.GPUVirtualAddress, (uint)_instanceBufferSize, InstanceStride)
        });
        _commandList.DrawInstanced((uint)_vertexCount, (uint)_instanceCount, 0, 0);

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
        // El flag de tearing solo vale con syncInterval 0: con vsync no se permite romper el cuadro.
        var flags = _vsync ? PresentFlags.None : PresentFlags.AllowTearing;
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

    private void UpdateConstants(int slot, double timeSeconds)
    {
        // El tiempo de la escena se pide a la ESCENA, no al host: la corrida es un viaje de N
        // segundos y el mundo completo es función de ese reloj.
        var (eye, target) = _scene.CameraAt(_sceneTime);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, (float)_width / Math.Max(1, _height), 0.5f, 400f);

        var constants = new FrameConstants
        {
            ViewProjection = Matrix4x4.Multiply(view, projection),
            CameraPosition = new Vector4(eye, 0f),
            LightDirection = new Vector4(Vector3.Normalize(new Vector3(-0.45f, -0.8f, 0.45f)), 0f),
            TimeAndParams = new Vector4((float)_sceneTime, 0f, 0f, 0f)
        };
        _constantBuffer.SetData(constants, slot * ConstantBufferSlice);
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
        DxgiShared.SafeDispose(_rootSignature);
        DxgiShared.SafeDispose(_queryHeap);
        DxgiShared.SafeDispose(_timestampReadback);
        DxgiShared.SafeDispose(_vertexBuffer);
        DxgiShared.SafeDispose(_instanceBuffer);
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
