# RapidOCR 页面与文字行定位合同（本地源码获批，整体未验收）

## 身份与边界

用户已授权基于官方 RapidOcrNet 4.2.0 的明确标识源码修订；本地程序集为 `RapidOcrNet.WinSight`，版本 `4.2.0-winsight.1`。上游固定提交为 `708cae2fcb88720e1d891a81b5ee3e8b2bcc139e`。仅将三个推理阶段的非取消异常从默认/空结果改为重抛；不修改识别算法。原始许可、来源、逐文件记录和补丁保留在 worker 子树。

WinML Host 不引用 RapidOCR/Skia/ORT 1.29 包。它只启动固定 `ocr/WinSight.OcrHost.exe`，使用既有 suspended-born Job、限额管道、取消与物理进程树结算。未知结算不得删任务材料。worker 的 ORT 与 WinML ORT 不放在同一运行目录；不是 OS/凭证沙箱。

## 输入、输出与提取身份

原有图片大小、40MP、八方向 EXIF、白名单与隐私边界不变。Host 先解码、方向归一并缩到单边2600，再写任务内 PNG，向 worker 发送 protocol 2 的有界请求（当前文字行几何协议；旧protocol1证据保留）。模型/字典四项均在固定 manifest 中；CPU ORT 会话最多配置4线程，使用 PPOCRv6 选项。没有启动下载或 Windows OCR 回退。

worker 接口见 [组件说明](../../inference/WinSight.OcrHost/README.md)。父层内部期限25秒涵盖 PNG 准备；外层较早取消/期限优先，不重置外层时钟。模型/输入/推理错误明确失败；合法空白成功；8000 UTF-16 字符上限带显式截断。

初次升级的 `inspect-image` 为protocol2；当前页面／几何候选升为 **protocol3**，新增必需的文本行矩形／UTF-16文字范围／几何截断标记，并保留必需 `ocrEngine`、`ocrEngineVersion`、`ocrModelProfile`。Desktop 严格比对身份和固定中英语言组合；旧 protocol 1/2 的外层响应或错误身份不能伪装成新引擎。此变更不修改 SQLite schema5/native marker、嵌入维数或原11个嵌入权重。

`OcrIdentity.cs` 和同一嵌入 manifest 被源码链接到 Host/Infrastructure；提取修订为 `native-image-exif-rapidocr-v3:<engineVersion>:<manifestSHA256>`（当前几何候选；此前v2证据保留）。图片 info 同时记录实际经过校验的 engine/version/profile。旧 v1/v2 信息在正常授权重扫时不再当作当前修订；未访问或迁移真实索引。

## 页面／文字行几何阶段的本地源码批准

独立只读审查 `97becc9f-044f-45c6-850f-ad38a85ae81d` 给出 `APPROVE_LOCAL_DOCUMENT_PAGES_SOURCE`，无新问题；审查者没有执行测试。父代理当前原生链路34/34包含真实Windows PDF渲染、纯栅格及已有文字层扫描件、RapidOCR文字行框、SQLite位置和独立通道；另有文档48、首阶段反例10、Storage27、Indexing29及启动生命周期5项回归。它们可能重叠，不相加为整产品验收。

仅一次自制扫描PDF真实CPU模型贯通约8.64秒，独立768维图像／OCR文本向量与页码文字行框入库，关键词和语义各返回一条；小样本阈值回退不代表准确率。该模型证据早于最后的render-only512MiB Job上限和已有文字层扫描判定增强，后者由当前34项原生用例覆盖，未重复模型运行。旧38项图片矩阵没有重跑，不能移作新协议验收。

当前默认仅前32页、1600像素长边、共享180秒页面预算；未覆盖页面／截断／错误明确partial，正文文字可覆盖后续页。OCR为归一化文字行矩形，不是逐字／逐词框或PDF点坐标。GUI叠加预览、Office精确跳转、PPT整页视觉、广泛OCR相关性、恶意PDF内存耗尽压力测试及安装／Release均未获验收。

## 既有组件验收记录（原协议快照）

这是本地源码候选，不是已部署功能。组件初始快照22项通过；两个集成快照各37/38，密集图超时。第三轮22/38：4线程密集图16.17秒返回，但测试使用旧 Infrastructure 二进制，身份校验阻止进一步断言。原失败均保留。

用户随后明确授权仅追加一轮图片验收。第四轮使用全新任务目录和实际重建程序集，**38/38通过**，总耗时41.45秒；密集图16.28秒，保留8000 UTF-16字符并明确标为partial。四处manifest身份一致；原断言、25秒内部及30秒密集图外部期限未变，结束时无本轮遗留进程或OCR临时目录。前述失败不追溯改绿。这是单轮CPU4受控夹具回归，不是广泛OCR准确率或时延SLA。

tokenizer 后续已按官方v0.23.3提交（实际Rust0.23.2）合入，并通过人工依赖集成审查及实际C# ABI／CPU文本-图片-音频模型验证；embedding profile相应更新，OCR profile未变，没有第五轮图片验收。四项组件的本地源码／集成独立复核已通过（e10f8ce4-81f9-4ac1-bcca-826583d2929f），并确认启动未结算时的资源保留缺陷已关闭；新self-contained/GUI/托管CI/完整打包/安装/Release验收仍未完成。
