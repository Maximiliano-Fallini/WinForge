using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class ActualizacionesPage : Page
{
    private readonly IWindowsUpdateService _windowsUpdateService;
    private WindowsUpdateMode? _selectedMode;

    private static readonly SolidColorBrush CardBgDefaultDark = new(Windows.UI.Color.FromArgb(255, 0x26, 0x2A, 0x31));
    private static readonly SolidColorBrush CardBgDefaultLight = new(Windows.UI.Color.FromArgb(255, 0xF7, 0xF8, 0xFA));
    private static readonly SolidColorBrush CardBgSelectedDark = new(Windows.UI.Color.FromArgb(255, 0x2F, 0x35, 0x41));
    private static readonly SolidColorBrush CardBgSelectedLight = new(Windows.UI.Color.FromArgb(255, 0xE2, 0xE9, 0xF6));
    private static readonly SolidColorBrush CardBgDisabled = new(Windows.UI.Color.FromArgb(255, 48, 34, 31));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(255, 138, 180, 248));
    private static readonly SolidColorBrush RedBorderBrush = new(Windows.UI.Color.FromArgb(255, 229, 115, 115));
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    // La card "Desactivar" es intencionalmente oscura (rojo apagado) en AMBOS temas y su
    // texto es claro, por eso su selección también se queda oscura; las otras dos siguen el tema.
    private SolidColorBrush CardBgDefault => ActualTheme == ElementTheme.Light ? CardBgDefaultLight : CardBgDefaultDark;
    private SolidColorBrush CardBgSelected => ActualTheme == ElementTheme.Light ? CardBgSelectedLight : CardBgSelectedDark;

    public ActualizacionesPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _windowsUpdateService = App.Services.GetRequiredService<IWindowsUpdateService>();
        Loaded += ActualizacionesPage_Loaded;

        // Si el tema cambia con la página abierta, re-aplicar los fondos de las cards.
        ActualThemeChanged += (s, e) =>
        {
            if (_selectedMode is { } mode) SelectMode(mode);
        };
    }

    private void ActualizacionesPage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshCurrentPolicy();
    }

    private void RefreshCurrentPolicy()
    {
        var policy = _windowsUpdateService.GetCurrentPolicy();
        CurrentModeText.Text = policy.Title;
        CurrentDescriptionText.Text = policy.Description;

        // Preseleccionar la card que coincide con el estado actual del sistema
        if (policy.Mode is WindowsUpdateMode.Default or WindowsUpdateMode.Recommended or WindowsUpdateMode.Disabled)
        {
            SelectMode(policy.Mode);
        }
    }

    private void ProfileCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<WindowsUpdateMode>(tag, out var mode))
        {
            SelectMode(mode);
        }
    }

    private void SelectMode(WindowsUpdateMode mode)
    {
        _selectedMode = mode;
        ApplyProfileButton.IsEnabled = true;

        UpdateCardVisual(DefaultCard, DefaultCardCheck, mode == WindowsUpdateMode.Default, CardBgDefault, TransparentBrush, CardBgSelected);
        UpdateCardVisual(RecommendedCard, RecommendedCardCheck, mode == WindowsUpdateMode.Recommended, CardBgDefault, TransparentBrush, CardBgSelected);
        // La card Desactivar mantiene fondo oscuro siempre (texto claro): seleccionada usa el navy oscuro.
        UpdateCardVisual(DisabledCard, DisabledCardCheck, mode == WindowsUpdateMode.Disabled, CardBgDisabled, RedBorderBrush, CardBgSelectedDark);
    }

    private static void UpdateCardVisual(Border card, FontIcon check, bool selected, SolidColorBrush unselectedBg, SolidColorBrush unselectedBorder, SolidColorBrush selectedBg)
    {
        card.BorderBrush = selected ? AccentBrush : unselectedBorder;
        card.BorderThickness = new Thickness(selected ? 2 : 1);
        card.Background = selected ? selectedBg : unselectedBg;
        check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ApplyProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMode is not { } mode)
        {
            return;
        }

        if (mode == WindowsUpdateMode.Disabled && !await ConfirmDisableAsync())
        {
            return;
        }

        SetControlsEnabled(false);
        ResultBar.IsOpen = false;

        try
        {
            var result = await _windowsUpdateService.ApplyPolicyAsync(mode);
            ResultBar.Title = result.Success ? "Configuración aplicada" : "No se pudo aplicar la configuración";
            ResultBar.Message = result.Success
                ? result.Message + (result.RestartRecommended ? " Reinicia el equipo para completar todos los cambios." : string.Empty)
                : result.Message;
            ResultBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ResultBar.IsOpen = true;

            if (!string.IsNullOrWhiteSpace(result.Details))
            {
                ResultBar.Message += " Algunos componentes informaron advertencias; consulta el registro de WHPO para más detalles.";
            }

            RefreshCurrentPolicy();
        }
        catch (Exception ex)
        {
            ResultBar.Title = "Error inesperado";
            ResultBar.Message = ex.Message;
            ResultBar.Severity = InfoBarSeverity.Error;
            ResultBar.IsOpen = true;
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async System.Threading.Tasks.Task<bool> ConfirmDisableAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "¿Desactivar Windows Update?",
            Content = "Esto detendrá los servicios y tareas de actualización, borrará su caché y dejarás de recibir actualizaciones de seguridad. Podrás revertirlo con Predeterminado de Windows o Recomendado.",
            PrimaryButtonText = "Desactivar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetControlsEnabled(bool enabled)
    {
        ApplyProfileButton.IsEnabled = enabled && _selectedMode != null;
    }
}
