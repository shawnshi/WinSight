using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinSight.MediaMetadata.Tests;

internal static class HostCases
{
    internal static async Task Run(Run run, string host, Synthetic f)
    {
        foreach (var expected in f.Metadata)
            await run.Case("host/inspect/" + Path.GetFileName(expected.SourcePath), async dir =>
            {
                string request = Request(dir, new { inputPath = expected.SourcePath, kind = expected.Kind });
                var result = await Child.Run(host, ["inspect-media", request], Path.Combine(dir, "cli"));
                Verify.That(result.Code == 0, result.Error); Metadata(result.Output, expected);
                Verify.That(Directory.GetFiles(dir).All(p => !p.EndsWith(".wav") && !p.EndsWith(".mp4")), "Inspect created media artifact");
            });
        foreach (var interval in f.Intervals)
            await run.Case("host/soundtrack/" + (interval.Start == .75 ? "middle" : "EOF"), async dir =>
            {
                var result = await Decode(host, dir, f.VideoPath, interval.Start, interval.End);
                Verify.That(result.Code == 0, result.Error); DecodeResponse(result.Output, f.VideoPath, interval, f.Metadata[2].DurationSeconds);
                string wave = Path.Combine(dir, "o", "audio.wav"); Wave.SamePhysicalTimeline(wave, interval.Reference); Wave.Markers(Wave.Read(wave), interval.Start);
            });
        await run.Case("host/no-audio-is-explicit-error", async dir =>
        {
            var result = await Decode(host, dir, f.SilentPath, .5, 1.5);
            Verify.That(result.Code != 0 && !string.IsNullOrWhiteSpace(result.Error) && string.IsNullOrWhiteSpace(result.Output), "Silent extraction must fail, never return empty success");
        });
        await run.Case("host/corrupt-is-not-no-audio", async dir =>
        {
            string corrupt = Path.Combine(dir, "bad.mp4"); File.WriteAllText(corrupt, "Not a native media container");
            var result = await Child.Run(host, ["inspect-media", Request(dir, new { inputPath = corrupt, kind = "video" })], Path.Combine(dir, "cli"));
            Verify.That(result.Code != 0 && !string.IsNullOrWhiteSpace(result.Error) && string.IsNullOrWhiteSpace(result.Output), "Parse failure mislabeled no-audio metadata");
        });
        await run.Case("host/unsupported-audio-codec-not-empty", async dir =>
        {
            string path = Path.Combine(dir, "bad.wav"); File.WriteAllText(path, "unsupported codec bytes");
            var result = await Child.Run(host, ["inspect-media", Request(dir, new { inputPath = path, kind = "audio" })], Path.Combine(dir, "cli"));
            Verify.That(result.Code != 0 && string.IsNullOrWhiteSpace(result.Output) && !string.IsNullOrWhiteSpace(result.Error), "Unsupported codec fabricated audio metadata");
        });
        await run.Case("host/reparse-source-ancestor", async dir =>
        {
            string target = Path.Combine(dir, "t"), link = Path.Combine(dir, "l"); Directory.CreateDirectory(target);
            string source = Path.Combine(target, "s.wav"); File.Copy(f.WavePath, source); Junction.Create(link, target);
            try
            {
                var result = await Child.Run(host, ["inspect-media", Request(dir, new { inputPath = Path.Combine(link, "s.wav"), kind = "audio" })], Path.Combine(dir, "cli"));
                Verify.That(result.Code != 0 && string.IsNullOrWhiteSpace(result.Output) && File.Exists(source), "Reparse ancestor accepted or target changed");
            }
            finally { Directory.Delete(link); }
        });
        await run.Case("host/strict-inspect/invalid-UTF8", async dir =>
        {
            string path = Path.Combine(dir, "i.json"); File.WriteAllBytes(path, [0xc3, 0x28]);
            var result = await Child.Run(host, ["inspect-media", path], Path.Combine(dir, "cli")); Verify.That(result.Code != 0 && string.IsNullOrWhiteSpace(result.Output), "Invalid UTF8 accepted");
        });
        var valid = JsonSerializer.Serialize(new { inputPath = f.VideoPath, kind = "video" });
        var invalid = new Dictionary<string, string>
        {
            ["unknown"] = valid[..^1] + ",\"outputDirectory\":\"x\"}", ["duplicate"] = valid[..^1] + ",\"kind\":\"video\"}",
            ["wrongcase"] = valid.Replace("\"kind\"", "\"Kind\""), ["nullkind"] = valid.Replace("\"video\"", "null"),
            ["missingkind"] = JsonSerializer.Serialize(new { inputPath = f.VideoPath }), ["missingpath"] = "{\"kind\":\"video\"}",
            ["nullpath"] = "{\"inputPath\":null,\"kind\":\"video\"}", ["numberpath"] = "{\"inputPath\":7,\"kind\":\"video\"}",
            ["numberkind"] = valid.Replace("\"video\"", "7"), ["framekind"] = valid.Replace("\"video\"", "\"frame\""),
            ["array"] = "[]", ["malformed"] = "{bad", ["trailing"] = valid + "{}", ["huge"] = new string(' ', 65537) + valid,
            ["relative"] = JsonSerializer.Serialize(new { inputPath = "relative.mp4", kind = "video" }),
            ["device"] = JsonSerializer.Serialize(new { inputPath = @"\\.\NUL", kind = "video" }),
            ["missingfile"] = JsonSerializer.Serialize(new { inputPath = Path.Combine(Path.GetDirectoryName(f.VideoPath)!, "missing.mp4"), kind = "video" }),
            ["directory"] = JsonSerializer.Serialize(new { inputPath = Path.GetDirectoryName(f.VideoPath), kind = "video" })
        };
        foreach (var pair in invalid)
            await run.Case("host/strict-inspect/" + pair.Key, async dir =>
            {
                string path = Path.Combine(dir, "i.json"); File.WriteAllText(path, pair.Value);
                var result = await Child.Run(host, ["inspect-media", path], Path.Combine(dir, "cli"));
                Verify.That(result.Code != 0 && string.IsNullOrWhiteSpace(result.Output), "Invalid request accepted: " + pair.Key);
            });
        foreach (var command in new[] { "inspect-media", "extract-soundtrack", "run-arbitrary-command" })
            await run.Case("host/closed-CLI/" + command, async dir =>
            {
                string path = Request(dir, new { inputPath = f.VideoPath, kind = "video" });
                var result = await Child.Run(host, [command, path, "--extra"], Path.Combine(dir, "cli")); Verify.That(result.Code != 0, "Extra args accepted");
                result = await Child.Run(host, [command], Path.Combine(dir, "missing")); Verify.That(result.Code != 0, "Missing request arg accepted");
                if (command == "run-arbitrary-command")
                {
                    result = await Child.Run(host, [command, path], Path.Combine(dir, "unknown")); Verify.That(result.Code != 0, "Unknown command accepted");
                }
            });
        foreach (string mutation in new[] { "kind", "null-end", "missing-end", "extra", "duplicate", "interval", "null-start", "null-path", "bad-edge", "path" })
            await run.Case("host/strict-soundtrack/" + mutation, async dir =>
            {
                var node = new JsonObject { ["inputPath"] = f.VideoPath, ["outputDirectory"] = Path.Combine(dir, "o"), ["kind"] = "audio", ["startSeconds"] = .75, ["endSeconds"] = 2.25, ["maxEdge"] = 512 };
                switch (mutation)
                {
                    case "kind": node["kind"] = "frame"; break;
                    case "null-end": node["endSeconds"] = null; break;
                    case "missing-end": node.Remove("endSeconds"); break;
                    case "extra": node["codec"] = "arbitrary"; break;
                    case "interval": node["endSeconds"] = .5; break;
                    case "null-start": node["startSeconds"] = null; break;
                    case "null-path": node["inputPath"] = null; break;
                    case "bad-edge": node["maxEdge"] = 0; break;
                    case "path": node["outputDirectory"] = "relative"; break;
                }
                string json = node.ToJsonString(); if (mutation == "duplicate") json = json[..^1] + ",\"kind\":\"audio\"}";
                string path = Path.Combine(dir, "d.json"); File.WriteAllText(path, json);
                var result = await Child.Run(host, ["extract-soundtrack", path], Path.Combine(dir, "cli"));
                Verify.That(result.Code != 0 && string.IsNullOrWhiteSpace(result.Output), "Invalid decode request accepted");
            });
    }
    internal static string Request(string dir, object value)
    { string path = Path.Combine(dir, "i.json"); File.WriteAllText(path, JsonSerializer.Serialize(value)); return path; }
    internal static Task<CommandResult> Decode(string host, string dir, string source, double start, double end) => Child.Run(host,
        ["extract-soundtrack", Request(dir, new { inputPath = source, outputDirectory = Path.Combine(dir, "o"), kind = "audio", startSeconds = start, endSeconds = end, maxEdge = 512 })], Path.Combine(dir, "cli"));
    internal static void Metadata(string json, NativeInfo expected)
    {
        Verify.That(json.TrimEnd('\r', '\n').IndexOfAny(['\r', '\n']) < 0, "Metadata must be exactly one line");
        using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
        string[] fields = ["protocol", "sourcePath", "kind", "durationSeconds", "hasAudio", "audioTrackCount", "width", "height"];
        Verify.That(r.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields.Order()), "Exact metadata field set");
        Verify.That(r.GetProperty("protocol").GetInt32() == 1 && r.GetProperty("sourcePath").GetString() == expected.SourcePath && r.GetProperty("kind").GetString() == expected.Kind, "Protocol/source/kind identity mismatch");
        Verify.That(Math.Abs(r.GetProperty("durationSeconds").GetDouble() - expected.DurationSeconds) < .000001 && r.GetProperty("hasAudio").GetBoolean() == expected.HasAudio && r.GetProperty("width").GetInt32() == expected.Width && r.GetProperty("height").GetInt32() == expected.Height, "Native metadata differs (not hardcoded construction duration)");
        var count = r.GetProperty("audioTrackCount"); Verify.That(expected.AudioTrackCount is null ? count.ValueKind == JsonValueKind.Null : count.GetInt32() == expected.AudioTrackCount, "Audio null vs native video count mismatch");
    }
    internal static void DecodeResponse(string json, string source, Interval interval, double duration)
    {
        using var doc = JsonDocument.Parse(json); var r = doc.RootElement; var a = r.GetProperty("artifacts");
        Verify.That(r.GetProperty("protocol").GetInt32() == 1 && r.GetProperty("sourcePath").GetString() == source && r.GetProperty("kind").GetString() == "audio" && Math.Abs(r.GetProperty("sourceDurationSeconds").GetDouble() - duration) < .000001 && a.GetArrayLength() == 1, "Soundtrack source/result identity");
        var item = a[0]; Verify.That(item.GetProperty("relativePath").GetString() == "audio.wav" && item.GetProperty("modality").GetString() == "audio" && item.GetProperty("startSeconds").GetDouble() == interval.Start && item.GetProperty("endSeconds").GetDouble() == interval.End, "Soundtrack original video timeline lost");
        Verify.That(item.GetProperty("sampleRate").GetInt32() == 16000 && item.GetProperty("channels").GetInt32() == 1 && item.GetProperty("bitsPerSample").GetInt32() == 16 && item.GetProperty("width").ValueKind == JsonValueKind.Null && item.GetProperty("height").ValueKind == JsonValueKind.Null, "Exact N1 nullable audio format");
    }
}
