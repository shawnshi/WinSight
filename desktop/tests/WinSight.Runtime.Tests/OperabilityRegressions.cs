using System.IO.Compression;
using System.Net;
using System.Text;
using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Core.Diagnostics;
using WinSight.Infrastructure.Indexing;
using WinSight.Core.Updates;
using WinSight.Infrastructure.Runtime;

/// <summary>Operability regressions: log, crash report, diagnostics bundle, provisioning policy, login item, update check, locks.</summary>
internal static class OperabilityRegressions
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class MemoryStore : IAutostartStore
    {
        public string? Value;
        public string? Read() => Value;
        public void Write(string? command) => Value = command;
    }

    private sealed class FakeFeed(HttpStatusCode status, string body, string? contentType = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json"), RequestMessage = request });
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test, string root)
    {
        await test("OPS-LOG-ROLL-AND-NOOP", async () =>
        {
            AppLog.ResetForTests();
            AppLog.Info("before initialize: must be a silent no-op");
            var data = Path.Combine(root, "ops-log");
            AppLog.Initialize(data, "unit");
            Check(Directory.Exists(Path.Combine(data, "logs")), "Initialize must create the log directory.");
            var line = new string('x', 7000);
            for (int i = 0; i < 400; i++) AppLog.Info(line);
            AppLog.Error("with exception", new InvalidOperationException("boom-marker"));
            var logs = Directory.GetFiles(Path.Combine(data, "logs"));
            Check(logs.Any(f => Path.GetFileName(f) == "winsight-unit.log"), "Current log file missing.");
            Check(logs.Count(f => Path.GetFileName(f).StartsWith("winsight-unit-", StringComparison.Ordinal)) >= 1, "A log past the size cap must roll to a dated file.");
            Check(File.ReadAllText(Path.Combine(data, "logs", "winsight-unit.log")).Contains("boom-marker"), "Exceptions must be written with their detail.");
            AppLog.ResetForTests();
        });
        await test("OPS-CRASH-REPORT", () =>
        {
            AppLog.ResetForTests();
            var data = Path.Combine(root, "ops-crash");
            AppLog.Initialize(data, "unit");
            string? last = null;
            for (int i = 0; i < AppLog.KeepCrashReports + 3; i++) last = AppLog.WriteCrashReport(new InvalidOperationException("crash-" + i), "test");
            var reports = Directory.GetFiles(Path.Combine(data, "logs"), "crash-*.txt");
            Check(reports.Length == AppLog.KeepCrashReports, "Crash reports must be pruned to the newest ones: " + reports.Length);
            var text = File.ReadAllText(last!);
            Check(text.Contains("crash-" + (AppLog.KeepCrashReports + 2), StringComparison.Ordinal) && text.Contains("version:") && text.Contains("os:"), "Report lacks exception or environment.");
            AppLog.ResetForTests();
            return Task.CompletedTask;
        });
        await test("OPS-DIAGNOSTICS-BUNDLE-REDACTS", () =>
        {
            AppLog.ResetForTests();
            var data = Path.Combine(root, "ops-bundle");
            AppLog.Initialize(data, "unit");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AppLog.Info("indexing " + Path.Combine(profile, "Documents", "secret.docx") + " as " + Environment.UserName + " on " + Environment.MachineName);
            File.WriteAllText(Path.Combine(data, "settings.json"), "{\"folders\":[\"" + profile.Replace("\\", "\\\\") + "\\\\Documents\"]}");
            File.WriteAllBytes(Path.Combine(data, "index.sqlite"), new byte[1024]);
            var zipPath = DiagnosticsBundle.Create(data);
            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).ToArray();
            foreach (var expected in new[] { "README.txt", "environment.txt", "settings.json", "data-directory.txt" })
                Check(names.Contains(expected), "Bundle lacks " + expected);
            Check(names.Any(n => n.StartsWith("logs/winsight-unit", StringComparison.Ordinal)), "Bundle lacks the log.");
            Check(!names.Any(n => n.Contains("index.sqlite", StringComparison.OrdinalIgnoreCase)), "Index content must never be bundled.");
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var text = reader.ReadToEnd();
                Check(!text.Contains(profile, StringComparison.OrdinalIgnoreCase) && !text.Contains(profile.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase), "Profile path leaked into " + entry.FullName);
                if (Environment.UserName.Length >= 3) Check(!System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\p{L}\p{N}_])" + System.Text.RegularExpressions.Regex.Escape(Environment.UserName) + @"(?![\p{L}\p{N}_])", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "User name leaked into " + entry.FullName);
            }
            Check(zip.GetEntry("data-directory.txt") is { } listing && new StreamReader(listing.Open()).ReadToEnd().Contains("index.sqlite"), "The listing should name the index without including it.");
            AppLog.ResetForTests();
            return Task.CompletedTask;
        });
        await test("OPS-PROVISION-POLICY", async () =>
        {
            var data = Path.Combine(root, "ops-provision");
            Directory.CreateDirectory(data);
            var host = Path.Combine(data, "WinSight.FoundryHost.exe");
            File.WriteAllText(host, "stub");
            int calls = 0; int exit = 1; string output = "network down";
            var now = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
            var provisioner = new ExecutionProviderProvisioner(data, host, (_, _) => { calls++; return Task.FromResult((exit, output)); }, TimeSpan.FromSeconds(5), () => now);
            Check((await provisioner.RunAsync(false)).Status == ProvisionStatus.Disabled && calls == 0, "A disabled setting must not start anything.");
            Check((await provisioner.RunAsync(true)).Status == ProvisionStatus.Failed && calls == 1, "First attempt should run and fail.");
            Check((await provisioner.RunAsync(true)).Status == ProvisionStatus.Deferred && calls == 1, "A failed attempt must back off, not retry on every launch.");
            now = now.AddMinutes(61);
            Check((await provisioner.RunAsync(true)).Status == ProvisionStatus.Failed && calls == 2, "After the backoff the attempt may repeat.");
            now = now.AddHours(2);
            Check((await provisioner.RunAsync(true)).Status == ProvisionStatus.Deferred && calls == 2, "Second failure backs off for six hours.");
            Check(ExecutionProviderProvisioner.Backoff(1) < ExecutionProviderProvisioner.Backoff(2) && ExecutionProviderProvisioner.Backoff(3) == TimeSpan.FromHours(24), "Backoff must grow to a daily retry.");
            now = now.AddDays(1); exit = 0; output = "ok";
            Check((await provisioner.RunAsync(true)).Status == ProvisionStatus.Succeeded && calls == 3, "Recovery should succeed once.");
            Check((await provisioner.RunAsync(true)).Status == ProvisionStatus.AlreadyDone && calls == 3, "Success is remembered.");
            var legacy = Path.Combine(root, "ops-provision-legacy"); Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "foundry-provision.done"), "x");
            Check((await new ExecutionProviderProvisioner(legacy, host, (_, _) => { throw new InvalidOperationException("must not run"); }).RunAsync(true)).Status == ProvisionStatus.AlreadyDone, "The legacy marker must still count.");
            var missing = new ExecutionProviderProvisioner(Path.Combine(root, "ops-provision-missing"), Path.Combine(data, "absent.exe"), (_, _) => throw new InvalidOperationException("must not run"));
            Check((await missing.RunAsync(true)).Status == ProvisionStatus.HostMissing, "A missing host is reported, not started.");
        });
        await test("OPS-PROVISION-TIMEOUT-KILLS", async () =>
        {
            var data = Path.Combine(root, "ops-provision-timeout"); Directory.CreateDirectory(data);
            var host = Path.Combine(data, "host.exe"); File.WriteAllText(host, "stub");
            bool cancelled = false;
            var provisioner = new ExecutionProviderProvisioner(data, host, async (_, token) =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                return (0, "");
            }, TimeSpan.FromMilliseconds(200));
            var outcome = await provisioner.RunAsync(true);
            Check(cancelled && outcome.Status == ProvisionStatus.Failed && outcome.Detail.Contains("timed out", StringComparison.Ordinal), "A hung host must be cancelled and reported as timed out: " + outcome);
        });
        await test("OPS-PROVISION-REAL-PROCESS-KILLED", async () =>
        {
            // The default runner against a real child that never exits: the process tree must be gone after the timeout.
            var data = Path.Combine(root, "ops-provision-real"); Directory.CreateDirectory(data);
            var host = Path.Combine(Environment.SystemDirectory, "ping.exe");
            if (!File.Exists(host)) return;
            // ping -n 40 runs for about 40 s; the 600 ms limit must kill it, and nothing may survive the call.
            var provisioner = new ExecutionProviderProvisioner(data, host, timeout: TimeSpan.FromMilliseconds(600), hostArguments: ["-n", "40", "127.0.0.1"]);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await provisioner.RunAsync(true);
            Check(outcome.Status == ProvisionStatus.Failed && outcome.Detail.Contains("timed out", StringComparison.Ordinal) && watch.Elapsed < TimeSpan.FromSeconds(15), "The hung child must be abandoned at the limit: " + outcome + " after " + watch.Elapsed);
            await Task.Delay(500);
            Check(!System.Diagnostics.Process.GetProcessesByName("ping").Any(p => { try { return !p.HasExited && p.StartTime > DateTime.Now.AddSeconds(-30); } catch { return false; } }), "A child must not outlive the provisioning call.");
        });
        await test("OPS-AUTOSTART-APPLY-AND-REPAIR", () =>
        {
            var store = new MemoryStore();
            var registration = new AutostartRegistration(store);
            var normal = AutostartRegistration.CommandFor(@"C:\Apps\WinSight\WinSight.App.exe", @"C:\Data\Default", @"C:\Data\Default");
            Check(normal == "\"C:\\Apps\\WinSight\\WinSight.App.exe\" --autostart", "Default data directory must not be repeated: " + normal);
            Check(AutostartRegistration.CommandFor(@"C:\Apps\W.exe", @"D:\Other", @"C:\Data\Default").EndsWith("--data-dir \"D:\\Other\"", StringComparison.Ordinal), "A custom data directory must travel with the login item.");
            Check(!registration.Apply(false, normal) && store.Value is null, "Disabled and absent: nothing to do.");
            Check(registration.Apply(true, normal) && store.Value == normal, "Enabling writes the command.");
            Check(!registration.Apply(true, normal), "Unchanged command must not rewrite.");
            var moved = normal.Replace("Apps", "Apps2");
            Check(registration.Apply(true, moved) && store.Value == moved, "A moved installation must repair the stale path.");
            Check(registration.Apply(false, moved) && store.Value is null && !registration.IsRegistered, "Disabling removes the login item.");
            return Task.CompletedTask;
        });
        await test("OPS-UPDATE-VERSION-ORDER", () =>
        {
            int Cmp(string a, string b)
            {
                if (!SemanticVersion.TryParse(a, out var x) || !SemanticVersion.TryParse(b, out var y)) throw new InvalidOperationException("parse " + a + " " + b);
                return Math.Sign(SemanticVersion.Compare(x, y));
            }
            Check(Cmp("0.2.0", "0.1.9") > 0 && Cmp("1.0.0", "0.9.9") > 0 && Cmp("0.1.0", "0.1.0") == 0, "core ordering");
            Check(Cmp("0.1.0", "0.1.0-preview.1") > 0, "a release outranks its prerelease");
            Check(Cmp("0.1.0-preview.2", "0.1.0-preview.1") > 0 && Cmp("0.1.0-preview.10", "0.1.0-preview.9") > 0, "numeric prerelease identifiers compare as numbers");
            Check(Cmp("0.1.0-beta", "0.1.0-alpha") > 0 && Cmp("0.1.0-alpha.1", "0.1.0-alpha") > 0, "alphabetic and longer prerelease ordering");
            Check(Cmp("v0.1.0+abc123", "0.1.0") == 0, "leading v and build metadata are ignored");
            Check(!SemanticVersion.TryParse("1.2", out _) && !SemanticVersion.TryParse("a.b.c", out _) && !SemanticVersion.TryParse("1.2.3-", out _), "invalid versions are rejected");
            return Task.CompletedTask;
        });
        await test("OPS-UPDATE-FEED-RULES", async () =>
        {
            const string url = "https://example.org/update.json";
            Check(!(await UpdateChecker.CheckAsync(null, "0.1.0")).Checked, "No feed means the feature is off.");
            Check(!(await UpdateChecker.CheckAsync("http://example.org/u.json", "0.1.0", new FakeFeed(HttpStatusCode.OK, "{}"))).Checked, "A plain-http feed must be refused.");
            var newer = await UpdateChecker.CheckAsync(url, "0.1.0-preview.1", new FakeFeed(HttpStatusCode.OK, "{\"version\":\"0.1.0\",\"url\":\"https://example.org/setup.exe\"}"));
            Check(newer is { Checked: true, UpdateAvailable: true, DownloadUrl: "https://example.org/setup.exe" }, "A newer release must be offered: " + newer.Message);
            Check(!(await UpdateChecker.CheckAsync(url, "0.2.0", new FakeFeed(HttpStatusCode.OK, "{\"version\":\"0.1.0\"}"))).UpdateAvailable, "An older feed is not an update.");
            Check(!(await UpdateChecker.CheckAsync(url, "0.1.0", new FakeFeed(HttpStatusCode.OK, "{\"version\":\"0.2.0\",\"url\":\"http://example.org/s.exe\"}"))).Checked, "A non-https download link is refused.");
            Check(!(await UpdateChecker.CheckAsync(url, "0.1.0", new FakeFeed(HttpStatusCode.OK, "not json"))).Checked, "Malformed feed is reported, not thrown.");
            Check(!(await UpdateChecker.CheckAsync(url, "0.1.0", new FakeFeed(HttpStatusCode.NotFound, ""))).Checked, "HTTP errors are reported.");
            Check(!(await UpdateChecker.CheckAsync(url, "0.1.0", new FakeFeed(HttpStatusCode.OK, "{\"version\":\"0.2.0\",\"pad\":\"" + new string('x', 70_000) + "\"}"))).Checked, "An oversized feed is refused.");
        });
        await test("OPS-SETTINGS-UPDATE-FEED-VALIDATION", async () =>
        {
            var store = new JsonSettingsStore(Path.Combine(root, "ops-settings", "settings.json"));
            var settings = await store.LoadAsync();
            Check(settings.UpdateFeedUrl is null && !settings.InstallExecutionProviders, "Defaults: no feed and no network provisioning.");
            settings.UpdateFeedUrl = "http://insecure.example/u.json";
            await Throws<InvalidDataException>(() => store.SaveAsync(settings));
            settings.UpdateFeedUrl = "https://example.org/u.json";
            await store.SaveAsync(settings);
            Check((await store.LoadAsync()).UpdateFeedUrl == "https://example.org/u.json", "An https feed persists.");
        });
        await test("OPS-INDEX-RUN-LOCK", () =>
        {
            var data = Path.Combine(root, "ops-lock");
            using (var first = IndexRunLock.TryAcquire(data))
            {
                Check(first is not null, "The first run takes the lock.");
                Check(IndexRunLock.TryAcquire(data) is null, "A second run in another process (or here) must be refused while it is held.");
            }
            using var again = IndexRunLock.TryAcquire(data);
            Check(again is not null, "The lock is released when the run ends.");
            return Task.CompletedTask;
        });
        await test("OPS-MAINTENANCE-GRANT-REQUIRED-AND-AUTO-DRAIN", async () =>
        {
            var parent = Path.Combine(root, "ops-maintenance");
            var data = Path.Combine(parent, "data");
            var documents = Path.Combine(parent, "documents");
            var outside = Path.Combine(parent, "outside");
            Directory.CreateDirectory(documents); Directory.CreateDirectory(outside);
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(new AppSettings { Folders = [documents], SetUp = true });
            var store = new MaintenanceJobStore(data);
            await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: true) { MaintenancePollInterval = TimeSpan.FromMilliseconds(60) });
            // 1. No grant: a queued request is never executed or claimed automatically.
            var orphan = await store.SubmitAsync(outside, "old-grant");
            await Task.Delay(500);
            Check(File.Exists(Path.Combine(store.Inbox, orphan.Id + ".json")), "Without a grant the request must stay untouched in the inbox.");
            // 2. A manual drain refuses it explicitly because no grant matches.
            var manual = await backend.DrainMaintenanceJobsAsync();
            Check(manual.Count == 1 && manual[0].Contains("no matching maintenance grant", StringComparison.Ordinal), "A request without a matching grant must be refused: " + string.Join("|", manual));
            // 3. With the grant, the loop picks requests up on its own; an unauthorized folder is still refused.
            var grant = await backend.GrantMaintenanceAsync();
            var job = await store.SubmitAsync(outside, grant!);
            for (int i = 0; i < 60 && !File.Exists(Path.Combine(store.Completed, job.Id + ".result.json")); i++) await Task.Delay(100);
            var result = Path.Combine(store.Completed, job.Id + ".result.json");
            Check(File.Exists(result), "With a grant the app must process the request without a click.");
            Check(File.ReadAllText(result).Contains("outside the authorized folders", StringComparison.Ordinal), "The automatic path must keep the folder authorization check.");
            // 4. Revoking the grant stops it and a stale request is refused.
            await backend.RevokeMaintenanceAsync();
            var stale = await store.SubmitAsync(documents, grant!);
            var after = await backend.DrainMaintenanceJobsAsync();
            Check(after.Count == 1 && after[0].Contains("no matching maintenance grant", StringComparison.Ordinal), "A request made under a revoked grant must be refused: " + string.Join("|", after));
            _ = stale;
        });
    }
}
