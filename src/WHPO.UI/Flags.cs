using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUIImage = Microsoft.UI.Xaml.Controls.Image;

namespace WHPO_UI;

/// <summary>
/// Banderas de los idiomas dibujadas en tiempo de ejecución (System.Drawing) y
/// exportadas a PNG en una carpeta temporal. Así se ven como banderas reales en
/// cualquier versión de Windows (los emoji de banderas solo se renderizan en
/// Windows 11 y en Windows 10 aparecen como letras "AR"/"US").
///
/// Cada bandera se dibuja la PRIMERA vez que se pide (<see cref="EnsureFlag"/>), no solo
/// al arrancar: los idiomas descargables (pt-BR, zh-CN, ru-RU…) no entran a
/// <see cref="I18n.Languages"/> hasta que se instala su pack, así que generar únicamente
/// los idiomas ya disponibles dejaba sin PNG justo a las filas que ofrecen descargarse
/// —y el BitmapImage apuntaba a un archivo inexistente: la imagen salía rota—. Un código
/// sin dibujo devuelve null y la fila queda solo con el nombre, nunca con una imagen rota.
/// </summary>
public static class Flags
{
    private const int W = 30;
    private const int H = 20;

    /// <summary>Códigos ya intentados en este proceso (con éxito o sin él): no se redibuja en cada fila.</summary>
    private static readonly HashSet<string> Attempted = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Sync = new();

    private static readonly string FlagDir =
        Path.Combine(Path.GetTempPath(), "WinForgeFlags");

    /// <summary>PNG de un código. El nombre va en minúsculas: los códigos de idioma no distinguen mayúsculas.</summary>
    private static string FlagFile(string code) => Path.Combine(FlagDir, code.ToLowerInvariant() + ".png");

    /// <summary>Devuelve la bandera como elemento para botones/menús.</summary>
    public static WinUIImage? GetImage(string code)
    {
        EnsureFlag(code);
        try
        {
            var file = FlagFile(code);
            // Sin PNG no hay bandera: devolver null deja al llamador sin imagen (y sin el
            // cuadro roto del BitmapImage apuntando a un archivo que no existe).
            if (!File.Exists(file)) return null;
            return new WinUIImage
            {
                Source = new BitmapImage(new Uri(file)),
                Width = 24,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch { return null; }
    }

    /// <summary>Devuelve la bandera como IconElement (para MenuFlyoutItem.Icon).</summary>
    public static IconElement? GetIcon(string code)
    {
        EnsureFlag(code);
        try
        {
            var file = FlagFile(code);
            if (!File.Exists(file)) return null;
            return new ImageIcon
            {
                Source = new BitmapImage(new Uri(file)),
                Width = 24,
                Height = 16
            };
        }
        catch { return null; }
    }

    /// <summary>Garantiza los PNG de los idiomas disponibles en esta sesión (embebidos + packs instalados).</summary>
    public static void EnsureGenerated()
    {
        foreach (var code in I18n.Languages)
            EnsureFlag(code);
    }

    /// <summary>
    /// Garantiza el PNG de UNA bandera: la dibuja si todavía no existe. Se llama al arrancar
    /// (para los idiomas ya disponibles) y por demanda desde GetImage/GetIcon, que es el
    /// camino de los idiomas todavía descargables.
    /// </summary>
    public static void EnsureFlag(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        lock (Sync)
        {
            if (!Attempted.Add(code)) return;
            try
            {
                Directory.CreateDirectory(FlagDir);
                var file = FlagFile(code);
                if (File.Exists(file)) return;
                using var bmp = DrawFlag(code);
                if (bmp == null) return;
                using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
                bmp.Save(fs, ImageFormat.Png);
            }
            catch { /* Si falla el dibujado, el botón muestra solo el nombre. */ }
        }
    }

    private static Bitmap? DrawFlag(string code)
    {
        try
        {
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            switch (code.ToLowerInvariant())
            {
                case "es-ar": // celeste / blanco / celeste + sol dorado
                {
                    g.Clear(Color.FromArgb(255, 0x74, 0xAC, 0xDF));
                    g.FillRectangle(Brushes.White, 0, H / 3, W, H / 3);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(255, 0xF6, 0xB4, 0x0E)), W / 2f - 3, H / 2f - 3, 6, 6);
                    break;
                }
                case "en-us": // 13 franjas + cantón azul con estrellas
                {
                    float stripeH = H / 13f;
                    var red = new SolidBrush(Color.FromArgb(255, 0xB2, 0x22, 0x34));
                    for (int i = 0; i < 13; i++)
                    {
                        if (i % 2 == 0)
                            g.FillRectangle(red, 0, i * stripeH, W, stripeH + 0.6f);
                    }
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0x3C, 0x3B, 0x6E)), 0, 0, W * 2 / 5f, H * 7 / 13f);
                    for (int r = 0; r < 5; r++)
                        for (int c = 0; c < 6; c++)
                            g.FillRectangle(Brushes.White, 1.6f + c * 2.1f, 1.2f + r * 2.3f, 1.1f, 1.1f);
                    break;
                }
                case "pt-br": // verde + rombo amarillo + círculo azul
                {
                    g.Clear(Color.FromArgb(255, 0x00, 0x97, 0x39));
                    var rombo = new SolidBrush(Color.FromArgb(255, 0xFE, 0xDD, 0x00));
                    PointF[] pts =
                    {
                        new(W / 2f, 0.5f), new(W - 0.5f, H / 2f),
                        new(W / 2f, H - 0.5f), new(0.5f, H / 2f)
                    };
                    g.FillPolygon(rombo, pts);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(255, 0x01, 0x21, 0x69)), W / 2f - 3.5f, H / 2f - 3.5f, 7, 7);
                    break;
                }
                case "de-de": // negro / rojo / dorado
                {
                    g.Clear(Color.FromArgb(255, 0x00, 0x00, 0x00));
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xDD, 0x00, 0x00)), 0, H / 3, W, H / 3);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xFF, 0xCE, 0x00)), 0, 2 * H / 3, W, H / 3);
                    break;
                }
                case "fr-fr": // azul / blanco / rojo verticales
                {
                    g.Clear(Color.FromArgb(255, 0x00, 0x55, 0xA4));
                    g.FillRectangle(Brushes.White, W / 3, 0, W / 3, H);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xEF, 0x41, 0x35)), 2 * W / 3, 0, W / 3 + 1, H);
                    break;
                }
                case "ru-ru": // blanco / azul / rojo
                {
                    g.Clear(Color.White);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0x00, 0x39, 0xA6)), 0, H / 3, W, H / 3);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xD5, 0x2B, 0x1E)), 0, 2 * H / 3, W, H / 3 + 1);
                    break;
                }
                case "zh-cn": // rojo con la estrella grande y las cuatro chicas
                {
                    g.Clear(Color.FromArgb(255, 0xDE, 0x29, 0x10));
                    var yellow = new SolidBrush(Color.FromArgb(255, 0xFF, 0xDE, 0x00));
                    FillStar(g, yellow, 6.5f, 6.5f, 4.6f);
                    FillStar(g, yellow, 12.6f, 2.9f, 1.7f);
                    FillStar(g, yellow, 14.6f, 6.0f, 1.7f);
                    FillStar(g, yellow, 13.9f, 9.7f, 1.7f);
                    FillStar(g, yellow, 11.0f, 12.4f, 1.7f);
                    break;
                }
                case "zh-tw": // rojo con el cantón azul y el sol blanco
                {
                    g.Clear(Color.FromArgb(255, 0xFE, 0x00, 0x00));
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0x00, 0x00, 0x95)), 0, 0, W / 2f, H / 2f);
                    float sx = W / 4f, sy = H / 4f;
                    var sun = new SolidBrush(Color.White);
                    // Doce rayos rectos alrededor del disco central.
                    for (int r = 0; r < 12; r++)
                    {
                        double angle = r * Math.PI / 6;
                        var p1 = new PointF(sx + (float)(Math.Cos(angle) * 2.2f), sy + (float)(Math.Sin(angle) * 2.2f));
                        var p2 = new PointF(sx + (float)(Math.Cos(angle) * 5.2f), sy + (float)(Math.Sin(angle) * 5.2f));
                        g.DrawLine(new Pen(sun, 1.4f), p1, p2);
                    }
                    g.FillEllipse(sun, sx - 2.4f, sy - 2.4f, 4.8f, 4.8f);
                    break;
                }
                case "uk-ua": // azul arriba / amarillo abajo
                {
                    g.Clear(Color.FromArgb(255, 0x00, 0x57, 0xD7));
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xFF, 0xD7, 0x00)), 0, H / 2f, W, H / 2f);
                    break;
                }
                case "th-th": // rojo / blanco / azul (doble) / blanco / rojo
                {
                    g.Clear(Color.FromArgb(255, 0xA5, 0x19, 0x31));
                    g.FillRectangle(Brushes.White, 0, H / 6f, W, H / 6f);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0x2D, 0x2A, 0x4A)), 0, H / 3f, W, H / 3f);
                    g.FillRectangle(Brushes.White, 0, 2 * H / 3f, W, H / 6f);
                    break;
                }
                case "cs-cz": // blanco / rojo con el triángulo azul a la izquierda
                {
                    g.Clear(Color.White);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xD7, 0x14, 0x1A)), 0, H / 2f, W, H / 2f);
                    PointF[] tri =
                    {
                        new(0, 0.5f), new(W * 0.55f, H / 2f), new(0, H - 0.5f)
                    };
                    g.FillPolygon(new SolidBrush(Color.FromArgb(255, 0x11, 0x47, 0x6C)), tri);
                    break;
                }
                case "nl-nl": // rojo / blanco / azul
                {
                    g.Clear(Color.FromArgb(255, 0xAE, 0x1C, 0x28));
                    g.FillRectangle(Brushes.White, 0, H / 3f, W, H / 3f);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0x21, 0x46, 0x8B)), 0, 2 * H / 3f, W, H / 3f + 1);
                    break;
                }
                case "pt-pt": // verde a la izquierda, rojo a la derecha y la esfera
                {
                    g.Clear(Color.FromArgb(255, 0x04, 0x63, 0x39));
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xDA, 0x29, 0x17)), W * 2 / 5f, 0, W * 3 / 5f, H);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(255, 0xFF, 0xE9, 0x00)), W * 2 / 5f - 3.2f, H / 2f - 3.2f, 6.4f, 6.4f);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(255, 0xDA, 0x29, 0x17)), W * 2 / 5f - 1.8f, H / 2f - 1.8f, 3.6f, 3.6f);
                    break;
                }
                case "pl-pl": // blanco arriba / rojo abajo
                {
                    g.Clear(Color.White);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xDC, 0x14, 0x3C)), 0, H / 2f, W, H / 2f);
                    break;
                }
                case "id-id": // rojo arriba / blanco abajo
                {
                    g.Clear(Color.FromArgb(255, 0xCE, 0x11, 0x26));
                    g.FillRectangle(Brushes.White, 0, H / 2f, W, H / 2f);
                    break;
                }
                case "tr-tr": // rojo con la media luna y la estrella blancas
                {
                    var red = new SolidBrush(Color.FromArgb(255, 0xE3, 0x0A, 0x17));
                    g.Clear(Color.FromArgb(255, 0xE3, 0x0A, 0x17));
                    float cx = 12.0f, cy = H / 2f;
                    g.FillEllipse(Brushes.White, cx - 5.6f, cy - 5.6f, 11.2f, 11.2f);
                    // El círculo rojo corrido a la derecha vacía la medialuna.
                    g.FillEllipse(red, cx - 3.6f, cy - 4.7f, 9.4f, 9.4f);
                    FillStar(g, Brushes.White, 22.0f, cy, 3.2f);
                    break;
                }
                case "vi-vn": // rojo con la estrella amarilla al centro
                {
                    g.Clear(Color.FromArgb(255, 0xDA, 0x25, 0x1E));
                    FillStar(g, new SolidBrush(Color.FromArgb(255, 0xFF, 0xFF, 0x00)), W / 2f, H / 2f, 5.6f);
                    break;
                }
                case "it-it": // verde / blanco / rojo verticales
                {
                    g.Clear(Color.FromArgb(255, 0x00, 0x92, 0x46));
                    g.FillRectangle(Brushes.White, W / 3, 0, W / 3, H);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xCD, 0x21, 0x2A)), 2 * W / 3, 0, W / 3 + 1, H);
                    break;
                }
                case "ja-jp": // blanco con el disco solar rojo
                {
                    g.Clear(Color.White);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(255, 0xBC, 0x00, 0x2D)), W / 2f - 5.2f, H / 2f - 5.2f, 10.4f, 10.4f);
                    break;
                }
                case "ko-kr": // blanco con el taegeuk y las barras negras de las esquinas
                {
                    g.Clear(Color.White);
                    float cx = W / 2f, cy = H / 2f, r = 4.9f;
                    var blue = new SolidBrush(Color.FromArgb(255, 0x00, 0x47, 0xA0));
                    var koRed = new SolidBrush(Color.FromArgb(255, 0xCD, 0x2E, 0x3A));
                    g.FillEllipse(blue, cx - r, cy - r, 2 * r, 2 * r);
                    // 180°→360° es la mitad de arriba: el rojo va arriba y el azul abajo,
                    // y los dos círculos chicos hacen el giro del taegeuk.
                    g.FillPie(koRed, cx - r, cy - r, 2 * r, 2 * r, 180, 180);
                    g.FillEllipse(koRed, cx - r, cy - r / 2f, r, r);
                    g.FillEllipse(blue, cx, cy - r / 2f, r, r);
                    // Cuatro barras cortas en las esquinas (los trigramas, sin detalle).
                    var black = new SolidBrush(Color.FromArgb(255, 0x00, 0x00, 0x00));
                    g.FillRectangle(black, 1f, 1f, 5f, 1.2f);
                    g.FillRectangle(black, W - 6f, 1f, 5f, 1.2f);
                    g.FillRectangle(black, 1f, H - 2.2f, 5f, 1.2f);
                    g.FillRectangle(black, W - 6f, H - 2.2f, 5f, 1.2f);
                    break;
                }
                default:
                    return null;
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Estrella de 5 puntas rellena, centrada en (cx, cy) con radio exterior r.</summary>
    private static void FillStar(Graphics g, System.Drawing.Brush brush, float cx, float cy, float r)
    {
        var pts = new PointF[10];
        float inner = r * 0.42f;
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            float radius = i % 2 == 0 ? r : inner;
            pts[i] = new PointF(cx + (float)(Math.Cos(angle) * radius), cy + (float)(Math.Sin(angle) * radius));
        }
        g.FillPolygon(brush, pts);
    }
}
