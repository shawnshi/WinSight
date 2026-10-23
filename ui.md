作为 Windows 桌面平台的设计与工程实践方案，WinSight 的界面遵循 [Microsoft Fluent Design System](https://learn.microsoft.com/en-us/windows/apps/design/)（Windows 11 设计语言规范），基于 [WinUI 3 (Windows App SDK 2.5.1)](https://www.nuget.org/packages/Microsoft.WindowsAppSDK) 构建。

设计核心确立为 **“键盘优先（Keyboard-First）、双形态解耦（HUD 浮窗 + 主控制台）、物理材质感（Mica + Acrylic）与亚秒级即时反馈”**。

\---

### 一、 视觉设计语言系统 (Visual Design Tokens)

|视觉维度|规范标准与技术落地|设计意图与用户心理模型|
|-|-|-|
|**材质层级**|• **主窗口**：`MicaBackdrop Kind="Base"`（随桌面壁纸动态漫反射）<br>||

<br>• **快搜浮窗**：`DesktopAcrylicBackdrop`（高通透高斯模糊磨砂亚克力）<br>

<br>• **悬浮卡片**：`LayerFillColorDefaultBrush`（带 1px 细微半透描边） | 建立“主程序是系统原生应用，快搜浮窗是系统级轻量 HUD 抬头显示”的层级认知。 |
| **圆角与间距** | • **窗口外框**：Windows 11 标准圆角（`DwmSetWindowAttribute` 开启 `DWMW\_WINDOW\_CORNER\_PREFERENCE`）<br>

<br>• **容器卡片**：`CornerRadius="8"`<br>

<br>• **操作控件/胶囊**：`CornerRadius="4"` 或全圆角（Capsule Pill）<br>

<br>• **网格基准**：严格遵循 4px/8px 网格基底 | 柔和的现代视感，杜绝生硬切割感，在多尺寸屏幕下呈现自然秩序。 |
| **字体排版** | • **字体族**：[Segoe UI Variable](https://learn.microsoft.com/en-us/windows/apps/design/style/typography)（Text / Display / Small 智能光学适配）<br>

<br>• **数值与代码**：Cascadia Code（等宽数字对齐，字符间距紧凑）<br>

<br>• **中文辅助**：Microsoft YaHei UI（字符基线微调与行高修正） | 保证文档长片段的高可读性，同时让命中相似度、行号与耗时指标清晰易读。 |
| **色彩与语义** | • **Accent Color**：系统主题色（`SystemAccentColor`）+ 智寻青（`#00B4D8`）<br>

<br>• **状态绿（MCP 运行）**：`#107C41`<br>

<br>• **警告橙（索引忙碌）**：`#D83B01`<br>

<br>• **文本层级**：`TextFillColorPrimary` / `Secondary` / `Tertiary` | 严格满足 WCAG 2.1 AA 级对比度（≥ 4.5:1），确保在深浅色模式间丝滑切换。 |

\---

### 二、 核心形态一：全局快搜浮窗 (`QuickSearchWindow`)

针对日常高频检索，默认按下 `Alt + Space` 即可在当前屏幕居中弹出一个 **无边框、亚克力高斯模糊的轻量 HUD 搜索台**（类似 Raycast / Spotlight，但具备深度的 Windows 原生图文检索体验）。

```text
┌───────────────────────────────────────────────────────────────────────────┐
│ 🔍  \[ 租房合同 违约金条款                                              ] ⏱ 12ms │
├───────────────────────────────────────────────────────────────────────────┤
│ \[全部 (14)]  \[📄 文档 (8)]  \[🖼️ 图片与截图 (4)]  \[💻 代码 (2)]       Tab 切换模态 │
├──────────────────────────────────────┬────────────────────────────────────┤
│ 📄 房屋租赁合同\_最终版.pdf              │ 📄 房屋租赁合同\_最终版.pdf · 第 4 页  │
│    ...若乙方提前解除合同，须支付相当于...│ 相似度: 92.4% (DirectML 语义命中)  │
│    D:\\Documents\\Contracts  \[第 4 页]  │ ────────────────────────────────── │
│                                      │ 第七条 违约责任                     │
│ 🖼️ 微信截图\_租房押金条.png              │ 7.2 租赁期间，任何一方提出提前解除 │
│    OCR: 收据 押金壹万元整 签字生效...    │ 合同的，须提前三十日书面通知对方， │
│    D:\\Pictures\\Screenshots           │ 并向守约方支付相当于【两个月租金】 │
│                                      │ 的违约金。                         │
│ 📄 租房意向确认单.docx                 │                                    │
│    ...押金及违约赔付条款细则...        │ \[Enter 打开原件] \[Ctrl+C 复制纯文本]│
└──────────────────────────────────────┴────────────────────────────────────┘

```

#### XAML 核心布局实现 (`QuickSearchWindow.xaml`)

```xml
<Window
    x:Class="WinSight.App.QuickSearchWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    Width="760" Height="480">

    <!-- 亚克力半透明材质背景 -->
    <Window.SystemBackdrop>
        <DesktopAcrylicBackdrop TintColor="{ThemeResource SolidBackgroundFillColorBase}"
                                TintOpacity="0.85" LuminosityOpacity="0.9"/>
    </Window.SystemBackdrop>

    <Grid Padding="16" RowDefinitions="Auto, Auto, \*">
        <!-- 1. 顶部无边框输入框与微观动态加载条 -->
        <Grid Grid.Row="0" Margin="0,0,0,12">
            <TextBox x:Name="SearchBox"
                     FontSize="18"
                     PlaceholderText="输入语义描述、精确关键字或代码报错代码..."
                     BorderThickness="0,0,0,1"
                     Background="Transparent"
                     Text="{x:Bind ViewModel.QueryText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}">
                <TextBox.Header>
                    <FontIcon Glyph="\&#xE721;" Margin="0,0,8,0" Foreground="{ThemeResource AccentFillColorDefaultBrush}"/>
                </TextBox.Header>
            </TextBox>
            <ProgressBar IsIndeterminate="{x:Bind ViewModel.IsSearching, Mode=OneWay}"
                         VerticalAlignment="Bottom" Height="2"
                         Visibility="{x:Bind ViewModel.SearchProgressVisibility, Mode=OneWay}"/>
        </Grid>

        <!-- 2. 模态筛选胶囊 (Segmented Control / SelectorBar) -->
        <SelectorBar Grid.Row="1" Margin="0,0,0,12" SelectedItem="{x:Bind ViewModel.SelectedModalityTab, Mode=TwoWay}">
            <SelectorBarItem Text="全部" Icon="AllApps"/>
            <SelectorBarItem Text="文档" Icon="Document"/>
            <SelectorBarItem Text="图片与截图" Icon="Pictures"/>
            <SelectorBarItem Text="代码" Icon="Code"/>
        </SelectorBar>

        <!-- 3. 双栏结果预览区 (Master-Detail 布局) -->
        <Grid Grid.Row="2" ColumnDefinitions="4.5\*, 5.5\*" ColumnSpacing="12">
            <!-- 左侧：即时搜索结果流 -->
            <ListView Grid.Column="0"
                      ItemsSource="{x:Bind ViewModel.SearchResults}"
                      SelectedItem="{x:Bind ViewModel.SelectedHit, Mode=TwoWay}"
                      SelectionMode="Single">
                <ListView.ItemTemplate>
                    <DataTemplate>
                        <Grid Padding="8,6" CornerRadius="6" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="\*"/>
                            </Grid.ColumnDefinitions>
                            <FontIcon Grid.Column="0" Glyph="{Binding IconGlyph}" Margin="0,0,10,0" VerticalAlignment="Top"/>
                            <StackPanel Grid.Column="1" Spacing="2">
                                <TextBlock Text="{Binding FileName}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                                <TextBlock Text="{Binding Snippet}" Style="{ThemeResource CaptionTextBlockStyle}"
                                           Foreground="{ThemeResource TextFillColorSecondaryBrush}" MaxLines="2" TextTrimming="CharacterEllipsis"/>
                            </StackPanel>
                        </Grid>
                    </DataTemplate>
                </ListView.ItemTemplate>
            </ListView>

            <!-- 右侧：上下文极速预览面板 -->
            <Border Grid.Column="1"
                    Background="{ThemeResource LayerFillColorDefaultBrush}"
                    CornerRadius="8" Padding="14" BorderThickness="1"
                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}">
                <ContentControl Content="{x:Bind ViewModel.SelectedHitPreviewControl, Mode=OneWay}"/>
            </Border>
        </Grid>
    </Grid>
</Window>

```

\---

### 三、 核心形态二：管理与控制中心 (`MainWindow`)

主控制台采用标准的 Windows 11 左导航拓扑架构（`NavigationView` + `Mica` 材质），聚焦管理性、透明审计与系统调优：

#### 1\. 多模态深度检索页 (`SearchPage`)

* **视窗拆分（Adaptive SplitView）**：
* **文档视图**：左侧命中段落列表，右侧内嵌渲染 PDF 原页高亮与 Docx 段落。
* **图片画廊模式（Image Gallery）**：采用 `ItemsView` + `UniformGridLayout` 自适应瀑布流，图片卡片底部浮动标签显示：`以图搜图余弦分 94.2%` + `本地 OCR 提取文字摘要`。



* **搜索调谐控件（Tuning Expander）**：
* 允许高级用户滑块调节“语义向量权重（Dense）”与“BM25 全文分词权重（Sparse）”的配比（默认 RRF 自动融合）。



#### 2\. 监控文件夹配置页 (`FoldersPage`)

* **卡片式管理（SettingsCard 风格）**：
* 采用 `CommunityToolkit.WinUI.Controls.SettingsCard`。
* 每个监控目录为一个折叠卡片，清晰呈现：目录路径、总索引文件数、磁盘体积开销、最后同步时间。
* 卡片内置快捷开关：`\[包含子目录]`、`\[启用图片视觉索引]`、`\[启用源代码索引]`、`\[临时暂停]`。



* **实时扫描动态反馈**：
* 当后台 `FileSystemWatcher` 感知到文件变化时，目录卡片边框流转微光脉冲动画，并展示带有增量计数的平滑 `ProgressBar`。



#### 3\. 向量库与模型调优页 (`StoragePage`)

* **向量库存储位置与热迁移向导**：
* 直观展示当前 `winsight\_index.sqlite` 的物理路径及占用空间（带动态环形饼图显示“向量数据 / FTS5 全文数据 / 图片缩略图缓存”的占比）。
* 点击 **“更改存储路径...”** 触发原生对话框，提供两个明晰选项：
* `\[保留并迁移现有索引到新路径]`（执行 SQLite 原生 `VACUUM INTO` 无损导出）。
* `\[在新路径重新初始化空索引库]`。





* **硬件执行提供程序（Execution Provider）实时看板**：
* 显示当前 DirectML 实际绑定的硬件芯片（如：`NVIDIA GeForce RTX 4070 Laptop GPU` 或 `Snapdragon X Elite NPU`）。
* 提供 `q4 (284 MB 显存，推荐)` 与 `q8 (509 MB 显存，高精度)` 权重热切换开关。



#### 4\. MCP 协议神经中枢与 Agent 审计台 (`McpControlPage`)

* **Hero 开关区**：
* 页面顶部设置巨大尺寸的 `ToggleSwitch`，伴随状态指示灯徽章（`● 本地 MCP 服务在线 (Port: 18420)`）。



* **客户端一键注册体验**：
* 针对 Claude Desktop 与 Cursor，各放置一张优雅的集成卡片：显示已探测到的客户端配置文件路径，用户仅需点击 `\[一键注入挂载]`，无需手动修改复杂 JSON。



* **Agent 实时调用瀑布流（Live Trace Feed）**：
* 模拟开发者控制台，实时滚动显示外部 Agent 的调用日志：
`\[20:48:02] Cursor 智能体调用 search\_files("DirectML 推理优化") -> 耗时 18ms，命中 5 条结果`。



\---

### 四、 键盘交互流与微交互动效规范 (Interaction Physics)

* **全局召唤与隐藏**：
* 用户在任意界面按下 `Alt + Space`，浮窗以 `Scale: 0.95 -> 1.0` 结合 `Opacity: 0 -> 1` 的弹簧曲线（Spring Animation，时长 150ms）迅速就位，焦点强制聚焦在 `SearchBox`。
* 按下 `Esc` 或点击窗口外区域，即时平滑隐藏，不阻塞当前正在进行的工作流。



* **纯键盘导航序列（Arrow \& Tab Flow）**：
* `↑` / `↓`：在搜索候选列表中移动高亮选择项，右侧预览窗实时无缝切换（防抖 50ms）。
* `Tab`：在筛选器（All / Docs / Images / Code）间快速轮转。
* `Space`：在图片结果上轻按触发全屏悬浮大图预览（Quick Look）。
* `Enter`：调用 Windows 默认程序直接打开文件并精准定位到对应页码或行号。
* `Ctrl + Enter`：在 Windows 资源管理器中打开并高亮选中文件（`explorer.exe /select,"path"`）。
* `Ctrl + Shift + C`：复制文件绝对路径；`Ctrl + C`：复制命中的高亮摘要文本。



* **系统音效适配**：
* 启用 Windows 11 原生细微音效（`ElementSoundPlayer`），在热键呼出、检索完成与配置变更时给予极低分贝的物理触感听觉反馈。
