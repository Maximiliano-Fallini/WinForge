using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using WHPO.Core;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Services;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// TCP avanzado (antes era la sección 6 de la pestaña Red, separada en su propia
/// pestaña). Funciona como un TCP Optimizer: presets de juego/Windows, Nagle,
/// algoritmo de congestión, ECN, timestamps, RSS, Fast Open, autotuning (solo
/// lectura), MTU por interfaz, editor de las plantillas del stack y las reglas
/// de red de Windows. Backup y restauración incluidos.
/// </summary>
public sealed partial class TcpPage : Page
{
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private bool _dataLoaded;

    private TcpService.TcpState? _tcpCurrent;

    /// <summary>
    /// True si la última lectura del estado TCP salió bien. Cuando falla, los controles quedan en sus
    /// valores por DEFECTO: los botones que ESCRIBEN se deshabilitan para que un clic no pise la
    /// configuración real del equipo con esos defaults. El backup sí se sigue pudiendo restaurar,
    /// porque viene de una lectura previa buena.
    /// </summary>
    private bool _tcpReadOk = true;

    // Controles de la card TCP (se reconstruyen en cada carga / cambio de idioma)
    private TextBlock? _tcpStatusText;
    private TextBlock? _tcpApplyResultText;
    private ToggleSwitch? _tcpNagleToggle;
    private ComboBox? _tcpCongestionCombo;
    private ToggleSwitch? _tcpEcnToggle;
    private ToggleSwitch? _tcpTimestampsToggle;
    private ToggleSwitch? _tcpRssToggle;
    private ToggleSwitch? _tcpFastOpenToggle;
    private TextBlock? _tcpActualNagle;
    private TextBlock? _tcpActualCongestion;
    private TextBlock? _tcpActualEcn;
    private TextBlock? _tcpActualTimestamps;
    private TextBlock? _tcpActualRss;
    private TextBlock? _tcpActualFastOpen;
    private TextBlock? _tcpAutoTuningText;
    private Button? _tcpAutoTuningFixButton;
    private TextBlock? _tcpMtuText;
    private Button? _tcpApplyButton;
    private Button? _gamingProfileButton;
    private Button? _windowsProfileButton;
    private Button? _customProfileButton;
    private Button? _rulesGamingButton;
    private Button? _rulesWindowsButton;
    private Button? _rulesCustomButton;
    /// <summary>Resultado del último ApplyTcpAsync (para el mensaje combinado del preset).</summary>
    private bool _lastTcpApplyOk;
    private string _lastTcpApplyMsg = "";

    private const string TcpBackupKey = "red.tcp.backup";

    private static Microsoft.UI.Xaml.Media.SolidColorBrush MutedTextBrush => ThemeBrushes.Get("MutedBrush");

    public TcpPage()
    {
        try
        {
            InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;

            _loggingService = App.Services.GetRequiredService<ILoggingService>();
            _settingsService = App.Services.GetRequiredService<ISettingsService>();

            // La card se construye en código con I18n.T: al cambiar idioma estando en
            // la página hay que reconstruirla. La página se recrea en cada navegación
            // (cache Disabled), así que se desuscribe en OnNavigatedFrom.
            I18n.LanguageChanged += OnLanguageChanged;
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en constructor TcpPage: {ex}", ex);
            throw;
        }
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        I18n.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        if (!_dataLoaded) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try { await BuildTcpAdvancedCardAsync(); }
            catch (Exception ex) { _loggingService.LogError($"Error re-traduciendo TcpPage: {ex.Message}", ex); }
        });
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (_dataLoaded) return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await BuildTcpAdvancedCardAsync();
                _dataLoaded = true;
            }
            catch (Exception ex2)
            {
                _loggingService.LogError($"Error cargando TcpPage: {ex2}", ex2);
                if (DebugText != null)
                {
                    DebugText.Text = I18n.T("Error cargando TCP: {0}", ex2.Message);
                    DebugText.Visibility = Visibility.Visible;
                }
            }
        });
    }

    /// <summary>
    /// Card TCP avanzado. Envuelve al cuerpo real para que un fallo NUNCA deje la
    /// card en "Consultando estado TCP…": el texto de estado siempre se resuelve
    /// con el error (y un botón Reintentar).
    /// </summary>
    private async Task BuildTcpAdvancedCardAsync()
    {
        try
        {
            await BuildTcpAdvancedCardCoreAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"TcpPage: error construyendo la card TCP: {ex.Message}", ex);
            ResolveStatusWithError(_tcpStatusText, I18n.T("Error cargando el estado TCP: {0}", ex.Message));
            if (DebugText != null)
            {
                DebugText.Text = I18n.T("Error cargando TCP: {0}", ex.Message);
                DebugText.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>Cuerpo real de la card TCP avanzado.</summary>
    private async Task BuildTcpAdvancedCardCoreAsync()
    {
        if (TcpAdvancedPanel == null) return;
        TcpAdvancedPanel.Children.Clear();

        var card = new Border
        {
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16)
        };
        var panel = new StackPanel { Spacing = 12 };
        card.Child = panel;
        TcpAdvancedPanel.Children.Add(card);

        _tcpStatusText = new TextBlock { Text = I18n.T("Consultando estado TCP..."), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_tcpStatusText);
        Feedback.Running(_tcpStatusText, I18n.T("Consultando estado TCP..."), persistent: true);

        var state = await TcpService.GetStateAsync();
        _tcpCurrent = state ?? new TcpService.TcpState();
        _tcpReadOk = state != null;
        if (!_tcpReadOk)
            ResolveStatusWithError(
                _tcpStatusText,
                I18n.T("No se pudo leer el estado TCP: {0}", "netsh") + " "
                    + I18n.T("Sin poder leer el estado real, Aplicar queda deshabilitado para no escribir valores por defecto sobre tu configuración."),
                BuildTcpAdvancedCardAsync);

        // Presets primero (antes de las opciones): solo CARGAN en los controles, el
        // usuario confirma con Aplicar. Los tres botones marcan el perfil REAL
        // aplicado (TCP + reglas): el seleccionado va con fondo de acento (estilo
        // pill del ThemePickerUi); "Personalizado" cuando lo aplicado no coincide.
        var presetsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var presetStyle = (Style)Resources["PresetButtonStyle"];
        var gamingBtn = new Button { Content = I18n.T("Óptimo para juegos"), Style = presetStyle, VerticalAlignment = VerticalAlignment.Center };
        gamingBtn.Click += (s, e) => LoadTcpPreset(gaming: true);
        var defaultBtn = new Button { Content = I18n.T("Valores por defecto (Windows)"), Style = presetStyle, VerticalAlignment = VerticalAlignment.Center };
        defaultBtn.Click += (s, e) => LoadTcpPreset(gaming: false);
        // "Personalizado" no carga nada ni aplica: es solo el marcador de "los controles
        // no describen ningún preset". Ya existe en el diccionario (ventiladores).
        var customBtn = new Button { Content = I18n.T("Personalizado"), Style = presetStyle, VerticalAlignment = VerticalAlignment.Center };
        customBtn.Click += (s, e) => SelectAmong(customBtn, _gamingProfileButton, _windowsProfileButton, _customProfileButton);
        presetsRow.Children.Add(gamingBtn);
        presetsRow.Children.Add(defaultBtn);
        presetsRow.Children.Add(customBtn);
        panel.Children.Add(presetsRow);
        _gamingProfileButton = gamingBtn;
        _windowsProfileButton = defaultBtn;
        _customProfileButton = customBtn;

        // Desactivar Nagle (TCPNoDelay + TcpAckFrequency en la interfaz activa)
        _tcpNagleToggle = NewToggle();
        // El toggle dice "Nagle está desactivado", y basta con UNO de los dos valores para que lo
        // esté (TCPNoDelay=1 ya apaga Nagle). Si quedó a medias, el toggle se ve ACTIVADO y aplicar
        // completa el par en vez de borrarlo: antes quedaba apagado y aplicar revertía el tweak de
        // otro optimizador sin que el usuario lo pidiera.
        _tcpNagleToggle.IsOn = _tcpCurrent.NoDelaySet || _tcpCurrent.AckFreqSet;
        _tcpNagleToggle.Toggled += (s, e) => UpdateProfileIndicator();
        var (nagleRow, nagleActual) = BuildSettingRow(
            I18n.T("Desactivar Nagle"), I18n.T("TCPNoDelay + TcpAckFrequency"), NagleActual(_tcpCurrent), _tcpNagleToggle, TtNagle);
        _tcpActualNagle = nagleActual;
        panel.Children.Add(nagleRow);

        // Algoritmo de congestión (CUBIC / CTCP)
        _tcpCongestionCombo = new ComboBox { MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Stretch };
        _tcpCongestionCombo.Items.Add(new ComboBoxItem { Content = I18n.T("CUBIC (predeterminado)"), Tag = "cubic" });
        _tcpCongestionCombo.Items.Add(new ComboBoxItem { Content = I18n.T("CTCP (menor latencia)"), Tag = "ctcp" });
        _tcpCongestionCombo.SelectedIndex = _tcpCurrent.CongestionProvider == "ctcp" ? 1 : 0;
        _tcpCongestionCombo.SelectionChanged += (s, e) => UpdateProfileIndicator();
        // El proveedor de congestión se escribe sobre la plantilla GLOBAL del stack (la que aplica
        // sin filtros y la MISMA que edita la sección de plantillas). La fila la nombra: antes
        // parecía un ajuste "global" sin decir en qué plantilla caía.
        var (congestionRow, congestionActual) = BuildSettingRow(
            I18n.T("Algoritmo de congestión"),
            I18n.T("Plantilla: {0}", TcpTemplateService.LabelFor(TcpTemplateService.ResolveNetshName(_tcpCurrent.GlobalTemplate))),
            CongestionActual(_tcpCurrent.CongestionProvider), _tcpCongestionCombo, TtCongestion);
        _tcpActualCongestion = congestionActual;
        panel.Children.Add(congestionRow);

        // ECN
        _tcpEcnToggle = NewToggle();
        _tcpEcnToggle.IsOn = _tcpCurrent.EcnEnabled;
        _tcpEcnToggle.Toggled += (s, e) => UpdateProfileIndicator();
        var (ecnRow, ecnActual) = BuildSettingRow(
            I18n.T("ECN (Notificación de congestión explícita)"), null, ActualText(_tcpCurrent.EcnEnabled), _tcpEcnToggle, TtEcn);
        _tcpActualEcn = ecnActual;
        panel.Children.Add(ecnRow);

        // Timestamps
        _tcpTimestampsToggle = NewToggle();
        _tcpTimestampsToggle.IsOn = _tcpCurrent.TimestampsEnabled;
        _tcpTimestampsToggle.Toggled += (s, e) => UpdateProfileIndicator();
        var (timestampsRow, timestampsActual) = BuildSettingRow(
            I18n.T("Timestamps TCP (RFC 1323)"), null, ActualText(_tcpCurrent.TimestampsEnabled), _tcpTimestampsToggle, TtTimestamps);
        _tcpActualTimestamps = timestampsActual;
        panel.Children.Add(timestampsRow);

        // RSS
        _tcpRssToggle = NewToggle();
        _tcpRssToggle.IsOn = _tcpCurrent.RssEnabled;
        _tcpRssToggle.Toggled += (s, e) => UpdateProfileIndicator();
        var (rssRow, rssActual) = BuildSettingRow(
            I18n.T("RSS (Receive Side Scaling)"), null, ActualText(_tcpCurrent.RssEnabled), _tcpRssToggle, TtRss);
        _tcpActualRss = rssActual;
        panel.Children.Add(rssRow);

        // Fast Open
        _tcpFastOpenToggle = NewToggle();
        _tcpFastOpenToggle.IsOn = _tcpCurrent.FastOpenEnabled;
        _tcpFastOpenToggle.Toggled += (s, e) => UpdateProfileIndicator();
        var (fastOpenRow, fastOpenActual) = BuildSettingRow(
            I18n.T("TCP Fast Open"), null, ActualText(_tcpCurrent.FastOpenEnabled), _tcpFastOpenToggle, TtFastOpen);
        _tcpActualFastOpen = fastOpenActual;
        panel.Children.Add(fastOpenRow);

        panel.Children.Add(new Rectangle { Height = 1, Fill = ThemeBrushes.Get("CardBorderBrush"), Margin = new Thickness(0, 4, 0, 4) });
        // Autotuning: solo lectura + botón "recomendado" si no está en Normal
        var autotuningPanel = new StackPanel { Spacing = 4 };
        autotuningPanel.Children.Add(BuildInfoTitle(I18n.T("Ajuste automático de la ventana TCP"), TtAutoTuning));
        _tcpAutoTuningText = new TextBlock { Text = AutoTuningActual(_tcpCurrent.AutoTuningLevel), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        autotuningPanel.Children.Add(_tcpAutoTuningText);
        if (!_tcpCurrent.AutoTuningLevel.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            _tcpAutoTuningFixButton = new Button { Content = I18n.T("Restaurar a Normal"), Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = _tcpReadOk };
            _tcpAutoTuningFixButton.Click += async (s, e) =>
            {
                var d = ReadTcpDesired();
                d.AutoTuningLevel = "normal";
                await ApplyTcpAsync(I18n.T("Ajuste automático restaurado a Normal."), d, includeAutoTuning: true);
            };
            ToolTipService.SetToolTip(_tcpAutoTuningFixButton, I18n.T(TtAutoTuning));
            autotuningPanel.Children.Add(_tcpAutoTuningFixButton);
        }
        panel.Children.Add(autotuningPanel);

        // MTU real por interfaz
        var mtuPanel = new StackPanel { Spacing = 4 };
        mtuPanel.Children.Add(BuildInfoTitle(I18n.T("MTU"), TtMtu));
        _tcpMtuText = new TextBlock { Text = MtuActual(_tcpCurrent), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        mtuPanel.Children.Add(_tcpMtuText);
        panel.Children.Add(mtuPanel);

        // Acciones (los presets ya están arriba, antes de las opciones)
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _tcpApplyButton = new Button { Content = I18n.T("Aplicar"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6), IsEnabled = _tcpReadOk };
        _tcpApplyButton.Click += async (s, e) => await ApplyTcpAsync(I18n.T("TCP aplicado correctamente."));
        var restoreBtn = new Button { Content = I18n.T("Restaurar todo"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        restoreBtn.Click += async (s, e) => await RestoreTcpAsync();
        buttons.Children.Add(_tcpApplyButton);
        buttons.Children.Add(restoreBtn);
        panel.Children.Add(buttons);

        _tcpApplyResultText = new TextBlock { Text = "", FontSize = 12, Visibility = Visibility.Collapsed, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_tcpApplyResultText);

        panel.Children.Add(new TextBlock
        {
            Text = I18n.T("Solo se incluyen ajustes con efecto real y reversibles. El ajuste automático de la ventana TCP no se toca al aplicar: se muestra y, si no está en Normal, tenés el botón para restaurarlo."),
            FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap
        });

        // Fase B: editor de plantillas del stack (perillas que Windows no expone).
        await RunSectionGuardedAsync(() => AppendTcpTemplateSectionAsync(panel), "plantillas TCP");

        if (state != null)
        {
            Feedback.Set(_tcpStatusText, null);
            _tcpStatusText.Visibility = Visibility.Collapsed;
        }

        // Reglas de Windows (nivel SO): misma pasada, la página se reconstruye
        // entera al cambiar de idioma. Va aislada: si falla, la card TCP se sigue
        // construyendo y no queda con el texto de "Consultando…".
        await RunSectionGuardedAsync(BuildWindowsRulesCardAsync, "reglas de red");
    }

    /// <summary>
    /// Resuelve el texto de estado con un error visible y un botón Reintentar en su
    /// panel. Nunca deja una carga a medias (el bug de "Consultando estado TCP…").
    /// </summary>
    private void ResolveStatusWithError(TextBlock? status, string message, Func<Task>? retryAction = null)
    {
        if (status == null) return;
        Feedback.Error(status, message, persistent: true);
        status.Visibility = Visibility.Visible;
        if (status.Parent is Panel host && !host.Children.OfType<Button>().Any(b => (b.Content as string) == I18n.T("Reintentar")))
        {
            var retry = new Button
            {
                Content = I18n.T("Reintentar"),
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var action = retryAction ?? BuildTcpAdvancedCardAsync;
            retry.Click += async (s, e) => await action();
            host.Children.Add(retry);
        }
    }

    /// <summary>Corre una sección de la página aislada: un fallo no arrastra al resto.</summary>
    private async Task RunSectionGuardedAsync(Func<Task> section, string sectionName)
    {
        try
        {
            await section();
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"TcpPage: error construyendo {sectionName}: {ex.Message}", ex);
            if (DebugText != null)
            {
                DebugText.Text = I18n.T("Error cargando {0}: {1}", sectionName, ex.Message);
                DebugText.Visibility = Visibility.Visible;
            }
        }
    }

    // =====================================================================
    // Reglas de Windows (red): tweaks a nivel SO que aplican TCP Optimizer /
    // CTTE en la rama "Windows Tweaks". Presets Rendimiento / Windows (predeterminado) +
    // restauración a defaults. Todos reversibles (RestoreDefaults borra).
    // =====================================================================

    private TextBlock? _rulesStatusText;
    private TextBlock? _rulesResultText;
    private Button? _rulesApplyButton;
    private readonly Dictionary<string, ComboBox> _rulesCombos = new();
    private readonly Dictionary<string, TextBlock> _rulesActual = new();
    private List<NetworkTweaksService.TweakState> _rulesCurrent = new();

    private static bool TryParseU32(string s, out uint v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out v);
        if (uint.TryParse(s, System.Globalization.NumberStyles.Integer, null, out v)) return true;
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
    }

    /// <summary>
    /// True si el valor leído del registro coincide con el PREDETERMINADO documentado de
    /// Windows. Reconoce el default por valor, no por ausencia: un ajuste con el valor ya
    /// escrito e igual al de Windows se lee como "Default de Windows" igual que si faltara.
    /// </summary>
    private static bool EqualsWindowsDefault(NetworkTweaksService.TweakDef def, string raw)
        => TryParseU32(raw, out var cur)
           && TryParseU32(def.WindowsDefault, out var defValue)
           && cur == defValue;

    private void LoadRulesPreset(bool gaming)
    {
        foreach (var st in _rulesCurrent)
        {
            if (!_rulesCombos.TryGetValue(st.Def.Id, out var combo)) continue;
            var want = gaming ? st.Def.Gaming : st.Def.Windows;
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == want);
            if (item != null) combo.SelectedItem = item;
        }
        UpdateRulesProfileIndicator();
        if (_rulesResultText != null)
        {
            _rulesResultText.Visibility = Visibility.Visible;
            Feedback.Info(_rulesResultText, I18n.T("Preset cargado: {0} — revisá los valores y toca Aplicar.", gaming ? I18n.T("Rendimiento (juegos)") : I18n.T("Windows (predeterminado)")));
        }
    }

    private async Task BuildWindowsRulesCardAsync()
    {
        if (WindowsRulesPanel == null) return;
        WindowsRulesPanel.Children.Clear();
        _rulesCombos.Clear();
        _rulesActual.Clear();

        var rulesCard = new Border
        {
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16)
        };
        var rulesPanel = new StackPanel { Spacing = 12 };
        rulesCard.Child = rulesPanel;
        WindowsRulesPanel.Children.Add(rulesCard);

        _rulesStatusText = new TextBlock { Text = I18n.T("Consultando reglas de red de Windows..."), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        rulesPanel.Children.Add(_rulesStatusText);

        // Presets primero (mismo patrón que la card TCP): cargan, no aplican. Tres
        // botones con estilo pill; "Personalizado" se marca cuando los combos no
        // describen ningún preset.
        var presetsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var rulesPresetStyle = (Style)Resources["PresetButtonStyle"];
        var perfBtn = new Button { Content = I18n.T("Rendimiento (juegos)"), Style = rulesPresetStyle };
        perfBtn.Click += (s, e) => LoadRulesPreset(gaming: true);
        var windowsBtn = new Button { Content = I18n.T("Windows (predeterminado)"), Style = rulesPresetStyle };
        windowsBtn.Click += (s, e) => LoadRulesPreset(gaming: false);
        var customBtn = new Button { Content = I18n.T("Personalizado"), Style = rulesPresetStyle };
        customBtn.Click += (s, e) => SelectAmong(customBtn, _rulesGamingButton, _rulesWindowsButton, _rulesCustomButton);
        presetsRow.Children.Add(perfBtn);
        presetsRow.Children.Add(windowsBtn);
        presetsRow.Children.Add(customBtn);
        rulesPanel.Children.Add(presetsRow);
        _rulesGamingButton = perfBtn;
        _rulesWindowsButton = windowsBtn;
        _rulesCustomButton = customBtn;

        _rulesCurrent = await NetworkTweaksService.GetStateAsync();
        if (_rulesCurrent.Count == 0)
        {
            ResolveStatusWithError(_rulesStatusText, I18n.T("No se pudieron leer las reglas de red de Windows."), BuildWindowsRulesCardAsync);
            // Sin reglas no hay perfil confiable: los tres botones quedan habilitados.
            UpdateProfileIndicator();
            return;
        }
        else if (_rulesStatusText != null)
            _rulesStatusText.Visibility = Visibility.Collapsed;

        foreach (var st in _rulesCurrent)
        {
            var combo = new ComboBox { MinWidth = 190, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectionChanged += (s, e) => UpdateRulesProfileIndicator();

            // Un ajuste cuyos dos presets escriben lo mismo no tiene nada que elegir: se ofrece una
            // sola opción en vez de dos idénticas. Es el caso de MaxUserPort, que en Windows 10/11
            // no debe escribirse (su default real es el rango dinámico 49152-65535).
            var presetsEquivalent = string.Equals(
                st.Def.Gaming?.Trim(), st.Def.Windows?.Trim(), StringComparison.OrdinalIgnoreCase);

            if (presetsEquivalent)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = I18n.T("Predeterminado de Windows"),
                    Tag = st.Def.Gaming ?? ""
                });
            }
            else
            {
                // El combo ofrece los dos valores del ajuste. Los que tienen nombre propio
                // (p. ej. SystemResponsiveness: 10 % / 20 %) usan su etiqueta; el resto, la genérica.
                combo.Items.Add(new ComboBoxItem { Content = I18n.T(st.Def.GamingLabel ?? "Rendimiento"), Tag = st.Def.Gaming });
                combo.Items.Add(new ComboBoxItem { Content = I18n.T(st.Def.WindowsLabel ?? "Windows (predeterminado)"), Tag = st.Def.Windows });
            }

            // Selección inicial por el valor actual del registro (helper reutilizable:
            // también lo usa el refresco "en lugar" tras aplicar/restaurar).
            SelectComboByValue(combo, st);
            _rulesCombos[st.Def.Id] = combo;

            var (row, actual) = BuildSettingRow(
                I18n.T(st.Def.Title),
                null,
                RulesActualText(st),
                combo,
                RuleTooltip(st.Def.Tooltip));
            _rulesActual[st.Def.Id] = actual;
            rulesPanel.Children.Add(row);
        }

        var rulesButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _rulesApplyButton = new Button { Content = I18n.T("Aplicar"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        _rulesApplyButton.Click += async (s, e) => await ApplyRulesAsync();
        rulesButtons.Children.Add(_rulesApplyButton);
        var rulesRestoreBtn = new Button { Content = I18n.T("Restaurar valores de Windows"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        rulesRestoreBtn.Click += async (s, e) => await RestoreRulesAsync();
        rulesButtons.Children.Add(rulesRestoreBtn);
        rulesPanel.Children.Add(rulesButtons);

        _rulesResultText = new TextBlock { Text = "", FontSize = 12, Visibility = Visibility.Collapsed, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        rulesPanel.Children.Add(_rulesResultText);

        rulesPanel.Children.Add(new TextBlock
        {
            Text = I18n.T("Estas reglas se leen del registro al arrancar Windows: reiniciá para que el cambio sea total. Todas son reversibles."),
            FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap
        });

        // Ambas tarjetas quedan con sus botones de perfil: cada una deduce el suyo.
        UpdateProfileIndicator();
        UpdateRulesProfileIndicator();
    }

    /// <summary>Texto "Actual:" de una regla, reconociendo el default por valor o por ausencia.</summary>
    private static string RulesActualText(NetworkTweaksService.TweakState st)
        => st.RawValue == null
            ? I18n.T("Default de Windows (sin valor en el registro)")
            : EqualsWindowsDefault(st.Def, st.RawValue)
                ? I18n.T("Default de Windows (valor en el registro: {0})", NetworkTweaksService.DisplayValue(st.Def, st.RawValue))
                : I18n.T("Actual: {0}", NetworkTweaksService.DisplayValue(st.Def, st.RawValue));

    /// <summary>
    /// Selecciona en el combo el ítem cuyo Tag coincide con el valor actual del registro;
    /// si no coincide con ninguno, cae en la última opción (Windows, la menos invasiva).
    /// Se compara contra los Tags cargados, así que un Tag vacío ("borrar el valor")
    /// nunca rompe la selección por quedar fuera de rango.
    /// </summary>
    private static void SelectComboByValue(ComboBox combo, NetworkTweaksService.TweakState st)
    {
        combo.SelectedIndex = combo.Items.Count - 1;
        if (st.RawValue != null && TryParseU32(st.RawValue, out var cur))
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem { Tag: string tag }
                    && TryParseU32(tag, out var tv) && tv == cur)
                {
                    combo.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Refresca la tarjeta de reglas EN SU LUGAR tras aplicar o restaurar: re-lee el
    /// estado y actualiza las etiquetas "Actual:" y las selecciones de cada combo, SIN
    /// reconstruir la card (reconstruir causaba un flash y perdía el scroll, como pasaba
    /// con "Restaurar valores"). Si la re-lectura falla, cae a reconstruir como respaldo.
    /// </summary>
    private async Task RefreshRulesInPlace()
    {
        var fresh = await NetworkTweaksService.GetStateAsync();
        if (fresh.Count == 0)
        {
            await BuildWindowsRulesCardAsync();
            return;
        }
        _rulesCurrent = fresh;
        foreach (var st in _rulesCurrent)
        {
            if (_rulesActual.TryGetValue(st.Def.Id, out var actual))
                actual.Text = RulesActualText(st);
            if (_rulesCombos.TryGetValue(st.Def.Id, out var combo))
                SelectComboByValue(combo, st);
        }
        UpdateRulesProfileIndicator();
    }

    private async Task ApplyRulesAsync(bool quiet = false)
    {
        if (_rulesResultText == null || _rulesCombos.Count == 0) return;
        if (!quiet) _rulesResultText.Visibility = Visibility.Visible;
        if (_rulesApplyButton != null) _rulesApplyButton.IsEnabled = false;
        Feedback.Running(_rulesResultText, I18n.T("Aplicando reglas de red..."));
        try
        {
            var values = new Dictionary<string, string>();
            foreach (var (id, combo) in _rulesCombos)
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: string tag })
                    values[id] = tag;
            }
            var failed = await NetworkTweaksService.ApplyAsync(values);
            if (failed.Count == 0)
            {
                if (!quiet) Feedback.Success(_rulesResultText, I18n.T("Reglas de red aplicadas. Reiniciá Windows para que el cambio sea total."));
            }
            else
            {
                // En modo preset el mensaje vive en la tarjeta TCP: se propaga como error visible.
                var msg = I18n.T("No se pudieron aplicar: {0}", string.Join(", ", failed));
                if (quiet && _tcpApplyResultText != null)
                {
                    _tcpApplyResultText.Visibility = Visibility.Visible;
                    Feedback.Error(_tcpApplyResultText, msg);
                }
                else Feedback.Error(_rulesResultText, msg);
            }
            await RefreshRulesInPlace();
        }
        catch (Exception ex)
        {
            Feedback.Error(_rulesResultText, ex.Message);
            _loggingService.LogError("Error aplicando reglas de red de Windows", ex);
        }
        finally
        {
            if (_rulesApplyButton != null) _rulesApplyButton.IsEnabled = true;
        }
    }

    private async Task RestoreRulesAsync()
    {
        if (_rulesResultText == null) return;
        _rulesResultText.Visibility = Visibility.Visible;
        if (_rulesApplyButton != null) _rulesApplyButton.IsEnabled = false;
        Feedback.Running(_rulesResultText, I18n.T("Restaurando valores de Windows..."));
        try
        {
            var (ok, msg) = await NetworkTweaksService.RestoreDefaultsAsync();
            if (ok) Feedback.Success(_rulesResultText, I18n.T("Valores de Windows restaurados. Reiniciá para aplicar todo."));
            else Feedback.Error(_rulesResultText, I18n.T("Error restaurando: {0}", msg));
            await RefreshRulesInPlace();
        }
        catch (Exception ex)
        {
            Feedback.Error(_rulesResultText, ex.Message);
        }
        finally
        {
            if (_rulesApplyButton != null) _rulesApplyButton.IsEnabled = true;
        }
    }

    private TcpService.TcpState ReadTcpDesired()
    {
        var d = new TcpService.TcpState();
        if (_tcpCongestionCombo?.SelectedItem is ComboBoxItem { Tag: string tag }) d.CongestionProvider = tag;
        d.EcnEnabled = _tcpEcnToggle?.IsOn ?? false;
        d.TimestampsEnabled = _tcpTimestampsToggle?.IsOn ?? false;
        d.RssEnabled = _tcpRssToggle?.IsOn ?? false;
        d.FastOpenEnabled = _tcpFastOpenToggle?.IsOn ?? false;
        d.NagleDisabled = _tcpNagleToggle?.IsOn ?? false;
        return d;
    }

    /// <summary>
    /// Marca el botón del perfil que describen LOS CONTROLES (no lo último aplicado),
    /// igual que TecladoPage.HighlightPresetInEffect: tocar un preset lo marca y
    /// posiciona ambas tarjetas; editar cualquier control vuelve a deducir y cae a
    /// "Personalizado" si ya no coincide. La marca nunca puede quedar desactualizada
    /// porque se deduce de lo que se ve, no de memoria.
    /// </summary>
    private void UpdateProfileIndicator()
    {
        if (_gamingProfileButton == null || _windowsProfileButton == null || _customProfileButton == null) return;
        SelectAmong(DetectTcpProfile() switch
        {
            "gaming" => _gamingProfileButton,
            "windows" => _windowsProfileButton,
            _ => _customProfileButton,
        }, _gamingProfileButton, _windowsProfileButton, _customProfileButton);
    }

    /// <summary>
    /// Igual que UpdateProfileIndicator pero para la tarjeta de Reglas: marca el botón
    /// del preset que describen LOS COMBOS de reglas. Cada tarjeta deduce su propio
    /// perfil de forma independiente.
    /// </summary>
    private void UpdateRulesProfileIndicator()
    {
        if (_rulesGamingButton == null || _rulesWindowsButton == null || _rulesCustomButton == null) return;
        SelectAmong(DetectRulesProfile() switch
        {
            "gaming" => _rulesGamingButton,
            "windows" => _rulesWindowsButton,
            _ => _rulesCustomButton,
        }, _rulesGamingButton, _rulesWindowsButton, _rulesCustomButton);
    }

    /// <summary>Qué preset describen los TOGGLES TCP. "custom" si no coincide con ninguno.</summary>
    private string DetectTcpProfile()
    {
        if (_tcpNagleToggle == null || _tcpCongestionCombo == null) return "custom";
        bool gaming = _tcpNagleToggle.IsOn
            && _tcpCongestionCombo.SelectedIndex == 1   // CTCP
            && (_tcpEcnToggle?.IsOn ?? false) == false
            && (_tcpTimestampsToggle?.IsOn ?? true) == false
            && (_tcpRssToggle?.IsOn ?? false)
            && (_tcpFastOpenToggle?.IsOn ?? false);
        bool windows = !_tcpNagleToggle.IsOn
            && _tcpCongestionCombo.SelectedIndex == 0   // CUBIC
            && (_tcpEcnToggle?.IsOn ?? true) == false
            && (_tcpTimestampsToggle?.IsOn ?? false)
            && (_tcpRssToggle?.IsOn ?? false)
            && (_tcpFastOpenToggle?.IsOn ?? false);
        if (gaming) return "gaming";
        if (windows) return "windows";
        return "custom";
    }

    /// <summary>Qué preset describen los COMBOS de reglas. "custom" si no coincide con ninguno.</summary>
    private string DetectRulesProfile()
    {
        if (_rulesCombos.Count == 0 || _rulesCurrent.Count == 0) return "custom";
        bool Match(bool gaming) => _rulesCurrent.All(st =>
        {
            if (!_rulesCombos.TryGetValue(st.Def.Id, out var combo)) return false;
            if (!(combo.SelectedItem is ComboBoxItem { Tag: string tag })) return false;
            var want = gaming ? st.Def.Gaming : st.Def.Windows;
            return string.Equals(tag?.Trim(), want?.Trim(), StringComparison.OrdinalIgnoreCase);
        });
        if (Match(gaming: true)) return "gaming";
        if (Match(gaming: false)) return "windows";
        return "custom";
    }

    /// <summary>Resalta el botón dado entre los tres y desmarca el resto (estilo XAML).</summary>
    private void SelectAmong(Button selected, Button? a, Button? b, Button? c)
    {
        if (a == null || b == null || c == null) return;
        var normal = (Style)Resources["PresetButtonStyle"];
        var sel = (Style)Resources["PresetButtonSelectedStyle"];
        a.Style = normal;
        b.Style = normal;
        c.Style = normal;
        selected.Style = sel;
    }

    /// <summary>
    /// Carga un preset en los controles de AMBAS tarjetas (TCP + reglas de Windows).
    /// Solo posiciona: el usuario confirma con Aplicar. La marca se deduce de los
    /// controles recién posicionados (los eventos ya la refrescan; el llamado final
    /// la fija por si algún control no disparó cambio).
    /// </summary>
    private void LoadTcpPreset(bool gaming)
    {
        if (_tcpNagleToggle == null || _tcpCongestionCombo == null) return;
        _tcpNagleToggle.IsOn = gaming;                        // Nagle desactivado en gaming
        _tcpCongestionCombo.SelectedIndex = gaming ? 1 : 0;   // CTCP / CUBIC
        if (_tcpEcnToggle != null) _tcpEcnToggle.IsOn = false;
        if (_tcpTimestampsToggle != null) _tcpTimestampsToggle.IsOn = !gaming;
        if (_tcpRssToggle != null) _tcpRssToggle.IsOn = true;
        if (_tcpFastOpenToggle != null) _tcpFastOpenToggle.IsOn = true;
        // Las reglas de Windows son parte del mismo perfil: se posicionan junto con el TCP.
        LoadRulesPreset(gaming);
        UpdateProfileIndicator();
        if (_tcpApplyResultText != null)
        {
            _tcpApplyResultText.Visibility = Visibility.Visible;
            Feedback.Info(_tcpApplyResultText, I18n.T("Preset cargado: {0} — revisá los valores y toca Aplicar en cada tarjeta.", gaming ? I18n.T("Óptimo para juegos") : I18n.T("Valores por defecto (Windows)")));
        }
    }

    private async Task ApplyTcpAsync(string successMessage, TcpService.TcpState? desiredOverride = null, bool includeAutoTuning = false, bool quiet = false)
    {
        if (_tcpApplyResultText == null) return;
        _tcpApplyResultText.Visibility = Visibility.Visible;
        if (_tcpApplyButton != null) _tcpApplyButton.IsEnabled = false;
        if (!quiet) Feedback.Running(_tcpApplyResultText, I18n.T("Aplicando TCP..."));
        try
        {
            SaveTcpBackup(_tcpCurrent);
            var desired = desiredOverride ?? ReadTcpDesired();
            // La plantilla GLOBAL la aporta el estado leído: ni el deseado de los controles ni el
            // backup la llevan, y sin esto ResolveNetshName caería siempre a "internet" — que es
            // justo el bug que este arreglo cierra (el ajuste terminaría en una plantilla que el
            // stack quizá no esté usando).
            desired.GlobalTemplate = _tcpCurrent?.GlobalTemplate ?? "";
            var (ok, msg) = await TcpService.ApplyAsync(desired, includeAutoTuning);
            _lastTcpApplyOk = ok;
            _lastTcpApplyMsg = msg;
            if (!quiet)
            {
                if (ok) Feedback.Success(_tcpApplyResultText, successMessage);
                else Feedback.Error(_tcpApplyResultText, I18n.T("Error aplicando TCP: {0}", msg));
            }
            await RefreshTcpAsync();
            UpdateProfileIndicator();
        }
        catch (Exception ex)
        {
            Feedback.Error(_tcpApplyResultText, ex.Message);
            _loggingService.LogError("Error aplicando TCP", ex);
        }
        finally
        {
            // Se vuelve a habilitar SOLO si la última lectura fue buena: si el estado no se pudo
            // leer, los controles son defaults y no deben poder escribirse.
            if (_tcpApplyButton != null) _tcpApplyButton.IsEnabled = _tcpReadOk;
        }
    }

    private async Task RestoreTcpAsync()
    {
        var backup = LoadTcpBackup();
        if (backup == null)
        {
            if (_tcpApplyResultText != null)
            {
                _tcpApplyResultText.Visibility = Visibility.Visible;
                Feedback.Info(_tcpApplyResultText, I18n.T("No hay backup guardado todavía."));
            }
            return;
        }
        await ApplyTcpAsync(I18n.T("Estado TCP anterior restaurado."), backup);
    }

    private async Task RefreshTcpAsync()
    {
        var state = await TcpService.GetStateAsync();
        if (state == null) return;
        _tcpCurrent = state;
        // Recuperamos el estado real: si la lectura inicial había fallado, Aplicar puede volver
        // (el finally de ApplyTcpAsync es el que rehabilita el botón).
        _tcpReadOk = true;

        // Actualizar etiquetas "Actual:" en el lugar (no se reconstruye la card,
        // así el mensaje de resultado queda visible).
        if (_tcpActualNagle != null) _tcpActualNagle.Text = NagleActual(state);
        if (_tcpActualCongestion != null) _tcpActualCongestion.Text = CongestionActual(state.CongestionProvider);
        if (_tcpActualEcn != null) _tcpActualEcn.Text = ActualText(state.EcnEnabled);
        if (_tcpActualTimestamps != null) _tcpActualTimestamps.Text = ActualText(state.TimestampsEnabled);
        if (_tcpActualRss != null) _tcpActualRss.Text = ActualText(state.RssEnabled);
        if (_tcpActualFastOpen != null) _tcpActualFastOpen.Text = ActualText(state.FastOpenEnabled);
        if (_tcpAutoTuningText != null) _tcpAutoTuningText.Text = AutoTuningActual(state.AutoTuningLevel);
        if (_tcpAutoTuningFixButton != null)
            _tcpAutoTuningFixButton.Visibility = state.AutoTuningLevel.Equals("normal", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed : Visibility.Visible;
        if (_tcpMtuText != null) _tcpMtuText.Text = MtuActual(state);

        // Sincronizar los controles con el estado real aplicado
        if (_tcpNagleToggle != null) _tcpNagleToggle.IsOn = state.NoDelaySet || state.AckFreqSet;
        if (_tcpCongestionCombo != null) _tcpCongestionCombo.SelectedIndex = state.CongestionProvider == "ctcp" ? 1 : 0;
        if (_tcpEcnToggle != null) _tcpEcnToggle.IsOn = state.EcnEnabled;
        if (_tcpTimestampsToggle != null) _tcpTimestampsToggle.IsOn = state.TimestampsEnabled;
        if (_tcpRssToggle != null) _tcpRssToggle.IsOn = state.RssEnabled;
        if (_tcpFastOpenToggle != null) _tcpFastOpenToggle.IsOn = state.FastOpenEnabled;
    }

    /// <summary>
    /// Guarda el estado PREVIO a tocar nada. Solo escribe si todavía no hay backup: es la foto del
    /// estado original de Windows. Si se sobrescribiera en cada aplicación, "Restaurar todo" terminaría
    /// devolviendo el preset anterior en vez del estado de fábrica — que es justo lo que el botón promete.
    /// </summary>
    private void SaveTcpBackup(TcpService.TcpState? state)
    {
        if (state == null) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(_settingsService.Get<string>(TcpBackupKey, ""))) return;

            var dto = new TcpBackupDto
            {
                Congestion = state.CongestionProvider,
                Ecn = state.EcnEnabled,
                Timestamps = state.TimestampsEnabled,
                Rss = state.RssEnabled,
                FastOpen = state.FastOpenEnabled,
                // "Cualquiera de los dos" = el usuario tenía Nagle desactivado (aunque fuera a
                // medias): restaurar escribe el par completo en vez de volver a activarle Nagle.
                NagleDisabled = state.NoDelaySet || state.AckFreqSet
            };
            _settingsService.Set(TcpBackupKey, JsonSerializer.Serialize(dto));
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error guardando backup TCP", ex);
        }
    }

    private TcpService.TcpState? LoadTcpBackup()
    {
        try
        {
            var json = _settingsService.Get<string>(TcpBackupKey, "");
            if (string.IsNullOrWhiteSpace(json)) return null;
            var dto = JsonSerializer.Deserialize<TcpBackupDto>(json);
            if (dto == null) return null;
            return new TcpService.TcpState
            {
                CongestionProvider = dto.Congestion ?? "cubic",
                EcnEnabled = dto.Ecn,
                TimestampsEnabled = dto.Timestamps,
                RssEnabled = dto.Rss,
                FastOpenEnabled = dto.FastOpen,
                NagleDisabled = dto.NagleDisabled
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ActualText(bool value)
        => I18n.T("Actual: {0}", value ? I18n.T("Activado") : I18n.T("Desactivado"));

    /// <summary>
    /// Estado de la fila "Desactivar Nagle". El tweak escribe DOS valores (TCPNoDelay y
    /// TcpAckFrequency), así que hay un estado intermedio real: con uno solo, Nagle ya está
    /// desactivado pero falta la otra mitad (el toggle se ve activado). Se nombra en vez de caer a
    /// "Desactivado", que decía lo contrario de lo que pasaba.
    /// </summary>
    private static string NagleActual(TcpService.TcpState state)
    {
        if (state.NoDelaySet && state.AckFreqSet)
            return I18n.T("Actual: {0}", I18n.T("Activado (TCPNoDelay + TcpAckFrequency)"));
        if (state.NoDelaySet)
            return I18n.T("Actual: {0}", I18n.T("Activado a medias (solo {0})", "TCPNoDelay"));
        if (state.AckFreqSet)
            return I18n.T("Actual: {0}", I18n.T("Activado a medias (solo {0})", "TcpAckFrequency"));
        return ActualText(false);
    }

    private static string CongestionActual(string provider)
    {
        // Si el real es dctcp/bbr2/newreno/compound/illinois se dice el nombre: antes caía todo a
        // "CUBIC (predeterminado)" y mentía en cualquier máquina que no tuviera cubic.
        string name = provider.ToLowerInvariant() switch
        {
            "ctcp" => "CTCP",
            "dctcp" => "DCTCP",
            "bbr2" => "BBR2",
            "newreno" => "NewReno",
            "compound" => "Compound",
            "illinois" => "Illinois",
            _ => I18n.T("CUBIC (predeterminado)")
        };
        return I18n.T("Actual: {0}", name);
    }

    private static string AutoTuningActual(string level)
    {
        string label = level switch
        {
            "disabled" => I18n.T("Deshabilitado"),
            "highlyrestricted" => I18n.T("Muy restringido"),
            "restricted" => I18n.T("Restringido"),
            "experimental" => I18n.T("Experimental"),
            _ => I18n.T("Normal (recomendado)")
        };
        var text = I18n.T("Actual: {0}", label);
        if (level == "disabled")
            text += " · " + I18n.T("Aviso: desactivar el ajuste automático puede reducir la velocidad de descarga. Se recomienda dejarlo en Normal.");
        return text;
    }

    private static string MtuActual(TcpService.TcpState state)
    {
        if (state.MtuList.Count == 0) return I18n.T("MTU: {0}", "--");
        return I18n.T("MTU: {0}", string.Join(", ", state.MtuList.Select(m => $"{m.Name} {m.Mtu}")));
    }

    /// <summary>
    /// Fila de ajuste: título + subtítulo/actual a la izquierda, control a la derecha.
    /// tooltip: texto (clave de traducción) que explica qué hace y cuándo conviene ON/OFF.
    /// Al lado del título se muestra un botón "?" con el mismo estilo que Optimizaciones:
    /// tooltip custom (título en negrita + descripción), colocado abajo del botón.
    /// </summary>
    private static (Grid Row, TextBlock Actual) BuildSettingRow(string title, string? subtitle, string? actual, FrameworkElement control, string? tooltip = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 2 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        titleRow.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(tooltip))
            titleRow.Children.Add(BuildInfoButton(title, tooltip));
        left.Children.Add(titleRow);
        if (!string.IsNullOrEmpty(subtitle))
            left.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap });
        TextBlock? actualTb = null;
        if (!string.IsNullOrEmpty(actual))
        {
            actualTb = new TextBlock { Text = actual, FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
            left.Children.Add(actualTb);
        }
        grid.Children.Add(left);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return (grid, actualTb ?? new TextBlock());
    }

    /// <summary>Título con botón "?" de info (estilo Optimizaciones).</summary>
    private static StackPanel BuildInfoTitle(string text, string tooltip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(BuildInfoButton(text, tooltip));
        return row;
    }

    /// <summary>
    /// Botón "?" chico con tooltip custom: título en negrita arriba, descripción abajo
    /// (mismo estilo que BuildInfoButton de OptimizacionesPage).
    /// </summary>
    private static Button BuildInfoButton(string title, string tooltipBody)
    {
        var infoButton = new Button
        {
            Content = "?",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            MinWidth = 22,
            MaxWidth = 22,
            Height = 22,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Foreground = MutedTextBrush,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel { Spacing = 6, MaxWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = I18n.T(tooltipBody),
            FontSize = 12,
            Foreground = MutedTextBrush,
            TextWrapping = TextWrapping.Wrap
        });

        ToolTipService.SetToolTip(infoButton, new ToolTip
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom,
            Content = content
        });
        return infoButton;
    }

    // ===== Tooltips: qué hace cada ajuste y cuándo conviene ON/OFF =====
    private const string TtNagle = "Nagle agrupa paquetes pequeños para reducir el overhead de red. Desactivarlo (TCPNoDelay + TcpAckFrequency = 1) baja la latencia de apps que envían muchos paquetes chicos: juegos online, RDP, SSH, voz. Costo: un poco más de overhead. → ON (desactivar) para juegos; OFF si solo navegás o descargás.";
    private const string TtCongestion = "Cómo reacciona el stack TCP a la congestión. CUBIC: el default moderno de Windows, buen balance. CTCP (Compound TCP): más agresivo en redes con pérdida, el clásico 'gamer'. → Probá CTCP si notás latencia o pérdida en juegos; CUBIC para uso general.";
    private const string TtEcn = "Marca los paquetes congestionados en vez de descartarlos. Desactivarlo evita problemas con routers o ISPs viejos que lo manejan mal (jitter o pérdida de paquetes). → OFF para juegos; ON solo si sabés que tu router lo soporta bien.";
    private const string TtTimestamps = "Agregan un timestamp a los paquetes para medir el RTT. Desactivarlos reduce un poco el overhead del stack y el tamaño de cada paquete. → OFF en juegos (ganancia marginal); ON en descargas masivas.";
    private const string TtRss = "Distribuye el procesamiento de paquetes entre varios núcleos de CPU. → ON si tu adaptador lo soporta (mejor throughput multihilo); los adaptadores viejos lo ignoran.";
    private const string TtFastOpen = "Permite enviar datos en el primer SYN: el handshake es más corto y baja la latencia de conexiones nuevas (juegos, navegación). → ON recomendado en general.";
    private const string TtAutoTuning = "Windows ajusta solo el tamaño de la ventana TCP. 'Normal' es lo recomendado: desactivarlo puede bajar la latencia con routers malos, pero suele reducir la velocidad de descarga.";
    private const string TtMtu = "Tamaño máximo de paquete. 1500 es el estándar Ethernet; con PPPoE (fibra con login) lo correcto es 1492. Un MTU incorrecto causa fragmentación o pérdida de paquetes.";

    // ===== Tooltips: reglas de Windows (red) — el servicio pasa la clave, acá va el texto =====
    private const string TtNetNti = "Windows reserva ancho de banda para apps multimedia y limita la red a 10 paquetes por milisegundo (ese es el default, valor 10). Desactivarlo (0xFFFFFFFF) quita el tope: bueno para juegos y streaming cuando el PC además reproduce audio o video. → Sin límite para juegos; default de Windows en otros casos.";
    private const string TtNetSysResp = "Porcentaje de CPU que Windows reserva para las tareas de fondo cuando una app multimedia está activa (default 20%). Bajarlo a 10 le da más CPU a esa app: es el valor más bajo que Windows acepta, porque los menores a 10 se vuelven a 20 automáticamente —por eso este ajuste NO trae un «0 %»: no haría nada. Solo nota la diferencia si el juego o el reproductor usan el planificador multimedia de Windows, y la mayoría de los juegos no. → 10 para juegos; 20 (default) en cualquier otro caso.";
    private const string TtNetMaxPort = "Puerto más alto del rango dinámico que Windows usa para las conexiones salientes. Desde Windows 10 el rango por defecto ya es 49152-65535 (unos 16 mil puertos), así que este ajuste suele estar de más: el agotamiento de puertos aparece con decenas de miles de conexiones simultáneas, no usando varios juegos. El rango completo se cambia con «netsh int ipv4 set dynamicport tcp» desde la terminal. → Dejalo como está salvo que midas un agotamiento real.";
    private const string TtNetTimedWait = "Segundos que una conexión cerrada sigue ocupando su puerto antes de liberarlo (default 120 s, y Windows no escribe el valor: lo aplica internamente). Bajarlo a 30 libera los puertos antes. Sirve solo si agotás puertos con miles de conexiones cortas; en uso normal no se nota y, si se exagera, un paquete atrasado puede confundir a una conexión nueva. → Dejalo como está salvo que midas un agotamiento real.";
    private const string TtNetLargeCache = "Le dice a Windows que priorice el caché del sistema de archivos en RAM (modo servidor) en vez de la memoria de las apps (default). Puede mejorar el throughput de red con mucha RAM; con poca RAM puede causar tirones. → ON con 16+ GB de RAM y uso intenso de red; OFF (default) en otros casos.";
    private const string TtNetLso = "El valor que escribe este ajuste es DisableTaskOffload: 1 APAGA toda la descarga al hardware de la interfaz activa (segmentación LSO, checksum y offload de RSS), no solo LSO — por eso la opción se llama «Desactivar la descarga» y no «activar LSO». Con la descarga apagada ese trabajo lo hace la CPU: sube el uso de CPU y en algunos drivers bajan los picos de latencia. → Dejá «Descarga activada» (el predeterminado) salvo que midas micro-tirones en juegos online.";

    // ===== Tooltips: plantillas TCP =====
    private const string TtTemplates = "Las plantillas del stack TCP (internet, personalizada, datacenter…) agrupan las perillas finas de Windows: RTO mínimo, ventana de congestión inicial, delayed ACK, RACK y sondeo de pérdida de cola. Son las que usan los tweakers. → Tocá una por vez y probá el efecto en tus partidas antes de dejarla fija.";
    private const string TtCongestionProvider = "Cómo reacciona TCP a la congestión: CUBIC (default moderno), CTCP (más agresivo con pérdida), DCTCP (para redes con ECN) y BBR2 (experimental, solo Windows 11). → Medí antes y después: no hay un ganador universal.";
    private const string TtMinRto = "Tiempo mínimo antes de retransmitir un segmento perdido. Más bajo reacciona antes a la pérdida, pero puede retransmitir de más en redes con jitter. Windows usa 300 ms; los perfiles de baja latencia bajan a 20-100. → Bajalo si medís pérdida y querés recuperación rápida.";
    private const string TtIcw = "Cuántos segmentos puede enviar TCP al arrancar una conexión (Initial Congestion Window). Más alto = la conexión llega a velocidad útil antes (mejor para descargas y para el primer segundo de una partida). → 10 es el default; 12-16 para baja latencia.";
    private const string TtDelayedAck = "Delayed ACK: Windows espera a juntar confirmaciones para ahorrar paquetes. Bajarlo (o poner frecuencia 1) confirma al instante como el tweak clásico de TcpAckFrequency, pero desde la plantilla y sin tocar el registro de la interfaz. → Bajalo en juegos con muchos paquetes chicos; dejalo alto en descargas.";
    private const string TtMaxSyn = "Cuántas veces reintenta el handshake SYN antes de dar la conexión por caída. Más alto tolera mejor una red con pérdida, pero tarda más en fallar. → 4 es el default de Windows.";
    private const string TtNonSack = "Resistencia a la pérdida de ACKs cuando el otro extremo no usa SACK. Activala solo si ves reconexiones raras en una red vieja. → OFF por defecto.";
    private const string TtRack = "RACK y sondeo de pérdida de cola: recuperación de pérdidas basada en tiempo. Windows los trae activados y Microsoft recomienda dejarlos así (funcionan mejor juntos). → Dejalos ON salvo que tus pruebas muestren lo contrario.";

    /// <summary>Clave del servicio → texto español del tooltip (misma mecánica que TtNagle).</summary>
    private static string RuleTooltip(string key) => key switch
    {
        "TtNetNti" => TtNetNti,
        "TtNetSysResp" => TtNetSysResp,
        "TtNetMaxPort" => TtNetMaxPort,
        "TtNetTimedWait" => TtNetTimedWait,
        "TtNetLargeCache" => TtNetLargeCache,
        "TtNetLso" => TtNetLso,
        _ => key
    };

    private static ToggleSwitch NewToggle()
        => new() { OnContent = "", OffContent = "" };

    /// <summary>TextBox numérico chico (para las perillas de la plantilla TCP).</summary>
    private static TextBox NewNumberBox(string value, double minWidth = 90)
        => new() { Text = value, MinWidth = minWidth, MaxWidth = minWidth + 40 };

    // =====================================================================
    // Fase B: plantillas TCP (netsh supplemental + Set-NetTCPSetting)
    // =====================================================================

    private List<TcpTemplateState> _tplStates = new();
    private ComboBox? _tplSelector;
    private ComboBox? _tplCongestion;
    private ComboBox? _tplAutoTuning;
    private TextBox? _tplMinRto;
    private TextBox? _tplIcw;
    private TextBox? _tplAckTimeout;
    private TextBox? _tplAckFreq;
    private TextBox? _tplMaxSyn;
    private ToggleSwitch? _tplEcn;
    private ToggleSwitch? _tplTimestamps;
    private ToggleSwitch? _tplNonSack;
    private ToggleSwitch? _tplCwndRestart;
    private ToggleSwitch? _tplRack;
    private ToggleSwitch? _tplTailLoss;
    private TextBlock? _tplStatus;
    private TextBlock? _tplResult;
    private TextBlock? _tplGlobalNote;
    private StackPanel? _tplFields;
    private Button? _tplApplyButton;
    private Button? _tplRestoreButton;

    private async Task AppendTcpTemplateSectionAsync(StackPanel host)
    {
        var section = new StackPanel { Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        section.Children.Add(new Rectangle { Height = 1, Fill = ThemeBrushes.Get("CardBorderBrush"), Margin = new Thickness(0, 2, 0, 6) });
        section.Children.Add(BuildInfoTitle(I18n.T("Plantillas TCP (avanzado)"), TtTemplates));
        section.Children.Add(new TextBlock
        {
            Text = I18n.T("Editor de la plantilla del stack: RTO mínimo, congestión inicial, delayed ACK, RACK y sondeo de pérdida de cola. Son las perillas que usan los tweakers y que Windows no expone en el panel normal."),
            FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap
        });

        _tplStatus = new TextBlock { Text = I18n.T("Consultando plantillas TCP..."), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        section.Children.Add(_tplStatus);
        Feedback.Running(_tplStatus, I18n.T("Consultando plantillas TCP..."), persistent: true);

        _tplStates = await TcpTemplateService.GetTemplatesAsync();
        if (_tplStates.Count == 0)
        {
            ResolveStatusWithError(_tplStatus, I18n.T("No se pudieron leer las plantillas TCP."), BuildTcpAdvancedCardAsync);
            host.Children.Add(section);
            return;
        }
        _tplStatus.Visibility = Visibility.Collapsed;

        _tplSelector = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var t in _tplStates)
            _tplSelector.Items.Add(new ComboBoxItem
            {
                Content = I18n.T(TcpTemplateService.LabelFor(t.Name))
                          + (t.IsGlobalDefault ? " · " + I18n.T("Actual") : "")
                          + (t.HasData ? "" : " · " + I18n.T("gestionada por Windows")),
                Tag = t.Name
            });
        _tplSelector.SelectionChanged += (s, e) => PopulateTemplateFields();
        section.Children.Add(_tplSelector);

        _tplFields = new StackPanel { Spacing = 10 };
        section.Children.Add(_tplFields);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var apply = new Button { Content = I18n.T("Aplicar"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        apply.Click += async (s, e) => await ApplyTemplateAsync();
        var restore = new Button { Content = I18n.T("Restaurar plantilla"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        restore.Click += async (s, e) => await RestoreTemplateAsync();
        _tplApplyButton = apply;
        _tplRestoreButton = restore;
        buttons.Children.Add(apply);
        buttons.Children.Add(restore);
        section.Children.Add(buttons);

        _tplResult = new TextBlock { Text = "", FontSize = 12, Visibility = Visibility.Collapsed, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        section.Children.Add(_tplResult);

        host.Children.Add(section);

        var defaultIndex = _tplStates.FindIndex(t => t.IsGlobalDefault);
        _tplSelector.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;
        PopulateTemplateFields();
    }

    private TcpTemplateState? SelectedTemplate()
    {
        if (_tplSelector?.SelectedItem is ComboBoxItem { Tag: string name })
            return _tplStates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>Vuelca el estado leído en los controles (se llama al cambiar de plantilla).</summary>
    private void PopulateTemplateFields()
    {
        if (_tplFields == null) return;
        var t = SelectedTemplate();
        if (t == null) return;
        _tplFields.Children.Clear();

        // "Automatic" no expone valores: la administra Windows según la red
        // (Get-NetTCPSetting la devuelve vacía), así que no hay nada que editar.
        if (!t.HasData)
        {
            if (_tplApplyButton != null) _tplApplyButton.IsEnabled = false;
            if (_tplRestoreButton != null) _tplRestoreButton.IsEnabled = !string.IsNullOrWhiteSpace(_settingsService.Get(TemplateBackupKey(t.Name), ""));
            _tplFields.Children.Add(new TextBlock
            {
                Text = I18n.T("Windows administra esta plantilla según las condiciones de la red: no tiene valores editables."),
                FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        if (_tplApplyButton != null) _tplApplyButton.IsEnabled = true;
        if (_tplRestoreButton != null) _tplRestoreButton.IsEnabled = !string.IsNullOrWhiteSpace(_settingsService.Get(TemplateBackupKey(t.Name), ""));

        _tplCongestion = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        // Win10 no acepta bbr2 en "netsh set supplemental" (solo none|ctcp|dctcp|default, según el
        // help oficial): se ofrece solo donde funciona. Sin este filtro, elegirlo en Win10 corría un
        // comando que falla y el mensaje que llegaba era "ERRORS: supplemental;", sin explicación.
        foreach (var p in TcpTemplateService.CongestionProviders)
        {
            if (p == "bbr2" && !WindowsCapabilities.IsWindows11) continue;
            _tplCongestion.Items.Add(new ComboBoxItem { Content = I18n.T(CongestionLabel(p)), Tag = p });
        }
        var currentProvider = t.CongestionProvider.ToLowerInvariant() switch
        {
            "ctcp" => "ctcp",
            "dctcp" => "dctcp",
            "bbr2" => "bbr2",
            _ => "default",
        };
        _tplCongestion.SelectedItem = _tplCongestion.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == currentProvider);
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Proveedor de congestión"), null, I18n.T("Actual: {0}", t.CongestionProvider), _tplCongestion, TtCongestionProvider).Row);

        _tplMinRto = NewNumberBox(t.MinRto.ToString());
        _tplFields.Children.Add(BuildSettingRow(I18n.T("RTO mínimo (ms)"), I18n.T("20 a 300"), null, _tplMinRto, TtMinRto).Row);

        _tplIcw = NewNumberBox(t.InitialCongestionWindow.ToString());
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Ventana de congestión inicial (MSS)"), I18n.T("2 a 64"), null, _tplIcw, TtIcw).Row);

        _tplAckTimeout = NewNumberBox(t.DelayedAckTimeout.ToString());
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Delayed ACK: espera (ms)"), I18n.T("10 a 600 · más bajo = ACKs más rápidos"), null, _tplAckTimeout, TtDelayedAck).Row);

        _tplAckFreq = NewNumberBox(t.DelayedAckFrequency.ToString());
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Delayed ACK: frecuencia"), I18n.T("1 a 255 · 1 = ACK inmediato por segmento"), null, _tplAckFreq, TtDelayedAck).Row);

        _tplMaxSyn = NewNumberBox(t.MaxSynRetransmissions.ToString());
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Retransmisiones SYN máximas"), null, null, _tplMaxSyn, TtMaxSyn).Row);

        _tplAutoTuning = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, label) in new[]
                 {
                     ("normal", "Normal (recomendado)"),
                     ("restricted", "Restringido"),
                     ("highlyrestricted", "Muy restringido"),
                     ("experimental", "Experimental"),
                     ("disabled", "Deshabilitado"),
                 })
            _tplAutoTuning.Items.Add(new ComboBoxItem { Content = I18n.T(label), Tag = value });
        _tplAutoTuning.SelectedItem = _tplAutoTuning.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == (t.AutoTuningLevel ?? "").ToLowerInvariant());
        _tplAutoTuning.SelectedItem ??= _tplAutoTuning.Items[0];
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Ajuste automático de la ventana TCP"), null, null, _tplAutoTuning, TtAutoTuning).Row);

        _tplEcn = NewToggle();
        _tplEcn.IsOn = t.EcnEnabled;
        _tplFields.Children.Add(BuildSettingRow(I18n.T("ECN (Notificación de congestión explícita)"), null, null, _tplEcn, TtEcn).Row);

        _tplTimestamps = NewToggle();
        _tplTimestamps.IsOn = t.TimestampsEnabled;
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Timestamps TCP (RFC 1323)"), null, null, _tplTimestamps, TtTimestamps).Row);

        _tplNonSack = NewToggle();
        _tplNonSack.IsOn = t.NonSackRttResiliency;
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Resistencia RTT sin SACK"), null, null, _tplNonSack, TtNonSack).Row);

        _tplCwndRestart = NewToggle();
        _tplCwndRestart.IsOn = t.CwndRestart ?? false;
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Reinicio de ventana de congestión"), null, null, _tplCwndRestart, TtRack).Row);

        _tplRack = NewToggle();
        _tplRack.IsOn = t.Rack ?? true;
        _tplFields.Children.Add(BuildSettingRow(I18n.T("RACK (recuperación por tiempo)"), null, null, _tplRack, TtRack).Row);

        _tplTailLoss = NewToggle();
        _tplTailLoss.IsOn = t.TailLossProbe ?? true;
        _tplFields.Children.Add(BuildSettingRow(I18n.T("Sondeo de pérdida de cola"), null, null, _tplTailLoss, TtRack).Row);

        if (!t.IsGlobalDefault)
        {
            _tplGlobalNote = new TextBlock
            {
                Text = I18n.T("Solo la plantilla global expone RACK, el sondeo de pérdida de cola y el reinicio de ventana: en las demás no se pueden leer ni aplicar desde acá."),
                FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap
            };
            _tplFields.Children.Add(_tplGlobalNote);
        }
    }

    private static string CongestionLabel(string provider) => provider switch
    {
        "ctcp" => "CTCP (más agresivo con pérdida)",
        "dctcp" => "DCTCP (datacenter, ECN)",
        "bbr2" => "BBR2 (experimental, Windows 11)",
        _ => "Default (CUBIC)",
    };

    private async Task ApplyTemplateAsync()
    {
        var t = SelectedTemplate();
        if (t == null || _tplResult == null) return;
        if (!t.HasData) return;
        SaveTemplateBackup(t);
        _tplResult.Visibility = Visibility.Visible;
        Feedback.Running(_tplResult, I18n.T("Aplicando plantilla TCP..."));
        try
        {
            var provider = (_tplCongestion?.SelectedItem as ComboBoxItem)?.Tag as string ?? "default";
            var (ok, msg) = await TcpTemplateService.ApplyAsync(
                t.Name,
                provider,
                ParseInt(_tplMinRto?.Text), ParseInt(_tplIcw?.Text),
                ParseInt(_tplAckTimeout?.Text), ParseInt(_tplAckFreq?.Text),
                _tplEcn?.IsOn, _tplTimestamps?.IsOn,
                (_tplAutoTuning?.SelectedItem as ComboBoxItem)?.Tag as string,
                ParseInt(_tplMaxSyn?.Text), _tplNonSack?.IsOn,
                t.IsGlobalDefault ? _tplCwndRestart?.IsOn : null,
                t.IsGlobalDefault ? _tplRack?.IsOn : null,
                t.IsGlobalDefault ? _tplTailLoss?.IsOn : null);
            if (ok) Feedback.Success(_tplResult, I18n.T("Plantilla TCP aplicada. Algunos cambios solo afectan conexiones nuevas."));
            else Feedback.Error(_tplResult, I18n.T("No se pudieron aplicar los cambios: {0}", msg));
            await RefreshTemplatesAsync();
        }
        catch (Exception ex)
        {
            Feedback.Error(_tplResult, ex.Message);
            _loggingService.LogError("TcpPage: error aplicando plantilla TCP", ex);
        }
    }

    private async Task RestoreTemplateAsync()
    {
        var t = SelectedTemplate();
        if (t == null || _tplResult == null) return;
        var json = _settingsService.Get(TemplateBackupKey(t.Name), "");
        if (string.IsNullOrWhiteSpace(json))
        {
            _tplResult.Visibility = Visibility.Visible;
            Feedback.Info(_tplResult, I18n.T("No hay backup de esta plantilla todavía."));
            return;
        }
        try
        {
            var b = JsonSerializer.Deserialize<TemplateBackupDto>(json);
            if (b == null) return;
            _tplResult.Visibility = Visibility.Visible;
            Feedback.Running(_tplResult, I18n.T("Restaurando plantilla..."));
            var (ok, msg) = await TcpTemplateService.ApplyAsync(
                t.Name, b.Provider, b.MinRto, b.Icw, b.AckTimeout, b.AckFreq,
                b.Ecn, b.Timestamps, b.AutoTuning, b.MaxSyn, b.NonSack, b.CwndRestart, b.Rack, b.TailLoss);
            if (ok) Feedback.Success(_tplResult, I18n.T("Plantilla anterior restaurada."));
            else Feedback.Error(_tplResult, I18n.T("No se pudieron aplicar los cambios: {0}", msg));
            await RefreshTemplatesAsync();
        }
        catch (Exception ex)
        {
            Feedback.Error(_tplResult, ex.Message);
        }
    }

    /// <summary>Relee las plantillas y repuebla los controles sin reconstruir la card.</summary>
    private async Task RefreshTemplatesAsync()
    {
        var name = SelectedTemplate()?.Name;
        _tplStates = await TcpTemplateService.GetTemplatesAsync();
        if (_tplStates.Count == 0 || _tplSelector == null) return;
        var item = _tplSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == name);
        if (item != null) _tplSelector.SelectedItem = item;
        PopulateTemplateFields();
    }

    private void SaveTemplateBackup(TcpTemplateState t)
    {
        try
        {
            var dto = new TemplateBackupDto
            {
                Provider = t.CongestionProvider.ToLowerInvariant() switch
                {
                    "ctcp" => "ctcp",
                    "dctcp" => "dctcp",
                    "bbr2" => "bbr2",
                    _ => "default",
                },
                MinRto = t.MinRto,
                Icw = t.InitialCongestionWindow,
                AckTimeout = t.DelayedAckTimeout,
                AckFreq = t.DelayedAckFrequency,
                Ecn = t.EcnEnabled,
                Timestamps = t.TimestampsEnabled,
                AutoTuning = string.IsNullOrWhiteSpace(t.AutoTuningLevel) ? null : t.AutoTuningLevel.ToLowerInvariant(),
                MaxSyn = t.MaxSynRetransmissions,
                NonSack = t.NonSackRttResiliency,
                CwndRestart = t.CwndRestart,
                Rack = t.Rack,
                TailLoss = t.TailLossProbe,
            };
            _settingsService.Set(TemplateBackupKey(t.Name), JsonSerializer.Serialize(dto));
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _loggingService.LogError("TcpPage: error guardando backup de plantilla", ex);
        }
    }

    private static string TemplateBackupKey(string name) => "red.tcptpl.backup." + name.ToLowerInvariant();

    private static int? ParseInt(string? text)
        => int.TryParse((text ?? "").Trim(), out var n) ? n : null;

    private sealed class TemplateBackupDto
    {
        public string? Provider { get; set; }
        public int? MinRto { get; set; }
        public int? Icw { get; set; }
        public int? AckTimeout { get; set; }
        public int? AckFreq { get; set; }
        public bool? Ecn { get; set; }
        public bool? Timestamps { get; set; }
        public string? AutoTuning { get; set; }
        public int? MaxSyn { get; set; }
        public bool? NonSack { get; set; }
        public bool? CwndRestart { get; set; }
        public bool? Rack { get; set; }
        public bool? TailLoss { get; set; }
    }

    private sealed class TcpBackupDto
    {
        public string? Congestion { get; set; }
        public bool Ecn { get; set; }
        public bool Timestamps { get; set; }
        public bool Rss { get; set; }
        public bool FastOpen { get; set; }
        public bool NagleDisabled { get; set; }
    }
}
