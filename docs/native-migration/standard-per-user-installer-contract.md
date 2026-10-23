# 标准 Inno 每用户安装：已批准边界

## 用户决定

用户明确确认：采用标准Inno每用户安装，允许创建本应用的开始菜单入口和卸载登记。此前提案中的“不自启、不改旧版”继续生效；本机测试要求有效，不要求VM。

此授权覆盖本应用相关per-user安装/升级/恢复/卸载、其开始菜单快捷方式与HKCU卸载登记的创建、更新和定向清理；不是全局工具安装、HKLM/服务/驱动、计划任务、自启、文件关联、PATH/系统环境变量、签名/发布、私有数据或旧版变更授权。下载的编译器仍保持task-portable。

## 身份与精确目标

- 本候选使用独立稳定Inno AppId `WinSight.Native.Preview`，显示名和开始菜单目录为`WinSight Native Preview`；不复用旧Swift/Tauri产品身份。
- 预期卸载登记仅为`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1`。不按模糊名字搜索/删除其他卸载项。
- 快捷方式仅在当前用户`%APPDATA%\Microsoft\Windows\Start Menu\Programs\WinSight Native Preview\`，启动和卸载入口均指向本候选实际文件；不创建桌面图标。
- 真实验收安装与data根均置于新建`.dev/native-migration/release-acceptance/standard-install-checks/<run>/`，不覆盖已有preview。采用明确`/DIR`与显式data根，不由启动入口默认落入个人旧数据。
- 标准发行默认per-user目录可以在设计中声明，但本轮不能借测试默认路径读写个人旧settings/index/models。首次本机运行始终绑定上述task根。
- 现有同AppId登记或菜单目录若不是本轮有哈希/身份回执的资源，停止受影响分支，不覆盖/删除；不因名字相同认定归本轮所有。

这些是本轮新候选的具体命名，不修改旧产品。Inno AppId不等于MSIX/package identity，不能据此声称Windows.Media.Ocr门已通过。

## 执行顺序

1. 当前NM-N3R-01..05定向修复及独立复审先完成；本决定不自动关闭任何问题。
2. 使用修复后冻结源码为新基线，实施标准per-user链路。安装必须有可用日常启动入口和标准Inno卸载器，不能只交付外部恢复控制器。
3. 对新标准链路再做必要独立审查；源审查或编译通过不等于安装恢复通过。
4. 使用真实native App/CLI/helper和已验证锁定模型组成新的完整task payload，核验实际文件、模型profile/768d/schema5及许可范围。
5. 本机新task根真实install -> 入口启动/App基本运行 -> upgrade -> 指定故障/中断恢复 -> uninstall。每条命令、外部进程/管道及清理阶段共用明确时限，保存退出码/日志/文件与登记回执。禁止重跑旧已耗尽UI harness。

`PrivilegesRequired=lowest`只描述安装逻辑，不证明来自当前elevated父进程的实际token已降低；记录真实测试token范围，不伪称标准用户或跨版本证明。不为测试关闭UAC、更改执行策略或安全配置。

## 不变量与回滚

- 安装文件与用户data分离，旧版仍保留，生产工作流不删除。
- 日常入口必须定位当前已验证release/helper，升级失败能继续启动上一已验证版本；测试不能只检查文件存在。
- settings/index/models及schema5/768d Float16、tokenizer/model预处理/profile、CAS和grant边界不改；卸载默认保留data，不递归删除任意未知文件。
- 开始前仅检查上述精确卸载键和菜单路径的存在、目标/值元数据和哈希，不枚举个人菜单/整个注册表或读取无关值。
- 首次不存在的本轮登记/菜单需在uninstall后证明清理；升级前已有本轮版本需保存可恢复快照并验证失败恢复指向原release。
- 不把文件名/路径当所有权；恢复/清理遵循来源哈希、交易/文件身份和未知数据保留/隔离原则。
- 安装不默认自动运行App，不开启后台监控或provider下载。验收启动显式CPU和合成roots，不能扫描真实corpus。

## 结果语义

本决定是实现和上述窄化本机外部写入的授权，不是验收/发布/生产cutover批准。P0–P6仅在各必要门真实完成后才可宣布本机范围通过；当前仍有N3 P1、标准安装、UI/命令映射、旧参考/媒体边界及许可等缺口。
