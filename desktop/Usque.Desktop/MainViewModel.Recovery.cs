using Usque.Core;

namespace Usque.Desktop;

public sealed partial class MainViewModel
{
    public bool ShowConnectionHelp => ShowRecovery || State == ConnectionState.Degraded;
    private bool ProtectionUnknown => _status.DetailKey is "err.protectionUnable" or "err.protectionUnknown";
    private bool RequiresRestoreBeforeRetry => _status.DetailKey is "detail.needsCleanup" or "err.restoreFirst" or "proxy.restoreFirst" ||
        _controller != null && (IsProxy && (_controller.ProtectionActive || _controller.HasRecoveryState) ||
            !_controller.ProfileExists && _controller.ProtectionActive);
    public bool ShowRetryConnection => ShowRecovery && !RequiresRestoreBeforeRetry;
    public bool ShowHealthRetry => State == ConnectionState.Degraded;
    public bool ShowRestoreRecovery => ShowConnectionHelp &&
        (_controller?.ProtectionActive == true || _controller?.HasRecoveryState == true);
    public bool CanRetryConnection => ShowRetryConnection && CanPower &&
        _controller?.Status.State is (ConnectionState.Blocked or ConnectionState.Error);

    // Prefer structured keys and diagnostic outcomes over guessing from raw,
    // potentially translated backend exception strings.
    private DiagnosticCheck? RelevantDiagnosticFailure =>
        _diagnosticSnapshot != null && (_controller?.LastHealth == null ||
            _diagnosticSnapshot.CheckedUtc >= _controller.LastHealth.CheckedUtc)
        ? _diagnosticSnapshot.Checks.FirstOrDefault(c => c.Outcome == DiagnosticOutcome.Failed) : null;
    private string? FailureDetailKey => _controller?.LastHealth is { Healthy: false } health
        ? health.DetailKey ?? (health.InternetReachable == false ? "health.failed.internet"
            : !health.WarpVerified && health.InternetReachable == true ? "health.failed.warp"
            : health.WarpVerified && !health.DnsReachable ? "health.failed.dns" : null)
        : _status.DetailKey;

    public string ConnectionHelpSummary
    {
        get
        {
            if (ProtectionUnknown) return L["help.failure"];
            if (RequiresRestoreBeforeRetry) return L[_status.DetailKey is "err.restoreFirst" or "proxy.restoreFirst"
                ? _status.DetailKey : "detail.needsCleanup"];
            if (RelevantDiagnosticFailure is { } failure) return L[failure.DetailKey];
            var key = FailureDetailKey;
            return key switch
            {
                "detail.blocked" or "health.failed.internet" or "health.failed.warp" or "health.failed.dns" or "err.transportTimeout" => L[key],
                _ => L[ShowHealthRetry ? "help.health" : "help.failure"]
            };
        }
    }

    public string ConnectionHelpAdvice
    {
        get
        {
            if (ProtectionUnknown || RequiresRestoreBeforeRetry) return "";
            if (RelevantDiagnosticFailure is { } failure) return L[failure.AdviceKey];
            return FailureDetailKey switch
            {
                "health.failed.internet" => L["diag.fix.internet"],
                "health.failed.warp" => L["diag.fix.warp"],
                "health.failed.dns" => L["diag.fix.dns"],
                "err.transportTimeout" => L["diag.fix.endpoint"],
                _ => ""
            };
        }
    }

    public string ConnectionHelpProtection => L[ProtectionUnknown ? "help.unknown"
        : _controller?.ProtectionActive == true ? "help.protected" : IsProxy ? "proxy.warning" : "help.unprotected"];

    private async Task RetryConnectionAsync()
    {
        if (!CanRetryConnection) return;
        // Unlike the power toggle, this action must never disconnect a session
        // that started while the panel was updating or a consent dialog was open.
        await ConnectAsync();
    }
}
