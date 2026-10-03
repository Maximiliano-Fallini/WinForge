namespace WinForge.Component.Benchmark.Assets;

/// <summary>
/// Un pack de assets PUBLICADO: la carpeta de un set (ver <c>Scenes/NeonScene.cs</c>,
/// <c>SceneAssets</c>) empaquetada y versionada, con su huella para poder verificarla al bajarla.
///
/// Por qué la versión va en el nombre del archivo además de acá: la URL del pack es
/// <c>benchmark-assets-&lt;set&gt;-&lt;versión&gt;.zip</c> dentro de la release de contenido, así que publicar
/// un pack nuevo no pisa el anterior (dos usuarios con versiones distintas de la app siguen bajando
/// el suyo) y el SHA-256 del bloque no cambia nunca para una versión ya publicada.
/// </summary>
/// <param name="Set">Nombre de la carpeta del set (el mismo que declara la escena en
/// <c>SceneDefinition.AssetSet</c>).</param>
/// <param name="Version">Versión del pack, en el nombre del archivo.</param>
/// <param name="Sha256">SHA-256 del .zip, en minúsculas (lo verifica la descarga).</param>
/// <param name="SizeBytes">Tamaño del .zip: se muestra antes de bajar y sirve para detectar una
/// respuesta HTTP truncada que igual diera el hash correcto por casualidad.</param>
internal sealed record AssetPack(string Set, string Version, string Sha256, long SizeBytes);

/// <summary>
/// Los packs de assets que este componente sabe bajar, por set.
///
/// Es la lista de PUBLICACIONES, no la de carpetas: un set puede estar en disco (el usuario lo bajó
/// con <c>tools/benchmark-assets/fetch-assets.ps1</c>) sin figurar acá, y en ese caso la escena corre
/// igual — esto solo agrega el camino "no tengo el set, lo traigo solo".
///
/// El bloque entre los marcadores lo reescribe <c>tools/benchmark-assets/pack-assets.ps1 -Update</c>
/// al publicar un pack: ahí adentro no se edita a mano (es el equivalente de la Regla de oro 4 para
/// los assets: la versión, el hash y el tamaño tienen que salir del archivo que se subió).
/// </summary>
internal static class AssetPackCatalog
{
    // BEGIN packs (lo reescribe tools/benchmark-assets/pack-assets.ps1 -Update)
    internal static readonly AssetPack[] Packs =
    {
        new AssetPack("neon", "1.0.0", "284906e18fde72602973131282b5f2971424d2b2299ec4decead21f475205635", 16642291),
    };
    // END packs

    /// <summary>El pack publicado de un set, o <c>null</c> si ese set todavía no se publicó.</summary>
    internal static AssetPack? Find(string set)
    {
        foreach (var pack in Packs)
        {
            if (string.Equals(pack.Set, set, StringComparison.OrdinalIgnoreCase)) return pack;
        }
        return null;
    }
}
