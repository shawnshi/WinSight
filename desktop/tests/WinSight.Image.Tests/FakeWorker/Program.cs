using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinSight.Shared;

Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--leaf", var leafPid])
{
    await PublishPidAsync(leafPid);
    await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
}
if (args is not ["inspect-image", var requestPath]) return 21;
using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath));
string source = request.RootElement.GetProperty("inputPath").GetString()!;
if (request.RootElement.GetProperty("kind").GetString() != "image") return 22;
string mode = Path.GetFileNameWithoutExtension(source);
if (mode is "hang" or "tree")
{
    await PublishPidAsync(source + ".pid");
    if (mode == "tree")
    {
        var child = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        child.ArgumentList.Add("--leaf"); child.ArgumentList.Add(source + ".child.pid");
        Process.Start(child)?.Dispose();
    }
    await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
}
if (mode == "oversize") { Console.Write(new string('x', 70000)); return 0; }
if (mode == "utf8") { Console.OpenStandardOutput().Write([0xff, 0x0a]); return 0; }
if (mode == "malformed") { Console.WriteLine("not json"); return 0; }
var exif = new Dictionary<string, string> { ["cameraModel"] = "SyntheticCamera" };
if (mode == "gps") exif["gpsLatitude"] = "SYNTHETIC GPS MUST BE REJECTED";
var response = new
{
    protocol = mode == "protocol" ? 4 : 3,
    ocrEngine = mode == "engine" ? "WindowsOcr" : OcrIdentity.Engine,
    ocrEngineVersion = mode == "engine-version" ? "4.2.0" : OcrIdentity.EngineVersion,
    ocrModelProfile = mode == "model-profile" ? new string('0', 64) : OcrIdentity.ModelProfile,
    sourcePath = mode == "wrong-source" ? source + ".different" : source,
    width = mode == "dimensions" ? 40000 : 720, height = 180, orientation = 1, frameCount = 1,
    exif, ocrText = mode == "text-limit" ? new string('x', 9000) : "SYNTHETIC OCR 758392",
    ocrLanguages = mode == "no-language" ? Array.Empty<string>() : new[] { "zh-Hans-CN", "en-US" },
    ocrWidth = 720, ocrHeight = 180, state = "done", detail = (string?)null,
    ocrRegions = new[] { new { line = 0, start = 0, length = 9, left = 0.1, top = 0.1, width = 0.5, height = 0.2 } }, ocrRegionsTruncated = false
};
var json = JsonSerializer.Serialize(response);
if (mode == "duplicate") json = json.Replace("\"protocol\":3", "\"protocol\":3,\"protocol\":3", StringComparison.Ordinal);
Console.WriteLine(json);
return 0;

static async Task PublishPidAsync(string path)
{
    // A newline terminates the complete PID; the consumer permits this writer while waiting.
    await File.WriteAllTextAsync(path, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
}
