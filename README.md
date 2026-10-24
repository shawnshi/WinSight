# WinSight（智寻）

WinSight 是面向 Windows 11 的本地文件检索项目，用关键词和多模态语义查找文档、代码、图片及媒体片段。当前原生候选以 .NET 11 RC2、WinUI 3、SQLite 和独立推理进程实现，不依赖远程模型 API 完成检索。

> **当前状态（2026-10-24）：开发预览／原生迁移候选，不是稳定发行版。** 结构化检索、PDF 页面／OCR 几何、PPT 整页视觉三个源码阶段已获独立批准；有界非 GUI 只读 PPT／来源 API 源码及披露的 P2 边界已由维护者接受。SearchPage 页面预览、OCR 行框与键盘交互已完成当前 200% DPI、自有合成夹具的有界验收并纳入源码同步；这不代表完整产品、所有 DPI 或真实 Office GUI 全语料获验收。标准安装、升级／卸载／恢复和 Release 门禁未闭合。见[源码合入范围与验收边界](docs/native-migration/source-integration-status.md)。

## 1. 项目定位与范围

- **面向谁**：需要在本机资料和代码中查找内容、并回到来源文件或片段的 Windows 用户。
- **解决什么问题**：文件名不能充分表达内容，关键词无法覆盖图片文字或语义相近的表达；项目将提取内容、位置和向量纳入同一检索链路。
- **源码入口**：WinUI 桌面程序和返回 JSON 的原生 CLI，两者复用后端与索引合同；SearchPage 的有界 GUI 预览已交付；CLI 锁定构建仍有下述已知缺口。
- **不做什么**：不默认扫描整块磁盘，不自动发现个人目录或迁移旧索引；不提供云端同步、通用 OCR 准确率保证、通用敏感数据识别或恒定延迟承诺。
- **Agent 集成**：MCP 是目标方向，当前原生 CLI 不是 MCP 服务，InferenceHost 的内部通信也不是 MCP；不能把目标接口当作已交付能力。

仓库现只维护 `desktop/`、`inference/` 和独立 Rust tokenizer 的原生路线。旧 Swift/Tauri 程序、旧 CLI／JSONL 协议及专用构建发布入口已按产品取舍退役，不再承诺旧版兼容。这是源码路线收敛，不代表旧版 47 项命令已全部迁移，也不代表安装、完整行为对齐或发布验收通过。见[旧链退役合同](docs/native-migration/legacy-retirement.md)。

## 2. 当前能力与限制

四项组件的本地源码／集成升级已完成并通过独立复核：.NET 11 RC2、Host BuildTools 10.0.26100.9169、明确标识的 RapidOcrNet.WinSight 4.2.0-winsight.1 独立 worker，以及官方 v0.23.3 固定提交的 tokenizer（实际 Rust crate 0.23.2）。已有图片38项、动态 ABI／真实模型 profile 及启动保留分支验证；这不代表部署、完整产品验收或 Release 批准。见[组件升级状态](docs/native-migration/component-upgrade-status.md)。

| 能力 | 当前范围与边界 |
| --- | --- |
| 文本和代码 | 新本地候选支持 DOCX 段落／表格、PPTX 标题／正文／表格／备注、XLSX 工作表／表头／缓存单元格值和行列上下文；结果携带来源位置。另有文本、PDF 文字、HTML 和独立代码索引；第二阶段已接入扫描PDF逐页视觉/OCR文本行框（32页／180秒覆盖上限，未覆盖部分标partial），本地源码已获独立批准；PPT整页视觉已接入本机PowerPoint，本地源码获独立批准（仅手动索引、忙时延后）；只读来源定位已接入后端 API 与 SearchPage 显式入口；真实 Office GUI 全语料未验收 |
| 图片 | 原生解码、视觉嵌入、隔离 RapidOCR 源码候选和 EXIF 属性白名单；旧协议受控图片集成回归38/38保留；新文字行定位协议由第二阶段页面链路验证，不等同完整图片矩阵或广泛OCR准确率 |
| 音视频 | 已接入原生音频窗口／视频帧采样与嵌入链路；短音频、具体编解码格式及视频端到端覆盖受采样和验收范围限制，不是语音转录服务 |
| 搜索 | 中文简繁字符／全半角归一化、中英混排、精确编号和字段过滤；关键词、文件名、目录及来源语义通道按位置单元做 RRF 融合。显示排序分而非置信度；结构化检索与PDF页面视觉／OCR几何两个阶段的本地源码均已获独立批准；第三阶段PPT整页视觉的本地源码亦已获独立批准；SearchPage 页面预览与当前 DPI 下的有界键盘验收已完成 |
| 增量与后台索引 | 文件变更监听、补扫、暂停／恢复、时段和供电策略；后台索引默认关闭，不会因首次启动自动扫描用户目录 |
| CLI | `configure`、`status`、`index`、`search`、`model-info`；不是旧 CLI 的直接替代品，也未交付原生 MCP 服务入口 |
| 平台与设备 | 桌面工程当前为 Windows 11 x64；已取得的真实模型证据以 CPU 为主。不能将 Host 的其他 RID 或提供程序选项视为 ARM64、GPU、NPU 已验收 |

### 来源预览与精确定位

- 重新核对授权根、来源单元和索引时保存的 SHA-256；缺少指纹或源文件已变化时，仅返回索引文字，移除栅格图、OCR 框和精确跳转能力。不自动重建用户索引。
- 显式调用可只读定位 Word 正文段落、Excel 合法工作表／A1 矩形、PPT 指定幻灯片；Word 字段及 Excel 公式／定义名称／外链等按当前策略拒绝。PPT 要求空闲独占：已有 PowerPoint 时拒绝接管；成功定位后把自有窗口移交用户。
- SearchPage 显式预览按钮或结果列表 Space 打开面板；显示来源文字、整页图和可切换的 OCR 行框。Escape 关闭并恢复焦点，Down 从搜索框进入结果列表，Enter 保留普通打开授权检查；宽屏并排、窄屏覆盖，图片与框共用坐标系，过期完成不能替换当前预览。
- 当前 200% DPI、明暗主题与宽窄窗口的自有合成 GUI 验收通过：视觉 43/43、键盘 26/26、生命周期 6/6，13 个预览租约均释放；12 张控件截图经人工复核。这些计数不是全产品验收，定向键消息与线程内 Shift 状态恢复不是物理硬件或所有 DPI 验收。
- Office 不是 OS／网络／凭证沙箱，可能改动自身缓存或最近使用记录。此前有界 Office 后端验收与本次合成 GUI 验收分开陈述；普通“打开文件”行为不变，不据此承诺真实 Office GUI 全语料或完整进程隔离。

图片缺少可用 OCR、发生截断或只覆盖多帧首帧时保留 `partial` 和原因，不冒充完整成功；坏图、非法协议和越界输入明确拒绝。音频不足 2 秒时按当前采样策略跳过。详见[图片合同](docs/native-migration/image-exif-ocr-contract.md)及[媒体采样策略](desktop/src/WinSight.Infrastructure/Media/Sampling/NativeMediaSamplingPolicy.cs)。

## 3. 核心架构

App 和 CLI 通过 Core 合同调用 Infrastructure。后端负责设置、授权目录、提取、索引和搜索；需要模型或媒体原生能力时启动独立 InferenceHost，而不是在 UI 进程中加载模型。

| 组件 | 职责与代码入口 |
| --- | --- |
| 桌面 App | WinUI 窗口、托盘、全局热键、设置和索引控制；[WinSight.App](desktop/src/WinSight.App) |
| Core | 请求／响应、服务接口、设置和 768 维向量合同；[Contracts.cs](desktop/src/WinSight.Core/Contracts.cs) |
| Infrastructure | 文件过滤、提取、索引、混合检索、SQLite、配置存储及受控 Host 客户端；[WinSight.Infrastructure](desktop/src/WinSight.Infrastructure) |
| 原生 CLI | 脚本化配置与检索，使用同一后端；[Program.cs](desktop/src/WinSight.Cli/Program.cs) |
| InferenceHost | Windows ML 模型推理、原生 tokenizer，以及独立的图片／媒体操作；[Host 工程](inference/WinSight.InferenceHost) |
| 原生 tokenizer | Rust 编译的本地 DLL，必须匹配目标进程架构；[winsight-tokenizer](native/winsight-tokenizer) |
| 安装与发布 | payload 清单、源文件哈希绑定、每用户安装和事务恢复；[安装源码](desktop/installer)与[发布脚本](scripts/Prepare-NativeRelease.ps1) |

Host 客户端和媒体任务管理负责超时、取消、进程树所有权与退出结算。索引暂停不是简单停止 UI 更新：当前工作及其原生子进程需要完成取消和结算。

SQLite 保存文件状态、片段、全文索引与向量；文档和代码分别使用 `index.sqlite`、`code.sqlite`。只写入带原生标记的新 schema5 索引，不自动接管或改写旧索引。

## 4. 当前技术架构

| 层次 | 当前实现 |
| --- | --- |
| 运行时与 UI | .NET 11 RC2 / C#、WinUI 3 / Windows App SDK；桌面工程声明的最低平台为 Windows 11 build 22000，编译目标为 Windows SDK 26100；这不是所有 Windows 11 版本均已安装验收的承诺 |
| 文档提取 | PdfPig、Open XML，以及受大小、字符数和格式限制的文本／代码提取 |
| 图片与媒体 | Windows 图像／媒体 API + 独立 RapidOCR 候选；固定中英模型，无运行时下载或 Windows OCR 回退 |
| 词法检索 | Microsoft.Data.Sqlite / SQLite FTS5；当前 tokenizer 为 `porter unicode61 remove_diacritics 2`，保留既有 schema 合同，并补充 CJK 候选处理 |
| 向量检索 | 768 维、归一化向量，以 FP16 存储；当前语义评分仍为托管向量缓存加线性余弦计算，不是已交付的 sqlite-vec、HNSW 或 ANN 索引 |
| 模型 | Windows ML；`onnx-community/embeddinggemma-2-ONNX` 的清单固定版本，包含文本、视觉和音频 ONNX 工件及 tokenizer 配置 |
| 分词 | 原生 Rust tokenizer DLL；不是以 C# `Microsoft.ML.Tokenizers` 替代当前实现 |
| 安装 | Inno Setup 标准每用户安装候选，配合受控启动器、源哈希绑定和恢复事务；当前现场安装验收尚未通过 |

精确版本以工程、锁文件及清单为准：[App](desktop/src/WinSight.App/WinSight.App.csproj)、[Infrastructure](desktop/src/WinSight.Infrastructure/WinSight.Infrastructure.csproj)、[Host](inference/WinSight.InferenceHost/WinSight.InferenceHost.csproj)、[模型清单](inference/WinSight.InferenceHost/model-manifest.json)。

暖查询可以复用缓存，但冷语义查询仍需读取向量并线性评分，CJK 候选也有词表扫描成本。响应时间受数据规模、缓存、磁盘和设备影响，本文不承诺“零延迟”或所有规模下的毫秒级检索。

## 5. 业务流程与数据边界

### 索引

1. 用户通过设置页或 CLI 明确选择文档／代码根目录，配置排除目录和类型；应用数据与索引目录不作为内容根扫描。
2. 后端核对授权、路径和文件策略，增量判断新增、变更及需要刷新提取版本的文件。
3. 按类型提取文本、图片 OCR／白名单元数据，或采样音频窗口／视频帧；记录可回查的片段位置。
4. 将内容切分成片段，为支持的模态生成向量，并校验维度、有限值与归一化要求。
5. 写入对应 SQLite 索引，保留 `pending`、`done`、`partial`、`failed` 等实际状态和原因；不把异常伪装成“无数据”。

手动索引由用户显式发起；App 后台索引还受启用状态、暂停、时段与供电策略约束。`processorOnly=true` 当前表示**自动索引仅限交流电**，不是仅使用 CPU，也不限制用户发起的手动索引。

### 检索

1. 接收查询、模式、类型、授权根和结果数量限制。
2. 关键词分支检索 FTS/CJK 候选；语义分支为查询生成向量，再对片段评分。
3. 在授权范围内分别召回后，以页面、幻灯片、表格行、段落或文件为来源单元做 RRF 融合；不平均正文、OCR 和视觉向量。CLI 默认关键词模式，可显式选择 `hybrid` 或 `semantic`。
4. 混合模式在模型不可用时可明确提示并退回关键词；纯语义模式的失败不能当作语义检索成功。小样本语义回退会给出提示，当前相关性阈值仍需进一步校准。

### 本地与隐私

文件提取和已配置模型的推理在本机执行，不要求远程模型 API。开发依赖还原、手动取得模型／工具和后续 Agent 对接属于另外的联网边界，不能因此承诺“所有环节永不联网”。

索引可能包含文件路径、正文摘要、OCR 和元数据，不应视为已加密或已脱敏。EXIF 属性白名单不查询 GPS、序列号和人物标签，但自由文本／OCR 仍可能含私人信息。私钥标记过滤也不是通用秘密检测。

## 6. 部署、安装与首次运行

### 终端用户

**当前没有在本 README 中承诺已通过完整安装验收的稳定安装包。** 不要将 `.dev/` 中的编译产物、隔离运行目录或旧任务 binding 当作可直接分发的安装版。

公开源码中的标准安装／恢复入口仍是**默认拒绝执行的未就绪模板**：本机私有目录、解释器和绑定不能公开复用。要启用，必须在独立任务中固化私有图、重新计算完整源／清单／绑定哈希，并通过对应验收；不能通过环境变量或调用方 JSON 选择代码，也不能把去掉拒绝语句当作验收。已有本地失败现场、模型及恢复记录没有随源码提交。

当前标准安装设计采用独立的 `WinSight.Native.Preview` 身份，提供当前用户的开始菜单入口和卸载入口；默认不启用自启，不修改全局 PATH，也不自动迁移旧版资料。完整包需要同时包含 App、CLI、Host、匹配架构的 tokenizer、运行时依赖、模型清单、权重和许可材料，不能只复制一个 EXE。

安装脚本与 payload、源文件哈希、目标目录及所有权绑定必须一致，不能绕过启动器或复用旧绑定。当前标准安装入口及其绑定还校验精确的 x64 Windows PowerShell Desktop 宿主版本（当前为 `5.1.29683.1000`），不能直接用 PowerShell 7 替代该入口。安装成功后仍需核验真实入口、模型／OCR、隔离数据检索，以及升级、卸载和失败恢复；编译通过或进程存在不代表这组验收完成。

参考：[标准每用户安装合同](docs/native-migration/standard-per-user-installer-contract.md)、[发布验收合同](docs/native-migration/release-acceptance-contract.md)。

### 开发者准备与构建

- Windows 11 x64、PowerShell 7、匹配的 MSVC C++ Build Tools / Windows SDK、Rust，以及 .NET SDK；PowerShell 7 用于开发构建脚本，不替代上述安装宿主。
- Desktop 和 Host 当前精确固定 SDK `11.0.100-rc.2.26504.105`，对应 Runtime 为 `11.0.0-rc.2.26504.105`；均禁用版本滚动，见 [desktop/global.json](desktop/global.json) 与 [inference/global.json](inference/global.json)。UI 工程自身仍限制为 x64。
- 先准备符合[模型清单](inference/WinSight.InferenceHost/model-manifest.json)的模型目录；项目不自动扫描私人模型缓存或下载缺失权重。
- Host 构建要求先用 `scripts/build-native-tokenizer.ps1` 取得匹配 Rust 目标的 tokenizer DLL 及源码／DLL 绑定收据，并显式提供 `-OcrModelRoot` 指向四个已校验 OCR 资产；不自动下载。已有入口为 [build-inference-host.ps1](scripts/build-inference-host.ps1)，完整暂存入口为 [Prepare-NativeRelease.ps1](scripts/Prepare-NativeRelease.ps1)。它们不是“已验收的一键安装命令”；首次依赖还原可能联网。
- `-AppOnlyPreview` 只生成不含完整模型／推理能力的预览暂存，不能作为完整功能或发布成功的证据。完整暂存要求显式模型目录，且拒绝覆盖已有阶段目录。

**已知构建缺口：CLI 的原有 RID 声明与包锁图尚未对齐。** CLI 锁文件包含 `win-x64`，工程未声明对应 `RuntimeIdentifiers`；直接对整个引用图强制 RID 又会影响无 RID 的 Core／Infrastructure 锁图。该问题在本轮 SDK 升级前已存在；本轮 CLI 仅静态调整 TFM／锁目标键，没有再次 restore／build，也不宣称 CLI 的锁定模式离线构建通过。后续应在独立修复中统一这组契约并重新验证；不要用去掉锁定检查来掩盖它。

开发 CLI 的帮助入口示意（先完成上述构建契约修复；在仓库根目录执行，首次运行可能还原依赖）：

```powershell
Push-Location desktop
try {
    dotnet run --project src/WinSight.Cli -- help
} finally {
    Pop-Location
}
```

### 已准备完整 payload 时的 CLI 示例

下面使用独立示例目录；需要先准备真实存在的文件、权重和完整 payload，再替换路径。命令会保存该示例的数据配置并索引指定目录，**不是安装命令**，不应直接套用到未授权的个人资料。

```powershell
$app = 'C:\WinSightExample\app'
$cli = Join-Path $app 'winsight-native.exe'
$data = 'C:\WinSightExample\Data'
$files = 'C:\WinSightExample\Files'

& $cli configure --data-dir $data --root $files `
    --host "$app\inference\WinSight.InferenceHost.exe" `
    --models "$app\models" --manifest "$app\inference\model-manifest.json"
& $cli status --data-dir $data
& $cli model-info --data-dir $data
& $cli index --data-dir $data --root $files
& $cli search --data-dir $data --query '项目方案' --mode hybrid --limit 20
```

所有业务命令都要求绝对 `--data-dir`；原生 CLI 不猜测旧设置，也不启动 App 的后台协调器。`configure` 不等于索引完成。若只需要对已有索引做关键词搜索，可显式使用 `--mode keyword`，不会为这次查询启动模型。

## 7. 主要配置

设置保存为 `<data-dir>\settings.json`，JSON 字段使用 camelCase。开发 App 未指定数据目录时默认使用 `%LOCALAPPDATA%\WinSight\NativePreview`；CLI 必须显式传入 `--data-dir`。安装启动器可以指定其他受控数据根，不能把开发默认目录当作所有安装的固定路径。

默认索引目录为 `<data-dir>\index`。数据目录应保持较短：SQLite 初始化还会追加私有暂存库及 sidecar 文件名，Windows 下过长路径可能报 `SQLITE_CANTOPEN`；当前不承诺任意长路径均可用。未显式配置 Host／模型路径时，后端相对应用目录查找 `inference\WinSight.InferenceHost.exe`、`inference\model-manifest.json` 和 `models\`；不同安装布局应以实际启动器与显式配置为准。

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `folders` / `codeFolders` | 空列表 | 分别授权文档和代码根；路径必须为绝对路径 |
| `skipped` / `codeSkipped` / `skipTypes` | 空列表 | 对应根下的排除目录及跳过类型 |
| `setUp` | `false` | 是否完成初始配置；CLI `configure` 会设为 `true` |
| `backgroundIndexingEnabled` | `false` | App 是否启用后台索引 |
| `indexHoursOn` | `false` | 是否限制自动索引时段 |
| `indexHours.start` / `indexHours.end` | `60` / `420` | 本地午夜起的分钟数，默认 01:00–07:00；关闭时段限制时不使用 |
| `processorOnly` | `true` | 自动索引仅在交流电条件运行；不等于 CPU 推理开关 |
| `includeWordComments` / `includeWordHeadersFooters` | `false` | 显式选择 DOCX 批注及页眉页脚；当前通过配置生效，不宣称已有新 UI 开关 |
| `hotkey` | `Control+Shift+Space` | 桌面搜索入口热键；冲突或注册失败不代表新绑定已生效 |
| `indexDirectory` | `null` | 覆盖默认索引目录，需绝对路径 |
| `modelDirectory` | `null` | 模型工件目录，需符合模型清单 |
| `inferenceExecutable` / `manifestPath` | `null` | 显式指定 Host EXE 和模型清单的绝对路径 |
| `theme` | `Default` | `Default`、`Light` 或 `Dark` |

优先使用设置页或 CLI 支持的参数修改配置；CLI 并未暴露表中全部字段。关闭 App/CLI 写入者后才考虑手工编辑 JSON，保留合法的 `version`／`revision`；错误版本、重复字段、非法路径和并发保存冲突会被拒绝。设置保存采用修订号与原子替换，不应把这些字段当作可随意清零的计数器。

字段和校验的事实来源：[AppSettings](desktop/src/WinSight.Core/Contracts.cs)、[JsonSettingsStore](desktop/src/WinSight.Infrastructure/Configuration/JsonSettingsStore.cs)、[DesktopBackend](desktop/src/WinSight.Infrastructure/Runtime/DesktopBackend.cs)。

## 8. 开发验证与文档导航

本 README 的命令是操作示例，不代表在读者环境执行成功，也不授予安装、迁移或扫描真实数据的权限。对新版本应分别保留构建／测试、真实模型集成和现场安装验收证据，不能互相替代。

- [源码合入范围与验收边界](docs/native-migration/source-integration-status.md)：已批准阶段、非 GUI 子集、保留问题及未发布材料。
- [原生桌面说明](desktop/README.md)：组件、局部命令、兼容与验证边界；带日期的历史产物不自动代表最新状态。
- [架构与开发指南](winsight_architecture_and_development_guide.md)：历史设计材料，不是当前原生实现或全部功能已交付的证明。
- [旧链退役合同](docs/native-migration/legacy-retirement.md)：源码删除范围、共享素材、旧 CLI 兼容终止及尚未关闭的验收缺口。
- [迁移计划](docs/native-migration/plan.md)与[命令清单](docs/native-migration/command-inventory.json)：历史对照与验收范围，不因旧源码删除而变成全量迁移通过。
- [原生桌面测试入口](scripts/Test-NativeDesktop.ps1)与[推理进程测试工程](desktop/tests/WinSight.Inference.Tests)：按脚本参数使用任务自有目录，不默认连接或修改真实数据。
- [图片 EXIF/OCR 合同](docs/native-migration/image-exif-ocr-contract.md)：字段白名单、限制、状态和证据范围。
- [标准安装合同](docs/native-migration/standard-per-user-installer-contract.md)与[发布验收合同](docs/native-migration/release-acceptance-contract.md)：部署与发布的独立门禁。

`techstack.md` 等选型资料中的推荐项可能与实现不同；判断当前行为时，以源码、工程／锁文件、模型清单及对应版本的执行证据为准。

原生候选的打包／诊断 CI 仅保留手动触发，普通 push 不自动上传原生候选或诊断产物；旧签名分发工作流不随此源码快照发布。源码检查工作流固定验证依赖 `defusedxml==0.7.1`，执行原生布局／资源／清单检查和 `native/winsight-tokenizer` 单元测试，不再编译旧 Tauri 壳；这不是全部 .NET 构建或真实模型测试。CI 运行不等于安装、模型分发或稳定版发布批准。

## 9. 许可证与上游归属

本仓库保留目标仓库原有的 [Apache-2.0 许可证](LICENSE)。WinSight 包含来自 [DigUp](https://github.com/ARahim3/DigUp) 的 MIT 许可代码，原版权与许可全文保留在 [DigUp-MIT.txt](desktop/licenses/DigUp-MIT.txt)，没有用根许可证替换或删除上游许可。

[NOTICE](NOTICE)说明来源和修改范围；[历史依赖归属](licenses/DigUp-Acknowledgements.txt)按原文保留，适用范围与当前原生依赖不同。模型和第三方组件各有许可，源码公开不等于其二进制／权重可以直接分发。
