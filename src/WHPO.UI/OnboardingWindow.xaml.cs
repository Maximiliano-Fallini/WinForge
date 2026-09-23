using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WHPO.Core;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Services;

namespace WHPO_UI;

/// <summary>
/// Flujo de onboarding de primera ejecución: el asistente REAL, que al terminar
/// persiste el tema elegido y marca el onboarding como completado. La vista
/// previa de desarrollo (OnboardingSimulatorWindow) hereda esta misma UI pero
/// sobreescribe los puntos donde se escribe configuración.
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int PhaseCount = 4;

    /// <summary>Índice de la fase de idioma (la PRIMERA: el asistente arranca eligiendo idioma).</summary>
    private const int LanguagePhase = 0;

    /// <summary>
    /// Índice de la fase de ANTIVIRUS: muestra la salud de las exclusiones de Windows
    /// Defender para WinForge (carpeta de la app, carpeta de datos y proceso) y las
    /// RE-VERIFICA cada 500 ms mientras la fase está en pantalla. Va penúltima, antes del
    /// cierre: el usuario ve el estado real de su equipo y después termina el asistente.
    /// </summary>
    private const int HealthPhase = 2;

    /// <summary>Cada cuánto se re-verifica la salud mientras esta fase está en pantalla.</summary>
    private const int HealthPollMilliseconds = 500;

    private const string FirstRunKey = "onboarding.complete";

    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly ILoggingService _loggingService;

    private int _phase;
    private AppTheme _selectedTheme;
    private UIElement[] _panels = Array.Empty<UIElement>();
    private readonly List<Border> _progressDots = new();
    private readonly List<Border> _progressLines = new();
    private readonly List<TextBlock> _progressLabels = new();

    /// <summary>Filas de la fase de antivirus (una por exclusión esperada) con sus
    /// referencias: en cada verificación se ACTUALIZAN sus textos e íconos, no se rehacen
    /// (recrear elementos cada medio segundo haría parpadear la fase).</summary>
    private readonly List<HealthRow> _healthRows = new();

    /// <summary>
    /// Una fila del panel de antivirus: se actualiza en cada verificación (no se recrea).
    /// Los dos botones son por fila y trabajan sobre la MISMA ruta que muestra el estado,
    /// no sobre una ruta de manual.
    /// </summary>
    private sealed record HealthRow(
        FontIcon Icon,
        TextBlock Label,
        TextBlock State,
        Button Open,
        Button Exclude,
        DefenderExclusionTarget Target);

    // Rutas reales de esta copia: las usan la verificación y los botones de cada fila.
    private string _healthExePath = "";
    private string _healthDataFolder = "";

    /// <summary>Re-verificación periódica de las exclusiones: solo vive mientras la fase
    /// de antivirus está en pantalla (ver StartHealthChecks / StopHealthChecks).</summary>
    private DispatcherQueueTimer? _healthTimer;

    /// <summary>Una sola lectura a la vez: si la anterior no terminó, el tick se saltea.</summary>
    private bool _healthInFlight;

    /// <summary>Se incrementa al salir de la fase (y en cada verificación): un resultado que
    /// llegue tarde, con la fase ya cerrada, se descarta en vez de pintar una fase oculta.</summary>
    private int _healthGeneration;

    /// <summary>Último veredicto confirmado de la fase de antivirus. null = todavía no
    /// llegó ninguna verificación (por ejemplo, recién entrando a la fase): mientras
    /// tanto el botón queda deshabilitado.</summary>
    private DefenderExclusionState? _healthOverall;

    /// <summary>Título de la ventana según el tipo de onboarding (real o simulador).</summary>
    protected virtual string WindowTitleText => I18n.T("Bienvenido a WinForge");

    public OnboardingWindow()
    {
        InitializeComponent();

        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _themeService = App.Services.GetRequiredService<IThemeService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();

        // Idioma: el onboarding puede abrirse ANTES que MainWindow (que es quien
        // inicializa I18n con el idioma guardado), así que resuelve su idioma acá.
        ApplyOnboardingLanguage();

        // Traducir los textos estáticos del XAML al idioma activo.
        I18n.ApplyToVisualTree(RootGrid);
        I18n.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            I18n.LanguageChanged -= OnLanguageChanged;
            // El timer de la fase de antivirus no debe quedar latiendo con la ventana cerrada.
            StopHealthChecks();
        };

        _panels = new UIElement[] { PhaseLanguage, PhaseTheme, PhaseHealth, PhaseThanks };

        BuildProgressDots();
        ApplyTexts();
        WireThemeCardsInteraction();
        SetSelectedTheme(_themeService.CurrentTheme);

        _phase = 0;
        UpdatePhaseUi(animateIn: false);

        // Ventana de tamaño fijo: el asistente no se puede achicar, agrandar ni
        // maximizar (arrastrar bordes o doble clic en la barra de título).
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        // Centrado: una ventana nueva se abre "donde caiga" (Windows las cascada). El
        // asistente tiene que aparecer centrado en el monitor donde se abre, tanto el real
        // como el simulador de la vista previa. Solo en la PRIMERA activación: si el
        // usuario mueve la ventana y vuelve a ella, no se la corre de lugar.
        Activated += Onboarding_Activated;
        Closed += (_, _) => Activated -= Onboarding_Activated;

        Title = WindowTitleText;
    }

    private bool _centered;

    private void Onboarding_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (_centered) return;
        _centered = true;

        // El tamaño real de la ventana recién queda resuelto DESPUÉS de la primera pasada
        // de layout: centrar en el próximo turno del dispatcher evita usar un tamaño
        // provisorio y que el centro salga corrido.
        if (!DispatcherQueue.TryEnqueue(CenterOnScreen))
            CenterOnScreen();
    }

    /// <summary>
    /// Centra la ventana en el ÁREA DE TRABAJO del monitor que le corresponde (no en la
    /// pantalla completa: así no queda debajo de la barra de tareas).
    /// </summary>
    private void CenterOnScreen()
    {
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (area == null) return;

            var wa = area.WorkArea;
            var size = AppWindow.Size;
            int x = wa.X + Math.Max(0, (wa.Width - size.Width) / 2);
            int y = wa.Y + Math.Max(0, (wa.Height - size.Height) / 2);
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Onboarding: centrar la ventana: {ex.Message}");
        }
    }

    private void BuildProgressDots()
    {
        for (int i = 0; i < PhaseCount; i++)
        {
            if (i > 0)
            {
                // Línea que conecta los pasos. Queda alineada con el centro del punto
                // (el punto mide 8 y la línea 2: 3 px de margen superior).
                var line = new Border
                {
                    Width = 28,
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    Margin = new Thickness(8, 3, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = ThemeBrushes.Get("MutedBrush")
                };
                _progressLines.Add(line);
                ProgressDots.Children.Add(line);
            }

            // Cada paso es una columna: el punto (pastilla ancha si es el actual) y su
            // rótulo debajo. El rótulo es lo que le dice al usuario dónde está: tres
            // puntos solos no explican nada.
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = ThemeBrushes.Get("MutedBrush")
            };
            var label = new TextBlock
            {
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush")
            };

            var step = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
            step.Children.Add(dot);
            step.Children.Add(label);

            _progressDots.Add(dot);
            _progressLabels.Add(label);
            ProgressDots.Children.Add(step);
        }
    }

    // ----- Cambio de fase -----

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        // Cerrojo de la fase de antivirus: sin la marca de verificación de las DOS filas
        // (ejecutable y carpeta de datos) no se continúa. Defender puede borrar o bloquear
        // la app al primer escaneo, y resolver eso es justamente lo que esta fase existe
        // para hacer: dejar pasar al usuario con la exclusión sin confirmar sería
        // saltarse el paso.
        if (_phase == HealthPhase && !HealthGateOpen())
        {
            UpdateHealthGate();   // re-evalúa por si un resultado acabó de llegar
            return;
        }
        if (_phase == PhaseCount - 1)
        {
            Finish();
            return;
        }
        _phase++;
        UpdatePhaseUi(animateIn: true);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_phase > 0)
        {
            _phase--;
            UpdatePhaseUi(animateIn: true);
        }
    }

    private void ThemeOption_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ThemeDarkCard)) _selectedTheme = AppTheme.Dark;
        else if (ReferenceEquals(sender, ThemeLightCard)) _selectedTheme = AppTheme.Light;
        else _selectedTheme = AppTheme.SystemDefault;

        // La elección se aplica visualmente en esta ventana y se persiste al terminar
        // (Finish). El simulador sobreescribe Finish y no guarda nada.
        SetSelectedTheme(_selectedTheme);
    }

    private async void StarButton_Click(object sender, RoutedEventArgs e)
    {
        // El enlace sale de ProjectEndpoints: es la única fuente de las URLs del repositorio.
        string url = ProjectEndpoints.Stargazers;
        if (UrlOpener.Open(url, _loggingService)) return;

        // Sin navegador el clic no puede quedar sin respuesta: se muestra el enlace para
        // copiarlo a mano (seleccionable) en vez de no hacer nada.
        _loggingService.LogWarning($"Onboarding: no se pudo abrir el navegador para la estrella ({url}).");
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = I18n.T("Dejar una estrella en GitHub"),
                Content = new TextBlock
                {
                    Text = url,
                    FontSize = 13,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = I18n.T("Cerrar"),
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Onboarding: no se pudo mostrar el enlace de la estrella: {ex.Message}");
        }
    }

    /// <summary>
    /// Finaliza el onboarding: persiste el tema elegido y marca el onboarding como
    /// completado. La vista previa de desarrollo (OnboardingSimulatorWindow) la
    /// sobreescribe para cerrar sin escribir nada.
    /// </summary>
    protected virtual void Finish()
    {
        try
        {
            _themeService.SetTheme(_selectedTheme);
            _settingsService.Set(FirstRunKey, true);
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Onboarding: finalizar: {ex.Message}");
        }

        Close();
    }

    // ----- Estado por fase -----

    private void UpdatePhaseUi(bool animateIn)
    {
        for (int i = 0; i < _panels.Length; i++)
            _panels[i].Visibility = i == _phase ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _phase > 0 ? Visibility.Visible : Visibility.Collapsed;

        // En la última fase el botón finaliza la vista previa.
        NextButton.Visibility = Visibility.Visible;
        NextButton.Content = _phase == PhaseCount - 1 ? I18n.T("Terminar") : I18n.T("Continuar");

        // Al entrar a la fase de idioma se arman las filas (locales al toque) y, si el
        // catálogo remoto todavía no llegó, se pide en segundo plano: cuando llegue, la
        // lista se re-arma con los descargables. Si la red falla, se reintenta cada vez
        // que se vuelve a entrar a la fase.
        if (_phase == LanguagePhase)
        {
            if (!LanguagePacks.CatalogLoaded && !_languageCatalogRequested)
            {
                _languageCatalogRequested = true;
                _ = LoadLanguageCatalogAsync();
            }
            BuildLanguagePhase();
        }

        // Salud del antivirus: la verificación periódica vive SOLO en esta fase. Al salir
        // se detiene (no tiene sentido leer el registro cada 500 ms mientras el usuario
        // está en otra fase) y al volver se reanuda con una verificación inmediata.
        if (_phase == HealthPhase) StartHealthChecks();
        else StopHealthChecks();

        // El botón depende del veredicto: en la fase de antivirus se habilita solo con la
        // marca de verificación de las dos filas; en las demás fases, siempre.
        UpdateHealthGate();

        UpdateProgressDots();

        if (animateIn)
        {
            AnimateIn(_panels[_phase]);
        }
    }

    private void UpdateProgressDots()
    {
        var accent = ThemeBrushes.Get("AccentBrush", _selectedTheme);
        var muted = ThemeBrushes.Get("MutedBrush", _selectedTheme);
        var secondary = ThemeBrushes.Get("SecondaryTextBrush", _selectedTheme);

        for (int i = 0; i < _progressDots.Count; i++)
        {
            bool current = i == _phase;
            bool done = i < _phase;

            // El paso actual es una PASTILLA ANCHA: cambia de ancho, nunca de alto, así
            // el riel no salta al avanzar. Los completados quedan en acento tenue.
            _progressDots[i].Width = current ? 22 : 8;
            _progressDots[i].Height = 8;
            _progressDots[i].CornerRadius = new CornerRadius(4);
            _progressDots[i].Background = current || done ? accent : muted;
            _progressDots[i].Opacity = done ? 0.55 : 1;

            _progressLabels[i].Foreground = current ? accent : secondary;
            _progressLabels[i].FontWeight = current
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            _progressLabels[i].Opacity = current || done ? 1 : 0.6;
        }
        for (int i = 0; i < _progressLines.Count; i++)
        {
            // La línea entre el paso i y i+1 se completa cuando se pasó el paso i+1.
            _progressLines[i].Background = (i + 1) <= _phase ? accent : muted;
        }
    }

    private void AnimateIn(UIElement element)
    {
        // La traslación se anima apuntando al TRANSFORM, no a la ruta de propiedad
        // "RenderTransform.(TranslateTransform.Y)": esa ruta solo resuelve cuando el elemento
        // ya fue medido, y al entrar a una fase que estaba oculta —Continuar y Atrás, justo
        // las dos que se usan en el onboarding— Begin() tiraba "Cannot resolve TargetProperty
        // RenderTransform.(TranslateTransform.Y)" y la app se cerraba de golpe. Animando el
        // objeto (TranslateTransform.Y) no hay ruta que resolver: el transform existe siempre
        // (se crea si el panel no lo declara en el XAML).
        TranslateTransform? slide = null;
        if (element is FrameworkElement fe)
        {
            slide = fe.RenderTransform as TranslateTransform;
            if (slide is null && fe.RenderTransform is null)
            {
                slide = new TranslateTransform();
                fe.RenderTransform = slide;
            }
            if (slide is not null) slide.Y = 16;
        }

        var sb = new Storyboard();

        var opacity = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(280) };
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(opacity, element);
        sb.Children.Add(opacity);

        if (slide is not null)
        {
            var translate = new DoubleAnimation { From = 16, To = 0, Duration = TimeSpan.FromMilliseconds(300) };
            Storyboard.SetTargetProperty(translate, "Y");
            Storyboard.SetTarget(translate, slide);
            sb.Children.Add(translate);
        }

        // Última red: si el motor de animación igual falla, la fase queda visible y sin
        // deslizamiento —una animación no puede cerrar la app.
        try
        {
            sb.Begin();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Onboarding: animación de entrada: {ex.Message}");
            if (element is FrameworkElement target) target.Opacity = 1;
            if (slide is not null) slide.Y = 0;
        }
    }

    // ----- Tema -----

    private void SetSelectedTheme(AppTheme theme)
    {
        _selectedTheme = theme;
        UpdateOnboardingTheme(theme);
        UpdateThemeCards();
        UpdateProgressDots();
    }

    private void UpdateThemeCards()
    {
        // Fondos y textos de las cards son LITERALES en el XAML (no reactivos al
        // tema). Acá solo se marca la selección (borde + badge de check) usando,
        // para el estado NO seleccionado, el borde fijo propio de cada card, para
        // que el toggle no les cambie el color al alternar el tema.
        // Rosa/Blanco y Negro/Azul no tienen card propia en el asistente: se
        // muestran como su tema BASE (claro/oscuro). La paleta propia se elige
        // después desde Configuración → Tema de la aplicación.
        bool darkSel = _selectedTheme is AppTheme.Dark or AppTheme.BlueBlack;
        bool lightSel = _selectedTheme is AppTheme.Light or AppTheme.PinkLight;
        bool sysSel = _selectedTheme == AppTheme.SystemDefault;

        SetThemeCard(ThemeDarkCard, darkSel, 0xFF3F3F3F);
        SetThemeCard(ThemeLightCard, lightSel, 0xFFC9C9C9);
        SetThemeCard(ThemeSystemCard, sysSel, 0xFFC9C9C9);

        // Badge de check (✓) sobre el preview de la card elegida.
        if (ThemeDarkCheck != null) ThemeDarkCheck.Visibility = darkSel ? Visibility.Visible : Visibility.Collapsed;
        if (ThemeLightCheck != null) ThemeLightCheck.Visibility = lightSel ? Visibility.Visible : Visibility.Collapsed;
        if (ThemeSystemCheck != null) ThemeSystemCheck.Visibility = sysSel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetThemeCard(Border card, bool selected, uint unselectedBorderArgb)
    {
        if (card == null) return;
        card.BorderBrush = selected
            ? ThemeBrushes.Get("AccentBrush", _selectedTheme)
            : new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)(unselectedBorderArgb >> 24),
                (byte)(unselectedBorderArgb >> 16),
                (byte)(unselectedBorderArgb >> 8),
                (byte)unselectedBorderArgb));
        card.BorderThickness = new Thickness(selected ? 2 : 1);
    }

    /// <summary>
    /// Cablea hover/cursor de las cards de tema: cursor de mano, leve elevación
    /// al pasar el mouse y realce del borde cuando la card no está seleccionada.
    /// </summary>
    private void WireThemeCardsInteraction()
    {
        foreach (var card in new[] { ThemeDarkCard, ThemeLightCard, ThemeSystemCard })
        {
            if (card == null) continue;
            card.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            card.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = 1, ScaleY = 1 };
            card.PointerEntered += ThemeCard_PointerEntered;
            card.PointerExited += ThemeCard_PointerExited;
        }
    }

    private void ThemeCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Border card) return;
        if (card.RenderTransform is Microsoft.UI.Xaml.Media.ScaleTransform st)
        {
            st.ScaleX = 1.03;
            st.ScaleY = 1.03;
        }

        // Realce del borde solo si no está seleccionada (la seleccionada usa el acento).
        bool selected = (card == ThemeDarkCard && _selectedTheme is AppTheme.Dark or AppTheme.BlueBlack)
                     || (card == ThemeLightCard && _selectedTheme is AppTheme.Light or AppTheme.PinkLight)
                     || (card == ThemeSystemCard && _selectedTheme == AppTheme.SystemDefault);
        if (!selected)
        {
            card.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(
                255, card == ThemeDarkCard ? (byte)0x6A : (byte)0x8A, (byte)0x8A, (byte)0x8A));
        }
    }

    private void ThemeCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Border card) return;
        if (card.RenderTransform is Microsoft.UI.Xaml.Media.ScaleTransform st)
        {
            st.ScaleX = 1;
            st.ScaleY = 1;
        }
        UpdateThemeCards(); // restaura el borde según la selección actual
    }

    private void UpdateOnboardingTheme(AppTheme theme)
    {
        // En el onboarding, el tema elegido se previsualiza SOLO en la card (la
        // "ventana"): el fondo de pantalla (RootGrid con el banner + scrim) NO
        // cambia al tocar los temas, y los botones de tema tienen colores
        // literales fijos (XAML). La barra de título de la ventana (donde están
        // la X y el minimizar) SÍ reacciona al tema, como en la app real.
        // El simulador hereda este mismo comportamiento.
        var effective = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            AppTheme.BlueBlack => ElementTheme.Dark,   // base oscura
            AppTheme.PinkLight => ElementTheme.Light,  // base clara
            _ => RootGrid.ActualTheme // Sistema: lo que la ventana resuelve hoy
        };

        ContentCard.RequestedTheme = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            AppTheme.BlueBlack => ElementTheme.Dark,
            AppTheme.PinkLight => ElementTheme.Light,
            _ => ElementTheme.Default
        };

        ApplyTitleBarTheme(effective);
    }

    /// <summary>
    /// Colorea la barra de título (franja de la X / minimizar) según el tema
    /// efectivo elegido en el onboarding: negra en oscuro, blanca en claro y
    /// según el sistema en "Sistema".
    /// </summary>
    private void ApplyTitleBarTheme(ElementTheme effective)
    {
        try
        {
            bool dark = effective == ElementTheme.Dark;
            var bg = dark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32)   // #202020
                : Windows.UI.Color.FromArgb(255, 255, 255, 255);
            var fg = dark
                ? Microsoft.UI.Colors.White
                : Microsoft.UI.Colors.Black;
            var hoverBg = dark
                ? Windows.UI.Color.FromArgb(255, 58, 58, 58)
                : Windows.UI.Color.FromArgb(255, 229, 229, 229);

            var tb = AppWindow.TitleBar;
            tb.BackgroundColor = bg;
            tb.ForegroundColor = fg;
            tb.ButtonBackgroundColor = bg;
            tb.ButtonForegroundColor = fg;
            tb.ButtonHoverBackgroundColor = hoverBg;
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonInactiveBackgroundColor = bg;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Onboarding: aplicar tema a la barra de título: {ex.Message}");
        }
    }

    // ----- Idiomas (fase 1) -----

    private bool _languageListBuilt;
    private bool _languageCatalogRequested;
    private bool _buildingLanguageList;

    /// <summary>Firma de la lista armada: qué idiomas hay y cuáles se pueden descargar.</summary>
    private string _languageSignature = string.Empty;

    /// <summary>
    /// Ítems ya construidos del desplegable (código, su nombre y su marca ✓). Sirve para
    /// re-marcar el idioma activo SIN tocar la colección del control.
    /// </summary>
    private readonly List<(ComboBoxItem Item, string Code, TextBlock Name, FontIcon Check)> _languageItems = new();

    /// <summary>Ítem del desplegable: el idioma y, si todavía no está instalado, el pack a bajar.</summary>
    private sealed record LanguageChoice(string Code, LanguageCatalogEntry? Entry);

    /// <summary>
    /// Pide el catálogo de idiomas en segundo plano y re-arma el desplegable cuando llega
    /// (así los descargables aparecen sin bloquear la fase). Si falla la red, deja el flag
    /// en falso para reintentar en la próxima entrada a la fase.
    /// </summary>
    private async Task LoadLanguageCatalogAsync()
    {
        try
        {
            await LanguagePacks.GetCatalogAsync(_loggingService);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Onboarding: catálogo de idiomas: {ex.Message}");
        }
        if (!LanguagePacks.CatalogLoaded)
            _languageCatalogRequested = false;
        // Si el catálogo trajo descargables, la firma de la lista cambió y BuildLanguagePhase
        // la rehace sola: no hace falta forzar el re-armado (que es lo que toca la colección).
        BuildLanguagePhase();
    }

    /// <summary>
    /// Desplegable de idioma: los embebidos y los packs instalados se activan al elegirlos;
    /// los descargables se distinguen por la marca de descarga y se bajan del repositorio
    /// al elegirlos. Queda SIEMPRE con el idioma activo seleccionado: nunca arranca vacío.
    /// </summary>
    private void BuildLanguagePhase()
    {
        var downloadable = LanguagePacks.Downloadable();
        string signature = string.Join("|", I18n.Languages) + "#" + string.Join("|", downloadable.Select(e => e.Code));

        if (_languageListBuilt && LanguageComboBox.Items.Count > 0 && signature == _languageSignature)
        {
            // La LISTA no cambió (mismos idiomas): el cambio de idioma solo mueve la marca ✓.
            // Se re-marcan los ítems que YA están y no se toca la colección del desplegable.
            //
            // Por qué importa: el cambio de idioma llega desde el SelectionChanged de ESTE
            // control —y también entra a la fase—, así que rehacerle la colección mientras el
            // control cierra su menú y recicla los ítems terminaba en "Element is already the
            // child of another element" y la ventana se caía. Los nombres son los endónimos,
            // así que en un cambio de idioma no hay nada que reconstruir: solo la ✓ y el peso
            // de la fuente del idioma activo.
            _loggingService.LogInfo($"Onboarding: desplegable de idioma → solo marcas ({_languageItems.Count} ítems)");
            RefreshLanguageMarks();
            return;
        }

        _languageListBuilt = true;
        _languageSignature = signature;

        var items = new List<ComboBoxItem>();
        _languageItems.Clear();
        foreach (var code in I18n.Languages)
            items.Add(BuildLanguageItem(new LanguageChoice(code, null)));
        foreach (var entry in downloadable)
            items.Add(BuildLanguageItem(new LanguageChoice(entry.Code, entry)));

        // El re-armado completo (que SÍ reemplaza la colección) se hace en el próximo turno
        // del dispatcher: si viene del SelectionChanged del propio desplegable, el control
        // todavía está cerrando su menú.
        _loggingService.LogInfo($"Onboarding: desplegable de idioma → re-armado completo ({items.Count} ítems)");
        if (!DispatcherQueue.TryEnqueue(() => SwapLanguageItems(items)))
            SwapLanguageItems(items);
    }

    /// <summary>
    /// Reemplaza la lista del desplegable y vuelve a marcar el idioma activo. Si el control
    /// rechaza el recambio, deja el flag en falso para que el próximo ingreso a la fase lo
    /// rearme: la ventana nunca se cierra por esto.
    /// </summary>
    private void SwapLanguageItems(List<ComboBoxItem> items)
    {
        try
        {
            LanguageComboBox.Items.Clear();
            foreach (var item in items)
                LanguageComboBox.Items.Add(item);
            _loggingService.LogInfo($"Onboarding: desplegable de idioma → colección re-armada ({items.Count} ítems)");
            RefreshLanguageMarks();
        }
        catch (Exception ex)
        {
            _languageListBuilt = false;
            _loggingService.LogWarning($"Onboarding: re-armar el desplegable de idioma: {ex.Message}");
        }
    }

    /// <summary>
    /// Marca el idioma activo en los ítems ya construidos (✓ visible y nombre en semibold) y
    /// deja la selección del control apuntando a él. NO modifica la colección de ítems: es lo
    /// que hace que un cambio de idioma no pueda romper el control del que salió.
    /// </summary>
    private void RefreshLanguageMarks()
    {
        foreach (var (_, code, name, check) in _languageItems)
        {
            bool active = string.Equals(code, I18n.Current, StringComparison.OrdinalIgnoreCase);
            name.FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            check.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        // La selección sigue al idioma activo, pero solo se toca si de verdad cambió: un
        // SelectedItem redundante dispara otro SelectionChanged al pedo.
        if (LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: LanguageChoice current }
            || !string.Equals(current.Code, I18n.Current, StringComparison.OrdinalIgnoreCase))
        {
            SelectLanguage(I18n.Current);
        }
    }

    /// <summary>
    /// Ítem del desplegable: bandera + nombre (el endónimo, igual en todos los idiomas) y,
    /// a la derecha, ✓ si es el activo o la marca de descarga si todavía no está instalado.
    /// </summary>
    private ComboBoxItem BuildLanguageItem(LanguageChoice choice)
    {
        bool active = string.Equals(choice.Code, I18n.Current, StringComparison.OrdinalIgnoreCase);

        var grid = new Grid { ColumnSpacing = 8, MinWidth = 210 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var flag = Flags.GetImage(choice.Code);
        if (flag is not null) grid.Children.Add(flag);

        var name = new TextBlock
        {
            Text = LanguagePacks.DisplayName(choice.Code),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            // El peso lo pone RefreshLanguageMarks (según el idioma activo del momento).
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        // Columna de acción: la marca de descarga (solo si hay pack para bajar) y la ✓ del
        // idioma activo. La ✓ va SIEMPRE en el árbol —oculta o no— para poder mover la marca
        // sin rehacer la colección del desplegable (que es lo que rompía al cambiar de idioma).
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (choice.Entry is not null)
        {
            actions.Children.Add(new FontIcon
            {
                Glyph = LanguageRowUi.DownloadGlyph,
                FontFamily = LanguageRowUi.SymbolFontFamily(),
                FontSize = 12,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        var check = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 12,
            Foreground = ThemeBrushes.Get("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = active ? Visibility.Visible : Visibility.Collapsed
        };
        actions.Children.Add(check);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        var item = new ComboBoxItem { Content = grid, Tag = choice };
        _languageItems.Add((item, choice.Code, name, check));
        return item;
    }

    /// <summary>Marca como seleccionado el ítem de un idioma sin disparar la descarga.</summary>
    private void SelectLanguage(string code)
    {
        _buildingLanguageList = true;
        try
        {
            foreach (var item in LanguageComboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is LanguageChoice choice
                    && string.Equals(choice.Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageComboBox.SelectedItem = item;
                    return;
                }
            }
            if (LanguageComboBox.Items.Count > 0)
                LanguageComboBox.SelectedIndex = 0;
        }
        finally
        {
            _buildingLanguageList = false;
        }
    }

    /// <summary>
    /// Elección de un idioma: los disponibles se activan al toque y los descargables se
    /// bajan y quedan activos al terminar.
    /// </summary>
    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_buildingLanguageList) return;
        if (LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: LanguageChoice choice }) return;

        HideLanguageStatus();

        if (choice.Entry is null)
        {
            if (string.Equals(choice.Code, I18n.Current, StringComparison.OrdinalIgnoreCase)) return;

            // Activar el idioma en el PRÓXIMO turno del dispatcher: el cambio re-arma el
            // desplegable (OnLanguageChanged), y limpiar sus ítems acá adentro —dentro de su
            // propio SelectionChanged— sería modificar la colección que el control está
            // recorriendo en ese momento.
            string code = choice.Code;
            if (!DispatcherQueue.TryEnqueue(() => ActivateLanguage(code)))
                ActivateLanguage(code);
            return;
        }

        await DownloadAndActivateAsync(choice);
    }

    /// <summary>
    /// Baja el pack del idioma elegido y lo activa. Mientras baja, el desplegable queda
    /// deshabilitado y el anillo de la derecha gira: no hay dos descargas a la vez. Si
    /// falla, avisa y devuelve la selección al idioma activo (el desplegable nunca queda
    /// apuntando a un idioma que no se puede usar).
    /// </summary>
    private async Task DownloadAndActivateAsync(LanguageChoice choice)
    {
        var entry = choice.Entry!;
        string name = LanguagePacks.DisplayName(entry.Code);

        LanguageComboBox.IsEnabled = false;
        LanguageDownloadRing.IsActive = true;
        LanguageDownloadRing.Opacity = 1;
        ShowLanguageStatus(string.Format(I18n.T("Descargando {0}..."), name), error: false);

        try
        {
            var outcome = await LanguagePacks.DownloadAsync(entry);
            if (outcome.Success)
            {
                ActivateLanguage(entry.Code);
                // El idioma recién instalado entra a I18n.Languages, así que la firma de la
                // lista cambió y BuildLanguagePhase la rehace con el pack ya instalado (con su
                // ✓ en vez de la marca de descarga).
                BuildLanguagePhase();
                ShowLanguageStatus(
                    string.Format(I18n.T("Descarga completa. {0} ya está activo."), name),
                    error: false);
                return;
            }
            ShowLanguageStatus(I18n.T("No se pudo descargar el idioma") + ": " + (outcome.Error ?? entry.Code), error: true);
        }
        catch (Exception ex)
        {
            ShowLanguageStatus(I18n.T("No se pudo descargar el idioma") + ": " + ex.Message, error: true);
        }
        finally
        {
            LanguageDownloadRing.IsActive = false;
            LanguageDownloadRing.Opacity = 0;
            LanguageComboBox.IsEnabled = true;
        }

        SelectLanguage(I18n.Current);
    }

    /// <summary>Activa un idioma disponible. El onboarding real persiste la elección;
    /// el simulador sobreescribe esto para solo previsualizar.</summary>
    protected virtual void ActivateLanguage(string code)
        => I18n.SetLanguage(code, _settingsService);

    private void ShowLanguageStatus(string text, bool error)
    {
        LanguageStatusText.Text = text;
        LanguageStatusText.Foreground = error
            ? ThemeBrushes.Get("SecondaryTextBrush")
            : ThemeBrushes.Get("AccentBrush");
        LanguageStatusText.Visibility = Visibility.Visible;
    }

    private void HideLanguageStatus()
    {
        LanguageStatusText.Text = string.Empty;
        LanguageStatusText.Visibility = Visibility.Collapsed;
    }

    // ----- Idioma (resolución) -----

    /// <summary>
    /// Resuelve el idioma del onboarding. En el primer arranque (sin preferencia
    /// guardada) detecta el idioma del sistema y lo aplica: si el sistema usa un
    /// idioma no disponible entre los soportados, cae a inglés completo (en-US).
    /// Con preferencia ya guardada (simulador o re-apertura), respeta la elección.
    /// </summary>
    protected virtual void ApplyOnboardingLanguage()
    {
        try
        {
            if (!_settingsService.Contains("app.language"))
            {
                // Primer arranque: idioma del sistema; si no está disponible, todo en inglés.
                var lang = I18n.DetectSystemLanguage() ?? I18n.DefaultLanguage;
                I18n.SetLanguage(lang, _settingsService);
                _loggingService.LogInfo($"Onboarding: idioma del sistema aplicado ({lang}).");
                return;
            }

            // Ya hay preferencia guardada: respetarla. SetLanguage es no-op si ya está
            // activa (caso normal, MainWindow la inicializó) y la activa si el onboarding
            // corre antes que MainWindow en una sesión posterior.
            var saved = _settingsService.Get("app.language", I18n.DefaultLanguage);
            I18n.SetLanguage(saved, _settingsService);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Onboarding: detectar idioma del sistema: {ex.Message}");
        }
    }

    // ----- Antivirus (salud de las exclusiones de Defender) -----

    /// <summary>
    /// Arranca (o reanuda) la verificación en vivo: una lectura inmediata y después un
    /// latido cada 500 ms. Es idempotente: volver a entrar a la fase no acumula timers.
    /// </summary>
    private void StartHealthChecks()
    {
        CaptureHealthPaths();
        BuildHealthRows();
        _ = RunHealthCheckAsync();
        _healthTimer ??= CreateHealthTimer();
        _healthTimer.Start();
    }

    private void StopHealthChecks()
    {
        _healthTimer?.Stop();
        _healthGeneration++;   // descarta un resultado que esté en vuelo
    }

    private DispatcherQueueTimer CreateHealthTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(HealthPollMilliseconds);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => _ = RunHealthCheckAsync();
        return timer;
    }

    /// <summary>
    /// Verifica y pinta el estado de las tres exclusiones (carpeta de la app, carpeta de
    /// datos y proceso). La lectura sale del registro de Defender: es barata, pero va en
    /// segundo plano porque la clave puede estar protegida (protección contra alteraciones)
    /// y no queremos que el hilo de la UI se quede esperando en cada latido.
    /// </summary>
    private async Task RunHealthCheckAsync()
    {
        if (_healthInFlight) return;
        _healthInFlight = true;
        int generation = ++_healthGeneration;
        try
        {
            // El ejecutable y el nombre del proceso se toman del proceso REAL: en la build
            // instalada coinciden con lo que excluye el instalador, y en la de desarrollo
            // dicen la verdad de esa copia (que puede no estar excluida).
            CaptureHealthPaths();
            string exePath = _healthExePath;
            string dataFolder = _healthDataFolder;
            string processName = Path.GetFileName(exePath) is { Length: > 0 } exe ? exe : "WinForge.exe";

            var health = await Task.Run(() => DefenderExclusionHealthService.Check(exePath, dataFolder, processName));

            // La fase pudo cerrarse (o reabrirse) mientras se leía: no pintar algo viejo.
            if (generation != _healthGeneration || _phase != HealthPhase) return;
            RenderHealth(health);
        }
        catch (Exception ex)
        {
            _loggingService.LogDebug($"Onboarding: salud de las exclusiones de Defender: {ex.Message}");
        }
        finally
        {
            _healthInFlight = false;
        }
    }

    /// <summary>Fija las rutas de esta copia (ejecutable y carpeta de datos) una sola vez.</summary>
    private void CaptureHealthPaths()
    {
        if (_healthExePath.Length == 0)
            _healthExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinForge.exe");
        if (_healthDataFolder.Length == 0)
            _healthDataFolder = AppPaths.RootDir;
    }

    private string HealthTargetPath(DefenderExclusionTarget target)
        => target == DefenderExclusionTarget.AppExecutable ? _healthExePath : _healthDataFolder;

    /// <summary>Arma una sola vez las filas: ícono + nombre, estado, y los dos botones (abrir su
    /// ubicación real y agregar la exclusión).</summary>
    private void BuildHealthRows()
    {
        if (_healthRows.Count > 0) return;

        foreach (var target in new[]
                 {
                     DefenderExclusionTarget.AppExecutable,
                     DefenderExclusionTarget.DataFolder,
                 })
        {
            var icon = new FontIcon { Glyph = "\uE946", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var state = new TextBlock
            {
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var open = HealthRowButton(I18n.T("Abrir"), accent: false);
            open.Click += (_, _) => OpenHealthTargetLocation(target);

            var exclude = HealthRowButton(I18n.T("Agregar"), accent: true);
            exclude.Click += async (_, _) => await AddHealthExclusionAsync(target);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { open, exclude }
            };

            var grid = new Grid { ColumnSpacing = 10, Width = 520 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, label }
            });
            Grid.SetColumn(state, 1);
            grid.Children.Add(state);
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            HealthRowsHost.Children.Add(grid);
            _healthRows.Add(new HealthRow(icon, label, state, open, exclude, target));
        }
    }

    /// <summary>Botón compacto de fila: el estilo por defecto pide 88 px de ancho y 36 de alto,
    /// que en una fila de 520 px se come el texto. El acento queda para "Agregar".</summary>
    private static Button HealthRowButton(string text, bool accent)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(10, 3, 10, 4),
            CornerRadius = new CornerRadius(4),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (accent && Application.Current.Resources["AccentButtonStyle"] is Style style)
            button.Style = style;
        return button;
    }

    /// <summary>Pinta el resultado: veredicto arriba y una fila por exclusión.</summary>
    private void RenderHealth(DefenderExclusionHealth health)
    {
        var good = Feedback.SuccessBrush;
        var bad = Feedback.WarningBrush;
        var muted = ThemeBrushes.Get("MutedBrush", _selectedTheme);
        var secondary = ThemeBrushes.Get("SecondaryTextBrush", _selectedTheme);

        (string Glyph, SolidColorBrush Brush, string Text) banner = health.Overall switch
        {
            DefenderExclusionState.Present => ("\uE73E", good,
                I18n.T("Todo en orden: Windows Defender no va a bloquear WinForge")),
            // "En parte" NO va en amarillo: el ejecutable está cubierto y la app no va a quedar
            // bloqueada. Lo que falta (la carpeta de datos) es una precaución para lo que la
            // app DESCARGA (componentes y el driver), no un riesgo comprobado: pintarlo como
            // advertencia era alarmar de más.
            DefenderExclusionState.Partial => ("\uE946", secondary,
                I18n.T("El ejecutable está excluido: WinForge no va a ser bloqueado. Falta la carpeta de datos (recomendable, no obligatorio).")),
            DefenderExclusionState.Missing => ("\uE7BA", bad,
                I18n.T("Falta la exclusión: Windows Defender podría bloquear WinForge")),
            DefenderExclusionState.Unreadable => ("\uE946", secondary,
                I18n.T("No se pudo leer la configuración de Defender")),
            _ => ("\uE946", secondary,
                I18n.T("Este equipo no usa Windows Defender: no hay nada que excluir")),
        };
        HealthBannerIcon.Glyph = banner.Glyph;
        HealthBannerIcon.Foreground = banner.Brush;
        HealthBannerText.Text = banner.Text;
        HealthBannerText.Foreground = banner.Brush;

        for (int i = 0; i < _healthRows.Count && i < health.Items.Count; i++)
        {
            var row = _healthRows[i];
            var icon = row.Icon;
            var label = row.Label;
            var state = row.State;
            var item = health.Items[i];

            icon.Glyph = item.State switch
            {
                DefenderExclusionState.Present => "\uE73E",
                DefenderExclusionState.Missing => "\uE7BA",
                _ => "\uE946",
            };
            // El amarillo queda para el ÚNICO caso que puede romper la app (falta el
            // ejecutable). A la carpeta de datos, que es recomendación, se la marca en
            // gris: se ve que falta sin gritar que hay un problema.
            bool isBlocker = item.Target == DefenderExclusionTarget.AppExecutable;
            icon.Foreground = item.State switch
            {
                DefenderExclusionState.Present => good,
                DefenderExclusionState.Missing when isBlocker => bad,
                _ => muted,
            };
            label.Text = HealthTargetLabel(item.Target);
            label.Foreground = secondary;
            state.Text = HealthStateText(item.State, isBlocker);
            state.Foreground = icon.Foreground;

            // El detalle va en el tooltip: la ruta buscada si falta, y QUé exclusión lo cubre
            // si está cubierto (puede ser una carpeta superior o el nombre del proceso).
            string tip = item.MatchedBy is { Length: > 0 }
                ? I18n.T("Cubierto por: {0}", item.MatchedBy)
                : I18n.T("Se busca: {0}", item.Value);
            ToolTipService.SetToolTip(label, new ToolTip
            {
                Content = new TextBlock { Text = tip, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
                Placement = PlacementMode.Bottom
            });

            // Los botones trabajan sobre la ruta que se está verificando (la de ESTE equipo) y
            // los tooltips se rehacen acá para que sigan el idioma. "Agregar" solo tiene sentido
            // mientras falte: si ya está cubierta, la propia verificación lo dice.
            row.Exclude.IsEnabled = item.State == DefenderExclusionState.Missing;
            SetRowButtonTip(row.Open, I18n.T("Abrir la ubicación: {0}", item.Value));
            SetRowButtonTip(row.Exclude, I18n.T("Agregar esta ruta a las exclusiones de Windows Defender: {0}", item.Value));
        }

        _healthOverall = health.Overall;
        UpdateHealthGate();   // el cerrojo del botón sigue al veredicto, sin esperar al latido
    }

    /// <summary>El botón de continuar se habilita solo cuando las DOS exclusiones están
    /// confirmadas (Present). Con el equipo sin Defender o con la configuración ilegible
    /// el paso no se puede cumplir de verdad — no hay nada que agregar ni confirmar — así
    /// que no se bloquea. Un "en parte" (falta la carpeta de datos) SÍ bloquea.</summary>
    private bool HealthGateOpen()
        => _healthOverall is DefenderExclusionState.Present
            or DefenderExclusionState.NotApplicable
            or DefenderExclusionState.Unreadable;

    /// <summary>Estado del botón según la fase y el último veredicto. Mientras no llega
    /// ninguna verificación (null) el botón queda deshabilitado: no se puede avanzar sin
    /// saber si Defender cubre la app.</summary>
    private void UpdateHealthGate()
    {
        if (_phase != HealthPhase)
        {
            NextButton.IsEnabled = true;
            ToolTipService.SetToolTip(NextButton, null);
            return;
        }

        NextButton.IsEnabled = HealthGateOpen();
        SetRowButtonTip(NextButton, NextButton.IsEnabled
            ? I18n.T("Todo listo: se puede continuar")
            : I18n.T("Para continuar, agregá las dos exclusiones y esperá a que aparezca la marca de verificación"));
    }

    private static void SetRowButtonTip(Button button, string text)
        => ToolTipService.SetToolTip(button, new ToolTip
        {
            Content = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
            Placement = PlacementMode.Bottom
        });

    /// <summary>
    /// Abre la ubicación REAL del elemento: el explorador con el ejecutable señalado, o la carpeta.
    /// Si la carpeta de datos todavía no existe (primer arranque), se abre la carpeta que la
    /// contiene y se dice por qué — no se inventa una ruta ni se crea nada por abrir una ventana.
    /// </summary>
    private void OpenHealthTargetLocation(DefenderExclusionTarget target)
    {
        var path = HealthTargetPath(target);
        try
        {
            if (target == DefenderExclusionTarget.AppExecutable && File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                return;
            }
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                return;
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", parent) { UseShellExecute = true });
                ShowHealthAction(I18n.T("Todavía no existe; se abrió la carpeta que la contiene: {0}", path), isError: false);
                return;
            }

            ShowHealthAction(I18n.T("No se encontró la ubicación: {0}", path), isError: true);
        }
        catch (Exception ex)
        {
            ShowHealthAction(I18n.T("No se pudo abrir la ubicación: {0}", ex.Message), isError: true);
            _loggingService.LogWarning($"Onboarding: abrir la ubicación de la exclusión ({target}): {ex.Message}");
        }
    }

    /// <summary>
    /// Agrega la ruta de la fila a las exclusiones de Defender. El éxito NO se anuncia: la
    /// verificación en vivo (cada 500 ms) es la que confirma el cambio, así que si no quedó, la
    /// fila lo sigue diciendo. Solo se informa lo que falla.
    /// </summary>
    private async Task AddHealthExclusionAsync(DefenderExclusionTarget target)
    {
        var path = HealthTargetPath(target);
        try
        {
            var (ok, message) = await DefenderService.AddPathExclusionAsync(path);
            if (!ok)
            {
                ShowHealthAction(I18n.T("No se pudo agregar la exclusión: {0}", message.Trim()), isError: true);
                _loggingService.LogWarning($"Onboarding: agregar la exclusión de Defender ({path}): {message.Trim()}");
                return;
            }

            ShowHealthAction(null, isError: false);
            _ = RunHealthCheckAsync();   // confirmación inmediata en vez de esperar al latido
        }
        catch (Exception ex)
        {
            ShowHealthAction(I18n.T("No se pudo agregar la exclusión: {0}", ex.Message), isError: true);
            _loggingService.LogWarning($"Onboarding: agregar la exclusión de Defender ({path}): {ex.Message}");
        }
    }

    /// <summary>Línea de resultado de las acciones de las filas. Vacía = no se muestra nada.</summary>
    private void ShowHealthAction(string? message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            HealthActionText.Visibility = Visibility.Collapsed;
            return;
        }

        HealthActionText.Text = message;
        HealthActionText.Foreground = isError ? Feedback.WarningBrush : ThemeBrushes.Get("SecondaryTextBrush", _selectedTheme);
        HealthActionText.Visibility = Visibility.Visible;
    }

    private static string HealthTargetLabel(DefenderExclusionTarget target) => target switch
    {
        // El nombre del ejecutable no se traduce: es un nombre de archivo.
        DefenderExclusionTarget.AppExecutable => "WinForge.exe",
        _ => I18n.T("Carpeta de datos"),
    };

    /// <summary>Texto del estado de una fila. <paramref name="isBlocker"/> es true solo en la
    /// fila del ejecutable: ahí un "Falta" es un problema; en la carpeta de datos es una
    /// recomendación pendiente y decir "Falta" a secas le daba el mismo peso.</summary>
    private static string HealthStateText(DefenderExclusionState state, bool isBlocker) => state switch
    {
        DefenderExclusionState.Present => I18n.T("Excluida"),
        DefenderExclusionState.Missing => I18n.T(isBlocker ? "Falta" : "Recomendado"),
        DefenderExclusionState.Unreadable => I18n.T("No se pudo leer"),
        _ => I18n.T("No aplica"),
    };

    // ----- Textos -----

    /// <summary>Rótulos del riel de fases, en el mismo orden que los paneles.</summary>
    private static readonly string[] PhaseLabels = { "Idioma", "Tema", "Antivirus", "Listo" };

    private void ApplyTexts()
    {
        SubtitleText.Text = I18n.T("Configurá WinForge a tu gusto. Son 4 pasos y podés cambiarlo todo después.");
        BackButton.Content = I18n.T("Atrás");
        NextButton.Content = I18n.T("Continuar");
        StarButton.Content = I18n.T("Dejar una estrella en GitHub");
        ApplyProgressLabels();
    }

    private void ApplyProgressLabels()
    {
        for (int i = 0; i < _progressLabels.Count && i < PhaseLabels.Length; i++)
            _progressLabels[i].Text = I18n.T(PhaseLabels[i]);
    }

    private void OnLanguageChanged()
    {
        I18n.ApplyToVisualTree(RootGrid);
        ApplyTexts();
        Title = WindowTitleText;

        // Re-marcar las filas: la ✓ tiene que seguir al idioma activo (si la lista de idiomas
        // no cambió, esto NO toca la colección del desplegable: ver BuildLanguagePhase).
        _loggingService.LogInfo($"Onboarding: cambio de idioma ({I18n.Current}) aplicado a la ventana");
        BuildLanguagePhase();

        UpdatePhaseUi(animateIn: false);
    }
}
