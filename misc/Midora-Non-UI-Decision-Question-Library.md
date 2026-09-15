# Midora 初版非 UI 实施决定问题库

状态：Q-NUI-001～Q-NUI-028、Q-NUI-030、Q-NUI-034～Q-NUI-042、Q-NUI-049 的产品答复均已记录；Q-NUI-029、Q-NUI-031～Q-NUI-033、Q-NUI-043～Q-NUI-048 为已按推荐方案实施、待产品确认的小决定；新的 Segment/Unit 音频缓存与 underrun 恢复架构已无剩余大决定阻塞，主体实现完成并进入全量门禁；M-AUD-001～012 全部通过
创建日期：2026-08-06
最近更新：2026-08-16
关联台账：`misc/Midora-Non-UI-Implementation-Tracker.md`

本文只收录实施“全部非 UI 初版能力”过程中真正需要产品所有者决定、确认或修改的问题。SRS 已规定的事实以及 `misc/Midora-SRS-Code-Conformance-Audit-2026-08-05.md` 中已确认的 1A～25.1A 不重复登记。

## 1. 使用规则

- **小决定**：不同选择影响局部、兼容面小且容易修改。实现方先按推荐方案实施，同时登记完整依据、影响和替代方案；产品所有者随后确认或要求修改。
- **大决定**：会改变可听结果、持久化兼容性、公共接口、确定性、并发模型、发布形态或大范围工作流。对应分支在决定前不实施；不受影响的工作继续。
- 每个问题必须记录：SRS/源码依据、事实与不确定性、影响范围、推荐方案、备选方案、当前实施状态、产品回答和最终处理提交。
- 已回答问题保留原编号，不删除，作为决定审计链。

## 2. 产品答复与实施判定

本节是 Q-NUI-002～Q-NUI-028、Q-NUI-030、Q-NUI-034～Q-NUI-042 的当前权威答复。第 3 节保留问题提出时的完整事实、影响和备选方案；其中已经在本表回答的问题，即使原问题正文仍保留“待确认”“待填写”和“等待回答”字样，也以本表为准。未列入本表的新小决定仍以问题正文中的“已实施、待确认”状态为准。

| 编号 | 产品答复与实施判定 |
|---|---|
| Q-NUI-002 | 采用推荐方案：初版 v1 没有需要支持的历史格式；保留严格版本预检和空迁移注册表。 |
| Q-NUI-003 | 采用推荐方案中的字段、默认值、任务快照与原子 History 语义；版本处理改为直接修订开发期 v1，不创建 v2，也不创建 v1→v2 迁移。当前 v1 尚未冻结或发布，开发期变化不构成升级；版本只在冻结时确定。 |
| Q-NUI-004 | 采用推荐方案；现有本机 Preferences 实现获确认。 |
| Q-NUI-005 | 采用推荐方案：会话内 `nextStableId` 不回退，Redo 恢复原 ID，仅有已撤销瞬态分配造成的计数器空洞不单独保持 Modified。 |
| Q-NUI-006 | 采用推荐方案；现有 Last Known Instrument Name 维护时机获确认。 |
| Q-NUI-007 | 采用推荐方案；现有 Lane 重绑定取整、Enum 最近值和插值转换获确认。 |
| Q-NUI-008 | 2026-08-16 后续决定取代旧空链 sentinel：非空 Chain 删除仍需确认；Note 共享 Mapping owner 强制保留，非 Note Event Mapping 与 Logical Parameter Mapping 删除时物理移除 owner，缺少 owner 表示原始值直通。 |
| Q-NUI-009 | 采用推荐方案：Definition 结构编辑必须显式迁移并作为单个原子 History entry 处理全部引用 Lane。 |
| Q-NUI-010 | 采用推荐方案；空 SubVoice 保持静音目标且不产生额外 Info。 |
| Q-NUI-011 | 采用推荐方案，允许内部共享音频状态快照 ABI 在开发期独立升级。v2 引入 seqlock；Q-NUI-022 的 held Preview 控制引入 v3；Q-NUI-034～035 的完整恢复区间命令使当前版本升至 v4，并继续保持同一 header/seqlock。该 ABI 是内部版本号，因此不适用 Q-NUI-003 的外部 Project 文件开发期版本原则。 |
| Q-NUI-012 | 采用推荐方案；现有 SDK、NuGet lock 与发布门锁定策略获确认。 |
| Q-NUI-013 | 产品所有者放行正式包含 BASS/BASSMIDI/BASSWASAPI DLL 的分发。公开身份为 `Zacksony`；产品无销售、广告、订阅、付费分发或其他形式收入；初版为 Windows `win-x64`，未来可能多平台；通过 GitHub Release 发布；发布当日必须按官方条款重新确认免费资格；最终包必须同时包含供应商要求的原始许可文本和现有 notices。该答复记录发布主体与产品事实及产品放行，不替代发布当日外部条款核验。 |
| Q-NUI-014 | 采用推荐方案；单应用实例按 Windows 交互登录 Session 隔离。 |
| Q-NUI-015 | 采用推荐方案；Save Copy 目标与当前 Project 文件相同时直接拒绝。 |
| Q-NUI-016 | 采用推荐方案；缺失 metadata 时使用 package 打开事务注入的 UTC `TimeProvider` 当前值同时初始化恢复 `createdAtUtc`/`modifiedAtUtc`，并保留既定恢复 Error/Modified 语义。 |
| Q-NUI-017 | 采用推荐方案；只有实际进入不可逆关闭/切换边界后才暂停工程时长，失败恢复不补计暂停窗口。 |
| Q-NUI-018 | 采用推荐方案；Recent Projects 使用独立本机 MRU 文件与现有更新/去重策略。 |
| Q-NUI-019 | 采用推荐方案：Time Signature 变化 tick 立即开启新 Bar，允许前一小节缩短；另新增规则：每个发生在小节中途、因而截断旧小节的 Time Signature 变化都产生一条 Warning。 |
| Q-NUI-020 | 采用推荐方案；初版 Global Event Scope Defaults 保持不可编辑的版本化空 marker，作用域继续由各正式事件语义固定。 |
| Q-NUI-021 | 2026-08-07 的严格失败决定已于 2026-09-10 被取代：超长 delta 仅在导出时插入空 Text Meta；MTrk 不拆分，数据区超 0xFFFFFFFF 字节只令导出失败，编译不感知。规范已同步，代码待实施；见 ADR-SMF-001～002。 |
| Q-NUI-022 | 采用推荐方案；held Preview 使用因果 Gate、`Int64.MaxValue` 未结束哨兵和未渲染 frontier 生效规则。2026-08-07 进一步确认 Segment Editor 左侧 Pitch Ruler 琴键与单个 Logical Note 放置预览均复用该逻辑，并属于初版范围。非 UI 全链已经实施并通过进程内、托管 Worker 和 Native AOT Worker 自动回归。 |
| Q-NUI-023 | 选择备选 A：直接把开发期 v1 的领域、创建与 schema 合法范围收窄为 `1..32767`，拒绝高 TPQ v1。当前处于开发期，没有既有兼容承诺，不创建新版本或迁移。 |
| Q-NUI-024 | Midora 稳定 ID 的核心值改为单个 C# `long`；不再以 `Guid`、`UInt128` 或两个 `ulong` 承载。Project 范围内的持久化单调递增 ID 足够满足身份需求。 |
| Q-NUI-025 | 采用推荐方案：合法范围 `1..long.MaxValue`；JSON 使用 canonical 十进制 integer；对象文件名使用无符号、无前导零的十进制 ASCII；protobuf 使用标量 `int64` 并保留各外层字段号；直接重写开发期 v1 契约，不提供 128-bit v1 迁移器。 |
| Q-NUI-026 | 2026-08-16 最终可听语义由 ADR-CORE-043 固定：普通 Gate/Release/Tail/allocation-group 结束只执行必要的精确 NoteOff，不发送 CC120，也不做通用目标 Reset；Project Reset Defaults 在 Segment lane 首次激活或非重叠复用激活时建立基线，并在 Segment End、显式消费者范围结束等硬边界完成最终清理。CC120 仅用于这些硬边界。此前 BASSMIDI 的 192-frame 实测仍作为硬边界实现事实保留。 |
| Q-NUI-027 | 接受已实施方案：人工 Console 使用显式 `MIDORA_AUDIO_WORKER_PATH` 或标准 `win-x64/publish` Native AOT `.exe`，不存在时失败，绝不回退 managed `.dll`。 |
| Q-NUI-028 | 活动输出设备播放中被拔出或禁用时，断开当前输出并受控停止；主应用不得因清理异常崩溃，必须要求用户显式重新指定设备，完成选择前不得自动或静默切换到任何设备。非活动设备变化不影响当前播放。 |
| Q-NUI-030 | 采用推荐方案：直接收紧开发期 v1，要求 Project 中每个 Time Signature 都满足 `4 × TPQ % denominator == 0`；Domain、semantic validation 和 persistence 统一拒绝不兼容组合。按 Q-NUI-003 直接修改 v1，不创建迁移版本。现已实施统一 Project Time Signature Map、可逆 Bar:Beat:Tick、自然拍网格/Snap 和中途截断 Warning。 |
| Q-NUI-034 | 采用五层分离推荐架构：canonical range cache、Segment/Unit compiled fragment、Segment/Unit PCM、playback span、短 Render-Ahead ring。旧 ring-cap 推荐及 A/B/C 作废。 |
| Q-NUI-035 | 选择备选 B：若 underrun 位于小节中途，恢复区间包含当前小节剩余部分和下一个完整自然小节；若正好位于 Bar 起点，只取当前完整小节。总恢复区间上限由 8 个四分音符放宽为 16 个，播放范围剩余不足时以实际终点为界。当前实现解释为 16 四分音符限制整个 `[F,R)`；若极端拍号导致截断下一完整小节，上限优先。 |
| Q-NUI-036 | 采用每 Unit 单通道渲染语义 + 有界复用 Stream pool。sample-voice 值允许用户在设置中修改；变化后使全部音频 PCM/cache generation 失效。默认值由 750 改为 500；Realtime/Offline 的精确范围见 Q-NUI-042。 |
| Q-NUI-037 | 采用 session-scoped 磁盘后备 + RAM hot set，并修改容量失败语义：缓存位置、当前占用和上限对用户透明；用户可选择缓存磁盘/目录并设置程序级最大长期缓存大小，允许 0。达到配额或普通长期缓存写入空间不足时显示状态 Warning，停止写入新长期缓存并退化为每次实时重渲染，不因长期缓存不可写直接阻止播放。Q-NUI-035 所需临时恢复存储和默认路径/配额见 Q-NUI-041。 |
| Q-NUI-038 | 采用推荐的因果 Dirty tick：能够证明旧前缀等价时保留；不能证明时回退 Segment 有效起点。 |
| Q-NUI-039 | 采用推荐方案：物理设备改变但实际采样率/格式相同时保留 device-independent Segment/Unit PCM，只重建设备绑定状态、ring 和输出 generation。 |
| Q-NUI-040 | 采用推荐缓存边界：正式 Project 内容在主时间线、固定 Segment Preview 和 exact-key 离线渲染间复用 raw Unit PCM；草稿及未知 Gate Preview 使用 transient generation。 |
| Q-NUI-041 | 采用推荐方案：Application Preferences 的默认缓存根目录为 `%LOCALAPPDATA%\Midora\AudioCache`，默认 reusable 上限为 16 GiB，允许 `0..long.MaxValue` bytes，0 禁用 reusable cache；仅允许可写的本机绝对路径，拒绝相对路径和 UNC/network path。Transient Recovery Spool 独立于 reusable 配额并单独透明显示；若磁盘 spool 和预留 RAM 都不可用，则受控 Stop、保留失败 tick 并报告 `AudioRecoveryStorageUnavailable`。 |
| Q-NUI-042 | 采用推荐方案：Realtime 与 Offline 继续作为两个独立可编辑设置，统一语义为 `Maximum Sample Voices per Unit Stream`；新安装 Realtime 默认 500，新 Project Offline 默认 500。任一值只能在无活动音频任务时提交，并清除当前 Project/session 的全部 PCM/audio cache generations；tick-domain compiler/canonical cache 不失效。开发期 v1 直接修改默认，不创建迁移。 |
| Q-NUI-049 | 产品所有者要求 MIDI 导出避免单个 MIDI Track 包含多个 Channel：每个实际有事件的 Channel Unit（Port + Channel）在同一文件内严格对应一个事件 Track；同一 Unit 被不同 Logical Track / Instance 先后复用时仍合并为一个 Track。按原始 Port→Channel 排序，Track Name 使用一基 `Port <P> / Channel <C>`。 |

版本判定以产品答复为准：开发期尚未冻结的外部 Project 文件契约直接修订 v1；不得仅因为开发过程中的字段或范围变化创建 v2。内部 ABI 有独立生命周期，Q-NUI-011 明确允许升级。后续冻结时再确定首个正式版本的完整 schema、descriptor 与 golden 资产。Q-NUI-034～Q-NUI-042 已完整冻结新的缓存/underrun、运行时存储和设置边界。

### Q-NUI-049：MIDI 导出每个 Channel Unit 严格对应一个 MIDI Track

- 类型：文件兼容性语义变更；产品所有者已明确确认。
- 记录日期：2026-08-15。
- 产品回答：MIDI 导出时避免一个 MIDI Track 出现多个 Channel；将一个 Unit 严格对应一个 Track，因为部分 MIDI 编辑器不支持单 Track 多 Channel。
- 先前规格与源码事实：SRS §14.3 和编码器按 `Logical Track × Port` 分组，因此同一 Logical Track / Port 内的多个 Channel Unit 会进入同一个事件 Track；Track Name 为 `<LogicalTrackDisplayName> / Port <P>`。
- 实施判定：Channel Unit 继续定义为 canonical 原始 `(Port, Channel)`。同一文件内，一个实际有 Channel Event 的 Unit 只生成一个事件 Track，每个事件 Track 只包含该 Unit；Unit 被不同 Logical Track / Instance 在不重叠时段先后复用时仍合并。Track 顺序固定为 Port→Channel，Track Name 固定为 `Port <P> / Channel <C>`。Per Logical Track 先做 owner 过滤，Per Port 先做 Port 过滤，再应用同一 Unit 分组。
- 影响范围：SMF Track 数量、事件 Track Name、Whole/Per Track/Per Port 内部组织、Track 排序、Readme 兼容说明、编码公共契约和 golden / parser 测试；不改变 Project、canonical、Unit 分配、MIDI Channel status、可听语义、Export Settings 或文件命名。
- 明确取舍：Whole Project / Per Port 文件不再在事件 Track 层保持 Logical Track 拆分；这是实现“一 Unit 一 Track”且处理 Unit 跨 Logical Track 时段复用所必需的结果。Per Logical Track 模式仍通过独立文件保持 Logical Track 边界。
- 当前实施状态：SRS、ADR、requirement trace、编码器和公共命名契约均已修订；Core Release 构建 0 warning / 0 error，核心解决方案 901/901 测试通过，其中 MIDI Export 35/35。

### Q-NUI-024：`MidoraId` 改为单个 `long` 的稳定 ID

- 类型：大决定，已确认方向。
- 记录日期：2026-08-07。
- 产品回答：`Midora.Domain.MidoraId` 重构为纯粹以单个 C# `long` 为核心的 ID；Project 范围内单调递增 ID 已足够，不再使用 `Guid`、`UInt128` 或两个 `ulong`，以减少 ID 运算和序列化/反序列化转换。
- 实施前源码事实：`MidoraId` 曾声明为 `record struct MidoraId(Guid Value)`，通过高低 64 位与 `UInt128` 相互转换；`MidoraProject.NextStableId` 曾为 `UInt128`；Mapping ABI v1、持久化 codec、schema、protobuf、文件名、descriptor/golden 与大量测试依赖 128-bit 表示。
- 当前 SRS 冲突：第 8.52.2 节建议不少于 128-bit；第 16.5.3、16.11.2、16.13.2 节以及第 00 章固定 32 位小写十六进制文本和 protobuf `StableId { fixed64 high; fixed64 low; }`。产品答复改变了该基线，后续实施必须显式修订相应规格记录，不能把旧 SRS 描述继续当作有效要求。
- 保持不变的语义：ID 仍是 Project 内全对象类型共享的稳定身份；名称、位置、tick、Port、Channel 和 ID 数值大小都不构成业务排序；分配仍由 Project 负责，保持正值、持久化单调递增、不补缺、不复用和全局唯一；复制生成新 ID，Undo/Redo 恢复原 ID。`0` 与负值不作为合法稳定 ID；到达 `long.MaxValue` 后必须结构化拒绝继续分配。
- 影响范围：Domain 公共值类型和 Project allocator；所有引用、集合键、排序 tie-break 与 canonical fingerprint；Mapping ABI v2；JSON/对象文件名/protobuf v1、descriptor、golden bytes、迁移预检与损坏诊断；应用 History、Compiler、Playback、MIDI、Audio Render、持久化和全仓测试 fixture。
- 版本处理：依照 Q-NUI-003 与 Q-NUI-023 的开发期原则，后续实现直接修订尚未冻结的 v1 契约资产，不因本次变化创建 Project file v2；内部 Mapping ABI 按 Q-NUI-011 的决定升级。
- 当前实施状态：已完成。SRS 与 ADR-CORE-035 已同步；Domain/allocator、Compiler/fingerprint/source trace、Mapping ABI v2、JSON、对象文件名、protobuf v1、descriptor/golden、Playback/Audio plan 和全仓 fixture 均改为单 `long`。专项 Release 构建 0 warning/0 error；Compiler 218/218、Persistence 85/85、MidiRenderPlan 专项 9/9 通过，完整发布门将在后续非 UI 总门统一重跑。

### Q-NUI-025：单 `long` 稳定 ID 的 v1 精确持久化编码

- 类型：大决定。
- 状态：已确认并实施。
- 发现日期：2026-08-07。
- 已确认事实：Q-NUI-024 已确定内存核心和 Project allocator 使用正 `long`；Q-NUI-003/Q-NUI-023 已确定开发期外部格式直接修订 v1。仅凭“使用 long”仍不能唯一决定 JSON 是数字还是字符串、对象文件名格式，以及 protobuf 使用 `int64`、`sint64` 或 `fixed64`。
- 影响范围：`.midora` v1 的 JSON Schema、对象文件名、protobuf descriptor/golden bytes、严格读取与损坏诊断、确定性 ZIP bytes，以及其他语言或 JavaScript 工具读取超出 `2^53-1` 的 JSON 数字时的精度。
- 推荐方案：合法范围统一为 `1..long.MaxValue`；JSON 中稳定 ID 与 `nextStableId` 使用十进制 JSON integer，原始 token 必须匹配 `[1-9][0-9]*`，拒绝小数和指数写法；对象文件名 `<id>` 使用无正负号、无前导零的 invariant 十进制 ASCII；protobuf 将各外层现有 ID 字段直接改为标量 `int64` 并保留这些外层字段号，删除嵌套 `StableId high/low`；protobuf 对正数使用标准 `int64` varint；所有读取器拒绝 0、负值、非 canonical 文件名和溢出。直接重写开发期 v1 descriptor/schema/golden，不提供 128-bit v1 迁移器。
- 推荐依据与限制：这是与 C# `long` 最直接、转换最少的表示，典型递增小 ID 的 protobuf varint 也更紧凑。限制是通用 JavaScript JSON 消费者无法精确表示大于 `2^53-1` 的数字；Midora 的正式 .NET 读取器不受该限制，但第三方工具必须使用任意精度整数解析。
- 备选方案及差异：A. JSON 和文件名使用 canonical 十进制字符串、protobuf 仍用 `int64`；跨语言 JSON 精度更稳健，但保留文本解析。B. JSON integer、文件名十进制、protobuf 使用 `fixed64`；每个值固定 8 bytes，但语义是 unsigned wire 且典型小 ID 更大。C. protobuf 使用 `sint64`；正数需要 ZigZag，收益不成立且 wire 与常规 `int64` 不同。D. 保留嵌套 `StableId` 但只留一个字段；仍保留无必要的消息层和转换。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-07，采用推荐方案。
- 最终处理与提交：已与 Q-NUI-024 一并实施。JSON 使用自定义 source-generated 可用 converter 严格检查原始 numeric token，拒绝字符串、小数、指数、符号、前导零、零与溢出；protobuf 各 ID 字段保持外层字段号并直接使用 `int64`，三份 descriptor hash 与代表性 golden bytes 已重建；旧 high/low 和 Mapping ABI v1 正式消费路径已从源码移除。

## 3. 问题原文、影响分析与新增待确认问题

Q-NUI-026～027 是本轮新增问题；其后 Q-NUI-002～025 的状态、产品回答和最终处理字段保留为 2026-08-07 答复前的原始快照，当前决定以第 2 节为准。

### Q-NUI-026：Channel Unit 结束时如何终止 SoundFont 残余 release

- 类型：大决定；阻塞 M-AUD-002/M-AUD-005 的正式语义修复，其他工作不阻塞。
- 状态：已采用推荐方案 A；真实 BASSMIDI 最小对比、正式实现与自动回归完成，等待人工复听。
- 发现日期：2026-08-07。
- 人工与自动证据：SubVoice 验收例的四个实例范围为 `[0,900)`、`[960,1860)`、`[1920,2820)`、`[2880,3780)`。每个模板和弦在局部 tick 430 NoteOff，SF2 release 到实例结束仍可听；实例结束的 CC11 Reset 恢复到 127。48 kHz PCM 在 tick 900、2820、3780 后明显增大，在 CC11 已接近 127 的 tick 1860 没有同类增大，与产品所有者报告的第一、第三、第四和弦末尾突增精确一致。根因已确认，不再只是推断。
- SRS 依据：10.16.3 要求普通生命周期按“Release/Tail 完成 → 必要精确 NoteOff → Reset → Channel Unit 可释放”；10.16.4 允许 All Notes Off 作为 Reset/安全兜底但不得替代精确 NoteOff；12.10.4～12.10.5 要求 Reset 完成后才能释放或同 tick 复用 Channel Unit；14.14.3 明确列出 All Notes Off、All Sound Off、Reset All Controllers 可以属于 canonical 范围清理；14.14.4 禁止 exporter 在 canonical 外自行追加清理；15.1 禁止 audio consumer 重解释 Release/Tail/Reset。
- 当前实现的额外事实：Reset 目前在每个 Raw Instance 结束时生成。非隔离重叠实例会合并到一个 allocation group 并共享 Channel Unit；如果只在每个实例结束时新增 CC120，先结束的实例会杀掉同组仍在发声的实例。因此安全终止必须按 allocation group/Channel Unit 占用结束处理，并同时审计现有状态 Reset 的归属，不能只给人工示例打补丁。
- 推荐方案 A：在 canonical compiler 中把资源安全清理统一到每个 allocation group 的实际结束点。先完成该 group 所有精确 NoteOff，再发 CC120 All Sound Off 终止 SoundFont 内部残余 voice，然后输出现有 Project/Global Reset 状态，最后释放或同 tick 复用 Channel Unit。硬范围结束使用同一顺序。若作者希望保留更长尾音，必须在 Event Instrument 的 Release/Tail 生命周期中显式延长 group，而不能依赖未知 SoundFont 残余越过正式实例结束。MIDI 导出会忠实包含这些 canonical CC120。
- 推荐依据：该方案最直接满足“Release/Tail 在资源释放前完成”和消费者不得重解释 canonical；能确定性消除 Reset 放大旧 release，也能在 Channel Unit 复用前建立真正干净的 BASSMIDI voice 状态。清理基于 allocation group 而不是单实例，可避免杀掉合法重叠实例。
- 推荐方案的可听与兼容影响：所有正常 allocation group 结束点的 SoundFont 内部残余 release 会被硬终止；生命周期设置不足时可能听到截尾。Canonical fingerprint、MIDI 导出事件和 golden bytes 会变化，Full/Incremental、范围、重叠、同 tick 复用和全部消费者都需回归。这正是该问题必须先决定的原因。
- 备选方案 B：保持当前正式语义，只把人工示例的实例改成首尾相接，使旧 Reset 与新 Logical Parameter 在同 tick 折叠。它能让当前样例不突增，但不再覆盖有间隔时的真实失败，属于规避验收，不推荐。
- 备选方案 C：允许 SoundFont residual release 越过实例结束，并延迟到后端检测静音后再 Reset。该方案使 canonical/资源分配依赖 SoundFont、采样率与后端内部状态，破坏确定性及 MIDI/音频一致性，不接受。
- 备选方案 D：改变 CC11 默认 Reset 值、把 Reset 提前到 NoteOff 前，或只在 BASS consumer 中插入清理。它们分别会造成另一方向的音量跳变、违反已定生命周期顺序，或使 MIDI 与音频消费不一致，不推荐。
- 需要产品所有者回答：是否采用推荐方案 A？如果不采用，请在 B～D 之外明确说明希望保留的正常实例结束尾音语义，以及 Channel Unit 何时才算可安全释放/复用。
- 产品回答：2026-08-07，采用推荐方案 A。实施前先验证两点：CC120 是否真正截断 SoundFont 自带 release 并稳定保证目标 Channel 无残余音；CC120 是否只作用于对应 Channel 而不是全局。
- 实测结论：使用首轮人工验收同一 `test.sf2`、BASSMIDI `2.4.16.0`、48 kHz 对比。仅 NoteOff 的 release 到 24000-frame 测试缓冲末端仍非零；在 frame 6000 加入 CC120 后，最后一个非零 frame 为 6191，即 192 frames / 4 ms 的后端防爆音衰减，frame 6192 起严格全零。双 Channel 同一 Stream 中，目标 Channel 衰减结束后的 PCM 与“只渲染非目标 Channel”的参考逐字节相同，证明 CC120 不是 Stream/全局清理且未改变另一 Channel。官方 BASSMIDI 事件定义也将 `MIDI_EVENT_SOUNDOFF` 标为对应 MIDI controller 120、按 `chan` 参数作用的 Stop all sounds；实测而非文档推断作为本次实现门。
- 最终处理与提交：编译器已把状态 Reset 从单个 Raw Instance 结束迁移到实际 allocation group 结束；有发声 Note 的每个 group/Channel Unit 按精确 NoteOff → CC120 → 已使用目标 Reset 排序。非隔离重叠组只在最后实例结束时清理；相邻组同 tick 复用时旧组清理先于新组 Initial/NoteOn；硬范围结束使用同序；空 SubVoice 不发 CC120；MIDI exporter 仅忠实编码 canonical。完整发布门 873/873、0 Skip、六个 solution 0 warning/0 error，当前源码 Native AOT Worker 产物为 `artifacts/non-ui-release-gate-q026-20260807`。编译器、MIDI、真实 SF2 PCM 和进程内/Native AOT 子进程逐字节一致回归已通过，等待 M-AUD-002/005/008 人工复听后关闭可听门。
- 2026-08-16 第一次后续决定：产品所有者明确禁止在普通 Gate End 或 allocation-group 生命周期结束处发送 All Sound Off。该阶段先把 CC120 收窄到 Segment End、Project/显式范围结束等硬边界，并暂时保留普通结束的目标 Reset。上面的 2026-08-07 实测结论继续证明 CC120 适合硬边界，但原“每个 group 结束都发送”的适用范围已被取代。
- 2026-08-16 最终后续决定：产品所有者进一步选择 ADR-CORE-043 的 lane 激活模型。普通 Gate/Release/Tail/instance/allocation-group 结束只执行必要的精确 NoteOff，不再做通用目标 Reset；Project Reset Defaults 在 Segment lane 首次激活或非重叠复用激活时先于 Initial State、用户起点状态和 NoteOn 建立基线。共享同一仍重叠 allocation cluster 的后续 Gate 不重复 Reset；Segment End 和消费者范围硬边界仍按精确 NoteOff → CC120 → 最终目标 Reset 清理。CC64/CC66/CC69 等保持型状态因此会保持到作者显式事件、下一次 lane 激活或硬边界。

### Q-NUI-027：人工 Console 的 Native AOT Worker 路径解析

- 类型：小决定；已按推荐方案实施，等待产品所有者确认或修改。
- 发现日期：2026-08-07。
- 已确认事实：旧 Console 固定传入 managed Worker `.dll`，M-AUD-007～009/011/012 在正式播放前被保护门正确拒绝；仓库发布门和标准 `dotnet publish -r win-x64` 已能生成并校验 Native AOT `.exe`。
- 推荐并已实施方案：Console 优先读取仅供验收进程使用的 `MIDORA_AUDIO_WORKER_PATH`；未设置时解析标准 `Midora.Audio.Bass.Worker/bin/<Configuration>/net10.0/win-x64/publish/Midora.Audio.Bass.Worker.exe`。路径不存在时给出明确发布提示；绝不回退 managed `.dll`，也不从多个历史 `artifacts/non-ui-release-gate-*` 中猜选产物。人工清单在子进程测试前显式执行 locked restore/publish，并检查 `.exe`、manifest、MIT License 和 notices。
- 影响范围：只影响开发期人工 Console 和验收说明，不进入产品配置、Project、canonical、安装布局或正式 Worker 协议。正式会话原有 `.exe`、ABI、版本、hash、架构和握手校验保持不变。
- 备选方案：A. 每条 Console 命令增加 Worker 位置参数，显式但重复且容易在 007～012 间传错。B. 自动选择最新 release-gate artifact，结果依赖本机历史和目录时间，不确定。C. Console 自动触发 AOT publish，把构建副作用混入播放启动且耗时不可控。
- 需要产品所有者确认：已确认。
- 产品回答：2026-08-07，接受已实施的推荐方案。
- 最终处理与提交：已实现并获确认；4 项路径回归通过，标准 publish 路径与显式环境变量路径都已实际启动 Native AOT Worker 完成子进程离线渲染。Segment 子进程/进程内 WAVE SHA-256 均为 `7D3001050F0EE44B7C19B50A83AFC486EEE415E0EEDF469255F4FAF0219ADDFF`，两条链逐字节一致；完整非 UI Release 门 866/866、0 Skip、0 warning/0 error。

### Q-NUI-028：活动输出设备丢失后的受控断开与人工重选门

- 类型：大决定；产品所有者已直接给出正式行为。
- 发现日期：2026-08-07。
- 人工证据：M-AUD-012 中 Worker 已正确检测 `deviceLost=True`，callback、ring 和 renderer 均未报告故障，但 Worker 把设备丢失作为普通 `Faulted/fault=1/exitCode=1` 退出；主进程随后在 `PlaybackController.Stop()` 再次把该终态当作清理失败抛出，控制台出现未处理 `MidoraAudioException` 堆栈。拔出或禁用非活动设备不会触发该路径，符合预期。
- SRS 依据与补充：§13.14.4 要求不可用设备不得进入 Playing，§13.14.5 要求运行期端点变化停止、清理、丢弃 sample-domain 缓存并提示，§13.19.7 把设备丢失列为不可恢复播放错误；SRS 没有明确错误后的重选门和禁止自动切换规则，本决定补足该运行时工作流，不修改 Project 或 canonical 语义。
- 产品回答：活动设备丢失时主应用进程不得崩溃；至少要断开输出端并主动要求用户手动重新指定设备，不得自动静默切换。非活动设备变化不影响播放。
- 实施方案：共享控制 ABI v2 追加 `OutputDeviceUnavailable` 非故障终态。Worker 检测活动设备丢失后先 Dispose WASAPI 输出、停止 render-ahead，再发布终态并以 0 正常退出；不得尝试对已失效端点 flush。后端把该终态与普通 Worker Fault 区分；PlaybackController 受控进入 Stopped、释放编辑锁、冻结失败 tick、清除活动计划并使 sample-domain 缓存失效，同时设置 `OutputDeviceSelectionRequired`。所有 Start/Preview 在门关闭前抛出专用 `OutputDeviceSelectionRequiredException`；只有显式 `SelectOutputDevice(string?)` 能清除门并重建设备域状态。
- 不自动切换边界：设备丢失处理本身绝不枚举并挑选替代设备，也不把系统默认映射变化当作隐式选择。`SelectOutputDevice(null)` 只在用户明确选择“System Default”时允许；它不是设备丢失路径的自动回退。
- 失败边界：未知 Worker 异常、callback/ring/renderer fault、非零退出和损坏 IPC 状态仍进入 Error，不能伪装成设备重选请求。若设备丢失后的托管资源清理本身失败，控制器保留聚合错误并进入 Error，但仍保持重选门。
- 当前状态：实现、完整非 UI Release 门和 M-AUD-012 物理复测均完成；本问题已关闭。
- 自动证据：Playback 状态机覆盖无异常受控停止、`flush=false`、编辑锁/缓存释放、失败 tick、重选前阻止 Start、显式选择后恢复；IPC 覆盖 ABI v2 和合法非故障终态；Worker/Session/Backend 覆盖终态与 exitCode 组合；既有设备通知测试继续证明只有选中设备的 Disabled/Fail 才构成 DeviceLost。完整发布门通过 881/881、0 Skip、六个 solution 0 warning/0 error，当前源码 Native AOT Worker 位于 `artifacts/non-ui-release-gate-q028-20260807/worker-win-x64`。

### Q-NUI-029：运行时 MIDI Render Plan 临时文件版本随稳定 ID 改为 `long`

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-07。
- 已确认事实：`MidiRenderPlanFile` 是主进程为 Native AOT Audio Worker 生成的有校验和临时计划，不属于 `.midora`、canonical 持久化、MIDI/WAVE 输出或公开交换格式。旧内部版本 2 的 source table 每项保存 16-byte Guid；Q-NUI-024 后正式 source ID 已变为正 `long`，继续写旧宽度会保留无意义转换，原地改格式但不升级版本则会把旧文件误读为新布局。
- 推荐并已实施方案：内部计划版本从 2 升为 3，source table 每项固定写一个 little-endian signed Int64，并在读写时要求正值、唯一；版本 2 及其他版本一律拒绝，不提供迁移。主进程与 Worker 同源发布，计划文件只在单次任务准备/清理窗口存活，因此无需跨版本兼容。
- 影响范围：只影响运行时临时 MIDI Render Plan 的 byte layout、大小上限计算和自动测试；不改变共享控制 ABI（该问题实施时为 v3，当前为 v4）、Project file v1、Mapping ABI v2、canonical、可听语义或用户工作流。每 source 从 16 bytes 降为 8 bytes。
- 备选方案：A. 保持内部版本 2 但改变布局，会使旧文件被错误解释，拒绝。B. 版本 3 继续写 16 bytes（8 bytes ID + 8 reserved），保留无用空间且没有兼容收益。C. Worker 同时读取 2/3，需要恢复 Guid→long 映射且临时文件没有兼容需求。
- 自动证据：计划 v3 确定性往返、checksum、边界长度、正/负/零/重复 source ID、旧版本拒绝与 render monitoring source lookup 均有覆盖；专项测试通过。
- 需要产品所有者确认：是否接受推荐的内部计划版本 3？若需修改，请说明为何单次任务临时文件需要跨版本读取。
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-030：非整除 TPQ / Time Signature 下的整数 Bar:Beat:Tick

- 类型：大决定；已确认并实施。
- 状态：2026-08-08 采用推荐方案；Domain、Application、Compiler、Persistence、Project 音乐位置和 SRS 已同步。
- 发现日期：2026-08-07。
- 已确认事实：SRS 允许 `TicksPerQuarterNote = 1..32767`，Time Signature 分母允许 `1/2/4/8/16/32/64`；同时又要求 Bar/Beat 为整数、Beat 以分母音符为单位、Tick 为 0-based Project tick 偏移，并要求 tick 与 Bar:Beat:Tick 唯一、可逆。一个分母拍的长度是 `4 × TPQ / denominator` 个 Project tick。若不能整除，例如 TPQ=1、分母=64，则一拍只有 `1/16` tick，不存在同时满足上述约束的整数 Tick 坐标。
- 冲突性质：这不是实现精度问题。只要 Project tick 仍是最小整数时间单位，就无法用整数 `Bar:Beat:Tick` 唯一表示落在若干分数拍边界之间的 tick；四舍五入会造成重复坐标、不可逆或长期漂移，零 tick 拍也可能出现。
- 影响范围：Bar:Beat:Tick 显示/输入、拍与小节网格、Snap、Time Signature 中途截断判定和 Warning 的公共语义；若改变 TPQ/Time Signature 合法性，还影响 Domain、创建、语义校验、`.midora` v1 schema 和导入失败诊断。绝对 Project tick、canonical 编译、播放、MIDI 和音频消费本身不因此受阻。
- 推荐方案：直接收紧尚未冻结的开发期 v1：Project 中每个 Time Signature 都必须满足 `4 × TPQ % denominator == 0`。Domain 创建/编辑、semantic validation 和 persistence 读取统一拒绝不兼容组合；错误必须指出 TPQ、分母和事件来源。这样每拍始终包含正整数 Project ticks，既定整数 Bar:Beat:Tick 可唯一往返，中途 Time Signature 截断 Warning 也能精确定义。该变化按 Q-NUI-003 直接修订 v1，不创建迁移版本。
- 推荐依据与限制：这是保持现有整数 tick、既定 BBT 语法、可逆性和无漂移网格的最小一致方案。限制是部分单独合法的 TPQ 与分母组合将不能共存，例如 TPQ=480 支持全部既定分母，而 TPQ=1 只支持 1、2、4；用户修改 TPQ 或 Time Signature 时可能需要先修正另一侧。
- 备选方案及差异：A. 把 BBT 的 Tick 改为有理数/子 tick，并连带修改解析、显示、公共类型和吸附；兼容面与 UI 复杂度显著增加。B. 对拍边界进行确定性量化；会产生不等长拍、可能的零 tick 拍和不可逆坐标，不推荐。C. 对不兼容 Project 禁用 BBT/拍网格，仅保留绝对 tick；违反初版要求这些功能可用的目标，不推荐。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-08，采用推荐方案。
- 最终处理与提交：已直接修订开发期 v1，不创建新版本或迁移。`ProjectTimeSignatureRules`/`ProjectTimeSignatureMap` 统一兼容性、Bar:Beat:Tick、自然小节/拍边界和 Snap；Domain 创建、Application Create/Update、semantic validation、persistence restore/serialize 全部拒绝不兼容组合；`MIDORA1018` 对中途截断产生 Warning。Core Release 构建 0 warning/0 error；Compiler 242、Persistence 94、Application 261 tests 通过。

### Q-NUI-031：held Preview 的有界因果窗口与续接阈值

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-07。
- 已确认事实：Q-NUI-022 固定 Gate Open 时 `GateLength = Int64.MaxValue`，但编译器不能真的展开到 `long.MaxValue`；Loop/Envelope/Mapping 可能持续产生事件。正式实时链还必须保持用户当前 Render-Ahead，并保证 Gate End 只影响 producer 尚未渲染的第一个 frame。
- 推荐并已实施方案：每次编译 8 秒的有限 causal window；当 producer frontier 距当前窗口末尾不多于 4 秒时，先在 producer frontier 暂停，使用同一 Gate Open 上下文编译下一窗口、替换未渲染后缀并恢复。Gate End 也沿用相同 pause/splice/resume 边界。窗口与阈值是内部调度常量，不保存到 Project、不影响 canonical 持久化或导出，也不覆盖用户 Render-Ahead。
- 推荐依据与限制：8 秒给编译/IPC 留出远大于正常调度抖动的余量；4 秒提前续接可在保留有限内存与有限 Loop 展开的同时避免接近窗口末端才阻塞。限制是极端超过 4 秒的暂停/编译/IPC 卡顿仍会进入既有 Buffering 或结构化失败，不能把无限计算伪装成实时保证。
- 影响范围：Preview Compiler 的有界范围、Playback 续接调度、ABI v3 引入且由当前 v4 保持的 pause/apply-plan/resume 与相关性能/故障测试。不会改变固定长度 Project Note、MIDI 导出、音频文件渲染、`.midora` 或 Mapping ABI。
- 备选方案：A. 使用更短固定窗口，会增加编译和 IPC 频率；B. 使用更长窗口，会增加最坏内存和 Loop 展开；C. 根据 Render-Ahead 动态改变窗口，会让调度行为更难预测且可能把用户参数误当语义。D. 一次编译到 `long.MaxValue` 不可终止，不接受。
- 自动证据：窗口续接、frontier 精确 NoteOff、ring 内 PCM 不回写、延迟报告、同音高活动实例、producer pause/drain/resume、ABI v3 generation 确认，以及托管/Native AOT Worker 往返均有测试。专项完整回归为 Compiler 224/224、Playback 60/60、Application 238/238、BASS 148/148。
- 需要产品所有者确认：是否接受 8 秒窗口与剩余 4 秒续接阈值？如需修改，请给出目标窗口/阈值或性能约束。
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-032：Project Object Clipboard 的会话内载荷表示

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-07。
- SRS 依据：第 20.6.2～20.6.4 节要求当前 Windows Clipboard、Midora Internal Object Payload + Plain Text Summary、仅来源 Project 会话有效、关闭/替换后失效、Copy 时不可变快照；不要求跨进程或跨应用序列化格式。
- 推荐并已实施方案：非 UI 层公开不透明的 `ProjectObjectClipboardPayload`，内部保存深复制快照并绑定 `ProjectDocumentSession` 的仅运行时身份对象；WPF 阶段只负责把该对象和 `PlainTextSummary` 写入当前 Windows Clipboard。粘贴必须同时通过 payload kind、目标上下文和来源会话身份检查；新建/打开/替换后的 Document 即使内容或路径相同也不能复用旧 payload。载荷不序列化、不持久化、不进入 Undo/Redo，也不建立内部历史或多槽。
- 推荐依据与限制：严格实现当前会话边界且不发明公开 clipboard wire format；进程内对象避免重复序列化复杂对象图和破损引用。限制是应用重启、另一进程或 Windows Clipboard 延迟呈现无法恢复 Midora 内部对象，只能看到 Plain Text Summary；这与初版不支持跨 Project Paste 一致。
- 备选方案：A. 为 Clipboard 发布版本化二进制/JSON 格式，可跨进程保留，但会新增永久兼容与不可信输入解析面。B. 只在内存保存 payload、Windows Clipboard 仅写 token；可工作但 token 在 clipboard 中没有自描述价值，且仍需同一进程注册表和过期清理。C. 仅比较 Project 路径或内容 ID，会错误允许关闭后重开同一文件继续 Paste，违反“当前来源 Project 会话”。
- 自动证据：跨 `ProjectDocumentSession` 拒绝、同会话 kind/目标检查、删除源对象后快照仍有效、payload 保留、重复 Paste 每次分配新 ID均已覆盖。
- 需要产品所有者确认：是否接受推荐的“不透明进程内 payload + Plain Text Summary + Document 会话身份”方案？若初版必须支持应用重启后 Paste，需要选择 A 并先冻结格式与安全边界。
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-033：`Compatible ordered content` 的初版非 UI 解释

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-07。
- SRS 依据：第 20.6.5 节明确列出 Segment、Logical Note、Logical Parameter 内容、SubVoice timeline events、普通 Conductor events，并另列未展开的 `Compatible ordered content`；第 20.5.6.4 节把 Mapping 等定义为有序结构。
- 推荐并已实施方案：初版非 UI Clipboard 把 `ValueCurve` 的选中 Point 内容和完整 `MappingChain` 视为 compatible ordered content。Value Curve 内容只能在同一 Event Instrument 内粘贴到 exact `MidiValueTarget`；Mapping Chain 只能在同一 Event Instrument 内替换目标 Chain，目标非空时需要显式确认。两者都保持顺序、深复制 owned stable ID、保留同 Instrument 外部引用，并在任一不兼容时整体失败。
- 推荐依据与限制：二者都是当前模型中明确存在且已有专用编辑命令的有序内容；同 Instrument/exact target 边界避免按名称或近似值域猜测。限制是初版没有把 Enum Item、SubVoice Definition、Logical Parameter Definition 等高层结构塞入普通 Clipboard；它们继续使用 reorder、Duplicate 或专用命令。
- 备选方案：A. `Compatible ordered content` 初版只指 Mapping Chain，不含 Value Curve 内容；会使 SubVoice 曲线点只能逐点编辑。B. 允许跨 Event Instrument 粘贴并按名称修复 Mapping 引用；违反稳定 ID 和禁止自动名称匹配。C. 把所有有序定义都放入 Clipboard；会与第 20.6.5 节显式排除的高层定义冲突。
- 自动证据：Value Curve exact target/同 Instrument 门、Mapping Chain 非空确认、深快照、新 Chain/Step ID、外部 Parameter 引用保留及 Undo/Redo 同一复制对象均已覆盖。
- 需要产品所有者确认：是否接受上述范围？若 Value Curve 内容不应属于普通 Clipboard，请选择 A；其他高层定义仍不建议加入。
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-034：Segment/Unit 长期音频缓存与 Buffering 恢复总架构

- 类型：大决定；阻塞长期 PCM 缓存、播放重复命中和新的 underrun 恢复分支，其他不依赖该架构的非 UI 工作继续。
- 状态：已确认并完成；旧推荐及 A/B/C 已撤回，采用重写后的五层推荐架构。Q-NUI-041～Q-NUI-042 已补足设置边界。
- 发现日期：2026-08-08；意图澄清日期：2026-08-08。
- 产品意图：Render-Ahead 是约 200 ms 的短期未来窗口，不是长期缓存。underrun 说明当前位置及后续可能持续高负载，不能每填约 200 ms 就断续推进；应保持当前位置静音等待，完整渲染一个自然音乐区间后再连续播放。已渲染内容必须同时进入可重复使用的长期缓存；音乐内容、设备/采样率及其他语义输入未变时，第二次播放同一区间不再重复编译或 BASSMIDI 渲染。长期缓存应由多个独立 Segment 的缓存组合，而不是一个不可分割的整曲文件；其他 Segment 的编辑不应使未改变的重负载 Segment 反复重算。
- SRS 与源码核对：SRS 的 Segment End/Reset、同 Track 不重叠和跨 Track 状态独立支持 Segment 作为主要缓存边界。当前编译器实际上已有全局分配之前的 `SegmentCacheEntry`/`RawInstance[]` 展开缓存，所以编译部分是形式化、增加范围结果缓存和安全细粒度复用，不是从零重写。正式 canonical 仍必须执行全局 256 Unit 检查和 Port/Channel 分配；音频不得绕过 canonical 直接消费私有缓存。
- 已确认的原冲突修正：长期缓存与精确 Render-Ahead ring 分层后，SRS 的一小节恢复目标不需要装入 ring。ring 只从已经完整发布的较长 playback span 分批读取，因此不再需要把恢复目标裁到 ring capacity。旧推荐错误地把两种缓存当成同一个容器，不能满足产品意图。
- 重写后的推荐方案：采用五层分离：① Project revision/canonical range cache；② Port/Channel-independent Segment/Unit compiled fragment cache；③ canonical 派生、位于 Mute/Solo/Master/Limiter 之前的 Segment/Unit PCM tile cache；④ 完成确定性求和、当前 Mute/Solo、Master 和单个全局 Limiter 的稀疏 playback span cache；⑤ 容量仍严格等于用户设置的短 Render-Ahead ring。exact replay 命中第④层时只搬运 PCM；第④层 miss 但第③层 hit 时只重新混音/Master/Limiter，不调用 BASSMIDI。
- 正式主线：`Project → Validation → Segment fragment cache → global canonical → canonical audio projection → Segment/Unit PCM → playback span → ring → WASAPI`。Segment 抽象片段不包含最终路由，但只是 compiler 内部缓存；Canonical Compiled Result 仍保留全局路由和诊断。
- Buffering 行为：在 underrun tick `F` 锁存 `Playing.Buffering`，音乐位置和 ring read position不推进；优先补齐 `[F,R)` 所需的全部 Segment/Unit tile 和最终 playback span；只有整个区间原子完成后才恢复。`R` 的精确定义见 Q-NUI-035。
- 完整分析：`misc/Midora-Segment-Compilation-and-Audio-Cache-Design-Discussion-2026-08-08.md`。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-08，采用推荐方案。
- 最终处理与提交：决定已记录；五层缓存、正式 Worker、自然段恢复和缓存 generation 边界均已实施。八组真实 SF2 分别通过 1053 项零跳过发布门，累计 8424/8424；cache miss→hit PCM 等价、零重复 BASS synthesis、256 Unit Stream、热路径 0 B 与 Native AOT 集成门已闭合。

### Q-NUI-035：underrun 位于小节中途时“一整个小节”的精确定义

- 类型：大决定；直接改变 Buffering 后恢复的可听时长。
- 状态：已确认并完成；选择备选 B，并把总上限由 8 个四分音符放宽为 16 个。
- 已确认矛盾：SRS 只写 `min(当前拍号下一整个小节长度, 8 个四分音符)`，没有规定 underrun tick 位于小节中途时，目标是“剩余当前小节”“从该 tick 起一个小节时长”还是“当前剩余部分再加下一个完整小节”。三者可相差接近一整个小节。
- 推荐方案：从 underrun tick `F` 起，按 `F` 处当前拍号计算一个完整小节的**时长**，再与 8 个四分音符取最小，令 `R = F + targetTicks`。它不强制 `R` 落在 Bar 边界；若区间内发生 Time Signature 变化，本次目标仍使用 `F` 处冻结拍号，下一次 underrun 再使用新拍号。这样无论 `F` 位于哪里，都确实储备一个完整小节时长，不会因恰好接近小节末尾只得到极短储备，也不会等待接近两个小节。
- 备选方案 A：只到当前自然小节末尾；Bar 语义直观，但 underrun 若发生在末尾附近会立即再次断续，不符合稳定恢复目的。
- 备选方案 B：当前小节剩余部分加下一个完整自然小节；最有自然段感，但可能接近两个小节，并与“最多 8 个四分音符”的硬上限冲突。
- 备选方案 C：从下一个 Bar 起缓存一个完整小节，同时 `F` 到下一个 Bar 另作前导；本质接近 B，协议更复杂。
- 最终语义解释：若 `F` 正好位于自然 Bar 起点，目标是该当前完整小节；若 `F` 位于小节中途，目标是当前小节剩余部分加下一个完整自然小节。随后以 `F + 16 个四分音符` 和本次播放实际终点共同裁剪 `R`，即总 `[F,R)` 上限为 16 个四分音符；若极端拍号下该上限截断“下一个完整小节”，上限优先。Time Signature 中途变化依 Q-NUI-019 立即形成新 Bar，因此自然小节边界使用同一正式 Bar Map。
- 需要产品所有者回答：已回答。上述“16 个四分音符限制整个恢复区间、Bar 起点不额外再取第二小节”是根据回答补足的精确解释；若意图不同，请直接修正。
- 产品回答：2026-08-08，选择 B；最大 8 个四分音符限制放宽为 16 个。
- 最终处理与提交：决定已记录并实施；Bar Map、F→R 锁存、16 四分音符总上限和恢复区间裁剪均有自动门。

### Q-NUI-036：每 Unit 单通道 Stream、Stream pool 与 sample-voice 上限

- 类型：大决定；改变正式实时/离线合成拓扑、达到 voice limit 时的可听结果和性能边界。
- 状态：已确认并完成推荐拓扑和可调/失效方向；Q-NUI-042 已确认 Realtime/Offline 两个默认均为 500。
- 已确认事实：现行 SRS/ADR 要求每个实际 Port 一个 BASSMIDI Stream，并把实时/离线 750 定义为每个 Port Stream 的上限。BASSMIDI 官方接口允许一个 Stream 有 1～128 个 MIDI channel；官方 voice limit 是每个 Stream 的 sample voice 上限，达到时会结束最低音量 voice。因此从每 Port 改为每 Unit 会让不同 Unit 不再竞争 750，显著增大任务总 voice 容量。
- 推荐方案：采纳产品所有者的 Unit 隔离目标，但不为每个 Unit 永久持有 Stream。定义“一个抽象 Channel Unit 渲染任务对应一个干净的 1-channel BASSMIDI Stream”，由有界、可复用 worker pool 执行；Unit PCM 分别缓存，按固定顺序在 Segment/Project 层求和。每次复用前执行精确 NoteOff、CC120、Reset 和状态重建。实时与音频文件渲染共享同一分解和求和语义。
- sample-voice 影响：现有 `Maximum Sample Voices per Stream` 将按字面作用于每个 Unit render Stream，而不再是每 Port；这避免跨 Unit voice stealing，但总 CPU 保护弱于现行语义。worker pool 并发数只控制完成速度和 native 资源，不改变音频结果。产品已把默认值从 750 改为 500，并要求允许用户修改；任一正式 sample-voice 设置提交后清除所有 Segment/Unit PCM、playback span、Preview/Offline raw PCM 与短 sample-domain generation，tick-domain compiled/canonical cache 不因此失效。
- 推荐依据与限制：该方案完全去除物理 Port/Channel 对 Unit PCM key 的影响，并实现我们自己的跨 Unit 混音；Stream 数量由 pool 而非 Project 总 Unit 数限定。限制是第一次渲染更慢、缓存量更大，且 voice limit 的资源语义发生实质变化。
- 备选方案 A：一个 Segment 使用尽量少的 1～128-channel render shard；首次渲染更快、Stream 更少，但同 shard 的 Unit 会继续竞争 voice limit，缓存也只能稳定到 shard/Segment 粒度。
- 备选方案 B：保持每 Port Stream，只增加最终时间片缓存；最少改动，但 Port 分配变化会破坏缓存身份，达到 voice limit 时也无法证明独立 Segment PCM 与整曲 Port Stream 等价，不能完整实现产品意图。
- 需要产品所有者回答：已回答；默认 500 对 Realtime/Offline 的精确作用范围见 Q-NUI-042。
- 产品回答：2026-08-08，采用推荐方案；复音数允许用户在设置中修改，修改后失效所有音频缓存；默认从 750 改为 500。
- 最终处理与提交：决定已记录并实施；voice 设置变化会轮换全部 PCM generations，tick-domain canonical 保持有效。

### Q-NUI-037：长期 PCM 缓存的位置、生命周期与容量失败语义

- 类型：大决定；决定磁盘消耗、重复播放保证、跨会话行为和故障工作流。
- 状态：已确认并完成推荐生命周期与容量/失败语义；Q-NUI-041 已确认默认设置与临时恢复存储边界。
- 已确认事实：stereo float32 48 kHz 每累计一小时 PCM 约 1.38 GB（约 1.29 GiB），192 kHz 约为四倍。Unit/Segment 分离后按所有缓存 Unit 的累计时长增长，可能远大于 Project 总时长。缓存不得写入 `.midora`。
- 已确认方案：初版采用 session-scoped 磁盘后备缓存 + 有界 RAM hot set；每个打开 Project 使用独立 cache session 目录。已完整写入并校验的条目在 Project 打开期间 pin，不做 LRU 驱逐；Project 关闭/替换时删除，不跨重开复用。缓存父目录/磁盘、最大长期缓存大小属于 Application Preferences，不进入 `.midora`；允许最大值为 0。应用必须展示配置位置、当前实际占用、最大值、是否仍在记录长期缓存，并提供安全打开/清理入口。
- 修改后的容量语义：新 tile 会超过用户配额、长期缓存目录不可写或普通磁盘剩余空间不足时，不删除当前会话已 pin 的完整条目，也不把播放提升为 Error。发布状态 Warning，停止写入新的**可复用长期缓存**，当前及以后 cache miss 改为每次实时 BASS 渲染；已有完整命中仍可读取。最大值 0 从任务开始就采用该模式，因此 exact replay 不再承诺免合成，这是用户显式选择的 100% 实时模式。
- 必须区分：上述降级只解决长期复用缓存不可写。若实时渲染发生 underrun，Q-NUI-035 已要求先存放并完成最长 16 四分音符的恢复区间再继续；这些 PCM 不可能只放入短 ring。它仍需要一次性 transient recovery spool（消费后立即删除，不提供下次复用）。如果 RAM 和所选磁盘都无法提供该临时空间，就不能同时满足 Q-NUI-035，必须受控停止并报告，而不能恢复 200 ms 断续播放。其配额归属和默认设置见 Q-NUI-041。
- 备选方案 A：Application Preference 提供缓存配额并使用 LRU；资源可控，但 exact replay 只在条目尚未被驱逐时保证，弱于本次产品意图。
- 备选方案 B：持久化 content-addressed cache 跨 Project 重开复用；体验更强，但要增加长期配额、隐私、旧 engine/SF2 清理、孤儿回收和崩溃恢复契约。
- 备选方案 C：只使用有界 RAM；实现简单，但无法覆盖长曲和大量 Unit，不满足目标。
- 需要产品所有者回答：已回答；默认目录/配额和 transient recovery spool 边界见 Q-NUI-041。
- 产品回答：2026-08-08，采用推荐方案；缓存位置与占用对用户透明，允许选择缓存磁盘/目录和程序级最大缓存大小；长期缓存允许为 0。磁盘不足时希望只显示状态 Warning，不再存长期缓存，继续每次实时渲染播放。
- 最终处理与提交：决定与必要限制已记录并实施；session store、配额 0、Warning 降级、透明统计和 spool/RAM 边界均有自动门。

### Q-NUI-038：Segment 内缓存的因果 Dirty 起点

- 类型：大决定；影响缓存正确性和局部编辑后的复用率。
- 状态：已确认并完成。
- 冲突说明：产品描述中的“内容改变后，只失效改变时间点之后”表达了正确的性能目标，但若把 UI 编辑 tick 无条件当作失效点会出错。Note length、Overlap/Lifecycle、Mapping、Parameter 状态或 Segment 裁剪变化可能使更早开始的实例或状态发生改变；SRS 当前也要求 Logical Note/Parameter 修改通常回退到 Segment 起点或可证明等价的 checkpoint。
- 推荐方案：每种编辑计算“最早可证明的因果影响 tick”，PCM 从包含该 tick 的 tile 起向后失效；能够证明旧前缀逐事件、逐 sample 等价时保留前缀。无法证明、修改 Instrument/Mapping/Lifecycle/Reset 或 checkpoint 状态不等价时，回退到 Segment 有效起点。全局 Port/Channel 分配仍从正式 compiler dirty checkpoint 重算直到状态收敛。
- 备选方案 A：任何 Segment 内容变化都使整个 Segment fragment/PCM 失效；最简单且正确，但长 Segment 局部编辑收益较低。
- 备选方案 B：一律从用户编辑 tick 向后失效；性能最好但不满足正确性，拒绝作为正式方案。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-08，接受推荐方案。
- 最终处理与提交：决定已记录并实施；cache key/dirty tick、Segment checkpoint 和 source/state hash 收敛均有回归。

### Q-NUI-039：切换物理设备但实际格式相同时的 PCM 复用

- 类型：小决定；已确认并完成。
- 状态：已确认并完成。
- 已确认冲突：现行 SRS 第 13.12.8 节写“设备或其实际采样率变化时，旧 Stream 和所有 sample-domain 缓存失效”。但推荐的 Segment/Unit PCM 在 BASS decode 阶段生成，不包含 WASAPI 设备身份；在实际采样率、stereo float32 格式及全部合成 key 相同时，物理 endpoint ID 不会改变这段 PCM。
- 推荐方案：把 sample-domain cache 分为 device-independent synthesis PCM 和 device-bound output state。切换设备时始终断开/重建 WASAPI、callback、ring 和设备绑定 playback generation；若新设备实际采样率/格式与旧设备完全相同，则保留已校验 Segment/Unit PCM。采样率或格式不同才失效全部 PCM。设备丢失仍执行 Q-NUI-028 的人工重选门，绝不自动切换。
- 备选方案：继续按现行 SRS 在任何设备 ID 变化时删除全部 PCM；实现保守，但会在同采样率设备切换后无必要地重复重负载合成。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-08，采用推荐方案。
- 最终处理与提交：决定已记录并实施；设备绑定 generation 与同格式 synthesis PCM 已分层，SRS blanket invalidation 已收窄。

### Q-NUI-040：主时间线、Preview 与音频文件渲染的缓存复用边界

- 类型：小决定；已确认，不改变正式音符语义，但影响缓存污染、命中率和实时/离线一致性。
- 状态：已确认并完成。
- 推荐方案：主时间线播放和绑定 Project 时间位置的固定 Segment Preview 共用 Segment/Unit compiled/PCM cache。Event Instrument Preview、Pitch Ruler、单 Note 放置等 Gate 未知或草稿源 Preview 使用独立 transient generation，不把未提交草稿写入 Project cache；若内容随后正式提交，只能按正式 source fingerprint 重新命中或生成。音频文件渲染可以在 SF2、采样率、voice policy、canonical fragment、范围起点冷启动上下文和 renderer version 全部相同时复用第三层 Unit PCM，但必须单独构建自己的 Track selection、Master/Limiter、范围和原子文件事务，绝不复用设备 ring 或普通 playback span。
- 推荐依据：避免临时 held Preview 污染长期 Project 缓存，同时让相同正式 Segment 在实时和离线 exact key 下共享最昂贵的 BASS 合成。Offline Maximum Sample Voices 与 realtime 设置不同时自然 cache miss。
- 备选方案 A：长期缓存只服务主时间线；边界最简单，但 Segment Preview 和同配置音频渲染会重复合成。
- 备选方案 B：所有 Preview 都写入同一缓存；命中机会多，但草稿、未知 Gate 和未提交内容会扩大存储并增加错误复用风险。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-08，采用推荐方案。
- 最终处理与提交：决定已记录并实施；正式 Project Preview/Offline exact-key 复用与 held/draft transient generation 已分离。

### Q-NUI-041：长期缓存默认设置与 0-cache 模式的临时恢复存储

- 类型：大决定；阻塞 Q-NUI-034～Q-NUI-037 的存储协议和 Application Preferences 契约。
- 状态：已确认并完成推荐方案。
- 已确认输入：Q-NUI-037 要求用户可见缓存目录、当前占用和最大值；可选择磁盘/目录；最大长期缓存允许 0；配额/空间不足时只产生状态 Warning，并退化为每次实时渲染。长期缓存不进入 `.midora`，clean close 删除，crash 后允许用户安全手动删除。
- 必须区分的两类磁盘数据：`Reusable Cache` 在第二次播放复用，受用户长期缓存上限约束；`Transient Recovery Spool` 只为一次 Q-NUI-035 Buffering 储存完整恢复区间，消费后立即删除，不允许下次命中。最大长期缓存为 0 时，前者完全禁用，但后者仍是保持“先完成自然段再恢复”的必要工作空间。
- 已确认上界：Q-NUI-035 把恢复音乐长度封顶为 16 个四分音符；合法 Tempo 还必须可表示为 24-bit MIDI Set Tempo。最慢可表示 Tempo 下，16 个四分音符不超过约 268.44 秒；单份 stereo float32 final spool 在 48 kHz 约 98.3 MiB、192 kHz 约 393.2 MiB。实现仍必须用正式 Tempo Map 精确预检实际 frame/byte 数，不能依靠该估算分配。
- 推荐默认与路径协议：Application Preferences 新增专用缓存根目录和最大 reusable cache bytes；默认根目录 `%LOCALAPPDATA%\Midora\AudioCache`，默认上限 16 GiB，合法上限为 `0..long.MaxValue` bytes，`0` 表示不写 reusable cache。用户可选择任意可写的本机绝对目录/卷；初版拒绝相对路径和 UNC/network path。Midora 只在该 root 内管理带版本化 manifest 的 `session-*` 子目录，绝不递归删除用户选中的 root 或其中未知文件。
- 推荐透明度/清理：Application Settings 展示配置 root、reusable 已用/上限、transient 当前/峰值、当前 retention enabled/disabled 和最近 Warning；提供 `Open Cache Folder`、`Clear Inactive Cache`。手工删除整个 root 在 Midora 完全退出时安全；运行中删除条目按 cache miss/Warning 处理，不得崩溃。clean close 删除本 session；启动时只清理能够通过 manifest + 无活动 lease 证明为 orphan 的 Midora session，不能猜测删除其他目录。
- 推荐配额语义：reusable cache 达到用户上限或写失败时，已完整 pin 条目继续可读，新条目不再写并产生非模态状态 Warning；下次 Project/用户显式 Retry 可重新探测。Transient spool 的实际占用显示给用户，但不计入 reusable 16 GiB/用户上限，否则 `0` 模式无法满足 Q-NUI-035。spool 优先使用所选 cache root，消费/Stop 后立即删除。
- 临时存储失败边界：如果 transient spool 的预检、创建、写入或校验失败，且没有足够的已预留 RAM 工作区，则当前 underrun 无法继续满足 Q-NUI-035；控制器应受控 Stop，保留失败 tick，并报告 `AudioRecoveryStorageUnavailable`。不得退回 200 ms 断续播放，也不得把普通 reusable cache 写失败误报为该致命边界。
- 备选方案 A：把 transient spool 也计入用户最大值；更符合“总磁盘上限”的字面，但最大值 0 时任何 underrun 都只能 Stop，无法形成用户希望的 100% 实时可继续模式。
- 备选方案 B：默认上限采用所选卷可用空间百分比；能适应不同磁盘，但默认值随时点变化，难以解释和测试。可在 UI 提供建议值，不建议作为持久化语义。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-08，采用推荐方案。
- 最终处理与提交：决定已记录并实施；Preferences、session root/quota、transient spool、unmanaged RAM fallback 与受控停止均有自动门。

### Q-NUI-042：默认 500 对 Realtime 与 Offline 设置的作用范围

- 类型：大决定；会修改 Application Preferences、Project Audio Render Settings 开发期 v1 默认值及达到 voice limit 时的可听结果。
- 状态：已确认并完成推荐方案。
- 已确认事实：现行规格有两个彼此分离的用户值，当前都默认 750：Realtime 值属于 Application Preferences，Offline 值属于 Project Audio Render Settings/任务覆盖。Q-NUI-036 已把 Stream 语义改成每 Unit，并要求用户可修改、修改后失效所有音频缓存、默认改为 500，但“这个复音数”没有唯一说明只改 Realtime 还是两者都改。
- 推荐解释：两个值继续分离且都可编辑，语义统一改名为 `Maximum Sample Voices per Unit Stream`；新安装 Realtime Application default = 500，新 Project 的 Offline Project default = 500。既有开发期 v1 直接改默认，不创建版本迁移。修改任一值时只允许在没有活动播放/预览/文件渲染任务时提交，并清除当前打开 Project/session 的全部 PCM/audio cache generations；tick-domain compiler/canonical cache不失效。一次 Offline 任务覆盖值仍只属于任务快照，不改 Project 默认，除非用户显式 `Save as Project Defaults`。
- 推荐依据：实时与文件渲染采用 Q-NUI-036 同一 Unit 分解时，两边默认一致最易理解；保留两个设置仍允许离线任务使用更高值。清空全部音频缓存严格遵守本次回答，即使精确 cache key 理论上可以保留另一设置的条目。
- 备选方案 A：只把 Realtime 默认改为 500，Offline 继续默认 750；保留离线更高质量/资源默认，但“默认从 750 改 500”的范围不一致。
- 备选方案 B：合并为一个 Application 值；会推翻既有 Project Offline Settings 和任务覆盖工作流，不推荐。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-08，采用推荐方案。
- 最终处理与提交：决定已记录并实施；Realtime/Offline 默认 500、活动任务提交门、History/Undo/Redo 与全部 PCM generation 轮换均有自动门。

### Q-NUI-043：自然拍 Snap 完全等距时的方向

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-08。
- 已确认事实：Q-NUI-030 要求无漂移的自然拍网格与 Snap，但 SRS 未规定目标 tick 到前后两个自然拍边界距离完全相等时选择哪一个。两种结果都保持整数、可逆且确定，不影响 Project 源数据之外的时间语义。
- 推荐并已实施方案：完全等距时选择后一个拍边界。Project 时间非负，该行为与 midpoint `AwayFromZero` 的前进方向一致；非等距时始终选择实际 tick 距离更近的边界。Time Signature 变化 tick 自身是新拍号的 Beat 1 边界，也可成为该后向目标。
- 影响范围：`ProjectTimeSignatureMap.SnapToNearestBeatGrid` 和后续 WPF Timeline 的自然拍 Snap；不影响 canonical、MIDI、音频、持久化、Undo/Redo 或已有对象。Pointer Up 提交的是已经算出的 absolute tick，Redo 不重新读取当前 Snap preference。
- 备选方案 A：完全等距时选择前一个边界；同样确定，但与当前推荐方向相反。备选方案 B：保留原 tick、不吸附；会让“Snap Enabled”在正中点形成例外。备选方案 C：按拖动方向选择；需要额外手势历史输入，同一目标 tick 不再是纯函数，不推荐作为基础服务语义。
- 自动证据：TPQ/分母矩阵、跨中途变拍的 previous/next/nearest beat grid，以及 960/1000 两边界正中 tick 980 向 1000 吸附均已覆盖。
- 需要产品所有者确认：是否接受完全等距时选择后一个自然拍边界？若选择 A，请明确改为前一个边界。
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-044：内部 MIDI Render Plan 文件加入 Segment/Unit 片段元数据

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-08。
- 已确认事实：正式 Native AOT Worker 只能从内部临时 MIDI Render Plan 文件取得冻结播放计划。Q-NUI-034～Q-NUI-040 要求 Worker 在 sample domain 区分可复用的 Segment/抽象 Unit 片段；旧内部 MDAP v3 只保存按 canonical Port 聚合的事件，不能无歧义恢复 Segment、Instrument、Instance Group、SubVoice、片段边界和 route-independent semantic fingerprint。该文件不是 `.midora`，不进入 Project 持久化，也不承诺跨版本兼容；Q-NUI-011 已确认内部 ABI 在开发期可正常升级。
- 推荐并已实施方案：内部 MDAP 直接升级到 v4，在原 canonical Port 计划之后追加确定性排序的 Unit fragment 记录及其 channel-0 事件；读取端严格只接受 v4，旧 v3 受控拒绝，不加入兼容分支。记录保留 canonical route 只用于当前 playback generation 的执行；长期 Unit PCM key 使用不含物理 Port/Channel 的 semantic fingerprint。
- 影响范围：主进程到 Native AOT Worker 的单次任务临时文件、大小/校验和/损坏门、Worker 的片段缓存调度。它不修改 canonical、MIDI 导出、音频文件格式、`.midora` schema、用户工作流或可听语义。当前编码会同时保留 Port 聚合事件和片段事件，内存/文件上限因而更保守；后续只有在证明能力边界受影响时才改为索引编码。
- 备选方案 A：保持 v3，另写第二个 sidecar manifest；会增加双文件原子性、校验和清理状态，不推荐。备选方案 B：让 Worker 从 canonical 或 Project 重新推断 Segment；违反正式消费者只消费冻结计划且不得重解释 Project 的主线。备选方案 C：兼容读取 v3；没有持久化或跨版本使用场景，只会永久扩大测试与攻击面。
- 自动证据：v4 确定性 round-trip、checksum/长度/损坏门、v3 拒绝、Unit 片段身份/边界/排序/channel-0 投影及计划文件上限均有定向测试。
- 需要产品所有者确认：是否接受内部 MDAP v4 直接替换 v3，并继续不提供旧内部临时文件兼容？
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-045：共享音频 Worker ABI v4 的 Buffering 恢复命令编码

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-08。
- 已确认事实：Q-NUI-035 要求 underrun 后先完整准备 `[F,R)` 才恢复。主进程持有统一 `ProjectTimeSignatureMap` 和冻结播放终点，能确定计算 `R`；Worker 只消费 sample-domain 计划，不应重新解释拍号。现行共享 ABI v3 已有 16-byte 固定 command record，其中 offset 4 的 64-bit payload 只被 held-preview apply-plan generation 使用，其他 held 命令要求它为零。Q-NUI-011 已明确允许该内部 ABI 在开发期升级。
- 推荐并已实施方案：共享控制 ABI 直接升级为 v4，新增闭集命令 `BufferingRecoveryPrepare(endFrame)`；`endFrame` 必须为正且不超过任务总 frame。沿用 command record offset 4 的通用 64-bit payload，不扩大 128-byte header、16-byte command record 或 1024-command ring；字段的 C# 名称改为中性的 `Payload`。主进程与 Worker 只接受 v4，v1～v3 和其他版本全部受控拒绝，不提供临时内部协议兼容。
- 影响范围：只影响主进程/Native AOT Worker 的单次会话命令闭集、校验与自动测试；不改变 `.midora`、canonical、MDAP v4、Mapping ABI、MIDI/WAVE、可听区间算法或用户设置。seqlock header、offset 68 状态序列和 offset 88 held generation 均不改变。
- 备选方案 A：扩大 header 增加专用 recovery endpoint 字段；会把一次性命令状态混入持续状态快照并改变映射布局。备选方案 B：让 Worker 从 tick/拍号自行计算 `R`；会复制 Project 音乐时间语义并违反 Worker 不重解释 Project。备选方案 C：另建 pipe/文本命令；违反固定有界零分配 IPC 约束。
- 自动证据：v4 当前版本/旧版拒绝、命令 round-trip、零/负 endpoint 拒绝、reserved byte、ring 边界、零分配和 Worker 恢复往返均有覆盖。
- 需要产品所有者确认：是否接受共享控制 ABI v4 的上述直接升级和通用 64-bit command payload 编码？
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-046：缓存命中片段在实时 Mute/Solo 时的防爆音退场

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-08。
- 已确认事实：Segment/Unit raw PCM cache 位于 Mute/Solo 之前。正常 live BASSMIDI 路径在 Track 被过滤时收到 CC120，并已用正式 SF2 验证会对目标 Channel 执行固定 4 ms 防爆音衰减；缓存命中路径不调用 BASSMIDI 合成，因此不能依靠该 native 衰减。若直接停止混入缓存 PCM，会在任意采样值处硬切并可能爆音；若继续读完整 tile，又违反 Mute/Solo 立即清理。
- 推荐并已实施方案：缓存命中的活动 Unit 在 source 从 audible 变为 filtered 时，从 producer 处理该命令的首个尚未渲染 frame 起执行固定 4 ms 线性 gain 退场，首 frame 保持当前幅度、最后一帧降至 `1/N`、后续严格为零；同时立即使该 source 后续 cache generation 失效/绕过。其他 Unit 不受影响。解除过滤仍按既有当前 tick 冷启动：只恢复必要非 Note 状态，不补发错过的 Note On。48 kHz 时 `N=192`，其他采样率使用向上取整的 4 ms frame 数。
- 影响范围：只影响播放中切换 Mute/Solo 且当前活动 Unit 恰好命中 raw PCM cache 的短暂监听退场；不改变 Project、canonical、导出、离线渲染、正常边界清理或长期 cache key。该退场不写入 reusable raw PCM。
- 备选方案 A：缓存命中立即硬切；最严格的瞬时静音，但存在明显不连续和爆音风险。备选方案 B：为命中 tile 启动隐藏 BASSMIDI Stream 并重建历史 Note 后再发 CC120；无法从任意 PCM frame 精确恢复 sample-voice/native envelope 状态，也会破坏“命中不合成”。备选方案 C：固定 4 ms equal-power 曲线；同样可防爆音，但与当前 CC120 实测只确认时长、未确认曲线，线性实现更简单且确定。
- 自动证据：完整 4 ms 后严格为零、首 frame 连续、跨不同 256-frame 分块结果逐样本一致；缓存暂存/监控失效及现有 live CC120 Channel 作用域门继续覆盖。
- 需要产品所有者确认：是否接受缓存命中路径使用固定 4 ms 线性退场来对应 native CC120 的 4 ms 防爆音行为？
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-047：最终播放跨度缓存命中后切换 Mute/Solo 的因果切换

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-08。
- 已确认事实：post-sum/Master/Limiter playback span 是冻结 audible-set 的最终 PCM。命中后不会运行底层 BASSMIDI/Limiter，因此播放中改变 Mute/Solo 时，旧跨度不能继续作为新 audible-set 的正式输出；同时不能回写已消费或已进入 device ring 的 PCM，也不能从任意 sample 恢复 BASSMIDI 内部 envelope/Limiter 历史。Q-NUI-022 已确立 producer frontier 的因果实时边界，Q-NUI-046 已确立 4 ms 线性防爆音时长。
- 推荐并已实施方案：在处理监控命令的首个 producer frontier 使当前最终跨度 generation 失效；底层 renderer 从该 frontier 按冷启动语义开始，不补发已经错过的 Note On，复用仍有效的 raw Unit PCM；Limiter 状态从 `gain=1` 重置。首 4 ms 使用旧最终跨度与新实时结果作线性交叉淡化，之后只输出新 generation。未消费的旧跨度不写回长期缓存；已完整发布的旧 audible-set exact span 仍可供以后相同 audible-set 命中。
- 影响范围：只影响“主时间线命中最终跨度后，在播放中切换 Mute/Solo”的短暂监听过渡；不改变 Project、canonical、导出、离线音频、raw Unit key、播放位置或已消费 PCM。由于冷启动不补 Note，解除过滤时可能要到下一次 Note On 才重新发声，这是现有正式监控语义，不是缓存特例。
- 备选方案 A：立即硬切到冷启动结果；更直接但可能在任意 sample 产生不连续。备选方案 B：继续旧跨度直到自然 tile 边界；会延迟 Mute/Solo 生效。备选方案 C：回滚 producer/device ring 并从旧 checkpoint 重放；需要冻结原生 sample-voice/Limiter 全状态并扩大并发协议，不符合初版因果边界。
- 自动证据：命中路径不调用底层 source；frontier 精确传递；1 kHz 下 4-frame 交叉淡化为旧、75/25、50/50、25/75，随后完全切换；分块读取、EOS、零托管分配和缓存 generation 失效均有定向覆盖。
- 需要产品所有者确认：是否接受上述 producer-frontier 冷启动、Limiter 重置和 4 ms 最终 PCM 交叉淡化？
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-048：长期 PCM 缓存的有界 RAM hot-set 与磁盘背压参数

- 类型：小决定；已按推荐方案实施，待产品所有者确认或修改。
- 发现日期：2026-08-08。
- 已确认事实：Q-NUI-037 已确认 session-scoped 磁盘后备 + RAM hot-set；SRS 禁止 WASAPI callback、BASSMIDI render/mix 和 ring 搬运线程直接做文件 I/O。若让内存映射页在渲染线程按需缺页，仍可能把磁盘等待隐藏进实时路径；若把整段 PCM 全载入 RAM，又没有可接受的工程时长上界。
- 推荐并已实施方案：所有 reusable PCM 文件读写都由专用低优先级 I/O 线程完成。最终跨度使用一个 `16,384 frames`（stereo float32 为 128 KiB）的顺序 SPSC hot-set；raw Unit 对每个本次实际存在 cache hit/miss 的 canonical Unit 分别预留 `16,384 frames` 读/写 ring，最大 256 Unit 时读、写各最多 32 MiB；写入累计到 `4,096 frames`、片段结束、切换片段或任务完成时刷盘。实时 render 线程只复制 unmanaged ring；读前或写后跟不上时返回 `Buffering`，沿 Q-NUI-035 完整自然段恢复。离线渲染没有设备 deadline，在同一 frame 原地等待 I/O，不推进输出。普通写失败使 capture 失效并继续现渲染；已接受 cache hit 的运行期读取失败作为明确 renderer fault，不输出未经验证的 PCM。
- 影响范围：只影响缓存 I/O 调度、Preparing RAM 预留、极端磁盘吞吐下何时进入 Buffering；不改变 cache key、PCM 字节、Project、canonical、导出、设备 ring 容量或用户 reusable 配额。最大 hot-set 在读写同时覆盖全部 256 Unit 时约 64 MiB，另有最终跨度 128 KiB 和既有固定工作区；任务结束全部释放。
- 备选方案 A：直接 memory-map 并由 render 线程访问；代码简单，但 page fault 可能违反无文件 I/O/无阻塞热路径。备选方案 B：Preparing 全量载入所有命中 PCM；第二次播放平稳，但 RAM 随工程长度和 Unit 数无界。备选方案 C：把 hot-set 大小做成用户设置；会新增缺乏产品价值的高级参数和测试矩阵，初版不推荐。
- 自动证据：跨 fragment 读前切换、写 ring wrap/批量刷盘、最终跨度 hit/miss、I/O 背压、完整回放及 producer/render 调用零托管分配均已覆盖；八组真实固定 SF2 的完整发布矩阵累计 8424/8424、0 failure、0 skip，并覆盖最大 256 Unit Stream 与真实 cache miss→hit。不同物理磁盘的吞吐差异仍属于后续硬件矩阵，不改变已实施协议。
- 需要产品所有者确认：是否接受上述固定有界 hot-set/刷盘参数，以及“实时背压进入正式 Buffering、离线原 frame 等待”的行为？
- 产品回答：待填写。
- 最终处理与提交：已实施，待确认。

### Q-NUI-002：初版 `.midora` 需要迁移的历史格式基线

- 类型：大决定
- 状态：待确认；只暂停“旧格式成功迁移器”分支
- 发现日期：2026-08-06
- SRS 依据：第 16.12.4、16.24.1～16.24.4 节。
- 已确认事实：当前仓库只发布 `fileFormatVersion = 1`、schema v1、两份对象 protobuf v1 descriptor 及其 golden bytes；不存在 v0 JSON Schema、protobuf descriptor、golden `.midora` 包、字段映射或曾对外发布的旧格式。提交 `4b69e72` 的基础包也标记为 v1，且仍可由当前实现读取，不构成旧格式。
- 不确定点：SRS 要求低版本项目在内存中迁移，但没有定义初版发布前究竟存在哪些合法历史版本，以及各版本到 v1 的逐字段转换。实现方不能从版本号 `0` 推断包结构、缺失字段默认、稳定 ID 生成或 protobuf wire 契约。
- 影响范围：`.midora` 文件兼容承诺、迁移后的可听语义与稳定 ID、Modified/保存确认工作流、golden 兼容资产，以及未来版本升级策略。
- 推荐方案：初版 v1 声明“当前没有已发布且受支持的历史格式”；本轮实现严格版本预检和可扩展迁移注册边界，但注册表为空。低于 v1 的未定义包以结构化 `MigrationUnavailable` 失败，不能伪装成损坏 v1 或静默补默认。未来首次升级到 v2 时，必须随 v1→v2 migrator、v1 golden packages 和字段级迁移测试一起发布。
- 推荐依据与限制：避免发明不存在的 v0 文件格式，同时建立未来真实迁移所需的入口。限制是第 16.24 节在初版 v1 没有成功迁移样本，只能验证“无已定义来源版本时严格失败”；若产品确有需要兼容的内部原型格式，则必须先冻结其完整契约。
- 备选方案及差异：A. 现在正式定义 v0 并提供完整 schema/descriptor/golden/字段映射，再实现 v0→v1；工作量和永久兼容面显著增加。B. 指定某个现有提交生成的包为历史格式，但必须给它新的真实版本号并明确与当前 v1 的差异，不能把两个不同契约都标成 v1。C. 对任意 `fileFormatVersion < 1` 猜测缺失字段默认；该方案不可验证且可能改变可听语义，不推荐。
- 当前实施状态：成功迁移器未实施；严格版本预检、未来迁移注册接口和故障注入可继续。
- 需要产品所有者回答：是否采用推荐方案？如果不采用，请明确需兼容的历史版本号，并提供或指定其 schema、descriptor、golden package/生成提交及字段迁移规则。
- 产品回答：待填写。
- 最终处理与提交：待填写。

### Q-NUI-003：Project MIDI Export Settings v2 字段与初始默认值

- 类型：大决定
- 状态：已确认并实施；开发期直接修订 v1
- 发现日期：2026-08-06
- SRS 依据：第 3.7.7、14.7、14.15、14.16、16.7.3、19.5.7～19.5.8 节。
- 已确认事实：`Export Settings` 是 Project 内容；可保存模式、范围策略、Track 选择策略、Routing、Readme 和 Warning 策略，但不得保存绝对输出路径。仓库已经发布并使用严格 `export-settings.json` schema v1，当前 v1 只包含 `schemaVersion`；按兼容规则不得直接在 v1 增加必填字段。SRS 明确 Readme 默认开启，并明确“按 Logical Track 导出”默认 Compact，但没有规定新 Project 的默认导出模式、Whole/Per Port 的 Routing 默认、Warning-as-error 默认，也没有说明默认 Track 策略为 Explicit 时是否持久化具体稳定 ID 集合。
- 不确定点：这些默认值决定首次打开配置时的工作流和 Port/Channel 输出；字段一旦进入 schema v2 就成为文件兼容承诺。把它们静默写进现有 v1 会同时违反严格 schema 和已发布字段冻结规则。
- 影响范围：Project 领域公共接口、Undo/Redo 设置命令、`.midora` schema 与迁移、默认 MIDI 文件组织、Routing、Readme、Warning 阻止行为，以及未来 UI 初始化。
- 推荐方案：保留 v1 不变，新增 `export-settings.json` schema v2，并提供明确的 v1→v2 文件级迁移。新 Project 与 v1 迁移默认采用：`Whole Project`、`Project Default Range`、`All Valid Logical Tracks`、`Compact`、`includeReadme = true`、`treatWarningsAsErrors = false`；只有 Manual Range 时保存合法 `startTick/endTick`。初版不在默认设置中持久化 Explicit Track ID 集合，只保存选择策略；一次性具体勾选仍属于任务快照。设置变更由一个原子领域命令提交/撤销。
- 推荐依据与限制：Whole Project 是最小惊讶的单文件入口；Compact 与当前确定性分配器一致，Readme 默认和 Warning 原级别遵循 SRS 已明确方向；不持久化具体勾选避免默认设置绑死当前编辑对象。限制是用户若期望默认分 Track、Preserve 或持久化固定 Track 集合，需要采用其他契约，且必须在 schema v2 发布前确定。
- 备选方案及差异：A. 默认 Per Logical Track + Compact，批量素材工作流更直接但首次导出产生多文件。B. 默认 Whole Project + Preserve，优先保持对应导出上下文的路由表示，但当前编译器分配本身已紧凑，两者初期通常同形。C. v2 保存 Explicit Track IDs，能形成固定默认子集，但对象删除/复制/损坏迁移与默认集合修复面显著扩大。D. 继续保留空 v1，只使用会话默认；这不满足 Project Export Settings 的正式持久化要求。
- 当前实施状态：三模式一次性任务、Routing 快照、Readme、冻结命名与文件事务既有实现保持；`ExportProjectSettings`、原子 History、`export-settings-v1.schema.json`/codec/package round-trip 与损坏恢复均已实施。
- 需要产品所有者回答：是否采用推荐方案？若不采用，请逐项给出默认模式、默认范围、默认 Track 策略（以及是否保存具体 ID）、默认 Routing、Readme 和 Warning-as-error，并确认仍采用 schema v2 + v1→v2 迁移而不是修改 v1。
- 产品回答：采用推荐字段与默认值，但当前处于开发期，直接修改 v1；不创建 v2 或迁移器。
- 最终处理与提交：SRS、ADR-CORE-037、领域设置、History、严格 JSON v1、package 打开/保存与覆盖测试已同步。具体显式 Track ID 与输出路径仍只属于任务快照。

### Q-NUI-004：Application Preferences 的本机路径与编码

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 17.2.2、20.14.1、20.14.6～20.14.9 节。
- 已确认事实：Application Preferences 自动保存于当前 Windows 用户本机，与 `.midora` 版本独立；读写失败使用安全默认值并显示非模态 Notice；不影响 Project、Modified 或 Undo/Redo。SRS 已固定偏好内容、音频默认值和禁止持久化项。
- 不确定点：SRS 没有规定本机具体目录、Windows Registry/文件选择、文本/二进制编码、文件名、大小上限或原子写入实现。
- 影响范围：仅当前 Windows 用户的本机偏好迁移、故障恢复和诊断；不影响 Project 文件兼容、canonical、可听语义、导出产物或公共音乐领域接口。
- 推荐方案：使用 `%LOCALAPPDATA%\Midora\preferences-v1.json`；采用独立 `schemaVersion = 1` 的 source-generated UTF-8 JSON、固定字段顺序、未知字段拒绝、1 MiB 读取上限，以及同目录临时文件 + flush + 原子 move/replace。文件不存在使用默认值且不报错；版本、格式、值域或 I/O 失败使用默认值并返回 `PreferenceReadFailed`/`PreferenceWriteFailed` Notice。
- 推荐依据与限制：LocalApplicationData 符合当前用户本机、无管理员权限的 Windows 应用惯例；JSON 便于开发期审计，独立版本可迁移；原子替换避免部分写入。限制是未来若改用 MSIX app data、Registry 或数据库，需要迁移或一次性回退默认值；初版本实现不提供手工导入/导出。
- 备选方案及差异：A. Windows Registry，权限和原子单值较成熟，但嵌套偏好、版本迁移和人工诊断不如文件直观。B. `%APPDATA%` roaming，可能被用户配置漫游，与 SRS“当前 Windows 用户本机”边界不完全一致。C. 二进制/SQLite，扩展和事务能力更强，但对当前小型单快照数据增加不必要依赖与兼容面。
- 当前实施状态：已实现 `ApplicationPreferencesStore` 与 `ApplicationPreferencesService`；覆盖确定性往返、原子替换、损坏/未知/超大/版本/写失败回退、边界值、Stopped-only 提交和缓存失效。
- 需要产品所有者回答：是否采用推荐方案？如需修改，请指定本机存储位置与编码；偏好字段、默认值及失败回退仍服从 SRS，不在本问题中重新决定。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-005：创建对象 Undo 后 `nextStableId` 与 Modified 的关系

- 类型：大决定
- 状态：已确认；已实施
- 发现日期：2026-08-06
- SRS 依据：第 3.10、3.12、16.5.3、16.13.2、16.27 节。
- 已确认事实：Project 保存 `nextStableId`；计数器必须持久化、单调递增、不补缺、不复用，所有现存对象 ID 全局唯一且小于它。创建、复制、Segment Split 右侧等操作必须分配新 ID；Project History 本身不持久化。Undo/Redo 要恢复对象原状态，Save 成功清除 Modified。
- 不确定点：创建对象后 Undo 若保留已推进的 `nextStableId`，可见对象和 canonical 已恢复但持久化源仍与保存点不同；若回退计数器，则违反“单调递增/不复用”的字面规则，并可能与 redo branch 中保留的对象 ID 冲突。SRS 没有说明仅存在于已撤销、从未保存且无存活引用的瞬态 ID 是否计入“不复用”。
- 影响范围：所有 Create/Duplicate/Split/Paste 等 Undo、Redo 身份、Modified 星号、关闭保存提示、`project.json` 的 `nextStableId`、redo 分支丢弃和跨会话 ID 安全。
- 推荐方案：`nextStableId` 在当前打开会话内永不回退；Redo 恢复原对象与原 ID，新分支分配更高 ID。History 判断 Modified 时把“只有已撤销瞬态分配导致的计数器空洞”视为不需要单独保存：Undo 回到保存点可清除 Modified；若以后因其他编辑 Save，则把更高计数器一并持久化。关闭一个 otherwise-clean Project 时允许丢弃这些从未持久化、无存活对象、无存活 redo history 的瞬态 ID；把“不复用”解释为当前会话及任何可存活/可持久化身份不得复用。
- 推荐依据与限制：该方案在当前会话内严格保持身份单调和 Redo 稳定，不会仅为不可见 allocator 空洞强迫用户保存；对象、引用、canonical 均无隐藏差异。限制是关闭后从旧保存点重开，未来可能再次分配一个只在已丢弃瞬态历史中出现过的数值；该瞬态身份没有文件、对象或 Undo 引用可观察。
- 备选方案及差异：A. 任何计数器推进都永久 Modified，Undo 创建后仍要求保存一个只有 `nextStableId` 变化的文件；最严格遵守字面单调，但用户工作流反直觉。B. Undo 创建时把计数器回退到命令前值，并在新分支复用；可实现字节级保存点恢复，但直接放宽当前“单调/不复用”规则。C. 将 allocator 高水位另存为 Application/会话状态；会让 Project 身份分配依赖文件外历史，违反 Project 自包含边界。
- 当前实施状态：全部现有稳定 ID 对象的创建/复制/Split/Paste 已接入统一 History；Embedded SF2 使用无副作用 staging、首次 Apply resource ID 分配和可逆资源租约切换；Mapping Chain 粘贴为新 Chain/Step ID。
- 需要产品所有者回答：是否采用推荐方案？如果不采用，请选择备选 A 或 B；C 不推荐且需要同时修改 Project 自包含不变量。
- 产品回答：采用推荐方案。
- 最终处理与提交：SRS 与 ADR-CORE-038 已同步；分配型创建/复制/Split/Paste/Embedded 选择均按首次 Apply 分配、Undo 高水位不退、Redo 原 ID 的统一协议实施并通过全 Application/Persistence 回归。

### Q-NUI-006：正常绑定期间 Last Known Instrument Name 的维护时机

> 历史问题：当前 SRS 第 24 章与 ADR-CORE-046 允许无音乐内容的 Logical Track 作为未绑定 shell；开始承载内容前必须取得唯一 Event Instrument Usage。跨 Usage 移动仍是原子换组，Definition 独立保留；本问题不得用于恢复旧 Folder/Last Known Name 或可见 parent tree。

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 7.12、7.20.2、11.3、16.10.3、20.8.4 节。
- 已确认事实：Logical Track 以稳定 ID 正式绑定 Event Instrument；名称不构成引用，也不得用于自动重绑。引用断裂或被引用 Instrument 删除后，应保留可用的最近绑定名称，仅供提示。名称和 Last Known Name 都按持久化 short text 保存。
- 不确定点：SRS 明确了删除/断裂后的结果，但没有逐操作规定正常绑定、显式取消绑定和已绑定 Instrument 重命名时，`lastBoundEventInstrumentName` 字段应何时刷新。
- 影响范围：仅以后发生删除、损坏或引用断裂时显示的提示文本，以及 Logical Track protobuf 中该快照字段的值；不改变稳定 ID 引用、编译、播放、MIDI、音频、资源分配或现有编辑是否进入 History/Modified。
- 推荐方案：成功绑定/改绑时把快照更新为目标 Instrument 当前名称；当前绑定的 Instrument 重命名时同步更新其所有已绑定 Track 快照；显式取消绑定时保留刚离开的 Instrument 名称；删除仍写入删除当时名称。Undo/Redo 精确恢复操作前的 ID 与快照值。
- 推荐依据与限制：这样“Last Known”在未来真正断裂时是最近一次用户可见名称，同时仍完全禁止按名称解析或修复。额外快照写入只发生在本来就会修改 Project 的绑定/重命名命令内，不新增独立 History entry。限制是 `.midora` 中已绑定 Track 也会保存一个可由 ID 解析出的冗余名称快照。
- 备选方案及差异：A. 只在删除或打开时发现断裂时写快照；正常绑定期间字段保持 null/旧值，文件更少冗余，但若目标对象内容已经无法读取，可能没有可用最新名称。B. 正常绑定时更新，但 Instrument 重命名不更新；实现更少联动，不过断裂提示可能显示历史旧名，不符合“最近”直觉。
- 当前实施状态：已在 `ProjectDomainEditCommands` 的绑定、取消绑定、Instrument 重命名和删除命令中按推荐方案实现，并覆盖精确 Undo/Redo；不影响底层按 ID 绑定规则。
- 需要产品所有者回答：是否采用推荐方案？如不采用，请选择 A 或 B；无论选择哪项，都不会启用按名称自动绑定。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-007：Logical Parameter Lane 重绑定的整数中点与 Enum 插值转换

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 11.13.1、11.19.2～11.19.5、18.2.5 节。
- 已确认事实：不兼容 Lane 重绑定必须由调用方显式选择 Clamp 或“丢弃范围外的值”；Double 转 Integer 先按目标 Integer 规则取整；转 Enum 先得到整数值，Clamp 时匹配最近已定义值且等距选较小值；Enum Lane 只允许 Step 状态。命令必须原子进入 Project History，并保留点稳定 ID。
- 不确定点：SRS 没有固定恰好位于 `n + 0.5` 时采用 AwayFromZero、ToEven 或其他中点规则，也没有逐句规定原 Linear point 重绑定为 Enum 后是自动改为 Step、拒绝整个重绑定，还是删除相关点。
- 影响范围：只影响用户明确执行类型不兼容 Lane 重绑定时的转换结果和曲线形状；不改变 `.midora` wire/schema、正常点编辑、编译器既有曲线求值、导出格式、音频后端或并发模型。命令可完整 Undo，因此修改成本局部。
- 推荐方案：整数中点统一采用 `MidpointRounding.AwayFromZero`；目标为 Enum 时，所有保留点的 interpolation 明确转为 `Step`。Clamp/Discard 仍是每次命令的显式参数，不设置隐藏默认；Enum 最近值等距时选较小值。
- 推荐依据与限制：现有 Mapping 整数目标的 `Round` 已使用 AwayFromZero，可避免应用修复命令与编译器常规整数化出现两套中点规则；Enum 的连续插值在 SRS 中本来就非法，转为 Step 可让显式修复完成后立即形成合法源数据。限制是原 Linear 段会变为阶梯状态，属于用户已选择重绑定到 Enum 时可预期但可听的变化。
- 备选方案及差异：A. 中点采用 ToEven，可减少统计偏差，但与现有 Mapping `Round` 不一致。B. 目标 Enum 遇到任何 Linear point 时拒绝，要求用户先单独改成 Step；最保守但会把一次修复拆成多步。C. 删除 Linear point；会产生比改为 Step 更大的数据损失，不推荐。
- 当前实施状态：`RebindLogicalParameterLane` 已实现显式 Clamp/Discard、强制 Enum 语义警告确认、AwayFromZero、Enum 最近值/等距较小值、Linear→Step、稳定 ID 保留和原子 Undo；自动测试覆盖确认门、Clamp、Discard、边界、精确恢复及 Full/Incremental 等价。
- 需要产品所有者回答：是否采用推荐方案？如不采用，请分别指定整数中点规则和目标 Enum 遇到 Linear point 时采用备选 B 或其他明确转换。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-008：删除非空 Mapping Chain 的确认与可选 owner 表示

- 类型：开发期源模型与局部编辑语义决定；已确认并实施。
- 首次记录：2026-08-06；后续取代决定：2026-08-16。
- SRS 依据：第 9.1.3～9.1.6、9.8、20.4.8、20.14.5 节；共享 Event Mapping 模型见 ADR-UI-030。
- 已确认事实：同一 SubVoice 的精确事件标量目标共享 Mapping。Note Number/Velocity 是编译器需要的强制共享 owner；非 Note 事件没有 Mapping 时可以直接输出原始值。Logical Parameter Mapping 本身也是独立可选 owner。删除 Chain 不应删除 Mapping Function、Envelope 或 Logical Parameter 资源。
- 取代原因：旧实现用 enabled 空链 sentinel 表示“已删除”，因此 Mapping Chains 列表中的非 Note 项始终存在，用户操作表现为无法删除。当前仍处于开发期，产品所有者已明确不要求旧数据兼容，继续保留 sentinel 没有成立的兼容依据。
- 最终方案：非空 Chain 删除要求一次显式确认。Note Number/Velocity owner 拒绝整链删除；非 Note `SubVoiceEventMapping` 和 `LogicalParameterMapping` 整链删除时物理移除 owner。Undo 恢复同一 owner、Chain、Step、Target Settings、引用和原顺序。事件点不随 Mapping owner 删除；对应 Event Lane 仍从原始事件投影并以原始值直通。普通事件点编辑、重新插入和同目标新增点不得重建已删除 owner；只有显式创建一个此前不存在的事件目标入口时可创建新的空 owner。
- 影响与边界：改变开发期 Project 对象图和序列化条目数量，不创建旧 sentinel 兼容读取分支；不改变 Note 强制 Mapping、Mapping Function/Envelope/Parameter 生命周期、canonical 唯一消费链或 Mapping ABI。删除与 Undo 进入 Project History，Full/Incremental 必须等价。
- 自动证据：覆盖非空确认、Note 拒绝、事件/参数 owner 物理删除、相同对象与索引 Undo、删除后事件编辑/新增点保持 raw pass-through、事件 Lane 继续可见，以及 Full/Incremental 等价。

### Q-NUI-009：Logical Parameter 类型、范围与 Enum 结构变更时的既有 Lane 迁移

- 类型：大决定
- 状态：已确认；已实施
- 发现日期：2026-08-06
- SRS 依据：第 9.8.2、9.8.10～9.8.12、11.12.3～11.13.4、11.19.2～11.19.5、18.5.2 节。
- 已确认事实：Logical Parameter Definition 可编辑名称、Integer/Double/Enum 类型、defaultValue、legal/display range 与 Enum items，修改必须进入全 Project Undo/Redo；Lane 按稳定 Parameter ID 绑定，正常重命名不破坏绑定。Integer、Double、Enum 对点值与插值有不同合法性，Enum 只允许已定义整数值与 Step。SRS 已为“把一条 Lane 显式重绑定到另一个 Parameter”规定 Clamp/Discard 和 Enum 语义警告，但没有把该规则扩展到“原 Parameter Definition 自身改变”。
- 不确定点：当类型、legal range、Enum 显式模式/数值/顺序/删除使当前 Project 中一条或多条既有 Lane Point 不再合法时，系统应拒绝 Definition 修改、保留失配数据并让编译失败，还是原子转换所有引用 Lane；若转换，还需决定逐 Lane 还是整次操作统一选择 Clamp/Discard、Double→Integer 中点、Linear→Enum、隐式 Enum 重排和删除项的语义。
- 影响范围：可能跨所有绑定同一 Event Instrument 的 Logical Track/Segment 批量改写 Lane Point，对默认状态、插值、Mapping 输出和可听结果产生大范围影响；还涉及 Undo 快照体积、诊断、复制、持久化源数据及未来 UI 确认工作流。稳定 ID 和 protobuf 字段可保持不变，但数据语义会改变。
- 推荐方案：Definition 编辑采用显式迁移计划并形成单个原子 History entry。仅重命名、合法 defaultValue、display range、不会使任何 default/Enum item/现有 Lane Point 失效的 legal range，以及不改变 Enum 数值身份的 item 重命名可直接提交。任何类型变更、Enum 显式模式切换、Enum 数值/顺序/删除或会使既有数据失效的 range 缩窄，都要求调用方明确选择 `Clamp` 或 `DiscardInvalidValues`，并对目标 Enum 确认语义警告；转换规则复用 Q-NUI-007 的 AwayFromZero、Enum 最近值等距取较小值和 Linear→Step，原子处理全 Project 所有引用 Lane。Enum item 稳定 ID 保留；被删除 item 的 Lane 数值按同一迁移策略处理。
- 推荐依据与限制：复用已有显式 Lane 重绑定规则，避免同一种不兼容转换出现两套舍入/Enum 语义；一次 Definition 修改与所有受影响 Lane 同事务，既不会留下中间非法状态，也能完整 Undo。限制是大 Project 的准备快照和转换成本较高，且 Enum 语义转换即使数值相近也不能保证音乐含义相同，因此必须显式确认。
- 备选方案及差异：A. 只要任何既有 Lane 会失效就拒绝 Definition 修改，要求用户先逐 Lane 修复；最保守但工作流繁琐，且多 Track 项目难以一次完成。B. 允许 Definition 修改但完全保留失配 Lane，让保存成功而 canonical 失败；最少改写数据，但一个高层接口编辑可使全工程不可播放，且恢复需逐点处理。C. 对所有失配值静默 Clamp/转 Step；操作简短但会在无明确授权下改变可听语义，不推荐。
- 当前实施状态：已实现 `MigrateLogicalParameterDefinition` 完整目标快照；类型/range/Enum 显式模式、数值、顺序、删除和新增与全 Project 引用 Lane 在单个 History entry 原子提交。转换复用 Q-NUI-007；新 Enum item 首次 Apply 分配 ID，Undo/Redo 保持身份。
- 需要产品所有者回答：是否采用推荐方案？如不采用，请选择 A 或 B，并分别说明类型变更、range 缩窄、Enum 模式/数值/顺序/删除的处理；C 不建议采用。
- 产品回答：采用推荐方案。
- 最终处理与提交：SRS 9.8.10、11.19.6、ADR-CORE-039 与 requirement trace 已同步；Clamp/Discard、Enum 确认、跨 Track Lane、点/Enum ID、失败前零分配和 Full/Incremental 等价均有自动测试。

### Q-NUI-010：空 SubVoice 是否产生 Info 诊断的 SRS 冲突

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 8.46～8.47、12.7.4 节。
- 已确认事实：两处都规定空 SubVoice 合法、不得自动忽略且仍计入 Channel Unit；第 8.47 明确要求实际编译使用时产生 Info，第 12.7.4 却明确写“空 SubVoice 是合法状态，不产生诊断”。Info 不导致失败，也不受 Warning-as-error 影响。
- 不确定点：同一 v0.1 SRS 对是否存在 Info 给出直接相反要求；不能同时满足。
- 影响范围：只影响实际参与实例的空 SubVoice 是否出现在编译诊断/未来诊断面板，以及相关自动测试；不改变资源占用、canonical MIDI、播放、导出、音频、持久化、Modified 或 Undo/Redo。
- 推荐方案：保留当前 `MIDORA1225 / Info`，仅当所属 Event Instrument 确有范围内实例时产生，并精确定位 Track/Instrument/SubVoice；不升级为 Warning，不阻止消费。后续修订 SRS 时把第 12.7.4 的“不产生诊断”改为“不产生 Warning/Error”。
- 推荐依据与限制：第 8.47 是专门定义“空 SubVoice 的诊断”的细化条款，包含等级和资源浪费理由，比第 12.7.4 的概述更具体；该选择也已形成编译与逐 Track 静音音频测试。限制是当前实现与第 12.7.4 字面不一致，必须由产品所有者确认冲突解释。
- 备选方案及差异：A. 删除 Info，严格采用第 12.7.4；资源占用仍正确，但用户无法从诊断理解静音 Channel Unit。B. 只在资源接近上限时提示；SRS 没有该条件，且会让相同空结构的诊断依赖其他实例，不推荐。
- 当前实施状态：已按推荐保留 `MIDORA1225`，现有编译器和 Audio Render 测试锁定 Info、静音输出与 Channel Unit 占用。
- 需要产品所有者回答：是否采用推荐方案？如不采用，将删除 `MIDORA1225` 及对应测试，但不改变空 SubVoice 的资源需求。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-011：共享音频 Worker 状态快照的 ABI v2 并发契约

- 类型：大决定
- 状态：已确认并实施
- 发现日期：2026-08-06
- SRS 依据：第 2 章确定性/失败原子性原则、第 13.30 节内部音频子进程与热路径零分配要求、INV-018～INV-028；`misc/Midora-Audio-Backend-Architecture-Decisions.md` 的 ADR-AUDIO-005。
- 已确认事实：当前共享内存 ABI v1 对每个对齐的 `Int32/Int64` 字段分别使用 Volatile 读写，且 Worker 是状态单写者；这能避免单字段撕裂，但不能保证包含 State、Position、RenderPosition、Underrun 和分配计数的整组快照来自同一次发布。发布方连续写两次相同 Playing/Rendering 状态时，读取方可能组合前一次与后一次字段；当前 v1 的 offset 68 被定义为必须为零的 reserved 字段，不能在仍声称兼容 v1 时静默改作序列号。
- 不确定点：初版尚未正式发布，SRS 没有固定内部共享 ABI 的版本号，也没有指定使用 seqlock、双缓冲还是允许仅保证逐字段一致。选择会改变进程间并发契约、协议兼容和确定性验证方式。
- 影响范围：实时播放、设备 Probe、离线渲染的状态/进度/故障读取，Native AOT Worker 与主进程的二进制兼容，零分配热路径和并发压力测试；不改变 Project、canonical、MIDI/WAVE 文件、可听语义或 UI 公共业务模型。
- 推荐方案：在初版发布前把共享控制协议升级为 ABI v2；保留总大小和其余 offset，把 offset 68 明确定义为对齐的 32-bit `statusSequence`。单一 Writer 每次发布先以原子增量变为奇数，再写完整字段，最后以 release 写/原子增量发布下一个偶数；Reader 读取偶数序列、复制全部字段、再次读取序列，只有两次相同且为偶数才接受，否则在固定上限内无分配重试，超过上限作为 IPC 一致性故障。Create 只建立 v2，Open 只接受 v2，主进程与 Worker 不做混合版本回退；压力测试验证从未观察到跨代组合、序列 wrap 不破坏相等判定、读写热路径零分配。
- 推荐依据与限制：seqlock 适合当前单 Writer、多次无锁 Reader 的小型固定快照，不增加映射大小，也不在音频热路径加锁或分配；显式升 v2 保持版本声明诚实。限制是极端持续写入时 Reader 可能达到重试上限并使任务失败，因此需要选择足够高且有界的上限；协议 v1 的测试 Worker 与 v2 主进程将被明确拒绝，必须同版本部署。
- 备选方案及差异：A. ABI v2 使用双状态槽加活动索引；Reader 更容易取得稳定槽，但要扩大并重排共享布局，复制/验证面更大。B. 保持 v1，只把 State 视作最后提交标志并允许同状态发布的字段跨代组合；兼容面最小，但无法证明整组进度和计数来自同一次发布，不推荐。C. 给状态读写加跨进程锁；可提供强快照，但阻塞与故障进程持锁风险不符合热路径约束，不推荐。
- 当前实施状态：已完成。共享控制 ABI 已因 Q-NUI-028 处于开发期 v2；offset 68 现定义为 `statusSequence`。四个完整状态发布入口统一执行 even→odd CAS、字段发布、release 写入下一偶数；Reader 仅接受前后相同的偶数序列，并在 1024 次有界无分配重试后报告 IPC 一致性错误。并发 Writer/中断发布被结构化拒绝；序列跨 `Int32` wrap 仍按位相等与奇偶规则工作。
- 需要产品所有者回答：已回答。
- 产品回答：采用推荐方案。该 ABI 是内部版本号，开发期升级合理，不适用外部 Project v1 的冻结原则。
- 最终处理与提交：SRS §13.30、ADR-AUDIO-005 与专项 requirement trace 已同步；测试覆盖跨代快照压力、奇数序列有界失败、并发/中断 Writer、序列 wrap、字段值域、损坏边界及状态发布/读取零托管分配。主进程与 Worker 只接受 ABI v2，不提供 v1 混合回退。

### Q-NUI-012：非 UI 发布门的精确 .NET SDK 与 NuGet 锁定策略

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 1 章/.NET 10 技术边界、第 21.3 节正确性与确定性优先级、INV-027；Mapping ABI 的 `Microsoft.NETCore.App.Ref 10.0.10` 仍由 INV-029 独立固定。
- 已确认事实：仓库全部项目目标框架为 `net10.0`，此前没有 `global.json`、NuGet lock files 或单命令非 UI 发布门；同一工作树会使用机器默认 SDK和当次解析出的传递包图。2026-08-06 的完整验证环境使用 `.NET SDK 10.0.302`；2026-08-30 将仓库精确固定版本显式升级为 `.NET SDK 10.0.400`。Mapping ABI 所需 reference pack 仍由 INV-029 独立固定；所有直接 PackageReference 已有显式版本。
- 不确定点：SRS 固定 .NET 10 和 Mapping reference pack，但未固定一般项目的 SDK feature band、是否允许 patch roll-forward、是否提交每项目 NuGet lock file，也未规定开发期可移植测试缺少原生 BASS/SF2 时应失败还是 Skip。
- 影响范围：开发/CI 机器准备、依赖还原、编译器与 Native AOT 产物可复现性、测试发现完整性和发布门维护；不改变 Project 文件、canonical、MIDI/WAVE、运行时用户设置或音乐语义。
- 推荐方案：提交 `global.json`，精确使用当前批准的 SDK `10.0.400`、`rollForward=disable`、禁止 prerelease；仓库级声明 `RuntimeIdentifiers=win-x64` 与 `RestorePackagesWithLockFile=true`，提交当前 38 个 `packages.lock.json`。普通开发测试在未配置原生集成资源时明确 Skip；正式 `Test-NonUIRelease.ps1` 必须显式给出经固定 manifest/hash 验证的 BASS 目录和一个现存 SF2，执行 locked restore、六个 solution Release build、Native AOT publish，再按版本化测试基线要求 10 个项目的当前精确计数全部通过且零 Skip；新增/删除测试必须显式评审并更新基线。
- 推荐依据与限制：精确 SDK和锁文件把构建输入从机器隐式状态变为提交内容；零 Skip 的正式门避免把缺少硬件/资源误报为通过。限制是安装了其他 .NET 10 SDK但没有当前精确固定版本的机器会在仓库根目录直接拒绝构建；安全升级 SDK/包时必须显式更新 `global.json`、基线及当前构建契约，评估 lock files，并重跑完整门。
- 备选方案及差异：A. SDK 使用 `latestPatch` roll-forward，安全补丁采用更方便，但不同时间/机器可能产生不同 AOT 与编译输出。B. 只固定直接包版本、不提交 lock files，文件较少但传递图仍可变化。C. 不固定 SDK，仅在发布记录中手工写版本；日常构建仍可能漂移，不推荐。
- 当前实施状态：已按推荐实现并在本机完整运行发布门；2026-08-30 的 SDK 10.0.400 升级同步刷新 ILLink/Native AOT 隐式依赖锁为 10.0.11，并通过固定 BASS 校验及 Native AOT Worker 发布验证。完整发布门仍须按当前测试基线独立执行。
- 需要产品所有者回答：是否采用推荐方案？如需允许 SDK patch roll-forward，请明确选择 A；NuGet 锁文件与正式零 Skip 门建议保留。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-013：正式分发 BASS 二进制的发布主体与许可放行

- 类型：大决定 / 外部发布门
- 状态：待确认；只暂停包含 BASS/BASSMIDI/BASSWASAPI DLL 的正式对外分发
- 发现日期：2026-08-06
- SRS 依据：第 21.6 节、INV-037、INV-038；根目录 `THIRD-PARTY-NOTICES.md`。
- 已确认事实：Midora 自有代码使用标准 MIT License，初版产品定位为免费、开源、非商业；该定位不把 BASS 变成开源依赖，也不自动证明任意发布主体满足 Un4seen 的免费使用条件。仓库不提交 DLL；当前技术发布门只从操作员目录复制三项固定版本/hash DLL到本地测试产物，并明确打印“分发授权仍是独立门”。
- 不确定点：尚未获得实际正式发布主体（个人/组织及其商业性质）、Midora 是否通过销售/广告/订阅/付费分发或其他方式获利、计划发布渠道与分发方式、正式发布日期有效条款的复核结论，以及要随包提供的供应商原始许可文件清单。技术实现不能代替权利人授权判断。
- 影响范围：任何包含 `bass.dll`、`bassmidi.dll`、`basswasapi.dll` 的 GitHub Release、安装包、压缩包、镜像或其他对外分发；不影响仓库 MIT 源码发布、不含 BASS 的构建、用户自行提供 DLL 的开发测试、编译/MIDI/持久化等非发声功能。
- 推荐方案：正式分发前由产品所有者冻结并书面记录：发布主体法定/公开身份及非商业性质；产品全部收入模式为无销售、无广告、无订阅、无付费分发；平台仅 Windows win-x64；具体渠道和是否由第三方镜像；按发布当日官方条款确认免费资格；把供应商要求的原始许可文本与现有 notices 一并纳入最终包。若任一事实不明确、发布主体具有商业性质、未来引入收入或条款解释有疑问，先联系 Un4seen 取得书面确认或购买适用许可，再放行含 DLL 产物。
- 推荐依据与限制：该方案严格执行 SRS 已确认的许可边界，不用项目“开源/免费”口号替代第三方授权。限制是实现方无法仅凭源码和自动测试自行完成主体资格、收入和届时条款的法律/商业事实核验；最终许可判断应由发布主体承担，必要时咨询专业人士或权利人。
- 备选方案及差异：A. 正式发布包不含任何 BASS DLL，仅提供校验工具和用户自行取得/配置流程；避免仓库方重新分发二进制，但首次使用流程更复杂，且仍需核验实际使用条件。B. 取得商业/其他明确许可后随包分发；成本与条款由权利人决定。C. 仅凭当前非商业声明直接随包分发；无法闭合 SRS 要求的主体/收入/渠道/届时条款核验，不可采用。
- 当前实施状态：技术构建、固定 hash/version 校验、runtime version gate、MIT/Third-Party Notices 复制和本地 Native AOT 测试产物均已实现；没有创建或推送正式发行包，也没有把 BASS DLL提交到 Git。
- 需要产品所有者回答：请提供实际发布主体、主体商业/非商业性质、全部收入方式、计划平台/渠道/分发形式，并选择推荐方案、A 或 B；在这些事实和届时条款核验完成前，本分支保持不放行正式含 DLL 分发。
- 产品回答：待填写。
- 最终处理与提交：待填写。

### Q-NUI-014：单应用实例是按 Windows 交互登录会话还是整机互斥

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 3.3 节、第 3.18.1 节和 INV-019；SRS 要求第二次启动转发给已有实例，并把具体转发与操作系统互斥机制留作实现细则。
- 已确认事实：Windows 的不同交互登录 Session 具有彼此隔离的桌面；一个 Session 中的 UI 进程不能可靠地激活另一个 Session 的窗口。内部 Native AOT 音频 Worker 不参与主应用实例互斥。
- 不确定点：SRS 的“整个系统只允许一个”没有明确区分同一 Windows 用户的多个远程/本地 Session，也没有规定跨 Session broker、服务或切换用户场景。
- 影响范围：只影响同一台 Windows 机器同时存在多个交互登录 Session 时，第二个 Session 能否独立运行 Midora；不改变单 Session 内的唯一实例、Project、持久化、canonical、MIDI/WAVE 或音频语义。
- 推荐方案：按当前 Windows 交互登录 Session 互斥。对象名包含稳定应用 ID 和 Windows Session ID，使用 `Local\\` 命名内核对象；第二次启动以 `CurrentUserOnly` Named Pipe 向同 Session 主实例转发。这样每个可见桌面最多一个主实例，并避免向不可见桌面转发。
- 推荐依据与限制：该方案不需要常驻 Windows 服务或跨 Session UI broker，符合桌面应用可操作边界。限制是同一机器的另一个登录 Session 可以运行自己的一个 Midora 实例；如果“整个系统”意图是机器级绝对唯一，则需另行设计跨 Session 授权和前台交互。
- 备选方案及差异：A. 整机 `Global\\` 互斥，最严格但第二个 Session 无法可靠激活首个 Session 的 UI，且需处理跨用户 ACL。B. 按 Windows 用户 SID、跨该用户全部 Session 互斥，需要 broker 决定请求应投递到哪个桌面并处理断开 Session，复杂度显著提高。C. 不做 OS 互斥只依赖窗口状态，存在竞态，不符合 SRS。
- 当前实施状态：已按推荐实现版本化、严格有界的启动 IPC v1；并发竞争只有一个 Primary，Unicode/空参数、畸形/截断客户端、队列上限、取消、释放与重新取得均有自动测试。WPF 只需在未来入口持有 lease 并消费请求队列。
- 需要产品所有者回答：是否采用推荐的“每个 Windows 交互登录 Session 一个 Midora 主实例”？如要求机器级绝对唯一，请选择 A；如要求同一用户跨 Session 唯一，请选择 B。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-015：Save Copy 目标等于当前 Project 文件时的处理

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 3.9.2 节、第 19.2.4 节；Save Copy 不得改变当前 Project path、Modified、Undo History、内存修改时间或当前文件版本信息，初版没有传统 Save As。
- 已确认事实：如果 Save Copy 直接覆盖当前 `.midora` 路径，磁盘上的“当前文件”会变成副本完成时快照，但内存仍按规范保持原 current file information、Modified 和 modified time；这会让当前路径的磁盘内容与打开会话状态分裂。
- 不确定点：SRS 未逐字规定文件选择器选中当前 Project 自身路径时应拒绝、转为 Save Project，还是允许覆盖。
- 影响范围：只影响 Save Copy 的目标路径预检查和错误提示；不改变 package 格式、普通 Save、其他副本目标、Project 源数据或音乐语义。
- 推荐方案：确定性比较完全限定 Windows 路径；若 Save Copy 目标等于当前 Project 路径，则在写文件前拒绝，并提示使用 Save Project。不得静默转成 Save，因为调用方明确选择的是不改变当前状态的命令。
- 推荐依据与限制：拒绝能同时保持 Save Copy 的全部“不改变当前状态”不变量和磁盘/会话一致性，也避免隐藏命令语义切换。限制是用户若确实想更新当前文件，需要回到普通 Save 命令。
- 备选方案及差异：A. 自动转为 Save Project，会改变 Modified/保存基线和内存 modified time，违背调用命令的显式语义。B. 允许 Save Copy 覆盖当前文件但不更新内存状态，会制造已确认的不一致，不可采用。C. 覆盖后自动重开副本，相当于未规定的 Save As/Project switch，不属于初版。
- 当前实施状态：已按推荐在 `ProjectPersistenceCoordinator` 中实现；覆盖前拒绝并保持原文件字节、current path 和文档状态，已有自动测试。
- 需要产品所有者回答：是否采用推荐方案？如希望自动转为普通 Save，请明确选择 A；B/C 不建议采用。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-016：缺失 metadata.json 时恢复时间戳的来源

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 3.6.1 节、第 16.13.9 节、第 16.18.3 节、第 19.3.5 节；`metadata.json` 缺失时必须用空 Metadata 默认值恢复、生成 Error 并标记 Modified，但规范未逐字指定只读 `createdAtUtc` / `modifiedAtUtc` 的替代值。
- 已确认事实：这两个 UTC 字段在 v1 `metadata.json` 中必填且必须满足 `modifiedAtUtc >= createdAtUtc`，不能表达 null；恢复后的 Project 必须允许用户普通保存。原实现隐式用 `TimeProvider.System` 构造恢复 Metadata，而 package 事务可使用注入时钟，两个时钟域不一致时会产生保存时间早于恢复创建时间并使保存失败。
- 不确定点：缺失 Metadata 时应使用打开时刻、原 package 文件系统时间、固定 epoch，还是增加未规定的“未知时间”表示。
- 影响范围：只影响 `metadata.json` 缺失这一已标记 Modified 的恢复分支及恢复后首次保存；不改变正常 Metadata、package schema、canonical、音频语义或迁移版本判断。
- 推荐方案：在 package 打开事务中使用该 `MidoraProjectPackageV1` 的 UTC `TimeProvider` 当前值同时初始化恢复 `createdAtUtc` 与 `modifiedAtUtc`；正常存在的 Metadata 随后仍按文件内容完整恢复。该时间表示“本软件建立恢复默认 Metadata 的时刻”，并与后续保存使用同一时钟来源。
- 推荐依据与限制：可确保恢复对象立即满足 v1 不变量、测试可重复且恢复后可保存；不依赖可被复制/改写的文件系统时间，也不伪造固定历史日期。限制是它不是原工程真实创建时间，UI 必须结合恢复 Error 明确该 Metadata 已丢失，不能把它描述为已证实的原始创建时间。
- 备选方案及差异：A. 使用 package 文件最后写入时间，可能被复制/解压/同步改写且不一定是工程创建时间。B. 使用 Unix epoch，确定且安全但会显示明显虚假的历史日期。C. 修改 v1 schema 允许 unknown/null，会破坏已发布兼容基线，不可作为小修采用。
- 当前实施状态：已按推荐让恢复构造器显式接收 package 时钟；增加缺失 Metadata 时间断言、恢复后普通 Save 与严格重开测试。
- 需要产品所有者回答：是否采用推荐方案？如偏好文件最后写入时间请选择 A；不建议 B/C。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-017：Project Switch 关闭流程的工程时长暂停边界

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 3.6.4、3.9.4、19.2.2、19.2.5 节；Project 打开期间的模态对话框、保存和任务窗口都累计工程时长，开始关闭后暂停，关闭取消时只恢复后续累计且不补计暂停时间。
- 已确认事实：New/Open/Close/Exit 共用 Stop → Draft → unsaved Save Guard → actual switch 顺序；Open/New 候选完成前旧 Project 必须保留；普通 Save 的工程时长快照应反映保存发生时的完整打开会话。现有 `ProjectCompilationSession` 已提供可组合的 `BeginProjectClosing` / `CancelProjectClosing`，但此前实际 Project Switch 工作流没有调用它们。
- 不确定点：SRS 的“Project 开始关闭”没有逐字指定为用户发出 New/Open/Close/Exit 请求时、进入未保存确认时，还是全部 Guard 通过并开始实际替换/释放当前 Project 时。
- 影响范围：只影响 Project Switch Guard 期间计入 `totalEditingTimeMilliseconds` 的时段和关闭取消后的恢复位置；不改变 Project 源音乐数据、Modified、Undo/Redo、metadata 修改时间、canonical、文件格式或可听结果。
- 推荐方案：Stop/cleanup、Draft、未保存确认和普通 Save 期间继续累计；全部 Guard 通过后，在调用实际 Project switch 动作前立即 `BeginProjectClosing`。实际切换成功保持暂停等待旧会话释放；实际切换异常或取消则 `CancelProjectClosing`，从失败/取消完成后的单调时钟位置继续，不补计实际切换尝试的暂停窗口。
- 推荐依据与限制：该边界同时符合“Project 打开时模态/保存/任务累计”和“开始关闭后暂停”，并保证 Open/New 候选失败、用户取消或 Save unavailable 不会让仍打开的旧 Project 提前停止计时。限制是用户停留在关闭确认对话框的时间仍计入工程总耗时；这是 SRS 对打开 Project 模态时段的明确规则。
- 备选方案及差异：A. 用户发出 Project Switch 请求即暂停；关闭确认、Draft 和可能很长的普通 Save 都不计时，与“打开状态的模态/保存累计”冲突。B. 实际 switch 成功返回后才暂停；实际资源替换/释放所耗时间会被计入，且失败路径不存在需要恢复的暂停窗口。C. 只对 Close/Exit 暂停，New/Open 替换旧 Project 不暂停；会让同一 Project 关闭语义因命令入口不同而不一致。
- 当前实施状态：已按推荐接入 `ApplicationTaskCoordinator.ExecuteProjectSwitchAsync`；自动测试覆盖 Guard Save 继续计时、实际切换入口暂停、成功保持暂停、未保存 Cancel 继续计时及实际切换失败恢复不补计。
- 需要产品所有者回答：是否采用推荐边界？如果希望用户一发出关闭/切换请求就停止累计，请选择 A。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-018：Recent Projects 的 MRU 与本机持久化策略

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 19.2.1 节规定 File 菜单包含 Recent Projects；第 20.14 节区分 Application Preferences 与不持久化会话状态，但没有定义 Recent Projects 的容量、排序、记录时机、失效路径或存储表示。
- 已确认事实：Recent Projects 不属于 Project Source Data，不能进入 `.midora`、Modified、Undo/Redo 或 canonical；New/Open 候选在实际 Project switch 前可能失败或取消，不能提前污染最近列表。现有严格 `preferences-v1.json` 已发布为固定字段集合，直接加入列表会让旧 reader 因未知字段拒绝整个偏好文件。
- 不确定点：列表容量、Windows 路径身份比较、何时记录、离线路径是否自动删除、是否记录时间戳，以及应扩展 Preferences 版本还是使用分离本机文件。
- 影响范围：只影响当前 Windows 用户的 File > Recent Projects 工作流和本机列表兼容；不改变 `.midora` 文件格式、Project 语义、音乐结果、输出文件或其他机器上的状态。
- 推荐方案：使用独立 `%LOCALAPPDATA%\Midora\recent-projects-v1.json`，最多 10 项、最新成功激活的持久化 Project 在首位，以 `Path.GetFullPath` + `OrdinalIgnoreCase` 去重。只有 Project 已成功提交为当前打开 Project 后才显式记录；失败/取消候选、Save Copy 和未提交的新建结果不记录。离线路径保留并显示不可用，直到用户显式移除/清空；不保存时间戳。文件使用 source-generated 严格 JSON v1、1 MiB 门和同目录原子替换，读失败空列表、写失败保留旧列表。
- 推荐依据与限制：10 项足以覆盖常用 MRU 且保持菜单简洁；分离文件不破坏 Preferences v1；成功激活边界避免候选污染；保留离线路径支持移动磁盘/网络位置。限制是列表不跨设备同步、不支持固定项目，用户必须显式清理长期失效项。
- 备选方案及差异：A. Preferences v2 内嵌列表，可统一文件但需要定义 v1→v2 迁移与降级行为。B. 最多 20 项，减少淘汰但菜单更长，仍需 UI 分组/滚动策略。C. 每次加载自动删除不存在路径，列表更干净但会误删暂时断开的可移动/网络工程。D. 记录候选验证成功而非实际激活，会把随后取消的 Open/New 放入列表。E. 保存 UTC 最近打开时间，便于未来排序/展示但增加 SRS 未要求的墙钟语义与隐私数据。
- 当前实施状态：已实现独立 `RecentProjectsStore/Service`；覆盖 10 项 MRU、Windows 大小写去重、严格重复/未知字段、确定性往返、1 MiB 上限、离线路径保留/动态可用投影、原子失败保持、移除和清空。WPF 后续只在 Project switch 完成分支调用记录入口。
- 需要产品所有者回答：是否采用推荐方案？如需 20 项请选择 B；如需 Preferences v2 统一存储请选择 A；C/D/E 不建议采用。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-019：小节中途 Time Signature 变化的 Bar:Beat:Tick 语义

- 类型：大决定
- 状态：待确认；暂停 Project 音乐位置/小节线/拍网格换算服务
- 发现日期：2026-08-06
- SRS 依据：第 4.9、20.13.3 节；Bar/Beat 从 1 开始、Tick offset 从 0 开始、Project 起点为 `1:1:0`，Beat 使用当前 Time Signature 分母单位且不推断复合拍大拍；修改拍号不移动任何绝对 tick。
- 已确认事实：当前领域与持久化允许 Time Signature 位于任意非负 tick，仅要求 tick 0 恰有一个、同 tick 唯一、分子 1–99、分母为 1/2/4/8/16/32/64；SRS 没有要求变化点落在既有小节边界。若变化发生在小节中途，必须先定义该变化点与前后小节编号的关系，才能确定所有后续 Bar:Beat:Tick、网格线、snap 和定位结果。
- 不确定点：中途拍号变化是立即截断当前小节并从新小节开始、在当前不完整小节内切换 beat 单位但不增加 Bar、延迟到旧拍号的下一小节边界生效，还是把这种变化认定为非法。
- 影响范围：Project 时间坐标显示与反向解析、Arrangement/Segment 映射位置、小节线/拍网格/snap、Marker 与播放光标定位，以及拍号编辑后的后续坐标重算；不改变事件绝对 tick、Tempo 秒时间、canonical MIDI 事件或 `.midora` 已存 tick。
- 推荐方案：每个 Time Signature 变化 tick 都立即成为一个新小节边界；如果它位于旧小节中途，旧小节是被截断的不完整小节，变化点的坐标为下一 Bar 的 `Beat 1, Tick 0`，此后按新分子/分母推进。位于 tick 0 的初始拍号仍是 `1:1:0`，位于原本小节边界的变化只正常开启下一 Bar。
- 推荐依据与限制：拍号事件从其精确 tick 正式生效，不需要延迟或移动源事件；前后每个 tick 都有唯一可逆坐标；小节线与 beat 单位在同一 tick 一致切换。限制是误放在小节中途的拍号会产生短小节并使后续 Bar 编号增加，UI 应明确显示变化点，不能静默吸附。
- 备选方案及差异：A. 中途变化继续使用同一 Bar 编号，并在变化点把 Beat 重置为 1；一个 Bar 内会出现两个 `Beat 1`，若格式不增加额外段号则 tick→坐标不可逆。B. 拍号延迟到旧拍号下一小节边界生效；存储 tick 与显示/网格实际生效 tick 不一致。C. 禁止中途变化并要求编辑命令吸附/拒绝；会把当前 SRS 允许的 Project 数据新增为语义错误，并影响已有文件兼容。D. 中途变化立即切换 beat 单位但 Beat 序号连续；不完整拍单位与 Tick offset 定义复杂，且可能产生 Beat 超过新分子的坐标。
- 当前实施状态：尚未实现 Bar:Beat:Tick/网格换算，避免把推荐方案伪装成 SRS 既定事实；现有 Time Signature 领域、持久化、编译与 MIDI 导出继续保留精确绝对 tick，不受暂停影响。
- 需要产品所有者回答：是否采用推荐的“变化 tick 立即开启下一 Bar，允许短小节”？如需禁止中途变化请选择 C，并确认对已存在中途变化的 `.midora` 应报 Error 还是打开后标记需修复。
- 产品回答：待填写。
- 最终处理与提交：待回答后实施并补 tick↔Bar:Beat:Tick、边界、溢出和随机往返测试。

### Q-NUI-020：Global Event Scope Defaults 的初版正式字段与语义

- 类型：大决定
- 状态：已确认并完成规范收口
- 发现日期：2026-08-06
- SRS 依据：第 2.3、3.2、3.4、3.11.2、12.22.5、17.2.1、18.9.1 节；SRS 将 Global Event Scope Defaults 列为必备 Project Content、Modified 来源、Project Settings 子页，并规定修改后可能改变 Reset 输出、状态作用域、资源释放和事件排序。
- 已确认事实：SRS 没有列出该对象的任何字段、枚举、默认值、作用层级、可覆盖事件类型或冲突解决规则。现有领域类型只有固定辅助规则“Note 非 Channel-Wide，其余 Template Event Channel-Wide”；已发布严格 v1 `settings/global-event-scope-defaults.json` 只有 `schemaVersion`，不能向 v1 静默增加字段。
- 不确定点：初版是否真的允许用户配置事件作用域；若允许，需要明确可配置对象（Event Kind、MIDI target、Initial/Reset、Logical Parameter 输出等）、候选作用域、默认表、Event Instrument/SubVoice 覆盖关系、非法组合、Reset/资源占用和同 tick 排序语义。
- 影响范围：领域模型、Project Settings/History/Modified、canonical 编译与缓存 fingerprint、Channel Group 共享/隔离、Reset、播放/预览/MIDI/音频一致性、JSON schema v2 和旧 v1 迁移；选择不同会改变可听结果与文件兼容性。
- 推荐方案：初版把该文件确认为“固定策略的版本化 marker”，不提供可编辑字段；正式规则继续由各事件语义固定：Note 是逐实例事件，Bank/Program/CC/Pitch Bend/RPN/NRPN/Pitch Bend Range 等是 Channel-Wide 状态。将 SRS 中“修改 Global Event Scope Defaults”解释为预留的未来版本入口，并在后续 SRS 修订中明确初版不可编辑。理由是当前没有足够信息定义安全、可逆且兼容的配置语义，空 v1 已经锁定；未来如新增配置必须发布 schema v2 与迁移规则。
- 推荐依据与限制：该方案不发明会影响听感的隐式默认，不改变已发布 v1，不扩大初版复杂度；限制是与当前 SRS 把它列为可修改 Project Content 的字面表述存在冲突，必须由产品所有者明确选择并在后续规范修订中消除。
- 备选方案及差异：A. 初版新增可配置字段；必须先完整回答上面的作用对象、枚举、默认、覆盖与冲突问题，并设计 schema v2，不能只给一个布尔值。B. 删除该 Project Content/文件；会破坏已发布 package 固定入口与 v1 兼容，不推荐。C. 保留空文件但仍在 UI 显示可编辑页；没有可提交语义，会制造虚假设置，不推荐。
- 当前实施状态：领域与持久化保留空 v1 marker；编译器按具体 MIDI/Event Instrument 语义执行既有固定作用域，不读取可配置 defaults；未实现 schema v2、History 或 UI 接线。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-07，采用推荐方案。
- 最终处理与提交：既有领域、空 v1 schema/codec 与编译器固定语义保持不变；2026-08-08 已同步修订 SRS 第 3、12、16、17、18 章，移除初版可编辑设置、Modified 来源和独立缓存失效入口。未来配置仍必须另行版本化。

### Q-NUI-021：超过 SMF 四字节 VLQ 上限的 MIDI Track 长间隔

- 类型：大决定
- 状态：已定案；旧严格拒绝实现仍在，新导出填充规则待实施，不再等待用户选择
- 发现日期：2026-08-06
- SRS 依据：§14.12.2、§14.12.8～9、§14.15.7、§14.19.8、§12.23.2、§23.12 和 INV-118；详细决定与验证计划见 [ADR-SMF-001～002](Midora-SMF-Export-Timing-Padding-and-Size-Limits-Architecture-Decisions.md)。
- 当前源码事实：四字节 VLQ 上限为 `0x0FFFFFFF`（268,435,455）。`StandardMidiFile` 仍直接拒绝超长事件/EOT delta；MTrk 大小目前写完整条 Track 后才检查。延迟分页编码的异常归属仍需与立即编码路径一起修正，不能承诺所有失败都已在写入事务之前被预检。
- 历史决定：2026-08-07 采用严格失败、不插占位，已有相应 exporter/task 测试。历史测试结果保留，但已不代表新规则验收通过。
- 产品回答：2026-09-10 改为固定零长度 Text Meta `FF 01 00` 填充每条 Track 的首段、事件间段和 EOT 尾段；不修改原事件 Tick/顺序，不改变编译、canonical、播放或音频渲染，也不增加编译 delta 扫描。
- 大小决定：用户明确撤回 MTrk 拆分方向。单个数据区最多 `0xFFFFFFFF` 字节（不含 8 字节头），超过时只拒绝本次 MIDI 导出，不拆 Track/文件、不截断、不扩大到整个文件限制，编译不感知。TPQ、Tempo、事件值、单条 payload、ntrks 等其余硬限制仍拒绝。
- 资源与提示：先用安全整数算术判断占位成本与 Track 字节预算，再有界流式写入并支持取消；成功只给导出级汇总 Info/README，不逐条输出占位。空 Text 重新导入按既有 opaque 规则保留，不擅自删除来源空文本。
- 需要产品所有者回答：无；不重新开放已决定的拆分或占位类型选项。
- 最终处理：本轮仅更新文档/SRS，未修改源码或测试、未提交/推送；产品实现排入 Logical 编译结果内存优化之后的极端 Tick 防护工作。

### Q-NUI-022：虚拟键盘按住预览的未知 Gate Length 与实时 canonical 语义

- 类型：大决定
- 状态：待确认；暂停 Event Instrument/SubVoice 虚拟键盘 held-preview 动态 Gate 分支
- 发现日期：2026-08-06
- SRS 依据：第 12.2.4、13.19、13.21～13.23 节；鼠标按下必须立即开始发声，按住期间 Gate 未结束，松开时发送 Gate End，实际按住时长作为临时 Gate Length 进入 MappingContext，全部输出必须来自 Preview CompileContext 的 canonical compiled result；实时链还使用可配置 20–2000 ms Render-Ahead。
- 已确认事实：C# Mapping ABI v1 从第一个模板事件起即可读取 `GateLength`。松开前最终 Gate Length 尚未知，因此 tick 0 的 Mapping 输出可能依赖未来输入；任何实时实现都无法同时知道该最终值并在按下瞬间输出。当前后端接收冻结 `MidiRenderPlan`，Render-Ahead producer 会提前生成尚未由设备消费的 PCM；松开时以已消费 frame、已渲染 frontier 或清空/回滚 ring 后的 frame 作为 Gate End，会产生不同 Gate 长度与响应延迟。当前代码只实现 SRS 13.22.4 的固定长度 Preview 按钮路径，没有 held-preview Gate Start/End API。
- 不确定点：held preview 是否允许使用与最终固定长度 canonical compile 不完全等价的因果实时语义；Gate End 应落在用户输入时对应的已消费音乐 frame、包含设备缓冲的下一可播放 frame，还是 producer 尚未渲染的 frontier；是否允许 held preview 使用小于用户 Render-Ahead 的专用缓冲；Mapping 在 Gate End 前读取 `GateLength` 时应得到什么稳定值。
- 影响范围：可听触发与 release 时机、C# Mapping ABI v1 运行语义、PreviewCompiler、PlaybackController/backend 接口、进程内与子进程 Worker 协议、render-ahead ring 回滚/重建、零分配热路径、确定性测试和硬件输入延迟测试。该选择不应改变普通固定长度 Preview、主播放、MIDI 导出或音频渲染。
- 推荐方案：为 held preview 明确定义“因果 Gate”子上下文：Gate Start 时 MappingContext 的 `GateLength` 使用固定哨兵 `Int64.MaxValue` 表示尚未结束；Gate End 后只对尚未渲染的边界及后续 Release/Tail 使用冻结的实际 Gate Length，Preview 消费范围硬边界再执行最终 Reset；不承诺整段字节等价于事后以最终 Gate Length 执行的一次固定预览编译。Gate End 固定落在 producer 尚未渲染的第一个 frame，禁止回写已消费/已缓冲 PCM；因此可听 release 最多增加当前 Render-Ahead，任务报告该输入到生效延迟。保持用户 Render-Ahead，不引入回滚和 callback 同步。
- 推荐依据与限制：该方案满足因果性、按下即时、热路径零分配和现有 ring/子进程模型，且不会伪造对未来 Gate Length 的预知。限制是它需要正式新增 held-preview canonical 语义和 MappingContext 哨兵约定，松开响应会受 Render-Ahead 影响，并与“实际 Gate Length 进入 MappingContext”及单次 canonical 结果的字面要求存在偏差，必须由产品所有者明确接受并修订 SRS/ABI 说明。
- 备选方案及差异：A. 延迟到松开后才按最终 Gate Length 编译并开始播放；保持单次 canonical 等价，但直接违反“按下立即开始发声”，听到的是事后回放。B. 按下用 `previewGateLength` 或滚动预测值编译，松开重编尾部；开始及时但早期 Mapping 结果依赖猜测，且 GateLength 预测变化会破坏确定性。C. 在 held preview 中禁止 Mapping 读取 GateLength，或固定返回 0；会改变 ABI v1 已确认语义并使同一 Instrument 在 Preview 与正式 Track 中不同。D. Gate End 映射到用户输入时的已消费 frame，丢弃其后的 render-ahead PCM并从该点恢复/重渲染；最接近实际按住时长，但要求 sample-domain 状态快照、BASSMIDI stream 回滚、ring 原子替换与防 underrun，显著扩大并发协议，且 Mapping 对未知最终 Gate Length 的前因问题仍然存在。E. 为 held preview 强制极小专用 Render-Ahead，再在下一未渲染 frame 生效；响应更快，但静默覆盖用户设置、设备稳定性和性能契约。
- 当前实施状态：固定长度 Event Instrument/SubVoice Preview 与 Segment Preview 已通过 canonical/统一音频链；held-preview 未实现，不以即时裸 MIDI 或停止重启冒充。其他非 UI 分支继续。
- 需要产品所有者回答：是否采用推荐的“因果 Gate + `Int64.MaxValue` 未结束哨兵 + 未渲染 frontier 生效”方案？若更重视事后 canonical 完全等价请选择 A；若要求接近输入瞬间的 release 请选择 D，并接受新增回滚协议与仍需另定 GateLength 前因规则。
- 产品回答：待填写。
- 最终处理与提交：待回答后新增 ADR、held PreviewContext/Compiler 契约、backend/Worker Gate 命令、进程内与子进程一致性测试、Mapping GateLength 测试、不同 Render-Ahead/设备 block/松开边界测试及人工延迟验收。

#### 2026-08-07 产品补充决定

- 当前 SRS 核查：第 18.2.3 节已要求 Segment Editor 左侧 Pitch Ruler “点击键位可发起预览”，但未规定 held Gate 的编译与音频语义；SRS 原先没有规定放置单个 Logical Note 时的预览。
- 产品补充：两项都属于初版范围，并且都是 Gate End 前最终 Gate Length 未知的预览，统一采用 Q-NUI-022 已确认的推荐方案。
- 规格处理：已在 SRS 第 9.7.7、12.2.4、13.22.7、13.24.5、18.2.3～18.2.4、20.19 节和 INV-039 中固定同一因果 Gate、`Int64.MaxValue` 哨兵、未渲染 frontier、清理、互斥及编辑不受预览失败阻断的规则。
- 代码状态：本次不改代码；实现时不得为钢琴卷帘增加直接 MIDI 或独立固定长度试听捷径，须与 Event Instrument / SubVoice held Preview 共用协议、编译和音频链测试。

### Q-NUI-023：Project TPQ 合法范围与 SMF 15-bit division 上限

- 类型：大决定
- 状态：已确认并实施
- 发现日期：2026-08-06
- SRS 依据：第 4.1.2、4.14、14.2.2、16.7.3 节；TPQ 在创建 Project 时确定且之后不可修改，MIDI division 必须直接使用 Project TPQ，导出时不允许重指定、升采样或降采样；SRS 把“TPQ 非法”列为 Error，但没有给出数值范围。
- 已确认事实：当前领域、Project 创建和已发布 `project-settings-v1.schema.json` 明确接受 `1..Int32.MaxValue`。SMF 的 TPQ division 是最高位必须为 0 的 15-bit 正整数，`StandardMidiFile` 因此只接受 `1..32767`；`CanonicalMidiFileExporter` 会把更高 TPQ 转换为结构化 Encoding Error，Artifact/Task 不发布 partial。高 TPQ Project 仍可编译、播放、音频渲染和持久化，但由于 TPQ 不可修改，也禁止导出换算，它在初版没有任何成功 MIDI 导出路径。
- 不确定点：初版 Project TPQ 是否本应限制为 `1..32767`；若是，已经允许到 `Int32.MaxValue` 的 v1 文件应继续打开但标记不可导出、迁移到新版本、还是作为结构错误拒绝；若高 TPQ 继续合法，是否正式接受“该 Project 不支持 MIDI 导出”的永久能力差异。
- 影响范围：Project 创建、领域构造、`project-settings` JSON schema 与文件兼容、打开/迁移、Event Instrument 默认 Template Length、时间换算、MIDI 导出可用性、诊断和创建 UI。不同选择不应改变既有合法低 TPQ Project 的事件 tick。
- 推荐方案：保留 v1 的 `1..Int32.MaxValue` 读取兼容，但把初版新建 Project 的 TPQ 输入限制为 `1..32767`；打开既有高 TPQ v1 时允许编辑、播放、音频和保存，并产生持久的 MIDI Export 不可用诊断，不自动改写 TPQ。后续若产品需要把高 TPQ 转换为可导出工程，另行设计显式“另存并重映射全部 tick”的版本化转换工具，不属于普通 Save 或 MIDI Export。
- 推荐依据与限制：不破坏已经发布的严格 v1 schema/文件，不让新建工程进入无法导出的状态，也遵守“创建后不可修改”和“导出时不重采样”。限制是同一 v1 schema 中仍存在历史兼容的高 TPQ 值，且它们永久缺少初版 MIDI Export；Project 创建范围与持久化可读取范围将有意不同，必须在规范和诊断中明确。
- 备选方案及差异：A. 将领域、创建、schema v1 全部收窄为 `1..32767` 并拒绝高 TPQ v1；规则最简单，但属于对已发布 schema v1 的不兼容修改，不推荐。B. 保持所有正 `Int32` TPQ 都可新建，MIDI 导出时结构化失败；实现现状最小，但用户可能创建后才发现永久无法导出。C. MIDI 导出时自动缩放 tick/division 到 32767 或其约数；会违反 SRS 明确禁止的导出重采样，并改变取整、同 tick 合并和文件字节。D. 允许创建后修改 TPQ 并重算全部内容；SRS 明确排除，且需要大型迁移/Undo/可听语义设计。
- 当前实施状态：Domain、New Project admission、Semantic Validation、开发期 project-settings v1 schema/codec 已统一收窄为 `1..32767`；SMF 继续直接写 Project TPQ，不自动重采样。
- 需要产品所有者回答：是否采用推荐的“新建限制 1..32767、既有高 TPQ v1 保持可打开但明确不可 MIDI 导出”方案？如选择 A，需明确授权发布不兼容的 schema v1 修订；如选择 C/D，需先另行冻结 tick 重映射算法与迁移契约。
- 产品回答：A。直接修改开发期 v1，不建立兼容或迁移分支。
- 最终处理与提交：SRS、ADR-CORE-036、领域/创建/语义验证、schema/codec 与边界测试已同步；完整证据记录在 `Midora-TPQ-Range-Requirement-Trace.md`。

## 4. 问题模板

### Q-NUI-XXX：标题

- 类型：小决定 / 大决定
- 状态：待确认 / 已按推荐实施待确认 / 已确认 / 已修改 / 已关闭
- 发现日期：
- SRS 依据：
- 已确认事实：
- 不确定点：
- 影响范围：
- 推荐方案：
- 推荐依据与限制：
- 备选方案及差异：
- 当前实施状态：
- 需要产品所有者回答：
- 产品回答：
- 最终处理与提交：

## 5. 已回答问题

### Q-NUI-001：损坏内嵌 SF2 的再次保存表示

- 类型：大决定
- 状态：已确认
- 发现日期：2026-08-06
- SRS 依据：第 6.7.1、6.7.3、16.7.6、16.15.4、16.21.3、16.27.4 节。
- 已确认事实：内嵌 SF2 的 settings hash、manifest hash、未压缩实际字节及文件大小必须一致；导入与写包必须流式处理；hash 不匹配或资源损坏不阻止 Project 打开、不标记 Project 已修改；被动资源状态不得改写 `soundfont-settings.json`。
- 不确定点：SRS 原文没有定义 Embedded 引用仍存在而资源已损坏/缺失时，如何同时满足允许保存、三方内容身份一致和禁止被动改写 Project。
- 影响范围：`.midora` v1 文件兼容性、损坏恢复工作流、普通保存与 Save Copy 原子事务、自校验、资源诊断，以及未来 UI 对损坏资源的操作提示。
- 推荐方案：保存前必须由用户明确选择修复动作；无动作时持久化层返回结构化的资源修复错误且不发布文件。可选动作固定为重新绑定/替换内嵌 SF2，或明确清除 SoundFont 引用；修复后重新保存。
- 推荐依据与限制：保持 settings、manifest、实际字节三方一致，不静默改变 Project，也不生成新的已知损坏包。该决定是对第 16.15.4 节“允许保存”的产品解释：允许在完成显式修复选择后保存。
- 备选方案及差异：A. 原样保留损坏字节会故意生成三方不一致的新包。B. 省略资源会生成已知缺失包。C. 静默清空引用会被动改变 Project。D. 更新 hash/size 会把损坏内容当作用户主动接受。
- 当前实施状态：已实现 `MidoraEmbeddedSoundFontRepairRequiredExceptionV1`；明确携带资源状态、预期/实际身份和仅有的两个修复动作。Preflight 可识别缺失、损坏或错配租约；staging 可识别复制期间变化；两者都在发布前原子失败。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-06，采用推荐方案。
- 最终处理与提交：代码和测试已完成，提交 `4efb0f6`。

## 6. 人工试听 / 手动硬件测试问题

本节不是产品决定。只在自动测试无法替代时，集中记录可复制命令、前置条件、预期结果和产品所有者返回结果。

完整命令、前置条件、逐项预期和返回模板见 `misc/Midora-Manual-Audio-Acceptance.md`。2026-08-07 首轮结果及完整错误输出保存在 `misc/Midora-Manual-Audio-Acceptance-Snapshot-2026-08-07.md`；后续复测不得覆盖该快照。

| 编号 | 类型 | 内容 | 状态 |
|---|---|---|---|
| M-AUD-001～003 | 离线人耳试听 | Segment 边界、SubVoice/Mapping、Tempo/Loop | 全部通过；002 确认只有短暂平滑截断，不爆音，整体听感符合预期 |
| M-AUD-004～006 | 实时进程内对照 | 与离线语义一致、无爆音/悬挂/断续 | 全部通过 |
| M-AUD-007～009 | 正式子进程拓扑 | 与进程内/离线语义一致、物理输出正常 | 全部通过 |
| M-AUD-010 | 物理设备 | enabled output endpoint 枚举与静音 callback probe | 通过：Beats Flex、48 kHz、27 callbacks、0 B、无 fault |
| M-AUD-011 | 物理设备 | 跟随系统默认时切换默认输出的受控失败 | 通过 |
| M-AUD-012 | 物理设备 | 活动 USB/蓝牙输出移除或禁用的受控断开与人工重选门 | 通过：0 B callback/child allocations、0 IPC underrun、无 child fault/异常堆栈，明确人工重选提示，返回码 2 |
