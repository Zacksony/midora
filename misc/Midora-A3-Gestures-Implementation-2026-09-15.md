# A3 统一手势与选择浮动工具实施记录

日期：2026-09-15。最新状态：**UAT-A3-01～10 及局部返修 R01～R06 已获用户验收通过** 。首轮返修、三行布局、图标裁切与抗锯齿、最终 15px / 空白菜单 / 标尺定位修订分别保留在 §5～8，最终验收原答见 §9。依据为 [A3 执行计划](Midora-Next-Development-Requirements-2026-09-11/execution/04-Gestures-and-Selection-Tools.md) 及 Q1/Q2 已确认回答。首次源码基线为干净 `e4db96b`（A2b 验收归档）；本次按用户要求记录并提交、推送完整 A3 成果，不本地发布、不启动下一阶段，不使用 computer-use。各实施轮中的“未提交”“待复核”等表述保留为当时状态。

## 需求 trace 与设计

- 输入：所属 Workspace/owner、不可变来源修订、选择修订、Pointer Down 世界坐标与修饰键。
- 输出：右键框选、指针和工具形态只改变会话状态；正式编辑复用现有 Project command、碰撞规约、一次 Undo 和结果选择。
- 手势：右键未拖动在松开时立即请求菜单，冷命中先显示禁用的 `Locating…`；超过既有拖动阈值转为框选，取消菜单查询。取消、失捕获、卸载、来源或选择失效后的迟到结果不得发布。
- 数值绘制：共享 Draw 主工具下增加独立 Free/Line/Horizontal 形态；左拖执行，Alt 强制当前形态，Shift 保留固定 Tick/单点语义。Velocity 只修改力度。Note/Segment/Inst. 不获得沿线创建能力。
- 顶部 ruler：Ctrl+左单击设 Edit Cursor，普通单击仍设 Playback Cursor；不恢复下部 ruler/SubVoice 的 Time Range。
- 浮动工具：跨主模式显示；13 个新增按钮使用既有菜单命令和能力判定；混合类型必须显式分组。保留 Move、两端 Resize、Follow/Pin/grip、实际预览和 delta。
- 滚屏：Move/Resize 共用 capture 生命周期内的 UI 调度；Pointer 世界坐标不夹到 viewport；鼠标停在边缘外仍更新，结束立即停止。不得用于音频时序。
- 持久化：不修改 Project Format、schema、canonical 或音频；工具 profile 的持久化属于后续 B。
- 非目标：全局图标替换、全局快捷键重定义、音乐编辑语义变更、只读 All Tracks 编辑。

## 已有文码差异核实

SRS §20.3.8 的旧简表将 Ctrl 框选写成 Toggle。历史 **ADR-UI-023** 已明确批准无修饰 Replace、Ctrl/Shift Add、Alt Remove、Ctrl+Alt Toggle，当前共享实现和调用遵循此决定。因此 A3 保留现有键义，同步 SRS 简表；这不是本轮新选定的键义。

原 §20.7.9.1 固定 300 ms 右键双击、右键绘线及 §20.4.12 Select 专属浮动工具由本次已批准合同替代。已同步 SRS §18/§20、INV-097/098 和 ADR-UI-045；原问题、用户原答、Project Format 4 与音乐语义没有改写。

## 1. 已修改项与路径

所有下列路径均相对仓库；源码根为 `src/midora-desktop/`。

| 任务／需求 | 实现 | 主要路径 |
|---|---|---|
| T-GEST-01／R18～21 | 核实旧合同、主工具／形态／上下文命令边界，保留原修饰键与正式命令 | SRS §18/§20/§22、ADR-UI-045、本报告 |
| T-GEST-02／R18 | 移除右双击切模式和人为等待；Down 冻结，Up 菜单，冷数据先 `Locating…`；空白不隐式操作旧选择 | `Midora.Desktop.Presentation/Controls/TimelineSurface.cs`、`.Gestures.cs`；`Midora.Desktop/InstrumentChangeLane.Interaction.cs` |
| T-GEST-03／R18 | 各主模式右拖框选；Velocity 对应 Note；Inst. 固定 y；排除琴键、轨头、brace、ruler、只读 All Tracks | 同上、`Interaction/TimelineToolPolicy.cs` |
| T-GEST-04／R18 | 三个图标 Free/Line/Horizontal；左拖、Alt 强制当前形态，非 Draw 禁用但记忆形态；适用宿主共享原采样／提交路径 | `Midora.Desktop/ValueTraceShapeSelector.xaml(.cs)`、`LaneTabHeader.xaml(.cs)`、`MainWindow.xaml`、`ConductorWorkspaceView.xaml(.cs)`、`PresentationModels.cs` |
| T-GEST-05／R19 | 顶部 Ctrl+左单击 Edit Cursor，保留选择；plain 为 Playback；补 Tooltip，不恢复被禁止的时间范围 | `TimelineSurface.cs`、`MainWindow.xaml.cs` |
| T-GEST-06／R20 | 跨模式 13 命令，已有 Move/左右 Resize/Follow/Pin/grip 保留；小视口换行和内部滚动；混合显式类型分组 | `TimelineSurface.SelectionActions.cs`、`Interaction/TimelineSelectionActions.cs`、`MainWindow.SelectionActions.cs`、`MainWindow.TimelineObjectList.cs`、`InstrumentChangeLane.SelectionTool.cs` |
| T-GEST-07／R21 | 普通／浮动 Move、Copy Move、Resize 使用共同世界坐标；33ms capture 边缘调度；松开按最终 pointer 取坐标，不重建预览选择 | `TimelineSurface.Gestures.cs`、`TimelineSurface.cs`、`InstrumentChangeLane.SelectionTool.cs` |
| T-GEST-08／R18/20/21 | 同一正式选择、共享预览及原命令；source/selection 失效取消；自身提交刷新不误取消自身手势 | 上述共享 Surface 和菜单 adapter；三类宿主实际模板回归 |
| T-GEST-09／全部 | 自动测试、规模测量、SRS/ADR、图标 notice、复用验收工程、一轮 UAT 清单 | 本报告、执行文档、Presentation/Desktop Tests、StyleGallery FluentSystemIcons/THIRD-PARTY-NOTICES |

浮动命令的能力不是另一套业务模型：普通表面从原菜单构建命令能力，按 source/selection/editability 缓存；混合选择调用既有分类菜单，选择具体子类型后才执行。菜单项和浮动按钮共用相同 handler。Inst. 仅开放包装已有的 Copy/Cut/Delete/水平翻转/Scale/Quantize/Properties；其余图标保留但禁用，不赋予包装不存在的音符能力。

新增 Geometry 来自既有 notices 固定的 Fluent System Icons revision `0a92ff83f03fa5319edaf0e2b2a09e460b69091a`，同步记录名称；没有引入另一套图标库或依赖。

## 2. 手势、取消与所有权

| 阶段 | 行为／终止门 |
|---|---|
| Right Down | 冻结 source、选择修订、tick/lane/value 投影与修饰键；不先改选；冷数据后台命中。 |
| 未超过阈值 → Right Up | 热结果直接菜单；冷结果立即展示禁用占位，只有当前请求可填入最初目标。不依赖再次 hover。 |
| 超过阈值 | 取消菜单请求，转世界坐标框选；只在 Up 发布选择。主模式不改变，旧右键直线能力由左键形态承接。 |
| 菜单关闭 | 监听 `IsOpen=false` 立即取消，不能等待可能被动画延迟的 `Closed`。未完成命中不得重开菜单或复活选择；来源修订或卸载也关闭已经完成定位的菜单。 |
| Move/Resize | 普通和浮动使用相同准备与预览；Ctrl/Shift/操作类型在 Down 冻结。viewport 裁切画面，但不截断编辑世界坐标。 |
| 边缘静止 | capture 内定时器继续调 viewport 和生效 delta；回内侧、Up、Escape、失捕获、卸载、禁编辑、来源／选择失效后停止。 |
| 自身发布 | 正式 ItemEditCompleted 内可能同步刷新选择；仅此调用期间不把它当外部失效。外部修订仍取消。 |
| 浮动菜单 | 只复用冻结 owner/选择的原能力；混合菜单异步完成后再次检查 source/selection/当前工作区；不能暗中取其中一种。 |

右键点击内容区空白（包括实际轨道／琴键区域之外的留白）不清空原对象选择，但菜单按空白容器／位置构造，不提供旧选择的批量命令；Arrangement 空白取消轨道头选择的既有规则保持。

时间性能路径没有改成逐对象 WPF 控件。工具本身是固定 13 个动作与少量控制项；已有分页查询、数值绘制、移动预览、缩放预览栅格和正式有界编辑仍复用。Inst. 的工具锚点从已建立的选择索引取第一个对象，不为工具构造完整选区数组。

## 3. 自动验证与实际结果

测试均通过命令行自动运行；HwndSource/真实 WPF 模板测试不是 computer-use，也不是人工视觉验收。产物留在 gitignored `.tmp/test-results/a3/`。

| 验证 | 结果／产物 |
|---|---|
| 修改前 Presentation 全套 | 482/482，13 秒；`a3-baseline-presentation.trx`。 |
| 最终 Presentation 全套 | 512/512，39 秒；`a3-presentation-final-5.trx`。含新增规模及已就绪菜单失效测试，不能把总耗时与 482 项基线直接当成产品性能差值。 |
| Desktop 全套 | 458/458，2分21秒（`a3-desktop-final-2.trx`）；末次菜单生命周期补强后，实际模板测试另跑 1/1，20秒（`a3-final-real-templates.trx`）。 |
| 真实 MainWindow 模板 | 三类宿主的形态禁用／恢复／绑定、全部 13 命令与图标、混合菜单、空白菜单保留选择；独立 `a3-real-templates-3.trx` 通过，并纳入 Desktop 全套。 |
| Desktop Release 构建 | 最终 22.24 秒，0 警告、0 错误；仅 `dotnet build`，不生成 dist、不发布 Worker。 |
| 原正式编辑核心回归 | 58/58，10秒，选择变换／混合删除／剪贴板／分页事务；`a3-application-regression.trx`。 |

新增的主要断言包括：5 种 surface × 4 主模式 × 5 框选修饰组合；Ctrl+ruler 保持选择与禁止时间范围；Free/Line/Horizontal、Alt/Shift、Snap1/8；冷热菜单和关闭竞态；四种主模式的 13 项布局及短窗口逐项可达；source/selection 失效零提交；真正捕获后鼠标静止的水平／垂直滚屏与停止；缩放后的 value 世界坐标；实际轨道外留白菜单。

运行命令：

```powershell
dotnet test src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj --no-restore
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj --no-restore
dotnet build src/midora-desktop/Midora.Desktop/Midora.Desktop.csproj -c Release --no-restore
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~MixedTimelineSelectionCommandsTests|FullyQualifiedName~ProjectSelectionTransformEditCommandsTests|FullyQualifiedName~ProjectObjectClipboardPureMidiTests|FullyQualifiedName~PagedSelectionAndEditTransactionTests'
```

### 3.1 性能测量的范围

以下来自 `a3-presentation-final-4.trx`，Debug 同机合成数据；后续 final-5 补菜单失效测试也已通过。工具表的数字是 **1000 次布局与能力判定总耗时** ，不是单帧；输入选择已准备好，不能当成百万选择创建耗时。

| 选择数量 | 1000 次布局／判定 | 该循环托管分配 |
|---:|---:|---:|
| 1 | 14.28 ms | 2,136,640 bytes |
| 10 | 11.45 ms | 2,136,040 bytes |
| 400,000 | 68.47 ms | 2,136,640 bytes |
| 1,000,000 | 12.75 ms | 2,136,040 bytes |

这说明该路径不随选择数线性分配；40万样本时的调度波动没有解释为算法复杂度增长。1000次总数不能用于声称 p95/p99 或完整 UI 帧率。

对原共享 Resize raster 扩大合成数据，实际遍历全部候选且验证输出非空：

| 类型 | 40万候选，一瓦片 | 100万候选，一瓦片 | 每次 raster 分配 |
|---|---:|---:|---:|
| Logical Note | 162.75 ms | 385.43 ms | 801,184 bytes |
| Direct MIDI Note | 164.63 ms | 597.71 ms | 801,184 bytes |
| SubVoice Template Note | 220.57 ms | 631.39 ms | 801,184 bytes |

上述是栅格生产耗时，不是阻塞 UI 的时间；不包含测试 fixture 的全量合成数组、snapshot/选择构造、正式编辑或完整进程内存。生产代码原有后台预览与有界缓存未被替换，不能据此承诺百万选区每次移动在一帧内产生完整新栅格。

原 6万候选 × 48 次连续 Resize 回归：

| 类型 | p50 / p95 / max | 48帧托管分配 | WS差值 |
|---|---|---:|---:|
| Logical | 21.10 / 23.08 / 26.36 ms | 38,465,664 bytes | +14,819,328 bytes |
| Direct MIDI | 21.66 / 25.96 / 33.59 ms | 38,465,664 bytes | +32,665,600 bytes |
| Template | 22.75 / 30.36 / 31.03 ms | 38,468,040 bytes | +8,343,552 bytes |

三类均通过原有 p95 <250ms、累计分配 <128MiB、WS增长 <256MiB 门；不是把它们设为新的产品帧率目标。没有相同完整轨迹的9KX2前后 p99/UI停顿/进程树基线，故不宣称实机性能收益或零退化。测试进程一次采样约 0.86GiB WS、系统报告历史峰值约 1.58GiB；不是连续峰值追踪，也不是项目内存预算。

### 3.2 回归中实际发现并处理的问题

- 更新旧右双击、Shift+右直线、Select 专属浮动工具断言，使其对应本轮批准合同；没有削弱音乐编辑断言。
- 冷 Conductor 复演已有 capture；一度被新 capture 互斥门误拒，已将带冻结复演位置的流程排除，并回归。
- 关闭 Popup 的 `Closed` 可能延后，真实取消竞态暴露过迟到结果；改为 `IsOpen` 立即取消并连续重复验证。
- 合成测试手工安装 drag/预览时未冻结 source/选择，新失效保护取消它；补齐测试前置，保留原预览身份／结果断言。
- 短视口工具滚动测试必须使 WPF visual 失效再取新帧；否则测试读到旧布局。现已验证 13 项全部可达。
- 最终复核 MouseUp 坐标时避免重做 `PrepareDragPreviewSelection`，只共享坐标换算；保留 A1 多点共同饱和预览=提交断言。
- 自动测试的原生 Popup 关闭动画尚未结束就关闭 STA，曾发生测试清理超时；现在明确等待 Closed/native popup 清理后拆 host，再退出 Dispatcher。最终全套通过；早期失败 TRX 保留，不计作通过。
- 末次 WPF 定向补测曾以 helper 类名过滤，匹配到 0 项（`a3-final-menu-integration.trx`），不计为验证；改用实际承载它的 `WpfInteractionRegressionTests.ComboAndScrollBarThemesProvideDedicatedTemplatesAndChevronGeometry`，最终 1/1 通过。

## 4. 人工验收与仍未覆盖的工程门

首次交付使用 [UAT-A3-01～10](Midora-Next-Development-Requirements-2026-09-11/execution/04-Gestures-and-Selection-Tools.md#uat-a3手势与浮动工具)；本次只复核 §5 局部清单。复用 `.tmp/uat/a2b/A2b-Lanes-and-Instruments.midora`，其存在已确认；本轮没有修改此工程。它包含三种宿主、音符、CC 与 Inst.；Tempo 可在 Conductor 加两三个点或用既有工程。

本轮实现没有缩减以下范围，但未把测试穷举完整：

1. 9KX2 全工程、40万／百万真实编辑下的整机输入延迟、冷页首预览、p99、长时间缓存／关闭释放；上面只有有界工具与预览栅格规模测量。
2. 真机多 DPI／系统字体／鼠标硬件、菜单边缘位置、主窗口最小尺寸的人工视觉和手感。
3. 全部按钮 × 全类型 × 全修饰键 × 播放／模态 × I/O故障的笛卡尔积；实际模板路由与既有核心事务回归不等于穷尽组合。
4. B 的同 Track 工具 profile、跨重启保存／恢复尚未实施：A3 形态保存在 Workspace 会话内，SubVoice 各自记忆；关闭工作区或重开项目不承诺恢复。
5. 没有 BASS 听感、音频压力、MIDI 导出全回归或格式迁移新测试。本轮未改这些实现、canonical 或 schema。

没有自动继续 A4/B；人工通过前不标记 A3 已验收。
## 5. A3 验收返修记录（2026-09-15）

用户确认：除 UAT-A3-09 外，其他项目均验收通过。本次不扩大到其他功能，不提交、推送或发布。

| 编号 | 输入与正式结果 | 边界及验证 |
|---|---|---|
| A3-R01 | 浮动工具改为固定分组、多行；按钮 36 DIP，图标可见轮廓最长边约 22 DIP | 所有 Timeline 和 Inst.；窄/矮视图内部滚动，不按选区规模增加控件 |
| A3-R02 | 工具顶部常驻名称栏，命中按钮立即显示名称，不依赖 ToolTip Popup | 禁用按钮也可读；切换/离开立即更新，不遍历选中对象 |
| A3-R03 | 独立末行 Deselect All，使用 SelectObjectSkewDismissRegular | 清空整个 Workspace 选择，不删除对象、不进 Undo；只读时仍可用 |
| A3-R04 | 三种 Piano Roll 的 Ctrl / Ctrl+Alt Copy+Move 使用与 Move 相同的未裁切 key delta，丢弃 key 不在 0–127 的副本 | 源音符保留，合法副本与碰撞幸存者被选择；小集合与有界分页路径一致；全部越界为无新增；事件 value Clamp 不变 |

这是用户明确要求的 Note Copy pitch 边界修订，取代此前整组 pitch clamp；共同 tick/Track 约束、失败原子性、取消、Undo/Redo、Direct NoteOff velocity、canonical 与持久化模型不变。

### 5.1 根因与实现

- 图标原先被限制在小单行布局中，且按 SVG 画板再缩放，实际可见轮廓比指定尺寸更小。现在用可见 Geometry bounds 适配约 22 DIP，并以 36 DIP 命中格分组：移动/两端 Resize/Follow，剪贴板/删除/属性，翻转/Scale/移调，其余批量工具，独立末行 Deselect All。Inst. 使用同一尺寸及分组定义。
- 原 ToolTip 依赖整个画布共享的 Popup、延迟和鼠标捕获状态。现在工具顶栏直接绘制当前命中名称，禁用按钮也可读；名称不影响布局。顶栏兼作手动拖动区，矮视图只滚动其下按钮区。
- Copy pitch 错误不只在浮动工具：预览层和 Logical/SubVoice Desktop adapter 仍有整组 pitch Clamp，Direct 小集合则把非法 key 传给严格验证器。因此不能用“捕获异常”修补；现统一取消 Note Copy pitch Clamp，在三种正式复制命令的小集合和有界分页分支中先过滤非法目标，再验证/分配 ID。原音符不修改，精确碰撞仍保留原占位者，仅选择幸存副本；全部越界不新增数据/Undo/ID，清空结果选择。
- `TimelineSurface` 的事件 `ValueDelta` 共同饱和路径与 Event/Parameter 正式命令未改；不能把 Note 的越界丢弃应用到事件值。

主要文件：`TimelineSelectionActions.cs`、`TimelineSurface.SelectionActions.cs`、`TimelineSurface.cs`、`InstrumentChangeLane.SelectionTool.cs`、`MainWindow.SelectionActions.cs`、`MainWindow.xaml.cs`、`MainWindow.InstrumentChangeEditing.cs`；三类复制对应 `ProjectPureMidiTimelineEditCommands.cs`、`ProjectBatchTimelineEditCommands.cs`、`ProjectTemplateNoteBatchEditCommands.cs` 与 `BoundedLogicalNoteCommands.cs`。图标来自既有固定 Fluent revision，已同步现有 notices。

### 5.2 返修验证

Release 模式产物与 TRX 位于仓库忽略目录 `.tmp/test-results/a3-repair/`；不是 `dist` 发布。

| 验证 | 结果与证据 |
|---|---|
| 三类 Note 复制边界专项 | 24/24；`a3-r-copy-boundaries-2.trx`。10/4101 个选择，跨小集合/有界路径，向上/向下、部分/全部越界、碰撞、源保留、精确字段与 Direct NoteOff velocity、ID、Undo/Redo、内存预算。 |
| Application 定向回归 | 162/162；`a3-r-application-regression.trx`。包含上面 24 项，以及有限 Logical、批量编辑、Pure MIDI 剪贴板、A1、碰撞和 Inst.。不是 162+24 个不同测试。 |
| Presentation 全套 | 521/521；`a3-r-presentation-final.trx`。全部 14 命令布局/滚动可达、禁用 hover 名称、Deselect 路由、三种 Note Ctrl/Ctrl+Alt 预览、事件共同 Clamp、真实主题 Geometry 及其余渲染/手势回归。 |
| Desktop 全套 | 458/458；`a3-r-desktop-full.trx`，2 分 42 秒。包含实际模板、选择后态、快捷键、宿主生命周期及既有 Desktop 回归；下面独立模板测试属于此套件，不重复计数。 |
| 真实 MainWindow 模板/提交集成 | 1/1；`a3-r-desktop-adapters-3.trx`。承载测试中执行三类 Note × 4 种 pitch delta × Ctrl/Ctrl+Alt 共 24 组实际 Desktop 提交；逐组检查源、副本、结果选择和 Undo。另核实 Inst. 16 个按钮、36/22 DIP、禁用按钮名称及实际模板资源。 |

实际窗口测试使用不可见 HwndSource 与受控 Dispatcher，不是 computer-use。新增 24 组提交/布局/Undo 后，单个承载测试运行 87 秒，原 30 秒总时限不足；仅该集成测试上限调整到 120 秒，单操作已有等待门未放宽。日志中的单组耗时包含全主窗口模板刷新、Dispatcher drain、Undo 与后台渲染，不能当成音符复制算法性能。早期失败保留：测试最初未套正式碰撞 wrapper、误用字段反射访问属性，以及把 SubVoice 事件投影错设为 TemplateEvent；均修正测试前置后重跑，没有削弱结果断言或改变事件 Clamp。

工具 1000 次布局及能力判定（已建立选择，不含选择构建/正式编辑）：1/10/40万/100万分别为 12.86/16.69/10.13/16.57 ms，分配约 2.13 MB，选区数量不改变工具规模。来自 final TRX；不是整机 UI 帧率或真实百万编辑测量。

离屏 96 DPI PNG 位于 Presentation Tests 的 `bin/Release/net10.0-windows/win-x64/.tmp/a3-visual/selection-toolbar.png`。已目视检查固定多行、可辨图标、顶栏名称和独立 Deselect 末行；未替代多 DPI 真机视觉验收。无需重验全部 A3，只需 [UAT-A3-R01～R04](Midora-Next-Development-Requirements-2026-09-11/execution/04-Gestures-and-Selection-Tools.md#51-首轮人工验收及返修)。§4 其余工程缺测保持，不自动继续下一阶段。

## 6. 浮动工具紧凑布局修订（2026-09-15）

此节取代 §5 的 22 DIP 图标、五组按钮和独立 Deselect 末行布局；不改变上一轮正式编辑与选择规则。输入是现有有效选择和用户明确指定的图标/行数，输出仅为 UI 布局与 Geometry。Pin 会话状态、命令能力、失败处理、选择来源、Undo/Redo、正式 Project、编译/音频及持久化均不变，不增加诊断或新业务接口。

### 6.1 实施范围

- 统一图标最长可见边为 16 DIP，保留 36 DIP 按钮命中格。既有固定 Fluent revision 中新增十四个原生 16px Regular Geometry，并更新原有第三方 notices；没有升级图标库、增加依赖或修改普通菜单图标。
- Pin 恒用 `Pin16Regular`，原启用背景保持；左右边界为 `ArrowExportRtl16Regular` / `ArrowExportLtr16Regular`，Move 为 `ArrowMove20Regular` 缩放到 16 DIP。Move、Transpose、Properties、Deselect All 在固定 revision 的 16px 路径返回 404，因此这些使用既有同款 20px 资源缩放。
- 按钮最多显示三行，顶栏名称仍独立固定。正常宽度下排列如下，事件点/Inst. 没有左右边界按钮，第一行余项居中：

| 行 | 从左到右 |
|---|---|
| 1 | Pin、左边界、右边界、Move、Properties、Deselect All |
| 2 | Copy、Cut、Delete、Flip Horizontal、Flip Vertical、Scale |
| 3 | Transpose、Batch Edit、Humanize、Split、Join、Quantize |

- TimelineSurface 与 Inst. 共用尺寸、分组和图标选择定义。窄视口内部换行，但可见按钮区高度上限仍为三行；矮视口也可在固定名称栏下滚动到全部按钮。不按选区数量增加控件、布局或查询。
- 本轮源码只涉及 `TimelineSelectionActions.cs`、`TimelineSurface.cs`、`TimelineSurface.SelectionActions.cs`、`InstrumentChangeLane.SelectionTool.cs`、共享 Geometry/notices 与对应测试。现有工作区其他 A3 / Copy pitch 改动保持。

### 6.2 验证与验收范围

本次 Release 测试产物在 `.tmp/test-results/a3-compact/`，不生成 `dist`。真实模板测试采用不可见 HwndSource / 受控 Dispatcher，不使用 computer-use。

| 验证 | 本次结果 |
|---|---|
| A3 Presentation 专项 | 46/46，`presentation-a3-compact.trx`；四主模式的三行布局、Deselect 首行末尾、36/16 DIP、矮视图命令可达、实际主题资源与既有交互回归。属于下面全套，不重复计数。 |
| Presentation 全套 | 521/521，`presentation-compact-full.trx`。 |
| Desktop 全套 | 458/458，`desktop-compact-full.trx`，3 分 3 秒。包含真实 MainWindow/Inst. 模板：三行、首行最后 Deselect、16px 资源、36 DIP 命中格、禁用按钮即时名称，以及三类 Note Ctrl/Ctrl+Alt 提交/Undo。 |

两个完整套件合计 979/979 通过；构建随测试完成，未新增编译警告/错误。本轮没有再运行 Application 核心全套；未修改核心命令，§5 的核心验证是上一轮记录，不计入本次测试数。

已目视检查新生成的 96 DPI 离屏 PNG：Pin/左右边界/Move 已换，按钮分为三行，Deselect 位于第一行末尾，名称栏未移动按钮。此 PNG 使用与 §5 同一测试路径，由本次测试覆盖；不是实机多 DPI 验收。所有 Geometry key 无重复，`git diff --check` 通过。

本次人工只需复核更新后的 UAT-A3-R01/R02：16px 图标与三行布局、Pin 背景、即时名称和取消选择位置。上一轮尚待确认的 R03/R04 状态保留，不将本轮局部视觉测试等同于新用户验收。

## 7. 菜单图标裁切、浮动图标抗锯齿与留白（2026-09-15）

### 7.1 需求 trace 与根因

- 输入/输出：现有 MenuItem、浮动工具和相同命令，输出只改变图标可见区域、抗锯齿和留白；所有宿主、菜单能力、命中动作与编辑语义保持。
- 菜单确认根因：共享模板的图标列为 18 DIP，图标 ContentPresenter 右侧 Margin 为 7 DIP，留下 11 DIP，无法容纳实际 16 DIP 图标。改为 28 DIP 列，其中 20 DIP 供图标、8 DIP 与正文隔开。对勾同列对齐，顶级菜单继续隐藏图标列；没有改菜单图标 Geometry、颜色或平滑方式。
- 浮动工具确认根因：TimelineSurface 为音符、网格启用 `EdgeMode.Aliased` / `NearestNeighbor`；Fluent 路径直接画入同一 DrawingContext，也失去了抗锯齿。菜单/Inst. 的 FluentIcon 是正常 WPF 控件，不走该路径。
- 方案边界：不切换整个 Timeline 的 EdgeMode，不重构音符/事件栅格，不增加选区物化或后台任务。只对最多十八个工具图标建立当前设备 DPI 的正常 WPF 抗锯齿小位图，再按设备像素对齐 1:1 绘制；含一像素透明余量，避免图形边缘被位图边界裁掉。
- 资源与失败归属：缓存不持久化、不进 Undo/Project/编译；每个工具只保留一个当前 DPI/Geometry/Brush 项，资源变化失效，Unloaded 清空并解除监听。图标键数量有硬上限，不按时间线缩放或选择规模建多层。既有资源缺失时保持原跳过行为，未增加音乐诊断或数据写入。

### 7.2 改动

- 16 DIP 图标不变；按钮从 36 收紧为 28 DIP，即四周留白从 10 降为 6 DIP；组间距 4→2 DIP，外边距 6→4 DIP。普通六列工具整体从 228×156 收紧为 176×124 DIP，名称栏仍 28 DIP，三行与原动作顺序不变。
- Inst. 同步使用以上常量；仅在内部确需滚动时预留滚动条空间，正常三行不额外留空。原 FluentIcon 平滑渲染保持，不重复栅格化。
- 实现文件：`Midora.Desktop.StyleGallery/Themes/Controls.xaml`、`TimelineSelectionActions.cs`、`TimelineSelectionIconCache.cs`、`TimelineSurface.SelectionActions.cs`、`TimelineSurface.cs`、`InstrumentChangeLane.SelectionTool.cs`；回归分别在 Presentation 的 A3/Glyph 测试及 Desktop 的 A3/共享菜单模板测试。
- 同步 §20.4.12、§20.15.4、INV-098、ADR 小决定与 UAT-A3-R01。没有改核心编辑文件、音频、格式、图标来源或依赖；既有未提交 A3 修改均保留。

### 7.3 自动验证

本轮 Release TRX 位于 `.tmp/test-results/a3-polish/`。

| 验证 | 证据 |
|---|---|
| 图标抗锯齿及缓存专项 | 6/6，`presentation-polish-icons-4.trx`。100%/125%/150%/200% DPI 的半透明边缘像素保留；同一 UIElement 直接画路径的旧模式为硬边，新模式保留抗锯齿。包含位置像素对齐、DPI/颜色/Geometry 变化、缓存上限与事件解绑。 |
| Presentation 全套 | 527/527，`presentation-polish-full.trx`，25 秒。包含上面六项、三行紧凑命中布局、所有动作/滚动可达、真实资源、旧有手势/渲染及事件 Clamp 回归。 |
| 真实菜单/MainWindow/Inst. 模板 | 1/1，`desktop-polish-templates.trx`，1 分 24 秒。28 个实际 MenuItem（14 种图标×16/20 DIP）逐项检查完整宽高、图文间距、Disabled、Checked；顶级菜单零宽保持。包含 Inst. 28 DIP 按钮与原正式提交/Undo 集成检查。 |
| Desktop 全套 | 458/458，`desktop-polish-full.trx`，3 分 5 秒。包含上述模板测试，以及其余 Desktop 功能/布局/提交回归。 |

本次两个完整套件合计 985/985 通过，专项是其子集，不重复相加；构建随测试完成，`git diff --check` 通过。未使用 computer-use，未提交/推送/发布。

缓存独立测量：18,000 次热读取为 3.04 ms、0 字节托管分配；100% DPI 十八个图标的像素数据为 23,328 bytes，200% 时小于 128 KiB。这不是整个 WPF 资源占用或 UI 帧率，未计 WPF 对象/合成开销，也不代表首次初始化零耗时。

首轮新测试的失败均保留在早期 TRX：抗锯齿测试最初错误地用裸 DrawingVisual 作为 Aliased 宿主（没有 UIElement 的 RenderOptions 回调），改为与 TimelineSurface 一致的 FrameworkElement 后仍保留旧模式零半透明像素、新模式有半透明像素的严格对比；可变 Geometry fixture 补 Clone，热路径分配测量排除首次 JIT/计时初始化。没有放宽编辑断言或通过关闭时间线 Aliased 来使测试通过。

已检查实际 96 DPI 离屏菜单/工具图像：裁切消失、平滑图标可见、三行收紧且标题完整。图像分别在 Desktop / Presentation Tests 的 `bin/Release/net10.0-windows/win-x64/.tmp/a3-visual/` 下。此结果不替代真机多 DPI 手感验收；人工只复核菜单图标完整度、浮动图标平滑度与间距，其他编辑行为不需重走全套。

## 8. 15px 浮动图标、空白选区菜单与标尺菜单定位（2026-09-15）

### 8.1 需求 trace 与实施边界

- 本轮用户明确将图标调整为 15 DIP；按钮相应为 27 DIP，使图标两侧各留 6 DIP，普通 Timeline 与 Inst. 共用。保留上一轮的局部抗锯齿、三行分组、即时名称和有界缓存，不改音符/事件图形渲染。
- 本轮明确取代 A3 初始“空白菜单只针对容器、不操作既有选区”的决定：内容空白右击保留选择，并提供该有效选区的完整菜单；无选择才回到容器菜单。混合 Note/Event 继续显式分类，不跳过其他类型。轨道头/brace/钢琴键盘等专用区域的语义不变。
- 定位问题：内容区复用 ContextMenu，以 RelativePoint 和局部 X/Y 偏移手动打开；之后 ruler/header 走 WPF ContextMenuService。不得把上次局部偏移再加到新的鼠标屏幕锚点上。原生打开前重新建立原生 placement，清除旧 rectangle/offset；冻结当前位置用于菜单命令。
- 输入仅为已有会话选区/手势/控件位置，输出为菜单可用性与视觉定位；不新增 Domain、诊断、文件字段、Undo 或音频语义。冷查询取消与 source/selection revision 校验不放宽。未提交/推送/发布，不使用 computer-use。

### 8.2 验证与人工复核

Release TRX 位于 `.tmp/test-results/a3-menu/`。本轮增加 `TimelineContextMenuPlacementTests`，扩展 `TimelineA3GestureTests`、`TimelineSelectionIconTests` 和真实 MainWindow/Inst. 集成检查。

- Presentation 全套 533/533 通过，`presentation-a3-menu-full.trx`，29 秒。专项 `presentation-a3-menu-2.trx` 为其中 58 项，不重复相加。
- 原生位置专项 6 项：Piano/Event/Velocity/Arrangement，另对 Piano/Event 加 1.5× 宿主变换；通过 WPF 原生 ContextMenuService、owner 属性 coercion 和 Popup HWND 开菜单，重复“内容→标尺”两次，检查菜单实际屏幕位置相对锚点误差不超过 2 device pixels，旧 X/Y 偏移归零。隐藏 HwndSource，未移动用户鼠标；这不是多显示器实机 DPI 验收。
- 图标专项保持 100%/125%/150%/200% DPI 抗锯齿与当前资源有界缓存检查，新增居中误差最多半个设备像素；15 DIP 逻辑尺寸和 27 DIP 按钮两侧留白准确。普通六列工具现在为 170×121 DIP；已检查离屏图像。
- 真实主窗口检查三类 Piano 的单一/混合选区空白菜单、无选区回退、各批量命令可用性与不改选择/Undo；Inst. 的空白有选择/无选择菜单分别启用/禁用原命令，图标实际中心与按钮中心一致。新增相邻区域检查：Note 选择在下方 Event 空白右击仍按选中的 Note 类型解析，不把 Note 当 value。

测试过程保留：第一轮新位置测试曾有测试代码命名空间笔误，编译前后已修正，不是生产代码失败。首轮真实模板矩阵在 120 秒总时限超时；保持生产实现与功能断言不变、补执行阶段定位后，第二轮同矩阵于 108 秒通过。该单个测试实际串行包含整套 MainWindow、24 个 Copy/Undo 分支及新增多次真实 Popup；本轮将其聚合时限设为 180 秒，单个异步就绪断言仍为 8 秒，不作为软件单次操作性能门。初次 Desktop 全套 458/458 于 172 秒通过；相邻区域补充分支的最终全套结果另行记录，不能据重跑通过反推首轮超时的唯一原因。

人工只复核 R01/R05/R06：15px 居中与三行间距；有选区时空白右击完整菜单；连续“内容右击→标尺右击→内容右击”菜单靠近本次点击。混合选择仍分类、无选区仍是容器菜单。未将本轮结果标记为用户已验收。

相邻区域补测的 `desktop-a3-menu-full-2.trx` 为 457/458：唯一失败是夹具试图直接找到尚未激活的 Event Tab，违反 A2b 单活动内容懒创建的既有设计。修正为通过真实 LaneTabSession 激活目标、检查后恢复原 Lane；没有为了测试创建额外常驻控件或修改生产模型。其余 457 项在最新生产代码上已通过。

最终 `desktop-a3-menu-3.trx` 定向复测该真实模板矩阵 **1/1 通过（115 秒）**，包含三类宿主的跨区域菜单、正常/混合/无选择、Inst. 空白选择和居中检查。与最新全套其余 457 项及 Presentation 533 项合并，991 个独立测试均有通过结果；不是宣称最后一次单独命令执行了完整 991 项。Release 构建及最终 `git diff --check` 通过。没有改音频/核心编辑/格式，也没有提交、推送、本地发布或使用 computer-use。

## 9. 最终人工验收归档（2026-09-15）

用户原答：`验收通过。记录、提交、推送。`

- 结合首轮已通过项与后续返修确认，UAT-A3-01～10、UAT-A3-R01～R06 全部标记为人工验收通过；本次不要求重复验收已通过部分。
- 最终 UI 为 15 DIP 图标、27 DIP 居中按钮及最多三行工具；有有效选区时内容空白右击使用完整选区菜单，标尺菜单不再叠加上次内容点击偏移。三类 Note Copy 越界丢弃、事件 Clamp 不变及此前 A3 手势功能一并归档。
- 本次仅更新验收记录，并提交、推送此前累计的 A3 源码、回归测试、SRS、ADR、图标 notices 和执行文档；不新增产品行为、不改变版本或格式、不生成 dist、不使用 computer-use。
- 自动验证仍以 §5～8 的实际运行记录为准，保留测试失败、修正及定向复测过程；本次文档归档不冒充重新执行全套测试。完整 9KX2 整机帧时序、多 DPI 实机视觉、音频听感和全部故障交叉组合仍未完成，人工验收不将这些工程缺测自动升级为已通过。工具 profile 持久化仍属于后续 B，未提前实施。
