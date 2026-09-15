# Midora Pure MIDI Tracks and SMF Import Requirement Trace

状态：2026-08-20 已按平铺 Arrangement / 非空内部 Root 破坏性修订；WPF 实机交互与真实外部 MIDI 兼容矩阵待产品验收
日期：2026-08-18（2026-08-20 更新当前结构）
上位规范：`misc/Midora-SRS-Initial-Release-v0.1/23-Pure-MIDI-Tracks-and-SMF-Import.md`、`24-Arrangement-Hierarchy-and-Preview.md`

## 1. 输入

- 完整 Project，包括 Conductor Track、global mixed Logical/Pure MIDI Track order、内部非空 MIDI Channel Roots、Event Instrument Definitions/Usages、两类 Segment、Project Settings 与单一 Project SoundFont。
- 纯 MIDI 数据源包括直接 MIDI Note、完整 MIDI 1.0 Channel Voice Event、导入后只读保留的 opaque SysEx / Meta Event、显式 Track 顺序与 Segment 暴露范围。
- SMF 导入输入仅限 Format 0 / 1、TPQN division；读取器必须支持 Running Status、MIDI Port Meta 与单个源 MTrk 内的多 Channel 数据。
- 外部 SMF 可缺失 tick 0 Tempo / Time Signature，可在同 tick 包含多个 Tempo，Track Name / Marker 也可使用严格 UTF-8、传统 Windows-31J/CP932 或含不可解码 payload；这些差异只在导入边界归一化，不成为 Project 源数据的宽松表示。
- 多 Channel/Port 源 MTrk 的 opaque 事件必须恰好保留一次；无 Channel 内容但需要保留结构的源 Track 使用确定的 structure-only Pure MIDI Track，不虚构 Channel Event。
- 编译请求仍显式携带 `[startTick, endTick)`、Track 选择、用途与 Warning-as-error 策略。

## 2. 正式输出

- 纯 MIDI Track 直接形成 canonical Channel Event，不经过 Event Instrument、Mapping 或 Logical Parameter 解释。
- 每个参与编译的非空 MIDI Channel Root 确定对应一个 Channel Unit；同 Root 的所有 Pure MIDI Track 在执行投影中按 `tick → global Arrangement Track order → Track 内事件顺序` 合并。
- Canonical Compiled Result 同时保存确定的 Unit 执行投影和 SMF 输出 Track 拓扑；播放、音频渲染和 MIDI 导出仍只消费 canonical。
- SMF 导出保持 Pure MIDI Track 的独立 MTrk、名称、顺序和自身 EOT；Logical 内容继续按 Channel Unit 组织 MTrk。
- Whole Project 与 Per Port 保持 Pure MIDI Track 拓扑；既有 Per Logical Track 模式只输出 Logical/Event Instrument。单独导出 Pure MIDI Track 使用 Whole Project 的显式选择。
- SMF 导入创建一个新的、尚未保存的 Project；不合并到当前 Project。
- 导入 candidate 在验证前已显式补全必需的 tick 0 Conductor 状态、对重复 Tempo 实施确定的后来者优先、按 UTF-8→Windows-31J 解释已建模文本，并为不可用 Track Name 产生非空回退名称；不可解码 Marker 被局部丢弃而不阻止其余内容导入。
- Arrangement 对 Pure MIDI Segment 组合 Direct Note layer 与位于其上、50% 透明度的 non-Note event layer；Conductor 第一行组合按类型着色的 point layer。

## 3. 边界

- 一个 MIDI Channel Root 是共享 Channel 状态、Unit 路由和音频硬边界；子 Track Segment 的并集活动连通区间构成一次 Root Channel 生命周期。
- 子 Segment 结束只精确关闭自身活动 Note；只有 Root 活动连通区间结束、Project End 或消费者范围结束才执行 Root 级 CC120 / Reset。
- 同一 Pure MIDI Track 内 Segment 不得重叠；不同 Track（包括同 Root）可以重叠。
- Root 路由为 `Auto` 或 `Fixed(Port, Channel)`；固定 Root 先预留，含参与 Segment 内容的 Auto Root 按最早成员 global order 分配，Logical Usage 资源分配必须绕开全部 Root Unit。
- MIDI Channel Root 可为 `Melodic` 或 `Percussion`。Logical/Event Instrument 路径继续按 melodic 语义使用其获配 Channel 10；导入的 Channel 10 Root 默认是 Percussion。
- Pure MIDI Project 源数据、canonical 与 MIDI 导出允许完整 Channel Voice Event，包括 CC91 / CC93；Event Instrument SubVoice 的创建入口仍保持既有受限事件面。音频后端继续使用 `BASS_MIDI_NOFX`，CC91 / CC93 不产生 Midora Reverb / Chorus 听感。
- SMF 导出继续显式写 status byte，不启用 Running Status；导入—导出不承诺字节级、Running Status 或原始 chunk 布局一致。

## 4. 失败条件

- SMF 不是 Format 0 / 1、使用 SMPTE division、结构损坏、VLQ/长度越界、Running Status 非法、事件截断或存在无法安全界定的 chunk。
- Track Name / Marker 的文本编码不可解码不是结构失败；只按确定兼容规则局部丢弃。未建模文本 Meta 作为 opaque payload 保留，不进行编码验证。
- Port 映射后仍超出 16 Ports、Root 总数或 Root 加 Logical 峰值需求超过 256 Units、固定 Root 路由重复、Root/Track/Segment 引用断裂。
- 同一 Pure MIDI Track 内 Segment 重叠、Segment 时间范围非法或直接事件数据越出 MIDI 1.0 wire 值域。
- 编译、导出、保存或导入事务失败时不得提交 partial Project、partial canonical 或 partial 输出文件。

## 5. 诊断

- 导入诊断必须定位到源文件、MTrk index、absolute tick、effective Port / Channel 与事件偏移；格式错误导致整个导入失败。
- 缺失默认状态、冗余同值 Tempo、Windows-31J 文本、非法/空白 Track Name 与回退命名记录 `Info`；同 tick 不同 Tempo及不可解码而被丢弃的 Marker 记录 `Warning`。UI 在成功原子提交后显示一份汇总、可复制的导入报告。
- 未配对的 Note message 以可保留的原始事件导入并产生汇总 Warning，不得静默删除或虚构 Gate。
- Pure MIDI Root 内跨 MTrk 的顺序敏感同 tick 组合不阻止编译；SMF 导出时按 Root 汇总为非阻塞 Warning，说明外部播放器的跨 Track 顺序可能不同。
- CC91 / CC93 在 Pure MIDI 数据、canonical 和 MIDI 导出中不产生 Warning 或 Error。

## 6. 持久化归属

- MIDI Channel Root、Pure MIDI Track、Midi Segment、直接事件、global mixed Track order、Track→Root membership、路由模式、Channel Mode、opaque imported event 和 Midora Stable ID 属于 Project 源数据。
- `.midora` 使用独立 Root 与 Track protobuf 对象文件，并由 `project.json` 的 global Arrangement Track tagged union 建立严格索引；Root 的相对次序从最早成员 Track 派生，不保存第二套 child order。旧开发期布局不构成本次破坏性修订的兼容读取承诺。
- Canonical、SMF 导入解析状态、兼容归一化报告、Root merge checkpoint、PCM cache、诊断、导出 Track plan 和 UI 会话状态不进入 `.midora`。

## 7. 运行时归属

- Shared Root 与 Pure MIDI Track 的 Mute/Solo 相互独立且只属运行期，按 SRS 24.5 计算；不改变 Project、canonical、MIDI 导出或音频文件渲染。
- 音频按 Root/Unit 使用一个共享 MIDI Channel 状态流；不得把同 Root 子 Track 分别合成后再求和。
- 缓存分为 MidiSegment 事件片段、Root merge/checkpoint、Root raw PCM tile、playback span 与 Render-Ahead ring；编辑只从最早 dirty tick 向后失效到状态 hash 收敛。
- Sequencer-Specific Meta、冻结导出拓扑、输出路径和跨 Track 兼容 Warning 只属于导入/导出任务或 canonical 派生数据。
- Segment Note/Event tiles、Conductor point tiles、LOD aggregation 和空间索引只属于 UI session；不得参与 canonical 或 bitmap hit testing。

## 8. 明确非目标

- 不支持 SMF Format 2、SMPTE division、MIDI 2.0 或导入当前已打开 Project。
- 不承诺 SMF 字节级 round-trip、第三方软件显示 Midora Root 文件夹层级，或第三方播放器严格遵守 Midora 的跨 MTrk 同 tick 顺序。
- 不开放自由 SysEx 创建/编辑；导入的 opaque SysEx / Meta 仅允许查看、移动归属、保留和重新导出。
- 不把 Pure MIDI Track 转换为 Logical Track/Event Instrument，也不把 Logical Track 隐式降级为 Pure MIDI Track。

## 9. 验证门

- Format 0/1、单/多 Channel MTrk、Running Status、MIDI Port 中途变化、无 Port、边界 VLQ、损坏 chunk、SMPTE/Format 2 拒绝。
- 缺失 tick 0 Tempo/Time Signature 补全、同 tick Tempo 的跨 MTrk/同 MTrk 后来者优先、同值/冲突诊断分级、UTF-8 与 Windows-31J Track Name/Marker、双重解码失败后的局部丢弃/回退，以及未建模文本 Meta 的 opaque 原始保留。
- opaque single-owner、纯 Conductor MTrk 0、非 Conductor 空 Track 与 structure-only Root/Track round-trip。
- 固定 Root 冲突、Auto 分配、Logical 绕开 Root、256 Unit 边界、Channel 10 Melodic/Percussion 投影。
- Root 活动连通区间、同 tick Segment 交接、子 Segment Note 精确关闭、Root 最终 Reset、同 Root 跨 Track 重叠 Note FIFO。
- Full/Incremental 等价、集合乱序输入、Track reorder 改变正式顺序、dirty-range 状态收敛与 Root PCM cache 命中。
- Pure MIDI Track 名称/顺序/EOT、Logical Unit Track、Midora Sequencer-Specific Meta、explicit status、跨 Track warning 和重新导入结构恢复。
- global Track order 派生 Root order、最后成员离开时删除 Root、Shared/Track Mute-Solo、Direct NoteOff velocity 与跨类型 Note clipboard。
- event-above-note 50% preview、Conductor point preview、可视 tile/局部失效与极端内容性能。
- `.midora` 严格 schema、descriptor/golden bytes、Root/Track 损坏隔离、确定性 ZIP 与破坏性开发期格式拒绝。

## 10. 实施结果（2026-08-19）

- Domain/Application 已实现 MIDI Channel Root、Pure MIDI Track、Midi Segment、Direct Note/Channel Event、opaque imported event、global mixed Track order、Track→Root membership、Root/Track/Segment 编辑、跨类型 Note clipboard 与原子 share/detach/reorder。
- SMF reader/importer 已实现 Format 0/1、TPQN、Running Status、Track 内动态 MIDI Port、多 Channel MTrk 拆分、explicit Port Mapping、FIFO Note 配对、未配对 Note 原样保留、opaque single-owner、Conductor 提取以及 Midora 私有 Meta 的可忽略结构恢复。
- Compiler/canonical 已实现 Fixed Root 预留、参与作用域的 Auto Root 分配、Logical 绕开 Root、Root 内 Track 总序、活动连通区间、子 Segment 精确 Note 关闭、Root 硬边界 Reset、独立 SMF Track descriptor、来源追踪、统计与 fingerprint。
- MIDI Export 已实现 Pure MIDI 独立 MTrk、名称/顺序/自身 EOT、Whole Project/Per Port 拓扑、显式 status、Midora 私有 Meta、跨 MTrk 同 tick 兼容性 Warning；Per Logical Track 明确拒绝 Pure MIDI Track ID。
- Playback/Audio 已把同 Root 成员 Track 合并到同一个 1-channel Unit stream，并实现 Shared Root/Track 独立 Mute/Solo、Melodic/Percussion Channel Mode 与 NOFX 下 CC91/CC93 的音频投影过滤；canonical 与 MIDI 导出仍原样保留这些事件。
- Persistence 已直接改写开发期 v1 schema/protobuf/package 结构，不提供旧开发期布局迁移或双读；Root/Track 文件、global Track index、损坏隔离、deterministic round-trip 与 descriptor/golden 均有回归。
- WPF 已提供 `Open MIDI as New Project`、Port Mapping review、平铺 Arrangement、Fixed route property、Auto Shared brace、Track 创建与编辑、Pure MIDI Segment Piano Roll/Event Lane、MIDI Track 图标，以及独立 Note/Event/Conductor tiled preview。

自动证据：`midora-core.slnx` 996/996、`midora-midi.slnx` 20/20、Desktop 60/60 + Presentation 123/123；重新发布当前源码的 `win-x64` Native AOT Worker 后，`Midora.Audio.Bass.Tests` 213/213、`Midora.AudioRender.Tests` 36/36。以上均为 0 failure、0 skip；Desktop 本轮因正在运行的 Midora 锁定默认 Release 目录，改用独立 artifacts 目录完成同源码构建和 60/60 回归。未执行 computer-use 或人工 WPF/听感验收。

仍需持续加固但不属于已知实现缺口：更多第三方 SMF 样本、极端大文件的交互性能、真实用户 Port Mapping 流程，以及不同外部 MIDI 软件对跨 MTrk 同 tick 顺序与私有 Meta 的处理差异。
