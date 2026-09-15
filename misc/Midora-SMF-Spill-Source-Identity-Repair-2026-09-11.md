# 大型 Pure MIDI 导出：排序落盘来源身份修复

## 需求追踪

- 输入：用户样本 `Golden Hour by MBMS - Final.mid`，正常导入、专用 MIDI Export 编译及 canonical SMF 投影。
- 现象：导出阶段报 `Stable IDs must be positive`；已用原样本及自动测试复现，堆栈定位到有界 SMF 排序器读回临时记录。
- 规范：SRS §14.1、§14.12、§14.18～19、§23.7/9/12/19，INV-003/009/040/045/053/066/118。只消费 canonical，保留事件/顺序/来源，不放宽 Project 正稳定 ID 规则。
- 输出：有效大轨道导出应与等价不落盘路径一致；保留系统生成事件的“无直接源对象”状态，不伪造 ID，也不跳过清理或状态事件。
- 边界与失败：内存/磁盘排序一致，多 run/多轮归并，非法必需 ID 与损坏数据仍失败；取消/异常不发布部分 MIDI，沿用有界缓冲和原子事务。
- 归属与非目标：修复可重建运行时排序表示；不更改 SRS、Project/持久化格式、音频语义、超长 delta 或 MTrk 字节规则。不提交、推送或发布，不使用 computer-use。用户原 MIDI 只读。

## 结论与根因

这是 Midora 的运行时序列化错误，不是样本含有非法 Project ID，也不是 MIDI 文件中的编号需要用户修改。

1. canonical 的 Root 初始化、默认值、硬边界清理等系统生成事件有正式输出 Track 归属，但不一定对应某个用户 Direct Note/Event。因此 `CanonicalSmfTrackChannelEvent.SourceObjectId` 可以为 `default`，对应临时记录中的 `0`；必填的 `ExportTrackId` 仍必须为正。
2. `BoundedCanonicalSmfEventSorter` 每 run 最多容纳 131,072 条记录。小输入直接内存排序，不重建 ID；大输入写入固定 40 字节记录后再读回/归并。
3. 写入路径正确保存了可选来源的 `0`，但读回路径错误地把它交给只接受正数的 `MidoraId.FromSequence`，于是触发截图异常。
4. 同样的缺失来源在 render / opaque 排序器中已有正确的可选 ID 解码，遗漏集中在 SMF channel-event 排序器。本次不改变其他排序器。

Git 证据：错误读回语句来自 `e4bb784`（2026-08-20，`perf(midi): scale import playback and timeline rendering`）；上一轮编码边界实施前的 `HEAD` 中，该语句及 paged export 调用路径均已存在。上一轮并未修改这个 Compiler reader。这里是源码/历史追溯结论，不声称已运行所有历史发布二进制。

此前 300,000 条 SMF 排序测试全部使用正的直接来源 ID；上一轮 Shared Root 的 resident/paged 导出等价用例只有少量事件，未到落盘阈值。因此它们没有覆盖“系统生成事件 + 实际落盘”的交集。

## 修复范围

- [BoundedCanonicalSmfEventSorter.cs](../src/midora-core/Midora.Compiler/BoundedCanonicalSmfEventSorter.cs)：仅对可选 `SourceObjectId` 解码 `0 → default`；正 ID 原样还原，负数仍走原有严格工厂并失败。`ExportTrackId` 解码规则不变。
- [Compiler Contracts.cs](../src/midora-core/Midora.Compiler/Contracts.cs)：补充上述既有身份语义的注释，不改变公开字段、二进制记录布局或 API。
- [排序回归](../src/midora-core/Midora.Compiler.Tests/BoundedCanonicalSmfEventSorterTests.cs)：同一混合输入分别走全内存、磁盘单轮归并、磁盘多轮归并，逐记录比较 ID、消息、tick、role、顺序；另验证缺失必填 Track ID 仍被拒绝。
- [实际阈值导出回归](../src/midora-core/Midora.MidiExport.Tests/MidiExportPagedSourceIdentityTests.cs)：70,000 Note / 140,000 endpoints 的真实 Content Pack，Fixed/Auto Shared Root × 起点 0/16。每例整曲、逐 Port 各写两次，验证逐字节确定性、精确 NoteOn/NoteOff tick/key/velocity、Track 名称/顺序/EOT、Root 初始化/清理、兄弟 Track 的 CC11 状态恢复及 Marker 保留。

没有放宽 Project ID 校验、伪造来源 ID、删除系统事件、跳过归并、增大缓存或改用全量内存排序。无 SRS、Project Format、版本、音频语义变更；保留上一轮超长 delta 填充与 MTrk 编码上限的实现。

## 先失败、后通过的证据

在本次产品修复前执行新增测试：

- 相同 67 条混合记录：内存路径通过，磁盘单轮/多轮均以截图同类异常失败。
- 四组 70,000 Note 正式编译/导出用例全部失败，完整堆栈均经过 `BoundedCanonicalSmfEventSorter.ReadRecord → PureMidiPagedCanonicalSource.QueryTrackChannelEventPages → MIDI writer`。
- 原 `Golden Hour` 样本同样在该 reader 失败，事务未发布输出目录或部分 MIDI。

只应用来源解码修复后，上述磁盘路径和四组大轨道端到端测试全部通过。

## 真实样本验证

样本仅以只读方式访问：`D:\MIDI\Black MIDIs\MBMS\Golden Hour by MBMS - Final.mid`。

| 项目 | 实测 |
| --- | ---: |
| 原文件字节数 | 27,515,740 |
| 导入扫描事件 | 6,871,676 |
| 导入配对音符 | 2,495,982 |
| 导入 Direct 非音符事件 | 1,873,384 |
| Pure Tracks / Roots | 39 / 16 |
| Content Pack pages / bytes | 624 / 81,144,325 |
| canonical Note On | 2,495,982 |
| canonical 范围 | `[0, 635863)` |

正式 `MidiProjectImportService → MidiExportCompilationCoordinator → MidiExportTaskRunner`，采用 Compact，README 开启。修复后整曲导出两次、逐 Port 导出两次，**4/4 成功**；样本本次仅使用一个 Port。

- 每份 `.mid` 都经过 `ValidateType1` 严格重读与流式事件扫描。
- 每份均包含 2,495,982 个非零力度 Note On，与 canonical 精确相等。
- 四份文件都是 27,517,613 bytes，SHA-256 都为 `3f930095b6d409cd47fa97aa8ad4a6f9fa6b4be8a294ceac344df74176c89030`。此处比较重复导出，不要求与原 MIDI 字节相同。
- 无超长 delta 填充；因此这个故障与实际执行填充分支无关。
- 导入约 8.22 s，首次专用编译约 5.48 s；四次完整导出任务分别约 9.15 / 8.58 / 8.70 / 11.21 s（含写盘/自验证/README/发布，不含随后探针额外严格重读）。这是功能验证计时，不是隔离的性能基准；末次与编译器回归有并行运行。
- 探针进程峰值 Working Set 737,976,320 bytes，约 704 MiB。设有 8.5 GB private bytes 终止保护，未触发。没有运行 UI 或音频，不把该数字当作整个 WPF 产品内存上限。

临时探针、日志、TRX 和输出仅位于忽略的 `.tmp/smf-export-id-investigation/`，不进入发布或 Git。原文件未改写。

## 回归与交付边界

Windows x64、Release、.NET SDK 10.0.400。测试证据目录：`.tmp/smf-export-id-investigation/tests/`。

| 测试 | 通过 | 失败 |
| --- | ---: | ---: |
| Midora.Compiler.Tests 全套 | 572 | 0 |
| Midora.MidiExport.Tests 全套 | 88 | 0 |
| Midora.Midi.Tests 全套 | 54 | 0 |
| Application 的 MidiProjectImport 两组 | 20 | 0 |
| Midora.Desktop.Tests 全套 | 434 | 0 |
| **合计（不重复计数定向运行）** | **1,168** | **0** |

桌面 Release 构建成功，0 警告、0 错误；`git diff --check` 通过。本轮新增 8 个回归实例，先失败运行不计入最终通过数；定向测试与全套中的重复运行不重复累加。

本次保留既有有界缓存、run fan-in、取消和失败原子性；新增处理只是固定大小记录解码的一次零值判断，不引入按工程规模增加的新常驻结构。没有宣称已经测试所有 MIDI 或第三方播放器听感，也没有运行历史发布版本做性能对照。

建议人工验收只需重新导入该样本并执行整曲 MIDI 导出，确认原报错消失；按需重新导入导出文件核对 Track/音符。当前不自动提交、推送或发布本地。
