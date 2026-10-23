using System.Diagnostics;
using System.Text.Json;
using WinSight.Infrastructure.Media;
using WinSight.MediaProcess.Tests;

if (args.Length == 4 && args[0] == "--driver")
{
    OwnedProcesses.Record(args[3], "driver");
    await using var driverClient = new NativeMediaProcessClient(new NativeMediaProcessOptions
    {
        HostExecutable = args[1], WorkspaceRoot = args[2], RequestTimeout = TimeSpan.FromSeconds(60)
    });
    await using var never = await driverClient.ExtractAsync(new(args[3], "audio", 0, 1));
    throw new InvalidOperationException("Hanging fake unexpectedly succeeded.");
}
var run = new TestRun(args);
NativeMediaProcessClient Client(string directory, double seconds = 3, string? host = null) => new(new NativeMediaProcessOptions
{
    HostExecutable = host ?? run.FakeHost,
    WorkspaceRoot = Path.Combine(directory, "workspace"),
    RequestTimeout = TimeSpan.FromSeconds(seconds), TerminationTimeout = TimeSpan.FromSeconds(4), MaxOutputBytes = 65536
});
string Source(string directory, string mode = "ok")
{
    var input = Path.Combine(directory, "input"); Directory.CreateDirectory(input);
    Directory.CreateDirectory(Path.Combine(directory, "workspace"));
    return TestRun.Source(input, mode);
}
NativeMediaProcessRequest Request(string source) => new(source, "audio", 0, 1);
async Task Bounded(Func<Task> action) => await action().WaitAsync(TimeSpan.FromSeconds(12));

await run.Case("pre-cancel-no-launch", async directory =>
{
    var source = Source(directory); await using var client = Client(directory);
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Check.Throws<OperationCanceledException>(async () => { await using var lease = await client.ExtractAsync(Request(source), cancel.Token); });
    Check.That(OwnedProcesses.Read(source).Length == 0 && !Directory.EnumerateFileSystemEntries(Path.Combine(directory, "workspace")).Any(), "Pre-cancel must create no child or task output.");
});
await run.Case("missing-local-source-no-launch", async directory =>
{
    var source = Source(directory); File.Delete(source); await using var client = Client(directory);
    await Check.Throws<IOException>(async () => { await using var lease = await client.ExtractAsync(Request(source)); });
    Check.That(OwnedProcesses.Read(source).Length == 0 && !Directory.EnumerateFileSystemEntries(Path.Combine(directory, "workspace")).Any(), "Missing source must create no child/output.");
});
await run.Case("remote-drive-negative-classification-no-filesystem-io", async directory =>
{
    foreach (var type in new[] { DriveType.Network, DriveType.Unknown, DriveType.NoRootDirectory })
    foreach (var leaf in new[] { "source.wav", "host.exe", "workspace" })
    {
        var roots = new List<string>();
        IOException? observed = null;
        try { _ = MediaTaskFiles.LocalPath(@"Z:\unaccessed-" + leaf, root => { roots.Add(root); return type; }); }
        catch (IOException error) when (error.GetType() == typeof(IOException) &&
            error.Message == $"Media paths require a local drive; classified drive type: {type}.") { observed = error; }
        Check.That(observed is not null && roots.SequenceEqual(new[] { @"Z:\" }), "Actual shared path guard must reject classified remote/unknown roots before filesystem access.");
    }
    await Task.CompletedTask; // Primitive used by constructor and immediate pre-I/O Create reclassification; not a real SMB mapping test.
});
await run.Case("invalid-audio-range-no-launch", async directory =>
{
    var source = Source(directory); await using var client = Client(directory);
    Exception? observed = null;
    try { await using var unexpected = await client.ExtractAsync(new(source, "audio", 1, 0)); }
    catch (Exception error) when (error is ArgumentException or InvalidDataException) { observed = error; }
    Check.That(observed is not null && OwnedProcesses.Read(source).Length == 0, "Invalid interval rejected before worker launch.");
});
foreach (var mode in new[] { "malformed", "multiline", "oversize", "stdoutflood", "stderrflood", "utf8", "stderrutf8", "escape", "absolute", "source", "kind", "modality", "start", "end", "count", "empty", "version", "size", "duration", "badformat", "bigfile" })
    await run.Case("protocol-" + mode, async directory =>
    {
        var source = Source(directory, mode); var original = TestRun.Hash(source);
        await using var client = Client(directory, 6);
        var clock = Stopwatch.StartNew();
        await Check.Throws<InvalidDataException>(async () => { await using var unexpected = await client.ExtractAsync(Request(source)); });
        Check.That(clock.Elapsed < TimeSpan.FromSeconds(11), "Protocol failure bounded including cleanup.");
        await OwnedProcesses.AssertReaped(source, "main");
        Check.That(TestRun.Hash(source) == original, "Rejected protocol cannot delete or change source.");
    });
await run.Case("png-frame-valid-positive-control", async directory =>
{
    var source = Source(directory); var hash = TestRun.Hash(source);
    await using var client = Client(directory, 6);
    var lease = await client.ExtractAsync(new(source, "frame", 0, MaxEdge: 64));
    await OwnedProcesses.AssertReaped(source, "main");
    var artifact = lease.ArtifactPath; var task = lease.TaskDirectory;
    await FixtureMedia.CheckPng(artifact, 64, true);
    Check.That(lease.Modality == "image" && File.Exists(artifact), "A valid fake frame receives a usable PNG lease.");
    await lease.DisposeAsync();
    Check.That(!Directory.Exists(task) && !File.Exists(artifact) && TestRun.Hash(source) == hash, "Valid PNG lease cleanup preserves source.");
});
foreach (var mode in FixtureMedia.PngFaultModes)
    await run.Case("png-frame-reject-" + mode, async directory =>
    {
        var source = Source(directory, mode); var hash = TestRun.Hash(source);
        var workspace = Path.Combine(directory, "workspace");
        var sibling = Path.Combine(workspace, "sibling-task"); Directory.CreateDirectory(sibling);
        var sentinel = Path.Combine(sibling, "foreign.keep"); File.WriteAllText(sentinel, "unrelated-owner");
        await using var client = Client(directory, 6);
        InvalidDataException error;
        try
        {
            error = await Check.Throws<InvalidDataException>(async () =>
            {
                await using var unexpected = await client.ExtractAsync(new(source, "frame", 0, MaxEdge: 64));
            });
        }
        finally
        {
            await OwnedProcesses.AssertReaped(source, "main");
            File.WriteAllText(Path.Combine(directory, "reaped-before-harness.json"), JsonSerializer.Serialize(
                OwnedProcesses.Read(source).Select(p => new { identity = p, alive = OwnedProcesses.Alive(p) }).ToArray()));
        }
        Check.That((error.Message.Contains("PNG", StringComparison.OrdinalIgnoreCase) ||
                error.StackTrace?.Contains("NativeMediaPng.ValidateScanlines", StringComparison.Ordinal) == true) &&
            !error.Message.Contains("schema", StringComparison.OrdinalIgnoreCase), "Must reach actual PNG/chunk or zlib scanline validation, not an earlier fake-response/schema failure.");
        Check.That(Directory.GetDirectories(workspace).SequenceEqual(new[] { sibling }) &&
            Directory.GetFiles(workspace, "request.json", SearchOption.AllDirectories).Length == 0 &&
            File.ReadAllText(sentinel) == "unrelated-owner" && TestRun.Hash(source) == hash, "Malformed frame cleanup removes only its owned task; foreign task/sentinel and source survive.");
    });
await run.Case("reparse-artifact-rejected-and-unsettled-cleanup-explicit", async directory =>
{
    var source = Source(directory, "reparse"); var original = TestRun.Hash(source);
    var client = Client(directory, 6);
    var error = await Check.Throws<IOException>(async () => { await using var unexpected = await client.ExtractAsync(Request(source)); });
    Check.That(error.ToString().Contains("Reparse points, cloud placeholders and non-regular files are not read.", StringComparison.Ordinal)
        && error.ToString().Contains("cleanup did not settle", StringComparison.Ordinal), "Unsafe output rejection and retained cleanup must both remain visible.");
    await OwnedProcesses.AssertReaped(source, "main");
    Check.That(TestRun.Hash(source) == original &&
        File.ReadAllText(Path.Combine(source + ".reparse-target", "target.keep")) == "outside-artifact-target", "Rejected artifact cannot traverse/delete target or source.");
    var shutdown = await Check.Throws<IOException>(async () => await client.DisposeAsync());
    Check.That(shutdown.Message.Contains("retained unsettled resources", StringComparison.Ordinal), "Shutdown cannot hide retained cleanup failure.");
});
await run.Case("nonzero-native-failure-visible", async directory =>
{
    var source = Source(directory, "nonzero"); await using var client = Client(directory);
    var error = await Check.Throws<IOException>(async () => { await using var unexpected = await client.ExtractAsync(Request(source)); });
    Check.That(error.ToString().Contains("27", StringComparison.Ordinal) || error.ToString().Contains("Synthetic native failure", StringComparison.Ordinal), "Native cause/exit evidence must remain visible.");
    await OwnedProcesses.AssertReaped(source, "main");
});
foreach (var mode in new[] { "hang", "uncooperative", "descendant" })
    foreach (var cancellation in new[] { false, true })
        await run.Case((cancellation ? "cancel-" : "deadline-") + mode, async directory =>
        {
            var source = Source(directory, mode); var hash = TestRun.Hash(source);
            await using var client = Client(directory, cancellation ? 10 : 3);
            using var cancel = new CancellationTokenSource();
            var extraction = client.ExtractAsync(Request(source), cancel.Token);
            await OwnedProcesses.WaitForRoles(source, mode == "descendant" ? ["main", "descendant"] : ["main"]);
            if (cancellation) cancel.Cancel();
            if (cancellation) await Check.Throws<OperationCanceledException>(async () => { await using var lease = await extraction; });
            else await Check.Throws<TimeoutException>(async () => { await using var lease = await extraction; });
            await OwnedProcesses.AssertReaped(source, mode == "descendant" ? ["main", "descendant"] : ["main"]);
            Check.That(TestRun.Hash(source) == hash, "Termination preserves input.");
        });
await run.Case("unconfirmed-default-off-cancel-control", async directory =>
{
    var source = Source(directory, "unconfirmed"); var hash = TestRun.Hash(source);
    await using var client = Client(directory, 20);
    using var cancel = new CancellationTokenSource();
    var extraction = client.ExtractAsync(Request(source), cancel.Token);
    await OwnedProcesses.WaitForRoles(source, "main", "descendant");
    var request = Directory.GetFiles(Path.Combine(directory, "workspace"), "request.json", SearchOption.AllDirectories).Single();
    var task = Path.GetDirectoryName(request)!;
    Check.That(File.ReadAllText(Path.Combine(task, "output", "audio.wav")) == "owned-worker-partial-output", "Real hung worker wrote output before cancellation.");
    cancel.Cancel();
    await Check.Throws<OperationCanceledException>(async () => { await using var lease = await extraction; });
    await OwnedProcesses.AssertReaped(source, "main", "descendant");
    Check.That(!Directory.Exists(task) && TestRun.Hash(source) == hash, "Default-off cancellation reaps and cleans partial owned output.");
});
await run.Case("unconfirmed-injected-caller-cancel-retains-and-poisons", async directory =>
{
    var source = Source(directory, "unconfirmed"); var hash = TestRun.Hash(source);
    var workspace = Path.Combine(directory, "workspace");
    var sentinel = Path.Combine(workspace, "sibling.keep"); File.WriteAllText(sentinel, "foreign-owner");
    var client = new NativeMediaProcessClient(new NativeMediaProcessOptions
    {
        HostExecutable = run.FakeHost, WorkspaceRoot = workspace,
        RequestTimeout = TimeSpan.FromSeconds(30), TerminationTimeout = TimeSpan.FromSeconds(4),
        ForceUnconfirmedTerminationForTesting = true
    });
    using var cancel = new CancellationTokenSource();
    var extraction = client.ExtractAsync(Request(source), cancel.Token);
    await OwnedProcesses.WaitForRoles(source, "main", "descendant");
    var identities = OwnedProcesses.Read(source);
    Check.That(identities.Count(p => p.Role == "main") == 1 && identities.Count(p => p.Role == "descendant") == 1 &&
        identities.All(OwnedProcesses.Alive) && !extraction.IsCompleted, "Actual main and descendant hang before caller cancellation.");
    var request = Directory.GetFiles(workspace, "request.json", SearchOption.AllDirectories).Single();
    var task = Path.GetDirectoryName(request)!; var output = Path.Combine(task, "output");
    var partial = Path.Combine(output, "audio.wav"); var requestHash = TestRun.Hash(request); var partialHash = TestRun.Hash(partial);
    using (var document = JsonDocument.Parse(File.ReadAllText(request)))
        Check.That(document.RootElement.GetProperty("inputPath").GetString() == source &&
            document.RootElement.GetProperty("outputDirectory").GetString() == output, "Retained paths belong to this request, not a sibling task.");
    cancel.Cancel();
    var failure = await Check.Throws<IOException>(async () => { await using var unexpected = await extraction; });
    Check.That(failure.GetType() == typeof(IOException) && failure.Message.Contains("termination unconfirmed", StringComparison.OrdinalIgnoreCase) &&
        failure.Message.Contains("retained", StringComparison.OrdinalIgnoreCase) && failure.Message.Contains(task, StringComparison.Ordinal) &&
        failure.ToString().Contains("injected", StringComparison.OrdinalIgnoreCase) &&
        failure.ToString().Contains("confirmation", StringComparison.OrdinalIgnoreCase), "Cancellation must expose explicit injected unconfirmed retention IOException, never ordinary OCE or filesystem-cleanup failure.");
    await OwnedProcesses.AssertReaped(source, "main", "descendant");
    File.WriteAllText(Path.Combine(directory, "reaped-before-harness.json"), JsonSerializer.Serialize(
        identities.Select(p => new { identity = p, alive = OwnedProcesses.Alive(p) }).ToArray()));
    void AssertRetained()
    {
        Check.That(Directory.Exists(task) && Directory.Exists(output) && File.Exists(request) && File.Exists(partial) &&
            TestRun.Hash(request) == requestHash && TestRun.Hash(partial) == partialHash && TestRun.Hash(source) == hash &&
            File.ReadAllText(sentinel) == "foreign-owner", "Task/request/partial output/source/sibling persist unchanged after unconfirmed termination.");
        PinAttacks.WriteDenied(source); PinAttacks.WriteDenied(request);
        PinAttacks.MoveDenied(Path.GetDirectoryName(source)!); PinAttacks.MoveDenied(task); PinAttacks.MoveDenied(workspace);
    }
    AssertRetained();
    var poisoned = await Check.Throws<IOException>(async () => { await using var unexpected = await client.ExtractAsync(Request(source)); });
    Check.That(poisoned.Message.Contains("retained unsettled resources", StringComparison.Ordinal) &&
        OwnedProcesses.Read(source).Length == identities.Length && Directory.GetDirectories(workspace).SequenceEqual(new[] { task }), "Poisoned client refuses further extraction without launching another worker/task.");
    var unaffected = Path.Combine(directory, "unaffected-client"); Directory.CreateDirectory(unaffected);
    var otherSource = Source(unaffected);
    await using (var other = Client(unaffected, 10))
    {
        await using var lease = await other.ExtractAsync(Request(otherSource));
        FixtureMedia.CheckWave(lease.ArtifactPath, 1);
        await OwnedProcesses.AssertReaped(otherSource, "main");
    }
    AssertRetained();
    for (var repeat = 0; repeat < 2; repeat++)
    {
        var shutdown = await Check.Throws<IOException>(async () => await client.DisposeAsync());
        Check.That(shutdown.Message.Contains("retained unsettled resources", StringComparison.Ordinal) &&
            shutdown.ToString().Contains("termination unconfirmed", StringComparison.OrdinalIgnoreCase), "Client Dispose must repeatedly report retained termination rather than claim success.");
        AssertRetained();
    }
});
await run.Case("immediate-parent-exit-descendant-pipes", async directory =>
{
    var source = Source(directory, "parentexit"); await using var client = Client(directory, 3);
    await Check.Throws<IOException>(async () => { await using var lease = await client.ExtractAsync(Request(source)); });
    await OwnedProcesses.AssertReaped(source, "main", "descendant");
});
await run.Case("pins-during-active-and-lease", async directory =>
{
    var source = Source(directory, "hang"); var hash = TestRun.Hash(source);
    await using var client = Client(directory, 15); using var cancel = new CancellationTokenSource();
    var extraction = client.ExtractAsync(Request(source), cancel.Token);
    await OwnedProcesses.WaitForRoles(source, "main");
    var workspace = Path.Combine(directory, "workspace");
    var taskRequest = Directory.GetFiles(workspace, "request.json", SearchOption.AllDirectories).Single();
    PinAttacks.WriteDenied(source); PinAttacks.WriteDenied(taskRequest);
    PinAttacks.MoveDenied(Path.GetDirectoryName(source)!); PinAttacks.MoveDenied(workspace);
    var target = Path.Combine(directory, "junction-target"); Directory.CreateDirectory(target);
    PinAttacks.JunctionDenied(Path.GetDirectoryName(source)!, target);
    cancel.Cancel(); await Check.Throws<OperationCanceledException>(async () => { await using var lease = await extraction; });
    await OwnedProcesses.AssertReaped(source, "main"); Check.That(TestRun.Hash(source) == hash, "Active pins preserve source bytes.");
    var okay = TestRun.Source(Path.GetDirectoryName(source)!, "ok");
    await using var lease = await client.ExtractAsync(Request(okay));
    PinAttacks.WriteDenied(okay); PinAttacks.WriteDenied(lease.ArtifactPath);
    PinAttacks.MoveDenied(Path.GetDirectoryName(okay)!); PinAttacks.MoveDenied(lease.TaskDirectory); PinAttacks.MoveDenied(workspace);
    PinAttacks.JunctionDenied(lease.TaskDirectory, target);
});
await run.Case("leases-sibling-sentinel-and-client-dispose", async directory =>
{
    var source = Source(directory); var original = TestRun.Hash(source);
    var client = Client(directory); var first = await client.ExtractAsync(Request(source));
    var second = await client.ExtractAsync(Request(source));
    var sentinel = Path.Combine(directory, "workspace", "sibling.keep"); File.WriteAllText(sentinel, "sentinel");
    Check.That(first.TaskDirectory != second.TaskDirectory, "Each request owns a distinct task.");
    var firstPath = first.ArtifactPath; var firstTask = first.TaskDirectory;
    var secondPath = second.ArtifactPath; var secondTask = second.TaskDirectory;
    FixtureMedia.CheckWave(firstPath, 1); FixtureMedia.CheckWave(secondPath, 1);
    await Bounded(async () => await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.DisposeAsync().AsTask())));
    Check.That(File.Exists(firstPath) && File.Exists(secondPath), "Client disposal cannot revoke returned leases.");
    PinAttacks.WriteDenied(source); PinAttacks.MoveDenied(secondTask);
    await Check.Throws<ObjectDisposedException>(async () => { await using var unexpected = await client.ExtractAsync(Request(source)); });
    await Bounded(async () => await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => first.DisposeAsync().AsTask())));
    Check.That(!File.Exists(firstPath) && !Directory.Exists(firstTask) && File.Exists(secondPath), "Disposing one lease cleans only its own task.");
    await Bounded(async () => await second.DisposeAsync());
    Check.That(!File.Exists(secondPath) && !Directory.Exists(secondTask) && File.ReadAllText(sentinel) == "sentinel" && TestRun.Hash(source) == original, "Sibling sentinel/source survive all cleanup.");
});
await run.Case("cleanup-interference-is-explicit", async directory =>
{
    var source = Source(directory); await using var client = Client(directory);
    var lease = await client.ExtractAsync(Request(source));
    using (var blocker = new FileStream(lease.ArtifactPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        await Check.Throws<IOException>(async () => await lease.DisposeAsync());
        Check.That(Directory.Exists(lease.TaskDirectory) && File.Exists(lease.ArtifactPath), "Unremovable output retained, never reported as cleaned.");
    }
});
await run.Case("unknown-output-is-not-recursively-deleted", async directory =>
{
    var source = Source(directory, "unknownfile"); await using var client = Client(directory);
    NativeMediaArtifactLease? lease = null;
    try
    {
        lease = await client.ExtractAsync(Request(source));
        var unknown = Directory.GetFiles(lease.TaskDirectory, "unknown.keep", SearchOption.AllDirectories).Single();
        await Check.Throws<IOException>(async () => await lease.DisposeAsync());
        Check.That(File.ReadAllText(unknown) == "must not recursively delete", "Unknown files retained.");
    }
    catch (IOException) when (lease is null)
    {
        var unknown = Directory.GetFiles(Path.Combine(directory, "workspace"), "unknown.keep", SearchOption.AllDirectories).Single();
        Check.That(File.Exists(unknown), "Rejecting unknown output cannot recursively erase it.");
    }
});
await run.Case("queued-cancel-does-not-cancel-active", async directory =>
{
    var source = Source(directory, "hang"); var queuedSource = TestRun.Source(Path.GetDirectoryName(source)!, "ok");
    await using var client = Client(directory, 15); using var activeCancel = new CancellationTokenSource(); using var queuedCancel = new CancellationTokenSource();
    var active = client.ExtractAsync(Request(source), activeCancel.Token); await OwnedProcesses.WaitForRoles(source, "main");
    var queued = client.ExtractAsync(Request(queuedSource), queuedCancel.Token); queuedCancel.Cancel();
    await Check.Throws<OperationCanceledException>(async () => { await using var lease = await queued; });
    Check.That(!active.IsCompleted && OwnedProcesses.Read(source).All(OwnedProcesses.Alive) && OwnedProcesses.Read(queuedSource).Length == 0, "Queue cancellation cannot launch or stop another request.");
    activeCancel.Cancel(); await Check.Throws<OperationCanceledException>(async () => { await using var lease = await active; });
    await OwnedProcesses.AssertReaped(source, "main");
});
await run.Case("queued-total-wall-clock-deadline", async directory =>
{
    var source = Source(directory, "hang"); var queuedSource = TestRun.Source(Path.GetDirectoryName(source)!, "uncooperative");
    await using var client = Client(directory, 3);
    var active = client.ExtractAsync(Request(source)); await OwnedProcesses.WaitForRoles(source, "main");
    var clock = Stopwatch.StartNew(); var queued = client.ExtractAsync(Request(queuedSource));
    await Check.Throws<TimeoutException>(async () => { await using var lease = await active; });
    await Check.Throws<TimeoutException>(async () => { await using var lease = await queued; });
    Check.That(clock.Elapsed < TimeSpan.FromSeconds(4.5), "Queue wait must consume request deadline, not restart it when decoding begins.");
    await OwnedProcesses.AssertReaped(source, "main");
    if (OwnedProcesses.Read(queuedSource).Length != 0) await OwnedProcesses.AssertReaped(queuedSource, "main");
});
await run.Case("queue-capacity-at-most-sixteen", async directory =>
{
    var source = Source(directory, "hang"); var client = Client(directory, 30);
    var active = client.ExtractAsync(Request(source)); await OwnedProcesses.WaitForRoles(source, "main");
    var queuedSources = Enumerable.Range(0, 20).Select(i => TestRun.Source(Path.GetDirectoryName(source)!, "ok" + i)).ToArray();
    var queued = queuedSources.Select(s =>
    {
        try { return client.ExtractAsync(Request(s)); }
        catch (IOException error) when (error.Message == "Native media admission limit (16) reached.")
        { return Task.FromException<NativeMediaArtifactLease>(error); }
    }).ToArray();
    await Task.Delay(400);
    Check.That(queued.Count(t => !t.IsCompleted) <= 16 && queued.All(t => !t.IsCompletedSuccessfully), "At most 16 queued; no second active decoder.");
    Check.That(queuedSources.All(s => OwnedProcesses.Read(s).Length == 0), "Queue cannot launch workers while active request hangs.");
    await Bounded(async () => await client.DisposeAsync());
    foreach (var task in queued.Prepend(active))
    {
        Exception? observed = null;
        try { await using var lease = await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { observed = error; }
        Check.That(observed is not null, "Rejected/disposed queue cannot return successful leases.");
    }
    await OwnedProcesses.AssertReaped(source, "main");
    Check.That(queuedSources.All(s => OwnedProcesses.Read(s).Length == 0), "Capacity rejection/dispose cannot launch queued processes.");
});
await run.Case("dispose-reaps-active-and-queued", async directory =>
{
    var source = Source(directory, "descendant"); var queuedSource = TestRun.Source(Path.GetDirectoryName(source)!, "ok");
    var client = Client(directory, 30);
    var active = client.ExtractAsync(Request(source)); await OwnedProcesses.WaitForRoles(source, "main", "descendant");
    var queued = client.ExtractAsync(Request(queuedSource));
    await Bounded(async () => await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.DisposeAsync().AsTask())));
    foreach (var task in new[] { active, queued })
    {
        Exception? observed = null;
        try { await using var unexpected = await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { observed = error; }
        Check.That(observed is not null, "Client dispose cannot return success to outstanding work.");
    }
    await OwnedProcesses.AssertReaped(source, "main", "descendant");
    Check.That(OwnedProcesses.Read(queuedSource).Length == 0, "Dispose cannot launch queued worker.");
});
await run.Case("driver-death-kill-on-job-close", async directory =>
{
    var source = Source(directory, "descendant"); var workspace = Path.Combine(directory, "workspace");
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    foreach (var argument in new[] { "--driver", run.FakeHost, workspace, source }) info.ArgumentList.Add(argument);
    using var driver = Process.Start(info) ?? throw new IOException("Owned driver creation failed.");
    var identity = new OwnedIdentity(driver.Id, driver.StartTime.ToUniversalTime().Ticks, "driver");
    try
    {
        await OwnedProcesses.WaitForRoles(source, "driver", "main", "descendant");
        OwnedProcesses.KillRecorded(identity);
        await OwnedProcesses.AssertReaped(source, "driver", "main", "descendant");
    }
    finally { OwnedProcesses.KillRecorded(identity); }
});
if (run.Host is null) run.NotRun("actual-N1-audio-frame-E2E", "Optional --inference-host omitted; fake success is not actual host acceptance.");
else
{
    foreach (var kind in new[] { "audio", "frame" })
        await run.Case("actual-N1-E2E-" + kind, async directory =>
        {
            var source = Source(directory);
            if (kind == "frame") { source = Path.Combine(Path.GetDirectoryName(source)!, "synthetic.mp4"); await FixtureMedia.Video(source); }
            var hash = TestRun.Hash(source); await using var client = Client(directory, 30, run.Host);
            var lease = await client.ExtractAsync(new(source, kind, 0, kind == "audio" ? 1 : null, 64));
            Check.That(lease.Modality == (kind == "audio" ? "audio" : "image") && lease.StartSeconds == 0 && lease.EndSeconds == (kind == "audio" ? 1 : null), "Actual lease metadata");
            Check.That(lease.SourceDurationSeconds is > 0 && File.Exists(lease.ArtifactPath) && Directory.Exists(lease.TaskDirectory), "Actual host artifact alive through lease.");
            if (kind == "audio") FixtureMedia.CheckWave(lease.ArtifactPath, 1); else await FixtureMedia.CheckPng(lease.ArtifactPath, 64);
            await client.DisposeAsync(); Check.That(File.Exists(lease.ArtifactPath), "Actual host lease outlives client.");
            var path = lease.ArtifactPath; var task = lease.TaskDirectory; await lease.DisposeAsync();
            Check.That(!File.Exists(path) && !Directory.Exists(task) && TestRun.Hash(source) == hash, "Actual host lease cleanup preserves source.");
        });
}
return run.ExitCode;
