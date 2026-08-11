using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class HerramientasPage : Page
{
    private readonly IWinUtilService _winUtilService;
    private readonly ILoggingService _loggingService;
    private bool _dataLoaded;

    // Cards oscuras fijas (paneles oscuros en ambos temas) con texto claro explícito.
    private static readonly SolidColorBrush LightTextBrush = new(Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xED));
    private static readonly SolidColorBrush CardBrush = new(Windows.UI.Color.FromArgb(255, 0x26, 0x2A, 0x31));
    private static readonly SolidColorBrush CardBorderBrush = new(Windows.UI.Color.FromArgb(255, 0x34, 0x3A, 0x45));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF));
    private static readonly SolidColorBrush SuccessBrush = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x07));
    private static readonly SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(255, 0xF0, 0x61, 0x6D));
    private static readonly SolidColorBrush MutedBrush = new(Windows.UI.Color.FromArgb(255, 0x9A, 0xA0, 0xA6));
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private List<WinFeatureInfo> _features = new();
    private List<WinFixInfo> _fixes = new();

    private readonly Dictionary<string, Border> _featureBadges = new();
    private readonly Dictionary<string, TextBlock> _featureBadgeText = new();
    private readonly Dictionary<string, Button> _featureToggleBtns = new();
    private readonly Dictionary<string, Button> _fixButtons = new();

    public HerramientasPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _winUtilService = App.Services.GetRequiredService<IWinUtilService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_dataLoaded) return;
        try
        {
            BuildSections();
            _dataLoaded = true;
            _ = RefreshFeatureStatesAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error cargando HerramientasPage: {ex}", ex);
        }
    }

    // ====== SECCIONES ======

    private void BuildSections()
    {
        FeaturesPanel.Children.Clear();
        FixesPanel.Children.Clear();

        _features = _winUtilService.GetFeatures();
        _fixes = _winUtilService.GetFixes();

        FeaturesPanel.Children.Add(BuildSectionHeader("Funciones", "Características opcionales de Windows que podés activar o desactivar."));
        foreach (var feature in _features)
        {
            FeaturesPanel.Children.Add(BuildFeatureCard(feature));
        }

        FixesPanel.Children.Add(BuildSectionHeader("Fixes", "Utilidades de reparación y configuración de un solo uso."));
        foreach (var fix in _fixes)
        {
            FixesPanel.Children.Add(BuildFixCard(fix));
        }
    }

    private StackPanel BuildSectionHeader(string title, string subtitle)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 10, 0, 2) };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 18 });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 13, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap });
        return stack;
    }

    // ====== CARD DE FEATURE ======

    private Border BuildFeatureCard(WinFeatureInfo feature)
    {
        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 14, 10),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var content = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = feature.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = LightTextBrush
        });
        titleRow.Children.Add(BuildInfoButton(feature.Name, feature.Description));
        content.Children.Add(titleRow);
        if (feature.NeedsRestart)
        {
            content.Children.Add(new TextBlock
            {
                Text = "⚠️ Requiere reiniciar el equipo para completar el cambio.",
                FontSize = 11.5,
                Foreground = WarningBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var rightColumn = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        var toggleBtn = new Button
        {
            Content = "Activar",
            Background = AccentBrush,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0B, 0x15, 0x20)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 7, 14, 7),
            MinWidth = 110
        };
        toggleBtn.Click += (s, e) => _ = ToggleFeatureAsync(feature);
        _featureToggleBtns[feature.Id] = toggleBtn;

        var badge = BuildStateBadge();
        _featureBadges[feature.Id] = badge;
        _featureBadgeText[feature.Id] = (TextBlock)badge.Child;

        rightColumn.Children.Add(toggleBtn);
        rightColumn.Children.Add(badge);

        Grid.SetColumn(content, 0);
        Grid.SetColumn(rightColumn, 1);
        grid.Children.Add(content);
        grid.Children.Add(rightColumn);

        card.Child = grid;
        return card;
    }

    private static Border BuildStateBadge()
    {
        var text = new TextBlock
        {
            Text = "Verificando...",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = MutedBrush
        };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0x9A, 0xA0, 0xA6)),
            Child = text
        };
        return badge;
    }

    private void UpdateFeatureState(string featureId, FeatureState state)
    {
        if (!_featureBadgeText.TryGetValue(featureId, out var text)) return;
        if (!_featureBadges.TryGetValue(featureId, out var badge)) return;
        if (!_featureToggleBtns.TryGetValue(featureId, out var toggleBtn)) return;

        switch (state)
        {
            case FeatureState.Enabled:
                text.Text = "✓ Activada";
                text.Foreground = SuccessBrush;
                badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 0x4C, 0xAF, 0x50));
                toggleBtn.Content = "Desactivar";
                toggleBtn.IsEnabled = true;
                break;
            case FeatureState.Disabled:
                text.Text = "Desactivada";
                text.Foreground = MutedBrush;
                badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0x9A, 0xA0, 0xA6));
                toggleBtn.Content = "Activar";
                toggleBtn.IsEnabled = true;
                break;
            case FeatureState.Pending:
                text.Text = "⏳ Pendiente de reinicio";
                text.Foreground = WarningBrush;
                badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 0xFF, 0xC1, 0x07));
                toggleBtn.Content = "Pendiente";
                toggleBtn.IsEnabled = false;
                break;
            default:
                text.Text = "Estado desconocido";
                text.Foreground = MutedBrush;
                badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0x9A, 0xA0, 0xA6));
                toggleBtn.Content = "Activar";
                toggleBtn.IsEnabled = true;
                break;
        }
    }

    private void SetFeatureBusy(string featureId, bool busy)
    {
        if (_featureToggleBtns.TryGetValue(featureId, out var toggle)) toggle.IsEnabled = !busy;
        if (_featureBadgeText.TryGetValue(featureId, out var text)) text.Text = busy ? "Procesando..." : text.Text;
    }

    private async Task RefreshFeatureStatesAsync()
    {
        // DISM es exclusivo: se consulta de a una feature por vez.
        foreach (var feature in _features)
        {
            var state = await _winUtilService.GetFeatureStateAsync(feature.Id);
            UpdateFeatureState(feature.Id, state);
        }
    }

    private async Task ToggleFeatureAsync(WinFeatureInfo feature)
    {
        SetFeatureBusy(feature.Id, true);
        var currentState = await _winUtilService.GetFeatureStateAsync(feature.Id);
        var enable = currentState != FeatureState.Enabled;
        var verb = enable ? "Activando" : "Desactivando";
        AppendConsole($"{verb} '{feature.Name}'...", ConsoleStatus.Running);

        var progress = new Progress<string>(line => AppendConsole(line, ConsoleStatus.Neutral));
        var result = enable
            ? await _winUtilService.EnableFeatureAsync(feature.Id, progress)
            : await _winUtilService.DisableFeatureAsync(feature.Id, progress);

        AppendConsole(result.Success ? $"'{feature.Name}' {(enable ? "activada" : "desactivada")}" : result.Output, result.Success ? ConsoleStatus.Applied : ConsoleStatus.Error);
        if (!result.Success) _loggingService.LogWarning($"Feature {feature.Id}: {result.Output}");

        SetFeatureBusy(feature.Id, false);
        var state = await _winUtilService.GetFeatureStateAsync(feature.Id);
        UpdateFeatureState(feature.Id, state);
    }

    // ====== CARD DE FIX ======

    private Border BuildFixCard(WinFixInfo fix)
    {
        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 14, 10),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var content = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = fix.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = LightTextBrush
        });
        titleRow.Children.Add(BuildInfoButton(fix.Name, fix.Description));
        content.Children.Add(titleRow);
        if (fix.IsLongRunning)
        {
            content.Children.Add(new TextBlock
            {
                Text = "⏱️ Puede tardar varios minutos.",
                FontSize = 11.5,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (fix.RequiresRestart)
        {
            content.Children.Add(new TextBlock
            {
                Text = "⚠️ Se recomienda reiniciar al terminar.",
                FontSize = 11.5,
                Foreground = WarningBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var runBtn = new Button
        {
            Content = "Ejecutar",
            Background = AccentBrush,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0B, 0x15, 0x20)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 7, 14, 7),
            MinWidth = 90
        };
        runBtn.Click += (s, e) => _ = RunFixAsync(fix);
        _fixButtons[fix.Id] = runBtn;
        actions.Children.Add(runBtn);

        if (fix.Id == "autologon")
        {
            var removeBtn = new Button
            {
                Content = "Quitar",
                Background = TransparentBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                Foreground = MutedBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 7, 12, 7)
            };
            removeBtn.Click += async (s, e) =>
            {
                removeBtn.IsEnabled = false;
                var result = await _winUtilService.RemoveAutoLogonAsync();
                AppendConsole(result.Success ? "AutoLogon desactivado" : result.Output, result.Success ? ConsoleStatus.Applied : ConsoleStatus.Error);
                removeBtn.IsEnabled = true;
            };
            actions.Children.Add(removeBtn);
        }

        if (fix.SupportsRevert)
        {
            var revertBtn = new Button
            {
                Content = "Quitar",
                Background = TransparentBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                Foreground = MutedBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 7, 12, 7)
            };
            revertBtn.Click += async (s, e) =>
            {
                revertBtn.IsEnabled = false;
                AppendConsole($"Revirtiendo '{fix.Name}'...", ConsoleStatus.Running);
                var progress = new Progress<string>(line => AppendConsole(line, ConsoleStatus.Neutral));
                var result = await _winUtilService.RevertFixAsync(fix.Id, progress);
                AppendConsole(result.Success ? $"'{fix.Name}' revertido" : result.Output, result.Success ? ConsoleStatus.Applied : ConsoleStatus.Error);
                if (!result.Success) _loggingService.LogWarning($"Revertir fix {fix.Id}: {result.Output}");
                revertBtn.IsEnabled = true;
            };
            actions.Children.Add(revertBtn);
        }

        Grid.SetColumn(content, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(content);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private async Task RunFixAsync(WinFixInfo fix)
    {
        if (fix.Id == "autologon")
        {
            await ShowAutoLogonDialogAsync();
            return;
        }

        if (!_fixButtons.TryGetValue(fix.Id, out var button)) return;
        button.IsEnabled = false;
        AppendConsole($"Ejecutando '{fix.Name}'...", ConsoleStatus.Running);

        var progress = new Progress<string>(line => AppendConsole(line, ConsoleStatus.Neutral));
        var result = await _winUtilService.RunFixAsync(fix.Id, progress);

        AppendConsole(result.Success ? $"'{fix.Name}' completado" : result.Output, result.Success ? ConsoleStatus.Applied : ConsoleStatus.Error);
        if (!result.Success) _loggingService.LogWarning($"Fix {fix.Id}: {result.Output}");

        button.IsEnabled = true;
    }

    private async Task ShowAutoLogonDialogAsync()
    {
        if (XamlRoot == null) return;

        var userBox = new TextBox { PlaceholderText = "Nombre de usuario", Header = "Usuario", Margin = new Thickness(0, 0, 0, 8) };
        var passBox = new PasswordBox { PlaceholderText = "Contraseña", Header = "Contraseña", Margin = new Thickness(0, 0, 0, 8) };
        var domainBox = new TextBox { PlaceholderText = "Dominio (opcional, dejar vacío para local)", Header = "Dominio" };

        var panel = new StackPanel
        {
            Spacing = 4,
            MaxWidth = 420,
            Children = { userBox, passBox, domainBox }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "AutoLogon",
            Content = panel,
            PrimaryButtonText = "Configurar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var username = userBox.Text?.Trim() ?? "";
        var password = passBox.Password ?? "";
        if (username.Length == 0)
        {
            AppendConsole("✗ Ingresá el nombre de usuario.", ConsoleStatus.Error);
            return;
        }

        AppendConsole($"Configurando AutoLogon para '{username}'...", ConsoleStatus.Running);
        var applyResult = await _winUtilService.SetAutoLogonAsync(username, password,
            string.IsNullOrWhiteSpace(domainBox.Text) ? null : domainBox.Text.Trim());
        AppendConsole(applyResult.Output, applyResult.Success ? ConsoleStatus.Applied : ConsoleStatus.Error);
    }

    // ====== BOTÓN DE INFO (tooltip) ======

    private static Button BuildInfoButton(string title, string description)
    {
        var infoButton = new Button
        {
            Content = "?",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            MinWidth = 22,
            MaxWidth = 22,
            Height = 22,
            Padding = new Thickness(0),
            Background = TransparentBrush,
            Foreground = MutedBrush,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };

        var toolTipContent = new StackPanel { Spacing = 6, MaxWidth = 420 };
        toolTipContent.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        toolTipContent.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap
        });

        ToolTipService.SetToolTip(infoButton, new ToolTip
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom,
            Content = toolTipContent
        });

        return infoButton;
    }

    // ====== CONSOLA ======

    private enum ConsoleStatus
    {
        Running,
        Applied,
        Skipped,
        Error,
        Neutral
    }

    private void AppendConsole(string message, ConsoleStatus status)
    {
        if (ConsolePanel == null || ConsoleText == null || ConsoleScroll == null) return;

        var wasCollapsed = ConsolePanel.Visibility == Visibility.Collapsed;
        ConsolePanel.Visibility = Visibility.Visible;

        // La consola vive al final del ScrollViewer: al aparecer la primera vez, scrollear
        // hasta ella para que el usuario vea el feedback del fix que acaba de ejecutar.
        if (wasCollapsed)
            ConsolePanel.StartBringIntoView();

        var (prefix, color) = status switch
        {
            ConsoleStatus.Running => ("▶", AccentBrush),
            ConsoleStatus.Applied => ("✓", SuccessBrush),
            ConsoleStatus.Skipped => ("ℹ", WarningBrush),
            ConsoleStatus.Error => ("✗", ErrorBrush),
            _ => ("·", MutedBrush)
        };

        if (ConsoleText.Inlines.Count > 0)
            ConsoleText.Inlines.Add(new Run { Text = Environment.NewLine });
        ConsoleText.Inlines.Add(new Run { Text = $"{prefix} {message}", Foreground = color });

        const int maxInlineCount = 800;
        while (ConsoleText.Inlines.Count > maxInlineCount)
        {
            ConsoleText.Inlines.RemoveAt(0);
            if (ConsoleText.Inlines.Count > 0)
                ConsoleText.Inlines.RemoveAt(0);
        }

        ConsoleScroll.UpdateLayout();
        ConsoleScroll.ChangeView(null, double.MaxValue, null, true);
    }

    private void ClearConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        ConsoleText?.Inlines.Clear();
    }

    private async void CopyConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConsoleText == null) return;
        var sb = new System.Text.StringBuilder();
        foreach (var inline in ConsoleText.Inlines)
        {
            if (inline is Run run)
                sb.Append(run.Text);
        }
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetText(sb.ToString());
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error copiando consola", ex);
        }
    }
}
