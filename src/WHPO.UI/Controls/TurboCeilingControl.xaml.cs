using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Controls;

/// <summary>
/// Card del techo de turbo: modo boost, límite de rendimiento y ajustes avanzados
/// del procesador, aplicados al plan de energía activo.
///
/// Vive en el componente Núcleos (es un ajuste del procesador, no del hub de
/// Optimizaciones): la pestaña muestra la temperatura y los núcleos, y esta card
/// es la perilla fina de frecuencia que los acompaña.
/// </summary>
public sealed partial class TurboCeilingControl : UserControl
{
    private readonly ITurboCeilingService _turboCeilingService;
    private readonly ILoggingService _loggingService;
    private readonly ISystemInfoService _systemInfoService;

    private TurboCeilingState? _turboState;
    private bool _turboUpdating;      // armar los controles no debe contar como cambio del usuario
    private readonly List<int> _boostOptionValues = new();   // posición de la barra → valor real de Windows
    private readonly Dictionary<string, Slider> _advancedSliders = new();
    private readonly Dictionary<string, TextBlock> _advancedValueTexts = new();
    // Procesador y su frecuencia nominal: se leen una vez y los usa el tooltip del modo boost
    // (el nombre, para el aviso de AMD; la frecuencia, para la badge del modo apagado).
    private string _cpuName = "";
    private double _cpuNominalMhz;

    // Perfil de energía elegido: Windows guarda un juego de valores para corriente (AC) y
    // otro para batería (DC). La card muestra y edita el del perfil elegido, y los atajos
    // (Predeterminado / Recomendado) actúan sobre ese mismo sin pisar el otro.
    private TurboPowerMode _mode = TurboPowerMode.Ac;

    // Esqueleto de carga: la lectura del estado (powercfg + registro del catálogo de energía)
    // tarda, y hasta que llegaba la card se veía vacía —barras sin valor, desplegable sin
    // contenido—. El esqueleto tiene la silueta de las filas reales y se queda un mínimo de
    // tiempo para que una lectura rápida no se vea como un parpadeo.
    private const int MinSkeletonVisibleMs = 450;
    private bool _turboLoading;
    private long _skeletonShownAtMs;
    private readonly List<Storyboard> _turboSkeletonStoryboards = new();

    // Cambios sin aplicar, uno por perfil: cambiar el desplegable no descarta lo movido en el
    // otro perfil, y "Aplicar" confirma todo junto (el perfil elegido cuenta como cambio
    // pendiente: nada de la card se da por aplicado hasta que se pulsa Aplicar).
    private readonly Dictionary<TurboPowerMode, TurboDraft> _drafts = new()
    {
        [TurboPowerMode.Ac] = new TurboDraft(),
        [TurboPowerMode.Dc] = new TurboDraft()
    };

    // El desplegable también queda pendiente: elegir un perfil no se da por aplicado hasta
    // que se pulsa Aplicar (ese botón es el que confirma la card entera).
    private bool _profilePending;

    /// <summary>Cambios de un perfil que todavía no se aplicaron al plan.</summary>
    private sealed class TurboDraft
    {
        public int? BoostMode { get; set; }
        public int? LimitPercent { get; set; }
        public Dictionary<string, int> Advanced { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasChanges => BoostMode != null || LimitPercent != null || Advanced.Count > 0;

        public void Clear()
        {
            BoostMode = null;
            LimitPercent = null;
            Advanced.Clear();
        }
    }

    public TurboCeilingControl()
    {
        InitializeComponent();
        _turboCeilingService = App.Services.GetRequiredService<ITurboCeilingService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();
        InitModeCombo();

        // La página de Núcleos se cachea: la suscripción se ata a Loaded/Unloaded para
        // no dejar el handler de idioma colgado al desmontar.
        Loaded += (_, _) =>
        {
            I18n.LanguageChanged -= OnLanguageChanged;
            I18n.LanguageChanged += OnLanguageChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            I18n.LanguageChanged -= OnLanguageChanged;
            // Si se sale de la pestaña a mitad de la carga, el pulso no queda vivo.
            StopSkeletonPulse();
        };
    }

    /// <summary>
    /// Relee el techo de turbo del plan activo. Núcleos la llama al abrir la pestaña,
    /// para no mostrar el estado de un plan que ya no está vigente, y con el esqueleto
    /// visible, igual que el resto de la página.
    /// </summary>
    public void Refresh() => _ = LoadTurboCeilingAsync(showSkeleton: true);

    // ===================== Skeleton de carga =====================

    /// <summary>Muestra el esqueleto y esconde las filas reales mientras se lee el estado.</summary>
    private void ShowSkeleton()
    {
        _skeletonShownAtMs = Environment.TickCount64;
        TurboSkeleton.Visibility = Visibility.Visible;
        TurboContent.Visibility = Visibility.Collapsed;
        StartSkeletonPulse();
    }

    private void ShowContent()
    {
        StopSkeletonPulse();
        TurboSkeleton.Visibility = Visibility.Collapsed;
        TurboContent.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// El esqueleto no se va antes de <see cref="MinSkeletonVisibleMs"/>: con una lectura
    /// rápida se vería como un parpadeo de la card.
    /// </summary>
    private async Task EnsureMinSkeletonVisibleAsync()
    {
        long elapsed = Environment.TickCount64 - _skeletonShownAtMs;
        if (elapsed < MinSkeletonVisibleMs)
            await Task.Delay((int)(MinSkeletonVisibleMs - elapsed));
    }

    /// <summary>
    /// Pulso de opacidad por bloque (1.0 → 0.35 en 900 ms, auto-reverse infinito), el
    /// mismo que usan los esqueletos de Biblioteca y Sistema.
    /// </summary>
    private void StartSkeletonPulse()
    {
        StopSkeletonPulse();
        try
        {
            foreach (var block in TurboSkeleton.Children.OfType<FrameworkElement>())
            {
                var sb = new Storyboard
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    AutoReverse = true
                };
                var anim = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.35,
                    Duration = new Duration(TimeSpan.FromMilliseconds(900))
                };
                Storyboard.SetTarget(anim, block);
                Storyboard.SetTargetProperty(anim, "Opacity");
                sb.Children.Add(anim);
                _turboSkeletonStoryboards.Add(sb);
                sb.Begin();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"TurboCeilingControl: animación del esqueleto: {ex.Message}");
        }
    }

    private void StopSkeletonPulse()
    {
        foreach (var sb in _turboSkeletonStoryboards)
        {
            try { sb.Stop(); } catch { }
        }
        _turboSkeletonStoryboards.Clear();
    }

    private void OnLanguageChanged()
    {
        I18n.ApplyToVisualTree(this);
        // El desplegable de perfil y las etiquetas del modo boost los escribe el código:
        // se rearman con el idioma nuevo.
        InitModeCombo();
        if (_turboState != null) ApplyTurboState(_turboState);
    }

    // ===================== Perfil de energía (AC / DC) =====================

    /// <summary>Carga el desplegable con los dos perfiles que guarda Windows.</summary>
    private void InitModeCombo()
    {
        var previous = _turboUpdating;
        _turboUpdating = true;
        try
        {
            // Se rearma la lista (no solo el índice) para que un cambio de idioma se vea
            // aunque el perfil elegido siga siendo el mismo.
            TurboModeCombo.ItemsSource = new[]
            {
                I18n.T("Escritorio (enchufado)"),
                I18n.T("Notebook (batería)")
            };
            TurboModeCombo.SelectedIndex = _mode == TurboPowerMode.Dc ? 1 : 0;
        }
        finally
        {
            _turboUpdating = previous;
        }
    }

    private void TurboModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_turboUpdating) return;
        var mode = TurboModeCombo.SelectedIndex == 1 ? TurboPowerMode.Dc : TurboPowerMode.Ac;
        if (mode == _mode) return;

        // Lo movido en el perfil que se deja queda guardado en su borrador (no se pierde) y
        // el perfil elegido pasa a contar como cambio pendiente: recién lo confirma Aplicar.
        CaptureDraft(_mode);
        _mode = mode;
        _profilePending = true;
        if (_turboState != null) ApplyTurboState(_turboState);
    }

    // ===================== Valores del perfil elegido =====================

    private int? BoostValue(TurboCeilingState s)
        => _mode == TurboPowerMode.Dc ? s.BoostModeValueDc : s.BoostModeValue;

    private int? LimitValue(TurboCeilingState s)
        => _mode == TurboPowerMode.Dc ? s.LimitPercentDc : s.LimitPercent;

    private int? BoostDefault(TurboCeilingState s)
        => _mode == TurboPowerMode.Dc ? s.DefaultBoostModeDc : s.DefaultBoostMode;

    private int? LimitDefault(TurboCeilingState s)
        => _mode == TurboPowerMode.Dc ? s.DefaultLimitPercentDc : s.DefaultLimitPercent;

    private int? AdvancedValue(TurboAdvancedSetting setting)
        => _mode == TurboPowerMode.Dc ? setting.DcValue : setting.Value;

    private int? AdvancedDefault(TurboAdvancedSetting setting)
        => _mode == TurboPowerMode.Dc ? setting.DefaultDc : setting.DefaultValue;

    // ===================== Borradores por perfil =====================

    private static int? BoostValueFor(TurboCeilingState s, TurboPowerMode mode)
        => mode == TurboPowerMode.Dc ? s.BoostModeValueDc : s.BoostModeValue;

    private static int? LimitValueFor(TurboCeilingState s, TurboPowerMode mode)
        => mode == TurboPowerMode.Dc ? s.LimitPercentDc : s.LimitPercent;

    private static int? AdvancedValueFor(TurboAdvancedSetting setting, TurboPowerMode mode)
        => mode == TurboPowerMode.Dc ? setting.DcValue : setting.Value;

    /// <summary>
    /// Guarda en el borrador del perfil lo que el usuario movió y todavía no aplicó. Se llama
    /// al cambiar de perfil (para no perder ese perfil al pasar al otro) y al pulsar Aplicar.
    /// </summary>
    private void CaptureDraft(TurboPowerMode mode)
    {
        if (_turboState == null) return;
        var draft = _drafts[mode];
        draft.Clear();

        if (_turboState.BoostModeExposed)
        {
            var selected = SelectedBoostMode();
            var applied = BoostValueFor(_turboState, mode);
            if (selected != null && applied != null && selected != applied) draft.BoostMode = selected;
        }

        if (_turboState.LimitExposed)
        {
            var value = (int)Math.Round(TurboLimitSlider.Value);
            var applied = LimitValueFor(_turboState, mode);
            if (applied != null && value != applied.Value) draft.LimitPercent = value;
        }

        foreach (var setting in _turboState.Advanced)
        {
            if (!setting.Exposed) continue;
            if (!_advancedSliders.TryGetValue(setting.SettingGuid, out var slider)) continue;
            var applied = AdvancedValueFor(setting, mode);
            if (applied == null) continue;
            var value = (int)Math.Round(slider.Value);
            if (value != applied.Value) draft.Advanced[setting.SettingGuid] = value;
        }
    }

    private void ClearDrafts()
    {
        foreach (var draft in _drafts.Values) draft.Clear();
    }

    // ===================== Lectura del estado =====================

    private async Task LoadTurboCeilingAsync(bool showSkeleton = false)
    {
        if (_turboLoading) return;
        // Esqueleto la primera vez (todavía no hay estado que mostrar) y cuando se pide
        // explícitamente al abrir la pestaña; NO al releer después de Aplicar, que ya
        // tiene valores en pantalla y solo se refresca.
        bool skeleton = showSkeleton || _turboState == null;
        _turboLoading = true;
        if (skeleton) ShowSkeleton();

        try
        {
            // powercfg + registro del catálogo de energía: fuera del hilo de UI.
            var state = await Task.Run(() => _turboCeilingService.GetState());
            _cpuName = state.CpuName;
            // Lo que el equipo reporta es la verdad: lo movido sin aplicar ya no existe.
            ClearDrafts();
            _profilePending = false;
            if (_cpuNominalMhz <= 0)
            {
                // Una sola vez: es WMI, y el tooltip se rearma al mover la barra.
                try { _cpuNominalMhz = await Task.Run(() => _systemInfoService.GetCpuInfo().MaxFrequencyMHz); }
                catch { }
            }
            ApplyTurboState(state);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"TurboCeilingControl: no se pudo leer el techo de turbo: {ex.Message}");
        }
        finally
        {
            // Sin estado no queda un esqueleto eterno: se muestra la card con lo que haya
            // (el estado vacío propio de la card ya explica cuando el equipo no expone nada).
            if (skeleton)
            {
                await EnsureMinSkeletonVisibleAsync();
                ShowContent();
            }
            _turboLoading = false;
        }
    }

    /// <summary>
    /// Pinta el estado REAL del equipo: los controles que existen, con el rango que el
    /// sistema declara y en el valor en el que está. Si el equipo no expone algo, se
    /// dice por qué en vez de mostrar un control que no puede hacer nada.
    /// </summary>
    private void ApplyTurboState(TurboCeilingState state)
    {
        _turboState = state;
        _turboUpdating = true;
        try
        {
            TurboPlanChipText.Text = string.IsNullOrWhiteSpace(state.PlanName)
                ? string.Empty
                : I18n.T("Plan activo: {0}", state.PlanName);

            // ---- Modo boost: barra escalonada con las posiciones que declara el sistema ----
            // Es una LISTA de modos, no una magnitud: la barra salta de uno a otro (sin
            // valores intermedios, porque Windows no los acepta) y arriba se muestra el
            // nombre del modo actual. Las posiciones salen del equipo, no del código.
            // La barra muestra lo movido sin aplicar (si lo hay); la badge "Actual" sigue
            // mostrando lo que el equipo reporta hoy.
            var boost = BoostValue(state);
            var boostDisplay = _drafts[_mode].BoostMode ?? boost;
            TurboBoostRow.Visibility = state.BoostModeExposed ? Visibility.Visible : Visibility.Collapsed;
            _boostOptionValues.Clear();
            if (state.BoostModeExposed && boostDisplay != null)
            {
                _boostOptionValues.AddRange(state.BoostModeOptions);
                TurboBoostSlider.Minimum = 0;
                TurboBoostSlider.Maximum = Math.Max(0, _boostOptionValues.Count - 1);
                TurboBoostSlider.StepFrequency = 1;

                var index = _boostOptionValues.IndexOf(boostDisplay.Value);
                TurboBoostSlider.Value = index >= 0 ? index : 0;
                TurboBoostValueText.Text = BoostModeLabel(boostDisplay.Value);
                UpdateBoostModeHelp(boostDisplay.Value);
            }
            else
            {
                TurboBoostValueText.Text = string.Empty;
                UpdateBoostModeHelp(null);
            }

            // Badges de la fila, en el mismo formato que las opciones de Optimizaciones.
            FillBadges(TurboBoostBadges, BoostRecommendedText(state),
                boost != null ? BoostModeLabel(boost.Value) : null);

            // ---- Límite de rendimiento (rango del equipo, nunca 0-100 fijo) ----
            TurboLimitRow.Visibility = state.LimitExposed ? Visibility.Visible : Visibility.Collapsed;
            // Se arma en código (no en el XAML) para que el texto envuelva dentro del tooltip y
            // siga el idioma: el tooltip de string del XAML se recortaba al llegar al ancho máximo.
            ToolTipService.SetToolTip(TurboLimitInfoButton, BuildLimitToolTip());
            var limit = LimitValue(state);
            var limitDisplay = _drafts[_mode].LimitPercent ?? limit;
            if (state.LimitExposed && limitDisplay != null)
            {
                TurboLimitSlider.Minimum = state.LimitMin;
                TurboLimitSlider.Maximum = state.LimitMax;
                TurboLimitSlider.StepFrequency = state.LimitStep > 0 ? state.LimitStep : 1;
                TurboLimitSlider.Value = limitDisplay.Value;
                TurboLimitValueText.Text = FormatLimitValue(limitDisplay.Value, state.LimitUnits);
            }
            else
            {
                TurboLimitValueText.Text = string.Empty;
            }

            FillBadges(TurboLimitBadges, LimitRecommendedText(state),
                limit != null ? FormatLimitValue(limit.Value, state.LimitUnits) : null);

            // ---- Estado vacío: se explica, no se esconde ----
            var unavailable = !state.HasAnyControl;
            TurboControlsPanel.Visibility = unavailable ? Visibility.Collapsed : Visibility.Visible;
            TurboUnavailableBorder.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
            if (unavailable)
            {
                TurboUnavailableText.Text = string.IsNullOrWhiteSpace(state.PlanGuid)
                    ? I18n.T("No se pudo leer el plan de energía activo, así que no hay ajustes que mostrar.")
                    : I18n.T("Este equipo no expone estos ajustes: Windows no los ofrece para este procesador o el firmware los bloqueó.");
            }

            // ---- Ajustes avanzados: se arman solo con los que expone el equipo ----
            BuildAdvancedControls(state);

            TurboScopeText.Text = unavailable
                ? string.Empty
                : I18n.T("Se aplica al plan de energía activo: si cambiás de plan, el techo vuelve al de ese plan.");
 // "Predeterminado" solo si el sistema reporta algún valor de fábrica (los
 // avanzados también vuelven al suyo); "Recomendado", siempre que haya algo
 // que tocar.
            var hasFactoryValues = LimitDefault(state) != null
                || BoostDefault(state) != null
                || state.Advanced.Exists(a => a.Exposed && AdvancedDefault(a) != null);
            TurboDefaultsButton.IsEnabled = state.HasAnyControl && hasFactoryValues;
            // "Recomendado" solo si el perfil elegido tiene algo que recomendar: el techo, el
            // boost o algún avanzado con recomendado en el catálogo para ese perfil.
            TurboRecommendedButton.IsEnabled = LimitValue(state) != null
                || BoostValue(state) != null
                || state.Advanced.Exists(a => a.Exposed
                    && (_mode == TurboPowerMode.Dc ? a.RecommendedDc : a.RecommendedValue) != null);
            TurboApplyButton.IsEnabled = false;   // se habilita cuando hay un cambio pendiente

 // Tooltips de los dos atajos: mismo formato que el resto de la card (título +
 // texto que envuelve), y se rearman acá porque el idioma cambia en caliente.
            ToolTipService.SetToolTip(TurboDefaultsButton, WrapToolTip(InfoToolTipContent(
                I18n.T("Predeterminado"),
                I18n.T("Carga los valores predeterminados del plan de energía activo en los ajustes de abajo. Se confirman al pulsar Aplicar."))));
            ToolTipService.SetToolTip(TurboRecommendedButton, WrapToolTip(InfoToolTipContent(
                I18n.T("Recomendado"),
                I18n.T("Carga el perfil recomendado del modo elegido: límite 100 %, modo boost Agresivo (o Activado si el equipo no declara el agresivo) y los ajustes avanzados que recomienda el catálogo. Se confirman al pulsar Aplicar."))));
            ToolTipService.SetToolTip(TurboModeInfoButton, WrapToolTip(InfoToolTipContent(
                I18n.T("Perfil de energía"),
                I18n.T("Windows guarda un juego de valores para corriente y otro para batería. Un equipo de escritorio solo usa el de corriente."))));

            // Traza de lo que la card pinta, al final: si un valor aplicado no aparece
            // en la UI, esto dice si el estado llegó distinto del que el servicio escribió.
            _loggingService.LogInfo(
                $"Techo de turbo ({_mode}): pintando boost={BoostValue(state)?.ToString() ?? "—"} límite={LimitValue(state)?.ToString() ?? "—"} " +
                string.Join(", ", state.Advanced.Where(a => a.Exposed).Select(a => $"{a.Key}={AdvancedValue(a)}")));
        }
        catch (Exception ex)
        {
            // Un fallo al pintar la card no puede romper el resto de la página.
            _loggingService.LogWarning($"TurboCeilingControl: no se pudo pintar el techo de turbo: {ex.Message}");
        }
        finally
        {
            _turboUpdating = false;
        }

        // Tras pintar, "Aplicar" queda en su estado real: se habilita si hay algo pendiente,
        // incluido el perfil elegido en el desplegable o lo movido en el otro perfil.
        UpdateTurboPendingState();
    }

    /// <summary>
    /// Arma una tarjeta por cada ajuste avanzado que el equipo expone: nombre, botón de ayuda
    /// (el detalle va en su tooltip), valor y una barra continua con el rango real del sistema.
    /// Van de a dos por fila para no apilar todos los ajustes uno abajo del otro. Los ajustes
    /// que no se pueden leer no se crean (y si no queda ninguno, la sección entera se oculta).
    /// </summary>
    private void BuildAdvancedControls(TurboCeilingState state)
    {
        TurboAdvancedPanel.Children.Clear();
        _advancedSliders.Clear();
        _advancedValueTexts.Clear();

        var exposed = state.Advanced.Where(a => a.Exposed).ToList();
        TurboAdvancedExpander.Visibility = exposed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (exposed.Count == 0) return;

        for (var i = 0; i < exposed.Count; i += 2)
        {
            var row = new Grid { ColumnSpacing = 28, VerticalAlignment = VerticalAlignment.Top };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(BuildAdvancedTile(exposed[i]));

            if (i + 1 < exposed.Count)
            {
                var right = BuildAdvancedTile(exposed[i + 1]);
                Grid.SetColumn(right, 1);
                row.Children.Add(right);
            }

            TurboAdvancedPanel.Children.Add(row);
        }
    }

    /// <summary>
    /// Una tarjeta de ajuste avanzado: nombre + botón ⓘ (con la ayuda en el tooltip), el valor
    /// actual arriba de la barra y la barra debajo, igual que los ajustes de arriba de la card.
    /// </summary>
    private FrameworkElement BuildAdvancedTile(TurboAdvancedSetting setting)
    {
        var tile = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new TextBlock
        {
            Text = I18n.T(setting.Key),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(TooltipStyles.CreateInfoButton(
            InfoToolTipContent(I18n.T(setting.Key), I18n.T(setting.HelpKey))));
        tile.Children.Add(header);

        // Recomendado / Actual del perfil elegido, como en las opciones de Optimizaciones.
        // El badge es el valor APLICADO; la barra puede estar mostrando uno movido sin aplicar.
        var displayValue = _drafts[_mode].Advanced.TryGetValue(setting.SettingGuid, out var drafted)
            ? drafted
            : (AdvancedValue(setting) ?? setting.Min);
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        FillBadges(badges, AdvancedRecommendedText(setting),
            FormatAdvancedValue(AdvancedValue(setting) ?? setting.Min, setting.UnitSymbol));
        tile.Children.Add(badges);

        var valueText = new TextBlock
        {
            Text = FormatAdvancedValue(displayValue, setting.UnitSymbol),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextWrapping = TextWrapping.Wrap
        };
        tile.Children.Add(valueText);

        var slider = new Slider
        {
            Minimum = setting.Min,
            Maximum = setting.Max,
            StepFrequency = Math.Max(1, setting.Step),
            Value = displayValue,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = setting.SettingGuid
        };
        slider.ValueChanged += TurboAdvancedSlider_ValueChanged;
        tile.Children.Add(slider);

        _advancedSliders[setting.SettingGuid] = slider;
        _advancedValueTexts[setting.SettingGuid] = valueText;
        return tile;
    }

    private static string FormatAdvancedValue(int value, string unit)
        => string.IsNullOrWhiteSpace(unit) ? value.ToString() : $"{value} {unit}";

    private void TurboAdvancedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_turboUpdating || _turboState == null) return;
        if (sender is Slider { Tag: string guid } slider)
        {
            var setting = _turboState.Advanced.Find(a => a.SettingGuid.Equals(guid, StringComparison.OrdinalIgnoreCase));
            if (setting != null && _advancedValueTexts.TryGetValue(guid, out var text))
                text.Text = FormatAdvancedValue((int)Math.Round(slider.Value), setting.UnitSymbol);
        }
        UpdateTurboPendingState();
    }

    /// <summary>Ajustes avanzados que el usuario cambió respecto de lo aplicado.</summary>
    private Dictionary<string, int> PendingAdvancedValues()
    {
        var pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_turboState == null) return pending;

        foreach (var setting in _turboState.Advanced)
        {
            if (!setting.Exposed) continue;
            var current = AdvancedValue(setting);
            if (current == null) continue;
            if (!_advancedSliders.TryGetValue(setting.SettingGuid, out var slider)) continue;
            var value = (int)Math.Round(slider.Value);
            if (value != current.Value) pending[setting.SettingGuid] = value;
        }
        return pending;
    }

    private static string BoostModeLabel(int value) => value switch
    {
        0 => I18n.T("Desactivado"),
        1 => I18n.T("Activado"),
        2 => I18n.T("Agresivo"),
        3 => I18n.T("Eficiente"),
        4 => I18n.T("Eficiente agresivo"),
        5 => I18n.T("Agresivo (frecuencia garantizada)"),
        6 => I18n.T("Eficiente agresivo (frecuencia garantizada)"),
        // Un valor que Windows agregue en el futuro se muestra crudo, no se inventa.
        _ => I18n.T("Modo {0}", value)
    };

    private static string FormatLimitValue(int value, string units)
        => string.IsNullOrWhiteSpace(units) ? value.ToString() : $"{value} {units}";

    /// <summary>Valor real del modo boost en la posición actual de la barra.</summary>
    private int? SelectedBoostMode()
    {
        if (_boostOptionValues.Count == 0) return null;
        var index = (int)Math.Round(TurboBoostSlider.Value);
        if (index < 0 || index >= _boostOptionValues.Count) return null;
        return _boostOptionValues[index];
    }

    private void TurboBoostSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_turboUpdating || _turboState == null) return;
        var value = SelectedBoostMode();
        TurboBoostValueText.Text = value != null ? BoostModeLabel(value.Value) : string.Empty;
        UpdateBoostModeHelp(value);
        UpdateTurboPendingState();
    }

    /// <summary>Lo que hay que saber antes de leer la lista de modos: no es una escala.</summary>
    private static string BoostModeIntro()
        => I18n.T("Cada posición es un modo que documenta Windows, no un grado de una misma escala.");

    // ---- Contenido de los tooltips de esta card ----
    //
    // Un string suelto dentro de un ToolTip NO envuelve: el ContentPresenter lo dibuja sin
    // TextWrapping y el texto se corta al llegar al ancho máximo. Por eso cada tooltip se arma
    // con TextBlocks que envuelven, y todos con el mismo formato: título, texto y viñetas.

    private static StackPanel NewToolTipBox() => new() { Spacing = 6, MaxWidth = 430 };

    private static TextBlock ToolTipTitle(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock ToolTipText(string text, bool emphasis = false, bool bullet = false) => new()
    {
        Text = (bullet ? "• " : string.Empty) + text,
        FontSize = 12,
        FontWeight = emphasis ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Feedback.MutedBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(bullet ? 8 : 0, 0, 0, 0)
    };

    private static ToolTip WrapToolTip(StackPanel content)
        => new() { Placement = PlacementMode.Bottom, Content = content };

    /// <summary>Tooltip informativo con el formato de la card: título, descripción y viñetas.</summary>
    private static StackPanel InfoToolTipContent(string title, string description, params string[] bullets)
    {
        var content = NewToolTipBox();
        content.Children.Add(ToolTipTitle(title));
        if (!string.IsNullOrEmpty(description)) content.Children.Add(ToolTipText(description));
        foreach (var bullet in bullets) content.Children.Add(ToolTipText(bullet, bullet: true));
        return content;
    }

    /// <summary>Tooltip del límite de rendimiento: se arma en código para que el texto envuelva.</summary>
    private static ToolTip BuildLimitToolTip()
        => WrapToolTip(InfoToolTipContent(
            I18n.T("Límite de rendimiento"),
            I18n.T("Porcentaje máximo del procesador que Windows puede usar (turbo incluido).")));

    private static string BoostModeHelp(int value) => value switch
    {
        0 => I18n.T("Sin turbo: el procesador no pasa de su frecuencia base. Es la posición más fresca y silenciosa."),
        1 => I18n.T("Windows pide turbo cuando detecta carga: es el comportamiento con el que viene el equipo."),
        2 => I18n.T("Windows pide turbo antes y lo sostiene más tiempo: más rendimiento, más calor y más consumo."),
        3 => I18n.T("No decide Windows: decide el propio procesador, y solo cuando le conviene en consumo."),
        4 => I18n.T("Decide el propio procesador, pero empuja más fuerte y por más tiempo que el eficiente."),
        5 => I18n.T("Turbo agresivo, pero limitado a la frecuencia sostenida que el equipo puede mantener sin bajar."),
        6 => I18n.T("Lo decide el procesador, empuja fuerte y se limita a la frecuencia sostenida: la más previsible."),
        // Un valor que Windows agregue en el futuro se explica como lo que es, sin inventarle un modo.
        _ => I18n.T("Valor {0}: el equipo lo informa y Windows no le da un nombre propio.", value)
    };

    /// <summary>
    /// Deja la explicación del modo boost en el tooltip del botón de ayuda. No se dibuja como
    /// texto fijo: la fila queda "nombre + barra" y el detalle se consulta.
    /// </summary>
    private void UpdateBoostModeHelp(int? value)
    {
        ToolTipService.SetToolTip(TurboBoostInfoButton, BuildBoostModeToolTip(value));
    }

    /// <summary>
    /// Tooltip del modo boost: título, aclaración de que no es una escala y la lista de perfiles
    /// con lo que cambia en cada uno, más el elegido marcado. Mismo formato que el tooltip del
    /// Modo juego de WinForge (título + subtítulo + viñetas) y se rearma al mover la barra.
    /// </summary>
    private ToolTip BuildBoostModeToolTip(int? current)
    {
        var content = NewToolTipBox();
        content.Children.Add(ToolTipTitle(I18n.T("Modo boost")));
        content.Children.Add(ToolTipText(BoostModeIntro()));

        // Solo los perfiles que expone este equipo: listar los siete documentados cuando el
        // firmware declara cinco mostraría posiciones que la barra no tiene.
        if (_boostOptionValues.Count > 0)
        {
            content.Children.Add(ToolTipText(I18n.T("Qué cambia en cada perfil:"), emphasis: true));
            foreach (var mode in _boostOptionValues)
                content.Children.Add(BuildBoostProfile(mode, mode == current));

            // Honestidad para AMD: el procesador administra el boost por su cuenta y, medido en un
            // Ryzen, puede no distinguir estas posiciones (la 2 y la 6 rindieron igual).
            if (IsAmdCpu())
                content.Children.Add(ToolTipText(I18n.T("En los procesadores AMD el boost lo administra el firmware del procesador, así que puede no distinguir estas posiciones.")));
        }

        content.Children.Add(ToolTipText(I18n.T("El cambio se aplica al pulsar Aplicar; Predeterminado vuelve al valor predeterminado del plan de energía activo.")));
        return WrapToolTip(content);
    }

    /// <summary>
    /// Un perfil de la lista: viñeta, nombre y badge de estado arriba, y la explicación abajo.
    /// El nombre va en negrita y con el color de texto principal, y la descripción en gris: antes
    /// los dos compartían estilo y no se veía dónde terminaba uno y empezaba la otra.
    /// </summary>
    private FrameworkElement BuildBoostProfile(int mode, bool isCurrent)
    {
        var block = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 0, 0) };

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        head.Children.Add(new TextBlock
        {
            Text = "•",
            FontSize = 12,
            Foreground = Feedback.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        var name = new TextBlock
        {
            // Sin Foreground propio a propósito: el color de texto principal lo separa de la
            // descripción gris de abajo.
            Text = isCurrent ? I18n.T("{0}  (activo)", BoostModeLabel(mode)) : BoostModeLabel(mode),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);

        var badge = BuildBoostBadge(mode);
        if (badge != null)
        {
            Grid.SetColumn(badge, 2);
            head.Children.Add(badge);
        }
        block.Children.Add(head);

        block.Children.Add(new TextBlock
        {
            Text = BoostModeHelp(mode),
            FontSize = 11,
            Foreground = Feedback.MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 0, 0, 0)
        });

        return block;
    }

    /// <summary>
    /// Badge de estado del perfil: qué significa ese modo para el techo de frecuencia, con lo que
    /// documenta Windows. Verde = el turbo puede llegar a su máximo; ámbar = no (turbo apagado, o
    /// limitado a la frecuencia garantizada). El modo apagado dice además en qué frecuencia queda,
    /// leída del equipo. Un modo que Windows no documenta no lleva badge: no se le inventa un estado.
    /// </summary>
    private Border? BuildBoostBadge(int mode)
    {
        bool positive;
        string text;
        switch (mode)
        {
            case 0:
                positive = false;
                text = _cpuNominalMhz > 0
                    ? I18n.T("Sin turbo · {0}", FormatFreqShort(_cpuNominalMhz))
                    : I18n.T("Sin turbo");
                break;
            case >= 1 and <= 4:
                positive = true;
                text = I18n.T("Turbo máximo");
                break;
            case >= 5 and <= 6:
                positive = false;
                text = I18n.T("Tope garantizado");
                break;
            default:
                return null;
        }

        return new Border
        {
            Padding = new Thickness(7, 1, 7, 1),
            CornerRadius = new CornerRadius(7),
            Background = ThemeBrushes.Get(positive ? "SuccessTintBrush" : "WarningTintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = positive ? Feedback.SuccessBrush : Feedback.WarningBrush
            }
        };
    }

    /// <summary>Texto del recomendado del boost en el perfil elegido (null si no hay).</summary>
    private static string? BoostRecommendedText(TurboCeilingState s)
    {
        if (!s.BoostModeExposed) return null;
        if (s.BoostModeOptions.Contains(2)) return BoostModeLabel(2);      // Agresivo
        if (s.BoostModeOptions.Contains(1)) return BoostModeLabel(1);      // Activado
        return null;
    }

    /// <summary>Texto del recomendado del techo: el máximo del plan, dentro del rango.</summary>
    private static string? LimitRecommendedText(TurboCeilingState s)
        => s.LimitExposed
            ? FormatLimitValue(Math.Clamp(100, s.LimitMin, s.LimitMax), s.LimitUnits)
            : null;

    /// <summary>Recomendado de un ajuste avanzado en el perfil elegido.</summary>
    private string? AdvancedRecommendedText(TurboAdvancedSetting setting)
    {
        var value = _mode == TurboPowerMode.Dc ? setting.RecommendedDc : setting.RecommendedValue;
        return value != null ? FormatAdvancedValue(value.Value, setting.UnitSymbol) : null;
    }

    /// <summary>Llena los badges de una fila: recomendado (si hay) y el valor actual del perfil.</summary>
    private void FillBadges(StackPanel panel, string? recommended, string? actual)
    {
        panel.Children.Clear();
        if (string.IsNullOrEmpty(actual)) return;
        if (!string.IsNullOrEmpty(recommended))
            panel.Children.Add(BuildSmallBadge($"{I18n.T("Recomendado")}: {recommended}", positive: true));
        panel.Children.Add(BuildSmallBadge(I18n.T("Actual: {0}", actual), positive: false));
    }

    /// <summary>Badge chico de las filas, con el mismo formato que los de Optimizaciones.</summary>
    private static Border BuildSmallBadge(string text, bool positive)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (positive)
            content.Children.Add(new FontIcon
            {
                Glyph = "\uE735",
                FontSize = 10,
                Foreground = Feedback.SuccessBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = positive ? Feedback.SuccessBrush : ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        return new Border
        {
            Padding = new Thickness(7, 1, 7, 1),
            CornerRadius = new CornerRadius(7),
            Background = ThemeBrushes.Get(positive ? "SuccessTintBrush" : "ChipBackgroundBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
    }

    /// <summary>Frecuencia compacta para las badges ("3,8 GHz" en vez de "3,80 GHz").</summary>
    private static string FormatFreqShort(double mhz)
        => mhz >= 1000 ? $"{mhz / 1000.0:0.#} GHz" : $"{mhz:F0} MHz";

    /// <summary>El boost lo administra el firmware en los procesadores AMD.</summary>
    private bool IsAmdCpu()
        => _cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase)
           || _cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)
           || _cpuName.Contains("Athlon", StringComparison.OrdinalIgnoreCase)
           || _cpuName.Contains("Threadripper", StringComparison.OrdinalIgnoreCase);

    private void TurboLimitSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_turboUpdating || _turboState == null) return;
        TurboLimitValueText.Text = FormatLimitValue((int)Math.Round(TurboLimitSlider.Value), _turboState.LimitUnits);
        UpdateTurboPendingState();
    }

    /// <summary>Habilita "Aplicar" solo cuando hay un cambio real respecto de lo aplicado.</summary>
    private void UpdateTurboPendingState()
    {
        if (_turboState == null)
        {
            TurboApplyButton.IsEnabled = false;
            TurboModeCombo.IsEnabled = true;
            return;
        }
        var pending = false;

        var boost = SelectedBoostMode();
        if (_turboState.BoostModeExposed && boost != null && boost != BoostValue(_turboState))
            pending = true;

        var limit = LimitValue(_turboState);
        if (_turboState.LimitExposed && limit != null &&
            (int)Math.Round(TurboLimitSlider.Value) != limit.Value)
            pending = true;

        if (!pending && PendingAdvancedValues().Count > 0) pending = true;

        // El perfil elegido en el desplegable y lo movido en el otro perfil también son
        // cambios sin aplicar: se confirman todos con el mismo Aplicar.
        if (!pending && (_profilePending || _drafts[OtherMode()].HasChanges)) pending = true;

        TurboApplyButton.IsEnabled = pending;
        // El desplegable ya no se bloquea: cambiar de perfil no descarta nada, cada perfil
        // guarda lo suyo en su borrador hasta que se aplica.
        TurboModeCombo.IsEnabled = true;
    }

    private TurboPowerMode OtherMode()
        => _mode == TurboPowerMode.Ac ? TurboPowerMode.Dc : TurboPowerMode.Ac;

    private async void TurboApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_turboState == null) return;
        TurboApplyButton.IsEnabled = false;
        TurboDefaultsButton.IsEnabled = false;
        TurboRecommendedButton.IsEnabled = false;
        Feedback.Running(TurboStatusText, I18n.T("Aplicando…"));

        try
        {
            // Lo que muestran las barras es el borrador del perfil elegido: se guarda y se
            // confirma junto con el desplegable en el mismo Aplicar.
            CaptureDraft(_mode);

            var selected = _mode;
            CommandResult? last = null;

            // Primero el perfil a la vista (el techo y el boost, que es lo que confirma el
            // desplegable) y después el otro, con lo que haya quedado movido sin aplicar.
            foreach (var mode in new[] { selected, OtherMode() })
            {
                var draft = _drafts[mode];
                int? boost;
                int? limit;
                Dictionary<string, int> advanced;

                if (mode == selected)
                {
                    // El perfil a la vista se confirma con el techo y el boost, que es lo que
                    // el desplegable elige; los avanzados solo si se movieron, para no reescribir
                    // políticas que el usuario no tocó.
                    boost = _turboState.BoostModeExposed ? SelectedBoostMode() : null;
                    limit = _turboState.LimitExposed ? (int)Math.Round(TurboLimitSlider.Value) : (int?)null;
                    advanced = PendingAdvancedValues();
                }
                else
                {
                    boost = draft.BoostMode;
                    limit = draft.LimitPercent;
                    advanced = new Dictionary<string, int>(draft.Advanced, StringComparer.OrdinalIgnoreCase);
                }

                if (boost == null && limit == null && advanced.Count == 0) continue;

 // Las lecturas de powercfg son síncronas y caras (varios procesos + WMI): si
 // corren en el hilo de UI, la ventana se congela varios segundos y parece que el
 // botón no hizo nada. El servicio se llama desde el pool y el resultado vuelve
 // al hilo de UI para pintar.
                var result = await Task.Run(() => _turboCeilingService.ApplyAsync(boost, limit, mode));
                if (result.Success && advanced.Count > 0)
                    result = await Task.Run(() => _turboCeilingService.ApplyAdvancedAsync(advanced, mode));

                last = result;
                if (!result.Success) break;
            }

            if (last != null) Feedback.Result(TurboStatusText, last);
        }
        catch (Exception ex)
        {
            Feedback.Error(TurboStatusText, ex.Message);
            _loggingService.LogWarning($"TurboCeilingControl: error aplicando el techo de turbo: {ex.Message}");
        }
        finally
        {
            // El estado que queda en la UI es el que el equipo reporta después de aplicar.
            await LoadTurboCeilingAsync();
        }
    }

    /// <summary>
    /// Atajo «Predeterminado»: deja cargados en las barras los valores de fábrica del perfil
    /// elegido, como cambio PENDIENTE. Ya no escribe en el plan al pulsarlo: se comporta como
    /// mover una barra o cambiar el desplegable, y el cambio se confirma con Aplicar.
    /// </summary>
    private void TurboDefaultsButton_Click(object sender, RoutedEventArgs e)
        => StageShortcut(recommended: false);

    /// <summary>
    /// Atajo «Recomendado»: deja cargado el perfil de rendimiento (límite 100 % y boost agresivo
    /// si el equipo lo expone, más los avanzados que el catálogo recomienda para el perfil
    /// elegido) como cambio PENDIENTE. Igual que «Predeterminado», se aplica con Aplicar.
    /// </summary>
    private void TurboRecommendedButton_Click(object sender, RoutedEventArgs e)
        => StageShortcut(recommended: true);

    /// <summary>
    /// Carga en las barras lo que aplicaría un atajo, sin escribir nada en el plan de energía.
    /// Se guarda solo lo que cambia algo (lo que ya está aplicado se descarta) y se cuentan los
    /// cambios para el aviso; después Aplicar confirma todo junto, como cualquier otro cambio.
    /// </summary>
    private void StageShortcut(bool recommended)
    {
        if (_turboState == null) return;

        var state = _turboState;
        var draft = _drafts[_mode];
        var staged = 0;

        // ---- Techo: el recomendado es el máximo del plan dentro del rango; si no, el de fábrica ----
        if (state.LimitExposed)
        {
            int? target = recommended
                ? Math.Clamp(100, state.LimitMin, state.LimitMax)
                : LimitDefault(state) is int factory ? Math.Clamp(factory, state.LimitMin, state.LimitMax) : null;
            if (target != null)
            {
                if (target == LimitValue(state))
                {
                    draft.LimitPercent = null;   // ya está en ese valor: nada que confirmar
                }
                else
                {
                    draft.LimitPercent = target;
                    staged++;
                }
            }
        }

        // ---- Modo boost: el mismo criterio que usaba el atajo cuando aplicaba de una ----
        if (state.BoostModeExposed)
        {
            var target = recommended ? RecommendedBoost(state) : BoostDefault(state);
            if (target != null && state.BoostModeOptions.Contains(target.Value))
            {
                if (target == BoostValue(state))
                    draft.BoostMode = null;
                else
                {
                    draft.BoostMode = target;
                    staged++;
                }
            }
        }

        // ---- Ajustes avanzados: solo los que el equipo expone y entran en su rango ----
        foreach (var setting in state.Advanced)
        {
            if (!setting.Exposed) continue;
            var target = recommended
                ? (_mode == TurboPowerMode.Dc ? setting.RecommendedDc : setting.RecommendedValue)
                : AdvancedDefault(setting);
            if (target == null) continue;
            if (target.Value < setting.Min || target.Value > setting.Max) continue;

            if (target == AdvancedValue(setting))
            {
                draft.Advanced.Remove(setting.SettingGuid);
                continue;
            }
            draft.Advanced[setting.SettingGuid] = target.Value;
            staged++;
        }

        if (staged == 0)
        {
            // El perfil ya está en esos valores. Se repinta por si el atajo descartó algún
            // borrador que ya coincidía con lo aplicado, y se informa lo que siga pendiente
            // (puede haber otro cambio movido a mano o en el otro perfil).
            ApplyTurboState(state);
            var pendingElsewhere = CountPendingChanges();
            Feedback.Info(TurboStatusText, pendingElsewhere == 0
                ? I18n.T("No hay ajustes pendientes de aplicar.")
                : I18n.T("{0} cambios pendientes de aplicar", pendingElsewhere), persistent: true);
            return;
        }

        // Repinta desde los borradores: barras, valores y badges quedan mostrando lo pendiente
        // y Aplicar se habilita. Nada se escribe en el plan hasta que se pulse ese botón.
        ApplyTurboState(state);
        Feedback.Info(TurboStatusText,
            I18n.T("{0} cambios pendientes de aplicar", CountPendingChanges()), persistent: true);
    }

    /// <summary>
    /// Total de valores que hoy difieren de lo aplicado, barra por barra y en los dos perfiles.
    /// Es el número que anuncia el aviso de los atajos: no cuenta solo lo que cargó el atajo,
    /// sino todo lo que Aplicar va a confirmar.
    /// </summary>
    private int CountPendingChanges()
    {
        if (_turboState == null) return 0;

        var other = _drafts[OtherMode()];
        var count = other.Advanced.Count
            + (other.BoostMode != null ? 1 : 0)
            + (other.LimitPercent != null ? 1 : 0);

        if (_turboState.BoostModeExposed && SelectedBoostMode() is int boost
            && boost != BoostValue(_turboState)) count++;

        if (_turboState.LimitExposed && LimitValue(_turboState) is int limit
            && (int)Math.Round(TurboLimitSlider.Value) != limit) count++;

        count += PendingAdvancedValues().Count;
        if (_profilePending) count++;
        return count;
    }

    /// <summary>Modo boost que recomienda la card: Agresivo si el equipo lo expone; si no, Activado.</summary>
    private static int? RecommendedBoost(TurboCeilingState s)
    {
        if (!s.BoostModeExposed) return null;
        if (s.BoostModeOptions.Contains(2)) return 2;
        if (s.BoostModeOptions.Contains(1)) return 1;
        return null;
    }
}
