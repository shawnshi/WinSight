using System.Text.Json;
using WinSight.Infrastructure.Indexing;





namespace WinSight.Bridge;





public sealed class McpToolsHandler


{


    private readonly ReadOnlyIndexReader reader;


    private readonly string? dataDirectory;





    public McpToolsHandler(ReadOnlyIndexReader reader, string? dataDirectory = null)


    {


        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));


        this.dataDirectory = dataDirectory;


    }





    public static IReadOnlyList<McpToolDefinition> GetToolDefinitions()


    {


        return


        [


            new McpToolDefinition(


                Name: "search_files",


                Description: "Search indexed files by keyword or hybrid mode, returning file-level hits with kind, score, match_type, excerpt, and source locations.",


                InputSchema: new


                {


                    type = "object",


                    properties = new


                    {


                        query = new


                        {


                            type = "string",


                            description = "Search query (1..4096 characters)",


                            minLength = 1,


                            maxLength = 4096


                        },


                        mode = new


                        {


                            type = "string",


                            @enum = new[] { "keyword", "hybrid" },


                            description = "Search mode ('keyword' or 'hybrid', default 'keyword')"


                        },


                        root = new


                        {


                            type = "string",


                            description = "Optional directory root filter"


                        },


                        topK = new


                        {


                            type = "integer",


                            description = "Maximum file hits (1..20, default 8)",


                            minimum = 1,


                            maximum = 20,


                            @default = 8


                        }


                    },


                    required = new[] { "query" }


                }


            ),


            new McpToolDefinition(


                Name: "read_file_context",


                Description: "Read bounded content from a file previously hit and verified in the search index.",


                InputSchema: new


                {


                    type = "object",


                    properties = new


                    {


                        path = new


                        {


                            type = "string",


                            description = "Absolute path to a file returned by search_files"


                        },


                        startLine = new


                        {


                            type = "integer",


                            description = "1-based starting line number (default 1)",


                            minimum = 1


                        },


                        lineCount = new


                        {


                            type = "integer",


                            description = "Number of lines to read (<= 200, content <= 64 KiB, default 50)",


                            minimum = 1,


                            maximum = 200,


                            @default = 50


                        },


                        expectedVersion = new


                        {


                            type = "string",


                            description = "Expected file version or mtime"


                        }


                    },


                    required = new[] { "path" }


                }


            ),


            new McpToolDefinition(


                Name: "get_index_status",


                Description: "Get read-only status of the index database, modalities, chunk counts, degradation status, and bridge state.",


                InputSchema: new


                {


                    type = "object",


                    properties = new { }


                }


            ),


            new McpToolDefinition(


                Name: "index_directory",


                Description: "Trigger indexing for a directory. In this slice, always returns unauthorized.",


                InputSchema: new


                {


                    type = "object",


                    properties = new


                    {


                        path = new


                        {


                            type = "string",


                            description = "Directory to index"


                        }


                    },


                    required = new[] { "path" }


                }


            )


        ];


    }





    public async Task<McpCallToolResult> CallToolAsync(string name, JsonElement? arguments, CancellationToken ct)


    {


        try


        {


            return name switch


            {


                "search_files" => await HandleSearchFilesAsync(arguments, ct).ConfigureAwait(false),


                "read_file_context" => await HandleReadFileContextAsync(arguments, ct).ConfigureAwait(false),


                "get_index_status" => await HandleGetIndexStatusAsync(arguments, ct).ConfigureAwait(false),


                "index_directory" => HandleIndexDirectory(arguments),


                _ => throw new McpException(McpErrors.ValidationError, $"Unknown tool '{name}'.")


            };


        }


        catch (McpException ex)


        {


            return new McpCallToolResult(


                Content: [McpTextContent.Create($"Error [{ex.Code}]: {ex.Message}")],


                IsError: true,


                Error: new McpToolError(ex.Code, ex.Message)


            );


        }


        catch (OperationCanceledException)


        {


            return new McpCallToolResult(


                Content: [McpTextContent.Create("Error [cancelled]: Operation was cancelled.")],


                IsError: true,


                Error: new McpToolError(McpErrors.Cancelled, "Operation was cancelled.")


            );


        }


        catch (Exception ex)


        {


            return new McpCallToolResult(


                Content: [McpTextContent.Create($"Error [internal]: {ex.Message}")],


                IsError: true,


                Error: new McpToolError(McpErrors.Internal, ex.Message)


            );


        }


    }





    private async Task<McpCallToolResult> HandleSearchFilesAsync(JsonElement? args, CancellationToken ct)


    {


        if (args == null || args.Value.ValueKind != JsonValueKind.Object)


        {


            throw new McpException(McpErrors.ValidationError, "Arguments object is required for search_files.");


        }





        var obj = args.Value;


        if (!obj.TryGetProperty("query", out var queryProp) || queryProp.ValueKind != JsonValueKind.String)


        {


            throw new McpException(McpErrors.ValidationError, "'query' string parameter is required.");


        }





        string query = queryProp.GetString()!;


        string? mode = obj.TryGetProperty("mode", out var modeProp) && modeProp.ValueKind == JsonValueKind.String


            ? modeProp.GetString()


            : null;





        string? root = obj.TryGetProperty("root", out var rootProp) && rootProp.ValueKind == JsonValueKind.String


            ? rootProp.GetString()


            : null;





        int topK = 8;


        if (obj.TryGetProperty("topK", out var topKProp) && topKProp.ValueKind == JsonValueKind.Number)


        {


            topK = topKProp.GetInt32();


        }





        var hits = await reader.SearchFilesAsync(query, mode, root, topK, ct).ConfigureAwait(false);





        string summary = hits.Count == 0


            ? "No matching files found."


            : $"Found {hits.Count} matching file(s).";





        return new McpCallToolResult(


            Content: [McpTextContent.Create(summary)],


            IsError: false,


            Hits: hits


        );


    }





    private async Task<McpCallToolResult> HandleReadFileContextAsync(JsonElement? args, CancellationToken ct)


    {


        if (args == null || args.Value.ValueKind != JsonValueKind.Object)


        {


            throw new McpException(McpErrors.ValidationError, "Arguments object is required for read_file_context.");


        }





        var obj = args.Value;


        if (!obj.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)


        {


            throw new McpException(McpErrors.ValidationError, "'path' string parameter is required.");


        }





        string path = pathProp.GetString()!;





        int? startLine = obj.TryGetProperty("startLine", out var startProp) && startProp.ValueKind == JsonValueKind.Number


            ? startProp.GetInt32()


            : null;





        int? lineCount = obj.TryGetProperty("lineCount", out var countProp) && countProp.ValueKind == JsonValueKind.Number


            ? countProp.GetInt32()


            : null;





        string? expectedVersion = obj.TryGetProperty("expectedVersion", out var verProp) && verProp.ValueKind == JsonValueKind.String


            ? verProp.GetString()


            : null;





        var result = await reader.ReadFileContextAsync(path, startLine, lineCount, expectedVersion, ct).ConfigureAwait(false);





        return new McpCallToolResult(


            Content: [McpTextContent.Create(result.Content)],


            IsError: false,


            File: result


        );


    }





    private async Task<McpCallToolResult> HandleGetIndexStatusAsync(JsonElement? args, CancellationToken ct)


    {


        var status = await reader.GetIndexStatusAsync(ct).ConfigureAwait(false);





        string summary = $"Index status: {status.ChunkCount} chunks, {status.FileCount} files, degraded={status.LexicalDegraded}, http={status.Http}, state={status.State}.";





        return new McpCallToolResult(


            Content: [McpTextContent.Create(summary)],


            IsError: false,


            Status: status


        );


    }





    /// <summary>


    /// Maintenance is a request, never a write: the caller must hold the user-created grant, and the desktop


    /// app performs the work after its own checks. Without a grant this stays unauthorized.


    /// </summary>


    private McpCallToolResult HandleIndexDirectory(JsonElement? args)


    {


        if (dataDirectory is null)


            throw new McpException(McpErrors.Unauthorized, "Maintenance authorization is unavailable: no data directory is bound.");


        var grant = MaintenanceJobStore.ReadGrant(dataDirectory);


        var presented = Environment.GetEnvironmentVariable("WINSIGHT_MCP_MAINTENANCE_GRANT");


        if (string.IsNullOrWhiteSpace(grant) || string.IsNullOrWhiteSpace(presented) ||


            !string.Equals(grant, presented, StringComparison.Ordinal))


            throw new McpException(McpErrors.Unauthorized,


                "Maintenance authorization must be independently acquired: create a grant in the app and present its identifier.");





        string root = "";


        if (args is { ValueKind: JsonValueKind.Object } value)


        {


            if (value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String) root = path.GetString() ?? "";


            else if (value.TryGetProperty("root", out var legacy) && legacy.ValueKind == JsonValueKind.String) root = legacy.GetString() ?? "";


        }


        if (string.IsNullOrWhiteSpace(root) || root.Length > 4096 || root.IndexOf((char)0) >= 0)


            throw new McpException(McpErrors.ValidationError, "A directory path is required.");





        var job = new MaintenanceJobStore(dataDirectory).SubmitAsync(root, "local").GetAwaiter().GetResult();


        return new McpCallToolResult(


            Content: [McpTextContent.Create($"Maintenance request queued for the desktop app: job {job.Id}. It runs only after the app accepts the path.")],


            IsError: false,


            JobId: job.Id,


            Status: new { jobId = job.Id, state = "pending", root = job.Root, requestedUtc = job.RequestedUtc }


        );


    }


}

    /// <summary>
    /// Maintenance is a request, never a write: the desktop app validates the path against its own authorized
    /// folders and performs the indexing. This tool only records the request and returns its id.
    /// </summary>

