using System.Net;
using System.Text;

namespace WinSight.Bridge;

/// <summary>
/// Loopback-only HTTP transport. It is disabled unless explicitly requested, binds 127.0.0.1, requires the
/// session bearer token, and refuses any Origin that is not this machine's own loopback.
/// </summary>
internal sealed class HttpTransport
{
    private const int MaxBodyBytes = 1024 * 1024;

    internal static async Task<int> RunAsync(int port, string token, Func<string, Task<string?>> dispatch, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
        listener.Start();
        Log($"http listening on 127.0.0.1:{port}/mcp (loopback only)");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _ = Task.Run(() => HandleAsync(context, token, dispatch, cancellationToken), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
        return 0;
    }

    private static async Task HandleAsync(HttpListenerContext context, string token, Func<string, Task<string?>> dispatch, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            void Reject(int status, string code, string message)
            {
                Write(response, status, $"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":{status},\"message\":\"{message}\",\"data\":{{\"category\":\"{code}\"}}}}}}");
            }
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
            {
                Reject(405, "validation_error", "Only POST is allowed.");
                return;
            }
            if (!OriginAllowed(request.Headers["Origin"]))
            {
                Reject(403, "unauthorized", "Origin is not permitted.");
                return;
            }
            var authorization = request.Headers["Authorization"];
            if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !FixedEquals(authorization[7..], token))
            {
                Reject(401, "unauthorized", "A valid bearer token is required.");
                return;
            }
            if (request.ContentLength64 > MaxBodyBytes)
            {
                Reject(413, "validation_error", "Request body exceeds 1 MiB.");
                return;
            }
            byte[] rawBuffer;
            using (var ms = new MemoryStream())
            {
                var chunk = new byte[Math.Min(16384, MaxBodyBytes + 1)];
                int total = 0;
                int read;
                while ((read = await request.InputStream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxBodyBytes + 1 - total)), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxBodyBytes)
                    {
                        Reject(413, "validation_error", "Request body exceeds 1 MiB.");
                        return;
                    }
                    ms.Write(chunk, 0, read);
                }
                rawBuffer = ms.ToArray();
            }
            string body = Encoding.UTF8.GetString(rawBuffer);
            if (body.Length == 0)
            {
                Reject(400, "validation_error", "Empty or oversized body.");
                return;
            }
            var reply = await dispatch(body).ConfigureAwait(false);
            if (reply is null) { Write(response, 204, null); return; }
            Write(response, 200, reply);
        }
        catch (Exception error)
        {
            Log("http handler failed: " + error.GetType().Name + ": " + error.Message);
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    private static bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;   // non-browser client; the bearer token still applies
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static void Write(HttpListenerResponse response, int status, string? body)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        if (body is null) { response.Close(); return; }
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    private static void Log(string message) => Console.Error.WriteLine("[winsight-mcp] " + message);
}
