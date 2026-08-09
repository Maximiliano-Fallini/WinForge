using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class SistemaPage : Page
{
    private readonly ISystemInfoService _systemInfoService;
    private readonly ILoggingService _loggingService;
    private bool _dataLoaded;
    private bool _monitoringActive;
    private long _totalStorage;
    private string _cpuCoresText = "--";

    // El skeleton debe permanecer visible un mínimo de tiempo para que el
    // efecto de carga se aprecie, aunque los datos lleguen en milisegundos.
    private static readonly System.Diagnostics.Stopwatch SkeletonWatch = System.Diagnostics.Stopwatch.StartNew();
    private const int MinSkeletonVisibleMs = 550;

    // Brush reutilizable para chips (evita acceder a Resources que puede fallar).
    // Los chips se mantienen oscuros en ambos temas; su texto es claro explícito.
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush ChipBrush = new(Windows.UI.Color.FromArgb(255, 0x2F, 0x33, 0x3B));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush ChipTextBrush = new(Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xED));

    public SistemaPage()
    {
        try
        {
            InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();
            _loggingService = App.Services.GetRequiredService<ILoggingService>();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }
        catch (Exception ex)
        {
            if (DebugText != null)
                DebugText.Text = $"Error init: {ex.Message}";
            try { _loggingService?.LogError($"Error en constructor SistemaPage: {ex}", ex); } catch { }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_dataLoaded) return;
        try
        {
            StartSkeletonPulse();
            await LoadInitialDataAsync();
            _dataLoaded = true;
            StartMonitoring();
        }
        catch (Exception ex)
        {
            HideAllSkeletons();
            DebugText.Visibility = Visibility.Visible;
            DebugText.Text = $"Error: {ex.Message}";
            try { _loggingService.LogError($"Error en OnLoaded SistemaPage: {ex}", ex); } catch { }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopMonitoring();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        StartMonitoring();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopMonitoring();
    }

    private async Task LoadInitialDataAsync()
    {
        // CPU + características
        _loggingService.LogInfo("SistemaPage: cargando CPU...");
        var cpuInfo = await Task.Run(() => _systemInfoService.GetCpuInfo());
        _loggingService.LogInfo($"SistemaPage: CPU obtenida: {cpuInfo.Name}");

        CpuNameText.Text = cpuInfo.Name.Trim();
        SetCpuLogo(cpuInfo.Name);

        _cpuCoresText = $"{cpuInfo.PhysicalCores} núcleos / {cpuInfo.LogicalProcessors} hilos";
        CpuDetailsText.Text = $"{FormatFrequency(cpuInfo.CurrentFrequencyMHz)} · {_cpuCoresText}";

        // Instrucciones del procesador en chips estilo CPU-Z
        InstructionsPanel.Items.Clear();
        var instructions = cpuInfo.InstructionSet.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (instructions.Length == 0)
        {
            InstructionsPanel.Items.Add(new TextBlock { Text = cpuInfo.Architecture == "x64" ? "x86-64" : "x86", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            foreach (var instr in instructions)
            {
                var chip = new Border
                {
                    Background = ChipBrush,
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 6, 6),
                    Child = new TextBlock
                    {
                        Text = instr.Trim(),
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                        TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = ChipTextBrush
                    }
                };
                InstructionsPanel.Items.Add(chip);
            }
        }

        // CPU e instrucciones listas: revelar sus cards
        await RevealCardAsync(CpuSkeleton, CpuContent);
        await RevealCardAsync(InstructionsSkeleton, InstructionsContent);

        // RAM
        _loggingService.LogInfo("SistemaPage: cargando RAM...");
        var memInfo = await Task.Run(() => _systemInfoService.GetMemoryInfo());
        _loggingService.LogInfo($"SistemaPage: RAM obtenida: {FormatBytes(memInfo.TotalBytes)}");
        RamTotalText.Text = FormatBytes(memInfo.TotalBytes);
        RamUsageText.Text = $"{memInfo.UsagePercent:F1}%";
        RamUsageBar.Value = Math.Max(0, Math.Min(100, memInfo.UsagePercent));
        RamDetailsText.Text = $"{FormatBytes(memInfo.UsedBytes)} usados · {FormatBytes(memInfo.AvailableBytes)} libre";
        await RevealCardAsync(RamSkeleton, RamContent);

        // GPU (preferir dedicada)
        _loggingService.LogInfo("SistemaPage: cargando GPU...");
        var gpus = await Task.Run(() => _systemInfoService.GetGpuInfo());
        _loggingService.LogInfo($"SistemaPage: GPUs obtenidas: {gpus.Count}");
        if (gpus.Count > 0)
        {
            var primaryGpu = gpus.FirstOrDefault(g => !g.Name.Contains("Radeon(TM)", StringComparison.OrdinalIgnoreCase) && !g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                            ?? gpus[0];

            GpuNameText.Text = primaryGpu.Name.Trim();
            SetGpuLogo(primaryGpu.Name);
            GpuUsageText.Text = "0%";
            GpuUsageBar.Value = 0;
            GpuTempText.Text = "";
            GpuVramText.Text = primaryGpu.DedicatedMemoryBytes > 0
                ? $"VRAM: {FormatVram(primaryGpu.DedicatedMemoryBytes)}"
                : "";
        }
        await RevealCardAsync(GpuSkeleton, GpuContent);

        // Almacenamiento
        _loggingService.LogInfo("SistemaPage: cargando discos...");
        var disks = await Task.Run(() => _systemInfoService.GetDiskInfo());
        _loggingService.LogInfo($"SistemaPage: Discos obtenidos: {disks.Count}");
        long totalStorage = disks.Sum(d => d.TotalSizeBytes);
        _totalStorage = totalStorage;
        long totalFreeStorage = disks.Sum(d => d.FreeSpaceBytes);
        StorageTotalText.Text = FormatBytes(totalStorage);
        double storageUsage = totalStorage > 0 ? (double)(totalStorage - totalFreeStorage) / totalStorage * 100 : 0;
        StorageUsageText.Text = $"{storageUsage:F1}%";
        StorageUsageBar.Value = Math.Max(0, Math.Min(100, storageUsage));
        StorageFreeText.Text = $"Libre: {FormatBytes(totalFreeStorage)}";
        await RevealCardAsync(StorageSkeleton, StorageContent);

        // Placa base
        _loggingService.LogInfo("SistemaPage: cargando placa base...");
        var boardInfo = await Task.Run(() => _systemInfoService.GetBoardInfo());
        BoardManufacturerText.Text = boardInfo.Manufacturer;
        BoardProductText.Text = boardInfo.Product;
        BoardVersionText.Text = boardInfo.Version;
        await RevealCardAsync(BoardSkeleton, BoardContent);

        // BIOS
        _loggingService.LogInfo("SistemaPage: cargando BIOS...");
        var biosInfo = await Task.Run(() => _systemInfoService.GetBiosInfo());
        BiosManufacturerText.Text = biosInfo.Manufacturer;
        BiosVersionText.Text = biosInfo.SMBIOSBIOSVersion;
        BiosDateText.Text = biosInfo.ReleaseDate;
        await RevealCardAsync(BiosSkeleton, BiosContent);

        // Sistema Operativo
        _loggingService.LogInfo("SistemaPage: cargando SO...");
        var osInfo = await Task.Run(() => _systemInfoService.GetOsInfo());
        OsNameText.Text = osInfo.Name;
        OsDetailsText.Text = $"{osInfo.Version} / Build {osInfo.BuildNumber} · {osInfo.Architecture} · {osInfo.ComputerName}";
        await RevealCardAsync(OsSkeleton, OsContent);

        _loggingService.LogInfo("SistemaPage: datos cargados");
    }

    // ===== Skeleton de carga por tarjeta =====
    private readonly List<Storyboard> _skeletonStoryboards = new();

    private FrameworkElement?[] AllSkeletons => new FrameworkElement?[]
    {
        CpuSkeleton, GpuSkeleton, RamSkeleton, StorageSkeleton, OsSkeleton,
        InstructionsSkeleton, BoardSkeleton, BiosSkeleton
    };

    private FrameworkElement?[] AllContents => new FrameworkElement?[]
    {
        CpuContent, GpuContent, RamContent, StorageContent, OsContent,
        InstructionsContent, BoardContent, BiosContent
    };

    /// <summary>
    /// Anima los bloques del skeleton con un pulso de opacidad hasta que cada card se revela.
    /// IMPORTANTE: se anima cada bloque (Rectangle), NO el panel contenedor: el panel tiene
    /// fondo opaco que tapa el contenido real, y si se animara su opacidad, el fondo se
    /// volvería translúcido y el HUD de la card quedaría visible a través del skeleton.
    /// </summary>
    private void StartSkeletonPulse()
    {
        try
        {
            foreach (var el in AllSkeletons)
            {
                if (el is not Panel panel) continue;
                foreach (var child in panel.Children)
                {
                    if (child is not FrameworkElement fe) continue;
                    var sb = new Storyboard
                    {
                        RepeatBehavior = RepeatBehavior.Forever,
                        AutoReverse = true
                    };
                    var anim = new DoubleAnimation
                    {
                        From = 1.0,
                        To = 0.35,
                        Duration = new Duration(TimeSpan.FromMilliseconds(900))
                    };
                    Storyboard.SetTarget(anim, fe);
                    Storyboard.SetTargetProperty(anim, "Opacity");
                    sb.Children.Add(anim);
                    _skeletonStoryboards.Add(sb);
                    sb.Begin();
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudo iniciar la animación de carga: {ex.Message}");
        }
    }

    private void HideSkeleton(FrameworkElement? skeleton)
    {
        if (skeleton == null) return;
        skeleton.Visibility = Visibility.Collapsed;

        // Si ya no queda ningún skeleton visible, detener las animaciones de pulso
        if (AllSkeletons.All(s => s == null || s.Visibility != Visibility.Visible))
        {
            foreach (var sb in _skeletonStoryboards)
            {
                try { sb.Stop(); } catch { }
            }
            _skeletonStoryboards.Clear();
        }
    }

    /// <summary>
    /// Revela una card: oculta su skeleton y muestra el contenido real.
    /// El contenido real arranca Collapsed, por lo que el skeleton nunca deja
    /// ver el HUD atrás (sin depender de z-order ni de opacidad del fondo).
    /// Garantiza un mínimo de tiempo visible del skeleton para que el efecto
    /// de carga se aprecie incluso cuando los datos llegan rápido.
    /// </summary>
    private async Task RevealCardAsync(FrameworkElement? skeleton, FrameworkElement? content)
    {
        long elapsed = SkeletonWatch.ElapsedMilliseconds;
        if (elapsed < MinSkeletonVisibleMs)
            await Task.Delay((int)(MinSkeletonVisibleMs - elapsed));
        RevealCard(skeleton, content);
    }

    private void RevealCard(FrameworkElement? skeleton, FrameworkElement? content)
    {
        HideSkeleton(skeleton);
        if (content != null)
            content.Visibility = Visibility.Visible;
    }

    private void HideAllSkeletons()
    {
        foreach (var el in AllSkeletons)
            HideSkeleton(el);
        // En caso de error, mostrar el contenido (quedará con los valores por defecto "--")
        foreach (var c in AllContents)
        {
            if (c != null)
                c.Visibility = Visibility.Visible;
        }
        foreach (var sb in _skeletonStoryboards)
        {
            try { sb.Stop(); } catch { }
        }
        _skeletonStoryboards.Clear();
    }

    private void StartMonitoring()
    {
        if (_monitoringActive) return;
        _monitoringActive = true;

        _systemInfoService.OnMetricsUpdated += OnMetricsUpdatedHandler;
        _systemInfoService.StartMonitoring(1000);
        _loggingService.LogInfo("SistemaPage: monitoreo iniciado");
    }

    private void StopMonitoring()
    {
        if (!_monitoringActive) return;
        _monitoringActive = false;

        _systemInfoService.OnMetricsUpdated -= OnMetricsUpdatedHandler;
        _systemInfoService.StopMonitoring();
        _loggingService.LogInfo("SistemaPage: monitoreo detenido");
    }

    private void OnMetricsUpdatedHandler(SystemMetrics metrics)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // CPU: uso + frecuencia real + temperatura
            CpuUsageText.Text = $"{metrics.CpuUsagePercent:F1}%";
            CpuUsageBar.Value = Math.Max(0, Math.Min(100, metrics.CpuUsagePercent));
            CpuDetailsText.Text = $"{FormatFrequency(metrics.CpuFrequencyMHz)} · {_cpuCoresText}";
            CpuTempText.Text = metrics.CpuTemperatureCelsius > 0
                ? $"· {metrics.CpuTemperatureCelsius:F0}°C"
                : "";

            // RAM: uso + usado/disponible
            RamUsageText.Text = $"{metrics.MemoryUsagePercent:F1}%";
            RamUsageBar.Value = Math.Max(0, Math.Min(100, metrics.MemoryUsagePercent));
            var totalMem = metrics.MemoryUsagePercent > 0
                ? (long)(metrics.MemoryUsedBytes / (metrics.MemoryUsagePercent / 100.0))
                : metrics.MemoryUsedBytes;
            RamDetailsText.Text = $"{FormatBytes(metrics.MemoryUsedBytes)} usados · {FormatBytes(Math.Max(0, totalMem - metrics.MemoryUsedBytes))} libre";

            // GPU: uso + temperatura (como el Administrador de tareas)
            if (metrics.Gpu != null)
            {
                GpuUsageText.Text = $"{metrics.Gpu.UsagePercent:F1}%";
                GpuUsageBar.Value = Math.Max(0, Math.Min(100, metrics.Gpu.UsagePercent));
                GpuTempText.Text = metrics.Gpu.TemperatureCelsius > 0
                    ? $"· {metrics.Gpu.TemperatureCelsius:F0}°C"
                    : "";
            }

            // Almacenamiento: uso agregado (se refresca cada ~10s con la caché de discos)
            if (metrics.Disks.Count > 0)
            {
                double storageUsage = metrics.Disks.Average(d => d.UsagePercent);
                StorageUsageText.Text = $"{storageUsage:F1}%";
                StorageUsageBar.Value = Math.Max(0, Math.Min(100, storageUsage));
                if (_totalStorage > 0)
                    StorageFreeText.Text = $"Libre: {FormatBytes((long)(_totalStorage * (1 - storageUsage / 100.0)))}";
            }
        });
    }

    private void SetCpuLogo(string cpuName)
    {
        try
        {
            var upper = cpuName.ToUpperInvariant();
            string logoPath;
            if (upper.Contains("AMD"))
                logoPath = "ms-appx:///logos/AmdLogo.png";
            else if (upper.Contains("INTEL"))
                logoPath = "ms-appx:///logos/IntelLogo.png";
            else
                return;

            CpuLogoImage.Source = new BitmapImage(new Uri(logoPath));
        }
        catch { }
    }

    private void SetGpuLogo(string gpuName)
    {
        try
        {
            var upper = gpuName.ToUpperInvariant();
            string logoPath;
            if (upper.Contains("NVIDIA"))
                logoPath = "ms-appx:///logos/NvidiaLogo.png";
            else if (upper.Contains("AMD") || upper.Contains("RADEON"))
                logoPath = "ms-appx:///logos/AmdLogo.png";
            else
                return;

            GpuLogoImage.Source = new BitmapImage(new Uri(logoPath));
        }
        catch { }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string FormatVram(long bytes)
    {
        var gb = bytes / 1073741824.0;
        if (gb >= 1) return $"{gb:F1} GB";
        return $"{bytes / 1048576.0:F0} MB";
    }

    private static string FormatFrequency(double mhz)
    {
        if (mhz <= 0) return "--";
        if (mhz >= 1000) return $"{mhz / 1000.0:F2} GHz";
        return $"{mhz:F0} MHz";
    }

}
