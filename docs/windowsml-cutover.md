# Windows ML 默认后端切换（源码阶段）

> 后续设备策略请求已取代本记录的 CPU-only 限定；当前后台候选准备和实际模型资格门见 [windowsml-provider-policy.md](windowsml-provider-policy.md)。

本轮授权：移除生产旧后端、移除实验性索引目录限制、默认启用新链路。延续“代码先集成，不进行运行验收”：不构建/运行应用或模型，不安装工具链，不读取、迁移或清空真实索引及用户模型目录。

## 修改边界与恢复

冻结当前已集成树，而非上游 HEAD：`.dev/winml-cutover/baseline.json` 和 `source-before/`。删除的生产 llama.cpp 目录/测试/脚本移至该任务的 `retired/`，不再参与 SwiftPM、构建或分发；旧 GGUF 数值参考不改写为 ONNX 参考。用户 README 和既有独立修改保留。

Write-set：`Package.swift`；`Sources/winsight/main.swift`、`DownloadModel.swift`、必要的模型/状态调用方；`Sources/WinSightKit/ModelFiles.swift`、`Indexer.swift`、`MeasuredCosts.swift` 及旧后端说明；`Sources/{LlamaRuntime,CLlamaWindows}` 与 `Tests/LlamaRuntimeTests`；`Tests/WindowsMLRuntimeTests` 的保留媒体样本及新合同测试源码、`Tests/WinSightKitTests/IndexStoreTests.swift` 的模型清单调用；llama 构建脚本/patch；开发、打包、源码检查、安装检查、notices 脚本；Windows 打包工作流和 `inference/global.json` 的 .NET 10 feature-band roll-forward；安装前置条件及前端模型大小/CPU 文案与对应 Rust API；工程文档。仅在这些实际调用链内改动，不重写用户 README，不改 SDK/模型版本，不删除用户缓存。

## 目标与保留合同

- Windows ML/ONNX 为唯一生产工厂；没有 legacy 选择、静默回退或必须设置实验开关的前提。
- 正常 `--index` 和默认目录可用，删除实验路径限制；**保留 profile、维度及错误一致性校验**。删除这些检查会把旧 GGUF 向量与 ONNX 查询混算，破坏数据完整性；本轮不这样做。
- 不自动清空旧索引。旧索引不兼容时明确报告；用户之后可显式重建。本轮不执行重建。
- 默认模型清单改为宿主固定的 11 个 ONNX/tokenizer/config 文件，包含音频图与 external data。下载器支持 `onnx/` 子目录，实际下载不在本轮执行。
- 文本、图像、音频、视频画面和时间窗声轨调用保留。失败项不标完成；协议超时/畸形响应继续报错。
- 开发/分发依赖改为私有推理宿主 + tokenizer DLL + Windows ML 库；当前 CPU 路线不声称 GPU/NPU 验收。framework-dependent 宿主需 .NET 10；不静默安装运行时。

## 失败模式与未执行断言

模型/manifest/native DLL 缺失、哈希不符、坏 WAV/无效图片、宿主终止/超时、回复行数或维度不符、旧索引 profile 不同，均应显式失败，不退回旧后端、不伪装为空数据。

后续负向断言：没有旧库时新工厂仍只选择 Windows ML；默认索引目录不再因“实验性”被拒绝；含旧向量的索引不得自动重置或混算；混合坏 WAV 批次保持对齐并继续处理。上述程序性断言本轮不运行。静态依赖核对及独立复核不能替代编译、媒体数值对齐、召回、安装、许可及真实迁移验收。

## 当前源码结果

已移除生产旧后端、SwiftPM 依赖、旧构建/分发入口及实验目录限制；默认工厂直接使用 Windows ML。11 项模型清单与宿主逐项静态核对一致；3 个媒体样本与移除前字节一致。CPU 标定记录增加可选 profile 身份，防止沿用旧后端的标定；旧 JSON 可解码，未执行标定。

仅执行源码/语法检查：Python AST、5 个 shell 脚本的 `bash -n`、依赖调用方核对、模型清单与恢复副本哈希核对。未运行测试套件、模型、应用或安装。编辑工具的 SDK/编译诊断仍未通过手工构建归因；YAML 中既有长行诊断不表示运行失败或通过。

原始独立静态复核见 历史本地审阅 `windowsml-cutover-review.md`（原文不随源码公开）。`WML-CUTOVER-001` 已补源码：读取/合并需当前已核实性能身份，无身份缓存不作已校准；未知身份估算不会启动模型，明确未校准；已补未执行的回归测试。后续设备切换 epoch 也绑定性能身份。最终跟进复核与设备策略一起记录。

状态：源码切换已落地，设备策略/修复待最终静态跟进；编译/运行、许可、完整安装与 P1–P5 验收未通过或尚未执行。
