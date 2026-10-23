# N2b-2：DesktopBackend factory与任务生命周期

前置：N2b-1已独立限定通过，NM-N2B1R-01关闭，reviews/n2b-indexing-recheck.md；102+29通过。这不是运行中App/真实corpus的激活许可。本切片授权范围是候选代码接线、明确task数据根的合成验证及独立审查；不部署、不启动既有预览、不读/写既有设置/index/模型缓存。

## 最小实现

保持DesktopOptions/Core/设置schema/public ABI及原模型client/provider配置不变，只在DesktopBackend的非code IndexAsync任务内接已通过的NativeMediaSamplingService与IndexingService internal四参构造。code路径保留public三参，无native媒体helper构造或launch。

优先**每次索引run拥有一个NativeMediaProcessClient**：在lifecycle锁内捕获与本次model/approved request一致的host路径；settings.InferenceExecutable或原applicationDirectory/inference/WinSight.InferenceHost.exe，不另找PATH/fallback host。workspace为明确dataDirectory下固定media-tasks子目录，继续被已有data/index exclusions覆盖，不将workspace写进Settings、Info/Detail/Excerpt。

NativeMediaProcessClient要求workspace已存在：允许仅在非code IndexAsync callback内准备该空目录。先MediaTaskFiles.LocalPath限制本地目标，再用Extraction.SafeFile.Open(dataDirectory,true)保护data根及祖先，持pin执行Directory.CreateDirectory(media-tasks)，再SafeFile验证新/既有workspace正规非reparse/cloud。code/search/status/backend初始化不创建；不跟随未知junction扩张写入、不移除既有资产。这是任务目录准备，不是媒体读取/codec初始化。

client构造不等于已读取媒体/下载模型；只有已授权media candidate/有可用capability才经既有session触发受控操作。不得加入parent WinRT、Process.Start旁路、provider/model下载或公共任意command。沿用N2默认deadline/Job/pins/lease；不改N1/N2a/N2b-0/N2b-1源码合同、受控错误或采样策略。

run结束（含normal/error/pause/cancel/shutdown）必须await native client.Dispose/退出确认之后，才能让IndexingController认为work结束。不要全局共享新的media队列/服务/状态或新后台loop。已转交消费者的lease仍由session/embedding await结束释放；client关闭不早删。

索引原异常与media client关闭异常同时发生时保留聚合/原cause，不让finally覆盖原异常；真实retention/poison不能转成成功/Paused假收敛。原始InnerException仅当前调用诊断，持久化诊断仍遵守P1边界。

现有SaveSettings拒绝active work，不顺带改变为自动取消；成功切换后下次run捕获新host/workspace，旧per-run helper已结束。保留设置CAS、watcher启停、SetUp/BackgroundIndexingEnabled授权、root/skip/data/index拒绝、shutdown/pause限时及semantic search行为。不能因resume新增用户从未启用的后台监控。

## 独占write-set

- runtime lane：仅desktop/src/WinSight.Infrastructure/Runtime/DesktopBackend.cs，以及desktop/media-runtime-handoff.md。不改Indexing/Media/Host/Core/Settings/IndexController/BackgroundCoordinator/依赖。
- test lane：仅新desktop/tests/WinSight.MediaBackend.Tests/**与desktop/media-backend-tests-handoff.md。可建test-only composite fake host（model stdio+native CLI）；全部配置、假manifest/模型占位、SQLite新db、媒体在task根。不得读取真实模型/个人文件。
- parent：集成、必要旧linked-source测试项目引用适配（不改旧断言/包版本）、主树构建/测试、独立review和状态。

## 验收必要项

使用实际DesktopBackend.CreateAsync与显式task DesktopOptions/DataDirectory；真实task SQLite仓库，fake768 embedding/stdin profile，不用真实模型。不要只测internal候选IndexingService，因为本切片检验默认backendfactory接线。

1. public backend document-mode audio/video索引实际产生768向量/原locator/完成revision；code-mode不启动media；纯文档保持原行为。helper与model使用相同捕获host路径，workspace位于task data/media-tasks且被排除。
2. 缺host/codec/capability/decoder或embedding故障：真实failed/partial/异常，无filename-only伪向量/无声轨伪done，无临时path持久化。已知hasAudio但失败不变false。
3. Pause在helper正在运行或embedding阻塞时：确定性握手后取消、记录精确PID+creation和Job后代确已退、source/task pins释放或真实retention错误。Paused只在run完整关闭后；resume仅尊重原背景授权，原settings未被偷改。
4. 外部cancel/Dispose shutdown：owned child物理收敛和任务清理；不按名字kill。持有embedding的sample不能提前清理；失败+cleanup聚合不丢cause。
5. SaveSettings在active work明确拒绝（现有语义），CAS/Background开关不受影响；work结束后新host用于下次run，无旧helper泄漏。背景开启只针对显式SetUp与授权合成roots，无隐含监控新根。
6. 不因初始化backend/search/status或code run扫描真实media/下载；未经授权root/skip/data/index不得触发media helper。禁用背景的host不自建watcher。status/source/SQLite内容可回查。

至少一条可选实际已批准Native Host的model-free decoding路径（若composite wrapper启动真实host必须在真实N2 Job后代中，限定synthetic input；外部provider/model一律禁止）。未提供该host时明确pending；主树RequireIntegration不能把缺组件当pass。

新test-only wrapper直接IPC必须核对真实InferenceHostClient/FakeHost协议及manifest验证，不能发明stdio。编译实际Infrastructure和新项目；旧Runtime/Pause/Indexing与App编译按相关性回归，不重开旧UI harness/耗尽review。所有剩余codec/真实model/visual/install及证书状态未知保留，不把factory通过等同release。

## SDK环境约束

任何dotnet命令先在进程作用域设DOTNET_GENERATE_ASPNET_CERTIFICATE=false、DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false、DOTNET_CLI_TELEMETRY_OPTOUT=1；使用既有CLI_HOME（不要新建first-use home），不改持久环境。离线显式公开NuGet exact版本、win-x64/locked、task packages/artifacts。若又出现certificate installed提示，停止该验证分支并报告；不检查/信任/导出/删除证书。历史状态未知见reviews/n2b-remediation-environment-anomaly.md。
