# P0–P6 当前机器验收矩阵审计

## 结论与审查范围

**整体仍为 BLOCKED，不能宣布 P0–P6 完成、生产切换或发布批准。** 当前机器可以补齐相当一部分验证；**缺少 VM/ISO 不是本轮阻塞**。干净机器、跨 Windows 版本及生产安装行为仍须标为未验证。

本报告以父工作树 `docs/native-migration/release-acceptance-contract.md` 的“本机测试”修订为准。`plan.md`、旧发布脚本及契约末尾残留的 clean-machine 表述，不能覆盖该修订。

本次只读核查了计划、47 项命令清单、发布/测试脚本、许可证声明、部分既有独立批准原件和最新部署报告。达到读取软预算后停止：

- 未执行 SDK、测试、UI harness、下载、安装或诊断。
- 未修改源码、状态、Git index、既有预览或任何系统配置。
- 未逐体复核全部当前 C#，未复算源码或 payload 哈希。
- 以下“已执行”均明确引用既有执行者的报告，不是本审计重执行。
- 新 CI/installer 在本次目录检查时尚未出现在父工作树；**本报告不批准其随后交付的实现**。

基线：`.dev/native-migration/release-acceptance/implementation-base/manifest.json`，记录时间 `2026-10-19T23:14:18.332674+00:00`，记录 HEAD `7db9537712995d14e50e0e44baa00f721b98f1c6`。这是冻结清单中的记录，不是本次 Git 查询结果。

## 一、逐门验收矩阵

| 门 | 已有证据及其有效范围 | 仍须提供的证据／真实阻塞 |
|---|---|---|
| **P0 基线** | 指定 implementation-base 包含 `manifest.json`、`source/`、`status-before.txt`；`plan.md` 明确保护行为与文件级恢复；`command-inventory.json` 登记 47 项命令。 | 基线资产已具备，但本次未逐文件复算。父级整合新 CI/installer 后须绑定最终 write-set 与哈希，证明 inherited dirty/staged deletions 未被覆盖。命令清单的 `nativeParity` 仍全部是 `pending`，不能据文件存在宣布行为映射完成。 |
| **P1 Native shell** | 最新部署报告记录 App Release/self-contained 发布和实际启动成功；真实 Pause/Resume、两个 viewport 的有限 UIA 检查 **5/5**。 | 单实例及重复激活、托盘点击/菜单、热键冲突与释放、hide/close/exit 区分、焦点与键盘、DPI/theme、屏幕阅读器等未获完整验收。App 编译、启动和上述 5 项不替代这些行为。旧 harness 与旧 UI 审查重试预算不得重开。 |
| **P2 Search slice** | `ui06-final-state.json` 记载已发布预览通过 app-only smoke 和非空 fake-host search slice；最终 equal-root 修补相关测试 **15/0**，但未获最终独立复核。 | 必须把当前最终 payload 的 synthetic index → 查询 → 结果 → open/reveal/copy/drag 链路绑到真实 App；覆盖空结果与错误区别、取消、乱序响应、撤权和嵌套根。fake embedding 不是模型相关性证明。真实模型缺失阻塞模型驱动验收。 |
| **P3 Engine parity** | `backend-recheck.md` 对 **NM-BR-01..10** bounded pass；记录 storage/indexing/inference/runtime 为 **20/29/48/19**，不是本次重跑。N2b-2 源码/证据 bounded pass；public-backend **29/0**、settlement **8/0**、bounded-reader **5/0**。 | 未建立完整、当前版本绑定的旧参考差分证据；缺真实模型。29 项后端用例使用 fake768 embedding，真实 WAV 只是 model-free 解码。全格式、真实音视频语义、图片/文档/code 模型结果、排序与 locator parity 不能由这些测试推定。 |
| **P4 Integration** | 后端 bounded review 涵盖授权根、删除安全、Git filtering、异常与资源清理。UI 状态记录 stale settings 恢复撤权问题已在 v3 partial review 与 CAS 物理测试中解决。最新 App 实际 pause/resume 已验证有限链路。 | 当前 payload 的配置原子保存/重载、并发 CAS、watcher 收敛、schedule 边界、文件动作、clipboard/drag、editor 集成仍需对应证据。真实 Registry/autostart 写入不在当前边界内；合成/静态 startup 检查不得冒充真实开机行为。 |
| **P5 Retirement** | 最新部署报告记录候选 payload 不含 Swift/Tauri/test/FakeHost；旧生产链保留符合本轮契约。 | 全 parity、完整 UI 与最终独立 release 审查尚未完成；旧 CLI/JSONL 外部调用方尚无完成盘点证据。本轮明确禁止改旧链路，因此不得以删除旧文件让 P5“变绿”。最多交付 retirement readiness，不能声称已退休。 |
| **P6 Distribution** | 最新部署报告记录 App/CLI/Host 发布退出 0；实际 CoreLib/CoreCLR/WinUI 从候选目录加载；819 文件、434,860,008 字节 manifest 检查通过；生产五个 lock 发布前后相同。 | 该 payload **没有真实模型**，属于预览。完整许可证包、当前依赖审计、安装器编译、真实安装/升级/卸载与故障恢复、远端 native CI 回执均未闭环。编译器和模型是确认的资源缺口；VM 不是。verification 安装模式不能代表生产 per-user 安装。 |

### 既有批准不能扩张

- `docs/native-migration/reviews/backend-recheck.md`：只批准 NM-BR-01..10，明确不批准 P3/P5/P6/UI。
- `docs/native-migration/reviews/n2b-runtime-review.md`：只批准 runtime factory + settlement guard。记录的审查 manifest SHA-256：

  `A42BAB99A585587CC89F19F583B9E6C541D1AE842F12825B7A6EEDA0E69F7141`

  本次未复算。该审查没有证明真实模型、全 codec、真实 OS 拒绝终止、安装或视觉验收。
- `desktop/README.md` 记载 N1 111 项、N2b-1 102 项及其 bounded 批准；本次未另行读取这两组全部原始审查与执行回执，不扩大其批准范围。
- `docs/native-migration/reviews/ui06-final-state.json` 明确 `independentApproval=false`。已有定向测试通过不等于最终 UI 批准。

## 二、47 项命令：必须补的是行为映射，不是重建 Tauri API

`docs/native-migration/command-inventory.json` 的所有条目仍标记 pending。这说明**验收账目未闭环**，不证明所有功能均未实现。

父级应为每项登记“当前 native 入口 → 保留的语义 → 对应执行证据或明确未完成”，至少覆盖：

| 类别 | 必须对应的行为 |
|---|---|
| UI-SEARCH | `ask`、`debug_query`、`debug_report`、`window_info` |
| UI-SETUP | `estimate`、`setup_info`、`pick_folders`、`apply_setup_folders`、`finish_setup` |
| SYS-FILES | `open_path`、`open_code`、`reveal`、`copy_file`、`drag_out` |
| SYS-WINDOW | `hide_panel`、`panel_height`、`panel_area`、`open_window`、`open_settings`、`quit_app`、`close_window`、`window_resized` |
| CONFIG | `settings_info`、`set_open_at_login`、`set_center_panel`、`set_processor_only`、`set_hotkey`、`hold_hotkey`、`set_tray_click`、`set_update_checks` |
| INDEX | `app_state`、`choose_folders`、`index_now`、`pause_indexing`、`remove_folder`、`set_skipped`、`set_skip_types`、`set_index_hours` |
| CODE | `code_setup_info`、`set_code_folders`、`choose_code_folders`、`remove_code_folder`、`turn_off_code`、`set_code_editor` |
| RELEASE | `check_for_updates`、`offer_update`、`open_licenses` |

允许 native UI 重设计内部调用方式；不能默默删除受保护行为。无当前 UI caller 的条目，也不能未经外部调用盘点就认定可删。

## 三、模型与差分参考：资源缺口及准确入口

### 已确认缺口

最新父级契约记录：仓库 `models` 和 `inference/models` 没有锁定 manifest 要求的 **11 个文件，共 505,137,637 字节**；未查询私人 cache、未下载。

这是真实模型验收和完整 payload 的阻塞，不是一般性的“测试还没跑”。

### 已定位的准确仓库入口

以下路径均相对仓库根目录（公开副本省略原本机绝对路径；本节是历史审计，不是当前安装批准）：

| 入口 | 用途及本次确认范围 |
|---|---|
| `inference/WinSight.InferenceHost/model-manifest.json` | 模型字节身份的指定权威入口。`Prepare-NativeRelease.ps1` 明确读取 `files[].path/size/sha256` 并逐文件校验。 |
| `scripts/onnx-p0/acquire_models.py` | 已确认存在的公共模型获取脚本；本次未读取正文，不批准直接执行。 |
| `scripts/onnx-p0/prepare_text_fixture.py` | 已确认存在的文本参考准备入口；本次未核验其输入、依赖和输出语义。 |
| `scripts/onnx-p0/test_probe.py` | 已确认存在的 probe 测试入口；不能由名称推定其覆盖真实模型 parity。 |
| `evals/synthetic.json`、`evals/multilingual.json`、`evals/code.json` | 已确认存在的候选评测 fixture；未读取正文，不认定为 gold vectors 或完整差分 oracle。 |
| `.dev/native-migration/p0/source/`、`implementation-base/source/` | 分别承担受保护旧契约和本轮候选恢复来源；不能拿未绑定版本的旧执行结果替代差分参考。 |

**本次没有读取 manifest 正文及上述参考脚本，故不编造模型文件名、公共仓库 URL、revision 或参考向量位置。** 最小补证是定向读取这些已知文件，不是扫描 cache、用户目录或 session 历史。

### 真实 parity 的最小输入合同

父级需要：

1. 11 个精确 artifact 的获准 task-root 副本及 manifest 校验回执；下载或提供资源另按现有边界处理。
2. 精确旧参考版本、可运行参考或可信既有输出；输出必须绑定 fixture、模型身份、prompt 和生成环境。
3. 共同输入下验证协议 1、原始 task-prompt、profile identity、768 维有限且归一化向量，以及 schema5/768 Float16 存储。
4. 比较 search 排序、code/page/media locator、文件状态、失败语义；不只比较维数或“返回非空”。
5. 数值容差和排序判据必须来自既有合同或有依据的测量，不能本轮临时编造。

旧参考是否可运行、参考输出是否已存在，**本次未确认**；不能把它们报告为已确认缺失。

## 四、UI：绕开旧失败路径，但不能绕过验收

最新可用部署来源：

`docs/native-migration/n2b-deployment-verification.md`

对应 stage：

`.dev/native-migration/deployment-checks/n2b-native-20261019/`

报告指向：

- `verification/published.smoke.result.json`
- `verification/interactive-ui.json`
- `verification/deployed-helper.json`
- `logs/{App,Cli,Host}.publish.log`

已有证据只覆盖真实启动、pause/resume、两个 viewport 和合成 WAV metadata。不能称完整 keyboard、屏幕阅读器、DPI/theme 或像素验收。

### 当前机器最小补测集合

使用**新的 task data 根和明确的生产 App PID/window subtree**，不覆盖现有预览，不重跑旧 `WinSight.Ui.Harness`：

1. **生命周期**：第二实例激活、关闭隐藏、显式退出、托盘和热键注册释放。先核验实例/热键隔离，不得干扰已有预览。
2. **查询与文件动作**：键盘查询、结果选择、打开/定位/复制/拖出；撤权后拒绝，独立选定 nested/equal root 不误拒；取消和乱序响应不呈现过期结果。
3. **设置与授权**：保存重载、错误可见、stale revision 冲突不恢复旧授权；code 与 document 选择域分离。
4. **可访问性**：Tab 顺序、可见焦点、Escape/Enter、控件 accessible name、状态播报；窄窗口/缩放/DPI/theme 与高对比度的相关状态。
5. **错误与关闭**：缺模型、helper 失败、暂停未收敛时不能显示成功；退出释放 task-owned 资源。

这些可以采取新的有界生产 App 验收用例或人工操作回执；不是修复或重新计数旧 harness。最终 equality patch 的独立接受仍不能由本报告自批。

## 五、CI、payload、许可与 installer 审计发现

### CI：新实现尚未在本次检查范围内落地

检查时 `.github/workflows/` 仅有既存 `ci.yml`、`packages.yml`；没有观察到 `native.yml`、`Test-NativeCI.ps1`。若父级随后整合，必须另绑定最终文件和实际验证，不能继承本报告批准。

新 CI 最少需证明：

- 独立 workflow，不改已有 dirty workflows；最小权限、无 secrets、无部署/发布。
- action 来自核验的官方来源并固定 commit；本次未进行公共 action 元数据核验。
- 每个 dotnet 进程具备三项环境值，复用获准 CLI_HOME；win-x64、production locks 和准确公开 cache 不漂移。
- step 失败即停止；超时受控，不能通过换 CLI 或自动 fallback 重试。
- artifact 排除 FakeHost/tests/用户数据；标明 app-only 或 full payload，不能统一标为 release-ready。
- 远端运行必须有 GitHub run/commit/artifact 回执。本地 PowerShell 测试不能替代远端绿灯；没有 push/远端执行授权时保持未执行。

### 现有脚本不能直接当本轮完整 runner

源码事实：

- `scripts/Test-NativeDesktop.ps1` 只串联五个既有 synthetic suites及可选 App build；不包含新 native media/backend/settlement 全部验收。
- 它把证据写入固定 `.dev/native-migration/verification`，可能覆盖既有回执。
- 它和 `Prepare-NativeRelease.ps1` 自身都不设置本轮要求的三项 dotnet 环境值或命令超时。
- `Prepare-NativeRelease.ps1` 的 publish 调用没有显式 `--no-restore`；仅凭脚本不能证明当前 offline/locked 执行合同，仍需核验项目属性和受控调用。
- `-AppOnlyPreview` 路径不发布 host/复制模型；最新包含 model-free helper 的部署报告是另一份实际装配证据，不能推定这个开关自动产生相同内容。

因此应由新 task-scoped runner 明确控制环境、恢复与日志边界，而不是盲目重跑旧总脚本。

### 许可与完整 payload：明确未完成

`desktop/THIRD-PARTY-NOTICES.txt` 明文标为 development candidate，承认其不是 P6 许可批准；目前可见只提供 DigUp MIT 文本及 PdfPig 来源说明。

必须对**最终实际 payload**收集、核对并随包提供：

- NuGet、.NET/App SDK runtime、native tokenizer 及传递依赖许可；
- 模型、字典和实际媒体库许可/notice；
- 对应版本、来源、完整性和分发条件；
- 当前依赖漏洞/签名检查回执及明确范围。

README 所述 metadata audit 不等于签名验证、漏洞检查或分发许可。模型 SHA 正确也不产生模型分发许可。

完整 payload 还须核对 host、tokenizer DLL、模型和 native runtime 的实际运行定位；不能仅看 tokenizer 文件存在或 manifest 列出文件。

### Installer：本机可验，不等于现在可执行

父级最新契约确认 ISCC/NSIS 不可用，常见 Inno6 路径不存在。**缺编译器阻塞真实 installer 编译与恢复验收**；不能用文件复制模拟器代替，也不能自动下载/安装工具。

新 verification build 的边界必须固定：

- `PrivilegesRequired=lowest`
- `CreateUninstallRegKey=no`
- 无快捷方式、自启、自动启动 App；
- 明确 task 安装/data 根，不触及旧预览；
- binary 与用户 settings/index/models 分离；
- 卸载默认保留数据，只删除安装器拥有的精确内容。

当前主进程 elevated。`PrivilegesRequired=lowest` 本身不会证明测试曾以普通用户权限运行；记录实际 token，不能声明完成非管理员验收。

实际编译器可用且静态/合成门通过后，父级最小真实矩阵为：

1. 首次安装与自包含启动，核对实际文件、版本和依赖定位。
2. 升级保留合成 settings、schema5 index 和 models。
3. 中断、部分复制、占用文件导致失败时，旧版本仍可恢复；错误和 restart-required 不得伪装为成功。
4. 恢复后真正启动旧版本并核对版本与数据，而非只确认目录存在。
5. 卸载保留用户数据，不跨路径删除。
6. 负向目标：错误根、reparse、越界路径被拒绝。

二进制回退不是用户数据恢复。若测试允许应用写入数据，须另外保存恢复点和核对恢复条件。

## 六、父级可立即完成与必须等待的事项

### 可在当前边界内优先完成

1. **完成并整合新 CI/installer 源码**，只审其实际 diff；保留旧 workflow、源码和 inherited deletions。
2. **新脚本静态与合成测试**：环境/锁保护、路径所有权、危险目标拒绝、失败传播、日志不覆盖、payload 分类；不用 SDK 或安装器模拟结果冒充端到端。
3. **定向补读模型 manifest 和参考脚本**，产出精确公共资源清单与旧参考输入合同；不下载。
4. **新生产 App 的有界 UI 验收**，以新的 task 根进行，不重试旧 harness、不改诊断设置。
5. **最终 payload 许可/依赖清单与命令映射**，把已通过的 bounded facts 与未通过门分开记账。

### 真实阻塞／条件

| ID | 条件 | 最小解除输入 |
|---|---|---|
| **RA-MODEL-01** | 11 个锁定模型 artifact 未提供 | 获准 task-root 精确字节及 manifest 校验；之后执行真实模型验收 |
| **RA-INSTALLER-01** | 安装器编译器不可用 | 明确可用、版本/来源已核验的编译器；不能自动下载或换工具逃避失败 |
| **RA-PARITY-01** | 完整差分参考证据未确认 | 锁定旧参考/可信输出、共同 fixtures 和判据；先定向核验是否已有 |
| **RA-UI-01** | 当前完整 UI 与最终 equality 修补未获完整接受 | 新有界 App 行为证据及最终审查；不重开旧预算 |
| **RA-LICENSE-01** | notice 明确不完整 | 最终 payload 对应许可文本、分发条件和依赖审计 |
| **RA-CI-01** | native workflow 实现与远端执行尚无可核验回执 | 最终源码、当地测试；远端运行另须实际运行回执和授权边界 |
| **RA-CUTOVER-01** | P5 退休与生产副作用被当前边界明确排除 | 先完成 readiness；真实退休/生产安装另按明确范围处理 |

**不是本轮阻塞：** 没有 VM/ISO、没有 Docker/VirtualBox/VMware、旧 harness 不再重试。
**仍不能宣称：** clean-machine 验收、全 Windows 版本支持、生产 per-user 安装、真实 autostart、发布或旧链路退休。

## 七、变更、哈希与最终交接

- **Owned source write-set：空。**
- **源码语义 Diff：无。**
- **本次实际执行测试：无。**
- **本次重新计算文件哈希：无。** 不把冻结 manifest 或既有 review 记录的哈希称为本次复算。
- 报告文件由父级/运行时持久化，持久化后的 SHA-256 应由父级计算。
- 最新部署、bounded approval 和 synthetic pass 可以继续复用其明确范围；当前最终候选与这些证据的字节绑定仍由父级确认。

**推荐顺序：先完成无需新增资源的脚本门、命令映射与当前 App 补测；再在精确模型和编译器到位后执行真实 parity 与 installer 恢复。缺项继续标 blocked，不通过删旧链路、模拟安装或复用旧批准制造完整验收。**