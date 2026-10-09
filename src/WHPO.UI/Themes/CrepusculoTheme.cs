using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// CREPÚSCULO — base oscura con un gradiente de atardecer propio (violeta de noche → magenta →
/// coral) y SIN foto de fondo: es el primer tema del grupo "Degradado" del selector.
///
/// Es el hermano sin imagen de los temas de entorno: donde esos pintan una foto, acá el fondo de
/// la ventana es el gradiente de <c>WindowBackdropBrush</c> más dos resplandores. Como no hay foto,
/// el tema arranca OPACO y sin desenfoque (0 % / 0 %, ver ThemeCatalog.KindOf): el degradado se ve
/// en el aire entre los paneles y no detrás de ellos.
///
/// El acento es dorado, el único de la familia: sobre el violeta del fondo queda como la última luz
/// del atardecer.
/// </summary>
internal static class CrepusculoTheme
{
    public static ThemeDefinition Create() => new()
    {
        Theme = AppTheme.Twilight,
        Base = AppTheme.Dark,
        NameKey = "Crepúsculo",
        // Tema de DEGRADADO (sin foto): arranca opaco y sin desenfoque.
        PanelTransparency = 0,
        PanelBlur = 0,
        Brushes =
        [
            // Las páginas y el contenedor del contenido quedan TRANSPARENTES: lo que se ve detrás es
            // el gradiente del fondo de la ventana (WindowBackdropBrush), no un color de página.
            new ThemePaint.Entry("AppBackgroundBrush", "#00000000"),
            new ThemePaint.Entry("CardBackgroundBrush", "#FF1E1235"),
            new ThemePaint.Entry("ToolTipBackgroundBrush", "#FF1E1235"),
            new ThemePaint.Entry("CardBorderBrush", "#33FFC46B"),
            new ThemePaint.Entry("CardStrokeBrush", "#00000000"),   // sin contorno: ver BrasaTheme
            new ThemePaint.Entry("SecondaryTextBrush", "#FFB9A6D0"),

            new ThemePaint.Entry("AccentBrush", "#FFFFC46B"),
            new ThemePaint.Entry("AccentForegroundBrush", "#FF2A1A05"),

            // Botón "Actualizar" de la barra de título (ver App.xaml): pastilla del dorado del
            // sol bajo el horizonte, con su rampa para hover/pressed.
            new ThemePaint.Entry("UpdateButtonBrush", "#FFFFC46B"),
            new ThemePaint.Entry("UpdateButtonForegroundBrush", "#FF2A1A05"),
            new ThemePaint.Entry("UpdateButtonPointerOverBrush", "#FFFFD48C"),
            new ThemePaint.Entry("UpdateButtonPressedBrush", "#FFE0A94F"),

            new ThemePaint.Entry("ChartBackgroundBrush", "#660F0A22"),
            new ThemePaint.Entry("ChartBackgroundHotBrush", "#66180F2C"),
            new ThemePaint.Entry("ChartGridBrush", "#332A1F4A"),
            new ThemePaint.Entry("ChartCrosshairBrush", "#8069549C"),
            new ThemePaint.Entry("ChartAxisTextBrush", "#FF8D7FB5"),
            new ThemePaint.Entry("ChartHoverBadgeBgBrush", "#E6181033"),
            new ThemePaint.Entry("ChartHoverBadgeBorderBrush", "#4DFFC46B"),
            new ThemePaint.Entry("ChartHoverTextBrush", "#FFF6F1FF"),

            new ThemePaint.Entry("MetricUsageBrush", "#FF6FD3FF"),
            new ThemePaint.Entry("MetricTempBrush", "#FF8CE0A0"),
            new ThemePaint.Entry("MetricPowerBrush", "#FFFFC46B"),

            new ThemePaint.Entry("SensorGridLineBrush", "#332A1F4A"),
            new ThemePaint.Entry("SensorGroupFillBrush", "#A61E1235"),
            new ThemePaint.Entry("SensorCategoryFillBrush", "#8C180F2C"),

            new ThemePaint.Entry("DisabledCardBackgroundBrush", "#A63A1F22"),
            new ThemePaint.Entry("DisabledCardTextBrush", "#FFFFC1BC"),

            new ThemePaint.Entry("CardHoverBrush", "#26FFFFFF"),
            new ThemePaint.Entry("CardSelectedBrush", "#40FFC46B"),
            new ThemePaint.Entry("AccentTintBrush", "#33FFC46B"),
            new ThemePaint.Entry("MutedBrush", "#FF9C8FC0"),

            new ThemePaint.Entry("OnboardingScrimBrush", "#55000000"),
            new ThemePaint.Entry("OnboardingSurfaceBrush", "#D91E1235"),

            new ThemePaint.Entry("ChipBackgroundBrush", "#FF2A1A47"),

            new ThemePaint.Entry("CoreCardBackgroundBrush", "#FF1E1235"),
            new ThemePaint.Entry("CoreTrackBackgroundBrush", "#8C160E2C"),

            // EL tema: el gradiente del atardecer (de la noche violácea al coral del horizonte, en
            // diagonal) y los dos resplandores que le dan aire. Es lo que pinta el fondo de la
            // ventana, el borde (se promedian sus paradas, ver PanelAppearance.BackdropBaseColor) y
            // la miniatura de la card en el selector de temas.
            new ThemePaint.Entry("WindowBackdropBrush", ThemePaint.Linear(0, 0, 0.2, 1,
                (0.0, "#FF120B2A"), (0.45, "#FF3A1B4E"), (0.8, "#FF6B2A52"), (1.0, "#FF8E3B4A"))),
            new ThemePaint.Entry("WindowGlowABrush", ThemePaint.Glow("#59C084FC", "#2E7C3AED")),
            new ThemePaint.Entry("WindowGlowBBrush", ThemePaint.Glow("#4DFFA36B", "#1FF97316"))
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FFFFC46B"),
            ("SystemAccentColorForegroundBrush", "#FF2A1A05"),
            ("AccentFillColorDefaultBrush", "#FFFFC46B"),
            ("AccentFillColorSecondaryBrush", "#E6FFC46B"),
            ("AccentFillColorTertiaryBrush", "#CCFFC46B"),
            ("NavigationViewDefaultPaneBackground", "#FF1A1030"),
            ("NavigationViewContentBackground", "#00000000"),
            ("ComboBoxDropDownBackground", "#F21E1235"),
            // Cuadros de texto: mismo tono que el desplegable (superficies del censo: ver
            // PanelAppearance.SurfaceKeys).
            ("TextControlBackground", "#FF1E1235"),
            ("TextControlBackgroundPointerOver", "#FF241640"),
            ("TextControlBackgroundFocused", "#FF2B1C4D"),
            ("TextControlBackgroundDisabled", "#FF1E1235"),
            ("ComboBoxDropDownBackgroundPointerOver", "#F2241640"),
            ("ComboBoxDropDownBackgroundPointerPressed", "#F22B1C4D"),
            ("ComboBoxDropDownBorderBrush", "#4DFFC46B"),
            ("MenuFlyoutPresenterBackground", "#F21E1235"),
            ("MenuFlyoutPresenterBorderBrush", "#4DFFC46B"),
            ("ComboBoxItemBackgroundPointerOver", "#FF241640"),
            ("ComboBoxItemBackgroundSelected", "#FF32205A"),
            ("ComboBoxItemBackgroundSelectedPointerOver", "#FF3A2768"),
            ("ComboBoxItemBackgroundSelectedPressed", "#FF32205A")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FFFFC46B"),
            ("SystemAccentColorLight1", "#FFFFD48C"),
            ("SystemAccentColorLight2", "#FFFFC46B"),
            ("SystemAccentColorLight3", "#FFF5B554"),
            ("SystemAccentColorDark1", "#FFE0A94F"),
            ("SystemAccentColorDark2", "#FFC79240"),
            ("SystemAccentColorDark3", "#FFA97A33")
        ]
    };
}
