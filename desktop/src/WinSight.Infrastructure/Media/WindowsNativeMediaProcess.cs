using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinSight.Infrastructure.Media;

/// <summary>Owns a suspended-born worker, its non-inheritable job and only its stdio pipes.</summary>
internal sealed class WindowsNativeMediaProcess : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly SafeFileHandle process;
    private readonly SafeFileHandle stdout;
    private readonly SafeFileHandle stderr;
    private readonly bool forceUnconfirmedTerminationForTesting;
    internal bool Settled { get; private set; }

    private WindowsNativeMediaProcess(SafeFileHandle job, SafeFileHandle process,
        SafeFileHandle stdout, SafeFileHandle stderr, bool forceUnconfirmedTerminationForTesting) =>
        (this.job, this.process, this.stdout, this.stderr, this.forceUnconfirmedTerminationForTesting) =
            (job, process, stdout, stderr, forceUnconfirmedTerminationForTesting);

    internal static WindowsNativeMediaProcess Start(string executable, string requestPath, string directory, TimeSpan terminationTimeout,
        bool forceUnconfirmedTerminationForTesting, NativeMediaOperation operation = NativeMediaOperation.Extract)
    {
        var verb = operation switch
        {
            NativeMediaOperation.Extract => "extract-media",
            NativeMediaOperation.Inspect => "inspect-media",
            NativeMediaOperation.InspectImage => "inspect-image",
            NativeMediaOperation.Soundtrack => "extract-soundtrack",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        SafeFileHandle? job = null, process = null, thread = null, outputRead = null, outputWrite = null,
            errorRead = null, errorWrite = null, inputRead = null, inputWrite = null;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero;
        var initialized = false;
        var assigned = false;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            Check(!job.IsInvalid, "Create media job");
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()), "Set kill-on-close");
            Pipe(out outputRead, out outputWrite);
            Pipe(out errorRead, out errorWrite);
            Pipe(out inputRead, out inputWrite);
            Check(SetHandleInformation(outputRead, 1, 0), "Protect stdout reader");
            Check(SetHandleInformation(errorRead, 1, 0), "Protect stderr reader");
            Check(SetHandleInformation(inputWrite, 1, 0), "Protect stdin writer");
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0) throw Error("Size inherited handle list");
            attributes = Marshal.AllocHGlobal(checked((nint)size));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size), "Initialize inherited handle list");
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, inputRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, outputWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, errorWrite.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, handles,
                (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero), "Set exact inherited handle list");
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = inputRead.DangerousGetHandle(), Output = outputWrite.DangerousGetHandle(),
                    Error = errorWrite.DangerousGetHandle()
                },
                Attributes = attributes
            };
            var command = new StringBuilder($"{Quote(executable)} {verb} {Quote(requestPath)}");
            Check(CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                0x00000004 | 0x00080000 | 0x08000000, IntPtr.Zero, directory, ref startup, out var info),
                "Create suspended media worker");
            process = new SafeFileHandle(info.Process, true);
            thread = new SafeFileHandle(info.Thread, true);
            Check(AssignProcessToJobObject(job, process), "Assign suspended media worker");
            assigned = true;
            Check(ResumeThread(thread) != uint.MaxValue, "Resume controlled media worker");
            var result = new WindowsNativeMediaProcess(job, process, outputRead, errorRead, forceUnconfirmedTerminationForTesting);
            job = process = outputRead = errorRead = null;
            return result;
        }
        catch (Exception startError)
        {
            // Even assignment failure must settle the still-suspended main process before releasing pins.
            if (process is not null && !process.IsInvalid)
            {
                try
                {
                    Check(assigned ? TerminateJobObject(job!, 1) : TerminateProcess(process, 1), "Stop failed media launch");
                    var clock = Stopwatch.StartNew();
                    while (WaitForSingleObject(process, 0) != 0 || (assigned && ActiveProcesses(job!) != 0))
                    {
                        if (clock.Elapsed >= terminationTimeout) throw new IOException("Failed media launch did not settle.");
                        Thread.Sleep(10);
                    }
                }
                catch (Exception settlementError)
                {
                    // Fail closed: keep the actual handles alive; never identify a process by reusable PID.
                    MediaRetention.Keep(job, process);
                    job = process = null;
                    throw new MediaTerminationException("Suspended worker could not be confirmed stopped.",
                        new AggregateException(startError, settlementError));
                }
            }
            throw;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            thread?.Dispose(); inputRead?.Dispose(); inputWrite?.Dispose();
            outputWrite?.Dispose(); errorWrite?.Dispose(); outputRead?.Dispose(); errorRead?.Dispose();
            process?.Dispose(); job?.Dispose();
        }
    }

    internal async Task<(string Output, string Error, uint ExitCode)> RunAsync(int maxBytes,
        TimeSpan terminationTimeout, CancellationToken cancellationToken)
    {
        using var stopReaders = new CancellationTokenSource();
        var output = ReadPipeAsync(stdout, maxBytes, stopReaders.Token);
        var error = ReadPipeAsync(stderr, maxBytes, stopReaders.Token);
        Exception? failure = null;
        uint exitCode = 0;
        try
        {
            while (!MainExited())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.IsFaulted) await output.ConfigureAwait(false);
                if (error.IsFaulted) await error.ConfigureAwait(false);
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            Check(GetExitCodeProcess(process, out exitCode), "Read media exit status");
        }
        catch (Exception ex) { failure = ex; }

        try
        {
            // Main exit alone does not establish that descendants have stopped writing or holding pipes.
            await TerminateAndConfirmAsync(terminationTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopReaders.Cancel();
            await ObserveReadersAsync(output, error).ConfigureAwait(false);
            throw new MediaTerminationException("Media job termination was not confirmed; task protections retained.",
                failure is null ? ex : new AggregateException(failure, ex));
        }
        try
        {
            await Task.WhenAll(output, error).WaitAsync(terminationTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) { failure ??= ex is TimeoutException ? new IOException("Media pipe drain deadline exceeded.", ex) : ex; }
        finally
        {
            stopReaders.Cancel();
            await ObserveReadersAsync(output, error).ConfigureAwait(false);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return (await output.ConfigureAwait(false), await error.ConfigureAwait(false), exitCode);
    }

    private static async Task ObserveReadersAsync(Task<string> output, Task<string> error)
    {
        // Polling readers have no blocking read/EOF dependency once cancellation is requested.
        try { await Task.WhenAll(output, error).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or DecoderFallbackException) { }
    }

    private bool MainExited()
    {
        var wait = WaitForSingleObject(process, 0);
        if (wait == 0) return true;
        if (wait == 258) return false;
        throw Error("Poll media process handle");
    }

    private async Task TerminateAndConfirmAsync(TimeSpan timeout)
    {
        Check(TerminateJobObject(job, 1), "Terminate media job");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (MainExited() && ActiveProcesses(job) == 0)
            {
                // NM-N2AR-02: inject only failed confirmation, never suppress real job termination/reaping.
                if (forceUnconfirmedTerminationForTesting)
                    throw new MediaTerminationException("Injected unconfirmed media termination for testing after OS-confirmed exit.",
                        new IOException("Instance-local confirmation failure injection; task protections must remain retained."));
                Settled = true;
                return;
            }
            if (clock.Elapsed >= timeout) throw new IOException("Media job still has live processes after termination deadline.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static uint ActiveProcesses(SafeFileHandle job)
    {
        Check(QueryInformationJobObject(job, 1, out var info, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero),
            "Query media job active processes");
        return info.ActiveProcesses;
    }

    private static async Task<string> ReadPipeAsync(SafeFileHandle pipe, int maxBytes, CancellationToken token)
    {
        var encoding = new UTF8Encoding(false, true);
        var decoder = encoding.GetDecoder();
        var bytes = new byte[Math.Min(4096, maxBytes)];
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        using var collected = new MemoryStream();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!PeekNamedPipe(pipe, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero))
            {
                if (Marshal.GetLastWin32Error() == 109) break;
                throw Error("Peek media output pipe");
            }
            if (available == 0) { await Task.Delay(10, token).ConfigureAwait(false); continue; }
            Check(ReadFile(pipe, bytes, (uint)Math.Min(bytes.Length, available), out var read, IntPtr.Zero), "Read media output pipe");
            if (read == 0) break;
            if (collected.Length + read > maxBytes) throw new InvalidDataException("Media output exceeds MaxOutputBytes.");
            decoder.GetChars(bytes, 0, (int)read, chars, 0, false);
            collected.Write(bytes, 0, (int)read);
        }
        decoder.GetChars([], 0, 0, chars, 0, true);
        return encoding.GetString(collected.GetBuffer(), 0, checked((int)collected.Length));
    }

    public void Dispose()
    {
        if (!Settled) throw new IOException("Cannot release an unsettled media job.");
        stdout.Dispose(); stderr.Dispose(); process.Dispose(); job.Dispose();
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static void Pipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        Check(CreatePipe(out read, out write, ref security, 0), "Create media stdio pipe");
    }
    private static IOException Error(string operation) => new(operation, new Win32Exception(Marshal.GetLastWin32Error()));
    private static void Check(bool success, string operation) { if (!success) throw Error(operation); }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Size; internal IntPtr Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        internal int Size; internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, Reserved2Size; internal IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        internal long ProcessTime, JobTime; internal uint LimitFlags;
        internal nuint MinWorkingSet, MaxWorkingSet; internal uint ActiveProcessLimit;
        internal nuint Affinity; internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        internal BasicLimits Basic; internal IoCounters Io;
        internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    {
        internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting info, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint count, out uint read, IntPtr overlapped);
}

internal enum NativeMediaOperation { Extract, Inspect, Soundtrack, InspectImage }

internal sealed class MediaTerminationException(string message, Exception inner) : IOException(message, inner);

internal static class MediaRetention
{
    private static readonly List<object> retained = [];
    internal static void Keep(params object?[] resources)
    {
        lock (retained) foreach (var resource in resources) if (resource is not null) retained.Add(resource);
    }
}
