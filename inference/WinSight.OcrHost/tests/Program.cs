using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

if (args is ["--parent-launch-retention", var lifecycleReport])
{
    Environment.ExitCode = await ParentLaunchLifecycle.RunAsync(lifecycleReport);
    return;
}

string worker = Path.GetFullPath(args[0]);
string root = Path.GetDirectoryName(worker)!;
string task = Path.GetFullPath(args[1]);
bool baseline = args.Contains("--baseline");
Directory.CreateDirectory(task);
string classifier = Path.Combine(root, "models/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
string dict = Path.Combine(root, "models/v6/ppocrv6_dict.txt");
using var options = RapidOcr.GetDefaultSessionOptions(1);
using var crop = new SKBitmap(160, 48);
crop.Erase(SKColors.White);
var evidence = new List<object>();
void Stage(string name, Action action)
{
    Exception? failure = null;
    try { action(); } catch (Exception ex) { failure = ex; }
    bool disposedRunFailure = name == "classifier-disposed-session-run-failure" && failure is NullReferenceException &&
        failure.StackTrace?.Contains("Microsoft.ML.OnnxRuntime.InferenceSession.RunImpl", StringComparison.Ordinal) == true;
    bool pass = baseline ? failure is null : failure is OnnxRuntimeException || disposedRunFailure;
    evidence.Add(new { name, pass, baseline, exception = failure?.GetType().Name });
    if (!pass) throw new Exception($"Stage assertion failed: {name}: {failure}");
}
using (var recognizer = new TextRecognizer())
{
    // A real initialized ORT classifier session rejects recognizer's 48px tensor (expects 80px).
    recognizer.InitModel(classifier, dict, options);
    Stage("recognizer-initialized-run-shape-failure", () => recognizer.GetTextLine(crop));
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    try { recognizer.GetTextLine(crop, cancel.Token); throw new Exception("Cancellation swallowed"); }
    catch (OperationCanceledException) { evidence.Add(new { name = "recognizer-cancellation", pass = true }); }
}
using (var detector = new TextDetector())
{
    detector.InitModel(classifier, options);
    Stage("detector-initialized-run-shape-failure", () => detector.GetTextBoxes(crop,
        new ScaleParam(160, 48, 64, 64), .5f, .3f, 1.6f));
}
using (var angle = new TextClassifier())
{
    angle.InitModel(classifier, options);
    angle.Dispose();
    Stage("classifier-disposed-session-run-failure", () => angle.GetAngle(crop));
}
if (baseline)
{
    Console.WriteLine(JsonSerializer.Serialize(evidence));
    return;
}
string image = Path.Combine(task, "input.png");
string request = Path.Combine(task, "request.json");
string Request(string path) => JsonSerializer.Serialize(new { protocol = 2, imagePath = path });
void Case(string name, byte[] bytes, bool success, string? expected = null)
{
    File.WriteAllBytes(request, bytes);
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(worker); start.ArgumentList.Add("inspect-ocr"); start.ArgumentList.Add(request);
    using var process = Process.Start(start)!;
    var outputRead = process.StandardOutput.ReadToEndAsync();
    var errorRead = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(45000)) { process.Kill(true); throw new Exception("worker timeout"); }
    string output = outputRead.GetAwaiter().GetResult();
    string error = errorRead.GetAwaiter().GetResult();
    File.WriteAllText(Path.Combine(task, name + ".stdout.json"), output);
    File.WriteAllText(Path.Combine(task, name + ".stderr.txt"), error);
    bool pass = success ? process.ExitCode == 0 && error.Length == 0 : process.ExitCode != 0 && output.Length == 0 && Encoding.UTF8.GetByteCount(error) <= 1024;
    if (success)
    {
        using var doc = JsonDocument.Parse(output);
        var r = doc.RootElement;
        string text = r.GetProperty("text").GetString()!;
        pass &= r.GetProperty("protocol").GetInt32() == 2 && r.GetProperty("engine").GetString() == "RapidOcrNet" &&
                r.GetProperty("engineVersion").GetString() == "4.2.0-winsight.1" && !r.GetProperty("truncated").GetBoolean() &&
                r.GetProperty("modelProfile").GetString()!.Length == 64 && text.Length <= 8000 && Encoding.UTF8.GetByteCount(output) <= 128 * 1024;
        if (expected is not null) pass &= expected.Length == 0 ? text.Length == 0 : text.Contains(expected, StringComparison.OrdinalIgnoreCase);
        evidence.Add(new { name, pass, exit = process.ExitCode, text });
    }
    else evidence.Add(new { name, pass, exit = process.ExitCode, error = error.Trim() });
    if (!pass) throw new Exception($"Case failed {name}: exit {process.ExitCode}; {output}; {error}");
}
void Bad(string name, string json) => Case(name, Encoding.UTF8.GetBytes(json), false);
Bad("malformed", "{");
Bad("duplicate", "{\"protocol\":2,\"protocol\":2,\"imagePath\":\"C:/none\"}");
Bad("unknown", "{\"protocol\":2,\"imagePath\":\"C:/none\",\"extra\":0}");
Bad("wrong-protocol", "{\"protocol\":3,\"imagePath\":\"C:/none\"}");
Case("utf8", [0xff, 0xfe], false);
Case("request-size", new byte[65537], false);
Bad("relative", Request("input.png"));
Bad("unc", Request("\\\\server\\share\\image.png"));
Bad("device", Request("\\\\?\\C:\\image.png"));
Bad("outside-task", Request(Path.Combine(root, "outside.png")));
File.WriteAllBytes(image, [1, 2, 3]);
Bad("invalid-image", Request(image));
using (var large = new FileStream(image, FileMode.Create)) large.SetLength(32L * 1024 * 1024 + 1);
Bad("image-size", Request(image));
using (var huge = new SKBitmap(2601, 1))
using (var data = huge.Encode(SKEncodedImageFormat.Png, 100))
using (var file = File.Create(image)) data.SaveTo(file);
Bad("image-dimensions", Request(image));
string fixtures = Path.GetFullPath(args[2]);
foreach (var fixture in new[] { "english", "chinese", "blank" })
{
    File.Copy(Path.Combine(fixtures, fixture + ".png"), image, true);
    Case(fixture, Encoding.UTF8.GetBytes(Request(image)), true, fixture == "blank" ? "" : fixture == "english" ? "ORCHID INVOICE 473829" : "图片文字识别测试");
}
string manifest = Path.Combine(root, "model-manifest.json");
byte[] original = File.ReadAllBytes(manifest);
try { File.WriteAllText(manifest, "{}"); Bad("manifest-mismatch", Request(image)); }
finally { File.WriteAllBytes(manifest, original); }
string rec = Path.Combine(root, "models/v6/PP-OCRv6_rec_small.onnx");
using (var file = new FileStream(rec, FileMode.Open, FileAccess.ReadWrite)) { int b = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(b ^ 1)); }
try { Bad("model-mismatch", Request(image)); }
finally { using var file = new FileStream(rec, FileMode.Open, FileAccess.ReadWrite); int b = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(b ^ 1)); }
Console.WriteLine(JsonSerializer.Serialize(evidence));
