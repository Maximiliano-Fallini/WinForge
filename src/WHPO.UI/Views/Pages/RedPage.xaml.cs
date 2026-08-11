using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Controls;

namespace WHPO_UI.Views.Pages;

public sealed partial class RedPage : Page
{
    private readonly INetworkService _networkService;
    private readonly ISystemInfoService _systemInfoService;
    private readonly ILoggingService _loggingService;
    private bool _dataLoaded;

    // Las cards creadas en código (DNS, adaptadores) se mantienen oscuras en ambos temas
    // (paneles oscuros), por eso su texto lleva un claro explícito y siempre legible.
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush LightTextBrush = new(Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xED));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush CardBrush = new(Windows.UI.Color.FromArgb(255, 0x26, 0x2A, 0x31));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(255, 138, 180, 248));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush MutedTextBrush = new(Windows.UI.Color.FromArgb(255, 180, 180, 180));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush LatencyErrorBrush = new(Windows.UI.Color.FromArgb(255, 255, 100, 100));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SuccessBrush = new(Windows.UI.Color.FromArgb(255, 106, 200, 133));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(255, 255, 193, 7));

    // Color del anillo de tiempo del packet loss test (azul)
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush TimeRingBrush = new(Windows.UI.Color.FromArgb(255, 138, 180, 248));

    // Lista de proveedores DNS preestablecidos
    private static readonly List<DnsPreset> DnsPresets = new()
    {
        new DnsPreset("Personalizado", "", ""),
        new DnsPreset("Google", "8.8.8.8", "8.8.4.4"),
        new DnsPreset("Cloudflare", "1.1.1.1", "1.0.0.1"),
        new DnsPreset("Quad9", "9.9.9.9", "149.112.112.112"),
        new DnsPreset("OpenDNS", "208.67.222.222", "208.67.220.220"),
        new DnsPreset("AdGuard DNS", "94.140.14.14", "94.140.15.15"),
        new DnsPreset("NextDNS", "45.90.28.167", "45.90.30.167"),
        new DnsPreset("Comodo Secure DNS", "8.26.56.26", "8.20.247.20"),
        new DnsPreset("CleanBrowsing Family", "185.228.168.168", "185.228.169.168"),
        new DnsPreset("CleanBrowsing Adult", "185.228.168.10", "185.228.169.11"),
        new DnsPreset("DNS.WATCH", "84.200.69.80", "84.200.70.40"),
        new DnsPreset("Yandex DNS", "77.88.8.8", "77.88.8.1"),
        new DnsPreset("Verisign DNS", "64.6.64.6", "64.6.65.6"),
        new DnsPreset("Neustar DNS", "156.154.70.5", "156.154.71.5"),
        new DnsPreset("UncensoredDNS", "91.239.100.100", "89.233.43.71"),
        new DnsPreset("Hurricane Electric", "74.82.42.42", ""),
        new DnsPreset("Xfinity", "75.75.75.75", "75.75.76.76"),
        new DnsPreset("Norton ConnectSafe", "199.85.126.10", "199.85.127.10"),
        new DnsPreset("DNS de fábrica (router)", "", ""),
    };

    // Servidores para packet loss test (como packetlosstest.com)
    private static readonly List<PacketLossServer> PacketLossServers = new()
    {
        new PacketLossServer("Cloudflare", "1.1.1.1"),
        new PacketLossServer("Google", "8.8.8.8"),
        new PacketLossServer("Quad9", "9.9.9.9"),
        new PacketLossServer("OpenDNS", "208.67.222.222"),
        new PacketLossServer("AdGuard DNS", "94.140.14.14"),
        new PacketLossServer("Verizon", "4.2.2.2"),
        new PacketLossServer("Level3", "4.2.2.1"),
        new PacketLossServer("Comcast", "75.75.75.75"),
        new PacketLossServer("Facebook", "157.240.22.35"),
        new PacketLossServer("Amazon", "176.32.98.166"),
        new PacketLossServer("Microsoft", "13.107.42.14"),
        new PacketLossServer("Google Alt", "8.8.4.4"),
    };

    private string _factoryPrimaryDns = "";
    private string _factorySecondaryDns = "";
    // DNS actual del sistema (lo que muestra la opción "Personalizado").
    // Se actualiza al aplicar un DNS; _factory* queda como snapshot de carga para "DNS de fábrica (router)".
    private string _currentPrimaryDns = "";
    private string _currentSecondaryDns = "";
    private readonly List<string> _manualDnsList = new();
    private System.Threading.CancellationTokenSource? _packetLossCts;
    private bool _packetLossRunning;
    private RingGauge? _sentGauge;
    private RingGauge? _timeGauge;
    private RingGauge? _receivedGauge;
    private int _packetLossDurationSeconds = 10;
    private int _packetLossRatePps = 10;
    private int _latePacketThresholdMs = 200;
    private DateTime _testStartTime;
    private System.Threading.Timer? _timerTicker;
    private int _liveSent;
    private int _liveReceived;

    public RedPage()
    {
        try
        {
            InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
            _networkService = App.Services.GetRequiredService<INetworkService>();
            _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();
            _loggingService = App.Services.GetRequiredService<ILoggingService>();
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en constructor RedPage: {ex}", ex);
            // Si InitializeComponent falló, no podemos mostrar nada en la UI
            // La excepción se propagará al sistema de navegación
            throw;
        }
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        
        // Solo cargar datos una vez y si no se han cargado previamente
        if (_dataLoaded) return;
        
        try
        {
            // Usar el Dispatcher para asegurar que la página esté completamente renderizada
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await LoadDataAsync();
                    _dataLoaded = true;
                }
                catch (Exception ex2)
                {
                    _loggingService.LogError($"Error en LoadDataAsync (navigated): {ex2}", ex2);
                    if (DebugText != null)
                        DebugText.Text = $"Error: {ex2.Message}";
                }
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error en OnNavigatedTo RedPage: {ex}", ex);
            if (DebugText != null)
                DebugText.Text = $"Error: {ex.Message}";
        }
    }

    private async Task LoadDataAsync()
    {
        try
        {
            // DNS reales actuales (incluye DHCP)
            _loggingService.LogInfo("RedPage: cargando DNS...");
            var actualDns = await Task.Run(() => _networkService.GetActualDnsServers());
            
            if (actualDns.Count > 0)
            {
                _factoryPrimaryDns = actualDns[0];
                _currentPrimaryDns = actualDns[0];
            }
            if (actualDns.Count > 1)
            {
                _factorySecondaryDns = actualDns[1];
                _currentSecondaryDns = actualDns[1];
            }

            // Inicializar desplegable de DNS
            InitializeDnsProviderCombo();

            // Inicializar servidores de packet loss test
            InitializePacketLossServerCombo();
            InitializeRingGauges();

            // Adaptadores (al final de la página)
            _loggingService.LogInfo("RedPage: cargando adaptadores...");
            var adapters = await Task.Run(() => _systemInfoService.GetNetworkInfo());
            if (AdaptersPanel != null)
            {
                AdaptersPanel.Children.Clear();
                foreach (var a in adapters)
                {
                    var card = BuildTextCard(
                        $"{a.Name}",
                        $"{a.Description}\nMAC {a.MacAddress} | IP {a.IpAddress}\n" +
                        $"{a.ConnectionType} | {a.SpeedMbps:F0} Mbps | Conectado {(a.IsConnected ? "Sí" : "No")}");
                    AdaptersPanel.Children.Add(card);
                }
            }

            _loggingService.LogInfo("RedPage: datos cargados");
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error en LoadDataAsync: {ex.Message}", ex);
            if (DebugText != null)
                DebugText.Text = $"Error: {ex.Message}";
        }
    }

    private void InitializePacketLossServerCombo()
    {
        if (PacketLossServerCombo == null) return;

        PacketLossServerCombo.Items.Clear();
        foreach (var server in PacketLossServers)
        {
            PacketLossServerCombo.Items.Add(new ComboBoxItem { Content = server.Name, Tag = server });
        }
        PacketLossServerCombo.SelectedIndex = 0;
    }

    private void InitializeRingGauges()
    {
        try
        {
            // Configurar slider de duración (evitar parseo XAML en runtime)
            if (PacketLossDurationSlider != null)
            {
                PacketLossDurationSlider.Minimum = 5;
                PacketLossDurationSlider.Maximum = 60;
                PacketLossDurationSlider.StepFrequency = 5;
                PacketLossDurationSlider.Value = 10;
                _packetLossDurationSeconds = 10;
                PacketLossDurationText.Text = "10 segundos";
            }

            // Configurar slider de paquetes por segundo
            if (PacketLossRateSlider != null)
            {
                PacketLossRateSlider.Minimum = 1;
                PacketLossRateSlider.Maximum = 100;
                PacketLossRateSlider.StepFrequency = 1;
                PacketLossRateSlider.Value = 10;
                _packetLossRatePps = 10;
                PacketLossRateText.Text = "10 pps";
            }

            _sentGauge = new RingGauge { Label = "Enviados" };
            _timeGauge = new RingGauge { Label = "Tiempo", ProgressBrush = TimeRingBrush };
            _receivedGauge = new RingGauge { Label = "Recibidos" };

            if (SentRingHost != null)
                SentRingHost.Children.Add(_sentGauge);
            if (TimeRingHost != null)
                TimeRingHost.Children.Add(_timeGauge);
            if (ReceivedRingHost != null)
                ReceivedRingHost.Children.Add(_receivedGauge);
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en InitializeRingGauges: {ex.Message}", ex);
        }
    }

    private void PacketLossDurationSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        try
        {
            _packetLossDurationSeconds = (int)e.NewValue;
            if (PacketLossDurationText != null)
            {
                PacketLossDurationText.Text = $"{_packetLossDurationSeconds} segundos";
            }
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en PacketLossDurationSlider_ValueChanged: {ex.Message}", ex);
        }
    }

    private void LatePacketThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        try
        {
            _latePacketThresholdMs = (int)e.NewValue;
            if (LatePacketThresholdText != null)
            {
                LatePacketThresholdText.Text = $"{_latePacketThresholdMs} ms";
            }
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en LatePacketThresholdSlider_ValueChanged: {ex.Message}", ex);
        }
    }

    private void PacketLossRateSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        try
        {
            _packetLossRatePps = (int)e.NewValue;
            if (PacketLossRateText != null)
            {
                PacketLossRateText.Text = $"{_packetLossRatePps} pps";
            }
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en PacketLossRateSlider_ValueChanged: {ex.Message}", ex);
        }
    }

    private async void PacketLossStartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_packetLossRunning)
            {
                _packetLossCts?.Cancel();
                return;
            }

            if (PacketLossServerCombo.SelectedItem is not ComboBoxItem { Tag: PacketLossServer server })
            {
                Feedback.Info(PacketLossStatusText, "Seleccione un servidor para iniciar el test.");
                return;
            }

            _packetLossRunning = true;
            _packetLossCts = new System.Threading.CancellationTokenSource();
            var cts = _packetLossCts;
            _liveSent = 0;
            _liveReceived = 0;
            PacketLossStartButton.Content = "Detener test";
            Feedback.Running(PacketLossStatusText, $"Probando {server.Name} ({server.Host})...", persistent: true);
            
            if (PacketLossResultsPanel != null)
                PacketLossResultsPanel.Visibility = Visibility.Collapsed;
            if (PacketLossDetailText != null)
                PacketLossDetailText.Visibility = Visibility.Collapsed;

            // Calibración: medir la latencia real con un ping rápido para calcular cuántos
            // paquetes por segundo se pueden enviar de verdad (un ping secuencial tarda
            // ~RTT). Con eso el anillo de casillas se arma con una cantidad alcanzable
            // y se completa al terminar el test.
            int effectivePps = _packetLossRatePps;
            try
            {
                using var calibPing = new System.Net.NetworkInformation.Ping();
                var calibReply = await calibPing.SendPingAsync(server.Host, 2000);
                if (calibReply.Status == System.Net.NetworkInformation.IPStatus.Success && calibReply.RoundtripTime > 0)
                {
                    // Margen de 15ms sobre el RTT medido para absorber jitter y overhead
                    int maxPps = Math.Max(1, (int)(1000.0 / (calibReply.RoundtripTime + 15)));
                    effectivePps = Math.Min(_packetLossRatePps, maxPps);
                }
            }
            catch
            {
                // Si falla la calibración, se usa la tasa pedida
            }

            int intervalMs = 1000 / Math.Max(1, effectivePps);

            // Total de casillas planificadas = duración × tasa efectiva. Como cada paquete
            // se agenda en t = k × intervalMs (ver loop), el número de pings que entran en
            // la duración es exactamente este valor y el anillo se completa al final.
            int expectedPackets = (int)Math.Ceiling(_packetLossDurationSeconds * 1000.0 / intervalMs);

            // Resetear gauges: casillas (1 por paquete, tamaño fijo) y valores
            if (_timeGauge != null)
            {
                _timeGauge.Value = "0s";
                _timeGauge.Progress = 0;
            }
            if (_sentGauge != null)
            {
                _sentGauge.ConfigureCells(expectedPackets);
                _sentGauge.Value = "0";
            }
            if (_receivedGauge != null)
            {
                _receivedGauge.ConfigureCells(expectedPackets);
                _receivedGauge.Value = "0";
            }

            _testStartTime = DateTime.UtcNow;

            // Ticker para actualizar el cronómetro, anillo de tiempo y estado en vivo (100ms)
            _timerTicker = new System.Threading.Timer(_ =>
            {
                var elapsed = (DateTime.UtcNow - _testStartTime).TotalSeconds;
                var progress = Math.Clamp(elapsed / _packetLossDurationSeconds, 0.0, 1.0);
                var liveLost = Math.Max(0, _liveSent - _liveReceived);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_packetLossRunning)
                    {
                        _timeGauge!.Value = $"{elapsed:F1}s";
                        _timeGauge.Progress = progress;
                        PacketLossStatusText.Text = $"Enviados: {_liveSent} | Recibidos: {_liveReceived} | Perdidos: {liveLost}  ·  {elapsed:F1}s / {_packetLossDurationSeconds}s";
                    }
                });
            }, null, 0, 100);

            const int timeoutMs = 2000;
            int sent = 0, received = 0, late = 0, highLatency = 0;
            long totalLatency = 0;
            long maxLatency = 0;

            using var ping = new System.Net.NetworkInformation.Ping();
            double nextStartMs = 0;

            // Enviar pings continuos durante la duración configurada, respetando la tasa PPS
            while (!cts.IsCancellationRequested &&
                   (DateTime.UtcNow - _testStartTime).TotalSeconds < _packetLossDurationSeconds)
            {
                // Agendamiento absoluto: el paquete k se envía en t = k × intervalMs desde
                // el inicio, sin acumular desvíos por la duración de cada ping. Así el
                // conteo enviado coincide exactamente con las casillas planificadas.
                var targetStart = _testStartTime.AddMilliseconds(nextStartMs);
                double waitMs = (targetStart - DateTime.UtcNow).TotalMilliseconds;
                if (waitMs > 0)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cts.Token); }
                    catch (TaskCanceledException) { break; }
                }
                nextStartMs += intervalMs;

                sent++;
                int packetIndex = sent - 1;

                // Anillo de enviados: la casilla se enciende en verde apenas se envía y
                // queda así (el envío fue correcto); el resultado del paquete se refleja
                // en el anillo de recibidos, para que ninguna casilla cambie de verde a rojo.
                if (_sentGauge != null)
                {
                    _sentGauge.SetPacketState(packetIndex, RingGauge.PacketCellState.Sent);
                    _sentGauge.Value = $"{sent}";
                }

                try
                {
                    var reply = await ping.SendPingAsync(server.Host, timeoutMs);
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        received++;
                        totalLatency += reply.RoundtripTime;
                        maxLatency = Math.Max(maxLatency, reply.RoundtripTime);
                        
                        // Late packets: latencia superior al umbral configurado
                        if (reply.RoundtripTime > _latePacketThresholdMs)
                        {
                            late++;
                        }
                        
                        // High latency: latencia > 100ms (indicador de congestión)
                        if (reply.RoundtripTime > 100)
                        {
                            highLatency++;
                        }

                        // Estado final del paquete en el anillo de recibidos:
                        // verde si llegó bien, rojo si vino tarde o se perdió
                        var cellState = (reply.RoundtripTime > _latePacketThresholdMs || reply.RoundtripTime > 100)
                            ? RingGauge.PacketCellState.Slow
                            : RingGauge.PacketCellState.Ok;
                        _receivedGauge?.SetPacketState(packetIndex, cellState);
                    }
                    else
                    {
                        // Timeout / destino inalcanzable: paquete perdido (rojo)
                        _receivedGauge?.SetPacketState(packetIndex, RingGauge.PacketCellState.Lost);
                    }
                }
                catch
                {
                    // Perdido (timeout)
                    _receivedGauge?.SetPacketState(packetIndex, RingGauge.PacketCellState.Lost);
                }

                _liveSent = sent;
                _liveReceived = received;
                if (_receivedGauge != null)
                {
                    _receivedGauge.Value = $"{received}";
                }

            }

            _timerTicker?.Dispose();
            _timerTicker = null;

            // Resultados finales
            int lost = sent - received;
            double packetLossPercent = sent > 0 ? (double)lost / sent * 100 : 100;
            double latePacketsPercent = sent > 0 ? (double)late / sent * 100 : 0;
            double avgLatency = received > 0 ? totalLatency / (double)received : 0;

            // Con un test de ping ICMP puro no se puede separar la pérdida de carga vs descarga:
            // el ping mide la ida y vuelta completa. Por eso carga y descarga muestran el mismo
            // valor que la pérdida total (round-trip) y la nota final lo aclara.
            PacketLossUploadText.Text = $"{packetLossPercent:F1}%";
            PacketLossTotalText.Text = $"{packetLossPercent:F1}%";
            PacketLossDownloadText.Text = $"{packetLossPercent:F1}%";
            PacketLossLateText.Text = $"{late} ({latePacketsPercent:F1}%)";

            PacketLossResultsPanel.Visibility = Visibility.Visible;
            PacketLossDetailText.Text =
                $"Servidor: {server.Name} ({server.Host}) | Enviados: {sent} | Recibidos: {received} | Perdidos: {lost} ({packetLossPercent:F1}%) | " +
                $"Tasa: {_packetLossRatePps} pps pedidos / {effectivePps} pps efectivos | Late (> {_latePacketThresholdMs} ms): {late} | Alta latencia (> 100 ms): {highLatency} | " +
                $"Avg: {avgLatency:F1} ms | Max: {maxLatency} ms\n" +
                "Nota: el ping ICMP mide la ida y vuelta completa; la pérdida de carga y de descarga no se puede separar, por eso ambas muestran la pérdida total.";
            PacketLossDetailText.Visibility = Visibility.Visible;

            if (cts.IsCancellationRequested)
            {
                Feedback.Warning(PacketLossStatusText, $"Test detenido por el usuario. Resultados parciales: {lost} de {sent} paquetes perdidos ({packetLossPercent:F1}%).");
            }
            else
            {
                var packetLossMessage = $"Test completado: {packetLossPercent:F1}% de pérdida ({lost} de {sent} paquetes perdidos).";
                if (packetLossPercent < 1)
                    Feedback.Success(PacketLossStatusText, packetLossMessage);
                else if (packetLossPercent < 5)
                    Feedback.Warning(PacketLossStatusText, packetLossMessage);
                else
                    Feedback.Error(PacketLossStatusText, packetLossMessage);
            }
        }
        catch (Exception ex)
        {
            Feedback.Error(PacketLossStatusText, ex.Message);
            _loggingService.LogError("Error en PacketLossStartButton_Click", ex);
        }
        finally
        {
            _packetLossRunning = false;
            _timerTicker?.Dispose();
            _timerTicker = null;
            _packetLossCts?.Dispose();
            _packetLossCts = null;
            PacketLossStartButton.Content = "Iniciar test";
        }
    }

    private void InitializeDnsProviderCombo()
    {
        try
        {
            if (DnsProviderCombo == null) return;
            
            DnsProviderCombo.Items.Clear();
            foreach (var preset in DnsPresets)
            {
                // ComboBoxItem (como en el resto de la app): el Content directo evita que
                // el popup mida mal con DisplayMemberPath al abrirse por primera vez.
                DnsProviderCombo.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset });
            }

            // La opción "Personalizado" es la primera y la seleccionada por defecto,
            // mostrando el DNS actual del sistema en los textboxes para que el usuario lo vea
            DnsProviderCombo.SelectedIndex = 0;

            // Cargar valores actuales en los textboxes (mostrar DHCP si está vacío)
            if (PrimaryDnsTextBox != null)
                PrimaryDnsTextBox.Text = string.IsNullOrEmpty(_currentPrimaryDns) ? "(Automático DHCP)" : _currentPrimaryDns;
            if (SecondaryDnsTextBox != null)
                SecondaryDnsTextBox.Text = string.IsNullOrEmpty(_currentSecondaryDns) ? "" : _currentSecondaryDns;

            // Personalizado seleccionado por defecto = editable
            if (PrimaryDnsTextBox != null)
                PrimaryDnsTextBox.IsReadOnly = false;
            if (SecondaryDnsTextBox != null)
                SecondaryDnsTextBox.IsReadOnly = false;

            // Actualizar lista de DNS manuales
            UpdateManualDnsList();
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en InitializeDnsProviderCombo: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Valores a aplicar según lo seleccionado en el combo (igual que el combo de
    /// tipo de test: la selección se lee al hacer clic, sin SelectionChanged).
    /// Un preset predefinido se aplica tal cual; "Personalizado" usa los campos.
    /// </summary>
    private (string Primary, string Secondary) GetDnsValuesToApply()
    {
        if (DnsProviderCombo.SelectedItem is ComboBoxItem { Tag: DnsPreset preset } && preset.Name != "Personalizado")
        {
            return (preset.PrimaryDns, preset.SecondaryDns);
        }
        return (PrimaryDnsTextBox.Text.Trim(), SecondaryDnsTextBox.Text.Trim());
    }

    private void OpenNetworkAdapterPropertiesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Obtener el adaptador de red activo (con gateway)
            var activeAdapter = GetActiveNetworkAdapter();
            
            if (!string.IsNullOrEmpty(activeAdapter))
            {
                // Intentar abrir propiedades del adaptador específico usando PowerShell
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"$adapter = Get-NetAdapter -Name '{activeAdapter.Replace("'", "''")}' -ErrorAction SilentlyContinue; if ($adapter) {{ $adapter | Get-NetIPInterface -AddressFamily IPv4 | Where-Object {{ $_.ConnectionState -eq 'Connected' }} | ForEach-Object {{ control.exe ncpa.cpl }} }} else {{ control.exe ncpa.cpl }}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                
                try
                {
                    System.Diagnostics.Process.Start(psi);
                }
                catch
                {
                    FallbackToNcpaCpl();
                }
            }
            else
            {
                FallbackToNcpaCpl();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error abriendo propiedades de red", ex);
            FallbackToNcpaCpl();
        }
    }

    private string GetActiveNetworkAdapter()
    {
        try
        {
            var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            var active = networkInterfaces.FirstOrDefault(ni => 
                ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                ni.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            );
            return active?.Name;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error en GetActiveNetworkAdapter: {ex.Message}", ex);
            return null;
        }
    }

    private void FallbackToNcpaCpl()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ncpa.cpl",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error abriendo ncpa.cpl", ex);
            Feedback.Error(ApplyDnsResultText, "No se pudo abrir conexiones de red. Ejecute 'ncpa.cpl' manualmente (Win+R → ncpa.cpl).");
        }
    }

    // El popup del ComboBox abre con un VerticalOffset que WinUI 3 calcula mal cuando
    // el combo está dentro del Frame/ScrollViewer: queda desplazado hacia arriba. En
    // cada apertura alineamos el tope del popup con el tope del combo (igual que el de
    // "Tipo de test"), una vez que el popup ya está medido.
    private async void DnsProviderCombo_DropDownOpened(object sender, object e)
    {
        var combo = (ComboBox)sender;
        try
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(16);
                var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(combo.XamlRoot);
                if (popups.Count == 0 || popups[0].Child is not FrameworkElement fe || fe.ActualHeight <= 0)
                    continue;

                var popup = popups[0];
                var popupPos = fe.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                var comboPos = combo.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                double delta = comboPos.Y - popupPos.Y;
                if (Math.Abs(delta) < 0.5) break;
                popup.VerticalOffset += delta;
                break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DNSCOMBO reposition: {ex.Message}");
        }
    }

    private void DnsTextBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        // Solo permitir números y puntos
        args.Cancel = args.NewText.Any(c => c != '.' && !char.IsDigit(c));
    }

    private async void ApplyDnsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (primaryDns, secondaryDns) = GetDnsValuesToApply();

            if (string.IsNullOrEmpty(primaryDns))
            {
                Feedback.Error(ApplyDnsResultText, "Seleccione o ingrese un DNS válido antes de aplicar.");
                return;
            }

            ApplyDnsButton.IsEnabled = false;
            Feedback.Running(ApplyDnsResultText, "Buscando adaptador de red...");

            var (success, message) = await ApplyDnsToActiveAdapterAsync(primaryDns, secondaryDns);
            if (success)
            {
                UpdateCurrentDns(primaryDns, secondaryDns);
                var applied = string.IsNullOrEmpty(secondaryDns) ? primaryDns : $"{primaryDns} / {secondaryDns}";
                Feedback.Success(ApplyDnsResultText, $"DNS aplicado: {applied}. {message}");
            }
            else
            {
                Feedback.Error(ApplyDnsResultText, message);
            }
        }
        catch (Exception ex)
        {
            Feedback.Error(ApplyDnsResultText, ex.Message);
            _loggingService.LogError("Error en ApplyDnsButton_Click", ex);
        }
        finally
        {
            ApplyDnsButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Detecta el adaptador de red activo y aplica los servidores DNS indicados.
    /// Devuelve (éxito, mensaje) para mostrar al usuario.
    /// </summary>
    private async Task<(bool Success, string Message)> ApplyDnsToActiveAdapterAsync(string primaryDns, string secondaryDns)
    {
        // Usar GetActiveNetworkAdapter que ya maneja la detección correcta del adaptador
        var adapterName = GetActiveNetworkAdapter();
        _loggingService.LogInfo($"GetActiveNetworkAdapter devolvió: {adapterName ?? "null"}");

        if (string.IsNullOrEmpty(adapterName))
        {
            // Si no encuentra por gateway, buscar cualquier interfaz Up
            var allInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            _loggingService.LogInfo($"Interfaces totales detectadas: {allInterfaces.Length}");
            foreach (var ni in allInterfaces)
            {
                _loggingService.LogInfo($"  - {ni.Name}: {ni.OperationalStatus}, {ni.NetworkInterfaceType}");
            }

            var anyUp = allInterfaces.FirstOrDefault(ni =>
                ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
            );

            if (anyUp != null)
            {
                adapterName = anyUp.Name;
                _loggingService.LogWarning($"Usando interfaz alternativa: {anyUp.Name}");
            }
        }

        if (string.IsNullOrEmpty(adapterName))
        {
            _loggingService.LogError("No se pudo encontrar ningún adaptador de red activo.");
            FallbackToNcpaCpl();
            return (false, "No se encontró ningún adaptador de red activo. Se abrirá la configuración de red.");
        }

        var result = await _networkService.SetDnsServersAsync(adapterName, primaryDns, secondaryDns);
        return (result.Success, result.Success
            ? "Servidores DNS configurados correctamente."
            : $"Error al configurar DNS: {result.Output}");
    }

    /// <summary>
    /// Actualiza la config "Personalizado" (DNS actual del sistema) tras aplicar DNS con éxito.
    /// Si "Personalizado" está seleccionado en el desplegable, refleja los nuevos valores al instante.
    /// </summary>
    private void UpdateCurrentDns(string primary, string secondary)
    {
        _currentPrimaryDns = primary;
        _currentSecondaryDns = secondary;

        // Solo refrescar los campos si la opción seleccionada es "Personalizado"
        if (DnsProviderCombo.SelectedItem is ComboBoxItem { Tag: DnsPreset preset } && preset.Name == "Personalizado")
        {
            if (PrimaryDnsTextBox != null)
                PrimaryDnsTextBox.Text = primary;
            if (SecondaryDnsTextBox != null)
                SecondaryDnsTextBox.Text = secondary ?? "";
        }
    }


    private async void AddManualDnsButton_Click(object sender, RoutedEventArgs e)
    {
        var dns1 = ManualDns1TextBox.Text.Trim();
        var dns2 = ManualDns2TextBox.Text.Trim();
        
        if (!string.IsNullOrEmpty(dns1))
        {
            _manualDnsList.Add(dns1);
        }
        if (!string.IsNullOrEmpty(dns2) && dns2 != dns1)
        {
            _manualDnsList.Add(dns2);
        }
        
        ManualDns1TextBox.Text = "";
        ManualDns2TextBox.Text = "";
        UpdateManualDnsList();
    }

    private void UpdateManualDnsList()
    {
        ManualDnsListPanel.Children.Clear();
        foreach (var dns in _manualDnsList)
        {
            var border = new Border
            {
                Background = CardBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 2, 0, 2)
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = dns, VerticalAlignment = VerticalAlignment.Center, Foreground = LightTextBrush });
            var removeBtn = new Button { Content = "×", Width = 24, Height = 24, Padding = new Thickness(0), FontSize = 14 };
            removeBtn.Click += (s, e) => { _manualDnsList.Remove(dns); UpdateManualDnsList(); };
            panel.Children.Add(removeBtn);
            border.Child = panel;
            ManualDnsListPanel.Children.Add(border);
        }
    }

    private async void RunDnsTestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetDnsTestButtonsEnabled(false);

            // Mostrar la pestaña de resultados y arrancar vacía
            DnsResultsTab.Visibility = Visibility.Visible;
            DnsTestResultsPanel.Children.Clear();
            Feedback.Running(DnsTestStatusText, "Ejecutando test de DNS...", persistent: true);

            await RunDnsTestCoreAsync();

            Feedback.Success(DnsTestStatusText, "Test de DNS completado.", persistent: true);
        }
        catch (Exception ex)
        {
            Feedback.Error(DnsTestStatusText, ex.Message, persistent: true);
            _loggingService.LogError("Error en RunDnsTestButton_Click", ex);
        }
        finally
        {
            SetDnsTestButtonsEnabled(true);
            // Evitar que el foco salte a los campos al deshabilitar el botón
            RunDnsTestButton.Focus(FocusState.Programmatic);
        }
    }

    private async void ApplyFastestDnsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetDnsTestButtonsEnabled(false);

            // Mostrar la pestaña de resultados y arrancar vacía
            DnsResultsTab.Visibility = Visibility.Visible;
            DnsTestResultsPanel.Children.Clear();
            Feedback.Running(DnsTestStatusText, "Ejecutando test de DNS...", persistent: true);

            var entries = await RunDnsTestCoreAsync();

            // Mejor candidato: DNS primario que respondió y con la menor latencia
            var best = entries
                .Where(entry => !string.IsNullOrEmpty(entry.Primary) && entry.BestLatency < double.MaxValue)
                .OrderBy(entry => entry.BestLatency)
                .FirstOrDefault();

            if (best == null)
            {
                Feedback.Error(DnsTestStatusText, "Ningún servidor DNS respondió. No se aplicó nada.", persistent: true);
                return;
            }

            Feedback.Running(DnsTestStatusText, $"Aplicando {best.Name} ({best.Primary})...", persistent: true);
            var (success, message) = await ApplyDnsToActiveAdapterAsync(best.Primary, best.Secondary);
            if (success)
            {
                UpdateCurrentDns(best.Primary, best.Secondary);
                Feedback.Success(DnsTestStatusText, $"✓ Aplicado {best.Name} ({best.Primary}) · {best.BestLatency:F0} ms", persistent: true);
            }
            else
            {
                Feedback.Error(DnsTestStatusText, message, persistent: true);
            }
        }
        catch (Exception ex)
        {
            Feedback.Error(DnsTestStatusText, ex.Message, persistent: true);
            _loggingService.LogError("Error en ApplyFastestDnsButton_Click", ex);
        }
        finally
        {
            SetDnsTestButtonsEnabled(true);
            ApplyFastestDnsButton.Focus(FocusState.Programmatic);
        }
    }

    private void SetDnsTestButtonsEnabled(bool enabled)
    {
        RunDnsTestButton.IsEnabled = enabled;
        ApplyFastestDnsButton.IsEnabled = enabled;
        CloseDnsResultsButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Ejecuta el test de DNS completo (personalizado + manuales + presets), mostrando
    /// cada resultado en vivo y devolviendo la lista de lo probado para decisiones posteriores.
    /// </summary>
    private async Task<List<DnsTestEntry>> RunDnsTestCoreAsync()
    {
        var entries = new List<DnsTestEntry>();

        // Probar el DNS personalizado (si está definido)
        var customPrimary = PrimaryDnsTextBox.Text.Trim();
        var customSecondary = SecondaryDnsTextBox.Text.Trim();

        if (!string.IsNullOrEmpty(customPrimary) || !string.IsNullOrEmpty(customSecondary))
        {
            Feedback.Running(DnsTestStatusText, "Probando DNS personalizado...", persistent: true);
            double primaryLatency = -1, secondaryLatency = -1;

            if (!string.IsNullOrEmpty(customPrimary))
            {
                primaryLatency = await _networkService.TestDnsLatencyAsync(customPrimary);
            }
            if (!string.IsNullOrEmpty(customSecondary) && customSecondary != customPrimary)
            {
                secondaryLatency = await _networkService.TestDnsLatencyAsync(customSecondary);
            }

            entries.Add(new DnsTestEntry("Personalizado", customPrimary, customSecondary, BestDnsLatency(primaryLatency, secondaryLatency)));
            AddDnsResultCard("Personalizado", customPrimary, customSecondary, primaryLatency, secondaryLatency);
        }

        // Probar DNS manuales agregados
        foreach (var manualDns in _manualDnsList)
        {
            Feedback.Running(DnsTestStatusText, $"Probando DNS manual ({manualDns})...", persistent: true);
            var latency = await _networkService.TestDnsLatencyAsync(manualDns);
            entries.Add(new DnsTestEntry("Manual", manualDns, "", BestDnsLatency(latency, -1)));
            AddDnsResultCard("Manual", manualDns, "", latency, -1);
        }

        // Probar cada proveedor preestablecido (excepto Personalizado y DNS de fábrica, para no duplicar)
        var presets = DnsPresets.Where(p => p.Name != "Personalizado" && p.Name != "DNS de fábrica (router)").ToList();
        int tested = 0;
        foreach (var preset in presets)
        {
            tested++;
            Feedback.Running(DnsTestStatusText, $"Probando ({tested}/{presets.Count}): {preset.Name}...", persistent: true);
            double primaryLatency = -1, secondaryLatency = -1;

            if (!string.IsNullOrEmpty(preset.PrimaryDns))
            {
                primaryLatency = await _networkService.TestDnsLatencyAsync(preset.PrimaryDns);
            }
            if (!string.IsNullOrEmpty(preset.SecondaryDns) && preset.SecondaryDns != preset.PrimaryDns)
            {
                secondaryLatency = await _networkService.TestDnsLatencyAsync(preset.SecondaryDns);
            }

            entries.Add(new DnsTestEntry(preset.Name, preset.PrimaryDns, preset.SecondaryDns, BestDnsLatency(primaryLatency, secondaryLatency)));
            AddDnsResultCard(preset.Name, preset.PrimaryDns, preset.SecondaryDns, primaryLatency, secondaryLatency);
        }

        return entries;
    }

    /// <summary>
    /// Agrega una card de resultado en tiempo real (apenas termina de probar cada DNS),
    /// insertándola en la posición que le corresponde por latencia (mejores primero).
    /// No se fuerza ningún scroll: el usuario mantiene el control de la posición.
    /// </summary>
    private void AddDnsResultCard(string name, string primary, string secondary, double primaryLatency, double secondaryLatency)
    {
        var card = BuildLatencyCard(name, primary, secondary, primaryLatency, secondaryLatency);
        InsertDnsResultSorted(card, BestDnsLatency(primaryLatency, secondaryLatency));
    }

    /// <summary>
    /// Mejor latencia de un proveedor (la menor entre primario y secundario);
    /// los que no responden van al final (MaxValue).
    /// </summary>
    private static double BestDnsLatency(double primaryLatency, double secondaryLatency)
    {
        var latencies = new List<double>();
        if (primaryLatency >= 0) latencies.Add(primaryLatency);
        if (secondaryLatency >= 0) latencies.Add(secondaryLatency);
        return latencies.Count > 0 ? latencies.Min() : double.MaxValue;
    }

    /// <summary>
    /// Inserta la card ordenada por latencia (ascendente) usando Tag para recordar
    /// la mejor latencia de cada card ya agregada.
    /// </summary>
    private void InsertDnsResultSorted(Border card, double latency)
    {
        card.Tag = latency;
        int index = 0;
        while (index < DnsTestResultsPanel.Children.Count)
        {
            if (DnsTestResultsPanel.Children[index] is Border existing &&
                existing.Tag is double existingLatency &&
                latency < existingLatency)
            {
                break;
            }
            index++;
        }
        DnsTestResultsPanel.Children.Insert(index, card);
    }

    private void CloseDnsResultsButton_Click(object sender, RoutedEventArgs e)
    {
        DnsResultsTab.Visibility = Visibility.Collapsed;
        DnsTestResultsPanel.Children.Clear();
        Feedback.Set(DnsTestStatusText, null);
    }

    private Border BuildLatencyCard(string name, string primary, string secondary, double primaryLatency, double secondaryLatency)
    {
        var border = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };

        var cardGrid = new Grid();
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var panel = new StackPanel { Spacing = 6 };
        var header = new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Foreground = LightTextBrush };
        panel.Children.Add(header);

        // DNS Primario
        if (!string.IsNullOrEmpty(primary))
        {
            var primaryPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            primaryPanel.Children.Add(new TextBlock { Text = "Primario:", Foreground = MutedTextBrush, VerticalAlignment = VerticalAlignment.Center });
            primaryPanel.Children.Add(new TextBlock { Text = primary, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Foreground = LightTextBrush });
            var primaryLatencyText = primaryLatency >= 0 ? $"{primaryLatency:F0} ms" : "Sin respuesta";
            primaryPanel.Children.Add(new TextBlock { Text = primaryLatencyText, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = primaryLatency >= 0 ? AccentBrush : LatencyErrorBrush, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(primaryPanel);
        }

        // DNS Secundario
        if (!string.IsNullOrEmpty(secondary))
        {
            var secondaryPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            secondaryPanel.Children.Add(new TextBlock { Text = "Secundario:", Foreground = MutedTextBrush, VerticalAlignment = VerticalAlignment.Center });
            secondaryPanel.Children.Add(new TextBlock { Text = secondary, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Foreground = LightTextBrush });
            var secondaryLatencyText = secondaryLatency >= 0 ? $"{secondaryLatency:F0} ms" : "Sin respuesta";
            secondaryPanel.Children.Add(new TextBlock { Text = secondaryLatencyText, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = secondaryLatency >= 0 ? AccentBrush : LatencyErrorBrush, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(secondaryPanel);
        }

        Grid.SetColumn(panel, 0);
        cardGrid.Children.Add(panel);

        // Botón "Aplicar": aplica este DNS al sistema directamente
        var applyButton = new Button
        {
            Content = "Aplicar",
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        applyButton.Click += async (s, e) =>
        {
            if (string.IsNullOrEmpty(primary))
            {
                Feedback.Error(ApplyDnsResultText, $"\"{name}\" no tiene DNS primario para aplicar.");
                return;
            }

            applyButton.IsEnabled = false;
            applyButton.Content = "Aplicando...";
            try
            {
                var (success, message) = await ApplyDnsToActiveAdapterAsync(primary, secondary);
                if (success)
                {
                    UpdateCurrentDns(primary, secondary);
                    Feedback.Success(ApplyDnsResultText, $"{name}: {message}");
                }
                else
                {
                    Feedback.Error(ApplyDnsResultText, $"{name}: {message}");
                }
                applyButton.Content = success ? "✓ Aplicado" : "Error";
            }
            catch (Exception ex)
            {
                Feedback.Error(ApplyDnsResultText, $"{name}: no se pudo aplicar: {ex.Message}");
                _loggingService.LogError($"Error aplicando DNS desde resultado ({name})", ex);
                applyButton.Content = "Error";
            }
            finally
            {
                applyButton.IsEnabled = true;
            }
        };

        Grid.SetColumn(applyButton, 1);
        cardGrid.Children.Add(applyButton);

        border.Child = cardGrid;
        return border;
    }

    private async void FlushDnsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FlushDnsButton.IsEnabled = false;
            Feedback.Running(FlushDnsResultText, "Ejecutando flush DNS...");
            var result = await _networkService.FlushDnsAsync();
            if (result.Success)
                Feedback.Success(FlushDnsResultText, "Caché DNS vaciada correctamente.");
            else
                Feedback.Error(FlushDnsResultText, result.Output);
        }
        catch (Exception ex)
        {
            Feedback.Error(FlushDnsResultText, ex.Message);
            _loggingService.LogError("Error en FlushDnsButton_Click", ex);
        }
        finally
        {
            FlushDnsButton.IsEnabled = true;
        }
    }

    private Border BuildTextCard(string title, string description)
    {
        var border = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };

        var panel = new StackPanel { Spacing = 4 };
        var titleBlock = new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = LightTextBrush };
        var descBlock = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = LightTextBrush };
        panel.Children.Add(titleBlock);
        panel.Children.Add(descBlock);
        border.Child = panel;
        return border;
    }

    private record DnsPreset(string Name, string PrimaryDns, string SecondaryDns);

    /// <summary>Resultado de un DNS probado en el test, con su mejor latencia.</summary>
    private record DnsTestEntry(string Name, string Primary, string Secondary, double BestLatency);

    private record PacketLossServer(string Name, string Host);
}
