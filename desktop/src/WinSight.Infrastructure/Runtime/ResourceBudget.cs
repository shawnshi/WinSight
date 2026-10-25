using WinSight.Core;

namespace WinSight.Infrastructure.Runtime;

public sealed class ResourceBudgetController
{
    private readonly HostSignals hostSignals;
    private readonly Action? onMemoryPressure;
    private readonly object sync = new();
    private CancellationTokenSource backfillInferenceCts = new();
    private CancellationTokenSource complexParseInferenceCts = new();

    public HostSignals HostSignals => hostSignals;

    public ResourceBudgetController(HostSignals hostSignals, Action? onMemoryPressure = null)
    {
        this.hostSignals = hostSignals ?? throw new ArgumentNullException(nameof(hostSignals));
        this.onMemoryPressure = onMemoryPressure;
    }

    public CancellationToken BackfillInferenceToken
    {
        get { lock (sync) return backfillInferenceCts.Token; }
    }

    public CancellationToken ComplexParseInferenceToken
    {
        get { lock (sync) return complexParseInferenceCts.Token; }
    }

    public void PreemptForInteractiveQuery()
    {
        lock (sync)
        {
            var oldBackfill = backfillInferenceCts;
            var oldComplex = complexParseInferenceCts;
            backfillInferenceCts = new();
            complexParseInferenceCts = new();
            oldBackfill.Cancel();
            oldComplex.Cancel();
            oldBackfill.Dispose();
            oldComplex.Dispose();
        }
    }

    public IReadOnlyList<IndexBudgetState> ComputeBudgets()
    {
        var power = hostSignals.GetPowerInfo();
        var idle = hostSignals.GetIdleTime();
        var memory = hostSignals.GetMemoryInfo();

        bool hasMemoryPressure = memory.AvailablePhysicalBytes < 1024UL * 1024 * 1024 || memory.CommitPercent > 85.0;
        if (hasMemoryPressure)
        {
            try
            {
                onMemoryPressure?.Invoke();
            }
            catch { }
        }

        bool isAc = power.IsAcPower;
        bool isLowBatteryOrSaver = power.IsBatterySaver || power.BatteryPercent < 20;
        bool isIdle = isAc && idle >= TimeSpan.FromMinutes(5);

        // 1. InteractiveQuery: highest priority, always active 1/1, not halved by memory pressure
        var interactiveState = new IndexBudgetState("InteractiveQuery", 1, 1, "active");

        // Base concurrency and state for NewFileIndex, Backfill, ComplexParse
        int newFileExtract = 1;
        int newFileInference = 1;
        string newFileState = "active";

        int backfillExtract;
        int backfillInference;
        string backfillState;

        int complexExtract;
        int complexInference;
        string complexState;

        if (!isAc)
        {
            if (isLowBatteryOrSaver)
            {
                backfillExtract = 0;
                backfillInference = 0;
                backfillState = "paused:power";

                complexExtract = 0;
                complexInference = 0;
                complexState = "paused:power";
            }
            else
            {
                backfillExtract = 0;
                backfillInference = 0;
                backfillState = "paused:battery";

                complexExtract = 0;
                complexInference = 0;
                complexState = "paused:battery";
            }
        }
        else
        {
            if (isLowBatteryOrSaver)
            {
                backfillExtract = 0;
                backfillInference = 0;
                backfillState = "paused:power";

                complexExtract = 0;
                complexInference = 0;
                complexState = "paused:power";
            }
            else if (isIdle)
            {
                backfillExtract = 2;
                backfillInference = 1;
                backfillState = "active";

                complexExtract = 1;
                complexInference = 0;
                complexState = "active";
            }
            else
            {
                backfillExtract = 0;
                backfillInference = 0;
                backfillState = "paused:idle";

                complexExtract = 0;
                complexInference = 0;
                complexState = "paused:idle";
            }
        }

        if (hasMemoryPressure)
        {
            newFileExtract /= 2;
            newFileInference /= 2;
            newFileState += ":memory";

            backfillExtract /= 2;
            backfillInference /= 2;
            backfillState += ":memory";

            complexExtract /= 2;
            complexInference /= 2;
            complexState += ":memory";
        }

        return new List<IndexBudgetState>
        {
            interactiveState,
            new IndexBudgetState("NewFileIndex", newFileExtract, newFileInference, newFileState),
            new IndexBudgetState("Backfill", backfillExtract, backfillInference, backfillState),
            new IndexBudgetState("ComplexParse", complexExtract, complexInference, complexState)
        };
    }
}
