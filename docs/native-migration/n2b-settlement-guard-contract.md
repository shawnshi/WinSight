# NM-N2B2-LIFECYCLE-01：关闭故障的状态机准入屏障

原N2b-runtime-contract.md要求真实retention/poison不转为Paused假收敛，但其初始write-set禁止Controller变更。实际IndexingController只等待逻辑callback退出，finally无条件idle完成；await media.Dispose并保存aggregate仍不足。该跨所有权缺口由主代理在原已授权Native/Pause物理收敛目标内单独处理，不扩大到真实corpus/部署或修改Core enum。

当前已集成runtime候选**仍BLOCKED**，原件/恢复点在n2b-runtime/settlement-guard/baseline/。只允许修改Runtime/IndexingController.cs与DesktopBackend.cs中的关闭故障报告调用；另增WinSight.IndexingSettlement.Tests，原public API/一般业务失败、暂停/恢复/设置CAS/背景授权保持。

## 最小语义

新增internal `IndexingController.ReportUnsettled(Exception cause)`：仅在本run的NativeMediaProcessClient.DisposeAsync失败时报告，保存第一个cause，状态固定Pausing，Failure为受控文字，明确需要进程重启；不从任意普通decode/embedding业务失败自动推导未收敛。

sticky故障不由idle/暂停/恢复/设置切换清除，不提供reset后门。HasWork在未收敛时保持true，阻止SaveSettings误以为空闲；新的Run在准入及workGate取得后都检查fault，故已排队任务也不得运行。Paused只在逻辑排空+取消任务结束且无sticky资源故障时发布。故障后Pause/Resume/Stop真实抛错，状态不能改Paused/Running，Failure不能清空；不制造永不完成的task伪装仍在工作。

Stop仍须执行既有cancel/退休cleanup、保持bounded等待，不因早抛fault泄漏epoch取消源。terminal条件下未取消的generation在transition/shutdown时仍要cancel；普通callback异常不报告ReportUnsettled时，正常idle/暂停/恢复语义不变。

DesktopBackend仅在media client关闭catch内先报告故障，再原样传播或保留indexing+cleanup聚合。非code每run所有权、source/lease、原safe错误持久化边界不动；Code/search/status不因该guard新增helper。该guard保护当前backend/controller实例；不自动重建backend、恢复未知native资源或声称OS沙箱。进程级retained资源/实际OS拒绝终止仍按原N2保留未知，不“清理”未知资产。

## 验证

- 精确重复原逻辑缺陷：回调关闭错误、并发pause、idle完成后仍不得Paused；resume/newrun/已排队run拒绝，HasWork/Failure粘性。
- 保持原inner cause、首次故障不被后续覆盖；无raw路径写入Failure；Stop bounded抛错但仍取消generation/退休资源。
- 没有ReportUnsettled的普通业务错误可正常pause/resume；无工作正常transition与既有取消/超时回归仍通过。
- 控制器级新suite使用真实Controller源码，不stub；故障注入是确定性生命周期标记，不冒充实际OS拒绝终止。public-backend正常Helper/Pause/Dispose仍由独立backend suite完成，guard-callsite与故障公开行为需独立review。
- 不用无限sleep/命名kill，命令≤180s；SDK进程证书抑制、离线/locked规则沿用原合同。状态fix+factory整体通过独立审查之前不激活live/private数据或部署。
