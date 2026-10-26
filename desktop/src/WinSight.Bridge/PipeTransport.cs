using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace WinSight.Bridge;

/// <summary>
/// Named-pipe transport. Access is limited to the current user's SID, and every connection must present the
/// per-session token in its first frame: a pipe name alone is not authentication.
/// </summary>
internal sealed class PipeTransport
{
    private const int MaxFrame = 1024 * 1024;

    internal static async Task<int> RunAsync(string pipeName, string token, Func<string, Task<string>> dispatch, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = CreateServer(pipeName);
            Log("pipe waiting: " + pipeName);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            Log("pipe client connected");
            var authenticated = false;
            while (server.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                string? frame;
                try
                {
                    frame = await ReadFrameAsync(server, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException error)
                {
                    await WriteFrameAsync(server, ErrorEnvelope("parse_error", error.Message), cancellationToken).ConfigureAwait(false);
                    break;
                }
                if (frame is null) break;
                if (!authenticated)
                {
                    if (!string.Equals(frame.Trim(), token, StringComparison.Ordinal))
                    {
                        Log("pipe client rejected: token mismatch");
                        await WriteFrameAsync(server, ErrorEnvelope("unauthorized", "A valid session token is required."), cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    authenticated = true;
                    await WriteFrameAsync(server, "{\"jsonrpc\":\"2.0\",\"id\":\"auth\",\"result\":{\"authenticated\":true}}", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var reply = await dispatch(frame).ConfigureAwait(false);
                await WriteFrameAsync(server, reply, cancellationToken).ConfigureAwait(false);
            }
            Log("pipe client disconnected");
        }
        return 0;
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        // CurrentUserOnly restricts the pipe to this user's SID at the kernel level, which is stronger than a
        // deny-everyone ACL and is the only combination the ACL overload accepts.
        _ = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current user has no SID.");
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0) return null;
        if (read != 4) throw new InvalidDataException("truncated frame header");
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > MaxFrame) throw new InvalidDataException($"frame length {length} is outside 1..{MaxFrame}");
        var payload = new byte[length];
        var filled = await stream.ReadAtLeastAsync(payload, length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (filled != length) throw new InvalidDataException("truncated frame body");
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task WriteFrameAsync(Stream stream, string payload, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        if (body.Length > MaxFrame) throw new InvalidDataException("reply exceeds the frame limit");
        var header = BitConverter.GetBytes(body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ErrorEnvelope(string code, string message) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":-32000,\"message\":\"{message.Replace("\"", "'")}\",\"data\":{{\"category\":\"{code}\"}}}}}}";

    private static void Log(string message) => Console.Error.WriteLine("[winsight-mcp] " + message);
}
