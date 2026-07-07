using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Interop;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace IpShow;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string PlaceholderIp = "--";
    private const string PlaceholderLocation = "—";
    private const string PlaceholderDns = "DNS —";
    private const string ChinaCountryCode = "CN";
    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "IpShow";
    private const string SettingsRegistryKey = @"Software\IpShow";
    private const string RefreshIntervalValueName = "RefreshIntervalSeconds";
    private const string AlwaysOnTopValueName = "AlwaysOnTop";
    private const string WindowLayerValueName = "WindowLayer";
    private const string DisplayModeValueName = "DisplayMode";
    private const string ShowTrayIconValueName = "ShowTrayIcon";
    private const string TextThemeValueName = "TextTheme";
    private const int DefaultRefreshSeconds = 10;
    private static readonly string GeoCityDbPath = Path.Combine(AppContext.BaseDirectory, "GeoIP", "GeoLite2-City.mmdb");
    private static readonly Lazy<DatabaseReader?> GeoCityReader = new(() => CreateGeoReader(GeoCityDbPath));
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _systemStatsTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayShowWindowMenu;
    private readonly Forms.ToolStripMenuItem _trayTrayOnlyMenu;
    private readonly Forms.ToolStripMenuItem _trayRefreshMenu;
    private readonly Forms.ToolStripMenuItem _trayExitMenu;
    private DockMode _dockMode = DockMode.Free;
    private WindowLayer _windowLayer = WindowLayer.Top;
    private DisplayMode _displayMode = DisplayMode.Desktop;
    private bool _showTrayIcon = true;
    private bool _isRefreshing;
    private bool _pendingRefresh;
    private NetworkTotals? _lastNetworkTotals;
    private CpuTimes? _lastCpuTimes;
    private readonly System.Windows.Media.Brush _locationChinaBrush;
    private readonly System.Windows.Media.Brush _locationOtherBrush;
    private readonly System.Windows.Media.Brush _locationUnknownBrush;
    private readonly System.Windows.Media.Brush _balancedStatusTextBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF3, 0xF6));
    private readonly System.Windows.Media.Brush _lightStatusTextBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
    private readonly System.Windows.Media.Brush _darkStatusTextBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x24, 0x2A));
    private string _currentLocationText = PlaceholderLocation;
    private System.Windows.Media.Brush _currentLocationBrush = System.Windows.Media.Brushes.Transparent;
    private string _currentDnsText = PlaceholderDns;
    private System.Windows.Media.Brush _statusTextBrush = System.Windows.Media.Brushes.Transparent;
    private string _downloadSpeedValueText = "0";
    private string _downloadSpeedUnitText = "B/s";
    private string _uploadSpeedValueText = "0";
    private string _uploadSpeedUnitText = "B/s";
    private string _cpuUsageText = "CPU --";
    private string _currentPublicIp = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentLocationText
    {
        get => _currentLocationText;
        private set
        {
            if (_currentLocationText == value) return;
            _currentLocationText = value;
            OnPropertyChanged(nameof(CurrentLocationText));
        }
    }

    public System.Windows.Media.Brush CurrentLocationBrush
    {
        get => _currentLocationBrush;
        private set
        {
            if (Equals(_currentLocationBrush, value)) return;
            _currentLocationBrush = value;
            OnPropertyChanged(nameof(CurrentLocationBrush));
        }
    }

    public string CurrentDnsText
    {
        get => _currentDnsText;
        private set
        {
            if (_currentDnsText == value) return;
            _currentDnsText = value;
            DnsMenu.Header = value;
            OnPropertyChanged(nameof(CurrentDnsText));
        }
    }

    public System.Windows.Media.Brush StatusTextBrush
    {
        get => _statusTextBrush;
        private set
        {
            if (Equals(_statusTextBrush, value)) return;
            _statusTextBrush = value;
            OnPropertyChanged(nameof(StatusTextBrush));
        }
    }

    public string DownloadSpeedValueText
    {
        get => _downloadSpeedValueText;
        private set
        {
            if (_downloadSpeedValueText == value) return;
            _downloadSpeedValueText = value;
            OnPropertyChanged(nameof(DownloadSpeedValueText));
        }
    }

    public string DownloadSpeedUnitText
    {
        get => _downloadSpeedUnitText;
        private set
        {
            if (_downloadSpeedUnitText == value) return;
            _downloadSpeedUnitText = value;
            OnPropertyChanged(nameof(DownloadSpeedUnitText));
        }
    }

    public string UploadSpeedValueText
    {
        get => _uploadSpeedValueText;
        private set
        {
            if (_uploadSpeedValueText == value) return;
            _uploadSpeedValueText = value;
            OnPropertyChanged(nameof(UploadSpeedValueText));
        }
    }

    public string UploadSpeedUnitText
    {
        get => _uploadSpeedUnitText;
        private set
        {
            if (_uploadSpeedUnitText == value) return;
            _uploadSpeedUnitText = value;
            OnPropertyChanged(nameof(UploadSpeedUnitText));
        }
    }

    public string CpuUsageText
    {
        get => _cpuUsageText;
        private set
        {
            if (_cpuUsageText == value) return;
            _cpuUsageText = value;
            OnPropertyChanged(nameof(CpuUsageText));
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        (_trayIcon, _trayShowWindowMenu, _trayTrayOnlyMenu, _trayRefreshMenu, _trayExitMenu) = CreateTrayIcon();
        _locationChinaBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("LocationChina");
        _locationOtherBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("LocationOther");
        _locationUnknownBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("LocationUnknown");
        CurrentLocationBrush = _locationUnknownBrush;
        StatusTextBrush = _balancedStatusTextBrush;

        SourceInitialized += (_, _) =>
        {
            GlassHelper.Apply(this);
            ApplyWindowLayer();
        };

        if (Http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("IpShow/1.0");
        }
        if (Http.DefaultRequestHeaders.CacheControl is null)
        {
            Http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
                MaxAge = TimeSpan.Zero
            };
        }

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(DefaultRefreshSeconds)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        _systemStatsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _systemStatsTimer.Tick += (_, _) => UpdateSystemStats();

        Loaded += (_, _) =>
        {
            SetDockMode(_dockMode, placeDefaultFreePosition: true);
            ApplySavedWindowLayer();
            ApplySavedTrayIconVisibility();
            ApplySavedDisplayMode();
            ApplySavedTextTheme();
            StartupMenu.IsChecked = IsStartupEnabled();
            ApplySavedRefreshInterval();
            HighlightCurrentCalendarItems();
            UpdateSystemStats();
            UpdateTrayText();
            _timer.Start();
            _systemStatsTimer.Start();
        };

        Activated += (_, _) =>
        {
            if (_windowLayer == WindowLayer.Bottom)
            {
                Dispatcher.BeginInvoke(ApplyWindowLayer, DispatcherPriority.Background);
            }
        };

        NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await RefreshAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged;
        _timer.Stop();
        _systemStatsTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            _pendingRefresh = true;
            return;
        }

        _isRefreshing = true;
        try
        {
            // Auxiliary probe isolated so its failure can't block the IP / geo fetch.
            try { UpdateDns(); } catch { }

            var data = await FetchIpDataAsync();
            if (data.Ip is { Length: > 0 } ip)
            {
                NotifyIfPublicIpChanged(ip);
                _currentPublicIp = ip;
                var (location, countryCode) = ResolveLocation(ip, data.Region, data.CountryCode);
                CurrentLocationText = location;
                ApplyLocationBrush(countryCode, isConnected: true);
                UpdateTrayText();
            }
            else
            {
                HandleDisconnected();
                UpdateTrayText();
            }
        }
        catch
        {
            HandleDisconnected();
            UpdateTrayText();
        }
        finally
        {
            _isRefreshing = false;
        }

        if (_pendingRefresh)
        {
            _pendingRefresh = false;
            await RefreshAsync();
        }
    }

    private async Task<IpLookupResult> FetchIpDataAsync()
    {
        // ipify first: fastest, IPv4-stable, and the GeoLite2 mmdb can reliably name
        // the resulting address (this matches the behavior that worked originally).
        var ipOnly = await TryFetchIpifyAsync();
        if (!string.IsNullOrWhiteSpace(ipOnly.Ip))
        {
            return ipOnly;
        }

        // Enriched fallbacks used only when ipify itself fails — they also carry region
        // and country_code, which ResolveLocation will use if the local mmdb has nothing.
        var ipWho = await TryFetchIpWhoAsync();
        if (!string.IsNullOrWhiteSpace(ipWho.Ip))
        {
            return ipWho;
        }

        var ipInfo = await TryFetchIpInfoAsync();
        if (!string.IsNullOrWhiteSpace(ipInfo.Ip))
        {
            return ipInfo;
        }

        return IpLookupResult.Empty;
    }

    private async Task<IpLookupResult> TryFetchIpInfoAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(BuildNoCacheUrl("https://ipinfo.io/json"));
            var data = JsonSerializer.Deserialize<IpInfoResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data is null || string.IsNullOrWhiteSpace(data.Ip))
            {
                return IpLookupResult.Empty;
            }

        return new IpLookupResult(data.Ip!, data.CountryCode, data.Region);
        }
        catch
        {
            return IpLookupResult.Empty;
        }
    }

    private async Task<IpLookupResult> TryFetchIpWhoAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(BuildNoCacheUrl("https://ipwho.is/"));
            var data = JsonSerializer.Deserialize<IpWhoResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data is { Success: true } && !string.IsNullOrWhiteSpace(data.Ip))
            {
            return new IpLookupResult(data.Ip!, data.CountryCode, data.Region);
            }

            return IpLookupResult.Empty;
        }
        catch
        {
            return IpLookupResult.Empty;
        }
    }

    private async Task<IpLookupResult> TryFetchIpifyAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(BuildNoCacheUrl("https://api.ipify.org?format=json"));
            var data = JsonSerializer.Deserialize<IpifyResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data is null || string.IsNullOrWhiteSpace(data.Ip))
            {
                return IpLookupResult.Empty;
            }

            return new IpLookupResult(data.Ip!, null, null);
        }
        catch
        {
            return IpLookupResult.Empty;
        }
    }

    private (string Location, string? CountryCode) ResolveLocation(string ip, string? regionRaw, string? countryCodeRaw)
    {
        var local = TryResolveLocationFromLocalDb(ip);
        var remoteRegion = NormalizeLocation(regionRaw);
        var remoteCountry = string.IsNullOrWhiteSpace(countryCodeRaw) ? null : countryCodeRaw!.Trim();

        // Prefer local mmdb name when available (consistent English / pinyin spelling).
        if (local.Location is { Length: > 0 } loc)
        {
            return (loc, local.CountryCode ?? remoteCountry);
        }

        // Local mmdb had nothing — fall back to the remote service's region string
        // so Chinese IPs (often missing from GeoLite2 subdivision data) still show.
        if (remoteRegion != "-")
        {
            return (remoteRegion, local.CountryCode ?? remoteCountry);
        }

        // Last resort: show the country code itself (e.g. "CN") so the line is never blank when online.
        if (!string.IsNullOrWhiteSpace(remoteCountry))
        {
            return (remoteCountry!, remoteCountry);
        }

        return (PlaceholderLocation, null);
    }

    private void ApplyLocationBrush(string? countryCode, bool isConnected)
    {
        if (!isConnected)
        {
            CurrentLocationBrush = _locationUnknownBrush;
            return;
        }

        if (!string.IsNullOrWhiteSpace(countryCode)
            && countryCode!.Trim().Equals(ChinaCountryCode, StringComparison.OrdinalIgnoreCase))
        {
            CurrentLocationBrush = _locationChinaBrush;
        }
        else
        {
            CurrentLocationBrush = _locationOtherBrush;
        }
    }

    private static async Task<long> MeasureSpeedAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            return response.IsSuccessStatusCode ? sw.ElapsedMilliseconds : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string BuildNoCacheUrl(string url)
    {
        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private void UpdateSystemStats()
    {
        UpdateNetworkSpeed();
        UpdateCpuUsage();
    }

    private void UpdateNetworkSpeed()
    {
        try
        {
            var current = ReadNetworkTotals();
            if (_lastNetworkTotals is null)
            {
                _lastNetworkTotals = current;
                SetSpeedTexts(0, 0);
                return;
            }

            var elapsed = Math.Max(0.001, (current.SampledAt - _lastNetworkTotals.Value.SampledAt).TotalSeconds);
            var received = Math.Max(0, current.BytesReceived - _lastNetworkTotals.Value.BytesReceived);
            var sent = Math.Max(0, current.BytesSent - _lastNetworkTotals.Value.BytesSent);

            SetSpeedTexts(received / elapsed, sent / elapsed);
            _lastNetworkTotals = current;
        }
        catch
        {
            DownloadSpeedValueText = "--";
            DownloadSpeedUnitText = string.Empty;
            UploadSpeedValueText = "--";
            UploadSpeedUnitText = string.Empty;
        }
    }

    private void SetSpeedTexts(double downloadBytesPerSecond, double uploadBytesPerSecond)
    {
        var download = FormatBytesPerSecond(downloadBytesPerSecond);
        var upload = FormatBytesPerSecond(uploadBytesPerSecond);
        DownloadSpeedValueText = download.Value;
        DownloadSpeedUnitText = download.Unit;
        UploadSpeedValueText = upload.Value;
        UploadSpeedUnitText = upload.Unit;
        UpdateTrayText();
    }

    private void UpdateCpuUsage()
    {
        try
        {
            var current = ReadCpuTimes();
            if (current is null)
            {
                CpuUsageText = "CPU --";
                return;
            }

            if (_lastCpuTimes is null)
            {
                _lastCpuTimes = current;
                CpuUsageText = "CPU 0%";
                UpdateTrayText();
                return;
            }

            var previous = _lastCpuTimes.Value;
            var total = current.Value.Total - previous.Total;
            var idle = current.Value.Idle - previous.Idle;
            var usage = total <= 0 ? 0 : (1.0 - idle / (double)total) * 100;
            usage = Math.Clamp(usage, 0, 100);

            CpuUsageText = $"CPU {usage:0}%";
            _lastCpuTimes = current;
            UpdateTrayText();
        }
        catch
        {
            CpuUsageText = "CPU --";
        }
    }

    private static NetworkTotals ReadNetworkTotals()
    {
        long bytesReceived = 0;
        long bytesSent = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var stats = nic.GetIPStatistics();
            bytesReceived += stats.BytesReceived;
            bytesSent += stats.BytesSent;
        }

        return new NetworkTotals(bytesReceived, bytesSent, DateTimeOffset.UtcNow);
    }

    private static SpeedText FormatBytesPerSecond(double bytesPerSecond)
    {
        string[] units = ["B/s", "K/s", "M/s", "G/s"];
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return new SpeedText(unit == 0 ? $"{value:0}" : $"{value:0.#}", units[unit]);
    }

    private static CpuTimes? ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return null;
        }

        var idle = idleTime.ToUInt64();
        var kernel = kernelTime.ToUInt64();
        var user = userTime.ToUInt64();
        return new CpuTimes(idle, kernel + user);
    }

    private static DatabaseReader? CreateGeoReader(string path)
    {
        try
        {
            // Force English so regions come back as e.g. "Guangdong" rather than localized Chinese.
            return File.Exists(path) ? new DatabaseReader(path, new[] { "en" }) : null;
        }
        catch
        {
            return null;
        }
    }

    private static (string? Location, string? CountryCode) TryResolveLocationFromLocalDb(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return (null, null);
        }

        // City DB is a superset of Country DB, and TryResolveFromCityDb already falls
        // back to the country name as its last-resort return, so a separate Country DB
        // lookup is no longer needed.
        return TryResolveFromCityDb(address);
    }

    private static (string? Location, string? CountryCode) TryResolveFromCityDb(IPAddress address)
    {
        if (GeoCityReader.Value is null)
        {
            return (null, null);
        }

        try
        {
            var response = GeoCityReader.Value.City(address);
            var iso = response?.Country?.IsoCode;
            var region = NormalizeLocation(response?.MostSpecificSubdivision?.Name);
            if (region != "-")
            {
                return (region, iso);
            }

            var city = NormalizeLocation(response?.City?.Name);
            if (city != "-")
            {
                return (city, iso);
            }

            var country = NormalizeLocation(response?.Country?.Name);
            return country == "-" ? (null, iso) : (country, iso);
        }
        catch (AddressNotFoundException)
        {
            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private void UpdateDns()
    {
        try
        {
            var dns = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                              && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => nic.GetIPProperties().DnsAddresses)
                .Where(list => list is { Count: > 0 })
                .SelectMany(list => list)
                .FirstOrDefault(addr => addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?? NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                                  && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(nic => nic.GetIPProperties().DnsAddresses)
                    .FirstOrDefault();

            CurrentDnsText = dns is null ? PlaceholderDns : $"DNS {dns}";
        }
        catch
        {
            CurrentDnsText = PlaceholderDns;
        }
    }

    private static string NormalizeLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase) ? "-" : value.Trim();
    }

    private void NotifyIfPublicIpChanged(string newIp)
    {
        // 首次获取（旧值为空）不提醒；仅当已有旧值且发生变化时弹一次托盘气泡。
        if (string.IsNullOrEmpty(_currentPublicIp) || _currentPublicIp == newIp)
        {
            return;
        }

        try
        {
            _trayIcon.ShowBalloonTip(
                3000,
                "IpShow",
                $"公网 IP 变化：{_currentPublicIp} → {newIp}",
                Forms.ToolTipIcon.Info);
        }
        catch
        {
        }
    }

    private void HandleDisconnected()
    {
        CurrentLocationText = PlaceholderLocation;
        ApplyLocationBrush(null, isConnected: false);
    }

    private void NetworkChange_NetworkAddressChanged(object? sender, EventArgs e)
        => _ = Dispatcher.InvokeAsync(() => RefreshWithRetriesAsync(2, TimeSpan.FromSeconds(1)));

    private void NetworkChange_NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            _ = Dispatcher.InvokeAsync(() => RefreshWithRetriesAsync(3, TimeSpan.FromSeconds(2)));
            return;
        }

        Dispatcher.Invoke(HandleDisconnected);
    }

    private async Task RefreshWithRetriesAsync(int attempts, TimeSpan delay)
    {
        for (var i = 0; i < attempts; i++)
        {
            await RefreshAsync();
            if (CurrentLocationText != PlaceholderLocation)
            {
                return;
            }

            if (i < attempts - 1)
            {
                await Task.Delay(delay);
            }
        }
    }

    private void AddressLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink hyperlink)
        {
            return;
        }

        var location = hyperlink.Tag as string;
        if (string.IsNullOrWhiteSpace(location) || location == "-")
        {
            return;
        }

        var url = $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(location)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击任意处 = 强制刷新（替代旧的单击刷新，避免误触）。
        if (e.ClickCount == 2)
        {
            await RefreshWithRetriesAsync(2, TimeSpan.FromSeconds(1));
            return;
        }

        if (_dockMode != DockMode.Free)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private void DockTopMenu_Click(object sender, RoutedEventArgs e) => SetDockMode(DockMode.Top);

    private void DockBottomMenu_Click(object sender, RoutedEventArgs e) => SetDockMode(DockMode.Bottom);

    private void DockBottomSafeMenu_Click(object sender, RoutedEventArgs e) => SetDockMode(DockMode.BottomSafe);

    private void DockTopRightMenu_Click(object sender, RoutedEventArgs e) => SetDockMode(DockMode.TopRight);

    private void DockBottomRightMenu_Click(object sender, RoutedEventArgs e) => SetDockMode(DockMode.BottomRight);

    private void DockFreeMenu_Click(object sender, RoutedEventArgs e) => SetDockMode(DockMode.Free);

    private void DockCurrentMenu_Click(object sender, RoutedEventArgs e) => SetDockMode(DockMode.LockCurrent);

    private void StartupMenu_Click(object sender, RoutedEventArgs e)
    {
        var enable = StartupMenu.IsChecked == true;
        if (!TrySetStartupEnabled(enable))
        {
            StartupMenu.IsChecked = !enable;
        }
    }

    private void LayerMenu_Click(object sender, RoutedEventArgs e)
    {
        var layer = sender == LayerBottomMenu
            ? WindowLayer.Bottom
            : sender == LayerNormalMenu
                ? WindowLayer.Normal
                : WindowLayer.Top;
        ApplyWindowLayer(layer);
        SaveWindowLayer(layer);
    }

    private void DisplayModeMenu_Click(object sender, RoutedEventArgs e)
    {
        var mode = sender == DisplayTrayOnlyMenu ? DisplayMode.TrayOnly : DisplayMode.Desktop;
        ApplyDisplayMode(mode);
        SaveDisplayMode(mode);
    }

    private void ShowTrayIconMenu_Click(object sender, RoutedEventArgs e)
    {
        var show = ShowTrayIconMenu.IsChecked == true;
        SetTrayIconVisibility(show);
        SaveTrayIconVisibility(_showTrayIcon);
    }

    private void TextThemeMenu_Click(object sender, RoutedEventArgs e)
    {
        var theme = sender == DarkTextMenu
            ? TextTheme.Dark
            : sender == LightTextMenu
                ? TextTheme.Light
                : TextTheme.Balanced;
        ApplyTextTheme(theme);
        SaveTextTheme(theme);
    }

    private async void RefreshNowMenu_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

    private async void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        try { UpdateDns(); } catch { }

        // Show loading state
        PingGoogleMenu.Header = "Google: ...";
        PingBaiduMenu.Header = "Baidu: ...";
        PingGitHubMenu.Header = "GitHub: ...";
        PingYouTubeMenu.Header = "YouTube: ...";
        PingTelegramMenu.Header = "Telegram: ...";

        // Test speed to all targets in parallel using HTTP
        var googleTask = MeasureSpeedAsync("https://www.google.com");
        var baiduTask = MeasureSpeedAsync("https://www.baidu.com");
        var githubTask = MeasureSpeedAsync("https://github.com");
        var youtubeTask = MeasureSpeedAsync("https://www.youtube.com");
        var telegramTask = MeasureSpeedAsync("https://api.telegram.org");

        var results = await Task.WhenAll(googleTask, baiduTask, githubTask, youtubeTask, telegramTask);

        // Update menu items with results (name in default color, speed in color)
        UpdateSpeedMenuItem(PingGoogleMenu, "Google", results[0]);
        UpdateSpeedMenuItem(PingBaiduMenu, "Baidu", results[1]);
        UpdateSpeedMenuItem(PingGitHubMenu, "GitHub", results[2]);
        UpdateSpeedMenuItem(PingYouTubeMenu, "YouTube", results[3]);
        UpdateSpeedMenuItem(PingTelegramMenu, "Telegram", results[4]);
    }

    private static string FormatSpeed(long ms) => ms >= 0 ? $"{ms}ms" : "timeout";

    private void UpdateSpeedMenuItem(MenuItem item, string name, long ms)
    {
        var textBlock = new TextBlock();
        textBlock.Inlines.Add(new Run($"{name}: "));
        textBlock.Inlines.Add(new Run(FormatSpeed(ms))
        {
            Foreground = ms >= 0
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50))  // Green
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36))  // Red
        });
        item.Header = textBlock;
    }

    private void RefreshIntervalMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string secondsRaw)
        {
            return;
        }

        if (!int.TryParse(secondsRaw, out var seconds))
        {
            return;
        }

        SetRefreshInterval(seconds, true);
    }

    private void SetDockMode(DockMode mode, bool placeDefaultFreePosition = false)
    {
        _dockMode = mode;

        DockTopMenu.IsChecked = mode == DockMode.Top;
        DockBottomMenu.IsChecked = mode == DockMode.Bottom;
        DockBottomSafeMenu.IsChecked = mode == DockMode.BottomSafe;
        DockTopRightMenu.IsChecked = mode == DockMode.TopRight;
        DockBottomRightMenu.IsChecked = mode == DockMode.BottomRight;
        DockFreeMenu.IsChecked = mode == DockMode.Free;
        DockCurrentMenu.IsChecked = mode == DockMode.LockCurrent;

        if (mode == DockMode.Free)
        {
            if (placeDefaultFreePosition)
            {
                PlaceDefaultFreePosition();
            }

            return;
        }

        if (mode == DockMode.LockCurrent)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        const double edgeMargin = 12;
        const double safeBottomMargin = 40;
        var leftCenter = workArea.Left + (workArea.Width - Width) / 2;
        var leftRight = workArea.Right - Width - edgeMargin;

        if (mode == DockMode.Top)
        {
            Left = leftCenter;
            Top = workArea.Top + edgeMargin;
        }
        else if (mode == DockMode.Bottom)
        {
            Left = leftCenter;
            Top = workArea.Bottom - Height - edgeMargin;
        }
        else if (mode == DockMode.BottomSafe)
        {
            Left = leftCenter;
            Top = workArea.Bottom - Height - safeBottomMargin;
        }
        else if (mode == DockMode.TopRight)
        {
            Left = leftRight;
            Top = workArea.Top + edgeMargin;
        }
        else
        {
            Left = leftRight;
            Top = workArea.Bottom - Height - edgeMargin;
        }
    }

    private void PlaceDefaultFreePosition()
    {
        var workArea = SystemParameters.WorkArea;
        const double rightInset = 260;
        const double topInset = 88;
        const double minMargin = 24;

        Left = Math.Max(workArea.Left + minMargin, workArea.Right - Width - rightInset);
        Top = Math.Min(workArea.Bottom - Height - minMargin, workArea.Top + topInset);
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
        var value = key?.GetValue(StartupValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TrySetStartupEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryKey);
            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return false;
                }

                key?.SetValue(StartupValueName, $"\"{exePath}\"");
            }
            else
            {
                key?.DeleteValue(StartupValueName, false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplySavedRefreshInterval()
    {
        var seconds = LoadRefreshIntervalSeconds();
        SetRefreshInterval(seconds, false);
    }

    private void SetRefreshInterval(int seconds, bool persist)
    {
        if (seconds < 5 || seconds > 600)
        {
            seconds = DefaultRefreshSeconds;
        }

        _timer.Interval = TimeSpan.FromSeconds(seconds);
        UpdateRefreshMenuChecks(seconds);

        if (persist)
        {
            SaveRefreshIntervalSeconds(seconds);
        }
    }

    private void UpdateRefreshMenuChecks(int seconds)
    {
        Refresh5Menu.IsChecked = seconds == 5;
        Refresh10Menu.IsChecked = seconds == 10;
        Refresh30Menu.IsChecked = seconds == 30;
        Refresh60Menu.IsChecked = seconds == 60;
    }

    private static int LoadRefreshIntervalSeconds()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsRegistryKey, false);
            var value = key?.GetValue(RefreshIntervalValueName);
            return value is int seconds ? seconds : DefaultRefreshSeconds;
        }
        catch
        {
            return DefaultRefreshSeconds;
        }
    }

    private static void SaveRefreshIntervalSeconds(int seconds)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsRegistryKey);
            key?.SetValue(RefreshIntervalValueName, seconds, RegistryValueKind.DWord);
        }
        catch
        {
        }
    }

    private void ApplySavedWindowLayer()
    {
        ApplyWindowLayer(LoadWindowLayer());
    }

    private void ApplyWindowLayer(WindowLayer layer)
    {
        _windowLayer = layer;
        Topmost = layer == WindowLayer.Top;
        UpdateLayerMenuChecks(layer);
        ApplyWindowLayer();
    }

    private void ApplyWindowLayer()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var insertAfter = _windowLayer switch
        {
            WindowLayer.Top => HwndTopMost,
            WindowLayer.Bottom => HwndBottom,
            _ => HwndNoTopMost
        };

        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void UpdateLayerMenuChecks(WindowLayer layer)
    {
        LayerTopMenu.IsChecked = layer == WindowLayer.Top;
        LayerNormalMenu.IsChecked = layer == WindowLayer.Normal;
        LayerBottomMenu.IsChecked = layer == WindowLayer.Bottom;
    }

    private static WindowLayer LoadWindowLayer()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsRegistryKey, false);
            var value = key?.GetValue(WindowLayerValueName) as string;
            if (Enum.TryParse<WindowLayer>(value, true, out var layer))
            {
                return layer;
            }

            var legacyTopmost = key?.GetValue(AlwaysOnTopValueName);
            return legacyTopmost is int enabled && enabled == 0 ? WindowLayer.Normal : WindowLayer.Top;
        }
        catch
        {
            return WindowLayer.Top;
        }
    }

    private static void SaveWindowLayer(WindowLayer layer)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsRegistryKey);
            key?.SetValue(WindowLayerValueName, layer.ToString(), RegistryValueKind.String);
            key?.SetValue(AlwaysOnTopValueName, layer == WindowLayer.Top ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
        }
    }

    private void ApplySavedDisplayMode()
    {
        ApplyDisplayMode(LoadDisplayMode());
    }

    private void ApplyDisplayMode(DisplayMode mode)
    {
        _displayMode = mode;
        DisplayDesktopMenu.IsChecked = mode == DisplayMode.Desktop;
        DisplayTrayOnlyMenu.IsChecked = mode == DisplayMode.TrayOnly;
        _trayShowWindowMenu.Checked = mode == DisplayMode.Desktop;
        _trayTrayOnlyMenu.Checked = mode == DisplayMode.TrayOnly;

        if (mode == DisplayMode.TrayOnly)
        {
            if (!_showTrayIcon)
            {
                SetTrayIconVisibility(true);
                SaveTrayIconVisibility(true);
            }

            Hide();
            return;
        }

        Show();
        Activate();
        ApplyWindowLayer();
        ApplyTrayIconVisibility();
    }

    private static DisplayMode LoadDisplayMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsRegistryKey, false);
            var value = key?.GetValue(DisplayModeValueName) as string;
            return Enum.TryParse<DisplayMode>(value, true, out var mode) ? mode : DisplayMode.Desktop;
        }
        catch
        {
            return DisplayMode.Desktop;
        }
    }

    private static void SaveDisplayMode(DisplayMode mode)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsRegistryKey);
            key?.SetValue(DisplayModeValueName, mode.ToString(), RegistryValueKind.String);
        }
        catch
        {
        }
    }

    private void ApplySavedTrayIconVisibility()
    {
        SetTrayIconVisibility(LoadTrayIconVisibility());
    }

    private void SetTrayIconVisibility(bool show)
    {
        if (!show && _displayMode == DisplayMode.TrayOnly)
        {
            ApplyDisplayMode(DisplayMode.Desktop);
            SaveDisplayMode(DisplayMode.Desktop);
        }

        _showTrayIcon = show;
        ShowTrayIconMenu.IsChecked = show;
        ApplyTrayIconVisibility();
    }

    private void ApplyTrayIconVisibility()
    {
        _trayIcon.Visible = _showTrayIcon || _displayMode == DisplayMode.TrayOnly;
    }

    private static bool LoadTrayIconVisibility()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsRegistryKey, false);
            var value = key?.GetValue(ShowTrayIconValueName);
            return value is int show ? show != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    private static void SaveTrayIconVisibility(bool show)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsRegistryKey);
            key?.SetValue(ShowTrayIconValueName, show ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
        }
    }

    private (Forms.NotifyIcon Icon,
        Forms.ToolStripMenuItem ShowWindow,
        Forms.ToolStripMenuItem TrayOnly,
        Forms.ToolStripMenuItem Refresh,
        Forms.ToolStripMenuItem Exit) CreateTrayIcon()
    {
        var showWindow = new Forms.ToolStripMenuItem("Desktop Window") { CheckOnClick = false };
        var trayOnly = new Forms.ToolStripMenuItem("Tray Only") { CheckOnClick = false };
        var refresh = new Forms.ToolStripMenuItem("Force Refresh");
        var exit = new Forms.ToolStripMenuItem("Exit");
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(showWindow);
        menu.Items.Add(trayOnly);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(refresh);
        menu.Items.Add(exit);

        var icon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Text = "IpShow",
            Visible = true
        };

        showWindow.Click += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                ApplyDisplayMode(DisplayMode.Desktop);
                SaveDisplayMode(DisplayMode.Desktop);
            });
        };
        trayOnly.Click += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                ApplyDisplayMode(DisplayMode.TrayOnly);
                SaveDisplayMode(DisplayMode.TrayOnly);
            });
        };
        refresh.Click += async (_, _) => await Dispatcher.InvokeAsync(RefreshAsync);
        exit.Click += (_, _) => Dispatcher.Invoke(Close);
        icon.DoubleClick += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                ApplyDisplayMode(DisplayMode.Desktop);
                SaveDisplayMode(DisplayMode.Desktop);
            });
        };

        return (icon, showWindow, trayOnly, refresh, exit);
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            var icon = string.IsNullOrWhiteSpace(exePath) ? null : Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is not null)
            {
                return icon;
            }
        }
        catch
        {
        }

        return Drawing.SystemIcons.Application;
    }

    private void UpdateTrayText()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var speed = $"↓ {DownloadSpeedValueText}{DownloadSpeedUnitText} ↑ {UploadSpeedValueText}{UploadSpeedUnitText}";
        _trayIcon.Text = TrimTrayText($"IpShow {CurrentLocationText} {CpuUsageText} {speed}");
    }

    private static string TrimTrayText(string text)
        => text.Length <= 63 ? text : text[..60] + "...";

    private void ApplySavedTextTheme()
    {
        ApplyTextTheme(LoadTextTheme());
    }

    private void ApplyTextTheme(TextTheme theme)
    {
        StatusTextBrush = theme switch
        {
            TextTheme.Dark => _darkStatusTextBrush,
            TextTheme.Light => _lightStatusTextBrush,
            _ => _balancedStatusTextBrush
        };
        BalancedTextMenu.IsChecked = theme == TextTheme.Balanced;
        LightTextMenu.IsChecked = theme == TextTheme.Light;
        DarkTextMenu.IsChecked = theme == TextTheme.Dark;
    }

    private static TextTheme LoadTextTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsRegistryKey, false);
            var value = key?.GetValue(TextThemeValueName) as string;
            if (Enum.TryParse<TextTheme>(value, true, out var theme))
            {
                return theme;
            }

            return TextTheme.Balanced;
        }
        catch
        {
            return TextTheme.Balanced;
        }
    }

    private static void SaveTextTheme(TextTheme theme)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsRegistryKey);
            key?.SetValue(TextThemeValueName, theme.ToString(), RegistryValueKind.String);
        }
        catch
        {
        }
    }

    private void HighlightCurrentCalendarItems()
    {
        var now = DateTime.Now;
        var currentMonth = now.Month;
        var currentDayOfWeek = (int)now.DayOfWeek;

        // Highlight current month
        var monthMenuItems = new[] { Month1Menu, Month2Menu, Month3Menu, Month4Menu, Month5Menu, Month6Menu,
                                      Month7Menu, Month8Menu, Month9Menu, Month10Menu, Month11Menu, Month12Menu };
        for (int i = 0; i < monthMenuItems.Length; i++)
        {
            if (i + 1 == currentMonth)
            {
                monthMenuItems[i].FontWeight = FontWeights.Bold;
                monthMenuItems[i].Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // Green
            }
        }

        // Highlight current day of week
        var weekdayMenuItems = new[] { Weekday0Menu, Weekday1Menu, Weekday2Menu, Weekday3Menu,
                                        Weekday4Menu, Weekday5Menu, Weekday6Menu };
        for (int i = 0; i < weekdayMenuItems.Length; i++)
        {
            if (i == currentDayOfWeek)
            {
                weekdayMenuItems[i].FontWeight = FontWeights.Bold;
                weekdayMenuItems[i].Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // Green
            }
        }
    }

    private enum DockMode
    {
        Free,
        Top,
        Bottom,
        BottomSafe,
        TopRight,
        BottomRight,
        LockCurrent
    }

    private enum WindowLayer
    {
        Top,
        Normal,
        Bottom
    }

    private enum DisplayMode
    {
        Desktop,
        TrayOnly
    }

    private enum TextTheme
    {
        Balanced,
        Light,
        Dark
    }

    private readonly record struct NetworkTotals(long BytesReceived, long BytesSent, DateTimeOffset SampledAt);

    private readonly record struct CpuTimes(ulong Idle, ulong Total);

    private readonly record struct SpeedText(string Value, string Unit);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public ulong ToUInt64() => ((ulong)_highDateTime << 32) | _lowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private readonly record struct IpLookupResult(string Ip, string? CountryCode, string? Region)
    {
        public static readonly IpLookupResult Empty = new(string.Empty, null, null);
    }

    private sealed class IpWhoResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("ip")]
        public string? Ip { get; set; }

        [JsonPropertyName("region")]
        public string? Region { get; set; }

        [JsonPropertyName("country_code")]
        public string? CountryCode { get; set; }
    }

    private sealed class IpInfoResponse
    {
        [JsonPropertyName("ip")]
        public string? Ip { get; set; }

        [JsonPropertyName("region")]
        public string? Region { get; set; }

        [JsonPropertyName("country")]
        public string? CountryCode { get; set; }
    }

    private sealed class IpifyResponse
    {
        [JsonPropertyName("ip")]
        public string? Ip { get; set; }
    }
}
