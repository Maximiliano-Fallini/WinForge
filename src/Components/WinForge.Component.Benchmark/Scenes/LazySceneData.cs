using System.Collections;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// Una textura que se decodifica la PRIMERA vez que alguien la pide, no cuando se arma el catálogo
/// de escenas.
///
/// Por qué: la página del Workshop enumera las escenas para llenar el selector, y eso no puede
/// costar la decodificación de treinta JPEG de 1k (medido: del orden de un segundo por textura en
/// un solo hilo) ni el disco. El primer pedido de verdad lo hace el backend cuando PREPARA la
/// escena, que es justo el momento para el que la app ya avisa "Preparando la escena…".
///
/// Si el archivo no está o no se puede decodificar, <see cref="Value"/> queda en <c>null</c> y la
/// escena sigue sin ese mapa (material de color plano): un asset faltante no puede tumbar la corrida.
/// </summary>
public sealed class SceneTextureSource
{
    private readonly Func<SceneTexture?> _factory;
    private SceneTexture? _value;
    private bool _resolved;

    private SceneTextureSource(Func<SceneTexture?> factory, string description)
    {
        _factory = factory;
        Description = description;
    }

    /// <summary>De dónde sale la textura (para el informe y los diagnósticos).</summary>
    public string Description { get; }

    public SceneTexture? Value
    {
        get
        {
            if (!_resolved)
            {
                try { _value = _factory(); }
                catch (Exception) { _value = null; }
                _resolved = true;
            }
            return _value;
        }
    }

    /// <summary>Textura de un archivo del set de assets (jpg/png).</summary>
    public static SceneTextureSource FromFile(string path, bool srgb) =>
        new(() => File.Exists(path) ? SceneTexture.FromFile(path, srgb) : null, path);

    /// <summary>Textura cuyos bytes ya están en memoria (las imágenes embebidas de un .glb).</summary>
    public static SceneTextureSource FromBytes(Func<byte[]?> bytes, string name, bool srgb) =>
        new(() => bytes() is { Length: > 0 } data ? SceneTexture.FromStream(data, name, srgb) : null, name);
}

/// <summary>
/// Lista de mallas que se arma la primera vez que alguien la recorre: las escenas con modelos glTF
/// cargan geometría REAL (leer el .gltf, expandir índices, aplicar los nodos) y eso no tiene por qué
/// pagarse al abrir el selector de escenas. Una vez armada queda cacheada y la comparten los cuatro
/// backends y todas las corridas de la sesión.
/// </summary>
public sealed class LazySceneMeshes : IReadOnlyList<SceneMesh>
{
    private readonly Func<IReadOnlyList<SceneMesh>> _factory;
    private readonly object _gate = new();
    private IReadOnlyList<SceneMesh>? _meshes;

    public LazySceneMeshes(Func<IReadOnlyList<SceneMesh>> factory) => _factory = factory;

    public IReadOnlyList<SceneMesh> Value
    {
        get
        {
            if (_meshes != null) return _meshes;
            lock (_gate)
            {
                _meshes ??= _factory();
                return _meshes;
            }
        }
    }

    public SceneMesh this[int index] => Value[index];

    public int Count => Value.Count;

    public IEnumerator<SceneMesh> GetEnumerator() => Value.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => Value.GetEnumerator();
}
