<p align="center">
  <img src="src/WHPO.UI/logos/WinForge.png" alt="Ícono de WinForge" width="120" />
</p>

# <p align="center">🛠️ WinForge</p>

**WinForge** es un **optimizador competitivo para Windows**, pensado para gamers y usuarios avanzados que quieren sacar el máximo provecho de su equipo. Una sola app para monitorear tu PC, optimizarla al jugar, gestionar tus juegos y ajustar Windows a tu medida — **en tu idioma**.

## ✨ ¿Qué hace?

### 🎮 Optimización automática al iniciar un juego
Cuando arrancás un juego, WinForge aplica y restaura automáticamente (nada queda tocado al cerrar):

- **Procesos en segundo plano** a prioridad baja + modo eficiencia (EcoQoS) — lista configurable de procesos del sistema y apps. Menos ruido en el sistema, más rendimiento para tu juego.
- **Pausa de Windows Update** y servicios de mantenimiento/telemetría mientras jugás (wuauserv, UsoSvc, BITS, WSearch, SysMain…), reiniciándolos al cerrar.
- **Plan de energía global** (o por juego) que se activa al iniciar y se revierte al cerrar.
- **Notificaciones silenciadas** durante la partida (modo "solo alarmas"), restauradas al salir.

### 🕹️ Biblioteca de juegos con reglas por juego
- Detecta juegos instalados desde **Steam, Epic, Battle.net, GOG, Xbox, Riot** y más.
- **Reglas por juego**: prioridad de CPU, afinidad de núcleos, prioridad de GPU, prioridad de E/S y plan de energía — con alcance *"Actual"* (solo la apertura actual) o *"Siempre"* (persistente).
- **Detección inteligente**: eventos WMI (cero polling) + detector de ventana fullscreen en primer plano para juegos fuera de la biblioteca (emuladores, itch.io, DRM-free…).
- **Lanzamiento desde la bandeja**: click derecho en el ícono → elegí un favorito y el juego arranca con la lógica correcta para cada launcher.

### 📊 Monitoreo
- CPU, memoria, red, sensores de temperatura (CPU/GPU), núcleos y uso en vivo.
- **Overlay de métricas en el juego** (FPS, CPU, GPU, RAM, temperaturas) con atajos de teclado.

### 🧰 Herramientas
- **Limpiar memoria caché en RAM** (lista standby) desde la app o la bandeja.
- **Planes de energía**, tweaks y debloat de Windows, reparación del sistema.
- **Teclado**: macros, reasignación y **autoclicker**.
- **Temporizador de apagado**, panel de ventanas, estabilidad y más.

### 🌍 Multilenguaje
La app se adapta al idioma de tu Windows (o lo elegís vos desde el selector de banderas del navbar) — se traduce al instante, sin reiniciar:

| 🇦🇷 | 🇺🇸 | 🇧🇷 | 🇩🇪 | 🇫🇷 |
|:-:|:-:|:-:|:-:|:-:|

## ⚙️ Requisitos

- **Windows 10/11** (probado en Windows 11 24H2)
- Ejecutar **como administrador** (necesario para optimización de procesos, servicios y planes de energía)
- [.NET SDK 9](https://dotnet.microsoft.com/download/dotnet/9.0) para compilar

## 📦 Instalación

### Instalador MSI
El instalador (`WinForge-0.1.0.msi`, ~85 MB, self-contained — no requiere .NET instalado) se genera localmente con `installer/build-installer.ps1` (requiere [WiX v7](https://github.com/wixtoolset/wix) en `.tools/wix`):

```powershell
powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1
```

> Nota: `installer/` y `.tools/` están fuera del repositorio (ver `.gitignore`): el instalador es un artefacto local.

### Compilar desde el código
```powershell
dotnet build src/WHPO.UI/WHPO.UI.csproj
```
El ejecutable queda en `src/bin/Debug/WHPO.Debug/WinForge.exe`.

## 🗂️ Estructura del proyecto

```
src/
├── WHPO.Core/       # Lógica y servicios: procesos, juegos, energía, sensores, tweaks, boost
└── WHPO.UI/         # Interfaz WinUI 3: páginas, bandeja del sistema, overlay, traducciones
installer/           # Fuentes WiX + script de build del MSI (local, fuera del repo)
```

## 🧱 Tecnología

- **WinUI 3** + **Windows App SDK** (.NET 9) — interfaz nativa moderna
- **WMI** — detección de procesos/juegos por eventos (cero polling)
- **LibreHardwareMonitor** — sensores de temperatura
- **WiX v7** — instalador MSI

---

*WinForge — el optimizador competitivo para tu Windows. Rendimiento y control, al máximo.*
