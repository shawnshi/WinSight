架构层级	技术组件 / NuGet 包	推荐版本	选型定位与核心职责	替代方案比对与采用理由
基础框架	.NET SDK / C#
10.0 (LTS)



C# 12

核心运行环境，目标平台定义为 net10.0-windows10.0.22621.0	相比 .NET 11，.NET 10 为长期支持版（LTS），企业兼容性与第三方原生 C++ 扩展绑定更稳定。
桌面 UI	Microsoft.WindowsAppSDK	2.5.1	提供 WinUI 3 现代控件、Mica 云母材质及窗口生命周期	相比 WPF/WinForms，WinUI 3 具备 Windows 11 原生硬件加速合成与 Fluent 动效支持。
桌面 UI	CommunityToolkit.Mvvm	8.4.2	基于 Source Generator 的轻量 MVVM 模式驱动	编译期生成 \[ObservableProperty]，消除反射开销，保持 UI 极速响应。
桌面 UI	SettingsControls	8.2.251219	Windows 11 原生风格设置卡片（SettingsCard）	统一监控文件夹列表、向量库配置的卡片折叠与交互规范。
系统托盘	H.NotifyIcon.WinUI	2.4.1	纯 WinUI 3 原生托盘图标与全局热键 (Alt+Space)	彻底摆脱对 System.Windows.Forms 的沉重依赖，保持进程纯净。
AI 推理	Microsoft.WindowsAppSDK.ML	2.1.94	Windows ML 本地硬件调度与 DirectML 推理会话	替代独立 Microsoft.ML.OnnxRuntime.DirectML，避免与系统 WinML 运行时冲突，自动调度 GPU/NPU。
分词器	Microsoft.ML.Tokenizers	2.0.0	加载 SentencePiece / BPE 词表配置	原生 C# 解析 EmbeddingGemma 2 的 262,144 词表分词，无 Python 胶水层依赖。
SIMD 计算	System.Numerics.Tensors	10.0.13	提供 TensorPrimitives SIMD 硬件指令加速	在 CPU/AVX2 指令集上毫秒级完成 256d/768d 向量的 L2 归一化与余弦相似度计算。
离线模型	embeddinggemma-2-ONNX	q4 量化	多模态共享向量空间嵌入模型（文本 270M + 视觉 170M）	内存常驻仅 175 MB（视觉按需加载 109 MB），完全避开 300M 音频模型，体积性价比最高。
混合存储	Microsoft.Data.Sqlite	10.0.13	单文件 SQLite 连接器（启用 WAL 模式）	杜绝独立数据库进程开销，支持通过原生 VACUUM INTO 实现向量库零停机热迁移。
向量扩展	sqlite-vec	v0.1.6 (vec0.dll)	基于 SQLite 虚表的纯 C 向量近邻检索（KNN）扩展	相比 Chroma / Milvus / Qdrant 等重型中间件，vec0.dll 仅需单个动态库即可嵌入 SQLite。
全文分词	SQLite FTS5 (内置)	内置支持	全文检索虚拟表，采用 trigram 三元组分词器	原生支持中英文及代码子串检索，无需外部维护繁重的 Jieba 中文分词字典。
PDF 解析	UglyToad.PdfPig	1.7.0-custom-5	纯 C# 按页提取 PDF 文本、坐标字块与元数据	无需外部 Poppler / PDFium C++ 依赖，避免打包尺寸膨胀与崩溃隔离问题。
Word 解析	DocumentFormat.OpenXml	3.5.1	微软官方 OpenXML SDK，高速提取 .docx 正文段落	内存占用低，免装 Office 桌面软件即可安全读取。
图像预处理	SixLabors.ImageSharp	4.1.3	读取 .png、.jpg、.webp 并归一化为 Tensor 输入	纯托管内存安全实现，避免 GDI+ 在高并发后台线程上的资源死锁。
本地 OCR	Windows.Media.Ocr	WinRT 内置	默认截图文字提取引擎（系统多语言包支持）	零额外打包体积，毫秒级响应常见屏幕截图与单据。
高精 OCR	RapidOcrNet	4.2.0	驱动 PaddleOCR (PP-OCRv5/v6) .onnx 模型	针对旋转角度大、印章覆盖等复杂排版提供可选的高精度文字识别通路。
Agent 协议	ModelContextProtocol	2.2.0	官方标准 MCP C# SDK（定义 \[McpServerTool]）	规范化对外暴露 search\_files、read\_file\_context 等工具契约。
HTTP 端点	ModelContextProtocol.AspNetCore	2.2.0	内置 Kestrel 提供本地 Streamable HTTP 服务	供支持直接 HTTP 挂载的本地智能体连接，主界面支持一键实时启停。
应用宿主	Microsoft.Extensions.Hosting	10.0.13	统一管理 DI 依赖注入、日志与后台扫描队列	与 MCP SDK 的底层抽象版本对齐，保证后台防抖文件监听器平稳运行。
安装打包	Inno Setup	6.3.3	生成标准的向导式安装包 WinSight-Setup-x64.exe	相比 MSIX，解包模式允许用户自由选择安装盘符，且能直接将 bridge 写入系统 PATH。
