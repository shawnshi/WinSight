using System.Text.Json;
using System.Text.Json.Serialization;
using WinSight.Core;
using WinSight.Infrastructure.Runtime;

var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("WinSight native migration preview (not a drop-in legacy CLI replacement).\n"
            + "Commands: configure, status, search, index, model-info\n"
            + "All require --data-dir ABSOLUTE_PATH. configure: --root PATH [--root PATH] [--code-root PATH]\n"
            + "[--host EXE --models DIR --manifest JSON]. search: --query TEXT [--mode keyword|hybrid|semantic]\n"
            + "[--root PATH --kind KIND --limit N]. index: --root PATH [--code] [--force].\n"
            + "Only explicit index/model-info/semantic requests start the inference host; no personal roots are inferred.");
        return 0;
    }
    var command = args[0];
    var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i++)
    {
        var key = args[i];
        if (!key.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument {key}.");
        var value = key is "--code" or "--force" ? "true"
            : ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {key}.");
        if (!options.TryGetValue(key, out var values)) options[key] = values = [];
        values.Add(value);
    }
    var allowed = command switch
    {
        "configure" => new[] { "--data-dir", "--root", "--code-root", "--host", "--models", "--manifest" },
        "search" => ["--data-dir", "--query", "--mode", "--root", "--kind", "--limit"],
        "index" => ["--data-dir", "--root", "--code", "--force"],
        "status" or "model-info" => ["--data-dir"],
        _ => throw new ArgumentException($"Unknown command {command}.")
    };
    foreach (var key in options.Keys)
        if (!allowed.Contains(key)) throw new ArgumentException($"Unsupported option {key} for {command}.");
    var data = One("--data-dir") ?? throw new ArgumentException("--data-dir is required; the preview never discovers private settings.");
    if (!Path.IsPathFullyQualified(data)) throw new ArgumentException("--data-dir must be absolute.");
    await using var backend = await DesktopBackend.CreateAsync(new DesktopOptions(data, EnableBackgroundIndexing: false), stop.Token);
    object response;
    switch (command)
    {
        case "configure":
            var settings = await backend.LoadSettingsAsync(stop.Token);
            if (options.TryGetValue("--root", out var roots)) settings.Folders = roots;
            if (options.TryGetValue("--code-root", out var codeRoots)) settings.CodeFolders = codeRoots;
            settings.InferenceExecutable = One("--host") ?? settings.InferenceExecutable;
            settings.ModelDirectory = One("--models") ?? settings.ModelDirectory;
            settings.ManifestPath = One("--manifest") ?? settings.ManifestPath;
            settings.SetUp = true;
            await backend.SaveSettingsAsync(settings, stop.Token);
            response = new { configured = true, backgroundIndexing = settings.BackgroundIndexingEnabled };
            break;
        case "status": response = await backend.GetStatusAsync(stop.Token); break;
        case "search":
            var query = One("--query") ?? throw new ArgumentException("--query is required.");
            var modeValue = One("--mode") ?? "keyword";
            var mode = modeValue switch
            {
                "keyword" => SearchMode.Keyword, "hybrid" => SearchMode.Hybrid, "semantic" => SearchMode.Semantic,
                _ => throw new ArgumentException("--mode must be keyword, hybrid or semantic.")
            };
            var limit = 50;
            if (One("--limit") is { } text && !int.TryParse(text, out limit))
                throw new ArgumentException("--limit must be an integer.");
            if (limit is < 1 or > 200) throw new ArgumentException("--limit must be in 1..200.");
            response = await backend.SearchAsync(new SearchRequest(query, limit, mode, One("--kind"), One("--root")), stop.Token);
            break;
        case "index":
            if (!options.TryGetValue("--root", out var indexRoots)) throw new ArgumentException("--root is required.");
            await backend.IndexAsync(new IndexRequest(indexRoots, Code: options.ContainsKey("--code"),
                Force: options.ContainsKey("--force")), cancellationToken: stop.Token);
            response = await backend.GetStatusAsync(stop.Token);
            break;
        case "model-info": response = await backend.GetEmbeddingInfoAsync(stop.Token); break;
        default: throw new ArgumentException("Unsupported command.");
    }
    Console.WriteLine(JsonSerializer.Serialize(response, json));
    return 0;

    string? One(string key)
    {
        if (!options.TryGetValue(key, out var values)) return null;
        if (values.Count != 1) throw new ArgumentException($"{key} may only be supplied once.");
        return values[0];
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "Canceled", canceled = true }, json));
    return 130;
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
    or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or JsonException or System.Data.Common.DbException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.GetType().Name, detail = error.Message }, json));
    return 2;
}
