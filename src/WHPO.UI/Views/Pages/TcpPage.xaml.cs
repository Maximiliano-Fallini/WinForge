using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
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
            Background = ThemeBrushes.Get("CardBackgroundBrush"),
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
        if (state == null)
            ResolveStatusWithError(_tcpStatusText, I18n.T("No se pudo leer el estado TCP: {0}", "netsh"));

        // Presets primero (antes de las opciones): un clic aplica todo el perfil
        var presetsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var gamingBtn = new Button { Content = I18n.T("Óptimo para juegos"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        gamingBtn.Click += async (s, e) => ApplyTcpPreset(gaming: true);
        var defaultBtn = new Button { Content = I18n.T("Valores por defecto (Windows)"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        defaultBtn.Click += async (s, e) => ApplyTcpPreset(gaming: false);
        presetsRow.Children.Add(gamingBtn);
        presetsRow.Children.Add(defaultBtn);
        panel.Children.Add(presetsRow);

        // Desactivar Nagle (TCPNoDelay + TcpAckFrequency en la interfaz activa)
        _tcpNagleToggle = NewToggle();
        _tcpNagleToggle.IsOn = _tcpCurrent.NagleDisabled;
        var (nagleRow, nagleActual) = BuildSettingRow(
            I18n.T("Desactivar Nagle"), I18n.T("TCPNoDelay + TcpAckFrequency"), ActualText(_tcpCurrent.NagleDisabled), _tcpNagleToggle, TtNagle);
        _tcpActualNagle = nagleActual;
        panel.Children.Add(nagleRow);

        // Algoritmo de congestión (CUBIC / CTCP)
        _tcpCongestionCombo = new ComboBox { MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Stretch };
        _tcpCongestionCombo.Items.Add(new ComboBoxItem { Content = I18n.T("CUBIC (predeterminado)"), Tag = "cubic" });
        _tcpCongestionCombo.Items.Add(new ComboBoxItem { Content = I18n.T("CTCP (menor latencia)"), Tag = "ctcp" });
        _tcpCongestionCombo.SelectedIndex = _tcpCurrent.CongestionProvider == "ctcp" ? 1 : 0;
        var (congestionRow, congestionActual) = BuildSettingRow(
            I18n.T("Algoritmo de congestión"), null, CongestionActual(_tcpCurrent.CongestionProvider), _tcpCongestionCombo, TtCongestion);
        _tcpActualCongestion = congestionActual;
        panel.Children.Add(congestionRow);

        // ECN
        _tcpEcnToggle = NewToggle();
        _tcpEcnToggle.IsOn = _tcpCurrent.EcnEnabled;
        var (ecnRow, ecnActual) = BuildSettingRow(
            I18n.T("ECN (Notificación de congestión explícita)"), null, ActualText(_tcpCurrent.EcnEnabled), _tcpEcnToggle, TtEcn);
        _tcpActualEcn = ecnActual;
        panel.Children.Add(ecnRow);

        // Timestamps
        _tcpTimestampsToggle = NewToggle();
        _tcpTimestampsToggle.IsOn = _tcpCurrent.TimestampsEnabled;
        var (timestampsRow, timestampsActual) = BuildSettingRow(
            I18n.T("Timestamps TCP (RFC 1323)"), null, ActualText(_tcpCurrent.TimestampsEnabled), _tcpTimestampsToggle, TtTimestamps);
        _tcpActualTimestamps = timestampsActual;
        panel.Children.Add(timestampsRow);

        // RSS
        _tcpRssToggle = NewToggle();
        _tcpRssToggle.IsOn = _tcpCurrent.RssEnabled;
        var (rssRow, rssActual) = BuildSettingRow(
            I18n.T("RSS (Receive Side Scaling)"), null, ActualText(_tcpCurrent.RssEnabled), _tcpRssToggle, TtRss);
        _tcpActualRss = rssActual;
        panel.Children.Add(rssRow);

        // Fast Open
        _tcpFastOpenToggle = NewToggle();
        _tcpFastOpenToggle.IsOn = _tcpCurrent.FastOpenEnabled;
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
            _tcpAutoTuningFixButton = new Button { Content = I18n.T("Restaurar a Normal"), Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left };
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
        _tcpApplyButton = new Button { Content = I18n.T("Aplicar"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
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
            Text = I18n.T("Solo se incluyen ajustes con efecto real y reversibles. El ajuste automático de ventana TCP se mantiene en Normal para no afectar la descarga."),
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
    // CTTE en la rama "Windows Tweaks". Presets Rendimiento/Ecológico +
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

    private void LoadRulesPreset(bool gaming)
    {
        foreach (var st in _rulesCurrent)
        {
            if (!_rulesCombos.TryGetValue(st.Def.Id, out var combo)) continue;
            var want = gaming ? st.Def.Gaming : st.Def.Eco;
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == want);
            if (item != null) combo.SelectedItem = item;
        }
        if (_rulesResultText != null)
        {
            _rulesResultText.Visibility = Visibility.Visible;
            Feedback.Info(_rulesResultText, I18n.T("Preset cargado: {0} — revisá los valores y toca Aplicar.", gaming ? I18n.T("Rendimiento (juegos)") : I18n.T("Ecológico (Windows)")));
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
            Background = ThemeBrushes.Get("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16)
        };
        var rulesPanel = new StackPanel { Spacing = 12 };
        rulesCard.Child = rulesPanel;
        WindowsRulesPanel.Children.Add(rulesCard);

        _rulesStatusText = new TextBlock { Text = I18n.T("Consultando reglas de red de Windows..."), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        rulesPanel.Children.Add(_rulesStatusText);

        // Presets primero (mismo patrón que la card TCP): cargan, no aplican.
        var presetsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var perfBtn = new Button { Content = I18n.T("Rendimiento (juegos)"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        perfBtn.Click += (s, e) => LoadRulesPreset(gaming: true);
        var ecoBtn = new Button { Content = I18n.T("Ecológico (Windows)"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        ecoBtn.Click += (s, e) => LoadRulesPreset(gaming: false);
        presetsRow.Children.Add(perfBtn);
        presetsRow.Children.Add(ecoBtn);
        rulesPanel.Children.Add(presetsRow);

        _rulesCurrent = await NetworkTweaksService.GetStateAsync();
        if (_rulesCurrent.Count == 0)
            ResolveStatusWithError(_rulesStatusText, I18n.T("No se pudieron leer las reglas de red de Windows."), BuildWindowsRulesCardAsync);
        else if (_rulesStatusText != null)
            _rulesStatusText.Visibility = Visibility.Collapsed;

        foreach (var st in _rulesCurrent)
        {
            var combo = new ComboBox { MinWidth = 190, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.Items.Add(new ComboBoxItem { Content = I18n.T("Rendimiento"), Tag = st.Def.Gaming });
            combo.Items.Add(new ComboBoxItem { Content = I18n.T("Ecológico (Windows)"), Tag = st.Def.Eco });
            // Selección inicial: el valor actual si coincide con un preset; si no,
            // el valor de Windows (Eco) como punto de partida menos invasivo.
            if (st.RawValue != null && TryParseU32(st.RawValue, out var cur))
            {
                if (TryParseU32(st.Def.Gaming, out var g) && cur == g) combo.SelectedIndex = 0;
                else if (TryParseU32(st.Def.Eco, out var e) && cur == e) combo.SelectedIndex = 1;
                else combo.SelectedIndex = 1;
            }
            else combo.SelectedIndex = 1;
            _rulesCombos[st.Def.Id] = combo;

            var actualText = st.RawValue == null
                ? I18n.T("Default de Windows (sin valor en el registro)")
                : I18n.T("Actual: {0}", NetworkTweaksService.DisplayValue(st.Def, st.RawValue));
            var (row, actual) = BuildSettingRow(
                I18n.T(st.Def.Title),
                null,
                actualText,
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
    }

    private async Task ApplyRulesAsync()
    {
        if (_rulesResultText == null || _rulesCombos.Count == 0) return;
        _rulesResultText.Visibility = Visibility.Visible;
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
                Feedback.Success(_rulesResultText, I18n.T("Reglas de red aplicadas. Reiniciá Windows para que el cambio sea total."));
            else
                Feedback.Error(_rulesResultText, I18n.T("No se pudieron aplicar: {0}", string.Join(", ", failed)));
            await BuildWindowsRulesCardAsync();
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
            await BuildWindowsRulesCardAsync();
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
    /// Carga un preset en los controles (NO lo aplica): el usuario revisa los valores
    /// contra el estado actual y decide si toca "Aplicar".
    /// </summary>
    private void ApplyTcpPreset(bool gaming)
    {
        if (_tcpNagleToggle == null || _tcpCongestionCombo == null) return;
        _tcpNagleToggle.IsOn = gaming;                        // off en gaming
        _tcpCongestionCombo.SelectedIndex = gaming ? 1 : 0;   // CTCP / CUBIC
        if (_tcpEcnToggle != null) _tcpEcnToggle.IsOn = false;
        if (_tcpTimestampsToggle != null) _tcpTimestampsToggle.IsOn = !gaming;
        if (_tcpRssToggle != null) _tcpRssToggle.IsOn = true;
        if (_tcpFastOpenToggle != null) _tcpFastOpenToggle.IsOn = true;
        var name = gaming ? I18n.T("Óptimo para juegos") : I18n.T("Valores por defecto (Windows)");
        if (_tcpApplyResultText != null)
        {
            _tcpApplyResultText.Visibility = Visibility.Visible;
            Feedback.Info(_tcpApplyResultText, I18n.T("Preset cargado: {0} — revisá los valores y toca Aplicar.", name));
        }
    }

    private async Task ApplyTcpAsync(string successMessage, TcpService.TcpState? desiredOverride = null, bool includeAutoTuning = false)
    {
        if (_tcpApplyResultText == null) return;
        _tcpApplyResultText.Visibility = Visibility.Visible;
        if (_tcpApplyButton != null) _tcpApplyButton.IsEnabled = false;
        Feedback.Running(_tcpApplyResultText, I18n.T("Aplicando TCP..."));
        try
        {
            SaveTcpBackup(_tcpCurrent);
            var desired = desiredOverride ?? ReadTcpDesired();
            var (ok, msg) = await TcpService.ApplyAsync(desired, includeAutoTuning);
            if (ok) Feedback.Success(_tcpApplyResultText, successMessage);
            else Feedback.Error(_tcpApplyResultText, I18n.T("Error aplicando TCP: {0}", msg));
            await RefreshTcpAsync();
        }
        catch (Exception ex)
        {
            Feedback.Error(_tcpApplyResultText, ex.Message);
            _loggingService.LogError("Error aplicando TCP", ex);
        }
        finally
        {
            if (_tcpApplyButton != null) _tcpApplyButton.IsEnabled = true;
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

        // Actualizar etiquetas "Actual:" en el lugar (no se reconstruye la card,
        // así el mensaje de resultado queda visible).
        if (_tcpActualNagle != null) _tcpActualNagle.Text = ActualText(state.NagleDisabled);
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
        if (_tcpNagleToggle != null) _tcpNagleToggle.IsOn = state.NagleDisabled;
        if (_tcpCongestionCombo != null) _tcpCongestionCombo.SelectedIndex = state.CongestionProvider == "ctcp" ? 1 : 0;
        if (_tcpEcnToggle != null) _tcpEcnToggle.IsOn = state.EcnEnabled;
        if (_tcpTimestampsToggle != null) _tcpTimestampsToggle.IsOn = state.TimestampsEnabled;
        if (_tcpRssToggle != null) _tcpRssToggle.IsOn = state.RssEnabled;
        if (_tcpFastOpenToggle != null) _tcpFastOpenToggle.IsOn = state.FastOpenEnabled;
    }

    private void SaveTcpBackup(TcpService.TcpState? state)
    {
        if (state == null) return;
        var dto = new TcpBackupDto
        {
            Congestion = state.CongestionProvider,
            Ecn = state.EcnEnabled,
            Timestamps = state.TimestampsEnabled,
            Rss = state.RssEnabled,
            FastOpen = state.FastOpenEnabled,
            NagleDisabled = state.NagleDisabled
        };
        try
        {
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

    private static string CongestionActual(string provider)
        => I18n.T("Actual: {0}", provider == "ctcp" ? "CTCP" : I18n.T("CUBIC (predeterminado)"));

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
    private const string TtNetSysResp = "Porcentaje de CPU que Windows reserva para tareas de fondo (default 20%). Ponerlo en 0 da a multimedia/juegos la máxima prioridad de planificación. → 0 para juegos; 20 (default) si también usás el PC para trabajar.";
    private const string TtNetMaxPort = "Cantidad máxima de puertos dinámicos para conexiones salientes (default 5000, de la era XP). Subirlo a 65534 evita el 'agotamiento de puertos' al abrir y cerrar muchas conexiones rápido (torrents, scanners, varios juegos). → 65534 para uso pesado; 5000 si todo funciona bien.";
    private const string TtNetTimedWait = "Segundos que una conexión cerrada queda reservada antes de liberar el puerto (default 240). Bajarlo a 30 libera puertos mucho más rápido: útil con muchas conexiones cortas (juegos, navegación). → 30 para juegos; 240 (default) en otros casos.";
    private const string TtNetLargeCache = "Le dice a Windows que priorice el caché del sistema de archivos en RAM (modo servidor) en vez de la memoria de las apps (default). Puede mejorar el throughput de red con mucha RAM; con poca RAM puede causar tirones. → ON con 16+ GB de RAM y uso intenso de red; OFF (default) en otros casos.";
    private const string TtNetLso = "Permite al adaptador agrupar envíos grandes en menos paquetes para ahorrar CPU. En algunos drivers agrega picos de latencia. Desactivar el offload (DisableTaskOffload=1) obliga a la CPU a segmentar: más uso de CPU, latencia potencialmente menor. → Depende del driver: probá ON si ves micro-tirones en juegos online.";

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
        foreach (var p in TcpTemplateService.CongestionProviders)
            _tplCongestion.Items.Add(new ComboBoxItem { Content = I18n.T(CongestionLabel(p)), Tag = p });
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
