using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;

namespace WHPO_UI;

/// <summary>
/// Splash de arranque: una tarjeta cuadrada sin bordes (estilo Discord) con un
/// velocímetro cuya aguja sigue el porcentaje de carga y, debajo, UNA sola línea
/// con el paso que se está ejecutando.
///
/// Se muestra apenas se abre la app (lo antes posible dentro de OnLaunched: después
/// del mutex de instancia única y del chequeo de administrador, que son instantáneos,
/// y nunca en un relanzamiento de tema ni en un arranque minimizado a la bandeja) y
/// se cierra cuando la ventana principal —o el asistente de primera configuración—
/// ya está en pantalla: el splash tapa el primer frame de esa ventana en vez de
/// dejar un hueco gris.
///
/// Arriba a la derecha tiene su único control: la X manda el arranque a la bandeja
/// (avisa a App, que termina de cargar sin mostrar la ventana principal). La tarjeta
/// se arrastra desde cualquier punto, como una barra de título.
///
/// Por qué el % no es "el trabajo real": el arranque son pasos de distinta duración
/// y algunos ni se pueden medir (registro, WMI, disco). El splash recibe un
/// porcentaje por paso y la aguja lo persigue con una animación propia, así el
/// velocímetro se mueve parejo aunque un paso tarde 5 ms y el siguiente 800 ms.
/// La cuenta del dial vive en <see cref="SplashGauge"/>.
/// </summary>
public sealed partial class SplashWindow : Window
{
    // Tamaño en DIPs: se convierte a píxeles físicos con el DPI del monitor (una
    // ventana sin bordes con tamaño fijo en píxeles quedaría recortada al 150 %).
    //
    // El alto es MENOR que el ancho: la tarjeta no necesita ser cuadrada. El dial mide
    // 300 de alto y va en un lienzo de 346 (los 46 de margen compensan que el dial está
    // abierto abajo, ver GaugeCanvas), así que dentro de los 368 DIP de contenido quedan
    // 11 DIP de aire arriba y abajo: la marca y la línea de estado siguen entrando y la
    // tarjeta se lee más baja, sin el aire de sobra que tenía con 448.
    private const double WindowWidthDip = 448;
    private const double WindowHeightDip = 408;

    /// <summary>Radio de esquina de la tarjeta como fracción del ancho: 14 DIP sobre
    /// 448 DIP de ancho (el mismo estilo Discord a cualquier DPI).</summary>
    private const double CardCornerFraction = 14.0 / WindowWidthDip;

    /// <summary>Recorte de la región: entra 6px desde cada borde de la ventana. El
    /// reborde que dibuja DWM vive en los píxeles más externos (verificado con captura
    /// del usuario) y con este recorte queda fuera del área visible; los 6px dan margen
    /// para el brillo de las esquinas, que asomaba como dos puntos claros.</summary>
    private const int RegionInsetPixels = 6;

    // Dial de 300x300: centro (150,150), arco a radio 118 y aguja que no llega a
    // las marcas internas (96..105).
    private const double DialSize = 300;
    private const double DialCenter = DialSize / 2;
    private const double TrackRadius = 118;
    private const double TickInnerRadius = 96;
    private const double TickOuterRadius = 105;
    private const double NeedleLength = 92;
    private const double NeedleHalfWidth = 7;
    private const double NeedleTail = 16;

    /// <summary>Reloj de la aguja: ~60 fps.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>La aguja persigue al objetivo: paso proporcional al tramo que falta
    /// (arranca rápido, desacelera) con un piso para no arrastrarse en el tramo final.</summary>
    private const double NeedleEase = 0.09;
    private const double NeedleMinStep = 0.4;

    /// <summary>Duración de la animación de la aguja: proporcional al tramo que falta,
    /// con piso y techo, para que un paso corto no se arrastre ni uno largo se eternice.
    /// Va a la par del ritmo del arco y del % (ver NeedleEase).</summary>
    private const double NeedleAnimationMaxMs = 600;
    private const double NeedleAnimationMinMs = 180;

    /// <summary>Tiempo mínimo en pantalla: con todo en caché el arranque puede tardar
    /// menos que un parpadeo, y un splash que aparece y desaparece se ve como un
    /// glitch en vez de como una carga.</summary>
    private const int MinimumVisibleMilliseconds = 1100;

    /// <summary>Tope de seguridad: si el arranque queda colgado en silencio, el splash
    /// se va solo en vez de tapar la pantalla para siempre.</summary>
    private const int SafetyTimeoutMilliseconds = 25000;

    private readonly DispatcherQueueTimer _timer;
    private readonly DateTime _shownAt = DateTime.UtcNow;
    private readonly ILoggingService? _log;

    /// <summary>% objetivo: lo que pidió el arranque (nunca retrocede).</summary>
    private double _target;

    /// <summary>Señal del 100 %: se enciende al cerrarse el arranque (Complete →
    /// "Listo", o el tope de seguridad del tick) y App la espera para abrir la
    /// ventana principal. Sin
    /// RunContinuationsAsynchronously: la continuación de App retoma el turno en la
    /// cola del Dispatcher en la vuelta siguiente, sin robarle frames de animación
    /// al splash dentro del mismo set_result.</summary>
    private readonly TaskCompletionSource<bool> _readyToOpenApp = new();


    /// <summary>% dibujado: persigue al objetivo tick a tick.</summary>
    private double _shown;

    /// <summary>Animación de la aguja en curso: se mantiene referenciada mientras corre
    /// (un Storyboard sin referencia viva puede terminar antes de tiempo).</summary>
    private Storyboard? _needleAnimation;

    /// <summary>Ángulo al que apunta la animación de la aguja: sirve para calcular la
    /// duración del tramo nuevo.</summary>
    private double _needleTargetAngle = SplashGauge.AngleForPercent(0);

    /// <summary>El arranque terminó: la aguja va a 100 %, se avisa "Listo" y se cierra.</summary>
    private bool _completed;

    /// <summary>Desvanecimiento en curso (evita empezarlo dos veces).</summary>
    private bool _closing;

    /// <summary>La X ya mandó el arranque a la bandeja: no se repite.</summary>
    private bool _hideRequested;

    /// <summary>Arrastre en curso: el puntero quedó capturado por la tarjeta.</summary>
    private bool _dragging;

    /// <summary>El usuario ya movió la ventana: el centrado automático no la vuelve a
    /// tocar (si otro Loaded la recentrara, se vería como que el arrastre "vuelve").</summary>
    private bool _userMoved;

    /// <summary>Cursor en pantalla y posición de la ventana al empezar el arrastre: el
    /// delta se calcula contra el cursor ABSOLUTO, no contra el puntero relativo a la
    /// ventana (que cambia al moverla y provocaría un lazo de realimentación).</summary>
    private PointInt32 _dragStartCursor;
    private PointInt32 _dragStartWindowPosition;

    /// <summary>
    /// La X del splash avisa por acá: el arranque (App) se suscribe para que la
    /// ventana principal se cree oculta, en la bandeja.
    /// </summary>
    public event Action? HideToTrayRequested;

    public SplashWindow()
    {
        InitializeComponent();

        try { _log = App.Services.GetService<ILoggingService>(); } catch { }

        // Las marcas primero: ApplyTheme les asigna el pincel del tema, así que tiene
        // que correr después de que existan.
        BuildDial();
        ApplyTheme();

        // Idioma: MainWindow todavía no existe (es quien inicializa I18n al abrirse),
        // así que el splash resuelve el idioma guardado por su cuenta. Sin esto, los
        // pasos saldrían en inglés y recién la ventana principal cambiaría de idioma.
        try { I18n.Initialize(App.Services.GetRequiredService<ISettingsService>()); } catch { }

        // Primero el recorrido de traducción (textos del XAML) y después el estado
        // inicial: si se hiciera al revés, el recorrido tomaría el texto ya traducido
        // como si fuera la clave fuente.
        I18n.ApplyToVisualTree(RootGrid);
        StatusText.Text = I18n.T("Iniciando WinForge…");

        // La X es un icono suelto: tooltip y nombre accesible con texto traducido
        // (viven fuera del árbol y el recorrido de I18n no los ve). El texto nombra la
        // acción real —mandar la app a la bandeja—, no un "Cerrar" que haría esperar
        // que la app se cierre.
        var closeLabel = I18n.T("Minimizar a la bandeja");
        ToolTipService.SetToolTip(CloseButton, closeLabel);
        AutomationProperties.SetName(CloseButton, closeLabel);

        // Ventana sin bordes ni barra de título (el contenido ES la ventana), fija,
        // común en z-order y fuera de Alt+Tab/barra de tareas: es una ventana MÁS, no
        // un cartel topmost. Puede quedar detrás si el usuario pasa a otra app mientras
        // carga (aparece en la barra del frente solo hasta ese momento) y al abrirse la
        // ventana principal (100 %) esta entra naturalmente delante, sin forzar nada.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = false;
        }
        try { AppWindow.IsShownInSwitchers = false; } catch { /* Win10 viejo: queda en Alt+Tab, no es grave */ }
        ApplyCustomFrame();

        // El marco custom se re-aplica cuando la ventana YA se mostró: fijar los
        // atributos de DWM solo antes del primer show hace que el compositor los
        // resetee al crear el frame — y ese es exactamente el reborde blanco que
        // volvía a aparecer alrededor del splash. Loaded corre con el contenido ya
        // en pantalla: la llamada es idempotente y barata, la ventana vive un
        // segundo y medio.
        RootGrid.Loaded += (_, _) =>
        {
            ApplyWindowSizeAndCenter();
            ApplyCustomFrame();
        };

        // El splash no se cierra "por accidente": Alt+F4 mientras carga no debe
        // dejar el arranque sin ventana. Solo lo cierra el propio arranque, al
        // completar el dial (o el tope de seguridad).
        AppWindow.Closing += (_, e) =>
        {
            if (!_closing) e.Cancel = true;
        };

        // El tamaño se resuelve antes de mostrar la ventana (para que no aparezca con
        // el tamaño por defecto) y se re-confirma cuando el XamlRoot ya reporta el DPI
        // definitivo del monitor.
        ApplyWindowSizeAndCenter();
        RootGrid.Loaded += (_, _) => ApplyWindowSizeAndCenter();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TickInterval;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    /// <summary>
    /// Crea y muestra el splash. Devuelve null si no se pudo abrir: el arranque
    /// continúa sin splash (nunca debe romper el inicio de la app).
    /// </summary>
    public static SplashWindow? Start()
    {
        try
        {
            var splash = new SplashWindow();
            splash.Activate();
            return splash;
        }
        catch (Exception ex)
        {
            try
            {
                App.Services.GetService<ILoggingService>()?
                    .LogWarning($"Splash: no se pudo mostrar ({ex.Message}); el arranque continúa sin splash.");
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// La X: manda el arranque a la bandeja. La ventana de carga se aparta y la app
    /// termina de cargar SIN mostrar la ventana principal: queda el icono en el área
    /// de notificación y desde ahí se abre cuando el usuario quiera. No cancela nada.
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Arranque ya terminado (desvanecimiento en curso): nada que esconder.
        if (_closing || _completed || _hideRequested) return;

        _hideRequested = true;
        try { AppWindow.Hide(); } catch { }

        _log?.LogInfo("Splash: el usuario ocultó la ventana; la app termina de cargar en la bandeja.");
        HideToTrayRequested?.Invoke();

        // El cierre real lo hace el arranque normal: cuando la ventana principal (oculta)
        // ya está creada, Complete() completa el dial y esta ventana se desvanece y se
        // cierra sola, como siempre.
    }

    /// <summary>
    /// Arrastre de la ventana, hecho por la app (la tarjeta no tiene barra de título):
    /// al presionar se captura el puntero y cada movimiento corre la ventana con el
    /// delta del CURSOR EN PANTALLA. El bucle de movimiento del sistema
    /// (WM_NCLBUTTONDOWN con HTCAPTION) no sirve acá: con la ventana recortada por
    /// región y sin marco se rompía y volvía sola a su posición.
    /// La X queda afuera: sus clics son suyos.
    /// </summary>
    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
            if (e.OriginalSource is DependencyObject source && IsInsideCloseButton(source)) return;
            if (!GetCursorPos(out var cursor)) return;

            _dragging = true;
            _dragStartCursor = new PointInt32(cursor.X, cursor.Y);
            _dragStartWindowPosition = AppWindow.Position;
            RootGrid.CapturePointer(e.Pointer);
        }
        catch { _dragging = false; }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        try
        {
            if (!GetCursorPos(out var cursor)) return;
            int dx = cursor.X - _dragStartCursor.X;
            int dy = cursor.Y - _dragStartCursor.Y;
            if (dx == 0 && dy == 0) return;

            AppWindow.Move(new PointInt32(
                _dragStartWindowPosition.X + dx,
                _dragStartWindowPosition.Y + dy));
            _userMoved = true;
        }
        catch { }
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag(e);

    private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag(e);

    /// <summary>Termina el arrastre y suelta la captura del puntero.</summary>
    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        try { RootGrid.ReleasePointerCapture(e.Pointer); } catch { }
    }

    /// <summary>¿El nodo es la X (o algo suyo, como su icono)? Entonces no arrastra.</summary>
    private bool IsInsideCloseButton(DependencyObject source)
    {
        for (DependencyObject? node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, CloseButton)) return true;
            if (ReferenceEquals(node, RootGrid)) return false;
        }
        return false;
    }

    /// <summary>
    /// Reporta el paso en curso: mueve el objetivo de la aguja y actualiza la línea
    /// de estado (se puede llamar desde cualquier hilo).
    /// </summary>
    public void Report(double percent, string status)
    {
        void Apply()
        {
            double target = Math.Max(_target, Math.Clamp(percent, 0, 100));
            bool advanced = target > _target;
            _target = target;
            // La aguja arranca su animación acá, en el paso: la sigue el compositor y no
            // depende del tick de la UI (ver AnimateNeedleTo).
            if (advanced) AnimateNeedleTo(_target);
            if (!string.IsNullOrEmpty(status))
                StatusText.Text = status;
        }

        if (DispatcherQueue.HasThreadAccess)
            Apply();
        else
            DispatcherQueue.TryEnqueue(Apply);
    }

    /// <summary>
    /// Reporta un paso y espera —con tope— a que la aguja llegue al objetivo: así el
    /// % avanza animado y no a saltos. Si el paso real ya tardó más que la animación,
    /// no agrega espera.
    /// </summary>
    public async Task ReportAsync(double percent, string status, int maxWaitMilliseconds = 800)
    {
        Report(percent, status);
        double target = _target;
        long deadline = Environment.TickCount64 + maxWaitMilliseconds;
        while (_shown < target - 0.4 && Environment.TickCount64 < deadline)
            await Task.Delay(TickInterval);
    }

    /// <summary>El arranque terminó: la aguja completa el dial, el estado queda en "Listo" y
    /// la ventana se desvanece y se cierra sola. Es TAMBIÉN la señal del 100 % que el
    /// usuario ve: App abre la ventana principal exacto acá (ReadyToOpenAppAsync); antes
    /// de este momento NO — App ya construyó la ventana, oculta, y la deja esperando.</summary>
    public void Complete()
    {
        void Apply()
        {
            _completed = true;
            _target = 100;
            StatusText.Text = I18n.T("Listo");
            // "Listo" ya se ve → cerrar el arranque: asienta el dial completo y prende
            // la señal de apertura de la ventana principal (FinishClosing).
            FinishClosing();
        }

        if (DispatcherQueue.HasThreadAccess)
            Apply();
        else
            DispatcherQueue.TryEnqueue(Apply);
    }

    /// <summary>
    /// El 100 % ya está en pantalla (aguja en tope y "Listo"): señal que App espera
    /// (MaybeOpenMainWindow) para abrir la ventana principal. La continuación retoma el
    /// turno en la cola del Dispatcher, sin robarle frames de animación al splash. Si el
    /// splash cerró antes (tope de seguridad), la señal ya quedó prendida igual.
    /// </summary>
    public Task ReadyToOpenAppAsync() => _readyToOpenApp.Task;

    /// <summary>Guardia del desvanecimiento: corre UNA vez (la marca _closing hace de
    /// candado). Llegan acá el propio 100 % (Complete) y el tope de seguridad del tick;
    /// el tick queda solo para la transición de opacidad hasta 0 y el Close final.</summary>
    private void FinishClosing()
    {
        if (_closing) return;
        _closing = true;
        // Estado garantizado antes de desvanecer: dial completo y "100%".
        _shown = _target = 100;
        AnimateNeedleTo(100);
        ApplyPercent(100);
        // La señal de apertura va acá y no solo en Complete(): si el arranque se
        // colgó y el splash se fue por el tope de seguridad, la app igual debe
        // abrirse (como siempre hizo sin depender del splash).
        _readyToOpenApp.TrySetResult(true);
    }

    // ===== Dibujo =====

    /// <summary>
    /// Marco custom de la ventana: esquinas redondeadas y SIN el reborde blanco/gris
    /// que Windows 11 pinta alrededor de todas las ventanas —incluidas las
    /// borderless— a nivel DWM (DWMWA_COLOR_NONE, la misma receta que usa el
    /// overlay). Se aplica en el constructor y de nuevo al mostrarse la ventana:
    /// fijado solo antes del primer show, DWM lo resetea y el reborde vuelve.
    /// </summary>
    private void ApplyCustomFrame()
    {
        // Nada de redondeo del sistema: la forma la da la región propia (abajo), y el
        // marco curvo de DWM era justo lo que dejaba píxeles claros en las esquinas.
        WindowBorder.DisableSystemRounding(this);
        WindowBorder.RemoveOutline(this);
        // Además de "sin color de borde": apagar el renderizado del marco de DWM
        // entero (reborde + brillo + sombra). En algunas builds de Windows 11 el
        // reborde blanco vuelve aunque el color sea NONE, porque ese atributo solo
        // cambia el color del borde, no su existencia; esto lo apaga de raíz.
        WindowBorder.DisableFrameRendering(this);
        // Definitivo: la forma visible de la ventana es la tarjeta recortada por la
        // propia app. Todo lo que el sistema pudiera dibujar alrededor (reborde,
        // brillo, sombra) queda fuera del recorte: no hay lugar donde aparezca el
        // halo. Se re-aplica en cada llamada porque la región se calcula del tamaño
        // físico actual (el splash corrige el tamaño por DPI al cargar).
        WindowBorder.ApplyOwnRoundedRegion(this, CardCornerFraction, RegionInsetPixels);
    }

    /// <summary>
    /// Pinta el FONDO del tema y la tarjeta como un panel más: la foto del tema (la misma que usa la
    /// ventana principal), el velo encima y, detrás de la tarjeta, el parche desenfocado cuando el
    /// deslizador de blur está encendido. La tarjeta queda con el color de panel del tema y la
    /// transparencia vigente, así que el arranque se lee como el resto de la app y no como un
    /// recuadro aparte.
    ///
    /// POR QUÉ NO SALE DEL DICCIONARIO: el splash se construye ANTES de que ThemeApplier aplique el
    /// tema, así que los pinceles del diccionario son los base (Light/Dark) y no la paleta del tema
    /// elegido. Las dos fuentes que sí lo conocen son <see cref="ThemePalettes"/> (la definición del
    /// tema) y <see cref="Wallpaper.ActiveImagePath"/> (la foto que le toca).
    /// </summary>
    private void ApplyBackdrop()
    {
        try
        {
            var theme = PanelAppearance.EffectiveTheme();

            // Los ajustes GUARDADOS, no los vigentes: el splash se construye antes de que
            // OnThemeApplied los lea, así que PanelAppearance.TransparencyPercent y BlurPercent
            // todavía son 0 y la tarjeta habría quedado opaca y sin vidrio.
            var (transparency, blur) = PanelAppearance.SavedAppearance();

            // La tarjeta ES un panel: mismo color de fábrica y misma transparencia que las cards.
            var card = ThemePalettes.TryGetFactoryColor(theme, "CardBackgroundBrush", out var cardColor)
                ? cardColor
                : Microsoft.UI.Colors.Transparent;
            // El color CON el alfa aplicado es el que se pinta; la línea de diagnóstico lo registra a
            // él (y no al de fábrica) porque es lo que prueba que el ajuste llegó al arranque.
            var cardBrush = PanelAppearance.ApplyToColor(card, transparency, blur);
            CardBorder.Background = new SolidColorBrush(cardBrush);

            // El velo del tema: es parte de la identidad del fondo, no un panel, así que el ajuste de
            // transparencia no lo toca (igual que en la ventana principal).
            var scrim = ThemePalettes.TryGetFactoryColor(theme, "WindowWallpaperScrimBrush", out var scrimColor)
                ? scrimColor
                : Microsoft.UI.Colors.Transparent;

            // El fondo GUARDADO, no el vigente: Mode/CustomPath recién se llenan en Wallpaper.Apply,
            // que corre al aplicar el tema (con la ventana principal ya creada).
            var path = Wallpaper.SavedImagePath(theme);
            if (path != null && System.IO.File.Exists(path))
            {
                BackdropBorder.Background = new ImageBrush
                {
                    ImageSource = new BitmapImage(new Uri(path)),
                    Stretch = Stretch.Fill
                };
                ScrimBorder.Background = new SolidColorBrush(scrim);
                // Verificación sin pantalla: la línea de appearance.log dice con qué tema, foto,
                // transparencia y desenfoque se pintó el arranque (ver PanelAppearance.Diag).
                PanelAppearance.Diag(
                    $"splash: tema {theme} con foto {System.IO.Path.GetFileName(path)}; velo {Hex(scrim)}; " +
                    $"tarjeta {Hex(cardBrush)} (fábrica {Hex(card)}) con transparencia {transparency:0} % " +
                    $"(fábrica del tema {ThemeCatalog.DefaultPanelTransparency(theme):0} % de transparencia " +
                    $"y {ThemeCatalog.DefaultPanelBlur(theme):0} % de desenfoque); " +
                    $"desenfoque guardado {blur:0} %; acento del dial {Hex(AccentColor(theme))}");
                _ = PaintBlurAsync(path, blur);
                return;
            }

            // Sin foto: el fondo del tema, tal cual lo define. Si el tema trae un DEGRADIENTE
            // (WindowBackdropBrush, como Crepúsculo), se pinta el pincel de la definición —es lo que
            // el splash no puede sacar de los diccionarios, que todavía tienen el tema base—; si no,
            // queda el color de página compuesto opaco, como en los clásicos.
            var backdrop = ThemePalettes.DefinitionBrush(theme, "WindowBackdropBrush");
            bool gradient = backdrop is LinearGradientBrush or RadialGradientBrush;
            if (gradient)
            {
                BackdropBorder.Background = backdrop;
            }
            else
            {
                var page = ThemePalettes.TryGetFactoryColor(theme, "AppBackgroundBrush", out var pageColor)
                    ? pageColor
                    : Microsoft.UI.Colors.Transparent;
                BackdropBorder.Background = new SolidColorBrush(PanelAppearance.ComposeOpaque(page));
            }

            PanelAppearance.Diag(
                $"splash: tema {theme} sin foto (fondo {(gradient ? "con el degradado del tema" : "plano")}); " +
                $"tarjeta {Hex(cardBrush)} (fábrica {Hex(card)}) con transparencia {transparency:0} %; " +
                $"desenfoque {blur:0} %");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Splash: no se pudo pintar el fondo del tema: {ex.Message}");
        }
    }

    /// <summary>
    /// El parche desenfocado de la tarjeta: decodifica la foto del tema a la resolución de trabajo,
    /// la desenfoca con la intensidad guardada y la pinta en el rectángulo que va detrás de la
    /// tarjeta. Es el mismo algoritmo que usa la ventana principal (<see cref="PanelBlurAlgorithm"/>),
    /// pero no su mismo camino: <see cref="PanelBlur"/> publica la copia en la ventana principal, que
    /// en este momento todavía no existe.
    /// </summary>
    private async Task PaintBlurAsync(string path, double percent)
    {
        try
        {
            if (percent <= 0.0) return;

            var (data, width, height) = await PanelBlur.DecodeAsync(path);
            await Task.Run(() =>
            {
                PanelBlurAlgorithm.Apply(data, width, height, percent);
                PanelBlurAlgorithm.Opaque(data);
            });

            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                CryptographicBuffer.CreateFromByteArray(data),
                BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);

            if (BlurPatch.Fill is ImageBrush brush) brush.ImageSource = source;
            BlurPatch.Visibility = Visibility.Visible;
            PanelAppearance.Diag($"splash: parche desenfocado {width}x{height} al {percent:0} %");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Splash: no se pudo desenfocar el fondo: {ex.Message}");
        }
    }

    /// <summary>Colores del tema activo resueltos SIN ventana principal: los
    /// {ThemeResource} de XAML no conocen todavía el tema elegido por el usuario.</summary>
    private void ApplyTheme()
    {
        try
        {
            // El tema ELEGIDO, por definición y no por diccionario: ver ThemeColor.
            var theme = PanelAppearance.EffectiveTheme();

            RootGrid.RequestedTheme = ThemeBrushes.ActiveThemeKey() == "Light"
                ? ElementTheme.Light
                : ElementTheme.Dark;

            RootGrid.Background = ThemeColor("CardBackgroundBrush", theme);

            // Y el fondo del tema + la tarjeta como panel (foto, velo, transparencia y desenfoque):
            // ver ApplyBackdrop. Va acá, junto al resto de los colores.
            ApplyBackdrop();
            BrandText.Foreground = ThemeColor("PrimaryTextBrush", theme);
            StatusText.Foreground = ThemeColor("SecondaryTextBrush", theme);
            PercentText.Foreground = ThemeColor("PrimaryTextBrush", theme);
            CloseButton.Foreground = ThemeColor("SecondaryTextBrush", theme);

            var accent = ThemeColor("AccentBrush", theme);
            var track = ThemeColor("MutedBrush", theme);
            var tick = ThemeColor("ChartGridBrush", theme);

            TrackArc.Stroke = track;
            TrackArc.Opacity = 0.35;
            ProgressArc.Stroke = accent;
            Needle.Fill = accent;
            HubOuter.Fill = accent;
            HubInner.Fill = ThemeColor("CardBackgroundBrush", theme);

            foreach (var child in TickLayer.Children)
            {
                if (child is Shape shape)
                {
                    shape.Stroke = tick;
                    shape.Opacity = 0.6;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Splash: no se pudieron aplicar los colores del tema: {ex.Message}");
        }
    }

    /// <summary>El acento del tema elegido, para la línea de diagnóstico (ver ApplyBackdrop).</summary>
    private static Windows.UI.Color AccentColor(AppTheme theme)
        => ThemePalettes.TryGetFactoryColor(theme, "AccentBrush", out var color)
            ? color
            : Microsoft.UI.Colors.Transparent;

    /// <summary>Color en #AARRGGBB para la línea de diagnóstico (ver ApplyBackdrop).</summary>
    private static string Hex(Windows.UI.Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Pincel de una clave del tema ELEGIDO.
    ///
    /// Por qué no solo <see cref="ThemeBrushes.Get"/>: los pinceles del diccionario resuelven por el tema
    /// EFECTIVO (claro/oscuro), y el splash se construye ANTES de que ThemeApplier escriba la paleta del
    /// tema, así que para un tema con identidad propia (Marea, Brasa…) el diccionario todavía
    /// tiene los colores base: el acento del dial salía en el azul del sistema en vez del turquesa de la
    /// foto. La definición del tema (<see cref="ThemePalettes.TryGetFactoryColor"/>) lo conoce desde el
    /// arranque y su respaldo es, justamente, ese mismo tema base.
    ///
    /// Y por qué el respaldo al final: los colores de fábrica se capturan de las DEFINICIONES de los
    /// temas (ThemePalettes.AllSemanticKeys), así que una clave que no la define ningún tema —
    /// PrimaryTextBrush, por ejemplo, que sale del diccionario base — no tiene color de fábrica y sin
    /// esta caída el texto quedaría TRANSPARENTE (invisible).
    /// </summary>
    /// <remarks>
    /// El respaldo va contra el tema PEDIDO, no contra el activo: el diccionario vigente en este
    /// momento todavía es el del tema con el que arranca la app (oscuro), así que pedir el pincel live
    /// a secas devolvía el blanco del tema oscuro y con un tema de base clara —Rosa/Blanco— la marca
    /// "WinForge" quedaba blanca sobre la tarjeta blanca: el reporte "en el loading del tema blanco no
    /// se ve el texto". <see cref="ThemeBrushes.Get(string, AppTheme)"/> resuelve contra el diccionario
    /// BASE del tema pedido, que es justo el color que le corresponde.
    /// </remarks>
    private static Brush ThemeColor(string key, AppTheme theme)
        => ThemePalettes.TryGetFactoryColor(theme, key, out var color)
            ? new SolidColorBrush(color)
            : ThemeBrushes.Get(key, theme);

    /// <summary>Marcas del dial, aguja y arcos. Las marcas van primero para que la
    /// aguja pase por encima de ellas.</summary>
    private void BuildDial()
    {
        for (int i = 0; i <= 10; i++)
        {
            double angle = SplashGauge.AngleForPercent(i * 10);
            var (x1, y1) = SplashGauge.PointOnDial(DialCenter, DialCenter, TickInnerRadius, angle);
            var (x2, y2) = SplashGauge.PointOnDial(DialCenter, DialCenter, TickOuterRadius, angle);
            TickLayer.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                StrokeThickness = i % 5 == 0 ? 3 : 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        // Aguja: punta hacia arriba (ángulo 0 = 12 en punto) y una cola corta que
        // equilibra el centro, como un velocímetro real.
        Needle.Points = new PointCollection
        {
            new Point(DialCenter, DialCenter - NeedleLength),
            new Point(DialCenter + NeedleHalfWidth, DialCenter),
            new Point(DialCenter, DialCenter + NeedleTail),
            new Point(DialCenter - NeedleHalfWidth, DialCenter)
        };

        TrackArc.Data = BuildArcGeometry(0, 100);
        // La aguja nace en 0 % (el ticker ya no la toca: la mueve su animación).
        NeedleRotate.Angle = SplashGauge.AngleForPercent(0);
        ApplyPercent(0);
    }

    /// <summary>Arco del dial entre dos porcentajes (el trazo va sobre el radio del arco).</summary>
    private static Geometry BuildArcGeometry(double fromPercent, double toPercent)
    {
        double fromAngle = SplashGauge.AngleForPercent(fromPercent);
        double toAngle = SplashGauge.AngleForPercent(toPercent);
        var (x1, y1) = SplashGauge.PointOnDial(DialCenter, DialCenter, TrackRadius, fromAngle);
        var (x2, y2) = SplashGauge.PointOnDial(DialCenter, DialCenter, TrackRadius, toAngle);

        var figure = new PathFigure
        {
            StartPoint = new Point(x1, y1),
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(x2, y2),
            Size = new Size(TrackRadius, TrackRadius),
            IsLargeArc = Math.Abs(toAngle - fromAngle) > 180,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// Mueve la aguja al porcentaje pedido con una animación PROPIA sobre el ángulo
    /// (<see cref="DoubleAnimation"/> + Storyboard). Es una animación "independiente":
    /// la ejecuta el compositor, así que la aguja sigue barriendo aunque el hilo de la UI
    /// esté bloqueado — que es justo lo que pasa alrededor del 75 %, cuando se construyen
    /// los packs de idioma y la ventana principal — en vez de quedarse congelada hasta que
    /// la UI se libere. El arco y el % siguen al tick de la UI y se ponen al día enseguida.
    /// </summary>
    private void AnimateNeedleTo(double percent)
    {
        double angle = SplashGauge.AngleForPercent(percent);
        double delta = Math.Abs(angle - _needleTargetAngle);
        _needleTargetAngle = angle;

        int duration = (int)Math.Clamp(
            Math.Round(delta / 120.0 * NeedleAnimationMaxMs),
            NeedleAnimationMinMs,
            NeedleAnimationMaxMs);

        try
        {
            var animation = new DoubleAnimation
            {
                To = angle,
                Duration = new Duration(TimeSpan.FromMilliseconds(duration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, NeedleRotate);
            Storyboard.SetTargetProperty(animation, nameof(RotateTransform.Angle));

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
            _needleAnimation = storyboard;
        }
        catch
        {
            // Sin animación (entorno sin compositor): al menos que la aguja no quede atrás.
            NeedleRotate.Angle = angle;
        }
    }

    private void ApplyPercent(double percent)
    {
        PercentText.Text = $"{(int)Math.Round(percent)}%";
        // La aguja NO se toca acá: la mueve su propia animación independiente
        // (AnimateNeedleTo), que no se congela cuando la UI está ocupada.
        ProgressArc.Data = BuildArcGeometry(0, percent);
    }

    // ===== Animación y cierre =====

    private void OnTick()
    {
        if (_closing)
        {
            RootGrid.Opacity -= 0.12;
            if (RootGrid.Opacity <= 0.01)
            {
                _timer.Stop();
                Close();
            }
            return;
        }

        double gap = _target - _shown;
        if (gap > 0.01)
        {
            _shown = Math.Min(_target, _shown + Math.Max(NeedleMinStep, gap * NeedleEase));
            ApplyPercent(_shown);
        }

        var elapsed = (DateTime.UtcNow - _shownAt).TotalMilliseconds;

        // Terminado el arranque: esperar el tiempo mínimo en pantalla y desvanecerse.
        // El apagón de estado (dial completo y "100%") ya lo hizo FinishClosing en el
        // propio Complete(); el tick solo lleva la opacidad a 0 y cierra, así el
        // desvanecimiento no se congela si el hilo de UI se ocupa (abertura de la app).
        if (_completed && elapsed >= MinimumVisibleMilliseconds)
        {
            FinishClosing();
            return;
        }

        if (!_completed && elapsed > SafetyTimeoutMilliseconds)
        {
            _log?.LogWarning("Splash: el arranque no reportó el final; se cierra el splash por el tope de seguridad.");
            FinishClosing();
        }
    }

    /// <summary>
    /// Tamaño (en píxeles físicos, según el DPI del monitor) y centrado en el área de
    /// trabajo: una ventana nueva se abre "donde caiga" y el splash tiene que aparecer
    /// centrado, no en una esquina.
    /// </summary>
    private void ApplyWindowSizeAndCenter()
    {
        try
        {
            double scale = 1.0;
            var xamlRoot = RootGrid.XamlRoot;
            if (xamlRoot != null && xamlRoot.RasterizationScale > 0)
            {
                scale = xamlRoot.RasterizationScale;
            }
            else
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (hwnd != IntPtr.Zero)
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    if (dpi >= 96) scale = dpi / 96.0;
                }
            }

            var size = new SizeInt32(
                (int)Math.Round(WindowWidthDip * scale),
                (int)Math.Round(WindowHeightDip * scale));
            AppWindow.Resize(size);

            // Si el usuario ya movió la ventana a mano, no se la devuelve al centro: solo
            // se corrige el tamaño (el ajuste por DPI sigue valiendo).
            if (_userMoved) return;

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (area == null) return;
            var workArea = area.WorkArea;
            AppWindow.Move(new PointInt32(
                workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2),
                workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2)));
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Splash: tamaño/centrado de la ventana: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ===== Arrastre de la ventana (no tiene barra de título) =====

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    /// <summary>Punto del cursor en píxeles de pantalla (GetCursorPos).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
