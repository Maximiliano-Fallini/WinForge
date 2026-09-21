using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Controls;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Control de ventiladores: banner de instalación del driver PawnIO si falta,
/// y una card profesional por ventilador con RPM/temperatura/duty en vivo,
/// nombre editable en línea, control manual (slider) y un editor de curva
/// (temperatura → duty) que se despliega al elegir un perfil de puntos en el
/// desplegable de control (el default es BIOS (auto): el ventilador es de la
/// placa). El acceso
/// al hardware lo hace FanControlService vía LibreHardwareMonitor + PawnIO.
/// </summary>
public sealed partial class FanControlPage : Page, IBackgroundPausable
{
    private readonly IFanControlService _fanService;
    private readonly ILoggingService _loggingService;
    private readonly DispatcherQueue _dispatcherQueue;
    private DispatcherQueueTimer? _pollTimer;
    private bool _polling;

    // Rango fijo del editor de curvas (ejes).
    private const double CurveTempMin = 20;
    private const double CurveTempMax = 100;
    // Alto del lienzo del editor de curvas: tiene que dejar aire para las
    // etiquetas de los ejes (abajo las temperaturas, a la izquierda los %).
    private const double CurveCanvasHeight = 176;
    // Márgenes internos del gráfico: la curva se dibuja entre ellos, así las
    // etiquetas de los ejes nunca se le montan encima.
    private const double CurveGutterLeft = 34;
    private const double CurveGutterRight = 8;
    private const double CurveGutterTop = 6;
    private const double CurveGutterBottom = 16;
    // Lado del área sensible de cada punto del gráfico (se arrastran con el mouse).
    private const double CurveDotHitSize = 24;

    // Paso de las grillas y etiquetas de los ejes del editor de curvas: 10 % de
    // uso (eje Y) y 10 °C de temperatura (eje X). Si la card queda muy angosta
    // (layout de 4 columnas), las etiquetas de temperatura pasan a cada 20 °C
    // para no encimarse: la grilla se sigue dibujando cada 10.
    private const double CurveDutyStep = 10;
    private const double CurveTempStep = 10;
    private const double CurveTempLabelMinSpacing = 28;
    private const double CurveTempLabelWidth = 24;

    // Alto de la fila del encabezado de cada card (icono, título, editor de
    // nombre y badge de modo). Es FIJO a propósito: al abrir el editor en línea
    // la card no debe cambiar de tamaño respecto de las demás. El valor es el
    // alto natural de los controles de la app (botón/mini-control de WinUI): si
    // fuera menor, la fila crecería igual al aparecer el cuadro de texto.
    private const double CardHeaderHeight = 32;

    // Rampa de referencia: la forma que la app usa como punto de partida y de la que
    // salen los perfiles de 4, 5 y 6 puntos (ver ResampleCurve). Es una RECTA: los
    // puntos van cada 15 °C y el uso sube 25 % en cada tramo, así ningún salto es más
    // brusco que otro (la rampa escalonada anterior terminaba metiendo 35 % en 10 °C,
    // que era justo el tramo donde el ventilador se disparaba de golpe).
    // Una recta es lo más predecible: cada 15 °C de más, 25 % de uso más.
    private static readonly List<FanCurvePoint> SeedCurve = new()
    {
        new FanCurvePoint(40, 25),
        new FanCurvePoint(55, 50),
        new FanCurvePoint(70, 75),
        new FanCurvePoint(85, 100),
    };

    // Variante silenciosa: LA MISMA recta, escalada para que su uso máximo quede en
    // el 85 % (QuietProfileMaxDuty). Al compartir la forma con la estándar, el perfil
    // "(silencioso)" es fácil de entender: la misma rampa, 15 % menos de uso en cada
    // temperatura, y con los mismos escalones simétricos.
    private static readonly List<FanCurvePoint> QuietSeedCurve =
        ScaleCurveToMax(SeedCurve, QuietProfileMaxDuty);

    // Perfiles de curva: los estándares (4, 5, 6) reparten tu curva actual en esa
    // cantidad de puntos conservando su forma; los "silencioso" aplican una curva
    // predefinida más suave (QuietSeedCurve) repartida en esa cantidad de puntos.
    private sealed record ProfileOption(string Key, int Points, bool Quiet, bool Bios = false);

    // Clave del perfil sin curva: el canal vuelve al control del BIOS. Se guarda
    // como cualquier otro perfil para que el desplegable lo muestre marcado.
    private const string BiosProfileKey = "auto";

    // "BIOS (auto)" va PRIMERO y es la opción por defecto: mientras el canal esté en
    // manos de la placa no hay curva que editar, así que el bloque del gráfico queda
    // oculto hasta que el usuario elija un perfil de puntos.
    private static readonly ProfileOption[] ProfileOptions =
    {
        new(BiosProfileKey, 0, false, true),
        new("4", 4, false), new("5", 5, false), new("6", 6, false),
        new("q4", 4, true), new("q5", 5, true), new("q6", 6, true),
    };
    private const int DefaultProfilePoints = 4;

    // Techo de uso de cada familia de perfiles de curva: los perfiles SIN
    // "(silencioso)" escalan la curva hasta el 100 % de uso y los "(silencioso)"
    // hasta el 85 %. El escalado es proporcional (ScaleCurveToMax), así la forma
    // de la curva se conserva y solo cambia su altura.
    private const double StandardProfileMaxDuty = 100;
    private const double QuietProfileMaxDuty = 85;

    // Tooltip de la lectura del card. El % de Uso es el duty (señal PWM que la
    // placa le manda al ventilador): la lectura confiable de su estado.
    private const string UsageTooltipKey =
        "Uso: señal PWM que la placa le manda al ventilador (0-100%) — la lectura más confiable de su estado.";
    // En canales de solo lectura la única lectura posible es el RPM del sensor,
    // que es best-effort (header sin tacómetro, cable de señal no conectado, fan
    // alimentado desde la fuente) y puede faltar aunque el ventilador gire.
    private const string RpmTooltipKey =
        "RPM: giro reportado por el sensor del ventilador — puede no estar disponible en todos los headers.";

    // Estado por card (se re-crean al reconstruir la lista).
    private sealed class FanCard
    {
        public required string Id;
        public required bool HasControl;
        public required TextBlock Title;
        public required Viewbox FanIcon;
        public required RotateTransform FanIconTransform;
        public required Storyboard FanSpinStoryboard;
        public required DoubleAnimation FanSpinAnimation;
        public required Button RenameButton;
        public required Button IdentifyButton;
        public required Grid TitleEditPanel;
        public required TextBox TitleEditBox;
        public required TextBlock ModeText;
        public required TextBlock RpmValue;
        public required TextBlock DutyValueText;
        public required StackPanel ReadingsRow;
        public required Slider DutySlider;
        public required Button AutoButton;
        public required Grid DutyRow;
        public required Border Divider;
        // Fila del desplegable de modo/perfil: es la única puerta al editor (en los
        // canales de solo lectura no existe).
        public required Grid CurveHeaderRow;
        public required TextBlock CurveStateText;
        public required Button ApplyCurveButton;
        public required ComboBox ProfileBox;
        // Dinámica de la curva: contenedor de la fila y un cuadro por parámetro
        // (°C, s, %/tick, %/tick). Los cuadros se arman al abrir el editor (igual
        // que las filas de puntos), así que existen recién ahí.
        public required WrapPanel DynamicsHost;
        public NumberBox? HysteresisBox;
        public NumberBox? ResponseBox;
        public NumberBox? StepUpBox;
        public NumberBox? StepDownBox;
        public double HysteresisC;
        public double ResponseSeconds;
        public double StepUpPercent;
        public double StepDownPercent;
        // Evita que sincronizar el desplegable dispare su SelectionChanged.
        public bool SuppressProfileEvents;
        // Espejo de IsCurveActive (lo refresca el sondeo): la curva está activa en
        // el canal.
        public bool CurveApplied;
        // El desplegable está en "BIOS (auto)": el canal lo gobierna la placa, así
        // que no hay gráfico, puntos ni botones de curva que mostrar.
        public bool BiosMode;
        // Bloque del editor (estado, dinámica, gráfico, puntos y botones): se
        // despliega solo con un perfil de puntos elegido.
        public required StackPanel CurvePanel;
        // Apartado plegado/desplegado. "Personalizar" es la puerta: abierto se ve el
        // desplegable de perfiles y la curva; cerrado la card queda corta para llegar
        // al resto de los ventiladores sin scrollear.
        public bool EditorOpen;
        public required Button CurveToggleButton;
        public required FontIcon CurveChevron;
        // Cuerpo del editor (dinámica, gráfico, puntos, botones y ayuda): se va entero
        // con "BIOS (auto)", donde no hay curva que editar ni aplicar.
        public required StackPanel CurveBodyPanel;
        public required StackPanel PointsHost;
        public required Canvas CurveCanvas;
        public required Border Root;
        public required string RawName;
        public List<FanCurvePoint> CurvePoints = new();
        public double? CurrentTempC;
        public bool SuppressSliderEvents;
        // Punto del gráfico que se está arrastrando (-1 = ninguno). Índice dentro
        // de CurvePoints, no de la lista ordenada.
        public int DragIndex = -1;
        // Renombrado en línea: en curso + "no confirmes" (lo marca el ✗ antes de
        // que el cuadro pierda el foco).
        public bool IsRenaming;
        public bool SuppressRenameCommit;
        public bool IsFanSpinning;
        public int FanSpinSpeedBucket = -1;
        // Prueba de identificación en curso: el badge lo avisa y el sondeo no lo
        // pisa mientras dura.
        public bool IsIdentifying;
        // El canal dice tener control pero la escritura de duty falló: no
        // ofrecemos un slider que parece funcionar y no hace nada.
        public bool WriteFailed;
        // El motor soltó este canal al BIOS por perder el sensor de temperatura:
        // la card lo avisa hasta que el usuario reaplica la curva o elige BIOS.
        public bool LastSensorLost;
        // Último estado publicado por el sondeo, para restaurar después de la
        // prueba (duty manual previo, o devolver el canal al BIOS).
        public bool LastManual;
        public bool LastCurveActive;
        public double? LastDutyPercent;
    }

    private readonly List<FanCard> _cards = new();
    private bool _restoreAllVisible;

    // Layout de cards: 3 o 4 columnas como máximo (persistido en settings).
    private const string ColumnsSettingsKey = "fancontrol.columns";
    private const double CardSpacing = 18;
    private const double MinimumCardWidth = 250;
    private const double ScrollBarReserve = 14;
    private double _cardWidth = 340;
    private int _columns = 3;

    public FanControlPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
        _fanService = App.Services.GetRequiredService<IFanControlService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        // El icono del TÍTULO va con el ACENTO del tema, igual que los títulos de las otras páginas
        // (Sensores, Overclock USB, Memoria). Se dibujaba con el color de texto principal, así que
        // al lado de un título con color se leía como un contorno blanco (o negro en tema claro).
        // Es el pincel vivo del tema: sigue el cambio de tema sin rehacer nada.
        PageFanIcon.Content = FanIcon.CreateCanvas(20, ThemeBrushes.Get("AccentBrush"));
        Unloaded += (s, e) => StopPolling();
    }

    private static SolidColorBrush MutedBrush => ThemeBrushes.Get("MutedBrush");
    private static SolidColorBrush CardBrush => ThemeBrushes.Get("CardBackgroundBrush");
    private static SolidColorBrush StrokeBrush => ThemeBrushes.Get("CardBorderBrush");
    private static SolidColorBrush HoverBrush => ThemeBrushes.Get("CardHoverBrush");

    // =====================================================================
    // Layout adaptable: 3 o 4 columnas como máximo
    // =====================================================================

    private void ApplyColumnLayout()
    {
        // Las cards y sus separaciones llenan el ancho visible completo: ya no
        // queda una franja vacía a la derecha por celdas fijas sin separación.
        double pageWidth = FanScroll.ActualWidth - 48 - ScrollBarReserve;
        if (pageWidth <= 0) return;

        // La elección del usuario es un máximo. En vistas angostas, o si hay
        // pocos ventiladores, bajamos columnas para no comprimir ni desperdiciar
        // todo el espacio a la derecha.
        int requestedColumns = Math.Min(_columns, Math.Max(1, _cards.Count));
        int columnsThatFit = Math.Max(1,
            (int)Math.Floor((pageWidth + CardSpacing) / (MinimumCardWidth + CardSpacing)));
        int columns = Math.Min(requestedColumns, columnsThatFit);

        _cardWidth = Math.Floor((pageWidth - CardSpacing * (columns - 1)) / columns);
        _loggingService.LogDebug($"FanControlPage: layout {columns} columna(s), ancho de card {_cardWidth:0.#} (page {pageWidth:0.#}, {_cards.Count} cards)");
        FansList.Width = pageWidth;
        foreach (var card in _cards)
            card.Root.Width = _cardWidth;

        Columns3Button.IsEnabled = _columns != 3;
        Columns4Button.IsEnabled = _columns != 4;
    }

    private void FanScroll_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyColumnLayout();

    private void Columns3Button_Click(object sender, RoutedEventArgs e)
    {
        _columns = 3;
        ApplyColumnLayout();
        PersistColumns();
    }

    private void Columns4Button_Click(object sender, RoutedEventArgs e)
    {
        _columns = 4;
        ApplyColumnLayout();
        PersistColumns();
    }

    private void PersistColumns()
    {
        try
        {
            var settings = App.Services.GetService<ISettingsService>();
            if (settings != null)
            {
                settings.Set(ColumnsSettingsKey, _columns);
                settings.Save();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"FanControlPage: persistir columnas falló: {ex.Message}");
        }
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        I18n.LanguageChanged += RebuildAll;

        // Cargar columnas preferidas (3 por defecto).
        var settings = App.Services.GetService<ISettingsService>();
        _columns = Math.Clamp(settings?.Get(ColumnsSettingsKey, 3) ?? 3, 3, 4);
        ApplyColumnLayout();

        _ = RefreshAsync(firstLoad: true);
        StartPolling();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        PauseBackgroundTimers();
        I18n.LanguageChanged -= RebuildAll;
    }

    /// <summary>
    /// Pausa de bandeja (IBackgroundPausable): Stop real del sondeo de 2 s
    /// (RefreshAsync toca el SuperIO en cada tick) — cerraba la fuga: antes
    /// seguía sondeando el chip con la ventana oculta.
    /// NOTA: el MOTOR de curvas (FanControlService, proceso) NO se pausa: se
    /// gobierna con su propio switch por canal.
    /// </summary>
    public void PauseBackgroundTimers() => StopPolling();

    /// <summary>Reanudación desde bandeja: solo con ventana visible.</summary>
    public void ResumeBackgroundTimers()
    {
        if (App.MainWindowInstance?.IsWindowVisible == true)
            StartPolling();
    }

    // =====================================================================
    // Ciclo de refresco
    // =====================================================================

    private async Task RefreshAsync(bool firstLoad = false)
    {
        try
        {
            // El acceso al hardware (Update de registros SuperIO) es I/O lenta:
            // corre fuera del hilo de UI y solo los toques de UI se encolan.
            _loggingService.LogDebug($"FanControlPage: refresco inicio (firstLoad={firstLoad})");
            var status = await Task.Run(() => _fanService.GetStatus());
            _loggingService.LogDebug($"FanControlPage: status ok (fanCount={status.FanCount})");

            _dispatcherQueue.TryEnqueue(() =>
            {
                UpdatePawnBanner(status);
                UpdateStatusText(status);
            });

            if (!status.PawnIOInstalled && firstLoad)
                return; // sin driver no hay nada que listar

            _loggingService.LogDebug("FanControlPage: leyendo fans...");
            var fans = await Task.Run(() => _fanService.GetFans());
            _loggingService.LogDebug($"FanControlPage: fans leídos ({fans.Count})");

            _dispatcherQueue.TryEnqueue(() =>
            {
                _loggingService.LogDebug($"FanControlPage: rebuild de cards ({fans.Count} ventiladores)");
                RebuildFanCards(fans);
                _loggingService.LogDebug("FanControlPage: rebuild listo");
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"FanControlPage: refresco falló: {ex.Message}");
        }
    }

    private void UpdatePawnBanner(FanControlStatus status)
    {
        if (status.PawnIOInstalled)
        {
            PawnBanner.Visibility = Visibility.Collapsed;
            return;
        }

        PawnBanner.Visibility = Visibility.Visible;
        PawnBannerTitle.Text = I18n.T("Driver PawnIO no instalado");
        PawnBannerDesc.Text = I18n.T("El control de ventiladores necesita el driver de kernel PawnIO (código abierto bajo GPL-2.0, firmado digitalmente, el mismo que usa LibreHardwareMonitor). Se descarga el instalador oficial, se verifica su hash SHA-256 y se instala en silencio — un clic.");
        InstallPawnButton.Content = I18n.T("Instalar PawnIO");
    }

    private void UpdateStatusText(FanControlStatus status)
    {
        if (!status.PawnIOInstalled)
        {
            // Sin PawnIO los fans de la GPU (NVIDIA/AMD/Intel) igual se ven: son
            // APIs de modo usuario del driver de video (NVAPI en NVIDIA, ADL en
            // AMD, IGCL en Intel), no necesitan driver de kernel. PawnIO solo hace
            // falta para el chip SuperIO de la placa. El mensaje avisa, no bloquea.
            StatusText.Text = status.FanCount > 0
                ? I18n.T("ℹ Se ven los ventiladores de la GPU (sin PawnIO). Instalá el driver para ver y controlar también los de la placa madre.")
                : I18n.T("⚠ Instalá el driver PawnIO para ver y controlar los ventiladores.");
            StatusText.Foreground = Feedback.WarningBrush;
            RestoreAllButton.Visibility = Visibility.Collapsed;
            _restoreAllVisible = false;
            return;
        }

        if (status.FanCount == 0)
        {
            // PawnIO instalado pero ningún canal: o la placa no expone fans por
            // SuperIO, o LHM falló (bus ISA ocupado al abrir), o la GPU no publica
            // ningún canal (AMD sin soporte de Overdrive/atiadlxx.dll, Intel sin
            // canal de control, NVIDIA sin cooler settings). El detalle por GPU
            // queda en el log (FanControlService: LogGpuDiagnostics).
            StatusText.Text = I18n.T("ℹ No se detectó ningún canal de ventilador. Si otro monitor de hardware está corriendo (HWiNFO, Afterburner, etc.), cerralo y volvé a entrar a esta pestaña.");
            StatusText.Foreground = Feedback.WarningBrush;
            RestoreAllButton.Visibility = Visibility.Collapsed;
            _restoreAllVisible = false;
            return;
        }

        // Sin texto de estado cuando todo está bien (el chip no aporta nada al
        // usuario; el detalle queda en el log).
        StatusText.Text = string.Empty;
        // El restablecimiento global queda siempre a mano: no requiere que haya
        // algo en manual o con curva para aparecer.
        RestoreAllButton.Visibility = Visibility.Visible;
        _restoreAllVisible = true;
    }

    // =====================================================================
    // Cards de ventiladores
    // =====================================================================

    private void RebuildFanCards(List<FanControlInfo> fans)
    {
        // Reconciliar por id: crea las cards nuevas y actualiza las existentes
        // sin destruir la que el usuario está tocando (evita saltos del slider
        // y del editor de curva).
        var byId = fans.Where(f => f.HasControl || f.Rpm.HasValue).ToList();
        var existing = _cards.ToDictionary(c => c.Id, c => c);
        var seen = new HashSet<string>();

        foreach (var fan in byId)
        {
            seen.Add(fan.Id);
            if (existing.TryGetValue(fan.Id, out var card))
            {
                UpdateCard(card, fan);
            }
            else
            {
                try
                {
                    card = BuildFanCard(fan);
                    _cards.Add(card);
                    FansList.Children.Add(card.Root);
                    UpdateCard(card, fan);
                }
                catch (Exception ex)
                {
                    // Una card que no se puede construir no debe llevarse la página.
                    _loggingService.LogWarning($"FanControlPage: no se pudo construir la card de {fan.Id}: {ex.Message}");
                }
            }
        }

        // Quitar cards cuyo ventilador desapareció.
        foreach (var card in _cards.Where(c => !seen.Contains(c.Id)).ToList())
        {
            FansList.Children.Remove(card.Root);
            _cards.Remove(card);
        }


        ApplyColumnLayout();
    }

    private FanCard BuildFanCard(FanControlInfo fan)
    {
        // ---------------------------------------------------------------
        // 1) Header: icono + título (editable en línea) + badge de modo.
        // ---------------------------------------------------------------
        var title = new TextBlock
        {
            Text = DisplayName(fan),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };

        var renameButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE70F", FontSize = 11 },
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 2, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = null,
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(renameButton, I18n.T("Editar nombre"));

        // "Identificar": prueba física del canal. Lo lleva al 100% unos segundos
        // y después lo devuelve exactamente a como estaba. Es la única forma de
        // atar una card a un ventilador real cuando el chip no reporta tacómetro
        // (varios headers de la placa leen 0 RPM aunque el fan gire).
        var identifyButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE721", FontSize = 11 },
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 2, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = null,
            BorderThickness = new Thickness(0),
            Visibility = fan.HasControl ? Visibility.Visible : Visibility.Collapsed
        };
        ToolTipService.SetToolTip(identifyButton, I18n.T("Identificar: acelera este canal al 100% unos segundos y después lo devuelve a como estaba. Mirá qué ventilador se acelera para saber cuál es en la placa."));

        // Editor en línea: reemplaza el título al editar (Enter confirma, Esc
        // cancela y salir del foco confirma). Toma TODO el ancho de la columna del
        // título y la MISMA altura que el estado de lectura, así la card no se
        // deforma ni se sale del ancho (antes el ✗ quedaba cortado por el borde).
        // La visibilidad la gobierna el PANEL: los hijos nacen visibles —
        // dejarlos colapsados escondía el cuadro y dejaba la card sin nombre.
        var titleEditBox = new TextBox
        {
            PlaceholderText = fan.RawName,
            MaxLength = 40,
            FontSize = 12,
            Height = CardHeaderHeight,
            // Height solo no alcanza: el estilo por defecto del TextBox trae
            // MinHeight 32 y la fila crecería igual. Hay que bajarlo explícito.
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(4),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        // VerticalContentAlignment NO alcanza en el TextBox de WinUI: el
        // ScrollViewer interno de su plantilla ("ContentElement") viene alineado
        // arriba y no sigue esa propiedad, y con un cuadro de 32 px el texto queda
        // pegado al borde superior (asimétrico con el título de lectura al lado).
        // Se centra por dentro, igual que el InputBox del NumberBox.
        titleEditBox.Loaded += (s, e) =>
        {
            if (titleEditBox.FindName("ContentElement") is FrameworkElement content)
                content.VerticalAlignment = VerticalAlignment.Center;
        };
        // Botones cuadrados del mismo alto que el cuadro: así el ✓ y el ✗ quedan
        // centrados con el texto y no "colgando" a media altura.
        var editConfirm = new Button
        {
            Content = new FontIcon { Glyph = "\uE73E", FontSize = 11 },
            Width = CardHeaderHeight,
            Height = CardHeaderHeight,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(editConfirm, I18n.T("Guardar nombre"));
        var editCancel = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 11 },
            Width = CardHeaderHeight,
            Height = CardHeaderHeight,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(editCancel, I18n.T("Cancelar"));

        // [cuadro de nombre (ocupa el sobrante)] [✓] [✗]
        var titleEditPanel = new Grid
        {
            ColumnSpacing = 6,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleEditPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleEditPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleEditPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleEditBox, 0);
        Grid.SetColumn(editConfirm, 1);
        Grid.SetColumn(editCancel, 2);
        titleEditPanel.Children.Add(titleEditBox);
        titleEditPanel.Children.Add(editConfirm);
        titleEditPanel.Children.Add(editCancel);

        // El icono monocromático se anima únicamente mientras el ventilador tiene actividad.
        var fanIconTransform = new RotateTransform();
        var fanIcon = FanIcon.CreateCanvas(18);
        fanIcon.RenderTransform = fanIconTransform;
        fanIcon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        fanIcon.Opacity = 0.5;
        var fanSpinAnimation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        var fanSpinStoryboard = new Storyboard();
        Storyboard.SetTarget(fanSpinAnimation, fanIconTransform);
        Storyboard.SetTargetProperty(fanSpinAnimation, "Angle");
        fanSpinStoryboard.Children.Add(fanSpinAnimation);

        // Estado de lectura: título + lápiz. El icono del ventilador vive en su
        // propia columna del encabezado para que el editor no lo tape ni empuje
        // el badge de modo fuera de la card.
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleRow.Children.Add(title);
        titleRow.Children.Add(renameButton);
        titleRow.Children.Add(identifyButton);

        // Ancho mínimo para los estados habituales (BIOS (auto), Curva, Manual) y
        // texto centrado: así el badge no cambia de tamaño al cambiar de modo y la
        // cabecera no se mueve.
        var modeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MinWidth = 84,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = MutedBrush
        };

        // Encabezado de 3 columnas con alto FIJO: [icono] [título | editor] [modo].
        var header = new Grid { ColumnSpacing = 8, MinHeight = CardHeaderHeight };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fanIcon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(fanIcon, 0);
        Grid.SetColumn(titleRow, 1);
        Grid.SetColumn(titleEditPanel, 1); // mismo hueco: nunca visibles a la vez
        Grid.SetColumn(modeText, 2);
        header.Children.Add(fanIcon);
        header.Children.Add(titleRow);
        header.Children.Add(titleEditPanel);
        header.Children.Add(modeText);

        // ---------------------------------------------------------------
        // 2) Fila de la única lectura del canal (chip de RPM).
        // ---------------------------------------------------------------
        // La card muestra UNA sola lectura, y siempre la confiable: en los
        // canales con control PWM el % de Uso vive en la fila del control (más
        // abajo) y esta fila queda oculta; los canales de solo lectura (GPU Intel
        // e headers sin PWM) no exponen duty, así que su único dato es el RPM.
        var rpmValue = new TextBlock { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

        var statsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        var rpmChip = StatChip(I18n.T("RPM"), rpmValue);
        statsRow.Children.Add(rpmChip);
        ToolTipService.SetToolTip(rpmChip, I18n.T(RpmTooltipKey));

        // ---------------------------------------------------------------
        // 3) Control manual: slider 0-100 + botón automático.
        // ---------------------------------------------------------------
        // Ancho fijo y centrado: el porcentaje cambia de "—" a "100 %" y la fila no
        // tiene que moverse por eso.
        var dutyValueText = new TextBlock
        {
            Text = "—",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Width = 46,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            TickFrequency = 10,
            TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Inline,
            IsThumbToolTipEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };

        var autoButton = new Button
        {
            Content = I18n.T("Automático"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 4, 12, 4),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        var dutyRow = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 0) };
        dutyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dutyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dutyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dutyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dutyLabel = new TextBlock
        {
            Text = I18n.T("Uso"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = MutedBrush
        };
        Grid.SetColumn(dutyLabel, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(dutyValueText, 2);
        Grid.SetColumn(autoButton, 3);
        dutyRow.Children.Add(dutyLabel);
        dutyRow.Children.Add(slider);
        dutyRow.Children.Add(dutyValueText);
        dutyRow.Children.Add(autoButton);
        // El % de Uso es el duty real leído del chip: lo explicamos para que no
        // parezca una estimación de software.
        ToolTipService.SetToolTip(dutyLabel, I18n.T(UsageTooltipKey));
        ToolTipService.SetToolTip(dutyValueText, I18n.T(UsageTooltipKey));

        // ---------------------------------------------------------------
        // 4) Separador + switch de curva + editor (colapsado por defecto).
        // ---------------------------------------------------------------
        var divider = new Border
        {
            Height = 1,
            Background = StrokeBrush,
            Opacity = 0.5,
            Margin = new Thickness(0, 2, 0, 2)
        };

        // Lienzo del gráfico de curva (dentro de un Border: Canvas no tiene
        // esquinas redondeadas propias). El fondo transparente no se ve, pero
        // hace falta para que el lienzo reciba los clics (doble clic = agregar
        // punto): un Canvas sin Background no es testeable con el mouse.
        var curveCanvas = new Canvas
        {
            Height = CurveCanvasHeight,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        var curveCanvasHost = new Border
        {
            Child = curveCanvas,
            Background = HoverBrush,
            CornerRadius = new CornerRadius(6),
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 400, CurveCanvasHeight) }
        };
        curveCanvasHost.SizeChanged += (s, e) =>
            ((RectangleGeometry)curveCanvasHost.Clip!).Rect = new Windows.Foundation.Rect(0, 0, curveCanvasHost.ActualWidth, CurveCanvasHeight);

        // Filas de puntos (temp → duty) editables.
        var pointsHost = new StackPanel { Spacing = 4 };

        // MinWidth/MinHeight en 0 SIEMPRE en los botones compactos: el estilo por
        // defecto de WinUI impone 120x32, así que sin esto no pueden medir lo que su
        // texto y en una card angosta se van de línea.
        var addPointButton = new Button
        {
            Content = I18n.T("Añadir punto"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 11,
            MinWidth = 0,
            MinHeight = 0
        };
        var resetCurveButton = new Button
        {
            Content = I18n.T("Reiniciar curva"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 11,
            MinWidth = 0,
            MinHeight = 0
        };
        // Los dos botones de edición, uno al lado del otro con tamaño natural (así
        // no quedan dos cuadros enormes con el texto flotando en el medio).
        // Centrados en la card: son los controles de la curva y quedan mejor en el
        // medio que pegados al borde izquierdo.
        var curveButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        curveButtons.Children.Add(addPointButton);
        curveButtons.Children.Add(resetCurveButton);

        var curveHint = new TextBlock
        {
            Text = I18n.T("Arrastrá un punto para moverlo (temperatura y uso); doble clic sobre el gráfico agrega uno nuevo."),
            FontSize = 11,
            Foreground = MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        // Estado (arriba, para saber de un vistazo si la curva controla el canal) y
        // la acción de aplicar/soltar, al final del bloque con el ancho completo.
        // Centrado y con alto reservado para dos líneas: la card no cambia de alto
        // cuando el texto pasa de una línea a dos al cambiar de estado.
        var curveStateText = new TextBlock
        {
            FontSize = 11,
            Foreground = MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 30,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Centrado en la card (como los botones de la curva), no estirado de lado a
        // lado.
        var applyCurveButton = new Button
        {
            Content = I18n.T("Aplicar esta curva"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 6, 16, 6),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // Desplegable de perfiles: elige quién maneja el canal (BIOS o una curva de
        // N puntos). Vive DENTRO de "Personalizar", centrado arriba del gráfico. Los
        // ítems son ComboBoxItem con el perfil en el Tag (como el resto de la app): la
        // elección se lee del Tag y no del índice, así el handler no depende de que el
        // orden de los ítems coincida con ProfileOptions.
        // Se le quita el contorno por defecto de WinUI (ese recuadro claro): queda
        // como el resto de los controles de la card, por relleno y no por línea.
        var profileBox = new ComboBox
        {
            MinWidth = 0,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = HoverBrush,
            BorderThickness = new Thickness(0),
            // Texto centrado, tanto el elegido como el placeholder.
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        foreach (var opt in ProfileOptions)
            profileBox.Items.Add(new ComboBoxItem
            {
                Content = opt.Bios
                    ? I18n.T("BIOS (auto)")
                    : opt.Quiet
                        ? I18n.T("{0} puntos (silencioso)", opt.Points)
                        : I18n.T("{0} puntos", opt.Points),
                Tag = opt.Key,
                // Los ítems de la lista, centrados como el desplegable cerrado.
                HorizontalContentAlignment = HorizontalAlignment.Center
            });
        ToolTipService.SetToolTip(profileBox,
            I18n.T("Control del ventilador: «BIOS (auto)» se lo deja a la placa; un perfil de puntos toma el canal con una curva (los estándar llegan al 100 % de uso y los (silencioso) al 85 %)."));

        // Encabezado plegable "Personalizar", centrado y con su flecha: es LA puerta
        // del apartado. Desplegado muestra el desplegable de perfiles y toda la
        // lógica de la curva; plegado deja la card corta para llegar al resto de los
        // ventiladores sin scrollear.
        var curveChevron = new FontIcon { Glyph = "\uE70D", FontSize = 10 };
        var toggleContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggleContent.Children.Add(new TextBlock
        {
            Text = I18n.T("Personalizar"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        toggleContent.Children.Add(curveChevron);

        var curveToggleButton = new Button
        {
            Content = toggleContent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6, 14, 6),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            // Relleno suave sin contorno: se lee como control sin dibujar un borde.
            Background = HoverBrush,
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(curveToggleButton, I18n.T("Gráfico y ajustes"));

        // El encabezado ocupa la fila entera (la card conserva su forma con el
        // apartado abierto o cerrado).
        var curveHeaderRow = new Grid();
        curveHeaderRow.Children.Add(curveToggleButton);

        // Dinámica de la curva: qué tiene que pasar entre la temperatura y el duty
        // escrito. El contenedor vive en el bloque desde el arranque (así la fila no
        // cambia de forma al abrir el editor) y se llena con BuildDynamicsRow. Centrado:
        // los campos tienen ancho fijo, así que la fila no se mueve.
        var dynamicsPanel = new WrapPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };

        // Lo que solo tiene sentido con una curva en juego (dinámica, gráfico, puntos,
        // botones y ayuda): se oculta entero con "BIOS (auto)", donde el canal lo
        // gobierna la placa y no hay nada que editar ni aplicar.
        var curveBodyPanel = new StackPanel { Spacing = 8 };
        curveBodyPanel.Children.Add(dynamicsPanel);
        curveBodyPanel.Children.Add(curveCanvasHost);
        curveBodyPanel.Children.Add(pointsHost);
        curveBodyPanel.Children.Add(curveButtons);
        curveBodyPanel.Children.Add(applyCurveButton);
        curveBodyPanel.Children.Add(curveHint);

        // Apartado completo: el desplegable de perfiles arriba, y debajo el estado y
        // el cuerpo del editor. Todo esto se despliega con "Personalizar".
        var curvePanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        curvePanel.Children.Add(profileBox);
        curvePanel.Children.Add(curveStateText);
        curvePanel.Children.Add(curveBodyPanel);

        // ---------------------------------------------------------------
        // Card raíz.
        // ---------------------------------------------------------------
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(header);
        stack.Children.Add(statsRow);
        stack.Children.Add(dutyRow);
        stack.Children.Add(divider);
        stack.Children.Add(curveHeaderRow);
        stack.Children.Add(curvePanel);

        // Sin contorno: la card se separa del fondo por su color (estilo plano), no
        // por una línea alrededor.
        var root = new Border
        {
            Width = _cardWidth,
            Background = CardBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12)
        };
        root.Child = stack;

        var card = new FanCard
        {
            Id = fan.Id,
            HasControl = fan.HasControl,
            Title = title,
            FanIcon = fanIcon,
            FanIconTransform = fanIconTransform,
            FanSpinStoryboard = fanSpinStoryboard,
            FanSpinAnimation = fanSpinAnimation,
            RenameButton = renameButton,
            IdentifyButton = identifyButton,
            TitleEditPanel = titleEditPanel,
            TitleEditBox = titleEditBox,
            ModeText = modeText,
            RpmValue = rpmValue,
            ReadingsRow = statsRow,
            DutyValueText = dutyValueText,
            DutySlider = slider,
            AutoButton = autoButton,
            DutyRow = dutyRow,
            Divider = divider,
            CurveHeaderRow = curveHeaderRow,
            CurveToggleButton = curveToggleButton,
            CurveChevron = curveChevron,
            CurveStateText = curveStateText,
            ApplyCurveButton = applyCurveButton,
            ProfileBox = profileBox,
            DynamicsHost = dynamicsPanel,
            CurvePanel = curvePanel,
            CurveBodyPanel = curveBodyPanel,
            PointsHost = pointsHost,
            CurveCanvas = curveCanvas,
            Root = root,
            // El id como último recurso: si LHM no le puso nombre al sensor, la
            // card igual se puede identificar y renombrar.
            RawName = string.IsNullOrWhiteSpace(fan.RawName) ? fan.Id : fan.RawName
        };

        // ------------------------------------------------------------------
        // Eventos.
        // ------------------------------------------------------------------

        // Durante el arrastre solo refleja el número; la escritura real al chip
        // ocurre al soltar (PointerCaptureLost), para no martillar los registros.
        slider.ValueChanged += (s, e) =>
        {
            if (card.SuppressSliderEvents) return;
            dutyValueText.Text = $"{e.NewValue:0}%";
        };
        slider.PointerCaptureLost += (s, e) =>
        {
            if (!card.SuppressSliderEvents)
                _ = ApplyDutyAsync(card, (float)slider.Value);
        };
        autoButton.Click += (s, e) => _ = SetAutoAsync(card);

        // Renombrado en línea.
        renameButton.Click += (s, e) => StartInlineRename(card);
        identifyButton.Click += (s, e) => _ = IdentifyAsync(card);
        title.Tapped += (s, e) => StartInlineRename(card);
        editConfirm.Click += (s, e) => CommitInlineRename(card);
        // El ✗ se anticipa al robo de foco: PointerPressed corre ANTES que el
        // LostFocus del cuadro, así que alcanza a marcar "no confirmes".
        editCancel.PointerPressed += (s, e) => card.SuppressRenameCommit = true;
        editCancel.Click += (s, e) => CancelInlineRename(card);
        titleEditBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; CommitInlineRename(card); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; CancelInlineRename(card); }
        };
        // Salir del foco confirma: sin esto el editor quedaba abierto con el
        // título oculto si el usuario hacía clic en cualquier otro lado.
        titleEditBox.LostFocus += (s, e) =>
        {
            if (card.SuppressRenameCommit) CancelInlineRename(card);
            else CommitInlineRename(card);
        };

        // Plegar o desplegar el bloque del editor. No toca el canal ni la curva: solo
        // cambia lo que se ve.
        curveToggleButton.Click += (s, e) =>
        {
            card.EditorOpen = !card.EditorOpen;
            _loggingService.LogDebug($"FanControlPage: editor de {card.Id} {(card.EditorOpen ? "desplegado" : "plegado")}");
            UpdateCurveSection(card);
            RedrawCurve(card);
        };

        applyCurveButton.Click += (s, e) =>
        {
            _loggingService.LogDebug($"FanControlPage: aplicar curva de {card.Id}");
            ApplyCurve(card);
            _loggingService.LogDebug($"FanControlPage: curva de {card.Id} aplicada");
        };

        // Cambiar de perfil: los estándares reparten la curva ACTUAL en N puntos
        // (no imponen forma nueva); los silenciosos aplican la curva suave
        // predefinida repartida en N puntos.
        profileBox.SelectionChanged += (s, e) =>
        {
            if (card.SuppressProfileEvents) return;
            if (profileBox.SelectedItem is not ComboBoxItem item || item.Tag is not string key) return;

            int index = Array.FindIndex(ProfileOptions, o => o.Key == key);
            if (index < 0) return;

            var opt = ProfileOptions[index];

            // La elección se REGISTRA siempre, aunque no haya que rehacer la curva:
            // si se saliera antes de guardar el perfil, el sondeo (que re-sincroniza
            // el desplegable cada 2 s) lo revertiría al perfil viejo y la opción
            // parecería "no se puede cambiar".
            try { _fanService.SetCurveProfile(card.Id, opt.Key); } catch { }

            // "BIOS (auto)": no es un perfil de curva, es devolverle el canal a la
            // placa. Suelta la curva (si estaba aplicada) y llama al BIOS, sin
            // cerrar el editor: se puede volver a aplicar sin salir y entrar.
            if (opt.Bios)
            {
                // Elegir BIOS resuelve el aviso de sensor perdido: es el mismo
                // estado al que el fail-safe llevó el canal.
                try { _fanService.ClearSensorLost(card.Id); } catch { }
                card.LastSensorLost = false;
                _ = SetAutoAsync(card);
                return;
            }

            // Elegir un perfil de puntos deja el apartado desplegado (la intención es
            // ajustar la curva): se carga la del canal —la guardada, o la semilla— y
            // su dinámica antes de repartirla.
            card.EditorOpen = true;
            EnsureCurveLoaded(card);
            BuildDynamicsRow(card);
            _loggingService.LogDebug($"FanControlPage: editor de {card.Id} desplegado ({opt.Key})");

            // La FORMA: los "(silencioso)" parten de la curva suave predefinida y
            // los estándares, de la curva actual del canal. Después se escala la
            // altura al techo de su familia (100 % los estándar, 85 % los
            // silenciosos), conservando la forma.
            var shape = opt.Quiet
                ? ResampleCurve(QuietSeedCurve, opt.Points)
                : ResampleCurve(card.CurvePoints, opt.Points);
            card.CurvePoints = ScaleCurveToMax(shape, opt.Quiet ? QuietProfileMaxDuty : StandardProfileMaxDuty);

            // Elegir un perfil de puntos muestra el cuerpo del editor ANTES de
            // dibujar (un lienzo colapsado mide 0 y el gráfico saldría vacío).
            card.BiosMode = false;
            UpdateCurveSection(card);
            BuildPointRows(card);
            RedrawCurve(card);
            SaveCurve(card, card.CurveApplied);

            // Re-sincroniza con la cantidad de puntos ya repartida, para que el
            // desplegable quede marcado en el perfil elegido.
            UpdateCurveSection(card);
        };

        // El lienzo se redibuja cuando cambia de tamaño (primer layout incluido).
        curveCanvas.SizeChanged += (s, e) => RedrawCurve(card);
        addPointButton.Click += (s, e) => AddCurvePoint(card);

        // Curva con el mouse (estilo MSI Afterburner): arrastrar un punto para
        // moverlo y doble clic en el gráfico para agregar uno nuevo. El puntero se
        // captura en el LIENZO y no en el punto: RedrawCurve recrea los puntos en
        // cada movimiento y el arrastre tiene que sobrevivir a eso.
        curveCanvas.PointerMoved += (s, e) => DragCurvePoint(card, e);
        curveCanvas.PointerReleased += (s, e) => EndCurveDrag(card, e);
        curveCanvas.PointerCaptureLost += (s, e) => EndCurveDrag(card, null);
        curveCanvas.DoubleTapped += (s, e) => AddCurvePointAt(card, e);
        resetCurveButton.Click += (s, e) =>
        {
            // "Reiniciar" vuelve al perfil por defecto (4 puntos, la recta de referencia).
            card.CurvePoints = new List<FanCurvePoint>(SeedCurve);
            try { _fanService.SetCurveProfile(card.Id, "4"); } catch { }
            BuildPointRows(card);
            RedrawCurve(card);
            SaveCurve(card, card.CurveApplied);
        };

        return card;
    }

    // Chip compacto "etiqueta + valor" para la fila de lecturas.
    private static Grid StatChip(string label, TextBlock value)
    {
        var panel = new Grid { ColumnSpacing = 6 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Ancho FIJO y centrado para el valor: si midiera lo que dice, cada cambio de
        // lectura ("9%" → "100%") movería la fila entera.
        value.Width = 46;
        value.TextAlignment = TextAlignment.Center;
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(value, 1);
        panel.Children.Add(labelBlock);
        panel.Children.Add(value);
        return panel;
    }

    private static string DisplayName(FanControlInfo fan) => fan.Name;

    private void UpdateCard(FanCard card, FanControlInfo fan)
    {
        card.CurrentTempC = fan.Temperature;
        card.LastManual = fan.IsManual;
        card.LastCurveActive = fan.IsCurveActive;
        card.LastDutyPercent = fan.DutyPercent;
        try { card.LastSensorLost = _fanService.WasSensorLost(card.Id); }
        catch { card.LastSensorLost = false; }

        // Una sola lectura por card, y siempre la confiable. En los canales con
        // control PWM el % de Uso (duty leído del registro del chip, en vivo)
        // está en la fila del control, así que esta fila se oculta. Los canales
        // de solo lectura (GPU Intel, headers sin PWM) no exponen duty: su único
        // dato es el RPM, que mostramos solo si el sensor lo reporta (> 0). Un 0
        // o null es "sin lectura de tacómetro" (header sin sensor, cable no
        // conectado), nunca "parado": el ventilador puede estar girando igual.
        if (fan.HasControl)
        {
            card.ReadingsRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            card.ReadingsRow.Visibility = Visibility.Visible;
            if (fan.Rpm.HasValue && fan.Rpm.Value > 0)
            {
                card.RpmValue.Text = $"{fan.Rpm.Value:0}";
                card.RpmValue.Foreground = null; // vuelve al color heredado normal
            }
            else
            {
                card.RpmValue.Text = "—";
                card.RpmValue.Foreground = MutedBrush;
            }
        }

        UpdateFanAnimation(card, fan);

        // Canal de solo lectura (RPM sin duty; GPU Intel o header sin PWM): sin controles.
        if (!fan.HasControl)
        {
            card.ModeText.Text = I18n.T("Solo lectura");
            card.ModeText.Foreground = MutedBrush;
            card.DutyRow.Visibility = Visibility.Collapsed;
            card.Divider.Visibility = Visibility.Collapsed;
            card.CurveHeaderRow.Visibility = Visibility.Collapsed;
            card.CurvePanel.Visibility = Visibility.Collapsed;
            card.DutyValueText.Text = "—";
            return;
        }

        card.DutyRow.Visibility = Visibility.Visible;
        card.Divider.Visibility = Visibility.Visible;
        card.CurveHeaderRow.Visibility = Visibility.Visible;

        // El servicio es el que dice si la curva controla el canal; el desplegable
        // se sincroniza con ese estado (y con el perfil guardado del canal).
        card.CurveApplied = fan.IsCurveActive;
        UpdateCurveSection(card);

        if (fan.IsCurveActive)
        {
            card.ModeText.Text = I18n.T("Curva");
            card.ModeText.Foreground = Feedback.AccentBrush;
            // Sin botón "Automático": para volver al control de la placa ya está
            // "BIOS (auto)" en el desplegable (y "Soltar al BIOS" en el editor).
            card.AutoButton.Visibility = Visibility.Collapsed;
            card.DutySlider.IsEnabled = false;

            var duty = fan.DutyPercent ?? 0;
            card.SuppressSliderEvents = true;
            if (Math.Abs(card.DutySlider.Value - duty) > 0.5)
                card.DutySlider.Value = duty;
            card.SuppressSliderEvents = false;
            card.DutyValueText.Text = $"{duty:0}%";
        }
        else if (fan.IsManual)
        {
            card.ModeText.Text = I18n.T("Manual");
            card.ModeText.Foreground = Feedback.AccentBrush;
            card.AutoButton.Visibility = Visibility.Visible;
            card.DutySlider.IsEnabled = true;

            var duty = fan.DutyPercent ?? 0;
            card.SuppressSliderEvents = true;
            if (Math.Abs(card.DutySlider.Value - duty) > 0.5)
                card.DutySlider.Value = duty;
            card.SuppressSliderEvents = false;
            card.DutyValueText.Text = $"{duty:0}%";
        }
        else
        {
            card.ModeText.Text = I18n.T("BIOS (auto)");
            card.ModeText.Foreground = MutedBrush;
            card.AutoButton.Visibility = Visibility.Collapsed;
            card.DutySlider.IsEnabled = false;

            // En modo BIOS el slider muestra el % REAL que está entregando el
            // chip (lectura del registro PWM) — el mismo dato que muestra
            // FanControl como "control". El usuario lo ve pero no lo toca.
            var live = fan.DutyPercent;
            card.DutySlider.IsEnabled = false;
            card.SuppressSliderEvents = true;
            card.DutySlider.Value = live ?? 0;
            card.SuppressSliderEvents = false;
            card.DutyValueText.Text = live.HasValue ? $"{live.Value:0}%" : "—";
        }

        // Avisos que tienen prioridad sobre el texto de modo normal.
        if (card.WriteFailed)
        {
            // El canal reporta control pero no acepta escrituras: lo decimos en
            // vez de dejar un slider que parece funcionar y no hace nada.
            card.ModeText.Text = I18n.T("Sin control");
            card.ModeText.Foreground = Feedback.ErrorBrush;
            card.DutySlider.IsEnabled = false;
            card.AutoButton.Visibility = Visibility.Collapsed;
        }
        else if (card.IsIdentifying)
        {
            // El canal queda en manual mientras dura la prueba: el badge lo avisa
            // y no ofrecemos "Automático" (pisaría la restauración).
            card.ModeText.Text = I18n.T("Identificando…");
            card.ModeText.Foreground = Feedback.AccentBrush;
            card.AutoButton.Visibility = Visibility.Collapsed;
        }
        else if (card.LastSensorLost)
        {
            // Fail-safe: el motor soltó el canal al BIOS porque el sensor dejó de
            // dar temperatura. Prioridad sobre el badge de BIOS: es el evento que
            // explica por qué la curva se desactivó sola.
            card.ModeText.Text = I18n.T("Sensor perdido");
            card.ModeText.Foreground = Feedback.WarningBrush;
            card.AutoButton.Visibility = Visibility.Collapsed;
        }
        // En un canal que no acepta escrituras no ofrecemos acciones que peleen
        // con eso.
        card.IdentifyButton.IsEnabled = !card.WriteFailed;
        card.ProfileBox.IsEnabled = !card.WriteFailed;

        // El marcador de temperatura actual en el gráfico.
        if (fan.IsCurveActive || card.CurvePanel.Visibility == Visibility.Visible)
            RedrawCurve(card);
    }

    /// <summary>
    /// El duty manda la velocidad del giro (lectura confiable del registro PWM).
    /// El RPM es best-effort, pero cuando hay lectura confirma que el ventilador
    /// gira: nunca dejamos el icono quieto con el tacómetro marcando vueltas.
    /// En canales de solo lectura (sin PWM) el RPM es la única referencia.
    /// </summary>
    private static void UpdateFanAnimation(FanCard card, FanControlInfo fan)
    {
        double activity = fan.DutyPercent ?? 0;
        if (fan.Rpm.GetValueOrDefault() > 0)
            activity = Math.Max(activity, 20); // giro mínimo visible hasta el siguiente escalón
        activity = Math.Clamp(activity, 0, 100);

        if (activity <= 0.1)
        {
            if (card.IsFanSpinning)
                card.FanSpinStoryboard.Stop();
            card.FanIconTransform.Angle = 0;
            card.FanIcon.Opacity = 0.5;
            card.IsFanSpinning = false;
            card.FanSpinSpeedBucket = -1;
            return;
        }

        // No reiniciar la animación cada tick: solo cambia su velocidad al pasar
        // a otro escalón de uso, para que el giro se perciba continuo.
        int speedBucket = Math.Clamp((int)Math.Ceiling(activity / 10), 1, 10);
        if (card.IsFanSpinning && card.FanSpinSpeedBucket == speedBucket) return;

        card.FanSpinStoryboard.Stop();
        int durationMs = 1900 - speedBucket * 135; // 0.55s a 1.77s por vuelta
        card.FanSpinAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        card.FanIcon.Opacity = 1;
        card.FanSpinStoryboard.Begin();
        card.IsFanSpinning = true;
        card.FanSpinSpeedBucket = speedBucket;
    }

    private async Task ApplyDutyAsync(FanCard card, float value)
    {
        try
        {
            var ok = await Task.Run(() => _fanService.SetManualDuty(card.Id, value));
            if (!ok)
                _loggingService.LogWarning($"FanControlPage: no se pudo fijar duty {value:0}% en {card.Id}");
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"FanControlPage: ApplyDuty falló: {ex.Message}");
        }
    }

    private async Task SetAutoAsync(FanCard card)
    {
        try
        {
            // Si la curva está aplicada, "Automático" la apaga también (si no, el
            // motor volvería a aplicar el duty al siguiente tick).
            if (card.CurveApplied)
            {
                // Devuelve el canal al BIOS, pero deja el editor abierto: así se
                // puede volver a aplicar la curva sin salir y volver a entrar.
                SaveCurve(card, enabled: false);
                card.CurveApplied = false;
            }

            // El canal queda en manos de la placa: el desplegable lo refleja.
            try { _fanService.SetCurveProfile(card.Id, BiosProfileKey); } catch { }
            UpdateCurveSection(card);
            await Task.Run(() => _fanService.SetAuto(card.Id));
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"FanControlPage: SetAuto falló: {ex.Message}");
        }
    }

    // =====================================================================
    // Identificar el ventilador físico
    // =====================================================================

    // Duración del pulso de identificación. Más largo que el tick de sondeo
    // (2 s) para que el salto al 100% llegue a verse en la card.
    private const int IdentifyPulseMs = 3000;

    /// <summary>
    /// Lleva el canal al 100% unos segundos y después lo devuelve a como estaba.
    /// Es la única manera de saber qué ventilador físico es una card cuando el
    /// chip no reporta tacómetro, y de paso prueba si el canal realmente acepta
    /// escrituras (si no, la card lo avisa en vez de mostrar un slider muerto).
    /// </summary>
    private async Task IdentifyAsync(FanCard card)
    {
        if (card.IsIdentifying || card.WriteFailed || !card.HasControl) return;

        card.IsIdentifying = true;
        card.IdentifyButton.IsEnabled = false;
        card.ModeText.Text = I18n.T("Identificando…");
        card.ModeText.Foreground = Feedback.AccentBrush;

        // Estado a restaurar al terminar la prueba.
        bool wasCurve = card.CurveApplied;
        bool wasManual = card.LastManual;
        double lastDuty = card.LastDutyPercent ?? 0;
        var curvePoints = wasCurve ? _fanService.GetFanCurve(card.Id) : null;
        bool hasCurve = curvePoints is { Count: >= 2 };

        try
        {
            // Con una curva activa, el motor del servicio re-aplicaría su duty en
            // el próximo tick (2 s) y cortaría la prueba: la suspendemos y la
            // devolvemos tal cual estaba.
            if (wasCurve && hasCurve)
                await Task.Run(() => _fanService.SetFanCurve(card.Id, curvePoints!, enabled: false));

            bool ok = await Task.Run(() => _fanService.SetManualDuty(card.Id, 100));
            if (!ok)
            {
                card.WriteFailed = true;
                _loggingService.LogWarning($"FanControlPage: la prueba de identificación no pudo escribir el duty de {card.Id}");
            }
            else
            {
                await Task.Delay(IdentifyPulseMs);
            }
        }
        catch (Exception ex)
        {
            card.WriteFailed = true;
            _loggingService.LogWarning($"FanControlPage: prueba de identificación de {card.Id} falló: {ex.Message}");
        }
        finally
        {
            try
            {
                if (wasCurve && hasCurve)
                    await Task.Run(() => _fanService.SetFanCurve(card.Id, curvePoints!, enabled: true));
                else if (wasManual)
                    await Task.Run(() => _fanService.SetManualDuty(card.Id, (float)Math.Clamp(lastDuty, 0, 100)));
                else
                    await Task.Run(() => _fanService.SetAuto(card.Id));
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"FanControlPage: no se pudo restaurar {card.Id} tras la prueba: {ex.Message}");
            }

            card.IsIdentifying = false;
            card.IdentifyButton.IsEnabled = true;
            _ = RefreshAsync();
        }
    }

    // =====================================================================
    // Renombrado en línea
    // =====================================================================

    private void StartInlineRename(FanCard card)
    {
        card.IsRenaming = true;
        card.SuppressRenameCommit = false;
        card.Title.Visibility = Visibility.Collapsed;
        card.RenameButton.Visibility = Visibility.Collapsed;
        card.IdentifyButton.Visibility = Visibility.Collapsed;
        // El badge de modo se esconde mientras editás: le deja todo el ancho al
        // editor y evita que el cuadro lo empuje fuera de la card.
        card.ModeText.Visibility = Visibility.Collapsed;
        card.TitleEditPanel.Visibility = Visibility.Visible;
        card.TitleEditBox.Text = _fanService.GetCustomName(card.Id);
        card.TitleEditBox.PlaceholderText = card.RawName;
        // El foco se pide en el pase siguiente: el panel acaba de hacerse visible
        // y pedirlo en el mismo tick puede fallar (todavía no está montado). Sin
        // foco el cuadro nunca dispara LostFocus y el editor quedaba abierto.
        _dispatcherQueue.TryEnqueue(() =>
        {
            card.TitleEditBox.Focus(FocusState.Programmatic);
            card.TitleEditBox.SelectAll();
        });
    }

    private void CommitInlineRename(FanCard card)
    {
        if (!card.IsRenaming) return;
        try { _fanService.SetCustomName(card.Id, card.TitleEditBox.Text); }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"FanControlPage: rename falló: {ex.Message}");
        }
        EndInlineRename(card);
    }

    private void CancelInlineRename(FanCard card)
    {
        if (!card.IsRenaming) return;
        EndInlineRename(card);
    }

    private void EndInlineRename(FanCard card)
    {
        card.IsRenaming = false;
        card.SuppressRenameCommit = false;
        card.TitleEditPanel.Visibility = Visibility.Collapsed;
        card.Title.Visibility = Visibility.Visible;
        card.RenameButton.Visibility = Visibility.Visible;
        // Solo vuelve si el canal es controlable: en los de solo lectura nunca
        // estuvo visible.
        card.IdentifyButton.Visibility = card.HasControl ? Visibility.Visible : Visibility.Collapsed;
        card.ModeText.Visibility = Visibility.Visible;

        // El nombre mostrado nunca queda vacío: si no hay nombre personalizado
        // (o el usuario borró todo) se vuelve al nombre del sensor.
        var custom = _fanService.GetCustomName(card.Id);
        card.Title.Text = custom.Length > 0 ? custom : card.RawName;
    }

    // =====================================================================
    // Editor de curva
    // =====================================================================

    // Cuadro numérico de la dinámica, con el MISMO patrón que las filas de puntos
    // (el único que ya funciona en esta página): valor en el inicializador, mínimo y
    // máximo definidos y validación que reemplaza la entrada inválida. MinWidth 0
    // porque el estilo por defecto de WinUI mide 120 px y no entra más de uno por
    // fila en una card.
    private static NumberBox DynamicsBox(double value, double max)
    {
        var box = new NumberBox
        {
            Value = double.IsNaN(value) ? 0 : value,
            Minimum = 0,
            Maximum = max,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            MinWidth = 0,
            Width = 84,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        CenterNumberText(box);
        return box;
    }

    // El NumberBox de WinUI no expone TextAlignment: se lo pide al TextBox de su
    // plantilla (nombre estándar "InputBox"), así el número queda centrado en el
    // cuadro igual que en el resto de los campos de la card.
    private static void CenterNumberText(NumberBox box)
    {
        box.Loaded += (s, e) =>
        {
            if (box.FindName("InputBox") is TextBox input)
                input.TextAlignment = TextAlignment.Center;
        };
    }

    // "etiqueta + cuadro" de un parámetro, con el detalle en el tooltip (que es
    // donde cabe una explicación sin ensanchar la card).
    private static StackPanel DynamicsField(string label, NumberBox box, string tooltip)
    {
        var field = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        field.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        field.Children.Add(box);
        ToolTipService.SetToolTip(field, tooltip);
        ToolTipService.SetToolTip(box, tooltip);
        return field;
    }

    // Arma la fila de dinámica del canal (histéresis, respuesta, subida, bajada). Se
    // construye al abrir el editor, y los cuadros nacen con el valor guardado: así no
    // hay que asignarlo después de crear el control.
    private void BuildDynamicsRow(FanCard card)
    {
        if (card.HysteresisBox != null) return;

        FanCurveDynamics dynamics;
        try
        {
            dynamics = _fanService.GetCurveDynamics(card.Id);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"FanControlPage: no se pudo leer la dinámica de {card.Id}: {ex.Message}");
            dynamics = new FanCurveDynamics(2, 3, 0, 0);
        }

        card.HysteresisC = dynamics.HysteresisC;
        card.ResponseSeconds = dynamics.ResponseSeconds;
        card.StepUpPercent = dynamics.StepUpPercent;
        card.StepDownPercent = dynamics.StepDownPercent;

        var hysteresisBox = DynamicsBox(card.HysteresisC, 20);
        var responseBox = DynamicsBox(card.ResponseSeconds, 60);
        var stepUpBox = DynamicsBox(card.StepUpPercent, 100);
        var stepDownBox = DynamicsBox(card.StepDownPercent, 100);

        // Los cuadros se guardan con cada cambio (también sin la curva aplicada:
        // queda como borrador, igual que los puntos).
        hysteresisBox.ValueChanged += (s, e) => SaveDynamics(card);
        responseBox.ValueChanged += (s, e) => SaveDynamics(card);
        stepUpBox.ValueChanged += (s, e) => SaveDynamics(card);
        stepDownBox.ValueChanged += (s, e) => SaveDynamics(card);

        card.HysteresisBox = hysteresisBox;
        card.ResponseBox = responseBox;
        card.StepUpBox = stepUpBox;
        card.StepDownBox = stepDownBox;

        card.DynamicsHost.Children.Clear();
        card.DynamicsHost.Children.Add(DynamicsField(I18n.T("Histéresis °C"), hysteresisBox,
            I18n.T("Histéresis (°C): el uso no cambia hasta que la temperatura se aleje más que esta banda de donde cambió por última vez. 0 la desactiva; en los extremos de la curva se ignora.")));
        card.DynamicsHost.Children.Add(DynamicsField(I18n.T("Respuesta s"), responseBox,
            I18n.T("Tiempo de respuesta (s): cuántos segundos tiene que sostenerse un cambio de temperatura antes de aplicarlo. 0 lo desactiva; en los extremos de la curva se ignora.")));
        card.DynamicsHost.Children.Add(DynamicsField(I18n.T("Subida %"), stepUpBox,
            I18n.T("Subida máxima de uso por actualización (cada 2 s): suaviza los cambios bruscos. 0 = sin límite.")));
        card.DynamicsHost.Children.Add(DynamicsField(I18n.T("Bajada %"), stepDownBox,
            I18n.T("Bajada máxima de uso por actualización (cada 2 s): suaviza los cambios bruscos. 0 = sin límite.")));

        _loggingService.LogDebug($"FanControlPage: fila de dinámica de {card.Id} armada");
    }

    // Guarda la dinámica tal como quedó en los cuadros del canal.
    private void SaveDynamics(FanCard card)
    {
        if (card.HysteresisBox is not { } hysteresisBox || card.ResponseBox is not { } responseBox ||
            card.StepUpBox is not { } stepUpBox || card.StepDownBox is not { } stepDownBox)
            return;

        double hysteresis = BoxValue(hysteresisBox, card.HysteresisC);
        double response = BoxValue(responseBox, card.ResponseSeconds);
        double stepUp = BoxValue(stepUpBox, card.StepUpPercent);
        double stepDown = BoxValue(stepDownBox, card.StepDownPercent);

        if (hysteresis == card.HysteresisC && response == card.ResponseSeconds &&
            stepUp == card.StepUpPercent && stepDown == card.StepDownPercent)
            return;

        card.HysteresisC = hysteresis;
        card.ResponseSeconds = response;
        card.StepUpPercent = stepUp;
        card.StepDownPercent = stepDown;

        try
        {
            _fanService.SetCurveDynamics(card.Id, new FanCurveDynamics(hysteresis, response, stepUp, stepDown));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"FanControlPage: guardar dinámica falló: {ex.Message}");
        }
    }

    // Valor del cuadro; si el usuario lo dejó vacío (NaN) se conserva el anterior.
    private static double BoxValue(NumberBox box, double fallback) =>
        double.IsNaN(box.Value) ? fallback : box.Value;

    private void SaveCurve(FanCard card, bool enabled)
    {
        try
        {
            // Se persiste SIEMPRE ordenada por temperatura (el editor mantiene
            // las filas donde el usuario las puso; la curva es una función).
            var sorted = card.CurvePoints.OrderBy(p => p.TemperatureC).ToList();
            _fanService.SetFanCurve(card.Id, sorted, enabled);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"FanControlPage: guardar curva falló: {ex.Message}");
        }
    }

    // Carga la curva del canal (la guardada, o la semilla de tres puntos) la primera
    // vez que el editor la necesita.
    private void EnsureCurveLoaded(FanCard card)
    {
        if (card.CurvePoints.Count > 0) return;

        // Un canal sin curva guardada arranca con el perfil por defecto (4 puntos).
        var saved = _fanService.GetFanCurve(card.Id);

        if (saved.Count < 2)
        {
            // Sin curva guardada: la semilla de referencia (que ya tiene 4 puntos).
            card.CurvePoints = new List<FanCurvePoint>(SeedCurve);
        }
        else if (saved.Count < DefaultProfilePoints)
        {
            // Curva guardada por una versión anterior (el default viejo era de 3
            // puntos): se reparte al perfil por defecto. La forma no cambia, porque
            // los puntos nuevos caen sobre los tramos que ya existían; así el
            // desplegable arranca en "4 puntos" y no en una cantidad vieja.
            card.CurvePoints = ResampleCurve(saved, DefaultProfilePoints);
            SaveCurve(card, card.CurveApplied);
        }
        else
        {
            card.CurvePoints = saved;
        }

        BuildPointRows(card);
    }

    // Refleja en el desplegable el perfil de la curva. Primero manda el perfil
    // recordado del canal (si la cantidad de puntos coincide), y si no, la cantidad
    // de puntos actual. Si no es ningún perfil (el usuario agregó o borró puntos a
    // mano), queda sin selección mostrando esa cantidad como texto de relleno.
    private void SyncProfileSelection(FanCard card)
    {
        // El sondeo (UpdateCard) pasa por acá cada 2 s: mientras el desplegable
        // está ABIERTO no se le toca nada — escribir SelectedIndex con el popup
        // desplegado se lo cierra solo y la opción queda imposible de elegir.
        if (card.ProfileBox.IsDropDownOpen) return;

        int biosIndex = Array.FindIndex(ProfileOptions, o => o.Bios);

        // "El canal lo gobierna la placa" solo es cierto si no hay curve nuestra y
        // el chip no está en modo software. Si está en software —nuestro slider, otro
        // programa, o una sesión anterior que lo dejó así— afirma BIOS sería mentir:
        // en ese caso el desplegable queda sin perfil y muestra el estado real.
        bool boardControls = !card.CurveApplied && !card.LastManual;

        string stored = string.Empty;
        try { stored = _fanService.GetCurveProfile(card.Id); } catch { }

        // "BIOS (auto)" guardado con la curva aplicada es contradictorio (solo puede
        // darse si el reaplicado del arranque reactivó la curva): manda el estado
        // real del canal y se limpia la clave para no volver a mentir.
        if (stored == BiosProfileKey && card.CurveApplied)
        {
            try { _fanService.SetCurveProfile(card.Id, string.Empty); } catch { }
            stored = string.Empty;
        }

        int index = -1;
        bool bios;

        if (stored == BiosProfileKey)
        {
            // El ventilador está en manos de la placa: es el estado por defecto.
            // (Si el chip quedó en software, abajo se cae a "sin perfil".)
            bios = boardControls;
        }
        else
        {
            // Perfil guardado que todavía coincide con la curva del editor.
            if (!string.IsNullOrEmpty(stored))
                index = Array.FindIndex(ProfileOptions, o => !o.Bios && o.Key == stored
                    && o.Points == card.CurvePoints.Count);

            if (index >= 0)
            {
                bios = false;
            }
            else
            {
                // Sin perfil que coincida: si el canal está en manos de la placa es
                // el estado por defecto; si lo gobierna una curva nuestra, mostramos
                // su cantidad de puntos; y si está en manual, no inventamos perfil.
                bios = stored.Length == 0 && boardControls;
                if (!bios && card.CurveApplied)
                    index = Array.FindIndex(ProfileOptions, o => !o.Quiet && !o.Bios && o.Points == card.CurvePoints.Count);
            }
        }

        if (bios) index = biosIndex;
        card.BiosMode = bios;

        // Solo se escribe si cambió de verdad: así el refresco no re-escribe la
        // selección (ni dispara eventos) cuando ya está en el perfil correcto.
        if (card.ProfileBox.SelectedIndex != index)
        {
            card.SuppressProfileEvents = true;
            card.ProfileBox.SelectedIndex = index;
            card.SuppressProfileEvents = false;
        }

        // Sin perfil que corresponda el desplegable queda sin selección, mostrando el
        // estado real como relleno: en modo manual no hay curva que valga, y si el
        // usuario agregó o borró puntos a mano, esa cantidad.
        var placeholder = index < 0
            ? (card.LastManual ? I18n.T("Manual") : I18n.T("{0} puntos", card.CurvePoints.Count))
            : string.Empty;
        if (card.ProfileBox.PlaceholderText != placeholder)
            card.ProfileBox.PlaceholderText = placeholder;
    }

    // Uso que la curva pide a una temperatura: interpolación lineal entre los puntos
    // vecinos y extremos fijos, igual que el motor de curvas del servicio.
    private static double CurveDutyAtTemp(IReadOnlyList<FanCurvePoint> points, double tempC)
    {
        if (points.Count == 0) return 0;
        if (tempC <= points[0].TemperatureC) return points[0].DutyPercent;
        if (tempC >= points[^1].TemperatureC) return points[^1].DutyPercent;

        for (int i = 1; i < points.Count; i++)
        {
            var from = points[i - 1];
            var to = points[i];
            if (tempC > to.TemperatureC) continue;

            double span = to.TemperatureC - from.TemperatureC;
            if (span <= 0.01) return to.DutyPercent;

            double f = (tempC - from.TemperatureC) / span;
            return from.DutyPercent + (to.DutyPercent - from.DutyPercent) * f;
        }
        return points[^1].DutyPercent;
    }

    // Reparte una curva en `count` puntos conservando su forma: mantiene los extremos
    // de temperatura y toma el uso interpolado en el medio. Es lo que hace el
    // desplegable de perfiles, así cambiar de 4 a 6 puntos no rehace la curva.
    private static List<FanCurvePoint> ResampleCurve(IReadOnlyList<FanCurvePoint> source, int count)
    {
        var ordered = source.Count >= 2
            ? source.OrderBy(p => p.TemperatureC).ToList()
            : new List<FanCurvePoint>(SeedCurve);

        double tMin = ordered[0].TemperatureC;
        double tMax = ordered[^1].TemperatureC;

        // Cada punto necesita 1 °C de separación para no pisarse: si el rango es más
        // angosto que eso, se abre desde donde empieza la curva.
        double needed = count - 1;
        if (tMax - tMin < needed)
        {
            tMin = Math.Clamp(tMin, CurveTempMin, CurveTempMax - needed);
            tMax = tMin + needed;
        }

        var result = new List<FanCurvePoint>(count);
        for (int i = 0; i < count; i++)
        {
            double temp = tMin + (tMax - tMin) * i / (count - 1);
            result.Add(new FanCurvePoint(
                Math.Round(temp),
                Math.Round(CurveDutyAtTemp(ordered, temp))));
        }
        return result;
    }

    // Escala una curva de forma PROPORCIONAL para que su uso máximo llegue al techo
    // pedido: todos los valores se multiplican por el mismo factor, así la forma se
    // conserva y solo cambia la altura. Es lo que hace que los perfiles estándar
    // terminen en 100 % y los "(silencioso)" en 85 %, sin deformar la curva.
    private static List<FanCurvePoint> ScaleCurveToMax(IReadOnlyList<FanCurvePoint> source, double maxDuty)
    {
        if (source.Count == 0) return new List<FanCurvePoint>();

        double currentMax = source.Max(p => p.DutyPercent);
        // Curva plana en 0 % (o casi): no hay forma que escalar.
        if (currentMax <= 0.01) return new List<FanCurvePoint>(source);

        double factor = maxDuty / currentMax;
        return source
            .Select(p => new FanCurvePoint(
                p.TemperatureC,
                Math.Clamp(Math.Round(p.DutyPercent * factor), 0, 100)))
            .ToList();
    }

    // Aplica la curva del editor al canal. Es la única acción que le saca el
    // ventilador al BIOS; para devolverlo NO hay botón: está "BIOS (auto)" en el
    // desplegable (y es el mismo camino que usa el reaplicado del arranque).
    private void ApplyCurve(FanCard card)
    {
        EnsureCurveLoaded(card);
        SaveCurve(card, enabled: true);
        // Reaplicar resuelve el estado de sensor perdido: el aviso desaparece.
        try { _fanService.ClearSensorLost(card.Id); } catch { }
        card.LastSensorLost = false;

        try
        {
            // Apenas la curva controla el canal, el perfil guardado vuelve a ser el de
            // la cantidad de puntos: "BIOS (auto)" ya no es cierto.
            if (_fanService.GetCurveProfile(card.Id) == BiosProfileKey)
                _fanService.SetCurveProfile(card.Id, string.Empty);
        }
        catch { }

        // Reflejo inmediato: no esperamos al próximo sondeo.
        card.CurveApplied = true;
        UpdateCurveSection(card);
        RedrawCurve(card);
        _ = RefreshAsync();
    }

    // Pinta el apartado de la curva: el desplegable (que elige BIOS o un perfil) y el
    // bloque del editor, que se despliega solo cuando hay un perfil de puntos elegido.
    // El desplegable queda siempre en su lugar para que la fila no cambie de forma
    // entre cards.
    private void UpdateCurveSection(FanCard card)
    {
        // Durante la identificación el servicio suspende la curva y deja el canal en
        // software a propósito: no reflejamos ese estado transitorio (el badge ya
        // dice "Identificando…") para no mostrar un modo que no es el real.
        if (card.IsIdentifying) return;

        SyncProfileSelection(card);

        // "Personalizar" abre y cierra el apartado entero; la flecha lo dice.
        card.CurveChevron.Glyph = card.EditorOpen ? "\uE70E" : "\uE70D";
        card.CurvePanel.Visibility = card.EditorOpen ? Visibility.Visible : Visibility.Collapsed;

        // Adentro, el desplegable de perfiles manda: con "BIOS (auto)" no hay curva que
        // editar ni que aplicar, así que el cuerpo (dinámica, gráfico, puntos y
        // botones) desaparece y queda solo el desplegable y la línea de estado.
        card.CurveBodyPanel.Visibility = card.BiosMode ? Visibility.Collapsed : Visibility.Visible;

        if (card.EditorOpen && !card.BiosMode)
        {
            // El cuerpo puede mostrarse sin que el usuario haya elegido perfil (canal
            // con curva aplicada al arrancar): la curva y su dinámica tienen que estar
            // armadas. Ambas son idempotentes.
            EnsureCurveLoaded(card);
            BuildDynamicsRow(card);
        }

        // El BOTÓN se pinta siempre, en cualquier modo: depende solo de si la curva
        // controla el canal. Si se pintara dentro de cada rama de la línea de estado,
        // los modos que no lo tocan (BIOS o manual) lo dejarían con la etiqueta del
        // estado anterior: por ejemplo "Soltar al BIOS" cuando en realidad la curva no
        // está aplicada y el botón APLICA (etiqueta y acción desincronizadas).
        // El botón aplica lo que hay en el editor y nada más: para devolver el canal
        // al BIOS está "BIOS (auto)" en el desplegable. Queda SIEMPRE visible y
        // habilitado: reaplicar una curva ya aplicada solo la reescribe igual, y así no
        // desaparece un control que el usuario espera encontrar en el bloque.
        card.ApplyCurveButton.Visibility = Visibility.Visible;
        card.ApplyCurveButton.Content = I18n.T("Aplicar esta curva");
        card.ApplyCurveButton.Background = Feedback.AccentBrush;
        card.ApplyCurveButton.Foreground = ThemeBrushes.Get("AccentForegroundBrush");
        card.ApplyCurveButton.BorderThickness = new Thickness(0);

        // La línea de estado describe el modo del canal SOLO cuando hay algo que
        // aclarar: con la curva controlando el canal el badge ya dice "Curva", así que
        // no repetimos el aviso acá (y se oculta la línea entera, sin dejar hueco).
        if (card.CurveApplied)
        {
            card.CurveStateText.Visibility = Visibility.Collapsed;
        }
        else
        {
            card.CurveStateText.Visibility = Visibility.Visible;

            if (card.LastSensorLost)
            {
                // Fail-safe: el badge ya dice "Sensor perdido"; la línea explica qué
                // pasó y qué hacer, en vez del engañoso "el ventilador lo controla
                // la placa" (cierto para el chip, pero no la razón).
                card.CurveStateText.Text = I18n.T("Sensor perdido: el canal volvió al BIOS. Reaplicá la curva para retomar el control.");
                card.CurveStateText.Foreground = Feedback.WarningBrush;
            }
            else if (card.BiosMode)
            {
                // Modo por defecto: el ventilador lo gobierna la placa y no hay nada
                // nuestro escrito en el canal.
                card.CurveStateText.Text = I18n.T("BIOS (auto): el ventilador lo controla la placa.");
                card.CurveStateText.Foreground = MutedBrush;
            }
            else if (card.LastManual)
            {
                // El chip está en modo software (nuestro slider u otro programa): el
                // ventilador NO lo controla la placa, y decirlo sería mentir.
                card.CurveStateText.Text = I18n.T("Manual: el ventilador no lo controla la placa.");
                card.CurveStateText.Foreground = MutedBrush;
            }
            else
            {
                card.CurveStateText.Text = I18n.T("Sin aplicar: el canal sigue el BIOS");
                card.CurveStateText.Foreground = MutedBrush;
            }
        }
    }

    // Reconstruye las filas de puntos (NumberBox temp → NumberBox duty + borrar).
    private void BuildPointRows(FanCard card)
    {
        card.PointsHost.Children.Clear();
        for (int i = 0; i < card.CurvePoints.Count; i++)
        {
            var index = i;
            var point = card.CurvePoints[i];

            var tempBox = new NumberBox
            {
                Value = point.TemperatureC,
                Minimum = CurveTempMin,
                Maximum = CurveTempMax,
                SmallChange = 1,
                LargeChange = 5,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
                Width = 84,
                FontSize = 14
            };
            CenterNumberText(tempBox);
            var dutyBox = new NumberBox
            {
                Value = point.DutyPercent,
                Minimum = 0,
                Maximum = 100,
                SmallChange = 1,
                LargeChange = 10,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
                Width = 84,
                FontSize = 14
            };
            CenterNumberText(dutyBox);

            tempBox.ValueChanged += (s, e) =>
            {
                // Al borrar el contenido para escribir otro número, el NumberBox
                // reporta NaN: se ignora para no meter un punto inválido en la
                // curva (terminaba en geometría NaN y tiraba el proceso).
                if (double.IsNaN(tempBox.Value)) return;
                // Mientras se teclea, ValueChanged dispara antes de que el NumberBox
                // valide el rango: se clampea acá para que un 150 no llegue a la curva.
                card.CurvePoints[index] = new FanCurvePoint(
                    Math.Clamp(tempBox.Value, CurveTempMin, CurveTempMax),
                    card.CurvePoints[index].DutyPercent);
                RedrawCurve(card);
                SaveCurve(card, card.CurveApplied);
            };
            dutyBox.ValueChanged += (s, e) =>
            {
                if (double.IsNaN(dutyBox.Value)) return;
                card.CurvePoints[index] = new FanCurvePoint(
                    card.CurvePoints[index].TemperatureC,
                    Math.Clamp(dutyBox.Value, 0, 100));
                RedrawCurve(card);
                SaveCurve(card, card.CurveApplied);
            };

            var removeButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 11 },
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2),
                IsEnabled = card.CurvePoints.Count > 2
            };
            removeButton.Click += (s, e) =>
            {
                if (card.CurvePoints.Count <= 2) return;
                card.CurvePoints.RemoveAt(index);
                BuildPointRows(card);
                RedrawCurve(card);
                SaveCurve(card, card.CurveApplied);
            };

            // Una columna elástica a cada lado: el grupo entero (etiquetas, cuadros y
            // borrar) queda centrado en la card, y todas las filas coinciden entre sí.
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var tempLabel = new TextBlock { Text = "Temp", FontSize = 11, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center };
            var arrowLabel = new TextBlock { Text = "→", FontSize = 11, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center };
            var dutyLabel = new TextBlock { Text = I18n.T("Uso"), FontSize = 11, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center };

            Grid.SetColumn(tempLabel, 1);
            Grid.SetColumn(tempBox, 2);
            Grid.SetColumn(arrowLabel, 3);
            Grid.SetColumn(dutyLabel, 4);
            Grid.SetColumn(dutyBox, 5);
            Grid.SetColumn(removeButton, 6);
            row.Children.Add(tempLabel);
            row.Children.Add(tempBox);
            row.Children.Add(arrowLabel);
            row.Children.Add(dutyLabel);
            row.Children.Add(dutyBox);
            row.Children.Add(removeButton);

            card.PointsHost.Children.Add(row);
        }

        // El desplegable de perfiles refleja cuántos puntos tiene la curva.
        SyncProfileSelection(card);
    }

    private void AddCurvePoint(FanCard card)
    {
        var sorted = card.CurvePoints.OrderBy(p => p.TemperatureC).ToList();
        if (sorted.Count == 0) { card.CurvePoints.Add(new FanCurvePoint(60, 50)); }
        else
        {
            // Insertar en el hueco más grande, con el duty interpolado allí.
            double bestTemp = 60, bestGap = -1;
            double bestDuty = 50;
            for (int i = 1; i < sorted.Count; i++)
            {
                var gap = sorted[i].TemperatureC - sorted[i - 1].TemperatureC;
                if (gap > bestGap)
                {
                    bestGap = gap;
                    bestTemp = (sorted[i - 1].TemperatureC + sorted[i].TemperatureC) / 2;
                    var t = gap <= 0 ? 0 : (bestTemp - sorted[i - 1].TemperatureC) / gap;
                    bestDuty = sorted[i - 1].DutyPercent + t * (sorted[i].DutyPercent - sorted[i - 1].DutyPercent);
                }
            }
            if (bestGap < 0)
            {
                // Un solo punto: agregar uno al lado con +15°C / +15%.
                bestTemp = Math.Clamp(sorted[0].TemperatureC + 15, CurveTempMin + 1, CurveTempMax - 1);
                bestDuty = Math.Clamp(sorted[0].DutyPercent + 15, 0, 100);
            }
            card.CurvePoints.Add(new FanCurvePoint(Math.Round(bestTemp), Math.Round(bestDuty)));
        }

        BuildPointRows(card);
        RedrawCurve(card);
        SaveCurve(card, card.CurveApplied);
    }

    // =====================================================================
    // Geometría del gráfico de curva
    // =====================================================================

    /// <summary>
    /// Zona de dibujo del gráfico (entre los márgenes de las etiquetas).
    /// </summary>
    private static (double Left, double Top, double Width, double Height) PlotArea(double w)
        => (CurveGutterLeft,
            CurveGutterTop,
            Math.Max(10, w - CurveGutterLeft - CurveGutterRight),
            CurveCanvasHeight - CurveGutterTop - CurveGutterBottom);

    /// <summary>Temperatura (°C) → posición X dentro del gráfico.</summary>
    private static double CurveX(double tempC, double w)
    {
        var p = PlotArea(w);
        var clamped = Math.Clamp(tempC, CurveTempMin, CurveTempMax);
        return p.Left + (clamped - CurveTempMin) / (CurveTempMax - CurveTempMin) * p.Width;
    }

    /// <summary>Uso (%) → posición Y dentro del gráfico (0% abajo, 100% arriba).</summary>
    private static double CurveY(double dutyPercent)
    {
        var p = PlotArea(0);
        return p.Top + (100 - Math.Clamp(dutyPercent, 0, 100)) / 100 * p.Height;
    }

    /// <summary>Inversa del eje X: posición del mouse → temperatura.</summary>
    private static double CurveTempAt(double x, double w)
    {
        var p = PlotArea(w);
        return CurveTempMin + Math.Clamp((x - p.Left) / p.Width, 0, 1) * (CurveTempMax - CurveTempMin);
    }

    /// <summary>Inversa del eje Y: posición del mouse → uso (%).</summary>
    private static double CurveDutyAt(double y)
    {
        var p = PlotArea(0);
        return (1 - Math.Clamp((y - p.Top) / p.Height, 0, 1)) * 100;
    }

    // =====================================================================
    // Curva con el mouse: arrastrar puntos y agregar con doble clic
    // =====================================================================

    // Static a propósito: lo invoca el handler de cada punto, que se crea dentro
    // de RedrawCurve (también static) y solo toca estado de la card.
    private static void StartCurveDrag(FanCard card, Grid dot, PointerRoutedEventArgs e)
    {
        if (dot.Tag is not FanCurvePoint point) return;

        // El índice se busca por REFERENCIA: los puntos son records y comparan
        // por valor, así que dos puntos distintos podrían coincidir.
        int index = -1;
        for (int i = 0; i < card.CurvePoints.Count; i++)
            if (ReferenceEquals(card.CurvePoints[i], point)) { index = i; break; }
        if (index < 0) return;

        card.DragIndex = index;
        card.CurveCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private static void DragCurvePoint(FanCard card, PointerRoutedEventArgs e)
    {
        if (card.DragIndex < 0 || card.DragIndex >= card.CurvePoints.Count) return;

        var canvas = card.CurveCanvas;
        double w = canvas.ActualWidth;
        if (double.IsNaN(w) || w < 60) return;

        var position = e.GetCurrentPoint(canvas).Position;
        var current = card.CurvePoints[card.DragIndex];

        double temp = Math.Round(CurveTempAt(position.X, w));
        double duty = Math.Round(CurveDutyAt(position.Y));

        // El punto no puede cruzarse con sus vecinos: la curva tiene que seguir
        // siendo una función (una sola velocidad para cada temperatura). Los
        // vecinos se comparan contra la posición ACTUAL del punto arrastrado, que
        // nunca los cruza.
        double lo = CurveTempMin, hi = CurveTempMax;
        foreach (var other in card.CurvePoints)
        {
            if (ReferenceEquals(other, current)) continue;
            if (other.TemperatureC < current.TemperatureC) lo = Math.Max(lo, other.TemperatureC + 1);
            else if (other.TemperatureC > current.TemperatureC) hi = Math.Min(hi, other.TemperatureC - 1);
        }
        if (lo <= hi) temp = Math.Clamp(temp, lo, hi);
        else temp = current.TemperatureC; // sin hueco libre entre vecinos
        duty = Math.Clamp(duty, 0, 100);

        // Si el movimiento no cambió el valor (está imantado a grados enteros), no
        // repintamos: el redibujado se hace en cada pixel que se mueve el mouse.
        if (temp == current.TemperatureC && duty == current.DutyPercent) return;

        card.CurvePoints[card.DragIndex] = new FanCurvePoint(temp, duty);
        RedrawCurve(card);
    }

    private void EndCurveDrag(FanCard card, PointerRoutedEventArgs? e)
    {
        if (card.DragIndex < 0) return;
        card.DragIndex = -1;
        if (e != null) card.CurveCanvas.ReleasePointerCapture(e.Pointer);

        // Se persiste recién al soltar (no en cada movimiento) y las filas de
        // números se sincronizan con lo que quedó en el gráfico.
        BuildPointRows(card);
        RedrawCurve(card);
        SaveCurve(card, card.CurveApplied);
    }

    private void AddCurvePointAt(FanCard card, DoubleTappedRoutedEventArgs e)
    {
        // Doble clic sobre un punto existente no agrega nada (ahí se arrastra).
        if ((e.OriginalSource as FrameworkElement)?.Tag is FanCurvePoint) return;

        var canvas = card.CurveCanvas;
        double w = canvas.ActualWidth;
        if (double.IsNaN(w) || w < 60) return;

        var position = e.GetPosition(canvas);
        double temp = Math.Round(CurveTempAt(position.X, w));
        double duty = Math.Round(CurveDutyAt(position.Y));

        // Una sola velocidad por temperatura: no se apilan puntos en el mismo °C.
        if (card.CurvePoints.Any(p => Math.Abs(p.TemperatureC - temp) < 1)) return;

        card.CurvePoints.Add(new FanCurvePoint(temp, duty));
        BuildPointRows(card);
        RedrawCurve(card);
        SaveCurve(card, card.CurveApplied);
    }

    // Dibuja el gráfico: ejes rotulados (izquierda el uso en %, abajo la
    // temperatura en °C), grilla, área tenue, curva, puntos arrastrables y el
    // marcador de temperatura actual.
    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    private static void RedrawCurve(FanCard card)
    {
        var canvas = card.CurveCanvas;
        canvas.Children.Clear();

        double w = canvas.ActualWidth;
        if (double.IsNaN(w) || w < 60) w = 320;
        var plot = PlotArea(w);

        // Eje Y: uso (%) cada 10, con la etiqueta en el margen izquierdo. Las
        // líneas de 0/50/100 quedan un poco más marcadas que las intermedias
        // para poder ubicarse de un vistazo sobre la grilla densa.
        for (double d = 0; d <= 100; d += CurveDutyStep)
        {
            double y = CurveY(d);
            bool major = Math.Abs(d % 50) < 0.1;
            canvas.Children.Add(new Line
            {
                X1 = plot.Left, Y1 = y, X2 = plot.Left + plot.Width, Y2 = y,
                Stroke = StrokeBrush, StrokeThickness = 1, Opacity = major ? 0.5 : 0.18
            });
            var label = new TextBlock
            {
                Text = $"{d:0}%",
                FontSize = 9,
                Foreground = MutedBrush,
                Width = CurveGutterLeft - 6,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, Math.Clamp(y - 6, 0, CurveCanvasHeight - 13));
            canvas.Children.Add(label);
        }

        // Eje X: temperatura (°C) cada 10, con las líneas cruzando toda el área de
        // trazado (los múltiplos de 20 quedan más marcados).
        for (double t = CurveTempMin; t <= CurveTempMax; t += CurveTempStep)
        {
            double x = CurveX(t, w);
            bool major = Math.Abs((t - CurveTempMin) % 20) < 0.1;
            canvas.Children.Add(new Line
            {
                X1 = x, Y1 = plot.Top, X2 = x, Y2 = plot.Top + plot.Height,
                Stroke = StrokeBrush, StrokeThickness = 1, Opacity = major ? 0.5 : 0.18
            });
        }

        // Marco del área de trazado (izquierda y abajo): cierra el gráfico y
        // separa las etiquetas de los ejes del contenido.
        canvas.Children.Add(new Line
        {
            X1 = plot.Left, Y1 = plot.Top, X2 = plot.Left, Y2 = plot.Top + plot.Height,
            Stroke = StrokeBrush, StrokeThickness = 1, Opacity = 0.55
        });
        canvas.Children.Add(new Line
        {
            X1 = plot.Left, Y1 = plot.Top + plot.Height, X2 = plot.Left + plot.Width, Y2 = plot.Top + plot.Height,
            Stroke = StrokeBrush, StrokeThickness = 1, Opacity = 0.55
        });

        // Etiquetas del eje X: van en su propio recorrido (no en el de la grilla)
        // para que el paso pueda degradarse a 20 °C en cards angostas sin perder
        // las líneas de cada 10. Los extremos se alinean hacia adentro para no
        // montarse sobre el margen del eje Y ni salirse del lienzo.
        double tempLabelSlots = (CurveTempMax - CurveTempMin) / CurveTempStep;
        double tempLabelStep = plot.Width / tempLabelSlots >= CurveTempLabelMinSpacing
            ? CurveTempStep
            : CurveTempStep * 2;
        for (double t = CurveTempMin; t <= CurveTempMax; t += tempLabelStep)
        {
            double x = CurveX(t, w);
            bool isFirst = Math.Abs(t - CurveTempMin) < 0.1;
            bool isLast = Math.Abs(t - CurveTempMax) < 0.1;
            var label = new TextBlock
            {
                Text = $"{t:0}°",
                FontSize = 9,
                Foreground = MutedBrush,
                Width = CurveTempLabelWidth,
                TextAlignment = isFirst ? TextAlignment.Left
                    : isLast ? TextAlignment.Right
                    : TextAlignment.Center
            };
            double left = isFirst ? x : isLast ? x - CurveTempLabelWidth : x - CurveTempLabelWidth / 2;
            Canvas.SetLeft(label, Math.Clamp(left, 0, Math.Max(0, w - CurveTempLabelWidth)));
            Canvas.SetTop(label, plot.Top + plot.Height + 2);
            canvas.Children.Add(label);
        }

        var sorted = card.CurvePoints
            .Where(p => IsFinite(p.TemperatureC) && IsFinite(p.DutyPercent))
            .OrderBy(p => p.TemperatureC).ToList();
        if (sorted.Count == 0) return;

        // Área tenue bajo la curva.
        var area = new Polygon
        {
            Fill = new SolidColorBrush(Feedback.AccentBrush.Color) { Opacity = 0.12 }
        };
        foreach (var p in sorted) area.Points.Add(new Windows.Foundation.Point(CurveX(p.TemperatureC, w), CurveY(p.DutyPercent)));
        area.Points.Add(new Windows.Foundation.Point(CurveX(sorted[^1].TemperatureC, w), CurveY(0)));
        area.Points.Add(new Windows.Foundation.Point(CurveX(sorted[0].TemperatureC, w), CurveY(0)));
        canvas.Children.Add(area);

        // Polilínea de la curva.
        var line = new Polyline { Stroke = Feedback.AccentBrush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        foreach (var p in sorted) line.Points.Add(new Windows.Foundation.Point(CurveX(p.TemperatureC, w), CurveY(p.DutyPercent)));
        canvas.Children.Add(line);

        // Puntos arrastrables: el círculo visible va dentro de un área sensible
        // transparente más grande (24 px), porque un círculo de 9 px es casi
        // imposible de agarrar con el mouse. El Tag guarda el punto para saber
        // cuál se está moviendo.
        foreach (var p in sorted)
        {
            var dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = Feedback.AccentBrush,
                Stroke = CardBrush,
                StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = p
            };
            var hit = new Grid
            {
                Width = CurveDotHitSize,
                Height = CurveDotHitSize,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Tag = p
            };
            hit.Children.Add(dot);
            hit.PointerPressed += (s, e) => StartCurveDrag(card, hit, e);
            Canvas.SetLeft(hit, CurveX(p.TemperatureC, w) - CurveDotHitSize / 2);
            Canvas.SetTop(hit, CurveY(p.DutyPercent) - CurveDotHitSize / 2);
            canvas.Children.Add(hit);
        }

        // Marcador de la temperatura actual (línea vertical + punto hueco en
        // el duty interpolado): muestra en vivo dónde está el canal en la curva.
        if (card.CurrentTempC.HasValue && sorted.Count >= 2)
        {
            var temp = card.CurrentTempC.Value;
            double duty = 0;
            if (temp <= sorted[0].TemperatureC) duty = sorted[0].DutyPercent;
            else if (temp >= sorted[^1].TemperatureC) duty = sorted[^1].DutyPercent;
            else
            {
                for (int i = 1; i < sorted.Count; i++)
                {
                    if (temp <= sorted[i].TemperatureC)
                    {
                        var a = sorted[i - 1];
                        var b = sorted[i];
                        var span = b.TemperatureC - a.TemperatureC;
                        duty = span <= 0 ? b.DutyPercent
                            : a.DutyPercent + (temp - a.TemperatureC) / span * (b.DutyPercent - a.DutyPercent);
                        break;
                    }
                }
            }

            canvas.Children.Add(new Line
            {
                X1 = CurveX(temp, w), Y1 = plot.Top, X2 = CurveX(temp, w), Y2 = plot.Top + plot.Height,
                Stroke = Feedback.WarningBrush, StrokeThickness = 1.5, Opacity = 0.9
            });
            var marker = new Ellipse
            {
                Width = 11,
                Height = 11,
                Fill = Feedback.WarningBrush
            };
            Canvas.SetLeft(marker, CurveX(temp, w) - 5.5);
            Canvas.SetTop(marker, CurveY(duty) - 5.5);
            canvas.Children.Add(marker);
        }

        // Pastilla con los valores del punto que se está arrastrando: mientras
        // movés, el gráfico te dice la temperatura y el uso exactos.
        if (card.DragIndex >= 0 && card.DragIndex < card.CurvePoints.Count)
        {
            var dragged = card.CurvePoints[card.DragIndex];
            var pill = new Border
            {
                Background = CardBrush,
                BorderBrush = Feedback.AccentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Child = new TextBlock
                {
                    Text = $"{dragged.TemperatureC:0}° · {dragged.DutyPercent:0}%",
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Feedback.AccentBrush
                }
            };
            Canvas.SetLeft(pill, Math.Clamp(CurveX(dragged.TemperatureC, w) + 14, 0, Math.Max(0, w - 74)));
            Canvas.SetTop(pill, Math.Clamp(CurveY(dragged.DutyPercent) - 26, 0, CurveCanvasHeight - 24));
            canvas.Children.Add(pill);
        }
    }

    // =====================================================================
    // Acciones globales
    // =====================================================================

    private async void RestoreAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() => _fanService.RestoreAll());

            // "Todos al automático" deja todos los canales en manos de la placa:
            // cada card vuelve al modo BIOS (no queda un perfil marcado que mienta
            // sobre quién controla el ventilador).
            foreach (var c in _cards)
            {
                try { _fanService.SetCurveProfile(c.Id, BiosProfileKey); } catch { }
                try { _fanService.ClearSensorLost(c.Id); } catch { }
                c.CurveApplied = false;
                c.BiosMode = true;
                c.LastSensorLost = false;
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"FanControlPage: RestoreAll falló: {ex.Message}");
        }
    }

    // =====================================================================
    // Instalación de PawnIO
    // =====================================================================

    private async void InstallPawnButton_Click(object sender, RoutedEventArgs e)
    {
        InstallPawnButton.IsEnabled = false;
        PawnInstallRing.Visibility = Visibility.Visible;
        PawnInstallRing.IsActive = true;
        StatusText.Text = I18n.T("Descargando e instalando PawnIO…");
        StatusText.Foreground = Feedback.AccentBrush;

        try
        {
            var result = await Task.Run(() => _fanService.InstallPawnIOSilentAsync());

            if (result.Success)
            {
                StatusText.Text = "✓ " + result.Message;
                StatusText.Foreground = Feedback.SuccessBrush;
                // Re-escanear hardware con el driver ya presente.
                _fanService.Reinitialize();
                await RefreshAsync();
                StartPolling();
            }
            else
            {
                StatusText.Text = "✗ " + result.Message;
                StatusText.Foreground = Feedback.ErrorBrush;
            }
        }
        finally
        {
            PawnInstallRing.IsActive = false;
            PawnInstallRing.Visibility = Visibility.Collapsed;
            InstallPawnButton.IsEnabled = true;
        }
    }

    // =====================================================================
    // Sondeo
    // =====================================================================

    private void StartPolling()
    {
        if (_polling) return;
        _polling = true;
        _pollTimer = _dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(2);
        _pollTimer.Tick += async (s, e) =>
        {
            _pollTimer?.Stop();
            await RefreshAsync();
            if (_polling) _pollTimer?.Start();
        };
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        _polling = false;
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void RebuildAll()
    {
        // Al cambiar el idioma: recrear cards (los textos van por I18n.T).
        FansList.Children.Clear();
        _cards.Clear();
        _ = RefreshAsync();
    }
}
