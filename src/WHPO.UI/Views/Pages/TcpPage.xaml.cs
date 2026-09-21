using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Services;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// TCP avanzado (antes era la sección 6 de la pestaña Red, separada en su propia
/// pestaña). Card única construida en código: presets de juego/Windows, Nagle,
/// algoritmo de congestión, ECN, timestamps, RSS, Fast Open, autotuning (solo
/// lectura) y MTU por interfaz. Backup y restauración incluidos.
/// </summary>
public sealed partial class TcpPage : Page
{
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private bool _dataLoaded;

    private TcpService.TcpState? _tcpCurrent;

    // Controles de la card del adaptador (velocidad/duplex, control de flujo, EEE...)
    private TextBlock? _adapterStatusText;
    private TextBlock? _adapterResultText;
    private Button? _adapterApplyButton;
    private readonly Dictionary<string, ComboBox> _adapterCombos = new();
    private readonly Dictionary<string, TextBlock> _adapterActual = new();
    // Selector de adaptador (cuando hay más de una interfaz física).
    private ComboBox? _adapterSelector;
    private List<AdapterAdvancedService.AdapterIface> _adapterIfaces = new();
    private string? _selectedAdapterGuid;
    private bool _adapterSelectorBuilding;

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
                    DebugText.Text = $"Error: {ex2.Message}";
            }
        });
    }

    private async Task BuildTcpAdvancedCardAsync()
    {
        if (TcpAdvancedPanel == null || AdapterPanel == null) return;
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
            Feedback.Error(_tcpStatusText, I18n.T("No se pudo leer el estado TCP: {0}", "netsh"));

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

        if (state != null)
        {
            Feedback.Set(_tcpStatusText, null);
            _tcpStatusText.Visibility = Visibility.Collapsed;
        }

        // Reglas de Windows (nivel SO) y card del adaptador: misma pasada, la
        // página se reconstruye entera al cambiar de idioma.
        await BuildWindowsRulesCardAsync();
        await BuildAdapterCardAsync();
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
        if (_rulesStatusText != null) _rulesStatusText.Visibility = Visibility.Collapsed;

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

    // =====================================================================
    // Propiedades avanzadas del adaptador (velocidad/duplex, control de
    // flujo, moderación de interrupciones, EEE, ahorro de energía) — el
    // mismo set que edita TCP Optimizer. Sirve para cualquier adaptador
    // físico activo, sea Ethernet o Wi-Fi.
    // =====================================================================

    private async Task BuildAdapterCardAsync()
    {
        AdapterPanel.Children.Clear();

        var card = new Border
        {
            Background = ThemeBrushes.Get("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16)
        };
        var panel = new StackPanel { Spacing = 12 };
        card.Child = panel;
        AdapterPanel.Children.Add(card);

        // Selector de adaptador (TCP Optimizer permite elegir la interfaz): lista
        // los adaptadores físicos y aplica/lee las props del seleccionado.
        _adapterIfaces = await AdapterAdvancedService.GetInterfacesAsync();
        if (_adapterIfaces.Count > 1)
        {
            var selectorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            selectorRow.Children.Add(new TextBlock { Text = I18n.T("Adaptador"), VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            _adapterSelector = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var iface in _adapterIfaces)
            {
                var item = new ComboBoxItem { Content = $"{iface.Alias} — {iface.Description}", Tag = iface.Guid };
                _adapterSelector.Items.Add(item);
                if (string.Equals(iface.Guid, _selectedAdapterGuid, StringComparison.OrdinalIgnoreCase)
                    || (_selectedAdapterGuid == null && item == _adapterSelector.Items.OfType<ComboBoxItem>().First()))
                    _adapterSelector.SelectedItem = item;
            }
            if (_adapterSelector.SelectedItem == null && _adapterSelector.Items.Count > 0)
                _adapterSelector.SelectedIndex = 0;
            _adapterSelector.SelectionChanged += AdapterSelector_SelectionChanged;
            selectorRow.Children.Add(_adapterSelector);
            panel.Children.Add(selectorRow);
            if (_adapterSelector.SelectedItem is ComboBoxItem sel)
                _selectedAdapterGuid = sel.Tag as string;
        }
        else if (_adapterIfaces.Count == 1)
        {
            _selectedAdapterGuid = _adapterIfaces[0].Guid;
        }

        _adapterStatusText = new TextBlock { Text = I18n.T("Consultando propiedades del adaptador..."), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_adapterStatusText);
        Feedback.Running(_adapterStatusText, I18n.T("Consultando propiedades del adaptador..."), persistent: true);

        var stateList = await AdapterAdvancedService.GetStateForGuidAsync(_selectedAdapterGuid);
        if (stateList == null)
        {
            Feedback.Set(_adapterStatusText, I18n.T("No se pudo detectar el adaptador de red activo."));
            return;
        }
        Feedback.Set(_adapterStatusText, null);
        _adapterStatusText.Visibility = Visibility.Collapsed;

        _adapterCombos.Clear();
        foreach (var state in stateList)
        {
            var combo = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var opt in state.Def.Options)
            {
                var item = new ComboBoxItem { Content = I18n.T(opt.LabelKey), Tag = opt.Value };
                combo.Items.Add(item);
                if (opt.Value == state.RawValue) combo.SelectedItem = item;
            }
            // Valor de fábrica: si el registro no trae la propiedad, el driver usa
            // su default. Seleccionamos el default en el combo para que no se vea vacío.
            if (combo.SelectedItem == null)
            {
                var fb = state.Def.Options?.FirstOrDefault(o => o.Value == state.Def.Fallback);
                if (fb != null)
                {
                    var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == fb.Value);
                    if (item != null) combo.SelectedItem = item;
                }
            }
            _adapterCombos[state.Def.RegName] = combo;

            var (row, actual) = BuildSettingRow(
                I18n.T(state.Def.Title),
                state.Supported ? null : I18n.T("Valor de fábrica (el driver no lo expone en el registro)"),
                null,
                combo,
                RuleTooltip(state.Def.Tooltip));
            _adapterActual[state.Def.RegName] = actual;
            panel.Children.Add(row);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _adapterApplyButton = new Button { Content = I18n.T("Aplicar"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        _adapterApplyButton.Click += async (s, e) => await ApplyAdapterAsync();
        buttons.Children.Add(_adapterApplyButton);
        var adapterRestoreBtn = new Button { Content = I18n.T("Restaurar valores de fábrica"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        adapterRestoreBtn.Click += async (s, e) => await RestoreAdapterAsync();
        buttons.Children.Add(adapterRestoreBtn);
        panel.Children.Add(buttons);

        _adapterResultText = new TextBlock { Text = "", FontSize = 12, Visibility = Visibility.Collapsed, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_adapterResultText);
    }

    private async void AdapterSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_adapterSelectorBuilding) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem { Tag: string guid }) return;
        _selectedAdapterGuid = guid;
        _adapterSelectorBuilding = true;
        try { await BuildAdapterCardAsync(); }
        finally { _adapterSelectorBuilding = false; }
    }

    private async Task RestoreAdapterAsync()
    {
        if (_adapterResultText == null) return;
        _adapterResultText.Visibility = Visibility.Visible;
        if (_adapterApplyButton != null) _adapterApplyButton.IsEnabled = false;
        Feedback.Running(_adapterResultText, I18n.T("Restaurando valores de fábrica..."));
        try
        {
            var (ok, msg) = await AdapterAdvancedService.RestoreDefaultsAsync(_selectedAdapterGuid);
            if (ok) Feedback.Success(_adapterResultText, I18n.T("Valores de fábrica restaurados. Reconectá la red para aplicar todo."));
            else Feedback.Error(_adapterResultText, I18n.T("Error restaurando: {0}", msg));
            await BuildAdapterCardAsync();
        }
        catch (Exception ex)
        {
            Feedback.Error(_adapterResultText, ex.Message);
        }
        finally
        {
            if (_adapterApplyButton != null) _adapterApplyButton.IsEnabled = true;
        }
    }

    private async Task ApplyAdapterAsync()
    {
        if (_adapterResultText == null || _adapterCombos.Count == 0) return;
        _adapterResultText.Visibility = Visibility.Visible;
        if (_adapterApplyButton != null) _adapterApplyButton.IsEnabled = false;
        Feedback.Running(_adapterResultText, I18n.T("Aplicando propiedades del adaptador..."));
        try
        {
            var values = new Dictionary<string, string>();
            foreach (var (regName, combo) in _adapterCombos)
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: string tag })
                    values[regName] = tag;
            }
            var failed = await AdapterAdvancedService.ApplyAsync(values, _selectedAdapterGuid);
            if (failed.Count == 0)
                Feedback.Success(_adapterResultText, I18n.T("Propiedades aplicadas. Algunos cambios se activan al reconectar la red."));
            else
                Feedback.Error(_adapterResultText, I18n.T("No se pudieron aplicar: {0}", string.Join(", ", failed)));
        }
        catch (Exception ex)
        {
            Feedback.Error(_adapterResultText, ex.Message);
            _loggingService.LogError("Error aplicando propiedades del adaptador", ex);
        }
        finally
        {
            if (_adapterApplyButton != null) _adapterApplyButton.IsEnabled = true;
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
    private const string TtGreenEthernet = "Función del driver que baja el consumo eléctrico cuando el enlace está inactivo o a baja velocidad. Puede agregar demoras de reactivación que se sienten como micro-tirones o ping más alto. → OFF para juegos/baja latencia; ON si priorizás consumo.";

    /// <summary>Clave del servicio → texto español del tooltip (misma mecánica que TtNagle).</summary>
    private static string RuleTooltip(string key) => key switch
    {
        "TtNetNti" => TtNetNti,
        "TtNetSysResp" => TtNetSysResp,
        "TtNetMaxPort" => TtNetMaxPort,
        "TtNetTimedWait" => TtNetTimedWait,
        "TtNetLargeCache" => TtNetLargeCache,
        "TtNetLso" => TtNetLso,
        "TtAdapterGreenEthernet" => TtGreenEthernet,
        _ => key
    };

    private static ToggleSwitch NewToggle()
        => new() { OnContent = "", OffContent = "" };

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
