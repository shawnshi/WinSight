# N2b-1：有界采样与可信索引消费（candidate）

前置：N2b-0独立限定通过，报告reviews/n2b-media-ops-review.md。本切片在用户已授权的原生化范围内编写候选实现和合成测试，不读/写真实语料、设置或既有index，不部署，不自动启用后台。**DesktopBackend当前默认构造路径保持不变**；通过此切片独立review后，才另接factory/后台生命周期。

## 固定策略

以n2b-sampling-source-contract.md及P0 Portable实际方法体为准：audio30s/25s、16k monoPCM16；RMS严格>0.003；尾段先扣已覆盖5s，新增>=2s才保留整个残余区间。视频first=min(.5,duration/2)、step=max(3,duration/1200)、maxEdge1024、image budget70；8x8整数RGB averageHash，距离<=4仅与上个保留帧比较，保留帧end延伸到下一保留帧start，最后到原duration；音轨采用同一音频策略。

音频区间由floor(duration*16000)对应的整数样本时间规划，非零起点/尾段保持原源时间轴。Native元数据/像素/FramePrecision与旧FFmpeg并非数值等价证明；切点、整数hash及EOF须定向测试，保留跨decoder parity未知，不宣称逐帧精确复现。

现有ContentExtractor.MaxSegments=5000、MaxBytes=32MiB保持。新路径最多5000次decode尝试（所有image/audio合计，静音/重复亦计）；这是透明的候选执行budget，不是声称旧音频有该上限。超过时明确partial，附覆盖/原因，不能done/伪造EOF。视频自身仍最多1200采样。用于定向边界测试的内部实例budget可降低至1..5000，不能升高、不能把未覆盖改成功；无公共/CLI开关。不全文件产物堆积：audio通常1个，frame lookahead去重允许最多2个未消费产物。

## 固定内部seam（public Core ABI不变）

namespace WinSight.Infrastructure.Media：

- internal sealed `NativeMediaSource : IDisposable`，`internal static NativeMediaSource Acquire(string sourcePath,string kind)`：调用方仅是IndexingService已授权candidate分支；kind必须与FilePolicy非code的audio/video一致。先复用MediaTaskFiles.LocalPath本地盘限制，再复用Extraction.SafeFile全路径/祖先pin、大小检查。构造不可由外部third-party extractor调用。
- internal sealed `NativeMediaSamplingService(NativeMediaProcessClient client,int maximumOperations=ContentExtractor.MaxSegments)`；`internal Task<NativeMediaSamplingSession> OpenAsync(NativeMediaSource source,CancellationToken cancellationToken=default)`。不拥有/Dispose传入client。
- internal sealed `NativeMediaSamplingSession : IAsyncDisposable`：`internal NativeMediaInfo Metadata {get;}`、`internal bool CoverageComplete {get;}`、`internal string? Detail {get;}`；`internal IAsyncEnumerable<NativeMediaSample> ReadAsync(bool includeImage,bool includeAudio,CancellationToken cancellationToken=default)`。非重入单次枚举，拒绝源身份漂移；即使调用方提前Dispose source，session也须独立持有相同身份的源pin至结束。未枚举/提前中止/预算截断/真实省略已有modality时CoverageComplete=false。
- internal sealed `NativeMediaSample : IAsyncDisposable`，只读 `internal IndexSegment Segment {get;}` 与 `internal EmbeddingInput Input {get;}`；仅可信session能创建；内部真实NativeMediaArtifactLease。Segment.Kind=frame/audio、Modality=image/audio，原时间Location/LocationEnd，Excerpt=null、Vector=null；Input从真实lease.Path建立，frame budget70、audio沿用音频embedding默认；不得从第三方Excerpt/Info反解artifact路径。
- service `internal const string Revision = "native-media-sampling-v1"`（metadata字段值，不改变向量profile/fingerprint）。辅助纯policy、RIFF RMS/PNG RGB unfilter hash放新Media/Sampling/子目录；不修改已通过N2b-0的client/protocol/PNG/Host文件。

Source.Acquire与session二次pin核对身份再metadata，原IndexingService.SafeFile也保持；不可用公共可伪造string token取代所有保护。Sample返回后的租约至embedding await结束才释放；session关闭不能抢删仍在消费的sample（N2真实lease独立保源）。严禁父侧WinRT metadata解析；新hash从已验证、write-denied的有界PNG做managed RGB解码/整数算法，不引入包/新数值推理。

## 索引消费

`IndexingService`保留原public三参构造、原non-media流程。新增internal四参构造(repo,extractor,embeddings,NativeMediaSamplingService)供candidate/tests；只有它启用media分支，实际DesktopBackend不改。其process在既有root/exclusion/generated/secret/type/byte授权和SafeFile检查后签发Source；第三方extractor永远不能通过Info/Excerpt指定artifact。mediaCanExtract仍不放宽FilePolicy。

逐个sample从typed Input调用同一IEmbeddingService，VectorContract验证768d；await结束后Dispose sample。已失去modality能力时不decode该类，session记录真实省略；完全无embedding服务/信息或必要能力时明确partial，不生成filename-only/text/media伪向量。视频有声轨但缺audio能力/提取失败，即使frame成功也不可done。完整静音/短音频可skipped；已保留可用结果但后续decode失败/预算/embedding单项错误为partial；零可用结果且真实decode/protocol失败为failed；cancel沿用传播、不盖完成章；embedding依赖异常仍使现有失败边界真实可见，不转无数据。保留完整区间检查和mtime/size提交前检查。

Media专用Info JSON字段 `native_media_revision` 只在完整成功done/skipped后标记Revision；partial不写“已覆盖”标记。仅audio/video旧done缺/异版本时绕过unchanged，使其重处理；doc/code unchanged/fingerprint、schema5/Float16/profile/metadata mismatch阻断和Force语义完全不变。不清库、不重建、无真实迁移。当前skipped本就不走done-only unchanged，不需额外更改。

## 独占write sets

1. Sampler lane：新 `desktop/src/WinSight.Infrastructure/Media/Sampling/**` 和desktop/media-sampling-handoff.md，不改旧Media/Host/Runtime/Core/Indexing。
2. Index consumer lane：`Indexing/IndexingService.cs`、`Runtime/TestAccess.cs`仅加friend WinSight.MediaIndexing.Tests、desktop/media-index-consumer-handoff.md，不改Runtime/DesktopBackend或Extractor/Host/Media/Repo。
3. Test lane：新 `desktop/tests/WinSight.MediaIndexing.Tests/**`、desktop/media-index-tests-handoff.md，不改生产/现有suite。可写fake Native worker和fake embedding/repo，所有夹具与task-onlySQLite新建在明确临时根，无真实模型/用户目录。

## 必要oracle

- pure audio窗：2s/2s-1sample、30/31/32/55/57s；严格RMS边界；attempt与retained分别计、降低budget必partial、自然EOF正好budget不可误partial。
- video计划<=1200、first/step、maxEdge1024/budget70；整数格均值/bit序与<=4边界、只跟last-kept比较；相同帧run-end、连续镜头顺序、末尾duration。
- 实际client/Host合成native audio/video+fake embeddings：非零locators、waveform对应、租约跨阻塞await、source/ancestor改名拒绝和最终释放；图/声两类完整/省略/错误不混同done。
- IndexingService repo/task-onlySQLite验证media vectors、revision只成功写、旧media重处理/doc-code不失效、指纹/profile mismatch在所有写之前阻断、未经授权root/排除项/secret/data dirs不启动child、third-party artifact spoof不能驱动外部路径。
- 取消/故障/停读/Dispose不早清理，真实owned PID确认；所有missing component要pending而不是stub/pass。原N1/N2a/N2b-0与Indexing回归保留。独立review通过前不hook后台/部署/称完整N2b。
