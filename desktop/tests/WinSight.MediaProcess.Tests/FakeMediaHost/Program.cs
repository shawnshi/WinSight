using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinSight.MediaProcess.Tests;

if (args.Length == 2 && args[0] == "--descendant")
{
    OwnedProcesses.Record(args[1], "descendant");
    await Task.Delay(Timeout.Infinite);
    return 0;
}
if (args.Length != 2 || args[0] != "extract-media" || !Path.IsPathFullyQualified(args[1]))
{
    Console.Error.WriteLine("Expected exactly extract-media ABS_REQUEST_JSON.");
    return 64;
}
try
{
    var bytes = File.ReadAllBytes(args[1]);
    if (bytes.Length > 65536) throw new InvalidDataException("Request too large.");
    using var request = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes));
    var properties = request.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    string[] allowed = ["inputPath", "outputDirectory", "kind", "startSeconds", "endSeconds", "maxEdge"];
    if (properties.Distinct().Count() != properties.Length || properties.Except(allowed).Any()) throw new InvalidDataException("Unknown/duplicate request field.");
    var root = request.RootElement;
    var source = root.GetProperty("inputPath").GetString()!;
    var output = root.GetProperty("outputDirectory").GetString()!;
    var kind = root.GetProperty("kind").GetString()!;
    var start = root.GetProperty("startSeconds").GetDouble();
    var end = root.TryGetProperty("endSeconds", out var endNode) ? endNode.GetDouble() : (double?)null;
    var maxEdge = root.TryGetProperty("maxEdge", out var edgeNode) ? edgeNode.GetInt32() : 512;
    if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(output) || !File.Exists(source) ||
        Directory.Exists(output) || File.Exists(output) || !double.IsFinite(start) || start < 0 ||
        (kind != "audio" && kind != "frame") || (kind == "audio" && (end is null || !double.IsFinite(end.Value) || end <= start || end - start > 30)) ||
        (kind == "frame" && (end is not null || maxEdge < 64 || maxEdge > 1024))) throw new InvalidDataException("N1 request contract.");
    OwnedProcesses.Record(source, "main");
    var mode = Path.GetFileNameWithoutExtension(source).Split('_')[0];
    if (mode == "unconfirmed")
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "audio.wav"), "owned-worker-partial-output");
    }
    if (mode is "descendant" or "parentexit" or "unconfirmed")
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        info.ArgumentList.Add("--descendant"); info.ArgumentList.Add(source);
        using var child = Process.Start(info) ?? throw new IOException("Unable to start owned descendant.");
        await OwnedProcesses.WaitForRoles(source, "descendant");
        if (mode == "parentexit") return 23;
    }
    if (mode is "hang" or "uncooperative" or "descendant" or "unconfirmed") await Task.Delay(Timeout.Infinite);
    if (mode == "nonzero") { Console.Error.WriteLine("Synthetic native failure."); return 27; }
    if (mode is "stdoutflood" or "stderrflood")
    {
        var stream = mode == "stdoutflood" ? Console.OpenStandardOutput() : Console.OpenStandardError();
        var block = Enumerable.Repeat((byte)'x', 8192).ToArray();
        while (true) await stream.WriteAsync(block);
    }
    if (mode is "utf8" or "stderrutf8")
    {
        var stream = mode == "utf8" ? Console.OpenStandardOutput() : Console.OpenStandardError();
        stream.Write(new byte[] { 0xc3, 0x28 }); stream.Flush();
        return 0;
    }
    if (mode == "malformed") { Console.WriteLine("{broken"); return 0; }
    Directory.CreateDirectory(output);
    var relative = kind == "audio" ? "audio.wav" : "frame.png";
    var artifactPath = Path.Combine(output, relative);
    if (kind == "audio") FixtureMedia.Wave(artifactPath, end!.Value - start); else FixtureMedia.Png(artifactPath);
    if (FixtureMedia.PngFaultModes.Contains(mode))
    {
        if (kind != "frame") throw new InvalidDataException("PNG fault fixtures require a FRAME request.");
        FixtureMedia.CorruptPng(artifactPath, mode);
    }
    if (mode == "badformat") File.WriteAllText(artifactPath, "not media");
    if (mode == "bigfile") { using var file = new FileStream(artifactPath, FileMode.Open, FileAccess.Write); file.SetLength(128L * 1024 * 1024); }
    if (mode == "reparse")
    {
        File.Delete(artifactPath);
        PinAttacks.CreateArtifactJunction(artifactPath, source + ".reparse-target");
        File.WriteAllText(Path.Combine(source + ".reparse-target", "target.keep"), "outside-artifact-target");
    }
    if (mode == "unknownfile") File.WriteAllText(Path.Combine(output, "unknown.keep"), "must not recursively delete");
    var artifact = new JsonObject { ["relativePath"] = relative, ["modality"] = kind == "audio" ? "audio" : "image",
        ["startSeconds"] = start, ["endSeconds"] = end,
        ["sampleRate"] = null, ["channels"] = null, ["bitsPerSample"] = null,
        ["width"] = null, ["height"] = null };
    if (kind == "audio") { artifact["sampleRate"] = 16000; artifact["channels"] = 1; artifact["bitsPerSample"] = 16; }
    else { artifact["width"] = 2; artifact["height"] = 2; }
    var result = new JsonObject { ["protocol"] = 1, ["sourcePath"] = source, ["kind"] = kind,
        ["sourceDurationSeconds"] = 4, ["artifacts"] = new JsonArray(artifact) };
    switch (mode)
    {
        case "escape": artifact["relativePath"] = "../source.wav"; break;
        case "absolute": artifact["relativePath"] = source; break;
        case "source": result["sourcePath"] = source + ".other"; break;
        case "kind": result["kind"] = kind == "audio" ? "frame" : "audio"; break;
        case "modality": artifact["modality"] = "text"; break;
        case "start": artifact["startSeconds"] = start + 0.25; break;
        case "end": artifact["endSeconds"] = end is null ? 1 : end + 0.25; break;
        case "count": result["artifacts"]!.AsArray().Add(artifact.DeepClone()); break;
        case "empty": result["artifacts"] = new JsonArray(); break;
        case "version": result["protocol"] = 2; break;
        case "size": artifact["sampleRate"] = 48000; artifact["width"] = 4096; break;
        case "duration": result["sourceDurationSeconds"] = -1; break;
    }
    var json = result.ToJsonString();
    if (mode == "oversize") json = json.TrimEnd('}') + ",\"padding\":\"" + new string('x', 70000) + "\"}";
    Console.WriteLine(json);
    if (mode == "multiline") Console.WriteLine(json);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 65;
}
