using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinSight.Infrastructure.Media;

namespace WinSight.Infrastructure.Runtime;

internal sealed class PreviewServerClient : IAsyncDisposable
{
    private readonly string hostPath;
    private readonly string workspacePath;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> pending = new();
    private Process? process;
    private StreamWriter? stdin;
    private StreamReader? stdout;
    private Task? readLoop;
    private IntPtr jobHandle;
    private long nextId;
    private bool disposed;

    internal PreviewServerClient(string hostExecutable, string workspaceRoot)
    {
        hostPath = MediaTaskFiles.LocalPath(hostExecutable);
        workspacePath = MediaTaskFiles.LocalPath(workspaceRoot);
    }

    internal async Task<NativePdfPageLease> RenderPdfPageAsync(NativePdfPageRequest request, CancellationToken token)
    {
        request.Validate();
        request = request with { SourcePath = MediaTaskFiles.LocalPath(request.SourcePath) };
        var files = MediaTaskFiles.CreatePdfPage(hostPath, workspacePath, request);
        try
        {
            var payload = await InvokeAsync("render-pdf-page", files.RequestPath, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            return NativePdfPageLease.Parse(payload, request, files, token);
        }
        catch
        {
            TryCleanup(files);
            throw;
        }
    }

    internal async Task<NativeWordPageLease> RenderWordPageAsync(NativeWordPageRequest request, CancellationToken token)
    {
        request.Validate();
        request = request with { SourcePath = MediaTaskFiles.LocalPath(request.SourcePath) };
        var files = MediaTaskFiles.CreateWordPage(hostPath, workspacePath, request);
        try
        {
            var payload = await InvokeAsync("render-word-page", files.RequestPath, TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            return NativeWordPageLease.Parse(payload, request, files, token);
        }
        catch
        {
            TryCleanup(files);
            throw;
        }
    }

    internal async Task<NativeExcelPageLease> RenderExcelPageAsync(NativeExcelPageRequest request, CancellationToken token)
    {
        request.Validate();
        request = request with { SourcePath = MediaTaskFiles.LocalPath(request.SourcePath) };
        var files = MediaTaskFiles.CreateExcelPage(hostPath, workspacePath, request);
        try
        {
            var payload = await InvokeAsync("render-excel-page", files.RequestPath, TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            return NativeExcelPageLease.Parse(payload, request, files, token);
        }
        catch
        {
            TryCleanup(files);
            throw;
        }
    }

    internal async Task<NativePresentationLease> RenderPresentationPageAsync(NativePresentationPageRequest request, CancellationToken token)
    {
        request.Validate();
        request = request with { SourcePath = MediaTaskFiles.LocalPath(request.SourcePath) };
        var files = MediaTaskFiles.CreatePresentationPage(hostPath, workspacePath, request);
        try
        {
            var payload = await InvokeAsync("render-presentation-page", files.RequestPath, TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            return NativePresentationLease.ParsePage(payload, request, files, token);
        }
        catch
        {
            TryCleanup(files);
            throw;
        }
    }

    internal async Task<NativeRasterPreviewLease> RenderRasterPreviewAsync(NativeRasterPreviewRequest request, CancellationToken token)
    {
        request.Validate();
        request = request with { SourcePath = MediaTaskFiles.LocalPath(request.SourcePath) };
        var files = MediaTaskFiles.CreateRasterPreview(hostPath, workspacePath, request);
        try
        {
            var payload = await InvokeAsync("render-raster-preview", files.RequestPath, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            return NativeRasterPreviewLease.Parse(payload, request, files, token);
        }
        catch
        {
            TryCleanup(files);
            throw;
        }
    }

    private async Task<string> InvokeAsync(string op, string requestPath, TimeSpan timeout, CancellationToken token)
    {
        await EnsureStartedAsync(token).ConfigureAwait(false);
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        try
        {
            var line = JsonSerializer.Serialize(new { id, op, requestPath });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);

            await writeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                if (stdin is null || process is not { HasExited: false })
                    throw new IOException("Preview worker is not running.");
                await stdin.WriteLineAsync(line.AsMemory(), deadline.Token).ConfigureAwait(false);
                await stdin.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }

            // On caller cancellation we abandon waiting on this TCS without killing the warm Office worker.
            return await tcs.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private async Task EnsureStartedAsync(CancellationToken token)
    {
        if (process is { HasExited: false } && stdin is not null && stdout is not null) return;
        await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is { HasExited: false } && stdin is not null && stdout is not null) return;
            ResetProcess();

            var psi = new ProcessStartInfo(hostPath, "--preview-stdio")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(hostPath)!
            };

            var proc = Process.Start(psi) ?? throw new IOException("Failed to start WinSight preview host.");
            try
            {
                AssignKillOnCloseJob(proc);
                proc.ErrorDataReceived += (_, _) => { };
                proc.BeginErrorReadLine();
                var reader = proc.StandardOutput;
                var writer = proc.StandardInput;

                using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                startupCts.CancelAfter(TimeSpan.FromSeconds(15));
                var readyLine = await reader.ReadLineAsync(startupCts.Token).ConfigureAwait(false);
                if (readyLine is null) throw new IOException("Preview worker exited before ready handshake.");

                process = proc;
                stdout = reader;
                stdin = writer;
                readLoop = Task.Run(() => ReadStdoutLoopAsync(reader));
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task ReadStdoutLoopAsync(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idProp) || !idProp.TryGetInt64(out var id)) continue;
                    if (!pending.TryRemove(id, out var tcs)) continue;
                    if (root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean())
                    {
                        tcs.TrySetResult(root.GetProperty("payload").GetString()!);
                    }
                    else
                    {
                        var err = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Preview worker error";
                        tcs.TrySetException(new IOException(err ?? "Preview worker error"));
                    }
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            foreach (var kv in pending)
                if (pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new IOException("Preview worker exited unexpectedly."));
        }
    }

    private void AssignKillOnCloseJob(Process proc)
    {
        if (jobHandle == IntPtr.Zero)
        {
            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle != IntPtr.Zero)
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(jobHandle, 9, ptr, (uint)size);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
        }
        if (jobHandle != IntPtr.Zero)
            AssignProcessToJobObject(jobHandle, proc.Handle);
    }

    private void ResetProcess()
    {
        foreach (var kv in pending)
            if (pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new IOException("Preview worker reset."));
        try { stdin?.Dispose(); } catch { }
        try { stdout?.Dispose(); } catch { }
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { process.Dispose(); } catch { }
        }
        stdin = null;
        stdout = null;
        process = null;
    }

    private static void TryCleanup(MediaTaskFiles files)
    {
        try { files.Cleanup(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (process is { HasExited: false } && stdin is not null)
            {
                try
                {
                    await writeGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await stdin.WriteLineAsync("{\"id\":0,\"op\":\"quit\"}").ConfigureAwait(false);
                        await stdin.FlushAsync().ConfigureAwait(false);
                    }
                    finally { writeGate.Release(); }
                    using var cts = new CancellationTokenSource(2500);
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch { }
            }
            ResetProcess();
            if (readLoop is not null)
            {
                try { await readLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
                readLoop = null;
            }
            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
                jobHandle = IntPtr.Zero;
            }
        }
        finally
        {
            lifecycleGate.Release();
            lifecycleGate.Dispose();
            writeGate.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryLimit, PeakJobMemoryLimit;
    }
}
