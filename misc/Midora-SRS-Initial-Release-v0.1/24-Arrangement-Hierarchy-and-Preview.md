# 第 24 章 Arrangement 平铺轨道、共享执行组与概览渲染

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 本章最近破坏性修订：**2026-08-21**

## 24.1 目的与优先级

本章定义 Arrangement 的正式平铺顺序、Event Instrument Definition、Event Instrument Usage、MIDI Channel Root、共享状态组、轨道拖放、Event Instruments 辅助栏，以及 Pure MIDI / Conductor 高性能概览。

本章替代 2026-08-18 建立的可见两级 parent/child Arrangement。与旧章节中的以下描述冲突时，以本章与第 22 章不变量为准：

```text
Event Instrument / MIDI Channel Root 占据可见 parent row；
parent child list 决定 Track 顺序；
Event Instrument 普通 Duplicate 级联复制 child subtree；
空 Fixed Root 合法并预留 Unit；
Logical Track 直接以 Event Instrument ID 表示独立 binding；
删除 Event Instrument 级联删除其全部 Logical Tracks。
```

本次仍是未发布开发期破坏性格式替换，不更新产品/SRS 版本，不兼容读取旧开发布局。

## 24.2 正式对象与权威顺序

### 24.2.1 Event Instrument Definition

Event Instrument 是可复用、可独立保存和编辑的重型声音定义。Project 保存独立的、有序 `Event Instrument Definition Index`。Definition：

```text
可以没有任何 Usage；
不会因为 Track 或 Usage 被删除而自动删除；
不直接占用 Channel Unit；
不直接出现在 Arrangement 轨道行；
不拥有 ordered Logical Track child list。
```

### 24.2.2 Event Instrument Usage

`Event Instrument Usage` 是 Project 内部、无用户名称的共享执行身份。每个 Usage：

```text
有稳定 ID；
引用且只引用一个 Event Instrument Definition；
被一个或多个 Logical Track 引用；
定义共享 Channel 状态、Segment 活动连通区间、Overlap 域、编译 dirty 域和缓存 owner；
不进入普通 Event Instrument 管理列表；
成员数降为 0 时必须在同一原子编辑中删除。
```

Usage 不是 Event Instrument Instance。Instance 仍表示一次 Logical Note 触发形成的编译/运行时实例。

一个成员的 Usage 在 UI 中表现为普通独立 Logical Track；两个及以上成员的 Usage 表现为 Shared Logical Track block。

### 24.2.3 MIDI Channel Root

MIDI Channel Root 仍是 Pure MIDI 的持久 Channel Unit、共享状态和生命周期身份。每个 Pure MIDI Track 必须引用且只引用一个 Root。

所有 Root 必须至少有一个 Track。Auto/Fixed Root 的最后一条 Track 被删除、剪切、改路由或移走时，Root 必须在同一原子编辑中自动删除；Undo 必须以原稳定 ID、路由、模式和成员关系恢复它。

Fixed Root 不具有独立用户可见对象生命周期：

```text
没有“创建空 Fixed Root”入口；
没有独立 Fixed Root row；
没有独立 Fixed Root 排序或删除命令；
Port、Channel、Routing Mode 和 Channel Mode 在 UI 中表现为 MIDI Track 的 Route 属性；
底层仍只在 Root 保存一份权威值，Track 不复制这些字段。
```

Auto Root 只有在多个 Track 共享时，才以 Shared MIDI Track block 的形式显式表现其组身份。

### 24.2.4 全局 Arrangement Track Order

Project 保存唯一、有序、tagged 的 `Arrangement Track Order`：

```text
Logical Track | Pure MIDI Track
```

它是以下语义的唯一权威：

```text
Arrangement 可见轨道顺序；
SMF 导入后的用户轨道顺序；
Pure MIDI SMF Track Projection 顺序；
同 Usage / Root 的跨 Track 同 tick 确定性顺序；
Auto Root 与 Usage 的首次出现顺序；
Track 编号、Move Up/Down 和拖放插入位置。
```

Conductor 固定显示为第一行，但不进入该集合。Event Instrument Definition order、Usage membership 和 Root membership 不得替代或重复保存 Track order。

## 24.3 Logical Track 绑定与空壳状态

Logical Track 保存可空的 `Event Instrument Usage ID`，并通过 Usage 间接引用 Definition。

`New Logical Track` 可以创建未指定 Usage 的空壳 Track。该状态仅用于安排和后续指定乐器：

```text
允许命名、排序、复制和删除；
没有 Segment/Note/Parameter 内容时不产生诊断；
在指定 Event Instrument 前禁止创建或粘贴音乐内容；
若结构损坏或非法路径形成“有内容但无 Usage”，编译为 Error。
```

`New Logical Track with Instrument...` 或 `Add Logical Track Using This Instrument` 必须创建新的独立 Usage，再创建并绑定 Track；不得默认把新 Track 加入同 Definition 的既有 Usage。刚创建新 Definition 时，Definition + Usage + Track 是一次原子编辑，成功后打开并激活 Definition Editor；选择既有 Definition 时保持 Arrangement 为活动 Workspace。

Track 菜单必须提供：

```text
Edit Event Instrument...
Duplicate
Duplicate and Share State
Assign / Change Event Instrument...
Share Instrument State With...
Make Independent
```

`Edit Event Instrument...` 只打开当前 Usage 引用的 Definition，不修改 Project；未绑定 Track 不存在目标。普通 `Duplicate` 与显式 `Duplicate and Share State` 的 membership 和插入位置见第 24.8.2 节。

`Share Instrument State With...` 选择目标 Logical Track/Usage。目标使用相同 Definition 时直接加入；Definition 不同时属于 Rebind，必须使用既有影响审查，取消或失败不得留下部分修改。

`Make Independent` 创建引用同一 Definition 的新 Usage，并只迁出当前 Track。旧 Usage 无成员时自动删除；变为单成员时保留但隐藏 block brace。

## 24.4 共享执行语义

### 24.4.1 Usage 活动连通区间

对一个 Usage 的全部参与 Logical Tracks，将其 Segment Project ranges 求并集；重叠或首尾相接的 ranges 形成一个 `[startTick, endTick)` 活动连通区间。该区间是真正的共享 Channel Group 生命周期。

当 Event Instrument 未启用 Per-Note Instance Isolation：

```text
一个 Usage 活动区间按 SubVoice 分配一个共享 Channel Unit；
Usage 内所有成员 Track 的实例共享该 SubVoice 的 Channel 状态；
同 tick 顺序使用全局 Arrangement Track Order，再使用对象显式顺序/稳定顺序；
Overlap Policy / Scope 在整个 Usage 内验证，不能通过拆成多条 Track 绕过；
成员 Segment End 只精确关闭该 Segment 拥有的 Note/Instance；
成员 Segment End 不发送 Usage 级 CC120/Reset，不杀死 sibling Track 的 Note；
Usage 活动区间结束才执行精确 NoteOff、CC120、最终 Reset 和 Unit 释放。
```

启用 Per-Note Instance Isolation 时，每个 Note 仍使用独立 Channel Group；Usage 继续承担 Definition 绑定、顺序、Overlap 域、监控来源与缓存 dirty owner，但不把并发 Note 合并到同一 Unit。

### 24.4.2 Usage 缓存与增量编译

Logical Segment 可继续产生规范化 fragment，但共享状态的正式合并层必须以 Usage 为 owner：

```text
Segment normalized fragment
→ Usage merged event/checkpoint stream
→ Usage/SubVoice raw PCM fragment
```

编辑一个成员 Track 时，dirty 起点至少回退到该 Usage 中最早受影响 tick；后续只可在状态与活动 Note 集均收敛后复用。不得错误失效其他 Usage/Root，也不得按成员 Track 分别合成后求和。

## 24.5 Root 路由与 Track 属性 UX

### 24.5.1 Fixed 路由

编辑某 MIDI Track 的 Fixed route 实际执行 Root membership 变更：

```text
目标 Port.Channel 未使用：创建新 Fixed Root并迁入 Track；
目标 Port.Channel 已由 Fixed Root 使用：加入该 Root；
离开后的旧 Root 无成员：自动删除；
整个变更是一个失败原子 Undo。
```

同一个 Port.Channel 只能有一个 Fixed Root。Fixed Track 可以在全局 Arrangement 中任意分散，不能强制连续；这用于保持导入 SMF 的 MTrk 顺序。

每条 Fixed Track Header 显示 route chip。多个 Track 共享同一 Fixed Root 时，chip tooltip/settings 显示 `Shared with N tracks`，悬停可低强调高亮可见成员。用户可以从任一成员 Track 的 `MIDI Route Settings...` 修改该 Root 唯一的 Channel Mode；多成员时必须明确提示受影响 Track 数并确认，确认后一次原子更新 Root 与全部成员的有效显示/行为。不得以报错要求用户寻找另一个 `Shared MIDI Route Settings` 入口。同一 Port.Channel 不允许同时拥有 Melodic 与 Percussion 两种模式。

### 24.5.2 Auto 路由

一个 Track 的独立 Auto Root 表现为 `Auto`。多个 Track 共享一个 Auto Root 时，它们必须在全局 Track Order 中连续，形成 Shared MIDI Track block。

Auto→Fixed 时解除连续约束且不自动改动全局顺序。Fixed→既有 Auto block 时加入 block；Fixed→新独立 Auto 时创建新 Auto Root。将若干非连续 Fixed members 作为一个整体改为 Auto 前，必须预览并确认将它们收拢为连续 block 的原子重排。

### 24.5.3 新建 MIDI Track

Arrangement `+ → New Raw MIDI Track...` 提供：

```text
New Auto MIDI Channel
New Fixed MIDI Channel (Port, Channel, Melodic/Percussion)
Use Existing MIDI Channel
```

选择未使用 Fixed Port.Channel 时原子创建 Root + 首条 Track；选择已使用 Port.Channel 时加入现有 Root，并禁用会与其 Root 配置矛盾的字段。不存在仅创建 Root 的提交结果。

## 24.6 平铺 Arrangement UI

### 24.6.1 行与工具栏

Arrangement 行顺序固定为：

```text
Conductor
Arrangement Track Order[0]
Arrangement Track Order[1]
...
```

不显示 Event Instrument 或 Root 空白 parent row。Logical/Pure MIDI Track 均直接承载 Segment，继续使用手工渲染、可视 tile 和范围查询，不得为 Segment/Note/Event 堆 WPF Control。

Arrangement 左侧 ruler header 与 Event Instruments pane title bar 等高，并承载以下紧凑入口：

```text
Fluent guitar 单图标 Event Instruments pane toggle
red Fluent + creation menu
Fluent speaker_2 Reset All Monitoring command
```

上述入口不得占用独立空白 Track row；小节号只绘制在右侧时间内容区。`Reset All Monitoring` 一次性清空 Track 与共享 Usage/Root 的运行期 Mute/Solo 状态，不修改 Project、Undo/Redo 或 canonical；播放中只提交一次一致的 monitoring 更新，失败时恢复调用前状态。其他 Timeline 工具继续位于 Arrangement 顶部工具栏。

Arrangement 顶部工具栏必须显示指针当前所在的 `(absolute Project tick)`，并在读数右侧以分割线隔开后续工具；读数使用 Primary text 前景色，与 Event/Parameter Lane 坐标读数一致。tick 使用当前 Operation Grid/Snap 的正式目标坐标。指针不在 Arrangement 时间内容区时，分割线与读数整体隐藏。该读数只是会话期 transient UI state，不修改 Edit Cursor、Project、Undo/Redo、canonical 或持久化。

创建菜单固定提供：

```text
New Logical Track
New Logical Track with Instrument...
New Raw MIDI Track...
```

主菜单 Project 也提供等价入口。播放或其他 Project 编辑锁期间创建/结构编辑命令禁用。

### 24.6.2 Event Instruments 辅助栏

Event Instruments pane 是 Arrangement 内的 Definition Browser，不是旧 Project Panel。它显示全部 Definition，包含未使用项及 usage/track count，并保存独立 Definition order。

支持：

```text
New / Copy / Cut / Paste / Duplicate / Rename / Edit / Move / Delete
Add Logical Track Using This Instrument
拖 Definition 到 Arrangement 空隙以创建独立 Usage + Logical Track
```

删除被引用 Definition 不得级联删除 Track。默认必须阻止，并列出引用 Usage/Track；用户须先改绑或删除相关 Track。删除最后一个 Track/Usage也绝不删除 Definition。

Event Instruments pane 属于 Arrangement 会话 UI，Project 打开时默认折叠；用户可从 Arrangement 左上角显式切换显示。该可见性不进入 Project、Undo/Redo 或 canonical。

每个 Definition 项左侧固定显示由其正式不透明 sRGB 颜色派生的窄竖线。该竖线在未选择、hover 与选择状态下均保持 Definition 颜色；该列表不得复用会以红色左边框覆盖颜色语义的通用选中态，选择仍可使用背景变化表达。

### 24.6.3 Header 与块视觉

所有 Track Header 预留同宽的左侧 group gutter，使独立 Track 与 block member 的标题对齐。

以下对象在成员数至少为 2 时形成连续 block：

```text
同一 Event Instrument Usage 的 Logical Tracks；
同一 Auto MIDI Channel Root 的 Pure MIDI Tracks。
```

block gutter 绘制跨全部成员的大括号。括号区域是独立 hit target：hover 时大括号及其 gutter 矩形背景整体高亮，且不得同时触发成员 Track Header 的 hover；在其右键菜单保持打开期间，同一大括号及 gutter 高亮必须保持，菜单关闭后才清除该 context highlight；按下后越过通用拖动阈值才拖动整个 block；右键提供共享状态/route、Mute/Solo group、Make Independent 等适用命令。Fixed Root members 不绘制 block。

Track Header 保留类型图标、名称、route/instrument 摘要、Mute/Solo、hover/pressed 和菜单。Header 左键或右键点击形成会话内单选：普通 Track 按 stable ID 保存，固定唯一 Conductor 以其固定 lane identity 保存；对已经单选的 Header 再次普通左键单击必须取消该 Track 单选。选中项以明确但低噪声的背景与内边框显示。该选择只作为 Track 命令和快捷键目标，不替代 Segment/Note/Event 的 Workspace Selection。brace 永不成为 Track 单选目标。Arrangement 空白处左键或右键均清除 Track 单选；空白右键菜单目标必须来自本次指针 hit test，不得复用旧 Track。Track 删除或 Project 切换时失效选择必须自动清除，且该状态不持久化、不进入 Undo/Redo。

Logical Track Header 的 Instrument 摘要是独立命中目标：有有效 Definition 时 hover 使用强调前景并显示 Hand，单击打开或激活该 Definition Editor；无绑定时保持普通不可点击摘要。该链接不得触发 Header 单选切换、拖动或 Mute/Solo。

Track 内容区在最后一个可见 Track 的底边绘制与行间一致的分割线，使 Track 区域与后续空白明确分界。

## 24.7 Track 拖放与组变更

### 24.7.1 一般规则

拖放开始必须越过通用总移动阈值。所有排序、Usage/Root 创建删除、Definition rebind 和 global order 修改构成一次原子 Project Edit；失败或取消时 Project 完全不变。

Logical 与 Pure MIDI Track 不允许跨类型成组。brace drag 只整体重排 block，永不合并到另一个 block。

### 24.7.2 目标区域

共享 block 的上、下边缘内侧各提供约 8 DIP 的外部插入 hit zone，实际绘制 3–4 DIP 的低强调实线；中间 body 是“加入 block”目标。目标切换使用约 4 DIP hysteresis，避免边缘抖动。命中解析、预览与最终 drop 必须冻结为同一个语义目标：一旦解析为 top/bottom exterior strip，插入线必须固定绘制在整个 block 的真实上/下外边界，不得因当前成员 lane 或重排归一化而短暂落入 block 内部间隙。同 block member 指向 exterior strip、即本次 drop 会脱离到 block 上/下方时，当前上/下目标边界必须在通用 strip 之上使用更粗的强调实线。外部 Track 指向 block body 时，只显示整个目标 block 的虚线外框，不得同时显示指针下成员 Track Header 的 hover 高亮。

```text
外部 Track → block body：加入目标并追加为最后成员，目标 block 全体显示虚线外框；
外部 Track → block top/bottom strip：放在 block 前/后，保持或变为独立；
其他 block member → target body：离开旧组并加入目标末尾；
同 block member → member insertion gap：精确内部重排；
同 block member → block exterior strip：脱离为新的独立 Usage/Auto Root；
brace → global gap：整体移动 block。
```

同 block 内第一行上半部和最后一行下半部必须分别能定位到第一/最后成员。相邻 block 间归一化为一个全局插入 gap，不得出现两个竞争目标。

### 24.7.3 singleton 与 Fixed chip drop

singleton Usage/Auto Root 没有 brace，因此其 Instrument/Auto chip 是显式 join target。Fixed Track 可分散排列，只有其 Fixed route chip（而非整行 body）是“加入该 Fixed Root”的目标；整行其余区域继续表示普通排序。

不同 Definition 的 Logical Track 拖入 Usage 时，使用琥珀色虚线预览并执行 Rebind 影响审查。取消时不移动 Track。

## 24.8 Copy / Cut / Paste / Duplicate / Delete

### 24.8.1 Event Instrument Definition

Definition Copy/Paste/Duplicate 只深拷贝 Definition 与全部内部对象，生成并重映射全部稳定 ID，不复制任何 Track 或 Usage。Event Instruments pane 的普通 `Duplicate` 是 Definition-only 复制入口；Track/Usage 上下文不得再显示同义的 `Duplicate Instrument Only`。

### 24.8.2 Logical Track

Logical Track 的普通 `Duplicate` 深拷贝 Track、Segments 和全部内容，但不得保留源 Usage：

```text
源 Track 已绑定：创建新稳定 ID 的独立 Usage，并让它引用源 Usage 的同一 Definition；
源 Track 属于 Shared Usage block：将新 singleton Track 插入整个源 block 之后，不拆开原 block；
源 Track 为绑定 singleton：将副本插入源 Track 之后；
源 Track 为未绑定空壳：副本仍未绑定、插入源 Track 之后，并保持无内容约束。
```

`Duplicate and Share State` 是独立的显式命令，只对已绑定 Logical Track 可用。它深拷贝相同 Track subtree，但保留源 Usage，并把副本紧邻插入源 Track 之后、留在同一连续 block；源 Usage 原为 singleton 时由此形成两成员 Shared block。

两种 Duplicate 都不复制 Definition，且必须为 Track、Segments、Notes、Lanes、Points、Curves 等 owned objects 生成新稳定 ID。创建新 Usage、global order 插入与 membership 变更必须构成一个原子 Project command 和一个 Undo；失败不得留下空 Usage 或部分副本。成功后按第 20.3.12 节选择新 Track，该 Selection 仍只属于 session UI state。

Paste 到明确 Usage target 时加入目标；普通空白 Paste 创建引用同一 Definition 的新独立 Usage。未绑定空壳 Track 的 Clipboard 副本仍未绑定且必须保持无内容约束。

### 24.8.3 Pure MIDI Track

Pure MIDI Track Duplicate 深拷贝 Track/Segments/direct/opaque 内容并保留可见 route：Fixed 副本加入同一 Fixed Root；Auto 副本加入同一 Auto Root。普通 Paste 到 route/block target 时加入目标；空白 Paste 使用来源 route，Fixed route 已存在时加入现有 Fixed Root，不创建冲突 Root。

### 24.8.4 Cut / Delete 与自动 owner 清理

Header/brace Drag Move 在一个原子命令中保持对象稳定 ID。Clipboard Cut 先冻结不可变快照再执行删除；后续 Paste 与普通 Copy 一样创建新的 Track/内容稳定 ID，避免 Cut 被 Undo 后再 Paste 时发生身份冲突。删除 Track 继续按内容确认规则执行。任一删除/移动后 Usage/Root 成员数为 0 时自动删除 owner；该删除命令的 Undo 必须恢复原 Track、owner、stable ID、global order、membership 和配置。

## 24.9 Mute / Solo

Track Mute/Solo 仍只属于运行期，不持久化、不进入 Undo、不影响 canonical、MIDI Export 或 Audio Render。Shared block/root 可以有独立 group Mute/Solo runtime state；它不改写成员 Track 开关。

运行期过滤必须按来源精确释放被过滤 Track 的活动 Note并恢复重新进入成员所需状态。不得因单个 Track Mute 对整个 Usage/Root发送CC120/Reset，不得杀死 sibling Note、卡住 producer 或无限 Buffering。

## 24.10 跨 Logical / Pure MIDI Note 剪贴板

Logical Note 与 Direct MIDI Note 只转换共同字段：relative Tick、Gate Length、Key、NoteOn/Instance Velocity。Logical→Direct 的 NoteOff Velocity 为 0；Direct→Logical 丢弃 NoteOff Velocity；Direct→Direct 保留它。该能力不转换 Segment、参数、Channel Event、Definition、Usage 或 Root。

## 24.11 Segment、Conductor 与编辑器概览

Pure MIDI Segment 的 Note 与 non-Note event、Logical Segment 的 Note 与 Logical Parameter point 分别使用独立 tile/layer。event / parameter 线位于 Note 上层、透明度 50%、至少 1 device pixel，高度按各自正式值域归一化；同 device column 使用最大高度聚合。Logical Parameter 不得为该概览恢复曲线插值，只投影正式离散点。

Conductor 固定第一行，直接显示按类型着色且大小不随缩放变化的圆点；End Marker 仍为专用竖线。

Arrangement ruler 另从 Conductor Marker 建立只读、不可命中的标签投影。标签左边界精确定位到 Marker tick，使用低强调浅灰圆角边框与 secondary text；小节号贴近 ruler 底部刻度，Marker 标签使用其上方空间。该投影不替代 Conductor Editor 的正式选择、命中或编辑入口。

Logical/Pure MIDI Segment Editor 的 horizontal overview 使用两个独立内容层和缓存：

```text
Note layer：只在 NoteOn / Gate Start 的真实 tick 绘制蓝灰色 1 device-pixel 竖线；
Event layer：只在 non-Note MIDI event / Logical Parameter point 的真实 tick 绘制暗红色 1 device-pixel 竖线；
Event layer 位于 Note layer 上方，且必须与播放指针红色可辨。
```

普通内存内容与分页 Direct MIDI 内容必须产生一致投影。不得把 Note Gate End、Note 持续区间、page min/max/count 跨度或已删除/移动对象的旧位置当作新 onset/event；没有对象的 tick 区间必须保持空白。缓存键至少区分内容指纹、extent 和 device-column width，Copy-on-write overlay 必须在叠加当前编辑值前排除被替换/删除的源 stable ID。

Logical / Pure MIDI Segment Arrangement 概览与 Conductor row 概览均必须：

```text
Conductor 继续只查询可见 source pages/range；
Arrangement Segment 的最高精度使用 `96 pixels / quarter note`、64-pixel 高度的固定基准 raster；较低精度只允许使用该基准的半八度 `1 / 2^(n/2)` 固定 LOD，不得把任意 viewport zoom/DPI 后的 Segment 宽高直接放入缓存身份；
Segment 的正式 tick 长度与 Project TPQN 决定固定基准总宽度，禁止把任意长度 Segment 压缩进一个固定总像素宽度；
固定 raster 以 256-pixel tile 分块；viewport 选择第一个 source-pixel 比例不大于当前显示比例的固定 LOD，禁止向下采样一像素对象，也不得为每个精确缩放值产生一套 tile；完整 Segment 目标矩形只执行一次 device-pixel 对齐，所有 tile 边界必须从该矩形的同一 device width 派生，使 pan 只能做整 device-pixel 平移而不能改变最近邻取样相位；
Arrangement Snapshot 发布后，以最多两个后台 raster worker 持续预热全部 Segment；每个 Segment 的完整预热层必须选择不超过 4 个 tile 的固定 LOD，前台 Draw 不等待预热完成；
当前 viewport 内的 Segment 必须先使用 `max(0, warmup LOD - 2)` 取得同内容指纹、同颜色身份的完整粗略 fallback；固定 LOD 每八度含两级，因此该 fallback 相对后台 warmup 层提高一倍水平分辨率，完整 Segment 最多 8 个 tile。fallback LOD 只由 Segment 长度与 Project TPQN 决定，不得随 display LOD 或 viewport 缩放改变。单个 Segment 只有在该 fallback 的全部 tile 就绪后才一次性可见，禁止暴露半幅粗略图；
当前显示层比 fallback 更粗时，允许以最近邻缩放该已缓存 fallback 作为临时底图；缩小视图不得为 fallback 生成新的 cache identity，也不得让已有概览重新变为空白。全部可见 Segment 的 fallback 就绪前，不得启动新的当前目标 LOD 请求；barrier 完成后，当前目标 LOD tile 就绪一个即独占其横向目标范围，该范围内禁止继续绘制 fallback，以免透明像素同时暴露两层。未被目标 tile 接管的范围继续显示 fallback，且粗缓存不得因替换而删除。不可见 Segment 的低并发预热可继续进行；
内容指纹或颜色变化只失效对应 Segment 的固定 LOD tile，未变化 Segment 在相同固定 LOD 间直接命中；
使用有界分块缓存和局部失效；
不创建逐对象 WPF Controls；
不把 Grid/cursor/selection/hover 烘焙进稳定 tile；
不从 bitmap 反推 hit test 或音乐语义；
在数千万对象时不建立全 Segment render array/dictionary/index。
```

预热属于低并发、可丢弃的 session presentation 工作；它不得取得 Project edit lock、不得阻塞 UI thread、音频 producer 或音频 IPC。缓存仍服从全局有界 LRU；Snapshot generation 改变时，旧预热结果可以留作普通内容键命中，但旧队列不得继续驱动当前视图重绘。

## 24.12 SMF 导入与导出顺序

`Open MIDI as New Project` 按源 MTrk index 排列导入 Track；单 MTrk 被 Port/Channel 拆分时，派生 Track 按该 MTrk 中 effective Port.Channel 首次出现顺序紧随排列。标准 MIDI 无法表达 Auto Root，因此普通外部导入产生 Fixed Roots。

Pure MIDI SMF Track Projection 过滤全局 Arrangement Track Order 中的 Pure MIDI Tracks；Root 不再决定 MTrk 分组顺序。同 Root 同 tick canonical merge 也使用该全局顺序。Logical Unit MTrk 继续按分配后的 Port/Channel 顺序位于 Pure MIDI MTrks 之后。

Midora Sequencer-Specific Meta 可以保存 Track/Root stable ID、membership、routing、mode 和 Auto identity，以支持 Midora→MIDI→Midora 结构恢复。若第三方重排使恢复出的 Auto members 不连续，导入必须保留文件 Track order、放弃该 Auto 恢复并按实际 Fixed Port.Channel 重建，同时给出一次性 Warning。

## 24.13 持久化

`project.json` 必须保存：

```text
ordered Event Instrument Definition index；
ordered Event Instrument Usage index；
ordered MIDI Channel Root index；
ordered tagged Arrangement Track index；
各对象文件路径与名称快照。
```

对象文件必须保存：

```text
Event Instrument：Definition 本体，不保存 Track child list；
Event Instrument Usage：ID + Definition ID；
Logical Track：可空 Usage ID + Track/Segment 内容；
MIDI Channel Root：Routing/Port/Channel/Mode，不保存 Track child order；
Pure MIDI Track：Root ID + Track/Segment/direct/opaque 内容。
```

membership 由 Track 的 owner ID 表达，顺序仅由 Arrangement Track index 表达；不得再保存 parent ordered child arrays 形成第二套顺序。以下结构均拒绝打开/编译：

```text
Usage 或 Root 无成员；
非空 Logical Track 无 Usage；
Track owner 缺失或 kind 错误；
同一 Track 在 Arrangement index 缺失或重复；
Shared Usage / Auto Root 成员在 Track order 中不连续；
两个 Fixed Root 使用同一 Port.Channel；
对象 ID、manifest、index 与文件 path 不一致。
```

Definition pane 展开、Arrangement viewport、selection、header focus、Mute/Solo 和 drag preview 属于 session/runtime state，不进入 `.midora`。Format 3 只额外保存第 3.11、16.33 节版本化的 Onion source/opacity/enabled 与 All-Tracks raw/compiled presentation；这些字段不属于 Track/Usage/Root source、global order、Project Modified 或 Undo/Redo，也不得改变本节 membership/顺序权威来源。

## 24.14 失败原子性与验收门

至少覆盖：

```text
混合 Logical/Pure global track order 保存、重开、Undo/Redo；
SMF 导入/导出保持源 Track 顺序与多 Channel bucket 次序；
Definition 0 usage 持久化，最后 Track 删除不删除 Definition；
Usage/Auto/Fixed Root 最后成员离开时同事务删除且 Undo 恢复原 ID；
Fixed route UI 改属、既有 P.C 合并及共享 Mode 确认；
shared Usage 活动连通区间、跨 Track overlap、同 tick 顺序与 Unit 数；
成员 Segment End 不清空 sibling state/note，Usage end 才最终 cleanup；
Auto/Usage block 连续性、brace reorder、body join、edge detach 和 hysteresis；
exterior strip 的预览线与最终 drop 同处 block 外边界，组内成员脱离时显示粗边界且不残留内部插入线；
Fixed Track 任意分散且 route chip join；
不同 Definition rebind 取消/失败原子性；
未绑定空壳限制与非法非空无 Usage诊断；
Definition/Track/Usage/Root Copy/Paste/Duplicate 的 ID/remap/membership；
Logical ordinary Duplicate 建立独立 Usage 并位于源 block 之后，Duplicate and Share State 保留 Usage 并位于源 Track 之后；
未绑定 Logical Track Duplicate 保持未绑定无内容约束，Ctrl+D 不得隐式共享；
Pure MIDI event-above-note preview、Conductor tile 和极端范围查询性能；
Arrangement ruler Marker label 的 tick 对齐、只读命中边界与小节号共存；
Segment horizontal overview 的真实 onset/event tick、空洞、移动/删除 overlay 与缓存命中一致性；
后台 Full/Incremental 对共享 Usage 完全等价。
```

## 24.15 明确非目标

初版不提供：

```text
可见 Event Instrument Usage 管理器或 Usage 命名；
空 MIDI Channel Root 或 Unit 预留 UI；
Event Instrument/Root 的 Arrangement 空白 parent row；
按 Definition 自动让所有 Logical Tracks 共享状态；
Fixed Root members 强制连续；
从概览 bitmap 反推命中；
把 Route 字段复制到每个 Pure MIDI Track 形成多份权威值；
旧树形开发布局的迁移、双写或兼容读取。
```
