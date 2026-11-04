# WinSight（智寻）实际技术栈架构规范

> **状态**：经工程实测与全量测试闭环核准的**真实技术栈规范（As-Is Actual Stack）**。
> 所有依赖项均已完成版本对齐、代码锁定与物理验证。

---

## 1. 核心架构层级与依赖选型清单

| 架构层级 | 技术组件 / 库 | 当前版本 | 选型定位与核心职责 | 架构裁决与设计理由 (Trade-off) |
| :--- | :--- | :--- | :--- | :--- |
| **基础运行环境** | **.NET SDK / C#** | **.NET 11 (C# latest)** | 核心执行环境，目标框架定义为 `net11.0-windows10.0.26100.0`，最低支持平台统一为 Windows 11 (`10.0.22000.0`)。 | 统一采用最新 .NET 11 运行时，充分利用 Tiered PGO、AVX-512/Vector SIMD 指令集及低分配垃圾回收（GC）。 |
| **桌面 UI 框架** | **Windows App SDK (WinUI 3)** | **2.5.1** | 提供 Windows 11 原生 Fluent Design 控件树、Mica Alt 云母材质、DesktopAcrylic 亚克力效果及沉浸式标题栏。 | 硬件加速直接合成渲染，相比 WPF/Web 壳具备极佳的冷启动速度与系统一致性。 |
| **MVVM 模式** | **原生手写轻量 MVVM** | — | 基于 `INotifyPropertyChanged` 与 `SetProperty<T>` 手写属性通知，配合 `ActionCommand`。 | **架构取舍**：放弃引入 `CommunityToolkit.Mvvm`，消除 Source Generator 依赖及代码生成开销，保持编译极速、二进制体积最小化。 |
| **系统托盘** | **原生 Win32 P/Invoke 封装** | — | `TrayIconService`，基于 `Shell_NotifyIcon`、隐藏顶层工具窗口（ToolWindow）与 Win32 消息泵。 | **架构取舍**：放弃引入第三方托盘库，直接监听底层 `TaskbarCreated` 消息实现 Explorer 崩溃自愈，零第三方内存开销。 |
| **文本分词 (中文)** | **`winsight-segmenter`** | **0.1.0** (CDylib) | 原生 Rust 动态库 (`winsight_segmenter.dll`)，封装 `jieba-rs 0.11.0`。索引用搜索模式，查询用精确模式，保留词频。 | **架构取舍**：C# 托管分词算法吞吐量低且缺少成熟分词词典。Rust 编译为原生 C ABI 动态库，毫秒级处理万级字符且内存安全。 |
| **文本分词 (多语言)**| **`winsight-tokenizer`** | **0.1.0** (CDylib) | 原生 Rust 动态库 (`winsight_tokenizer.dll`)，精确锁定 HuggingFace `tokenizers` (rev `3012770`)。 | 满足 EmbeddingGemma 2 的 262,144 大词表、BPE 与 Special Tokens 编码，零隐式截断。 |
| **向量计算与检索** | **托管内存 SIMD 点积** | — | `System.Numerics.Vector<float>` 硬件加速单指令多数据流（SIMD）内联点积。 | **架构取舍**：在 10 万以内中型个人桌面语料下，内存连续 float32 配合 AVX2 每次查询点积仅需 ~15ms，相比侵入式的 C++ 向量库（如未成熟的 `sqlite-vec`）拥有更高的稳定性和零外部 DLL 故障率。 |
| **混合存储底座** | **Microsoft.Data.Sqlite** | **10.0.13** | 单文件 SQLite 嵌入式连接器，启用 WAL 模式与 NORMAL 同步。 | 托管无独立服务开销，支持跨进程只读并发，并以 FP16 BLOB（每个向量 1.5 KB）紧凑存储 768 维多模态嵌入。 |
| **全文倒排检索** | **SQLite FTS5** | 内置支持 | 双虚拟表分工：`fts`（porter + unicode61 负责拉丁词/精确标识符）+ `fts_seg`（中文分词倒排与真实 BM25）。 | 彻底摒弃全表词表扫描（`fts5vocab` 扫描），两路 FTS 均产生原生 BM25 评分并在截断前参与 RRF 融合。 |
| **文档内容提取** | **DocumentFormat.OpenXml**<br>**PdfPig** | **3.5.1**<br>**0.1.16** | 微软官方 OpenXML 解析 Word/Excel/PPT；PdfPig 纯 C# 按页提取 PDF 文本与坐标。 | 无需安装 Office 桌面软件，无需调用外部重量级 Poppler/PDFium C++ 依赖。 |
| **图像渲染与采样** | **Windows WIC (COM ABI)**<br>**Windows.Data.Pdf** | Windows 内置 | 基于 Windows Imaging Component 的高保真图像解码、方向旋转纠正与双三次下采样（Bicubic）。 | 复用 Windows 操作系统内置硬件编解码管线，杜绝 System.Drawing (GDI+) 线程死锁，不引入第三方图像库。 |
| **多模态推理 Worker** | **`WinSight.InferenceHost`** | **自研进程** | 独立 Windows 进程，搭载 `Microsoft.ML.OnnxRuntime 1.31.0`，承载 EmbeddingGemma-2 多模态模型。 | **架构取舍**：大模型推理必须进程隔离。绑定 Windows Job Object（`KILL_ON_JOB_CLOSE`），以低于正常优先级（`BelowNormal`）运行，杜绝后台卡顿前台 UI。 |
| **高精本地 OCR** | **`WinSight.OcrHost`** | **自研进程** | 独立 Windows 进程，搭载 `RapidOcrNet.WinSight` (PaddleOCR v4/v5 ONNX)。 | 针对票据、表格与倾斜排版提供高召回文本提取，彻底隔离 SkiaSharp 与 ORT 内存。 |
| **Agent MCP 协议** | **`winsight-mcp`** | **自研进程** | 独立可执行程序（由 `WinSight.Bridge` 生成），支持 Stdio 与环回 HTTP 两种传输通道。 | **架构取舍**：放弃引入庞大的 ASP.NET Core 和未成熟的官方 MCP SDK，手写极简 JSON-RPC 2.0，只读查库 + 单向信箱维护任务，确保绝对安全。 |
| **安装与打包系统** | **Inno Setup** | **6.3.3 / 6.7.3** | `desktop/installer/WinSight.iss`，生成每用户标准安装包。快捷方式直连 exe，支持自启清理与 PATH 注入。 | 规范安装至 `C:\Program Files\WinSight`，数据严格隔离至 `%LOCALAPPDATA%\WinSight\NativePreview`。 |
| **代码签名工具** | **Sign-NativeArtifacts** | PowerShell 7 | 基于 Windows SDK `signtool.exe`，支持 RFC 3161 时间戳与 PFX 凭据安全注入。 | 仅对 WinSight 自有二进制进行 Authenticode 签名校验，保护分发完整性。 |

---

## 2. 架构设计决策与取舍说明 (Architectural Decisions)

1. **为什么不用 Web 壳（Tauri / Electron）？**
   WinSight 的核心场景要求即时响应（亚秒级呼出 HUD 浮窗）和低内存常驻。WinUI 3 原生 XAML 渲染具备最轻量级的内存底座与对 Windows 11 Mica / Acrylic 的底层合成能力。
2. **为什么不引入第三方 MVVM 框架与 DI 容器？**
   桌面单机软件的依赖关系在生命周期内是确定的。放弃反射型 DI 容器和大型 MVVM 框架，使得应用的冷启动时间压制在 300ms 以内，彻底消除 JIT 加载几十个无用程序集的时间。
3. **为什么采用多进程而非多线程并发推理？**
   ONNX Runtime 和 PaddleOCR 在执行矩阵算子时会申请大量原生堆内存，偶发异常会直接导致进程 Crash。将其隔离在外部 Worker 中，不仅实现了进程崩溃自愈，而且能够通过 Windows Job Object 实现硬性资源管控。
