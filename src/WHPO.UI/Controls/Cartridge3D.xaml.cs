using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using System.Threading;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WHPO_UI.Controls;

/// <summary>
/// Cartucho/memory card 3D REAL renderizado con Direct3D 11 dentro de un SwapChainPanel
/// de WinUI (no es una card plana que se inclina: es un cubo con volumen, costados y
/// dorso). Se puede ARRASTRAR con el mouse para girarlo y ver sus caras. La portada del
/// juego se texturiza en el frente y el dorso; los costados son plástico de color.
///
/// Se usa en dos lugares: como MODELO de la card en el modo 3D de la biblioteca (una
/// instancia por juego) y en el detalle de doble clic. Por eso lo que NO cambia entre
/// cartuchos (device, contexto, factory, geometría, shaders y estados) es COMPARTIDO y
/// se construye UNA sola vez, y el render es BAJO DEMANDA (no hay un bucle de 60 fps por
/// cartucho): solo se redibuja al girar, al asentarse, al cambiar la portada o el
/// tamaño. Compartir el contexto inmediato es seguro porque todo el render corre en el
/// hilo de UI.
/// </summary>
public sealed partial class Cartridge3D : UserControl
{
    /// <summary>
    /// Recursos idénticos para todos los cartuchos: device, contexto, factory y todo lo
    /// que depende solo de la geometría (buffers, shaders, estados). Sin esto, cada card
    /// compilaba los shaders HLSL por su cuenta y mostrar la grilla en 3D congelaba la UI.
    /// </summary>
    private sealed class SharedD3D
    {
        public readonly ID3D11Device Device;
        public readonly ID3D11DeviceContext Context;
        public readonly IDXGIFactory2 Factory;
        public readonly ID3D11Buffer VertexBuffer;
        public readonly ID3D11Buffer IndexBuffer;
        public readonly ID3D11VertexShader Vs;
        public readonly ID3D11PixelShader Ps;
        public readonly ID3D11InputLayout Layout;
        public readonly ID3D11SamplerState Sampler;
        public readonly ID3D11RasterizerState Raster;
        /// <summary>Cantidad de índices de la malla del cartucho.</summary>
        public readonly int IndexCount;
        /// <summary>
        /// Muestras de MSAA con las que se dibuja (4 si el dispositivo lo soporta). Sin
        /// antialiasing, el canto biselado y las muescas laterales salen escalonados: es
        /// lo que hace que la silueta se vea sucia.
        /// </summary>
        public readonly int MuestrasMsaa;

        public SharedD3D()
        {
            var flags = DeviceCreationFlags.BgraSupport;
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            var created = D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, flags, levels,
                out ID3D11Device device, out ID3D11DeviceContext context);
            // Sin GPU por hardware (equipos virtuales, escritorio remoto, driver caído)
            // el cartucho quedaría invisible sin avisar: antes de darse por vencido se
            // cae a WARP, el rasterizador por software de Windows.
            if (!created.Success)
                D3D11.D3D11CreateDevice(
                    null, DriverType.Warp, flags, levels,
                    out device, out context).CheckError();
            Device = device;
            Context = context;
            using (var dxgiDevice = Device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
                Factory = adapter.GetParent<IDXGIFactory2>();

            VertexBuffer = CreateGeometry(Device, out var indexBuffer, out int indexCount);
            IndexBuffer = indexBuffer;
            IndexCount = indexCount;
            CreateShaders(Device, out var vs, out var ps, out var layout);
            Vs = vs;
            Ps = ps;
            Layout = layout;
            // Anisotrópico + Clamp: interpola entre mips (la cara de 600 px se reduce ~3x
            // en la card y sin mips los trazos finos del texto hacen aliasing) y con
            // anisotropía la etiqueta NO se borronea cuando el cartucho está girado, que es
            // justo cuando el plano de la calcomanía queda muy escorzado y el filtrado
            // isotrópico elige un mip demasiado bajo. Clamp porque una UV exactamente en 1.0
            // mezclaría con el téxel del lado opuesto de la textura.
            Sampler = Device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.Anisotropic,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxAnisotropy = 8,
                MinLOD = 0f,
                MaxLOD = float.MaxValue
            });
            MuestrasMsaa = Device.CheckMultisampleQualityLevels(Format.B8G8R8A8_UNorm, 4) > 0 ? 4 : 1;
            // Sin culling: la malla es un sólido cerrado (los cantos biselados dejan
            // caras casi de perfil), así que descartar por orientación no ahorra nada y
            // sí puede comerse un canto por un sentido de vértices.
            Raster = Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        }

        private static ID3D11Buffer CreateGeometry(ID3D11Device dev, out ID3D11Buffer indexBuffer, out int indexCount)
        {
            // Geometría procedural del cartucho (esquinas redondeadas, muescas laterales
            // y canto biselado con normales suaves): la MISMA pieza que dibuja el
            // previsualizador offscreen, así que lo que se ve ahí es lo que sale acá.
            var (modelo, indices) = CartridgeModel.BuildMesh();
            var v = new Vertex[modelo.Length];
            for (int i = 0; i < modelo.Length; i++)
            {
                var m = modelo[i];
                v[i] = new Vertex(m.Position, m.Normal, m.Uv, m.Tint);
            }
            indexCount = indices.Length;
            indexBuffer = dev.CreateBuffer(indices, BindFlags.IndexBuffer);
            return dev.CreateBuffer(v, BindFlags.VertexBuffer);
        }

        private static void CreateShaders(ID3D11Device dev, out ID3D11VertexShader vs, out ID3D11PixelShader ps, out ID3D11InputLayout layout)
        {
            // El VS y el PS van en el MISMO texto HLSL y cada uno necesita su propia
            // entrada: con una sola función "main" la compilación del vertex shader
            // fallaba (X4502: invalid vs_5_0 output semantic 'SV_TARGET') y el error
            // quedaba invisible.
            var vsRes = Vortice.D3DCompiler.Compiler.Compile(CartridgeModel.Hlsl, "VSMain", "vs", "vs_5_0", out var vsBlob, out var vsErr);
            if (!vsRes.Success) throw new InvalidOperationException("shader de vértices: " + vsErr?.AsString());
            var psRes = Vortice.D3DCompiler.Compiler.Compile(CartridgeModel.Hlsl, "PSMain", "ps", "ps_5_0", out var psBlob, out var psErr);
            if (!psRes.Success) throw new InvalidOperationException("shader de píxeles: " + psErr?.AsString());
            vs = dev.CreateVertexShader(vsBlob);
            ps = dev.CreatePixelShader(psBlob);
            layout = dev.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    24, 0),
                new InputElementDescription("COLOR",    0, Format.R32G32B32A32_Float, 32, 0),
            }, vsBlob);
            vsBlob.Dispose();
            psBlob.Dispose();
        }
    }

    private static SharedD3D? _shared;
    private static SharedD3D Shared => _shared ??= new SharedD3D();

    // No hay swapchain: el render va a una textura con ALFA, se copia a una staging, se lee
    // en CPU y se vuelca en un único WriteableBitmap en el Image de XAML: así el cartucho
    // tiene transparencia real, sin caja y sin reasignar la fuente de imagen en cada cuadro.
    private ID3D11Texture2D? _rt;
    private WriteableBitmap? _wbmp;
    private byte[]? _pixeles;
    private ID3D11Texture2D[] _lectura = Array.Empty<ID3D11Texture2D>();
    private ID3D11Query[] _finLectura = Array.Empty<ID3D11Query>();
    private bool[] _slotOcupado = Array.Empty<bool>();
    private int _slot;
    private double _msLectura, _peorLectura;
    private int _cuadrosPublicados, _cuadrosSalteados;
    /// <summary>Hay un cambio de orientación o un cuadro en vuelo que debe dibujarse/leerse en el próximo cuadro de composición.</summary>
    private bool _dirty;
    private bool _renderingHooked;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11DepthStencilView? _dsv;
    private ID3D11Texture2D? _depthTexture;
    // El buffer de constantes (matrices) es POR INSTANCIA: cada cartucho lo escribe con
    // su propia orientación antes de dibujar, así dos cartuchos no se pisan la matriz.
    private ID3D11Buffer? _constants;
    // Dos texturas por cartucho: la cara pintada (calcomanía) y el dorso (contactos).
    private ID3D11Texture2D? _faceTexture;
    private ID3D11ShaderResourceView? _faceSrv;
    private ID3D11Texture2D? _backTexture;
    private ID3D11ShaderResourceView? _backSrv;

    /// <summary>Inclinación de reposo (y a la que vuelve al soltar el arrastre).</summary>
    private const float RestPitch = -2.5f;

    private float _rasterScale = 1f;
    private float _yaw;
    private float _pitch = RestPitch;
    /// <summary>
    /// Zoom de la rueda: qué FRACCIÓN del panel ocupa el cartucho (0.85 = 85% con aire
    /// alrededor, 0.985 = casi tocando los bordes). La distancia de cámara se deriva de
    /// este valor con el auto-encuadre, así el cartucho NUNCA se corta: ni al girarlo ni
    /// al hacer zoom. Un offset de distancia (como estaba antes) permitía meter la
    /// cámara dentro del encuadre y el zoom adentro volvía a recortar al rotar.
    /// </summary>
    private float _zoom = 0.85f;

    // Datos con los que se PINTAN las caras del cartucho (los pone la página con SetCard).
    private string _title = "Juego";
    private string _launcher = "";
    private string _code = "";
    private string? _artPath;
    private GameHeader _header = GameLook.DefaultHeader;
    private Color _plastic = Color.FromArgb(48, 48, 54);
    private bool _pendingFaces;   // se pidió pintar antes de que D3D esté listo
    /// <summary>
    /// Pintado diferido para la grilla: la card guarda los datos pero NO pinta hasta
    /// que el host llama <see cref="EnsurePainted"/>. En la vista de detalle
    /// (<c>deferred = false</c>) se pinta inmediatamente al cargar D3D.
    /// </summary>
    private bool _deferredPaint;
    /// <summary>
    /// El primer render se salta hasta que haya caras pintadas: sin esto, cada card
    /// hace al abrir un render + readback GPU→CPU con el plástico gris, y con N
    /// juegos son N readbacks sincrónicos seguidos. El render real lo dispara
    /// ApplyFaces al llegar el pintado.
    /// </summary>
    private bool _awaitingFaces;
    private int _paintGeneration; // descarta pintados viejos si después llega arte nuevo

    /// <summary>Log opcional (lo inyecta la página para depurar en runtime). Null = sin log.</summary>
    public Action<string>? Logger { get; set; }
    private void Log(string msg) { try { Logger?.Invoke(msg); } catch { } }

    /// <summary>
    /// Fallo del cartucho: queda en el log de la app (antes iba solo a la salida de
    /// depuración, así que un cartucho que no dibujaba no dejaba NINGUNA pista). El
    /// mensaje raíz importa: los fallos de D3D/HLSL llegan envueltos en excepciones
    /// genéricas de inicialización de tipo.
    /// </summary>
    private void Fail(string etapa, Exception ex)
    {
        var raiz = ex.GetBaseException();
        Log($"FALLO {etapa}: {ex.GetType().Name}: {ex.Message}" +
            (raiz.Message != ex.Message ? $" | causa: {raiz.Message}" : ""));
        Debug.WriteLine($"Cartridge3D {etapa} falló: {ex}");
    }

    /// <summary>
    /// Si es true, el arrastre respeta el scroll de la página: el primer movimiento
    /// decide el gesto (horizontal = girar el cartucho; vertical = dejar scrollear) y al
    /// soltar el cartucho vuelve al frente con un suavizado. Es lo que usa la biblioteca.
    /// En false (detalle) el giro es libre e inmediato, como siempre.
    /// </summary>
    public bool ScrollFriendly { get; set; }

    /// <summary>Si es false, la rueda NO hace zoom y se deja pasar al scroll de la página.</summary>
    public bool ZoomOnWheel { get; set; } = true;

    private bool _dragging;
    private bool _armed;
    private bool _ready;
    private Windows.Foundation.Point _start;
    private Windows.Foundation.Point _last;

    // Asentado (vuelta al frente) tras soltar, solo en modo ScrollFriendly.
    private const double SettleSeconds = 0.45;
    private const double DragStartThreshold = 7; // px antes de decidir giro vs scroll
    private bool _settling;
    private DateTime _settleStart;
    private double _settleFromY, _settleFromX, _settleToY, _settleToX;

    // Animación de ENTRADA ("el cartucho se pone en su lugar haciendo un giro"): se
    // arranca mostrando el dorso y se gira 180° hasta aterrizar en el frente. Si las
    // caras todavía se están pintando, el giro espera al primer cuadro con arte para
    // no girar un cartucho de plástico gris.
    private const double IntroSeconds = 0.7;
    private bool _intro;
    private bool _introPending;
    private DateTime _introStart;
    private double _introFromY, _introFromX;

    private int _lastW = -1, _lastH = -1;
    // Tamaño del swapchain en PÍXELES físicos (DIP x escala de rasterizado) y color del
    // fondo del panel. Ver las notas de CardBackground y de CreateSwapChain.
    private int _pxW = -1, _pxH = -1;
    // Render con MSAA: se dibuja en esta textura con muestras y se resuelve al back
    // buffer del swapchain antes de presentar.
    private ID3D11Texture2D? _msaaTexture;
    private ID3D11RenderTargetView? _msaaRtv;




    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 Uv;
        /// <summary>a = cara: 1 calcomanía (frente), 2 dorso, 0 plástico del canto.</summary>
        public Vector4 Tint;
        public Vertex(Vector3 p, Vector3 n, Vector2 uv, Vector4 t)
        { Position = p; Normal = n; Uv = uv; Tint = t; }
        public const int Size = CartridgeModel.VertexSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 World;
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        // Color del plástico de ESTE cartucho: la malla es compartida entre todas las
        // cards, así que cada una manda el suyo por acá.
        public Vector4 Plastic;
    }

    public Cartridge3D()
    {
        InitializeComponent();
        InputRoot.PointerPressed += OnPointerPressed;
        InputRoot.PointerMoved += OnPointerMoved;
        InputRoot.PointerReleased += OnPointerReleased;
        InputRoot.PointerCaptureLost += OnPointerCaptureLost;
        InputRoot.PointerExited += OnPointerExited;
        InputRoot.PointerWheelChanged += OnPointerWheel;
        InputRoot.SizeChanged += OnSurfaceSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void StartRenderLoop()
    {
        if (_renderingHooked) return;
        _renderingHooked = true;
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void StopRenderLoop()
    {
        if (!_renderingHooked) return;
        _renderingHooked = false;
        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void RequestRender()
    {
        _dirty = true;
        StartRenderLoop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        try
        {
            int w = Math.Max(1, (int)InputRoot.ActualWidth);
            int h = Math.Max(1, (int)InputRoot.ActualHeight);
            // Buffer de constantes por instancia, uso Default: UpdateSubresource sobre un
            // recurso Default es seguro aunque haya dibujos pendientes en la cola de la GPU.
            _constants = Shared.Device.CreateBuffer(
                (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<Constants>(),
                BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
            EnsureFallbackTextures();
            PrepareTarget(w, h);
            _lastW = w;
            _lastH = h;
            _ready = true;
            if (_pendingFaces && !_deferredPaint)
            {
                _pendingFaces = false;
                StartPaint();
            }
            // Sin caras todavía: no se renderiza el plástico gris. El primer render
            // lo dispara ApplyFaces al llegar el pintado. Sin esto, cada card hace un
            // render + readback inútil al abrir.
            if (!_awaitingFaces)
                Render(forzar: true);
            if (_introPending && !_awaitingFaces && !_dragging) { _introPending = false; StartIntro(); }
            // La escala se loguea a propósito: si el monitor está al 150% y acá dice 1, el
            // swapchain se creó en DIP y la composición lo estira (todo se ve borroso).
            Log($"init OK surface={w}x{h} px={_pxW}x{_pxH} escala={_rasterScale:0.##} msaa={Shared.MuestrasMsaa}x");
            if (XamlRoot != null) XamlRoot.Changed += OnXamlRootChanged;
        }
        catch (Exception ex) { Fail("init", ex); }
    }

    /// <summary>
    /// Al cambiar la escala del monitor (mover la ventana a otra pantalla) el swapchain
    /// queda en la escala vieja y la imagen se estira: se rearma con la escala nueva.
    /// </summary>
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!_ready) return;
        try
        {
            float escala = (float)(XamlRoot?.RasterizationScale ?? 1.0);
            if (Math.Abs(escala - _rasterScale) < 0.001f) return;
            int w = Math.Max(1, (int)InputRoot.ActualWidth);
            int h = Math.Max(1, (int)InputRoot.ActualHeight);
            ReleaseTargets();
            PrepareTarget(w, h);
            Render(forzar: true);
            Log($"escala {escala:0.##}: destino en px={_pxW}x{_pxH}");
        }
        catch (Exception ex) { Fail("escala", ex); }
    }

    /// <summary>Libera los recursos que dependen del tamaño del cartucho.</summary>
    private void ReleaseTargets()
    {
        _rtv?.Dispose(); _dsv?.Dispose(); _depthTexture?.Dispose();
        _msaaRtv?.Dispose(); _msaaTexture?.Dispose();
        _rt?.Dispose();
        foreach (var t in _lectura) t?.Dispose();
        foreach (var q in _finLectura) q?.Dispose();
        _rtv = null; _dsv = null; _depthTexture = null;
        _msaaRtv = null; _msaaTexture = null; _rt = null;
        _lectura = Array.Empty<ID3D11Texture2D>();
        _finLectura = Array.Empty<ID3D11Query>();
        _slotOcupado = Array.Empty<bool>();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot != null) XamlRoot.Changed -= OnXamlRootChanged;
        StopRenderLoop();
        _settling = false;
        _dirty = false;
        DisposeGpu();
        _ready = false;
        _lastW = _lastH = -1;
    }

    /// <summary>
    /// Prepara el destino del render en píxeles físicos (DIP x escala del monitor) y
    /// asigna un único WriteableBitmap a Surface.Source.
    /// </summary>
    private void PrepareTarget(int w, int h)
    {
        float monitorScale = (float)(XamlRoot?.RasterizationScale ?? 1.0);
        if (monitorScale <= 0.01f || monitorScale > 8f) monitorScale = 1f;
        // Supersampling moderado (1.35x mínimo) + MSAA 4x: mantiene nitidez en textos y
        // bordes sin agrandar la textura de readback GPU→CPU durante el giro.
        _rasterScale = Math.Max(1.35f, monitorScale);
        _pxW = Math.Max(1, (int)Math.Round(w * _rasterScale));
        _pxH = Math.Max(1, (int)Math.Round(h * _rasterScale));
        CreateViews(_pxW, _pxH);
        _wbmp = new WriteableBitmap(_pxW, _pxH);
        Surface.Source = _wbmp;
    }

    private void CreateViews(int w, int h)
    {
        ReleaseTargets();
        int muestras = Shared.MuestrasMsaa;
        if (muestras > 1)
        {
            var sd = new SampleDescription((uint)muestras, 0);
            _msaaTexture = Shared.Device.CreateTexture2D(new Texture2DDescription
            {
                Format = Format.B8G8R8A8_UNorm, Width = (uint)w, Height = (uint)h,
                MipLevels = 1, ArraySize = 1, BindFlags = BindFlags.RenderTarget,
                SampleDescription = sd
            });
            _msaaRtv = Shared.Device.CreateRenderTargetView(_msaaTexture);
            _depthTexture = Shared.Device.CreateTexture2D(new Texture2DDescription
            {
                Format = Format.D24_UNorm_S8_UInt, Width = (uint)w, Height = (uint)h,
                MipLevels = 1, ArraySize = 1, BindFlags = BindFlags.DepthStencil,
                SampleDescription = sd
            });
        }
        else
        {
            _depthTexture = Shared.Device.CreateTexture2D(
                Format.D24_UNorm_S8_UInt, (uint)w, (uint)h, 1, 1, null, BindFlags.DepthStencil);
        }
        _dsv = Shared.Device.CreateDepthStencilView(_depthTexture);
        // Destino 1x donde se resuelve el MSAA y copia para poder leerlo en CPU.
        _rt = Shared.Device.CreateTexture2D(new Texture2DDescription
        {
            Format = Format.B8G8R8A8_UNorm, Width = (uint)w, Height = (uint)h,
            MipLevels = 1, ArraySize = 1, BindFlags = BindFlags.RenderTarget,
            SampleDescription = new SampleDescription(1, 0)
        });
        _rtv = Shared.Device.CreateRenderTargetView(_rt);
        const int Slots = 3;
        _lectura = new ID3D11Texture2D[Slots];
        _finLectura = new ID3D11Query[Slots];
        _slotOcupado = new bool[Slots];
        for (int i = 0; i < Slots; i++)
        {
            _lectura[i] = Shared.Device.CreateTexture2D(
                Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, null,
                BindFlags.None, ResourceOptionFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
            _finLectura[i] = Shared.Device.CreateQuery(new QueryDescription(QueryType.Event));
        }
        _slot = 0;
        _pixeles = new byte[w * h * 4];
    }

    private void LogLectura()
    {
        if (_cuadrosPublicados == 0 && _cuadrosSalteados == 0) return;
        double promedio = _cuadrosPublicados == 0 ? 0 : _msLectura / _cuadrosPublicados;
        Log($"lectura: {_cuadrosPublicados} cuadros, promedio {promedio:F1} ms, peor {_peorLectura:F1} ms, salteados {_cuadrosSalteados}");
        _cuadrosPublicados = 0; _cuadrosSalteados = 0; _msLectura = 0; _peorLectura = 0;
    }

    /// <summary>
    /// Distancia mínima de la cámara para que el cartucho ENTRE completo en el viewport
    /// con la orientación dada (world = RotX(pitch) * RotY(yaw)). Se calcula sobre las
    /// 8 esquinas del bounding box de la malla: la proyección de cualquier punto
    /// interior es combinación convexa de la de las esquinas, así que si las 8 entran
    /// con margen, entra toda la silueta (bisel y muescas incluidos). La cámara está en
    /// (0, 0, d) mirando al origen, y cada esquina impone d ≥ z + |x|/(m·tanH) y
    /// d ≥ z + |y|/(m·tanV): la esquina más cercana (z máxima) es la que más agranda la
    /// perspectiva, y por eso el encuadre en reposo no alcanzaba al inclinar el cartucho.
    /// </summary>
    private static float FitDistance(in Matrix4x4 world, float aspect, float margen)
    {
        // margen = fracción del panel que ocupa el cartucho. El tope del zoom (0.985)
        // deja siempre aire para el bisel y el antialias: jamás se llega a 1.0.
        float tanV = MathF.Tan(22.5f * MathF.PI / 180f); // mitad del FOV vertical (45°)
        float tanH = tanV * aspect;
        float hx = CartridgeModel.Width * 0.5f;
        float hy = CartridgeModel.Height * 0.5f;
        float hz = CartridgeModel.Thickness * 0.5f;
        float d = 0f;
        for (int i = 0; i < 8; i++)
        {
            var p = new Vector3(
                (i & 1) == 0 ? -hx : hx,
                (i & 2) == 0 ? -hy : hy,
                (i & 4) == 0 ? -hz : hz);
            var q = Vector3.Transform(p, world);
            d = MathF.Max(d, q.Z + MathF.Abs(q.X) / (margen * tanH));
            d = MathF.Max(d, q.Z + MathF.Abs(q.Y) / (margen * tanV));
        }
        return d;
    }

    /// <summary>
    /// Dibuja un cuadro y actualiza el WriteableBitmap en sitio (sin SetBitmapAsync ni
    /// allocaciones por cuadro). Con `forzar = true` hace Map directo del slot recién
    /// dibujado para que el primer cuadro (apertura / cambio de caras / resize) sea
    /// visible de inmediato; durante el arrastre/giro usa el anillo de 3 slots sin
    /// bloquear el hilo de UI.
    /// </summary>
    private void Render(bool forzar = false)
    {
        if (!_ready || _rt == null || _wbmp == null || _pixeles == null) return;
        try
        {
            var ctx = Shared.Context;
            int w = _pxW > 0 ? _pxW : Math.Max(1, (int)InputRoot.ActualWidth);
            int h = _pxH > 0 ? _pxH : Math.Max(1, (int)InputRoot.ActualHeight);

            if (forzar || _dirty || _settling)
            {
                var destino = _msaaRtv ?? _rtv;
                if (destino == null || _dsv == null || _faceSrv == null || _backSrv == null) return;
                ctx.OMSetRenderTargets(destino, _dsv);
                ctx.ClearRenderTargetView(destino, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
                ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1f, 0);
                ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0f, 0f, w, h, 0f, 1f));
                ctx.RSSetState(Shared.Raster);
                float aspect = w / (float)h;
                float radPitch = _pitch * MathF.PI / 180f;
                float radYaw = _yaw * MathF.PI / 180f;
                var world = Matrix4x4.CreateRotationX(radPitch) * Matrix4x4.CreateRotationY(radYaw);
                // Auto-encuadre: la cámara se aparta lo que haga falta para que el
                // cartucho ENTRE completo en la orientación actual ocupando _zoom del
                // panel. El zoom de la rueda cambia esa fracción: agranda hasta casi
                // llenar el panel, pero jamás recorta — tampoco al girar.
                float dist = FitDistance(world, aspect, _zoom);
                var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, dist), Vector3.Zero, Vector3.UnitY);
                var proj = Matrix4x4.CreatePerspectiveFieldOfView(45f * MathF.PI / 180f, aspect, 0.1f, 100f);
                var c = new Constants
                {
                    World = world,
                    View = view,
                    Projection = proj,
                    Plastic = new Vector4(_plastic.R / 255f, _plastic.G / 255f, _plastic.B / 255f, 1f)
                };
                ctx.UpdateSubresource(c, _constants!);
                ctx.IASetInputLayout(Shared.Layout);
                ctx.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                ctx.IASetVertexBuffer(0, Shared.VertexBuffer, Vertex.Size);
                ctx.IASetIndexBuffer(Shared.IndexBuffer, Format.R16_UInt, 0);
                ctx.VSSetShader(Shared.Vs);
                ctx.VSSetConstantBuffer(0, _constants);
                ctx.PSSetShader(Shared.Ps);
                ctx.PSSetConstantBuffer(0, _constants);
                ctx.PSSetShaderResource(0, _faceSrv);
                ctx.PSSetShaderResource(1, _backSrv);
                ctx.PSSetSampler(0, Shared.Sampler);
                ctx.DrawIndexed((uint)Shared.IndexCount, 0, 0);
                if (_msaaRtv != null && _msaaTexture != null)
                {
                    ctx.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), null);
                    ctx.ResolveSubresource(_rt, 0, _msaaTexture, 0, Format.B8G8R8A8_UNorm);
                }
                if (_lectura.Length > 0)
                {
                    int escribir = _slot;
                    ctx.CopyResource(_lectura[escribir], _rt);
                    ctx.End(_finLectura[escribir]);
                    // Flush explícito: sin esto, con DoNotFlush en GetData, D3D11 no envía
                    // el comando al driver y los 3 slots se sobreescriben en bucle dando
                    // "salteados 300+" (1 FPS).
                    ctx.Flush();
                    _slotOcupado[escribir] = true;
                    _slot = (escribir + 1) % _lectura.Length;
                }
                _dirty = false;
            }

            if (_lectura.Length > 0)
            {
                int ultimoEscrito = (_slot - 1 + _lectura.Length) % _lectura.Length;
                int elegir = -1;
                if (forzar && _slotOcupado[ultimoEscrito])
                {
                    elegir = ultimoEscrito;
                }
                else
                {
                    // Buscar el slot ocupado más reciente cuya copia GPU→Staging ya terminó
                    for (int k = 0; k < _lectura.Length; k++)
                    {
                        int idx = (ultimoEscrito - k + _lectura.Length) % _lectura.Length;
                        if (!_slotOcupado[idx]) continue;
                        if (_finLectura[idx] != null &&
                            ctx.GetData(_finLectura[idx], AsyncGetDataFlags.None, out int _))
                        {
                            elegir = idx;
                            break;
                        }
                    }
                    // Si los 3 slots siguen en vuelo, leer el más antiguo (2 cuadros atrás, ya listo)
                    // para nunca quedarnos sin publicar y que jamás se pisen todos los slots.
                    if (elegir < 0 && _slotOcupado[_slot])
                    {
                        elegir = _slot;
                    }
                }

                if (elegir >= 0)
                {
                    var reloj = Stopwatch.StartNew();
                    var map = ctx.Map(_lectura[elegir], 0, MapMode.Read);
                    try
                    {
                        var pixeles = _pixeles;
                        int rowBytes = w * 4;
                        int pitch = (int)map.RowPitch;
                        if (pitch == rowBytes)
                        {
                            Marshal.Copy(map.DataPointer, pixeles, 0, rowBytes * h);
                        }
                        else
                        {
                            for (int y = 0; y < h; y++)
                            {
                                Marshal.Copy(IntPtr.Add(map.DataPointer, y * pitch), pixeles, y * rowBytes, rowBytes);
                            }
                        }
                    }
                    finally { ctx.Unmap(_lectura[elegir], 0); }
                    using (var stream = _wbmp.PixelBuffer.AsStream())
                    {
                        stream.Seek(0, System.IO.SeekOrigin.Begin);
                        stream.Write(_pixeles, 0, w * h * 4);
                    }
                    // Si leímos un slot, descartar ese y los más viejos para no republicar un cuadro antiguo
                    int idxLimpiar = elegir;
                    for (int k = 0; k < _lectura.Length; k++)
                    {
                        _slotOcupado[idxLimpiar] = false;
                        if (idxLimpiar == _slot) break;
                        idxLimpiar = (idxLimpiar - 1 + _lectura.Length) % _lectura.Length;
                    }
                    _wbmp.Invalidate();
                    double ms = reloj.Elapsed.TotalMilliseconds;
                    _msLectura += ms; _peorLectura = Math.Max(_peorLectura, ms); _cuadrosPublicados++;
                }
                else
                {
                    _cuadrosSalteados++;
                }
            }
        }
        catch (Exception ex) { Fail("render", ex); }
    }

    private bool HasPendingReadback()
    {
        for (int i = 0; i < _slotOcupado.Length; i++)
            if (_slotOcupado[i]) return true;
        return false;
    }

    /// <summary>
    /// Texturas de reemplazo (plástico liso): dejan los dos slots siempre válidos
    /// mientras las caras pintadas se arman en segundo plano.
    /// </summary>
    private void EnsureFallbackTextures()
    {
        if (_faceSrv == null)
            (_faceTexture, _faceSrv) = CreatePlainTexture();
        if (_backSrv == null)
            (_backTexture, _backSrv) = CreatePlainTexture();
    }

    private (ID3D11Texture2D, ID3D11ShaderResourceView) CreatePlainTexture()
    {
        var tex = Shared.Device.CreateTexture2D(
            Format.B8G8R8A8_UNorm, 1, 1, 1, 1, null, BindFlags.ShaderResource);
        // B8G8R8A8: los bytes van B, G, R, A.
        var px = new byte[]
        {
            (byte)(_plastic.B * 0.86), (byte)(_plastic.G * 0.86), (byte)(_plastic.R * 0.86), 0xFF
        };
        Shared.Context.UpdateSubresource(px, tex, 0, 4, 4);
        return (tex, Shared.Device.CreateShaderResourceView(tex));
    }

    /// <summary>
    /// Datos del cartucho: con esto se PINTAN las dos caras (carcasa, calcomanía con el
    /// arte, franjas y el dorso con los contactos) y se suben a la GPU. Se puede llamar
    /// antes de que el control esté cargado: en ese caso queda pendiente y se pinta al
    /// terminar la inicialización de D3D.
    /// </summary>
    public void SetCard(string title, string launcher, string code, string? artPath, bool deferred = false, GameHeader? header = null)
    {
        _title = string.IsNullOrWhiteSpace(title) ? "Juego" : title;
        _launcher = launcher ?? "";
        _code = code ?? "";
        _header = header ?? GameLook.DefaultHeader;
        _artPath = !string.IsNullOrEmpty(artPath) && System.IO.File.Exists(artPath) ? artPath : null;
        _plastic = CartridgeModel.PlasticColor(_title, _artPath);
        _deferredPaint = deferred;
        _awaitingFaces = _faceSrv == null || deferred;
        if (!_ready) { _pendingFaces = true; return; }
        if (!deferred || InputRoot.ActualWidth > 0)
        {
            _deferredPaint = false;
            StartPaint();
        }
    }

    /// <summary>¿Sigue pendiente el pintado diferido de esta card?</summary>
    public bool IsPaintPending => _deferredPaint;

    /// <summary>
    /// Pinta la card si su pintado estaba diferido. La llama el host cuando la card
    /// ya es visible en el scroll. Sin efecto si ya se pintó o si D3D no está listo.
    /// </summary>
    public void EnsurePainted()
    {
        if (!_deferredPaint) return;
        if (!_ready) { _pendingFaces = true; return; }
        _deferredPaint = false;
        StartPaint();
    }

    /// <summary>
    /// Pinta (o recupera de la caché en disco) las dos caras y las sube. El pintado es
    /// GDI+ puro y no toca D3D, así que corre fuera del hilo de UI: con una cara de
    /// 600x878 por juego, pintarlas en el hilo de UI congelaba la grilla.
    ///
    /// Además va ESCALONADO: como mucho 3 pintados a la vez en toda la grilla. Sin
    /// esto, abrir la biblioteca dispara N pintados + N subidas a GPU a la vez.
    /// </summary>
    public static int MaxConcurrentPaints = 3;
    private static readonly SemaphoreSlim PaintGate = new(MaxConcurrentPaints, MaxConcurrentPaints);

    private void StartPaint()
    {
        int gen = ++_paintGeneration;
        string title = _title, launcher = _launcher, code = _code;
        string? arte = _artPath;
        GameHeader header = _header;
        Color plastic0 = _plastic;
        _ = Task.Run(() =>
        {
            Color plastic = plastic0;
            Bitmap? cara = null, dorso = null;
            bool gateTaken = false;
            try
            {
                // Escalonado: si la grilla quiere pintar 40 caras a la vez, acá se
                // hace la fila de a 3. El LoadCached (disco) también pasa por la
                // puerta para no saturar el disco con N lecturas paralelas.
                // El plástico real se calcula acá (fondo), NO en SetCard: ese método
                // corre en la UI y SampleAverage abre y decodifica el banner.
                PaintGate.Wait();
                gateTaken = true;
                plastic = CartridgeModel.PlasticColor(title, arte);
                string clave = CacheKey(title, code, arte, header);
                cara = LoadCached(CaraCache, clave) ?? PaintAndCache(title, launcher, code, arte, plastic, false, header);
                dorso = LoadCached(DorsoCache, clave) ?? PaintAndCache(title, launcher, code, arte, plastic, true, header);
            }
            catch (Exception ex) { Fail("pintar", ex); }
            finally { if (gateTaken) { try { PaintGate.Release(); } catch { } } }

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // Si ya llegó arte nuevo, este pintado quedó viejo: se descarta.
                    if (gen == _paintGeneration && cara != null && dorso != null)
                        ApplyFaces(cara, dorso);
                }
                catch (Exception ex) { Fail("aplicar caras", ex); }
                finally { cara?.Dispose(); dorso?.Dispose(); }
            });
        });
    }

    /// <summary>Carpeta de caras pintadas (se cachean por juego para no repintarlas).</summary>
    private static string FaceCacheDir => System.IO.Path.Combine(WHPO.Core.AppPaths.RootDir, "cartfaces");
    private static string CaraCache => System.IO.Path.Combine(FaceCacheDir, "cara");
    private static string DorsoCache => System.IO.Path.Combine(FaceCacheDir, "dorso");

    /// <summary>Versión del pintado de caras: al subirla, las caras cacheadas se repintan.</summary>
    private const int FaceCacheVersion = 17;

    /// <summary>Clave de caché: versión + título + código + arte (nombre, tamaño y fecha) + paleta
    /// + encabezado. La paleta y el encabezado forman parte de la clave para que cambiar de
    /// carcasa o editar la etiqueta no reutilice caras viejas.</summary>
    private static string CacheKey(string title, string code, string? artPath, GameHeader header)
    {
        string arte = "";
        try
        {
            if (!string.IsNullOrEmpty(artPath) && System.IO.File.Exists(artPath))
            {
                var fi = new System.IO.FileInfo(artPath);
                arte = $"{System.IO.Path.GetFileName(artPath)}-{fi.Length}-{fi.LastWriteTimeUtc.Ticks}";
            }
        }
        catch { }
        return Math.Abs($"v{FaceCacheVersion}|{title}|{code}|{arte}|{CardSkin.Current.Key}|{header.Signature}".GetHashCode(StringComparison.Ordinal)).ToString("x8");
    }

    private static Bitmap? LoadCached(string baseRuta, string clave)
    {
        try
        {
            string ruta = $"{baseRuta}-{clave}.png";
            return System.IO.File.Exists(ruta) ? new Bitmap(ruta) : null;
        }
        catch { return null; }
    }

    private static Bitmap? PaintAndCache(
        string title, string launcher, string code, string? arte, Color plastic, bool dorso, GameHeader header)
    {
        var bmp = dorso
            ? CartridgeModel.PaintBack(launcher, code, plastic, flat: true)
            : CartridgeModel.PaintFace(title, launcher, code, arte, plastic, flat: true, header: header);
        try
        {
            System.IO.Directory.CreateDirectory(FaceCacheDir);
            bmp.Save($"{(dorso ? DorsoCache : CaraCache)}-{CacheKey(title, code, arte, header)}.png",
                System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { } // sin caché se repinta la próxima vez: no es fatal
        return bmp;
    }

    /// <summary>Sube las dos caras pintadas a la GPU (una textura cada una).</summary>
    private void ApplyFaces(Bitmap cara, Bitmap dorso)
    {
        var (t1, s1) = UploadBitmap(cara);
        var (t2, s2) = UploadBitmap(dorso);
        _faceSrv?.Dispose(); _faceTexture?.Dispose();
        _backSrv?.Dispose(); _backTexture?.Dispose();
        _faceTexture = t1; _faceSrv = s1;
        _backTexture = t2; _backSrv = s2;
        // Llegaron las caras: se acabó la espera y se fuerza el render del cuadro nuevo
        // aunque TocaPublicar esté dentro del intervalo del cuadro anterior.
        _awaitingFaces = false;
        Render(forzar: true);
        // Llegó el arte: recién ahora arranca el giro de entrada si estaba esperando.
        if (_introPending && !_dragging) { _introPending = false; StartIntro(); }
    }

    private (ID3D11Texture2D, ID3D11ShaderResourceView) UploadBitmap(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] bytes = new byte[w * h * 4];
        try
        {
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * w * 4, w * 4);
        }
        finally { bmp.UnlockBits(data); }

        // Con cadena de mips completa: la cara mide 600 px de ancho y en la card se ve a
        // ~200, así que sin mips el texto y el arte hacen aliasing (se ven sucios).
        var tex = Shared.Device.CreateTexture2D(new Texture2DDescription
        {
            Format = Format.B8G8R8A8_UNorm, Width = (uint)w, Height = (uint)h,
            MipLevels = 0, ArraySize = 1,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.GenerateMips,
            // El SampleDescription va EXPLÍCITO: con el inicializador de objeto queda en
            // 0 muestras y CreateTexture2D falla con E_INVALIDARG.
            SampleDescription = new SampleDescription(1, 0)
        });
        // El row pitch va EXPLÍCITO: con el UpdateSubresource de dos argumentos (pitch 0)
        // el driver sube la imagen con las filas pisadas y la cara llega corrompida
        // (medido: 61,5% de los píxeles mal en un banner real).
        Shared.Context.UpdateSubresource(bytes, tex, 0, (uint)(w * 4), (uint)(w * 4 * h));
        var srv = Shared.Device.CreateShaderResourceView(tex);
        Shared.Context.GenerateMips(srv);
        return (tex, srv);
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready) return;
        int w = Math.Max(1, (int)InputRoot.ActualWidth);
        int h = Math.Max(1, (int)InputRoot.ActualHeight);
        if (w == _lastW && h == _lastH) return;
        try
        {
            _lastW = w;
            _lastH = h;
            ReleaseTargets();
            PrepareTarget(w, h);
            Render(forzar: true);
        }
        catch (Exception ex) { Fail("resize", ex); }
    }

    private void OnCompositionRendering(object? sender, object e)
    {
        if (_intro)
        {
            double t = (DateTime.UtcNow - _introStart).TotalSeconds / IntroSeconds;
            if (t >= 1.0)
            {
                _yaw = 0f;
                _pitch = RestPitch;
                _intro = false;
                _dirty = true;
            }
            else
            {
                // Mismo OutBack que el asentado: el giro "aterriza" elástico en el frente
                const double s = 0.7;
                double u = t - 1.0;
                double k = 1.0 + (s + 1.0) * u * u * u + s * u * u;
                _yaw = (float)(_introFromY * (1.0 - k));
                _pitch = (float)(_introFromX + (RestPitch - _introFromX) * (1.0 - Math.Pow(1.0 - t, 3)));
                _dirty = true;
            }
        }

        if (_settling)
        {
            double t = (DateTime.UtcNow - _settleStart).TotalSeconds / SettleSeconds;
            if (t >= 1.0)
            {
                _yaw = (float)(((_settleToY % 360.0) + 360.0) % 360.0);
                _pitch = (float)_settleToX;
                _settling = false;
                _dirty = true;
                LogLectura();
            }
            else
            {
                // OutBack con overshoot suave (0.7) como en HeroCard.qml de Carthage
                const double s = 0.7;
                double u = t - 1.0;
                double k = 1.0 + (s + 1.0) * u * u * u + s * u * u;
                _yaw = (float)(_settleFromY + (_settleToY - _settleFromY) * k);
                _pitch = (float)(_settleFromX + (_settleToX - _settleFromX) * (1.0 - Math.Pow(1.0 - t, 3)));
                _dirty = true;
            }
        }

        if (_dirty || _settling || _intro || HasPendingReadback())
        {
            Render(forzar: false);
        }

        if (!_dirty && !_settling && !_intro && !_dragging && !HasPendingReadback())
        {
            StopRenderLoop();
        }
    }

    private double _dragVelX;
    private long _lastMoveTicks;

    /// <summary>
    /// Animación de entrada para la vista de inspección: el cartucho "se pone en su
    /// lugar" girando 180° (arranca en el dorso y aterriza en el frente) con el mismo
    /// easing OutBack del asentado. Si el pintado de caras todavía no terminó, el giro
    /// queda pendiente y arranca con el primer cuadro con arte.
    /// </summary>
    public void PlayIntro()
    {
        if (!_ready || _awaitingFaces) { _introPending = true; return; }
        StartIntro();
    }

    private void StartIntro()
    {
        _settling = false;
        _introFromY = _yaw + 180.0;
        _introFromX = Math.Clamp(_pitch + 14.0, -38.0, 38.0);
        _introStart = DateTime.UtcNow;
        _intro = true;
        _yaw = (float)_introFromY;
        _pitch = (float)_introFromX;
        RequestRender();
    }

    /// <summary>Voltea el cartucho 180° (frente ↔ dorso) con animación elástica OutBack.</summary>
    public void Flip()
    {
        _settleFromY = _yaw;
        _settleFromX = _pitch;
        _settleToY = Math.Round(_yaw / 180.0) * 180.0 + 180.0;
        _settleToX = RestPitch;
        _settleStart = DateTime.UtcNow;
        _settling = true;
        RequestRender();
    }

    private void StartSettle(double velocityX = 0)
    {
        // Aterriza en la cara más cercana (múltiplo de 180°: frente o dorso) sumando
        // la inercia del arrastre, como HeroCard.settle(velocityX) en Carthage.
        double coast = _yaw + Math.Clamp(velocityX * 0.22, -180.0, 180.0);
        _settleFromY = _yaw;
        _settleFromX = _pitch;
        _settleToY = Math.Round(coast / 180.0) * 180.0;
        _settleToX = RestPitch;
        _settleStart = DateTime.UtcNow;
        _settling = true;
        RequestRender();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(InputRoot);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _settling = false;
        _intro = false;
        _introPending = false;
        _dragVelX = 0;
        _lastMoveTicks = Stopwatch.GetTimestamp();
        _start = _last = pt.Position;
        if (ScrollFriendly)
        {
            // Sin capturar todavía: el primer movimiento decide si es giro o scroll.
            _armed = true;
            _dragging = false;
        }
        else
        {
            _armed = true;
            _dragging = false;
            try { InputRoot.CapturePointer(e.Pointer); } catch { }
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(InputRoot).Position;
        if (!_armed && !_dragging)
        {
            // Inclinación sutil hacia el puntero cuando está en reposo (HeroCard tilt ±6°)
            if (!ScrollFriendly && !_settling && !_intro && InputRoot.ActualWidth > 0 && InputRoot.ActualHeight > 0)
            {
                double nx = (p.X / InputRoot.ActualWidth) - 0.5;
                double ny = (p.Y / InputRoot.ActualHeight) - 0.5;
                float baseYaw = (float)(Math.Round(_yaw / 180.0) * 180.0);
                _yaw = baseYaw + (float)(nx * 12.0);
                _pitch = RestPitch + (float)(ny * 10.0);
                RequestRender();
            }
            return;
        }
        if (!_dragging)
        {
            double adx = Math.Abs(p.X - _start.X);
            double ady = Math.Abs(p.Y - _start.Y);
            if (adx < DragStartThreshold && ady < DragStartThreshold) { _last = p; return; }
            if (ScrollFriendly && ady >= adx) { _armed = false; return; }
            _dragging = true;
            _last = p;
            _lastMoveTicks = Stopwatch.GetTimestamp();
            try { InputRoot.CapturePointer(e.Pointer); } catch { }
            StartRenderLoop();
            return;
        }
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Max(0.001, (now - _lastMoveTicks) / (double)Stopwatch.Frequency);
        _lastMoveTicks = now;

        float dx = (float)(p.X - _last.X);
        float dy = (float)(p.Y - _last.Y);
        _last = p;
        _dragVelX = _dragVelX * 0.6 + 0.4 * (dx * 0.55 / dt);
        _yaw += dx * 0.58f;
        _pitch = Math.Clamp(_pitch + dy * 0.35f, -38f, 38f);
        RequestRender();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        try { InputRoot.ReleasePointerCapture(e.Pointer); } catch { }
        if (!_dragging)
        {
            // Si fue un clic limpio sin arrastrar en la vista de detalle, voltea 180° (frente ↔ dorso)
            if (_armed && !ScrollFriendly)
            {
                _armed = false;
                Flip();
                return;
            }
            _armed = false;
            return;
        }
        _dragging = false;
        _armed = false;
        StartSettle(_dragVelX);
        LogLectura();
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) { _armed = false; return; }
        _dragging = false;
        _armed = false;
        StartSettle(_dragVelX);
        LogLectura();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging || _settling || _intro || ScrollFriendly) return;
        float targetY = (float)(Math.Round(_yaw / 180.0) * 180.0);
        if (MathF.Abs(_yaw - targetY) > 0.2f || MathF.Abs(_pitch - RestPitch) > 0.2f)
            StartSettle(0);
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        // En modo card la rueda NO hace zoom: se deja pasar para que scrollee la página.
        if (!ZoomOnWheel) return;
        int delta = e.GetCurrentPoint(InputRoot).Properties.MouseWheelDelta;
        // Rueda arriba = acercar = el cartucho ocupa más panel (hasta 98,5%: sin corte).
        _zoom = Math.Clamp(_zoom + delta * 0.0006f, 0.35f, 0.985f);
        RequestRender();
    }

    private void DisposeGpu()
    {
        _faceSrv?.Dispose(); _faceTexture?.Dispose();
        _backSrv?.Dispose(); _backTexture?.Dispose();
        _constants?.Dispose();
        ReleaseTargets();
        _wbmp = null;
        _pixeles = null;
        // Device, contexto, factory, geometría y shaders son COMPARTIDOS: no se disponen acá.
        _faceSrv = null; _faceTexture = null; _backSrv = null; _backTexture = null; _constants = null;
        _rtv = null; _dsv = null; _depthTexture = null;
        _msaaRtv = null; _msaaTexture = null; _rt = null; _rtv = null; _dsv = null;
        _lectura = Array.Empty<ID3D11Texture2D>();
        _finLectura = Array.Empty<ID3D11Query>();
        _slotOcupado = Array.Empty<bool>();
    }
}
