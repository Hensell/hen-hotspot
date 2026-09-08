using System.Windows;
using System.Windows.Threading;
using Windows.Networking.Connectivity;

namespace HenHotspot;

public partial class MainWindow
{
    private bool windowClosed;
    private int networkRefreshQueued;

    private void InitializeRefreshHandling()
    {
        NetworkInformation.NetworkStatusChanged += NetworkStatusChanged;
        StateChanged += WindowStateChanged;
        Activated += WindowActivated;
    }

    private void StopRefreshHandling()
    {
        windowClosed = true;
        NetworkInformation.NetworkStatusChanged -= NetworkStatusChanged;
        StateChanged -= WindowStateChanged;
        Activated -= WindowActivated;
    }

    private void NetworkStatusChanged(object sender)
    {
        // WinRT raises this on a worker thread; coalesce bursts before entering WPF.
        if (Interlocked.Exchange(ref networkRefreshQueued, 1) != 0) return;
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref networkRefreshQueued, 0);
            RequestStatusRefresh();
        }));
    }

    private void WindowStateChanged(object? sender, EventArgs e)
    {
        UpdateRefreshInterval();
        if (WindowState != WindowState.Minimized) RequestStatusRefresh();
    }

    private void WindowActivated(object? sender, EventArgs e) => RequestStatusRefresh();

    private void RequestStatusRefresh()
    {
        if (!windowClosed && IsLoaded && !busy && !deviceBusy) _ = RefreshAsync();
    }

    private void UpdateRefreshInterval()
    {
        timer.Interval = RefreshPolicy.Interval(WindowState == WindowState.Minimized,
            dnsFilter is not null || deviceGuard is not null, status?.State == "On",
            status is null || status.Error is not null || status.State is not ("On" or "Off"));
    }
}
