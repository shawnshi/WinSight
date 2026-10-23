using System.Diagnostics;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Inference;

var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "synthetic-runs", Guid.NewGuid().ToString("N")));
var fake = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../FakeHost/bin/Debug/net10.0/FakeHost.exe"));
if (args is ["--child-owner", var childFake, var childDirectory])
{
    await using var owner = NewClient(childDirectory, childFake);
    await owner.GetInfoAsync();
    File.WriteAllText(Path.Combine(childDirectory, "owner-ready"), "ready");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
Directory.CreateDirectory(testRoot);
var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception error) { Console.WriteLine($"FAIL {name}: {error}"); failed++; }
}
string Scenario(string mode)
{
    var directory = Path.Combine(testRoot, mode + " path & spaces " + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}
static InferenceHostClient NewClient(string directory, string executable, int requestMs = 2000, int startupMs = 2000, bool automatic = false) =>
    new(executable, directory, Path.Combine(directory, "manifest with spaces &.json"), automatic,
        TimeSpan.FromMilliseconds(startupMs), TimeSpan.FromMilliseconds(requestMs));
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static EmbeddingInput[] Items(int count = 1) => Enumerable.Range(0, count).Select(i => new EmbeddingInput("text", "raw query no prompt " + i)).ToArray();
static JsonElement[] Audit(string directory)
{
    var path = Path.Combine(directory, "audit.jsonl");
    if (!File.Exists(path)) return [];
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    var text = reader.ReadToEnd();
    var complete = text.LastIndexOf('\n');
    if (complete < 0) return []; // A record currently being appended is pending, not a malformed complete record.
    return text[..complete].Split('\n').Select(line => JsonDocument.Parse(line.TrimEnd('\r')).RootElement.Clone()).ToArray();
}
static int[] Pids(string directory) => Audit(directory).Where(item => item.TryGetProperty("started", out _)).Select(item => item.GetProperty("started").GetInt32()).ToArray();
static bool IsAlive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
static async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(15, timeout.Token);
}

await Test("lazy, literal ArgumentList, exact raw text, cpu default, LE vectors and graceful quit", async () =>
{
    var directory = Scenario("success");
    var client = NewClient(directory, fake);
    Check(!File.Exists(Path.Combine(directory, "audit.jsonl")), "Constructor started host.");
    var info = await client.GetInfoAsync();
    Check(info.ProfileId == new string('a', 64) && info.Dimension == 768 && info.Capabilities.SequenceEqual(new[] { "text", "image", "audio" }), "Wrong info.");
    Check(info.RequestedProvider == "CPUExecutionProvider" && info.Validation == "unverified-source-integration", "Fabricated provider or validation.");
    var input = "原样 question \"quoted\"\nquery: no implicit policy";
    var results = await client.EmbedAsync([new("text", input)]);
    Check(results.Count == 1 && results[0].Error is null && results[0].Vector is { Length: 768 } vector && Math.Abs(vector[0] - 0.6f) < 1e-7 && vector[1] == 0.8f, "LE vector mismatch.");
    await client.DisposeAsync();
    var entries = Audit(directory);
    Check(entries.Single(item => item.TryGetProperty("started", out _)).GetProperty("device").GetString() == "cpu", "Default provider was not CPU.");
    Check(entries.Where(item => item.TryGetProperty("request", out _)).Select(item => item.GetProperty("request"))
        .Single(item => item.GetProperty("op").GetString() == "text").GetProperty("inputs")[0].GetString() == input, "Caller prompt changed.");
    Check(entries.Any(item => item.TryGetProperty("request", out var request) && request.GetProperty("op").GetString() == "quit"), "Graceful quit missing.");
    Check(Pids(directory).All(pid => !IsAlive(pid)), "Child survives dispose.");
});
await Test("mixed modalities/budgets, batches <=32, one process", async () =>
{
    var directory = Scenario("success");
    await using var client = NewClient(directory, fake);
    var inputs = Items(35).Concat(new[] { new EmbeddingInput("image", "synthetic image", 560), new EmbeddingInput("audio", "synthetic audio") }).ToArray();
    var results = await client.EmbedAsync(inputs);
    Check(results.Count == 37 && results.All(item => item.Vector is not null), "Mixed batch incomplete.");
    var batches = Audit(directory).Where(item => item.TryGetProperty("request", out _)).Select(item => item.GetProperty("request")).ToArray();
    Check(batches.Select(item => item.GetProperty("inputs").GetArrayLength()).SequenceEqual(new[] { 32, 3, 1, 1 }), "Batch bounds/order incorrect.");
    Check(batches[2].GetProperty("budget").GetInt32() == 560 && Pids(directory).Length == 1, "Budget or process reuse incorrect.");
});
await Test("byte-bounded batches including escaped Unicode", async () =>
{
    var directory = Scenario("success");
    await using var client = NewClient(directory, fake);
    var results = await client.EmbedAsync(Enumerable.Range(0, 32).Select(_ => new EmbeddingInput("text", new string('文', 16000))).ToArray());
    Check(results.Count == 32, "Byte batching lost results.");
    var lines = Audit(directory).Where(item => item.TryGetProperty("request", out _)).Select(item => item.GetProperty("request")).ToArray();
    Check(lines.Length > 1 && lines.All(line => System.Text.Encoding.UTF8.GetByteCount(line.GetRawText()) <= InferenceHostClient.MaxLineBytes), "Byte request bound violated.");
});
await Test("real per-item failure remains partial, not success vector or transport error", async () =>
{
    var directory = Scenario("partial");
    await using var client = NewClient(directory, fake);
    var results = await client.EmbedAsync(Items(3));
    Check(results[0].Vector is not null && results[1].Vector is null && results[1].Error == "IOException: synthetic unreadable item" && results[2].Vector is not null, "Partial errors not preserved.");
    await client.GetInfoAsync();
    Check(Pids(directory).Length == 1, "Partial failure reset healthy host.");
});
foreach (var mode in new[] { "id", "dimension", "count", "error-count", "base64", "base64-space", "byte-count", "norm", "zero", "nan", "infinity", "status", "invalid", "invalid-utf8", "duplicate", "nested-duplicate", "oversize" })
    await Test("reject/reset " + mode, async () =>
    {
        var directory = Scenario(mode);
        await using var client = NewClient(directory, fake);
        await Throws<InvalidDataException>(async () => await client.EmbedAsync(Items()));
        Check(Pids(directory).All(pid => !IsAlive(pid)), "Malformed reply child survived reset.");
    });
foreach (var mode in new[] { "truncated", "eof", "request-error" })
    await Test("transport/request failure " + mode, async () =>
    {
        var directory = Scenario(mode);
        await using var client = NewClient(directory, fake);
        await Throws<IOException>(async () => await client.EmbedAsync(Items()));
        Check(Pids(directory).All(pid => !IsAlive(pid)), "Failed exchange child survived.");
    });
foreach (var mode in new[] { "ready-false", "protocol", "profile", "model", "mode", "capabilities", "downloads", "profile-drift" })
    await Test("strict ready/info " + mode, async () =>
    {
        var directory = Scenario(mode);
        await using var client = NewClient(directory, fake);
        await Throws<InvalidDataException>(async () => await client.GetInfoAsync());
        Check(Pids(directory).All(pid => !IsAlive(pid)), "Bad identity child survived.");
    });
await Test("unsupported media capability is not sent", async () =>
{
    var directory = Scenario("text-only");
    await using var client = NewClient(directory, fake);
    await Throws<NotSupportedException>(async () => await client.EmbedAsync([new("image", "synthetic")]));
    Check(Audit(directory).All(item => !item.TryGetProperty("request", out _)), "Unsupported op sent to host.");
});
await Test("opt-in auto is reported as request, not actual GPU", async () =>
{
    var directory = Scenario("success");
    await using var client = NewClient(directory, fake, automatic: true);
    var info = await client.GetInfoAsync();
    Check(info.RequestedProvider == "device-candidates-with-real-model-gates", "Auto provider misreported.");
});
await Test("stderr flood is drained without deadlock", async () =>
{
    var directory = Scenario("stderr");
    await using var client = NewClient(directory, fake);
    await client.EmbedAsync(Items());
});
foreach (var mode in new[] { "startup-timeout", "request-timeout", "no-read" })
    await Test("timeout kills/restarts without late-reply poisoning " + mode, async () =>
    {
        var directory = Scenario(mode);
        await using var client = NewClient(directory, fake, requestMs: 300, startupMs: 2000);
        var inputs = mode == "no-read" ? Enumerable.Range(0, 32).Select(_ => new EmbeddingInput("text", new string('x', 20000))).ToArray() : Items();
        await Throws<TimeoutException>(async () => await client.EmbedAsync(inputs));
        Check(Pids(directory).Length == 1, "Fixture did not reach its first managed startup before the deadline.");
        Check(!IsAlive(Pids(directory)[0]), "Timed-out child survived.");
        var results = await client.EmbedAsync(Items());
        Check(results[0].Vector is not null && Pids(directory).Length == 2, "Fresh session not usable.");
    });
await Test("cancel active exchange resets child then next request succeeds", async () =>
{
    var directory = Scenario("cancel");
    await using var client = NewClient(directory, fake);
    using var cancel = new CancellationTokenSource();
    var running = client.EmbedAsync(Items(), cancel.Token);
    await Until(() => Audit(directory).Any(item => item.TryGetProperty("request", out _)));
    cancel.Cancel();
    await Throws<OperationCanceledException>(async () => await running);
    Check(!IsAlive(Pids(directory)[0]), "Cancelled child survived.");
    Check((await client.EmbedAsync(Items()))[0].Vector is not null && Pids(directory).Length == 2, "Cancellation poisoned next request.");
});
await Test("cancel startup resets child then next request succeeds", async () =>
{
    var directory = Scenario("startup-cancel");
    await using var client = NewClient(directory, fake);
    using var cancel = new CancellationTokenSource();
    var running = client.GetInfoAsync(cancel.Token);
    await Until(() => Pids(directory).Length == 1);
    cancel.Cancel();
    await Throws<OperationCanceledException>(async () => await running);
    Check(!IsAlive(Pids(directory)[0]), "Cancelled startup survived.");
    await client.GetInfoAsync();
    Check(Pids(directory).Length == 2, "Startup cancellation did not restart.");
});
await Test("profile remains pinned across child restarts", async () =>
{
    var directory = Scenario("restart-profile");
    await using var client = NewClient(directory, fake, requestMs: 300);
    await Throws<TimeoutException>(async () => await client.EmbedAsync(Items()));
    await Throws<InvalidDataException>(async () => await client.EmbedAsync(Items()));
    Check(Pids(directory).Length == 2 && Pids(directory).All(pid => !IsAlive(pid)), "New profile silently accepted.");
});
await Test("concurrent requests are serialized and matched", async () =>
{
    var directory = Scenario("delay");
    await using var client = NewClient(directory, fake);
    var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.EmbedAsync(Items())));
    Check(results.All(batch => batch.Single().Vector is not null) && Pids(directory).Length == 1, "Concurrent reply mismatch or extra process.");
});
await Test("cancel queued waiter leaves active exchange intact", async () =>
{
    var directory = Scenario("delay");
    await using var client = NewClient(directory, fake);
    var first = client.EmbedAsync(Items());
    await Until(() => Audit(directory).Any(item => item.TryGetProperty("request", out _)));
    using var cancel = new CancellationTokenSource(30);
    await Throws<OperationCanceledException>(async () => await client.GetInfoAsync(cancel.Token));
    Check((await first)[0].Vector is not null && Pids(directory).Length == 1, "Waiter cancellation reset another caller.");
});
await Test("admission bound, active disposal, idempotent disposal, no orphan", async () =>
{
    var directory = Scenario("hold");
    var client = NewClient(directory, fake);
    var active = client.EmbedAsync(Items());
    await Until(() => Audit(directory).Any(item => item.TryGetProperty("request", out _)));
    var waiters = Enumerable.Range(0, 31).Select(_ => client.GetInfoAsync()).ToArray();
    await Throws<InvalidOperationException>(async () => await client.GetInfoAsync());
    var dispose1 = client.DisposeAsync().AsTask();
    var dispose2 = client.DisposeAsync().AsTask();
    await Throws<OperationCanceledException>(async () => await active);
    foreach (var waiter in waiters) await Throws<OperationCanceledException>(async () => await waiter);
    await Task.WhenAll(dispose1, dispose2);
    Check(Pids(directory).All(pid => !IsAlive(pid)), "Child survived active disposal.");
    await Throws<ObjectDisposedException>(async () => await client.GetInfoAsync());
});
await Test("dispose kills descendants as well as host", async () =>
{
    var directory = Scenario("tree");
    var client = NewClient(directory, fake);
    await client.GetInfoAsync();
    var leafPid = Audit(directory).Single(item => item.TryGetProperty("leaf", out _)).GetProperty("leaf").GetInt32();
    Check(IsAlive(leafPid), "Tree fixture did not start.");
    await client.DisposeAsync();
    await Until(() => !IsAlive(leafPid));
    Check(Pids(directory).All(pid => !IsAlive(pid)), "Host survived tree cleanup.");
});
await Test("quit timeout uses bounded kill fallback", async () =>
{
    var directory = Scenario("ignore-quit");
    var client = NewClient(directory, fake);
    await client.GetInfoAsync();
    var timer = Stopwatch.StartNew();
    await client.DisposeAsync();
    Check(timer.Elapsed < TimeSpan.FromSeconds(4) && Pids(directory).All(pid => !IsAlive(pid)), "Quit fallback did not clean up promptly.");
});
await Test("input errors/empty/pre-cancel do not start subprocess", async () =>
{
    var directory = Scenario("success");
    await using var client = NewClient(directory, fake);
    Check((await client.EmbedAsync([])).Count == 0, "Empty input should be empty.");
    await Throws<ArgumentException>(async () => await client.EmbedAsync([new("video", "x")]));
    await Throws<ArgumentException>(async () => await client.EmbedAsync([new("text", "x", 0)]));
    await Throws<System.Text.EncoderFallbackException>(async () => await client.EmbedAsync([new("text", "\ud800")]));
    await Throws<ArgumentOutOfRangeException>(async () => await client.EmbedAsync(Items(4097)));
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Throws<OperationCanceledException>(async () => await client.GetInfoAsync(cancel.Token));
    Check(Pids(directory).Length == 0, "Invalid/empty/cancelled input started subprocess.");
});
if (OperatingSystem.IsWindows())
    await Test("forced parent exit closes Windows job and kills host", async () =>
    {
        var directory = Scenario("tree");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var argument in new[] { "--child-owner", fake, directory }) start.ArgumentList.Add(argument);
        using var parent = Process.Start(start)!;
        try
        {
            await Until(() => File.Exists(Path.Combine(directory, "owner-ready")));
            var pid = Pids(directory).Single();
            var leafPid = Audit(directory).Single(item => item.TryGetProperty("leaf", out _)).GetProperty("leaf").GetInt32();
            Check(IsAlive(pid) && IsAlive(leafPid), "Child tree not live before parent kill.");
            parent.Kill(entireProcessTree: false);
            await parent.WaitForExitAsync();
            await Until(() => !IsAlive(pid) && !IsAlive(leafPid));
        }
        finally { if (!parent.HasExited) parent.Kill(true); }
    });
Console.WriteLine($"RESULT passed={passed} failed={failed}; synthetic-only; artifacts={testRoot}");
return failed == 0 ? 0 : 1;
