using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Usque.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private TrayController? _tray;
    private bool _allowClose, _closing, _forceExit;
    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        FitToWorkArea(SystemParameters.WorkArea);
        SourceInitialized += (_, _) => { if (!viewModel.IsPreview) _tray = new TrayController(this, viewModel); };
        Closed += (_, _) => { _tray?.Dispose(); viewModel.Dispose(); };
    }
    internal void FitToWorkArea(Rect workArea)
    {
        // Width/Height alone are coerced by WPF when MinWidth/MinHeight are
        // larger than a small or high-DPI display's logical working area.
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
    }
    private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
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
    public void RestoreFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    public void RequestExit() { _forceExit = true; Close(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
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
            Close();
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
