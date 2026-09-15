# LC-T3：紧凑 Logical canonical 正式实施

日期：2026-09-10。状态：**正式实现已接入，2,388 项回归及 54 个受守护对照进程通过；用户已确认验收通过。**

## 需求追踪

- 输入仍是完整 Project、冻结 CompilationRequest 与 source revision；输出仍是完整 canonical 事件、来源、总序、Range/FIFO/Reset、分配、诊断与 fingerprint。
- 依据 SRS §12.4、§12.10～12、§12.17、§12.21～25、INV-009/010/015/020/025/066，以及[LC-T3 专项方案](Midora-Logical-Canonical-Time-Optimization-Analysis-and-Plan-2026-09-10.md) §6.3/§7。T1/T2 达标后原先暂缓；用户在完成隔离实验并获知收益和反例后，明确要求正式实施。
- 不改变公共 CanonicalMidiEvent/SourceReference 契约、音乐语义、Project Format、版本、SRS、音频 DSP、编辑或 UI。不扩大下一步的极端 Tick/SMF 任务范围。
- 取消、预算、I/O、页损坏及 source revision 失败均不得发布部分新结果；合法旧结果与 reader 继续有效。数据仅写 ProgramRoot `.tmp/CompilerRuns` owned directory，不进入项目。
- 实施轮次未提交、推送或本地发布；验收通过后按用户明确请求提交、推送，仍不本地发布。全程不使用 computer-use，不读取或修改用户草稿。

## ADR-LCT-003：热记录与完整来源分离

1. Raw → canonical 排序 → 范围扫描 → 最终页使用 96-byte `CompactCanonicalEvent`；公共事件仍为 232 bytes，SourceReference 仍为 160 bytes。在有界 consumer cursor 中无损恢复公共值，不让消费者另建来源解释。
2. Event Tick 与 Source Tick 独立保存；逐事件 LogicalNoteId、SourceEventId 放入热记录，其余完整 Source 字段保存在独立 value-page sidecar，包括不参与旧比较器排序的 UsageId 和 Source.ExportTrackId。
3. 来源发现字典最多保留 4096 项；达到上限仍完整追加冷来源，只降低去重率。正式版将其工作预约定为 3 MiB，覆盖 4049→8419 容量扩容时新旧 entries/buckets 共存，而不是原型仅按有效元素预约 2 MiB。来源页服从既有 resident/spill/metadata 预算，不引用可变 Raw pool。
4. 每个来源 reader 使用独立的最多四页随机读缓存，目录/reader/数组头预留 1 KiB，各解码页另按完整页记录大小预约。先预约再分配；所有实际 spill 读取继续验证原 stored SHA、描述符与精确解码长度。读取失败不能发布缓存命中，游标之间不共享可变解码缓存。reader 必须直接保活拥有清理 finalizer 的来源 table，而不只是保活其底层 value store；否则 table 可能在 reader 存活时过早终结。
5. 比较器先比较无来源的热前缀；只有平局才按完整旧比较器比较恢复后的值。来源页索引、插入顺序、压缩率和物理页位置绝不是音乐排序或 fingerprint 的一部分。
6. Range 的活动 FIFO 保存 32-byte 紧凑来源；起点恢复、冷启动不补 NoteOn、真实 Direct NoteOff、硬结束和同 Tick winner 不变。范围边界借用 sealed parent 来源表并追加局部来源，最终 fold 重映射到独立最终表，不让短范围结果保活整曲来源。
7. 小结果仍按原 4096-event 阈值交付公共内联数组；大结果为独立不可变页，清理编译缓存不能释放仍被有效结果/读者持有的页。取消和异常释放本次工作 owner；不得向正常产品流程加入强制 GC。
8. 保持 resident/working/metadata **128/128/64 MiB** 与 spill **16 GiB**。它们不构成整个进程上限：源模型、实例/分配、活动状态、旧结果及 CLR/WPF 仍须独立衡量。

## 验证门

- 逐字段往返（含高熵来源超过 intern 上限）、同 Tick 全部 tie-breaker、Source.Tick 不等于 Event.Tick、父子表生命周期。
- 取消、预算拒绝、重复 Dispose、页损坏/截断、多个并存 reader、旧结果清缓存后继续消费；失败后预算必须可重新预约。
- 完整 Compiler、Playback、MIDI Export、AudioRender、Application、Desktop 自动回归；不是声卡或真实 WPF 人工验收的替代。
- 正式候选对已冻结 T1/T2 基线，串行运行完整 oracle 和重点时间/内存复测；受 8 GiB Job private-commit 硬限与 2 GiB 系统余量保护。
- 记录首次 Full、Incremental、fresh Full、消费者准备与实际写出；不以事件数或 record 尺寸取代完整等价证据，也不隐去小场景/进程峰值反例。

## 实验基线及已知限制

正式实施前的隔离实验：64 个成功进程对照，Compiler/Playback/MIDI Export/AudioRender 共 808 项通过；完整记录位于本机 ignored `.tmp/lct3-20260910/REPORT.md`。以 `69940d6`（T1/T2）为基线，三组大场景首次 Full 中位数改善约 17～22%，Incremental 改善约 26～34%。小项目没有稳定收益。

这不是普遍降低进程峰值的承诺：Shared100k 单次 Full 隔离进程的 Private 峰中位数由 324.8 增到 390.8 MiB；高展开完整 oracle 进程也存在峰值上升。记录变小、可达结果和累计分配减少，不等于 GC/LOH 高水位或整个 WPF 内存必然下降；未用 heap dump 精确归因，不把推断写成事实。正式实现不得靠强制 GC 隐藏这些差异。

## 正式自动回归

本轮以正式源码构建，不运行 `dist` 发布。六套 Release build 均为 0 warning、0 error；测试结果如下（不重复计入单独跑的 12 项定向测试）：

| 测试程序集 | 通过 | 失败 / 跳过 |
|---|---:|---:|
| Compiler | 560 | 0 / 0 |
| Playback | 155 | 0 / 0 |
| MIDI Export | 62 | 0 / 0 |
| AudioRender | 37 | 0 / 0 |
| Application | 1140 | 0 / 0 |
| Desktop | 434 | 0 / 0 |
| 合计 | 2388 | 0 / 0 |

新增紧凑存储定向测试覆盖：23,001 个高熵完整来源往返、超过 4096 项去重上限后的保存、正反比较器逐字段 tie、4 个并行 reader、取消隔离、mutable/disposed parent 拒绝、构造与父 reader 部分失败后的预算归还、损坏页驱逐后重读仍拒绝、失败读不缓存、reader 独立存活时 owner 不被提前终结。既有全套测试继续覆盖 Full/Incremental、旧结果存活、范围状态、Loop/Pre-Roll、映射、诊断、分页损坏与消费者等价。

证据位于本机 ignored `.tmp/lct3-formal/tests/` 和 `build-logs/`。这些测试没有启动真实声卡或应用窗口，不能表述为实际听感/交互已验收。

## 正式性能复测

基线为冻结的 `69940d6509be08e3087a0587a424affa8ef70556`（T1/T2），不是最初数组版，也不是上一轮原型。候选为本轮正式主工作树。Windows x64、i7-10750H（6C/12T）、32 GB、SDK 10.0.400、运行时 10.0.11；串行独立进程，A/B 顺序交错，不与构建/全回归并跑。不清 OS 文件缓存；首次 Full 包含 JIT，不能叫物理磁盘冷启动。没有固定 CPU 频率或消除全部系统背景负载，以下三次中位数与范围不是 p95/p99 或置信区间。

### 主要编译时间

每组 3 对运行，单位秒。Incremental 为一次真实内容编辑；fresh Full 为同进程内新 compiler 对编辑后同输入重新编译。完整序列 oracle 另行计时，不混入编译耗时。

| 场景 / 完整事件数 | Full：基线→正式 | 耗时减少 | Incremental：基线→正式 | 耗时减少 | fresh Full：基线→正式 |
|---|---:|---:|---:|---:|---:|
| expansion32 / 525,612 | 3.997→3.440 | 13.9% | 2.046→1.170 | 42.8% | 1.974→1.142 |
| expansion128 / 2,102,316 | 9.639→7.545 | 21.7% | 7.915→5.784 | 26.9% | 8.054→5.550 |
| shared100k / 1,200,013 | 6.376→5.298 | 16.9% | 4.639→3.161 | 31.9% | 4.314→2.957 |

Full 的 min～max：expansion32 基线 3.829～4.334 / 正式 2.641～3.502；expansion128 9.449～9.852 / 7.462～7.668；shared100k 6.272～6.423 / 5.287～5.803。Incremental 分别为 1.771～2.119 / 1.051～1.188、7.593～8.163 / 5.681～5.790、4.438～4.849 / 3.129～3.167。正式结果保持实验的主要收益，但不能把两轮绝对秒数直接解释为原型→正式的增减。

单对扩展检查：5,000 个独立 Segment（1,335,010 events）Full 7.645→6.865 s、Incremental 4.172→3.003 s；shared10k（120,013 events）Full 1.804→1.611 s。它们覆盖来源高基数及较小规模，不作为稳定百分比承诺。

### 明确记录时间反例

- 63 events 的 tiny：首次 Full 515.9→518.8 ms，基本持平，没有稳定收益。
- complex64（5,766 events，单对）：Incremental 190.6→212.1 ms、fresh Full 118.7→135.5 ms。
- mixed32（9,529 events，单对）：Incremental 150.7→199.2 ms；Full 756.4→769.4 ms。
- invalid 失败夹具（单对）：Full 368.0→387.5 ms，累计分配完全一致；不能把进程/JIT 波动归因成新的成功输出存储开销。
- Full 后首次 512-tick 查询还包含查询 API / oracle 首次初始化。三次中位数：expansion32 **19.46→28.79 ms**，expansion128 **12.63→15.10 ms**，shared100k 11.33→11.53 ms。尾部查询分别为 0.762→1.210、0.521→0.744、0.713→0.695 ms。来源间接还原不是零成本，不能说全部读取都更快；这些数字也不是实际 UI 帧耗时。

### 内存：有界工作集、结果驻留与进程峰分别计量

排序/页读写等被预算明确计量的工作集峰值：三组大场景由 **60.3 MiB** 降至 **27.9～28.8 MiB**。来源去重与 reader 的正式预约比原型更保守，没有提高整个预算。首次 Full 的 spill peak：expansion32 9.3→0 MiB、expansion128 62.8→49.4 MiB、shared100k 25.2→15.7 MiB。

| 场景 | 清缓存后单份结果 retained 估算，MiB | 完整 oracle + 三结果并存进程 Private peak 中位数，MiB |
|---|---:|---:|
| expansion32 | 107.0→48.2 | 1051.8→591.7 |
| expansion128 | 85.6→86.0 | 1069.6→940.3 |
| shared100k | 137.8→119.8 | 1490.1→1025.4 |
| segmented5000（单对） | 56.7→57.3 | **1222.6→1419.4** |

retained 是显式 owner 遍历估算，不是全 GC heap；满预算下可驻留更多有效记录，不保证驻留字节减少。进程峰包含 oracle 临时分配、多份合法结果和 GC 高水位；segmented5000 的峰仍增加约 197 MiB（16.1%）。其与前一实验的方向一致，因此不能仅用记录缩小来宣称“所有项目更省内存”。

独立 `cold` 模式仅编译一次、不做全流 oracle，也不持有三个结果。每组 3 对，Private peak 单位 MiB：

| 场景 | 峰中位数：基线→正式 | 基线 min～max | 正式 min～max | Full 中位数：基线→正式 |
|---|---:|---:|---:|---:|
| expansion128 | 328.3→284.4 | 287.8～375.9 | 251.6～286.4 | 9.759→7.607 s |
| shared100k | **324.5→391.0** | 323.0～410.4 | 373.2～391.1 | 6.444→5.460 s |

Shared 峰上升约 **66.5 MiB / 20.5%**，同时结果 retained 从 137.8 降到 119.8 MiB、累计编译分配约 1.19→1.11 GB。已确认“进程峰可能升”，但没有 heap/GC 时间线证据精确分摊它；合理推断包括分配寿命、GC/LOH 碎片及回收触发时机，不能称为已证实的具体根因。关闭后被跟踪的 owner 弱引用均为 0；这不是全进程所有静态缓存均为零或不存在任何泄漏的证明。

### 消费者与 MIDI 导出

固定 1,049,260 canonical events / 524,288 NoteOn，均 3 对进程。端到端值先对每次运行相加，再取中位数，**不是相加各阶段中位数**：

| 测量项 | 基线→正式 |
|---|---:|
| compile + cold metadata + first render-plan adapter | 6.970→6.143 s（减少 11.9%） |
| cold metadata 单项 | 1.594→1.566 s |
| adapter 首次 / 重复 | 54.77→52.78 ms / 0.670→0.677 ms |
| compile + MIDI prepare + first write | 6.575→5.637 s（减少 14.3%） |
| MIDI prepare 单项 | 493.18→477.20 ms |
| MIDI first write / repeat write | 782.21→717.32 ms / 699.92→650.40 ms |
| consumer 进程 Private peak | 312.8→271.2 MiB |
| export 进程 Private peak | 282.4→263.1 MiB |

consumer 就绪时间范围：基线 6.891～7.009 s、正式 6.030～6.237 s；MIDI 全阶段范围：基线 6.572～6.601 s、正式 5.461～6.413 s。写出单项正式有 998.7 ms 的一次高值，不能声称每次 I/O 都更快。主要时间收益仍在编译，没有修改音频播放调度或 DSP。

12 份实际 SMF 输出（两个版本 × 三次进程 × 每进程首次及重复写出），均为 **4,197,281 bytes**，SHA-256 完全相同：`0561babf9ec520808f8147df558877597e4c9911708e383b2b872db87acd3de9`。完整 mixed 消费者 oracle 另覆盖 Logical/Pure MIDI、opaque、Channel Mode 与范围输出；大消费者探针的 shape/count 本身不充当完整语义证明。

### 证据闭环与判定

- 54 个进程全部成功、无排除运行；30 个 run 模式进程执行完整 oracle/窗口/旧结果检查，12 个 cold 进程隔离单次峰，另 6 个 consumer + 6 个 export。完整编译序列/Source、诊断、分配、范围、fingerprint 与冻结基线一致；cold 额外核对计数和 fingerprint，不用它代替全字段检查。
- 54 个运行的最大采样 Private 为 **1,566,502,912 bytes（约 1.46 GiB）**；没有触及 8 GiB Job 上限或 2 GiB 系统余量守护。六套回归中的最高峰在 Application 套件，约 2.34 GiB，亦未触发守护。
- 正式编译器 MVID：`dba7b119-1d07-4e84-a221-1bfdfbb2224d`；SHA-256：`8b85d332fc79556a1d44b6448be616d5edcd9ff8d60ef361f0158ff22cc8659b`。Compiler/Desktop 回归输出和三种正式探针中的编译器 DLL 均核验为该同一版本，避免“测的是实验 DLL，交付是另一份代码”。冻结基线 DLL SHA-256：`0a4eb686b78ed4e717e3deca6cce3e752f5abbc13906d6fc203442e30d70c1c8`。
- 原始 JSONL、TRX、guard、汇总和严格完整性核查保存于本机 ignored `.tmp/lct3-formal/`：`runs/`、`tests/`、`summary.json`、`verified-evidence.json`。`Run-Regressions.ps1`、`Run-Benchmarks.ps1`、`Verify-Evidence.ps1`、`Summarize.ps1` 保留本轮重现步骤；正式可复用探针入口在 `eng/LogicalCompiledMemoryProbe`。不把大型临时文件加入 Git。
- 判定：LC-T3 达到“进一步减少大 Logical 编译等待与有界工作集”的目标；不改变预算和音乐语义。小结果及部分查询没有稳定加速、Shared 单次峰和高基数多结果峰可能增加，已作为验收已知代价保留。没有以强制 GC、丢来源、放宽校验、扩大预算或改格式掩盖代价。

## 变更边界与人工验收

| 位置 | 本轮职责 |
|---|---|
| `Midora.Compiler/CompactCanonicalStorage.cs` | 热记录、冷来源表、独立 reader、精确比较与最终重映射 |
| `Midora.Compiler/CompilerValueStore.cs` | cursor-local 四页随机读与完整校验/预算归还 |
| `Midora.Compiler/LogicalRawEventPatterns.cs`、`MidoraCompiler.cs` | 接入 Raw 物化、排序、Range/FIFO；不展开全量公共记录 |
| `Midora.Compiler/LogicalCanonicalEventSource.cs` | 最终紧凑页、索引与公共消费者 cursor |
| Compiler tests、`eng/LogicalCompiledMemoryProbe` | 字段/失败/生命周期回归、单次 Full 与多结果峰分离测量 |
| 本记录、时间方案/报告、内存设计、roadmap、收尾台账 | 记录正式状态；历史实验数据不改写成当前结果 |

用户已对本轮交付确认“验收通过”。以下保留交付时建议的四组代表场景；该确认不扩张为全部项目、规模与硬件的覆盖：

1. 常用 Event Instrument，含 Loop、Pre-Roll、Mapping：Full Compile，播放及中途播放；听感、诊断定位与原先一致。
2. 高展开 Logical Segment：编译、修改、Undo/Redo 后反复编译；观察等待时间与内存是否稳定，编辑功能应保持不变。
3. Shared Logical Usage 与 Pure MIDI 混合项目：All Tracks/Compiled、播放与 MIDI/音频导出保持一致；尤其检查同 Channel 的事件状态与硬边界。
4. 大编译中取消、再编译、关闭并重开项目：不出现半成品结果、失效来源、异常关闭或不释放的旧 owner。

本轮不为这些验收修改音频配置、DSP、视图缓存或编辑命令；不将有限回归结果表述为全部项目与硬件的性能保证。后续极端 Tick/SMF 编码防护仍按原计划另行实施。
