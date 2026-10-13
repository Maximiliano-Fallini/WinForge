using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// METAL — base oscura con foto de una superficie metálica (acero pulido) detrás de toda la ventana.
///
/// Tema de ENTORNO, con la paleta más neutra del set: acero y plata, sin tinte de color. El
/// acento es un azul acero claro, lo único "frío" de la interfaz, y los resplandores son
/// gris azulado, que mantienen la identidad también cuando se elige el fondo "Degradado".
///
/// La foto es clara (gris a pleno brillo), así que el velo es fuerte: sin él, las superficies
/// oscuras y el texto claro no se leerían sobre el reflejo del metal.
/// </summary>
internal static class MetalTheme
{
    public static ThemeDefinition Create() => new()
    {
        Theme = AppTheme.Metal,
        Base = AppTheme.Dark,
        NameKey = "Metal",
        Wallpaper = "metal.jpg",
        WallpaperCredit = "Imagen local del usuario",
        // Tema de ENTORNO (trae foto): arranca con los paneles al 50 % y un vidrio
        // suave (5 %), que es como la foto se ve de verdad. Los dos números son POR TEMA.
        PanelTransparency = 50,
        PanelBlur = 5,
        Brushes =
        [
            // Páginas y contenedor del contenido transparentes: se ve la foto del fondo.
            new ThemePaint.Entry("AppBackgroundBrush", "#00000000"),
            new ThemePaint.Entry("WindowWallpaperBrush", ThemePaint.Image("metal.jpg")),
            // Velo de acero oscuro: el metal es claro y sin velo el contraste del texto
            // claro sobre el reflejo se vuelve incómodo.
            new ThemePaint.Entry("WindowWallpaperScrimBrush", "#9913171B"),
            new ThemePaint.Entry("CardBackgroundBrush", "#FF181B1F"),
            new ThemePaint.Entry("ToolTipBackgroundBrush", "#FF181B1F"),
            new ThemePaint.Entry("CardBorderBrush", "#33A7C0D6"),
            new ThemePaint.Entry("CardStrokeBrush", "#00000000"),   // sin contorno: ver BrasaTheme
            new ThemePaint.Entry("SecondaryTextBrush", "#FFAEB6BF"),

            new ThemePaint.Entry("AccentBrush", "#FFA7C0D6"),
            new ThemePaint.Entry("AccentForegroundBrush", "#FF111820"),

            // Botón "Actualizar" de la barra de título (ver App.xaml): pastilla del azul
            // acero (el acento que sale del metal) con su rampa para hover/pressed.
            new ThemePaint.Entry("UpdateButtonBrush", "#FFA7C0D6"),
            new ThemePaint.Entry("UpdateButtonForegroundBrush", "#FF111820"),
            new ThemePaint.Entry("UpdateButtonPointerOverBrush", "#FFC2D6E6"),
            new ThemePaint.Entry("UpdateButtonPressedBrush", "#FF88A3BC"),

            new ThemePaint.Entry("ChartBackgroundBrush", "#660E1114"),
            new ThemePaint.Entry("ChartBackgroundHotBrush", "#66161A1E"),
            new ThemePaint.Entry("ChartGridBrush", "#332A3138"),
            new ThemePaint.Entry("ChartCrosshairBrush", "#80687A8A"),
            new ThemePaint.Entry("ChartAxisTextBrush", "#FF97A3AE"),
            new ThemePaint.Entry("ChartHoverBadgeBgBrush", "#E6181B1F"),
            new ThemePaint.Entry("ChartHoverBadgeBorderBrush", "#4DA7C0D6"),
            new ThemePaint.Entry("ChartHoverTextBrush", "#FFF1F5F8"),

            new ThemePaint.Entry("MetricUsageBrush", "#FF6FC9E8"),
            new ThemePaint.Entry("MetricTempBrush", "#FF86E29B"),
            new ThemePaint.Entry("MetricPowerBrush", "#FFE3C56B"),

            new ThemePaint.Entry("SensorGridLineBrush", "#332A3138"),
            new ThemePaint.Entry("SensorGroupFillBrush", "#A6181B1F"),
            new ThemePaint.Entry("SensorCategoryFillBrush", "#8C121518"),

            new ThemePaint.Entry("DisabledCardBackgroundBrush", "#A62B1C1C"),
            new ThemePaint.Entry("DisabledCardTextBrush", "#FFFFC1BC"),

            new ThemePaint.Entry("CardHoverBrush", "#26FFFFFF"),
            new ThemePaint.Entry("CardSelectedBrush", "#40A7C0D6"),
            new ThemePaint.Entry("AccentTintBrush", "#33A7C0D6"),
            new ThemePaint.Entry("MutedBrush", "#FF8C959E"),

            new ThemePaint.Entry("OnboardingScrimBrush", "#55000000"),
            new ThemePaint.Entry("OnboardingSurfaceBrush", "#D9181B1F"),

            new ThemePaint.Entry("ChipBackgroundBrush", "#FF23272C"),

            new ThemePaint.Entry("CoreCardBackgroundBrush", "#FF181B1F"),
            new ThemePaint.Entry("CoreTrackBackgroundBrush", "#8C101317"),

            new ThemePaint.Entry("WindowBackdropBrush", ThemePaint.Linear(0, 0, 0.5, 1,
                (0.0, "#FF0E1114"), (0.5, "#FF1D2227"), (1.0, "#FF0F1215"))),
            new ThemePaint.Entry("WindowGlowABrush", ThemePaint.Glow("#4D9FB6CC", "#1F5C6B7A")),
            new ThemePaint.Entry("WindowGlowBBrush", ThemePaint.Glow("#408A9BAB", "#1F4A5560"))
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FFA7C0D6"),
            ("SystemAccentColorForegroundBrush", "#FF111820"),
            ("AccentFillColorDefaultBrush", "#FFA7C0D6"),
            ("AccentFillColorSecondaryBrush", "#E6A7C0D6"),
            ("AccentFillColorTertiaryBrush", "#CCA7C0D6"),
            ("NavigationViewDefaultPaneBackground", "#FF16191D"),
            ("NavigationViewContentBackground", "#00000000"),
            ("ComboBoxDropDownBackground", "#F2181B1F"),
            // Cuadros de texto: mismo tono que el desplegable (superficies del censo: ver
            // PanelAppearance.SurfaceKeys).
            ("TextControlBackground", "#FF181B1F"),
            ("TextControlBackgroundPointerOver", "#FF1F2328"),
            ("TextControlBackgroundFocused", "#FF262B31"),
            ("TextControlBackgroundDisabled", "#FF181B1F"),
            ("ComboBoxDropDownBackgroundPointerOver", "#F21F2328"),
            ("ComboBoxDropDownBackgroundPointerPressed", "#F2262B31"),
            ("ComboBoxDropDownBorderBrush", "#4DA7C0D6"),
            ("MenuFlyoutPresenterBackground", "#F2181B1F"),
            ("MenuFlyoutPresenterBorderBrush", "#4DA7C0D6"),
            ("ComboBoxItemBackgroundPointerOver", "#FF1F2328"),
            ("ComboBoxItemBackgroundSelected", "#FF2B323A"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "#FF353D46"),
            ("ComboBoxItemBackgroundSelectedPressed", "#FF2B323A")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FFA7C0D6"),
            ("SystemAccentColorLight1", "#FFC2D6E6"),
            ("SystemAccentColorLight2", "#FFA7C0D6"),
            ("SystemAccentColorLight3", "#FF96AFC7"),
            ("SystemAccentColorDark1", "#FF88A3BC"),
            ("SystemAccentColorDark2", "#FF6F8AA3"),
            ("SystemAccentColorDark3", "#FF587086")
        ]
    };
}
