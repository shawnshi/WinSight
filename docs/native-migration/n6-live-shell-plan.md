# N6：真实标准每用户安装链路的最小实施 delta

**结论：延伸现有 N5 coordinator，增加显式版本化 live backend；不复制 coordinator，不解除原 `Production` 拒绝门。** 最薄可交付物是“内部真实标准安装候选”，不是生产发行。当前无需新的用户授权决定。

状态：只读设计。未修改文件、执行命令、编译、运行测试或安装；不构成独立批准。三次实际 native CI 构建预算保持耗尽，不追加执行。

## 1. 现状与承重证据

| 当前源码 | 已核实行为及直接影响 |
|---|---|
| `docs/native-migration/reviews/n5-kernel-review-state.json` | 已集成、独立 `pass_bounded_four_id_remediation`；仅 file-only Verification。没有 live shell 执行、PS5 独立资格或发布批准。 |
| `NativeInstallCoordinator.psm1`，`Invoke-N5Kernel`、`Invoke-N5Worker`、公开入口 | 已有七操作、intent/journal 预测、receipt-before-anchor、CAS、独占锁、assignment barrier 和结构化 settlement。父入口及 worker 均拒绝 Production mutation，必须保留。 |
| 同文件，`Assert-N5Shell`、`New-N5Fact`、`Commit` | shell 是普通文件模拟证据；事实固定标记 Verification；提交锚为 ControlRoot 文件，不能直接充当真实 HKCU/menu 提交。 |
| 同文件，`Inspect` | 明确 `MetadataInspected / ControlMetadataOnly / NotAssessed`；不能作为启动或安装健康验证。 |
| `NativeInstaller.psm1:76–105,363–447,450–541` | 完整 payload 可用 Production stage 校验；Prepare/Activate/Finalize 已存在。Remove Finalize 不可逆。core 不接受 TransactionId，也没有 shell 事务。 |
| `Invoke-NativeDeployment.ps1` / `Invoke-NativeDeployWorker.ps1` | 仍是任意 `EnginePath`、落盘 request/assigned 协议，不是 N5 固定能力入口。 |
| `WinSight.Native.iss` | 当前身份随 CandidateMode 变化，禁用卸载登记/卸载器，无菜单；仅 `ssPostInstall` 调旧 dispatcher。不是标准 shell。 |
| `Build-NativeInstaller.ps1` | 尚未包含 coordinator/launcher/binding；payload 自动复制到 `{tmp}` 的时点不能满足 PrepareToInstall。 |
| `Prepare-NativeRelease.ps1` | 默认执行 `dotnet publish`；不能用于当前禁止新增产品构建的直接下一步。还使用 PS5 不具备的部分 Path API。 |
| 已完成 shell 合同及固定 Inno 快照 | `ssPostInstall` 已越过安装回滚范围；卸载没有事务回滚。沿用其证据，不再研究生命周期。 |

**可溯源断言：**现有 phase API 足以实现 payload 分阶段提交；缺的是 live 资源证明、OS 锚、真实 Inno 包装及外层进程控制，而不是第二套 payload engine。

## 2. 最小文件集与职责

### 修改

1. **`desktop/installer/NativeInstallCoordinator.psm1`**
   - 保留 `FileVerification/1` 路径及全部旧限制。
   - 对新 binding 精确分派 live backend。
   - 复用现有 intent、journal、CAS、锁、worker 和 settlement。
   - live payload 改用流式校验，不继承 verification 的 payload 字节预算。
2. **`desktop/installer/Invoke-NativeInstallCoordinator.ps1`**
   - 同步新命令参数白名单、版本化 binding/source pins。
   - 保留入口预检计时和失败 settlement。
3. **`desktop/installer/Invoke-NativeDeployment.ps1`**
   - 改成固定标准链路控制器；删除任意 engine/path 字典转发能力。
   - 管理实际 setup/uninstaller 的 owned Job、共同 deadline 和退出后现场验证。
4. **`desktop/installer/Invoke-NativeDeployWorker.ps1`**
   - 不再读取可自由指定 engine 的 request。
   - 固定 sibling/hash、闭合请求、assignment 后才运行 setup/uninstaller。
5. **`desktop/installer/WinSight.Native.iss`**
   - 固定批准身份、真正卸载器、两个当前用户菜单入口及 callbacks。
6. **`scripts/Build-NativeInstaller.ps1`**
   - 生成 live binding、提前提取清单、完整 source pins、package/build receipt。
   - 明确区分旧 verification 包与新 standard candidate。
7. **`scripts/Prepare-NativeRelease.ps1`**
   - 增加显式“复用冻结 published payload”分支；该分支不得调用 dotnet。
   - 使用 PS5 可用路径处理；现有 publish 分支不扩大执行授权。
8. **`desktop/installer/tests/Test-NativeCoordinator.ps1`**
   - 保留旧 oracle，增加 binding 分派及协议隔离回归。

### 新增

- **`desktop/installer/NativeStandardShell.psm1`**：仅 live 资源探针、HKCU 锚读写及版本化 receipt 校验；不包含第二套事务/进程控制器。
- **`desktop/installer/Start-WinSightNative.ps1`**：先验证、等待 validator 完全 settled，再启动真实 App。
- **`scripts/Test-NativeStandardInstall.ps1`**：定向 synthetic/live 验收与外层回执。
- 更新既有实施计划及本次 acceptance 状态，不重写已验证生命周期合同。

**冻结不改：**`NativeInstaller.psm1`、`Invoke-NativeInstaller.ps1`、`NativeDeploymentJob.psm1`；不涉及 C#、锁文件、系统配置或旧版。

## 3. 公开 API 与 binding/receipt delta

### 3.1 协议隔离

新 binding 使用独立、闭合 schema：

- `schema: 2`
- `backend: "InnoPerUser/1"`
- `purpose: "InternalStandardCandidate"`
- `releaseApproved: false`
- instance ID、固定 AppId、displayName
- 明确 task/install/payload/data/control 根
- source/package manifest pins、宿主限制、shell contract version
- 精确 registry view/key/value 集合、菜单路径及两个 link 规格
- 当前 package pin及可选可信 previous setup 引用

旧 `FileVerification/1` 不接受新字段或 live backend；旧 Production mutation 仍拒绝。不得用 `Mode=Production` 单字段开启 live 能力。

Payload core 的 `Mode=Production` 仅由已验证 live backend 内部选择，表示必须完整 payload，不代表发布批准。

### 3.2 根与所有权

本轮目标固定为：

` .dev/native-migration/release-acceptance/standard-install-checks/run-01/`

其下：

- `install/shell`：Inno 管理的稳定控制组件；
- `install/deployment`：严格 payload root；
- `install/unins*`：实际生成并记录身份的 Inno 资源；
- `data`：独立且卸载保留；
- `control`：receipt、intent、可信恢复组件及 setup pins。

只核验：

`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1`

以及当前用户：

`Programs\WinSight Native Preview\`

每次真实写入前重检。名称相同、单纯路径存在、caller 声称 owned 均不成立。首次存在未知资源即停止；后续须由此前可信 receipt 证明所有权。

### 3.3 公开操作

保留七操作：

`Inspect / CreateIntent / Prepare / Commit / Finalize / Recover / ResumeFinalize`

新增：

**`ValidateLaunch`**：只读验证 committed selected release、实际 payload、shell 身份和确切 DataRoot，返回可启动描述；不启动 App、不修复资源。

共同参数仍为 binding path/hash、operation、transaction ID、intent hash、expected receipt hash、共享 tick/deadline。保持逐命令闭合参数组合。

- 不公开任意 engine、脚本、注册表路径、shortcut evidence 文件或命令字典。
- `Commit` 自行读取真实 OS 资源；不接受 caller 提供“已观察 registry/menu”作为证明。
- live `Inspect` 必须明确区分 metadata inspection 与完整资源验证，不复用含糊成功状态。

### 3.4 真实资源 observation

新增独立 `LiveResourceObservation/1`，由 live backend 生成：

- binding/intent/operation、观察时间和实际 token/SID/elevation；
- 精确 HKCU view/key、允许值的名称/类型/内容；
- 两个 `.lnk` 的实际路径、字节 hash、解析出的 target、arguments、workingDirectory；
- launcher/source 文件实际大小/hash；
- uninstall command 的实际 executable、arguments；
- 实际 uninstaller/log 文件清单、大小/hash；
- 资源状态：`Present / Absent / Mismatch / ProbeError`；
- 原生错误分类，禁止把访问失败当不存在。

快捷方式通过实际 Shell Link 接口读取内容。卸载命令必须解析并匹配已记录文件和允许参数；不执行现场任意字符串。

**pending、committed 自有 registry 值必须从 shell observation 的稳定内容摘要中分离**，否则形成 receipt hash 自引用。

### 3.5 Receipt 与锚

新增 `StandardCommitReceipt/1`，至少记录：

binding/instance/AppId、四根、transaction/operation、previous receipt、manifest/release、journal及 previous/next state hashes、source/package pins、live observation hash、宿主身份、可信 previous setup 引用。

动态生成的卸载器和 link hash 无法在 CreateIntent 时预知。因此：

- intent 预先固定的是**允许资源规格和 payload 变更**；
- live receipt hash 在实际 probe 后生成，不能伪造为预先确定；
- 如旧 API 要求预知 commit receipt，必须对新协议明确取消该要求，不能复用 verification 的预测值冒充 live receipt。

Apply 提交：

1. 持久化 immutable observation/receipt；
2. 重检旧 committed 值与现场规格；
3. 更新并 flush 精确 HKCU 自有 `NativeCommittedReceiptSha256`；
4. 回读匹配后才报告 committed；
5. ControlRoot anchor 仅作辅助，不替代 HKCU 权威。

Remove 提交使用外部 ControlRoot 的版本化 removal anchor，绑定**卸载前已保留的可信 intent/previous receipt**。登记消失本身不是提交证明。

崩溃后依据锚判断提交状态；receipt 存在但未被锚选中不算提交。无法区分时 BLOCKED，不盲目 Recover。此机制不是抵御同用户恶意篡改的安全边界。

## 4. 固定入口、callbacks 与顺序

### 外层控制器

公开输入仅允许：

- 固定 binding path/hash；
- 闭合动作：Install、Upgrade、Uninstall、显式恢复/续完成；
- transaction/intent/previous receipt pins；
- 已锚定 setup 身份，而非任意 executable；
- `StartTick`、总预算，**最大 170000ms**。

从准备性工作开始统一计时；callback/worker 不重新起表。实际 Inno 进程也必须在 owned Job 内，先 assignment 再 release barrier。复用冻结 Job 模块；不请求 breakaway。

既有 N5 单命令 160000ms 上限无需放大：每次取剩余预算，并留终止/drain及外层核验余量。超时不能继续同步补偿。

### 安装/升级

1. 外层冻结 pins，重检精确资源所有权。
2. `InitializeSetup`：只读检查身份、根和 invocation。
3. `PrepareToInstall`：按固定清单提前提取；CreateIntent、Apply Prepare；重复调用只验证同一事务。
4. `ssInstall`：只检查准备门。
5. Inno 安装 shell、菜单、HKCU 登记、真正卸载器。
6. `ssPostInstall`：live probe → Activate → receipt → HKCU committed anchor。
7. setup 退出、外层证明树退出及 readers settled，再核验提交和实际资源，执行 Finalize。
8. `ssDone/DeinitializeSetup` 不承担必要提交动作。

后置失败使用明确状态/退出码及外部 receipt，不依赖 RaiseException 触发不存在的回滚。

### 卸载：必须修正旧合同的最终时点

1. `InitializeUninstall`：只读预检。
2. `usUninstall`：同步保存可信组件至 ControlRoot，创建 removal intent，Remove Prepare；失败阻止开始删除。
3. Inno 删除自己管理的 shell/menu/key。
4. `usPostUninstall`：仅记录中间 observation，**不报告完整卸载，不作最终 removal commit**。
5. 外层等待 uninstaller 及整个 owned tree 退出，再从外部控制组件验证精确资源，包括最终可能自删的 uninstaller。
6. 资源实际满足删除规格后，Remove Activate → durable removal receipt/anchor → Finalize。

若自删除未完成、需重启、存在残留或探针失败，不提交，保留 payload 与恢复材料。这样不需要修改冻结 core，也不把 `usPostUninstall` 当最终物理事实。

### 日常启动

菜单指向固定系统 Windows PowerShell 5.1 launcher，不使用 ExecutionPolicy Bypass。

launcher 调 `ValidateLaunch`，验证可信 committed release、真实 App/helper/model pins及精确：

`--DATA-DIR "<绑定的 DataRoot>"`

validator 完全 settled 后，launcher 自身启动实际 App；App **不得是短生命周期 validation Job 的后代**。不执行自动 Recover/Finalize、下载或数据扫描。

## 5. Payload 与真实 API 边界

- 819 文件基础包和 11 项 pinned public models 是来源基线，不是新完整 manifest 的豁免证明。
- 从明确冻结的 published stage及公开模型源复用，逐项校验；禁止新增产品 publish/build。
- 新包必须含真实 App、CLI、helper、全部模型及许可材料，`appOnlyPreview=false`。
- 新 backend 生成 core 接受的 schema1 manifest；不把旧 manifest 的额外字段直接交给 N5 严格字段验证。
- 采用固定块流式 hash，按块检查剩余 deadline；文件总量/字节上限依据实际完整 manifest 明确生成，不沿用 verification 64MiB 或单文件内存读取限制。
- 控制 JSON 仍保留明确大小、深度、条目上限；不足时单独提出 schema 上限变化，不能改成无界读取。

**真实限制：**冻结 core 有同步 hash/copy，不能靠 adapter 承诺每次 I/O 都可协作取消。170秒硬边界由外层 Job 终止提供，可能留下需显式恢复的部分状态。若完整 payload 在该预算内不能完成，必须停止并报告测量证据，不能自动加时、跳过校验或改 core。

仅承诺同机默认 Windows PowerShell 5.1、ASCII task 根及实际可接受路径长度；不安装 PS7、不改 long-path/SDK/证书/执行策略。实际 elevated token 如实记录。

## 6. 封闭失败与恢复矩阵

| 边界 | 结果与唯一允许后续 |
|---|---|
| 未知 key/menu/root、身份/hash 冲突 | 写入前拒绝；不接管、不模糊搜索、不删除。 |
| Prepare/复制中断 | 使用预存 intent/journal pin做 payload-only Recover。 |
| Inno 安装失败 | 探测实际回滚结果；payload 单独恢复；shell 不完整则 `ShellRepairRequired`。 |
| Activate 后、锚提交前失败 | committed 仍选 previous；显式 payload Recover，必要时 shell 修复。 |
| receipt 已写、锚状态不明 | 先读真实权威锚；无法判定则 BLOCKED，禁止盲重试。 |
| 锚已提交、Finalize 失败 | `CommittedCleanupPending`；只允许 ResumeFinalize。 |
| 卸载部分删除/自删未完成 | 不作 removal commit；payload-only Recover 或显式续卸载；不保证菜单存在。 |
| Remove 已提交、Finalize 部分删除 | 只允许可信 ResumeFinalize；不承诺完整恢复。 |
| Job/reader 未 settled、probe error | `BlockedUnsettled` 或明确 ProbeError；不报无数据/成功，不启动 App。 |

可信 previous setup 仅限同一新标准链路、同 AppId、同 task 根且有先前哈希锚的 setup。无可信 previous 时保持 `ShellRepairRequired/BLOCKED`；不运行历史 preview/Swift/Tauri、不下载未知安装器、不后台修复。

不递归删除未知内容；Inno 不加通配 `[UninstallDelete]`。payload core 发现未知文件继续拒绝；DataRoot 保留。

## 7. 实施切片与验收门

### 切片 A：协议及合成验证

先核验实际 Diff，再运行定向 PS5 synthetic cases：

- 旧 FileVerification 行为和 Production refusal 不变；
- 新 binding 未知字段/backend、混合参数全部拒绝；
- 同宿主 journal 预测 hash 与真实 core 一致；
- fake registry/link evidence 文件不能满足 live Commit；
- 动态 observation 与 receipt 无自引用；
- receipt-before-anchor 各中断点及重复请求；
- postcommit Recover 拒绝；
- Remove 部分 Finalize 仅续完成；
- deadline、Job 查询/终止失败、reader fault 的真实 settlement。

### 切片 B：真实 shell 候选

- reuse-only 完整 stage、build pins及官方 compiler 校验；
- 定向验证提前提取、真正卸载器、精确 key/link/launch args；
- 编译只产出内部候选，`releaseApproved=false`。
- 实际 Diff 及物理证据经独立审查后进入 live 门；源码通过不算安装通过。

### 切片 C：本机新 `run-01`

在现有窄化授权内，由父代理安排：

install → 精确资源 probe → validator settled → 真实 App/task DataRoot/helper → upgrade → precommit 故障恢复 → postcommit 续清理 → 真正 uninstall → 外层树退出与最终资源 probe。

每次完整尝试共享 ≤170秒；记录原生退出码、token、receipt pins、Job/readers settlement及精确资源前后状态。不得重跑耗尽的 native CI或旧 UI harness。

最后对实际 Diff 与证据独立复审。尚未实测的完整 payload 时限、PS5执行策略、最终自删除和实际 App 运行均保持未验证；不能据本设计宣布 P0–P6、生产发行或独立批准完成。