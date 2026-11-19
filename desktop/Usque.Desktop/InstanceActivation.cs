using System.Runtime.InteropServices;

namespace Usque.Desktop;

// A second launch in the same Windows session asks the running instance to show
// its window instead of reporting "already running". The single-instance mutex
// is machine-wide, but this event lives in the session-local namespace, so a
// launch never raises another signed-in user's window on their desktop.
internal sealed class InstanceActivation : IDisposable
{
    internal const string DefaultName = @"Local\UsqueDesktop-57d78a76-activate";
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _registration;
    private int _disposed;

    private InstanceActivation(EventWaitHandle signal, Action activate)
    {
        _signal = signal;
        _registration = ThreadPool.RegisterWaitForSingleObject(signal,
            (_, _) => { if (Volatile.Read(ref _disposed) == 0) activate(); }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    // `activate` runs on a thread-pool thread; marshal to the UI yourself.
    public static InstanceActivation Listen(Action activate, string name = DefaultName) =>
        new(new EventWaitHandle(false, EventResetMode.AutoReset, name), activate);

    public static bool TrySignal(string name = DefaultName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var signal)) return false;
            using (signal)
            {
                // This process was started by the user and may own the foreground;
                // let the running instance take focus when it shows its window.
                AllowSetForegroundWindow(AnyProcess);
                return signal.Set();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registration.Unregister(null);
        _signal.Dispose();
    }

    private const int AnyProcess = -1;
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
