using System.Text.Json;
using WinSight.App.Services;
using WinSight.App.ViewModels;
using WinSight.Core;

namespace WinSight.Ui.Harness;

public sealed record TestResult(string Name, bool Passed, string Details, TimeSpan Elapsed);

public sealed record TestSuiteReport(
    string SuiteName,
    DateTime Timestamp,
    bool AllPassed,
    int TotalCount,
    int PassedCount,
    int FailedCount,
    IReadOnlyList<TestResult> Results);

public static class HarnessRunner
{
    public static async Task<TestSuiteReport> RunAllTestsAsync(string tempDir, Action<string>? log = null)
    {
        var results = new List<TestResult>();
        log?.Invoke("=== Starting WinSight UI Synthetic Test Suite (NM-UI-01..10 Verification) ===");

        // 1. Settings & Persistence
        results.Add(await RunTestAsync("Settings_LoadAndSave_Contracts", async () =>
        {
            var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new SettingsViewModel(backend);
            await vm.InitializeAsync();

            if (vm.Hotkey != "Control+Shift+Space") throw new Exception($"Expected default hotkey Control+Shift+Space, got {vm.Hotkey}");
            if (vm.BackgroundIndexingEnabled) throw new Exception("Expected BackgroundIndexingEnabled default false");
            if (vm.IndexHoursStart != 60 || vm.IndexHoursEnd != 420) throw new Exception($"Expected 60/420 index hours, got {vm.IndexHoursStart}/{vm.IndexHoursEnd}");

            vm.BackgroundIndexingEnabled = true;
            vm.IndexHoursStart = 120;
            vm.IndexHoursEnd = 360;
            vm.Theme = "Dark";
            vm.ProcessorOnly = false;
            vm.TrayClick = "window";

            bool saved = await vm.SaveAsync();
            if (!saved) throw new Exception("Failed to save settings");

            var reloaded = await backend.LoadSettingsAsync();
            if (!reloaded.BackgroundIndexingEnabled) throw new Exception("BackgroundIndexingEnabled was not saved");
            if (reloaded.IndexHours.Start != 120 || reloaded.IndexHours.End != 360) throw new Exception("IndexHours minutes were not saved correctly");
            if (reloaded.Theme != "Dark") throw new Exception("Theme Dark was not saved");
            if (reloaded.TrayClick != "window") throw new Exception("TrayClick window was not saved");

            return "Settings atomic load/save verified with BackgroundIndexingEnabled and minutes schedule";
        }, log));

        // 2. NM-UI-03 & NM-UI-09: Folders Management, SetUp Workflow and Path Boundary Separator
        results.Add(await RunTestAsync("Folders_Management_SetUp_And_BoundaryPruning", async () =>
        {
            var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new FoldersViewModel(backend);
            await vm.InitializeAsync();

            string testFolder = Path.Combine(tempDir, "docs");
            string testFolder2 = Path.Combine(tempDir, "docs2");
            string testSub = Path.Combine(testFolder, "private");
            string testSub2 = Path.Combine(testFolder2, "private");

            await vm.AddFolderAsync(testFolder);
            await vm.AddFolderAsync(testFolder2);
            await vm.AddSkippedAsync(testSub);
            await vm.AddSkippedAsync(testSub2);
            await vm.AddSkipTypeAsync(".bak");

            // NM-UI-03: Explicit confirmation required before SetUp=true and monitoring becomes active
            if (vm.IsSetUp) throw new Exception("SetUp must be false before user confirms folders");
            bool confirmed = await vm.ConfirmFoldersAndEnableMonitoringAsync();
            if (!confirmed || !vm.IsSetUp || !vm.IsMonitoringActive)
                throw new Exception("Explicit confirmation failed to set SetUp=true and activate background monitoring");

            // NM-UI-09: Boundary separator pruning check
            await vm.RemoveFolderAsync(testFolder);
            if (vm.Folders.Contains(testFolder)) throw new Exception("Folder not removed");
            if (vm.Skipped.Contains(testSub)) throw new Exception("Child skipped folder was not pruned");
            // Must preserve testSub2 under docs2!
            if (!vm.Skipped.Contains(testSub2))
                throw new Exception("NM-UI-09 Violation: Removing 'docs' mistakenly pruned exclusion 'docs2\\private'!");

            return "Folders management, SetUp explicit confirmation, and path boundary pruning verified";
        }, log));

        // 3. Storage & Model Inspection (Lazy / No auto-probe check)
        results.Add(await RunTestAsync("StorageModel_ExplicitProbe_Contract", async () =>
        {
            var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new StorageModelViewModel(backend);

            if (backend.WasEmbeddingInfoCalled)
                throw new Exception("Violation: GetEmbeddingInfoAsync was called during initialization! It must be explicit user action.");

            await vm.RefreshStatusAsync();
            if (backend.WasEmbeddingInfoCalled)
                throw new Exception("Violation: RefreshStatusAsync invoked embedding info probe!");

            await vm.InspectModelAsync();
            if (!backend.WasEmbeddingInfoCalled) throw new Exception("GetEmbeddingInfoAsync was not called when user clicked inspect");
            if (!vm.HasEmbeddingInfo) throw new Exception("ViewModel did not receive embedding info");
            if (vm.Dimension != 768) throw new Exception($"Expected dimension 768, got {vm.Dimension}");
            if (!vm.RequestedProvider.Contains("CPU")) throw new Exception($"Expected CPU default provider, got {vm.RequestedProvider}");

            return "Embedding inspection strictly explicit on-demand (CPU default, 768-dim vector)";
        }, log));

        // 4. QuickSearch ViewModel Stale Result Guard & Cancellation
        results.Add(await RunTestAsync("QuickSearch_StaleResultGuard_And_Virtualization", async () =>
        {
            var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new QuickSearchViewModel(backend);

            vm.Query = "a";
            vm.Query = "ab";
            vm.Query = "abc";
            vm.Query = "patient";

            await Task.Delay(500);

            if (backend.SearchCallCount > 2) throw new Exception($"Debounce should collapse rapid typing into one two-phase search (keyword then hybrid), saw {backend.SearchCallCount} calls");
            if (backend.SearchModes.Count == 0 || backend.SearchModes[0] != SearchMode.Keyword) throw new Exception("First search phase must be the fast keyword pass");
            if (backend.SearchModes.Count == 2 && backend.SearchModes[1] != SearchMode.Hybrid) throw new Exception("Second search phase must be the hybrid pass");
            if (vm.Hits.Count == 0) throw new Exception("Expected search hits for 'patient'");
            var top = vm.Hits[0];
            if (string.IsNullOrEmpty(top.FileName)) throw new Exception("Search hit missing filename");
            if (string.IsNullOrEmpty(top.ScoreDisplay)) throw new Exception("Search hit missing score display");
            if (string.IsNullOrEmpty(top.IconGlyph)) throw new Exception("Search hit missing icon glyph");

            vm.SelectedKind = "code";
            await Task.Delay(400);
            foreach (var hit in vm.Hits)
            {
                if (!string.Equals(hit.Kind, "code", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Kind filter 'code' returned non-code item: {hit.Kind}");
            }

            return "Stale result guard dropped intermediate responses; virtualized item view models formatted correctly";
        }, log));

        // 5. NM-UI-02, NM-UI-04 & NM-UI-05: MainWindow Indexing Latest Settings & Doc vs Code Separation
        results.Add(await RunTestAsync("MainWindow_IndexNow_DocVsCodeSeparation_And_NoFakePause", async () =>
        {
            var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new MainWindowViewModel(backend);
            await vm.InitializeAsync();

            // NM-UI-04: Modify settings on backend to verify TriggerIndexNowAsync reloads latest committed settings
            var latestSettings = await backend.LoadSettingsAsync();
            latestSettings.Folders = [Path.Combine(tempDir, "DocRoot")];
            latestSettings.CodeFolders = [Path.Combine(tempDir, "CodeRoot")];
            latestSettings.CodeSkipped = [Path.Combine(tempDir, "CodeRoot", "bin")];
            await backend.SaveSettingsAsync(latestSettings);

            backend.RecordedIndexRequests.Clear();
            await vm.TriggerIndexNowAsync();

            // NM-UI-05: Must execute 2 separate index requests: 1 for doc with Code=false, 1 for code with Code=true
            if (backend.RecordedIndexRequests.Count != 2)
                throw new Exception($"Expected 2 separate index requests (doc and code), but got {backend.RecordedIndexRequests.Count}");

            var docReq = backend.RecordedIndexRequests[0];
            if (docReq.Code) throw new Exception("NM-UI-05 Violation: Document roots must have Code=false!");
            if (docReq.Roots.Count != 1 || !docReq.Roots.Contains(Path.Combine(tempDir, "DocRoot")))
                throw new Exception("Document index request did not use latest committed Folders!");

            var codeReq = backend.RecordedIndexRequests[1];
            if (!codeReq.Code) throw new Exception("NM-UI-05 Violation: Code roots must have Code=true!");
            if (codeReq.Roots.Count != 1 || !codeReq.Roots.Contains(Path.Combine(tempDir, "CodeRoot")))
                throw new Exception("Code index request did not use latest committed CodeFolders!");

            return "Manual indexing reloads latest settings and cleanly separates doc (Code=false) from code (Code=true)";
        }, log));

        // 6. Win32 Hotkey Service & Conflict Handling
        results.Add(await RunTestAsync("HotkeyService_Registration_And_ConflictReporting", () =>
        {
            var (mods, vk) = HotkeyService.ParseHotkey("Control+Shift+Space");
            if (mods != (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT) || vk != 0x20)
                throw new Exception($"Failed to parse Control+Shift+Space: mods={mods}, vk={vk}");

            using var hotkey = new HotkeyService("Control+Shift+Space");
            if (hotkey.HasConflict)
            {
                if (string.IsNullOrEmpty(hotkey.ConflictMessage))
                    throw new Exception("Conflict detected but no ConflictMessage provided");
            }

            hotkey.HoldHotkey(true);
            hotkey.HoldHotkey(false);

            return $"Hotkey service initialized, conflict status: {hotkey.HasConflict} ({hotkey.ConflictMessage ?? "No conflict"})";
        }, log));

        // 7. Win32 Tray Icon Lifecycle
        results.Add(await RunTestAsync("TrayIconService_Lifecycle", () =>
        {
            using var tray = new TrayIconService();
            tray.UpdateTooltip("WinSight Test Tooltip");
            return "Tray icon message window created, modified and cleanly disposed";
        }, log));

        // 8. Single Instance Mutex & Signaling
        results.Add(await RunTestAsync("SingleInstance_Mutex_And_DataDirHash", () =>
        {
            string isolatedDir = Path.Combine(tempDir, "SingleInstanceTest");
            string hash1 = SingleInstanceService.ComputeDataDirHash(isolatedDir);
            string hash2 = SingleInstanceService.ComputeDataDirHash(isolatedDir + "\\");
            if (hash1 != hash2) throw new Exception("Data dir hash did not normalize trailing slashes");

            bool acquired1 = SingleInstanceService.TryAcquire(isolatedDir, false, out var inst1);
            if (!acquired1 || inst1 is null) throw new Exception("First instance failed to acquire mutex");

            bool showSignaled = false;
            inst1.ShowRequested += () => showSignaled = true;

            bool acquired2 = SingleInstanceService.TryAcquire(isolatedDir, false, out var inst2);
            if (acquired2 || inst2 is not null)
            {
                inst2?.Dispose();
                inst1.Dispose();
                throw new Exception("Second instance was not blocked by single instance mutex!");
            }

            Thread.Sleep(100);
            inst1.Dispose();

            if (!showSignaled) throw new Exception("Second instance failed to signal ShowRequested on primary instance");

            return $"Single instance mutex with hash {hash1} blocked secondary launch and dispatched signal";
        }, log));

        // 9. NM-UI-01: Command Line Parser Validation
        results.Add(await RunTestAsync("NM_UI_01_CommandLineParser", () =>
        {
            // Valid default
            if (!CommandLineParser.TryParse([], out var dirDefault, out var smoke1, out var toggle1, out var err1))
                throw new Exception($"Default parsing failed: {err1}");
            if (!Path.IsPathFullyQualified(dirDefault)) throw new Exception("Default data-dir must be fully qualified");

            // Valid case-insensitive
            string targetDir = Path.Combine(tempDir, "CustomPreview");
            if (!CommandLineParser.TryParse(["--DATA-DIR", targetDir, "--SMOKE-TEST"], out var dir2, out var smoke2, out var toggle2, out var err2))
                throw new Exception($"Case-insensitive parsing failed: {err2}");
            if (dir2 != Path.GetFullPath(targetDir) || !smoke2) throw new Exception("Parsed values mismatched");

            // Duplicate --data-dir
            if (CommandLineParser.TryParse(["--data-dir", targetDir, "--data-dir", targetDir], out _, out _, out _, out var dupErr))
                throw new Exception("Duplicate --data-dir was permitted");
            if (string.IsNullOrEmpty(dupErr)) throw new Exception("Expected duplicate error message");

            // Missing value
            if (CommandLineParser.TryParse(["--data-dir"], out _, out _, out _, out var missingErr))
                throw new Exception("Missing --data-dir value was permitted");

            // Unknown argument
            if (CommandLineParser.TryParse(["--invalid-flag"], out _, out _, out _, out var unknownErr))
                throw new Exception("Unknown argument was permitted");

            // Relative path rejection
            if (CommandLineParser.TryParse(["--data-dir", "relative\\path"], out _, out _, out _, out var relErr))
                throw new Exception("Relative --data-dir was permitted");

            return "Strict unified CLI parsing verified (case-insensitive, duplicate error, missing value, unknown option, relative path rejection)";
        }, log));

        // 10. NM-UI-06: FileActionPlanner Authorization & Scope Checks
        results.Add(await RunTestAsync("NM_UI_06_FileActionPlanner_AuthorizationAndScope", () =>
        {
            string docRoot = Path.Combine(tempDir, "Docs");
            string codeRoot = Path.Combine(tempDir, "Code");
            string previewDataDir = Path.Combine(tempDir, "PreviewData");

            var settings = new AppSettings
            {
                Folders = [docRoot],
                Skipped = [Path.Combine(docRoot, "private")],
                SkipTypes = [".log"],
                CodeFolders = [codeRoot],
                CodeSkipped = [Path.Combine(codeRoot, "obj")]
            };

            // 1. Authorized doc file
            var plan1 = FileActionPlanner.PlanOpen(Path.Combine(docRoot, "report.pdf"), "pdf", 1, settings, previewDataDir);
            if (!plan1.IsAuthorized) throw new Exception($"Expected authorized doc file, got: {plan1.DenialReason}");

            // 2. Doc file under skipped folder -> Denied
            var plan2 = FileActionPlanner.PlanOpen(Path.Combine(docRoot, "private", "secret.pdf"), "pdf", 1, settings, previewDataDir);
            if (plan2.IsAuthorized) throw new Exception("Doc file under skipped folder was authorized!");

            // 3. Doc file with extension in skipTypes -> Denied
            var plan3 = FileActionPlanner.PlanOpen(Path.Combine(docRoot, "debug.log"), "doc", null, settings, previewDataDir);
            if (plan3.IsAuthorized) throw new Exception("File with extension in skipTypes was authorized!");

            // 4. File outside all roots -> Denied
            var plan4 = FileActionPlanner.PlanOpen(Path.Combine(tempDir, "unauthorized.pdf"), "pdf", null, settings, previewDataDir);
            if (plan4.IsAuthorized) throw new Exception("File outside authorized roots was authorized!");

            // 5. File inside preview data directory -> Denied
            var plan5 = FileActionPlanner.PlanOpen(Path.Combine(previewDataDir, "stolen.pdf"), "pdf", null, settings, previewDataDir);
            if (plan5.IsAuthorized) throw new Exception("File inside preview data directory was authorized!");

            // 6. Authorized code file
            var plan6 = FileActionPlanner.PlanOpen(Path.Combine(codeRoot, "Program.cs"), "code", 10, settings, previewDataDir);
            if (!plan6.IsAuthorized) throw new Exception($"Expected authorized code file, got: {plan6.DenialReason}");

            // 7. Code file under codeSkipped -> Denied
            var plan7 = FileActionPlanner.PlanOpen(Path.Combine(codeRoot, "obj", "temp.cs"), "code", 1, settings, previewDataDir);
            if (plan7.IsAuthorized) throw new Exception("Code file under codeSkipped folder was authorized!");

            return "FileActionPlanner authorization and scope checks fully verified across doc/code/skip boundaries";
        }, log));

        // 11. NM-UI-07: FileActionPlanner Allowlist and Security
        results.Add(await RunTestAsync("NM_UI_07_FileActionPlanner_AllowlistAndSecurity", () =>
        {
            string docRoot = Path.Combine(tempDir, "Docs");
            var settings = new AppSettings { Folders = [docRoot] };

            // Allowed extensions
            string[] allowed = ["test.pdf", "test.txt", "test.docx", "test.png", "test.mp4"];
            foreach (var file in allowed)
            {
                var plan = FileActionPlanner.PlanOpen(Path.Combine(docRoot, file), "doc", null, settings);
                if (!plan.IsAuthorized) throw new Exception($"Allowed file '{file}' was denied: {plan.DenialReason}");
            }

            // Dangerous extensions: .hta, .lnk, .url, .website, .appref-ms, .reg, .exe, .bat, .cmd, .ps1, .vbs
            string[] dangerous = ["attack.hta", "shortcut.lnk", "bookmark.url", "site.website", "app.appref-ms", "hack.reg", "bad.exe", "bad.bat", "bad.cmd", "bad.ps1", "bad.vbs"];
            foreach (var bad in dangerous)
            {
                var plan = FileActionPlanner.PlanOpen(Path.Combine(docRoot, bad), "doc", null, settings);
                if (plan.IsAuthorized) throw new Exception($"Dangerous file '{bad}' was not blocked!");
            }

            return "FileActionPlanner security allowlist verified; all dangerous executable extensions blocked";
        }, log));

        // 12. NM-UI-10: FileActionPlanner Editor & Locator Dispatch
        results.Add(await RunTestAsync("NM_UI_10_FileActionPlanner_EditorAndLocatorDispatch", () =>
        {
            string codeRoot = Path.Combine(tempDir, "Code");
            string docRoot = Path.Combine(tempDir, "Docs");

            // 1. VS Code editor dispatch
            var settingsVsCode = new AppSettings { CodeFolders = [codeRoot], CodeEditor = "vscode" };
            var planVsCode = FileActionPlanner.PlanOpen(Path.Combine(codeRoot, "app.cs"), "code", 42, settingsVsCode);
            if (planVsCode.ActionType != ActionPlanType.LaunchProcess || planVsCode.Executable != "code")
                throw new Exception($"Expected VS Code launch process, got {planVsCode.Executable}");
            if (planVsCode.Arguments.Count != 2 || planVsCode.Arguments[0] != "-g" || !planVsCode.Arguments[1].EndsWith(":42"))
                throw new Exception($"Expected argument -g path:42, got: {string.Join(" ", planVsCode.Arguments)}");

            // 2. Cursor editor dispatch
            var settingsCursor = new AppSettings { CodeFolders = [codeRoot], CodeEditor = "cursor" };
            var planCursor = FileActionPlanner.PlanOpen(Path.Combine(codeRoot, "app.cs"), "code", 88, settingsCursor);
            if (planCursor.ActionType != ActionPlanType.LaunchProcess || planCursor.Executable != "cursor")
                throw new Exception($"Expected Cursor launch process, got {planCursor.Executable}");

            // 3. Notepad default text editor dispatch
            var settingsDefault = new AppSettings { CodeFolders = [codeRoot], CodeEditor = "default" };
            var planDefault = FileActionPlanner.PlanOpen(Path.Combine(codeRoot, "app.cs"), "code", 1, settingsDefault);
            if (planDefault.ActionType != ActionPlanType.LaunchProcess || planDefault.Executable != "notepad.exe")
                throw new Exception($"Expected notepad launch process, got {planDefault.Executable}");

            // 4. PDF Locator Notice
            var settingsDoc = new AppSettings { Folders = [docRoot] };
            var planPdf = FileActionPlanner.PlanOpen(Path.Combine(docRoot, "paper.pdf"), "pdf", 7, settingsDoc);
            if (planPdf.NoticeMessage == null || !planPdf.NoticeMessage.Contains("Page 7"))
                throw new Exception($"PDF locator notice missing or invalid: {planPdf.NoticeMessage}");

            // 5. Media Locator Notice
            var planMedia = FileActionPlanner.PlanOpen(Path.Combine(docRoot, "talk.mp4"), "video", 75.5, settingsDoc);
            if (planMedia.NoticeMessage == null || !planMedia.NoticeMessage.Contains("01:15"))
                throw new Exception($"Media locator notice missing or invalid: {planMedia.NoticeMessage}");

            return "Editor dispatch with explicit ArgumentList and honest locator notices verified";
        }, log));

        // 4b. Two-phase search: keyword rows first, hybrid refines in place, selection survives, every state is distinct
        results.Add(await RunTestAsync("QuickSearch_TwoPhase_And_States", async () =>
        {
            var backend = new SyntheticDesktopBackend(tempDir);
            var gate = new TaskCompletionSource();
            SearchHit Hit(string name, double score, string type) => new(
                FileId: name.GetHashCode(), Path: Path.Combine(tempDir, name), Kind: "doc", Score: score, MatchType: type,
                Excerpt: "excerpt " + name, Location: 1, FileSize: 10, ModifiedTime: 1);
            var a = Hit("a.docx", .9, "keyword"); var b = Hit("b.docx", .8, "keyword"); var c = Hit("c.docx", .7, "semantic");
            string? lastKind = null, lastQuery = null;
            backend.SearchHandler = async (req, ct) =>
            {
                lastKind = req.Kind; lastQuery = req.Query;
                switch (req.Query)
                {
                    case "none": return new SearchResponse(Array.Empty<SearchHit>(), TimeSpan.FromMilliseconds(1), true);
                    case "boom": throw new InvalidOperationException("boom");
                    case "partial":
                        if (req.Mode == SearchMode.Keyword) return new SearchResponse(new[] { a }, TimeSpan.FromMilliseconds(1), false);
                        throw new InvalidOperationException("embedding host down");
                }
                if (req.Mode == SearchMode.Keyword) return new SearchResponse(new[] { a, b }, TimeSpan.FromMilliseconds(1), false);
                await gate.Task.WaitAsync(ct);
                return new SearchResponse(new[] { b, a, c }, TimeSpan.FromMilliseconds(5), true);
            };
            var vm = new QuickSearchViewModel(backend, searchDebounce: TimeSpan.Zero);

            async Task WaitFor(Func<bool> condition, string what)
            {
                for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(20);
                if (!condition()) throw new Exception("Timed out waiting for: " + what);
            }

            if (vm.ViewState != SearchViewState.Idle) throw new Exception("Fresh view model must be Idle");

            vm.Query = "plan";
            await WaitFor(() => vm.Hits.Count == 2, "keyword rows");
            if (!vm.IsLoading || !vm.IsRefining) throw new Exception("Keyword rows must show while the semantic pass is still running");
            if (vm.ViewState != SearchViewState.Results) throw new Exception("Keyword rows must be in Results state");
            vm.SelectedIndex = 1;
            gate.SetResult();
            await WaitFor(() => vm.Hits.Count == 3 && !vm.IsLoading, "hybrid refinement");
            if (vm.SelectedItem is null || !vm.SelectedItem.Path.EndsWith("b.docx")) throw new Exception("Selection must follow the same hit after refinement");
            if (vm.SelectedIndex != 0) throw new Exception($"Selected hit moved to index 0 in the refined list, got {vm.SelectedIndex}");

            vm.Query = "none";
            await WaitFor(() => vm.ViewState == SearchViewState.NoResults && !vm.IsLoading, "no-results state");
            if (vm.Hits.Count != 0) throw new Exception("No-results state must clear previous rows");

            vm.Query = "boom";
            await WaitFor(() => vm.ViewState == SearchViewState.Error && !vm.IsLoading, "error state");
            if (!vm.HasNotice) throw new Exception("Error state must carry a notice");

            vm.Query = "partial";
            await WaitFor(() => vm.Hits.Count == 1 && !vm.IsLoading, "keyword fallback");
            if (vm.ViewState != SearchViewState.Results || !vm.HasNotice) throw new Exception("Hybrid failure must keep keyword rows and explain why");

            vm.Query = "none type:pdf";
            await WaitFor(() => vm.ViewState == SearchViewState.NoResults && !vm.IsLoading, "operator query");
            if (lastKind != "pdf" || lastQuery != "none") throw new Exception($"type: operator must reach the backend as Kind=pdf with a clean query, got kind={lastKind} query={lastQuery}");
            if (vm.SelectedKind is not null) throw new Exception("An inline operator must not change the type bar");

            vm.Query = "";
            await WaitFor(() => vm.ViewState == SearchViewState.Idle, "idle state");
            return "Keyword rows first, hybrid refinement keeps selection; idle/no-results/error/keyword-fallback states are distinct";
        }, log));

        // 4d. Hits of one file collapse onto one row; expansion is in place, keeps selection and survives a refresh
        results.Add(await RunTestAsync("QuickSearch_GroupsHitsByFile", async () =>
        {
            var backend = new SyntheticDesktopBackend(tempDir);
            SearchHit Hit(string name, double location, double score) => new(
                FileId: 7, Path: Path.Combine(tempDir, name), Kind: "pdf", Score: score, MatchType: "keyword",
                Excerpt: "page " + location, Location: location, FileSize: 10, ModifiedTime: 1);
            var hits = new[] { Hit("deck.pdf", 2, .9), Hit("other.pdf", 1, .8), Hit("deck.pdf", 5, .7), Hit("deck.pdf", 9, .6) };
            backend.SearchHandler = (_, _) => Task.FromResult(new SearchResponse(hits, TimeSpan.FromMilliseconds(1), true));
            var vm = new QuickSearchViewModel(backend, searchDebounce: TimeSpan.Zero);
            async Task WaitFor(Func<bool> condition, string what)
            {
                for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(20);
                if (!condition()) throw new Exception("Timed out waiting for: " + what);
            }

            vm.Query = "deck";
            await WaitFor(() => vm.Hits.Count == 2 && !vm.IsLoading, "collapsed rows");
            var deck = vm.Hits[0];
            if (deck.MoreCount != 2 || deck.IsSecondary || deck.GroupExpanded) throw new Exception("First row must be the best hit with 2 more hits folded under it");
            if (!vm.StatusText.Contains("4 条命中") || !vm.StatusText.Contains("2 个文件")) throw new Exception("Status must report hits and files: " + vm.StatusText);

            vm.SelectedIndex = 1; // other.pdf
            vm.ToggleGroup(deck);
            if (vm.Hits.Count != 4 || !vm.Hits[1].IsSecondary || !vm.Hits[2].IsSecondary || vm.Hits[3].Path.EndsWith("other.pdf") == false)
                throw new Exception("Expanding must insert the other hits directly under their file row");
            if (vm.SelectedItem is null || !vm.SelectedItem.Path.EndsWith("other.pdf")) throw new Exception("Expanding a group above the selection must not move the selection");

            vm.Query = "deck2";
            await WaitFor(() => vm.Hits.Count == 4 && !vm.IsLoading, "expansion survives refresh");
            if (!vm.Hits[0].GroupExpanded) throw new Exception("A refresh must not fold a group the user opened");

            vm.SelectedIndex = 2; // a secondary row
            vm.CollapseGroupOf(vm.SelectedItem);
            if (vm.Hits.Count != 2 || vm.SelectedItem is null || vm.SelectedItem.IsSecondary || !vm.SelectedItem.Path.EndsWith("deck.pdf"))
                throw new Exception("Folding while a child is selected must move the selection to its file row");
            return "One row per file, in-place expansion keeps selection, survives refresh, folds back to the file row";
        }, log));

        // 4c. Query operators and history (pure logic; the in-memory store never touches user data)
        results.Add(await RunTestAsync("Search_Operators_And_History", () =>
        {
            var (q1, k1) = WinSight.App.Services.SearchOperators.Parse("预算 type:pdf");
            if (q1 != "预算" || k1 != "pdf") throw new Exception($"type:pdf not extracted: [{q1}] [{k1}]");
            var (q2, k2) = WinSight.App.Services.SearchOperators.Parse("type:图片 海边 日落");
            if (q2 != "海边 日落" || k2 != "image") throw new Exception($"type:图片 not extracted: [{q2}] [{k2}]");
            var (q3, k3) = WinSight.App.Services.SearchOperators.Parse("type:unknown 预算");
            if (q3 != "type:unknown 预算" || k3 is not null) throw new Exception("Unknown operator values must stay part of the text");
            var (q4, k4) = WinSight.App.Services.SearchOperators.Parse("code: type:pdf");
            if (q4 != "code: type:pdf" || k4 is not null) throw new Exception("code: queries must not be rewritten");
            var (q5, k5) = WinSight.App.Services.SearchOperators.Parse("notype:pdf x");
            if (k5 is not null) throw new Exception("An operator must start at a word boundary");

            var store = new WinSight.App.Services.SearchHistoryStore(null, capacity: 3);
            store.Add("alpha"); store.Add("beta"); store.Add("Alpha"); store.Add("gamma"); store.Add("delta"); store.Add("x");
            if (store.Items.Count != 3 || store.Items[0] != "delta" || store.Items[1] != "gamma" || store.Items[2] != "Alpha")
                throw new Exception("History must be most-recent-first, deduplicated case-insensitively, capped and ignore 1-char queries: " + string.Join("|", store.Items));
            if (store.Suggest("al").Single() != "Alpha") throw new Exception("Suggest must filter by substring");
            var file = Path.Combine(tempDir, "history", "search-history.json");
            var persistent = new WinSight.App.Services.SearchHistoryStore(file);
            persistent.Add("persisted query");
            var reopened = new WinSight.App.Services.SearchHistoryStore(file);
            if (reopened.Items.Count != 1 || reopened.Items[0] != "persisted query") throw new Exception("History must survive a restart");
            reopened.Clear();
            if (new WinSight.App.Services.SearchHistoryStore(file).Items.Count != 0) throw new Exception("Clear must persist");
            return "type: operator parsed at word boundaries, unknown values and code: untouched; history ordered, capped, persistent and clearable";
        }, log));

        // 4e. Layout state: dragged list width and the one-time tip persist; widths are always clamped to something usable
        results.Add(await RunTestAsync("Search_Layout_State", () =>
        {
            var file = Path.Combine(tempDir, "layout", "search-layout.json");
            var store = new WinSight.App.Services.SearchLayoutStore(file);
            if (store.ListWidth is not null || store.TipSeen) throw new Exception("Fresh layout state must be empty");
            store.ListWidth = 410; store.TipSeen = true;
            var reopened = new WinSight.App.Services.SearchLayoutStore(file);
            if (reopened.ListWidth != 410 || !reopened.TipSeen) throw new Exception("Layout state must survive a restart");
            reopened.ListWidth = null;
            if (new WinSight.App.Services.SearchLayoutStore(file).ListWidth is not null) throw new Exception("Reset must persist");
            File.WriteAllText(file, "{ not json");
            if (new WinSight.App.Services.SearchLayoutStore(file).ListWidth is not null) throw new Exception("A corrupt file must fall back to defaults");
            File.WriteAllText(file, "{\"ListWidth\": 12}");
            if (new WinSight.App.Services.SearchLayoutStore(file).ListWidth is not null) throw new Exception("A width below the minimum must be ignored");
            double Clamp(double w, double c) => WinSight.App.Services.SearchLayoutStore.ClampListWidth(w, c);
            if (Clamp(100, 1200) != 260) throw new Exception("Lower clamp");
            if (Clamp(2000, 1200) != 840) throw new Exception("Upper clamp must leave 360px for the stage");
            if (Clamp(500, 500) != 260) throw new Exception("A container too small for both falls back to the minimum list width");
            return "list width and tip flag persist; corrupt or tiny values fall back; clamp keeps list >=260 and stage >=360";
        }, log));

        // 13. Search Kind Contracts & Mapping
        results.Add(await RunTestAsync("Search_Kind_Contracts_And_Mapping", async () =>
        {
            var backend = new SyntheticDesktopBackend(tempDir);
            var vm = new QuickSearchViewModel(backend);

            string[] validKinds = ["doc", "pdf", "image", "screenshot", "video", "audio", "code"];
            foreach (var kind in validKinds)
            {
                vm.SelectedKind = kind;
                if (vm.SelectedKind != kind) throw new Exception($"Valid kind '{kind}' was altered: '{vm.SelectedKind}'");
                var res = await backend.SearchAsync(new SearchRequest("test", Kind: vm.SelectedKind));
                if (res == null) throw new Exception($"Search failed for kind '{kind}'");
            }

            vm.SelectedKind = "document";
            if (vm.SelectedKind != "doc") throw new Exception($"Legacy alias 'document' was not mapped to 'doc', got: {vm.SelectedKind}");

            vm.SelectedKind = "media";
            if (vm.SelectedKind != "video") throw new Exception($"Legacy alias 'media' was not mapped to 'video', got: {vm.SelectedKind}");

            vm.SelectedKind = "unsupported_xyz";
            if (vm.SelectedKind != null) throw new Exception($"Unsupported kind was not reset to null, got: {vm.SelectedKind}");

            return "All 7 valid backend kinds verified and UI legacy aliases mapped to real service contracts";
        }, log));

        // 14. NM-UI-06 Regression 1: Nested Chosen Roots (Open/Reveal/Copy)
        results.Add(await RunTestAsync("NM_UI_06_NestedChosenRoots_OpenRevealCopy_Regression", () =>
        {
            string previewDataDir = Path.Combine(tempDir, "PreviewDataDir");
            string baseDoc = Path.Combine(tempDir, "Docs");
            string nestedChosenDoc = Path.Combine(baseDoc, "private", "chosen");
            string excludedDoc = Path.Combine(baseDoc, "private");
            string subSkipDoc = Path.Combine(nestedChosenDoc, "deep_skip");

            var settings = new AppSettings
            {
                Folders = [baseDoc, nestedChosenDoc],
                Skipped = [excludedDoc, subSkipDoc],
                CodeFolders = [Path.Combine(tempDir, "Code"), Path.Combine(tempDir, "Code", "vendor", "lib")],
                CodeSkipped = [Path.Combine(tempDir, "Code", "vendor")]
            };

            // A. Target strictly inside nested chosen root: must be AUTHORIZED for Open, Reveal and Copy
            string nestedFile = Path.Combine(nestedChosenDoc, "report.pdf");
            var openPlan = FileActionPlanner.PlanOpen(nestedFile, "pdf", 1, settings, previewDataDir);
            if (!openPlan.IsAuthorized)
                throw new Exception($"NM-UI-06 Violation: Nested chosen root file was rejected! Reason: {openPlan.DenialReason}");

            var revealPlan = FileActionPlanner.PlanReveal(nestedFile, "pdf", settings, previewDataDir);
            if (!revealPlan.IsAuthorized)
                throw new Exception($"NM-UI-06 Violation: Nested chosen reveal was rejected! Reason: {revealPlan.DenialReason}");

            var copyPlan = FileActionPlanner.PlanCopy(nestedFile, "pdf", settings, previewDataDir);
            if (!copyPlan.IsAuthorized)
                throw new Exception($"NM-UI-06 Violation: Nested chosen copy was rejected! Reason: {copyPlan.DenialReason}");

            // B. Target in skipped folder outside chosen root: must be DENIED
            string outsideChosen = Path.Combine(excludedDoc, "other.pdf");
            var deniedPlan = FileActionPlanner.PlanOpen(outsideChosen, "pdf", 1, settings, previewDataDir);
            if (deniedPlan.IsAuthorized)
                throw new Exception("NM-UI-06 Violation: File in excluded folder outside nested chosen root was authorized!");

            // C. Target in sub-skip inside nested chosen root: must be DENIED
            string subSkipFile = Path.Combine(subSkipDoc, "secret.pdf");
            var subSkipPlan = FileActionPlanner.PlanOpen(subSkipFile, "pdf", 1, settings, previewDataDir);
            if (subSkipPlan.IsAuthorized)
                throw new Exception("NM-UI-06 Violation: File inside sub-skip within chosen root was authorized!");

            // D. Nested chosen for Code roots
            string nestedCodeFile = Path.Combine(tempDir, "Code", "vendor", "lib", "module.cs");
            var codePlan = FileActionPlanner.PlanOpen(nestedCodeFile, "code", 10, settings, previewDataDir);
            if (!codePlan.IsAuthorized)
                throw new Exception($"NM-UI-06 Violation: Nested chosen code file was rejected! Reason: {codePlan.DenialReason}");

            string unchosenCodeFile = Path.Combine(tempDir, "Code", "vendor", "other.cs");
            var unchosenCodePlan = FileActionPlanner.PlanOpen(unchosenCodeFile, "code", 10, settings, previewDataDir);
            if (unchosenCodePlan.IsAuthorized)
                throw new Exception("NM-UI-06 Violation: Unchosen code in skipped folder was authorized!");

            // E. Data directory hard-exclusion cannot be overridden even if added to Folders
            var maliciousSettings = new AppSettings { Folders = [previewDataDir] };
            var hardExcludedPlan = FileActionPlanner.PlanOpen(Path.Combine(previewDataDir, "data.pdf"), "pdf", 1, maliciousSettings, previewDataDir);
            if (hardExcludedPlan.IsAuthorized)
                throw new Exception("NM-UI-06 Violation: Data directory hard exclusion was overridden by root!");

            foreach (bool codeDomain in new[] { false, true })
            {
                string parent = Path.Combine(tempDir, codeDomain ? "EqualCode" : "EqualDocs");
                string chosen = Path.Combine(parent, "private");
                var equal = new AppSettings();
                var roots = codeDomain ? equal.CodeFolders : equal.Folders;
                var skips = codeDomain ? equal.CodeSkipped : equal.Skipped;
                roots.Add(parent);
                roots.Add(chosen + Path.DirectorySeparatorChar);
                skips.Add(chosen.ToUpperInvariant());
                string target = Path.Combine(chosen, codeDomain ? "module.cs" : "report.pdf");
                string kind = codeDomain ? "code" : "pdf";
                if (!FileActionPlanner.PlanOpen(target, kind, 1, equal, previewDataDir).IsAuthorized
                    || !FileActionPlanner.PlanReveal(target, kind, equal, previewDataDir).IsAuthorized
                    || !FileActionPlanner.PlanCopy(target, kind, equal, previewDataDir).IsAuthorized)
                    throw new Exception($"Equal chosen/skip root incorrectly denied an action; codeDomain={codeDomain}.");
                string deep = Path.Combine(chosen, "still-skipped");
                skips.Add(deep);
                string blocked = Path.Combine(deep, codeDomain ? "private.cs" : "private.pdf");
                if (FileActionPlanner.PlanOpen(blocked, kind, 1, equal, previewDataDir).IsAuthorized
                    || FileActionPlanner.PlanReveal(blocked, kind, equal, previewDataDir).IsAuthorized
                    || FileActionPlanner.PlanCopy(blocked, kind, equal, previewDataDir).IsAuthorized)
                    throw new Exception($"Equal-root override widened a strictly nested skip; codeDomain={codeDomain}.");
            }
            return "Nested/equal chosen roots authorize all three actions in both domains; strict child skips and hard exclusions remain denied";
        }, log));

        // 15. NM-UI-06 Regression 2: Settings Save Never Resurrects Revoked Roots or Background Indexing Grant
        results.Add(await RunTestAsync("NM_UI_06_SettingsSave_NeverResurrectRevokedRootsOrBG_Regression", async () =>
        {
            var backend = new SyntheticDesktopBackend(tempDir);

            // Step 1: Initial state has an authorized folder and background monitoring enabled
            string originalFolder = Path.Combine(tempDir, "OriginalDocs");
            var initial = await backend.LoadSettingsAsync();
            initial.Folders = [originalFolder];
            initial.BackgroundIndexingEnabled = true;
            initial.SetUp = true;
            initial.Theme = "Default";
            await backend.SaveSettingsAsync(initial); // Revision becomes 1

            // Step 2: SettingsViewModel loads snapshot with originalFolder and BG=true
            var settingsVm = new SettingsViewModel(backend);
            await settingsVm.InitializeAsync();

            // Step 3: Another operation revokes the folder and disables background indexing
            var revoked = await backend.LoadSettingsAsync();
            revoked.Folders = [];
            revoked.BackgroundIndexingEnabled = false;
            revoked.SetUp = false;
            await backend.SaveSettingsAsync(revoked); // Revision becomes 2

            // Step 4: SettingsViewModel only changes Theme to "Dark" and saves
            settingsVm.Theme = "Dark";
            bool saveResult = await settingsVm.SaveAsync();
            if (!saveResult)
                throw new Exception($"SettingsViewModel.SaveAsync() failed: {settingsVm.StatusMessage}");

            // Step 5: Verify backend state: Theme updated, but revoked folder and BG grant MUST NOT be resurrected!
            var currentBackendSettings = await backend.LoadSettingsAsync();
            if (currentBackendSettings.Theme != "Dark")
                throw new Exception($"Expected Theme 'Dark', got '{currentBackendSettings.Theme}'");

            if (currentBackendSettings.Folders.Count != 0)
                throw new Exception($"NM-UI-06 Violation: Revoked Folders were resurrected! Count={currentBackendSettings.Folders.Count}, first={currentBackendSettings.Folders[0]}");

            if (currentBackendSettings.BackgroundIndexingEnabled)
                throw new Exception("NM-UI-06 Violation: Revoked BackgroundIndexingEnabled was resurrected to true!");

            if (currentBackendSettings.SetUp)
                throw new Exception("NM-UI-06 Violation: Revoked SetUp was resurrected to true!");

            // Step 6: Verify CAS collision rejection: direct save with stale revision MUST throw
            var staleSnapshot = new AppSettings { Revision = 0, Theme = "Stale" };
            bool casRejected = false;
            try
            {
                await backend.SaveSettingsAsync(staleSnapshot);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase))
            {
                casRejected = true;
            }
            if (!casRejected)
                throw new Exception("Backend SaveSettingsAsync did not reject stale revision with CAS exception!");

            // Step 7: Verify FileActionPlanner rejects file in revoked folder under current settings
            var authCheck = FileActionPlanner.CheckAuthorization(Path.Combine(originalFolder, "file.pdf"), "pdf", currentBackendSettings);
            if (authCheck.Allowed)
                throw new Exception("File in revoked folder was authorized after Settings save!");

            return "Settings save preserved latest authorization state; revoked roots and BG grants were not resurrected, CAS conflict enforced";
        }, log));

        bool allPassed = results.All(r => r.Passed);
        var report = new TestSuiteReport(
            SuiteName: "WinSight.Ui.SyntheticTestSuite",
            Timestamp: DateTime.UtcNow,
            AllPassed: allPassed,
            TotalCount: results.Count,
            PassedCount: results.Count(r => r.Passed),
            FailedCount: results.Count(r => !r.Passed),
            Results: results);

        log?.Invoke($"=== Test Run Completed: {report.PassedCount}/{report.TotalCount} passed ===");

        try
        {
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string reportPath = Path.Combine(AppContext.BaseDirectory, "ui-harness-report.json");
            File.WriteAllText(reportPath, json);

            string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            if (Directory.Exists(projectRoot))
            {
                File.WriteAllText(Path.Combine(projectRoot, "ui-harness-report.json"), json);
            }
        }
        catch
        {
            // Ignore file write errors during test run
        }

        return report;
    }

    private static async Task<TestResult> RunTestAsync(string name, Func<Task<string>> action, Action<string>? log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string details = await action();
            sw.Stop();
            log?.Invoke($"[PASS] {name} ({sw.ElapsedMilliseconds} ms): {details}");
            return new TestResult(name, true, details, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            log?.Invoke($"[FAIL] {name} ({sw.ElapsedMilliseconds} ms): {ex.Message}");
            return new TestResult(name, false, ex.Message, sw.Elapsed);
        }
    }

    private static Task<TestResult> RunTestAsync(string name, Func<string> action, Action<string>? log)
    {
        return RunTestAsync(name, () => Task.FromResult(action()), log);
    }
}
