# 原生源码合入范围（2026-10-24）

本次合入是开发预览源码同步，不是安装版、完整产品或 Release 验收。状态字段见 [status.json](status.json)；具体实现以同一提交中的源码、工程、锁文件和清单为准。

## 纳入范围

- 已批准的 Swift/Tauri 源码退役及共享素材归档；旧 CLI 兼容不再承诺。Apache-2.0 根许可、DigUp MIT 及历史归属材料保留。
- 已独立批准的四组件源码集成：.NET 11 RC2、固定 Host BuildTools、独立 RapidOCR worker、固定 Git 提交的 Rust tokenizer。模型权重、构建缓存及本地二进制不纳入源码同步。
- 文档阶段一：DOCX 段落／表格、XLSX 工作表／表头／缓存值、PPTX 标题／正文／表格／备注；中文归一化、精确字面量／字段和来源单元 RRF。正文、OCR、页面视觉向量独立，不平均混合。
- 阶段二：PDF 有界逐页栅格／OCR 文字行几何及来源定位；前 32 页／180 秒等覆盖限制保留 partial，不承诺全篇视觉覆盖。
- 阶段三：安装版 PowerPoint 的整页 PNG 视觉索引；后台不启动 Office，手动索引显式允许并提示独占，已有 PowerPoint 时延后。
- 阶段四的有界非 GUI 来源 API：授权／SHA-256 复核、文本降级、预览材料租约、只读 Office 精确定位／自有窗口移交及晚取消修复。维护者已接受该子集及披露的 P2；不是整个含 GUI 阶段获批。

schema5、`winsight-native-schema5-fp16-v1` 标记、768 维 FP16 和现有模型 profile 不变。可选来源元数据不授权迁移、重建或读取真实用户索引。

## 明确不纳入

- `SourcePreviewPane.xaml(.cs)`、`SourcePreviewHarness.cs` 及其未验收入口接线；这些材料保留本地。相关 QuickSearchViewModel、HarnessProgram、SyntheticDesktopBackend 使用预览工作开始前的已批准版本。保留的 PreviewGeometry 仅为后端测试共用的坐标计算代码，不提供 GUI 入口。
- 新 GUI 页面预览、OCR 叠框及 Space／Escape 交互。已有尝试在 OnLaunched 前报 `XamlParseException 0x802B000A`，根因未证实；不能由 App 编译成功推导启动或交互通过。普通文件打开入口不变。
- `.dev/` 本地日志、失败原件、恢复现场、完整审查制品、模型、缓存、用户文件／设置／索引，以及安装包和 Release 工件。

## 只读 Office API 的边界

Word 仅支持有界正文段落；字段文档拒绝精确打开。Excel 要求合法工作表与 A1 矩形，公式、定义名称及当前策略识别的链接数据拒绝。复杂 Word Story／文本框／修订与 COM 正文映射、Excel queryTables／pivotCaches 等仍不属于广泛保真或安全保证。

PPT 要求空闲独占；已有 POWERPNT 时不激活或接管。导航核对原始源文件身份、ReadOnly 和目标幻灯片后移交自有窗口。归属证据为独占期间的自有 PID／frame／Application.Path，并非 PPT NativeOM 或直接 COM-HWND 绑定。Office 可能更新自身 profile、缓存或最近使用记录；它不是 OS／网络／凭证沙箱，也没有严格磁盘 quota。用户手动加入自动化实例的文稿不受技术隔离保证。

只有在强制终止前已观察到自然 exit0、物理 Job 结算、严格有界 UTF-8 管道读取及结果协议校验通过，Office 成功才不会被晚本地取消覆盖。活进程被杀、非零／未知退出、未确认结算或非法结果不升级为成功。

## 保留问题与验收缺口

维护者接受而未宣称修复的 P2 包括：receipt 先于 handoff 导致某些失败保守保留；休眠 GUI 的几何／租约及取消耦合问题；Word 映射与包策略预检边界；Excel 策略边界和历史 LSP；后端最终 CTS／semaphore 释放；外部设置文件在快照后的变更窗口。未知移交结果不能自动重放。

CLI 的 RID／锁图问题仍未解决，本轮仅有此前静态 TFM 调整，不宣称新的 CLI restore/build 通过。旧自动 tokenizer 供应链 validator 仍失败，人工审查不改写历史失败。GUI、广泛 OCR／Office 保真和模型相关性、性能 SLA、self-contained、完整 OCR 打包、法律分发、安装／升级／卸载／恢复和 Release 仍缺少各自验收。

## 审查与证据范围

来源 API 最终只读独立审查 `df952a0e-2c1a-4653-828c-ffe19dae413f` 关闭稳定 P1 `SN01-R01 = SN02-R01`，对有界非 GUI 子集给出 `CONDITIONAL_ON_OWNER_ACCEPTANCE`；维护者随后接受。原审查文件 SHA-256：

`ac5f7755922d59e06a1f3a944da99ee2f511f461cd798d9d99a5bbc3dbcf1241`

父代理保留真实 process/client/files/pins 反例 2 失败／5 控制，到修复后 9/9；另有实际只读 PPT 导航 10/10。synthetic envelope 不是真实 Office handoff；早先 client-stub 5/5 不作为真实 RunAsync 边界闭合证明。PDF34、文档48、review10、后端16等均是不同范围的历史收据，不能相加为整产品验收。最终仅变更 process 文件取得 fresh LSP0，其他历史诊断和 vendor warning 仍保留。审查者只读，运行证据由父代理产生。

详细 `.dev/` 证据不随源码发布；上述哈希和审查编号用于绑定维护者保留原件，并非提供读者可访问的独立测试或审查全文。可公开回查的项目状态是本文件、[机器状态](status.json)以及同一提交的源码／测试；不据此声称读者已复现或 hosted CI 已通过。

本次源码集成在当前主机复核：源码检查 17/17；Host、App、DocumentTests、Storage、Indexing、Inference 六个工程的 task-local locked restore／build 通过；文档48、review10、Storage27、Indexing29、Inference48 各自通过，不相加为整产品验收。Host 首次在新工作树中因缺少忽略目录的 tokenizer DLL 被既有 guard 拒绝；核验后仅复用既有匹配源码／DLL 绑定产物完成构建，未新跑 Rust validator，未把 DLL 纳入提交。Inference 使用合成 fake host，不是模型或 Office 测试。未运行 GUI、Office、安装或旧图片38，未证明 hosted CI 或全新机器复现。

普通 push 仅触发源码检查和原生 tokenizer 单元测试。原生候选打包／诊断工作流保留手动触发；没有安装、模型下载、真实用户数据操作或创建 Release 的授权。
