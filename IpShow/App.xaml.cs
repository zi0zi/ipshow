using System.Threading;
using System.Windows;

namespace IpShow;

public partial class App : System.Windows.Application
{
    // Named mutex ensures only one IpShow instance runs per user session.
    private const string SingleInstanceMutexName = "Global\\IpShow.SingleInstance.{7B4E2F1A-3C9D-4A6B-8F12-5D9E0A1B2C3D}";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance already owns the mutex — exit silently without starting the window.
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
