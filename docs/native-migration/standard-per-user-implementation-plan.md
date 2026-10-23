# 标准 Inno per-user 实施合同

**结论：保留现有版本化部署引擎，但将其改为受协调的分阶段事务；Inno 只拥有安装外壳、标准卸载器、登记和菜单。不能只打开 `Uninstallable`，继续在 `ssPostInstall` 调用当前 `Apply`。**

状态：只读设计完成；未实施、未编译、未运行安装或测试，不构成独立审查批准。依据父级指定的 N4 当前基线；未修改任何索引或工作树。

## 1. 现状与源码证据

| 证据 | 当前行为与直接影响 |
|---|---|
| `desktop/installer/WinSight.Native.iss:10–27` | AppId 随 Mode 变化；明确关闭卸载登记和卸载器，没有菜单。不是批准的标准安装。 |
| 同文件 `CurStepChanged` | 在 `ssPostInstall` 调用引擎；`STATEHASH` 依赖外部传入，没有持久化的日常升级/卸载信任来源。 |
| `NativeInstaller.psm1`，`Invoke-NativeTransactionCore`，约 `358–434` | Apply 复制、验证、发布版本并切换 state 后立即删除 journal；后续 Inno 失败已无法沿原 journal 撤销。 |
| 同符号 Remove 分支 | 将版本移入 `removed/`、删除 state，保留恢复 journal；这是恢复点，不是实际删除安装 payload。 |
| `Invoke-NativeRecoveryCore:275–351` | 已有严格哈希、布局、旧版本验证及 quarantine 语义，应保留，不能放宽为路径存在即可信。 |
| `Invoke-NativeDeployment.ps1` | 请求固定为 `Operation='Apply'`；当前 bounded dispatcher 尚不能服务标准卸载或分阶段恢复。 |
| `scripts/Build-NativeInstaller.ps1` | 真实 manifest 文件进入 `{tmp}` staging，由 engine 发布；Inno 当前不拥有最终 payload。 |
| `scripts/Prepare-NativeRelease.ps1` | 完整 payload 的既有结构是 `app/`，包含 CLI、`app/inference/`、`app/models/`、许可材料。不能用 model-free CI artifact 替代。 |
| `scripts/Test-NativeSearchSlice.ps1:26–35` | 已有实际 App `--DATA-DIR` 参数使用证据；不能让菜单启动回落到个人默认 data。 |

**可验收断言：** Inno 的文件日志不会自动纳入 engine 创建的 `versions/`；现有 engine 的“Applied”也不代表菜单、卸载器和登记已经成功。因此必须明确所有权和共同提交条件。

## 2. 推荐方案与取舍

### 推荐：外壳与 payload 分权，一个协调事务

- **Inno owns：** 固定启动脚本、必要控制脚本、`unins*.exe/.dat`、精确 HKCU 卸载项、两个菜单快捷方式。
- **Engine owns：** `versions/`、state、journal、copying、removed、quarantine，以及所有 manifest payload。
- **协调记录 owns：** 两侧提交进度、可信旧/新 state 哈希、恢复所需来源和资源回执。
- **Data：** 不归卸载清理所有；安装/升级不得覆盖已有 settings/index/models。

这不是两个相互独立的回滚器：**engine 在外壳成功以前不能结束可撤销阶段；Inno 卸载在 engine 预检成功以前不能开始删除外壳。**

### 不选的方案

1. **仅启用 Inno 登记、卸载器和 Icons：** 最小改动，但留下提交后丢失 journal、卸载不删除 payload、状态哈希无来源三个缺口，不能验收。
2. **全部 payload 改由 Inno 直接安装：** 可减少自定义部署职责，但要替换当前版本切换、严格所有权和恢复机制；不是此次最薄切片。

## 3. 设计合同

### 3.1 固定身份与目录

完全沿用批准合同：

- AppId：`WinSight.Native.Preview`，不能再随 Verification/Production 变化。
- 显示名/菜单目录：`WinSight Native Preview`。
- 唯一卸载键：
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1`
- 唯一菜单目录：
  `%APPDATA%\Microsoft\Windows\Start Menu\Programs\WinSight Native Preview\`
- 本轮根：
  `.dev/native-migration/release-acceptance/standard-install-checks/<run>/`

建议本轮布局：

```text
<run>/install/                 Inno {app}
    shell/                    Inno 跟踪的固定启动/控制文件
    unins*.exe/.dat            Inno 创建并跟踪
    deployment/               engine 独占根，不能混入 shell/unins 文件
<run>/data/                    显式 DataRoot，卸载保留
<run>/control/                 协调证据、恢复输入；不随 Inno 删除
```

分开 `deployment/` 是必要条件：现有 engine 的严格布局检查不接受未知 shell/uninstaller 文件。

本轮显式 `/DIR` 必须与冻结的 InstallRoot 一致；DataRoot 同样冻结，升级不得静默改根。发行默认路径可另行声明，但不得用于此次真实测试。

### 3.2 标准外壳

Inno 开启：

- `CreateAppDir=yes`
- `Uninstallable=yes`
- `CreateUninstallRegKey=yes`
- 保持 `PrivilegesRequired=lowest`、无自动启动、无自启和环境修改。
- 显式当前用户菜单常量；不依赖 elevated 父进程下的公共菜单默认值。
- 卸载快捷方式指向 Inno 提供的实际卸载器路径。
- 不用 `[UninstallDelete]` 通配清理 `deployment/` 或 DataRoot。

**首次写入前**检查精确卸载键、菜单目录、目标 install 根。存在但不能由本轮来源、哈希和身份回执证明所有权时停止；不覆盖，不“接管”。

### 3.3 稳定日常启动目标

菜单指向固定的：

```text
{app}/shell/Start-WinSightNative.ps1
```

由系统 Windows PowerShell 使用 `-NoProfile -File` 启动；不添加执行策略绕过。若当前策略阻止执行，报告真实 blocker，不改策略。

启动脚本：

1. 读取已提交安装回执，验证固定 Root/DataRoot/身份。
2. 使用回执中的 state 哈希验证 state；不能直接散列当前 state 后自认可信。
3. 解析当前 manifest release，校验启动所需 App/helper 文件。
4. 将绝对 `--data-dir` 传给实际 App，工作目录绑定已验证 release。
5. 遇到未决协调事务：仅当保存的 previous 回执及旧 release 仍验证成功时启动旧版，否则明确拒绝；不自行清理或修复。

首片将 bootstrap 内容冻结：升级必须字节相同；需要更新 bootstrap 时另走已验证的外壳更新协议。避免本次同时引入“启动器更新后无法启动旧版本”的新故障面。

### 3.4 state 信任与事务接口

在**精确卸载键内的本应用自有值**保存已提交 receipt 引用/hash，并在 `<run>/control/` 保留对应不可变回执。内容至少包括：

- 安装实例 ID、精确 Root/DataRoot/AppId；
- 当前 release、state hash；
- bootstrap/控制文件哈希；
- 菜单目标及卸载器身份回执；
- 提交事务 ID、协议版本。

这提供来源一致性和误覆盖保护，**不是抵御同用户恶意篡改的安全边界**。

新增 engine 接口建议：

```text
Invoke-NativeInstallPhase
  Operation: Apply | Remove
  Phase: Prepare | Activate | Finalize | Rollback
  TransactionId, InstallRoot, DataRoot
  ExpectedStateSha256, ExpectedJournalSha256
  StageDirectory, ManifestSha256（Apply）
```

输出：`phase / transactionId / previousStateSha256 / nextStateSha256 / journalSha256 / recoveryRequired`。

要求：

- Prepare 持久化 previous/next、预期哈希和恢复输入，再复制或移动。
- Apply Prepare 不切换 active state。
- Activate 可切换 state，但**保留 journal**。
- Finalize 仅在外壳验证及 receipt 提交后结束事务。
- Remove Prepare 保留现有可逆搬移能力；Finalize 才按 manifest 删除已验证 removed payload。
- 每阶段以事务 ID、阶段和哈希作 CAS；重复请求只能返回已完成结果或拒绝冲突，不能重新执行业务副作用。
- 不把现有无 journal 的 Apply 当作新的 Prepare。
- 现有 standalone 测试入口可保留，但标准安装不得旁路协调接口。

journal 更新前预先保存预期下一哈希/来源证据；恢复不能通过重新散列可疑现场来“补发信任”。

## 4. 安装、升级与卸载顺序

### Install / upgrade

1. **预检：** 精确资源所有权、根隔离、完整 payload、旧 state、无未决事务；保存升级前外壳/登记/菜单和可信旧 setup 恢复来源。
2. **Prepare：** 在外部 control 建立协调记录；engine 验证并准备新版本，旧 current 保持可用。
3. **Inno 外壳阶段：** 创建/核验固定 shell、标准卸载器、登记、菜单；记录实际结果。
4. **Activate：** engine 切换 state，保留 journal。
5. **提交：** 验证两侧实际文件/目标后发布新 receipt，再 Finalize。
6. 未达到共同提交条件：恢复旧 state/receipt；保留不能安全清理的证据。外壳若已受损，使用冻结的旧 setup 做显式、定向修复，不手工猜造 Inno uninstall log。

**不得把某个 Inno 回调当成断电原子提交点。** 实施前定向核验 Inno 6.7.3 的安装/卸载事件、失败回滚和 uninstall-log 行为；通过故障注入确认提交钩子与补偿顺序。

### Uninstall

1. 在 Inno 开始删除前，同步 bounded 预检：receipt/state/payload/所有权、占用和未决事务。
2. engine Remove Prepare：可逆地移到 `removed/`，保留 journal 和恢复依据。
3. 允许 Inno 删除自己跟踪的 shell、菜单、登记。
4. 在卸载后期，用已提取到临时目录的控制组件同步执行 Remove Finalize；删除仅限验证通过的 manifest 文件和空目录。
5. 保存外部完成/未决回执。Inno 最终自删除失败不能被写成完整卸载成功。

不启动脱离监督的“卸载后清理”后台进程。断电或卸载器失败后的恢复可以是**显式续卸载或旧 setup 修复**；不能承诺 Inno 已删文件可由 engine 单独恢复。DataRoot 始终保留。

## 5. 失败矩阵

| 失败位置 | 必须保持/恢复的结果 |
|---|---|
| 预检发现未知登记、菜单、文件或哈希不符 | 无业务写入，停止；不覆盖或删除未知资源。 |
| Prepare 复制失败、超时、进程中断 | 旧 receipt/current 保持有效；保留可信 journal，部分复制进入既有恢复/隔离流程。 |
| Inno 外壳失败，尚未 Activate | 旧 payload 不受影响；核验外壳是否回到旧状态，不能只相信 setup exit code。 |
| Activate 后、receipt 提交前中断 | 协调记录足以识别旧/新状态；验证后补偿到旧版，禁止盲重跑 Apply。 |
| receipt 已提交、Finalize 未完成 | 验证新 state/payload/外壳后续完成 Finalize；不因 timeout 自动回滚已提交状态。 |
| engine Remove 预检或 Prepare 失败 | 阻止 Inno 删除外壳；恢复旧 state，保留正常卸载入口。 |
| Inno 卸载部分成功或 Finalize 失败 | 标记未决卸载，保留外部恢复材料；显式续卸载/修复，不宣称卸载完成。 |
| payload/旧版文件占用 | 不按名称 kill、不自动重启；保留上一可用版本或停止卸载，输出精确 blocker。 |
| 未知内容或损坏 quarantine | 保留并停止清理，不扩大删除范围。 |

沿用 owned-job、assignment barrier、共享 native tick 和 settlement 检查。dispatcher 扩展为上述操作，不再固定 Apply。**一次 setup/uninstall 及其所有阶段、drain、补偿共用 ≤170 秒预算**；不能每个阶段重新获得 140 秒。

## 6. 精确源码 write-set

修改：

1. `desktop/installer/WinSight.Native.iss`
   标准身份/登记/菜单、预检、分阶段协调及 uninstall 生命周期。
2. `desktop/installer/NativeInstaller.psm1`
   分阶段接口、保留 journal、严格 Remove Finalize、恢复/CAS。
3. `desktop/installer/Invoke-NativeInstaller.ps1`
   暴露新阶段参数和真实错误结果。
4. `desktop/installer/Invoke-NativeDeployment.ps1`
   操作泛化、共享总 deadline、阶段结果。
5. `desktop/installer/Invoke-NativeDeployWorker.ps1`
   对应请求校验和阶段转发。
6. `scripts/Build-NativeInstaller.ps1`
   固定身份、shell include、完整 sourceHashes 和构建回执。
7. `scripts/Test-NativeInstallRecovery.ps1`
   对应协议与故障回归。
8. `docs/native-migration/native-installer-handoff.md`
   实际协议、恢复命令和剩余限制。

新增：

- `desktop/installer/NativeInstallCoordinator.psm1`
- `desktop/installer/Start-WinSightNative.ps1`
- `scripts/Test-NativeStandardInstall.ps1`

`NativeDeploymentJob.psm1` 优先原样复用。无 C#、locks、旧 workflow、旧安装器或全局配置修改。

## 7. 首个薄切片与验收

**首片就是完整标准链路，不先交付“有登记但无法可靠卸载”的半成品。**

1. **实现协议与合成 oracle。**
   覆盖 Prepare/Activate/receipt/Finalize 各边界；断言旧版可选择、未知资源拒绝、重复阶段不重复执行、Remove 真正删除已知 payload。
2. **编译一个标准外壳。**
   使用父级已准备的 task-portable Inno；完整真实 App/CLI/helper、锁定模型和许可 payload。Verification 故障能力与实际标准菜单/卸载行为同时保留，Production 禁止故障注入。
3. **首次真实安装与菜单启动。**
   记录实际 token；验证精确登记、两个快捷方式、真实 uninstaller。启动实际 App 并证明它使用 task DataRoot/当前 helper，而非只检查文件存在。
4. **升级与一次指定失败恢复。**
   先完成成功升级，再针对共同提交边界注入失败；验证上一已验证 App 实际可启动、receipt/state 一致。首次真实 blocker 即停止，不消耗剩余预算盲重试。
5. **标准卸载。**
   从实际卸载入口执行；证明本轮登记/菜单已清理、已知 payload 已删除或明确未决，settings/index/models 前后哈希不变，未知资源未删除。

## 8. 待批准、未验证与回退

- **无需再次批准：** 标准 per-user、指定 Start Menu/HKCU 身份、本机新 task 根验证、已授权模型获取。
- **没有新增产品方向选择需要用户决定。** 上述 shell/payload 分权属于落实已批准标准安装的推荐实现；不代表实施或审查已批准。
- **实施前待核验：** Inno 6.7.3 精确事件与失败语义、当前执行策略对固定 PS 启动器的兼容性、真实完整 payload/许可门。
- **回退条件：** 任一侧身份不可信、无法验证旧版、协调状态歧义、预算到期或 cleanup 未 settled，立即停止。仅按冻结回执恢复本轮资源；不触及旧 preview、私有 data 或未知文件。
- Inno AppId **不提供 package identity，也不关闭 Windows.Media.Ocr 门**；标准安装通过不能替代其余 P0–P6 验收。