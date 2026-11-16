# WinSight（智寻）

穿透图文，洞见桌面；智寻于毫秒，赋能本地智能体。
See Through Files, Instantly. Local Multimodal Search for Windows and Agents.



WinSight（智寻）是一款基于 .NET 11、WinUI 3 与多模态嵌入模型的 Windows 11 原生桌面检索中枢，在零云端依赖的纯本地环境下实现跨文档、代码与截图的毫秒级图文混搜，并通过标准化模型上下文协议（MCP）无缝挂载为本地 AI Agent 的全知外部记忆。
WinSight is a Windows 11-native desktop search hub powered by .NET 11, WinUI 3, and on-device multimodal embeddings—delivering millisecond-level semantic retrieval across documents, code, and images with 100% privacy, while exposing standard Model Context Protocol (MCP) endpoints for local AI agents.



WinSight 是面向 Windows 11 的本地多模态文件检索项目，用关键词与深度语义向量联合查找本机文档、代码、图片及媒体片段。项目以 .NET 11、WinUI 3 (Windows App SDK 2.5.1)、SQLite FTS5 和独立推理隔离进程实现，不依赖远程模型 API，所有索引与向量计算完全在本地完成。



> **当前状态（2026-11）：v1.1.0 正式里程碑，运行在 .NET 11 预览运行时上。** 全链路多模态文档检索、PDF/PPT/Word/Excel 持久化温态栅格预览与页面步进、RapidOCR 交互式标定框点击复制、旧版 Office 格式（.doc/.xls/.ppt）隔离抽取与只读来源跳转均已稳定合入主线。桌面主控制台采用 Windows 11 原生 Mica Alt + `LeftCompact` 紧凑导航与统一 Fluent 2 设计系统，支持页面状态保活与窗口坐标记忆；快捷搜索 HUD 浮窗采用无边框圆角 DesktopAcrylic 亚克力材质，按鼠标所在显示器自适应居中弹出。后端引擎已升级为 **64 槽位 LOH-free 分块 Int8 标量量化向量池（`CompactVectorIndex` + `Avx2` 预量化 SIMD 点积 + CoW 增量补丁）** 与 **SQLite WAL 读写双连接分离**（600 文件基准测试下冷加载内存较分块前降低 53.8%，核内热检索耗时低于 1 ms），并在双芯片平台上实现自适应异构加速：AMD Ryzen AI 平台由 AIE 硬件微码（.rai）驱动 NPU 视觉特征提取与 DirectML GPU 融合，Intel 平台由 OpenVINO / DirectML 驱动紧凑型模型。采用 Inno Setup 7 标准交付完整形态 B 安装包，自动感知 CPU 架构（AuthenticAMD vs GenuineIntel）实现零磁盘浪费的按需条件安装。

> **预览运行时与已知限制。** 本版本构建于 .NET 11 RC（`desktop/global.json` 锚定 `11.0.100-rc.2.26504.105` 并配置 `rollForward: latestFeature`，项目属性配置 `<RollForward>LatestPatch</RollForward>`）：在 .NET 11 正式版发布并完成升级验证之前，请按预览版使用；`docs/native-migration/status.json` 的 `releaseApproved` 仍为 `false`。
> - **规模与内存**：向量引擎采用 64 槽位 LOH-free 分块 Int8 标量量化紧凑池（单块 49,152 B，脱离 .NET 大对象堆；支持 `Avx2.MultiplyAddAdjacent` 预量化 SIMD 内积与写前并发版本隔离）+ 按需 SQLite 主键回表水合（`HydrateSemanticHitsAsync`），并配备推理宿主多模态图闲置 5 分钟自动卸载与内存压力联动回收；索引期间单文档内联嵌入上限为 64 段，超长文档标为 `partial`，支持通过 `winsight-native backfill` CLI 命令或桌面端“一键后台补全向量”游标断点补齐。
> - **文档格式**：`.docx/.xlsx/.pptx/.odt/.rtf/.pdf/.txt/.csv/.html` 直接抽取正文；非 UTF-8 文本会按字节结构识别 GBK/GB18030。旧版 `.doc/.xls/.ppt`（及 `.dot/.xlt/.pot/.pps`）由隔离的推理宿主进程解析：只取正文、表格单元格与幻灯片/备注文字，加密文件会被跳过并说明原因。WPS 专有格式 `.wps/.et/.dps` 暂不抽取，仅可按文件名检索。
> - **MCP 桥**：只读且不加载嵌入模型，`mode=hybrid` 与 `keyword` 同为词法检索；`index_directory` 需要先在应用的 MCP 控制台创建维护授权。强制执行 `SafeFile` 祖先重解析点（Junction/Symlink）穿透阻断、句柄级修改时间复核及环回 HTTP Chunked `1 MiB` 请求体上限（超限返回 `413`）；返回的文件内容一律视为不可信数据。
> - **USN 追赶**：需要卷读取权限；无权限或日志不可用时回退到 FileSystemWatcher + 定期对账，实际状态见“存储与模型”页的 `USN` 字段。

---

## 1. 项目定位与核心特性

* **面向谁**：需要在海量本机资料、扫描件、多模态媒体和代码中快速查找内容，并精确回跳到来源片段的 Windows 用户与开发者。
* **解决什么问题**：文件名无法完全概括文档主旨，传统关键词匹配无法命中语义相近的表达或图片中的文字。WinSight 将正文提取、OCR 文字、位置单元与 768 维向量嵌入纳入统一的混合检索链路。
* **用户形态**：

  * **WinUI 3 桌面主控制台**：`LeftCompact` 紧凑导航 + `Ctrl+1..5` 全局切页且页面状态全程保活；支持检索模式切换（混合/语义/关键词，纯关键词故障明确提示错误而非空列表）、可点击语法过滤胶囊、文档页内翻页、`SourcePreviewPane` 滚轮缩放与 OCR 蓝框点击复制；目录管理支持文档/代码仓库 (`code:`) 双视图与文件夹拖拽授权；存储页集成向量覆盖率进度、一键后台补全、缓存清理与数据库空闲页回收；设置页支持开关即改即存、快捷键录制器与原生 24 小时制 `TimePicker`。
  * **HUD 快捷搜索栏 (`QuickSearchWindow`)**：无边框圆角 Acrylic Spotlight 悬浮框，自动在鼠标所在显示器弹出；支持图片与文档栅格预览，`Ctrl+C` 复制完整路径、`Ctrl+Shift+C` 复制摘要，与主窗口快捷键心智完全统一。
  * **返回 JSON 的原生 CLI (`winsight-native.exe`)**：支持脚本化配置、全量/增量建库、单次检索、后台原位向量补全 (`backfill`) 与脱敏诊断。
  * **本地 Agent MCP 桥接器 (`winsight-mcp.exe`)**：随应用内置提供，支持标准 Stdio 管道与环回 HTTP，控制台支持一键导出 Claude Desktop / Cursor / Windsurf / VS Code & Pi 客户端配置并内置桥接器健康探针。
* **纯正本地与隐私承诺**：默认不全盘遍历扫描；不向任何云端上传个人文件或检索词；不包含用户行为遥测。

---

## 2. 核心功能与技术边界

|能力|当前范围与实现机制|
|-|-|
|**文档与代码提取**|支持 DOCX 段落/表格、PPTX 幻灯片/正文/备注、XLSX 工作表/单元格上下文；另有纯文本、PDF 文本、HTML 与独立代码文件索引。扫描 PDF 逐页执行 WIC 渲染与 OCR 行框标定（上限 32 页/180 秒）；内置持久化 `--preview-stdio` 预热预览服务与 SHA-256 磁盘缓存（200 文件 / 150 MB LRU，页数持久化保留），实现 Word/Excel/PPT/PDF 秒级高清栅格渲染与只读来源定位。|
|**图片与多模态**|基于 Windows Imaging Component (WIC) 硬件加速解码与双三次采样；独立进程加载 RapidOCR (PaddleOCR ONNX) 执行文本检测与识别；提取 EXIF 白名单元数据，杜绝个人隐私泄露。|
|**音视频片段**|原生音频 16kHz PCM 采样（30 秒窗口、5 秒步进跳跃）与视频关键帧采样嵌入；定位为片段检索，不作为离线语音全量转录服务。|
|**混合检索与排序**|中文简繁归一化（FormKC）、多词中文分词召回优化、中英混排、精确编号与字段过滤。双路 FTS：`fts`（porter + unicode61 处理拉丁词/路径）与 `fts_seg`（基于 Rust jieba-rs 的中文分词倒排与真实 BM25 词频），并在二阶段混合检索中复用词法快照结果。融合层结合文件名、目录与语义通道按位置单元执行 RRF 融合打分。|
|**增量与后台索引**|基于 SQLite 持久任务队列（`index_tasks`，Prepared 批量入队与 `Attempts == 8` 死信封顶）与 `index.sqlite` / `code.sqlite` 双库双队列隔离；按同卷分组执行单趟 NTFS USN Journal 离线追赶，监控已处理文件通过定时合并唤醒避免全盘回退。四类并发预算（交互查询/新文件/历史补建/复杂解析）配合时间段与“仅接电源”策略闸门调度，交互查询优先于后台批次。|
|**命令行工具**|`configure`、`status`、`index`、`search`、`backfill`、`model-info`、`diagnostics`；支持绝对路径 `--data-dir` 隔离。|
|**平台支持**|Windows 11 x64（最低运行版本 Windows 11 21H2 Build 22000）；模型计算以 CPU (SIMD AVX2) 和 DirectML/NPU 为主。|

---

## 3. 核心架构与模块划分

```
WinSight/
├── desktop/
│   ├── src/
│   │   ├── WinSight.App/            # WinUI 3 桌面端主程序 (XAML, 8 个全汉化视图, 托盘与热键)
│   │   ├── WinSight.Bridge/         # 本地 MCP 桥 (编译输出: winsight-mcp.exe)
│   │   ├── WinSight.Cli/            # 命令行管理工具 (编译输出: winsight-native.exe)
│   │   ├── WinSight.Core/           # 零依赖核心契约、实体模型、AppLog 滚动日志、更新检查
│   │   └── WinSight.Infrastructure/ # 存储、检索、分词封装、提取、调度解耦分部类
│   │       ├── Runtime/             # DesktopBackend 拆解为 Indexing/Search/Devices/Maintenance
│   │       ├── Storage/             # SQLite WAL 双连接读写分离、FTS5、CoW 向量池与代际重建
│   │       ├── Search/              # SearchService (AVX2 Int8 点积, 动态误报率显著性门槛)
│   │       ├── Indexing/            # USN 同卷追赶、双库增量队列、死信与任务状态机
│   │       └── Media/               # 进程通信、音视频采样、Office 安全互斥策略
│   └── installer/                   # 主线安装脚本 (WinSight.iss) 与历史契约归档 (legacy/)
├── inference/
│   ├── WinSight.InferenceHost/      # 默认多模态嵌入 Worker (Windows ML 2.4.89 / ORT 1.27.1，含签名锁定 EP 加载)
│   ├── WinSight.OcrHost/            # 隔离的 RapidOCR 识别 Worker
│   ├── WinSight.WinMlHost/          # 可选实验性 Windows ML 嵌入宿主
│   └── WinSight.FoundryHost/        # 可选硬件提供程序注册管理器 (Foundry Local)
└── native/
    ├── winsight-segmenter/          # jieba-rs 中文分词 C ABI 动态库
    └── winsight-tokenizer/          # HuggingFace Tokenizers C ABI 动态库
```

* **高聚合拆解**：`DesktopBackend` 拆分为生命周期、索引管线、混合检索、硬件探测与运维调度 5 个单一职责的分部文件，消解了单文件上帝类；后台监控通过 `BackgroundIndexCoordinator` 的 `inflightOps` 与 `queueWorkGate` 保障卸载零泄漏。
* **进程树隔离与 Office 安全策略**：推理、OCR 与硬件探测子进程均绑定 Windows Job Object（`KILL_ON_JOB_CLOSE`）并在 `BelowNormal` 优先级下运行，父进程退出或超时自动回收进程树；持久化 Office 预览通过 **PID 差集**仅托管其独占拉起的无界面实例（`Visible == false && openCount == 0` 才关闭），结合跨进程 `PowerPointPolicy.AutomationMutex`、发布者签名校验及无界面失效密码哨兵，不误关用户既有文档窗口。
* **并发互斥安全**：引入 `IndexRunLock` 独占文件锁，在 App、CLI 和后台定时补建/轮询任务之间建立跨进程互斥栅栏，防止 SQLite 数据库多写争用。

---

## 4. 技术栈总览

技术栈与依赖版本以 `desktop/Directory.Build.props`、`inference/Directory.Build.props`、各项目 `.csproj` 与 `packages.lock.json` 为准。

|层次|当前实现与版本选型|
|-|-|
|**运行时与 SDK**|**.NET 11 RC (`net11.0-windows10.0.26100.0`)**；桌面端、桥接器与推理宿主统一面向 Windows 11 RTM (`10.0.22000.0`) 及以上版本，构建开启 `RestorePackagesWithLockFile` 与 `<RollForward>LatestPatch</RollForward>`。|
|**桌面 UI**|**Windows App SDK 2.5.1 (WinUI 3)**；原生 Mica Alt 与 DesktopAcrylic 材质合成，手写轻量 MVVM（零反射开销）。|
|**向量嵌入模型**|**EmbeddingGemma-2 ONNX (q4 量化)**；768 维单向量空间多模态对齐（文本/图片/音频），内存常驻约 ~175 MB。|
|**向量检索引擎**|64 槽位 LOH-free 分块紧凑 Int8 标量量化内存向量池（`CompactVectorIndex`，Copy-on-Write 隔离 + **`Avx2` 预量化 Int8 内积加速**与 `inflightVectorWrites`/`vectorCacheSeq` 并发事务防陈旧缓存），配合按需回表水合仅提取 Top-K 命中上下文。|
|**词法与倒排检索**|**Microsoft.Data.Sqlite 10.0.13** (WAL 模式，**`ReadWrite` / `ReadOnly` 双连接读写分离** + `PRAGMA busy_timeout=5000`) + **FTS5**；`fts` 负责拉丁词/精确标识符，`fts_seg` 负责中文分词与真实 BM25 词频计算。|
|**双原生分词库**|**Rust CDylib 原生库**：`winsight_segmenter.dll` (jieba-rs 0.11.0) + `winsight_tokenizer.dll` (HuggingFace tokenizers rev `3012770`)。|
|**多媒体与图像解码**|**Windows Imaging Component (WIC COM ABI)** 硬件加速解码与方向旋转，Windows.Data.Pdf 页面矢量渲染。|
|**OCR 文字识别**|**`RapidOcrNet.WinSight 4.2.0-winsight.1`**；隔离进程加载 PaddleOCR ONNX 权重，内存完全独立。|
|**Agent MCP 桥**|**`winsight-mcp.exe`**；自研轻量 JSON-RPC 2.0 协议引擎（禁用 `PRAGMA mmap_size`），支持 Stdio 与 Bearer 令牌鉴权的环回 HTTP（1 MiB Chunked 请求体封顶），强制 `SafeFile` 祖先重解析点校验。|
|**安装与发布系统**|**Inno Setup 6/7** 标准安装包；默认安装至 Program Files（亦支持 `/CURRENTUSER` 单用户模式），开启 `ChangesEnvironment=yes` 与 `REG_EXPAND_SZ` 写入 `PATH`，卸载保留 `{localappdata}\WinSight\NativePreview` 用户数据交互确认。|

---

## 5. 业务流程与检索流水线

### 索引生成管线

1. **授权与作用域过滤**：用户在“目录管理”页或 CLI 明确添加扫描目录，应用数据目录与索引目录天然处于排除列表。
2. **增量比对与 USN 追赶**：按 NTFS 卷聚合根目录执行单趟 NTFS USN Journal 增量追赶并共享检查点；无权限或日志溢出时平滑降级为受控目录树差集比对，单文件失败累计 8 次进入终端死信（不再盲目唤醒轮询）。
3. **分块与边界自适应**：

   * 采用 **Token 估算预算切块**（英文约 4 字符/Token，中文约 1.5 字符/Token，单块上限约 ~450 Token）；
   * 优先对齐段落、换行、句末标点（`。！？；`）与逗号，解决传统空白切块导致的中文跨词截断。
4. **多模态批处理嵌入**：

   * 文本段落添加 `title: {文件名} | text: {切块}` 提示词；
   * 图像生成 280-token 视觉嵌入，PDF 页面提取 OCR 文本行后再次生成多级切块（单文档内联超 64 段时标记为 `partial` 交由游标式 `backfill` 补全）；
   * 写入前校验有限值、非零与 768 维 L2 归一化规范。
5. **持久化与代际隔离**：文档写入 `index.sqlite`，代码写入 `code.sqlite`。向量以 FP16 BLOB 紧凑存储，更新操作采用原子事务。

### 混合检索流水线

1. **查询分析**：解析 `kind:`、`root:`、`project:`、`amount:` 等结构化过滤字段，分离拉丁词与中文词汇。
2. **双路全文匹配**：

   * 拉丁词走 `fts` 匹配，支持 Porter 词干还原与通配符；
   * 中文词由 `winsight_segmenter.dll` 分词后直接 MATCH `fts_seg`，获得真实 BM25 分值。
3. **向量语义召回**：

   * 查询生成向量后，通过 `QuantizeQuery` + `DotInt8PreQuantized`（`Avx2` 整数乘加）计算候选片段的余弦相似度；
   * **动态误报率显著性门槛**：基于通道单元总量动态计算标准正态分位数（控制每个通道每次查询预期误报仅 ~1 条），彻底消除传统固定 $z \ge 2$ 在大语料下固定放行 2% 噪声的缺陷；样本少于 5 个时退回 $\text{cosine} \ge 0.5$ 小样本底线。
4. **RRF 倒数排名融合**：

   * 关键词通道（权重 2.0）、语义通道（权重 1.0）、文件名（权重 1.5）、目录路径（权重 0.7）按来源单元做 RRF 融合打分，取 Top-K 返回结果。

---

## 6. 部署、安装与运行指南

### 1. 终端用户安装使用

* **现成安装包直接安装**：
可以直接运行仓库中已编译打包好的完整安装包：[**`releases/WinSight-1.1.0-x64-setup.exe`**](releases/WinSight-1.1.0-x64-setup.exe)（全模态完整安装包形态 B，主安装程序 `WinSight-1.1.0-x64-setup.exe` 配合 `WinSight-1.1.0-x64-setup-1.bin` 单一数据卷，经跨宿主共享私有运行时、重复模型去重与固态流压缩优化后，分发体积由 5.48 GB 骤降至 1.21 GB）。自动探测底层 CPU 芯片平台（AMD Ryzen AI vs Intel Core Ultra / OpenVINO），根据硬件环境**仅解压安装目标平台所需的最优模型与微码**，非目标平台文件完全跳过，实现真正的开箱即用且零磁盘冗余。
* **分卷校验和**：
  * 主程序：`f72d9a72ad33bc0982fed384943965f0d1367d0e5d45ef331d4a3fa93be14d4b  WinSight-1.1.0-x64-setup.exe`
  * 数据卷 1：`f249ec9a4604ef9f4b9c3cf71ef00d608a883a4319c1534e368eaa4f2e43860d  WinSight-1.1.0-x64-setup-1.bin`
* **标准安装路径**：双击运行安装向导，默认安装至 **`C:\Program Files\WinSight`**。
* **安装包特性与可选任务**：

  * [x] **全模态模型开箱即用**：完整内置打包 11 个 EmbeddingGemma-2 向量权重文件 + 4 个 RapidOCR 文字定位与识别模型，安装即可直接体验毫秒级图文混搜与 GPU/DirectML 硬件加速，无需任何额外模型下载配置；
  * [x] 注入官方高保真多分辨率图标 (`WinSight.ico`)；
  * [x] 创建桌面快捷方式（默认不勾选，保持桌面整洁）；
  * [x] 将 WinSight 命令行工具自动添加至系统 PATH（勾选后可直接在任何终端键入 `winsight-native` 或 `winsight-mcp`）；
  * [x] 智能占用防护：安装与覆盖升级前自动检测并提示优雅关闭 7 类运行中进程，杜绝文件锁死。
* **开始菜单与秒级冷启动**：快捷方式直接原生拉起 `WinSight.App.exe`，启动时间 < 300ms，无控制台黑框弹出。

* **卸载清洁度**：卸载程序自动清理注册表 Run 自启动项与 PATH 环境变量；卸载时交互弹窗友好询问是否保留个人已建立的索引数据库与设置（默认安全保留）。

### 2. 开发者环境准备与构建

1. **环境依赖**：

   * 操作系统：Windows 11 x64 (Build 22000+)；
   * .NET SDK：11.0.100 RC（参阅 `desktop/global.json` 及 `Directory.Build.props`）；
   * Rust 工具链：`stable-x86_64-pc-windows-msvc`（包含 cargo）；
   * Inno Setup 6/7：用于本地编译打包（可选）。
2. **编译原生动态库**（含 MSBuild 源哈希绑定回执）：

```powershell
cargo build --release --locked --manifest-path native/winsight-segmenter/Cargo.toml
cargo fetch --locked --manifest-path native/winsight-tokenizer/Cargo.toml
./scripts/build-native-tokenizer.ps1
```

3. **锁定恢复、构建与运行合成测试套件**（仓库无单一 `.sln`，各组件与测试均为独立项目；完整 10 项 CI 矩阵见 `.github/workflows/ci.yml`）：

```powershell
# 示例：锁定还原并构建桌面主程序与 MCP 桥接器
dotnet restore desktop/src/WinSight.App/WinSight.App.csproj -r win-x64 --locked-mode
dotnet build desktop/src/WinSight.App/WinSight.App.csproj -r win-x64 --no-restore -c Release
dotnet restore desktop/src/WinSight.Bridge/WinSight.Bridge.csproj -r win-x64 --locked-mode
dotnet build desktop/src/WinSight.Bridge/WinSight.Bridge.csproj -r win-x64 --no-restore -c Release

# 示例：运行核心合成测试套件
dotnet run --project desktop/tests/WinSight.Storage.Tests/WinSight.Storage.Tests.csproj -c Debug
dotnet run --project desktop/tests/WinSight.Runtime.Tests/WinSight.Runtime.Tests.csproj -c Debug
dotnet run --project desktop/tests/WinSight.Bridge.Tests/WinSight.Bridge.Tests.csproj -c Debug -- "$env:TEMP\winsight-bridge-test"
```

4. **打包发布暂存包**：

```powershell
# 准备完整发布目录 (包含全平台模型)
./scripts/Prepare-NativeRelease.ps1 -StageDirectory "D:\projects\WinSight\.dev\native-migration\stage-1.1.0" -IncludePlatformModels
# 生成安装包
./scripts/Build-NativeInstaller.ps1 -StageDirectory "D:\projects\WinSight\.dev\native-migration\stage-1.1.0" -OutputDirectory "D:\projects\WinSight\releases"
```

### 3. 原生 CLI 使用示例

```powershell
# 1. 配置监控根目录与模型路径
winsight-native configure --data-dir "C:\MyData" --root "C:\Documents" `
    --host "C:\Program Files\WinSight\inference\WinSight.InferenceHost.exe" `
    --models "C:\Program Files\WinSight\models" --manifest "C:\Program Files\WinSight\inference\model-manifest.json"

# 2. 查看当前状态与索引指标
winsight-native status --data-dir "C:\MyData"

# 3. 执行单次索引建立
winsight-native index --data-dir "C:\MyData" --root "C:\Documents"

# 4. 执行多模态混合检索
winsight-native search --data-dir "C:\MyData" --query "2026年政府采购方案" --mode hybrid --limit 10

# 5. 对历史未嵌入文本片段执行原位向量补全（支持中断续传）
winsight-native backfill --data-dir "C:\MyData"

# 6. 导出脱敏的系统诊断包（供故障排查）
winsight-native diagnostics --data-dir "C:\MyData" --out "C:\MyData\diag.zip"
```

---

## 7. 运维、诊断与稳定性设计

* **滚动日志与崩溃自愈**：

  * GUI 进程向 `<data>\logs\winsight-app.log` 写入滚动日志（单文件 2 MiB，保留 8 个）；
  * 注册了 `AppDomain.UnhandledException` 与 `WinUI.UnhandledException` 全局捕获器，未处理崩溃独立输出为 `crash-*.txt`（保留 10 个）；
  * CLI 操作向 `winsight-cli.log` 记录执行流水。
* **脱敏诊断包一键导出**：

  * 在设置页“关于”卡片中点击“导出诊断日志包”，或通过 CLI 执行 `diagnostics`；
  * 导出的 zip 包仅包含系统环境信息、日志、设置和文件结构清单；用户名、计算机名与个人主目录路径在打包时**自动正则脱敏替换**，严禁包含任何文档全文或向量内容。
* **开机自启动与死链自纠**：

  * 设置页勾选“开机自动启动”后，写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；
  * 携带 `--autostart` 参数启动时仅安静驻留托盘，手动启动始终显示主窗口；应用启动时会自动纠偏因升级或路径变化导致的自启死链。
* **网络下载防御与退避**：

  * 硬件执行提供程序（Foundry Local）的在线下载默认关闭（`installExecutionProviders = false`）；
  * 开启后下载全程挂载 Windows Job Object，超时强制终止子进程树；下载失败自动触发 **1小时 / 6小时 / 24小时 阶梯退避**，杜绝死循环消耗流量。
* **可选版本更新检查**：

  * 用户可配置私有 HTTPS JSON 规范源（`{"version":"x.y.z","url":"https://..."}`）；
  * 点击“检查更新”仅做无痕只读拉取对比，绝不在后台静默下载或替换二进制。

---

## 8. 自动化测试与质量门禁

项目在 GitHub Actions (`.github/workflows/ci.yml`) 的每次推送与 PR 中，固定执行以下 10 项模型无关合成测试套件的自动化验证（全部测试均在无人工干预的隔离沙箱中跑通）：

1. **`WinSight.DesktopPolicy.Tests`**：验证热键语法解析、注册防冲突回滚、命令行参数解析（`--autostart`、`--data-dir`）。
2. **`WinSight.Inference.Tests`**：验证推理子进程生命周期、子进程 BelowNormal 优先级控制、崩溃优雅重连与取消结算。
3. **`WinSight.Runtime.Tests`**：验证滚动日志轮转、崩溃脱敏导出、硬件提供程序退避与 Job 清理、开机自启纠偏、文件锁互斥、双库代码隔离（`code.sqlite`）防全盘回退、后台任务有界排空及 MCP 维护任务消费循环。
4. **`WinSight.Pause.Tests`**：验证索引暂停/恢复状态机、USN 日志补齐收敛、并发取消与句柄释放。
5. **`WinSight.Storage.Tests`**：验证 SQLite WAL 事务原子性与并发防陈旧缓存提升、64 槽位 LOH-free CoW 向量池、AVX2 预量化 Int8 点积等价性、终态死信封顶（`Attempts == 8`）与游标 `backfill`、多根同卷 USN 追赶、FTS 双虚拟表与中英文词法召回。
6. **`WinSight.Indexing.Tests`**：验证 Token 估算预算切块、CJK 标点自适应重叠、Unicode 代理对安全与异常格式容错。
7. **`WinSight.IndexingSettlement.Tests`**：验证任务队列状态机、注入故障结算与持久化。
8. **`WinSight.DocumentSearch.Tests`**：验证 Office 结构化字段提取、Excel 单元格交集、Word 坐标定位。
9. **`WinSight.DocumentSearch.Tests --review-cases`**：验证深层复杂富媒体排版回归。
10. **`WinSight.Bridge.Tests`**：验证 MCP 客户端 Stdio 与命名管道协议契约、祖先重解析点（Junction）阻断、Chunked `1 MiB` 超限拦截（`413`）及只读 `PRAGMA mmap_size=0` 隔离。

---

## 9. 许可证与开源归属

* 本仓库采用 [Apache-2.0 许可证](LICENSE)。
* 模型权重及第三方依赖遵循各自开源授权，分发时自动附带第三方声明文件 [THIRD-PARTY-NOTICES.txt](desktop/THIRD-PARTY-NOTICES.txt)。

