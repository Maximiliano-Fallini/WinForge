using System;
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

    public ConfiguracionPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _themeService = App.Services.GetRequiredService<IThemeService>();
        _startupService = App.Services.GetRequiredService<IStartupService>();

        Loaded += OnLoaded;
        MinimizeToTrayToggle.Toggled += OnMinimizeToTrayToggled;
        ShowTrayMetricsToggle.Toggled += OnShowTrayMetricsToggled;
        LaunchAtStartupToggle.Toggled += OnLaunchAtStartupToggled;
        StartMinimizedToggle.Toggled += OnStartMinimizedToggled;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        try
        {
            MinimizeToTrayToggle.IsOn = _settingsService.Get("window.minimizeToTray", true);
            ShowTrayMetricsToggle.IsOn = _settingsService.Get("tray.showMetrics", false);
            LaunchAtStartupToggle.IsOn = _startupService.IsEnabled();
            StartMinimizedToggle.IsOn = _settingsService.Get("window.startMinimized", false);
            SelectTheme(_themeService.CurrentTheme);
            App.MainWindowInstance?.UpdateTrayMetricsState();
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

    private void OnShowTrayMetricsToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settingsService.Set("tray.showMetrics", ShowTrayMetricsToggle.IsOn);
        _settingsService.Save();
        App.MainWindowInstance?.UpdateTrayMetricsState();
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
}
