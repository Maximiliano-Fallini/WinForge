using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class ConfiguracionPage : Page
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IStartupService _startupService;
    private bool _isLoading;

    // ---- Menú de navegación: pestañas del menú lateral y su clave de configuración ----
    private static readonly (string Tag, string Label)[] NavTabs =
    {
        ("sistema", "Sistema"),
        ("red", "Red"),
        ("memoria", "Memoria"),
        ("temporizador", "Resolución del Temporizador"),
        ("nucleos", "Núcleos y Plan de energía"),
        ("estabilidad", "Test de estabilidad"),
        ("sensores", "Monitor de sensores"),
        ("optimizaciones", "Optimizaciones"),
        ("herramientas", "Herramientas y funciones"),
        ("panelwindows", "Panel de Windows"),
        ("reparacion", "Reparación"),
        ("actualizaciones", "Windows Update")
    };

    private readonly Dictionary<string, CheckBox> _navCheckBoxes = new();

    public ConfiguracionPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _themeService = App.Services.GetRequiredService<IThemeService>();
        _startupService = App.Services.GetRequiredService<IStartupService>();

        Loaded += OnLoaded;
        MinimizeToTrayToggle.Toggled += OnMinimizeToTrayToggled;
        OptimizePerformanceToggle.Toggled += OnOptimizePerformanceToggled;
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
            LaunchAtStartupToggle.IsOn = _startupService.IsEnabled();
            StartMinimizedToggle.IsOn = _settingsService.Get("window.startMinimized", false);
            BuildNavMenu();
            SelectTheme(_themeService.CurrentTheme);
            App.MainWindowInstance?.UpdateTrayStatus();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void SelectTheme(AppTheme theme)
    {
        var item = ThemeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(comboItem => string.Equals(comboItem.Tag?.ToString(), theme.ToString(), StringComparison.Ordinal));
        ThemeComboBox.SelectedItem = item;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<AppTheme>(tag, out var theme))
        {
            return;
        }

        _themeService.SetTheme(theme);
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

    private void OnLaunchAtStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var result = _startupService.SetEnabled(LaunchAtStartupToggle.IsOn);
        if (!result.Success)
        {
            // Sin feedback visual: se revierte el toggle para reflejar el estado real.
            _isLoading = true;
            LaunchAtStartupToggle.IsOn = !LaunchAtStartupToggle.IsOn;
            _isLoading = false;
        }
    }

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        if (StartMinimizedToggle.IsOn && !MinimizeToTrayToggle.IsOn)
        {
            _isLoading = true;
            MinimizeToTrayToggle.IsOn = true;
            _isLoading = false;
            _settingsService.Set("window.minimizeToTray", true);
        }

        _settingsService.Set("window.startMinimized", StartMinimizedToggle.IsOn);
        _settingsService.Save();
    }

    // ===================== Menú de navegación =====================

    private void BuildNavMenu()
    {
        _navCheckBoxes.Clear();
        NavItemsPanel.Children.Clear();

        foreach (var (tag, label) in NavTabs)
        {
            var cb = new CheckBox
            {
                Content = label,
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

    private void OnNavCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
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

    private void UpdateNavMenuSummary()
    {
        int visible = NavTabs.Count(t => _navCheckBoxes.TryGetValue(t.Tag, out var cb) && cb.IsChecked == true);
        NavMenuSummaryText.Text = visible == NavTabs.Length
            ? "Todas las pestañas son visibles"
            : $"{visible} de {NavTabs.Length} pestañas visibles";
    }
}
