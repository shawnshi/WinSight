# N2b 元数据与视频声轨原生能力探针

范围：任务合成媒体，无真实模型、私人媒体或既有索引。探针源码 `desktop/tests/WinSight.Media.MetadataProbe/`；输出位于 `.dev/native-migration/native-stack-gap/n2b-metadata-probe/`。离线恢复、固定SDK编译与两次有界进程运行均退出0。

## 已核验事实

- `MediaClip` 没有直接的 GetAudioEncodingProperties 方法；视频音轨应检查 `EmbeddedAudioTracks`，不能根据文件后缀猜测。
- 纯色无声MP4：轨道数0，原时长2秒。
- 纯色带AAC声轨MP4：轨道数1，原时长2.0201587秒。编码容器时长不应被手写为构造素材的2秒。
- `BackgroundAudioTrack.CreateFromFileAsync` **不能**直接接受该带声轨MP4，实际错误 `Source clip cannot be video file`。当前N1 audio分支使用该API，因此视频声轨不能直接套用音频文件路径。
- 对真实MediaClip按原时间轴裁剪[0,2]，放入MediaComposition，以WAV PCM16/mono/16000Hz和Precise进行纯音频渲染，成功；独立RIFF读取实际32000帧、单声道、16位。

证据：`fixtures/metadata-probe.json`、`fixtures-clip-audio/metadata-probe.json`、`fixtures-clip-audio/soundtrack-validation.json`。首次探针发现API不支持；第二次同时保留该观察并验证不同的真实clip渲染路径，没有把unsupported改写为no-audio。

## 对实现的约束

完整采样前必须由受控子进程获取原时长、视频尺寸/声轨存在信息；不能在父侧直接调用可能失控的native媒体解析。当前N1只接受调用方已知的区间，N2a只封装该提取操作，因此需要附加metadata路径。

建议新增独立的inspect-media及视频声轨提取操作，原extract-media/stdio行为与模型数学保持不变。采样和索引需等待旧策略源码核对及新增协议/取消/租约测试，不把这些探针当作完整媒体兼容证明。

尚未证明其他容器/codec、旋转/多音轨、AAC延迟补偿、任意媒体的数值parity或性能。没有下载codec、模型或执行提供器。
