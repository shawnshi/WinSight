# N2b-0：受控元数据与视频声轨操作

N1与N2a已分别独立限定通过；这不替代本切片审查。用户授权分步原生化仍覆盖该代码工作。本切片解决完整采样的前置缺口，不直接接索引、改设置、模型、vector/profile/schema或正在运行的预览。

## 固定Host接口

新增两个闭集CLI操作（不是任意命令转发）：

1. `inspect-media ABS_REQUEST_JSON`：`MediaInspectRequest { inputPath, kind }`，kind只能audio/video。严格≤64KiB、大小写/重复/未知/缺失/null/错误类型拒绝，输入路径/本地盘/源pin沿用N1守卫。没有输出目录和媒体产物。成功输出一行camelCase `MediaInspectResult { protocol:1, sourcePath, kind, durationSeconds, hasAudio, audioTrackCount, width, height }`。
   - audio：用真实背景轨的OriginalDuration；成功则hasAudio=true、audioTrackCount=null（未知原容器轨道数，不编造1）、width/height=0。
   - video：真实MediaClip原时长/视频尺寸、EmbeddedAudioTracks.Count；hasAudio与count一致，尺寸必须真实有效。native错误或未知/非正时长不能改成无声或零数据。
2. `extract-soundtrack ABS_REQUEST_JSON`：沿用MediaDecodeRequest/Result及其严格字段规则，kind必须audio、明确≤30秒区间；作用于视频。只能在实际MediaClip中有一个嵌入音轨时执行：按原时轴裁剪真实clip，以MediaComposition/Precise输出PCM16/mono16k WAV。0轨是明确无声轨错误，>1轨先明确NotSupported，不暗改轨道选择。返回Kind=audio、Modality=audio、原视频start/end与真实源时长，产物audio.wav；复用实际WAV格式/全区间样本验证。

Host固定方法：`NativeMediaCommand.RunInspectRequestFileAsync(string,CancellationToken=default)`、`RunSoundtrackRequestFileAsync(string,CancellationToken=default)`。可加新内部helper但不要改现有DTO字段顺序/含义、extract-media与stdio/provider行为；NativeMediaContracts新增records由父级所有。

## 固定父侧接口

在现有 `WinSight.Infrastructure.Media.NativeMediaProcessClient` 增加：

- `Task<NativeMediaInfo> InspectAsync(string sourcePath,string kind,CancellationToken=default)`。
- `public sealed record NativeMediaInfo(string SourcePath,string Kind,double DurationSeconds,bool HasAudio,int? AudioTrackCount,int Width,int Height)`。
- `Task<NativeMediaArtifactLease> ExtractSoundtrackAsync(NativeMediaProcessRequest request,CancellationToken=default)`，request.Kind必须audio。

沿用N2a单worker/≤16 admission、全请求deadline、出生即Job控制、精确stdio继承、有界UTF8、真实终止/确认与隔离保留、路径pins和消费租约。不得另造Process.Start旁路、parent WinRT元数据解析或公共任意command参数。

Inspect在真实worker完成与确认退出后返回严格验证的metadata并清理自身任务，不能泄漏请求文件或产生消费产物；上层索引session仍负责跨Inspect/Extract固定源身份。音轨操作返回原有真实产物租约；client关闭不能提前删除消费中的产物。内部可用闭集operation/共享执行骨架，但不弱化原Extract默认行为和fault注入测试门。

## 独立所有权

- Host工作树：Program.cs、NativeMediaCommand.cs、新NativeMediaInspect/Soundtrack helper；不改父级DTO、模型、数值、包版本。
- Transport工作树：Infrastructure/Media/**，不改旧推理协议、Core或索引，不改Host。
- Tests工作树：新 `desktop/tests/WinSight.MediaMetadata.Tests/**`，可读复用既有夹具源；不修改上述生产代码或已验收的111/59套件。
- 主代理：DTO、合同、哈希集成、主树构建/全套回归和独立review；不发布。

## 负向断言和验证

真实合成WAV、无声/有声MP4：metadata的时长/尺寸/轨道存在与Native文件一致。容器AAC时长与构造素材时长可能不同，禁止硬写2秒。声轨必须验证非零起点的时变内容与EOF，不只检查32k样本数；父级已有[0,2] 32000帧探针仅是能力依据。

严格参数/路径/驱动/重解析、no-audio、多轨显式拒绝、损坏视频/元数据、畸形/未知/重复JSON、错误response字段、超时/取消/后代、client.Dispose、请求清理/消费租约跨await，均不得伪装empty/done。多轨选择、AAC任意编码延迟与广泛codec仍作为明确未覆盖项。

验证需实际Host CLI + N2受控client端到端；不可只链接方法。保留原N1 111与N2a 59回归。新增Operation的独立review通过后，才开始N2b-1完整采样/索引消费；现有Corpus/用户目录不读、不写、不迁移。
