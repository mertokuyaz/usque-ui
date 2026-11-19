using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Usque.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private TrayController? _tray;
    private bool _allowClose, _closing, _forceExit, _closed;
    // notificationArea: false is for hosts (tests) that must not create a real tray icon.
    public MainWindow(MainViewModel viewModel, bool notificationArea = true)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WatchDisplayChanges();
            if (notificationArea && !viewModel.IsPreview) _tray = new TrayController(this, viewModel);
        };
        Activated += (_, _) => { QueueDisplayFit(); _ = viewModel.RefreshStartupAsync(); };
        StateChanged += (_, _) => QueueDisplayFit();
        Closed += (_, _) => { _closed = true; StopWatchingDisplayChanges(); _tray?.Dispose(); viewModel.Dispose(); };
    }
    internal void FitToWorkArea(Rect workArea)
    {
        // Width/Height alone are coerced by WPF when MinWidth/MinHeight are
        // larger than a small or high-DPI display's logical working area.
        MinWidth = Math.Min(900, workArea.Width);
        MinHeight = Math.Min(600, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
    }
    private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    // Replacing the bound log text resets a TextBox to its first line. Follow new
    // entries while the reader is at the end; otherwise keep their place.
    private void OnLogChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var offset = box.VerticalOffset;
        var following = offset + box.ViewportHeight >= box.ExtentHeight - 1;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (following) box.ScrollToEnd();
            else box.ScrollToVerticalOffset(offset);
        });
    }
    private void OnLogVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, box.ScrollToEnd);
    }
    private void OnMinimize(object sender, RoutedEventArgs e) => MinimizeWindow();
    internal void MinimizeWindow()
    {
        if (_viewModel.MinimizeToTray && _tray != null) HideToTray();
        else
        {
            ShowInTaskbar = true;
            WindowState = WindowState.Minimized;
        }
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnExit(object sender, RoutedEventArgs e) => RequestExit();
    public void HideToTray() { if (_tray != null) { Hide(); _tray.ExplainBackground(); } }
    public void RestoreFromTray()
    {
        // Tray clicks are still delivered while the modal exit question runs
        // inside Closing, where WPF throws on Show(). That dialog already has
        // the user's attention; never let a reopen request crash the app.
        if (_closed || _closing) return;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    public void RequestExit() { if (_viewModel.CanExit) { _forceExit = true; Close(); } }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && !_viewModel.CanExit)
        {
            e.Cancel = true;
            if (!_forceExit) MinimizeWindow();
            return;
        }
        if (!_forceExit && !_allowClose)
        {
            e.Cancel = true;
            MinimizeWindow();
            return;
        }
        if (_allowClose || !_viewModel.NeedsExitCleanup) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _viewModel.BeginExit();
        try
        {
            var choice = _viewModel.ConfirmExit();
            if (choice == MessageBoxResult.Cancel) return;
            // A new Connect command must not race the awaited shutdown cleanup.
            IsEnabled = false;
            if (choice == MessageBoxResult.Yes) await _viewModel.DisconnectForExitAsync();
            _allowClose = true;
            // "Keep protection" (and an already-clean restore) completes without
            // yielding. WPF rejects Close() while this Closing event is running,
            // so close once the current close attempt has unwound.
            await Dispatcher.Yield(DispatcherPriority.Normal);
            if (!_closed) Close();
        }
        catch (Exception ex) { _viewModel.ReportExitError(ex); }
        finally
        {
            _closing = false;
            if (!_allowClose)
            {
                _forceExit = false;
                _viewModel.CancelExit();
                IsEnabled = true;
            }
        }
    }
}
