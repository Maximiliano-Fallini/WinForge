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

    // Fondos desde los recursos de tema: siguen la paleta activa (oscuro/claro).
    // Se resuelven con el tema EFECTIVO de la app (ThemeBrushes), no con el del sistema.
    private static SolidColorBrush CardBgDefault => ThemeBrushes.Get("CardBackgroundBrush");
    private static SolidColorBrush CardBgSelected => ThemeBrushes.Get("CardSelectedBrush");
    private static SolidColorBrush CardBgDisabled => ThemeBrushes.Get("DisabledCardBackgroundBrush");
    private static SolidColorBrush AccentBrush => ThemeBrushes.Get("AccentBrush");
    private static SolidColorBrush RedBorderBrush => (SolidColorBrush)App.Current.Resources["ErrorBrush"];
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

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
        CurrentModeText.Text = I18n.T(policy.Title);
        CurrentDescriptionText.Text = I18n.T(policy.Description);

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
        UpdateCardVisual(DisabledCard, DisabledCardCheck, mode == WindowsUpdateMode.Disabled, CardBgDisabled, RedBorderBrush, CardBgSelected);
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
            ResultBar.Title = result.Success ? I18n.T("Configuración aplicada") : I18n.T("No se pudo aplicar la configuración");
            ResultBar.Message = result.Success
                ? I18n.T(result.Message) + (result.RestartRecommended ? " " + I18n.T("Reinicia el equipo para completar todos los cambios.") : string.Empty)
                : I18n.T(result.Message);
            ResultBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ResultBar.IsOpen = true;

            if (!string.IsNullOrWhiteSpace(result.Details))
            {
                ResultBar.Message += " " + I18n.T("Algunos componentes informaron advertencias; consulta el registro de WHPO para más detalles.");
            }

            RefreshCurrentPolicy();
        }
        catch (Exception ex)
        {
            ResultBar.Title = I18n.T("Error inesperado");
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
            Title = I18n.T("¿Desactivar Windows Update?"),
            Content = I18n.T("Esto detendrá los servicios y tareas de actualización, borrará su caché y dejarás de recibir actualizaciones de seguridad. Podrás revertirlo con Predeterminado de Windows o Recomendado."),
            PrimaryButtonText = I18n.T("Desactivar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetControlsEnabled(bool enabled)
    {
        ApplyProfileButton.IsEnabled = enabled && _selectedMode != null;
    }
}
