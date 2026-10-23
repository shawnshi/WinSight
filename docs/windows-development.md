# WinSight Windows 开发说明

当前源码为 Tauri/Rust + Swift 文件处理/SQLite，独立 .NET Windows ML/ONNX 推理宿主；不是 WinUI 重写，也没有新增 MCP 服务。用户根目录 README 保持原样。

本轮按“代码先集成，不进行运行验收”工作。Windows ML 已设为源码默认且唯一后端；这不是编译、模型执行、召回或安装验收通过。旧后端移除与恢复记录见 [windowsml-cutover.md](windowsml-cutover.md)。

## 模块

- `shell/ui/`：HTML/CSS/JS，Tauri 2 / WebView2。
- `shell/src-tauri/`：Windows 桌面壳、托盘、快捷键、窗口及子进程管理。
- `Sources/winsight/`：Swift CLI、搜索/索引服务与 JSONL 工具入口。
- `Sources/WinSightKit/`：提取、SQLite/FTS5、排序、Windows ML 进程适配器。
- `inference/WinSight.InferenceHost/`：Windows App SDK ML 2.1.94 携带的 ORT，CPU 图执行、预处理和有界 JSONL。
- `native/winsight-tokenizer/`：固定 tokenizers 0.23.1 的 C ABI。
- `Sources/CPDFium/`：PDFium 接口。生产树不再包含 CLlama/LlamaRuntime。
- `Tests/WindowsMLRuntimeTests/Fixtures/`：保留的公开图片/语音样本；旧 GGUF 数值参考已归档，不作为 ONNX 真值。

## 模型与索引

`ModelFiles.swift` 与宿主 `model-manifest.json` 使用同一固定社区 revision 的 11 个文件，总计 505,137,637 bytes，包含文本、视觉和音频图及其 external data。默认下载到 `%LOCALAPPDATA%/WinSight/Models`。`WINSIGHT_ONNX_MODELS` 或 `WINSIGHT_MODELS` 可覆盖；`WINSIGHT_INFERENCE_HOST` 可覆盖宿主位置。

不再需要实验性后端开关，默认索引和普通 `--index` 可用。旧 GGUF 文件不读取、不自动删除，也没有回退路径。旧向量不能与 ONNX 查询混算：保留 profile/维度校验，不会自动清空旧索引。需要时由用户显式重建；本轮没有打开或重建真实索引。

文本、图像、音频和视频声轨仍通过共同的 768 维图。视频画面沿用逐帧图片，音轨保留 30 秒窗/25 秒 hop、时间定位及失败语义；不宣称多帧视频模型或声轨检索已验收。默认从 CPU 服务开始，按 [设备候选策略](windowsml-provider-policy.md) 在后台准备、注册适用 EP，用实际模型资格门决定逐图启用。CPU-only 偏好可禁止后台准备；表格本身不代表 GPU/NPU 已通过。

## 后续开发环境

预先准备 Windows x64、Swift 6.4、MSVC Build Tools/Windows SDK、Rust/Cargo、Git Bash、CMake/Ninja、Node、Python、PowerShell 7 和 .NET 10 SDK。FFmpeg 构建还需 MSYS2 MINGW64。应用界面需 WebView2。

框架依赖的推理宿主需要 **与宿主匹配的 .NET 10 x64/ARM64 Runtime**。Windows ML app-local 不代表打包了 .NET CLR；安装器只检查前置运行时，不静默安装。打包时把对应宿主架构的 C++ runtime 放入私有 `helper/inference/`。QNN 只在原生 ARM64 宿主上验证；构建脚本支持 `-RuntimeIdentifier win-arm64` 和 `aarch64-pc-windows-msvc` tokenizer target。该 target/对应 MSVC 组件及 RID restore assets 须事先准备，未执行 ARM64 构建或设备验收；不是全应用原生 ARM64 分发已经完成。干净环境安装尚未验证。

本轮没有安装上述工具，也没有运行下列命令；它们只是未来获准后的操作说明。

```bash
# MSYS2 MINGW64:
scripts/build-ffmpeg.sh

# x64 MSVC 环境中的 Git Bash:
export PATH="$(dirname "$(command -v cl)"):$PATH"
scripts/fetch-vendor.sh
cargo fetch --locked --manifest-path native/winsight-tokenizer/Cargo.toml
(cd inference && dotnet restore WinSight.InferenceHost/WinSight.InferenceHost.csproj --locked-mode --runtime win-x64)
# 原生 ARM64 宿主改用 --runtime win-arm64；还需对应 Rust target/MSVC 组件。
swift build -c release --product winsight
scripts/dev-run.sh
```

开发启动仍使用项目 `.dev/` 下的数据，不是安全沙箱。`build-inference-host.ps1` 构建/暂存私有宿主，离线 Cargo、禁止自动 restore，不执行宿主/模型。`dev-run.sh` 与 `package-windows.sh` 接入该脚本；需要事先准备依赖缓存和 restore assets。

## 打包与边界

`scripts/package-windows.sh` 把宿主及 tokenizer/SDK 库放入 `helper/inference/`，不再打包 llama/ggml/Vulkan。`.github/workflows/packages.yml` 的定义改为 Windows ML 准备；未推送、未触发 CI。

**`scripts/check-windows-package.sh` 会实际安装到 `%LOCALAPPDATA%/WinSight`，可能下载模型并覆盖安装版，只能在可丢弃 VM/CI 中运行。本轮没有执行。**

新增依赖的许可、完整 notices、LGPL 来源及重链接义务仍需发行审查，[media-attribution.md](media-attribution.md) 不是审查通过证明。模型权重不随安装器分发，其上游条款单独适用。编译、tensor 数值一致性、跨模态召回、进程清理、合成索引重建及回滚、安装闭包均待后续验收。

Windows OCR、旧 `.doc`、RAW/SVG 等仍有既有限制。SQLite 含路径/文本，不因本地运行而自动加密。更新端点/公钥仍未配置，未启用上游更新服务。图标与品牌来源见 [winsight-rename.md](winsight-rename.md)，原 Windows 精简来源为 ARahim3/DigUp `7db9537712995d14e50e0e44baa00f721b98f1c6`。
