using System.Security.Cryptography;
using System.Text;

namespace WinSight.App.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle showEvent;
    private readonly EventWaitHandle toggleEvent;
    private readonly ManualResetEvent stopEvent = new(false);
    private readonly Thread listenerThread;
    private bool disposed;

    public event Action? ShowRequested;
    public event Action? ToggleRequested;

    private SingleInstanceService(Mutex mutex, EventWaitHandle showEvent, EventWaitHandle toggleEvent)
    {
        this.mutex = mutex;
        this.showEvent = showEvent;
        this.toggleEvent = toggleEvent;

        listenerThread = new Thread(ListenForSignals)
        {
            IsBackground = true,
            Name = "WinSight_SingleInstanceListener"
        };
        listenerThread.Start();
    }

    public static string ComputeDataDirHash(string dataDir)
    {
        var normalized = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..12];
    }

    public static bool TryAcquire(string dataDir, bool isToggle, out SingleInstanceService? service)
    {
        var hash = ComputeDataDirHash(dataDir);
        var mutexName = $"WinSight_NativePreview_{hash}";
        var showEventName = $"WinSight_NativePreview_{hash}_Show";
        var toggleEventName = $"WinSight_NativePreview_{hash}_Toggle";

        // Do not swallow exceptions during mutex creation; true failures must not disguise as a secondary instance.
        var mutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            // Signal the running instance
            try
            {
                if (isToggle)
                {
                    if (EventWaitHandle.TryOpenExisting(toggleEventName, out var targetToggleEvent))
                    {
                        using (targetToggleEvent)
                        {
                            targetToggleEvent.Set();
                        }
                    }
                }
                else
                {
                    if (EventWaitHandle.TryOpenExisting(showEventName, out var targetShowEvent))
                    {
                        using (targetShowEvent)
                        {
                            targetShowEvent.Set();
                        }
                    }
                }
            }
            catch
            {
                // Signal error on secondary instance does not crash primary
            }

            service = null;
            return false;
        }

        var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, showEventName);
        var toggleEvt = new EventWaitHandle(false, EventResetMode.AutoReset, toggleEventName);

        service = new SingleInstanceService(mutex, showEvt, toggleEvt);
        return true;
    }

    private void ListenForSignals()
    {
        var handles = new WaitHandle[] { stopEvent, showEvent, toggleEvent };
        while (true)
        {
            int index = WaitHandle.WaitAny(handles);
            if (index == 0)
            {
                // stopEvent signaled
                break;
            }
            if (index == 1)
            {
                ShowRequested?.Invoke();
            }
            else if (index == 2)
            {
                ToggleRequested?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // Wake up listener thread cleanly before disposing handles
        stopEvent.Set();
        if (listenerThread.IsAlive)
        {
            listenerThread.Join(2000);
        }

        stopEvent.Dispose();
        showEvent.Dispose();
        toggleEvent.Dispose();

        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
    }
}
