# N2b-1 冻结源码采样核对（尚未接入）

规范来源是P0源码，不是techstack建议或N1默认值。这里只固化本次已直接读到的算法；N2b-0元数据/声轨切片先独立验收，不在其间接通索引。

## 实际Windows/Portable合同

来源 `.dev/native-migration/p0/source/Sources/WinSightKit/Platform/Portable/Media+Portable.swift` 约80–170行、`Model.swift` 64–67行、`Indexer.swift` 452–467行。

- 音频：16kHz mono PCM16，window=30秒，hop=25秒。
- 每次满窗输出前计算 `sqrt(mean((sample/32768)^2))`，**严格大于0.003**才保留；等于阈值拒绝。源码 `MediaExtractors.swift#rms`。
- 尾段不是简单“剩余长度≥2秒”：若已经输出满窗，须先扣除5秒已覆盖overlap，**新增未覆盖样本数≥32000**才输出整个残余buffer。未输出过满窗则整个buffer≥32000才输出。
- 例：长度30/31秒只有[0,30]；32秒额外输出[25,32]。2秒首段保留；不足2秒不保留。静音判断仍独立应用。
- FFmpeg原实现流式解完整音频，时间定位来自实际PCM sample offset，不是临时WAV零点。原逻辑没有5000次音频尝试上限，不能把设计建议直接改为既有合同。
- 视频：interval=3秒，默认maxFrames=1200，step=max(3,duration/1200)，first=min(0.5,duration/2)。采样点first+n*step，排除start≥duration。调用方maxPixel=**1024**，frame embedding budget=70，不是N1默认512。
- 原FFmpeg `fps ... start_time=first:round=up` 的槽语义是取槽时刻之前/当时的最后一帧。N1的NearestFrame API不是其精确等价证明；源时间定位、镜头切点与Native解码实际内容必须定向验证，不宣称逐帧parity。
- 去重仅与**上一个保留帧**比较8×8 averageHash，Hamming距离**≤4**即丢弃当前帧；不把丢弃帧作为下次参考。保留帧的end最终设为下一保留帧start，最后end=duration。
- 视频音轨存在来自独立streams probe；探测失败不能等价false。存在时使用同一套音频window/hop策略；不能只有frames就把失败声轨记为done。

## AverageHash细节

来源 `Platform/Portable/Images+Portable.swift` 153–176行。按8×8格覆盖真实RGB图；格边界用整数除法，并确保格至少含1像素（小图允许重复覆盖）。像素灰度先取整数 `(299R+587G+114B)/1000`，格均值与全格均值再分别取整数。仅cell>mean置位；bit=index=cy*8+cx。不要用浮点/大于等于或简单重采样8×8替代。

旧帧经FFmpeg缩放/JPEG再解码，新帧为Native PNG；同一hash算法不证明这些像素一致。需隔离算法oracle与跨decoder视觉parity。

## 接入前尚需核验

- 当前IndexingService.ProcessAsync已有源SafeFile保护，仅允许原文件image和text；任意third-party Excerpt/Info不得成为临时artifact输入。
- 新增可信sealed内部session/真实lease映射，源保护跨metadata、采样、embedding，临时产物活到对应await结束；逐件处理，不能全文件产物堆积。
- 保持Core公共records、schema5/Float16/模型/tokenizer/profile。媒体专用版本标记只使过旧media记录重新尝试，不自动重建或清库；现有fingerprint mismatch仍阻断。
- 未确认退出、取消、声轨失败与能力缺失必须保留不同结果；partial不能盖done。数据量/segment限额需依据现有实际限制，不创造未经说明的无声截断。
- AAC EOF/native时间量化、多音轨选择、复杂容器/旋转/可变帧率与真实模型parity尚未验收。这里没有许可访问真实语料、导入设置或修改任何既有索引。
