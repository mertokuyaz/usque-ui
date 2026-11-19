using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Usque.Desktop;

public partial class MainWindow
{
    private HwndSource? _displaySource;
    private bool _displayFitQueued, _movingWindow;

    private void WatchDisplayChanges()
    {
        _displaySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _displaySource?.AddHook(OnDisplayMessage);
        QueueDisplayFit();
    }

    private void StopWatchingDisplayChanges()
    {
        _displaySource?.RemoveHook(OnDisplayMessage);
        _displaySource = null;
    }

    private IntPtr OnDisplayMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x0231: // WM_ENTERSIZEMOVE: don't clamp a window while dragging it.
                _movingWindow = true;
                break;
            case 0x0232: // WM_EXITSIZEMOVE
                _movingWindow = false;
                QueueDisplayFit();
                break;
            case 0x02E0: // WM_DPICHANGED (let WPF apply the suggested rectangle first)
            case 0x007E: // WM_DISPLAYCHANGE
            case 0x001A: // WM_SETTINGCHANGE (includes taskbar/work-area changes)
            case 0x0047: // WM_WINDOWPOSCHANGED (also keyboard/programmatic monitor moves)
                QueueDisplayFit();
                break;
        }
        return IntPtr.Zero;
    }

    private void QueueDisplayFit()
    {
        if (_displaySource == null || _displayFitQueued || Dispatcher.HasShutdownStarted) return;
        _displayFitQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _displayFitQueued = false;
            if (_displaySource == null || _movingWindow || WindowState != WindowState.Normal) return;
            var handle = _displaySource.Handle;
            var area = Forms.Screen.FromHandle(handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            FitToWorkArea(new Rect(0, 0, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY));
            // Screen rectangles and HWND positions are physical pixels. Clamp
            // in that space; mixing global DIP coordinates across monitors with
            // different scaling can put the title bar on the wrong display.
            if (!GetWindowRect(handle, out var bounds)) return;
            var constrained = ConstrainToWorkArea(new Rect(bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
                new Rect(area.X, area.Y, area.Width, area.Height));
            if (constrained.X != bounds.Left || constrained.Y != bounds.Top)
                SetWindowPos(handle, IntPtr.Zero, (int)constrained.X, (int)constrained.Y, 0, 0,
                    0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
        });
    }

    internal static Rect ConstrainToWorkArea(Rect bounds, Rect area) => new(
        Math.Clamp(bounds.X, area.Left, Math.Max(area.Left, area.Right - bounds.Width)),
        Math.Clamp(bounds.Y, area.Top, Math.Max(area.Top, area.Bottom - bounds.Height)),
        bounds.Width, bounds.Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
