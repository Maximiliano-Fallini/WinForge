# Shadow mapping de 1 cascada — implementado

> Ítem de la Fase 1 del benchmark cinematográfico (ver `README-CINEMATICO.md` §6). **Está
> implementado en las cuatro APIs y encendido en la escena Neón (`ShadowStrength = 1`).**
>
> Lo único que falta es mirarlo: el código compila (0 errores, 0 advertencias) y el SPIR-V está
> regenerado, pero **no se corrió ni se capturó** en ninguna API (ver §4).

## 1. Qué hace

Una sola pasada de PROFUNDIDAD de toda la escena vista **desde el sol**, a una textura de 2048², y
después cada píxel pregunta "¿estoy tapado de la luna?" contra ese mapa. Consecuencias:

- **La geometría proyecta sola.** No hay que declarar una esfera por objeto (eso es lo que hacía
  `ObjectShadow`, y por eso los autos tenían una mancha redonda y las paredes no proyectaban nada).
  En la escena Neón no hay NADA que configurar: el volumen del mapa se deduce de la geometría
  (`SceneDefinition.ResolveShadowVolume`).
- **Resolución y costo FIJOS**: una cascada, 2048², una pasada más por frame. Dos corridas del mismo
  equipo siguen midiendo lo mismo (con cascadas múltiples el costo dependería de hacia dónde mira la
  cámara y los números dejarían de ser comparables).
- **Las cuatro APIs dibujan el mismo mundo**: el valor que enciende la sombra vive en la ESCENA, no
  en un backend, así que no puede haber dos APIs con sombras y dos sin ellas.

## 2. Cómo está armado, pieza por pieza

**Datos (C#)**

| Pieza | Dónde |
|---|---|
| Volumen (AABB) deducido de la geometría, o a mano con `ShadowVolume` | `SceneDefinition.ResolveShadowVolume` |
| Perillas: `ShadowStrength`, `ShadowMapSize = 2048`, `DepthBias`, `Penumbra` | `SceneDefinition`, `SceneShadowVolume` |
| Qué mallas proyectan | `SceneDefinition.MeshCastsShadow` |
| Matriz de luz (cámara ortográfica detrás del volumen) | `SceneEnvironmentConstants.BuildLightViewProjection` |
| Ajuste por API de la pasada | `SceneEnvironmentConstants.LightViewProjectionFor` |
| Matriz + parámetros en el bloque por frame (240 bytes) | `SceneEnvironmentConstants.From` |

**Shader (HLSL y GLSL en paridad 1:1)**

- `Texture2D ShadowMap : register(t6)` + `SamplerComparisonState ShadowSampler : register(s3)`
  (GLSL: `uniform sampler2DShadow shadowMap;`).
- `ShadowFactor(worldPosition)`: proyecta al espacio de luz, compara con PCF 3×3 y devuelve 1 fuera
  del volumen o con la sombra apagada. **Solo se aplica al término directo del sol** en `ShadePbr`:
  el ambiente y el IBL no se sombrean (una superficie en sombra sigue recibiendo el cielo, que es lo
  que la hace leer como sombra y no como un agujero negro).
- `VulkanShaders.g.cs` está regenerado y verificado: `tools/SpirvGen` reproduce el archivo **byte a
  byte** (se comprobó que el .cs no cambia de hash al volver a correrlo).

**Los cuatro backends** (todo detrás de `if (_shadowEnabled)`, apagado por defecto y encendido en la
Neón):

| API | Recurso | Pasada | Descriptor |
|---|---|---|---|
| D3D11 | textura `R32_Typeless` (DSV `D32_Float` / SRV `R32_Float`) + sampler de comparación | `OMSetRenderTargets(0, …, dsv)` y `PSSetShader(null)`: solo profundidad | `t6`/`s3` |
| D3D12 | recurso `R32_Typeless` en `PixelShaderResource` + DSV propio + SRV y sampler en los heaps | PSO propia de 0 render targets y sin PS, con las DOS barreras de estado | tablas 6 y 7 |
| Vulkan | imagen `D32Sfloat` (`Sampled` + `DepthStencilAttachment`) + sampler de comparación | render pass solo-profundidad (2048², `finalLayout = ShaderReadOnlyOptimal`) y pipeline sin etapa de fragmentos | bindings 8 y 7 |
| OpenGL | textura `GL_DEPTH_COMPONENT24` con `GL_COMPARE_REF_TO_TEXTURE` + FBO propio | FBO con `glDrawBuffer(GL_NONE)`, programa con el mismo VS y un PS vacío | unidad de textura 3 |

La geometría se dibuja con el **MISMO vertex shader** en las cuatro: la pasada le pone la matriz del
sol en `ViewProjection` y listo. No hay shader nuevo, ni entry point nuevo, ni pipeline duplicada
salvo la de "solo profundidad" (que en D3D12 y Vulkan no puede faltar, porque el pipeline es un
objeto).

## 3. Los tres detalles que no se deducen leyendo el código

1. **Tabla de bindings de Vulkan.** `SpirvGen` compila con `-fvk-t-shift 2` y `-fvk-s-shift 4`:

   | Recurso | Registro HLSL | Binding Vulkan | Tipo |
   |---|---|---|---|
   | Constantes por frame | `b0` | 0 | UniformBuffer |
   | Textura de detalle | `t0` | 2 | SampledImage |
   | Franja de métricas | `t1` | 3 | StorageBuffer |
   | Sampler de detalle | `s0` | 4 | Sampler |
   | Sampler de materiales | `s1` | 5 | Sampler |
   | Texturas de materiales | `t4` | 6 | SampledImage |
   | Shadow map | `t6` | **8** | SampledImage |
   | Sampler comparador | `s3` | **7** | Sampler |

   `t6`/`s3` y **no** `t5`/`s2`: con los corrimientos actuales `t5 → 7` y `s2 → 6`, y el 6 ya lo
   ocupa la imagen de materiales (dos descriptores de tipos distintos en un binding son inválidos).
   Además, el layout TIENE que declarar estos bindings **aunque la escena no use sombras**: la
   referencia vive en el SPIR-V, no en el camino de ejecución, así que sin ellos falla la creación de
   la pipeline y no se dibuja NADA.

2. **La orientación de la textura NO es la misma en las cuatro APIs.** El shader calcula su
   coordenada con la fórmula clásica `uv = ndc.xy * 0.5 + 0.5`, así que la PASADA tiene que escribir
   con esa convención; lo que cambia es a qué téxel manda la placa cada posición de clip:

   - **Direct3D y Vulkan**: el NDC tiene la Y hacia ABAJO, así que `ndc.y = +1` cae en `v = 0` (el
     opuesto de la fórmula). La pasada niega la Y de clip. Es el mismo motivo por el que todo
     muestreo de shadow map en Direct3D usa `float2(0.5, -0.5)`.
   - **OpenGL**: la Y ya cae bien (`ndc.y = +1` → `v = 1`), pero la PROFUNDIDAD no: el búfer de
     OpenGL mapea [-1,1] a [0,1] mientras Direct3D y Vulkan ya usan [0,1], y la matriz de
     System.Numerics produce la convención de Direct3D. Sin convertir (`z' = 2z − w`), el valor
     escrito caería en [0,5 .. 1] y la comparación no encontraría ninguna sombra.

   El arreglo va en `LightViewProjectionFor(scene, ShadowMapAxis)`, **nunca en el shader**: el bloque
   que ve el shader es idéntico en las cuatro APIs y por eso las cuatro comparan el mismo número.

3. **Dos bloques de constantes por frame, no uno.** La pasada de sombras lleva la matriz del sol y la
   principal la de la cámara, y las dos van en el MISMO command buffer. Un solo bloque o una sola
   rebanada se pisa entre las dos escrituras del CPU (D3D11: `Map(WriteDiscard)` dos veces; D3D12:
   rebanadas `slot * 2` y `slot * 2 + 1`; Vulkan: dos uniform buffers y dos descriptor sets por
   frame).

## 4. Qué falta verificar (y cómo)

Nada de esto se puede hacer sin la aplicación corriendo:

1. **Que las sombras se vean y estén en su lugar.** Es el punto entero del cambio. Un error de eje (el
   punto 2 de arriba), de sesgo o de escala se ve como sombras corridas, espejadas o con acné, y
   ninguna de las dos cosas da un error en ningún log.
2. **Paridad entre las cuatro**: la misma captura en las CUATRO APIs y en la MISMA sesión (ver §9 del
   README cinematográfico: las capturas viejas son de sesiones distintas y no sirven).
3. **Vulkan con `VK_LAYER_KHRONOS_validation`**: sin errores de descriptor, de layout ni de barreras.
4. **Que la sombra apagada no cambie nada**: con `ShadowStrength = 0` la imagen tiene que ser idéntica
   a la del build anterior (0.1.12) en las cuatro APIs.
5. **El sesgo y la penumbra** son las dos únicas perillas de arte y hoy están en su valor inicial
   (`DepthBias = 0.0016`, `Penumbra = 1.6`): el sesgo se mide en unidades de profundidad del volumen
   (unos 0,6 m en este diorama), así que si las sombras se despegan de los objetos, ESTE es el número
   que hay que bajar, y si aparece acné en el asfalto, subirlo.

## 5. Cosas que quedaron afuera a propósito

- **Cascadas múltiples**: harían que el costo por frame dependa de la cámara (dos corridas del mismo
  equipo dejarían de medir lo mismo). El diorama de la Neón entra entero en una.
- **Sombras de las partículas** (lluvia, polen): son transparentes y el shader las orienta hacia la
  cámara, así que en espacio de luz serían manchas que no corresponden a nada. Tampoco proyectan
  sombra en la vida real.
- **Sombras de las escenas que el shader desplaza** (la grilla del terreno del Aero y las rocas del
  corredor): su posición en el CPU no es la del mundo. Esas escenas siguen con las esferas analíticas
  de `ShadowCasters` (`ObjectShadow`), que se quedan como respaldo para quien no tenga shadow map.
- **`spirv-cross`**: HECHO (ver §12 de `README-CINEMATICO.md`): el GLSL del backend OpenGL se genera
  del MISMO SPIR-V que usa Vulkan, así que no hay dos fuentes a mano. El shader de la pasada sigue
  siendo el mismo en las cuatro APIs; el único agregado de OpenGL es el FS vacío
  (`GlShaders.EmptyPixelShader`), porque ahí no existe una pasada sin etapa de fragmentos.
