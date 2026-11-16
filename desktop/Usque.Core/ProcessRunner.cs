using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Usque.Core;

internal static class ProcessRunner
{
    public static ProcessStartInfo StartInfo(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public static async Task<string> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken token, Action<string>? log = null)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var job = new ProcessJob();
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {info.FileName}.");
        try
        {
            job.Assign(process);
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            var error = await stderr;
            if (!string.IsNullOrWhiteSpace(error)) log?.Invoke(error.Trim());
            if (process.ExitCode != 0)
                throw new IOException($"{Path.GetFileName(info.FileName)} failed ({process.ExitCode}): {error.Trim()}");
            return output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"{Path.GetFileName(info.FileName)} timed out.");
        }
        finally
        {
            await StopAsync(process, job);
        }
    }

    internal static async Task StopAsync(Process process, ProcessJob job)
    {
        // Close the job first: descendants can inherit redirected pipe handles,
        // keeping output readers alive even after the main process exits.
        job.Dispose();
        try
        {
            // Assignment may have failed, so also stop an unassigned child.
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
        await process.WaitForExitAsync();
    }
}

// Closing this handle (including abrupt UI termination) kills the VPN process.
// The persistent WFP filters are deliberately NOT tied to this job's lifetime.
internal sealed class ProcessJob : IDisposable
{
    private IntPtr _handle;
    public ProcessJob()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new System.ComponentModel.Win32Exception(error);
        }
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new System.ComponentModel.Win32Exception();
    }
    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
