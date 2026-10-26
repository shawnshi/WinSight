using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace WinSight.Bridge;

public sealed class McpStdioServer
{
    public const int MaxMessageBytes = 1024 * 1024; // 1 MiB
    private readonly McpBridgeStateMachine stateMachine;
    private readonly McpToolsHandler toolsHandler;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> activeRequests = new();
    private readonly object writeGate = new();

    public McpStdioServer(McpBridgeStateMachine stateMachine, McpToolsHandler toolsHandler)
    {
        this.stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        this.toolsHandler = toolsHandler ?? throw new ArgumentNullException(nameof(toolsHandler));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        stateMachine.TransitionTo(McpBridgeState.Starting);
        Console.Error.WriteLine("[winsight-mcp] Bridge starting up...");

        stateMachine.TransitionTo(McpBridgeState.Running);
        Console.Error.WriteLine("[winsight-mcp] Bridge is running (stdio transport).");

        try
        {
            var stdinStream = Console.OpenStandardInput();
            using var reader = new BoundedLineReader(stdinStream, MaxMessageBytes);

            while (!cancellationToken.IsCancellationRequested)
            {
                var lineResult = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (lineResult.IsEof)
                {
                    Console.Error.WriteLine("[winsight-mcp] Stdin reached EOF. Initiating shutdown.");
                    break;
                }

                if (lineResult.ExceededLimit)
                {
                    Console.Error.WriteLine($"[winsight-mcp] Validation error: incoming message exceeded 1 MiB limit ({lineResult.ByteCount} bytes).");
                    SendErrorResponse(
                        id: null,
                        code: -32600,
                        message: "validation_error: message exceeds 1 MiB limit",
                        data: new { code = McpErrors.ValidationError, limitBytes = MaxMessageBytes }
                    );
                    continue;
                }

                if (string.IsNullOrWhiteSpace(lineResult.Line))
                {
                    continue;
                }

                _ = HandleLineAsync(lineResult.Line, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[winsight-mcp] Operation cancelled.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[winsight-mcp] Server error: {ex}");
            stateMachine.TransitionTo(McpBridgeState.Faulted);
            throw;
        }
        finally
        {
            stateMachine.TransitionTo(McpBridgeState.Stopping);
            Console.Error.WriteLine("[winsight-mcp] Stopping bridge...");
            stateMachine.TransitionTo(McpBridgeState.Disabled);
            Console.Error.WriteLine("[winsight-mcp] Bridge disabled.");
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken globalCt)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[winsight-mcp] JSON parse error: {ex.Message}");
            SendErrorResponse(
                id: null,
                code: -32700,
                message: "parse_error: Invalid JSON-RPC 2.0 message",
                data: new { code = McpErrors.ParseError, detail = ex.Message }
            );
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                SendErrorResponse(null, -32600, "validation_error: Invalid JSON-RPC 2.0 request", new { code = McpErrors.ValidationError });
                return;
            }

            if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
            {
                SendErrorResponse(null, -32600, "validation_error: Missing 'method' in JSON-RPC request", new { code = McpErrors.ValidationError });
                return;
            }

            string method = methodProp.GetString()!;
            object? idValue = null;
            string? idString = null;
            if (root.TryGetProperty("id", out var idProp))
            {
                idValue = idProp.ValueKind switch
                {
                    JsonValueKind.Number => idProp.GetInt64(),
                    JsonValueKind.String => idProp.GetString(),
                    _ => (object?)idProp.GetRawText()
                };
                idString = idValue?.ToString();
            }

            root.TryGetProperty("params", out var paramsProp);

            // Notification: id is null
            bool isNotification = idValue == null;

            if (isNotification)
            {
                HandleNotification(method, paramsProp);
                return;
            }

            // Request
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt);
            if (idString != null)
            {
                activeRequests[idString] = reqCts;
            }

            try
            {
                await HandleRequestAsync(idValue, method, paramsProp, reqCts.Token).ConfigureAwait(false);
            }
            finally
            {
                if (idString != null)
                {
                    activeRequests.TryRemove(idString, out _);
                }
            }
        }
    }

    private void HandleNotification(string method, JsonElement paramsElement)
    {
        switch (method)
        {
            case "notifications/initialized":
                Console.Error.WriteLine("[winsight-mcp] Received notifications/initialized.");
                break;

            case "notifications/cancelled":
                if (paramsElement.ValueKind == JsonValueKind.Object &&
                    paramsElement.TryGetProperty("requestId", out var reqIdProp))
                {
                    string reqId = reqIdProp.ValueKind == JsonValueKind.Number
                        ? reqIdProp.GetInt64().ToString()
                        : reqIdProp.GetString() ?? "";

                    if (!string.IsNullOrEmpty(reqId) && activeRequests.TryGetValue(reqId, out var cts))
                    {
                        Console.Error.WriteLine($"[winsight-mcp] Cancelling request {reqId}.");
                        cts.Cancel();
                    }
                }
                break;

            default:
                Console.Error.WriteLine($"[winsight-mcp] Unhandled notification: {method}");
                break;
        }
    }

    private async Task HandleRequestAsync(object? id, string method, JsonElement paramsElement, CancellationToken ct)
    {
        try
        {
            switch (method)
            {
                case "initialize":
                    var initResult = new McpInitializeResult(
                        ProtocolVersion: "2024-11-05",
                        Capabilities: new
                        {
                            tools = new
                            {
                                listChanged = false
                            }
                        },
                        ServerInfo: new McpServerInfo("winsight-mcp", "1.0.0")
                    );
                    SendSuccessResponse(id, initResult);
                    break;

                case "ping":
                    SendSuccessResponse(id, new { });
                    break;

                case "tools/list":
                    var tools = McpToolsHandler.GetToolDefinitions();
                    SendSuccessResponse(id, new McpToolsListResult(tools));
                    break;

                case "tools/call":
                    if (paramsElement.ValueKind != JsonValueKind.Object)
                    {
                        SendErrorResponse(id, -32602, "validation_error: Invalid parameters for tools/call", new { code = McpErrors.ValidationError });
                        return;
                    }

                    if (!paramsElement.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    {
                        SendErrorResponse(id, -32602, "validation_error: Missing tool 'name'", new { code = McpErrors.ValidationError });
                        return;
                    }

                    string toolName = nameProp.GetString()!;
                    paramsElement.TryGetProperty("arguments", out var argsProp);

                    var toolResult = await toolsHandler.CallToolAsync(toolName, argsProp, ct).ConfigureAwait(false);
                    SendSuccessResponse(id, toolResult);
                    break;

                default:
                    Console.Error.WriteLine($"[winsight-mcp] Unknown method: {method}");
                    SendErrorResponse(id, -32601, $"Method not found: {method}", new { code = McpErrors.ValidationError });
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SendErrorResponse(id, -32000, "cancelled: Request was cancelled", new { code = McpErrors.Cancelled });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[winsight-mcp] Error handling request {method}: {ex}");
            SendErrorResponse(id, -32603, $"internal: {ex.Message}", new { code = McpErrors.Internal });
        }
    }

    private void SendSuccessResponse(object? id, object? result)
    {
        var response = new JsonRpcResponse("2.0", id, result, null);
        string json = McpJson.Serialize(response);
        WriteLineToStdout(json);
    }

    private void SendErrorResponse(object? id, int code, string message, object? data = null)
    {
        var error = new JsonRpcError(code, message, data);
        var response = new JsonRpcResponse("2.0", id, null, error);
        string json = McpJson.Serialize(response);
        WriteLineToStdout(json);
    }

    private void WriteLineToStdout(string message)
    {
        lock (writeGate)
        {
            // MCP Transport rule: stdout ONLY writes legal JSON-RPC messages (one per line, UTF-8, no BOM)
            Console.Out.WriteLine(message);
            Console.Out.Flush();
        }
    }
}

public sealed record LineReadResult(string Line, int ByteCount, bool ExceededLimit, bool IsEof);

public sealed class BoundedLineReader : IDisposable
{
    private readonly Stream stream;
    private readonly int maxBytes;
    private readonly byte[] buffer = new byte[8192];
    private int bufferPos = 0;
    private int bufferCount = 0;
    private bool disposed;

    public BoundedLineReader(Stream stream, int maxBytes)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.maxBytes = maxBytes;
    }

    public async Task<LineReadResult> ReadLineAsync(CancellationToken ct)
    {
        using var memoryStream = new MemoryStream();
        int totalBytes = 0;
        bool exceeded = false;

        while (true)
        {
            if (bufferPos >= bufferCount)
            {
                bufferPos = 0;
                bufferCount = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (bufferCount == 0)
                {
                    // EOF
                    if (totalBytes == 0) return new LineReadResult("", 0, false, true);
                    break;
                }
            }

            byte b = buffer[bufferPos++];
            totalBytes++;

            if (b == (byte)'\n')
            {
                break;
            }
            if (b == (byte)'\r')
            {
                // Peek next byte for \n
                continue;
            }

            if (totalBytes > maxBytes)
            {
                exceeded = true;
                // Discard rest of this line
                continue;
            }

            memoryStream.WriteByte(b);
        }

        if (exceeded)
        {
            return new LineReadResult("", totalBytes, true, false);
        }

        string line = Encoding.UTF8.GetString(memoryStream.ToArray());
        return new LineReadResult(line, totalBytes, false, false);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
        }
    }
}
