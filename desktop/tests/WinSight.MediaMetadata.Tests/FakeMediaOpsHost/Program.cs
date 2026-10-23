using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinSight.MediaMetadata.Tests;

if (args.Length == 2 && args[0] == "--owned-child")
{
    Owned.Record(args[1], "child"); await Task.Delay(Timeout.Infinite); return 0;
}
if (args.Length != 2 || args[0] is not ("inspect-media" or "extract-soundtrack") || !Path.IsPathFullyQualified(args[1])) return 64;
try
{
    var bytes = File.ReadAllBytes(args[1]); Verify.That(bytes.Length <= 65536, "Request too large");
    using var doc = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes)); var r = doc.RootElement;
    bool inspect = args[0] == "inspect-media";
    string[] fields = inspect ? ["inputPath", "kind"] : ["inputPath", "outputDirectory", "kind", "startSeconds", "endSeconds", "maxEdge"];
    var names = r.EnumerateObject().Select(p => p.Name).ToArray();
    Verify.That(names.Length == fields.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length && !names.Except(fields).Any(), "Exact request field set required");
    var source = r.GetProperty("inputPath").GetString()!; var kind = r.GetProperty("kind").GetString();
    Verify.That(Path.IsPathFullyQualified(source) && File.Exists(source) && (inspect ? kind is "audio" or "video" : kind == "audio"), "Invalid source/kind");
    string mode = Path.GetFileNameWithoutExtension(source);
    Owned.Record(source, "worker");
    if (mode is "descendant" or "parentexit")
    {
        var si = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        si.ArgumentList.Add("--owned-child"); si.ArgumentList.Add(source);
        using var child = Process.Start(si) ?? throw new IOException("Could not start owned child"); await Owned.Wait(source, "child");
        if (mode == "parentexit") return 29;
    }
    if (mode is "hang" or "descendant") await Task.Delay(Timeout.Infinite);
    if (mode == "nativeerror" && !inspect) { Console.Error.WriteLine("Synthetic decoder failure with audio present"); return 27; }
    if (mode == "malformed") { Console.WriteLine("{broken"); return 0; }
    if (mode is "utf8" or "stderrutf8") { (mode == "utf8" ? Console.OpenStandardOutput() : Console.OpenStandardError()).Write(new byte[] { 0xc3, 0x28 }); return 0; }
    if (mode == "oversize") { Console.WriteLine(new string('x', 70000)); return 0; }
    JsonObject response;
    if (inspect)
    {
        response = new() { ["protocol"] = 1, ["sourcePath"] = source, ["kind"] = kind, ["durationSeconds"] = 4d,
            ["hasAudio"] = kind == "audio" || mode != "silent", ["audioTrackCount"] = kind == "audio" ? null : mode == "silent" ? 0 : 1,
            ["width"] = kind == "video" ? 256 : 0, ["height"] = kind == "video" ? 144 : 0 };
        switch (mode)
        {
            case "badbool": response["hasAudio"] = "true"; break;
            case "nullbool": response["hasAudio"] = null; break;
            case "badcount": response["audioTrackCount"] = -1; break;
            case "fractioncount": response["audioTrackCount"] = 1.5; break;
            case "countnull": response["audioTrackCount"] = null; break;
            case "stringcount": response["audioTrackCount"] = "1"; break;
            case "zeroaudio": response["audioTrackCount"] = 0; break;
            case "contradiction": response["hasAudio"] = false; break;
            case "audiocount": response["audioTrackCount"] = 1; break;
            case "baddims": response["width"] = 0; break;
            case "audiodims": response["width"] = 256; break;
            case "badheight": response["height"] = -1; break;
            case "fractionwidth": response["width"] = .5; break;
            case "nullwidth": response["width"] = null; break;
        }
    }
    else
    {
        var start = r.GetProperty("startSeconds").GetDouble(); var end = r.GetProperty("endSeconds").GetDouble();
        var output = r.GetProperty("outputDirectory").GetString()!;
        // N1 admits nullable end/maxEdge only where its strict decode contract permits them.
        var edge = r.GetProperty("maxEdge"); Verify.That(edge.ValueKind == JsonValueKind.Null || edge.GetInt32() is >= 64 and <= 1024, "Invalid maxEdge");
        Verify.That(double.IsFinite(start) && start >= 0 && double.IsFinite(end) && end > start && end - start <= 30 && Path.IsPathFullyQualified(output) && !Directory.Exists(output), "Invalid audio interval/output");
        Directory.CreateDirectory(output); Wave.Write(Path.Combine(output, "audio.wav"), end - start, start);
        var artifact = new JsonObject { ["relativePath"] = "audio.wav", ["modality"] = "audio", ["startSeconds"] = start, ["endSeconds"] = end,
            ["sampleRate"] = 16000, ["channels"] = 1, ["bitsPerSample"] = 16, ["width"] = null, ["height"] = null };
        response = new() { ["protocol"] = 1, ["sourcePath"] = source, ["kind"] = "audio", ["sourceDurationSeconds"] = 4d, ["artifacts"] = new JsonArray(artifact) };
        if (mode == "badinterval") artifact["startSeconds"] = 0;
        if (mode == "badformat") File.WriteAllText(Path.Combine(output, "audio.wav"), "not audio");
        if (mode == "escape") artifact["relativePath"] = "../audio.wav";
    }
    string durationKey = inspect ? "durationSeconds" : "sourceDurationSeconds";
    switch (mode)
    {
        case "badsource": response["sourcePath"] = source + ".other"; break;
        case "badkind": response["kind"] = "frame"; break;
        case "nullsource": response["sourcePath"] = null; break;
        case "nullkind": response["kind"] = null; break;
        case "badduration": response[durationKey] = -1; break;
        case "zeroduration": response[durationKey] = 0; break;
        case "nullduration": response[durationKey] = null; break;
        case "stringduration": response[durationKey] = "4"; break;
        case "version": response["protocol"] = 2; break;
        case "extra": response["unexpected"] = true; break;
        case "missing": response.Remove("kind"); break;
    }
    string json = response.ToJsonString();
    if (mode == "duplicate") json = json[..^1] + ",\"kind\":\"" + kind + "\"}";
    if (mode == "overflowduration") json = json.Replace("\"" + durationKey + "\":4", "\"" + durationKey + "\":1e999");
    if (mode == "wrongcase") json = json.Replace("\"kind\":", "\"Kind\":");
    Console.WriteLine(json); if (mode == "multiline") Console.WriteLine(json);
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 65; }
