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

/// <summary>
/// Debloat: eliminación de aplicaciones preinstaladas (bloatware) y optimización
/// de las aplicaciones que se conservan. Los tweaks viven en TweakService con la
/// categoría "Debloat" (basados en Win11Debloat / Christitus WinUtil).
/// </summary>
public sealed partial class DebloatPage : Page
{
    private readonly ILoggingService _loggingService;
    private readonly ITweakService _tweakService;
    private bool _dataLoaded;
    private bool _contentShown;

    // Pinceles desde los recursos de tema de la app (claro/oscuro): las cards creadas en
    // código acompañan al tema igual que las del XAML.
    private static SolidColorBrush CardBrush => ThemeBrushes.Get("CardBackgroundBrush");
    private static SolidColorBrush CardHoverBrush => ThemeBrushes.Get("CardHoverBrush");
    private static SolidColorBrush CardSelectedBrush => ThemeBrushes.Get("CardSelectedBrush");
    private static SolidColorBrush AccentBrush => ThemeBrushes.Get("AccentBrush");
    private static SolidColorBrush SuccessBrush => (SolidColorBrush)App.Current.Resources["SuccessBrush"];
    private static SolidColorBrush MutedBrush => ThemeBrushes.Get("MutedBrush");
    private static readonly SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(255, 0xF0, 0x61, 0x6D));
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private readonly Dictionary<string, CheckBox> _tweakChecks = new();
    private readonly Dictionary<string, Border> _tweakBadges = new();
    private readonly Dictionary<string, Border> _tweakCards = new();
    private readonly Dictionary<string, TextBlock> _tweakTitles = new();
    private List<TweakDefinition>? _allTweaks;
    private static readonly SolidColorBrush MutedBadgeBrush = new(Windows.UI.Color.FromArgb(40, 0x9E, 0x9E, 0x9E));
    private static readonly SolidColorBrush AppliedBadgeBrush = new(Windows.UI.Color.FromArgb(45, 0x4C, 0xAF, 0x50));

    public DebloatPage()
    {
        try
        {
            InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            _loggingService = App.Services.GetRequiredService<ILoggingService>();
            _tweakService = App.Services.GetRequiredService<ITweakService>();
            Loaded += OnLoaded;

            // Al cambiar el idioma, reconstruir las cards (títulos, descripciones
            // y badges se crean en código con I18n.T; el recorrido del árbol visual
            // no los alcanza).
            I18n.LanguageChanged += () =>
            {
                if (_dataLoaded) LoadTweaks();
            };

            // Al cambiar el tema, re-aplicar los colores a las cards EXISTENTES
            // (no se reconstruyen: se perdería la selección del usuario).
            ActualThemeChanged += (s, e) =>
            {
                if (_dataLoaded) ApplyThemeToCards();
            };
        }
        catch (Exception ex)
        {
            if (DebugText != null)
                DebugText.Text = $"Error init: {ex.Message}";
            try { _loggingService?.LogError($"Error en constructor DebloatPage: {ex}", ex); } catch { }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_dataLoaded) return;
        try
        {
            // Skeleton visible mientras corre la detección de instalado/no instalado.
            ShowSkeleton();
            LoadTweaks();
            _dataLoaded = true;

            _tweakService.TweakStateChanged += OnTweakStateChanged;

            // Seguridad: si la detección tarda demasiado (o falla), mostrar el
            // contenido igual para no dejar la página en "cargando" para siempre.
            _ = ForceShowContentAfterTimeoutAsync();
        }
        catch (Exception ex)
        {
            DebugText.Text = $"Error: {ex.Message}";
            try { _loggingService.LogError($"Error en OnLoaded DebloatPage: {ex}", ex); } catch { }
        }
    }

    // ===================== Skeleton de carga =====================

    private void ShowSkeleton()
    {
        _contentShown = false;
        if (PageSkeleton == null || PageContent == null) return;
        PageSkeleton.Visibility = Visibility.Visible;
        PageContent.Visibility = Visibility.Collapsed;
    }

    private void ShowContent()
    {
        if (_contentShown) return;
        _contentShown = true;
        if (PageSkeleton == null) return;
        PageSkeleton.Visibility = Visibility.Collapsed;
        PageContent.Visibility = Visibility.Visible;
    }

    // Seguridad: si la detección de apps tarda más de 8 s (PowerShell lento o
    // fallo), se muestra el contenido igual.
    private async Task ForceShowContentAfterTimeoutAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (!_contentShown)
            DispatcherQueue.TryEnqueue(ShowContent);
    }

    /// <summary>
    /// Vuelve a detectar el estado de instalado/no instalado: invalida las cachés
    /// del servicio, muestra el skeleton de carga y re-corre la detección.
    /// </summary>
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (RefreshButton == null) return;
        RefreshButton.IsEnabled = false;
        try
        {
            // Invalidar cachés para consultar el estado real del sistema.
            _tweakService.InvalidateAppxChecks();
            ShowSkeleton();
            await RefreshBadgesAsync();
            ShowContent(); // aunque la detección falle internamente, se muestra el contenido
        }
        catch (Exception ex)
        {
            ShowContent();
            _loggingService.LogError("Error al volver a detectar apps", ex);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnTweakStateChanged(string tweakId, bool applied)
    {
        try
        {
            // Re-detectar el estado (instalado + aplicado) en background: las cachés
            // del servicio se invalidan al aplicar, así que el resultado es correcto.
            _ = RefreshBadgesAsync();
        }
        catch (Exception ex)
        {
            try { _loggingService.LogError("Error actualizando estado por TweakStateChanged", ex); } catch { }
        }
    }

    /// <summary>
    /// Estado de una card: si la app NO está instalada, la casilla queda deshabilitada,
    /// el título apagado y el badge muestra "No instalada". Si sí está instalada, la
    /// casilla vuelve a ser seleccionable y el badge refleja aplicado/oculto.
    /// </summary>
    private void ApplyAppState(string tweakName, bool installed, bool applied)
    {
        if (_tweakChecks.TryGetValue(tweakName, out var cb))
        {
            cb.IsEnabled = installed;
            if (!installed) cb.IsChecked = false;
        }

        // Título apagado (gris) cuando la app no está instalada. Se usa Opacity en
        // vez de Foreground: setear Foreground a null rompe la herencia del tema y
        // el texto queda invisible (bug: la card de Xbox no mostraba el título).
        if (_tweakTitles.TryGetValue(tweakName, out var title))
            title.Opacity = installed ? 1.0 : 0.5;

        if (_tweakBadges.TryGetValue(tweakName, out var badge))
        {
            if (!installed)
            {
                badge.Visibility = Visibility.Visible;
                badge.Background = MutedBadgeBrush;
                if (badge.Child is TextBlock tb)
                {
                    tb.Text = I18n.T("No instalada");
                    tb.Foreground = MutedBrush;
                }
            }
            else
            {
                // Restaurar el badge normal (verde "✓ Aplicado") y mostrarlo solo si aplicado.
                badge.Background = AppliedBadgeBrush;
                if (badge.Child is TextBlock tb)
                {
                    tb.Text = I18n.T("✓ Aplicado");
                    tb.Foreground = SuccessBrush;
                }
                badge.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void LoadTweaks()
    {
        TweaksPanel.Children.Clear();
        _tweakChecks.Clear();
        _tweakBadges.Clear();
        _tweakCards.Clear();
        _tweakTitles.Clear();

        // Tooltip del botón de actualizar (se re-aplica al cambiar de idioma).
        ToolTipService.SetToolTip(RefreshButton, I18n.T("Volver a detectar"));

        // Cachear definiciones UNA sola vez (evita GetAllTweaks() por cada tarjeta).
        _allTweaks = _tweakService.GetAllTweaks();

        var removeApps = GetRemoveApps();
        var optimizeApps = GetOptimizeApps();

        // Dos columnas: apps a desinstalar a la izquierda, optimizaciones a la derecha.
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var removeSection = BuildSection(I18n.T("Desinstalar apps"), I18n.T("Elimina las aplicaciones preinstaladas (bloatware). Se pueden reinstalar desde Microsoft Store."), removeApps);
        var optimizeSection = BuildSection(I18n.T("Optimizaciones de aplicaciones"), I18n.T("Limpia y configura las aplicaciones que decidís conservar."), optimizeApps);

        Grid.SetColumn(removeSection, 0);
        Grid.SetColumn(optimizeSection, 1);
        grid.Children.Add(removeSection);
        grid.Children.Add(optimizeSection);
        TweaksPanel.Children.Add(grid);

        // Verificar estados aplicados en segundo plano para no bloquear la UI.
        _ = RefreshBadgesAsync();

        _loggingService.LogInfo($"DebloatPage: {removeApps.Count + optimizeApps.Count} tweaks cargados");
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
        _tweakCards[tweak.Name] = card;

        var content = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var titleBlock = new TextBlock
        {
            Text = I18n.T(tweak.Name),
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        _tweakTitles[tweak.Name] = titleBlock;
        titleRow.Children.Add(titleBlock);
        titleRow.Children.Add(BuildInfoButton(tweak));
        content.Children.Add(titleRow);

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
            Text = I18n.T(tweak.Name),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        toolTipContent.Children.Add(new TextBlock
        {
            Text = I18n.T(tweak.Description),
            FontSize = 12,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap
        });
        toolTipContent.Children.Add(new TextBlock
        {
            Text = I18n.T("Compatibilidad: {0}", I18n.T(tweak.Compatibility)),
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
            Text = I18n.T("✓ Aplicado"),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = SuccessBrush
        };

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
    }

    /// <summary>
    /// Re-aplica los colores de tema a las cards de tweaks sin reconstruirlas
    /// (preserva la selección del usuario).
    /// </summary>
    private void ApplyThemeToCards()
    {
        foreach (var (tweakName, card) in _tweakCards)
        {
            var selected = _tweakChecks.TryGetValue(tweakName, out var cb) && cb.IsChecked == true;
            card.Background = selected ? CardSelectedBrush : CardBrush;
            card.BorderBrush = selected ? AccentBrush : TransparentBrush;
            card.BorderThickness = new Thickness(selected ? 1.5 : 1);
        }
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

        var wasCollapsed = ConsolePanel.Visibility == Visibility.Collapsed;
        ConsolePanel.Visibility = Visibility.Visible;

        if (wasCollapsed)
            ConsolePanel.StartBringIntoView();

        var (prefix, color) = status switch
        {
            ConsoleStatus.Running => ("▶", AccentBrush),
            ConsoleStatus.Applied => ("✓", SuccessBrush),
            ConsoleStatus.Skipped => ("ℹ", WarningBrush()),
            ConsoleStatus.Error => ("✗", ErrorBrush),
            _ => ("·", MutedBrush)
        };

        if (ConsoleText.Inlines.Count > 0)
            ConsoleText.Inlines.Add(new Run { Text = Environment.NewLine });
        ConsoleText.Inlines.Add(new Run { Text = $"{prefix} {message}", Foreground = color });

        // Limitar el crecimiento para no degradar el rendimiento tras muchos lotes.
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

    private static SolidColorBrush WarningBrush() =>
        (SolidColorBrush)App.Current.Resources["WarningBrush"];

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
            ShowNotification(I18n.T("No hay tweaks seleccionados. Marcá los que quieras."), "warning");
            return;
        }

        if (XamlRoot is null) return; // la página aún no está en el árbol visual

        var dialog = new ContentDialog
        {
            Title = apply ? I18n.T("Aplicar {0} tweaks", selected.Count) : I18n.T("Revertir {0} tweaks", selected.Count),
            PrimaryButtonText = apply ? I18n.T("Aplicar") : I18n.T("Revertir"),
            CloseButtonText = I18n.T("Cancelar"),
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
                ? I18n.T("Se van a aplicar {0} tweaks. Revisá la lista antes de continuar.", selected.Count)
                : I18n.T("Se van a revertir {0} tweaks. Revisá la lista antes de continuar.", selected.Count),
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
                Text = I18n.T(name),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });

            if (tweak != null)
            {
                col.Children.Add(new TextBlock
                {
                    Text = I18n.T(tweak.Description),
                    FontSize = 12,
                    Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap
                });

                var meta = I18n.T(tweak.Compatibility);
                if (isApplied) meta += " · " + I18n.T("YA APLICADA");
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
            ShowNotification(I18n.T("No hay tweaks seleccionados. Marcá los que quieras."), "warning");
            return;
        }

        SetBatchBusy(true);
        int ok = 0, failed = 0, skipped = 0;
        var failures = new List<string>();

        var verb = apply ? "Aplicar" : "Revertir";
        AppendConsole(I18n.T("{0} {1} tweaks seleccionados...", I18n.T(verb), selected.Count), ConsoleStatus.Neutral);

        var progress = new Progress<string>(line => AppendConsole(line, ConsoleStatus.Neutral));

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
                    AppendConsole(I18n.T("'{0}' ya estaba aplicada", name), ConsoleStatus.Skipped);
                    continue;
                }
                if (!apply && !isApplied)
                {
                    skipped++;
                    AppendConsole(I18n.T("'{0}' no estaba aplicada (nada que revertir)", name), ConsoleStatus.Skipped);
                    continue;
                }

                AppendConsole(I18n.T("{0} '{1}'...", I18n.T(verb), name), ConsoleStatus.Running);
                var result = apply
                    ? await _tweakService.ApplyTweakAsync(def.Id, progress)
                    : await _tweakService.RevertTweakAsync(def.Id, progress);

                if (result.Success)
                {
                    ok++;
                    AppendConsole(I18n.T(apply ? "'{0}' aplicada" : "'{0}' revertida", name), ConsoleStatus.Applied);
                }
                else
                {
                    failed++;
                    failures.Add($"{name}: {result.Message}");
                    AppendConsole(I18n.T("'{0}' ERROR: {1}", name, result.Message), ConsoleStatus.Error);
                }
            }

            _ = RefreshBadgesAsync();
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add(ex.Message);
            AppendConsole(I18n.T("ERROR de lote: {0}", ex.Message), ConsoleStatus.Error);
            _loggingService.LogError("Error ejecutando lote de tweaks", ex);
        }
        finally
        {
            SetBatchBusy(false);
        }

        if (failures.Count > 0)
        {
            _loggingService.LogWarning("Fallas en lote: " + string.Join(" | ", failures));
        }

        var summary = I18n.T("{0} aplicadas, {1} omitidas, {2} con error.", ok, skipped, failed);
        AppendConsole(I18n.T("Resumen: {0}", summary), ConsoleStatus.Neutral);
        ShowNotification(I18n.T("Lote {0}: {1}", I18n.T(apply ? "aplicado" : "revertido"), summary), failed > 0 ? "error" : "success");
    }

    private void SetBatchBusy(bool busy)
    {
        ApplySelectedButton.IsEnabled = !busy;
        RevertSelectedButton.IsEnabled = !busy;
        ApplySelectedButton.Content = busy ? I18n.T("Aplicando...") : I18n.T("Aplicar seleccionados");
        RevertSelectedButton.Content = busy ? I18n.T("Revirtiendo...") : I18n.T("Revertir seleccionados");
    }

    private async Task RefreshBadgesAsync()
    {
        Dictionary<string, (bool Applied, bool Installed)> states;
        try
        {
            states = await Task.Run(() =>
            {
                // Pre-cargar el estado de instalación de todos los paquetes Appx en UN
                // solo Get-AppxPackage (evita un proceso PowerShell por app). Corre dentro
                // de Task.Run: su bloqueo sincrónico (GetAwaiter().GetResult()) nunca debe
                // ejecutarse en el hilo de UI o la app se congela.
                _tweakService.WarmUpAppxChecks();

                var dict = new Dictionary<string, (bool, bool)>();
                foreach (var (tweakName, _) in _tweakBadges)
                {
                    var def = _allTweaks?.FirstOrDefault(t => t.Name == tweakName);
                    if (def == null) continue;
                    dict[tweakName] = (_tweakService.IsTweakApplied(def.Id), _tweakService.IsTweakAppInstalled(def.Id));
                }
                return dict;
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error verificando estados de tweaks", ex);
            return;
        }

        // Detección terminada: aplicar el estado a todas las cards.
        foreach (var (tweakName, state) in states)
        {
            ApplyAppState(tweakName, state.Installed, state.Applied);
        }

        // Reemplazar el skeleton por el contenido real.
        ShowContent();
    }

    private TweakDefinition? GetTweakDefinition(string tweakName)
        => _allTweaks?.FirstOrDefault(t => t.Name == tweakName);

    // ====== LISTAS DE TWEAKS ======

    private List<TweakInfo> GetRemoveApps()
    {
        var list = new List<TweakInfo>();
        AddTweak(list, "Cortana - Desinstalar", "Elimina el asistente de voz de Microsoft (discontinuado).", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Candy Crush Saga - Desinstalar", "Elimina el clásico juego de King preinstalado en Windows.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Clipchamp - Desinstalar", "Elimina el editor de video de Microsoft.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Colección Solitario - Desinstalar", "Elimina la colección de solitario de Microsoft.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Skype - Desinstalar", "Elimina la versión UWP de Skype (discontinuada).", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "OneNote - Desinstalar", "Elimina la versión UWP de OneNote.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Teams - Desinstalar", "Elimina las versiones de Microsoft Teams (nueva y clásica).", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "TikTok - Desinstalar", "Elimina la app preinstalada de TikTok.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Spotify - Desinstalar", "Elimina la app preinstalada de Spotify.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Correo y calendario - Desinstalar", "Elimina la app de correo y calendario (discontinuada, reemplazada por Outlook).", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Noticias - Desinstalar", "Elimina la app de noticias de Microsoft.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Películas y TV - Desinstalar", "Elimina la app de video de Microsoft.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Office Hub - Desinstalar", "Elimina el hub de acceso a las aplicaciones de Office.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Phone Link - Desinstalar", "Elimina la integración con el teléfono (antes Tu Teléfono).", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Xbox (app) - Desinstalar", "Elimina la app de Xbox. OJO: es necesaria para instalar algunos juegos de PC.", "Se puede reinstalar desde Microsoft Store");
        AddTweak(list, "Copilot - Desinstalar", "Elimina el asistente de IA integrado de Windows.", "Se puede reinstalar desde Microsoft Store");
        return list;
    }

    private List<TweakInfo> GetOptimizeApps()
    {
        var list = new List<TweakInfo>();
        AddTweak(list, "Brave Browser - Desbloat", "Desactiva varias molestias como Brave Rewards, Leo AI, Crypto Wallet y VPN.", "Requiere Brave Browser instalado");
        AddTweak(list, "Google Chrome - Desbloat", "Desactiva telemetría, apps en segundo plano, avisos de navegador predeterminado y comentarios en Google Chrome.", "Requiere Google Chrome instalado");
        AddTweak(list, "Mozilla Firefox - Desbloat", "Desactiva telemetría, estudios, Pocket, comandos de comentarios y avisos de navegador predeterminado en Firefox.", "Requiere Mozilla Firefox instalado");
        AddTweak(list, "Opera - Desbloat", "Desactiva telemetría, apps en segundo plano, avisos de navegador predeterminado y comentarios en Opera.", "Requiere Opera instalado");
        AddTweak(list, "Microsoft Edge - Desbloat", "Desactiva varias opciones de telemetría, popups y otras molestias en Edge.", "Requiere Microsoft Edge instalado");
        AddTweak(list, "Microsoft Edge - Eliminar", "Desinstala Microsoft Edge creando un archivo dummy MicrosoftEdge.exe que engaña al desinstalador oficial para una eliminación a nivel de sistema.", "Requiere precaución");
        AddTweak(list, "Microsoft OneDrive - Eliminar", "Deniega permisos para eliminar archivos de usuario de OneDrive, usa su desinstalador para quitarlo y restaura los permisos.", "Requiere precaución");
        AddTweak(list, "Windows AI - Desactivar y eliminar", "Elimina y desactiva todas las funciones y paquetes de IA.", "Compatible con Windows 11");
        AddTweak(list, "Barra de juegos (Game Bar) - Desinstalar", "Desinstala el paquete Microsoft.XboxGamingOverlay (la app de la barra de juegos), cerrando antes sus procesos. Windows puede reinstalarla con las actualizaciones. Revertir abre la Microsoft Store para reinstalarla.", "Requiere precaución");
        AddTweak(list, "Widgets - Quitar", "Elimina los molestos widgets en la parte inferior izquierda de la barra de tareas.", "Compatible con Windows 10/11");
        AddTweak(list, "Instalación automática de software Razer - Desactivar", "Bloquea TODAS las instalaciones de software Razer. El hardware funciona bien sin software.", "Solo hardware Razer");
        AddTweak(list, "Lista de bloqueo de URL de Adobe - Activar", "Reduce interrupciones bloqueando selectivamente conexiones a servidores de activación y telemetría de Adobe.", "Requiere software Adobe");
        return list;
    }

    private void AddTweak(List<TweakInfo> list, string name, string description, string compatibility)
    {
        list.Add(new TweakInfo(name, description, compatibility, true, "Debloat"));
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
                NotificationPanel.Visibility = Visibility.Visible;
                NotificationPanel.Children.Add(notificationBar);

                // Auto-cerrar después de 4 segundos
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                timer.Tick += (s, e) =>
                {
                    notificationBar.IsOpen = false;
                    NotificationPanel.Children.Remove(notificationBar);
                    if (NotificationPanel.Children.Count == 0)
                        NotificationPanel.Visibility = Visibility.Collapsed;
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
