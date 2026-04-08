using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Win32;

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
    private const int DefaultRefreshSeconds = 10;
    private static readonly string GeoCityDbPath = Path.Combine(AppContext.BaseDirectory, "GeoIP", "GeoLite2-City.mmdb");
    private static readonly Lazy<DatabaseReader?> GeoCityReader = new(() => CreateGeoReader(GeoCityDbPath));
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private readonly DispatcherTimer _timer;
    private DockMode _dockMode = DockMode.BottomSafe;
    private bool _isRefreshing;
    private bool _pendingRefresh;
    private readonly Brush _locationChinaBrush;
    private readonly Brush _locationOtherBrush;
    private readonly Brush _locationUnknownBrush;
    private string _currentLocationText = PlaceholderLocation;
    private Brush _currentLocationBrush = Brushes.Transparent;
    private string _currentDnsText = PlaceholderDns;

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

    public Brush CurrentLocationBrush
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
            OnPropertyChanged(nameof(CurrentDnsText));
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _locationChinaBrush = (Brush)Application.Current.FindResource("LocationChina");
        _locationOtherBrush = (Brush)Application.Current.FindResource("LocationOther");
        _locationUnknownBrush = (Brush)Application.Current.FindResource("LocationUnknown");
        CurrentLocationBrush = _locationUnknownBrush;

        SourceInitialized += (_, _) => GlassHelper.Apply(this);

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

        Loaded += (_, _) =>
        {
            SetDockMode(_dockMode);
            StartupMenu.IsChecked = IsStartupEnabled();
            ApplySavedRefreshInterval();
            HighlightCurrentCalendarItems();
            _timer.Start();
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
                var (location, countryCode) = ResolveLocation(ip, data.Region, data.CountryCode);
                CurrentLocationText = location;
                ApplyLocationBrush(countryCode, isConnected: true);
            }
            else
            {
                HandleDisconnected();
            }
        }
        catch
        {
            HandleDisconnected();
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

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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

    private async void RefreshNowMenu_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

    private async void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
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
                ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))  // Green
                : new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36))  // Red
        });
        item.Header = textBlock;
    }

    private async void CurrentIpText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => await RefreshWithRetriesAsync(2, TimeSpan.FromSeconds(1));

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

    private void SetDockMode(DockMode mode)
    {
        _dockMode = mode;

        DockTopMenu.IsChecked = mode == DockMode.Top;
        DockBottomMenu.IsChecked = mode == DockMode.Bottom;
        DockBottomSafeMenu.IsChecked = mode == DockMode.BottomSafe;
        DockTopRightMenu.IsChecked = mode == DockMode.TopRight;
        DockBottomRightMenu.IsChecked = mode == DockMode.BottomRight;
        DockFreeMenu.IsChecked = mode == DockMode.Free;
        DockCurrentMenu.IsChecked = mode == DockMode.LockCurrent;

        if (mode == DockMode.Free || mode == DockMode.LockCurrent)
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
                monthMenuItems[i].Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // Green
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
                weekdayMenuItems[i].Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // Green
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
