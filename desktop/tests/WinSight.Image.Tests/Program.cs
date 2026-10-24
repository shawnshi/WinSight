using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Media;

if (args is not [var actualHost, var fakeHost, var fixtures, var evidence])
    throw new ArgumentException("Supply actual model-free Host, controlled fake apphost, synthetic fixtures and a NEW absolute evidence directory.");
foreach (var path in args) if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("All test arguments must be absolute.");
if (Directory.Exists(evidence)) throw new ArgumentException("Evidence directory must be new.");
Directory.CreateDirectory(evidence);
var results = new List<object>(); int passed = 0, failed = 0;
async Task Test(string name, Func<Task> action)
{
    var watch = Stopwatch.StartNew();
    try { await action(); passed++; results.Add(new { name, passed = true, seconds = watch.Elapsed.TotalSeconds }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; results.Add(new { name, passed = false, error = error.ToString(), seconds = watch.Elapsed.TotalSeconds }); Console.WriteLine("FAIL " + name + ": " + error); }
}
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
NativeMediaProcessClient Client(string host, string label, int timeoutMs = 10000)
{
    var workspace = Path.Combine(evidence, label); Directory.CreateDirectory(workspace);
    return new NativeMediaProcessClient(new NativeMediaProcessOptions
    {
        HostExecutable = host, WorkspaceRoot = workspace,
        RequestTimeout = TimeSpan.FromMilliseconds(timeoutMs), TerminationTimeout = TimeSpan.FromSeconds(5)
    });
}
void Empty(string workspace) => Check(!Directory.EnumerateFileSystemEntries(Path.Combine(evidence, workspace)).Any(), "Native task or pins leaked: " + workspace);
string FakeInput(string mode)
{
    var path = Path.Combine(evidence, mode + ".png"); File.WriteAllText(path, "SYNTHETIC FAKE INPUT; no real decoder may accept this"); return path;
}
static async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(10, timeout.Token);
}
static bool TryReadPublishedPid(string path, out int pid)
{
    pid = 0;
    try
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(input);
        var text = reader.ReadToEnd();
        return text.EndsWith("\n", StringComparison.Ordinal)
            && int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pid)
            && pid > 0;
    }
    catch (IOException) { return false; } // Only the task-owned readiness marker may still be opening/writing.
}
static bool Exited(int pid)
{
    try { using var process = Process.GetProcessById(pid); return process.HasExited; }
    catch (ArgumentException) { return true; }
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

await Test("native/unpackaged-English-OCR-and-snapshot-cleanup", async () =>
{
    await using var client = Client(actualHost, "native-english");
    var info = await client.InspectImageAsync(Path.Combine(fixtures, "english.png"));
    Check(info.State == "done" && info.OcrText.Contains("ORCHID") && info.OcrText.Contains("473829"), "Actual native OCR did not read the known English image.");
    Check(info.Width == 900 && info.Height == 220 && info.OcrLanguages.Contains("en-US"), "OCR dimensions/language drift.");
    File.WriteAllText(Path.Combine(evidence, "english-response.json"), JsonSerializer.Serialize(info));
    Empty("native-english");
});
await Test("native/Chinese-OCR-uses-pinned-bilingual-model", async () =>
{
    await using var client = Client(actualHost, "native-chinese");
    var info = await client.InspectImageAsync(Path.Combine(fixtures, "chinese.png"));
    var text = Regex.Replace(info.OcrText, @"\s+", "");
    Check(info.State == "done" && text.Contains("图片") && text.Contains("文字") && text.Contains("584237") && info.OcrLanguages.Contains("zh-Hans-CN"), "Actual Chinese OCR did not read the known glyphs.");
    File.WriteAllText(Path.Combine(evidence, "chinese-response.json"), JsonSerializer.Serialize(info));
    Empty("native-chinese");
});
for (int value = 1; value <= 8; value++)
{
    int orientation = value;
    await Test("native/EXIF-orientation-" + orientation + "-and-GPS-exclusion", async () =>
    {
        string label = "orientation-" + orientation;
        await using var client = Client(actualHost, label);
        var info = await client.InspectImageAsync(Path.Combine(fixtures, label + ".jpg"));
        var text = Regex.Replace(info.OcrText, @"\s+", "");
        Check(info.State == "done" && info.Orientation == orientation, "EXIF orientation/status drift.");
        Check(info.OcrWidth == 900 && info.OcrHeight == 220 && text.Contains("ORCHID") && text.Contains("473829"), "RespectExifOrientation did not restore upright OCR pixels.");
        Check(info.Exif.GetValueOrDefault("cameraManufacturer") == "TestMaker" && info.Exif.GetValueOrDefault("cameraModel") == "CameraFixture", "Known camera tags were not extracted.");
        Check(info.Exif.ContainsKey("dateTaken") && info.Exif.ContainsKey("isoSpeed") && info.Exif.ContainsKey("fNumber"), "Known photo date/exposure tags missing.");
        var serialized = JsonSerializer.Serialize(info);
        Check(!serialized.Contains("gps", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("latitude", StringComparison.OrdinalIgnoreCase), "GPS escaped the whitelist.");
        File.WriteAllText(Path.Combine(evidence, label + ".json"), serialized);
        Empty(label);
    });
}
await Test("native/blank-is-verified-blank-not-unavailable", async () =>
{
    await using var client = Client(actualHost, "blank");
    var info = await client.InspectImageAsync(Path.Combine(fixtures, "blank.png"));
    Check(info.State == "done" && info.Detail == null && info.OcrText.Length == 0 && info.OcrLanguages.Count > 0, "Verified blank image became fake unavailable or an OCR failure.");
    var extraction = NativeImageExtraction.Create(info, "blank");
    Check(extraction.State == "done" && extraction.Segments.Count == 1 && extraction.Segments[0].Modality == "image", "Blank OCR should retain only the real visual input.");
});
await Test("native/large-image-OCR-is-downscaled-to-2600-edge", async () =>
{
    await using var client = Client(actualHost, "wide");
    var info = await client.InspectImageAsync(Path.Combine(fixtures, "wide.png"));
    Check(info.State == "done" && info.Width == 6000 && info.Height == 600 && info.OcrWidth == 2600 && info.OcrHeight == 260
        && info.OcrText.Contains("473829"), "OCR scale bound or known text was lost.");
    Empty("wide");
});
await Test("native/OCR-text-limit-is-explicit-partial-not-complete", async () =>
{
    await using var client = Client(actualHost, "dense", 30000);
    var info = await client.InspectImageAsync(Path.Combine(fixtures, "dense.png"));
    Check(info.State == "partial" && info.OcrText.Length is > 6000 and <= 8000 && info.Detail!.Contains("8000-character"), "OCR text truncation was not bounded/explicit.");
    File.WriteAllText(Path.Combine(evidence, "dense-summary.json"), JsonSerializer.Serialize(new { info.State, characters = info.OcrText.Length, info.Detail }));
    Empty("dense");
});
await Test("native/40MP-decode-limit-is-enforced-before-OCR", async () =>
{
    await using var client = Client(actualHost, "oversized-image");
    try { await client.InspectImageAsync(Path.Combine(fixtures, "over-40mp.png")); throw new InvalidOperationException("45 MP image was accepted."); }
    catch (IOException error) { Check(error.Message.Contains("40 MP"), "Image rejection did not prove the dimension guard."); }
    Empty("oversized-image");
});
await Test("native/multiframe-remains-explicitly-partial", async () =>
{
    await using var client = Client(actualHost, "animation");
    var info = await client.InspectImageAsync(Path.Combine(fixtures, "animated.gif"));
    Check(info.FrameCount == 2 && info.State == "partial" && info.Detail!.Contains("first image frame"), "Animation coverage was silently marked complete.");
});
await Test("native/corrupt-image-fails-and-releases-pins", async () =>
{
    await using var client = Client(actualHost, "corrupt");
    await Throws<IOException>(() => client.InspectImageAsync(Path.Combine(fixtures, "corrupt.png")));
    Empty("corrupt");
});
await Test("extraction/OCR-and-EXIF-use-normal-text-segments-and-revision", async () =>
{
    await using var client = Client(actualHost, "extraction");
    var extractor = new ContentExtractor(client);
    var result = await extractor.ExtractAsync(Path.Combine(fixtures, "orientation-1.jpg"), false);
    Check(result.State == "done" && result.Segments.Any(s => s.Modality == "image")
        && result.Segments.Any(s => s.Modality == "text" && s.Excerpt!.Contains("473829"))
        && result.Segments.Any(s => s.Modality == "text" && s.Excerpt!.Contains("CameraFixture")), "Image/text extraction contract incomplete.");
    Check(NativeImageExtraction.IsCurrent(result.Info) && !NativeImageExtraction.IsCurrent("{}"), "Image extraction cache revision is not explicit.");
    Check(!result.Info!.Contains("gps", StringComparison.OrdinalIgnoreCase), "GPS persisted in file info.");
});
await Test("extraction/no-helper-is-truthful-partial", async () =>
{
    var result = await new ContentExtractor().ExtractAsync(Path.Combine(fixtures, "english.png"), false);
    Check(result.State == "partial" && result.Detail!.Contains("not configured"), "Missing native inspector was hidden as complete OCR.");
});
await Test("extraction/Chinese-phrase-spacing-does-not-change-Latin-words-or-lines", () =>
{
    var info = new NativeImageInspection(Path.Combine(fixtures, "chinese.png"), 900, 220, 1, 1, new Dictionary<string, string>(),
        "图 片 文 字 识 别\nORCHID INVOICE 584237", ["zh-Hans-CN"], 900, 220, "done", null);
    var result = NativeImageExtraction.Create(info, "chinese");
    Check(result.Segments.Any(s => s.Excerpt == "图片文字识别\nORCHID INVOICE 584237"), "CJK compaction broke a phrase, Latin spacing or line boundary.");
    return Task.CompletedTask;
});
await Test("extraction/cache-revision-rejects-nonobject-info", () =>
{
    Check(!NativeImageExtraction.IsCurrent("[]") && !NativeImageExtraction.IsCurrent("null") && !NativeImageExtraction.IsCurrent("{"), "Non-object/malformed legacy metadata must trigger refresh, not stop the crawl.");
    return Task.CompletedTask;
});
await Test("extraction/surrogate-safe-bounded-text-chunks", () =>
{
    var text = string.Concat(Enumerable.Repeat("x😀", 2000));
    var info = new NativeImageInspection(Path.Combine(fixtures, "english.png"), 900, 220, 1, 1, new Dictionary<string, string>(), text, ["en-US"], 900, 220, "done", null);
    var result = NativeImageExtraction.Create(info, "bounded");
    foreach (var segment in result.Segments.Where(s => s.Modality == "text"))
        Check(segment.Excerpt!.Length <= 1800 && !char.IsLowSurrogate(segment.Excerpt[0]) && !char.IsHighSurrogate(segment.Excerpt[^1]), "Chunk split a surrogate pair or exceeded its bound.");
    return Task.CompletedTask;
});
await Test("extraction/private-key-material-does-not-enter-image-or-text-index", () =>
{
    var info = new NativeImageInspection(Path.Combine(fixtures, "english.png"), 900, 220, 1, 1, new Dictionary<string, string>(),
        "-----BEGIN PRIVATE KEY----- SYNTHETIC MARKER ONLY", ["en-US"], 900, 220, "done", null);
    var result = NativeImageExtraction.Create(info, "sensitive");
    Check(result.State == "skipped" && result.Segments.Count == 0, "Private-key OCR material was retained.");
    return Task.CompletedTask;
});
await Test("controlled/success-cleans-task-and-keeps-original-source-identity", async () =>
{
    var source = FakeInput("normal");
    await using var client = Client(fakeHost, "fake-normal");
    var info = await client.InspectImageAsync(source);
    Check(info.State == "done" && info.SourcePath == source && info.OcrText.Contains("758392"), "Controlled protocol did not exercise actual client.");
    Empty("fake-normal");
    File.Move(source, source + ".released");
});
foreach (var mode in new[] { "malformed", "duplicate", "protocol", "wrong-source", "engine", "engine-version", "model-profile", "gps", "no-language", "dimensions", "text-limit", "utf8", "oversize" })
{
    await Test("controlled/reject-" + mode + "-without-no-data", async () =>
    {
        var source = FakeInput(mode);
        string label = "fake-" + mode;
        await using var client = Client(fakeHost, label);
        await Throws<InvalidDataException>(() => client.InspectImageAsync(source));
        Empty(label);
        File.Move(source, source + ".released");
    });
}
await Test("controlled/cancellation-kills-owned-tree-before-return", async () =>
{
    var source = FakeInput("tree");
    await using var client = Client(fakeHost, "cancel-tree");
    using var cancellation = new CancellationTokenSource();
    var pending = client.InspectImageAsync(source, cancellation.Token);
    int parent = 0, child = 0;
    await Until(() => TryReadPublishedPid(source + ".pid", out parent) && TryReadPublishedPid(source + ".child.pid", out child));
    cancellation.Cancel(); await Throws<OperationCanceledException>(() => pending);
    Check(Exited(parent) && Exited(child), "Canceled image operation returned before its physical tree settled.");
    Empty("cancel-tree"); File.Move(source, source + ".released");
});
await Test("controlled/deadline-and-pre-cancel-do-not-leak-task-or-source-pins", async () =>
{
    var source = FakeInput("hang");
    await using var client = Client(fakeHost, "deadline", 500);
    await Throws<TimeoutException>(() => client.InspectImageAsync(source));
    Empty("deadline"); File.Move(source, source + ".released");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    await Throws<OperationCanceledException>(() => client.InspectImageAsync(Path.Combine(evidence, "never-read.png"), cancellation.Token));
    Empty("deadline");
});
File.WriteAllText(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new
{
    passed, failed, assertions = results,
    assemblies = new[] { typeof(NativeMediaProcessClient).Assembly, typeof(ExtractionResult).Assembly }.Select(a => new { a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }),
    scope = "Actual native OCR/EXIF plus actual bounded client and controlled failures; all inputs are synthetic."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT passed={passed} failed={failed}; evidence={evidence}");
return failed == 0 ? 0 : 1;
