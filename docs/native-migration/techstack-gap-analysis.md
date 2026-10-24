# techstack.md 与当前 Windows 原生实现对照

> 退役范围说明（2026-10-23）：本页保留历史设计、迁移依据或行为对照。涉及 Swift/Tauri、旧 Vendor、旧 CLI 与旧生成脚本的路径／命令只供回查，不再是当前可执行开发入口。原始源码位于 Git 提交 `400c5b501e664f7aaaf2c2ec5e1c270cf0425a87`；当前主线见仓库 README 和 `docs/native-migration/legacy-retirement.md`。原验收缺口不会因源码退役自动关闭。


结论：**原生候选的主框架已经完成替换；优先补音视频提取和原生发布链，不按 NuGet 清单重写正确实现。**旧 Swift/Tauri 发布链仍存在，候选运行成功不等于产品入口已切换。原始 `techstack.md` 保留不改。

分析基线：`.dev/native-migration/native-stack-gap/baseline/manifest.json`；输入文档 SHA-256：`ECF288BA9D18EDD4668EF06997D4A9FCD93EAA2933000A2457BF303DFB7DC774`。两路只读审计覆盖桌面/部署和引擎/存储；主代理补查服务方法体、数据库 tokenizer 和实际 workflow。源码事实不等于运行时验收，未经读取的旧协议能力不作“全仓不存在”断言。

## 应实施与应保留

| 对照项 | 当前事实及证据 | 判断与动作 |
|---|---|---|
| 正式 UI/发布入口 | `desktop/src/WinSight.App` 已是 WinUI；`.github/workflows/packages.yml` 仍构建 Swift、Tauri/NSIS、FFmpeg，`ci.yml` 仍测 Rust shell | **应逐步替换发布链**。先新增原生验证/候选入口，完整模型和安装回滚验收后才退役旧链，不先删除 |
| 音视频提取 | `Infrastructure/Extraction/ContentExtractor.ExtractAsync` 对音视频明确返回不支持；原生 API 探针通过不等于此方法已接通 | **最高优先补齐**。用 WinRT 解码、任务产物及父进程硬取消，替代旧链对 FFmpeg 的媒体职责；不新增 FFmpeg/Python |
| PDF 文本 | 官方 `PdfPig [0.1.16]`，纯托管；`ReadPdf` 仍在进程内 | **保留解析库**。techstack 的 `1.7.0-custom-5` 不能作为升级目标；后续考虑解析隔离，不换成不能提取正文的 PDF 渲染 API |
| Word | `DocumentFormat.OpenXml [3.5.1]`，`ContentExtractor.ReadWord` | **已采用，保留**；无须安装 Office/COM 自动化 |
| 图像 | 宿主 `MediaImage` 已用 WinRT/WIC 解码；数值预处理为托管实现 | **已原生，保留**。不为匹配提案引入 ImageSharp，更不同时改变 resize、色彩、归一化与 embedding profile |
| .NET/C# | `desktop/global.json` 固定 SDK `10.0.113`；`Directory.Build.props` 为 `LangVersion=latest` | 已采用 .NET 10；提案的 C# 12 不是现状，不盲降语言版本 |
| Windows SDK | App 目标 `26100`，最低 OS `22000`；提案写 `22621` | 编译目标与最低运行版本不是同一字段；兼容性必须实测，不仅改 TFM 字符串 |
| WinUI 3 | `Microsoft.WindowsAppSDK [2.5.1]`、`UseWinUI=true`；已发布 App 先前冷启动/UIA 通过 | 已采用；本轮限定源文件未发现显式 Mica 配置，材质可单独做呈现优化，不是替换框架理由 |
| MVVM/设置卡片 | 手写 `INotifyPropertyChanged` 和 XAML 控件，未采用 Toolkit/SettingsControls | 都是托管/原生 UI；**不默认换包**。只有具体重复或交互缺陷才定向处理，不声称 Source Generator 自动消除性能问题 |
| 托盘/热键 | `TrayIconService` 调用 Shell_NotifyIcon；`HotkeyService` 调用 Win32 RegisterHotKey | 已原生且不依赖 Forms；**不必加入 H.NotifyIcon**。默认 Ctrl+Shift+Space 保留，不暗改 Alt+Space |
| 文件动作/剪贴板 | `FileActionService` 使用 Windows Shell/Explorer 和 WinRT Clipboard；授权由 planner 与当前设置控制 | 属于 Windows 能力；Explorer 命令可评估结构化 Shell API，但不以“用了 Process”判定为跨平台残留，也不削弱授权 |
| Windows ML | 宿主直接引用 `Microsoft.WindowsAppSDK.ML [2.1.94]`；其包提供 `Microsoft.ML.OnnxRuntime` namespace | 已采用；namespace 名称不能证明独立 ORT/DirectML 包残留。保留 provider 数值与设备资格门 |
| 严格离线 | CPU 默认路径可离线；auto 资格流程中的 EnsureReady 可能下载 provider | **需要明确运行策略**。不把 auto 宣传成严格离线；本轮不运行 provider 下载、不改用户设备偏好 |
| tokenizer | 私有路径加载的原生 Rust DLL，固定 tokenizers `0.23.1` 与完整 tokenizer JSON | 已是原生，不是 Python 胶水；替换为 Microsoft.ML.Tokenizers 前须逐 token 证明 normalization、added tokens、BOS/EOS 和后处理一致 |
| 模型分支 | 当前 manifest 与宿主明确包含 text/image/audio | **保留音频**。提案“避开音频模型”会删现有目标能力；模型体积/内存数字未实测，不作为裁剪依据 |
| SIMD | 当前候选 CPU 向量运算为托管循环；未把 TensorPrimitives 当成现有能力 | 性能优化候选，不是非原生缺陷；先量测，再做排序/数值 parity，不为包清单增加依赖 |
| SQLite | `Microsoft.Data.Sqlite [10.0.13]`、WAL、schema5、768d Float16 BLOB | 已采用嵌入式原生 SQLite；保留存储契约，不改为数据库服务 |
| sqlite-vec | 当前无 vec0 虚表 | 属于检索/数据结构变更，不是补原生 DLL 即可。须单独设计迁移、完整排名/过滤对照、回滚和 DLL 许可完整性 |
| FTS5 | `SqliteIndexRepository.Tokenizer` 为 porter/unicode61，不是 trigram | 真实差异，但更换会改变索引与查询语义；作为独立搜索能力项目，不混进媒体/框架替换 |
| VACUUM INTO | 当前没有自动热迁移承诺 | 新增运维能力，不是原生化完成项；必须明确复制/切换/锁与恢复契约，不能自动移动用户索引 |
| Windows OCR | 当前 unpackaged 方向；官方 Windows.Media.Ocr 桌面支持要求包身份 | 不能承诺“零体积直接可用”。MSIX/包身份与独立本地 OCR 需明确取舍；不自动安装 RapidOcrNet/模型 |
| MCP/HTTP | 候选 `IDesktopBackend`、包清单未实现提案中的工具/Streamable HTTP 宿主；未完成旧协议的全仓方法体核验 | 是新增协议能力，不是 Windows 原生替换项；先定义工具、权限、认证、端口/生命周期与兼容契约 |
| Hosting | 当前显式创建 backend，手动、有界释放 watcher/模型/数据库 | 托管原生实现有效；不为“统一”强加 Generic Host。只修可复现的生命周期故障 |
| Git 文件筛选 | `Indexing/GitKeptFiles` 仍调用外部 git | Git 语义依赖，不是 macOS/Swift 残留；保留 .gitignore/排除/工作树语义，不手写一个简化 Git 规则引擎 |
| 安装器 | 当前原生仅自包含目录；旧工作流为 NSIS，未验证 Inno 编译器/干净机安装 | 应实施新的原生安装/升级回滚链；无模型载荷和许可闭环前不得切换正式发布，用户/系统 PATH 修改必须明确 |
| Python/PowerShell 脚本 | 源码检查、夹具、包审核等开发工具，不是新 App 的运行依赖 | 不需要为“纯净”重写；不能把构建脚本语言等同于产品运行时依赖 |

## 分步执行与不可混做的变更

1. **N0 对照闭环**：本报告和源基线；保留原始目标文档，不静默把提案改成事实。
2. **N1 原生媒体原语**：现有宿主新增免模型 `extract-media` 命令；单个明确音频区间或视频帧；真实 WinRT 夹具与负向测试。不改模型、tokenizer、数据库或现有 stdio。
3. **N2 安全接入索引**：父进程持有源文件保护、独立任务目录和产物租约，确认子进程结束后才清理；保留原音频采样、视频声轨/帧策略及位置，完整/partial/failed 明确。没有此步，不能把 N1 宣称为 App 已支持音视频索引。
4. **N3 发布与平台收口**：补原生 CI/候选载荷与安装恢复测试；确认替代覆盖后才切 Swift/Tauri/FFmpeg 正式链。显式 Mica、Shell API 适配属于可分离后续项，不阻塞核心正确性。
5. **独立项目**：OCR 身份、tokenizer 替换、sqlite-vec/trigram、模型裁剪、MCP 和默认快捷键分别做方案与验收，避免组合成不可回滚的大改。

当前 N1 已完成限定验收：111项套件用例通过，独立复核关闭 NM-N1R-01..03（见 `reviews/native-media-recheck.md`）。N2a 受控进程/租约完成限定验收：59/59通过，独立复核关闭 NM-N2AR-01/02（见 `reviews/n2-process-recheck.md`）。N2b-0正分Host、传输、测试三个工作树补齐受控元数据和视频声轨操作，见 `n2b-media-ops-contract.md`；原生探针确认视频不能直接走BackgroundAudioTrack，但真实MediaClip可精准输出WAV。完整采样/索引消费仍未完成。既有本机预览不覆盖、不停止，也不读取其用户交互后的设置或索引。

## 证据范围与未验证

方法体与项目文件是本地可回查证据；旧 UI harness 的 3 次渲染失败与旧 UI reviewer 超时预算不重置。此前正式 App 生产启动/UIA 证据不能替代本轮媒体验收或干净机器安装。

静态比较没有访问真实模型、私人媒体或 Vector Lake。SDK/API 文档只用于确认签名与支持边界，codec、裁剪时间语义、数值、取消及资源回收必须有真实合成探针。本轮不会自动下载 provider、模型、codec 或全局工具。
