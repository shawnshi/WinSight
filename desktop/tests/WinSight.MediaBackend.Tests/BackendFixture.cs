using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Storage;

namespace WinSight.MediaBackend.Tests;

internal sealed class BackendFixture : IAsyncDisposable
{
    internal readonly string Root, Input, Data, App, Audit, Models, Manifest, Host;
    internal DesktopBackend Backend = null!;
    internal AppSettings Settings = null!;
    private readonly string fake;
    internal BackendFixture(string root, string fake)
    {
        Root = root; this.fake = fake;
        Input = Path.Combine(root,"input"); Data = Path.Combine(root,"data"); App = Path.Combine(root,"app");
        Audit = Path.Combine(root,"evidence"); Models = Path.Combine(root,"models"); Manifest = Path.Combine(Models,"synthetic-manifest.json");
        Host = Path.Combine(App,"inference","CompositeHost.exe");
        foreach(string dir in new[]{Input,Data,App,Audit,Models}) Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(Models,"synthetic-weights.placeholder"), "NOT A MODEL; never load via ONNX");
        File.WriteAllText(Manifest, JsonSerializer.Serialize(new { syntheticOnly = true, note = "Only composite fake accepts this; real host pins eleven assets and rejects it" }));
    }
    internal string InstallHost(string? name = null, string mode = "ok", string? native = null)
    {
        string dir = name is null ? Path.GetDirectoryName(Host)! : Path.Combine(App,name);
        Directory.CreateDirectory(dir);
        foreach (string source in Directory.GetFiles(Path.GetDirectoryName(fake)!))
            if (Path.GetFileName(source).StartsWith("CompositeHost.",StringComparison.Ordinal)) File.Copy(source,Path.Combine(dir,Path.GetFileName(source)));
        Evidence.Write(Path.Combine(dir,"composite.json"), new HostConfig(Root,Audit,mode,native));
        return Path.Combine(dir,"CompositeHost.exe");
    }
    internal async Task Open(bool hostEnabled = true, bool setup = true, bool backgroundSetting = false, bool backgroundHost = false, string mode = "ok", string? native = null, bool fallback = false)
    {
        string host = InstallHost(mode:mode,native:native);
        if (fallback) File.Copy(host,Path.Combine(Path.GetDirectoryName(host)!,"WinSight.InferenceHost.exe"));
        // Runtimeconfig names follow the real apphost binary's embedded DLL name. Keep CompositeHost files alongside renamed exe.
        Settings = new AppSettings { SetUp = setup, BackgroundIndexingEnabled = backgroundSetting, Folders = [Input], ProcessorOnly = false, CodeFolders = [Input],
            InferenceExecutable = fallback ? null : hostEnabled ? host : Path.Combine(App,"missing.exe"), ModelDirectory = Models, ManifestPath = Manifest };
        File.WriteAllText(Path.Combine(Data,"settings.json"), JsonSerializer.Serialize(Settings,new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase }));
        Backend = await DesktopBackend.CreateAsync(new DesktopOptions(Data,App,backgroundHost));
    }
    internal string Media(string kind = "audio", MediaFixture? fixture = null, string? directory = null, string name = "source")
    {
        directory ??= Input; Directory.CreateDirectory(directory); string path = Path.Combine(directory,name+(kind=="audio"?".wav":".mp4"));
        if (kind=="audio") MediaBytes.Wave(path,(fixture??new()).Duration); else File.WriteAllBytes(path,"SYNTHETIC VIDEO: composite decoder only"u8.ToArray());
        Evidence.Write(path+".fixture.synthetic",fixture??new()); return path;
    }
    internal async Task Index(bool code = false, CancellationToken token = default, IReadOnlyList<string>? excluded = null, IReadOnlyList<string>? skip = null, long max = 32L*1024*1024)
        => await Backend.IndexAsync(new IndexRequest([Input],Excluded:excluded,SkipTypes:skip,Code:code,Force:true,MaxFileBytes:max),cancellationToken:token);
    internal async Task<IndexedFile?> Row(string path, bool code = false)
    {
        await using var repository = new SqliteIndexRepository(Path.Combine(Data,"index",code?"code.sqlite":"index.sqlite"));
        await repository.InitializeAsync(); return await repository.GetFileAsync(path);
    }
    internal async Task Snapshot()
    {
        foreach (string name in new[]{"index","code"})
        {
            string path=Path.Combine(Data,"index",name+".sqlite"); if(!File.Exists(path))continue;
            await using var repo=new SqliteIndexRepository(path); await repo.InitializeAsync();
            Evidence.Write(Path.Combine(Audit,Guid.NewGuid().ToString("N")+".sqlite.json"),new { database=path, files=await repo.ReadFilesAsync(),native_format=await repo.GetMetaAsync("native_format"),status=await repo.GetStatusAsync() });
        }
    }
    internal JsonElement[] Events() => Directory.GetFiles(Audit,"*.event.json").Select(p=>Evidence.Read<JsonElement>(p)).ToArray();
    internal JsonElement[] MediaEvents() => Events().Where(e=>e.GetProperty("phase").GetString()=="media").ToArray();
    internal JsonElement[] Embeddings() => Events().Where(e=>e.GetProperty("phase").GetString()=="embedding").ToArray();
    internal async Task<JsonElement> Wait(string receipt, Task index)
    {
        string path=Path.Combine(Audit,receipt); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!File.Exists(path))
        { if(index.IsCompleted){await index;throw new InvalidDataException("Index ended without deterministic handshake: "+receipt);} await Task.Delay(10,timeout.Token); }
        return Evidence.Read<JsonElement>(path);
    }
    internal void WorkspaceEmpty()
    { string w=Path.Combine(Data,"media-tasks"); Evidence.Assert(!Directory.Exists(w)||!Directory.EnumerateFileSystemEntries(w).Any(),"Unsettled native task workspace"); }
    internal static void State(IndexedFile? row,string expected,bool revision)
    {
        Evidence.Assert(row is not null && row.State==expected,$"Expected {expected}, actual {row?.State}: {row?.Detail}");
        bool actual=row!.Info is not null && JsonDocument.Parse(row.Info).RootElement.TryGetProperty("native_media_revision",out var marker) && marker.GetString()=="native-media-sampling-v1";
        Evidence.Assert(actual==revision,"Completion revision does not match coverage");
        foreach(var segment in row.Segments) if(segment.Vector is not null) VectorContract.Validate(segment.Vector);
    }
    internal static void Redacted(IndexedFile? row,BackendFixture h)
    {
        Evidence.Assert(row is not null,"Missing persisted failed/partial row");
        string text=JsonSerializer.Serialize(new {row!.Detail,row.Info,Segments=row.Segments.Select(s=>s.Excerpt)});
        foreach(string raw in new[]{"RAW_CANARY","SYNTHETIC_UNTRUSTED","media-tasks",h.Audit})
            Evidence.Assert(!text.Contains(raw,StringComparison.OrdinalIgnoreCase),"P1 persisted untrusted callback/decode path: "+raw);
    }
    internal void HelpersGone()
    { foreach(string f in Directory.GetFiles(Audit,"*.pid.json").Where(p=>!p.EndsWith(".model.pid.json",StringComparison.Ordinal))) Evidence.Assert(!Evidence.Alive(Evidence.Read<Identity>(f)),"Helper/descendant still alive: "+f); }
    public async ValueTask DisposeAsync()
    {
        if(Backend is null)return;
        try { await Backend.DisposeAsync(); } finally { await Snapshot(); Evidence.Gone(Audit); }
    }
}
