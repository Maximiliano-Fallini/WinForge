using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// MAREA — base oscura con foto de agua de fondo detrás de toda la ventana.
///
/// Mismo esquema de entorno (foto real + superficies translúcidas) con la paleta del
/// agua: turquesa de acento y resplandores turquesa/azul.
///
/// La foto de este tema es MUY luminosa (agua celeste a pleno sol), así que el velo es
/// fuerte: sin él, el texto claro y las cards no se leerían.
/// </summary>
internal static class MareaTheme
{
    public static ThemeDefinition Create() => new()
    {
        Theme = AppTheme.Tide,
        Base = AppTheme.Dark,
        NameKey = "Marea",
        Wallpaper = "marea.jpg",
        WallpaperCredit = "Imagen local del usuario",
        // Tema de ENTORNO (trae foto): arranca con los paneles casi invisibles (90 %) y un vidrio
        // suave (5 %), que es como la foto se ve de verdad. Los dos números son POR TEMA.
        PanelTransparency = 90,
        PanelBlur = 5,
        Brushes =
        [
            // Páginas y contenedor del contenido transparentes: se ve la foto del fondo.
            new ThemePaint.Entry("AppBackgroundBrush", "#00000000"),
            new ThemePaint.Entry("WindowWallpaperBrush", ThemePaint.Image("marea.jpg")),
            // Velo azul profundo: baja la luminosidad del agua lo suficiente para que el
            // texto claro quede legible sin apagar la textura del agua.
            new ThemePaint.Entry("WindowWallpaperScrimBrush", "#99102A33"),
            new ThemePaint.Entry("CardBackgroundBrush", "#FF062430"),
            new ThemePaint.Entry("ToolTipBackgroundBrush", "#FF062430"),
            new ThemePaint.Entry("CardBorderBrush", "#3396E6DD"),
            new ThemePaint.Entry("CardStrokeBrush", "#00000000"),   // sin contorno: ver BrasaTheme
            new ThemePaint.Entry("SecondaryTextBrush", "#FF9CC2C6"),

            new ThemePaint.Entry("AccentBrush", "#FF4FD8C4"),
            new ThemePaint.Entry("AccentForegroundBrush", "#FF03211F"),

            // Botón "Actualizar" de la barra de título (ver App.xaml): pastilla del turquesa
            // del agua (el acento que sale de la foto) con su rampa para hover/pressed.
            new ThemePaint.Entry("UpdateButtonBrush", "#FF4FD8C4"),
            new ThemePaint.Entry("UpdateButtonForegroundBrush", "#FF03211F"),
            new ThemePaint.Entry("UpdateButtonPointerOverBrush", "#FF6EE2D2"),
            new ThemePaint.Entry("UpdateButtonPressedBrush", "#FF35B7A6"),

            new ThemePaint.Entry("ChartBackgroundBrush", "#66051822"),
            new ThemePaint.Entry("ChartBackgroundHotBrush", "#66071C24"),
            new ThemePaint.Entry("ChartGridBrush", "#33254A52"),
            new ThemePaint.Entry("ChartCrosshairBrush", "#80607F88"),
            new ThemePaint.Entry("ChartAxisTextBrush", "#FF7FA3AB"),
            new ThemePaint.Entry("ChartHoverBadgeBgBrush", "#E6042630"),
            new ThemePaint.Entry("ChartHoverBadgeBorderBrush", "#4D5FD3C4"),
            new ThemePaint.Entry("ChartHoverTextBrush", "#FFEAFBF8"),

            new ThemePaint.Entry("MetricUsageBrush", "#FF5AD1E8"),
            new ThemePaint.Entry("MetricTempBrush", "#FF7FE08C"),
            new ThemePaint.Entry("MetricPowerBrush", "#FFF5CC66"),

            new ThemePaint.Entry("SensorGridLineBrush", "#33254A52"),
            new ThemePaint.Entry("SensorGroupFillBrush", "#A6062430"),
            new ThemePaint.Entry("SensorCategoryFillBrush", "#8C051B24"),

            new ThemePaint.Entry("DisabledCardBackgroundBrush", "#A62B1C1C"),
            new ThemePaint.Entry("DisabledCardTextBrush", "#FFFFC1BC"),

            new ThemePaint.Entry("CardHoverBrush", "#26FFFFFF"),
            new ThemePaint.Entry("CardSelectedBrush", "#404FD8C4"),
            new ThemePaint.Entry("AccentTintBrush", "#334FD8C4"),
            new ThemePaint.Entry("MutedBrush", "#FF84A6AC"),

            new ThemePaint.Entry("OnboardingScrimBrush", "#55000000"),
            new ThemePaint.Entry("OnboardingSurfaceBrush", "#D9062430"),

            new ThemePaint.Entry("ChipBackgroundBrush", "#FF102630"),

            new ThemePaint.Entry("CoreCardBackgroundBrush", "#FF062430"),
            new ThemePaint.Entry("CoreTrackBackgroundBrush", "#8C04161E"),

            new ThemePaint.Entry("WindowBackdropBrush", ThemePaint.Linear(0, 0, 0.35, 1,
                (0.0, "#FF03141C"), (0.45, "#FF04303A"), (1.0, "#FF06202C"))),
            new ThemePaint.Entry("WindowGlowABrush", ThemePaint.Glow("#4D2DD4BF", "#1F14B8A6")),
            new ThemePaint.Entry("WindowGlowBBrush", ThemePaint.Glow("#4D38BDF8", "#1F0EA5E9"))
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FF4FD8C4"),
            ("SystemAccentColorForegroundBrush", "#FF03211F"),
            ("AccentFillColorDefaultBrush", "#FF4FD8C4"),
            ("AccentFillColorSecondaryBrush", "#E64FD8C4"),
            ("AccentFillColorTertiaryBrush", "#CC4FD8C4"),
            ("NavigationViewDefaultPaneBackground", "#FF08202B"),
            ("NavigationViewContentBackground", "#00000000"),
            ("ComboBoxDropDownBackground", "#F2062430"),
            // Cuadros de texto: mismo tono que el desplegable (superficies del censo: ver
            // PanelAppearance.SurfaceKeys).
            ("TextControlBackground", "#FF062430"),
            ("TextControlBackgroundPointerOver", "#FF0A2C38"),
            ("TextControlBackgroundFocused", "#FF0E3442"),
            ("TextControlBackgroundDisabled", "#FF062430"),
            ("ComboBoxDropDownBackgroundPointerOver", "#F20A2C38"),
            ("ComboBoxDropDownBackgroundPointerPressed", "#F20E3442"),
            ("ComboBoxDropDownBorderBrush", "#4D5FD3C4"),
            ("MenuFlyoutPresenterBackground", "#F2062430"),
            ("MenuFlyoutPresenterBorderBrush", "#4D5FD3C4"),
            ("ComboBoxItemBackgroundPointerOver", "#FF0A2C38"),
            ("ComboBoxItemBackgroundSelected", "#FF0E3A46"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "#FF124650"),
            ("ComboBoxItemBackgroundSelectedPressed", "#FF0E3A46")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FF4FD8C4"),
            ("SystemAccentColorLight1", "#FF6EE2D2"),
            ("SystemAccentColorLight2", "#FF4FD8C4"),
            ("SystemAccentColorLight3", "#FF3FC9B5"),
            ("SystemAccentColorDark1", "#FF35B7A6"),
            ("SystemAccentColorDark2", "#FF269A8B"),
            ("SystemAccentColorDark3", "#FF1B7F73")
        ]
    };
}
