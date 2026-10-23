using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Windows.AI.MachineLearning;

namespace WinSight.InferenceHost;

internal sealed record GraphResult(float[] Values, int[] Shape);

// The catalog establishes device applicability, not model suitability. CPU owns service until each graph qualifies.
internal sealed class ExecutionProviderManager : IDisposable
{
    private sealed class Slot(Func<InferenceSession> create)
    {
        internal readonly object Gate = new();
        internal readonly Lazy<InferenceSession> Cpu = new(create);
        internal InferenceSession? Active;
        internal string ActiveName = "CPUExecutionProvider";
        internal string LastRunName = "CPUExecutionProvider";
        internal string? ActiveSignature;
        internal bool Observed;
    }
    private sealed record Probe(string Graph, NamedOnnxValue[] Inputs, string Output, GraphResult Reference, int? ExpectedRows, string Signature);
    private readonly ModelManifest models;
    private readonly Dictionary<string, Slot> slots;
    private readonly BlockingCollection<Probe> probes = new(3);
    private readonly ConcurrentDictionary<string, string> states = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Task background;
    private long epoch;
    internal long Epoch => Interlocked.Read(ref epoch);
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "OpenVINOExecutionProvider", "VitisAIExecutionProvider", "MIGraphXExecutionProvider",
        "QNNExecutionProvider", "NvTensorRtRtxExecutionProvider"
    };

    internal ExecutionProviderManager(ModelManifest models, bool automatic)
    {
        this.models = models;
        slots = new[] { "model_q4.onnx", "vision_encoder_q4.onnx", "audio_encoder_q4.onnx" }
            .ToDictionary(graph => graph, graph => new Slot(() =>
            {
                using var options = ModelProbe.CpuOptions();
                return new InferenceSession(models.Graph(graph), options);
            }));
        if (automatic && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            background = Task.Run(PrepareAndValidateAsync);
        else
        {
            states["catalog"] = automatic ? "CPU: dynamic catalog EPs require Windows 11 24H2+" : "CPU-only preference";
            background = Task.CompletedTask;
        }
    }

    internal bool Loaded(string graph) => slots[graph].Cpu.IsValueCreated;
    internal object Status => new
    {
        epoch = Epoch,
        providers = states.OrderBy(pair => pair.Key).Select(pair => new { name = pair.Key, state = pair.Value }).ToArray(),
        graphs = slots.OrderBy(pair => pair.Key).Select(pair =>
        {
            lock (pair.Value.Gate) return new
            {
                graph = pair.Key, eligible_candidate = pair.Value.ActiveName,
                last_execution = pair.Value.LastRunName, qualified_input_contract = pair.Value.ActiveSignature
            };
        }).ToArray()
    };
    // WML-EP-002: bind both snapshot reads to the caller\'s batch epoch, not just to each other.
    internal string? PerformanceIdentity(string embeddingProfile, long? expectedEpoch = null)
    {
        var before = Epoch;
        if (expectedEpoch.HasValue && expectedEpoch.Value != before) return null;
        var names = slots.OrderBy(pair => pair.Key).Select(pair =>
        {
            lock (pair.Value.Gate) return pair.Key + "=" + pair.Value.LastRunName;
        });
        var value = ModelManifest.Hash(System.Text.Encoding.UTF8.GetBytes("performance-v2\n" + embeddingProfile
            + "\nepoch=" + before + "\n" + string.Join("\n", names)));
        return before == Epoch ? value : null;
    }

    internal GraphResult Run(string graph, NamedOnnxValue[] inputs, string? output = null, int? expectedRows = null)
    {
        var slot = slots[graph];
        var signature = Signature(inputs, expectedRows);
        GraphResult result;
        lock (slot.Gate)
        {
            var cpu = slot.Cpu.Value;
            output ??= cpu.OutputMetadata.Keys.Single();
            // Qualification covers this observed modality/shape/output contract, not unseen input families.
            if (slot.Active is not null && slot.ActiveSignature == signature)
            {
                try
                {
                    var accelerated = Execute(slot.Active, inputs, output);
                    if (!ValidResult(graph, accelerated, expectedRows)) throw new InvalidDataException("EP output contract failed");
                    StampRoute(slot, slot.ActiveName);
                    return accelerated;
                }
                catch (Exception error) when (error is OnnxRuntimeException or InvalidDataException)
                {
                    states[graph] = "CPU fallback: " + error.GetType().Name + ": " + error.Message;
                    slot.Active.Dispose();
                    slot.Active = null;
                    slot.ActiveName = "CPUExecutionProvider";
                    slot.ActiveSignature = null;
                    Interlocked.Increment(ref epoch);
                }
            }
            // WML-EP-003: count the attempted route even when execution/output validation fails.
            StampRoute(slot, "CPUExecutionProvider");
            result = Execute(cpu, inputs, output);
            // WML-EP-001: CPU retry and candidate use the same complete, input-dependent output contract.
            if (!ValidResult(graph, result, expectedRows)) throw new InvalidDataException("CPU output contract failed");
            if (!slot.Observed && !background.IsCompleted)
            {
                slot.Observed = true;
                try
                {
                    var copy = inputs.Select(Clone).ToArray();
                    if (!probes.TryAdd(new Probe(graph, copy, output, result, expectedRows, signature)))
                        states[graph] = "CPU: qualification queue full";
                }
                catch (ArgumentException error) { states[graph] = "CPU: sample unavailable: " + error.Message; }
            }
        }
        return result;
    }

    private async Task PrepareAndValidateAsync()
    {
        try
        {
            var catalog = ExecutionProviderCatalog.GetDefault()
                ?? throw new InvalidOperationException("Windows ML EP catalog unavailable");
            // FindAllProviders is device-filtered by Windows ML, including compatible not-yet-installed EPs.
            foreach (var provider in catalog.FindAllProviders().Where(provider => Allowed.Contains(provider.Name)))
            {
                if (stopping.IsCancellationRequested) return;
                if (provider.Name == "QNNExecutionProvider" && RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                {
                    states[provider.Name] = "requires native ARM64 host/tokenizer; x64 emulation is not QNN qualification";
                    continue;
                }
                try
                {
                    states[provider.Name] = "preparing: " + provider.ReadyState;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    var operation = provider.EnsureReadyAsync();
                    operation.Progress = (_, percent) => states[provider.Name] = $"preparing {percent:F0}%";
                    using var cancellation = timeout.Token.Register(() => operation.Cancel());
                    var prepared = await operation;
                    if (prepared.Status != ExecutionProviderReadyResultState.Success)
                        throw new InvalidOperationException($"EP preparation {prepared.Status}: {prepared.ExtendedError}");
                    if (!provider.TryRegister()) throw new InvalidOperationException("TryRegister returned false");
                    states[provider.Name] = "registered; real-model qualification pending";
                }
                catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
                {
                    states[provider.Name] = $"unavailable: {error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";
                }
            }
            foreach (var probe in probes.GetConsumingEnumerable(stopping.Token))
            {
                // Re-enumerate after registration; never assume a cached device survives driver/catalog changes.
                var devices = OrtEnv.Instance().GetEpDevices().Where(device => Allowed.Contains(device.EpName)
                    && (device.EpName != "QNNExecutionProvider" || RuntimeInformation.ProcessArchitecture == Architecture.Arm64))
                    .OrderBy(device => device.HardwareDevice.Type == OrtHardwareDeviceType.NPU ? 0
                        : device.HardwareDevice.Type == OrtHardwareDeviceType.GPU ? 1 : 2)
                    .Take(8).ToArray();
                foreach (var device in devices)
                {
                    if (stopping.IsCancellationRequested) return;
                    if (Qualify(probe, device)) break;
                }
                if (devices.Length == 0) states[probe.Graph] = "CPU: no applicable registered candidate device";
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            states["background"] = $"CPU retained: {error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";
        }
    }

    private bool Qualify(Probe probe, OrtEpDevice device)
    {
        var slot = slots[probe.Graph];
        var label = device.EpName + ":" + device.HardwareDevice.Type + ":" + device.HardwareDevice.DeviceId;
        var directory = Path.Combine(Path.GetTempPath(), "WinSight-ep-" + Guid.NewGuid().ToString("N"));
        InferenceSession? candidate = null;
        try
        {
            Directory.CreateDirectory(directory);
            using var options = ModelProbe.CpuOptions();
            options.AppendExecutionProvider(OrtEnv.Instance(), new[] { device }, new Dictionary<string, string>());
            options.ProfileOutputPathPrefix = Path.Combine(directory, "nodes");
            options.EnableProfiling = true;
            states[probe.Graph] = "qualifying " + label;
            candidate = new InferenceSession(models.Graph(probe.Graph), options);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var runOptions = new RunOptions();
            using var cancellation = timeout.Token.Register(() => runOptions.Terminate = true);
            var output = Execute(candidate, probe.Inputs, probe.Output, runOptions);
            if (!ValidResult(probe.Graph, output, probe.ExpectedRows) || !Equivalent(probe.Reference, output))
                throw new InvalidDataException("CPU numerical/shape/output agreement failed");
            var trace = candidate.EndProfiling();
            var traceFile = new FileInfo(trace);
            if (!traceFile.Exists || traceFile.Length > 32 * 1024 * 1024 || !HasComputePlacement(trace, device.EpName))
                throw new InvalidDataException("No target-EP compute-node execution evidence");
            // CPU InferenceSession supports concurrent Run; background qualification never holds the foreground gate.
            var cpuTime = Median(slot.Cpu.Value, probe, runOptions);
            var epTime = Median(candidate, probe, runOptions);
            if (epTime >= cpuTime * 0.95) throw new InvalidDataException("Warm execution did not improve CPU median by 5%");
            timeout.Token.ThrowIfCancellationRequested();
            lock (slot.Gate)
            {
                if (stopping.IsCancellationRequested) return false;
                slot.Active = candidate;
                slot.ActiveName = label;
                slot.ActiveSignature = probe.Signature;
                candidate = null;
                Interlocked.Increment(ref epoch);
                states[probe.Graph] = $"eligible {label} for observed input contract; cosine>=0.999; warm median CPU={cpuTime:F3}ms EP={epTime:F3}ms";
            }
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            states[probe.Graph + "/" + label] = $"rejected: {error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";
            return false;
        }
        finally
        {
            candidate?.Dispose();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException error) { Console.Error.WriteLine("EP profile cleanup: " + error.Message); }
        }
    }

    private static double Median(InferenceSession session, Probe probe, RunOptions runOptions)
    {
        _ = Execute(session, probe.Inputs, probe.Output, runOptions); // Both sides warm the same real input.
        var values = new double[3];
        for (var index = 0; index < values.Length; index++)
        {
            var watch = Stopwatch.StartNew();
            var output = Execute(session, probe.Inputs, probe.Output, runOptions);
            if (!ValidResult(probe.Graph, output, probe.ExpectedRows) || !Equivalent(probe.Reference, output))
                throw new InvalidDataException("Repeated model agreement failed");
            values[index] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(values);
        return values[1];
    }

    private static bool ValidResult(string graph, GraphResult result, int? expectedRows)
    {
        if (!result.Values.All(float.IsFinite)) return false;
        if (graph == "model_q4.onnx")
            return result.Shape.SequenceEqual(new[] { 1, 768 })
                && Math.Abs(Math.Sqrt(result.Values.Sum(value => (double)value * value)) - 1) <= .001;
        return result.Shape.Length == 2 && result.Shape[0] > 0 && result.Shape[1] == 512
            && (!expectedRows.HasValue || result.Shape[0] == expectedRows.Value);
    }

    private void StampRoute(Slot slot, string name)
    {
        if (slot.LastRunName == name) return;
        slot.LastRunName = name;
        Interlocked.Increment(ref epoch);
    }

    private static string Signature(NamedOnnxValue[] inputs, int? rows) => string.Join(";", inputs.Select(input =>
        input.Name + "=" + (input.Value switch
        {
            Tensor<float> tensor => "f:" + string.Join(",", tensor.Dimensions.ToArray()),
            Tensor<long> tensor => "i:" + string.Join(",", tensor.Dimensions.ToArray()),
            Tensor<bool> tensor => "b:" + string.Join(",", tensor.Dimensions.ToArray()),
            _ => throw new ArgumentException("unsupported graph input type")
        }))) + ";expectedRows=" + rows;

    private static bool Equivalent(GraphResult cpu, GraphResult other)
    {
        if (!cpu.Shape.SequenceEqual(other.Shape) || cpu.Values.Length == 0 || cpu.Values.Length != other.Values.Length) return false;
        double dot = 0, a = 0, b = 0, difference = 0;
        for (var i = 0; i < cpu.Values.Length; i++)
        {
            if (!float.IsFinite(cpu.Values[i]) || !float.IsFinite(other.Values[i])) return false;
            dot += (double)cpu.Values[i] * other.Values[i];
            a += (double)cpu.Values[i] * cpu.Values[i];
            b += (double)other.Values[i] * other.Values[i];
            var delta = (double)cpu.Values[i] - other.Values[i];
            difference += delta * delta;
        }
        return a == 0 || b == 0 ? cpu.Values.SequenceEqual(other.Values)
            : dot / Math.Sqrt(a * b) >= .999 && Math.Sqrt(difference / a) <= .01;
    }

    private static bool HasComputePlacement(string trace, string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(trace));
        foreach (var node in document.RootElement.EnumerateArray())
        {
            if (!node.TryGetProperty("args", out var args) || !args.TryGetProperty("provider", out var provider)
                || provider.GetString() != name || !args.TryGetProperty("op_name", out var operation)) continue;
            var op = operation.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(op) && !new[] { "Memcpy", "Identity", "Cast", "Shape", "Reshape", "Transpose", "Squeeze", "Unsqueeze", "Gather" }
                .Any(prefix => op.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static NamedOnnxValue Clone(NamedOnnxValue value) => value.Value switch
    {
        Tensor<float> tensor when tensor.Length <= 8_000_000 => NamedOnnxValue.CreateFromTensor(value.Name, new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray())),
        Tensor<long> tensor when tensor.Length <= 8_000_000 => NamedOnnxValue.CreateFromTensor(value.Name, new DenseTensor<long>(tensor.ToArray(), tensor.Dimensions.ToArray())),
        Tensor<bool> tensor when tensor.Length <= 8_000_000 => NamedOnnxValue.CreateFromTensor(value.Name, new DenseTensor<bool>(tensor.ToArray(), tensor.Dimensions.ToArray())),
        _ => throw new ArgumentException("unsupported or oversized qualification sample tensor")
    };

    private static GraphResult Execute(InferenceSession session, NamedOnnxValue[] inputs, string output, RunOptions? options = null)
    {
        using var results = options is null ? session.Run(inputs, new[] { output }) : session.Run(inputs, new[] { output }, options);
        var tensor = results.Single().AsTensor<float>();
        return new GraphResult(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    public void Dispose()
    {
        stopping.Cancel();
        probes.CompleteAdding();
        // Join before disposing sessions/model read locks; parent process timeout is the hard stop for a stuck native EP.
        background.GetAwaiter().GetResult();
        foreach (var slot in slots.Values)
        {
            lock (slot.Gate)
            {
                slot.Active?.Dispose();
                if (slot.Cpu.IsValueCreated) slot.Cpu.Value.Dispose();
            }
        }
        probes.Dispose();
        stopping.Dispose();
    }
}
