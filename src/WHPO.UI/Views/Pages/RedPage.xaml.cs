using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Extensions.DependencyInjection;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Controls;
using WHPO_UI.Services;

namespace WHPO_UI.Views.Pages;

public sealed partial class RedPage : Page
{
    private readonly INetworkService _networkService;
    private readonly ISystemInfoService _systemInfoService;
    private readonly ILoggingService _loggingService;
    private bool _dataLoaded;

    // Las cards creadas en código (DNS, adaptadores) acompañan al tema de la app.
    // Pinceles desde los recursos de tema (claro/oscuro), resueltos con el tema
    // EFECTIVO (ThemeBrushes), no con el del sistema.
    private static Microsoft.UI.Xaml.Media.SolidColorBrush CardBrush => ThemeBrushes.Get("CardBackgroundBrush");
    private static Microsoft.UI.Xaml.Media.SolidColorBrush AccentBrush => ThemeBrushes.Get("AccentBrush");
    private static Microsoft.UI.Xaml.Media.SolidColorBrush MutedTextBrush => ThemeBrushes.Get("MutedBrush");
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush LatencyErrorBrush = new(Windows.UI.Color.FromArgb(255, 255, 100, 100));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SuccessBrush = new(Windows.UI.Color.FromArgb(255, 106, 200, 133));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(255, 255, 193, 7));
    // Amarillo del badge "(BETA)" (color fijo deliberado: no depende del tema).
    // Color como struct (seguro en campo estático): el pincel se crea al usarlo, en
    // el hilo de la UI, para no instanciar objetos XAML en el .cctor.
    private static readonly Windows.UI.Color BetaColor = Windows.UI.Color.FromArgb(255, 255, 212, 0);

    // Color del anillo de tiempo del packet loss test (azul)
    private static Microsoft.UI.Xaml.Media.SolidColorBrush TimeRingBrush => ThemeBrushes.Get("AccentBrush");

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

    private readonly ISettingsService _settingsService;
    private WlanOptimizerService? _wlan;
    private TcpService.TcpState? _tcpCurrent;
    private bool _wlanBlockOn;
    private bool _wlanStreamOn;
    private bool _buildingWlan;

    // Controles de la card TCP (se reconstruyen en cada carga / cambio de idioma)
    private TextBlock? _tcpStatusText;
    private TextBlock? _tcpApplyResultText;
    private ToggleSwitch? _tcpNagleToggle;
    private ComboBox? _tcpCongestionCombo;
    private ToggleSwitch? _tcpEcnToggle;
    private ToggleSwitch? _tcpTimestampsToggle;
    private ToggleSwitch? _tcpRssToggle;
    private ToggleSwitch? _tcpFastOpenToggle;
    private TextBlock? _tcpActualNagle;
    private TextBlock? _tcpActualCongestion;
    private TextBlock? _tcpActualEcn;
    private TextBlock? _tcpActualTimestamps;
    private TextBlock? _tcpActualRss;
    private TextBlock? _tcpActualFastOpen;
    private TextBlock? _tcpAutoTuningText;
    private Button? _tcpAutoTuningFixButton;
    private TextBlock? _tcpMtuText;
    private Button? _tcpApplyButton;

    // Controles del optimizador WLAN (inline en la card del adaptador Wi-Fi)
    private TextBlock? _wlanStatusText;
    private ToggleSwitch? _wlanBlockToggle;
    private ToggleSwitch? _wlanStreamToggle;
    private Button? _wlanScanButton;
    private WlanOptimizerService.WlanAdapterInfo? _wlanAdapter;

    private const string TcpBackupKey = "red.tcp.backup";

    public RedPage()
    {
        try
        {
            InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
            _networkService = App.Services.GetRequiredService<INetworkService>();
            _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();
            _loggingService = App.Services.GetRequiredService<ILoggingService>();
            _settingsService = App.Services.GetRequiredService<ISettingsService>();
            // Singleton: el keep-alive del optimizador WLAN vive en DI y sobrevive a
            // la navegación entre pestañas. Los toggles arrancan con el estado real
            // (si el bloqueo quedó activo al salir de la página, sigue activo).
            _wlan = App.Services.GetRequiredService<WlanOptimizerService>();
            _wlanBlockOn = _wlan.BlockScanActive;
            _wlanStreamOn = _wlan.StreamingActive;

            // Título del packet loss test con el badge "(BETA)" en amarillo.
            ApplyPacketLossTitle();

            // Las cards (adaptadores, DNS) se construyen en código con I18n.T: al
            // cambiar idioma estando en la página hay que reconstruirlas. La página
            // se recrea en cada navegación (cache Disabled), así que se desuscribe
            // en OnNavigatedFrom para no acumular handlers.
            I18n.LanguageChanged += OnLanguageChanged;
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en constructor RedPage: {ex}", ex);
            // Si InitializeComponent falló, no podemos mostrar nada en la UI
            // La excepción se propagará al sistema de navegación
            throw;
        }
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        I18n.LanguageChanged -= OnLanguageChanged;
        // NO detener el keep-alive del optimizador WLAN aquí: el servicio es singleton
        // y el bloqueo de escaneo debe seguir activo al navegar a otra pestaña
        // (igual que el original WLAN Optimizer mientras la app corre). Se restaura
        // al cerrar la app (ver MainWindow "Salir" / App.OnWindowClosed).
    }

    /// <summary>Título "Packet Loss Test (BETA)" con el badge en amarillo.</summary>
    private void ApplyPacketLossTitle()
    {
        PacketLossTitleText.Inlines.Clear();
        PacketLossTitleText.Inlines.Add(new Run { Text = I18n.T("Packet Loss Test") + " " });
        PacketLossTitleText.Inlines.Add(new Run { Text = I18n.T("(BETA)"), Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(BetaColor) });
    }

    private void OnLanguageChanged()
    {
        ApplyPacketLossTitle();
        if (!_dataLoaded) return;
        // Reconstruir las cards con el idioma nuevo (mismo flujo que la carga inicial).
        DispatcherQueue.TryEnqueue(async () =>
        {
            try { await LoadDataAsync(); }
            catch (Exception ex) { _loggingService.LogError($"Error re-traduciendo RedPage: {ex.Message}", ex); }
        });
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

            // Adaptadores (al final de la página). El optimizador WLAN se despliega
            // inline en la card del adaptador Wi-Fi (primero conectado / primero), en
            // vez de tener su propia sección.
            _loggingService.LogInfo("RedPage: cargando adaptadores...");
            var adapters = await Task.Run(() => _systemInfoService.GetNetworkInfo());
            if (AdaptersPanel != null)
            {
                AdaptersPanel.Children.Clear();
                var wlanHosts = _wlan?.GetAdapters() ?? new List<WlanOptimizerService.WlanAdapterInfo>();
                var wlanHost = wlanHosts.FirstOrDefault(a => a.State == 1) ?? wlanHosts.FirstOrDefault();
                bool optimizerAttached = false;
                foreach (var a in adapters)
                {
                    // Título = nombre de la interfaz ("Ethernet"/"Wi-Fi"). En WMI,
                    // Name y Description del adaptador suelen ser idénticos (nombre del
                    // hardware): usar NetConnectionId evita que el nombre se repita.
                    string title = string.IsNullOrWhiteSpace(a.NetConnectionId) ? a.Name : a.NetConnectionId;
                    var desc = $"{a.Description}\nMAC {a.MacAddress} | IP {a.IpAddress}\n" +
                               $"{a.ConnectionType} | {a.SpeedMbps:F0} Mbps | {I18n.T("Conectado: {0}", a.IsConnected ? I18n.T("Sí") : I18n.T("No"))}";
                    bool isWifi = a.ConnectionType == "WiFi";
                    Border card;
                    if (isWifi && wlanHost != null && !optimizerAttached)
                    {
                        optimizerAttached = true;
                        _wlanAdapter = wlanHost;
                        card = BuildWlanAdapterCard(title, desc, wlanHost);
                    }
                    else
                    {
                        card = BuildTextCard(title, desc);
                    }
                    AdaptersPanel.Children.Add(card);
                }
            }

            // TCP avanzado (card propia, patrón "Consultando...")
            await BuildTcpAdvancedCardAsync();

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
                PacketLossDurationText.Text = I18n.T("10 segundos");
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

            _sentGauge = new RingGauge { Label = I18n.T("Enviados") };
            _timeGauge = new RingGauge { Label = I18n.T("Tiempo"), ProgressBrush = TimeRingBrush };
            _receivedGauge = new RingGauge { Label = I18n.T("Recibidos") };

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
                PacketLossDurationText.Text = I18n.T("{0} segundos", _packetLossDurationSeconds);
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
            PacketLossStartButton.Content = I18n.T("Detener test");
            Feedback.Running(PacketLossStatusText, I18n.T("Probando {0} ({1})...", server.Name, server.Host), persistent: true);
            
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
                        PacketLossStatusText.Text = I18n.T("Enviados: {0} | Recibidos: {1} | Perdidos: {2}  ·  {3}s / {4}s", _liveSent, _liveReceived, liveLost, $"{elapsed:F1}", _packetLossDurationSeconds);
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
                I18n.T("Servidor: {0} ({1}) | Enviados: {2} | Recibidos: {3} | Perdidos: {4} ({5}%) | Tasa: {6} pps pedidos / {7} pps efectivos | Late (> {8} ms): {9} | Alta latencia (> 100 ms): {10} | Avg: {11} ms | Max: {12} ms",
                    server.Name, server.Host, sent, received, lost, $"{packetLossPercent:F1}", _packetLossRatePps, effectivePps, _latePacketThresholdMs, late, highLatency, $"{avgLatency:F1}", maxLatency) +
                "\n" + I18n.T("Nota: el ping ICMP mide la ida y vuelta completa; la pérdida de carga y de descarga no se puede separar, por eso ambas muestran la pérdida total.");
            PacketLossDetailText.Visibility = Visibility.Visible;

            if (cts.IsCancellationRequested)
            {
                Feedback.Warning(PacketLossStatusText, I18n.T("Test detenido por el usuario. Resultados parciales: {0} de {1} paquetes perdidos ({2}%).", lost, sent, $"{packetLossPercent:F1}"));
            }
            else
            {
                var packetLossMessage = I18n.T("Test completado: {0}% de pérdida ({1} de {2} paquetes perdidos).", $"{packetLossPercent:F1}", lost, sent);
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
            PacketLossStartButton.Content = I18n.T("Iniciar test");
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
                DnsProviderCombo.Items.Add(new ComboBoxItem { Content = I18n.T(preset.Name), Tag = preset });
            }

            // La opción "Personalizado" es la primera y la seleccionada por defecto,
            // mostrando el DNS actual del sistema en los textboxes para que el usuario lo vea
            DnsProviderCombo.SelectedIndex = 0;

            // Cargar valores actuales en los textboxes (mostrar DHCP si está vacío)
            if (PrimaryDnsTextBox != null)
                PrimaryDnsTextBox.Text = string.IsNullOrEmpty(_currentPrimaryDns) ? I18n.T("(Automático DHCP)") : _currentPrimaryDns;
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

    /// <summary>
    /// Al elegir un proveedor predefinido, refleja sus servidores en los campos
    /// Primario/Secundario para que el usuario vea qué se va a aplicar (y pueda editarlos).
    /// "Personalizado" y "DNS de fábrica (router)" muestran el DNS actual del sistema.
    /// </summary>
    private void DnsProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (DnsProviderCombo.SelectedItem is ComboBoxItem { Tag: DnsPreset preset })
            {
                string primary, secondary;
                if (!string.IsNullOrEmpty(preset.PrimaryDns))
                {
                    primary = preset.PrimaryDns;
                    secondary = preset.SecondaryDns;
                }
                else
                {
                    // "Personalizado" / "DNS de fábrica (router)": mostrar lo que hay aplicado
                    primary = _currentPrimaryDns;
                    secondary = _currentSecondaryDns;
                }

                if (PrimaryDnsTextBox != null)
                    PrimaryDnsTextBox.Text = string.IsNullOrEmpty(primary) ? I18n.T("(Automático DHCP)") : primary;
                if (SecondaryDnsTextBox != null)
                    SecondaryDnsTextBox.Text = string.IsNullOrEmpty(secondary) ? "" : secondary;

                // El test de DNS y el historial manual usan estos campos cuando no hay preset
                UpdateManualDnsList();
            }
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en DnsProviderCombo_SelectionChanged: {ex.Message}", ex);
        }
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
                Feedback.Success(ApplyDnsResultText, I18n.T("DNS aplicado: {0}. {1}", applied, message));
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
            return (false, I18n.T("No se encontró ningún adaptador de red activo. Se abrirá la configuración de red."));
        }

        var result = await _networkService.SetDnsServersAsync(adapterName, primaryDns, secondaryDns);
        return (result.Success, result.Success
            ? I18n.T("Servidores DNS configurados correctamente.")
            : I18n.T("Error al configurar DNS: {0}", result.Output));
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
            panel.Children.Add(new TextBlock { Text = dns, VerticalAlignment = VerticalAlignment.Center });
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

            Feedback.Running(DnsTestStatusText, I18n.T("Aplicando {0} ({1})...", best.Name, best.Primary), persistent: true);
            var (success, message) = await ApplyDnsToActiveAdapterAsync(best.Primary, best.Secondary);
            if (success)
            {
                UpdateCurrentDns(best.Primary, best.Secondary);
                Feedback.Success(DnsTestStatusText, I18n.T("✓ Aplicado {0} ({1}) · {2} ms", best.Name, best.Primary, $"{best.BestLatency:F0}"), persistent: true);
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
            Feedback.Running(DnsTestStatusText, I18n.T("Probando DNS manual ({0})...", manualDns), persistent: true);
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
            Feedback.Running(DnsTestStatusText, I18n.T("Probando ({0}/{1}): {2}...", tested, presets.Count, preset.Name), persistent: true);
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
        var header = new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 };
        panel.Children.Add(header);

        // DNS Primario
        if (!string.IsNullOrEmpty(primary))
        {
            var primaryPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            primaryPanel.Children.Add(new TextBlock { Text = I18n.T("Primario:"), Foreground = MutedTextBrush, VerticalAlignment = VerticalAlignment.Center });
            primaryPanel.Children.Add(new TextBlock { Text = primary, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
            var primaryLatencyText = primaryLatency >= 0 ? $"{primaryLatency:F0} ms" : I18n.T("Sin respuesta");
            primaryPanel.Children.Add(new TextBlock { Text = primaryLatencyText, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = primaryLatency >= 0 ? AccentBrush : LatencyErrorBrush, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(primaryPanel);
        }

        // DNS Secundario
        if (!string.IsNullOrEmpty(secondary))
        {
            var secondaryPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            secondaryPanel.Children.Add(new TextBlock { Text = I18n.T("Secundario:"), Foreground = MutedTextBrush, VerticalAlignment = VerticalAlignment.Center });
            secondaryPanel.Children.Add(new TextBlock { Text = secondary, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
            var secondaryLatencyText = secondaryLatency >= 0 ? $"{secondaryLatency:F0} ms" : I18n.T("Sin respuesta");
            secondaryPanel.Children.Add(new TextBlock { Text = secondaryLatencyText, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = secondaryLatency >= 0 ? AccentBrush : LatencyErrorBrush, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(secondaryPanel);
        }

        Grid.SetColumn(panel, 0);
        cardGrid.Children.Add(panel);

        // Botón "Aplicar": aplica este DNS al sistema directamente
        var applyButton = new Button
        {
            Content = I18n.T("Aplicar"),
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
                Feedback.Error(ApplyDnsResultText, I18n.T("\"{0}\" no tiene DNS primario para aplicar.", name));
                return;
            }

            applyButton.IsEnabled = false;
            applyButton.Content = I18n.T("Aplicando...");
            try
            {
                var (success, message) = await ApplyDnsToActiveAdapterAsync(primary, secondary);
                if (success)
                {
                    UpdateCurrentDns(primary, secondary);
                    Feedback.Success(ApplyDnsResultText, I18n.T("{0}: {1}", name, message));
                }
                else
                {
                    Feedback.Error(ApplyDnsResultText, I18n.T("{0}: {1}", name, message));
                }
                applyButton.Content = success ? I18n.T("✓ Aplicado") : I18n.T("Error");
            }
            catch (Exception ex)
            {
                Feedback.Error(ApplyDnsResultText, I18n.T("{0}: no se pudo aplicar: {1}", name, ex.Message));
                _loggingService.LogError($"Error aplicando DNS desde resultado ({name})", ex);
                applyButton.Content = I18n.T("Error");
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

    // ===================== TCP / Red avanzado =====================

    private async Task BuildTcpAdvancedCardAsync()
    {
        if (TcpAdvancedPanel == null) return;
        TcpAdvancedPanel.Children.Clear();

        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16)
        };
        var panel = new StackPanel { Spacing = 12 };
        card.Child = panel;
        TcpAdvancedPanel.Children.Add(card);

        _tcpStatusText = new TextBlock { Text = I18n.T("Consultando estado TCP..."), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_tcpStatusText);
        Feedback.Running(_tcpStatusText, I18n.T("Consultando estado TCP..."), persistent: true);

        var state = await TcpService.GetStateAsync();
        _tcpCurrent = state ?? new TcpService.TcpState();
        if (state == null)
            Feedback.Error(_tcpStatusText, I18n.T("No se pudo leer el estado TCP: {0}", "netsh"));

        // Presets primero (antes de las opciones): un clic aplica todo el perfil
        var presetsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var gamingBtn = new Button { Content = I18n.T("Óptimo para juegos"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        gamingBtn.Click += async (s, e) => ApplyTcpPreset(gaming: true);
        var defaultBtn = new Button { Content = I18n.T("Valores por defecto (Windows)"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        defaultBtn.Click += async (s, e) => ApplyTcpPreset(gaming: false);
        presetsRow.Children.Add(gamingBtn);
        presetsRow.Children.Add(defaultBtn);
        panel.Children.Add(presetsRow);

        // Desactivar Nagle (TCPNoDelay + TcpAckFrequency en la interfaz activa)
        _tcpNagleToggle = NewToggle();
        _tcpNagleToggle.IsOn = _tcpCurrent.NagleDisabled;
        var (nagleRow, nagleActual) = BuildSettingRow(
            I18n.T("Desactivar Nagle"), I18n.T("TCPNoDelay + TcpAckFrequency"), ActualText(_tcpCurrent.NagleDisabled), _tcpNagleToggle, TtNagle);
        _tcpActualNagle = nagleActual;
        panel.Children.Add(nagleRow);

        // Algoritmo de congestión (CUBIC / CTCP)
        _tcpCongestionCombo = new ComboBox { MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Stretch };
        _tcpCongestionCombo.Items.Add(new ComboBoxItem { Content = I18n.T("CUBIC (predeterminado)"), Tag = "cubic" });
        _tcpCongestionCombo.Items.Add(new ComboBoxItem { Content = I18n.T("CTCP (menor latencia)"), Tag = "ctcp" });
        _tcpCongestionCombo.SelectedIndex = _tcpCurrent.CongestionProvider == "ctcp" ? 1 : 0;
        var (congestionRow, congestionActual) = BuildSettingRow(
            I18n.T("Algoritmo de congestión"), null, CongestionActual(_tcpCurrent.CongestionProvider), _tcpCongestionCombo, TtCongestion);
        _tcpActualCongestion = congestionActual;
        panel.Children.Add(congestionRow);

        // ECN
        _tcpEcnToggle = NewToggle();
        _tcpEcnToggle.IsOn = _tcpCurrent.EcnEnabled;
        var (ecnRow, ecnActual) = BuildSettingRow(
            I18n.T("ECN (Notificación de congestión explícita)"), null, ActualText(_tcpCurrent.EcnEnabled), _tcpEcnToggle, TtEcn);
        _tcpActualEcn = ecnActual;
        panel.Children.Add(ecnRow);

        // Timestamps
        _tcpTimestampsToggle = NewToggle();
        _tcpTimestampsToggle.IsOn = _tcpCurrent.TimestampsEnabled;
        var (timestampsRow, timestampsActual) = BuildSettingRow(
            I18n.T("Timestamps TCP (RFC 1323)"), null, ActualText(_tcpCurrent.TimestampsEnabled), _tcpTimestampsToggle, TtTimestamps);
        _tcpActualTimestamps = timestampsActual;
        panel.Children.Add(timestampsRow);

        // RSS
        _tcpRssToggle = NewToggle();
        _tcpRssToggle.IsOn = _tcpCurrent.RssEnabled;
        var (rssRow, rssActual) = BuildSettingRow(
            I18n.T("RSS (Receive Side Scaling)"), null, ActualText(_tcpCurrent.RssEnabled), _tcpRssToggle, TtRss);
        _tcpActualRss = rssActual;
        panel.Children.Add(rssRow);

        // Fast Open
        _tcpFastOpenToggle = NewToggle();
        _tcpFastOpenToggle.IsOn = _tcpCurrent.FastOpenEnabled;
        var (fastOpenRow, fastOpenActual) = BuildSettingRow(
            I18n.T("TCP Fast Open"), null, ActualText(_tcpCurrent.FastOpenEnabled), _tcpFastOpenToggle, TtFastOpen);
        _tcpActualFastOpen = fastOpenActual;
        panel.Children.Add(fastOpenRow);

        panel.Children.Add(new Rectangle { Height = 1, Fill = ThemeBrushes.Get("CardBorderBrush"), Margin = new Thickness(0, 4, 0, 4) });        // Autotuning: solo lectura + botón "recomendado" si no está en Normal
        var autotuningPanel = new StackPanel { Spacing = 4 };
        autotuningPanel.Children.Add(BuildInfoTitle(I18n.T("Ajuste automático de la ventana TCP"), TtAutoTuning));
        _tcpAutoTuningText = new TextBlock { Text = AutoTuningActual(_tcpCurrent.AutoTuningLevel), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        autotuningPanel.Children.Add(_tcpAutoTuningText);
        if (!_tcpCurrent.AutoTuningLevel.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            _tcpAutoTuningFixButton = new Button { Content = I18n.T("Restaurar a Normal"), Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left };
            _tcpAutoTuningFixButton.Click += async (s, e) =>
            {
                var d = ReadTcpDesired();
                d.AutoTuningLevel = "normal";
                await ApplyTcpAsync(I18n.T("Ajuste automático restaurado a Normal."), d, includeAutoTuning: true);
            };
            ToolTipService.SetToolTip(_tcpAutoTuningFixButton, I18n.T(TtAutoTuning));
            autotuningPanel.Children.Add(_tcpAutoTuningFixButton);
        }
        panel.Children.Add(autotuningPanel);

        // MTU real por interfaz
        var mtuPanel = new StackPanel { Spacing = 4 };
        mtuPanel.Children.Add(BuildInfoTitle(I18n.T("MTU"), TtMtu));
        _tcpMtuText = new TextBlock { Text = MtuActual(_tcpCurrent), FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        mtuPanel.Children.Add(_tcpMtuText);
        panel.Children.Add(mtuPanel);

        // Acciones (los presets ya están arriba, antes de las opciones)
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _tcpApplyButton = new Button { Content = I18n.T("Aplicar"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        _tcpApplyButton.Click += async (s, e) => await ApplyTcpAsync(I18n.T("TCP aplicado correctamente."));
        var restoreBtn = new Button { Content = I18n.T("Restaurar todo"), Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6) };
        restoreBtn.Click += async (s, e) => await RestoreTcpAsync();
        buttons.Children.Add(_tcpApplyButton);
        buttons.Children.Add(restoreBtn);
        panel.Children.Add(buttons);

        _tcpApplyResultText = new TextBlock { Text = "", FontSize = 12, Visibility = Visibility.Collapsed, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_tcpApplyResultText);

        panel.Children.Add(new TextBlock
        {
            Text = I18n.T("Solo se incluyen ajustes con efecto real y reversibles. El ajuste automático de ventana TCP se mantiene en Normal para no afectar la descarga."),
            FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap
        });

        if (state != null)
        {
            Feedback.Set(_tcpStatusText, null);
            _tcpStatusText.Visibility = Visibility.Collapsed;
        }
    }

    private TcpService.TcpState ReadTcpDesired()
    {
        var d = new TcpService.TcpState();
        if (_tcpCongestionCombo?.SelectedItem is ComboBoxItem { Tag: string tag }) d.CongestionProvider = tag;
        d.EcnEnabled = _tcpEcnToggle?.IsOn ?? false;
        d.TimestampsEnabled = _tcpTimestampsToggle?.IsOn ?? false;
        d.RssEnabled = _tcpRssToggle?.IsOn ?? false;
        d.FastOpenEnabled = _tcpFastOpenToggle?.IsOn ?? false;
        d.NagleDisabled = _tcpNagleToggle?.IsOn ?? false;
        return d;
    }

    /// <summary>
    /// Carga un preset en los controles (NO lo aplica): el usuario revisa los valores
    /// contra el estado actual y decide si toca "Aplicar".
    /// </summary>
    private void ApplyTcpPreset(bool gaming)
    {
        if (_tcpNagleToggle == null || _tcpCongestionCombo == null) return;
        _tcpNagleToggle.IsOn = gaming;                        // off en gaming
        _tcpCongestionCombo.SelectedIndex = gaming ? 1 : 0;   // CTCP / CUBIC
        if (_tcpEcnToggle != null) _tcpEcnToggle.IsOn = false;
        if (_tcpTimestampsToggle != null) _tcpTimestampsToggle.IsOn = !gaming;
        if (_tcpRssToggle != null) _tcpRssToggle.IsOn = true;
        if (_tcpFastOpenToggle != null) _tcpFastOpenToggle.IsOn = true;
        var name = gaming ? I18n.T("Óptimo para juegos") : I18n.T("Valores por defecto (Windows)");
        if (_tcpApplyResultText != null)
        {
            _tcpApplyResultText.Visibility = Visibility.Visible;
            Feedback.Info(_tcpApplyResultText, I18n.T("Preset cargado: {0} — revisá los valores y toca Aplicar.", name));
        }
    }

    private async Task ApplyTcpAsync(string successMessage, TcpService.TcpState? desiredOverride = null, bool includeAutoTuning = false)
    {
        if (_tcpApplyResultText == null) return;
        _tcpApplyResultText.Visibility = Visibility.Visible;
        if (_tcpApplyButton != null) _tcpApplyButton.IsEnabled = false;
        Feedback.Running(_tcpApplyResultText, I18n.T("Aplicando TCP..."));
        try
        {
            SaveTcpBackup(_tcpCurrent);
            var desired = desiredOverride ?? ReadTcpDesired();
            var (ok, msg) = await TcpService.ApplyAsync(desired, includeAutoTuning);
            if (ok) Feedback.Success(_tcpApplyResultText, successMessage);
            else Feedback.Error(_tcpApplyResultText, I18n.T("Error aplicando TCP: {0}", msg));
            await RefreshTcpAsync();
        }
        catch (Exception ex)
        {
            Feedback.Error(_tcpApplyResultText, ex.Message);
            _loggingService.LogError("Error aplicando TCP", ex);
        }
        finally
        {
            if (_tcpApplyButton != null) _tcpApplyButton.IsEnabled = true;
        }
    }

    private async Task RestoreTcpAsync()
    {
        var backup = LoadTcpBackup();
        if (backup == null)
        {
            if (_tcpApplyResultText != null)
            {
                _tcpApplyResultText.Visibility = Visibility.Visible;
                Feedback.Info(_tcpApplyResultText, I18n.T("No hay backup guardado todavía."));
            }
            return;
        }
        await ApplyTcpAsync(I18n.T("Estado TCP anterior restaurado."), backup);
    }

    private async Task RefreshTcpAsync()
    {
        var state = await TcpService.GetStateAsync();
        if (state == null) return;
        _tcpCurrent = state;

        // Actualizar etiquetas "Actual:" en el lugar (no se reconstruye la card,
        // así el mensaje de resultado queda visible).
        if (_tcpActualNagle != null) _tcpActualNagle.Text = ActualText(state.NagleDisabled);
        if (_tcpActualCongestion != null) _tcpActualCongestion.Text = CongestionActual(state.CongestionProvider);
        if (_tcpActualEcn != null) _tcpActualEcn.Text = ActualText(state.EcnEnabled);
        if (_tcpActualTimestamps != null) _tcpActualTimestamps.Text = ActualText(state.TimestampsEnabled);
        if (_tcpActualRss != null) _tcpActualRss.Text = ActualText(state.RssEnabled);
        if (_tcpActualFastOpen != null) _tcpActualFastOpen.Text = ActualText(state.FastOpenEnabled);
        if (_tcpAutoTuningText != null) _tcpAutoTuningText.Text = AutoTuningActual(state.AutoTuningLevel);
        if (_tcpAutoTuningFixButton != null)
            _tcpAutoTuningFixButton.Visibility = state.AutoTuningLevel.Equals("normal", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed : Visibility.Visible;
        if (_tcpMtuText != null) _tcpMtuText.Text = MtuActual(state);

        // Sincronizar los controles con el estado real aplicado
        if (_tcpNagleToggle != null) _tcpNagleToggle.IsOn = state.NagleDisabled;
        if (_tcpCongestionCombo != null) _tcpCongestionCombo.SelectedIndex = state.CongestionProvider == "ctcp" ? 1 : 0;
        if (_tcpEcnToggle != null) _tcpEcnToggle.IsOn = state.EcnEnabled;
        if (_tcpTimestampsToggle != null) _tcpTimestampsToggle.IsOn = state.TimestampsEnabled;
        if (_tcpRssToggle != null) _tcpRssToggle.IsOn = state.RssEnabled;
        if (_tcpFastOpenToggle != null) _tcpFastOpenToggle.IsOn = state.FastOpenEnabled;
    }

    private void SaveTcpBackup(TcpService.TcpState? state)
    {
        if (state == null) return;
        var dto = new TcpBackupDto
        {
            Congestion = state.CongestionProvider,
            Ecn = state.EcnEnabled,
            Timestamps = state.TimestampsEnabled,
            Rss = state.RssEnabled,
            FastOpen = state.FastOpenEnabled,
            NagleDisabled = state.NagleDisabled
        };
        try
        {
            _settingsService.Set(TcpBackupKey, JsonSerializer.Serialize(dto));
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error guardando backup TCP", ex);
        }
    }

    private TcpService.TcpState? LoadTcpBackup()
    {
        try
        {
            var json = _settingsService.Get<string>(TcpBackupKey, "");
            if (string.IsNullOrWhiteSpace(json)) return null;
            var dto = JsonSerializer.Deserialize<TcpBackupDto>(json);
            if (dto == null) return null;
            return new TcpService.TcpState
            {
                CongestionProvider = dto.Congestion ?? "cubic",
                EcnEnabled = dto.Ecn,
                TimestampsEnabled = dto.Timestamps,
                RssEnabled = dto.Rss,
                FastOpenEnabled = dto.FastOpen,
                NagleDisabled = dto.NagleDisabled
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ActualText(bool value)
        => I18n.T("Actual: {0}", value ? I18n.T("Activado") : I18n.T("Desactivado"));

    private static string CongestionActual(string provider)
        => I18n.T("Actual: {0}", provider == "ctcp" ? "CTCP" : I18n.T("CUBIC (predeterminado)"));

    private static string AutoTuningActual(string level)
    {
        string label = level switch
        {
            "disabled" => I18n.T("Deshabilitado"),
            "highlyrestricted" => I18n.T("Muy restringido"),
            "restricted" => I18n.T("Restringido"),
            "experimental" => I18n.T("Experimental"),
            _ => I18n.T("Normal (recomendado)")
        };
        var text = I18n.T("Actual: {0}", label);
        if (level == "disabled")
            text += " · " + I18n.T("Aviso: desactivar el ajuste automático puede reducir la velocidad de descarga. Se recomienda dejarlo en Normal.");
        return text;
    }

    private static string MtuActual(TcpService.TcpState state)
    {
        if (state.MtuList.Count == 0) return I18n.T("MTU: {0}", "--");
        return I18n.T("MTU: {0}", string.Join(", ", state.MtuList.Select(m => $"{m.Name} {m.Mtu}")));
    }

    /// <summary>
    /// Fila de ajuste: título + subtítulo/actual a la izquierda, control a la derecha.
    /// tooltip: texto (clave de traducción) que explica qué hace y cuándo conviene ON/OFF.
    /// Al lado del título se muestra un botón "?" con el mismo estilo que Optimizaciones:
    /// tooltip custom (título en negrita + descripción), colocado abajo del botón.
    /// </summary>
    private static (Grid Row, TextBlock Actual) BuildSettingRow(string title, string? subtitle, string? actual, FrameworkElement control, string? tooltip = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 2 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        titleRow.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(tooltip))
            titleRow.Children.Add(BuildInfoButton(title, tooltip));
        left.Children.Add(titleRow);
        if (!string.IsNullOrEmpty(subtitle))
            left.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap });
        TextBlock? actualTb = null;
        if (!string.IsNullOrEmpty(actual))
        {
            actualTb = new TextBlock { Text = actual, FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
            left.Children.Add(actualTb);
        }
        grid.Children.Add(left);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return (grid, actualTb ?? new TextBlock());
    }

    /// <summary>Título con botón "?" de info (estilo Optimizaciones).</summary>
    private static StackPanel BuildInfoTitle(string text, string tooltip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(BuildInfoButton(text, tooltip));
        return row;
    }

    /// <summary>
    /// Botón "?" chico con tooltip custom: título en negrita arriba, descripción abajo
    /// (mismo estilo que BuildInfoButton de OptimizacionesPage).
    /// </summary>
    private static Button BuildInfoButton(string title, string tooltipBody)
    {
        var infoButton = new Button
        {
            Content = "?",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            MinWidth = 22,
            MaxWidth = 22,
            Height = 22,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Foreground = MutedTextBrush,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel { Spacing = 6, MaxWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = I18n.T(tooltipBody),
            FontSize = 12,
            Foreground = MutedTextBrush,
            TextWrapping = TextWrapping.Wrap
        });

        ToolTipService.SetToolTip(infoButton, new ToolTip
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom,
            Content = content
        });
        return infoButton;
    }

    // ===== Tooltips: qué hace cada ajuste y cuándo conviene ON/OFF =====
    private const string TtNagle = "Nagle agrupa paquetes pequeños para reducir el overhead de red. Desactivarlo (TCPNoDelay + TcpAckFrequency = 1) baja la latencia de apps que envían muchos paquetes chicos: juegos online, RDP, SSH, voz. Costo: un poco más de overhead. → ON (desactivar) para juegos; OFF si solo navegás o descargás.";
    private const string TtCongestion = "Cómo reacciona el stack TCP a la congestión. CUBIC: el default moderno de Windows, buen balance. CTCP (Compound TCP): más agresivo en redes con pérdida, el clásico 'gamer'. → Probá CTCP si notás latencia o pérdida en juegos; CUBIC para uso general.";
    private const string TtEcn = "Marca los paquetes congestionados en vez de descartarlos. Desactivarlo evita problemas con routers o ISPs viejos que lo manejan mal (jitter o pérdida de paquetes). → OFF para juegos; ON solo si sabés que tu router lo soporta bien.";
    private const string TtTimestamps = "Agregan un timestamp a los paquetes para medir el RTT. Desactivarlos reduce un poco el overhead del stack y el tamaño de cada paquete. → OFF en juegos (ganancia marginal); ON en descargas masivas.";
    private const string TtRss = "Distribuye el procesamiento de paquetes entre varios núcleos de CPU. → ON si tu adaptador lo soporta (mejor throughput multihilo); los adaptadores viejos lo ignoran.";
    private const string TtFastOpen = "Permite enviar datos en el primer SYN: el handshake es más corto y baja la latencia de conexiones nuevas (juegos, navegación). → ON recomendado en general.";
    private const string TtAutoTuning = "Windows ajusta solo el tamaño de la ventana TCP. 'Normal' es lo recomendado: desactivarlo puede bajar la latencia con routers malos, pero suele reducir la velocidad de descarga.";
    private const string TtMtu = "Tamaño máximo de paquete. 1500 es el estándar Ethernet; con PPPoE (fibra con login) lo correcto es 1492. Un MTU incorrecto causa fragmentación o pérdida de paquetes.";
    private const string TtWlanBlock = "Windows escanea redes Wi-Fi periódicamente aunque estés conectado, y eso causa micro-picos de latencia en juegos y streaming. Este toggle lo bloquea y se re-aplica cada 5 s mientras la app está abierta. → ON para juegos/streaming por Wi-Fi; OFF si necesitás roaming o la lista de redes siempre fresca.";
    private const string TtWlanStream = "Reduce aún más los escaneos del adaptador priorizando la conexión activa. Algunos drivers no lo soportan (la app avisa). → ON para streaming o juegos si tu driver lo permite.";
    private const string TtWlanScan = "El bloqueo deja la lista de redes desactualizada; este botón fuerza un escaneo manual para refrescarla.";

    private static ToggleSwitch NewToggle()
        => new() { OnContent = "", OffContent = "" };

    private sealed class TcpBackupDto
    {
        public string? Congestion { get; set; }
        public bool Ecn { get; set; }
        public bool Timestamps { get; set; }
        public bool Rss { get; set; }
        public bool FastOpen { get; set; }
        public bool NagleDisabled { get; set; }
    }

    // ===================== Optimizador WLAN (inline en la card Wi-Fi) =====================

    private Border BuildWlanAdapterCard(string title, string desc, WlanOptimizerService.WlanAdapterInfo adapter)
    {
        _buildingWlan = true;
        try
        {
            var card = new Border
            {
                Background = CardBrush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };
            var panel = new StackPanel { Spacing = 10 };
            card.Child = panel;

            var textPanel = new StackPanel { Spacing = 4 };
            textPanel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            textPanel.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(textPanel);

            panel.Children.Add(new Rectangle { Height = 1, Fill = ThemeBrushes.Get("CardBorderBrush"), Margin = new Thickness(0, 2, 0, 2) });

            _wlanStatusText = new TextBlock { Text = "", FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_wlanStatusText);

            _wlanBlockToggle = NewToggle();
            _wlanBlockToggle.IsOn = _wlanBlockOn;
            _wlanBlockToggle.Toggled += WlanBlockToggle_Toggled;
            panel.Children.Add(BuildSettingRow(I18n.T("Bloquear escaneo de fondo"), null, null, _wlanBlockToggle, TtWlanBlock).Row);

            _wlanStreamToggle = NewToggle();
            _wlanStreamToggle.IsOn = _wlanStreamOn;
            _wlanStreamToggle.Toggled += WlanStreamToggle_Toggled;
            panel.Children.Add(BuildSettingRow(I18n.T("Modo streaming"), I18n.T("Reduce aún más los escaneos del adaptador"), null, _wlanStreamToggle, TtWlanStream).Row);

            _wlanScanButton = new Button { Content = I18n.T("Escanear ahora"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 8, 16, 8), CornerRadius = new CornerRadius(6) };
            _wlanScanButton.Click += WlanScanButton_Click;
            ToolTipService.SetToolTip(_wlanScanButton, I18n.T(TtWlanScan));
            panel.Children.Add(_wlanScanButton);

            if (_wlanBlockOn) SetWlanStatusActive();
            else SetWlanStatusInactive();
            return card;
        }
        finally
        {
            _buildingWlan = false;
        }
    }

    private void WlanBlockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_buildingWlan || _wlanBlockToggle == null || _wlanAdapter == null) return;
        _wlanBlockOn = _wlanBlockToggle.IsOn;
        var adapter = _wlanAdapter;
        if (_wlanBlockOn)
        {
            _wlan!.StartKeepAlive(adapter.Guid, true, _wlanStreamOn);
            SetWlanStatusActive();
        }
        else
        {
            _wlan!.StopKeepAlive();
            _wlan!.SetBackgroundScan(adapter.Guid, true);
            _wlanStreamOn = false;
            if (_wlanStreamToggle != null) _wlanStreamToggle.IsOn = false;
            SetWlanStatusInactive();
        }
    }

    private void WlanStreamToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_buildingWlan || _wlanStreamToggle == null || _wlanAdapter == null) return;
        _wlanStreamOn = _wlanStreamToggle.IsOn;
        var adapter = _wlanAdapter;
        if (_wlanBlockOn)
        {
            _wlan!.StartKeepAlive(adapter.Guid, true, _wlanStreamOn);
            SetWlanStatusActive();
        }
        else
        {
            _wlan!.SetMediaStreaming(adapter.Guid, _wlanStreamOn);
        }
        WarnIfStreamingUnsupported();
    }

    private void WarnIfStreamingUnsupported()
    {
        // Algunos drivers (ej: RTL8811AU USB) rechazan el opcode de media streaming
        // con ERROR_INVALID_PARAMETER: avisar una vez en vez de dejar el toggle mudo.
        if (_wlan != null && !_wlan.MediaStreamingSupported && _wlanStreamOn && _wlanStatusText != null)
            Feedback.Warning(_wlanStatusText, I18n.T("Este adaptador no soporta el modo streaming."), persistent: true);
    }

    private void WlanScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wlanAdapter == null) return;
        var adapter = _wlanAdapter;
        if (_wlan!.ScanNow(adapter.Guid) && _wlanStatusText != null)
            Feedback.Success(_wlanStatusText, I18n.T("Escaneo solicitado."));
        else if (_wlanStatusText != null)
            Feedback.Error(_wlanStatusText, I18n.T("Error configurando WLAN: {0}", "WlanScan"));
    }

    private void SetWlanStatusActive()
    {
        if (_wlanStatusText != null)
            Feedback.Success(_wlanStatusText, I18n.T("Estado: {0}", I18n.T("Activo (re-aplicación cada 5 s)")), persistent: true);
    }

    private void SetWlanStatusInactive()
    {
        if (_wlanStatusText != null)
            Feedback.Info(_wlanStatusText, I18n.T("Estado: {0}", I18n.T("Inactivo")), persistent: true);
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
        var titleBlock = new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var descBlock = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap };
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
