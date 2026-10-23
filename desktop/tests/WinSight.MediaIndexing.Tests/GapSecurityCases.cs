using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinSight.MediaIndexing.Tests;

internal static class GapSecurityCases
{
    private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task InitGit(string root)
    {
        string git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim().Trim('"'), "git.exe")).First(File.Exists);
        var start = new ProcessStartInfo(git) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot"); start.Environment.Clear();
        if (systemRoot is not null) start.Environment["SystemRoot"] = systemRoot;
        start.Environment["PATH"] = Path.GetDirectoryName(git)!;
        foreach (var pair in new Dictionary<string, string> { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_SYSTEM"] = "NUL", ["GIT_CONFIG_GLOBAL"] = "NUL", ["GIT_CONFIG_COUNT"] = "0", ["GIT_TERMINAL_PROMPT"] = "0" }) start.Environment[pair.Key] = pair.Value;
        foreach (string arg in new[] { "--no-pager", "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", "-c", "protocol.allow=never", "init", "--quiet", "--template=", root }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic git init did not start");
        var identity = new PidReceipt(process.Id, process.StartTime.ToUniversalTime().Ticks);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.CreationUtcTicks) { process.Kill(); Check.That(process.WaitForExit(3000), "Synthetic git PID exit unconfirmed"); }
            throw;
        }
        var receipt = new { executable = git, arguments = start.ArgumentList.ToArray(), identity, exit = process.ExitCode, stdout = await stdout, stderr = await stderr };
        Owned.Atomic(Path.Combine(Path.GetDirectoryName(root)!, "git-init.json"), receipt);
        Check.That(process.ExitCode == 0 && Directory.Exists(Path.Combine(root, ".git")), "Synthetic local git init failed");
        Check.That(!File.Exists(Path.Combine(root, ".git", "index")), "Synthetic Git must not stage anything");
    }

    internal static async Task Run(Run run, ActualApi api, string fake)
    {
        await run.Case("gap/index/code-unchanged-in-isolated-git", async dir =>
        {
            string root = Path.Combine(dir, "project"); Directory.CreateDirectory(root); await InitGit(root);
            var h = new ConsumerHarness(api); var before = new Dictionary<string, (object Row, string Hash, long Size)>();
            string?[] revisions = [null, "{\"native_media_revision\":\"old\"}", "{\"native_media_revision\":\"native-media-sampling-v1\"}"];
            for (int i = 0; i < revisions.Length; i++)
            {
                string path = Path.Combine(root, $"source{i}.cs"); File.WriteAllText(path, $"class Synthetic{i} {{ }}");
                h.Seed(path, "code", revisions[i]); before[path] = (h.Row(path), Digest(path), new FileInfo(path).Length);
            }
            h.Metadata["indexing_fingerprint"] = "native-extraction-v1|code-lines1800-cells";
            var client = api.Client(fake, Path.Combine(dir, "w"));
            try { await ConsumerHarness.Index(h.Consumer(api.Sampler(client)), h.Request(root, code: true)); }
            finally { await ActualApi.Dispose(client); }
            foreach (var (path, value) in before)
            {
                Check.That(ReferenceEquals(value.Row, h.Row(path)), "Code revision incorrectly invalidated unchanged row");
                Check.That(Digest(path) == value.Hash && new FileInfo(path).Length == value.Size, "Protected synthetic code changed");
            }
            Check.That(h.ExtractCalls == 0 && h.EmbeddingCalls == 0 && !h.Writes.Any(w => w.StartsWith("file:") || w == "delete"), "Code unchanged must bypass extraction, embeddings and row writes");
            Check.That(Directory.GetFileSystemEntries(Path.Combine(dir, "w")).Length == 0 && !Directory.Exists(Path.Combine(root, "p")), "Code candidate launched media child");
            Check.That(!File.Exists(Path.Combine(root, ".git", "index")), "Consumer modified synthetic Git index");
            Owned.Atomic(Path.Combine(dir, "unchanged.json"), before.Select(p => new { path = p.Key, sha256 = p.Value.Hash, size = p.Value.Size, preservedRow = true }).ToArray());
        });
        await run.Case("gap/index/generated-only-code-directories-no-media-child", async dir =>
        {
            string root = Path.Combine(dir, "project"); Directory.CreateDirectory(root); await InitGit(root);
            string[] folders = ["build", "dist", "out", "target", "obj", "coverage", "generated", "__generated__", "vendor", "third_party"];
            var before = new Dictionary<string, string>();
            foreach (string folder in folders)
            {
                string sub = Path.Combine(root, folder); Directory.CreateDirectory(sub);
                foreach (string name in new[] { "source.cs", "media.mp4" }) { string file = Path.Combine(sub, name); File.WriteAllText(file, "synthetic generated only"); before[file] = Digest(file); }
            }
            var client = api.Client(fake, Path.Combine(dir, "w")); var h = new ConsumerHarness(api);
            try { await ConsumerHarness.Index(h.Consumer(api.Sampler(client)), h.Request(root, code: true)); }
            finally { await ActualApi.Dispose(client); }
            Check.That(h.Files.Count == 0 && h.ExtractCalls == 0 && h.EmbeddingCalls == 0, "Generated-only code directories were consumed");
            Check.That(!h.Writes.Any(w => w.StartsWith("file:")) && Directory.GetFileSystemEntries(Path.Combine(dir, "w")).Length == 0, "Generated directories caused row/decode work");
            Check.That(before.All(p => Digest(p.Key) == p.Value), "Generated source receipts changed");
            Owned.Atomic(Path.Combine(dir, "generated.json"), new { folders, unchanged = before, rows = h.Files.Count, decoderChildren = 0 });
        });
        await run.Case("gap/source/pinned-replacement-denied-disposed-permit-replay", async dir =>
        {
            string path = SamplerCases.Source(dir, "audio", new(2)); string hash = Digest(path);
            var source = (IDisposable)api.Source(path, "audio"); var client = api.Client(fake, Path.Combine(dir, "w"));
            try
            {
                SamplerCases.Blocked(path); RenameDenied(path); SamplerCases.BlockedDirectory(dir);
                Check.That(Digest(path) == hash && new FileInfo(path).Length == 4, "Pinned source was changed");
                source.Dispose(); File.Move(path, path + ".original"); File.WriteAllBytes(path, [9, 8, 7, 6]);
                await Check.Throws(async () => { await using var session = (IAsyncDisposable)await api.Open(api.Sampler(client), source); }, e => e.GetType() == typeof(ObjectDisposedException));
                Check.That(SamplerCases.Requests(dir).Length == 0 && Directory.GetFileSystemEntries(Path.Combine(dir, "w")).Length == 0, "Disposed source permit reached inspect child");
                Check.That(Digest(path + ".original") == hash && new FileInfo(path + ".original").Length == 4, "Original source receipt lost");
                Owned.Atomic(Path.Combine(dir, "identity-replay.json"), new { original = hash, replacement = Digest(path), rejection = "ObjectDisposedException", workerRequests = 0 });
            }
            finally { source.Dispose(); await ActualApi.Dispose(client); }
        });
        foreach (var scenario in new[] { (Duration: 10d, Mode: "tone", Budget: 5, Complete: true), (Duration: 32d, Mode: "duplicate", Budget: 12, Complete: false) })
            await run.Case($"gap/lifecycle/full-artifact-birth-peaks/{scenario.Mode}", async dir =>
            {
                string root = Path.Combine(dir, "input"); Directory.CreateDirectory(root);
                string path = SamplerCases.Source(root, "video", new(scenario.Duration, scenario.Mode, Audit: true));
                string workspace = Path.Combine(dir, "w"); var client = api.Client(fake, workspace); var h = new ConsumerHarness(api);
                var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var consumed = new List<string>();
                h.DuringEmbedding = async (input, ct) =>
                {
                    string artifact = ActualApi.Get<string>(input, "Value");
                    Check.That(consumed.All(p => !File.Exists(p)), "Previous consumer artifact survived its completed await");
                    if (consumed.Count == 0) { entered.SetResult(artifact); await release.Task.WaitAsync(ct); }
                    else await Task.Yield();
                    using (var stream = new FileStream(artifact, FileMode.Open, FileAccess.Read, FileShare.Read)) Check.That(stream.ReadByte() >= 0, "Lease unreadable after embedding await");
                    SamplerCases.Blocked(path); consumed.Add(artifact);
                };
                Task? index = null;
                try
                {
                    index = ConsumerHarness.Index(h.Consumer(api.Sampler(client, scenario.Budget)), h.Request(root));
                    try
                    {
                        string artifact = await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                        using (var stream = new FileStream(artifact, FileMode.Open, FileAccess.Read, FileShare.Read)) Check.That(stream.Length > 0, "Artifact missing during blocked await");
                        SamplerCases.Blocked(path); RenameDenied(path); SamplerCases.BlockedDirectory(root);
                    }
                    finally { release.TrySetResult(); }
                    await index;
                    ConsumerHarness.State(h.Row(path), scenario.Complete ? "done" : "partial", scenario.Complete);
                    var births = ArtifactAudit.Read(root); var decodes = births.Where(b => b.Created is not null).ToArray();
                    Check.That(births.Length == scenario.Budget + 1 && births[0].Created is null && decodes.Length == scenario.Budget, "Audit must cover every real attempt plus inspect");
                    Check.That(births.Select(b => b.Sequence).SequenceEqual(Enumerable.Range(0, births.Length)), "Audit sequence incomplete");
                    Check.That(decodes.All(b => b.Live.Contains(b.Created!)), "Birth snapshot missed new artifact");
                    int framePeak = births.Max(b => b.Live.Count(p => Path.GetFileName(p) == "frame.png"));
                    int audioPeak = births.Max(b => b.Live.Count(p => Path.GetFileName(p) == "audio.wav"));
                    Check.That(framePeak == 2 && audioPeak == 1 && births.All(b => b.Live.Length <= 2), "Transient sampler peak exceeds two frames / one audio");
                    Check.That(h.Inputs.Any(i => ActualApi.Get<string>(i, "Operation") == "audio") && h.Inputs.Any(i => ActualApi.Get<string>(i, "Operation") == "image"), "Peak test must actually reach both modalities");
                    Check.That(decodes.All(b => !File.Exists(b.Created)) && consumed.All(p => !File.Exists(p)) && Directory.GetFileSystemEntries(workspace).Length == 0, "Post-await cleanup incomplete");
                    Check.That(SamplerCases.Requests(root).Length == births.Length, "Worker request/audit reconciliation");
                    Owned.Gone(dir);
                    Owned.Atomic(Path.Combine(dir, "peaks.json"), new { framePeak, audioPeak, attempts = decodes.Length, sharedBound = scenario.Budget, complete = scenario.Complete, consumed, postAwaitWorkspaceEmpty = true });
                }
                finally { release.TrySetResult(); if (index is not null && !index.IsCompleted) await index; await ActualApi.Dispose(client); }
            });
        await run.Case("gap/lifecycle/real-inspect-unconfirmed-retention-poison-dispose", async dir =>
        {
            string path = SamplerCases.Source(dir, "audio", new(2, Audit: true)); string hash = Digest(path);
            string workspace = Path.Combine(dir, "w"); Directory.CreateDirectory(workspace);
            var options = api.New("Media.NativeMediaProcessOptions");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var pair in new Dictionary<string, object> { ["HostExecutable"] = fake, ["WorkspaceRoot"] = workspace, ["RequestTimeout"] = TimeSpan.FromSeconds(5), ["TerminationTimeout"] = TimeSpan.FromSeconds(3), ["ForceUnconfirmedTerminationForTesting"] = true }) options.GetType().GetProperty(pair.Key, flags)!.SetValue(options, pair.Value);
            var client = api.New("Media.NativeMediaProcessClient", options);
            Exception failure = await Check.Throws(async () => { await ActualApi.Await(ActualApi.Call(client, "InspectAsync", path, "audio", CancellationToken.None)!); }, e => e.GetType() == typeof(IOException) && e.Message.Contains("termination unconfirmed"));
            var audit = ArtifactAudit.Read(dir); Check.That(audit.Length == 1 && audit[0].Created is null, "Real Inspect worker did not execute");
            string request = audit[0].Request; string requestHash = Digest(request);
            Check.That(Directory.GetDirectories(workspace).Length == 1 && File.Exists(request), "Unsettled inspect task was cleaned");
            SamplerCases.Blocked(path); RenameDenied(path); Owned.Gone(dir);
            Exception poison = await Check.Throws(async () => { await ActualApi.Await(ActualApi.Call(client, "InspectAsync", path, "audio", CancellationToken.None)!); }, e => e.GetType() == typeof(IOException) && e.Message.Contains("retained unsettled resources"));
            Exception disposal = await Check.Throws(async () => await ActualApi.Dispose(client), e => e.GetType() == typeof(IOException) && e.Message.Contains("shutdown has retained unsettled resources"));
            await Check.Throws(async () => await ActualApi.Dispose(client), e => ReferenceEquals(e, disposal));
            Check.That(SamplerCases.Requests(dir).Length == 1 && ArtifactAudit.Read(dir).Length == 1, "Poisoned client launched another child");
            SamplerCases.Blocked(path); RenameDenied(path);
            Check.That(File.Exists(request) && Digest(request) == requestHash && Digest(path) == hash && new FileInfo(path).Length == 4, "Retention protections/receipts lost during Dispose");
            // The retained source pins its immediate parent against rename; publish the receipt in a new unpinned child.
            string evidence = Path.Combine(dir, "evidence"); Directory.CreateDirectory(evidence);
            Owned.Atomic(Path.Combine(evidence, "retention.json"), new { source = path, sourceHash = hash, size = 4, request, requestHash, sourceStillPinned = true, taskRetained = true, failure = failure.ToString(), poison = poison.ToString(), disposal = disposal.ToString(), osWorkerGone = true });
            // Deliberately retained resources belong to production's failure path; no reflection cleanup or false success.
        });
    }
    private static void RenameDenied(string path)
    {
        try { File.Move(path, path + ".forbidden"); }
        catch (IOException) { return; }
        File.Move(path + ".forbidden", path); throw new InvalidDataException("Pinned source rename succeeded");
    }
}
