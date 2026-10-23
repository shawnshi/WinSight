# WinSight（智寻）架构与开发指南

> Windows 11 本地多模态文件检索与 MCP 服务。技术基线：C# / .NET 10.0、WinUI 3、EmbeddingGemma 2 ONNX、固定 768 维、SQLite / sqlite-vec / FTS5；完整文本与视觉模型随安装包离线交付。

- 文档版本：2.0.1；修订日期：2026-10-14。
- 状态：经设计修订的开发基线，不是已经实现或通过运行验收的产品。
- 本轮确认：使用 .NET 10.0；所有入库与查询向量固定为 768 维；模型打包在安装包中。
- 本轮修正：模型与分词契约、索引原子更新、MCP 权限、迁移回退、检索分数和交付门禁。
- 阅读方式：本指南中的“必须”“不得”是目标实现与验收要求；“待验证”意味着发布门尚未通过，不能用设计描述冒充实测。

## 一、产品目标与边界

### 1. 产品目标

WinSight 面向两类使用者：人通过快捷搜索与主窗口检索文档、代码和图片；授权 Agent 通过 MCP 检索并读取有限上下文。

必须保留的能力：

1. WinUI 3 主窗口、系统托盘、可配置全局快捷键与边输边搜浮窗。
2. 多目录监控、子目录开关、图片/代码索引开关、暂停与手动增量扫描。
3. PDF、DOCX、Markdown、纯文本、源代码、图片的本地解析；图片视觉语义与 OCR 双路检索。
4. 文本语义、中文词法、文件名与标识符精确检索；定位到可解释的页、段落或行范围。
5. 本地 Streamable HTTP 与 stdio 桥接；由桌面端统一控制外部访问。
6. SQLite 存储位置选择、受控迁移、空库创建、索引重建与压缩。
7. 无需预装 .NET 或开发工具的 Windows x64 安装包，内含文本模型、视觉模型、tokenizer、processor、词典、原生库与许可证。

音频/视频模型及其索引不在首版范围；不因为上游模型支持就随包引入这些工件。PPTX、XLSX、复杂压缩包与附件解析另行扩展，不把 OpenXML SDK 的能力等同于首版已支持格式。

### 2. 本地与隐私边界

- 解析、OCR、分词、embedding、索引与检索在本机执行；正常使用不要求访问模型下载站、云推理或在线词典。
- 首次离线安装后不自动下载缺失模型。工件缺失、损坏或不兼容时显示具体错误，语义功能不可用；仍可显式使用已验证的词法检索，不伪装为完整服务。
- MCP 默认关闭。启用后，获准客户端可读取限定内容；若客户端使用云端模型，返回内容可能被它外传。WinSight 不承诺控制客户端的数据处理。
- 索引包含正文、OCR、路径等敏感材料。数据库、WAL、备份、迁移副本和日志属于同一隐私边界，使用当前用户受限 ACL；其他本地用户不可默认访问。
- 诊断不默认记录完整查询、正文、完整路径或模型输入。审计默认只保存客户端标识、工具、时间、耗时、返回数量、状态码；详细记录必须由用户开启且可清理。

### 3. 非目标

首版不提供云同步、局域网共享、跨用户索引、自动执行命中文件、代理替用户修改文档或自动重建全部索引。搜索结果中的文件内容是不可信数据，不能被当作 Agent 指令或产品配置。

## 二、固定技术基线与依赖管理

| 项目 | 确认基线 | 约束 |
|---|---|---|
| 运行时 | .NET 10.0 LTS，C# | 不保留 .NET 8 生产运行路径；锁定 SDK 与 NuGet 依赖 |
| 桌面 | WinUI 3 / Windows App SDK | Windows 11 x64；以目标发行版实际支持矩阵选定 SDK 包版本 |
| 模型 | EmbeddingGemma 2 ONNX，文本 + 视觉 | 锁定源 revision；不装音频模型；不推测 ONNX 输入输出 |
| 向量 | 768 维 Float32，L2 归一化 | 查询、文本、代码、视觉与 OCR 辅助向量一致；不提供维度切换 |
| 默认量化 | q4 文本与视觉工件 | 先通过 CPU / DirectML 对齐门；其他量化不是即时设置项 |
| 存储 | Microsoft.Data.Sqlite + sqlite-vec + FTS5 | 固定扩展版本与架构；FTS5 / trigram / vec0 启动自检 |
| 中文词法 | Jieba 系搜索分词 + FTS5 unicode61 | 优先验证 jieba-rs 原生适配；rjieba 仅为候选/参考，不默认为 .NET 库 |
| 字符串召回 | 独立 trigram FTS | 补充路径片段、标识符和三字符以上子串，不替代中文分词 |
| 解析 | PdfPig、OpenXML、Windows.Media.Ocr | 格式与 OCR 语言能力逐项验收，依赖包名不等于完整格式支持 |
| MCP | 官方 C# SDK / ASP.NET Core transport | 锁定兼容版本，核查默认安全与生命周期行为 |
| 分发 | Unpackaged WinUI 3 + Inno Setup | 主程序和桥接器均 self-contained；首版只承诺经过验证的 .exe 安装路径 |

本表不是已完成的 package manifest。实现阶段建立 `global.json`、集中包版本与 lockfile，执行来源、完整性、漏洞、许可证及安装脚本检查。旧文档中的版本号不因曾经出现在表格里而自动成为批准的长期依赖。

`rjieba` 与 `jieba-rs` 已核验为 MIT；仍须随包保留许可证，并核验词典、自定义词库和全部传递依赖的分发权。ImageSharp 等组件按真实许可核查，不统一写成“全部开源可自由商用”。

## 三、单宿主与轻量桥接架构

### 1. 进程职责

```text
WinUI 主窗口 / 快捷搜索 / 托盘
              │
       SearchCoordinator
              │
  推理调度器 ──┼── 存储与索引协调器
              │
  HTTP MCP / 当前用户 Named Pipe
              │
      winsight-mcp.exe（stdio）
              │
         已授权 MCP 客户端
```

- `WinSight.App.exe` 是唯一核心宿主，持有 UI、后台任务、推理 session、索引写入器、HTTP 与管道服务。
- 每个 stdio 客户端可启动自己的 `winsight-mcp.exe`，桥接器不加载模型、不直接打开数据库、不独立索引。
- 这不是 UI 与推理的双进程故障隔离。主进程停止时 UI、搜索、索引与 MCP 一起受影响；不以“主从双进程”承诺毫秒级响应或崩溃隔离。
- 首版只允许当前用户单实例核心宿主。多客户端由统一请求队列和预算调度，不建立各自模型副本。

### 2. .NET 工程边界

| 工程/目录 | 职责 | 边界 |
|---|---|---|
| `WinSight.Core` | 领域模型、搜索/索引/权限接口、错误码与定位契约 | `net10.0`；不引用 WinUI、ONNX、SQLite 或 MCP SDK |
| `WinSight.Infrastructure` | ONNX、原生中文分词适配、解析/OCR、SQLite、文件系统 | Windows 目标；实现 Core 接口 |
| `WinSight.App` | WinUI、DI/host 生命周期、用户授权、MCP 适配与状态展示 | 所有产品入口使用同一权限和搜索服务 |
| `WinSight.McpBridge` | stdio MCP 与命名管道客户端 | `net10.0`；只读/维护权限随当前用户会话获取 |
| `native/winsight-tokenizer` | 若选择 jieba-rs，提供受控 C ABI 的原生适配 | 仅填补 .NET 集成缺口，不把 Python 扩展当现成 DLL |
| `tests` / `installer` / `models` | 契约测试、安装脚本、固定工件清单 | 不把开发机环境依赖带给终端用户 |

MCP attributes 和工具适配位于 App/Bridge 的边界层，不向 Core 引入 transport 依赖。首版不另建通用插件框架、分布式队列或独立数据库进程。

### 3. 生命周期与调度

- 启动顺序：配置与 ACL → 工件校验 → 数据库能力/版本校验 → 模型按需加载 → 索引协调器 → UI 就绪。MCP 只有在用户启用且相关服务通过自检后启动。
- UI 搜索、MCP 读取、索引任务共享资源，但优先级明确：交互查询高于后台索引；维护/迁移由协调器取得独占维护租约。
- 使用有界队列和背压；后台索引不得无限缓存图片、全文或待生成向量。
- 所有操作携带 cancellation、请求 ID、资源预算和确定的错误语义；不把超时/权限失败改写为空结果。
- 退出顺序：拒绝新请求 → 取消在途任务 → 停止 transport → 排空/持久化任务状态 → 关闭读写连接 → 释放模型 → 退出。

## 四、模型工件、推理与 768 维合同

### 1. 模型工件清单

安装包必须包含经验证的一组一致工件；`model-manifest.json` 必须记录：

- 模型源与不可变 revision，ONNX 导出来源与转换说明。
- 文本/视觉 `.onnx`、对应 `.onnx_data`、tokenizer、processor/config，以及运行所需的全部辅助文件。
- 每个文件的相对路径、字节数、SHA-256、许可证与分发说明；不得保留未填的占位哈希进入发布包。
- 每个 ONNX 图的输入/输出名称、dtype、shape/动态轴、opset 与支持 EP 的验收结果。
- 文本格式、特殊 token、BOS/EOS、padding、截断、池化/投影与最终 `sentence_embedding` 语义。
- 视觉 resize/patch/soft-token 预算、像素缩放、mask 与编码器到最终共享向量的连接关系。
- 量化标识、输出维度 `768`、L2 规则、模型/处理器 fingerprint。

仓库当前 q4 工件名包括 `onnx/model_q4.onnx` 和 `onnx/vision_encoder_q4.onnx` 及各自外部数据文件；完整依赖仍以固定 revision 的导出清单为准。q8 文件名不能按 `model_q8.onnx` 猜测。装包不能只复制两个 `.onnx`。

启动校验检查路径不越界、文件存在、大小/哈希、外部权重引用、许可证清单与图 metadata。只允许从已批准的应用目录加载原生库和模型，不从当前工作目录探测同名 DLL。

### 2. 文本 tokenizer 与 embedding 前缀

Hugging Face `tokenizer.json` 不直接交给 `LlamaTokenizer.Create`。该 API 所要求的 SentencePiece BPE 模型流与 JSON 文件不是同一契约。先根据实际 tokenizer 算法和特殊 token 选择受支持的 .NET 实现；若需要原生适配，必须有明确 ABI、许可和离线打包策略。

在首次实现前，用官方 processor/tokenizer 建立固定文本 fixture，逐项对齐 token ID、mask、截断与特殊 token。fixture 包含中文、英文、中英混合、代码、换行、emoji、空字符串和长文本；不得通过“可以运行”替代数值对齐。

| 用途 | 文本格式 |
|---|---|
| 文档/通用检索查询 | `task: search result | query: {query}` |
| 代码查询 | `task: code retrieval | query: {query}` |
| 文档、代码、OCR 语料 | `title: {title or none} | text: {content}` |

模型 tokenizer 与中文词法 tokenizer 是两个独立概念。中文分词不改写 embedding 输入；embedding 使用原始文本及模型规定的前缀。

### 3. 视觉处理与最终输出

官方 processor 当前配置有 `do_normalize=false`、`rescale_factor=1/255`、`patch_size=16`、`max_soft_tokens=280`。不能沿用未经验证的固定 `896×896` 拉伸与 `[-1,1]` 归一化。

必须按固定 revision 的 processor 复现实际输入：颜色空间、方向、缩放、patch/soft-token、mask 和文本占位符等由对应模型契约决定，不凭文件名推断。图输入究竟是 pixels、patches 或其他特征，以 `InputMetadata` 和导出说明为准。

图片经过完整多模态链路输出最终共享空间 embedding。视觉编码器的中间输出不得直接展平后截断为 768 维。最终输出按已验证名称读取，禁止 `results.First()` 与“看 rank 决定任意 mean pooling”的猜测方式。

### 4. 固定维度与有效性

- 所有查询/语料最终输出必须恰为 768 维，元素有限且范数有效；不以 `Math.Min` 容忍维度错误。
- 按确定的模型契约完成 pooling/投影后进行 L2 归一化；零范数、NaN、Infinity 和维度不符为显式错误，不入库。
- 存储与查询都用 Float32，禁止不兼容空间混用。
- 上游 MRL 能力不转化为产品维度开关；设置页仅显示“768 维（固定）”。
- 模型 revision、量化、tokenizer、processor、pooling 或分块变化触发兼容性检查；未经验证的变化生成新索引代次，不在旧向量上继续写入。

### 5. EP 与 session 生命周期

DirectML 是 GPU 加速候选，不承诺通用 NPU。其 session 设置必须满足当前 ONNX Runtime 限制，包括顺序执行和关闭 memory pattern。受不支持并发 `Run` 的 session 由一个调度器串行执行；索引与查询不能绕过它。

CPU 是必要基线，DirectML 只有在工件/算子/输出对齐与目标设备测试通过后启用。回退覆盖 session 创建、首次执行和已分类的运行失败，保留原异常；不采用宽泛 catch 隐藏错误。CPU 若也失败，报告模型不可用。

`ConfiguredProvider`、`SessionProvider` 和经 profile 观察到的节点分配分开展示；未测不写“全部运行在 GPU”。卸载 timer 由调度器管理，只有无执行、无引用租约时释放视觉资源；运行超过空闲周期不能导致漏卸载或执行中释放。60 秒仅为可验证默认策略，不是显存释放保证。

模型文件体积不是进程内存。安装包大小、文本常驻、视觉加载、队列峰值、CPU/GPU 内存分别实测；不再承诺“内存 284 MB”。

## 五、文件解析、定位与增量索引

### 1. 授权路径与过滤

用户选择目录意味着授权产品在明确选项内读取，而非授权 MCP 读取其中所有文件。统一 `FileAccessPolicy` 用于扫描、搜索、预览、打开和 MCP：

- Windows 规范化路径与目录组件边界校验，不能使用字符串 `StartsWith` 或 SQL `LIKE prefix%` 作权限判定。
- 首版拒绝网络路径；默认不跟随 reparse point。允许的例外需要解析最终目标、卷/文件身份并重新授权。
- 对读取句柄对应目标重新验证，处理检查后替换/链接跳转；不能只验证输入字符串。
- 排除 `.git`、`node_modules`、`bin`、`obj`、`.vs`、不支持格式、应用自身模型/数据库/WAL/临时索引/日志；密钥与凭证类文件包括 `.env`、`.pem`、`.key` 及明确配置的变体。
- 排除规则在直接上下文读取同样生效，防止“未索引但能读”绕过。
- 子目录关闭、目录撤销和根目录重叠有确定规则：采用最具体获准根；有效选项来自该根。撤销立即阻止结果与读取，之后事务清理旧索引。
- 上下文读取必须指向当前仍有授权的命中文件，不接受任意绝对路径作为唯一凭据。

### 2. 格式与稳定定位

| 格式 | 提取与分块 | 定位/失败要求 |
|---|---|---|
| PDF | 按页提取，再按模型 token 预算切片；长文档优先前 30 页，余页必须进入可恢复的低优先级队列后台补全 | 1 起始页码和页内片段；记录总页数、已覆盖页、待补页和补全/终止原因。无文本页进入可选渲染+OCR能力门，不表示“无内容” |
| DOCX | 段落/表格文本，按预算切片 | 段落/表格标识与字符范围；不伪造固定页码或源行号。预览使用提取视图，原文保持不变 |
| TXT/MD | 按检测编码严格解码，记录换行映射 | 1 起始行范围；解码失败显式报告，不静默丢字符 |
| 代码 | 支持语言按经验证的语法边界；否则明确回退到行/窗口切片 | 行范围、语言、解析模式；不承诺所有扩展都具备函数级语义切片 |
| 图片 | 视觉向量 + OCR 原文；足够 OCR 文本可附加文本向量 | 像素尺寸、OCR语言/状态、图像定位。两路结果按文件聚合 |

文本默认目标 512 模型 token、约 80 token 重叠，保留实际切片版本。标题/前缀/特殊 token 计入模型最大输入长度，不无条件按字符数截断。

OCR 使用 Windows.Media.Ocr。引擎缺失、语言包不可用、图像过大、损坏和“识别成功但无文字”分别报告；不是所有机器都支持中英混合。检查 OCR 最大尺寸，对超限图片受控缩放或分片。OCR 关闭/缺失时视觉检索可独立存在，状态必须明确。

### 3. 任务状态与最终收敛

Watcher 负责及时发现变化，定期/启动扫描负责最终收敛。使用有界 Channel 与约 1,500 ms 防抖，不把事件流当可靠日志。

长 PDF 的前 30 页提交与余页作业入队必须在同一持久化事务完成。第 31 页起按有界页批写入低优先级任务，保存文件内容版本、页范围、已提交 checkpoint 和待补范围；主进程重启后自动恢复，重复页批幂等，不能依赖临时 Channel 保存唯一任务。每批补全保留该内容版本已提交的切片，再通过文件级事务发布合并后的切片、FTS、向量与覆盖。文件变更时旧版本页批作废，重新规划全部覆盖，禁止跨版本合并。

在用户未暂停、权限仍有效、未触及硬资源限制且依赖可用时，调度器必须通过保留配额或等价公平策略最终完成所有可处理余页，不能让交互请求使后台补全永久饥饿。只有预定页范围全部处理并经索引对账后才标记 Ready。暂时让位、用户暂停或可恢复中断保持 Partial 与恢复作业；硬页数/字节预算、损坏页或必要能力缺失导致终止时，明确标注 Partial/Failed、未覆盖范围与 resource_limit/parse_error/capability_unavailable 等原因和最小恢复动作，不当作正常补全完成，也不无限重试。取消与权限撤销按各自合同处理。

每个文件记录 `Pending / Processing / Partial / Ready / Failed / Cancelled`；队列恢复状态持久化。任务携带文件身份、发现版本和有效选项快照。处理顺序：

1. 等待文件稳定并取得读取租约，记录身份、mtime、size 与读取内容哈希。
2. 在数据库写事务外解析、分块、OCR、分词与推理；受限制的暂存避免囤积全部文档。
3. 提交前再次核查内容版本与权限；版本变化丢弃过时结果并重新排队，禁止旧任务覆盖新版本。
4. 通过唯一索引写入器原子替换一个文件的所有切片、FTS、向量和状态。

哈希、解析与提交必须对应同一内容版本；相同 mtime 不足以保证未修改。重命名、删除与重复事件按文件身份幂等处理；路径冲突不吞掉异常。Watcher buffer overflow 触发补扫且可观察。首版任务恢复可以重算，不要求保存未完成模型张量。

### 4. 资源与负向合同

为解析字节数、页数、解压量、像素数、单文件时长和返回内容设上限；压缩型文档的磁盘体积不等于展开体积。至少覆盖空文件、畸形格式、损坏 PDF、大图、文件被占用、磁盘满、队列饱和、取消和解析依赖崩溃。

一个文件失败不能使索引队列永久停止；错误保留文件级状态与可诊断类别，不无限重试。部分 PDF 必须显示 `Partial` 和覆盖范围，搜索无命中不能推出未覆盖页面无内容。

## 六、中文分词与混合检索

### 1. rjieba 的定位与集成决策

已核验：`messense/rjieba-py` 是 `jieba-rs` 的 Python binding，安装方式是 `pip install rjieba`；并非已经提供的 .NET 包或可直接 P/Invoke 的稳定 C ABI。正式产品不因分词单一需求引入 Python 环境。

本方案采用以下验证顺序：

1. 优先验证 `jieba-rs` 通过最小自有 `cdylib` C ABI 与 .NET 互操作，随安装包分发；rjieba 可在隔离开发工具中作为对照，不是终端依赖。
2. 若该适配不能通过维护、性能或打包门，比较真实可用且维护/许可合格的 .NET Jieba 实现，使用同一 fixture 和检索评价集验收，不混用两套索引规则。
3. 分词选型在开发阶段零完成；未通过则中文词法能力为发布阻塞，不能静默退回 trigram 并声称两字中文已支持。

原生方案是待实现设计，不存在可直接引用的“官方 rjieba.dll”。ABI 必须规定 UTF-8、长度、buffer ownership、释放、错误码、panic 边界与线程安全；失败不可变成空 token。固定 crate、词典和适配版本，附 cargo lock、许可证与构建证据。

### 2. 词法索引合同

在应用层使用 Jieba 搜索模式生成词项，然后以空格分隔写入 FTS5 `unicode61`。不注册未经验证的 SQLite 自定义分词扩展。索引与查询使用相同规范化、词典和搜索模式，变更 fingerprint 必须重建词法索引。

- 中文短词如“医保”“合同”“报销”进入独立词项索引，可精确词命中；单字查询采用受限文件名/词项策略，避免无界扫描。
- 自定义词典覆盖产品名、医疗/技术词、用户明确添加术语；按版本管理，不联网获取词典。
- 混合输入保留英文、数字、标识符，并提取完整错误码/函数名；分词不能破坏 `0x80070005` 等精确标识符。
- embedding 和预览都保留原始正文，禁止把分词后的文本作为用户展示内容。
- FTS 查询通过受控构造器生成词项/短语与 AND/OR 组合，不把原始用户输入直接当 FTS 语法；SQL 参数化不等于 FTS 语法已经转义。

另建 raw trigram 索引用于长度至少 3 的连续子串、OCR 标识符和名称片段。trigram 不负责两字中文，完整自然语言也不能强制变成一个连续短语。

### 3. 检索计划与排序

查询解析为自然语言、中文关键词、精确标识符、名称/路径片段，并校验空输入、长度、模态与目录范围。

- 自然语言：768 维向量 + 分词全文召回。
- 中文短词：分词全文 + 文件名；向量可补充。
- 标识符：规范化精确词项/原文子串优先，向量不代替精确召回。
- 目录筛选：授权目录解析为允许的文件/根身份，而不是 SQL 字符串前缀。

向量 KNN 必须在有效权限/筛选集合内完成或有严格有界的补取协议；不能只在全库 Top-K 后过滤并宣称目录内结果完整。`sqlite-vec` 版本、partition/metadata 过滤能力、JOIN 次序和计划要在目标环境验证，不预先承诺未经实测的 SQL 组合。

词法、trigram 和向量先归并为两个主排名通道：语义与词法；同一证据只计一次词法通道贡献。RRF 的 `k=60` 可作为初值，权重由固定检索集评价后锁定。RRF 是融合排序分，不是概率或语义相似度。

候选切片先合并，再按文件聚合：每文件最多返回一个主结果及有限的补充证据。视觉/OCR 双路图片不能重复挤占文件数 Top-K；长文档多切片也不能占满全部结果。

返回结果使用：

- 文件 ID、当前内容版本、授权根、模态、定位类型和范围；对外默认使用不泄露多余路径的引用 ID。
- 原始片段、证据通道、索引覆盖与新鲜度。
- `CosineSimilarity`（未实际计算则为 null）、`LexicalScore`、`FusionScore`、`MatchKind`。

禁止给 FTS 独有命中赋造假的 `0.85` 余弦分。不同量化/模态的分数不能未经评价直接套统一置信度阈值；低相关、部分覆盖和零结果要能区分。

## 七、SQLite Schema 与索引一致性

### 1. 单活动库与版本元数据

默认数据库位于 `%LOCALAPPDATA%\WinSight\winsight_index.sqlite`。运行时 WAL 模式可能产生 `-wal`、`-shm`，因此“单数据库”不表示运行时只有一个文件。备份和迁移使用 SQLite 一致性导出，不直接搬运活动主文件。

每个活动库只有一个模型/解析/词法兼容代次。以下 SQL 是普通表和 FTS 的契约；实现还须有 schema version 迁移与启动校验。

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE index_meta (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    schema_version INTEGER NOT NULL,
    generation_id TEXT NOT NULL,
    model_fingerprint TEXT NOT NULL,
    processor_fingerprint TEXT NOT NULL,
    chunker_fingerprint TEXT NOT NULL,
    lexical_fingerprint TEXT NOT NULL,
    embedding_dimension INTEGER NOT NULL CHECK (embedding_dimension = 768)
);

CREATE TABLE watched_folders (
    folder_id INTEGER PRIMARY KEY,
    folder_key TEXT NOT NULL UNIQUE,
    folder_path TEXT NOT NULL,
    include_subfolders INTEGER NOT NULL CHECK (include_subfolders IN (0, 1)),
    enable_images INTEGER NOT NULL CHECK (enable_images IN (0, 1)),
    enable_code INTEGER NOT NULL CHECK (enable_code IN (0, 1)),
    is_paused INTEGER NOT NULL CHECK (is_paused IN (0, 1)),
    policy_version INTEGER NOT NULL,
    last_scan_utc TEXT
);

CREATE TABLE indexed_files (
    file_id INTEGER PRIMARY KEY,
    folder_id INTEGER NOT NULL REFERENCES watched_folders(folder_id),
    file_key TEXT NOT NULL UNIQUE,
    file_path TEXT NOT NULL,
    file_name TEXT NOT NULL,
    modality TEXT NOT NULL CHECK (modality IN ('doc', 'code', 'image')),
    file_size_bytes INTEGER NOT NULL CHECK (file_size_bytes >= 0),
    last_write_utc TEXT NOT NULL,
    sha256_hash TEXT,
    content_version TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('Pending', 'Processing', 'Partial', 'Ready', 'Failed', 'Cancelled')),
    coverage_json TEXT NOT NULL,
    last_error_code TEXT
);

CREATE TABLE content_chunks (
    chunk_id INTEGER PRIMARY KEY,
    file_id INTEGER NOT NULL REFERENCES indexed_files(file_id),
    chunk_index INTEGER NOT NULL,
    chunk_role TEXT NOT NULL CHECK (chunk_role IN ('text', 'visual', 'ocr')),
    locator_json TEXT NOT NULL,
    snippet_text TEXT NOT NULL,
    has_vector INTEGER NOT NULL CHECK (has_vector IN (0, 1)),
    UNIQUE (file_id, chunk_index)
);
CREATE INDEX ix_chunks_file ON content_chunks(file_id);
CREATE INDEX ix_files_folder ON indexed_files(folder_id);

CREATE TABLE indexing_jobs (
    job_id TEXT PRIMARY KEY,
    folder_id INTEGER NOT NULL REFERENCES watched_folders(folder_id),
    file_key TEXT NOT NULL,
    expected_version TEXT NOT NULL,
    job_kind TEXT NOT NULL CHECK (job_kind IN ('file', 'pdf_pages')),
    page_start INTEGER,
    page_end INTEGER,
    checkpoint_json TEXT NOT NULL DEFAULT '{}',
    state TEXT NOT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    last_error_code TEXT,
    updated_utc TEXT NOT NULL,
    CHECK ((job_kind = 'file' AND page_start IS NULL AND page_end IS NULL)
        OR (job_kind = 'pdf_pages' AND page_start IS NOT NULL AND page_end IS NOT NULL
            AND page_start >= 1 AND page_end >= page_start))
);

CREATE VIRTUAL TABLE fts_terms USING fts5(
    file_name_terms,
    body_terms,
    identifier_terms,
    tokenize = 'unicode61'
);
CREATE VIRTUAL TABLE fts_raw USING fts5(
    file_name,
    raw_text,
    tokenize = 'trigram'
);
```

两个 FTS 表自行保存内容，`rowid = chunk_id`。这样不再将缺少 `file_name` 的 `content_chunks` 声明为外部内容源，代价是一定重复存储，计入容量测试。空视觉文本可不写 FTS；原文和分词文本各有用途。

`file_key` 与 `folder_key` 由 Windows 路径/身份服务生成，不依赖 SQLite NOCASE 完整处理 Unicode 和物理路径身份。

以下虚拟表必须在已加载固定版本 `vec0.dll` 的实际 SQLite 运行时验收，普通 SQLite 不能验证扩展行为：

```sql
CREATE VIRTUAL TABLE vec_chunks USING vec0(
    chunk_id INTEGER PRIMARY KEY,
    modality TEXT PARTITION KEY,
    embedding float[768] distance_metric=cosine
);
```

### 2. 唯一写入器与事务

只有一个逻辑写入器，维护一条写连接；读请求使用有界独立读连接和 snapshot/连接租约。WAL 支持读写协作，不等于无锁；所有连接设置 foreign_keys、busy timeout，逐连接正确加载扩展。默认 NORMAL 持久性等级及断电行为必须记录；需要更强持久性时在实测后采用 FULL。

不要用覆盖全部查询的大锁把搜索、索引和迁移无差别串行化。迁移/压缩通过维护租约协调所有连接，并在新连接开放前恢复服务。

文件更新事务：

1. 再次检查文件版本、权限和活动代次。
2. 按旧 chunk ID 删除 `vec_chunks`、`fts_terms`、`fts_raw`，再删旧 `content_chunks`。
3. 写入新切片；其 FTS rowid 与 vec chunk_id 一致；无向量切片显式 `has_vector=0`。
4. 更新 indexed_files 的版本、哈希、覆盖、状态和任务回执，一次提交。

删除文件或目录也经同一写入器，先清理派生索引再删主表。普通表外键不能替 vec/FTS 做级联；不用 cascade 掩盖独立虚拟表维护。事务失败回滚，旧可用版本不被半成品覆盖。独立状态事务可记录失败但不得把旧切片标记为新内容。

至少执行孤儿对账：FTS/vec rowid 无对应 chunk、`has_vector=1` 无向量、派生索引数与应索引切片数不一致、代次不匹配；问题报告为索引错误而非“搜索无数据”。

### 3. 重建与代次切换

模型/处理器/切片/词法 fingerprint 不兼容时建立旁路 staging 数据库，原活动库继续服务。默认完整重建；仅词法规则变化可在经过验证的专用维护流程中从原文重建 FTS，不修改旧 fingerprint 后继续使用未重建的索引。

切换前必须通过 integrity check、外键检查、派生索引对账与固定查询集。`ActiveSearchBundle` 将模型句柄、处理器 fingerprint、数据库租约和 generation_id 一起发布，防止新查询向量进入旧空间。切换后旧请求在旧租约内完成或取消，旧库留作恢复点；不能只修改数据库路径或模型选项。

## 八、存储位置、备份与迁移

### 1. 存储位置策略

用户选择本机目录；首版禁止网络共享，目标卷文件系统/ACL/WAL能力必须通过自检。任意应用私有目标仍需验证当前用户保护边界。默认选择用户私有目录，不自动把内容库放到多人可读目录。

“迁移现有数据”和“新建空库”是两种动作。目标已存在时拒绝覆盖，除非另有明确覆盖授权与恢复点；不能执行无提示的 File.Delete。

### 2. 迁移状态机

```text
Ready → Maintenance → Exporting → Validating → Switching → Ready
                     │              │            │
                     └──────────────┴────────────┴→ RecoverOld
```

1. 冻结配置版本，获得维护租约；拒绝新维护任务，排空/取消读租约，暂停写入而不丢任务。
2. 检查目标 ACL、空间、同一路径/文件身份、文件系统与禁止覆盖条件。
3. 使用 `VACUUM INTO` 或经验证的 SQLite backup API 导出到唯一临时文件；核验扩展虚拟表在该方法下的完整性。
4. 以独立连接加载 vec0，执行 integrity_check、foreign_key_check、代次/计数/索引对账和样例检索。
5. 准备新连接池与搜索 bundle；原子更新带版本的配置指针。切换前留存旧文件和配置恢复点。
6. 成功后恢复队列；失败则恢复旧路径、连接和 bundle，并返回具体失败阶段。旧库不自动删除。

新建空库也必须初始化完整 Schema/metadata 和授权目录配置；显示需重建，不把空库当迁移成功。取消、磁盘满、权限不足、导出失败、新库损坏、配置写失败与进程中断都要有可恢复状态。

`VACUUM` 为维护操作，不能在活跃查询/写入下任意启动；数据目录迁移不是原始文件目录迁移，已有文件授权要重新核验。

## 九、MCP 工具、传输与授权

### 1. 对外工具合同

| 工具 | 默认权限 | 输入/返回 |
|---|---|---|
| `search_files` | 获准目录内搜索 | query、枚举 mode、获准目录 ID、topK 1–20；文件级证据列表，标注覆盖/新鲜度 |
| `read_file_context` | 有效命中引用的限定读取 | 命中 ID、内容版本、locator、范围；版本过期返回 stale_reference，不能直接放行任意绝对路径 |
| `get_index_status` | 最小只读状态 | 可用模态、计数、覆盖、错误/忙状态；默认不暴露数据库路径和全部监控根 |
| `index_directory` | 独立维护授权，默认关闭 | 获准目录 ID、受限 force；返回 job_id，异步任务由状态接口查询，不长时间占住工具请求 |

对 mode、空查询、范围、目录 ID 和内容版本严格校验。维护授权不能因模型调用了工具而自动获得；强制全量重建需明确授权和资源预算。

初始安全预算：query 上限 4,096 字符；单次上下文最多 200 行或 10 个 PDF 页，且 UTF-8 正文最多 64 KiB；普通调用默认 30 秒超时。DOCX 按段落数执行等价上限。范围无效返回 validation_error；超预算返回明确 truncated 标记与续读定位，不能静默截断。参数是否需要调整由产品验收决定，不伪装实测最优值。

结果是 MCP 结构化内容，不嵌套一个不可解释的 JSON 字符串。主错误类至少区分 validation_error、unauthorized、excluded_file、stale_reference、busy、cancelled、timeout、model_unavailable、index_incompatible、parse_error 与内部错误；日志保留脱敏原异常。

### 2. HTTP 与命名管道

- HTTP 仅绑定 loopback，默认 18420，可配置；实际 IPv4/IPv6监听地址可见，不开放 LAN。按规范校验 Origin，拒绝不获准 Origin；缺失 Origin 的非浏览器客户端按认证合同处理。
- localhost 不替代身份。使用当前用户受保护存储中的高熵本地凭据或等价经过验收的认证机制；token 不进入 URL、日志、stdio内容或普通配置明文。
- 首版第三方客户端优先通过 stdio 桥接使用命名管道。直接 HTTP 接入只有在该客户端支持安全凭据配置且经过授权后启用，不能为了兼容跳过认证。
- 管道 ACL 限定当前用户/获准宿主，进行连接身份、会话与权限校验；固定管道名本身不是认证。
- pipe 与 MCP framing 分开定义。若 IPC 承载 envelope，约定版本、长度上限、request_id、cancel、timeout、重连与错误映射；不要将 SDK stdio 流误当任意长度 JSON 文本。
- 桥接器 stdout 只写合法 MCP 消息，日志至 stderr；协议握手、取消、通知、断连行为以 SDK 当前版本为准。
- 自动静默拉起宿主只有用户开启后才允许；单实例锁防止多客户端重复拉起。启动失败、协议版本不符和用户禁用不无限重试。

### 3. 启停状态

服务状态为 `Disabled / Starting / Running / Stopping / Faulted`。用户意图与实际运行状态分开记录，不能在 StartAsync 前就显示 Running。

关闭过程先撤销外部授权并拒绝新请求，再取消在途 MCP 调用、关闭 HTTP 和 pipe、处理桥接断连，完成后为 Disabled。桌面端搜索与内部索引可继续，除非用户另外暂停。生命周期转换加锁，所有 transport 使用同一权限/状态快照。

状态审计覆盖失败、拒绝和取消，不只记录成功调用；UI 事件通过 DispatcherQueue 切回 UI 线程。

### 4. 客户端配置助手

用户点击“预览并接入”才读取指定客户端配置；展示脱敏差异，不自动扫描其他客户端。先备份，解析失败就停止，保留未知字段，使用唯一临时文件与原子替换；写入前核查文件版本避免覆盖并发修改。已存在同名不同内容入口时不静默覆盖。

默认只注入桥接器绝对路径与 `--stdio`，不写 token。安装目录调整、升级/卸载的残留配置清理须用户授权；客户端未验证则只提供配置预览，不声称成功挂载。

## 十、WinUI 3 界面与可观察状态

遵循 Windows Fluent Design；不自动套用其他品牌。保留快捷浮窗与四个主标签页：

| 页面 | 必需行为 |
|---|---|
| 快捷浮窗 | 可配置热键，冲突检测；默认可尝试 Alt+Space 但告知 Windows 系统菜单冲突；键盘选择、预览、打开 |
| 搜索页 | 模态/授权目录筛选；文件级列表；原文证据、页/段/行定位；实际测得分数与匹配类型 |
| 监控目录页 | 根、子目录、图片、代码、暂停；文件数、任务状态与覆盖；失败可查看原因，Partial 不显示为完成 |
| 存储与模型页 | 实际路径、占用、迁移/空库/压缩；固定 768 维；模型包版本、完整性、量化和实际 provider；无维度切换 |
| MCP 控制台 | 默认关闭；启停真实状态、授权客户端、HTTP/pipe状态、维护权限和脱敏审计 |

约 150 ms 输入防抖只减少请求数，不是端到端延迟保证。每次输入递增 request_id 并取消旧请求；旧结果返回时不得覆盖新查询。冷模型显示真实加载状态，无编造进度百分比。

托盘“关闭窗口”与“彻底退出”分别定义；快捷键注册通过已验证的 Win32/Windows 能力，不因托盘库存在就假定其提供全局热键。原始文件不自动执行，打开前重新检查权限与当前目标；危险可执行格式不纳入正常检索打开路径。

验收涵盖明暗模式、高 DPI、窄屏/放大、键盘、焦点、屏幕阅读器、减少动态效果、空/忙/失败状态。MCP 工具不依赖 UI 线程响应；UI 也不直接同步执行 ONNX。

## 十一、离线安装、升级与构建

### 1. 构建基线

App 的项目属性片段如下；它不是省略依赖后仍可直接构建的完整 csproj。实际项目需固定 Windows SDK 和 Windows App SDK 依赖并通过 .NET 10 兼容性验收。

```xml
<PropertyGroup>
  <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
  <SupportedOSPlatformVersion>10.0.22000.0</SupportedOSPlatformVersion>
  <OutputType>WinExe</OutputType>
  <UseWinUI>true</UseWinUI>
  <WindowsPackageType>None</WindowsPackageType>
  <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
  <SelfContained>true</SelfContained>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <ApplicationManifest>app.manifest</ApplicationManifest>
</PropertyGroup>
```

Bridge 使用 `net10.0`、`AssemblyName=winsight-mcp`、`RuntimeIdentifier=win-x64`、`SelfContained=true`。首版可以目录发布以降低单文件抽取复杂度；如使用 PublishSingleFile，必须验证所有原生依赖、提取位置和干净机器启动。不再承诺桥接器不足 2 MB。

### 2. 安装包内容与落盘

构建统一 release staging，至少包含：

```text
release/
  app/                         WinSight.App.exe + 全部自包含依赖
  bridge/                      winsight-mcp.exe + 全部自包含依赖
  models/embeddinggemma-2/<revision>/
    model-manifest.json
    onnx/                      文本/视觉图及外部数据
    tokenizer/processor/config 等清单内工件
  native/win-x64/               vec0.dll + 经批准的中文分词适配
  dictionaries/                固定词典与用户词典模板
  licenses/                    产品、模型、词典、NuGet/Rust/原生库许可
  release-manifest.json         全部产物的哈希、版本、架构与SBOM引用
```

模型是安装包必带内容，不是“可选预置”或首次运行补下载。默认只读落盘到安装目录的 versioned models；用户数据、配置、自定义词典、索引和日志放在当前用户私有目录。新安装与升级不能写入旧索引目录冒充模型目录。

默认每用户安装可采用 `%LOCALAPPDATA%\Programs\WinSight`，应用目录及模型受当前用户 ACL 保护；机器级安装作为另一个明确验收路径，数据库仍按用户隔离。不让主程序以管理员身份长期运行。

Inno Setup 必须复制 release staging 中全部目录和文件，保持相对结构，不只拷贝 bridge exe 与 vec0。包中模型许可/通知与安装器信息页可回查；来源 revision 和校验通过后才入包。

首版不默认修改 PATH；通过复制桥接绝对路径/配置助手接入。开机启动与桌面快捷方式明确可选，MCP 不因开机启动自动获准。卸载默认保留个人索引与配置，删除需要单独选项；不顺带删外部客户端配置。

### 3. 构建与发布门

流水线必须执行：SDK/依赖锁定检查 → restore/build/test → App 与 Bridge self-contained publish → native/model/dictionary/license 收集 → 清单校验 → Inno 编译 → 安装/升级/卸载测试。

PowerShell 使用基于 `$PSScriptRoot` 的绝对路径；每次 `dotnet`、native builder 和 `iscc` 返回后检查 `$LASTEXITCODE`，失败立即停止，不能只设置 `$ErrorActionPreference` 并无条件输出“完成”。

安装器和 exe/DLL 签名按发行政策执行，校验模型/依赖完整性和真实文件权限。只有产物存在、内容完整且干净 Windows 测试通过后才报告发布成功；签名、构建和发布均不能由设计文档自动获得执行授权。

MSIX 可在后续另行验证模型体积、桥接启动、身份/生命周期与第三方客户端兼容；不再把未实现的双轨发布列为首版保证。

### 4. 模型升级与恢复

版本化模型包通过新的受控安装包交付，运行中不原地覆盖模型文件。升级安装保留旧完整包与旧索引恢复点，先校验新包再进入 staging 重建。只有兼容或新索引通过门禁后切换 ActiveSearchBundle；失败继续使用旧版本，不自动降到不兼容新空间。

用户自定义词典位于数据目录，升级不覆盖；词典变化生成新 fingerprint。模型/程序/数据库回退必须成套匹配，而不是只回退 exe。

## 十二、验收矩阵、容量与开发顺序

### 1. 容量与性能口径

768 维 Float32 原始向量大小为 `768 × 4 = 3,072` 字节/切片。10 万切片为 307.2 MB；100 万切片为 3.072 GB（十进制），未含原文、双 FTS、元数据、SQLite开销和WAL。按切片数、视觉/OCR向量数和平均正文长度评估，不能用文件数代替。

模型工件大小、安装包压缩后体积与运行内存分开记录；不把 q4 下载体积作为进程内存上限。`sqlite-vec` KNN 不自动意味着 ANN，规模扩大必须测查询计划与实际延迟，未触发性能缺口前不切换数据库。

性能报告注明 CPU、GPU、驱动、Windows版本、RAM、模型 revision/EP、索引规模和查询集，覆盖冷启动、暖查询、首次视觉加载、后台索引竞争、多客户端、CPU回退、长文件与迁移。记录 p50/p95、峰值CPU/GPU内存、吞吐、错误率和取消耗时。

建议暖查询 p95 ≤ 500 ms 作为首轮待确认目标，在固定验收机、固定查询集和索引规模下量测；冷加载单列。只有通过实测后才对外承诺响应速度，未定验收机/规模不得宣称达标。

### 2. 可验证交付门

| Gate | 正向/负向断言 | 必需证据 |
|---|---|---|
| G1 模型契约 | 官方 tokenizer/processor fixture 对齐；768有限向量；文本/图片检索排序可解释；缺失/损坏工件报错 | 固定revision、metadata、样本输出与差异；CPU和DirectML分别记录，未测EP不标通过 |
| G2 中文词法 | “医保/合同/报销”、产品词、中英混合、错误码、引号等输入；索引/查询同规则；分词依赖失败可见 | 原生/托管互操作与固定词典fixture；FTS真实查询、中文检索评价 |
| G3 一致性 | 新增/修改/删除/重命名无陈旧/孤儿；事务中断回滚；失败版本不覆盖旧版 | 已加载vec0的实际SQLite事务与对账；普通SQLite只验证FTS不替代vec集成 |
| G4 增量恢复 | watcher溢出补扫；文件变化拒绝旧任务；崩溃重启/磁盘满/取消收敛；长PDF第31页之后的内容最终可检索，重启后继续补全；暂时未完成与资源限制终止明确区分 | 在第31页及后续页放唯一测试词，页批中断/重启后全部命中、无重复/跨版本切片；公平调度、覆盖/终止原因、任务checkpoint与索引对账回执 |
| G5 检索 | 文件级去重、目录内过滤召回、短词/标识符/语义/视觉/OCR、多证据定位 | 固定标注查询集，Recall@K/nDCG或明确命中断言，记录失败而非只报均分 |
| G6 权限与MCP | 排除文件/越界/链接/旧引用拒绝；HTTP Origin/认证；pipe ACL；关闭取消；stdout纯协议 | 当前用户/另一用户边界测试、真实客户端与transport集成回执 |
| G7 迁移/代次 | 目标存在拒绝；导出/新库/配置失败可恢复；模型与DB一起切换 | vec虚拟表导出验证、integrity/对账、回滚路径与进程中断测试 |
| G8 UI | 旧查询不覆盖新结果；键盘/焦点/状态可访问；热键冲突与托盘退出 | 最终渲染、关键交互、辅助技术与高DPI检查 |
| G9 离线交付 | 无.NET/Python/开发工具、断网首次启动；模型与词典齐全；Bridge可运行；升级/卸载不删用户数据 | 干净Windows11 x64安装记录、release manifest、离线网络观察、许可与签名检查 |

空结果是业务结果；认证、模型、分词、数据库或解析失败是错误，不归一化为 no_data。未通过必要 Gate 的功能不得标记为已完成；公开状态须说明静态配置、已执行测试与未测环境。

### 3. 开发顺序

- 阶段零：冻结模型/依赖工件与 .NET 10 构建；完成 tokenizer/视觉链路、jieba-rs .NET适配候选、CPU/DirectML和许可最小探针。不能先让整库索引后再核对向量语义。
- 阶段一：完成一个授权目录的文本+OCR、768维索引、双FTS、原子更新与文件级检索；状态/错误/取消可见。该切片不冒充完整多模态交付。
- 阶段二：接入经过G1的视觉向量、所有声明格式、长PDF余页可恢复补全、长期任务恢复、代次切换和位置迁移；验证吞吐和容量。
- 阶段三：接入带权限的HTTP/stdio、全局快搜、四页面和客户端配置助手；统一同一核心合同。
- 阶段四：完整离线安装包、干净机器、真实客户端、恢复演练与最终发布门。

每阶段交付可运行、可测试的切片；未实现功能不提供伪响应。必要功能/安全门未通过就停止该发布，不以UI完成率替代验收。

## 十三、公开依据与本轮变更索引

以下来源仅用于核验接口/配置/政策，不是本产品测试结果；检索日期为2026-10-14。发布实现须锁定源revision，网页当前内容不能替代工件清单。

- [EmbeddingGemma 2 官方模型](https://huggingface.co/google/embeddinggemma-2)
- [官方 processor_config.json](https://huggingface.co/google/embeddinggemma-2/blob/main/processor_config.json)
- [EmbeddingGemma 2 ONNX 模型与工件](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX)
- [LlamaTokenizer.Create 输入契约](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.tokenizers.llamatokenizer.create)
- [ONNX Runtime DirectML EP 限制](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
- [SQLite FTS5](https://sqlite.org/fts5.html)
- [sqlite-vec KNN](https://alexgarcia.xyz/sqlite-vec/features/knn.html)
- [MCP transport 与安全要求](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)
- [.NET 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [rjieba Python binding](https://github.com/messense/rjieba-py)
- [jieba-rs Rust 实现与 bindings](https://github.com/messense/jieba-rs)

| 前轮审计项 | 本轮处理 |
|---|---|
| WS-01 模型契约 | 删除错误推理示例；明确真实图/processor、tokenizer fixture、最终输出、完整离线工件 |
| WS-02 DirectML并发 | 固定session约束、统一调度、分类回退、实际provider区分 |
| WS-03 FTS/派生一致性 | 自存双FTS、唯一写入器、事务替换/删除、孤儿对账 |
| WS-04 代次 | 固定768维，metadata/fingerprint、新库与模型bundle成套切换 |
| WS-05 MCP边界 | 统一路径策略、当前用户认证/ACL、只读/维护分权、返回预算与关闭取消 |
| WS-06 排序 | 中文分词+trigram、受控FTS、文件级聚合、真实分数和目录内召回验证 |
| WS-07 迁移 | 目标不覆盖、临时导出、验证/切换/恢复、WAL与租约协调 |
| WS-08 索引恢复 | 版本任务、周期收敛、格式定位、资源上限、Partial/Failed状态 |
| 性能与分发 | 删除未经实测的内存/延迟/桥接体积承诺；.NET10、完整离线包、退出码/许可/干净机门 |

本文替代旧版未经编译的 C#/XAML/安装器示例。后续实际源码只有通过对应契约与物理验证才能成为实现依据；本次设计修订不代表模型、原生分词、vec0、MCP 或安装包已经运行验收。
