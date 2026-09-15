# Logical 编译时间专项分析与后续实施方案

日期：2026-09-10。状态：**专项分析；不是已实施或已验收的时间优化**。

后续状态：本文保留分析当时的判断和候选数字。LC-T1/T2 已实施，见[时间回收报告](Midora-Logical-Canonical-Time-Implementation-2026-09-10.md)；用户在 LC-T3 隔离实验后另行批准正式实施，当前实现与验证见[LC-T3 记录](Midora-Logical-Canonical-Compact-Implementation-2026-09-10.md)。下文“条件项”不是当前未获授权或仍未实施的结论。

## 1. 本轮边界与结论

上一轮 Logical canonical 有限内存实现保持了所测正式结果，但大型 Logical 编译的时间退化未获接受。本轮保留该工作树，不回退、不继续修改产品；只读审计和隔离实验位于 `.tmp/logical-time-analysis/`。不修改 SRS、版本、项目格式、音频语义或编辑行为；不提交、推送、发布，不使用 computer-use。

结论先行：**当前数倍时间退化不是“有限内存必然如此”。最主要的问题是完整大记录经过太多中间结果，每趟 spill 又重复对这些大记录计算完整性校验。** 这与单纯的磁盘带宽不足不同，也不意味着应该直接删除校验。

此外确认了排序枚举装箱、音频 metadata 快路径闭包分配、索引/指纹重复扫描等可改善点。建议以“减少数据体积和全量处理次数”为主线，以有界工作集调优为辅，不能只扩大内存或换一种压缩算法就宣布完成。

本文的实测、源码事实、设计建议和未验证收益分别注明。原内存报告仍是此前实现的记录，不因本文而成为已接受的时间性能基线。

### 1.1 Requirement trace

| 项目 | 本轮依据与约束 |
| --- | --- |
| 输入 | 同一 Project Source、CompilationRequest、源修订和消费者参数 |
| 正式输出 | 全部事件、完整来源、顺序、计数、诊断、分配、Conductor、SMF descriptor、canonical/consumer fingerprint 均保持 |
| 性能目标 | 消除当前明显时间回退，同时保留有限内存、旧读者合法性与失败原子性 |
| 失败 | 取消、临时 I/O、损坏、预算、算术/容量失败不得发布半成品；只释放本事务所有数据 |
| 持久化 | 仅本机运行时临时页/索引的物理实现可变；`.midora` 与 Mapping ABI 不变 |
| 非目标 | 极端 Tick/SMF delta 下一项、音频 DSP、UI/选择/编辑功能重构，不与本轮混做 |

需求依据：SRS [第 12 章](Midora-SRS-Initial-Release-v0.1/12-Compilation-and-Canonical-Compiled-Result.md) §12.4、§12.10～12.12、§12.17、§12.21、§12.24～12.25；[跨系统不变量](Midora-SRS-Initial-Release-v0.1/22-Requirement-Locator-and-Cross-System-Invariants.md) INV-010/015/020/025/066。特别是 §12.24 与 INV-020：正确性、确定性及上限满足后，时间优先于最小空间；§12.25.1 并不要求每事件复制完整 SourceReference。

## 2. 测量范围与可信度

### 2.1 对照版本

- 旧数组基线：`c02e0bc`，Compiler MVID `10fd65c7-51a0-4658-8295-4b9b4f67ae81`。
- 上轮最终有限内存 candidate6：Compiler MVID `331a2a8f-d5a4-4309-9a1b-63c1af7348b0`。
- 本轮 instrumented compiler：复制当前 Compiler 源码到独立 `.tmp`，链接冻结依赖；增加计时/实验开关，不覆盖上述二进制。
- 开始时记录了正式 `src/eng` 中 1050 个文件的 SHA-256；结束时复核，用于证明本轮没有暗中修改正式实现。

详细旧数据见[内存验证报告](Midora-Logical-Canonical-Memory-Verification-2026-09-10.md)。本轮测试机为 Intel Core i7-10750H，6 核/12 逻辑处理器，.NET 10.0.11、Release、win-x64。

### 2.2 方法

1. 合成有效 Logical 项目，覆盖大量共享触发和模板/Loop 扩张；不是用原样 Pure MIDI 的 9KX2 冒充 Logical 编译压力。
2. 编译 Full → 修改一个源音符 → Incremental → 新 Compiler 的 fresh Full。保留旧结果检查不可变读者；最后受控 GC 检查 owner 释放。
3. 完整 oracle 与编译计时分开；它覆盖全部事件和来源，而不是只比较总数或首尾采样。
4. 分阶段计时；页级计数/计时记录 SHA、codec、文件读写、排序 run；没有每事件 Stopwatch。归并迭代器跨 `yield` 的 inclusive 时间包含下游处理，**不得再次与内部细项相加**。
5. 所有重型实验串行，Windows Job 8 GiB 进程树 Private 硬上限、至少 2 GiB 系统可用余量。未放宽守护以获取更大样本。
6. 首次 Full 包含冷 JIT/初始化；fresh Full 是热进程、独立 Compiler，不代表操作系统文件缓存被人工清空。少数次测量不能当作 p95/p99 或跨硬件保证。
7. 文件 I/O 计时是进程调用耗时/字节；可能命中 Windows 文件缓存，不能直接解释为物理磁盘传输量。

本轮没有实际声卡/SF2、WPF 真实编曲或整个 1800 万音符源项目的人工测试；不得将 console 数字直接宣称为用户编辑界面端到端耗时。

## 3. 热点分解：不是主要卡在物理磁盘

### 3.1 约 210 万事件的热编译

夹具 `Expansion128`：128 个触发 × 4 SubVoice × 256 模板音符 × 8 Loop，Full 输出 2,102,316 Events、1,048,576 NoteOn。以下为本轮 instrumented **fresh Full**，总计约 28.652 秒；该阶段的事件内容已包含探针的一次源修改。上轮同夹具 candidate6 fresh Full 为 27.864 秒，不能混成同一次测试。

| 互斥阶段 | 时间 |
| --- | ---: |
| Raw 展开 | 约 1.448 s |
| Materialize 构建/初始 runs | 约 3.333 s |
| Materialize 归并与折叠 | 约 8.926 s |
| Range 正序扫描/写中段 | 约 4.319 s |
| Range 最终归并/写最终页 | 约 4.617 s |
| 最终页目录/计数/驻留预热 | 约 2.052 s |
| canonical fingerprint | 约 3.953 s |

其余为 allocation、投影及封装；上表因四舍五入不作为精确加总。

同一次运行的内部成本如下。这些细项已经包含在上表中，不与上表相加：

| 内部工作 | 时间/量级 | 解释 |
| --- | ---: | --- |
| 页 SHA-256：读校验 | 约 9.932 s | 每次从 spill 解码后重新校验 |
| 页 SHA-256：写校验 | 约 8.553 s | 每份中间/最终 spill 页生成时校验 |
| SHA 合计 | **约 18.486 s，64.5%** | 全部来自 Canonical 宽记录；本夹具 Raw 未 spill |
| Deflate 编码+解码 | 约 3.363 s | 与 SHA 独立计时 |
| 实际文件 read/write 调用 | 约 0.230 s | 不能把 SHA/解压耗时都算成磁盘等待 |
| run 内排序 | 约 1.047 s | 不包含归并比较，不能称全部排序仅这些时间 |

全流水线校验的原始数据为 **4,915,757,056 bytes，约 4.578 GiB**：读 2533.875 MiB（2796 次），写 2154.156 MiB（2377 次）。但一份最终裸记录只有约 **465 MiB**。压缩后的实际读写总量约 353.5 MiB。这就是“磁盘流量不大，但编译很慢”的主要解释。

发布阶段也得到明确分解：

- index 约 2052 ms，其中页 SHA 约 1801 ms、解压 168 ms、文件读取 18 ms，其他约 66 ms。
- fingerprint 约 3953 ms，其中页 SHA 约 1477 ms、解压 141 ms、文件读取 15 ms，其他约 2320 ms。
- 后一个“其他”包含 FNV、枚举和记录传递，不能全称作纯 FNV 算术。

首次 instrumented Full 为约 37.4 秒，明显高于热运行；本文保留该反证，不用它夸大稳定退化。52.6 万事件的 fresh Full 约 5.570 秒，其中页 SHA 约 3.117 秒（55.9%）、codec 约 0.591 秒、文件调用约 0.051 秒，也指向同一问题。

### 3.2 为什么会反复处理这么多数据

当前物理流水线：

```text
compact Raw
  → 恢复完整 SourceReference + 232-byte CanonicalMidiEvent
  → 排序 runs / 中间归并
  → 降序同 Tick 折叠，写 allEvents
  → 正序 Range 扫描，写 middle
  → 反向 middle + 边界归并，再写 rangedStore
  → 全读建页目录/计数/预热
  → 再全读计算正式升序 fingerprint
```

每个 spill 写/读均叠加 SHA；中间数据越多、趟数越多，校验越多。232 bytes 是 `Unsafe.SizeOf<CanonicalMidiEvent>()` 实测，其中 `SourceReference` 为 160 bytes。这里不是只传 3 个 MIDI 字节，而是携带完整来源与排序语义的大记录。

源码位置：`MidoraCompiler.cs` MaterializeEvents（约 3149）、ApplyRange（3352）、ForResult（4231）；`LogicalCanonicalEventSource.cs` 构造索引与 FinalizeRangeEvents；`CompilerValueStore.cs` FlushPage、ReadPage、外排序 Merge。源码是事实依据，具体 CPU 占比则以本轮测量为准。

## 4. 其他已确认的非必要成本

### 4.1 排序：枚举装箱与大记录热路径

冻结 candidate6 的 `CanonicalComparer.Compare` Release IL 共 1252 bytes，包含两个显式 `box`：

- `x.Role.CompareTo(y.Role)`：`IL_002D box` → `System.Enum.CompareTo(Object)`。
- `x.Source.Origin.CompareTo(y.Source.Origin)`：`IL_04D3 box` → 同类调用。

静态 IL 不代表每次比较固定分配两个对象，JIT 可能消除某些装箱。为此另做真实事件排序隔离实验：525,612 条旧 canonical 事件确定性打乱，比较原宽记录排序与 40-byte 热排序 key；key 前缀相等时仍调用原完整比较器，排序后逐记录比较一致。

| 分布；热轮中位数 | 原宽记录排序 | 热 key 构造+排序 | 记录一致性 |
| --- | ---: | ---: | --- |
| 真实事件打乱 | 约 351 ms | 约 14 + 93 ms | 全部相同 |
| 将同组真实记录 Tick 统一的人工极密分布 | 约 560 ms | 约 13 + 158 ms | 全部相同 |

原宽记录每次累计分配约 31.8 MB / 539.4 MB；热 key 约 21.1 MB（含 key 数组）。首个 key 轮次存在明显 JIT 预热差异，未混入热轮中位数。

**限制：**这不是完整有界排序实现，也不是新 canonical 布局。它同时改变记录搬运和比较方式，不能把全部收益归于其中一个因素。40 bytes 只是排序 key；完整记录还需要 MIDI 数据、语义组和可逆来源。全量 key 数组只在隔离 micro 中用来测机制，正式实现必须限定在固定 run 内，不能将它照搬为全事件数组。

### 4.2 音频 metadata：快路径意外创建闭包

`CanonicalAudioUnitProjection.ResolveBuilder` 的 Release IL 在方法入口就创建 display-class，并把完整 232-byte event 复制进去。命中字典后立即返回也绕不开这个入口分配。

- `IL_0000 newobj`；`IL_0008 stfld` 完整事件；快路径成功返回在 `IL_008B`。
- x64 闭包估算约 248 bytes。上轮 1,049,260 Events 的两遍 metadata：`1,049,260 × 2 × 248 ≈ 520.4 MB`。
- 上轮该步骤实测累计分配 524.5 MB，量级高度吻合；本轮没有做分配栈采样，不把它说成精确逐字节归因。

修法是分开快路径与 fallback，或使用无捕获循环；保留唯一候选检查和原 fragment owner 规则。**它不是有限内存所必需的代价，也不需要改音频语义。** 该步骤本来就是后台发布前准备，所以真实“编译就绪”耗时还包含它，不能只报告 compiler-only 时间。

### 4.3 fingerprint 算术存在较小优化空间

FNV 的 8-byte 整数输入若高位为零，那些零字节仍必须参与原算法，但可以用模 2^64 下的 `Prime^N` 等价乘法合并，**不是省略字段或改 hash 版本**。

独立 primitive 实验验证 1,000,000 个混合零/小正数/大正负数及 12 个边界值、变化的初始 hash，逐项与旧算式相等；整序列 hash 也相同。该混合分布中热轮约 13～14 ms → 约 10 ms/百万个 long。这里只测整数原语，不是整个正式 fingerprint 或编译收益；其优先级低于减少校验字节与扫描次数。

### 4.4 消费者重复扫描的边界

- 当前音频 metadata 两遍的原因是旧 V1 fragment hash 先写 EventCount，再写内容。不能直接删掉第一遍然后改变 hash。
- MIDI 导出 Coordinator 汇总 Track/Unit、Exporter 完整校验、随后逐 Unit 输出，仍有额外扫描。
- Unit mask 只表示页里是否存在该 Unit；若多数页都交织大量 Unit，逐 Unit 导出可能重复解码同一批页。此项当前只有代码依据，没有新的极端导出耗时测量。
- MIDI writer 已一次流式写入并回填 MTrk 长度；不能错误地把它说成“为了长度又全扫一遍”。

## 5. 已排除的简单修法

同环境串行对照，仍使用 Expansion128。每一行都是独立进程的 Full/Incremental/fresh Full；单次结果仅用于归因，不当作稳定百分位。

| 变体 | Full / Incremental / fresh Full（秒） | 进程 Private 采样峰 | 判断 |
| --- | --- | ---: | --- |
| 未修改 frozen candidate6 复跑 | 32.816 / 28.344 / 28.605 | 约 1.56 GiB | 当前环境参照 |
| 临时 clone，关闭计量，逻辑不改 | 29.911 / 28.490 / 32.777 | 约 0.95 GiB | 未恢复旧版速度；有冷启动/调度波动 |
| 驻留 128 → 512 MiB，其余原样 | 21.385 / 19.565 / 19.897 | **约 4.07 GiB** | 有收益，但三份合法结果并存及 CLR 高水位令峰值明显增加；不能作主方案 |
| 128 MiB，关闭压缩，仍校验原始页 | 28.518 / 26.788 / 33.455 | 约 1.60 GiB | 未消除主要成本；读 2.657 GB、写 2.259 GB |
| 128 MiB，仅消除两处枚举装箱 | 29.627 / 28.563 / 29.792 | 约 0.92 GiB | 累计分配少约 118 MB，但总耗时改善未超过运行波动 |

所有行完整 oracle 一致，守护未触发。instrumented fresh Full 28.652 秒与未修改 frozen6 的 28.605 秒接近；关闭观察器也没有恢复旧版速度，支持“SHA 是实际热点而非探针制造”这一判断。但多组墙钟存在波动，不能把该单次差额认定为固定的 0.17% 观察器成本。

压缩关闭后的 fresh Full 墙钟约 33.455 秒、CPU 约 29.656 秒，存在 I/O/调度差额。因此不据这一行声称压缩一定更快或更慢；能确认的是校验的原始字节数未减少、总退化仍在，而且磁盘流量大幅上升。

峰值包括三个合法结果、oracle 临时序列化及 CLR 高水位，不是单次 result 驻留。尤其不能把“noBox 进程峰值更低”全归于省掉 118 MB：分配时机/GC 状态也会影响峰值。

## 6. 后续实现设计

### 6.1 优先方向：校验实际存储表示，而不是取消 SHA-256

当前 compressed page 是先对大 raw record bytes 做 SHA，再压缩；读取时解压为完整 raw bytes 后再次 SHA。磁盘实际存的是很小的压缩数据，却反复散列大得多的还原数据。

建议评估的正式页结构是：

```text
固定、版本化、经过范围验证的页描述符
  + 完整 stored payload（compressed 或 raw）
  + SHA-256(descriptor || stored payload)
```

写入时校验将落盘的完整表示；读取时先检查长度/预算并验证完整 SHA，再解码；解码必须恰好产生声明的字节数、不能少字节或有额外输出。不可压缩的页直接保存 raw，仍校验全部 raw，不能因“不值得压缩”而跳过完整性保护。

必须说明的边界：

- 这是**编译器私有临时页格式**的改变，不是改 `.midora`、source pack checksum、canonical fingerprint、音频 fragment hash、原生 DLL 校验或 SF2 策略。
- 完整校验 stored bytes 能检测落盘表示的损坏；它不等同于额外校验 codec 是否错误地产生了另一份 decoded bytes。正式采用时要明确此保护边界，并以独立 codec 往返/差分、完整 canonical oracle、损坏与截断测试补足，不能宣称两种校验覆盖一切完全相同。
- 固定描述符至少绑定内部格式版本、record kind/size、record count、codec、stored/decoded length；解析前先作安全长度预算，不能先按不可信长度分配。
- descriptor 必须与 payload 一起受保护；不能只校验 payload，却允许错误的 codec/count/record type 将同一数据解释成另一种记录。
- 读者在校验、解码任一失败时明确中止，不跳页、不返回部分成功、不静默重试。
- 不把“曾经验证过一次”推广成以后每次实际磁盘重读都无校验；合法驻留的不可变已验证页可继续复用，不增加无界解码缓存。

#### 6.1.1 已完成的 stored-SHA 隔离实验

复制出独立 Compiler MVID `bafa43e9-1b3c-4c8e-b046-79e9cf5c20da`，仅将页 SHA 对象从 raw 改为 stored payload，仍为 128 MiB 驻留、原比较器和原 run/fan-in。读时先完整 SHA、再解压并检查 exact decoded length；canonical 音乐 fingerprint 不改。

**原型尚未把描述符纳入 SHA，不是可以直接合入产品的完整页格式。** 它用于验证“减少校验字节数”的收益，而非宣称完整性设计已完成。

| 夹具 | Full / Incremental / fresh Full | fresh Full 参照 | Private 采样峰 |
| --- | --- | --- | ---: |
| Expansion128 | 12.899 / 11.430 / **11.104 s** | 当日 candidate6 28.605 s；旧数组版历史 7.456 s | 约 1.68 GiB |
| Shared100k | 8.355 / 6.373 / **6.366 s** | candidate6 历史 14.386 s；旧数组版历史 4.415 s | 约 1.42 GiB |

两组完整旧 oracle、Full/Incremental/fresh Full、清 cache 后旧 reader、关闭后 0/5 owner 均通过，守护未触发。Expansion128 热编译约减少 61.2%，但仍比旧数组版历史样本慢约 49%；Shared100k 仍慢约 44%。**有显著改善空间不等于本次已经实现“时间无回退”。**

Expansion128 的热页 SHA 降为读 740.4 ms、写 639.7 ms，总计约 **1.380 s**；校验对象从 4688.0 MiB raw 降为约 **353.5 MiB stored**。Deflate 仍约 3.259 s，累计托管分配仍约 1.375 GB，未通过多留内存或少生成事件取得这个收益。后续应依据新的剩余热点继续优化，不再按原先 18.5 秒 SHA 占比推算所有改动的收益。

### 6.2 减少 Range 与 publication 的全量中间结果

不建议只为消除复制而永久让小范围 result 引用完整 allEvents 大文件：这会使小范围消费者长期保留远超需要的 backing，且很难准确释放。

推荐目标结构：

```text
有界排序 + 原同 Tick 折叠
  → 已排序源
  → 正序 Range 状态/FIFO sweep
  → 唯一最终有序输出器
      → 最终页 + 页目录 + NoteOn/总数
      → 现有字段顺序的 fingerprint 累加器
```

具体边界：

1. 保留范围前状态、活动 Note FIFO、起点恢复、硬结束 NoteOff/Reset。**Full 从 0 开始也不能直接跳过 ApplyRange**：现有末端事件可能被重建为 `CompilerBoundaryCleanup` 来源，听起来一样也不代表完整结果一样。
2. 中段只过滤/顺序输出，不先写一份 middle 再写另一份完整 rangedStore。
3. 起点新恢复状态与该 Tick 的已有事件按原比较器及 SemanticGroup winner 合并。若需知道该 Tick 最终状态，使用有界预读/第二次读取该边界，不能把百万个同 Tick Note 全塞进内存。
4. endTick 的适用 endpoint 处理完成后停止读无关后缀，不继续扫描其后的全部 Logical 历史。
5. 页 Tick bounds、Unit mask、计数在最终 winner 写入时产生，不为它们再读完整结果。缓存晋升单独按预算处理，避免把成本移到首次播放。
6. FNV 使用 `Begin / Append / Complete` 形式，字段、类型宽度、顺序完全沿用旧算法。降序页直接 hash、分别 hash Logical/Pure 后拼接、以页 SHA 代替正式 hash，都不成立。
7. 混合 Pure/Logical 仍在正确的总序位置进入原 fingerprint 流。分页/内联阈值和布局不能改变 fingerprint。

如果一次把 Range 升序输出与全部发布摘要融合风险过高，可先“最终写页同时产目录，保留一次升序 fingerprint/预热扫描”，再单独删除 middle；每步均检查完整 oracle，不能一次重写后只比事件数。

### 6.3 紧凑 canonical 热记录与有界来源表

现有 Raw 已分离 context/pattern。Materialize 又先恢复完整 SourceReference，抵消了该结构在后续排序中的优势。长期应直接从 compact Raw 进入 compact canonical 热路径。

建议先原型验证 **80～96 bytes/完整紧凑记录 + 有界来源表**，不是承诺最终尺寸。40-byte micro key 不含全部字段，不能当正式记录尺寸。目标布局必须能够无损恢复：

- Event.Tick、Source.Tick 分别保存；不能假定相等。
- MIDI message、Role、StableOrder、SemanticTargetKey/Group、物理 Unit、必要 SMF 字段。
- Track/Segment/Note/Instrument/SubVoice/Usage context。
- Template/Parameter/Mapping/Step/Function/Curve/Envelope/Origin 等来源差异及其余 Source 字段。

必要约束：

1. context/pattern 表是 immutable、带 owner 的正式结果后备，不得引用继续增长或清 cache 后即失效的可变 Raw pool。
2. intern Dictionary 有上限；来源 Tick 等逐事件值不能直接放进无限增长的全 Source 去重表。超限可降低去重率、改用有界页/sidecar，不能丢来源。
3. 来源表、排序 key、reader、活动 Note FIFO、扩容新旧数组均计入预算；不能只报告 event record 数组变小。
4. 池索引不是排序语义。完整 comparator tie-breaker 必须与旧版一致；不要顺手加入旧比较器或旧 fingerprint 没有的字段。
5. 不能让每次比较都随机解压来源页。run 内按预算准备所需比较信息；稀有 tie 的 fallback 也需有界，不把查表转成另一种 I/O 热点。
6. 公共 consumer 只在有界 page/cursor 边界恢复完整 `CanonicalMidiEvent`；不恢复成全事件数组。

这项涉及更多所有权和比较器代码，风险高于单纯存储校验与消除闭包。应由前述实验后的剩余热点决定实施深度；若较低风险步骤已经满足双门槛，不为“结构更漂亮”强行同时重写全部模型。

### 6.4 排序工作集和 codec 策略

- 把两处 enum 比较换为保持原枚举数值顺序的强类型比较；本轮全编译实验只证明省分配，不夸大总时间收益。
- 固定 run 内可采用紧凑热 key/索引，公共事件还原顺序完全相同，不使用全量 key 数组。
- 以总字节预算决定 run/fan-in，而不盲目把所有泛型 sorter 都改成更大的 records 数。
- 2,102,316 records 在 32768/run 时约 65 runs；131072/run 时约 17 runs，配合合适 fan-in 可少一次中间归并。本轮已做下述组合实验，但还不能定为最终参数。
- 准入预算须包含所有 decoded/encoded reader buffers、codec scratch、输出页、heads/key、边界状态及并存的新旧数组，不能因 fan-in 上调突破工作集。
- 不凭某组压缩率就关闭全局压缩；区分临时 runs 和最终页，分别测冷读/顺序读/反复 Unit 查询。低压缩率数据必须有 raw fallback。

隔离 tuned 候选 MVID `59fba8aa-abe8-4ee3-a15d-9580213ae674`，在 stored-SHA 原型上加入无 enum 装箱，且**只将 Canonical 排序**的 run 从 32768 改为 131072、fan-in 从 16 改为 32；Raw 不变。驻留/工作/metadata 预算仍为 128/128/64 MiB。

| 夹具 | tuned Full / Incremental / fresh Full | 对 stored-SHA 的 fresh 改善 | Private 采样峰 |
| --- | --- | ---: | ---: |
| Expansion128 | 14.146 / 9.947 / **10.326 s** | 约 7.0% | 约 1.08 GiB |
| Shared100k | 7.597 / 5.682 / **5.667 s** | 约 11.0% | 约 1.19 GiB |

Expansion128 的归并调用由 6 降为 1，少读取 513 页、约 464.9 MiB raw，工作预算采样峰约 33.2 MiB。完整 oracle、旧读者、关闭释放和守护均通过。但它的首次 Full 比 stored-SHA 更慢，保留这个反例；两项都只有单次独立进程，不能宣称各阶段稳定改善。Expansion128 未达到预设的超过 10% 追加复测门，所以没有追加两次相同测试，也没有将这些数据伪称为独立重复的中位数。

这证明减少归并层数有效，但当前组合收益远小于修正 SHA 对象的收益。不能把组合收益拆成 run、fan-in、无装箱各自的独立贡献；tuned 对旧数组历史 fresh Full 仍慢约 38.5% / 28.4%，还需要减少后续全量处理，而不是继续盲目增大 run。

### 6.5 消费者准备与复用

优先独立修正 `ResolveBuilder`：快路径无闭包，fallback 保留 `SingleOrDefault` 的多候选拒绝，不能改成 `FirstOrDefault` 隐藏归属错误。

进一步减少音频 metadata 一次全扫需要中立摘要：在**最终 Range/折叠之后**冻结准确 fragment 计数，随后仍按旧 count-prefix hash 格式顺序写内容。不能用 Raw 数量，也不能让 Compiler 反向依赖 Playback；Shared Usage 和硬边界归属只维护一套规则。Bank→Program→NoteOn 的 preset 状态必须按音乐正序处理。

MIDI 导出若被实测证明逐 Unit 重读是瓶颈，再评估有界 Unit 索引/轻量 spool；不预先为所有结果永久存第二份全项目投影。导出校验只能由等价的已冻结摘要替代，不能删掉错误检测。

session 发布前的预热、源修订复核、sample generation、失败后的镜像恢复、旧 reader 保活必须保持。现有同结果 `CreatePlaybackView` 共享可复用；不能据此跨修订复用 metadata。增量后缀复用仍要求 allocation/活动 Note/共享状态 checkpoint 收敛，不能只看“这页源没改”。

## 7. 推荐实施拆分

不建议再拆成八轮用户验收。这是同一条编译数据路径的时间回收，适合 **两个必要内部增量 + 一个条件增量**；每个增量有自动化门，用户最后集中验收一次。若中间改变保护边界或遇到未批准语义，单独暂停确认；不以“性能优化”为由静默接受。

### LC-T1：纠正重复校验与确定的无效分配

1. 正式定义私有页的版本化描述符、stored SHA 和 exact decode 契约；不能直接拷回本轮缺少 descriptor 保护的原型。
2. 保留 raw fallback、每次实际重读完整校验、预算前置、失败原子性与取消；给 codec 本身的正确性单独补门。
3. 消除枚举装箱及 `ResolveBuilder` 快路径闭包，保留完整比较顺序与唯一归属检查。
4. 大 run/fan-in 仅在读者全部缓冲纳入同一预算后选取；分别测试，不把本轮组合值当魔法常数。
5. 测完整编译及 metadata-ready。达到明显改善只是进入下一步的条件，不表示已经接受相对旧版的剩余回退。

输出：正式设计记录、完整性测试、旧 oracle 对照和新热点分解。此步主要改变私有存储与分配实现，音乐语义风险较低，页损坏/释放/预算实现风险中等。

### LC-T2：减少 Range 与发布阶段的全量 passes

建议按以下顺序小步完成，每一步均保持可比的完整结果：

1. 最终写页顺带生成目录、Unit presence、计数；避免为这些简单摘要单独读完整结果。
2. 改为有界正序 Range writer，消除 middle 和第二份 rangedStore 的重复处理；精确处理起点/终点同 Tick 群，不跳过 Full 的边界语义。
3. 在最终升序输出或必要的统一升序归并中累加原 fingerprint；混合 Pure/Logical 不能分别 hash 后拼接。
4. 仅在剩余实测值得时，加入逐项等价的 FNV 原语优化；不能以 primitive micro 通过代替正式 fingerprint golden。
5. 减少 metadata 重复读取时，将准确中立计数冻结在最终结果，保留旧 count-prefix hash 和状态机。

输出：Full/Incremental/范围/来源/消费者等价证据，真实 ready 时间及资源表。这一步涉及排序、边界和生命周期，正确性风险比 LC-T1 高；需要全套编译器/消费者回归，不能只看听感或事件数量。

### LC-T3：条件实施紧凑 canonical 热路径

若 LC-T1/2 后大记录搬运、归并或 codec 仍占显著比例且时间门未过，再实施第 6.3 节；不是现在就承诺一定全部重写。

1. 先在固定 run/cursor 中验证完整紧凑记录，逐字段还原比对。
2. 完成有界且 immutable 的 source context/pattern 所有权，覆盖高熵来源、无去重收益、多个旧 revision 同时存活。
3. 再扩展至 raw→canonical→range→final 全热链；对外保留统一 canonical 语义，不把 compact 业务解释泄漏给每个消费者。
4. 若后续证实多 Unit 导出重读占主要成本，另加有界 Unit 索引/spool；没有实测依据时不增加第二份永久投影。

输出：尺寸、完整来源、预算和旧读者证明，以及端到端收益。这是风险最高的一步，不能仅凭预估 record 变小倍数推算整编译加速倍数。

### 7.1 需要保留的实现决策边界

- 没有新增音乐语义、项目格式或用户操作决定。现有 same-Tick winner、Source、FIFO、Range、共享状态、hash 均不变。
- stored-SHA **确实改变私有页校验的保护对象**。它不是删除校验，但对 codec 自身误还原的覆盖与 raw SHA 不同；实施前必须明确采用该可信 codec 边界及相应测试。若不接受，保留 raw 校验，优先减少 passes/记录体积，不能隐藏这一取舍。
- 时间/内存门槛是下面的建议验收策略，不是本轮新定的 SRS 上限，也不是已保证的最终数字。

## 8. 时间与内存双验收门

### 8.1 时间门

比较三个版本：旧数组版、当前有限内存版、待交付版。必须使用同机、同输入、同配置；安全可运行的旧版重新实测，不能只拿本文历史数字判断达标。

- 每个重点夹具至少 3 次独立进程，分别报告首次 Full、单编辑 Incremental、fresh Full 的中位数及最小/最大；三次不能称 p95。
- 同时报告 compiler-only、metadata-ready、第一次范围/Unit 查询、导出准备和实际枚举，防止把成本移到第一次播放/导出。
- 推荐将旧数组版耗时的 **1.25 倍以内**作为大型场景的回收目标，而非承诺；若仍超过 **1.5 倍**，不直接交付为“优化完成”，先重新归因或明确请求接受剩余取舍。小项目优先看绝对延迟和实际工作流，不因毫秒噪声机械判失败。
- 保留原内存报告中的 Mixed32、Complex64、Shared10k、Expansion32、Shared100k、Expansion128；增加局部编辑、非零范围、接近整曲末尾的短范围、多个消费者反复读取。
- 单次结果或 micro 可用于决定方向，不能替代稳定性门；不得把本轮 10.326 s 称为已承诺的最坏耗时。

### 8.2 内存、I/O 与所有权门

- 起步保留驻留/工作/metadata 128/128/64 MiB；不以提高到 512 MiB 驻留作为默认修法。
- 这些是本轮 canonical 子系统预算，**不是整个编译器或进程仅用 320 MiB 的承诺**。源数据、Raw/实例状态、活动 FIFO、诊断、旧结果、消费者和 WPF 均需独立统计。
- 同时测总分配量、GC heap、Private、Working Set、resident/working/metadata 实际峰、临时文件 live/累计读写；Working Set 不代替可达对象分析。
- 包含三个合法结果并存、旧 reader 清 cache 后继续读、消费者仍持有旧结果，以及取消/异常的暂态峰值。
- 正常编译、编辑、取消、失败、关闭循环至少 20 次；有界热缓存可升至平台，不能每轮累积 owner/临时文件。最后受控 GC 仅用于诊断可达性，不把强制 GC 加进普通用户流程。
- 重型测试继续用 8 GiB Job 硬上限和 2 GiB 系统余量；触限标为测试被保护终止，不放宽到导致系统分页假死。
- 不要求每组瞬时峰都低于旧值；允许有明确预算与释放时机的内存换时间，但必须解释峰值来源，不以“缓存”笼统代替证据。

## 9. 正确性与失败测试矩阵

| 范围 | 必须验证 |
| --- | --- |
| 正式结果 | 所有事件及完整 Source、顺序、fingerprint、Unit 分配、诊断、Conductor、SMF/opaque descriptor 与旧版完全一致；Full/Incremental 相同 |
| 布局边界 | 内联/分页阈值、4095/4096/4097、run 和 fan-in 临界值、最后不足一页、一个 Tick 跨多页/run；布局不改变正式结果 |
| 来源与比较 | Event.Tick 与 Source.Tick 不同、所有 Origin、默认/缺省 ID、合法内部 sentinel、低概率 comparator tie；不得遗漏冷字段 |
| 状态与范围 | 非零起点、范围内无音符、只有状态、精确 endTick、硬边界、Note FIFO、多次同 Key、RPN/NRPN 多消息 SemanticGroup、Bank/Program |
| Logical 特性 | Shared Usage、多 SubVoice、Loop、Pre-Roll、Held Preview、短/长 Note 策略、Envelope/Mapping 及其来源 |
| 混合项目 | Logical 与 inline/paged Pure MIDI 混合，固定/自动 Root，乱序集合输入、同输入重复编译；Pure 链不被顺手改写 |
| 页完整性 | descriptor 各字段损坏、payload 截断/改字节、错误 checksum、原始回退、高熵页、错误 codec、有效 SHA 但非法压缩流、解码过短/过长及 record count 不符 |
| 取消/原子性 | 展开、run flush、merge、Range、finalize、metadata 各阶段取消；预算不足、磁盘错误；不得发布半成品或删除其他合法 owner 数据 |
| 并发/寿命 | 两个独立 reader、多个 revision、cache clear、结果关闭、旧 reader 存活；不得复用错误 revision，取消不破坏他人正在读的页 |
| 消费者 | 起点状态恢复、fragment/hash/事件计划一致、Onion 来源及计数、MIDI 正式内容一致；Mute/Solo 仅消费过滤 |

测试不能仅通过改变 expected 值“接受新结果”。若旧行为与规范确有冲突，独立报告并请求决定，不混在时间优化里修改。

用户最终集中验收建议仅覆盖四条真实工作流；大量字段及失败注入交给自动化：

1. 小型事件乐器：创建音符、Loop/Mapping、编译、播放和中途播放，确认无固定等待回退。
2. 大型 Logical Segment：从大型 MIDI 复制大量音符后，分别小编辑/大编辑、Undo/Redo，观察编译完成和可播放时间；不是只打开原 Pure MIDI 测试。
3. Shared Logical + Pure MIDI 混合：Full 与编辑后 Incremental、MIDI/音频导出准备及使用后的项目关闭重开。
4. 编译中取消、快速连续编辑/切换消费者，确认旧结果不会错误替代新结果，也不会出现持续增加的内存和临时文件。

这些是**未来实施后的验收清单**，本轮没有执行真实 WPF 或音频人工验收。

## 10. 证据、局限与本轮交付

可复查的本机实验材料位于 `.tmp/logical-time-analysis/`，它们是 ignored 临时诊断材料，不要求发布或入库；关键数字与限制已在本文归档：

- `RESULTS.md`：11 个受守护编译探针进程的汇总、MVID、完整 oracle/释放结果。
- `runs/*/result.jsonl`、对应 `*-guard/guard-result.json`：阶段计时、分配、读写、退出和守护记录。
- `Run-Attribution-Matrix.ps1`、`Run-Stored-Checksum.ps1`、`Run-Tuned-Candidate.ps1`：串行对照脚本；拒绝覆盖既有结果。
- `sort-probe/`、`sort-run1/`：完整比较顺序的排序 micro；`fnv-probe/`、`fnv-run1-guard/`：FNV 整数原语等价实验。二者不包含在上述 11 个完整编译进程计数中。
- `pipeline-il/result/`、`consumer-audit/ResolveBuilder-IL-evidence.md`：冻结 Release 二进制的枚举装箱/闭包证据。
- `formal-file-hashes.json`、`Protect-Workspace.ps1 -Verify`：本轮正式 `src/eng` 文件保护复核。

全部 11 个编译进程正常退出，完整旧版 oracle、Full/Incremental/fresh、保留旧结果与关闭后 0/5 owner 检查通过，8 GiB/2 GiB 守护均未触发。**这不等于临时原型已通过正式产品回归**：本轮没有执行 descriptor/codec 损坏注入、所有 Range 组合、真实 WPF 或声卡测试；这些属于第 9 节实施门。

仍未确定的事项：紧凑 canonical 最终可达尺寸和全链收益、LC-T2 合并 passes 的实际收益、多 Unit 导出最坏读取量、其他 CPU/磁盘下的收益幅度。不能由本文给出“必定恢复旧版速度”或“所有项目只慢某一百分比”的保证。

本轮正式新增仅本分析方案；不改产品源码、已有 SRS/路线图或用户草稿，保留此前未提交的有限内存改造。结束时已复核正式 `src/eng` 共 1050 个文件：Changed 为零、New 为零；`git diff --check` 通过，本文无行尾空白。不提交、推送或生成 `dist`。
