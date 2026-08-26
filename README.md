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

## ⚙️ Requisitos

- **Windows 10/11** (probado en Windows 11 25H2)
- Ejecutar **como administrador** (necesario para optimización de procesos, servicios y planes de energía)

## 📦 Instalación

<p align="center">
  <a href="https://github.com/Maximiliano-Fallini/WinForge/releases/download/v0.1.5/WinForge-0.1.5.msi">
    <img src="https://img.shields.io/badge/%E2%AC%87_Descargar_WinForge-v0.1.5_MSI_~87_MB-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Descargar WinForge 0.1.5 MSI"/>
  </a>
  <br/>
  <sub>(26/8/2026 UTC)</sub>
  <br/>
  <a href="https://github.com/Maximiliano-Fallini/WinForge/releases/tag/v0.1.5">
    <img src="https://img.shields.io/badge/%F0%9F%93%84_Ver_en_Releases-v0.1.5_(pre_release)-2ea44f?style=flat-square" alt="Releases"/>
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

## 🧱 Tecnología

- **WinUI 3** + **Windows App SDK** (.NET 9) — interfaz nativa moderna
- **WMI** — detección de procesos/juegos por eventos (cero polling)
- **LibreHardwareMonitor** — sensores de temperatura
- **WiX v7** — instalador MSI

---

*WinForge — el optimizador competitivo para tu Windows. Rendimiento y control, al máximo.*
