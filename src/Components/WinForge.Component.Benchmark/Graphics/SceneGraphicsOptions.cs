using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Cómo se DIBUJA la escena: lo que el usuario elige antes de arrancar (con un preset o perilla por
/// perilla) y que cambia la imagen y, por lo tanto, lo que se mide.
///
/// Vive en el pedido de la corrida, y el motivo es la COMPARACIÓN:
/// <list type="bullet">
/// <item>En la ESCENA no puede vivir: las tres escenas del catálogo se arman UNA vez y son el mismo
/// objeto para todas las corridas (ver <c>SceneCatalog.All</c>), así que una perilla por corrida no
/// tiene dónde guardarse.</item>
/// <item>En el BACKEND tampoco: las cuatro APIs tienen que dibujar lo mismo, y una perilla que cada
/// backend interpreta por su cuenta daría dos imágenes distintas del mismo mundo. Por eso el valor
/// viaja en <see cref="BackendInitOptions"/> a las CUATRO por el mismo camino —el mismo criterio por
/// el que la fuerza de la sombra vive en la escena—.</item>
/// </list>
///
/// Los valores por defecto son el LOOK DE SIEMPRE: sin tocar la configuración, la escena se dibuja
/// exactamente como se dibujaba antes de que esta clase existiera.
/// </summary>
public sealed record SceneGraphicsOptions
{
    public static SceneGraphicsOptions Default { get; } = new();

    /// <summary>
    /// Sombras proyectadas. Apagadas, los backends no corren la PASADA de profundidad (no hay nada que
    /// escribir en el mapa) y el bloque de constantes deja la fuerza en cero, así que el shader sale
    /// del sombreado en su primera línea: no es "una sombra más suave", es el costo de la pasada y del
    /// muestreo menos.
    ///
    /// Lo que NO cambia: el mapa de sombras se sigue creando con la escena (ocupa VRAM, pero no cuesta
    /// tiempo por frame) y el volumen de la escena se sigue resolviendo. Lo que se mide al apagarlas es
    /// el costo por frame, que es lo que interesa.
    /// </summary>
    public bool Shadows { get; init; } = true;

    /// <summary>Resolución mínima del mapa de sombras que acepta la configuración.</summary>
    public const int MinShadowMapSize = 512;

    /// <summary>
    /// Resolución máxima. 4096² de profundidad son 64 MB: más que eso no entra en cualquier placa y el
    /// benchmark tiene que poder correr en la máquina del que lo prueba.
    /// </summary>
    public const int MaxShadowMapSize = 4096;

    private readonly int _shadowMapSize = SceneDefinition.ShadowMapSize;

    /// <summary>
    /// Resolución del MAPA DE SOMBRAS: el lado del cuadrado donde la pasada escribe la profundidad
    /// desde el sol. 2048 es la de siempre; 1024 abarata la pasada (y ablanda el borde), 4096 la
    /// encarece y afina el detalle. Es la perilla que más mueve el trabajo por frame de la pasada.
    ///
    /// El valor se guarda ya NORMALIZADO (potencia de dos entre <see cref="MinShadowMapSize"/> y
    /// <see cref="MaxShadowMapSize"/>): la textura de profundidad se crea con este número, así que un
    /// valor raro (0, 3000, 100000) sería un error del driver y no una imagen fea. El validador vive
    /// ACÁ, en el dato, y no en cada backend, porque es el mismo número para las cuatro APIs.
    /// </summary>
    public int ShadowMapSize
    {
        get => _shadowMapSize;
        init => _shadowMapSize = NormalizeShadowMapSize(value);
    }

    /// <summary>
    /// Tamaño de un téxel del mapa en UV, para el PCF. Sale del tamaño EFECTIVO del mapa, que es el que
    /// crean los cuatro backends: si el borde de la sombra se calculara con otro número, el PCF
    /// muestrearía con el paso equivocado (borde más ancho o más corto que el real).
    /// </summary>
    public float TexelSize => 1f / _shadowMapSize;

    /// <summary>
    /// Entorno de la escena (el HDRI proyectado a armónicos esféricos, ver <c>SceneEnvironment</c>):
    /// alimenta la luz ambiente y los REFLEJOS del cielo que reciben las superficies
    /// (<c>SkyIrradiance</c> y <c>SkyReflection</c> en el shader). Apagado, la escena cae al cielo
    /// PROCEDURAL del estilo, que es el camino que ya usan las escenas sin HDRI: no es solo un cambio
    /// de fondo, cambia lo que reciben los materiales.
    ///
    /// Además el archivo .hdr NO se lee: la carga es perezosa y lo que la dispara es el pedido de los
    /// coeficientes (ver <c>SceneEnvironmentConstants.From</c>), así que apagarlo acorta el
    /// calentamiento además de sacar el costo por píxel.
    /// </summary>
    public bool Environment { get; init; } = true;

    /// <summary>
    /// Lleva un tamaño cualquiera al valor EFECTIVO que van a usar los cuatro backends: la potencia de
    /// dos de abajo, dentro del rango válido. Es pública porque la página y los presets tienen que
    /// mostrar y comparar el MISMO número que se corre (un settings.json escrito a mano con 3000 no
    /// puede quedar en pantalla como 3000 mientras la textura se crea de 2048).
    /// </summary>
    public static int NormalizeShadowMapSize(int size)
    {
        if (size <= 0) return SceneDefinition.ShadowMapSize;

        // A la potencia de dos de ABAJO, y nunca por debajo del mínimo: los filtros y los formatos de
        // bloque rinden mejor con potencias de dos, y el PCF asume téxeles cuadrados del mismo tamaño.
        int power = MinShadowMapSize;
        while (power < MaxShadowMapSize && power * 2 <= size) power *= 2;
        return power;
    }
}
