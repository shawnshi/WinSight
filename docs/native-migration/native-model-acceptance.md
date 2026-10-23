# 本机真实模型限定验收

## 结论

当前发布候选Host的CPU真实文本/图片/音频推理通过。最终768维向量与独立Python ORT CPU计算、冻结P0文本输出的max_abs均为0；未修改模型、产品代码、数值阈值或既有测试结果。此结果不是完整P0–P6、旧Swift/FFmpeg、GPU、Backend索引链路或发布批准。

## 输入与证据

- Host：`.dev/native-migration/deployment-checks/n2b-native-20261019/candidate/app/inference/WinSight.InferenceHost.exe`，精确二进制及生产源码/locks哈希在preflight/verify中核对。
- 模型：冻结manifest revision `daa72c51243991dfcaf9f9137d2c573d8f7790c0`，11文件505137637字节，全部size/SHA256匹配。公开旧任务模型逐项验证后复制，不读取私人cache。
- 真实执行与原始Host协议：`.dev/native-migration/lanes/native_model_acceptance/scripts/model-acceptance/actual-1/`。
- 参考与最终保护验证：同目录`actual-2/`，`reference.json`及`verify.command.json`。
- actual-2的preflight/Host是actual-1已通过产物的逐文件SHA复制，不是重执行；18项复用记录在`resume-provenance.json`。

## 已执行

1. preflight：11模型、明确CPU无provider自动下载分支、source/locks/224 inherited staged deletions保护通过。
2. Host：真实三模态推理，有限且归一化768维输出、不同输入区分、重复性、空文本/无效inputs/坏WAV拒绝、错误后恢复、profile稳定；三个模型session实际加载。quit后Job ActiveProcesses=0，读取线程退出确认。
3. 独立CPU参考：官方PyPI `onnxruntime-1.24.4-cp313-cp313-win_amd64.whl`，SHA256 `3b6ba8b0181a3aa88edab00eb01424ffc06f42e71095a91186c2249415fcff93`；仅解包至task site-packages，通过进程PYTHONPATH使用，不用pip、不升级全局Python。profile记录三个实际CPU图，main7次、vision2次、audio2次运行。
4. 对比text/image/audio/冻结P0 text：max_abs分别0/0/0/0，cosine约1；使用执行前固定阈值，无事后放宽。
5. verify：parent/lane产品source、production locks、候选、模型、224 inherited staged deletions未变。

## 失败、修复和限制

- 模型test-engineer代理2小时超时（`NM-N3-MODEL-AGENT-TIMEOUT-01`）；父级已检查partial源码，确认相关Python/本轮模型Root Host进程均为0后接手。没有更换provider或AI CLI。原产物与beforeimages保存在`release-acceptance/model-timeout-evidence/`。
- 父级修复外层无界ReadToEnd/管道等待：每流65536字节、严格UTF8、145秒上限、有限退出/排空、不覆盖旧phase证据；六项新真实子进程边界测试全部通过（exact/overflow/invalid/empty/nonzero/timeout），不是模型测试。
- 首次Python参考因全局ORT1.20.1不识别GatherBlockQuantized.bits失败，保留actual-1完整错误。按新证据在task准备与native1.24系列相符的ORT1.24.4，仅重试参考分支一次。未改权重/ONNX属性/预处理；失败计数不因新目录重置。
- 初次Host测试原生stderr路径与外层Python stderr发生命名碰撞，原生stderr内容未能保留，不宣称其无警告。后续脚本已分离为host.protocol.stderr.txt；本轮不为补日志重跑已通过Host。
- 图片是48×48且budget=1，刻意不覆盖resize/JPEG/旋转；音频是短合成16k PCM，不等于完整30s原采样策略或codec/时间轴证明。
- Python预处理是源码知情的独立实现，最终CPU向量相等不证明原Swift/FFmpeg数值、像素、完整检索排名、用户真实corpus、GPU/auto provider、UI交互或安装/许可证验收。
- 此页是执行者报告，不是独立模型批准；CI/installer独立审查另有fail记录，不能借本页放行。
