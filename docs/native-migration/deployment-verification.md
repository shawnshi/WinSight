# 本机部署验证

结果：**新的正式 App 发布产物启动及基本交互验证通过**。不是安装器、完整迁移或独立验收批准。

## 部署位置

- 可执行文件：`.dev/native-migration/deployment-checks/20261019T140428/candidate/app/WinSight.App.exe`
- 任务目录启动器：同目录任务根 `Launch-Preview.cmd`，显式传入 `interactive-data`；不要用裸双击 EXE 代替任务目录启动，因为裸启动使用产品默认数据位置。
- 当前测试数据：同一任务目录 `interactive-data/`，未导入个人目录、旧索引或真实模型。
- 验证后进程仍运行，PID **5908**；关闭主窗口按既有契约隐藏到托盘，不等同于退出。
- 原 `.dev/native-migration/release/` 未覆盖，原发布 manifest SHA-256 保持一致。没有系统安装、开机启动、PATH、注册表或外部发布操作。

## 已执行

| 检查 | 结果 |
|---|---|
| Release 自包含发布、551 个文件大小/SHA-256 | 通过；测试宿主和旧 Swift/Tauri 文件未进入包 |
| 新任务目录冷启动并正常退出 | 退出 0 |
| 实际主 VM 暂停、暂停态关键词搜索、托盘服务恢复 | 与真实 Backend 状态一致 |
| 实际运行模块 | CoreCLR、CoreLib、WinUI DLL 均来自新发布目录；.NET 10.0.13 |
| 可见 App 窗口 UI Automation | 真实 Pause/Resume 按钮及可见状态均通过 |
| 800×600、1100×760 | 暂停按钮未越出窗口边界；恢复原窗口尺寸 |

窗口自动化仅从已知 PID 的窗口句柄进入该窗口子树，没有扫描桌面、截图、全局键盘输入或读取其他窗口。共 5 项窗口检查通过。这不等同于完整像素/无障碍认证，也没有验证托盘原生弹出菜单的实际选择。

## 与原阻塞的关系

原 `WinSight.Ui.Harness` 的 XAML 渲染失败仍保留 3/3 次计数，未再次启动该 harness，也没有重启独立 reviewer。本次在用户“部署验证”授权下，新建正式 App 的独立发布目录，并验证不同的生产可执行文件/启动路径。生产 App 成功不能反推 harness 根因，更不能冒充独立审查。

## 证据和限制

任务根目录：`.dev/native-migration/deployment-checks/20261019T140428/`。

- `deployment-result.json`
- `verification/published.smoke.result.json`（含实际加载模块路径）
- `verification/interactive-ui.json`（按钮及窗口边界回执）
- `verification/running-app.json`、发布日志、首轮探针编译失败日志

新探针首轮缺少 `System.IO` using，按编译错误修正后构建退出 0。正式 App 本轮冷启动和窗口自动化均首轮通过。

自动化于 14:10:50 UTC 结束。测试设置文件随后才创建（14:11:01 UTC），背景偏好在后续观察中为 true；不能将其归因为自动化操作，也不能未经证据认定为用户操作。没有擅自改回；后续回执确认 SetUp=false 且文档/代码根均为空，因此未获得实际目录扫描条件。验收只覆盖上述操作时点，不承诺用户后续交互后的设置不变。

仍未验证真实模型与多模态效果、长时间索引、明暗主题、键盘/读屏、完整 DPI/像素、干净机器安装/升级/卸载/回滚、签名和完整许可。当前是**不含模型的本机预览部署**，P0–P6 与生产替代尚未完成。主动 LSP 仍存在与成功编译不符的 WinUI 缺失引用诊断，不宣称 LSP 干净。
