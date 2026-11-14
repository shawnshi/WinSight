using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WinSight.InferenceHost;

internal sealed record GraphResult(float[] Values, int[] Shape);

internal sealed class EpStatus
{
    public required string Name { get; init; }
    public required string State { get; set; }
    public required string InstallState { get; set; }
    public string? ReadyState { get; set; }
    public string? LibraryPath { get; set; }
}

internal sealed record CandidateDeviceInfo(
    string Ep,
    string Type,
    bool Discrete,
    uint VendorId,
    uint DeviceId,
    string Confidence,
    int Rank
);

// The catalog establishes device applicability, not model suitability. CPU owns service until each graph qualifies.
internal sealed class ExecutionProviderManager : IDisposable
{
    private sealed class Slot(Func<InferenceSession> create)
    {
        internal readonly object Gate = new();
        internal readonly Lazy<InferenceSession> Cpu = new(create);
        internal InferenceSession? Active;
        internal Func<InferenceSession>? Initializer;
        internal Func<InferenceSession>? ReInitFactory;
        internal string ActiveName = "CPUExecutionProvider";
        internal string LastRunName = "CPUExecutionProvider";
        internal string? ActiveSignature;
        internal DateTime LastUsedUtc;
        internal bool Observed;
    }
    private sealed record Probe(string Graph, NamedOnnxValue[] Inputs, string Output, GraphResult Reference, int? ExpectedRows, string Signature);
    private readonly ModelManifest models;
    private readonly string preference;
    private readonly Dictionary<string, Slot> slots;
    private readonly BlockingCollection<Probe> probes = new(3);
    private readonly ConcurrentDictionary<string, string> states = new();
    private readonly ConcurrentDictionary<string, EpStatus> epStatuses = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task background;
    private readonly Timer idleEvictionTimer;
    private long epoch;
    internal long Epoch => Interlocked.Read(ref epoch);
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "OpenVINOExecutionProvider", "VitisAIExecutionProvider", "MIGraphXExecutionProvider",
        "QNNExecutionProvider", "NvTensorRtRtxExecutionProvider", "DirectMLExecutionProvider",
        "DmlExecutionProvider", "RyzenAILightExecutionProvider", "RyzenAIExecutionProvider"
    };
    // Re-enabled after qualification moved to a child process: a faulting provider is now rejected, not fatal.
    // Kept as a named entry so it can be withdrawn again without touching the candidate logic.

    internal static string? ChildProcessOverridePath { get; set; }
    internal static TimeSpan? ChildTimeoutOverride { get; set; }

    static ExecutionProviderManager()
    {
        var envOverride = Environment.GetEnvironmentVariable("WINSIGHT_TEST_QUALIFY_CHILD");
        if (!string.IsNullOrWhiteSpace(envOverride))
            ChildProcessOverridePath = envOverride;

        var timeoutEnv = Environment.GetEnvironmentVariable("WINSIGHT_TEST_QUALIFY_TIMEOUT_MS");
        if (int.TryParse(timeoutEnv, out var ms) && ms > 0)
            ChildTimeoutOverride = TimeSpan.FromMilliseconds(ms);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryLimit;
        public UIntPtr PeakJobMemoryLimit;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    internal ExecutionProviderManager(ModelManifest models, string preference = "auto")
    {
        this.models = models;
        this.preference = preference;

        InitializeCatalog();
        DiscoverInitialProviders();

        var graphList = models.IsRyzenAI
            ? new[] { "model_text_s256.onnx", "vision_encoder_static_140.onnx", "audio_encoder_10s.onnx", "model_image_s144.onnx", "model_audio_10s.onnx" }
            : models.IsIntelOpenVINO
            ? new[] { "model_text_d256.onnx", "model_text_q32.onnx", "vision_encoder_static_140.onnx", "audio_encoder_10s.onnx", "model_image_140.onnx", "model_audio_10s.onnx" }.Where(g => File.Exists(models.Graph(g))).ToArray()
            : new[] { "model_q4.onnx", "vision_encoder_q4.onnx", "audio_encoder_q4.onnx" };

        slots = graphList
            .ToDictionary(graph => graph, graph => new Slot(() =>
            {
                using var options = ModelProbe.CpuOptions();
                return new InferenceSession(models.Graph(graph), options);
            }));

        if (models.IsRyzenAI && preference != "cpu")
        {
            InitializeRyzenAiAcceleration();
            background = Task.CompletedTask;
        }
        else if (models.IsIntelOpenVINO && preference != "cpu")
        {
            InitializeIntelOpenVinoAcceleration();
            background = Task.CompletedTask;
        }
        else if (preference != "cpu")
            background = Task.Run(PrepareAndValidateAsync);
        else
        {
            states["preference"] = "CPU-only preference";
            background = Task.CompletedTask;
        }
        idleEvictionTimer = new Timer(_ =>
        {
            try { TrimIdleSessions(TimeSpan.FromMinutes(5)); } catch { }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private static void EnsureVitisAiRegistered(OrtEnv ort)
    {
        try
        {
            var targetDir = @"C:\ProgramData\RyzenAI\vitisai_ep";
            var targetDll = Path.Combine(targetDir, "onnxruntime_providers_vitisai.dll");
            if (!File.Exists(targetDll))
            {
                var winApps = @"C:\Program Files\WindowsApps";
                if (Directory.Exists(winApps))
                {
                    var match = Directory.GetDirectories(winApps, "MicrosoftCorporationII.WinML.AMD.NPU.EP*").FirstOrDefault();
                    if (match != null)
                    {
                        var epSrc = Path.Combine(match, "ExecutionProvider");
                        if (Directory.Exists(epSrc))
                        {
                            Directory.CreateDirectory(targetDir);
                            foreach (var file in Directory.GetFiles(epSrc))
                            {
                                var dest = Path.Combine(targetDir, Path.GetFileName(file));
                                if (!File.Exists(dest)) File.Copy(file, dest, true);
                            }
                        }
                    }
                }
            }

            if (File.Exists(targetDll))
            {
                try
                {
                    ort.RegisterExecutionProviderLibrary("VitisAIExecutionProvider", targetDll);
                }
                catch (Exception ex) when (ex.Message.Contains("already registered", StringComparison.OrdinalIgnoreCase)) { }
            }
        }
        catch { /* Non-blocking: will fall back to catalog or DirectML */ }
    }

    private void InitializeRyzenAiAcceleration()
    {
        if (!models.IsRyzenAI) return;
        var ort = OrtEnv.Instance();
        var devices = ort.GetEpDevices();
        var dmlDevice = devices.FirstOrDefault(d => d.EpName.Contains("Dml", StringComparison.OrdinalIgnoreCase));
        var vitisDevice = devices.FirstOrDefault(d => d.EpName.Contains("VitisAI", StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.EpName.Contains("RyzenAI", StringComparison.OrdinalIgnoreCase));
        if (vitisDevice is null)
        {
            EnsureVitisAiRegistered(ort);
            devices = ort.GetEpDevices();
            vitisDevice = devices.FirstOrDefault(d => d.EpName.Contains("VitisAI", StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => d.EpName.Contains("RyzenAI", StringComparison.OrdinalIgnoreCase));
        }

        // 1. GPU (DirectML) 承接: 文本主干立即加载 (保持搜索零延迟)，图像融合与音频融合按需惰性加载 (释放 3GB+ 显存与内存)
        if (dmlDevice != null)
        {
            // 文本主干立即就绪
            if (slots.TryGetValue("model_text_s256.onnx", out var textSlot))
            {
                try
                {
                    using var dmlOpts = ModelProbe.AcceleratorOptions();
                    dmlOpts.AppendExecutionProvider(ort, new[] { dmlDevice }, new Dictionary<string, string>());
                    var session = new InferenceSession(models.Graph("model_text_s256.onnx"), dmlOpts);
                    textSlot.Active = session;
                    textSlot.ActiveName = "DirectML (GPU: " + dmlDevice.HardwareDevice.DeviceId + ")";
                    textSlot.LastRunName = textSlot.ActiveName;
                    states["model_text_s256.onnx"] = "eligible " + textSlot.ActiveName + " [Strategy B]";
                }
                catch (Exception ex)
                {
                    states["model_text_s256.onnx"] = "DML init fallback to CPU: " + ex.Message;
                }
            }

            // 图像融合图与音频融合图按需惰性实例化
            foreach (var graph in new[] { "model_image_s144.onnx", "model_audio_10s.onnx" })
            {
                if (!slots.TryGetValue(graph, out var slot)) continue;
                slot.ActiveName = "DirectML (GPU: " + dmlDevice.HardwareDevice.DeviceId + ")";
                slot.ReInitFactory = slot.Initializer = () =>
                {
                    using var dmlOpts = ModelProbe.AcceleratorOptions();
                    dmlOpts.AppendExecutionProvider(ort, new[] { dmlDevice }, new Dictionary<string, string>());
                    return new InferenceSession(models.Graph(graph), dmlOpts);
                };
                states[graph] = "eligible " + slot.ActiveName + " [Lazy Strategy B]";
            }
        }

        // 2. NPU (VitisAI) 承接: 静态视觉编码器（按需惰性绑定 eg2_vis_140.rai 微码，避免空载常驻）
        var raiPath = Path.Combine(models.VaipCacheDir, "eg2_vis_140", "eg2_vis_140.rai");
        if (File.Exists(raiPath))
        {
            if (slots.TryGetValue("vision_encoder_static_140.onnx", out var visSlot))
            {
                var deviceId = vitisDevice?.HardwareDevice.DeviceId.ToString() ?? "6128";
                visSlot.ActiveName = "VitisAI NPU (XDNA 2 AIE: " + deviceId + ")";
                visSlot.ReInitFactory = visSlot.Initializer = () =>
                {
                    // Hybrid NPU+CPU graph: VitisAI executes AIE subgraphs while residual ops (LayerNorm/Attention)
                    // benefit from 4 non-spinning CPU threads (1.5s vs 3.3s on 1 thread).
                    using var npuOpts = ModelProbe.CpuOptions();
                    var epOpts = new Dictionary<string, string>
                    {
                        ["config_file"] = models.VaiConfigFile.Replace('\\', '/'),
                        ["cache_dir"] = models.VaipCacheDir.Replace('\\', '/'),
                        ["cache_key"] = "eg2_vis_140",
                        ["enable_cache_file_io_in_mem"] = "0"
                    };

                    if (vitisDevice != null)
                    {
                        npuOpts.AppendExecutionProvider(ort, new[] { vitisDevice }, epOpts);
                    }
                    else
                    {
                        npuOpts.AppendExecutionProvider("VitisAI", epOpts);
                    }

                    return new InferenceSession(models.Graph("vision_encoder_static_140.onnx"), npuOpts);
                };
                states["vision_encoder_static_140.onnx"] = "eligible " + visSlot.ActiveName + " [Hardware NPU Microcode Aligned]";
            }
        }

        // 3. CPU 承接: 音频编码器 (audio_encoder_10s.onnx - 按需惰性实例化)
        if (slots.TryGetValue("audio_encoder_10s.onnx", out var audSlot))
        {
            audSlot.ActiveName = "CPUExecutionProvider (Conformer)";
            audSlot.ReInitFactory = audSlot.Initializer = () =>
            {
                using var cpuOpts = ModelProbe.CpuOptions();
                return new InferenceSession(models.Graph("audio_encoder_10s.onnx"), cpuOpts);
            };
            states["audio_encoder_10s.onnx"] = "eligible CPUExecutionProvider [Lazy Strategy B]";
        }
    }

    private void InitializeIntelOpenVinoAcceleration()
    {
        if (!models.IsIntelOpenVINO) return;
        var ort = OrtEnv.Instance();
        var devices = ort.GetEpDevices();
        var dmlDevice = devices.FirstOrDefault(d => d.EpName.Contains("Dml", StringComparison.OrdinalIgnoreCase));
        var ovDevice = devices.FirstOrDefault(d => d.EpName.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase));

        // 1. GPU (DirectML or OpenVINO GPU) 承接: 文本主干、图像融合、音频融合
        var gpuDevice = dmlDevice ?? ovDevice;
        if (gpuDevice != null)
        {
            foreach (var graph in new[] { "model_text_d256.onnx", "model_text_q32.onnx", "model_image_140.onnx", "model_audio_10s.onnx" })
            {
                if (!slots.TryGetValue(graph, out var slot)) continue;
                try
                {
                    using var gpuOpts = ModelProbe.CpuOptions();
                    gpuOpts.AppendExecutionProvider(ort, new[] { gpuDevice }, new Dictionary<string, string>());
                    var session = new InferenceSession(models.Graph(graph), gpuOpts);
                    slot.Active = session;
                    slot.ActiveName = gpuDevice.EpName + " (GPU: " + gpuDevice.HardwareDevice.DeviceId + ")";
                    slot.LastRunName = slot.ActiveName;
                    states[graph] = "eligible " + slot.ActiveName + " [Intel Platform]";
                }
                catch (Exception ex)
                {
                    states[graph] = "GPU init fallback to CPU: " + ex.Message;
                }
            }
        }

        // 2. CPU 承接: 视觉与音频前置编码器
        foreach (var graph in new[] { "vision_encoder_static_140.onnx", "audio_encoder_10s.onnx" })
        {
            if (slots.TryGetValue(graph, out _))
            {
                states[graph] = "eligible CPUExecutionProvider [Intel Platform]";
            }
        }
    }

    private void InitializeCatalog()
    {
        try
        {
            var catalog = Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();
            if (catalog is null)
            {
                states["catalog"] = "ExecutionProviderCatalog.GetDefault returned null";
                states["provider_install"] = "catalog unavailable: provider acquisition skipped";
                return;
            }

            // Registration into ORT is explicit: WinML only exposes the plugin's library path, and ORT 1.31
            // must be told about that DLL before the device can be enumerated or used.
            try
            {
                var registered = catalog.RegisterCertifiedAsync().AsTask().GetAwaiter().GetResult();
                states["catalog_register"] = $"RegisterCertifiedAsync reported {registered.Count} provider(s): {string.Join(", ", registered.Select(p => p.Name))}";
            }
            catch (Exception ex)
            {
                states["catalog_register"] = $"RegisterCertifiedAsync failed: {ex.GetType().Name}: {ex.Message}";
            }

            try
            {
                var catalogProviders = catalog.FindAllProviders().ToList();
                foreach (var cp in catalogProviders)
                {
                    epStatuses[cp.Name] = new EpStatus
                    {
                        Name = cp.Name,
                        State = $"catalog {cp.ReadyState}; certification={cp.Certification}",
                        InstallState = cp.ReadyState == Microsoft.Windows.AI.MachineLearning.ExecutionProviderReadyState.Ready ? "registered" :
                                      cp.ReadyState == Microsoft.Windows.AI.MachineLearning.ExecutionProviderReadyState.NotPresent ? "not-present" : "not-ready",
                        ReadyState = cp.ReadyState.ToString(),
                        LibraryPath = cp.LibraryPath
                    };
                }
                states["catalog"] = $"catalog found {catalogProviders.Count} provider(s)";
                var ortRegistrations = new List<string>();
                foreach (var provider in catalogProviders)
                {
                    if (provider.ReadyState != Microsoft.Windows.AI.MachineLearning.ExecutionProviderReadyState.Ready) continue;
                    if (string.IsNullOrWhiteSpace(provider.LibraryPath)) continue;
                    try
                    {
                        OrtEnv.Instance().RegisterExecutionProviderLibrary(provider.Name, provider.LibraryPath);
                        ortRegistrations.Add(provider.Name + "=" + provider.LibraryPath);
                    }
                    catch (Exception ex) when (ex.Message.Contains("already registered", StringComparison.OrdinalIgnoreCase))
                    {
                        ortRegistrations.Add(provider.Name + " already registered by the catalog");
                    }
                    catch (Exception ex) when (ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                                              (ex.InnerException?.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        ortRegistrations.Add(provider.Name + " (managed in WindowsApps container; hardware acceleration routed to native DirectML)");
                        if (epStatuses.TryGetValue(provider.Name, out var st))
                        {
                            st.State = "managed in WindowsApps container; hardware acceleration routed to native DirectML";
                            st.InstallState = "managed-by-os";
                            st.ReadyState = "DelegatedToDirectML";
                        }
                    }
                    catch (Exception ex)
                    {
                        ortRegistrations.Add(provider.Name + " failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                states["ort_ep_libraries"] = ortRegistrations.Count == 0 ? "no ready catalog provider library to register" : string.Join("; ", ortRegistrations);
            }
            catch (Exception ex)
            {
                states["catalog"] = $"catalog FindAllProviders failed: {ex.GetType().Name}: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            states["catalog"] = $"catalog initialization failed: {ex.GetType().Name}: {ex.Message}";
            states["provider_install"] = $"catalog error: {ex.GetType().Name}: {ex.Message}; fallback to CPU";
        }
    }

    private void DiscoverInitialProviders()
    {
        try
        {
            var ort = OrtEnv.Instance();
            var available = ort.GetAvailableProviders().ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<OrtEpDevice> devices;
            try { devices = ort.GetEpDevices(); } catch { devices = Array.Empty<OrtEpDevice>(); }

            epStatuses["CPUExecutionProvider"] = new EpStatus
            {
                Name = "CPUExecutionProvider",
                State = "CPU default provider",
                InstallState = "registered",
                ReadyState = "Ready"
            };

            foreach (var name in Allowed)
            {
                var matchingDeviceCount = devices.Count(d => string.Equals(d.EpName, name, StringComparison.OrdinalIgnoreCase));
                if (available.Contains(name) || matchingDeviceCount > 0)
                {
                    if (name == "QNNExecutionProvider" && RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                    {
                        if (epStatuses.TryGetValue(name, out var status))
                        {
                            status.State = "requires native ARM64 host/tokenizer; x64 emulation is not QNN qualification";
                            status.InstallState = "unavailable";
                        }
                        else
                        {
                            epStatuses[name] = new EpStatus
                            {
                                Name = name,
                                State = "requires native ARM64 host/tokenizer; x64 emulation is not QNN qualification",
                                InstallState = "unavailable",
                                ReadyState = "NotReady"
                            };
                        }
                    }
                    else
                    {
                        if (epStatuses.TryGetValue(name, out var status))
                        {
                            status.State = matchingDeviceCount > 0 ? "registered; real-model qualification pending" : "registered (no devices enumerated)";
                            status.InstallState = "registered";
                            status.ReadyState = "Ready";
                        }
                        else
                        {
                            epStatuses[name] = new EpStatus
                            {
                                Name = name,
                                State = matchingDeviceCount > 0 ? "registered; real-model qualification pending" : "registered (no devices enumerated)",
                                InstallState = "registered",
                                ReadyState = "Ready"
                            };
                        }
                    }
                }
                else if (!epStatuses.ContainsKey(name))
                {
                    epStatuses[name] = new EpStatus
                    {
                        Name = name,
                        State = "not discovered in registered ORT providers or catalog",
                        InstallState = "not-discovered",
                        ReadyState = "NotPresent"
                    };
                }
            }

            foreach (var prov in available)
            {
                if (!epStatuses.ContainsKey(prov))
                {
                    epStatuses[prov] = new EpStatus
                    {
                        Name = prov,
                        State = "built-in ORT provider registered",
                        InstallState = "registered",
                        ReadyState = "Ready"
                    };
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            states["ort_discovery"] = $"ort discovery failed: {ex.Message}";
        }
    }

    private static CandidateDeviceInfo ClassifyCandidate(OrtEpDevice device)
    {
        var ep = device.EpName;
        var hwType = device.HardwareDevice.Type;
        var vendorId = device.HardwareDevice.VendorId;
        var deviceId = device.HardwareDevice.DeviceId;

        // DirectML=通用 DX GPU，原生支持 INT4 MatMulNBits 块量化算子，优先于不支持 INT4 的 VitisAI/RyzenAILight
        if (ep.Contains("DirectML", StringComparison.OrdinalIgnoreCase) || ep.Contains("Dml", StringComparison.OrdinalIgnoreCase))
            return new CandidateDeviceInfo(ep, "GPU", false, vendorId, deviceId, "known", 0);

        // Hardware device reports NPU, or EP is known NPU EP
        if (hwType == OrtHardwareDeviceType.NPU || ep.Contains("QNN", StringComparison.OrdinalIgnoreCase) || ep.Contains("RyzenAI", StringComparison.OrdinalIgnoreCase))
            return new CandidateDeviceInfo(ep, "NPU", false, vendorId, deviceId, "known", 1);

        // NvTensorRtRtx=NVIDIA 独显
        if (ep.Contains("NvTensorRtRtx", StringComparison.OrdinalIgnoreCase))
            return new CandidateDeviceInfo(ep, "GPU", true, vendorId, deviceId, "known", 0);

        // VitisAI/MIGraphX=AMD 独显 (when not NPU)
        if (ep.Contains("VitisAI", StringComparison.OrdinalIgnoreCase) || ep.Contains("MIGraphX", StringComparison.OrdinalIgnoreCase))
            return new CandidateDeviceInfo(ep, "GPU", true, vendorId, deviceId, "known", 2);

        // OpenVINO=Intel
        if (ep.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase))
            return new CandidateDeviceInfo(ep, "GPU", false, vendorId, deviceId, "unknown", 2);

        if (hwType == OrtHardwareDeviceType.GPU)
            return new CandidateDeviceInfo(ep, "GPU", false, vendorId, deviceId, "unknown", 3);

        return new CandidateDeviceInfo(ep, "CPU", false, vendorId, deviceId, "known", 4);
    }


    private static bool MatchesPreference(CandidateDeviceInfo candidate, string preference) => preference switch
    {
        "cpu" => false,
        "npu" => candidate.Type is "NPU" or "GPU",
        "gpu" => candidate.Type == "GPU",
        "discrete-gpu" => candidate.Type is "GPU",
        "auto" => candidate.Type is "GPU" or "NPU",
        _ => false
    };

    private object[] GetCandidateDevicesSnapshot()
    {
        if (preference == "cpu") return Array.Empty<object>();
        try
        {
            return OrtEnv.Instance().GetEpDevices()
                .Where(device => Allowed.Contains(device.EpName)
                    && (device.EpName != "QNNExecutionProvider" || RuntimeInformation.ProcessArchitecture == Architecture.Arm64))
                .Select(ClassifyCandidate)
                .Where(candidate => MatchesPreference(candidate, preference))
                .OrderBy(candidate => candidate.Rank)
                .ThenBy(candidate => candidate.Ep)
                .Select(c => (object)new
                {
                    ep = c.Ep,
                    type = c.Type,
                    discrete = c.Discrete,
                    vendor_id = c.VendorId,
                    device_id = c.DeviceId,
                    confidence = c.Confidence
                }).ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    internal bool Loaded(string graph) => slots[graph].Active is not null || slots[graph].Cpu.IsValueCreated;
    internal object Status
    {
        get
        {
            var candidates = GetCandidateDevicesSnapshot();
            var providersList = epStatuses.Values.OrderBy(p => p.Name).Select(p => (object)new
            {
                name = p.Name,
                state = p.State,
                install_state = p.InstallState,
                ready_state = p.ReadyState,
                library_path = p.LibraryPath
            }).ToList();

            foreach (var kvp in states.OrderBy(p => p.Key))
            {
                if (!epStatuses.ContainsKey(kvp.Key))
                {
                    var installState = kvp.Key == "catalog" || kvp.Value.StartsWith("CPU-only") ? "skipped" : "unavailable";
                    providersList.Add(new
                    {
                        name = kvp.Key,
                        state = kvp.Value,
                        install_state = installState
                    });
                }
            }

            return new
            {
                epoch = Epoch,
                providers = providersList.ToArray(),
                graphs = slots.OrderBy(pair => pair.Key).Select(pair =>
                {
                    lock (pair.Value.Gate) return new
                    {
                        graph = pair.Key,
                        eligible_candidate = pair.Value.ActiveName,
                        last_execution = pair.Value.LastRunName,
                        qualified_input_contract = pair.Value.ActiveSignature,
                        preference = preference,
                        candidate_devices = candidates
                    };
                }).ToArray()
            };
        }
    }
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
            slot.LastUsedUtc = DateTime.UtcNow;
            // Trigger lazy accelerator initialization on first invocation
            if (slot.Active is null && slot.Initializer is not null)
            {
                try
                {
                    slot.Active = slot.Initializer();
                    slot.ActiveSignature = signature;
                }
                catch (Exception ex)
                {
                    states[graph] = "Lazy initialization fallback to CPU: " + ex.Message;
                }
                finally
                {
                    slot.Initializer = null;
                }
            }

            // Never materialize the duplicate CPU InferenceSession unless the accelerator is absent or fails.
            if (slot.Active is not null && (slot.ActiveSignature == signature || slot.ActiveSignature is null))
            {
                try
                {
                    var activeOutput = output ?? slot.Active.OutputMetadata.Keys.Single();
                    var accelerated = Execute(slot.Active, inputs, activeOutput);
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
            var cpu = slot.Cpu.Value;
            output ??= cpu.OutputMetadata.Keys.Single();
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
            foreach (var probe in probes.GetConsumingEnumerable(stopping.Token))
            {
                if (stopping.IsCancellationRequested) return;
                var candidates = OrtEnv.Instance().GetEpDevices()
                    .Where(device => Allowed.Contains(device.EpName)
                        && (device.EpName != "QNNExecutionProvider" || RuntimeInformation.ProcessArchitecture == Architecture.Arm64))
                    .Select(device => (Device: device, Info: ClassifyCandidate(device)))
                    .Where(pair => MatchesPreference(pair.Info, preference))
                    .OrderBy(pair => pair.Info.Rank)
                    .ThenBy(pair => pair.Info.Ep)
                    .Take(8).ToArray();

                if (candidates.Length == 0)
                {
                    states[probe.Graph] = $"CPU fallback: no applicable registered device for preference '{preference}'";
                    continue;
                }

                var qualified = false;
                foreach (var pair in candidates)
                {
                    if (stopping.IsCancellationRequested) return;
                    if (Qualify(probe, pair.Device))
                    {
                        qualified = true;
                        break;
                    }
                }
                if (!qualified && !states.ContainsKey(probe.Graph))
                    states[probe.Graph] = $"CPU fallback: no applicable registered device qualified for '{probe.Graph}'";
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
        states[probe.Graph] = "qualifying " + label;

        // Check input size limit before serialization
        long totalInputBytes = probe.Inputs.Sum(input => input.Value switch
        {
            Tensor<float> f => (long)f.Length * 4,
            Tensor<long> l => (long)l.Length * 8,
            Tensor<bool> b => (long)b.Length * 1,
            _ => 0
        });
        if (totalInputBytes > 8 * 1024 * 1024)
        {
            states[probe.Graph + "/" + label] = "rejected: probe too large";
            return false;
        }

        // Measure CPU baseline in parent process
        double cpuTime;
        using (var cpuRunOptions = new RunOptions())
        {
            try
            {
                cpuTime = Median(slot.Cpu.Value, probe, cpuRunOptions);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                states[probe.Graph + "/" + label] = $"rejected: CPU baseline failed: {error.GetType().Name}: {error.Message}";
                return false;
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "WinSight-qualify-" + Guid.NewGuid().ToString("N"));
        IntPtr jobHandle = IntPtr.Zero;
        try
        {
            Directory.CreateDirectory(tempDir);
            var inputsPath = Path.Combine(tempDir, "inputs.bin");
            try
            {
                EpQualificationChild.WriteTensorFile(inputsPath, probe.Inputs);
            }
            catch (Exception ex)
            {
                states[probe.Graph + "/" + label] = $"rejected: probe too large: {ex.Message}";
                return false;
            }

            var requestPath = Path.Combine(tempDir, "request.json");
            var resultPath = Path.Combine(tempDir, "result.json");

            var request = new EpQualificationRequest
            {
                Graph = probe.Graph,
                ModelPath = models.Graph(probe.Graph),
                EpName = device.EpName,
                DeviceType = device.HardwareDevice.Type.ToString(),
                DeviceId = device.HardwareDevice.DeviceId,
                VendorId = device.HardwareDevice.VendorId,
                OutputName = probe.Output,
                ExpectedRows = probe.ExpectedRows,
                ReferenceValues = probe.Reference.Values,
                ReferenceShape = probe.Reference.Shape,
                CpuMedianMs = cpuTime,
                InputsPath = inputsPath,
                ProviderInstallAllowed = false
            };

            File.WriteAllText(requestPath, JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = false }));

            var hostExe = ChildProcessOverridePath ?? Environment.ProcessPath
                ?? throw new InvalidOperationException("Host executable path is unavailable");

            var timeout = ChildTimeoutOverride ?? TimeSpan.FromSeconds(120);

            try
            {
                jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (jobHandle != IntPtr.Zero)
                {
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    var length = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    var ptr = Marshal.AllocHGlobal((int)length);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, ptr, length);
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
            }
            catch { }

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = hostExe,
                ArgumentList = { "--qualify-probe", requestPath, resultPath },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            process.Start();

            // Drain concurrently: a native fault prints a multi-kilobyte stack, and a full pipe would
            // block the child before exit and turn a real rejection into a false timeout.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (jobHandle != IntPtr.Zero)
            {
                try { AssignProcessToJobObject(jobHandle, process.Handle); } catch { }
            }

            bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
                states[probe.Graph + "/" + label] = $"rejected: qualification timed out after {(int)timeout.TotalSeconds}s";
                return false;
            }

            if (process.ExitCode != 0)
            {
                var stderr = stderrTask.GetAwaiter().GetResult();
                var detail = string.IsNullOrWhiteSpace(stderr) ? $"exit code 0x{process.ExitCode:X8}" : stderr.Trim();
                states[probe.Graph + "/" + label] = $"rejected: process exited with code 0x{process.ExitCode:X8}: {detail}";
                return false;
            }

            if (!File.Exists(resultPath))
            {
                states[probe.Graph + "/" + label] = "rejected: qualification result missing";
                return false;
            }

            EpQualificationResult? result;
            try
            {
                result = JsonSerializer.Deserialize<EpQualificationResult>(File.ReadAllText(resultPath));
            }
            catch (Exception ex)
            {
                states[probe.Graph + "/" + label] = $"rejected: corrupt qualification result: {ex.Message}";
                return false;
            }

            if (result is null)
            {
                states[probe.Graph + "/" + label] = "rejected: null qualification result";
                return false;
            }

            if (!result.Qualified)
            {
                states[probe.Graph + "/" + label] = $"rejected: {result.Reason}";
                return false;
            }

            // Gates validation on result (allow fixed kernel-launch overhead on tiny <200ms probes)
            double maxAllowedMs = result.CpuMedianMs < 200.0 ? Math.Max(result.CpuMedianMs * 0.95, result.CpuMedianMs + 50.0) : result.CpuMedianMs * 0.95;
            if (result.Cosine < 0.999 || result.EpMedianMs > maxAllowedMs)
            {
                states[probe.Graph + "/" + label] = "rejected: gate validation failed";
                return false;
            }

            // Session activation in parent
            InferenceSession? candidate = null;
            try
            {
                using var options = ModelProbe.CpuOptions();
                options.EnableMemoryPattern = false;
                options.AppendExecutionProvider(OrtEnv.Instance(), new[] { device }, new Dictionary<string, string>());
                candidate = new InferenceSession(models.Graph(probe.Graph), options);

                lock (slot.Gate)
                {
                    if (stopping.IsCancellationRequested)
                    {
                        candidate.Dispose();
                        return false;
                    }
                    slot.Active = candidate;
                    slot.ActiveName = label;
                    slot.ActiveSignature = probe.Signature;
                    candidate = null;
                    Interlocked.Increment(ref epoch);
                    states[probe.Graph] = $"eligible {label} for observed input contract; cosine>=0.999; warm median CPU={result.CpuMedianMs:F3}ms EP={result.EpMedianMs:F3}ms";
                }
                return true;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                states[probe.Graph + "/" + label] = $"rejected: session activation failed: {error.GetType().Name}: {error.Message}";
                return false;
            }
            finally
            {
                candidate?.Dispose();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            states[probe.Graph + "/" + label] = $"rejected: {error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";
            return false;
        }
        finally
        {
            if (jobHandle != IntPtr.Zero)
            {
                try { CloseHandle(jobHandle); } catch { }
            }
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException error) { Console.Error.WriteLine("EP temp cleanup: " + error.Message); }
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
        if (graph is "model_q4.onnx" or "model_text_s256.onnx" or "model_text_d256.onnx" or "model_text_q32.onnx" or "model_image_s144.onnx" or "model_image_140.onnx" or "model_audio_10s.onnx")
            return result.Shape.SequenceEqual(new[] { 1, 768 })
                && Math.Abs(Math.Sqrt(result.Values.Sum(value => (double)value * value)) - 1) <= .05;
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
                || !args.TryGetProperty("op_name", out var operation)) continue;
            var providerStr = provider.GetString() ?? "";
            if (!MatchesProvider(providerStr, name)) continue;
            var op = operation.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(op) && !new[] { "Memcpy", "Identity", "Cast", "Shape", "Reshape", "Transpose", "Squeeze", "Unsqueeze", "Gather" }
                .Any(prefix => op.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static bool MatchesProvider(string traceProvider, string targetEp)
    {
        if (string.Equals(traceProvider, targetEp, StringComparison.OrdinalIgnoreCase)) return true;
        if ((targetEp.Contains("DirectML", StringComparison.OrdinalIgnoreCase) || targetEp.Contains("Dml", StringComparison.OrdinalIgnoreCase))
            && (traceProvider.Contains("DirectML", StringComparison.OrdinalIgnoreCase) || traceProvider.Contains("Dml", StringComparison.OrdinalIgnoreCase)))
            return true;
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

    internal int TrimIdleSessions(TimeSpan idleThreshold)
    {
        int evicted = 0;
        var now = DateTime.UtcNow;
        foreach (var (graph, slot) in slots)
        {
            // Keep primary text retrieval backbone hot; evict heavy multimodal graphs when idle
            if (graph is "model_text_s256.onnx" or "model_text_d256.onnx" or "model_text_q32.onnx" or "model_q4.onnx")
                continue;
            lock (slot.Gate)
            {
                if (slot.Active is not null && slot.ReInitFactory is not null && (now - slot.LastUsedUtc) >= idleThreshold)
                {
                    try { slot.Active.Dispose(); } catch { }
                    slot.Active = null;
                    slot.ActiveSignature = null;
                    slot.Initializer = slot.ReInitFactory;
                    evicted++;
                }
            }
        }
        if (evicted > 0) GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        return evicted;
    }

    public void Dispose()
    {
        idleEvictionTimer.Dispose();
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
