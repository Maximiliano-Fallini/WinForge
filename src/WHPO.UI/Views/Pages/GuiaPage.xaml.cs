using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Components;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Components;

namespace WHPO_UI.Views.Pages;

public sealed partial class GuiaPage : Page
{
    private sealed record GuideLink(string Label, string PageId);

    private sealed record GuideEntry(
        string Title,
        string Summary,
        string[] Steps,
        GuideLink[] Links,
        string SearchTerms);

    /// <summary>
    /// Un componente que la Guía explica. Se arma a partir del registro de componentes —lo que el
    /// usuario tiene instalado de verdad— más la explicación de <see cref="ComponentGuides"/>: la
    /// Guía no inventa funciones, describe las que existen en la app.
    /// </summary>
    private sealed record GuideTopic(
        string Title,
        string Summary,
        string PageId,
        string SearchTerms,
        string IconGlyph,
        string Category,
        string? How);

    // ===== Medidas de las tarjetas de la portada =====
    // Ancho tope del contenido de cada desplegable. Sin este tope las tarjetas se estiran a todo
    // el ancho de la ventana y la grilla cambia de columnas según el tamaño: con el tope, la
    // portada se lee igual en cualquier ancho y el ojo encuentra dónde empieza cada fila.
    private const double ContentMaxWidth = 720;

    // Ancho de una tarjeta de pregunta. Las tarjetas van en UNA columna (son títulos largos que
    // se parten en dos líneas si se las aprieta), así que el ancho es el del contenido tope.
    private const double QuestionCardWidth = ContentMaxWidth;

    private static readonly GuideEntry[] CommonQuestions =
    {
        new(
            "¿Por dónde empiezo?",
            "Una orientación rápida para ubicarte y encontrar las funciones principales.",
            new[]
            {
                "Sistema muestra un resumen del equipo y sus componentes principales.",
                "Usá el menú lateral para recorrer las herramientas; cada sección se abre en su propia página.",
                "Si no encontrás una función, buscala en Workshop o escribí su nombre arriba."
            },
            new[] { new GuideLink("Abrir Sistema", "sistema"), new GuideLink("Explorar componentes", "workshop") },
            "inicio orientacion menu navegar"),
        new(
            "¿Cómo encuentro o activo un componente?",
            "Workshop reúne los componentes disponibles y permite instalarlos, actualizarlos o quitarlos.",
            new[]
            {
                "Abrí Workshop y buscá el nombre o la función que necesitás.",
                "Instalá el componente desde su tarjeta. Los componentes integrados pueden requerir reiniciar WinForge para aparecer.",
                "Si solo querés mostrar u ocultar una pestaña ya instalada, revisá Configuración → Navegación."
            },
            new[] { new GuideLink("Abrir Workshop", "workshop"), new GuideLink("Abrir Configuración", "configuracion") },
            "instalar activar componente pestaña mostrar ocultar workshop navegación"),
        new(
            "¿Dónde veo el uso y la temperatura del equipo?",
            "Sistema ofrece un resumen; Monitor de sensores muestra lecturas disponibles en vivo.",
            new[]
            {
                "Abrí Sistema para ver CPU, memoria y GPU en una vista general.",
                "Para temperaturas, frecuencias y voltajes disponibles, buscá Monitor de sensores en Workshop.",
                "La disponibilidad de cada lectura depende del hardware y de los sensores que exponga el equipo."
            },
            new[] { new GuideLink("Abrir Sistema", "sistema"), new GuideLink("Buscar componentes", "workshop") },
            "temperatura sensores uso cpu ram memoria gpu voltaje frecuencia"),
        new(
            "¿Cómo cambio el tema y la transparencia?",
            "La apariencia se configura desde Configuración. Los temas de entorno muestran una imagen de fondo.",
            new[]
            {
                "Abrí Configuración y entrá en Apariencia.",
                "Elegí un tema de entorno para aplicar sus valores iniciales de 50 % de transparencia y 5 % de blur.",
                "Después podés ajustar los controles manualmente. Al seleccionar otro tema de entorno, los valores se restablecen."
            },
            new[] { new GuideLink("Abrir Configuración", "configuracion") },
            "tema apariencia transparencia blur desenfoque fondo aurora marea brasa"),
        new(
            "¿Dónde configuro los planes de energía?",
            "La sección Núcleos y Plan de energía concentra los perfiles y controles relacionados con energía.",
            new[]
            {
                "Abrí Núcleos y Plan de energía desde el menú lateral.",
                "Revisá el plan activo y los controles disponibles para tu versión de Windows.",
                "Aplicá un cambio por vez y verificá el resultado antes de combinar varios ajustes."
            },
            new[] { new GuideLink("Abrir planes de energía", "nucleos") },
            "energía planes rendimiento procesador cpu núcleos"),
        new(
            "¿Cómo configuro WinForge para un juego?",
            "La Biblioteca de juegos permite elegir juegos y configurar el comportamiento del Modo juego.",
            new[]
            {
                "Abrí Biblioteca de juegos y seleccioná un juego de la lista.",
                "Revisá las opciones del Modo juego y los procesos asociados antes de activarlas.",
                "Podés volver a esta sección para cambiar o quitar la configuración."
            },
            new[] { new GuideLink("Abrir Biblioteca de juegos", "procesos") },
            "juegos biblioteca boost modo juego procesos"),
        new(
            "¿Qué hago si una función no aparece o no responde?",
            "Probá primero las comprobaciones básicas y usá las páginas de reparación si el problema continúa.",
            new[]
            {
                "Confirmá que el componente esté instalado en Workshop y habilitado en Configuración → Navegación.",
                "Si la función depende de hardware, comprobá que Windows o el fabricante expongan ese dispositivo.",
                "Para problemas de Windows, consultá las herramientas de Reparación. Algunas operaciones requieren permisos de administrador."
            },
            new[] { new GuideLink("Revisar componentes", "workshop"), new GuideLink("Configuración", "configuracion") },
            "problema error no aparece no funciona reparar diagnóstico"),
    };

    /// <summary>
    /// Qué hace cada componente y cómo se usa, en dos frases: la primera dice PARA QUÉ sirve y la
    /// segunda CÓMO se usa. La clave es el id del componente (el mismo de ComponentRegistry), así
    /// que la lista se arma sola con lo que el usuario tiene instalado y este texto es lo que la
    /// Guía le explica de cada uno.
    ///
    /// Va acá y no en components.json a propósito: esto es texto de la APP (Regla de oro 1), no la
    /// ficha del componente que publica el Workshop. Un componente descargado trae su propio nombre
    /// y descripción, pero la explicación de uso es cosa de la Guía, y así un componente nuevo en
    /// el repo no necesita republicar un pack para aparecer explicado.
    ///
    /// Lo que no esté en esta tabla se muestra igual, con su descripción del registro: la Guía
    /// nunca pierde un componente por no tener una entrada escrita.
    /// </summary>
    private static readonly Dictionary<string, (string What, string How)> ComponentGuides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sistema"] = ("Información general del equipo: procesador, memoria, placa de video y discos, con temperaturas por dispositivo.",
            "Abrí la página y mirá el resumen. Sirve para saber qué hay instalado y detectar de entrada si algo se está recalentando."),
        ["red"] = ("Estado de la conexión: adaptadores, DNS y pruebas de latencia y pérdida de paquetes.",
            "Elegí un proveedor de DNS y aplicalo para cambiarlo, o corré el test de paquetes para ver si la conexión es estable."),
        ["nucleos"] = ("Planes de energía de Windows, estado de los núcleos y el techo de turbo del procesador.",
            "Elegí un plan de energía y toca Aplicar. También podés comparar dos planes para ver la diferencia entre ellos."),
        ["procesos"] = ("Biblioteca de juegos y Modo juego: detecta los juegos instalados y optimiza el equipo mientras jugás.",
            "Seleccioná un juego y configurá su Modo juego (prioridad, afinidad de núcleos, plan de energía). Se aplica al abrirlo y se revierte al cerrarlo."),
        ["configuracion"] = ("Todo lo que se puede dejar a tu gusto: apariencia, idioma, navegación, inicio con Windows y el arranque.",
            "Entrá en Apariencia para el tema y la transparencia, en Navegación para mostrar u ocultar pestañas, y en Idioma para cambiar el idioma de la app."),
        ["workshop"] = ("Todos los componentes de WinForge en un solo lugar: se instalan, actualizan o desinstalan desde acá.",
            "Buscá el componente que te interesa y toca Instalar. Los que ya tengas se pueden ocultar o quitar desde su propia tarjeta."),
        ["memoria"] = ("Limpieza de la memoria RAM: vacía la lista de reserva de Windows para liberar memoria ocupada por programas cerrados.",
            "Elegí un preajuste o definí a qué cantidad de memoria usada se limpia sola, y tocá Limpiar para hacerlo a mano."),
        ["optimizaciones"] = ("Los ajustes de rendimiento de Windows en una lista, cada uno con su explicación y su estado.",
            "Revisá la lista y activá los que te sirvan. Se pueden revertir: cada ajuste vuelve a su valor original al desactivarlo."),
        ["sensores"] = ("Lecturas de hardware en vivo: temperaturas, frecuencias, voltajes y consumo de cada componente.",
            "Dejá la página abierta mientras usás el equipo para ver cómo se comporta. Qué sensores aparecen depende de tu hardware."),
        ["ventiladores"] = ("Curvas de ventilador: la velocidad de cada ventilador según la temperatura que informe el sensor.",
            "Elegí el sensor de referencia, armá la curva y aplicala. Andá de a poco: una curva agresiva puede recalentar el equipo."),
        ["procesosvivos"] = ("Gestor de procesos: qué se está ejecutando, cuántos recursos usa y con qué prioridad.",
            "Ordená por la columna que te interese y usá las acciones de cada proceso para cambiar su prioridad o afinidad, o cerrarlo."),
        ["estabilidad"] = ("Test de estabilidad: pruebas de esfuerzo para comprobar si el equipo aguanta.",
            "Elegí el tipo de prueba y el tiempo, y mirá temperaturas y consumo mientras corre. Sirve para validar un cambio antes de darlo por bueno."),
        ["overlay"] = ("Métricas sobre el juego: FPS, uso de CPU, placa, memoria y temperatura, dibujadas encima.",
            "Configurá la esquina y el formato, y usá el atajo de teclado para mostrar u ocultar el overlay mientras jugás."),
        ["macros"] = ("Macros de teclado y mouse: se graba una secuencia y se reproduce con un atajo.",
            "Grabá la secuencia, asignale un atajo y guardala. Después la reproducís con ese atajo desde cualquier parte."),
        ["autoclicker"] = ("Clics automáticos a la velocidad que definas, con un atajo para prenderlos y apagarlos.",
            "Elegí el botón, la velocidad de clic y el atajo. Se enciende y se apaga con el atajo, sin volver a la ventana."),
        ["debloat"] = ("Saca aplicaciones y servicios que Windows trae preinstalados y casi nunca se usan.",
            "Revisá la lista y marcá lo que quieras quitar. Conviene ir de a poco y saber qué se está quitando: hay cosas que pueden hacer falta."),
        ["herramientas"] = ("Funciones opcionales de Windows y utilidades de reparación que se usan una sola vez.",
            "Buscá la función por su nombre y activala o desactivala. Las utilidades de reparación explican qué hacen antes de ejecutarse."),
        ["panelwindows"] = ("Accesos directos a paneles y configuraciones de Windows que están medio escondidas.",
            "Buscá el panel que necesitás y tocalo para abrirlo, en vez de ir caminando hasta él por el menú de Windows."),
        ["reparacion"] = ("Herramientas de reparación del sistema: revisan y arreglan archivos de Windows dañados.",
            "Elegí la reparación según el problema. Algunas tardan bastante y piden reiniciar: conviene correrlas de a una."),
        ["actualizaciones"] = ("Windows Update y las actualizaciones de la propia app, en un solo lugar.",
            "Desde acá podés pausar o reanudar las actualizaciones de Windows, y ver si hay una versión nueva de WinForge."),
        ["limpieza"] = ("Libera espacio en el disco: caché y temporales, archivos duplicados y programas que arrancan solos.",
            "Hacé el chequeo del disco y limpiá lo que te marque. El buscador de duplicados te muestra qué va a borrar antes de borrarlo."),
        ["overclockusb"] = ("Ajusta la frecuencia de reporte (polling) de mouse, teclado y mandos, y mide la latencia real.",
            "Elegí el dispositivo y la frecuencia que quieras. El medidor te muestra el intervalo real para saber si el cambio surtió efecto."),
        ["teclado"] = ("Repetición del teclado: la velocidad de repetición y el retardo, más allá de lo que permite el panel de Windows.",
            "Ajustá el retardo y la velocidad, y probá el resultado en el cuadro de la misma página antes de aplicarlo."),
        ["temporizador"] = ("Resolución del temporizador de Windows: el intervalo con el que el sistema despierta al procesador.",
            "Activá la resolución más fina y verificá con el medidor. Es lo que suele mejorar la respuesta en juegos."),
        ["tcp"] = ("Plantillas de red: perfiles de parámetros TCP y proveedor de congestión ya probados.",
            "Elegí una plantilla y aplicala. Si algo anda mal, el botón de restaurar vuelve todo a los valores de Windows."),
        ["fancontrol"] = ("Control manual de los ventiladores cuando la placa o la placa de video lo permiten.",
            "Fijá la velocidad en porcentado y aplicala. Si no aparece tu ventilador, puede que el hardware no lo exponga."),
        ["benchmark"] = ("Prueba de rendimiento gráfico: escenas 3D que miden los FPS reales de tu equipo.",
            "Elegí la API gráfica y la escena, y dejá correr la medición. Al final te da el promedio y los mínimos."),
    };

    private static readonly GuideTopic[] FallbackTopics =
    {
        new("Sistema y sensores", "Información del equipo, lecturas y dispositivos.", "sistema", "hardware temperatura monitoreo sensores", "\uE71D", "Sistema", null),
        new("Rendimiento y energía", "Procesador, memoria, optimizaciones y planes.", "nucleos", "cpu memoria ram energía rendimiento", "\u9E71", "Rendimiento", null),
        new("Red y latencia", "Conexión, pruebas de red y ajustes relacionados.", "red", "internet tcp usb teclado latencia", "\uE968", "Red", null),
        new("Juegos", "Biblioteca, procesos y opciones del Modo juego.", "procesos", "gaming biblioteca boost", "\uE7FC", "Juego", null),
        new("Apariencia y ajustes", "Temas, navegación, idioma y preferencias.", "configuracion", "personalizar tema idioma transparencia", "\uE790", "Sistema", null),
    };

    private readonly INavigationService _navigationService;
    private readonly ILoggingService _loggingService;
    private readonly ComponentRegistry _registry;
    private readonly ISettingsService _settings;
    private GuideEntry? _selectedEntry;

    public GuiaPage()
    {
        InitializeComponent();
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _registry = App.Services.GetRequiredService<ComponentRegistry>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        SearchBox.PlaceholderText = I18n.T("¿Qué necesitás hacer o resolver?");
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        I18n.LanguageChanged += OnLanguageChanged;
        // El registro cambia cuando se instala o desinstala un componente desde el Workshop: la
        // Guía se rearma con lo que hay, igual que el navbar (ver ComponentRegistry.Changed).
        _registry.Changed += OnComponentsChanged;
        Render();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        I18n.LanguageChanged -= OnLanguageChanged;
        _registry.Changed -= OnComponentsChanged;
    }

    private void OnComponentsChanged() => Render();

    private void OnLanguageChanged()
    {
        SearchBox.PlaceholderText = I18n.T("¿Qué necesitás hacer o resolver?");
        Render();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _selectedEntry = null;
        Render();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedEntry = null;
        Render();
    }

    private void Render()
    {
        string query = (SearchBox.Text ?? "").Trim();
        LandingHost.Children.Clear();
        StepsHost.Children.Clear();
        LinksHost.Children.Clear();
        EmptyText.Visibility = Visibility.Collapsed;

        bool hasQuery = query.Length > 0;
        LandingHost.Visibility = _selectedEntry == null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = _selectedEntry == null ? Visibility.Collapsed : Visibility.Visible;

        if (_selectedEntry is { } selected)
        {
            DetailTitle.Text = I18n.T(selected.Title);
            DetailSummary.Text = I18n.T(selected.Summary);
            for (int i = 0; i < selected.Steps.Length; i++)
                StepsHost.Children.Add(BuildStep(i + 1, selected.Steps[i]));
            foreach (var link in selected.Links)
                LinksHost.Children.Add(BuildLinkButton(link));
            return;
        }

        var questions = CommonQuestions.Where(entry => Matches(entry.Title, entry.Summary, entry.SearchTerms, query)).ToArray();
        if (questions.Length > 0)
        {
            // Una columna de ancho fijo: cada pregunta ocupa su propia fila y todas arrancan en
            // el mismo borde.
            var questionCards = new StackPanel { Spacing = 6, Width = QuestionCardWidth };
            for (int i = 0; i < questions.Length; i++)
                questionCards.Children.Add(BuildQuestionCard(questions[i], i + 1));
            LandingHost.Children.Add(BuildSectionExpander(
                "Preguntas comunes",
                "Elegí una pregunta para ver una respuesta paso a paso.",
                questions.Length,
                questionCards));
        }

        var topics = BuildTopics().Where(topic => Matches(topic.Title, topic.Summary, topic.SearchTerms, query)).ToArray();

        if (topics.Length > 0)
        {
            // Un desplegable por categoría, con los componentes instalados de esa categoría
            // adentro. Así la portada no es una lista plana de veinte tarjetas: se agrupa por lo
            // que el usuario fue a buscar (Monitoreo, Sistema, Juego...) y cada grupo dice cuántos
            // componentes tiene. La categoría sale del propio componente (ComponentCategory), no de
            // una lista que mantener.
            foreach (var group in topics.GroupBy(t => t.Category))
                LandingHost.Children.Add(BuildSectionExpander(
                    group.Key,
                    "{0} componentes en esta categoría",
                    group.Count(),
                    BuildComponentGrid(group.ToArray())));
        }

        if (questions.Length == 0 && topics.Length == 0)
            EmptyText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Los temas de la Guía = los componentes que el usuario tiene instalados, cada uno explicado
    /// con lo que hace y cómo se usa.
    ///
    /// De dónde sale cada cosa: el nombre, la descripción, el icono y la categoría salen del
    /// REGISTRO de componentes (ComponentRegistry), que es lo mismo que dibuja el navbar —o sea que
    /// si una pestaña se ve en el menú, aparece explicada acá, y si se desinstaló, desaparece—. El
    /// "cómo se usa" sale de <see cref="ComponentGuides"/>.
    ///
    /// Qué se descarta, y con el MISMO criterio que el navbar (ver MainWindow.ApplyNavItemVisibility):
    /// los core (Sistema, Red, Núcleos, Biblioteca, Workshop, Guía, Configuración) no se listan
    /// —son la app misma, no algo que se descubra—, y un componente marcado como no instalado
    /// ("builtin.removed.&lt;id&gt;") tampoco. Así la Guía no ofrece páginas que no se pueden abrir.
    ///
    /// Si con ese filtro no queda nada (una instalación pelada), se cae a las secciones fijas de
    /// <see cref="FallbackTopics"/> para que la portada nunca quede vacía.
    /// </summary>
    private List<GuideTopic> BuildTopics()
    {
        var topics = new List<GuideTopic>();

        foreach (var component in _registry.All)
        {
            if (component.IsCore) continue;
            if (ComponentRegistry.CoreTags.Contains(component.Id)) continue;

            // Mismo criterio de "está instalado" que el navbar.
            if (_settings.Get("builtin.removed." + component.Id, _registry.RequiresInstall(component.Id)))
                continue;

            ComponentGuides.TryGetValue(component.Id, out var guide);

            topics.Add(new GuideTopic(
                component.Name,
                component.Description,
                component.Id,
                // Para buscar, el texto de la Guía también cuenta: si el usuario escribe "vaciar
                // RAM" tiene que encontrar Limpieza de memoria aunque la descripción no lo diga.
                component.Name + " " + component.Description + " " + (guide.What ?? "") + " " + (guide.How ?? ""),
                component.IconGlyph,
                CategoryName(component.Category),
                guide.How));
        }

        if (topics.Count == 0)
            topics.AddRange(FallbackTopics);

        // Orden estable: por categoría y después alfabético, para que la lista no cambie de lugar
        // entre un render y otro según el orden interno del registro.
        return topics
            .OrderBy(t => CategoryOrder(t.Category))
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Orden de las categorías en la portada: el mismo del navbar.</summary>
    private static int CategoryOrder(string category) => category switch
    {
        "Rendimiento" => 0,
        "Latencia" => 1,
        "Monitoreo" => 2,
        "Juego" => 3,
        "Red" => 4,
        _ => 5
    };

    private static string CategoryName(ComponentCategory category) => category switch
    {
        ComponentCategory.Juego => "Juego",
        ComponentCategory.Rendimiento => "Rendimiento",
        ComponentCategory.Latencia => "Latencia",
        ComponentCategory.Monitoreo => "Monitoreo",
        _ => "Sistema"
    };

    /// <summary>
    /// La grilla de componentes de una categoría. Cada tarjeta muestra el icono y el nombre del
    /// componente, qué hace, y cómo se usa —esa última línea es la que responde a "cómo utilizarlo",
    /// que es justo lo que la descripción de una línea del registro no alcanza a decir—. El clic
    /// abre la página del componente.
    /// </summary>
    private FrameworkElement BuildComponentGrid(GuideTopic[] topics)
    {
        var grid = new Grid { Width = ContentMaxWidth, ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < (topics.Length + 1) / 2; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < topics.Length; i++)
        {
            var card = BuildTopicCard(topics[i]);
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            grid.Children.Add(card);
        }

        return grid;
    }
    /// <summary>¿El texto casa con la búsqueda? Sin búsqueda, todo pasa.</summary>
    private static bool Matches(string title, string summary, string searchTerms, string query)
    {
        if (query.Length == 0) return true;
        return (I18n.T(title) + " " + I18n.T(summary) + " " + searchTerms)
            .Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// Un desplegable de sección de la Guía.
    ///
    /// El estilo va acá y no en un {ThemeResource} a propósito: es la MISMA composición que las
    /// cards del resto de la app —fondo de card con el alfa del ajuste de transparencia y esquinas
    /// de 12, como DetailPanel— resuelta con ThemeBrushes, que es lo que hace que el desplegable
    /// siga al tema elegido y a los deslizadores de apariencia. Un Expander suelto sale con el
    /// relleno de WinUI (LayerFillColorDefaultBrush), un gris translúcido ajeno a la paleta; y el
    /// DefaultExpanderStyle del proyecto usa pinceles del sistema, que tampoco responden al ajuste.
    ///
    /// SIN CONTORNO, igual que las cards (ver Cards.xaml: el filete de 1 px se lee como un reborde
    /// sobre todo con el vidrio encendido): el fondo de card ya separa el desplegable del fondo de
    /// la página, y las tarjetas de adentro hacen el resto.
    ///
    /// El contenido va con un ancho tope centrado: sin eso, la grilla de tarjetas se estira a todo
    /// el ancho de la ventana y la cantidad de columnas cambia según el tamaño.
    /// </summary>
    private static Expander BuildSectionExpander(string title, string subtitleTemplate, int count, UIElement content)
    {
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock
        {
            Text = I18n.T(title),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = I18n.T(subtitleTemplate, count),
            FontSize = 11,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        // El contenido se envuelve en una columna de ancho acotado: el Expander ocupa todo el
        // ancho (HorizontalAlignment.Stretch) y esto es lo que le pone el tope.
        if (content is FrameworkElement element)
            element.MaxWidth = ContentMaxWidth;

        return new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 14)
        };
    }

    /// <summary>
    /// Tarjeta de una pregunta. Lleva su número a la izquierda: la portada es una lista ordenada y
    /// sin el número las tarjetas se leen como un bloque indistinto de títulos. El número es
    /// decorativo (no hay que traducirlo) y se dibuja con el acento, como los pasos del detalle.
    /// </summary>
    private Button BuildQuestionCard(GuideEntry entry, int index)
    {
        var number = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = ThemeBrushes.Get("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = index.ToString(),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeBrushes.Get("AccentForegroundBrush")
            }
        };

        var title = new TextBlock
        {
            Text = I18n.T(entry.Title),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(number);
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        var button = MakeCardButton(row, borderless: true);
        button.MinHeight = 46;
        button.Padding = new Thickness(12, 8, 12, 8);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Click += (_, _) =>
        {
            _selectedEntry = entry;
            Render();
        };
        return button;
    }

    /// <summary>
    /// La tarjeta de un componente: icono + nombre arriba, qué hace en el medio, y cómo se usa
    /// abajo, con la marca del acento para que se lea como el consejo de la tarjeta y no como más
    /// texto suelto.
    ///
    /// Es una card SIN contorno, igual que el resto de la app (ver Cards.xaml: el filete de 1 px se
    /// lee como un reborde, sobre todo con el vidrio encendido): el fondo de card ya la separa del
    /// fondo del desplegable.
    /// </summary>
    private Button BuildTopicCard(GuideTopic topic)
    {
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // "Ventiladores" no tiene glifo propio: el del registro es un candado (\uE72E). Igual
        // que en el navbar y en la card del Workshop, se reemplaza por el FanIcon dibujado a
        // mano para que el ícono sea el mismo fan de línea en toda la app (si acá quedaba el
        // glifo, la tarjeta de la Guía mostraba un candado en lugar del ventilador).
        IconElement icon = string.Equals(topic.PageId, "ventiladores", StringComparison.OrdinalIgnoreCase)
            ? WHPO_UI.Controls.FanIcon.CreateIcon(17, ThemeBrushes.Get("AccentBrush"))
            : new FontIcon
            {
                Glyph = topic.IconGlyph,
                FontSize = 17,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
        if (icon is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(icon);

        var title = new TextBlock
        {
            Text = I18n.T(topic.Title),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(header);
        content.Children.Add(new TextBlock
        {
            Text = I18n.T(topic.Summary),
            FontSize = 12,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        // El "cómo se usa": la pieza que la Guía agrega y que no está en ningún otro lado. Solo se
        // dibuja si hay texto —un tema de respaldo no lo tiene—.
        if (!string.IsNullOrWhiteSpace(topic.How))
            content.Children.Add(new TextBlock
            {
                Text = I18n.T(topic.How),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });

        var button = MakeCardButton(content, borderless: true);
        button.Padding = new Thickness(14);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Click += (_, _) => NavigateTo(topic.PageId);
        return button;
    }

    /// <summary>
    /// El botón que hace de tarjeta: mismo fondo de card (con el alfa del ajuste de transparencia)
    /// que el resto de la app. El contenido es un FrameworkElement y no un StackPanel para que una
    /// tarjeta pueda armar su interior con la grilla que le convenga —la de pregunta lleva su
    /// número en una columna fija— en vez de obligarla a apilar todo en vertical.
    /// </summary>
    private static Button MakeCardButton(FrameworkElement content, bool borderless = false)
        => new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            BorderBrush = borderless
                ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                : ThemeBrushes.Get("SecondaryTextBrush"),
            BorderThickness = borderless ? new Thickness(0) : new Thickness(1),
            Content = content
        };

    private static Border BuildStep(int number, string text)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrushes.Get("AccentBrush"),
            Child = new TextBlock
            {
                Text = number.ToString(),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeBrushes.Get("AccentForegroundBrush")
            }
        });
        var stepText = new TextBlock
        {
            Text = I18n.T(text),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(stepText, 1);
        row.Children.Add(stepText);
        return new Border { Child = row, Padding = new Thickness(2, 4, 2, 4) };
    }

    private Button BuildLinkButton(GuideLink link)
    {
        var button = new Button
        {
            Content = I18n.T(link.Label),
            Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrushes.Get("AccentBrush"),
            Foreground = ThemeBrushes.Get("AccentForegroundBrush")
        };
        button.Click += (_, _) => NavigateTo(link.PageId);
        return button;
    }

    private void NavigateTo(string pageId)
    {
        try
        {
            ComponentHostPage.PendingComponentId = pageId;
            _navigationService.NavigateTo(pageId);
            if (!string.Equals(_navigationService.CurrentPage, pageId, StringComparison.OrdinalIgnoreCase))
            {
                EmptyText.Text = I18n.T("No se pudo abrir esa sección. Revisá que el componente esté disponible en Workshop.");
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            App.MainWindowInstance?.HighlightNavigationPage(pageId);
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Guía: no se pudo abrir la sección '{pageId}': {ex}", ex);
            EmptyText.Text = I18n.T("No se pudo abrir esa sección. Revisá que el componente esté disponible en Workshop.");
            EmptyText.Visibility = Visibility.Visible;
        }
    }
}
