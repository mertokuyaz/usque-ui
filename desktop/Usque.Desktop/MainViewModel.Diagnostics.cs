using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using Usque.Core;

namespace Usque.Desktop;

public sealed record DiagnosticRow(string Name, string Result, string Detail, string Advice);

public sealed partial class MainViewModel
{
    public string Logs => string.Join(Environment.NewLine, _logs);
    public bool CanDiagnose => IsAvailable && _runDiagnostics != null && !_acting && !_startupBusy && !_diagnosing && !_checkingHealth && !IsBusy;
    public ObservableCollection<DiagnosticRow> DiagnosticChecks { get; } = [];
    public string DiagnosticCheckedAt => _diagnosticSnapshot is { } snapshot
        ? L.Format("health.checkedAt", snapshot.CheckedUtc.ToLocalTime().ToString("HH:mm:ss")) : L["diag.notRun"];
    public string HealthSummary => _controller?.LastHealth is { } health ? health.Localize(_redactor.Redact) : L["health.noData"];
    public string HealthCheckedAt => _controller?.LastHealth is { } health
        ? L.Format("health.checkedAt", health.CheckedUtc.ToLocalTime().ToString("HH:mm:ss")) : L["health.notChecked"];
    public string Latency => _controller?.LastHealth?.LatencyMilliseconds is { } latency
        ? $"{latency:0} {L["health.probeSuffix"]}" : L["health.none"];

    private void ExportDiagnostics()
    {
        var target = _interaction.SelectReport(L["dlg.appTitle"], $"usque-diagnostics-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");
        if (target == null) return;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var report = DiagnosticsWriter.Create(version, _status, _controller?.ActiveHttp2 ?? false, Preferences,
            _controller?.ProtectionActive ?? false, _controller?.LastHealth, IsProxy ? new() : _traffic.Current, _logs, _redactor,
            _controller?.ReconnectCount ?? 0, _diagnosticSnapshot);
        DiagnosticsWriter.Write(target, report);
        AddLog(L["log.exported"]);
    }
    private async Task CheckHealthAsync()
    {
        if (_controller == null) return;
        _checkingHealth = true; Refresh();
        try { await _controller.CheckHealthNowAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
        finally { _checkingHealth = false; Refresh(); }
    }
    private async Task DiagnoseAsync()
    {
        if (_runDiagnostics == null) return;
        var generation = _diagnosticGeneration;
        _diagnosing = true; Refresh();
        try
        {
            var snapshot = await _runDiagnostics(Mode, _lifetime.Token).WaitAsync(_lifetime.Token);
            if (_disposed || generation != _diagnosticGeneration) return;
            _diagnosticSnapshot = snapshot;
            RefreshDiagnosticRows();
            AddLog(L["diag.completed"]);
        }
        catch (OperationCanceledException) { }
        finally { _diagnosing = false; Refresh(); }
    }
    private void ClearDiagnostics()
    {
        _diagnosticGeneration++;
        _diagnosticSnapshot = null;
        DiagnosticChecks.Clear();
    }
    private void RefreshDiagnosticRows()
    {
        DiagnosticChecks.Clear();
        if (_diagnosticSnapshot == null) return;
        foreach (var check in _diagnosticSnapshot.Checks)
            DiagnosticChecks.Add(new(L[check.NameKey], L["diag.outcome." + check.Outcome],
                L[check.DetailKey], string.IsNullOrEmpty(check.AdviceKey) ? "" : L[check.AdviceKey]));
    }
    public void AddLog(string line)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess()) { Dispatch(() => AddLog(line)); return; }
        var safe = _redactor.Redact(line);
        if (safe.Length > 2048) safe = safe[..2048] + " [truncated]";
        _logs.Enqueue($"{DateTime.Now:HH:mm:ss}  {safe}");
        while (_logs.Count > 200) _logs.Dequeue();
        Changed(nameof(Logs));
        CommandManager.InvalidateRequerySuggested();
    }
}
