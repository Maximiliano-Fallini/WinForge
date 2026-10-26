using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Numerics;

namespace WHPO_UI.Controls;

/// <summary>
/// Cartucho procedural inspirado en Carthage (https://github.com/lopiksaa/carthage),
/// sin dependencias de WinUI ni de D3D:
///   - GEOMETRÍA 3D: losa de esquinas redondeadas (radio 36/600), muescas laterales
///     a y=708 (altura 54, profundidad 10) y canto biselado con normales suaves.
///   - CARAS PINTADAS (canvas de referencia 600×878):
///       * Carcasa de plástico con pared lateral inferior (en vista 2D), degradado
///         diagonal desde arriba-izquierda, grano fino y bisel de borde iluminado.
///       * Pestaña superior hundida (206, 34, 188×28) y ranura de flecha inferior
///         apuntando al dock (248, 798, 104×42).
///       * Pozo hundido y etiqueta redondeada (44, 60, 512×714, r=20) dividida en:
///           1. Cabecera (44, 60, 512×172): logo y marca del launcher + nombre.
///           2. Ventana de arte (44, 232, 512×454): carátula del juego (con fondo
///              borroneado si es banner horizontal, o panel tipográfico si no hay arte).
///           3. Franja inferior de código (44, 686, 512×88): etiqueta impresa en
///              monoespaciado ("STEAM - 730 - CS2").
///       * Dorso con grabado en relieve ("WINFORGE", "GAME CARD", código) y ventana
///         hundida inferior (128, 690, 344×96) con 11 contactos dorados.
///   - HLSL del sombreado 3D (dos luces direccionales + especular diferenciado entre
///     plástico vivo y calcomanía mate).
/// </summary>
public static class CartridgeModel
{
    // ===== Canvas de referencia (idéntico a Carthage theme.py) =====
    public const int FaceWidth = 600;
    public const int FaceHeight = 878;
    public const int ShellThickness2D = 12;

    /// <summary>Alto/ancho del cartucho (878 / 600 ≈ 1.4633): evita deformar la card.</summary>
    public const float Aspect = FaceHeight / (float)FaceWidth;

    // ===== Medidas del modelo 3D, en unidades de "ancho de cara" =====
    public const float Width = 1f;
    public const float Height = Aspect;
    /// <summary>Grosor 3D: 46 sobre una cara de 600 (≈7,7%).</summary>
    public const float Thickness = 46f / FaceWidth;
    /// <summary>Radio de las esquinas exteriores: 36/600 (cartucho real, no teléfono).</summary>
    public const float CornerRadius = 36f / FaceWidth;
    /// <summary>Radio del canto biselado 3D (fino como en la card de la biblioteca).</summary>
    public const float BevelRadius = 2.5f / FaceWidth;
    /// <summary>Muescas laterales de traba: y=708 (desde arriba), alto 54, prof 10.</summary>
    public const float NotchTopY = 708f;
    public const float NotchHeightPx = 54f;
    public const float NotchDepthPx = 10f;
    public const float NotchDepth = NotchDepthPx / FaceWidth;
    public const float NotchHeight = NotchHeightPx / FaceWidth;

    // ===== Geometría de la etiqueta sobre el canvas 600×878 =====
    public const int StickerX = 44, StickerY = 60, StickerW = 512, StickerH = 714, StickerR = 20;
    public const int StickerWellInset = 5;
    public const int TabX = 206, TabY = 34, TabW = 188, TabH = 28, TabR = 10;
    public const int HeaderX = 44, HeaderY = 60, HeaderW = 512, HeaderH = 172;
    public const int ArtX = 44, ArtY = 232, ArtW = 512, ArtH = 454;
    public const int LabelX = 44, LabelY = 686, LabelW = 512, LabelH = 88;
    public const int ArrowX = 248, ArrowY = 798, ArrowW = 104, ArrowH = 42;

    // Compatibilidad
    public const int HeroH = ArtH, CodeH = LabelH;

    /// <summary>Tamaño en bytes del vértice (lo usa el input layout de D3D).</summary>
    public const int VertexSize = 48;

    public readonly struct Vertex
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Vector2 Uv;
        /// <summary>rgb = 1; a = 1 en frente, 2 en dorso, 0 en canto de plástico.</summary>
        public readonly Vector4 Tint;

        public Vertex(Vector3 p, Vector3 n, Vector2 uv, Vector4 t)
        { Position = p; Normal = n; Uv = uv; Tint = t; }
    }

    // ===== Shading HLSL: caras fieles a la card de la biblioteca + volumen en el canto =====
    public const string Hlsl = @"
cbuffer Constants : register(b0)
{
    row_major float4x4 World;
    row_major float4x4 View;
    row_major float4x4 Projection;
    float4 Plastic;
};
struct VSIn { float3 Pos:POSITION; float3 Nrm:NORMAL; float2 Uv:TEXCOORD0; float4 Tint:COLOR0; };
struct VSOut { float4 Pos:SV_POSITION; float3 Nrm:NORMAL; float2 Uv:TEXCOORD0; float4 Tint:COLOR0; };
VSOut VSMain(VSIn i)
{
    VSOut o;
    float4 wp = mul(float4(i.Pos, 1.0f), World);
    o.Pos = mul(mul(wp, View), Projection);
    o.Nrm = mul(float4(i.Nrm, 0.0f), World).xyz;
    o.Uv = i.Uv;
    o.Tint = i.Tint;
    return o;
}
Texture2D face : register(t0);
Texture2D back : register(t1);
SamplerState samp : register(s0);
float4 PSMain(VSOut i) : SV_TARGET
{
    float3 n = normalize(i.Nrm);
    float3 clave = normalize(float3(-0.32f, 0.44f, 0.84f));
    float3 relleno = normalize(float3(0.38f, -0.42f, 0.62f));
    // En las caras planas (frente y dorso, mask=1) se preserva al 100% el color exacto de
    // la textura pintada para que luzca idéntica a la card de la biblioteca; en el canto
    // (mask=0) el espesor tiene el tono oscuro de la pared inferior (0.86 del plástico)
    // con una variación sutil al girarlo.
    float luzCanto = 0.84f + 0.08f * saturate(dot(n, clave)) + 0.04f * saturate(dot(n, relleno));
    float4 arteFrente = face.Sample(samp, i.Uv);
    float4 arteDorso = back.Sample(samp, i.Uv);
    float4 arte = lerp(arteFrente, arteDorso, step(1.5f, i.Tint.a));
    float mask = step(0.5f, i.Tint.a);
    float3 colorCanto = Plastic.rgb * luzCanto;
    float3 colorCara = arte.rgb;
    float3 finalRgb = saturate(lerp(colorCanto, colorCara, mask));
    return float4(finalRgb, 1.0f);
}";

    // ===== Geometría 3D =====

    /// <summary>
    /// Contorno 2D de la losa (x a la derecha, y arriba), con las muescas laterales en la
    /// posición exacta de Carthage (y=708..762 en el canvas de 878 de alto, es decir en el
    /// tercio inferior del cartucho).
    /// </summary>
    private static List<(Vector2 P, Vector2 N)> BuildOutline()
    {
        float hw = Width * 0.5f, hh = Height * 0.5f, r = CornerRadius;
        float nd = NotchDepth;
        // En el canvas 600x878, y crece hacia abajo; en el modelo 3D, y crece hacia arriba.
        float notchTop = hh - (NotchTopY / FaceWidth);
        float notchBot = hh - ((NotchTopY + NotchHeightPx) / FaceWidth);
        const int Arco = 10;
        var pts = new List<Vector2>();

        // Borde superior, de izquierda a derecha.
        pts.Add(new(-hw + r, hh));
        pts.Add(new(hw - r, hh));
        // Esquina superior derecha: 90° → 0°.
        for (int i = 1; i <= Arco; i++)
        {
            float a = MathF.PI * 0.5f * (1f - i / (float)Arco);
            pts.Add(new(hw - r + r * MathF.Cos(a), hh - r + r * MathF.Sin(a)));
        }
        // Lado derecho hacia abajo, con la muesca biselada a 45° como en Carthage.
        pts.Add(new(hw, notchTop));
        pts.Add(new(hw - nd, notchTop - nd));
        pts.Add(new(hw - nd, notchBot + nd));
        pts.Add(new(hw, notchBot));
        pts.Add(new(hw, -hh + r));
        // Esquina inferior derecha: 0° → -90°.
        for (int i = 1; i <= Arco; i++)
        {
            float a = -MathF.PI * 0.5f * i / Arco;
            pts.Add(new(hw - r + r * MathF.Cos(a), -hh + r + r * MathF.Sin(a)));
        }
        // Borde inferior, de derecha a izquierda.
        pts.Add(new(-hw + r, -hh));
        // Esquina inferior izquierda: -90° → -180°.
        for (int i = 1; i <= Arco; i++)
        {
            float a = -MathF.PI * 0.5f - MathF.PI * 0.5f * i / Arco;
            pts.Add(new(-hw + r + r * MathF.Cos(a), -hh + r + r * MathF.Sin(a)));
        }
        // Lado izquierdo hacia arriba, con su muesca biselada.
        pts.Add(new(-hw, notchBot));
        pts.Add(new(-hw + nd, notchBot + nd));
        pts.Add(new(-hw + nd, notchTop - nd));
        pts.Add(new(-hw, notchTop));
        pts.Add(new(-hw, hh - r));
        // Esquina superior izquierda: 180° → 90°.
        for (int i = 1; i <= Arco; i++)
        {
            float a = MathF.PI - MathF.PI * 0.5f * i / Arco;
            pts.Add(new(-hw + r + r * MathF.Cos(a), hh - r + r * MathF.Sin(a)));
        }

        var outline = new List<(Vector2 P, Vector2 N)>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            var prev = pts[(i - 1 + pts.Count) % pts.Count];
            var next = pts[(i + 1) % pts.Count];
            var n1 = Perp(pts[i] - prev);
            var n2 = Perp(next - pts[i]);
            var n = n1 + n2;
            if (n.LengthSquared() < 1e-8f) n = n2;
            outline.Add((pts[i], Vector2.Normalize(n)));
        }
        return outline;

        static Vector2 Perp(Vector2 d)
        {
            var n = new Vector2(d.Y, -d.X);
            return n.LengthSquared() < 1e-8f ? Vector2.Zero : Vector2.Normalize(n);
        }
    }

    public static (Vertex[] Vertices, ushort[] Indices) BuildMesh()
    {
        var outline = BuildOutline();
        int n = outline.Count;
        float t = Thickness * 0.5f, r = BevelRadius;
        const int AnillosPerfil = 4;

        var verts = new List<Vertex>();
        var idx = new List<ushort>();

        // En la textura 3D (flat=true) la pared inferior de espesor la aporta la propia
        // malla 3D, así que la cara se mapea sobre el alto útil (866 px) sin doble pared.
        float uvHeight = Height * ((FaceHeight - ShellThickness2D) / (float)FaceHeight);
        Vector2 UvFrente(Vector2 p) => new(p.X / Width + 0.5f, (0.5f - p.Y / Height) * (uvHeight / Height));

        ushort[] AddRing(float u, float z, float nz, float arco, bool dorso)
        {
            var ring = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                var (p, pn) = outline[i];
                var pos = new Vector3(p.X - pn.X * u, p.Y - pn.Y * u, z);
                var nrm = new Vector3(pn.X * arco, pn.Y * arco, nz);
                nrm = nrm.LengthSquared() < 1e-8f ? Vector3.UnitZ * (dorso ? -1f : 1f) : Vector3.Normalize(nrm);
                var uv = UvFrente(new Vector2(pos.X, pos.Y));
                uv = new Vector2(Math.Clamp(uv.X, 0.002f, 0.998f), Math.Clamp(uv.Y, 0.002f, 0.998f));
                if (dorso) uv = new Vector2(1f - uv.X, uv.Y);
                var tint = new Vector4(1f, 1f, 1f, MathF.Abs(nz) > 0.99f ? (dorso ? 2f : 1f) : 0f);
                ring[i] = (ushort)verts.Count;
                verts.Add(new Vertex(pos, nrm, uv, tint));
            }
            return ring;
        }

        ushort[] Perfil(float a, bool atras) => AddRing(
            r * (1f - MathF.Sin(a)),
            ((t - r) + r * MathF.Cos(a)) * (atras ? -1f : 1f),
            MathF.Cos(a) * (atras ? -1f : 1f),
            MathF.Sin(a),
            atras);

        var frente = new List<ushort[]>();
        var dorso = new List<ushort[]>();
        for (int k = 0; k <= AnillosPerfil; k++)
        {
            frente.Add(Perfil(MathF.PI * 0.5f * (AnillosPerfil - k) / AnillosPerfil, false));
            dorso.Add(Perfil(MathF.PI * 0.5f * k / AnillosPerfil, true));
        }

        void Banda(ushort[] a, ushort[] b)
        {
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                idx.Add(a[i]); idx.Add(b[i]); idx.Add(b[j]);
                idx.Add(a[i]); idx.Add(b[j]); idx.Add(a[j]);
            }
        }
        for (int k = 0; k < frente.Count - 1; k++) Banda(frente[k], frente[k + 1]);
        for (int k = 0; k < dorso.Count - 1; k++) Banda(dorso[k], dorso[k + 1]);
        Banda(dorso[^1], frente[0]);

        void AddCara(ushort[] ring, float z, bool esDorso)
        {
            var uv = UvFrente(Vector2.Zero);
            if (esDorso) uv = new Vector2(1f - uv.X, uv.Y);
            var tint = new Vector4(1f, 1f, 1f, esDorso ? 2f : 1f);
            ushort c = (ushort)verts.Count;
            verts.Add(new Vertex(new Vector3(0, 0, z), new Vector3(0, 0, esDorso ? -1f : 1f), uv, tint));
            for (int i = 0; i < ring.Length; i++)
            {
                int j = (i + 1) % ring.Length;
                idx.Add(c);
                if (esDorso) { idx.Add(ring[j]); idx.Add(ring[i]); }
                else { idx.Add(ring[i]); idx.Add(ring[j]); }
            }
        }
        AddCara(frente[^1], t, false);
        AddCara(dorso[0], -t, true);

        return (verts.ToArray(), idx.ToArray());
    }

    // ===== Paleta y códigos =====

    /// <summary>
    /// Color de plástico de la carcasa. Por defecto usa el grafito oscuro estilo
    /// Carthage (#3c3c41 → #222226) con un sutil matiz derivado del arte o launcher,
    /// para que toda la bandeja luzca como hardware real de colección.
    /// </summary>
    public static Color PlasticColor(string title, string? artPath)
    {
        // Color de plástico ÚNICO para toda la bandeja: el que elige la apariencia 3D
        // (CardSkin). Así el cartucho y la consola del pie comparten el color del tema o
        // el preset elegido en Configuración → Apariencia. Antes se teñía con el promedio
        // del arte de cada juego y la biblioteca se veía como cartuchos de lotes distintos.
        // Los parámetros se mantienen por compatibilidad: el color ya no depende del juego.
        return CardSkin.Current.Plastic;
    }

    /// <summary>
    /// Etiqueta inferior estilo Carthage: "STEAM - 730 - CS2" o "EPIC - FORTNITE".
    /// </summary>
    public static string Code(string launcher, string? appId, string title)
    {
        string source = string.IsNullOrWhiteSpace(launcher) ? "WINFORGE" : launcher.Trim().ToUpperInvariant();
        if (source == "BLIZZARD") source = "BATTLE.NET";

        // Código corto de 3 a 6 letras derivado del título (como el slug de Carthage)
        string shortCode = BuildShortTitleCode(title);
        if (!string.IsNullOrWhiteSpace(appId) && appId.Length <= 8 && !string.Equals(appId, shortCode, StringComparison.OrdinalIgnoreCase))
            return $"{source} - {appId.ToUpperInvariant()} - {shortCode}";
        return $"{source} - {shortCode}";
    }

    private static string BuildShortTitleCode(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "GAME";
        var words = title.Split(new[] { ' ', ':', '-', '_', '.', '’', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
        {
            var chars = new List<char>();
            foreach (var w in words)
            {
                char c = char.ToUpperInvariant(w[0]);
                if (char.IsLetterOrDigit(c)) chars.Add(c);
                if (chars.Count >= 6) break;
            }
            if (chars.Count >= 2) return new string(chars.ToArray());
        }
        var clean = new List<char>();
        foreach (char ch in title.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch)) clean.Add(ch);
            if (clean.Count >= 6) break;
        }
        return clean.Count > 0 ? new string(clean.ToArray()) : "GAME";
    }

    // ===== Pintado de caras (2D en grilla y texturas 3D) =====

    /// <summary>
    /// Silueta del cartucho con esquinas redondeadas y las dos muescas laterales en y=708.
    /// Si <paramref name="height"/> es menor que FaceHeight (vista 2D con pared inferior),
    /// recorta la cara superior dejando visible la pared inferior.
    /// </summary>
    private static GraphicsPath BuildCartridgePath(float inset, float height)
    {
        float x0 = inset, y0 = inset;
        float x1 = FaceWidth - inset, y1 = height - inset;
        float r = Math.Max(4f, 36f - inset);
        float d = r * 2f;

        float ny0 = NotchTopY;
        float ny1 = NotchTopY + NotchHeightPx;
        float nd = NotchDepthPx;

        var p = new GraphicsPath();
        // Arriba-izquierda a arriba-derecha
        p.AddArc(x0, y0, d, d, 180, 90);
        p.AddArc(x1 - d, y0, d, d, 270, 90);

        // Lado derecho con muesca
        p.AddLine(x1, y0 + r, x1, ny0);
        p.AddLine(x1, ny0, x1 - nd, ny0 + nd);
        p.AddLine(x1 - nd, ny0 + nd, x1 - nd, ny1 - nd);
        p.AddLine(x1 - nd, ny1 - nd, x1, ny1);
        p.AddLine(x1, ny1, x1, y1 - r);

        // Abajo-derecha a abajo-izquierda
        p.AddArc(x1 - d, y1 - d, d, d, 0, 90);
        p.AddArc(x0, y1 - d, d, d, 90, 90);

        // Lado izquierdo con muesca
        p.AddLine(x0, y1 - r, x0, ny1);
        p.AddLine(x0, ny1, x0 + nd, ny1 - nd);
        p.AddLine(x0 + nd, ny1 - nd, x0 + nd, ny0 + nd);
        p.AddLine(x0 + nd, ny0 + nd, x0, ny0);
        p.AddLine(x0, ny0, x0, y0 + r);

        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// Dibuja la carcasa plástica compartida por el frente y el dorso (exactamente como
    /// `_draw_body` en `render.py` de Carthage).
    /// Con <paramref name="flat"/> = false (grilla 2D), deja transparente el exterior de
    /// las esquinas/muescas y pinta la pared inferior de espesor 12 px.
    /// Con <paramref name="flat"/> = true (textura del modelo 3D) pinta la MISMA carcasa
    /// (incluida la pared inferior) sobre base opaca: la silueta la aporta la malla y así
    /// el cartucho del popup luce igual que la card de la biblioteca.
    /// </summary>
    private static void DrawShellBody(Graphics g, Color plastic, bool flat)
    {
        if (flat)
        {
            // Textura del modelo 3D: la MISMA carcasa que la card de la biblioteca
            // (incluida la pared inferior de espesor ShellThickness2D), pintada sobre
            // base opaca en vez de dejar transparente el exterior de la silueta: la
            // silueta en 3D la da la malla. Antes la textura 3D era plástico plano sin
            // pared y el cartucho del popup salía con otro estilo que el de la grilla.
            using var flatBrush = new SolidBrush(plastic);
            g.FillRectangle(flatBrush, 0, 0, FaceWidth, FaceHeight);
        }
        else
        {
            g.Clear(Color.Transparent);
        }

        // Pared inferior de espesor (vista en la grilla 2D): tono sólido levemente más
        // oscuro que la cara superior, sin degradado ni sombras artificiales pintadas.
        using (var fullShell = BuildCartridgePath(0.5f, FaceHeight))
        using (var sideBrush = new SolidBrush(Darken(plastic, 0.14f)))
        {
            g.FillPath(sideBrush, fullShell);
        }

        // Cara superior plana y limpia (termina ShellThickness2D unidades antes del borde
        // inferior): sin degradado diagonal ni sombras pintadas encima.
        float faceH = FaceHeight - ShellThickness2D;
        using var facePath = BuildCartridgePath(0.5f, faceH);
        using (var faceBrush = new SolidBrush(plastic))
        {
            g.FillPath(faceBrush, facePath);
        }
    }

    /// <summary>
    /// Pinta un HUECO moldeado en el plástico: el pozo donde vive la etiqueta y la ventana
    /// de los contactos del dorso.
    ///
    /// Tres capas, en este orden y por una razón:
    ///   1. piso oscuro — al fondo del hueco no llega la luz, así que arranca más oscuro
    ///      que el plástico que lo rodea (y tapa el antialias del borde);
    ///   2. oclusión ambiental — un degradado radial que oscurece MÁS el borde que mira a
    ///      la luz (arriba-izquierda) que el opuesto, con el foco corrido hacia
    ///      abajo-derecha;
    ///   3. canto del molde — la pared vertical y el labio iluminado del lado en sombra.
    ///
    /// Las capas 2 y 3 se dibujan con el clip puesto, así que caen ENCIMA del contenido:
    /// eso es lo que hace que el rectángulo se lea hundido y no pegado sobre la carcasa.
    /// </summary>
    private static void DrawRecess(Graphics g, GraphicsPath path, Rectangle bounds, Color plastic, float depth, bool paintFloor)
    {
        if (path.PointCount < 3 || bounds.Width < 4 || bounds.Height < 4) return;

        if (paintFloor)
        {
            using var floorBrush = new SolidBrush(Mix(plastic, Color.Black, 0.66f));
            g.FillPath(floorBrush, path);
        }

        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);

        // Oclusión ambiental en anillos, no en un solo degradado radial: cada anillo es un
        // trazo grueso centrado en el borde (el clip deja ver solo su mitad interior), y la
        // suma de anillos con alfa decreciente hace que la sombra nazca casi negra contra la
        // pared y se apague hacia el centro. Un degradado único teñía de parejo todo el
        // contenido y el pozo se veía plano.
        var tl = new PointF(bounds.Left, bounds.Top);
        var br = new PointF(bounds.Right, bounds.Bottom);
        float maxAlpha = Math.Clamp(depth * 30f, 46f, 190f);
        const int Anillos = 9;
        for (int i = 0; i < Anillos; i++)
        {
            float t = i / (float)(Anillos - 1);
            int alpha = (int)(maxAlpha * (1f - t) * (1f - t));
            if (alpha <= 2) continue;
            using var penumbra = new LinearGradientBrush(
                tl, br,
                Color.FromArgb(alpha, 0, 0, 0),
                Color.FromArgb(0, 0, 0, 0));
            using var anillo = new Pen(penumbra, 2f + i * 4f);
            g.DrawPath(anillo, path);
        }

        // Pared del molde: el canto vertical entre el plástico y el contenido. Con el clip
        // puesto solo asoma la mitad interior del trazo, así queda pegada al borde.
        using (var wallPen = new Pen(Mix(plastic, Color.Black, 0.52f), Math.Max(1.6f, depth * 0.55f)))
        {
            g.DrawPath(wallPen, path);
        }

        // Canto del hueco: el labio inferior-derecho recibe la luz, el resto queda en sombra.
        // Es el único elemento claro del conjunto, así que carga con casi toda la lectura
        // del relieve: sobre un contenido oscuro es lo que separa el pozo de la carcasa.
        using (var lip = new LinearGradientBrush(
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            Color.Black, Color.Black))
        {
            lip.InterpolationColors = new ColorBlend
            {
                Colors = new[] { ToAlpha(Color.White, 0), ToAlpha(Color.White, 0), ToAlpha(Color.White, 168) },
                Positions = new[] { 0f, 0.58f, 1f }
            };
            using var lipPen = new Pen(lip, Math.Max(1.6f, depth * 0.55f));
            g.DrawPath(lipPen, path);
        }

        g.Restore(state);
    }

    /// <summary>
    /// Pinta una pieza moldeada en RELIEVE sobre el plástico: la pestaña superior, la flecha
    /// del dock, cualquier detalle que tenga que sobresalir.
    ///
    /// La cara lleva un degradado que va del tono iluminado (arriba-izquierda) al tono en
    /// sombra (abajo-derecha), el canto que mira a la luz se subraya en blanco y el opuesto
    /// en negro, y detrás se proyecta una sombra corta. Esa sombra —y no un contorno duro—
    /// es lo que separa la pieza de la carcasa y le da volumen.
    /// </summary>
    private static void DrawRelief(Graphics g, GraphicsPath path, Rectangle bounds, Color plastic, float lift)
    {
        if (bounds.Width < 3 || bounds.Height < 3) return;

        if (lift > 0.5f)
        {
            var estado = g.Save();
            g.TranslateTransform(lift * 0.45f, lift * 0.7f);
            using (var drop = new SolidBrush(Color.FromArgb(66, 0, 0, 0)))
                g.FillPath(drop, path);
            g.Restore(estado);
        }

        using (var cara = new LinearGradientBrush(
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            Lighten(plastic, 0.15f), Darken(plastic, 0.24f)))
        {
            g.FillPath(cara, path);
        }

        using (var canto = new LinearGradientBrush(
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            Color.Black, Color.Black))
        {
            canto.InterpolationColors = new ColorBlend
            {
                Colors = new[] { ToAlpha(Color.White, 124), ToAlpha(Color.White, 0), Color.FromArgb(112, 0, 0, 0) },
                Positions = new[] { 0f, 0.56f, 1f }
            };
            using var cantoPen = new Pen(canto, 1.4f);
            g.DrawPath(cantoPen, path);
        }
    }

    /// <summary>
    /// Cara frontal del cartucho: carcasa, pestaña superior hundida, pozo, etiqueta con
    /// cabecera del launcher, ventana de carátula, franja inferior de código y flecha inferior.
    /// </summary>
    public static Bitmap PaintFace(string title, string launcher, string code, string? artPath, Color plastic, bool flat = true, GameHeader? header = null)
    {
        var bmp = new Bitmap(FaceWidth, FaceHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        DrawShellBody(g, plastic, flat);

        // 1) Pestaña superior en relieve: la traba donde el cartucho se engancha.
        var tabRect = new Rectangle(TabX, TabY, TabW, TabH);
        using (var tabPath = RoundPath(tabRect, TabR))
            DrawRelief(g, tabPath, tabRect, plastic, 4f);

        // 2) Etiqueta redondeada (clip de cabecera + carátula + franja de código)
        var stickerRect = new Rectangle(StickerX, StickerY, StickerW, StickerH);
        using var stickerPath = RoundPath(stickerRect, StickerR);

        // Marco del molde: el canto de plástico que rodea el hueco, más ancho que el borde
        // porque la mitad interior la tapa el piso del pozo. Recibe luz en el lado que mira
        // a la luz y sombra en el opuesto; es lo que hace que el rectángulo parezca estar
        // dentro de la carcasa y no apoyado arriba.
        using (var frame = new LinearGradientBrush(
            new PointF(stickerRect.Left, stickerRect.Top),
            new PointF(stickerRect.Right, stickerRect.Bottom),
            Color.Black, Color.Black))
        {
            frame.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(150, 0, 0, 0),
                    Color.FromArgb(0, 0, 0, 0),
                    ToAlpha(Lighten(plastic, 0.32f), 195)
                },
                Positions = new[] { 0f, 0.52f, 1f }
            };
            using var framePen = new Pen(frame, 10f);
            g.DrawPath(framePen, stickerPath);
        }

        // Piso del pozo: queda debajo del contenido, así el antialias del borde no deja
        // asomar el plástico claro y el rectángulo arranca ya hundido.
        using (var pocketFloor = new SolidBrush(Mix(plastic, Color.Black, 0.62f)))
            g.FillPath(pocketFloor, stickerPath);

        var prevClip = g.Clip;
        g.SetClip(stickerPath, CombineMode.Intersect);

        var headerRect = new Rectangle(HeaderX, HeaderY, HeaderW, HeaderH + 1);
        var artRect = new Rectangle(ArtX, ArtY, ArtW, ArtH);
        var labelRect = new Rectangle(LabelX, LabelY, LabelW, LabelH + 1);

        // Fondo de la cabecera (#1c1c1f)
        using (var headBrush = new SolidBrush(Color.FromArgb(28, 28, 32)))
            g.FillRectangle(headBrush, headerRect);

        // Cabecera: logo oficial del launcher + nombre de la plataforma, o el texto
        // elegido por juego (nombre del juego, texto libre, solo marca o nada).
        PaintHeader(g, headerRect, launcher, title, header);

        // Fondo de la ventana de arte
        using (var artBack = new SolidBrush(Color.FromArgb(14, 14, 18)))
            g.FillRectangle(artBack, artRect);

        // Carátula del juego dentro de ArtRect (44, 232, 512×454)
        PaintArtWindow(g, artRect, title, artPath, plastic);

        // Línea divisoria sutil sobre y bajo el arte
        using (var sepPen = new Pen(Color.FromArgb(90, 0, 0, 0), 2f))
        {
            g.DrawLine(sepPen, artRect.Left, artRect.Top, artRect.Right, artRect.Top);
            g.DrawLine(sepPen, labelRect.Left, labelRect.Top, labelRect.Right, labelRect.Top);
        }

        // Franja inferior de código (#0b0b0c)
        using (var labelBrush = new SolidBrush(Color.FromArgb(11, 11, 13)))
            g.FillRectangle(labelBrush, labelRect);

        PaintCodeStrip(g, labelRect, code);

        g.Clip = prevClip;

        // El pozo de la etiqueta: oclusión ambiental y canto del molde ENCIMA del
        // contenido. Es la capa que hace que todo el rectángulo se vea metido hacia
        // adentro en vez de apoyado en la carcasa.
        DrawRecess(g, stickerPath, stickerRect, plastic, 6f, paintFloor: false);

        // 3) Flecha inferior en relieve (apunta al dock): la misma pieza moldeada que la pestaña
        var arrowRect = new Rectangle(ArrowX, ArrowY, ArrowW, ArrowH);
        using (var arrowPath = new GraphicsPath())
        {
            arrowPath.AddPolygon(new[]
            {
                new PointF(ArrowX, ArrowY),
                new PointF(ArrowX + ArrowW, ArrowY),
                new PointF(ArrowX + ArrowW * 0.5f, ArrowY + ArrowH)
            });
            arrowPath.CloseFigure();
            DrawRelief(g, arrowPath, arrowRect, plastic, 4f);
        }

        return bmp;
    }

    /// <summary>
    /// Cabecera de la etiqueta (44, 60, 512×172): muestra el icono/logo del launcher y
    /// su nombre en tipografía pesada centrada (como `render_header` en Carthage).
    /// </summary>
    private static void PaintHeader(Graphics g, Rectangle r, string launcher, string title, GameHeader? look)
    {
        string mode = look?.Mode ?? GameLook.HeaderPlatform;
        // «Sin texto» deja el fondo oscuro de la cabecera: la etiqueta respira sin marca.
        if (mode == GameLook.HeaderNone) return;

        string norm = (launcher ?? "").Trim();
        string brandName = norm.ToUpperInvariant() switch
        {
            "STEAM" => "STEAM",
            "EPIC" or "EPIC GAMES" => "EPIC GAMES",
            "BLIZZARD" or "BATTLE.NET" => "BATTLE.NET",
            "UBISOFT" => "UBISOFT",
            "EA" => "EA",
            "GOG" => "GOG",
            "XBOX" => "XBOX",
            "RIOT" => "RIOT GAMES",
            "BLACKSMITH" => "BLACKSMITH",
            "EMULADOR" => "EMULATOR",
            _ => string.IsNullOrWhiteSpace(norm) ? "WINFORGE" : norm.ToUpperInvariant()
        };

        bool showLogo = mode == GameLook.HeaderPlatform;
        string text = mode switch
        {
            GameLook.HeaderTitle => (title ?? "").Trim().ToUpperInvariant(),
            GameLook.HeaderCustom => (look?.Text ?? "").Trim().ToUpperInvariant(),
            _ => brandName
        };
        if (string.IsNullOrWhiteSpace(text)) return;

        using var logoBmp = showLogo ? LoadLauncherLogoBitmap(norm) : null;
        int iconSize = logoBmp != null ? 54 : 0;
        int gap = logoBmp != null ? 16 : 0;
        float available = Math.Max(40f, r.Width - 32 - iconSize - gap);

        // El texto se achica hasta entrar en la cabecera: un nombre de juego largo no
        // puede pisar la carátula ni salirse de la etiqueta.
        Font font = new("Segoe UI", 34f, FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF textSize = g.MeasureString(text, font);
        for (float size = 32f; size >= 18f && textSize.Width > available; size -= 2f)
        {
            font.Dispose();
            font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            textSize = g.MeasureString(text, font);
        }

        using (font)
        using (var textBrush = new SolidBrush(Color.FromArgb(244, 244, 246)))
        using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
        {
            // Último recurso: recorte con puntos suspensivos si ni en el mínimo entra.
            if (textSize.Width > available)
            {
                while (text.Length > 1 && g.MeasureString(text + "…", font).Width > available)
                    text = text[..^1];
                text = text.TrimEnd() + "…";
                textSize = g.MeasureString(text, font);
            }

            float totalW = iconSize + gap + textSize.Width;
            float startX = r.X + (r.Width - totalW) * 0.5f;
            float centerY = r.Y + r.Height * 0.5f;

            if (logoBmp != null)
            {
                var iconRect = new Rectangle(
                    (int)MathF.Round(startX),
                    (int)MathF.Round(centerY - iconSize * 0.5f),
                    iconSize, iconSize);
                g.DrawImage(logoBmp, iconRect);
            }

            var textRect = new RectangleF(startX + iconSize + gap, r.Y, textSize.Width + 20, r.Height);
            g.DrawString(text, font, textBrush, textRect, sf);
        }
    }

    private static Bitmap? LoadLauncherLogoBitmap(string launcher)
    {
        string? fileName = launcher.Trim().ToLowerInvariant() switch
        {
            "steam" => "steamlogo.png",
            "epic" or "epic games" => "epicgameslogo.png",
            "blizzard" or "battle.net" => "battlenet.png",
            "ubisoft" => "ubisoftlogo.png",
            "ea" => "ealogo.png",
            "gog" => "goglogo.png",
            "xbox" => "xboxlogo.png",
            "riot" => "riotlogo.png",
            "blacksmith" => "blacksmithlogo.png",
            _ => null
        };
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string path = fileName != null
                ? Path.Combine(baseDir, "logos", "launcher", fileName)
                : Path.Combine(baseDir, "logos", "WinForge.png");
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var raw = new Bitmap(fs);
            return new Bitmap(raw);
        }
        catch { return null; }
    }

    /// <summary>
    /// Ventana de arte (44, 232, 512×454):
    /// - Si la imagen es panorámica (como el header 460×215 de Steam), dibuja una copia
    ///   borroneada y oscurecida llenando el fondo, luego el banner completo centrado y
    ///   el título del juego abajo para que luzca nítido y sin recortes feos.
    /// - Si es vertical/cuadrada, llena la ventana con Cover y el título abajo.
    /// - Si no hay imagen, muestra un panel con degradado y el título tipográfico grande.
    /// </summary>
    private static void PaintArtWindow(Graphics g, Rectangle artRect, string title, string? artPath, Color plastic)
    {
        var oldClip = g.Clip;
        g.SetClip(artRect, CombineMode.Intersect);

        using (var artBmp = LoadArt(artPath))
        {
            if (artBmp != null)
            {
                bool isWideBanner = artBmp.Width > artBmp.Height * 1.35f;
                if (isWideBanner)
                {
                    // 1) Fondo borroneado que cubre toda la ventana (como el Loader wideArt de Carthage)
                    using (var blurred = Blurred(artBmp, 14))
                    {
                        var bgCover = Cover(blurred.Size, artRect);
                        using var darkAttr = new ImageAttributes();
                        darkAttr.SetColorMatrix(new ColorMatrix(new[]
                        {
                            new[] { 0.62f, 0f, 0f, 0f, 0f },
                            new[] { 0f, 0.62f, 0f, 0f, 0f },
                            new[] { 0f, 0f, 0.62f, 0f, 0f },
                            new[] { 0f, 0f, 0f, 1f, 0f },
                            new[] { 0f, 0f, 0f, 0f, 1f }
                        }));
                        g.DrawImage(blurred, bgCover, 0, 0, blurred.Width, blurred.Height, GraphicsUnit.Pixel, darkAttr);
                    }

                    // 2) El banner panorámico completo encajado en la mitad superior/central
                    var fitZone = new Rectangle(artRect.X, artRect.Y + 18, artRect.Width, (int)(artRect.Height * 0.64f));
                    var fit = FitInside(artBmp.Size, fitZone);

                    g.DrawImage(artBmp, fit);
                }
                else if (artBmp.Width <= 256 && artBmp.Height <= 256)
                {
                    // Ícono de ejecutable (64..256 px): centrarlo nítido sobre fondo limpio
                    // en lugar de estirarlo 8x a 512×454.
                    using (var bg = new SolidBrush(Color.FromArgb(30, 32, 38)))
                        g.FillRectangle(bg, artRect);
                    int iconSide = 168;
                    var iconDst = new Rectangle(
                        artRect.X + (artRect.Width - iconSide) / 2,
                        artRect.Y + (artRect.Height - 90 - iconSide) / 2,
                        iconSide, iconSide);
                    g.DrawImage(artBmp, iconDst);
                }
                else
                {
                    var coverZone = Cover(artBmp.Size, artRect);
                    g.DrawImage(artBmp, coverZone);
                }

                // Título limpio del juego, sin franja/sombra negra artificial encima del arte
                DrawArtTitle(g, new RectangleF(artRect.X + 24, artRect.Bottom - 118, artRect.Width - 48, 102), title);
            }
            else
            {
                // Sin arte: panel plano uniforme y título en mayúsculas (sin degradado artificial)
                using (var bg = new SolidBrush(Color.FromArgb(38, 40, 48)))
                {
                    g.FillRectangle(bg, artRect);
                }
                using var font = new Font("Segoe UI", 44f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(Color.FromArgb(232, 232, 238));
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisWord
                };
                g.DrawString((title ?? "JUEGO").ToUpperInvariant(), font, brush,
                    new RectangleF(artRect.X + 36, artRect.Y + 36, artRect.Width - 72, artRect.Height - 72), sf);
            }
        }

        g.Clip = oldClip;
    }

    private static void DrawArtTitle(Graphics g, RectangleF box, string title)
    {
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Far,
            Trimming = StringTrimming.EllipsisWord
        };
        float size = 36f;
        Font? font = null;
        while (size > 22f)
        {
            font?.Dispose();
            font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(title, font, (int)box.Width, sf);
            if (measured.Height <= box.Height) break;
            size -= 3f;
        }
        font ??= new Font("Segoe UI", 22f, FontStyle.Bold, GraphicsUnit.Pixel);
        using (font)
        {
            using var white = new SolidBrush(Color.FromArgb(248, 248, 250));
            g.DrawString(title, font, white, box, sf);
        }
    }

    /// <summary>
    /// Franja inferior de código (44, 686, 512×88): texto monoespaciado centrado que se
    /// ajusta en tamaño para entrar completo (como el `label` de `Cartridge.qml`).
    /// </summary>
    private static void PaintCodeStrip(Graphics g, Rectangle r, string code)
    {
        string txt = string.IsNullOrWhiteSpace(code) ? "WINFORGE - GAME" : code.ToUpperInvariant();
        float maxW = r.Width - 48f;
        float size = 28f;
        Font? font = null;
        while (size > 16f)
        {
            font?.Dispose();
            font = new Font("Consolas", size, FontStyle.Bold, GraphicsUnit.Pixel);
            if (g.MeasureString(txt, font).Width <= maxW) break;
            size -= 2f;
        }
        font ??= new Font("Consolas", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
        using (font)
        using (var brush = new SolidBrush(Color.FromArgb(240, 240, 242)))
        using (var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        })
        {
            g.DrawString(txt, font, brush, new RectangleF(r.X + 24, r.Y, r.Width - 48, r.Height), sf);
        }
    }

    /// <summary>
    /// Dorso del cartucho (exactamente como `render_back` en Carthage):
    /// plástico moldeado, letras en relieve ("WINFORGE", "GAME CARD", código) y
    /// ventana hundida inferior (128, 690, 344×96) con 11 contactos dorados.
    /// </summary>
    /// <summary>
    /// PNG de una cara de MUESTRA del cartucho (fondo transparente), reducida a
    /// <paramref name="width"/> × <paramref name="height"/>. La usa el selector de estilo de
    /// Configuración → Apariencia: la MISMA cara que pinta la biblioteca 3D (cabecera del
    /// launcher, carátula y franja de código), con un título genérico para no mostrar arte de
    /// un juego concreto ni depender de archivos en disco.
    /// </summary>
    public static byte[] PaintSampleFacePng(int width, int height)
    {
        const string title = "GAME CARD";
        using var face = PaintFace(title, "WinForge", "WINFORGE · GAME CARD", artPath: null,
            PlasticColor(title, null), flat: false);
        int w = Math.Max(1, width), h = Math.Max(1, height);
        using var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(face, new Rectangle(0, 0, w, h));
        }
        using var png = new MemoryStream();
        scaled.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    public static Bitmap PaintBack(string launcher, string code, Color plastic, bool flat = true)
    {
        var bmp = new Bitmap(FaceWidth, FaceHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        DrawShellBody(g, plastic, flat);

        Color shellHi = Color.FromArgb(72, 255, 255, 255);
        // En plásticos claros (blanco, celeste muy liviano) el relieve aclarado no se leería:
        // el texto se pinta oscuro y el labio iluminado queda solo como brillo del molde.
        bool lightPlastic = 0.299f * plastic.R + 0.587f * plastic.G + 0.114f * plastic.B > 150;
        Color shellTop = lightPlastic ? Darken(plastic, 0.62f) : Lighten(plastic, 0.10f);

        // Letras moldeadas en relieve en el centro superior
        DrawRaisedText(g, "WINFORGE", 58f, FontStyle.Bold, FaceWidth * 0.5f, 320f, shellTop, shellHi);
        DrawRaisedText(g, "GAME CARD", 26f, FontStyle.Bold, FaceWidth * 0.5f, 385f, shellTop, shellHi);
        if (!string.IsNullOrWhiteSpace(code))
            DrawRaisedText(g, code.ToUpperInvariant(), 22f, FontStyle.Bold, FaceWidth * 0.5f, 595f, shellTop, shellHi, "Consolas");

        // Ventana hundida de los contactos en la parte inferior (128, 690, 344×96, r=14)
        const int WinX = 128, WinY = 690, WinW = 344, WinH = 96;
        var winRect = new Rectangle(WinX, WinY, WinW, WinH);
        using (var winPath = RoundPath(winRect, 14))
            DrawRecess(g, winPath, winRect, plastic, 5f, paintFloor: true);

        // 11 contactos dorados (width=20, gap=9.6, height=64)
        const int Pads = 11;
        const float PadW = 20f, Gap = 9.6f, PadH = 64f;
        float totalPadsW = Pads * PadW + (Pads - 1) * Gap;
        float x0 = (FaceWidth - totalPadsW) * 0.5f;
        float padY = WinY + 16f;

        Color goldTop = Color.FromArgb(246, 220, 142);
        Color goldMid = Color.FromArgb(212, 166, 66);
        Color goldBot = Color.FromArgb(156, 114, 36);

        for (int i = 0; i < Pads; i++)
        {
            float px = x0 + i * (PadW + Gap);
            var padRect = new Rectangle((int)MathF.Round(px), (int)MathF.Round(padY), (int)PadW, (int)PadH);
            using var padPath = RoundPath(padRect, 3);
            using (var goldGrad = new LinearGradientBrush(
                new PointF(padRect.Left, padRect.Top),
                new PointF(padRect.Right, padRect.Bottom),
                goldTop, goldBot))
            {
                var cb = new ColorBlend(3)
                {
                    Colors = new[] { goldTop, goldMid, goldBot },
                    Positions = new[] { 0f, 0.45f, 1f }
                };
                goldGrad.InterpolationColors = cb;
                g.FillPath(goldGrad, padPath);
            }
            using var hiPen = new Pen(Color.FromArgb(95, 255, 255, 255), 1.2f);
            g.DrawLine(hiPen, padRect.Left + 3, padRect.Top + 2, padRect.Right - 3, padRect.Top + 2);
        }

        return bmp;
    }

    private static void DrawRaisedText(
        Graphics g, string text, float pxSize, FontStyle style, float cx, float cy,
        Color bodyColor, Color hiColor, string family = "Segoe UI")
    {
        using var font = new Font(family, pxSize, style, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var hiBrush = new SolidBrush(hiColor))
            g.DrawString(text, font, hiBrush, new PointF(cx - 1.3f, cy - 1.3f), sf);
        using (var loBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.DrawString(text, font, loBrush, new PointF(cx + 1.6f, cy + 1.8f), sf);
        using (var mainBrush = new SolidBrush(Darken(bodyColor, 0.04f)))
            g.DrawString(text, font, mainBrush, new PointF(cx, cy), sf);
    }

    // ===== Auxiliares de dibujo =====

    private static void PaintGrain(Graphics g, Rectangle bounds)
    {
        var rnd = new Random(20261013);
        for (int i = 0; i < 1400; i++)
        {
            int x = bounds.X + rnd.Next(bounds.Width);
            int y = bounds.Y + rnd.Next(bounds.Height);
            int a = rnd.Next(5, 15);
            using var punto = new SolidBrush(rnd.Next(2) == 0
                ? Color.FromArgb(a, 255, 255, 255)
                : Color.FromArgb(a, 0, 0, 0));
            g.FillRectangle(punto, x, y, 2, 2);
        }
    }

    private static GraphicsPath RoundPath(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        p.AddArc(r.X, r.Y, d * 2, d * 2, 180, 90);
        p.AddArc(r.Right - d * 2, r.Y, d * 2, d * 2, 270, 90);
        p.AddArc(r.Right - d * 2, r.Bottom - d * 2, d * 2, d * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - d * 2, d * 2, d * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Bitmap? LoadArt(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var raw = new Bitmap(stream);
            return new Bitmap(raw);
        }
        catch { return null; }
    }

    private static Rectangle Cover(Size src, Rectangle dst)
    {
        float escala = Math.Max(dst.Width / (float)src.Width, dst.Height / (float)src.Height);
        int w = (int)MathF.Ceiling(src.Width * escala), h = (int)MathF.Ceiling(src.Height * escala);
        return new Rectangle(dst.X - (w - dst.Width) / 2, dst.Y - (h - dst.Height) / 2, w, h);
    }

    private static Rectangle FitInside(Size src, Rectangle dst)
    {
        float escala = Math.Min(dst.Width / (float)src.Width, dst.Height / (float)src.Height);
        int w = (int)MathF.Round(src.Width * escala), h = (int)MathF.Round(src.Height * escala);
        return new Rectangle(dst.X + (dst.Width - w) / 2, dst.Y + (dst.Height - h) / 2, w, h);
    }

    private static Bitmap Blurred(Bitmap src, int divisor)
    {
        int w = Math.Max(1, src.Width / divisor), h = Math.Max(1, src.Height / divisor);
        var chico = new Bitmap(w, h);
        using (var g = Graphics.FromImage(chico))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, w, h);
        }
        var grande = new Bitmap(src.Width, src.Height);
        using (var g = Graphics.FromImage(grande))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(chico, 0, 0, src.Width, src.Height);
        }
        chico.Dispose();
        return grande;
    }

    // ===== Auxiliares de color =====

    private static Color SampleAverage(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Color.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var src = new Bitmap(stream);
            using var uno = new Bitmap(1, 1);
            using (var g = Graphics.FromImage(uno))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, 1, 1);
            }
            var c = uno.GetPixel(0, 0);
            return Color.FromArgb(c.R, c.G, c.B);
        }
        catch { return Color.Empty; }
    }

    public static void RgbToHsl(Color c, out float h, out float s, out float l)
    {
        float r = c.R / 255f, gg = c.G / 255f, b = c.B / 255f;
        float max = MathF.Max(r, MathF.Max(gg, b)), min = MathF.Min(r, MathF.Min(gg, b));
        l = (max + min) * 0.5f;
        float d = max - min;
        if (d < 1e-6f) { h = 0; s = 0; return; }
        s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        if (max == r) h = 60f * (((gg - b) / d) % 6f);
        else if (max == gg) h = 60f * ((b - r) / d + 2f);
        else h = 60f * ((r - gg) / d + 4f);
        if (h < 0) h += 360f;
    }

    private static Color ColorFromHsl(float h, float s, float l)
    {
        float c = (1f - MathF.Abs(2f * l - 1f)) * s;
        float x = c * (1f - MathF.Abs((h / 60f % 2f) - 1f));
        float m = l - c * 0.5f;
        (float r, float gg, float b) = h switch
        {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x)
        };
        return Color.FromArgb(
            (int)Math.Clamp((r + m) * 255f, 0, 255),
            (int)Math.Clamp((gg + m) * 255f, 0, 255),
            (int)Math.Clamp((b + m) * 255f, 0, 255));
    }

    private static Color Lighten(Color c, float amount) => Color.FromArgb(
        (int)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255),
        (int)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255),
        (int)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255));

    private static Color Darken(Color c, float amount) => Color.FromArgb(
        (int)(c.R * (1f - amount)), (int)(c.G * (1f - amount)), (int)(c.B * (1f - amount)));

    private static Color ToAlpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Mezcla lineal de dos colores (<paramref name="t"/> = 0 → <paramref name="a"/>).</summary>
    private static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)Math.Clamp(a.R + (b.R - a.R) * t, 0, 255),
        (int)Math.Clamp(a.G + (b.G - a.G) * t, 0, 255),
        (int)Math.Clamp(a.B + (b.B - a.B) * t, 0, 255));
}
