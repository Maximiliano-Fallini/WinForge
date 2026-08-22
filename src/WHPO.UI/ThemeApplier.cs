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
        _lastAppliedTheme = theme;
        if (_mainWindow == null)
            return;

        var elementTheme = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => ElementTheme.Default
        };

        // Aplicar el tema al contenido de la ventana
        if (_mainWindow.Content is FrameworkElement rootElement)
        {
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