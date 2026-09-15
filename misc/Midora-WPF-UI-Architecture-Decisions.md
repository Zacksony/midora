# Midora WPF UI Architecture Decisions

状态：Accepted for implementation
创建日期：2026-08-08
范围：正式 WPF UI，不改变 SRS、Domain、Compiler、Canonical Result 或消费者语义

## ADR-UI-001：生产应用与共享呈现程序集

- 决定：建立 `Midora.Desktop` 生产 WPF 可执行程序和 `Midora.Desktop.Presentation` 共享 WPF 类库；Style Gallery 改为消费共享类库，不再持有可分叉的主题副本。
- 原因：`AGENTS.md` §8 要求生产 UI 复用已批准的 Palette、ControlTemplates、icons 和 Chrome，不能长期维护平行 token。
- 边界：Style Gallery 不是生产依赖；Domain/Application 不引用 WPF。

## ADR-UI-002：呈现状态与业务状态分离

- 决定：`DesktopSession` 只组合当前 `ProjectDocumentSession`、运行协调器、Workspace Session State、Application Preferences 和 Transient Interaction；Project 修改只能通过现有 `IProjectEditCommand` 执行。
- 原因：SRS 17.2、20.19 要求四类状态所有权和统一 Project History。
- 结果：ViewModel/Presenter 不直接把 Selection、zoom、Draft、Mute/Solo 或 Task History 写入 Domain。

## ADR-UI-003：渲染型时间线

- 决定：Arrangement、Piano Roll、SubVoice/Logical Parameter/Conductor 事件视图采用少量 `FrameworkElement` 自绘表面；每个表面通过 `OnRender(DrawingContext)` 绘制，不为每个数据对象创建 `Control`、`Shape`、Binding 或 RoutedEvent handler。
- 原因：用户明确要求应对大量 Note/Event；WPF 大量视觉树对象会放大 measure/arrange、binding、input 和 GC 成本。
- 结果：标准控件仅用于 Toolbar、字段、列表外壳、ScrollBar 和可访问的主要命令；海量音乐对象是绘制 primitive。

## ADR-UI-004：不可变渲染快照与 revision 门

- 决定：UI 线程从正式模型构建按稳定 ID 排序的不可变 `TimelineRenderSnapshot`；snapshot 携带 Project semantic revision 和 workspace projection key。后台可准备纯数据索引，但只在 UI 线程按 revision 原子替换。
- 原因：避免绘制/命中过程中枚举正在改变的集合，并保证命中目标与提交 revision 一致。
- 失败语义：revision 不匹配时丢弃旧结果；手势提交前重新验证目标，不进行部分提交。

## ADR-UI-005：分层索引、裁剪与命中

- 决定：时间轴对象按 lane/pitch 分桶，桶内按 start tick 排序并建立分层 maximum-end 区间索引；viewport query 使用 start-tick 二分上界和 maximum-end 子树裁剪，hit test 只查询指针邻域。密集重叠按 z-order 与稳定 ID 产生确定顺序。
- 原因：兼顾长对象跨入 viewport 与大量短对象；避免每帧扫描全 Project。
- 限制：结构改变时重建受影响桶；小型快照允许线性路径，但公开行为和排序相同。

## ADR-UI-006：绘图资源与失效策略

- 决定：冻结并复用 Brushes/Pens/Geometries；文本使用按 DPI/font/text key 的有界 `FormattedText` cache；只在 model、viewport、theme、selection 或 transient overlay 改变时 `InvalidateVisual`。
- 决定：静态 grid/content/overlay 分层缓存；播放 cursor 等高频 overlay 不强制重建静态 content snapshot。
- 限制：所有 cache 有显式容量和清空条件；不把缓存放入 Project 或 Application Preferences。

## ADR-UI-007：输入手势状态机

- 决定：所有自绘表面复用明确状态机：Idle、Pointing、Dragging、Marquee、Drawing、Resizing、Panning、ContextTarget；Pointer capture、Escape、deactivation 和 revision 变化都有确定取消路径。
- 决定：拖动期间只更新 transient preview；Pointer Up 后通过 Application command 做一次原子提交。Snap 以 Primary Selection 计算一个 shared delta。
- 决定：Segment / Note 的 `Alt` 强制 Move、`Ctrl+Alt` 强制 Copy+Move，以及 Velocity 的 `Alt` 强制轨迹均在 Pointer Down 时解析并冻结；拖动途中修饰键变化不改变操作类型。Alt 不再承担临时绕过 Snap 的语义。
- 决定：Piano Roll Note 放置、Note Move/Copy-Move 与 Event/Parameter Point Move/Copy-Move 的 `Shift` 固定时间轴语义同样只在 Pointer Down 解析并冻结。Note 放置保持默认 length，Note/Point Move 的 tick delta 固定为零；Event Lane 空白处 `Shift + Left Drag` 只创建起点 tick 的一个 Point，纵向仍可调整 value。既有 `Shift + Right Drag` 水平线继续固定起点 value。
- 决定：Note 与 Event/Parameter Point 的选择集 Move/Copy-Move/Resize transient preview 显示全部相关选择对象，而不是只显示 Primary。Move 优先复用已缓存 Selection raster 并做一次二维变换；缓存未就绪或 Resize 时，只查询会变换到当前 viewport 的对象并合并为一个冻结 `StreamGeometry`，不得为每个对象建立 WPF `Shape` 或逐项 `Draw*` 热路径。
- 决定：Logical Parameter Point 的 Ctrl+拖动必须调用独立原子复制命令并分配新稳定 ID；不允许 UI 显示 Copy-Move 预览而 Pointer Up 实际移动原对象。复制结果的同 tick 冲突仍由 ADR-UI-033 的 point later-wins 规则统一处理。
- 决定：只有 Timeline 已消费 Alt 强制手势时，主窗口才锁存来源 surface，并在对应 Alt KeyUp 的 preview 阶段阻止主菜单访问模式、随后恢复来源焦点；`Alt+F4`、普通 Alt 和窗口失焦不共享该锁存。Draw hover 外轮廓始终属于单对象 transient overlay，不进入或失效 raster cache。
- 原因：落实 SRS 20.1、20.3～20.5 的一次手势一次 Undo 和无 partial success。

## ADR-UI-008：线程与任务边界

- 决定：WPF visual tree、Workspace 状态以及由 WPF 直接发起的 `ProjectDocumentSession.Execute/Undo/Redo` 只由 Dispatcher UI 线程访问。文件、编译、输出和可安全索引构建使用现有异步 coordinator；SoundFont 等 Application coordinator 可以在后台完成验证后通过其既有原子命令提交 Project，但不得直接刷新 WPF 投影。进度通过不可变 snapshot 回到 Dispatcher。
- 决定：`HistoryChanged`、`CompilationChanged` 和 playback state 通知不得在发布事件的同步调用栈内重建 WPF 投影。正式 WPF 会话只识别 `DispatcherSynchronizationContext`，统一把 collection/property refresh 排入该 Dispatcher；非 WPF 测试上下文不冒充 UI Dispatcher。SoundFont 等后台流程即使在 `ConfigureAwait(false)` 后原子提交 Project edit，也只能通过该队列刷新 UI。
- 决定：后台状态更新不得主动移动焦点、Selection 或 scroll；只有显式用户导航命令可以。
- 原因：避免跨线程模型变更、Project transaction 内布局重入和焦点竞争；保持现有 task/lock 语义。

## ADR-UI-009：主题、窗口 Chrome 与语言

- 决定：生产应用直接使用已批准的黑红暗色共享资源、Fluent System Icons geometry 和 10×10 caption geometry；caption 使用方形按钮、Arrow cursor、layout rounding 和最大化无外框策略。
- 决定：共享 `Button.Caption` style 必须设置 `WindowChrome.IsHitTestVisibleInChrome=True`。所有自定义 owned dialog 的标题栏按钮由这一共享契约成为可交互区域，Close 继续路由到安全 Cancel。
- 决定：初版所有内置 UI 文案固定 English；本仓库开发文档和交付说明继续使用简体中文。
- 原因：分别来自已批准视觉基线与 SRS 20.13.1。

## ADR-UI-010：可测试的 Presentation Core

- 决定：viewport math、interval index、selection、snap、workspace identity、command availability 和 lock routing 使用不依赖 live WPF Window 的纯 C# 类型；WPF controls 只做输入适配和绘制。
- 原因：使确定性、规模、边界和失败语义能在 CI 自动验证；实际窗口截图只承担视觉验收，不能替代逻辑测试。

## ADR-UI-011（已由 ADR-CORE-052 的 ABI v3 规则取代）：历史 C# Mapping Draft 复用正式编译配置

- 决定：由 `Midora.Compiler.CSharpMappingDraftCompiler` 暴露只验证未应用 Draft 的窄接口；内部直接复用正式 `CSharpMappingCompiler`，不得在 WPF 端另配 Roslyn、引用集、ABI、语言版本或缓存键。
- 边界：Draft 编译不修改 Project、不进入 History、不替换 Applied Version，也不是 canonical consumer 输入；Apply 仍通过 `ProjectDomainEditCommands.UpdateMappingFunction` 做一次原子 Project 编辑，并触发正式编译。
- 原因：SRS 18.5.4、20.11.7 要求 Draft 与 Applied Version 分离，同时禁止 UI 重建一套 Mapping 语义。若在 Desktop 复制 Roslyn 配置，会产生与正式编译不一致的接受结果。

## ADR-UI-012：Conductor 概览只读投影

- 决定：Arrangement ruler 使用独立 `RulerSnapshot` 投影 Tempo、Time Signature、Key、Marker 和 End Marker；它与 Arrangement Segment snapshot 分离，全部条目标记为不可命中、不可编辑。
- 决定：同 tick 的多个 Conductor 事件按正式类型顺序稳定合并为一个视觉标签，避免标签覆盖；编辑仍只在 Conductor Workspace 发生。
- 原因：SRS 18.1 要求 Arrangement 提供 Conductor 概览，但 UI 不得在概览中建立第二套编辑入口或改变同 tick 正式顺序。

## ADR-UI-013（已由 ADR-UI-042 取代）：Diagnostics 身份共享、过滤状态隔离

- 历史决定：曾由 Bottom compact Diagnostics 与完整 Diagnostics Workspace 共享 `DiagnosticRow` 身份并隔离各自筛选。Bottom Panel 已由 ADR-UI-042 删除；仍有效的边界是 Diagnostics 后台刷新不得抢焦点，独立 Workspace 自己保存筛选、选择和滚动状态。

## ADR-UI-014：Project Tree 与 Workspace Tab 的可靠实现边界

- 决定：Project Tree 保持浅层语义导航树，不对 `TreeViewItem` 启用 WPF hierarchical virtualization；大规模对象列表仍使用 recycling virtualization，海量音乐对象仍使用自绘。
- 决定：Workspace Tab header 使用水平像素滚动，选中内容直接绑定 `SelectedItem`；选中新 Workspace 后显式 `BringIntoView`。
- 原因：WPF 层级虚拟化在增量重建后会破坏子节点实现和 UI Automation；Project Tree 只有五个固定根和有限顶层对象，不是完整对象图。Tab 的逻辑滚动会让新选中的容器停留在未布局状态。

## ADR-UI-015：SubVoice 双渲染表面与预览会话状态

- 决定：一个 Active SubVoice 使用共享时间视口的两个自绘表面：128 音高 Note piano roll，以及 CC/Pitch Bend 连续层和 Program/Bank/RPN/NRPN/Pitch Bend Range 离散事件层；overview 合并两者只用于导航。
- 决定：Initial State 是独立视图，不投影到 tick 0；Program 在 UI 显示为 1～128，正式模型仍保存 0～127。
- 决定：Preview 展开状态、Full Instrument/Selected SubVoice 模式、Mute 和 Solo Selected 全部属于 Workspace Session State；它们只改变 preview request，不修改 Project、canonical result、导出或渲染。
- 原因：SRS 18.3～18.4 明确要求独立 Note piano roll、Timeline/Initial State 分离和可折叠 Preview；用户明确要求逻辑层和 SubVoice 层都不得堆逐事件 WPF 控件。

## ADR-UI-016：主窗口关闭请求异步门与非重入关闭

- 决定：第一次 WPF `Closing` 事件始终取消当前关闭，串行执行 Stop、Draft 与 Save Guard；确认成功后只把第二次 `Close()` 排入 Dispatcher，不在仍处于 `Closing` 调用栈时重入窗口关闭。第二次事件由已批准标志放行。
- 决定：关闭 Guard 执行期间拒绝并发关闭请求；退出流程中的异常在窗口仍可用时显示并保留应用，不允许从 `async void` 事件处理器逸出为进程级未处理异常。
- 原因：SRS 19.2.2、19.2.5 要求标题栏关闭、File > Exit 和 Alt+F4 共用 Stop/Draft/Save Guard；WPF 禁止在 `Closing` 事件仍执行时再次调用 `Close()`。即使一个返回 `Task` 的 Guard 同步完成，也必须遵守此非重入边界。
- 边界：该门只协调 WPF 窗口生命周期，不改变 Project 关闭语义、保存事务、播放 cleanup 或 Application Preferences 的所有权。

## ADR-UI-017：输入路由完成后再做视觉树结构变更

- 决定：来自 Popup、Menu、Tree/List 双击或 Enter 的创建与 Workspace 导航，先结束当前 transient interaction；Popup 必须先关闭，然后把结构性 edit/navigation 排入 Dispatcher。Project/Compiler 的同步通知同样不在当前 routed input 调用栈内重建绑定集合。
- 决定：禁止通过投递不改变尺寸的 `WM_SIZE`、同步强制 `UpdateLayout` 或其他伪造窗口消息来“提交” WPF container layout。
- 原因：Popup 使用独立 HWND；在其 Click 路由或 TreeView 双击路由尚未退栈时清空/重建 ItemsSource、Tab 或 Workspace，会把 mouse capture、selection 和 measure/arrange 带入重入状态，表现为整个主窗口不再处理输入。
- 边界：排队只改变 UI 提交时机，不改变 Project command 的原子性、History 顺序、稳定 ID、编译输入或持久化结果。

## ADR-UI-018：分层栅格缓存、离散 LOD 与独立命中

- 决定：Arrangement 的 Segment Note Preview 不再在每帧逐 Note 调用 `DrawingContext`。每个 Segment 以稳定 ID、preview 内容指纹、调色板 revision 和 DPI 为键，生成一张固定分辨率的冻结 `BitmapSource`；缩放、平移、Selection、hover 和播放指针只拉伸或复用该位图，不重建 preview 内容。
- 坐标不变量：Arrangement preview 始终映射到完整、未按 viewport 裁剪的 Segment 世界矩形，再由可视 Segment 矩形裁剪；平移只能平移该目标矩形，禁止把缓存图像重新拉伸到当前可见切片。piano tile 的 LOD/tile 坐标必须按值绑定到同一个 raster request、cache key 和屏幕目标矩形，后台任务不得捕获随后变化的循环变量。
- 决定：Segment 与 SubVoice 的 piano roll 共用二维 tile renderer。基础 Note 层以 `256 × 256` device-pixel、Pbgra32 tile 缓存；grid/active range、Selection/primary、hover/drag 和 cursor 保持独立覆盖层。平移复用相同 LOD tile，缩放切换量化的水平/垂直 LOD；小于一个 device pixel 的 Note 以覆盖像素聚合，而命中仍使用原始 Note 区间。
- 决定：tile 只从不可变 `TimelineRenderSnapshot` 和按 lane/pitch 分桶的 interval index 读取。每个 tile 使用忽略 Selection/Primary 的局部视觉内容指纹；编辑一个 Note 只轮换与该 Note 相交的 tile key，其他 tile 跨 workspace revision 复用。后台最多两个 raster worker，不同 in-flight raster key 最多 64 个；结果携带 projection/tile-content generation，过期结果不得替换当前画面。UI 只在 tile 完成后原子接收冻结 bitmap。
- 决定：Selection 使用独立、按稳定 ID 查询的不可变 presentation snapshot。单纯 Selection/Primary 变化不得重建 Note 基础 snapshot、interval index、Segment preview 或 piano-roll tile；精确 selection border、selected fill 和 transient gesture 在前景层绘制。
- 决定：共享 UI raster cache 的内存预算为 `256 MiB`，使用 LRU 回收；可视 tile 外最多预取一圈。缓存只属于当前进程和 Project session，Project 关闭/替换时整体清空，不写磁盘、不进入 `.midora`、Undo/Redo、Project fingerprint 或 Application Preferences。
- 失败语义：后台 raster 异常记录为 UI runtime trace 并丢弃对应 tile；不得修改 Project、阻塞输入或让过期 bitmap 覆盖新 revision。tile 未就绪只允许暂时显示静态背景与已就绪覆盖层，不允许回退到每帧逐 Note 绘制。
- 边界：本阶段使用纯 WPF `BitmapSource` 与 CPU 像素栅格，不引入 SkiaSharp、D3DImage 或自建 Direct3D surface。raster backend 保持可替换；只有基准证明纯 WPF 路径仍不能满足正式性能门时，才另行评估 native/GPU backend。

## ADR-UI-019：带保护区的瓦片、批量选择层与定向工作区刷新

- 决定：Piano Roll 的每个 `256 × 256` 核心瓦片在四边各增加 1 device-pixel 保护区；相邻瓦片按相同世界坐标重复栅格化保护区并重叠组合。被瓦片边缘截断的 Note 不生成伪边框，只有 Note 的真实起点、终点和上下边缘生成轮廓，避免瓦片缝隙及长 Note 内部的人工分界线。
- 决定：Piano Selection 使用独立的 selection-revision 瓦片层；Velocity 的普通与选中柱状统一进入横向瓦片层。Primary、drag、正在编辑的 Velocity 值和 cursor 仍是小规模 transient overlay。大选区不得退回逐 Note WPF primitive 路径。
- 决定：框选的视觉矩形与最终 interval-index 查询必须共享同一份吸附后 tick/lane 边界。时间范围按拖动方向解析：Pointer Down 锚点独立吸附并固定，只吸附移动端；左右方向不足一个 operation step 时均向各自拖动方向覆盖一个完整有效 operation step，不得用吸附后的起点加吸附长度反算固定锚点。一次框选通过批量 selection mutation 只推进一次 selection revision。
- 决定：`ProjectDocumentSession` 分离 History 状态通知与携带 `ProjectChangeSet` 的内容通知。Desktop 只重建受 Track、Event Instrument 或 Conductor 变更影响的 Workspace；保存点等纯 History 变化只刷新状态属性。编辑一个 Track 不得重建其他 Track 的已打开 Segment/SubVoice 大型快照。
- 边界：正式编译仍保持现有同步、原子和 canonical 结果语义；本决定不把 UI 响应速度问题转化为延迟编译或未验证 Project 状态。

## ADR-UI-020：最终设备像素坐标栅格化，不再缩放 Piano tile

- 修正 ADR-UI-018 的离散 LOD 部分：Piano Roll tile 仍为 `256 × 256` device-pixel 核心和四边 1 device-pixel 保护区，但 tile 必须按当前实际 `devicePixelsPerTick` 与 `devicePixelsPerLane` 生成，并以该精确缩放的 IEEE 754 bit pattern 作为 cache key。WPF 组合阶段只允许 1:1 device-pixel 映射，不再把量化 LOD bitmap 二次放大或缩小。
- 同一 tick 的左右边界必须由同一表达式直接换算并执行一次最近像素舍入；禁止用 `floor(start)` 与 `ceil(end)` 两套方向相反的规则，也禁止用“已舍入 start + width”推导 end。相邻 Note 的共享 tick 因而得到完全相同的像素边界。
- 同一 pitch lane 的 top/bottom 必须从全局 lane 边界计算并舍入，再换算到 tile 局部坐标；不得按每个 tile 单独缩放已栅格化的行，从而避免横向 tile 之间发生 1 device-pixel 的纵向相位差。
- Arrangement Segment preview 的最高精度使用固定参考比例 `96 pixels / quarter note`、64-pixel 高度和 256-pixel 横向 tile；较低精度仅允许半八度 `1 / 2^(n/2)` 固定 LOD，精确 viewport zoom 不直接进入 cache key。Segment tick 长度、Project TPQN 与固定 LOD 决定总参考宽度；viewport 选择第一个 source-pixel 比例不大于当前显示比例的固定目标层，避免完成层遍历亚像素 tile，也禁止最近邻向下采样跳过一像素短 Note。完整 Segment 的 left/top/width/height 先统一换算并舍入到 device pixel，所有 tile 的相对边界再从这一个固定 device width 派生；pan 因而只改变整 device-pixel translation，不改变 tile 内采样相位。normalized start/end 使用相同的最近像素边界规则，tile 精确映射到完整 Segment 世界矩形后裁剪。单个 Note 在源 bitmap 中覆盖相邻两行，以避免 `64 px` 预览缩小到常规轨道高度时，最近邻采样完整跳过只有一行的首音符。Snapshot 后台预热选择每个 Segment 不超过四个 tile 的完整固定层；可见 fallback 固定使用 `max(0, warmup LOD - 2)`，即提高一倍水平分辨率且最多八个 tile，只有全套 tile 命中才原子呈现。fallback 身份不随 viewport 改变；显示层更粗时直接缩放复用，禁止因缩小视图重新空白。全部可见 fallback 完成后才准入新的目标请求；目标 tile 就绪后独占其横向范围，fallback 仅绘制在未覆盖间隙且缓存继续保留。该修正取代曾把任意长度 Segment 压进固定 512-pixel 总宽度、以及缩小时仍遍历最高精度全部 tile 的设计。
- 合成不变量：tile 目标宽度必须执行浮点比例换算；禁止让整数除法把部分 tile 的目标宽度截断为零。Presentation 回归必须覆盖 `TimelineSurface → async raster cache → final DrawingContext composition`，不能只测试 rasterizer 输出。
- 依据：实机复现确认离散 LOD bitmap 的 WPF 二次采样会让 1-pixel border 在特定缩放下坍缩，并让相邻 tile 出现不同采样相位。该修正只改变 UI runtime cache 与像素覆盖，不改变 Note/Segment 语义、命中、编辑、持久化或可听结果。
- 后续边界：此实现吸收了高性能 MIDI 编辑器常见的“语义实例 + 统一最终像素变换”原则，但没有复制或引入 yinhe 的 AGPL 源码，仓库许可证因此不变。若将来改用 GPU instance renderer，需另立 ADR、性能门和许可证审计。

## ADR-UI-021：Piano tile 完整帧保留与原子切换

- 决定：Segment/SubVoice Piano Roll 的 Note 层和缓存 Selection 层分别记录上一组“可视 tile 全部已完成”的 cache key。编辑或精确缩放导致当前可视集合存在未完成 tile 时，继续绘制上一完整集合；当前集合全部可用后一次性切换，禁止在同一过渡帧中混合空白块和零散的新块。
- 坐标：编辑时旧 tile 保持原比例；缩放时旧 tile 按其原始 device-pixel scale 反算世界 tick/lane 边界，再映射到当前 viewport。该临时映射只持续到精确比例的新 tile 全部完成；稳态仍严格执行 ADR-UI-020 的最终像素 1:1 组合。
- 资源：完整帧只保存 key，不复制 `BitmapSource` 或像素数组。只有全部 key 仍可从共享 LRU 取得时才使用旧帧；任何一项已回收即退回现有静态背景/当前已就绪块行为，因此不扩大 `256 MiB` raster cache 预算，也不阻塞 UI。
- 边界：grid、active range、cursor、primary outline 和 transient edit preview 始终使用当前状态；旧帧只是一层短暂视觉替身，不参与 hit test、Selection、Project、Undo/Redo、编译、播放或持久化。Arrangement preview 和 Velocity tile 不在本次改动范围。
- 依据：异步 tile key 在 Note 编辑和每个精确缩放级别都会轮换；原实现会在新 bitmap 完成前暴露背景，从而产生整块闪烁。保留旧完整集合能消除该空白窗口，同时不触碰 rasterizer、内容指纹、缓存键、后台 worker 或对象命中架构。

## ADR-UI-022：Velocity 固定柱与提交时栅格化

- 决定：Velocity 不再以 Note 的 `[startTick, endTick)` 画等长矩形。每个 Note 只在 start tick 投影当前横向 cache LOD 下固定 3-pixel 窄柱和 tile source 中 7-pixel 的方形 marker；pitch 写入 presentation Z key，同 tick 低 pitch 先画、高 pitch 后画，direct hit 使用相反顺序命中最上层。
- 手势：空白区域发起的左键自由绘制和右键直线插值在 capture 期间只保存、绘制指针轨迹，不查询并逐柱覆盖 Note。`Alt + Left Drag` 在 direct hit 之前强制选择自由轨迹路线，即使起点命中柱或 marker。MouseUp 才通过不可变 interval index 生成 stable-ID → velocity map，并调用既有批量命令形成一个 Undo。无 Alt 直接按住柱或 marker 时只维护一个 Note 的 transient value，不显示轨迹。
- 缓存：MouseMove 不改变 snapshot、Selection revision 或 Velocity tile key。批量命令提交后异步生成新 tile；新可视集合未完整前继续显示上一完整 Velocity frame，完整后原子切换。frame 只引用共享 LRU key，不复制像素。
- 原因：旧实现虽然只在 MouseUp 提交 Project，但每次 MouseMove 都枚举 `_velocityEdits` 并为所有已触及 Note 调用 WPF rectangle drawing；密集数据下覆盖层成本随手势长度持续增长。轨迹层把拖动期绘制成本改为只与鼠标采样点数相关。
- 边界：轨迹只是 transient UI state；取消或 capture 丢失不提交。最终 velocity、Selection 过滤、稳定 ID、Project command 原子性和 Undo 语义不变，Note 的 tick、length 与 pitch 不受影响。

## ADR-UI-023：Direct Timeline Select 优先框选

- 决定：Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的 Select 模式在单次左键按下时先于对象 hit test 进入 marquee capture；起点位于 Segment / Note 内部时也不发出 `ItemInvoked`，因此不再提供单对象点击选择。双击仍进入既有对象命中与导航路线。
- 原因：极端密集对象覆盖画布时，先命中对象会令用户无法从中间位置开始框选。Select 的明确职责改为区域选择；单对象选择仍可在 Draw 模式通过点击完成。
- 边界：有效 marquee 的集合运算固定为：无修饰键 Replace、Ctrl Add、Alt Remove、Ctrl+Alt Toggle；Shift 保留为 Add。任一修饰键路径都以现有选择为基础，空选区不改变选择；无修饰键的有效空选区执行 Replace 并清空原选择。小于 marquee 阈值的普通点击只设置 Edit Cursor，不改变 Object Selection。该决定不改变 Draw、Split、Erase、右键上下文命中、对象编辑、Project 数据或 Undo。

## ADR-UI-024（层级、Unbound 与 Library 拖放部分已由 ADR-UI-039 取代）：Arrangement 放置手势与 Track Header 直接操作

- 决定：Arrangement Draw 在空白区域按下时建立 transient Segment placement；未越过阈值使用 `1 × TPQ` 默认长度，向右拖动则按操作粒度改变结束 tick，MouseUp 只提交一次 `CreateSegment`。目标间隙不足时仍沿用新建 Segment 的可用间隙裁剪规则。
- 决定：Track Header 作为 Timeline 内容以外的独立命中区，维护 hover、pressed 和 reorder transient state；拖动完成只调用正式 `ReorderLogicalTrack`。上下文菜单调用既有 Rename、Bind、Delete 和 Reorder Project command，不另建 UI 业务模型。
- 历史决定：Arrangement snapshot 曾增加绑定 Event Instrument 名称或 Unbound / missing 状态，并允许从 Event Instrument Library 拖放绑定；ADR-UI-039 后续又建立了 mixed parent tree。两者均已由 ADR-UI-041 / ADR-CORE-046 取代。当前 Arrangement 投影 global mixed Track order，跨 Usage/Root 拖放走原子换组或独立化命令。
- 仍有效边界：Track Header 的 hover、pressed、drag target 和菜单 target 不保存、不进入 Undo；正式名称、父子顺序和所有权只属于 Project Content。

## ADR-UI-025：Note pitch 越界删除与损坏来源安全投影

- 决定：普通 Logical Note / Template Note 批量移动不再以选择集边界 clamp pitch。领域命令对全部 Note 应用同一请求 delta，删除结果 pitch 越出 `0..127` 的 Note，并移动其余 Note；两部分由一个 prepared command 原子 Apply / Undo，Undo 按原容器索引恢复被删除对象。
- 决定：复制拖动继续执行共同 pitch clamp，避免改变源对象或产生部分副本。时间负值、非法长度和 velocity 等其他无效结果仍在 mutation 前拒绝。
- 决定：Presentation 对已损坏 Project 中的非法 Note pitch 使用 `Math.Clamp(pitch, 0, 127)` 计算安全 lane，同时标记 `Invalid`；诊断导航先验证 Segment / Note 稳定 ID 仍存在，再构建 Selection 和 viewport。
- 原因：用户明确要求移动越界 Note 被丢弃而非存入非法 pitch；持久化损坏或旧缺陷留下的非法对象仍需可诊断、可定位且不能使 WPF projection 构造崩溃。
- 边界：该规则改变 Project 编辑结果但不改变 Compiler 对非法源数据的 Error，也不允许正式消费者接收非法 Note。删除可 Undo，不产生新稳定 ID。

## ADR-UI-026：Direct Timeline 边缘命中与 SubVoice Piano Roll 共用契约

- 决定：`TimelineViewport` 明确区分“定位 tick”和“包含 tick”。定位、Snap 与放置继续使用最近 tick；半开区间 hit test 使用对连续世界坐标向下取整的包含 tick。不得把四舍五入后的定位 tick 用作 `[startTick,endTick)` 内容归属，否则高缩放下每个 tick 的后半段会被错误归入右侧对象或空白。
- 决定：Arrangement Segment、Logical Note 与 Template Note 的 Draw 边缘命中先以固定 `5 DIP` 扩展 interval-index 候选，再在屏幕坐标中解析真实 Start/End 边缘；不得直接用半开区间 `[startTick,endTick)` 的零容差内容命中决定 Resize。共享边界左侧指向左对象 End、右侧指向右对象 Start；精确重合时依次优先 Primary、Selected、End。
- 决定：Direct Timeline 的 Select 单击仍先进入 marquee capture；Pointer Up 未达到框选阈值时发出背景定位并设置吸附后的 Edit Cursor，保留现有 Object Selection，不恢复单对象点击选择。
- 决定：Timeline 右键菜单提供 `Deselect All` 与 `Invert Selection`；前者清空当前 Workspace Selection，后者只 Toggle 当前 surface snapshot 中可命中的对象，并保留当前 surface 之外的既有选择。
- 决定：SubVoice Note Piano Roll 复用 Segment Piano Roll 的 `TimelineSurface` 交互与颜色路径：接入同一 `MarqueeCompleted`、Edit Cursor、Template 有效范围和蓝灰 Note palette；对象种类仍为 `TemplateNote`，编辑继续路由到正式 Template command。
- 原因：半开区间适合正式范围语义，但视觉 End 边缘本身位于区间外；高缩放时把连续坐标四舍五入成整数 tick，会让一个 tick 的后半段提前归入下一个 tick，形成边缘左侧的命中空洞。SubVoice 缺少事件/状态绑定则会使同一控件产生行为和颜色分叉。
- 边界：该决定只改变 UI hit resolution、session cursor 和 presentation binding；不改变 Project Note/Segment 范围、稳定 ID、Selection 数据模型、编译、播放、Undo/Redo 或持久化。

## ADR-UI-027：批量边缘调整采用共享请求量与逐对象长度饱和

- 决定：Arrangement Segment、Segment Logical Note 与 SubVoice Template Note 的批量边缘调整使用 Primary 对象吸附后得到的同一个请求 Edge Delta，但不再先按选择集中最短对象的剩余长度共同裁剪 delta。正式 Application command 对每个对象独立计算结果；缩短超过该对象可用长度时，仅该对象在 `1 tick` 处饱和，其他对象继续应用完整请求 delta。
- 决定：左边缘调整保持每个对象原右边缘不变，右边缘调整保持每个对象原左边缘不变。Segment 左边缘同时按实际应用量更新 `ContentOffsetTick`；时间非负、内容窗口合法、Segment 不重叠和整数溢出等硬约束仍在 mutation 前验证。任一结构性约束失败时整批拒绝，不产生部分修改。
- 原因：共同按最短对象裁剪会使一个短对象限制所有较长对象，无法表达用户请求的批量缩短量。共享请求量加逐对象最小长度饱和既保持非比例批量编辑语义，也使 `100 tick` 与 `20 tick` 对象共同缩短 `60 tick` 时确定地产生 `40 tick` 与 `1 tick`。
- 边界：一次手势仍只提交一个 Project command 和一个 Undo；不改变对象稳定 ID、编译/canonical 语义、持久化格式或 Snap 来源。该规则只对长度最小值做逐对象饱和，不把重叠、容器边界等结构性错误降级为部分成功。

## ADR-UI-028：Arrangement Bar Grid 的分母拍辅助线

- 决定：Arrangement 在可见 Grid 选择 `Bar` 时继续以主实线绘制 Project Time Signature Map 的自然小节边界，并在每个小节内按当前拍号的 `TicksPerBeat` 绘制颜色更浅的低强调实线。拍号变化 tick 无论是否截断前一自然小节，都作为新小节的主实线起点；旧小节只绘制变化点之前实际存在的拍边界。
- 决定：新建 Project 或重置编辑器时，Arrangement 默认 `Grid = Bar`、`Snap = 1/8` 且 Snap 开启。共享的 Piano Roll 设置继续保持既有默认值，Segment/SubVoice Piano Roll、Velocity 和 Event Lane 不增加 Bar 模式拍内辅助线。
- 原因：只显示小节边界时，Arrangement 在 Bar Grid 下缺少拍级定位参照；直接复用正式 `ProjectTimeSignatureMap` 可正确覆盖 `3/4`、`6/8` 与变拍，而不在 UI 建立第二套时间语义。
- 原因：WPF 的虚线会把每条纵向辅助线进一步细分为大量 dash，平移和缩放重绘时开销明显高于实线；使用低不透明度实线保持层级区分，并减少网格绘制成本。
- 边界：辅助线只是当前 viewport 的 transient 绘制，不进入 Project、Undo/Redo、`.midora`、编译或输出。初版仍不实现 additive meter、复合拍重音分组或钢琴卷帘同类增强。

## ADR-UI-029：SubVoice 非 Note MIDI 编辑统一为实际事件点

- 决定：正式 WPF 不再创建或编辑 Value Curve。每一个可见 Event Lane 由精确 `MidiValueTarget` 标识，Lane 中每个可见点直接对应一个 `TemplateEvent`；同 target、同 tick 只保留一个事件点。不同 CC number、RPN / NRPN number 或复合事件字段不得合并为同一 Lane。
- 决定：直接拖动只维护一个点的 transient value；自由轨迹、`Alt + Left Drag` 强制轨迹和右键直线在捕获期间只绘制 overlay，MouseUp 才把采样 tick 批量 upsert 为 Template Event，并形成一个 Project Undo。轨迹不是 Curve，不进入 Project、Compiler、`.midora` 或 clipboard。
- 决定：本 ADR 的 CC 目录只约束 Event Instrument/SubVoice。该目录冻结为 BASSMIDI 2.4 MIDI implementation chart 与 SubVoice 合法普通用户事件的交集；CC120～127 不暴露，CC91 / CC93 继续禁止。Bank 与 RPN / NRPN 保留专用事件类型，但官方明确 recognized 的 CC0 / 32 / 6 / 38 / 98～101 仍可按普通 CC 选择。UI 统一显示 `number - name`。Pure MIDI Event Lane 按 SRS 第 23 章提供完整 Channel Voice Event，不受本目录限缩。
- 决定：当前底层 Value Curve 类型可以在本轮后续清理中删除，但正式 UI、创建命令和新数据不再依赖它。产品所有者已明确允许开发期破坏旧数据兼容性，因此不增加旧 Curve UI、旧 clipboard 或旧工程迁移分支。
- 原因：旧的 Add Curve / Add Event 双模型令画面与正式事件不一致，也把不同 target 粗略合并；实际事件点模型使绘制结果、命中、Undo 和 canonical 输入一一对应。
- 边界：该决定不允许 WPF 直接生成 canonical 事件；Template Event 仍必须经过 Semantic Validation 和 Compiler。Note、Velocity 与 Segment Parameter Curve 不属于本决定范围。

## ADR-UI-030：SubVoice 事件 Mapping 按精确标量目标共享

- 决定：`TemplateEvent` 只保存事件点自身的稳定 ID、tick、类型和值，不再拥有 Number / Value / Secondary Value 三条 Mapping Chain。`SubVoice` 按精确的 `TemplateEventMappingTarget` 拥有共享 Mapping；其身份为事件种类、必要的事件编号以及可映射标量字段。Note 因而分别有 Number 与 Velocity 两个目标，Bank 分别有 MSB / LSB，Pitch Bend Range 分别有 Semitones / Cents；不同 CC、RPN、NRPN number 仍是不同目标。
- 决定：同一 SubVoice 中，同一精确标量目标无论有多少事件点，最多存在一条 Mapping Chain 和一组整数目标设置。删除全部对应事件点不自动删除该共享 Mapping；以后重新创建该类事件继续复用原定义。用户显式删除非 Note Mapping owner 后，现存原始事件 Lane 仍可见并以原始值直通，普通事件点编辑和同目标新增点不得静默重建 owner；Note Number/Velocity owner 不允许整链删除。事件点复制、批量复制和 Timeline clipboard 只复制事件点值，不复制 Mapping。完整 SubVoice 复制则只复制一次共享 Mapping 集合，并确定地重映射其 Parameter、Envelope 与 Mapping Function 引用。
- 决定：Compiler、Semantic Validator、fingerprint、稳定 ID 审计、持久化和 Mapping 编辑器只枚举 `SubVoice.EventMappings` 一次。编译某个事件点时，根据该点的精确目标查找共享 Mapping，并继续把该事件点稳定 ID写入 canonical source trace；共享所有权不得削弱逐事件诊断定位。
- 持久化：开发期直接替换 Event Instrument protobuf 模型；`TemplateEventV1` 不再保存三条 Mapping，`SubVoiceV1` 新增共享 Mapping 集合。不提供旧 per-event Mapping 数据迁移或双读分支。
- 原因：per-event 三链模型使没有 Mapping 的海量事件点也各自分配三个稳定对象，事件点数量增长会线性放大内存、Project 对象图、Mapping 列表、序列化体积和 fingerprint 成本；实际 Mapping 语义属于同一 SubVoice 的事件目标转换规则，不属于单个采样点。
- 边界：共享键必须保持 ADR-UI-029 的精确 Event Lane 身份；不得为了进一步减少条目而把 CC1 与 CC11、不同 RPN / NRPN 或复合事件的两个字段错误合并。该决定改变源模型和开发期文件格式，但不绕过 canonical compilation，也不改变 Mapping Chain 内步骤顺序和 Mapping ABI。

## ADR-UI-031：目标感知的 Mapping 编辑与 Instance Velocity 预设

- 决定：Mapping Step 编辑器必须先解析 Mapping Chain 的正式 owner 和精确 target，再生成 Source 与 Mapping Function 候选。Logical Parameter Mapping 不提供 Template Note/Velocity；非 Note 事件链不提供 Template Note/Velocity；无 Per-Note Instance Isolation 时不提供 Envelope、Trigger Note、Gate Length 或 Pitch Delta。无 Isolation 的 Note Number 链不允许添加任何 Step。
- 决定：无 Isolation 时唯一新增例外是共享 `Note · Velocity` target 可直接读取 `TriggerVelocity`。该值已属于每个 Logical Note 实例的 Mapping Context，并最终写入逐 NoteOn 事件，不占用或改变 Channel Unit 状态；同一 Source 映射到 CC、RPN/NRPN、Program、Pitch、Note Number 或其他 target 时仍要求 Isolation。Mapping Function Expression 经正式推导后依赖任何 per-note Context 时仍要求 Isolation。
- 决定：`Follow Instance Velocity` 不增加旁路字段或第二套编译语义；它是共享 Note Velocity Mapping Chain 的 UI 预设：首个 enabled Step 为 `TriggerVelocity / Override`。新建 Event Instrument 的默认 SubVoice和显式新建 SubVoice 都生成该预设；关闭时只移除这一个基准 Step，后续自定义 Step 保留。复制、粘贴、持久化和 Undo/Redo继续按普通 Mapping Chain 处理。
- 决定：Parameter Mapping 的 source、target SubVoice 与 MIDI target 可在创建后修改，完整 route 变更是一个原子 Project command；列表提供正式重排。Mapping Chain 的对象所属 Properties 公开 enabled、owner、target、最终 rounding/overflow、step count 与 stable ID；Step 的 Parameter、Envelope 与 Function 引用只通过带显示名称的对象选择器编辑，不要求用户手填 Stable ID。
- 依据：用户于 2026-08-15 明确批准无 Isolation 的 `TriggerVelocity → Note Velocity`，并要求默认 Follow Instance Velocity。该决定收窄并替代 SRS 9.9 对这一精确 target 的 blanket isolation 要求，也把 SRS 8.53.3 的 fixed template velocity 默认改为新建 SubVoice 默认跟随；未修改 SRS 原文。
- 边界：本决定不允许共享通道状态随 Note 实例变化，不改变 Channel Unit 分配、overlap、NoteOff 配对、canonical consumer 或 Mapping ABI。损坏/旧数据中的非法组合仍由 Semantic Validator 诊断；UI 过滤不是正式验证的替代品。

## ADR-UI-032：Draw Note 放置期间的纵向 Key 草稿与纯音高试听

- 决定：Segment 与 SubVoice Piano Roll 的 Draw 空白放置共用一个 transient Note draft。Pointer Down 冻结 start tick 和默认 length；水平拖动改变 length，纵向拖动把当前 lane clamp 到 MIDI Key `0..127` 并改变 draft pitch。MouseUp 仅以最终 pitch/length 提交一次正式创建命令。
- 决定：Pointer Down 以及每次 draft pitch 实际变化时，调用既有独立 pure-Note audition stream；该 stream 先 All Sound Off 再 NoteOn，不读取 Event Instrument、Mapping、Program 或其他事件。Pointer Up、Escape、capture 丢失和取消都执行 NoteOff + All Sound Off。
- 依据：用户于 2026-08-15 明确要求创建 Note 时可上下拖动改变 Key 并即时试听。该决定替代 SRS 18.2.4 中“新建 Logical Note 不启动声音预览”的旧交互限制；未修改 SRS 原文。
- 边界：试听只属于 transient interaction，不进入 Project、Undo/Redo、编译、canonical result、缓存或持久化；音频不可用时创建语义不改变。

## ADR-UI-033：选择集变换、精确起点冲突与受限批量表达式

- 决定：普通 Note、Logical Parameter Point 与 SubVoice MIDI Event Point 编辑统一在 `ProjectDocumentSession` 的 prepared-edit 边界处理“同 owner、同精确 target、同 start tick”冲突，但按对象类型采用不同策略。Logical/Template Note 保持 incumbent 优先：新建或移动到既有精确 tick+key 的 later Note 静默删除，同一手势的多个 newcomer 按 owner 稳定顺序保留第一个。Logical Parameter Point 与 SubVoice MIDI Event Point 改为本次编辑优先：只要当前手势产生 newcomer，就删除目标 tick 的 incumbent，并在多个 newcomer 中保留 owner 稳定顺序的最后一个。不同 start tick 的 Note gate overlap 不属于该规则，继续由 Event Instrument 配置和 Compiler 诊断决定。
- 性能边界：prepared edit 必须显式登记实际触及的 Segment、Logical Parameter Lane 或 SubVoice；事务层只扫描这些 owner，不得在每次编辑后扫描整个 Project。未被本次编辑造成的既有损坏冲突保持原样，交由现有验证与诊断处理。
- 变换边界：Horizontal Flip / Scale / Batch Edit 与直接拖动使用相同的精确冲突策略；Logical Parameter Point 与 SubVoice MIDI Event Point 不因同 tick 重叠而拒绝整批操作，而是在 Apply 后保留本次编辑中 owner 稳定顺序最后的 newcomer。Segment window 重叠仍在 Apply 前整批拒绝。Note transform 即使形成 gate overlap 也不拒绝；精确同 tick + key 仍应用 newcomer 删除规则。Segment Join 依照同一规则保留左侧/先到对象。
- Segment 左边缘：向左 resize 在 `ContentOffsetTick` 足够时只改变 Segment window；若继续向左会令 offset 小于零，则把 Segment 内全部 Note 和 Parameter Point 同量右移，使暴露与未暴露内容的 Project 绝对位置不变。Project tick 0、同轨 Segment 不重叠和至少 1 tick 长度仍是硬边界。
- 选择集操作：Segment、Logical/Template Note、Logical Parameter Point 与 SubVoice MIDI Event Point 的 Flip、Scale、Transpose 与 Batch Edit 均由 Application 原子命令完成。Segment 内容操作只处理与当前暴露 `[ContentOffsetTick, ContentEndTick)` 相交的对象；隐藏对象不被变换。删除越界 Note 仍可 Undo。
- 表达式：Batch Edit 的 `=` 语法只允许数值/布尔运算、条件表达式、double cast 和数值型 `System.Math`；拒绝语句、赋值、对象创建、任意 API 与循环。Roslyn 仅解析受限 Expression 语法，正式 binder 保留 C# 数值提升和 Math 重载选择后建立 `System.Linq.Expressions` 委托；不得 Emit DLL、创建 AssemblyLoadContext、读取运行机器 TPA 或依赖发布目录中的 reference assembly。`v1/p1/k1/g1/t1` 依赖仍以静态 DAG 排序，直接或间接环均在提交前拒绝；一次整批求值共享 10 秒上限。所有整数结果采用 `AwayFromZero`，目标字段再执行明确的 clamp/delete 规则。
- UI 会话：批量操作前后的 Selection/Primary Selection 按 Project history state ID 建立 session bookmark。操作后不存在的 ID 由 workspace projection 清除；Undo 回到旧 state 时恢复被删除对象原有选择，Redo 再恢复新 state 的选择。bookmark 不进入 Project、`.midora`、canonical fingerprint 或普通 Undo payload。
- 预设：Note 与 Event Batch preset 分别平铺在 `<ProgramRoot>\Data\Presets\NoteBatchPresets` 和 `<ProgramRoot>\Data\Presets\EventBatchPresets`，每个 preset 是一个独立 JSON。它们属于应用本地可携数据，不是 Application Preferences，也不进入 Project；撞名拒绝，损坏的独立文件只在当次列表中隔离省略。该路径已被 Program-root portable storage 决策取代，不得读写旧 `%LOCALAPPDATA%\Midora\Presets`。
- 依据：产品所有者于 2026-08-18 明确批准 Logical Track clipboard、精确冲突静默删除、批量选择变换与批量表达式工作流。该决定收窄并替代 SRS 20.6.5 对 Logical Track 普通 clipboard 的排除，以及与精确 newcomer 冲突失败相抵触的旧交互文本；SRS 原文未修改。

## ADR-UI-034：事件轨迹按音乐时间网格采样与焦点敏感批量快捷键

- 决定：Segment Logical Parameter 与 SubVoice MIDI Event 的自由轨迹和右键直线不再按屏幕像素密度产生事件点。MouseUp 时将 transient 指针轨迹投影到有效 Operation Grid：Snap 关闭时步长严格为 `1 tick`；Snap 开启时逐个使用当前 Operation Subdivision，`Bar` 依照 Project Time Signature Map 枚举实际小节边界。轨迹反向经过同一 tick 时以后经过的值覆盖先前值。
- 决定：`Shift + Right Drag` 在 Pointer Down 时冻结为水平直线手势，纵值固定为起点值，横向范围仍由起终点和有效 Operation Grid 决定。普通 Right Drag 继续执行起终点线性插值；Left Drag 继续执行分段自由轨迹。所有路线只在 MouseUp 提交一组 point upsert 和一个 Undo。
- 决定：Event/Parameter Lane 工具栏在 Snap 左侧显示当前可提交坐标 `(tick, value)`；tick 使用当前 Operation Grid，value 使用 Lane 的正式显示范围与整数/连续格式。`Shift + Right Drag` 期间 value 始终显示 Pointer Down 冻结的水平线常量；`Shift + Left Drag` 期间 tick 始终显示 Pointer Down 冻结的单点 tick。
- 决定：焦点位于支持选择集变换的 TimelineSurface 且不在文本编辑、Popup 或 Menu 时，`Ctrl+Q`、`Ctrl+T`、`Ctrl+E` 分别调用现有 Scale、Transpose、Batch Edit 命令。命令上下文在按键发生时从当前焦点 surface 与稳定 ID selection 重新解析，不复用上一次右键菜单目标；不支持的对象类型或空选择执行 No Action。
- 依据：产品所有者于 2026-08-18 明确要求逐 Tick/Snap Tick 轨迹、水平线修饰手势和三项快捷键。快捷键决定明确替代 SRS 20.12.1/20.12.13 中未登记且明确排除 `Ctrl+Q`、`Ctrl+E` 的旧初版表；SRS 原文未修改。
- 边界：指针轨迹、快捷键目标和 Dialog draft 都是 transient/session UI state。正式点仍通过 Application command 进入 Project，编译与消费者链路不变；本决定不增加 Curve 对象、不改变 `.midora` 格式或 canonical 语义。

## ADR-UI-035：事件点二维栅格瓦片与固定设备尺寸

- 决定：Segment Logical Parameter 与 SubVoice MIDI Event 的已提交点集不再由 `TimelineSurface.OnRender` 逐点调用 WPF `DrawEllipse`。两者共用 `256 × 256` device-pixel 核心二维 tile、DPI 感知保护区、既有 `256 MiB` LRU 和后台 raster worker；UI 线程只组合当前可视 tile 并预取外围一圈。
- 坐标：横向 tile 使用当前精确 `devicePixelsPerTick`，纵向 tile 使用当前精确 `devicePixelsPerNormalizedValue`；两者的 IEEE 754 bit pattern 都进入 cache key。普通点固定为半径 `4 DIP`，Selection ring 固定为半径 `6 DIP`，先换算到设备像素再栅格化。稳定画面按 1:1 设备像素组合，时间缩放和值轴缩放不得改变 point glyph 的最终视觉尺寸。
- 缓存：点内容使用当前不可变 snapshot 的内容指纹，Selection/Primary revision 同时进入 tile generation；因此大选区也不得退回逐点 WPF overlay。编辑或 Selection 改变后，只有当前可视与预取 tile 在后台重建；相同比例下新集合未完整前可沿用上一完整 frame，完成后原子切换。
- 即时层：hover、当前单点拖动、创建预览、自由轨迹/直线和 marquee 保持小规模 transient vector overlay。命中、框选与最终 edit 继续读取原始稳定 ID、tick/value 和 interval index，bitmap 不参与 hit test，也不成为 Project 数据。
- 选择集拖动：Point Move/Copy-Move 使用独立 `EventPointSelection` 透明 tile，仅栅格化当前稳定 ID Selection，并在手势中以共享 tick/value delta 做一次屏幕变换。未完成 tile 不阻塞 UI，暂时回退为“viewport 反查 + 单个冻结 StreamGeometry”；不得恢复逐 Point WPF 绘制。Note Move 同理优先平移既有 Piano Selection tile；Note Resize 因各对象最小 1 tick 饱和不同，使用可视候选合并几何。
- 失败与边界：tile 失败沿用 ADR-UI-018 的 runtime trace/丢弃语义，不阻塞输入、不回退逐点绘制。缓存只属于 UI session，不写入 `.midora`、Undo/Redo、编译结果或 Application Preferences；本决定不改变 Logical Parameter/MIDI Event 语义、插值、持久化或 canonical consumer。
- 依据：产品所有者于 2026-08-18 报告 MIDI Event 与 Logical Parameter 点稍多即出现明显卡顿，并明确要求参照 Velocity 缓存且任何缩放下点尺寸不变。

## ADR-UI-036：世界坐标框选、蓝色目标轮廓与 Copy-Move Selection 冻结

- 决定：Select marquee 的 Pointer Down 锚点以实际 tick + lane/normalized value 保存，不保存成随 viewport 解释的屏幕矩形。滚轮或其他会话内 viewport 变化后，起点重新投影，终点从当前 pointer 与当前 viewport 求值；显示与 MouseUp 查询调用同一范围解析。
- 决定：Note Move/Copy-Move 的完整 Selection 目标预览使用独立透明 `PianoDragPreview` tile，只绘制 `Brush.Info` 蓝色实线外轮廓；Resize fallback 的合并 geometry 使用同一 Pen。源 Note 继续由普通/Selection tile 绘制，不能把目标预览画成已提交 Note。
- 决定：已选对象上的 Ctrl Pointer Down 不立即 Toggle Selection。若手势未越过 3 DIP 阈值，Pointer Up 才执行一次 Ctrl Click Toggle；若进入 Copy-Move，则整个手势保持原 Selection revision，直接平移相同的 selection-only raster。该规则消除大 Point Selection 在复制拖动开始时的两次全层失效。
- 决定：Piano Roll lane height 下限为 4 DIP。128 个 MIDI lane 仍是硬边界，额外可视高度显示空白。
- 性能边界：Note 的横/纵向选择集预览都必须是 tile 共享变换或单一合并 geometry。纵向变调试听走持久 Worker 的非阻塞直接命令，不允许 Pointer Move 同步等待 response 文件；音频协议失败仍显式报告，不能静默丢弃。
- 归属：marquee anchor、deferred Ctrl click、drag-preview tile、试听活动状态和 viewport 均为 session/runtime state，不进入 Project、Undo/Redo、canonical、缓存持久化或 `.midora`。本决定不改变最终编辑命令、碰撞规则或可听映射。

## ADR-UI-037：SubVoice 下方事件编辑器与世界坐标选框裁剪

- 决定：SubVoice Timeline 与 Segment Timeline 使用同一垂直布局顺序：主钢琴卷帘、共享水平 overview/scroll、splitter、下方 Lane 编辑器。SubVoice 工具栏左侧提供 `Lanes` toggle；关闭时只折叠下方 Velocity/Event Lane 编辑器，水平导航仍保持可用。
- 决定：SubVoice 下方编辑器由旧的 Star 比例改为 Workspace-local pixel height，默认 `190 DIP`，范围与 Segment 一致为 `110..520 DIP`。拖动 splitter 的目标值在 ViewModel 边界 clamp；toggle 只改变 session 可见性，恢复时继续使用此前高度。
- 决定：Select marquee 保持未裁剪的世界坐标投影矩形；绘制时对 lane content viewport 执行 clip。不得先把矩形与 viewport 求交后再绘制完整边框，因为这种做法会在实际边缘已经出界时，于 viewport 边缘制造一条假的虚线边界。
- 归属：下方编辑器可见性、高度、当前 Lane tab、timeline viewport 与 marquee 均为每个 Workspace 的 session UI state，不进入 Project、Undo/Redo、canonical、编译、输出或 `.midora`。本决定不改变上回已确定的世界坐标框选范围和 MouseUp 命中结果。

## ADR-UI-038（Track Header 分组条款已由 ADR-UI-041 取代）：Pure MIDI 复用 Timeline 核心并以 adapter 隔离领域语义

- 决定：Arrangement、Piano Roll、Velocity Lane、Point/Event Lane、Grid/Snap、tile cache、hit testing 和通用手势继续使用同一套高性能 presentation/interaction engine；Logical Segment、SubVoice 与 Midi Segment 只通过各自 adapter 提供不可变查询、选择身份、预览投影和原子 Application command。不得复制第三套 TimelineSurface 或把 Pure MIDI 事件转换成 Logical Parameter / Template Event。
- 历史 Header 条款：曾要求 Pure MIDI Track 位于可见 Root parent 下。该显示结构已由 ADR-UI-041 取代；仍有效的是 MIDI 图标、Root 权威路由、无 Event Instrument Bind/Unbind，以及所有正式顺序/成员变更通过 Application command 提交。
- 决定：Midi Segment Editor 的 Piano Roll 与 Velocity Lane 沿用 Logical Segment 的视觉和手势；下方 Event Lane 使用完整 MIDI 1.0 Channel Voice 事件目录。CC91/93、CC120～127、Poly Pressure 与 Channel Pressure 在该 adapter 中合法；ADR-UI-029 的受限目录只适用于 Event Instrument/SubVoice。Opaque imported SysEx/Meta 仅在 Event List 中查看、移动和删除，并由只读 `Properties...` 显示 payload 摘要，不提供自由 payload 编辑器。
- 决定：共享 UI 核心不得重建 Root 生命周期、Unit 路由、跨 Track 总序、SMF Track 拓扑或 Reset。所有正式结果仍经 Project command、Semantic Validation、Compiler 与 canonical；SMF 打开工作流使用 detached candidate 与 Level 3 modal lock。
- 依据：产品所有者于 2026-08-18 接受 Pure MIDI Track 与 SMF 导入方案。正式领域、缓存、持久化与导出决定见 SRS 第 23 章、INV-050～INV-057 与 ADR-PMIDI-001～008。
- 边界：viewport、lane height、selection、tile、drag overlay 和 import review draft 属于 session/runtime；Root/Track/Segment/direct/opaque 对象属于 Project。修复共享交互或性能缺陷必须对全部 adapter 做契约回归，但不要求三套复制实现。

## ADR-UI-039（已被 ADR-UI-041 取代）：删除 Project Panel，并由 Conductor-first 两级 Arrangement 统一外层导航

- 决定：主窗口删除左侧 Project Panel；Arrangement 固定为第一 Tab、常驻、不可关闭和不可重排。其行结构固定为 Conductor、混排 Event Instrument/Root parents、展开后的对应 child Tracks。父节点 Timeline 侧留白但继续绘制 Grid；展开只由左侧 disclosure target 触发。
- 决定：Arrangement Toolbar 左侧 `Add` Fluent icon 与主菜单 Project 提供 New Event Instrument / New MIDI Channel Root。Event Instrument Header 双击打开 Editor；parent/child Header 提供完整 context menu、drag threshold、插入线、层级移动、复制和删除。Logical Track 不显示 Unbind，所在 Event Instrument 就是绑定。
- 决定：Event Instrument/Root 与 child Tracks 各有独立 runtime Mute/Solo。父 Solo 存在时忽略 child Solo；否则 child Solo 使用全局 Track 规则；两层 Mute 始终过滤。开关视觉状态不得改写另一层状态。
- 归属：mixed/child order 和 parent ownership 通过 Application command 进入 Project；expand、viewport、selection、focus、drag state 和 Mute/Solo 属于 session/runtime。Project Settings 与 Diagnostics 继续由菜单、Bottom Panel/Status Bar 打开。
- 历史依据：产品所有者于 2026-08-18 接受本 ADR 所记录的两级 parent/child 方案。该方案后续由 2026-08-20 的平铺 Arrangement 与 2026-08-21 的 Track Duplicate 命令修订取代；正式语义见 SRS 第 24 章、INV-058～062 与 ADR-CORE-046。

本历史决定中的可见 parent 行、展开/折叠、parent subtree command 与层级 Mute/Solo 已全部被 ADR-UI-041 取代；Arrangement 常驻第一 Tab、无 Project Panel、高性能自绘与菜单入口仍然有效。

## ADR-UI-040：Pure MIDI event-on-note 概览与 Conductor point 概览使用独立瓦片层

- 决定：Pure MIDI Segment 的 Direct Note 与 non-Note event 使用独立稳定 tile layers。Event 线统一颜色、位于 Note 上层、透明度固定 50%、宽度至少 1 device pixel；7-bit/Pitch Bend 按正式范围归一化高度，无标量 opaque event 使用 full-height presence line。缩小时同 device-pixel column 取最大高度，不能用重复叠画提高亮度。
- 决定：Conductor Arrangement row 不创建 Segment，直接用固定 device-size 圆点显示 event；不同类型使用稳定不同颜色和固定纵向 band，Project End Marker 保持专用线。缩小时按 tile/column/type 聚合。
- 缓存：两种概览使用可视 tick 查询、空间索引、device-pixel tile、内容 fingerprint、DPI/style/transform key 与局部失效。Pure MIDI Note/Event 分开失效；Grid、cursor、selection、hover/drag overlay 不进入稳定 tile。UI 线程只组合可视 tiles，不得为对象创建 WPF Controls，也不得在 tile 失败时回退逐对象绘制。
- 边界：bitmap/LOD 只属于 session presentation，不参与 hit test、Project、Undo、编译、canonical 或导出。命中和导航始终读取稳定 ID/index。Logical Segment 不增加 non-Note event preview。
- 依据：产品所有者明确更正 Event 线应在 Note 上层并使用 50% 透明度，以便密集内容同时可读。正式视觉与验收见 SRS 第 24.11、24.14 节及 INV-063～064。

## ADR-UI-041（已接受）：平铺 Track、Shared brace 与 Event Instrument Definition 管理栏

- 决定：Arrangement 只显示固定第一行 Conductor 与 global mixed Logical/Pure MIDI Track rows，不显示 Event Instrument Usage 或 MIDI Channel Root parent row。独立 Track 与 Fixed Root members 可自由混排；同 Usage 或 Auto Root 的多 Track 成员必须连续，并以左侧 Shared brace 表示。Brace 是独立命中区，可整体拖动，并提供独立 runtime Mute/Solo；Track 自身 Mute/Solo 保持独立。
- 决定：Track body 拖入 Shared block 内部时加入该组；block 顶/底各 `8 DIP` 实心插入带表示置于组外。外部 Track 只能追加到 block 末尾，组内 Track 可精确重排；拖出后建立独立 Usage/Auto Root，最后成员离开时原 owner 随同删除。Fixed route 作为 Track property 显示，但底层 Root 仍是唯一权威身份且永不为空。
- 决定：Arrangement 左侧 ruler header 提供 Fluent guitar 单图标 toggle，以显隐 Event Instrument Definition 管理栏；同一区域承载 `Add` 与 Reset All Monitoring。Definition 可独立创建、复制、剪切、粘贴、删除、重命名、编辑和排序，并提供 `Add Logical Track Using This Instrument`；删除最后一条 Track 不删除 Definition。`Add` 菜单提供空 Logical Track、选择/便捷创建 Definition 后建立独立 Usage 的 Logical Track，以及配置/复用 route 后建立 Raw MIDI Track。
- 2026-08-21 命令修订：Logical Track 普通 `Duplicate` 创建引用同一 Definition 的新独立 Usage，并在源 Shared block 之后插入；`Duplicate and Share State` 才保留源 Usage并紧邻源 Track 插入。Track 菜单提供 `Edit Event Instrument...`，不再提供 `Duplicate Instrument Only`；Definition Browser 的普通 Duplicate 仍只复制 Definition。`Ctrl+D` 固定调用普通独立 Duplicate。
- 2026-08-21 路由修订：共享 Fixed Root 的任一成员 Track 都可通过自己的 `MIDI Route Settings...` 修改唯一 Root 的 Channel Mode；多成员时先显示影响数量并确认，确认后原子更新全部成员，不跳转到另一套共享设置命令。
- 归属：global order、Usage/Root membership 与 Definition order 经 Application command 进入 Project/History；brace hover、插入带、管理栏可见性、selection、drag transient 与 Mute/Solo 属于 session/runtime。所有时间线仍使用现有 tile/cache/hit-test 核心，不能因平铺重构退回逐对象 WPF Controls。
- 依据：产品所有者于 2026-08-20 接受平铺结构、自动 Usage、非空内部 Root、Fixed property UX、Shared block drag 和独立 Definition 浏览工作流。正式领域与持久化规则见 ADR-CORE-046、SRS 第 24 章及 INV-058～064、INV-073～074。

## ADR-UI-042（已接受，2026-08-22 修订）：删除 Global Inspector、Bottom Panel 与 Details/Tasks，并迁移为事务式 Properties

- 决定：主窗口永久删除右侧 Global Inspector、分隔条、View 菜单入口及其宽度/可见性 Application Preference。Active Workspace Selection 不再持续驱动任何全局属性面板；选择、hover、后台诊断和 Workspace 切换都不得令另一块全局 UI 自动改变编辑目标。
- 决定：属性编辑归属对象自身 Workspace，但统一由固定目标模态 `Properties…` 呈现。对话框编辑 Draft，`OK` 以一个原子 Application command 提交，`Cancel` 或验证失败不改变 Project；多选 Mixed 字段默认禁用，显式开始统一值后才可编辑，并可逐字段还原。
- 决定：Logical/Midi Segment、Logical/Direct/Template Note、Logical Parameter/Direct/Template Event、Value Curve Point、Conductor Event 和 Event Instrument 内部结构对象均使用相应语义包装。Direct MIDI 不暴露 raw Data1/Data2；opaque payload 只读。UI 不显示 Stable ID、内部引用编号或内部序号。
- 决定：Event Instrument 仅保留 Configurations / SubVoice Tab；Parameters/Properties Tab 删除。Parameter Mapping 的 Source、SubVoice、Target Kind、适用 CC/RPN/NRPN 与其余属性在同一 Properties 对话框创建和编辑，不再拆分 route 步骤。
- 决定：主窗口删除 Bottom Panel、Details/Tasks Tab 和 Task history。Diagnostics 只在独立 Workspace 呈现；一次只呈现当前前台任务 overlay。状态栏截断消息保留显式全文查看能力，但它不是通用对象 Details。
- 决定：播放/前台任务锁定期间，Properties 的 Project-backed 输入 Disabled；只读 Properties 与 Diagnostics 可查看。属性提交必须继续经过 Application command、正式验证、Undo/Redo 和编译失效链，UI 不直接改 Domain 对象。
- 性能边界：Selection、hover、viewport、播放指针和诊断刷新不得重建属性表。属性投影只在显式打开模态对话框时按目标对象构建；大型选择只物化实际需要的字段/对象，不得遍历未选中的大型 Segment 内容。
- 依据：产品所有者于 2026-08-22 明确决定完全迁移并删除 Inspector，并批准先按上述推荐方向全量实施、再逐项进行 UI 验收。该决定取代 SRS 第 17 章 Global Inspector、相关默认偏好和把 Inspector 作为唯一精确编辑入口的旧要求；正式同步见 SRS 第 17、18、20、23 章。

## ADR-UI-043：超大型分页 MIDI 编辑采用批量身份解析、目标碰撞查询与 Selection 指标快照

- 决定：Direct MIDI paged source 提供 stable-ID set、Note start-key set 和 Event target-key set 的批量查询。Domain collection 负责把 source/replacement/added 统一投影为当前可编辑对象；UI 和 Application 层不得重新全量枚举 Segment 来定位少量对象。
- 决定：Selection revision 变化时，各 active timeline projection 一次批量解析已选对象，并冻结按对象类型聚合的 tick/lane/value 边界及 earliest item。拖动约束、音高试听 anchor 和 Pointer Move 直接读取该快照；正式编辑后批量重建一次并移除不存在的 stable ID。
- 决定：Direct Note/Event 多字段修改通过 collection-local batch scope 合并 source identity 判定和 generation publication。精确碰撞策略按 Segment 合并全部目标 key，并让 paged pack 每个候选 page 最多解码/扫描一次；collision winner、Undo/Redo 和 imported duplicate 边界不变。
- 决定：不改变 Note 精确键的 Length/Velocity 等编辑直接绕过碰撞策略；Direct Note 起点候选从排序 NoteOn endpoint page 二分读取。Logical Note 与 Template Note 的常用创建、Move、Copy-Move 和边界调整同样提交目标 `(tick, key)`，而不是把整个 Segment/SubVoice 注册为碰撞范围。
- 决定：Move/Copy-Move 的 selection-only tile 可以在后台未完全就绪时渐进组合已完成 tile。缺 tile 不得触发 UI 线程逐帧重建整个 Selection geometry；原始选中对象仍保留在源位置，因此渐进目标层不会造成已提交数据错觉。
- 归属：批量查询索引、Selection metrics、materialized-source identity 和 raster in-flight 状态均为 runtime/session 数据，不进入 `.midora`、canonical、MIDI/Audio 输出或 Application Preferences。本决定不改变 Project 语义、碰撞规则、音频结果和缓存持久化。
- 依据：产品所有者使用 `Krash Noets 6.7 million.mid` 观察到稳定视图流畅但任意编辑和大选区提交严重阻塞；源码审计确认旧路径存在 `O(total objects × selected IDs)` 定位、逐目标分页查询和每帧选区扫描。

## ADR-UI-044：About 运行时资源快照与全局入口

- 决定：About 改为独立只读模态窗口，继续从唯一程序集版本来源展示产品版本，并增加 Desktop、Audio Worker(s) 与 Combined 的 CPU、Working Set 和 Private Memory 定时快照。统计只读取 ADR-AUDIO-020 的运行时观测接口；采样失败不得关闭窗口、修改状态或影响音频线程。
- 决定：`F12` 是无修饰键的全局 About 快捷键，在普通文本焦点下仍可用；打开前遵循既有模态界面优先规则，仅停止 Event Instrument 底部钢琴预览，不得错误停止项目播放。
- 决定：CPU 百分比按整机逻辑处理器总容量归一化，并在界面中明确说明；第一帧尚无时间差时显示 Measuring，而不是伪造 `0%`。
- 边界：About 不持久化采样、不建立历史图表、不驱动任务、缓存或降级策略，也不承诺 Windows Task Manager 必然按同样方式折叠进程。

## 小决定审计

以下均是局部、可替换且不改变可听结果/持久化/公共业务接口的小决定，按用户授权采用推荐方案：

- 自绘入口使用 WPF `FrameworkElement.OnRender`，不引入第三方 UI/图形框架。
- 时间坐标内部使用 `long tick`；像素换算使用 `double`，提交前回到 checked integer tick。
- interval index 采用 lane buckets + sorted arrays + hierarchical maximum-end tree，不采用 R-tree。
- 高性能列表使用 WPF recycling virtualization；只有时间线内容使用专用自绘。
- UI tests 使用独立 `Midora.Desktop.Presentation.Tests`，窗口 smoke/截图测试与纯逻辑测试分层。

若后续发现必须改变大范围工作流、公开业务接口、并发模型、持久化或可听结果，暂停受影响分支并另行登记决定问题；独立 UI 工作继续。
