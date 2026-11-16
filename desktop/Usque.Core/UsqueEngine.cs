using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Usque.Core;

public sealed class UsqueEngine(AppPaths paths, Action<string> log) : IVpnEngine
{
    public void ValidateInstallation() => ValidateInstallation(ConnectionMode.Tunnel);

    public void ValidateInstallation(ConnectionMode mode)
    {
        if (!File.Exists(paths.Engine)) throw new FileNotFoundException("VPN engine missing. Run desktop/build.ps1 and launch the app from dist/desktop.");
        PortableExecutable.Validate(paths.Engine, RuntimeInformation.ProcessArchitecture);
        if (mode != ConnectionMode.Tunnel) return;
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(paths.Engine)!, "wintun.dll")))
            throw new FileNotFoundException("wintun.dll is missing beside engine/usque.exe. Run desktop/build.ps1 -DownloadWintun.");
        if (!File.Exists(paths.NetworkScript))
            throw new FileNotFoundException("Scripts/Network.ps1 is missing. Keep the complete desktop distribution together.");
        PortableExecutable.Validate(Path.Combine(Path.GetDirectoryName(paths.Engine)!, "wintun.dll"), RuntimeInformation.ProcessArchitecture);
    }

    public async Task RegisterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Profile)!);
        // Never invoke upstream's interactive overwrite prompt, and never leave
        // a partially written profile at the final path after cancellation.
        var temporary = paths.Profile + ".register-" + Guid.NewGuid().ToString("N");
        try
        {
            await ProcessRunner.RunAsync(ProcessRunner.StartInfo(paths.Engine, "--config", temporary,
                "register", "--accept-tos", "--name", Environment.MachineName, "--model", "PC"),
                TimeSpan.FromSeconds(90), cancellationToken, log);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(temporary, cancellationToken));
            _ = ProfileStore.Validate(json.RootElement, false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, paths.Profile, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async IAsyncEnumerable<TunnelEvent> RunAsync(ConnectOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = CreateStartInfo(options);
        using var job = new ProcessJob();
        using var process = Process.Start(info) ?? throw new IOException("Unable to launch the VPN engine.");
        Task? stderr = null;
        try
        {
            job.Assign(process);
            process.StandardInput.Close();
            stderr = RelayAsync(process.StandardError);
            // Bound initial startup; a silent backend must not spin forever.
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(60));
            bool ready = false;
            while (true)
            {
                var line = await ReadStatusLineAsync(process.StandardOutput, ready ? cancellationToken : startup.Token, cancellationToken);
                if (line == null) break;
                var update = ParseStatus(line);
                if (update.State == "ready") ready = true;
                yield return update;
            }
            await process.WaitForExitAsync(cancellationToken);
            throw new IOException($"VPN engine exited (code {process.ExitCode}). See diagnostics for details.");
        }
        finally
        {
            await ProcessRunner.StopAsync(process, job);
            if (stderr != null) await stderr;
        }
    }

    internal TunnelEvent ParseStatus(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var state = root.GetProperty("state").GetString() ?? throw new InvalidDataException("Invalid engine event.");
            var name = root.TryGetProperty("interface", out var adapter) ? adapter.GetString() : null;
            return new(state, name);
        }
        catch (JsonException ex)
        {
            // Upstream command failures (e.g. occupied proxy ports) are text on
            // stdout. Preserve the actionable error, not a JSON parser message.
            var message = line.Length <= 2048 ? line : line[..2048];
            log(message);
            throw new IOException(message, ex);
        }
    }

    internal ProcessStartInfo CreateStartInfo(ConnectOptions options)
    {
        options.Validate();
        var command = options.Mode switch
        {
            ConnectionMode.Socks5 => "socks", ConnectionMode.HttpProxy => "http-proxy", _ => "nativetun"
        };
        var info = ProcessRunner.StartInfo(paths.Engine, "--config", paths.Profile, command,
            "--always-reconnect", "--status-json", "--keepalive-period", "15s", "--reconnect-delay", "2s");
        if (options.IsProxy)
        {
            foreach (var argument in new[] { "--bind", "127.0.0.1", "--port", options.ProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                info.ArgumentList.Add(argument);
        }
        else
        {
            info.ArgumentList.Add("--interface-name"); info.ArgumentList.Add(NetworkManager.InterfaceName);
        }
        if (options.Http2) info.ArgumentList.Add("--http2");
        return info;
    }

    private async Task RelayAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line) log(line);
    }

    private static async Task<string?> ReadStatusLineAsync(StreamReader reader, CancellationToken readToken, CancellationToken sessionToken)
    {
        try { return await reader.ReadLineAsync(readToken); }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            throw new TimeoutException("The VPN engine did not create its adapter within 60 seconds.");
        }
    }
}
