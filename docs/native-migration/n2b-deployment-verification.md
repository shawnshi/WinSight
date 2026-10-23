# N2b原生媒体候选：全新隔离本地部署验证

结果：**新候选发布、实际App启动/基础交互、model-free helper验证通过**。这是此前已授权“部署验证”的同范围新task stage，不是系统安装、release批准或真实corpus启用。

## 目录与启动

Stage：`.dev/native-migration/deployment-checks/n2b-native-20261019/candidate/`

启动：`.dev/native-migration/deployment-checks/n2b-native-20261019/Launch-Preview.cmd`，固定使用同stage的`interactive-data`。实际交互验证PID为37064（时点记录，不保证未来仍运行），进程/创建时间/精确exe在`interactive-process.json`。新实例留作本地查看；不停止/覆盖原5908历史预览，也未核查其当前存活或用户交互后的数据。

## 实际执行与证据

所有显式dotnet命令在进程级禁用certificate生成/globaltools PATH并opt-out telemetry，不新CLI_HOME。

- App、CLI、InferenceHost分别Release/win-x64/self-contained **--no-restore** publish到新目录，均退出0。日志`logs/{App,Cli,Host}.publish.log`。无NuGet联网、模型/provider下载或Cargo。
- 新manifest包含819个实际payload文件，434860008字节。逐项大小/SHA通过，未包含test/FakeHost/Swift/Tauri或真实模型payload。App和model-free native helper可同时存在，`appOnlyPreview=true`表示未带模型的非发布预览；额外标记modelFreeNativeHelperIncluded=true。
- 实际新App `--data-dir <新smoke-data> --smoke-test`，DOTNET_ROOT指向不存在目录、禁止multilevel fallback；退出0、指定task SQLite实际建立。实际CoreLib/CoreCLR/WinUI来自候选自身目录，观察到.NET10.0.13。
- smoke经真实main VM pause、paused期间keyword search、tray-service resume验证backend状态，非仅按钮文字模拟。`verification/published.smoke.{stdout,stderr,result}.json/log`。
- 新interactive-data的实际WinUI进程，由既有编译UIA probe只访问该PID/window subtree，点击真实Pause/Resume并检查可见状态；800x600、1100x760 viewport各检查按钮在窗口内，**5/5通过**。`verification/interactive-ui.json`及`logs/interactive-ui.log`。
- 已发布的 `candidate/app/inference/WinSight.InferenceHost.exe inspect-media`读取本任务复制的合成2秒WAV，实际duration2/hasAudio=true/audioTrackCount=null通过；新输出不依赖模型。`verification/deployed-helper.json`、stdout/stderr、fixture SHA保留。
- 五个生产lock（Core/Infra/App/CLI/Host）发布前后字节相同。原`.dev/native-migration/release/`及旧deployment-check stage均未写入。

## 源码验收绑定

N2b-2独立限定通过，报告reviews/n2b-runtime-review.md、机器状态reviews/n2b-runtime-review-state.json；NM-N2B2-LIFECYCLE-01关闭。父级核验12份受审Runtime/新tests源码与review manifest A42BAB99A585587CC89F19F583B9E6C541D1AE842F12825B7A6EEDA0E69F7141一致。

独立review为源码/记录证据审查，非reviewer重执行。主树Runtime guard与media helper真实provider/model数学仍不等于跨运行时数值parity。

## 不覆盖的批准

没有读取/导入真实文件、用户设置、模型缓存或既有index，没有启用真实corpus后台监控。无真实模型payload，因此不能把新预览的模型驱动音视频semantic索引/检索说成已验证；public backend的29项合成用例使用fake768 embeddings，native case为model-free解码。

本UIA覆盖不是完整keyboard/屏幕阅读器/DPI/theme/像素验收，不修复旧UI harness三次失败或补齐旧独立UI审查。没有运行安装器/注册表/autostart/外部客户端变更，没有clean-machine安装升级卸载、签名、push或外部发布。历史证书store变化未知，无inspect/trust/export/delete。

整个P0–P6与生产切换未完成；不能据本stage退休旧Swift/Tauri/FFmpeg生产链路。后续Native CI/安装恢复/真实模型parity各走自己的有界门。
