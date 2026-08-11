using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Monitor de sensores: una sola grilla con columnas Nombre | Actual | Mínimo | Máximo | Promedio.
/// Cada hardware es un desplegable (chevron) que contiene categorías ("Temperatura",
/// "Distancia a TjMax", "Uso", ...) también desplegables, y dentro cada sensor es una fila.
/// Todas las filas comparten la misma grilla, así las columnas quedan siempre alineadas.
/// </summary>
public sealed partial class SensoresPage : Page
{
    private readonly ISensorService _sensorService;
    private readonly ILoggingService _loggingService;
    private DispatcherQueueTimer? _pollTimer;
    private bool _polling;
    private int _nextRow;

    // Colores fijos para texto sobre el fondo oscuro de la app.
    private static readonly SolidColorBrush LightTextBrush = new(Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xED));
    private static readonly SolidColorBrush MutedBrush = new(Windows.UI.Color.FromArgb(255, 0xB4, 0xB4, 0xB4));

    // Línea de la grilla y rellenos de los encabezados (coinciden con el XAML).
    private SolidColorBrush _lineBrush = new(Windows.UI.Color.FromArgb(255, 0x3A, 0x42, 0x50));
    private SolidColorBrush _groupFill = new(Windows.UI.Color.FromArgb(255, 0x1B, 0x22, 0x2D));
    private SolidColorBrush _categoryFill = new(Windows.UI.Color.FromArgb(255, 0x14, 0x1A, 0x24));

    private const double ValueColumnWidth = 100;

    // Orden de presentación de las categorías dentro de cada hardware.
    private static readonly string[] CategoryOrder =
    {
        "Temperatura", "Distancia a TjMax", "Uso", "Velocidad de reloj",
        "Potencia", "Voltaje", "Ventiladores", "Datos", "Transferencia", "Otros"
    };

    // Íconos (Segoe MDL2 Assets) por tipo de hardware.
    private static readonly Dictionary<SensorGroupKind, string> GroupGlyphs = new()
    {
        [SensorGroupKind.Cpu] = "\uE950",          // Component
        [SensorGroupKind.Gpu] = "\uE7F4",          // TVMonitor
        [SensorGroupKind.Memory] = "\uE7F1",       // SDCard
        [SensorGroupKind.Motherboard] = "\uE772",  // Devices
        [SensorGroupKind.Storage] = "\uEDA2",      // HardDrive
        [SensorGroupKind.Other] = "\uE713"         // Setting
    };

    // Íconos por categoría.
    private static readonly Dictionary<string, string> CategoryGlyphs = new()
    {
        ["Temperatura"] = "\uE9CA",          // Frigid (termal)
        ["Distancia a TjMax"] = "\uE9CA",    // Frigid
        ["Uso"] = "\uE908",                 // FourBars (carga)
        ["Velocidad de reloj"] = "\uEC4A",   // SpeedHigh (tacómetro)
        ["Potencia"] = "\uE83E",            // BatteryCharging9 (flujo de energía)
        ["Voltaje"] = "\uE945",             // LightningBolt (símbolo de voltaje)
        ["Datos"] = "\uE965",               // MediaStorageTower (datos/almacenamiento)
        ["Transferencia"] = "\uE880",       // StatusDataTransfer
        ["Otros"] = "\uE713"                // Setting
    };

    // Una fila de sensor: los 4 TextBlock de valores + estado anterior (update incremental).
    private sealed class RowView
    {
        public required TextBlock CurrentBlock { get; init; }
        public required TextBlock MinBlock { get; init; }
        public required TextBlock MaxBlock { get; init; }
        public required TextBlock AvgBlock { get; init; }
        public string LastCurrent = "";
        public string LastMin = "";
        public string LastMax = "";
        public string LastAvg = "";
    }

    private sealed class CategoryView
    {
        public required FontIcon Chevron { get; init; }
        public required List<UIElement> HeaderCells { get; init; }
        public required List<UIElement> Cells { get; init; }
        public Dictionary<string, RowView> Sensors { get; } = new();
        public bool IsExpanded { get; set; } = true;
    }

    private sealed class GroupView
    {
        public required string Name { get; init; }
        public required FontIcon Chevron { get; init; }
        public bool IsExpanded { get; set; } = true;
        public Dictionary<string, CategoryView> Categories { get; } = new();
    }

    private readonly List<GroupView> _groups = new();

    public SensoresPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
        _sensorService = App.Services.GetRequiredService<ISensorService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        if (Resources.TryGetValue("SensorGridLineBrush", out var lineBrush) && lineBrush is SolidColorBrush sb)
            _lineBrush = sb;
        if (Resources.TryGetValue("SensorGroupFillBrush", out var g) && g is SolidColorBrush gb)
            _groupFill = gb;
        if (Resources.TryGetValue("SensorCategoryFillBrush", out var c) && c is SolidColorBrush cb)
            _categoryFill = cb;
        Unloaded += (s, e) => StopPolling();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SetStatusOnce($"{Feedback.RunningPrefix} Leyendo sensores...", Feedback.AccentBrush);
        StartPolling();
        _ = PollOnceAsync();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopPolling();
    }

    private void StartPolling()
    {
        if (_pollTimer == null)
        {
            _pollTimer = DispatcherQueue.CreateTimer();
            _pollTimer.Interval = TimeSpan.FromMilliseconds(500);
            _pollTimer.Tick += (s, e) => _ = PollOnceAsync();
        }
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        _pollTimer?.Stop();
    }

    private async Task PollOnceAsync()
    {
        // Sin solapamientos y con la ventana oculta en bandeja no se rastrean sensores.
        if (_polling) return;
        if (App.MainWindowInstance is { } w && !w.IsWindowVisible) return;
        _polling = true;
        try
        {
            var groups = await Task.Run(() => _sensorService.GetSensorGroups());
            UpdateUi(groups);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error leyendo sensores", ex);
            SetStatusOnce($"{Feedback.ErrorPrefix} No se pudieron leer los sensores: {ex.Message}", Feedback.ErrorBrush);
        }
        finally
        {
            _polling = false;
        }
    }

    private void UpdateUi(List<SensorGroupInfo> groups)
    {
        if (!_sensorService.IsAvailable)
        {
            SetStatusOnce($"{Feedback.ErrorPrefix} LibreHardwareMonitor no disponible en este equipo. Verificá que la app corre como administrador.",
                Feedback.ErrorBrush);
            return;
        }

        if (groups.Count == 0)
        {
            SetStatusOnce($"{Feedback.InfoPrefix} No se detectaron sensores en este equipo.", Feedback.WarningBrush);
            return;
        }

        // Sin mensaje permanente: con datos cargados el estado se oculta.
        SensorsStatusText.Visibility = Visibility.Collapsed;

        foreach (var group in groups)
        {
            var gv = _groups.FirstOrDefault(g => g.Name == group.Name);
            if (gv == null)
            {
                gv = BuildGroupHeader(group.Name, group.Kind);
                _groups.Add(gv);
            }

            foreach (var category in group.Categories.OrderBy(c => RankOf(c.Name)))
            {
                var cv = EnsureCategory(gv, category.Name);

                foreach (var sensor in category.Sensors)
                {
                    if (!cv.Sensors.TryGetValue(sensor.Name, out var row))
                        row = AddSensorRow(gv, cv, sensor.Name);

                    var current = Fmt(sensor.Current, sensor.Unit);
                    var min = Fmt(sensor.Min, sensor.Unit);
                    var max = Fmt(sensor.Max, sensor.Unit);
                    var avg = Fmt(sensor.Average, sensor.Unit);

                    if (row.LastCurrent != current) { row.LastCurrent = current; row.CurrentBlock.Text = current; }
                    if (row.LastMin != min) { row.LastMin = min; row.MinBlock.Text = min; }
                    if (row.LastMax != max) { row.LastMax = max; row.MaxBlock.Text = max; }
                    if (row.LastAvg != avg) { row.LastAvg = avg; row.AvgBlock.Text = avg; }

                    var color = ColorFor(sensor);
                    if (!ReferenceEquals(row.CurrentBlock.Foreground, color))
                        row.CurrentBlock.Foreground = color;
                }

                // Recién creadas (o nuevas) respetan el estado de colapso actual.
                ApplyCategoryVisibility(gv, cv);
            }
        }
    }

    // ---- Construcción del árbol (una sola grilla, filas ordenadas por índice) ----

    private GroupView BuildGroupHeader(string groupName, SensorGroupKind kind)
    {
        var chevron = CreateChevron();
        var icon = new FontIcon
        {
            Glyph = GroupGlyphs.TryGetValue(kind, out var g) ? g : "\uE713",
            FontSize = 14,
            Foreground = Feedback.AccentBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var nameBlock = new TextBlock
        {
            Text = groupName,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = LightTextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(chevron);
        panel.Children.Add(icon);
        panel.Children.Add(nameBlock);

        var header = new Border
        {
            Background = _groupFill,
            BorderBrush = _lineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = panel
        };
        Grid.SetColumnSpan(header, 5);
        AddRow(header);
        SensorsGrid.Children.Add(header);

        var gv = new GroupView { Name = groupName, Chevron = chevron };
        header.Tapped += (s, e) => ToggleGroup(gv);
        return gv;
    }

    private CategoryView EnsureCategory(GroupView gv, string categoryName)
    {
        if (gv.Categories.TryGetValue(categoryName, out var existing))
            return existing;

        var chevron = CreateChevron();
        var nameBlock = new TextBlock
        {
            Text = categoryName,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = CreateCategoryIcon(categoryName);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(chevron);
        panel.Children.Add(icon);
        panel.Children.Add(nameBlock);

        var header = new Border
        {
            Background = _categoryFill,
            BorderBrush = _lineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 6, 10, 6),
            Child = panel
        };
        Grid.SetColumnSpan(header, 5);
        AddRow(header);
        SensorsGrid.Children.Add(header);

        var cv = new CategoryView
        {
            Chevron = chevron,
            HeaderCells = new List<UIElement> { header },
            Cells = new List<UIElement>()
        };
        gv.Categories[categoryName] = cv;
        header.Tapped += (s, e) => ToggleCategory(gv, cv);
        return cv;
    }

    private RowView AddSensorRow(GroupView gv, CategoryView cv, string sensorName)
    {
        var row = NextRow();

        var name = new TextBlock
        {
            Text = sensorName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = MutedBrush
        };
        var nameCell = CreateCell(name, rightLine: true, column: 0, leftPad: 44);
        Grid.SetRow(nameCell, row);
        SensorsGrid.Children.Add(nameCell);

        var current = CreateValueBlock();
        var currentCell = CreateCell(current, rightLine: true, column: 1);
        Grid.SetRow(currentCell, row);
        SensorsGrid.Children.Add(currentCell);

        var min = CreateValueBlock();
        var minCell = CreateCell(min, rightLine: true, column: 2);
        Grid.SetRow(minCell, row);
        SensorsGrid.Children.Add(minCell);

        var max = CreateValueBlock();
        var maxCell = CreateCell(max, rightLine: true, column: 3);
        Grid.SetRow(maxCell, row);
        SensorsGrid.Children.Add(maxCell);

        var avg = CreateValueBlock();
        var avgCell = CreateCell(avg, rightLine: false, column: 4);
        Grid.SetRow(avgCell, row);
        SensorsGrid.Children.Add(avgCell);

        cv.Cells.Add(nameCell);
        cv.Cells.Add(currentCell);
        cv.Cells.Add(minCell);
        cv.Cells.Add(maxCell);
        cv.Cells.Add(avgCell);

        var rv = new RowView { CurrentBlock = current, MinBlock = min, MaxBlock = max, AvgBlock = avg };
        cv.Sensors[sensorName] = rv;
        return rv;
    }

    // Ícono de categoría: FontIcon monocromo de Segoe MDL2 Assets, salvo
    // Ventiladores, que es un fan de PC dibujado con geometría vectorial
    // (no existe un glyph de ventilador en MDL2).
    private static FrameworkElement CreateCategoryIcon(string categoryName)
    {
        if (categoryName == "Ventiladores")
            return CreateFanIcon();

        return new FontIcon
        {
            Glyph = CategoryGlyphs.TryGetValue(categoryName, out var g) ? g : "\uE713",
            FontSize = 12,
            Foreground = Feedback.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    // ---- Ventilador de PC (PathIcon con geometría generada) ----

    private static PathIcon CreateFanIcon() => new()
    {
        Data = CreateFanGeometry(),
        Width = 14,
        Height = 14,
        Foreground = Feedback.MutedBrush
    };

    // Fan de PC en un viewbox de 24x24: aro exterior, 4 aspas curvadas y buje central.
    private static Geometry CreateFanGeometry()
    {
        const double cx = 12, cy = 12;
        const double hubR = 2.6;    // radio del buje central
        const double rimR = 10.4;   // radio del aro exterior
        const double half = 13;     // media apertura angular de cada aspa
        const double sweep = 24;    // barrido de la pala (dirección de giro)
        const double rIn = hubR + 0.2;
        const double rOut = rimR - 0.45;

        var geo = new PathGeometry();

        // Aro exterior.
        geo.Figures.Add(CircleFigure(cx, cy, rimR));

        // Aspas.
        for (int k = 0; k < 4; k++)
            geo.Figures.Add(FanBladeFigure(cx, cy, rIn, rOut, k * 90.0 + 45.0, half, sweep));

        // Buje central (va al final para cubrir las raíces de las aspas).
        geo.Figures.Add(CircleFigure(cx, cy, hubR));
        return geo;
    }

    private static PathFigure CircleFigure(double cx, double cy, double r) => new()
    {
        StartPoint = new Point(cx + r, cy),
        IsClosed = true,
        IsFilled = true,
        Segments =
        {
            new ArcSegment { Point = new Point(cx - r, cy), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise },
            new ArcSegment { Point = new Point(cx + r, cy), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise }
        }
    };

    private static PathFigure FanBladeFigure(double cx, double cy,
        double rIn, double rOut, double centerDeg, double half, double sweep)
    {
        var p1 = Polar(cx, cy, rIn, centerDeg - half);      // raíz, lado trasero
        var p2 = Polar(cx, cy, rIn, centerDeg + half);      // raíz, lado delantero
        var p3 = Polar(cx, cy, rOut, centerDeg + half + sweep);  // punta delantera
        var p4 = Polar(cx, cy, rOut, centerDeg - half + sweep);  // punta trasera
        var midR = (rIn + rOut) / 2;

        var figure = new PathFigure
        {
            StartPoint = p1,
            IsClosed = true,
            IsFilled = true
        };

        // Borde interno junto al buje.
        figure.Segments.Add(new ArcSegment { Point = p2, Size = new Size(rIn, rIn), SweepDirection = SweepDirection.Clockwise });
        // Borde delantero barrido (curva hacia el sentido de giro).
        figure.Segments.Add(new BezierSegment
        {
            Point1 = Polar(cx, cy, midR, centerDeg + half + sweep * 0.35),
            Point2 = Polar(cx, cy, midR, centerDeg + half + sweep * 0.75),
            Point3 = p3
        });
        // Borde exterior.
        figure.Segments.Add(new ArcSegment { Point = p4, Size = new Size(rOut, rOut), SweepDirection = SweepDirection.Clockwise });
        // Borde trasero (curva de vuelta al buje).
        figure.Segments.Add(new BezierSegment
        {
            Point1 = Polar(cx, cy, midR, centerDeg - half + sweep * 0.75),
            Point2 = Polar(cx, cy, midR, centerDeg - half + sweep * 0.35),
            Point3 = p1
        });
        return figure;
    }

    private static Point Polar(double cx, double cy, double r, double deg)
    {
        var rad = deg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }

    private Border CreateCell(TextBlock content, bool rightLine, int column = 0, double leftPad = 8)
    {
        var cell = new Border
        {
            BorderBrush = _lineBrush,
            BorderThickness = new Thickness(0, 0, rightLine ? 1 : 0, 1),
            Padding = new Thickness(leftPad, 3, 8, 3),
            Child = content
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    // Una fila nueva por elemento: WinUI no crea filas implícitas por Grid.Row, así que
    // cada índice necesita su RowDefinition explícita o todo se superpone en la misma celda.
    private void AddRow(FrameworkElement element)
    {
        var row = NextRow();
        Grid.SetRow(element, row);
    }

    private int NextRow()
    {
        var row = _nextRow++;
        while (SensorsGrid.RowDefinitions.Count <= row)
            SensorsGrid.RowDefinitions.Add(new RowDefinition());
        return row;
    }

    private static FontIcon CreateChevron() => new()
    {
        Glyph = "\uE76C",
        FontSize = 10,
        Foreground = MutedBrush,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static void SetChevron(FontIcon chevron, bool open)
    {
        chevron.RenderTransform = open ? new RotateTransform { Angle = 90, CenterX = 5, CenterY = 5 } : null;
    }

    // ---- Colapso/expansión ----

    private void ToggleGroup(GroupView gv) => SetGroupExpanded(gv, !gv.IsExpanded);

    private void ToggleCategory(GroupView gv, CategoryView cv) => SetCategoryExpanded(gv, cv, !cv.IsExpanded);

    private void SetGroupExpanded(GroupView gv, bool expanded)
    {
        gv.IsExpanded = expanded;
        SetChevron(gv.Chevron, expanded);
        foreach (var cv in gv.Categories.Values)
            ApplyCategoryVisibility(gv, cv);
    }

    private void SetCategoryExpanded(GroupView gv, CategoryView cv, bool expanded)
    {
        cv.IsExpanded = expanded;
        SetChevron(cv.Chevron, expanded);
        ApplyCategoryVisibility(gv, cv);
    }

    // ---- Acciones de la barra de herramientas ----

    private void ExpandAllGroupsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var gv in _groups)
            SetGroupExpanded(gv, true);
    }

    private void CollapseAllGroupsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var gv in _groups)
            SetGroupExpanded(gv, false);
    }

    private void ExpandAllCategoriesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var gv in _groups)
            foreach (var cv in gv.Categories.Values)
                SetCategoryExpanded(gv, cv, true);
    }

    private void CollapseAllCategoriesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var gv in _groups)
            foreach (var cv in gv.Categories.Values)
                SetCategoryExpanded(gv, cv, false);
    }

    private void ApplyCategoryVisibility(GroupView gv, CategoryView cv)
    {
        var groupOpen = gv.IsExpanded;
        foreach (var cell in cv.HeaderCells)
            cell.Visibility = groupOpen ? Visibility.Visible : Visibility.Collapsed;

        var catOpen = groupOpen && cv.IsExpanded;
        foreach (var cell in cv.Cells)
            cell.Visibility = catOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Helpers ----

    private static int RankOf(string categoryName)
    {
        var index = Array.IndexOf(CategoryOrder, categoryName);
        return index < 0 ? CategoryOrder.Length : index;
    }

    private static TextBlock CreateValueBlock() => new()
    {
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Feedback.MutedBrush
    };

    private static string Fmt(double? value, string unit)
    {
        if (!value.HasValue) return "—";
        var v = value.Value;
        // Relojes: GHz cuando superan 1000 MHz (5.30 GHz en vez de 5300 MHz), igual que Núcleos.
        if (unit == "MHz" && v >= 1000)
            return $"{v / 1000.0:F2} GHz";
        var text = Math.Abs(v) >= 100 ? v.ToString("F0") : v.ToString("F1");
        return string.IsNullOrEmpty(unit) ? text : $"{text} {unit}";
    }

    private static SolidColorBrush ColorFor(SensorReadingInfo sensor)
    {
        if (sensor.Current is not double v) return Feedback.MutedBrush;

        switch (sensor.Kind)
        {
            case SensorReadingKind.Temperature:
                if (v >= 85) return Feedback.ErrorBrush;
                if (v >= 70) return Feedback.WarningBrush;
                return Feedback.SuccessBrush;
            case SensorReadingKind.Load:
                if (v >= 95) return Feedback.ErrorBrush;
                if (v >= 80) return Feedback.WarningBrush;
                return Feedback.SuccessBrush;
            case SensorReadingKind.Power:
            case SensorReadingKind.Voltage:
                return Feedback.AccentBrush;
            default:
                return Feedback.MutedBrush;
        }
    }

    private void SetStatusOnce(string fullText, SolidColorBrush brush)
    {
        if (SensorsStatusText.Tag as string == fullText) return;
        SensorsStatusText.Tag = fullText;
        Feedback.Set(SensorsStatusText, fullText, brush, persistent: true);
    }
}
