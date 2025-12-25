using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IpShow;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private readonly DispatcherTimer _timer;
    private DockMode _dockMode = DockMode.Bottom;
    private string? _previousIp;

    private string _ipAddress = "IP: --";
    private string _locationText = "--";
    private string _statusText = "Initializing...";
    private Brush _statusBrush = OkBrush;

    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x57, 0xC1, 0xFF));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));

    public event PropertyChangedEventHandler? PropertyChanged;

    public string IpAddress
    {
        get => _ipAddress;
        private set
        {
            if (_ipAddress == value) return;
            _ipAddress = value;
            OnPropertyChanged(nameof(IpAddress));
        }
    }

    public string LocationText
    {
        get => _locationText;
        private set
        {
            if (_locationText == value) return;
            _locationText = value;
            OnPropertyChanged(nameof(LocationText));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set
        {
            if (_statusBrush == value) return;
            _statusBrush = value;
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        if (Http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("IpShow/1.0");
        }

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        Loaded += (_, _) =>
        {
            SetDockMode(_dockMode);
            _timer.Start();
        };
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await RefreshAsync();
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private async Task RefreshAsync()
    {
        try
        {
            var json = await Http.GetStringAsync("https://ipapi.co/json/");
            var data = JsonSerializer.Deserialize<IpApiResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data?.Ip is { Length: > 0 } ip)
            {
                IpAddress = ip;
                LocationText = BuildLocationText(data);

                StatusText = _previousIp is null
                    ? "Connected"
                    : $"Connected · last: {_previousIp}";
                StatusBrush = OkBrush;

                if (!string.Equals(_previousIp, ip, StringComparison.OrdinalIgnoreCase))
                {
                    _previousIp = ip;
                }
            }
            else
            {
                SetDisconnected("No IP data");
            }
        }
        catch
        {
            SetDisconnected("Disconnected");
        }
    }

    private string BuildLocationText(IpApiResponse data)
    {
        var country = string.IsNullOrWhiteSpace(data.CountryName) ? "Unknown" : data.CountryName;
        var region = string.IsNullOrWhiteSpace(data.Region) ? "Region" : data.Region;
        return $"{country} · {region}";
    }

    private void SetDisconnected(string reason)
    {
        StatusText = _previousIp is null
            ? reason
            : $"{reason} · last: {_previousIp}";
        StatusBrush = ErrorBrush;
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

    private void DockFreeMenu_Click(object sender, RoutedEventArgs e) => SetDockMode(DockMode.Free);

    private async void RefreshNowMenu_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

    private void SetDockMode(DockMode mode)
    {
        _dockMode = mode;

        DockTopMenu.IsChecked = mode == DockMode.Top;
        DockBottomMenu.IsChecked = mode == DockMode.Bottom;
        DockFreeMenu.IsChecked = mode == DockMode.Free;

        if (mode == DockMode.Free)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;

        if (mode == DockMode.Top)
        {
            Top = workArea.Top + 8;
        }
        else
        {
            Top = workArea.Bottom - Height - 8;
        }
    }

    private enum DockMode
    {
        Free,
        Top,
        Bottom
    }

    private sealed class IpApiResponse
    {
        public string? Ip { get; set; }
        public string? CountryName { get; set; }
        public string? Region { get; set; }
    }
}
