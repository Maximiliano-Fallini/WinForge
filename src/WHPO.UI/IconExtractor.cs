using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WHPO_UI;

/// <summary>
/// Extrae el ícono de máxima resolución disponible de un ejecutable usando la API
/// de Shell de Windows: SHGetImageList con SHIL_JUMBO (hasta 256 px) — la misma
/// lista de íconos que usa el Explorador de archivos.
///
/// Por qué existe: Icon.ExtractAssociatedIcon solo devuelve el ícono de 32 px y
/// estirarlo a 64 px en la card se ve borroso. Con esta API los exes modernos
/// devuelven 256×256 y el downscale a 64 px queda nítido. Los exes viejos (solo
/// íconos de 16/32 px) devuelven su mejor tamaño — es el mismo comportamiento
/// del Explorador.
/// </summary>
internal static class IconExtractor
{
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int SHIL_JUMBO = 0x4;
    private const uint ILD_TRANSPARENT = 0x1;
    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    /// <summary>
    /// Ícono genérico de .exe (el de "aplicación" del Explorador) SIN tocar el disco:
    /// SHGFI_USEFILEATTRIBUTES sobre un nombre ficticio hace que el shell devuelva el
    /// ícono asociado a la extensión .exe. Es el fallback para procesos cuyo exe no se
    /// puede leer (protegidos, sin MainModule) o que no traen recurso de ícono propio.
    /// </summary>
    public static Bitmap? ExtractDefaultExeIcon()
    {
        try
        {
            var sfi = new SHFILEINFO();
            IntPtr sysList = SHGetFileInfo("placeholder.exe", FILE_ATTRIBUTE_NORMAL, ref sfi,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
            if (sysList == IntPtr.Zero || sfi.iIcon < 0) return null;

            Guid iid = IID_IImageList;
            if (SHGetImageList(SHIL_JUMBO, ref iid, out IImageList il) != 0) return null;
            if (il.GetIcon(sfi.iIcon, ILD_TRANSPARENT, out IntPtr hIcon) != 0) return null;
            try
            {
                using var icon = Icon.FromHandle(hIcon);
                var bmp = icon.ToBitmap();
                if (bmp == null) return null;
                var trimmed = TrimTransparentMargins(bmp);
                if (!ReferenceEquals(trimmed, bmp)) bmp.Dispose();
                return trimmed;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Bitmap con el ícono de alta resolución del exe (o null si falla).</summary>
    public static Bitmap? ExtractHighResIcon(string filePath)
    {
        try
        {
            var sfi = new SHFILEINFO();
            IntPtr sysList = SHGetFileInfo(filePath, 0, ref sfi,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
            if (sysList == IntPtr.Zero || sfi.iIcon < 0) return null;

            Guid iid = IID_IImageList;
            if (SHGetImageList(SHIL_JUMBO, ref iid, out IImageList il) != 0) return null;
            if (il.GetIcon(sfi.iIcon, ILD_TRANSPARENT, out IntPtr hIcon) != 0) return null;
            try
            {
                using var icon = Icon.FromHandle(hIcon);
                var bmp = icon.ToBitmap();
                if (bmp == null) return null;
                var trimmed = TrimTransparentMargins(bmp);
                if (!ReferenceEquals(trimmed, bmp)) bmp.Dispose();
                return trimmed;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recorta los márgenes totalmente transparentes de un ícono extraído del shell.
    /// Los exes con recurso de ícono chico (16/32 px) aparecen en la lista JUMBO de
    /// 256×256 con el dibujo chico en una ESQUINA y el resto de la tela transparente:
    /// estirar esa tela completa haría el ícono diminuto en la fila (7×7 px en 32×32).
    /// Recortar la zona visible hace que el ícono llene su caja al escalarlo, igual
    /// que el Explorador lo muestra centrado y llenando. Devuelve una copia recortada,
    /// o la MISMA instancia si no hay márgenes que quitar.
    /// </summary>
    public static Bitmap TrimTransparentMargins(Bitmap source)
    {
        try
        {
            int minX = source.Width, minY = source.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                {
                    if (source.GetPixel(x, y).A > 8)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

            // Todo transparente, o ya llena la tela: nada que recortar.
            if (maxX < 0
                || (minX == 0 && minY == 0 && maxX == source.Width - 1 && maxY == source.Height - 1))
                return source;

            return source.Clone(new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1), source.PixelFormat);
        }
        catch
        {
            return source;
        }
    }

    /// <summary>
    /// Ícono propio del juego en su carpeta (.ico): los juegos VIEJOS a menudo no
    /// tienen recurso de ícono en el exe (o solo 16/32 px) pero traen un .ico
    /// (game.ico, icon.ico o el mismo nombre que el exe) para los accesos directos.
    /// Devuelve la ruta solo si es un candidato CONFIABLE (coincide con el nombre del
    /// exe, es un nombre típico del juego, o es el único .ico de la carpeta); el exe
    /// sigue siendo la fuente principal, esto es un respaldo para juegos antiguos.
    /// </summary>
    public static string? FindConfidentLocalIcon(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var exeName = Path.GetFileNameWithoutExtension(exePath);

            // 1) .ico con el mismo nombre que el exe (máxima confianza).
            var match = Directory.EnumerateFiles(dir, "*.ico", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileNameWithoutExtension(f).Equals(exeName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f =>
                {
                    try { return new FileInfo(f).Length; } catch { return 0L; }
                })
                .FirstOrDefault();
            if (match != null) return match;

            // 2) Nombres típicos de ícono del juego, en la carpeta del exe y (si el
            //    exe está anidado, ej. Windows\game.exe) en la raíz de instalación.
            string[] roots = { dir };
            var parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(parent)
                && !parent.Equals(dir, StringComparison.OrdinalIgnoreCase))
                roots = new[] { dir, parent };
            string[] preferred = { "game", "icon", "folder", "logo", "app" };
            foreach (var root in roots)
            {
                foreach (var p in preferred)
                {
                    var f = Path.Combine(root, p + ".ico");
                    if (File.Exists(f)) return f;
                }
            }

            // 3) Si hay UN solo .ico en la carpeta, es casi seguro el del juego.
            var all = Directory.EnumerateFiles(dir, "*.ico", SearchOption.TopDirectoryOnly).ToList();
            if (all.Count == 1) return all[0];
            return null;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Interfaz COM de la lista de imágenes del shell. Se declaran todos los métodos
    /// en el orden exacto de la vtable; solo GetIcon se invoca realmente.
    /// </summary>
    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        int ReplaceIcon(int i, IntPtr hicon, out int pi);
        int SetOverlayImage(int iImage, int iOverlay);
        int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        int AddMasked(IntPtr hbmImage, uint crMask, out int pi);
        int Draw(ref int pimldp);
        int Remove(int i);
        int GetIcon(int i, uint flags, out IntPtr picon);
        int GetImageInfo(int i, out int pImageInfo);
        int Copy(int iDst, IImageList punkSrc, int iSrc, uint uFlags);
        int Merge(int i1, IImageList punkMerge, int i2, int dx, int dy, ref Guid riid, out IntPtr ppv);
        int Clone(ref Guid riid, out IntPtr ppv);
        int GetImageRect(int i, out int prc);
        int GetIconSize(out int cx, out int cy);
        int SetIconSize(int cx, int cy);
        int GetImageCount(out int pi);
        int SetImageCount(int uNewCount);
        int SetBkColor(uint clrBk, out uint pclr);
        int GetBkColor(out uint pclr);
        int BeginDrag(int iTrack, int dxHotspot, int dyHotspot);
        int EndDrag();
        int DragEnter(IntPtr hwndLock, int x, int y);
        int DragLeave(IntPtr hwndLock);
        int DragMove(int x, int y);
        int SetDragCursorImage(int iDrag, IntPtr hicon, int x, int y);
        int DragShowNolock(int fShow);
        int GetDragImage(out int ppt, out int pptHotspot, ref Guid riid, out IntPtr ppv);
        int GetItemFlags(int i, out uint dwFlags);
        int GetOverlayImage(int iOverlay, out int piIndex);
    }
}
