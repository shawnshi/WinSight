using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--leaf", var leafPid])
{
    await File.WriteAllTextAsync(leafPid, Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
}
if (args is not ["inspect-image", var requestPath]) return 21;
using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath));
string source = request.RootElement.GetProperty("inputPath").GetString()!;
if (request.RootElement.GetProperty("kind").GetString() != "image") return 22;
string mode = Path.GetFileNameWithoutExtension(source);
if (mode is "hang" or "tree")
{
    await File.WriteAllTextAsync(source + ".pid", Environment.ProcessId.ToString());
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
    protocol = mode == "protocol" ? 2 : 1,
    sourcePath = mode == "wrong-source" ? source + ".different" : source,
    width = mode == "dimensions" ? 40000 : 720, height = 180, orientation = 1, frameCount = 1,
    exif, ocrText = mode == "text-limit" ? new string('x', 9000) : "SYNTHETIC OCR 758392",
    ocrLanguages = mode == "no-language" ? Array.Empty<string>() : new[] { "en-US" },
    ocrWidth = 720, ocrHeight = 180, state = "done", detail = (string?)null
};
var json = JsonSerializer.Serialize(response);
if (mode == "duplicate") json = json.Replace("\"protocol\":1", "\"protocol\":1,\"protocol\":1", StringComparison.Ordinal);
Console.WriteLine(json);
return 0;
