# A4b：模板时间线、标尺与编译反馈

日期：2026-09-17；验收收尾：2026-09-18。状态：四项已实施、自动验证完成；用户已确认 UAT-A4b 全部验收通过。后续新增手柄/编译提示、Template 标签及圆角柔化也已分别获用户明确验收，实施与验证口径独立保留在 [追加记录](Midora-A4b-Marker-Editing-Followup-2026-09-17.md)。

基线：`87ba50ab8228301f7c1800e02c5a2d5283e0746e`。实施轮授权不包含提交、推送或本地发布；2026-09-18 用户在验收后另行授权记录、提交和推送，仍不包含本地发布。不使用 computer-use。逻辑参数专用点类型 / 写入边界强化仍延期。

## 需求追踪

依据：执行计划 `execution/07-Template-Timeline-and-Compile-Feedback.md` 的 R15/R22/R31/R32、已批准 D-UI05/06/07；SRS §7.27、§18.4.4、§18.11、§20.1 及 INV-009/010/020/041/078/089/113/116。

| 输入 | 输出 / 边界 | 失败与归属 |
|---|---|---|
| Definition、全部 SubVoice 内容、Loop、Pre-Roll、冻结 Snap | Template ruler 尾手柄只改变正式 Template Length；原长度加吸附 delta，停在合法下界；不裁剪、删除或改写事件 / Loop / Pre-Roll | Escape、失捕获、owner/revision 变化零提交；一次 Undo。预览属于会话，长度属于 source |
| Project 拍号图、Segment offset、显示密度 | 小节标签按全局一基小节号抽样；平移不改变抽样相位 | 宽整数保护极端 Tick；不改变音乐时间和 Snap |
| All Tracks 首次有效布局 / DPI | 内容区扣除 ruler，整数 device pixels/key ≥3；初始化一次 | 已有手调 / 恢复状态优先；零高度等待，切 Tab / resize 不重置 |
| 当前编译 generation / revision、流程已有工作量 | Compile 旁显示阶段；可靠分母才显示该阶段百分比 | 不预扫 / 预展开；节流、旧任务隔离，失败和取消清理；不进入 canonical / 指纹 / source |

## 实施设计约束

1. 模板下界复用分页内容的不可变摘要，避免每次拖动遍历所有对象；曲线 / Loop / Pre-Roll 同样计入。正式命令再次验证当前状态。UI 手势冻结原长、下界、Snap、来源修订，取消不改模型。
2. 标尺只改变标签抽样，不修改已经正确的原始 tick 标尺分支。跳过不可见小节必须是有界跳跃，而不是从 tick 0 遍历。
3. 编译进度是可选的旁路观察者；未知总量只给阶段，已知总量从现有工作循环累计。UI 读取最新值，不对每对象排队 Dispatcher 通知。不改变缓存身份、编译输出或取消语义。
4. 首次 Fit 状态属于 Workspace 而非视图实例，避免 Tab 卸载/重建后重新覆盖用户视口。DPI 下限以设备像素而非 DIP 判断。

## 实施结果与改动位置

### R15：SubVoice 的 Template Length 手柄

- 在 SubVoice 钢琴卷帘顶部标尺的模板尾端加入蓝色窄手柄；拖动时显示实际新长度和 delta。它编辑整个 Definition 的 Template Length，不是单个 SubVoice 的长度。
- 对冻结的原长度加吸附 delta，非 Snap 整倍的原长度不会先被强制对齐。下界覆盖所有 SubVoice 音符结束、瞬时事件/曲线点之后的必要 tick、Loop 两个端点及 Pre-Roll；到下界停止，不移动或删除音乐内容。
- 正常下界查询复用分页索引的修订摘要；只在摘要的极端 Int64 饱和边界执行完整算术核验。MouseMove 只使用按下时冻结的下界，不重新扫描模板。
- 按下时冻结 owner、修订、原长、下界和 Snap。Escape、失捕获、卸载、owner/revision 改变取消，松开一次正式原子命令；提交前还检查当前文档 PublicationRevision，避免 UI 尚未刷新时提交旧手势。
- 保留 Configurations 一次同时缩短 Template Length 与 Pre-Roll 的原能力：正式命令按 **最终 draft 的 Pre-Roll** 验证，不错误地用旧值阻止原本合法的操作。

主要文件：`TimelineSurface.TemplateLength.cs`、`TimelineSurface.cs`、`PresentationModels.cs`、`MainWindow.xaml/.cs`、`ProjectEventInstrumentEditCommands.cs`。

### R22：小节标尺固定抽样相位

- 标签候选由全局一基小节 ordinal 决定，Segment 使用 Project offset 映射；平移不再以当前视口左缘重新开始一套标签。
- 普通密度由拍号图的自然小节长度摘要确定 power-of-two 步长；非常密集的截短小节再按全局 Tick 桶限密。单个很短的截断小节不会让项目其余正常区域的标签全部变稀。
- 查找以直接跳跃为主，工作量受视口宽度限制，不从 tick 0 开始遍历。不改 Snap、网格线或音乐拍号语义；原本正确的纯 Tick 标尺分支不改。

主要文件：`ProjectMusicalTime.cs`、`TimelineGridPresentation.cs`、`TimelineSurface.cs`。

### R31：Compile 显示真实阶段进度

- 顶部显示例如 `Compile · Logical instances 37%`。百分比仅表示 **当前阶段** ，不是整个编译的预计剩余时间。未知可靠总量的阶段只显示名称。
- Logical tracks、MIDI roots、Logical instances 复用既有集合计数；验证、排序、范围处理、指纹和音频投影等阶段只显示名称。不为进度额外预扫描或展开结果。
- 旁路观察器仅保留最新阶段/整数百分比，不排队保存每条事件进度，不回调 Dispatcher。实例循环最多约每千分之一工作量检查一次进度；桌面侧约每 100 ms 读取一次。
- 每次编译尝试使用独立观察器；旧任务继续收尾不能覆盖当前任务。完成、失败、取消、关闭后清理进度。观察器不进入缓存身份、canonical、Project、Undo 或文件格式。
- 后台 Full/Incremental 与恢复 Full 使用同一旁路；原来的打开/导入任务弹窗没有被此功能替代。

主要文件：`CompilationProgress.cs`、`Contracts.cs`、`MidoraCompiler.cs`、`PureMidiCompilation.cs`、`ProjectCompilationSession.cs`、`DesktopSessionController.cs`、`MainWindow.xaml/.cs`。

### R32：All Tracks 首次垂直 Fit

- 首次有效布局扣除顶部 ruler 后，按实际 DPI 计算尽量容纳 128 个键的整数 device pixels/key，最小为 3；高度不足时保留下限并允许滚动。
- 初始定位到顶端；只初始化一次。Tab 卸载重建、resize 和 Raw/Compiled 切换不反复覆盖手调视口；有效恢复状态优先。
- 显式 Fit 也使用同一内容高度/DPI 算法，保持原来的水平 Fit 行为。

主要文件：`AllTracksView.xaml.cs`、`AllTracksWorkspaceViewModel.cs`、`TimelineSurface.cs`。

## 验证记录

环境：Windows x64、SDK 10.0.400、测试运行时 .NET 10.0.11。使用代码驱动的隐藏 WPF 控件/窗口测试，未使用 computer-use，未做人工视觉验收。

### 构建与回归

| 验证 | 结果 | 范围/口径 |
|---|---:|---|
| Desktop Release 构建 | 0 warning / 0 error | 仅 `dotnet build`，未发布到 dist |
| Desktop.Tests Debug 构建 | 0 warning / 0 error | 隐藏 WPF 测试基础 |
| Application 全套 | 1,254 / 1,254，6 分 53 秒 | 包含原子编辑、Undo、批量编辑等既有回归；验收工程测试随后新增 |
| Application 最终定向复跑 | 14 / 14 | 模板摘要、正式命令与新验收工程；其中 13 条与全套重复，新增 1 条 |
| Compiler Release | 575 / 575，约 17 秒 | 不含下面单独运行的计时实验 |
| Compiler 计时实验 | 1 / 1，约 39 秒 | 102.4 万 NoteOn，全流程进度开关对照 |
| Playback 全套 | 159 / 159，约 9 秒 | 含过期任务、失败、取消、Dispose 的进度隔离 |
| Presentation 全套 | 563 / 563，约 52 秒 | 含标尺、DPI 数学、手柄捕获和极端 Tick |
| Desktop 全套最终独立复跑 | 481 / 481，4 分 4 秒 | All Tracks 首布局/Tab 重建、绑定、快捷键及既有 UI 回归 |

按不同测试用例去重合计 **3,034** 条通过；这是上述多次运行的合计，不是一条命令同时运行全部项目的结果。

新增测试集中于 `TemplateLengthSummaryTests`、`CompilationProgressTests`、`CompilationProgressLifetimeTests`、`TimelineA4bTests`、`A4bWorkspaceTests`。既有 XAML 回归不再靠 Compile 按钮的固定文本定位，改用其不变的 Automation name，并继续检查原样式与禁用颜色。

覆盖的重点边界：

- 模板非整倍原长、正负 delta、合法最小值、全部 SubVoice/曲线/Loop 单端与双端/Pre-Roll、修改后摘要刷新、Int64 饱和与溢出拒绝。
- 手柄与已有手势互斥、真实 WPF 捕获/失捕获、Escape、界外坐标、取消无提交、松开单次提交。另经代码审查确认，提交入口有文档 PublicationRevision 比较，防止文档已改变而 UI 尚未重建时提交过期手势。
- 小节标签平移稳定、不同 Segment offset、正负局部坐标、拍号变化、极密截短小节、极端 Tick。
- All Tracks 零高度后首个有效布局、短窗口、100/125/150/200% DPI 数学、已有状态优先、真实 WPF 视图重建不重置。
- 进度开关下 Full/Incremental/缓存结果等价，未知总量、百万次 Report 的有界分配，取消/失败/被新修订取代/Dispose 清理。

### 测试过程中暴露并处理的问题

1. 新模板下界最初包含当前 Pre-Roll，暴露了原有“一次同时缩短两者”的回归。已改为正式 timing 命令验证最终 draft，原回归及本次全套通过。
2. 与大型 Application 测试并行时，Desktop 既有 `PagedHorizontalFlipRetainsLargeSelectionRenderGeometry` 出现一次 10 秒等待超时。未扩大超时、未修改该渲染路径；停止并行负载后独立完整运行 481 条通过。资源竞争是可能解释，不能把它当作已完全证明的唯一原因。
3. 验收工程最初用 resident `Events` 与存储布局相关 fingerprint 比较保存前后，这是不正确的测试口径：重开 Pure MIDI 后为分页 backing。最终改为比较完整 `QueryEventPages` canonical 序列、SMF descriptor，并检验同一重开 backing 的重复编译 fingerprint；保存/重开验证通过，未为此修改编译器语义。

### 时间与分配对照

进度实验：Release、先热身一次、同一构建中可选进度观察器关闭/开启交替测试，共 6 次。场景展开 **1,024,000 个 NoteOn** ，canonical 包含其 NoteOff 等完整事件。记录全流程 Compile，而不是只计量计数器调用。

| 顺序 | 观察器 | 编译耗时 | 累计托管分配字节 | 发布快照 |
|---:|---|---:|---:|---:|
| 1 | 关闭 | 5,171.92 ms | 1,240,319,920 | — |
| 2 | 开启 | 5,182.61 ms | 1,240,315,760 | 111 |
| 3 | 开启 | 5,290.46 ms | 1,240,321,504 | 111 |
| 4 | 关闭 | 5,497.01 ms | 1,240,320,040 | — |
| 5 | 关闭 | 5,409.05 ms | 1,240,316,880 | — |
| 6 | 开启 | 5,438.63 ms | 1,240,315,648 | 111 |

中位数：关闭 **5.409 秒** ，开启 **5.290 秒** 。约 −2.2% 应视为运行波动，不能宣称启用进度使编译变快；这组样本未测到明显时间退化。累计分配不等于同时驻留内存，分配差异也处于运行噪声范围。基准比较的是同一代码中是否附加观察器，未单独测量基础分支成本，也不包含真实 UI 绘制成本。

模板摘要：10 万事件，最后一次定向运行初次摘要 **14.330 ms** ；**10,000 次热查询共 34.561 ms / 4,720,000 bytes** 。手柄拖动使用冻结值，不执行这 10,000 次查询。冷构建/JIT 时间不能当作稳定百分位统计。

测试进程抽样未触及 9 GB 警戒线；这不是完整堆分析或精确峰值证明。本轮未执行 9KX2 全交互长时间 soak、跨真实显示器 DPI 切换、音频听感测试，亦未改变音频后端。视觉舒适性及真实编曲负载仍由下面人工验收确认。

### 可复跑入口

```powershell
dotnet build src/midora-desktop/Midora.Desktop/Midora.Desktop.csproj -c Release --no-restore -v minimal
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj --no-restore -v minimal
dotnet test src/midora-core/Midora.Compiler.Tests/Midora.Compiler.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName!~CompareLogicalCompilationWithAndWithoutProgress' -v minimal
dotnet test src/midora-core/Midora.Compiler.Tests/Midora.Compiler.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~CompareLogicalCompilationWithAndWithoutProgress' --logger 'console;verbosity=detailed'
dotnet test src/midora-core/Midora.Playback.Tests/Midora.Playback.Tests.csproj --no-restore -v minimal
dotnet test src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj --no-restore -v minimal
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj --no-restore -v minimal
```

建议逐项目运行，避免大型数据测试与 WPF 定时等待相互干扰。计时实验不设机器无关的毫秒性能承诺。

## 人工验收清单

五项均由用户于 2026-09-17 明确确认 **验收通过** 。以下保留原步骤供回归，本轮不自动进入后续阶段。

验收工程：`.tmp/uat/a4b/A4b-Template-Timeline-Verified.midora`（gitignored，Format 4，已保存/重开/编译验证；未覆盖用户工程）。可用源码启动当前版本打开它，不需要指定 SoundFont 即可测试编辑/编译。

准备：打开事件乐器 `Template 817 - minimum 601` 的 SubVoice。项目 TPQN=192；需要确定数值时，将 Snap 设为四分音符，即 192 ticks，并启用 Snap。模板初长 817、Pre-Roll=120、Loop=192～384；第二个 SubVoice 在 tick 600 有 CC11，因此整个 Definition 下界为 601。

| 编号 | 操作 | 应观察到的结果 |
|---|---|---|
| UAT-A4b-01 | 拖动 SubVoice 顶部标尺 tick 817 的蓝色尾手柄，先右移一格；Undo 后左移一格，再继续向左。 | 原长＋delta：右移一格 1009，左移一格 625；继续左移停在 601，而不是偷偷删事件或跳回 Snap 整倍。预览显示实际长度及 delta，松开一次 Undo。 |
| UAT-A4b-02 | 切换两个 SubVoice 检查长度一致；拖动时按 Escape，再尝试拖动到视图垂直范围外松开；测试 Undo/Redo。可另把 Pre-Roll 临时设为 700，再左拖。 | Escape 不改项目；合法界外松开只提交一次；两个 SubVoice/Configurations 同步。Pre-Roll=700 时下界变为 700，Loop 和事件位置不变。测试后 Undo 恢复。 |
| UAT-A4b-03 | 对实际大型项目执行 Compile，并编辑后观察后台 Incremental Compile；再观察一次有错误的编译。 | Compile 显示当前阶段，有可靠分母时显示该阶段百分比；完成/错误后恢复普通文字，无旧任务进度残留。小工程太快可能看不到中间阶段，不应为展示而强制延迟。速度无明显退化。 |
| UAT-A4b-04 | 在大窗口首次打开 All Tracks，再手动垂直缩放/滚动、切其他 Tab 后返回、调整窗口大小。也可关闭 All Tracks 后在小窗口重新打开。 | 首次尽量显示 key 0～127；不足 384 device pixels 时保留 3px/key 下限和滚动能力。手调后不会因切 Tab/resize/Raw-Compiled 切换被反复重置。 |
| UAT-A4b-05 | Arrangement 水平缩小后左右平移；再分别打开起点 1152 和 3072 的两个 Segment，以相近缩放平移，经过 tick 5000 拍号变化。 | 仍在可见范围内的小节标签保持同一全局抽样集合，不随每次平移轮换；局部时间与 Project 小节对齐。改变缩放时允许改变显示密度。 |

## 文档同步、非目标与剩余状态

- 已同步本任务执行页、总执行索引、需求总览/已批准决策的实施状态、Roadmap 及 SRS UI 相关条款；保留用户原答，不把自动测试写成人工验收通过。
- 未修改 Project Format 4、音频语义、逻辑编译事件语义、A4a Lines 行为或 `DEFER-LPARAM-01`。
- 未提交、推送、生成或覆盖 dist；验收工程只在 `.tmp/uat/a4b`。
