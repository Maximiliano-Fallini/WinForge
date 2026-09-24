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

        /// <summary>False para el harness sin ventana visible (verificación headless).</summary>
        public bool ShowWindow { get; init; } = true;
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
        // El bucle respeta el token y tiene su propio límite de tiempo: si algo se cuelga del lado
        // del driver, el join no se queda esperando para siempre (el watchdog está en el bucle).
        thread.Join();

        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return report!;
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
            DurationSeconds = request.DurationSeconds,
            WarmupSeconds = request.WarmupSeconds
        };
        foreach (var pair in request.Fingerprint) report.System[pair.Key] = pair.Value;

        IGraphicsBackend? backend = null;
        Win32Window? sceneWindow = null;
        MetricsHud? hud = null;
        var samples = new List<FrameSample>(4096);
        var sensorSamples = new List<SensorSample>();
        string? abortReason = null;

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

            var (screenWidth, screenHeight) = Win32Window.PrimaryScreenSize();
            bool fullscreenStyle = request.Presentation != PresentationMode.Windowed;
            int windowWidth = fullscreenStyle ? screenWidth : Math.Min(1600, Math.Max(960, screenWidth - 240));
            int windowHeight = fullscreenStyle ? screenHeight : Math.Min(900, Math.Max(540, screenHeight - 200));

            sceneWindow = new Win32Window("", windowWidth, windowHeight,
                borderless: fullscreenStyle, topMost: fullscreenStyle, visible: request.ShowWindow, clickThrough: false);
            WindowHandleForDiagnostics = sceneWindow.Handle;
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
                VSync = request.VSync
            });
            sceneWindow.Resized += (width, height) =>
            {
                try { backend!.Resize(width, height); } catch { }
            };

            report.AdapterName = backend.AdapterName;
            report.AdapterDetail = backend.AdapterDetail;
            report.Width = sceneWindow.ClientWidth;
            report.Height = sceneWindow.ClientHeight;

            if (request.Presentation == PresentationMode.ExclusiveFullscreen)
            {
                backend.SetFullscreen(true);
                if (backend is D3D11Backend d3d11 && !d3d11.ExclusiveFullscreenAccepted)
                {
                    report.Warnings.Add(BenchmarkNote.Of("El monitor no aceptó la pantalla completa exclusiva: la corrida siguió en ventana."));
                }
            }
            if (!backend.HasGpuTiming)
            {
                report.Warnings.Add(BenchmarkNote.Of("Esta API no expone tiempos de GPU: el informe no tiene ms de GPU por frame."));
            }

            if (request.ShowHud && request.ShowWindow)
            {
                hud = new MetricsHud(sceneWindow.Handle, sceneWindow.ClientWidth, BuildHudData);
            }

            // El título arranca con el nombre de la escena y la API (la ventanita sin bordes no
            // tiene barra, así que esto solo se ve en modo ventana, y con métricas en vivo).
            if (request.ShowWindow)
            {
                sceneWindow.SetTitle($"{request.Scene.Name} · {backend.ApiName}");
            }

            // ---- Bucle de frames ----
            var clock = Stopwatch.StartNew();
            double lastPresent = 0;
            double progressTimer = -0.25, hudTimer = -0.25, sensorTimer = -0.25, titleTimer = -0.25;
            int frame = 0;
            double totalSeconds = request.WarmupSeconds + request.DurationSeconds;
            var watchdog = TimeSpan.FromSeconds(totalSeconds + 300);

            while (clock.Elapsed.TotalSeconds < totalSeconds)
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

                // Calentamiento: el frame se dibuja y se presenta igual (shaders, caches,
                // buffers), pero NO entra a la medición.
                if (presentTime >= request.WarmupSeconds)
                {
                    if (frame > 0) samples.Add(new FrameSample(
                        (presentTime - lastPresent) * 1000.0,
                        gpuForPreviousFrame,
                        cpuMs,
                        presentMs,
                        backend.LastQueueWaitMs));
                    frame++;
                    liveFrame = frame;
                    liveGpuMs = gpuForPreviousFrame;
                    if (samples.Count > 0)
                    {
                        liveFrameMs = samples[^1].FrameMs;
                        liveFps = liveFrameMs > 0 ? 1000.0 / liveFrameMs : 0;
                    }
                }
                lastPresent = presentTime;
                liveElapsed = Math.Max(0, presentTime - request.WarmupSeconds);

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
                    hud.Refresh();
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
                    if (hud != null && !fullscreenStyle) hud.Position(sceneWindow.Handle);
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

            report.Completed = abortReason == null && clock.Elapsed.TotalSeconds >= totalSeconds;
            if (abortReason != null) report.AbortReason = abortReason;
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
            if (report.Completed && report.Stats.DurationSeconds < request.DurationSeconds * 0.9)
            {
                report.Warnings.Add(BenchmarkNote.Of("La corrida midió menos de lo pedido: {0} s de {1} s.",
                    ((int)Math.Round(report.Stats.DurationSeconds)).ToString(), request.DurationSeconds.ToString()));
            }
            if (report.Stats.GpuSamples > 0 && report.Stats.GpuSamples < report.Stats.Frames / 2)
            {
                report.Warnings.Add(BenchmarkNote.Of("Pocas muestras de tiempo de GPU: el driver puede no estar reportando timestamps."));
            }

            return report;
        }
        finally
        {
            // Orden: HUD, backend (dispositivo/swapchain) y por último la ventana.
            try { hud?.Dispose(); } catch { }
            try { backend?.Dispose(); } catch { }
            try { sceneWindow?.Dispose(); } catch { }
            WindowHandleForDiagnostics = 0;
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
