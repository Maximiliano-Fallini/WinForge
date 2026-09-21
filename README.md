
<p align="center">
  <img src="src/WHPO.UI/logos/WinForge.png" height="150" alt="Ícono de WinForge"/>
</p>
<h1 align="center">WinForge</h1>

**WinForge** es un **optimizador competitivo para Windows**, pensado para gamers y usuarios avanzados que quieren sacar el máximo provecho de su equipo. Una sola app para monitorear tu PC, optimizarla al jugar, gestionar tus juegos y ajustar Windows a tu medida.

<p align="center">
   <b>100% Gratuito</b> — todas las funciones de optimización son completamente gratis, sin límites.
</p>

<h2 align="center">🌐 Multilenguaje</h2>

<p align="center">
  La app se adapta al idioma de tu Windows (o lo elegís vos desde el selector de banderas del navbar) — se traduce al instante, sin reiniciar.<br/>
  <code>es-AR</code>&ensp;<code>en-US</code>&ensp;<code>pt-BR</code>&ensp;<code>de-DE</code>&ensp;<code>fr-FR</code><br/><br/>
  <img src="assets/flags/ar.png" height="26" alt="es-AR" title="Español (es-AR)" hspace="8"/>&nbsp;&nbsp;
  <img src="assets/flags/us.png" height="26" alt="en-US" title="English (en-US)" hspace="8"/>&nbsp;&nbsp;
  <img src="assets/flags/br.png" height="26" alt="pt-BR" title="Português (pt-BR)" hspace="8"/>&nbsp;&nbsp;
  <img src="assets/flags/de.png" height="26" alt="de-DE" title="Deutsch (de-DE)" hspace="8"/>&nbsp;&nbsp;
  <img src="assets/flags/fr.png" height="26" alt="fr-FR" title="Français (fr-FR)" hspace="8"/>
</p>

## ✨ ¿Qué hace?

### 🎮 Optimización automática al iniciar un juego
Cuando arrancás un juego, WinForge aplica y restaura automáticamente (nada queda tocado al cerrar):

- **Procesos en segundo plano** a prioridad baja + modo eficiencia (EcoQoS) — lista configurable de procesos del sistema y apps. Menos ruido en el sistema, más rendimiento para tu juego.
- **Pausa de Windows Update** y servicios de mantenimiento/telemetría mientras jugás (wuauserv, UsoSvc, BITS, WSearch, SysMain…), reiniciándolos al cerrar.
- **Plan de energía global** (o por juego) que se activa al iniciar y se revierte al cerrar.
- **Notificaciones silenciadas** durante la partida (modo "solo alarmas"), restauradas al salir.

### 🕹️ Biblioteca de juegos con reglas por juego
- Detecta juegos instalados desde **Steam, Epic, Battle.net, GOG, Xbox, Riot** y más.
- **Detección de emuladores**: RetroArch, Dolphin, PCSX2, RPCS3, Cemu, MAME, PPSSPP, Ryujinx, Lime3DS, Mesen, bsnes y FBNeo — cada uno aparece como una card lanzable en la biblioteca.
- **Reglas por juego**: prioridad de CPU, afinidad de núcleos, prioridad de GPU, prioridad de E/S y plan de energía — con alcance *"Actual"* (solo la apertura actual) o *"Siempre"* (persistente).
- **Detección inteligente**: eventos WMI (cero polling) + detector de ventana fullscreen en primer plano para juegos fuera de la biblioteca (itch.io, DRM-free…).
- **Lanzamiento desde la bandeja**: click derecho en el ícono → elegí un favorito y el juego arranca con la lógica correcta para cada launcher.

### 🧩 Workshop — instalá solo lo que usás
- La app arranca con las 5 pestañas esenciales: **Sistema, Red, Núcleos y Plan de energía, Biblioteca de juegos y Workshop** (más Configuración).
- El resto de las funciones viven ahora en el **Workshop** como componentes: instalás con un click y la pestaña aparece en el navbar al instante; podés desinstalarlas cuando quieras y reinstalarlas sin perder tu configuración. Reordená las pestañas arrastrándolas.
- Catálogo de componentes **descargables** desde GitHub con verificación de integridad (SHA-256), actualización y desinstalación real desde la app.

**Componentes disponibles en el Workshop:**

| Componente | Qué hace |
|---|---|
| 🔧 **Control de ventiladores** | Curvas PWM por sensor (CPU/GPU/placa) vía driver PawnIO, con autostart al iniciar sesión |
| 🌐 **TCP / Red avanzado** | Stack TCP estilo TCP Optimizer (Nagle, congestión, ECN, RSS, Fast Open) + **Reglas de Windows** (limitación multimedia, SystemResponsiveness, puertos efímeros, TIME_WAIT, caché, LSO) con presets *Rendimiento* / *Ecológico* y restauración a defaults |
| 🎛️ **Propiedades del adaptador** | Velocidad/duplex, control de flujo, moderación de interrupciones, EEE, Ethernet verde y ahorro de energía — por adaptador, con selector de interfaz |
| 🖥️ **Overlay de métricas** | FPS, 1% low, CPU, GPU, RAM y temperaturas en el juego, customizable y con atajos |
| ⚡ **Overclock USB** | Polling rate de mouses/teclados/mandos vía filtro kernel (SweetLow) |
| 🕒 **Resolución del temporizador** | Timer resolution + keep-alive al minimizar a bandeja |
| ⌨️ **Filtro de teclas / Macros / Autoclicker** | Repetición, macros con hotkey global y clicks automáticos |
| 🧠 **Memoria** | Limpieza inteligente y automática de la caché RAM |
| 📈 **Sensores / Estabilidad / Procesos** | Monitoreo en vivo, test de estrés y gestor de procesos |
| 🧹 **Debloat / Limpieza / Reparación / Windows Update** | Mantenimiento completo del sistema |

### 📊 Monitoreo
- CPU, memoria, red, sensores de temperatura (CPU/GPU), núcleos y uso en vivo.
- **Overlay de métricas en el juego** (FPS, CPU, GPU, RAM, temperaturas) con atajos de teclado.

### 🧰 Herramientas
- **Limpiar memoria caché en RAM** (lista standby) desde la app o la bandeja.
- **Limpiador de caché** de navegadores con soporte para layout Chromium moderno (Opera GX, Edge, Chrome…).
- **Planes de energía**, tweaks y debloat de Windows, reparación del sistema.
- **Teclado**: macros, reasignación y **autoclicker**.
- **Temporizador de apagado**, panel de ventanas, estabilidad y más.

## ⚙️ Requisitos

- **Windows 10/11** (testeado en Windows 11 25H2)

## 📦 Instalación

<p align="center">
  <a href="https://github.com/Maximiliano-Fallini/WinForge/releases">
    <img src="https://img.shields.io/badge/%E2%AC%87_Descargar_WinForge-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Descargar WinForge"/>
  </a>
  <br/>
  <sub>El instalador (MSI) está en la última release.</sub>
  <br/>
  <br/>
  <a href="https://github.com/Maximiliano-Fallini/WinForge/releases">
    <img src="https://img.shields.io/badge/%F0%9F%93%84_Ver_en_Releases-winforge-2ea44f?style=flat-square" alt="Releases"/>
  </a>
  &nbsp;
  <img src="https://img.shields.io/badge/self_contained-no_requiere_.NET-5c5c5c?style=flat-square" alt="Self contained"/>
  &nbsp;
  <img src="https://img.shields.io/badge/ejecutar-como_administrador-e3b341?style=flat-square" alt="Como administrador"/>
  <br/>
  <sub>⭐ Beta — ¿encontraste un problema? <a href="https://github.com/Maximiliano-Fallini/WinForge/issues">Reportalo en Issues</a></sub>
</p>

## ⭐ Apoyá el proyecto

<div align="center">

<b>Todas las funciones de optimización son 100% gratuitas.</b><br/>
Si te gusta WinForge, dejá una ⭐ para mostrar apoyo — es gratis y ayuda muchísimo a que el proyecto siga creciendo.

<a href="https://github.com/Maximiliano-Fallini/WinForge/stargazers">
    <img src="https://img.shields.io/badge/%E2%AD%90_Dej%C3%A1_una_estrella-ffdd00?style=for-the-badge&logo=github&logoColor=black" alt="Dejá una estrella en GitHub"/>
  </a>

</div>

> [!WARNING]
> Esta aplicación **no cuenta con firma digital** y, por diseño, **modifica el sistema operativo ejecutándose como administrador** (limpieza de archivos, gestión de procesos, tweaks del sistema,etc). Por estos motivos, algunos antivirus pueden detectarla como un **falso positivo**.
>
> Si confiás en el proyecto, agregá una excepción en tu antivirus. Podés verificar la integridad del instalador comparando el SHA-256 publicado en cada release.

---

*WinForge — el optimizador competitivo para tu Windows. Rendimiento y control, al máximo.*
