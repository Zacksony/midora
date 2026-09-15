# 阶段 6：全链路、关闭释放与交叉操作验证

日期：2026-09-09。当前状态：**四项内存清理完成；随后获准的独立 Pure MIDI 正确性修复已通过 3157 / 3157 公共回归及当前 WPF 大会话复验，编译阻塞解除。2026-09-09 用户确认 MEM-C01～C07 全部验收通过。**

后续收尾：正文的 Int64 暂缓与 Int32 限制只代表阶段 6 当时决定，已被[后续独立实施](Midora-Int64-Diagnostics-and-Bounded-Readme-Verification.md)取代；相关代码已提交、推送，用户确认人工大体全部通过。F4 / Shift+F4 仍明确暂缓。成功 Logical canonical 的进一步内存优化排为下一项，见 [当前台账](Midora-Pre-Expansion-Closeout-and-Next-Step-2026-09-09.md)。

本文保留原阶段 6 批次的 3,140 通过 / 2 失败和当时 Compiler 未改的实测证据；不要把它们误读为当前仍失败。后续代码、范围事件 oracle、9KX2 三轮性能代价及当前构建 WPF 结果统一见 [独立修复验证报告](Midora-Pure-MIDI-Range-Correctness-Repair-Validation-2026-09-09.md)。用户在验收通过后另行授权提交、推送；不生成 `dist`。

本轮依据 [六阶段计划](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md) §11。开始时 HEAD 为 `e12b6c254a02624bb14b999154ebac80f5492bd9`，工作区干净；阶段 4/5 已由用户另行指令提交、推送。本轮不提交、推送、不生成 `dist`，不使用 computer-use。

## 1. 范围与已确认决定

- 保持全部音乐、编辑、选择、历史和文件格式契约；不主动调整音频算法、不缩小缓存精度、不裁剪历史、不在正常路径强制 GC。
- 全部 O01～O40 的适用范围、真实入口、证据层级另见 [逐项审计](Midora-Memory-Stage6-Operation-Coverage-Audit.md)。存在测试入口不等于任意规模和每个真实鼠标手势均已跑过。
- 用户本轮明确决定暂缓 Int64 诊断总数及分页 UI：保留当前 `Int32` 总数边界。不得截断、抽样替代正式诊断，或将这个边界写成已解决。设计记录见 [诊断边界](Midora-Memory-Stage6-Diagnostics-Design.md)。
- 实现 trace 和并发/所有权边界见 [阶段设计](Midora-Memory-Stage6-Requirement-Trace-and-Design.md)、[异常清理设计](Midora-Memory-Stage6-Cleanup-Design.md)。不改 SRS。

### 1.1 本轮生产修改

| 文件 | 根因及改动 |
| --- | --- |
| [ProjectCompilationSession.cs](../src/midora-core/Midora.Playback/ProjectCompilationSession.cs) | 取消回调或 worker 故障曾跳过后续释放，且 disposed 标志阻止再次清理；保持既有等待顺序，独立清理全部 owner 并释放结果/镜像/任务引用，最终报告原异常或完整聚合 |
| [ProjectDocumentSession.cs](../src/midora-core/Midora.Application/ProjectDocumentSession.cs) | 先脱离 History 列表，再按原顺序逐项释放；某条历史释放失败不阻止其他项，不复制整条历史或裁剪有效 Undo |
| [BoundedEditResourceLease.cs](../src/midora-core/Midora.Application/BoundedEditResourceLease.cs) | 临时/已发布资源容器转交清理所有权后，保持逆序逐项尝试；一个 Dispose 错误不遗留其余资源，已移交 source 的资源不提前释放 |
| [DesktopSessionController.cs](../src/midora-desktop/Midora.Desktop/DesktopSessionController.cs) | 关闭/替换工程时某视图或 backend 失败不再跳过后续独立 owner；新 Project 一旦已被当前会话接管，不因后续激活错误被 caller finally 误销毁；prepared backend 也在异常关闭后释放 |

正常成功路径与音乐/文件语义不变。专项证明逐项清理、所有权、被跟踪资源及异常保留；不证明任意线程并发关闭、不可取消的 worker、OOM/StackOverflow 或下层 native Dispose 内部失败仍能释放全部 handle。多处错误均保留，但不承诺跨激活与 finally 阶段严格按实际发生时间排序。

## 2. 测量口径与安全措施

所有压力进程通过私有 Job 启动，首次执行前即施加 **8 GiB 私有提交硬上限**，另以 100 ms 采样监控树 Private/WS 及 **2 GiB 系统可用内存余量**；只结束测试自己的进程树。重型运行与构建串行；不实际耗尽系统，不修改源 MIDI。8 GiB 是测试停止线，不是新增产品上限。

- 环境：Release/x64、.NET SDK 10.0.400、runtime 10.0.11。旧源码构建冻结到 `.tmp/memory-stage6/baseline`，包含文件 SHA-256；后续候选另存，不覆盖基线。
- 字节统一分别报告 B/MiB/GiB；累计 allocation 不能当作常驻；Working Set 不能当作全部提交；`GC.GetGCMemoryInfo` 是最近一次 GC 的数据，不应与任意时刻的 Private 直接相减归因。
- 纯编译探针在非内联场景方法返回后才检查弱引用，排除当前栈、闭包和局部变量人为保活。三轮连续场景只有弱引用与标量结果逃逸。
- `VirtualQuery` 只枚举当前测试进程的区域元数据，分 MEM_PRIVATE/MEM_MAPPED/MEM_IMAGE 已提交及 reserved。它不是堆对象归属分析，不能从“Private 减 GC committed”推导某个原生库泄漏。
- 真实 WPF 探针创建正式 App/MainWindow/模板/Surface 并离屏绘制，使用真实 Desktop 会话、自然后台编译、History、Save/Copy/Open；不调用窗口 Show 或注入屏幕输入。不包含物理音频设备、听感、显示器最终像素和人的操作延迟，不能替代人工 C。
- 受控 GC/LOH compact 只用于诊断阶段，生产代码没有新增 GC 调用。

## 3. 已完成的编译持有链归因

工具：`eng/MemoryStage6Probe/`。固定每 Trigger 4 个 SubVoice、每 Voice 8 个 Template Note、4 次 Loop；每轮 Full → 修改一个 Velocity → Incremental → 独立 Full oracle。保留 first/current/oracle 三份结果直到比较结束，这是探针有意施加的同时存活上界，不能当作普通播放常驻。

全部事件及 Source、diagnostic 序列逐项相等，Full/Incremental fingerprint 相等；正式结果计数断言独立计算。每种规模连续三轮，关闭所有 owner 并退出场景调用栈后，Project/Segment/Instrument/Compiler/三份结果共 7 类弱引用均为 0。

| Trigger / 每轮 canonical | 三轮 Full ms | 三轮 Incremental ms | Job 峰 Private / WS |
| --- | --- | --- | --- |
| 1,000 / 296,044 events，128,000 NoteOn | 2,219.55 / 974.74 / 1,150.82 | 1,030.40 / 933.36 / 981.84 | 912,617,472 / 734,846,976 B |
| 10,000 / 2,960,044 events，1,280,000 NoteOn | 14,953.28 / 10,462.54 / 11,541.56 | 10,486.56 / 9,933.39 / 10,145.16 | 5,991,759,872 / 5,664,489,472 B |

这是同一实现的新进程首轮与连续热轮，不是阶段 5 前后性能对照，不能据此宣称加速或减速比例。三轮基线 `.tmp/memory-stage6/retention-1000-r1`、`retention-10000-r1` 均 exit 0、未触发 guard。

### 3.1 关闭后的大数字从哪里来

10,000 Trigger 三轮关闭、受控 GC 后：

| 轮次 | Private B | 托管存活估算 B | GC committed B | 关键弱引用 |
| --- | ---: | ---: | ---: | --- |
| 1 | 3,934,314,496 | 8,448,104 | 3,876,077,568 | 0 / 7 |
| 2 | 3,909,410,816 | 8,464,864 | 3,845,402,624 | 0 / 7 |
| 3 | 4,572,614,656 | 8,459,592 | 4,501,180,416 | 0 / 7 |

**已确认：**在本次可复现样本中，关闭后约 3.6～4.3 GiB 的 Private 大部分对应 GC 保留的已提交区域，不是表中 Project/canonical 等对象仍活着。第三轮 VirtualQuery 的 MEM_PRIVATE committed 为 4,561,154,048 B，与进程 Private 量级一致；约 68 MiB 的 Private/GC 差额仍包含未精分 CLR/JIT/线程栈等成本，不能强行归给某个 owner。

**结论边界：**这解释了本次重复样本，不能从新记录反推阶段 5 单次旧采样的每一个字节，也不能证明任意 WPF/native 对象不存在泄漏。GC committed 高水位是真实系统提交负担，只是“尚未归还操作系统”不同于“仍由项目对象持有”。没有证据支持为降低显示数字而在正常关闭时强制 GC、裁剪历史或丢弃热缓存，本轮不采用这些手段。

## 4. 全链路 WPF、交叉命令及故障验证

### 4.1 阶段 5 基线上的真实 WPF 连续链

`eng/MemoryStage6WpfProbe` schema 1；先小夹具，再 500k 梯度，再完整 9KX2。三轮均 exit 0，未触发内存守护。样本为 `D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`；定位 `MIDI Out #23`，以 Note start 在 `[168960, Segment End)` 为选区。不是将“与范围相交”混作同一选区，也不改源文件。

| 实际选中 / 粘贴后 Logical Note | 连续新增及逐次 Undo | View cycle | Job 峰 Private / WS B |
| --- | ---: | ---: | --- |
| 小夹具 20 / 20 | 2 | 2 | 174,403,584 / 266,493,952 |
| 500,000 / 464,942 | 2 | 2 | 2,103,402,496 / 2,180,661,248 |
| 1,382,908 / 903,352 | 5 | 3 | 2,687,373,312 / 2,710,200,320 |

完整场景沿用默认新建空 Event Instrument、Overlap Reject：479,556 条 exact collision 按原规则未加入；后台得到 **68,502 条完整诊断**，保留原 Pure MIDI 成功结果。没有为“内存通过”修改成忽略重叠或清空诊断。对诊断只比较最多 257 个固定 ordinal 的完整内容、精确总数及其他结果字段；这是一条大链抽样 oracle，逐项所有诊断正确性由独立紧凑诊断/Compiler 测试负责。

连续链实际执行：导入/编译 → 复制 → 预取消及首次非零 Planning 进度后取消（目标零发布）→ 正式跨类型粘贴 → Direct/Logical/Instrument/Conductor/Arrangement/Onion/All Tracks Raw/Compiled → 有界对象列表读行 → 5 次新增与后台收敛 → 5 次 Undo 与后台收敛 → Paste Undo/Redo → 替换 Clipboard → 保留 History/视图 Save/Save Copy → 关闭 → 两份文件分别重开校验并关闭。

这是实际模板和数据消费者，并非逐屏 GUI 自动点击。离屏对象列表显隐需显式激活 provider，只读最多 32 行；不测真实 HWND 的 IsVisible 调度器。每次离屏 Render 不等待全部 raster 完成，因此其时间也**不是**“全部正确音符已在显示器出现”的延迟。新/旧区域收敛和最终像素另有 Surface 自动测试，真实手感保留人工 C。新建 Instrument 的 SubVoice 是空的，本场景不能作为百万 SubVoice 渲染证据。

完整基线链的一轮时间（n=1，不给加速/退化比例）：

| 动作 | 墙钟 |
| --- | --- |
| Import | 21.654 s |
| Copy | 3.327 s |
| 分页 Paste 准备＋发布 | 5.623 s |
| Paste 后后台编译收敛 | 15.576 s |
| 5 次新增同步提交 | 0.456 / 0.168 / 0.181 / 0.182 / 0.164 s |
| 对应后台编译收敛 | 16.999 / 12.655 / 12.045 / 11.838 / 12.314 s |
| 5 次 Undo 后后台编译收敛 | 12.305 / 12.231 / 15.819 / 14.684 / 15.207 s |
| Save / Save Copy | 39.029 / 33.118 s |
| 两次重新打开 | 24.670 / 23.828 s |

每次后台编译累计 allocation 约 10.58～10.71 GB，**不是常驻**；整条链峰 Private 约 2.50 GiB，而非逐修改累积到 17 GB。后台十余秒和严格保存数十秒仍是真实时间成本，本轮仅做收口验证，不把它写成时间成本已经消失。

### 4.2 真实 WPF 关闭后

三次关闭后 MainWindow/Session 仍存活；Project、Document、Compilation、各 Workspace、Surface、Snapshot、Clipboard、ObjectListSource、canonical 等 11 类被跟踪对象，受控 GC 后弱引用均为 0。

完整 baseline：

| 关闭点 | live managed B | GC committed B | Private B | 口径 |
| --- | ---: | ---: | ---: | --- |
| 导入场景退出 | 11,326,640 | 621,092,864 | 794,726,400 | 普通受控 GC |
| 同上 | 11,279,504 | 586,289,152 | 704,012,288 | 再做诊断 LOH compact |
| Save 重开退出 | 61,605,984 | 591,151,104 | 711,049,216 | 诊断 LOH compact 后 |
| Save Copy 重开退出 | 61,474,728 | 654,057,472 | 764,469,248 | 诊断 LOH compact 后 |

最后两次在 compact 前，Private/GC committed 差额约 456/476 MB；compact 后约 120/110 MB。**只能确认观测差额缩小**，不能把变化全部归因于 native 泄漏或某个 WPF 缓存；未做逐 native heap 的所有者精分。基线 schema 1 未记录新加的 raster/PreparationStorage gauges，不补造历史数据。候选 schema 2 已额外记录这些实际计账及关闭归零断言。

基线原始记录：`.tmp/memory-stage6/wpf-small-v3`、`wpf-500k`、`wpf-full` 及各自 `-guard`。每条大链各一轮，不能声称统计分布、整机最低内存或任意长会话保证。

### 4.3 候选回归

异常清理候选的专项与 WPF 连续链已完成；完整测试集的状态另记，不把部分通过写成整体通过。

先冻结异常清理候选到 `.tmp/memory-stage6/candidate`，schema 2 小夹具与 100 次 view cycle 均 exit 0：

| 场景 | Job 峰 Private / WS B | 结果 |
| --- | --- | --- |
| 20 Note / 2 次编辑 / 2 cycles | 178,552,832 / 267,980,800 | 3 次关闭后六项 raster gauges 为 0，11 类弱引用为 0；Save/Copy 重开结构计数一致 |
| 20 Note / 2 次编辑 / 100 cycles | 184,115,200 / 297,811,968 | 长循环完成，详细 gauges 和退出断言见独立 WPF 记录 |

长循环在第 25 / 50 / 75 / 99 轮的同点 Raster 均为 37,172,320 B / 160 entries，Private 分别为 168,235,008 / 164,794,368 / 164,700,160 / 164,913,152 B；这些观测点没有随轮数线性增长。这是同一小工程的 100 次视图循环及三次 Project 关闭，不是 100 次大工程重开。

随后两次完整 9KX2 候选连续链均 exit 0、未触发守护：

| 场景 | Job 峰 Private / WS B | Raster 阶段采样峰 B |
| --- | --- | ---: |
| `wpf-candidate-full-r1` | 2,866,278,400 / 2,884,136,960 | 104,186,608 |
| `wpf-candidate-full-r2` | 2,876,760,064 / 2,938,339,328 | 103,248,608 |

两轮均为 1,382,908 源选区 → 903,352 Logical Notes；原 Pure 数据 14 Roots / 40 Tracks / 40 Segments / 17,999,999 Notes / 3,484 Channel events 的重开计数一致，68,502 条诊断完整保留。PreparationStorage 阶段采样最高均为 39,033,912 B，所见同点全部为 Baseline，Active/Cached 为 0；这不是在途任务内部的连续峰值。

| 动作 | r1 | r2 |
| --- | ---: | ---: |
| Import | 21.529 s | 21.935 s |
| Copy | 3.145 s | 3.076 s |
| Paste 准备＋发布 | 5.941 s | 6.008 s |
| Paste 后后台收敛 | 13.547 s | 13.586 s |
| 5 次新增同步提交范围 | 0.137～0.425 s | 0.157～0.426 s |
| 5 次新增后台收敛范围 | 12.149～13.221 s | 11.927～13.268 s |
| 5 次 Undo 后台收敛范围 | 12.028～12.495 s | 11.893～12.376 s |
| Save / Save Copy | 36.946 / 32.630 s | 36.934 / 32.959 s |
| 两次重新打开 | 23.749 / 23.808 s | 23.540 / 23.393 s |

两轮共六次关闭后，完成缓存字节/项数、在途数、订阅数、待回调数、运行数六项均归零；受控 GC 后 11 类跟踪弱引用均为 0。原导入关闭后的 managed live 为 11.47～11.51 MB，两种重开退出约 61.83～62.03 MB；Private 仍可保有 0.84～1.14 GB。不能把这个差额都解释成 native 泄漏，也不以强制 GC 改写生产行为。

证据 `.tmp/memory-stage6/wpf-candidate-{small,long,full-r1,full-r2}`，详见 [WPF 完整结果及口径](../eng/MemoryStage6WpfProbe/RESULTS-2026-09-09.md) §6～8。候选 Private 峰 **2.669～2.679 GiB**，实际高于单次 baseline 的 2.503 GiB；schema 2 新增计账且 baseline 只有一轮，不能宣称统计提速或正常链峰值下降。四项异常释放修复的收益由故障反证归因，不能把此前全部内存改造成果归给本轮四项修改。

这些是**异常清理候选**结果；Compiler 未修改。大链的 source census 和诊断身份抽样门不能覆盖下面独立发现的 canonical 范围差异，不能据大链通过宣称整个编译契约通过。

新增连续消费测试首先暴露夹具错用 Role：Pure MIDI 普通音符使用 `DirectMidi`，不是 Logical 的 `NoteOn` role。修正为按 wire message 找 NoteOn 后，保留更严格的 tick/port/channel/key/velocity、DirectMidi role 及 Source 断言，不改变产品。

随后，保存重开由普通内容转分页内容的 oracle 检出真正跨布局差异：Root 硬边界的 StableOrder/SemanticGroup 不一致，并进一步核实到分页编译未正确覆盖提前 range end 的 Root 清理。正式字段参与排序/状态分组，不能在测试中删掉字段以通过；当前两项失败保留，尚未实施 Compiler 修复。

异常清理的独立验证已完成：候选 **31/31**；把同一套测试的对应生产 DLL 单独替换为冻结基线后，**2 通过 / 29 失败 / 0 跳过**。失败明确落在后续 owner 未释放、异常未聚合、重复释放和误销毁已接管 Project，并非程序集加载失败；两个已转交资源的控制用例旧版也通过。详见 [故障清理反证](Midora-Memory-Stage6-Cleanup-Validation.md)。该反证不覆盖后续独立发现的 Pure MIDI 编译缺陷。

独立范围反证 `PureMidiRangeLayoutEquivalenceTests.EarlyConsumerEndClosesSharedRootNotesAndResetsStateInBothSourceLayouts` 已实际运行：同一小工程、同一 `[300,600)` 上，普通布局返回并声明 11 events / 1 NoteOn，分页布局声明 12 events / 2 NoteOn、实际查询仅 6 events；明确缺少 tick600 的两条 NoteOff 和四条终点状态清理。该测试与前面的保存重开对照均保留失败，不标 Skip、不删除断言。证据 `.tmp/memory-stage6/compiler-range-proof-r1/results/compiler-range-proof-r1.trx`；927 ms，guard 无中止。

因此，**在本节原批次结束时，阶段 6 整体正确性门被阻塞**。独立问题、精确索引所需范围、代价及当时的候选方案见 [Pure MIDI 范围编译阻塞项](Midora-Memory-Stage6-Pure-MIDI-Range-Blocker-2026-09-09.md)。gate-active 查询不能代表跨轨道同音高的 FIFO 活动来源。当时未擅自修改 Compiler；用户随后授权独立修复。最新完整 oracle、公共回归和边界索引代价见本文开头链接，不删除原失败记录或修改 Source/可听语义来迎合测试。

### 4.4 最终公共回归

使用 `eng/MemoryStage6Probe/Run-Regression.ps1` 串行完成 11 个项目的 Release 构建与测试，`--no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false`，每个项目独立 OutDir、guard、TRX。构建日志无 warning/error；测试中的两项失败仍使总命令 **exit 1**，不是成功退出或可接受跳过。

| 测试集 | 总数 | 通过 | 失败 | 未执行 |
| --- | ---: | ---: | ---: | ---: |
| Common | 81 | 81 | 0 | 0 |
| MIDI | 29 | 29 | 0 | 0 |
| Application | 1,139 | 1,138 | 1 | 0 |
| Compiler | 456 | 455 | 1 | 0 |
| Persistence | 228 | 228 | 0 | 0 |
| MIDI Export | 47 | 47 | 0 | 0 |
| Playback | 143 | 143 | 0 | 0 |
| Audio Render | 37 | 37 | 0 | 0 |
| Desktop.Presentation | 445 | 445 | 0 | 0 |
| Desktop | 427 | 427 | 0 | 0 |
| BASS 托管 gate 子集 | 110 | 110 | 0 | 0 |
| **合计** | **3,142** | **3,140** | **2** | **0** |

两项失败均为本轮新增的反证，不是本轮生产清理引入：

1. `MemoryStage6ConsumerChainTests.MixedEditsReachColdWarmSeekPlansMidiExportAndEditedPackageRoundTrip`：第一次修改态文件重开后的 canonical 第 60 项起 metadata 不相等；前面的三采样率、260 范围缓存链及原工程 MIDI 已实际执行。重开后的 range/MIDI/plan、第二份文件 Open 和该测试尾部 Undo/Redo **未执行**；不能用其他链的通过代填本例后半段。
2. `PureMidiRangeLayoutEquivalenceTests.EarlyConsumerEndClosesSharedRootNotesAndResetsStateInBothSourceLayouts`：分页源提前终点缺少两条预期 NoteOff；详细六条边界缺失和计数反证见 §4.3/独立报告。

`Midora.dll`、Application、Playback、Compiler、Domain、Desktop.Presentation 的最终回归 DLL SHA-256 均与 WPF 冻结 candidate 相同。Compiler SHA-256 同时与阶段 5 baseline 相同：`DEAD8792D25262633BCA14D5712AB03788250C084B453C390244D375B0317A0C`；没有通过偷偷改 Compiler 或仅换旧测试 DLL 解释差异。

本次 11 个 guard 都没有触发停止，进程树峰 Private 为 2,323,988,480 B，WS 为 2,568,728,576 B。原始证据为 `.tmp/memory-stage6/regression-final/regression-summary.json`、各套 `*.trx` 和 `guard/{child.stdout.log,child.stderr.log,guard.jsonl,guard-result.json}`。全部运行已结束，没有遗留进行中的压力任务。

表中的未执行 0 是 TRX 口径：若 opt-in Fact 未提供样本而自行 return，它仍显示 Passed，**不能据此宣称对应真实 MIDI 已运行**；真实 9KX2 证据限定为 §4.1～4.3 的独立 WPF 流程。BASS 仅为 10 个明确托管测试类的 110 项，不执行原生 fixture、真实 SoundFont、物理设备、回调 deadline 或实际听感。以上盲区不写成已通过。

## 5. M01～M13 收口与限制

以下区分已修缺陷、必要成本和明确延期；阶段 6 新增门的最终计数见 §4.3，不能把仅有历史证据的行冒充本轮重新跑过全部历史大样本。

| 问题 | 处置 / 对应证据 | 不能据此承诺的内容 |
| --- | --- | --- |
| M01：Pure MIDI 只读枚举全量三表 | 阶段 1 改值游标/页；本轮真实 18M 导入、跨类型 Copy、修改态 Save/Copy/两次 Open 同时持有 WPF/History，未复现全量 facade 保活 | 原始 MIDI 自身及必要页目录不是零成本 |
| M02：共享 Settings 保活 Workspace/Project | 阶段 2 所有权修复；本轮 WPF 关闭后被跟踪 Workspace/Project 弱引用归零；另补关闭/切换异常仍退役独立 owner | 弱引用仅证明被跟踪对象，不是全部 CLR/native heap 的逐对象证明 |
| M03：最近命令 Surface 强引用 | 阶段 2 清理；本轮实际 MainWindow 仍存活，旧 Surface/Snapshot 弱引用归零 | 不代表关闭瞬间 WS 必须下降 |
| M04：禁用 Onion 保留旧来源 | 阶段 2 detach/来源释放；本轮 Onion、All Tracks Raw/Compiled 与普通视图往返和关闭链 | 当前启用的只读投影与合法共享源必须继续存活 |
| M05：完成订阅无限追加 | 阶段 2 request/subscriber 去重与生命周期；原 60k request/200 次每类 Workspace 证据保留，候选 WPF 增加实际 raster gauges/关闭归零门 | 256 MiB 完成瓦片上限不包含整个源模型；baseline schema 1 没有该 gauges，不能补造 |
| M06：范围/采样计划无淘汰 | 阶段 3 共享存储按 byte/entry 预算及 active lease；本轮连续消费链的编辑失效、三采样率、260 范围驱逐/重访均在保存重开断言之前实际执行，故障关闭外部 reader 保留门独立通过 | 正在使用的 consumer lease 不得强制销毁；另发现分页范围正式输出不等价，见 §4.3，缓存寿命通过不代表范围编译正确 |
| M07：Opaque payload 漏计账 | 阶段 3 payload 字节准入、oversize bypass、共享身份计账；小型全链继续精确保留 opaque / 事件和 MIDI 输出 | 不把大 payload 降采样或删掉；正式 Source payload 本身仍有成本 |
| M08：Logical/SubVoice/Conductor 全量持久化中间表示 | 阶段 4 严格流式 writer/reader，descriptor/schema/损坏/取消门；本轮修改态真实 WPF Save/Copy/Open 补消费者并存 | 严格验证和文件 I/O 仍可能耗时数十秒；本轮未取消校验 |
| M09：小 Segment PageBuilder 绕过预算 | 阶段 3 整体 writer 预算与多 owner/page builder 计账，边界测试纳入公共回归 | 不承诺无限多小对象的所有元数据常数空间 |
| M10：稀疏 ID 位图放大 | 阶段 3 自适应 dense/sparse 及精确唯一性，极端 ID 测试纳入公共回归 | 不能通过重编号 Stable ID 或省略校验降内存 |
| M11：Logical/SubVoice 首次快照和目录 | 阶段 4 分页值根、共享空间目录/有界 facade；本轮约 138 万源 Note 转约 90 万 Logical，与视图/历史/保存共存 | 真实大链目标 SubVoice 为空，不冒充百万 SubVoice UI 压力；其普通/adopt 1M 证据属于阶段 4 |
| M12：Logical 编译展开 | 阶段 5 消除重复 Raw 数组、共享模板模式/紧凑诊断；本轮 1.28M NoteOn/2.96M events 三轮 Full/Incremental/Full 与关闭弱引用通过，GC committed 高水位已分离说明 | **必要 canonical 数组仍随正式输出增长，不是常量内存。** Int32 诊断溢出按用户决定暂缓，不改成删行/aggregate，也不宣称无上限编译 |
| M13：History/Clipboard 跨命令 | 阶段 5 共享持久值根、splice、Clipboard 依赖 lease；本轮 F10 多命令 Undo/Redo/新分支/替换 Clipboard/取消、WPF 长链，另补 History/临时资源任一 Dispose 抛错后继续清理其余项 | **有效历史的必要磁盘页及元数据仍随真实编辑量增长。** 不裁剪有效历史、不伪称全会话常数预算 |

因此，不能把本轮结果写成“任意千万音符/任意展开倍数/任意 SF2 最多 X GiB”。已测工作负载区间见 §3～4；物理音频与 SoundFont 并存的总提交、任意长期历史、未测极端展开仍不提供整机最低 RAM 结论。诊断 Int32 是明确延期，而不是已验证的合理成本。

## 6. 人工验收 C

2026-09-09，用户明确确认“MEM-C全部验收通过”，以下 **MEM-C01～C07 均记录为用户验收通过**，不是依据自动测试代填。§4.3 暴露的 Compiler 阻塞已由独立修复及完整回归解除；人工结论不替代范围/重开 oracle，也不代表超过 Int32 的诊断边界已修复。自动化负责内容/字节/计数/释放与失败原子性，人工验收负责连续使用的响应、选择、焦点、音色与图像及时性。保留原操作卡，供追溯此次验收范围。

| 编号 | 建议检查动作 | 重点 |
| --- | --- | --- |
| MEM-C01 | 在真实工程交替编辑三种音符、事件点、Properties、两三种常用批量工具 | 选择及焦点正确，操作衔接自然，无旧结果重新出现 |
| MEM-C02 | 累积几次操作后连续 Undo/Redo，Undo 后另做新操作，再替换 Clipboard；取消一次长操作后继续编辑 | 历史/选择恢复正确，新分支正确，取消不留下半次编辑 |
| MEM-C03 | 使用带 Loop、Pre-Roll、Mapping、共享状态的 Instrument，编辑后编译并播放/预览、从中途播放 | 音乐内容和听感不变；编辑同步时间与后台编译等待分开判断 |
| MEM-C04 | Onion 的 Previous/Next/手选/禁用往返，All Tracks Raw/Compiled 与普通视图切换，播放/跳转/跟随 | 只读来源、stale 提示、颜色、播放指针、快捷键仍正常 |
| MEM-C05 | 推荐复查 9KX2：`MIDI Out #23` 的 `[168960, 末尾)` → 新 Logical Segment `@168960, Length24576` 的 Tick0；小改数次，再 Save/Copy/Close/Open 并继续编辑 | 不再逐次增加到系统假死；原始内容及修改态重开正确。正式重叠规则造成复制前后对象计数减少不是数据丢失 |
| MEM-C06 | 大/小选区在新旧区域平移缩放，跨过 Segment 右界；移动及双边长度调整，切换 Velocity/Event Lane | 以前已验收的预览速度、边框、选择高亮、图像刷新不退化 |
| MEM-C07 | MIDI/Audio 导出；取消一个长任务后再次导出或编辑 | 进度/取消可用，输出正常，后续没有锁住或无响应；物理设备/实际 SoundFont 听感由此补验 |

资源观察请分别看 Private/提交与 Working Set。关闭后 GC committed/WS 未立即回落不单独判失败；若连续相同操作后的稳定平台持续抬高，或出现图像/选择/焦点问题，请记录操作序列与当时阶段。本轮 8 GiB 守护仅用于自动测试，不要求用户故意把机器推到内存上限。
