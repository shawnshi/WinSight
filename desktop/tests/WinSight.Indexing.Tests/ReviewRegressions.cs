using System.Diagnostics;
using System.Text;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;

internal sealed partial class Suite
{
    private static async Task GitAsync(string root, params string[] arguments)
    {
        var executable = GitKeptFiles.FindExecutable() ?? throw new Exception("Synthetic Git regressions require git.exe.");
        var start = GitKeptFiles.CreateStartInfo(executable, root);
        // Synthetic fixture paths exceed MAX_PATH once Git appends its object hash suffix.
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.longpaths=true");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        await GitKeptFiles.RunAsync(start, CancellationToken.None);
    }
    private static Task InitGitAsync(string root) => GitAsync(root, "init", "--template=");
    private static async Task<IncompleteIndexingException> IncompleteAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (IncompleteIndexingException error) { Check(error.Failures.Count != 0, "Incomplete exception lost structured details."); return error; }
        throw new Exception("Expected explicit IncompleteIndexingException, not successful completion.");
    }

    private async Task ReviewRegressionsAsync()
    {
        await Test("NM-BR-03 Git-kept filtering precedes reads/storage/embedding; tracked, info-exclude, nested and ancestor repos", async () =>
        {
            const string dummy = "DUMMY_NOT_A_SECRET_NM_BR_03";
            var root = Folder("NM-BR-03 repo with spaces"); await InitGitAsync(root);
            var child = Path.Combine(root, "chosen-subfolder"); Directory.CreateDirectory(child);
            var ignored = Put(child, "LocalOptions.cs", "class LocalOptions { string token = \"" + dummy + "\"; }");
            var infoIgnored = Put(child, "InfoOptions.cs", "class InfoOptions { string token = \"" + dummy + "\"; }");
            var tracked = Put(child, "TrackedOptions.cs", "class TrackedOptions { void Kept() {} }");
            var kept = Put(child, "Kept.cs", "class Kept { void Run() {} }");
            Put(root, ".gitignore", "LocalOptions.cs\nTrackedOptions.cs\n");
            Directory.CreateDirectory(Path.Combine(root, ".git", "info"));
            File.WriteAllText(Path.Combine(root, ".git", "info", "exclude"), "InfoOptions.cs\n");
            // Staging is confined to this disposable synthetic repository; the lane index is never touched.
            await GitAsync(root, "add", "--force", "--", "chosen-subfolder/TrackedOptions.cs");
            var marker = Path.Combine(root, "monitor-ran.txt");
            var monitor = Put(root, "monitor.cmd", "@echo off\r\necho unexpected > \"" + marker + "\"\r\n");
            File.AppendAllText(Path.Combine(root, ".git", "config"), "\n[core]\n\tfsmonitor = \"" + monitor.Replace('\\', '/') + "\"\n\thooksPath = \"" + root.Replace('\\', '/') + "\"\n");
            var nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested); await InitGitAsync(nested);
            var nestedIgnored = Put(nested, "NestedLocal.cs", "class NestedLocal { string token = \"" + dummy + "\"; }");
            var nestedKept = Put(nested, "NestedKept.cs", "class NestedKept { void Go() {} }"); Put(nested, ".gitignore", "NestedLocal.cs\n");
            var repo = new FakeRepository(); var extractor = new CountingExtractor(new ContentExtractor()); var model = new FakeEmbedding();
            await new IndexingService(repo, extractor, model).IndexAsync(new([root], Code: true));
            Check(repo.Files.ContainsKey(tracked) && repo.Files.ContainsKey(kept) && repo.Files.ContainsKey(nestedKept), "Git-kept tracked/untracked/nested files lost.");
            foreach (var path in new[] { ignored, infoIgnored, nestedIgnored })
                Check(!extractor.Paths.Contains(path, StringComparer.OrdinalIgnoreCase) && !repo.WritePaths.Contains(path, StringComparer.OrdinalIgnoreCase) && !repo.Files.ContainsKey(path), "Ignored ordinary .cs was read or written.");
            Check(model.Inputs.All(i => !i.Value.Contains(dummy)) && !File.Exists(marker), "Ignored dummy token reached embedding or Git executed configured monitor.");
            var ancestorRepo = new FakeRepository(); var ancestorExtractor = new CountingExtractor(new ContentExtractor());
            await new IndexingService(ancestorRepo, ancestorExtractor, new FakeEmbedding()).IndexAsync(new([child], Code: true));
            Check(ancestorRepo.Files.ContainsKey(kept) && !ancestorExtractor.Paths.Contains(ignored, StringComparer.OrdinalIgnoreCase), "Chosen repo subfolder ignored ancestor Git policy.");
        });
        await Test("NM-BR-03 missing Git fails closed without code reads/writes/embedding", async () =>
        {
            var root = Folder("NM-BR-03 missing"); await InitGitAsync(root); Put(root, "LocalOptions.cs", "class LocalOptions { string token = \"DUMMY_NOT_SECRET\"; }");
            var repo = new FakeRepository(); var extractor = new CountingExtractor(new ContentExtractor()); var model = new FakeEmbedding();
            var path = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", Folder("empty-executable-path"));
                var error = await IncompleteAsync(() => new IndexingService(repo, extractor, model).IndexAsync(new([root], Code: true)));
                Check(error.Failures.Any(f => f.Detail.Contains("Git executable unavailable")) && extractor.Calls == 0 && repo.Files.Count == 0 && model.Inputs.Count == 0, "Missing Git widened scope or hid failure.");
            }
            finally { Environment.SetEnvironmentVariable("PATH", path); }
        });
        await Test("NM-BR-03 Git subprocess output/deadline/exit/cancel bounds and child cleanup", async () =>
        {
            var fakeBin = Folder("NM-BR-03 fake-git-bin");
            foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(source, Path.Combine(fakeBin, Path.GetFileName(source)), true);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "WinSight.Indexing.Tests.exe"), Path.Combine(fakeBin, "git.exe"), true);
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", fakeBin);
                foreach (var mode in new[] { "flood", "missing-nul", "error", "timeout", "cancel" })
                {
                    var root = Folder("NM-BR-03 process-" + mode); Directory.CreateDirectory(Path.Combine(root, ".git"));
                    Put(root, ".git-test-mode", mode); Put(root, "LocalOptions.cs", "class LocalOptions { string token = \"DUMMY_NOT_SECRET\"; }");
                    var repo = new FakeRepository(); var extractor = new CountingExtractor(new ContentExtractor()); var model = new FakeEmbedding();
                    using var cancellation = new CancellationTokenSource();
                    var clock = Stopwatch.StartNew();
                    var run = new IndexingService(repo, extractor, model).IndexAsync(new([root], Code: true), cancellationToken: cancellation.Token);
                    if (mode == "cancel")
                    {
                        while (!File.Exists(Path.Combine(root, ".git-test.pid")) && clock.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(20);
                        cancellation.Cancel();
                        await Throws<OperationCanceledException>(() => run);
                    }
                    else
                    {
                        var error = await IncompleteAsync(() => run);
                        var expected = mode switch { "flood" => "byte bound", "missing-nul" => "NUL-terminated", "error" => "exit 42", _ => "deadline" };
                        Check(error.Failures.Any(f => f.Detail.Contains(expected)), "Git failure detail wrong for " + mode + ": " + error.Message);
                    }
                    Check(clock.Elapsed < TimeSpan.FromSeconds(15) && extractor.Calls == 0 && repo.Files.Count == 0 && model.Inputs.Count == 0,
                        $"Git process limit failed: mode={mode}, elapsed={clock.Elapsed.TotalSeconds:F2}s, extraction={extractor.Calls}, rows={repo.Files.Count}, embeddings={model.Inputs.Count}.");
                    var pidPath = Path.Combine(root, ".git-test.pid"); Check(File.Exists(pidPath), "Synthetic Git child did not run.");
                    var pid = int.Parse(File.ReadAllText(pidPath));
                    try { using var process = Process.GetProcessById(pid); Check(process.HasExited, "Git child leaked after " + mode); }
                    catch (ArgumentException) { }
                }
            }
            finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
        });
        await Test("NM-BR-04 every overlapping root identity is checked before deletion, either root order", async () =>
        {
            foreach (var reverse in new[] { false, true })
            {
                var parent = Folder("NM-BR-04 replaced-" + reverse); var child = Path.Combine(parent, "nested"); Directory.CreateDirectory(child);
                var old = Put(child, "old.txt"); var repo = new FakeRepository(); var index = new IndexingService(repo, new ContentExtractor(), new FakeEmbedding());
                var roots = reverse ? new[] { child, parent } : new[] { parent, child };
                await index.IndexAsync(new(roots)); File.Delete(old); Put(parent, "trigger.txt"); var changed = false;
                var error = await IncompleteAsync(() => index.IndexAsync(new(roots), new ProgressSink(p =>
                {
                    if (p.Phase != "pending" || changed) return;
                    changed = true; Directory.Move(child, child + "-old"); Directory.CreateDirectory(child);
                })));
                Check(changed && repo.Files.ContainsKey(old) && error.Failures.Any(f => f.Root == child && f.Phase == "reconcile_failed"), "Overlapping root mismatch deleted a row or hid the failure.");
            }
        });
        await Test("NM-BR-04 all covering root leases remain held during delete and are released afterward", async () =>
        {
            var parent = Folder("NM-BR-04 leases"); var child = Path.Combine(parent, "nested"); Directory.CreateDirectory(child); var old = Put(child, "old.txt");
            var repo = new FakeRepository(); var index = new IndexingService(repo, new ContentExtractor(), new FakeEmbedding());
            await index.IndexAsync(new([parent, child])); File.Delete(old); var checkedRoots = 0;
            repo.BeforeDelete = _ =>
            {
                foreach (var root in new[] { parent, child })
                {
                    try { Directory.Move(root, root + "-moved"); Directory.Move(root + "-moved", root); throw new Exception("Responsible root was not pinned: " + root); }
                    catch (IOException) { checkedRoots++; }
                }
            };
            await index.IndexAsync(new([parent, child]));
            Check(checkedRoots == 2 && !repo.Files.ContainsKey(old), "Valid delete did not hold every root lease.");
            Directory.Move(child, child + "-released"); Directory.Move(parent, parent + "-released");
        });
        await Test("NM-BR-07 optional progress absence cannot hide incomplete scans; successes and failure details remain", async () =>
        {
            var missing = Path.Combine(Folder("NM-BR-07 parent"), "unavailable"); var goodRoot = Folder("NM-BR-07 good"); var good = Put(goodRoot, "good.txt");
            var repo = new FakeRepository(); var error = await IncompleteAsync(() => new IndexingService(repo, new ContentExtractor(), new FakeEmbedding()).IndexAsync(new([missing, goodRoot])));
            Check(error is IOException && error.Completed == 1 && repo.Files[good].State == "done" && error.Failures.Single().Root == missing && error.Failures[0].Phase == "crawl_failed", "Incomplete outcome lost successful writes/structured details.");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => new IndexingService(repo, new ContentExtractor()).IndexAsync(new([missing]), cancellationToken: cancel.Token));
        });
        await Test("NM-BR-08 changed root casing and file/directory case-only rename preserve binary stored path and ID", async () =>
        {
            var root = Folder("NM-BR-08-Cases"); var original = Put(root, "Document.txt"); var repo = new FakeRepository { StrictPathIdentity = true };
            var index = new IndexingService(repo, new ContentExtractor(), new FakeEmbedding()); await index.IndexAsync(new([root])); var stored = repo.Files[original];
            await Throws<InvalidDataException>(() => repo.UpsertFileAsync(stored with { Path = original.ToUpperInvariant() }));
            Put(root, "Document.txt", "Changed first body is intentionally longer than the original to force extraction and replacement.");
            repo.WritePaths.Clear(); await index.IndexAsync(new([root.ToUpperInvariant()]));
            Check(repo.Files.Count == 1 && repo.Files[original].Id == stored.Id && repo.WritePaths.All(p => string.Equals(p, original, StringComparison.Ordinal)), "Case-variant requested root changed stored path identity.");
            File.Move(original, original + ".temporary"); var renamedFile = Path.Combine(root, "document.TXT"); File.Move(original + ".temporary", renamedFile);
            Directory.Move(root, root + "-temporary"); var renamedRoot = Path.Combine(Path.GetDirectoryName(root)!, "nm-br-08-cases"); Directory.Move(root + "-temporary", renamedRoot);
            Put(renamedRoot, "document.TXT", "Second changed document after a case-only file and directory rename, with another deliberately distinct content size for reindexing.");
            repo.WritePaths.Clear(); await index.IndexAsync(new([renamedRoot]));
            var updated = repo.Files.Values.Single();
            Check(updated.Id == stored.Id && updated.State == "done" && string.Equals(updated.Path, original, StringComparison.Ordinal) && repo.WritePaths.All(p => p == original), "Case-only rename supplied a new binary path with the old ID.");
        });
    }
}

internal static class GitProcessFixture
{
    internal static async Task<bool> TryRunAsync(string[] arguments)
    {
        if (!arguments.Contains("ls-files")) return false;
        var modePath = Path.Combine(Environment.CurrentDirectory, ".git-test-mode");
        if (!File.Exists(modePath)) return false;
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".git-test.pid"), Environment.ProcessId.ToString());
        switch (File.ReadAllText(modePath))
        {
            case "flood":
                var buffer = new byte[65536]; using (var output = Console.OpenStandardOutput())
                    for (var i = 0; i <= GitKeptFiles.MaxOutputBytes / buffer.Length; i++) await output.WriteAsync(buffer);
                break;
            case "missing-nul": Console.Write("Kept.cs"); break;
            case "error": Console.Error.Write("synthetic Git error"); Environment.ExitCode = 42; break;
            case "timeout" or "cancel": await Task.Delay(TimeSpan.FromMinutes(1)); break;
            default: throw new InvalidDataException("Unknown synthetic Git mode.");
        }
        return true;
    }
}
