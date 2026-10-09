using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// ROSA / BLANCO — base clara, sin imagen de fondo.
///
/// Fondo rosa bien marcado y cards BLANCAS: el contraste viene del color, no de un borde
/// apenas visible. El navbar blanco forma un solo bloque con las cards.
/// </summary>
internal static class RosaBlancoTheme
{
    public static ThemeDefinition Create() => new()
    {
        Theme = AppTheme.PinkLight,
        Base = AppTheme.Light,
        NameKey = "Rosa / Blanco",
        // Tema PLANO (sin foto): arranca opaco y sin desenfoque. Los dos números son POR TEMA:
        // si el usuario los mueve, este tema los recuerda y los demás siguen con los suyos.
        PanelTransparency = 0,
        PanelBlur = 0,
        Brushes =
        [
            new ThemePaint.Entry("AppBackgroundBrush", "#FFF9E6F0"),
            new ThemePaint.Entry("CardBackgroundBrush", "#FFFFFFFF"),
            new ThemePaint.Entry("ToolTipBackgroundBrush", "#FFFFFFFF"),
            new ThemePaint.Entry("CardBorderBrush", "#FFF2C9DD"),
            new ThemePaint.Entry("CardStrokeBrush", "#00000000"),   // sin contorno: ver BrasaTheme
            new ThemePaint.Entry("SecondaryTextBrush", "#FF8A5E74"),

            new ThemePaint.Entry("AccentBrush", "#FFD6338A"),
            new ThemePaint.Entry("AccentForegroundBrush", "#FFFFFFFF"),

            // Botón "Actualizar" de la barra de título (ver App.xaml): pastilla del acento
            // del tema con su rampa para hover/pressed. Vive en Brushes para que
            // ThemePalettes la escriba y el snapshot la restaure como el resto de la paleta.
            new ThemePaint.Entry("UpdateButtonBrush", "#FFD6338A"),
            new ThemePaint.Entry("UpdateButtonForegroundBrush", "#FFFFFFFF"),
            new ThemePaint.Entry("UpdateButtonPointerOverBrush", "#FFDF4F9C"),
            new ThemePaint.Entry("UpdateButtonPressedBrush", "#FFB02570"),

            new ThemePaint.Entry("ChartBackgroundBrush", "#FFFDF2F8"),
            new ThemePaint.Entry("ChartBackgroundHotBrush", "#FFFBE4EF"),
            new ThemePaint.Entry("ChartGridBrush", "#FFF3D5E4"),
            new ThemePaint.Entry("ChartCrosshairBrush", "#FFDBA3C3"),
            new ThemePaint.Entry("ChartAxisTextBrush", "#FF9A6E85"),
            new ThemePaint.Entry("ChartHoverBadgeBgBrush", "#FFFBE4EF"),
            new ThemePaint.Entry("ChartHoverBadgeBorderBrush", "#FFE8C0D4"),
            new ThemePaint.Entry("ChartHoverTextBrush", "#FF4A2436"),

            new ThemePaint.Entry("MetricUsageBrush", "#FFC2185B"),
            new ThemePaint.Entry("MetricTempBrush", "#FF3A9A4A"),
            new ThemePaint.Entry("MetricPowerBrush", "#FFC99600"),

            new ThemePaint.Entry("SensorGridLineBrush", "#FFF3D5E4"),
            new ThemePaint.Entry("SensorGroupFillBrush", "#FFFFFFFF"),
            new ThemePaint.Entry("SensorCategoryFillBrush", "#FFFBEEF5"),

            new ThemePaint.Entry("DisabledCardBackgroundBrush", "#FFFBE9E6"),
            new ThemePaint.Entry("DisabledCardTextBrush", "#FF8A4A42"),

            new ThemePaint.Entry("CardHoverBrush", "#FFFDF1F7"),
            new ThemePaint.Entry("CardSelectedBrush", "#FFF9DCEA"),
            new ThemePaint.Entry("AccentTintBrush", "#22D6338A"),
            new ThemePaint.Entry("MutedBrush", "#FFB0879C"),

            new ThemePaint.Entry("OnboardingScrimBrush", "#73FFFFFF"),
            new ThemePaint.Entry("OnboardingSurfaceBrush", "#E6FFFFFF"),

            new ThemePaint.Entry("ChipBackgroundBrush", "#FFFBEEF5"),

            new ThemePaint.Entry("CoreCardBackgroundBrush", "#FFFCF3F8"),
            new ThemePaint.Entry("CoreTrackBackgroundBrush", "#FFF6DEEA")
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FFD6338A"),
            ("SystemAccentColorForegroundBrush", "#FFFFFFFF"),
            ("AccentFillColorDefaultBrush", "#FFD6338A"),
            ("AccentFillColorSecondaryBrush", "#E6D6338A"),
            ("AccentFillColorTertiaryBrush", "#CCD6338A"),
            ("NavigationViewDefaultPaneBackground", "#FFFFFFFF"),
            ("NavigationViewContentBackground", "#FFF9E6F0"),
            ("ComboBoxDropDownBackground", "#FFFFFFFF"),
            // Cuadros de texto: mismo tono que el desplegable (superficies del censo: ver
            // PanelAppearance.SurfaceKeys).
            ("TextControlBackground", "#FFFFFFFF"),
            ("TextControlBackgroundPointerOver", "#FFFDF1F7"),
            ("TextControlBackgroundFocused", "#FFF9DCEA"),
            ("TextControlBackgroundDisabled", "#FFFFFFFF"),
            ("ComboBoxDropDownBackgroundPointerOver", "#FFFDF1F7"),
            ("ComboBoxDropDownBackgroundPointerPressed", "#FFF9DCEA"),
            ("ComboBoxDropDownBorderBrush", "#FFF2C9DD"),
            ("MenuFlyoutPresenterBackground", "#FFFFFFFF"),
            ("MenuFlyoutPresenterBorderBrush", "#FFF2C9DD"),
            ("ComboBoxItemBackgroundPointerOver", "#FFFDF1F7"),
            ("ComboBoxItemBackgroundSelected", "#FFF9DCEA"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "#FFF5CBE1"),
            ("ComboBoxItemBackgroundSelectedPressed", "#FFF9DCEA")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FFD6338A"),
            ("SystemAccentColorLight1", "#FFDF4F9C"),
            ("SystemAccentColorLight2", "#FFD6338A"),
            ("SystemAccentColorLight3", "#FFC22A7B"),
            ("SystemAccentColorDark1", "#FFB02570"),
            ("SystemAccentColorDark2", "#FF9A2061"),
            ("SystemAccentColorDark3", "#FF841B53")
        ]
    };
}
