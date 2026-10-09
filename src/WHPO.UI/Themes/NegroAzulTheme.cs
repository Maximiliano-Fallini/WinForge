using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// NEGRO / AZUL — base oscura (azul-negro profundo, acento celeste), sin imagen de fondo.
///
/// Todo el tema vive en la misma familia azul: fondo #060A12 y cards/navbar #0E1524 forman
/// UN solo bloque, con el celeste #4FC3F7 como único color de acento.
/// </summary>
internal static class NegroAzulTheme
{
    public static ThemeDefinition Create() => new()
    {
        Theme = AppTheme.BlueBlack,
        Base = AppTheme.Dark,
        NameKey = "Negro / Azul",
        // Tema PLANO (sin foto): arranca opaco y sin desenfoque. Los dos números son POR TEMA:
        // si el usuario los mueve, este tema los recuerda y los demás siguen con los suyos.
        PanelTransparency = 0,
        PanelBlur = 0,
        Brushes =
        [
            new ThemePaint.Entry("AppBackgroundBrush", "#FF060A12"),
            new ThemePaint.Entry("CardBackgroundBrush", "#FF0E1524"),
            new ThemePaint.Entry("ToolTipBackgroundBrush", "#FF0E1524"),
            new ThemePaint.Entry("CardBorderBrush", "#FF223047"),
            new ThemePaint.Entry("CardStrokeBrush", "#00000000"),   // sin contorno: ver BrasaTheme
            new ThemePaint.Entry("SecondaryTextBrush", "#FF8FA3BC"),

            new ThemePaint.Entry("AccentBrush", "#FF4FC3F7"),
            new ThemePaint.Entry("AccentForegroundBrush", "#FF04121C"),

            // Botón "Actualizar" de la barra de título (ver App.xaml): pastilla del celeste
            // del tema con su rampa para hover/pressed.
            new ThemePaint.Entry("UpdateButtonBrush", "#FF4FC3F7"),
            new ThemePaint.Entry("UpdateButtonForegroundBrush", "#FF04121C"),
            new ThemePaint.Entry("UpdateButtonPointerOverBrush", "#FF6BD0FA"),
            new ThemePaint.Entry("UpdateButtonPressedBrush", "#FF2FA9E0"),

            new ThemePaint.Entry("ChartBackgroundBrush", "#FF05080E"),
            new ThemePaint.Entry("ChartBackgroundHotBrush", "#FF0B1626"),
            new ThemePaint.Entry("ChartGridBrush", "#FF16202F"),
            new ThemePaint.Entry("ChartCrosshairBrush", "#FF33415A"),
            new ThemePaint.Entry("ChartAxisTextBrush", "#FF66788F"),
            new ThemePaint.Entry("ChartHoverBadgeBgBrush", "#FF16202F"),
            new ThemePaint.Entry("ChartHoverBadgeBorderBrush", "#FF223047"),
            new ThemePaint.Entry("ChartHoverTextBrush", "#FFE8F2FC"),

            new ThemePaint.Entry("MetricUsageBrush", "#FF4CC2C9"),
            new ThemePaint.Entry("MetricTempBrush", "#FF4CC257"),
            new ThemePaint.Entry("MetricPowerBrush", "#FFFFC93C"),

            new ThemePaint.Entry("SensorGridLineBrush", "#FF223047"),
            new ThemePaint.Entry("SensorGroupFillBrush", "#FF0E1524"),
            new ThemePaint.Entry("SensorCategoryFillBrush", "#FF0A0F1A"),

            new ThemePaint.Entry("DisabledCardBackgroundBrush", "#FF2B1C1C"),
            new ThemePaint.Entry("DisabledCardTextBrush", "#FFFFC1BC"),

            new ThemePaint.Entry("CardHoverBrush", "#FF141C2E"),
            new ThemePaint.Entry("CardSelectedBrush", "#FF13233A"),
            new ThemePaint.Entry("AccentTintBrush", "#224FC3F7"),
            new ThemePaint.Entry("MutedBrush", "#FF7A8CA3"),

            new ThemePaint.Entry("OnboardingScrimBrush", "#55000000"),
            new ThemePaint.Entry("OnboardingSurfaceBrush", "#D90E1524"),

            new ThemePaint.Entry("ChipBackgroundBrush", "#FF16202F"),

            new ThemePaint.Entry("CoreCardBackgroundBrush", "#FF0E1524"),
            new ThemePaint.Entry("CoreTrackBackgroundBrush", "#FF070B12")
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FF4FC3F7"),
            ("SystemAccentColorForegroundBrush", "#FF04121C"),
            ("AccentFillColorDefaultBrush", "#FF4FC3F7"),
            ("AccentFillColorSecondaryBrush", "#E64FC3F7"),
            ("AccentFillColorTertiaryBrush", "#CC4FC3F7"),
            ("NavigationViewDefaultPaneBackground", "#FF0E1524"),
            ("NavigationViewContentBackground", "#FF060A12"),
            ("ComboBoxDropDownBackground", "#FF0E1524"),
            // Cuadros de texto: mismo tono que el desplegable (superficies del censo: ver
            // PanelAppearance.SurfaceKeys).
            ("TextControlBackground", "#FF0E1524"),
            ("TextControlBackgroundPointerOver", "#FF111A2C"),
            ("TextControlBackgroundFocused", "#FF141C2E"),
            ("TextControlBackgroundDisabled", "#FF0E1524"),
            ("ComboBoxDropDownBackgroundPointerOver", "#FF111A2C"),
            ("ComboBoxDropDownBackgroundPointerPressed", "#FF141C2E"),
            ("ComboBoxDropDownBorderBrush", "#FF223047"),
            ("MenuFlyoutPresenterBackground", "#FF0E1524"),
            ("MenuFlyoutPresenterBorderBrush", "#FF223047"),
            ("ComboBoxItemBackgroundPointerOver", "#FF141C2E"),
            ("ComboBoxItemBackgroundSelected", "#FF13233A"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "#FF182A44"),
            ("ComboBoxItemBackgroundSelectedPressed", "#FF13233A")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FF4FC3F7"),
            ("SystemAccentColorLight1", "#FF6BD0FA"),
            ("SystemAccentColorLight2", "#FF4FC3F7"),
            ("SystemAccentColorLight3", "#FF3AB4EE"),
            ("SystemAccentColorDark1", "#FF2FA9E0"),
            ("SystemAccentColorDark2", "#FF1E93C8"),
            ("SystemAccentColorDark3", "#FF127CB0")
        ]
    };
}
