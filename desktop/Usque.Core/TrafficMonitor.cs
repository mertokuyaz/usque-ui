using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Usque.Core;

public sealed record TrafficSnapshot(double DownloadBytesPerSecond = 0, double UploadBytesPerSecond = 0,
    long DownloadedBytes = 0, long UploadedBytes = 0, bool AdapterAvailable = false);

public sealed class TrafficAccumulator
{
    private string? _adapter;
    private long _received, _sent;
    private TimeSpan _time;
    private bool _sampled;
    public TrafficSnapshot Current { get; private set; } = new();
    public void Begin(string? adapter, long received, long sent, TimeSpan time)
    {
        _adapter = adapter; _received = received; _sent = sent; _time = time; _sampled = true;
        Current = new();
    }
    public TrafficSnapshot Sample(string? adapter, long received, long sent, TimeSpan time)
    {
        if (adapter == null)
        {
            _adapter = null;
            _time = time;
            return Current = Current with { DownloadBytesPerSecond = 0, UploadBytesPerSecond = 0, AdapterAvailable = false };
        }
        var same = _adapter == adapter;
        var down = !_sampled ? 0 : same && received >= _received ? received - _received : Math.Max(0, received);
        var up = !_sampled ? 0 : same && sent >= _sent ? sent - _sent : Math.Max(0, sent);
        var seconds = (time - _time).TotalSeconds;
        _adapter = adapter; _received = received; _sent = sent; _time = time; _sampled = true;
        return Current = new(seconds > 0 ? down / seconds : 0, seconds > 0 ? up / seconds : 0,
            Current.DownloadedBytes + down, Current.UploadedBytes + up, true);
    }
    public static string FormatBytes(double value)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

public sealed class TrafficMonitor
{
    private readonly TrafficAccumulator _accumulator = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public TrafficSnapshot Current => _accumulator.Current;
    public void Begin()
    {
        var (id, down, up) = Read();
        _accumulator.Begin(id, down, up, _clock.Elapsed);
    }
    public TrafficSnapshot Poll()
    {
        var (id, down, up) = Read();
        return _accumulator.Sample(id, down, up, _clock.Elapsed);
    }
    private static (string? Id, long Down, long Up) Read()
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a => a.Name == NetworkManager.InterfaceName);
            if (adapter == null) return (null, 0, 0);
            var counters = adapter.GetIPStatistics();
            return (adapter.Id, counters.BytesReceived, counters.BytesSent);
        }
        catch (NetworkInformationException) { return (null, 0, 0); }
    }
}
