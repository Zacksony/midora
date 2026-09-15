# Pure MIDI 范围编译阻塞修复：验证报告

日期：2026-09-09。状态：**修复完成；3157 / 3157 公共回归、9KX2 三轮前后实测和当前构建的完整 WPF 大会话复验通过。编译阻塞已解除；2026-09-09 用户确认 MEM-C01～C07 全部验收通过，并另行授权提交、推送，不授权本地发布。**

实施时按用户“实施修复”授权，独立修复阶段 6 暴露的 Compiler 正确性阻塞并保留此前阶段 6 工作；实施时未提交、推送、发布或使用 computer-use。不修改 Project Format、软件版本、音频算法或 UI 工作流。后续已验收并提交、推送。超过 Int32 的诊断列表当时暂缓，现已由[独立 Int64 实施](Midora-Int64-Diagnostics-and-Bounded-Readme-Verification.md)解决；历史限制不得继续作为当前阻塞。

设计与预算见 [修复设计](Midora-Pure-MIDI-Range-Correctness-Repair-Design.md)。历史反证见 [原阻塞记录](Midora-Memory-Stage6-Pure-MIDI-Range-Blocker-2026-09-09.md)；历史失败仍保留，不反向改写成通过。

## 1. 修复内容与边界

| 已确认根因 | 本轮处置 |
| --- | --- |
| Pure MIDI resident 与 paged 分别处理范围，实际事件、默认/恢复状态、终点清理和计数不一致 | 使用同一 Pure MIDI 投影；旧双路径删除；小源仍可冻结为数组，大源仍分页 |
| 提前 end 没有完整释放 FIFO 剩余 Note | 对成对 Note 用端点计数，对 raw 与普通 Note 的实际相互影响建有界精确前缀；来源由正式 NoteOn FIFO 后缀取得 |
| Query 窗口恰从子 Segment End 开始时，裁剪 NoteOff 重复 | 自然原始 end 与越过 content window 的裁剪 end 分开，每个端点只出现一次 |
| 中途共享状态的来源不一定仍有 Segment 与请求相交 | 恢复、preset 预备元数据及缓存身份均包含活动连通区间内的早期兄弟来源 |
| 保存重开后生成事件的排序元数据不同；SMF 小/大路径另行排序 | 统一生成事件的 stable/semantic/SMF 字段；导出使用同一 canonical Track 投影 |
| FIFO 剩余 Note 来源 Track 可能已经 EOT | 来源身份不变，最终 Root 清理的 SMF 归属按 SRS 23.7.5 指向该边界参与的最低顺序 Track |
| 端点归并游标强持有各页解码数组，绕过既有缓存淘汰 | 游标只保留 Current 值及弱页引用，既有 decoded LRU 仍控制强持有 |
| 单个 future raw Note 使短范围也扫描普通音符全前缀 | 加入 raw 最早位置判定、可证明独立的 raw 计数分支；真正交互时才构建有界合并前缀索引 |

内部 Pure MIDI 缓存 ABI 升到 `AUDIO_FRAGMENT_V4` / `CANONICAL_RANGE_V2`，不复用旧错误投影的 PCM。不会删除源工程，不改变持久化格式。

## 2. 正确性证据

### 2.1 独立反证保留

原 `PureMidiRangeLayoutEquivalenceTests` 未删、未 Skip、未放松任何断言。旧 DLL 的 `[300,600)` 反证仍失败；新实现通过，实际查询事件与声明计数一致，包含两条终点 NoteOff、完整清理、原来源 ID。不是仅比较两个可能同时错误的生产实现。

`MemoryStage6ConsumerChainTests` 保留完整字段和输出比较：混合 Logical/Pure、Loop/Pre-Roll/Mapping、共享 Root、三采样率计划、260 范围缓存、MIDI 导出、修改态 Save/Copy/Open、Undo/Redo。原来在第一次重开处中断的后半链必须实际跑完才能计为通过。

### 2.2 新增覆盖

- 12 个 seed × raw/成对两类 × 5 个范围，共 120 组；3 条共享 Track、相接 Segment、content offset、被裁剪 Gate、同 key 重叠、0/127 NoteOff velocity；Fixed/Auto 两种路由。逐条比较全部 canonical 字段，并用独立小 FIFO oracle 核对 Note 端点/来源；7 ticks 分窗拼接必须等于整范围。
- 12 轮 COW 修改，包括 Note start/gate/key、raw Off tick/key、新 raw On、删除 Note、crop；缓存编译与新 Compiler Full 的事件、fingerprint、NoteOn/总数相等。
- 12 组导出范围：不同源布局逐字节相等，opaque 保留，每个 MTrk 自身 EOT 不延长；跨 Track FIFO 来源已结束的情况由独立断言覆盖。
- 早期兄弟 Bank/Program 改动会改变范围和 fragment 身份，preset 预备列表仍包含该来源；新建空 MIDI Track 不做零宽度 Query。
- 排序强制小 run / 多路 spill，升降序完整 Source round-trip；raw 前缀精确 tick 重用、扩大查询、取消不发布、4096 checkpoint 上限与 ClearCache。
- 600,000 条交错端点源、4 MiB decoded cache；在归并队列持有全部路时执行诊断 GC，新增存活量须小于 12 MiB；随后仍能读完 300,000 条目标记录且顺序正确。这是专项活对象上限断言，不是产品中强制 GC。
- 明确验证：future raw 不建前缀；完全无普通 Note 的孤立 Off 不建前缀；会消费普通 Note 的 Off 必须走精确索引。

### 2.3 最终公共回归

命令：`eng/MemoryStage6Probe/Run-Regression.ps1`，Release、独立 OutDir、11 套测试串行，每套独立 8 GiB / 2 GiB guard。证据目录 `.tmp/pure-midi-range-repair/regression-final/`。

| 测试项目 | 通过 / 总数 |
| --- | ---: |
| Common | 81 / 81 |
| MIDI | 29 / 29 |
| Application | 1139 / 1139 |
| Compiler | 470 / 470 |
| Persistence | 228 / 228 |
| MIDI Export | 48 / 48 |
| Playback | 143 / 143 |
| Audio Render | 37 / 37 |
| Desktop Presentation | 445 / 445 |
| Desktop | 427 / 427 |
| BASS 托管子集 | 110 / 110 |
| **合计** | **3157 / 3157** |

最终运行无失败、无 TRX NotExecuted；各 test 子进程正常退出，未触发 guard。最大进程树 Private 为 2,516,504,576 bytes（2.344 GiB），对应 Application 套件；该套件峰 WS 为 2,594,770,944 bytes（2.417 GiB）。这包含测试宿主和构建进程，不是产品运行最低配置。统计原件为 `regression-summary.json`，逐套 TRX 和 guard 摘要均保留在上述目录。

BASS 只执行现有十类托管 gate，不执行物理设备或真实 SoundFont；opt-in 测试自行 return 不能当成真实样本已运行。真实 MIDI 证据只引用下一节的独立探针。

### 2.4 中间失败如何处置

中间版本的空 Root 零长度查询、SMF final owner、旧 ABI golden 均有明确失败→修复→复验，证据保存在 `regression-r1`、`export-red`、`export-green-r1`、`abi-full` 等目录，未删除。

Application 一个既有测试没有 Dispose Document History，却直接删除还在被 history lease 使用的 spill 目录。修正测试 owner 顺序，不改断言，不重试文件删除。表达式分配与 Conductor dispatcher 时间门在中间整套各失败一次；未改相关产品逻辑、删断言或放宽门限，最终串行结果单独列出。

## 3. 9KX2 冷热时间与内存

输入 `D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`，18,000,000 Note；不改样本、不加载 SF2、不播放物理音频。对照旧 stage-6 冻结 DLL 与最终候选，相同探针串行交替各三次，使用 8 GiB private commit 硬限制、至少 2 GiB 系统 available reserve。每次独立进程、独立导入；OS 文件缓存不主动清空。

旧版范围输出本身错误，因此“旧版快”不代表它完成了同样的正确工作。特别是短范围旧声明 32,893,304 events / 16,444,840 NoteOn，实际仅 88,946 events / 44,508 NoteOn；新实现应为 91,807 / 44,508，终点 NoteOff 由 217 补齐为 3,018。

### 3.1 可复现方式与构建身份

运行 `eng/PureMidiRangeProbe/Run-Comparison.ps1`，`Repetitions=3`。冻结对照来自 `.tmp/memory-stage6/candidate`，修复后来自最终回归 `Midora.Application.Tests/bin`。全部原始 JSONL 和六份 guard 结果在 `.tmp/pure-midi-range-repair/comparison-final/`，未覆盖先前记录。

| 组件 MVID | 修复前 | 修复后 |
| --- | --- | --- |
| Compiler | `43c623d5-1258-4558-a130-a971fde09069` | `a65bb08c-f054-4422-9f81-eb50f39db8fa` |
| Domain | `ddf7a0e8-ba43-476f-953a-ae58236156f0` | `371a305a-ea71-436c-a986-7ae2bcd7985f` |

最终 Compiler SHA-256 为 `AEBEA797D9D3B63E8BE3E49773165ABFF96EC4B3F4D966ACC7F152AA3596B1CA`；Application 回归、Desktop 回归和性能探针复制的该 DLL 完全相同。运行时为 .NET 10.0.11。这里的 cold 只代表该进程第一次请求此范围，之前已完成导入和表中前序请求；**不是 OS 文件缓存冷启动，也不表示整个进程未曾读取这些源页**。

### 3.2 时间代价

单位 ms；各格为三次中位数，不把 Query 的时间藏进“编译很快”的结论。首/热请求顺序固定，背景没有其他大型测试。

| 场景 | 修复前首次 | 修复后首次 | 修复前重复 | 修复后重复 |
| --- | ---: | ---: | ---: | ---: |
| 全范围 Compile | 46.45 | 52.08 | 41.95 | 48.77 |
| 全范围结果读取密集窗口 `[168960,169056)` | 994.53 | 1062.49 | 363.04 | 311.50 |
| 中途至自然末尾 Compile `[168960,208898)` | 46.76 | 83.68 | 62.21 | 86.88 |
| 提前结束 Compile `[168960,169056)` | 29.17 | 50.26 | 22.22 | 68.29 |
| 提前结束结果完整读取 | 154.34 | 463.77 | 186.21 | 513.91 |
| 原始 Note 消息区间 Compile `[181200,181400)` | 32.07 | 630.40 | 10.27 | 362.14 |
| 原始 Note 消息区间完整读取 | 1148.49 | 1352.58 | 532.07 | 1164.72 |

其他同序列中位数：

| 场景 | 修复前 ms | 修复后 ms |
| --- | ---: | ---: |
| 完整导入 | 19657.61 | 18827.39 |
| 从 full 读取开头 `[0,96)` | 125.66 | 91.99 |
| 中途 canonical 读取开头 96 ticks | 223.05 | 200.84 |
| 一个 Direct Note 新增后的 Incremental | 14.20 | 451.08 |
| 同次编辑后的独立 Full oracle | 9.25 | 32.31 |
| 同次编辑后的完整短范围读取 | 530.45 | 371.18 |
| ClearCache | 0.08 | 0.09 |

**明确存在时间代价，不能写成“无性能倒退”。**这条探针在原始 Note 消息区间之后，返回较早的 cut 并进行单 Note 修改；首次 Incremental 包含新修订的边界/端点预备，451 ms 不能用紧随其后的 32 ms Full 替代。与随后 Query 合计约 822 ms，对照约 545 ms。该时间是后台编译/查询，不是拖动或命令的 UI 同步时长。

最终提前结束首次 Compile 三次为 **38.42 / 50.26 / 51.04 ms**，已消除中间版本约 3.2 秒的无关 raw 前缀扫描。修复后 raw 区间首次为 **596.81～633.94 ms**；这里确实需要判断 raw 与普通 Note 的精确影响并取得终点计数，不能用少发事件换回原错误路径的 32 ms。完整输出的 FIFO 剩余来源后缀读取也有额外成本。当前不是为全部数据证明了最快算法，后续若优化，应保持本轮完整 oracle 与内存预算。

导入和普通窗口的部分数字更快，但只有三轮且受 GC / OS 页缓存影响，不宣称稳定加速百分比。Warm 不保证每次比 cold 快：不同步骤会触发 GC、淘汰和首次执行的代码路径。

### 3.3 实际事件结果

三轮修复后的短范围完整枚举，均断言声明数等于实际数；重复请求内容 hash 稳定，修改后的 Incremental/Full fingerprint 和计数相等。

| 范围 / 内容 | 实际总事件 | 实际 NoteOn | 位于 end 的 NoteOff |
| --- | ---: | ---: | ---: |
| `[168960,169056)` 修复前 | 88,946 | 44,508 | 217 |
| 同范围修复后 | 91,807 | 44,508 | 3,018 |
| 同范围新增一个 Note 后 | 91,809 | 44,509 | 3,018 |
| `[181200,181400)` 修复前 | 350,149 | 173,558 | 322 |
| 同范围修复后 | 352,010 | 173,558 | 2,155 |

修复后的 full 声明为 36,003,450 events / 18,000,000 NoteOn；中途至自然末尾声明为 13,750,244 / 6,872,183。探针没有为了这两个长范围强制枚举全部 3600 万条事件，不能把声明计数写成独立全枚举证据；完整小样本 oracle、短范围实际枚举与既有编译计数测试分别承担相应验证。

### 3.4 内存

| 运行 | guard 峰 Private bytes | guard 峰 WS bytes |
| --- | ---: | ---: |
| 修复前 1 | 915,378,176 | 853,061,632 |
| 修复前 2 | 1,039,339,520 | 977,731,584 |
| 修复前 3 | 1,021,779,968 | 958,967,808 |
| 修复后 1 | 794,791,936 | 762,712,064 |
| 修复后 2 | 527,958,016 | 541,310,976 |
| 修复后 3 | 918,224,896 | 924,205,056 |

六次正常结束且 guard 未触发；修复后三轮最高 Private **0.855 GiB**，不是内存无界的全曲 FIFO 对象索引。峰值受 GC 采样与堆高水位影响，不能把三次数字差距全归为产品收益。

累计 allocation 的例子：完整短范围读取中位数从约 154.6 MB 变为 260.4 MB，raw 区间首次编译约 128.8 MB；这与上述同时驻留峰值不是同一指标。不得据此要求用户准备累计 allocation 总和的物理 RAM。

被淘汰候选也记录在 `.tmp/pure-midi-range-repair/perf-after-r1` / `perf-after-r2`：早期一次使用通用视图查询计数导致秒级退化；改用端点索引后，热提前截断回到约 39 ms，但其冷截断仍 3,193 ms。进一步调查发现 raw NoteOff 全部晚于当前 end，错误地触发了前缀构建。最终代码增加精确范围门与独立 raw 分支。**不得把这个中间候选的热成绩当成已修好冷启动的证据。**

## 4. 当前构建的 WPF 大会话复验

使用最终 Desktop 回归 DLL 和既有 `eng/MemoryStage6WpfProbe`，不改探针中的音乐/选择/释放断言；参数 `2000000 5 3`，源仍为同一 9KX2。证据目录 `.tmp/pure-midi-range-repair/wpf-final/` 和 `wpf-final-guard/`。最终 `passed=true`、guard exit 0、无 stop reason，共 2895 次安全采样。

- 实际选中 1,382,908 个 Note，按既定同 start/key 碰撞规则发布 903,352 个 Logical Note；不是为了降内存主动缩小选区。
- 完成预取消和首次非零 Planning 进度取消、正式 Paste、五次新增/逐次 Undo、Paste Undo/Redo、三次视图循环、保留历史和视图 Save/Copy、两份文件分别重开及三次关闭。
- 峰 Private **3,195,703,296 bytes（2.976 GiB）**，峰 WS **3,163,283,456 bytes（2.946 GiB）**；前一 cleanup 候选两次峰 Private 2.669～2.679 GiB。本次单轮峰值更高，不能写成“内存完全不增”；没有出现逐次增长至 8～17 GiB 的旧现象，且关闭后的跟踪引用归零。不能用这一次差额精确归因全部原生内存。
- 三次关闭，六项 Raster gauge 均归零；受控 GC 后 11 类跟踪弱引用全部为 0，未等待 LOH compact 才释放。受控 GC 仅为诊断，不进入产品。
- Direct 源在两次重开均保留 14 Roots、40 Tracks、40 Segments、17,999,999 条成对 Note、3,484 条 Channel Event、0 Opaque；另含 raw NoteOn，所以正式 NoteOn 数为 18,000,000。结构数量门不冒充全部源字节的独立比较。

| 阶段 | 当前复验时间 |
| --- | ---: |
| 完整导入 | 22.825 s |
| Copy | 3.030 s |
| 5 次新增的同步命令 | 0.446 / 0.138 / 0.290 / 0.197 / 0.165 s |
| 5 次新增后的后台收敛 | 15.950 / 13.588 / 13.850 / 13.425 / 12.661 s |
| 5 次 Undo 的同步命令 | 11.41 / 2.63 / 2.64 / 1.53 / 0.77 ms |
| 5 次 Undo 后台收敛 | 14.429 / 13.542 / 13.699 / 13.656 / 13.909 s |
| Save / Save Copy | 41.507 / 37.029 s |
| saved / copy 重开 | 27.188 / 27.371 s |

后台包含默认空 Instrument / Overlap Reject 下的完整失败诊断处理；延迟与命令同步时间分开，不把 13～16 秒后台收敛说成 UI 同步卡住。与原阶段 6 大会话仍属同一量级，但这里只有一次当前构建复验，不据此给严格加速/退化百分比。

| 关闭后 controlled GC | managed live bytes | Private bytes | 11 类跟踪引用 / 六项 Raster |
| --- | ---: | ---: | --- |
| 原导入 | 11,510,488 | 883,474,432 | 全 0 / 全 0 |
| saved 重开 | 61,753,720 | 1,040,273,408 | 全 0 / 全 0 |
| copy 重开 | 61,831,152 | 1,142,292,480 | 全 0 / 全 0 |

这些 Private 高水位不是“旧 Project 仍活着”的证据，也没有用强制 GC 掩盖正常业务阶段的峰值。离屏真实 WPF 模板 / Surface / Desktop 会话验证，不是 computer-use，不代表显示器最终像素、鼠标输入时序或实际听感验收。

## 5. 剩余代价 / 未覆盖边界

1. 任意敌对 raw Off 与普通 Note 真正交错时，第一次建立精确前缀可能扫描该 Root 的长前缀并 spill；只有 bounded memory 保证，不承诺任意首次 seek 常数时间。缓存有上限，淘汰后可能重建。
2. FIFO 剩余来源查询从 NoteOn 后缀逐步向前；极端长时稀疏存活、交错页、超大 COW overlay 仍可能读更多页。不是仅凭一次 9KX2 就给任意数据的耗时上限。
3. 新的完整终点清理必然比遗漏它的旧版做更多工作；累计 allocation 不等于同时驻留。guard 峰值属于探针的多个 canonical 并存，不代表实际一次 Play 的最低 RAM。
4. 物理音频、SoundFont 峰内存、WASAPI deadline 和听感未在本轮验证；不声明它们通过。音频算法未变，但范围事件补齐可改变旧错误边界的听感，属于正确性修复。
5. 历史边界：本修复阶段曾按用户决定暂缓 Int64；后续已明确获准并完成 Int64 完整序列/条件分页，不再是当前限制。完整正式诊断仍不删除或近似；MIDI README 另按新决定输出前 1000 条及准确省略数。
6. 缓存 ABI 更新后，旧 Pure MIDI PCM 不再命中，第一次使用可能需要重新渲染；这不是持续不复用缓存，也不是删除或改写用户工程。

## 6. 定向人工复验

自动门已完成，建议用一个共享 Channel、音符与 CC/Program 分轨的工程检查：

1. 从头播放、从中途播放；CC/Bank/Program/Pitch 状态符合原曲，不补发起点前音符。
2. 把消费者范围/End Marker 放到延音中途，检查结束干净，无残音；重复开始/停止。
3. 做一次 MIDI/Logical 修改、Undo/Redo、保存重开，再做同样播放/导出。
4. 9KX2 中途及提前结束的首次等待、第二次等待是否符合报告量级，没有持续内存增长。

无需为本修复重做 A/B 全部 UI 验收。2026-09-09 用户明确确认阶段 6 原连续验收 C（MEM-C01～C07）全部通过，见 [验收记录](Midora-Memory-Stage6-Validation-Report-2026-09-09.md#6-人工验收-c)。该人工结论与本报告自动验证分开记录；上列建议不虚构独立测量数据，已披露的性能代价和延期边界保持不变。
