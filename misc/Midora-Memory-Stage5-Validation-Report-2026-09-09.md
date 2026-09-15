# 内存优化阶段 5：M12 / M13 验证报告

日期：2026-09-09。开始时 HEAD：`4a4beb63a11514e1f9042dc0dd739e143d120559`，工作区包含用户已验收但尚未提交的阶段 4 修改，完整保留。

状态：阶段 5 本轮实现与专项工程验证完成，M12/M13 的保留边界见 §7；不据此宣称任意展开规模、完整 WPF 长会话或验收 C 已完成。不自动进入阶段 6，不提交、推送或生成 `dist`。

2026-09-09 后续收尾：上段及正文保留阶段 5 当时的授权和证据。后续阶段 6、独立编译修复及 MEM-C 已通过；§7 的 Int32 诊断边界已由[独立 Int64 实施](Midora-Int64-Diagnostics-and-Bounded-Readme-Verification.md)解决。成功 Logical canonical 尚未深度分页的限制仍然存在，现列为[下一项优先工作](Midora-Pre-Expansion-Closeout-and-Next-Step-2026-09-09.md)，不是要求重开本阶段。

## 1. 需求与根因

依据：[执行计划 §10](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md)、[本阶段设计与需求追踪](Midora-Memory-Stage5-Requirement-Trace-and-Design.md)。未改 Project Format 3、SRS 音乐语义、Mapping ABI 或有效历史范围。

用户复现为 9KX2 的 MIDI Out #23 后段约 138 万 Note 转入 Logical Segment 后，每次小编辑继续增长内存。阶段 4 已让源音符分页；本阶段证据表明不能把源分页当作整个编译/诊断/历史链已有限内存：

- 默认 Reject / SamePitch 重叠诊断逐对物化。小型同音高夹具 10,000 Trigger 已产生 1,182,860 个重叠 Error，另有一个空 SubVoice Warning；UI 又建立完整行对象并复制筛选选择。
- 每个实例与 SubVoice 都保存完整 `RawMidiEvent[]`；实测单条 Raw value 为 208 bytes，其中 SourceReference 160 bytes，大量 Reset/Initial 及模板记录只是上下文身份和绝对 tick 不同。
- canonical 同 tick 折叠、范围处理和消费者准备重复复制全事件数组，使新旧编译缓存交接的合理共存成本被进一步放大。
- Note 粘贴本已有独立 owner-root，却再次套全 Project 草稿；完成后的历史还持有不再需要的原 Clipboard lease。不可变树编辑重建所有分支，splice 与局部编辑相交时还可经旧叶持有过期来源链。

这几项不等于“17 GB 已按组件精确分摊”。独立探针、真实样本数据链与 WPF 单元验证分别报告，真实完整 UI/音频/后台长期共存仍属于阶段 6。

## 2. 已实施

| 范围 | 实现 | 不改变的内容 |
| --- | --- | --- |
| Raw cache | 相对 tick/sequence/source context，精确 event pattern 共享；发现缓存最多 4,096 pattern / 16 MiB；冻结 voice initial/target 闭包按 Segment 共享 | 每个实例的实际事件、来源字段、顺序、Mapping、Loop、Pre-Roll 和生命周期 |
| overlap diagnostics | 紧凑来源表与连续范围；按需还原每一条重复诊断；计数直接汇总、筛选按物理来源 | 正式条数、重复、顺序、Severity、Source 与导航，不删除或降级 Error |
| Diagnostic UI | 虚拟 IList + 最多 256 行缓存；旧选择索引 O(1) 识别，后台筛选复用不可变选区 | 全部项目诊断与筛选能力；不建立第二个业务模型 |
| canonical / consumers | 稳定原地 fold/range 压缩；避免无 Direct append 的重复排序；读取 canonical span 不先复制全数组 | 统一 canonical、范围恢复、同 tick 顺序、正式音频/MIDI 投影 |
| 失败与 README | 任务结果共享诊断；播放失败短摘要另保留完整诊断；正式 README 按行严格 UTF-8 流式输出并可取消 | 不截断 README 诊断正文、不改变正式字节与原子输出 |
| History / Clipboard | sequence path-copy；编辑/splice 来源地址压平；已独立准备的 Note 粘贴不持多余全项目草稿/原 Clipboard lease | 全部 Undo/Redo、旧读者 lease、随机/表达式 Redo 不重新求值、取消原子性 |

详细记录：[诊断与 README](Midora-Memory-Stage5-Diagnostics-Design.md)、[M13 共享历史](Midora-Memory-Stage5-M13-Shared-History-Design.md)。

## 3. 安全与测量口径

重型测试串行，工具在子程序第一条指令前分配私有 Windows Job。8 GiB（约 8.59 GB）Private commit 硬上限、Private/Working Set 采样阈值，以及系统 available RAM 至少 2 GiB；提前于用户约 9 GB 的警戒要求。只结束本次拥有的测试树，不结束用户程序。96 MiB 低限人工夹具已实际触发终止验证，无需耗尽机器。

基线在生产修改前冻结，候选和原版使用相同夹具。编译计时不包含 digest/census；守护峰值包含这些仪器及刻意共存的 Full oracle。累计 allocation 不是常驻；诊断性强制 GC 不进入产品或正式操作计时。纯数据探针不运行 WPF、SoundFont、音频设备，不把它的结果当成真实应用内存上限。

详细阶段日志、二进制身份与对照结果由 [测量报告](Midora-Memory-Stage5-Probe-Results-2026-09-09.md) 汇总。

## 4. 公共回归

Release、受守护串行回归，共 **3,107 / 3,107** 个唯一 case，通过 11 个测试项目。没有跳过/失败计入通过：

| 测试集 | 通过 |
| --- | ---: |
| Common | 81 |
| MIDI | 29 |
| Application | 1,128 |
| Compiler | 455 |
| Persistence | 228 |
| MIDI Export | 47 |
| Playback | 140 |
| Audio Render | 37 |
| Desktop Presentation | 445 |
| Desktop | 407 |
| BASS 托管协议/ring/limiter/PCM/WAVE/plan gate | 110 |

主回归 `.tmp/memory-stage5/regression-final-v1/` 包含 3,102 项；补充的完整 Compiler 453 项与最终 455 项分别位于 `.tmp/memory-stage5/compiler-roundtrip-final-results/`、`.tmp/memory-stage5/compiler-accounting-final-results/`，只新增计入 3 个 source/context/Group 极值及 voice-state 修订测试、2 个 ResourceShortage 计账测试，没有重复累加旧 case。

- 新 Raw 缓存测试首轮暴露 sentinel 的相对表示阻止重复模式共享，已修复并重跑。正式 MIDI/来源结果没有据此放宽。
- Playback 原精确异常类型断言更新为携带完整诊断的 `CompilationRejectedException`，同时验证仍含 MIDORA1306、后端未准备、锁和状态正确；不是忽略失败。
- Application 包含最终 splice 收口的 M13 新 21 case：百万根、深层历史、Clipboard 释放、取消、splice/小编辑交替、cache-only 和旧来源释放。
- 新 WPF 诊断测试验证 50,000,000 个逻辑条目的默认列表不物化、最多 256 个缓存行、旧视图 selection 查找、取消/最新筛选与热切换；Compiler 紧凑来源/范围测试另验证 49,995,000 条完整诊断的计数、抽样、过滤与存储。不依赖 computer-use。
- README 验证完整旧 Markdown 字节、保留重复行、单遍有界写入、取消、元数据冻结及任务原子性。
- 最终只读复查补齐失败 canonical 的 ResourceShortage 对象及五个 ID 数组计账；按底层身份去重，无音乐结果或公共 API 改变。两个独立 collector、共享失败结果及重复收集专项均通过。
- 公共回归最大测试树 Private / Working Set 为约 2.57 / 3.36 GiB（Application 集，含构建/测试宿主），没有触发守护。BASS 集仅托管 gate，没有发布 Worker、启动物理设备或做听感验收。

真实 9KX2 场景已完成，见 §6。工程测量收口状态以文首及完整测量报告为准。

## 5. 已完成的编译压力对照

以下是单轮进程内多修订结果，不当作独立多次统计，也不把工具峰值当作完整 WPF 主程序峰值。所有旧/新对照均使用同一正式负载；完整事件/来源/诊断 digest 与 Full/Incremental oracle 一致。

| 负载 | 改造前外采峰值 Private | 改造后外采峰值 Private | 编译时间变化 |
| --- | ---: | ---: | --- |
| 10k 默认新空 Instrument，1,182,861 条完整诊断，5 次修订；最终同版流式计数工具 | 957.90 MiB | 50.36 MiB | Full 1.401→1.011 s；增量 0.496～0.925→0.087～0.158 s |
| 50k Trigger，单模板音符，共 600,011 事件，10 次修订 | 2,946.34 MiB | 1,517.54 MiB | Full 4.509→3.972 s；增量 2.641～4.123→1.861～2.506 s |
| 1k Trigger ×4 SubVoice ×8 模板 Note ×4 Loop，共 128k NoteOn /296,044 事件 | 1,374.79 MiB | 641.64 MiB | Full 2.145→2.090 s；不据一次测量宣称稳定加速 |

更大合法倍率：10k Trigger ×4×8×4，成功输出 **1,280,000 NoteOn /2,960,044 事件 /4 Unit**，Full 10.787 s，增量 9.494 s。候选守护峰值 Private **4,271.41 MiB（约 4.17 GiB）**、WS 3,957.02 MiB；包含独立 Full oracle 与旧结果同时存活及全量 digest。未运行该倍率的未修复版，不能虚构其降幅。

已测 canonical value 为 232 bytes，上述 2,960,044 事件的元素 payload 必要成本为 686,730,208 bytes；Raw 的 2.96M 逻辑记录共享为 96 个数组、元素 payload 397,824 bytes，另有来源目录、每实例/voice/sequence 与正常元数据。不能把去重数组大小冒充整个编译缓存大小。

时间/测量限制：默认失败夹具的完整诊断枚举会按需重建诊断对象。最早工具的 GroupBy 计数会保留全部诊断引用、污染内存观察，该轮历史结果保留在测量报告中，不与新版混算。表中已换成同一最终工具的旧/新重测：6 份完整 digest 一致，总墙钟 23.765→16.689 s，但累计 allocation 2.921→4.090 GB，lazy 全行枚举仍有更多临时分配。候选阶段结束采样曾达到 55.12 MiB，高于 100 ms 外部采样的 50.36 MiB；这些是采样峰值而非每纳秒真实峰值，Private 硬上限另由 Job 同步约束。巨大真实样本只计总数及 257 条固定抽样。

10k 倍率关闭后的受控 GC live managed 约 7.8 MiB、GC committed 约 87.3 MiB，但立即采样 Private 仍约 4.1 GiB，不能据托管 live 很低声称整个进程已回落。该探针没有 WPF；缺少 runtime/native/OS 分区证据时不把差额武断归因为 GC 高水位或泄漏，后续全链路阶段需纳入延迟回收与内存图分析。

M13 另有 100k Note、10 次结构 CopySegments/Paste、全 Undo/Redo、新分支和清理的旧/新受守护对照，断言均通过，外采 Private 188,157,952→161,230,848 bytes。两版原 Clipboard 的 2 个 store /2,400,008 bytes spill 都正确保留至 History Dispose 后归零；结构粘贴仍需要该 lease，不因“优化”提前释放。Note 粘贴的独立准备与旧 source 释放、百万根路径共享和深层 splice 分别由 21 项 M13 专项验证，不用结构粘贴这条对照代替。

## 6. 真实 9KX2 跨类型复现

使用正式 ImportFile、ProjectDocumentSession、Clipboard、CreateLogicalNote、Undo/Redo 和 **默认 75 ms debounce 的自然后台 ProjectCompilationSession**，未关闭自动编译。目标固定 ProjectStart 168960 / Length 24576，粘贴 local tick 0；源文件只读。先跑 500k，再跑完整选区，两个进程均 exit 0，未触发守护。

| 项目 | 500k 梯度 | 完整源选区 |
| --- | ---: | ---: |
| 选择源 Note 数 | 500,000 | 1,382,908（2M 安全上限未触及） |
| 实际发布 Logical Note 数 | 464,942 | 903,352 |
| 按既有精确重叠规则折叠 | 35,058 | 479,556 |
| 连续正式新增 / 逐次 Undo | 各 2 次 | 各 5 次 |
| 峰值 Private | 1,437,798,400 B（1.34 GiB） | **2,621,988,864 B（2.44 GiB）** |
| 峰值 WS | 1,451,282,432 B | 2,654,326,784 B |
| 初次粘贴后的后台等待 | 6.15 s | 9.78 s |
| 新增后后台等待 | 4.27～4.59 s | 8.20～8.65 s |

完整场景的 Copy / Paste 为 3.81 / 4.00 s；正式单音符命令为 0.09～0.22 s，后续后台编译另计。每次新编译累计分配约 9.58 GB，**累计分配不是常驻内存**，本阶段仍未消除逐实例展开的全部临时工作。自然 GC 下前两次编辑后 Private 上升至约 2.1 GiB，后续新增和 Undo 在约 2.1～2.3 GiB 波动；没有重现用户每次小编辑继续攀升到 7～17 GB 的模式。未安全重跑原版完整选区，故不将用户 UI 观察值与本探针计算成严格旧/新百分比。

默认新空 Instrument 保持正式 OverlapValidation 失败；初次完整场景保留 **68,502 条诊断**。这不是编译变为可消费，也没有为了内存删 Error。每次等待核对 SourceRevision / CompiledRevision；失败不替换已成功的原 MIDI 结果（36,003,624 events）。独立 Full、每次新增的 Undo、Paste Undo 后恢复原成功、Redo 后恢复原失败均验证结果身份与 257 个固定诊断 ordinal。小型夹具使用全部事件和全部诊断的 exact digest；大样本抽样不冒充全行证明。

测试明确按 Note start-in-range 选取；复制后减少的音符属于当前共同编辑规则的 exact start/key 碰撞处理，并非测量工具静默降采样。目标 Segment 长度始终保持 24576。

完整场景显式 Dispose 后受控 GC 的 managed 约 69.1 MiB，GC heap 约 219 MiB、committed 约 1.24 GiB；3 秒无 GC 观察后 Private 仍约 2.05 GiB。夹具的局部变量/闭包仍可能保留来源，且此模式没有 Project 弱引用断言；这些是 **Dispose 后观测**，不是整个 Project/native 已完全回收的证明。阶段 6 继续负责完整 WPF 长会话与内存图归因。

## 7. 必须保留的限制

1. 不能承诺任意 Trigger × SubVoice × Template × Loop 展开后都只占常量 RAM。成功 Logical canonical 仍有必要完整事件数组、分配和来源；本阶段减少的是重复存储、额外副本和无用保活。极端合法输出自身仍可能很大，是否需要深度 Logical canonical 页化必须以压力梯度结果决定，不能以 Pure MIDI 已分页代替。
2. 诊断公共序列仍沿用 `IReadOnlyList` 的 Int32 Count。超过 2,147,483,647 条会触发 checked overflow，完整新诊断不能发布，不能宣称任意密度均已解决。该边界不一定需要百万源：65,537 个同 key、不同 start 且 gate 全相交的合法源就会有 2,147,516,416 个 pair。此为公式与源码确认，未通过耗尽原版内存进行实测。本轮没有静默截断或删除诊断；彻底解决需 Int64 逻辑总数与分页导航/消费者契约，不等同增加一个内存预算常量。
3. 不删除有效历史；必要变化页、稳定地址和历史结构仍随真实编辑量增长。共享读缓存的 64 MiB 不等于全部历史最多 64 MiB，更不是程序总上限。
4. 最终完整 WPF/后台编译/视图/音频/打开保存共存及用户手感验收归阶段 6 / 验收 C，本阶段不提前宣布其通过。

## 8. 交付与后续验收

- 工程交付为当前源码及本文链接的设计、测试、测量工具；未运行 computer-use，未生成 dist，未提交或推送。阶段 4 的已验收未提交修改完整保留。
- 本阶段按原计划不要求用户重新执行全部 UI 检查，人工整体验收仍合并到阶段 6 后的 C。可选的快速复查只需重复本次 9KX2 跨类型粘贴及连续新增路径。
- 下一阶段先核对完整 WPF 自然后台与多视图/历史/保存共存，再分析 Private/GC committed/live 差额；不要把源码仍存在的必要 canonical、历史成本或累计 allocation 直接叫作泄漏。
- Int32 诊断总数属于明确未解决的极端边界。后续若扩展为 Int64/分页导航，需要独立写明公开序列、UI/导出/诊断消费者契约和可取消性，不能静默丢行或改成较少条 aggregate 诊断来让测试通过。
