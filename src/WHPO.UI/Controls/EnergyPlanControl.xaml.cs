using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Controls;

namespace WHPO_UI.Controls;

/// <summary>
/// Bloque de energía de la app: selector del plan de energía activo, gestión de
/// planes (renombrar / activar / borrar / instalar) y comparación entre dos
/// planes. El techo de turbo vive en el componente Núcleos (TurboCeilingControl).
///
/// Antes vivía dentro de la pestaña "Núcleos"; ahora es un control propio que
/// la categoría "Energía" de la pantalla de Optimizaciones monta cuando se abre.
/// </summary>
public sealed partial class EnergyPlanControl : UserControl
{
    private readonly ICpuPowerService _cpuPowerService;
    private readonly IWinUtilService _winUtilService;
    private readonly ILoggingService _loggingService;

    // ---- Navbar interno (Energía / Gestionar / Comparar) ----
    private int _selectedTabIndex;
    private bool _manageLoaded;
    private bool _compareLoaded;

    /// <summary>Pestaña interna activa (0 = Energía, 1 = Gestionar, 2 = Comparar).</summary>
    public int SelectedTabIndex => _selectedTabIndex;

    /// <summary>
    /// Avisa a quien monta el control que cambió la pestaña interna: la página de
    /// Optimizaciones lo usa para ocultar las optimizaciones en las pestañas de
    /// gestión y comparación, donde no tienen nada que ver.
    /// </summary>
    public event Action<int>? TabChanged;

    /// <summary>Relleno de la barra de pestañas interna (PlanTabs), reusado entre pasadas.</summary>
    private Brush? _navBarFill;

    /// <summary>Corta las reentradas del repaso de idioma (ver ApplyLanguageToControl).</summary>
    private bool _applyingLanguage;

    /// <summary>Ya hay una segunda pasada de idioma en cola.</summary>
    private bool _languagePassScheduled;

    public EnergyPlanControl()
    {
        InitializeComponent();
        _cpuPowerService = App.Services.GetRequiredService<ICpuPowerService>();
        _winUtilService = App.Services.GetRequiredService<IWinUtilService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();

        // El navbar INTERNO se pinta a mano (ver PaintNavBar): su {ThemeResource} no sigue al ajuste
        // de apariencia, así que la franja se quedaba plana mientras las cards ya tenían vidrio.
        Loaded += (s, e) =>
        {
            PanelAppearance.Applied -= PaintNavBar;
            PanelAppearance.Applied += PaintNavBar;
            PaintNavBar();
            EnsureInitialSelection();
            Refresh();
        };
        Unloaded += (s, e) =>
        {
            I18n.LanguageChanged -= OnLanguageChanged;
            PanelAppearance.Applied -= PaintNavBar;
        };

        // El evento de idioma es estático y vive toda la app: se suscribe una
        // sola vez y se da de baja al desmontar el control.
        I18n.LanguageChanged += OnLanguageChanged;
    }

    // ===================== Paleta de tema =====================
    // Los colores viven en los recursos de tema de la app (claro/oscuro) y se
    // resuelven con el tema EFECTIVO (ThemeBrushes), no con el del sistema.
    private static SolidColorBrush ThemeBrush(string key) => ThemeBrushes.Get(key);

    private static SolidColorBrush BGreen => ThemeBrush("MetricTempBrush");
    private static SolidColorBrush BTimeLabel => ThemeBrush("ChartAxisTextBrush");
    private static SolidColorBrush BNeutral => ThemeBrush("ChartAxisTextBrush");

    /// <summary>
    /// Fondo de las cards de plan (un tono distinto al de la card grande, para que
    /// se lean como tabla): es una card, así que lleva el pincel de superficie
    /// (vidrio del motor incluido) como cualquier otra.
    /// </summary>
    private static Brush RowBackgroundBrush =>
        ThemeBrushes.GetSurface(ThemeBrushes.ActiveThemeKey() == "Light" ? "ChartHoverBadgeBgBrush" : "ChartBackgroundBrush");

    /// <summary>
    /// Superficie de card del tema: es la que el ajuste de transparencia atenúa y
    /// la que el desenfoque reconoce como panel (ver PanelBlurLayer).
    /// </summary>
    private static Brush CardBrush => ThemeBrushes.GetSurface("CardBackgroundBrush");

    /// <summary>Texto secundario según el tema (claro/oscuro).</summary>
    private SolidColorBrush SecondaryBrush =>
        ActualTheme == ElementTheme.Light
            ? new SolidColorBrush(Color.FromArgb(255, 0x5D, 0x63, 0x6B))
            : new SolidColorBrush(Color.FromArgb(255, 0x9A, 0xA0, 0xA6));

    /// <summary>
    /// Relee el plan activo. La categoría "Energía" lo llama cada vez que se abre,
    /// para que el selector no muestre un plan viejo.
    /// </summary>
    public void Refresh() => _ = LoadPowerPlansAsync();

    // ===================== Navbar interno =====================

    /// <summary>
    /// Pinta la barra interna con el color del panel del menú, el alfa de la
    /// transparencia vigente y el vidrio que esté en juego.
    /// </summary>
    private void PaintNavBar()
    {
        try
        {
            _navBarFill = PanelAppearance.PaneBarFill(_navBarFill);
            PlanNavBar.Background = _navBarFill;
        }
        catch { /* arranque temprano */ }
    }

    // ===================== Idioma =====================

    private void OnLanguageChanged()
    {
        // Las filas de planes guardan textos traducidos al crearse. Reconstruirlas
        // desde la fuente actual evita conservar el idioma anterior.
        if (_manageLoaded)
        {
            _ = LoadManagePlansAsync();
            _ = LoadInstallPresetsAsync();
        }

        // El marcador "(activo)" se forma al crear cada ComboBoxItem; recargarlo
        // asegura que el selector no conserve el idioma anterior.
        _ = LoadPowerPlansAsync();

        UpdateColorButtons();
        ApplyLanguageAfterLayout();
    }

    private void ApplyLanguageAfterLayout()
    {
        ApplyLanguageToControl();

        // Una sola segunda pasada en cola: antes cada llamada encolaba DOS más, y
        // con todas las cargas de la pestaña (planes, presets, comparación) se
        // apilaban pasadas de más sobre el mismo control.
        if (_languagePassScheduled) return;
        _languagePassScheduled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (EnergiaTab.Visibility == Visibility.Visible) EnergiaTab.UpdateLayout();
                if (GestionarTab.Visibility == Visibility.Visible) GestionarTab.UpdateLayout();
                if (CompararTab.Visibility == Visibility.Visible) CompararTab.UpdateLayout();
            }
            catch { }

            ApplyLanguageToControl();
            DispatcherQueue.TryEnqueue(() =>
            {
                _languagePassScheduled = false;
                ApplyLanguageToControl();
            });
        });
    }

    private void ApplyLanguageToControl()
    {
        // Reentrancia: el repaso de idioma toca textos y mide el control, y eso
        // puede volver a entrar por una carga en curso. Sin este corte, una
        // pestaña que se traduce a sí misma se llamaba sin fin (pila desbordada).
        if (_applyingLanguage) return;
        _applyingLanguage = true;

        try
        {
            I18n.ApplyToVisualTree(this);

            // Estos controles pueden no tener template visual realizado todavía.
            PowerPlanCombo.PlaceholderText = I18n.T("Cargando planes...");
            ComparePlanACombo.PlaceholderText = I18n.T("Seleccioná un plan");
            ComparePlanBCombo.PlaceholderText = I18n.T("Seleccioná un plan");

            if (PlanTabs != null)
            {
                foreach (var item in PlanTabs.Items.OfType<SelectorBarItem>())
                {
                    if (item.Text is string s && Translations.TryGetSource(s, I18n.Current, out var source))
                        item.Text = I18n.T(source);
                }

                // Al cambiar el texto de los ítems hay que re-medir la barra: si no,
                // el indicador de selección conserva el ancho anterior y se ve corrido.
                // (No se reasienta la selección: eso re-entraba en ApplyTabVisibility
                // → TranslateCompareTab → repaso de idioma → pila desbordada.)
                PlanTabs.UpdateLayout();
            }
        }
        finally
        {
            _applyingLanguage = false;
        }
    }

    /// <summary>
    /// Deja seleccionada la primera pestaña y avisa al host. Se hace acá y no en
    /// XAML porque el SelectorBar calcula mal su indicador cuando la selección
    /// viene declarativa y el control se monta tarde (el bloque se crea al abrir
    /// la categoría "Energía").
    /// </summary>
    private void EnsureInitialSelection()
    {
        if (PlanTabs == null || PlanTabs.Items.Count == 0) return;

        if (PlanTabs.SelectedItem is null)
        {
            // El handler de SelectionChanged ya hace el resto (alto + aviso).
            PlanTabs.SelectedItem = PlanTabs.Items[0];
            return;
        }

        _selectedTabIndex = Math.Max(0, PlanTabs.Items.IndexOf(PlanTabs.SelectedItem));
        TabChanged?.Invoke(_selectedTabIndex);
    }

    // ===================== Pestañas internas =====================

    private void PlanTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (PlanTabs == null || PlanTabs.Items.Count == 0) return;

        // El SelectorBar también avisa cuando la selección queda vacía: no hay
        // pestaña que mostrar ni contenido que cargar en ese caso.
        if (PlanTabs.SelectedItem is null) return;

        int idx = PlanTabs.Items.IndexOf(PlanTabs.SelectedItem);
        _selectedTabIndex = idx < 0 ? 0 : idx;
        _loggingService.LogInfo($"Energía: pestaña {_selectedTabIndex}");

        ApplyTabVisibility();
        LazyLoadTab(_selectedTabIndex);

        // El host decide con esto si muestra u oculta los ajustes de la categoría.
        TabChanged?.Invoke(_selectedTabIndex);
    }

    private void ApplyTabVisibility()
    {
        if (EnergiaTab == null || GestionarTab == null || CompararTab == null) return;
        EnergiaTab.Visibility = _selectedTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        GestionarTab.Visibility = _selectedTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CompararTab.Visibility = _selectedTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        // Traducir la pestaña Comparar cuando se hace visible
        if (_selectedTabIndex == 2)
        {
            TranslateCompareTab();
        }
    }

    private void LazyLoadTab(int index)
    {
        if (index == 1 && !_manageLoaded)
        {
            _manageLoaded = true;
            _ = LoadManagePlansAsync();
            _ = LoadInstallPresetsAsync();
        }
        if (index == 2 && !_compareLoaded)
        {
            _compareLoaded = true;
            LoadCompareCombos();
        }
    }

    // ===================== Plan de energía =====================

    // El popup del ComboBox abre con un VerticalOffset que WinUI 3 calcula mal cuando
    // el combo está dentro del Frame/ScrollViewer: queda desplazado hacia arriba. En
    // cada apertura alineamos el tope del popup con el tope del combo (igual que el de
    // "Tipo de test"), una vez que el popup ya está medido.
    private async void PowerPlanCombo_DropDownOpened(object sender, object e)
    {
        var combo = (ComboBox)sender;
        try
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(16);
                var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(combo.XamlRoot);
                if (popups.Count == 0 || popups[0].Child is not FrameworkElement fe || fe.ActualHeight <= 0)
                    continue;

                var popup = popups[0];
                var popupPos = fe.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                var comboPos = combo.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                double delta = comboPos.Y - popupPos.Y;
                if (Math.Abs(delta) < 0.5) break;
                popup.VerticalOffset += delta;
                break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"POWERPLAN reposition: {ex.Message}");
        }
    }

    private async Task LoadPowerPlansAsync()
    {
        try
        {
            // GetPowerPlans ya maneja sus errores internamente y devuelve lista vacía.
            var plans = await Task.Run(() => _cpuPowerService.GetPowerPlans());

            int activeIndex = -1;
            PowerPlanCombo.Items.Clear();
            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                PowerPlanCombo.Items.Add(new ComboBoxItem
                {
                    Content = plan.IsActive ? I18n.T("{0}  (activo)", plan.Name) : plan.Name,
                    Tag = plan.Guid
                });
                if (plan.IsActive) activeIndex = i;
            }

            PowerPlanCombo.SelectedIndex = activeIndex >= 0 ? activeIndex : (plans.Count > 0 ? 0 : -1);
            ApplyPowerPlanButton.IsEnabled = PowerPlanCombo.SelectedIndex >= 0;
            // No pisar el mensaje de confirmación/error cuando la carga es exitosa.
            if (plans.Count == 0)
                Feedback.Warning(PowerPlanStatusText, "No se detectaron planes de energía.");
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error cargando planes de energía: {ex.Message}");
            Feedback.Warning(PowerPlanStatusText, "No se pudieron cargar los planes de energía.");
        }
    }

    private async void ApplyPowerPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (PowerPlanCombo.SelectedItem is not ComboBoxItem { Tag: string planGuid })
            return;
        var planName = (PowerPlanCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? planGuid;

        ApplyPowerPlanButton.IsEnabled = false;            Feedback.Running(PowerPlanStatusText, I18n.T("Aplicando plan..."));
        try
        {
            var result = await _cpuPowerService.SetActivePowerPlanAsync(planGuid);

            // La selección del combo no representa el estado del sistema: volver a leer ambos
            // desde Windows asegura que la card muestre el plan que realmente quedó activo.
            await LoadPowerPlansAsync();

            if (result.Success)
                Feedback.Success(PowerPlanStatusText, I18n.T("Plan de energía establecido: {0}", planName));
            else
                Feedback.Result(PowerPlanStatusText, result);
        }
        catch (Exception ex)
        {
            Feedback.Error(PowerPlanStatusText, ex.Message);
            _loggingService.LogWarning($"NucleosPage: error aplicando plan de energía: {ex.Message}");
        }
        finally
        {
            ApplyPowerPlanButton.IsEnabled = PowerPlanCombo.SelectedIndex >= 0;
        }
    }

    // ===================== Encabezados plegables =====================

    private void ManageHeaderButton_Click(object sender, RoutedEventArgs e)
        => ToggleSection(ManageBody, ManageChevron);

    private void InstallHeaderButton_Click(object sender, RoutedEventArgs e)
        => ToggleSection(InstallBody, InstallChevron);

    /// <summary>
    /// Pliega o despliega el cuerpo de una card sin tocar su estilo: solo cambia
    /// la visibilidad del bloque y el glifo del chevron (abajo = abierto).
    /// </summary>
    private static void ToggleSection(FrameworkElement body, FontIcon chevron)
    {
        bool expand = body.Visibility != Visibility.Visible;
        body.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        chevron.Glyph = expand ? "\uE70D" : "\uE76C";
    }

    // ===================== Gestionar planes de energía =====================

    private sealed class PlanListItem
    {
        public string Guid { get; }
        public string Name { get; }
        public string Description { get; }
        public bool IsActive { get; }

        public PlanListItem(string guid, string name, string description, bool isActive)
        {
            Guid = guid;
            Name = name;
            Description = description;
            IsActive = isActive;
        }
    }

    private async Task LoadManagePlansAsync()
    {
        try
        {
            _loggingService.LogInfo("Energía: cargando gestión de planes");
            var plans = await Task.Run(() => _cpuPowerService.GetPowerPlans());

            var items = new List<PlanListItem>();
            foreach (var plan in plans)
            {
                var desc = await Task.Run(() => _cpuPowerService.GetPowerPlanDescription(plan.Guid));
                items.Add(new PlanListItem(plan.Guid, plan.Name, desc, plan.IsActive));
            }

            PlansList.Items.Clear();
            foreach (var item in items)
                PlansList.Items.Add(BuildManageRow(item));

            if (items.Count == 0)
            {
                ManagePlanStatusText.Visibility = Visibility.Visible;
                Feedback.Warning(ManagePlanStatusText, "No se detectaron planes de energía.");
            }

            _loggingService.LogInfo($"Energía: gestión de planes lista ({items.Count} planes)");

            // Traducir el contenido recién creado cuando sus templates ya existen.
            ApplyLanguageAfterLayout();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error cargando gestión de planes: {ex.Message}");
            ManagePlanStatusText.Visibility = Visibility.Visible;
            Feedback.Warning(ManagePlanStatusText, "No se pudieron cargar los planes de energía.");
        }
    }

 // Plantilla plana para los ítems de las listas de planes: sin los visuales
 // por defecto de ListViewItem (hover/selección del sistema), que asomaban
 // alrededor de la card y generaban un "doble fondo". El hover y la selección
 // se manejan a mano con los mismos brushes del navbar (CardHover/CardSelected).
    private static Microsoft.UI.Xaml.Controls.ControlTemplate? _flatListItemTemplate;
    private static Microsoft.UI.Xaml.Controls.ControlTemplate FlatListItemTemplate()
    {
        if (_flatListItemTemplate == null)
        {
            const string xaml =
                "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "TargetType=\"ListViewItem\">" +
                "<ContentPresenter HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\"/>" +
                "</ControlTemplate>";
            _flatListItemTemplate = (Microsoft.UI.Xaml.Controls.ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        }
        return _flatListItemTemplate;
    }

 // Estados visuales de una card de plan: normal / hover / seleccionada.
    private static void PlanRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewItem { IsSelected: false } lv && lv.Content is Border b)
            b.Background = ThemeBrushes.Get("CardHoverBrush");
    }

    private static void PlanRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewItem { IsSelected: false } lv && lv.Content is Border b)
            b.Background = RowBackgroundBrush;
    }

    private static void RefreshPlanRowVisuals(ListView list)
    {
        foreach (var i in list.Items)
            if (i is ListViewItem lv && lv.Content is Border b)
                b.Background = lv.IsSelected
                    ? ThemeBrushes.Get("CardSelectedBrush")
                    : RowBackgroundBrush;
    }

    private static ListViewItem BuildPlanContainer(object tag, Border card)
    {
        var lvItem = new ListViewItem
        {
            Content = card,
            Tag = tag,
            Template = FlatListItemTemplate(),
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        lvItem.PointerEntered += PlanRow_PointerEntered;
        lvItem.PointerExited += PlanRow_PointerExited;
        return lvItem;
    }

    private static ListViewItem BuildManageRow(PlanListItem item)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var guid = new TextBlock
        {
            Text = item.Guid,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(guid, 1);
        row.Children.Add(guid);

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Description) ? "—" : item.Description,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(desc, 2);
        row.Children.Add(desc);

        var active = new TextBlock
        {
            Text = item.IsActive ? I18n.T("✓ Activo") : "",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = item.IsActive ? BGreen : BTimeLabel
        };
        Grid.SetColumn(active, 3);
        row.Children.Add(active);

 // Card del plan con UN solo fondo: la plantilla plana del contenedor elimina
 // el hover/selección por defecto del sistema; el estilo de selección replica
 // el de las pestañas del navbar (CardSelectedBrush).
        var card = new Border
        {
            Background = RowBackgroundBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = row
        };
        return BuildPlanContainer(item, card);
    }

    private void PlansList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshPlanRowVisuals(PlansList);
        var item = (PlansList.SelectedItem as ListViewItem)?.Tag as PlanListItem;
        bool has = item != null;
        RenamePlanButton.IsEnabled = has;
        ActivatePlanButton.IsEnabled = has && !item!.IsActive;
        DeletePlanButton.IsEnabled = has && !item!.IsActive;
    }

    private void SetManageStatus(bool success, string message, string? template = null, object?[]? args = null)
    {
        var text = template != null
            ? I18n.T(template, args ?? Array.Empty<object?>())
            : I18n.T(message);
        if (success)
            Feedback.Success(ManagePlanStatusText, text);
        else
            Feedback.Error(ManagePlanStatusText, text);
    }

    private async void RenamePlanButton_Click(object sender, RoutedEventArgs e)
    {
        var item = (PlansList.SelectedItem as ListViewItem)?.Tag as PlanListItem;
        if (item == null || XamlRoot == null) return;

        var nameBox = new TextBox
        {
            Text = item.Name,
            Header = I18n.T("Nuevo nombre"),
            MaxLength = 255
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Renombrar plan"),
            Content = nameBox,
            PrimaryButtonText = I18n.T("Renombrar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var newName = nameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            SetManageStatus(false, "El nombre no puede estar vacío.");
            return;
        }

        var result = await _cpuPowerService.RenamePowerPlanAsync(item.Guid, newName);
        SetManageStatus(result.Success, result.Output, result.MessageTemplate, result.MessageArgs);
        if (result.Success)
        {
            await LoadManagePlansAsync();
            _ = LoadPowerPlansAsync(); // refrescar el selector de la pestaña Núcleos
        }
    }

    private async void ActivatePlanButton_Click(object sender, RoutedEventArgs e)
    {
        var item = (PlansList.SelectedItem as ListViewItem)?.Tag as PlanListItem;
        if (item == null) return;

        ActivatePlanButton.IsEnabled = false;
        Feedback.Running(ManagePlanStatusText, "Activando plan...");
        var result = await _cpuPowerService.SetActivePowerPlanAsync(item.Guid);
        SetManageStatus(result.Success, result.Output, result.MessageTemplate, result.MessageArgs);
        if (result.Success)
        {
            await LoadManagePlansAsync();
            _ = LoadPowerPlansAsync(); // refrescar el selector de la pestaña Núcleos
        }
    }

    private async void OpenPowerOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _winUtilService.LaunchPanelAsync("power");
            if (!result.Success) SetManageStatus(false, result.Output, result.MessageTemplate, result.MessageArgs);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error abriendo planes de energía: {ex.Message}");
            SetManageStatus(false, ex.Message);
        }
    }

    private async void DeletePlanButton_Click(object sender, RoutedEventArgs e)
    {
        var item = (PlansList.SelectedItem as ListViewItem)?.Tag as PlanListItem;
        if (item == null || XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Borrar plan de energía"),
            Content = $"{I18n.T("¿Seguro que querés borrar ")}\"{item.Name}\"{I18n.T("? Esta acción no se puede deshacer.")}",
            PrimaryButtonText = I18n.T("Borrar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        DeletePlanButton.IsEnabled = false;
        var result = await _cpuPowerService.DeletePowerPlanAsync(item.Guid);
        SetManageStatus(result.Success, result.Output, result.MessageTemplate, result.MessageArgs);
        if (result.Success)
        {
            await LoadManagePlansAsync();
            await LoadInstallPresetsAsync(); // el estado "Instalado" de la lista depende de los planes reales
            _ = LoadPowerPlansAsync(); // refrescar el selector de la pestaña Núcleos
        }
    }

    // ===================== Instalar planes de energía =====================

    // GUIDs de subgrupos/settings de energía (documentados por Microsoft).
    private const string SubProcessorGuid = "54533251-82be-4824-96c1-47b60b740d00";
    private const string ProcThrottleMinGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string ProcThrottleMaxGuid = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    private const string PerfBoostModeGuid = "be337238-0d82-4146-a960-4f3749d470c7";
    private const string PerfAutonomousGuid = "8baa4a8a-14c6-4451-8e8b-14bdbd197537";

    // Esquemas base de Windows.
    private const string BalancedSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerfSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string PowerSaverSchemeGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string UltimatePerfSchemeGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private sealed class InstallPreset
    {
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public string? BuiltInGuid { get; }
        public string? BaseGuid { get; }
        public PowerPlanTuning[]? Tunings { get; }

        public InstallPreset(string name, string description, string category, string? builtInGuid = null, string? baseGuid = null, PowerPlanTuning[]? tunings = null)
        {
            Name = name;
            Description = description;
            Category = category;
            BuiltInGuid = builtInGuid;
            BaseGuid = baseGuid;
            Tunings = tunings;
        }
    }

    private sealed class InstallRowItem
    {
        public InstallPreset Preset { get; }
        public bool Installed { get; }

        public InstallRowItem(InstallPreset preset, bool installed)
        {
            Preset = preset;
            Installed = installed;
        }
    }

    private static List<InstallPreset> BuildInstallPresets() => new()
    {
        new InstallPreset(I18n.T("Máximo rendimiento"), I18n.T("Plan oculto de Windows para máximo desempeño (Ultimate Performance)."), I18n.T("Windows"), builtInGuid: UltimatePerfSchemeGuid),
        new InstallPreset(I18n.T("Alto rendimiento"), I18n.T("Plan oficial de Windows para alto desempeño."), I18n.T("Windows"), builtInGuid: HighPerfSchemeGuid),
        new InstallPreset(I18n.T("Ahorro de energía"), I18n.T("Plan oficial de Windows para reducir el consumo."), I18n.T("Windows"), builtInGuid: PowerSaverSchemeGuid),
        new InstallPreset(I18n.T("Equilibrado"), I18n.T("Plan oficial de Windows: balance entre rendimiento y consumo."), I18n.T("Windows"), builtInGuid: BalancedSchemeGuid),
        new InstallPreset(I18n.T("Ryzen Balanced"), I18n.T("Preset estilo 1usmus para CPU AMD Ryzen: mínimo 99%, boost eficiente agresivo y modo autónomo desactivado."), I18n.T("Ryzen"), baseGuid: BalancedSchemeGuid, tunings: new[]
        {
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMinGuid, 99, 99),
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMaxGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, PerfBoostModeGuid, 4, 4),
            new PowerPlanTuning(SubProcessorGuid, PerfAutonomousGuid, 0, 0)
        }),
        new InstallPreset(I18n.T("Ryzen High Performance"), I18n.T("Preset estilo 1usmus para CPU AMD Ryzen: mínimo 100%, boost agresivo y modo autónomo desactivado."), I18n.T("Ryzen"), baseGuid: HighPerfSchemeGuid, tunings: new[]
        {
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMinGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMaxGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, PerfBoostModeGuid, 2, 2),
            new PowerPlanTuning(SubProcessorGuid, PerfAutonomousGuid, 0, 0)
        }),
        new InstallPreset(I18n.T("Bitsum Highest Performance"), I18n.T("Preset de la comunidad: mínimo 100% para evitar el parking de núcleos y boost agresivo."), I18n.T("Comunidad"), baseGuid: HighPerfSchemeGuid, tunings: new[]
        {
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMinGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMaxGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, PerfBoostModeGuid, 2, 2)
        }),
        new InstallPreset(I18n.T("Bitsum Balanced"), I18n.T("Preset de Bitsum para uso general: mínimo 5% y boost eficiente habilitado."), I18n.T("Comunidad"), baseGuid: BalancedSchemeGuid, tunings: new[]
        {
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMinGuid, 5, 5),
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMaxGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, PerfBoostModeGuid, 3, 3)
        })
    };

    private async Task LoadInstallPresetsAsync()
    {
        try
        {
            var plans = await Task.Run(() => _cpuPowerService.GetPowerPlans());
            var presets = BuildInstallPresets();

            InstallPlansList.Items.Clear();
            foreach (var preset in presets)
            {
                bool installed = preset.BuiltInGuid != null
                    ? plans.Any(p => string.Equals(p.Guid, preset.BuiltInGuid, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                    : plans.Any(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
                InstallPlansList.Items.Add(BuildInstallRow(preset, installed));
            }

            // Traducir el contenido recién creado cuando sus templates ya existen.
            ApplyLanguageAfterLayout();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error cargando presets de instalación: {ex.Message}");
            InstallPlanStatusText.Visibility = Visibility.Visible;
            Feedback.Warning(InstallPlanStatusText, "No se pudieron cargar los planes instalables.");
        }
    }

    private static ListViewItem BuildInstallRow(InstallPreset preset, bool installed)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = preset.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var desc = new TextBlock
        {
            Text = preset.Description,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = BTimeLabel
        };
        Grid.SetColumn(desc, 1);
        row.Children.Add(desc);

        var category = new TextBlock
        {
            Text = preset.Category,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BNeutral
        };
        Grid.SetColumn(category, 2);
        row.Children.Add(category);

        var status = new TextBlock
        {
            Text = installed ? I18n.T("Instalado") : "",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = installed ? BGreen : BTimeLabel
        };
        Grid.SetColumn(status, 3);
        row.Children.Add(status);

 // Card del plan, misma mecánica que en Gestionar planes.
        var card = new Border
        {
            Background = RowBackgroundBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = row
        };
        return BuildPlanContainer(new InstallRowItem(preset, installed), card);
    }

    private void InstallPlansList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshPlanRowVisuals(InstallPlansList);
        var item = (InstallPlansList.SelectedItem as ListViewItem)?.Tag as InstallRowItem;
        InstallPlanButton.IsEnabled = item != null && !item.Installed;
    }

    private async void InstallPlanButton_Click(object sender, RoutedEventArgs e)
    {
        var item = (InstallPlansList.SelectedItem as ListViewItem)?.Tag as InstallRowItem;
        if (item == null || item.Installed) return;

        InstallPlanButton.IsEnabled = false;
        Feedback.Running(InstallPlanStatusText, "Instalando plan...");

        var preset = item.Preset;
        var result = preset.BuiltInGuid != null
            ? await _cpuPowerService.InstallBuiltInSchemeAsync(preset.BuiltInGuid)
            : await _cpuPowerService.CreateCustomPowerPlanAsync(preset.Name, preset.BaseGuid!, preset.Tunings!);

        SetInstallStatus(result);
        if (result.Success)
        {
            await LoadManagePlansAsync();
            await LoadInstallPresetsAsync();
            _ = LoadPowerPlansAsync(); // refrescar el selector de la pestaña Núcleos
        }
    }

    private void SetInstallStatus(CommandResult result)
    {
        var text = result.MessageTemplate != null
            ? I18n.T(result.MessageTemplate, result.MessageArgs ?? Array.Empty<object?>())
            : I18n.T(result.Output);
        if (result.Success)
            Feedback.Success(InstallPlanStatusText, text);
        else
            Feedback.Error(InstallPlanStatusText, text);
    }

    // ===================== Comparar planes de energía =====================

    // Colores por defecto de las curvas de comparación: desde los recursos de tema.
    private static Color DefaultColorA => ThemeBrushes.Get("MetricTempBrush").Color;
    private static Color DefaultColorB => ThemeBrushes.Get("MetricPowerBrush").Color;

    private Color _colorA = DefaultColorA;
    private Color _colorB = DefaultColorB;
    private PowerPlanDetail? _detailA;
    private PowerPlanDetail? _detailB;

    private void LoadCompareCombos()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var plans = _cpuPowerService.GetPowerPlans();
                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var plan in plans)
                    {
                        ComparePlanACombo.Items.Add(new ComboBoxItem { Content = plan.Name, Tag = plan.Guid });
                        ComparePlanBCombo.Items.Add(new ComboBoxItem { Content = plan.Name, Tag = plan.Guid });
                    }
                    UpdateColorButtons();
                });
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"NucleosPage: error cargando planes para comparar: {ex.Message}");
            }
        });
    }

    private void TranslateCompareTab()
    {
        // Traducir el contenido de la pestaña Comparar cuando se hace visible.
        if (CompararTab != null && CompararTab.Visibility == Visibility.Visible)
            ApplyLanguageAfterLayout();
    }

    private static string? SelectedPlanGuid(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void ComparePlanCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var guidA = SelectedPlanGuid(ComparePlanACombo);
        var guidB = SelectedPlanGuid(ComparePlanBCombo);

        if (guidA != null && guidB != null && guidA == guidB)
        {
            // No permitir el mismo plan en ambos desplegables
            if (ReferenceEquals(sender, ComparePlanACombo)) ComparePlanACombo.SelectedItem = null;
            else ComparePlanBCombo.SelectedItem = null;
            Feedback.Warning(CompareStatusText, "Los dos planes deben ser diferentes.");
        }
        else
        {
            Feedback.Set(CompareStatusText, null);
        }

        CompareButton.IsEnabled = guidA != null && guidB != null && guidA != guidB;
    }

    private void ColorAButton_Click(object sender, RoutedEventArgs e) => ShowColorPickerFor(planA: true);

    private void ColorBButton_Click(object sender, RoutedEventArgs e) => ShowColorPickerFor(planA: false);

    private void ShowColorPickerFor(bool planA)
    {
        if (XamlRoot == null) return;
        var target = planA ? ColorAButton : ColorBButton;
        var picker = new ColorPicker
        {
            Color = planA ? _colorA : _colorB,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSpectrumVisible = true,
            IsColorSliderVisible = true,
            IsHexInputVisible = true,
            Width = 300
        };
        var flyout = new Flyout { Content = picker, Placement = FlyoutPlacementMode.Bottom };
        flyout.Closed += (_, _) =>
        {
            var c = picker.Color;
            if (planA) _colorA = c; else _colorB = c;
            UpdateColorButtons();
            if (CompareScroll.Visibility == Visibility.Visible)
                BuildComparison();
        };
        flyout.ShowAt(target);
    }

    private void UpdateColorButtons()
    {
        SetColorButtonContent(ColorAButton, _colorA, "Color A");
        SetColorButtonContent(ColorBButton, _colorB, "Color B");
    }

    private static void SetColorButtonContent(Button button, Color color, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock { Text = I18n.T(label), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        var guidA = SelectedPlanGuid(ComparePlanACombo);
        var guidB = SelectedPlanGuid(ComparePlanBCombo);
        if (guidA == null || guidB == null || guidA == guidB) return;

        CompareButton.IsEnabled = false;
        Feedback.Running(CompareStatusText, "Comparando planes...");

        try
        {
            var (da, db) = await Task.Run(() => (_cpuPowerService.GetPowerPlanDetails(guidA), _cpuPowerService.GetPowerPlanDetails(guidB)));
            if (da == null || db == null)
            {
                Feedback.Error(CompareStatusText, "No se pudo leer el detalle de uno de los planes.");
                return;
            }

            _detailA = da;
            _detailB = db;
            BuildComparison();

            // Sin feedback de "comparación lista": la cuadrícula ya muestra las diferencias.
            Feedback.Set(CompareStatusText, null);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error comparando planes: {ex.Message}");
            Feedback.Error(CompareStatusText, I18n.T("No se pudo comparar: {0}", ex.Message));
        }
        finally
        {
            CompareButton.IsEnabled = SelectedPlanGuid(ComparePlanACombo) != null && SelectedPlanGuid(ComparePlanBCombo) != null;
        }
    }

    private void BuildComparison()
    {
        if (_detailA == null || _detailB == null) return;

        ComparePlanAContainer.Children.Clear();
        ComparePlanBContainer.Children.Clear();

        ComparePlanAContainer.Children.Add(BuildPlanHeader(_detailA.Name, _colorA));
        ComparePlanBContainer.Children.Add(BuildPlanHeader(_detailB.Name, _colorB));

        int n = Math.Max(_detailA.Subgroups.Count, _detailB.Subgroups.Count);
        for (int i = 0; i < n; i++)
        {
            var sgA = i < _detailA.Subgroups.Count ? _detailA.Subgroups[i] : null;
            var sgB = i < _detailB.Subgroups.Count ? _detailB.Subgroups[i] : null;

            var cardA = sgA != null ? BuildSubgroupCard(sgA, _detailB, _colorA) : null;
            var cardB = sgB != null ? BuildSubgroupCard(sgB, _detailA, _colorB) : null;

            // Sincronizar el estado de colapso: si se abre/cierra uno, el otro lo sigue,
            // así las filas quedan alineadas entre las dos cuadrículas.
            var expA = SubgroupExpanderOf(cardA);
            var expB = SubgroupExpanderOf(cardB);
            if (expA != null && expB != null)
            {
                expA.Expanding += (_, _) => expB.IsExpanded = true;
                expA.Collapsed += (_, _) => expB.IsExpanded = false;
            }

            if (cardA != null) ComparePlanAContainer.Children.Add(cardA);
            if (cardB != null) ComparePlanBContainer.Children.Add(cardB);
        }

        CompareScroll.Visibility = Visibility.Visible;
        CompareActionsPanel.Visibility = Visibility.Visible;

        // Las cards de los resultados son paneles nuevos: se le pide al desenfoque que los busque en
        // el acto (ver PanelBlurLayer.ContentChanged). Sin esto, el vidrio recién aparecía cuando
        // otro cambio de layout disparaba el barrido periódico, y con la ventana quieta no llegaba.
        PanelBlurLayer.ContentChanged("resultados de la comparación listos");

        // Traducir el contenido de comparación recién creado cuando sus templates ya existen.
        ApplyLanguageAfterLayout();
    }

    private void ExpandAllButton_Click(object sender, RoutedEventArgs e) => SetAllExpandersExpanded(true);

    private void CollapseAllButton_Click(object sender, RoutedEventArgs e) => SetAllExpandersExpanded(false);

    private void SetAllExpandersExpanded(bool expanded)
    {
        foreach (var child in ComparePlanAContainer.Children)
            if (SubgroupExpanderOf(child) is { } expA) expA.IsExpanded = expanded;
        foreach (var child in ComparePlanBContainer.Children)
            if (SubgroupExpanderOf(child) is { } expB) expB.IsExpanded = expanded;
    }

 /// <summary>
 /// El plegable de un subgrupo de los resultados: vive dentro de su card (ver
 /// <see cref="BuildSubgroupCard"/>), que es la que aporta la superficie del tema.
 /// </summary>
    private static Expander? SubgroupExpanderOf(object? container)
        => container is Border { Child: Expander expander } ? expander : null;

    private static Border BuildPlanHeader(string name, Color color)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stack.Children.Add(new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        });
        var planName = new TextBlock
        {
            Text = name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(planName, name);
        stack.Children.Add(planName);
        // El contenido se corre con su propio margen (no con el Padding del Border) para que el
        // tinte del plan pinte la card ENTERA: el encabezado es una sola superficie teñida, y no una
        // caja de color metida adentro de otra.
        stack.Margin = new Thickness(12, 10, 12, 10);

        // El encabezado es parte de los resultados, así que lleva la superficie de card del tema
        // (ver CardBrush): es la que responde a la transparencia y la que el desenfoque reconoce
        // como panel. El tinte del color del plan —lo que distingue la columna A de la B— va encima
        // como capa aparte, para no pisar la superficie del tema.
        var tint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(10)
        };
        var layers = new Grid();
        layers.Children.Add(tint);
        layers.Children.Add(stack);

        return new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = layers
        };
    }

 /// <summary>
 /// Un subgrupo de los resultados: la card del tema con el plegable adentro. La card es la que
 /// hace que las filas de los resultados se vean afectadas por la transparencia y el desenfoque
 /// igual que el resto de la app (ver CardBrush y PanelBlurLayer): sin ella quedaban apoyadas
 /// directamente sobre el fondo del tema y ninguno de los dos deslizadores las alcanzaba.
 /// </summary>
    private Border BuildSubgroupCard(PowerSubgroupInfo sg, PowerPlanDetail? other, Color color)
        => new()
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 5, 10, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Child = BuildSubgroupExpander(sg, other, color)
        };

    private Expander BuildSubgroupExpander(PowerSubgroupInfo sg, PowerPlanDetail? other, Color color)
    {
        int diffCount = 0;
        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(BuildSettingHeaderRow());

        foreach (var setting in sg.Settings)
        {
            var otherSetting = FindSetting(other, sg.Guid, setting.Guid);
            bool acDiff = otherSetting != null && !string.Equals(setting.AcValue, otherSetting.AcValue, StringComparison.OrdinalIgnoreCase);
            bool dcDiff = otherSetting != null && !string.Equals(setting.DcValue, otherSetting.DcValue, StringComparison.OrdinalIgnoreCase);
            if (acDiff || dcDiff) diffCount++;
            body.Children.Add(BuildSettingRow(setting, acDiff, dcDiff, color));
        }

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var subgroupName = new TextBlock
        {
            Text = sg.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(subgroupName, sg.Name);
        header.Children.Add(subgroupName);
        if (diffCount > 0)
        {
            header.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 2),
                Background = new SolidColorBrush(Color.FromArgb(0x28, color.R, color.G, color.B)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"{diffCount} diff",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        return new Expander
        {
            Header = header,
            Content = body,
            // El plegable vive DENTRO de la card de resultados (ver BuildSubgroupCard): sin relleno
            // ni contorno propios, así la superficie —y su parche de vidrio— es una sola.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = true
        };
    }

    private static PowerSettingInfo? FindSetting(PowerPlanDetail? other, string subgroupGuid, string settingGuid)
    {
        if (other == null) return null;
        var sg = other.Subgroups.FirstOrDefault(x => string.Equals(x.Guid, subgroupGuid, StringComparison.OrdinalIgnoreCase));
        return sg?.Settings.FirstOrDefault(x => string.Equals(x.Guid, settingGuid, StringComparison.OrdinalIgnoreCase));
    }

    private static Grid BuildSettingHeaderRow()
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
        AddSettingColumns(grid);

        grid.Children.Add(new TextBlock { Text = I18n.T("ID de configuración"), FontSize = 11, FontWeight = FontWeights.SemiBold });
        var nombre = new TextBlock { Text = I18n.T("Nombre"), FontSize = 11, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(nombre, 1);
        grid.Children.Add(nombre);
        var ac = new TextBlock { Text = I18n.T("Valor AC"), FontSize = 11, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(ac, 2);
        grid.Children.Add(ac);
        var dc = new TextBlock { Text = I18n.T("Valor DC"), FontSize = 11, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(dc, 3);
        grid.Children.Add(dc);
        return grid;
    }

    private static void AddSettingColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
    }

    private Border BuildSettingRow(PowerSettingInfo setting, bool acDiff, bool dcDiff, Color color)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 5, 4, 5) };
        AddSettingColumns(row);

        if (acDiff || dcDiff)
            row.Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B));

        var highlight = new SolidColorBrush(color);
        var normal = SecondaryBrush;

        var guidTb = new TextBlock
        {
            Text = setting.Guid,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(guidTb, setting.Guid);
        row.Children.Add(guidTb);

        var nombre = new TextBlock
        {
            Text = setting.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        ToolTipService.SetToolTip(nombre, setting.Name);
        Grid.SetColumn(nombre, 1);
        row.Children.Add(nombre);

        var ac = new TextBlock
        {
            Text = FormatDisplayValue(setting.AcValue),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = acDiff ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = acDiff ? highlight : normal
        };
        ToolTipService.SetToolTip(ac, ac.Text);
        Grid.SetColumn(ac, 2);
        row.Children.Add(ac);

        var dc = new TextBlock
        {
            Text = FormatDisplayValue(setting.DcValue),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = dcDiff ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = dcDiff ? highlight : normal
        };
        ToolTipService.SetToolTip(dc, dc.Text);
        Grid.SetColumn(dc, 3);
        row.Children.Add(dc);

        return new Border { Child = row };
    }

    private static string FormatDisplayValue(string value)
    {
        // Los valores ya llegan traducidos por el servicio (nombre localizado,
        // porcentaje, tiempo legible o decimal); solo se rellena el vacío.
        return string.IsNullOrEmpty(value) ? "--" : value;
    }

}
