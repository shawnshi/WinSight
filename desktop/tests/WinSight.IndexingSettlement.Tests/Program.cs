using System.Reflection;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Runtime;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0]))
    throw new ArgumentException("Supply one NEW absolute task evidence directory.");
Directory.CreateDirectory(args[0]);
var rows = new List<object>(); int failed = 0;
var report = typeof(IndexingController).GetMethod("ReportUnsettled", BindingFlags.Instance | BindingFlags.NonPublic);
// Optional reflection intentionally allows running this same negative oracle against the frozen pre-fix source.
void Poison(IndexingController c, Exception cause) => report?.Invoke(c, [cause]);
TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
async Task<Exception> Throws(Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(4)); }
    catch (Exception e) when (e is not TimeoutException) { return e; }
    throw new InvalidDataException("Expected a bounded fault, not successful completion.");
}
async Task Case(string name, Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(7)); rows.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; rows.Add(new { name, status = "failed", error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Fenced(IndexingController c)
{
    Require(c.State == IndexingControlState.Pausing, "Unsettled resource fault must not publish Paused/Running.");
    Require(c.HasWork && c.Failure is not null, "Physical uncertainty must remain visible to reconfiguration/admission.");
    Require(c.Failure!.Contains("Restart", StringComparison.Ordinal) && !c.Failure.Contains("synthetic-artifact"), "Safe sticky failure must require restart without copying raw paths.");
}

await Case("normal/pause-resume-and-shutdown", async () =>
{
    var c = new IndexingController(TimeSpan.FromSeconds(2));
    await c.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
    Require(await c.SetPausedAsync(true, CancellationToken.None) && c.State == IndexingControlState.Paused, "Normal pause");
    Require(await c.SetPausedAsync(false, CancellationToken.None) && c.State == IndexingControlState.Running, "Normal resume");
    await c.StopAsync(); Require(c.State == IndexingControlState.Paused && !c.HasWork, "Normal stop");
});
await Case("ordinary-business-error-is-not-retention", async () =>
{
    var c = new IndexingController(TimeSpan.FromSeconds(2)); var cause = new IOException("ordinary synthetic decode failure");
    var error = await Throws(() => c.RunAsync(_ => Task.FromException(cause), CancellationToken.None));
    Require(ReferenceEquals(error, cause) && !c.HasWork && c.Failure is null, "Ordinary fault must preserve old semantics");
    await c.SetPausedAsync(true, CancellationToken.None); await c.SetPausedAsync(false, CancellationToken.None);
    await c.RunAsync(_ => Task.CompletedTask, CancellationToken.None); await c.StopAsync();
});
await Case("fault/parallel-pause-observes-close-failure", async () =>
{
    var c = new IndexingController(TimeSpan.FromSeconds(2)); var entered = Signal();
    var cause = new IOException("synthetic-artifact/task/retained shutdown");
    var work = c.RunAsync(async token =>
    {
        entered.SetResult();
        try { await Task.Delay(Timeout.Infinite, token); }
        catch (OperationCanceledException) { Poison(c, cause); throw cause; }
    }, CancellationToken.None);
    await entered.Task;
    var pause = c.SetPausedAsync(true, CancellationToken.None);
    Require(ReferenceEquals(await Throws(() => work), cause), "Original callback close cause must propagate");
    var fault = await Throws(async () => { await pause; });
    Require(fault is IOException && ReferenceEquals(fault.InnerException, cause), "Pause must observe resource close cause, not just idle");
    Fenced(c);
    await Throws(async () => { await c.SetPausedAsync(false, CancellationToken.None); }); Fenced(c);
    await Throws(() => c.StopAsync()); Fenced(c);
});
await Case("fault/already-queued-work-cannot-enter", async () =>
{
    var c = new IndexingController(TimeSpan.FromSeconds(2)); var entered = Signal(); var release = Signal(); int calls = 0;
    var cause = new IOException("synthetic-artifact/queued retention");
    var first = c.RunAsync(async _ => { entered.SetResult(); await release.Task; Poison(c, cause); throw cause; }, CancellationToken.None);
    await entered.Task;
    var queued = c.RunAsync(_ => { calls++; return Task.CompletedTask; }, CancellationToken.None);
    release.SetResult(); await Throws(() => first);
    await Throws(() => queued); Require(calls == 0, "Queued callback ran after physical settlement fault"); Fenced(c);
    await Throws(() => c.RunAsync(_ => { calls++; return Task.CompletedTask; }, CancellationToken.None));
    Require(calls == 0, "Fresh callback admitted after poison"); await Throws(() => c.StopAsync());
});
await Case("fault/first-cause-sticky-and-no-transition-clears-it", async () =>
{
    var c = new IndexingController(TimeSpan.FromSeconds(2)); var first = new IOException("synthetic-artifact/first");
    Poison(c, first); Poison(c, new IOException("synthetic-artifact/second"));
    foreach (var paused in new[] { true, false, true })
    {
        var error = await Throws(async () => { await c.SetPausedAsync(paused, CancellationToken.None); });
        Require(error is IOException && ReferenceEquals(error.InnerException, first), "Original poison cause overwritten"); Fenced(c);
    }
    await Throws(() => c.StopAsync()); Fenced(c);
});
await Case("fault/stop-still-cancels-generation-before-failing", async () =>
{
    var c = new IndexingController(TimeSpan.FromSeconds(2)); var entered = Signal(); var cancelObserved = Signal(); var release = Signal();
    var cause = new IOException("synthetic-artifact/close");
    var work = c.RunAsync(async token =>
    {
        using var registration = token.Register(() => cancelObserved.TrySetResult());
        Poison(c, cause); entered.SetResult(); await release.Task; throw cause;
    }, CancellationToken.None);
    await entered.Task; var stop = c.StopAsync();
    try { await cancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    finally { release.TrySetResult(); }
    await Throws(() => work); var error = await Throws(() => stop);
    Require(error is IOException && ReferenceEquals(error.InnerException, cause), "Stop must expose resource fault after cancellation/drain"); Fenced(c);
});
await Case("fault/report-is-required-and-rejects-null", async () =>
{
    Require(report is not null, "Missing ReportUnsettled: frozen old controller is a deliberate negative oracle.");
    var c = new IndexingController(TimeSpan.FromSeconds(2));
    try { report!.Invoke(c, [null]); throw new InvalidDataException("Null cause accepted"); }
    catch (TargetInvocationException e) when (e.InnerException is ArgumentNullException) { }
    Require(!c.HasWork && c.Failure is null && c.State == IndexingControlState.Running, "Invalid report changed state");
    await c.StopAsync();
});
await Case("normal/external-cancel-remains-recoverable", async () =>
{
    var c = new IndexingController(TimeSpan.FromSeconds(2)); using var stop = new CancellationTokenSource(); var entered = Signal();
    var work = c.RunAsync(async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }, stop.Token);
    await entered.Task; stop.Cancel(); Require(await Throws(() => work) is OperationCanceledException, "Cancellation changed type");
    Require(!c.HasWork && c.Failure is null, "Ordinary cancellation poisoned controller");
    await c.SetPausedAsync(true, CancellationToken.None); await c.SetPausedAsync(false, CancellationToken.None); await c.StopAsync();
});
await File.WriteAllTextAsync(Path.Combine(args[0], "results.json"), JsonSerializer.Serialize(new { failed, results = rows }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT passed={rows.Count - failed} failed={failed}; source-linked Controller, deterministic injected settlement faults, not OS refusal");
return failed == 0 ? 0 : 1;
