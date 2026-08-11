using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel.DataTransfer;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class ReparacionPage : Page
{
    private readonly ILoggingService _loggingService;
    private readonly IRepairService _repairService;
    private bool _dataLoaded;

    // Las cards de herramientas se mantienen oscuras en ambos temas (paneles oscuros),
    // por eso su título lleva texto claro explícito y siempre legible.
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush LightTextBrush = new(Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xED));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush CardBrush = new(Windows.UI.Color.FromArgb(255, 0x26, 0x2A, 0x31));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(255, 255, 193, 7));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush MutedBrush = new(Windows.UI.Color.FromArgb(255, 150, 150, 150));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(255, 138, 180, 248));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SuccessBrush = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xAF, 0x50));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(255, 0xF0, 0x61, 0x6D));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush AccentForegroundBrush = new(Windows.UI.Color.FromArgb(255, 16, 20, 24));

    public ReparacionPage()
    {
        try
        {
            InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            _loggingService = App.Services.GetRequiredService<ILoggingService>();
            _repairService = App.Services.GetRequiredService<IRepairService>();
            Loaded += OnLoaded;
        }
        catch (Exception ex)
        {
            _loggingService?.LogError($"Error en constructor ReparacionPage: {ex}", ex);
            try
            {
                var errorText = new TextBlock
                {
                    Text = $"Error al inicializar la página: {ex.Message}",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 100, 100)),
                    TextWrapping = TextWrapping.Wrap
                };
                ToolsPanel.Children.Add(errorText);
            }
            catch { }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_dataLoaded) return;
        try
        {
            LoadTools();
            _dataLoaded = true;
            _loggingService.LogInfo("ReparacionPage cargada correctamente");
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error en OnLoaded ReparacionPage: {ex}", ex);
        }
    }

    private void LoadTools()
    {
        ToolsPanel.Children.Clear();

        var tools = _repairService.GetAvailableTools();

        // Agrupar por categoría
        var systemTools = tools.Where(t => t.Id is "sfc" or "dism" or "chkdsk" or "component_store").ToList();
        var networkTools = tools.Where(t => t.Id is "reset_network" or "flush_dns").ToList();
        var otherTools = tools.Where(t => t.Id is "repair_store" or "repair_profile").ToList();

        // Sección: Reparación del sistema
        ToolsPanel.Children.Add(BuildSectionHeader(Symbol.Repair, "Reparación del sistema", "Herramientas que reparan archivos y componentes de Windows"));
        foreach (var tool in systemTools)
        {
            ToolsPanel.Children.Add(BuildToolCard(tool));
        }

        // Sección: Red
        ToolsPanel.Children.Add(BuildSectionHeader(Symbol.Globe, "Red", "Herramientas para solucionar problemas de conectividad"));
        foreach (var tool in networkTools)
        {
            ToolsPanel.Children.Add(BuildToolCard(tool));
        }

        // Sección: Otro
        ToolsPanel.Children.Add(BuildSectionHeader(Symbol.More, "Otro", "Otras herramientas de reparación"));
        foreach (var tool in otherTools)
        {
            ToolsPanel.Children.Add(BuildToolCard(tool));
        }

        _loggingService.LogInfo($"ReparacionPage: {tools.Count} herramientas cargadas");
    }

    private StackPanel BuildSectionHeader(Symbol icon, string title, string subtitle)
    {
        var header = new StackPanel { Spacing = 2, Margin = new Thickness(0, 12, 0, 4) };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(new SymbolIcon
        {
            Symbol = icon,
            Foreground = AccentBrush,
            Width = 20,
            Height = 20
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 18
        });
        header.Children.Add(titleRow);

        header.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(MutedBrush.Color)
        });
        return header;
    }

    private Border BuildToolCard(RepairToolInfo tool)
    {
        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16)
        };

        var root = new StackPanel { Spacing = 8 };

        // Título
        root.Children.Add(new TextBlock
        {
            Text = tool.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 15,
            Foreground = LightTextBrush
        });

        // Descripción
        root.Children.Add(new TextBlock
        {
            Text = tool.Description,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(MutedBrush.Color)
        });

        // Compatibilidad y administrador
        var infoPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        infoPanel.Children.Add(new TextBlock
        {
            Text = $"Compatibilidad: {tool.Compatibility}",
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(MutedBrush.Color)
        });
        if (tool.RequiresAdmin)
        {
            infoPanel.Children.Add(new TextBlock
            {
                Text = "🔒 Requiere admin",
                FontSize = 12,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Emoji"),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(WarningBrush.Color)
            });
        }
        root.Children.Add(infoPanel);

        // Advertencia de duración
        if (tool.IsLongRunning)
        {
            root.Children.Add(new TextBlock
            {
                Text = "⏱️ Se abrirá una consola: puede tardar varios minutos",
                FontSize = 12,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Emoji"),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(WarningBrush.Color),
                FontStyle = Windows.UI.Text.FontStyle.Italic
            });
        }

        // Botón de ejecución (mismo estilo accent que las acciones de RedPage)
        var button = new Button
        {
            Content = "Ejecutar",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 120,
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(16, 8, 16, 8),
            CornerRadius = new CornerRadius(6),
            Background = AccentBrush,
            Foreground = AccentForegroundBrush,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Tag = tool.Id
        };
        button.Click += async (s, e) => await ToolButton_Click(tool, button);
        root.Children.Add(button);

        card.Child = root;
        return card;
    }

    private void ShowNotification(string message, string type)
    {
        try
        {
            var notificationBar = new Microsoft.UI.Xaml.Controls.InfoBar
            {
                Message = message,
                IsOpen = true,
                Severity = type == "success"
                    ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success
                    : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
                Margin = new Thickness(12),
                CornerRadius = new CornerRadius(8)
            };

            if (NotificationPanel != null)
            {
                NotificationPanel.Children.Add(notificationBar);

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, e) =>
                {
                    notificationBar.IsOpen = false;
                    NotificationPanel.Children.Remove(notificationBar);
                    timer.Stop();
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error mostrando notificación: {ex.Message}", ex);
        }
    }

    private async Task ToolButton_Click(RepairToolInfo tool, Button button)
    {
        // Las herramientas que se ejecutan como comandos del sistema se abren en una consola real
        // con su progreso nativo (SFC/DISM/CHKDSK no transmiten su progreso si se redirige la salida).
        if (tool.Id is "sfc" or "dism" or "chkdsk" or "component_store" or "reset_network" or "flush_dns")
        {
            LaunchInConsole(tool);
            return;
        }

        try
        {
            button.IsEnabled = false;
            button.Content = "Ejecutando...";

            // Reparar Windows Store transmite su progreso en vivo a la consola embebida.
            if (tool.Id == "repair_store")
            {
                ConsolePanel.Visibility = Visibility.Visible;
                AppendConsole($"▶ Ejecutando {tool.Name}...", ConsoleStatus.Running);
            }

            RepairResult result = tool.Id switch
            {
                "repair_store" => await _repairService.RepairStoreAsync(new Progress<string>(line => AppendConsole(line, ConsoleStatus.Neutral))),
                "repair_profile" => await _repairService.RepairUserProfileAsync(),
                _ => new RepairResult(false, "Herramienta no reconocida")
            };

            if (tool.Id == "repair_store")
            {
                AppendConsole(result.Success ? $"✓ {tool.Name} completado." : $"✗ {tool.Name}: {result.Message}", result.Success ? ConsoleStatus.Applied : ConsoleStatus.Error);
            }

            // Estas herramientas no abren consola: muestran el detalle en un diálogo
            // para que el resultado sea visible (antes el aviso de 3s pasaba desapercibido).
            if (tool.Id is "repair_store" or "repair_profile")
            {
                await ShowResultDialogAsync(tool.Name, result);
            }
            else if (result.Success)
            {
                ShowNotification($"{tool.Name} - Operación exitosa", "success");
            }
            else
            {
                ShowNotification($"{tool.Name} - Error", "error");
            }

            _loggingService.LogInfo($"Herramienta {tool.Id}: {(result.Success ? "Éxito" : "Fallo")}");
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error ejecutando herramienta {tool.Id}", ex);
            ShowNotification($"{tool.Name} - Error: {ex.Message}", "error");
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = "Ejecutar";
        }
    }

    /// <summary>
    /// Muestra el resultado de una herramienta en un diálogo con el detalle completo.
    /// </summary>
    private async Task ShowResultDialogAsync(string toolName, RepairResult result)
    {
        if (XamlRoot == null) return;

        var panel = new StackPanel { Spacing = 12, MaxWidth = 560 };

        panel.Children.Add(new TextBlock
        {
            Text = result.Message,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush((result.Success ? SuccessBrush : ErrorBrush).Color)
        });

        if (!string.IsNullOrWhiteSpace(result.Details))
        {
            var detailsText = new TextBlock
            {
                Text = result.Details,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 11.5,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(MutedBrush.Color)
            };
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
                Content = detailsText
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = toolName,
            Content = panel,
            CloseButtonText = "Cerrar"
        };
        await dialog.ShowAsync();
    }

    // ====== CONSOLA EN VIVO ======

    private enum ConsoleStatus
    {
        Running,
        Applied,
        Error,
        Neutral
    }

    private void AppendConsole(string message, ConsoleStatus status)
    {
        if (ConsolePanel == null || ConsoleText == null || ConsoleScroll == null) return;

        var wasCollapsed = ConsolePanel.Visibility == Visibility.Collapsed;
        ConsolePanel.Visibility = Visibility.Visible;

        // La consola vive al final del ScrollViewer: al aparecer la primera vez, scrollear
        // hasta ella para que el usuario vea el feedback de la reparación que ejecutó.
        if (wasCollapsed)
            ConsolePanel.StartBringIntoView();

        var (prefix, color) = status switch
        {
            ConsoleStatus.Running => ("▶", AccentBrush),
            ConsoleStatus.Applied => ("✓", SuccessBrush),
            ConsoleStatus.Error => ("✗", ErrorBrush),
            _ => ("·", MutedBrush)
        };

        if (ConsoleText.Inlines.Count > 0)
            ConsoleText.Inlines.Add(new Run { Text = Environment.NewLine });
        ConsoleText.Inlines.Add(new Run { Text = $"{prefix} {message}", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color.Color) });

        // Limitar el crecimiento para no degradar el rendimiento tras muchas líneas.
        const int maxInlineCount = 1200;
        while (ConsoleText.Inlines.Count > maxInlineCount)
        {
            ConsoleText.Inlines.RemoveAt(0);
            if (ConsoleText.Inlines.Count > 0)
                ConsoleText.Inlines.RemoveAt(0);
        }

        ConsoleScroll.UpdateLayout();
        ConsoleScroll.ChangeView(null, double.MaxValue, null, true);
    }

    private void ClearConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        ConsoleText?.Inlines.Clear();
    }

    private async void CopyConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConsoleText == null) return;
        var sb = new System.Text.StringBuilder();
        foreach (var inline in ConsoleText.Inlines)
        {
            if (inline is Run run)
                sb.Append(run.Text);
        }
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetText(sb.ToString());
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error copiando consola", ex);
        }
    }

    /// <summary>
    /// Abre una consola real (CMD) elevada con la herramienta nativa para ver el progreso real.
    /// </summary>
    private void LaunchInConsole(RepairToolInfo tool)
    {
        var commandLine = tool.Id switch
        {
            "sfc" => "sfc /scannow",
            "dism" => "dism /Online /Cleanup-Image /RestoreHealth",
            "chkdsk" => "chkdsk C: /scan",
            "component_store" => "dism /Online /Cleanup-Image /StartComponentCleanup /ResetBase",
            "reset_network" => "netsh winsock reset && netsh int ip reset",
            "flush_dns" => "ipconfig /flushdns",
            _ => tool.Id
        };

                // /c ejecuta el comando; al final deja la ventana abierta con "pause" para poder leer el resultado.
        // IMPORTANTE: no usar "title" al inicio, porque consume el resto de la línea y el comando no se ejecuta.
        //
        // SFC y DISM necesitan que estén corriendo (y habilidades) servicios que los tweaks de desbloat suelen
        // dejar parados/deshabilitados: BITS, Windows Update (wuauserv), CryptSvc y TrustedInstaller.
        // Si no están activos, SFC falla con "Protección de recursos de Windows no pudo iniciar el
        // servicio de reparación". Por eso, para esas herramientas, la consola elevada primero (re)habita
        // y arranca esos servicios de forma durable (sc config ... start= demand / auto) antes del comando.
        var servicePrep = (tool.Id == "sfc" || tool.Id == "dism")
            ? "echo Iniciando servicios requeridos (BITS/Actualizacion/TrustedInstaller)... & "
              + "sc config trustedinstaller start= demand >nul 2>&1 & "
              + "sc config wuauserv start= demand >nul 2>&1 & "
              + "sc config bits start= demand >nul 2>&1 & "
              + "sc config cryptsvc start= auto >nul 2>&1 & "
              + "net start bits >nul 2>&1 & "
              + "net start wuauserv >nul 2>&1 & "
              + "net start cryptsvc >nul 2>&1 & "
              + "net start trustedinstaller >nul 2>&1 & "
            : "";
        var args = $"/c {servicePrep}{commandLine} & echo. & echo --- TERMINADO: revise el resultado de arriba --- & pause";

        try
        {
            // IMPORTANTE: usar cmd.exe del System32 NATIVO (64 bits). Si la app es x86,
            // Environment.SystemDirectory apunta a SysWOW64 y el cmd.exe/sfc.exe de 32 bits
            // no pueden iniciar el servicio de reparación (TrustedInstaller) en Windows de 64 bits.
            // El FileName se resuelve vía Sysnative (visible para el proceso x86 que lo lanza),
            // pero el directorio de trabajo debe ser el System32 REAL: Sysnative no es válido
            // como CWD para el cmd.exe de 64 bits ("El directorio actual no es válido").
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(RepairService.NativeSystemDirectory, "cmd.exe"),
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = false,
                WorkingDirectory = RepairService.SystemWorkingDirectory
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"No se pudo abrir la consola para {tool.Id}", ex);
            ShowNotification($"{tool.Name} - No se pudo abrir la consola elevada: {ex.Message}", "error");
            return;
        }

        ShowNotification($"{tool.Name} - Consola abierta", "success");
    }
}
