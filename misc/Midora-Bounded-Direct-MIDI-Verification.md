# Direct MIDI 有限内存批量编辑：实现与验证记录

日期：2026-09-06。此文档是实施证据，不改变 SRS。新增结构、百万音符资源门及最后的正式目录/范围查询/进度取消回归均已通过。

## 已实施的路径

- Notes / Channel Events / Opaque Events 使用共享不可变源与稀疏 COW 根；旧快照和 Undo 保持旧根，不为准备镜像重新创建所有可变音符/点。
- 音符移动、左右长度修改、水平/垂直翻转、Scale、Transpose、Batch Edit、Humanize、Split、Join、Quantize、属性、Velocity、删除、复制拖动与粘贴，以标量记录外排计划并一次发布根。
- Channel Event 的移动、复制拖动、属性、水平/垂直翻转、Scale、Batch Edit、Quantize、画线、删除和粘贴采用同类管线。碰撞查询仅处理编辑目标键；一个点命中大量原有重复事件仍采用有界处理。
- Opaque Event 移动、复制、删除仅调整标量 metadata；不为改变 Tick 复制 payload。复制粘贴与结构复制的 payload 按有界页写入，单条新 payload 亦受 working 预算准入。
- Arrangement 的 Direct Segment 内容变换、左边界扩展所需全内容平移、Segment/Track 复制以及 Segment Split 均使用 scalar source；大内容的构建发生于 detached 准备，而非 UI 发布。左扩展与结构复制保留既有同键重复，不错误套用音符编辑碰撞规则。仅变更 Segment 窗口且不移动内容不遍历其音符。
- 大集合的源 ID 查询按页批量执行；密集选择按 ordinal 页顺序读取后使用已有选择集合判断成员，避免随机 ID 顺序轮番淘汰磁盘索引页。计划中已持有值时使用 `ResolveValues`，不再丢弃值后逐 ID 回读同一记录。

不把“小于 4096 个 opaque 事件”当作小内存集合：单个 payload 可达 MiB 级，原有整批 `ToArray` 快路径可能造成大量复制。因此 opaque 编辑统一使用 metadata 根；普通固定大小音符/Channel Event 的有限小集合仍保留低开销路径。

## 已测正确性与性能

测试为 Release、源码自动化测试，不使用 computer-use。以下时间来自不同独立测试或带并行测试的运行，不能互作严格 A/B 性能提升百分比。这里没有新增 9KX2 实机 GUI 操作计时。

| 项目 | 实测结果 |
| --- | --- |
| Direct / ExactCollision / BulkScalability / 两个 Extreme 覆盖组 | 34/34 通过 |
| Segment 变换、COW 根、选择变换覆盖组 | 27/27 通过 |
| 最终 ExactCollision、Direct Dense 结构、旧 Split、Logical Command / Progress / Cancel 覆盖组 | 43/43 通过，9.497 秒 |
| 60,000 条真正冷 ContentPack 音符：Resize 准备及快照/Undo 路径 | 0.883 秒；源解码页 miss 1 |
| 60,000 条内存音符：Resize | Prepare 1.129 秒；Apply 0.0093 ms；Undo 0.0045 ms；Redo 0.0019 ms |
| 60,000 个 Channel Event：移动、快照、Undo | 1.189 秒 |
| 60,000 个 Opaque Event：移动、快照、Undo | 0.512 秒 |
| 60,000 音符 Split 为 120,000 音符 | Prepare 6.219 秒；Apply 2.4 ms；Undo 0.6 ms；Redo 0.1 ms |
| 1,000,000 条真正冷 ContentPack 音符：Resize，含最终 spill | Prepare 22.367 秒；Apply 0.014 ms；Undo 0.010 ms；Redo 0.003 ms |

上述 Split 的准备线程累计分配为 393.33 MiB；它不等于峰值存活内存。该测试读取的 managed heap 从约 53.5 MiB 到 244.4 MiB，Undo 后约 100 MiB；还不能把此数值解释为应用的总 Working Set 或最终历史保留预算。

### 百万冷源的明确资源结果

`MillionColdPagedNotesResizeWithinPreparationBudgetsAndPublishConstantSizeRoots` 通过：以真正的 ContentPack writer 写入百万记录，随后冷页读入；选区使用可重放连续 ID 枚举，不预建百万可变对象或 ID 数组。准备包括 `lease.Complete` 最终落盘，发布后显式 `MarkPublished`。

- working 峰值 7.97 MiB；resident staging 峰值 64.00 MiB；owned spill 峰值 912.61 MiB。
- 发布前 resident staging 为 **0 byte**；Apply / Undo 本线程托管分配均为 **0 byte**。
- 源解码页 miss 为 16；准备线程累计分配 1908.52 MiB；观测 managed heap 从 79.93 MiB 到 322.67 MiB。
- 完整准备耗时 22.367 秒，不能把 root 发布的微秒级时间当成整次编辑耗时。这份结果证明有界 RAM 和低成本 Undo，但同样显示百万级外排/索引准备仍有明显时间代价。

包含资源门的 focused 组最初 56 项中 55 项通过；唯一失败为既有 `MidiSegmentSplitPartitionsEveryDirectEventKindAndIsExactlyReversible` 在根发布后仍读取旧 Track 目录。测试已改为从正式 Project 按稳定 ID 获取现 Track，并在最终 43/43 的重编运行中通过，完整内容、精确分割与 Undo/Redo 断言均保留。

最终取消测试在结构复制的真实标量记录进度回调触发取消，验证正式 Track、Segment、NextStableId 不变，未发布 Resident / Spill 都回到 0。Track 和多 Segment 复制共用整个操作的记录总数；Split 按实际两遍分区读取的总记录数报告，不按计时器伪造百分比。

另补足 exact-key 的根因：未知 source 的最大末尾 Tick 原本在快照构造时通过全范围扫描取得，使只创建一个 Note 的操作也会扫描不相关源。现在仅在消费者确实需要范围时求值；真实 ContentPack 仍使用已有常量时间 bounds。cache-only 首次缺少这类 bounds 时返回 Pending，不缓存异常；后台求值后 cached-only 可恢复。测试仍要求创建只触发一次精确目标键查询，没有放宽为允许全源扫描。

### 实际 WPF 选择几何回归

`SelectionPresentationLifecycleTests.PagedHorizontalFlipRetainsLargeSelectionRenderGeometry` 使用 1,000,000 条 ContentPack 音符，真实 WPF projection、选择几何与红色选择渲染。新根发布后原本在固定 10 秒几何等待内超时；修复随机 ID 读取顺序后通过，**没有延长测试等待限制**。整项耗时 31.938 秒，包含百万源构建、编辑与验证，不等于渲染帧耗时。

原因不是选择丢失，而是选择 ID 的无序访问反复读取两个 spill 索引；仅延长超时或让 UI 同步读页都会掩盖该问题。修复使用密集集合顺序读取，并在后台预取中明确预热 cached-ID 入口依赖的 ID / ordinal / rank 页。

## 资源约束的含义

按既定命令预算执行：64 MiB working、64 MiB resident staging、16 GiB owned spill；正式发布前 `BoundedEditResourceLease.Complete` 将新页和辅助索引 resident staging 落盘。结果与历史保留共享的不可变根和 owned backing；读取页使用进程共享的有界 LRU，不是为每个 Undo 永久保留 64 MiB RAM。

纯 MIDI ContentPack 的解码页上限为 4 MiB，opaque 单条记录必须能容入该页。运行时新建的 payload 另做 working 大小准入；越限在正式音乐数据修改前失败。Opaque 缓存另受 8 MiB 和 8192 项双上限约束。

这些限额只约束本管线拥有的工作内存、staging 和磁盘。它们不代表整个程序固定只用 128 MiB，也不包括既有音乐源、用户选择集合、编译结果、WPF 原生位图、GC 高水位及声音库。最低系统内存不能直接据此写成 128 MiB。

## 验证范围说明

- 最终统一 Release 验证结果见 `Midora-Bounded-Bulk-Editing-Requirement-Trace.md`：相关项目合计 2,134 项通过；Direct MIDI 专项包含在其中，不重复累计。
