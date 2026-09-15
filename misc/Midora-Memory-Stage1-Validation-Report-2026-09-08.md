# 内存优化阶段 1：Pure MIDI 只读链路验证报告

日期：2026-09-08。状态：**阶段 1 工程门通过。M01、可重复基线和操作覆盖索引已完成；人工验收合并到阶段 2 后的验收 A。**

依据：[六阶段执行与验收计划](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md) §6、[原调查](Midora-Stage8-Memory-Investigation-2026-09-08.md) M01、[只读值设计记录](Midora-Memory-Stage1-ReadOnly-Values-Design.md)。

## 1. 本阶段改变什么

只修改 Pure MIDI 三类记录的只读访问，以及依赖它们的保存、打开、自校验和小内容编译读取：

| 位置 | 修改 |
| --- | --- |
| `Midora.Domain/PureMidiPagedContent.cs`、`PureMidiReadOnlyValues.cs` | Note / Channel Event / Opaque 增加正式顺序的 `EnumerateValues(CancellationToken)`，冻结已提交不可变根，按 source ordinal、replacement、tombstone、added tail 流式读取。 |
| `Midora.Persistence/MidoraProjectPackageV1.cs` | 保存预检、打开后的全包 ID 校验改用只读值；向遍历传递取消。全局唯一性、高水位和所有记录的检查保留。 |
| `Midora.Persistence/PureMidiContentPackPersistenceV1.cs` | 编辑后的 merged pack writer 直接写 value；无编辑的原 pack 复用、校验、事务和自校验不变。 |
| `Midora.Compiler/PureMidiCompilation.cs` | 小内容 / 非分页 materialization 的只读枚举改用 value；既有排序、可听语义和延迟 canonical 路径不变。 |
| 测试和工程文档 | 新增只读 / 持久化回归、可复用探针、包内容比较、统计脚本、公共回归入口和 40 项操作覆盖索引。 |

正式 UI、批量操作算法、绘图缓存、音频和文件版本均未修改。没有提交、推送或本地发布，没有开始阶段 2。

## 2. 根因与修复边界

原 mutable collection 的普通枚举会逐条生成可编辑 facade，同时登记编辑索引：Note / Channel 的 `_materializedSourceIds`、`_materializedSourceValues`、`_sourceIndices`，以及 Opaque 的对应原值 / 位置映射。即使消费者只是检查 ID 或写文件，这些表仍会随整个源增长。保存自校验又打开一份 Project，使原项目和自校验项目同时承受该成本。

修复不是删除三表，而是把正式只读流程与可编辑对象分开：

- 新游标只保留迭代状态和已冻结根，不为每条记录注册 facade，不新建全源 ordinal / 空间索引。
- 不使用 tick-range query 替代正式遍历，避免漏掉 crop 外数据、非法 tick 或改变原始序列。
- 导入既有重复记录、端点顺序、NoteOff velocity、Opaque payload 均保留；没有借优化机会去重。
- 仍检查全部 ID、损坏 / 未知字段 / checksum，仍执行保存后重开自校验及原子发布。
- 现有 `TryGetById`、mutable IList、setter、Undo 和 retained facade 身份保留。未提交 batch 不可泄露为“完整只读快照”。

这里的有限空间指**枚举的额外空间**，不是整个保存流程严格常量 RAM。原有解码 cache、全包 ID 位图、pack builder、History 等仍有各自的所有权和预算。Opaque 的 payload 使用已有只读内存语义，不再为本次扫描复制；payload-aware 预算仍属于 M07。

全部只读消费者的审计结果、保留的可写兼容路径与现有预算，见 [操作覆盖索引](Midora-Memory-Optimization-Coverage.md) §3～4。

## 3. 可重复测量方法

### 3.1 版本与环境

- 修改前源码：`c4be7c89ddcc4e1c19bc01a6fd2656ee16a7347c`；相对原调查源码 `11074bdee3bbaa60b473b435a92016d2d1aad999` 仅有文档差异。
- 本轮开始时唯一工作区内容是尚未追踪的已批准执行计划。没有混入其他未提交产品改动。
- 对照从该 HEAD 导出必要构建文件到 `.tmp/memory-stage1/head-baseline` 并独立构建；没有 checkout、reset 或覆盖当前工作区。
- Release / x64、SDK 10.0.400、运行时 .NET 10.0.11、Windows 10.0.26200、12 个逻辑处理器、Workstation GC。
- 两版使用完全相同的 `eng/MemoryStage1Probe/Program.cs`；记录实际 Domain / Persistence / Application DLL SHA-256，不能只用目录名辨认版本。
- 测量在同机器上交替、串行运行，每个主流程各 3 次。不清系统文件缓存；“新进程”不代表磁盘冷缓存。

### 3.2 样本和工作负载

原 MIDI 只读，输出均位于 `.tmp/memory-stage1` 的独立新目录，不修改用户原件。

| 简称 | 样本 | 源 Note / Track |
| --- | --- | --- |
| 1M | `D:\MIDI\Black MIDIs\[Black Score] - I'm So Happy.mid` | 1,000,000 / 26 |
| 18M | `D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid` | 17,999,999 / 40 |

样本身份：1M 为 8,010,662 bytes / SHA-256 `b1390ca381916be4c60ef65e8bee5d7e16e75637714454a39c1519be01cf529b`；18M 为 144,175,201 bytes / SHA-256 `948fdddfff2f050c1b4e70f240b6138e5ba10939030a82286e149ddcb93e6249`。

`roundtrip`：对音符最多的 Segment 的前 60k Note 依次右 Resize、Move；每次 Prepare→Commit→Undo→Redo→Undo，再取消一次准备、设置 presentation，然后 Save→Save Copy→Open。保存时音乐内容已恢复，但存在编辑历史。

`edited`：同上，最后再正式提交一次 Move，**保留修改**再 Save→Save Copy→Open。它强制覆盖实际 edited pack 写入路径，不能仅用 Undo 后 pristine 复用代表编辑保存。

`save`：导入后不创建编辑历史，直接 Save；用于补充纯无编辑路径。

每次记录保存 / 重开后的 Note、Channel、Opaque、Track、Segment 数量、ID 高水位和损坏诊断；包内容另外比较，不把数量相同等同于所有事件逐项相同。

### 3.3 指标的含义和限制

- **Save / Copy 时间包含完整事务和严格重开自校验**，不拿简化 value-scan 与旧完整校验作不等工作量对比。
- Peak WS 为操作系统报告的进程高水位；Private / managed peak 为约 100 ms 采样峰值。累计分配是整个流程曾分配的总量，不是常驻内存。
- 正式操作过程中不强制 GC；退出全部 owner 后另行 GC 与 WeakReference 检查。CLR committed、空闲堆段和 WS 不要求立即归零。
- 为隔离 M01，后台编译 debounce 固定为 1 小时；没有运行 WPF、音频 Worker 或播放。不能把这个峰值称为完整应用上限，也不能把命令 Undo 返回时间当作 UI 刷新耗时。
- 探针在 Private Bytes 超过 14 GiB 时请求取消；该限制只保护测试机器，不是新增产品限制。
- TRX 公共回归、真实大样本归因探针与人工体验验收分别记录。大样本性能测试期间不同时运行构建或其他重负载。

## 4. 测量结果

### 4.1 主流程时间：各 3 次，秒

数值为中位数，括号为最小～最大。两种规模 × 两种流程 × 两版本 × 3 次，共 **24 次成功进程运行**。

| 样本 / 流程 | 操作 | 修改前 | 修改后 |
| --- | --- | ---: | ---: |
| 1M / roundtrip | Save | 5.69（5.48～5.92） | 2.43（2.39～2.46） |
| 同上 | Save Copy | 2.96（2.95～3.14） | 1.25（1.25～1.29） |
| 同上 | Open | 2.40（2.39～2.57） | 0.85（0.83～0.87） |
| 1M / edited | Save | 5.71（5.69～6.76） | 2.72（2.68～3.25） |
| 同上 | Save Copy | 3.80（3.39～3.93） | 1.56（1.50～1.86） |
| 同上 | Open | 2.82（2.42～2.89） | 0.83（0.81～1.10） |
| 18M / roundtrip | Save | 105.53（93.27～107.80） | 30.65（29.22～35.62） |
| 同上 | Save Copy | 84.54（74.71～96.09） | 29.98（29.57～31.23） |
| 同上 | Open | 49.34（47.66～55.18） | 15.77（14.93～15.88） |
| 18M / edited | Save | 95.44（91.10～101.05） | 31.19（31.17～42.00） |
| 同上 | Save Copy | 74.54（73.22～87.08） | 30.91（30.48～34.76） |
| 同上 | Open | 45.43（44.94～47.94） | 13.53（13.43～15.18） |

18M 实际保留编辑后的 Save 中位数减少约 **67%**。只读分配减少没有以保存 / 打开变慢为代价。

### 4.2 内存：MiB

以下同样为中位数；Peak WS 另外给出范围。Private / managed 是采样峰值中位数；累计分配是整个工作负载，不是常驻量。

| 样本 / 流程 | 版本 | Peak WS（范围） | Peak Private | Peak managed | 累计分配 |
| --- | --- | ---: | ---: | ---: | ---: |
| 1M / roundtrip | 前 | 1,164.96（1,144.27～1,183.41） | 1,148.11 | 1,106.57 | 3,089.54 |
| 同上 | 后 | 591.24（572.01～661.39） | 560.17 | 526.89 | 1,797.63 |
| 1M / edited | 前 | 1,153.59（1,137.73～1,182.96） | 1,133.45 | 1,085.80 | 3,423.44 |
| 同上 | 后 | 689.90（576.03～715.31） | 662.67 | 621.72 | 2,108.45 |
| 18M / roundtrip | 前 | 10,131.61（9,371.43～10,405.04） | 10,457.82 | 10,230.85 | 45,202.27 |
| 同上 | 后 | 813.39（762.40～1,007.07） | 786.88 | 736.69 | 22,043.38 |
| 18M / edited | 前 | 11,883.11（10,343.10～13,217.98） | 12,273.14 | 11,636.12 | 46,638.53 |
| 同上 | 后 | 1,173.89（976.94～1,195.52） | 1,134.57 | 1,072.15 | 23,199.77 |

18M edited 的 Peak WS 中位数为 **11.60 GiB → 1.15 GiB，约减少 90%**。它只代表本文隔离的导入 / 编辑 / 持久化流程，不包含完整 WPF / Worker 会话。

在全部 28 次成功运行（含下节补测）中，退出 owner 并退栈后 Project 的 WeakReference 均为 false。优化版主流程 post-GC managed：1M 约 69～85 MiB，18M 约 11～46 MiB；不是 0，而且某些情况下高于旧版 post-GC 的值。

已确认 pack 解码 / writer 使用 `ArrayPool` 并归还缓冲；较少 GC 压力可能让共享池保留更多可复用数组，这可解释该方向，但本阶段未对这些剩余字节逐条采集 heap 持有链，**不能把全部差额都归因于池，也不能据此宣称所有运行时 owner 已释放**。已确认的是全量编辑索引不再由只读过程生成、Project 弱引用释放；WPF 和其他全局 cache 的完整释放验证仍属于后续阶段。没有为了把 post-GC 数字压低而清空缓存或在产品中主动 GC。

### 4.3 无编辑、无历史的直接 Save：每版每样本补测 1 次

这是独立工作负载，不混入三轮主流程统计。为避免重复耗费大量旧版测试时间，仅作单次覆盖，不由它推导方差或长尾。

| 样本 | Save 前→后，秒 | Peak WS 前→后，MiB |
| --- | ---: | ---: |
| 1M | 6.15 → 2.94 | 701.04 → 342.19 |
| 18M | 87.47 → 28.30 | 8,984.12 → 724.50 |

### 4.4 Save 时间拆分：18M edited，3 次中位数

| 子阶段 | 前，秒 | 后，秒 |
| --- | ---: | ---: |
| 正式 ID / SupportedProject 等 preflight | 42.07 | 12.22 |
| 内容 / pack 构建 | 5.79 | 4.52 |
| ZIP 写入 | 0.72 | 0.76 |
| 完整重开自校验 | 46.30 | 13.95 |
| 原子发布与清理 | 0.06 | 0.10 |

每个子阶段各自取中位数，因此不能把该表简单相加当作整次 Save 中位数。主要收益来自 preflight 与自校验，不是删掉 ZIP 或文件 I/O。

### 4.5 变化、噪声与未证明的性能

- 所有成对的 Save / Copy / Open 主测均更快；没有观察到这些改动路径的时间退化。
- 18M edited 的 import 中位数是 13.65→14.89 秒（约 +9%），而 roundtrip 的同一 import 是 16.12→14.31 秒。Import 本身未改，且导入发生在两种工作流分叉前；这种相反变化不足以证明引入了导入退化或优化。仍保留全部数据，不把它忽略或计入本轮加速收益。
- ZIP / 发布的亚秒波动也保留；没有证据把它归为新只读接口造成的回归。3 次不能用于可靠 p95 / p99。
- 探针保留了 60k Note 首次 / 重复 Undo 的命令 checkpoint，但计时包含很短的诊断采样，不据亚毫秒 / 毫秒差异评价 UI 手感。三种钢琴卷帘的可视收敛、WPF lifetime 仍需阶段 2 的专门验证和验收 A。
- 首次基线设施试跑曾因 probe 引用缓存未携带 `Google.Protobuf` 运行依赖失败。已改为独立目录、`CopyLocalLockFileAssemblies=true`、probe `Rebuild` 和依赖预检，成功矩阵重新完整运行；该失败及旧临时探针结果不进入以上统计。

## 5. 正确性与回归

本轮新增：

- `PureMidiReadOnlyValuesTests`：三类记录 0 / 1 / 1M 全量扫描额外分配不随记录数增长；base / overlay / replacement / tombstone / clear / splice；隐藏与非法值可见；冻结根可重复读取；取消；retained facade 编辑及恢复；只读后 ID 查找仍走原索引；未提交 batch 拒绝。
- `PureMidiReadOnlyPersistenceTests`：70k Note + 70k Channel + Opaque 的无编辑 / 有编辑往返；重复保存字节确定性；65,537 页边界的 merged pack 与旧 mutable 写入参考逐字节一致；隐藏位置的跨类型重复 ID 仍拒绝；预检取消不改目标文件 / metadata / 索引。

### 5.1 构建与公共门

运行 `eng/MemoryStage1Probe/Run-Regression.ps1 -ResultsDirectory .tmp/memory-stage1/regression-01`；内部均为 Release `dotnet test --no-restore`，构建依赖后运行。结果如下：

| 套件 | 通过 | 失败 | TRX notExecuted |
| --- | ---: | ---: | ---: |
| Midora.Application.Tests | 1,051 | 0 | 0 |
| Midora.Compiler.Tests | 437 | 0 | 0 |
| Midora.Persistence.Tests | 111 | 0 | 0 |
| Midora.MidiExport.Tests | 43 | 0 | 0 |
| Midora.Playback.Tests | 117 | 0 | 0 |
| Midora.AudioRender.Tests | 37 | 0 | 0 |
| Midora.Desktop.Presentation.Tests | 417 | 0 | 0 |
| Midora.Desktop.Tests | 384 | 0 | 0 |
| **合计** | **2,597** | **0** | **0** |

其中本轮新用例为只读 API **13** 个、持久化 **7** 个。两版 Application、探针及当前 Desktop 的 Release 构建均成功；未通过发布脚本构建 `dist`。

TRX 的 notExecuted=0 **不等于所有 opt-in 大样本已启用**。部分既有测试在未设置样本变量时直接返回：如专用 WPF memory probe、大型 generation / selection failure reproduction、百万 Tempo 等。本阶段没有启动这些专项负载，也未运行 BASS 真实音色库 / 设备压力测试。普通 STA / WPF 控件、命令、选择、渲染回归已随 Desktop 两套件执行；不是人工视觉验收或完整 WPF 内存调查。

### 5.2 真正启用的 40 万音符读取测试

单独将 `MIDORA_REAL_SELECTION_READ_MIDI` 设为已授权 9KX2 样本，运行 `RealMidiSelectionReadPerformanceTests`，测试后恢复该进程环境设置。实际执行 1 个测试，通过：

- Track：`MIDI Out #23`；owner 内 1,824,636 Note。
- 范围 `[168816, 193536)` / key 0～127，取 400,000 个实际 Note。
- Properties 首次 **1.302 s**，重复 **1.244 s**，Selection metrics **1.333 s**。
- 各次累计分配约 459～520 MiB，读取结束时 managed 约 207～302 MiB。不能把累计分配说成常驻。
- 断言 400,000 对象 / 5 个属性（含 NoteOff velocity）、范围结果与原 compressed IDs 均正确。

它证明当前选择读取路径能真实运行；没有同口径旧版 UI 端到端对照，因此不声称该读取变快，也不据此替代阶段 2 的屏幕更新 / lifetime 测试。

### 5.3 全量只读计数与包内容

- 优化版 14 次成功进程运行共核对 **482 个 census checkpoint**；三类源记录的可编辑索引 / retained facade 新增长均为 0。不是在保存后清空表来制造结果。
- 修改前 18M roundtrip 的 Note 三表各达到 17,999,999 项；edited 因既有精确碰撞规则，移动后剩 17,992,130 Note，三表各达到该值。两版均得到相同 survivor 结果，没有新增丢音符行为。
- 三轮主流程的 Save / Copy，以及两组纯 Save，共 **26 对包**通过 `Compare-Content.ps1`：1M 每包 74 个比较 entry，18M 每包 101 个比较 entry。
- Project / Track / content pack / presentation entry 解压内容 SHA-256 相同；metadata 仅排除创建 / 修改时间和自动会话时长，其他属性相同。包含这些可变字段 checksum 的 manifest 不作跨运行字节比较，其严格性由每次自校验和既有 golden / fault tests 验证。
- 小型确定时间 fixture 额外比较了**整个 ZIP 文件字节相同**；edited merged pack 又与旧 mutable 枚举参考 writer 逐字节对照相同。两种证据不混淆。
- 隐藏位置重复 ID、跨记录类型重复、取消、旧目标保护、保存故障、Format 1/2 迁移、Format 3 / presentation、Full / Incremental、canonical / MIDI 排序等公共门全部通过。

### 5.4 可复现证据

追踪到仓库的工具：`eng/MemoryStage1Probe/`，包括构建说明、`Run-Workloads.ps1`、`Summarize-Results.ps1`、`Compare-Content.ps1`、`Run-Regression.ps1`。四个 PowerShell 脚本均解析通过，全部已实际执行；矩阵 runner 在纯 Save 补测中验证正常启动 / 退出。其超时 / 低磁盘保护分支未主动故障触发，不声明故障演练已通过。

未进入 Git 的本地证据均在 `.tmp/memory-stage1/`：

| 路径 | 内容 |
| --- | --- |
| `head-baseline/`、`before-probe/`、`after-probe/` | 精确 HEAD 源码对照与隔离二进制；未混用历史 dist |
| `<1m|18m>-<roundtrip|edited>-<before|after>-<1..3>.log` 与同名目录 | 原始 checkpoint、每 100 ms CSV、Save / Copy 产物 |
| `summary.json` | 主流程逐次值和中位数 / 范围；由追踪的统计脚本生成 |
| `no-edit/` | 独立无编辑 Save 的 4 次输出及 summary |
| `content-comparisons.json` | 26 对正式内容比较结果 |
| `regression-01/*.trx` | 八套公共回归原始记录 |
| `selection-read-01/selection-read.trx` | 真实 400k 选择读取输出与断言 |

每个 log 首部记录样本及 DLL SHA-256。核心 Domain 对照 DLL：前 `88fe0b5335041f77093f819c6805b13ad21d67fc88f035ab533fcd33d84ffe7b`，后 `4574423da6269fcd342d010156a3049bfd9aa81cbf68dc3b5917e399b5136e4c`；Persistence：前 `66cf32f403d93748eb2e4e0e49e4e3568882dc218b0ee62a08502042753afd96`，后 `a799651fc3ea693aac4325939bd43c5822d3430f606fd0603cec43af0037b272`。这些标识对应本次测量的二进制，不是未来任意重建的承诺。

## 6. 剩余风险与后续边界

1. M02～M05 的实际 WPF Workspace / Onion 持有链、最近 Surface 引用和完成订阅，本阶段未修复；不能据此宣布 11 GiB 相关问题全部解决。
2. M06～M13 的计划 / payload / builder / sparse ID / Logical 展开 / History 总预算仍按原六阶段顺序推进。Sparse ID 的 `StableIdSetV1` 放大尤其不属于此次修复。
3. 手动或遗留编辑路径显式要求可变 facade 时仍可能登记对象；这不等于全量只读回归。若未来消费者又使用 mutable 全枚举，新的计数回归必须阻止它进入保存 / 校验路径。
4. 本次只冻结 M01 的测量入口和操作覆盖索引；不是所有 F01～F10、三种 UI 的所有大型组合均已跑完。后续阶段仍需依次完成各自工程门。
5. 不据此制定最低 RAM 配置、不保证所有机器按相同比例加速、不调整文件兼容或音频语义。

## 7. 用户验收与工作区

按已同意的协作方式，阶段 1 的人工检查合并到阶段 2 后的**验收 A**；本轮不要求重测全产品。临时试用时如出现保存 / 打开内容差异或编辑退化，应作为阻塞反馈。

只新增 / 修改上述产品代码、测试、探针和文档。不更新 SRS / 产品版本 / Project Format，不提交、不推送、不生成 `dist`，不自动启动阶段 2。
