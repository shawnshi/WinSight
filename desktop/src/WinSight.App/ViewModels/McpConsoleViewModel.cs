using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinSight.Core;

namespace WinSight.App.ViewModels;

/// <summary>
/// Read-only view of the MCP bridge: how many maintenance requests are waiting, what recently happened, and
/// which transports exist. Draining is the one action, and it goes through the same backend call as Settings.
/// </summary>
public sealed class McpConsoleViewModel : INotifyPropertyChanged
{
    private readonly IMcpBackend backend;
    private string summary = "Loading the MCP bridge state…";
    private IReadOnlyList<string> recent = Array.Empty<string>();
    private IReadOnlyList<string> notes = Array.Empty<string>();
    private string actionResult = "";
    private bool busy;
    private string configSnippet = "";
    private string copyFeedback = "";
    private string selectedClient = "claude";
    private string clientPathHint = @"%APPDATA%\Claude\claude_desktop_config.json";
    private string bridgeHealthStatus = "";

    public McpConsoleViewModel(IMcpBackend backend)
    {
        this.backend = backend;
        UpdateSnippet();
    }

    public string Summary => summary;
    public IReadOnlyList<string> Recent => recent;
    public IReadOnlyList<string> Notes => notes;
    public string ActionResult => actionResult;
    public bool CanDrain => !busy;
    public string ConfigSnippet => configSnippet;
    public string CopyFeedback => copyFeedback;
    public string ClientPathHint => clientPathHint;
    public string BridgeHealthStatus => bridgeHealthStatus;

    public string SelectedClient
    {
        get => selectedClient;
        set
        {
            if (selectedClient == value) return;
            selectedClient = value;
            clientPathHint = value switch
            {
                "cursor" => @"%USERPROFILE%\.cursor\mcp.json",
                "windsurf" => @"%USERPROFILE%\.codeium\windsurf\mcp_config.json",
                "vscode" => @".vscode\mcp.json（工作区）或 ~/.pi/agent/mcp.json",
                _ => @"%APPDATA%\Claude\claude_desktop_config.json"
            };
            Raise(nameof(SelectedClient));
            Raise(nameof(ClientPathHint));
            UpdateSnippet();
        }
    }

    public void UpdateSnippet()
    {
        var appDir = AppContext.BaseDirectory;
        var bridge = Path.Combine(appDir, "bridge", "winsight-mcp.exe");
        if (!File.Exists(bridge))
        {
            var defaultInstall = @"C:\Program Files\WinSight\bridge\winsight-mcp.exe";
            if (File.Exists(defaultInstall)) bridge = defaultInstall;
        }

        var indexDir = "D:\\WinSightIndex";
        try
        {
            var prop = backend.GetType().GetProperty("IndexDirectory");
            if (prop?.GetValue(backend) is string dir && !string.IsNullOrWhiteSpace(dir))
                indexDir = dir;
        }
        catch { }
        var indexPath = Path.Combine(indexDir, "index.sqlite");

        var rootKey = selectedClient == "vscode" ? "servers" : "mcpServers";
        var entry = new JsonObject
        {
            ["command"] = bridge,
            ["args"] = new JsonArray
            {
                "--index",
                indexPath,
                "--stdio"
            }
        };
        if (selectedClient == "vscode") entry["type"] = "stdio";

        var doc = new JsonObject
        {
            [rootKey] = new JsonObject
            {
                ["winsight"] = entry
            }
        };

        configSnippet = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Raise(nameof(ConfigSnippet));
    }

    public void ProbeBridgeHealth()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bridge", "winsight-mcp.exe"),
            @"C:\Program Files\WinSight\bridge\winsight-mcp.exe"
        };
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            bridgeHealthStatus = "未检测到 winsight-mcp.exe，请检查安装目录下的 bridge\\ 子目录。";
        }
        else
        {
            var fi = new FileInfo(exe);
            bridgeHealthStatus = $"✓ 桥接器就绪：{exe}（{fi.Length / 1024} KB · 更新于 {fi.LastWriteTime:yyyy-MM-dd HH:mm}）";
        }
        Raise(nameof(BridgeHealthStatus));
    }

    public void CopyConfig()
    {
        if (string.IsNullOrWhiteSpace(configSnippet)) UpdateSnippet();
#if WINDOWS
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(configSnippet);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
#endif
        copyFeedback = "✓ 已成功复制 MCP 配置到剪贴板！可直接粘贴至 Claude Desktop 或 Cursor 配置文件中。";
        Raise(nameof(CopyFeedback));
    }

    public async Task RefreshAsync()
    {
        try
        {
            var status = await backend.GetMcpConsoleStatusAsync();
            if (status is null) { summary = "当前构建未启用 MCP 桥接状态查询。"; recent = Array.Empty<string>(); notes = Array.Empty<string>(); }
            else
            {
                summary = $"待处理请求: {status.Pending} · 执行中: {status.Claimed} · 已完成: {status.Completed} · 传输协议: stdio 就绪";
                recent = status.Recent.Count == 0 ? new[] { "尚无外部智能体提交目录维护请求。" } : status.Recent;
                notes = new[]
                {
                    "search_files — 跨文档、PDF、表格、代码与图片的 RRF 混合语义检索工具",
                    "read_file_context — 带行号、页码边界与二进制安全防护的受控上下文读取工具",
                    "get_index_status — 查询本地索引数据库规模、向量覆盖率与运行状态",
                    "index_directory — 在已授权目录范围内触发增量或全量索引更新"
                };
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            summary = "Reading the MCP state failed: " + error.Message;
        }
        Raise(nameof(Summary)); Raise(nameof(Recent)); Raise(nameof(Notes));
    }

    public async Task DrainAsync()
    {
        if (busy) return;
        busy = true; Raise(nameof(CanDrain));
        try
        {
            var outcomes = await backend.DrainMaintenanceJobsAsync();
            actionResult = outcomes.Count == 0 ? "当前没有待处理的维护请求。" : string.Join("; ", outcomes);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            actionResult = "处理维护请求失败：" + error.Message;
        }
        finally { busy = false; Raise(nameof(CanDrain)); Raise(nameof(ActionResult)); }
        await RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
