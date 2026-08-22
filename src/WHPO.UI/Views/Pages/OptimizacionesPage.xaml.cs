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

    // Pinceles desde los recursos de tema de la app (claro/oscuro): las cards creadas en
    // código acompañan al tema igual que las del XAML. Se resuelven con el tema EFECTIVO
    // (ThemeBrushes), no con el del sistema.
    private static SolidColorBrush CardBrush => ThemeBrushes.Get("CardBackgroundBrush");
    private static SolidColorBrush CardHoverBrush => ThemeBrushes.Get("CardHoverBrush");
    private static SolidColorBrush CardSelectedBrush => ThemeBrushes.Get("CardSelectedBrush");
    private static SolidColorBrush AccentBrush => ThemeBrushes.Get("AccentBrush");
    private static SolidColorBrush SuccessBrush => (SolidColorBrush)App.Current.Resources["SuccessBrush"];
    private static SolidColorBrush WarningBrush => (SolidColorBrush)App.Current.Resources["WarningBrush"];
    private static SolidColorBrush MutedBrush => ThemeBrushes.Get("MutedBrush");
    private static readonly SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(255, 0xF0, 0x61, 0x6D));
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private readonly Dictionary<string, CheckBox> _tweakChecks = new();
    private readonly Dictionary<string, Border> _tweakBadges = new();
    private readonly Dictionary<string, Border> _tweakCards = new();
    // Títulos de las cards: se actualizan según el estado de instalación de la app
    // (p. ej. "O&O ShutUp10++ - Ejecutar" → "- Instalar" si no está instalada).
    private readonly Dictionary<string, TextBlock> _tweakTitles = new();
    private List<TweakDefinition>? _allTweaks;

    // ====== PRECONFIGURACIONES ======
    // Espejo de los presets de winutil (Chris Titus Tech): Minimal → Mínimo,
    // Standard → Balanceado, Advanced → Gaming. Se omiten los tweaks que ya viven
    // en otra pestaña de la app (Herramientas tiene "Limpieza de disco", que cubre
    // DiskCleanup + DeleteTempFiles de winutil).
    private static readonly string[] PresetMinimo =
    [
        "ConsumerFeatures - Desactivar",
        "Tabla binaria de plataforma Windows (WPBT) - Desactivar",
        "Servicios - Configurar en Manual",
        "Telemetría - Desactivar"
    ];

    private static readonly string[] PresetBalanceado =
    [
        "Historial de actividad - Desactivar",
        "ConsumerFeatures - Desactivar",
        "Detección automática de carpetas en Explorador - Desactivar",
        "Tabla binaria de plataforma Windows (WPBT) - Desactivar",
        "Seguimiento de ubicación - Desactivar",
        "Servicios - Configurar en Manual",
        "Telemetría - Desactivar",
        "Optimización de entrega - Desactivar",
        "Finalizar tarea con clic derecho - Activar",
        "Punto de restauración - Crear"
    ];

    private static readonly string[] PresetGaming =
    [
        "Punto de restauración - Crear",
        "Historial de actividad - Desactivar",
        "ConsumerFeatures - Desactivar",
        "Detección automática de carpetas en Explorador - Desactivar",
        "Tabla binaria de plataforma Windows (WPBT) - Desactivar",
        "Seguimiento de ubicación - Desactivar",
        "Servicios - Configurar en Manual",
        "Telemetría - Desactivar",
        "Optimización de entrega - Desactivar",
        "Finalizar tarea con clic derecho - Activar",
        "Resultados recomendados de Microsoft Store - Desactivar",
        "Diseño anterior del menú Inicio - Activar",
        "Menú contextual anterior - Activar"
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

            // Al cambiar el idioma, reconstruir las cards (títulos, descripciones
            // y badges se crean en código con I18n.T; el recorrido del árbol visual
            // no los alcanza). La página vive con NavigationCacheMode, así que se
            // suscribe una sola vez como el tema.
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

            // Actualizar el badge al instante cuando un tweak se aplica/revierte
            // (el refresco por lote puede tardar o fallar con cachés intermedias).
            // La página vive con NavigationCacheMode, así que se suscribe una vez.
            _tweakService.TweakStateChanged += OnTweakStateChanged;
        }
        catch (Exception ex)
        {
            DebugText.Text = $"Error: {ex.Message}";
            try { _loggingService.LogError($"Error en OnLoaded OptimizacionesPage: {ex}", ex); } catch { }
        }
    }

    private void OnTweakStateChanged(string tweakId, bool applied)
    {
        try
        {
            var def = _allTweaks?.FirstOrDefault(t => t.Id == tweakId);
            if (def == null) return;
            if (_tweakBadges.TryGetValue(def.Name, out var badge))
                badge.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            try { _loggingService.LogError("Error actualizando badge por TweakStateChanged", ex); } catch { }
        }
    }

    private void LoadTweaks()
    {
        TweaksPanel.Children.Clear();
        PresetsPanel.Children.Clear();
        _tweakChecks.Clear();
        _tweakBadges.Clear();
        _tweakCards.Clear();
        _tweakTitles.Clear();

        // Cachear definiciones UNA sola vez (evita GetAllTweaks() por cada tarjeta).
        _allTweaks = _tweakService.GetAllTweaks();

        BuildPresets();

        var essential = GetEssentialTweaks();
        var advanced = GetAdvancedTweaks();

        // Dos columnas: esenciales a la izquierda, avanzados a la derecha.
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var essentialSection = BuildSection("Essential Tweaks", I18n.T("Tweaks recomendados para todos los sistemas"), essential);
        var advancedSection = BuildSection("Advanced Tweaks", I18n.T("Requieren precaución, verificar compatibilidad"), advanced);

        Grid.SetColumn(essentialSection, 0);
        Grid.SetColumn(advancedSection, 1);
        grid.Children.Add(essentialSection);
        grid.Children.Add(advancedSection);
        TweaksPanel.Children.Add(grid);

        // Verificar estados aplicados en segundo plano para no bloquear la UI.
        _ = RefreshBadgesAsync();

        _loggingService.LogInfo($"OptimizacionesPage: {essential.Count + advanced.Count} tweaks cargados");
    }

    // ====== PRECONFIGURACIONES ======

    private void BuildPresets()
    {
        // Glifos monocromos MDL2 con el acento de la app (mismo estilo que el resto de la app).
        var presets = new (string Glyph, string Name, string Subtitle, string[] Tweaks)[]
        {
            ("\uE72E", "Mínimo", "Privacidad esencial", PresetMinimo),          // Lock
            ("\uE722", "Balanceado", "Recomendado para todos los sistemas", PresetBalanceado), // SpeedMedium
            ("\uE7FC", "Gaming", "Perfil agresivo de máximo rendimiento", PresetGaming)        // Game
        };

        foreach (var (glyph, name, subtitle, tweaks) in presets)
        {
            // Glifo monocromo con el acento de la app + título/subtítulo con fuente normal.
            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 20,
                Foreground = AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var textPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            textPanel.Children.Add(new TextBlock
            {
                Text = I18n.T(name),
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = I18n.T("{0} tweaks · {1}", tweaks.Length, I18n.T(subtitle)),
                FontSize = 11,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap
            });

            var content = new Grid { ColumnSpacing = 12 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(icon);
            Grid.SetColumn(textPanel, 1);
            content.Children.Add(textPanel);

            var button = new Button
            {
                // Sin reborde. BorderThickness 0 explícito: si se omite, el template
                // por defecto del Button dibuja su propio borde (ButtonBorderBrush).
                Content = content,
                Background = CardBrush,
                BorderBrush = TransparentBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Width = 200,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            button.Click += (s, e) => ApplyPreset(tweaks);
            PresetsPanel.Children.Add(button);
        }
    }

    private void ApplyPreset(string[] tweaks)
    {
        foreach (var (tweakName, check) in _tweakChecks)
        {
            check.IsChecked = tweaks.Contains(tweakName);
        }
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

        // Igual que WinUtil: la sección avanzada se marca como "CAUTION" (título ámbar + ⚠️)
        // para distinguirla visualmente de los tweaks esenciales.
        var isCaution = title.Contains("Advanced", StringComparison.OrdinalIgnoreCase);

        var headerStack = new StackPanel { Spacing = 2 };
        var titleBlock = new TextBlock
        {
            Text = I18n.T(title),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 18
        };
        // NO setear Foreground en null: rompe la herencia del tema y el título queda invisible.
        // Solo la sección Advanced pinta su título de ámbar (estilo CAUTION de WinUtil).
        if (isCaution) titleBlock.Foreground = WarningBrush;
        headerStack.Children.Add(titleBlock);
        headerStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            Foreground = MutedBrush
        });

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        headerRow.Children.Add(chevron);
        // Ícono de advertencia monocromo (E7BA) en lugar del emoji ⚠️ a color.
        if (isCaution)
        {
            headerRow.Children.Add(new FontIcon
            {
                Glyph = "\uE7BA",
                FontSize = 16,
                Foreground = WarningBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
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

        // Contenido: título + botón de info + descripción
        var content = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var titleBlock = new TextBlock
        {
            // Nombre completo (ej: "Microsoft OneDrive - Eliminar") para entender la acción a primera vista.
            // Sin Foreground explícito: hereda el color de texto del tema (claro/oscuro). Se actualiza
            // en RefreshBadgesAsync según el estado de instalación ("- Ejecutar" / "- Instalar").
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
    }

    /// <summary>
    /// Re-aplica los colores de tema a las cards de tweaks y a los botones de
    /// preconfiguración sin reconstruirlas (preserva la selección del usuario).
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

        foreach (var child in PresetsPanel.Children)
        {
            if (child is Button button)
                button.Background = CardBrush;
        }
    }

    // ====== SELECCIÓN / LOTE ======

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

        // La consola vive al final del ScrollViewer: al aparecer la primera vez, scrollear
        // hasta ella para que el usuario vea el feedback del lote que acaba de ejecutar.
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
            ShowNotification(I18n.T("No hay tweaks seleccionados. Marcá los que quieras o usá una preconfiguración."), "warning");
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
            ShowNotification(I18n.T("No hay tweaks seleccionados. Marcá los que quieras o usá una preconfiguración."), "warning");
            return;
        }

        SetBatchBusy(true);
        int ok = 0, failed = 0, skipped = 0;
        var failures = new List<string>();

        var verb = apply ? "Aplicar" : "Revertir";
        AppendConsole(I18n.T("{0} {1} tweaks seleccionados...", I18n.T(verb), selected.Count), ConsoleStatus.Neutral);

        // Estilo cmd/winutil: reporta los comandos reales que ejecuta cada tweak.
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
            // Los botones SIEMPRE se rehabilitan, aunque algo falle.
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
        // Verificar todos los estados en background para no congelar la UI al abrir la página.
        Dictionary<string, (bool Applied, bool Installed)> states;
        try
        {
            states = await Task.Run(() =>
            {
                var dict = new Dictionary<string, (bool, bool)>();
                foreach (var (tweakName, _) in _tweakBadges)
                {
                    var def = _allTweaks?.FirstOrDefault(t => t.Name == tweakName);
                    dict[tweakName] = def != null
                        ? (_tweakService.IsTweakApplied(def.Id), _tweakService.IsTweakAppInstalled(def.Id))
                        : (false, true);
                }
                return dict;
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error verificando estados de tweaks", ex);
            return;
        }

        foreach (var (tweakName, state) in states)
        {
            if (_tweakBadges.TryGetValue(tweakName, out var badge))
                badge.Visibility = state.Applied ? Visibility.Visible : Visibility.Collapsed;

            // Título dinámico: si la app del tweak no está instalada y hay un nombre
            // alternativo (p. ej. "O&O ShutUp10++ - Instalar" vs "- Ejecutar"), se muestra.
            if (_tweakTitles.TryGetValue(tweakName, out var title))
            {
                var def = GetTweakDefinition(tweakName);
                if (def != null)
                {
                    var display = !state.Installed && !string.IsNullOrEmpty(def.NameWhenNotInstalled)
                        ? def.NameWhenNotInstalled!
                        : def.Name;
                    var translated = I18n.T(display);
                    if (title.Text != translated) title.Text = translated;
                }
            }
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
        AddTweak(list, "Detección automática de carpetas en Explorador - Desactivar", "El Explorador intenta adivinar el tipo de carpeta según su contenido, ralentizando la navegación. ¡ADVERTENCIA! Desactivará la agrupación del Explorador.", "Compatible con Windows 10/11", true, "Essential Tweaks");
        return list;
    }

    private List<TweakInfo> GetAdvancedTweaks()
    {
        var list = new List<TweakInfo>();
        AddTweak(list, "Advertencias de archivos RDP sin firmar - Desactivar", "Desactiva las advertencias al lanzar archivos RDP sin firmar introducidas en las últimas actualizaciones.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Fecha y hora - Configurar en UTC", "Esencial para equipos con dual-boot. Corrige la sincronización horaria con sistemas Linux.", "Solo dual-boot con Linux", true, "Advanced Tweaks");
        AddTweak(list, "Inicio y Galería del Explorador - Desactivar", "Elimina Inicio y Galería del Explorador y establece Este PC como predeterminado.", "Compatible con Windows 11", true, "Advanced Tweaks");
        AddTweak(list, "Efectos visuales - Configurar en Máximo rendimiento", "Configura las preferencias del sistema a rendimiento. Puedes hacerlo manualmente con sysdm.cpl.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Almacenamiento reservado - Desactivar", "Desactiva el almacenamiento reservado de Windows (7-10 GB para actualizaciones). Solo recomendado en discos pequeños. Re-activar antes de grandes actualizaciones.", "Solo en discos pequeños", true, "Advanced Tweaks");
        AddTweak(list, "Storage Sense - Desactivar", "Storage Sense elimina archivos temporales automáticamente.", "Compatible con Windows 10/11", true, "Advanced Tweaks");

        AddTweak(list, "Notificaciones del sistema y calendario - Desactivar", "Desactiva todas las notificaciones INCLUYENDO el calendario.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Menú contextual anterior - Activar", "Restaura el menú contextual clásico del Explorador, reemplazando la versión simplificada de Windows 11.", "Compatible con Windows 11", true, "Advanced Tweaks");
        AddTweak(list, "IPv6 - Configurar IPv4 como preferido", "Configurar la preferencia IPv4 puede tener beneficios de latencia y seguridad en redes privadas sin IPv6.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Teredo - Desactivar", "Teredo es un túnel IPv6 que puede causar latencia adicional, aunque puede causar problemas con algunos juegos.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "IPv6 - Desactivar", "Desactiva IPv6.", "Requiere precaución", true, "Advanced Tweaks");
        AddTweak(list, "Apps en segundo plano - Desactivar", "Desactiva todas las apps de Microsoft Store en segundo plano, lo que debe hacerse individualmente desde Windows 11.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Optimizaciones de pantalla completa - Desactivar", "Desactiva FSO en todas las aplicaciones. NOTA: Desactivará la gestión de color en pantalla completa exclusiva.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
        AddTweak(list, "Barra de juegos (Game Bar) - Desactivar", "Desactiva la barra de juegos de Xbox (Win+G) y la grabación en segundo plano (Game DVR), que pueden robar rendimiento en juegos. Revertible desde la app.", "Compatible con Windows 10/11", true, "Advanced Tweaks");
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
