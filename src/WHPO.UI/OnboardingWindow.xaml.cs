using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Flujo de onboarding de primera ejecución: el asistente REAL, que al terminar
/// persiste el tema elegido y marca el onboarding como completado. La vista
/// previa de desarrollo (OnboardingSimulatorWindow) hereda esta misma UI pero
/// sobreescribe los puntos donde se escribe configuración.
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int PhaseCount = 2;
    private const string FirstRunKey = "onboarding.complete";

    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly ILoggingService _loggingService;

    private int _phase;
    private AppTheme _selectedTheme;
    private UIElement[] _panels = Array.Empty<UIElement>();
    private readonly List<Border> _progressDots = new();
    private readonly List<Border> _progressLines = new();

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
        Closed += (_, _) => I18n.LanguageChanged -= OnLanguageChanged;

        _panels = new UIElement[] { PhaseTheme, PhaseThanks };

        BuildProgressDots();
        ApplyTexts();
        SetSelectedTheme(_themeService.CurrentTheme);

        _phase = 0;
        UpdatePhaseUi(animateIn: false);

        Title = WindowTitleText;
    }

    private void BuildProgressDots()
    {
        for (int i = 0; i < PhaseCount; i++)
        {
            if (i > 0)
            {
                // Línea que conecta los puntos completados.
                var line = new Border
                {
                    Width = 30,
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    Margin = new Thickness(0, 4, 0, 0),
                    Background = ThemeBrushes.Get("MutedBrush")
                };
                _progressLines.Add(line);
                ProgressDots.Children.Add(line);
            }

            var dot = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = ThemeBrushes.Get("MutedBrush")
            };
            _progressDots.Add(dot);
            ProgressDots.Children.Add(dot);
        }
    }

    private static Microsoft.UI.Xaml.Media.FontFamily UiSymbolFontFamily()
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var r)
            && r is Microsoft.UI.Xaml.Media.FontFamily ff)
        {
            return ff;
        }
        return new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons");
    }

    // ----- Cambio de fase -----

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
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

    private void StarButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/Maximiliano-Fallini/WinForge/stargazers")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Onboarding: abrir GitHub: {ex.Message}");
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

        UpdateProgressDots();

        if (animateIn)
        {
            AnimateIn(_panels[_phase]);
        }
    }

    private void UpdateProgressDots()
    {
        var done = ThemeBrushes.Get("AccentBrush", _selectedTheme);
        var inactive = ThemeBrushes.Get("MutedBrush", _selectedTheme);

        for (int i = 0; i < _progressDots.Count; i++)
        {
            _progressDots[i].Background = i <= _phase ? done : inactive;
            _progressDots[i].Width = 10;
            _progressDots[i].Height = 10;
        }
        for (int i = 0; i < _progressLines.Count; i++)
        {
            // La línea entre el punto i y i+1 se completa cuando se pasó el punto i+1.
            _progressLines[i].Background = (i + 1) <= _phase ? done : inactive;
        }
    }

    private void AnimateIn(UIElement element)
    {
        if (element is FrameworkElement fe && fe.RenderTransform is TranslateTransform tt)
            tt.Y = 16;

        var sb = new Storyboard();

        var opacity = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(280) };
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(opacity, element);

        var translate = new DoubleAnimation { From = 16, To = 0, Duration = TimeSpan.FromMilliseconds(300) };
        Storyboard.SetTargetProperty(translate, "RenderTransform.(TranslateTransform.Y)");
        Storyboard.SetTarget(translate, element);

        sb.Children.Add(opacity);
        sb.Children.Add(translate);
        sb.Begin();
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
        SetThemeCard(ThemeDarkCard, _selectedTheme == AppTheme.Dark);
        SetThemeCard(ThemeLightCard, _selectedTheme == AppTheme.Light);
        SetThemeCard(ThemeSystemCard, _selectedTheme == AppTheme.SystemDefault);
    }

    private void SetThemeCard(Border card, bool selected)
    {
        if (card == null) return;
        card.BorderBrush = selected
            ? ThemeBrushes.Get("AccentBrush", _selectedTheme)
            : ThemeBrushes.Get("CardBorderBrush", _selectedTheme);
        card.BorderThickness = new Thickness(selected ? 2 : 1);
    }

    private void UpdateOnboardingTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => ElementTheme.Default
        };
    }

    // ----- Idioma -----

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

    // ----- Textos -----

    private void ApplyTexts()
    {
        SubtitleText.Text = I18n.T("Configurá WinForge a tu gusto. Son 2 pasos y podés cambiarlo todo después.");
        BackButton.Content = I18n.T("Atrás");
        NextButton.Content = I18n.T("Continuar");
        StarButton.Content = I18n.T("Dejar una estrella en GitHub");
    }

    private void OnLanguageChanged()
    {
        I18n.ApplyToVisualTree(RootGrid);
        ApplyTexts();
        Title = WindowTitleText;
        UpdatePhaseUi(animateIn: false);
    }
}
