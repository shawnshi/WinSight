using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WinSight.InferenceHost;

internal sealed record EpQualificationRequest
{
    public required string Graph { get; init; }
    public required string ModelPath { get; init; }
    public required string EpName { get; init; }
    public required string DeviceType { get; init; }
    public required uint DeviceId { get; init; }
    public required uint VendorId { get; init; }
    public required string OutputName { get; init; }
    public required int? ExpectedRows { get; init; }
    public required float[] ReferenceValues { get; init; }
    public required int[] ReferenceShape { get; init; }
    public required double CpuMedianMs { get; init; }
    public required string InputsPath { get; init; }
    public bool ProviderInstallAllowed { get; init; }
}

internal sealed record EpQualificationResult
{
    public required bool Qualified { get; init; }
    public required string Reason { get; init; }
    public double EpMedianMs { get; init; }
    public double CpuMedianMs { get; init; }
    public double Cosine { get; init; }
}

internal sealed class TensorDescriptor
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required int[] Dimensions { get; init; }
    public required long ByteOffset { get; init; }
    public required long ByteLength { get; init; }
}

internal sealed class TensorFileHeader
{
    public required TensorDescriptor[] Tensors { get; init; }
}

internal static class EpQualificationChild
{
    internal const long MaxTensorFileBytes = 8 * 1024 * 1024; // 8 MiB

    internal static void WriteTensorFile(string path, NamedOnnxValue[] inputs)
    {
        var descriptors = new List<TensorDescriptor>();
        long currentOffset = 0;
        foreach (var input in inputs)
        {
            string dataType;
            int[] dims;
            long byteLength;
            switch (input.Value)
            {
                case Tensor<float> f:
                    dataType = "float";
                    dims = f.Dimensions.ToArray();
                    byteLength = (long)f.Length * sizeof(float);
                    break;
                case Tensor<long> l:
                    dataType = "int64";
                    dims = l.Dimensions.ToArray();
                    byteLength = (long)l.Length * sizeof(long);
                    break;
                case Tensor<bool> b:
                    dataType = "bool";
                    dims = b.Dimensions.ToArray();
                    byteLength = (long)b.Length * sizeof(bool);
                    break;
                default:
                    throw new ArgumentException($"Unsupported tensor value type: {input.Value.GetType()}");
            }
            descriptors.Add(new TensorDescriptor
            {
                Name = input.Name,
                DataType = dataType,
                Dimensions = dims,
                ByteOffset = currentOffset,
                ByteLength = byteLength
            });
            currentOffset += byteLength;
        }

        var header = new TensorFileHeader { Tensors = descriptors.ToArray() };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, new JsonSerializerOptions { WriteIndented = false });
        long totalFileLength = 4L + headerBytes.Length + currentOffset;

        if (totalFileLength > MaxTensorFileBytes)
            throw new InvalidOperationException($"probe too large: total size {totalFileLength} exceeds {MaxTensorFileBytes}");

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(file);
        writer.Write(headerBytes.Length);
        writer.Write(headerBytes);

        foreach (var input in inputs)
        {
            switch (input.Value)
            {
                case Tensor<float> f:
                    var fArray = f.ToArray();
                    var fBytes = MemoryMarshal.AsBytes<float>(fArray);
                    file.Write(fBytes);
                    break;
                case Tensor<long> l:
                    var lArray = l.ToArray();
                    var lBytes = MemoryMarshal.AsBytes<long>(lArray);
                    file.Write(lBytes);
                    break;
                case Tensor<bool> b:
                    var bArray = b.ToArray();
                    var bBytes = MemoryMarshal.AsBytes<bool>(bArray);
                    file.Write(bBytes);
                    break;
            }
        }
    }

    internal static NamedOnnxValue[] ReadTensorFile(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length > MaxTensorFileBytes)
            throw new InvalidOperationException($"probe too large or missing: size {fileInfo?.Length ?? 0}");

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(file);
        int headerLength = reader.ReadInt32();
        if (headerLength <= 0 || headerLength > 1024 * 1024)
            throw new InvalidDataException($"Invalid tensor header length: {headerLength}");

        var headerBytes = reader.ReadBytes(headerLength);
        var header = JsonSerializer.Deserialize<TensorFileHeader>(headerBytes)
            ?? throw new InvalidDataException("Invalid tensor header JSON");

        long binaryStart = file.Position;
        var result = new NamedOnnxValue[header.Tensors.Length];

        for (int i = 0; i < header.Tensors.Length; i++)
        {
            var desc = header.Tensors[i];
            file.Seek(binaryStart + desc.ByteOffset, SeekOrigin.Begin);
            var bytes = reader.ReadBytes(checked((int)desc.ByteLength));
            if (bytes.Length != desc.ByteLength)
                throw new EndOfStreamException($"Premature end of file reading tensor {desc.Name}");

            NamedOnnxValue value = desc.DataType switch
            {
                "float" => NamedOnnxValue.CreateFromTensor(desc.Name,
                    new DenseTensor<float>(MemoryMarshal.Cast<byte, float>(bytes).ToArray(), desc.Dimensions)),
                "int64" => NamedOnnxValue.CreateFromTensor(desc.Name,
                    new DenseTensor<long>(MemoryMarshal.Cast<byte, long>(bytes).ToArray(), desc.Dimensions)),
                "bool" => NamedOnnxValue.CreateFromTensor(desc.Name,
                    new DenseTensor<bool>(MemoryMarshal.Cast<byte, bool>(bytes).ToArray(), desc.Dimensions)),
                _ => throw new InvalidDataException($"Unknown tensor data type: {desc.DataType}")
            };
            result[i] = value;
        }

        return result;
    }

    internal static (bool Equivalent, double Cosine) ComputeEquivalence(GraphResult cpu, GraphResult other)
    {
        if (!cpu.Shape.SequenceEqual(other.Shape) || cpu.Values.Length == 0 || cpu.Values.Length != other.Values.Length)
            return (false, 0);
        double dot = 0, a = 0, b = 0, difference = 0;
        for (var i = 0; i < cpu.Values.Length; i++)
        {
            if (!float.IsFinite(cpu.Values[i]) || !float.IsFinite(other.Values[i])) return (false, 0);
            dot += (double)cpu.Values[i] * other.Values[i];
            a += (double)cpu.Values[i] * cpu.Values[i];
            b += (double)other.Values[i] * other.Values[i];
            var delta = (double)cpu.Values[i] - other.Values[i];
            difference += delta * delta;
        }
        if (a == 0 || b == 0)
        {
            var seq = cpu.Values.SequenceEqual(other.Values);
            return (seq, seq ? 1.0 : 0.0);
        }
        var cosine = dot / Math.Sqrt(a * b);
        var diffRatio = Math.Sqrt(difference / a);
        var ok = cosine >= .999 && diffRatio <= .01;
        return (ok, cosine);
    }

    internal static bool ValidResult(string graph, GraphResult result, int? expectedRows)
    {
        if (!result.Values.All(float.IsFinite)) return false;
        if (graph == "model_q4.onnx")
            return result.Shape.SequenceEqual(new[] { 1, 768 })
                && Math.Abs(Math.Sqrt(result.Values.Sum(value => (double)value * value)) - 1) <= .001;
        return result.Shape.Length == 2 && result.Shape[0] > 0 && result.Shape[1] == 512
            && (!expectedRows.HasValue || result.Shape[0] == expectedRows.Value);
    }

    internal static bool HasComputePlacement(string trace, string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(trace));
        foreach (var node in document.RootElement.EnumerateArray())
        {
            if (!node.TryGetProperty("args", out var args) || !args.TryGetProperty("provider", out var provider)
                || !args.TryGetProperty("op_name", out var operation)) continue;
            var providerStr = provider.GetString() ?? "";
            if (!MatchesProvider(providerStr, name)) continue;
            var op = operation.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(op) && !new[] { "Memcpy", "Identity", "Cast", "Shape", "Reshape", "Transpose", "Squeeze", "Unsqueeze", "Gather" }
                .Any(prefix => op.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    internal static bool MatchesProvider(string traceProvider, string targetEp)
    {
        if (string.Equals(traceProvider, targetEp, StringComparison.OrdinalIgnoreCase)) return true;
        if ((targetEp.Contains("DirectML", StringComparison.OrdinalIgnoreCase) || targetEp.Contains("Dml", StringComparison.OrdinalIgnoreCase))
            && (traceProvider.Contains("DirectML", StringComparison.OrdinalIgnoreCase) || traceProvider.Contains("Dml", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    internal static GraphResult Execute(InferenceSession session, NamedOnnxValue[] inputs, string output, RunOptions? options = null)
    {
        using var results = options is null ? session.Run(inputs, new[] { output }) : session.Run(inputs, new[] { output }, options);
        var tensor = results.Single().AsTensor<float>();
        return new GraphResult(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    internal static int Run(string requestPath, string resultPath)
    {
        string tempProfileDir = Path.Combine(Path.GetTempPath(), "WinSight-ep-child-" + Guid.NewGuid().ToString("N"));
        InferenceSession? session = null;
        EpQualificationRequest? request = null;
        try
        {
            if (!File.Exists(requestPath))
            {
                WriteResult(resultPath, new EpQualificationResult { Qualified = false, Reason = "Request file not found" });
                return 1;
            }

            var requestJson = File.ReadAllText(requestPath);
            request = JsonSerializer.Deserialize<EpQualificationRequest>(requestJson)
                ?? throw new InvalidDataException("Invalid qualification request JSON");

            if (!File.Exists(request.InputsPath))
            {
                WriteResult(resultPath, new EpQualificationResult { Qualified = false, Reason = "Inputs file not found" });
                return 1;
            }

            var inputs = ReadTensorFile(request.InputsPath);
            var reference = new GraphResult(request.ReferenceValues, request.ReferenceShape);

            var ort = OrtEnv.Instance();
            // Registration is per process: the parent registered the provider in its own OrtEnv, so this
            // child must register it again before it can see the device. Register-only: never download here.
            var childCatalogNote = "catalog unavailable";
            try
            {
                var catalog = Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();
                if (catalog is null) childCatalogNote = "catalog unavailable";
                else
                {
                    var registered = catalog.RegisterCertifiedAsync().AsTask().GetAwaiter().GetResult();
                    childCatalogNote = "RegisterCertifiedAsync reported " + registered.Count + " provider(s)";
                    // Uncensored providers (for example an uncertified vendor EP) are not covered by the
                    // catalog call, so register every ready library explicitly; duplicates are idempotent.
                    foreach (var provider in catalog.FindAllProviders())
                    {
                        if (provider.ReadyState != Microsoft.Windows.AI.MachineLearning.ExecutionProviderReadyState.Ready) continue;
                        if (string.IsNullOrWhiteSpace(provider.LibraryPath)) continue;
                        try { OrtEnv.Instance().RegisterExecutionProviderLibrary(provider.Name, provider.LibraryPath); }
                        catch (Exception inner) when (inner.Message.Contains("already registered", StringComparison.OrdinalIgnoreCase)) { }
                        catch (Exception inner) { childCatalogNote += $"; {provider.Name} register failed: {inner.GetType().Name}"; }
                    }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                childCatalogNote = "catalog registration failed: " + error.GetType().Name + ": " + error.Message;
            }
            var devices = ort.GetEpDevices();
            OrtEpDevice? matchedDevice = null;
            foreach (var d in devices)
            {
                if (string.Equals(d.EpName, request.EpName, StringComparison.OrdinalIgnoreCase))
                {
                    if (d.HardwareDevice.DeviceId == request.DeviceId && d.HardwareDevice.VendorId == request.VendorId)
                    {
                        matchedDevice = d;
                        break;
                    }
                    matchedDevice ??= d;
                }
            }

            if (matchedDevice is null)
            {
                WriteResult(resultPath, new EpQualificationResult
                {
                    Qualified = false,
                    Reason = $"Target EP device not found in OrtEnv: {request.EpName} ({childCatalogNote})"
                });
                return 0;
            }

            Directory.CreateDirectory(tempProfileDir);
            using var options = ModelProbe.CpuOptions();
            options.AppendExecutionProvider(ort, new[] { matchedDevice }, new Dictionary<string, string>());
            options.ProfileOutputPathPrefix = Path.Combine(tempProfileDir, "nodes");
            options.EnableProfiling = true;

            session = new InferenceSession(request.ModelPath, options);

            using var runOptions = new RunOptions();
            var output = Execute(session, inputs, request.OutputName, runOptions);

            if (!ValidResult(request.Graph, output, request.ExpectedRows))
            {
                WriteResult(resultPath, new EpQualificationResult
                {
                    Qualified = false,
                    Reason = "EP output contract failed"
                });
                return 0;
            }

            var (equivalent, cosine) = ComputeEquivalence(reference, output);
            if (!equivalent)
            {
                WriteResult(resultPath, new EpQualificationResult
                {
                    Qualified = false,
                    Reason = $"CPU numerical/shape/output agreement failed (cosine={cosine:F4})",
                    Cosine = cosine
                });
                return 0;
            }

            var trace = session.EndProfiling();
            var traceFile = new FileInfo(trace);
            if (!traceFile.Exists || traceFile.Length > 32 * 1024 * 1024 || !HasComputePlacement(trace, matchedDevice.EpName))
            {
                WriteResult(resultPath, new EpQualificationResult
                {
                    Qualified = false,
                    Reason = "No target-EP compute-node execution evidence",
                    Cosine = cosine
                });
                return 0;
            }

            // Warm 3 runs for median
            var values = new double[3];
            for (var index = 0; index < values.Length; index++)
            {
                var watch = Stopwatch.StartNew();
                var warmOutput = Execute(session, inputs, request.OutputName, runOptions);
                if (!ValidResult(request.Graph, warmOutput, request.ExpectedRows) || !ComputeEquivalence(reference, warmOutput).Equivalent)
                {
                    WriteResult(resultPath, new EpQualificationResult
                    {
                        Qualified = false,
                        Reason = "Repeated model agreement failed",
                        Cosine = cosine
                    });
                    return 0;
                }
                values[index] = watch.Elapsed.TotalMilliseconds;
            }
            Array.Sort(values);
            var epMedian = values[1];

            if (epMedian >= request.CpuMedianMs * 0.95)
            {
                WriteResult(resultPath, new EpQualificationResult
                {
                    Qualified = false,
                    Reason = $"Warm execution did not improve CPU median by 5% (CPU={request.CpuMedianMs:F3}ms, EP={epMedian:F3}ms)",
                    EpMedianMs = epMedian,
                    CpuMedianMs = request.CpuMedianMs,
                    Cosine = cosine
                });
                return 0;
            }

            WriteResult(resultPath, new EpQualificationResult
            {
                Qualified = true,
                Reason = "ok",
                EpMedianMs = epMedian,
                CpuMedianMs = request.CpuMedianMs,
                Cosine = cosine
            });
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                WriteResult(resultPath, new EpQualificationResult
                {
                    Qualified = false,
                    Reason = $"{ex.GetType().Name}: {ex.Message}",
                    CpuMedianMs = request?.CpuMedianMs ?? 0
                });
            }
            catch { }
            return 0;
        }
        finally
        {
            session?.Dispose();
            try
            {
                if (Directory.Exists(tempProfileDir))
                    Directory.Delete(tempProfileDir, true);
            }
            catch { }
        }
    }

    private static void WriteResult(string resultPath, EpQualificationResult result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(resultPath, json);
    }
}
