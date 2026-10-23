# Windows ML：设备候选、后台准备、实际模型资格门

用户指定的候选表作为源码合同，不以设备标签证明 EP 可运行。延续源码阶段：本轮不启动宿主、不运行模型、不实际下载/安装 EP，不安装工具链、不动真实索引。

## 候选规则

- CPU 是始终可用的基线和故障回退，不回退旧后端。
- 普通 PC：ORT CPU；catalog 判定适用时验证 OpenVINO。
- Intel Core Ultra：OpenVINO CPU/GPU/NPU；实际设备由注册后的 `OrtEnv.GetEpDevices()` 发现。
- AMD Ryzen AI：VitisAI NPU；兼容 GPU 上的 MIGraphX。
- Snapdragon：QNN；宿主和 tokenizer 必须原生 ARM64，不把 x64 仿真当作 QNN native。
- NVIDIA RTX：NvTensorRtRtx；具体代际/驱动仍以 SDK catalog 适用性及模型执行为准。
- 不自动引入 WebGPU、DirectML 或其他未要求的 EP。动态 catalog EP 当前官方要求 Windows 11 24H2/build 26100+；旧系统保留 CPU。

## 后台状态机与资源

`CPU ready → discover compatible providers → EnsureReadyAsync → result.Status 检查 → TryRegister → GetEpDevices → per-graph actual-model probe → profile 节点归属/数值/耗时门 → enable graph-specific EP`。

`FindAllProviders()` 已按设备兼容性筛选；不枚举后无差别下载安装所有 EP。每个兼容且白名单内的 provider 每宿主生命周期只准备一次，后台串行，无网络离线或驱动失败的无限重试。`--device cpu` / CPU-only 偏好禁止自动准备；默认 `auto`。进度和失败只写状态/诊断，不从后台污染 JSONL stdout。

使用真实请求形成有界内存样本，不写输入内容到磁盘、不发往外部服务。每张图最多一份、队列最多 3 份、每轮最多 8 个设备；不把媒体 encoder 的 512 列中间行当索引向量。模型文件继续保持只读共享锁。

资格门是源码默认保守策略，尚未实测：同一固定模型、同一真实张量，与 CPU 输出 shape/有限值/余弦至少 0.999、相对 L2 误差不高于 1% 对齐，最终句向量仍须归一化；以 profile 证明目标 EP 上有非复制/非纯形状节点执行；预热后 3 次中位执行耗时至少比 CPU 快 5%。不满足则不启用。每张图单独通过，不用文本成功豁免视觉/音频。资格仅覆盖保存的模态/张量形状/预期行数合同：未覆盖的特征路径或动态尺寸仍用 CPU，不将一份文本样本外推为所有媒体输入通过。同一合同内的全部语义内容仍需后续数值/召回测试；该门不是通用正确性证明或整包验收。

前台继续用 CPU；后台候选 session 只有通过后才在无在途请求时切换。CPU 基线可并发 Run，不持有前台 session 的切换锁；资源竞争和响应延迟仍需实测。active EP 运行失败或完整输出合同失败（包含输入相关的媒体行数）后解除该候选并用同模型 CPU 再执行一次；CPU 仍失败则显式报错。保留错误，不混用权重或吞成空数据。shutdown 取消后台准备/执行；native EP 编译不保证可中断，进程级终止仍由父适配器超时承担，未验证“无残留”。

## Profile 与性能缓存

通过数值门的 EP 与 CPU 使用同一 embedding profile，故障回退不换向量身份；该 profile 仍绑定模型、tokenizer、精度和预处理，不含设备路径或时间。profile v2 明确声明“已资格验证的逐图 EP”，不把 v1 CPU-only profile 静默重标。

性能身份快照必须绑定批次开始 epoch（`WML-EP-002`），并记录实际执行路线，而不是只记录可用候选；合同外输入走 CPU 也会更新路线 epoch。性能缓存不得因有一份旧 JSON 就算已校准。`WML-CUTOVER-001` 修复把当前身份准入放在读入和合并入口；估算命令不启动模型来求身份，未知时明确未校准。新增回归测试源覆盖无身份、不同身份、相同身份、标定失败；本轮不执行。独立的 performance profile 还绑定当前逐图 EP/设备及切换 epoch；跨请求切换或同一批次切换时不标为稳定标定，不用 embedding profile 代替性能身份。

## 修改与恢复范围

恢复点 `.dev/winml-ep-policy/{baseline.json,source-before/}`。write-set：新的宿主 EP 管理类；`EmbeddingEngine/Program/StdioServer/ModelManifest/NativeTokenizer`；RID/原生 tokenizer staging 的 csproj/PowerShell 与对应打包/安装/CI定义；Swift 工厂、性能缓存身份/API、CPU-only UI/API 和工程文档；相关测试源码。无更换模型/SDK/tokenizer 版本、无系统驱动安装、无真实索引迁移。ARM64 支持先落实宿主+tokenizer 的原生 RID；不把现有 x64 Swift/Tauri/FFmpeg/PDFium 分发链称为已验证的全 ARM64 应用。

## 官方接口来源

- https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/initialize-execution-providers
- https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/register-execution-providers
- https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/select-execution-providers
- https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers
- https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.SessionOptions.html

已查官方 API：`EnsureReadyAsync()` 结果 `ExecutionProviderReadyResultState.Success` 后 `TryRegister()`；`AppendExecutionProvider(OrtEnv, IReadOnlyList<OrtEpDevice>, IReadOnlyDictionary<string,string>)` 显式设备绑定。SDK 注册成功、session 可构造、真实节点执行、数值一致性和整包可用是不同证据层。本轮没有执行其中的运行门。

## 本轮源码与证据边界

已加入 `ExecutionProviderManager`：catalog 后台准备、注册、逐图真实张量资格门、实际节点 profile、数值与中位耗时比较、EP 故障同模型 CPU 回退；`info` 返回后台状态，Swift 工具入口可刷新该状态。已加入 ARM64 RID/tokenizer staging 与匹配 CRT/.NET 前置条件的源码，不等于原生 ARM64 二进制已构建。

静态检查记录 `.dev/winml-ep-policy/static-source-checks.json`：Python AST、shell `bash -n`、改动 JS 的 `node --check`、csproj XML、模型清单逐项一致、无旧后端调用、用户 README 未变。未主动进行 C#/Swift/Rust 构建验收，未运行回归测试、EP 下载安装或任何模型/应用，未验证速度或节点归属。源码编译诊断仍有 SDK 符号解析缺口，不能把这些静态检查当作运行通过。

首次独立静态复核见 历史本地审阅 `windowsml-provider-review.md`（原文不随源码公开），确认 `WML-CUTOVER-001` 已 source-addressed，并提出 `WML-EP-001/002`（媒体输出回退边界、批次性能身份竞态）。补丁已将媒体预期行数纳入 active EP/CPU 同一结果合同，性能快照绑定批次起始 epoch，并收紧到已观察的输入合同。

跟进子任务 `3f210f55-6d46-446d-8135-5bfa6dfd1cf6` 在 300 秒超时，已停止委派、没有最终审批回执。保留的 历史本地部分输出 `windowsml-provider-followup-partial.md`（原文不随源码公开） 认为 001/002 source-addressed，但指出 `WML-EP-003`（CPU 失败前未记账路线）。主代理据此将 CPU 路线 stamp 移到执行之前，失败/部分成功批次一律不带性能身份。该最后补丁只有源码核对，**未取得独立最终确认**；不得把部分报告或超时当通过。

最终状态：源码接入已落盘；静态最终复核未完成，编译/设备/数值/性能/资源/取消/安装等必要验收未执行，不能称 P1–P5 或可发布交付完成。没有进行现场 EP 安装、模型运行或真实索引操作。
