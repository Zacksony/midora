# 内存优化阶段 3：验证与时间代价

日期：2026-09-08。范围：M06、M07、M09、M10。状态：**阶段 3 工程门通过，2,935 项公共回归通过；不进入阶段 4。**

本轮由用户单独授权实施阶段 3；实施时不提交/推送。2026-09-09 用户另行授权提交、推送本阶段成果；仍不实施阶段 4、不本地发布、不使用 computer-use。正式 Project Format、音乐/编译/音频语义、现有编辑及渲染精度均保持不变。

## 1. 已实施项与资源归属

| 项 | 实际修改 | 上限与所有权 |
| --- | --- | --- |
| M06 | 范围 canonical、realtime、sample 三族历史计划改为共享字节 LRU；不可变 backing 共享；活动消费 lease | 每 session 独占缓存合计 128 MiB / 256 项。当前 canonical baseline、活动计划、共享部分分别计账；合法大计划可用但不进入缓存 |
| M07 | ID cache 只留地址；Opaque 页同时按条数和实际 payload capacity 分页；选择/剪贴板逐项借用；Properties 不再留 mutable facade | 每源 descriptor 65,536 项 / 8 MiB；进程 payload LRU 8 MiB。通常页 4 MiB / 4,096 项，大单项完整交付；现有 clipboard int offset 限制未扩大 |
| M09 | active / partial / pending 页实际数组容量纳入预算；小 tail 按需增长；压力下 owned spool，输出顺序不变 | 每 writer 64 MiB 页工作数组；spool 扩容槽复用、校验、取消/失败清理。不是所有 writer、元数据、Brotli/native 的全局预算 |
| M10 | 稀疏 ushort 容器到密集 bitmap 自适应；超预算有界外排；保持全包重复校验及原来源顺序 | 每 validator 8 MiB，含扩容与迁移预留；外排活跃记录空间 ≤48×已接收 ID 数加 owner 元数据，旧 run 及时清理 |

补齐的生命周期细节：计账目录低水位缩容；Session Dispose 清掉旧 canonical 强引用及后台镜像引用；消费者持有的旧计划不被缓存淘汰提前销毁；Held Preview 同步替换窗口同时计账新旧计划。主路径及取消/失败仍由原状态机控制，不在 audio callback / synthesis / mix 线程引入预算处理。

设计记录：[总设计及 lease ADR](Midora-Memory-Stage3-Budgets-and-Leases-Design.md)、[Opaque](Midora-Memory-Stage3-Opaque-Payload-Design.md)、[PageBuilder](Midora-Memory-Stage3-PageBuilder-Design.md)、[Stable ID](Midora-Memory-Stage3-Stable-ID-Validation.md)。

## 2. 测量方法

- 冻结旧基线：阶段 2 提交 `4305ad3`；改动前构建到 `.tmp/memory-stage3/baseline`。新实现独立构建，不覆盖 baseline。
- 同一 `eng/MemoryStage3Probe` 源码分别引用旧 / 新 DLL；SDK 10.0.400、运行时 10.0.11、Windows x64、12 logical processors、workstation GC。所有重型测试/探针串行。
- 数值单位 MiB/GiB 均为二进制。GC 后 retained、累计 allocated、actual rented capacity、Working Set 是不同指标；特别是旧 tail 数组大多尚未触及，不能把它们的 GC heap 大小直接当成已使用的物理 RAM。
- `plans` / `packs` 为各 fixture 单次进程测量；IDs / Opaque 每组 3 轮中位数。JIT、GC 和 OS page cache 会影响计时，不承诺稳定百分比提速。
- 原始 JSON/TRX/CSV 放在 ignored `.tmp/memory-stage3/`；本报告保留关键结果，探针源码及 [运行说明](../eng/MemoryStage3Probe/README.md) 可复现。正式代码不主动执行探针中的 GC。

## 3. M06：范围计划平台与重访代价

合成输入为 1,000 个逻辑音符，每个新范围紧接一次同范围热读，再建立 100 个采样率计划。初始 canonical 约 6.20 MiB GC heap。

最终同条件 128 范围测量（包含生命周期补修及重访断言）：

| 指标 | 旧 | 新 |
| --- | ---: | ---: |
| 128 范围后 GC heap | 462.03 MiB | 132.51 MiB |
| 再加 100 个采样率后 GC heap | 544.60 MiB | 132.74 MiB |
| 全探针峰值 Working Set | 2,110.13 MiB | 679.70 MiB |
| 冷准备 median | 71.64 ms | 67.49 ms |
| 冷准备 p95 | 140.62 ms | 133.67 ms |
| 当前范围热读 median | 0.0014 ms | 0.0034 ms |
| 重访 tick32 早期范围 | 0.004 ms，仍缓存 | 57.36 ms，已淘汰重建 |
| 总测量时间，含探针 GC | 13.35 s | 12.83 s |

新版本扩大至 **1,000 范围＋100 采样率**：64 范围后 GC heap 稳定在约 131.59～132.54 MiB；独占 cache 计账约 125.37～126.35 MiB，始终 ≤128 MiB；范围缓存保留约 71 项，加入采样率后约 125 项、淘汰1,975次，没有按历史范围数持续增长。cold median 57.02 ms、p95 76.09 ms，hot median 0.0025 ms；随后重访tick250为64.33 ms，fingerprint与5,987,500 frames均和原结果一致，重访后累计淘汰1,977次。这组不能与旧128范围的median当作严格提速比。

缓存的时间取舍是：离开工作集的旧范围再次访问需要重建，而不是永远命中；额外计账也让微秒级热读略增。新旧128范围的重访结果均为fingerprint `2565875705491910586`、5,998,400 frames；证明重建没有改变正式输出。首轮及最终复测的冷准备处于同一量级，均未把被淘汰重访偷计为热命中。

该预算不限制合法活动计划本身，也不限制正在编译的瞬时分配。本轮 1,000 范围累计分配约 60.1 GiB，但 GC 后存活量受控；二者不矛盾，也不代表消除了编译分配成本。更多消费者/历史所有权优化留给后续已批准阶段。

### 真实分页 MIDI 的时间复核

9KX2另做同一旧 / 新探针的分页realtime准备，不读取SF2、不启动Worker、不把18M音符转成100份离线事件数组。

最初4范围测量cold median为167.49→191.06 ms（+14.1%），因此按10%调查门继续扩大到32范围，未直接宣布无退化：

| 32范围 | 旧 | 新 |
| --- | ---: | ---: |
| cold median | 117.81 ms | 122.23 ms |
| cold p95 | 182.19 ms | 169.03 ms |
| hot median | 0.0133 ms | 0.0175 ms |
| 总准备测试时间，含GC检查点 | 4.018 s | 4.054 s |
| 累计分配 | 1,660.17 MiB | 1,665.00 MiB |

较多范围下，median增约4.42 ms / 3.75%，总耗时增约36 ms / 0.9%；累计分配增约4.82 MiB，符合新增描述/计账的准备期成本。4范围结果包含较强启动阶段变化，不能外推成稳定14%退化；32范围也不被包装为多次完整进程的统计承诺。热点明确仍是原范围准备而不是UI或音频回调，未为追求此探针速度恢复无限缓存。

新32范围保留64项、cache计账69.64 MiB；尚未超过工作集，因此无需淘汰。两版重访tick768均为fingerprint `-2923026293311244488`、17,768,960 frames。较少范围内新heap不一定更小：额外描述表/计账，以及导入时不同pool高水位仍有成本。本轮收益是受压时的平台，不是所有输入都绝对降低每一个内存指标。

## 4. M09：尾页、实际容量与字节等价

| 输入 | 旧未完成页 GC heap 增量 | 新增量 | 旧 Add+Complete | 新 Add+Complete |
| --- | ---: | ---: | ---: | ---: |
| 1 Segment × 1 Note | 2.070 MiB | 0.134 MiB | 62.88 ms | 72.91 ms |
| 100 Segment × 1 Note | 194.019 MiB | 0.447 MiB | 87.39 ms | 94.60 ms |
| 1,000 Segment × 1 Note | 1,939.006 MiB | 3.284 MiB | 514.23 ms | 308.73 ms |
| 1,000 × 120 Note，混合 Channel/Opaque | 2,752.870 MiB | 27.342 MiB | 1,214.14 ms | 1,015.57 ms |
| 1 Segment × 70,000 Note，混合事件 | 18.967 MiB | 23.451 MiB | 269.60 ms | 252.61 ms |

正常 64 MiB 下，1,000 小 Segment 的 active 数组为 2,048,000 B；混合样本实际数组峰值 24,522,896 B，均无需 spool。单个大 Segment 的 heap 反而增约 4.48 MiB，来自有界 pending 归属及 ArrayPool 高水位；未宣称所有输入都会降低 heap。

同一混合样本把**内部测试预算**压至 9 MiB：实际峰值与 reservation 峰值均 9,437,184 B；9,494 次 spill、22.36 MiB spool 高水位；用时 **2.28 s**，比默认预算的 **1.02 s** 慢。这是明确的 I/O 代价，正式默认仍为 64 MiB。

11 个独立 probe 输出中，所有对应旧 / 新文件的完整 SHA-256 相同，9 / 64 MiB 也相同，覆盖满页和 Complete 尾页顺序。具体 hash 表见 M09 专项文档。spool 损坏、取消、IO 失败、extent 重用和实际租用容量有独立断言。

## 5. M10：稀疏改进与密集代价

| 输入 | 旧 GC retained | 新 GC retained | 旧 median | 新 median |
| --- | ---: | ---: | ---: | ---: |
| 1,000 sparse，ID 间隔 2^20 | 125.08 MiB | 约 105 KiB | 21.13 ms | 0.63 ms |
| 18,000,000 dense | 2.25 MiB | 2.17 MiB | 225.13 ms | 280.70 ms |

密集例局部校验约慢 **55.6 ms（约25%）**，自适应初期的累计分配也由约 2.25 MiB 增至 6.55 MiB；最终 bitmap 大小相近。这个代价是 validator 本身，不等于整个项目 Save/Open 慢25%。

新版本 80,000 sparse：median 172.18 ms，resident 计账峰值 **8,388,536 B ≤8 MiB**；spill 峰值 3,840,000 B、累计写入 5,412,864 B。未运行旧版 80k sparse，以免刻意制造已知约10 GiB 位图分配。

## 6. M07：大 payload 留存与重新读取

无自身 decoded cache 的模拟源，96 个事件、每个 payload 1 MiB：

| 指标 | 旧 | 新 |
| --- | ---: | ---: |
| ID cache 留下的 payload 弱引用存活字节 | 96 MiB | 0 |
| 热读后 GC retained | 约96.02 MiB | 约18 KiB |
| 冷查询 median | 19.22 ms | 17.45 ms |
| 热查询 median | 0.204 ms | 9.291 ms |
| 冷 payload 读取次数 | 96 | 192 |
| 热 payload 读取次数 | 0 | 96 |
| 冷 / 热全源地址扫描次数 | 1 / 0 | 1 / 0 |

新版本只留7,348 B计账 descriptor，不钉住96 MiB原字节。冷路径需先定位地址再取值；超工作集热查询不能继续依赖旧的无限留存。此最差源的冷累计分配约从96 MiB增至192 MiB，热从约27 KiB变为96 MiB，必须如实承认；真实 `.mpk` 有现存64 MiB decoded LRU，不能把这个无缓存源的时间直接外推到普通小事件。

真实 pack 的 MiB payload → selection → clipboard → rewrite 已做 bytes equality；slice capacity、超过普通页预算的合法单项、旧 snapshot / overlay、稠密及稀疏选择、取消与最终 lease 释放均有专项测试。只读 Properties 的原有标量及256-byte hex预览保持不变。

## 7. 真实 9KX2 往返

复用阶段1同一无UI探针，引用阶段3 candidate；不是整套真实 WPF 端到端测试。

- 样本 `9KX2 18 Million Notes.mid`，144,175,201 bytes；SHA-256 `948fdddfff2f050c1b4e70f240b6138e5ba10939030a82286e149ddcb93e6249`。
- 完成 Import → 60k 编辑准备/提交 → Undo/Redo/再次Undo → 保存 → Save Copy → 重开。
- 重开仍为 **17,999,999 Note、40 Track、0 damaged Track**；没有把只读路径退回全源 mutable facade 物化，三个 Direct 集合对应表计数维持0。
- 导入约18.69 s，Save约32.81 s，Save Copy约31.02 s，重开约14.45 s；整探针104.11 s。保存包括正式预检、严格写入和自校验，未删减验证来换速度。
- 峰值 Working Set **854.94 MiB**；导入后GC managed101.79 MiB，原/重开两个Project同时存活时180.81 MiB；退出对象作用域后约20.08 MiB，两个Project弱引用均已释放。
- 这是本轮新版本单次运行；没有据此宣称相对阶段2的完整流程百分比提升，也不把855 MiB写成用户实机最低内存需求。

## 8. 最终自动回归记录

公共回归 **2,935 / 2,935 通过**；0 failed、0 NotExecuted。串行、Release、逐测试项目隔离输出，目录 `.tmp/memory-stage3/regression-final/`。

最终 Application Release及独立Worker常规build均为0 warning / 0 error；Worker只构建到 `.tmp/memory-stage3/worker-build/`，没有Native AOT publish、没有`dist`产物。新增Common项目依赖及相应lock元数据已更新，未升级第三方包或产品版本。

| 测试项目 | 最终通过 |
| --- | ---: |
| Common | 81 |
| MIDI | 29 |
| Application | 1,081 |
| Compiler | 437 |
| Persistence | 129 |
| MIDI Export | 43 |
| Playback | 140 |
| Audio Render | 37 |
| Presentation | 445 |
| Desktop | 403 |
| Managed audio 限定门 | 110 |

Playback 包含本轮新增的23项 cache/Session生命周期测试：目录旧 backing 真正可GC、baseline释放、外部lease继续有效、取消、Held Preview替换窗口及失败清理。托管音频覆盖实际 ring、shared control、PCM IO、Limiter、WAV 写入和 plan / event-stream 协议的既有零分配/字段一致性断言；不等同 native音频实机验证。

已完成的定向门：M07及相关联合69/69、M09最终27/27、Stable ID18/18；它们存在交集，不与公共回归简单相加冒充独立用例总数。

首轮 Application 跨进程测试发现测试启动器按目录名推断 `Configuration`，与隔离 OutDir 不兼容；修为 `dotnet vstest` 直接执行当前程序集，保留真正跨进程OS namespace断言。完整 Application 补跑 **1,081/1,081** 通过，首轮失败TRX保留；这不是跳过失败或提高超时阈值。

新增 lifecycle 测试先有一次 `ReadOnlySpan` 断言重载编译错误，修为 `IsEmpty` 检查。其后两项最后reader弱引用测试中，测试方法直接取得 `LastAttempt` 产生的临时引用被JIT保活；把取得lease隔离到 `NoInlining` helper，并额外断言 disposed lease 的描述引用为null后，完整Playback补跑 **140/140** 通过。未加等待/重试、未放宽GC断言、未为通过这两项改产品逻辑。

最终统计使用 `Midora.Application.Tests.rerun.trx` 和 `Midora.Playback.Tests.rerun.trx`，其余使用对应原TRX。首轮 Application 1项失败、Playback 2项失败的记录仍在目录中，不混入最终通过统计，也不删除以伪装首轮全绿。

## 9. 替代方案、边界与下一步

按计划对超过10%的局部时间变化单独归因，而非直接接受一个百分比：

- **密集ID**：新增稀疏容器探测、向bitmap迁移、扫描ordinal及取消/预算检查，带来已测约56 ms。保留原大位图会恢复稀疏125 MiB/1,000 ID问题；全量HashSet会使18M密集输入远大于当前约2.2 MiB；所有ID一律外排又会让正常密集样本写入至少数百MiB记录。因此保留自适应方案，不取消校验来换时间。后两种为结构成本分析，没有伪称已跑出替代方案耗时。
- **Opaque热读**：不再留住超工作集所有payload，必须重新读取。扩大缓存只能移动阈值，不能消除任意payload规模的重访代价；单次地址解析同步交付payload可进一步减少冷重读，但涉及统一ID-query排序/返回接口，本轮不擅自扩大。当前保留索引查询与实际decoded LRU，超大压力源热操作新增约9 ms，非UI每帧操作。
- **PageBuilder压力**：不得通过提前Flush改变正式pack页顺序；因此选择可校验spool，而不恢复按Segment数量无限预留大数组。9 MiB压力用时增加至2.28 s；默认64 MiB无spool时，已测混合规模为1.02 s。小fixture的7～10 ms差异含冷JIT，单次结果不能归结为普遍退化。
- **范围缓存**：无限历史命中不可与有限保留兼得；128 MiB在合成样本能保留约35套range+plan、当前canonical共享不重复收费。提高到256 MiB可扩大热集但也提高驻留量，不能保证任意跳转永不重建。本轮选择128 MiB并公开57～64 ms重访成本；没有给拖动/渲染缩减缓存或增加冷页等待。

本轮不承诺整个应用“固定128 MiB”或任何单一最低RAM要求：当前Project/分页源、基线canonical、合法活动大计划、并发writer、GC/native/WPF和用户保留历史仍有各自归属。默认性能优先原则保留，不减少渲染精度或正常Undo来压低数字。

未运行 native设备/SF2/AOT音频集成，也未重新做人眼UI全量验收；托管回归与文件等价不能冒充这些验证。公共测试内需要显式环境变量的巨大样本case未自动开启，真实大样本只按第7节报告。

按既定六阶段计划，阶段3的人工结果合并进**阶段4后的验收B**。本轮列出的四项已实施；M08/M11（阶段4）、History（阶段5）及最终长流程（阶段6）没有擅自提前实施。
