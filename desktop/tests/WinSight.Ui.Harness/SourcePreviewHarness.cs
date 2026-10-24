using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using WinSight.App.ViewModels;
using WinSight.App.Views;
using WinSight.Core;

namespace WinSight.Ui.Harness;

internal sealed class SourcePreviewHarnessApp : HarnessApp
{
    private readonly string imagePath, output;
    internal SourcePreviewHarnessApp(string image, string output) : base(output, true)
    {
        imagePath = Path.GetFullPath(image); this.output = Path.GetFullPath(output);
        UnhandledException += (_, e) => Trace(this.output, "UnhandledException", e.Exception, e.Message);
        Trace(this.output, "preview application constructor body");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Trace(output, "OnLaunched");
        var checks = new List<object>();
        var frames = new List<object>();
        var keys = new List<object>();
        var leases = new List<PreviewFixture>();
        int failed = 0, previewCalls = 0, navigationCalls = 0;
        bool visualCompleted = false, visualMachineChecksPassed = false, keyboardActivated = false, keyboardCompleted = false;
        string phase = "setup";
        Window? window = null;
        SearchPage? page = null;
        void Check(string name, bool ok, object? detail = null)
        {
            checks.Add(new { name, ok, detail, phase });
            if (!ok) failed++;
        }
        PreviewFixture Fixture(bool current = true, bool badGeometry = false, bool canNavigate = true)
        {
            var value = new PreviewFixture(imagePath, current, badGeometry, canNavigate);
            leases.Add(value); return value;
        }
        SearchHit Hit(string name, long id = 1) => new(id, Path.Combine(output, name + ".pdf"), "pdf", .9, "hybrid",
            "Indexed context: 行一 mixed text. 第二行。", 7, 7, "page", new SourceAnchor(new("pdf-page", Page: 7), "ocr"));
        try
        {
            if (!File.Exists(Path.Combine(output, "execution-prepared"))) throw new IOException("GUI evidence reservation missing.");
            await using var backend = new SyntheticDesktopBackend(output);
            var hits = new SearchHit[] { Hit("current-source-consulting-report-with-a-long-filename", 1), Hit("second-source", 2), Hit("third-source", 3) };
            backend.SearchHandler = (_, _) => Task.FromResult(new SearchResponse(hits, TimeSpan.FromMilliseconds(1), true, null));
            backend.PreviewHandler = (_, _) => { previewCalls++; return Task.FromResult<ISourcePreview>(Fixture()); };
            backend.NavigationHandler = (_, _) => { navigationCalls++; return Task.FromResult(new SourceNavigationResult(true, "Synthetic exact-location callback; no Office opened.")); };
            page = new SearchPage(backend);
            // Capture the real opaque own host, not a transparent Page or an edited PNG.
            var captureHost = (Grid)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\" />");
            captureHost.Children.Add(page);
            window = new Window { Title = "WinSight owned visual / keyboard acceptance", Content = captureHost };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            page.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) => keys.Add(new { phase = "routed-preview-key-down", key = e.Key.ToString(), e.Handled, shiftDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) })), true);
            page.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => keys.Add(new { phase = "routed-key-down", key = e.Key.ToString(), e.Handled, shiftDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) })), true);
            window.Activate();
            await Task.Delay(180);
            var search = (TextBox)page.FindName("SearchBox");
            var list = (ListView)page.FindName("ResultsListView");
            var pane = page.PreviewControl;
            var close = (Button)pane.FindName("ClosePreview");
            var exact = (Button)pane.FindName("ExactOpen");
            var open = (Button)pane.FindName("OpenSourceFile");
            var raster = (Image)pane.FindName("RasterImage");
            var rasterArea = (FrameworkElement)pane.FindName("RasterArea");
            var scroll = (ScrollViewer)pane.FindName("PreviewScroll");
            var ocr = (ListBox)pane.FindName("OcrLines");

            async Task Until(Func<bool> predicate)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!predicate())
                {
                    if (timer.ElapsedMilliseconds > 3500) throw new TimeoutException("Owned GUI assertion condition did not become true.");
                    await Task.Delay(25);
                }
            }
            async Task Resize(double width, double height, string label)
            {
                double scale = page.XamlRoot.RasterizationScale;
                var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
                double wantedWidth = Math.Min(width, area.Width / scale - 32);
                double wantedHeight = Math.Min(height, area.Height / scale - 64);
                appWindow.ResizeClient(new SizeInt32((int)Math.Round(wantedWidth * scale), (int)Math.Round(wantedHeight * scale)));
                await Task.Delay(150); page.UpdateLayout();
                frames.Add(new { label, scale, requestedWidth = width, requestedHeight = height, expectedWidth = wantedWidth, expectedHeight = wantedHeight, page.ActualWidth, page.ActualHeight, clientWidth = appWindow.ClientSize.Width, clientHeight = appWindow.ClientSize.Height, hostBackgroundAlpha = (captureHost.Background as SolidColorBrush)?.Color.A });
                Check(label + "_logical_client_size", Math.Abs(page.ActualWidth - wantedWidth) <= 1 && Math.Abs(page.ActualHeight - wantedHeight) <= 1 && wantedWidth >= (width >= 900 ? 900 : 340));
            }
            async Task Key(VirtualKey key)
            {
                PostOwnedKey(hwnd, key);
                keys.Add(new { phase = "posted-to-own-focused-hwnd", key = key.ToString() });
                await Task.Delay(70);
            }
            async Task ShiftTab()
            {
                var target = OwnedKeyTarget(hwnd);
                uint thread = GetCurrentThreadId();
                if (GetWindowThreadProcessId(target, out _) != thread) throw new InvalidOperationException("Modifier control must run on the owned target UI thread.");
                var original = new byte[256];
                if (!GetKeyboardState(original)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var shifted = (byte[])original.Clone(); shifted[0x10] |= 0x80; shifted[0xA0] |= 0x80;
                try
                {
                    if (!SetKeyboardState(shifted)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if (!Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                        throw new InvalidOperationException("WinUI did not observe owned-thread Shift state; do not claim reverse Tab.");
                    await Key(VirtualKey.Tab);
                    keys.Add(new { phase = "own-ui-thread-shift-tab", threadId = thread, physicalHardware = false });
                }
                finally
                {
                    if (GetCurrentThreadId() != thread) throw new InvalidOperationException("UI continuation changed threads; modifier restoration cannot be claimed.");
                    if (!SetKeyboardState(original)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if (((GetKeyState(0x10) & 0x8000) != 0) != ((original[0x10] & 0x80) != 0)) throw new InvalidOperationException("Own-thread Shift restoration failed.");
                }
            }
            async Task Populate(string query)
            {
                int beforeRequest = backend.SearchCallCount;
                page.SetInitialQuery(query);
                await Until(() => backend.SearchCallCount > beforeRequest && page.ViewModel.Hits.Count == 3 && !page.ViewModel.IsLoading);
                page.ViewModel.SelectedIndex = 0; list.UpdateLayout();
            }
            void FocusResult()
            {
                list.ScrollIntoView(page.ViewModel.SelectedItem); list.UpdateLayout();
                if (list.ContainerFromIndex(page.ViewModel.SelectedIndex) is Control item) item.Focus(FocusState.Keyboard);
                else list.Focus(FocusState.Keyboard);
            }
            async Task OpenWithSpace()
            {
                FocusResult(); await Key(VirtualKey.Space);
                await Until(() => page.IsPreviewOpen && pane.HasActivePreview && pane.OverlayCount == 2);
            }
            void Contained(string label, FrameworkElement child, FrameworkElement parent)
            {
                var box = child.TransformToVisual(parent).TransformBounds(new Windows.Foundation.Rect(0, 0, child.ActualWidth, child.ActualHeight));
                Check(label, child.ActualWidth > 0 && child.ActualHeight > 0 && box.X >= -1 && box.Y >= -1 && box.Right <= parent.ActualWidth + 1 && box.Bottom <= parent.ActualHeight + 1, new { box.X, box.Y, box.Width, box.Height, parent.ActualWidth, parent.ActualHeight });
            }
            async Task Visual(string name)
            {
                page.UpdateLayout(); await Task.Delay(70);
                var actions = (Grid)pane.FindName("PreviewActions");
                Contained(name + "_exact_button_inside_actions", exact, actions);
                Contained(name + "_open_button_inside_actions", open, actions);
                Contained(name + "_close_inside_pane", close, pane);
                scroll.ChangeView(null, 0, null, true); await Task.Delay(70);
                await SnapshotAsync(pane, Path.Combine(output, name + "-actions.png"));
                pane.ShowRasterInViewport(); await Task.Delay(90); pane.UpdateLayout();
                Contained(name + "_raster_visible_in_scroll_viewport", raster, scroll);
                Check(name + "_image_decoded", raster.Source is BitmapImage { PixelWidth: 1600, PixelHeight: 900 });
                Check(name + "_overlay_visible", pane.OverlayCount == 2 && ((Canvas)pane.FindName("Overlay")).Visibility == Visibility.Visible);
                var point = pane.OverlayRectangles[0].TransformToVisual(raster).TransformPoint(new Windows.Foundation.Point());
                Check(name + "_overlay_coordinate_alignment", Math.Abs(point.X - 160) < .01 && Math.Abs(point.Y - 180) < .01);
                await SnapshotAsync(pane, Path.Combine(output, name + "-raster.png"));
                Check(name + "_opaque_theme_host", captureHost.Background is SolidColorBrush { Color.A: 255 });
                await SnapshotAsync(captureHost, Path.Combine(output, name + "-page.png"));
            }

            // Phase1 never posts a key and does not depend on foreground activation.
            phase = "visual";
            int beforeVisual = failed;
            await Resize(1000, 720, "wide");
            await Populate("layout");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                captureHost.RequestedTheme = theme; page.RequestedTheme = theme; await Task.Delay(100);
                Check(theme + "_theme_applied", page.ActualTheme == theme);
                await Resize(1000, 720, theme + "_wide");
                var previewButton = Descendants<Button>(list).First(b => AutomationProperties.GetName(b) == "Preview source");
                Invoke(previewButton); await Until(() => page.IsPreviewOpen && pane.HasActivePreview && pane.OverlayCount == 2);
                await Visual(theme.ToString().ToLowerInvariant() + "-wide");
                Invoke(close); await Until(() => !page.IsPreviewOpen);
                Check(theme + "_visual_close_button_releases_lease", leases.All(x => x.Disposed));
                Invoke(previewButton); await Until(() => page.IsPreviewOpen && pane.HasActivePreview && pane.OverlayCount == 2);
                await Resize(360, 720, theme + "_narrow");
                Check(theme + "_narrow_overlay_and_focus_cycle_configured", ((SplitView)page.FindName("PreviewHost")).DisplayMode == SplitViewDisplayMode.Overlay && pane.TabFocusNavigation == KeyboardNavigationMode.Cycle);
                await Visual(theme.ToString().ToLowerInvariant() + "-narrow");
                Invoke(close); await Until(() => !page.IsPreviewOpen);
            }
            visualCompleted = true; visualMachineChecksPassed = failed == beforeVisual;
            await File.WriteAllTextAsync(Path.Combine(output, "visual-results.json"), JsonSerializer.Serialize(new { visualCompleted, visualMachineChecksPassed, humanVisualReviewPending = true, checks, frames, postedKeyCount = keys.Count(x => JsonSerializer.Serialize(x).Contains("posted-to-own-focused-hwnd", StringComparison.Ordinal)), scope = "Actual own controls only. UIA-driven screenshots precede any keyboard activation requirement." }, new JsonSerializerOptions { WriteIndented = true }));
            Trace(output, "visual checkpoint saved; waiting for explicit user activation");

            // Phase2: never automate this confirmation button or waive the own-foreground guard.
            phase = "activation";
            await Resize(1000, 720, "keyboard_activation");
            var activation = new ContentDialog
            {
                XamlRoot = page.XamlRoot,
                Title = "截图已生成：开始键盘验收",
                Content = "请激活此测试窗口，然后点击“开始键盘验收”。开始后请勿切换窗口。只向本窗口发送定向测试消息，不操作其他窗口。",
                PrimaryButtonText = "开始键盘验收",
                CloseButtonText = "仅保留截图",
                DefaultButton = ContentDialogButton.Primary
            };
            var confirmation = activation.ShowAsync().AsTask();
            if (await Task.WhenAny(confirmation, Task.Delay(110000)) != confirmation) activation.Hide();
            var answer = await confirmation;
            await Task.Delay(180);
            keyboardActivated = answer == ContentDialogResult.Primary && GetForegroundWindow() == hwnd;
            Check("user_activated_owned_window_before_keyboard", keyboardActivated, new { confirmed = answer == ContentDialogResult.Primary, ownWindowForeground = GetForegroundWindow() == hwnd, noGlobalInput = true });
            Trace(output, keyboardActivated ? "user activation confirmed; keyboard phase" : "keyboard blocked; visual checkpoint retained");
            if (keyboardActivated)
            {
                phase = "keyboard";
                try
                {
                    await Populate("keyboard-layout");
                    int beforeEditor = previewCalls;
                    search.Focus(FocusState.Keyboard); await Key(VirtualKey.Space);
                    Check("editor_space_does_not_open_preview", !page.IsPreviewOpen && previewCalls == beforeEditor && ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), search));
                    await Until(() => !page.ViewModel.IsLoading && page.ViewModel.Hits.Count == 3);
                    await Key(VirtualKey.Down);
                    Check("down_moves_from_query_to_results", page.ViewModel.SelectedIndex == 0 && IsWithin(FocusManager.GetFocusedElement(page.XamlRoot) as DependencyObject, list), new { modelIndex = page.ViewModel.SelectedIndex, listIndex = list.SelectedIndex, focusedType = FocusManager.GetFocusedElement(page.XamlRoot)?.GetType().FullName, focusedName = (FocusManager.GetFocusedElement(page.XamlRoot) as FrameworkElement)?.Name, focusedWithinResults = IsWithin(FocusManager.GetFocusedElement(page.XamlRoot) as DependencyObject, list), searchCalls = backend.SearchCallCount });
                    await OpenWithSpace();
                    Check("space_opens_production_searchpage_preview", page.IsPreviewOpen && pane.OverlayCount == 2);
                    Check("space_moves_focus_to_close", ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), close));
                    Check("raster_and_indexed_lines", pane.OverlayCount == 2 && pane.RasterFrame.ActualWidth == 1600 && pane.RasterFrame.ActualHeight == 900);
                    var rect = pane.OverlayRectangles[0];
                    Check("pixel_geometry", Math.Abs(Canvas.GetLeft(rect) - 160) < .001 && Math.Abs(Canvas.GetTop(rect) - 180) < .001 && Math.Abs(rect.Width - 400) < .001 && Math.Abs(rect.Height - 90) < .001);
                    Check("actual_image_decode", raster.Source is BitmapImage { PixelWidth: 1600, PixelHeight: 900 });
                    ocr.SelectedIndex = 1;
                    Check("line_selection_highlight", pane.OverlayRectangles[1].StrokeThickness == 9 && pane.OverlayRectangles[0].StrokeThickness == 4);
                    var toggle = (CheckBox)pane.FindName("ShowBoxes"); toggle.IsChecked = false;
                    Check("overlay_toggle", ((Canvas)pane.FindName("Overlay")).Visibility == Visibility.Collapsed); toggle.IsChecked = true;
                    Invoke(exact); await Until(() => navigationCalls == 1);
                    Check("exact_action_uses_only_synthetic_navigation", navigationCalls == 1);
                    close.Focus(FocusState.Keyboard); await Key(VirtualKey.Tab);
                    Check("tab_moves_to_next_preview_action", !ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), close));
                    await ShiftTab();
                    Check("owned_thread_shift_tab_returns_to_close", ReferenceEquals(FocusManager.GetFocusedElement(page.XamlRoot), close));
                    await Key(VirtualKey.Escape); await Until(() => !page.IsPreviewOpen);
                    Check("escape_closes_and_restores_result_focus", IsWithin(FocusManager.GetFocusedElement(page.XamlRoot) as DependencyObject, list) && leases.All(x => x.Disposed));
                    int beforeClosedEscape = previewCalls; await Key(VirtualKey.Escape);
                    Check("escape_when_closed_preserves_query_and_selection", !page.IsPreviewOpen && previewCalls == beforeClosedEscape && page.ViewModel.SelectedIndex == 0 && page.ViewModel.Hits.Count == 3);
                    int beforeEnter = backend.LoadSettingsCount;
                    await Key(VirtualKey.Enter); await Until(() => backend.LoadSettingsCount > beforeEnter);
                    Check("enter_keeps_ordinary_open_authorization_guard", backend.LoadSettingsCount == beforeEnter + 1 && previewCalls == beforeClosedEscape && !page.IsPreviewOpen && page.ViewModel.Hits.Count == 2 && page.ViewModel.HasNotice);
                    await Populate("keyboard-next");
                    foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    {
                        captureHost.RequestedTheme = theme; page.RequestedTheme = theme; await Task.Delay(70);
                        await Resize(1000, 720, theme + "_keyboard_wide");
                        await OpenWithSpace();
                        await Resize(360, 720, theme + "_keyboard_narrow");
                        close.Focus(FocusState.Keyboard); bool cycle = true;
                        for (int i = 0; i < 8; i++)
                        {
                            await Key(VirtualKey.Tab);
                            cycle &= IsWithin(FocusManager.GetFocusedElement(page.XamlRoot) as DependencyObject, pane);
                        }
                        Check(theme + "_overlay_tab_stays_in_owned_preview", cycle);
                        close.Focus(FocusState.Keyboard); await Key(VirtualKey.Escape); await Until(() => !page.IsPreviewOpen);
                        Check(theme + "_escape_restores_focus_after_narrow_overlay", IsWithin(FocusManager.GetFocusedElement(page.XamlRoot) as DependencyObject, list));
                    }
                    await Resize(1000, 720, "keyboard_late_cancel");
                    var pendingStarted = new TaskCompletionSource(); var pendingRelease = new TaskCompletionSource(); PreviewFixture? canceledLate = null;
                    backend.PreviewHandler = async (_, _) => { pendingStarted.SetResult(); await pendingRelease.Task; canceledLate = Fixture(); return canceledLate; };
                    try
                    {
                        Invoke(Descendants<Button>(list).First(b => AutomationProperties.GetName(b) == "Preview source"));
                        await Until(() => pendingStarted.Task.IsCompleted && page.IsPreviewOpen);
                        close.Focus(FocusState.Keyboard); await Key(VirtualKey.Escape); await Until(() => !page.IsPreviewOpen);
                    }
                    finally { pendingRelease.TrySetResult(); }
                    await Until(() => canceledLate?.Disposed == true);
                    Check("escape_during_load_rejects_late_preview_and_disposes", !page.IsPreviewOpen && raster.Source is null && !pane.HasActivePreview);
                    Check("real_routed_keyboard_events_observed", keys.Count(x => JsonSerializer.Serialize(x).Contains("routed-", StringComparison.Ordinal)) >= 5);
                    keyboardCompleted = true;
                }
                catch (Exception error) { Check("keyboard_phase", false, error.ToString()); Trace(output, "keyboard failure; visual checkpoint retained", error); }
                finally
                {
                    if (page.IsPreviewOpen) { Invoke(close); await Until(() => !page.IsPreviewOpen); }
                }
            }

            // Phase3 lifecycle controls do not depend on keyboard availability.
            phase = "lifecycle";
            await Resize(1000, 720, "lifecycle");
            var started = new TaskCompletionSource(); var release = new TaskCompletionSource(); PreviewFixture? stale = null;
            backend.PreviewHandler = async (hit, _) =>
            {
                if (hit.Path.EndsWith("slow.pdf", StringComparison.Ordinal)) { started.SetResult(); await release.Task; stale = Fixture(); return stale; }
                return Fixture();
            };
            var model = page.ViewModel;
            var slow = pane.ShowAsync(model, new SearchItemViewModel(Hit("slow")));
            await started.Task; await pane.ShowAsync(model, new SearchItemViewModel(Hit("fast"))); release.SetResult(); await slow;
            Check("stale_completion_cannot_replace_fast_preview", ((TextBlock)pane.FindName("SourceTitle")).Text == "fast.pdf" && stale?.Disposed == true && pane.OverlayCount == 2);
            backend.PreviewHandler = (_, _) => Task.FromResult<ISourcePreview>(Fixture(current: false));
            await pane.ShowAsync(model, new SearchItemViewModel(Hit("changed")));
            Check("changed_source_never_shows_raster_boxes_or_exact_open", pane.OverlayCount == 0 && rasterArea.Visibility == Visibility.Collapsed && !exact.IsEnabled && raster.Source is null);
            PreviewFixture? bad = null;
            backend.PreviewHandler = (_, _) => Task.FromResult<ISourcePreview>(bad = Fixture(badGeometry: true));
            await pane.ShowAsync(model, new SearchItemViewModel(Hit("invalid-geometry")));
            Check("invalid_geometry_clears_raster_and_releases_lease", bad?.Disposed == true && raster.Source is null && pane.OverlayCount == 0 && !exact.IsEnabled && !pane.HasActivePreview);
            await pane.ClearAsync();
            Check("all_preview_leases_released", leases.All(x => x.Disposed));
            backend.PreviewHandler = (_, _) => Task.FromResult<ISourcePreview>(Fixture());
            Invoke(Descendants<Button>(list).First(b => AutomationProperties.GetName(b) == "Preview source"));
            await Until(() => page.IsPreviewOpen && pane.HasActivePreview);
            window.Content = new Grid(); await Until(() => leases.All(x => x.Disposed));
            Check("page_unload_releases_active_preview", leases.All(x => x.Disposed) && !pane.HasActivePreview);
        }
        catch (Exception error)
        {
            failed++; checks.Add(new { name = "harness", ok = false, error = error.ToString() }); Trace(output, "harness failure", error);
        }
        finally
        {
            try { if (page is not null) await page.PreviewControl.ClearAsync(); }
            catch (Exception error) { failed++; checks.Add(new { name = "cleanup", ok = false, error = error.ToString() }); }
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { success = failed == 0 && visualCompleted && keyboardCompleted, failed, visualCompleted, visualMachineChecksPassed, humanVisualReviewPending = true, keyboardActivated, keyboardCompleted, checks, frames, keys, sourcePreviewFixtureLeases = leases.Count, disposedLeases = leases.Count(x => x.Disposed), scope = "Actual production SearchPage/SourcePreviewPane; measured current-DPI client sizes and light/dark; targeted own-window posted key messages plus scoped, restored own-UI-thread Shift+Tab state (not physical keyboard). No global SendInput/SendKeys, Office, real user index or hardware-keyboard/all-DPI claim." }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = failed == 0 ? 0 : 1; window?.Close(); Exit();
        }
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject parent)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, parent)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Invoke(Button button)
    {
        if (!button.IsEnabled) throw new InvalidOperationException("Synthetic action must be enabled before UIA invocation.");
        var peer = FrameworkElementAutomationPeer.FromElement(button) ?? new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new InvalidOperationException("Owned button has no Invoke provider.");
        invoke.Invoke();
    }
    private static nint OwnedKeyTarget(nint hwnd)
    {
        if (GetForegroundWindow() != hwnd) throw new InvalidOperationException("Own GUI lost foreground; do not inject a key.");
        nint target = GetFocus();
        if (target == 0 || (target != hwnd && !IsChild(hwnd, target))) throw new InvalidOperationException("No focused child in the owned GUI.");
        GetWindowThreadProcessId(target, out uint pid);
        if (pid != Environment.ProcessId) throw new InvalidOperationException("Key target is not the captured own process.");
        return target;
    }
    private static void PostOwnedKey(nint hwnd, VirtualKey key)
    {
        var target = OwnedKeyTarget(hwnd);
        if (!PostMessageW(target, 0x0100, (nuint)key, 1) || !PostMessageW(target, 0x0101, (nuint)key, unchecked((nint)0xC0000001)))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetKeyboardState([In] byte[] state);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint hwnd, uint message, nuint wparam, nint lparam);

    internal static void Trace(string directory, string stage, Exception? error = null, string? nativeMessage = null)
    {
        if (nativeMessage is { Length: > 4096 }) nativeMessage = nativeMessage[..4096];
        try { File.AppendAllText(Path.Combine(directory, "startup-trace.log"), DateTime.UtcNow.ToString("o") + " " + stage + (error is null ? "" : " | " + error.GetType().FullName + " 0x" + error.HResult.ToString("X8") + " " + error.Message) + (string.IsNullOrWhiteSpace(nativeMessage) ? "" : " | native: " + nativeMessage) + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static async Task SnapshotAsync(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element); var pixels = await bitmap.GetPixelsAsync();
        using var random = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, random);
        var data = new byte[(int)pixels.Length]; using (var reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(data);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, data);
        await encoder.FlushAsync(); random.Seek(0); await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var input = random.AsStreamForRead(); await input.CopyToAsync(file);
    }
    private sealed class PreviewFixture(string path, bool current, bool badGeometry, bool canNavigate) : ISourcePreview
    {
        public bool Disposed { get; private set; }
        public string Excerpt => "Indexed context. 行一 mixed text. 第二行。";
        public SourceAnchor Source => new(new("pdf-page", Page: 7), "ocr", Regions: new OcrTextRegion[] { new(0, badGeometry ? double.NaN : .1, .2, .25, .1, "行一 mixed text"), new(1, .2, .5, .4, .07, "第二行") });
        // Keep a path even for stale fixtures to verify the production UI fails closed itself.
        public string? RasterPath => path;
        public int PixelWidth => 1600;
        public int PixelHeight => 900;
        public string Notice => current ? "Current source · PDF page 7 · indexed OCR text-line boxes" : "Source changed; indexed text only";
        public bool SourceCurrent => current;
        public bool CanNavigate => canNavigate;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
