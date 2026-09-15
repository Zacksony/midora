# MIDI 导出编码边界实施与验证

## 需求追踪

依据：SRS §14.12.2/8/9、§14.15.7、§14.19.8、INV-118，以及 ADR-SMF-001/002。本文记录实现与证据，不修改已批准的需求。

- 输入：冻结的 Canonical Compiled Result、导出范围/模式与最终输出路径。
- 输出：保持原事件 tick、顺序、payload 和各 Track EOT 的 SMF Type 1；超长 delta 用空 Text Meta 分段。成功后只产生一个导出级填充汇总，与编译诊断及其 README 行数上限独立。
- 边界：delta VLQ 最大 `0x0FFFFFFF`；每个 MTrk **数据区**最大 `0xFFFFFFFF` 字节，不拆分。TPQN、MTrk 数、Tempo、事件值及 payload 长度原有硬限制不放宽。
- 失败：每条记录（含所需填充）写入前预算，普通记录还保留最少四字节 EOT；实际尾部 delta 仅在枚举结束后计算。编码失败与 I/O 失败区分，取消不报编码错误；沿用 staging、自验证和原子发布/回滚。
- 归属：填充和汇总只属于本次导出；不进入 Project、canonical、编译统计/诊断、音频、缓存身份或持久化格式。
- 非目标：Running Status 输出、MTrk 拆分、音频调整、软件/Project 版本升级、本地发布。

## 实施设计

1. 低层 SMF writer 统一维护每 Track 字节预算，安全计算填充数，以有界块写入并支持取消及节流进度。payload 长度仍走严格 VLQ，不复用 delta 的补点规则。
2. resident 与 paged canonical 均流式编码；正式导出不通过整文件 MemoryStream，避免少量事件加巨大间隔造成巨型内存分配。
3. 保留编码错误的类型、限制、已证明字节下界、Track/tick 上下文，经事务传至导出任务。已写 staging 不得泄漏为正式产物。
4. 编码成功的每文件统计用于聚合；README 在 MIDI 编码完成后写入，关闭 README 时结果窗口仍报告汇总。进度基于实际已编码事件/字节与填充工作，不额外全量扫描 canonical。

## 验证计划

- 普通文件 golden bytes、六个 delta 临界值、前导/中段/EOT、同 tick、长 Gate、非零导出起点、opaque F0/F7 与原有空 Text 的 round-trip。
- MTrk B−1/B/B+1（计数流和预算函数，不分配 4 GiB）、多个合法 MTrk 总文件超过 4 GiB、极端 int64 Tick 提前失败。
- payload/TPQN/Tempo/轨数边界、流式异常归属、取消、I/O 注入、末文件失败和既有目标不变；所有导出模式与 README 开关。
- 普通数据时间/内存测量、填充块上限与取消响应；相关 MIDI、导出及桌面测试。

## 实施状态

产品实施与自动验证完成，待用户人工验收。本轮没有修改 SRS/Compiler/音频/版本/持久化格式；未提交、推送或本地发布，未使用 computer-use。既有未跟踪用户草稿未读、未改。

## 代码落点与边界

- [SMF writer](../src/midora-midi/Midora.Midi/StandardMidiFile.cs)、[编码预算](../src/midora-midi/Midora.Midi/StandardMidiFileEncoding.cs)：`N=(D-1)/M` 的整数填充，所有增量记录写入前预算，EOT 最小预留与实际尾段区分。普通 Channel 路径不计算填充数量，一次写入 delta/status/data；原短 delta 字节不变。
- 占位只使用一个惰性初始化的 **57,344 byte（56 KiB）** 静态块，每块最多 8,192 个 `FF FF FF 7F FF 01 00`；不会分配 N 个事件。payload 在复制前检查 VLQ 长度，实际写入分为最多 64 KiB 一块。取消在事件/块边界检查，不能中断已经进入操作系统的一次阻塞 Write。
- [CanonicalMidiFileExporter](../src/midora-core/Midora.MidiExport/CanonicalMidiFileExporter.cs)：删除 resident 整文件物化分支，复用分页路径的有序流；保留既有 canonical 一致性/Bank 顺序验证，不增加 Compiler 扫描或新诊断。最终 MTrk 数在 staging 创建前校验。
- [编码结果](../src/midora-core/Midora.MidiExport/Contracts.cs)、[错误与汇总](../src/midora-core/Midora.MidiExport/MidiExportEncodingBoundary.cs)：准备成功不等于文件编码已完成；最终以输出事务为准。Track 名称/索引、相对事件/EOT tick、源 Track 与绝对 tick、实际值或大小下界可随边界错误传递，不要求用户读内部 ID。
- [输出事务](../src/midora-core/Midora.MidiExport/MidiExportOutputTransaction.cs)：普通编码、延迟分页、自验证与真实 I/O 错误明确区分；对意外的延迟源异常也清理 staging。仍使用原有原子发布/回滚。不写整 Track/整文件内存缓冲；显式 `FileBytes` / artifact `Content` 仅保留为小型消费者/测试便利 API，正式任务不调用。
- [Artifact builder](../src/midora-core/Midora.MidiExport/MidiExportArtifactBuilder.cs)、[README](../src/midora-core/Midora.MidiExport/MidiExportReadmeBuilder.cs)、[Task runner](../src/midora-core/Midora.MidiExport/MidiExportTaskRunner.cs)：先编码 MIDI，后写 README。每文件统计仅在完整成功写入后替换，不因重复编码重复累计；失败任务不返回成功填充摘要。README 的独立 Export Info 不占前 1000 条编译诊断额度。
- [桌面 Session](../src/midora-desktop/Midora.Desktop/DesktopSessionController.cs)、[主窗口](../src/midora-desktop/Midora.Desktop/MainWindow.xaml.cs)：后台编码/写盘，保留会话编辑锁；现有任务窗口显示文件、Track、已编码原事件数、数据字节与当前间隔填充数。未预扫全文件获取伪百分比，阶段没有准确总量时保持不确定进度。进度节流并在 UI 合并，完成结果含独立 Info（无需 README）。

## 实际验证

环境：Windows x64、Release、.NET SDK 10.0.400 / runtime 10.0.11；对照基线 `e0b3299`。测试输出在被忽略的 `.tmp/smf-boundaries/tests/`。

| 测试组 | 通过 | 失败 | 主要证据 |
| --- | ---: | ---: | --- |
| Midora.Midi.Tests | 54 | 0 | golden、六个 delta 边界、前/中/尾段、精确 EOT、长 Gate、显式 status、payload 独立门、B−1/B/B+1、快路径 EOT 预留、两个大 MTrk 总文件 >4 GiB、块取消 |
| Midora.MidiExport.Tests | 84 | 0 | 全部模式、非零范围、Fixed/Auto Shared Root、resident/paged 字节等价、原 opaque/F0/F7、单/多文件汇总、README 开关/1000 条限制/Warning-as-Error、编码/自验证/I/O/意外源失败与取消回滚 |
| Midora.Compiler.Tests（全套） | 568 | 0 | 既有编译正确性、确定性及 Full/Incremental 回归；Compiler 源码零变更 |
| Midora.Desktop.Tests（全套） | 434 | 0 | 既有桌面服务/导出工作流及 UI 自动测试回归 |
| Application 的 MidiProjectImport 两组测试 | 20 | 0 | 内存/流式重新导入保留原空 Text 和填充 Text，NoteOn/Off、Gate 和 NoteOff velocity 不变 |
| **合计** | **1160** | **0** | 未把基线/中间重复运行加进合计 |

最终桌面 Release 构建成功，0 个警告、0 个错误；`git diff --check` 通过，实施/ADR/台账内本地文件链接核对无缺失。

基线 MIDI 29/29、MIDI Export 62/62。新规则替换两层及任务层旧“超长 delta 直接拒绝”的断言；另增加真正的超限失败覆盖，而不是删除失败门。

关键证据：

1. 4 GiB 验证使用真正 writer + 不留存内容的 seekable 计数流，覆盖 `4,294,967,294 / 4,294,967,295 / 4,294,967,296` 字节，核对实际写入数量和 header 回填；两个合法 MTrk 的总文件为 `14 + 2×(8+B)` 字节仍可编码。另覆盖普通 Channel 快路径最后四字节的 EOT 预留。
2. `long.MaxValue` 空 Track 只写 22 字节 header 后即失败：34,359,738,496 个占位的预算超过上限，没有生成/写入海量填充；测试该失败的线程分配低于 128 KiB。
3. 填充中的真实 staging 取消在第一块后停止，实际只写约 56 KiB，目录清理且不发布产物；多文件末项失败/取消时三个既有目标逐字节不变。
4. 冷/热（同 frozen source 再次读取）resident/paged 编码字节相同；重复写同一结果不累计重复摘要。已测试每个 Pure Track 独立 EOT、结构 Track、共享 Root 同 key 重叠与 F0/F7 分包。
5. 汇总不修改 canonical fingerprint、事件数量、NoteOn 数量或编译诊断；README 输出超过 1000 条诊断的场景仍保留完整填充摘要。导入器不把空 Text 私自识别/删除为 Midora 占位。

## 时间/内存微基准

同一临时探针按需枚举 **1,000,000 个 NoteOn/NoteOff 对（2,000,000 条事件）**，输出 8,000,026 bytes；每种模式预热 3 次后取 5 次中位数。文件使用 256 KiB FileStream 缓冲和普通 Flush，**不是持久介质强制 flush 测速**。基线 DLL 在本轮修改前保存；探针不进入正式代码或 Git。

| writer | 计数流 | 本地文件 | 常态线程分配（不含已建输入） |
| --- | ---: | ---: | --- |
| 基线 | 55.65 ms | 81.74 ms | 72 B / 262,240 B |
| 初版统一预算（已淘汰） | 116.67 ms | 138.70 ms | 96 B / 262,264 B |
| 最终快路径 | 60.63 ms | 84.99 ms | 96 B / 262,264 B |

最终本机计数流约 +9%、文件约 +4%（分别约 +5/+3 ms 每百万音符），不是“完全零代价”；初版明显退化已针对热路径修正。此微基准只测 SMF writer，不测编译、完整导出自验证或大型工程端到端耗时，不能外推为完整导出也固定增幅 4%。真实设备和缓存状态会影响结果，文件基线单次有约 149 ms 波动。新增填充路径为固定 56 KiB 块；普通路径不初始化该块，未观察到随事件数增长的 writer 托管分配。

## 剩余边界与人工验收

- 没有实际写 4 GiB 文件，也没有读/分配 256 MiB payload 做极值 I/O 测试；其门使用计数流/长度验证。小文件真实写盘、严格重读、取消和原子事务已测。真实磁盘容量耗尽、驱动故障或第三方播放器对超大文件的兼容性不由这些测试证明。
- 合法的极长空白仍可能生成接近 4 GiB 的 Track；这是标准编码成本，不额外添加未经批准的 Tick/占位数限制。进度可见且可取消，但一次底层同步磁盘 Write 的延迟无法由应用硬保证。
- 本轮未做真实声卡/完整超大型工程播放测试；音频路径未变，不对它的时序或性能作新增承诺。SRS、软件版本和 Project 格式均不变。

建议人工验收（不要求真的生成巨型文件）：

1. 普通项目导出 MIDI/README，核对原音乐、导出范围、文件名正常；无超长间隔时没有新增填充 Info。
2. 将一个可编译项目的导出结束 Tick 设为 `268435456`（或更长但仅数倍 M），检查导出成功、结果窗口出现填充汇总；打开 README 核对同一汇总。关闭 README 再导出，结果仍有 Info。
3. 导入上一步的 MIDI，确认音符/时间位置不变；Imported Meta 可能看见新增空 Text，属于已批准行为。
4. 导出耗时较长的正常大型项目时，检查 UI 任务仍响应取消，进度文本持续更新；取消不覆盖既有文件。**不要为验收刻意填满磁盘。**

完成上述验收后，再由用户决定提交/推送或后续需求；本轮未自动执行这些操作。

## 后续验收修复（2026-09-11）

`Golden Hour by MBMS - Final.mid` 暴露了既有有界 SMF 排序器遗漏可选来源 ID 的落盘解码问题，报 `Stable IDs must be positive`。该错误读回语句早于本轮编码边界变更；前述测试未覆盖“Root 生成事件 + 超过正式落盘阈值”的交集。已补充真实样本和强制落盘回归并修复，详见 [独立修复记录](Midora-SMF-Spill-Source-Identity-Repair-2026-09-11.md)。本节不覆盖或改写前述当时测试记录，编码边界规则保持不变。
