# Pure MIDI 范围编译正确性修复

日期：2026-09-09。状态：实现及工程验证完成；2026-09-09 用户确认 MEM-C01～C07 全部验收通过。3157 项公共回归、三轮真实 MIDI 前后实测及当前构建的 WPF 大会话证据、额外时间/内存成本见 [验证报告](Midora-Pure-MIDI-Range-Correctness-Repair-Validation-2026-09-09.md)。

## 授权与需求追踪

用户批准将阶段 6 暴露的编译缺陷作为一个独立阻塞修复阶段实施，一次交付验收。实施时保留已有阶段 6 改动，不提交、推送或发布，不使用 computer-use；后续已验收并提交、推送。Int64 当时暂缓，当前已由[后续独立实施](Midora-Int64-Diagnostics-and-Bounded-Readme-Design.md)取代，不再是本修复遗留的阻塞项。

- 输入：冻结 Project、正式 Track 顺序/身份、路由、content window 和 CompileContext；分页布局不是音乐输入。
- 输出：完整 canonical 事件、起点状态、终点清理、Source/SMF 归属、排序及准确计数；Full/Incremental 与 Save/Open 等价。
- 边界：不重触范围前 NoteOn；同 Channel/key 按 FIFO 释放；子 Segment 不重置 sibling 状态；消费者提前结束必须完整清理。
- 依据：SRS §12.4、§12.10.7、§12.11.7、§12.21、§12.25、§23.6～23.10；INV-009/010/015/025/050/051/065～071。
- 持久化：不修改 Project Format、source pack wire、SRS 或音乐工作流。新增索引仅为可重建、修订绑定的运行时派生物。
- 非目标：不改 Logical 音乐语义、音频算法、SoundFont、设备、Mute/Solo、编辑/渲染交互。
- 失败：取消、I/O 失败、过期修订不得发布部分索引/结果；临时数据必须有明确 owner、预算及释放；不以吞异常、截断事件或全曲物化修复。

## 实施方向

1. Pure MIDI 使用同一范围投影规则；小源可保留 resident 输出，但不得因 backing 改变状态、生命周期或元数据。Logical 范围处理保持独立。
2. 分离普通窗口事件与小型状态/生命周期描述，正式事件枚举保持有界。终点 FIFO 不能以 gate-active 的对象身份代替：gate-active 可用于纯成对消息的数量摘要，但剩余来源须取正式 NoteOn 序列的 FIFO 后缀；raw Note 消息另需处理前缀下溢。
3. 优先复用 immutable source 的页目录、端点与修订快照；索引只保存有界工作页及必要摘要，不建立全曲第二份 SourceReference/Note 对象图。冷构建、热查询、编辑失效分别计时，不把首次构建藏在热查询报告中。
4. 原有独立范围反证与消费链测试保留；扩展跨 Track、不同 crop/page layout、raw Note、准确计数、取消和源释放验证。

## 验证与验收

分别验证小源、分页源、混合源的全范围、非零起点、提前终点、自然端点、Root/子 Segment 边界和重复状态事件；保留全部 canonical 字段比较。真实大型样本只在进程树守护下串行测试，硬上限 8 GiB、系统余量至少 2 GiB。报告编译/查询耗时、分配、Private/WS、索引驻留与释放。实现后补充实际算法、预算、结果和未覆盖风险，不能把本文候选当作已验证成果。

## 实际实现

### 1. 一套 Pure MIDI 投影

删除旧的 Pure MIDI 全物化、历史状态与边界归属实现，全部 Pure MIDI 使用共同投影。小源仍可即时冻结为 resident canonical 数组，大源仍返回分页 source；Logical 的实例/Mapping/范围算法不改。不是把小源也改成长期持有全量分页缓存。

Root 起点恢复按活动连通区间、正式 Track/event 顺序取各目标最近值，包含已经结束的兄弟 Segment，不补发范围前 NoteOn。默认 Reset 与真实状态按既有 reset-first 顺序恢复，每个目标最终只恢复一次。各子 Segment 精确释放自己的原始 Note/未配对 raw Note，但不提前复位共享 Channel。

最终消费者 end 必须补齐 FIFO 剩余实例的 NoteOff、CC120 与已污染目标 Reset。自然结束和消费者提前截断分别处理；原始 Direct NoteOff 恰好位于 end 时，保留其原始 velocity。原始 event/opaque 的相对顺序不因分页而变化。

### 2. 计数不以 UI 命中查询代替端点索引

`CountNoteStarts` 使用已存在的 NoteOn 端点页目录：完全被范围包含的页直接加 RecordCount，仅边缘页做二分；COW 的删除、替换、增加按值修正。NoteOff 数使用起点计数和边界活动计数计算，避免为了统计一个短范围而遍历全部普通 Note。

活动对象只能给出成对 Note 的**数量**，不能给出跨 Track FIFO 的正确剩余来源。终点来源从正式排序的 NoteOn 后缀按 key 获取，向前按 1024 ticks 起步、倍增窗口扩展，使用有界排序；若 note gate 交错，仍保留真正 FIFO 剩余的来源 ID。

未配对 raw Note 的处理分三层：

1. 尚在 end 之后的 raw 消息不能触发任何前缀索引构建。
2. 仅重放 raw 消息及其 Segment 关闭；若 raw 自身发生下溢，用普通 Note 端点索引检查该精确事件顺序之前是否确实有普通 Note 活动。若不存在，raw 与普通成对端点可独立计数；这有明确的非负前缀证明，不是忽略错误消息。
3. 若 raw Off 会消费普通 Note，改为对该 Root 的正式合并端点构建精确有界前缀计数索引，含同 tick 顺序与下溢。缓存只保存计数摘要，冷构建可能扫描很长前缀；这部分成本不能从热查询报告中隐去。取消时不发布半份索引。

该设计不宣称任意敌对 raw 数据的第一次 seek 都是常数时间，也不宣称源页在任意极端交错布局下都只读少数页。它避免普通文件因无关 raw 消息退化，并保留异常原始序列的精确语义。

### 3. SMF / Source / 缓存身份

SMF 统一从 canonical 的同一 Track 投影编码；小源/分页源不再各自重排一遍生命周期事件。生成的状态事件保留唯一稳定排序键，明确 role 优先级。

终点剩余音符的来源 Track 可能早已结束。按 SRS 23.7.5，最终生成 NoteOff 的 `Source.TrackId/DirectMidiObjectId` 仍指原 FIFO 实例，但 `ExportTrackId` 指向该边界最低顺序的参与 Track；不能为了放置清理事件延长已经结束 Track 的 EOT。

Pure MIDI fragment 身份包含完整活动区间的共享源、Reset Defaults、crop、COW 内容和顺序，包括中途起播所依赖的早期兄弟状态。范围 canonical 身份复用该摘要并加入范围与物理分配；不重复散列同一份完整编辑 overlay。Preset 预备元数据同样保留那些早期来源。

内部缓存 ABI 更新为 `MIDORA_PURE_MIDI_AUDIO_FRAGMENT_V4` 与 `MIDORA_PURE_MIDI_CANONICAL_RANGE_V2`，防止旧错误投影产生的 PCM/范围缓存复用。**不修改软件版本、Project Format 3 或 source pack wire 格式**；旧缓存自然 miss，不删除用户工程。

### 4. 内存预算与生命周期

| 数据 | 上限 / 释放 |
| --- | --- |
| Root 终点计数 | 每 Root 128 个 int64，随 canonical source 释放并纳入 retained-storage 计账 |
| raw 前缀缓存 | 每 Compiler 最多 4096 个 checkpoint，每内容键最多 1024 个；每个 checkpoint 为 128 个 int64 + tick，LRU 淘汰整项；换 Project、ClearCache、Dispose 清空 |
| 前缀索引构建 | staged checkpoint 同样限制 1024，取消/失败不发表；不持有 Project、Source、decoded page 或文件 handle |
| 外部排序 | 每 run 最多 131072 条，最多 64 路归并，输出页最多 16384 条；临时 wire 112 bytes/条，正式对象大小不能等同 wire 大小；结束/取消销毁临时 owner |
| 端点归并游标 | 只强持有单个 Current 值、页标识与位置；页数组为弱引用，由既有 decoded-page LRU 管理，回收后可重读；不再让各路游标锁住全部解码页 |

前缀缓存是 Compiler 内部固定预算，不重复计入每个 canonical/result lease；其条目数量门和 ClearCache 门有专项测试。正式历史、源页目录、真实输出和 COW 内容本身仍有必要成本；这些限制不是“整个程序固定内存上限”。同一 Root 的逐 Track SMF 输出仅由终点 owner 计算一次终点来源，不为每个 sibling 重做后缀查找。

### 5. 回归中发现的测试设施问题

`BoundedLogicalParameterMigrationTests` 的一个 fixture 创建了 Document History 却没有 Dispose，随后直接删除仍被历史 lease 使用的 spill 目录。改为 `using ProjectDocumentSession`，按实际 Workspace 所有权顺序清理；保留全部音乐/Undo 断言，不以重试删除绕过文件锁。

中间回归还出现过一次既有表达式零分配断言、一次 Conductor dispatcher 时限断言失败；未因此修改表达式或 UI 产品逻辑。最终回归需无其他大型探针并行，结果及是否仍有失败另列验证报告，不能把这些中间运行标成通过。
