using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class ConfiguracionPage : Page
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IStartupService _startupService;
    private readonly ILoggingService _loggingService;
    private readonly IInstalledGamesService _installedGamesService;
    private readonly IAppUpdateService _appUpdateService;
    private bool _isLoading;
    private bool _updatingNavAll;

    // Pestaña seleccionada del navbar interno (0=Inicio, 1=Actualizaciones, 2=Caché, 3=Navegación, 4=Desarrollo).
    private int _selectedTabIndex;

    // Último resultado del chequeo de actualizaciones (para el botón "Instalar").
    private AppUpdateInfo? _updateInfo;
    private OnboardingSimulatorWindow? _onboardingSimulator;

    // ---- Menú de navegación: pestañas del menú lateral y su clave de configuración ----
    private static readonly (string Tag, string Label)[] NavTabs =
    {
        ("sistema", "Sistema"),
        ("red", "Red"),
        ("memoria", "Memoria"),
        ("temporizador", "Resolución del Temporizador"),
        ("nucleos", "Núcleos y Plan de energía"),
        ("overlay", "Overlay de métricas"),
        ("procesos", "Biblioteca de juegos"),
        ("procesosvivos", "Gestión de procesos"),
        ("teclado", "Teclado y Macros"),
        ("autoclicker", "Autoclicker"),
        ("estabilidad", "Test de estabilidad"),
        ("sensores", "Monitor de sensores"),
        ("optimizaciones", "Optimizaciones"),
        ("debloat", "Debloat"),
        ("herramientas", "Herramientas y funciones"),
        ("panelwindows", "Panel de Windows"),
        ("reparacion", "Reparación"),
        ("actualizaciones", "Windows Update"),
        ("limpieza", "Limpieza del dispositivo")
    };

    private readonly Dictionary<string, CheckBox> _navCheckBoxes = new();

    // Sección de desarrollo: visible solo si la build es más nueva que la release publicada.
    public ConfiguracionPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _themeService = App.Services.GetRequiredService<IThemeService>();
        _startupService = App.Services.GetRequiredService<IStartupService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _installedGamesService = App.Services.GetRequiredService<IInstalledGamesService>();
        _appUpdateService = App.Services.GetRequiredService<IAppUpdateService>();

        Loaded += OnLoaded;

        // La página usa caché de navegación: estas suscripciones se hacen una sola vez.
        // Sincroniza el indicador de la pestaña interna "Actualizaciones" con el estado
        // global del chequeo (MainWindow dispara el evento al completar el check).
        if (App.MainWindowInstance != null)
            App.MainWindowInstance.AppUpdateStateChanged += OnAppUpdateStateChanged;

        // Al cambiar de idioma, actualizar el resumen del menú de navegación (la
        // página usa caché de navegación, así que se suscribe una sola vez).
        I18n.LanguageChanged += OnLanguageChanged;
        MinimizeToTrayToggle.Toggled += OnMinimizeToTrayToggled;
        OptimizePerformanceToggle.Toggled += OnOptimizePerformanceToggled;
        DeveloperLogsToggle.Toggled += OnDeveloperLogsToggled;
        LaunchAtStartupToggle.Toggled += OnLaunchAtStartupToggled;
        StartMinimizedToggle.Toggled += OnStartMinimizedToggled;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        try
        {
            MinimizeToTrayToggle.IsOn = _settingsService.Get("window.minimizeToTray", true);
            OptimizePerformanceToggle.IsOn = _settingsService.Get("tray.optimizePerformance", true);
            DeveloperLogsToggle.IsOn = _settingsService.Get("logging.developerLogs", false);
            LaunchAtStartupToggle.IsOn = _startupService.IsEnabled();
            StartMinimizedToggle.IsOn = _settingsService.Get("window.startMinimized", false);

            // Normaliza el valor del registro de inicio (agrega/quita el flag de
            // minimizado según la opción actual) y limpia feedbacks viejos.
            SyncStartupRegistration();
            UpdateStartMinimizedHint();
            Feedback.Set(StartupFeedbackText, null);
            Feedback.Set(MinimizedFeedbackText, null);

            BuildNavMenu();
            ApplyConfigTabsLanguage();
            SelectTheme(_themeService.CurrentTheme);
            ApplyThemeOptionsLanguage();
            UpdateDeveloperLogsSize();
            UpdateCacheSize();
            App.MainWindowInstance?.UpdateTrayStatus();
            ApplyUpdatesTabBadge();

            // Chequeo inicial de actualizaciones al abrir la página (silencioso:
            // si falla la red, solo se apaga el estado de "Buscando...").
            _ = CheckForUpdatesAsync();

            UpdateDevelopmentToolsVisibility();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void SelectTheme(AppTheme theme)
    {
        var tag = theme.ToString();
        var item = ThemeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(comboItem => string.Equals(comboItem.Tag?.ToString(), tag, StringComparison.Ordinal));
        ThemeComboBox.SelectedItem = item;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        if (Enum.TryParse<AppTheme>(tag, out var theme))
        {
            _themeService.SetTheme(theme);
        }
    }

    private void OnMinimizeToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settingsService.Set("window.minimizeToTray", MinimizeToTrayToggle.IsOn);
        _settingsService.Save();
    }

    private void OnOptimizePerformanceToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settingsService.Set("tray.optimizePerformance", OptimizePerformanceToggle.IsOn);
        _settingsService.Save();
        App.MainWindowInstance?.UpdateTrayStatus();
    }

    private void OnDeveloperLogsToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        bool enabled = DeveloperLogsToggle.IsOn;
        _settingsService.Set("logging.developerLogs", enabled);
        _settingsService.Save();
        // Aplica al instante: deja/escribe el app.log según el estado del toggle.
        _loggingService.SetFileLoggingEnabled(enabled);
        UpdateDeveloperLogsSize();
    }

    private async void DeleteLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Borrar logs"),
            Content = I18n.T("¿Borrar todos los logs de desarrollo? Esta acción no se puede deshacer."),
            PrimaryButtonText = I18n.T("Borrar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        long freed = _loggingService.GetLogFilesSize();
        _loggingService.DeleteLogFiles();
        UpdateDeveloperLogsSize();
        Feedback.Success(DeleteLogsFeedbackText, I18n.T("Logs borrados: {0} liberados.", FormatBytes(freed)));
    }

    private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = _loggingService.LogDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ConfiguracionPage: abrir carpeta de logs: {ex.Message}");
        }
    }

    private void UpdateDeveloperLogsSize()
    {
        long size = _loggingService.GetLogFilesSize();
        DeveloperLogsSizeText.Text = I18n.T("Tamaño de los logs: {0}", FormatBytes(size));
        // Sin logs no hay nada que borrar: el botón queda deshabilitado (y el feedback limpio).
        DeleteLogsButton.IsEnabled = size > 0;
        if (size == 0) DeleteLogsFeedbackText.Text = "";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    // ===== Caché de la aplicación =====
    // Cubre todo lo que la app guarda regenerable en disco: banners e íconos de
    // juegos (GestionarProcesosPage) y la biblioteca de juegos cacheada. NO toca
    // settings.json (configuración) ni los logs (botón "Borrar logs").

    private static string WhpoCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WHPO");

    private void UpdateCacheSize()
    {
        CacheSizeText.Text = I18n.T("Tamaño de la caché: {0}", FormatBytes(GetCacheSizeBytes()));
    }

    private static long GetCacheSizeBytes()
    {
        long total = 0;
        try
        {
            var bannerDir = GestionarProcesosPage.BannerCacheDir;
            if (Directory.Exists(bannerDir))
                total += Directory.EnumerateFiles(bannerDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            var gamesCache = Path.Combine(WhpoCacheDir, "gamescache.json");
            var fi = new FileInfo(gamesCache);
            if (fi.Exists) total += fi.Length;
            var tmp = new FileInfo(gamesCache + ".tmp");
            if (tmp.Exists) total += tmp.Length;
        }
        catch { }
        return total;
    }

    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Limpiar caché"),
            Content = I18n.T("¿Borrar toda la caché de la aplicación? Se volverán a descargar los banners de juegos y la biblioteca se re-escanneará. La configuración no se toca."),
            PrimaryButtonText = I18n.T("Limpiar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            long freed = GetCacheSizeBytes();

            // Banners e íconos de juegos: se re-descargan/re-extraen al abrir la biblioteca.
            var bannerDir = GestionarProcesosPage.BannerCacheDir;
            if (Directory.Exists(bannerDir))
                Directory.Delete(bannerDir, recursive: true);
                // Íconos de procesos en memoria: apuntan a archivos recién borrados;
                // invalidar para que se re-extraigan sin reiniciar la app.
                ProcesosPage.ClearIconCache();

            // Biblioteca cacheada (memoria + disco): re-escaneo en la próxima visita.
            _installedGamesService.ClearCache();

            UpdateCacheSize();
            Feedback.Success(ClearCacheFeedbackText, I18n.T("Caché borrada: {0} liberados.", FormatBytes(freed)));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ConfiguracionPage: limpiar caché: {ex.Message}");
            Feedback.Error(ClearCacheFeedbackText, I18n.T("No se pudo limpiar la caché: {0}", ex.Message));
        }
    }

    private void ResetWindowPositionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Borra window.x/y/width/height y re-centra al instante: sin posición
            // guardada, RestoreOrCenterWindow vuelve al 1400x800 centrado.
            App.MainWindowInstance?.ResetWindowPosition();
            Feedback.Success(ResetWindowPositionFeedbackText, I18n.T("Posición de la ventana restablecida."));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ConfiguracionPage: restablecer posición de ventana: {ex.Message}");
            Feedback.Error(ResetWindowPositionFeedbackText, I18n.T("No se pudo restablecer la posición: {0}", ex.Message));
        }
    }

    /// <summary>
    /// Reescribe el valor del registro Run para que coincida con la opción de
    /// minimizado actual. Normaliza instalaciones viejas (el valor quedó sin el
    /// flag cuando la opción se activó antes de que el flag existiera).
    /// </summary>
    private void SyncStartupRegistration()
    {
        try
        {
            if (_startupService.IsEnabled())
                _startupService.SetEnabled(true, StartMinimizedToggle.IsOn);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ConfiguracionPage: no se pudo sincronizar el inicio automático: {ex.Message}");
        }
    }

    /// <summary>
    /// Muestra un aviso bajo "Iniciar minimizado" cuando la opción está activa pero
    /// no puede tener efecto (el inicio con Windows está apagado): así no parece
    /// que el toggle no funcionara.
    /// </summary>
    private void UpdateStartMinimizedHint()
    {
        StartMinimizedHint.Visibility = (StartMinimizedToggle.IsOn && !LaunchAtStartupToggle.IsOn)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnLaunchAtStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var result = _startupService.SetEnabled(LaunchAtStartupToggle.IsOn, StartMinimizedToggle.IsOn);
        if (!result.Success)
        {
            // Se revierte el toggle para reflejar el estado real y se muestra por qué falló.
            _isLoading = true;
            LaunchAtStartupToggle.IsOn = !LaunchAtStartupToggle.IsOn;
            _isLoading = false;
            Feedback.Error(StartupFeedbackText, I18n.T("No se pudo cambiar el inicio con Windows: {0}", result.Message));
        }
        else
        {
            Feedback.Success(StartupFeedbackText, LaunchAtStartupToggle.IsOn
                ? I18n.T("Inicio con Windows activado.")
                : I18n.T("Inicio con Windows desactivado."));
        }

        UpdateStartMinimizedHint();
    }

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        bool minimized = StartMinimizedToggle.IsOn;

        // Dependencias de "Iniciar minimizado": la bandeja (para ocultarse ahí) y el
        // inicio con Windows (la única forma de que Windows lo lance al iniciar sesión).
        if (minimized && !MinimizeToTrayToggle.IsOn)
        {
            _isLoading = true;
            MinimizeToTrayToggle.IsOn = true;
            _isLoading = false;
            _settingsService.Set("window.minimizeToTray", true);
        }

        _settingsService.Set("window.startMinimized", minimized);

        if (minimized && !LaunchAtStartupToggle.IsOn)
        {
            // Activar minimizado implica activar el inicio automático (si no, no
            // tendría efecto): se escribe el valor del registro con el flag.
            var result = _startupService.SetEnabled(true, true);
            if (result.Success)
            {
                _isLoading = true;
                LaunchAtStartupToggle.IsOn = true;
                _isLoading = false;
                Feedback.Success(StartupFeedbackText, I18n.T("Inicio con Windows activado."));
            }
            else
            {
                // No se pudo activar el inicio automático: revertir minimizado.
                _isLoading = true;
                StartMinimizedToggle.IsOn = false;
                _isLoading = false;
                _settingsService.Set("window.startMinimized", false);
                Feedback.Error(MinimizedFeedbackText, I18n.T("No se pudo cambiar el inicio con Windows: {0}", result.Message));
            }
        }
        else if (_startupService.IsEnabled())
        {
            // El inicio ya está activo: reescribir el valor con/sin el flag para que
            // el próximo arranque de Windows respete el nuevo estado.
            var result = _startupService.SetEnabled(true, minimized);
            if (!result.Success)
                Feedback.Error(StartupFeedbackText, I18n.T("No se pudo cambiar el inicio con Windows: {0}", result.Message));
        }

        _settingsService.Save();
        UpdateStartMinimizedHint();

        if (StartMinimizedToggle.IsOn)
            Feedback.Success(MinimizedFeedbackText, I18n.T("Se abrirá minimizado al iniciar sesión."));
    }

    // ===================== Herramientas de desarrollo =====================

    private void UpdateDevVersionInfo()
    {
        try
        {
            if (DevVersionInfoText == null) return;
            var info = _updateInfo ?? App.MainWindowInstance?.LatestUpdate;
            if (info == null) return;

            var current = info.CurrentVersion ?? AppUpdateService.CurrentVersion();

            // Estado legible (no el nombre crudo del enum): si no se pudo contactar
            // a GitHub, el motivo típico es no tener internet y eso es lo que se
            // muestra — no un "Error" genérico.
            var status = info.Status switch
            {
                AppUpdateStatus.NoConnection => I18n.T("Falta de conexión a internet"),
                AppUpdateStatus.UpToDate => I18n.T("WinForge está actualizado."),
                AppUpdateStatus.UpdateAvailable => I18n.T("¡Hay una versión nueva disponible!"),
                AppUpdateStatus.DevelopmentBuild => I18n.T("Versión en desarrollo"),
                _ => I18n.T("No se pudo comprobar actualizaciones.")
            };

            var lines = new List<string>
            {
                I18n.T("Versión instalada: {0}", current),
                I18n.T("Estado del chequeo: {0}", status)
            };
            if (!string.IsNullOrEmpty(info.LatestVersion))
            {
                // Solo si el chequeo detectó la release: si no, no hay contra qué
                // comparar y las líneas "última release"/"por delante/por detrás"
                // serían inventadas.
                var latest = info.LatestVersion;
                var ahead = false;
                try { ahead = Version.Parse(current).CompareTo(Version.Parse(latest!)) > 0; } catch { }
                var relation = I18n.T(ahead ? "por delante" : "por detrás");
                lines.Add(I18n.T("Última release en GitHub: {0}", latest));
                lines.Add(I18n.T("La build de desarrollo está {0} de la release publicada.", relation));
            }
            DevVersionInfoText.Text = string.Join(Environment.NewLine, lines);
        }
        catch { DevVersionInfoText.Text = I18n.T("No se pudo obtener la información de versión."); }
    }

    private void ResetWindowPosButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance != null)
        {
            // Llamar al método que ya existe en MainWindow para restablecer posición
            App.MainWindowInstance.ResetWindowPosition();
            Feedback.Success(DevFeedbackText, I18n.T("Posición de ventana restablecida."));
        }
    }

    private void SimulateOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        // Abre el simulador del asistente de primera configuración (tema → aviso
        // gratis): se ve idéntico al onboarding real pero no persiste nada.
        if (_onboardingSimulator != null)
        {
            _onboardingSimulator.Activate();
            return;
        }

        _onboardingSimulator = new OnboardingSimulatorWindow();
        _onboardingSimulator.Closed += (_, _) => _onboardingSimulator = null;
        _onboardingSimulator.Activate();
    }

    private void UpdateDevelopmentToolsVisibility()
    {
        // La pestaña depende exclusivamente de la comparación contra la última
        // release publicada, no del sufijo beta/alpha de la versión.
        var update = _updateInfo ?? App.MainWindowInstance?.LatestUpdate;
        bool isDevelopmentBuild = update?.Status == AppUpdateStatus.DevelopmentBuild;

        DevelopmentTabItem.Visibility = isDevelopmentBuild ? Visibility.Visible : Visibility.Collapsed;
        DevSection.Visibility = isDevelopmentBuild ? Visibility.Visible : Visibility.Collapsed;
        DevTab.Visibility = isDevelopmentBuild && ReferenceEquals(ConfigTabs.SelectedItem, DevelopmentTabItem)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Si una comprobación posterior deja de considerar esta build como de
        // desarrollo, no dejamos seleccionada una pestaña que acaba de ocultarse.
        if (!isDevelopmentBuild && ReferenceEquals(ConfigTabs.SelectedItem, DevelopmentTabItem))
        {
            _selectedTabIndex = 0;
            ConfigTabs.SelectedItem = ConfigTabs.Items.OfType<SelectorBarItem>().FirstOrDefault();
        }

        if (isDevelopmentBuild)
            UpdateDevVersionInfo();

        ApplyTabVisibility();
    }

    // ===================== Actualizaciones de la app =====================

    private void OnAppUpdateStateChanged()
    {
        ApplyUpdatesTabBadge();
        UpdateDevelopmentToolsVisibility();
    }

    /// <summary>
    /// Muestra u oculta el indicador de "actualización disponible" sobre la pestaña
    /// interna "Actualizaciones" de Configuración. Se sincroniza con el estado global
    /// del chequeo (MainWindow), de modo que queda visible incluso antes de abrir y
    /// consultar la pestaña. SelectorBarItem no expone InfoBadge, así que se marca con
    /// un ícono de descarga junto al texto.
    /// </summary>
    private void ApplyUpdatesTabBadge()
    {
        try
        {
            if (ConfigTabs == null) return;
            var item = ConfigTabs.Items.OfType<SelectorBarItem>().Skip(1).FirstOrDefault(); // "Actualizaciones"
            if (item == null) return;

            bool show = (_updateInfo ?? App.MainWindowInstance?.LatestUpdate) is { Available: true };
            if (show && item.Icon == null)
            {
                item.Icon = new FontIcon
                {
                    Glyph = "\uE896", // Descargar
                    FontFamily = UiSymbolFontFamily(),
                    FontSize = 12
                };
            }
            else if (!show)
            {
                item.Icon = null;
            }
        }
        catch
        {
            // El badge es decorativo: un fallo aquí nunca debe romper la página.
        }
    }

    private static Microsoft.UI.Xaml.Media.FontFamily UiSymbolFontFamily()
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var r)
            && r is Microsoft.UI.Xaml.Media.FontFamily ff)
        {
            return ff;
        }
        return new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons");
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync();
    }

    /// <summary>
    /// Consulta las releases del repositorio y muestra si hay una versión más
    /// nueva. Corre en Task.Run para no congelar la UI durante la consulta HTTP.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateFeedbackText.Text = "";
        UpdateStatusText.Text = I18n.T("Buscando actualizaciones...");
        UpdateDetailText.Text = "";

        try
        {
            var info = await Task.Run(() => _appUpdateService.CheckForUpdatesAsync());
            _updateInfo = info;
            ApplyUpdateUi(info);
            UpdateDevelopmentToolsVisibility();
        }
        catch (Exception ex)
        {
            _updateInfo = null;
            ApplyUpdateUi(new AppUpdateInfo { Status = AppUpdateStatus.Error, CurrentVersion = AppUpdateService.CurrentVersion() });
            UpdateDevelopmentToolsVisibility();
            Feedback.Error(UpdateFeedbackText, I18n.T("Error de red o servicio no disponible: {0}", ex.Message));
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Aplica el resultado del chequeo a toda la UI de la pestaña (badge de estado,
    /// mensajes y tarjetas de versiones). Cada estado usa su propio ícono y color:
    /// actualizado → check verde · disponible → descarga con acento ·
    /// build en desarrollo → info · error → alerta.
    /// </summary>
    private void ApplyUpdateUi(AppUpdateInfo info)
    {
        string glyph;
        string status;
        string detail;
        Microsoft.UI.Xaml.Media.Brush brush;

        switch (info.Status)
        {
            case AppUpdateStatus.UpdateAvailable:
                glyph = "\uE896"; // Descargar
                brush = ThemeBrushes.Get("AccentBrush");
                status = I18n.T("¡Hay una versión nueva disponible!");
                detail = I18n.T("Se puede instalar la v{0} sobre la actual.", info.LatestVersion);
                break;
            case AppUpdateStatus.UpToDate:
                glyph = "\uE73E"; // Verificado
                brush = ThemeBrushes.Get("SuccessBrush");
                status = I18n.T("WinForge está actualizado.");
                detail = I18n.T("La versión instalada (v{0}) es la más reciente publicada.", info.CurrentVersion);
                break;
            case AppUpdateStatus.DevelopmentBuild:
                glyph = "\uE946"; // Información
                brush = ThemeBrushes.Get("MutedBrush");
                status = I18n.T("Versión en desarrollo");
                detail = I18n.T("Esta build va adelantada: la última release publicada es la v{0}.",
                    info.LatestVersion ?? info.CurrentVersion);
                break;
            case AppUpdateStatus.NoConnection:
                glyph = "\uE946"; // Información
                brush = ThemeBrushes.Get("MutedBrush");
                status = I18n.T("Falta de conexión a internet");
                detail = I18n.T("No se pudo detectar la última versión publicada. Revisá tu conexión a internet e intentá de nuevo.");
                break;
            default: // Error
                glyph = "\uE946"; // Información (alerta)
                brush = ThemeBrushes.Get("ErrorBrush");
                status = I18n.T("No se pudo comprobar actualizaciones.");
                detail = I18n.T("Revisá tu conexión a internet e intentá de nuevo.");
                break;
        }

        UpdateStateIcon.Glyph = glyph;
        UpdateStateIcon.Foreground = brush;
        UpdateStatusText.Foreground = brush;
        UpdateStatusText.Text = status;
        UpdateDetailText.Text = detail;
        UpdateDetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        InstalledVersionValue.Text = string.IsNullOrEmpty(info.CurrentVersion) ? "—" : "v" + info.CurrentVersion;
        LatestVersionValue.Text = string.IsNullOrEmpty(info.LatestVersion) ? "—" : "v" + info.LatestVersion;
        ReleaseNotesHref.NavigateUri = new Uri(
            info.ReleaseNotesUrl ?? $"https://github.com/{AppUpdateService.RepositoryFullName}/releases");

        InstallUpdateButton.Visibility = info.Available ? Visibility.Visible : Visibility.Collapsed;
        ApplyUpdatesTabBadge();
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateInfo is not { Available: true }) return;

        var downloadUrl = _updateInfo.DownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            Feedback.Warning(UpdateFeedbackText,
                I18n.T("No se encontró el instalador en la release. Descargalo manualmente desde el repositorio."));
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Actualizar WinForge"),
            Content = I18n.T("Se descargará la versión {0} y la app se cerrará para instalarla. ¿Continuar?", _updateInfo.LatestVersion),
            PrimaryButtonText = I18n.T("Actualizar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        InstallUpdateButton.IsEnabled = false;
        UpdateFeedbackText.Text = "";
        Feedback.Running(UpdateFeedbackText, I18n.T("Descargando actualización... (la app se cerrará sola)"), persistent: true);

        try
        {
            // Flujo compartido con el botón del navbar (MainWindow): guarda la línea
            // de relanzamiento, descarga el MSI y lo instala; la app se cierra sola.
            bool launched = App.MainWindowInstance != null
                && await App.MainWindowInstance.InstallUpdateAsync(_updateInfo);

            if (!launched)
            {
                InstallUpdateButton.IsEnabled = true;
                Feedback.Error(UpdateFeedbackText, I18n.T("No se pudo iniciar la actualización. Revisá tu conexión e intentá de nuevo."));
            }
        }
        catch (Exception ex)
        {
            InstallUpdateButton.IsEnabled = true;
            _loggingService.LogWarning($"ConfiguracionPage: actualizar: {ex.Message}");
            Feedback.Error(UpdateFeedbackText, I18n.T("Error al actualizar: {0}", ex.Message));
        }
    }

    // ===================== Menú de navegación =====================

    private void BuildNavMenu()
    {
        _navCheckBoxes.Clear();
        NavItemsPanel.Children.Clear();

        // Agrupar por categoría lógica con subtítulos (más legible que una lista plana).
        string? currentCat = null;
        foreach (var (tag, label) in NavTabs)
        {
            var cat = NavCategory(tag);
            if (cat != currentCat)
            {
                currentCat = cat;
                NavItemsPanel.Children.Add(new TextBlock
                {
                    Text = I18n.T(cat),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeBrushes.Get("TextFillColorSecondaryBrush"),
                    Margin = new Thickness(0, NavItemsPanel.Children.Count == 0 ? 0 : 6, 0, 2)
                });
            }

            var cb = new CheckBox
            {
                Content = I18n.T(label),
                Tag = tag,
                IsChecked = _settingsService.Get("nav." + tag, true),
                MinHeight = 34
            };
            cb.Checked += OnNavCheckChanged;
            cb.Unchecked += OnNavCheckChanged;
            _navCheckBoxes[tag] = cb;
            NavItemsPanel.Children.Add(cb);
        }

        UpdateNavMenuSummary();
    }

    /// <summary>Agrupa las pestañas del menú en categorías para la vista de configuración.</summary>
    private static string NavCategory(string tag)
    {
        // Categorías para agrupar las pestañas del menú en la vista de configuración.
        switch (tag)
        {
            case "sistema": case "red": case "memoria": case "temporizador":
            case "nucleos": case "sensores": case "optimizaciones":
                return "Sistema y rendimiento";
            case "overlay": case "procesos": case "procesosvivos": case "teclado":
            case "autoclicker": case "estabilidad":
                return "Juegos y automatización";
            case "debloat": case "herramientas": case "panelwindows": case "reparacion":
            case "actualizaciones": case "limpieza":
                return "Mantenimiento";
            default:
                return "Otras";
        }
    }

    private void OnNavCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _updatingNavAll) return;
        SaveNavVisibility();
    }

    private void SaveNavVisibility()
    {
        foreach (var (tag, _) in NavTabs)
            _settingsService.Set("nav." + tag, _navCheckBoxes.TryGetValue(tag, out var cb) && cb.IsChecked == true);
        _settingsService.Save();
        App.MainWindowInstance?.ApplyNavigationVisibility();
        UpdateNavMenuSummary();
    }

    private void SetAllNavVisible(bool visible)
    {
        // Cambia todos los checks sin que cada uno dispare un guardado por separado.
        _updatingNavAll = true;
        try
        {
            foreach (var (tag, _) in NavTabs)
                if (_navCheckBoxes.TryGetValue(tag, out var cb)) cb.IsChecked = visible;
        }
        finally
        {
            _updatingNavAll = false;
        }
        SaveNavVisibility();
    }

    private void ShowAllNavButton_Click(object sender, RoutedEventArgs e) => SetAllNavVisible(true);
    private void HideAllNavButton_Click(object sender, RoutedEventArgs e) => SetAllNavVisible(false);

    private void UpdateNavMenuSummary()
    {
        int visible = NavTabs.Count(t => _navCheckBoxes.TryGetValue(t.Tag, out var cb) && cb.IsChecked == true);
        int total = NavTabs.Length;

        NavMenuSummaryText.Text = visible == total
            ? I18n.T("Todas las pestañas son visibles")
            : I18n.T("{0} de {1} pestañas visibles", visible, total);
        NavMenuCountText.Text = $"{visible}/{total}";
        NavProgressBar.Value = total == 0 ? 0 : (double)visible / total;
        NavBadgeIcon.Foreground = visible == total ? ThemeBrushes.Get("SuccessBrush") : ThemeBrushes.Get("AccentBrush");
    }

    private void OnLanguageChanged()
    {
        ApplyConfigTabsLanguage();
        UpdateNavMenuSummary();
        ApplyThemeOptionsLanguage();
        UpdateDeveloperLogsSize();
    }

    /// <summary>
    /// Traduce los ítems del desplegable de tema. Los ComboBoxItem no se realizan
    /// en el árbol visual hasta que se abre el desplegable, así que el recorrido
    /// del I18n no los alcanza: hay que traducirlos por código.
    /// </summary>
    private void ApplyThemeOptionsLanguage()
    {
        ThemeSystemItem.Content = I18n.T("Usar sistema");
        ThemeDarkItem.Content = I18n.T("Oscuro");
        ThemeLightItem.Content = I18n.T("Claro");
    }

    // ===================== Navbar interno (pestañas) =====================

    /// <summary>
    /// Traduce las pestañas del SelectorBar interno (Inicio / Actualizaciones /
    /// Caché / Navegación / Desarrollo). Los SelectorBarItem pueden no estar realizados en el árbol visual,
    /// así que se traducen por colección lógica (mismo patrón que NucleosPage).
    /// </summary>
    private void ApplyConfigTabsLanguage()
    {
        try
        {
            if (ConfigTabs == null) return;
            foreach (var item in ConfigTabs.Items.OfType<SelectorBarItem>())
            {
                if (item.Text is string s && Translations.TryGetSource(s, out var source))
                    item.Text = I18n.T(source);
            }
        }
        catch { }
    }

    // ===================== Navbar interno (pestañas) =====================

    private void ConfigTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ConfigTabs == null || ConfigTabs.Items.Count == 0) return;
        int idx = ConfigTabs.SelectedItem != null ? ConfigTabs.Items.IndexOf(ConfigTabs.SelectedItem) : 0;
        _selectedTabIndex = idx < 0 ? 0 : idx;
        ApplyTabVisibility();
    }

    private void ApplyTabVisibility()
    {
        if (HomeTab == null || UpdatesTab == null || CacheTab == null || NavTab == null || DevTab == null) return;
        HomeTab.Visibility = _selectedTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesTab.Visibility = _selectedTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CacheTab.Visibility = _selectedTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        NavTab.Visibility = _selectedTabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        DevTab.Visibility = _selectedTabIndex == ConfigTabs.Items.IndexOf(DevelopmentTabItem)
            && DevelopmentTabItem.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Si la pestaña Actualizaciones todavía no tiene resultado (el chequeo de
        // OnLoaded sigue en curso), re-ejecutarlo para que muestre el estado apenas
        // se abra. Es idempotente: si ya terminó, el texto ya es el resultado.
        if (_selectedTabIndex == 1 && UpdateStatusText.Text == I18n.T("Comprobando actualizaciones..."))
        {
            _ = CheckForUpdatesAsync();
        }
    }
}
