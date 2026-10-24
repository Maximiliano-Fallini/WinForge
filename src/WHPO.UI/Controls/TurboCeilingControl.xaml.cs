using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

    public TurboCeilingControl()
    {
        InitializeComponent();
        _turboCeilingService = App.Services.GetRequiredService<ITurboCeilingService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();

        // La página de Núcleos se cachea: la suscripción se ata a Loaded/Unloaded para
        // no dejar el handler de idioma colgado al desmontar.
        Loaded += (_, _) =>
        {
            I18n.LanguageChanged -= OnLanguageChanged;
            I18n.LanguageChanged += OnLanguageChanged;
            Refresh();
        };
        Unloaded += (_, _) => I18n.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>
    /// Relee el techo de turbo del plan activo. Núcleos la llama al abrir la pestaña,
    /// para no mostrar el estado de un plan que ya no está vigente.
    /// </summary>
    public void Refresh() => _ = LoadTurboCeilingAsync();

    private void OnLanguageChanged()
    {
        I18n.ApplyToVisualTree(this);
        // Las etiquetas del modo boost y los textos del estado vacío los escribe el
        // código: se re-arman con el idioma nuevo.
        if (_turboState != null) ApplyTurboState(_turboState);
    }

    // ===================== Lectura del estado =====================

    private async Task LoadTurboCeilingAsync()
    {
        try
        {
            // powercfg + registro del catálogo de energía: fuera del hilo de UI.
            var state = await Task.Run(() => _turboCeilingService.GetState());
            _cpuName = state.CpuName;
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
            TurboBoostRow.Visibility = state.BoostModeExposed ? Visibility.Visible : Visibility.Collapsed;
            _boostOptionValues.Clear();
            if (state.BoostModeExposed && state.BoostModeValue != null)
            {
                _boostOptionValues.AddRange(state.BoostModeOptions);
                TurboBoostSlider.Minimum = 0;
                TurboBoostSlider.Maximum = Math.Max(0, _boostOptionValues.Count - 1);
                TurboBoostSlider.StepFrequency = 1;

                var index = _boostOptionValues.IndexOf(state.BoostModeValue.Value);
                TurboBoostSlider.Value = index >= 0 ? index : 0;
                TurboBoostValueText.Text = BoostModeLabel(state.BoostModeValue.Value);
                UpdateBoostModeHelp(state.BoostModeValue.Value);
            }
            else
            {
                TurboBoostValueText.Text = string.Empty;
                UpdateBoostModeHelp(null);
            }

            // ---- Límite de rendimiento (rango del equipo, nunca 0-100 fijo) ----
            TurboLimitRow.Visibility = state.LimitExposed ? Visibility.Visible : Visibility.Collapsed;
            // Se arma en código (no en el XAML) para que el texto envuelva dentro del tooltip y
            // siga el idioma: el tooltip de string del XAML se recortaba al llegar al ancho máximo.
            ToolTipService.SetToolTip(TurboLimitInfoButton, BuildLimitToolTip());
            if (state.LimitExposed && state.LimitPercent != null)
            {
                TurboLimitSlider.Minimum = state.LimitMin;
                TurboLimitSlider.Maximum = state.LimitMax;
                TurboLimitSlider.StepFrequency = state.LimitStep > 0 ? state.LimitStep : 1;
                TurboLimitSlider.Value = state.LimitPercent.Value;
                TurboLimitValueText.Text = FormatLimitValue(state.LimitPercent.Value, state.LimitUnits);
            }
            else
            {
                TurboLimitValueText.Text = string.Empty;
            }

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
            var hasFactoryValues = (state.LimitExposed && state.DefaultLimitPercent != null)
                || (state.BoostModeExposed && state.DefaultBoostMode != null)
                || state.Advanced.Exists(a => a.Exposed && a.DefaultValue != null);
            TurboDefaultsButton.IsEnabled = state.HasAnyControl && hasFactoryValues;
            // El perfil recomendado solo mueve el boost y el límite (los avanzados no se
            // tocan): si el equipo no expone ninguno de los dos, no hay nada que aplicar.
            TurboRecommendedButton.IsEnabled = state.LimitExposed || state.BoostModeExposed;
            TurboApplyButton.IsEnabled = false;   // se habilita cuando hay un cambio pendiente

 // Tooltips de los dos atajos: mismo formato que el resto de la card (título +
 // texto que envuelve), y se rearman acá porque el idioma cambia en caliente.
            ToolTipService.SetToolTip(TurboDefaultsButton, WrapToolTip(InfoToolTipContent(
                I18n.T("Predeterminado"),
                I18n.T("Vuelve a los valores predeterminados del plan de energía activo."))));
            ToolTipService.SetToolTip(TurboRecommendedButton, WrapToolTip(InfoToolTipContent(
                I18n.T("Recomendado"),
                I18n.T("Aplica el perfil recomendado: límite 100 % y modo boost Agresivo (o Activado si el equipo no declara el agresivo). No toca los ajustes avanzados."))));

            // Traza de lo que la card pinta, al final: si un valor aplicado no aparece
            // en la UI, esto dice si el estado llegó distinto del que el servicio escribió.
            _loggingService.LogInfo(
                $"Techo de turbo: pintando boost={state.BoostModeValue?.ToString() ?? "—"} límite={state.LimitPercent?.ToString() ?? "—"} " +
                string.Join(", ", state.Advanced.Where(a => a.Exposed).Select(a => $"{a.Key}={a.Value}")));
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

        var valueText = new TextBlock
        {
            Text = FormatAdvancedValue(setting.Value ?? setting.Min, setting.UnitSymbol),
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
            Value = setting.Value ?? setting.Min,
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
            if (!setting.Exposed || setting.Value == null) continue;
            if (!_advancedSliders.TryGetValue(setting.SettingGuid, out var slider)) continue;
            var value = (int)Math.Round(slider.Value);
            if (value != setting.Value.Value) pending[setting.SettingGuid] = value;
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
        if (_turboState == null) return;
        var pending = false;

        var boost = SelectedBoostMode();
        if (_turboState.BoostModeExposed && boost != null && boost != _turboState.BoostModeValue)
            pending = true;

        if (_turboState.LimitExposed && _turboState.LimitPercent != null &&
            (int)Math.Round(TurboLimitSlider.Value) != _turboState.LimitPercent.Value)
            pending = true;

        if (!pending && PendingAdvancedValues().Count > 0) pending = true;

        TurboApplyButton.IsEnabled = pending;
    }

    private async void TurboApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_turboState == null) return;
        TurboApplyButton.IsEnabled = false;
        TurboDefaultsButton.IsEnabled = false;
        TurboRecommendedButton.IsEnabled = false;
        Feedback.Running(TurboStatusText, I18n.T("Aplicando…"));

        try
        {
            var boost = _turboState.BoostModeExposed ? SelectedBoostMode() : null;
            var limit = _turboState.LimitExposed ? (int)Math.Round(TurboLimitSlider.Value) : (int?)null;

            // Solo se envía lo que realmente cambió; el botón ya está deshabilitado si no hay
            // nada pendiente, pero así el mensaje de error no confunde "sin cambios" con un fallo.
            var boostPending = boost != null && boost != _turboState.BoostModeValue;
            var limitPending = limit != null && limit != _turboState.LimitPercent;

 // Las lecturas de powercfg son síncronas y caras (varios procesos + WMI): si
 // corren en el hilo de UI, la ventana se congela varios segundos y parece que el
 // botón no hizo nada. El servicio se llama desde el pool y el resultado vuelve
 // al hilo de UI para pintar.
            var result = new CommandResult(true, "");
            if (boostPending || limitPending)
                result = await Task.Run(() => _turboCeilingService.ApplyAsync(
                    boostPending ? boost : null, limitPending ? limit : null));

            var advanced = PendingAdvancedValues();
            if (result.Success && advanced.Count > 0)
                result = await Task.Run(() => _turboCeilingService.ApplyAdvancedAsync(advanced));

            Feedback.Result(TurboStatusText, result);
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

    private async void TurboDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        TurboApplyButton.IsEnabled = false;
        TurboDefaultsButton.IsEnabled = false;
        TurboRecommendedButton.IsEnabled = false;
        Feedback.Running(TurboStatusText, I18n.T("Restaurando…"));

        try
        {
            var result = await Task.Run(() => _turboCeilingService.RestoreDefaultsAsync());
            Feedback.Result(TurboStatusText, result);
        }
        catch (Exception ex)
        {
            Feedback.Error(TurboStatusText, ex.Message);
            _loggingService.LogWarning($"TurboCeilingControl: error restaurando el techo de turbo: {ex.Message}");
        }
        finally
        {
            await LoadTurboCeilingAsync();
        }
    }

    /// <summary>
    /// Atajo "Recomendado": aplica el perfil de rendimiento (límite 100 % y boost agresivo si
    /// el equipo lo expone) en el momento, igual que los atajos de Optimizaciones, y relee el
    /// estado para mostrar lo que el equipo aceptó de verdad.
    /// </summary>
    private async void TurboRecommendedButton_Click(object sender, RoutedEventArgs e)
    {
        TurboApplyButton.IsEnabled = false;
        TurboDefaultsButton.IsEnabled = false;
        TurboRecommendedButton.IsEnabled = false;
        Feedback.Running(TurboStatusText, I18n.T("Aplicando…"));

        try
        {
            var result = await Task.Run(() => _turboCeilingService.ApplyRecommendedAsync());
            Feedback.Result(TurboStatusText, result);
        }
        catch (Exception ex)
        {
            Feedback.Error(TurboStatusText, ex.Message);
            _loggingService.LogWarning($"TurboCeilingControl: error aplicando el perfil recomendado: {ex.Message}");
        }
        finally
        {
            await LoadTurboCeilingAsync();
        }
    }
}
