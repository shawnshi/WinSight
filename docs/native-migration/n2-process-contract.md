# N2a：媒体子进程与产物租约合同

前置门：N1 已获得独立限定通过，`reviews/native-media-recheck.md` 关闭 NM-N1R-01..03。用户已授权分步补齐原生实现；该审查只证明 N1，不代替 N2 授权或验收。

本切片不改 Core/模型/stdin推理协议、索引算法、设置或运行中的预览。先交付可独立验证的父侧媒体传输，再进入 N2b 采样/索引消费。所有运行测试只使用合成文件与任务目录。

## 固定组件与接口

生产 write-set：`desktop/src/WinSight.Infrastructure/Media/**`（新增），仅必要的 `Runtime/TestAccess.cs` 追加测试友元。既有 SafeFile、WindowsChildJob 和推理客户端只读复用，不改其原语义；可新增媒体专属进程/job包装。

- `public sealed record NativeMediaProcessRequest(string SourcePath, string Kind, double StartSeconds, double? EndSeconds = null, int MaxEdge = 512)`。
- `public sealed class NativeMediaProcessOptions`：init属性 `HostExecutable:string`、`WorkspaceRoot:string`、`RequestTimeout:TimeSpan=60秒`、`TerminationTimeout:TimeSpan=10秒`、`MaxOutputBytes:int=65536`。构造前两者必须显式绝对本地路径，不发现私人缓存。
- `public sealed class NativeMediaProcessClient : IAsyncDisposable`，构造器接受上述 options，`Task<NativeMediaArtifactLease> ExtractAsync(NativeMediaProcessRequest request, CancellationToken cancellationToken=default)`。
- `public sealed class NativeMediaArtifactLease : IAsyncDisposable`：只读 `ArtifactPath:string`、`Modality:string`、`StartSeconds:double`、`EndSeconds:double?`、`SourceDurationSeconds:double?`、`TaskDirectory:string`。成功返回仅一个 N1 产物；消费完成前持有源和产物/任务父路径保护。Caller负责在embedding await结束后Dispose，不自动提前释放。

Host命令仅 `extract-media ABS_REQUEST_JSON`，ProcessStart参数结构化，不通过shell。JSON frame空end字段省略，不发送显式null。N1成功协议严格验证，包括版本、源路径、kind、时间、模态、恰好一个产物、相对单文件名、有限尺寸/格式与字节上限。

## 必须成立的断言

1. **本地/授权范围**：请求的源、受信宿主和任务父目录先做OS本地盘类型检查，再访问元数据。只能处理明确传入的源，不能自动扫描其他目录；路径保护不是新的用户授权。
2. **路径稳定**：复用SafeFile按组件pin源/宿主/工作根与任务路径，拒绝reparse/cloud/device并核验真实句柄路径。源/任务祖先在native工作与返回租约期间不能被改名/替换。不能以单次Path.GetFullPath或叶文件FileShare代替祖先保护。
3. **从出生受控**：不允许先跑再附加job的逃逸窗口。以Windows CREATE_SUSPENDED创建子进程，附加KILL_ON_JOB_CLOSE受控job后才Resume。若实现不了这一物理门，应阻塞，不悄悄降级Process.Start后Attach。stdio继承只允许确切子端handles；不泄漏父级pin/凭证/任意句柄。
4. **有界读与期限**：stdout/stderr按严格UTF-8有界读取，各≤MaxOutputBytes；溢出、非法编码、协议错、取消、超时与Dispose全部终止受控进程并确认退出。排队和请求总墙钟受RequestTimeout限制；不无界等待管道EOF。
5. **实际收敛**：主动TerminateJobObject并确认main已退出及job active-processes为0之后才清理；仅WaitForExit主进程或关闭job不等于确认所有后代结束。终止未确认则明确失败并保留任务目录/必要保护，不假报已清理/已取消完成。
6. **产物所有权**：每次创建独占新任务目录，request.json与worker输出目录均受保护。源、宿主、其他任务/租约和任意陌生文件不得删除。返回产物须位于任务目录、无重解析、是常规有界文件；越界或伪造响应失败。
7. **清理不穿越**：确认进程结束后只删除本任务的已知文件/空目录，释放叶文件句柄时仍保留父目录pin，禁止递归跟随重解析或清理未知树。清理失败显式报错/保留路径，不能吞成成功。失败任务目录可保留证据，不做全局历史清扫。
8. **并发和关闭**：默认单活动解码，有限排队（≤16），取消排队者不影响当前解码；Dispose阻止新请求，终止活动子进程并等已知任务收敛。已经返回的消费租约不能被client.Dispose提前删除；租约Dispose幂等、资源按单写者规则释放。

## 失败模式与负向测试

永久套件 `desktop/tests/WinSight.MediaProcess.Tests/**`，独立合成FakeMediaHost子项目只用于测试，不混入生产包。须覆盖：

- 实际N1宿主至少一次音频/视频成功，产物存在直到租约Dispose；source/settings/model不变。
- pre-cancel、坏参数/远程分类/不存在源：无子进程或输出；两个租约互不误删。
- 子进程挂起、native不合作、后代持有管道、stdout/stderr洪流/非法UTF8、坏JSON/多行/错误路径/模态/区间/大小：有限时间失败，进程/job确实结束，未确认前不删。
- 创建即派生后代，证明suspended attach后所有后代在job内；强制父测试driver退出后不能遗留媒体worker。
- 在解码或消费租约持有时尝试改名/写入/放置junction源/工作祖先，应被物理拒绝；不触碰项目外私人目录。
- Dispose与活动/排队/已返回租约并发；仅当前任务路径可清理，兄弟哨兵保留；清理失败/退出未确认不得伪装成功。

本切片需实际Win32测试和独立审查。源码存在、测试计数或端口/进程运行本身不代表生命周期门通过。N2b背景索引仍保持未接入；不能将N1/N2a成功声明为完整媒体覆盖。
