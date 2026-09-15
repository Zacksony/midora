# 第 23 章 Pure MIDI Track 与 Standard MIDI File 导入

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 MIDI Channel Root、Pure MIDI Track、Midi Segment、直接 MIDI 事件、SMF Format 0 / 1 导入、SMF Track 结构保留、Root 级编译/音频/缓存语义，以及它们与现有 Logical Track 主线的边界。本章是 Pure MIDI 数据的专项规范；与旧章节的宽泛 Logical-only 描述冲突时，以本章及第 22 章修订后的不变量为准。

## 23.1 功能范围与主线

Midora 初版正式支持两条并列编曲主线：

```text
Logical Track
→ Logical Segment
→ Logical Note / Logical Parameter
→ Event Instrument expansion
→ canonical MIDI

Pure MIDI Track
→ Midi Segment
→ direct MIDI Note / Channel Event
→ canonical MIDI
```

两条主线必须共同遵守：

```text
Project Source Data
→ Semantic Validation
→ Compilation
→ Canonical Compiled Result
→ Playback / Preview / MIDI Export / Audio Rendering
```

Pure MIDI Track 不绕过 semantic validation 或 canonical；消费者不得直接读取 Midi Segment 重新解释事件、路由、Reset、顺序或 Track 拓扑。

## 23.2 Project 对象关系

Project 的 Pure MIDI Track 位于第 24 章 global mixed Arrangement track list 中：

```text
Project
├─ MIDI Channel Roots (internal, non-empty shared execution identities)
└─ Arrangement Tracks (mixed Logical / Pure MIDI order)
   └─ Pure MIDI Track → Root ID
      └─ Midi Segments
         ├─ Direct MIDI Notes
         ├─ Direct MIDI Channel Events
         └─ Opaque Imported Events
```

`MIDI Channel Root`、`Pure MIDI Track` 和 `Midi Segment` 都必须拥有 Project 内全局唯一的稳定 ID。名称、显示顺序、Port、Channel、tick 和源 MTrk index 都不得替代身份。

## 23.3 MIDI Channel Root

### 23.3.1 定义

MIDI Channel Root 是一组 Pure MIDI Track 的必选内部共享执行身份。它不占 Arrangement 行，也不保存 Note、普通 Channel Event 或 Segment；它定义这些成员 Track 共享的：

```text
Channel Unit identity
Channel-wide state
Root lifecycle
routing policy
Melodic / Percussion mode
audio synthesis stream
Root-level cache and reset boundary
```

一个 Root 在一次 CompileContext 中最多对应一个 Channel Unit；一个 Channel Unit 也不得同时属于两个 Root。

### 23.3.2 Root 字段

Root 至少保存：

```text
Stable ID
Display Name
Routing Mode: Auto | Fixed
Fixed Port / Channel when Routing Mode = Fixed
Channel Mode: Melodic | Percussion
不保存 child order；成员由 Track 的 Root ID 反向索引
```

Root 名称允许重复，不参与路由或身份判断。Root 必须至少有一个成员 Track；最后一个成员移出或删除时必须与 Root 原子删除。Root 的确定顺序取其成员在 global Arrangement order 中的最早位置，再以 stable ID 作最终兜底；不得保存第二套 child/global order。

### 23.3.3 路由模式

`Fixed` Root 必须保存合法的一基用户 Port `1..16` 与 Channel `1..16`；内部 wire 编码仍为 0-based。两个 Fixed Root 指向同一 Port.Channel 时 semantic validation 失败。

`Auto` Root 不保存历史分配结果。编译器根据当前 Project、CompileContext、Root 顺序和低号优先规则确定 Unit；重新打开、Full Compile 与等价 Incremental Compile 必须得到相同结果。

### 23.3.4 Channel Mode

`Melodic` 与 `Percussion` 是 Root 的正式源语义：

```text
Melodic   -> audio stream 显式关闭默认 drum part；导出到 Channel 10 时写规定的 Normal Part 初始化
Percussion -> audio stream 建立 percussion/drum part；导出到 Channel 10 时不得写 Normal Part 初始化
```

Logical/Event Instrument 分配路径继续把所有获配 Channel 10 Unit 显式初始化为 melodic。只有 Pure MIDI Root 可以选择 Percussion；从 SMF 导入的 Channel 10 Root 默认是 Percussion，其他 Channel 默认是 Melodic。

## 23.4 Pure MIDI Track

### 23.4.1 定义与内容

Pure MIDI Track 是用户可见、可命名、可排序并可独立导出为 SMF MTrk 的直接 MIDI 编曲轨道。它必须属于一个 Root，并保存：

```text
Stable ID
Display Name
Optional Color
Explicit order within Root
Midi Segment collection
```

Pure MIDI Track 不绑定 Event Instrument，不保存 Logical Parameter Lane，且不生成 Event Instrument Instance。

### 23.4.2 顺序语义

Root 内 Track 顺序是正式音乐语义的一部分。同一 Root 的事件合并顺序固定为：

```text
absolute tick
→ Pure MIDI Track explicit order
→ event explicit order within that Track
```

因此重排 Pure MIDI Track 必须标记 Project Modified、进入 Undo/Redo、使相关 canonical/cache 失效，并可能改变同 tick 冲突的可听结果。稳定 ID 只能作完全不可区分输入的确定性兜底，不能替代显式顺序。

### 23.4.3 Mute / Solo

Pure MIDI Track 的 Mute / Solo 与 Logical Track 相同，属于运行期监听状态：

```text
not persisted
not undoable
does not mark Modified
does not change canonical source semantics
does not affect MIDI Export or Audio Render
```

Root 源数据不保存 Mute/Solo。Arrangement 中可见的 shared Root block 可按第 24.9 节拥有独立 group Mute/Solo 运行时状态，且不得改写成员 Track 开关。Root 级共享 Channel 状态意味着单独 Mute 某个成员 Track 可能改变其他成员在监听时收到的共享状态；播放层必须按 canonical 来源追踪执行受控过滤和恢复，不得修改 Project 或重新分配 Root Unit。

## 23.5 Midi Segment

### 23.5.1 与 Logical Segment 共用的容器规则

Midi Segment 与 Logical Segment 共用以下正式语义：

```text
stable identity
[startTick, endTick) range
local tick coordinate
active crop/content window
hidden content retention
move / copy / resize / split / join / delete
same-Track non-overlap
Project End / consumer range clipping
single Project Undo per atomic gesture
```

同一 Pure MIDI Track 内的 Midi Segment 不得重叠；不同 Pure MIDI Track 的 Segment 可以重叠，包括同一 Root 内的 Track。相邻 Segment 合法，不自动连接。

Midi Segment 可在 Pure MIDI Track 之间移动或复制；目标 Track 不得产生 Segment 重叠。同类型跨 Root 移动保留全部直接 MIDI 内容。Logical Segment 与 Midi Segment 之间的移动、复制和粘贴只通过第 20.6.14 节的正式转换与损失确认，不允许隐式丢弃数据或把转换当作编译展开。

### 23.5.2 Segment 内容

Midi Segment 保存：

```text
Direct MIDI Notes
Direct MIDI Channel Events
Opaque imported SysEx / Meta events that belong to this source Track
hidden content outside the active crop window
explicit same-tick order
```

Velocity Lane 直接编辑 NoteOn velocity。底部 Event Lane 直接编辑 MIDI Channel Event，而不是 Logical Parameter。Piano Roll Note 与正式 NoteOn/NoteOff 对一对应，不触发 Event Instrument Mapping。

### 23.5.3 创建与 Track End

用户创建 Midi Segment 时使用 Arrangement 的共享 Segment 创建规则。SMF 导入产生的非空 Track 默认创建一个：

```text
[0, source MTrk End Of Track tick)
```

Segment 长度保留源 MTrk 尾部空白。源 MTrk 的 EOT 为 0 或拆分后完全无可归属内容时，可以保留空 Pure MIDI Track 而不创建零长度 Segment。

## 23.6 Direct MIDI Note 与事件

### 23.6.1 Direct MIDI Note

Direct MIDI Note 至少保存：

```text
Stable ID
local start tick
positive gate length
key 0..127
NoteOn velocity 1..127
NoteOff velocity 0..127
explicit ordering identity for both endpoints
```

同 Root、同 Track 或跨 Track 的同 key Note 重叠均允许。Canonical 和音频后端继续按同 Port.Channel.key 的 FIFO 规则逐个配对 NoteOff。

导入时 `Note On velocity = 0` 按 MIDI 1.0 语义规范化为 NoteOff velocity 0；Midora 不承诺保留其原始 wire 表达。

Direct MIDI Note 在 Pure MIDI 数据链的移动、Resize、复制、canonical SMF projection 和导出中必须保留 NoteOff velocity，即使当前 BASS 后端不使用它。与 Logical Note 跨类型剪贴板时只转换 relative tick、gate、key 与 NoteOn/instance velocity：Logical→Direct 将 NoteOff velocity 设为 0；Direct→Logical 丢弃 NoteOff velocity。详见第 24.6 节。

### 23.6.2 Channel Voice Event 范围

Pure MIDI Track Event Lane 与编译器必须支持完整 MIDI 1.0 Channel Voice 面：

```text
Note Off / Note On
Polyphonic Key Pressure
Control Change 0..127, including Channel Mode
Program Change
Channel Pressure
Pitch Bend
```

Note 编辑优先使用 Direct MIDI Note 对象；无法成对表达的原始 Note message 以 raw direct event 保留。

CC91 / CC93、CC120..127、Polyphonic Key Pressure 与 Channel Pressure 在 Pure MIDI Track 中均可创建、编辑、编译和导出，不产生“不受支持”诊断。Event Instrument SubVoice 的创建、Mapping、Initial/Reset target 面仍按第 8～10 章的受限集合执行，不因 Pure MIDI 支持而自动扩大。

### 23.6.3 原始顺序与重复事件

Pure MIDI 数据不得采用 Logical/Event Instrument 的同目标最终值折叠。以下内容均允许并必须保留：

```text
same tick + same kind duplicates
same tick + same controller duplicates
same key overlapping Notes
cross-Track conflicting state writes
raw RPN / NRPN CC sequences
Channel Mode commands
```

同一 Track 内按显式事件顺序输出；跨 Track 按 23.4.2 合并。后写入事件的运行结果由 MIDI Channel 状态自然决定，编译器不得静默删除、覆盖或重新排序。

### 23.6.4 未配对 Note message

SMF 导入按源 MTrk、effective Port、Channel、key 使用 FIFO 尝试配对 NoteOn/NoteOff。无法配对的 NoteOff 或到 EOT 仍未关闭的 NoteOn：

```text
must be preserved as raw direct Note message
must not be dropped
must not receive an invented gate
must produce one aggregated import Warning per affected source Track
```

Piano Roll 只把成功配对的 Note 显示为长度矩形；raw Note message 在 Event List 中显示并可删除或移动，精确只读信息通过固定 `Properties...` 对话框查看。该只读包装不显示内部稳定 ID，也不提供无约束 wire payload 编辑。

### 23.6.5 Opaque imported events

以下内容允许从 SMF 导入并按原始 payload 与 Track/tick/order 保存：

```text
F0 / F7 SysEx events
unmapped text/meta events
lyrics / cue / device / program name metadata
sequencer-specific metadata
unknown but structurally valid SMF meta event types
```

初版不提供自由 SysEx 或任意 Meta payload 创建/字节编辑。Opaque event 可以查看、选择、移动、删除、随 Segment 操作和重新导出；除第 23.13.2 节明确列出的 GS/XG Part Mode target Channel 归属与音频特权外，其 payload 不得被解释成 Event Instrument、Mapping 或 Channel 分配指令。

Tempo、Time Signature、Key Signature、Marker 等已由 Midora 正式建模的全局 Meta 必须导入 Conductor Track，不作为 opaque Track event 重复保存。Track Name、MIDI Port 与 End Of Track 是结构信息，分别进入 Track/Root/Segment 结构。

## 23.7 Root 活动连通区间与 Reset

### 23.7.1 活动连通区间

对一个 Root，把所有子 Track 当前参与编译的 Segment `[startTick, endTick)` 求并集。没有正 tick 间隙的连续并集区间称为 Root 活动连通区间；一个 Segment 在 tick `T` 结束而另一个在同一 tick `T` 开始时，Root 不经过空闲状态，二者属于同一连通区间。

Root 活动连通区间是一个真正的 MIDI Channel 生命周期。

### 23.7.2 区间开始

Root 从空闲进入活动时，编译器必须在任何用户 Direct Event/NoteOn 之前建立该 Root 的确定初始状态。初始化来源包括 Project Reset Defaults、Root Channel Mode 及范围恢复所需状态；不得在 Root 已活动时因另一个子 Segment 开始而重复执行 Channel-wide 初始化。

### 23.7.3 子 Segment 结束

子 Segment 到达 End 时：

```text
close every still-active Note owned by that Segment exactly
do not send CC120 solely because this child Segment ended
do not reset shared CC/Bank/Program/Pitch/RPN/NRPN state
do not kill Notes owned by sibling Tracks
```

该 Segment 内已经发生的 Channel 状态继续作为 Root 共享状态，直到后续事件覆盖或 Root 生命周期结束。

### 23.7.4 Root 区间结束

当同一 tick 聚合全部 Segment End/Start 后确认 Root 不再有活动 Segment时，执行：

```text
precise NoteOff for every remaining active Root Note
CC120 All Sound Off at the hard boundary
final Reset for state targets used/polluted by this Root interval
release the audio/cache lifecycle for that interval
```

Project End Marker、显式消费者 range end 和 Stop/Reset Playback Engine 仍是更高层硬边界。

### 23.7.5 SMF 生命周期事件归属

Root 级生成事件必须具有确定的 SMF 输出 Track 归属，不得额外创建用户未请求的 Root Control MTrk：

```text
interval-start generated events
→ lowest-order child Track whose Segment starts the interval

interval-end generated events
→ lowest-order child Track whose Segment ends the interval

project/range final Root cleanup
→ lowest-order participating child Track at that boundary
```

该归属只组织 SMF MTrk；Unit 执行投影仍按完整 Root 顺序消费全部事件。

## 23.8 Channel Unit 分配

### 23.8.1 分配顺序

每次编译必须按以下固定阶段分配：

```text
1. validate all Fixed Roots and reserve their exact Units
2. allocate every Auto Root with participating Segment content in earliest-member global Track order to the lowest unreserved Unit
3. allocate Logical/Event Instrument Channel Groups from the remaining Units
4. fail atomically if any Root or Logical allocation cannot be satisfied
```

结构上不允许空 Root。纳入本次 CompileContext 的 Fixed Root 即使成员 Track 没有可编译内容也保留其精确 Unit，因为固定路由表达明确占用意图；没有任何参与 Segment 内容的 Auto Root 不分配 Unit。Auto Root 一旦分配，在本次 CompileContext 的整个范围内保持同一 Unit；不得与 Logical instance 做时间复用。

### 23.8.2 容量与统计

资源硬上限仍为 256 Units。有效需求是：

```text
globally reserved/allocated Root Units
+ peak simultaneously occupied Logical/Event Instrument Units
```

超过 256、Fixed 冲突或 Auto 分配失败均为 Error。资源统计必须分别报告 Root reserved/allocated count、Logical peak 和 combined peak；combined peak `>= 248` 仍只产生既有 Info。

### 23.8.3 Compact / Preserve Routing

Fixed Root 在任何导出 Routing 模式下都不得改址。Auto Root 与 Logical allocation 可以按 CompileContext 重新确定性紧凑分配，但必须绕开 Fixed Root，并保持同一 Root 只对应一个 Unit。`Preserve Routing` 保持本次正式编译的结果；任何无法保持语义等价的 Compact 请求整体失败。

## 23.9 编译与 Canonical Compiled Result

### 23.9.1 Pure MIDI 编译

Pure MIDI Track 编译不执行：

```text
Event Instrument expansion
Logical Parameter Mapping
Mapping Function
Template lifecycle
Per-Note Instance Isolation
SubVoice allocation
```

它只执行 Segment crop/边界、Direct Note materialization、raw event validation、Root merge、范围状态恢复、Root lifecycle、Unit allocation、source tracing 和 canonical freeze。

### 23.9.2 两个正式投影

Canonical Compiled Result 必须从同一事件集提供两个一致的正式投影：

```text
Execution Projection
  group by Channel Unit / Root
  total order by tick and canonical order
  consumed by playback and audio render

SMF Track Projection
  group by ExportTrackId
  preserve Pure MIDI Track topology
  consumed by MIDI Export
```

每个 canonical event 至少能追踪：

```text
Port / Channel Unit
absolute tick
execution order
ExportTrackId
source Root / MidiTrack / MidiSegment / direct object
compiler-generated reason when applicable
```

导出器不得读取 Project 重新推断 Track Name、Root membership、EOT 或事件归属；这些必须由 canonical 的冻结 SMF Track descriptor 提供。

### 23.9.3 排序边界

Logical/Event Instrument 事件继续遵守第 8、11、12 章的状态准备和生命周期排序。Pure MIDI 原始事件保留 23.4.2、23.6.3 的显式顺序；不得用 Logical 状态优先级重排它们。

编译器生成的范围恢复、Root 初始化和硬边界清理事件按明确 canonical role 插入，并不得改变同 Track 其余原始事件的相对顺序。

### 23.9.4 Full / Incremental 等价

Pure MIDI Track 顺序、Root 路由/模式、Segment 内容和 opaque payload 都必须进入 canonical fingerprint。Full 与 Incremental 对同一输入必须生成相同：

```text
Unit assignment
Root lifecycle intervals
event bytes and order
ExportTrack descriptors
diagnostics
resource statistics
```

## 23.10 编译与音频缓存

### 23.10.1 不允许按子 Track 独立合成

同 Root 子 Track 共享一个 MIDI Channel 的 Program/CC/Pitch/voice 状态。不得把各子 Track 分别送入独立 synth stream 后混音；该做法会改变共享 Channel 语义。

### 23.10.2 缓存层次

Pure MIDI 路径使用：

```text
MidiSegment normalized event fragment cache
Root merged event/checkpoint cache
pre-Master/pre-Limiter Root raw PCM tile/cache pack
post-sum playback span cache
Render-Ahead ring
```

Root PCM key 至少包括 Root composite fingerprint、start-state fingerprint、Channel Mode、程序级 Enabled SF2/SFZ 有序配置（含 target）及主文件元数据缓存身份、Tempo projection、sample rate/format、native baseline、voice policy 和 renderer version。Root composite fingerprint 对可听内容的投影必须覆盖实际 Direct Note / Channel Event 字段、分页 source opaque 内容 fingerprint 与 copy-on-write delta；renderer 新增或改变 privileged SysEx 等可听解释时必须提升 renderer cache generation，不能假设未改变的 source fingerprint 会自然淘汰旧 PCM。集合 `Generation`、编辑次数或仅 stable ID 不构成内容 identity。

### 23.10.3 失效与收敛

编辑 MidiSegment 只使其 normalized fragment 和对应 Root 从最早受影响 tick 起 dirty。Root merge、canonical、PCM 与 playback span 向后重新计算；当新旧 Root checkpoint 的完整 Channel state、active Note multiset、event suffix dependency 和 allocation state hash 相等且后续 source 未变时，可以复用旧后缀。

其他 Root 和未受影响 Logical Unit 的完整缓存不得仅因一个 MidiSegment 编辑而失效。最坏情况下允许重算该 Root 到 CompileContext 结束，但不得通过保留历史错误状态换取命中。

## 23.11 SMF 导入

### 23.11.1 支持范围

初版只接受：

```text
Standard MIDI File Format 0 or Format 1
MIDI 1.0 events
positive TPQN division 1..32767
```

明确拒绝：

```text
Format 2
SMPTE division
MIDI 2.0 UMP / MIDI Clip
malformed or truncated chunks/events
unsafe sizes or counts beyond implementation's documented bounded admission limits
```

### 23.11.2 Running Status

读取器必须支持合法 Running Status。状态只在当前 MTrk 内有效；首个依赖 status 的 data byte、错误 data-byte 数量、非法 status 延续或跨 MTrk 继承均导致导入失败。Meta/SysEx 对 Running Status 的清除行为必须符合 SMF 1.0 解析规则。

导入后不保存“本事件原来是否省略 status”这一 wire 表达；Project 保存的是语义事件。Midora 的 SMF 导出继续为每个 Channel Event 显式写 status byte。

### 23.11.3 多 Channel MTrk 拆分

导入器不得因一个源 MTrk 含多个 Channel 而拒绝文件。它必须维护每个源 MTrk 的当前有效 MIDI Port，并按：

```text
(effective Port, Channel)
```

把 Channel Event 拆到对应 Root / Pure MIDI Track。源 Track 内 Port 变化后的事件使用变化后的 effective Port。缺少 Port Meta 时使用内部 Port 0（UI Port 1）。

Format 0 的单 MTrk 可由此产生多个 Pure MIDI Track；Format 1 的多 Channel MTrk 同样拆分。拆分后的 Track 顺序按源 MTrk 顺序，再按该 MTrk 中 `(Port, Channel)` 首次出现顺序；名称使用源 Track Name，并追加确定的 Port/Channel 区分后缀。

无 Channel 的 opaque SysEx/Meta 不得因拆分而复制。第 23.13.2 节可识别的 GS/XG Part Mode SysEx 由 payload target Channel 建立或选择对应 bucket；其他 opaque event 对每个 `(source MTrk, effective Port)` 按原顺序归属到该源 MTrk 在同 Port 首次出现的派生 Pure MIDI Track。若该 Port 没有 Channel bucket，但仍有必须保留的普通 opaque 内容，则创建一个 structure-only Pure MIDI Track。Structure-only Track 挂到该 Port 已存在的最低 Channel Root；若该 Port 尚无 Root，则建立 `Fixed(Port, Channel 1) / Melodic` Root。它自身不产生 Channel Event，但仍服从 Fixed Root 预留规则。

Format 1 的 MTrk 0 若在提取 Conductor 与结构 Meta 后没有剩余 Channel/opaque 内容，只由 Conductor Track 表达，不额外创建空 Pure MIDI Track。其他源 MTrk 若需要保留空 Track 名称/EOT，则按前述 structure-only 规则使用默认或当时有效 Port；不得丢弃、复制到所有拆分 Track 或虚构 Channel Event。

### 23.11.4 Root 创建与 Port 映射

同一有效 `(Port, Channel)` 的导入 Track 进入同一个 Root。源 Port `0..15` 可直接建立 Fixed Root；Channel 10 Root 默认 Percussion。

如果源 Port 超出 Midora 范围、Port 标识无法一一映射或结果超过 16 Ports / 256 Roots，必须在提交新 Project 前显示显式 Port Mapping/冲突 Review。用户可以建立一对一合法映射或取消；不得 modulo、clamp、静默合并两个源 Port 或部分导入。

### 23.11.5 Conductor 与 Track Meta

以下事件正式映射到 Conductor Track：

```text
Tempo
Time Signature
Key Signature
Marker
```

Track Name 用作 Pure MIDI Track 名称。MIDI Port 与 EOT 用于 Root/Segment 结构。其他结构合法 Meta/SysEx 按 23.6.5 与 23.11.3 的 single-owner 规则保存为 opaque event。

为了在不放松 Midora Project 内部契约的前提下接受常见外部 SMF，导入器必须在 detached candidate 进入 semantic validation 前执行以下确定性兼容归一化：

```text
没有 tick 0 Tempo
  -> 显式添加 120 BPM

没有 tick 0 Time Signature
  -> 显式添加 4/4

同 tick 存在多个 Tempo
  -> 按 source MTrk index，再按该 MTrk 内原事件顺序排序，只保留最后一个

同 tick 存在多个 Time Signature 或多个 Key Signature
  -> 各类型分别按 source MTrk index，再按该 MTrk 内原事件顺序排序，只保留最后一个

Track Name 缺失、trim 后为空或所有 Track Name 都无法按严格 UTF-8 / Windows-31J 解码
  -> 使用 `MIDI Track N`，N 为一基 source MTrk index
  -> 同一 source MTrk 拆分为多个派生 Track 时追加确定的 Port/Channel 后缀
```

Tempo、Time Signature 与 Key Signature 的“后来者”只由源 MTrk 与原事件顺序决定，不得依赖集合枚举、稳定 ID 分配或导入时并发。每种类型内，值完全相同的同 tick 重复项作为冗余项移除并记录一条汇总 `Info`；存在不同值时因正式 Conductor 状态被改变而记录一条汇总 `Warning`。该归一化只发生在外部 SMF 导入边界，不放松 Midora Project 内部“同 tick 单一正式状态”的 semantic validation。

导入器解释 Track Name 与 Marker 时必须先尝试严格 UTF-8；失败后再按固定 Windows-31J（Microsoft code page 932）严格解码，不读取系统区域设置，也不使用 Unicode 替换字符。Windows-31J 解码成功时把所得 Unicode 文本进入正式 Track Name / Marker 模型，并汇总记录 `Info`；后续 SMF 导出把该文本统一编码为严格 UTF-8，不承诺保留原字节编码。

若两种编码都失败，单个 Track Name Meta Event 只丢弃该名称事件，并在需要时使用上述确定性回退名称；单个 Marker Meta Event 也只丢弃该 Marker，并因丢失已建模内容汇总记录 `Warning`。上述情况都不得拒绝其余结构合法的 MIDI。其他未被 Midora 建模的文本 Meta 不在导入时解码，继续按 single-owner opaque 原始 payload 保存；SMF 导出仍只产生严格 UTF-8 的正式文本 Meta。

上述补全、去重、丢弃与回退命名不进入 Project、Undo/Redo 或 Compiler Diagnostics。它们只进入当次导入任务的结构化 `Info` / `Warning` 报告；成功提交 Project 后，UI 必须显示一份汇总且可复制的报告。

### 23.11.6 新 Project 事务

`Open MIDI as New Project`：

```text
run the common Project Switch Guard
pass 1: stream-parse structure/conductor/Port/Channel summaries into a detached import plan
perform Port Mapping Review when required
pass 2: stream-parse channel records directly into transactional source page-pack builders
validate the detached paged candidate without materializing all records
use source TPQN exactly
derive Project Name from source file stem
create Roots / Tracks / Segments / Conductor atomically
commit only after all mapping and validation succeeds
open the new Project as unsaved
leave the program-level SoundFont list unchanged
```

失败或取消时保留当前 Project，不暴露 partial candidate，不产生 Undo entry。初版不提供“Import MIDI into Current Project”。

导入不得调用 `File.ReadAllBytes`、不得保留完整 parsed-event graph，也不得同时保留全量 channel-event lists、paired-order hash set、Direct object graph 和其 compilation snapshot。两遍读取必须对同一冻结文件 identity/length 使用只读句柄或在遍间复核 identity/length/last-write；源文件中途改变时导入失败。Note pairing 按每个 source MTrk/Port/Channel/key 的有界 active FIFO 完成；已完成记录按固定 page 大小排序/写入，跨 page 总序由 page descriptor 与 k-way merge 保证。

## 23.12 SMF 导出

### 23.12.1 固定格式

Midora 仍只导出 SMF Type 1、Project TPQ、严格 UTF-8 文本 Meta，并为每个 Channel Event 显式写 status byte。Running Status 只属于导入兼容能力；导出不提供开关。

### 23.12.2 Track 拓扑

每个导出文件：

```text
MTrk 0 = Conductor / Meta Track
then every selected Pure MIDI Track as one independent single-channel MTrk
then Logical/Event Instrument output as one MTrk per actual Channel Unit
```

Pure MIDI MTrk 顺序固定为 global Arrangement order 过滤 Pure MIDI Track 的结果。Logical Unit MTrk 继续按实际 Port→Channel 排序。一个 Pure MIDI MTrk 只含其 canonical `ExportTrackId` 的事件；同 Root 的多个 MTrk 可以共享 Port.Channel。该规则保证 SMF 导入形成的 Track 顺序可在导出时保持。

该拓扑适用于 Whole Project 与 Per Port。既有 `Per Logical Track` 模式保持为 Logical/Event Instrument 专用，不复制 Pure MIDI Track；单独导出某条 Pure MIDI Track 使用 Whole Project + 显式 Track 选择。

Conductor、被选择 Pure MIDI Track 与实际 Logical Unit MTrk 的合计数量必须可由 SMF MThd 的 unsigned 16-bit `ntrks` 表示；超出时导出在创建 staging 文件前整体失败，不得合并用户 Track 规避上限。

每个 MTrk 数据区固定受 `0xFFFFFFFF = 4,294,967,295` 字节上限约束，不包含 8 字节 chunk 头。不得为了容纳更多字节拆分 Pure MIDI Track、Logical Unit Track 或 Conductor；超限仅令本次导出原子失败，Compiler 不统计该编码大小或拒绝编译。这不是整个 SMF 文件大小上限，完整边界见 §14.12.8～9。

### 23.12.3 Track Name、Port 与 Root metadata

Pure MIDI MTrk 的 Track Name 必须是冻结 canonical descriptor 中的用户 Track 名称，不得改成 `Port P / Channel C`。每个 MTrk 写对应 Root 的 MIDI Port Meta，Channel status 使用该 Root 的 Channel。

为了在 Midora 间精确往返，Pure MIDI MTrk 在 tick 0 可以写版本化 Midora Sequencer-Specific Meta，保存 Root/Track Stable ID、Root Name、Root membership、global Arrangement Track position、Routing Mode 和 Channel Mode。该 Meta：

```text
must not affect playback
must be safely ignorable by other software
must use a versioned bounded binary payload
must not replace standard Track Name / Port / Channel representation
```

重新导入时优先使用合法且一致的 Midora metadata；缺失、被外部软件删除或不一致时，退化为按有效 Port.Channel 和 MTrk 顺序重建 Root，不得因此拒绝一个本来合法的标准 MIDI 文件。

SMF 没有可移植的 Root/folder 嵌套结构。Midora 只能保证其他软件看到独立、命名、有序的平级 MTrk；不得承诺第三方 UI 显示 Root 文件夹。

### 23.12.4 End Of Track

Pure MIDI MTrk 的 EOT 使用该 Track 在冻结导出范围内的自身结束位置，保留 Midi Segment 尾部空白；不再强制与所有其他 MTrk 对齐。Conductor 与 Logical Unit MTrk 继续使用导出任务统一 endTick。整个文件长度由所有 MTrk 的最大 EOT 决定。

非零范围导出时，所有输出 tick 相对 startTick 重基；Pure MIDI Track EOT 必须 clamp 到该 Track 与请求范围的交集并保持非负。结构有效的空 Track 可以在 tick 0 写 EOT。

### 23.12.5 Channel 10 初始化

以下事件 Track 在 Track Name、MIDI Port 和 Midora metadata 后、canonical Channel Event 前写既有 GS→XG Normal Part 初始化：

```text
Logical Unit MTrk whose Unit channel is Channel 10
Pure MIDI MTrk whose Root is Melodic and routed to Channel 10
```

Percussion Root 的 Channel 10 MTrk 不得写 Normal Part 初始化。初始化不改写 canonical Bank/Program，也不发送 GM/GS/XG Reset。

### 23.12.6 Direct events 与 opaque events

Pure MIDI MTrk 原样编码 canonical 中的完整 Channel Voice Event，包括 CC91、CC93、Channel Pressure、Poly Pressure 和 Channel Mode。合法 opaque SysEx/Meta 按冻结 payload、tick 和 Track 内顺序重新导出；导出器不得把 opaque payload 解释为 Midora 业务对象。

仅为编码超长 delta，允许按 §14.12.2 插入固定空 Text Meta；不得改变上述原事件或将占位回写源 Project/canonical。重新导入时按合法 opaque Meta 保留，不能仅凭 `FF 01 00` 就删除用户原有空文本，不保证字节级 round-trip。单条 payload 长度超限仍拒绝，不能使用 delta 填充规则拆开 payload。

### 23.12.7 跨 MTrk 同 tick 兼容 Warning

Midora 内部执行顺序是确定的，但 SMF 不提供跨 MTrk 的可移植总顺序。导出预检查必须检测同 Root 不同 MTrk 在同 tick 的顺序敏感组合，例如：

```text
same target conflicting state writes
Bank / Program / Reset versus NoteOn
same Port.Channel.key NoteOff versus NoteOn
Channel Mode command versus sibling events
```

检测到时按 Root 产生一条汇总、非阻塞 Warning，包含计数和首个位置。不得移动事件、插入 tick 偏移、合并 Track 或拒绝编译。Warning-as-error 只在用户明确启用该既有导出策略时阻止导出。

### 23.12.8 Round-trip 边界

正式保证：

```text
single-channel Type 1 source MTrk -> preserve separate Track, name, order and Track End
Midora-created Pure MIDI Track -> preserve the same properties
multiple MTrks on one Root -> remain multiple MTrks
```

不保证：

```text
byte-identical output
original Running Status decisions
original chunk byte layout
Type 0 remains Type 0
multi-channel source MTrk remains one MTrk
third-party retention of Midora private metadata
identical cross-MTrk tie ordering in every player
```

## 23.13 播放、预览与音频渲染

### 23.13.1 Root/Unit synth 语义

一个 Root 的所有子 Track 必须合并进入一个抽象 1-channel synth stream；不得每 Track 独立合成后求和。Logical Unit 与 Root Unit 仍按稳定顺序求和，再应用 Playback Master Volume 与全局 Limiter。

Stream 创建、重建和复用前必须按 canonical Unit 的 Channel Mode 建立 Melodic 或 Percussion 状态；不能无条件执行 melodic `DEFDRUMS(0)`。

### 23.13.2 CC91 / CC93 与 Channel Mode SysEx 特权

正式 BASSMIDI Stream 继续启用 `BASS_MIDI_NOFX | BASS_MIDI_NOTEOFF1`。Pure MIDI canonical 中的 CC91 / CC93 在 MIDI 文件语义中保留，但 Midora 实时/离线音频投影确定性忽略其 Reverb/Chorus 效果，不产生诊断，也不改变缓存键以外的正式 MIDI 结果。

初版音频消费者原则上不解释或发送 opaque imported SysEx/Meta；它们继续存在于 Project/canonical SMF 投影并可重新导出。因此 Midora 音频试听不承诺复现依赖未知 SysEx 的外部设备行为。

唯一特权是可确定归属单个 MIDI Channel 的 GS/XG Part Mode SysEx：

- Roland GS DT1 `41 <device 10..1F> 42 12 40 <part 10..1F> 15 <mode 00..02> <valid checksum> F7`；
- Yamaha XG `43 <device 10..1F> 4C 08 <part 00..0F> 07 <mode 00..02> F7`。

导入拆分多 Channel MTrk 时，这两种消息必须归属其 payload 指定 Channel 的派生 Pure MIDI Track；不得沿用普通无 Channel opaque event 的 first-owner 规则。Compiler 在保留原 opaque source/SMF 投影的同时，额外产生明确类型的 canonical audio privileged event，保持 absolute tick、source Track、SMF Track order 与 event order。若播放/渲染从 Root 活动连通区间中途开始，必须在范围起点恢复该连通区间内最近一条先前 Part Mode；不得跨 Root 空闲边界继承。

音频消费者把 privileged event 的目标重定向到该 Root 的 1-channel Unit stream channel 0，并以规范化完整 SysEx 原始字节提交给 BASSMIDI。Root `Channel Mode` 仍只负责 Stream 初始 Melodic/Percussion 状态；后续 privileged event 可以改变活动期间模式。任意其他 GS/XG 参数、GM/GS/XG Reset、厂商 SysEx、F7 continuation 与 Meta 继续不发送。识别失败、校验和错误或不支持的 mode 不产生特权，也不产生诊断，仍按普通 opaque event 保留和导出。

### 23.13.3 Mute / Solo

播放中的 Pure MIDI Track Mute 必须关闭该来源当前活动 Note，并阻止其后续来源事件；不得向整个 Root 发送 CC120 或 Reset。解除 Mute/Solo 时，播放层按当前 canonical frontier 恢复该 Track 必需的非 Note 来源状态并重新路由到活动 Root Unit，但不补发范围前 NoteOn。共享 Channel 状态可能使监听结果受被过滤 Track 的状态事件影响；这属于运行期监听，不改写成品输出。

## 23.14 UI 与编辑器复用

### 23.14.1 Arrangement

Arrangement 固定显示 Conductor 第一行，随后按一个 global mixed order 平铺 Logical / Pure MIDI Track。Pure MIDI Track 名称左侧显示 MIDI 图标；Fixed Root 在 UI 中表现为 Track 的 Port.Channel / mode 属性，不显示 Root 行；共享 Auto Root 的连续成员以 brace block 表示并可整体移动、加入或拆出。完整规则见第 24 章。

Track Header 的 hover/pressed、重排、Rename、Copy/Cut/Paste/Duplicate、Delete、Mute/Solo 与 Segment 操作复用既有样式和交互。Root 不显示独立 Header 或 Mute/Solo；Event Instrument binding 命令不显示在 Pure MIDI Track 菜单。

Pure MIDI Segment 除 Direct Note preview 外，还在 Note 上层绘制统一颜色、50% 透明度的 non-Note event 线；两层独立缓存和局部失效。Conductor 第一行使用独立缓存的按类型着色圆点概览。完整视觉、LOD 与性能边界见第 24.11、24.14 节。

## 23.20 Pure MIDI Track 颜色分配

新建与 SMF 导入的 Pure MIDI Track 必须获得固定八色低饱和调色板中的 concrete color。调色板及顺序固定为：

```text
#6d7fa8  #9b6a6a  #6f936f  #9a815f
#806fa3  #60918c  #9a6f8a  #849064
```

选择颜色时，以 Track 最终插入的 global Arrangement 位置之前出现的 Pure MIDI Track 数量对 8 取模；不得按 Root、随机数、路径、名称或 hash 分配。中间插入、删除、重排或 Root 变化不得重染既有 Track。

Duplicate 与 Copy/Paste 继承源 Track 的 concrete color。旧数据缺少颜色时只使用固定 fallback，不按当前 Arrangement order 动态推导。Properties 可原子修改颜色；颜色进入 Project Undo/Redo 和 Modified，但不影响 compiler、canonical、MIDI Export、Audio Render 或音频缓存。

### 23.14.2 共享 Segment/Piano Roll

不得从头复制第三套 Timeline 编辑器。实现应抽取并复用：

```text
Arrangement Segment rendering and gestures
Piano Roll rendering / hit testing / selection / note gestures
Velocity Lane
point/event Lane
Grid / Snap / zoom / pan / scroll
tile cache and transient overlay
```

Logical Segment、SubVoice 与 Midi Segment 通过数据/命令 adapter 提供不同领域对象和提交规则。修复共享视觉或手势缺陷时不得要求在三套复制代码中分别修复。

### 23.14.3 Midi Segment Editor

Midi Segment Editor 的上部 Piano Roll 与 Velocity 交互和 Logical Segment Editor 一致；下部 Lane 直接选择 MIDI Channel Event 类型。完整 Channel Voice Event 可创建；opaque imported event 只在 Event List 中选择、移动、删除，并通过固定只读 `Properties...` 对话框查看 payload 摘要，不提供自由 payload 编辑器。

用户编辑造成 exact collision 时，Direct Note 的同 start tick + key 后来对象静默丢弃；Direct Channel Event 的同 tick + 同正式事件类型由后来编辑对象覆盖原对象。该规则只在相关编辑命令提交时作用：SMF 导入所得、尚未被该 exact key 编辑触及的重复 Note/Event 必须保留，打开、浏览、编译和导出不得为了套用编辑器便利规则而静默清洗源数据。

Root/Track/Grid/Snap/Lane 高度等视图状态仍属于 Project Session UI State，不进入 `.midora`。

## 23.15 持久化

`.midora` 必须分别保存 Root 与 Track：

```text
midi-channel-roots/mcr_<id>.pb
midi-tracks/mt_<id>.pb
```

Root 文件只保存共享路由字段，不保存有序 Track 引用；Track 文件保存 parent Root ID、Track 字段、Midi Segment、Direct Note/Event、opaque payload 与顺序。`project.json` 保存 global Arrangement track tagged union，并分别建立 Root/Track 路径与名称快照；Root 成员由 Track 引用反向建立。manifest、project index、文件名、对象内部 ID/type 必须严格一致；空 Root、重复 Track、断裂 Root 引用或第二套 child order 均按结构损坏处理。

本次变更属于开发期破坏性格式修订。旧开发期 `.midora` 布局不提供兼容读取、迁移或双写；schema、protobuf descriptor 和 golden bytes 必须作为同一当前基线整体重建。产品版本名称不因该开发期格式修订自动改变。

Root 或 Track 单对象损坏可形成 Damaged Placeholder；损坏 Root 下正常 Track 必须保留可定位索引但不参与编译，直到用户删除损坏 Root/相关对象。只要存在任何 Damaged Placeholder，继续遵守禁止 Save/Save Copy 的规则。

## 23.16 诊断与失败原子性

诊断必须能够定位：

```text
MIDI Channel Root
Pure MIDI Track
Midi Segment
Direct Note/Event
source MTrk index and byte offset during import
effective Port/Channel
absolute and local tick
ExportTrackId
```

Root 固定路由冲突、资源超限、Track 内 Segment 重叠、事件值域/结构非法、SMF 解析失败和导出 MTrk 数超出 MThd `ntrks` 表示范围为 Error。跨 MTrk 同 tick 兼容风险只属于导出 Warning。合法 CC91/CC93、同 key Note overlap、同 tick direct duplicates 和多 Track 共享 Root 不产生编译诊断。

导入、编译、保存和导出都必须失败原子：失败不得提交 partial Project、partial canonical、partial package 或 partial `.mid`。

## 23.17 明确非目标

初版不支持：

```text
SMF Format 2
SMPTE time division
Import MIDI into Current Project
MIDI 2.0 / UMP / MIDI Clip
free-form SysEx or arbitrary Meta byte editing
portable folder hierarchy in third-party SMF editors
byte-identical SMF round-trip
conversion between Pure MIDI Track and Logical Track/Event Instrument
one SoundFont per Root/Track/Port
traditional realtime MIDI OUT
```

## 23.18 验证门

实现至少必须覆盖：

```text
Format 0/1 + TPQN import golden files
Running Status state-machine and malformed input corpus
multi-channel MTrk and mid-Track Port changes
opaque Meta/SysEx preservation
FIFO Note pairing and unmatched raw Note preservation
Root fixed/auto allocation and 256 Unit boundaries
Melodic/Percussion Channel 10 audio/export behavior
Root connected lifecycle and child Segment boundary isolation
same-Root overlapping Tracks and deterministic order
Full/Incremental equivalence under unordered collection input
Root checkpoint convergence and localized PCM cache invalidation
Pure MIDI Track name/order/EOT SMF export
Midora private metadata present/stripped re-import
explicit-status export and semantic—not byte—round trip
cross-MTrk order-sensitive warning
strict persistence schema/descriptor/golden bytes and damaged placeholders
shared Timeline performance with dense Notes/events
```

## 23.19 极端规模 source pages 与范围消费

### 23.19.1 Source data 物理表示

`MIDI Channel Root → Pure MIDI Track → Midi Segment → Direct MIDI Note/Event` 的领域关系保持不变。Pure MIDI Track 的 record body 物理上使用第 16.30 节定义的 immutable page pack；对象 class/list 只允许用于小型新建内容、当前编辑 overlay 或兼容测试，不能作为导入大文件后的唯一正式存储。

每个 Midi Segment 必须提供不分配全量对象的 value cursor：

```text
EnumerateNotes / EnumerateChannelEvents / EnumerateOpaqueEvents
QueryNotesIntersecting(startTick, endTick, optional pitch range)
QueryEvents(startTick, endTick, optional event target)
TryGetByStableId
aggregate count / tick bounds / content fingerprint
```

查询合并 immutable base、replacement/new overlay 与 tombstone，返回正式逻辑顺序。ID lookup 使用 page ID bounds 和 overlay index，不允许建立与整个项目 record 数等长的 managed dictionary。

### 23.19.2 内存与临时存储边界

导入、打开、编译、播放准备、MIDI 导出、音频渲染和 UI 浏览的常驻 managed memory 必须由：

```text
number of Roots/Tracks/Segments
active Note FIFO
bounded source/canonical/IPC page caches
current edit overlay and selection
visible UI tiles/ranges
```

决定，而不是由 Project 总 Note/Event 数线性决定。允许 source backing 与 canonical/runtime packs 使用本地 SSD；文件数量按 Project/Track/generation 有界，禁止每 page/Note 一个文件。磁盘不足、page checksum 失败或无法建立 transaction backing 是显式、失败原子的导入/打开/编译错误。

### 23.19.3 范围编译与远处内容

Full Compile 可以顺序访问全部 source pages以冻结完整 canonical generation，但不得一次物化全部记录。Playback range cursor 只读取当前起点 checkpoint 和请求水位覆盖的 pages；远处 Pure MIDI Segment 的 record count 不得影响播放 Startup 准备。MIDI Export 与 Audio Render 顺序消费完整 pages，并用有界前瞻/回压控制内存。

### 23.19.4 性能与规模验证

除小型 semantic golden 外，自动/基准验证至少包含合成的 1M、10M、100M Direct Note 数据，验证：

```text
peak managed bytes remain within documented page-cache + overlay budget
no whole-file byte[] and no whole-project event/object array
range query cost follows intersecting pages, not total Project records
playback Startup event preparation is independent of far-future record count
page-boundary FIFO/order/range-restore equivalence
cancel/fault leaves no published partial pack/project/canonical generation
```

产品验收样本可使用更高规模文件，但测试名称必须报告输入 record 数、pack bytes、import/compile/startup elapsed、peak working set、peak managed heap、page hit/miss 与 IPC in-flight peak；未执行的规模不得宣称通过。
