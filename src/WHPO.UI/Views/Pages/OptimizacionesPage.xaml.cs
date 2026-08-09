using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel.DataTransfer;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class OptimizacionesPage : Page
{
    private readonly ILoggingService _loggingService;
    private readonly ITweakService _tweakService;
    private bool _dataLoaded;

    // Texto claro fijo para las cards oscuras creadas en código: se mantienen oscuras en
    // AMBOS temas (paneles oscuros), por eso su texto es claro explícito y siempre legible.
    private static readonly SolidColorBrush LightTextBrush = new(Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xED));
    private static readonly SolidColorBrush CardBrush = new(Windows.UI.Color.FromArgb(255, 0x26, 0x2A, 0x31));
    private static readonly SolidColorBrush CardHoverBrush = new(Windows.UI.Color.FromArgb(255, 0x2E, 0x33, 0x3B));
    private static readonly SolidColorBrush CardSelectedBrush = new(Windows.UI.Color.FromArgb(255, 0x2F, 0x35, 0x41));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF));
    private static readonly SolidColorBrush SuccessBrush = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x07));
    private static readonly SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(255, 0xF0, 0x61, 0x6D));
    private static readonly SolidColorBrush MutedBrush = new(Windows.UI.Color.FromArgb(255, 0x9A, 0xA0, 0xA6));
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private readonly Dictionary<string, CheckBox> _tweakChecks = new();
    private readonly Dictionary<string, Border> _tweakBadges = new();
    private List<TweakDefinition>? _allTweaks;

    // ====== PRECONFIGURACIONES ======

    private static readonly string[] PresetMinimo =
    [
        "Historial de actividad - Desactivar",
        "Resultados recomendados de Microsoft Store - Desactivar",
        "Seguimiento de ubicación - Desactivar",
        "ConsumerFeatures - Desactivar",
        "Telemetría - Desactivar",
        "Optimización de entrega - Desactivar",
        "Tabla binaria de plataforma Windows (WPBT) - Desactivar",
        "Prevenir apps complementarias de dispositivos",
        "Punto de restauración - Crear",
        "Limpieza de disco - Ejecutar",
        "Archivos temporales - Eliminar",
        "Finalizar tarea con clic derecho - Activar"
    ];

    private static readonly string[] PresetBalanceado =
    [
        .. PresetMinimo,
        "Hibernación - Desactivar",
        "Widgets - Quitar",
        "Servicios - Configurar en Manual",
        "Detección automática de carpetas en Explorador - Desactivar",
        "Diseño anterior del menú Inicio - Activar"
    ];

    private static readonly string[] PresetGaming =
    [
        "Telemetría - Desactivar",
        "Historial de actividad - Desactivar",
        "Optimización de entrega - Desactivar",
        "ConsumerFeatures - Desactivar",
        "Efectos visuales - Configurar en Máximo rendimiento",
        "Optimizaciones de pantalla completa - Desactivar",
        "Apps en segundo plano - Desactivar",
        "Teredo - Desactivar",
        "IPv6 - Configurar IPv4 como preferido",
        "Storage Sense - Desactivar",
        "Notificaciones del sistema y calendario - Desactivar",
        "Widgets - Quitar",
        "Hibernación - Desactivar",
        "Servicios - Configurar en Manual",
        "Detección automática de carpetas en Explorador - Desactivar",
        "Punto de restauración - Crear",
        "Finalizar tarea con clic derecho - Activar"
    ];

    public OptimizacionesPage()
    {
        try
        {
            InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            _loggingService = App.Services.GetRequiredService<ILoggingService>();
            _tweakService = App.Services.GetRequiredService<ITweakService>();
            Loaded += OnLoaded;
        }
        catch (Exception ex)
        {
            if (DebugText != null)
                DebugText.Text = $"Error init: {ex.Message}";
            try { _loggingService?.LogError($"Error en constructor OptimizacionesPage: {ex}", ex); } catch { }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_dataLoaded) return;
        try
        {
            LoadTweaks();
            _dataLoaded = true;
        }
        catch (Exception ex)
        {
            DebugText.Text = $"Error: {ex.Message}";
            try { _loggingService.LogError($"Error en OnLoaded OptimizacionesPage: {ex}", ex); } catch { }
        }
    }

    private void LoadTweaks()
    {
        TweaksPanel.Children.Clear();
        PresetsPanel.Children.Clear();
        _tweakChecks.Clear();
        _tweakBadges.Clear();

        // Cachear definiciones UNA sola vez (evita GetAllTweaks() por cada tarjeta).
        _allTweaks = _tweakService.GetAllTweaks();

        BuildPresets();

        var essential = GetEssentialTweaks();
        var advanced = GetAdvancedTweaks();

        // Secciones abiertas al entrar, SIN caja contenedora (header plano + tarjetas sueltas).
        TweaksPanel.Children.Add(BuildSection("Essential Tweaks", "Tweaks recomendados para todos los sistemas", essential));
        TweaksPanel.Children.Add(BuildSection("Advanced Tweaks", "Requieren precaución, verificar compatibilidad", advanced));

        UpdateSelectionCount();

        // Verificar estados aplicados en segundo plano para no bloquear la UI.
        _ = RefreshBadgesAsync();

        _loggingService.LogInfo($"OptimizacionesPage: {essential.Count + advanced.Count} tweaks cargados");
    }

    // ====== PRECONFIGURACIONES ======

    private void BuildPresets()
    {
        var presets = new (string Name, string Subtitle, string[] Tweaks)[]
        {
            ("🔒 Mínimo", "Privacidad y limpieza básica", PresetMinimo),
            ("⚖️ Balanceado", "Mínimo + comodidad diaria", PresetBalanceado),
            ("🎮 Gaming", "Máximo rendimiento para juegos", PresetGaming)
        };

        foreach (var (name, subtitle, tweaks) in presets)
        {
            var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
            stack.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Emoji"), // emojis de los presets sin cortarse
                Foreground = LightTextBrush
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{tweaks.Length} tweaks · {subtitle}",
                FontSize = 11,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap
            });

            var button = new Button
            {
                Content = stack,
                Background = CardBrush,
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2A, 0x2F, 0x38)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                MinHeight = 54,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            button.Click += (s, e) => ApplyPreset(tweaks, name);
            PresetsPanel.Children.Add(button);
        }
    }

    private void ApplyPreset(string[] tweaks, string presetName)
    {
        foreach (var (tweakName, check) in _tweakChecks)
        {
            check.IsChecked = tweaks.Contains(tweakName);
        }
        UpdateSelectionCount();
        var count = _tweakChecks.Count(kv => kv.Value.IsChecked == true);
        ShowNotification($"Perfil {presetName}: {count} tweaks seleccionados. Revisá la selección y aplicá en lote.", "info");
    }

    // ====== SECCIONES Y TARJETAS ======

    /// <summary>
    /// Sección con header plano (sin borde/caja) y tarjetas que se pueden colapsar tocando el header.
    /// </summary>
    private StackPanel BuildSection(string title, string subtitle, List<TweakInfo> tweaks)
    {
        var section = new StackPanel { Spacing = 8 };

        var chevron = new TextBlock
        {
            Text = "▾",
            FontSize = 14,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerStack = new StackPanel { Spacing = 2 };
        headerStack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 18
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            Foreground = MutedBrush
        });

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        headerRow.Children.Add(chevron);
        headerRow.Children.Add(headerStack);

        var header = new Button
        {
            Content = headerRow,
            Background = TransparentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(4)
        };

        var cardsPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var tweak in tweaks)
        {
            cardsPanel.Children.Add(BuildTweakCard(tweak));
        }

        var expanded = true; // abiertas al entrar
        header.Click += (s, e) =>
        {
            expanded = !expanded;
            cardsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = expanded ? "▾" : "▸";
        };

        section.Children.Add(header);
        section.Children.Add(cardsPanel);
        return section;
    }

    private Border BuildTweakCard(TweakInfo tweak)
    {
        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 14, 10),
            BorderBrush = TransparentBrush,
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Casilla de selección múltiple (estilo winutil)
        var checkBox = new CheckBox
        {
            Tag = tweak.Name,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 28
        };
        checkBox.Checked += (s, e) => OnSelectionChanged(tweak.Name, card, true);
        checkBox.Unchecked += (s, e) => OnSelectionChanged(tweak.Name, card, false);
        _tweakChecks[tweak.Name] = checkBox;

        // Contenido: título + botón de info + descripción
        var content = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            // Nombre completo (ej: "Microsoft OneDrive - Eliminar") para entender la acción a primera vista.
            Text = tweak.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = LightTextBrush
        });
        titleRow.Children.Add(BuildInfoButton(tweak));
        content.Children.Add(titleRow);

        // Sin descripción visible: el tooltip (?) ya muestra la descripción de cada tweak.

        // Badge de estado "Aplicado"
        var badge = BuildAppliedBadge(tweak.Name);
        _tweakBadges[tweak.Name] = badge;

        Grid.SetColumn(checkBox, 0);
        Grid.SetColumn(content, 1);
        Grid.SetColumn(badge, 2);
        grid.Children.Add(checkBox);
        grid.Children.Add(content);
        grid.Children.Add(badge);

        card.Child = grid;

        // Micro-interacción: hover suave
        card.PointerEntered += (s, e) => { if (checkBox.IsChecked != true) card.Background = CardHoverBrush; };
        card.PointerExited += (s, e) => { if (checkBox.IsChecked != true) card.Background = CardBrush; };

        return card;
    }

    private Button BuildInfoButton(TweakInfo tweak)
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
            Text = tweak.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        toolTipContent.Children.Add(new TextBlock
        {
            Text = tweak.Description,
            FontSize = 12,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap
        });
        toolTipContent.Children.Add(new TextBlock
        {
            Text = $"Compatibilidad: {tweak.Compatibility}",
            FontSize = 11,
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

    private Border BuildAppliedBadge(string tweakName)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 0x4C, 0xAF, 0x50)),
            Visibility = Visibility.Collapsed
        };
        badge.Child = new TextBlock
        {
            Text = "✓ Aplicado",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = SuccessBrush
        };

        // El estado se chequea de forma asíncrona en RefreshBadgesAsync (no bloquea la UI).
        return badge;
    }

    private void OnSelectionChanged(string tweakName, Border card, bool selected)
    {
        if (selected)
        {
            card.Background = CardSelectedBrush;
            card.BorderBrush = AccentBrush;
            card.BorderThickness = new Thickness(1.5);
        }
        else
        {
            card.Background = CardBrush;
            card.BorderBrush = TransparentBrush;
            card.BorderThickness = new Thickness(1);
        }
        UpdateSelectionCount();
    }

    // ====== SELECCIÓN / LOTE ======

    private void UpdateSelectionCount()
    {
        if (SelectionCountText == null) return;
        var count = _tweakChecks.Count(kv => kv.Value.IsChecked == true);
        SelectionCountText.Text = count == 0
            ? "Ningún tweak seleccionado"
            : $"{count} tweak{(count == 1 ? "" : "s")} seleccionado{(count == 1 ? "" : "s")}";
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var check in _tweakChecks.Values)
        {
            check.IsChecked = false;
        }
        UpdateSelectionCount();
    }

    // ====== CONSOLA (estado en vivo estilo winutil) ======

    private enum ConsoleStatus
    {
        Running,   // ▶ en curso
        Applied,   // ✓ aplicada/revertida
        Skipped,   // ℹ ya estaba en ese estado
        Error,     // ✗ falló
        Neutral    // · información
    }

    private void AppendConsole(string message, ConsoleStatus status)
    {
        if (ConsolePanel == null || ConsoleText == null || ConsoleScroll == null) return;

        ConsolePanel.Visibility = Visibility.Visible;

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

        // Limitar el crecimiento para no degradar el rendimiento tras muchos lotes.
        const int maxInlineCount = 800; // ~400 líneas (cada una son 2 inlines)
        while (ConsoleText.Inlines.Count > maxInlineCount)
        {
            ConsoleText.Inlines.RemoveAt(0);
            if (ConsoleText.Inlines.Count > 0)
                ConsoleText.Inlines.RemoveAt(0);
        }

        // Forzar layout antes del scroll para que la última línea quede visible.
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

    private async void ApplySelectedButton_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunBatchAsync(apply: true);

    private async void RevertSelectedButton_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunBatchAsync(apply: false);

    /// <summary>
    /// Muestra un diálogo de revisión con los tweaks seleccionados antes de ejecutarlos.
    /// </summary>
    private async Task ConfirmAndRunBatchAsync(bool apply)
    {
        var selected = _tweakChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        if (selected.Count == 0)
        {
            ShowNotification("No hay tweaks seleccionados. Marcá los que quieras o usá una preconfiguración.", "warning");
            return;
        }

        if (XamlRoot is null) return; // la página aún no está en el árbol visual

        var dialog = new ContentDialog
        {
            Title = apply ? $"Aplicar {selected.Count} tweaks" : $"Revertir {selected.Count} tweaks",
            PrimaryButtonText = apply ? "Aplicar" : "Revertir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = BuildReviewPanel(selected, apply)
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        await RunBatchAsync(apply, selected);
    }

    private UIElement BuildReviewPanel(List<string> selected, bool apply)
    {
        var root = new StackPanel { Spacing = 10, MaxWidth = 560 };

        root.Children.Add(new TextBlock
        {
            Text = apply
                ? $"Se van a aplicar {selected.Count} tweaks. Revisá la lista antes de continuar."
                : $"Se van a revertir {selected.Count} tweaks. Revisá la lista antes de continuar.",
            FontSize = 13,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap
        });

        var list = new StackPanel { Spacing = 6 };
        foreach (var name in selected)
        {
            var tweak = GetTweakDefinition(name);
            var isApplied = tweak != null && _tweakService.IsTweakApplied(tweak.Id);

            var row = new Border
            {
                Background = CardBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                BorderBrush = isApplied ? SuccessBrush : TransparentBrush,
                BorderThickness = new Thickness(1)
            };

            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(new TextBlock
            {
                Text = name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = LightTextBrush
            });

            if (tweak != null)
            {
                col.Children.Add(new TextBlock
                {
                    Text = tweak.Description,
                    FontSize = 12,
                    Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap
                });

                var meta = tweak.Compatibility;
                if (isApplied) meta += " · YA APLICADA";
                col.Children.Add(new TextBlock
                {
                    Text = meta,
                    FontSize = 11,
                    Foreground = isApplied ? SuccessBrush : MutedBrush,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            row.Child = col;
            list.Children.Add(row);
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = list
        };
        root.Children.Add(scroll);
        return root;
    }

    private async Task RunBatchAsync(bool apply, List<string>? preselected = null)
    {
        var selected = preselected ?? _tweakChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        if (selected.Count == 0)
        {
            ShowNotification("No hay tweaks seleccionados. Marcá los que quieras o usá una preconfiguración.", "warning");
            return;
        }

        SetBatchBusy(true);
        int ok = 0, failed = 0, skipped = 0;
        var failures = new List<string>();

        var verb = apply ? "Aplicar" : "Revertir";
        AppendConsole($"{verb} {selected.Count} tweaks seleccionados...", ConsoleStatus.Neutral);

        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var name = selected[i];
                var def = GetTweakDefinition(name);
                if (def == null)
                {
                    failed++;
                    failures.Add($"{name}: no encontrado en el servicio");
                    AppendConsole($"'{name}' no se encontró en el servicio", ConsoleStatus.Error);
                    continue;
                }

                var isApplied = _tweakService.IsTweakApplied(def.Id);
                if (apply && isApplied)
                {
                    skipped++;
                    AppendConsole($"'{name}' ya estaba aplicada", ConsoleStatus.Skipped);
                    continue;
                }
                if (!apply && !isApplied)
                {
                    skipped++;
                    AppendConsole($"'{name}' no estaba aplicada (nada que revertir)", ConsoleStatus.Skipped);
                    continue;
                }

                AppendConsole($"{verb} '{name}'...", ConsoleStatus.Running);
                var result = apply
                    ? await _tweakService.ApplyTweakAsync(def.Id)
                    : await _tweakService.RevertTweakAsync(def.Id);

                if (result.Success)
                {
                    ok++;
                    AppendConsole($"'{name}' {(apply ? "aplicada" : "revertida")}", ConsoleStatus.Applied);
                }
                else
                {
                    failed++;
                    failures.Add($"{name}: {result.Message}");
                    AppendConsole($"'{name}' ERROR: {result.Message}", ConsoleStatus.Error);
                }
            }

            _ = RefreshBadgesAsync();
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add(ex.Message);
            AppendConsole($"ERROR de lote: {ex.Message}", ConsoleStatus.Error);
            _loggingService.LogError("Error ejecutando lote de tweaks", ex);
        }
        finally
        {
            // Los botones SIEMPRE se rehabilitan, aunque algo falle.
            SetBatchBusy(false);
        }

        if (failures.Count > 0)
        {
            _loggingService.LogWarning("Fallas en lote: " + string.Join(" | ", failures));
        }

        var summary = $"{ok} {(apply ? "aplicadas" : "revertidas")}, {skipped} omitidas, {failed} con error.";
        AppendConsole($"Resumen: {summary}", ConsoleStatus.Neutral);
        ShowNotification($"Lote {(apply ? "aplicado" : "revertido")}: {summary}", failed > 0 ? "error" : "success");
    }

    private void SetBatchBusy(bool busy)
    {
        ApplySelectedButton.IsEnabled = !busy;
        RevertSelectedButton.IsEnabled = !busy;
        ClearSelectionButton.IsEnabled = !busy;
        ApplySelectedButton.Content = busy ? "Aplicando..." : "Aplicar seleccionados";
        RevertSelectedButton.Content = busy ? "Revirtiendo..." : "Revertir seleccionados";
    }

    private async Task RefreshBadgesAsync()
    {
        // Verificar todos los estados en background para no congelar la UI al abrir la página.
        Dictionary<string, bool> states;
        try
        {
            states = await Task.Run(() =>
            {
                var dict = new Dictionary<string, bool>();
                foreach (var (tweakName, _) in _tweakBadges)
                {
                    var def = _allTweaks?.FirstOrDefault(t => t.Name == tweakName);
                    dict[tweakName] = def != null && _tweakService.IsTweakApplied(def.Id);
                }
                return dict;
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error verificando estados de tweaks", ex);
            return;
        }

        foreach (var (tweakName, applied) in states)
        {
            if (_tweakBadges.TryGetValue(tweakName, out var badge))
                badge.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private TweakDefinition? GetTweakDefinition(string tweakName)
        => _allTweaks?.FirstOrDefault(t => t.Name == tweakName);

    // ====== LISTAS DE TWEAKS ======

    private List<TweakInfo> GetEssentialTweaks()
    {
        var list = new List<TweakInfo>();
        AddTweak(list, "Historial de actividad - Desactivar", "Borra documentos recientes, portapapeles e historial de ejecución.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Hibernación - Desactivar", "La hibernación está pensada para portátiles, ya que guarda la memoria antes de apagar el equipo. Realmente nunca debería usarse en escritorios.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Widgets - Quitar", "Elimina los molestos widgets en la parte inferior izquierda de la barra de tareas.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Diseño anterior del menú Inicio - Activar", "Restaura el diseño antiguo del menú Inicio anterior al despliegue gradual del nuevo en 25H2. En versiones nuevas de Windows no funcionará.", "Compatible con Windows 11 25H2", true, "Essential Tweaks");
        AddTweak(list, "Resultados recomendados de Microsoft Store - Desactivar", "No mostrará apps recomendadas de Microsoft Store al buscar en el menú Inicio.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Seguimiento de ubicación - Desactivar", "Desactiva el seguimiento de ubicación.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Servicios - Configurar en Manual", "Configura algunos servicios en Manual y ajusta SvcHostSplitThresholdInKB para reducir significativamente la cantidad de procesos svchost.exe.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "ConsumerFeatures - Desactivar", "Detiene instalaciones promocionadas de apps y reduce sugerencias de contenido de Microsoft Store.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Telemetría - Desactivar", "Desactiva la telemetría de Microsoft.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Optimización de entrega - Desactivar", "Evita que Windows use tu ancho de banda para subir actualizaciones a otros equipos en internet o red local.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "BitLocker - Desactivar", "Desactiva BitLocker.", "Solo si no usas cifrado de disco", true, "Essential Tweaks");
        AddTweak(list, "Punto de restauración - Crear", "Crea un punto de restauración en tiempo de ejecución por si se necesita revertir modificaciones.", "Requiere permisos de administrador", true, "Essential Tweaks");
        AddTweak(list, "Finalizar tarea con clic derecho - Activar", "Habilita la opción de finalizar tarea al hacer clic derecho en un programa de la barra de tareas.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Tabla binaria de plataforma Windows (WPBT) - Desactivar", "WPBT permite que el fabricante ejecute programas al iniciar, como software antirrobo o instalaciones forzadas sin consentimiento. Riesgo de seguridad.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Prevenir apps complementarias de dispositivos", "Evita que se instale software adicional al conectar dispositivos (ej. anuncios al conectar un monitor). Riesgo de seguridad.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Limpieza de disco - Ejecutar", "Ejecuta la limpieza del disco C: y elimina actualizaciones de Windows antiguas.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Archivos temporales - Eliminar", "Borra las carpetas TEMP.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        AddTweak(list, "Detección automática de carpetas en Explorador - Desactivar", "El Explorador intenta adivinar el tipo de carpeta según su contenido, ralentizando la navegación. ¡ADVERTENCIA! Desactivará la agrupación del Explorador.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        return list;
    }

    private List<TweakInfo> GetAdvancedTweaks()
    {
        var list = new List<TweakInfo>();
        AddTweak(list, "Brave Browser - Desbloat", "Desactiva varias molestias como Brave Rewards, Leo AI, Crypto Wallet y VPN.", "Requiere Brave Browser instalado", true, "Advanced Tweaks");
        AddTweak(list, "Advertencias de archivos RDP sin firmar - Desactivar", "Desactiva las advertencias al lanzar archivos RDP sin firmar introducidas en las últimas actualizaciones.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Microsoft Edge - Desbloat", "Desactiva varias opciones de telemetría, popups y otras molestias en Edge.", "Requiere Microsoft Edge instalado", true, "Advanced Tweaks");
        AddTweak(list, "Microsoft Edge - Eliminar", "Desinstala Microsoft Edge creando un archivo dummy MicrosoftEdge.exe que engaña al desinstalador oficial para una eliminación a nivel de sistema.", "Requiere precaución", true, "Advanced Tweaks");
        AddTweak(list, "Fecha y hora - Configurar en UTC", "Esencial para equipos con dual-boot. Corrige la sincronización horaria con sistemas Linux.", "Solo dual-boot con Linux", true, "Advanced Tweaks");
        AddTweak(list, "Microsoft OneDrive - Eliminar", "Deniega permisos para eliminar archivos de usuario de OneDrive, usa su desinstalador para quitarlo y restaura los permisos.", "Requiere precaución", true, "Advanced Tweaks");
        AddTweak(list, "Inicio y Galería del Explorador - Desactivar", "Elimina Inicio y Galería del Explorador y establece Este PC como predeterminado.", "Compatible con Windows 11", true, "Advanced Tweaks");
        AddTweak(list, "Efectos visuales - Configurar en Máximo rendimiento", "Configura las preferencias del sistema a rendimiento. Puedes hacerlo manualmente con sysdm.cpl.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Almacenamiento reservado - Desactivar", "Desactiva el almacenamiento reservado de Windows (7-10 GB para actualizaciones). Solo recomendado en discos pequeños. Re-activar antes de grandes actualizaciones.", "Solo en discos pequeños", true, "Advanced Tweaks");
        AddTweak(list, "Storage Sense - Desactivar", "Storage Sense elimina archivos temporales automáticamente.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Windows AI - Desactivar y eliminar", "Elimina y desactiva todas las funciones y paquetes de IA.", "Compatible con Windows 11", true, "Advanced Tweaks");
        AddTweak(list, "Instalación automática de software Razer - Desactivar", "Bloquea TODAS las instalaciones de software Razer. El hardware funciona bien sin software.", "Solo hardware Razer", true, "Advanced Tweaks");
        AddTweak(list, "Notificaciones del sistema y calendario - Desactivar", "Desactiva todas las notificaciones INCLUYENDO el calendario.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Lista de bloqueo de URL de Adobe - Activar", "Reduce interrupciones bloqueando selectivamente conexiones a servidores de activación y telemetría de Adobe.", "Requiere software Adobe", true, "Advanced Tweaks");
        AddTweak(list, "Menú contextual anterior - Activar", "Restaura el menú contextual clásico del Explorador, reemplazando la versión simplificada de Windows 11.", "Compatible con Windows 11", true, "Advanced Tweaks");
        AddTweak(list, "IPv6 - Configurar IPv4 como preferido", "Configurar la preferencia IPv4 puede tener beneficios de latencia y seguridad en redes privadas sin IPv6.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Teredo - Desactivar", "Teredo es un túnel IPv6 que puede causar latencia adicional, aunque puede causar problemas con algunos juegos.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "IPv6 - Desactivar", "Desactiva IPv6.", "Requiere precaución", true, "Advanced Tweaks");
        AddTweak(list, "Apps en segundo plano - Desactivar", "Desactiva todas las apps de Microsoft Store en segundo plano, lo que debe hacerse individualmente desde Windows 11.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Optimizaciones de pantalla completa - Desactivar", "Desactiva FSO en todas las aplicaciones. NOTA: Desactivará la gestión de color en pantalla completa exclusiva.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "O&O ShutUp10++ - Ejecutar", "Ejecuta O&O ShutUp10++ para aplicar su colección de tweaks de privacidad.", "Requiere descargar O&O ShutUp10++", true, "Advanced Tweaks");
        return list;
    }

    private void AddTweak(List<TweakInfo> list, string name, string description, string compatibility, bool isReversible, string category)
    {
        list.Add(new TweakInfo(name, description, compatibility, isReversible, category));
    }

    // ====== NOTIFICACIONES ======

    private void ShowNotification(string message, string type)
    {
        try
        {
            var severity = type switch
            {
                "success" => InfoBarSeverity.Success,
                "error" => InfoBarSeverity.Error,
                "warning" => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational
            };

            var notificationBar = new InfoBar
            {
                Message = message,
                IsOpen = true,
                Severity = severity,
                Margin = new Thickness(12),
                CornerRadius = new CornerRadius(8)
            };

            if (NotificationPanel != null)
            {
                NotificationPanel.Children.Add(notificationBar);

                // Auto-cerrar después de 4 segundos
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                timer.Tick += (s, e) =>
                {
                    notificationBar.IsOpen = false;
                    NotificationPanel.Children.Remove(notificationBar);
                    timer.Stop();
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            try { _loggingService.LogError($"Error mostrando notificación: {ex.Message}", ex); } catch { }
        }
    }
}

/// <summary>
/// Información de un tweak del sistema.
/// </summary>
public record TweakInfo(
    string Name,
    string Description,
    string Compatibility,
    bool IsReversible,
    string Category);
