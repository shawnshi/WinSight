# techstack 对照与原生补齐：执行边界

> 退役范围说明（2026-10-23）：本页保留历史设计、迁移依据或行为对照。涉及 Swift/Tauri、旧 Vendor、旧 CLI 与旧生成脚本的路径／命令只供回查，不再是当前可执行开发入口。原始源码位于 Git 提交 `400c5b501e664f7aaaf2c2ec5e1c270cf0425a87`；当前主线见仓库 README 和 `docs/native-migration/legacy-retirement.md`。原验收缺口不会因源码退役自动关闭。


输入：项目根 `../techstack.md`（原文保持不变）；本轮源码基线 `.dev/native-migration/native-stack-gap/baseline/manifest.json`。分析结论以方法体、当前依赖锁和实际探针为准，不把推荐包清单当实现事实。

## 已明确的推演断言

1. WinUI、Win32 托盘/热键、Windows ML、WIC 与托管 OpenXML 已属于 Windows 原生或适当的 .NET 实现；缺少某个 UI 工具包不是重写理由。
2. 当前 `ContentExtractor.ExtractAsync` 对音视频返回不支持；这是候选原生链路尚未接齐，不是已经完成的行为。优先补齐本地 WinRT 解码，而不是引入 ffmpeg、Python 或另一套推理运行时。
3. Rust tokenizer 是原生 DLL，不等同于非原生胶水。替换 tokenizer、768d Float16/既有 schema、模型音频分支、FTS 或排名算法，必须先证明契约与行为兼容；不能从 techstack 的条目直接推出删除/迁移许可。
4. 当前 unpackaged/Inno 方向不能默认承诺 Windows.Media.Ocr 的正式桌面支持。包身份与独立 OCR 引擎是需要明确取舍的不同方案，不自动安装或下载。

## 有序切片

- N0：两路只读对照，补齐“已原生／应替换／新增能力／契约风险”证据表。主代理负责整合，不让部分审计代替完整事实。
- N1：在现有 `WinSight.InferenceHost` 增加不加载模型的原生媒体提取命令。仅使用现有 WinRT API；先交付可独立运行、验证的音频 PCM 与视频帧提取，不修改向量或 tokenizer。
- N2：在 N1 通过后接通父侧受控进程、产物租约和索引消费。保留原文件定位与完整/partial/failed 语义；硬取消、先确认退出再清理是前置门。
  - N2a：受控进程/租约已独立限定通过，59项回归通过。
  - N2b-0：metadata/单声轨操作已独立限定通过，新增129通过/1真实多轨负例未测；原N1 111与N2a 59回归通过。见reviews/n2b-media-ops-review-state.json。
  - N2b-1：按冻结源码规则实现可信sealed采样session与候选IndexingService消费，分三个隔离工作树；通过其独立验收前，不接DesktopBackend默认/后台路径、不部署。合同n2b-indexing-contract.md。
- N3：根据实测对齐剩余平台项，不为引入推荐 NuGet 重写现有 Win32 服务。存储/SIMD、MCP、OCR、安装身份等分别按证据与契约处理，不混入 N1。
- 验收后再更新候选部署；不覆盖当前运行的本机预览，也不删旧 Swift/Tauri。

## N1 行为合同

范围：`inference/WinSight.InferenceHost/Program.cs`、新增媒体命令实现、`desktop/tests/WinSight.NativeMedia.Tests/` 与配套交接记录。当前阶段不修改 Corpus 索引、原有 stdio 协议、模型清单、依赖版本或用户设置。

CLI 为 `extract-media ABSOLUTE_REQUEST_JSON`，入口在当前模型/stdio 分流之前；成功向 stdout 输出单行 camelCase JSON，失败维持现有 Program 非零退出与 stderr 错误，不输出空成功。请求 JSON ≤64 KiB、拒绝未知字段，字段由 `NativeMediaContracts.cs` 的 MediaDecodeRequest 固定。核心实现签名为 `NativeMediaCommand.ExecuteAsync(MediaDecodeRequest, CancellationToken)` 与 `RunRequestFileAsync(string, CancellationToken)`。

输入为明确绝对源文件和新的任务输出目录。N1 每次只提取**显式一个区间/一帧**：Kind=audio 时 EndSeconds 必填、0≤start<end、区间≤30秒；Kind=frame 时 EndSeconds 必须空、MaxEdge 为64–1024。输出 Protocol=1、原始源路径、可获知的原时长（未知为null）和一个相对名称产物。音频归一到 PCM16/16kHz/mono；视频帧保持原比例且两边均不超过 MaxEdge。不把模型文件、临时路径或解码产物自动写入既有索引。请求的裁剪区间必须实际存在；不能将截短产物虚报为完整请求。原始音频30秒窗/25秒hop、2秒尾部及RMS阈值、视频3秒采样/去重与声轨策略在 N2 实现，不在本叶命令中偷换。

N1 只新增解码原语，尚不承担全文件 done/partial 汇总；错误用异常与非零退出。N2 才以覆盖范围、采样上限和解码/embedding结果确定完整或partial。

关键失败模式：

- 空/畸形参数、非绝对路径、已有输出目录、设备或重解析路径：在读取/写入媒体前明确拒绝。
- 缺失、损坏、无内置 codec：非零退出/明确错误，不伪装空内容成功，不在线安装 codec。
- 预取消：不创建产物；在途取消/超时：明确失败，不宣称 WinRT token 能保证硬停止。父进程硬取消在 N2 实施前不得把该模式接入后台索引。
- 区间/尺寸上限：单次只生成一个≤30秒音频片段或一张尺寸受限帧；越界明确拒绝。N2全文件调度必须汇总覆盖与partial，不能用首段代表完整文件。
- native 原生操作尚未确认停止时，不抢先递归删除其可能仍在写入的任务目录。失败产物留在任务目录，不能当作成功结果。

验收：不用真实模型或个人媒体；用确定性合成 WAV/纯色视频检查实际采样率/声道/位宽、裁剪区间和帧尺寸/颜色；负向检查参数、输出冲突、损坏文件、预取消与 partial 上限。构建实际宿主，并以真实媒体命令做薄端到端；测试链接方法体不能替代宿主命令调度验证。

## 未覆盖副作用

不访问私人模型缓存，不下载模型/执行提供器，不安装系统组件，不改注册表、PATH、开机启动或客户端配置，不发布或替换生产链路。旧 UI harness 与旧 reviewer 的失败预算保持原状。
