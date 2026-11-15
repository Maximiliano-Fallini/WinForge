using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace WHPO_UI.Controls;

/// <summary>
/// Consola retro: un gabinete arcade de bar. El cuerpo es UN perfil lateral (el
/// contorno del mueble) extruido a lo ancho, igual que la consola es una losa: así la
/// silueta —marquesina que sobresale, bisel de pantalla inclinado, panel de mandos
/// salido y puerta de monedas— sale sola de la geometría y el SOMBREADO del canto
/// cambia al girar el modelo.
///
/// Las caras del perfil (marquesina, pantalla, panel, frente) toman cada una su
/// rectángulo de la textura frontal; los laterales usan el rectángulo de arte lateral.
/// </summary>
public static partial class CartridgeModel
{
    // ===== Perfil del gabinete (plano Y-Z, en px del canvas 600x878) =====
    // Y = fila del canvas (0 arriba), Z = profundidad (0 = centro del mueble).
    // El recorrido va del frente-abajo hacia atrás y sube por el frente.
    private static readonly (float Y, float Z)[] ArcadeProfile =
    {
        (850f, 100f),   // 0 filo inferior del frente
        (850f, -200f),  // 1 fondo
        (40f, -200f),   // 2 tope trasero
        (40f, 140f),    // 3 tope delantero: la marquesina SOBRESALE hacia adelante
        (150f, 80f),    // 4 filo inferior de la marquesina (entra hacia la pantalla)
        (172f, 60f),    // 5 escalón bajo la marquesina
        (185f, 5f),     // 6 tope del bisel de pantalla
        (370f, 50f),    // 7 pie del bisel = fondo del panel de mandos
        (500f, 200f),   // 8 borde delantero del panel: inclinado hacia el jugador
        (545f, 200f),   // 9 gota del panel (cae por delante del frente, lo tapa)
        (545f, 100f)    // 10 tope del frente (cierra contra el punto 0)
    };

    private const float ArcadeWidthPx = 380f;

    // ===== Rectángulos de la textura frontal =====
    private static readonly RectangleF ArcadeMarqueeRect = new(20, 18, 280, 132);
    private static readonly RectangleF ArcadeScreenRect = new(20, 170, 280, 178);
    private static readonly RectangleF ArcadeDeckRect = new(20, 366, 280, 180);
    private static readonly RectangleF ArcadeDeckLipRect = new(20, 552, 280, 34);
    private static readonly RectangleF ArcadeFrontRect = new(20, 600, 280, 258);
    private static readonly RectangleF ArcadeSideRect = new(330, 12, 200, 372);

    /// <summary>Rectángulo de textura de cada tramo del perfil (null = plástico del canto).</summary>
    private static RectangleF? ArcadeEdgeRect(int edge) => edge switch
    {
        3 => ArcadeMarqueeRect,   // 3→4 marquesina
        6 => ArcadeScreenRect,    // 6→7 bisel/pantalla
        7 => ArcadeDeckRect,      // 7→8 panel de mandos
        8 => ArcadeDeckLipRect,   // 8→9 gota del panel
        10 => ArcadeFrontRect,    // 10→0 frente (arte + monedas)
        _ => null
    };

    private static Vector2 ArcadePoint(int i)
    {
        var (y, z) = ArcadeProfile[i];
        return new Vector2((FaceHeight * 0.5f - y) / FaceWidth, z / FaceWidth);
    }

    /// <summary>Malla del gabinete: perfil extruido + dos tapas laterales.</summary>
    private static (Vertex[] Vertices, ushort[] Indices) BuildArcadeMesh()
    {
        var verts = new List<Vertex>();
        var idx = new List<ushort>();
        int n = ArcadeProfile.Length;
        float hx = ArcadeWidthPx * 0.5f / FaceWidth;

        // Centro del perfil para orientar las normales hacia afuera.
        var puntos = new Vector2[n];
        var centro = Vector2.Zero;
        for (int i = 0; i < n; i++) { puntos[i] = ArcadePoint(i); centro += puntos[i]; }
        centro /= n;

        ushort Push(Vector3 p, Vector3 nrm, float u, float v, float canal, float occ = 1f)
        {
            ushort id = (ushort)verts.Count;
            verts.Add(new Vertex(p, nrm, new Vector2(u, v), new Vector4(occ, 1f, 1f, canal)));
            return id;
        }

        for (int e = 0; e < n; e++)
        {
            var a = puntos[e];
            var b = puntos[(e + 1) % n];
            // Normal en el plano Y-Z, hacia afuera del perfil.
            var dir = b - a;
            var nrm2 = Vector2.Normalize(new Vector2(-dir.Y, dir.X));
            if (Vector2.Dot(nrm2, (a + b) * 0.5f - centro) < 0f) nrm2 = -nrm2;
            var nrm = new Vector3(nrm2.X, nrm2.Y, 0f);
            // El canto se ve de costado: se oscurece un poco el material.
            var rect = ArcadeEdgeRect(e);
            float canal = rect.HasValue ? 1f : 0f;
            float u0 = 0.002f, u1 = 0.998f, v0 = 0.002f, v1 = 0.998f;
            bool aEsArriba = a.X >= b.X; // X de malla = Y del canvas invertida
            if (rect.HasValue)
            {
                var r = rect.Value;
                u0 = r.Left / FaceWidth; u1 = r.Right / FaceWidth;
                v0 = r.Top / FaceHeight; v1 = r.Bottom / FaceHeight;
            }

            // 4 esquinas: el borde del perfil se extruye de -hx a +hx.
            Vector3 P(Vector2 q, float x) => new(x, q.X, q.Y);
            float vA = aEsArriba ? v0 : v1;
            float vB = aEsArriba ? v1 : v0;

            var i0 = Push(P(a, -hx), nrm, u0, vA, canal);
            var i1 = Push(P(a, hx), nrm, u1, vA, canal);
            var i2 = Push(P(b, hx), nrm, u1, vB, canal);
            var i3 = Push(P(b, -hx), nrm, u0, vB, canal);
            idx.AddRange(new[] { i0, i1, i2, i0, i2, i3 });
        }

        // Tapas: abanico desde el centro del perfil, con el arte lateral.
        var side = ArcadeSideRect;
        float zMin = 0f, zMax = 0f, yMin = 0f, yMax = 0f;
        foreach (var p in puntos)
        {
            zMin = MathF.Min(zMin, p.Y); zMax = MathF.Max(zMax, p.Y);
            yMin = MathF.Min(yMin, p.X); yMax = MathF.Max(yMax, p.X);
        }
        // Se mapea la CAJA del perfil al arte lateral: el recorte lo hace la silueta.
        (float U, float V) SideUv(Vector2 p)
            => (side.Left / FaceWidth + (p.Y - zMin) / MathF.Max(0.001f, zMax - zMin) * (side.Width / FaceWidth),
                side.Top / FaceHeight + (yMax - p.X) / MathF.Max(0.001f, yMax - yMin) * (side.Height / FaceHeight));

        foreach (float x in new[] { -hx, hx })
        {
            var nrm = new Vector3(x < 0 ? -1f : 1f, 0f, 0f);
            var c = centro;
            var cuv = SideUv(c);
            var cIdx = Push(new Vector3(x, c.X, c.Y), nrm, cuv.U, cuv.V, 1f);
            var ring = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                var uv = SideUv(puntos[i]);
                ring[i] = Push(new Vector3(x, puntos[i].X, puntos[i].Y), nrm, uv.U, uv.V, 1f);
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (x > 0) idx.AddRange(new[] { cIdx, ring[i], ring[j] });
                else idx.AddRange(new[] { cIdx, ring[j], ring[i] });
            }
        }

        return (verts.ToArray(), idx.ToArray());
    }

    // ===== Pintado =====

    /// <summary>Cara frontal del gabinete: las seis ventanas del perfil + el arte lateral.</summary>
    private static void PaintArcadeFace(Graphics g, string title, string launcher, string code,
        string? artPath, Color plastic, GameHeader? header)
    {
        // Chasis: negro de mueble, el mismo hilo que el bisel del cartucho.
        g.Clear(Color.FromArgb(18, 19, 22));

        PaintArcadeSide(g, title, launcher, artPath);
        PaintArcadeMarquee(g, title, launcher);
        PaintArcadeScreen(g, title, artPath);
        PaintArcadeDeck(g, artPath);
        PaintArcadeDeckLip(g);
        PaintArcadeFrontPanel(g, title, launcher, artPath, code);
    }

    /// <summary>Marquesina iluminada: la marca arriba y el emulador abajo.</summary>
    private static void PaintArcadeMarquee(Graphics g, string title, string launcher)
    {
        var r = ArcadeMarqueeRect;
        using (var back = new LinearGradientBrush(r,
            Color.FromArgb(26, 46, 92), Color.FromArgb(10, 16, 34), 90f))
            g.FillRectangle(back, r);
        // Rayos de "energía" detrás de la tipografía, como la referencia.
        var rnd = new Random(17);
        for (int i = 0; i < 22; i++)
        {
            float y0 = r.Top + (float)rnd.NextDouble() * r.Height;
            float x0 = r.Left + (float)rnd.NextDouble() * r.Width;
            using var pen = new Pen(Color.FromArgb(26 + rnd.Next(40), 90, 160, 255), 1f + (float)rnd.NextDouble() * 1.6f);
            g.DrawLine(pen, x0, y0, x0 + 14 + (float)rnd.NextDouble() * 40, y0 + (float)rnd.NextDouble() * 10 - 5);
        }
        // Marco negro y filo iluminado del gabinete.
        using (var frame = new Pen(Color.FromArgb(255, 12, 13, 16), 10f))
            g.DrawRectangle(frame, r.Left + 5, r.Top + 5, r.Width - 10, r.Height - 10);
        using (var glow = new Pen(Color.FromArgb(70, 160, 210, 255), 1.4f))
            g.DrawRectangle(glow, r.Left + 11, r.Top + 11, r.Width - 22, r.Height - 22);

        ArcadeTracked(g, "WINFORGE", r.Left + 16, r.Top + 26, r.Width - 32, 28f, 3f,
            Color.FromArgb(244, 247, 255), Color.FromArgb(30, 34, 48));
        string sub = (string.IsNullOrWhiteSpace(title) ? launcher : title).ToUpperInvariant();
        ArcadeTracked(g, sub, r.Left + 16, r.Top + 76, r.Width - 32, 17f, 2f,
            Color.FromArgb(120, 190, 255), Color.FromArgb(10, 14, 24));
    }

    /// <summary>Bisel con la pantalla apagada: vidrio oscuro y un reflejo en diagonal.</summary>
    private static void PaintArcadeScreen(Graphics g, string title, string? artPath)
    {
        var r = ArcadeScreenRect;
        using (var frame = new LinearGradientBrush(r,
            Color.FromArgb(26, 27, 31), Color.FromArgb(9, 10, 12), 90f))
            g.FillRectangle(frame, r);
        using (var bevel = new Pen(Color.FromArgb(58, 255, 255, 255), 1.4f))
            g.DrawRectangle(bevel, r.Left + 1, r.Top + 1, r.Width - 2, r.Height - 2);

        var glass = Rectangle.Round(new RectangleF(r.Left + 22, r.Top + 20, r.Width - 44, r.Height - 40));
        using (var path = RoundPath(glass, 6))
        {
            using (var screen = new LinearGradientBrush(glass,
                Color.FromArgb(12, 14, 20), Color.FromArgb(3, 4, 6), 60f))
                g.FillPath(screen, path);
            var clip = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            using (var glare = new LinearGradientBrush(glass,
                Color.FromArgb(30, 180, 205, 255), Color.FromArgb(0, 180, 205, 255), 35f))
                g.FillRectangle(glare, glass);
            // El logo del emulador, muy tenue: la pantalla está apagada.
            using var art = LoadArt(artPath);
            if (art != null)
            {
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(new ColorMatrix
                {
                    Matrix00 = 0.55f, Matrix11 = 0.55f, Matrix22 = 0.55f, Matrix33 = 0.16f, Matrix44 = 1f
                });
                int side = Math.Min(glass.Width, glass.Height) - 26;
                g.DrawImage(art, new Rectangle(glass.Left + (glass.Width - side) / 2,
                    glass.Top + (glass.Height - side) / 2, side, side),
                    0, 0, art.Width, art.Height, GraphicsUnit.Pixel, attrs);
            }
            g.Restore(clip);
        }
        using (var rimPath = RoundPath(glass, 6))
        using (var rim = new Pen(Color.FromArgb(90, 8, 9, 11), 2.2f))
            g.DrawPath(rim, rimPath);
    }

    /// <summary>Panel de mandos: joystick de bola roja a la izquierda y seis botones.</summary>
    private static void PaintArcadeDeck(Graphics g, string? artPath)
    {
        var r = ArcadeDeckRect;
        // Panel azul impreso: el tablero de mandos es parte del arte del mueble, no una
        // tapa negra. Los mandos van en la mitad de abajo (el borde de abajo del
        // rectángulo es el filo delantero, el que ve el jugador).
        using (var panel = new LinearGradientBrush(r,
            Color.FromArgb(48, 98, 182), Color.FromArgb(16, 32, 68), 90f))
            g.FillRectangle(panel, r);
        var rnd = new Random(11);
        for (int i = 0; i < 18; i++)
        {
            float y0 = r.Top + (float)rnd.NextDouble() * r.Height;
            float x0 = r.Left + (float)rnd.NextDouble() * r.Width;
            using var pen = new Pen(Color.FromArgb(18 + rnd.Next(30), 140, 190, 255), 1f);
            g.DrawLine(pen, x0, y0, x0 + 20 + (float)rnd.NextDouble() * 36, y0 - 4 + (float)rnd.NextDouble() * 8);
        }
        using (var rim = new Pen(Color.FromArgb(80, 255, 255, 255), 1.2f))
            g.DrawLine(rim, r.Left + 3, r.Top + 2, r.Right - 3, r.Top + 2);
        using (var frame = new Pen(Color.FromArgb(236, 10, 12, 16), 7f))
            g.DrawRectangle(frame, r.Left + 3, r.Top + 3, r.Width - 6, r.Height - 6);

        // Joystick: base hundida, fuelle, eje y bola.
        float jx = r.Left + r.Width * 0.24f, jy = r.Top + r.Height * 0.60f;
        using (var well = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            g.FillEllipse(well, jx - 46, jy - 16, 92, 40);
        using (var boot = new LinearGradientBrush(new RectangleF(jx - 34, jy - 14, 68, 30),
            Color.FromArgb(40, 42, 48), Color.FromArgb(10, 11, 13), 90f))
            g.FillEllipse(boot, jx - 34, jy - 14, 68, 30);
        using (var shaft = new LinearGradientBrush(new RectangleF(jx - 7, jy - 46, 14, 40),
            Color.FromArgb(120, 124, 132), Color.FromArgb(40, 42, 48), 0f))
            g.FillRectangle(shaft, jx - 7, jy - 46, 14, 40);
        using (var ball = new LinearGradientBrush(new RectangleF(jx - 34, jy - 96, 68, 68),
            Color.FromArgb(232, 72, 66), Color.FromArgb(126, 22, 24), 300f))
        {
            g.FillEllipse(ball, jx - 34, jy - 96, 68, 68);
            using var shine = new SolidBrush(Color.FromArgb(120, 255, 255, 255));
            g.FillEllipse(shine, jx - 18, jy - 84, 22, 16);
        }

        // Seis botones rojos en dos filas de tres.
        for (int i = 0; i < 6; i++)
        {
            float bx = r.Left + r.Width * 0.52f + (i % 3) * r.Width * 0.14f;
            float by = r.Top + r.Height * 0.46f + (i / 3) * r.Height * 0.24f;
            using (var well = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                g.FillEllipse(well, bx - 17, by - 15, 34, 34);
            using (var cap = new LinearGradientBrush(new RectangleF(bx - 15, by - 17, 30, 30),
                Color.FromArgb(236, 78, 70), Color.FromArgb(140, 26, 26), 60f))
                g.FillEllipse(cap, bx - 15, by - 17, 30, 30);
            using (var shine = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                g.FillEllipse(shine, bx - 8, by - 11, 10, 7);
        }
    }

    private static void PaintArcadeDeckLip(Graphics g)
    {
        var r = ArcadeDeckLipRect;
        using var brush = new LinearGradientBrush(r,
            Color.FromArgb(70, 120, 220), Color.FromArgb(14, 24, 54), 90f);
        g.FillRectangle(brush, r);
        using var line = new Pen(Color.FromArgb(150, 190, 230, 255), 1.2f);
        g.DrawLine(line, r.Left, r.Top + 1, r.Right, r.Top + 1);
    }

    /// <summary>Frente: arte del emulador, tipografía de marquesina y puerta de monedas.</summary>
    private static void PaintArcadeFrontPanel(Graphics g, string title, string launcher, string? artPath, string code)
    {
        var r = ArcadeFrontRect;
        using (var back = new LinearGradientBrush(r,
            Color.FromArgb(38, 72, 138), Color.FromArgb(10, 18, 38), 90f))
            g.FillRectangle(back, r);
        using var art = LoadArt(artPath);
        if (art != null)
        {
            var clip = g.Save();
            g.SetClip(r, CombineMode.Intersect);
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix
            {
                Matrix00 = 1f, Matrix11 = 1f, Matrix22 = 1f, Matrix33 = 0.55f, Matrix44 = 1f
            });
            g.DrawImage(art, Rectangle.Round(r), 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, attrs);
            g.Restore(clip);
        }
        using (var shade = new LinearGradientBrush(r,
            Color.FromArgb(0, 0, 0, 0), Color.FromArgb(170, 6, 8, 14), 90f))
            g.FillRectangle(shade, r.Left, r.Top + r.Height * 0.45f, r.Width, r.Height * 0.55f);

        string name = (string.IsNullOrWhiteSpace(title) ? launcher : title).ToUpperInvariant();
        ArcadeTracked(g, "ARCADE", r.Left, r.Top + 26, r.Width, 34f, 3.2f,
            Color.FromArgb(240, 60, 54), Color.FromArgb(20, 8, 10));
        ArcadeTracked(g, name, r.Left, r.Top + 70, r.Width, 17f, 2f,
            Color.FromArgb(235, 242, 255), Color.FromArgb(12, 16, 26));

        // Puerta de monedas: chapa oscura con dos ranuras y el botón rojo, como la foto.
        var door = new RectangleF(r.Left + r.Width * 0.5f - 62, r.Bottom - 58, 124, 44);
        using (var doorPath = RoundPath(Rectangle.Round(door), 5))
        {
            using (var metal = new LinearGradientBrush(door,
                Color.FromArgb(58, 60, 66), Color.FromArgb(24, 25, 29), 90f))
                g.FillPath(metal, doorPath);
            using (var edge = new Pen(Color.FromArgb(120, 210, 214, 224), 1.2f))
                g.DrawPath(edge, doorPath);
        }
        using (var slot = new SolidBrush(Color.FromArgb(8, 9, 11)))
        {
            g.FillRectangle(slot, door.Left + 24, door.Top + 12, 7, 20);
            g.FillRectangle(slot, door.Left + 40, door.Top + 12, 7, 20);
        }
        using (var coin = new LinearGradientBrush(new RectangleF(door.Right - 36, door.Top + 10, 22, 22),
            Color.FromArgb(226, 62, 56), Color.FromArgb(120, 18, 20), 60f))
            g.FillEllipse(coin, door.Right - 36, door.Top + 10, 22, 22);

        using (var seam = new Pen(Color.FromArgb(160, 12, 13, 17), 3f))
            g.DrawRectangle(seam, r.Left, r.Top, r.Width, r.Height);
    }

    /// <summary>Lateral: panel oscuro con el arte del emulador y la marca en vertical.</summary>
    private static void PaintArcadeSide(Graphics g, string title, string launcher, string? artPath)
    {
        var r = ArcadeSideRect;
        using (var back = new LinearGradientBrush(r,
            Color.FromArgb(24, 48, 96), Color.FromArgb(8, 12, 24), 90f))
            g.FillRectangle(back, r);
        var rnd = new Random(23);
        for (int i = 0; i < 40; i++)
        {
            float y0 = r.Top + (float)rnd.NextDouble() * r.Height;
            float x0 = r.Left + (float)rnd.NextDouble() * r.Width;
            using var pen = new Pen(Color.FromArgb(18 + rnd.Next(34), 110, 170, 255), 1f);
            g.DrawLine(pen, x0, y0, x0 + 30 + (float)rnd.NextDouble() * 60, y0 - 8 + (float)rnd.NextDouble() * 16);
        }
        using var art = LoadArt(artPath);
        if (art != null)
        {
            // Misma razón que la serigrafía: la carátula se queda en la franja donde la
            // tapa lateral existe a lo alto del cuerpo, y arriba de la tipografía.
            float side = r.Width * 0.52f;
            var box = new RectangleF(r.Left + 18, r.Top + 54, side, side);
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix
            {
                Matrix00 = 1f, Matrix11 = 1f, Matrix22 = 1f, Matrix33 = 0.8f, Matrix44 = 1f
            });
            g.DrawImage(art, Rectangle.Round(box), 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, attrs);
        }
        // La ventana lateral cubre el rectángulo completo del perfil, pero la silueta
        // NO: arriba la marquesina deja el frente vacío y abajo el cuerpo no llega tan
        // adelante. Por eso la serigrafía va sobre el 66% de atrás, donde la tapa
        // siempre existe; centrada en todo el ancho se cortaba contra el borde.
        string name = (string.IsNullOrWhiteSpace(title) ? launcher : title).ToUpperInvariant();
        float tx = r.Left + 14f, tw = r.Width * 0.62f;
        ArcadeTracked(g, "WINFORGE", tx, r.Top + r.Height * 0.565f, tw, 15f, 2.2f,
            Color.FromArgb(238, 244, 255), Color.FromArgb(10, 14, 24));
        ArcadeTracked(g, "ARCADE", tx, r.Top + r.Height * 0.625f, tw, 19f, 2f,
            Color.FromArgb(236, 66, 58), Color.FromArgb(12, 8, 10));
        ArcadeTracked(g, name, tx, r.Bottom - 40, tw, 12f, 1.2f,
            Color.FromArgb(210, 224, 244), Color.FromArgb(10, 14, 24));
        using var frame = new Pen(Color.FromArgb(220, 10, 11, 14), 8f);
        g.DrawRectangle(frame, r.Left + 4, r.Top + 4, r.Width - 8, r.Height - 8);
    }

    /// <summary>Texto centrado con tracking, como la serigrafía de la consola.</summary>
    private static void ArcadeTracked(Graphics g, string text, float left, float top, float width,
        float size, float tracking, Color color, Color shadow)
    {
        using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        float total = 0f;
        foreach (char c in text) total += g.MeasureString(c.ToString(), font).Width + tracking;
        total -= tracking;
        float x = left + (width - total) * 0.5f;
        using var brush = new SolidBrush(color);
        using var shade = new SolidBrush(shadow);
        foreach (char c in text)
        {
            string s = c.ToString();
            float w = g.MeasureString(s, font).Width;
            g.DrawString(s, font, shade, x + 1.5f, top + 2f);
            g.DrawString(s, font, brush, x, top);
            x += w + tracking;
        }
    }

    /// <summary>Dorso del gabinete: madera/plástico oscuro con la rejilla de ventilación.</summary>
    private static void PaintArcadeBack(Graphics g, string launcher, string code, Color plastic)
    {
        g.Clear(Color.FromArgb(18, 19, 22));
        var panel = new RectangleF(40, 120, FaceWidth - 80, FaceHeight - 260);
        using (var back = new LinearGradientBrush(panel,
            Color.FromArgb(30, 31, 36), Color.FromArgb(14, 15, 18), 90f))
            g.FillRectangle(back, panel);
        using (var vent = new Pen(Color.FromArgb(150, 6, 7, 9), 5f))
            for (int y = 0; y < 10; y++)
                g.DrawLine(vent, panel.Left + 60, panel.Top + 90 + y * 15, panel.Right - 60, panel.Top + 90 + y * 15);
        using var edge = new Pen(Color.FromArgb(90, 210, 214, 224), 1.2f);
        g.DrawRectangle(edge, panel.Left, panel.Top, panel.Width, panel.Height);
    }
}
