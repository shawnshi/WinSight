namespace WinSight.Bridge;

public static class McpErrors
{
    public const string ValidationError = "validation_error";
    public const string Unauthorized = "unauthorized";
    public const string ExcludedFile = "excluded_file";
    public const string StaleReference = "stale_reference";
    public const string Busy = "busy";
    public const string Cancelled = "cancelled";
    public const string Timeout = "timeout";
    public const string ModelUnavailable = "model_unavailable";
    public const string IndexIncompatible = "index_incompatible";
    public const string ParseError = "parse_error";
    public const string Internal = "internal";
}

public sealed class McpException : Exception
{
    public string Code { get; }

    public McpException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}
