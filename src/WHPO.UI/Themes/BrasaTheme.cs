using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// BRASA — base oscura con foto de brasas encendidas detrás de toda la ventana.
///
/// Tema NUEVO (0.3.1). Es el único tema cálido: acento naranja de fuego sobre superficies
/// casi negras con un dejo rojizo, y resplandores naranja/rojo que mantienen la identidad
/// cuando se elige el fondo "Degradado" en vez de la foto.
/// </summary>
internal static class BrasaTheme
{
    public static ThemeDefinition Create() => new()
    {
        Theme = AppTheme.Brasa,
        Base = AppTheme.Dark,
        NameKey = "Brasa",
        Wallpaper = "brasa.jpg",
        WallpaperCredit = "Imagen local del usuario",
        // Tema de ENTORNO (trae foto): arranca con los paneles casi invisibles (90 %) y un vidrio
        // suave (5 %), que es como la foto se ve de verdad. Los dos números son POR TEMA.
        PanelTransparency = 90,
        PanelBlur = 5,
        Brushes =
        [
            new ThemePaint.Entry("AppBackgroundBrush", "#00000000"),
            new ThemePaint.Entry("WindowWallpaperBrush", ThemePaint.Image("brasa.jpg")),
            // Velo oscuro: las brasas son puntos muy brillantes sobre negro y sin velo el
            // contraste del texto claro sobre la foto se vuelve incómodo.
            new ThemePaint.Entry("WindowWallpaperScrimBrush", "#801A0A08"),
            new ThemePaint.Entry("CardBackgroundBrush", "#FF240F0C"),
            new ThemePaint.Entry("ToolTipBackgroundBrush", "#FF240F0C"),
            new ThemePaint.Entry("CardBorderBrush", "#33FFB59A"),
            // Borde de las CARDS: TRANSPARENTE — las cards no llevan contorno (CardStyle lo tiene en
            // 0, ver Controls/Cards.xaml). Antes era un filete al 10 % y, con el desenfoque encendido,
            // el ajuste de transparencia lo llevaba al tope del vidrio (0,62): #19000000 -> #9E000000
            // en appearance.log, un reborde marcado sobre la card. La clave queda declarada (la paleta
            // de fábrica se arma con estas definiciones, ver ThemePalettes.AllSemanticKeys) para que un
            // tema solo tenga que poner acá su color si quiere contorno. Ver App.xaml.
            new ThemePaint.Entry("CardStrokeBrush", "#00000000"),
            new ThemePaint.Entry("SecondaryTextBrush", "#FFD6A79C"),

            new ThemePaint.Entry("AccentBrush", "#FFFF8A4C"),
            new ThemePaint.Entry("AccentForegroundBrush", "#FF2A0F06"),

            // Botón "Actualizar" de la barra de título (ver App.xaml): pastilla del naranja
            // de las brasas (el acento que sale de la foto) con su rampa para hover/pressed.
            // Es el cambio que más se nota: el verde fijo de antes chocab de frente con el
            // único tema cálido de la app.
            new ThemePaint.Entry("UpdateButtonBrush", "#FFFF8A4C"),
            new ThemePaint.Entry("UpdateButtonForegroundBrush", "#FF2A0F06"),
            new ThemePaint.Entry("UpdateButtonPointerOverBrush", "#FFFFA470"),
            new ThemePaint.Entry("UpdateButtonPressedBrush", "#FFE0763C"),

            new ThemePaint.Entry("ChartBackgroundBrush", "#660F0504"),
            new ThemePaint.Entry("ChartBackgroundHotBrush", "#661B0A06"),
            new ThemePaint.Entry("ChartGridBrush", "#333A231E"),
            new ThemePaint.Entry("ChartCrosshairBrush", "#80734F45"),
            new ThemePaint.Entry("ChartAxisTextBrush", "#FFB08B82"),
            new ThemePaint.Entry("ChartHoverBadgeBgBrush", "#E6240F0C"),
            new ThemePaint.Entry("ChartHoverBadgeBorderBrush", "#4DFF8A4C"),
            new ThemePaint.Entry("ChartHoverTextBrush", "#FFFFF1EC"),

            new ThemePaint.Entry("MetricUsageBrush", "#FF6FC9E8"),
            new ThemePaint.Entry("MetricTempBrush", "#FFFF8A4C"),
            new ThemePaint.Entry("MetricPowerBrush", "#FFFFD34D"),

            new ThemePaint.Entry("SensorGridLineBrush", "#333A231E"),
            new ThemePaint.Entry("SensorGroupFillBrush", "#A6240F0C"),
            new ThemePaint.Entry("SensorCategoryFillBrush", "#8C1A0B09"),

            new ThemePaint.Entry("DisabledCardBackgroundBrush", "#A62B1C1C"),
            new ThemePaint.Entry("DisabledCardTextBrush", "#FFFFC1BC"),

            new ThemePaint.Entry("CardHoverBrush", "#26FFFFFF"),
            new ThemePaint.Entry("CardSelectedBrush", "#40FF8A4C"),
            new ThemePaint.Entry("AccentTintBrush", "#33FF8A4C"),
            new ThemePaint.Entry("MutedBrush", "#FFB08B82"),

            new ThemePaint.Entry("OnboardingScrimBrush", "#55000000"),
            new ThemePaint.Entry("OnboardingSurfaceBrush", "#D9240F0C"),

            new ThemePaint.Entry("ChipBackgroundBrush", "#FF3A1C12"),

            new ThemePaint.Entry("CoreCardBackgroundBrush", "#FF240F0C"),
            new ThemePaint.Entry("CoreTrackBackgroundBrush", "#8C180A08"),

            new ThemePaint.Entry("WindowBackdropBrush", ThemePaint.Linear(0, 0, 0.6, 1,
                (0.0, "#FF150705"), (0.5, "#FF250C08"), (1.0, "#FF12060A"))),
            new ThemePaint.Entry("WindowGlowABrush", ThemePaint.Glow("#4DF97316", "#1F9A3412")),
            new ThemePaint.Entry("WindowGlowBBrush", ThemePaint.Glow("#40DC2626", "#1F7F1D1D"))
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FFFF8A4C"),
            ("SystemAccentColorForegroundBrush", "#FF2A0F06"),
            ("AccentFillColorDefaultBrush", "#FFFF8A4C"),
            ("AccentFillColorSecondaryBrush", "#E6FF8A4C"),
            ("AccentFillColorTertiaryBrush", "#CCFF8A4C"),
            ("NavigationViewDefaultPaneBackground", "#FF2A1210"),
            ("NavigationViewContentBackground", "#00000000"),
            ("ComboBoxDropDownBackground", "#F2240F0C"),
            // Cuadros de texto: mismo tono que el desplegable (son superficies del censo: ver
            // PanelAppearance.SurfaceKeys). El alfa lo pone el deslizador de transparencia.
            ("TextControlBackground", "#FF240F0C"),
            ("TextControlBackgroundPointerOver", "#FF2E140F"),
            ("TextControlBackgroundFocused", "#FF381912"),
            ("TextControlBackgroundDisabled", "#FF240F0C"),
            ("ComboBoxDropDownBackgroundPointerOver", "#F22E140F"),
            ("ComboBoxDropDownBackgroundPointerPressed", "#F2381912"),
            ("ComboBoxDropDownBorderBrush", "#4DFFB59A"),
            ("MenuFlyoutPresenterBackground", "#F2240F0C"),
            ("MenuFlyoutPresenterBorderBrush", "#4DFFB59A"),
            ("ComboBoxItemBackgroundPointerOver", "#FF2E140F"),
            ("ComboBoxItemBackgroundSelected", "#FF3A1C12"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "#FF461F15"),
            ("ComboBoxItemBackgroundSelectedPressed", "#FF3A1C12")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FFFF8A4C"),
            ("SystemAccentColorLight1", "#FFFFA470"),
            ("SystemAccentColorLight2", "#FFFF8A4C"),
            ("SystemAccentColorLight3", "#FFEE7A3C"),
            ("SystemAccentColorDark1", "#FFE0763C"),
            ("SystemAccentColorDark2", "#FFC46430"),
            ("SystemAccentColorDark3", "#FFA55224")
        ]
    };
}
