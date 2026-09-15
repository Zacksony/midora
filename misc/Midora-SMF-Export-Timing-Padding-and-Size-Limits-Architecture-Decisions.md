# SMF 导出：超长 delta 填充与单个 MTrk 大小限制

日期：2026-09-10。状态：**产品所有者已定案，规范已同步；后续产品实施与自动验证已完成，待人工验收**。实施、实测及剩余边界见[实施记录](Midora-SMF-Encoding-Boundaries-Implementation-2026-09-10.md)。

本文最初为只修改文档的定案记录；§7～9 保留当时的源码与验证状态，不代表后续实现仍缺失。软件版本 `1.0.0-dev`、Project Format 3、presentation schema 2 和已有旧格式读取能力不变。

## 1. 决策与需求追踪

用户明确决定：单个 MTrk 不做超限拆分，保留其 32-bit chunk length 上限，仅在 MIDI 导出时报错，Compiler 不感知该大小限制；其余采用导出时填充超长 delta、严格拒绝其他硬限制的方案。

规范依据：[SRS 第 14 章](Midora-SRS-Initial-Release-v0.1/14-MIDI-Export.md) §14.12.2、§14.12.8～9、§14.15.7、§14.19.8，[第 12 章](Midora-SRS-Initial-Release-v0.1/12-Compilation-and-Canonical-Compiled-Result.md) §12.23.2，[第 23 章](Midora-SRS-Initial-Release-v0.1/23-Pure-MIDI-Tracks-and-SMF-Import.md) §23.12，以及 [INV-118](Midora-SRS-Initial-Release-v0.1/22-Requirement-Locator-and-Cross-System-Invariants.md)。线格式参考入口为 MIDI Association 的 [Standard MIDI Files 规范](https://midi.org/standard-midi-files-specification)；本文不扩展 SMF 标准。

| 维度 | 定案 |
| --- | --- |
| 输入 | 成功、完整、冻结的 MIDI Export canonical、SMF Track Projection、每 Track 的事件顺序/EOT、本次输出规划 |
| 正式输出 | 原事件位置与顺序不变、必要时带空 Text Meta 占位的 SMF Type 1；按需生成 README 汇总 |
| 时间边界 | 内部 Tick 继续服从既有非负 Int64 及安全运算要求；单条线格式 delta 为 `0..0x0FFFFFFF` |
| 大小边界 | 每个 MTrk 数据区最多 `0xFFFFFFFF` 字节；不含 8 字节 chunk 头，不是整个文件上限；不拆 Track |
| 失败 | 非法语义仍由原验证负责；文件编码硬限制、磁盘、取消和自校验沿任务事务处理，不发布部分成功 |
| 诊断 | 可容纳的填充为导出级汇总 Info；MTrk 大小等超限为导出编码 Error，不伪装为编译 Error 或普通 I/O 错误 |
| 持久化 | 不修改原 Project、`.midora`、presentation、Undo/Redo、Modified；占位仅在导出产物中 |
| 运行时 | 字节预算、占位计数、进度和取消属于导出任务；不进入 Worker、播放/音频缓存 |
| 非目标 | 不更改编译事件集、Full/Incremental 算法、Track 拓扑、Running Status 策略或音频；不支持超限 MTrk、自动拆文件、拆 payload、降低 TPQ或截断音乐 |

## 2. ADR-SMF-001：只在导出时分段编码超长 delta

**已接受。** 取代 Q-NUI-021 和 ADR-CORE-010 中“任何单间隔超过四字节 VLQ 就失败，绝不插入 Meta spacer”的旧产品决定。旧测试仍是旧实现的证据，不是新规则的验收证据。

采用固定的零长度 Text Meta：`FF 01 00`。它不发送 Channel 消息，不改变音符、CC、Tempo、路由或生命周期。必须由每个 MTrk 的流式编码器独立插入，不在 canonical 中创建模拟事件、不在 Project 中增添 Marker/Text、不让 Compiler 或增量编译多做一次 delta 扫描。

原事件先按冻结导出起点重基；设其与本 Track 前一编码事件的间隔为 `D`，`M = 268,435,455`：

```text
D = 0：原事件前写 delta 0，不加占位。
D > 0：N = floor((D - 1) / M)。
写 N 次：VLQ(M) + FF 01 00。
再写：VLQ(D - N × M) + 原事件。
```

这是整数计算，不能先把 Tick 转成 double。每个占位的前置 delta 固定使用四字节 `FF FF FF 7F`，因此占位总成本为 `7 × N` 字节。应先做预算检查，再以有界块流式写入；不生成 N 个托管事件对象。

| 原间隔 D | 占位数 N | 原事件前的剩余 delta |
| ---: | ---: | ---: |
| 0 | 0 | 0 |
| M − 1 | 0 | M − 1 |
| M | 0 | M |
| M + 1 | 1 | 1 |
| 2M | 1 | M |
| 2M + 1 | 2 | 1 |

相同规则用于首事件、任意两事件之间和最后一个事件到 EOT。包括只有结构 Meta/EOT 的 Track，以及 Conductor、Pure MIDI、Logical Unit 各路径；不能因为另一条同 Port.Channel Track 中存在事件，就省略本 Track 必需的时间编码。

必须保持原始同 tick 事件的相对顺序、NoteOff velocity、Bank/Program、RPN/NRPN、opaque payload、Channel 初始化和 EOT 的既有顺序。填充不是重置/状态恢复点。对于本 Track 的有效 F0/F7 分包序列，也不能因为增加非 MIDI 通道的 Text Meta 而改变原分包消息、时刻或先后关系；这类路径须单独验证。

## 3. ADR-SMF-002：MTrk 不拆分，超限仅拒绝本次导出

**已接受。** 正式上限是 `4,294,967,295 bytes = 4 GiB − 1 byte`，并非十进制 4,000,000,000 字节。统计 MTrk length 字段描述的数据区：

- 包含全部 delta、显式 status、事件 payload、Track Name/Port/私有结构 Meta、GS/XG 与 CC91/93 初始化、占位、EOT；
- 不包含 `MTrk` 标识和 length 字段共 8 字节；
- 每条 Track 分别应用，整个 `.mid` 可以大于 4 GiB；不得把总文件位置误当作当前 Track 长度。

任何一个 Track 超限则整个本次导出事务失败，不自动将同一个 MTrk 分成多个 Track、不用多文件替代、不写超过 32-bit 的非标准长度字段。Conductor 一条、Pure MIDI 一 Track 一条、Logical 一 Unit 一条的结构继续成立，单文件 `ntrks <= 65535` 的独立上限也不改变。

理由：MTrk 在 Type 1 中是同步轨道，不是简单的顺序续块。自动拆分会触及跨 Track 同 tick 排序、Note 配对、元事件归属、名称/EOT、重新导入结构等独立兼容问题；本轮产品决定不引入这套语义。用户关于实际规模的估计是接受该产品限制的理由，不是“永远不可能触发”的技术保证，必须保留明确失败与测试。

Compiler 不计算或预测 MTrk 字节数，不把合法 canonical 标成失败；导出失败不能破坏已成功的编译修订、播放或音频渲染能力。既有消费者自己的时间/采样/资源限制仍适用，并非承诺任意 Int64 范围均可渲染。

## 4. 其他硬限制不放宽

| 类型 | 保留的约束与处理 |
| --- | --- |
| TPQN/TPQ | `1..32767`；现有 Project/编译验证与导出防御门保留，不改 SMPTE，不重采样 Tick |
| Tempo | `60,000,000m / BPM`，最终一次 AwayFromZero；结果必须在 `1..16777215`，不 clamp |
| Channel Event | 每种事件的正式 4-bit Channel、7-bit 数据、14-bit 组合值等原值域；不把 signed Pitch Bend 的 UI 值域与 wire 值域混淆 |
| 拍号、调号及其他已解释 Meta | 保持各类型的固定长度、枚举、数值/组合约束；例如现有拍号分子 1～99、分母集合 1/2/4/8/16/32/64 及 `4×TPQ % denominator == 0` 不变；不扩大为理论 wire 全范围 |
| 单条 Meta/SysEx payload | length VLQ 上限 `0x0FFFFFFF`；按该线格式字段覆盖字节计数，SysEx 终止字节若在 payload 中就计入；长度超限不能靠 Text Meta 或拆记录规避 |
| 单个文件 Track 数 | `1..65535`，包含 Conductor/空结构 Track；输出规划可确定时在创建 staging 文件前失败，不合并 Track；256 Unit 不是 Track 数上限 |
| 内部安全运算 | 负 Tick、乱序、非法 EOT、Int64/decimal 等正式运算无法表示时仍受控失败；不增加 `总 Tick <= VLQ 最大值` 或任意音符数量上限 |

源值域/结构错误保持原语义验证归属；只在文件编码层才能确定的约束由导出拒绝，不要求给整个 canonical 增加输出字节扫描。未知但结构合法的 opaque Meta/SysEx 不因无法解释厂商语义而拒绝。

空 Text Meta 可能被第三方列表显示，也无法单凭其字节判断是否来自 Midora。导出不回写原项目；若用户显式重新导入，按既有 opaque 规则保存合法输入，不擅自清理空文本。本轮不增加私有占位标记或无损字节 round-trip 保证。

## 5. 防止填充造成资源失控

有限内存不代表可以无限写盘：接近 `long.MaxValue` 的单间隔按公式需要 34,359,738,496 个占位，仅占位就需 240,518,169,472 字节（约 224 GiB），明显已不可能装进一个合法 MTrk。必须通过整数预算在开始该占位循环前失败，而不是写数百 GiB 后回填长度时才报错。

建议实现边界：

1. 低层严格 VLQ 写入器保留 `0..M` 验证，payload length 继续直接使用该严格门；只为 delta 新增填充入口，禁止“一律把所有 VLQ 都拆开”。
2. Track writer 维护独立的已编码数据字节数。每次先验证本条记录，再计算 `占位字节 + 剩余 delta 字节 + 记录字节`，与剩余预算比较。普通记录写入时至少保留最小 EOT 的 4 字节；只有确认事件枚举结束，才按真正最后事件到 EOT 的间隔计算准确尾段成本，此时不重复预留 EOT。仅知道 descriptor 的 EOT 不等于中间没有更多事件，不能提前把剩余时间全当作空白，否则会误报超限或双计占位。
3. 计算前检查差值、乘法、加法的可表示性，可用减法预算/受检算术或更宽中间数；不能让 `N×7`、累计文件位置或统计回绕。明显无法容纳时报告已知下界即可，不为获得“全轨精确最终大小”继续展开其余事件。
4. 正常事件枚举顺便计数，不额外全轨物化或全量预编码。只有确实插入占位才增加输出字节/I/O；原合法短间隔路径应保持原字节和流式性能，仅付出常数级边界检查。
5. 巨大但仍在 MTrk 上限内的填充也必须分块检查取消，进度包含实际编码工作，不用 UI 线程承担写入。合法输出规模仍可能需要显著磁盘时间，不能承诺“只占内存少就很快”。本轮不另设未经产品确认的占位个数或工程时长硬上限。
6. 即时和延迟分页写入都必须结构化保留编码错误；取消不算 Error。失败清理 staging，不覆盖已有目标，不发布其他同任务的部分文件；磁盘不足/权限/目标变化等继续报告真实 I/O 原因。

## 6. 用户可见结果与诊断

- 成功且有填充：一项导出级汇总 Info，包含插入数、涉及 MTrk 数和文件数；README 有独立兼容性摘要。没有填充不显示；关闭 README 仍可从导出结果查看汇总。
- 不为每个占位或间隔创建 Diagnostic。此 Info 不受 Warning-as-error 阻止，不挤占 README 前 1000 条正式编译 Warning/Info 的展示额度，也不改变 canonical 诊断、NoteOn/事件统计。
- 超限失败：明确显示具体上限及实际值/确定超限下界，尽可能附文件、Track 名称、Tick 与源位置；不要求用户理解内部 ID/类型名，也不把编码超限显示成笼统的“写 staging 目录失败”。
- 导出失败无成功 README，原项目保持可保存、可编辑和原有编译状态。

## 7. 定案时的源码事实与待实施改动（历史）

以下为源码核对，不代表本次已运行新测试：

- [StandardMidiFile](../src/midora-midi/Midora.Midi/StandardMidiFile.cs) 的 `WriteTrack` 仍对事件 delta 和 EOT delta 直接调用严格 `WriteVariableLength`，超 M 即抛异常；没有 spacer。该 VLQ 方法也服务 payload length，当前错误文字却统一写 delta，需要在实施时区分。
- `WriteType1` 已检查 TPQ、ntrks；MTrk 大小目前是在整个 Track 写完后与 `uint.MaxValue` 比较。本次要求把防护前移到每次事件/占位预算，避免无效写盘。
- [CanonicalMidiFileExporter](../src/midora-core/Midora.MidiExport/CanonicalMidiFileExporter.cs) 存在立即编码与延迟分页写入路径；[输出事务](../src/midora-core/Midora.MidiExport/MidiExportOutputTransaction.cs) 中延迟编码异常可能归入通用 staging 写入失败。应打通结构化编码错误和任务 UI，而非只修一个 catch。
- [Exporter 测试](../src/midora-core/Midora.MidiExport.Tests/CanonicalMidiFileExporterTests.cs) 的 `AcceptsMaximumSmfDeltaAndRejectsTheNextTickWithoutSpacerEvents` 与 [Task 测试](../src/midora-core/Midora.MidiExport.Tests/MidiExportTaskRunnerTests.cs) 的 `SmfDeltaOverflowFailsBeforePublishingAnyOutput` 仍锁定旧失败行为；必须在实际实施时替换为新规则，并另保留 payload/MTrk 等真正失败的原子性测试。本轮不修改这些测试，不把旧 Passed 当成新验证。

## 8. 定案时的实施顺序与验证门（已由实施记录承接）

不改变已批准顺序：**Logical 编译结果内存优化 → 极端 Tick 防护及本次 SMF 编码边界 → 新一轮功能需求**。见 [收尾与下一步台账](Midora-Pre-Expansion-Closeout-and-Next-Step-2026-09-09.md)。本文的定案不代表已经启动任一产品实现。

建议在第二项内先完成安全算术/Track 字节预算，再接入 delta 填充、结构化诊断、README 汇总，最后贯穿所有导出模式与分页/非分页验证。UI 网格端点保护与 SMF 编码边界是两条独立调用链，不用一个 catch 或统一 Tick 截断替代。

| 验证组 | 必须覆盖 |
| --- | --- |
| VLQ 与精确字节 | D=0、M−1、M、M+1、2M、2M+1；检查完整输出字节、精确占位数、所有实际 delta 合法 |
| 时间/EOT | 首个事件、事件间隔、尾部空白/EOT、空 Track、长 Gate 跨越多个 M、非零范围重基、同 tick 原事件顺序不变；不能把尚未枚举的后续事件区间误算成 EOT 空白 |
| 消费隔离 | 导出前后 canonical 事件/来源/顺序/fingerprint/诊断/统计不变；Full/Incremental 等价；不向音频投影传入占位 |
| Track 拓扑 | Conductor/Pure MIDI/Logical、同 Fixed/Auto Root 多 Track、Whole/Per Port/Per Logical Track；无按大小分裂/合并，名称/Port/各 EOT 保留 |
| 大小边界 | 当前 MTrk 数据区 B−1/B/B+1，B=`0xFFFFFFFF`；包含结构/初始化/占位/EOT，排除 8 字节头；多个合法 Track 的文件合计可超过 B |
| 其他硬限制 | TPQ=32767/32768、ntrks=65535/65536、Tempo 最终舍入边界、非法值/Meta 长度、单条 payload M/M+1；payload 不能误走填充分支 |
| SysEx/Meta | 来源空 Text 不丢失、未知合法 opaque 保留、F0/F7 记录顺序与时间；重新导入不复制、删除或识别错普通空文本 |
| 极端与安全 | `long.MaxValue` 间隔、累计运算边界；预算即时报错、没有巨量分配/写盘、取消延迟有界，不为算错误详情继续全轨扫描 |
| 错误事务 | 分页与非分页编码失败、填充中取消、自校验失败、磁盘写失败、多文件最后一项失败、已有目标；保留旧文件，无 partial 发布，错误类型准确 |
| 摘要 | 零/一个/大量占位、多文件、README 开/关、Warning-as-error、已有超过 1000 条编译诊断；只增加一份汇总而不扩大正式诊断集 |
| 性能 | 小型与大型 MIDI 的常规导出、冷/热分页、超长稀疏间隔：计时、峰内存、读取/写入字节、取消响应；禁止额外全轨数组/扫描造成倒退 |

接近 4 GiB/payload 上界的算术门优先用计数字节 sink、虚拟大长度及参数化小预算覆盖，避免为边界单测实际分配/写出数 GiB；这只能证明对应算术/失败路径，不能冒充真实大文件 I/O 测试。另用可负担的实际 SMF golden/重新解析验证完整线格式和事务；必要的大文件集成单独 opt-in，并沿用系统余量/进程树守护。

本轮只做文档一致性、链接和 diff 检查；不构建、不运行产品测试。后续实施必须补实际结果，不能用本节计划代替测试证据。

## 9. 定案时的文档验证记录（历史）

- 更新 14 份既有 Markdown，新增本文，共 15 份文档；未修改产品源码、测试、构建配置、版本、schema、发布产物或用户草稿。
- `git diff --check` 通过；新增/修改文字的 18 个本地文件链接全部可解析，0 个缺失目标；INV-001～118 编号无重复，INV-118 位于原不变量表内，新增 SRS 小节无重复。
- 对上表六个 delta 边界及 `long.MaxValue` 共七组输入做独立整数算术核对，满足 `N×M + remainder = D` 且 remainder 在合法 VLQ 范围内；确认约 224 GiB 的极端填充成本。此项仅为文档公式检查，不是产品测试或性能实测。
- 没有构建、运行产品测试、提交、推送或本地发布；新功能状态仍为待实施。
