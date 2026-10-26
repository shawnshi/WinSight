// Bridge fixture: asserts the tool contract over stdio, then proves the pipe transport returns identical hits.
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Extraction;
using WinSight.Infrastructure.Indexing;
using WinSight.Infrastructure.Storage;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Use a fresh absolute task directory.");
var root = args[0];
if (Directory.Exists(root)) throw new ArgumentException("Task directory must be new.");
Directory.CreateDirectory(root);
var docs = Path.Combine(root, "docs");
Directory.CreateDirectory(docs);
var corpus = new (string Name, string Body, string Query)[]
{
    ("中医保政策.txt", "医保 结算 报销 比例 调整 通知 2026 年 执行", "医保报销"),
    ("b合同评审.txt", "采购 合同 评审 流程 与 审批 权限 说明", "合同评审"),
    ("c错误码.txt", "错误码 E1001 表示 接口 超时 请 重试", "E1001"),
};
foreach (var (name, body, _) in corpus) await File.WriteAllTextAsync(Path.Combine(docs, name), body, new UTF8Encoding(false));
var database = Path.Combine(root, "index.sqlite");
await using (var repository = new SqliteIndexRepository(database))
{
    await repository.InitializeAsync();
    await repository.SetMetaAsync("profile_id", "bridge-fixture-profile");
    await repository.SetMetaAsync(IndexRebuildCoordinator.LexiconSchemaKey, "2");
    await using var embeddings = new FixtureEmbeddings();
    await new IndexingService(repository, new ContentExtractor(), embeddings).IndexAsync(new IndexRequest([docs]));
}
var hashBefore = Hash(database);
var bridge = FindBridge();
var checks = new List<(string Name, bool Pass, string? Detail)>();
void Check(string name, bool pass, string? detail = null) => checks.Add((name, pass, detail));

string[] Signature(JsonElement reply) =>
    Hits(reply).Select(h => Path.GetFileName(h.GetProperty("path").GetString()!) + "@" + Math.Round(h.GetProperty("score").GetDouble(), 4)).ToArray();

var stdioSignature = Array.Empty<string>();
await using (var stdio = new StdioBridge(bridge, database))
{
    await stdio.Call("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "fixture", version = "1" } });
    var names = (await stdio.Call("tools/list", new { })).GetProperty("result").GetProperty("tools")
        .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
    Check("stdio publishes four tools", names.SequenceEqual(new[] { "search_files", "read_file_context", "get_index_status", "index_directory" }), string.Join(",", names));
    foreach (var entry in corpus)
    {
        var hits = Hits(await stdio.Tool("search_files", new { query = entry.Query, topK = 5 }));
        Check("stdio recalls " + entry.Name, hits.Any(h => Path.GetFileName(h.GetProperty("path").GetString()!) == entry.Name), hits.Length.ToString());
    }
    var first = Hits(await stdio.Tool("search_files", new { query = corpus[0].Query, topK = 3 }))[0].GetProperty("path").GetString()!;
    Check("stdio reads a hit back", Code(await stdio.Tool("read_file_context", new { path = first, startLine = 1, lineCount = 5 })) is null);
    Check("stdio refuses an unindexed path", Code(await stdio.Tool("read_file_context", new { path = Path.Combine(root, "absent.txt") })) == "excluded_file");
    var status = (await stdio.Tool("get_index_status", new { })).GetProperty("result").GetProperty("status");
    Check("stdio status counts files", status.GetProperty("fileCount").GetInt32() == corpus.Length);
    Check("stdio status hides the database path", !status.ToString().Contains("index.sqlite", StringComparison.OrdinalIgnoreCase));
    Check("stdio reports http disabled", status.GetProperty("http").GetString() == "disabled");
    Check("stdio accepts a maintenance request", Code(await stdio.Tool("index_directory", new { root = docs })) is null);
    Check("stdio rejects an oversized query", Code(await stdio.Tool("search_files", new { query = new string('x', 5000) })) == "validation_error");
    Check("stdio rejects an unknown tool", Code(await stdio.Call("tools/call", new { name = "nope", arguments = new { } })) is not null);
    Check("stdio stdout carried only JSON", stdio.Output.All(line => line.Trim().Length == 0 || Parses(line)));
    stdioSignature = Signature(await stdio.Tool("search_files", new { query = corpus[0].Query, topK = 3 }));
}

var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
var pipeName = "winsight-mcp-fixture-" + Guid.NewGuid().ToString("N");
var pipeStart = new ProcessStartInfo(bridge)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    ArgumentList = { "--index", database, "--pipe", pipeName, "--pipe-token-env", "WINSIGHT_MCP_FIXTURE_TOKEN" },
};
pipeStart.Environment["WINSIGHT_MCP_FIXTURE_TOKEN"] = token;
using var pipeProcess = Process.Start(pipeStart)!;
pipeProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("[bridge] " + e.Data); };
pipeProcess.BeginErrorReadLine();
using (var wrong = Connect(pipeName))
{
    Frame(wrong, "not-the-token");
    Check("pipe refuses a wrong token", Category(Read(wrong)) == "unauthorized", Category(Read(wrong)));
}
using (var client = Connect(pipeName))
{
    Frame(client, token);
    Check("pipe authenticates the session token", Read(client)?.Contains("\"authenticated\":true", StringComparison.Ordinal) == true);
    Frame(client, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "search_files", arguments = new { query = corpus[0].Query, topK = 3 } } }));
    var reply = Read(client)!;
    using var document = JsonDocument.Parse(reply);
    var pipeSignature = Signature(document.RootElement);
    Check("pipe returns the same hits as stdio", pipeSignature.SequenceEqual(stdioSignature), string.Join("|", pipeSignature) + " vs " + string.Join("|", stdioSignature));
    Frame(client, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "index_directory", arguments = new { root = docs } } }));
    using var maintenance = JsonDocument.Parse(Read(client)!);
    Check("pipe accepts a maintenance request", Code(maintenance.RootElement) is null, Code(maintenance.RootElement));
}
pipeProcess.Kill(true);

// --- loopback HTTP ---
const int httpPort = 18791;
var httpStart = new ProcessStartInfo(bridge)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    ArgumentList = { "--index", database, "--http-port", httpPort.ToString(), "--pipe-token-env", "WINSIGHT_MCP_FIXTURE_TOKEN" },
};
httpStart.Environment["WINSIGHT_MCP_FIXTURE_TOKEN"] = token;
using var httpProcess = Process.Start(httpStart)!;
httpProcess.ErrorDataReceived += (_, _) => { };
httpProcess.BeginErrorReadLine();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var endpoint = new Uri("http://127.0.0.1:" + httpPort + "/mcp/");
var searchBody = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "search_files", arguments = new { query = corpus[0].Query, topK = 3 } } });
var ready = DateTime.UtcNow.AddSeconds(20);
while (DateTime.UtcNow < ready)
{
    try { using var probe = await http.PostAsync(endpoint, new StringContent(searchBody, Encoding.UTF8, "application/json")); break; }
    catch (HttpRequestException) { await Task.Delay(250); }
}
using (var anonymous = await http.PostAsync(endpoint, new StringContent(searchBody, Encoding.UTF8, "application/json")))
    Check("http refuses a request without a token", (int)anonymous.StatusCode == 401, ((int)anonymous.StatusCode).ToString());
using (var foreign = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(searchBody, Encoding.UTF8, "application/json") })
{
    foreign.Headers.Add("Authorization", "Bearer " + token);
    foreign.Headers.Add("Origin", "https://evil.example");
    using var reply = await http.SendAsync(foreign);
    Check("http refuses a foreign origin", (int)reply.StatusCode == 403, ((int)reply.StatusCode).ToString());
}
using (var authorized = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(searchBody, Encoding.UTF8, "application/json") })
{
    authorized.Headers.Add("Authorization", "Bearer " + token);
    using var reply = await http.SendAsync(authorized);
    using var document = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
    var overHttp = Signature(document.RootElement);
    Check("http returns the same hits as stdio", overHttp.SequenceEqual(stdioSignature), string.Join("|", overHttp));
}
httpProcess.Kill(true);

// --- maintenance request hand-off ---
var jobReply = await Exchange(bridge, database, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = "index_directory", arguments = new { path = docs } } }));
Check("index_directory records a maintenance request", jobReply is not null && jobReply.Contains("jobId", StringComparison.Ordinal), jobReply is null ? "no reply" : jobReply[..Math.Min(140, jobReply.Length)]);
// The bridge derives its data directory from the index path, so the fixture must use the same view.
var dataDirectory = Directory.GetParent(Directory.GetParent(database)!.FullName)!.FullName;
var store = new MaintenanceJobStore(dataDirectory);
var claimed = store.ClaimNext();
Check("the request is claimable by the app", claimed is not null && claimed.Root.Length != 0, claimed?.Id ?? "none");
if (claimed is not null)
{
    await store.CompleteAsync(claimed, false, "fixture: refused by policy");
    Check("the outcome is recorded next to the job", File.Exists(Path.Combine(store.Completed, claimed.Id + ".result.json")));
}
Check("the index file is untouched", Hash(database) == hashBefore);

var failed = checks.Count(c => !c.Pass);
var result = new { scope = "synthetic corpus; both transports end to end", passed = checks.Count - failed, failed, checks = checks.Select(c => new { name = c.Name, pass = c.Pass, detail = c.Detail }) };
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Pass)) Console.WriteLine("FAIL " + check.Name + " :: " + check.Detail);
Console.WriteLine($"Bridge fixture: {result.passed}/{checks.Count} passed");
return failed == 0 ? 0 : 1;

static JsonElement[] Hits(JsonElement reply) =>
    reply.GetProperty("result").TryGetProperty("hits", out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
static string? Code(JsonElement reply)
{
    if (!reply.TryGetProperty("result", out var result)) return null;
    if (result.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)) return code.GetString();
    return result.TryGetProperty("isError", out var flag) && flag.GetBoolean() ? "isError" : null;
}
static bool Parses(string line) { try { using var _ = JsonDocument.Parse(line); return true; } catch (JsonException) { return false; } }
static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
static string? Category(string? json)
{
    if (json is null) return null;
    using var document = JsonDocument.Parse(json);
    return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("data", out var data) && data.TryGetProperty("category", out var category) ? category.GetString() : null;
}
static NamedPipeClientStream Connect(string name)
{
    var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline)
    {
        try { client.Connect(1000); return client; } catch (TimeoutException) { }
    }
    throw new TimeoutException("the pipe server never accepted a connection");
}
static void Frame(Stream stream, string text)
{
    var body = Encoding.UTF8.GetBytes(text);
    stream.Write(BitConverter.GetBytes(body.Length), 0, 4);
    stream.Write(body, 0, body.Length);
    stream.Flush();
}
static string? Read(Stream stream)
{
    var header = new byte[4];
    if (stream.Read(header, 0, 4) != 4) return null;
    var body = new byte[BitConverter.ToInt32(header, 0)];
    var read = 0;
    while (read < body.Length)
    {
        var chunk = stream.Read(body, read, body.Length - read);
        if (chunk <= 0) return null;
        read += chunk;
    }
    return Encoding.UTF8.GetString(body);
}
static async Task<string?> Exchange(string bridge, string database, string payload)
{
    var start = new ProcessStartInfo(bridge)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList = { "--index", database },
    };
    using var process = Process.Start(start)!;
    process.ErrorDataReceived += (_, _) => { };
    process.BeginErrorReadLine();
    await process.StandardInput.WriteLineAsync(payload);
    await process.StandardInput.FlushAsync();
    var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
    try { process.StandardInput.Close(); } catch (IOException) { }
    if (!process.WaitForExit(10000)) process.Kill(true);
    return line;
}

static string FindBridge()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var built = Path.Combine(directory.FullName, "desktop", "src", "WinSight.Bridge", "bin", configuration, "net11.0-windows10.0.26100.0", "win-x64", "winsight-mcp.exe");
            if (File.Exists(built)) return built;
        }
    throw new FileNotFoundException("winsight-mcp.exe was not found next to the repository.");
}

internal sealed class StdioBridge : IAsyncDisposable
{
    private readonly Process process;
    private readonly List<string> output = new();
    private int sequence;
    public IReadOnlyList<string> Output => output;

    public StdioBridge(string executable, string database)
    {
        process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "--index", database },
        })!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
    }

    public Task<JsonElement> Tool(string tool, object arguments) => Call("tools/call", new { name = tool, arguments });

    public async Task<JsonElement> Call(string method, object parameters)
    {
        var id = Interlocked.Increment(ref sequence);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await process.StandardInput.FlushAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (line is null) throw new EndOfStreamException("the bridge closed stdout");
            output.Add(line);
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id)
                    return document.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        throw new TimeoutException("no reply over stdio");
    }

    public async ValueTask DisposeAsync()
    {
        try { process.StandardInput.Close(); } catch (IOException) { }
        if (!process.WaitForExit(10000)) process.Kill(true);
        process.Dispose();
        await Task.CompletedTask;
    }
}

internal sealed class FixtureEmbeddings : IEmbeddingService
{
    private static readonly EmbeddingInfo Info = new("bridge-fixture-profile", 768, "synthetic", "fixture", "fixture", "cpu", "synthetic", ["text"]);
    public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(Info);
    public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default)
    {
        var results = new List<EmbeddingResult>(inputs.Count);
        foreach (var input in inputs)
        {
            var vector = new float[768];
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.Value ?? ""));
            for (var i = 0; i < vector.Length; i++) vector[i] = hash[i % hash.Length] / 255f - 0.5f;
            var norm = MathF.Sqrt(vector.Sum(v => v * v));
            for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
            results.Add(new EmbeddingResult(vector));
        }
        return Task.FromResult<IReadOnlyList<EmbeddingResult>>(results);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
