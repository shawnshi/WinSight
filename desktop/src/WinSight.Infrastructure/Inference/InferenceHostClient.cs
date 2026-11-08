using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinSight.Core;

namespace WinSight.Infrastructure.Inference;

/// <summary>Owns one lazy, serialized JSONL host. Callers supply task prompts verbatim.</summary>
public sealed class InferenceHostClient : IEmbeddingService
{
    internal const int MaxLineBytes = 1024 * 1024;
    private const int MaxPending = 32;
    private const int MaxInteractiveYieldMilliseconds = 2000;
    private const int YieldPollMilliseconds = 5;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string executable, modelDirectory, manifestPath;
    private readonly string accelerator;
    private readonly TimeSpan startupTimeout, requestTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource disposedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Session? session;
    private EmbeddingInfo? identity;
    private int pending, disposed, interactiveWaiting;
    private long nextId, lastInteractiveTick = -10000;

    public InferenceHostClient(string executable, string modelDirectory, string manifestPath,
        string accelerator = "auto")
        : this(executable, modelDirectory, manifestPath, accelerator,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)) { }

    public InferenceHostClient(string executable, string modelDirectory, string manifestPath,
        bool automaticProviders)
        : this(executable, modelDirectory, manifestPath, automaticProviders ? "auto" : "cpu",
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)) { }

    internal InferenceHostClient(string executable, string modelDirectory, string manifestPath,
        string accelerator,
        TimeSpan startupTimeout, TimeSpan requestTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(accelerator);
        if (startupTimeout <= TimeSpan.Zero || requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        this.executable = executable;
        this.modelDirectory = modelDirectory;
        this.manifestPath = manifestPath;
        this.accelerator = accelerator;
        this.startupTimeout = startupTimeout;
        this.requestTimeout = requestTimeout;
    }

    internal InferenceHostClient(string executable, string modelDirectory, string manifestPath,
        bool automaticProviders, TimeSpan startupTimeout, TimeSpan requestTimeout)
        : this(executable, modelDirectory, manifestPath, automaticProviders ? "auto" : "cpu",
            startupTimeout, requestTimeout) { }

    public Task<EmbeddingInfo> GetInfoAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        using var reply = await ExchangeAsync(new { id = ++nextId, op = "info" }, nextId, token);
        var info = ReadInfo(reply.RootElement);
        CheckIdentity(info);
        return info;
    }, cancellationToken, start: true, interactive: true);

    public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default) =>
        EmbedCoreAsync(inputs, interactive: false, cancellationToken);

    /// <summary>
    /// User-query path. It overtakes queued bulk batches and, once the host has accepted the request, a caller that stops
    /// waiting (the next keystroke) abandons only its own wait: the host finishes the short request, the reply is consumed,
    /// and the model stays loaded instead of being killed and cold-started.
    /// </summary>
    public Task<IReadOnlyList<EmbeddingResult>> EmbedInteractiveAsync(IReadOnlyList<EmbeddingInput> inputs, CancellationToken cancellationToken = default) =>
        EmbedCoreAsync(inputs, interactive: true, cancellationToken);

    private async Task<IReadOnlyList<EmbeddingResult>> EmbedCoreAsync(IReadOnlyList<EmbeddingInput> inputs, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count > 4096) throw new ArgumentOutOfRangeException(nameof(inputs), "At most 4096 items per call.");
        var snapshot = inputs.ToArray();
        foreach (var input in snapshot)
        {
            if (input is null || input.Operation is not ("text" or "image" or "audio") || input.Value is null || input.Budget is < 1 or > 1120)
                throw new ArgumentException("Each input requires text/image/audio, a non-null value and budget in 1..1120.", nameof(inputs));
            // Strict encoding rejects unpaired surrogates instead of silently replacing caller text.
            if (StrictUtf8.GetByteCount(input.Value) > MaxLineBytes / 2)
                throw new ArgumentException("An input exceeds the bounded JSONL request size.", nameof(inputs));
        }
        if (snapshot.Length == 0)
        {
            // Preserve the historical contract: an empty call still observes disposal and cancellation.
            return await ExecuteAsync<IReadOnlyList<EmbeddingResult>>(_ => Task.FromResult<IReadOnlyList<EmbeddingResult>>([]),
                cancellationToken, start: false, interactive).ConfigureAwait(false);
        }
        // Each wire batch takes the host separately, so a waiting user query can slip in between two bulk batches.
        var results = new List<EmbeddingResult>(snapshot.Length);
        for (var offset = 0; offset < snapshot.Length;)
        {
            var from = offset;
            var (batch, count) = await ExecuteAsync(async token =>
            {
                var first = snapshot[from];
                if (!identity!.Capabilities.Contains(first.Operation, StringComparer.Ordinal))
                    throw new NotSupportedException($"Host does not advertise {first.Operation} capability.");
                var size = 1;
                while (size < 32 && from + size < snapshot.Length && snapshot[from + size].Operation == first.Operation
                    && snapshot[from + size].Budget == first.Budget) size++;
                var id = ++nextId;
                byte[] payload;
                while (true)
                {
                    payload = JsonSerializer.SerializeToUtf8Bytes(new { id, op = first.Operation, budget = first.Budget,
                        inputs = snapshot.Skip(from).Take(size).Select(item => item.Value).ToArray() });
                    if (payload.Length <= MaxLineBytes) break;
                    if (size == 1) throw new ArgumentException("An input exceeds the bounded JSONL request size.", nameof(inputs));
                    size = Math.Max(1, size / 2);
                }
                using var reply = await ExchangeBytesAsync(payload, id, token).ConfigureAwait(false);
                return (ReadBatch(reply.RootElement, size), size);
            }, cancellationToken, start: true, interactive).ConfigureAwait(false);
            results.AddRange(batch);
            offset += count;
        }
        return results;
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken, bool start = true, bool interactive = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref pending) > MaxPending)
        {
            Interlocked.Decrement(ref pending);
            throw new InvalidOperationException("Inference request admission limit reached.");
        }
        if (!interactive) return await RunSerializedAsync(action, cancellationToken, start, interactive: false).ConfigureAwait(false);
        // The work runs to completion once the host owns it; only this caller's wait is abandoned on cancellation.
        Interlocked.Increment(ref interactiveWaiting);
        var work = RunSerializedAsync(action, cancellationToken, start, interactive: true);
        try { return await work.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !work.IsCompleted)
        {
            _ = work.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private async Task<T> RunSerializedAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken callerToken, bool start, bool interactive)
    {
        // Interactive work is shielded from caller cancellation once the gate is taken; it still ends with the client and the request timeout.
        using var queueLink = CancellationTokenSource.CreateLinkedTokenSource(callerToken, lifetime.Token);
        using var shielded = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var linked = interactive ? shielded : queueLink;
        var acquired = false;
        var counted = interactive;
        try
        {
            while (true)
            {
                if (!interactive) await YieldToInteractiveAsync(queueLink.Token).ConfigureAwait(false);
                // Queueing is bounded in both size and time; cancelling a waiter must not reset another caller's child.
                if (!await gate.WaitAsync(requestTimeout, queueLink.Token).ConfigureAwait(false)) throw new TimeoutException("Inference queue wait timed out.");
                if (!interactive && Volatile.Read(ref interactiveWaiting) > 0)
                {
                    gate.Release();
                    continue;
                }
                acquired = true;
                break;
            }
            linked.Token.ThrowIfCancellationRequested();
            if (start) await EnsureStartedAsync(linked.Token).ConfigureAwait(false);
            return await action(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            if (acquired) Reset();
            throw;
        }
        finally
        {
            if (interactive) Interlocked.Exchange(ref lastInteractiveTick, Environment.TickCount64);
            if (counted) Interlocked.Decrement(ref interactiveWaiting);
            if (acquired) gate.Release();
            Interlocked.Decrement(ref pending);
        }
    }

    /// <summary>Bulk batches step aside while a user query is queued; the wait is capped so a typing burst cannot starve indexing.</summary>
    private async Task YieldToInteractiveAsync(CancellationToken token)
    {
        var waited = 0;
        while ((Volatile.Read(ref interactiveWaiting) > 0 || Environment.TickCount64 - Interlocked.Read(ref lastInteractiveTick) < 35)
               && waited < MaxInteractiveYieldMilliseconds)
        {
            await Task.Delay(YieldPollMilliseconds, token).ConfigureAwait(false);
            waited += YieldPollMilliseconds;
        }
    }

    internal static IReadOnlyList<string> BuildArguments(string modelDirectory, string manifestPath,
        string accelerator)
    {
        // Provider acquisition lives in the isolated Foundry host; the inference host only registers what is
        // already on the machine, so it takes no install switch.
        return new List<string> { "--stdio", "--models", modelDirectory, "--manifest", manifestPath, "--device", accelerator };
    }

    internal static string ExpectedRequestedProvider(string accelerator) => accelerator switch
    {
        "cpu" => "CPUExecutionProvider",
        "auto" => "device-auto-with-real-model-gates",
        "discrete-gpu" => "device-discrete-gpu-with-real-model-gates",
        "npu" => "device-npu-with-real-model-gates",
        "gpu" => "device-gpu-with-real-model-gates",
        _ => throw new ArgumentException($"Unsupported accelerator: '{accelerator}'. Allowed: auto, discrete-gpu, npu, gpu, cpu.", nameof(accelerator))
    };

    private async Task EnsureStartedAsync(CancellationToken token)
    {
        if (session is not null) return;
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"Inference host executable not found: '{executable}'. Please check installation.", executable);
        }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in BuildArguments(modelDirectory, manifestPath, accelerator))
            start.ArgumentList.Add(argument);
        session = Session.Start(start);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(startupTimeout);
        try
        {
            using var ready = await session.ReadAsync(deadline.Token);
            var root = ready.RootElement;
            Require(Number(root, "protocol") == 1, "Unsupported inference protocol.");
            if (!Flag(root, "ready")) throw new InvalidDataException("Inference host startup failed: " + Text(root, "error"));
            var info = ReadInfo(root);
            CheckIdentity(info);
            identity ??= info;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Inference host startup timed out.");
        }
    }

    private Task<JsonDocument> ExchangeAsync(object request, long id, CancellationToken token) =>
        ExchangeBytesAsync(JsonSerializer.SerializeToUtf8Bytes(request), id, token);

    private async Task<JsonDocument> ExchangeBytesAsync(byte[] request, long id, CancellationToken token)
    {
        Require(request.Length <= MaxLineBytes, "Request exceeds 1 MiB.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(requestTimeout);
        JsonDocument? reply = null;
        try
        {
            await session!.Input.WriteAsync(request, deadline.Token).AsTask().WaitAsync(deadline.Token);
            await session.Input.WriteAsync(new byte[] { 10 }, deadline.Token).AsTask().WaitAsync(deadline.Token);
            await session.Input.FlushAsync(deadline.Token).WaitAsync(deadline.Token);
            reply = await session.ReadAsync(deadline.Token);
            Require(Number(reply.RootElement, "id") == id, "Inference reply id mismatch.");
            _ = Flag(reply.RootElement, "ok");
            return reply;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            reply?.Dispose();
            throw new TimeoutException("Inference request timed out.");
        }
        catch
        {
            reply?.Dispose();
            throw;
        }
    }

    private EmbeddingInfo ReadInfo(JsonElement root)
    {
        Require(Number(root, "protocol") == 1, "Unsupported inference protocol.");
        if (root.TryGetProperty("ok", out _)) Require(Flag(root, "ok"), "Host info request failed.");
        var info = Field(root, "info", JsonValueKind.Object);
        Require(Number(info, "dim") == VectorContract.Dimension, "Host dimension mismatch.");
        var profile = Text(info, "profile_id");
        Require(IsHash(profile), "Invalid embedding profile id.");
        var model = Text(info, "model");
        var revision = Text(info, "revision");
        Require(model == "onnx-community/embeddinggemma-2-ONNX" && revision == "daa72c51243991dfcaf9f9137d2c573d8f7790c0", "Host model identity mismatch.");
        Require(Text(info, "dtype") == "q4", "Host dtype mismatch.");
        var runtime = Text(info, "runtime");
        Require(runtime.StartsWith("windows-ml-ort-", StringComparison.Ordinal) && runtime.Length > 15, "Invalid host runtime.");
        var provider = Text(info, "requested_provider");
        var expectedProvider = ExpectedRequestedProvider(accelerator);
        Require(provider == expectedProvider, "Host requested provider mismatch.");
        Require(!Flag(info, "provider_downloads_allowed"), "Host must never allow provider downloads.");
        var textOnly = Flag(info, "text_only");
        var capabilities = Field(root, "capabilities", JsonValueKind.Object);
        var text = Flag(capabilities, "text");
        var image = Flag(capabilities, "image");
        var audio = Flag(capabilities, "audio");
        Require(!textOnly && text, "Host mode/capabilities differ from the requested full-media mode.");
        var backendStatus = "";
        if (info.TryGetProperty("backend_status", out var statusProp)
            && statusProp.ValueKind != JsonValueKind.Null && statusProp.ValueKind != JsonValueKind.Undefined)
        {
            backendStatus = statusProp.ValueKind == JsonValueKind.String
                ? statusProp.GetString() ?? ""
                : statusProp.GetRawText();
        }
        return new EmbeddingInfo(profile, VectorContract.Dimension, runtime, model, revision, provider,
            Text(info, "validation"), Array.AsReadOnly(new[] { ("text", text), ("image", image), ("audio", audio) }
                .Where(item => item.Item2).Select(item => item.Item1).ToArray()), backendStatus);
    }

    private void CheckIdentity(EmbeddingInfo info)
    {
        if (identity is null) return;
        // BackendStatus is live diagnostic state (epoch, provider qualification progress), not identity:
        // comparing it made a legitimate host restart look like an identity change and failed indexing.
        Require(identity.ProfileId == info.ProfileId && identity.Dimension == info.Dimension && identity.Runtime == info.Runtime
            && identity.Model == info.Model && identity.Revision == info.Revision && identity.RequestedProvider == info.RequestedProvider
            && identity.Validation == info.Validation && identity.Capabilities.SequenceEqual(info.Capabilities),
            "Host embedding identity changed.");
    }

    private static IReadOnlyList<EmbeddingResult> ReadBatch(JsonElement root, int count)
    {
        if (root.TryGetProperty("error", out _))
        {
            Require(!Flag(root, "ok"), "Contradictory host request error.");
            throw new IOException("Inference host rejected request: " + Text(root, "error"));
        }
        Require(Number(root, "dim") == VectorContract.Dimension && Number(root, "count") == count, "Batch shape mismatch.");
        var errors = Field(root, "errors", JsonValueKind.Array);
        Require(errors.GetArrayLength() == count, "Batch error count mismatch.");
        var encoded = Text(root, "vectors");
        var byteCount = count * VectorContract.Dimension * sizeof(float);
        Require(encoded.Length == (byteCount + 2) / 3 * 4 && encoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/'), "Invalid base64 vector encoding.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("Invalid base64 vector encoding.", error); }
        Require(bytes.Length == byteCount, "Vector byte count mismatch.");
        var results = new List<EmbeddingResult>(count);
        foreach (var error in errors.EnumerateArray())
        {
            Require(error.ValueKind == JsonValueKind.Null || (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString())), "Invalid item error.");
            var vector = new float[VectorContract.Dimension];
            for (var column = 0; column < vector.Length; column++)
            {
                vector[column] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan((results.Count * vector.Length + column) * sizeof(float), sizeof(float)));
                Require(float.IsFinite(vector[column]), "Non-finite vector payload.");
            }
            if (error.ValueKind == JsonValueKind.Null)
            {
                VectorContract.Validate(vector);
                results.Add(new EmbeddingResult(vector));
            }
            else
            {
                Require(vector.All(value => value == 0), "Failed item must contain only the host's zero placeholder.");
                results.Add(new EmbeddingResult(null, error.GetString()));
            }
        }
        Require(Flag(root, "ok") == results.All(result => result.Error is null), "Batch status disagrees with item errors.");
        if (root.TryGetProperty("performance_profile_id", out var performance))
            Require(performance.ValueKind == JsonValueKind.Null || (performance.ValueKind == JsonValueKind.String && IsHash(performance.GetString()!)), "Invalid performance profile.");
        return results;
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static JsonElement Field(JsonElement root, string name, JsonValueKind kind)
    {
        Require(root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out _), $"Missing host field: {name}.");
        var value = root.GetProperty(name);
        Require(value.ValueKind == kind, $"Invalid host field: {name}.");
        return value;
    }
    private static string Text(JsonElement root, string name)
    {
        var text = Field(root, name, JsonValueKind.String).GetString()!;
        Require(!string.IsNullOrWhiteSpace(text), $"Empty host field: {name}.");
        return text;
    }
    private static long Number(JsonElement root, string name)
    {
        var value = Field(root, name, JsonValueKind.Number);
        Require(value.TryGetInt64(out var number), $"Invalid host integer: {name}.");
        return number;
    }
    private static bool Flag(JsonElement root, string name)
    {
        Require(root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False, $"Invalid host flag: {name}.");
        return value.GetBoolean();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private void Reset()
    {
        var old = session;
        session = null;
        old?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { await disposedCompletion.Task; return; }
        lifetime.Cancel();
        await gate.WaitAsync();
        try
        {
            if (session is not null)
            {
                using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    var id = ++nextId;
                    using var reply = await ExchangeAsync(new { id, op = "quit" }, id, quit.Token);
                    Require(Flag(reply.RootElement, "ok"), "Host rejected quit.");
                    await session.Process.WaitForExitAsync(quit.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException or InvalidOperationException)
                {
                    // Shutdown is best effort only; Reset still closes the job and kills the entire tree.
                }
            }
        }
        finally
        {
            Reset();
            gate.Release();
            disposedCompletion.TrySetResult();
        }
    }

    private sealed class Session : IDisposable
    {
        internal Process Process { get; }
        internal Stream Input => Process.StandardInput.BaseStream;
        private readonly Stream output;
        private readonly byte[] chunk = new byte[8192];
        private int position, available;
        private readonly Task stderr;
        private readonly WindowsChildJob? job;
        private readonly EventHandler onExit;

        private Session(Process process)
        {
            Process = process;
            output = process.StandardOutput.BaseStream;
            if (OperatingSystem.IsWindows()) job = WindowsChildJob.Attach(process, lowPriority: false);
            stderr = DrainAsync(process.StandardError.BaseStream);
            onExit = (_, _) => Kill();
            AppDomain.CurrentDomain.ProcessExit += onExit;
        }

        internal static Session Start(ProcessStartInfo start)
        {
            var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new IOException("Inference host did not start.");
                return new Session(process);
            }
            catch (Exception ex)
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
                if (ex is IOException) throw;
                throw new IOException($"Failed to start inference process '{start.FileName}': {ex.Message}", ex);
            }
        }

        internal async Task<JsonDocument> ReadAsync(CancellationToken token)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (position == available)
                {
                    available = await output.ReadAsync(chunk, token).AsTask().WaitAsync(token);
                    position = 0;
                    if (available == 0) throw new EndOfStreamException("Inference host stdout ended before a complete JSONL reply.");
                }
                var value = chunk[position++];
                if (value == 10) break;
                Require(line.Length < MaxLineBytes, "Inference reply exceeds 1 MiB.");
                line.WriteByte(value);
            }
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(StrictUtf8.GetString(line.GetBuffer(), 0, (int)line.Length), new JsonDocumentOptions { MaxDepth = 16 });
                Require(document.RootElement.ValueKind == JsonValueKind.Object, "Inference reply must be an object.");
                CheckDuplicates(document.RootElement);
                return document;
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException)
            {
                document?.Dispose();
                throw new InvalidDataException("Invalid inference JSONL/UTF8.", error);
            }
            catch { document?.Dispose(); throw; }
        }

        private static void CheckDuplicates(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    Require(fields.Add(property.Name), "Duplicate inference JSON field.");
                    CheckDuplicates(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
        }

        private static async Task DrainAsync(Stream stream)
        {
            var buffer = new byte[8192];
            try { while (await stream.ReadAsync(buffer) != 0) { } }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }

        private void Kill()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (OperatingSystem.IsWindows()) job?.Dispose();
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.ProcessExit -= onExit;
            Kill();
            try { Process.WaitForExit(1000); }
            catch (InvalidOperationException) { }
            Process.StandardInput.Dispose();
            Process.StandardOutput.Dispose();
            Process.StandardError.Dispose();
            // The bounded drain owns no writes and terminates on pipe close; observe any unexpected fault.
            _ = stderr.Exception;
            Process.Dispose();
        }
    }
}
