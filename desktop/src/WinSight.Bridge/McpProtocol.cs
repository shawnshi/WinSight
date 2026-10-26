using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WinSight.Bridge;

public static class McpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

public sealed record JsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] object? Id,
    [property: JsonPropertyName("result")] object? Result,
    [property: JsonPropertyName("error")] JsonRpcError? Error);

public sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] object? Data = null);

public sealed record McpTextContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text)
{
    public static McpTextContent Create(string text) => new("text", text);
}

public sealed record McpToolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record McpCallToolResult(
    [property: JsonPropertyName("content")] IReadOnlyList<McpTextContent> Content,
    [property: JsonPropertyName("isError")] bool IsError,
    [property: JsonPropertyName("error")] McpToolError? Error = null,
    [property: JsonPropertyName("hits")] object? Hits = null,
    [property: JsonPropertyName("file")] object? File = null,
    [property: JsonPropertyName("status")] object? Status = null,
    [property: JsonPropertyName("jobId")] string? JobId = null);

public sealed record McpToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] object InputSchema);

public sealed record McpInitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("capabilities")] object Capabilities,
    [property: JsonPropertyName("serverInfo")] McpServerInfo ServerInfo);

public sealed record McpServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

public sealed record McpToolsListResult(
    [property: JsonPropertyName("tools")] IReadOnlyList<McpToolDefinition> Tools);

public sealed record FileHit(
    string Path,
    string Kind,
    double Score,
    string MatchType,
    string Excerpt,
    int? Page,
    int? Paragraph,
    string Version);

public sealed record FileContextResult(
    string Path,
    int StartLine,
    int LineCount,
    int LinesRead,
    int TotalLines,
    string Version,
    string Content,
    bool Truncated);

public sealed record IndexStatusResult(
    IReadOnlyList<string> Modalities,
    int FileCount,
    int ChunkCount,
    IReadOnlyDictionary<string, int> StatusDistribution,
    bool LexicalDegraded,
    string RebuildGeneration,
    int MonitoredDirectoryCount,
    string Http,
    string State);
