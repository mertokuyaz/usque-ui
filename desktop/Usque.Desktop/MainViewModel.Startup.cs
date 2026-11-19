namespace Usque.Desktop;

public sealed partial class MainViewModel
{
    private bool? _startupEnabled;
    private bool _startupBusy, _refreshingStartup;
    internal Task StartupOperation { get; private set; } = Task.CompletedTask;
    public bool CanExit => IsAvailable && !_startupBusy;

    internal Task RefreshStartupAsync()
    {
        if (!IsAvailable || _startup == null || _preferences == null || _startupBusy || _refreshingStartup)
            return StartupOperation;
        _refreshingStartup = true;
        Refresh();
        return StartupOperation = ReadStartupAsync();
    }

    private async Task ReadStartupAsync()
    {
        try { _startupEnabled = await Task.Run(() => _startup!.IsEnabled); }
        catch (Exception ex) { _startupEnabled = null; AddLog(ex.Message); }
        finally { _refreshingStartup = false; Refresh(); }
    }

    private async Task SetStartupEnabledAsync(bool enabled)
    {
        try
        {
            var previous = await Task.Run(() => _startup!.IsEnabled);
            // Copying/verifying the protected package and talking to Task
            // Scheduler can take seconds. Neither belongs on the dispatcher.
            await Task.Run(() => _startup!.SetEnabled(enabled));
            try
            {
                // Save on the dispatcher using the latest preferences, so a
                // language change made during the copy is not overwritten.
                Save(Preferences with { StartWithWindows = enabled });
            }
            catch (Exception saveError)
            {
                try { await Task.Run(() => _startup!.SetEnabled(previous)); }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("Settings could not be saved and the startup task could not be rolled back.",
                        saveError, rollbackError);
                }
                throw;
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            await ReadStartupAsync();
            _startupBusy = false;
            Refresh();
        }
    }
}
