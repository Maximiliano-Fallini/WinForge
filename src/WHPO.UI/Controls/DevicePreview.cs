using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;

namespace WHPO_UI.Controls;

/// <summary>
/// Render por software del dispositivo para la card de la biblioteca. Usa la MISMA
/// malla (<see cref="CartridgeModel.BuildMesh"/>), las mismas caras pintadas y el mismo
/// encuadre que <see cref="Cartridge3D"/>: así la grilla y la vista de detalle muestran
/// la misma pieza (ángulo, grosor y franja frontal) sin instanciar un control D3D por
/// card. Es GDI+/CPU puro, corre en el hilo de pintado de la grilla y el PNG queda
/// cacheado como el resto del arte.
/// </summary>
internal static class DevicePreview
{
    /// <summary>Textura ya volcada a floats RGB (0..1) para muestrear en el rasterizador.</summary>
    private readonly struct Tex
    {
        public readonly float[] Px;
        public readonly int W, H;

        public Tex(Bitmap bmp)
        {
            W = bmp.Width;
            H = bmp.Height;
            Px = new float[W * H * 3];
            var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < H; y++)
                {
                    IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                    for (int x = 0; x < W; x++)
                    {
                        IntPtr p = IntPtr.Add(row, x * 4);
                        int i = (y * W + x) * 3;
                        Px[i] = System.Runtime.InteropServices.Marshal.ReadByte(IntPtr.Add(p, 2)) / 255f;
                        Px[i + 1] = System.Runtime.InteropServices.Marshal.ReadByte(IntPtr.Add(p, 1)) / 255f;
                        Px[i + 2] = System.Runtime.InteropServices.Marshal.ReadByte(p) / 255f;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
        }
    }

    /// <summary>
    /// Renderiza el dispositivo con la cámara y el sombreado de <see cref="Cartridge3D"/>
    /// sobre fondo transparente. <paramref name="pitchDeg"/> es la inclinación de reposo,
    /// <paramref name="zoom"/> la fracción del panel que ocupa la pieza (igual que la
    /// rueda del detalle) y <paramref name="yawDeg"/> el giro sobre el eje vertical que
    /// usa el arrastre en la card. El ancho/alto define el aspecto del encuadre.
    /// </summary>
    internal static Bitmap Render(
        DeviceKind kind, Bitmap face, Bitmap back, Color plastic,
        int width, int height, float pitchDeg, float zoom = 0.95f, float yawDeg = 0f)
    {
        int W = Math.Max(2, width), H = Math.Max(2, height);
        // 2×2 supersampling: el rasterizador es de un sample por píxel y, al reducir la
        // card, el canto superior quedaba escalonado (se leía como una línea punteada).
        const int SS = 2;
        int RW = W * SS, RH = H * SS;
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        var (verts, idx) = CartridgeModel.BuildMesh(kind);
        if (verts.Length == 0 || idx.Length == 0) return bmp;

        var front = new Tex(face);
        var rear = new Tex(back);
        var shell = CartridgeModel.DeviceShellColor(kind, plastic);
        float pr = shell.R / 255f, pg = shell.G / 255f, pb = shell.B / 255f;

        var key = Vector3.Normalize(new Vector3(-0.32f, 0.44f, 0.84f));
        var fill = Vector3.Normalize(new Vector3(0.38f, -0.42f, 0.62f));
        var world = Matrix4x4.CreateRotationY(yawDeg * MathF.PI / 180f)
                  * Matrix4x4.CreateRotationX(pitchDeg * MathF.PI / 180f);
        float aspect = RW / (float)RH;
        float dist = FitDistance(kind, world, aspect, zoom);
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, dist), Vector3.Zero, Vector3.UnitY);
        float f = 1f / MathF.Tan(22.5f * MathF.PI / 180f); // FOV vertical 45°

        var sp = new Vector2[verts.Length];
        var dep = new float[verts.Length];
        var nrm = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            var pv = Vector3.Transform(Vector3.Transform(verts[i].Position, world), view);
            nrm[i] = Vector3.TransformNormal(verts[i].Normal, world);
            dep[i] = -pv.Z;
            sp[i] = new Vector2((f * pv.X / dep[i]) * 0.5f * RH + RW * 0.5f,
                                RH * 0.5f - (f * pv.Y / dep[i]) * 0.5f * RH);
        }

        var px = new byte[RW * RH * 4];            // BGRA, alfa 0 = fondo
        var zbuf = new float[RW * RH];
        for (int i = 0; i < zbuf.Length; i++) zbuf[i] = float.MaxValue;

        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
            var a = sp[i0]; var b = sp[i1]; var c = sp[i2];
            int x0 = (int)MathF.Max(0, MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            int x1 = (int)MathF.Min(RW - 1, MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int y0 = (int)MathF.Max(0, MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            int y1 = (int)MathF.Min(RH - 1, MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (Math.Abs(area) < 1e-6f) continue;
            float invArea = 1f / area;
            var v0 = verts[i0]; var v1 = verts[i1]; var v2 = verts[i2];
            float w0 = 1f / dep[i0], w1 = 1f / dep[i1], w2 = 1f / dep[i2];

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float cx = x + 0.5f, cy = y + 0.5f;
                    float l0 = ((b.X - cx) * (c.Y - cy) - (b.Y - cy) * (c.X - cx)) * invArea;
                    float l1 = ((c.X - cx) * (a.Y - cy) - (c.Y - cy) * (a.X - cx)) * invArea;
                    float l2 = 1f - l0 - l1;
                    if (l0 < -1e-4f || l1 < -1e-4f || l2 < -1e-4f) continue;
                    float depth = 1f / (l0 * w0 + l1 * w1 + l2 * w2);
                    int pi = y * RW + x;
                    if (depth >= zbuf[pi] - 1e-6f) continue;

                    float iu = (l0 * v0.Uv.X * w0 + l1 * v1.Uv.X * w1 + l2 * v2.Uv.X * w2) * depth;
                    float iv = (l0 * v0.Uv.Y * w0 + l1 * v1.Uv.Y * w1 + l2 * v2.Uv.Y * w2) * depth;
                    float canal = l0 * v0.Tint.W + l1 * v1.Tint.W + l2 * v2.Tint.W;
                    float ocl = l0 * v0.Tint.X + l1 * v1.Tint.X + l2 * v2.Tint.X;
                    var n = nrm[i0] * l0 + nrm[i1] * l1 + nrm[i2] * l2;
                    if (n.LengthSquared() > 1e-8f) n = Vector3.Normalize(n);

                    float esFrente = Math.Clamp(1f - Math.Abs(canal - 1f), 0f, 1f);
                    float esDorso = Math.Clamp(1f - Math.Abs(canal - 2f), 0f, 1f);
                    float esPozo = Math.Clamp(1f - Math.Abs(canal - 3f), 0f, 1f);
                    float esTextura = Math.Clamp(esFrente + esDorso, 0f, 1f);

                    float luzCanto = 0.84f + 0.08f * Math.Max(0f, Vector3.Dot(n, key))
                                   + 0.04f * Math.Max(0f, Vector3.Dot(n, fill));
                    float luzPozo = 0.30f + 0.70f * Math.Max(0f, Vector3.Dot(n, key))
                                  + 0.22f * Math.Max(0f, Vector3.Dot(n, fill));
                    float luz = luzCanto + (luzPozo - luzCanto) * esPozo;
                    float occ = 1f + (ocl - 1f) * esPozo;
                    float cr = pr * luz * occ, cg = pg * luz * occ, cb = pb * luz * occ;

                    float tr = cr, tg = cg, tb = cb;
                    if (esTextura > 0f)
                    {
                        Sample(esDorso > esFrente ? rear : front, iu, iv, out float sr, out float sg, out float sb);
                        tr = cr + (sr - cr) * esTextura;
                        tg = cg + (sg - cg) * esTextura;
                        tb = cb + (sb - cb) * esTextura;
                    }

                    px[pi * 4] = (byte)(Math.Clamp(tb, 0f, 1f) * 255f);
                    px[pi * 4 + 1] = (byte)(Math.Clamp(tg, 0f, 1f) * 255f);
                    px[pi * 4 + 2] = (byte)(Math.Clamp(tr, 0f, 1f) * 255f);
                    px[pi * 4 + 3] = 255;
                    zbuf[pi] = depth;
                }
            }
        }

        // Cierra las grietas de 1 px entre triángulos que comparten borde: este
        // rasterizador no aplica la regla top-left de D3D, así que el contorno podía
        // quedar punteado. Si un píxel vacío tiene dos vecinos opacos, se rellena.
        px = CloseSeams(px, RW, RH);
        px = Downsample(px, RW, RH, SS);

        var dataOut = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < H; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * W * 4,
                    IntPtr.Add(dataOut.Scan0, y * dataOut.Stride), W * 4);
        }
        finally { bmp.UnlockBits(dataOut); }
        return bmp;
    }

    /// <summary>Reduce el buffer supersampleado al tamaño final promediando 2×2 y
    /// ponderando por alfa (el color no se oscurece contra el fondo transparente).</summary>
    private static byte[] Downsample(byte[] src, int W, int H, int ss)
    {
        int ow = W / ss, oh = H / ss;
        var dst = new byte[ow * oh * 4];
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                int sa = 0, sr = 0, sg = 0, sb = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        int j = ((y * ss + sy) * W + (x * ss + sx)) * 4;
                        int a = src[j + 3];
                        sa += a;
                        sb += src[j] * a; sg += src[j + 1] * a; sr += src[j + 2] * a;
                    }
                }
                int i = (y * ow + x) * 4;
                if (sa > 0)
                {
                    dst[i] = (byte)(sb / sa);
                    dst[i + 1] = (byte)(sg / sa);
                    dst[i + 2] = (byte)(sr / sa);
                }
                dst[i + 3] = (byte)(sa / (ss * ss));
            }
        }
        return dst;
    }

    /// <summary>Rellena los píxeles transparentes encerrados entre dos bordes opacos
    /// (grietas de un píxel del rasterizado) con el promedio de sus vecinos.</summary>
    private static byte[] CloseSeams(byte[] src, int W, int H)
    {
        var dst = (byte[])src.Clone();
        for (int y = 1; y < H - 1; y++)
        {
            for (int x = 1; x < W - 1; x++)
            {
                int i = (y * W + x) * 4;
                if (dst[i + 3] != 0) continue;
                int n = 0, sb = 0, sg = 0, sr = 0;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0);
                    int ny = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    int j = (ny * W + nx) * 4;
                    if (src[j + 3] == 0) continue;
                    sb += src[j]; sg += src[j + 1]; sr += src[j + 2]; n++;
                }
                if (n < 2) continue;
                dst[i] = (byte)(sb / n);
                dst[i + 1] = (byte)(sg / n);
                dst[i + 2] = (byte)(sr / n);
                dst[i + 3] = 255;
            }
        }
        return dst;
    }

    /// <summary>Misma distancia de cámara que <see cref="Cartridge3D"/>: encuadra el
    /// cuerpo real del dispositivo, no el canvas completo del cartucho.</summary>
    private static float FitDistance(DeviceKind kind, in Matrix4x4 world, float aspect, float zoom)
    {
        float tanV = MathF.Tan(22.5f * MathF.PI / 180f);
        float tanH = tanV * aspect;
        float hx = CartridgeModel.Width * 0.5f, hy = CartridgeModel.Height * 0.5f;
        if (kind != DeviceKind.Cartridge)
        {
            var (_, _, bw, bh, _) = CartridgeModel.DeviceBody(kind);
            hx = bw / (2f * CartridgeModel.FaceWidth);
            hy = bh / (2f * CartridgeModel.FaceWidth);
        }
        float hz = CartridgeModel.DeviceThickness(kind);
        float d = 0f;
        for (int i = 0; i < 8; i++)
        {
            var p = new Vector3((i & 1) == 0 ? -hx : hx,
                                (i & 2) == 0 ? -hy : hy,
                                (i & 4) == 0 ? -hz : hz);
            var q = Vector3.Transform(p, world);
            d = MathF.Max(d, q.Z + MathF.Abs(q.X) / (zoom * tanH));
            d = MathF.Max(d, q.Z + MathF.Abs(q.Y) / (zoom * tanV));
        }
        return d;
    }

    /// <summary>Muestreo bilineal con clamp, igual que el sampler del HLSL.</summary>
    private static void Sample(in Tex tex, float u, float v, out float r, out float g, out float b)
    {
        // Los campos se copian a locales: la función local no puede capturar un `in`.
        var px = tex.Px;
        int tw = tex.W, th = tex.H;
        float fx = Math.Clamp(u, 0f, 1f) * tw - 0.5f;
        float fy = Math.Clamp(v, 0f, 1f) * th - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;

        float At(int x, int y, int ch)
        {
            x = Math.Clamp(x, 0, tw - 1);
            y = Math.Clamp(y, 0, th - 1);
            return px[(y * tw + x) * 3 + ch];
        }

        r = At(x0, y0, 0) * (1 - tx) * (1 - ty) + At(x0 + 1, y0, 0) * tx * (1 - ty)
          + At(x0, y0 + 1, 0) * (1 - tx) * ty + At(x0 + 1, y0 + 1, 0) * tx * ty;
        g = At(x0, y0, 1) * (1 - tx) * (1 - ty) + At(x0 + 1, y0, 1) * tx * (1 - ty)
          + At(x0, y0 + 1, 1) * (1 - tx) * ty + At(x0 + 1, y0 + 1, 1) * tx * ty;
        b = At(x0, y0, 2) * (1 - tx) * (1 - ty) + At(x0 + 1, y0, 2) * tx * (1 - ty)
          + At(x0, y0 + 1, 2) * (1 - tx) * ty + At(x0 + 1, y0 + 1, 2) * tx * ty;
    }
}
