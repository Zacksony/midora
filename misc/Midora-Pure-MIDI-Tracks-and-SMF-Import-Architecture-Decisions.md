# Midora Pure MIDI Tracks 与 SMF Import 架构决定

> 状态：**Accepted**  
> 接受日期：**2026-08-18**  
> 需求基线：`misc/Midora-SRS-Initial-Release-v0.1/23-Pure-MIDI-Tracks-and-SMF-Import.md`  
> Requirement trace：`misc/Midora-Pure-MIDI-Tracks-and-SMF-Import-Requirement-Trace.md`

本文记录 Pure MIDI Track、SMF 导入和保留 MIDI Track 拓扑所需的架构决定。它补充 `Midora-Core-Architecture-Decisions.md` 与 `Midora-Audio-Backend-Architecture-Decisions.md`；冲突的旧决定由本文明确取代或限缩。

## 1. Requirement trace

### 1.1 正式输入

```text
Project TPQ and Conductor Track
MIDI Channel Roots with Stable ID, order, routing and Channel Mode
Pure MIDI Tracks with Stable ID, parent Root and explicit order
Midi Segments with Content Window and direct/opaque events
Logical/Event Instrument source data
CompileContext range and Track selection
SMF 1.0 Format 0/1 + TPQN byte stream for import
MIDI Export settings and frozen output plan
Project SoundFont identity and audio renderer profile
```

### 1.2 正式输出

```text
one canonical result containing an Execution Projection and SMF Track Projection
deterministic fixed/auto Root and Logical Unit allocation
Root lifecycle intervals and compiler-generated boundary events
SMF Type 1 preserving selected Pure MIDI Track name/order/EOT topology
an atomically-created unsaved Project from a supported SMF
strict .midora Root/Track source files
Root-level event/checkpoint/PCM cache generations
```

### 1.3 关键边界

- Pure MIDI 仍必须经过 `Project → Validation → Compilation → Canonical → Consumer`，不建立旁路。
- Root 是 Channel Unit 与 Channel-wide 状态的身份；Pure MIDI Track 是用户编曲和 SMF MTrk 的身份。
- 同 Root 跨 Track 冲突合法且有确定的 Midora 执行顺序，但 SMF 第三方消费者的跨 MTrk tie 顺序不受 Midora 控制。
- Midi Segment 复用现有 Segment 容器行为；内部对象不经过 Event Instrument、Mapping、Lifecycle 或 SubVoice 分配。
- 旧开发期项目格式不兼容；本变更不建立迁移、双读或双写。

### 1.4 失败条件

```text
duplicate Fixed Root route
combined Root reservation plus Logical peak > 256
same owning Track Segment overlap
invalid direct MIDI values/order/identity
malformed/truncated SMF or unsupported Format 2/SMPTE division
unresolvable source Port mapping
strict persistence mismatch or damaged required structure
MIDI encoding/self-validation failure
```

导入、编译、保存和导出均失败原子，不发布 partial Project、canonical、package 或 MIDI 文件。

### 1.5 持久化与运行时归属

Root routing/mode/order、Track membership/order/name、Midi Segment、direct/raw/opaque event 属于 Project 源数据。Auto Root 实际获配 Unit、Root interval、canonical projections、checkpoint、cache key、PCM、SMF import candidate、输出路径和任务状态属于派生或运行时数据。

## 2. ADR-PMIDI-001（已接受）：Root 是 Channel 生命周期，Track 是编辑与 MTrk 身份

### 决定

正式领域关系固定为：

```text
MIDI Channel Root
└─ Pure MIDI Track
   └─ Midi Segment
      ├─ Direct MIDI Note
      ├─ Direct MIDI Channel Event
      └─ Opaque Imported Event
```

Root 本身不保存普通时间线事件。它定义所有 child Tracks 共享的 Unit identity、Channel state、routing、Melodic/Percussion mode、synth stream、lifecycle 和 cache owner。Pure MIDI Track 保存名称、顺序与 Segment，是一条独立 SMF MTrk 的正式身份。

### 理由

把 Root 与 Track 合并会迫使“一 Channel 只能一 Track”，无法保留导入文件的编曲结构；把每个 Track 当独立 Unit 又会破坏同 Channel 的 Program/CC/Pitch/Note 共享语义。两层模型同时保留 Channel 正确性和跨软件 Track 可读性。

### 拒绝方案

- Pure MIDI Track 直接等于 Unit：无法表达同 Channel 多 Track。
- Root 自身也是可编曲 Track：会制造两套事件归属和模糊 MTrk 身份。
- 把 MIDI 导入强制转换为 Logical Track/Event Instrument：不能普遍保持任意 MIDI 语义。

### 后果

Root 成员 Track 重排是可听语义变更；Track Mute/Solo 必须按来源精确过滤，不能清空整个 Root Unit。UI 依 ADR-UI-041 展示 global Track order、Fixed route property 或 Auto Shared brace，不再显示 Root parent row。

## 3. ADR-PMIDI-002（已接受）：活动连通区间定义 Root 生命周期

### 决定

对同一 Root 的已选择 Midi Segments 求 `[startTick,endTick)` 并集；无正 tick 间隙的连通区间是一个真正 Channel 生命周期。子 Segment End 只精确结束该 Segment 拥有的 Note，保留共享 Channel state 与 sibling Notes；只有 Root interval end、Project End Marker 或消费者 range end 执行 Root 级剩余 NoteOff、CC120、final Reset 和释放。

相邻 Segment 在同一 tick 交接属于同一 interval，不能先 Reset 再开始。Root 初始化只在 idle→active 边界发生。

### 理由

同 Root 的 Track 共享 Channel-wide state。按 child Segment 独立 Reset 会截断其他 Track、破坏持续控制器状态并制造瞬时噪音。

### 拒绝方案

- 每个 Segment End 一律 CC120/Reset。
- 永不 Reset，直到 Project end。
- 依据 Track 而非 Root 建立 Channel 生命周期。

### 后果

编译器必须追踪 Note owner、Root active segment count 和完整 Channel state。SMF 中 Root-generated event 依照 SRS §23.7.5 确定归属到某个 child MTrk，不额外创建 Root Control Track。

## 4. ADR-PMIDI-003（已接受）：Fixed Roots、Auto Roots、Logical groups 的确定分配

### 决定

一次 CompileContext 的 Unit 分配阶段固定为：

```text
validate and reserve all Fixed Roots
→ allocate every participating Auto Root by its earliest member global Track order to the lowest free Unit
→ allocate Logical/Event Instrument groups from remaining Units
```

空 Fixed Root 仍保留 Unit；空 Auto Root 不分配。Root 在本次上下文内不与 Logical instance 做时间复用。总需求是 Root reserved/allocated count 加 Logical peak，最大 256；`>=248` 仍是 Info。

Channel 10 的 mode 来自消费者身份：Logical/Event Instrument 固定 Melodic；Pure MIDI Root 使用显式 Melodic/Percussion，导入 Channel 10 默认 Percussion。

### 理由

Fixed routing 是用户的外部互操作承诺，必须先保留。Root 跨整个上下文持有 Unit 可避免 Track/SMF 身份和 Channel state 随时间漂移；Logical 动态 coloring 继续使用剩余资源。

### 拒绝方案

- Fixed 与 Auto 一起参与低号分配。
- Root 与 Logical instance 分时复用 Unit。
- 无条件把 Channel 10 改成 melodic 或 percussion。

### 后果

Compact Routing 不得移动 Fixed Root；Root 数量会减少 Logical 可用资源。Full/Incremental 必须产生相同路由与资源统计。

## 5. ADR-PMIDI-004（已接受）：Canonical 同时冻结执行投影与 SMF Track 投影

### 决定

Canonical Compiled Result 从同一事件集冻结：

```text
Execution Projection
  events grouped/ordered by Root or Logical Channel Unit
  playback/audio consumer input

SMF Track Projection
  events grouped by ExportTrackId
  frozen descriptor: name/order/port/channel/root mode/EOT/source
  MIDI export consumer input
```

Pure MIDI 原始事件总序为 `absolute tick → Track explicit order → event explicit order`，不使用 Logical 同目标折叠。编译器生成的 Root initialize/boundary events具有显式 role 与确定 MTrk 归属。导出器不得回读 Project 推断 Track 拓扑。

### 理由

只按 Unit 存 canonical 会丢掉 Pure MIDI MTrk 身份；只按 MTrk 存 canonical 又会迫使音频消费者重建跨 Track Channel 总序。双投影在一个冻结结果中解决两类消费者需求，不产生第二语义来源。

### 拒绝方案

- MIDI 导出回读 Project 再分 Track。
- 一个 canonical event 复制成两套独立、可分叉的事件对象。
- 为了 MTrk 保留而让音频按 Track 分 synth。

### 后果

来源链必须包含 Root/Track/Segment/direct object/ExportTrackId。增量编译等价测试需要同时比较两种投影、descriptor 与诊断。

## 6. ADR-PMIDI-005（已接受）：Root 级合成、checkpoint 与 PCM 缓存

### 决定

同一 Root 的全部 child Track 先按 Execution Projection 合并，进入一个 1-channel BASSMIDI stream。不得每 Track 独立合成后求和。Pure MIDI 缓存层为：

```text
normalized MidiSegment event fragment
→ Root merged event/checkpoint
→ pre-Master/pre-Limiter Root PCM blocks/pack
→ playback span
→ Render-Ahead ring
```

编辑只 dirty 所属 MidiSegment 与 Root 的最早 causal tick；完整 Root state、active Note multiset、allocation 和后缀依赖 hash 收敛后允许复用旧后缀。其他 Roots 与 Logical Units 不连带失效。

### 理由

共享 Channel state 使 per-Track PCM 缓存语义错误；Root 粒度是可以正确重放 Program/CC/Pitch/Note 的最小合成边界。Segment event fragment 和 checkpoint 仍保留局部编辑性能。

### 后果

Root PCM key 必须包含 composite/start-state/mode/SF2/Tempo/sample/native/voice/renderer fingerprints。Mute/Solo 中途变更需要在 producer frontier 原子重建 Root suffix，不能直接复用不匹配的 Root PCM。

## 7. ADR-PMIDI-006（已接受）：SMF 导入做语义规范化，不做字节 round-trip

### 决定

`Open MIDI as New Project` 支持 SMF 1.0 Format 0/1 与 TPQN `1..32767`，拒绝 Format 2、SMPTE division 和畸形/超限输入。读取器支持合法 Running Status，但 Project 不保存省略 status 的 wire 决定。

每个源 MTrk 维护 effective MIDI Port，并按 `(Port,Channel)` 拆分 Channel Events；一个多 Channel MTrk 不被拒绝。Track Name/Port/EOT 进入结构，Tempo/Time Signature/Key Signature/Marker 进入 Conductor，其他合法 SysEx/Meta 以 opaque payload 保存且不得因拆分而复制：每个 source MTrk/effective Port 的 opaque 内容归属到同 Port 首个派生 Track；没有 Channel bucket 时建立 structure-only Track，并确定性挂到该 Port 最低既有 Root 或新的 `Fixed(Port, Channel 1) / Melodic` Root。纯 Conductor 的 Format 1 MTrk 0 不制造空 Pure MIDI Track；其他需保留的空源 Track 使用 structure-only 规则。NoteOn velocity 0 规范化为 NoteOff；按源 MTrk/Port/Channel/key FIFO 配对，未配对消息保留为 raw direct event 并汇总 Warning。

导入边界负责把常见但不影响 SMF 结构可解析性的外部差异归一化为合法 Project：缺失 tick 0 Tempo / Time Signature 分别补 120 BPM / 4/4；同 tick Tempo 按 source MTrk index 再按 Track 内 event order 使用后来者；缺失、空白或非法 UTF-8 Track Name 不使导入失败，非法名称事件被丢弃，并在需要时使用一基 source MTrk index 的确定性回退名称。这些决定在 semantic validation 之前完成，不改变内部 Project 不变量。同值 Tempo 重复是 `Info`，值冲突是 `Warning`；其他兼容修复为 `Info`。结构化结果只属于当次导入任务，成功后以一份可复制报告呈现，不持久化。

导入构建 detached candidate，经过 Port Mapping Review 和完整验证后原子替换当前 Project；成功结果是使用源 TPQN 的未保存新 Project。不提供导入当前 Project。

### 理由

Running Status 几乎是必需输入兼容能力，但是否省略 status 不是音乐语义。多 Channel MTrk 是合法 SMF，拆分比拒绝更符合导入目标。原子新 Project 避免 TPQ 合并、Undo 和现有对象冲突的额外复杂度。

### 拒绝方案

- 拒绝 Running Status 或多 Channel MTrk。
- 保留完整源文件 blob并旁路领域模型。
- 自动缩放后导入当前 Project。
- 给未配对 Note 发明 gate 或静默丢弃。

## 8. ADR-PMIDI-007（已接受）：SMF Type 1 保留 Pure MIDI MTrk 拓扑

### 决定

每个输出文件的 MTrk 顺序固定：

```text
Conductor
→ selected Pure MIDI Tracks in Root/Track order
→ Logical Unit Tracks in Port/Channel order
```

Pure MIDI 每 Track 一个独立单 Channel MTrk，保留冻结名称、顺序、Port.Channel 与自身 EOT；同 Root 的多个 MTrk 共享 Port.Channel。Logical/Event Instrument 继续每实际 Unit 一个 MTrk。Pure MIDI 可写版本化、可忽略的 Midora Sequencer-Specific Meta 辅助精确 Root round-trip，但标准 Track Name/Port/Channel 始终是基础。

该 Pure MIDI 拓扑用于 Whole Project 与 Per Port；既有 Per Logical Track 模式继续只输出 Logical/Event Instrument，不能把 Pure MIDI Track 复制到每个 Logical Track 文件。单独导出 Pure MIDI Track 使用 Whole Project 的显式选择。

导出继续每个 Channel Event 显式写 status。Pure MIDI CC91/CC93、Channel Mode、Poly/Channel Pressure 与 opaque event 必须导出。Logical Channel 10 与 Melodic Root Channel 10 写 GS→XG Normal Part；Percussion Root 不写。

同 Root 跨 MTrk 的同 tick 顺序敏感组合产生一条 Root 汇总 Warning；不移动 tick、不合并 Track、不拒绝普通导出。字节一致、Type 0 保持和所有第三方播放器采用 Midora tie 顺序均不保证。

### 理由

用户跨软件工作需要 Track 名称与编排结构。把 Pure MIDI 数据再次合并为 Unit MTrk 虽然编码简单，却会破坏导入后结构并把 Midora 变成不可逆终点。

### 取代范围

本 ADR 取代 `ADR-CORE-010` 与 `Midora-MIDI-Export-Unit-Track-Requirement-Trace.md` 中“一切 Channel Unit 在同一文件只对应一个 MTrk”和“所有 MTrk EOT 相同”的全局表述。旧决定继续只适用于 Logical/Event Instrument Unit Track；Tempo、Bank、UTF-8、显式 status、事务和其他不冲突部分继续有效。

## 9. ADR-PMIDI-008（已接受）：破坏性开发格式与共享 UI adapter

### 决定

`.midora` 分别保存 `midi-channel-roots/mcr_<id>.pb` 与 `midi-tracks/mt_<id>.pb`，Segment/direct/raw/opaque 数据内嵌 Track。`project.json`、manifest、路径、对象 ID/type 和父子索引严格一致。当前未发布开发格式直接提升基线并拒绝旧格式，不实现迁移/双读/双写。

UI 不复制第三套 Timeline。Arrangement Segment、piano roll、Velocity、point lane、Grid/Snap、tile cache、hit testing 和 gestures 使用共享 presentation/interaction engine；Logical Segment、SubVoice 和 Midi Segment 通过 adapter 提供数据查询与原子命令。

### 理由

开发期兼容层会冻结尚未发布的错误模型并显著扩大测试面。共享 UI engine 可避免相同交互缺陷和性能修复在三套代码中分叉。

### 验证门

- strict JSON/protobuf schema、descriptor/golden bytes、损坏占位和确定 Zip；
- 旧开发格式明确拒绝；
- 三种 piano roll adapter 的共同行为/性能契约测试；
- 数百万 direct notes/events 的可视区域渲染、命中测试和编辑提交基准。

## 10. ADR-PMIDI-009（已被 ADR-CORE-046 取代）：Root 纳入 mixed Arrangement parent

### 决定

该旧决策曾规定 mixed parent tree，现由 `Midora-Flat-Arrangement-and-Shared-Usage-Architecture-Decisions.md` 的 ADR-CORE-046 取代。当前 Root 不占 Arrangement 行、不保存 child order；Pure MIDI Track 通过 Root ID 表示共享 Channel Unit，global mixed Track order 是唯一可见顺序。标准 SMF Track Name 仍只使用 Track 名称；版本化 Midora Sequencer-Specific Meta 可保存 Root stable ID/routing/mode 与成员关系，用于 Midora→MIDI→Midora 恢复，普通 MIDI consumer 可忽略。

当前没有可见 Root subtree clipboard。Pure MIDI Track Copy/Cut 后 Paste 创建新 Track 与新稳定 ID；需要独立 route 时建立新 Auto Root，加入既有 shared route 时由明确成员命令处理。只有 drag/move 保留 Track stable ID。Shared Root brace 与 Track 各自有 runtime Mute/Solo；Logical Note 与 Direct Note 跨剪贴板只转换共同字段，Direct NoteOff velocity 在 Direct→Direct、canonical SMF projection 与 export 中保留。

Pure MIDI Segment preview 的 non-Note event 使用独立缓存层，绘制在 Note 上层且固定 50% opacity；它只表达可视摘要，不改写 Root execution order。Conductor preview 使用另一个按 type 聚合的 point tile layer。

### 理由

唯一 global Track order 消除 parent/child tree 与 SMF 源 Track 顺序的冲突；标准 Track Name 尊重跨软件工作流，私有 Meta 保留 Midora Root 共享身份但不污染普通结构。独立 overview layers 使极端 MIDI 内容可见时仍保持 UI 性能，又不把 bitmap 变成领域模型。

### 后果与验证

- 更新 project JSON/protobuf descriptor/golden bytes，旧开发布局明确拒绝；
- Root allocation/export tests 使用 filtered mixed order；
- deep-copy ID/reference remap 与 Fixed→Auto 回归；
- Shared Root/Track Mute-Solo 快速切换与 Root state recovery；
- Direct NoteOff velocity round-trip；
- event-above-note 50% screenshot golden、tile invalidation 与百万事件基准。

## 11. ADR-PMIDI-010（已接受）：Out-of-core source pack、paged canonical 与 range-query UI

### 决定

Pure MIDI 的领域层级和语义不变，但大量 Direct records 不再由 `List<class>` 作为唯一物理表示。每个 Pure MIDI Track 持有一个 immutable source page pack；每个 Midi Segment 以 page ranges + copy-on-write overlay 表达 Note/Event/opaque 内容。默认 page 上限为 65,536 records 与 4 MiB decoded payload，任一先到即封页；默认 decoded source-page LRU 为 64 MiB。Stable ID lookup 使用 page ID bounds 与小型 overlay index，不建立全 Project ID dictionary。

`.midora` 使用小型 `midi-tracks/mt_<id>.pb` + 单 Track `midi-content/mt_<id>.mpk`。后者内部有版本化 header/directory、deterministic compressed pages、per-page checksum 与 footer；不按 page 建 Zip entry。打开时顺序复制 pack 到 session backing file后释放原 Zip；保存时流式合并 base + overlay 到新 pack，并参与既有临时包—自校验—原子替换事务。本次仍是开发期破坏性格式，不读旧 direct-record-in-protobuf 布局。

SMF 导入固定两遍流式解析：第一遍只冻结结构、Conductor、Port/Channel bucket、EOT 与 metadata plan；第二遍直接进行 FIFO Note pairing并写 transactional page builders。禁止 `File.ReadAllBytes`、完整 parsed graph、全量 paired-order set和导入后第二份 Pure MIDI 深拷贝。

Canonical对Pure MIDI保留延迟range source、aggregate metadata与source-aware fingerprint；Execution/SMF/audio各自从同一正式source生成最多16,384 records的紧凑consumer pages。非有序source通过131,072-record固定sort run、session临时文件和最多64-way多轮merge形成逻辑总序，不建立全Project canonical arrays。Project compilation snapshot分享immutable source page root并冻结overlay generation。UI preview/piano/Velocity/event lanes直接使用tick/pitch/target range query与page-local LOD，不建立全Segment render-item arrays/indexes。

### 理由

当前逐对象 source、deep snapshot、pending/final canonical arrays、audio projection copies 与 full Timeline snapshot 会把同一 MIDI record同时放大为多份 80～224 byte 托管记录。其峰值与总事件数线性增长，7M Note 已达到约 10～20 GiB，并使 164M 样本不可行。仅扩大 array/IPC 限制既不能控制内存，也不能解除远处事件对播放启动的阻塞。

Page pack 保留 SSD 顺序吞吐优势，同时把常驻内存约束为活动 FIFO、页面缓存、可见 tiles 与编辑 overlay。单 Track 一个 pack避免 page-per-file；immutable base + COW 使 snapshot/Undo不复制未修改内容。

### 拒绝方案

- 只提高 `MaximumImportEventCount`、MDAP 256 MiB或16M event limit；
- 把 `DirectMidiNote` 改成较小 class/struct但仍全量复制到 canonical/UI/IPC；
- 一个 Segment 一个巨大 protobuf repeated field并用 `ParseFrom` 整体反序列化；
- 每 page/Segment 一个本地文件；
- 仅对 bitmap 做 tile cache，而 source snapshot仍全量对象化。

### 后果与验证

已引入source pack codec、流式importer、延迟paged compiler result、range cursor、bounded external sorter、UI data provider与破坏性persistence golden。大规模操作仍可能按用户明确选择读取整个范围，但必须顺序分页并有进度/取消，不能回退到一次性数组。详细trace与实测数据：`misc/Midora-Extreme-MIDI-Scalability-Requirement-Trace.md`。

## 12. ADR-PMIDI-011（已接受）：播放端点索引与有界中途状态恢复

### 决定

`MIDMPK3` v3在原始Note/Event/Opaque source pages之外，确定性写入每Segment局部有序的NoteOn endpoint、NoteOff endpoint和Channel Event endpoint pages。原始source page继续使用最多65,536 records或4 MiB decoded的双重上限；endpoint page固定最多16,384 records。每个NoteOn endpoint page另保存该页Note的最大end tick，组成用于active-note查询的页级interval index。Reader只接受v3，旧开发期v1/v2直接拒绝，不提供迁移或运行期回退扫描。

Track内按tick查询先用目录bounds裁剪相交endpoint pages，再对page-local sorted runs做`PriorityQueue`有界k-way merge；跨Track/Root继续由canonical merge保持正式same-tick key。中途起播的Channel状态按每16,384个Channel Event建立checkpoint，恢复时只扫描最近checkpoint后缀；active Notes只解码满足`minimumStart < cursor < maximumEnd`的NoteOn endpoint pages并在页内二分start边界，不触碰普通Note page，也不从Segment起点重扫全部历史Note。

### 理由与边界

原始Note page的bounds同时覆盖最早NoteOn与最晚NoteOff。一个长Note会使同一页在大量250 ms窗口内持续“相交”，导致重复解压、扫描和外部排序；中途起播也会从巨型Segment起点回扫。端点索引用可控的pack空间放大换取顺序读取、稳定producer吞吐和有界冷启动。索引是source records的确定性物理派生物，不进入音乐语义、fingerprint或编辑身份；损坏时必须拒绝pack，不能静默使用慢路径掩盖损坏。

验证覆盖跨endpoint page同tick顺序、NoteOn/Off边界、Channel checkpoint前后等价、active-note恢复且不读取普通Note page、正常source page上限保持65,536、v1/v2拒绝、1M/18M/164M样本常驻内存与启动窗口查询。

## 13. ADR-PMIDI-012（已接受）：外部 SMF 已建模文本采用 UTF-8→Windows-31J 兼容解码

### 决定

导入 Track Name 与 Marker 时先使用无替换字符的严格 UTF-8；失败后使用固定 Windows-31J（Microsoft code page 932）严格解码。不得依赖当前 Windows 区域设置或系统 ANSI code page。Windows-31J 成功结果转换为普通 Unicode Project 文本并汇总报告 `Info`；导出仍按正式严格 UTF-8 编码，因此不承诺文本 Meta 的原始字节 round-trip。

两种解码都失败时，不把结构合法的 SMF 判为损坏：Track Name 丢弃并进入既有确定性名称回退，Marker 丢弃并汇总报告 `Warning`。其他未建模文本 Meta 不解码，继续以 single-owner opaque payload 原样保存。

### 理由与边界

SMF 1.0 没有为历史文本 Meta 固定现代 Unicode 编码，大量日本制作环境实际写入 Shift-JIS/Windows-31J。把这类可选显示文本当作结构错误会拒绝本可安全导入的音乐数据；使用机器区域设置又会破坏确定性。固定 CP932 fallback 可覆盖目标兼容面，同时让无法可靠解释的文本只局部损失。该决定不放宽 chunk、VLQ、Running Status、数值 Meta 或事件边界校验，也不改变 `.midora`、canonical、播放或音频语义。

### 验证

内存导入与两遍流式 `ImportFile` 路径都覆盖日文 CP932 Track Name/Marker、严格 UTF-8 优先级、不可解码 Marker 局部丢弃和诊断分级；结果 Project 必须可通过正式 semantic validation / compile。opaque 文本 Meta 的原始 payload 回归保持不变。
