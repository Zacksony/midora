# 阶段 5 安全探针：测量记录

日期：2026-09-09。状态：**本轮安全探针测量已结束；不是人工WPF验收或阶段6长会话结论。** 阶段5整体实施/验收状态以主报告为准。

依据：[执行计划 §10](D:/Programing/midora/misc/Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md)、SRS §12.21/12.24/12.25。工具与复现命令见 [MemoryStage5Probe README](D:/Programing/midora/eng/MemoryStage5Probe/README.md)。未运行 computer-use、发布 `dist`、提交或推送。

## 1. 口径与版本

- 每组旧/新各一个独立 Release/x64 进程，.NET 10.0.11。一次进程内 5/10 个不同 revision 不是 5/10 次独立重复，不据此宣称中位数/p99 或稳定加速倍数。
- baseline 来自阶段 4 最终 Application.Tests 输出；冻结于 [baseline manifest](D:/Programing/midora/.tmp/memory-stage5/baseline/binary-snapshot.json)。Domain MVID `75f1345c-41c9-4377-9c3b-14ecec4384ae`；Compiler `1b16bdc4-e870-4efe-be62-0012775572f2`。
- 本节候选为 [candidate-v1 manifest](D:/Programing/midora/.tmp/memory-stage5/candidate-v1/binary-snapshot.json)，Domain `d46e171d-8ff1-4cc9-965f-08a8fc4dbb11`；Compiler `8493069a-7f8b-437d-a558-a25da210fff9`。经源码修改时间与逐DLL SHA复核，v1冻结于10:43，晚于M13 splice最后修改10:40，已包含该修复；与10:47公共回归输出及10:55当前源码输出hash相同。早期沟通中“v1不含M13最终splice”的时间判断错误，在此纠正。
- schema 2 digest 完整覆盖有序事件、来源、全部诊断、context、statistics、分配、SMF descriptor 和 Conductor。每次 revision 后的 hash 与 Raw census 不计入 compile 阶段计时，但进入总墙钟、累计分配和进程峰值。巨大真实诊断另用有限抽样，不做数亿条完整 digest。
- 没有 WPF、native audio、SoundFont、真实 UI 后台并发或保存/重开链路，不代表整机总内存、最低配置或阶段 6 长会话结论。普通阶段没有强制 GC；只有末尾诊断释放阶段执行。

## 2. 已验证的安全守护

外部父进程先暂停创建 child，再放入私有 Windows Job 后恢复；Job 对全后代设置 **8 GiB Private commit 硬上限**（约 8.59 GB）、kill-on-close。每 100 ms 另采样合计 WS/Private，默认保留系统 available RAM 2 GiB。守护只终止该 Job；不按名称结束用户进程。

低阈值自测 [guard-low-cap-selftest](D:/Programing/midora/.tmp/memory-stage5/runs/guard-low-cap-selftest/guard-result.json) 使用最多 128 MiB 人工递增分配，阈值 96 MiB。在 WS 101,457,920 bytes（约 96.76 MiB）、Private 79,486,976 bytes 时真实触发 `process-tree-working-set-limit`，关闭其两个 owned PID，exit 124。没有通过把机器推到 9 GB 来测试守护。WS 采样有约一个采样间隔的微量超调；Private commit 另受 Job 的同步硬限约束。

测量中的正式成功组均未触发守护。输出含 `guard.jsonl`、`guard-result.json`、`child.stdout.log`、`child.stderr.log`，中断/异常日志不覆盖。

## 3. M12：10k 默认新空 Instrument，失败诊断路径

夹具：10,000 Logical Note、单 SubVoice、模板无 Note、默认 Reject/SamePitch、不隔离、每 4 tick 一个 Trigger、gate 480、velocity 单音符修改 5 次。正式失败为 OverlapValidation；每次准确保留 **1 条 MIDORA1225 + 1,182,860 条 MIDORA2201**，不是把合法源丢弃或截断诊断后报成功。

原始结果：[旧](D:/Programing/midora/.tmp/memory-stage5/runs/baseline-v2-empty-10k-r5/result.json)、[候选 v1](D:/Programing/midora/.tmp/memory-stage5/runs/candidate-v1-empty-10k-r5/result.json)。逐阶段 6 份 exact digest 全等，末次 Incremental 与独立 Full oracle 全等。

| 指标 | baseline | candidate-v1 |
| --- | ---: | ---: |
| 初次 Full | 1,343.76 ms | 982.50 ms |
| 5 次增量编译范围 | 624.04～1,125.60 ms | 114.61～183.16 ms |
| 每次增量累计分配 | 395.19～396.30 MiB | 103.80～103.83 MiB |
| 外部采样 peak Private | 1,116.60 MiB | 492.26 MiB |
| 外部采样 peak WS | 1,141.05 MiB | 524.87 MiB |
| 最后诊断 GC 后 managed | 1.74 MiB | 1.77 MiB |
| 含全部仪器总墙钟 | 21.67 s | 23.28 s |
| 含全部仪器累计分配 | 2,974.53 MiB | 4,092.23 MiB |

最后两行不能省略：候选不再永久物化所有诊断，探针却每轮为完整 oracle 枚举它们，临时构造诊断对象增加了仪器工作。`full-oracle` 也包含两次完整 digest：旧 4,225.35 ms / 395.29 MiB，新 4,410.01 ms / 555.12 MiB。因此这里同时成立的是“正式 compile 阶段更快/更少分配”和“当前完整诊断枚举探针总分配更多”；不能把总墙钟说成已经加速，也不能把新增延迟分配藏掉。

Raw cache 均为 10k instances、10k voices、100k logical Raw event records。旧版 10k 独立事件数组；实际 managed `RawMidiEvent` 为 208 bytes、其中 `SourceReference` 为 160 bytes，因此旧数组元素 payload 合计 20,800,000 bytes（由数量×已测值类型大小推导，不含数组/对象头）。候选实际去重为一个事件数组，元素 payload 560 bytes，source table capacity payload 672 bytes；10k RawEventSequence/instance 与目录对象仍存在，**这不等于整个 Raw cache 只占 1,232 bytes**。

## 4. M12：50k 成功 shared 单模板 Note

夹具：50,000 Logical Notes、单 SubVoice、一个模板 Note、Reject、shared、每 512 tick 一个 Trigger、gate 480、24 个变化 pitch/变化 velocity、10 次单音符 revision。正式成功输出 **600,011 个 canonical events、50,000 个 NoteOn、峰值 1 Unit、无诊断**。

原始结果：[旧](D:/Programing/midora/.tmp/memory-stage5/runs/baseline-v2-note-shared-50k-r10/result.json)、[候选 v1](D:/Programing/midora/.tmp/memory-stage5/runs/candidate-v1-note-shared-50k-r10/result.json)。11 份逐阶段 exact digest 全等，末次 Incremental 与 Full oracle 全等。

| 指标 | baseline | candidate-v1 |
| --- | ---: | ---: |
| 初次 Full | 4,508.67 ms | 3,972.31 ms |
| 10 次增量范围 | 2,641.14～4,123.33 ms | 1,861.47～2,506.24 ms |
| 每次增量累计分配 | 约 2,348.44 MiB | 约 1,293.69 MiB |
| 外部采样 peak Private | 2,946.34 MiB | 1,517.54 MiB |
| 外部采样 peak WS | 2,858.38 MiB | 1,505.10 MiB |
| 最后诊断 GC 后 managed | 10.78 MiB | 10.81 MiB |
| 含仪器总墙钟 | 120.48 s | 102.50 s |
| 含仪器累计分配 | 37,707.85 MiB | 25,063.15 MiB |

Raw 逻辑数量始终为 50k instances / voices、600k records；旧 50k 事件数组、元素 payload 推导 124,800,000 bytes。候选 24 个去重事件数组、实际元素 payload 16,128 bytes，source table capacity payload 672 bytes；仍不含 instance/sequence、目录和 headers。完整 canonical 仍有不可省略的正式输出成本。

## 5. M13 与工具正确性烟测

[history-ownership-smoke](D:/Programing/midora/.tmp/memory-stage5/runs/history-ownership-smoke/history-result.json)：100 Notes、3 次正式 CopySegments/Paste、Clipboard 替换、全 Undo、全 Redo、新分支、History Dispose 全部断言通过；编译明确延后 1 小时，仅用于编辑/所有权归因，不计真实自然后台编译性能。

只对原 Clipboard 的底层 store 做 reference 去重计账：2 stores、resident payload 0、spill 2,408 bytes。baseline 在 Clipboard 替换后仍保留 2,408 bytes，History Dispose 后归零。这是原 payload 仍被有效 History lease 保留的证据，不能外推成“每个命令必占 64 MiB”。候选跨命令/百万级结果及更完整 Source/History/Undo node 断言由专项测试和后续最终候选记录提供；当前烟测不关闭 M13。

## 6. 异常与排除结果

- `baseline-smoke-empty` / `-v2` 是最初 JSON 无法序列化 ReadOnlySpan 的仪器失败，修复后小烟测通过；不进入性能表。
- 初始 `baseline-empty-let-10k-r5` / `baseline-note-let-50k-r10` 未打开 isolation，按正式规则产生 MIDORA1215；不能当作成功扩展负载。已确认 LetOverlap 必须 Per-Note Instance Isolation。`-valid` 小对照通过，但未与 schema 2 正式主表混算。
- 一个 `baseline-empty-50k-r10` 启动与暂停指令交叉，收到暂停后核验命令行并只停止该次测试 child PID；保留非零退出结果，不计通过、不称为守护超限。
- 初次倍率夹具设置 shared，正式 MIDORA1213 拒绝 Loop（要求 isolation），其 `baseline-multiplier-1k-r3` 日志只作为夹具校验，不计性能通过。后续两版均明确改用 isolated。

## 7. 最终候选：SubVoice / Template / Loop 合法倍率

最终依赖冻结见 [candidate-final manifest](D:/Programing/midora/.tmp/memory-stage5/candidate-final/binary-snapshot.json)，来源为完整公共回归输出 `regression-final-v1/Midora.Application.Tests/bin`，与前文中间 candidate-v1 分开保存。探针自身按相同 baseline ABI 构建；最终版 Raw census 另记录 canonical 元素实际 `Unsafe.SizeOf=232 bytes`，只计数组元素，不含数组头及引用图。

倍率夹具：1,000 Trigger × 4 SubVoice × 8 Template Note × 4 Loop，Reject、isolated、Trigger 间隔 2048/gate1920、变化 pitch/velocity、3 次增量修订。Loop 必须开启 isolation；相邻 Trigger 不重叠，正式峰值为4 Unit。两版均成功生成 **128,000 NoteOn / 296,044 canonical events**，无诊断；4 份 exact digest 全等，末次 Incremental 与独立 Full 全等。

原始结果：[baseline](D:/Programing/midora/.tmp/memory-stage5/runs/baseline-multiplier-valid-1k-r3/result.json)、[final](D:/Programing/midora/.tmp/memory-stage5/runs/final-multiplier-valid-1k-r3/result.json)，各自守护结果在同名 `-guard` 目录。

| 指标 | baseline | final |
| --- | ---: | ---: |
| Full | 2,144.89 ms | 2,090.32 ms |
| Full 累计分配 | 1,108.15 MiB | 613.15 MiB |
| 3 次增量范围 | 1,076.09～1,210.47 ms | 918.97～1,104.96 ms |
| 每次增量累计分配 | 约 1,107 MiB | 约 612 MiB |
| peak Private | 1,374.79 MiB | 641.64 MiB |
| peak WS | 1,351.00 MiB | 624.25 MiB |

Raw 均为1k instances /4k voices /296k records；旧4k事件数组的元素 payload 推导61,568,000 bytes，新96数组397,824 bytes + source table capacity10,752 bytes。Canonical 296,044×232=68,682,208 bytes，不能通过共享 Raw 消除这部分必要正式输出。

自然后台工作流小烟测：[final-background-smoke](D:/Programing/midora/.tmp/memory-stage5/runs/final-background-smoke/midi-logical-result.json)。生成的20-note MIDI 经正式导入、Direct→Logical Clipboard、exact target `ProjectStart=168960/Length=24576`、3次正式空位置 CreateLogicalNote、后台发布、Full抽样对照、逐次Undo新增再编译、Paste Undo/Redo均通过。使用产品默认75ms debounce，测试等待不会强制立即编译；每次记录SourceRevision/CompiledRevision/LastSuccessfulResult，仍不包含WPF或音频。该小烟测不等于真实1.38M样本已通过。

同一候选扩大到 **10,000×4×8×4**、1次增量，输出 **1,280,000 NoteOn /2,960,044 canonical events /4 Unit**，无诊断，最后 Full/Incremental exact digest 全等：[10k结果](D:/Programing/midora/.tmp/memory-stage5/runs/final-multiplier-valid-10k-r1/result.json)、[守护](D:/Programing/midora/.tmp/memory-stage5/runs/final-multiplier-valid-10k-r1-guard/guard-result.json)。Full10.7875秒，增量9.4936秒；peak Private4,478,898,176 bytes、WS4,149,239,808 bytes，未触发8GiB守护。40k voices/2.96M Raw records仍是96个数组397,824 bytes，source table10,752 bytes；canonical元素payload686,730,208 bytes。总墙钟138.38秒，累计分配32,302,943,512 bytes，包含大量完整事件digest；`full-oracle`66.743秒包含2份digest，不是纯编译计时。保留必要canonical结果及其oracle并存的峰值，不以Raw共享后payload很小掩盖总成本。

该10k轮末尾的诊断GC后，live managed降至8,182,832 bytes、GC committed91,521,024 bytes，但Private仍4,406,837,248 bytes、WS4,106,657,792 bytes。它证明托管活动对象下降，**不证明Private/实际驻留立即回落**；剩余原生/运行时提交或延迟回收原因未在本探针内归因，不能把controlledGC当成已解决的物理释放验收。

仪器审查修复：早期结果的诊断计数使用GroupBy，会在计数时保留全部诊断引用，放大候选lazy诊断的峰值；后续工具改为按Code流式dictionary累计，不保存整个分组。完整diagnostic digest仍枚举所有诊断并产生lazy临时对象。前文数字仍按原版工具保留，不将改工具后的数字与旧工具混算；无诊断的倍率夹具不受该分组物化影响。

版本核验：final冻结依赖的Application/Domain/Compiler hash与candidate-v1重合是因为两者均在最终M13 splice修改之后构建，不是旧DLL。仍以每份manifest精确hash为依据，后续ResourceShortage计账修补需单独冻结新版本。

## 8. 真实 9KX2：自然后台编译与正式历史

使用包含ResourceShortage计账修复的 [candidate-final-v2 manifest](D:/Programing/midora/.tmp/memory-stage5/candidate-final-v2/binary-snapshot.json)，Application MVID `87378c5b-c99f-4cc4-976e-28c0b34e6f0b`、Compiler `21c02055-7560-4b73-8ee2-bdc4e1f7073d`。最新探针去掉诊断GroupBy并使用100ms有界轮询等待默认75ms debounce自然后台发布；先经20-note/3编辑烟测通过。没有运行WPF，不是人工UI验收。

输入只读文件`D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`，144,175,201 bytes，SHA256 `948fdddfff2f050c1b4e70f240b6138e5ba10939030a82286e149ddcb93e6249`。完整导入保留全部源Track；在MIDI Out #23的唯一Segment选择Note start于 `[168960,193536)`，Paste到新空Instrument绑定的Logical Segment，ProjectStart168960、Length24576。默认Reject/SamePitch，不增加模板发声内容。每次新增通过范围查询找到未占用start/key，再执行正式CreateLogicalNote，等待当前revision完成；逐个Undo新增并再编译，最后Undo/Redo原Paste。

50万梯度：[结果](D:/Programing/midora/.tmp/memory-stage5/runs/final-v2-real-500k-r2-background/midi-logical-result.json)、[守护](D:/Programing/midora/.tmp/memory-stage5/runs/final-v2-real-500k-r2-background-guard/guard-result.json)。源同pitch半开区间pair134,802、最高活动8，未越Int32；500,000选中Note通过正式Exact duplicate规则发布464,942条，折叠35,058，目标Length保持24576。Paste与2次新增后的OverlapValidation失败均准确保持66,810条诊断（本fixture用Count+257固定ordinal抽样，不是全digest），与独立Full抽样一致；两次Undo新增恢复前一抽样。Undo Paste后成功；Redo恢复同一失败抽样。整个失败过程中LastSuccessfulResult持续保留36,003,624events。

导入14.504秒、Copy1.593秒、Paste2.229秒；Paste后等待自然编译6.149秒，两次新增命令224.97/49.39ms，随后自然编译4.591/4.268秒。每次后台compile累计分配约4.92GB，不是驻留内存；守护peak Private1,437,798,400 bytes、WS1,451,282,432 bytes，exit0、未触阈值。关闭document/session/project后，诊断GC后managed97,819,920 bytes、GC heap452,366,640 bytes、GC committed967,098,368 bytes；3秒不再GC采样Private仍1,259,847,680 bytes、WS1,272,045,568 bytes。保留live/heap/committed差异，不把差值一概当泄漏或全部当碎片，也不宣称物理驻留已归零。

现有Int32诊断Count边界仍是风险：65,537个不同start但全部互相重叠的同key Note理论产生2,147,516,416pairs，越`int.MaxValue`。本轮不重型物化该输入；真实选区保守源pair若越界，探针会在Paste前停测并上报，而不是截断为成功。

关闭阶段的限定：这是显式Dispose后观察，不是全部owner已被回收的证明。虽已清空显式project/session/source变量，探针的闭包/局部target/instrument等仍可能保留模型；未设置WeakReference<Project>回收断言。不得把该阶段的残留定性为产品泄漏，也不得宣称Project全部释放；全owner内存图与旧Project弱引用属于公共专项测试/阶段6证据。

### 完整1,382,908选区，5次正式新增

[完整结果](D:/Programing/midora/.tmp/memory-stage5/runs/final-v2-real-full-r5-background/midi-logical-result.json)、[守护](D:/Programing/midora/.tmp/memory-stage5/runs/final-v2-real-full-r5-background-guard/guard-result.json)。工具选区上限2M大于实际1,382,908，未截断源选区。源半开同pitch pair844,199、最高活动9，未越Int32。正式Paste发布**903,352条Logical Note**，479,556条exact duplicates按既有碰撞规则折叠；目标ProjectStart168960/Length24576保持不变。这一数据量变化是正式编辑语义，不是内存优化截断。

Paste及每次新增均正式失败于OverlapValidation，保持**68,502条诊断**；采样的Count、257固定ordinal全部来源字段及结果身份与独立Full一致，5次Undo新增依次恢复前一身份。Undo Paste成功（0诊断），Redo恢复同一失败身份。SourceRevision/CompiledRevision按1～13逐次一致发布；失败时LastSuccessfulResult保留36,003,624events。这是“预期失败路径正确完成”，不是声称空Instrument已有可播放输出，也不是对全部68,502诊断做full digest。

| 阶段/指标 | 测量 |
| --- | ---: |
| 正式完整MIDI导入 | 14.657秒 |
| Copy Direct Notes | 3.815秒 |
| Paste as Logical | 3.997秒 |
| Paste后自然后台编译等待 | 9.780秒 |
| 5次正式CreateLogicalNote命令 | 222.97 /101.04 /89.78 /89.43 /89.85 ms |
| 5次新增后自然后台编译等待 | 8.632 /8.443 /8.314 /8.195 /8.646秒 |
| 每次新增后后台编译累计分配 | 约9.576GB（不是驻留） |
| 独立Full及257抽样oracle | 9.466秒 |
| 守护peak Private | 2,621,988,864 bytes（约2.442GiB） |
| 守护peak WS | 2,654,326,784 bytes（约2.472GiB） |

5次新增后阶段尾Private依次1.619/2.249/2.165/2.346/2.358GB，managed约1.234/1.131/1.223/1.222/1.222GB；在该有限修订序列内未观察到“每次新增都永久复制整个累计结果”的线性驻留增长。累计分配仍很大，后台每轮约8～9秒，**不能据此称编辑/编译已达到交互延迟目标或整个会话长期无增长**。

显式Dispose后诊断GC：managed72,483,920 bytes、GC heap229,998,280 bytes、GC committed1,331,265,536 bytes；3秒无额外GC后Private仍2,201,972,736 bytes、WS2,238,054,400 bytes。guard exit0、stopReason null；全程没有接近8GiB私有Job上限或执行用户进程终止。GC与Private差距仍保留为阶段6需要内存图/OS映射归因的风险。

## 9. 修正仪器后：10k诊断路径正式对照

同一最新探针源码分别针对原始baseline/final-v2冻结依赖构建，诊断计数改为流式按Code累计，不使用GroupBy保留全部lazy诊断。夹具与§3完全相同。结果：[baseline-stream](D:/Programing/midora/.tmp/memory-stage5/runs/baseline-stream-empty-10k-r5/result.json)、[final-v2-stream](D:/Programing/midora/.tmp/memory-stage5/runs/final-v2-stream-empty-10k-r5/result.json)。六份完整有序canonical/diagnostic/source digest相等，末次Full oracle相等；仍全部保留1,182,861条诊断。**内存比较优先使用本节同版仪器数据，§3保留为仪器修正前记录。**

| 指标 | baseline | final-v2 |
| --- | ---: | ---: |
| Full | 1,400.97ms | 1,011.36ms |
| 5次Incremental范围 | 496.21～925.28ms | 87.19～157.85ms |
| Full累计分配 | 397.29MiB | 105.42MiB |
| 每次Incremental累计分配 | 395.19～396.29MiB | 103.80～103.83MiB |
| 外部100ms采样peak Private | 957.90MiB | 50.36MiB |
| 外部100ms采样peak WS | 971.46MiB | 87.89MiB |
| 含全部仪器总墙钟 | 23.765秒 | 16.689秒 |
| 含全部仪器累计分配 | 2,920,662,528 bytes | 4,089,670,512 bytes |
| 最终诊断GC后managed | 1,854,560 bytes | 1,861,040 bytes |

候选某个阶段尾独立采样Private为55.12MiB，高于外部100ms采样最大50.36MiB；因此后者只能称采样峰值，不能冒充所有瞬间的绝对峰值。Job 8GiB Private commit限制为同步硬限，不依赖这个100ms采样精度。候选总累计分配仍较高，来自完整oracle/计数枚举lazy诊断构造的临时对象；不能因峰值更低而省略这一成本。所有成功对照仍各只有一次独立进程，不外推统计分位数。

## 10. 100k×10结构Clipboard / History对照

同版工具，100,000个Logical Note的Segment，正式CopySegments后连续Paste10次，再替换Clipboard、全Undo、全Redo、新分支、History Dispose。编译显式延后一小时，仅做编辑/所有权归因。两版全部内容数量/历史/分支断言通过：[baseline](D:/Programing/midora/.tmp/memory-stage5/runs/baseline-history-100k-c10/history-result.json)、[final-v2](D:/Programing/midora/.tmp/memory-stage5/runs/final-v2-history-100k-c10/history-result.json)。

| 指标 | baseline | final-v2 |
| --- | ---: | ---: |
| 外采peak Private | 188,157,952 bytes | 161,230,848 bytes |
| 外采peak WS | 228,868,096 bytes | 202,997,760 bytes |
| 已计时各阶段时间之和 | 3,911.11ms | 3,701.16ms |
| 已计时各阶段累计分配 | 1,267,286,016 bytes | 1,267,084,520 bytes |
| 末尾诊断GC后managed | 13,911,896 bytes | 13,911,704 bytes |

对**原始Clipboard store**做reference去重，均为2 stores、resident payload0、spill2,400,008 bytes；每次Paste和Clipboard替换后该spill仍存在，History Dispose后为0。结构Paste命令确实仍需要有效源lease，保留到History释放是正确行为，不能要求Clipboard替换时无条件销毁。该structural CopySegments对照本来就不应证明Direct/Logical跨类型准备完成后的源clipboard冗余lease消除，亦不覆盖全部Project/History独占图；专项所有权测试和§8真实跨类型工作流提供不同证据。两版差异较小不证明M13无效，单轮20～30MB峰差也不应当作稳定收益。

至此停止增加压力规模并释放独占测试槽。未运行1.38M原始baseline、65,537全相交Int32越界物化、WPF/音频人工流程或长会话；现有输出不覆盖这些验收边界。
