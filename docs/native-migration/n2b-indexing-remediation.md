# N2b-1 / NM-N2B1R-01 定向修复

仅处理review-v1快照884863727409C4BBA56571350568B70D50B24E6BAC5B61C49A8BEB80AA1E6569的P1：临时artifact/task路径可能经自由格式embedding错误或native/cleanup异常进入IndexedFile.Detail。审查报告reviews/n2b-indexing-first-review.md。Factory/default接线继续阻断；既有用户索引不读写。

## 保持的合同

成功数据与采样/向量/schema/profile/指纹/取消/资源所有权不变；旧public三参和非media错误消息行为不变。真实错误不能变成空成功；failure/partial、异常依赖传播、原有效segments、无完成revision和消费后清理全部保持。

## 媒体持久化边界

优先使用闭集错误阶段/类别、原source区间/计数与HResult作为持久化诊断，不把IEmbeddingService或native返回的自由格式消息视为可信可落盘文本。单纯Replace当前artifact完整路径不足以覆盖task目录、不同斜杠/大小写、编码/截断等表现。

- EmbeddingResult.Error：保持item-failure计数及failed/partial，持久化受控embedding阶段/区间/类别；不复制任意原文。
- Embedding依赖异常：向调用方保持异常类型/传播和原InnerException供当次诊断，但顶层用于row.Detail的Message必须安全；非media原异常构造路径必须保持原有表现。
- media decode/protocol/cleanup错误：相同安全边界，不能只修embedding item而漏掉其他临时目录来源。源路径在IndexedFile.Path正常保存，不是本问题；不把input artifact/task/workspace根写入Detail/Info/Excerpt。
- cleanup与embedding同时失败：保留真实聚合和类别，不让聚合Message回带自由格式路径。OOM/StackOverflow/cancel门不能变成成功。

## 独占资产与测试

修复lane只改Indexing/IndexingService.cs和desktop/media-index-redaction-handoff.md；不改Sampler/Host/Core/defaultBackend。测试lane只改新MediaIndexing.Tests/**和desktop/media-index-redaction-tests-handoff.md。父级集成、编译和定向复核，普通回归不当独立批准。

必须构造真实输入路径的item.Error与IOException：首项failed、后项partial（保留前一个有效向量）、依赖异常仍传播。检查持久化所有segments.Excerpt、Info、Detail无artifact文件、output/task/workspace根或各斜杠/大小写/转义形式；原sourcePath字段保持正常。无需读取真实媒体/模型。额外覆盖native/cleanup错误回显task路径，防止近邻漏项；错误类别/阶段仍可解释，no revision，lease最终释放/真实隔离保留不混淆。

在当前bad binary .dev/mi-prod1/...Infrastructure.dll上留存负向repro，固定以后按实际main compiled DLL重新运行RequireIntegration全套及Indexing旧29项；Host未变，不为此重编/重跑无关Host套件。每个稳定observable最多3次执行，保留失败；完成后对新的source/evidence快照做独立定向复核。
