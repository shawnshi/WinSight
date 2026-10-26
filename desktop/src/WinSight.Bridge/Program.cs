using System.Text;

namespace WinSight.Bridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Enforce UTF-8 without BOM for stdio
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        string? indexPath = null;
        string? pipeName = null;
        string? tokenEnvironment = null;

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--index" || args[i] == "-i") && i + 1 < args.Length)
            {
                indexPath = args[++i];
            }
            else if (args[i] == "--pipe" && i + 1 < args.Length)
            {
                pipeName = args[++i];
            }
            else if (args[i] == "--pipe-token-env" && i + 1 < args.Length)
            {
                tokenEnvironment = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(indexPath))
        {
            indexPath = Environment.GetEnvironmentVariable("WINSIGHT_INDEX_PATH");
        }

        if (string.IsNullOrWhiteSpace(indexPath))
        {
            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "WinSight Native Preview", "data", "index", "index.sqlite"
            );

            if (File.Exists(defaultPath))
            {
                indexPath = defaultPath;
            }
            else
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinSight", "data", "index", "index.sqlite"
                );
                indexPath = File.Exists(fallback) ? fallback : defaultPath;
            }
        }

        Console.Error.WriteLine($"[winsight-mcp] Selected index path: {indexPath}");

        var stateMachine = new McpBridgeStateMachine();
        var registry = new AllowedFileRegistry();
        using var reader = new ReadOnlyIndexReader(indexPath, registry, stateMachine);
        var toolsHandler = new McpToolsHandler(reader);
        var server = new McpStdioServer(stateMachine, toolsHandler);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("[winsight-mcp] SIGINT/Ctrl+C received. Initiating graceful shutdown...");
            cts.Cancel();
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(pipeName))
            {
                // The pipe carries the same JSON-RPC payloads as stdio; the token is read from the environment
                // so it never appears in a command line or a log.
                var token = Environment.GetEnvironmentVariable(tokenEnvironment ?? "WINSIGHT_MCP_TOKEN");
                if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
                    throw new InvalidOperationException("A session token of at least 32 characters must be supplied through the environment.");
                stateMachine.TransitionTo(McpBridgeState.Starting);
                stateMachine.TransitionTo(McpBridgeState.Running);
                Console.Error.WriteLine("[winsight-mcp] Bridge is running (named pipe transport).");
                try
                {
                    return await PipeTransport.RunAsync(pipeName, token, line => server.CaptureLineAsync(line, cts.Token)!, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    stateMachine.TransitionTo(McpBridgeState.Stopping);
                    stateMachine.TransitionTo(McpBridgeState.Disabled);
                }
            }
            await server.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[winsight-mcp] Fatal error: {ex}");
            return 1;
        }
    }
}
