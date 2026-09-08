using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Página "Filtro de teclas" (componente "teclado"): configura la repetición del
/// teclado con tres valores en milisegundos, los aplica en vivo al instante
/// (SPI_SETFILTERKEYS, igual que FilterKeysSetter) y los guarda en el registro
/// del sistema para que persistan.
///
/// Las macros son un componente separado (MacrosPage).
/// </summary>
public sealed partial class TecladoPage : Page
{
    private readonly IKeyboardService _keyboardService;
    private readonly ILoggingService _loggingService;
    private bool _loaded;
    private Button? _selectedPreset;

    // Valores por defecto de Windows (Keyboard Response): Slow Keys 0 / Retardo 250 / Velocidad 33.
    private const int DefaultIgnoreMs = 0;
    private const int DefaultDelayMs = 250;
    private const int DefaultRateMs = 33;

    // Preset "Optimizada": los valores que yo uso (fijos, NO se leen del registro para
    // que no se igualen al default cuando el registro queda vacío).
    private const int OptimizedIgnoreMs = 0;
    private const int OptimizedDelayMs = 130;
    private const int OptimizedRateMs = 20;

    public TecladoPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _keyboardService = App.Services.GetRequiredService<IKeyboardService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_loaded)
        {
            _loaded = true;
            LoadCurrentValues();
        }
    }

    private void LoadCurrentValues()
    {
        try
        {
            var s = _keyboardService.GetSettings();
            IgnoreUnderBox.Value = s.IgnoreUnderMs;
            RepeatDelayBox.Value = s.RepeatDelayMs;
            RepeatRateBox.Value = s.RepeatRateMs;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"TecladoPage: no se pudo leer la configuración: {ex.Message}");
        }
    }

    /// <summary>
    /// Presets como botones: completan los 3 campos con su configuración. Nunca
    /// aplican por sí solos; el usuario ajusta y da a Aplicar. El seleccionado
    /// queda resaltado hasta que se elija otro.
    /// </summary>
    private void DefaultPresetButton_Click(object sender, RoutedEventArgs e)
    {
        SelectPreset(DefaultPresetButton);
        IgnoreUnderBox.Value = DefaultIgnoreMs;
        RepeatDelayBox.Value = DefaultDelayMs;
        RepeatRateBox.Value = DefaultRateMs;
    }

    private void OptimizedPresetButton_Click(object sender, RoutedEventArgs e)
    {
        SelectPreset(OptimizedPresetButton);
        IgnoreUnderBox.Value = OptimizedIgnoreMs;
        RepeatDelayBox.Value = OptimizedDelayMs;
        RepeatRateBox.Value = OptimizedRateMs;
    }

    private void CurrentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        SelectPreset(CurrentPresetButton);
        try
        {
            var s = _keyboardService.GetSettings();
            IgnoreUnderBox.Value = s.IgnoreUnderMs;
            RepeatDelayBox.Value = s.RepeatDelayMs;
            RepeatRateBox.Value = s.RepeatRateMs;
        }
        catch (Exception ex)
        {
            Feedback.Info(FeedbackText, I18n.T("No se pudieron leer los valores actuales: {0}", ex.Message));
        }
    }

    /// <summary>Resalta el preset seleccionado y deselecciona el anterior.</summary>
    private void SelectPreset(Button button)
    {
        if (_selectedPreset != null && _selectedPreset != button)
            _selectedPreset.Style = (Style)Resources["PresetButtonStyle"];
        button.Style = (Style)Resources["PresetButtonSelectedStyle"];
        _selectedPreset = button;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int? ignore = GetValue(IgnoreUnderBox);
            int? delay = GetValue(RepeatDelayBox);
            int? rate = GetValue(RepeatRateBox);

            if (ignore == null || delay == null || rate == null)
            {
                Feedback.Error(FeedbackText, "Ingresá un número válido en los 3 campos antes de aplicar.");
                return;
            }

            if (_keyboardService.Apply(ignore.Value, delay.Value, rate.Value, SaveToRegistrySwitch.IsOn, out string error))
            {
                Feedback.Success(FeedbackText,
                    SaveToRegistrySwitch.IsOn
                        ? "Aplicado en vivo y guardado en el registro."
                        : "Aplicado en vivo (sin guardar en el registro).");
            }
            else
            {
                Feedback.Error(FeedbackText, I18n.T("No se pudo aplicar: {0}", error));
            }
        }
        catch (Exception ex)
        {
            Feedback.Error(FeedbackText, I18n.T("Error al aplicar: {0}", ex.Message));
            _loggingService.LogWarning($"TecladoPage: error aplicando: {ex.Message}");
        }
    }

    /// <summary>
    /// Devuelve el valor entero del NumberBox, o null si no es un número válido
    /// (campo vacío / sin terminar). Evita escribir basura (p. ej. 30000) en el registro.
    /// </summary>
    private static int? GetValue(NumberBox box)
    {
        double v = box.Value;
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        return (int)Math.Clamp(v, 0, 5000);
    }
}
