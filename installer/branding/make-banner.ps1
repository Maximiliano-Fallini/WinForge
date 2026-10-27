# ============================================================
# Genera installer\branding\banner.bmp: la franja de 370x64 que UI.wxs pone
# arriba de cada diálogo del instalador.
#
# Windows Installer no permite estilar los diálogos por CSS ni cambiar el fondo:
# el único lienzo real es un Bitmap de la tabla Control. Por eso la identidad
# (logo, marca, BETA y el filo de acento) vive en esta imagen y no en el XAML.
#
# Uso:  powershell -ExecutionPolicy Bypass -File installer\branding\make-banner.ps1
# ============================================================
Add-Type -AssemblyName System.Drawing

$outDir   = $PSScriptRoot
$root     = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$logoPath = Join-Path $root 'src\WHPO.UI\logos\WinForge.png'

$W = 370; $H = 64

$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# Fondo: degradado diagonal del azul noche del tema oscuro al azul profundo.
$rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(255, 0x0B, 0x16, 0x26),
    [System.Drawing.Color]::FromArgb(255, 0x15, 0x33, 0x55),
    20.0)
$g.FillRectangle($brush, $rect)
$brush.Dispose()

# Halo suave a la derecha: profundidad sin dibujar nada reconocible.
$halo = New-Object System.Drawing.Drawing2D.GraphicsPath
$halo.AddEllipse(250, -30, 190, 130)
$haloBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($halo)
$haloBrush.CenterColor = [System.Drawing.Color]::FromArgb(38, 0x5A, 0xA8, 0xFF)
$haloBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0x5A, 0xA8, 0xFF))
$g.FillPath($haloBrush, $halo)
$haloBrush.Dispose(); $halo.Dispose()

# Logo, proporción intacta dentro de 44x44.
$logo = [System.Drawing.Image]::FromFile($logoPath)
$box = 44
$ratio = [Math]::Min($box / $logo.Width, $box / $logo.Height)
$lw = [int]($logo.Width * $ratio)
$lh = [int]($logo.Height * $ratio)
$g.DrawImage($logo, 14 + [int](($box - $lw) / 2), 10 + [int](($box - $lh) / 2), $lw, $lh)
$logo.Dispose()

# Marca.
$fontMark = New-Object System.Drawing.Font('Segoe UI Semibold', 21, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fontSub  = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$white    = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xEA, 0xF2, 0xFF))
$sub      = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x8F, 0xA6, 0xC4))
$g.DrawString('WINFORGE', $fontMark, $white, 68, 9)
$g.DrawString('Windows optimizer', $fontSub, $sub, 70, 37)
$fontMark.Dispose(); $fontSub.Dispose(); $white.Dispose(); $sub.Dispose()

# Chip "BETA" a la derecha: llena el aire del banner con información real.
$chipW = 44; $chipH = 20; $chipX = $W - $chipW - 16; $chipY = 22
$chipPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 9
$chipPath.AddArc($chipX, $chipY, $r, $r, 180, 90)
$chipPath.AddArc($chipX + $chipW - $r, $chipY, $r, $r, 270, 90)
$chipPath.AddArc($chipX + $chipW - $r, $chipY + $chipH - $r, $r, $r, 0, 90)
$chipPath.AddArc($chipX, $chipY + $chipH - $r, $r, $r, 90, 90)
$chipPath.CloseFigure()
$chipFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 0x3D, 0x8B, 0xFD))
$chipLine = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 0x3D, 0x8B, 0xFD), 1)
$g.FillPath($chipFill, $chipPath)
$g.DrawPath($chipLine, $chipPath)
$chipFont = New-Object System.Drawing.Font('Segoe UI Semibold', 10, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$chipText = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x9C, 0xC4, 0xFF))
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = [System.Drawing.StringAlignment]::Center
$fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
$g.DrawString('BETA', $chipFont, $chipText, (New-Object System.Drawing.RectangleF($chipX, $chipY, $chipW, $chipH)), $fmt)
$fmt.Dispose(); $chipFont.Dispose(); $chipText.Dispose(); $chipLine.Dispose(); $chipFill.Dispose(); $chipPath.Dispose()

# Filo inferior de acento.
$accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x3D, 0x8B, 0xFD))
$g.FillRectangle($accent, 0, $H - 3, $W, 3)
$accent.Dispose()

$g.Dispose()
$out = Join-Path $outDir 'banner.bmp'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()

"Banner generado: $out"
