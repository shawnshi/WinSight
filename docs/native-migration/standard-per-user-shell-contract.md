# 标准 shell/coordinator 首片实施合同
**结论：可以在现有授权内实施，但必须放弃“Inno 后置回调抛异常即可回滚”的假定。** Inno 6.7.3 的 `ssPostInstall` 已在自身安装回滚范围之外；卸载也没有安装式事务回滚。最小修正是：保持已审 phase engine 不动，由外部 coordinator 管理提交证据、显式补偿和续完成；外壳损坏时使用可信旧 setup 修复。
状态：只读设计；未实施、编译、运行 setup、写登记或菜单，未执行第四次 native CI，不构成实施验收或独立审查批准。
## 1. 现状与源码证据
基线沿用 `docs/native-migration/reviews/n4-reviewed-integration.json`：两个 bounded slice 已集成，46 个独立 phase case；标准 shell/coordinator 尚不存在。
### 已审 core 的实际合同
`desktop/installer/NativeInstaller.psm1`：
- `107–148`：state 使用当前 PowerShell 的 `ConvertTo-Json -Depth 30` 产生 UTF-8 字节；读取要求外部可信 `ExpectedStateSha256`。
- `150–167`：PayloadRoot 严格布局不接受 shell、uninstaller 或 coordinator 文件。
- `363–447`：Activate/Finalize 要求可信 journal hash、目标绑定及 phase 对应 state pin。
- `399–421`：Remove Finalize 删除已知 payload，允许从部分删除继续；这是不可逆清理，不是回滚点。
- `450–541`：Prepare 的确定性 schema1 journal 包含 previous/next；Apply Prepare 发布候选但不切换 state，Remove Prepare 尚不搬移 payload。
- `260–349`：现有 Recover 验证 journal、布局和资源后恢复 previous；有 quarantine 时会保留未决证据。
- `578–601`：公开接口仍是现有 `Invoke-NativeTransaction` / `Invoke-NativeRecovery`。
因此，旧实施计划中的新增 `Rollback` phase、engine TransactionId 参数及 core 修改清单已经过时，**不得照搬**。PS5-on-PS7-state canonical rejection 是已观察事实；本片仅承诺同一 Windows PowerShell 5.1 writer/reader，不声称跨宿主兼容。
### Inno 官方版本证据
父会话取得的官方文本快照位于：
`.dev/native-migration/release-acceptance/n4-current-machine/official-inno-6.7.3/`
其 `manifest.json` 记录官方 URL、SHA256 与 Git blob SHA1；版本结论以下列固定提交源码为准：
[官方固定提交](https://github.com/jrsoftware/issrc/tree/4adf37ed7f3fd2bd11c6836ba056e3de170fbabf)
| 事件/边界 | 核验结果 |
|---|---|
| `InitializeSetup` | 初始化期间、wizard 尚未创建；False 中止。不是安装事务开始或提交点。 |
| `PrepareToInstall` | 实际安装前的准备门；返回非空字符串停在准备页。可能未被调用，也可能重复调用，不能无条件重做 Prepare。 |
| `ssInstall` | `Setup.MainForm.pas:233` 附近，在 `PerformInstall` 前调用，参数为 `HandleExceptions=False`。此时还没有后者的安装资源回滚栈。 |
| Inno 文件/图标/登记 | `Setup.Install.pas:2840–2915`：创建目录、复制文件、创建图标、注册表项，随后创建/登记卸载器并保存 uninstall log。 |
| Inno rollback | 同文件 `2916–2960`：清空日志前的异常由 `PerformInstall` 回滚自身已记录资源；不包含 engine 的外部写入。 |
| `ssPostInstall` | `Setup.MainForm.pas:233–254`：`PerformInstall` 成功返回、普通 `[Run]` 后才调用，且 `SetStep(..., True)` 捕获并显示回调异常。**已经不能据此回滚 Inno 资源。** |
| `ssDone` | 同文件 `416`，成功安装退出前调用，同样 `HandleExceptions=True`。异常不撤销已保存 uninstall log、文件、登记或菜单。 |
| `DeinitializeSetup` | `Setup.MainFunc.pas:4010–4030`：退出清理阶段执行，异常被处理；也可能在未安装任何内容时执行。不能作为唯一提交/恢复入口。 |
| `InitializeUninstall` | `Setup.Uninstall.pas:682–703`：确认卸载之前执行；False/异常中止。因此这里只做预检，不提前搬移 release。 |
| `usUninstall` | 同文件 `724–732`：确认、mutex 检查之后，`PerformUninstall` 之前调用，异常向外传播，可阻止尚未开始的删除。 |
| `usPostUninstall` | 同文件 `732–767`：`PerformUninstall` 返回后调用，`HandleException=True`。此时可能已经部分删除；事件本身不是“全部删除成功”证明。 |
| `usDone` / `DeinitializeUninstall` | 同文件 `785–805`：后期通知及 finally 清理；没有恢复已删文件/登记/菜单的补偿栈。 |
对应源码：[安装资源与回滚](https://github.com/jrsoftware/issrc/blob/4adf37ed7f3fd2bd11c6836ba056e3de170fbabf/Projects/Src/Setup.Install.pas#L2751-L2969)、[安装回调](https://github.com/jrsoftware/issrc/blob/4adf37ed7f3fd2bd11c6836ba056e3de170fbabf/Projects/Src/Setup.MainForm.pas#L61-L78)、[卸载主流程](https://github.com/jrsoftware/issrc/blob/4adf37ed7f3fd2bd11c6836ba056e3de170fbabf/Projects/Src/Setup.Uninstall.pas#L682-L805)。
[官方事件帮助](https://jrsoftware.org/ishelp/topic_scriptevents.htm)为实时帮助，不冒充固定版本源码。以上初始化/准备事件的说明来自该帮助；其具体调用位置未全部逐行核验。
## 2. 推荐方案与取舍
采用三个所有权域：
```text
<run>/
  install/                   Inno {app}
    shell/                   固定 launcher、binding、控制组件
    unins*.exe/.dat           真正 Inno 卸载器及日志
    deployment/              严格 engine PayloadRoot
  data/                      独立 DataRoot，保留
  control/                   外部协调证据、可信恢复材料
```
- Inno 只管理 shell、标准卸载器、精确菜单和卸载登记。
- engine 只管理 `deployment/` 的既有 schema1 资源。
- coordinator 管理实例绑定、可信 hashes、共同提交与恢复。
- 首片升级冻结 bootstrap 字节及入口目标；不同时引入 bootstrap 自升级协议。
不选：
1. **仅打开 Uninstallable/登记/Icons**：没有可靠 state 信任来源和共同提交。
2. **让 Inno 接管全部 payload**：会替换已审版本化引擎，超出最薄切片。
3. **依赖 ssDone/卸载回调异常回滚**：与官方实现矛盾。
## 3. 设计合同
### 3.1 身份、根和所有权
严格沿用批准合同：
- AppId：`WinSight.Native.Preview`。
- 名称/菜单目录：`WinSight Native Preview`。
- 唯一卸载键：`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1`。
- 仅当前用户菜单中的启动、卸载两个入口，无桌面入口。
- 本机验收根仅为新建 `standard-install-checks/<run>/`。
固定 binding 包含 InstallRoot、PayloadRoot、DataRoot、ControlRoot、实例 ID、AppId 和协议版本。所有操作必须完全匹配；不接受从普通命令行自由换根。
精确登记键、菜单或目标资源存在但缺乏本轮可信回执时停止。未知内容、reparse 路径、身份不符或资源占用不得通过递归删除、名称匹配 kill 或覆盖解决。
### 3.2 最小公开 coordinator/dispatcher 操作
新增一个 coordinator 入口，公开操作限定为：
| 操作 | 语义 |
|---|---|
| `Inspect` | 只读核验 binding、提交锚、资源所有权及未决事务 |
| `Prepare` | 建立可信 intent；调用 engine Apply/Remove Prepare |
| `Commit` | 核验 Inno 结果，Activate，发布共同提交证据；**不调用 Finalize** |
| `Finalize` | 仅凭已验证共同提交证据执行现有 engine Finalize |
| `Recover` | 仅未共同提交时映射既有 Recover；有提交证据时拒绝 |
| `ResumeFinalize` | 显式验证已提交事务并续清理，不重新 Apply/Remove |
输入包括固定 binding、coordinator transaction ID、可信 intent hash、预期 receipt hash及共享 deadline。Transaction ID 只属于 coordinator，不能传成不存在的 engine 参数。
dispatcher 不再接受任意 engine 路径/参数字典；限定 coordinator 入口和操作。worker 验证请求、assignment barrier 后转发；结果必须包含真实退出码、phase hashes、提交状态及 settlement。
重复请求规则：
- Prepare 已完成：验证同一 intent 后返回已有结果，不再次 Prepare。
- Activate/Finalize：遵循 core 的实际幂等能力。
- journal 已删除时，只有可信 finalized receipt 与现场一致才能返回“已完成”；不能靠“文件不存在”推定成功。
- 超时不代表未提交；先核验提交证据，禁止盲重试业务操作。
### 3.3 state、journal 与 receipt 信任
**不改变 core schema1 或 serializer。**
coordinator 在 engine Prepare 前，以同一 PS5.1 宿主构造现有确定性 journal 的预期内容，保存：
- previous 原始 canonical state bytes/hash；
- next canonical state bytes/hash；
- schema1 journal 预期 hash；
- manifest/source pins；
- 初始化中断恢复所需的可信证据。
这只是适配既有协议；合成测试必须证明预测 hash 与 core 实际输出一致。不能中断后重新散列现场 journal 来补发信任。
独立 coordinator receipt 使用自己的 schema1，至少包含：
- 实例/AppId、四个固定根、operation、transaction ID；
- previous receipt hash、previous/next state hash、journal hash；
- manifest/release ID、PS writer 身份、冻结 shell/source hashes；
- 精确菜单目标/参数及卸载器实际身份；
- 预期共同提交 receipt hash和恢复来源 pins。
receipt 使用明确固定的 UTF-8 字节编码、原子发布并请求 durable flush；hash针对原始字节，不经跨宿主重序列化。
**信任锚：**
- 安装/升级：精确卸载键内单一自有值 `NativeCommittedReceiptSha256`，指向固定 ControlRoot 下按 hash 定位的不可变 receipt。先持久化 receipt，再更新并 flush 此值。
- pending intent另有自有 hash锚；不得覆盖 committed 值来表示“正在安装”。
- 卸载前将可信 intent、previous receipt、预期 removal-commit hash及恢复组件保存到外部 ControlRoot；登记删除后，显式恢复命令必须携带该预先可信的 intent hash。
- 卸载提交使用匹配预期 hash 的外部 removal receipt；不能把登记键消失当成提交证据。
这些防止误接管和意外漂移，不构成抵御同用户恶意篡改的安全边界。硬断电后若锚/receipt 不一致，进入可诊断未决状态；不宣称跨文件/注册表具备硬件级原子性。
### 3.4 稳定启动
菜单调用系统 Windows PowerShell 5.1 的固定 launcher：
```text
-NoProfile -File "<InstallRoot>\shell\Start-WinSightNative.ps1"
-Root "<InstallRoot>"
```
不添加执行策略绕过。
launcher：
1. 验证 `-Root` 与自身位置、binding、精确登记身份一致。
2. 从 committed hash 验证不可变 receipt；不信任现场 `state.current`。
3. 选择 receipt 已提交的 release，验证其可信 manifest/实际 payload。
4. 使用该 release 的真实 App/helper，显式传入 `--DATA-DIR "<DataRoot>"`，工作目录绑定该 release。
5. 未提交升级即使 live state 已 Activate，也只能选择 previous committed release；首次安装未提交则拒绝启动。
6. 不自动 Recover、Finalize、下载或扫描数据。
因此，启动不因 live state 暂时切到 next 而提前暴露未提交 release。
### 3.5 安装/升级顺序
1. `InitializeSetup`：只读身份/根预检。
2. `PrepareToInstall`：
   - 从已验证 package 提取控制组件及完整 staging；
   - 持久化 intent/恢复证据；
   - engine Apply Prepare。
   - 重复回调只验证并复用同一事务。
3. `ssInstall`：仅核验 Prepare gate，不重复部署。
4. Inno 正常安装 shell、图标、登记和真正卸载器，保存 uninstall log。
5. `ssPostInstall`：
   - 核验实际 shell、菜单、卸载器、登记和旧提交锚；
   - engine Activate；
   - 持久化 shared receipt并更新可信 committed 锚；
   - 之后才允许 Finalize。
6. `ssDone`：只确认/报告最终状态，不承担必要提交动作。
7. `DeinitializeSetup`：尽力记录，不是唯一恢复路径。
**关键：第5步失败不会让 Inno自动撤销第4步。** 未提交时按可信 intent恢复 engine previous；外壳异常使用可信旧 setup显式修复，不手工拼造 uninstall log。
post-commit Finalize失败必须报告 `CommittedCleanupPending`，保留新 committed release并给出 `ResumeFinalize` 所需 hashes；不得 Recover回旧版。通过明确的 setup自定义非零退出码和外部结果回执报告，不能仅 RaiseException。官方源码显示自定义退出码在 `DeinitializeSetup` 前取得，所以不能到该事件才决定状态。
### 3.6 标准卸载顺序
1. `InitializeUninstall`：只读验证，准备使用可信外部恢复材料；此时用户尚可能取消。
2. `usUninstall`：
   - 将已验证控制组件同步复制到独立临时目录；
   - 保存外部 intent/恢复材料；
   - engine Remove Prepare，payload仍在原位置；
   - 失败则抛出并阻止尚未开始的 Inno删除。
3. Inno删除自身跟踪的 shell、菜单、登记及卸载资源。
4. `usPostUninstall`：
   - 用临时控制组件核验精确资源删除结果；
   - 存在残留、未知资源或需重启状态时，不提交；
   - engine Remove Activate；
   - 持久化外部 shared removal receipt；
   - 再 Remove Finalize。
5. `usDone`/`DeinitializeUninstall`只报告、记录及有限清理。
不能保证“卸载任意 precommit失败后菜单始终可用”：Inno可能已部分删除菜单/外壳。最小可实现合同是：
- payload在共同提交前保持可恢复；
- 显式 Recover恢复 payload；
- 可信旧 setup修复标准外壳及入口；
- 在修复完成前报告 `ShellRepairRequired`，不能声称正常入口可用。
卸载后期异常可能只被显示，退出码不能单独证明成功。最终验收必须同时检查外部 settled receipt和精确资源实际状态；卸载器最终自删除未证实也不得报告完整清理成功。
## 4. 失败与预算
| 边界 | 必须结果 |
|---|---|
| Prepare前发现未知资源/不可信状态 | 无业务修改，停止 |
| Prepare复制/初始化中断 | committed锚不变；使用预存 journal pin和初始化证据恢复 |
| Inno安装阶段失败 | 核验其实际回滚结果；engine单独 Recover |
| Activate后、共同提交前失败 | previous仍为启动选择；恢复engine previous；必要时修复外壳 |
| 共同提交后清理失败 | 不回滚；`CommittedCleanupPending`，显式续Finalize |
| 卸载删除阶段部分成功 | payload不Final­ize；保留材料，显式恢复/修复或续卸载 |
| Remove Finalize部分删除 | 只允许可信续Finalize，禁止声称可以完整Recover |
| 未知内容、hash冲突、超时未settled | 保留证据、停止受影响分支；不扩大清理 |
一次实际 setup/uninstall尝试从首次准备性工作开始，共享 **170000ms** native-tick deadline，涵盖提取、各phase、进程/管道drain及补偿；回调或worker不得重新起表。
- dispatcher保留现有assignment barrier、owned job和descendant settlement。
- 每次子进程预算取“当前剩余”，并预留有限终止/drain时间。
- deadline后不能继续同步补偿；记录未决并停止。
- 后续人工显式恢复是新尝试，不是隐藏的自动重试。
- 真实验收的外部控制器也必须约束setup/uninstaller本身；不能仅限制PowerShell而宣称整个Inno有硬时限。
- 人工停留完成页不触发延后提交：必要工作必须在`ssPostInstall`内结束。
170秒是否容纳完整payload尚未测量；不能以已有CI filesystem性能结果推定通过。
## 5. 精确源码 write-set与验收
### 修改
- `desktop/installer/WinSight.Native.iss`
- `desktop/installer/Invoke-NativeDeployment.ps1`
- `desktop/installer/Invoke-NativeDeployWorker.ps1`
- `scripts/Build-NativeInstaller.ps1`
- `docs/native-migration/standard-per-user-implementation-plan.md`
### 新增
- `desktop/installer/NativeInstallCoordinator.psm1`
- `desktop/installer/Invoke-NativeInstallCoordinator.ps1`
- `desktop/installer/Start-WinSightNative.ps1`
- `scripts/Test-NativeStandardInstall.ps1`
**冻结不改：** `NativeInstaller.psm1`、`Invoke-NativeInstaller.ps1`、`NativeDeploymentJob.psm1`及Standalone/Recover语义。若适配时发现必须改它们，应停止并提出具体差异，不能静默修core。
构建器生成固定binding、shell/source hash清单及stage提取清单。当前`[Files]`自动复制staging发生太晚，不能直接用于Prepare；显式提前提取接口的准确路径匹配语义仍需定向核验，不能编造已验证实现。
### 实施切片
1. **合成coordinator oracle**
   - 同PS5.1 canonical预测hash与实际core一致。
   - 覆盖每个提交边界的进程终止、重复请求、unknown资源和期限耗尽。
   - 断言precommit选择previous、postcommit拒绝Recover。
   - 验证Remove部分Finalize只能续完成。
2. **真实标准shell**
   - 固定AppId，真正`unins*.exe/.dat`，精确HKCU登记及两个菜单入口。
   - 完整task payload，不使用model-free CI包替代。
   - 定向独立审查实际Diff。
3. **本机新task根验收**
   - install → 菜单真实启动并验证task DataRoot/helper。
   - upgrade → 指定precommit故障 → previous真实启动。
   - postcommit清理故障 → 显式ResumeFinalize。
   - 实际uninstaller → 删除本轮资源、保留DataRoot及未知内容。
   - 外部证据记录退出码、settlement、资源前后回执；不只检查文件存在。
## 6. 待批准、未验证与回退
- **无需新增用户决定：**上述修正仍属于已批准标准per-user/menu/uninstall范围。
- **尚未验证：**提前提取API细节、执行策略对固定PS launcher的允许情况、完整payload下170秒预算、真实断电持久性及实际setup/uninstall行为。
- **回退：**未共同提交时使用可信hash-bound Recover；外壳损坏使用可信旧setup显式修复。共同提交后仅续Finalize；Remove Finalize开始后不承诺数据恢复。
- 无后台服务、schedule、自启、UAC绕过、PATH/HKLM/MSIX或旧preview修改。
- `WinSight.Native.Preview`只是Inno AppId，不是OCR package identity。
- 已耗尽三次构建的实际native CI分支保持停止；本设计不声明完整P0–P6通过或发布批准。

## 原任务补充边界

补充合同约束：文中“可信旧 setup”仅指**本轮标准链路内、同一 `WinSight.Native.Preview` AppId、同一明确任务根、具有已验证哈希锚的前一候选 setup**。

不得使用既有 Swift/Tauri、历史 preview、其他卸载键或菜单资源。修复必须显式执行并报告真实结果；没有可信前版 setup 或哈希锚时，保留证据并进入 `ShellRepairRequired/BLOCKED`，不自动下载、运行未知安装器或建立后台恢复。