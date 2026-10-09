using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// AURORA — base oscura con foto de aurora boreal detrás de toda la ventana.
///
/// Tema NUEVO (0.3.1). La identidad sale de la foto: verde y violeta sobre azul de noche,
/// así que el acento es un verde menta luminoso con un resplandor violeta de contrapunto
/// (así el tema también tiene identidad propia cuando se elige el fondo "Degradado").
/// </summary>
internal static class AuroraTheme
{
    public static ThemeDefinition Create() => new()
    {
        Theme = AppTheme.Aurora,
        Base = AppTheme.Dark,
        NameKey = "Aurora",
        Wallpaper = "aurora.jpg",
        WallpaperCredit = "Imagen local del usuario",
        // Tema de ENTORNO (trae foto): arranca con los paneles casi invisibles (90 %) y un vidrio
        // suave (5 %), que es como la foto se ve de verdad. Los dos números son POR TEMA.
        PanelTransparency = 90,
        PanelBlur = 5,
        Brushes =
        [
            new ThemePaint.Entry("AppBackgroundBrush", "#00000000"),
            new ThemePaint.Entry("WindowWallpaperBrush", ThemePaint.Image("aurora.jpg")),
            // La foto ya es oscura (cielo nocturno): el velo solo unifica el tono verdoso.
            new ThemePaint.Entry("WindowWallpaperScrimBrush", "#73102A33"),
            new ThemePaint.Entry("CardBackgroundBrush", "#FF07242A"),
            new ThemePaint.Entry("ToolTipBackgroundBrush", "#FF07242A"),
            new ThemePaint.Entry("CardBorderBrush", "#335FE6B4"),
            new ThemePaint.Entry("CardStrokeBrush", "#00000000"),   // sin contorno: ver BrasaTheme
            new ThemePaint.Entry("SecondaryTextBrush", "#FFA6C9C4"),

            new ThemePaint.Entry("AccentBrush", "#FF4FE0A8"),
            new ThemePaint.Entry("AccentForegroundBrush", "#FF04231B"),

            // Botón "Actualizar" de la barra de título (ver App.xaml): pastilla del menta
            // de la aurora (el acento que sale de la foto) con su rampa para hover/pressed.
            new ThemePaint.Entry("UpdateButtonBrush", "#FF4FE0A8"),
            new ThemePaint.Entry("UpdateButtonForegroundBrush", "#FF04231B"),
            new ThemePaint.Entry("UpdateButtonPointerOverBrush", "#FF7FEAC4"),
            new ThemePaint.Entry("UpdateButtonPressedBrush", "#FF35C08E"),

            new ThemePaint.Entry("ChartBackgroundBrush", "#66051620"),
            new ThemePaint.Entry("ChartBackgroundHotBrush", "#66061D22"),
            new ThemePaint.Entry("ChartGridBrush", "#33245A52"),
            new ThemePaint.Entry("ChartCrosshairBrush", "#80607F88"),
            new ThemePaint.Entry("ChartAxisTextBrush", "#FF7FA3AB"),
            new ThemePaint.Entry("ChartHoverBadgeBgBrush", "#E607242A"),
            new ThemePaint.Entry("ChartHoverBadgeBorderBrush", "#4D4FE0A8"),
            new ThemePaint.Entry("ChartHoverTextBrush", "#FFEAFBF8"),

            new ThemePaint.Entry("MetricUsageBrush", "#FF5AD1E8"),
            new ThemePaint.Entry("MetricTempBrush", "#FF86E29B"),
            new ThemePaint.Entry("MetricPowerBrush", "#FFF5CC66"),

            new ThemePaint.Entry("SensorGridLineBrush", "#33245A52"),
            new ThemePaint.Entry("SensorGroupFillBrush", "#A607242A"),
            new ThemePaint.Entry("SensorCategoryFillBrush", "#8C051C22"),

            new ThemePaint.Entry("DisabledCardBackgroundBrush", "#A62B1C1C"),
            new ThemePaint.Entry("DisabledCardTextBrush", "#FFFFC1BC"),

            new ThemePaint.Entry("CardHoverBrush", "#26FFFFFF"),
            new ThemePaint.Entry("CardSelectedBrush", "#404FE0A8"),
            new ThemePaint.Entry("AccentTintBrush", "#334FE0A8"),
            new ThemePaint.Entry("MutedBrush", "#FF7FA8A4"),

            new ThemePaint.Entry("OnboardingScrimBrush", "#55000000"),
            new ThemePaint.Entry("OnboardingSurfaceBrush", "#D907242A"),

            new ThemePaint.Entry("ChipBackgroundBrush", "#FF123A3E"),

            new ThemePaint.Entry("CoreCardBackgroundBrush", "#FF07242A"),
            new ThemePaint.Entry("CoreTrackBackgroundBrush", "#8C051A1F"),

            new ThemePaint.Entry("WindowBackdropBrush", ThemePaint.Linear(0, 0, 0.6, 1,
                (0.0, "#FF04141C"), (0.5, "#FF07262C"), (1.0, "#FF03131F"))),
            new ThemePaint.Entry("WindowGlowABrush", ThemePaint.Glow("#4034D399", "#1F0F766E")),
            new ThemePaint.Entry("WindowGlowBBrush", ThemePaint.Glow("#458B5CF6", "#1F5B21B6"))
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FF4FE0A8"),
            ("SystemAccentColorForegroundBrush", "#FF04231B"),
            ("AccentFillColorDefaultBrush", "#FF4FE0A8"),
            ("AccentFillColorSecondaryBrush", "#E64FE0A8"),
            ("AccentFillColorTertiaryBrush", "#CC4FE0A8"),
            ("NavigationViewDefaultPaneBackground", "#FF08242C"),
            ("NavigationViewContentBackground", "#00000000"),
            ("ComboBoxDropDownBackground", "#F207242A"),
            // Cuadros de texto: mismo tono que el desplegable (superficies del censo: ver
            // PanelAppearance.SurfaceKeys).
            ("TextControlBackground", "#FF07242A"),
            ("TextControlBackgroundPointerOver", "#FF0A2E34"),
            ("TextControlBackgroundFocused", "#FF0D383E"),
            ("TextControlBackgroundDisabled", "#FF07242A"),
            ("ComboBoxDropDownBackgroundPointerOver", "#F20A2E34"),
            ("ComboBoxDropDownBackgroundPointerPressed", "#F20D383E"),
            ("ComboBoxDropDownBorderBrush", "#4D5FE6B4"),
            ("MenuFlyoutPresenterBackground", "#F207242A"),
            ("MenuFlyoutPresenterBorderBrush", "#4D5FE6B4"),
            ("ComboBoxItemBackgroundPointerOver", "#FF0A2E34"),
            ("ComboBoxItemBackgroundSelected", "#FF0E3C42"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "#FF12484E"),
            ("ComboBoxItemBackgroundSelectedPressed", "#FF0E3C42")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FF4FE0A8"),
            ("SystemAccentColorLight1", "#FF7FEAC4"),
            ("SystemAccentColorLight2", "#FF4FE0A8"),
            ("SystemAccentColorLight3", "#FF3FD099"),
            ("SystemAccentColorDark1", "#FF35C08E"),
            ("SystemAccentColorDark2", "#FF26A176"),
            ("SystemAccentColorDark3", "#FF1B8360")
        ]
    };
}
