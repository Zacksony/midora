# B3：完整工作区导航恢复与懒激活

初稿：2026-09-20；实施记录：2026-09-20；回退后复核：2026-09-21。状态：**已重新实施，待人工 UAT；未发布** 。

本文件是 B3 的独立实施计划，承接 [B1/B2 总边界](05-Workspace-State-and-Track-Navigation.md)、[B2 保存读取契约](06-Workspace-State-Persistence-B2.md) 和 [工作区状态需求调查](../03-Multi-Instance-and-Workspace-State.md)。它不替代用户在 [04 决策问答](../04-Decisions-and-Preparation.md) 中已经确认的原答，也不把本文件中的“推荐”自动提升为产品决定。

## 1. 目标、输入、输出与非目标

### 1.1 目标

B3 使用户在同一 `.midora` 项目中显式保存的工作区导航状态可以在重新打开后安全恢复：

1. 恢复已打开 Workspace 的集合、全局顺序和活动 Workspace；
2. 恢复对象 Workspace 的当前子页、面板状态和已批准的轻量局部位置；
3. 只创建当前需要的视图，其他保存的 Workspace 保留为轻量描述，在用户激活时再创建；
4. 恢复过程不自动播放、试听、Seek，不恢复选择、Undo/Redo、任务、菜单、拖动或任何运行时资源；
5. 项目音乐内容、Canonical Compiled Result、音频缓存、MIDI/WAV 输出和音乐 Modified 状态不因视图恢复改变。

### 1.2 正式输入与输出

| 类别 | 输入 | B3 输出 | 明确不输出 |
|---|---|---|---|
| Workspace 身份 | `WorkspaceKind`、稳定 owner ID、对象间的必要稳定关联 | 有序的轻量 Workspace 描述集合 | WPF `TabItem`、ViewModel、控件引用、名称猜测 |
| 导航 | 活动 Workspace、Tab 顺序、是否可关闭/固定的正式类型 | 可验证的活动项和顺序 | 隐式创建 Domain 对象、按名称重绑定 |
| 编辑器页面 | Event Instrument 当前子页/SubVoice、Project Settings 页面、Diagnostics 过滤器等批准字段 | 激活对应页面时的初始纯值 | 草稿、焦点控件、文本选择、展开的菜单 |
| 视图 | Arrangement、Conductor、All Tracks、Segment 等已批准 viewport/tool/panel 状态 | 恢复后的停止态视口；All Tracks 先适配后应用保存视口 | 位图、分页页、编译索引、后台任务、缓存命中状态 |
| 指针与监听 | 静态编辑指针、合法 Mute/Solo 初值 | 停止态、经边界校准的指针和运行时监听过滤 | Playing/Buffering、试听、设备、native handle、voice state |

`WorkspaceKind` 当前正式集合为 `Arrangement`、`EventInstrumentLibrary`、`ProjectSettings`、`Diagnostics`、`ConductorTrack`、`SegmentEditor`、`EventInstrumentEditor` 和 `AllTracks`。对象型 Workspace 只能以稳定 ID 标识；用户界面不得显示这些 ID。

### 1.3 非目标

- 不实现多实例、跨项目 Clipboard、R30 Tracks 侧栏或新的编辑功能；这些专题不阻塞 B3。
- 不持久化或恢复 Selection、Time Range、浮动工具锚点、Undo/Redo、任务进度、上下文菜单、输入法状态、播放/试听、编译结果、音频缓存或图形缓存。
- 不在打开项目时为隐藏 Workspace 全量解码大型 MIDI、建立钢琴卷帘索引、编译逻辑轨道或创建音频 Worker。
- 不通过修改音乐源、Canonical fingerprint、Project Modified 或 Undo 栈来“修复”视图状态。
- 不把 B2 当前存在的 profile/lane/onion/monitoring DTO 字段误称为 Tab 已恢复；B3 必须有实际消费者和自动证据。

## 2. 当前基线与依赖

| 基线 | 已确认事实 | B3 的边界 |
|---|---|---|
| B1 | 会话内的 Track profile、Segment/SubVoice 局部状态和 owner 生命周期已交付 | 复用 typed registry；不保留 VM 来实现“恢复” |
| B2 | presentation schema 3、冻结 Save/Save Copy、section recovery、64 MiB JSON 总预算已交付；UAT-B2-01～06 已通过 | 以独立 section 扩展，不改变 Project Format 4 |
| 桌面 Workspace | Arrangement 固定第一；对象 Workspace 通过稳定身份区分；All Tracks 有首次 Fit 与独立 viewport | 恢复顺序不能让通用 Rebuild 覆盖已恢复值 |
| 音乐生命周期 | Project source 发布、Full/Incremental compile、音频与导出均已有独立链路 | 视图恢复不能触发新的音乐语义变更或隐藏页全量编译 |

进入实现前必须复核当前源码符号和接口，不使用本文件中的行号作为稳定契约。重点入口包括 `WorkspaceKind`/`WorkspaceKey`、`DesktopSessionController` 的打开/关闭/激活流程、`WorkspaceStateRegistry`、`ProjectPresentationWorkspaceV4`、`ProjectPresentationSessionV3`、`ProjectPersistenceCoordinator`、`AllTracksWorkspaceViewModel` 和各 Workspace 的首次布局逻辑。

## 3. B3 子任务分解

### T-STATE-07a：导航描述与持久化分区

定义独立的 Workspace navigation snapshot：

- `Tabs`：唯一、可排序的轻量 Workspace 描述；对象型描述包含正式 owner ID，类型型描述只含正式 WorkspaceKind；
- `ActiveTab`：当前活动 Workspace 的稳定引用；
- 每个描述的纯值页面/视口引用，引用 B2 已保存 profile/local/lane 状态而不复制大对象；
- 可选的“关闭但保留状态”描述只能保留批准的轻量状态，不能保留 VM、source factory、分页 lease 或缓存。

该子任务必须先冻结 schema 版本、字段编号/JSON 属性、section 名称、重复项和未知字段策略，再写 codec/golden test。不得把新的导航字段偷偷追加到 strict schema 3 而不更新版本策略。

### T-STATE-07b：捕获、规范化与保存接入

在显式 Save/Save Copy 的同一冻结点捕获当前导航：

1. 从 Workspace 主管理器捕获顺序、活动项和各宿主的纯值状态；
2. 通过 owner snapshot 校验对象仍属于当前 Project revision；
3. 去重 WorkspaceKey，过滤已删除 owner，保持 Arrangement 固定第一；
4. 不因每次 Pan/Zoom/Tab hover 写文件；只在正式 Save 的 presentation snapshot 中发布；
5. Save 期间若导航再次改变，只标记新的 presentation revision，不能误清为已保存；Save Copy 使用同一冻结快照且不重置当前会话 baseline。

### T-STATE-07c：读取、故障隔离与失效 owner

- 先读取音乐 source，再独立读取 navigation section；navigation 损坏不能阻止音乐项目打开；
- 每个描述分别进行 kind、owner、关联对象、范围、顺序和活动项校验；坏项丢弃并给出独立 Warning，不创建幽灵 Tab；
- section 级损坏只回退导航 section，不清除 B2 其他 profile、lane、Onion/All Tracks 和 monitoring；
- owner 被删除、复制、跨轨道迁移或 Undo 恢复时使用正式 ID map，不按名称、数组索引或当前 Tab 位置迁移；
- 已失效的活动项回退到 Arrangement（推荐），并保持音乐可用；不得自动打开 Diagnostics 以外的替代对象。

### T-STATE-07d：打开生命周期与懒激活

推荐的打开顺序：

1. 建立 Project source、owner 索引和 B1/B2 typed presentation registry；
2. 验证 navigation 描述并安装轻量 Tab registry；
3. 立即创建 Arrangement 和活动 Workspace 的最小 VM；
4. 只对活动视图应用已保存状态；需要编译/渲染的 Compiled 视图走现有后台准备，并受当前 Project session/revision 取消；
5. 其他 Workspace 仅在用户切换时实例化；实例化前再次确认 owner 和项目 revision；
6. 激活完成后恢复正确焦点，保持 Stopped；迟到的旧任务不得覆盖新项目或新一次激活的状态。

隐藏页不得在打开阶段顺序创建、预热、扫描、编译或持有音频资源。若用户连续激活多个隐藏页，必须使用有界队列和取消/合并策略，不能无限积压 Dispatcher、后台任务或 WPF 资源。

### T-STATE-07e：各 Workspace 的恢复适配器

| Workspace | 必须恢复 | 初始化优先级与特殊规则 |
|---|---|---|
| Arrangement | 固定第一、垂直 viewport、工具/Snap/Grid、面板和合法编辑指针 | 不允许内容外创建 Segment；显式从 Arrangement 打开某 Segment 时，按既有“编辑指针优先居中”规则覆盖普通恢复滚动 |
| ConductorTrack | viewport、Tempo 纵轴、工具/Snap、底部栏高度及静态编辑指针 | 不恢复点选择、拖动和播放状态；Tempo 范围必须先校验再应用 |
| SegmentEditor | owner Segment、Piano/Event/Lanes/List 状态、每 lane 轴、viewport、工具和静态编辑指针 | Logical/MIDI 使用各自正式 owner；不扫描隐藏 Segment 的大型内容；Lane target 仍由 B2 lane registry 管理 |
| EventInstrumentEditor | Definition、当前子页、当前 SubVoice、左栏宽度/滚动/折叠、钢琴/事件视图批准状态 | Definition 不存在时丢弃该 Tab；不恢复弹窗、draft、Properties 编辑中间态和音频预览 |
| EventInstrumentLibrary | 列表滚动、筛选/排序等已批准轻量状态 | 不恢复隐式选中对象、右键菜单或外部拖动；对象删除后回到无选中 |
| AllTracks | Raw/Compiled 模式、viewport、垂直缩放、跟随播放开关等批准视图状态 | 首次有效布局的 Fit 只执行一次；恢复 viewport 在 Fit 之后应用，Compiled 只对活动视图后台准备 |
| ProjectSettings | 当前设置页面 | 只恢复页面，不恢复未提交控件、文本选择或焦点；无 Project 时不创建项目专属状态 |
| Diagnostics | 过滤器、排序/页面等批准导航状态 | 不恢复诊断对象副本；新项目/新会话若无对应诊断则回退默认 |

### T-STATE-07f：焦点、快捷键与用户动作优先级

- Tab 激活结束后焦点必须落在该 Workspace 的正式 Surface/根容器，而不是 Lane 下拉框、名称输入框或隐藏控件；快捷键 A/D/S/E、Ctrl+Z/Y 等应立即作用于新页面。
- 在打开过程收到用户新的 Tab 点击、关闭、Seek、编辑或停止操作时，取消/淘汰旧恢复任务；用户动作优先于后台恢复。
- 显式导航的目标优先级高于保存的普通 viewport：例如从 Arrangement 双击某 Segment，应先打开该 Segment 并按编辑指针规则定位，再应用不冲突的局部状态。
- 任何 modal、菜单、浮动工具、输入法和拖动状态均从默认静止态开始，不得伪造上一次的中间交互。

### T-STATE-07g：生命周期、并发与释放

覆盖以下状态转移：项目关闭、打开另一个项目、迁移旧格式、Save/Save Copy、Undo/Redo 删除或恢复 owner、Duplicate/Move owner、后台 compile/render 迟到、取消、窗口关闭和重复激活。

每个异步激活任务必须带 Project session token、navigation revision、owner generation 和取消标记；任务完成时只允许同一代状态提交。关闭 Workspace 应释放 VM、订阅、WPF bitmap、分页 lease、native/worker 引用和任务，不由“恢复状态”长时间持有。

### T-STATE-08a：长会话与资源门

以真实 WPF 运行 50、100、200 个不同 Workspace 描述，并反复执行打开/切换/关闭/重排/保存/重开。记录：

- managed heap、LOH、Working Set、WPF 原生资源、订阅数量和后台任务数量；
- 首次打开 Project、恢复 Arrangement、激活一个大型 Segment、激活 All Tracks 的时间；
- 关闭/切换后资源是否回落，是否存在旧 owner、旧 project 或旧 bitmap 被引用；
- 9KX2 与小项目的 UI 响应、取消和输入焦点是否稳定。

资源预算必须以实测冻结，不把“正常项目很小”当作上限。若达到既定 presentation/恢复预算，隔离有问题的导航分区并说明，不允许静默截断活动用户状态或让系统因恢复任务假死。

### T-STATE-08b：音乐等价与消费者回归

对同一 Project 做以下对照：未恢复视图、恢复视图、恢复后立即关闭重开、恢复后手动编辑、Full Compile、Incremental Compile、MIDI Export、Audio Render。要求 canonical 内容、事件顺序、音频输出、诊断和 Project Modified 语义不因 presentation 恢复改变；只有用户真正修改音乐时才进入音乐 Undo/Modified。

### T-STATE-08c：兼容、损坏与发布边界

- schema 1/2/3 的旧 presentation 仍可读；没有 B3 section 时使用默认 Arrangement；
- 旧软件打开新文件时仍能加载音乐并按既有规则丢弃未知 presentation；
- unknown field、重复 field、坏 ID、非法范围、超长列表、损坏 ZIP/JSON、截断文件、磁盘满、锁冲突、Save 取消均不破坏原文件；
- migration dirty、Save Copy 和旧格式可见副本保护继续符合现有 B2 契约；
- 不把 B3 导航恢复错误报告成音乐诊断，也不在无 Project 时留下幽灵 Tab。

## 4. 概念数据形状（进入实现前需冻结）

下面是便于讨论和测试的概念形状，不是要求直接照抄的 DTO：

```text
WorkspaceNavigationSection
  schemaVersion
  tabs[]
    workspaceKind
    ownerId?                 // 仅数据身份，不向用户显示
    secondaryOwnerId?        // 例如 SubVoice/Segment 的正式关联
    order
    stateRef                 // 引用 B2 profile/local/lane 或本 section 的小型状态
  activeTabRef?
  perWorkspaceStates[]
    workspaceKey
    viewport/tool/panel/page 的批准纯值
  recoverySummary?           // 仅用于 section recovery 诊断，不是 UI 状态
```

实现时必须保证：

1. 一个 `WorkspaceKey` 在 `tabs` 中最多出现一次；`order` 不得依赖集合遍历顺序；
2. 所有 ID 在当前 Project owner 索引中验证，不能因名称相同而猜测；
3. 字段值只用有限标量、枚举、稳定 ID 和有界数组；不得把 WPF 对象、委托、Task、取消令牌、文件句柄、缓存键或音频句柄序列化；
4. 记录顺序、活动项和每页状态要能确定性编码，重复保存同一冻结状态得到相同字节；
5. 任何省略、回退和失效都要有可读 Warning，但 Warning 不能覆盖首个真正的音乐错误。

## 5. 恢复时序与失败原子性

```text
读取 Project source
  → 读取并独立验证 presentation sections
  → 建立 owner/index 与 B1/B2 registry
  → 安装轻量 Workspace descriptors
  → 创建 Arrangement + 当前活动 Workspace
  → 应用恢复值并校验边界
  → 后台准备当前活动 Compiled 视图
  → 用户激活时懒创建其他 Workspace
  → 旧任务以 session/revision/owner generation 丢弃
```

打开失败时保留当前项目和原工作区；新项目只有在音乐 source 成功提交后才接管导航描述。presentation section 失败不得回滚已成功加载的音乐；若恢复期间窗口关闭或用户打开另一项目，所有未完成激活必须取消或被代际检查丢弃。禁止使用“最后一次回调刚好返回”作为正确性条件。

## 6. 自动验证门

| 门 | 覆盖内容 | 必须证明 |
|---|---|---|
| B3-SCHEMA | DTO/schema、确定性编码、重复/未知字段、预算 | 新旧 reader、golden bytes、超限与损坏隔离 |
| B3-NAV | WorkspaceKey 去重、顺序、活动项、固定 Arrangement | 乱序输入、重复项、失效 owner、类型/对象 Workspace 混合 |
| B3-RESTORE | 每种 Workspace adapter | Capture→Save→Open→Activate 的字段逐项等价，边界校准且不恢复禁止项 |
| B3-LAZY | 活动/隐藏/Compiled | 打开时不创建隐藏 VM、不扫描隐藏大型内容；活动页后台准备可取消 |
| B3-FOCUS | 切页、快捷键、显式打开优先级 | 新页焦点正确，A/D/S/E/Ctrl+Z/Y 可直接响应，用户操作压过迟到恢复 |
| B3-LIFECYCLE | 关闭、重开、删除、复制、Move、Undo、项目替换 | 无旧订阅/任务/bitmap/owner 引用；无幽灵 Tab |
| B3-EQUIV | Full/Incremental、MIDI/WAV、canonical | 视图恢复不改变正式音乐结果、诊断和 Modified/Undo 语义 |
| B3-COMPAT | schema 1/2/3、旧软件、坏包、迁移/Save Copy | 音乐可用、来源保护、presentation 独立回退 |
| B3-RESOURCE | 50/100/200 Tabs、9KX2、长会话 | 时间、Working Set、LOH/WPF/native/任务持有量有基线和释放证据 |

自动验证必须覆盖冷启动/冷打开，而不是只在已有 Tab/缓存的热会话中测试。未运行的测试不得在交付报告中写成通过。

## 7. 人工验收批次

### UAT-B3-01：基本导航往返

打开包含 Arrangement、Conductor、多个 Segment、Event Instrument、All Tracks、Project Settings 和 Diagnostics 的项目，调整 Tab 顺序、活动 Tab、Event Instrument 子页/当前 SubVoice、Arrangement/Conductor/All Tracks 视口，显式 Save，关闭并重开。预期：顺序、活动项、子页和批准视图状态恢复；项目 Stopped；不恢复 Selection、Undo、任务或试听。

### UAT-B3-02：懒恢复与大型项目

使用 9KX2 或同等级大型 MIDI，保存多个隐藏 Segment/Compiled/All Tracks 页面后重开。预期：打开阶段只创建 Arrangement 与活动页，不因隐藏页长时间扫描；点击隐藏页时才加载，取消/切换不会卡死或显示旧 owner 内容。

### UAT-B3-03：焦点与显式导航

从 Arrangement 显式双击不同 Segment/事件乐器，切换普通 Tab、Lane、Instrument 子页，立即使用 A/D/S/E、Ctrl+Z/Y 和播放控制。预期：焦点在正确 Surface；显式编辑指针定位优先；不会落在 Lane/名称输入框；迟到后台任务不能夺回焦点。

### UAT-B3-04：owner 生命周期

删除、Undo、Duplicate、Move Segment/Track/Event Instrument/Root，保存并重开。预期：有效 Tab 只绑定当前正式 owner；失效 Tab 被丢弃并提示，不创建幽灵对象；存活 owner 的 B2 dormant Onion 规则不被改变。

### UAT-B3-05：损坏与兼容

分别准备坏 navigation section、未知字段、重复字段、失效 ID、旧 schema、迁移项目、Save Copy、取消/磁盘写入失败副本。预期：音乐仍可打开，坏视图区独立回退，原文件和迁移保护不被破坏，诊断完整且不与音乐错误混淆。

### UAT-B3-06：长会话与资源

在 50/100/200 个 Workspace 描述间反复切换、关闭、重开和 Save，期间打开一次大型 Segment 和 All Tracks。预期：内存、订阅、任务和 WPF/native 资源不随历史次数无限增长；关闭后旧内容不再可见；UI 可取消并保持可操作。

## 8. 需要用户决定或进入实现前确认的事项

以下不是本文件替用户做出的决定；若不改变已有批准白名单，只需在实施前逐项确认。推荐项是工程建议，不等于已批准。

### B3-D01：导航 section 的版本策略

**问题：** B2 当前 presentation schema 为 3，B3 是否新建 schema 4/独立 `workspaceNavigation` section，并继续读取 schema 1/2/3；还是扩展 schema 3？

**推荐：** 新建可明确识别的 schema 4（或等价的独立 navigation section 版本），不在 strict schema 3 中偷偷增加字段。这样旧读者可安全丢弃未知导航，B3 可以独立 golden/预算；Project Format 4 不变。

**用户决定：** 同意推荐。采用当前 presentation schema 4；继续读取 schema 1/2/3，外层 Project Format 4 不变。

### B3-D02：失效活动 Tab 的回退

**问题：** 保存的活动 owner 已被删除或项目迁移后不存在时，是否统一回退 Arrangement，还是尝试最近的同类 Workspace？

**推荐：** 统一回退 Arrangement，并给一次明确 Warning；不按名称/邻近位置猜测，避免打开错误音乐对象。

**用户决定：** 同意推荐。

### B3-D03：隐藏描述何时实例化

**问题：** 重开项目时是否只实例化 Arrangement 与保存的活动 Tab，所有其他 Tab 等用户点击后再创建？

**推荐：** 选择“活动优先、其余全懒激活”。Tab 头可以由轻量描述显示，但不创建 VM、索引、编译或缓存；用户点击时才实例化并可取消。

**用户决定：** 同意推荐。实现使用仅保存纯值的轻量占位描述；切换时才物化真正 Workspace。

### B3-D04：恢复中用户动作优先级

**问题：** 用户在后台恢复某个 Tab 时切换到另一个 Tab、关闭项目或开始编辑，是否立即取消旧恢复？

**推荐：** 立即取消/代际淘汰旧任务，用户当前动作优先；任何迟到结果只能提交到仍匹配的 session/revision/owner generation。

### B3-D05：导航状态预算与超限处理

**问题：** 对 Workspace 描述数、单描述字节、导航 section 总字节和同时在途激活任务，是否沿用 B2 总预算并由实测冻结，还是新增独立上限？

**推荐：** 先做 50/100/200 Tab 和大型项目实验，再冻结有界数值；超限时保存音乐和可用视图分区并列出省略项或允许取消，不静默删除用户刚设置的活动项。

### B3-D06：Settings/Diagnostics 是否随项目保存

**问题：** 已批准白名单包含 Project Settings 当前页与 Diagnostics 过滤器；是否继续把它们作为当前 Project presentation 恢复，还是仅作为会话级 UI 状态？

**推荐：** 按已批准白名单继续随当前 Project 保存；无 Project 时不产生独立持久化，不恢复跨项目的 Settings/Diagnostics 页面。

**用户决定：** 同意推荐。

### B3-D07：活动 Compiled 视图的准备时机

**问题：** 重开后若活动页是 All Tracks/Compiled 或编译结果依赖页，是否允许在音乐打开完成后后台准备，而不阻塞首次可交互时间？

**推荐：** 允许仅活动页后台准备；隐藏页不预热。若准备失败，保留可编辑源视图并给局部 Warning，可重试，不阻塞项目打开。

**用户决定：** 同意推荐。

## 9. 交付顺序与报告要求

建议分三个可验收切片，但不把每个切片强制拆成用户验收轮：

1. **B3a 导航 codec/捕获/基本恢复：** D01～D03 冻结后完成 schema、Save/Read、Arrangement/活动 Tab、基础失败隔离；先运行 B3-SCHEMA/NAV/RESTORE。
2. **B3b Workspace adapters/懒激活/焦点：** 完成各 Workspace 页面、All Tracks Fit 优先级、后台取消和用户动作优先级；运行 B3-LAZY/FOCUS/LIFECYCLE。
3. **B3c 长会话/兼容/资源收尾：** 完成旧 schema、迁移/Save Copy、损坏注入、50/100/200 Tabs、9KX2 和音乐等价门；完成 UAT-B3-01～06。

每次交付报告必须列出：

- 实际修改文件和对应 T/UAT ID；
- 保存/读取 schema、section、字节和集合预算；
- 自动测试命令及真实结果，未运行项明确列出；
- 冷打开与懒激活的时间、Working Set、LOH/WPF/native/后台任务证据；
- 旧格式/坏 section/取消/竞态结果；
- 未解决风险和需要用户验收的具体步骤。

## 10. B3 实施记录（2026-09-20）

### 10.1 已实现范围

- `settings/project-presentation.json` 的当前 writer 升为 schema 4；schema 1/2/3 仍可读取，外层 Project Format 4 与音乐 source wire 不变。
- 新增独立 `workspaceNavigation` section，保存有序 Tab key、活动 Tab，以及已批准的页面/视口纯值；不保存选择、Undo/Redo、任务、菜单、焦点、缓存、VM、编译结果或音频资源。
- Save/Save Copy 在同一冻结 presentation snapshot 捕获导航；workspaceNavigation 作为完整 section 参加 64 MiB 总预算，超预算时整个导航 section 省略并保留音乐及其他可表达 presentation，不截断数组。
- Open 独立验证导航：固定 Arrangement、按稳定 owner ID 逐项丢弃失效 Tab/视图，活动项失效时回退 Arrangement 并给出状态提示；保存快照也会在 owner 删除后先过滤导航。Project Settings 与 Diagnostics 的当前页/筛选/页码也随项目保存。
- 重开只立即物化 Arrangement 与活动 Workspace；其余 Tab 使用轻量占位描述，用户激活时才创建实际 VM、应用保存的纯值并建立运行时状态。All Tracks 的 Compiled 准备沿用已有可取消后台链路，仅对活动实际视图运行。
- Instrument 当前 SubVoice 恢复在物化后立即重建该 SubVoice 的对象列表、Lane 与快照；Diagnostics 用户在恢复后修改筛选或翻页时会清除旧页码请求，避免迟到页码覆盖新筛选。

### 10.2 代码与格式文件

- `src/midora-core/Midora.Persistence/ProjectPresentationWorkspaceV4.cs`
- `src/midora-core/Midora.Persistence/ProjectPresentationCodecV4.cs`
- `src/midora-core/Midora.Persistence/ProjectPresentationV3.cs`
- `src/midora-core/Midora.Persistence/PersistenceContractV4.cs`
- `src/midora-core/Midora.Persistence/MidoraProjectPackageV1.cs`
- `src/midora-core/Midora.Application/ProjectPresentationSessionV3.cs`
- `src/midora-core/Midora.Persistence/Schemas/Json/project-presentation-v4.schema.json`
- `src/midora-core/Midora.Persistence/Schemas/Json/midora-json-v4.schema-set.sha256`
- `src/midora-desktop/Midora.Desktop/DesktopSessionController.WorkspaceNavigation.cs`
- `src/midora-desktop/Midora.Desktop/LazyWorkspaceViewModel.cs`
- `src/midora-desktop/Midora.Desktop/DesktopSessionController.cs`
- `src/midora-desktop/Midora.Desktop/DesktopSessionController.WorkspaceState.cs`
- `src/midora-desktop/Midora.Desktop/PresentationModels.cs`
- 相关 Persistence schema/golden/compatibility 测试同步升至当前 schema 4；Application snapshot 测试覆盖导航快照保留与失效 owner 过滤。

### 10.3 自动验证证据

已运行：

```text
dotnet build src/midora-core/Midora.Persistence/Midora.Persistence.csproj --no-restore -v:minimal
dotnet test src/midora-core/Midora.Persistence.Tests/Midora.Persistence.Tests.csproj --no-restore -v:minimal
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj --no-restore -v:minimal
dotnet build src/midora-desktop/Midora.Desktop/Midora.Desktop.csproj --no-restore -v:minimal
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj --no-restore -v:minimal
```

结果：Persistence 构建 0 警告/0 错误；Persistence Tests **245/245 通过**；Application 构建 0 警告/0 错误，新增导航快照测试 **1/1 通过**，此前 Application 全套 **1256/1256 通过**；Desktop 构建 0 警告/0 错误；Desktop Tests **531/531 通过**。本轮未运行真实 WPF 冷启动/9KX2 长会话、坏包故障注入和 50/100/200 Tab 资源压力测试，仍需人工 UAT 与后续工程门验证。

### 10.4 待人工验收

使用既有 UAT-B3-01～06：保存并重开完整 Tab 集合、活动页和视口；大型项目懒激活；焦点/快捷键；owner 删除/Undo；损坏与旧 schema；长会话和资源释放。尤其检查活动 Instrument 的 SubVoice/Lane 恢复、Diagnostics 页码与筛选交互、All Tracks Compiled 不阻塞首屏。

本记录不表示 UAT 已通过，也不表示已提交、推送或本地发布。

### 10.5 从 B3 前基线重新实施的复核（2026-09-21）

工作区曾回退到 B3 实施前；本轮按本文件已确认的 D01～D07 重新恢复上述实现，没有使用 computer-use、没有运行本地发布，也没有提交或推送。复核后的自动验证结果为：Persistence 构建 0 警告/0 错误，Persistence Tests **245/245**；Application 全套 **1257/1257**；Desktop 构建 0 警告/0 错误，Desktop Tests **531/531**。`git diff --check` 未发现空白错误（仅有 Git 的换行符提示）。真实 WPF 冷启动、坏包注入及 50/100/200 Workspace 资源压力仍未运行，继续由 UAT-B3-01～06 验收。
