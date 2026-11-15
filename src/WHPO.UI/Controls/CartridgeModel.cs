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
/// <summary>Tipo de hardware que representa la pieza: cartucho de videojuego, consola
/// vertical (emulador de consola) o teléfono (emulador de celular). Afecta la geometría
/// 3D, la silueta 2D y el pintado de ambas caras; el canvas y el pipeline son los mismos.</summary>
public enum DeviceKind
{
    Cartridge,
    Console,
    Phone,
    /// <summary>Consola retro: gabinete arcade de bar (perfil propio, ver
    /// CartridgeModel.Arcade.cs).</summary>
    Arcade
}

public static partial class CartridgeModel
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

    /// <summary>
    /// Profundidad REAL del pozo de la etiqueta, en unidades de ancho de cara (18 px del
    /// canvas de 600, ≈3% del ancho, como el hundido de una etiqueta en un cartucho de
    /// verdad). No es un dato de pintura: la malla hunde el fondo y modela las paredes, así
    /// que el espesor se ve al girar el cartucho. Tiene que quedar por debajo del
    /// semiespesor (23 px) para no atravesar el dorso.
    /// </summary>
    public const float PocketDepth = 18f / FaceWidth;

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
SamplerState samp : register(s0);    float4 PSMain(VSOut i) : SV_TARGET
{
    float3 n = normalize(i.Nrm);
    float3 clave = normalize(float3(-0.32f, 0.44f, 0.84f));
    float3 relleno = normalize(float3(0.38f, -0.42f, 0.62f));
    // El canal alfa del tinte dice qué clase de vértice es: 1 cara, 2 dorso, 0 canto del
    // cartucho y 3 pared del pozo. Con eso cada superficie elige su propia iluminación.
    float canal = i.Tint.a;
    float esFrente = saturate(1.0f - abs(canal - 1.0f));
    float esDorso = saturate(1.0f - abs(canal - 2.0f));
    float esPozo = saturate(1.0f - abs(canal - 3.0f));
    float esTextura = max(esFrente, esDorso);
    // Oclusión por vértice: 1 en la cara, menos adentro del pozo (la luz no llega igual).
    float ocl = i.Tint.r;
    // En las caras texturadas se preserva al 100% el color de la textura pintada, para que
    // el cartucho luzca idéntico a la card de la biblioteca. El canto del cartucho mantiene
    // su tono parejo y las paredes del pozo tienen un rango mucho más amplio: el giro del
    // modelo sobre las luces es lo que las hace cambiar de claro a oscuro.
    float luzCanto = 0.84f + 0.08f * saturate(dot(n, clave)) + 0.04f * saturate(dot(n, relleno));
    float luzPozo = 0.30f + 0.70f * saturate(dot(n, clave)) + 0.22f * saturate(dot(n, relleno));
    float4 arteFrente = face.Sample(samp, i.Uv);
    float4 arteDorso = back.Sample(samp, i.Uv);
    float4 arte = lerp(arteFrente, arteDorso, esDorso);
    float3 colorCanto = Plastic.rgb * lerp(luzCanto, luzPozo, esPozo) * lerp(1.0f, ocl, esPozo);
    float3 finalRgb = saturate(lerp(colorCanto, arte.rgb, esTextura));
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

        return WithNormals(pts);
    }

    /// <summary>Cuerpo del dispositivo dentro del canvas 600×878, en coordenadas de canvas
    /// (y hacia abajo): el teléfono es alto y angosto con proporción real de celular
    /// (19.5:9 ≈ 404×878, centrado horizontal) y la consola es una caja ancha y baja
    /// tipo PS1 (560×420, centrada verticalmente). Lo comparten la malla 3D, el pintado
    /// de caras y la silueta 2D de la tile, así los tres ven la misma pieza.</summary>
    internal static (float X, float Y, float W, float H, float R) DeviceBody(DeviceKind kind) => kind switch
    {
        DeviceKind.Phone => (70f, 0f, 460f, 878f, 48f),
        // Gabinete arcade: alto y angosto, centrado en el canvas.
        DeviceKind.Arcade => (110f, 30f, 380f, 820f, 6f),
        // Caja ancha con las proporciones reales de una PS1 (27,6×18,5 cm ≈ 1,49):
        // 580×390, centrada en y=439 como el canvas.
        _ => (10f, 244f, 580f, 390f, 18f)
    };

    // ===== Layout de la consola (canvas 600x878) =====
    // Caja 10..590 x 244..634 (27,6x18,5 cm de la PS1, 1,49:1) y tapa circular de
    // radio 168 centrada en (300, 413): casi toda la profundidad, como en la real,
    // con dos alas de 122 px para los botones. La franja 582..634 es la cara
    // FRONTAL, que la malla mapea al canto inferior.
    internal const float ConsoleSeamY = 582f;
    internal const float ConsoleLidCx = 300f;
    internal const float ConsoleLidCy = 413f;
    internal static float ConsoleLidR => ConsoleModel.Current.LidRadius;
    /// <summary>Relieve de la tapa en la malla (unidades de ancho de cara).</summary>
    /// <summary>Relieve de la tapa en la malla, por modelo de consola.</summary>
    internal static float ConsoleLidRaise => ConsoleModel.Current.LidRaise;


    /// <summary>Color de la carcasa del dispositivo. La consola es gris claro como la
    /// PS1 real (tinteado con el plástico del tema), el teléfono usa el tema tal cual.</summary>
    internal static Color DeviceShellColor(DeviceKind kind, Color plastic) => kind switch
    {
        DeviceKind.Console => Mix(plastic, Color.FromArgb(208, 206, 198), 0.72f),
        // El mueble arcade es negro/grafito aunque el tema sea claro: los cantos y las
        // molduras del gabinete no siguen el color de la carcasa del cartucho.
        DeviceKind.Arcade => Mix(plastic, Color.FromArgb(22, 23, 27), 0.86f),
        _ => plastic
    };

    /// <summary>El código de la etiqueta incluye el launcher ("INDEPENDIENTE - B5"
    /// para juegos standalone). En hardware ese prefijo no dice nada: se muestra la
    /// marca del programa. Solo afecta a dispositivos, no a los cartuchos.</summary>
    private static string DeviceCode(string code)
    {
        const string independiente = "Independiente";
        if (!string.IsNullOrWhiteSpace(code) && code.StartsWith(independiente, StringComparison.OrdinalIgnoreCase))
            return "WinForge" + code.Substring(independiente.Length);
        return code;
    }

    /// <summary>Contorno de un dispositivo (consola o teléfono): losa redondeada SIN muescas
    /// laterales ni pozo de etiqueta — la pieza es hardware, no un cartucho.</summary>
    private static List<(Vector2 P, Vector2 N)> BuildDeviceOutline(DeviceKind kind)
    {
        var (x, y, w, h, r) = DeviceBody(kind);
        var pts = BuildRoundedRectOutline(x, y, x + w, y + h, r, porArco: 10);
        // La consola se densifica: la tapa se proyecta desde el contorno, y con solo los
        // vértices de las esquinas el disco salía con el borde RECTO arriba y abajo (dos
        // vértices para todo un lado del cuerpo). Los puntos extra caen sobre las mismas
        // rectas, así que la silueta y las normales no cambian.
        if (kind == DeviceKind.Console) pts = Densify(pts, 0.035f);
        return WithNormals(pts);
    }

    /// <summary>Agrega puntos intermedios en los tramos más largos que
    /// <paramref name="maxLength"/> (unidades de ancho de cara).</summary>
    private static List<Vector2> Densify(List<Vector2> pts, float maxLength)
    {
        var dense = new List<Vector2>(pts.Count * 2);
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            dense.Add(a);
            float len = Vector2.Distance(a, b);
            int steps = Math.Max(0, (int)MathF.Ceiling(len / maxLength) - 1);
            for (int k = 1; k <= steps; k++)
                dense.Add(Vector2.Lerp(a, b, k / (float)(steps + 1)));
        }
        return dense;
    }

    /// <summary>Espesor (mitad, en coordenadas de malla) del dispositivo. La consola
    /// clásica mide 0.055 por lado = 0.11 total: sobre la profundidad del cuerpo (0.65)
    /// es el 16,9 %, el mismo frente que muestra la card de la biblioteca (franja de
    /// 52 px + labio de 12 px sobre 390) y que la PS1 real (3,0 cm de 18,5 cm ≈ 16 %).
    /// El modelo elegido en Apariencia ajusta ese grosor. El cartucho usa la mitad de
    /// <see cref="Thickness"/>.</summary>
    internal static float DeviceThickness(DeviceKind kind) => kind switch
    {
        DeviceKind.Console => ConsoleModel.Current.Thickness,
        // El mueble va de -150 a +185 px de profundidad: la media caja lo cubre.
        DeviceKind.Arcade => 205f / FaceWidth,
        _ => Thickness * 0.5f
    };

    /// <summary>Vista del dispositivo dentro del canvas 600×878 para la GRILLA: el cuerpo
    /// (DeviceBody) más un poco de aire, recortado al canvas. La consola es una caja
    /// ancha dentro de un canvas vertical: sin recorte la tile la pinta al ~44% del
    /// alto y se ve chica. La imagen de grilla y la silueta 2D usan este mismo rectángulo
    /// para que coincidan siempre.</summary>
    /// <summary>Inclinación de reposo de la cámara por tipo de pieza. La consola se mira
    /// desde arriba (se ven la tapa y los puertos), el teléfono apenas inclinado (es
    /// alto y de frente se lee mejor), el gabinete arcade derecho y el cartucho casi de
    /// frente. La usan la card cacheada, el control 3D y el selector de modelos.</summary>
    internal static float RestPitch(DeviceKind kind) => kind switch
    {
        DeviceKind.Cartridge => -2.5f,
        DeviceKind.Arcade => 0f,
        DeviceKind.Phone => -10f,
        _ => -30f
    };

    internal static (float X, float Y, float W, float H) DeviceViewRect(DeviceKind kind)
    {
        var (bx, by, bw, bh, _) = DeviceBody(kind);
        const float pad = 14f;
        float x0 = Math.Clamp(bx - pad, 0f, FaceWidth), x1 = Math.Clamp(bx + bw + pad, 0f, FaceWidth);
        float y0 = Math.Clamp(by - pad, 0f, FaceHeight), y1 = Math.Clamp(by + bh + pad, 0f, FaceHeight);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private static List<(Vector2 P, Vector2 N)> WithNormals(List<Vector2> pts)
    {
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

    /// <summary>
    /// Rectángulo redondeado del canvas 600×878 pasado a coordenadas de la malla (x a la
    /// derecha, y arriba). El recorrido sigue el del canvas: no importa el sentido, porque
    /// las normales de las paredes salen del área con signo.
    /// </summary>
    private static List<Vector2> BuildRoundedRectOutline(float cx0, float cy0, float cx1, float cy1, float radio, int porArco = 6)
    {
        var pts = new List<Vector2>();
        void Arco(float cx, float cy, float desde, float hasta)
        {
            for (int i = 0; i <= porArco; i++)
            {
                float a = (desde + (hasta - desde) * i / porArco) * MathF.PI / 180f;
                pts.Add(ToModel(cx + radio * MathF.Cos(a), cy + radio * MathF.Sin(a)));
            }
        }

        Arco(cx0 + radio, cy0 + radio, 180f, 270f);
        Arco(cx1 - radio, cy0 + radio, 270f, 360f);
        Arco(cx1 - radio, cy1 - radio, 0f, 90f);
        Arco(cx0 + radio, cy1 - radio, 90f, 180f);
        return pts;
    }

    /// <summary>Punto del canvas (600×878, y hacia abajo) a coordenadas de la malla (y arriba).</summary>
    private static Vector2 ToModel(float cx, float cy) =>
        new((cx - FaceWidth * 0.5f) / FaceWidth, (FaceHeight * 0.5f - cy) / FaceWidth);

    /// <summary>Contorno del pozo de la etiqueta en coordenadas de la malla.</summary>
    private static List<Vector2> BuildPocketOutline() =>
        BuildRoundedRectOutline(StickerX, StickerY, StickerX + StickerW, StickerY + StickerH, StickerR);

    /// <summary>
    /// Punto donde el rayo que sale del origen corta el contorno cerrado <paramref name="polygon"/>.
    /// El origen se asume adentro del contorno, así que se toma el corte más lejano.
    /// </summary>
    private static Vector2 RayToPolygon(Vector2 dir, List<Vector2> polygon)
    {
        float mejor = -1f;
        var punto = dir;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            float ex = b.X - a.X, ey = b.Y - a.Y;
            float det = -dir.X * ey + ex * dir.Y;
            if (MathF.Abs(det) < 1e-9f) continue;
            float s = (-a.X * ey + ex * a.Y) / det;
            float u = (dir.X * a.Y - dir.Y * a.X) / det;
            if (s > 0f && u >= -0.001f && u <= 1.001f && s > mejor)
            {
                mejor = s;
                punto = dir * s;
            }
        }
        return punto;
    }

    public static (Vertex[] Vertices, ushort[] Indices) BuildMesh(DeviceKind kind = DeviceKind.Cartridge)
    {
        // El gabinete arcade no es una losa: tiene perfil propio (marquesina, bisel,
        // panel salido) y su propia malla.
        if (kind == DeviceKind.Arcade) return BuildArcadeMesh();
        var outline = kind == DeviceKind.Cartridge ? BuildOutline() : BuildDeviceOutline(kind);
        int n = outline.Count;
        // La consola es más gruesa que el cartucho: en la PS1 real el frente es una
        // cara alta (foto frontal) y al inclinarla se ve como losa, no como papel.
        // La consola/teléfono NO llevan el bisel del cartucho: ese anillo de plástico
        // entre la silueta y la cara se leía como un SEGUNDO borde alrededor de la
        // tapa (más notorio arriba, con el modelo inclinado). Sin bisel la cara llega
        // al contorno y el canto queda como una sola línea limpia.
        float t = DeviceThickness(kind), r = kind == DeviceKind.Cartridge ? BevelRadius : 0f;
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
        if (r > 0f)
        {
            for (int k = 0; k <= AnillosPerfil; k++)
            {
                frente.Add(Perfil(MathF.PI * 0.5f * (AnillosPerfil - k) / AnillosPerfil, false));
                dorso.Add(Perfil(MathF.PI * 0.5f * k / AnillosPerfil, true));
            }
        }
        else
        {
            // Consola y teléfono sin bisel: UN solo anillo por cara. Con radio 0 los
            // cinco anillos del perfil caían en el mismo plano y los cuatro ciegos
            // (tinte de plástico) peleaban con el texturado: el sombreado ganaba en
            // parches y se veían manchas oscuras sobre la tapa y las alas.
            frente.Add(AddRing(0f, t, 1f, 0f, false));
            dorso.Add(AddRing(0f, -t, -1f, 0f, true));
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
        // La pared perimetral de la losa (dorso[^1] → frente[0]). En consola se
        // sustituye por anillos propios con la franja frontal mapeada (ver abajo).
        if (kind != DeviceKind.Console)
        {
            if (kind == DeviceKind.Phone)
            {
                // El canto del teléfono es PLÁSTICO. Con el bandeo genérico el borde
                // trasero trae la UV espejada, así que la pared interpolaba de 1-u a u
                // y barría TODA la textura a lo ancho del espesor: se veía el texto y el
                // logo de la cara estirados sobre el canto. Con copias en canal plástico
                // (Tint.W = 0) el canto se sombrea por normal, sin muestrear la textura.
                var rimF = new ushort[n];
                var rimB = new ushort[n];
                for (int i = 0; i < n; i++)
                {
                    var a = verts[frente[0][i]];
                    var b = verts[dorso[^1][i]];
                    rimF[i] = (ushort)verts.Count;
                    verts.Add(new Vertex(a.Position, a.Normal, a.Uv,
                        new Vector4(a.Tint.X, a.Tint.Y, a.Tint.Z, 0f)));
                    rimB[i] = (ushort)verts.Count;
                    verts.Add(new Vertex(b.Position, b.Normal, a.Uv,
                        new Vector4(b.Tint.X, b.Tint.Y, b.Tint.Z, 0f)));
                }
                Banda(rimB, rimF);
            }
            else
            {
                Banda(dorso[^1], frente[0]);
            }
        }

        // Consola: la PARED FRONTAL de la losa (borde inferior del contorno) muestrea
        // la franja frontal de la textura (puertos de mando + MEMORY CARD, filas
        // 640..692) para que al inclinar el modelo las entradas estén en el LATERAL
        // FRONTAL y no sobre la tapa. Dos recaudos que ya mordieron:
        // - Los anillos de la pared son COPIAS (no los frente[0]/dorso[^1] originales):
        //   esos vértices también son el anillo exterior del bisel, y remapearlos
        //   pintaba la franja sobre el canto (botones/puertos impresos en el borde)
        //   y la textura frontal sobre el bisel trasero al girar el modelo. Con
        //   copias, el bisel queda plástico como antes y solo la pared lleva la
        //   franja.
        // - El peso `w` se calcula con la geometría (la junta está en la fila 582 del
        //   plan): así la franja solo cubre el frente y no las esquinas de la pared.
        //   La franja se pinta FUERA del cuerpo (640..692) para que la tapa no la
        //   muestree al mapear su cara.
        if (kind == DeviceKind.Console)
        {
            // La franja se pinta aparte, debajo del cuerpo (640..692): si se pintara
            // encima de la tapa, el recorte de UV metía la textura del faldón dentro
            // de la cara superior y los botones salían estirados.
            float vTop = 640f * 866f / 770884f; // junta frontal → v
            float vBot = 692f * 866f / 770884f; // filo inferior → v
            // Segunda franja (700..752) para el canto TRASERO: los conectores de la foto
            // van en la pared de atrás, no sobre la cara inferior (que es la textura del
            // dorso). Cada pared elige su franja según la fila del contorno.
            float vAtrasTop = 700f * 866f / 770884f;
            float vAtrasBot = 752f * 866f / 770884f;
            var rTop = frente[0];
            var rBot = dorso[^1];
            var paredTop = new ushort[n];
            var paredBot = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                float cy = 439f - verts[rTop[i]].Position.Y * 600f; // fila canvas
                float wFrente = Math.Clamp((cy - 582f) / 52f, 0f, 1f);
                float wAtras = Math.Clamp((272f - cy) / 28f, 0f, 1f);
                float w = Math.Max(wFrente, wAtras);
                bool paredAtras = wAtras > 0f;
                float vT = paredAtras ? vAtrasTop : vTop;
                float vB = paredAtras ? vAtrasBot : vBot;
                var vt = verts[rTop[i]];
                paredTop[i] = (ushort)verts.Count;
                verts.Add(new Vertex(vt.Position, vt.Normal,
                    new Vector2(vt.Uv.X, vt.Uv.Y + (vT - vt.Uv.Y) * w),
                    new Vector4(vt.Tint.X, vt.Tint.Y, vt.Tint.Z, vt.Tint.W + (1f - vt.Tint.W) * w)));
                var vb = verts[rBot[i]];
                // vb trae uv.X espejado (es del dorso): se desespeja para alinear con vt.
                paredBot[i] = (ushort)verts.Count;
                verts.Add(new Vertex(vb.Position, vb.Normal,
                    new Vector2(1f - vb.Uv.X, vb.Uv.Y + (vB - vb.Uv.Y) * w),
                    new Vector4(vb.Tint.X, vb.Tint.Y, vb.Tint.Z, vb.Tint.W + (1f - vb.Tint.W) * w)));
            }
            Banda(paredBot, paredTop);
        }

        // Consola: la tapa del disco SOBRESALE del cuerpo. En la PS1 la tapa tiene su
        // propio relieve y al mirar la consola en ángulo se ve el escalón; una cara
        // plana con la sombra pintada se lee como una calcomanía. El anillo exterior
        // comparte los vértices del contorno (sin costuras) y la tapa se levanta
        // ConsoleLidRaise con su pared: así el brillo del canto cambia al girar.
        void AddConsoleTop(ushort[] bordeCara)
        {
            float lcx = (ConsoleLidCx - FaceWidth * 0.5f) / FaceWidth;
            float lcy = (FaceHeight * 0.5f - ConsoleLidCy) / FaceWidth;
            float lr = ConsoleLidR / FaceWidth;
            var centroTapa = new Vector2(lcx, lcy);
            int n = bordeCara.Length;

            var baseTapa = new ushort[n];
            var topeTapa = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                var p = verts[bordeCara[i]].Position;
                var dir = new Vector2(p.X, p.Y) - centroTapa;
                dir = dir.LengthSquared() < 1e-9f ? Vector2.UnitX : Vector2.Normalize(dir);
                var xy = centroTapa + dir * lr;

                baseTapa[i] = (ushort)verts.Count;
                verts.Add(new Vertex(new Vector3(xy.X, xy.Y, t), Vector3.UnitZ,
                    UvFrente(xy), new Vector4(1f, 1f, 1f, 1f)));
                topeTapa[i] = (ushort)verts.Count;
                verts.Add(new Vertex(new Vector3(xy.X, xy.Y, t + ConsoleLidRaise), Vector3.UnitZ,
                    UvFrente(xy), new Vector4(1f, 1f, 1f, 1f)));
            }

            // Anillo plano entre el contorno del cuerpo y la base de la tapa.
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                idx.Add(bordeCara[i]); idx.Add(bordeCara[j]); idx.Add(baseTapa[j]);
                idx.Add(bordeCara[i]); idx.Add(baseTapa[j]); idx.Add(baseTapa[i]);
            }

            // Pared de la tapa: plástico (canal 0), normal radial hacia afuera.
            var paredAbajo = new ushort[n];
            var paredArriba = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                var p = verts[topeTapa[i]].Position;
                var dir = new Vector2(p.X - lcx, p.Y - lcy);
                dir = dir.LengthSquared() < 1e-9f ? Vector2.UnitX : Vector2.Normalize(dir);
                var nrm = new Vector3(dir.X, dir.Y, 0f);
                var xy = new Vector2(p.X, p.Y);
                paredAbajo[i] = (ushort)verts.Count;
                verts.Add(new Vertex(new Vector3(p.X, p.Y, t), nrm,
                    UvFrente(xy), new Vector4(1f, 1f, 1f, 0f)));
                paredArriba[i] = (ushort)verts.Count;
                verts.Add(new Vertex(new Vector3(p.X, p.Y, t + ConsoleLidRaise), nrm,
                    UvFrente(xy), new Vector4(1f, 1f, 1f, 0f)));
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                idx.Add(paredAbajo[i]); idx.Add(paredArriba[j]); idx.Add(paredArriba[i]);
                idx.Add(paredAbajo[i]); idx.Add(paredAbajo[j]); idx.Add(paredArriba[j]);
            }

            // Tapa: abanico con la textura, para que la serigrafía quede donde se pintó.
            ushort centro = (ushort)verts.Count;
            verts.Add(new Vertex(new Vector3(lcx, lcy, t + ConsoleLidRaise), Vector3.UnitZ,
                UvFrente(centroTapa), new Vector4(1f, 1f, 1f, 1f)));
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                idx.Add(centro); idx.Add(topeTapa[i]); idx.Add(topeTapa[j]);
            }
        }

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
        // Pozo real de la etiqueta. La cara ya no es una tapa plana: del borde del bisel
        // sale un anillo plano hasta el labio del pozo, de ahí bajan las paredes y el fondo
        // queda hundido PocketDepth. Es lo único que da profundidad de verdad: al girar el
        // cartucho aparece el espesor de la pared, cosa que ningún sombreado pintado puede
        // falsear.
        void AddPocket(ushort[] bordeCara)
        {
            var contorno = BuildPocketOutline();
            int n = bordeCara.Length;

            // Labio: cada vértice del contorno se proyecta hasta el rectángulo de la
            // etiqueta. Así el anillo de la cara y la pared comparten exactamente los
            // mismos puntos y no queda ninguna costura entre los dos.
            var labioXY = new Vector2[n];
            var labio = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                var p = verts[bordeCara[i]].Position;
                var dir = new Vector2(p.X, p.Y);
                if (dir.LengthSquared() < 1e-9f) dir = Vector2.UnitX;
                labioXY[i] = RayToPolygon(Vector2.Normalize(dir), contorno);
                labio[i] = (ushort)verts.Count;
                verts.Add(new Vertex(
                    new Vector3(labioXY[i].X, labioXY[i].Y, t),
                    Vector3.UnitZ,
                    UvFrente(labioXY[i]),
                    new Vector4(1f, 1f, 1f, 1f)));
            }

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                idx.Add(labio[i]); idx.Add(bordeCara[i]); idx.Add(bordeCara[j]);
                idx.Add(labio[i]); idx.Add(bordeCara[j]); idx.Add(labio[j]);
            }

            // Normal hacia adentro del pozo, que es el lado desde el que se ven las paredes.
            // Se deduce del área con signo para no depender del sentido de recorrido.
            float area = 0f;
            for (int i = 0; i < n; i++)
            {
                var a = labioXY[i];
                var b = labioXY[(i + 1) % n];
                area += a.X * b.Y - b.X * a.Y;
            }
            float signo = area >= 0f ? 1f : -1f;

            float fondo = t - PocketDepth;
            var arriba = new ushort[n];
            var abajo = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                var tan = labioXY[(i + 1) % n] - labioXY[(i - 1 + n) % n];
                var nrm = new Vector3(-tan.Y * signo, tan.X * signo, 0f);
                nrm = nrm.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(nrm);
                arriba[i] = (ushort)verts.Count;
                verts.Add(new Vertex(new Vector3(labioXY[i].X, labioXY[i].Y, t), nrm,
                    UvFrente(labioXY[i]), new Vector4(0.90f, 1f, 1f, 3f)));
                abajo[i] = (ushort)verts.Count;
                verts.Add(new Vertex(new Vector3(labioXY[i].X, labioXY[i].Y, fondo), nrm,
                    UvFrente(labioXY[i]), new Vector4(0.55f, 1f, 1f, 3f)));
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                idx.Add(arriba[i]); idx.Add(abajo[i]); idx.Add(abajo[j]);
                idx.Add(arriba[i]); idx.Add(abajo[j]); idx.Add(arriba[j]);
            }

            // Fondo: la etiqueta misma, mapeada con las mismas UV que el labio. La oclusión
            // ambiental ya viene pintada en la textura, así que acá no hay que inventar nada.
            //
            // Necesita su PROPIO anillo: los vértices de la pared llevan canal 3 y, al
            // interpolarse sobre el fondo, el alfa pasaba por 2 en el medio de cada
            // triángulo y el shader muestreaba la textura del DORSO — se veían los contactos
            // dorados a través de la etiqueta.
            ushort centroPozo = (ushort)verts.Count;
            verts.Add(new Vertex(new Vector3(0f, 0f, fondo), Vector3.UnitZ,
                UvFrente(Vector2.Zero), new Vector4(1f, 1f, 1f, 1f)));
            var fondoAnillo = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                fondoAnillo[i] = (ushort)verts.Count;
                verts.Add(new Vertex(new Vector3(labioXY[i].X, labioXY[i].Y, fondo), Vector3.UnitZ,
                    UvFrente(labioXY[i]), new Vector4(1f, 1f, 1f, 1f)));
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                idx.Add(centroPozo); idx.Add(fondoAnillo[i]); idx.Add(fondoAnillo[j]);
            }
        }

        // La pestaña y la flecha NO llevan geometría: van hundidas en la pintura. Hacerlas
        // sobresalir de la cara las volvía una placa pegada al frente —y la pintura ya
        // traía su propio sombreado, así que el detalle se veía doble—; además iban hacia
        // afuera cuando el resto del cartucho está moldeado hacia adentro.
        // El pozo de la etiqueta es solo del cartucho: consola/teléfono pintan la cara
        // completa del dispositivo (la ventana es parte del dibujo, no un hueco).
        if (kind == DeviceKind.Cartridge)
            AddPocket(frente[^1]);
        else if (kind == DeviceKind.Console)
            AddConsoleTop(frente[^1]);
        else
            AddCara(frente[^1], t, false);
        AddCara(dorso[0], -t, true);

        return (verts.ToArray(), idx.ToArray());
    }

    // ===== Dispositivos: consola vertical y teléfono =====
    // Canvas, UV y pipeline idénticos al cartucho; solo cambian la losa redondeada (sin
    // muescas ni pozo) y el dibujo de ambas caras.

    /// <summary>Carcasa de consola/teléfono: la misma losa redondeada que la malla (canon
    /// 600×878). Con flat=false (grilla 2D) deja transparente el exterior y pinta la pared
    /// inferior de ShellThickness2D, igual que DrawShellBody pero sin muescas.</summary>
    private static void DrawDeviceShell(Graphics g, DeviceKind kind, Color plastic, bool flat)
    {
        var (x, y, w, h, radio) = DeviceBody(kind);
        Color shell = DeviceShellColor(kind, plastic);
        if (flat)
        {
            // Textura 3D: la malla solo mapea el cuerpo; el resto del canvas queda en
            // plástico (nunca se muestrea) y el pintado del dispositivo va encima.
            using var flatBrush = new SolidBrush(shell);
            g.FillRectangle(flatBrush, 0, 0, FaceWidth, FaceHeight);
            return;
        }

        g.Clear(Color.Transparent);
        // Pared inferior (visible en la grilla): el cuerpo desplazado ShellThickness2D px.
        using (var wallPath = RoundPath(new Rectangle((int)x, (int)(y + ShellThickness2D), (int)w, (int)h), (int)radio))
        using (var wallBrush = new SolidBrush(Darken(shell, 0.14f)))
            g.FillPath(wallBrush, wallPath);
        using var facePath = RoundPath(new Rectangle((int)x, (int)y, (int)w, (int)h), (int)radio);
        using var faceBrush = new SolidBrush(shell);
        g.FillPath(faceBrush, facePath);
    }

    /// <summary>Tamaño de fuente (inicio en <paramref name="inicio"/>) que hace caber el
    /// texto en <paramref name="anchoMax"/> px con Segoe UI; nunca baja de 10.</summary>
    private static float FitFontSize(Graphics g, string texto, float inicio, FontStyle estilo, float anchoMax)
    {
        if (string.IsNullOrEmpty(texto)) return inicio;
        using var f0 = new Font("Segoe UI", inicio, estilo);
        float wdt = g.MeasureString(texto, f0).Width;
        if (wdt <= anchoMax) return inicio;
        return Math.Max(10f, inicio * anchoMax / wdt);
    }

    private static void PaintDeviceFace(Graphics g, DeviceKind kind, string title, string launcher, string code, string? artPath, Color plastic, bool flat, GameHeader? header)
    {
        if (kind == DeviceKind.Arcade)
        {
            PaintArcadeFace(g, title, launcher, code, artPath, plastic, header);
            return;
        }
        DrawDeviceShell(g, kind, plastic, flat);
        if (kind == DeviceKind.Phone)
        {
            // Teléfono real: cuerpo centrado (460×878). Bisel negro con cámara
            // perforada arriba; la pantalla ocupa casi todo el cuerpo.
            using (var bezelPath = RoundPath(new Rectangle(84, 14, 432, 850), 40))
            using (var bezelBrush = new SolidBrush(Color.FromArgb(11, 11, 14)))
                g.FillPath(bezelBrush, bezelPath);
            g.FillEllipse(Brushes.Black, 300 - 10, 40 - 10, 20, 20);
            g.FillEllipse(new SolidBrush(Color.FromArgb(26, 42, 74)), 300 - 4, 40 - 4, 8, 8);

            PaintArtWindow(g, new Rectangle(94, 64, 412, 678), title, artPath, plastic);

            var strip = new Rectangle(94, 760, 412, 64);
            using (var stripPath = RoundPath(strip, 10))
            using (var stripBrush = new SolidBrush(Color.FromArgb(20, 20, 24)))
                g.FillPath(stripBrush, stripPath);
            PaintCodeStrip(g, strip, DeviceCode(code));
            using (var homePath = RoundPath(new Rectangle(250, 844, 100, 7), 3))
            using (var homeBrush = new SolidBrush(Color.FromArgb(60, 60, 68)))
                g.FillPath(homeBrush, homePath);
            return;
        }

        // ===== Consola: vista superior (tapa) + cara frontal =====
        // Rehecha sobre las proporciones de la PS1: caja ancha, tapa circular que
        // ocupa casi toda la profundidad, alas laterales con los botones y un faldón
        // frontal con las entradas. Todo el relieve es pintado (la malla aporta el
        // espesor, el canto y el escalón de la tapa): la luz viene de arriba-izquierda,
        // así que cada pieza lleva su sombra abajo-derecha y su filo claro del otro lado.
        PaintConsoleFace(g, title, launcher, artPath, plastic);
    }

    /// <summary>Ícono impreso en los botones de la consola.</summary>
    private enum ConsoleGlyph { None, Power, Eject }

    /// <summary>
    /// Cara superior de la consola (canvas 600×878). Es la vista de la foto: cuerpo
    /// 10..590 × 244..634, tapa circular de radio 168 centrada en (300, 413) —casi toda
    /// la profundidad, como en la PS1 real—, alas de 122 px con RESET/POWER y OPEN, y la
    /// franja 582..634 que la malla mapea al canto frontal (ranuras y puertos).
    /// </summary>
    private static void PaintConsoleFace(Graphics g, string title, string launcher, string? artPath, Color plastic)
    {
        var (bx, by, bw, bh, br) = DeviceBody(DeviceKind.Console);
        float x1 = bx + bw, y1 = by + bh;
        Color shell = DeviceShellColor(DeviceKind.Console, plastic);
        Color ink = Darken(shell, 0.50f);
        Color hi = Mix(shell, Color.White, 0.74f);
        const float Seam = ConsoleSeamY;
        const float Lx = ConsoleLidCx, Ly = ConsoleLidCy;
        // El radio y el relieve vienen del modelo de consola elegido (Apariencia).
        float Lr = ConsoleLidR;

        using var bodyPath = RoundPath(new Rectangle((int)bx, (int)by, (int)bw, (int)bh), (int)br);

        // 1. Cuerpo: degradado diagonal suave. El gris de la PS1 no es blanco; si el
        //    claro se pasa, la consola deja de leerse como plástico.
        using (var baseBrush = new LinearGradientBrush(
            new PointF(bx, by), new PointF(x1, y1),
            Mix(shell, Color.White, 0.15f), Mix(shell, Color.Black, 0.13f)))
            g.FillPath(baseBrush, bodyPath);

        // 2. Oclusión del canto (la losa no lleva bisel): tres trazos cada vez más
        //    anchos y tenues, recortados a la silueta, para que el filo se apague.
        var edgeClip = g.Save();
        g.SetClip(bodyPath, CombineMode.Intersect);
        for (int i = 0; i < 3; i++)
        {
            using var edge = new Pen(Color.FromArgb(30 - i * 8, 0, 0, 0), 2.4f + i * 6f);
            g.DrawPath(edge, bodyPath);
        }
        g.Restore(edgeClip);

        // 3. Juntas de las alas: la sección de la tapa es una pieza aparte, con una
        //    ranura a cada lado, tangente al disco como en la consola real.
        using (var groove = new Pen(Color.FromArgb(56, 0, 0, 0), 2.2f))
        using (var grooveHi = new Pen(Color.FromArgb(32, 255, 255, 255), 1f))
        using (var grooveLo = new Pen(Color.FromArgb(24, 0, 0, 0), 1.2f))
        {
            foreach (float sx in new[] { Lx - Lr, Lx + Lr })
            {
                g.DrawLine(groove, sx, by + 12, sx, Seam - 5);
                g.DrawLine(grooveHi, sx - 2.2f, by + 14, sx - 2.2f, Seam - 7);
                g.DrawLine(grooveLo, sx + 2.2f, by + 14, sx + 2.2f, Seam - 7);
            }
        }

        // 4. Tapa circular: canal del molde, domo apenas convexo, junta concéntrica del
        //    disco y la uña de apertura con sus tres marcas a las 4:30.
        var lid = new RectangleF(Lx - Lr, Ly - Lr, Lr * 2, Lr * 2);
        foreach (var (grow, ancho, alfa) in new[] { (4.5f, 6f, 16), (2.4f, 3f, 26), (0.5f, 1.6f, 54) })
        {
            using var canal = new Pen(Color.FromArgb(alfa, 0, 0, 0), ancho);
            g.DrawEllipse(canal, lid.X - grow, lid.Y - grow, lid.Width + grow * 2, lid.Height + grow * 2);
        }

        using (var lidPath = new GraphicsPath())
        {
            lidPath.AddEllipse(lid);
            using (var lidBrush = new LinearGradientBrush(
                new PointF(Lx - Lr * 0.55f, Ly - Lr * 0.85f),
                new PointF(Lx + Lr * 0.80f, Ly + Lr * 0.95f),
                Mix(shell, Color.White, 0.175f), Mix(shell, Color.Black, 0.055f)))
                g.FillPath(lidBrush, lidPath);

            var lidClip = g.Save();
            g.SetClip(lidPath, CombineMode.Intersect);
            using (var rimHi = new Pen(Color.FromArgb(62, 255, 255, 255), 2.4f))
                g.DrawArc(rimHi, lid.X + 2, lid.Y + 2, lid.Width - 4, lid.Height - 4, 186f, 132f);
            using (var rimLo = new Pen(Color.FromArgb(26, 0, 0, 0), 2.6f))
                g.DrawArc(rimLo, lid.X + 2, lid.Y + 2, lid.Width - 4, lid.Height - 4, 8f, 152f);
            using (var ringLo = new Pen(Color.FromArgb(30, 0, 0, 0), 1.6f))
                g.DrawEllipse(ringLo, Lx - Lr * 0.94f, Ly - Lr * 0.94f, Lr * 1.88f, Lr * 1.88f);
            using (var ringHi = new Pen(Color.FromArgb(38, 255, 255, 255), 1.1f))
                g.DrawEllipse(ringHi, Lx - Lr * 0.94f, Ly - Lr * 0.94f + 1.5f, Lr * 1.88f, Lr * 1.88f);
            g.Restore(lidClip);
        }

        float grip = 44f * MathF.PI / 180f;
        float gx = MathF.Cos(grip), gy = MathF.Sin(grip);
        var notchA = new PointF(Lx + gx * (Lr - 18), Ly + gy * (Lr - 18));
        var notchB = new PointF(Lx + gx * (Lr - 2), Ly + gy * (Lr - 2));
        using (var notch = new Pen(Color.FromArgb(72, 0, 0, 0), 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(notch, notchA, notchB);
        using (var notchHi = new Pen(Color.FromArgb(55, 255, 255, 255), 1.2f))
            g.DrawLine(notchHi, notchA.X - 1.5f, notchA.Y + 1.6f, notchB.X - 1.5f, notchB.Y + 1.6f);

        // 5. Serigrafía de la tapa: marca arriba (donde la PS1 dice SONY), el ÍCONO del
        //    emulador al centro y el nombre debajo, como el logotipo de la consola.
        // La serigrafía se escala con la tapa: en los modelos de tapa chica el logo y el
        // nombre tienen que entrar dentro del disco igual que en la clásica.
        float ls = Lr / 162f;
        var serigrafia = g.Save();
        g.TranslateTransform(Lx, Ly);
        g.ScaleTransform(ls, ls);
        g.TranslateTransform(-Lx, -Ly);
        DrawTrackedText(g, "WINFORGE", 19f, FontStyle.Bold, Lx, Ly - 88f, 4.6f, Color.FromArgb(225, ink), hi, 1.2f);
        // El logo de la tapa es el ícono del emulador (el mismo arte que usa la card),
        // no una marca fija: así cada emulador se reconoce por su propio logo. Sin arte
        // válido se cae al emblema propio.
        if (!PaintConsoleLogo(g, Lx, Ly + 10f, artPath))
            PaintConsoleMark(g, Lx, Ly + 20f, shell);
        string shown = string.IsNullOrWhiteSpace(title) ? launcher : title;
        if (!string.IsNullOrWhiteSpace(shown))
        {
            shown = shown.ToUpperInvariant();
            float size = FitTrackedSize(g, shown, 20f, FontStyle.Bold, 252f, 0.6f);
            DrawTrackedText(g, shown, size, FontStyle.Bold, Lx, Ly + 80f, 0.6f, Color.FromArgb(228, ink), hi, 1f);
        }
        g.Restore(serigrafia);

        // 6. Botones de las alas: RESET es la tecla chica; POWER y OPEN son los dos
        //    discos grandes del mismo gris del cuerpo, con el ícono impreso.
        PaintConsoleButton(g, 69f, 442f, 18f, "RESET", ConsoleGlyph.None, shell, Color.FromArgb(200, ink), hi);
        PaintConsoleButton(g, 69f, 526f, 38f, "POWER", ConsoleGlyph.Power, shell, Color.FromArgb(84, 190, 118), hi);
        PaintConsoleButton(g, 531f, 526f, 38f, "OPEN", ConsoleGlyph.Eject, shell, Color.FromArgb(96, 156, 226), hi);

        // 7. Cara frontal: la malla la mapea al canto inferior, así que se pinta en su
        //    PROPIA franja del canvas (640..692), debajo del cuerpo. Desde arriba solo
        //    se ve la junta del faldón; las entradas aparecen al inclinar la consola.
        const float BandTop = 640f, BandH = 52f;
        using (var bandBrush = new LinearGradientBrush(
            new PointF(bx, BandTop), new PointF(bx, BandTop + BandH),
            Mix(shell, Color.White, 0.09f), Mix(shell, Color.Black, 0.24f)))
            g.FillRectangle(bandBrush, bx, BandTop, bw, BandH);

        using (var seamShadow = new Pen(Color.FromArgb(92, 0, 0, 0), 3f))
            g.DrawLine(seamShadow, bx, BandTop, x1, BandTop);
        using (var seamHi = new Pen(Color.FromArgb(92, 255, 255, 255), 1.2f))
            g.DrawLine(seamHi, bx, BandTop + 2.6f, x1, BandTop + 2.6f);

        // Faldón central: la sección de la tapa baja al frente algo más alta que las
        // alas. Es el escalón que hace que la consola se lea como PS1 y no como caja.
        var apron = new Rectangle((int)(Lx - Lr + 7f), (int)(BandTop + 2f),
            (int)(Lr * 2 - 14f), (int)(BandH - 4f));
        using (var apronPath = RoundPath(apron, 5))
        {
            using (var fill = new SolidBrush(Mix(shell, Color.White, 0.06f)))
                g.FillPath(fill, apronPath);
            using (var line = new Pen(Color.FromArgb(58, 0, 0, 0), 1.4f))
                g.DrawPath(line, apronPath);
            using (var lip = new Pen(Color.FromArgb(72, 255, 255, 255), 1.1f))
                g.DrawLine(lip, apron.Left + 4, apron.Top + 1.4f, apron.Right - 4, apron.Top + 1.4f);
        }

        PaintConsoleMemoryBay(g, 205f, BandTop + 4f, shell, ink, hi, "1");
        PaintConsoleMemoryBay(g, 395f, BandTop + 4f, shell, ink, hi, "2");
        // Solo quedan los dos conectores centrales (MEMORY CARD): los puertos de mando
        // de las alas se sacaron a pedido.

        // Filo inferior: labio claro arriba de la sombra del canto.
        using (var bottomHi = new Pen(Color.FromArgb(70, 255, 255, 255), 1.2f))
            g.DrawLine(bottomHi, bx + 14, BandTop + BandH - 5f, x1 - 14, BandTop + BandH - 5f);
        using (var bottomLo = new Pen(Color.FromArgb(105, 0, 0, 0), 2.4f))
            g.DrawLine(bottomLo, bx + 10, BandTop + BandH - 1.8f, x1 - 10, BandTop + BandH - 1.8f);

        // 7b. Cara TRASERA: la malla mapea el canto opuesto a esta otra franja
        //     (700..752), así los conectores van en el lateral de atrás y no sobre la
        //     cara inferior. Misma escala y paleta que la franja frontal.
        const float RearTop = 700f;
        using (var rearBrush = new LinearGradientBrush(
            new PointF(bx, RearTop), new PointF(bx, RearTop + BandH),
            Mix(shell, Color.White, 0.07f), Mix(shell, Color.Black, 0.26f)))
            g.FillRectangle(rearBrush, bx, RearTop, bw, BandH);
        using (var rearSeam = new Pen(Color.FromArgb(88, 0, 0, 0), 2.6f))
            g.DrawLine(rearSeam, bx, RearTop, x1, RearTop);
        using (var rearSeamHi = new Pen(Color.FromArgb(78, 255, 255, 255), 1.1f))
            g.DrawLine(rearSeamHi, bx, RearTop + 2.4f, x1, RearTop + 2.4f);

        // La pared trasera se ve desde afuera, o sea rotada 180° respecto de la textura.
        // Se pinta el contenido de la franja ya rotado para que en el modelo los puertos
        // caigan en el orden de la foto y los rótulos se lean derechos. El fondo queda
        // afuera de la rotación para no invertir la luz de la franja.
        Color rearInk = Color.FromArgb(212, Darken(shell, 0.64f));
        // La pared trasera se ve por su cara externa y la malla invierte el eje vertical
        // ahí: cada rótulo se pinta espejado en V sobre su propio centro para que en el
        // modelo se lea derecho, sin mover los puertos.
        void RearLabel(string text, float cx)
        {
            float cy = RearTop + 6f;
            var st = g.Save();
            g.TranslateTransform(0f, cy);
            g.ScaleTransform(1f, -1f);
            g.TranslateTransform(0f, -cy);
            DrawTrackedText(g, text, 6.6f, FontStyle.Bold, cx, cy, 0.7f, rearInk, hi, 0.4f);
            g.Restore(st);
        }

        // Rótulos cortos: el canto tiene 600 px y los nombres completos se tocaban.
        RearLabel("PARALLEL I/O", 94f);
        RearLabel("SERIAL", 203f);
        RearLabel("AUDIO", 265f);
        RearLabel("RFU", 318f);
        RearLabel("VIDEO", 358f);
        RearLabel("AV OUT", 426f);
        RearLabel("AC IN", 528f);

        PaintConsoleParallelPort(g, new Rectangle(28, 712, 132, 34), shell);
        PaintConsoleSerialPort(g, new Rectangle(184, 720, 38, 18), shell);
        PaintConsoleRca(g, 250f, 730f, Color.FromArgb(206, 56, 50), 12f);
        PaintConsoleRca(g, 280f, 730f, Color.FromArgb(236, 234, 228), 12f);
        PaintConsoleDcJack(g, 318f, 730f, 7f);
        PaintConsoleRca(g, 358f, 730f, Color.FromArgb(230, 194, 52), 12f);
        PaintConsoleAvMulti(g, new Rectangle(378, 716, 96, 26), shell);
        PaintConsoleAcIn(g, new Rectangle(506, 714, 44, 32), shell);

        // Junta del faldón con la tapa vista DESDE ARRIBA: es el mismo molde que la
        // pared repite en su primer renglón, así la línea sigue al inclinar el modelo.
        using (var seamTopLo = new Pen(Color.FromArgb(58, 0, 0, 0), 2f))
            g.DrawLine(seamTopLo, bx + 6, Seam, x1 - 6, Seam);
        using (var seamTopHi = new Pen(Color.FromArgb(58, 255, 255, 255), 1f))
            g.DrawLine(seamTopHi, bx + 6, Seam + 1.8f, x1 - 6, Seam + 1.8f);

        // 8. Marca de molde arriba a la derecha (la "E" de la foto) y grano fino, que
        //    es lo que evita que el plástico se vea como un degradado digital.
        DrawTrackedText(g, "E", 9f, FontStyle.Bold, 553f, 268f, 0f, Color.FromArgb(34, ink), hi, 0.4f);
        var grainClip = g.Save();
        g.SetClip(bodyPath, CombineMode.Intersect);
        PaintGrain(g, new Rectangle((int)bx, (int)by, (int)bw, (int)bh), 0.35f);
        g.Restore(grainClip);
    }

    /// <summary>
    /// Emblema de la tapa: placa de cuatro colores en perspectiva y una "W" maciza
    /// apoyada encima. Toma la composición del logotipo de la PS1 (símbolo sobre base
    /// de colores), pero es una marca propia de WinForge.
    /// </summary>
    private static void PaintConsoleMark(Graphics g, float cx, float cy, Color shell)
    {
        const float W = 98f, H = 17f, Skew = 7f;
        Color[] colors =
        {
            Color.FromArgb(66, 120, 210), Color.FromArgb(238, 194, 64),
            Color.FromArgb(64, 178, 112), Color.FromArgb(210, 68, 62)
        };
        float top = cy + 4f, bottom = top + H;
        for (int i = 0; i < 4; i++)
        {
            float x0 = cx - W * 0.5f + W * i / 4f;
            float x1 = x0 + W / 4f;
            using var brush = new SolidBrush(colors[i]);
            g.FillPolygon(brush, new[]
            {
                new PointF(x0 + Skew, top),
                new PointF(x1 + Skew, top),
                new PointF(x1, bottom),
                new PointF(x0, bottom)
            });
        }
        using (var edge = new Pen(Color.FromArgb(70, 0, 0, 0), 1.1f))
            g.DrawPolygon(edge, new[]
            {
                new PointF(cx - W * 0.5f + Skew, top),
                new PointF(cx + W * 0.5f + Skew, top),
                new PointF(cx + W * 0.5f, bottom),
                new PointF(cx - W * 0.5f, bottom)
            });
        using (var sheen = new Pen(Color.FromArgb(120, 255, 255, 255), 1.2f))
            g.DrawLine(sheen, cx - W * 0.5f + Skew + 2, top + 1.4f, cx + W * 0.5f + Skew - 2, top + 1.4f);

        // La "W" lleva un canto oscuro detrás: así se lee elevada sobre la placa.
        using var font = new Font("Segoe UI", 48f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var deep = new SolidBrush(Color.FromArgb(150, 104, 28, 24)))
            g.DrawString("W", font, deep, new PointF(cx + 2.2f, cy - 21f + 2.8f), sf);
        using (var face = new LinearGradientBrush(
            new PointF(cx, cy - 46f), new PointF(cx, cy + 2f),
            Color.FromArgb(232, 84, 64), Color.FromArgb(206, 48, 38)))
            g.DrawString("W", font, face, new PointF(cx, cy - 21f), sf);
    }

    /// <summary>
    /// Ícono del emulador sobre la tapa: se recorta al cuadrado central y se escala
    /// nítido, como la calcomanía del fabricante en la consola real. Devuelve false
    /// si no hay arte válido (ahí la tapa usa el emblema propio).
    /// </summary>
    private static bool PaintConsoleLogo(Graphics g, float cx, float cy, string? artPath)
    {
        using var art = LoadArt(artPath);
        if (art == null || art.Width < 2 || art.Height < 2) return false;

        const float Side = 112f;
        float src = Math.Min(art.Width, art.Height);
        var dest = new RectangleF(cx - Side * 0.5f, cy - Side * 0.5f, Side, Side);
        var crop = new RectangleF((art.Width - src) * 0.5f, (art.Height - src) * 0.5f, src, src);
        DrawLogoShadow(g, art, dest, 2f, 2.6f, crop);
        g.DrawImage(art, dest, crop, GraphicsUnit.Pixel);
        return true;
    }

    /// <summary>
    /// Sombra suave debajo de un logo con alfa: las partes blancas de un logo se
    /// pierden sobre un fondo claro, y un rectángulo de fondo se vería pegado.
    /// </summary>
    private static void DrawLogoShadow(Graphics g, Bitmap logo, RectangleF dest, float dx, float dy,
        RectangleF? source = null)
    {
        using var shadow = new ImageAttributes();
        shadow.SetColorMatrix(new ColorMatrix
        {
            Matrix00 = 0f, Matrix11 = 0f, Matrix22 = 0f,
            Matrix33 = 0.30f,
            Matrix44 = 1f
        });
        var shadowDest = Rectangle.Round(new RectangleF(dest.X + dx, dest.Y + dy, dest.Width, dest.Height));
        var src = source ?? new RectangleF(0, 0, logo.Width, logo.Height);
        g.DrawImage(logo, shadowDest, (int)src.X, (int)src.Y,
            (int)src.Width, (int)src.Height, GraphicsUnit.Pixel, shadow);
    }

    /// <summary>
    /// Tecla de consola: pozo hundido, domo del mismo gris del cuerpo con el filo
    /// iluminado arriba y su ícono/serigrafía impresos.
    /// </summary>
    private static void PaintConsoleButton(Graphics g, float cx, float cy, float r, string label,
        ConsoleGlyph glyph, Color shell, Color labelColor, Color hi)
    {
        // Pozo: una ranura fina, no un aro grueso. La tecla de la PS1 va casi a ras del
        // cuerpo; el aro ancho la convertía en un bowl.
        using (var well = new Pen(Color.FromArgb(46, 0, 0, 0), 2.2f))
            g.DrawEllipse(well, cx - r - 2.4f, cy - r - 2.4f, (r + 2.4f) * 2, (r + 2.4f) * 2);
        using (var wellHi = new Pen(Color.FromArgb(52, 255, 255, 255), 1f))
            g.DrawArc(wellHi, cx - r - 3.4f, cy - r - 3.4f, (r + 3.4f) * 2, (r + 3.4f) * 2, 196f, 132f);

        using (var dome = new SolidBrush(Mix(shell, Color.White, 0.07f)))
            g.FillEllipse(dome, cx - r, cy - r, r * 2, r * 2);
        using (var shade = new Pen(Color.FromArgb(44, 0, 0, 0), Math.Max(1.4f, r * 0.09f)))
            g.DrawArc(shade, cx - r + 2, cy - r + 2, (r - 2) * 2, (r - 2) * 2, 8f, 150f);
        using (var light = new Pen(Color.FromArgb(75, 255, 255, 255), Math.Max(1.1f, r * 0.07f)))
            g.DrawArc(light, cx - r + 2, cy - r + 2, (r - 2) * 2, (r - 2) * 2, 192f, 136f);

        switch (glyph)
        {
            case ConsoleGlyph.Power:
                using (var pen = new Pen(labelColor, 2.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(pen, cx - 10f, cy - 18f, 20f, 20f, -58f, 296f);
                    g.DrawLine(pen, cx, cy - 21f, cx, cy - 7f);
                }
                break;
            case ConsoleGlyph.Eject:
                using (var brush = new SolidBrush(labelColor))
                    g.FillPolygon(brush, new[]
                    {
                        new PointF(cx - 9f, cy - 6f), new PointF(cx + 9f, cy - 6f), new PointF(cx, cy - 17f)
                    });
                using (var bar = new Pen(labelColor, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(bar, cx - 9f, cy + 1f, cx + 9f, cy + 1f);
                break;
        }

        bool icon = glyph != ConsoleGlyph.None;
        float labelSize = Math.Max(6.5f, icon ? r * 0.24f : r * 0.36f);
        DrawTrackedText(g, label, labelSize, FontStyle.Bold, cx,
            cy + (icon ? r * 0.40f : 0f), icon ? 1.1f : 0.3f, labelColor, hi, 0.7f);
    }

    /// <summary>
    /// Ranura de tarjeta de memoria de la cara frontal: número impreso, placa
    /// "MEMORY CARD" y el conector hundido con sus ocho contactos dorados.
    /// </summary>
    private static void PaintConsoleMemoryBay(Graphics g, float cx, float top, Color shell, Color ink, Color hi, string number)
    {
        DrawTrackedText(g, number, 13f, FontStyle.Bold, cx, top + 6f, 0f, Color.FromArgb(195, ink), hi, 0.9f);

        var plate = new Rectangle((int)(cx - 57), (int)(top + 13), 114, 12);
        using (var platePath = RoundPath(plate, 3))
        {
            using (var fill = new SolidBrush(Mix(shell, Color.White, 0.36f)))
                g.FillPath(fill, platePath);
            using (var line = new Pen(Mix(shell, Color.Black, 0.26f), 1.1f))
                g.DrawPath(line, platePath);
        }
        DrawTrackedText(g, "MEMORY CARD", 6.4f, FontStyle.Bold, cx, plate.Top + 6f, 1f, Color.FromArgb(210, ink), hi, 0.5f);

        var slot = new Rectangle((int)(cx - 52), (int)(top + 27), 104, 18);
        using (var slotPath = RoundPath(slot, 3))
        {
            using (var floor = new SolidBrush(Color.FromArgb(15, 15, 18)))
                g.FillPath(floor, slotPath);
            DrawRecess(g, slotPath, slot, shell, 2.2f, paintFloor: false, escala: 0.3f);
        }
        for (int i = 0; i < 8; i++)
        {
            float px = slot.Left + 8 + i * 12.4f;
            using var pin = new LinearGradientBrush(
                new PointF(px, slot.Top + 5), new PointF(px + 6, slot.Bottom - 4),
                Color.FromArgb(188, 172, 120), Color.FromArgb(96, 84, 56));
            g.FillRectangle(pin, px, slot.Top + 5, 6.4f, 8.5f);
        }
    }

    /// <summary>Puerto paralelo (DB-25) del dorso: boca ancha con dos filas de pines.</summary>
    private static void PaintConsoleParallelPort(Graphics g, Rectangle r, Color shell)
    {
        using var path = RoundPath(r, 4);
        using (var floor = new SolidBrush(Color.FromArgb(20, 20, 24)))
            g.FillPath(floor, path);
        DrawRecess(g, path, r, shell, 2.4f, paintFloor: false, escala: 0.3f);
        float paso = (r.Width - 20f) / 11f;
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < 12; i++)
            {
                using var pin = new SolidBrush(Color.FromArgb(176, 172, 160));
                g.FillRectangle(pin, r.Left + 10f + i * paso,
                    r.Top + r.Height * 0.20f + row * r.Height * 0.33f, 3.6f, r.Height * 0.26f);
            }
    }

    /// <summary>Puerto serial del dorso: boca chica con su hilera de contactos.</summary>
    private static void PaintConsoleSerialPort(Graphics g, Rectangle r, Color shell)
    {
        using var path = RoundPath(r, 3);
        using (var floor = new SolidBrush(Color.FromArgb(14, 14, 17)))
            g.FillPath(floor, path);
        DrawRecess(g, path, r, shell, 1.6f, paintFloor: false, escala: 0.25f);
        for (int i = 0; i < 8; i++)
            using (var pin = new SolidBrush(Color.FromArgb(168, 164, 152)))
                g.FillRectangle(pin, r.Left + 4.5f + i * 3.9f, r.Top + 6, 2.2f, 11f);
    }

    /// <summary>Toma RCA del dorso (rojo/izquierdo, blanco/derecho o amarillo/video).</summary>
    private static void PaintConsoleRca(Graphics g, float cx, float cy, Color ring, float r = 16f)
    {
        float metal = r * 0.81f;
        using (var outer = new SolidBrush(Color.FromArgb(24, 24, 27)))
            g.FillEllipse(outer, cx - r, cy - r, r * 2, r * 2);
        using (var ringMetal = new LinearGradientBrush(
            new PointF(cx - metal, cy - metal), new PointF(cx + metal, cy + metal),
            Color.FromArgb(154, 151, 144), Color.FromArgb(52, 51, 48)))
            g.FillEllipse(ringMetal, cx - metal, cy - metal, metal * 2, metal * 2);
        using (var color = new SolidBrush(ring))
            g.FillEllipse(color, cx - r * 0.59f, cy - r * 0.59f, r * 1.18f, r * 1.18f);
        using (var hole = new SolidBrush(Color.FromArgb(12, 12, 14)))
            g.FillEllipse(hole, cx - r * 0.19f, cy - r * 0.19f, r * 0.38f, r * 0.38f);
        using (var shine = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(0.8f, r * 0.07f)))
            g.DrawArc(shine, cx - metal, cy - metal, metal * 2, metal * 2, 200, 70);
    }

    /// <summary>Toma de corriente continua chica (RFU DC OUT).</summary>
    private static void PaintConsoleDcJack(Graphics g, float cx, float cy, float r = 9f)
    {
        using (var outer = new SolidBrush(Color.FromArgb(26, 26, 29)))
            g.FillEllipse(outer, cx - r, cy - r, r * 2, r * 2);
        using (var hole = new SolidBrush(Color.FromArgb(8, 8, 10)))
            g.FillEllipse(hole, cx - r * 0.56f, cy - r * 0.56f, r * 1.12f, r * 1.12f);
        using (var rim = new Pen(Color.FromArgb(120, 158, 156, 148), Math.Max(0.9f, r * 0.13f)))
            g.DrawEllipse(rim, cx - r, cy - r, r * 2, r * 2);
    }

    /// <summary>Salida AV MULTI: conector ancho con su hilera de contactos dorados.</summary>
    private static void PaintConsoleAvMulti(Graphics g, Rectangle r, Color shell)
    {
        using var path = RoundPath(r, 4);
        using (var floor = new SolidBrush(Color.FromArgb(22, 20, 18)))
            g.FillPath(floor, path);
        DrawRecess(g, path, r, shell, 2f, paintFloor: false, escala: 0.28f);
        float paso = (r.Width - 14f) / 11f;
        for (int i = 0; i < 12; i++)
            using (var pin = new SolidBrush(Color.FromArgb(186, 168, 116)))
                g.FillRectangle(pin, r.Left + 7 + i * paso,
                    r.Top + r.Height * 0.32f, 2.6f, r.Height * 0.42f);
    }

    /// <summary>Entrada de corriente alterna: zócalo de dos agujeros (figura 8).</summary>
    private static void PaintConsoleAcIn(Graphics g, Rectangle r, Color shell)
    {
        using var path = RoundPath(r, 6);
        using (var floor = new SolidBrush(Color.FromArgb(16, 16, 18)))
            g.FillPath(floor, path);
        DrawRecess(g, path, r, shell, 2.2f, paintFloor: false, escala: 0.3f);
        float cx = r.Left + r.Width * 0.5f, cy = r.Top + r.Height * 0.5f;
        foreach (float dx in new[] { -7.5f, 7.5f })
        {
            using var hole = new SolidBrush(Color.FromArgb(6, 6, 8));
            g.FillEllipse(hole, cx + dx - 5.5f, cy - 5.5f, 11, 11);
            using var rim = new Pen(Color.FromArgb(80, 255, 255, 255), 1f);
            g.DrawEllipse(rim, cx + dx - 5.5f, cy - 5.5f, 11, 11);
        }
    }

    /// <summary>Tamaño que hace entrar un texto CON tracking en <paramref name="maxWidth"/>.
    /// <see cref="FitFontSize"/> mide sin tracking y un título largo se salía de la tapa.</summary>
    private static float FitTrackedSize(Graphics g, string text, float start, FontStyle style, float maxWidth, float tracking)
    {
        using var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };
        for (float size = start; size >= 7f; size -= 0.5f)
        {
            using var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            float width = tracking * Math.Max(0, text.Length - 1);
            for (int i = 0; i < text.Length; i++)
                width += g.MeasureString(text[i].ToString(), font, PointF.Empty, sf).Width;
            if (width <= maxWidth) return size;
        }
        return 7f;
    }

    /// <summary>
    /// Texto serigrafiado con tracking (espaciado entre letras). GDI+ no expone
    /// letter-spacing, así que se dibuja letra por letra; el resaltado corre un píxel
    /// hacia abajo para que la marca se lea impresa en el plástico y no flotando.
    /// </summary>
    private static void DrawTrackedText(Graphics g, string text, float pxSize, FontStyle style,
        float cx, float cy, float tracking, Color color, Color highlight, float highlightOffset)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var font = new Font("Segoe UI", pxSize, style, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };
        float total = 0f;
        for (int i = 0; i < text.Length; i++)
            total += g.MeasureString(text[i].ToString(), font, PointF.Empty, sf).Width + tracking;
        total -= tracking;
        float x = cx - total * 0.5f;
        float y = cy - font.Height * 0.5f;
        for (int i = 0; i < text.Length; i++)
        {
            string ch = text[i].ToString();
            using (var halo = new SolidBrush(Color.FromArgb(highlight.A / 2, highlight)))
                g.DrawString(ch, font, halo, new PointF(x + 0.7f, y + highlightOffset), sf);
            using (var brush = new SolidBrush(color))
                g.DrawString(ch, font, brush, new PointF(x, y), sf);
            x += g.MeasureString(ch, font, PointF.Empty, sf).Width + tracking;
        }
    }

    private static void PaintDeviceBack(Graphics g, DeviceKind kind, string launcher, string code, Color plastic, bool flat)
    {
        if (kind == DeviceKind.Arcade)
        {
            PaintArcadeBack(g, launcher, code, plastic);
            return;
        }
        DrawDeviceShell(g, kind, plastic, flat);
        Color hi = Mix(plastic, Color.White, 0.55f);
        if (kind == DeviceKind.Phone)
        {
            // Módulo de cámaras cuadrado arriba a la izquierda del cuerpo
            // (cuerpo x 70..530) + textos elevados.
            var module = new Rectangle(98, 30, 120, 120);
            using (var modPath = RoundPath(module, 26))
            using (var modBrush = new SolidBrush(Color.FromArgb(22, 22, 26)))
                g.FillPath(modBrush, modPath);
            foreach (var (lx, ly) in new[] { (128, 60), (188, 60), (128, 120) })
            {
                g.FillEllipse(new SolidBrush(Color.FromArgb(8, 8, 10)), lx - 20, ly - 20, 40, 40);
                g.FillEllipse(new SolidBrush(Color.FromArgb(30, 46, 80)), lx - 11, ly - 11, 22, 22);
            }
            g.FillEllipse(new SolidBrush(Color.FromArgb(240, 230, 170)), 188 - 7, 120 - 7, 14, 14);

            DrawRaisedText(g, "WINFORGE", 54f, FontStyle.Bold, 300f, 480f, plastic, hi);
            DrawRaisedText(g, "MOBILE", 30f, FontStyle.Regular, 300f, 540f, plastic, hi);
            DrawRaisedText(g, string.IsNullOrWhiteSpace(code) ? "WINFORGE - MOBILE" : DeviceCode(code).ToUpperInvariant(), 26f, FontStyle.Bold, 300f, 720f, plastic, hi);
            return;
        }        // ===== Consola (dorso) =====
        // La cara trasera no recibe la luz de arriba: base más apagada, rejilla de
        // ventilación a la izquierda, etiqueta de modelo al centro y el bloque de
        // conectores a la derecha. La textura del dorso va espejada en U por la malla,
        // así que se pinta normal y se lee normal al girar la pieza.
        var (bx, by, bw, bh, br) = DeviceBody(DeviceKind.Console);
        float x1 = bx + bw, y1 = by + bh;
        Color shellBack = DeviceShellColor(DeviceKind.Console, plastic);
        using var backBody = RoundPath(new Rectangle((int)bx, (int)by, (int)bw, (int)bh), (int)br);

        using (var baseBrush = new LinearGradientBrush(
            new PointF(bx, by), new PointF(x1, y1),
            Mix(shellBack, Color.Black, 0.06f), Mix(shellBack, Color.Black, 0.17f)))
            g.FillPath(baseBrush, backBody);

        var backClip = g.Save();
        g.SetClip(backBody, CombineMode.Intersect);

        // Rejilla: ranuras finas, no barras. El dorso real es una parrilla de rendijas
        // apretadas sobre el plástico.
        for (int i = 0; i < 9; i++)
        {
            var vent = new Rectangle(54, 312 + i * 22, 188, 7);
            using var ventPath = RoundPath(vent, 4);
            using (var floor = new SolidBrush(Mix(shellBack, Color.Black, 0.60f)))
                g.FillPath(floor, ventPath);
            DrawRecess(g, ventPath, vent, shellBack, 1.4f, paintFloor: false, escala: 0.22f);
        }

        // Etiqueta de modelo: calcomanía clara con tinta oscura. Antes el texto iba a
        // 10 px sobre el mismo gris del cuerpo y no se leía; así el contraste es real
        // y el modelo entra entero en una línea.
        var plate = new Rectangle(252, 314, 314, 180);
        using (var shadePath = RoundPath(new Rectangle(plate.X + 1, plate.Y + 3, plate.Width, plate.Height), 10))
        using (var shade = new SolidBrush(Color.FromArgb(26, 0, 0, 0)))
            g.FillPath(shade, shadePath);
        using (var platePath = RoundPath(plate, 10))
        {
            using (var fill = new LinearGradientBrush(
                new PointF(plate.Left, plate.Top), new PointF(plate.Right, plate.Bottom),
                Mix(shellBack, Color.Black, 0.50f), Mix(shellBack, Color.Black, 0.60f)))
                g.FillPath(fill, platePath);
            using (var line = new Pen(Color.FromArgb(70, 255, 255, 255), 1.1f))
                g.DrawPath(line, platePath);
        }
        float plateCx = plate.X + plate.Width * 0.5f;
        // Tinta clara sobre el gris grafito: el texto chico se lee de verdad y el logo
        // (W blanca + F naranja) se ve como el sello original, sin lavarse.
        Color plateInk = Color.FromArgb(242, 240, 239, 233);
        Color plateHi = Color.FromArgb(120, 0, 0, 0);

        // Logo de WinForge sobre la etiqueta, como el sello del fabricante.
        using (var logo = LoadLauncherLogoBitmap("WinForge"))
        {
            if (logo != null)
            {
                const float Box = 76f;
                float scale = Box / Math.Max(logo.Width, logo.Height);
                float lw = logo.Width * scale, lh = logo.Height * scale;
                var dest = new RectangleF(plateCx - lw * 0.5f,
                    plate.Top + 12f + (Box - lh) * 0.5f, lw, lh);
                g.DrawImage(logo, dest);
            }
        }

        DrawTrackedText(g, "WINFORGE", 25f, FontStyle.Bold, plateCx, plate.Top + 104f, 3f, plateInk, plateHi, 0.8f);
        DrawTrackedText(g, "CONSOLE", 12f, FontStyle.Regular, plateCx, plate.Top + 126f, 3.6f,
            Color.FromArgb(215, plateInk), plateHi, 0.6f);
        string model = string.IsNullOrWhiteSpace(code) ? "MODEL WF-01" : DeviceCode(code).ToUpperInvariant();
        float modelSize = FitTrackedSize(g, model, 16f, FontStyle.Regular, plate.Width - 40f, 0.5f);
        DrawTrackedText(g, model, modelSize, FontStyle.Regular, plateCx, plate.Top + 158f, 0.5f,
            Color.FromArgb(245, plateInk), plateHi, 0.6f);

        // ===== Conectores traseros (como la PS1 real) =====
        // Panel hundido a lo ancho del dorso con la fila de puertos y sus rótulos:
        // PARALLEL I/O · SERIAL I/O · AUDIO OUT (R/L) · RFU DC OUT · VIDEO OUT ·
        // AV MULTI OUT · AC IN. Antes había dos conectores genéricos sueltos.
        // Patas de goma en las esquinas: es la cara que apoya. Los conectores ya no van
        // acá: la malla mapea el canto trasero a la franja 700..752 del frente.
        foreach (var (fx, fy) in new[] { (62f, 280f), (538f, 280f), (62f, 598f), (538f, 598f) })
        {
            using (var halo = new SolidBrush(Color.FromArgb(26, 0, 0, 0)))
                g.FillEllipse(halo, fx - 30, fy - 26, 60, 56);
            using (var foot = new SolidBrush(Color.FromArgb(56, 54, 52)))
                g.FillEllipse(foot, fx - 25, fy - 24, 50, 48);
            using (var dome = new SolidBrush(Color.FromArgb(82, 79, 75)))
                g.FillEllipse(dome, fx - 17, fy - 17, 34, 32);
            using (var gloss = new SolidBrush(Color.FromArgb(58, 255, 255, 255)))
                g.FillEllipse(gloss, fx - 12, fy - 14, 18, 10);
        }

        // Filo inferior: la cara que apoya no se ve, el canto sí.
        using (var bottom = new Pen(Color.FromArgb(110, 0, 0, 0), 2.4f))
            g.DrawLine(bottom, bx + 10, y1 - 2f, x1 - 10, y1 - 2f);
        g.Restore(backClip);
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
    private static void DrawRecess(Graphics g, GraphicsPath path, Rectangle bounds, Color plastic, float depth, bool paintFloor, float escala = 1f)
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
            using var anillo = new Pen(penumbra, 2f + i * 4f * escala);
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
    /// Cara frontal del cartucho: carcasa, pestaña superior hundida, pozo, etiqueta con
    /// cabecera del launcher, ventana de carátula, franja inferior de código y flecha inferior.
    /// </summary>
    public static Bitmap PaintFace(string title, string launcher, string code, string? artPath, Color plastic, bool flat = true, GameHeader? header = null, DeviceKind kind = DeviceKind.Cartridge)
    {
        var bmp = new Bitmap(FaceWidth, FaceHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (kind != DeviceKind.Cartridge)
        {
            PaintDeviceFace(g, kind, title, launcher, code, artPath, plastic, flat, header);
            return bmp;
        }

        DrawShellBody(g, plastic, flat);

        // 1) Pestaña superior: la traba donde el cartucho se engancha. Va HUNDIDA en el
        // plástico, igual que la etiqueta: pozo chico, pared en sombra contra la luz y
        // labio iluminado del lado opuesto.
        var tabRect = new Rectangle(TabX, TabY, TabW, TabH);
        using (var tabPath = RoundPath(tabRect, TabR))
        {
            using (var tabFill = new SolidBrush(Darken(plastic, 0.12f)))
                g.FillPath(tabFill, tabPath);
            DrawRecess(g, tabPath, tabRect, plastic, 3.5f, paintFloor: false, escala: 0.25f);
        }

        // 2) Etiqueta redondeada (clip de cabecera + carátula + franja de código)
        var stickerRect = new Rectangle(StickerX, StickerY, StickerW, StickerH);
        using var stickerPath = RoundPath(stickerRect, StickerR);

        // Piso del pozo, debajo del contenido. No se pinta ningún marco alrededor de la
        // etiqueta: en el 3D esa pared ya existe como geometría y pintarla también la hacía
        // ver doble. En la grilla 2D el borde lo resuelven la oclusión y el labio de abajo.
        using (var pocketFloor = new SolidBrush(Mix(plastic, Color.Black, 0.45f)))
            g.FillPath(pocketFloor, stickerPath);

        var prevClip = g.Clip;
        g.SetClip(stickerPath, CombineMode.Intersect);

        // Margen del pozo: el contenido impreso NO llega al borde de la etiqueta, deja una
        // franja del piso del hueco. Sin eso la carátula y la franja de código tocan la
        // pared, su brillo se come el canto y todo parece pegado ENCIMA de la carcasa en
        // vez de hundido dentro de ella.
        const int Margen = 10;
        var headerRect = new Rectangle(HeaderX + Margen, HeaderY + Margen, HeaderW - Margen * 2, HeaderH - Margen + 1);
        var artRect = new Rectangle(ArtX + Margen, ArtY, ArtW - Margen * 2, ArtH);
        var labelRect = new Rectangle(LabelX + Margen, LabelY, LabelW - Margen * 2, LabelH - Margen + 1);

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

        // 3) Flecha inferior (apunta al dock): mismo molde hundido que la pestaña.
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
            using (var arrowFill = new SolidBrush(Darken(plastic, 0.12f)))
                g.FillPath(arrowFill, arrowPath);
            DrawRecess(g, arrowPath, arrowRect, plastic, 3.5f, paintFloor: false, escala: 0.2f);
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

                    // 2) El banner panorámico completo encajado en la mitad superior/central.
                    // Sus bordes se difuminan: contra el fondo borroneado el corte recto se
                    // veía como una tira pegada ENCIMA de la etiqueta.
                    var fitZone = new Rectangle(artRect.X, artRect.Y + 18, artRect.Width, (int)(artRect.Height * 0.64f));
                    var fit = FitInside(artBmp.Size, fitZone);

                    using (var banner = FeatherEdges(artBmp, (int)(artBmp.Height * 0.16f)))
                        g.DrawImage(banner, fit);
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

    public static Bitmap PaintBack(string launcher, string code, Color plastic, bool flat = true, DeviceKind kind = DeviceKind.Cartridge)
    {
        var bmp = new Bitmap(FaceWidth, FaceHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (kind != DeviceKind.Cartridge)
        {
            PaintDeviceBack(g, kind, launcher, code, plastic, flat);
            return bmp;
        }

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

    private static void PaintGrain(Graphics g, Rectangle bounds, float intensity = 1f)
    {
        var rnd = new Random(20261013);
        int dots = (int)(1400 * Math.Clamp(intensity, 0f, 1f));
        for (int i = 0; i < dots; i++)
        {
            int x = bounds.X + rnd.Next(bounds.Width);
            int y = bounds.Y + rnd.Next(bounds.Height);
            int a = Math.Max(2, (int)(rnd.Next(5, 15) * Math.Clamp(intensity, 0f, 1f)));
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

    /// <summary>
    /// Copia con los bordes superior e inferior desvanecidos en alfa, para que una imagen se
    /// funda con lo que hay detrás en vez de terminar en un corte recto. Se hace sobre el
    /// canal alfa real (no con un degradado encima) para no lavar el color de la imagen.
    /// </summary>
    private static Bitmap FeatherEdges(Bitmap src, int alto)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
        }

        int n = Math.Max(1, Math.Min(alto, src.Height / 2));
        var data = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var buffer = new byte[data.Stride * dst.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
        for (int y = 0; y < dst.Height; y++)
        {
            float f = y < n ? y / (float)n
                    : y >= dst.Height - n ? (dst.Height - 1 - y) / (float)n
                    : 1f;
            if (f >= 1f) continue;
            int a = (int)(Math.Clamp(f, 0f, 1f) * 255f);
            int fila = y * data.Stride;
            for (int x = 0; x < dst.Width; x++)
            {
                int o = fila + x * 4 + 3;
                buffer[o] = (byte)(buffer[o] * a / 255);
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        dst.UnlockBits(data);
        return dst;
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
