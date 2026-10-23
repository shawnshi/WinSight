# P3 Windows 原生媒体能力探针

## 已执行事实

目标是判断当前 unpackaged .NET 10 桌面进程能否调用系统媒体 API，不是模型或完整格式验收。

- 独立探针：`desktop/tests/WinSight.Media.Probe/`，构建退出 0、0 警告、0 错误。
- 包身份查询返回 `15700`，当前探针无 package identity。
- 只生成并读取任务目录中的合成 WAV、纯色 0.5 秒短视频和缩略图；没有个人文件、模型、截图、组件安装或云端 OCR。
- 运行回执：`.dev/native-migration/p3-native-media-fixture/media-probe.json`。
- 独立输出核对：同目录 `media-output-validation.json`。

| 探针 | 实际结果 |
|---|---|
| `MediaTranscoder` WAV 转码 | 成功；输出实际为 PCM16、单声道、16000 Hz、16000 帧 |
| `MediaComposition` 纯色视频渲染 | 成功生成 MP4；0.5 秒是构造输入，不据此宣称任意媒体时间轴等价 |
| `MediaComposition.GetThumbnailAsync` | 成功；实际 JPEG 64×48，中心 RGB 为 31/96/128，合成色为 32/96/128 |

取帧方法属于 **MediaComposition**，不是 MediaClip。早期搜索摘要混淆了归属；已按 SDK 类型与[官方方法签名](https://learn.microsoft.com/en-us/uwp/api/windows.media.editing.mediacomposition.getthumbnailasync?view=winrt-26100)修正探针后构建和执行。

## 尚未证明

这些结果不能证明任意容器/codec、长媒体、视频声轨时间窗口、模型数值或召回效果，也不能证明 WinRT 取消能硬中断原生解码。探针有进程外 45 秒终止保护，本次未触发该保护；不把它视为生产提取器隔离已经实现。

当前生产 `ContentExtractor` 和索引流水线没有因为探针成功而自动获得新格式能力；正式接入仍需产物租约、原始 locator、profile/fingerprint、错误/partial 状态和取消测试。

## OCR 支持边界

[微软 namespace 文档](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr?view=winrt-26100)明确：

> APIs in the Windows.Media.Ocr namespace are only supported for desktop apps with package identity. This means that the app is installed and run from an MSIX package.

因此不对当前 unpackaged 目标承诺此 OCR 能力。本次没有尝试对私人图片 OCR。引入包身份或独立本地 OCR 引擎涉及发行/依赖选择，不能用隐式安装或模型下载代替明确设计。

## 后续实施约束

只读架构分析建议复用现有 `WinSight.InferenceHost` 可执行文件的不加载模型提取模式，在独立有界会话中解码。父侧保留源文件身份保护，产物必须位于任务目录内并在 embedding 消费完成后统一清理；确认子进程终止后才清理，超时/崩溃不能伪装为空内容。

此建议不是已实现或批准的完整媒体链路。无需新服务、全局工具或 Python 运行时；当前探针中的 Python 仅用于本地核对合成 WAV/JPEG 输出，不是产品依赖。
