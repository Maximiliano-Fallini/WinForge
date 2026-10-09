using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// La copia DESENFOCADA de la imagen de fondo: la que usan los paneles como relleno cuando el
/// deslizador "Blur" está encendido (ver <see cref="PanelAppearance"/>).
///
/// POR QUÉ EXISTE (no es lo mismo que el acrílico de Windows): el <c>AcrylicBrush</c> de WinUI 3
/// desenfoca el contenido que tiene DETRÁS, pero Windows lo APAGA —y lo reemplaza por un color
/// plano— cuando el usuario desactiva los "Efectos de transparencia"
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency = 0</c>),
/// y también en equipos que no cumplen los requisitos o en ahorro de batería. Con el ajuste
/// apagado el deslizador no producía NINGÚN efecto visible. Acá el desenfoque lo hacemos
/// nosotros, así que no depende de nada del sistema: se decodifica la foto del fondo, se reduce y
/// se desenfoca (<see cref="PanelBlurAlgorithm"/>). El resultado es un <see cref="ImageSource"/>
/// chico que pinta la capa de <see cref="PanelBlurLayer"/>, recortada a los paneles.
///
/// LA FOTO SE DECODIFICA UNA VEZ, LA INTENSIDAD ES BARATA: los píxeles de la foto quedan guardados
/// (<see cref="_pixels"/>) tal como salieron del decodificador, SIN desenfocar. Cada valor del
/// deslizador vuelve a desenfocar ESE búfer —nunca la foto del disco— con el radio que le toca
/// (ver <see cref="PanelBlurAlgorithm.RadiusForPercent"/>), así que mover el deslizador cuesta un
/// par de milisegundos por paso y la intensidad es gradual. Antes la copia se calculaba con un
/// radio fijo: el porcentaje solo encendía o apagaba la capa.
///
/// ARRASTRAR NO ENCOLA: el trabajo de fondo es UNO, y mientras corre va mirando el último valor
/// pedido (<see cref="_wantedPercent"/>). Un arrastre de veinte ticks no calcula veinte copias: se
/// saltea los intermedios y publica el último (si no, la ventana iba a ir mostrando intensidades
/// viejas mientras el usuario ya movió el deslizador).
/// </summary>
internal static class PanelBlur
{
    private static readonly object Sync = new();

    private static int _generation;
    private static ImageSource? _source;

    /// <summary>
    /// La MISMA copia desenfocada, pero como surface de composición: es la que usa el vidrio declarativo
    /// (ver <see cref="PanelGlass"/>) para que el recorte de cada panel lo siga el compositor por
    /// expresión, sin trabajo managed por frame.
    /// </summary>
    private static ICompositionSurface? _surface;

    /// <summary>El PNG en memoria del que se carga la surface: tiene que sobrevivir hasta que la carga termine.</summary>
    private static InMemoryRandomAccessStream? _surfaceStream;

    /// <summary>
    /// Los píxeles de la foto decodificados (Bgra8), NÍTIDOS: son la base de cada intensidad. Cada
    /// desenfoque trabaja sobre una COPIA de este búfer (desenfocar el original acumularía el
    /// desenfoque de todos los pasos del deslizador).
    /// </summary>
    private static byte[]? _pixels;
    private static int _width;
    private static int _height;

    /// <summary>Porcentaje que está publicado en la copia vigente (el que se ve en la ventana).</summary>
    private static double _publishedPercent = -1;

    /// <summary>Porcentaje pedido (el último valor del deslizador): el trabajo de fondo converge a este.</summary>
    private static double _wantedPercent;

    /// <summary>Hay un desenfoque de fondo corriendo: los pedidos nuevos no arrancan otro.</summary>
    private static bool _busy;

    /// <summary>Ancho en píxeles de la copia (el parche la escala al tamaño de la ventana).</summary>
    internal static int PixelWidth => _width;

    /// <summary>Alto en píxeles de la copia (el parche la escala al tamaño de la ventana).</summary>
    internal static int PixelHeight => _height;

    /// <summary>La foto desenfocada; null si todavía no está lista o si no hay foto de fondo.</summary>
    internal static ImageSource? Source
    {
        get { lock (Sync) return _source; }
    }

    /// <summary>La misma copia como surface de composición (ver <see cref="PanelGlass"/>); null si todavía no está.</summary>
    internal static ICompositionSurface? Surface
    {
        get { lock (Sync) return _surface; }
    }

    internal static bool IsReady => Source != null;

    /// <summary>Cómo terminó un intento de publicar la copia (ver <see cref="PublishAsync"/>).</summary>
    private enum Publish
    {
        /// <summary>La copia quedó publicada: la ventana ya la está mostrando.</summary>
        Publicada,

        /// <summary>Cambió la foto o el tema mientras se desenfocaba: la copia quedó vieja.</summary>
        Vieja,

        /// <summary>No hay ventana (o su cola de UI no acepta trabajo): no hay dónde publicar.</summary>
        SinVentana
    }

    /// <summary>
    /// (Re)genera la copia desenfocada para el tema indicado. La llama <see cref="Wallpaper"/>
    /// cuando termina de derivar los colores de las superficies (o sea, cuando ya se sabe con qué
    /// color va teñida) y también cuando no hay foto, para descartar la anterior.
    /// </summary>
    internal static void Refresh(AppTheme theme)
    {
        try
        {
            // CON EL VIDRIO DE LA PLATAFORMA NO SE ARMA COPIA... salvo para el CHROME. El acrílico
            // in-app desenfoca la foto por panel desde el compositor (ver
            // PanelAppearance.PlatformGlassInCharge), así que el desenfoque de caja sobre la CPU —y la
            // foto desenfocada que había que tener en memoria y publicar en una superficie de
            // composición— eran trabajo por nada PARA EL RESTO DE LOS PANELES. El chrome, en cambio, sí
            // la usa: sus piezas van pegadas y el vidrio por elemento corta la foto en seco en cada
            // junta (ver PanelAppearance.ChromeSharedGlass), así que con foto y desenfoque pedido la
            // copia se arma igual. Sin foto o con el deslizador en 0 se descarta, como antes.
            if (PanelAppearance.PlatformGlassInCharge && !PanelAppearance.SharedGlassNeeded)
            {
                Interlocked.Increment(ref _generation);
                lock (Sync)
                {
                    _source = null;
                    _surface = null;
                }

                PanelAppearance.Diag("desenfoque: no se arma la copia desenfocada — el vidrio lo pinta el MOTOR (compositor o acrílico)");
                return;
            }

            int generation = Interlocked.Increment(ref _generation);

            var path = Wallpaper.ActiveImagePath(theme);
            if (path == null || !File.Exists(path))
            {
                lock (Sync)
                {
                    _source = null;
                    _surface = null;
                    _pixels = null;
                    _publishedPercent = -1;
                }
                PanelAppearance.Diag("desenfoque: sin foto de fondo — los paneles quedan con el color del tema");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // Decodificar es trabajo de fondo: no toca la UI. Es lo único caro del sistema, y
                    // pasa una sola vez por foto y por tema.
                    var decoded = await DecodeAsync(path);
                    if (generation != _generation) return;

                    lock (Sync)
                    {
                        _pixels = decoded.Data;
                        _width = decoded.Width;
                        _height = decoded.Height;
                        _publishedPercent = -1;
                    }

                    PanelAppearance.Diag(
                        $"desenfoque: foto lista en {decoded.Width}x{decoded.Height} (la intensidad la pone el deslizador)");

                    // La foto ya está: si el deslizador pedía una intensidad antes de que terminara
                    // la decodificación, el desenfoque sale ahora (SetStrength no tenía píxeles).
                    Request();
                }
                catch (Exception ex)
                {
                    PanelAppearance.Diag($"desenfoque: no se pudo preparar la copia ({ex.Message})");
                }
            });
        }
        catch (Exception ex)
        {
            PanelAppearance.Diag($"desenfoque: {ex.Message}");
        }
    }

    /// <summary>
    /// Pide una intensidad, en puntos del deslizador (0 = sin desenfoque). La llama
    /// <see cref="PanelAppearance.SetBlurPercent"/> en cada movimiento del deslizador: si ya hay un
    /// desenfoque de fondo corriendo, esto solo deja anotado el valor nuevo y el trabajo en curso
    /// lo toma al terminar (ver <see cref="BuildStrengthsAsync"/>).
    /// </summary>
    internal static void SetStrength(double percent)
    {
        if (!double.IsFinite(percent)) percent = 0.0;

        lock (Sync)
        {
            _wantedPercent = Math.Clamp(percent, 0.0, 100.0);
            // Sin foto decodificada todavía no hay nada que desenfocar: el pedido queda anotado y
            // Refresh lo toma cuando la foto esté.
            if (_pixels == null) return;
        }

        Request();
    }

    /// <summary>Descarta la copia vigente (cambio a un tema sin foto, o de fondo a degradado).</summary>
    internal static void Clear()
    {
        Interlocked.Increment(ref _generation);
        lock (Sync)
        {
            _source = null;
            _surface = null;
            _pixels = null;
            _publishedPercent = -1;
        }
    }

    /// <summary>Arranca el trabajo de fondo, si no hay uno corriendo.</summary>
    private static void Request()
    {
        lock (Sync)
        {
            if (_busy || _pixels == null) return;
            _busy = true;
        }

        _ = BuildStrengthsAsync();
    }

    /// <summary>Apaga el semáforo del trabajo de fondo.</summary>
    private static void Done()
    {
        lock (Sync) _busy = false;
    }

    /// <summary>
    /// El desenfoque de fondo: UNA tarea que va convergiendo al último porcentaje pedido. Cada
    /// vuelta lee los píxeles y el valor vigentes BAJO LOCK, desenfoca una copia y publica; si
    /// mientras tanto el usuario movió el deslizador, la vuelta siguiente lo alcanza. Se apaga sola
    /// cuando lo publicado coincide con lo pedido, así que un arrastre no deja trabajo acumulado.
    /// </summary>
    private static async Task BuildStrengthsAsync()
    {
        try
        {
            while (true)
            {
                double percent;
                byte[] pixels;
                int width, height, generation;

                lock (Sync)
                {
                    // Nada que hacer (o la foto se descartó): se apaga el semáforo y se sale. Va
                    // dentro del lock junto con el pedido de SetStrength para que no se pierda un
                    // valor pedido justo entre "no hay nada que hacer" y "me apago".
                    if (_pixels == null || Math.Abs(_wantedPercent - _publishedPercent) < 0.5)
                    {
                        _busy = false;
                        return;
                    }

                    percent = _wantedPercent;
                    pixels = _pixels;
                    width = _width;
                    height = _height;
                    generation = _generation;
                }

                // Sobre una copia: los píxeles decodificados tienen que quedar nítidos para que el
                // próximo paso del deslizador vuelva a empezar de cero y no acumule desenfoques.
                var data = (byte[])pixels.Clone();
                var stopwatch = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    PanelBlurAlgorithm.Apply(data, width, height, percent);
                    PanelBlurAlgorithm.Opaque(data);
                });
                stopwatch.Stop();

                switch (await PublishAsync(data, width, height, percent, generation))
                {
                    case Publish.Publicada:
                        PanelAppearance.Diag(
                            $"desenfoque: copia {percent:0} % lista (radio {PanelBlurAlgorithm.RadiusForPercent(percent)} px en {stopwatch.Elapsed.TotalMilliseconds:0.0} ms)");
                        break;

                    case Publish.Vieja:
                        // Cambió la foto o el tema mientras se desenfocaba. La foto nueva se está
                        // decodificando en otro lado (ver Refresh), así que se espera un poco en vez
                        // de rehacer el desenfoque en vano sobre los píxeles viejos.
                        await Task.Delay(50);
                        break;

                    case Publish.SinVentana:
                        // Sin ventana no hay capa que pintar (la app se está cerrando).
                        Done();
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            PanelAppearance.Diag($"desenfoque: {ex.Message}");
            Done();
        }
    }

    /// <summary>
    /// Publica la copia desenfocada: el <see cref="SoftwareBitmapSource"/> es un objeto de XAML, así
    /// que se arma y se carga en el hilo de UI, y recién cuando la carga termina sirve de
    /// <see cref="ImageSource"/>.
    /// </summary>
    private static async Task<Publish> PublishAsync(byte[] data, int width, int height, double percent, int generation)
    {
        if (generation != _generation) return Publish.Vieja;

        var window = App.MainWindowInstance;
        if (window == null) return Publish.SinVentana;

        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(data),
            BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);

        var done = new TaskCompletionSource<bool>();
        bool published = false;

        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (generation != _generation) return;

                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
                if (generation != _generation) return;

                // La MISMA copia, además, como surface de composición: es la que usa el vidrio declarativo
                // (ver PanelGlass). Se arma acá —en el hilo de UI, porque LoadedImageSurface es de XAML— y
                // solo cuando cambia la foto o el deslizador, nunca por frame.
                var surface = await SurfaceAsync(data, width, height);

                lock (Sync)
                {
                    _source = source;
                    _surface = surface;
                    _publishedPercent = percent;
                }
                published = true;

                // La capa de los paneles ya está encendida o no según el deslizador: acá solo se le
                // avisa que hay copia nueva.
                PanelBlurLayer.Invalidate();

                // Y con la copia publicada el chrome puede pasar al vidrio compartido (ver
                // ChromeSharedGlass): hasta acá sus piezas llevaban el relleno del motor, así que se
                // repintan ahora y no cuando el usuario toque un deslizador.
                PanelAppearance.OnSharedGlassReady();
            }
            catch (Exception ex)
            {
                PanelAppearance.Diag($"desenfoque: no se pudo cargar la copia ({ex.Message})");
            }
            finally
            {
                done.TrySetResult(true);
            }
        }))
        {
            return Publish.SinVentana;
        }

        await done.Task;
        return published ? Publish.Publicada : Publish.Vieja;
    }

    /// <summary>
    /// La copia desenfocada como SURFACE de composición (ver <see cref="PanelGlass"/>). Se codifica a PNG
    /// en memoria porque <c>LoadedImageSurface</c> decodifica una imagen —no acepta píxeles sueltos—, y así
    /// el vidrio declarativo no necesita ninguna dependencia nueva (Win2D) para pintar.
    /// </summary>
    private static async Task<ICompositionSurface?> SurfaceAsync(byte[] data, int width, int height)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, data);
            await encoder.FlushAsync();
            stream.Seek(0);

            // El stream tiene que seguir vivo hasta que la carga termine: la surface lo va leyendo.
            _surfaceStream = stream;
            return LoadedImageSurface.StartLoadFromStream(stream);
        }
        catch (Exception ex)
        {
            PanelAppearance.Diag($"desenfoque: no se pudo armar la surface del vidrio ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Decodifica la foto chica SIN desenfocar. Devuelve los píxeles Bgra8 y su tamaño: el
    /// desenfoque de cada intensidad se calcula después, sobre una copia de estos bytes.
    /// </summary>
    internal static async Task<(byte[] Data, int Width, int Height)> DecodeAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        // Se decodifica a un ancho fijo: el costo deja de depender del tamaño del archivo (las
        // fotos del tema son de 1920 px o más) y el desenfoque igual borra todo el detalle fino.
        int width = PanelBlurAlgorithm.WorkingWidth;
        int height = Math.Max(1, (int)Math.Round(decoder.PixelHeight * (width / (double)decoder.PixelWidth)));

        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)width,
            ScaledHeight = (uint)height,
            InterpolationMode = BitmapInterpolationMode.Linear
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return (pixels.DetachPixelData(), width, height);
    }
}
