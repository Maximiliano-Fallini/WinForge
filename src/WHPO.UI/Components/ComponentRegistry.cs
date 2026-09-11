using System;
using System.Collections.Generic;
using System.Linq;
using WHPO.Core.Components;
using WHPO.Core.Services;
using WHPO_UI.Views.Pages;

namespace WHPO_UI.Components;

/// <summary>
/// Implementación de IWinForgeComponent para las páginas de fábrica (built-in):
/// el navbar y el Workshop las tratan igual que a un componente descargado, pero
/// viven dentro de WinForge.exe (IsCore marca las "si o si" que nunca se ocultan).
/// </summary>
public sealed class BuiltinComponent : IWinForgeComponent
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string IconGlyph { get; init; }
    public required ComponentCategory Category { get; init; }
    public required Type PageType { get; init; }
    public bool IsCore { get; init; }

    private string? _versionOverride;

    /// <summary>
    /// Versión INDIVIDUAL del componente (base 0.1.0), independiente de la
    /// versión de la app: cada pestaña se actualiza por su cuenta desde el
    /// Workshop, no con cada release de la app. Las core (parte del exe)
    /// siguen la versión de la app.
    /// </summary>
    public string Version
    {
        get => _versionOverride ?? (IsCore ? AppUpdateService.CurrentVersion() : "0.1.0");
        init => _versionOverride = value;
    }

    public string MinAppVersion => "";

    public object CreatePage(IServiceProvider services)
        => Activator.CreateInstance(PageType) ?? throw new InvalidOperationException($"No se pudo crear la página de {Id}.");
}

/// <summary>
/// Registro único de componentes: fuente de verdad del navbar dinámico y del
/// Workshop. Los built-in se registran acá (orden = orden del navbar); los
/// componentes descargados se agregan al arrancar (LoadInstalledComponents) y al
/// instalarlos en caliente desde el Workshop. El navbar se reconcilia con el
/// evento Changed.
/// </summary>
public sealed class ComponentRegistry
{
    /// <summary>Tags del set "si o si": nunca ocultables ni desinstalables.</summary>
    public static readonly HashSet<string> CoreTags = new(StringComparer.OrdinalIgnoreCase)
    { "sistema", "red", "nucleos", "procesos", "workshop", "configuracion" };

    private readonly List<IWinForgeComponent> _items = new();

    // Integrados de fábrica por id: permite recuperar la copia del exe como
    // FALLBACK cuando la copia descargada del repo de un componente no carga.
    private readonly Dictionary<string, BuiltinComponent> _builtinsById = new(StringComparer.OrdinalIgnoreCase);

    // Ids de los integrados NO core: desde la 0.3.0 (introducción del Workshop)
    // nacen "no instalados" y hay que instalarlos desde el Workshop.
    private readonly HashSet<string> _nonCoreBuiltinIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True si el id corresponde a un integrado de fábrica no core: desde la 0.3.0
    /// nace "no instalado" (define el default de "builtin.removed.<id>" y de
    /// "nav.<id>"). Los core y los componentes descargados no requieren instalación.
    /// </summary>
    public bool RequiresInstall(string id)
        => _nonCoreBuiltinIds.Contains(id);

    /// <summary>Se dispara al agregar o quitar componentes (el navbar se reconcilia).</summary>
    public event Action? Changed;

    public ComponentRegistry()
    {
        foreach (var c in CreateBuiltins())
        {
            if (!c.IsCore) _nonCoreBuiltinIds.Add(c.Id);
            _builtinsById[c.Id] = (BuiltinComponent)c;
            _items.Add(c);
        }
    }

    public IReadOnlyList<IWinForgeComponent> All => _items;

    public IWinForgeComponent? Find(string id)
        => _items.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True si el id corresponde a un integrado de fábrica (copia en el exe).</summary>
    public bool IsBuiltin(string id)
        => _builtinsById.ContainsKey(id);

    public void Register(IWinForgeComponent component)
    {
        _items.RemoveAll(c => string.Equals(c.Id, component.Id, StringComparison.OrdinalIgnoreCase));
        _items.Add(component);
        Changed?.Invoke();
    }

    public void Unregister(string id)
    {
        int removed = _items.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Changed?.Invoke();
    }

    /// <summary>
    /// Vuelve a registrar el integrado de fábrica del id indicado: FALLBACK de
    /// arranque cuando su copia descargada del repo no se pudo cargar. No hace
    /// nada si el id no es un integrado o si ya hay algo registrado con ese id.
    /// Devuelve true si restauró el integrado.
    /// </summary>
    public bool RestoreBuiltin(string id)
    {
        if (!_builtinsById.TryGetValue(id, out var builtin)) return false;
        if (Find(id) != null) return false;
        _items.Add(builtin);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Componentes que van al navbar como ítems generados (no core).</summary>
    public IEnumerable<IWinForgeComponent> NavbarComponents => _items.Where(c => !c.IsCore);

    // =====================================================================
    // Catálogo de fábrica (el orden acá es el orden del navbar)
    // =====================================================================

    private static IEnumerable<IWinForgeComponent> CreateBuiltins()
    {
        // ===== Core: "si o si" con la app =====
        yield return new BuiltinComponent
        {
            Id = "sistema", Name = "Sistema", IconGlyph = "\uE71D", Category = ComponentCategory.Sistema,
            Description = "Monitoreo del equipo: CPU, RAM y GPU con temperaturas por dispositivo.",
            PageType = typeof(SistemaPage), IsCore = true
        };
        yield return new BuiltinComponent
        {
            Id = "red", Name = "Red", IconGlyph = "\uE968", Category = ComponentCategory.Monitoreo,
            Description = "Estadísticas de red, latencia y optimizaciones de la conexión.",
            PageType = typeof(RedPage), IsCore = true
        };
        yield return new BuiltinComponent
        {
            Id = "nucleos", Name = "Núcleos y Plan de energía", IconGlyph = "\uE950", Category = ComponentCategory.Rendimiento,
            Description = "Gestión de núcleos del procesador y planes de energía del sistema.",
            PageType = typeof(NucleosPage), IsCore = true
        };
        yield return new BuiltinComponent
        {
            Id = "procesos", Name = "Biblioteca de juegos", IconGlyph = "\uE7FC", Category = ComponentCategory.Juego,
            Description = "Tu biblioteca de juegos con el boost del modo juego y su configuración.",
            PageType = typeof(GestionarProcesosPage), IsCore = true
        };
        yield return new BuiltinComponent
        {
            Id = "workshop", Name = "Workshop", IconGlyph = "\uE719", Category = ComponentCategory.Sistema,
            Description = "Catálogo de componentes: instalá, actualizá o desinstalá funciones de WinForge.",
            PageType = typeof(WorkshopPage), IsCore = true
        };

        // ===== No core: componentes del navbar (ocultables / desinstalables) =====
        // Orden agrupado por categoría: Latencia → Rendimiento → Monitoreo →
        // Juego → Sistema (el orden acá es el orden del navbar).
        yield return new BuiltinComponent
        {
            Id = "temporizador", Name = "Resolución del Temporizador", IconGlyph = "\uE823", Category = ComponentCategory.Latencia,
            Description = "Ajuste de latencia: habilita la regla de Windows GlobalTimerResolutionRequest para reducir la latencia de entrada (input lag).",
            PageType = typeof(TemporizadorPage)
        };
        yield return new BuiltinComponent
        {
            Id = "overclockusb", Name = "Overclock USB", IconGlyph = "\uE88E", Category = ComponentCategory.Latencia,
            Description = "Aumenta la frecuencia de sondeo (polling rate) de los dispositivos USB para reducir la latencia de entrada (input lag).",
            PageType = typeof(OverclockUsbPage)
        };
        yield return new BuiltinComponent
        {
            Id = "teclado", Name = "Filtro de teclas", IconGlyph = "\uE765", Category = ComponentCategory.Latencia,
            Description = "Repetición del teclado en milisegundos y filtro de pulsaciones (FilterKeys).",
            PageType = typeof(TecladoPage)
        };
        yield return new BuiltinComponent
        {
            Id = "memoria", Name = "Memoria", IconGlyph = "\uEEA0", Category = ComponentCategory.Rendimiento,
            Description = "Limpieza inteligente y automática de la caché en la memoria RAM.",
            PageType = typeof(MemoriaPage)
        };
        yield return new BuiltinComponent
        {
            Id = "optimizaciones", Name = "Optimizaciones", IconGlyph = "\uE90F", Category = ComponentCategory.Rendimiento,
            Description = "Optimizaciones para ganar rendimiento.",
            PageType = typeof(OptimizacionesPage)
        };
        yield return new BuiltinComponent
        {
            Id = "sensores", Name = "Monitor de sensores", IconGlyph = "\uE957", Category = ComponentCategory.Monitoreo,
            Description = "Sensores del equipo en vivo: temperaturas, voltajes y frecuencias.",
            PageType = typeof(SensoresPage)
        };
        yield return new BuiltinComponent
        {
            Id = "procesosvivos", Name = "Gestión de procesos", IconGlyph = "\uE21D", Category = ComponentCategory.Monitoreo,
            Description = "Gestor de procesos del sistema: también muestra métricas como recursos, prioridades y estados.",
            PageType = typeof(ProcesosPage)
        };
        yield return new BuiltinComponent
        {
            Id = "estabilidad", Name = "Test de estabilidad", IconGlyph = "\uE9D2", Category = ComponentCategory.Monitoreo,
            Description = "Pruebas de estrés para validar la estabilidad del equipo.",
            PageType = typeof(EstabilidadPage)
        };
        yield return new BuiltinComponent
        {
            Id = "overlay", Name = "Overlay de métricas", IconGlyph = "\uE95A", Category = ComponentCategory.Monitoreo,
            Description = "Métricas en superposición: FPS, CPU, GPU, RAM, 1% low, etc.",
            PageType = typeof(OverlayPage)
        };
        yield return new BuiltinComponent
        {
            Id = "macros", Name = "Macros", IconGlyph = "\uE70F", Category = ComponentCategory.Juego,
            Description = "Grabá secuencias de teclas y clics y reproducilas con un atajo global.",
            PageType = typeof(MacrosPage)
        };
        yield return new BuiltinComponent
        {
            Id = "autoclicker", Name = "Autoclicker", IconGlyph = "\uE962", Category = ComponentCategory.Juego,
            Description = "Clics automáticos configurables con hotkey global.",
            PageType = typeof(AutoclickerPage)
        };
        yield return new BuiltinComponent
        {
            Id = "debloat", Name = "Debloat", IconGlyph = "\uE74D", Category = ComponentCategory.Sistema,
            Description = "Quitá apps y servicios preinstalados de Windows que no usás.",
            PageType = typeof(DebloatPage)
        };
        yield return new BuiltinComponent
        {
            Id = "herramientas", Name = "Herramientas y funciones", IconGlyph = "\uE713", Category = ComponentCategory.Sistema,
            Description = "Funciones opcionales de Windows y utilidades de reparación de un solo uso.",
            PageType = typeof(HerramientasPage)
        };
        yield return new BuiltinComponent
        {
            Id = "panelwindows", Name = "Panel de Windows", IconGlyph = "\uE8FB", Category = ComponentCategory.Sistema,
            Description = "Accesos directos a paneles y configuraciones ocultas de Windows.",
            PageType = typeof(PanelWindowsPage)
        };
        yield return new BuiltinComponent
        {
            Id = "reparacion", Name = "Reparación", IconGlyph = "\uE72C", Category = ComponentCategory.Sistema,
            Description = "SFC, DISM y reparaciones del sistema en un solo lugar.",
            PageType = typeof(ReparacionPage)
        };
        yield return new BuiltinComponent
        {
            Id = "actualizaciones", Name = "Windows Update", IconGlyph = "\uE896", Category = ComponentCategory.Sistema,
            Description = "Gestión de las actualizaciones de Windows y del sistema de la app.",
            PageType = typeof(ActualizacionesPage)
        };
        yield return new BuiltinComponent
        {
            Id = "limpieza", Name = "Limpieza del dispositivo", IconGlyph = "\uE74D", Category = ComponentCategory.Sistema,
            Description = "Liberá espacio en el disco (caché, archivos temporales, etc.): chequeo del sistema, buscador de archivos duplicados y administración de aplicaciones al iniciar el sistema.",
            PageType = typeof(LimpiezaPage)
        };
    }
}
