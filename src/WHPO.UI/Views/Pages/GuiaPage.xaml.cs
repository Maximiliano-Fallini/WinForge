using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Components;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Components;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Página "Guía": una portada con preguntas frecuentes y una ficha por componente.
///
/// Todo se arma con el MISMO modelo (<see cref="GuideArticle"/>): una pregunta frecuente
/// y un componente son la misma cosa —un título, una bajada y pasos para resolverlo—, así
/// que los dos abren el mismo panel de detalle. Esa es la refactorización de fondo: antes
/// las preguntas tenían su detalle y los componentes solo navegaban a su página, o sea que
/// la Guía nunca explicaba cómo se usaba un componente más allá de una línea.
///
/// El contenido sale de dos fuentes: los componentes que el usuario tiene instalados (del
/// registro, lo mismo que dibuja el navbar) y <see cref="ComponentGuides"/>, que es donde
/// vive la explicación de uso de cada uno. Nada se inventa: si un componente no tiene
/// entrada en esa tabla, igual aparece con su descripción del registro.
/// </summary>
public sealed partial class GuiaPage : Page
{
    /// <summary>Botón que abre otra página de la app (lo que hace accionable una guía).</summary>
    private sealed record GuideLink(string Label, string PageId);

    /// <summary>Pregunta y respuesta corta de una ficha (la duda puntual de ese componente).</summary>
    private sealed record GuideQa(string Question, string Answer);

    /// <summary>
    /// Una entrada de la Guía. <paramref name="PageId"/> null = es una pregunta frecuente
    /// (no navega a ningún lado); <paramref name="Category"/> null = no lleva la etiqueta
    /// de categoría en el detalle.
    /// </summary>
    private sealed record GuideArticle(
        string Title,
        string Summary,
        string? How,
        string[] Steps,
        GuideLink[] Links,
        string SearchTerms,
        string IconGlyph,
        string? Category,
        string? PageId,
        // Dudas puntuales del componente (null = sin preguntas propias). Va con valor por
        // defecto para que las preguntas generales no tengan que pasarlo.
        GuideQa[]? Faqs = null,
        // False = el componente está en el registro pero no instalado: la Guía lo explica
        // igual y su ficha ofrece instalarlo desde Workshop.
        bool Installed = true);

    /// <summary>Alto fijo de las tarjetas de componente: todas iguales, sin importar el texto.</summary>
    private const double ComponentCardHeight = 132;

    private const string HelpGlyph = "\uE897";
    private const string ChevronGlyph = "\uE76C";

    // ===================== Contenido: preguntas frecuentes =====================

    private static readonly GuideArticle[] CommonQuestions =
    {
        new(
            "¿Por dónde empiezo?",
            "Una orientación rápida para ubicarte y encontrar las funciones principales.",
            null,
            new[]
            {
                "Sistema muestra un resumen del equipo y sus componentes principales.",
                "Usá el menú lateral para recorrer las herramientas; cada sección se abre en su propia página.",
                "Si no encontrás una función, buscala en Workshop o escribí su nombre arriba."
            },
            new[] { new GuideLink("Abrir Sistema", "sistema"), new GuideLink("Explorar componentes", "workshop") },
            "inicio orientacion menu navegar",
            HelpGlyph, null, null),
        new(
            "¿Cómo encuentro o activo un componente?",
            "Workshop reúne los componentes disponibles y permite instalarlos, actualizarlos o quitarlos.",
            null,
            new[]
            {
                "Abrí Workshop y buscá el nombre o la función que necesitás.",
                "Instalá el componente desde su tarjeta. Los componentes integrados pueden requerir reiniciar WinForge para aparecer."
            },
            new[] { new GuideLink("Abrir Workshop", "workshop"), new GuideLink("Abrir Configuración", "configuracion") },
            "instalar activar componente pestaña workshop",
            HelpGlyph, null, null),
        new(
            "¿Dónde veo el uso y la temperatura del equipo?",
            "Sistema ofrece un resumen; Monitor de sensores muestra lecturas disponibles en vivo.",
            null,
            new[]
            {
                "Abrí Sistema para ver CPU, memoria y GPU en una vista general.",
                "Para temperaturas, frecuencias y voltajes disponibles, buscá Monitor de sensores en Workshop.",
                "La disponibilidad de cada lectura depende del hardware y de los sensores que exponga el equipo."
            },
            new[] { new GuideLink("Abrir Sistema", "sistema"), new GuideLink("Buscar componentes", "workshop") },
            "temperatura sensores uso cpu ram memoria gpu voltaje frecuencia",
            HelpGlyph, null, null),
        new(
            "¿Cómo mido y reduzco la latencia de red?",
            "La latencia de red es el tiempo que tarda un paquete en ir al servidor y volver (el ping). Se mide y se ajusta desde Red.",
            null,
            new[]
            {
                "Abrí Red y corré la prueba de latencia y pérdida de paquetes: te da el tiempo de ida y vuelta y cuántos paquetes se pierden.",
                "Si hay picos o cortes, probá una plantilla de red en TCP, que ajusta parámetros de conexión.",
                "Si una página tarda en empezar a cargar pero el ping es bajo, el problema es la resolución de nombres: cambiá el proveedor de DNS."
            },
            new[]
            {
                new GuideLink("Medir en Red", "red"),
                new GuideLink("Abrir TCP", "tcp")
            },
            "latencia de red ping lag ms milisegundos dns tcp conexion servidor paquetes",
            HelpGlyph, null, null),
        new(
            "¿Cómo mido y reduzco la latencia de sondeo (USB)?",
            "La latencia de sondeo es local: cada cuánto el mouse, el teclado o el mando informan un evento al equipo. No tiene nada que ver con el ping.",
            null,
            new[]
            {
                "Abrí Overclock USB, elegí el dispositivo y mirá el medidor: muestra el intervalo real de reporte.",
                "Subí la frecuencia de reporte y volvé a medir para confirmar que el intervalo bajó.",
                "Para la respuesta general del sistema, revisá también la resolución del temporizador de Windows en Temporizador."
            },
            new[]
            {
                new GuideLink("Medir sondeo", "overclockusb"),
                new GuideLink("Abrir Temporizador", "temporizador")
            },
            "latencia de sondeo polling usb mouse teclado mando intervalo reporte input lag",
            HelpGlyph, null, null),
        new(
            "¿Cómo cambio el tema y la transparencia?",
            "La apariencia se configura desde Configuración. Los temas de entorno muestran una imagen de fondo.",
            null,
            new[]
            {
                "Abrí Configuración y entrá en Apariencia.",
                "Elegí un tema de entorno para aplicar sus valores iniciales de 50 % de transparencia y 5 % de blur.",
                "Después podés ajustar los controles manualmente. Al seleccionar otro tema de entorno, los valores se restablecen."
            },
            new[] { new GuideLink("Abrir Configuración", "configuracion") },
            "tema apariencia transparencia blur desenfoque fondo aurora marea brasa",
            HelpGlyph, null, null),
        new(
            "¿Dónde configuro los planes de energía?",
            "El plan de energía vive en Optimizaciones, en la categoría Energía, junto al resto de los ajustes de rendimiento.",
            null,
            new[]
            {
                "Abrí Optimizaciones desde el menú lateral.",
                "Entrá en la tarjeta Energía.",
                "Revisá el plan activo y los controles disponibles para tu versión de Windows.",
                "Aplicá un cambio por vez y verificá el resultado antes de combinar varios ajustes."
            },
            new[] { new GuideLink("Abrir Energía", "optimizaciones") },
            "energía planes rendimiento procesador cpu núcleos optimizaciones",
            HelpGlyph, null, null),
        new(
            "¿Cómo configuro WinForge para un juego?",
            "La Biblioteca de juegos permite elegir juegos y configurar el comportamiento del Modo juego.",
            null,
            new[]
            {
                "Abrí Biblioteca de juegos y seleccioná un juego de la lista.",
                "Revisá las opciones del Modo juego y los procesos asociados antes de activarlas.",
                "Podés volver a esta sección para cambiar o quitar la configuración."
            },
            new[] { new GuideLink("Abrir Biblioteca de juegos", "procesos") },
            "juegos biblioteca boost modo juego procesos",
            HelpGlyph, null, null),
        new(
            "¿Qué diferencia hay entre «Actual» y «Siempre»?",
            "Son los dos alcances de las reglas de un juego: cuándo se aplican. Las dos se recuerdan entre reinicios.",
            null,
            new[]
            {
                "«Siempre» se guarda y se aplica cada vez que el juego se abre, lo inicies desde WinForge o desde su launcher.",
                "«Actual» se aplica solo cuando abrís el juego desde WinForge, con el botón Iniciar de la Biblioteca.",
                "Cualquiera de las dos se borra desde Eliminar reglas, en la configuración del juego."
            },
            new[] { new GuideLink("Abrir Biblioteca de juegos", "procesos") },
            "actual siempre regla sesión alcance recordar juego",
            HelpGlyph, null, null),
        new(
            "¿Cómo hago que Windows arranque más rápido?",
            "Dos ajustes: sacar del arranque lo que no usás y revisar los ajustes de rendimiento.",
            null,
            new[]
            {
                "Abrí Arranque y apagá las aplicaciones que no necesitás que se abran solas (no se desinstalan).",
                "En Optimizaciones, activá los ajustes de rendimiento que correspondan a tu uso, de a uno.",
                "Reiniciá y compará el tiempo de arranque: si algo empeora, desactivá ese ajuste."
            },
            new[]
            {
                new GuideLink("Abrir Arranque", "arranque"),
                new GuideLink("Abrir Optimizaciones", "optimizaciones")
            },
            "velocidad rápido arranque inicio boot lentitud rendimiento",
            HelpGlyph, null, null),
        new(
            "¿Necesito permisos de administrador?",
            "Sí: WinForge se abre con permisos de administrador para poder aplicar los cambios.",
            null,
            new[]
            {
                "Windows pide confirmar la elevación al abrir la app; sin eso, WinForge avisa y se cierra.",
                "Algunas funciones además dependen de que tu hardware o Windows expongan el dato o el control.",
                "Si una acción no funciona, mirá el aviso de la propia pantalla: suele indicar el motivo."
            },
            Array.Empty<GuideLink>(),
            "administrador permisos uac elevar acceso",
            HelpGlyph, null, null),
        new(
            "¿Los cambios se pueden deshacer?",
            "Depende de la función: los ajustes de la app se pueden revertir.",
            null,
            new[]
            {
                "En Optimizaciones, cada ajuste vuelve a su valor original al desactivarlo.",
                "Las reglas de un juego se revierten cuando el juego se cierra (el plan de energía, por ejemplo).",
                "Las herramientas de un solo uso —reparación, limpieza— no cambian configuración, así que no hay nada que deshacer."
            },
            new[]
            {
                new GuideLink("Abrir Optimizaciones", "optimizaciones"),
                new GuideLink("Abrir Reparación", "reparacion")
            },
            "deshacer revertir volver atrás restaurar original",
            HelpGlyph, null, null),
        new(
            "¿Qué hago si una función no aparece o no responde?",
            "Probá primero las comprobaciones básicas y usá las páginas de reparación si el problema continúa.",
            null,
            new[]
            {
                "Si la función depende de hardware, comprobá que Windows o el fabricante expongan ese dispositivo.",
                "Para problemas de Windows, consultá las herramientas de Reparación. Algunas operaciones requieren permisos de administrador."
            },
            new[] { new GuideLink("Revisar componentes", "workshop"), new GuideLink("Configuración", "configuracion") },
            "problema error no aparece no funciona reparar diagnóstico",
            HelpGlyph, null, null),
    };

    // ===================== Contenido: cómo se usa cada componente =====================

    /// <summary>
    /// Qué hace cada componente, cómo se usa y los pasos concretos. La clave es el id del
    /// componente (el mismo de ComponentRegistry): la lista se arma sola con lo que el
    /// usuario tiene instalado y este texto es lo que la Guía le explica de cada uno.
    ///
    /// Va acá y no en components.json a propósito: esto es texto de la APP (Regla de oro 1),
    /// no la ficha del componente que publica el Workshop. Un componente descargado trae su
    /// propio nombre y descripción, pero la explicación de uso es cosa de la Guía, y así un
    /// componente nuevo en el repo no necesita republicar un pack para aparecer explicado.
    ///
    /// Lo que no esté en esta tabla se muestra igual, con su descripción del registro: la
    /// Guía nunca pierde un componente por no tener una entrada escrita.
    /// </summary>
    private static readonly Dictionary<string, (string What, string How, string[] Steps)> ComponentGuides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sistema"] = (
                "Información general del equipo: procesador, memoria, placa de video y discos, con temperaturas por dispositivo.",
                "Abrí la página y mirá el resumen. Sirve para saber qué hay instalado y detectar de entrada si algo se está recalentando.",
                new[]
                {
                    "Abrí Sistema desde el menú lateral.",
                    "Recorré el resumen: procesador, memoria, placa de video y discos.",
                    "Mirá las temperaturas de cada dispositivo para detectar algo fuera de rango."
                }),
            ["red"] = (
                "Estado de la conexión: adaptadores, DNS y pruebas de latencia y pérdida de paquetes.",
                "Elegí un proveedor de DNS y aplicalo para cambiarlo, o corré el test de paquetes para ver si la conexión es estable.",
                new[]
                {
                    "Abrí Red y revisá los adaptadores y el DNS actual.",
                    "Elegí un proveedor de DNS y aplicalo para cambiarlo.",
                    "Corré la prueba de latencia y pérdida de paquetes para ver si la conexión es estable."
                }),
            ["nucleos"] = (
                "Planes de energía de Windows, estado de los núcleos y el techo de turbo del procesador.",
                "Elegí un plan de energía y tocá Aplicar. También podés comparar dos planes para ver la diferencia entre ellos.",
                new[]
                {
                    "Abrí Núcleos para ver el plan de energía activo.",
                    "Elegí un plan de la lista y tocá Aplicar.",
                    "Opcional: compará dos planes para ver la diferencia entre ellos."
                }),
            ["procesos"] = (
                "Biblioteca de juegos y Modo juego: detecta los juegos instalados y optimiza el equipo mientras jugás.",
                "Seleccioná un juego y configurá su Modo juego (prioridad, afinidad de núcleos, plan de energía). Se aplica al abrirlo y se revierte al cerrarlo.",
                new[]
                {
                    "Abrí Biblioteca de juegos y ubicá el juego en la lista.",
                    "Abrí su configuración (engranaje) y elegí prioridad, afinidad y plan de energía.",
                    "Elegí el alcance: Siempre aplica en cada apertura; Actual solo al lanzarlo desde WinForge.",
                    "Iniciá el juego desde la biblioteca para que la regla se aplique."
                }),
            ["configuracion"] = (
                "Todo lo que se puede dejar a tu gusto: apariencia, idioma, inicio con Windows y el arranque.",
                "Entrá en Apariencia para el tema y la transparencia, y en Idioma para cambiar el idioma de la app.",
                new[]
                {
                    "Abrí Configuración desde el menú lateral.",
                    "En Apariencia elegí el tema y ajustá transparencia y desenfoque.",
                    "En Idioma cambiá el idioma de la app."
                }),
            ["workshop"] = (
                "Todos los componentes de WinForge en un solo lugar: se instalan, actualizan o desinstalan desde acá.",
                "Buscá el componente que te interesa y tocá Instalar. Los que ya tengas se pueden quitar desde su propia tarjeta.",
                new[]
                {
                    "Abrí Workshop y buscá el componente por nombre o función.",
                    "Tocá Instalar y esperá a que termine la descarga.",
                    "Actualizá o quitá cada componente desde su propia tarjeta."
                }),
            ["memoria"] = (
                "Limpieza de la memoria RAM: vacía la lista de reserva de Windows para liberar memoria ocupada por programas cerrados.",
                "Elegí un preajuste o definí a qué cantidad de memoria usada se limpia sola, y tocá Limpiar para hacerlo a mano.",
                new[]
                {
                    "Abrí Memoria y elegí un preajuste de limpieza automática.",
                    "Tocá Limpiar para vaciar la lista de reserva ahora mismo.",
                    "Repetilo cuando notes el equipo lento después de cerrar programas pesados."
                }),
            ["optimizaciones"] = (
                "Los ajustes de rendimiento de Windows en una lista, cada uno con su explicación y su estado.",
                "Revisá la lista y activá los que te sirvan. Se pueden revertir: cada ajuste vuelve a su valor original al desactivarlo.",
                new[]
                {
                    "Abrí Optimizaciones y recorré las categorías.",
                    "Leé qué hace cada ajuste antes de activarlo.",
                    "Activá los que te sirvan: al desactivarlos vuelven a su valor original."
                }),
            ["sensores"] = (
                "Lecturas de hardware en vivo: temperaturas, frecuencias, voltajes y consumo de cada componente.",
                "Dejá la página abierta mientras usás el equipo para ver cómo se comporta. Qué sensores aparecen depende de tu hardware.",
                new[]
                {
                    "Abrí Sensores y dejá la página abierta mientras usás el equipo.",
                    "Compará temperaturas, frecuencias y consumo bajo carga.",
                    "Qué lecturas aparecen depende del hardware y de los controladores instalados."
                }),
            ["ventiladores"] = (
                "Curvas de ventilador: la velocidad de cada ventilador según la temperatura que informe el sensor.",
                "Elegí el sensor de referencia, armá la curva y aplicala. Andá de a poco: una curva agresiva puede recalentar el equipo.",
                new[]
                {
                    "Abrí Ventiladores y elegí el sensor de referencia.",
                    "Armá la curva de temperatura a velocidad con puntos suaves.",
                    "Aplicala y verificá que los ventiladores respondan; andá de a poco."
                }),
            ["procesosvivos"] = (
                "Gestor de procesos: qué se está ejecutando, cuántos recursos usa y con qué prioridad.",
                "Ordená por la columna que te interese y usá las acciones de cada proceso para cambiar su prioridad o afinidad, o cerrarlo.",
                new[]
                {
                    "Abrí Procesos vivos para ver qué se está ejecutando.",
                    "Ordená por CPU, memoria o disco para encontrar qué consume.",
                    "Usá las acciones de cada fila para cambiar prioridad o afinidad, o cerrar el proceso."
                }),
            ["estabilidad"] = (
                "Test de estabilidad: pruebas de esfuerzo para comprobar si el equipo aguanta.",
                "Elegí el tipo de prueba y el tiempo, y mirá temperaturas y consumo mientras corre. Sirve para validar un cambio antes de darlo por bueno.",
                new[]
                {
                    "Abrí Estabilidad y elegí el tipo de prueba y su duración.",
                    "Iniciá el test y mirá temperaturas y consumo mientras corre.",
                    "Usalo para validar un cambio antes de darlo por bueno."
                }),
            ["overlay"] = (
                "Métricas sobre el juego: FPS, uso de CPU, placa, memoria y temperatura, dibujadas encima.",
                "Configurá la esquina y el formato, y usá el atajo de teclado para mostrar u ocultar el overlay mientras jugás.",
                new[]
                {
                    "Abrí Overlay y elegí qué métricas mostrar y en qué esquina.",
                    "Asigná el atajo de teclado para mostrarlo u ocultarlo.",
                    "Con un juego abierto, usá el atajo para verlo encima."
                }),
            ["macros"] = (
                "Macros de teclado y mouse: se graba una secuencia y se reproduce con un atajo.",
                "Grabá la secuencia, asignale un atajo y guardala. Después la reproducís con ese atajo desde cualquier parte.",
                new[]
                {
                    "Abrí Macros y grabá la secuencia de teclas o clics.",
                    "Asignale un atajo y guardala.",
                    "Reproducila con ese atajo desde cualquier ventana."
                }),
            ["autoclicker"] = (
                "Clics automáticos a la velocidad que definas, con un atajo para prenderlos y apagarlos.",
                "Elegí el botón, la velocidad de clic y el atajo. Se enciende y se apaga con el atajo, sin volver a la ventana.",
                new[]
                {
                    "Abrí Autoclicker y elegí el botón y la velocidad de clic.",
                    "Asigná el atajo de encendido y apagado.",
                    "Activá el atajo y usá el mouse con normalidad: el clic se repite solo."
                }),
            ["debloat"] = (
                "Saca aplicaciones preinstaladas que Windows trae y casi nunca se usan.",
                "Revisá la lista y marcá lo que quieras quitar. Conviene ir de a poco y saber qué se está quitando: hay cosas que pueden hacer falta.",
                new[]
                {
                    "Abrí Debloat y revisá la lista de aplicaciones.",
                    "Marcá solo lo que sepas que no usás.",
                    "Aplicá los cambios y revisá el resultado antes de seguir con más."
                }),
            ["sistematweaks"] = (
                "Ajustes de Windows que no son aplicaciones: telemetría, servicios, hibernación, IPv6 y más.",
                "Marcá los ajustes, revisá la lista y aplicá. Son cambios del sistema: conviene ir de a uno y saber qué toca cada uno antes de aplicarlo.",
                new[]
                {
                    "Abrí Optimizaciones y elegí «Ajustes del sistema».",
                    "Marcá solo los ajustes que entiendas y aplicalos.",
                    "Revisá el resultado antes de seguir con más."
                }),
            ["herramientas"] = (
                "Funciones opcionales de Windows y utilidades de reparación que se usan una sola vez.",
                "Buscá la función por su nombre y activala o desactivala. Las utilidades de reparación explican qué hacen antes de ejecutarse.",
                new[]
                {
                    "Abrí Herramientas y buscá la función por su nombre.",
                    "Leé qué hace antes de ejecutarla.",
                    "Activala o desactivala y, si es una reparación, esperá a que termine."
                }),
            ["panelwindows"] = (
                "Accesos directos a paneles y configuraciones de Windows que están medio escondidas.",
                "Buscá el panel que necesitás y tocalo para abrirlo, en vez de ir caminando hasta él por el menú de Windows.",
                new[]
                {
                    "Abrí Panel de Windows y buscá el panel que necesitás.",
                    "Tocá el acceso para abrirlo directamente.",
                    "Usalo para llegar rápido a configuraciones escondidas de Windows."
                }),
            ["reparacion"] = (
                "Herramientas de reparación del sistema: revisan y arreglan archivos de Windows dañados.",
                "Elegí la reparación según el problema. Algunas tardan bastante y piden reiniciar: conviene correrlas de a una.",
                new[]
                {
                    "Abrí Reparación y elegí la herramienta según el problema.",
                    "Leé qué hace: algunas tardan bastante y piden reiniciar.",
                    "Corré una a la vez y revisá el resultado antes de la siguiente."
                }),
            ["actualizaciones"] = (
                "Windows Update y las actualizaciones de la propia app, en un solo lugar.",
                "Desde acá podés pausar o reanudar las actualizaciones de Windows, y ver si hay una versión nueva de WinForge.",
                new[]
                {
                    "Abrí Actualizaciones.",
                    "Pausá o reanudá Windows Update según lo que necesites.",
                    "En la misma página se ve si hay una versión nueva de WinForge."
                }),
            ["limpieza"] = (
                "Libera espacio en el disco: caché, archivos temporales y archivos duplicados.",
                "Hacé el chequeo del disco y limpiá lo que te marque. El buscador de duplicados te muestra qué va a borrar antes de borrarlo.",
                new[]
                {
                    "Abrí Limpieza y analizá el disco.",
                    "Revisá qué ocupa espacio y marcá lo que quieras borrar.",
                    "Confirmá la limpieza; en duplicados, la vista previa muestra qué se va a borrar."
                }),
            ["arranque"] = (
                "Las aplicaciones que se abren solas al iniciar Windows, con su interruptor de encendido.",
                "Apagá las que no necesites para que el arranque sea más rápido. No se desinstalan: las podés volver a prender cuando quieras.",
                new[]
                {
                    "Abrí Arranque para ver qué se abre solo con Windows.",
                    "Apagá las que no necesites para que el inicio sea más rápido.",
                    "Volvé a prenderlas cuando quieras: no se desinstala nada."
                }),
            ["overclockusb"] = (
                "Ajusta la frecuencia de reporte (polling) de mouse, teclado y mandos, y mide la latencia real.",
                "Elegí el dispositivo y la frecuencia que quieras. El medidor te muestra el intervalo real para saber si el cambio surtió efecto.",
                new[]
                {
                    "Abrí Overclock USB y elegí el dispositivo.",
                    "Seleccioná la frecuencia de reporte y aplicala.",
                    "Mirá el medidor para confirmar el intervalo real."
                }),
            ["teclado"] = (
                "Repetición del teclado: la velocidad de repetición y el retardo, más allá de lo que permite el panel de Windows.",
                "Ajustá el retardo y la velocidad, y probá el resultado en el cuadro de la misma página antes de aplicarlo.",
                new[]
                {
                    "Abrí Teclado y ajustá el retardo y la velocidad de repetición.",
                    "Probá el resultado en el cuadro de la misma página.",
                    "Aplicá cuando el ritmo te resulte cómodo."
                }),
            ["temporizador"] = (
                "Resolución del temporizador de Windows: el intervalo con el que el sistema despierta al procesador.",
                "Activá la resolución más fina y verificá con el medidor. Es lo que suele mejorar la respuesta en juegos.",
                new[]
                {
                    "Abrí Temporizador y activá la resolución más fina.",
                    "Verificá con el medidor que el intervalo cambió.",
                    "Se nota sobre todo en la respuesta dentro de juegos."
                }),
            ["tcp"] = (
                "Plantillas de red: perfiles de parámetros TCP y proveedor de congestión ya probados.",
                "Elegí una plantilla y aplicala. Si algo anda mal, el botón de restaurar vuelve todo a los valores de Windows.",
                new[]
                {
                    "Abrí TCP y elegí una plantilla de red.",
                    "Aplicala y probá la conexión.",
                    "Si algo empeora, usá Restaurar para volver a los valores de Windows."
                }),
            ["fancontrol"] = (
                "Control manual de los ventiladores cuando la placa o la placa de video lo permiten.",
                "Fijá la velocidad en porcentaje y aplicala. Si no aparece tu ventilador, puede que el hardware no lo exponga.",
                new[]
                {
                    "Abrí Control de ventiladores.",
                    "Fijá la velocidad en porcentaje y aplicala.",
                    "Si tu ventilador no aparece, el hardware no expone el control."
                }),
            ["benchmark"] = (
                "Prueba de rendimiento gráfico: escenas 3D que miden los FPS reales de tu equipo.",
                "Elegí la API gráfica y la escena, y dejá correr la medición. Al final te da el promedio y los mínimos.",
                new[]
                {
                    "Abrí Benchmark y elegí la API gráfica y la escena.",
                    "Iniciá la medición y dejá correr sin tocar el equipo.",
                    "Al final compará el promedio y los mínimos."
                }),
        };

    /// <summary>
    /// Dudas puntuales de cada componente ("¿cómo mido la latencia?", "¿por qué no aparece mi
    /// ventilador?"…). Van separadas de <see cref="ComponentGuides"/> para no tocar la tabla de
    /// uso: acá responde lo que el usuario pregunta DESPUÉS de saber que la función existe.
    /// Se muestran en la ficha del componente y también entran a la búsqueda.
    /// </summary>
    private static readonly Dictionary<string, GuideQa[]> ComponentFaqs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sistema"] = new[]
            {
                new GuideQa("¿De dónde salen las temperaturas?",
                    "De los sensores que exponen el procesador, la placa y la placa de video. Si un dispositivo no los informa, aparece sin temperatura."),
                new GuideQa("¿Por qué no veo mi placa de video?",
                    "Puede faltar el controlador o que el sensor no esté expuesto. Revisá que Windows la reconozca en el Administrador de dispositivos."),
            },
            ["red"] = new[]
            {
                new GuideQa("¿Cómo mido la latencia de red?",
                    "Con la prueba de latencia y pérdida de paquetes de la propia página: da el tiempo de ida y vuelta (ping) y cuántos paquetes se pierden."),
                new GuideQa("¿Esto cambia la latencia de sondeo del mouse?",
                    "No. Red mide el viaje al servidor; el sondeo del mouse o teclado es local y se ajusta en Overclock USB."),
                new GuideQa("¿Cambiar el DNS reduce el ping?",
                    "No cambia el ping del juego: mejora cuánto tarda en resolverse un nombre de dominio. Para el ping importan la ruta y la conexión."),
                new GuideQa("¿Puedo volver al DNS anterior?",
                    "Sí. Volvé a elegir el proveedor que tenías y aplicalo de nuevo sobre el adaptador."),
            },
            ["nucleos"] = new[]
            {
                new GuideQa("¿Cómo sé qué plan de energía está activo?",
                    "La página muestra el plan activo al abrirse y se actualiza cuando aplicás otro."),
                new GuideQa("¿Qué plan conviene para jugar?",
                    "Un plan de alto rendimiento evita que el procesador baje de frecuencia. Compará dos planes para ver la diferencia antes de decidir."),
            },
            ["procesos"] = new[]
            {
                new GuideQa("¿Por qué no se aplicó la regla de mi juego?",
                    "Necesita una regla configurada y que el juego se abra. Con el alcance «Actual» solo se aplica si lo lanzás desde WinForge."),
                new GuideQa("¿Cuándo uso «Actual» y cuándo «Siempre»?",
                    "«Actual» para probar sin compromiso: se recuerda y se aplica al lanzar desde WinForge. «Siempre» para que valga en cada apertura, venga de donde venga."),
                new GuideQa("¿Se revierte al cerrar el juego?",
                    "Sí: el plan de energía vuelve al anterior cuando el juego se cierra."),
            },
            ["configuracion"] = new[]
            {
                new GuideQa("¿Cómo quito una pestaña que no uso?",
                    "Desinstalá su componente desde Workshop: la pestaña desaparece del menú y podés reinstalarlo cuando quieras. No hay forma de esconderla sin desinstalarla."),
                new GuideQa("¿Cómo cambio el idioma?",
                    "En Configuración → Idioma. Si el pack no está descargado, la app lo baja sola."),
            },
            ["workshop"] = new[]
            {
                new GuideQa("¿Por qué no puedo instalar un componente?",
                    "Si está marcado como en desarrollo, solo se instala desde builds de desarrollo de la app; en la versión estable aparece sin botón."),
                new GuideQa("¿Cómo desinstalo un componente?",
                    "Desde su propia tarjeta en Workshop. Algunos cambios de la interfaz se ven recién al reiniciar WinForge."),
            },
            ["memoria"] = new[]
            {
                new GuideQa("¿La limpieza hace más rápido el equipo?",
                    "Libera memoria que quedó retenida por programas cerrados. Ayuda cuando queda poca disponible; no acelera un equipo que ya tiene memoria de sobra."),
                new GuideQa("¿Cuándo conviene limpiar?",
                    "Cuando notes el equipo lento después de cerrar programas pesados, o dejá la limpieza automática por umbral."),
            },
            ["optimizaciones"] = new[]
            {
                new GuideQa("¿Puedo revertir un ajuste?",
                    "Sí: al desactivarlo vuelve a su valor original."),
                new GuideQa("¿Conviene activar todo junto?",
                    "No. Aplicá de a uno y verificá el resultado: algunos ajustes cambian el comportamiento de Windows."),
            },
            ["sensores"] = new[]
            {
                new GuideQa("¿Por qué faltan lecturas?",
                    "Cada placa y cada controlador expone sensores distintos. Lo que el hardware no informa no se puede mostrar."),
                new GuideQa("¿En qué se diferencia de Sistema?",
                    "Sistema es el resumen del equipo; Sensores muestra las lecturas en vivo, con más detalle."),
            },
            ["ventiladores"] = new[]
            {
                new GuideQa("¿Cómo aumento la velocidad de los ventiladores?",
                    "Con la curva: subí el porcentaje de los tramos de temperatura alta (o bajá el umbral al que empieza a subir) y aplicá. La página ajusta cada ventilador según esa curva."),
                new GuideQa("¿Por qué no aparece mi ventilador?",
                    "Solo se muestran los canales que la placa o la placa de video permiten controlar; los que no lo permiten quedan afuera."),
                new GuideQa("¿Una curva más agresiva enfría más?",
                    "Sí, a costa de más ruido. Subí la velocidad de a poco y mirá la temperatura real antes de exagerar."),
            },
            ["procesosvivos"] = new[]
            {
                new GuideQa("¿Cómo cambio las reglas de un proceso del sistema?",
                    "Buscá el proceso en la lista y usá sus acciones para cambiar prioridad o afinidad, o para cerrarlo."),
                new GuideQa("¿Es seguro cerrar un proceso?",
                    "Cerrá solo lo que reconozcas. Terminar un proceso de Windows puede forzar el cierre de la sesión."),
            },
            ["estabilidad"] = new[]
            {
                new GuideQa("¿Cuánto tiene que durar la prueba?",
                    "Lo suficiente para que las temperaturas se estabilicen. Para validar un overclock, corré más tiempo."),
                new GuideQa("¿Qué hago si falla?",
                    "Mirá temperaturas y voltajes, volvé al valor anterior y probá de nuevo con un cambio más chico."),
            },
            ["overlay"] = new[]
            {
                new GuideQa("¿Cómo lo muestro en el juego?",
                    "Con el atajo configurado, mientras el juego está corriendo."),
                new GuideQa("¿Por qué no aparece?",
                    "Revisá que el atajo esté asignado y que el juego esté en primer plano; algunos juegos no dejan dibujar encima."),
            },
            ["macros"] = new[]
            {
                new GuideQa("¿Se reproducen fuera de WinForge?",
                    "Sí, con el atajo global mientras la app está abierta."),
                new GuideQa("¿Qué pasa si el atajo ya está en uso?",
                    "Windows entrega la combinación a una sola aplicación: elegí una que no use otro programa."),
            },
            ["autoclicker"] = new[]
            {
                new GuideQa("¿Cómo lo detengo?",
                    "Con el mismo atajo que lo enciende, sin volver a la ventana."),
                new GuideQa("¿Cómo aumento la velocidad de clic?",
                    "Subí los clics por segundo en el control de la página. Cuanto más alto, más exigente es para el programa que recibe los clics."),
                new GuideQa("¿Funciona en cualquier juego?",
                    "Depende del juego: algunos no aceptan clics sintéticos y los ignoran o los bloquean."),
            },
            ["debloat"] = new[]
            {
                new GuideQa("¿Se puede deshacer?",
                    "No todo. Antes de aplicar, revisá qué vas a quitar y de qué depende."),
                new GuideQa("¿Qué conviene quitar?",
                    "Aplicaciones que no usás. Las apps de Microsoft se pueden reinstalar desde Store; las de terceros, volviéndolas a bajar."),
            },
            ["sistematweaks"] = new[]
            {
                new GuideQa("¿Es lo mismo que Debloat?",
                    "No: Debloat saca aplicaciones; acá se cambian ajustes del sistema que no son apps."),
                new GuideQa("¿Se puede deshacer?",
                    "Sí: al desmarcar el tweak y volver a aplicarlo, el ajuste vuelve a su estado anterior."),
            },
            ["herramientas"] = new[]
            {
                new GuideQa("¿Cuándo uso las reparaciones?",
                    "Cuando una función de Windows falla y querés descartar archivos dañados."),
                new GuideQa("¿Requieren reiniciar?",
                    "Algunas sí; la tarjeta de la herramienta lo indica antes de ejecutarla."),
            },
            ["panelwindows"] = new[]
            {
                new GuideQa("¿Esto cambia algo en Windows?",
                    "No: solo abre el panel o la configuración. Los cambios los hacés vos adentro."),
                new GuideQa("¿Para qué sirve entonces?",
                    "Para llegar en un clic a configuraciones que están escondidas en el menú de Windows."),
            },
            ["reparacion"] = new[]
            {
                new GuideQa("¿Cuál elijo?",
                    "La que coincida con el síntoma. Si no estás seguro, empezá por la más liviana y mirá el resultado."),
                new GuideQa("¿Cuánto tarda?",
                    "Depende del disco y del daño. Algunas piden reiniciar, así que conviene correrlas de a una."),
            },
            ["actualizaciones"] = new[]
            {
                new GuideQa("¿Pausar Windows Update es seguro?",
                    "Es una pausa temporal. Al reanudar se instalan las actualizaciones pendientes."),
                new GuideQa("¿Cómo actualizo WinForge?",
                    "La misma página avisa si hay una versión nueva de la app y permite instalarla."),
            },
            ["limpieza"] = new[]
            {
                new GuideQa("¿Qué se borra exactamente?",
                    "Caché, archivos temporales y duplicados. En cada caso la vista previa muestra qué se va a borrar."),
                new GuideQa("¿Se puede recuperar lo borrado?",
                    "No: lo que se elimina no pasa por la papelera."),
            },
            ["arranque"] = new[]
            {
                new GuideQa("¿Desinstala las aplicaciones?",
                    "No. Solo evita que se abran solas con Windows."),
                new GuideQa("¿Cómo vuelvo atrás?",
                    "Prendé el interruptor de nuevo: la aplicación vuelve a abrirse con Windows."),
            },
            ["overclockusb"] = new[]
            {
                new GuideQa("¿Cómo mido la latencia de sondeo?",
                    "Con el medidor de la página: muestra el intervalo real de reporte del dispositivo, en milisegundos."),
                new GuideQa("¿Cómo aumento la velocidad de reporte (Hz)?",
                    "Elegí el dispositivo, subí la frecuencia y aplicá. El medidor confirma el intervalo real: si no baja, el cambio no surtió efecto."),
                new GuideQa("¿Esto reduce la latencia de red o el ping?",
                    "No. El sondeo es local: cambia cada cuánto el dispositivo avisa un evento. El ping es el viaje al servidor y depende de la conexión y la ruta."),
                new GuideQa("¿Más Hz siempre es mejor?",
                    "Reduce el intervalo de reporte, pero algunos dispositivos pierden estabilidad si se los exige de más."),
            },
            ["teclado"] = new[]
            {
                new GuideQa("¿Cómo aumento la velocidad de repetición del teclado?",
                    "Bajá el retardo (cuánto tarda en empezar a repetir) y subí la velocidad (cuántas repeticiones por segundo). Probá en el cuadro de la página antes de aplicar."),
                new GuideQa("¿Se nota en los juegos?",
                    "Afecta la repetición de teclas, no el tiempo de respuesta del juego."),
            },
            ["temporizador"] = new[]
            {
                new GuideQa("¿Qué es la resolución del temporizador?",
                    "El intervalo con el que Windows despierta al procesador. Más fina, más precisión y más despertares."),
                new GuideQa("¿Consume más batería?",
                    "En un portátil puede notarse: una resolución más fina despierta la CPU más seguido."),
            },
            ["tcp"] = new[]
            {
                new GuideQa("¿Aumenta mi velocidad de internet?",
                    "No. Ajusta parámetros de TCP que pueden mejorar la estabilidad y reducir cortes, no el ancho de banda."),
                new GuideQa("¿Cómo vuelvo a los valores de Windows?",
                    "Con Restaurar, que deja la configuración de red como la tenía Windows."),
            },
            ["fancontrol"] = new[]
            {
                new GuideQa("¿Cómo subo la velocidad de un ventilador?",
                    "Elegí el ventilador, fijá el porcentaje y aplicá. El cambio se mantiene mientras la página controla ese canal."),
                new GuideQa("¿Por qué no aparece ningún ventilador?",
                    "El hardware no expone el control. La página solo muestra canales controlables."),
                new GuideQa("¿Puedo fijarlo en 0 %?",
                    "No conviene: con el ventilador detenido el equipo puede recalentarse."),
            },
            ["benchmark"] = new[]
            {
                new GuideQa("¿Puedo comparar resultados con otro equipo?",
                    "Solo sirven para comparar entre corridas del mismo equipo, con la misma API y la misma escena."),
                new GuideQa("¿Por qué baja el rendimiento con el tiempo?",
                    "Suele ser temperatura o procesos en segundo plano. Corré la prueba sin nada más abierto."),
            },
        };

    /// <summary>Temas fijos cuando el usuario no tiene componentes extra instalados.</summary>
    private static readonly GuideArticle[] FallbackTopics =
    {
        new("Sistema y sensores", "Información del equipo, lecturas y dispositivos.", null,
            Array.Empty<string>(), Array.Empty<GuideLink>(), "hardware temperatura monitoreo sensores",
            "\uE71D", "Sistema", "sistema"),
        new("Rendimiento y energía", "Procesador, memoria, optimizaciones y planes.", null,
            Array.Empty<string>(), Array.Empty<GuideLink>(), "cpu memoria ram energía rendimiento",
            "\u9E71", "Rendimiento", "nucleos"),
        new("Red y latencia", "Conexión, pruebas de red y ajustes relacionados.", null,
            Array.Empty<string>(), Array.Empty<GuideLink>(), "internet tcp usb teclado latencia",
            "\uE968", "Red", "red"),
        new("Juegos", "Biblioteca, procesos y opciones del Modo juego.", null,
            Array.Empty<string>(), Array.Empty<GuideLink>(), "gaming biblioteca boost",
            "\uE7FC", "Juego", "procesos"),
        new("Apariencia y ajustes", "Temas, navegación, idioma y preferencias.", null,
            Array.Empty<string>(), Array.Empty<GuideLink>(), "personalizar tema idioma transparencia",
            "\uE790", "Sistema", "configuracion"),
    };

    // ===================== Estado =====================

    private readonly INavigationService _navigationService;
    private readonly ILoggingService _loggingService;
    private readonly ComponentRegistry _registry;
    private readonly ISettingsService _settings;

    /// <summary>Entrada abierta en el detalle (null = portada).</summary>
    private GuideArticle? _selected;

    public GuiaPage()
    {
        InitializeComponent();
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _registry = App.Services.GetRequiredService<ComponentRegistry>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        ApplyLanguage();
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
        ApplyLanguage();
        Render();
    }

    /// <summary>Textos fijos de la página (encabezado, buscador, volver, vacío).</summary>
    private void ApplyLanguage()
    {
        PageTitle.Text = I18n.T("Guía");
        PageSubtitle.Text = I18n.T("Aprendé a usar cada componente y resolvé las dudas más comunes.");
        SearchBox.PlaceholderText = I18n.T("¿Qué necesitás hacer o resolver?");
        BackText.Text = I18n.T("Volver a la guía");
        EmptyText.Text = I18n.T("No encontramos guías para esa búsqueda. Probá con otras palabras.");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _selected = null;
        Render();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        Render();
    }

    // ===================== Render =====================

    private void Render()
    {
        string query = (SearchBox.Text ?? "").Trim();

        LandingHost.Children.Clear();
        DetailHost.Children.Clear();
        EmptyText.Visibility = Visibility.Collapsed;

        bool hasSelection = _selected != null;
        LandingHost.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        if (_selected is { } selected)
        {
            BuildDetail(selected);
            return;
        }

        BuildLanding(query);
    }

    /// <summary>
    /// La portada: primero las preguntas frecuentes (lo que la gente viene a resolver) y
    /// después los componentes agrupados por categoría, con una tarjeta por componente.
    /// </summary>
    private void BuildLanding(string query)
    {
        // Se arma UNA vez: la lista completa también alimenta la sección de preguntas por
        // componente, que busca dentro del texto de cada pregunta.
        var allTopics = BuildTopics();
        var questions = CommonQuestions.Where(a => Matches(a, query)).ToArray();
        var topics = allTopics.Where(a => Matches(a, query)).ToArray();

        if (questions.Length > 0)
            LandingHost.Children.Add(BuildFaqSection(questions));

        if (BuildComponentQuestionsSection(allTopics, query) is { } componentQuestions)
            LandingHost.Children.Add(componentQuestions);

        // Un bloque por categoría (Monitoreo, Rendimiento…): el orden es el del navbar, así
        // la Guía se recorre igual que el menú.
        foreach (var group in topics.GroupBy(t => t.Category ?? "Sistema"))
            LandingHost.Children.Add(BuildCategorySection(group.Key, group.ToArray()));

        if (questions.Length == 0 && topics.Length == 0)
            EmptyText.Visibility = Visibility.Visible;
    }

    // ===================== Portada: preguntas =====================

    private UIElement BuildFaqSection(GuideArticle[] questions)
    {
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(BuildSectionHeader(
            I18n.T("Preguntas frecuentes"),
            I18n.T("Las dudas que más se repiten, con la respuesta paso a paso.")));

        var rows = new StackPanel();
        for (int i = 0; i < questions.Length; i++)
        {
            if (i > 0) rows.Children.Add(BuildDivider());
            rows.Children.Add(BuildFaqRow(questions[i]));
        }

        section.Children.Add(new Border
        {
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Child = rows
        });

        return section;
    }

    /// <summary>Una fila de pregunta: texto a la izquierda y chevron a la derecha.</summary>
    private Button BuildFaqRow(GuideArticle article)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = I18n.T(article.Title),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrushes.Get("PrimaryTextBrush")
        };
        row.Children.Add(title);

        var chevron = new FontIcon
        {
            Glyph = ChevronGlyph,
            FontSize = 11,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 1);
        row.Children.Add(chevron);

        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10)
        };
        button.Click += (_, _) =>
        {
            _selected = article;
            Render();
        };
        return button;
    }

    /// <summary>
    /// Todas las preguntas de todos los componentes en un solo lugar.
    ///
    /// Sin búsqueda van dentro de un desplegable (son decenas y no deben tapar la portada);
    /// con búsqueda se muestran expandidas, porque escribir "velocidad", "latencia" o
    /// "ventilador" tiene que devolver la pregunta exacta —no solo la tarjeta del componente—.
    /// </summary>
    private UIElement? BuildComponentQuestionsSection(IEnumerable<GuideArticle> topics, string query)
    {
        var pairs = topics
            .SelectMany(t => (t.Faqs ?? Array.Empty<GuideQa>()).Select(qa => (Article: t, Qa: qa)))
            .Where(p => query.Length == 0
                || I18n.T(p.Qa.Question).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        if (pairs.Length == 0) return null;

        var rows = new StackPanel();
        for (int i = 0; i < pairs.Length; i++)
        {
            if (i > 0) rows.Children.Add(BuildDivider());
            rows.Children.Add(BuildComponentQuestionRow(pairs[i].Article, pairs[i].Qa));
        }

        var header = BuildSectionHeader(
            I18n.T("Preguntas por componente"),
            query.Length == 0
                ? I18n.T("{0} preguntas de todos los componentes: velocidad, latencia, limpieza y más.", pairs.Length)
                : I18n.T("{0} preguntas encontradas", pairs.Length));

        if (query.Length == 0)
        {
            // Desplegable: la portada no se convierte en una lista de sesenta filas.
            return new Expander
            {
                Header = header,
                Content = rows,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 14)
            };
        }

        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(header);
        section.Children.Add(new Border
        {
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Child = rows
        });
        return section;
    }

    /// <summary>Fila de una pregunta de componente: pregunta, componente al que pertenece y chevron.</summary>
    private Button BuildComponentQuestionRow(GuideArticle article, GuideQa qa)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = I18n.T(qa.Question),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrushes.Get("PrimaryTextBrush")
        });

        var topic = new TextBlock
        {
            Text = I18n.T(article.Title).ToUpperInvariant(),
            FontSize = 9.5,
            CharacterSpacing = 60,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(topic, 1);
        row.Children.Add(topic);

        var chevron = new FontIcon
        {
            Glyph = ChevronGlyph,
            FontSize = 11,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 2);
        row.Children.Add(chevron);

        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10)
        };
        button.Click += (_, _) =>
        {
            _selected = article;
            Render();
        };
        return button;
    }

    // ===================== Portada: componentes =====================

    private UIElement BuildCategorySection(string category, GuideArticle[] topics)
    {
        var section = new StackPanel { Spacing = 10 };
        // Cuenta los INSTALADOS (la categoría ahora también lista los que no lo están, con
        // su aviso). Singular y plural explícitos: "1 instalado".
        int installedCount = topics.Count(t => t.Installed);
        string countText = installedCount == topics.Length
            ? (installedCount == 1 ? I18n.T("1 instalado") : I18n.T("{0} instalados", installedCount))
            : I18n.T("{0} de {1} instalados", installedCount, topics.Length);
        section.Children.Add(BuildSectionHeader(
            I18n.T(category).ToUpperInvariant(),
            countText));

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < (topics.Length + 1) / 2; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < topics.Length; i++)
        {
            var card = BuildComponentCard(topics[i]);
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            grid.Children.Add(card);
        }

        section.Children.Add(grid);
        return section;
    }

    /// <summary>
    /// Tarjeta de un componente: ícono, nombre y qué hace. El pie ("Cómo usarlo") es lo que
    /// avisa que la tarjeta abre una ficha con los pasos.
    /// </summary>
    private Button BuildComponentCard(GuideArticle article)
    {
        int faqCount = (article.Faqs ?? Array.Empty<GuideQa>()).Length;

        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = BuildIconTile(article, 30, 15);
        // El componente que no está instalado se ve, pero apagado: se distingue de un vistazo
        // sin sacarlo de la lista (sus preguntas siguen siendo útiles).
        if (!article.Installed) icon.Opacity = 0.55;
        header.Children.Add(icon);

        var title = new TextBlock
        {
            Text = I18n.T(article.Title),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        // Grid de tres filas (encabezado / descripción / pie): la descripción ocupa el
        // espacio libre, así el pie "Cómo usarlo" queda alineado abajo en TODAS las tarjetas.
        var content = new Grid { RowSpacing = 6 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(header);

        var summary = new TextBlock
        {
            Text = I18n.T(article.Summary),
            FontSize = 11.5,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(summary, 1);
        content.Children.Add(summary);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 2, 0, 0)
        };
        footer.Children.Add(new TextBlock
        {
            Text = article.Installed ? I18n.T("Cómo usarlo") : I18n.T("No instalado"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = article.Installed
                ? ThemeBrushes.Get("AccentBrush")
                : ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (article.Installed)
            footer.Children.Add(new FontIcon
            {
                Glyph = ChevronGlyph,
                FontSize = 9,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        // Cuántas preguntas tiene la ficha: así se ve que la tarjeta no solo explica el uso,
        // también responde las dudas puntuales de ese componente.
        if (faqCount > 0)
        {
            footer.Children.Add(new TextBlock
            {
                Text = "·",
                FontSize = 11,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            });
            footer.Children.Add(new TextBlock
            {
                Text = faqCount == 1 ? I18n.T("1 pregunta") : I18n.T("{0} preguntas", faqCount),
                FontSize = 11,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        Grid.SetRow(footer, 2);
        content.Children.Add(footer);

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(10),
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            BorderThickness = new Thickness(0),
            // Alto fijo: todas las tarjetas del bloque miden lo mismo, aunque el nombre o la
            // descripción sean más cortos. El texto de más se recorta (MaxLines + trimming)
            // en vez de agrandar la tarjeta y desalinear la grilla.
            Height = ComponentCardHeight
        };
        button.Click += (_, _) =>
        {
            _selected = article;
            Render();
        };
        return button;
    }

    // ===================== Detalle =====================

    /// <summary>
    /// Ficha de una entrada: encabezado con ícono y categoría, qué hace, el paso a paso y
    /// las acciones (abrir el componente o las páginas relacionadas).
    /// </summary>
    private void BuildDetail(GuideArticle article)
    {
        var body = new StackPanel { Spacing = 16 };

        // Encabezado: ícono + título + categoría.
        var header = new StackPanel { Spacing = 4 };
        var headerRow = new Grid { ColumnSpacing = 12 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.Children.Add(BuildIconTile(article, 38, 18));

        var titleBlock = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        titleBlock.Children.Add(new TextBlock
        {
            Text = I18n.T(article.Title),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        // Kicker: categoría y, si no está instalado, el aviso (así la ficha no ofrece una
        // página que todavía no existe).
        string? kicker = string.IsNullOrWhiteSpace(article.Category)
            ? null
            : I18n.T(article.Category!).ToUpperInvariant();
        if (!article.Installed)
        {
            string notInstalled = I18n.T("No instalado").ToUpperInvariant();
            kicker = kicker == null ? notInstalled : kicker + "  ·  " + notInstalled;
        }
        if (kicker != null)
            titleBlock.Children.Add(new TextBlock
            {
                Text = kicker,
                FontSize = 10,
                CharacterSpacing = 60,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush")
            });
        Grid.SetColumn(titleBlock, 1);
        headerRow.Children.Add(titleBlock);
        header.Children.Add(headerRow);
        body.Children.Add(header);

        // Qué hace.
        body.Children.Add(new TextBlock
        {
            Text = I18n.T(article.Summary),
            FontSize = 13,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        // Paso a paso (la respuesta de una pregunta o el uso de un componente).
        if (article.Steps.Length > 0)
        {
            body.Children.Add(BuildSectionLabel(I18n.T("Paso a paso")));
            var steps = new StackPanel { Spacing = 10 };
            for (int i = 0; i < article.Steps.Length; i++)
                steps.Children.Add(BuildStep(i + 1, article.Steps[i]));
            body.Children.Add(steps);
        }

        // Preguntas propias del componente: las dudas que aparecen después de saber que
        // la función existe ("¿cómo mido la latencia?", "¿por qué no aparece mi ventilador?").
        var faqs = article.Faqs ?? Array.Empty<GuideQa>();
        if (faqs.Length > 0)
        {
            body.Children.Add(BuildSectionLabel(I18n.T("Preguntas frecuentes")));
            var faqHost = new StackPanel { Spacing = 12 };
            foreach (var qa in faqs)
            {
                var item = new StackPanel { Spacing = 3 };
                item.Children.Add(new TextBlock
                {
                    Text = I18n.T(qa.Question),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                item.Children.Add(new TextBlock
                {
                    Text = I18n.T(qa.Answer),
                    FontSize = 12.5,
                    Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
                faqHost.Children.Add(item);
            }
            body.Children.Add(faqHost);
        }

        // Acciones: abrir el componente y/o páginas relacionadas.
        var actions = new WHPO_UI.Controls.WrapPanel { Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        if (!string.IsNullOrWhiteSpace(article.PageId))
        {
            // No instalado: en vez de ofrecer una página que no existe, se manda al Workshop.
            var primary = article.Installed
                ? new GuideLink(I18n.T("Abrir {0}", I18n.T(article.Title)), article.PageId!)
                : new GuideLink(I18n.T("Instalar en Workshop"), "workshop");
            actions.Children.Add(BuildLinkButton(primary, primary: true));
        }
        foreach (var link in article.Links)
            actions.Children.Add(BuildLinkButton(link, primary: false));
        if (actions.Children.Count > 0)
            body.Children.Add(actions);

        DetailHost.Children.Add(new Border
        {
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22, 20, 22, 22),
            Child = body
        });
    }

    // ===================== Piezas compartidas =====================

    private static UIElement BuildSectionHeader(string title, string subtitle)
    {
        var header = new StackPanel { Spacing = 3 };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 40,
            Foreground = ThemeBrushes.Get("PrimaryTextBrush")
        });
        header.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        return header;
    }

    private static TextBlock BuildSectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        CharacterSpacing = 40,
        FontWeight = FontWeights.SemiBold,
        Foreground = ThemeBrushes.Get("SecondaryTextBrush")
    };

    private static Border BuildDivider() => new()
    {
        Height = 1,
        Background = ThemeBrushes.Get("CardBorderBrush"),
        Margin = new Thickness(12, 0, 12, 0)
    };

    /// <summary>
    /// Ícono del componente dentro de un cuadrado redondeado tenue. "Ventiladores" no tiene
    /// glifo propio: el del registro es un candado (\uE72E), así que se usa el FanIcon
    /// dibujado a mano para que sea el mismo ventilador del navbar.
    /// </summary>
    private static FrameworkElement BuildIconTile(GuideArticle article, double size, double iconSize)
    {
        IconElement icon = string.Equals(article.PageId, "ventiladores", StringComparison.OrdinalIgnoreCase)
            ? WHPO_UI.Controls.FanIcon.CreateIcon(iconSize, ThemeBrushes.Get("AccentBrush"))
            : new FontIcon
            {
                Glyph = string.IsNullOrWhiteSpace(article.IconGlyph) ? HelpGlyph : article.IconGlyph,
                FontSize = iconSize,
                Foreground = ThemeBrushes.Get("AccentBrush")
            };

        if (icon is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.27),
            Background = ThemeBrushes.Get("CoreTrackBackgroundBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon
        };
    }

    private static Border BuildStep(int number, string text)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = ThemeBrushes.Get("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Top
        };
        badge.Child = new TextBlock
        {
            Text = number.ToString(),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushes.Get("AccentForegroundBrush")
        };
        row.Children.Add(badge);

        var stepText = new TextBlock
        {
            Text = I18n.T(text),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(stepText, 1);
        row.Children.Add(stepText);

        return new Border { Child = row };
    }

    private Button BuildLinkButton(GuideLink link, bool primary)
    {
        var button = new Button
        {
            Content = I18n.T(link.Label),
            Padding = new Thickness(14, 7, 14, 7),
            CornerRadius = new CornerRadius(8),
            FontSize = 12.5,
            Background = primary
                ? ThemeBrushes.Get("AccentBrush")
                : ThemeBrushes.Get("CoreTrackBackgroundBrush"),
            Foreground = primary
                ? ThemeBrushes.Get("AccentForegroundBrush")
                : ThemeBrushes.Get("PrimaryTextBrush")
        };
        button.Click += (_, _) => NavigateTo(link.PageId);
        return button;
    }

    // ===================== Datos =====================

    /// <summary>
    /// Los temas de la Guía = los componentes que el usuario tiene instalados, cada uno
    /// explicado con lo que hace y cómo se usa.
    ///
    /// De dónde sale cada cosa: el nombre, la descripción, el icono y la categoría salen del
    /// REGISTRO de componentes (ComponentRegistry), lo mismo que dibuja el navbar —si una
    /// pestaña se ve en el menú, aparece explicada acá, y si se desinstaló, desaparece—. El
    /// "cómo se usa" sale de <see cref="ComponentGuides"/>.
    ///
    /// Qué se descarta: solo un componente marcado como no instalado
    /// ("builtin.removed.&lt;id&gt;"), que no se puede abrir. Los CORE sí se listan: antes se
    /// excluían por ser "la app misma", pero eso dejaba afuera justo las páginas donde
    /// están las respuestas más buscadas (Red para medir latencia, Núcleos, Biblioteca).
    /// La Guía es un manual, no un catálogo de descubrimientos.
    ///
    /// Si no queda nada, se cae a <see cref="FallbackTopics"/>.
    /// </summary>
    private List<GuideArticle> BuildTopics()
    {
        var topics = new List<GuideArticle>();

        foreach (var component in _registry.All)
        {
            // Mismo criterio de "está instalado" que el navbar, pero SIN ocultar el que no
            // lo está: la Guía lo explica igual y la ficha ofrece instalarlo desde Workshop.
            // Ocultarlos era lo que dejaba sin preguntas a páginas como Ventiladores.
            // Los integrados en el hub de Optimizaciones (Arranque, TCP, Limpieza…) no se
            // instalan desde Workshop: viven dentro de esa página y siempre están
            // disponibles. Sin esto la Guía los mostraba como «No instalado» y ofrecía
            // instalarlos donde ni siquiera aparecen.
            bool hubIntegrated = ComponentRegistry.HubIntegratedIds.Contains(component.Id);
            bool installed = hubIntegrated
                || !_settings.Get("builtin.removed." + component.Id, _registry.RequiresInstall(component.Id));

            ComponentGuides.TryGetValue(component.Id, out var guide);
            string? how = string.IsNullOrWhiteSpace(guide.How) ? null : guide.How;
            string[] steps = guide.Steps is { Length: > 0 }
                ? guide.Steps
                : how != null ? new[] { how } : Array.Empty<string>();
            GuideQa[] faqs = ComponentFaqs.TryGetValue(component.Id, out var componentFaqs) && componentFaqs != null
                ? componentFaqs
                : Array.Empty<GuideQa>();

            topics.Add(new GuideArticle(
                component.Name,
                component.Description,
                how,
                steps,
                Array.Empty<GuideLink>(),
                // Para buscar, el texto de la Guía también cuenta: si el usuario escribe
                // "vaciar RAM" tiene que encontrar Limpieza de memoria aunque la descripción
                // no lo diga.
                string.Join(' ', component.Name, component.Description, guide.What, guide.How,
                    string.Join(' ', steps),
                    string.Join(' ', faqs.Select(f => f.Question + " " + f.Answer))),
                component.IconGlyph,
                CategoryName(component.Category),
                component.Id,
                faqs,
                installed));
        }

        if (topics.Count == 0)
            topics.AddRange(FallbackTopics);

        // Orden estable: por categoría y después alfabético, para que la lista no cambie de
        // lugar entre un render y otro según el orden interno del registro.
        return topics
            .OrderBy(t => CategoryOrder(t.Category ?? "Sistema"))
            .ThenBy(t => I18n.T(t.Title), StringComparer.CurrentCultureIgnoreCase)
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

    /// <summary>¿El texto casa con la búsqueda? Sin búsqueda, todo pasa.</summary>
    private static bool Matches(GuideArticle article, string query)
    {
        if (query.Length == 0) return true;
        return (I18n.T(article.Title) + " " + I18n.T(article.Summary) + " " + article.SearchTerms)
            .Contains(query, StringComparison.CurrentCultureIgnoreCase);
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
