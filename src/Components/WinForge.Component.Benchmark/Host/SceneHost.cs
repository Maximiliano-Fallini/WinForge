using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using WinForge.Component.Benchmark.Graphics;
using WinForge.Component.Benchmark.Metrics;
using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark.Host;

/// <summary>
/// Corre una escena de principio a fin y devuelve el informe. Todo el trabajo gráfico pasa en
/// un hilo PROPIO (ventana, dispositivo, bucle de frames): así la UI de la app sigue viva
/// (progreso, botón de detener) y, sobre todo, la medición no compite con el hilo de UI que
/// está pintando la página.
///
/// La corrida es de DURACIÓN FIJA EN SEGUNDOS (modelo 3DMark, no de frames): la escena avanza
/// por su recorrido y dos corridas de la misma duración hacen el mismo viaje. El bucle no usa
/// vsync ni límite de FPS salvo que se pida: se mide lo que la máquina da.
/// </summary>
internal static class SceneHost
{
    /// <summary>Pedido de corrida. Los valores por defecto salen de la escena, no de acá.</summary>
    internal sealed class RunRequest
    {
        public required GraphicsApi Api { get; init; }
        public required SceneDefinition Scene { get; init; }
        public PresentationMode Presentation { get; init; } = PresentationMode.Windowed;

        /// <summary>Segundos de corrida MEDIDA (después del calentamiento).</summary>
        public int DurationSeconds { get; init; }

        /// <summary>Segundos de calentamiento sin medir (compilado de shaders, caches de driver).</summary>
        public double WarmupSeconds { get; init; }

        public bool VSync { get; init; }
        public int AdapterIndex { get; init; } = -1;

        /// <summary>
        /// Cómo se DIBUJA la escena (sombras y lo que venga): lo que el usuario eligió en la página
        /// antes de arrancar. Viaja tal cual a los cuatro backends y al informe.
        /// </summary>
        public SceneGraphicsOptions Graphics { get; init; } = SceneGraphicsOptions.Default;

        /// <summary>
        /// Preset gráfico elegido (nombre visible, texto fuente en español) o "Personalizado". Viaja al
        /// informe: dos corridas con el mismo preset y las mismas perillas se reconocen de un vistazo.
        /// </summary>
        public string GraphicsPreset { get; init; } = "";

        /// <summary>
        /// Tamaño de la VENTANA de la escena (0 = automático: el que entra en el monitor). Se pide en
        /// modo ventana y se IGNORA en pantalla completa: en bordes o exclusiva manda el monitor, y
        /// "1280×720" ahí adentro no significa nada. Si lo pedido no entra en el área útil de la
        /// pantalla se encaja, y el informe lo dice en vez de recortar la escena en silencio.
        /// </summary>
        public int WindowWidth { get; init; }
        public int WindowHeight { get; init; }

        /// <summary>False para el harness sin ventana visible (verificación headless).</summary>
        public bool ShowWindow { get; init; } = true;

        /// <summary>La franja de métricas sobre la escena NO tiene switch en la app: se dibuja
        /// siempre (la página ya no lo configura). El harness la apaga para no crear ventanas.</summary>
        public bool ShowHud { get; init; } = true;

        public string ComponentVersion { get; init; } = "";
        public string AppVersion { get; init; } = "";

        /// <summary>Lee una muestra de sensores de la app (null si no hay fuente disponible).</summary>
        public Func<SensorSample?>? SensorProvider { get; init; }

        /// <summary>Huella del equipo (CPU, GPU, RAM, SO…).</summary>
        public Dictionary<string, string> Fingerprint { get; init; } = new();
    }

    internal sealed record RunProgress(
        int Frame, int TotalFrames, double ElapsedSeconds, double Fps, double FrameMs, double GpuMs);

    /// <summary>HWND de la ventana de la escena en curso (verificación del probador; 0 si no hay).</summary>
    internal static nint WindowHandleForDiagnostics { get; set; }

    /// <summary>
    /// HWND de la franja de métricas en curso (verificación del probador; 0 si no hay). Sirve para
    /// auditar el z-order: si el HUD queda por DEBAJO de la escena, no se ve aunque esté pintado.
    /// </summary>
    internal static nint HudHandleForDiagnostics => liveHudWindow;

    /// <summary>Corre la escena y devuelve el informe. Bloquea hasta que termina.</summary>
    internal static BenchmarkReport Run(RunRequest request, IProgress<RunProgress>? progress, CancellationToken token)
    {
        BenchmarkReport? report = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { report = RunCore(request, progress, token); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "WinForgeBenchmark"
        };
        thread.Start();
        // Espera CON salidas (ver WaitForRun): el Join() sin límite era el "no se puede
        // cancelar" — un Present colgado del lado del DRIVER no sale nunca de la llamada nativa.
        if (!WaitForRun(thread, request, token))
        {
            failure = new InvalidOperationException(
                "La corrida no respondió dentro del tiempo límite (posible cuelgue del driver): se canceló desde la interfaz.");
        }

        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return report!;
    }

    /// <summary>Margen sobre la duración pedida: cubre el calentamiento real, el compilado de shaders y un driver que se cuelga.</summary>
    private const int RunMarginSeconds = 300;

    /// <summary>Gracia desde que se pide cancelar hasta dar el bucle por colgado (ver <see cref="WaitForRun"/>).</summary>
    private const int CancelGraceSeconds = 2;

    /// <summary>
    /// Piso de frames del calentamiento (ver el bucle de frames): por debajo de esto NO se empieza a
    /// medir, aunque el reloj diga que el calentamiento terminó. La primera vuelta de cada camino
    /// nuevo (el primer DrawInstanced, el primer Present del swapchain, el primer render de la
    /// franja de métricas, el JIT de los métodos del bucle) no es rendimiento sostenido, y en un
    /// arranque lento esos frames caben enteros dentro de los segundos de calentamiento.
    /// </summary>
    private const int WarmupMinimumFrames = 30;

    /// <summary>HWND del HUD en curso (ver <see cref="HideLiveWindows"/>; 0 si no hay).</summary>
    private static nint liveHudWindow;

    /// <summary>
    /// Espera al hilo de la corrida sin quedarse pegado, en tramos, con dos salidas:
    /// una cancelación que el bucle ya no puede ver (el caso del driver colgado dentro de
    /// <c>Present</c>: la UI se libera en 2 s en vez de esperar a que el token llegue solo) y el
    /// tope absoluto de duración + margen, que devuelve false y se informa como falla.
    /// </summary>
    private static bool WaitForRun(Thread thread, RunRequest request, CancellationToken token)
    {
        var limit = TimeSpan.FromSeconds((int)(request.WarmupSeconds + request.DurationSeconds) + RunMarginSeconds);
        var clock = Stopwatch.StartNew();
        DateTime? cancelSeen = null;

        while (!thread.Join(100))
        {
            if (token.IsCancellationRequested)
            {
                cancelSeen ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - cancelSeen.Value).TotalSeconds < CancelGraceSeconds) continue;

                // El bucle no vio el token: está clavado dentro de una llamada del driver. Se
                // libera la UI ya y el hilo queda huérfano (es background: muere con el proceso).
                HideLiveWindows();
                throw new OperationCanceledException(token);
            }
            if (clock.Elapsed < limit) continue;

            HideLiveWindows();
            return false;
        }
        return true;
    }

    /// <summary>
    /// Oculta las ventanas de una corrida colgada. <c>ShowWindow</c> no exige ser el hilo dueño y
    /// es la única salida posible: si el hilo quedó clavado, la ventana seguiría a pantalla
    /// completa mostrando un cuadro congelado que no se puede cerrar.
    /// </summary>
    private static void HideLiveWindows()
    {
        if (WindowHandleForDiagnostics != 0) NativeMethods.ShowWindow(WindowHandleForDiagnostics, NativeMethods.SW_HIDE);
        if (liveHudWindow != 0) NativeMethods.ShowWindow(liveHudWindow, NativeMethods.SW_HIDE);
    }

    private static BenchmarkReport RunCore(RunRequest request, IProgress<RunProgress>? progress, CancellationToken token)
    {
        var report = new BenchmarkReport
        {
            ComponentVersion = request.ComponentVersion,
            AppVersion = request.AppVersion,
            Backend = BackendRegistry.NameOf(request.Api),
            SceneId = request.Scene.Id,
            SceneName = request.Scene.Name,
            Presentation = PresentationLabel(request.Presentation),
            VSync = request.VSync,
            Shadows = request.Graphics.Shadows,
            ShadowMapSize = request.Graphics.ShadowMapSize,
            Environment = request.Graphics.Environment,
            GraphicsPreset = request.GraphicsPreset,
            DurationSeconds = request.DurationSeconds,
            WarmupSeconds = request.WarmupSeconds
        };
        foreach (var pair in request.Fingerprint) report.System[pair.Key] = pair.Value;

        // Lo que la construcción de la escena dejó anotado (modelos o texturas que faltaron y la
        // escena reemplazó): entra al informe como avisos, para que el faltante no quede solo en
        // una lista interna que nadie mira. SOLO los de la escena corrida: los avisos de las
        // otras escenas no incumben a este informe.
        IEnumerable<string> sceneNotes = request.Scene.Id switch
        {
            "neon" => NeonScene.Notes,
            "aero" => AeroAssets.Notes,
            _ => Array.Empty<string>(),
        };
        foreach (var note in sceneNotes) report.Warnings.Add(BenchmarkNote.Of(note));
        if (request.Scene.Materials.Overflowed)
        {
            report.Warnings.Add(BenchmarkNote.Of(
                "El atlas de materiales se quedó sin ranuras: hay mallas dibujadas con el material neutro, sin sus texturas."));
        }

        IGraphicsBackend? backend = null;
        Win32Window? sceneWindow = null;
        MetricsHud? hud = null;
        nint sceneHandle = 0;
        var samples = new List<FrameSample>(4096);
        var sensorSamples = new List<SensorSample>();
        string? abortReason = null;
        double measuredStart = -1;        // reloj del primer frame medido (-1 = todavía calentando)
        bool resizedDuringRun = false;    // se recreó el swapchain (y la profundidad) en plena corrida

        // Estado vivo que consume el HUD (se actualiza desde el bucle).
        int liveFrame = 0;
        double liveFps = 0, liveFrameMs = 0, liveGpuMs = 0, liveElapsed = 0;
        SensorSample? liveSensor = null;

        HudData BuildHudData() => new(
            request.Scene.Name,
            liveFrame,
            request.DurationSeconds,
            liveElapsed,
            liveFps,
            liveFrameMs,
            liveGpuMs,
            liveSensor?.GpuUsagePercent ?? 0,
            liveSensor?.GpuTemperatureCelsius ?? 0,
            liveSensor?.GpuClockMHz ?? 0,
            liveSensor?.GpuWatts ?? 0,
            (liveSensor?.VramUsedMb ?? 0) / 1024.0,
            (liveSensor?.VramTotalMb ?? 0) / 1024.0,
            liveSensor?.CpuUsagePercent ?? 0,
            liveSensor?.CpuTemperatureCelsius ?? 0,
            liveSensor?.CpuClockMHz ?? 0,
            liveSensor?.CpuWatts ?? 0,
            liveSensor?.RamUsagePercent ?? 0);

        try
        {
            Win32Window.KeepAwake();

            // La escena sale en el monitor donde está el cursor, no siempre en el primario: si el
            // usuario trabaja en otro monitor, la ventana sin bordes aparecía en la pantalla
            // principal y la corrida se veía como "no pasa nada".
            var monitor = Win32Window.MonitorUnderCursor();
            bool fullscreenStyle = request.Presentation != PresentationMode.Windowed;
            // Tamaño de la ventana: lo pedido en la configuración gráfica o el automático de siempre.
            // Se encaja en el ÁREA ÚTIL (sin la barra de tareas) porque una ventana con bordes del
            // tamaño del monitor entero deja el título y el borde de abajo fuera de la pantalla.
            var (workWidth, workHeight) = Win32Window.WorkAreaUnderCursor();
            bool askedResolution = request.WindowWidth > 0 && request.WindowHeight > 0;
            int windowWidth = fullscreenStyle ? monitor.Width
                : askedResolution ? Math.Min(request.WindowWidth, workWidth)
                : Math.Min(1600, Math.Max(960, monitor.Width - 240));
            int windowHeight = fullscreenStyle ? monitor.Height
                : askedResolution ? Math.Min(request.WindowHeight, workHeight)
                : Math.Min(900, Math.Max(540, monitor.Height - 200));

            // Lo pedido se compara con el TAMAÑO FINAL y no con el monitor: si el encaje recortó la
            // resolución, el informe no puede decir que corrió a la resolución que se pidió.
            if (askedResolution && !fullscreenStyle &&
                (windowWidth != request.WindowWidth || windowHeight != request.WindowHeight))
            {
                // El aviso lleva el CAMINO DE RECUPERACIÓN: la resolución completa de la pantalla se
                // consigue en pantalla completa sin bordes, donde la escena ocupa el monitor entero.
                report.Warnings.Add(BenchmarkNote.Of(
                    "La resolución pedida ({0}×{1}) no entra en el área útil del monitor con la ventana con bordes: la corrida salió en {2}×{3}. Para esa resolución exacta, usá pantalla completa sin bordes.",
                    request.WindowWidth.ToString(), request.WindowHeight.ToString(),
                    windowWidth.ToString(), windowHeight.ToString()));
            }
            int windowX = fullscreenStyle ? monitor.X : monitor.X + Math.Max(0, (monitor.Width - windowWidth) / 2);
            int windowY = fullscreenStyle ? monitor.Y : monitor.Y + Math.Max(0, (monitor.Height - windowHeight) / 2);

            sceneWindow = new Win32Window("", windowWidth, windowHeight,
                borderless: fullscreenStyle, topMost: fullscreenStyle, visible: request.ShowWindow, clickThrough: false,
                x: windowX, y: windowY);
            sceneHandle = sceneWindow.Handle;
            WindowHandleForDiagnostics = sceneHandle;
            sceneWindow.CloseRequested += () => abortReason ??= "Se cerró la ventana de la escena.";
            sceneWindow.KeyPressed += key =>
            {
                if (key == NativeMethods.VK_ESCAPE) abortReason ??= "Cortada con Esc.";
            };

            backend = BackendRegistry.Create(request.Api);
            sceneWindow.RefreshClientSize();
            backend.Initialize(new BackendInitOptions
            {
                WindowHandle = sceneWindow.Handle,
                Width = sceneWindow.ClientWidth,
                Height = sceneWindow.ClientHeight,
                Scene = request.Scene,
                AdapterIndex = request.AdapterIndex,
                VSync = request.VSync,
                Graphics = request.Graphics
            });
            sceneWindow.Resized += (width, height) =>
            {
                try
                {
                    backend!.Resize(width, height);
                    // Recrear el swapchain (y la profundidad) es trabajo de INFRAESTRUCTURA: si
                    // cae dentro de la medición, hay frames de la corrida que no son de la escena
                    // —y el peor frame del informe no se explica solo—. El informe lo dice.
                    if (measuredStart >= 0) resizedDuringRun = true;
                }
                catch { }
            };

            report.AdapterName = backend.AdapterName;
            report.AdapterDetail = backend.AdapterDetail;
            report.Width = sceneWindow.ClientWidth;
            report.Height = sceneWindow.ClientHeight;

            if (request.Presentation == PresentationMode.ExclusiveFullscreen)
            {
                // Hay dos motivos distintos para no tener exclusivo y el informe tiene que decir
                // CUÁL: "la API no lo implementa" no se arregla desde el panel de control de la placa.
                if (backend.SupportsExclusiveFullscreen)
                {
                    backend.SetFullscreen(true);
                    // El respaldo a ventana se informa para CUALQUIER API: si el usuario pidió
                    // exclusivo y no se pudo, el informe no puede decir "pantalla completa exclusiva".
                    if (!backend.ExclusiveFullscreenAccepted)
                    {
                        report.Warnings.Add(BenchmarkNote.Of("El monitor no aceptó la pantalla completa exclusiva: la corrida siguió en ventana."));
                    }
                }
                else
                {
                    report.Warnings.Add(BenchmarkNote.Of("Esta API no tiene pantalla completa exclusiva en este componente: la corrida salió en ventana."));
                }
            }
            if (!backend.HasGpuTiming)
            {
                report.Warnings.Add(BenchmarkNote.Of("Esta API no expone tiempos de GPU: el informe no tiene ms de GPU por frame."));
            }

            // La franja de métricas va SIEMPRE (abajo y centrada sobre la escena): la página no
            // la configura. Solo se omite en el harness sin ventana visible, que no tiene escena.
            //
            // Si el backend la dibuja DENTRO del frame (OverlayInFrame), NO se crea la ventana:
            // con vsync apagado Windows entrega el swapchain por el camino de tearing y saca la
            // ventana de la composición del escritorio, así que una franja en ventana aparte
            // parpadeaba en Direct3D o directamente no se veía en Vulkan.
            bool hudInFrame = backend.OverlayInFrame;
            if (request.ShowHud && request.ShowWindow)
            {
                hud = new MetricsHud(sceneWindow.Handle, BuildHudData, withWindow: !hudInFrame);
                liveHudWindow = hud.Handle;
            }

            // El título arranca con el nombre de la escena y la API (la ventanita sin bordes no
            // tiene barra, así que esto solo se ve en modo ventana, y con métricas en vivo).
            if (request.ShowWindow)
            {
                sceneWindow.SetTitle($"{request.Scene.Name} · {backend.ApiName}");
            }

            // ---- Bucle de frames ----
            // El bucle corre hasta juntar los segundos MEDIDOS pedidos: el calentamiento no recorta
            // la corrida (si arranca más tarde, la corrida se estira).
            var clock = Stopwatch.StartNew();
            double lastPresent = 0;
            double progressTimer = -0.25, hudTimer = -0.25, sensorTimer = -0.25, titleTimer = -0.25;
            int frame = 0;
            int presentedFrames = 0;    // frames presentados desde que arrancó el bucle (calentamiento incluido)
            int warmupFrames = 0;       // frames descartados antes de medir
            long overlayVersion = 0;   // sube cada vez que se rasteriza la franja dentro del frame
            double totalSeconds = request.WarmupSeconds + request.DurationSeconds;
            var watchdog = TimeSpan.FromSeconds(totalSeconds + RunMarginSeconds);

            while (measuredStart < 0 || clock.Elapsed.TotalSeconds - measuredStart < request.DurationSeconds)
            {
                if (token.IsCancellationRequested)
                {
                    abortReason ??= "Cancelada por el usuario.";
                    break;
                }
                if (abortReason != null) break;
                if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_ESCAPE) & 0x8000) != 0)
                {
                    abortReason = "Cortada con Esc.";
                    break;
                }
                if (clock.Elapsed > watchdog)
                {
                    abortReason = "Se superó el límite de tiempo de la corrida.";
                    break;
                }
                if (backend.DeviceLost)
                {
                    abortReason = "El dispositivo gráfico se perdió durante la corrida.";
                    break;
                }

                // El tiempo de GPU disponible AHORA es el del frame anterior (ver D3D11Backend).
                double gpuForPreviousFrame = backend.LastGpuFrameMs;

                // Se mide en tres tramos separados a propósito: trabajo del CPU, entrega del
                // frame y (más abajo) tiempo de GPU. Mezclarlos haría que el informe dijera
                // "CPU" cuando en realidad la placa viene atrasada.
                double now = clock.Elapsed.TotalSeconds;
                double renderStart = now;
                backend.RenderFrame(now);
                double afterRender = clock.Elapsed.TotalSeconds;
                // RenderFrame incluye la espera por la ranura (fence): eso NO es trabajo del CPU,
                // es la GPU viniendo atrasada. El backend lo reporta aparte (LastQueueWaitMs) y
                // acá se descuenta, si no el informe diría "CPU" donde dice "la placa viene atrasada"
                // (en Direct3D 12 esa espera es la mayor parte del frame cuando hay cola).
                double cpuMs = Math.Max(0, (afterRender - renderStart) * 1000.0 - backend.LastQueueWaitMs);

                double presentMs = backend.Present();
                double presentTime = clock.Elapsed.TotalSeconds;
                presentedFrames++;

                // Calentamiento: el frame se dibuja y se presenta igual (shaders, subidas, caches
                // del driver), pero NO entra a la medición. El recorte es por TIEMPO con un piso de
                // FRAMES: si el arranque es lento, 2,5 s pueden ser tres frames y esos tres frames
                // siguen siendo arranque (el piso los descarta igual).
                if (presentedFrames < WarmupMinimumFrames || presentTime < request.WarmupSeconds)
                {
                    warmupFrames++;
                    liveElapsed = 0;
                }
                else if (measuredStart < 0)
                {
                    // Primer frame medido: solo fija la referencia del tramo medido y se descarta
                    // (su intervalo arranca en un present del calentamiento).
                    measuredStart = presentTime;
                    warmupFrames++;
                }
                else
                {
                    samples.Add(new FrameSample(
                        (presentTime - lastPresent) * 1000.0,
                        gpuForPreviousFrame,
                        cpuMs,
                        presentMs,
                        backend.LastQueueWaitMs));
                    frame++;
                    liveFrame = frame;
                    liveGpuMs = gpuForPreviousFrame;
                    liveFrameMs = samples[^1].FrameMs;
                    liveFps = liveFrameMs > 0 ? 1000.0 / liveFrameMs : 0;
                    liveElapsed = presentTime - measuredStart;
                }
                lastPresent = presentTime;

                Win32Window.PumpMessages();

                if (request.SensorProvider != null && presentTime - sensorTimer >= 0.25)
                {
                    sensorTimer = presentTime;
                    var sample = request.SensorProvider();
                    if (sample.HasValue)
                    {
                        liveSensor = sample;
                        sensorSamples.Add(sample.Value);
                    }
                }

                if (hud != null && presentTime - hudTimer >= 0.25)
                {
                    hudTimer = presentTime;
                    if (hudInFrame)
                    {
                        // DENTRO del frame: se rasteriza la franja y se la entrega al backend.
                        // El backend la dibuja en CADA frame (el quad); el buffer solo se
                        // re-copia cuando cambian los números (la versión sube).
                        overlayVersion++;
                        var overlay = hud.RenderOverlay(
                            sceneWindow.ClientWidth, sceneWindow.ClientHeight, overlayVersion);
                        backend.SetOverlay(overlay);
                    }
                    else
                    {
                        hud.Refresh();
                    }
                }

                // El título de la ventana lleva las mismas métricas que el HUD: en modo ventana
                // se ven sin depender de que la franja esté por encima. Y el HUD sigue a la
                // ventana por si la mueven (en pantalla completa no hace falta).
                if (request.ShowWindow && presentTime - titleTimer >= 0.5)
                {
                    titleTimer = presentTime;
                    sceneWindow.SetTitle(
                        $"{liveFps:F0} FPS · {liveFrameMs:F2} ms · GPU {liveGpuMs:F2} ms · " +
                        $"{liveElapsed:F0}/{request.DurationSeconds:F0} s · Esc para cortar");
                    if (hud != null && !hudInFrame)
                    {
                        // En ventana la franja sigue a la escena; en pantalla completa la escena
                        // no se mueve y lo que puede cambiar es el orden: otra ventana topmost
                        // creada después (otro overlay, una notificación) tapa la franja sin que
                        // esté rota, y repetir el TOPMOST la devuelve al frente.
                        if (fullscreenStyle) hud.RestickTopMost();
                        else hud.Position(sceneWindow.Handle);
                    }
                }

                if (progress != null && presentTime - progressTimer >= 0.25)
                {
                    progressTimer = presentTime;
                    progress.Report(new RunProgress(
                        frame, request.DurationSeconds, liveElapsed, liveFps, liveFrameMs, liveGpuMs));
                }
            }

            // ---- Cierre del informe ----
            // El último frame no tiene muestra de GPU (su timing se lee al frame siguiente que
            // ya no existe): se recorta para que GPU y frame queden alineados.
            if (samples.Count > 0 && samples[^1].GpuMs == 0)
                samples.RemoveAt(samples.Count - 1);

            // "Completa" es haber juntado los segundos MEDIDOS: con el piso de frames del
            // calentamiento, el reloj desde el arranque del bucle ya no es la referencia.
            bool measuredRequestedTime = measuredStart >= 0 &&
                clock.Elapsed.TotalSeconds - measuredStart >= request.DurationSeconds;
            report.Completed = abortReason == null && measuredRequestedTime;
            if (abortReason != null) report.AbortReason = abortReason;
            // El calentamiento REAL (puede ser más largo que el pedido si el arranque fue lento) se
            // guarda en el informe: es lo que hace auditable que la carga de shaders y las subidas
            // de buffers no estén dentro de los números.
            report.WarmupFrames = warmupFrames;
            report.WarmupElapsedSeconds = measuredStart >= 0 ? measuredStart : clock.Elapsed.TotalSeconds;
            // El warmup ya se descartó al volcar las muestras: acá entra 0 (la firma queda para
            // la verificación estadística, que sí prueba el recorte explícito).
            report.Stats = FrameStats.Compute(samples, request.Scene.FrameBudgetMs, 0);
            report.Sensors = SensorSummary.Compute(sensorSamples);

            if (request.SensorProvider == null)
            {
                report.Warnings.Add(BenchmarkNote.Of("No se pudieron leer los sensores de la app: el informe no trae temperaturas, frecuencias ni potencias."));
            }
            else if (!report.Sensors.HasData)
            {
                report.Warnings.Add(BenchmarkNote.Of("Los sensores de la app no reportaron datos durante la corrida."));
            }
            if (request.VSync)
            {
                report.Warnings.Add(BenchmarkNote.Of("Se presentó con vsync: el FPS queda topeado por la frecuencia del monitor."));
            }
            if (resizedDuringRun)
            {
                report.Warnings.Add(BenchmarkNote.Of("La ventana cambió de tamaño durante la corrida: se recreó el swapchain y hay frames de la medición que no son de la escena."));
            }
            if (report.Completed && report.Stats.DurationSeconds < request.DurationSeconds * 0.9)
            {
                report.Warnings.Add(BenchmarkNote.Of("La corrida midió menos de lo pedido: {0} s de {1} s.",
                    ((int)Math.Round(report.Stats.DurationSeconds)).ToString(), request.DurationSeconds.ToString()));
            }
            if (report.Stats.GpuSamples > 0 && report.Stats.GpuSamples < report.Stats.Frames / 2)
            {
                report.Warnings.Add(BenchmarkNote.Of("Pocas muestras de tiempo de GPU: el driver puede no estar reportando timestamps."));
            }
            else if (report.Stats.GpuSamples == 0 && backend?.HasGpuTiming == true)
            {
                // 0 muestras NO es "la placa estuvo ociosa": es "el driver no devolvió el dato".
                // Mostrar 0,000 ms como si fuera una medición es un número que miente, y el aviso
                // de "pocas muestras" no cubre el cero (ahí el informe parecería una placa sin uso).
                report.Warnings.Add(BenchmarkNote.Of("El driver no devolvió ningún tiempo de GPU en esta corrida: el informe no puede decir cuánto trabajó la placa."));
            }

            return report;
        }
        finally
        {
            var hudHandle = hud?.Handle ?? 0;   // antes de disponer: Dispose deja el HWND en 0
            // Orden: HUD, backend (dispositivo/swapchain) y por último la ventana.
            try { hud?.Dispose(); } catch { }
            try { backend?.Dispose(); } catch { }
            try { sceneWindow?.Dispose(); } catch { }
            // Solo se limpia si sigue siendo la ventana de ESTA corrida: una corrida huérfana que
            // despierte más tarde no puede borrar los HWND de la corrida nueva.
            if (WindowHandleForDiagnostics == sceneHandle) WindowHandleForDiagnostics = 0;
            if (liveHudWindow == hudHandle) liveHudWindow = 0;
            Win32Window.ReleaseAwake();
        }
    }

    private static string PresentationLabel(PresentationMode mode) => mode switch
    {
        PresentationMode.Windowed => "Ventana",
        PresentationMode.BorderlessFullscreen => "Pantalla completa sin bordes",
        PresentationMode.ExclusiveFullscreen => "Pantalla completa exclusiva",
        _ => mode.ToString()
    };
}
