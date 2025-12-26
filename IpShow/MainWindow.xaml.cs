using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
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
    private const string PlaceholderLocation = "-";
    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "IpShow";
    private const string SettingsRegistryKey = @"Software\IpShow";
    private const string RefreshIntervalValueName = "RefreshIntervalSeconds";
    private const int DefaultRefreshSeconds = 10;
    private static readonly string GeoCityDbPath = Path.Combine(AppContext.BaseDirectory, "GeoIP", "GeoLite2-City.mmdb");
    private static readonly string GeoCountryDbPath = Path.Combine(AppContext.BaseDirectory, "GeoIP", "GeoLite2-Country.mmdb");
    private static readonly Lazy<DatabaseReader?> GeoCityReader = new(() => CreateGeoReader(GeoCityDbPath));
    private static readonly Lazy<DatabaseReader?> GeoCountryReader = new(() => CreateGeoReader(GeoCountryDbPath));
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private readonly DispatcherTimer _timer;
    private DockMode _dockMode = DockMode.Bottom;
    private string? _previousIp;
    private string? _previousLocation;
    private bool _isRefreshing;
    private bool _pendingRefresh;
    private readonly Brush _prefixConnectedBrush;
    private readonly Brush _prefixDisconnectedBrush;

    private string _currentIpText = PlaceholderIp;
    private string _currentLocationText = PlaceholderLocation;
    private string _previousIpText = PlaceholderIp;
    private string _previousLocationText = PlaceholderLocation;
    private Brush _currentPrefixBrush = Brushes.Transparent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentIpText
    {
        get => _currentIpText;
        private set
        {
            if (_currentIpText == value) return;
            _currentIpText = value;
            OnPropertyChanged(nameof(CurrentIpText));
        }
    }

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

    public string PreviousIpText
    {
        get => _previousIpText;
        private set
        {
            if (_previousIpText == value) return;
            _previousIpText = value;
            OnPropertyChanged(nameof(PreviousIpText));
        }
    }

    public string PreviousLocationText
    {
        get => _previousLocationText;
        private set
        {
            if (_previousLocationText == value) return;
            _previousLocationText = value;
            OnPropertyChanged(nameof(PreviousLocationText));
        }
    }

    public Brush CurrentPrefixBrush
    {
        get => _currentPrefixBrush;
        private set
        {
            if (Equals(_currentPrefixBrush, value)) return;
            _currentPrefixBrush = value;
            OnPropertyChanged(nameof(CurrentPrefixBrush));
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _prefixConnectedBrush = (Brush)Application.Current.FindResource("PrefixConnected");
        _prefixDisconnectedBrush = (Brush)Application.Current.FindResource("PrefixDisconnected");
        CurrentPrefixBrush = _prefixDisconnectedBrush;

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
            var data = await FetchIpDataAsync();
            if (data.Ip is { Length: > 0 } ip)
            {
                var location = ResolveLocationText(ip, data.Region);
                if (!string.Equals(CurrentIpText, ip, StringComparison.OrdinalIgnoreCase)
                    && CurrentIpText != PlaceholderIp)
                {
                    _previousIp = CurrentIpText;
                    _previousLocation = CurrentLocationText;
                }

                CurrentIpText = ip;
                CurrentLocationText = location;
                UpdatePreviousLine();
                UpdateConnectionState(true);
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
        var ipOnly = await TryFetchIpifyAsync();
        if (!string.IsNullOrWhiteSpace(ipOnly.Ip))
        {
            return ipOnly;
        }

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

            return new IpLookupResult(data.Ip!, PlaceholderLocation, PlaceholderLocation);
        }
        catch
        {
            return IpLookupResult.Empty;
        }
    }

    private string ResolveLocationText(string ip, string? regionRaw)
    {
        var localRegion = TryResolveRegionFromLocalDb(ip);
        if (!string.IsNullOrWhiteSpace(localRegion))
        {
            return localRegion;
        }

        var region = NormalizeLocation(regionRaw);
        return region == "-" ? PlaceholderLocation : region;
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
            return File.Exists(path) ? new DatabaseReader(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveRegionFromLocalDb(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return null;
        }

        var city = TryResolveRegionFromCityDb(address);
        if (!string.IsNullOrWhiteSpace(city))
        {
            return city;
        }

        var country = TryResolveCountryFromDb(address);
        return string.IsNullOrWhiteSpace(country) ? null : country;
    }

    private static string? TryResolveRegionFromCityDb(IPAddress address)
    {
        if (GeoCityReader.Value is null)
        {
            return null;
        }

        try
        {
            var response = GeoCityReader.Value.City(address);
            var region = NormalizeLocation(response?.MostSpecificSubdivision?.Name);
            if (region != "-")
            {
                return region;
            }

            var city = NormalizeLocation(response?.City?.Name);
            return city != "-" ? city : null;
        }
        catch (AddressNotFoundException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveCountryFromDb(IPAddress address)
    {
        if (GeoCountryReader.Value is null)
        {
            return null;
        }

        try
        {
            var response = GeoCountryReader.Value.Country(address);
            var country = NormalizeLocation(response?.Country?.Name);
            return country == "-" ? null : country;
        }
        catch (AddressNotFoundException)
        {
            return null;
        }
        catch
        {
            return null;
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
        if (CurrentIpText != PlaceholderIp)
        {
            _previousIp = CurrentIpText;
            _previousLocation = CurrentLocationText;
            CurrentIpText = PlaceholderIp;
            CurrentLocationText = PlaceholderLocation;
            UpdatePreviousLine();
        }
        else
        {
            CurrentLocationText = PlaceholderLocation;
        }

        UpdateConnectionState(false);
    }

    private void UpdatePreviousLine()
    {
        if (string.IsNullOrWhiteSpace(_previousIp))
        {
            PreviousIpText = "--";
            PreviousLocationText = PlaceholderLocation;
            return;
        }

        if (string.IsNullOrWhiteSpace(_previousLocation))
        {
            PreviousIpText = _previousIp;
            PreviousLocationText = PlaceholderLocation;
            return;
        }

        PreviousIpText = _previousIp;
        PreviousLocationText = _previousLocation;
    }

    private void UpdateConnectionState(bool isConnected)
    {
        CurrentPrefixBrush = isConnected ? _prefixConnectedBrush : _prefixDisconnectedBrush;
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
            if (CurrentIpText != PlaceholderIp)
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
