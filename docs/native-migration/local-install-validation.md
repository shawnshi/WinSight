# 本机安装与启动验证（2026-10-25）

## 结论与范围

维护者的一次**本机、每用户、未签名私有预览包**安装已完成标准提交（`Finalized`），随后经已安装的每日启动器启动自包含 GUI，检出可响应的主窗口。

这不是正式发行验收。原始安全描述符、用户路径、登记备份、失败现场、私有 binding、安装包和运行日志不随仓库发布；本文是维护者本机运行的脱敏摘要，不能替代读者在自己环境中的验收。

本次源码同步包含已实测的修复算法、x64 自包含项目声明及源级回归测试。公开安装脚本仍保留固定候选模板的停止门；仓库 checkout 不是可直接执行的通用安装器。物化一个新的私有候选仍需审核路径、重新计算原始源码 SHA 链并编译/验收，不能复用本机 binding 或把源码测试当成重新安装成功。

## 本机运行证据摘要

- 先完整备份原失败实例，再按精确菜单目录、完整程序父目录、唯一 HKCU 卸载叶子的顺序退役。原始文件和 pending 保留；没有运行归档脚本或 raw uninstaller，也没有伪造 `StandardRecovered`。
- 历史 6 次真实业务失败保留，第 7 次尝试成功。本轮只执行一次 Prepare、一次 Install，没有自动重放。
- 标准入口、原生 Setup 和 worker 均 exit 0；结果为 `NativeLiveTransactionResult/1 / Finalized`。自有活动进程为 0，双输出流 EOF、readers settled、owner 句柄关闭。
- 1074 个载荷文件逐字节匹配固定 manifest，无额外载荷文件。Manifest SHA256：`21d99776b3e3e4b2b683e8eb7690e026195b720cdfea466afdeb36432564da02`。
- state、标准提交回执和 finalization 的哈希引用一致；pending/journal 不存在。
- 当前 HKCU 叶子有 20 个带类型值、0 子键：19 个 Inno 字段加成功提交生成的 `NativeCommittedReceiptSha256`。该指针匹配真实提交回执，不再使用旧未提交现场的 19 值数量作为成功标准。
- 两条菜单 LNK 的原始文件与提交后的稳定观察一致。新鲜 `Inspect` 为 `ObservedApply`、mismatches=0；普通 Inspect 的 `mutationPermission=false`，不产生新 live 权限。其 payload health 未评估的部分由独立逐文件检查补验。
- GUI 通过安装后的、源码与绑定均核验的每日启动器启动；validator Job 已关闭，未绕过更外层 Job。主窗口检出且 Responding=true，应用按用户请求保持运行。没有注入键鼠输入。
- 启动使用新的专用 dataRoot；未读取、迁移或覆盖旧默认 settings/index。没有把工作树改动写入正在运行的安装目录。

以上安装使用 Windows x64、Windows PowerShell Desktop 5.1、Inno Setup 6.7.3 和精确 .NET 11 RC2 运行时。它不是整个公开仓库、所有设备或未来候选的安装保证。

## 合入的源码修复

1. **原生生产者提前失败可见**：等待回调连接时观察已退出的非零原生生产者，保留阶段与退出码，不等到总 deadline 才报告；原始 start/deadline 与清理预留不重置，exit 0 本身不当失败。
2. **带缓冲管道回包生命周期**：生产使用 65536-byte 缓冲，WriteAsync 完成不等于客户端消费；等待原客户端关闭再 Disconnect。EOF 仅用于生命周期收敛，不新增业务 ack、owner 或提交权限；尾随数据、异常与超时仍拒绝成功。
3. **快捷方式 Target 身份**：完全相同字符串保留原合同；仅 case-only Target 差异才核对规范无 reparse 路径及同时持有的两个读句柄，比较卷号与 128-bit FileID。Arguments、WorkingDirectory 继续区分大小写精确比较；同内容副本、不同名硬链接及大小写敏感目录中的不同文件不能获得容差。
4. **回调错误日志**：在固定 Inno 6.7.3 下使用 `ExecAndLogOutput`，保留 host、argv、等待方式和协议。
5. **自包含发布声明**：App 显式包含 Windows App SDK runtime；CLI 声明 win-x64 RID；Host/OCR host 显式 self-contained。Core/Infrastructure 不承受全局 RuntimeFrameworkVersion/RID 覆盖，依赖锁版本不变。

## 仓库级可复现检查

Windows x64 使用系统 Desktop PowerShell 5.1（不要以 PS7 替代该协议测试宿主）：

```powershell
$desktop = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
& $desktop -NoLogo -NoProfile -NonInteractive -File .\desktop\installer\tests\Test-NativeInstallFixes.ps1 `
  -OutputDirectory "$env:TEMP\WinSightInstallFixes-unique-new-directory"
```

输出目录必须是新的自有目录，失败记录不覆盖。测试只创建自有文件、LNK、进程和不同名称的合成管道；不加载 guarded 生产模块入口、不生成真实 owner/NoCommit、不写 live program/registry/menu。

可追加 `-EnableCaseSensitiveFixture`：只为该新目录中的一个空子目录启用大小写敏感属性，创建 `Case.bin`/`case.bin` 同内容不同 FileID 的反例。需要相应本机能力；显式请求而能力缺失时失败，不伪装为通过。该标志不是系统级策略更改。测试夹具保留，并非 OS 沙盒或出生隔离。

手动 Native CI 会运行不带该可选标志的组件回归；维护者本次本地验证包含该额外反例。普通 push 仍只触发只读源码检查，Native 打包工作流仅手动触发。动态 runner 路径在初始化 step 写入 `GITHUB_ENV`，不在不支持 `runner` 上下文的 job-level env 求值。

## 仍未验收

- 完整产品搜索/索引相关性、全 DPI、硬件输入和真实 Office 全语料；
- 升级、卸载、跨重启恢复、无条件安全描述符回退；
- 完整供应链/CVE/再分发许可、签名与正式消费者发行。

协作 mutex、同卷移动、文件共享限制与 reparse 检查不是对同用户恶意并发的 OS 隔离。菜单/程序/登记跨表面退役不是一个原子事务；登记比较后删除也不是原子 CAS。

`overall=blocked_pending_required_acceptance`、`productionCutover=false` 和 `releaseApproved=false` 继续有效。本次通过本机私有安装与有界 GUI 启动，不改变这些发行边界。
