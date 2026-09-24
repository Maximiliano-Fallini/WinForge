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

    /// <summary>Bloque de constantes por frame: matriz de 64 bytes + 3 vectores de 16.</summary>
    private const int ConstantBufferSize = 64 + 16 * 3;

    private const int VertexStride = 24;     // Vector3 posición + Vector3 normal
    private const int InstanceStride = 32;   // 2 × Vector4

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
    private ID3D11RasterizerState _rasterizerState = null!;

    private ID3D11VertexShader _geometryVertexShader = null!;
    private ID3D11PixelShader _geometryPixelShader = null!;
    private ID3D11VertexShader _skyVertexShader = null!;
    private ID3D11PixelShader _skyPixelShader = null!;
    private ID3D11InputLayout _inputLayout = null!;

    private ID3D11Buffer _vertexBuffer = null!;
    private ID3D11Buffer _instanceBuffer = null!;
    private ID3D11Buffer _constants = null!;

    private ID3D11Query _disjointTimer = null!;
    private ID3D11Query _timestampStart = null!;
    private ID3D11Query _timestampEnd = null!;
    private bool _timingPending;
    private bool _disposed;

    private SceneDefinition _scene = null!;
    private double _sceneTime;
    private int _vertexCount;
    private int _instanceCount;
    private int _width;
    private int _height;
    private bool _vsync;
    private FeatureLevel _featureLevel;

    public GraphicsApi Api => GraphicsApi.D3D11;
    public string ApiName => "Direct3D 11";
    public string AdapterName { get; private set; } = "";
    public string AdapterDetail { get; private set; } = "";
    public bool SupportsExclusiveFullscreen => true;
    public bool HasGpuTiming => true;
    public double LastGpuFrameMs { get; private set; }

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
        CreateTimingQueries();
        ApplyViewport();
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
        using var geometryVs = Compile(common + SceneShaders.GeometryVertexShader, "VSMain", SceneShaders.VertexProfile);
        using var geometryPs = Compile(common + SceneShaders.GeometryVertexShader + SceneShaders.GeometryPixelShader, "PSMain", SceneShaders.PixelProfile);
        using var skyVs = Compile(common + SceneShaders.SkyVertexShader, "VSSky", SceneShaders.VertexProfile);
        using var skyPs = Compile(common + SceneShaders.SkyVertexShader + SceneShaders.SkyPixelShader, "PSSky", SceneShaders.PixelProfile);

        _geometryVertexShader = _device.CreateVertexShader(geometryVs);
        _geometryPixelShader = _device.CreatePixelShader(geometryPs);
        _skyVertexShader = _device.CreateVertexShader(skyVs);
        _skyPixelShader = _device.CreatePixelShader(skyPs);

        var elements = new[]
        {
            new InputElementDescription("POSITION", 0, DxgiFormat.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, DxgiFormat.R32G32B32_Float, 12, 0),
            new InputElementDescription("INSTANCE", 0, DxgiFormat.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("INSTANCE", 1, DxgiFormat.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1)
        };
        _inputLayout = _device.CreateInputLayout(elements, geometryVs);

        // Fondo: sin prueba de profundidad y sin escritura. Se dibuja primero y la geometría
        // siempre queda encima (el estado por defecto de D3D11 ya prueba y escribe profundidad).
        _skyDepthState = _device.CreateDepthStencilState(DepthStencilDescription.None);

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
    }

    private void CreateGeometryBuffers()
    {
        _vertexBuffer = _device.CreateBuffer(_scene.Vertices, BindFlags.VertexBuffer);
        _instanceBuffer = _device.CreateBuffer(_scene.Instances, BindFlags.VertexBuffer);
    }

    private void CreateTimingQueries()
    {
        _disjointTimer = _device.CreateQuery(QueryType.TimestampDisjoint);
        _timestampStart = _device.CreateQuery(QueryType.Timestamp);
        _timestampEnd = _device.CreateQuery(QueryType.Timestamp);
    }

    /// <summary>
    /// Compila un shader con el <c>d3dcompiler_47</c> del sistema. Si falla, el error viaja
    /// ENTERO con el texto del compilador: un shader que no compila sin su línea no se arregla.
    /// </summary>
    private static Blob Compile(string source, string entryPoint, string profile)
    {
        var result = Compiler.Compile(source, entryPoint, "escena", profile, out var blob, out var errors);
        if (result.Failure || blob == null)
        {
            string message = errors != null ? errors.AsString() : result.Description;
            SafeDispose(errors);
            SafeDispose(blob);
            throw new InvalidOperationException($"No se pudo compilar el shader '{entryPoint}': {message}");
        }
        SafeDispose(errors);
        return blob;
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
        }
        catch
        {
            // Si el monitor no lo acepta, la corrida sigue en ventana: el informe lo aclara.
            ExclusiveFullscreenAccepted = false;
        }
    }

    public void RenderFrame(double timeSeconds)
    {
        _sceneTime = timeSeconds;
        CollectGpuTiming();

        _context.Begin(_disjointTimer);
        _context.End(_timestampStart);

        _context.ClearRenderTargetView(_renderTarget, new Color4(0.02f, 0.02f, 0.03f, 1f));
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1f, 0);
        BindRenderTarget();

        _context.RSSetState(_rasterizerState);

        // ---- Fondo (sin profundidad) ----
        _context.OMSetDepthStencilState(_skyDepthState, 0);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_skyVertexShader);
        _context.PSSetShader(_skyPixelShader);
        _context.Draw(3, 0);

        // ---- Geometría instanciada ----
        _context.OMSetDepthStencilState(null, 0);   // estado por defecto: profundidad activa
        UpdateConstants(timeSeconds);
        _context.VSSetConstantBuffer(0, _constants);
        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffers(
            0,
            new[] { _vertexBuffer, _instanceBuffer },
            new[] { (uint)VertexStride, (uint)InstanceStride },
            new[] { 0u, 0u });
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_geometryVertexShader);
        _context.PSSetShader(_geometryPixelShader);
        _context.DrawInstanced((uint)_vertexCount, (uint)_instanceCount, 0, 0);

        _context.End(_timestampEnd);
        _context.End(_disjointTimer);
        _timingPending = true;
    }

    /// <summary>
    /// Presenta y devuelve los ms que tardó. Con la cola de la GPU llena, Present espera al
    /// monitor: ese tiempo es de la ENTREGA, no del CPU, y por eso se mide aparte.
    /// </summary>
    public double Present()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // El flag de tearing solo vale con syncInterval 0: con vsync no se permite romper el cuadro.
        // (Es la misma regla que en el backend de Direct3D 12.)
        var present = _swapChain.Present(_vsync ? 1u : 0u, _vsync ? PresentFlags.None : PresentFlags.AllowTearing);
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

    private void UpdateConstants(double timeSeconds)
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
            LightDirection = new Vector4(Vector3.Normalize(new Vector3(-0.45f, -0.8f, 0.4f)), 0f),
            TimeAndParams = new Vector4((float)_sceneTime, 0f, 0f, 0f)
        };
        _context.UpdateSubresource(ref constants, _constants);
    }

    /// <summary>
    /// Lee el tiempo de GPU del frame ANTERIOR. Se lee un frame más tarde a propósito: el dato
    /// del frame en curso todavía no está listo y esperarlo frenaría justamente lo que se quiere
    /// medir. Si aún no está, ese frame queda sin muestra de GPU (el informe dice cuántas hubo).
    /// </summary>
    private void CollectGpuTiming()
    {
        if (!_timingPending) return;
        _timingPending = false;

        // DoNotFlush: si el dato todavía no está listo se SALTA la muestra. Leerlo con el flag
        // por defecto obliga a la placa a terminar el frame en curso — es decir, el instrumento
        // frena lo que está midiendo, y así aparecían frames de más de un segundo.
        if (!_context.GetData(_disjointTimer, AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint disjoint)) return;
        if (disjoint.Disjoint) return;
        if (!_context.GetData(_timestampStart, AsyncGetDataFlags.DoNotFlush, out ulong start)) return;
        if (!_context.GetData(_timestampEnd, AsyncGetDataFlags.DoNotFlush, out ulong end)) return;
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Liberar en orden inverso al de creación. Todo best-effort: una corrida abortada puede
        // dejar recursos a medio crear y no queremos que el Dispose tire otra excepción encima.
        SafeDispose(_skyVertexShader); SafeDispose(_skyPixelShader);
        SafeDispose(_geometryVertexShader); SafeDispose(_geometryPixelShader);
        SafeDispose(_inputLayout);
        SafeDispose(_vertexBuffer); SafeDispose(_instanceBuffer); SafeDispose(_constants);
        SafeDispose(_skyDepthState); SafeDispose(_rasterizerState);
        SafeDispose(_disjointTimer); SafeDispose(_timestampStart); SafeDispose(_timestampEnd);
        SafeDispose(_depthView); SafeDispose(_depthTexture); SafeDispose(_renderTarget); SafeDispose(BackBufferForDiagnostics);
        SafeDispose(_swapChain); SafeDispose(_context); SafeDispose(_device); SafeDispose(_factory);
    }

    private static void SafeDispose(IDisposable? resource)
    {
        try { resource?.Dispose(); } catch { }
    }
}
