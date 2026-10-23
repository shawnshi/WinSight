using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinSight.MediaProcess.Tests;

var run = new TestRun(args);
uint Crc32(ReadOnlySpan<byte> data)
{
    uint crc = 0xffffffff;
    foreach (var value in data)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
    }
    return crc ^ 0xffffffff;
}
await run.Case("fixture-wav-png-video", async directory =>
{
    var wave = TestRun.Source(directory);
    FixtureMedia.CheckWave(wave, 4);
    var png = Path.Combine(directory, "fixture.png"); FixtureMedia.Png(png);
    await FixtureMedia.CheckPng(png, 64, true);
    await FixtureMedia.Video(Path.Combine(directory, "synthetic.mp4"));
    File.WriteAllText(Path.Combine(directory, "hashes.json"), JsonSerializer.Serialize(
        Directory.GetFiles(directory).ToDictionary(p => Path.GetFileName(p)!, TestRun.Hash)));
});
foreach (var kind in new[] { "audio", "frame" })
    await run.Case("fake-contract-" + kind, async directory =>
    {
        var source = TestRun.Source(directory); var original = TestRun.Hash(source);
        var output = Path.Combine(directory, "output"); var request = Path.Combine(directory, "request.json");
        var dto = new Dictionary<string, object> { ["inputPath"] = source, ["outputDirectory"] = output,
            ["kind"] = kind, ["startSeconds"] = 0, ["maxEdge"] = 64 };
        if (kind == "audio") dto["endSeconds"] = 1;
        File.WriteAllText(request, JsonSerializer.Serialize(dto));
        var child = await TestRun.Invoke(run.FakeHost, ["extract-media", request], Path.Combine(directory, "child"));
        Check.That(child.Code == 0 && child.Stderr.Length == 0, "Fake success exit/stderr");
        var text = new UTF8Encoding(false, true).GetString(child.Stdout).TrimEnd('\r', '\n');
        Check.That(!text.Contains('\n') && !text.Contains('\r'), "Exactly one JSON line");
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Check.That(root.GetProperty("protocol").GetInt32() == 1 && root.GetProperty("sourcePath").GetString() == source && root.GetProperty("kind").GetString() == kind, "N1 envelope");
        var artifacts = root.GetProperty("artifacts"); Check.That(artifacts.GetArrayLength() == 1, "Exactly one artifact");
        var item = artifacts[0];
        Check.That(item.GetProperty("modality").GetString() == (kind == "audio" ? "audio" : "image") && item.GetProperty("startSeconds").GetDouble() == 0, "Artifact modality/start");
        var path = Path.Combine(output, item.GetProperty("relativePath").GetString()!);
        if (kind == "audio") FixtureMedia.CheckWave(path, 1); else await FixtureMedia.CheckPng(path, 64, true);
        Check.That(TestRun.Hash(source) == original, "Source unchanged");
        await OwnedProcesses.AssertReaped(source, "main");
    });
foreach (var mode in FixtureMedia.PngFaultModes)
    await run.Case("fake-frame-" + mode, async directory =>
    {
        var source = TestRun.Source(directory, mode); var hash = TestRun.Hash(source);
        var output = Path.Combine(directory, "output"); var request = Path.Combine(directory, "request.json");
        File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, outputDirectory = output, kind = "frame", startSeconds = 0, maxEdge = 64 }));
        var child = await TestRun.Invoke(run.FakeHost, ["extract-media", request], Path.Combine(directory, "child"));
        Check.That(child.Code == 0 && child.Stderr.Length == 0, "Malformed PNG must still produce a successful, complete N1 frame response.");
        var text = new UTF8Encoding(false, true).GetString(child.Stdout).TrimEnd('\r', '\n');
        Check.That(!text.Contains('\n') && !text.Contains('\r'), "Exactly one response line.");
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Check.That(root.EnumerateObject().Count() == 5 && root.GetProperty("protocol").GetInt32() == 1 &&
            root.GetProperty("sourcePath").GetString() == source && root.GetProperty("kind").GetString() == "frame" &&
            root.GetProperty("sourceDurationSeconds").GetDouble() == 4 && root.GetProperty("artifacts").GetArrayLength() == 1, "Complete N1 frame envelope, not an earlier schema error.");
        var item = root.GetProperty("artifacts")[0];
        Check.That(item.EnumerateObject().Count() == 9 && item.GetProperty("relativePath").GetString() == "frame.png" &&
            item.GetProperty("modality").GetString() == "image" && item.GetProperty("startSeconds").GetDouble() == 0 &&
            item.GetProperty("endSeconds").ValueKind == JsonValueKind.Null && item.GetProperty("width").GetInt32() == 2 &&
            item.GetProperty("height").GetInt32() == 2 && new[] { "sampleRate", "channels", "bitsPerSample" }.All(p => item.GetProperty(p).ValueKind == JsonValueKind.Null), "All required frame fields, including irrelevant null audio members.");
        var control = Path.Combine(directory, "valid.png"); FixtureMedia.Png(control); await FixtureMedia.CheckPng(control, 64, true);
        var valid = File.ReadAllBytes(control); var actual = File.ReadAllBytes(Path.Combine(output, "frame.png"));
        Check.That(actual.AsSpan(0, 29).SequenceEqual(valid.AsSpan(0, 29)), "Every malformed fixture keeps valid signature, IHDR fields and dimensions.");
        if (mode != "pngihdrcrc") Check.That(actual.AsSpan(0, 33).SequenceEqual(valid.AsSpan(0, 33)), "Valid IHDR CRC must survive non-IHDR mutations.");
        switch (mode)
        {
            case "pngheader33": Check.That(actual.Length == 33, "Exact report counterexample: 33 bytes only, no IDAT or IEND."); break;
            case "pngihdrcrc":
            case "pngidatcrc":
                var differences = Enumerable.Range(0, valid.Length).Where(i => actual[i] != valid[i]).ToArray();
                Check.That(actual.Length == valid.Length && differences.Length == 1 &&
                    (mode == "pngihdrcrc" ? differences[0] == 29 : differences[0] >= valid.Length - 16 && differences[0] < valid.Length - 12), "Only the intended chunk CRC changes, not image data or schema.");
                break;
            case "pngnoidat": Check.That(actual.Length == 45 && actual.AsSpan(33).SequenceEqual(valid.AsSpan(valid.Length - 12)), "IHDR plus valid IEND only."); break;
            case "pngnoiend": Check.That(actual.AsSpan().SequenceEqual(valid.AsSpan(0, valid.Length - 12)), "Whole IDAT retained, entire IEND absent."); break;
            case "pngtruncated": Check.That(actual.AsSpan().SequenceEqual(valid.AsSpan(0, valid.Length - 15)), "IDAT claims its full extent but CRC is truncated."); break;
            case "pngtrailing": Check.That(actual.Length == valid.Length + 1 && actual.AsSpan(0, valid.Length).SequenceEqual(valid) && actual[^1] == 0x42, "Complete valid PNG plus trailing byte."); break;
            case "pngdeflate":
            case "pngzlibtruncated":
            case "pngadler":
            case "pngexcess":
                var length = BinaryPrimitives.ReadInt32BigEndian(actual.AsSpan(33, 4));
                var originalLength = BinaryPrimitives.ReadInt32BigEndian(valid.AsSpan(33, 4));
                Check.That(actual.Length == length + 57 && actual.AsSpan(37, 4).SequenceEqual("IDAT"u8) &&
                    actual.AsSpan(actual.Length - 12).SequenceEqual(valid.AsSpan(valid.Length - 12)) &&
                    BinaryPrimitives.ReadUInt32BigEndian(actual.AsSpan(41 + length, 4)) == Crc32(actual.AsSpan(37, length + 4)), "Malformed zlib is enclosed in a complete IDAT with a correct CRC and valid IEND.");
                if (mode == "pngzlibtruncated")
                    Check.That(length == originalLength - 2 && actual.AsSpan(41, length).SequenceEqual(valid.AsSpan(41, length)), "Only the last two Adler trailer bytes are truncated; PNG extents/CRC are repaired.");
                else if (mode == "pngadler")
                    Check.That(length == originalLength && actual.AsSpan(41, length - 1).SequenceEqual(valid.AsSpan(41, length - 1)) &&
                        actual[40 + length] == (valid[40 + length] ^ 1), "Only the Adler32 trailer is corrupted; deflate and PNG CRC are otherwise valid.");
                if (mode is "pngdeflate" or "pngadler")
                    await Check.Throws<InvalidDataException>(() => Task.Run(() =>
                    {
                        using var payload = new MemoryStream(actual, 41, length);
                        using var decoder = new ZLibStream(payload, CompressionMode.Decompress);
                        decoder.CopyTo(Stream.Null);
                    }));
                if (mode == "pngexcess")
                {
                    using var payload = new MemoryStream(actual, 41, length);
                    using var decoder = new ZLibStream(payload, CompressionMode.Decompress);
                    using var pixels = new MemoryStream(); decoder.CopyTo(pixels);
                    Check.That(pixels.Length == 15 && pixels.ToArray().AsSpan(0, 14).SequenceEqual(new byte[] { 0, 192, 32, 32, 192, 32, 32, 0, 192, 32, 32, 192, 32, 32 }) &&
                        pixels.ToArray()[14] == 0, "Valid zlib decompresses to one excess byte beyond the two 2x2 RGB scanlines.");
                }
                break;
        }
        Check.That(TestRun.Hash(source) == hash, "Mutation affects only the owned output PNG.");
        await OwnedProcesses.AssertReaped(source, "main");
    });
await run.Case("fake-exact-cli", async directory =>
{
    var child = await TestRun.Invoke(run.FakeHost, ["extract-media", Path.Combine(directory, "request.json"), "--hang"], Path.Combine(directory, "child"));
    Check.That(child.Code != 0 && child.Stdout.Length == 0 && child.Stderr.Length != 0, "Extra production flags rejected");
});
await run.Case("fake-frame-explicit-null-rejected", async directory =>
{
    var source = TestRun.Source(directory); var output = Path.Combine(directory, "output");
    var request = Path.Combine(directory, "request.json");
    File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, outputDirectory = output, kind = "frame", startSeconds = 0, endSeconds = (double?)null }));
    var child = await TestRun.Invoke(run.FakeHost, ["extract-media", request], Path.Combine(directory, "child"));
    Check.That(child.Code != 0 && child.Stdout.Length == 0 && !Directory.Exists(output), "Explicit null cannot satisfy N1 request");
});
await run.Case("fake-reparse-artifact-fixture", async directory =>
{
    var source = TestRun.Source(directory, "reparse"); var request = Path.Combine(directory, "request.json");
    var output = Path.Combine(directory, "output");
    File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, outputDirectory = output, kind = "audio", startSeconds = 0, endSeconds = 1 }));
    var child = await TestRun.Invoke(run.FakeHost, ["extract-media", request], Path.Combine(directory, "child"));
    Check.That(child.Code == 0 && child.Stderr.Length == 0, "Reparse fixture must reach success protocol, not fail for missing symlink privilege.");
    using var result = JsonDocument.Parse(child.Stdout);
    var path = Path.Combine(output, result.RootElement.GetProperty("artifacts")[0].GetProperty("relativePath").GetString()!);
    Check.That((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0, "Fake actually produced reparse artifact.");
    await OwnedProcesses.AssertReaped(source, "main");
});
await run.Case("junction-attack-control", directory =>
{
    PinAttacks.VerifyJunctionProbe(directory);
    return Task.CompletedTask;
});
foreach (var mode in new[] { "descendant", "parentexit", "unconfirmed" })
    await run.Case("fake-immediate-" + mode + "-pipe-fixture", async directory =>
    {
        var source = TestRun.Source(directory, mode); var request = Path.Combine(directory, "request.json");
        File.WriteAllText(request, JsonSerializer.Serialize(new { inputPath = source, outputDirectory = Path.Combine(directory, "output"), kind = "audio", startSeconds = 0, endSeconds = 1 }));
        var info = new System.Diagnostics.ProcessStartInfo(run.FakeHost) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("extract-media"); info.ArgumentList.Add(request);
        using var main = System.Diagnostics.Process.Start(info) ?? throw new IOException("Fixture start failed.");
        var identity = new OwnedIdentity(main.Id, main.StartTime.ToUniversalTime().Ticks, "fixture-main");
        File.WriteAllText(Path.Combine(directory, "main.identity.json"), JsonSerializer.Serialize(identity));
        var buffer = new byte[1]; var read = main.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory()).AsTask();
        try
        {
            await OwnedProcesses.WaitForRoles(source, "main", "descendant");
            var descendant = OwnedProcesses.Read(source).Single(p => p.Role == "descendant");
            if (mode == "unconfirmed")
                Check.That(File.ReadAllText(Path.Combine(directory, "output", "audio.wav")) == "owned-worker-partial-output", "Quarantine fixture has real partial output before cancellation.");
            if (mode == "parentexit")
            {
                await main.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
                Check.That(main.ExitCode == 23, "Intentional immediate parent exit");
            }
            else OwnedProcesses.KillRecorded(identity);
            Check.That(OwnedProcesses.Alive(descendant), "Fixture must leave descendant alive without a controlling production job.");
            Check.That(await Task.WhenAny(read, Task.Delay(250)) != read, "Descendant must actually retain pipe; closed pipe would not exercise EOF-hang risk.");
        }
        finally
        {
            OwnedProcesses.KillRecorded(identity);
            OwnedProcesses.EmergencyCleanup(source);
        }
        Check.That(await read.WaitAsync(TimeSpan.FromSeconds(3)) == 0, "Pipe closes after all recorded fixture processes are reaped.");
        await OwnedProcesses.AssertReaped(source, "main", "descendant");
    });
run.NotRun("N2-integration-and-N1-E2E", "Fixtures only: no production client compiled or called; fixture/fake passes are not lifecycle or actual-host approval.");
return run.ExitCode;
