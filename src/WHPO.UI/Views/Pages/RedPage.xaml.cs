using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private readonly List<string> _manualDnsList = new();
    private System.Threading.CancellationTokenSource? _packetLossCts;
    private bool _packetLossRunning;
    private RingGauge? _sentGauge;
    private RingGauge? _timeGauge;
    private RingGauge? _receivedGauge;
    private int _packetLossDurationSeconds = 15;
    private int _latePacketThresholdMs = 200;
    private DateTime _testStartTime;
    private System.Threading.Timer? _timerTicker;

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
            }
            if (actualDns.Count > 1)
            {
                _factorySecondaryDns = actualDns[1];
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
            PacketLossServerCombo.Items.Add(server);
        }
        PacketLossServerCombo.DisplayMemberPath = "Name";
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
                PacketLossDurationSlider.Value = 15;
                _packetLossDurationSeconds = 15;
                PacketLossDurationText.Text = "15 segundos";
            }

            _sentGauge = new RingGauge { Label = "Enviados" };
            _timeGauge = new RingGauge { Label = "Tiempo" };
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

    private async void PacketLossStartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_packetLossRunning)
            {
                _packetLossCts?.Cancel();
                return;
            }

            if (PacketLossServerCombo.SelectedItem is not PacketLossServer server)
            {
                PacketLossStatusText.Text = "Seleccione un servidor para iniciar el test.";
                return;
            }

            _packetLossRunning = true;
            _packetLossCts = new System.Threading.CancellationTokenSource();
            var cts = _packetLossCts;
            PacketLossStartButton.Content = "Detener test";
            PacketLossStatusText.Text = $"Probando {server.Name} ({server.Host})...";
            
            if (PacketLossResultsPanel != null)
                PacketLossResultsPanel.Visibility = Visibility.Collapsed;
            if (PacketLossDetailText != null)
                PacketLossDetailText.Visibility = Visibility.Collapsed;

            // Resetear gauges
            if (_sentGauge != null)
            {
                _sentGauge.Value = "0";
                _sentGauge.Progress = 0;
            }
            if (_timeGauge != null)
            {
                _timeGauge.Value = "0s";
                _timeGauge.Progress = 0;
            }
            if (_receivedGauge != null)
            {
                _receivedGauge.Value = "0";
                _receivedGauge.Progress = 0;
            }

            _testStartTime = DateTime.UtcNow;

            // Ticker para actualizar el cronómetro y anillos (250ms)
            _timerTicker = new System.Threading.Timer(_ =>
            {
                var elapsed = (DateTime.UtcNow - _testStartTime).TotalSeconds;
                var progress = Math.Clamp(elapsed / _packetLossDurationSeconds, 0.0, 1.0);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_packetLossRunning)
                    {
                        _timeGauge!.Value = $"{elapsed:F1}s";
                        _timeGauge.Progress = progress;
                    }
                });
            }, null, 0, 250);

            const int timeoutMs = 2000;
            int sent = 0, received = 0, late = 0, highLatency = 0;
            long totalLatency = 0;
            long maxLatency = 0;

            using var ping = new System.Net.NetworkInformation.Ping();

            // Enviar pings continuos durante la duración configurada
            while (!cts.IsCancellationRequested &&
                   (DateTime.UtcNow - _testStartTime).TotalSeconds < _packetLossDurationSeconds)
            {
                sent++;
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
                    }

                    // Actualizar gauges en cada ping
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_sentGauge != null)
                        {
                            _sentGauge.Value = $"{sent}";
                            _sentGauge.Progress = Math.Clamp(sent / 100.0, 0.0, 1.0);
                        }
                        if (_receivedGauge != null)
                        {
                            _receivedGauge.Value = $"{received}";
                            _receivedGauge.Progress = Math.Clamp(received / 100.0, 0.0, 1.0);
                        }
                    });
                }
                catch
                {
                    // Perdido (timeout)
                }
            }

            _timerTicker?.Dispose();
            _timerTicker = null;

            // Resultados finales
            int lost = sent - received;
            double packetLossPercent = sent > 0 ? (double)lost / sent * 100 : 100;
            double latePacketsPercent = sent > 0 ? (double)late / sent * 100 : 0;
            double avgLatency = received > 0 ? totalLatency / (double)received : 0;

            // NOTA: Con un test de ping ICMP puro, NO se puede determinar upload vs download packet loss.
            // Esas métricas requieren test HTTP/TCP.
            // Mostramos métricas que SÍ se pueden medir:
            // - Total Packet Loss: paquetes que no llegan (timeout)
            // - Late Packets: paquetes que llegan pero con latencia > al umbral configurado
            // - High Latency: paquetes con latencia > 100ms (indicador de congestión)
            double uploadLossPercent = packetLossPercent; // Paquetes perdidos (no llegan)
            double highLatencyPercent = highLatency > 0 ? (double)highLatency / sent * 100 : 0;

            // Actualizar textos de resultados
            PacketLossUploadText.Text = $"{uploadLossPercent:F1}%";
            PacketLossTotalText.Text = $"{packetLossPercent:F1}%";
            PacketLossDownloadText.Text = "N/D"; // No se puede determinar con ping ICMP
            PacketLossLateText.Text = $"{late} ({latePacketsPercent:F1}%)";

            PacketLossResultsPanel.Visibility = Visibility.Visible;
            PacketLossDetailText.Text = $"Servidor: {server.Name} ({server.Host}) | Enviados: {sent} | Recibidos: {received} | Perdidos: {lost} | Late: {late} (> {_latePacketThresholdMs}ms) | Alta latencia (>100ms): {highLatency} | Avg: {avgLatency:F1} ms | Max: {maxLatency} ms";
            PacketLossDetailText.Visibility = Visibility.Visible;
            PacketLossStatusText.Text = cts.IsCancellationRequested
                ? $"Test detenido por el usuario. Resultados parciales ({sent} pings)."
                : "Test de pérdida de paquetes completado.";
        }
        catch (Exception ex)
        {
            PacketLossStatusText.Text = $"Error: {ex.Message}";
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
                DnsProviderCombo.Items.Add(preset);
            }

            DnsProviderCombo.DisplayMemberPath = "Name";

            // La opción "Personalizado" es la primera y la seleccionada por defecto,
            // mostrando el DNS de fábrica en los textboxes para que el usuario lo vea
            DnsProviderCombo.SelectedIndex = 0;

            // Cargar valores de fábrica en los textboxes (mostrar DHCP si está vacío)
            if (PrimaryDnsTextBox != null)
                PrimaryDnsTextBox.Text = string.IsNullOrEmpty(_factoryPrimaryDns) ? "(Automático DHCP)" : _factoryPrimaryDns;
            if (SecondaryDnsTextBox != null)
                SecondaryDnsTextBox.Text = string.IsNullOrEmpty(_factorySecondaryDns) ? "" : _factorySecondaryDns;

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

    private void DnsProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DnsProviderCombo.SelectedItem is not DnsPreset preset) return;
        if (PrimaryDnsTextBox == null || SecondaryDnsTextBox == null) return;

        bool isCustom = preset.Name == "Personalizado";
        
        // Solo "Personalizado" es editable; los demás presets son solo lectura
        PrimaryDnsTextBox.IsReadOnly = !isCustom;
        SecondaryDnsTextBox.IsReadOnly = !isCustom;

        switch (preset.Name)
        {
            case "Personalizado":
                // Mostrar el DNS actual del sistema para que el usuario lo vea y edite
                PrimaryDnsTextBox.Text = _factoryPrimaryDns;
                SecondaryDnsTextBox.Text = _factorySecondaryDns;
                break;
            case "DNS de fábrica (router)":
                // Mostrar lo que tiene el router/sistema (solo lectura)
                PrimaryDnsTextBox.Text = _factoryPrimaryDns;
                SecondaryDnsTextBox.Text = _factorySecondaryDns;
                break;
            default:
                // Presets predefinidos: mostrar DNS fijos (solo lectura)
                PrimaryDnsTextBox.Text = preset.PrimaryDns;
                SecondaryDnsTextBox.Text = preset.SecondaryDns;
                break;
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
            ApplyDnsResultText.Text = "No se pudo abrir conexiones de red. Ejecute 'ncpa.cpl' manualmente (Win+R → ncpa.cpl).";
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
            var primaryDns = PrimaryDnsTextBox.Text.Trim();
            var secondaryDns = SecondaryDnsTextBox.Text.Trim();

            if (string.IsNullOrEmpty(primaryDns))
            {
                ApplyDnsResultText.Text = "Seleccione o ingrese un DNS válido antes de aplicar.";
                return;
            }

            ApplyDnsButton.IsEnabled = false;
            ApplyDnsResultText.Text = "Buscando adaptador de red...";

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
                ApplyDnsResultText.Text = "No se encontró ningún adaptador de red activo. Se abrirá la configuración de red.";
                _loggingService.LogError("No se pudo encontrar ningún adaptador de red activo.");
                FallbackToNcpaCpl();
                return;
            }

            ApplyDnsResultText.Text = $"Configurando DNS en {adapterName}...";

            var result = await _networkService.SetDnsServersAsync(adapterName, primaryDns, secondaryDns);

            ApplyDnsResultText.Text = result.Success
                ? "Servidores DNS configurados correctamente."
                : $"Error al configurar DNS: {result.Output}";
        }
        catch (Exception ex)
        {
            ApplyDnsResultText.Text = $"Error: {ex.Message}";
            _loggingService.LogError("Error en ApplyDnsButton_Click", ex);
        }
        finally
        {
            ApplyDnsButton.IsEnabled = true;
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
            RunDnsTestButton.IsEnabled = false;
            ClearDnsResultsButton.IsEnabled = false;
            CloseDnsResultsButton.IsEnabled = false;

            // Mostrar la pestaña de resultados y arrancar vacía
            DnsResultsTab.Visibility = Visibility.Visible;
            DnsTestResultsPanel.Children.Clear();
            DnsTestStatusText.Text = "Ejecutando test de DNS...";

            // Probar el DNS personalizado (si está definido)
            var customPrimary = PrimaryDnsTextBox.Text.Trim();
            var customSecondary = SecondaryDnsTextBox.Text.Trim();
            
            if (!string.IsNullOrEmpty(customPrimary) || !string.IsNullOrEmpty(customSecondary))
            {
                DnsTestStatusText.Text = "Probando DNS personalizado...";
                double primaryLatency = -1, secondaryLatency = -1;
                
                if (!string.IsNullOrEmpty(customPrimary))
                {
                    primaryLatency = await _networkService.TestDnsLatencyAsync(customPrimary);
                }
                if (!string.IsNullOrEmpty(customSecondary) && customSecondary != customPrimary)
                {
                    secondaryLatency = await _networkService.TestDnsLatencyAsync(customSecondary);
                }
                
                AddDnsResultCard("Personalizado", customPrimary, customSecondary, primaryLatency, secondaryLatency);
            }

            // Probar DNS manuales agregados
            foreach (var manualDns in _manualDnsList)
            {
                DnsTestStatusText.Text = $"Probando DNS manual ({manualDns})...";
                var latency = await _networkService.TestDnsLatencyAsync(manualDns);
                AddDnsResultCard("Manual", manualDns, "", latency, -1);
            }

            // Probar cada proveedor preestablecido (excepto Personalizado y DNS de fábrica, para no duplicar)
            var presets = DnsPresets.Where(p => p.Name != "Personalizado" && p.Name != "DNS de fábrica (router)").ToList();
            int tested = 0;
            foreach (var preset in presets)
            {
                tested++;
                DnsTestStatusText.Text = $"Probando ({tested}/{presets.Count}): {preset.Name}...";
                double primaryLatency = -1, secondaryLatency = -1;
                
                if (!string.IsNullOrEmpty(preset.PrimaryDns))
                {
                    primaryLatency = await _networkService.TestDnsLatencyAsync(preset.PrimaryDns);
                }
                if (!string.IsNullOrEmpty(preset.SecondaryDns) && preset.SecondaryDns != preset.PrimaryDns)
                {
                    secondaryLatency = await _networkService.TestDnsLatencyAsync(preset.SecondaryDns);
                }
                
                AddDnsResultCard(preset.Name, preset.PrimaryDns, preset.SecondaryDns, primaryLatency, secondaryLatency);
            }

            DnsTestStatusText.Text = "Test de DNS completado.";
        }
        catch (Exception ex)
        {
            DnsTestStatusText.Text = $"Error: {ex.Message}";
            _loggingService.LogError("Error en RunDnsTestButton_Click", ex);
        }
        finally
        {
            RunDnsTestButton.IsEnabled = true;
            ClearDnsResultsButton.IsEnabled = true;
            CloseDnsResultsButton.IsEnabled = true;
            // Evitar que el foco salte a los campos al deshabilitar el botón
            RunDnsTestButton.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>
    /// Agrega una card de resultado en tiempo real (apenas termina de probar cada DNS),
    /// insertándola en la posición que le corresponde por latencia (mejores primero) y
    /// hace auto-scroll al resultado más reciente dentro de la pestaña.
    /// </summary>
    private void AddDnsResultCard(string name, string primary, string secondary, double primaryLatency, double secondaryLatency)
    {
        var card = BuildLatencyCard(name, primary, secondary, primaryLatency, secondaryLatency);
        InsertDnsResultSorted(card, BestDnsLatency(primaryLatency, secondaryLatency));

        // Después del layout, scrollear al último resultado agregado
        DispatcherQueue.TryEnqueue(() =>
        {
            if (DnsResultsScroll != null)
                DnsResultsScroll.ChangeView(null, DnsResultsScroll.ScrollableHeight, null);
        });
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

    private void ClearDnsResultsButton_Click(object sender, RoutedEventArgs e)
    {
        DnsTestResultsPanel.Children.Clear();
        DnsTestStatusText.Text = "Resultados limpiados. Ejecutá el test para volver a medir.";
    }

    private void CloseDnsResultsButton_Click(object sender, RoutedEventArgs e)
    {
        DnsResultsTab.Visibility = Visibility.Collapsed;
        DnsTestResultsPanel.Children.Clear();
        DnsTestStatusText.Text = "";
    }

    private Border BuildLatencyCard(string name, string primary, string secondary, double primaryLatency, double secondaryLatency)
    {
        var border = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };

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

        border.Child = panel;
        return border;
    }

    private async void FlushDnsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FlushDnsButton.IsEnabled = false;
            FlushDnsResultText.Text = "Ejecutando flush DNS...";
            var result = await _networkService.FlushDnsAsync();
            FlushDnsResultText.Text = result.Success
                ? "Caché DNS vaciada correctamente."
                : $"Error al vaciar la caché DNS: {result.Output}";
        }
        catch (Exception ex)
        {
            FlushDnsResultText.Text = $"Error: {ex.Message}";
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

    private record PacketLossServer(string Name, string Host);
}
