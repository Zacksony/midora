# 内存优化阶段 3：Opaque payload 所有权与预算

状态：实施及自动验证通过；完整统计与边界见 [阶段 3 最终报告](Midora-Memory-Stage3-Validation-Report-2026-09-08.md)。

## 需求追踪

- 依据：实施/验收计划阶段 3 的 M07/F05；SRS §16.30、INV-065、INV-095～096。
- 输入：合法 Opaque Meta/SysEx 的原始 payload、正式稳定 ID/顺序、冻结的 Project 选择。
- 输出：事件内容、ID、顺序、选择结果、只读属性、复制粘贴和 `.mpk` 字节保持不变。
- 失败/取消：中止本次读取/准备并释放借用与临时存储，不提交不完整编辑。
- 非目标：不解释更多 SysEx、不改声音或编译语义、不改 Format 3 / `.mpk`、不建立新的 payload 合法性上限。

## 所有权与预算

| 存储 | 归属 | 本次机制 | 边界 |
|---|---|---|---|
| source ID lookup | 每个不可变 source，其 revision snapshots 共用 | 只留 ID + source ordinal；不留 payload | 每源 65,536 项 / 8 MiB 保守准入上限；不是全进程 8 MiB |
| Opaque 虚拟对象页 | 本次调用方 | 同时按条数及 payload backing capacity 截页 | 最多 4,096 条且通常不超过 4 MiB；单条更大时完整独占一页 |
| selection reader | 本次 preparation | 有界排序 scalar ordinal，再逐个借用 payload | 同一迭代步只留当前事件，不建立选择规模的 payload 数组 |
| Opaque payload LRU | 进程级，可重建 | 共享不可变数组；按整个 backing array（包括隐藏 slice 区域）、数组头/对齐、目录实际 capacity 和保守节点开销计账 | 全进程最多 8 MiB / 8,192 项；超预算合法单对象直接返回但不进入 LRU |
| clipboard | clipboard 及仍在使用它的命令持有的 lease | descriptor 与原始字节写既有有界 resident/spill；reader 一次还原一个事件 | 沿用统一 64 MiB resident、64 MiB working、16 GiB spill；不把全部 Opaque payload 放入普通数组列表 |
| properties VM | 属性弹窗 | 仅保留标量和原有前 256 bytes 的 hex 预览 | 不再为只读展示创建或留存 mutable event facade；显示内容没有缩减 |

`RetainedBytes` 是固定 x64 目标下的 backing capacity 加保守元数据计账，不是 GC heap 测量。source ID cache 的总量是所有仍然存活 source 的计账之和；不能把每源上限当作全局上限。它随 source/snapshot/history 的最后持有者释放而可回收。

`BorrowedPayloadBytes` 单列本操作活跃 payload 的实际 backing capacity。同一 byte[] 的嵌套借用按身份只计一次。通常它属于源/解码页，也可能是 clipboard 或 PayloadBank 刚解码的单个返回值，因此不能声称全部都是零分配。它不是另一份 64 MiB 固定 working budget；单个合法大事件不会因为普通页预算而被拒绝。此设计也不声称整个进程具有固定内存总上限。

LRU 键只有 numeric source identity 与 ordinal，不保留 Project/source 引用；既有进程级 LRU 生命周期保留，Project 关闭后仍可能留存最多上述预算的热 payload，直到逐出或进程结束。活动返回值被调用者持有时，LRU 逐出不会使它失效。

既有 clipboard byte store 的总 byte count/offset 仍为 `int`；本阶段没有把它升级为超过 `int.MaxValue` 的单一 Opaque byte stream。因此 16 GiB 是 preparation spill 总预算，不是对单个 Opaque clipboard stream 可达到 16 GiB 的承诺。5 MiB 单事件测试针对 Domain source / PayloadBank 返回值，不改变既有 `.mpk` 编码页格式或其校验规则。

## 实现要点

1. ID 查询扫描只提取地址；命中后使用已有 source ordinal 索引读取对应页，禁止为每个 ID 命中重扫整个 source。
2. removed/replacement/added overlay 仍以冻结正式序列映射 ordinal；地址缓存不包含 revision-local 决策。
3. 冷查询可能先解析地址、再读取对应 payload。这是在不保留全部 payload 的前提下可能产生的额外解码；源页缓存和连续 formal-order 读取继续复用。
4. 虚拟页不拆分单事件。为判定下一项是否能放入本页，最坏可短暂持有本页与一个 look-ahead 事件；后者不留在结果页中。选择与 clipboard 使用单事件 reader，不经过这种整页 payload 暂存。
5. payload 缓存对相同 backing array 在不同键下的预算计账是保守重复计费，不会低报或额外复制其内容。单对象按原始 length 返回，slice 的前后隐藏容量只影响预算，不泄露到输出。
6. 取消检查覆盖选择迭代、clipboard 页复制、PayloadBank 分块解码。缓存/计账不进入实时音频线程。

## 自动验证门

- 96 × 1 MiB ID 查询：地址热命中不再次扫描；弱引用证明缓存没有留下 96 MiB payload。
- 96 × 1 MiB 虚拟页：4 MiB 页预算、完整 ordinal 覆盖；单个 5 MiB 值不拒绝。
- sparse 与 dense（至少 4,096 项）selection：逐项借用、正式顺序、全部释放；取消与未知 ID 原子失败。
- source 删除/替换/新增与旧 snapshot：ID/address/formal order 一致。
- ID cache 满准入量的 backing capacity；LRU 先灌满小项再用 MiB 大项替换，目录高水位仍计账。
- 只读 properties 原有 256-byte hex 预览、零 facade 留存；多个 slice 共享大数组按 backing capacity 去重借用。
- clipboard 原始 bytes、spill、最后 lease 释放及取消清理。
- 真正 `.mpk` 的 MiB payload → 选择 → clipboard → rewrite 逐字节相等；decoded cache 留存不超过其预算。

## 已执行证据

2026-09-08，`OpaquePayloadBudgetTests` 的 15 个测试均通过；与 ID cache、selection reader、clipboard storage 及最新 Content Pack builder/page 回归联合运行共 **69/69** 通过（12 秒）。结果：`.tmp/memory-stage3/payload-tests/payload-final.trx`。

该时间是整组测试时间，不能当作单操作性能。`eng/MemoryStage3Probe` 的 `opaque <count> <payload-MiB> <report.json>` 模式用于相同旧/新程序集的三轮 cold/hot、累计分配、GC 后留存及弱引用 payload 计数；最终耗时对比由阶段 3 总报告记录。

同机冻结基线/当前程序集的 96 × 1 MiB 探针结果（`.tmp/memory-stage3/opaque-old.json`、`opaque-new.json`）；耗时与累计分配均取三轮中位数，留存/查询/读取次数三轮一致：

| 指标 | 旧 | 新 |
|---|---:|---:|
| ID cache 导致仍存活的 payload | 96 MiB | 0 |
| 当前 descriptor 计账 | 无此计数器 | 7,348 bytes |
| cold 耗时中位数 | 19.22 ms | 17.45 ms |
| hot 耗时中位数 | 0.204 ms | 9.29 ms |
| cold 累计分配（约） | 96.08 MiB | 192.10 MiB |
| hot 累计分配（约） | 27.30 KiB | 96.06 MiB |
| cold / hot ID query 次数 | 1 / 0 | 1 / 0 |
| cold / hot payload 读取次数 | 96 / 0 | 192 / 96 |

这里的模拟 source **故意不自带 decoded page cache**，以隔离 ID cache 的责任；每次 ordinal 读取都会重新创建 1 MiB 数组，因此属于热读的最差无缓存源情形。新实现消除 96 MiB 常驻，但在这组极端输入中每次遍历多约 9 ms、累计分配增加；这是可以观测到的取舍，不能隐藏或用“总体更快”概括。真实 `.mpk` 仍复用有界 decoded LRU，不能把该热读倍率直接套到正常 Note/CC 操作或实际文件。三次 cold 波动较大，只记录中位数，不据此宣称性能提升。

读取次数的变化不是热命中重新扫描整个 source：旧版 cold 的一次 ID query 读取 96 个 payload 并全部留在 ID cache，hot 因而无需再次读取；新版 cold 的一次 ID query 读取 96 个事件以解析地址，然后按这 96 个 ordinal 重新取得 payload，共读取 192 次。新版 hot 保留地址命中，ID query 为 0，但仍需要逐 ordinal 读取 96 个 payload。累计分配是这一整次遍历中先后产生的数组总量，并非同时留存量或峰值。三轮新旧 checksum 均为 `100679040`；逐字节内容保证另由真正 `.mpk` 的集成测试验证，不由 checksum 代替。
