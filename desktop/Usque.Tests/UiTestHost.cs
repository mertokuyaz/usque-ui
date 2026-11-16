using System.Windows;
using System.Windows.Threading;
using Usque.Desktop;

// One background STA and one startup-suppressed Application for the entire test
// process. Individual UI tests remain independently selectable by the runner.
internal static class UiTestHost
{
    private static readonly Lazy<Task<Dispatcher>> Host = new(Start);

    public static async Task RunAsync(Func<Task> test)
    {
        var dispatcher = await Host.Value;
        await dispatcher.InvokeAsync(test).Task.Unwrap();
    }

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App { SuppressStartupForTests = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "Usque UI tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
}
