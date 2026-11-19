using System.Threading.Channels;

namespace Usque.Core;

// One session owns all machine-wide networking. Unexpected failures NEVER
// remove protection; only an explicit StopAsync / recovery action does that.
public sealed class ConnectionController(IProfileStore profiles, IVpnEngine engine,
    INetworkManager network, IKillSwitch killSwitch, IConnectionHealth? health = null, ConnectionTimings? timings = null)
{
    private CancellationTokenSource? _session;
    private Task? _runner;
    private volatile bool _protectionActive;
    private sealed record ActiveAttempt(CancellationToken Token);
    private ActiveAttempt? _attempt;
    private int _healthEpoch;
    private readonly SemaphoreSlim _healthChecks = new(1, 1);
    private readonly ConnectionTimings _timings = timings ?? ConnectionTimings.Default;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private Channel<EnvironmentChange>? _environment;
    private volatile bool _suspended;
    private int _consecutiveFailures;
    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<HealthResult>? HealthChanged;
    public HealthResult? LastHealth { get; private set; }
    public bool ActiveHttp2 { get; private set; }
    public ConnectOptions ActiveOptions { get; private set; } = new();
    public int ReconnectCount { get; private set; }
    public DateTimeOffset? RetryAtUtc { get; private set; }
    public DateTimeOffset? LastSuccessfulCheckUtc { get; private set; }
    public bool TransportEstablished { get; private set; }
    public ConnectionStatus Status { get; private set; } = new(ConnectionState.Offline, Localization.Instance["detail.offline"])
        { DetailKey = "detail.offline" };
    // WPF evaluates this property frequently. Native queries belong at lifecycle
    // boundaries, not in bindings; a failed query is conservatively held active.
    public bool ProtectionActive => _protectionActive;
    public bool HasRecoveryState => network.HasRecoveryState;
    public bool ProfileExists => profiles.Exists;

    public void Initialize()
    {
        try
        {
            if (ReadProtectionState())
                SetLocalized(ConnectionState.Blocked, "detail.blocked");
            else if (network.HasRecoveryState)
                SetLocalized(ConnectionState.Error, "detail.needsCleanup");
        }
        catch (Exception ex)
        {
            SetLocalized(ConnectionState.Error, "err.protectionUnable", ex.Message);
        }
    }

    public async Task ConnectAsync(ConnectOptions options)
    {
        await _operations.WaitAsync();
        try
        {
            if (_runner is { IsCompleted: false }) return;
            options.Validate();
            LastHealth = null;
            ActiveHttp2 = options.Http2;
            ActiveOptions = options;
            ReconnectCount = 0;
            _consecutiveFailures = 0;
            LastSuccessfulCheckUtc = null;
            RetryAtUtc = null;
            _environment = Channel.CreateBounded<EnvironmentChange>(new BoundedChannelOptions(1)
                { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            _session?.Dispose();
            _session = new CancellationTokenSource();
            _runner = RunAsync(options, _session.Token);
        }
        finally { _operations.Release(); }
    }

    private async Task RunAsync(ConnectOptions options, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            SetLocalized(ConnectionState.Connecting, "detail.preparing");
            engine.ValidateInstallation(options.Mode);
            ReadProtectionState();
            // Proxy mode must never silently release an existing machine lock.
            if (options.IsProxy && (ProtectionActive || network.HasRecoveryState))
                throw new ConnectionPreconditionException("proxy.restoreFirst");
            // A stale NRPT rule can point DNS at a tunnel that no longer exists.
            // Remove it before registration, without lowering existing filters.
            if (network.HasRecoveryState) await network.CleanupAsync(token);
            token.ThrowIfCancellationRequested();
            // Registration is the only direct bootstrap request. Never silently
            // lower a kill switch left behind by a previous session.
            if (!profiles.Exists)
            {
                if (ProtectionActive)
                    throw new ConnectionPreconditionException("err.restoreFirst");
                SetLocalized(ConnectionState.Registering, "detail.registering");
                await engine.RegisterAsync(token);
                token.ThrowIfCancellationRequested();
            }
            var transport = options;
            while (true)
            {
                ActiveHttp2 = transport.Http2;
                ActiveOptions = transport;
                LastHealth = null;
                var profile = await profiles.LoadAsync(transport.Http2, token);
                token.ThrowIfCancellationRequested();
                SetLocalized(ConnectionState.Connecting, transport.Http2 ? "detail.connecting.http2" : "detail.connecting.quic");
                // Replace exceptions atomically while keeping the default blocks.
                // No Disarm call is allowed anywhere in the fallback path.
                if (!transport.IsProxy && (transport.KillSwitch || ProtectionActive))
                {
                    killSwitch.Arm(profile.Endpoint, transport.Http2);
                    _protectionActive = true;
                }
                token.ThrowIfCancellationRequested();
                try
                {
                    if (_suspended && options.AutoReconnect) await WaitForRetryAsync(_timings.RetryDelay, token);
                    if (!transport.IsProxy) await network.PrepareAsync(profile.Endpoint, token);
                    token.ThrowIfCancellationRequested();
                    await RunTransportAsync(transport, token);
                }
                catch (EnvironmentChangedException) when (options.AutoReconnect && !token.IsCancellationRequested)
                {
                    ReconnectCount++;
                    SetLocalized(ConnectionState.Reconnecting, "recovery.network");
                    if (!transport.IsProxy) await network.CleanupAsync(token);
                    await WaitForRetryAsync(_timings.RetryDelay, token);
                    continue;
                }
                catch (TransportAttemptException) when (options.AutomaticFallback && !transport.Http2 && !token.IsCancellationRequested)
                {
                    // RunTransportAsync has already killed and joined the old
                    // engine before routes, endpoint exception or profile change.
                    ReconnectCount++;
                    SetLocalized(ConnectionState.Reconnecting, ProtectionActive ? "detail.fallback.protected" : "detail.fallback.open");
                    if (!transport.IsProxy) await network.CleanupAsync(token);
                    token.ThrowIfCancellationRequested();
                    transport = transport with { Http2 = true };
                    continue;
                }
                catch (Exception ex) when (options.AutoReconnect && !token.IsCancellationRequested &&
                    ex is IOException or TimeoutException)
                {
                    ReconnectCount++;
                    // Cleanup failure is intentionally outside the retry catch:
                    // never build a new session on partially restored networking.
                    if (!transport.IsProxy) await network.CleanupAsync(token);
                    var seconds = Math.Min(_timings.MaximumRetryDelay.TotalSeconds,
                        _timings.RetryDelay.TotalSeconds * Math.Pow(2, Math.Min(_consecutiveFailures++, 8)));
                    await WaitForRetryAsync(TimeSpan.FromSeconds(seconds), token, ex.Message,
                        (ex as TransportAttemptException)?.DetailKey);
                    continue;
                }
                throw new TransportAttemptException(Localization.Instance["err.engineStopped"], detailKey: "err.engineStopped");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // The engine iterator is disposed before this block, killing its job.
            try { if (!options.IsProxy) await network.CleanupAsync(CancellationToken.None); }
            catch (Exception cleanup) { ex = new IOException($"{ex.Message} Cleanup: {cleanup.Message}", ex); }
            ReportFailure(ex);
        }
    }

    // Called by the Windows observer, never starts an offline/stopped session.
    public void NotifyEnvironmentChanged(EnvironmentChange change)
    {
        if (change == EnvironmentChange.Suspend) _suspended = true;
        else if (change == EnvironmentChange.Resume) _suspended = false;
        if (ActiveOptions.AutoReconnect && _session is { IsCancellationRequested: false })
            _environment?.Writer.TryWrite(change);
    }

    private async Task WaitForRetryAsync(TimeSpan delay, CancellationToken token, string? reason = null, string? reasonKey = null)
    {
        var changes = _environment!.Reader;
        try
        {
            while (true)
            {
                while (changes.TryRead(out _)) { }
                if (_suspended)
                {
                    RetryAtUtc = null;
                    SetLocalized(ConnectionState.Reconnecting, "recovery.suspended");
                    await changes.ReadAsync(token);
                    continue;
                }
                RetryAtUtc = DateTimeOffset.UtcNow + delay;
                var key = ProtectionActive ? "recovery.wait.protected" : "recovery.wait.open";
                var suffix = reason == null ? "" : " " + reason;
                Set(ConnectionState.Reconnecting, Localization.Instance[key] + suffix, key, suffix: suffix, suffixKey: reasonKey);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                var timer = Task.Delay(delay, wait.Token);
                var changed = changes.ReadAsync(wait.Token).AsTask();
                var finished = await Task.WhenAny(timer, changed);
                wait.Cancel();
                try { await timer; } catch (OperationCanceledException) { }
                try { await changed; } catch (OperationCanceledException) { }
                token.ThrowIfCancellationRequested();
                if (finished == timer) return;
                // Coalesce bursts of adapter changes before preparing new routes.
                delay = _timings.RetryDelay;
            }
        }
        finally { RetryAtUtc = null; }
    }

    private async Task RunTransportAsync(ConnectOptions options, CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        var active = new ActiveAttempt(attempt.Token);
        Volatile.Write(ref _attempt, active);
        Interlocked.Increment(ref _healthEpoch);
        var updates = Channel.CreateBounded<TunnelEvent>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in engine.RunAsync(options, attempt.Token).WithCancellation(attempt.Token))
                    await updates.Writer.WriteAsync(update, attempt.Token);
                updates.Writer.TryComplete();
            }
            catch (Exception ex) { updates.Writer.TryComplete(ex); }
        });
        Task<TunnelEvent>? next = null;
        var environmentChange = options.AutoReconnect ? _environment!.Reader.ReadAsync(attempt.Token).AsTask() : null;
        Task<HealthResult?>? healthCheck = null;
        CancellationTokenSource? deadlineCancellation = null, healthCancellation = null, probeCancellation = null;
        bool initialHealthCheck = false, verificationPending = false;
        void StartHealthCheck(bool initial)
        {
            probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
            initialHealthCheck = initial;
            verificationPending = false;
            healthCheck = ReadHealthAsync(probeCancellation.Token);
        }
        Task Delay(ref CancellationTokenSource? timer, TimeSpan duration)
        {
            timer?.Cancel(); timer?.Dispose();
            timer = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
            return Task.Delay(duration, timer.Token);
        }
        try
        {
            bool configured = false, connected = false;
            var deadline = Delay(ref deadlineCancellation, _timings.TransportTimeout);
            var healthTimer = Delay(ref healthCancellation, Timeout.InfiniteTimeSpan);
            while (true)
            {
                next ??= updates.Reader.ReadAsync(attempt.Token).AsTask();
                // Probes must not stop us from reading tunnel loss/exit events or
                // enforcing the startup deadline. Prefer queued engine events.
                var completed = await Task.WhenAny(next, deadline, healthTimer, healthCheck ?? healthTimer, environmentChange ?? healthTimer);
                token.ThrowIfCancellationRequested();
                if (environmentChange?.IsCompletedSuccessfully == true) throw new EnvironmentChangedException();
                if (next.IsCompleted) completed = next;
                else if (deadline.IsCompleted) completed = deadline;
                if (completed == deadline) throw new TransportAttemptException(Localization.Instance["err.transportTimeout"],
                    detailKey: "err.transportTimeout");
                if (completed == healthTimer)
                {
                    StartHealthCheck(initial: false);
                    healthTimer = Delay(ref healthCancellation, Timeout.InfiniteTimeSpan);
                    continue;
                }
                if (completed == healthCheck)
                {
                    var cancelled = probeCancellation!.IsCancellationRequested;
                    HealthResult? result = null;
                    try { result = await healthCheck; }
                    catch (OperationCanceledException) when (cancelled) { }
                    healthCheck = null;
                    probeCancellation.Dispose(); probeCancellation = null;
                    if (!cancelled && result != null)
                    {
                        ApplyHealth(result, initialHealthCheck);
                        if (initialHealthCheck) deadline = Delay(ref deadlineCancellation, Timeout.InfiniteTimeSpan);
                        healthTimer = Delay(ref healthCancellation, _timings.HealthInterval);
                    }
                    // A reconnect may arrive before the cancelled probe finishes.
                    if (verificationPending) StartHealthCheck(initial: true);
                    continue;
                }
                TunnelEvent update;
                try { update = await next; }
                catch (ChannelClosedException ex)
                {
                    throw new TransportAttemptException(ex.InnerException?.Message ?? Localization.Instance["err.engineStopped"], ex,
                        ex.InnerException == null ? "err.engineStopped" : null);
                }
                next = null;
                token.ThrowIfCancellationRequested();
                switch (update.State)
                {
                    case "ready":
                        if (options.IsProxy) { configured = true; break; }
                        if (string.IsNullOrWhiteSpace(update.Interface)) throw new InvalidDataException("The VPN engine did not report its adapter.");
                        if (ProtectionActive)
                        {
                            var luid = await network.GetTunnelLuidAsync(update.Interface, attempt.Token);
                            token.ThrowIfCancellationRequested();
                            killSwitch.AllowTunnel(luid);
                        }
                        await network.ConfigureAsync(update.Interface, attempt.Token);
                        configured = true;
                        break;
                    case "connected" when configured && !connected:
                        connected = true;
                        TransportEstablished = true;
                        if (health != null)
                        {
                            SetLocalized(ConnectionState.Verifying, "detail.verifying");
                            if (healthCheck == null) StartHealthCheck(initial: true);
                            else verificationPending = true;
                        }
                        else
                        {
                            _consecutiveFailures = 0;
                            SetLocalized(ConnectionState.Connected, options.IsProxy ? "proxy.connected" : "detail.connected.nohealth");
                            deadline = Delay(ref deadlineCancellation, Timeout.InfiniteTimeSpan);
                        }
                        break;
                    case "disconnected":
                        connected = false;
                        TransportEstablished = false;
                        ReconnectCount++;
                        verificationPending = false;
                        InvalidateHealth();
                        probeCancellation?.Cancel();
                        SetLocalized(ConnectionState.Reconnecting, ProtectionActive ? "detail.reconnecting.protected" : "detail.reconnecting.open");
                        deadline = Delay(ref deadlineCancellation, _timings.TransportTimeout);
                        healthTimer = Delay(ref healthCancellation, Timeout.InfiniteTimeSpan);
                        break;
                }
            }
        }
        finally
        {
            InvalidateHealth();
            TransportEstablished = false;
            attempt.Cancel();
            deadlineCancellation?.Dispose(); healthCancellation?.Dispose();
            await producer;
            if (next != null) { try { await next; } catch (Exception) { } }
            if (environmentChange != null) { try { await environmentChange; } catch (OperationCanceledException) { } }
            // Observe and join every probe before fallback can change networking.
            if (healthCheck != null) { try { await healthCheck; } catch (Exception) { } }
            probeCancellation?.Dispose();
            Interlocked.CompareExchange(ref _attempt, null, active);
        }
    }

    public Task CheckHealthNowAsync()
    {
        var active = Volatile.Read(ref _attempt);
        if (active == null || Status.State is not (ConnectionState.Connected or ConnectionState.Degraded))
            throw new InvalidOperationException(Localization.Instance["err.connectFirst"]);
        return CheckHealthAsync(active.Token);
    }

    private async Task CheckHealthAsync(CancellationToken token)
    {
        var epoch = Volatile.Read(ref _healthEpoch);
        var result = await ReadHealthAsync(token);
        token.ThrowIfCancellationRequested();
        if (result != null && epoch == Volatile.Read(ref _healthEpoch)) ApplyHealth(result, initial: false);
    }

    private async Task<HealthResult?> ReadHealthAsync(CancellationToken token)
    {
        if (health == null) return null;
        var epoch = Volatile.Read(ref _healthEpoch);
        await _healthChecks.WaitAsync(token);
        try
        {
            if (epoch != Volatile.Read(ref _healthEpoch)) return null;
            var result = await health.CheckAsync(ActiveOptions, token);
            token.ThrowIfCancellationRequested();
            return epoch == Volatile.Read(ref _healthEpoch) ? result : null;
        }
        finally { _healthChecks.Release(); }
    }

    private void ApplyHealth(HealthResult result, bool initial)
    {
        LastHealth = result;
        if (result.Healthy) { LastSuccessfulCheckUtc = result.CheckedUtc; _consecutiveFailures = 0; }
        HealthChanged?.Invoke(result);
        if (initial && !result.Healthy) throw new TransportAttemptException(result.Detail, detailKey: result.DetailKey);
        var state = result.Healthy ? ConnectionState.Connected : ConnectionState.Degraded;
        if (initial || Status.State != state) Set(state, result.Detail, result.DetailKey);
    }

    private sealed class TransportAttemptException(string message, Exception? inner = null, string? detailKey = null) : IOException(message, inner)
    {
        public string? DetailKey { get; } = detailKey;
    }
    private sealed class ConnectionPreconditionException(string key) : InvalidOperationException(Localization.Instance[key])
    {
        public string DetailKey { get; } = key;
    }
    private sealed class EnvironmentChangedException : IOException;

    public async Task StopAsync()
    {
        await _operations.WaitAsync();
        try
        {
            InvalidateHealth();
            SetLocalized(ConnectionState.Disconnecting, "detail.disconnecting");
            _session?.Cancel();
            if (_runner != null) await _runner;
            _runner = null;
            _session?.Dispose();
            _session = null;
            _environment = null;
            RetryAtUtc = null;
            // Restore networking while protection is STILL active. If cleanup
            // fails, leave the filters in place and allow recovery to be retried.
            if (!ActiveOptions.IsProxy || network.HasRecoveryState || ProtectionActive)
                await network.CleanupAsync(CancellationToken.None);
            if (!ActiveOptions.IsProxy || ProtectionActive) killSwitch.Disarm();
            _protectionActive = false;
            SetLocalized(ConnectionState.Offline, "detail.offline");
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            throw;
        }
        finally { _operations.Release(); }
    }

    private bool ReadProtectionState()
    {
        try { return _protectionActive = killSwitch.IsEnabled; }
        catch
        {
            // Unknown is not the same as disabled. Do not permit profile changes
            // or bootstrap requests, and never claim verified protection in UI.
            _protectionActive = true;
            throw;
        }
    }

    private void ReportFailure(Exception error)
    {
        InvalidateHealth();
        bool active;
        try { active = ReadProtectionState(); }
        catch (Exception statusError)
        {
            SetLocalized(ConnectionState.Error, "err.protectionUnknown", error.Message, statusError.Message);
            return;
        }
        var key = error switch
        {
            TransportAttemptException transport => transport.DetailKey,
            ConnectionPreconditionException precondition => precondition.DetailKey,
            _ => null
        };
        Set(active ? ConnectionState.Blocked : ConnectionState.Error, error.Message, key);
    }

    private void InvalidateHealth()
    {
        Interlocked.Increment(ref _healthEpoch);
        LastHealth = null;
    }

    private void SetLocalized(ConnectionState state, string key, params object[] arguments) =>
        Set(state, Localization.Instance.Format(key, arguments), key, arguments);

    private void Set(ConnectionState state, string detail, string? key = null, object[]? arguments = null,
        string suffix = "", string? suffixKey = null)
    {
        Status = new(state, detail)
            { DetailKey = key, DetailArguments = arguments ?? [], DetailSuffix = suffix, DetailSuffixKey = suffixKey };
        StatusChanged?.Invoke(Status);
    }
}
