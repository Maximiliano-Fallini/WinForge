using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// AMATISTA — base oscura con foto de amatista (cristal violeta) detrás de toda la ventana.
///
/// Tema de ENTORNO, hermano de Marea/Aurora/Brasa: mismo esquema (foto real + superficies
/// translúcidas), con la paleta de la piedra. La identidad sale de la foto —violetas y
/// lilas con brillos cálidos—, así que el acento es un lila luminoso que se lee sobre las
/// superficies oscuras y los resplandores son violeta/púrpura.
///
/// La foto es de tono medio (violeta) y con luz pareja, así que el velo es moderado:
/// alcanza para unificar el tono sin apagar la textura del cristal.
/// </summary>
internal static class AmatistaTheme
{
    public static ThemeDefinition Create() => new()
    {
        Theme = AppTheme.Amatista,
        Base = AppTheme.Dark,
        NameKey = "Amatista",
        Wallpaper = "amatista.jpg",
        WallpaperCredit = "Imagen local del usuario",
        // Tema de ENTORNO (trae foto): arranca con los paneles al 50 % y un vidrio
        // suave (5 %), que es como la foto se ve de verdad. Los dos números son POR TEMA.
        PanelTransparency = 50,
        PanelBlur = 5,
        Brushes =
        [
            // Páginas y contenedor del contenido transparentes: se ve la foto del fondo.
            new ThemePaint.Entry("AppBackgroundBrush", "#00000000"),
            new ThemePaint.Entry("WindowWallpaperBrush", ThemePaint.Image("amatista.jpg")),
            // Velo violeta oscuro: baja la luz de la piedra lo justo para que el texto
            // claro quede legible sin perder el brillo del cristal.
            new ThemePaint.Entry("WindowWallpaperScrimBrush", "#80160824"),
            new ThemePaint.Entry("CardBackgroundBrush", "#FF160D2B"),
            new ThemePaint.Entry("ToolTipBackgroundBrush", "#FF160D2B"),
            new ThemePaint.Entry("CardBorderBrush", "#33B18CF5"),
            new ThemePaint.Entry("CardStrokeBrush", "#00000000"),   // sin contorno: ver BrasaTheme
            new ThemePaint.Entry("SecondaryTextBrush", "#FFB4A6D6"),

            new ThemePaint.Entry("AccentBrush", "#FFB18CF5"),
            new ThemePaint.Entry("AccentForegroundBrush", "#FF1A1038"),

            // Botón "Actualizar" de la barra de título (ver App.xaml): pastilla del lila
            // de la piedra (el acento que sale de la foto) con su rampa para hover/pressed.
            new ThemePaint.Entry("UpdateButtonBrush", "#FFB18CF5"),
            new ThemePaint.Entry("UpdateButtonForegroundBrush", "#FF1A1038"),
            new ThemePaint.Entry("UpdateButtonPointerOverBrush", "#FFC7ABF8"),
            new ThemePaint.Entry("UpdateButtonPressedBrush", "#FF9A72E0"),

            new ThemePaint.Entry("ChartBackgroundBrush", "#660D0820"),
            new ThemePaint.Entry("ChartBackgroundHotBrush", "#66150E2C"),
            new ThemePaint.Entry("ChartGridBrush", "#3326204A"),
            new ThemePaint.Entry("ChartCrosshairBrush", "#806F5AA8"),
            new ThemePaint.Entry("ChartAxisTextBrush", "#FF9A8BC0"),
            new ThemePaint.Entry("ChartHoverBadgeBgBrush", "#E6160D2B"),
            new ThemePaint.Entry("ChartHoverBadgeBorderBrush", "#4DB18CF5"),
            new ThemePaint.Entry("ChartHoverTextBrush", "#FFF2ECFF"),

            new ThemePaint.Entry("MetricUsageBrush", "#FF6FD3FF"),
            new ThemePaint.Entry("MetricTempBrush", "#FF8CE0A0"),
            new ThemePaint.Entry("MetricPowerBrush", "#FFB18CF5"),

            new ThemePaint.Entry("SensorGridLineBrush", "#3326204A"),
            new ThemePaint.Entry("SensorGroupFillBrush", "#A6160D2B"),
            new ThemePaint.Entry("SensorCategoryFillBrush", "#8C110926"),

            new ThemePaint.Entry("DisabledCardBackgroundBrush", "#A62B1C1C"),
            new ThemePaint.Entry("DisabledCardTextBrush", "#FFFFC1BC"),

            new ThemePaint.Entry("CardHoverBrush", "#26FFFFFF"),
            new ThemePaint.Entry("CardSelectedBrush", "#40B18CF5"),
            new ThemePaint.Entry("AccentTintBrush", "#33B18CF5"),
            new ThemePaint.Entry("MutedBrush", "#FF8E82B0"),

            new ThemePaint.Entry("OnboardingScrimBrush", "#55000000"),
            new ThemePaint.Entry("OnboardingSurfaceBrush", "#D9160D2B"),

            new ThemePaint.Entry("ChipBackgroundBrush", "#FF241748"),

            new ThemePaint.Entry("CoreCardBackgroundBrush", "#FF160D2B"),
            new ThemePaint.Entry("CoreTrackBackgroundBrush", "#8C0D081F"),

            new ThemePaint.Entry("WindowBackdropBrush", ThemePaint.Linear(0, 0, 0.5, 1,
                (0.0, "#FF0B0618"), (0.5, "#FF1A1038"), (1.0, "#FF0D0720"))),
            new ThemePaint.Entry("WindowGlowABrush", ThemePaint.Glow("#4D8B5CF6", "#1F5B21B6")),
            new ThemePaint.Entry("WindowGlowBBrush", ThemePaint.Glow("#4DB18CF5", "#1F7C3AED"))
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FFB18CF5"),
            ("SystemAccentColorForegroundBrush", "#FF1A1038"),
            ("AccentFillColorDefaultBrush", "#FFB18CF5"),
            ("AccentFillColorSecondaryBrush", "#E6B18CF5"),
            ("AccentFillColorTertiaryBrush", "#CCB18CF5"),
            ("NavigationViewDefaultPaneBackground", "#FF150C28"),
            ("NavigationViewContentBackground", "#00000000"),
            ("ComboBoxDropDownBackground", "#F2160D2B"),
            // Cuadros de texto: mismo tono que el desplegable (superficies del censo: ver
            // PanelAppearance.SurfaceKeys).
            ("TextControlBackground", "#FF160D2B"),
            ("TextControlBackgroundPointerOver", "#FF1C1234"),
            ("TextControlBackgroundFocused", "#FF231741"),
            ("TextControlBackgroundDisabled", "#FF160D2B"),
            ("ComboBoxDropDownBackgroundPointerOver", "#F21C1234"),
            ("ComboBoxDropDownBackgroundPointerPressed", "#F2231741"),
            ("ComboBoxDropDownBorderBrush", "#4DB79BF0"),
            ("MenuFlyoutPresenterBackground", "#F2160D2B"),
            ("MenuFlyoutPresenterBorderBrush", "#4DB79BF0"),
            ("ComboBoxItemBackgroundPointerOver", "#FF1C1234"),
            ("ComboBoxItemBackgroundSelected", "#FF2A1B52"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "#FF33215F"),
            ("ComboBoxItemBackgroundSelectedPressed", "#FF2A1B52")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FFB18CF5"),
            ("SystemAccentColorLight1", "#FFC7ABF8"),
            ("SystemAccentColorLight2", "#FFB18CF5"),
            ("SystemAccentColorLight3", "#FFA37BEF"),
            ("SystemAccentColorDark1", "#FF9A72E0"),
            ("SystemAccentColorDark2", "#FF855BC4"),
            ("SystemAccentColorDark3", "#FF6E48A6")
        ]
    };
}
