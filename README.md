# WinSight（智寻）

穿透图文，洞见桌面；智寻于毫秒，赋能本地智能体。
See Through Files, Instantly. Local Multimodal Search for Windows and Agents.



WinSight（智寻）是一款基于 .NET 11、WinUI 3 与多模态嵌入模型的 Windows 11 原生桌面检索中枢，在零云端依赖的纯本地环境下实现跨文档、代码与截图的毫秒级图文混搜，并通过标准化模型上下文协议（MCP）无缝挂载为本地 AI Agent 的全知外部记忆。
WinSight is a Windows 11-native desktop search hub powered by .NET 11, WinUI 3, and on-device multimodal embeddings—delivering millisecond-level semantic retrieval across documents, code, and images with 100% privacy, while exposing standard Model Context Protocol (MCP) endpoints for local AI agents.



WinSight 是面向 Windows 11 的本地多模态文件检索项目，用关键词与深度语义向量联合查找本机文档、代码、图片及媒体片段。项目以 .NET 11、WinUI 3 (Windows App SDK 2.5.1)、SQLite FTS5 和独立推理隔离进程实现，不依赖远程模型 API，所有索引与向量计算完全在本地完成。



> **当前状态（2026-11）：v0.9.0，运行在 .NET 11 预览运行时上。** 全链路多模态文档检索、PDF/PPT 逐页视觉渲染、RapidOCR 几何定位、旧版 Office 格式（.doc/.xls/.ppt）隔离抽取与只读来源跳转均已稳定合入主线。桌面主控制台采用 Windows 11 原生 Mica Alt 沉浸式标题栏，快捷搜索 HUD 浮窗采用 DesktopAcrylic 亚克力材质与 800×520 双栏即时预览；全界面已完成规范简体中文本地化。主线安装通过 Inno Setup 脚本 `desktop/installer/WinSight.iss` 与 `scripts/Build-NativeInstaller.ps1` 交付标准安装包，默认安装至 `C:\Program Files\WinSight`，带有官方高保真 Logo 图标与可选系统 PATH 自动注入。GitHub CI 自动化工作流已全量覆盖 10 大核心套件（270+ 个断言用例验证通过）。

> **预览运行时与已知限制。** 本版本构建于 .NET 11 RC（精确 SDK `11.0.100-rc.2`，`RollForward=Disable`）：在 .NET 11 正式版发布并完成升级验证之前，请按预览版使用；`docs/native-migration/status.json` 的 `releaseApproved` 仍为 `false`。
> - **规模**：检索是内存向量暴力扫描，实测约 2 万段 32 ms、10 万段 155 ms（含全部通道）；设置页会在接近 8 万向量段时提示。更大语料需要 ANN/分片。
> - **文档格式**：`.docx/.xlsx/.pptx/.odt/.rtf/.pdf/.txt/.csv/.html` 直接抽取正文；非 UTF-8 文本会按字节结构识别 GBK/GB18030。旧版 `.doc/.xls/.ppt`（及 `.dot/.xlt/.pot/.pps`）由隔离的推理宿主进程解析：只取正文、表格单元格与幻灯片/备注文字，加密文件会被跳过并说明原因。WPS 专有格式 `.wps/.et/.dps` 暂不抽取，仅可按文件名检索。
> - **MCP 桥**：只读且不加载嵌入模型，`mode=hybrid` 与 `keyword` 同为词法检索；`index_directory` 需要先在应用的 MCP 控制台创建维护授权。返回的文件内容一律是不可信数据。
> - **USN 追赶**：需要卷读取权限；无权限或日志不可用时回退到 FileSystemWatcher + 定期对账，实际状态见“存储与模型”页的 `USN` 字段。

\---

## 1\. 项目定位与核心特性

* **面向谁**：需要在海量本机资料、扫描件、多模态媒体和代码中快速查找内容，并精确回跳到来源片段的 Windows 用户与开发者。
* **解决什么问题**：文件名无法完全概括文档主旨，传统关键词匹配无法命中语义相近的表达或图片中的文字。WinSight 将正文提取、OCR 文字、位置单元与 768 维向量嵌入纳入统一的混合检索链路。
* **用户形态**：

  * **WinUI 3 桌面主控制台**：多标签页管理、沉浸式卡片化配置、存储与队列监控、OCR 框高亮来源预览。
  * **HUD 快捷搜索栏 (`QuickSearchWindow`)**：全局热键秒级呼出，800×520 双栏分治，光标悬浮即时呈现右侧全文高亮上下文。
  * **返回 JSON 的原生 CLI (`winsight-native.exe`)**：支持脚本化配置、全量/增量建库、单次检索与脱敏诊断。
  * **本地 Agent MCP 桥接器 (`winsight-mcp.exe`)**：随应用内置提供，支持标准 Stdio 管道与环回 HTTP，采用单向异步信箱（Mailbox）机制防止外部 AI Agent 越权写坏库。
* **纯正本地与隐私承诺**：默认不全盘遍历扫描；不向任何云端上传个人文件或检索词；不包含用户行为遥测。

\---

## 2\. 核心功能与技术边界

|能力|当前范围与实现机制|
|-|-|
|**文档与代码提取**|支持 DOCX 段落/表格、PPTX 幻灯片/正文/备注、XLSX 工作表/单元格上下文；另有纯文本、PDF 文本、HTML 与独立代码文件索引。扫描 PDF 逐页执行 WIC 渲染与 OCR 行框标定（上限 32 页/180 秒）；PPT 支持基于 Office 进程的只读来源定位。|
|**图片与多模态**|基于 Windows Imaging Component (WIC) 硬件加速解码与双三次采样；独立进程加载 RapidOCR (PaddleOCR ONNX) 执行文本检测与识别；提取 EXIF 白名单元数据，杜绝个人隐私泄露。|
|**音视频片段**|原生音频 16kHz PCM 采样（30 秒窗口、5 秒步进跳跃）与视频关键帧采样嵌入；定位为片段检索，不作为离线语音全量转录服务。|
|**混合检索与排序**|中文简繁归一化（FormKC）、中英混排、精确编号与字段过滤。双路 FTS：`fts`（porter + unicode61 处理拉丁词/路径）与 `fts_seg`（基于 Rust jieba-rs 的中文分词倒排与真实 BM25 词频）。融合层结合文件名、目录与语义通道按位置单元执行 RRF 融合打分。|
|**增量与后台索引**|SQLite 级持久任务队列（`index_tasks`），支持任务去重、原子替换与崩溃断点续跑。NTFS USN Journal 增量追赶离线变更；四类并发预算（交互查询/新文件/历史补建/复杂解析）当前只作状态估算展示；后台索引的实际闸门是时间段与“仅接电源”设置（默认开启），用户查询在推理批次之间优先于后台索引。|
|**命令行工具**|`configure`、`status`、`index`、`search`、`model-info`、`diagnostics`；支持绝对路径 `--data-dir` 隔离。|
|**平台支持**|Windows 11 x64（最低运行版本 Windows 11 21H2 Build 22000）；模型计算以 CPU (SIMD AVX2) 和 DirectML/NPU 为主。|

\---

## 3\. 核心架构与模块划分

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
│   │       ├── Storage/             # SQLite 连接池、WAL、FTS5、代际重建迁移
│   │       ├── Search/              # SearchService (SIMD 点积, 动态误报率显著性门槛)
│   │       ├── Indexing/            # USN 日志捕获、增量队列、任务状态机
│   │       └── Media/               # 进程通信、音视频采样、Office 定位策略
│   └── installer/                   # 主线安装脚本 (WinSight.iss) 与历史契约归档 (legacy/)
├── inference/
│   ├── WinSight.InferenceHost/      # 默认多模态嵌入 Worker (Windows ML ORT 1.31)
│   ├── WinSight.OcrHost/            # 隔离的 RapidOCR 识别 Worker
│   ├── WinSight.WinMlHost/          # 可选实验性 Windows ML 嵌入宿主
│   └── WinSight.FoundryHost/        # 可选硬件提供程序注册管理器 (Foundry Local)
└── native/
    ├── Cargo.toml                   # 统一 Rust Cargo Workspace
    ├── winsight-segmenter/          # jieba-rs 中文分词 C ABI 动态库
    └── winsight-tokenizer/          # HuggingFace Tokenizers C ABI 动态库
```

* **高聚合拆解**：`DesktopBackend` 拆分为生命周期、索引管线、混合检索、硬件探测与运维调度 5 个单一职责的分部文件，消解了单文件上帝类。
* **进程树殉爆保护**：所有推理与 OCR 子进程均绑定 Windows Job Object（`KILL_ON_JOB_CLOSE`），并在 `BelowNormal` 优先级下运行，前台 UI 永不卡顿，父进程崩溃时子进程驱动级自动销毁。
* **并发互斥安全**：引入 `IndexRunLock` 独占文件锁，在 App、CLI 和后台定时轮询任务之间建立跨进程互斥栅栏，防止 SQLite 数据库多写争用。

\---

## 4\. 技术栈总览

技术栈与依赖版本以 `desktop/Directory.Build.props`、各项目 `.csproj` 与 `packages.lock.json` 为准。

|层次|当前实现与版本选型|
|-|-|
|**运行时与 SDK**|**.NET 11 (C# latest)**；桌面端与推理宿主统一面向 `net11.0-windows10.0.26100.0`，最低支持平台统一为 Windows 11 RTM (`10.0.22000.0`)。|
|**桌面 UI**|**Windows App SDK 2.5.1 (WinUI 3)**；原生 Mica Alt 与 DesktopAcrylic 材质合成，手写轻量 MVVM（零反射开销）。|
|**向量嵌入模型**|**EmbeddingGemma-2 ONNX (q4 量化)**；768 维单向量空间多模态对齐（文本/图片/音频），内存常驻仅 \~175 MB。|
|**向量检索引擎**|内存连续 float32 配合 **SIMD 硬件加速 (`System.Numerics.Vector<float>`) 内联点积**，10 万向量点积耗时 \~15 ms。|
|**词法与倒排检索**|**Microsoft.Data.Sqlite 10.0.13** (WAL 模式) + **FTS5**；`fts` 负责拉丁词/精确标识符，`fts_seg` 负责中文分词与真实 BM25 词频计算。|
|**双原生分词库**|**Rust CDylib 原生库**：`winsight_segmenter.dll` (jieba-rs 0.11.0) + `winsight_tokenizer.dll` (HuggingFace tokenizers rev `3012770`)。|
|**多媒体与图像解码**|**Windows Imaging Component (WIC COM ABI)** 硬件加速解码与方向旋转，Windows.Data.Pdf 页面矢量渲染。|
|**OCR 文字识别**|**`RapidOcrNet.WinSight 4.2.0-winsight.1`**；隔离进程加载 PaddleOCR ONNX 权重，内存完全独立。|
|**Agent MCP 桥**|**`winsight-mcp.exe`**；自研轻量 JSON-RPC 2.0 协议引擎，支持 Stdio 与 Bearer 令牌鉴权的环回 HTTP。|
|**安装与发布系统**|**Inno Setup 6/7** 标准安装包；默认安装至 Program Files（亦支持 `/CURRENTUSER` 单用户模式），快捷方式直连 exe，支持 PATH 环境变量注入与用户数据保留确认。|

\---

## 5\. 业务流程与检索流水线

### 索引生成管线

1. **授权与作用域过滤**：用户在“目录管理”页或 CLI 明确添加扫描目录，应用数据目录与索引目录天然处于排除列表。
2. **增量比对与 USN 追赶**：优先尝试 NTFS USN Journal 增量追赶离线变更；无权限或日志溢出时平滑降级为目录树差集比对。
3. **分块与边界自适应**：

   * 采用**Token 估算预算切块**（英文约 4 字符/Token，中文约 1.5 字符/Token，单块上限 \~450 Token）；
   * 优先对齐段落、换行、句末标点（`。！？；`）与逗号，解决传统空白切块导致的中文跨词截断。
4. **多模态批处理嵌入**：

   * 文本段落添加 `title: {文件名} | text: {切块}` 提示词；
   * 图像生成 280-token 视觉嵌入，PDF 页面提取 OCR 文本行后再次生成多级切块；
   * 写入前校验有限值、非零与 768 维 L2 归一化规范。
5. **持久化与代际隔离**：文档写入 `index.sqlite`，代码写入 `code.sqlite`。向量以 FP16 BLOB 紧凑存储，更新操作采用原子事务。

### 混合检索流水线

1. **查询分析**：解析 `kind:`、`root:`、`project:`、`amount:` 等结构化过滤字段，分离拉丁词与中文词汇。
2. **双路全文匹配**：

   * 拉丁词走 `fts` 匹配，支持 Porter 词干还原与通配符；
   * 中文词由 `winsight_segmenter.dll` 分词后直接 MATCH `fts_seg`，获得真实 BM25 分值。
3. **向量语义召回**：

   * 查询生成向量后，通过 SIMD 并行计算候选片段的余弦相似度；
   * **动态误报率显著性门槛**：基于通道单元总量动态计算标准正态分位数（控制每个通道每次查询预期误报仅 ~1 条），彻底消除传统固定 $z \ge 2$ 在大语料下固定放行 2% 噪声的缺陷；样本少于 5 个时退回 $\text{cosine} \ge 0.5$ 小样本底线。
4. **RRF 倒数排名融合**：

   * 关键词通道（权重 2.0）、语义通道（权重 1.0）、文件名（权重 1.5）、目录路径（权重 0.7）按来源单元做 RRF 融合打分，取 Top-K 返回结果。

\---

## 6\. 部署、安装与运行指南

### 1\. 终端用户安装使用

* **现成安装包直接安装**：
可以直接运行仓库中已编译打包好的完整安装包：[**`releases/WinSight-0.9.0-x64-setup.exe`**](releases/WinSight-0.9.0-x64-setup.exe)（体积约 452 MiB，SHA-256 校验和：`1feabac767da5c26b5a332b8b4833b3c81ed39bf324b776bc218aad91d9f40d4`）。
* **标准安装路径**：双击运行安装向导，默认安装至 **`C:\Program Files\WinSight`**。
* **安装包特性与可选任务**：

  * [x] **全模态模型开箱即用**：完整内置打包 11 个 EmbeddingGemma-2 向量权重文件 + 4 个 RapidOCR 文字定位与识别模型，安装即可直接体验毫秒级图文混搜与 GPU/DirectML 硬件加速，无需任何额外模型下载配置；
  * [x] 注入官方高保真多分辨率图标 (`WinSight.ico`)；
  * [x] 创建桌面快捷方式（默认不勾选，保持桌面整洁）；
  * [x] 将 WinSight 命令行工具自动添加至系统 PATH（勾选后可直接在任何终端键入 `winsight-native` 或 `winsight-mcp`）；
  * [x] 智能占用防护：安装与覆盖升级前自动检测并提示优雅关闭 7 类运行中进程，杜绝文件锁死。
* **开始菜单与秒级冷启动**：快捷方式直接原生拉起 `WinSight.App.exe`，启动时间 < 300ms，无控制台黑框弹出。

* **卸载清洁度**：卸载程序自动清理注册表 Run 自启动项与 PATH 环境变量；卸载时交互弹窗友好询问是否保留个人已建立的索引数据库与设置（默认安全保留）。

### 2\. 开发者环境准备与构建

1. **环境依赖**：

   * 操作系统：Windows 11 x64 (Build 22000+)；
   * .NET SDK：11.0.100（参阅 `desktop/global.json`）；
   * Rust 工具链：stable-x86\_64-pc-windows-msvc（包含 cargo）；
   * Inno Setup 6：用于本地编译打包（可选）。
2. **编译原生动态库**：

```powershell
   cd native/winsight-segmenter
   cargo build --release --locked
   cd ../winsight-tokenizer
   cargo build --release --locked --target x86_64-pc-windows-msvc
   ```

3. **运行测试套件**：

```powershell
   # 运行全量桌面测试
   cd desktop
   dotnet test
   ```

4. **打包发布暂存包**：

```powershell
   # 准备完整发布目录
   ./scripts/Prepare-NativeRelease.ps1 -StageDirectory "D:\projects\WinSight\.dev\native-migration\stage-0.9.0"
   # 生成安装包
   ./scripts/Build-NativeInstaller.ps1 -StageDirectory "D:\projects\WinSight\.dev\native-migration\stage-0.9.0" -OutputDirectory "D:\projects\WinSight\releases"
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

# 5. 导出脱敏的系统诊断包（供故障排查）
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

\---

## 8\. 自动化测试与质量门禁

项目在 GitHub Actions (`.github/workflows/ci.yml`) 的每次推送与 PR 中，固定执行以下 10 大测试套件的自动化验证（全部测试均在无人工干预的隔离沙箱中跑通）：

1. **`WinSight.DesktopPolicy.Tests`**：验证热键语法解析、注册防冲突回滚、命令行参数解析（`--autostart`、`--data-dir`）。
2. **`WinSight.Inference.Tests`**：验证推理子进程生命周期、子进程 BelowNormal 优先级控制、崩溃优雅重连与取消结算。
3. **`WinSight.Runtime.Tests`**：验证滚动日志轮转、崩溃脱敏导出、硬件提供程序退避策略、开机自启纠偏、版本号比较规范、文件锁互斥、MCP 维护任务自动消费循环。
4. **`WinSight.Pause.Tests`**：验证索引暂停/恢复状态机、USN 日志补齐收敛、并发取消与句柄释放。
5. **`WinSight.Storage.Tests`**：验证 SQLite WAL 事务原子性、向量 FP16 编解码精度、FTS 双虚拟表一致性、中英文词法分词召回、SIMD 向量点积与显著性门槛。
6. **`WinSight.Indexing.Tests`**：验证 Token 估算预算切块、CJK 标点自适应重叠、Unicode 代理对安全与异常格式容错。
7. **`WinSight.IndexingSettlement.Tests`**：验证任务队列状态机、注入故障结算与持久化。
8. **`WinSight.DocumentSearch.Tests`**：验证 Office 结构化字段提取、Excel 单元格交集、Word 坐标定位。
9. **`WinSight.DocumentSearch.Tests --review-cases`**：验证深层复杂富媒体排版回归。
10. **`WinSight.Bridge.Tests`**：验证 MCP 客户端 Stdio 管道与命名管道接口契约合规性。

\---

## 9\. 许可证与开源归属

* 本仓库采用 [Apache-2.0 许可证](LICENSE)。
* 模型权重及第三方依赖遵循各自开源授权，分发时自动附带第三方声明文件 [THIRD-PARTY-NOTICES.txt](desktop/THIRD-PARTY-NOTICES.txt)。

