using System.Windows.Input;

namespace Usque.Desktop;

internal sealed class RelayCommand(Action action, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        try { if (CanExecute(parameter)) action(); }
        catch (Exception ex) { onError(ex); }
    }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

internal sealed class AsyncCommand(Func<Task> action, Func<bool> canExecute, Action<Exception> onError) : ICommand
{
    private bool _executing;
    public bool CanExecute(object? parameter) => !_executing && canExecute();
    public async void Execute(object? parameter) => await ExecuteAsync();

    internal async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        _executing = true;
        CommandManager.InvalidateRequerySuggested();
        try { await action(); }
        catch (Exception ex) { onError(ex); }
        finally
        {
            _executing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
