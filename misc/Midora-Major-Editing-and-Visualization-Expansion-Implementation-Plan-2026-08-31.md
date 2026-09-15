# Midora 大型编辑与可视化扩展实施方案（已定案记录）

- 日期：2026-08-31
- 状态：**非规范性决策记录与实施计划；阶段 1～8 及后续 Loop、Onion 菜单/模式持久化/导航修订均已完成并获用户验收，相关代码已提交、推送。2026-09-09 用户再次确认目前人工验收大体全部通过；精细体验、完整作品 Bug 修复和查漏补缺留到后续真实编曲，不等同正式发布验收。**
- 适用仓库基线：`b8ed363`
- 目的：把本轮需求整理为可审查、可逐项定案、可分阶段实施的长期存档。

阶段 6 交付记录：`Midora-Stage6-Conductor-Requirement-Trace.md`、`Midora-Stage6-Conductor-Architecture-Decisions.md`、`Midora-Stage6-Conductor-Validation-Report.md`、`Midora-Stage6-Conductor-Acceptance-Checklist.md`。

阶段 7 交付记录：[需求及实现追踪](Midora-Stage7-Timeline-Object-Lists-Requirement-Trace.md)、[验证报告](Midora-Stage7-Timeline-Object-Lists-Validation-Report.md)、[14 项人工验收清单](Midora-Stage7-Timeline-Object-Lists-Acceptance-Checklist.md)。

阶段 8 交付记录：[需求及实现决策](Midora-Stage-8-Onion-Requirement-Trace-and-Decisions.md)、[使用说明](Midora-Stage8-Onion-User-Guide.md)、[验证与性能报告](Midora-Stage8-Onion-Validation-Report.md)、[合并验收清单](Midora-Stage8-Acceptance-Checklist.md)、[最终菜单/模式/导航修订](Midora-Stage8-Onion-Source-Modes-and-Navigation-2026-09-08.md)。当前收尾与下一项工作见 [2026-09-09 状态台账](Midora-Pre-Expansion-Closeout-and-Next-Step-2026-09-09.md)。

> 本文不是 SRS、ADR 或 Project Format 规范，不修改任何既有需求。凡是与现行 SRS 冲突、会改变可听语义、持久化格式、公共交互或性能基础设施的内容，必须先由产品所有者完成本文末尾的决策，再正式更新 SRS、跨系统不变量和 ADR，之后才能进入代码实施。

## 1. 本轮边界

### 1.1 本轮只做什么

- 审计现行 SRS、Domain、Application、Persistence、Compiler 与 Desktop 的相关约束；
- 修正原始需求中不够精确、彼此冲突或会形成性能陷阱的部分；
- 给出推荐的目标架构、实施顺序、失败边界、测试门和迁移策略；
- 列出需要产品所有者逐项决定的争议点。

### 1.2 本轮明确不做什么

- 不修改产品代码、测试、SRS、ADR、Project Format 或发布产物；
- 不创建或迁移任何用户数据；
- 不运行 `dist` 发布；
- 不使用 computer-use；
- 不把本文的推荐默认值视为已经定案。

## 2. 需求覆盖表

| ID | 原始需求 | 分类 | 决策状态 |
|---|---|---|---|
| R-01 | 所有程序数据固定放在程序根目录；缓存/临时数据进可见 `.tmp\`，配置与预设进分类数据目录 | 程序级持久化 / 运维 | 已定案 |
| R-02 | Bank/Program 名称目录，程序级导入、导出、编辑、可从 SF2 读取 | 程序级模型 / 资源元数据 | 已定案 |
| R-03 | Event Instrument 一键创建 Logical Parameter 并绑定一个或全部 SubVoice 事件 | Domain / Mapping / UI | 已定案 |
| R-04 | Conductor 左侧事件列表与 Tempo 阶梯图、密集事件优化 | Domain 查询 / UI / 渲染 | 已定案 |
| R-05 | 三种钢琴卷帘左侧虚拟事件数据列表与混合选择 | 选择模型 / UI / 性能 | 已定案 |
| R-06 | Note Humanize | 新编辑命令 | 已定案 |
| R-07 | Note Split / Join；Note 与 Event Quantize | 新编辑命令 | 已定案 |
| R-08 | Note/Event Batch Create 与表达式、预设 | 新编辑命令 / 表达式安全 | 已定案 |
| R-09 | Preview Keyboard 黑键力度按自身长度计算 | 已知缺陷 | 不需产品决策 |
| R-10 | 新建 Event Instrument 后首次播放偶发无声 | 已知缺陷 / 编译与音频生命周期 | 不需产品决策；需先定位根因 |
| R-11 | Note 创建长度按 delta Snap，不重写初始长度 | 已知交互错误 / 规格补充 | 已定案 |
| R-12 | Arrangement Segment Resize 预览闪烁 | 已知视觉缺陷 | 不需产品决策 |
| R-13 | Velocity 顶部标记在低缩放消失 | 已知视觉缺陷 | 不需产品决策 |
| R-14 | Logical/MIDI Segment 跨类型拖动、复制和粘贴 | Domain 转换 / 数据丢失确认 | 已定案 |
| R-15 | SubVoice Loop / Pre-Roll 时间线标记 | UI semantic overlay | 已定案 |
| R-16 | Event Draw 模式右键点菜单，同时保留右键拖线 | 指针状态机 | 不需产品决策 |
| R-17 | Initial State 合法格式的越界值自动 Clamp | 编辑语义 | 已定案 |
| R-18 | 时间线右键双击切换 Draw / Select | 指针状态机 | 已定案；接受菜单延迟 |
| R-19 | Event 批量编辑后恢复焦点 | 全局模态命令焦点 | 不需产品决策 |
| R-20 | Pure MIDI Track 独立颜色、导入/新建自动轮换；Logical Track 保留既有可选颜色 override | Project Domain / UI | 已定案 |
| R-21 | Track/SubVoice 洋葱皮与全轨 Raw/Compiled 叠加视图 | Project presentation / Compiler trace / 渲染 | 已定案 |
| R-22 | 选择附近浮动移动/Resize 工具框 | UI overlay / 选择聚合 | 已定案 |
| R-23 | 旧格式 Project 迁移后，普通 Save 经确认并自动保留旧格式副本后，可在原路径升级覆盖 | Project migration / 安全保存事务 | 已定案 |

## 3. 已确认的现状与必须纠正的前提

### 3.1 Bank 并不存在一份覆盖所有设备的“通用标准名称表”

MIDI 1.0 标准化的是 Bank Select 的数值机制（CC0/CC32）以及 Program Change；General MIDI 对 Program Number 提供通用乐器名称，但这不等于为所有 Bank MSB/LSB 组合规定了统一名称。GS、XG、SoundFont 和厂商设备各自有自己的目录。因此目标功能应称为 **Instrument Catalog / Bank & Program Catalog**，并明确每条名称来自哪一套 Profile，不能把某个厂商表冒充全局标准。

建议目录来源分层：

1. 内置 GM Program 名称；
2. 可选的、明确标注来源的 GS/XG Profile；
3. 用户导入或手工编辑的 Profile；
4. 用户主动从 SF2 `phdr` preset 元数据生成的资源目录；
5. SFZ 以文件名/用户命名作为单一目标条目，不扫描 sample/include。

名称只用于 UI，不改变 Project 中正式的 Bank MSB、Bank LSB、Program 数值，也不参与编译或 canonical fingerprint。

### 3.2 仅开启 WPF 虚拟化不足以支持百万对象列表

当前列表投影和选择模型仍会全量创建行对象、排序结果或 `ImmutableHashSet<MidoraId>`。即使 UI 只显示几十行，Shift 选择一百万项仍会物化一百万 ID，后续命令还会再次复制集合。

因此 R-04、R-05、R-06～R-08、R-21、R-22 的共同前置不是一个新 `ListBox`，而是：

- ordinal/range 查询；
- 基于固定快照的范围选择描述；
- 稀疏 additions/exclusions；
- 分页、流式命令消费；
- detached 结果构建与原子 page-root 交换；
- 有界缓存与精确范围失效。

### 3.3 现行规格存在明确冲突

以下需求不能作为“小 UI 改动”直接落地：

- SRS §20.4.7 当前明确排除 `Randomize / Humanize`；
- SRS §20.7.7 当前只允许异类选择的共同操作，本轮要求显式 `For Notes >` / `For Events >` 子集操作；
- SRS §18.1.6 当前不允许 Logical/MIDI Segment 隐式互转；
- SRS §17.2.5 与 §20.14 当前明确不在 `.midora` 保存 UI View State，而洋葱皮要求 Project 持久化；
- SRS §18.7 当前 Conductor Event List 位于底部，本轮改成左侧；
- SRS §4.15.1 当前不提供任意通用 Meta 编辑器；
- SRS §6 当前明确不解析 SoundFont preset 名称，本轮要求可由 SF2 读取目录。

### 3.4 Project Format 影响

当前 writer 是 Project Format 2。以下定案后会产生新的持久化语义：

- Project 内的洋葱皮配置，以及以后逐步加入的其他 Project presentation/view state。

这组变化应集中形成 **Project Format 3**，而不是在 Format 2 上偷偷增加字段。Format 1/2 reader、descriptor 和 golden 必须冻结，打开旧格式时 detached 迁移、完整验证、一次提交，保存只写 Format 3。旧格式文件在“打开与迁移”阶段仍绝不改写；迁移后的普通 Save 则改走专用原路径升级事务：明确确认、自动保留旧格式原字节副本、验证后原子发布 Format 3，不再永久禁止保存回来源路径。

Logical Track 继续保留既有颜色 override，不发生迁移。Pure MIDI Track 的可选颜色字段已经存在于 Domain、protobuf 与 codec；补齐自动赋色、Properties 和显示不要求新格式。Humanize、Split、Join、Note/Event Quantize、Batch Create 的结果仍是既有 Note/Event 数据，本身也不要求新格式；其 Preset 是程序级 JSON，不进入 Project。

## 4. 目标架构总览

```text
Writable program root
  -> Data / Preferences / Recent / Catalogs / Presets / Diagnostics
  -> .tmp / AudioCache / SessionContent / CompilerRuns / WorkerExchange

Project source
  -> paged timeline sources
  -> frozen range/ordinal selection descriptors
  -> detached paged edit planner
  -> validation + collision reducer
  -> atomic root swap + compact undo
  -> compiler / canonical result

Timeline source adapters
  -> virtual object list
  -> piano/event/conductor raster layers
  -> onion read-only layers
  -> hit testing / locate / floating selection tools
```

核心原则：

1. 不为百万对象建立百万 WPF Control 或 Row ViewModel；
2. 不在 UI 线程全量排序、枚举或构建 ID 集合；
3. 不从 bitmap 反推命中、选择或音乐语义；
4. 所有大型编辑 detached 构建，成功后一次原子提交，一个 Undo；
5. 表达式编译一次，循环中只调用有界委托；
6. Project presentation 数据不得污染 canonical、音频缓存或编译 fingerprint；
7. 功能共享核心，三种钢琴卷帘只使用不同 adapter，不能复制三套行为。

## 5. 分系统设计

## 5.1 程序根目录下的便携数据布局

### 5.1.1 当前问题

当前至少有 Preferences、Recent Projects、Presets、SessionContent 和默认 AudioCache 使用 `%LOCALAPPDATA%\Midora`。这会把容量预算和 I/O 静默放到系统盘，也使用户无法仅靠移动 Midora 目录完整搬迁程序状态。

### 5.1.2 推荐模型

产品所有者已否决 bootstrap / Registry / 可切换外部 Data Root。正式模型改为：**程序自身的规范化根目录是唯一程序数据根**，不得再向 `%LOCALAPPDATA%\Midora`、`%TEMP%\Midora`、Registry 或其他隐蔽位置写入 Midora 程序数据。

```text
<ProgramRoot>\
  Midora.exe
  Data\
    Preferences\
    Recent\
    Catalogs\
    Presets\
    Diagnostics\
  .tmp\
    AudioCache\
    SessionContent\
    CompilerRuns\
    AudioWorkerExchange\
```

`ProgramRoot` 固定为启动时规范化并冻结的 `AppContext.BaseDirectory` 物理路径，而不是当前工作目录。`Data\` 保存需要备份、分享或跨启动保留的小型正式程序数据，并按类型继续分类；`.tmp\` 虽以点开头，但不设置 Windows Hidden 属性，保存所有可删除、可重建、可能很大的缓存和工作文件。SoundFont、Project、MIDI、导出目标等用户主动选择的外部文件不复制进这些目录。

“所有程序数据”只承诺 Midora 自己创建和管理的文件；Windows WER dump、pagefile、文件系统日志和第三方系统级数据不受 Midora 路径策略控制。Schemas、assets、Native/BASS 和 exe 是只读程序组件，也不属于 `.tmp` 清理对象。

现有独立 `Local Cache Root` 配置应在正式变更中删除：AudioCache 固定为 `<ProgramRoot>\.tmp\AudioCache`。移动整个程序目录必须在 Midora 关闭时进行；复制 `Data\` 即可分享配置和 Preset，复制 `.tmp\` 不是必要条件。

正式 ZIP/升级包不得携带会覆盖用户状态的 `Data\` 或 `.tmp\` 内容；把新版本解压到另一个目录视为另一份独立 portable installation。初版继续按单用户应用定义，不支持多个 Windows 用户并发写同一程序根。Native/BASS 仍只按固定绝对组件路径和 pinned manifest 加载，绝不能因为 ProgramRoot 可写而从 `Data`、`.tmp` 或当前工作目录搜索 DLL。

### 5.1.3 生命周期与旧位置边界

新版不检测、不读取也不迁移旧 `%LOCALAPPDATA%\Midora` 数据；目前没有需要迁移的正式用户。新目录中的所有小型正式文件继续使用临时文件、校验、原子替换；大型工作目录继续使用版本 manifest、活动 lease/lock、直接子目录校验和启动期 best-effort 回收。

程序启动必须先对 `<ProgramRoot>\Data` 与 `<ProgramRoot>\.tmp` 做创建、写入、flush、原子替换、锁和删除的窄范围探测。不可写或能力不足时显示明确错误并拒绝进入主程序；绝不 fallback 到 C 盘或 `%TEMP%`，也不实现伪只读 Project 模式。首版只正式支持本机可写固定卷，拒绝 UNC/network 和可移动介质。

单实例身份固定为“当前用户 + 规范化 ProgramRoot”：同一根只允许一个 writer，不同根是互相独立的 portable installation，可同时运行并使用各自 Data/.tmp。`Data\Diagnostics` 有明确数量与总字节上限，跨启动保留并允许用户直接删除。

Worker IPC 与当前临时目录授权会校验 `Path.GetTempPath()`。改到 `.tmp\AudioWorkerExchange` 不是普通字符串替换：必须同步冻结 Host/Worker 双方允许根、父进程传递的 session token、路径规范化/reparse-point 防护和清理契约。

## 5.2 Instrument Catalog / Bank & Program Catalog

### 5.2.1 程序级模型

建议增加有序 Catalog Profile：

```text
CatalogProfile
  ProfileId
  DisplayName
  SourceKind = BuiltIn | User | ImportedSf2
  SourceSoundFontEntryId?   # 程序级稳定引用，不进入 Project
  Banks[]

BankEntry
  BankMsb
  BankLsb
  DisplayName?
  Programs[]

ProgramEntry
  Program
  DisplayName
```

它只保存在 `<ProgramRoot>\Data\Catalogs`，不进入 `.midora`。Project 始终保存数值；在另一份 Midora 目录没有同一 Catalog 时，Project 仍可完整打开、编译、导出，只是 UI 回退为 `Bank MSB n / LSB n / Program n`。

### 5.2.2 名称解析

UI 显示名称必须返回来源，例如：

```text
Concert Grand (Imported: Platinum Grand III)
Acoustic Grand Piano (General MIDI)
Program 0
```

同一三元组冲突时必须有确定优先级，不能依赖文件枚举顺序。若采用 SoundFont 顺序解析，程序级 SoundFont 配置项必须先获得稳定 `SoundFontEntryId`，资源目录绑定该 ID；不能靠可能变化的路径或列表 ordinal 冒充身份。推荐：用户 override > 当前 enabled SoundFont 列表按正式优先顺序解析出的资源条目 > 用户 Profile 顺序 > 内置 Profile。Disabled SoundFont 默认不参与播放上下文名称解析，但其目录仍可在 Catalog Editor 中浏览。

### 5.2.3 从 SF2 读取

推荐作为用户显式执行的 `Scan Presets...`：

- 只解析 RIFF `sfbk/pdta/phdr` 所需元数据；
- 不读取 sample 数据，不解析 modulators，不修改 SoundFont 设置；
- 不作为设置 Apply、播放准备或 Project 打开的隐式步骤；
- 读取失败只影响目录导入，不影响音频 Worker 使用该文件；
- 扫描快照先保留原始 `sf2Bank`（ushort）、preset 与名称；导入向 MIDI MSB/LSB 目录时再显式转换。bank 128 与 >127 值不得直接塞入 0..127 的 MIDI MSB；
- 生成的是可编辑的目录快照，SF2 后续变化不自动重写用户目录。

也可通过 BASSMIDI 查询 preset，但会把目录编辑器与 native Worker 生命周期绑定，且难以在音频不可用时工作，不作为首选。

### 5.2.4 导入导出

使用版本化 JSON，必须包含：

- schema/version；
- profile name/source；
- MSB/LSB/Program 原始整数；
- Unicode 名称；
- 稳定、确定的排序；
- 重复键拒绝或显式 merge policy；
- 未知字段按独立目录规范决定是否拒绝，不能沿用 Project Format 的隐式宽松行为。

## 5.3 Event Instrument 快捷 Logical Parameter 映射

### 5.3.1 推荐入口

在 Logical Parameters 区域新增 `Add Event Binding...`，对话框一次完成：

- Logical Parameter Name（默认 MIDI CC 正式名称）；
- Target Event Kind / Controller / RPN / NRPN 等；
- Target SubVoices：当前、勾选多个、全部；
- Source value type/range；
- Operation：Override / Add / Multiply / Remap Range / Clamp 等；
- 自定义输入/输出范围；
- 重名和既有 mapping 的处理预览。

首版建议继续使用现行 shortcut 的 Integer parameter，不顺便扩展 Double/Enum。默认名称按目标确定：`CC n - Name`、`Pitch Bend`、`Program`、`RPN msb,lsb`、`NRPN msb,lsb`，没有标准名时仍使用数值 fallback。

OK 后必须作为一个原子 Definition command：

1. 创建一个 Logical Parameter Definition；
2. 为每个目标 SubVoice 创建一个 Parameter Mapping；
3. 缺 Lane 时只创建空 `SubVoiceEventMapping` owner；Parameter Mapping 自己拥有其 Steps，不额外复制另一个同义 chain；
4. 若 UI 尚无对应 Lane，则创建“空入口”，不得在 tick 0 添加事件点；
5. 任一目标非法则整批不提交；
6. Undo 一次恢复全部。

### 5.3.2 运算语义

建议复用正式 mapping accumulator：

- Override：`result = mapped(x)`；
- Add：`result = c + mapped(x)`；
- Multiply：`result = c * factor(x)`；
- Remap Range：把 source range 映射到指定 target range；
- 本快捷命令创建的 Mapping 默认 `OverflowPolicy=Clamp`；这不改变正式 Mapping 仍可选择 Fail/Clamp 的全局能力。

新 Parameter 的中性 default 必须按模式冻结：Override 默认取目标 default，Add 默认 offset 0，Multiply 默认 factor 1；创建快捷绑定本身不得立即改变没有参数点时的声音。

其中 `c` 是链中此前累积值或状态型事件的保持值，不能把 Add/Multiply 实现为另一个 UI 私有算法。多个 Parameter Mapping 指向同一目标时继续按正式 mapping order 求值。

## 5.4 共用高性能 Timeline 数据与选择基础

### 5.4.1 `ITimelineObjectSource`

建议抽象统一查询接口，由 Logical Segment、MIDI Segment、SubVoice 与 Conductor 提供 adapter：

```text
Count(snapshot)
TryGetPageByOrdinal(snapshot, startOrdinal, count)
FindOrdinalAtOrAfterTick(snapshot, tick)
TryFindOrdinalById(snapshot, id)
QueryTickRange(snapshot, start, end, filter)
Prefetch(snapshot, range/ordinal)
```

稳定显示总顺序建议：

```text
Tick
-> formal phase / source order
-> Object kind rank
-> Lane/target
-> Key
-> formal order
-> Stable ID（只用于内部最终 tie-break，不展示）
```

所有 adapter 必须把导入重复、Opaque、Note 与 Point 映射到同一个确定总序；不得使用 hash/集合枚举顺序。显示顺序本身不改变 canonical 的同 tick 顺序，`formal phase/source order` 只用于稳定投影现有正式顺序。

### 5.4.2 压缩选择

建议选择状态表示为：

```text
Frozen source revision
+ ordinal ranges
+ sparse included IDs
- sparse excluded IDs
```

这样 Shift 选择一百万项通常只增加一个 range。需要真实 ID 的少量 UI 操作按页解析；大型命令直接流式消费 range。必须区分两类 revision 变化：其他命令在 detached 计算期间修改 Project 时，本命令因 stale revision 整批拒绝；只有本命令自己的原子提交产生新 revision 后，才使用其 `SelectionResultMapping` 把仍存在、被替换、被碰撞删除的对象映射到新选择。

### 5.4.3 大型编辑事务

推荐统一：

1. 冻结 owner、Project revision、选择描述和参数；
2. 后台按页读取；
3. detached 生成结果页；
4. 统一处理范围、溢出、碰撞；
5. revision 已变化则拒绝陈旧结果；
6. 一次原子 page-root swap；
7. 每个 owner 只发一次精确范围 invalidation；
8. Undo 保存旧 page roots / 紧凑 journal，不保存数百万份重复对象快照；
9. staging 同时受 records、RAM bytes、disk bytes 三项预算约束，开始前检查可用空间，运行中继续计量；
10. 每个 staging 目录携带 manifest、task/session owner 与活动 lock；启动和 Project 关闭时只清理无活动锁、路径校验通过的遗留目录；
11. Cancel、磁盘不足或表达式错误不得修改 Project、选择或消耗稳定 ID。

## 5.5 Conductor 编辑视图

### 5.5.1 布局

```text
Toolbar
┌─────────────────────┬──────────────────────────────┐
│ Virtual Event List  │ Tempo（高阶梯图）             │
│                     │ Time Signature                │
│                     │ Key Signature                 │
│                     │ Marker                        │
│                     │ Project End                   │
└─────────────────────┴──────────────────────────────┘
```

### 5.5.2 Tempo 阶梯图

Tempo 是离散状态，不是曲线插值：

- 旧值保持到变化 tick；
- 变化 tick 画竖线；
- 点落在新值位置；
- 新值从变化 tick 水平保持；
- tile 左边界查询“边界前最后一个 Tempo”以恢复保持状态；
- 不允许渲染斜线暗示 tempo ramp。

密集 LOD：同一 device column 保留首、末、最小、最大，画包络与最后保持值；点只有在可分离时才显示固定 device-size glyph。命中仍查询源数据。

编辑交互完整沿用 Event Lane：自由画线、两点直线、`y=k` 水平线、点拖动、框选及 Shift/Ctrl/Alt 修饰键。Snap 开启时按当前 Snap 间隔插入 Tempo state；关闭时按逐 tick 语义插入。即使创建了密集状态，显示仍是离散阶梯保持，不产生隐式 ramp。tick 0 的唯一 Tempo 与同 tick later-wins 规则必须在批量绘制归并阶段继续成立。

### 5.5.3 其他事件

- 正式 Tempo、Time Signature、Key Signature、Marker、Project End 均显示；
- 点固定 device size，按 event type/device column 聚合；
- 标签只在不互相覆盖时显示，完整文本在列表、Tooltip、Properties 中始终可见；
- 不建议首版把任意 Opaque Meta 提升为 Conductor Domain 事件。

## 5.6 三种钢琴卷帘左侧事件数据列表

### 5.6.1 共用控件

建立一个 owner-data `TimelineObjectListPane`，适配：

- Logical Segment：Logical Note + Logical Parameter Point；
- MIDI Segment：Direct MIDI Note + Channel Event + Opaque SysEx/Meta；
- SubVoice：Template Note + Template MIDI Event。

在 `Lanes` 左侧增加开关，默认隐藏。打开时只读取首个可见页与小幅 overscan；隐藏时不得做任何列表分页、排序或行构建。

### 5.6.2 行语义

推荐一条 Note 只显示为一行 Note 对象，列出 start、gate/end、key、velocity；不要拆成 NoteOn 与 NoteOff 两行，否则同一个正式对象会出现两个可选行，Selection、Properties 和 Delete 会产生歧义。

Opaque SysEx/Meta 可显示并支持 Locate、Properties、Delete，但不因为被归到 `For Events` 就开放数值 Batch Edit。

### 5.6.3 选择和菜单

- 单击替换选择；
- Ctrl 切换单项；
- Shift 按冻结的显示顺序选择 anchor 到目标的 ordinal range；
- 鼠标拖动按行范围选择，不创建每行 selection object；
- 双击打开该对象 Properties；
- Locate 使钢琴卷帘居中；事件必要时自动打开 Lanes 并切到目标 Lane；
- 右键菜单打开时冻结目标选择，后续 hover 或后台刷新不得改目标。

混合 Note/Event：

- Delete 处理全部，形成一个 Undo；
- Copy/Cut、Ctrl+E、Ctrl+Q 等类型专用快捷键禁用；
- 一级菜单显示 `For Notes >` 和 `For Events >`；
- 子菜单只处理用户显式选择的类型子集；`For Events` 内仍只显示冻结 Event 子集全部共同合法的命令，必要时再按 lane target/value domain 分组；
- 未处理类型继续保持选择；
- Undo 恢复完整原选择；
- 选择含只读或当前锁定对象时，跨类型 Delete 整体禁用，不得部分删除。

## 5.7 共用受限数字表达式核心

不能直接把当前 Batch Edit 类复制到 Split 和 Generator。建议提取：

```text
BoundedNumericExpressionCompiler
+ ExpressionProfile
+ VariableSchema
+ ResultDependencyGraph
```

统一安全边界：

- 非空表达式必须以 `=` 开头；
- 最长 8,192 scalar；
- 最多 512 syntax node；
- 最大语法深度 64；
- 只允许 numeric literal、批准运算符、条件表达式和固定快照的 `System.Math` 数值函数；
- 禁止赋值、lambda、对象创建、数组、索引、语句、循环、任意 API 和反射；
- 结果必须是 finite `double`；
- result variable 依赖图必须无环；
- 每个表达式只编译一次，迭代中重复调用委托。

表达式本身不含循环且 AST 有硬上限，因此不建议对每一个对象设置 10 秒 timeout。长操作使用最大候选数、可取消前台任务、chunk budget 和 Project revision 检查。

所有 Preset 必须保存独立 `presetSchemaVersion`、`expressionProfileId/version`、`toolKind` 与数值格式契约。加载旧版或外部 Preset 时重新执行完整语法/API/资源边界验证，不能反序列化后直接执行。

## 5.8 Humanize

首版只作用于 Tick、Gate、Velocity，不包含 Key。Seed 提供 `Auto` 与显式输入两种方式；Undo/Redo 冻结已经生成的结果。

每字段：

```text
Disabled
Add       old + Uniform(min,max)
Multiply  old * Uniform(min,max)
Override  Uniform(min,max)
```

- Add/Override 对整数属性使用闭区间均匀整数；
- Multiply 生成连续 double，最终 `AwayFromZero`；
- `min/max` 必须 finite 且 `min <= max`；Add 允许负范围；Multiply factor 必须 `>=0`，允许 0，负 factor 验证失败；
- 先为 Tick/Gate/Velocity 生成全部 raw result，再按固定联合顺序归一化，不能因字段 UI 顺序改变结果；
- Velocity clamp 1..127；
- Gate 至少 1，并以归一化后的 start 做 checked `start + gate`；无可表示正 Gate 时整批失败；
- Segment Note 的模型边界不是当前暴露/crop 窗口；结果仅落在 crop 外仍合法且保留。若 Tick 原始结果越过 owner 的正式硬边界，则删除该 Note；不自动扩展 Segment 暴露窗口或 SubVoice Template Length。Velocity/Gate 仍按各自边界 clamp，不因越界删除 Note；
- 每个对象/字段独立抽样；随机值按 `(seed, owner stable identity, frozen formal ordinal, field kind)` 派生，新增/禁用另一字段不得改变其他字段的结果；
- 最后执行 Note later-loses 碰撞；
- 使用 Midora 固定 PRNG 与冻结 seed，不能依赖未来 .NET 版本可能变化的 `System.Random` 序列；
- Undo/Redo 必须重现相同结果。

## 5.9 Note Split

推荐采用**每个 owner 内的全局选区刀线**：

```text
selectionLeft  = min(note.start)
selectionRight = max(note.end)
```

刀线从左到右严格递增，只切穿过该 tick 的已选 Note；落在空隙的刀仍计数。不同 Segment/SubVoice 分别计算范围，但整个命令仍为一个 Undo。

模式：

1. Fixed Piece Length：每隔 N ticks 一刀；
2. Maximum Piece Count：把选区 span 分为最多 N 个平衡区间；使用整数有理边界 `floor(j*span/blockCount)`，去除重复和两端边界，保证可形成时各区间长度差最多 1 tick；
3. Expression：
   - `i` 为当前刀序号；
   - `tr` 为上一刀相对 selectionLeft 的 tick，第一刀前是 0；
   - 返回值是相对上一刀的正长度；
   - finite 后 AwayFromZero，再 clamp 到至少 1；
   - 下一刀达到/越过 selectionRight 时停止，不在终点产生零长度片段；
   - 每次求值都计入 Maximum Cuts。

算法必须是单调刀线与按时间排序 Note 的 active-interval sweep；允许使用 128 个 key bucket 和有序端点结构，目标复杂度为 `O((N+K) log N + producedFragments)`，禁止每条 Note 遍历全部刀线的 `O(N*K)`。

Maximum Cuts 不能替代 Maximum Result Objects：一刀可切穿大量重叠 Note。正式任务还必须冻结最大结果片段数、最大 staging bytes 与磁盘空间门，超限时整批失败。

继承：Key、NoteOn velocity 与共有属性继承；每个 Direct Note 片段都继承源 NoteOff velocity。按时间最早的第一片保留原 Stable ID，后续片分配新 ID；新片段的 NoteOn/NoteOff formal order 必须由源 order 与片段序号确定生成，不得由新 ID 决定。最终保留的全部片段保持选中；Undo 恢复原对象与原选择。

## 5.10 Join Notes

按 owner + key 分组并按 start/formal order sweep。建议用 `Maximum Gap Ticks` 明确语义：

- 默认 0，只连接 overlap 或首尾相接；
- `next.start - current.end <= MaximumGap` 时并入同一 run；
- Maximum Gap 是非负 int64；所有 end、差值和最终 length 使用 checked 算术，溢出整批失败；
- 输出 `[first.start, max(end))`；
- NoteOn velocity 默认取第一条；
- Direct NoteOff velocity 默认取 run 最后一条；
- 第一条保留 ID，其余删除；
- 不同 owner 不连接；
- 一个命令、一个 Undo、结果保持选中。

Join 只处理选中 Note。结果跨过未选中的同 key Note 时，不删除或改写未选中对象；不同 start tick 的重叠继续保留，并由既有 Event Instrument overlap/编译诊断规则处理。

该参数同时覆盖“绝不跨静音”和“允许填平小间隙”两种使用场景，避免把隐藏语义写死。

## 5.11 Quantize Notes / Events

UI 复用 Snap 的友好拍值列表，并提供 Custom Ticks。计算必须调用同一正式 grid/time-signature 服务，不能另写一套近似算法。

Segment Note 必须先从 content-local tick 经 `ProjectStartTick` / `ContentOffsetTick` 转为 Project absolute tick 选择格点，再确定性投影回内容坐标；拍号变化读取该 absolute tick 的 Time Signature Map。SubVoice 没有 Project conductor 上下文，以模板 tick 0 为固定原点，只使用 TPQN 分数/custom ticks。

Note 已定案：

- 对话框同时提供 `Start only` 与 `Start and End`，默认 `Start only`；
- 首版固定 100%，不提供 Strength；
- 三种视图都不提供 Bar，只提供分数与 Custom Ticks；
- 恰好处于两格正中时选择较早格点；
- start/end 分别吸附后若 `end <= start`，把 end 饱和到 `start+1`。

Event Quantize 是同一基础服务的 point-only profile：只改变 Tick，不改变 Value、lane target、formal payload 或其他属性；不同 lane 可以在同一命令中处理，碰撞键仍按各自正式 lane target 分开。首版只覆盖 Direct MIDI Channel Event、Logical Parameter Point 与 SubVoice MIDI Event，不包含 Opaque SysEx/Meta 或 Conductor 事件。

无论选择哪种模式：

- Gate 最终至少 1；
- Event 同 tick + 同正式 lane target 时按冻结的原 formal order later-wins；被命中的既有导入重复全部属于本次碰撞归并范围，未命中的重复保持；
- tick 算术 checked；
- 同 start+key 按冻结的原 formal order 判定 later-loses，不能按 ID set 或分页返回顺序；
- 不修改未被命中的导入重复记录；
- 结果与 Undo 的选择状态确定。

## 5.12 Batch Create Notes / Events

### 5.12.1 UI 与 profile

Note Generator：

```text
Velocity expression / Initial (default 1)
Key expression      / Initial (default 0)
Gate expression     / Initial (default 1)
Tick expression     / Initial (default 0)
```

Event Generator：

```text
Point Value expression / Initial (default lane minimum)
Tick expression        / Initial (default 0)
```

共同字段：Base Tick（默认编辑线）、Maximum Candidates（默认 65,535）、可选 Maximum Relative Start Tick、`Create first object from initial values` 复选框（默认关闭）、Validate、Help、Preset。该复选框必须保存进 Preset。

### 5.12.2 推荐迭代 ABI

- `i` 从 0 开始；
- `*0` 是上一轮结果，第 0 轮取 Initial；
- `*1` 是本轮按依赖图计算后的结果；
- 全部本轮结果完成后才整体成为下一轮的 `*0`；
- `tr` 固定等于本轮输入 `t0`（上一轮/Initial 的相对 tick）；Base Tick 只是坐标原点，因此若 Initial Tick 非 0，第一个候选输入的 `tr` 也非 0；
- 复选框关闭（默认）时，第 0 个对象先执行表达式，再使用本轮 `*1` 创建；
- 复选框开启时，Initial 直接产生 candidate `i=0` 并计入 Maximum Candidates；第一次表达式求值使用 `i=1`，以前一 Initial 对象作为 `*0`；
- Tick 是相对 Base Tick 的 offset，实际 tick=`Base + round(t1)`；
- 空表达式表示 identity，非空必须以 `=` 开头。

停止顺序：

1. 达到 Maximum Candidates；
2. 求值；
3. 非 finite、依赖错误或 checked overflow 则整批失败；
4. 若相对 Tick 超过 Maximum Relative Start Tick，则停止且不创建本轮；
5. 对字段执行正式 clamp；
6. 形成候选；
7. 统一碰撞归并。

Maximum 必须计表达式迭代/候选，不是碰撞后的保留数量，否则持续生成同一 tick 的表达式可能永不终止。

碰撞：

- Note：既有对象优先；候选间 `i` 较小者优先；
- Event：候选覆盖命中的 exact-key 既有事件；若该 key 有多个导入重复，视为全部被本次编辑命中并全部移除，只保留最后候选；候选间 `i` 较大者优先；
- Direct Note 的 NoteOn/NoteOff order、Channel Event 相对其他同 tick 事件的 order 都必须由冻结的 source order 和 iteration 确定生成，禁止依赖 dictionary、新 ID 或外部归并偶然顺序。

Tick 表达式可以倒退，因此任意表达式路径要以有界 chunk 生成临时页，再按 `(tick,key,iteration)` / `(tick,target,iteration)` 外部归并。首版快速路径只对白名单化、显然单调的形式开放，例如 `t1=t0+c` 且冻结的 `c>0`；含条件、Math 或跨字段递推的一律外部归并，不能依赖一般性的“单调证明”。

Maximum Candidates 之外另设 staging RAM/disk byte budget 与剩余空间门。负 Tick clamp 到 0 时仍消耗候选数，可能产生大量碰撞，Help 和进度必须同时显示 candidates 与 retained count。Preset 保存表达式、Initial、首对象复选框、Maximum、可选 max tick，不保存绝对 Base Tick、owner 或 lane target；并携带版本化 profile 元数据。目录固定在 `<ProgramRoot>\Data\Presets` 的对应分类中。

## 5.13 Logical/MIDI Segment 跨类型转换

### 5.13.1 转换范围

跨类型拖动、复制、剪贴板粘贴均使用一个正式 `SegmentConversionService`，不能各自实现。

共有字段：

- Segment Project start、length、content offset；当前两种 Segment 没有共同的名称字段；
- Note start、length/gate、key、NoteOn velocity；
- 暴露窗口。Direct event endpoint order 不是可转换字段，只能用于确定遍历、碰撞与新 order 的生成。

不能转换：

- Logical Parameter Points；
- Direct Channel Events；
- Opaque Meta/SysEx；
- Direct NoteOff velocity 到 Logical Note；
- Event Instrument 特有映射/实例语义。

Logical -> MIDI 新 NoteOff velocity 固定为 0，与当前跨类型 Note clipboard 一致。

### 5.13.2 失败原子性与警告

- 扫描整个 Segment 正式内容，不只扫描当前暴露窗口；
- 有任何不可转换数据时，预览警告列出按类型计数；
- 用户确认后才 detached 构建目标；
- 同一次拖动/复制允许包含多个 Logical/MIDI Segment，统一展示一次丢弃摘要，全部成功或全部失败；
- Move 只有在目标完整验证并准备提交后才删除源；
- 目标 Segment overlap、未绑定 Logical Track、range 或结构约束非法时源保持不变；Note exact collision 按 later-loses 归并并计入丢弃摘要，不作为整批失败；导入形成的同 start+key 重复 Direct Note 转为 Logical 时按冻结 source order 保留第一条，其余计入丢弃摘要；
- 一次转换是一个 Undo；
- Copy/Paste 结果选择目标，Move 保持转换后目标选择；
- hidden Note 必须转换；非 Note 数据按类型和数量提示。MIDI -> Logical 只有在至少一个 Direct NoteOff velocity 非零时才额外提示该字段及受影响数量；全为 0 时不产生无意义警告。

## 5.14 Track 颜色

Pure MIDI Track 的可选 opaque sRGB 字段已经存在于现行 Domain 和持久化格式；本项只需补齐正式赋值命令、Properties、导入/新建策略和消费显示。Arrangement、Segment、数据列表、洋葱皮共享一个颜色解析服务。导入和新建使用固定 8 色低饱和 palette 按全局 Arrangement 顺序轮换；Duplicate 默认继承源颜色，新建后下一轨继续轮换。

Logical Track 保持现行正式模型：默认解析 Event Instrument Definition 颜色，并允许 Track 自己的可选颜色 override。不得删除、隐藏或迁移既有 override，也不得把某条 Track override 写回共享 Definition。该决定不触发 Project Format 升级。

## 5.15 洋葱皮与全轨叠加视图

### 5.15.1 当前 Segment 洋葱皮

洋葱皮是独立、只读、不可命中的 raster layer：

```text
selected source-track onion blocks
-> current Segment normal notes
-> current selection / edit preview
```

不能把来源音符合并进当前 Segment snapshot，也不能污染当前音符 tile fingerprint。

时间映射：当前 Segment local tick -> Arrangement absolute tick -> 查询来源轨道相交 Segment -> 投影回当前 local tick。默认建议只显示来源 Segment 暴露范围，不显示被 crop/content offset 隐藏的对象。

性能：

- 按 Arrangement 顺序叠加；
- 例如每 8 轨合成一个 overlay block；
- 单轨变化只失效所属 block；
- 低缩放按 device column 聚合；
- block cache 有明确内存上限；
- 100/200/400 Track 都不能在 UI 线程全量枚举。

### 5.15.2 All Tracks 视图

- Raw：从 Project source 按 Arrangement 轨道顺序投影；
- Compiled（2026-09-08 用户更正）：Logical 只消费 Canonical Compiled Result，NoteOn/NoteOff 按正式 FIFO 配对；Pure MIDI 复用当前源音符，不建整曲 FIFO 显示索引。此混合显示不改变正式编译/播放/导出。
- canonical source 当前已经包含 TrackId；Compiled 模式先验证所有 Logical/Pure 编译路径均完整携带并直接复用，只有发现缺口时才扩展 source trace，不能由 Port/Channel/名称反推；
- 可视范围查询必须包含 viewport 左边界之前 NoteOn、但 Gate 延续到 viewport 内的活动 Note，不能只查询 viewport 内 NoteOn；
- Compiled 模式按来源 Logical/Pure Track 颜色显示；编译失败或结果过期时仅逻辑层保留最后成功结果并明显标记 `Logical stale`；MIDI 始终显示当前源音符；
- 只读，不显示 Lanes，不参与 Project 选择。

### 5.15.3 持久化

推荐 Format 3 增加独立 `settings/project-presentation.json`。它必须有 manifest entry kind、checksum、确定排序、严格 unknown/duplicate field 规则、损坏隔离与 Save Copy 语义，并保存：

- 每个目标 Track 的 source Track onion preset；
- 每个目标 SubVoice 的 source SubVoice onion preset；
- track/subvoice stable references 与层顺序；
- enable、opacity 与默认 Raw/Compiled 模式。

每个目标 Track 保存自己的 source Track 列表，每个目标 SubVoice 保存自己的 source SubVoice 列表；当前 Track/SubVoice 自动从自身 onion 来源排除。Duplicate 复制来源配置并重映射被深复制对象的内部 ID。Track/SubVoice 删除时在当前会话把相关引用变成 dormant；Undo 恢复同 Stable ID 时重新生效。写入文件时过滤仍悬空的引用，Reader 不允许悬空 ID。

已定案保存来源、顺序、enable、opacity 与默认 Raw/Compiled；缩放、滚动、列表宽度和工具框位置本轮仍是 session state。以后其他视图状态可逐步进入同一 Project presentation 模型。

Presentation 文件不参与 compiler/canonical fingerprint。Presentation 变更不进入 Undo/Redo，也不设置音乐 Project Modified/标题星号；实现维护独立的 `PresentationRevision/PresentationDirty`，但只随用户显式 Save/Save Copy 写入。Presentation-only 变化不触发关闭提示或后台 Project rewrite，未显式保存就关闭/崩溃时允许丢失。

Save 开始时同时冻结音乐 Project source 与 presentation snapshot；Save 期间发生的新 presentation 变化保持为 pending revision。Presentation 文件损坏时隔离该文件、恢复默认视图并报告 Warning，绝不能阻止音乐 Project 加载；以后加入的其他 Project view state 也采用同一损坏隔离行为。

## 5.16 SubVoice Loop / Pre-Roll overlay

- `[0, PreRollTicks)` 背景使用 active range 之外的压暗色；
- Loop Start/End 为 device-snapped 黄色竖线，贯穿 piano、Velocity 与 Event Lane；
- 顶部 ruler 显示 start/end tick 标签和范围带；
- 只有一个 Loop 端点时显示该端点，不填充完整范围；
- overlay 变化只重绘 semantic overlay，不使 Note/Event tile 失效。

## 5.17 Selection 浮动工具框

使用 Timeline host 上方唯一 overlay/adorner，不为对象创建控件。

固定内容：

- 使用 Pin 图标与 Tooltip，不称 Lock；
- Note 与 Arrangement Segment 都显示方向可辨的左边界 Resize、右边界 Resize；Event Point 不显示 Resize；
- Move：仅在选择集存在共同合法 delta 语义时可用；
- Move 手势开始时按住 `Ctrl`，只在当前对象类型已有 Copy Drag 能力时触发 Copy+Move；不支持时 Invalid，不得退化为 Move；
- 顶部 grip：用户手动移动工具框。

任何选择规模都显示 Snap、边界和 Clamp 后实际会提交的 delta，例如 `+96 Ticks`、`-8 Keys, +192 Ticks`。浮动工具不得建立独立预览阈值：小选择复用普通 Draw 的即时矢量预览，大选择复用同一平移/Resize raster tile；不得因超过阈值而只显示 delta 或让选择预览消失。选择 bounding metrics 在后台增量维护，不能每帧遍历全部选择，也不能为每个对象创建 WPF 控件。

Pin 行为固定为：

- Follow（默认）：跟随选择包围框，并始终保持在 viewport 内；选区出界时贴近视图边缘；
- Pin：相对选区/时间线世界位置固定，不做 viewport 自动跟随；平移视图时可以移出屏幕；
- 两种状态都允许拖 grip 手动移动。

工具框位置只保存为 session/workspace state，不进入 Project。

## 5.18 指针、焦点和小型缺陷的统一修复

### 5.18.1 Event Draw 右键点

状态机：

1. Right Down 命中点：只冻结 hit 与原选择快照，进入 pending；不得在此时改变正式 Selection，也不立即画线或开菜单；
2. 移动超过系统 drag threshold：取消菜单候选，按原起点进入现有右键画线；
3. Right Up 未超过 threshold：按右键选择规则打开点/选择菜单；
4. 一旦进入画线，即使鼠标回到起点，松开也不弹菜单。

冷页 exact hit query 尚未返回时，手势保持 pending，不得先按空白启动画线；query 完成、取消、pointer capture 丢失和 Escape 都必须有确定结束路径。

### 5.18.2 右键双击

所有三种 piano roll、全部 event/parameter lane 与 Arrangement 的可编辑时间线区域统一支持右键双击切换 Draw/Select；当前处于其他模式时切到 Select。第一笔未拖动 Right Up 以单调时钟启动固定 300 ms 单击菜单/双击候选窗口；只有同一 Surface 合法内容区的第二个 Right Down 位于 `[0, 300 ms)` 且与首个 Right Up 的水平、垂直位移分别不超过 `6 DIP` 时才切换模式并取消菜单。实现不得使用 WPF `ClickCount`、Windows double-click time 或系统空间范围；右键拖动超过阈值时取消菜单与双击候选。对象区、空白区和不同 Workspace 不得采用不同时间或轴向位移阈值。

### 5.18.3 模态焦点恢复

建立统一 `ModalCommandFocusScope`：打开对话框前记录发起 TimelineSurface；关闭并完成命令后 Dispatcher 恢复焦点；若 workspace 已关闭/切换则不恢复。覆盖 Batch Edit、Scale、Transpose、Properties、Humanize、Split、Quantize、Generator，而不是只修 Ctrl+E。

### 5.18.4 已定位缺陷

- 黑键 velocity：当前按整个控件高度归一化，应按命中黑键自身 Bounds 归一化；
- Note Creation Snap：当前 Timeline 与宿主存在二次吸附，并在拖动后丢失默认长度基数；应冻结初始长度 `L0`。固定 step 且 Snap 开启时，正向拖动越过手势阈值后 `n=max(1,ceil(max(0,pointerDeltaTicks)/step))`、`length=L0+n*step`；回拖到非正 delta 时回到 `L0`。Snap 关闭时使用原始整数 delta，仍不短于 `L0`。Shift time-lock 创建只改 Key，不得改变长度；
- Segment Resize preview：拖动 delta 反复取消/重建 raster，应用冻结的可见矢量 geometry 每帧只加 delta；
- Velocity marker：aggregate 路径必须为每个可见 onset column 保留至少一个固定 device-size 顶部 marker；
- Initial State clamp：Event Instrument 全局 Initial State 与每个 SubVoice Initial State 的全部数值输入都覆盖；只对语法正确且可表示的数值执行目标范围 clamp，语法错误、NaN/Infinity 继续拒绝；发生 clamp 时短暂显示非阻塞说明，不产生 Error。

### 5.18.5 “新 Event Instrument 首次播放无声”

当前信息不足以断言根因。实施前必须建立稳定自动复现并记录：

```text
create Definition
-> create/bind Usage and Logical Track
-> create Segment/Note
-> incremental compile revision/fingerprint
-> canonical Unit/event count
-> playback plan/cache key
-> Worker submitted events
```

现有 Application 层回归已经覆盖“创建、编辑、绑定、创建 Segment/Note 后无需保存即可得到 canonical NoteOn”以及分多次编译后绑定；因此目前不能把根因直接归到 Compiler。更可能的范围是实际 UI 命令路径、canonical 发布之后的 realtime plan/fragment、PCM cache identity 或 Worker generation，但这仍是待验证推断。重点排查 Definition/Usage 绑定后的编译 dirty range、canonical revision 发布、音频计划缓存 identity、Worker 预热时冻结的项目 revision。修复必须发生在最早丢失状态的层，禁止用“首次播放强制 full compile”或“静默重试”遮盖。

## 5.19 旧格式 Project 的原路径升级保存

### 5.19.1 用户工作流

打开受支持的旧格式 Project 时继续使用 detached migration：打开和迁移本身绝不写入来源文件。迁移后的普通 **Save** 不再弹普通“首次保存”路径选择器，也不再以“旧来源只读”为由永久拒绝；它进入专用的 **Upgrade Project In Place** 流程：

1. 对话框明确显示来源路径、来源 Format、目标 Format，以及将要自动创建的永久旧版副本路径；
2. 用户取消时不创建任何文件，Project 保持 migration-dirty；
3. 用户确认后，先构建并严格重开验证当前格式临时包；
4. 自动保留来源文件的原字节副本；
5. 只有旧版副本已经安全存在时，才允许以当前格式原子替换原路径；
6. 成功后把原路径设为正式 `CurrentProjectPath`，清除迁移保护和 migration-dirty；后续 Save 完全按普通当前格式保存，不重复生成迁移副本。

用户此前是否手工执行过 Save Copy 不影响这条流程，也不再造成无法保存：普通 Save 始终可以进入上述确认事务。Save Copy 到其他路径仍只生成当前格式副本，不改当前路径、不清除 migration-dirty，也不代替旧格式原字节副本。Save Copy 若目标就是受保护的旧来源路径，则不得绕过升级事务，应提示改用普通 Save。

### 5.19.2 永久旧版副本

旧版副本必须是来源 `.midora` 的逐字节副本，不是把迁移后的内存 Project 重新序列化为旧格式。推荐可见名称：

```text
<ProjectStem> - Original Format <old> before Format <current>.midora
```

它与来源文件位于同一目录，保留 `.midora` 扩展名，可以作为旧版本 Project 直接打开。命名继续经过现有 Windows 安全文件名和长度规则；使用不区分大小写的冲突键。目标已存在且字节与当前旧来源完全一致时可复用；内容不同时绝不覆盖，预先冻结并在确认框显示稳定的 ` (2)`、` (3)` 后缀。确认后若该名字被其他进程抢占，则本次事务中止并重新确认，不能静默改成用户未确认的路径。

该副本是用户文件：不加入临时目录、缓存配额或自动清理，也不自动加入 Recent Projects。只承诺主文件字节完全一致，不承诺复制 NTFS ACL、ADS 或文件时间等文件系统元数据。

### 5.19.3 失败原子与并发保护

专用升级事务必须冻结来源绝对路径、来源/目标 Format、打开时来源内容 identity、最终备份路径和本次确认授权。来源 identity 至少包含长度和完整内容摘要；实现应在已经读取 Project 的数据流上同步计算，避免为打开过程额外完整读取一次。升级发布期间取得排他 source lease，并在发布前重新验证 identity，不能覆盖打开后被外部程序替换的文件。

正式失败结果固定为：

| 失败点 | 结果 |
|---|---|
| 用户取消，或当前格式序列化/自校验失败 | 来源不动，不创建永久副本，仍为 migration-dirty |
| 来源丢失、identity 改变或无法取得排他 lease | 中止并要求重新打开；不得备份或覆盖未知新文件 |
| 副本复制、flush、校验或发布失败 | 来源不动，绝不进入替换阶段 |
| 永久副本已存在，但当前格式发布失败 | 永久副本保留，来源仍为旧格式，Project 仍为 migration-dirty；重试可复用同一份一致副本 |
| 原子发布成功、临时清理失败 | 保存仍成功，永久副本保留，只报告清理 Warning |
| 发布成功但进程在内存状态提交前崩溃 | 下次按磁盘实际当前 Format 正常打开，不再生成第二份“旧格式”副本 |

普通 Save 的短生命周期事务备份与这里的永久旧版副本是两种不同职责，不得复用一个“成功后删除”的隐藏 `.bak` 冒充用户副本。Windows 实现可优先采用同卷、带唯一 backup destination 的单次原子 replace，但正式约束是上述前置条件、结果和失败原子性，而不是某个具体 API。

## 6. 实施工作包与依赖顺序

### 八个实施检查点与人工验收节奏

原五阶段中，编辑工具、Conductor/三种对象列表、跨类型 Segment/Onion 三组的单阶段工作量仍明显过大。实施调整为八个可独立构建、可回退、可人工验收的检查点；每个检查点只引入一个主要架构变化或一组高度相关的用户工作流。检查点不是八次发布：除最后阶段外只做针对性自动回归和人工验收，避免产品所有者每次重复完整验收。

每一阶段结束后停止继续扩展，先提交一份阶段报告并等待产品所有者验收；验收发现的问题归入当前阶段修完，不能把已知失败带入下一阶段。阶段边界不自动授权 Git push、`dist` 发布或 computer-use，这些仍只在当前请求明确要求时执行。

每份阶段报告固定包含：

1. **已修改项清单**：按本文 `R-*`、工作包、SRS/ADR/Format、代码模块列出；
2. **行为变化与兼容性**：用户可见变化、文件迁移、Undo/Redo、诊断与失败语义；
3. **自动验证证据**：实际执行的命令、通过/失败/跳过数量、性能样本与峰值资源；
4. **已知风险和未完成项**：不得用“后续会修”掩盖当前阶段验收失败；
5. **用户测试清单**：每项包含前置条件、具体步骤、预期结果和重点观察点；
6. **进入下一阶段的门**：产品所有者明确验收通过，或明确接受记录在案的剩余限制。

#### 阶段 1：正式规格、Format 3 与安全持久化

覆盖 `WP-00`、`WP-03`，以及 `WP-09` 中 Format 3 与旧格式原路径升级事务。主要交付：

- 正式更新 SRS、跨系统不变量、ADR、Format 3 schema/descriptor/golden；
- Program-root portable storage、`.tmp` 生命周期和旧 `%LOCALAPPDATA%` 边界；
- Format 1/2 detached migration、确认后自动保留旧格式原字节副本并原路径升级；
- 为后续 Onion/presentation 冻结 Format 3 容器，但本阶段不提前实现 Onion UI。

交付给用户的重点测试清单：

- 分别打开 Format 1/2 样本，验证取消升级、确认升级、自动副本、原路径覆盖、重复 Save、Save Copy、备份撞名与重新打开；
- 验证程序根 `Data` / `.tmp` 的位置、只读目录失败、两份 portable 副本隔离与临时残留回收；
- 当前 Format 的普通 Save/Save Copy、保存失败和崩溃恢复不回归；
- 确认 Project 音乐内容在 Format 3 round-trip 后与迁移前 canonical 完全一致。

#### 阶段 2：分页选择、编辑事务与直接操作可靠性

覆盖 `WP-01`、`WP-02`、`WP-11`。这是后续百万对象列表和全部大型批量工具的共同性能门；选择工具框在本阶段成为新 selection metrics 与 command adapter 的可见验收面。主要交付：

- ordinal/page 查询、压缩选择、流式 detached edit、compact undo 与 source trace 基础；
- 明确的 page/chunk/tile、常驻内存、临时磁盘和取消响应预算；
- 黑键 Velocity、Note delta Snap、Segment resize preview、Velocity marker、Initial State clamp、modal focus；
- 新 Event Instrument 首次播放无声的根因修复；
- 选择浮动工具框、默认 Follow/可选 Pin、手动位置、Move/Ctrl Copy+Move、Note/Arrangement Segment 双边 Resize adapter，以及所有规模共享 delta 与矢量/瓦片预览 UX；
- 全时间线右键双击 Draw/Select 与延迟右键菜单状态机。

交付给用户的重点测试清单：

- 使用 1M/18M 样本做框选、Ctrl/Shift 选择、移动、Resize、复制粘贴和首次/重复 Undo/Redo；
- 冷页滚动、快速缩放、编辑后局部刷新及取消大型操作，确认 UI 不阻塞、结果不陈旧；
- 三种钢琴卷帘验证黑/白键力度、创建 Note delta Snap、低缩放 Velocity marker；
- Arrangement 批量 resize 预览、弹窗关闭后的快捷键焦点和 Initial State clamp；
- 新建 Event Instrument 后不保存即绑定、写 Note、连续播放；
- 工具框默认 Follow、Pin、贴边、手动拖位、移动/Ctrl Copy+Move、Note/Segment 双边 Resize、异构选择门控、所有规模有效 delta，以及小选择矢量/大选择瓦片预览；
- 右键单击、双击、拖动、固定 `[0, 300 ms)` / 轴向 `6 DIP` 边界、菜单延迟、capture loss、Escape，并确认不同 Windows 双击设置不改变结果；
- 对比阶段开始前基线，确认现有已验收的渲染、选择和编辑速度没有退化。

#### 阶段 3：Instrument Catalog、快捷 Parameter Mapping 与 Track Color

覆盖 `WP-04`，以及 `WP-09` 的 Track Color slice。主要交付：

- Instrument Catalog 的内置/用户/SF2 profile、导入导出、排序和名称解析；
- Logical Parameter 快捷创建与多 SubVoice 原子 Mapping；
- no-tick0 Lane 创建、Override/Add/Multiply 范围语义与 Properties UI；
- MIDI Track 固定 palette、Properties 与 Duplicate 继承；Logical Track 保留 Definition color 和既有 override。

交付给用户的重点测试清单：

- Catalog 手工编辑、导入/导出、冲突策略、SF2 扫描与 SoundFont 更新后的显示；
- 快捷 Mapping 的单个/全部 SubVoice、缺 Lane 自动添加、Override/Add/Multiply 和失败原子；
- All SubVoices 只冻结命令提交时的当前成员；以后新增 SubVoice 不自动绑定，并验证删除/Undo、保存重开；
- Catalog 名称只影响 UI，不改变 Bank/Program 数值或 canonical fingerprint；
- MIDI 导入、新建、Duplicate 的颜色轮换和 Properties；Logical Track override 不回写共享 Definition。

#### 阶段 4：Humanize、Split、Join 与 Quantize

覆盖 `WP-07`。主要交付：

- 共用受限数字表达式 profile；
- Note Humanize；
- Note Split / Join；
- Note 与 Event Quantize；
- presets、预览、取消、选择结果映射和 compact Undo/Redo。

交付给用户的重点测试清单：

- Humanize 相同 seed 重现、各随机模式、Tick 越 owner 硬边界删除和联合 Tick/Gate 边界；
- Split 三种模式、最大 cuts/results、表达式停止、Direct MIDI 顺序和第一片身份；
- Join gap、Velocity 策略、未选中重叠 Note 和不同 owner；
- Quantize Note/Event 的 grid、tie、Start/Start+End、collision 和拍号变化；
- 三种钢琴卷帘结果一致、混合选择门控、取消、Undo/Redo 和保存重开；
- 1M 对象下的进度、取消、峰值内存/磁盘及结果确定性。

#### 阶段 5：Note/Event Batch Create 与跨类型 Segment 转换

2026-09-06 实施记录：[需求追踪](Midora-Stage5-Generation-and-Segment-Conversion-Requirement-Trace.md)、[架构决定](Midora-Stage5-Generation-and-Segment-Conversion-Architecture-Decisions.md)、[验证与性能报告](Midora-Stage5-Validation-and-Performance-Report.md)、[18 项人工验收清单](Midora-Stage5-Manual-Acceptance-Checklist.md)。用户已确认本阶段验收通过，并显式授权提交推送后进入阶段 6。

覆盖 `WP-08`，以及 `WP-09` 的 Segment conversion slice。两者共用阶段 2/4 已稳定的 detached staging、碰撞归并、结果选择、失败原子和大型结果预算，但在 UI 中仍保持两个独立工具。主要交付：

- Note/Event generator profiles、字段依赖 DAG 与循环拒绝；
- Initial 首对象开关、候选/结果/临时字节预算；
- chunk generation、外部归并、collision reducer、进度、取消和原子提交；
- Help、验证状态与程序级 presets；
- Logical/MIDI Segment 双向 drag/copy/paste、非 Note 数据丢弃摘要、hidden content 扫描、Move rollback 与选择映射。

交付给用户的重点测试清单：

- Note 四字段、Event 两字段的初始值、`i`/`tr`/前后轮变量及依赖顺序；
- Initial 首对象开/关、最大 candidates、最大 relative tick、负值和越界 clamp/delete；
- 自循环、跨字段循环、非法表达式、NaN/Infinity、上限和中途取消；
- 同 start+key Note、同 tick Event、既有 imported duplicate 的精确碰撞结果；
- preset 保存/读取/撞名/版本拒绝；
- 10 万、100 万、1000 万候选的进度、峰值内存/磁盘、Undo/Redo 和重复运行确定性；
- Logical→MIDI、MIDI→Logical，无额外事件、含参数/Channel/Opaque Event、取消确认、Move 目标失败、多混合 Segment、hidden content、重复 Note 与 NoteOff Velocity。

#### 阶段 6：Conductor 编辑器

覆盖 `WP-05`。主要交付：

- Conductor 左侧分页列表、Tempo 阶梯图、自由/直线/水平线编辑、其他正式事件 lane；
- 密集点聚合、tile 左边界状态恢复、标签、Properties、Locate 与选择同步；
- Tempo 及其他正式 Conductor 类型的分页查询和局部失效。

交付给用户的重点测试清单：

- Conductor 的 Tempo step 保持、tick 0、同 tick later-wins、三种画线方式和百万点滚动/缩放；
- Time Signature、Key Signature、Marker、End 的点、标签、列表和 Properties；
- 左侧列表与右侧时间线双向选择、Locate、Ctrl/Shift/拖框；
- 10k/1M Conductor events 的冷页、快速缩放、局部编辑和 Undo/Redo；
- 编译、播放和 MIDI 导出的 Conductor 语义不因 UI 重构改变。

#### 阶段 7：三种钢琴卷帘 Timeline 对象列表与 SubVoice Overlay

覆盖 `WP-06`，以及 `WP-10` 的 Loop/Pre-Roll slice，并完成列表相关右键点、Locate、混合选择及焦点交互。主要交付：

- Logical Segment、MIDI Segment、SubVoice 共用虚拟对象列表；
- 单选、Ctrl、Shift、拖框、Properties、Locate 与选择同步；
- 混合 Note/Event 的共同快捷键门控与 `For Notes` / `For Events` 冻结子集菜单；
- Opaque SysEx/Meta 的只读/可编辑能力门、冷页 exact hit 与 Event Draw 右键 click/drag 状态机；
- SubVoice Pre-Roll 暗区、单端/双端 Loop 标记，以及贯穿 piano/Velocity/Event Lane 的同步竖线。

交付给用户的重点测试清单：

- 三种钢琴卷帘分别验证列表默认隐藏、分页滚动、排序、选择同步、双击 Properties 和 Locate 自动开 Lane；
- Shift 百万范围、Ctrl 稀疏多选、拖框选择和视图内/外同步；
- 混合 Delete，以及不合法 Copy/Cut/Batch 快捷键禁用；
- `For Notes` / `For Events` 只处理菜单打开时冻结的对应子集；
- Opaque SysEx/Meta 的显示、定位、属性和删除边界；
- 右键单击点、右键拖线、冷页命中、Escape/capture-loss，以及弹窗关闭后的快捷键焦点；
- Pre-Roll、单端 Loop、双端 Loop，在不同 DPI/zoom 下的标签与贯穿线像素对齐；Overlay 改动不得让 Note/Event tile 失效。

#### 阶段 8：洋葱皮、All Tracks 与发布前全量回归

2026-09-08 后续确认：Settings 与手选来源弹窗分离，Previous/Next 为仅显示邻居的命令；手选来源独立保留，快捷模式也持久化。Project Format 3 不变，独立 presentation schema 2 读旧 v1 为 custom。All Tracks 标尺/内容单击调用既有 Seek。详细边界和验证见 `Midora-Stage8-Onion-Source-Modes-and-Navigation-2026-09-08.md`。

2026-09-07 实施时，Raw/Compiled、presentation 生命周期和独立有界缓存已落地，8A、8B 自动门通过；当时尚待人工验收。阶段 8 及上述后续细节现已获用户验收并提交、推送。历史测量保留，当前 Compiled 的 MIDI 源音符混合语义及模式持久化以 2026-09-08 修订为准；总体通过不等同于逐项全规模验收或正式发布授权。

覆盖 `WP-10` 的 Onion slice 与 `WP-12`。本阶段设置两个内部自动门：先完成并冻结 Onion 专项测试，再运行发布级全量回归；产品所有者只需做一次合并后的人工验收。主要交付：

- Current Segment Raw onion；
- SubVoice onion；
- All Tracks Raw/Compiled；
- Project presentation 持久化、dormant reference、损坏隔离、compiled stale/source-trace UX；
- 独立只读 overlay cache、轨道分块与局部失效；
- 全量功能、持久化、编译、渲染、音频与性能回归；
- 文档、Help、快捷键说明和最终验收记录。

交付给用户的重点测试清单：

- 当前 Segment、跨 Segment、SubVoice onion 的时间映射、来源筛选、轨道顺序、颜色和只读命中；
- 100/200/400 Track 叠加、快速缩放/滚动、单轨编辑后的局部失效和内存上限；
- All Tracks Raw/Compiled、编译失败或 stale、来源颜色和 Note 配对；
- Track/SubVoice 删除与 Undo、Duplicate、dormant reference 恢复；
- 显式 Save/Save Copy、关闭不提示、保存重开及 presentation 损坏隔离；
- 现有全部编辑命令、Undo/Redo、Full/Incremental equivalence、Format 1/2/3 打开保存、MIDI 导入导出和音频回归；
- 1M/3M/7M/18M 及合成极端项目的启动、滚动、缩放、选择、批量编辑、取消、保存和峰值资源；
- 最终人工创作流程，从新建 Project 到编译、播放、MIDI/音频导出，确认没有被单项自动测试覆盖不到的工作流断裂。

内部门：

1. **8A Feature Complete Gate**：Onion/All Tracks 专项正确性、持久化和性能测试全部通过；
2. **8B Release Regression Gate**：全套自动回归、性能报告和最终用户清单完成。8A 失败时不得开始 8B。

依赖关系：

```text
阶段 1 ─┬─> 阶段 2 ─┬─> 阶段 4 ─> 阶段 5 ─┐
        │           ├─> 阶段 6 ─> 阶段 7 ─┤
        └─> 阶段 3 ────────────────────────┤
                                           └─> 阶段 8
```

正式实施仍按 1→8 串行推进，避免并行阶段同时改动同一正式模型；依赖图只用于判断某阶段验收失败时必须暂停哪些后续工作。

### WP-00：SRS、ADR 与 Format 3 正式化

- 把第 9 章全部已定案结果正式写入规格；
- 更新 SRS 04/06/09/11/16/17/18/20/22/24；
- 新增 Portable Program-Root Storage ADR；
- 新增 Paged Selection/Edit Transaction ADR；
- 新增 Project Presentation/Onion ADR；
- 洋葱皮 presentation 已确认新增持久语义，因此冻结 Format 3 schema/descriptor/migration；Logical Track override 保持不变，Pure MIDI Track 赋色和跨类型转换本身不触发格式升级；
- 正式化旧格式原路径升级保存：打开阶段只读、确认、永久原字节副本、源 identity/lease、原子替换、失败恢复及 Save Copy 边界。

### WP-01：性能基础设施（无新可见 UI）

- `ITimelineObjectSource`；
- ordinal page/query index；
- compressed selection；
- detached paged edit transaction；
- compact undo；
- source-trace 查询；
- 性能与内存基准。

这是 R-04～R-08、R-21、R-22 的前置，不能跳过。

### WP-02：独立缺陷与交互基础

- 黑键 velocity；
- Note creation delta Snap；
- Segment resize vector preview；
- Velocity onset marker；
- Event right-click click/drag 状态机；
- modal focus restore；
- Initial State clamp；
- 新 Event Instrument 无声根因修复。

### WP-03：Portable Program-Root Storage

- ProgramRoot 解析与启动写入门；
- `Data\` / `.tmp\` store abstraction 与各 store 重定位；
- 删除旧 `%LOCALAPPDATA%\Midora` 探测/迁移分支，验证不会访问旧位置；
- `.tmp` manifest、lease、回收与 IPC allow-root；
- 发布/覆盖升级不得携带或覆盖用户 `Data\`；
- fault injection。

### WP-04：Instrument Catalog 与快捷 Parameter Mapping

- catalog schema/store/import/export/editor；
- GM/profile display resolver；
- explicit SF2 scan；
- quick mapping atomic command/UI；
- no-tick0 lane creation tests。

### WP-05：Conductor 查询与编辑器重构

- paged Conductor source；
- left owner-data list；
- tempo step raster；
- dense LOD；
- labels/properties/selection/locate。

### WP-06：三种钢琴卷帘数据列表

- shared pane；
- Logical/Direct/Template adapters；
- range selection；
- mixed menus；
- Locate；
- million-object tests。

### WP-07：共用表达式与 Note/Event 编辑工具

- bounded expression core；
- Note/Event Quantize；
- Join；
- Humanize；
- Split；
- presets；
- selection result mapping。

### WP-08：Batch Create

- Note/Event generator profiles；
- dependency DAG；
- chunk generation/external merge；
- progress/cancel/atomic commit；
- presets；
- 1M/10M candidate tests。

### WP-09：Format 3、Track Color 与跨类型 Segment

- 实施 Format 1/2 -> 3 detached presentation migration；
- 实施迁移 Project 的原路径升级确认、自动永久旧版副本和原子发布状态机；
- MIDI Track palette；
- Logical Track 既有 override 回归（不迁移）；
- `SegmentConversionService`；
- drag/copy/paste adapters；
- data-loss preview/confirmation。

### WP-10：Semantic Overlay 与洋葱皮

顺序：

1. Loop/Pre-Roll overlay；
2. Current Segment Raw onion；
3. SubVoice onion；
4. All Tracks Raw；
5. All Tracks Compiled + source trace/stale UX。

### WP-11：Selection 浮动工具框与右键双击

- shared overlay；
- command adapters；
- selection metrics；
- 默认 Follow、Pin 与手动位置；
- Note/Arrangement Segment 双边 Resize，能力受限的 Ctrl Copy+Move；
- 全规模有效 delta，以及复用普通 Draw 的小选择矢量/大选择 raster tile 预览；
- 采用固定 `[0, 300 ms)`、轴向 `6 DIP` 且不依赖系统设置的全时间线右键双击/延迟菜单状态机。

### WP-12：全量回归与发布前验收

- Full/Incremental equivalence；
- persistence/migration golden；
- UI interaction matrix；
- 18M sample and synthetic extremes；
- audio regression（不主动改音频语义）。

## 7. 测试与验收门

## 7.1 正确性

- 三种 Note 与两类 Numeric Point 的全部新命令（含 Event Quantize）具备 scalar oracle、bulk 结果、Undo/Redo 一致；
- Note/Event collision 精确遵守 INV-077；
- imported duplicates 未被命中的保持不变；
- Project revision 变化、取消、磁盘满、异常均失败原子；
- Full/Incremental 对同输入完全等价；
- 跨类型 Segment 的丢弃清单、确认、Move rollback 与 selection 恢复；
- 洋葱皮、颜色、目录名称不改变 canonical fingerprint；
- Compiled overlay 的逻辑展开只消费 canonical，不重解释乐器；按 2026-09-08 决定，Pure MIDI 直接复用源音符的只读显示；
- Format 1/2 -> 3 presentation migration golden、deterministic save、corruption isolation；
- 迁移后原路径 Save：确认取消、当前格式构建失败、源 identity 改变、backup name collision、备份失败、publish 失败、崩溃恢复、成功状态切换和第二次普通 Save；
- 自动旧版副本与升级前来源逐字节一致，且升级后的原路径严格重开为当前 Format；Save Copy 不清除 migration-dirty，也不替代或绕过原路径升级事务。

## 7.2 表达式安全

- 8,192 scalar、512 node、64 depth 边界；
- 拒绝 assignment、lambda、new、数组、索引、语句、循环、任意 API；
- result self-cycle、两项循环、深层循环；
- NaN、Infinity、除零、checked overflow；
- profile 不存在变量；
- `Math.` 与隐式 `System.Math` 固定白名单；
- preset 作为不可信输入重新完整验证；
- 编译一次、重复求值确定。

## 7.3 性能与内存

基线样本至少包含现有 18M Note MIDI，并增加合成极端：

- Conductor：10k、1M 事件；
- 列表：1M、10M 混合对象；
- Shift 一次选择 1M rows；
- Humanize/Note Quantize/Join/Split：1M Note；Event Quantize：1M、10M points；
- Generator：1M、10M candidates；
- Onion：100/200/400 Tracks；
- Track colors/import：400 Tracks；
- Portable program root：只读根、确认不访问旧数据位置、`.tmp` 回收、磁盘满、reparse point 越界、多 ProgramRoot 并行实例。

强制门：

- 隐藏列表不得产生全量工作；
- 打开列表只加载 visible + bounded overscan；
- UI 帧内不全量排序或枚举百万对象；
- 大型选择不按对象数线性增加 ID set；
- 长操作可取消，但提交原子；
- 常驻内存由 page/chunk/tile budget 限制；
- 冷页/后台 raster 不阻塞 UI；
- 选择或 presentation 变化只局部失效；
- 不为提高内存表现而退化已经验收的编辑速度。

## 7.4 UI 状态机

- right click / jitter / drag / Shift+right drag / double click 全组合；
- modal OK/Cancel/close 后焦点；
- mixed selection keyboard gate 与 typed submenu；
- list Locate 自动 lane；
- toolbox pin/follow/manual drag；
- black/white key top/middle/bottom velocity；
- low zoom Velocity marker；
- Loop/Pre-Roll 在不同 DPI/zoom 下的 device-pixel 对齐；
- Note create `L0=49, step=192`：49、241、433、625，向左仍为 49。

## 8. 主要风险

| 风险 | 后果 | 控制方式 |
|---|---|---|
| 只虚拟化 UI，不改选择/命令 | 百万选择仍爆内存 | WP-01 必须先完成 |
| 为每个新工具复制编辑代码 | 三种钢琴卷帘行为分叉 | shared profile + shared paged transaction |
| Generator 允许接近 `int.MaxValue` 且内存构建 | 进程 OOM/磁盘失控 | 产品硬上限、chunk、预估、取消、临时页 |
| Right click 与 double click 同区零延迟或依赖系统时间 | 菜单抢捕获、跨机器行为不一致或双击失效 | 固定 300 ms / 轴向 6 DIP 自有状态机（UI-08） |
| 洋葱皮混入正式 note snapshot | 命中/选择/cache fingerprint 污染 | 独立只读 overlay layer |
| 跨类型 Move 先删源 | 验证失败造成数据丢失 | detached target first, atomic commit |
| SF2 扫描绑定 Worker | 设置/播放与目录编辑互相阻塞 | explicit metadata-only scan |
| 自动把 Logical override 写回 Definition | 改变共享轨道颜色 | 保持既有 override，完全不迁移或回写 |
| 首次无声靠 full compile/重试掩盖 | 隐藏 dirty/cache bug | 从首个 revision 丢失点修复 |
| 把迁移源保护直接删除 | 可在无备份或源已被外部修改时覆盖用户旧文件 | 专用 Upgrade-In-Place 状态机、原字节永久副本、identity/lease 与失败原子 |

## 9. 产品决策记录（已全部定案）

本章中的 ID 是稳定讨论编号。**9.0 的决策记录优先于后面保留的候选说明**；候选说明继续保留用于追踪取舍，不再表示待决。两轮产品决定与后续迁移保存补充均登记于 2026-08-31。

### 9.0 第一轮已登记决定

需求调整：

- Quantize 同时支持 Note 与 Event；Event 只改变 Tick，首版只覆盖三种 piano roll 的正式数值事件点；
- 撤回“Logical Track 只能显示 Definition 颜色”，保留现行 Logical Track 可选颜色 override；
- Batch Create 同时提供“先用 Initial 创建首对象”和“先执行表达式”的方式，以 Preset 字段保存，默认关闭；
- Onion 及未来 Project view state 不进入 Undo/Redo，也不设置音乐 Project Modified。

| 决策组 | 已登记结果 | 状态 |
|---|---|---|
| `STO-01..08` | 原可移动 Data Root / bootstrap 方案整体退役；改为 `<ProgramRoot>\Data` 与 `<ProgramRoot>\.tmp` | 已定案 |
| `CAT-01..09` | 全部采用原推荐选项 | 已定案 |
| `MAP-01..09` | 全部采用原推荐选项 | 已定案 |
| `UI-01,04..07,09..10` | 采用原推荐选项 | 已定案 |
| `UI-02` | A：Tempo 保留 Event Lane 同款自由画线、直线与 `y=k` 线；用户可通过 Snap 控制密度 | 已定案 |
| `UI-03` | A：完整沿用 Event Lane 修饰键与选择/拖动语义 | 已定案 |
| `UI-08` | A（后续细化）：所有时间线右键菜单等待固定 300 ms；双击使用 `[0, 300 ms)` 与水平、垂直位移分别不超过 `6 DIP` 的自有判定，不依赖系统设置 | 已定案 |
| `EDIT-01` | A：Humanize 首版只做 Tick/Gate/Velocity | 已定案 |
| `EDIT-10` | C：Note Quantize 提供 Start only / Start+End | 已定案 |
| `EDIT-14` | 两种均提供；新增默认关闭且进入 Preset 的首对象复选框 | 已定案 |
| 其余 `EDIT-*` | 采用原推荐选项；`HUM-01=B` 覆盖 `EDIT-03` 的 Tick 边界处理 | 已定案 |
| `COLOR-01` | 原需求撤回；保留 Logical Track override，不迁移 | 已定案并退役该问题 |
| `COLOR-02` | B：Pure MIDI Track 按全局 Arrangement 顺序轮换，Duplicate 继承 | 已定案 |
| `ONION-01,03..09` | 采用原推荐选项 | 已定案 |
| `ONION-02` | 不进 Undo、不设置 Modified；仅随显式 Save/Save Copy 写入 | 已定案 |
| `SEG-01..06` | 全部采用原推荐选项 | 已定案 |
| `TOOL-01..03` | 全部采用原推荐选项 | 已定案 |
| `PORT-01,02,04,05` | 采用各自推荐 A | 已定案 |
| `PORT-03` | B：完全不检测旧 `%LOCALAPPDATA%\Midora` | 已定案；目前无用户数据需要迁移 |
| `HUM-01` | B：Tick 越过 owner 硬边界时删除 Note；Gate/Velocity 仍 clamp | 已定案 |
| `QTZ-01` | A：只量化三种 piano roll 的正式数值事件点 | 已定案 |
| `GEN-01` | A：Initial 首对象为 `i=0` 且计入 Maximum，表达式从 `i=1` 开始 | 已定案 |
| `VIEW-01..03` | 全部采用各自推荐 A；以后新增 view state 同样损坏隔离 | 已定案 |

### 9.1 Portable Program-Root Storage（全部已定案）

旧 `STO-01..08` 不再适用。以下失败、介质和实例边界均已定案。

#### PORT-01：程序根不可写或不满足原子写/锁能力时（已定案 A）

- A：显示明确错误与目录要求，然后拒绝进入主程序；不提供只读工程模式，不 fallback。
- B：进入只读修复模式，允许浏览 Project，但禁用所有可能写 `Data/.tmp` 的功能。
- **推荐：A。** B 会给几乎每个任务增加只读分支，且大型 Project 打开本身就需要 SessionContent；它不是一个真正可用的只读模式。

#### PORT-02：日志、崩溃报告等诊断文件放在哪里（已定案 A）

- A：放 `Data\Diagnostics`，采用明确数量/总字节上限，跨启动保留，用户可直接查看和删除。
- B：放 `.tmp\Diagnostics`，按缓存清理，异常退出信息可能较快丢失。
- **推荐：A。** 诊断不是可重建缓存；有界保留仍符合“可见、可管理、都在程序根目录”。

#### PORT-03：发现旧 `%LOCALAPPDATA%\Midora` 数据时（已定案 B）

- A：显式提示“复制正式设置/目录/预设”或“忽略”；只复制，不自动删除旧数据；AudioCache/SessionContent 不迁移。
- B：完全不检测旧位置，用户手工搬运。
- C：自动移动并删除旧位置。
- **已定案：B。** 当前没有正式用户数据需要迁移；新版完全不检测旧位置，避免为不存在的迁移需求保留复杂路径。

#### PORT-04：正式支持的程序目录介质（已定案 A）

- A：首版只正式支持通过能力探测的本机可写固定卷；拒绝 UNC/network 与可移动介质，并在原子替换、锁或 free-space 查询不可靠时明确失败。
- B：任意可写路径，包括 UNC/network share。
- **推荐：A。** 用户放到本机 D 盘可正常工作；网络/可移动介质的断连、锁与 rename 语义不应在首版静默承担。

#### PORT-05：多份 portable 副本的单实例身份（已定案 A）

- A：单实例身份加入“当前用户 + 规范化 ProgramRoot”；同一根只允许一个 writer，不同根可各自运行并使用各自 Data/.tmp。
- B：继续全局只有一个 Midora 实例，第二份 portable 的请求转发给第一份，即使两者根目录不同。
- **推荐：A。** B 会让用户从第二份副本启动，却实际读写第一份副本的数据根，直接违背可见目录模型。

### 9.2 Instrument Catalog（`CAT-01..09` 已定案为各自推荐项）

#### CAT-01：产品名称与“标准”表述

- A：称 `Standard Bank Names`。
- B：称 `Instrument Catalogs`，每项显示来源；GM 只作为一个内置 Profile。
- **推荐：B。** 不制造不存在的全局标准。

#### CAT-02：Profile 作用域

- A：只有一个全局合并表。
- B：多个有序、可启用 Profile；解析时按优先级叠加。
- **推荐：B。** 能容纳 GM/GS/XG、多个 SF2 与用户命名。

#### CAT-03：名称冲突优先级

- A：最后导入者胜。
- B：用户 override > 当前 SoundFont 顺序 > 用户 Profile 顺序 > 内置。
- C：每次冲突弹窗选择。
- **推荐：B。** 确定且符合现有 SoundFont 优先顺序。

#### CAT-04：从 SF2 读取的实现

- A：用户显式、独立解析 `phdr` 元数据。
- B：通过 BASSMIDI/Worker 查询。
- C：设置 Apply 时自动扫描。
- **推荐：A。** 不把目录编辑和音频生命周期耦合；C 会重新引入大文件操作延迟。

#### CAT-05：SF2 更新后的目录

- A：自动监控并刷新。
- B：保持导入快照，用户显式 Rescan。
- **推荐：B。** 名称是程序级辅助数据，不承担资源身份验证。

#### CAT-06：导入冲突策略

- A：拒绝整个导入。
- B：Replace Profile / Merge With Preview 两种显式选择。
- **推荐：B。** Merge 仍必须在提交前列出覆盖项，一次原子写入。

#### CAT-07：SF2 `bank` 字段如何投影到 MIDI MSB/LSB

- A：固定沿用当前 Midora/BASSMIDI 语义：SF2 bank 对应 Bank MSB，Bank LSB=0。
- B：导入时提供可选转换 profile，并在预览中展示最终 MSB/LSB。
- **推荐：B；只有 raw bank 0..127 时默认 profile 才使用 A。** bank 128 与更大值交给 `CAT-09`；不同工具可能用不同方式解释扩展 bank，不能在无预览时静默猜测。

#### CAT-08：Imported SF2 Profile 如何关联 SoundFont

- A：只保存路径。
- B：为程序级 SoundFont 列表项增加稳定 `SoundFontEntryId`，Profile 可绑定它；disabled 项不参与播放上下文名称解析。
- C：Profile 永远独立，不按 SoundFont 顺序解析。
- **推荐：B。** 路径和 ordinal 都不是稳定身份，同时保留独立用户 Profile。

#### CAT-09：SF2 bank 128 与 >127

- A：统一拒绝这些 preset。
- B：扫描快照保留原始 ushort bank；导入向 MIDI triple 时要求显式 profile/映射，bank 128 可在预览中标记常见 percussion 用途但不自动冒充 MIDI MSB 128。
- **推荐：B。** MIDI MSB/LSB 只有 0..127，不能截断或无条件映射。

### 9.3 快捷 Logical Parameter 映射（`MAP-01..09` 已定案为各自推荐项）

#### MAP-01：一个参数绑定多个 SubVoice 的正式表示

- A：一个共享 Mapping 对象持有多个目标。
- B：一个 Logical Parameter + 每个 SubVoice 一个正式 Parameter Mapping。
- **推荐：B。** 复用现有 owner、排序、诊断和删除语义。

#### MAP-02：已有同目标 Mapping 时

- A：静默追加。
- B：静默替换。
- C：对话框显示已有顺序，选择 Append / Replace / Cancel。
- **推荐：C。** Add/Multiply 的顺序会改变结果，不能静默决定。

#### MAP-03：Multiply 的自定义范围

- A：参数直接作为乘数 `c*x`。
- B：先把 source range 映射到用户指定 factor range，再 `c*factor`。
- **推荐：B。** 对 CC 0..127 更可用，也避免默认把值放大 127 倍。

#### MAP-04：目标 SubVoice 部分失败

- A：成功的先创建，其余警告。
- B：整批失败，不创建 Parameter 或任何 Mapping。
- **推荐：B。** 保持 Definition 编辑原子性。

#### MAP-05：“自动添加 Lane”的数据语义

- A：在 tick 0 写默认事件点。
- B：只创建空 UI/owner 入口和必要 Mapping Chain，不创建事件点。
- **推荐：B。** 延续当前“空入口”规则。

#### MAP-06：`All SubVoices` 是否动态包含以后新增的 SubVoice

- A：命令执行时冻结当前 SubVoice 列表，未来新增者不自动绑定。
- B：保存一个动态组规则，未来新增 SubVoice 自动获得 Mapping。
- **推荐：A。** B 会引入全新的持久 owner/group 语义，并使后续新增操作带隐式副作用。

#### MAP-07：Add 的默认 offset range

- A：沿用目标完整范围，例如 CC 为 0..127。
- B：使用双极小范围，例如 CC 默认 -127..127，UI 明确显示并允许改写。
- C：不设默认，用户必须输入。
- **推荐：B。** Add 使用单极 0..127 只能增加，通常不符合调制预期；具体默认值仍应在定案时冻结。

#### MAP-08：快捷入口是否扩展 Logical Parameter 类型

- A：首版保持 Integer，范围/default 由所选 operation/target 决定。
- B：同时允许 Double/Enum。
- **推荐：A。** B 超出现有 shortcut 能力；可在普通 Properties 中继续创建高级参数。

#### MAP-09：新参数的中性 default

- A：所有模式都使用目标当前 default。
- B：Override=目标 default，Add=0，Multiply=1。
- **推荐：B。** 创建 Mapping 但尚未绘制参数点时不应立即改变声音。

### 9.4 Conductor 与事件数据列表（`UI-01..10` 已定案）

#### UI-01：Conductor 的 Meta 范围

- A：只包含正式 Tempo、Time Signature、Key Signature、Marker、Project End。
- B：把任意 Opaque SMF Meta 也提升到 Conductor 编辑器。
- **推荐：A。** B 是 Domain 与导入 round-trip 的另一项大需求。

#### UI-02：Tempo 是否允许自由画线（已定案 A）

- A：完全复制 Event Lane 左/右键画线。
- B：只提供点创建、移动、选择、属性与批量编辑。
- **已定案：A。** 保留自由画线、直线与 `y=k` 线；Snap 决定插点间隔，关闭 Snap 时沿用现有 Event Lane 的逐 tick 插点语义。渲染仍按状态保持的阶梯图显示，不把相邻状态解释成线性 Tempo ramp。

#### UI-03：Tempo 修饰键（已定案 A）

- A：Shift 固定 tick 只改 BPM；Ctrl 拖动复制；Alt 沿用绘制语义。
- B：仅普通拖动，不加修饰键。
- **已定案：A。** 完整沿用 Event Lane 当前有效的修饰键和画线状态机，不再排除 Alt 绘制语义。

#### UI-04：列表中的 Note 是一行还是 NoteOn/NoteOff 两行

- A：一条 Note 一行，显示 start/end/gate。
- B：拆为两条 MIDI 风格事件行。
- **推荐：A。** 正式可编辑对象是 Note；B 会造成双选择和双删除歧义。

#### UI-05：Opaque SysEx/Meta 是否显示在 MIDI Segment 列表

- A：不显示。
- B：显示并支持 Locate/Properties/Delete，不支持数值 Batch Edit。
- **推荐：B。** 列表才真正覆盖“全部事件”，同时不虚构数值语义。

#### UI-06：列表宽度/显示状态保存在哪里

- A：Project。
- B：session/workspace state，默认隐藏。
- C：完全不保存。
- **推荐：B。** 不应为普通面板状态升级 Project Format。

#### UI-07：混合选择 typed submenu

- A：坚持当前“只显示共同命令”，不提供子菜单。
- B：允许明确的 `For Notes >` / `For Events >`，菜单打开时冻结各子集。
- **推荐：B。** 这是显式目标选择，不属于静默跳过；需要修改 SRS §20.7.7。

#### UI-08：右键双击与单击菜单冲突（已定案 A）

- A：所有时间线右键菜单使用同一个显式双击候选窗口，以支持任意合法内容位置双击。
- B：右键双击只在空白可编辑区生效；对象上单击菜单不延迟。
- C：取消右键双击快捷方式。
- **已定案：A，并于 2026-09-01 细化。** 首个未拖动 Right Up 到第二个 Right Down 的单调时间差必须位于 `[0, 300 ms)`，两位置必须同时满足 `|dx| <= 6 DIP` 与 `|dy| <= 6 DIP`；单击菜单等待同一 300 ms。必须用统一自有手势状态机实现，禁止依赖 WPF `ClickCount`、Windows double-click time 或系统空间容差，也不能由各视图自行设定阈值。

#### UI-09：Loop/Pre-Roll 标签位置

- A：只在顶部 piano ruler 显示文字，竖线贯穿所有 panel。
- B：每个 lane 都重复显示文字。
- **推荐：A。** 减少遮挡。

#### UI-10：Initial State Clamp 的反馈

- A：静默改为 clamp 后值。
- B：clamp 并短暂显示非阻塞说明；不产生 Error。
- C：保持拒绝。
- **推荐：B。** 符合“自动 clamp”，同时避免用户不知道输入被改写。

### 9.5 Note/Event 编辑工具与 Generator（全部已定案）

#### EDIT-01：Humanize 是否包含 Key

- A：只做 Tick/Gate/Velocity。
- B：也允许 Key。
- **推荐：A。** Key 随机化更像生成/作曲，且会引入越界删除；以后可独立增加。

#### EDIT-02：Humanize 是否公开 Seed

- A：只用随机 seed。
- B：`Auto` + 可输入显式 seed。
- **推荐：B。** 便于复现黑乐谱生成结果；Undo/Redo 始终冻结结果。

#### EDIT-03：Humanize Tick 越界（由后续 `HUM-01=B` 最终覆盖为 B）

- A：clamp 到 owner 合法边界。
- B：删除越界 Note。
- C：整批失败。
- **最终定案：B。** 该结果由后续 `HUM-01` 明确覆盖原推荐。

#### EDIT-04：Split 是全局刀线还是每 Note 独立

- A：每个 owner 以选区左右边界生成一组全局刀线。
- B：每条 Note 从自己的 start 重新运行模式/表达式。
- **推荐：A。** 与用户定义的 `tr=选区最左侧` 一致，也适合图案式切割。

#### EDIT-05：Maximum Piece Count 的作用域

- A：整个选区最多 N 块区间。
- B：每条 Note 最多 N 片。
- **推荐：A，与 EDIT-04 配套。**

#### EDIT-06：`i` 从 0 还是 1 开始

- A：0-based。
- B：1-based。
- **推荐：A。** 周期函数和数组式公式更自然，但 Help 必须明确。

#### EDIT-07：Direct Note Split 的 NoteOff Velocity

- A：每个片段都继承源 NoteOff velocity。
- B：只有最后片继承，其余使用 0。
- **推荐：A。** 每个片段是独立 retrigger Note，完整继承最直观。

#### EDIT-08：Join 是否跨真实空隙

- A：只合并 overlap/touching。
- B：所有选中的同 key Note 一律填平到一个 Note。
- C：提供 Maximum Gap Ticks，默认 0。
- **推荐：C。** 同时支持严格结合和有意填平小间隙。

#### EDIT-09：Join 后 velocity

- A：NoteOn 取第一条，Direct NoteOff 取最后条。
- B：平均值。
- C：最大值。
- **推荐：A。** 保留 run 的起始 attack 与最终 release 信息。

#### EDIT-10：Quantize 作用范围

- A：只量化 start，Gate 不变。
- B：start 与 end 分别量化，重算 Gate。
- C：对话框提供两种模式。
- **推荐：C，默认 A。** 增量复杂度不高，避免很快再次扩展 UI。

#### EDIT-11：Quantize 是否加入 Strength

- A：固定 100%。
- B：0..100% Strength。
- **推荐：A 作为首版。** Humanize 已承担非严格时间变化；若选 B 需冻结取整规则。

#### EDIT-12：Quantize 的 Bar 选项

- A：三种视图都不提供 Bar，只提供分数和 ticks。
- B：Segment 提供 Bar，SubVoice 不提供。
- C：三种都提供，SubVoice 以 Project tick 0/当前 TPQN 推导。
- **推荐：A。** SubVoice 模板没有完整 Project Time Signature 上下文。

#### EDIT-13：Quantize 正中 tie-break

- A：较早格点。
- B：较晚格点（AwayFromZero）。
- **推荐：A。** Quantize 没有鼠标 movement direction，不能照搬依赖拖动方向的 Snap 分支；必须显式冻结 tie-break。

#### EDIT-14：Generator 第一个对象（已定案为两种均提供）

- 新增 `Create first object from initial values` 复选框，默认关闭并保存进 Preset；
- 关闭：第 0 轮先运行表达式，Initial 只作为 `*0`；
- 开启：Initial 直接创建首对象，表达式从后续对象开始；
- Initial 首对象消耗 candidate `i=0` 并计入 Maximum；后续表达式从 `i=1` 开始。

#### EDIT-15：Generator 的 `tr`

- A：等于本轮输入 `t0`（上一轮/Initial 的相对 tick）。
- B：等于本轮输出 `t1`。
- C：另定义为 `t1 - firstTick`。
- **推荐：A。** 无循环依赖，且与“上次迭代值”一致。

#### EDIT-16：Maximum Objects 的计数

- A：表达式候选/迭代数。
- B：碰撞后最终保留数。
- **推荐：A。** B 在恒定 Tick 表达式下可能永不停止。

#### EDIT-17：Maximum Candidates / Cuts 的硬上限

- A：允许到 `int.MaxValue`。
- B：首版固定 16,777,216（2^24），以后按格式/性能门提升。
- C：无产品硬上限，只按磁盘预算动态拒绝。
- **推荐：B。** `int.MaxValue` 个对象并非实际可安全承诺的初版能力；默认仍为 65,535。

#### EDIT-18：Generator 负相对 Tick

- A：clamp 到 0。
- B：跳过本候选继续。
- C：整批失败。
- **推荐：A。** 与原需求“统一 clamp”一致；碰撞归并会处理边界堆积。

#### EDIT-19：Event Generator 是否覆盖 Logical Parameter Lane

- A：只支持 MIDI Event。
- B：MIDI Event 与 Logical Parameter 都支持，并按 Definition 类型归一化。
- **推荐：B。** 与三种钢琴卷帘统一目标一致。

#### EDIT-20：Preset 是否保存目标 Lane

- A：保存并在其他项目按 ID 查找。
- B：不保存，应用到当前 active lane。
- **推荐：B。** Preset 是程序级且不应依赖 Project stable ID。

#### EDIT-21：Preset 是否保存 Maximum/Max Tick

- A：保存表达式和 Initial，但不保存限制。
- B：一并保存 Maximum 与可选 max relative tick；不保存 Base Tick。
- **推荐：B。** 限制属于生成算法的一部分。

#### EDIT-22：大型编辑是否必须成为可取消前台任务

- A：同步阻塞直到完成。
- B：detached 可取消任务，成功后瞬时原子提交。
- **推荐：B。** 这是千万对象下保持 UI 响应与失败原子的必要条件。

#### EDIT-23：Note Creation 正向 delta 的第一步

- A：只要越过 drag threshold，长度从 `L0` 立即变为 `L0 + step`。
- B：鼠标实际 delta 达到完整 step 才变化。
- **推荐：A。** 符合原示例 49 -> 241；预览与提交必须相同。

#### EDIT-24：Operation=`Bar` 时 Note Creation delta

- A：每一步从上一步 end 开始增加该位置拍号定义的完整一小节时长；不把 end 对齐到 barline。
- B：把 end 吸附到下一个实际 barline。
- C：Note Creation 不提供 Bar，只接受固定 tick subdivision。
- **推荐：A。** 这才保持“对 delta 增加一个操作单位”的语义；B 在初始 end 未对齐时只是补齐残余小节。

#### EDIT-25：Split/Join 后选择

- A：选择所有最终保留结果；Undo 恢复原选择。
- B：保持原 ID 能对应的第一片/第一条。
- **推荐：A。** 便于连续操作，也与批量命令结果选择一致。

#### EDIT-26：Humanize Multiply 是否允许负 factor

- A：允许任意 finite min/max，最终再 clamp。
- B：factor range 必须 `>=0`，允许 0；负数验证失败。
- **推荐：B。** 负 Gate/Velocity factor 不具备清晰的“类人化”意义，且会把大量值压到下界。

#### EDIT-27：Humanize Tick 超出 owner 可表示范围时是否扩展容器

- A：自动扩展 Segment 暴露窗口或 SubVoice Template Length。
- B：不扩展；Segment 只按内容模型的硬边界 clamp，允许落在 crop 外；SubVoice clamp 到当前模板合法范围。
- C：整批失败。
- **推荐：B。** Humanize 不应暗中改变 Segment/Template 结构。

#### EDIT-28：Split 后哪个片段保留原 Stable ID

- A：按时间最早的第一片保留，后续片分配新 ID。
- B：所有片段都分配新 ID。
- **推荐：A。** 保留来源诊断和 selection trace，同时结果映射仍明确。

#### EDIT-29：Quantize Start+End 后 `end <= start`

- A：把 end 设为 `start+1`。
- B：保持原 Gate，从量化后的 start 重新计算 end。
- C：整批失败。
- **推荐：A。** 与 Note 最小 1 tick 及结果 clamp 的既有方向一致；预览必须显示该结果。

#### EDIT-30：Generator `tr` 的首轮定义

- A：`tr=t0`，坐标原点 Base Tick 的 tr=0；若 Initial Tick 非 0，则第一个候选输入的 tr 自然非 0。
- B：首轮强制 tr=0，后续才等于上一轮相对 Tick。
- C：删除 tr，只保留 t0/t1。
- **推荐：A。** “基准处 tr=0”是坐标系定义，不代表每个配置下首个候选都位于基准。

#### EDIT-31：Split/Generator 是否采用独立结果与 staging 预算

- A：只限制 Cuts/Candidates。
- B：另设 Maximum Result Objects、staging RAM bytes、staging disk bytes 和剩余空间门；具体数值经基准测试后冻结。
- **推荐：B。** Cuts/Candidates 不能约束一刀切开大量重叠 Note 的输出规模，也不能防止临时磁盘写满。

### 9.6 跨类型 Segment 与颜色（全部已定案）

#### SEG-01：跨类型转换是否扫描 hidden content（已定案 B）

- A：只转换当前暴露范围。
- B：转换 Segment 内全部 Note；全部非 Note 数据计入丢弃警告。
- **推荐：B。** Segment 移动/复制不能静默丢失未暴露对象。

#### SEG-02：Logical -> MIDI 的 NoteOff velocity（已定案 A）

- A：0（沿用当前跨类型 Note clipboard）。
- B：64。
- C：在确认对话框选择。
- **推荐：A。** 保持既有共同字段转换；若改 B，应同时改所有跨类型 Note 入口。

#### SEG-03：有非 Note 数据时的行为（已定案 B）

- A：一律拒绝转换。
- B：显示类型和数量，确认后丢弃并只转换 Note。
- **推荐：B。** 符合原需求；Move 必须 target-first 原子提交。

#### SEG-04：同一次拖动多个混合 Segment（已定案 A）

- A：允许，统一预览一次丢弃清单，全部成功或全部失败。
- B：首版只允许单 Segment。
- **推荐：A。** 现有 Arrangement 已支持混合 Segment 批量编辑，限制单项会不一致。

#### SEG-05：导入形成的同 start+key 重复 Direct Note 转为 Logical 时（已定案 B）

- A：全部保留，即使目标 Logical 模型的编辑碰撞规则不允许。
- B：按确定 source order 保留第一条，其余计入转换丢弃摘要。
- C：存在重复就拒绝整个转换。
- **推荐：B。** Segment 转换是一次新编辑；它不能把不满足目标编辑契约的新重复对象写进去，但必须明确报告丢弃数量。

#### SEG-06：Direct NoteOff velocity 的警告（已定案 B）

- A：任何 MIDI -> Logical 转换都固定警告。
- B：只要存在至少一个非零 NoteOff velocity 就把数量列入数据丢失确认；全为 0 时不单独警告该字段。
- C：从不提示。
- **推荐：B。** 避免无信息损失时频繁弹窗，同时不静默丢失有意义的数据。

#### COLOR-01：旧 Logical Track Color Override 的 Format 3 迁移（已退役）

产品所有者撤回删除 override 的需求。Logical Track 继续允许并显示现有颜色 override；不需要迁移、不丢弃字段、不写回 Definition，也不因颜色升级 Project Format。

#### COLOR-02：Pure MIDI Track palette 分配

- A：按每个 Root 内轮换。
- B：按全局 Arrangement 顺序轮换；Duplicate 继承源色。
- C：随机。
- **推荐：B。** 确定、跨 Fixed/Auto Root 一致。

### 9.7 洋葱皮与浮动工具框（全部已定案）

#### ONION-01：配置持久化粒度

- A：每个 Segment 各存一份来源列表。
- B：每个目标 Track 保存一份 source Track 列表；该 Track 的各 Segment 共用。每个 workspace 只存临时 enable。
- C：Project-wide 只有一份来源列表。
- D：完全 session-only。
- **推荐：B。** 比每 Segment 配置紧凑，又允许不同编曲轨道查看不同参照。

#### ONION-02：Project presentation 修改是否标记 Modified/进入 Undo（已定案）

- 已定案：Presentation/view state 的直接编辑不进入 Undo/Redo，也不设置音乐 Project Modified 或标题星号；
- 必须使用独立的 `PresentationRevision/PresentationDirty`，不得复用音乐 Project revision；
- pending presentation 只随显式 Save/Save Copy 写入；
- 音乐对象删除后引用在会话中 dormant，Undo 恢复同 ID 时重新生效，保存时过滤仍悬空项。

#### ONION-03：来源 Segment hidden content

- A：只显示实际暴露范围。
- B：显示 Segment 内全部隐藏内容。
- **推荐：A。** 与 Arrangement 可听/可编辑范围一致。

#### ONION-04：当前轨道是否可作为自身 onion 来源

- A：允许。
- B：自动排除，当前轨道只由正式层显示。
- **推荐：B。** 避免重复叠色。

#### ONION-05：Compiled 模式遇到失败/过期结果

- A：禁用 Compiled 并清空。
- B：显示最后成功结果，明确标 `Stale`。
- **推荐：B。** 对分析更有用，但绝不能伪装为当前结果。

#### ONION-06：Compiled Note 的颜色来源

- A：按最终 Port/Channel 着色。
- B：扩充 canonical source trace，按来源 Logical/Pure Track 着色。
- **推荐：B。** 用户要求轨道颜色，Port/Channel 无法稳定反推轨道。

#### ONION-07：SubVoice onion 配置

- A：每个 Event Instrument 只有一份 source SubVoice 列表。
- B：每个目标 SubVoice 保存一份 source SubVoice 列表。
- **推荐：B。** 与 Track onion 的“每个目标拥有来源集合”一致，也支持不同 SubVoice 使用不同参照。

#### ONION-08：Duplicate 与引用生命周期

- A：Duplicate 目标 Track/SubVoice 时复制其 onion 来源列表；Definition 深复制时，指向被复制 SubVoice 的内部引用重映射到新 ID。删除时原子清理，Undo 一并恢复。
- B：Duplicate 后 onion 配置为空。
- **推荐：A。** 复制编辑上下文更符合用户预期，同时必须做稳定 ID remap。

#### ONION-09：哪些配置写入 Project

- A：只保存来源 ID 列表和顺序；enable、opacity、Raw/Compiled 都是 session state。
- B：保存来源、顺序、enable、opacity、默认 Raw/Compiled；缩放/滚动仍只属 session。
- C：包括面板宽度、缩放和滚动全部保存。
- **推荐：B。** 原需求称“洋葱皮配置”需要保存，不能静默缩小成只保存来源；C 又会恢复普通 View State 持久化。

#### TOOL-01：工具框 Pin/Follow 的正式语义/命名（已定案 B，2026-09-01 细化默认值）

- 命名固定使用 Pin/Follow，不称 Lock。
- Follow 是新 Timeline Surface 默认：工具框跟随选择包围框并贴 viewport 边界保持可达。
- Pin 使工具框相对时间线世界/Selection 固定，可随视图平移出屏。
- **当前定案：** 保留 Pin 图标/Tooltip 与两种行为，但默认状态由 Pin 改为 Follow；两者都允许 grip 手动移动。

#### TOOL-02：Horizontal Resize 适用对象（2026-09-01 改定 B）

- A：只对 Note。
- B：Note 与 Arrangement Segment；两者均拆成左边界、右边界两个方向可辨按钮；Event Point 不显示。
- **当前定案：B。** Segment 复用已经验收的混合 Logical/MIDI Selection shared-delta、最小长度、边界与 Undo 规则，不另建命令语义。

#### TOOL-03：工具框位置持久化（已定案 B）

- A：Project。
- B：session/workspace state。
- C：每次自动重置。
- **推荐：B。** 不属于音乐或值得共享的 Project presentation。

### 9.8 第二轮新增精确语义（全部已定案）

#### HUM-01：“越界删除即可”具体覆盖什么（已定案 B）

本轮同时给出了“其余 `EDIT-*` 同意推荐”和“越界删除即可”。前者会令 `EDIT-03=A`（Humanize Tick clamp），后者可能表示 `EDIT-03=B`（删除 Note），两者不能同时成立。

- A：该句话只描述未来如果 Humanize 加入 Key 时的 Key 越界；当前 Tick 仍按 `EDIT-03=A` clamp，Gate/Velocity 也 clamp。
- B：Humanize 后只要 Tick 超出 owner 合法硬边界，就删除整条 Note；Gate/Velocity 仍 clamp。
- C：Tick、Gate、Velocity 任一原始计算结果越界都删除整条 Note。
- **已定案：B。** Tick 越过 owner 正式硬边界时删除整条 Note；Gate/Velocity 仍 clamp。

#### QTZ-01：Event Quantize 的“Event”精确范围（已定案 A）

- A：只覆盖三种 piano roll 中有正式数值 lane 的点：Direct MIDI Channel Event、Logical Parameter Point、SubVoice MIDI Event；不含 Opaque SysEx/Meta，也不含 Conductor。
- B：在 A 基础上允许 Opaque SysEx/Meta 做 tick-only quantize；不改 payload。
- C：在 B 基础上也包含 Conductor 的 Tempo/Time Signature/Key Signature/Marker。
- **已定案：A。** Opaque 与 Conductor 不在首版 Event Quantize 范围。

Event Quantize 的其余语义不再另设决策：Value 不变、使用 Note Quantize 同一 grid/tie-break、只有 Tick 模式、同 exact lane key 后来者覆盖、未命中导入重复保留。

#### GEN-01：开启“用 Initial 创建首对象”后的候选编号（已定案 A）

- A：Initial 直接创建 candidate `i=0`，计入 Maximum Candidates；第一次表达式求值使用 `i=1`，并以上一对象 Initial 作为 `*0`。
- B：Initial 对象不占 candidate 编号或 Maximum；第一次表达式仍使用 `i=0`。
- **已定案：A。** 每个实际候选都有唯一连续 i，Maximum 包含 Initial 首对象。

#### VIEW-01：不标 Modified 的 presentation 何时写入 `.midora`（已定案 A）

- A：只随用户显式 Save / Save Copy 写入。Presentation-only 变化不会触发关闭提示；若之后未显式保存就关闭或崩溃，这些变化丢失。
- B：维护独立 dirty，在 idle/close 时静默事务重写整个 `.midora`。
- C：写外部 sidecar，并在以后 Save 时合入 `.midora`。
- **已定案：A。** Presentation 只随显式 Save/Save Copy 写入；允许未保存 view state 在关闭或崩溃时丢失。

Save 开始时必须同时冻结 Project source snapshot 与 presentation snapshot；Save Copy 包含当前 presentation。保存期间又发生的 presentation 变化保留为新的 pending revision，不能被错误视为已保存。

#### VIEW-02：Project presentation 文件损坏时（已定案 A）

- A：隔离 presentation、使用默认视图并报告 Warning；音乐 Project 仍打开，不标 Modified。
- B：把它视为核心 source 损坏并拒绝整个 Project。
- **已定案：A。** 该隔离原则也适用于以后加入的其他 Project view state；视图数据损坏不得阻止音乐内容加载。

#### VIEW-03：音乐对象删除/Undo 与 Onion 引用（已定案 A）

- A：删除 Track/SubVoice 时在会话中把相关引用变成 dormant；Undo 恢复同 stable ID 时自动重新生效；保存时过滤仍悬空的引用，Reader 永不接收悬空 ID。
- B：删除对象时永久清理相关 onion 引用；Undo 音乐对象也不恢复这些引用。
- C：把引用清理/恢复直接附在音乐删除命令的 Undo payload 中。
- **已定案：A。** 会话中保留 dormant 引用，保存边界过滤悬空引用。

### 9.9 旧格式原路径升级保存（已定案）

#### MIG-01：迁移 Project 是否允许覆盖原来源路径（已定案 A）

- A：打开/迁移阶段保持来源只读；普通 Save 显式确认、自动创建可见的旧格式原字节永久副本，再以当前 Format 原子覆盖原路径。
- B：继续永久禁止覆盖来源路径，只允许用户另存当前格式。
- **已定案：A。** 手工 Save Copy 不是前置条件，也不应让后续普通 Save 继续被多余阻止。Save Copy 到其他路径保持当前格式副本语义；只有专用普通 Save 升级事务可覆盖受保护来源。

### 9.10 决策闭合

截至迁移保存补充，本文列出的 91 个稳定决策 ID 均已有确定结果，没有剩余产品决策项。实施前仍需完成第 10 章列出的 SRS、跨系统不变量、ADR 与 Project Format 3 正式化；那是规格落地工作，不是继续默认增加产品范围的授权。

## 10. 定案后的文档修改范围

决策完成后，正式实施前至少需要：

- SRS 04：Conductor 事件列表、Tempo step graph、Meta 边界；
- SRS 06：Instrument Catalog 与显式 SF2 metadata scan；
- SRS 09：快捷 Parameter Mapping 的 operation/range/order；
- SRS 11：跨 Segment 转换、Track Color；
- SRS 16：Format 3、presentation 文件、detached migration、确认后原路径升级与永久旧版副本事务；
- SRS 17：Portable Program-Root Storage、程序目录写入门、列表布局状态；
- SRS 18：Conductor/三种 piano roll 布局、onion、semantic overlay；
- SRS 20：Humanize/Split/Join/Quantize/Generator、typed submenu、右键状态机、selection tool；
- SRS 22：新增跨系统不变量；
- SRS 24：MIDI Track color、all-track overlay、Arrangement conversion；
- ADR：Portable Program-Root Storage、Paged Selection/Edit、Expression Profiles、Project Presentation、Segment Conversion、Project Format Upgrade-In-Place；
- Format schemas/descriptors/goldens：为已确认的 Format 3 Project presentation 新增并冻结。

### 10.1 外部规范依据

- MIDI Association, [General MIDI](https://midi.org/general-midi)：GM Program 命名与 GM1/GM2 的边界；
- MIDI Association, [MIDI 1.0 Control Change Messages](https://midi.org/midi-1-0-control-change-messages)：CC0 与 CC32 的 Bank Select 数值机制；
- E-mu/Creative, [SoundFont Technical Specification](https://www.synthfont.com/SFSPEC21.PDF)：`phdr` 中 preset name、preset number 与 bank number 的元数据结构。

这些资料只用于定义目录来源与 SF2 元数据读取边界；Midora 的正式 Bank/Program MIDI 数值语义仍以项目 SRS 定案为准。

## 11. 结论

这批需求可实施，但不应作为互不相关的小功能逐条堆入现有 UI。最重要的共同基础是：**分页时间线查询、压缩范围选择、流式 detached 编辑事务和受限表达式 profile**。先完成这四项，百万对象列表、Humanize/Split/Generator、洋葱皮和浮动工具框才能同时满足正确性、内存上限和现有大型 MIDI 性能目标。

两轮讨论及后续补充已经定案本文列出的全部产品语义，包括 Portable Program-Root Storage、Catalog、快捷 Mapping、Conductor/事件列表、编辑工具、跨类型 Segment、Track Color、Onion、Presentation 保存、选择浮动工具框，以及旧格式 Project 的原路径安全升级保存。

上述规格/不变量/ADR/Format 3 与八阶段工作包已经落地，不再作为待启动任务。后续六阶段内存优化及独立编译修复也已完成；用户于 2026-09-09 确认下一步顺序为 **成功 Logical 编译结果内存优化 → 极端 Tick 溢出防护（包含高 TPQN 组合边界）→ 新一轮需求**，见 [收尾与下一步](Midora-Pre-Expansion-Closeout-and-Next-Step-2026-09-09.md)。不得因开始新的需求批次而重新开放既有已定案语义或擅自改变功能行为。
