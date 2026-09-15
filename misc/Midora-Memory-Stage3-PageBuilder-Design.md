# 阶段 3：PageBuilder 容量与未满页暂存设计

状态：已实施，定向自动验证通过；不是新的 SRS 或项目格式。

## 需求追踪

- 依据：内存阶段计划 §8 / M09、SRS §16.30.3～6、§16.34、INV-020 / 030 / 065。
- 输入/输出：相同顺序的 Note、Channel Event、Opaque record；输出原版本 `.mpk` 字节。正式满页 Flush 时机、Complete 时 SegmentId/Kind 尾页顺序、endpoint sort、Brotli 压缩级别、checksum 全部不变。
- 非目标：不调整音乐模型、ID、wire、页边界、压缩算法、音频或 Undo；不移除保存后自校验。
- 失败/取消：临时 pack 不发布，清空 builder 和编码任务后关闭 owned spool；不触及来源。沿用现有取消与异常诊断通道。
- 持久化归属：spool 不进入 Project/`.midora`；仅 writer 生命周期。

## 容量与调度

沿用 writer 原有 64 MiB 工作预算；将 active/incomplete 的实际 MemoryStream.Capacity、实际 endpoint ArrayPool 数组长度乘结构体大小与 pending 页的容量保留值共同计费。endpoint builder 不再创建无用途的字节流，初始 endpoint 租用 16 个元素；普通 stream 初始为零、按需至少 256 bytes 并倍增。

数组扩容期间旧/新同时存在也预留。pending 保留值计实际 endpoint 输入数组、decoded pool bucket、compressed pool bucket及扩容旧数组；不是仅按有效 record count 估计。另以 `PendingActualBufferBytes` / `PeakActualBufferBytes` 对每次真实 pool 租用长度独立计数，包括 compressed grow 的 old+new；测试要求真实峰值不超过保留值，Complete/Dispose 后实际活动容量为零。

不足时先顺序收割既有 pending，再将非当前 builder 的 incomplete 数据存入 owned spool。不提前 Flush、不改变页面输出顺序。内部测试可使用低于单个合法最大页编码峰值的预算：此时其他驻留工作全部释放，只允许这一页的有界工作峰值暂借；不新增合法内容拒绝。正式 64 MiB 预算大于单页峰值。

## Spool 所有权

首次实际压力下才使用 `MidoraOwnedTemporaryDirectoryLease` 在 `<ProgramRoot>/.tmp/CompilerRuns` 创建 `content-build` run。一个 `DeleteOnClose` 文件，非 endpoint 保存已有 wire 字节，endpoint 暂存当前进程内结构体原始记录，恢复后仍按既有 comparer 排序/序列化。没有进程间或持久兼容契约。

Extent 容量取二的幂，按容量复用已归还 extent；同样大小尾页反复换入换出不永久追加。每个有效 extent 保存 SHA-256，恢复时校验，避免临时内容损坏后被重算正式 page hash 掩盖。磁盘的高水位与曾同时需要的各容量桶槽位有关，不是导入持续时间；按实际 Length 观测。取消/Complete/Dispose 关闭文件及 lease，强制终止后由通用 manifest/lock 回收。

## 不包含在页数组预算中的资源

64 MiB 是**每个 writer 的页工作数组预算**，不是整个进程所有字节的硬上限。下列资源必须单独报告，不能包装成已被这一常量覆盖：

- builder/ordinal 字典为 O(非空 Segment × kind)；page directory 为 O(page count)。Complete 后清空 writer 已不用的结构与数组，不再因外部仍持有 writer 而延续。
- 一个 output FileStream 的固定 128 KiB I/O 缓冲，以及固定数量编码器的 Brotli/Task 运行时开销；回收到共享 ArrayPool 的空闲数组也不能当成“物理内存立即归零”。
- spool extent 索引为 O(各容量桶同时使用槽位的历史峰值)；文件 Length = 各桶 capacity × 该桶已分配槽位数之和。相同尾页反复访问复用槽位而不按访问次数增长；最宽松上界受 builder 数和单页 4 MiB/endpoint 上限限定，不能声称固定 64 MiB 磁盘预算。
- MIDI streaming import 在一个 source MTrk End 时已经 Complete/Dispose 它的全部 target writers，不保留过去 MTrk 的 active tail；但同一 source MTrk 拆到多个 Port.Channel 时可以有多个 writer。同类预算相加，不宣称它们共享一个全进程 64 MiB 池。本轮不擅自添加全局硬容量拒绝。

## 验证门

1. 1 / 100 / 1,000 个单 Note Segment 的 actual capacity、zero pending 和 Complete 后释放。
2. 大量未满尾页压力、重访、endpoint/opaque 混合；active+pending 平台、spool复用与清理。
3. 不同预算和并发下逐字节相同；以冻结旧版 Domain DLL 再验证物理字节不变。
4. encoder fault / cancellation / spool 创建失败保持未发布文件删除。

## 实测结果（2026-09-08）

使用同一 `eng/MemoryStage3Probe` 源码，分别链接冻结的阶段 2 基线及当前候选 DLL，每组各启动一个独立进程，顺序执行、不并行跑重型测试。尾页 heap 为添加完成、Complete 之前主动 GC 后相对初始值的增量；这是探针测量，不在正式代码主动 GC。耗时只计 Add + Complete，不计探针 GC、SHA-256 文件比对；这些是各组单次冷启动测量，不是多轮中位数。

| 输入 | 旧尾页 heap | 新尾页 heap | 旧峰值 Working Set | 新峰值 Working Set | 旧 Add+Complete | 新 Add+Complete |
|---|---:|---:|---:|---:|---:|---:|
| 1 Segment × 1 Note | 2.070 MiB | 0.134 MiB | 29.18 MiB | 29.11 MiB | 62.88 ms | 72.91 ms |
| 100 Segment × 1 Note | 194.019 MiB | 0.447 MiB | 50.53 MiB | 30.68 MiB | 87.39 ms | 94.60 ms |
| 1,000 Segment × 1 Note | 1,939.006 MiB | 3.284 MiB | 249.74 MiB | 43.22 MiB | 514.23 ms | 308.73 ms |
| 1,000 Segment × 120 Note + 2 Channel + 2 Opaque | 2,752.870 MiB | 27.342 MiB | 460.92 MiB | 79.25 MiB | 1,214.14 ms | 1,015.57 ms |
| 1 Segment × 70,000 Note + 2 Channel + 2 Opaque | 18.967 MiB | 23.451 MiB | 54.88 MiB | 58.18 MiB | 269.60 ms | 252.61 ms |

以上新版本均使用正式 64 MiB / writer 预算。1,000 单 Note Segment 的 active 实际数组仅 2,048,000 bytes，无 spool；1,000 × 120 混合样本实际数组峰值 24,522,896 bytes，reservation 峰值 24,573,072 bytes，无 spool。所有样本 Complete 后 writer active capacity 均为零。

不能把旧 heap 数字说成旧进程实际物理 RAM：旧端点数组绝大多数未触及，Working Set 明显小于托管保留容量。单个大 Segment 测试的新 heap 高约 4.48 MiB，反映异步页输入更早归属于有界 pending、及共享 ArrayPool 不同尺寸的高水位；不是所有输入都能减少托管 heap。本轮重点消除小尾页倍增和漏计预算，不承诺每个场景绝对更省。

### 强压与确定性

同一 1,000 × 120 混合样本，内部测试预算降至 9 MiB：

- actual 和 reservation 峰值均为 **9,437,184 bytes**，没有超过测试预算。
- active 尾页 9,430,014 bytes，尾页 heap 13.005 MiB（元数据/运行时不算进数组预算）。
- 9,494 次 spill、spool 高水位 23,447,552 bytes；Complete/Dispose 后 owned spool 清理。
- Add 1,427.88 ms + Complete 852.95 ms = 2,280.83 ms，较正常 64 MiB 的 1,015.57 ms 慢。此处是明确的强压 I/O 取舍，不能隐藏为“内存优化没有时间代价”；正式默认不触发这组尾页的溢写。

所有对应的旧/新 `.mpk` **完整文件逐字节 SHA-256 相同**，包括满页输出及 Complete 尾页；低预算/高预算也相同：

| 输入 | SHA-256 |
|---|---|
| 1 × 1 | `6503D15D52B228106F9B851CA82914BE26CA9A178CE77CEB036B658EB325F521` |
| 100 × 1 | `4792F20D351B7B9966A2E7D64266C68EDB0A8D6A416724C63701C0519AFFFF9A` |
| 1,000 × 1 | `2B87619B95014335D62DA4830155271E409F1A7A44CB6A78F34DAE9D9CF3B7C8` |
| 1,000 × 120 混合；新版本含 9 / 64 MiB | `D9164FF543885E399ECFBA487A4ECCF8B23EF739DAD3A9C33F384232CA4DB686` |
| 1 × 70,000 混合 | `4F9689BE981A08394AE6C1502351124DAF7605E9A2B5B72E6F68BF99DAB19516` |

### 自动验证与复现证据

- `ContentPackBuilderBudgetTests` + 既有 `PureMidiContentPackTests`：**27 / 27 通过**。涵盖 tiny tails、预算/并发 byte equality、spool 重访容量复用、取消、I/O 注入失败、spool 损坏拒绝、最大合法 Opaque、Opaque 解码真实容量计费及超预算单页不缓存。
- Application Release 全构建：0 warning / 0 error。最终定向测试 TRX：`.tmp/memory-stage3/builders-final/builders-final.trx`。
- 同条件原始测量：`.tmp/memory-stage3/builders-measured/*.json`，11 个独立进程结果；这些 ignored 文件不是正式交付依赖，复现入口为源码 probe。
- 冻结旧 Domain SHA-256：`BA88F98828625797D8FFC7C1DFA116C842B4C2331AB96C68D6EF8D37307EB862`；本次候选 Domain：`AB8584957C07AFB0227B629B612EAA49CEA1C22D2378F7AA00A2523808778D00`。
- 这是 Domain/文件/资源生命周期测试，不代表做过真实 WPF 端到端或大型 MIDI 全流程时间验收。
