# Reglas de desarrollo — WinForge

> Documento de referencia rápida para desarrolladores (humanos o IA). Léelo completo
> antes de tocar código: estas reglas evitan los errores más comunes del proyecto.

## Qué es el proyecto (estado actual)

**WinForge** — "optimizador y monitor del sistema" para Windows. Es una app de escritorio
**WinUI 3** (Windows App SDK, .NET) que combina dos cosas:

- **Optimización**: tweaks de rendimiento (39 en `TweakService`), herramientas de reparación
  (SFC/DISM/CHKDSK…), features/fixes estilo WinUtil, paneles clásicos de Windows,
  políticas de Windows Update, limpieza de memoria, DNS, limpieza de inicio.
- **Monitoreo**: sensores (LHM), núcleos de CPU con gráficos y plan de energía,
  estabilidad, memoria, red, teclado, autoclicker, temporizador.

El proyecto **está terminado y funcional**. No hay un roadmap de fases pendiente:
los cambios son evolutivos (nuevas features, correcciones, mejoras).

## Estructura

```
src/
├── WHPO.Core/          # Servicios de lógica SIN dependencia de UI (WPF/WinUI)
│   └── Services/
│       ├── TweakService.cs, RepairService.cs, WinUtilService.cs,
│       ├── WindowsUpdateService.cs, NetworkService.cs, MemoryService.cs,
│       ├── SensorService.cs, CpuPowerService.cs, StabilityService.cs,
│       ├── StartupService.cs, SystemInfoService.cs, KeyboardService.cs,
│       ├── AutoClickerService.cs, ThemeService.cs, SettingsService.cs,
│       ├── LoggingService.cs, NavigationService.cs
│       └── Interfaces/   # Contratos de los servicios
└── WHPO.UI/            # App WinUI 3 (el ejecutable se llama WinForge.exe)
    ├── I18n.cs         # Motor de traducciones (ver abajo)
    ├── Translations.cs # Diccionario de idiomas (ver abajo)
    ├── ThemeApplier.cs # Aplica el tema a la ventana raíz
    ├── ThemeBrushes.cs # Resuelve pinceles del tema EFECTIVO (ver abajo)
    ├── Feedback.cs     # Mensajes de estado/notificaciones (traduce automáticamente)
    ├── Flags.cs        # Banderas dibujadas para el selector de idioma
    ├── MainWindow.xaml(.cs)   # Navbar, selector de idioma, bandeja del sistema
    └── Views/Pages/    # 17 páginas (una por sección del navbar)
```

## Regla de oro 1 — TODA la UI tiene que estar traducida

La app soporta **5 idiomas**: `es-AR` (fuente), `en-US`, `pt-BR`, `de-DE`, `fr-FR`.
El idioma se elige desde el navbar (bandera + código, ej. `🇦🇷 es-AR`) y se guarda en
settings (`app.language`).

### Cómo funciona el sistema

- **El diccionario (`Translations.cs`) está claveado por el texto fuente en español.**
  Cada entrada es una tupla con la traducción en los otros 4 idiomas.
  ```csharp
  ["Aplicar cambios"] = ("Apply changes", "Aplicar alterações", "Änderungen übernehmen", "Appliquer les modifications"),
  ```
- **`I18n.T("...")`** traduce un string. **`I18n.T(plantilla, args)`** traduce una
  plantilla con marcadores (`{0}`, `{1}`…) y la formatea; la traducción puede reordenar
  los marcadores libremente.
- **Fallback en cadena**: idioma elegido → `en-US` → español. Si falta la clave, se
  muestra el español y **la UI nunca se rompe**.
- **XAML estático**: el motor recorre el árbol visual (`I18n.ApplyToVisualTree`) y
  traduce `Text`/`Content`/`PlaceholderText`/`Header` automáticamente, recordando el
  texto original por elemento (no pisa textos dinámicos como estados o contadores).
- **`Feedback`** traduce automáticamente los mensajes que recibe.

### Qué hacer SIEMPRE al agregar texto visible

1. **En XAML**: escribí el texto en español como siempre y agregá la clave a
   `Translations.cs`. El walker lo traduce solo.
2. **En code-behind**: envolvé el string en `I18n.T("...")`. Si tiene variables, usá
   la plantilla con marcadores: `I18n.T("Uso {0}% · {1}°C", uso, temp)`.
3. **Si la página construye contenido en código** (cards, botones, badges): suscribite a
   `I18n.LanguageChanged` para reconstruir/traducir al cambiar de idioma. Ejemplos ya
   hechos: `HerramientasPage`, `ReparacionPage`, `NucleosPage`. Si la página no usa
   caché de navegación, **desuscribite** para no filtrar memoria.
4. **Nunca** pongas un string visible solo en español sin clave. La regla práctica:
   "si un humano lo puede leer en pantalla, tiene que estar en el diccionario".
5. **Los datos del sistema no se traducen**: nombres de CPU, adaptadores, planes de
   energía, sensores de LHM — los pone Windows y son intocables.

### Agregar un idioma nuevo

1. Agregar el código a `I18n.Languages`.
2. Agregar una columna (posición) en cada tupla de `Translations.cs`.
3. Dibujar la bandera en `Flags.cs` y agregar el ítem al menú del navbar.

## Regla de oro 2 — Toda la UI tiene que reaccionar al tema seleccionado

La app tiene tema **Claro / Oscuro / Sistema** (`AppTheme`), elegible en Configuración
y guardado en settings (`AppTheme`). El tema se aplica con `RequestedTheme` en el root
de la ventana (`ThemeApplier`) y dispara `ThemeChanged` (`ThemeService`).

### Cómo funciona

- **En XAML**: usá `{ThemeResource Clave}` siempre. Los `ThemeDictionaries` de la app
  (`Light`/`Dark`) ya tienen todos los pinceles (cards, fondo, texto atenuado, grids de
  gráficos, grupos de sensores…).
- **En code-behind** (elementos creados en código): **NUNCA** usés
  `App.Current.Resources["Clave"]` — esa búsqueda resuelve con el tema del **sistema**,
  no el de la ventana, y las cards creadas en código quedan con los colores equivocados
  (el bug clásico de este proyecto: "al pasar a claro, las cards siguen oscuras").
  Usá **`ThemeBrushes.Get("Clave")`**, que resuelve con el diccionario del tema
  **efectivo** de la ventana.
- **Si construís contenido en código con colores**: suscribite a `ThemeChanged` (o
  reconstruí al entrar a la página) para re-aplicar los pinceles cuando cambia el tema.

### Qué hacer SIEMPRE

1. En XAML → `{ThemeResource ...}`. En código → `ThemeBrushes.Get(...)`.
2. **Nunca** hardcodear colores (ni en XAML ni en código).
3. Si una página crea elementos visuales en code-behind, verificar en **Claro** y en
   **Oscuro** que se vean bien en ambos.

## Check-list antes de dar por terminado un cambio

- [ ] Ningún string visible sin pasar por el diccionario / `I18n.T()`.
- [ ] Las páginas con contenido en código reaccionan a `I18n.LanguageChanged` (y se
      desuscriben si no usan caché).
- [ ] Ningún color hardcodeado; pinceles vía `{ThemeResource}` o `ThemeBrushes.Get()`.
- [ ] Las cards creadas en código se ven bien en claro y en oscuro.
- [ ] `dotnet build src/WHPO.UI/WHPO.UI.csproj` compila sin errores.
- [ ] Cerrar la app antes de compilar si está corriendo (bloquea los archivos de salida
      y el build falla en el paso de copiado).
