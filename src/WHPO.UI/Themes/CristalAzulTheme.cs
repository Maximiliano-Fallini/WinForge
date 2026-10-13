using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// CRISTAL AZUL — base oscura con foto de cristales azules (zafiro facetado) detrás de toda
/// la ventana.
///
/// Tema de ENTORNO, hermano de Marea/Aurora/Brasa/Amatista/Metal: foto real + superficies
/// translúcidas. La identidad sale de la foto —azules profundos con facetas y brillos
/// cian—, así que el acento es un azul eléctrico luminoso que se lee sobre las superficies
/// oscuras y los resplandores son azul y cian.
///
/// La foto es oscura pero muy saturada y con brillos intensos: el velo es de los fuertes
/// (60 %, como Marea), para que las facetas más claras no le coman el contraste al texto.
/// </summary>
internal static class CristalAzulTheme
{
    public static ThemeDefinition Create() => new()
    {
        Theme = AppTheme.CristalAzul,
        Base = AppTheme.Dark,
        NameKey = "Cristal Azul",
        Wallpaper = "cristalazul.jpg",
        WallpaperCredit = "Imagen local del usuario",
        // Tema de ENTORNO (trae foto): arranca con los paneles al 50 % y un vidrio
        // suave (5 %), que es como la foto se ve de verdad. Los dos números son POR TEMA.
        PanelTransparency = 50,
        PanelBlur = 5,
        Brushes =
        [
            // Páginas y contenedor del contenido transparentes: se ve la foto del fondo.
            new ThemePaint.Entry("AppBackgroundBrush", "#00000000"),
            new ThemePaint.Entry("WindowWallpaperBrush", ThemePaint.Image("cristalazul.jpg")),
            // Velo zafiro profundo: la foto ya es oscura, pero sus facetas y brillos son
            // intensos; con el velo el conjunto queda parejo y el texto claro se lee.
            new ThemePaint.Entry("WindowWallpaperScrimBrush", "#99101F4A"),
            new ThemePaint.Entry("CardBackgroundBrush", "#FF0B1A33"),
            new ThemePaint.Entry("ToolTipBackgroundBrush", "#FF0B1A33"),
            new ThemePaint.Entry("CardBorderBrush", "#335AB6FF"),
            new ThemePaint.Entry("CardStrokeBrush", "#00000000"),   // sin contorno: ver BrasaTheme
            new ThemePaint.Entry("SecondaryTextBrush", "#FF9FB6D8"),

            new ThemePaint.Entry("AccentBrush", "#FF5AB6FF"),
            new ThemePaint.Entry("AccentForegroundBrush", "#FF06162E"),

            // Botón "Actualizar" de la barra de título (ver App.xaml): pastilla del azul
            // eléctrico (el acento que sale de los brillos del cristal) con su rampa.
            new ThemePaint.Entry("UpdateButtonBrush", "#FF5AB6FF"),
            new ThemePaint.Entry("UpdateButtonForegroundBrush", "#FF06162E"),
            new ThemePaint.Entry("UpdateButtonPointerOverBrush", "#FF7CC9FF"),
            new ThemePaint.Entry("UpdateButtonPressedBrush", "#FF3B9CF0"),

            new ThemePaint.Entry("ChartBackgroundBrush", "#6606162E"),
            new ThemePaint.Entry("ChartBackgroundHotBrush", "#660A1E3A"),
            new ThemePaint.Entry("ChartGridBrush", "#331E3A66"),
            new ThemePaint.Entry("ChartCrosshairBrush", "#80708FB8"),
            new ThemePaint.Entry("ChartAxisTextBrush", "#FF8FA8C8"),
            new ThemePaint.Entry("ChartHoverBadgeBgBrush", "#E60B1A33"),
            new ThemePaint.Entry("ChartHoverBadgeBorderBrush", "#4D5AB6FF"),
            new ThemePaint.Entry("ChartHoverTextBrush", "#FFEFF6FF"),

            new ThemePaint.Entry("MetricUsageBrush", "#FF5AC8E8"),
            new ThemePaint.Entry("MetricTempBrush", "#FF7FE08C"),
            new ThemePaint.Entry("MetricPowerBrush", "#FF7FA9FF"),

            new ThemePaint.Entry("SensorGridLineBrush", "#331E3A66"),
            new ThemePaint.Entry("SensorGroupFillBrush", "#A60B1A33"),
            new ThemePaint.Entry("SensorCategoryFillBrush", "#8C081327"),

            new ThemePaint.Entry("DisabledCardBackgroundBrush", "#A62B1C1C"),
            new ThemePaint.Entry("DisabledCardTextBrush", "#FFFFC1BC"),

            new ThemePaint.Entry("CardHoverBrush", "#26FFFFFF"),
            new ThemePaint.Entry("CardSelectedBrush", "#405AB6FF"),
            new ThemePaint.Entry("AccentTintBrush", "#335AB6FF"),
            new ThemePaint.Entry("MutedBrush", "#FF89A0C4"),

            new ThemePaint.Entry("OnboardingScrimBrush", "#55000000"),
            new ThemePaint.Entry("OnboardingSurfaceBrush", "#D90B1A33"),

            new ThemePaint.Entry("ChipBackgroundBrush", "#FF13284A"),

            new ThemePaint.Entry("CoreCardBackgroundBrush", "#FF0B1A33"),
            new ThemePaint.Entry("CoreTrackBackgroundBrush", "#8C06122A"),

            new ThemePaint.Entry("WindowBackdropBrush", ThemePaint.Linear(0, 0, 0.5, 1,
                (0.0, "#FF040C1F"), (0.5, "#FF0E2050"), (1.0, "#FF061129"))),
            new ThemePaint.Entry("WindowGlowABrush", ThemePaint.Glow("#4D2563EB", "#1F1E3A8A")),
            new ThemePaint.Entry("WindowGlowBBrush", ThemePaint.Glow("#4D38BDF8", "#1F0284C7"))
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FF5AB6FF"),
            ("SystemAccentColorForegroundBrush", "#FF06162E"),
            ("AccentFillColorDefaultBrush", "#FF5AB6FF"),
            ("AccentFillColorSecondaryBrush", "#E65AB6FF"),
            ("AccentFillColorTertiaryBrush", "#CC5AB6FF"),
            ("NavigationViewDefaultPaneBackground", "#FF091422"),
            ("NavigationViewContentBackground", "#00000000"),
            ("ComboBoxDropDownBackground", "#F20B1A33"),
            // Cuadros de texto: mismo tono que el desplegable (superficies del censo: ver
            // PanelAppearance.SurfaceKeys).
            ("TextControlBackground", "#FF0B1A33"),
            ("TextControlBackgroundPointerOver", "#FF102445"),
            ("TextControlBackgroundFocused", "#FF162F58"),
            ("TextControlBackgroundDisabled", "#FF0B1A33"),
            ("ComboBoxDropDownBackgroundPointerOver", "#F2102445"),
            ("ComboBoxDropDownBackgroundPointerPressed", "#F2162F58"),
            ("ComboBoxDropDownBorderBrush", "#4D7CC9FF"),
            ("MenuFlyoutPresenterBackground", "#F20B1A33"),
            ("MenuFlyoutPresenterBorderBrush", "#4D7CC9FF"),
            ("ComboBoxItemBackgroundPointerOver", "#FF102445"),
            ("ComboBoxItemBackgroundSelected", "#FF162F58"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "#FF1C3A6A"),
            ("ComboBoxItemBackgroundSelectedPressed", "#FF162F58")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FF5AB6FF"),
            ("SystemAccentColorLight1", "#FF7CC9FF"),
            ("SystemAccentColorLight2", "#FF5AB6FF"),
            ("SystemAccentColorLight3", "#FF4AA8F5"),
            ("SystemAccentColorDark1", "#FF3B9CF0"),
            ("SystemAccentColorDark2", "#FF2A7FCC"),
            ("SystemAccentColorDark3", "#FF1E63A6")
        ]
    };
}
