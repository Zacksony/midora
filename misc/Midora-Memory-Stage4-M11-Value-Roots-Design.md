# 阶段 4 / M11：普通 Logical/SubVoice 入口与只读编译值根

日期：2026-09-09。实施设计记录，不修改 SRS。

## 需求追踪

- 输入：正式顺序的 Logical Note、SubVoice Event，以及既有 detached immutable value source。
- 输出：原顺序、稳定 ID、全部精度字段不变的 immutable source root；Compiler 继续产生相同 canonical、诊断与来源。
- 依据：SRS §12.21、§12.24～25、§20.3.15、§20.4.11；INV-003、009、010、015、095、096；内存执行计划 §9。
- 边界/失败：既有重复 ID 检查与原子编辑不变；旧 snapshot 必须继续读旧值。页不是音乐边界。
- 归属：值页/目录是 Domain 的运行时物理表示；facade 是按需编辑对象，不是第二份正式源。所有新状态不持久化、不进入 Modified。
- 非目标：不改 wire、Mapping owner、碰撞规则、音频或音乐语义；不重写已有大型分页工具；不提前阶段 5 的编译展开或 History 总预算。

## 决定

1. 复用既有 `AdoptSource(project, IImmutableTimelineValueSource<T>, token)`、`AdoptSnapshot` 和 COW root。`CreateQuerySnapshot().EnumerateAll()` 保留原集合正式顺序；流式 reader 可以直接对接这些 API，不要求生成 Domain 对象。
2. 普通 Logical/SubVoice 集合在一页规模后晋升为同一 immutable value root。晋升保留仍被编辑器持有的原对象身份，但集合只以弱引用注册 facade；不再常驻完整对象 store 与第二份 frozen-value dictionary。
3. 连续普通追加以一个有界尾页累计，发布前/读取前 flush；追加目录采用结构共享而非每对象全量重建。旧值根、空间目录和同 revision 快照继续共享。
4. Compiler 从 value snapshot 读取并执行原有稳定排序，RawInstance 只保存 LogicalNoteSnapshotValue。排序仍可能是全量值数组；展开预算属于阶段 5，此处不声称编译已完全有界。
5. 必要 source scalar/空间/ordinal 元数据仍随源规模增长；本阶段消除不必要的第二套逐对象图，不把全 Project value pages 误称为固定 RAM 上限。验证记录普通构建/adopt、snapshot/clone、局部编辑和旧 revision 的数量/分配证据。

## 精确 ID 目录与累计小编辑补充

- 普通单条 Add、非尾部 Insert/InsertRange、Logical Parameter/Value Curve point 也经过同一晋升。每次读取都 flush 的单条追加同时填满 sequence 与 ordinal 的 128-record 尾叶，不能累积“每对象一页”。
- 同一 revision 的有序空间页共用固定 identity-order 数组。非排序输入仍保留自己的排序数组；这不改变 range query 或局部 fingerprint。
- 页 ID 范围按正式顺序严格分离时，地址目录使用共享范围树；页内二分或最多 128 ID 比较。若页范围交错、倒序或重叠，必须构建独立的精确 ID→ordinal 排序页树，不能全 source 线性回退。
- 精确目录叶最多 128 项，字段为 `Int64 ID + Int32 ordinal`，约 12 bytes/record 加树/数组头。首次转换的排序工作数组是 16 bytes/address，旧范围树约 8 bytes/record 加头；它们只包含地址元数据，转换完成后旧范围树与临时数组可释放。它不是固定大小缓存；普通内存 source 不被强制 spill。既有 indexed adopted source 与显式 `PrepareOrdinalLookup(builder)` 继续使用独立外置精确索引。
- 后续 monotonic ID suffix 以有界叶 + AVL join 接入；插在已有 ID 范围中的新 ID 只 path-copy 相交叶和树路径。局部数值编辑共享原地址目录。原 snapshot 的地址映射不变，不按每次小追加重新排序全表。
- `AddRange(IEnumerable<T>)` 保留既有完整输入验证/对象身份的调用边界；输入暂存不等于集合长期保留。正式 reader 使用流式值页 builder→Adopt，不再经过此对象入口。大型编辑继续使用原有分页工具。

## 验证门

100k / 1M 的 Logical、SubVoice 普通追加与 adopted source；首次及重复 snapshot、clone 共享、局部编辑只改相关页、ID/字段/正式顺序/区间 fingerprint、弱 facade 回收；Compiler golden 与 Full/Incremental 等价沿用专项回归。测试结果由阶段 4 报告汇总，不将尚未运行的门写为通过。

## 2026-09-09 自动验证记录

- Release `MemoryStage4LogicalValueRootTests | PagedTimelineCollectionTests | PagedTimelineSharedRootTests | PersistentTimelineSequence` 过滤回归：56/56 通过，17 s。包括 100k / 1M 普通 Logical、SubVoice、CurvePoint 与 adopted roots；随机、倒序及跨页交错 ID 的精确查找，测试比较次数上限 32；已有 100k ID 根后追加 1,024 地址/单 Note 的分配均小于 512 KiB。
- Release `Midora.Compiler.Tests`：437/437 通过，2 s。验证阶段、fingerprint、Logical/Template/Curve 编译输入改值读取后保持原 golden/诊断/Full-Incremental 规则。
- Release `Midora.Persistence.Tests` 构建成功，0 警告 / 0 错误；该次仅构建，不把它记为 Persistence 测试通过。
- 以上是自动门，不等于 WPF 人工验收。最终 old/new 同机内存与耗时矩阵由总阶段报告记录；ordinary 构建和第一次 snapshot 必须分别列出并另列总和，避免把元数据计算前移误认为总耗时增长或凭空消失。

## 末轮只读准备并发与取消审查

- 旧 snapshot 的 `PrepareOrdinalLookup(builder)` 可在后台将与 live owner 共享的只读地址缓存迁移为 external index；它不持有 owner 的 snapshot-publication 锁，因此不能以 Project 写入串行排除并发。追加现在使用 `TryAppend` 一次捕获不可变 `PreparedIndex`，先构造完整新地址根，再修改 owner 的 leaf/discovery 元数据；若捕获的是 external，直接使用既有 generic COW mutation。后台随后切换旧缓存不会改变新根引用的完整前缀，不要求扩大锁域，也不阻塞已经准备好的只读索引。
- 精确排序树的最终 Build 每个递归入口检查取消，每叶最多复制 128 条地址；Create 返回前及 OrdinalDirectory cache 发布前再次检查。范围目录最终树合并也逐节点检查。取消不把半成品地址树安装到共享 snapshot。
- 增加确定性同步门覆盖有序/精确目录下 external 发布在追加前、地址扩展中及空间页创建中三种交错；检查旧新完整顺序、ID/ordinal、范围查询、再次追加，以及 external 取消不 retain/可重试。通过私有阶段入口验证最终 post-sort Build 在空树、单叶、分支规模的取消，避免只测枚举前取消而漏掉最终分配阶段。
- 末轮 Release 定向回归 **66/66** 通过（18 s），Compiler **437/437** 通过（2 s）。TRX：`.tmp/memory-stage4/test-results/stage4-domain-final-audit.trx`、`stage4-compiler-final-audit.trx`。随后串行构建 Persistence.Tests 到 `.tmp/memory-stage4/current/`，0 警告/错误，6.06 s；包含 root 的最新 staging `Flush()` 修改，不把该构建记为 Persistence 测试通过。
- 最终 current DLL MVID：Domain `e50a52f7-f247-4732-8674-9eaeff16a979` → `75f1345c-41c9-4377-9c3b-14ecec4384ae`；Persistence `13c23625-2716-4304-9e0f-d07b481209dd` → `558d6b21-1d68-47cb-8118-4325751bff13`。该 current 构建不包含 Compiler；已测正常 Release bin 的 Compiler MVID 为 `1b16bdc4-e870-4efe-be62-0012775572f2`，没有有效 before 采集值，不声称前后比较。

## Desktop 属性测试同步补充

最终 Desktop 首轮 403 例有 1 例在 `ConductorPropertiesApplyOnlyOnExplicitTransaction` 的 `SelectedConductorEvent?.Id` 断言得到 null；同例此前正式属性标题已正确为 Tempo。原因与本轮缓存表示变更有因果联系，不能写成完全无关或简单偶发：`EnsurePublishedState` 不再创建全量 frozen value dictionary，首次 ID 查找可能触发地址元数据准备。Conductor 复用该 generic store，`SelectWorkspaceObject` 立即保存正式 `Selection.Primary/IdSet`，但 `TryRefreshSelectionPresentation` 的 cache-only 查询可能返回 Pending；后台 PrefetchIds 后，通过捕获的 Dispatcher 再执行 `RefreshConductorPrimary` 才发布可选展示行。普通 async xUnit 上下文不会自动处理此 Dispatcher 队列，同步属性读取也不会替它发布展示行。

仅修改该测试：新增正式 `Selection.Primary` 立即正确的断言，保留全部原断言；在展示行断言前监听 `SelectedConductorEvent` 的 PropertyChanged 完成信号并运行当前 DispatcherFrame，5 秒超时，不固定 sleep、不改变产品选择/属性语义。定向与全套重跑结果由总阶段记录补入。

同步修正后，隔离 Release 构建的定向用例 **1/1** 通过（914 ms）、`DesktopSessionControllerTests` **54/54** 通过（9 s）、Desktop 最终全套 **403/403** 通过（1 m 24 s，0 失败/跳过），按顺序串行执行。新 TRX 位于 `.tmp/memory-stage4/regression-desktop-final/`：`conductor-properties-selection.trx`、`desktop-session-controller.trx`、`Midora.Desktop.Tests.trx`。原失败 `.tmp/memory-stage4/regression-final/Midora.Desktop.Tests.trx` 保留；未删断言、未修改产品 Selection/UI 行为。
