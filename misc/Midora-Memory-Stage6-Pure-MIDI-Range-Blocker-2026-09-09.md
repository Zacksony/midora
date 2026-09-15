# 阶段 6 阻塞记录：Pure MIDI 分页范围与 materialized canonical 不一致

日期：2026-09-09。本文件保留**发现时的阻塞证据和候选方案**，不改写历史失败。

后续状态：用户已授权独立修复，原两项失败 oracle 保留并通过，最新公共回归 3157 / 3157。实际算法、预算、三轮大样本代价和最终复验以 [修复设计](Midora-Pure-MIDI-Range-Correctness-Repair-Design.md) / [修复验证报告](Midora-Pure-MIDI-Range-Correctness-Repair-Validation-2026-09-09.md) 为准；下文“尚未批准”“Compiler 未改”等均描述本记录最初建立时的状态，不是当前待决策项。

本记录是分析与候选设计，不修改 SRS，不代表候选索引已实现或性能已验证。问题由阶段 6 连续消费链回归暴露，根因位于既有 Pure MIDI 双编译路径，不是本轮四项内存清理引入的生产修改。不得删除、跳过失败测试或缩减 canonical 字段来使阶段 6 通过。

## 1. 已执行证据

### 1.1 独立范围反证

测试：`PureMidiRangeLayoutEquivalenceTests.EarlyConsumerEndClosesSharedRootNotesAndResetsStateInBothSourceLayouts`，源码为 [PureMidiRangeLayoutEquivalenceTests.cs](../src/midora-core/Midora.Compiler.Tests/PureMidiRangeLayoutEquivalenceTests.cs)。测试不依赖 full canonical 的相等断言先通过。

输入为两个内容、稳定 ID、Arrangement 顺序相同的 Project：一个保留小型对象源，一个将同源记录写入小型 `PureMidiContentPack` 后挂接。共同夹具：

- TPQ=480；一个 Fixed/Melodic Root，zero-based Port=1、Channel=9；两条共享 Root 的 Track，各一个 `[0,1920)` Segment。
- Track A：Note key=36、`[0,960)`、NoteOn velocity=100、NoteOff velocity=37；CC11@240=55。
- Track B：Note key=38、`[360,840)`、NoteOn velocity=90、NoteOff velocity=45；CC91@400=99；opaque Text@420=`range`。
- Project Reset Default CC11=23；正式 `Playback` 编译范围 `[300,600)`。
- 双方完整读取 `QueryEventPages(300,600,includeStateAtStart:true)`，不是读取可能只包含 resident 部分的 `Events`。

2026-09-09 单独受守护 Release 构建成功；唯一测试 **Failed**，测试进程汇总 927 ms（TRX 单例 duration 0.903 s）。无其他测试并行。守护正常完成，exit=1、stopReason=null、178 samples；构建与测试进程树峰值 Private=263,970,816 bytes、WS=420,859,904 bytes。守护限制为 8 GiB Private / 2 GiB 系统余量；它不是新产品预算。

| 项目 | materialized | paged |
| --- | ---: | ---: |
| 声明 `TotalEventCount` | 11 | 12 |
| 完整范围查询事件数 | 11 | 6 |
| 声明 `TotalNoteOnEventCount` | 1 | 2 |
| 实际范围内 NoteOn 数 | 1 | 1 |
| tick=600 终止事件数 | 6 | 0 |

materialized 的独立预期断言全部通过；paged 在独立 Note 序列预期处失败：实际只有 NoteOn@360，没有预期的两条 NoteOff@600。计数与其余六条差异在断言之前已完整 dump，不是根据未执行断言推测。

paged 缺少的 tick=600 整组正式事件：

| 消息 | 正式角色 / 来源 |
| --- | --- |
| NoteOff key=36 / velocity=0 | `NoteOff`；Track A / 原 Note ID；`CompilerBoundaryCleanup` |
| NoteOff key=38 / velocity=0 | `NoteOff`；Track B / 原 Note ID；`CompilerBoundaryCleanup` |
| CC120=0 | `RootBoundaryCleanup`；Track A；`MidiChannelRootLifecycle` |
| CC11=23 | 同上，恢复 Project Reset Default |
| CC91=0 | 同上 |
| CC121=0 | 同上 |

起点也有差异：materialized 有三条恢复（CC91=0、CC121=0、CC11=55）；paged 有四条（CC121=0、CC11=23、CC91=0、CC11=55），多出 CC11 默认值，并且恢复顺序、`Source.Origin`、`StableOrder` / `SemanticGroup` 不同。这不是单纯声明数错误。上述事件数只计 canonical MIDI channel events；opaque 是独立 SMF 投影，不应额外算成此处第七条 channel event。

证据文件：

- [独立反证 TRX / 完整双方事件 dump](../.tmp/memory-stage6/compiler-range-proof-r1/results/compiler-range-proof-r1.trx)
- [进程资源守护结果](../.tmp/memory-stage6/compiler-range-proof-r1/guard/guard-result.json)
- [构建和测试输出](../.tmp/memory-stage6/compiler-range-proof-r1/guard/child.stdout.log)

### 1.2 连续消费链先发现的 full metadata 差异

`MemoryStage6ConsumerChainTests.MixedEditsReachColdWarmSeekPlansMidiExportAndEditedPackageRoundTrip` 的 `consumer-chain-r4` 已通过前半段小工程编辑、Full/Incremental、三个采样率计划、缓存复用/驱逐重访、监控隔离和原工程 MIDI 导出；在修改态 Save/Open 后 full canonical 对照失败。63 个事件中前 60 个相等，最后三个 tick=1920 的 Pure Root 清理消息、顺序、Source 相同，但 order/group 编码不同：materialized order 从 `4611686018427387915` 开始，paged 从 `2305843009213695871` 开始。

该次没有越过断言执行重开后的 MIDI 编码与范围比较；不能把它们写成通过。证据：[consumer-chain-r4.trx](../.tmp/memory-stage6/consumer-chain-r4/results/consumer-chain-r4.trx)。

早一轮 `application-stage6-r3` 的 Direct Note role 查找错误及 F10 的 10 分钟 debounce 均为测试夹具问题，已经窄修；本记录的两项生产差异不应与这些夹具修正混为一谈。F10 改用正式默认 75 ms debounce 和 60 s `Ensure` deadline；其两例及八例 cleanup 在 r3 已通过。

## 2. Requirement trace 与正式边界

| 项目 | 约束 |
| --- | --- |
| 输入 | 同 Project source / 稳定身份 / global Arrangement order / routing / CompileContext；backing、页切分、缓存状态不是音乐输入 |
| 正式输出 | 完整 canonical 消息、次序、角色、状态恢复、来源、SMF 归属与计数一致；Full/Incremental 一致 |
| 范围 | `[startTick,endTick)`；非零起点恢复非 Note 状态但不重触旧 NoteOn；consumer end 必须精确 NoteOff 与 Reset；恰等于 endTick 的真实 Direct NoteOff 保留原 velocity，不重复补发 |
| 共享状态 | Pure Root 跨 Track 聚合；Segment End 不能重置仍活动 sibling 的共享 Channel 状态；同 key NoteOff 按正式 FIFO 配对 |
| 失败 / 诊断 | 校验、取消、预算/磁盘失败、过期修订不得发布半成品；不能静默丢 Note/Source、钳制事件数或以全曲扫描隐藏查询回归；具体新增诊断代码待设计，不在本文虚构 |
| 持久化 | 不变更 Project Format 3 或 SRS；候选索引是可重建工作数据，不能进入 `.midora` 的 source/presentation 内容 |
| 运行时 | 准备/编译线程构建与查询；明确 owner、共享、lease、cache 独占字节及失效；不进入实时音频活动线程 |
| 非目标 | 不改 Logical 语义、声音合成算法、SoundFont/设备、Mute/Solo 规则、文件格式、用户工作流或原生后端 |

依据：SRS §12.1.2、§12.4、§12.10.7、§12.11.7、§12.21.2；§23.6、§23.6.4、§23.7.1～23.7.5；INV-009/010/015/025；[内存执行计划](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md) §5/§11 的不同页边界下语义及形式一致门。

`StableOrder` / `SemanticGroup` 是公开 `CanonicalMidiEvent` 字段，不是磁盘定位符：参与 canonical 比较、state group 恢复/折叠和 result fingerprint。SRS 不规定具体整数编码，但同正式输入不能因 backing 改变编码。因此保留完整字段 oracle，不删字段、不归一化。

对 full 尾部仅 numeric 差异，SMF channel projection 不携带这两个字段，普通 audio fragment fingerprint 也不写它们；仅此差异预计不改变该夹具的导出字节。这是源码推断，不是重开导出实测。materialized/paged 音频计划本身还采用不同 fingerprint 编码路径，不能把跨 backing cache-key 差异都归因于 numeric 字段。独立反证已确定真实 channel 事件缺失；尚未执行 PCM/设备验收，不声称已实测具体可听故障。

## 3. 已确认根因与旧测试空白

1. `MidoraCompiler` 的 materialized Pure 路径先生成 Root interval lifecycle，再进入通用 `ApplyRange`；后者裁范围、恢复状态、按 `(Port,Channel,key)` FIFO 补终点 NoteOff/Reset。`AssignPureMidiRangeBoundaryOwnership` 仅重标 Role/Source/SMF owner。
2. paged Pure 路径不经这个范围处理。`BuildPureMidiPlan` 对 Root interval 只做相交过滤、不裁 interval end；`PureMidiPagedCanonicalSource.AppendLifecycleRange` 只在自然 interval end 输出终止组，consumer end 提前时没有相同终止组。`AppendRootRangeState` 又独立编码起点恢复。
3. 两路终止 metadata 分别使用 `long.MaxValue/2` 全 Unit sequence 和 `long.MaxValue/4+interval.EndTick` Root-local sequence；仅统一数字不能补上缺失的正式事件。
4. paged `CountEvents` 使用 Segment 内容总数等摘要，未对应实际消费者窗口投影，产生本夹具声明 12/2、实际 6/1 的差异。

定位文件：[MidoraCompiler.cs](../src/midora-core/Midora.Compiler/MidoraCompiler.cs)、[PureMidiCompilation.cs](../src/midora-core/Midora.Compiler/PureMidiCompilation.cs)、[PureMidiPagedCanonicalSource.cs](../src/midora-core/Midora.Compiler/PureMidiPagedCanonicalSource.cs)、[Contracts.cs](../src/midora-core/Midora.Compiler/Contracts.cs)。

既有 `PagedPureMidiCompilationTests.MaterializedAndPagedDirectEventsShareOverflowSafeCanonicalOrdering` 只比较带 Direct object ID 的 `DirectMidi` 事件；`NonzeroRangeDoesNotRetriggerActiveDirectNoteInEitherStorageMode` 也只比较带 Direct identity 的自然 Note 端点。它们没有覆盖 Root 生命周期，不能用其通过来否认本缺陷。

## 4. 为什么不能用现有 gate-active 查询直接修补

同 Root/key：A=`[0,100)`，B=`[10,20)`。tick=20 的 NoteOff 在 MIDI FIFO 中关闭最早 A；tick=30 的剩余 FIFO 来源是 B。现有 `QueryActiveNotes(30)` / `QueryActiveValues(30)` 按原 gate 相交返回 A。活动数量虽同为 1，补发 NoteOff 的来源却不同，跨 Track 归属也可能错误。

此外冷启动不重触旧 NoteOn，不代表可以删除前缀 FIFO：既有 `ApplyRange` 仍用完整已见 NoteOn/Off 的 FIFO 选择结束清理来源。范围前旧 Note 的 NoteOff 可影响范围内同 key 新 Note。必须以现有正式行为和 SRS 为 oracle，不能为索引便利改音乐规则。

“活动 FIFO 是已见 NoteOn 序列的后缀 K 个”是可用的查询方向，不意味着每次必须 replay 全 Root。可是 K 不能简单取 totalOn-totalOff：SRS §23.6.4 允许未配对 raw NoteOff，空队列 off 不消耗未来 NoteOn。候选摘要必须处理 prefix underflow（例如 sum/min-prefix 的可组合计数摘要）并合入 paired 与 raw 端点、Segment 截断事件及正式跨 Track tie order；Source 后缀选择还需要 rank/reverse 查询。任何硬清理对摘要的处理必须与正式生成事件及既有 FIFO 行为一致，不能擅自把 CC120 解释为清空源码配对队列。

## 5. 底层索引可行性与候选模块

### 5.1 已有能力与不能直接保证的条件

`IPureMidiPlaybackEndpointSource` 目前仅有 Note starts、ends、原 gate-active、ordered Channel Event 查询，没有 per-key prefix count、rank/reverse 或 FIFO checkpoint。

`PureMidiContentPack` 每 Note 保存 Note 记录及两种 endpoint 记录；endpoint 页最多 16,384 条、普通 decoded page 有 4 MiB 上限。目录提供 RecordCount、min/max tick、active-end、min/max key、lane mask、ordinal/ID 范围等，没有每 key 的精确计数或排序位置。endpoint 页内排序，但页间 tick 区间允许重叠。

`PageRangeIndex` 利用 min/max prefix 做候选页剪枝；`QueryNoteEndpoints` 先解码各候选页，将保有整页数组的 cursor 加入优先队列后归并。对历史完整前缀直接调用它，不能保证只解码边界少量页，也不能用解码 LRU 的额定上限掩盖被 cursor 持有的数组。

仅新增每 endpoint 页 128 个 uint 的 key 直方图：按 18,000,000 Note、每页 16,384 条粗估，两种 endpoint 页共约 1.07 MiB 原始直方图数据（未计对象/目录/稀疏页开销）。它能跳过完全位于 cut 前的页；若任意布局下所有页都跨 cut，则仍需读取每个部分相交页。这个估算不是完整索引预算，也不是最坏 seek 上界证明。

### 5.2 可选实现方向，尚未批准编码

候选为单一 Pure Root 范围投影，Logical `ApplyRange` 不动，小型对象源保留小源快路径、不强制写 pack。分页底层增加细粒度 per-key prefix/rank/reverse 目录，或准备阶段生成等价的有界外存排序侧索引。按正式 tie order 聚合 Root 时，不持有全量 endpoint/value/source 对象图。

预计模块不止两处 compiler 方法：

| 模块 | 所需工作 |
| --- | --- |
| `Midora.Domain/PureMidiContentPack.cs` | endpoint 计数/选择的内部索引；页边界与任意页间交错；受限 cursor/decoded bytes；兼容现存 pack 的构建途径 |
| `Midora.Domain/PureMidiPagedContent.cs` | 正式查询 capability、snapshot 与 COW overlay/exclusion 合并；删除/移动/改 key/裁 Segment 后的计数与来源修正 |
| `Midora.Compiler/PureMidiCompilation.cs` | 同一 Pure range 语义、Root 聚合及小源路径；精确 Source/SMF owner 和边界职责 |
| `Midora.Compiler/PureMidiPagedCanonicalSource.cs` 与共享 helper | 初始化/restore/end 的唯一 metadata 编码、准确窗口计数、索引 owner 与 checkpoint/rank 查询 |
| 对应 Domain/Compiler/Application/Playback/MidiExport 测试 | 页布局、COW、取消/释放、canonical→plan→MIDI→Save/Open 及性能反证 |

侧索引可以只落可重建工作目录而不改 Project 格式；是否调整内部 pack 格式、是否由 pack source 共享细索引再按 Root 聚合，须在实施前写清版本/兼容和 lifetime。不可静默更改 `.midora` 持久化契约。

### 5.3 生成阶段、时间与空间成本

- 构建在 immutable source / revision 冻结后的准备阶段，不能每次 Seek 扫历史全曲。新 pack 可评估随端点编码积累摘要；旧 pack 至少需一次 O(N) 读取，首次构建成本要单独展示，不能藏成“冷查询”。
- 每 key 全序/rank 若不能由目录直接得到，还要排序或归并；现有页内有序可以复用，但需有界 fan-in、多趟外排，而不是一次保留所有页 cursor。只宣称 O(N) 读取不代表总 CPU/I/O 是 O(N)。
- 目标查询成本是索引定位 + 有上界的边界块 + 必要的活动后缀 K / 窗口事件 + 跨 Track tie 归并；K 本身可以很大，输出与工作页必须分批流式，不能要求全量 Source queue 常驻。
- 计数目录可按页数增长；细 rank/ordinal/tick 目录最坏可能按 N 增长，宜外存。具体记录宽度、索引字节公式、fan-in、工作页与 resident cache 默认值未定，须先做小/大源工作量测量再记录设计选择。
- 既有 64 MiB 解码/编辑预算、128 MiB 每 compilation session 的三族准备缓存预算不是本索引的免费额度，更不是进程总上限。新增工作区、共享索引、双 revision 交接、活动 cursor/lease、metadata 与磁盘 live bytes 必须分别计账；只能在有证据时复用/分配既有预算，不叠加隐形缓存。

## 6. 失效、失败原子性与释放要求

这些是候选实现必须满足的验收条件，不是已实现事实：

1. source backing identity、内容 revision、COW delta/exclusions、Segment start/window、Root membership、Track 正式次序/routing 变化必须使受影响索引失效或精确局部更新；不能以名称、list index 或旧 fingerprint 冒充身份。
2. 多次范围和不同消费者复用 immutable 索引；共享块按 identity 计一次。新 revision 成功前保留旧 reader；替换/淘汰不释放仍有 active lease 的块；baseline/cache/active 的并集分列。
3. staging 只能使用本工程正式可重建工作目录中的 owned 子目录（候选 CompilerRuns 或 SessionContent，由最终 owner 决定）；固定预算下复用 extent，不能重复失败后无限追加。不得读取旧 `%LOCALAPPDATA%/Midora`。
4. 构建、归并、前缀/窗口查询每个有界 block 检查 cancellation；取消、I/O/校验失败、过期 revision 不发布部分目录/结果，精确移除本任务 owned staging，不损伤 Project source、旧文件或在用读者。
5. 索引损坏必须可重建或明确失败，不用丢事件/跳页回退。预算不足优先受控 spill/不进入历史缓存，不把缓存准入变成原合法 Project 的新限制。
6. 编译器/session Dispose、Project 关闭、cache clear、失败和取消后，索引文件 handle、块 lease、decoded arrays、checkpoint/root descriptor 释放可观测；不依赖强制 GC 才成立。

## 7. 修复准入与出口门

### 7.1 正确性

- 保留本文两项失败测试的完整 oracle；当前独立反证不得被 full metadata 断言先挡住。直接比较小夹具明确预期消息、Source、计数，再做两 layout 对照。
- full、nonzero start、early end、自然 NoteOff 恰在 endTick；NoteOff velocity=0/127；Root/Segment/消费者同 tick 边界不得重复清理；opaque 原 payload/归属保持。
- shared Root 跨 Track 状态恢复、已结束 sibling 的共享 CC、跨 Track 同 key 交错端点、gate-active 与 FIFO 来源不一致、raw 未配对 Note、冷启动旧 off 与新 on；不能只验证 key 数量。
- 相同 tick 的 off/on、显式 order 重复和稳定 ID tie；乱序输入、不同 pack 页切分/全部页跨 cut；同输入重复、Full/Incremental、Save/Copy/Open。
- 小 Logical/Loop/PreRoll/映射与 Pure 共存，正式 range→sample plans→MIDI 导出；不以 resident `Events` / `Ports` 的部分内容当全量，不改音频算法补 compiler 漏项。
- fault injection：构建/归并/发布/查询取消，损坏索引、磁盘失败、旧 revision、COW 大 delta、cache 驱逐中的 active reader；确认不发布部分结果和最终释放。

### 7.2 时间与内存

- 小对象源前后等工作量测量，不能强制写大 pack 或引入重 builder；同时记录首次编译、首次非零 Seek、热范围、驱逐后重访。
- 真实已授权大型样本及合成最坏页布局串行受守护执行，冻结样本/revision/OutDir；记录源码页读取数、decoded/cursor 字节、构建/查询时间、分配、Private/WS、自然 GC 与关闭释放。
- 证明 Seek 不随距 Root 起点增加而扫描完整前缀；证明所有页 tick 重叠时不会一次保留全部 endpoint 数组。源规模相关的必要索引磁盘容量与单查询常量工作集分别报告。
- 新旧 revision + undo/history + clipboard + WPF + active plans 共存的 owner 并集计账，不用单层 LRU 或小测试替代；大样本取消与重复开始/关闭后不累计目录或 handle。
- 具体性能容差、工作页数/默认字节预算必须在候选实现前后数据中冻结；目前没有可据实承诺的加速倍数或全进程固定 GiB 上限。

## 8. 发现时的决策与交付边界（历史记录）

已向用户提出：A）单列阻塞修复阶段（建议），本轮完成已冻结内存清理与公共回归并保留失败；B）本轮继续扩展，接受新增 Domain/Compiler 索引设计、实现、性能及完整语义回归范围。**截至本记录写入，用户决定尚未收到。**

没有找到可立即复用、同时保证正确 FIFO 来源与有界 Seek 的小索引；这不等于算法不可实现，也不意味着必须每次全 Root replay。阶段划分是待决策项，原有硬边界/FIFO/确定性契约不是待重新选择的音乐规则。

当前只新增反证测试与分析文档；Compiler 生产未改。测试槽已释放，不再运行测试；后续公共回归由主任务串行安排并如实保留失败。在修复及相应验证完成前，不能宣称阶段 6 整体通过。
