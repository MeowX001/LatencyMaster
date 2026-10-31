using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LatencyMaster;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<TargetEntry> _targets = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _settingsPath;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private int _intervalSeconds = 1;
    private TargetEntry? _gatewayTarget;
    private bool _settingsReady;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Latency Master";
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LatencyMaster", "targets.json");
        LoadTargets();
        _settingsReady = true;
        RenderTargets();
        Closed += (_, _) => _shutdown.Cancel();
        _ = PollLoopAsync(_shutdown.Token);
    }

    private void LoadTargets()
    {
        var loadedTargets = false;
        try
        {
            if (File.Exists(_settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                List<TargetSettings>? saved;
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    // Migrate the target-list-only format used by earlier builds.
                    saved = JsonSerializer.Deserialize<List<TargetSettings>>(document.RootElement.GetRawText());
                }
                else
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(document.RootElement.GetRawText());
                    saved = settings?.Targets;
                    if (settings is not null) SelectInterval(settings.IntervalSeconds);
                }
                if (saved is { Count: > 0 })
                {
                    foreach (var item in saved)
                    {
                        // Earlier builds stored the gateway in the user list. The live pinned row replaces it.
                        if (item.Protocol == ProbeProtocol.Icmp && IPAddress.TryParse(item.Host, out _) &&
                            (item.Name.Equals("Gateway", StringComparison.OrdinalIgnoreCase) ||
                             item.Name.Equals("Default gateway", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        _targets.Add(new TargetEntry(item.Name, item.Host, item.Protocol, item.Port));
                    }
                    loadedTargets = true;
                }
            }
        }
        catch { /* Start with the built-in list if settings are unreadable. */ }

        if (!loadedTargets)
        {
            _targets.Add(new TargetEntry("Cloudflare", "1.1.1.1", ProbeProtocol.Icmp, 443));
            _targets.Add(new TargetEntry("Google", "google.com", ProbeProtocol.Https, 443));
            _targets.Add(new TargetEntry("GitHub", "github.com", ProbeProtocol.Https, 443));
        }
        UpdateGatewayTarget();
        SaveTargets();
    }

    private void SaveTargets()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var data = _targets.Where(t => !t.IsGateway).Select(t => new TargetSettings(t.Name, t.Host, t.Protocol, t.Port)).ToList();
            var settings = new AppSettings(_intervalSeconds, data);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"Could not save targets: {ex.Message}";
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UpdateGatewayTarget();
            var batch = _targets.ToArray();
            if (batch.Length > 0)
            {
                var results = await Task.WhenAll(batch.Select(t => ProbeAsync(t, cancellationToken)));
                if (cancellationToken.IsCancellationRequested) break;
                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var result in results)
                    {
                        result.Target.SentPackets++;
                        if (result.LatencyMs.HasValue) result.Target.ReceivedPackets++;
                        result.Target.LatencyMs = result.LatencyMs;
                        result.Target.ResolvedIp = result.ResolvedIp;
                        result.Target.Status = result.Status;
                        if (result.LatencyMs.HasValue)
                        {
                            result.Target.History.Enqueue(result.LatencyMs.Value);
                            while (result.Target.History.Count > 24) result.Target.History.Dequeue();
                        }
                    }
                    RenderTargets();
                    FooterStatus.Text = $"Monitoring {_targets.Count} target{(_targets.Count == 1 ? "" : "s")} · Updated {DateTime.Now:t}";
                });
            }
            try { await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<ProbeResult> ProbeAsync(TargetEntry target, CancellationToken appToken)
    {
        if (target.IsGateway && string.IsNullOrWhiteSpace(target.Host))
            return new(target, null, null, "No gateway");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var watch = Stopwatch.StartNew();
        try
        {
            string? ip = null;
            switch (target.Protocol)
            {
                case ProbeProtocol.Icmp:
                {
                    var addresses = await Dns.GetHostAddressesAsync(target.Host, timeout.Token);
                    ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString()
                         ?? addresses.FirstOrDefault()?.ToString();
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(target.Host, 3500);
                    return reply.Status == IPStatus.Success
                        ? new(target, reply.RoundtripTime, NormalizeAddress(reply.Address?.ToString()) ?? ip, "Online")
                        : new(target, null, ip, reply.Status == IPStatus.TimedOut ? "Timeout" : reply.Status.ToString());
                }
                case ProbeProtocol.Tcp:
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(target.Host, target.Port, timeout.Token);
                    watch.Stop();
                    ip = NormalizeAddress((client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString());
                    return new(target, Math.Max(1, watch.ElapsedMilliseconds), ip, "Online");
                }
                case ProbeProtocol.Https:
                {
                    var url = target.Host.Contains("://", StringComparison.Ordinal) ? target.Host : $"https://{target.Host}/";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                        return new(target, null, null, "Invalid URL");
                    using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    watch.Stop();
                    ip = Dns.GetHostAddresses(uri.DnsSafeHost).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
                    return new(target, Math.Max(1, watch.ElapsedMilliseconds), ip, $"HTTP {(int)response.StatusCode}");
                }
                default:
                    return new(target, null, null, "Unknown protocol");
            }
        }
        catch (OperationCanceledException) when (!appToken.IsCancellationRequested)
        {
            return new(target, null, null, "Timeout");
        }
        catch (Exception ex)
        {
            return new(target, null, null, ex is SocketException or PingException ? "Unreachable" : "Check failed");
        }
    }

    private void RenderTargets()
    {
        TargetsPanel.Children.Clear();
        foreach (var target in _targets)
        {
            var row = new Grid
            {
                Margin = new Thickness(14, 12, 10, 12),
                ColumnSpacing = 12,
                Background = new SolidColorBrush(Color.FromArgb(255, 27, 30, 32))
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });

            var glyph = target.Protocol switch { ProbeProtocol.Icmp => "◉", ProbeProtocol.Tcp => "⇄", _ => "◎" };
            var icon = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(255, 40, 46, 49)),
                Child = new TextBlock { Text = glyph, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#8DD3B0") }
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var title = new TextBlock { Text = target.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush("#F0F2F3"), TextTrimming = TextTrimming.CharacterEllipsis };
            var shownAddress = target.ResolvedIp ?? target.Host;
            var hostLabel = HostLabel(target.Host);
            var hostPart = string.IsNullOrWhiteSpace(hostLabel) || string.Equals(shownAddress, hostLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $"   ·   {hostLabel}";
            var detail = new TextBlock { Text = $"{shownAddress}{hostPart}   ·   {ProtocolLabel(target)}", FontSize = 12, Foreground = Brush("#92999F"), TextTrimming = TextTrimming.CharacterEllipsis };
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4, Children = { title, detail } };
            Grid.SetColumn(labels, 1);
            row.Children.Add(labels);

            var lost = target.SentPackets - target.ReceivedPackets;
            var lossPercent = target.SentPackets == 0 ? 0 : (double)lost * 100 / target.SentPackets;
            var packets = new TextBlock
            {
                Text = $"Sent: {target.SentPackets:N0}\nReceived: {target.ReceivedPackets:N0}\nLost: {lost:N0} ({lossPercent:0.#}%)",
                FontSize = 12,
                LineHeight = 18,
                Foreground = lost == 0 ? Brush("#B7C0C5") : Brush("#E7A442"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(packets, 2);
            row.Children.Add(packets);

            var spark = new TextBlock { Text = BuildSparkline(target.History), FontSize = 22, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), Foreground = Brush("#647079"), VerticalAlignment = VerticalAlignment.Center, CharacterSpacing = 30 };
            Grid.SetColumn(spark, 3);
            row.Children.Add(spark);

            var latency = new TextBlock
            {
                Text = target.LatencyMs is long ms ? $"{ms} ms" : target.Status,
                FontSize = target.LatencyMs.HasValue ? 20 : 13,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = target.LatencyMs is long value ? LatencyBrush(value) : Brush(target.Status == "Timeout" || target.Status == "Unreachable" ? "#E77C83" : "#A5ADB4"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(latency, 4);
            row.Children.Add(latency);

            if (!target.IsGateway)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                var index = _targets.IndexOf(target);
                var firstMovableIndex = _gatewayTarget is null ? 0 : 1;
                actions.Children.Add(CreateActionButton("↑", "Move up", index <= firstMovableIndex, () => MoveTarget(target, -1)));
                actions.Children.Add(CreateActionButton("↓", "Move down", index >= _targets.Count - 1, () => MoveTarget(target, 1)));
                actions.Children.Add(CreateActionButton("×", "Remove target", false, () =>
                {
                    _targets.Remove(target);
                    SaveTargets();
                    RenderTargets();
                }));
                Grid.SetColumn(actions, 5);
                row.Children.Add(actions);
            }

            TargetsPanel.Children.Add(new Border { CornerRadius = new CornerRadius(10), Child = row });
        }
    }

    private async void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "For example: Home router", MinWidth = 300 };
        var hostBox = new TextBox { PlaceholderText = "IP address, hostname, or URL", MinWidth = 300 };
        var protocolBox = new ComboBox { SelectedIndex = 0, MinWidth = 300 };
        protocolBox.Items.Add(new ComboBoxItem { Content = "ICMP ping" });
        protocolBox.Items.Add(new ComboBoxItem { Content = "TCP connection" });
        protocolBox.Items.Add(new ComboBoxItem { Content = "HTTPS request" });
        var portBox = new NumberBox { Value = 443, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Header = "TCP port (used for TCP checks)" };
        var fields = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Name" }, nameBox, new TextBlock { Text = "Address" }, hostBox, new TextBlock { Text = "Check method" }, protocolBox, portBox } };
        var dialog = new ContentDialog
        {
            Title = "Add a target",
            Content = fields,
            PrimaryButtonText = "Add target",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var host = hostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            FooterStatus.Text = "Enter an IP address, hostname, or URL first.";
            return;
        }
        var selected = protocolBox.SelectedIndex switch { 1 => ProbeProtocol.Tcp, 2 => ProbeProtocol.Https, _ => ProbeProtocol.Icmp };
        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? host : nameBox.Text.Trim();
        _targets.Add(new TargetEntry(name, host, selected, (int)portBox.Value));
        SaveTargets();
        RenderTargets();
    }

    private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var seconds))
        {
            _intervalSeconds = seconds;
            if (_settingsReady) SaveTargets();
        }
    }

    private void SelectInterval(int seconds)
    {
        var items = IntervalCombo.Items.OfType<ComboBoxItem>().ToList();
        var index = items.FindIndex(item => int.TryParse(item.Tag?.ToString(), out var value) && value == seconds);
        if (index >= 0)
            IntervalCombo.SelectedIndex = index;
        else
            _intervalSeconds = 1;
    }

    private Button CreateActionButton(string content, string tooltip, bool isDisabled, Action action)
    {
        var button = new Button
        {
            Content = content,
            FontSize = content == "×" ? 19 : 15,
            Width = 28,
            Height = 30,
            Padding = new Thickness(0),
            IsEnabled = !isDisabled,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = Brush(isDisabled ? "#4F565B" : "#AAB1B7")
        };
        button.Click += (_, _) => action();
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private void MoveTarget(TargetEntry target, int offset)
    {
        var currentIndex = _targets.IndexOf(target);
        if (currentIndex < 0) return;
        var firstMovableIndex = _gatewayTarget is null ? 0 : 1;
        var newIndex = currentIndex + offset;
        if (newIndex < firstMovableIndex || newIndex >= _targets.Count) return;
        _targets.Move(currentIndex, newIndex);
        SaveTargets();
        RenderTargets();
    }

    private void UpdateGatewayTarget()
    {
        var gatewayAddress = FindDefaultGateway();
        if (_gatewayTarget is null)
        {
            _gatewayTarget = new TargetEntry("Default gateway", gatewayAddress?.ToString() ?? string.Empty, ProbeProtocol.Icmp, 443, true);
            _targets.Insert(0, _gatewayTarget);
        }
        else if (!string.Equals(_gatewayTarget.Host, gatewayAddress?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            _gatewayTarget.Host = gatewayAddress?.ToString() ?? string.Empty;
            _gatewayTarget.ResolvedIp = null;
            _gatewayTarget.LatencyMs = null;
            _gatewayTarget.History.Clear();
            _gatewayTarget.Status = gatewayAddress is null ? "No gateway" : "Checking…";
        }

        var gatewayIndex = _targets.IndexOf(_gatewayTarget);
        if (gatewayIndex > 0) _targets.Move(gatewayIndex, 0);
    }

    private static IPAddress? FindDefaultGateway()
    {
        try
        {
            const uint internetProbeAddress = 0x08080808; // 8.8.8.8, used to ask Windows which interface owns the default route.
            if (GetBestInterface(internetProbeAddress, out var interfaceIndex) == 0)
            {
                var routeInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.GetIPProperties().GetIPv4Properties()?.Index == interfaceIndex);
                var gateway = routeInterface?.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (gateway is not null) return gateway;
            }

            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);

    private static string? NormalizeAddress(string? address)
    {
        if (IPAddress.TryParse(address, out var parsed) && parsed.IsIPv4MappedToIPv6)
            return parsed.MapToIPv4().ToString();
        return address;
    }

    private static string HostLabel(string host)
    {
        if (Uri.TryCreate(host.Contains("://", StringComparison.Ordinal) ? host : $"https://{host}", UriKind.Absolute, out var uri))
            return uri.DnsSafeHost;
        return host;
    }

    private static string ProtocolLabel(TargetEntry target) => target.Protocol switch
    {
        ProbeProtocol.Icmp => "ICMP",
        ProbeProtocol.Tcp => $"TCP :{target.Port}",
        _ => "HTTPS"
    };

    private static string BuildSparkline(IEnumerable<long> history)
    {
        var values = history.ToArray();
        if (values.Length == 0) return "────────";
        const string levels = "▁▂▃▄▅▆▇█";
        var low = values.Min();
        var high = values.Max();
        if (high == low) return new string('▄', Math.Min(12, values.Length));
        return new string(values.TakeLast(12).Select(v => levels[(int)Math.Clamp((v - low) * (levels.Length - 1) / (high - low), 0, levels.Length - 1)]).ToArray());
    }

    private static SolidColorBrush LatencyBrush(long ms) => Brush(ms < 70 ? "#55D68B" : ms < 120 ? "#C2D85C" : ms < 180 ? "#E7A442" : "#EF6B75");
    private static SolidColorBrush Brush(string hex) => new(AppColorUtil.ToColor(hex));

    private sealed class TargetEntry(string name, string host, ProbeProtocol protocol, int port, bool isGateway = false)
    {
        public string Name { get; } = name;
        public string Host { get; set; } = host;
        public ProbeProtocol Protocol { get; } = protocol;
        public int Port { get; } = port;
        public bool IsGateway { get; } = isGateway;
        public string? ResolvedIp { get; set; }
        public long? LatencyMs { get; set; }
        public long SentPackets { get; set; }
        public long ReceivedPackets { get; set; }
        public string Status { get; set; } = "Checking…";
        public Queue<long> History { get; } = new();
    }

    private sealed record TargetSettings(string Name, string Host, ProbeProtocol Protocol, int Port);
    private sealed record AppSettings(int IntervalSeconds, List<TargetSettings> Targets);
    private sealed record ProbeResult(TargetEntry Target, long? LatencyMs, string? ResolvedIp, string Status);
    private enum ProbeProtocol { Icmp, Tcp, Https }
}

internal static class AppColorUtil
{
    public static Color ToColor(string hex)
    {
        var value = hex.TrimStart('#');
        return Color.FromArgb(255,
            Convert.ToByte(value[..2], 16), Convert.ToByte(value.Substring(2, 2), 16), Convert.ToByte(value.Substring(4, 2), 16));
    }
}
