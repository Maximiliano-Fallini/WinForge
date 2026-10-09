using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Fondo de la ventana: qué se pinta detrás de TODO —menú, barra de título y contenido—.
///
/// Tres modos:
/// - <see cref="ModeTheme"/> (predeterminado): la imagen del tema, la de su carpeta
///   <c>Themes\Backgrounds\</c>. Los temas clásicos no traen imagen, así que ahí se ve el
///   color o el gradiente del tema.
/// - <see cref="ModeGradient"/>: sin imagen; queda el fondo propio del tema
///   (WindowBackdropBrush + sus resplandores).
/// - <see cref="ModeCustom"/>: una imagen propia que quedó guardada por una versión
///   anterior (la app ya no permite elegir una).
///
/// La imagen se escribe en la MISMA clave que usa la definición del tema
/// (<c>WindowWallpaperBrush</c>), así que el resto de la ventana no sabe de dónde salió: en
/// pantalla es siempre un ImageBrush detrás de la capa de fondo, y el velo del tema sigue
/// encima para que el texto se lea.
///
/// El modo y la ruta vienen del ajuste guardado que escribía la card "Fondo" de la
/// pestaña Apariencia (ya no existe): acá solo se lee, para que una elección vieja siga valiendo.
/// </summary>
public static class Wallpaper
{
    public const string ModeSettingKey = "appearance.wallpaperMode";
    public const string PathSettingKey = "appearance.wallpaperPath";

    /// <summary>La imagen del tema (o la del usuario si eligió una).</summary>
    public const string ModeTheme = "theme";

    /// <summary>Sin imagen: el gradiente/color del tema.</summary>
    public const string ModeGradient = "gradient";

    /// <summary>Imagen elegida por el usuario, copiada a la carpeta de datos de la app.</summary>
    public const string ModeCustom = "custom";

    private static bool _loaded;

    /// <summary>Modo vigente (<see cref="ModeTheme"/>, <see cref="ModeGradient"/> o <see cref="ModeCustom"/>).</summary>
    public static string Mode { get; private set; } = ModeTheme;

    /// <summary>Ruta de la imagen del usuario (solo tiene sentido en <see cref="ModeCustom"/>).</summary>
    public static string? CustomPath { get; private set; }

    /// <summary>
    /// Aplica el fondo vigente sobre el tema indicado. La llama PanelAppearance al terminar de
    /// aplicar un tema, que es cuando la paleta ya está puesta y el ajuste guardado ya se leyó.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                Mode = NormalizeMode(PanelAppearance.Get(ModeSettingKey, ModeTheme));
                CustomPath = PanelAppearance.Get<string?>(PathSettingKey, null);

                // Una imagen elegida a mano que ya no está en disco (se borró la carpeta de
                // datos, se restauró un respaldo) no puede dejar la ventana sin fondo: se
                // vuelve al fondo del tema.
                if (Mode == ModeCustom && !File.Exists(CustomPath))
                {
                    Mode = ModeTheme;
                    CustomPath = null;
                    PanelAppearance.Set(ModeSettingKey, Mode);
                    PanelAppearance.Set(PathSettingKey, "");
                }
            }

            var brush = Mode switch
            {
                ModeGradient => new ImageBrush(),
                ModeCustom => ThemePaint.ImageFromPath(CustomPath),
                _ => ThemePaint.Image(ThemeCatalog.WallpaperFile(theme))
            };

            PanelAppearance.WriteThemeValue("WindowWallpaperBrush", PanelAppearance.ThemeKeyFor(theme), brush);
            PanelAppearance.Diag($"fondo: modo={Mode} tema={theme} archivo={DescribeSource(theme)}");

            // Con la imagen ya en su clave, las superficies (cards, menú, chips) se
            // recalculan desde ESA imagen: es lo que hace que el fondo y los paneles
            // compartan paleta, venga la foto del tema o la haya elegido el usuario.
            RefreshDerivedPanels(theme);
        }
        catch (Exception ex)
        {
            PanelAppearance.Diag($"fondo: no se pudo aplicar ({ex.Message})");
        }
    }

    // =====================================================================
    // Superficies derivadas de la imagen (la regla del diseño: si hay foto de
    // fondo, las cards y los paneles NO llevan colores escritos a mano)
    // =====================================================================

    /// <summary>Descarta resultados de una imagen que ya no está en uso (cambio de tema/fondo mientras se decodificaba).</summary>
    private static int _deriveGeneration;

    /// <summary>
    /// La foto que corresponde al fondo GUARDADO, resuelta SIN aplicar nada: <see cref="Mode"/> y
    /// <see cref="CustomPath"/> recién se llenan en <see cref="Apply"/>, que corre cuando la ventana
    /// principal ya existe. La necesita el splash, que se pinta antes que todo eso: sin esto mostraría
    /// siempre la foto del tema —nunca la imagen propia del usuario— y con el fondo en degradado
    /// pintaría una foto que la app no va a mostrar. Devuelve null si el fondo guardado no es imagen.
    /// </summary>
    internal static string? SavedImagePath(AppTheme theme)
    {
        try
        {
            var mode = NormalizeMode(PanelAppearance.Get(ModeSettingKey, ModeTheme));
            if (mode == ModeGradient) return null;

            var custom = PanelAppearance.Get<string?>(PathSettingKey, null);
            if (mode == ModeCustom)
            {
                // Igual que Apply: una imagen elegida que ya no está en disco no deja la ventana
                // ni el splash sin fondo, se usa el del tema.
                if (custom != null && File.Exists(custom)) return custom;
                mode = ModeTheme;
            }

            return mode != ModeGradient && ThemeCatalog.WallpaperFile(theme) is { } name
                ? Path.Combine(ThemePaint.BackgroundsDirectory, name)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ruta de la imagen que está en uso, o null si el fondo es el gradiente o el tema no
    /// trae foto. Es la MISMA resolución que hace la miniatura (ver PreviewImage).
    /// </summary>
    internal static string? ActiveImagePath(AppTheme theme)
    {
        if (Mode == ModeGradient) return null;
        return Mode == ModeCustom
            ? CustomPath
            : ThemeCatalog.WallpaperFile(theme) is { } name
                ? Path.Combine(ThemePaint.BackgroundsDirectory, name)
                : null;
    }

    /// <summary>
    /// Recalcula el color de las superficies (cards, menú, chips) a partir de la imagen que
    /// hay detrás de la ventana: promedio de la foto compuesto con el velo del tema, del que
    /// <see cref="ThemePaint.SurfaceFromImage"/> saca el color de cada escalón. Así una
    /// imagen elegida por el usuario queda armónica sin que nadie escriba un color, y si el
    /// fondo pasa a ser el degradado del tema (que SÍ es identidad escrita a mano), vuelven
    /// los colores de la definición.
    ///
    /// Pisa los colores DE FÁBRICA (ThemePalettes.SetFactoryColor) y no los del diccionario
    /// vivo: la transparencia de paneles calcula siempre desde el color de fábrica, así el
    /// deslizador sigue funcionando exactamente igual sobre el color derivado.
    ///
    /// Es fire-and-forget con contador de generación: si mientras se decodifica la foto el
    /// usuario cambió de tema o de fondo, el resultado viejo se descarta. Solo corre en
    /// temas con paleta propia: los clásicos pintan páginas opacas encima, la imagen nunca
    /// se ve y no hay nada que armonizar.
    /// </summary>
    internal static void RefreshDerivedPanels(AppTheme theme)
    {
        try
        {
            if (!ThemePalettes.HasOwnPalette(theme))
            {
                // Los temas clásicos no tienen foto detrás: no hay nada que derivar ni que
                // desenfocar (si veníamos de un tema con imagen, se descarta la copia).
                PanelBlur.Clear();
                return;
            }

            int generation = Interlocked.Increment(ref _deriveGeneration);
            var image = ActiveImagePath(theme);
            if (image == null)
            {
                ApplyDerivedPanels(theme, null);
                return;
            }

            // El velo es parte del fondo que se ve: el color del que derivan las superficies
            // es la foto YA velada, no la foto cruda (en Marea, sin el velo, el agua celeste
            // daría superficies mucho más claras que las que realmente hay detrás).
            var scrim = ThemePalettes.TryGetFactoryColor(theme, "WindowWallpaperScrimBrush", out var veil)
                ? veil
                : Windows.UI.Color.FromArgb(0, 0, 0, 0);

            _ = Task.Run(async () =>
            {
                try
                {
                    var average = await TryGetAverageColorAsync(image);
                    if (average == null) return;
                    var backdrop = ThemePaint.ComposeOver(scrim, average.Value);

                    // Las escrituras tocan diccionarios de recursos y controles: van en el
                    // hilo de UI. Sin ventana todavía no hay nada que repintar: la próxima
                    // aplicación de tema (o Attach, cuando la ventana exista) lo corre de nuevo.
                    var window = App.MainWindowInstance;
                    if (window == null) return;
                    window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (generation == _deriveGeneration) ApplyDerivedPanels(theme, backdrop);
                    });
                }
                catch (Exception ex)
                {
                    PanelAppearance.Diag($"superficies derivadas: no se pudo leer la imagen ({ex.Message})");
                }
            });
        }
        catch (Exception ex)
        {
            PanelAppearance.Diag($"superficies derivadas: {ex.Message}");
        }
    }

    /// <summary>
    /// Escribe los colores derivados (o restaura los de la definición, con backdrop=null) y
    /// re-aplica la transparencia vigente, que es quien pinta las superficies en los
    /// diccionarios y repinta a mano el menú, la barra de título y el borde de la ventana.
    /// </summary>
    private static void ApplyDerivedPanels(AppTheme theme, Windows.UI.Color? backdrop)
    {
        if (backdrop == null)
        {
            ThemePalettes.ResetFactoryColors(theme);
            PanelAppearance.Diag($"superficies derivadas: tema={theme} sin imagen — colores de la definición");
        }
        else
        {
            // Escalones de luminosidad: cards un pelo más claras que el menú y chips más
            // aún — la misma jerarquía sutil que tenían los valores escritos a mano.
            var card = ThemePaint.SurfaceFromImage(backdrop.Value, 0.10);
            ThemePalettes.SetFactoryColor(theme, "CardBackgroundBrush", card);
            ThemePalettes.SetFactoryColor(theme, "CoreCardBackgroundBrush", card);
            ThemePalettes.SetFactoryColor(theme, "NavigationViewDefaultPaneBackground", ThemePaint.SurfaceFromImage(backdrop.Value, 0.09));
            ThemePalettes.SetFactoryColor(theme, "ChipBackgroundBrush", ThemePaint.SurfaceFromImage(backdrop.Value, 0.125));
            PanelAppearance.Diag($"superficies derivadas: tema={theme} fondo {ThemePaint.Hex(backdrop.Value)} -> card {ThemePaint.Hex(card)}");
        }

        // Con los colores de fábrica ya definitivos (los derivados de la foto, si la hay) se
        // rearma la copia desenfocada: su tinte tiene que ser el color de las cards que acaban
        // de quedar escritas, no el que tenía el tema antes de derivar. Es trabajo de fondo (ver
        // PanelBlur) y, cuando termine, vuelve por acá vía SetTransparencyPercent.
        PanelBlur.Refresh(theme);

        PanelAppearance.SetTransparencyPercent(PanelAppearance.TransparencyPercent, persist: false);
    }

    /// <summary>
    /// Color promedio de una imagen, decodificada A 48x48 (el promedio no necesita más y
    /// evita reservar memoria por un archivo de megas). Null si no se pudo leer.
    /// </summary>
    private static async Task<Windows.UI.Color?> TryGetAverageColorAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = 48,
            ScaledHeight = 48,
            InterpolationMode = BitmapInterpolationMode.Linear
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var data = pixels.DetachPixelData();

        long r = 0, g = 0, b = 0;
        int counted = 0;
        for (int i = 0; i + 3 < data.Length; i += 4)
        {
            if (data[i + 3] < 16) continue;   // pixel transparente: no vota
            b += data[i];
            g += data[i + 1];
            r += data[i + 2];
            counted++;
        }
        if (counted == 0) return null;
        return Windows.UI.Color.FromArgb(255,
            (byte)(r / counted), (byte)(g / counted), (byte)(b / counted));
    }

    /// <summary>Nombre del archivo en uso, para la bitácora de diagnóstico.</summary>
    private static string DescribeSource(AppTheme theme)
        => Mode == ModeGradient
            ? "(sin imagen: el fondo propio del tema)"
            : Mode == ModeCustom
                ? Path.GetFileName(CustomPath ?? "?")
                : ThemeCatalog.WallpaperFile(theme) ?? "(el tema no trae imagen)";

    private static string NormalizeMode(string? mode)
        => mode switch
        {
            ModeGradient => ModeGradient,
            ModeCustom => ModeCustom,
            _ => ModeTheme
        };
}
