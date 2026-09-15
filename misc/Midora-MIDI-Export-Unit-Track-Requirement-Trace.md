# Midora Logical/Event Instrument MIDI 导出 Channel Unit / MIDI Track Requirement Trace

状态：历史增量已实现；**全局范围已由 2026-08-18 Pure MIDI 决定取代，现仅适用于 Logical/Event Instrument Unit MTrk**
日期：2026-08-15

2026-08-18 范围修订：本文下文的“一 Unit 一 Track”、Port→Channel 排序、`Port <P> / Channel <C>` 名称与统一 EOT，只约束 Logical/Event Instrument 输出。Pure MIDI Track 必须按 `misc/Midora-Pure-MIDI-Tracks-and-SMF-Import-Requirement-Trace.md`、SRS 第 23 章与 ADR-PMIDI-007，一用户 Track 一 MTrk；同 Root 多 MTrk 可共享 Unit，并保留 Track 名称、Root/Track 顺序和自身 EOT。若下文未显式写出限定词，`Unit` / `事件 Track` 均解释为 Logical/Event Instrument Unit / MTrk。

## 1. 输入

- 用途为 `CompilationPurpose.MidiExport`、成功、完整且可消费的 `CanonicalCompiledResult`。
- canonical 中每条 Channel Event 的原始 `ZeroBasedPort`、`ZeroBasedChannel`、绝对 tick、同 tick 顺序、消息与来源。
- Whole Project、Per Logical Track、Per Port 三种冻结导出模式，以及 Per Logical Track 的 owner 过滤和 Per Port 的原始 Port 过滤。
- 本次导出冻结的 Unit Track Name 布局；Channel Unit 身份固定为原始 `(Port, Channel)`。

## 2. 正式输出

- SMF Type 1 的 Track 0 仍为 Conductor / Meta Track。
- 每个 MIDI 事件 Track 严格只承载一个原始 Channel Unit，即一个 `(Port, Channel)`；同一导出文件内，同一实际有事件的 Channel Unit 严格只对应一个 MIDI 事件 Track。
- 一个 Unit 被不重叠的不同 Logical Track / Instance 先后复用时，其 canonical Channel Event 仍合并进入该 Unit 的唯一 MIDI Track；不因来源 Logical Track 不同拆成多个 MIDI Track。
- 事件 Track 按原始 Port、再按原始 Channel 的全局低号顺序排列。Per Port 文件先按原始 Unit 分组，再仅把 MIDI Port Meta 归一化为 Port 1；Channel status nibble 不改变。
- 事件 Track Name 固定为 `Port <P> / Channel <C>`，P/C 均为一基编号；名称描述资源路由，不冒充单一 Logical Track owner。

## 3. 范围与资源 / 文件边界

- Unit 分组只组织导出文件，不改变 Compiler 的 Channel Unit 分配、复用、占用区间、Reset、生命周期或 canonical 排序。
- 不生成完全无 Channel Event 的 Unit Track；Conductor-only 零长度或空音乐文件仍合法。
- Per Logical Track 仍按 owner 过滤 canonical 内容并每个 Logical Track 生成独立 `.mid`；文件内部再按该 Track 实际使用的 Unit 拆分。
- Per Port 仍只为实际有 canonical 事件的 Port 生成文件；原始 Port 继续记录在文件名、Track Name 与 Readme 中。
- Channel 10 melodic 的 GS→XG 初始化仍只写入实际 Channel 10 Unit Track，且每个相关 Unit Track 各一次。

## 4. 失败条件

- canonical 事件的 Port / Channel / status channel 不一致。
- Unit Track 布局缺失、重复冲突，或一个输出事件 Track 被构造为包含多个原始 Unit。
- Per Port 请求的 Port 没有 canonical Channel Event。
- TPQ、Tempo、文本、Channel Event、自校验或文件事务的既有失败条件保持不变。2026-09-10 对 VLQ delta 的严格拒绝已由 [SMF 导出边界决定](Midora-SMF-Export-Timing-Padding-and-Size-Limits-Architecture-Decisions.md) 取代：仅导出插入空 Text Meta；payload VLQ 仍严格拒绝。MTrk 不按大小拆分，数据区超 `0xFFFFFFFF` 字节仅导出失败。此新规则的产品代码/测试待实施，下文历史结果不证明其已通过。

任何失败均不得发布 partial `.mid` 或成功 Readme。

## 5. 诊断

- canonical 路由不一致继续归类为 `CanonicalConsistency`。
- Unit Track 布局缺失、冲突或一 Track 多 Unit 归类为 MIDI Export `Encoding` 诊断。
- 诊断尽量保留 canonical `SourceReference`；纯布局错误定位到原始 Port / Channel。

## 6. 持久化归属

- 不新增或修改 `.midora` 字段、Export Settings 字段、Undo / Redo、Modified 或 canonical fingerprint。
- SMF Track 组织与 Track Name 只属于导出产物；冻结布局和绝对输出路径只属于本次任务。

## 7. 运行时归属

- Unit 分组、Track 排序、命名、编码和读取后自校验属于 MIDI Export Preparing / Encoding。
- 不读取播放设备、SoundFont、音频 Worker、缓存命中、播放光标或 Mute / Solo。

## 8. 明确非目标

- 不把 Logical Track 改成 Channel Unit，也不改变 Project 的 Logical Track / Segment 模型。
- 不为了保留 Whole Project MIDI 内部的 Logical Track 分组而允许一个 Unit 重复出现在多个 MIDI Track。
- 不把多个 Channel Unit 合并进一个 MIDI Track，即使它们来自同一 Logical Track 或同一 Port。
- 不重分配、压缩、折叠、删除或新增 canonical Channel Event。
- 不承诺第三方编辑器理解 MIDI Port Meta、Channel 10 vendor SysEx 或非 GM SoundFont；本变更只保证每个事件 Track 的单 Channel Unit 结构。

## 9. 验证门

- 同一 Port 的两个 Channel 必须生成两个事件 Track，且每个 Track 的全部 Channel Event status nibble 唯一。
- 同一 Unit 被两个 Logical Track 先后复用时，Whole Project 文件中仍只有一个对应事件 Track，事件均保留。
- Whole Project、Per Logical Track、Per Port 均验证一 Unit 一 Track；Per Port 的 Port Meta 为 0、Track Name 保留原始 Port。
- Channel 10 初始化只存在于 Channel 10 Unit Track，顺序和次数保持不变。
- Track 顺序固定为原始 Port→Channel，不依赖 Logical Track、集合枚举顺序或编辑历史。
- 既有 canonical 消息顺序、统一 EOT、空文件、非零起点和失败原子性回归继续通过。
