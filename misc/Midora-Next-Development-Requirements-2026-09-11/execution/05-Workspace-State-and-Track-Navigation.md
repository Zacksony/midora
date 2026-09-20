# 工作区恢复、同轨道共享与 Tracks 导航：B1 详细计划及 B2/B3 边界

初稿：2026-09-14；B1/B2 实施：2026-09-18；B2 人工验收归档：2026-09-20；B3 任务细化：2026-09-20。实施前源码基线：`caf8cec81e8d5b019df870fa871a46e464522d01`。状态：**B1、B2 已实施并通过用户验收；B3 已完成计划细化但未实施；TRACK 未实施** 。B2 的独立任务、UAT-B2-01～06 及证据见 [06-B2 执行文档](06-Workspace-State-Persistence-B2.md)；B3 的任务、决策项、自动门和 UAT 见 [06b-B3 执行文档](06b-Workspace-State-Restore-B3.md)。详见 [B1 实施记录](../../Midora-B1-Workspace-State-Implementation-2026-09-18.md)。

入口：[执行总计划](00-Execution-Plan.md)。需求／源码依据：[03](../03-Multi-Instance-and-Workspace-State.md)、[01 的 R30](../01-Interaction-and-Display.md)；唯一决定与原答：[04](../04-Decisions-and-Preparation.md) D-STATE01～03、D-TRACK01，包含 Q2 D-STATE03.d/e。本文件不重复问答，不改变已经批准的白名单。

## 1. 追踪、范围与依赖

| 维度 | 约束 |
|---|---|
| 需求／优先级 | R06、R07=P2；R30=P3，保持独立，不以新增导航面板阻塞状态专题 |
| 输入／输出 | 当前音乐 owner 身份及轻量视图描述 → 同 Track profile／局部状态 → 显式 Save 冻结 presentation；不序列化 ViewModel |
| 持久化／运行期 | 不进入音乐 Modified／Undo／canonical；监听 Mute/Solo 可保存初值但仍只过滤运行期，不影响成品输出 |
| 失败 | presentation 分区损坏只回退该区并提示；音乐仍加载。预算超限明确告知，可保存音乐和可用视图部分或取消，不静默丢失 |
| 释放 | 删除 owner 立即释放自身状态；存活 owner 可留预算内 dormant Onion 来源 ID，不持有被删对象、页、位图、任务或 lease |
| 非目标 | 不恢复 Selection／Undo 栈、任务、音频状态、缓存、自动保存或崩溃快照；不提前加入白名单外的未来字段 |

B1 的 R28/A2b 与 A3 前置已经实施并获人工验收，A4b 及追加修订也已验收并提交；仍保留各轮报告中的未测平台和性能断言波动，不能把人工验收写成所有工程门通过。不要求 R30 已实施。主 ToolMode 与事件绘线形态继承 A3 最终合同，不另造工具系统。B 与多实例 C 的保存冲突接口需共同审查，但无须让两专题整体互等。

B1 已按 Q1/Q2 修订会话内归属、默认、删除目标与 dormant 来源边界的 SRS/ADR。现行 SRS §16.28、§16.33 及第21章、INV-011/060/091/112 的文件白名单保持不变；B2 已以独立 schema 3 处理持久化扩展，外层 Project Format 4 不变，B3 仍不改变音乐 source。

## 2. 阶段交付及原任务追踪

保留原 T-ID。下面三阶段仍各按一次完整人工验收组织，B1 的内部拆分不增加验收轮次：

- **B1：会话内的正确归属与记忆。** 实施 Track 通用 profile、Segment/SubVoice 轻量局部状态、关闭再开及 owner 生命周期。只为后续保存建立可冻结的纯数据边界；不自动重开项目 Tabs。已有 Onion/All-Tracks 能力在 B2 schema 3 中保持并继续由该 schema 持久化。
- **B2：保存和读取契约。** 新 presentation schema、冻结快照、显式 Save/Save Copy、分区故障隔离、静止指针和两级监听初值。读取后的新编辑状态先进入 B1 registry，手动打开对应编辑器可消费；自动恢复所有 Tabs/子页属于 B3。
- **B3：完整工作区恢复。** 接通剩余白名单的实际捕获/应用、Tabs 顺序/活动项、各 Workspace 子页与布局、隐藏页懒恢复和长会话验证。不能用 B2 已有某个 DTO 字段就宣称界面已能恢复；未接通的字段必须列明，不能写假默认值冒充捕获。

B1→B2→B3 是本专题依赖。C 多实例、TRACK 侧栏不作为 B1 前置，也不在 B1 中顺带实现。

| ID | 对应决定／前置 | 交付边界 | 必须提供的完成证据 | 批次 |
|---|---|---|---|---|
| T-STATE-01 | D-STATE01、D-STATE03；R28身份合同 | 按 §4/5 冻结字段、纯数据接口、通知及预算；正式实施前同步对应规格/ADR | 状态归属和默认值测试、资源预算实测与来源记录 | B1，展开为 01a～01c |
| T-STATE-02 | T-STATE-01；A3工具合同 | Track profile 同步、Segment/SubVoice 局部状态、现有控件接入和关闭再开 | 三宿主真实 WPF、延迟 target 发现、焦点与首次布局回归 | B1，展开为 02a～02c |
| T-STATE-03 | T-STATE-02；D-STATE03.c/d | Duplicate／转轨／删除／Undo、dormant 来源 ID、项目替换及完整回归 | 删除后无额外 source lease/VM 引用；一次人工验收包 | B1，展开为 03a～03d |
| T-STATE-04 | T-STATE-01；D-STATE02 | 独立版本化 presentation codec、section 校验与 B2 文件/读取预算；子项 04a～04e 见 [06 §4.1](06-Workspace-State-Persistence-B2.md#41-t-state-04codec-schema-与预算) | 旧音乐格式读取、新旧 presentation golden、损坏/未知字段/超限隔离；预算实测，不凭经验猜上限 | B2a Codec/预算 |
| T-STATE-05 | T-STATE-04；D-STATE02.a/c | Save/Save Copy 冻结轻量描述、并发 revision、staging/atomic publish 与迁移保护；子项 05a～05f 见 [06 §4.2](06-Workspace-State-Persistence-B2.md#42-t-state-05save-save-copy-与原子发布) | 保存期间新视图变化、取消、写入失败、旧格式升级副本和 source identity 竞争；无部分覆盖 | B2b Save/Copy |
| T-STATE-06 | T-STATE-05；D-STATE01.b/c | section 隔离读取、B1 registry 导入、静止指针边界、Track/group Mute/Solo 初值；子项 06a～06e 见 [06 §4.3](06-Workspace-State-Persistence-B2.md#43-t-state-06读取隔离恢复与监听初值) | 首次播放过滤正确、两级 Mute/Solo 独立；canonical 与 SMF/WAV 不受 presentation 污染 | B2c Read/Monitor |
| T-STATE-07 | T-STATE-02～06 | Tabs顺序、活动子页／owner、局部viewport、面板状态懒恢复；显式Arrangement打开保留定位编辑指针优先级 | 隐藏Tab不提前扫描／编译；活动Compiled正常后台准备；关闭/切换不串状态 | B3；细分 07a～07g 见 [06b §3](06b-Workspace-State-Restore-B3.md#3-b3-子任务分解) |
| T-STATE-08 | T-STATE-07 | 长会话、批量打开关闭及音乐编辑后的状态回归 | 订阅数、managed/native/WPF及任务持有释放证据；同音乐Full/Incremental与成品等价 | B3；细分 08a～08c 见 [06b §3](06b-Workspace-State-Restore-B3.md#3-b3-子任务分解) |
| T-TRACK-01 | R30、D-TRACK01.a～d | 两类Segment左侧Tracks默认隐藏，复用Arrangement头及命令，不创建第二套轨道顺序 | 当前owner与操作选中态可区分；各命令路由及音符选择不被误清 | TRACK |
| T-TRACK-02 | T-TRACK-01 | 双击含编辑指针的Segment，否则最近者等距取前；无Segment不创建；接入轻量状态 | 转轨／删除当前owner／共享组／MuteSolo／关闭面板的自动回归；B已交付时补profile扩展 | TRACK |

## 3. 实施前源码复核：不是从旧调查行号直接实施

以下是对上述基线的只读确认，不是运行测试或新功能成果。路径相对仓库；符号用于后续定位，避免依赖会随实施漂移的行号。

| 当前入口 | 已确认现状 | B1 应处理的风险 |
|---|---|---|
| `Midora.Desktop/DesktopSessionController.cs`：`PianoRollEditorSettings`、`OpenSegment` | 两种 Segment 经同一控制器级 settings 创建；`CloseWorkspace` 直接 Dispose VM | 不能继续跨全部 Track 共享；普通关闭前须捕获纯数据，不能靠保留 VM 记忆 |
| `Midora.Desktop/PresentationModels.cs`：`TimelineEditorSettings` | 包含 Snap/Grid/默认音长力度，也包含 `TimeSignatureMap`、TPQN、参考位置的拍号上下文 | 只能共享用户偏好；不能将该整个可变对象直接变成 Track 公共状态，造成不同 Segment 互相改写量化上下文 |
| 同文件：`TimelineWorkspaceViewModel`、`WorkspaceViewModel.ValueTraceShape` | TickSpan/StartTick/FirstLane/LaneHeight、ToolMode、下栏布局在 VM；绘线形态在 Workspace 基类 | 将共用字段与局部字段分开；首次 Rebuild 的 viewport 初始化不能覆盖刚恢复的状态 |
| 同文件：`InstrumentWorkspaceViewModel._subVoiceEditorSettings` | 已按 SubVoice 保存 Piano/Event settings，但 ToolMode、viewport 等仍有 Instrument VM 字段 | 扩展到已批准的每 SubVoice 独立状态，不因切 SubVoice 串工具/位置；不与 Logical Track 绑定同步 |
| `Midora.Desktop/LaneTabHeader.xaml.cs`、`LaneTabSession.cs` | 静态弱表以 VM 为 key，内部按 owner 保存 Lane 状态；`Sync` 会过滤当前不存在的 key；轴通过 Surface Capture/Restore | 控件卸载/VM 重建后不能丢记忆；异步目录尚未完成不等于 target 被删除，不能提前滤掉待恢复 target |
| `Midora.Desktop/TimelineObjectListState.cs` | 混合 IsVisible/Width/FirstRow 与 source factory/source、激活状态 | 仅捕获布局标量；factory、source、分页任务不能随记忆保留 |
| `Midora.Application/ProjectPresentationSessionV3.cs`、`ProjectPersistenceCoordinator.cs` | 已有独立 revision/save snapshot、复制映射及保存时 dormant 过滤；当前 state 只含 Onion/All-Tracks | 复用保存语义；B1 不冒充已扩容的文件格式，也不能在每个 Pan 时复制整个 presentation |
| `Midora.Desktop/DesktopSessionController.Onion.cs`：`OnPresentationChanged` | 总 presentation revision 用于刷新 Onion，变化可触发 All Tracks Rebuild | 普通编辑器状态必须独立失效/通知；不能每次滚动都令所有 Onion/All Tracks 投影重建 |
| `Midora.Application/ProjectDocumentSession.cs`：`ContentChanged`、`PresentationObjectsCloned` | 已有音乐发布/change set 和复制 ID map 入口 | 用正式 owner/ID 变化关联生命周期，不能按名称或轨道索引猜迁移；重复通知和 Undo/Redo 不重写仍存活对象的新视图状态 |
| `Midora.Desktop/AllTracksWorkspaceViewModel.cs` | 已有首布局适配标志、独立 viewport、异步编译展开生命周期 | B1 不重做 All Tracks；B3 恢复已有 viewport 时必须优先于首次 Fit，仍只构建活动视图 |

源码目录：Desktop 位于 `src/midora-desktop/`，Application/Persistence 位于 `src/midora-core/`。现有 `V3` 类名不等于当前 Project writer 版本：当前音乐 writer 是 Format 4、presentation writer 是 schema 3（reader 保留 schema 1/2）。

### 3.1 正式规格衔接清单

下表为原规划的规格同步分工；B1 行已按已确认原答同步，其余仍后置，不能让代码与旧条款保留相反规则：

| 条款/ADR | 当前与新需求的差别 | 同步阶段 |
|---|---|---|
| SRS §20.1.4、§17.2.3 | 旧文描述全会话共享 Segment/SubVoice settings；新决定为 Track profile 与各 SubVoice 独立 | B1；同时消除旧 Grid 默认描述与当前 Bar-only 界面的文码差异，不恢复 Grid 值选择 |
| §20.1.6、§18.2、§18.4、INV-098/120 | 新 Workspace 默认、主工具与绘线形态、Lane 状态现在需与会话轻量记忆衔接 | B1；继承 A3 主 ToolMode，不生成两套主工具 |
| §18.11.3、INV-116；[presentation ADR](../../Midora-Project-Presentation-and-Format-3-Architecture-Decisions.md) | 需要明确“删除目标自身状态”与“存活目标保留 dormant 来源 ID”的边界 | B1，保存端延续 B2 |
| §3.11、§16.7.5/16.28/16.33、§17.2、§20.15、第21章；INV-011/060/091/112 | 当前文件白名单扩展为 schema 3 批准 workspace section；监听仍只影响消费过滤 | B2 已实施；不进入音乐 source 或 canonical |
| §17 Workspace 导航、§18 各工作区、§20.1.6 | 自动恢复 Tabs/子页、懒应用与失效 owner 处理 | B3；不自动恢复 Selection、Time Range、历史、任务或播放 |

## 4. 字段归属、默认与保存边界

依据 [04 §7 附表 A/B](../04-Decisions-and-Preparation.md#附表-a--d-state01a-的完整保存白名单q1-原表已确认) 和 Q2 D-STATE03.d/e。下表是工程展开，不替换或改写用户原答。B1 栏目都是**同一 Project 会话内** 的轻量状态；“后续保存”不表示 B1 已跨重启恢复。

### 4.1 B1 实施字段矩阵

| 字段组 | 正式状态 owner | 无记忆时默认/解释 | 更新与恢复规则 | 持久化阶段 |
|---|---|---|---|---|
| Piano 横向缩放 | Track ID | 沿用宿主现有首次缩放规则 | 共享缩放量，不能共享鼠标锚点/StartTick；禁止保存 ActualWidth/当前像素缓存作为缩放 | B2 |
| Piano 纵向缩放 | Track ID | 沿用现有首次高度、≥3 device pixels/key 的整数步进 | 同轨同步高度；各 Segment 保留自己的纵向位置，DPI/窗口变化走原约束 | B2 |
| Grid 显隐 | Track ID | true，显示仍为 Bar-only | 不新增具体 Grid 值选择；不与 Application Appearance 的 Lines 全局开关混同 | B2 |
| Piano Snap enabled/subdivision | Track ID，独立 Piano 字段 | true、现行 1/16 初值及分数解析 | 只共享 subdivision 描述；实际步长/拍号/绝对时间计算仍由各 owner 上下文求值 | B2 |
| Event Snap enabled/subdivision | Track ID，独立 Event 字段 | 沿用现行底栏默认 | 同轨底栏同步；不能连带切换 Piano Snap；Vel./Inst./普通 target 共用现行 Event Snap 路由 | B2 |
| 主 ToolMode | Track ID | Select | Piano/底栏共享；同轨同步、不同轨隔离，不因 Shared Usage/Root 而合并 | B2 |
| ValueTraceShape | Track ID | 现行 Free | 与主 ToolMode 独立保存；非 Draw 禁用形态操作，但不抹掉记忆；D 不重置形态 | B2 |
| 默认 Note 长度/Velocity | Track ID | 长度由现行 TPQN 初始化、Velocity 100 | 仅影响后续创建；不能量化或改写已有音乐对象 | B2 |
| Lanes 显隐与最后非零高度 | Track ID | 沿用当前显示及 `TimelineLowerEditorLayout` 默认 | 隐藏时不能把折叠的 0 写成记忆高度；当前帧布局不能反馈覆盖共享值 | B2 |
| List 显隐、通用宽度 | Track ID | 默认隐藏；现行宽度 400、合法范围 240～700 DIP | 只共享布局；不可持有 `TimelineObjectListSource`/factory/行对象 | B2 |
| 横向 StartTick、纵向 FirstLane/合法位置 | Segment ID | 原首次位置 | 使用内容坐标，含 ContentOffset；普通切页/重开保留，显式 Arrangement 打开适用 §5.2 的定位优先级 | B2 |
| 活动 Lane、普通 target 的显隐/顺序 | Segment ID + typed target key | 现行默认；Vel. 第一、适用 Inst. 第二 | 用稳定 Parameter ID/MIDI target，不存显示名/列表 index；不能为同步在别的 Segment 创建 Lane 或 Mapping | B2 |
| 每 target 纵轴（含 Vel.） | Segment ID + typed target key | 现行完整合法显示范围 | 复用 Surface Capture/Restore 的坐标契约，有限值校验；不将显示域范围写回原始 MIDI 值 | B2 |
| List 局部行位置及现有可配置列/筛选/排序 | Segment ID | 首行及现有控件默认 | 行 ordinal 不能跨内容修订直接当对象身份，按新 source 边界校准；没有的控件不为本阶段新增 | B2，未接入的全局布局由 B3 补齐 |
| 上述编辑设置、缩放、位置、Lane/List | Event Instrument ID + SubVoice ID | 各宿主现有默认 | 每 SubVoice 独立；切 SubVoice 前捕获、后应用；关闭 Instrument Tab 可释放 VM 而保留合法轻量描述 | B2 |
| Track/SubVoice Onion 配置 | 现行 target Track/SubVoice ID | schema 3 默认 | 不复制第二套；只衔接 Duplicate、删除自身状态与 dormant 来源引用 | 已保存；B1 不退化旧能力 |

Tracks 面板开关/尺寸已获后续批准，但 R30 尚未实现：只预留字段分区与能力扩展点，B1 不显示空面板、伪造默认选项或提前实现 R30。

默认值必须调用/抽取当前正式初始化路径，而不是在 registry 再复制一套常量。实施自动测试分别覆盖 Segment 的 TPQN 首次 TickSpan、SubVoice 自身首次位置、各宿主默认高度。记录于本表的源码初值不替代实际 Surface 的 DPI/有效范围约束。

### 4.2 B2/B3 接收的其余白名单与明确排除项

| 类别 | 后续字段及归属 | 接口边界 |
|---|---|---|
| Tabs | Project：WorkspaceKey 顺序、活动 key；关闭对象的轻量描述仍可保存 | 不保存 WPF Tab/VM 实例；Arrangement 固定第一且唯一；B3 懒创建 |
| Arrangement/Conductor | 各 Workspace 的缩放、滚动、工具、适用 Snap/Grid、面板布局 | 独立于 Track profile；B1 仅保证不被串改，B3 补全捕获/应用 |
| Event Instrument 外层 | Definition ID：活动子页/当前 SubVoice、左栏宽度/滚动/折叠、已批准的键盘显示设置 | 不能把名称编辑草稿、预览 held keys、音频句柄或音乐 Initial State 当视图保存 |
| All Tracks | 当前模式及 viewport | 模式复用现有 state；新 viewport 恢复只在 B3 覆盖首次 Fit，不保存 compiled 索引 |
| 指针 | 各适用 Workspace 静止 Edit Cursor，项目 Playback Cursor | B2 接入现行寻址/范围；恢复始终 Stopped，不自动发起播放/预览/Seek 任务 |
| 监听初值 | Track 与 Usage/Root 各自 Mute/Solo | B2 恢复到运行期过滤接口；组与成员独立，不改变 canonical/导出 |
| 其他导航 | Diagnostics 筛选、Project Settings 当前页、对象 List 合法列/筛选等 | B2 定义白名单 codec，B3 补齐实际 adapter；不保存诊断副本或模态草稿 |

明确不纳入：Selection/SelectionSnapshot（包括混合选择路由）、Time Range、浮动工具依赖某次选区的坐标/锚点、鼠标/拖动/ContextMenu 状态、Undo/Redo、导航历史、编译结果、缓存位图、页快照/lease、CancellationToken/Task、音频状态。应用级 Follow Playback、Appearance Lines、SoundFonts 等仍按既有 Preferences 管理，不偷放进 Track profile。

## 5. B1 实现设计与接口约束

以下设计已由 B1 的 registry、adapter 与正式 identity companion 实现；具体类型/测试入口见实施记录，类型名不构成公共文件协议。

### 5.1 一份会话 registry，分区 revision 与只含值的描述

建议新增会话拥有的 `WorkspaceStateRegistry`，存储 `TrackEditorProfile`、`SegmentViewState`、`SubVoiceViewState` 等 typed 记录。key 必须包含当前会话身份和正式 owner ID；不同项目即使 ID 相同也不能共享。默认描述可惰性产生，不能在打开大型 MIDI 时为每个未访问 Segment 预建状态。

- registry 的记忆项和冻结快照只拥有值/有限字符串/稳定 ID/有界小集合；不能包含 VM、View、Project、source delegate、页、位图或 lease。运行期 profile 通知只注册活动 adapter；隐藏时解除、激活时重订阅，关闭时显式释放，不能靠订阅保留已关闭 VM。
- 用户偏好和运行时解析分离：`TimelineEditorSettings` 的拍号图、参考时间、derived step 等留在 owner adapter。主区/事件区共享同一 profile 的适用字段，但各自保留 Snap 设置，按 Project 绝对时间和 Segment 内容映射求值。
- Track profile 修改发布一次带字段集合和目标 Track 的通知；活动视图按需应用，隐藏视图只标记 revision，激活时读取。禁止 VM 彼此复制属性产生反馈环；相同值不增 revision，不为纯状态变更执行音乐 `Rebuild`、重新编译、Selection 查询或全轨解码。
- 区分整体保存 revision、每 Track/local revision、Onion 内容配置 revision。普通 Zoom/Pan 不改变 Onion 来源身份或 All Tracks compiled 索引身份；只允许现有视口驱动的局部查询。不得把所有新字段接到现有无差别 `Presentation.Changed` 后每次全表刷新。
- 横向缩放沿用当前 `TickSpan` 表达和合法下限，纵向沿用当前设备像素量化；窗口大小/布局投影不是用户主动修改，不反写共享 profile。发生共享缩放时各 Segment 保持自己的世界坐标起点，只有发起缩放的视图应用自己的鼠标锚点调整。
- B1 不做运行期数据协议，后续 schema 不序列化这些实现类。给 B2 提供冻结纯值快照/分区导入和已保存 revision 接口；B1 不调用新字段的文件写入。

### 5.2 捕获、应用、导航优先级

1. 用当前 Project/owner 身份解析 registry；将 profile、局部 viewport、Lane 描述先应用，再允许首次默认初始化和可见内容准备。内部区分“未初始化”和“已恢复有效值”，不能用 `StartTick==0` 判断无记忆。
2. Lane key/轴恢复须等待对应 owner 的合法 target 目录可用，但不等待全部精确 Count；未知计数不是 0、暂未发现不是删除。不在 UI 线程扫描全 Segment 来验证恢复 key。权威数据确认删除后才过滤，迟到目录需检查会话/owner/revision。
3. 控件的 Loaded/Unloaded/布局回调不能把暂时空默认值回写：先明确 capture/apply guard 和代次。切 SubVoice、关闭 Tab、隐藏 Lanes 时，捕获最后一个有效范围；失捕获/取消手势仍走既有流程。
4. 普通切 Tab、同会话关闭再开、B3 项目恢复采用已记忆位置；只有从 Arrangement 显式打开且编辑指针落在 Segment 半开范围内，才在应用恢复值后居中该编辑指针。Locate 等显式导航保持现有优先级；不因后台刷新调用 `OpenSegment` 而偷做居中。
5. 恢复不创建/选择音乐对象，不再现旧 Selection；完成激活后恢复到正确 Timeline/事件画布焦点，不能落到 Lane 下拉框，不能抢走当前模态窗口或文本输入焦点。

### 5.3 生命周期与音乐编辑衔接

| 动作 | 必须实现的后态 |
|---|---|
| 普通关闭 Tab | owner 仍存在则先捕获轻量状态，再解除 adapter/控件订阅和在途请求、Dispose VM；再次打开创建新 VM，不保留旧 Selection/页/索引 |
| Duplicate Track（独立/Share 两种） | 使用正式 ID map 复制 Track profile 的值，副本之后独立；共享执行组不等于视图共享。已有 Onion remap 保持；重复通知不覆盖已存在目标的新状态 |
| Segment 转轨 | owner 若保留 ID 则保留合法局部位置并重新绑定目标 Track profile；跨类型转换若生成新 ID，消费正式转换映射，不按名称/位置猜源；Undo 再按当时正式归属绑定 |
| 新建/粘贴形成新 Segment | 没有已批准继承数据时走目标 Track profile 和宿主局部默认，不把源 Selection 或资源图复制到新 owner；不在本轮改变剪贴板语义 |
| 删除 Track/Segment/SubVoice/Definition | 音乐提交后立即取消对应请求、移除自身 profile/局部状态、关闭关联 Tab 并释放订阅；要覆盖隐藏与已关闭对象，不只遍历可见 Workspaces |
| 删除后 Undo/Redo | 恢复音乐不自动重开被删 Tab、不从 Undo 中复活已释放 profile；以后显式打开使用合法默认/存活 Track profile。存活目标的 dormant Onion 来源 ID 可随同身份恢复而再次生效 |
| 删除 Onion 来源 | 来源自身状态释放；存活目标只留预算内 ID，不留对象/页/任务/lease；保存过滤副本不能反向清掉会话内 dormant ID |
| Project 替换/关闭 | 仅会话替换成功后整批释放旧 registry；打开取消/失败保留旧会话状态。迟到回调既不能复活旧项，也不能写入新项目同值 ID |

这些规则只控制视图附属状态，不得绕过或扩大现有音乐 Undo。结构监听优先消费已有 change set/clone map；需补通知时只传小型身份/归属信息，不携带整轨数据。音乐 Undo 本身合法持有的不可变页不能算作 registry 泄漏，测试要区分原有持有者和本轮新增持有者。

## 6. B1 可执行子任务与顺序

以下是原 T-STATE-01～03 的子项，不增加 R-ID、阶段数或产品要求；每项完成都要保留对应证据。

| 子任务 | 具体实施/主要落点 | 完成门 |
|---|---|---|
| T-STATE-01a | 审核 §4 每字段及默认/数值域，记录相关 SRS/ADR 修订；固定基线测试和三宿主字段映射 | 无 raw/显示轴混淆，无新增白名单；所有建议名与正式字段区分 |
| T-STATE-01b | 增加纯值 registry、分区 revision、捕获/应用 adapter 接口；设计 B2 freeze/restore 接口 | 数据图无 WPF/Domain/source/Task 持有；相同值不通知，独立项目 ID 不串 |
| T-STATE-01c | 建立资源/通知计数探针，测默认/密集 owner 描述成本，冻结 B1 admission 与资源预算，给 B2 保存预算提供实测输入 | 数值有测量依据，过载/关闭释放有测试；未冻结不能称 B1 完成 |
| T-STATE-02a | 拆控制器级 Piano 设置，将两类 Segment 的通用项接 Track profile；本地 math context 不共享 | 同轨同步/异轨隔离；跨拍号/ContentOffset 下 Snap、创建与 Resize 结果不变；不引发全 source 扫描 |
| T-STATE-02b | 将 Segment、SubVoice 的局部 viewport/Lane/List 描述接 registry；迁移 `LaneTabHeader` 弱表的持久职责 | 控件重建/子页切换不丢合法状态；异步 Lane 目录不会误删待恢复项 |
| T-STATE-02c | 在 `OpenSegment`/`OpenInstrument`、SubVoice 切换、`CloseWorkspace` 接入 capture/apply；保留导航/首次布局和焦点优先级 | 真实 WPF 模板、宽度/DPI/隐藏面板、模态返回及 A/D/S/E/Ctrl+Z/Y 回归 |
| T-STATE-03a | 接 clone map、转轨/转换 change set、owner 删除，协调已有 Onion 复制/过滤 | 正式归属是唯一来源；无 profile 交叉引用；撤销不重开 Tab |
| T-STATE-03b | 会话成功替换/失败/关闭的取消释放；范围化 state/Onion 通知，接重复回调与旧代次拒绝 | 晚回调零发布、引用/订阅/任务释放；已验收的缓存与首帧行为不倒退 |
| T-STATE-03c | 完成 §7 全矩阵、前后同机性能对照、保存/编译无污染回归 | 失败/未测如实记录，性能门不靠提高原阈值通过 |
| T-STATE-03d | 生成 §8 的小型验收工程及说明，形成报告并同步原 T-ID 状态 | 一次人工 UAT-B1-01～06；不把 B2/B3 标为已交付 |

顺序：01a→01b/01c→02a→02b/02c→03a/03b→03c→03d。工程任务可在本轮内部交错验证，不要求用户每完成一项重新验收。

## 7. 自动验证、资源预算与性能门

### 7.1 正确性与生命周期矩阵

| 组 | 必测情况/断言 |
|---|---|
| AUTO-B1-01 归属 | Logical、MIDI 各两同轨 Segment＋另一 Track；Shared/独立 Usage、Fixed/Auto Root 均不合并 profile；两 Definition、各两 SubVoice；Arrangement/Conductor 不被串改 |
| AUTO-B1-02 时间/缩放 | 不同 ProjectStart/ContentOffset/拍号位置、TPQN 最小/常用/最大、接近 long.MaxValue；zoom 保留世界位置、首次恢复不被 Rebuild 覆盖；创建/边界 delta Snap 与基线相同 |
| AUTO-B1-03 Lane/焦点 | Vel./Inst./普通/opaque、正负显示域、隐藏重排、target 冷发现/计数未就绪/取消、target 真删除；控件卸载重建、切 Tab/SubVoice、A 对当前区域 Snap、D 不重置绘线形态 |
| AUTO-B1-04 编辑生命周期 | Duplicate 两种、跨轨 Move/Copy、Segment 类型转换/拆分/删除、Definition/SubVoice 删除、批量 Undo/Redo；正式选择后态保持，零串 owner、零自动重开 |
| AUTO-B1-05 资源释放 | 关闭 Tab 与删 owner 区别、Project 切换成功/失败、旧回调到达、重复 Dispose/通知、并发后台目录取消；弱引用/订阅计数及资源 lease 对照 |
| AUTO-B1-06 不变性 | 只改新状态：音乐 Modified/Undo 深度/编译 revision 与 canonical 不变；保存音乐字节及旧 presentation 内容契约不变；普通播放/预览所有权不变 |
| AUTO-B1-07 未来保存接口 | freeze 后再改状态不污染旧快照；分区导入不发布半态；Snapshot 不持有 VM/source；B1 现有 schema 1/2 与 Format 1～4 reader 测试不得被新类型破坏 |

规划指定的复用入口：`TimelineEditorSettingsTests`、`LaneTabSessionTests`、`LaneTabInteractionRegressionTests`、`LaneTabWpfIntegrationTests`、`WorkspaceLifecycleTests`、`HostedWorkspaceLifecycleTests`、`OnionWorkspaceTests`、`TimelineObjectListIntegrationTests`、`A4bWorkspaceTests`；Application 的 `OnionPresentationLifecycleTests`/`ProjectPersistenceCoordinatorTests`，Persistence 的 `ProjectPresentationSchema2Tests`。B1 实施已运行所属工程的全套，并补充 registry/归属/生命周期专门测试；实际结果以实施报告为准，不以这份入口名单代替运行证据。

### 7.2 基准与预算的冻结方式

规划阶段未运行实验；B1 实施已补同机 `caf8cec` / 当前版本对照和 10,000 个已访问 owner 压力测试，实际数值、边界与未测项见实施记录。注册表采用 64 MiB 保守计费、131,072 项总条目、单 owner 65,536 个 Lane 描述/显式 target 上限；B2 复用 64 MiB presentation JSON 编码上限，预算不等同于进程总内存。B3 恢复并发预算仍未冻结。

| 预算/指标 | 实验输入与记录 | 通过条件 |
|---|---|---|
| 状态条目/字节 | 合成 1/10/100/1,000 Track，累计访问至 10,000 Segment 描述；Lane 从空/2/16 到大量合法 target；记录每项及总 retained bytes | 与已访问 owner/实际保存字段成比例，不与音符数量成比例；闭包/dormant/字符串也有上限，不能只数 profile 个数 |
| 热更新 | 同轨多个已开/隐藏 Tab 连续 Zoom/Pan/换工具；测 handler 次数、UI p50/p95/p99、分配及 source 查询/解码计数 | 不为同步枚举音符；隐藏 view 无渲染任务；通知只落在受影响 owner；避免整 registry 拷贝 |
| 冷重开/局部失效 | 真实 WPF 从未渲染区、Segment 范围内外、关闭重开与 Lane 冷目录；与 `caf8cec` 同场景对照 | 保留已验收的无闪烁/无陈旧高亮/有界栅格路径，不能为记忆引入全源重建；时间门限按重复基线和波动冻结 |
| 生命周期 | 100 轮开关页、切 SubVoice、Duplicate/delete/Undo；关闭后等待既有后台清理，并在测试中区分 GC heap/Working Set | registry 不额外保留 VM/页面/位图/lease，订阅/队列随关闭回到合理基线；不把进程 Working Set 高水位直接当泄漏 |
| 保存/恢复接口 | 纯值 freeze/clone 的分配峰值、分区数量和最坏编码体积估算 | 不因每次状态变化分配全量快照；B1 冻结 resident/条目/通知预算，B2 才冻结文件字节/读取预算，B3 才冻结真实恢复并发预算 |

真实大样本复用既有授权 `9KX2 18 Million Notes.mid`、MIDI Out #23 的密集段；实际运行前检查存在性和可用内存。准备/测量不可破坏用户项目，不自动保存覆盖它。进程树接近 9 GiB 或提前出现失控增长时停止探针；这是安全警戒，不是合格预算。合成矩阵先跑，真实样本只补索引/缓存/恢复的非线性风险。

资源超限不得静默淘汰用户要求记住的状态或影响音乐数据。B2 保存按 D-STATE02.c 给“保存音乐及可用视图部分/取消”；B1 若预算实验暴露尚未约定的可见行为取舍，必须在原问答文档写具体问题后确认，不能擅自用“关闭/打开被拒绝”或“悄悄忘记”解决。工程预算数值由实验负责，不要求用户猜。

建议正式验证入口：两个 Desktop 测试工程的 Release 全套、Application 的上述定向测试、Persistence presentation/迁移回归及 Desktop Release build；实际命令、耗时、通过/失败/跳过和专用探针入口在实施报告列全。性能测试与构建分开运行，失败要复查而不是从汇总删除；沿用 A4b 两处全套性能断言波动记录作为基线风险，不假定本轮已消失。

## 8. 人工验收：B1 仍只一轮，6 项

已生成 `B1-Workspace-State.midora` 小型样例及说明，位置为仓库忽略的 `.tmp/uat/b1/`，不提交二进制产物。包含两个同轨 Logical Segment 与共享成员轨，Fixed/Auto 各两条 MIDI 轨，两个 SubVoice，不同起点/ContentOffset 和多个 target。工程故障、资源峰值与组合由自动测试承担；用户不手工构造坏包或百万对象。

保留原 UAT-B1-01/02 的范围，补充 03～06。全部当前为 **实现已交付并通过用户验收** ；自动测试通过不代替人工验收。提交 `a6ac12a` 已推送。

| 编号 | 操作 | 可观察预期 |
|---|---|---|
| UAT-B1-01 | 按样例依次打开同轨的两个 Segment、另一 Track；修改缩放、主工具、绘线形态、Piano/Event 各自 Snap、Lanes/List 显隐与尺寸。Logical/MIDI 各检查一组 | 同轨通用项同步；另一轨即使同组也不变；Piano/Event Snap 不串；每个 Segment 原先滚动位置不被另一页鼠标位置替换 |
| UAT-B1-02 | 在编辑指针位于 Segment 外时关闭再开；Duplicate Track 后改变副本设置；删除一个已选 Onion 来源再 Undo | 重开恢复轻量记忆但不恢复旧选择；副本以后独立；删除源不保留其 Tab，Undo 恢复来源显示却不重开来源自己的 Tab |
| UAT-B1-03 | 两个同轨 Segment 设置不同活动 Lane、隐藏/顺序和纵轴；隐藏 Lanes、换 Tab、关闭再开；新建/删除一条 Lane | Lane 局部状态各自保持；另一个 Segment 不多出正式 Lane；冷计数结束不跳回 Vel.；重新显示时恢复最后有效高度/范围 |
| UAT-B1-04 | 两个 SubVoice 分别设置缩放、位置、工具、绘线形态与 Lane，再切换/关闭重开 Instrument；改变绑定的 Logical Track 设置 | 每个 SubVoice 保持自己的状态；不跟 Logical Track 或另一 SubVoice 串改；外层现有功能无回退 |
| UAT-B1-05 | 将已调好局部位置的 Segment 移到另一条具不同设置的 Track，Undo/Redo；删除该 Segment 后 Undo 并显式重开 | 转轨用目标通用设置、合法局部位置保留；Undo 随正式归属重新绑定；删除后不会自动重开或复活已释放的自身状态 |
| UAT-B1-06 | 把编辑指针放在 Segment 内，从 Arrangement 显式打开，再普通切 Tab；在 Piano/Event/Inst. 间使用 A/D/S/E 与一次真实编辑的 Ctrl+Z/Y；只调整视图后观察标题 | 显式打开指针居中、普通切页保持位置；快捷键作用于正确区域；纯状态不产生音乐星号或占用音乐 Undo。B1 不要求重启后恢复新字段 |

返修只重测失败项及有依据的邻接项，不让用户重跑整个 A 系列。大型样本流畅度可以追加一次体验抽查，但不能把自动资源/故障门转嫁给用户。

## 9. B2/B3 的接口交接与后续验收骨架

| 接口 | B1 交付 | 后续责任与不可越界 |
|---|---|---|
| Freeze | 当前会话/owner 下的不可变纯值、分区 revision，音乐/presentation 身份分开 | B2 在正式 Save 起点冻结音乐与视图；保存中新增变化不能被标为已保存；Save Copy 不重置原基线 |
| Restore | typed 分区验证/应用接口，默认工厂、owner 有效性与通知抑制 | B2 已实现 schema 3/预算和 section recovery；B3 控制初始化顺序/激活焦点 |
| Owner lifecycle | 复制映射、转轨、删除目标与 dormant 来源分离 | B2 保存过滤悬空副本；B3 丢弃已失效 Tab 描述，不恢复 Selection/任务，不重新创造 Domain 对象 |
| Activation | 新 VM 绑定当前 profile/局部描述；隐藏 adapter 不进行重计算 | B3 恢复轻量 Tabs，按需创建；只当前活动 Compiled 视图正常后台准备，不为隐藏 Tabs 全量编译/渲染 |
| Monitoring/cursors | 预留只含 stable ID/标量的独立分区，不连接音频新路径 | B2 在首次播放计划前恢复监听初值和合法静止指针，永远 Stopped；不恢复设备/voice/native state |
| 多实例保存 | 不新增长期持有文件/全局静态跨会话状态 | C 后续接入文件身份/冲突协调；B 继续遵守已存在旧格式确认备份和原子保存，不先删除 Mutex 或修改共享目录 |

新 presentation schema 版本、实际 JSON 分区布局、字节预算已在 B2 实施中冻结为 schema 3、64 MiB presentation JSON 上限及有界 section 数量；保留旧 schema 1/2 reader/golden。仅扩展独立视图数据不提升音乐 Project Format 4；不能原地给旧 strict schema 偷加字段。

后续人工 ID 保持，进入对应阶段再细化操作，不在 B1 要求验收：

- UAT-B2-01：只改已接入视图/两级 Mute/Solo，不加音乐星，显式 Save 后手动打开可恢复、始终 Stopped；未保存的纯视图变化关闭不提示。
- UAT-B2-02：提供损坏 presentation 的测试副本，合法分区和音乐仍可用，坏区有提示并回退；加上 Save Copy/保存失败/迁移副本的代表性场景。
- UAT-B3-01～06：多个 Tabs/顺序/活动子页/侧栏/位置自动恢复，后台页懒加载；切页焦点正确；失效 owner、损坏 section、兼容、长会话和性能由自动证据与人工批次共同覆盖，具体步骤见 [06b §7](06b-Workspace-State-Restore-B3.md#7-人工验收批次)。
- UAT-TRACK-01/02：R30 Tracks 侧栏仍独立，不为了状态专题提前实施；沿用原单击/双击导航与轨头命令范围。

## 10. 进入门与前序文档验证（历史记录）

进入 B1 需用户另行明确授权实施。工程进入门为：复核基线、承接已批准语义修订对应 SRS/ADR、建立原基线测量与 §4 字段测试；按 §6 顺序完成而非直接散改 XAML。B1 验收通过后已冻结并实施 B2 schema 3；B3 的真实冷恢复不能由热缓存结果替代。

2026-09-18 本轮仅只读复核源码和文档，细化本计划及索引。没有运行产品构建、自动测试、WPF/音频或性能实验，没有生成 UAT 工程；未改代码、SRS、ADR、版本、用户原答，未提交、推送或发布。文档检查单独记录，不冒充产品验证。

文档检查结果：10个 B1 子任务编号、6个人工检查编号均唯一；本轮5份修改文档的64处本地链接文件目标存在，新加附表 A 锚点匹配；新增粗体后空格及按仓库换行配置的 `git diff --check` 通过。差异仅为规划 Markdown，Q1/Q2 原问答、源码、AGENTS 与正式 SRS 均未修改。

## 11. B1 实施交接

T-STATE-01a～01c、02a～02c、03a～03d 已实施并通过用户整体验收，提交 `a6ac12a` 已推送。跨类型 Move 的新旧 Segment ID 由 Application 正式转换结果提供；Copy 不继承局部状态；未创建事件的显式 MIDI Lane 也可在会话内关闭重开。真实 WPF 模板恢复、预算/释放、源码包不变性与大样本对照的证据集中在 [B1 记录](../../Midora-B1-Workspace-State-Implementation-2026-09-18.md)。B2 schema 3、分区 Save/Restore、坏 section recovery 和省略诊断已实施，自动证据见 [06-B2 执行文档](06-Workspace-State-Persistence-B2.md)，B3 仍未开始。

## 12. B2 细化入口

B2 不再沿用“临近再细化”的旧状态。其四个工程切片、T-STATE-04a～06e、B2-CODEC/PERSIST/RESTORE/MONITOR/RESOURCE/COMPAT 验证门、UAT-B2-01～06 及六项实施前决策，统一维护在 [06-Workspace-State-Persistence-B2.md](06-Workspace-State-Persistence-B2.md)。B3 的 T-STATE-07a～08c、B3 自动门、UAT-B3-01～06 和进入实现前决策统一维护在 [06b-Workspace-State-Restore-B3.md](06b-Workspace-State-Restore-B3.md)。本文件继续作为专题总边界和 B1 的字段/生命周期依据；若 B3 需要改变既有外部语义，必须先回到 04 决策问答和 SRS/ADR 更新。

## 13. B3 细化入口

B3 本轮仅完成任务细化，未改代码、SRS、ADR、版本或发布产物。具体的 T-STATE-07a～08c、Workspace 恢复时序、懒激活与焦点优先级、B3-SCHEMA～RESOURCE 自动门、UAT-B3-01～06 及 B3-D01～D07 进入实现前决策，统一维护在 [06b-Workspace-State-Restore-B3.md](06b-Workspace-State-Restore-B3.md)。B3 实施前仍须冻结 schema/section 版本策略和恢复资源预算；不得把本计划或未运行的矩阵当成产品已支持的恢复行为。
