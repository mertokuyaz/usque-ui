using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Usque.Core;

internal sealed class Fixture
{
    public List<string> Calls { get; } = [];
    public FakeProfile Profiles { get; }
    public FakeEngine Engine { get; }
    public FakeNetwork Network { get; }
    public FakeKillSwitch Kill { get; }
    public ConnectionController Controller { get; }
    public Fixture(bool existing = false, IConnectionHealth? health = null, ConnectionTimings? timings = null)
    {
        Profiles = new() { Exists = existing };
        Engine = new(Profiles, Calls); Network = new(Calls); Kill = new(Calls);
        Controller = new(Profiles, Engine, Network, Kill, health, timings);
    }
    public async Task Connect()
    {
        var wait = WaitFor(ConnectionState.Connected);
        Engine.Events.Writer.TryWrite(new("ready", NetworkManager.InterfaceName));
        Engine.Events.Writer.TryWrite(new("connected"));
        await wait;
    }
    public Task WaitFor(ConnectionState state, bool requireNew = false)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ConnectionStatus value)
        {
            if (value.State != state) return;
            Controller.StatusChanged -= Handler;
            tcs.TrySetResult();
        }
        Controller.StatusChanged += Handler;
        if (!requireNew && Controller.Status.State == state) Handler(Controller.Status);
        return tcs.Task;
    }
}

internal sealed class FakeProfile : IProfileStore
{
    public bool Exists { get; set; }
    public Task? LoadGate { get; set; }
    public async Task<WarpProfile> LoadAsync(bool http2, CancellationToken cancellationToken)
    {
        if (LoadGate != null) await LoadGate;
        return new(http2 ? "162.159.198.2" : "162.159.198.1", "172.16.0.2", "2606:4700::2", "id");
    }
}

internal sealed class FakeEngine(FakeProfile profiles, List<string> calls) : IVpnEngine
{
    public int Registrations { get; private set; }
    public int Starts { get; private set; }
    public Action? BeforeRegister { get; set; }
    public bool HoldRegistration { get; set; }
    public Queue<Channel<TunnelEvent>> Attempts { get; } = new();
    public List<ConnectOptions> StartedOptions { get; } = [];
    public Channel<TunnelEvent> Events { get; } = Channel.CreateUnbounded<TunnelEvent>();
    public void ValidateInstallation() { }
    public async Task RegisterAsync(CancellationToken cancellationToken)
    {
        BeforeRegister?.Invoke();
        Registrations++;
        if (HoldRegistration) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        profiles.Exists = true;
    }
    public async IAsyncEnumerable<TunnelEvent> RunAsync(ConnectOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Starts++;
        StartedOptions.Add(options);
        calls.Add("engine-start");
        var source = Attempts.TryDequeue(out var scripted) ? scripted : Events;
        try { await foreach (var item in source.Reader.ReadAllAsync(cancellationToken)) yield return item; }
        finally { calls.Add("engine-stop"); }
    }
}

internal sealed class FakeNetwork(List<string> calls) : INetworkManager
{
    public bool FailConfigure { get; set; }
    public bool FailCleanup { get; set; }
    public bool HasRecoveryState { get; set; }
    public Task? CleanupGate { get; set; }
    public Task PrepareAsync(string endpoint, CancellationToken cancellationToken) { calls.Add("prepare"); HasRecoveryState = true; return Task.CompletedTask; }
    public Task<ulong> GetTunnelLuidAsync(string name, CancellationToken cancellationToken) => Task.FromResult(123ul);
    public Task ConfigureAsync(string name, CancellationToken cancellationToken)
    {
        calls.Add("configure");
        if (FailConfigure) throw new IOException("simulated route failure");
        return Task.CompletedTask;
    }
    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        calls.Add("cleanup");
        if (CleanupGate != null) await CleanupGate;
        if (FailCleanup) throw new IOException("simulated cleanup failure");
        HasRecoveryState = false;
    }
}

internal sealed class FakeKillSwitch(List<string> calls) : IKillSwitch
{
    public bool Enabled { get; set; }
    public bool FailRead { get; set; }
    public int Reads { get; private set; }
    public Action? BeforeArm { get; set; }
    public bool IsEnabled
    {
        get
        {
            Reads++;
            if (FailRead) throw new IOException("simulated WFP status failure");
            return Enabled;
        }
    }
    public void Arm(string endpoint, bool http2) { BeforeArm?.Invoke(); calls.Add("arm"); Enabled = true; }
    public void AllowTunnel(ulong luid) => calls.Add("allow-tunnel");
    public void Disarm() { calls.Add("disarm"); Enabled = false; }
}
