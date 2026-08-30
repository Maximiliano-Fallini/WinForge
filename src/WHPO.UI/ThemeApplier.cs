using System;
using Microsoft.UI.Xaml;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Implementación WinUI de IThemeApplier.
/// Aplica el tema a la ventana raíz de la aplicación.
/// </summary>
public class ThemeApplier : IThemeApplier
{
    private Window? _mainWindow;
    private AppTheme? _lastAppliedTheme;

    /// <summary>
    /// Establece la ventana raíz a la que se le aplicará el tema.
    /// Si ya se aplicó un tema antes (p. ej. ThemeService.Initialize() corrió
    /// antes de que la ventana existiera), se reaplica ahora para que el arranque
    /// respete el tema guardado (claro/oscuro).
    /// </summary>
    public void SetMainWindow(Window window)
    {
        _mainWindow = window;
        if (_lastAppliedTheme is { } theme)
            ApplyTheme(theme);
    }

    public void ApplyTheme(AppTheme theme)
    {
        var previousTheme = _lastAppliedTheme;

        // Gestión de paletas: restaurar el diccionario del tema ANTERIOR y pisar
        // los pinceles de identidad del nuevo. RestoreBase es no-op si el tema
        // anterior no tiene paleta propia. La restauración va ANTES de la
        // aplicación para que nunca queden pinceles mezclados entre paletas.
        ThemePalettes.RestoreBase(ThemePalettes.BaseThemeFor(previousTheme ?? theme));
        ThemePalettes.ApplyPalette(theme);

        _lastAppliedTheme = theme;

        var elementTheme = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            AppTheme.PinkLight => ElementTheme.Light,   // base clara con paleta rosa
            AppTheme.BlueBlack => ElementTheme.Dark,    // base oscura con paleta azul
            _ => ElementTheme.Default
        };

        // Aplicar el tema al contenido de la ventana
        if (_mainWindow.Content is FrameworkElement rootElement)
        {
            // Los {ThemeResource} SOLO se re-evalúan con un cambio de tema efectivo.
            // Las transiciones que involucran paletas propias pueden dejar el tema
            // efectivo igual (Rosa/Blanco → Claro, o Sistema → Rosa/Blanco cuando el
            // sistema ya está en claro): alternamos y volvemos EN EL MISMO TICK para
            // forzar la re-evaluación de los pinceles (no hay frame intermedio).
            bool paletteSwitched = ThemePalettes.HasOwnPalette(theme)
                || (previousTheme is { } prev && ThemePalettes.HasOwnPalette(prev));
            if (paletteSwitched && rootElement.RequestedTheme == elementTheme)
            {
                rootElement.RequestedTheme = elementTheme == ElementTheme.Light
                    ? ElementTheme.Dark
                    : ElementTheme.Light;
            }
            rootElement.RequestedTheme = elementTheme;
        }
    }

    public AppTheme GetSystemTheme()
    {
        // Detectar el tema del sistema operativo
        var uiSettings = new Windows.UI.ViewManagement.UISettings();
        var fgColor = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);

        // Si el foreground es blanco, el tema es oscuro
        return (fgColor.R == 255 && fgColor.G == 255 && fgColor.B == 255)
            ? AppTheme.Dark
            : AppTheme.Light;
    }

}