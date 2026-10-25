using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Image = Microsoft.UI.Xaml.Controls.Image;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace WHPO_UI.Controls;

/// <summary>
/// Editor de carátula de un juego: elige una imagen, deja moverla y hacer zoom dentro
/// de la ventana real de arte del cartucho (512×454) y guarda un PNG recortado en la
/// carpeta de personalizaciones. No toca la biblioteca: la página decide qué hacer con
/// <see cref="OutputPath"/> al cerrar el diálogo.
/// </summary>
public sealed class ArtCropDialog : ContentDialog
{
    // Ventana de arte del cartucho: mismo aspecto que CartridgeModel.ArtW × ArtH.
    private const double ViewW = 420;
    private const double ViewH = ViewW * WHPO_UI.Controls.CartridgeModel.ArtH / WHPO_UI.Controls.CartridgeModel.ArtW;
    // Se guarda más grande de lo que se muestra para que el zoom no se vea blando.
    private const int OutW = 1024;
    private const int OutH = OutW * WHPO_UI.Controls.CartridgeModel.ArtH / WHPO_UI.Controls.CartridgeModel.ArtW;

    private readonly string _exe;
    private readonly Func<string?>? _pickImage;
    private readonly Grid _viewport;
    private readonly Image _image;
    private readonly Slider _zoom;
    private readonly TextBlock _sourceLabel;
    private readonly TextBlock _error;
    private string? _sourcePath;
    private int _imgW, _imgH;
    private double _scale, _tx, _ty;
    private double _zoomFactor = 1;
    private bool _panning;
    private bool _suppressZoom;
    private Point _panStart;
    private double _panTx, _panTy;

    public ArtCropDialog(string exe, string gameName, string? initialPath, bool hasCustomArt, Func<string?>? pickImage = null)
    {
        _exe = exe;
        _pickImage = pickImage;
        _sourcePath = initialPath;
        Title = I18n.T("Carátula de {0}", gameName);
        PrimaryButtonText = I18n.T("Aplicar");
        if (hasCustomArt) SecondaryButtonText = I18n.T("Quitar personalizada");
        CloseButtonText = I18n.T("Cancelar");
        DefaultButton = ContentDialogButton.Primary;

        _image = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };

        _viewport = new Grid
        {
            Width = ViewW,
            Height = ViewH,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 8, 9, 12)),
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, ViewW, ViewH) }
        };
        _viewport.Children.Add(_image);
        _viewport.PointerPressed += OnPressed;
        _viewport.PointerMoved += OnMoved;
        _viewport.PointerReleased += OnReleased;
        _viewport.PointerCaptureLost += (_, _) => _panning = false;

        _zoom = new Slider
        {
            Minimum = 100,
            Maximum = 260,
            Value = 100,
            StepFrequency = 5,
            Width = ViewW,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _zoom.ValueChanged += (_, e) =>
        {
            if (_suppressZoom || _imgW <= 0) return;
            SetZoom(e.NewValue / 100.0, keepCenter: true);
        };

        _sourceLabel = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = WHPO_UI.ThemeBrushes.Get("SecondaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _error = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x7A, 0x7A)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var choose = new Button
        {
            Content = I18n.T("Elegir otra imagen…"),
            FontSize = 12,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(8)
        };
        choose.Click += async (_, _) => await PickAnotherAsync();

        var hint = new TextBlock
        {
            Text = I18n.T("Arrastrá la imagen para centrarla y usá el zoom para acercarla. El recorte se ve tal cual en el cartucho."),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = WHPO_UI.ThemeBrushes.Get("SecondaryTextBrush")
        };

        var zoomRow = new Grid { ColumnSpacing = 8 };
        zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var zoomText = new TextBlock
        {
            Text = I18n.T("Zoom"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = WHPO_UI.ThemeBrushes.Get("PrimaryTextBrush")
        };
        Grid.SetColumn(zoomText, 0);
        Grid.SetColumn(_zoom, 1);
        zoomRow.Children.Add(zoomText);
        zoomRow.Children.Add(_zoom);

        var sourceRow = new Grid { ColumnSpacing = 8 };
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_sourceLabel, 0);
        Grid.SetColumn(choose, 1);
        sourceRow.Children.Add(_sourceLabel);
        sourceRow.Children.Add(choose);

        Content = new StackPanel
        {
            Width = ViewW,
            Spacing = 10,
            Children = { _viewport, zoomRow, hint, sourceRow, _error }
        };

        PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (_sourcePath == null || _imgW <= 0)
                {
                    ShowError(I18n.T("Elegí una imagen primero."));
                    args.Cancel = true;
                    return;
                }
                SaveCrop();
            }
            catch (Exception ex)
            {
                ShowError(I18n.T("No se pudo preparar la carátula: {0}", ex.Message));
                args.Cancel = true;
            }
        };
    }

    /// <summary>Ruta del PNG recortado; válida solo si el diálogo cerró con «Aplicar».</summary>
    public string? OutputPath { get; private set; }

    /// <summary>Imagen original usada para el recorte (se guarda para poder reajustarla).</summary>
    public string? SourcePath => _sourcePath;

    /// <summary>Carga la imagen inicial. Se llama antes de ShowAsync, cuando el XamlRoot
    /// todavía no hace falta porque el diálogo no está en el árbol visual.</summary>
    public async Task LoadInitialAsync()
    {
        if (!string.IsNullOrWhiteSpace(_sourcePath) && File.Exists(_sourcePath))
            await LoadImageAsync(_sourcePath);
    }

    private async Task PickAnotherAsync()
    {
        try
        {
            string? picked = _pickImage?.Invoke();
            if (string.IsNullOrWhiteSpace(picked) || !File.Exists(picked)) return;
            await LoadImageAsync(picked);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task LoadImageAsync(string path)
    {
        try
        {
            _error.Visibility = Visibility.Collapsed;
            using (var probe = System.Drawing.Image.FromFile(path))
            {
                _imgW = probe.Width;
                _imgH = probe.Height;
            }
            if (_imgW <= 0 || _imgH <= 0) throw new InvalidOperationException("La imagen no tiene tamaño.");

            var bitmap = new BitmapImage();
            using (var stream = File.OpenRead(path))
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

            _image.Source = bitmap;
            _sourcePath = path;
            _sourceLabel.Text = System.IO.Path.GetFileName(path);
            _zoomFactor = 1;
            _suppressZoom = true;
            _zoom.Value = 100;
            _suppressZoom = false;
            LayoutImage(resetPosition: true);
        }
        catch (Exception ex)
        {
            _sourcePath = null;
            _image.Source = null;
            _imgW = _imgH = 0;
            ShowError(I18n.T("No se pudo abrir la imagen: {0}", ex.Message));
        }
    }

    private void SetZoom(double factor, bool keepCenter)
    {
        if (_imgW <= 0) return;
        double oldScale = _scale;
        double centerX = (ViewW * 0.5 - _tx) / Math.Max(oldScale, 0.0001);
        double centerY = (ViewH * 0.5 - _ty) / Math.Max(oldScale, 0.0001);

        _zoomFactor = Math.Clamp(factor, 1.0, 2.6);
        LayoutImage(resetPosition: false);

        if (keepCenter && oldScale > 0)
        {
            _tx = ViewW * 0.5 - centerX * _scale;
            _ty = ViewH * 0.5 - centerY * _scale;
            ClampAndApply();
        }
    }

    private void LayoutImage(bool resetPosition)
    {
        if (_imgW <= 0 || _imgH <= 0) return;
        // La imagen SIEMPRE cubre la ventana: el cartucho nunca muestra franjas vacías.
        double cover = Math.Max(ViewW / _imgW, ViewH / _imgH);
        _scale = cover * _zoomFactor;
        _image.Width = _imgW * _scale;
        _image.Height = _imgH * _scale;
        if (resetPosition)
        {
            _tx = (ViewW - _image.Width) * 0.5;
            _ty = (ViewH - _image.Height) * 0.5;
        }
        ClampAndApply();
    }

    private void ClampAndApply()
    {
        double minX = Math.Min(0, ViewW - _image.Width);
        double minY = Math.Min(0, ViewH - _image.Height);
        _tx = Math.Clamp(_tx, minX, 0);
        _ty = Math.Clamp(_ty, minY, 0);
        _image.Margin = new Thickness(_tx, _ty, 0, 0);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_imgW <= 0) return;
        _panStart = e.GetCurrentPoint(_viewport).Position;
        _panTx = _tx;
        _panTy = _ty;
        _panning = true;
        _viewport.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetCurrentPoint(_viewport).Position;
        _tx = _panTx + (p.X - _panStart.X);
        _ty = _panTy + (p.Y - _panStart.Y);
        ClampAndApply();
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        _viewport.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void SaveCrop()
    {
        if (_sourcePath == null) throw new InvalidOperationException("Falta la imagen.");
        string target = GameLook.TargetArtPath(_exe);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        string temp = target + ".tmp";

        double k = OutW / ViewW;
        try
        {
            using (var source = System.Drawing.Image.FromFile(_sourcePath))
            using (var bmp = new Bitmap(OutW, OutH, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Black);
                g.DrawImage(source, new RectangleF(
                    (float)(_tx * k), (float)(_ty * k),
                    (float)(_image.Width * k), (float)(_image.Height * k)));
                bmp.Save(temp, ImageFormat.Png);
            }
            File.Move(temp, target, overwrite: true);
            OutputPath = target;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }
}
