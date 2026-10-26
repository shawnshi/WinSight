using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

// Test-only executable: no ONNX references, model reads, or production inclusion.
if (args is ["--tree-leaf"]) { await Task.Delay(Timeout.InfiniteTimeSpan); return 0; }
// The host takes no provider-install switch: acquisition lives in the isolated Foundry host.
if (args.Length is not 7 || args[0] != "--stdio" || args[1] != "--models" || args[3] != "--manifest"
    || args[5] != "--device" || args[6] is not ("cpu" or "auto" or "discrete-gpu" or "npu" or "gpu")) return 91;
const bool installAllowed = false;
var requestedProvider = args[6] switch
{
    "cpu" => "CPUExecutionProvider",
    "auto" => "device-auto-with-real-model-gates",
    "discrete-gpu" => "device-discrete-gpu-with-real-model-gates",
    "npu" => "device-npu-with-real-model-gates",
    _ => "device-gpu-with-real-model-gates"
};
var directory = args[2];
var mode = Path.GetFileName(directory).Split(' ')[0];
if (args[4] != Path.Combine(directory, "manifest with spaces &.json")) return 92;
var audit = Path.Combine(directory, "audit.jsonl");
var first = !File.Exists(audit);
void Log(object item) => File.AppendAllText(audit, JsonSerializer.Serialize(item) + "\n");
void Emit(object item) { Console.WriteLine(JsonSerializer.Serialize(item)); Console.Out.Flush(); }
Log(new { started = Environment.ProcessId, device = args[6], downloadsAllowed = installAllowed });
if ((mode is "startup-timeout" or "startup-cancel") && first) await Task.Delay(10000);
if (mode == "ready-false") { Emit(new { ready = false, protocol = 1, error = "synthetic missing models" }); return 1; }
if (mode == "stderr") { await Console.Error.WriteAsync(new string('e', 3 * 1024 * 1024)); await Console.Error.FlushAsync(); }
object Info(bool changed = false) => new {
    model = mode == "model" ? "wrong" : "onnx-community/embeddinggemma-2-ONNX",
    revision = "daa72c51243991dfcaf9f9137d2c573d8f7790c0", runtime = "windows-ml-ort-1.24.3", dtype = "q4",
    dim = 768, text_only = mode == "mode", profile_id = mode == "profile" ? "bad" : new string(changed || (mode == "restart-profile" && !first) ? 'b' : 'a', 64),
    validation = "unverified-source-integration", requested_provider = requestedProvider,
    provider_downloads_allowed = mode == "downloads" || installAllowed, backend_status = mode == "nostatus" ? (object?)null : new { epoch = first ? 0 : 1, providers = Array.Empty<object>(), graphs = Array.Empty<object>() }, sessions_loaded = new { text = false, vision = false, audio = false }
};
object Capabilities() => new { text = mode != "capabilities", image = mode != "text-only", audio = mode != "text-only" };
Emit(new { ready = true, protocol = mode == "protocol" ? 2 : 1, info = Info(), capabilities = Capabilities() });
if (mode == "no-read" && first) await Task.Delay(10000);
while (await Console.In.ReadLineAsync() is { } line)
{
    using var request = JsonDocument.Parse(line);
    var root = request.RootElement;
    var id = root.GetProperty("id").GetInt64();
    var op = root.GetProperty("op").GetString();
    Log(new { pid = Environment.ProcessId, request = root.Clone() });
    if (op == "quit")
    {
        if (mode == "ignore-quit") await Task.Delay(10000);
        Emit(new { id, ok = true });
        return 0;
    }
    if (op == "info")
    {
        if (mode == "tree")
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--tree-leaf");
            using var leaf = Process.Start(start)!;
            Log(new { leaf = leaf.Id });
        }
        Emit(new { id, ok = true, protocol = 1, info = Info(mode == "profile-drift"), capabilities = Capabilities() });
        continue;
    }
    if ((mode is "request-timeout" or "cancel" or "restart-profile" or "status-drift") && first) await Task.Delay(10000);
    if (mode == "delay") await Task.Delay(250);
    if (mode == "hold") await Task.Delay(10000);
    if (mode == "invalid") { Console.WriteLine("{garbage}"); continue; }
    if (mode == "invalid-utf8") { await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xFF, 10 }); continue; }
    if (mode == "oversize") { Console.WriteLine(new string('x', 1024 * 1024 + 1)); continue; }
    if (mode == "duplicate") { Console.WriteLine($"{{\"id\":{id},\"ok\":true,\"ok\":false}}"); continue; }
    if (mode == "nested-duplicate") { Console.WriteLine($"{{\"id\":{id},\"ok\":true,\"extra\":{{\"x\":1,\"x\":2}}}}"); continue; }
    if (mode == "truncated") { Console.Write("{\"ok\":"); Console.Out.Flush(); return 0; }
    if (mode == "eof") return 0;
    if (mode == "request-error") { Emit(new { id, ok = false, error = "synthetic request rejected" }); continue; }
    var inputs = root.GetProperty("inputs");
    var count = inputs.GetArrayLength();
    var errors = new string?[count];
    var bytes = new byte[count * 768 * sizeof(float)];
    for (var row = 0; row < count; row++)
    {
        if (mode == "partial" && row == 1) { errors[row] = "IOException: synthetic unreadable item"; continue; }
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(row * 768 * 4), mode switch {
            "zero" => 0, "norm" => 0.1f, "nan" => float.NaN, "infinity" => float.PositiveInfinity, _ => 0.6f });
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(row * 768 * 4 + 4), mode == "zero" ? 0 : 0.8f);
    }
    var encoded = Convert.ToBase64String(bytes);
    if (mode == "base64") encoded = "!" + encoded[1..];
    if (mode == "base64-space") encoded = " " + encoded[1..];
    if (mode == "byte-count") encoded = encoded[..^4];
    Emit(new { id = mode == "id" ? id + 1 : id, ok = mode != "status" && errors.All(error => error is null),
        dim = mode == "dimension" ? 767 : 768, count = mode == "count" ? count + 1 : count, vectors = encoded,
        errors = mode == "error-count" ? [] : errors, performance_profile_id = (string?)null });
}
return 0;
