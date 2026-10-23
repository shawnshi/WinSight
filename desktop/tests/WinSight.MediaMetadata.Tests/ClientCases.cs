using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;

namespace WinSight.MediaMetadata.Tests;

internal sealed class ClientApi(Assembly assembly)
{
    private readonly Type clientType = assembly.GetType("WinSight.Infrastructure.Media.NativeMediaProcessClient", true)!;
    private readonly Type optionsType = assembly.GetType("WinSight.Infrastructure.Media.NativeMediaProcessOptions", true)!;
    private readonly Type requestType = assembly.GetType("WinSight.Infrastructure.Media.NativeMediaProcessRequest", true)!;
    internal static ClientApi? TryLoad(string path, out string? pending)
    {
        Verify.That(Path.IsPathFullyQualified(path) && File.Exists(path), "Explicit production client DLL missing");
        string directory = Path.GetDirectoryName(path)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string file = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var type = assembly.GetType("WinSight.Infrastructure.Media.NativeMediaProcessClient");
        if (type?.GetMethod("InspectAsync", [typeof(string), typeof(string), typeof(CancellationToken)]) is null ||
            type.GetMethod("ExtractSoundtrackAsync") is null)
        { pending = "Actual public InspectAsync/ExtractSoundtrackAsync absent in supplied production DLL; no stub, no retry"; return null; }
        var info = assembly.GetType("WinSight.Infrastructure.Media.NativeMediaInfo", true)!;
        Verify.That(info.GetConstructor([typeof(string), typeof(string), typeof(double), typeof(bool), typeof(int?), typeof(int), typeof(int)]) is not null, "NativeMediaInfo fixed public record signature missing");
        pending = null; return new(assembly);
    }
    internal object Create(string host, string workspace, double timeout = 4, bool forceRetention = false)
    {
        Directory.CreateDirectory(workspace);
        var options = Activator.CreateInstance(optionsType)!;
        foreach (var item in new Dictionary<string, object> { ["HostExecutable"] = host, ["WorkspaceRoot"] = workspace, ["RequestTimeout"] = TimeSpan.FromSeconds(timeout), ["TerminationTimeout"] = TimeSpan.FromSeconds(3) })
            optionsType.GetProperty(item.Key)!.SetValue(options, item.Value);
        if (forceRetention) optionsType.GetProperty("ForceUnconfirmedTerminationForTesting", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(options, true);
        return Activator.CreateInstance(clientType, options)!;
    }
    private static async Task<object> Invoke(object client, MethodInfo method, object?[] args)
    {
        Task task;
        try { task = (Task)method.Invoke(client, args)!; }
        catch (TargetInvocationException e) when (e.InnerException is not null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        await task; return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
    internal Task<object> Inspect(object client, string source, string kind, CancellationToken ct = default) =>
        Invoke(client, clientType.GetMethod("InspectAsync", [typeof(string), typeof(string), typeof(CancellationToken)])!, [source, kind, ct]);
    internal Task<object> Soundtrack(object client, string source, double start = .75, double? end = 2.25, string kind = "audio", CancellationToken ct = default)
    {
        var request = Activator.CreateInstance(requestType, [source, kind, start, end, 512]);
        return Invoke(client, clientType.GetMethod("ExtractSoundtrackAsync", [requestType, typeof(CancellationToken)])!, [request, ct]);
    }
    internal static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    internal static ValueTask Dispose(object value) => ((IAsyncDisposable)value).DisposeAsync();
}

internal static class ClientCases
{
    internal static async Task Run(Run run, ClientApi api, string fake)
    {
        foreach (var kind in new[] { "audio", "video" })
            foreach (var mode in new[] { "valid", "silent" })
                await run.Case($"client/inspect/{kind}/{mode}", dir => WithClient(api, fake, dir, async (c, source, work) =>
                {
                    var info = await api.Inspect(c, source, kind); bool audio = kind == "audio" || mode != "silent";
                    Verify.That(ClientApi.Property<string>(info, "SourcePath") == source && ClientApi.Property<string>(info, "Kind") == kind && ClientApi.Property<double>(info, "DurationSeconds") == 4 && ClientApi.Property<bool>(info, "HasAudio") == audio, "Public metadata/source identity mismatch");
                    Verify.That(ClientApi.Property<int?>(info, "AudioTrackCount") == (kind == "audio" ? null : mode == "silent" ? 0 : 1) && ClientApi.Property<int>(info, "Width") == (kind == "audio" ? 0 : 256) && ClientApi.Property<int>(info, "Height") == (kind == "audio" ? 0 : 144), "Public null count/dimensions mismatch");
                    Empty(work); Owned.Gone(source, "worker");
                }, mode));
        var commonBad = new[] { "badsource", "badkind", "nullsource", "nullkind", "badduration", "zeroduration", "nullduration", "stringduration", "overflowduration", "version", "extra", "missing", "duplicate", "wrongcase", "malformed", "utf8", "stderrutf8", "oversize", "multiline", "badbool", "nullbool" };
        foreach (string mode in commonBad.Concat(["badcount", "fractioncount", "countnull", "stringcount", "zeroaudio", "contradiction", "baddims", "badheight", "fractionwidth", "nullwidth"]))
            await run.Case("client/reject-metadata/" + mode, dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                await Verify.Throws(async () => { await api.Inspect(c, source, "video"); }, e => e is InvalidDataException);
                Empty(work); Owned.Gone(source, "worker");
            }, mode));
        foreach (string mode in new[] { "audiocount", "audiodims", "contradiction" })
            await run.Case("client/reject-audio-metadata/" + mode, dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                await Verify.Throws(async () => { await api.Inspect(c, source, "audio"); }, e => e is InvalidDataException); Empty(work);
            }, mode));
        await run.Case("client/audio-present-extract-failure-is-not-no-audio", dir => WithClient(api, fake, dir, async (c, source, work) =>
        {
            var info = await api.Inspect(c, source, "video"); Verify.That(ClientApi.Property<bool>(info, "HasAudio"), "Precondition: known audio present");
            await Verify.Throws(async () => { await api.Soundtrack(c, source); }, e => e is IOException);
            Verify.That(ClientApi.Property<bool>(info, "HasAudio"), "Decode failure changed metadata to no-audio"); Empty(work);
        }, "nativeerror"));
        foreach (string operation in new[] { "inspect", "soundtrack" })
        {
            foreach (string mode in new[] { "nativeerror", "badsource", "badkind", "version", "extra", "malformed", "badinterval", "badformat", "escape" }.Where(_ => operation == "soundtrack"))
                await run.Case($"client/{operation}/failure/{mode}", dir => WithClient(api, fake, dir, async (c, source, work) =>
                {
                    await Verify.Throws(async () => { await Operation(api, c, source, operation); },
                        e => mode == "nativeerror" ? e is IOException : e is InvalidDataException);
                    Empty(work); Owned.Gone(source, "worker");
                }, mode));
            await run.Case($"client/{operation}/reparse-source-rejected", dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                string target = Path.Combine(dir, "t"), link = Path.Combine(dir, "l"); Directory.CreateDirectory(target); File.Copy(source, Path.Combine(target, "s.wav")); Junction.Create(link, target);
                try
                {
                    await Verify.Throws(async () => { await Operation(api, c, Path.Combine(link, "s.wav"), operation); }, e => e is IOException);
                    Empty(work); Verify.That(!Directory.Exists(Path.Combine(target, "p")), "Reparse source reached fake worker");
                }
                finally { Directory.Delete(link); }
            }));
            await run.Case($"client/{operation}/caller-cancel-and-source-pin", dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                using var cancel = new CancellationTokenSource(); var pending = Operation(api, c, source, operation, cancel.Token);
                await Owned.Wait(source, "worker", "child");
                await Verify.Throws(() => { using var ignored = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); return Task.CompletedTask; }, e => e is IOException);
                cancel.Cancel(); await Verify.Throws(async () => { await pending; }, e => e is OperationCanceledException);
                Owned.Gone(source, "worker", "child"); Empty(work);
            }, "descendant"));
            await run.Case($"client/{operation}/deadline-descendant", dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                var pending = Operation(api, c, source, operation); await Owned.Wait(source, "worker", "child");
                await Verify.Throws(async () => { await pending; }, e => e is TimeoutException); Owned.Gone(source, "worker", "child"); Empty(work);
            }, "descendant", 1.5));
            await run.Case($"client/{operation}/parentexit-reaps-descendant", dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                await Verify.Throws(async () => { await Operation(api, c, source, operation); }, e => e is IOException or TimeoutException);
                Owned.Gone(source, "worker", "child"); Empty(work);
            }, "parentexit"));
            await run.Case($"client/{operation}/dispose-cancels-and-rejects", dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                var pending = Operation(api, c, source, operation); await Owned.Wait(source, "worker", "child"); await ClientApi.Dispose(c);
                await Verify.Throws(async () => { await pending; }, e => e is ObjectDisposedException);
                await Verify.Throws(async () => { await Operation(api, c, source, operation); }, e => e is ObjectDisposedException);
                Owned.Gone(source, "worker", "child"); Empty(work);
            }, "descendant"));
            await run.Case($"client/{operation}/precancel-no-launch", dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                await Verify.Throws(async () => { await Operation(api, c, source, operation, cancel.Token); }, e => e is OperationCanceledException);
                Verify.That(Owned.Read(source).Length == 0, "Canceled admission launched worker"); Empty(work);
            }));
            await run.Case($"client/{operation}/retained-shutdown-fault-is-observable", async dir =>
            {
                string source = Path.Combine(dir, "descendant.wav"), work = Path.Combine(dir, "w"); Wave.Write(source, 4);
                var c = api.Create(fake, work, 8, forceRetention: true); using var cancel = new CancellationTokenSource(); bool shutdownObserved = false;
                try
                {
                    var pending = Operation(api, c, source, operation, cancel.Token); await Owned.Wait(source, "worker", "child"); cancel.Cancel();
                    var requestError = await Verify.Throws(async () => { await pending; }, e => e is IOException);
                    var shutdownError = await Verify.Throws(async () => { await ClientApi.Dispose(c); }, e => e is IOException);
                    shutdownObserved = true; File.WriteAllText(Path.Combine(dir, "retention.txt"), requestError + "\n" + shutdownError);
                    Verify.That(Directory.GetFileSystemEntries(work).Length != 0, "Unconfirmed request resources deleted");
                    await Verify.Throws(async () => { await Operation(api, c, source, operation); }, e => e is IOException or ObjectDisposedException);
                    await Verify.Throws(async () => { await ClientApi.Dispose(c); }, e => e is IOException);
                }
                finally
                {
                    cancel.Cancel();
                    try { if (!shutdownObserved) await ClientApi.Dispose(c); }
                    finally { foreach (var id in Owned.Read(source)) Owned.Stop(id); }
                }
            });
            await run.Case($"client/{operation}/bounded-admission", dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                using var cancel = new CancellationTokenSource(); var pending = new List<Task<object>>();
                try
                {
                    for (int i = 0; i < 16; i++) pending.Add(Operation(api, c, source, operation, cancel.Token));
                    await Owned.Wait(source, "worker");
                    await Verify.Throws(async () => { await Operation(api, c, source, operation, cancel.Token); }, e => e is IOException);
                    Verify.That(Owned.Read(source).Count(i => i.Role == "worker") == 1, "Queued requests launched parallel workers");
                }
                finally
                {
                    cancel.Cancel();
                    foreach (var task in pending) await Verify.Throws(async () => { await task; }, e => e is OperationCanceledException);
                }
                Empty(work); Owned.Gone(source, "worker");
            }, "hang", 15));
        }
        foreach (string? path in new string?[] { null, "", "relative.wav" })
            await run.Case("client/inspect-invalid-path/" + (path ?? "null"), dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                await Verify.Throws(async () => { await api.Inspect(c, path!, "audio"); }, e => e is ArgumentException or IOException); Empty(work);
            }));
        foreach (string kind in new[] { "frame", "", "VIDEO" })
            await run.Case("client/inspect-invalid-kind/" + kind, dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                await Verify.Throws(async () => { await api.Inspect(c, source, kind); }, e => e is ArgumentException); Empty(work);
            }));
        foreach (var interval in new[] { (Start: -.1, End: (double?)1), (Start: 0d, End: (double?)null), (Start: 0d, End: (double?)31), (Start: double.NaN, End: (double?)2), (Start: 1d, End: (double?)1) })
            await run.Case("client/soundtrack-invalid-interval/" + interval, dir => WithClient(api, fake, dir, async (c, source, work) =>
            {
                await Verify.Throws(async () => { await api.Soundtrack(c, source, interval.Start, interval.End); }, e => e is ArgumentException);
                Verify.That(Owned.Read(source).Length == 0, "Invalid interval launched worker"); Empty(work);
            }));
        await run.Case("client/soundtrack-requires-audio-kind", dir => WithClient(api, fake, dir, async (c, source, work) =>
        {
            await Verify.Throws(async () => { await api.Soundtrack(c, source, kind: "frame", end: null); }, e => e is ArgumentException); Empty(work);
        }));
        await run.Case("client/soundtrack-lease-across-await-dispose-source-pins", dir => WithClient(api, fake, dir, async (c, source, work) =>
        {
            var lease = await api.Soundtrack(c, source);
            try
            {
                string path = ClientApi.Property<string>(lease, "ArtifactPath");
                Verify.That(ClientApi.Property<string>(lease, "Modality") == "audio" && ClientApi.Property<double>(lease, "StartSeconds") == .75 && ClientApi.Property<double?>(lease, "EndSeconds") == 2.25 && ClientApi.Property<double?>(lease, "SourceDurationSeconds") == 4, "New operation changed existing lease contract");
                await Task.Yield(); await ClientApi.Dispose(c); Verify.That(File.Exists(path), "Client dispose deleted live lease");
                Wave.Markers(Wave.Read(path), .75);
                await Verify.Throws(() => { File.Move(source, source + ".moved"); return Task.CompletedTask; }, e => e is IOException);
            }
            finally { await ClientApi.Dispose(lease); }
            Empty(work); File.Move(source, source + ".moved");
        }));
    }
    internal static async Task Actual(Run run, ClientApi api, string host, Synthetic fixture)
    {
        foreach (var expected in fixture.Metadata)
            await run.Case("client/actual-inspect/" + Path.GetFileName(expected.SourcePath), async dir =>
            {
                string work = Path.Combine(dir, "w"); var c = api.Create(host, work, 15);
                try
                {
                    var info = await api.Inspect(c, expected.SourcePath, expected.Kind);
                    Verify.That(ClientApi.Property<string>(info, "SourcePath") == expected.SourcePath && ClientApi.Property<string>(info, "Kind") == expected.Kind && Math.Abs(ClientApi.Property<double>(info, "DurationSeconds") - expected.DurationSeconds) < .000001 && ClientApi.Property<bool>(info, "HasAudio") == expected.HasAudio && ClientApi.Property<int?>(info, "AudioTrackCount") == expected.AudioTrackCount && ClientApi.Property<int>(info, "Width") == expected.Width && ClientApi.Property<int>(info, "Height") == expected.Height, "Actual public native metadata mismatch"); Empty(work);
                }
                finally { await ClientApi.Dispose(c); }
            });
        foreach (var interval in fixture.Intervals)
            await run.Case("client/actual-soundtrack/" + (interval.Start == .75 ? "middle" : "EOF"), async dir =>
            {
                string work = Path.Combine(dir, "w"); var c = api.Create(host, work, 15);
                try
                {
                    var lease = await api.Soundtrack(c, fixture.VideoPath, interval.Start, interval.End);
                    try
                    {
                        await Task.Yield(); await ClientApi.Dispose(c); string path = ClientApi.Property<string>(lease, "ArtifactPath");
                        Wave.SamePhysicalTimeline(path, interval.Reference); Wave.Markers(Wave.Read(path), interval.Start);
                        Verify.That(ClientApi.Property<double?>(lease, "SourceDurationSeconds") == fixture.Metadata[2].DurationSeconds && ClientApi.Property<double>(lease, "StartSeconds") == interval.Start && ClientApi.Property<double?>(lease, "EndSeconds") == interval.End, "Actual lease lost original video timeline");
                    }
                    finally { await ClientApi.Dispose(lease); }
                    Empty(work);
                }
                finally { await ClientApi.Dispose(c); }
            });
        await run.Case("client/actual-silent-explicit-failure", async dir =>
        {
            string work = Path.Combine(dir, "w"); var c = api.Create(host, work, 15);
            try { await Verify.Throws(async () => { await api.Soundtrack(c, fixture.SilentPath, .5, 1.5); }, e => e is IOException); Empty(work); }
            finally { await ClientApi.Dispose(c); }
        });
    }
    private static Task<object> Operation(ClientApi api, object c, string source, string operation, CancellationToken ct = default) => operation == "inspect" ? api.Inspect(c, source, "video", ct) : api.Soundtrack(c, source, ct: ct);
    private static void Empty(string work) => Verify.That(Directory.GetFileSystemEntries(work).Length == 0, "Request/artifact/task leaked: " + work);
    private static async Task WithClient(ClientApi api, string host, string dir, Func<object, string, string, Task> body, string mode = "valid", double timeout = 4)
    {
        string source = Path.Combine(dir, mode + ".wav"), work = Path.Combine(dir, "w"); Wave.Write(source, 4); var c = api.Create(host, work, timeout);
        Exception? failure = null;
        try { await body(c, source, work); }
        catch (Exception e) { failure = e; }
        try { await ClientApi.Dispose(c); }
        catch (Exception e) { failure = failure is null ? e : new AggregateException(failure, e); }
        // Emergency cleanup never substitutes for assertions or hides retained shutdown faults.
        foreach (var id in Owned.Read(source)) if (Owned.Alive(id))
        {
            var leak = new IOException("Owned process leaked; emergency cleanup was required: " + id);
            failure = failure is null ? leak : new AggregateException(failure, leak); Owned.Stop(id);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
