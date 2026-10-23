# Native CI、安装恢复与P0–P6最终验收：本轮边界

用户明确要求继续完成这三项。本轮基线 `.dev/native-migration/release-acceptance/baseline/`，HEAD/506份源码/已有dirty状态已冻结。已有N1/N2媒体bounded批准及隔离部署不是整体release批准。

## 允许的实现与禁止的自动扩张

实现新的native-only CI、native installer/recovery候选和可执行验收矩阵；保留当前Swift/Tauri/FFmpeg生产workflows/源码，不能因新增pipeline删除旧链路。没有push/外部发布、签名、真实corpus迁移、读取私人模型cache或系统配置/证书/Hyper-V变更授权。所有本地验证只用明确task根和合成数据。

CI仅新增独立workflow，不改已有dirty ci.yml/packages.yml。默认只build/test/artifact候选，permissions最小，不带secrets、不部署、不发release；动作引用须核验官方来源并固定commit，项目SDK/NuGet/Cargo锁不更新。远程Actions未执行时如实未执行，本地脚本测试不等于GitHub绿灯。可查官方公共文档/动作元数据；不下载模型/编译器或安装依赖，缺项先报告。

Installer候选与recovery测试脚本可写；本机真实安装执行仍要求编译器、精确target、恢复点和已通过静态/合成门。禁止在主环境试装未知工具或默默写Registry/快捷方式/autostart。可设计独立verification build：PrivilegesRequired=lowest、CreateUninstallRegKey=no、无快捷方式/自启、安装目录和data均为明确task根、不自动运行App。生产per-user构建与该verification模式必须分开，不把verification通过当完整生产行为通过。

升级/回滚保护binary和用户数据分离、原profile/schema5/768d Float16不变；uninstall默认保留用户settings/index/models，不能递归删除任意路径。对中断/失败注入、部分复制、文件被占用、重启需求、恢复版本验证必须有真实oracle与可回查日志，不用文件存在/manifest/模拟器代替实际installer恢复证明。

## 三路所有权

1. CI lane：新.github/workflows/native.yml、新scripts/Test-NativeCI.ps1及必要新CI helper/new tests、docs/native-migration/native-ci-handoff.md。不改旧workflow、installer、受审C#或全局配置。
2. Installer lane：新desktop/installer/**、新scripts/Build-NativeInstaller.ps1、Test-NativeInstallRecovery.ps1及必要task-only新tests、docs/native-migration/native-installer-handoff.md。不改CI、现有代码/locks、旧NSIS/Tauri、真实安装或用户数据。
3. P0–P6只读审计：新候选既有源码/报告/证据逐项核对，只返回验收matrix、阻塞/重复风险/最小输入，不修代码、不执行旧失败harness、不扫描真实模型/私人记录/整个session目录。旧UI重试预算不重开；当前完整release baseline可以给新的有界源审查建议，但不能谎称旧harness已修复或覆盖。

父级负责source合并、真实验证、独立review、授权与最终状态。拒绝validation产生权限，拒绝“为完成P6”越过主环境边界。

## 用户后续范围调整

用户明确要求“在本机测试验证即可”。因此本轮最终验收目标改为当前本机，不要求新建VM/提供ISO，不把缺少干净VM当本轮阻塞；跨版本/干净机器结果仍是未验证，不能宣称通过。本机真实installer验证限新的task安装/data根；不修改现有preview、私人数据或系统配置。已有依赖缺口仍须真实解决，不能伪称实际编译/模型测试已运行。

## 标准每用户安装授权更新

用户已明确确认标准Inno每用户安装，允许本应用开始菜单入口与卸载登记。精确身份、HKCU/菜单/新task安装及data目标、恢复与清理边界见`standard-per-user-installer-contract.md`。本授权仅替代前文对本应用相关per-user登记/菜单的禁止或待决定状态；不授权全局工具/HKLM/自启/系统环境/旧版改动。当前NM-N3R-01..05修复阶段仍不执行setup或登记；通过修复和独立门后直接推进已获批标准链路及有界本机验证，无需重复请求相同授权。

## 依赖授权与就绪更新

用户随后明确“授权下载”，覆盖本轮task目录内依赖获取与准备，不覆盖全局安装。父级在旧公开任务 `.dev/onnx-p0/models` 找到11项固定模型，全部验证对应manifest的size/SHA256后复制到本轮 `dependencies/models`，未读取私人cache、未重复下载505MB。官方Inno6.7.3安装包10,592,232字节，GitHub官方发布SHA256为9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732，Authenticode Valid。根据官方tag is-6_7_3 / commit4adf37ed7f3fd2bd11c6836ba056e3de170fbabf的setup.iss/isportable.iss确认 /PORTABLE=1，父级仅在dependencies/tools/inno-6.7.3准备编译器，过程exit0，无uninstaller，三个相关卸载键前后均不存在。不宣称无OS遥测或绝对无系统副作用。ISCC /?打印帮助返回1，保留该结果，不视为实际compile通过或portable准备失败。

证据：`.dev/native-migration/release-acceptance/dependencies/{acquisition.json,compiler-signature.json,compiler-portable-result.json,provenance/}`。当次可用CLI_HOME是已授权的既有用户目录（公开副本省略本机路径；当时.dotnet/10.0.113.dotnetFirstUseSentinel已存在）；这只是历史环境记录，不授权在读者机器上读取用户目录或新建first-use homes。下面能力缺口仅是首次探针的历史事实，不覆盖本更新。

## 已核验能力缺口（历史探针）

- 当前Windows10.0.29683、主进程elevated。管理员身份不是额外授权。
- ISCC/NSIS/actionlint/Docker/VirtualBox/VMware/Sandbox命令不可用；常见Inno6路径不存在。Hyper-V查询可用但未返回现有VM。不得据此假称具备干净机器。
- 仓库models与inference/models中，锁定manifest的11个文件均未提供，合计505137637字节。未查询私人cache、未下载。
- 缺编译器和真实模型时，推进可独立的CI/installer源码与只读验收准备，但对应真实验收保持BLOCKED，不宣称完整P0–P6完成。VM不再是本轮必要条件。

## 运行预算与SDK约束

每命令<=180秒，写入最多3分钟；同observable最多3次surfaced尝试/2次有证据修复，不重置旧UI/provider计数。只有当前任务合成根允许安装/recovery模拟；不按名字kill进程。

显式dotnet进程certificate-generation=false/globaltools-PATH=false/telemetry-optout=1，复用CLI_HOME，offline公开NuGet准确依赖、win-x64与production locked。先前cert installed提示实际store变化未知；无检查/信任/导出/删除。项目诊断临时配置已恢复，不能沿用上轮临时授权常驻禁用；若新的C#编辑需要同等控制先提精确task方案，CI/installer优先PS/YAML/Inno，不创造C#编辑副作用。

## 本轮DONE标准

分别记录：源码静态审查、本地脚本/合成测试、真实native CI执行、真实model与旧参考的parity、当前App全交互/可访问性、干净机器安装升级卸载/故障恢复、许可/完整payload。缺任何必要门均标blocked/incomplete；禁止把所有子任务结束或产物生成当P0–P6完成。独立release审查与用户cutover/发布授权分开。
