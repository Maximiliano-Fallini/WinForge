# i18n del componente — verificación (2/10/2026)

Cómo traduce el componente: `AppBridge.T` entra por reflexión a `WHPO_UI.I18n.T`, que resuelve en este
orden: **pack del idioma → bloque `i18n` de la entrada del componente en `components.json` → tabla
embebida de la app → en-US → español (fuente)**. El bloque del catálogo se registra al arrancar (copia
cacheada, ver `App.xaml.cs`) y en cada carga del Workshop; el componente re-aplica sus textos en cada
cambio de idioma (`BenchmarkPage._retranslate` + `AppBridge.OnLanguageChanged`).

**Veredicto:** el mecanismo es compatible (fallback sin romper nada, claves exactas, sin placeholders
rotos) y el catálogo es simétrico: **116 claves × 6 idiomas, todas con valor y con los mismos
placeholders que el texto fuente**. Lo que falta es **cobertura**: los textos de abajo no están en
ninguna de las tres fuentes, así que se ven en **español en los 6 idiomas** (inglés incluido).

## Faltan en el `i18n` de `components.json` (63)

### Tarjeta "Gráficos de la escena" (17)
- `Gráficos de la escena` — título de la tarjeta.
- `Cada escena guarda su propia configuración y lo elegido queda anotado en el informe de la corrida.` — pista.
- `Calidad gráfica` — rótulo.
- `Un nivel preajustado que fija todas las opciones de abajo de un golpe. Si después tocás una opción suelta, la calidad pasa a Personalizado.` — tooltip.
- `Sombras` — rótulo.
- `Las sombras que proyectan los objetos. El nivel fija las dos cosas a la vez: si la pasada de sombras se dibuja y con cuánta resolución del mapa. Desactivadas la escena no cambia: se saca el costo de la pasada de profundidad y del muestreo, que es justo lo que la corrida mide.` — tooltip.
- `Iluminación global` — rótulo.
- `La luz ambiente y los reflejos que reciben los materiales, tomados del cielo real de la escena (HDRI). Desactivada, la escena se ilumina solo con las luces de su estilo y el fondo pasa a ser un cielo procedural: cambia la luz y los reflejos, no solo el fondo.` — tooltip.
- `Resolución` — rótulo.
- `Resolución de la ventana de la escena; solo se aplica en modo ventana, porque en pantalla completa manda el monitor. Automática usa el tamaño que entra en el monitor y Nativa el área útil. Si lo pedido no entra, la corrida sale en la que entre y queda un aviso en el informe.` — tooltip.
- `Activada` / `Desactivada` — opciones de iluminación global.
- `La luz ambiente y los reflejos salen del cielo real de la escena (HDRI). Es la iluminación de siempre.` — línea de estado (HDRI).
- `La escena se ilumina solo con las luces de su estilo y el fondo pasa a ser un cielo procedural: además de cambiar la luz y los reflejos, se saltea la lectura del .hdr al arrancar.` — línea de estado (procedural).
- `La ventana de la escena: Automática usa el tamaño que entra en el monitor y Nativa el área útil. Si lo pedido no entra, la corrida sale en la que entre y queda un aviso en el informe.` — línea de estado (ventana).
- `En pantalla completa la escena usa la resolución del monitor y lo elegido acá no se usa.` — línea de estado (fullscreen).
- `Nativa` — opción de resolución.

### Presets de calidad (9)
- `Bajo` · `Medio` · `Alto` · `Ultra` — niveles.
- `Deja cada ajuste por separado: abajo se eligen las sombras, la iluminación global y la resolución una por una.` — descripción de Personalizado.
- `Lo más liviano: sombras desactivadas, sin iluminación global y con la ventana en 1280 × 720.`
- `Sombras medias e iluminación global, con la ventana en 1600 × 900.`
- `Sombras altas e iluminación global, con la ventana en automático. Es la calidad de siempre y la recomendada.`
- `Sombras ultra e iluminación global, con la ventana al tamaño del monitor. Lo más pesado de la lista.`

### Niveles de sombra (8)
- `Bajas` · `Medias` · `Altas` — niveles (`Ultra` y `Desactivadas` ya están cubiertos: `Ultra` falta por los presets y `Desactivadas` está en la tabla de la app).
- `Sin sombras proyectadas. La escena no cambia: se saca el costo de la pasada de profundidad y del muestreo, que es justo lo que la corrida mide.`
- `El mapa más chico (512²): el costo por frame más bajo y el borde de la sombra más blando.`
- `Sombras en 1024²: equilibrio entre definición del borde y costo por frame.`
- `Sombras en 2048²: el borde nítido de siempre, sin castigar el frame.`
- `Sombras en 4096²: el borde más fino posible. La textura de profundidad ocupa 64 MB de memoria de video y la pasada cuesta algo más por frame.`

### Tooltips viejos de la tarjeta de configuración (5)
- `Con qué API de Windows se dibuja la escena: Direct3D 11, Direct3D 12, Vulkan u OpenGL. Las que no estén disponibles en este equipo aparecen deshabilitadas.`
- `El diorama que se dibuja mientras se mide. Cada escena tiene su propio peso (geometría, sombras, reflejos): dos corridas de escenas distintas no se comparan.`
- `El adaptador con el que se dibuja. Automática usa la de más memoria: en un equipo con dos GPU (una integrada y una dedicada), conviene elegir la dedicada.`
- `Cómo se abre la escena: en ventana, en pantalla completa sin bordes (se ve como pantalla completa sin cambiar el modo de video del monitor) o en pantalla completa exclusiva (menos intermediarios, pero puede fallar si otra app la tiene tomada).`
- `Cuánto dura la MEDICIÓN después del calentamiento. Esc corta la corrida y el informe guarda lo medido hasta ahí.`

### Informe (14)
- Filas: `Gráficos` · `Preset gráfico` · `Calentamiento` · `Espera de la cola de la GPU`.
- Valores/templates: `sombras {0}²` · `sin sombras` · `entorno HDRI` · `cielo procedural` · ` · con sincronización vertical` · `sin datos: el driver no devolvió tiempos de GPU en esta corrida` · `mediana {0:F3} ms · promedio {1:F3} ms` · `{0:F1} s y {1} frames fuera de los números (compilado de shaders, subida de geometría y caches del driver)` · `{0:F1} s fuera de los números (compilado de shaders, subida de geometría y caches del driver)`.
- Error del diálogo: `No se pudo abrir la confirmación: {0}`.

### Escena (2)
- `Frutiger Aero (en desarrollo)` — nombre.
- `El paseo de la postal: la cámara gira por la cascada y el arroyo del desagüe, cruza el campo estilo XP con su sendero de tierra, sobrevuela el lago hondo y termina frente a la ciudad —torres de vidrio con losa, casas con balcones y locales con toldo—. El prado, la orilla y el paseo están poblados con MODELOS REALES de fotogrametría (árboles, matas, pasto, flores, rocas, bancos y faroles, cada uno con su material PBR) y el aire lleva partículas con sprite: polen fino, hojas y pétalos girando y semillas con penacho. Cielo, agua y rápidos, burbujas transparentes a la deriva y sombras de primer plano. Mide relleno por píxel, material por material y muchos objetos instanciados.` — descripción.

### Avisos del host (5)
- `La resolución pedida ({0}×{1}) no entra en el área útil del monitor con la ventana con bordes: la corrida salió en {2}×{3}. Para esa resolución exacta, usá pantalla completa sin bordes.`
- `Esta API no tiene pantalla completa exclusiva en este componente: la corrida salió en ventana.`
- `La ventana cambió de tamaño durante la corrida: se recreó el swapchain y hay frames de la medición que no son de la escena.`
- `La corrida midió menos de lo pedido: {0} s de {1} s.`
- `El driver no devolvió ningún tiempo de GPU en esta corrida: el informe no puede decir cuánto trabajó la placa.`

### Motivos de corte (6)
`BenchmarkPage` arma el estado como `AppBridge.T("Corrida cortada: {0}", result.AbortReason)`: el template
se traduce y el motivo entra CRUDO (el `string.Format` va sobre el texto ya traducido), así que en otro
idioma queda una frase mezclada ("Run stopped: Se cerró la ventana de la escena.").

- Con entrada en el catálogo pero **sin usar** (habría que pasarlas por `T`): `Se cerró la ventana de la escena.` · `Cortada con Esc.` · `Cancelada por el usuario.`
- **Sin entrada** en ninguna fuente: `Se superó el límite de tiempo de la corrida.` · `El dispositivo gráfico se perdió durante la corrida.` · `La corrida no respondió dentro del tiempo límite (posible cuelgue del driver): se canceló desde la interfaz.`

## Otros hallazgos

- **El pack pisa al catálogo**: `En vivo` (título de la tarjeta) está traducido en el catálogo
  (`ru-RU`: "В реальном времени", `zh-CN`: "实时") pero el pack de esos idiomas trae "Жить" / "居住" y
  tiene PRIORIDAD sobre el catálogo, así que esos dos idiomas muestran la traducción mala. Se corrige en
  `languages-src/ru-RU.json` y `languages-src/zh-CN.json` (no en `components.json`).
- **Dos claves viejas del catálogo** que el componente ya no pide: `La corrida no se completó: se
  midieron {0} de {1} frames.` (hoy es `La corrida midió menos de lo pedido: {0} s de {1} s.`) y `Se
  superó el límite de 15 minutos de corrida.` (hoy es `Se superó el límite de tiempo de la corrida.`).
- **Args dinámicos en español dentro de plantillas traducidas** (misma clase que los motivos de corte):
  las razones de disponibilidad de las APIs (`BackendRegistry`, "No disponibles en este equipo: {0}."), el
  motivo técnico de los packs de assets (`pack.Detail` en "No se pudieron preparar los assets de la
  escena: {0}"), `AdapterDetail` y los mensajes de excepción.
- **HUD de la escena** (`Host/MetricsHud.cs`): las etiquetas del overlay en vivo (`FPS`, `frame`,
  `GPU/frame`, `GPU`, `CPU`, `RAM`, `VRAM`) y el título `WinForge Benchmark` se dibujan con GDI y no pasan
  por el motor de i18n. Son términos neutros, pero si se quiere traducir "frame" hay que tocar el HUD.
- **Catálogo vs versión del componente**: la entrada de `components.json` dice `0.1.15` y
  `BenchmarkComponent.Version` es `0.1.16`: al publicar el 0.1.16 hay que actualizar versión, URL, hash y
  tamaño del zip, además de las claves de este documento.

> Cuando estas claves estén en `components.json` (y los packs corregidos), este archivo se puede borrar.
