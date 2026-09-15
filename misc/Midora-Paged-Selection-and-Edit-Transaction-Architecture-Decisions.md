# Midora Paged Selection and Edit Transaction Architecture Decisions

状态：Stage 2 核心功能已由产品所有者验收；阻塞性交互优化实施中，完成后等待复验（2026-09-01）

上位需求：`Midora-Major-Editing-and-Visualization-Expansion-Implementation-Plan-2026-08-31.md` 的 WP-01、WP-02、WP-11。本文先冻结性能/正确性边界，避免后续百万对象工具各自建立不兼容模型。

## Stage 2 实施状态与边界

本阶段已经建立以下共同基础：

- `ITimelineObjectSource<T>`、revision-bound ordinal/page/range query、source trace 与流式 stable-ID 解析；
- Logical Note、SubVoice Template Event、Logical Parameter Point、Direct MIDI Note、Direct MIDI Channel Event 与 opaque event 的正式 source adapter；
- Workspace Selection 的压缩 stable-ID 集合，以及可表达大范围 interval/bitmap + sparse include/exclude 的 selection descriptor；
- detached page staging、owned spill、revision gate、一次 root swap、精确 change set 与 immutable old/new root compact Undo；
- Timeline 浮动选择工具、右键 click/drag/double-click 冻结状态机，以及本阶段批准的直接交互修复。

现有已高度优化的编辑命令不因本阶段被强制改写为同一 transaction 类；它们可继续使用既有 persistent batch path。后续阶段新增的百万对象 Humanize、Split、Quantize、Generator 等命令必须复用本文边界，不能再次建立全量 `List<T>`、逐对象 Undo closure 或无界 staging。本文也不把 ordinal 提升为持久身份：正式业务身份仍然只有 stable ID。

## ADR-PAGED-001：ordinal/page 是选择与编辑的共同地址空间

决定：三种 Note source、Logical Parameter 与 Direct/Template Event 都必须提供稳定 owner-local ordinal、按 tick/key/lane 的 page/range query 和 page generation。Selection 默认表示为 owner + page interval/bitmap + sparse exception，而不是每个对象一个 boxed ID/hash entry；少量选择仍允许 compact inline ID path。

稳定 ID 继续是业务身份；ordinal 只是当前 source revision 的查询地址，不能持久化或跨 revision 盲用。每个 selection snapshot 同时冻结 owner revision，并能按需流式解析 stable ID。

## ADR-PAGED-002：detached streaming edit

决定：百万对象变换不得先把全部对象复制到 `List<T>` 再原地修改正式集合。命令在 detached staging 中按 page/chunk 顺序读取、计算、做 collision reduce，并写 compact result/delta；全部验证成功后通过一个 owner edit transaction 原子交换受影响 pages/overlay root。

取消、表达式错误、资源上限、revision 变化或碰撞归并失败时只丢弃 staging，不发布 partial source。一次工具操作仍是一个 Project Undo。

## ADR-PAGED-003：compact Undo/Redo

决定：Undo payload 优先保存 immutable old/new page references、局部 overlay roots、压缩 ID/ordinal mapping 与必要的小型 metadata，不保存编辑前后两份完整逐对象图。Redo 使用已经验证的 detached result，不重新运行随机数或表达式。

Selection restoration 使用压缩 result mapping；被 collision/越界删除的对象按工具正式选择规则处理。Undo/Redo 必须检查 owner base revision，不能把旧 page root 应用到不相干 revision。

## ADR-PAGED-004：有界资源与 spill

决定：每个大型命令同时受候选数量、结果数量、decoded working bytes、resident staging bytes、spill bytes 与取消检查间隔约束。内存预算耗尽时只允许 spill 到 `<ProgramRoot>\.tmp\CompilerRuns` 或后续专用 EditRuns owned lease；禁止改用无界内存或系统 temp。

性能目标以线性或 `O(n log pages)` 为上界；禁止逐项 `List.Remove`、全 owner `ToArray`、每项 WPF 对象、每项 Undo closure 或每次结果插入重建全索引。

Stage 2 冻结的默认预算为：

| 预算 | 默认值 |
|---|---:|
| source / staging page records | 4,096 |
| cancellation check interval | 256 records |
| per-page decoded/encoded working buffers | 64 MiB |
| resident staging bytes | 64 MiB |
| spill bytes | 16 GiB |
| candidate/result record count | 100,000,000 |

这些默认值属于本阶段大型编辑执行 profile，不是 Project 数据，不持久化，也不影响 canonical fingerprint。单页编码结果若超过 working-byte 上限或托管数组边界，必须在发布前失败；spill 只允许写入 `<ProgramRoot>\.tmp\CompilerRuns` 下具有 owner lease 的运行目录，Dispose、取消和失败都必须清理本次 owned run。

## ADR-PAGED-005：UI、编译和缓存观察同一提交

决定：正式 edit transaction 发布一个精确 owner/range/page change set。Piano Roll/Event Lane/Velocity/Arrangement preview、selection projection、Full/Incremental Compiler 和 audio invalidation 都从同一 change set 更新；不得各自推断修改范围。

旧 raster/LOD 可作为视觉 fallback，但 revision 不匹配的旧 tile 不得重新成为正式当前层。后台任务必须能在 page boundary 取消，并在发布时经过 revision gate。

## ADR-PAGED-006：右键手势与浮动选择工具不建立第二套编辑语义

决定：Timeline 右键按下只冻结 hit target、原 selection、container 与 tick，不立即改变正式选择或同步解码冷页。移动越过系统 drag threshold 时取消菜单候选并立即进入既有 Event/Parameter 右键 trace；未越阈值的 Right Up 才应用右键选择规则，并以该 Right Up 的单调时间戳与位置启动固定候选。只有同一 Surface 合法内容区内第二个 Right Down 位于 `[0, 300 ms)` 且两位置的水平、垂直位移分别不超过 `6 DIP` 时才形成双击；否则单击菜单在 300 ms 后打开。实现不得使用 WPF `ClickCount`、Windows double-click time 或系统 double-click spatial tolerance。合法双击取消菜单并切换 Draw/Select；Escape、capture loss、Workspace 失效或 revision 变化都取消未完成候选。

浮动选择工具只是正式 Selection 与现有 move/resize/copy command adapter 的矢量 overlay：同质且兼容的选择才显示；Follow 是新 Surface 默认，Pin 改为锚定 Selection 世界坐标，顶部 grip 在两种状态下都允许手动移动。Note 与 Arrangement Segment 提供相互独立、方向可辨的 ResizeStart/ResizeEnd；Event/Parameter Point 不提供 Resize。Move 手势只在当前类型已有 Copy Drag 能力时允许 `Ctrl` Copy+Move，不支持时 Invalid，不得退化为 Move 或借此新增业务复制语义。

浮动工具与普通 Draw 直接操作必须调用同一预览选择器和同一 command adapter。所有选择规模及所有 Move/Resize 手势都显示 Snap、边界与共同饱和后实际会提交的 delta；小选择使用有界即时矢量预览，大选择使用既有平移/Resize raster tile，禁止浮动工具按对象数量提前返回成 delta-only。任何路径都不得建立逐对象 WPF 控件或全量对象预览图。

## Stage 2 验证门

- Logical、Direct MIDI、SubVoice Note 以及三类 point 的同语义命令矩阵；
- 1M/18M 样本的 marquee/Ctrl/Shift/Select All、move/resize/copy/delete/undo/redo；
- collision、越界删除、导入重复保留和选择恢复；
- cold/warm page、跨页、跨 owner 与 revision race；
- peak managed/native/working-set、spill bytes、取消延迟与临时残留；
- Full/Incremental canonical 完全一致，视图命中和渲染无陈旧状态。
