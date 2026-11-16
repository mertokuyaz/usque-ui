using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Usque.Core;

namespace Usque.Desktop;

internal sealed class TrayController : IDisposable
{
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _drawing;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.ToolStripMenuItem _state = new() { Enabled = false };
    private readonly Forms.ToolStripMenuItem _open = new();
    private readonly Forms.ToolStripMenuItem _power = new();
    private readonly Forms.ToolStripMenuItem _restore = new();
    private readonly Forms.ToolStripMenuItem _exit = new();
    private readonly TrayPresentation _presentation;
    private bool _explained;
    private bool _disposed;

    public TrayController(MainWindow window, MainViewModel viewModel)
    {
        _window = window; _viewModel = viewModel;
        _presentation = new(viewModel.State);
        _drawing = CreateIcon();
        _icon = new Forms.NotifyIcon { Icon = _drawing, Text = "Usque Desktop", ContextMenuStrip = _menu, Visible = true };
        _menu.Items.Add(_state);
        _open.Click += (_, _) => ShowWindow();
        _menu.Items.Add(_open);
        _power.Click += (_, _) => Dispatch(() => { if (viewModel.PowerCommand.CanExecute(null)) viewModel.PowerCommand.Execute(null); });
        _menu.Items.Add(_power);
        _restore.Click += (_, _) => Dispatch(() => { if (viewModel.RestoreCommand.CanExecute(null)) viewModel.RestoreCommand.Execute(null); });
        _menu.Items.Add(_restore);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _exit.Click += (_, _) => Dispatch(window.RequestExit);
        _menu.Items.Add(_exit);
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.BalloonTipClicked += (_, _) => ShowWindow();
        _icon.MouseMove += (_, _) => Dispatch(() => UpdateTooltip(TrayPresentation.FormatTooltip(viewModel.TrayStatus)));
        viewModel.PropertyChanged += Update;
        Update(null, new(string.Empty));
    }
    private void ShowWindow() => Dispatch(_window.RestoreFromTray);
    private void Dispatch(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (_disposed || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }
    private void Update(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(MainViewModel.TrayStatus))
        {
            Apply(_presentation.Update(_viewModel.TrayStatus, trafficSample: true));
            return;
        }
        // Rate ticks have their own single combined update. Do not rebuild the
        // menu for every individual download/upload/session-time notification.
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName is not
            (nameof(MainViewModel.State) or nameof(MainViewModel.Title) or nameof(MainViewModel.Detail) or
             nameof(MainViewModel.CanPower) or nameof(MainViewModel.CanRestore) or nameof(MainViewModel.IsExiting)))
            return;
        var local = AppLocalization.Instance;
        _state.Text = _viewModel.Title;
        _open.Text = local["tray.open"];
        _power.Text = local[_viewModel.IsSession ? "tray.disconnect" : "tray.connect"];
        _power.Enabled = _viewModel.CanPower;
        _restore.Text = local["tray.restore"];
        _restore.Enabled = _viewModel.RestoreCommand.CanExecute(null);
        _exit.Text = local["tray.exit"];
        _exit.Enabled = !_viewModel.IsExiting;
        Apply(_presentation.Update(_viewModel.TrayStatus));
    }
    private void UpdateTooltip(string tooltip)
    {
        if (_icon.Text != tooltip) _icon.Text = tooltip;
    }
    private void Apply(TrayUpdate update)
    {
        UpdateTooltip(update.Tooltip);
        if (update.Notice is { } notice)
            _icon.ShowBalloonTip(4000, notice.Title, notice.Body,
                notice.Warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }
    public void ExplainBackground()
    {
        if (_explained) return;
        _explained = true;
        var local = AppLocalization.Instance;
        _icon.ShowBalloonTip(3000, local["tray.runningTitle"], local["tray.runningHint"], Forms.ToolTipIcon.Info);
    }
    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Drawing.Color.Transparent);
        using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(103, 228, 191));
        graphics.FillEllipse(fill, 1, 1, 30, 30);
        using var pen = new Drawing.Pen(Drawing.Color.FromArgb(14, 48, 39), 3);
        graphics.DrawArc(pen, 9, 10, 14, 14, 0, 180);
        graphics.DrawLine(pen, 9, 10, 9, 17);
        graphics.DrawLine(pen, 23, 10, 23, 17);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Drawing.Icon.FromHandle(handle); return (Drawing.Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= Update;
        _icon.Visible = false;
        _icon.Dispose(); _menu.Dispose(); _drawing.Dispose();
    }
}
