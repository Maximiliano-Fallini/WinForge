using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// El ENTORNO de una escena: la luz que llega de todo alrededor, sacada de un HDRI real (radiancia
/// medida en el lugar, no un color inventado).
///
/// Por qué armónicos esféricos (9 coeficientes) y no un mapa de entorno:
/// <list type="bullet">
/// <item>Es el formato más chico posible: 9 <c>float3</c> viajan en el MISMO bloque de constantes por
/// frame que el estilo y las luces (ver <c>Graphics.SceneEnvironmentConstants</c>), así que el IBL no
/// agrega ni una textura, ni un sampler, ni un descriptor, ni una atadura por frame en ninguna de las
/// cuatro APIs. Un cubemap prefiltrrado con mips sí agregaría todo eso CUATRO veces (y en Vulkan,
/// además, un layout de imagen por nivel).</item>
/// <item>Es determinista y gratis por frame: el trabajo pesado (decodificar el .hdr y proyectar) pasa
/// UNA vez, al preparar la escena, y por frame solo se evalúa un polinomio de grado 2.</item>
/// <item>Alcanza de sobra para lo que decide el look: irradiancia difusa (el ambiente que evita los
/// negros planos) y un reflejo del entorno con rugosidad. El detalle fino del HDRI —carteles, luces
/// puntuales del mapa— no entra en 9 coeficientes, y para eso están las luces puntuales de la escena.
/// </item>
/// </list>
///
/// La proyección es la de Ramamoorthi &amp; Hanrahan: se acumula <c>∫ L(ω) Y_lm(ω) dω</c> sobre todos
/// los téxeles del equirect (con el ángulo sólido de cada uno, que es <c>senθ·Δθ·Δφ</c>) y el shader
/// aplica el kernel que corresponda: el del coseno para el difuso y uno que se afila con la rugosidad
/// para el especular. Los mismos 9 coeficientes sirven para las dos cosas porque el kernel se aplica
/// al EVALUAR, no al proyectar.
///
/// La carga es PEREZOSA a propósito: enumerar el catálogo de escenas (para llenar el selector) no
/// puede costar leer y proyectar un HDRI de un megapíxel. El primer pedido de verdad lo hace el
/// backend al preparar la escena, que es el momento para el que la app ya avisa "Preparando la
/// escena…".
/// </summary>
public sealed class SceneEnvironment
{
    /// <summary>Cantidad de coeficientes de armónicos esféricos (hasta banda 2, l = 0..2).</summary>
    internal const int CoefficientCount = 9;

    private readonly Func<string?> _path;
    private readonly object _gate = new();
    private Vector3[]? _coefficients;
    private bool _resolved;

    private SceneEnvironment(Func<string?> path, float intensity, string description)
    {
        _path = path;
        Intensity = intensity;
        Description = description;
    }

    /// <summary>Cuánto pesa el entorno sobre el ambiente procedural de la escena (1 = tal cual el
    /// HDRI, 0 = apagado). Es la perilla de dirección de arte: un HDRI nocturno es MUY oscuro y a
    /// veces conviene subirlo para que la escena no quede a un plano negro.</summary>
    public float Intensity { get; }

    /// <summary>De dónde salió el entorno, para el informe y los diagnósticos.</summary>
    public string Description { get; }

    /// <summary>
    /// Entorno cuyo archivo se resuelve la PRIMERA vez que alguien lo pide. Es lo que usa la escena,
    /// que conoce la ruta recién cuando resuelve su carpeta de assets (y eso no puede pasar al
    /// enumerar el catálogo).
    /// </summary>
    public static SceneEnvironment Lazy(Func<string?> path, float intensity = 1f, string description = "") =>
        new(path, intensity, description);

    /// <summary>True si el entorno tiene coeficientes: sin HDRI legible, la escena se queda con su
    /// cielo procedural y no hay IBL.</summary>
    public bool IsAvailable { get { Resolve(); return _coefficients != null; } }

    /// <summary>Los 9 coeficientes de radiancia, o <c>null</c> si no hay HDRI legible.</summary>
    public Vector3[]? Coefficients { get { Resolve(); return _coefficients; } }

    private void Resolve()
    {
        if (_resolved) return;
        lock (_gate)
        {
            if (_resolved) return;
            try
            {
                string? path = _path();
                if (!string.IsNullOrEmpty(path)) _coefficients = Project(path);
            }
            catch (Exception)
            {
                // Sin assets (o con el HDRI ilegible) la escena sigue: pierde el IBL, no la corrida.
                _coefficients = null;
            }
            _resolved = true;
        }
    }

    /// <summary>
    /// Proyecta un equirect de radiancia a 9 coeficientes de armónicos esféricos. Devuelve <c>null</c>
    /// si el archivo no se puede leer.
    ///
    /// El OJO de la proyección: la base se evalúa con los ejes permutados (x→x, y→z, z→y) para que el
    /// eje polar del armónico sea el Y del mundo, que es "arriba". La permutación es una rotación, así
    /// que la base sigue siendo ortonormal — y el shader tiene que usar EXACTAMENTE las mismas
    /// expresiones (ver <c>EnvShEval</c> en <c>SceneShaders.cs</c>): si una de las dos cambia de ejes,
    /// el entorno sale girado y la luz viene del lado equivocado.
    /// </summary>
    internal static Vector3[]? Project(string path)
    {
        var hdr = HdrImage.Load(path, out int width, out int height);
        if (hdr == null || width < 4 || height < 2) return null;

        var coefficients = new Vector3[CoefficientCount];
        var basis = new float[CoefficientCount];

        double deltaTheta = Math.PI / height;
        double deltaPhi = 2.0 * Math.PI / width;

        for (int y = 0; y < height; y++)
        {
            // Centro del téxel y no su esquina: con la esquina el mapa queda inclinado media celda.
            double theta = (y + 0.5) * deltaTheta;
            double sinTheta = Math.Sin(theta);
            double cosTheta = Math.Cos(theta);
            double weight = sinTheta * deltaTheta * deltaPhi;   // ángulo sólido de ESTE téxel

            for (int x = 0; x < width; x++)
            {
                double phi = (x + 0.5) * deltaPhi;
                var direction = new Vector3(
                    (float)(sinTheta * Math.Sin(phi)),
                    (float)cosTheta,
                    (float)(-sinTheta * Math.Cos(phi)));

                EvaluateBasis(direction, basis);

                int offset = (y * width + x) * 3;
                float red = hdr[offset + 0];
                float green = hdr[offset + 1];
                float blue = hdr[offset + 2];
                if (red == 0f && green == 0f && blue == 0f) continue;

                for (int i = 0; i < CoefficientCount; i++)
                {
                    float scaled = (float)(basis[i] * weight);
                    coefficients[i] += new Vector3(red * scaled, green * scaled, blue * scaled);
                }
            }
        }

        return coefficients;
    }

    /// <summary>
    /// Base real de armónicos esféricos hasta l = 2, con los ejes permutados (ver la nota del ojo en
    /// <see cref="Project"/>). Los valores son los de la base normalizada estándar.
    /// </summary>
    private static void EvaluateBasis(Vector3 direction, float[] basis)
    {
        // u = (x, z, y): el componente "z" de la base es el ARRIBA del mundo.
        float ux = direction.X;
        float uy = direction.Z;
        float uz = direction.Y;

        basis[0] = 0.282095f;
        basis[1] = 0.488603f * uy;
        basis[2] = 0.488603f * uz;
        basis[3] = 0.488603f * ux;
        basis[4] = 1.092548f * ux * uy;
        basis[5] = 1.092548f * uy * uz;
        basis[6] = 0.315392f * (3f * uz * uz - 1f);
        basis[7] = 1.092548f * ux * uz;
        basis[8] = 0.546274f * (ux * ux - uy * uy);
    }
}
