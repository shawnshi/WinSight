# 原生源码合入范围（2026-10-25）

本次是开发预览源码同步，不发布私有安装包或用户绑定，也不是完整产品／Release 验收。此前本机私有包的标准安装及自包含 GUI 启动已通过有界核验；与本轮源码构建／测试及历史合成 GUI 验收分别陈述。状态字段见 [status.json](status.json)，部署摘要见 [本机安装与启动验证](local-install-validation.md)。

## 本轮安装修复与发布声明

合入非零原生生产者提前失败、带缓冲管道 peer-close 生命周期、Inno 6.7.3 回调输出日志及 case-only LNK Target 的卷号／128-bit FileID 验证；Arguments/WorkingDirectory 仍精确，wire/owner/NoCommit/时钟合同不变。公开模板停止门保持，入口、worker、launcher 与 builder 的固定源码 SHA 同步闭合，没有发布私有路径、绑定或恢复执行器。

App 的 Windows App SDK 自包含文件、CLI 的 win-x64 RID 及 Host/OCR 的 self-contained 声明已补齐。四个工程各自 locked restore／publish exit 0，20 个依赖锁文件与基线 Git blob 一致；Core 不接受全局 RID/RuntimeFrameworkVersion 覆盖。此处只验构建边界，不新增模型、Office、完整 CLI 行为或安装运行额度。

本机真实安装使用此前私有物化包，1074 文件、登记提交指针、两条菜单链接及标准 finalization 一致；已安装每日启动器随后启动可响应主窗口。公开源码测试不冒充该私有包，也不声称本轮重新物化／安装了新的公开源码图。原始本机材料未发布，详见脱敏验证摘要。

## 纳入范围

- 已批准的 Swift/Tauri 源码退役及共享素材归档；旧 CLI 兼容不再承诺。Apache-2.0 根许可、DigUp MIT 及历史归属材料保留。
- 已独立批准的四组件源码集成：.NET 11 RC2、固定 Host BuildTools、独立 RapidOCR worker、固定 Git 提交的 Rust tokenizer。模型权重、构建缓存及本地二进制不纳入源码同步。
- 文档阶段一：DOCX 段落／表格、XLSX 工作表／表头／缓存值、PPTX 标题／正文／表格／备注；中文归一化、精确字面量／字段和来源单元 RRF。正文、OCR、页面视觉向量独立，不平均混合。
- 阶段二：PDF 有界逐页栅格／OCR 文字行几何及来源定位；前 32 页／180 秒等覆盖限制保留 partial，不承诺全篇视觉覆盖。
- 阶段三：安装版 PowerPoint 的整页 PNG 视觉索引；后台不启动 Office，手动索引显式允许并提示独占，已有 PowerPoint 时延后。
- 阶段四的有界非 GUI 来源 API：授权／SHA-256 复核、文本降级、预览材料租约、只读 Office 精确定位／自有窗口移交及晚取消修复。维护者已接受该子集及披露的 P2；它与后续 SearchPage 合成 GUI 验收分开陈述。

schema5、`winsight-native-schema5-fp16-v1` 标记、768 维 FP16 和现有模型 profile 不变。可选来源元数据不授权迁移、重建或读取真实用户索引。

## 明确不纳入

- QuickSearchWindow 重设计、所有 DPI、正式无障碍认证、物理硬件 Shift+Tab 及真实 Office GUI 全语料验收。SearchPage 的有界预览入口与对应测试源码已纳入；普通文件打开入口不变。
- `.dev/` 本地日志、失败原件、恢复现场、完整审查制品、模型、缓存、用户文件／设置／索引，以及安装包和 Release 工件。

## SearchPage GUI 的有界验收

本轮新增 `SourcePreviewPane.xaml(.cs)`、`SourcePreviewHarness.cs`，并合入 SearchPage、QuickSearchViewModel、HarnessProgram、SyntheticDesktopBackend 与 Harness 工程的已验收接线。预览按钮／结果列表 Space 打开来源文字、页面图和 OCR 行框；Escape 关闭与焦点恢复，Down 从搜索框进入结果，Enter 仍走普通打开授权。宽屏并排、窄屏覆盖，过期完成、来源漂移和异常几何不能留下错误栅格／精确打开状态。

实际自有进程在当前 200% DPI、明暗主题、1000×720／360×720 DIP 下通过视觉43/43、键盘26/26、激活2/2及生命周期6/6，13个夹具租约均释放，正常退出并确认自有 Job 结算。父代理逐张复核12张控件 PNG；4张整页图使用不透明主题测试宿主，未修改生产页面样式或拼接像素。栅格视图主动滚动时，前一个控件可以部分离开视口；独立 actions 视图核验完整标题、关闭与操作按钮，不承诺任意滚动位置所有控件同时可见。

修复包括 Harness 的 ApplicationDefinition／IXamlMetadataProvider 生成路径、x64 应用本地 Windows App SDK（.NET 仍为 framework-dependent），以及搜索框的 PreviewKeyDown 订阅。仅更换该订阅属性后，Down 的实际焦点由 SearchBox 进入 ListViewItem；生成代码与候选／工作区源码一致性均核验。历史启动、焦点 API 失败与重试停止记录原样保留，不追溯改绿。

定向键消息仅发往自有前台 HWND／子窗口／PID；反向 Tab 通过自有 UI 线程 Shift 状态保存、修改与 finally 恢复完成，不等于硬件 Shift+Tab。未调用全局 SendInput／SendKeys；运行时内部附加输入队列不透明，不构成 OS 隔离。该验收不覆盖真实 Office GUI 全语料、多 DPI、QuickSearchWindow、完整产品或安装发布。原非 GUI 的保留 P2 不因合成 GUI 用例通过而全部关闭。

## 只读 Office API 的边界

Word 仅支持有界正文段落；字段文档拒绝精确打开。Excel 要求合法工作表与 A1 矩形，公式、定义名称及当前策略识别的链接数据拒绝。复杂 Word Story／文本框／修订与 COM 正文映射、Excel queryTables／pivotCaches 等仍不属于广泛保真或安全保证。

PPT 要求空闲独占；已有 POWERPNT 时不激活或接管。导航核对原始源文件身份、ReadOnly 和目标幻灯片后移交自有窗口。归属证据为独占期间的自有 PID／frame／Application.Path，并非 PPT NativeOM 或直接 COM-HWND 绑定。Office 可能更新自身 profile、缓存或最近使用记录；它不是 OS／网络／凭证沙箱，也没有严格磁盘 quota。用户手动加入自动化实例的文稿不受技术隔离保证。

只有在强制终止前已观察到自然 exit0、物理 Job 结算、严格有界 UTF-8 管道读取及结果协议校验通过，Office 成功才不会被晚本地取消覆盖。活进程被杀、非零／未知退出、未确认结算或非法结果不升级为成功。

## 保留问题与验收缺口

维护者接受而未宣称修复的 P2 包括：receipt 先于 handoff 导致某些失败保守保留；预览取消与定位令牌的剩余耦合边界（几何异常后的栅格／租约清理已通过有界合成控件用例，不代表任意本地资源路径）；Word 映射与包策略预检边界；Excel 策略边界和历史 LSP；后端最终 CTS／semaphore 释放；外部设置文件在快照后的变更窗口。未知移交结果不能自动重放。

CLI 的 RID／锁图缺口现已由显式 win-x64 声明和本轮 locked publish 闭合；App、Host、OCR 的自包含发布也已验证。旧自动 tokenizer 供应链 validator 的失败原样保留，人工审查不改写历史记录。完整产品 GUI、多 DPI、真实 Office GUI 全语料、广泛 OCR／Office 保真、模型相关性、性能 SLA、法律分发、升级／卸载／跨重启恢复和 Release 仍缺少各自验收；本机私有安装与主窗口启动成功不关闭这些边界。

## 审查与证据范围

来源 API 最终只读独立审查 `df952a0e-2c1a-4653-828c-ffe19dae413f` 关闭稳定 P1 `SN01-R01 = SN02-R01`，对有界非 GUI 子集给出 `CONDITIONAL_ON_OWNER_ACCEPTANCE`；维护者随后接受。原审查文件 SHA-256：

`ac5f7755922d59e06a1f3a944da99ee2f511f461cd798d9d99a5bbc3dbcf1241`

父代理保留真实 process/client/files/pins 反例 2 失败／5 控制，到修复后 9/9；另有实际只读 PPT 导航 10/10。synthetic envelope 不是真实 Office handoff；早先 client-stub 5/5 不作为真实 RunAsync 边界闭合证明。PDF34、文档48、review10、后端16等均是不同范围的历史收据，不能相加为整产品验收。最终仅变更 process 文件取得 fresh LSP0，其他历史诊断和 vendor warning 仍保留。审查者只读，运行证据由父代理产生。

详细 `.dev/` 证据不随源码发布；上述哈希和审查编号用于绑定维护者保留原件，并非提供读者可访问的独立测试或审查全文。可公开回查的项目状态是本文件、[机器状态](status.json)以及同一提交的源码／测试；不据此声称读者已复现或 hosted CI 已通过。

此前不含新 GUI 的源码同步在当前主机复核：源码检查 17/17；Host、App、DocumentTests、Storage、Indexing、Inference 六个工程的 task-local locked restore／build 通过；文档48、review10、Storage27、Indexing29、Inference48 各自通过，不相加为整产品验收。Host 首次在新工作树中因缺少忽略目录的 tokenizer DLL 被既有 guard 拒绝；核验后仅复用既有匹配源码／DLL 绑定产物完成构建，未新跑 Rust validator，未把 DLL 纳入提交。Inference 使用合成 fake host，不是模型或 Office 测试。那轮同步未运行 GUI、Office、安装或旧图片38，未证明 hosted CI 或全新机器复现。本轮 GUI 的实际运行证据为上文独立范围，不把历史非 GUI 测试改写为 GUI 测试；本次发布复核也不另获 GUI、Office、模型或安装运行额度。

上一轮（2026-10-24）GUI 源码发布复核：源码检查17/17；Ui Harness、App、DocumentTests、Storage、Indexing 五个工程的 task-local locked restore/build 均为0warning/0error；文档48、review10、Storage27、Indexing29各自通过。9个GUI源码文件与此前通过视觉/键盘验收的工作区字节一致；该轮没有重跑GUI、Office、模型、CLI、旧Rust供应链validator、旧图片38或安装。既有25个JSON／归属文本的根工作区CRLF与Git规范LF差异逐项核对为仅换行差异，不产生依赖锁图或该批文件Git差异；OCR原始清单作为profile输入保持批准字节不变。LSP与模式化秘密检查的覆盖有限，不声明全IDE清零或完整秘密/法律审计。

普通 push 仅触发源码检查和原生 tokenizer 单元测试。原生候选打包／诊断工作流仍仅手动触发，已将 runner 临时路径移至初始化 step 的 GITHUB_ENV，并对齐旧触发／hosted-fixture 测试；本轮不手动派发远端 Native 打包，不运行新的安装、GUI、模型、Office 或真实用户数据操作，不创建 Release。
