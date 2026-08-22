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
/// </summary>
public static class Flags
{
    private const int W = 30;
    private const int H = 20;

    private static readonly Dictionary<string, string> Cache = new();

    private static readonly string FlagDir =
        Path.Combine(Path.GetTempPath(), "WinForgeFlags");

    private static string FlagFile(string code) => Path.Combine(FlagDir, code + ".png");

    /// <summary>Devuelve la bandera como elemento para botones/menús.</summary>
    public static WinUIImage? GetImage(string code)
    {
        try
        {
            return new WinUIImage
            {
                Source = new BitmapImage(new Uri(FlagFile(code))),
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
        try
        {
            return new ImageIcon
            {
                Source = new BitmapImage(new Uri(FlagFile(code))),
                Width = 24,
                Height = 16
            };
        }
        catch { return null; }
    }

    /// <summary>Garantiza que los PNG de todas las banderas existen en el cache temporal.</summary>
    public static void EnsureGenerated()
    {
        try
        {
            Directory.CreateDirectory(FlagDir);
            foreach (var code in I18n.Languages)
            {
                var file = FlagFile(code);
                if (File.Exists(file)) continue;
                using var bmp = DrawFlag(code);
                if (bmp != null)
                {
                    using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
                    bmp.Save(fs, ImageFormat.Png);
                }
            }
        }
        catch { /* Si falla el dibujado, el botón muestra solo el nombre. */ }
    }

    private static Bitmap? DrawFlag(string code)
    {
        try
        {
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            switch (code)
            {
                case "es-AR": // celeste / blanco / celeste + sol dorado
                {
                    var celeste = new SolidBrush(Color.FromArgb(255, 0x74, 0xAC, 0xDF));
                    g.Clear(celeste.Color);
                    g.FillRectangle(Brushes.White, 0, H / 3, W, H / 3);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(255, 0xF6, 0xB4, 0x0E)), W / 2f - 3, H / 2f - 3, 6, 6);
                    break;
                }
                case "en-US": // 13 franjas + cantón azul con estrellas
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
                case "pt-BR": // verde + rombo amarillo + círculo azul
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
                case "de-DE": // negro / rojo / dorado
                {
                    g.Clear(Color.FromArgb(255, 0x00, 0x00, 0x00));
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xDD, 0x00, 0x00)), 0, H / 3, W, H / 3);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xFF, 0xCE, 0x00)), 0, 2 * H / 3, W, H / 3);
                    break;
                }
                case "fr-FR": // azul / blanco / rojo verticales
                {
                    g.Clear(Color.FromArgb(255, 0x00, 0x55, 0xA4));
                    g.FillRectangle(Brushes.White, W / 3, 0, W / 3, H);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0xEF, 0x41, 0x35)), 2 * W / 3, 0, W / 3 + 1, H);
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
}
