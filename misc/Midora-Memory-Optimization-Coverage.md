# 内存优化：操作覆盖索引与阶段基线

日期：2026-09-08。配套 [六阶段实施 / 验收计划](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md)。本文件是工程执行索引，不修改 SRS，不表示列出的每种大样本或 UI 场景本轮均已测量。

2026-09-09 状态收尾：六阶段与独立编译修复已经验收；后续 Int64 诊断、README 上限及滚轮修复已完成并纳入用户大体验收通过。各阶段表格保留当时测试入口和口径，当前替代项见 §11；下一步见 [独立 Logical 编译内存工作](Midora-Pre-Expansion-Closeout-and-Next-Step-2026-09-09.md#3-下一步logical-编译结果内存优化)。

## 1. 使用方式

每一阶段复用本表，仅重跑受影响链路及其公共回归门；阶段 6 再串联全部操作。结果记录在各阶段报告，并关联 TRX / 探针原始输出。不得把某个通用测试套件通过写成该操作所有对象类型、规模、入口和 UI 效果已经验收。

符号：N = MIDI / Logical / SubVoice Note；P = Channel / Logical Parameter / SubVoice Event point；S = MIDI / Logical Segment。实际适用对象仍以 SRS / 现有命令为准。不适用组合不新增能力。

正常结果必须校验字段、稳定 ID、正式序列与碰撞；历史校验提交一次、Undo / Redo、选择恢复与分支；长任务校验准备、排序 / spill、提交前取消、异常清理，不只看操作返回。持久化要覆盖改完后 Save / Save Copy / Open，而非只在 Undo 回原状后保存。

## 2. 操作与已有自动入口

以下测试名指同名 `*Tests.cs`；用 `rg --files src` 定位。具体本轮运行范围与 opt-in 大样本是否启用，必须以阶段报告为准。

| 编号 | 对象 / 入口 / 操作 | 必须保持的行为 | 自动入口 / 补测方向 |
| --- | --- | --- | --- |
| O01 | N/P/S 单选、框选、Ctrl/Shift、全选、取消 | compressed selection、范围边界、视觉与逻辑同选区 | TimelineObjectSelectionTests、WorkspaceSelectionScalabilityTests、SelectionUiFailureReproductionTests |
| O02 | 对象列表单 / 混合选择 | 双向选择、类型分组操作、定位与 Lane | TimelineObjectListIntegrationTests、TimelineTypedSelectionSourceMappingTests |
| O03 | Read Selection、Scale 准备、Read Properties | owner hint / revision 精确；不能每字段扫描全源 | ProjectTimelineSelectionReaderTests、SelectionReadOptimizationTests、RealMidiSelectionReadPerformanceTests（显式 opt-in） |
| O04 | N/P 创建与删除 | Note later-loses、Event later-wins，仅处理命中键 | BoundedDirectMidiNoteEditTests、BoundedDirectMidiEventEditTests、BoundedPointCommandTests |
| O05 | N/P/S Move；Draw / Select 浮动工具 | 位置 / 值 / owner / 边界与真实选择同步 | ProjectBatchTimelineEditCommandsTests、BoundedLogicalNoteCommandTests、WpfInteractionRegressionTests |
| O06 | Ctrl 复制拖动、Alt 与 Shift 修饰 | 新 ID、复制后选择、固定 tick / key、精确碰撞 | ProjectObjectClipboardPureMidiTests、Stage4DesktopSelectionIntegrationTests、TimelineRenderingTests |
| O07 | N/S 左边界 Resize | 隐藏数据、起点与长度、最小值、Undo | BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、TimelineRenderingTests |
| O08 | N/S 右边界 Resize | gate、边界、选择全体；不改变其他字段 | BoundedDirectMidiNoteEditTests、BoundedLogicalNoteCommandTests、TimelineRenderingTests |
| O09 | Snap / 无 Snap、创建 delta、视图外拖动 | 时间 / 值取整与原有创建语义 | TimelineNavigationTests、TimelineEventContextGestureTests、TimelineValueTraceSamplerTests |
| O10 | N/P 水平翻转 | 正式范围、同键碰撞、原有 endpoint order | ProjectSelectionTransformEditCommandsTests、ProjectAdvancedTimelineEditCommandsTests |
| O11 | S 仅内容 / 连同窗口水平翻转 | 只影响暴露对象，保留隐藏内容 | BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、ProjectMultiOwnerAdvancedEditCommandsTests |
| O12 | N/S 垂直翻转 | key 映射、隐藏对象及参数不被误动 | ProjectAdvancedTimelineEditCommandsTests、ProjectAdvancedTimelineRootSwapProductionTests |
| O13 | N/P/S Scale | 四舍五入、长度与位置、同类 / 混合 owner | ProjectSelectionTransformEditCommandsTests、ProjectMultiOwnerAdvancedEditCommandsTests、SelectionReadOptimizationTests |
| O14 | N/S Transpose | 越界删除、保留 survivor ID、Undo 原集合 | ProjectAdvancedTimelineEditCommandsTests、ProjectAdvancedTimelineEditIntegrationTests |
| O15 | N/P/S Batch Edit | 表达式依赖、clamp / 删除、失败零发布 | ProjectBatchTimelineEditCommandsTests、BatchEditExpressionProgramTests、BoundedEditRoundTripTests |
| O16 | N Humanize | 固定 seed；Redo 不重抽；稀疏 unchanged holes | ProjectAdvancedTimelineEditCommandsTests、BoundedDirectMidiQueryRegressionTests |
| O17 | N Split，三种模式及预设 | 端点 / NoteOff velocity、上限 / 取消、ID | BoundedDirectMidiSplitJoinTests、BoundedLogicalNoteCommandTests、NoteSplitPresetStoreTests |
| O18 | N Join | 同 key 接合、原始顺序、选择与 Undo | BoundedDirectMidiSplitJoinTests、BoundedLogicalNoteCommandTests |
| O19 | N/P Quantize | 舍入、Note / Event 相反碰撞规则、tombstone | BoundedDirectMidiQueryRegressionTests、BoundedPointCommandTests、ProjectLogicalParameterQuantizeTests |
| O20 | N/P Batch Create、初值 / 表达式 / 预设 | 依赖与最大数量、终止、失败 / 取消 | TimelineGenerationIntegrationTests、GeneratorExpressionProgramTests、TimelineGenerationPresetStoreTests |
| O21 | N/P/S Properties，Mixed、逐字段还原 | OK 一次事务；取消无修改；类型共同属性 | AdvancedEditDialogsTests、Stage4DesktopSelectionIntegrationTests、ProjectBatchTimelineEditCommandsTests |
| O22 | Copy / Cut / Paste、Clipboard 替换 | bounded storage、精确 ID 和重新选择、可用菜单 | ProjectClipboardStorageTests、ProjectClipboardPasteTargetTests、ClipboardSelectionRoutingTests |
| O23 | Duplicate、跨轨道、Shared / Independent | 独立身份与共享执行状态，正式顺序不变 | BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、ProjectObjectClipboardTests |
| O24 | MIDI ↔ Logical Segment 转换 | 损失确认、共同 Note 字段、hidden window、原子 Undo | SegmentConversionServiceTests、SegmentConversionPlacementAndHistoryTests、SegmentConversionRoutingTests |
| O25 | P 自由画线 / 直线 / y=k / 固定 tick | 每 snap 点、越界坐标、同 tick 后者覆盖 | BoundedPointCommandTests、TimelineValueTraceSamplerTests、TimelineEventContextGestureTests |
| O26 | Lane 新建、切换、删除、参数定义 | 选择清除 / 保留契约、焦点、mapping owner | BoundedPointCommandTests、MappingEditingPolicyTests、LogicalParameterEventBindingDialogTests |
| O27 | S Split / 窗口扩充与裁剪 | 跨边界音符、参数起点状态、隐藏对象 | BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、BoundaryCleanupTests |
| O28 | Track / Root / Usage 新建、排序、改绑、删除 | 全局顺序 / 非空 Root / Definitions 不误删 | ProjectFlatArrangementEditCommandsTests、ProjectObjectClipboardTests、DesktopSessionControllerTests；Stage5ArrangementRoutingTests 只覆盖 Segment 转换，不能单独为本项背书 |
| O29 | Instrument / SubVoice / Mapping / Loop / Pre-Roll 编辑 | 依赖失效、共享状态、编译诊断 / 可听语义 | MappingEditingPolicyTests、LogicalParameterEventBindingCompilerTests、LoopEntryCompilationTests、PreRollCompilationTests |
| O30 | 首次 / 重复 / 跨区域 Undo，Redo，清分支 | 旧 root / selection 精确恢复，无全源 ID 退化 | PagedSelectionAndEditTransactionTests、PureMidiCowRootTests、BoundedEditRoundTripTests；M01 探针 60k |
| O31 | Arrangement / 三种 Piano Roll / Velocity / Event Lanes | 现有细边框 / 高亮、cache-only UI、局部失效 | TimelineRenderingTests、PagedTimelineCacheOnlyTests、PureMidiPagedPresentationTests、PagedLogicalPresentationTests |
| O32 | Conductor | Tempo Step、高密度点、时间 / 状态、拖动 | ConductorInteractionTests、ConductorProjectionTests、ConductorRenderingPerformanceTests |
| O33 | Onion / All Tracks | 层序、只读、来源、Compiled 混合显示及生命周期 | OnionWorkspaceTests、TimelineOnionTests、OnionPresentationLifecycleTests、CompiledOnionNoteIndexTests |
| O34 | Full / Incremental Compile | canonical / 诊断 / 来源完全等价 | PureMidiCompilationTests、PagedPureMidiCompilationTests、IncrementalCompilationTests、SourceTraceTests |
| O35 | Playback / Preview / Seek / Mute-Solo | 只消费 canonical，范围 / FIFO / 状态恢复 | PagedPureMidiAudioPlanTests、PreviewCompilerTests、音频专用门（阶段 3 / 6） |
| O36 | MIDI / Audio Export | 正式事件 / frame、命名与原子取消 | PureMidiExportTests、Midora.MidiExport.Tests / Midora.AudioRender.Tests 全套；音频性能非 M01 |
| O37 | New / Open / Save / Save Copy / Close | 保存时间与内容、严格校验、资源释放 | PureMidiReadOnlyPersistenceTests、ProjectPersistenceCoordinatorTests、M01 real-MIDI probe |
| O38 | Format 1/2 迁移、Format 3 严格性 / 故障 | 旧副本、未知 / 重复 / checksum、原子发布 | PersistenceContractV1Tests、PersistenceContractV2Tests、ProjectPresentationSchema2Tests、MidoraProjectPackageFaultInjectionV1Tests、FormatMigrationDesktopTests |
| O39 | Tab 切换 / 关闭、Project 切换 / 取消在途任务 | 不保留旧图与旧 owner；不会僵尸订阅 | SelectionPresentationLifecycleTests、OnionPresentationLifecycleTests、WpfMemoryProbeTests；M02–05 |
| O40 | Imported Meta / SysEx 的属性、Copy / Cut / Paste / Delete、随 Segment 变换 | payload 原样、正式顺序、只开放现有可编辑属性；不可把 opaque 当数值 point 任意生成 / 量化 | ProjectObjectClipboardPureMidiTests、MixedTimelineSelectionCommandsTests、BoundedDirectMidiSegmentTransformTests；大 payload 计账归 M07 |

阶段 1 只修改 O34 / O37 读取方式及基础设施；公共编辑与 UI 回归用于防止间接退化，不实施 O39 或其他阶段预算重构。阶段 2、3 的后续覆盖分别记录在本文末尾，不回写为阶段 1 已完成的工作。

## 3. M01 只读消费者逐项审计

| 消费者 | 确认的现状 / 本阶段动作 |
| --- | --- |
| `MidoraProjectPackageV1.ValidateSupportedProject / EnumeratePureMidiTrackIds` | 原来枚举 mutable Note / Channel / Opaque 导致三表增长；改为正式值序列并传递取消。仍校验全局唯一 ID / 高水位，包括暴露范围外数据。 |
| `ValidateLoadedStableIds` | 原来打开和保存自校验都会再次物化；共用新值遍历。没有删除或跳过自校验。 |
| `PureMidiContentPackPersistenceV1.WriteMergedPack` | 编辑后、不能直接复用 pack 的正式 writer 改为值流。保持序列顺序、所有字段和 payload；byte-identical reference test。 |
| `FindReusablePack / CopyReusablePackAndHash` | 元数据 / pristine 检查及流式复制，本来不物化；保持不动。 |
| `PureMidiCompilation` 小型非分页计划 / opaque 冻结 / materialize | 只读枚举改为值；之后原有 tick / order / ID 排序及 MIDI 投影不变。 |
| `SemanticValidator` Pure MIDI 路径 | 使用 `EditedValues` 加已校验不可变源；不遍历 editable facade，保持既有验证协议。 |
| `PureMidiPagedCanonicalSource` | fingerprint 使用正式根 / edited values，播放端点、状态、统计为 value query；本阶段不改。 |
| `PureMidiPresentationSources` / Onion / 对象列表 | 使用 query snapshot / value object source / page 查询，不以 mutable full enumeration 绘制；生命周期和 retained view cache 属于阶段 2，不在本轮改。 |
| bounded selection、Clipboard、变换 | 使用 revision-bound object source 与值页 / ID；保留避免破坏 indexed lookup、collision 和 Undo。 |
| `ShiftMidiSegmentContent`、兼容碰撞路径 | 存在 mutable enumeration，但负责 setter 或实际对象删除，不是只读消费者。不能机械换成 readonly 值或删 identity 表；本阶段保留。bounded 主路径已有独立测试。 |
| Collection `GetEnumerator / CopyTo / IndexOf` 兼容接口 | 仍是明确可编辑对象接口。保留 setter / identity / collapse-to-source；正式全量只读消费者不再使用它。新 API 不是“所有访问都不分配”的承诺。 |
| Logical / SubVoice protobuf DTO 和 Conductor JSON | 不使用这三个 Direct MIDI 集合，属于 M08；避免把本阶段扩大为另一次序列化重写。 |

## 4. 冻结资源口径

| 容器 / 层 | 当前依据 | 计账与本阶段边界 |
| --- | --- | --- |
| Pure MIDI 解码 cache | `PureMidiContentPack.DefaultDecodedCacheByteLimit` = 64 MiB | 共享解码页一次计账；并非进程总上限。M07 继续审计 Opaque payload 实际占用。 |
| Pure MIDI writer 编码缓冲 | `DefaultEncodeBufferBudget` = 64 MiB；单 decoded page ≤4 MiB | 活动 writer 独立工作缓冲；builder 尾页 / 并发预算属于 M09。 |
| detached edit working / resident | `DetachedPagedEditTransaction` 默认各 64 MiB | 不把两个独立预算或共享值页重复算为总上限；spill 归任务 / 历史 owner。 |
| bounded immutable values decoded cache | `BoundedImmutableValueSource` 64 MiB | 正式 source / lease 与 LRU retained 分开计账。 |
| WPF raster cache | `SharedTimelineRasterCache` 256 MiB | 完成位图预算不含所有在途 / retained Drawing / UI subscriptions；本阶段不调整。 |
| 三种 source facade identity 表 | 原来只读全源可各增长至 N | 新只读扫描增量必须为 0；已有显式编辑索引不强制清空。 |
| 持久化 `StableIdSetV1` | 2^20 IDs / block 的 ulong 位图 | 本阶段保留全局唯一性验证；稀疏巨大 ID 放大留给 M10。 |
| Project / source / Undo | immutable root 可共享，拥有者关闭释放 | 原项目与保存自校验项目允许同时存活；不可合并 ID 生命周期或删正常 Undo。 |

## 5. 验收职责

工程侧承担完整自动正确性、重复性能测量、失败 / 取消、容器计数和资源归因。用户在阶段 2 完成后执行验收 A 的少量代表性音乐工作流；本阶段不要求用户重测整份表。

需人工确认的是真实操作手感和视觉变化；计数、字节等价及大规模内存归因由自动证据覆盖。若发现新增 UI 延迟 / 首次长尾，即使 M01 峰值变小也不能视为验收通过。

## 6. 阶段 2：M02～M05 生命周期补测

实现及最终通过数量见 [阶段 2 报告](Midora-Memory-Stage2-Validation-Report-2026-09-08.md)。以下补充 O31～O33、O39 的工程入口，不替代后续预算、History 或全部大选区操作的验证。

| 所有者 / 边界 | 自动入口 | 核心断言 |
| --- | --- | --- |
| Workspace / Project / shared EditorSettings | WorkspaceLifecycleTests | 100 次循环和 Project 替换；幂等 Dispose、handler 解除、弱引用释放；隐藏保留状态；关闭 VM 不拖住 Project |
| 选区与 Lane discovery 完成通知 | WorkspaceLifecycleTests、SelectionPresentationLifecycleTests | 取消后无需 pump 即断开捕获图；旧修订/关闭后不得重新发布 selection metrics 或 lane 结果 |
| 非瓦片完成与 UI 合并信号 | PresentationDispatchLifetimeTests、ConductorInteractionTests | 排队回调可撤销；合并信号只弱持 Surface；Conductor 语义查询独立于 raster epoch，来源/卸载仍取消 |
| MainWindow 命令和延迟焦点 | TimelineCommandTargetTests | 弱持 Surface/VM；要求活动实例、DataContext 相同；旧目标无效且不阻止回收 |
| 真实视觉树和 Modal 返回 | HostedWorkspaceLifecycleTests（由 WpfInteractionRegressionTests 的 App/STA 入口执行） | 隐藏 HwndSource 中 Loaded/Unloaded、同模板 Surface 重用、Properties/Batch 逻辑焦点、关闭后的延迟返回 |
| Onion 与 All Tracks | OnionLifetimeTests、TimelineOnionLifetimeTests、OnionWorkspaceTests | hidden/disabled/zero opacity/nosources 释放投影；保留设置及合法 stale index；构建关闭、进度合并与重入保护 |
| 瓦片共享计算 / 独立订阅 | TimelineRasterSubscriptionTests | 60k Request 只保留 6 订阅/3 计算；部分/最后消费者取消、已排通知撤销、新代竞争、Clear、队列存储有界 |
| 低缩放实际扫描取消 | RasterAggregationCancellationTests、TimelineRasterAggregateCancellationTests | 逻辑/模板/Direct overlay/pack/bounded 查询逐页/节点/最多 256 条检查；未取消的聚合、work count、像素不变 |
| 两层概览与现有图像行为 | TimelineRenderingTests、TimelineOnionTests、OnionProbe | 概览 coarse barrier、三种 Piano/Velocity/Event、高亮、叠层、DPI、Raw/Compiled；生产缓存精度和 256 MiB 预算不变 |
| 真 WPF 长循环 / 大源对照 | eng/MemoryStage2Probe | 同旧/新 DLL 和模板、每 Project 100 次、两个 Project；18M 大样本、自然/GC 后内存和时间分报 |

阶段 2 不触及 O01～O30 的编辑算法；对应公共测试用于检查生命周期改动的间接回归，不据此声明所有操作的有限内存预算已收口。阶段 1＋2 的 MEM-A01～A08 已由用户粗略验收通过，精细编曲验收留到发布前；阶段 2 随后仅补修 All Tracks 激活焦点。当时未自动推进阶段 3，后由用户另行启动。

## 7. 阶段 3：M06 / M07 / M09 / M10 预算补测

实现、旧新测量与最终回归状态见 [阶段 3 报告](Midora-Memory-Stage3-Validation-Report-2026-09-08.md)。不把下列模块预算等同整个应用的统一硬上限，不替代阶段 4 的 M08/M11 或阶段 5 的 History 总预算。

| 所有者 / 边界 | 自动入口 | 核心断言 |
| --- | --- | --- |
| 范围 canonical / realtime / sample 三族缓存 | PreparationStorageCacheTests、MemoryStage3Probe `plans` | 共享 128 MiB / 256 项；backing 去重；baseline/active/cache 分列；1,000 范围平台、冷热、超工作集重访；合法大对象返回但不钉住 |
| Preparing / 替换 / Buffering / Stop / Dispose | PreparationStorageCacheTests、SessionPreparationLifecycleTests、PlaybackControllerTests | Start 前 lease、失败原子释放；Held Preview 替换期间新旧两套保活；不提前 Dispose immutable page；最后 reader 释放；目录高水位与 canonical backing 的弱引用 GC |
| 跨 Track / Root / shared Usage 状态 | PagedPureMidiAudioPlanTests、PureMidiCompilationTests、PreviewCompilerTests、Midora.Playback.Tests 全套 | 既有中途恢复、Mute/Solo、Seek、loop、边界语义与正式计划不变 |
| Opaque ID / virtual page / reader | OpaquePayloadBudgetTests、PureMidiSourceIdResolutionCacheTests、ProjectTimelineSelectionReaderTests | 96 × 1 MiB、4 MiB 可变页、合法大单项、sparse/dense 地址映射、旧修订、取消、无 payload identity 常驻 |
| Opaque Properties / clipboard | OpaquePayloadBudgetTests、ProjectClipboardStorageTests、Desktop 公共测试 | 256-byte 预览与标量完整，零 facade 留存；字节往返、spill、lease、slice backing 实际计费；原 opaque byte-stream int offset 限制没有升级 |
| Content Pack active/partial/pending | ContentPackBuilderBudgetTests、PureMidiContentPackTests、MemoryStage3Probe `packs` | actual capacity、old+new 扩容、1/100/1000 tails、64 MiB 正常与 9 MiB 压力；spool 复用、损坏、失败、取消；对应旧新完整文件 SHA-256 一致 |
| Stable ID resident/external | StableIdValidatorV1Tests、MemoryStage3Probe `ids` | dense/sparse/极大值、全包重复及来源顺序；8 MiB 含迁移预留；异常 extent/IO、取消、保存来源不变 |
| 原型至真实大型项目 | MemoryStage1Probe `roundtrip`（阶段 3 candidate DLL） | 9KX2 导入、60k 编辑及 Undo/Redo、Save/Copy/Open，17,999,999 Note、40 Track、0 damaged；source facade 三表不增长；Project 弱引用释放 |
| 音频热路径保底 | Run-Regression.ps1 的托管音频过滤 | ring、共享协议、PCM IO、Limiter、WAV 写入、MIDI plan/stream 分页；无新增 callback 分配/锁，未据此宣称设备/原生实机验收 |

本轮 UI 生产变更仅为 Opaque Properties 的等价只读取值；未改变 Piano Roll / Arrangement / Onion 渲染策略、交互与音频算法。公共测试中需要显式环境变量的大样本用例未自动开启；真实大样本证据以本轮明确运行的独立探针为准。人工音乐验收 B 按计划合并至阶段 4 后，不要求用户在阶段 3 重跑全表。

## 8. 阶段 4：M08 / M11 值根与严格流式持久化

完整实测、时间代价、3,060 项最终公共回归与人工 B 卡见 [阶段 4 报告](Midora-Memory-Stage4-Validation-Report-2026-09-09.md)。不提前宣称 M12 / M13 或整个应用内存已收口。

| 范围 | 自动入口 | 核心断言 |
| --- | --- | --- |
| Ordinary / adopted Logical Note、Template Event、CurvePoint | MemoryStage4LogicalValueRootTests、PagedTimelineCollectionTests、PagedTimelineSharedRootTests | 100k/1M 普通与 adopt、4096 晋升、头/中插入、对象身份、弱 facade、snapshot/clone 共享、局部修改和旧根隔离 |
| 地址、页与长期小编辑 | PersistentTimelineSequenceTests、MemoryStage4LogicalValueRootTests | 128-record 尾叶；随机/倒序/交错 ID 精确查询；已有 100k 根追加 1024 地址分配门；不全表扫描 |
| 后台 frozen index / live Append | MemoryStage4LogicalValueRootTests | external 交接在追加前/索引扩展中/空间页构造中，旧新内容不丢；最终建树取消不发布，可重试 |
| Compiler readonly source / fingerprint / RawInstance | Midora.Compiler.Tests 437 全套 | 不物化 facade 的值输入；golden、Full/Incremental、诊断、Loop/Pre-Roll、共享状态和正式顺序不变；展开总预算归阶段 5 |
| Logical / Instrument v1/v2 protobuf | StreamingProtobufCodecTests、原 PersistenceContractV1/V2 测试 | 旧生成器逐字节 oracle、只读 value pages、合法字段乱序、presence、unknown/duplicate、UTF-8/enum、header 错误优先级、short/nonseek/cancel |
| Conductor JSON | ConductorStreamingCodecTests | 68 专项：全部类型/decimal/Unicode、乱序兼容、原子接管、损坏回退；16MiB 空白固定输入窗口，合法巨大 scalar 兼容；20k 输出28次write且bytes相同 |
| Package staging / SHA / 全字节自校验 | PackageContentStreamTests、MemoryStage4PackageTransactionTests、MidoraProjectPackageFaultInjectionV1Tests | 一次一文件；EOF/short read/leave-open；仅坏 staging 也拒绝；取消与原目标安全；分页重开继续编辑/保存；旧格式备份/迁移规则不变 |
| 量化前后对照 | eng/MemoryStage4Probe | 同代码/冻结旧新 DLL、三轮百万 Logical/SubVoice/Conductor，100k/mixed、cold/warm 分列；全包条目hash相等；必要source与累计分配/峰值分列 |
| 真实 Pure MIDI 不回退 | eng/MemoryStage1Probe roundtrip、Compare-Content.ps1 | 9KX2 17,999,999源Note/40Track、60k两类编辑/Undo/Redo/取消/Save/Copy/Open；38次facade census零增长；旧新101项内容一致；Project弱引用释放 |
| UI / 选择显示一致性 | Presentation 445、Desktop 403 全套 | 三种编辑器、工具、Lane、Properties、Onion/All Tracks 共用路径；Conductor 正式选择即时与异步显示完成分别断言，不跳过旧断言 |

本阶段没有更改生产 UI/渲染或音频算法，测试同步补修不属于 UI 行为调整。用户随后确认 B01～B09 整体通过，并另行授权阶段 5；其报告的 Logical 大粘贴后逐修订内存增长由 M12/M13 继续调查处理。

## 9. 阶段 5：M12 / M13 编译、诊断和历史

实施及压力结果见 [阶段 5 报告](Midora-Memory-Stage5-Validation-Report-2026-09-09.md)。以下是已运行的正确性入口，不把数据探针或虚拟行测试当作完整 WPF 长会话验收。

| 范围 | 自动入口 | 核心断言 |
| --- | --- | --- |
| Logical Raw pattern / context / source | LogicalRawPatternTests、LogicalRawPatternRoundTripTests、Compiler 455 全套 | 精确共享、64 种 context 继承、Group 极值、来源还原、4,096 pattern 发现缓存上限、修订隔离、Full/Incremental 与原正式语义 |
| 完整 overlap diagnostics | CompactDiagnosticTests | 49,995,000 条紧凑诊断的计数、顺序、重复、Source、筛选；不将 Error 改为摘要后丢弃正文 |
| WPF 虚拟诊断与后台筛选 | VirtualDiagnosticRowsTests、Desktop 407 全套 | 50,000,000 逻辑行、256 行缓存、旧 selection 查找、过滤取消和最新修订；无全行 WPF 对象集合 |
| 正式失败和 README | CompilationRejectedExceptionTests、MidiExportReadmeBuilderTests、MidiExportArtifactBuilderTests、Playback 140 | 失败摘要最多读取 32 行但保留完整序列；README 严格 UTF-8 全行流式、取消、冻结参数和原子输出 |
| Canonical / 范围与消费 | Compiler、MIDI Export、Playback、Audio Render 公共集 | 原地 fold/range 稳定顺序与完整来源；无多余全数组副本；共享 canonical span 不另立音频语义 |
| 失败 canonical 资源计账 | ResourceShortageRetainedStorageTests | 对象与五个底层 ID 数组均计账，共享结果不重复计费，重复遍历幂等 |
| History / Clipboard / splice | MemoryStage5HistoryOwnershipTests（21 case）、Application 1,128 全套 | 百万根路径共享、64/65 代与 512 次叶修改、四方向 Note 粘贴源释放、Undo/Redo/分支/取消、旧读者合法性、80 次 splice 与小编辑交替 |
| 受守护专项压力 | eng/MemoryStage5Probe | 冻结旧/新 DLL；重复编译完整 digest；多 Voice/Template/Loop 梯度；指定 9KX2 跨类型链；8 GiB 私有 Job 及 2 GiB 系统余量 |

M12 的发现缓存有固定预算，但正式 Logical canonical 和有效历史所需内容不因此变为常量。所有测得峰值、累计分配与仍未深度分页的部分在阶段报告中分列。完整 UI、后台、缓存及保存/播放长期共存仍属于阶段 6，不预填验收 C。

## 10. 阶段 6：交叉操作和证据层级

详见 [40 项逐行审计](Midora-Memory-Stage6-Operation-Coverage-Audit.md) 和 [阶段 6 报告](Midora-Memory-Stage6-Validation-Report-2026-09-09.md)。审计明确记录适用/不适用与每条入口，运行是否完成以报告和日志为准。

- `MemoryStage6CrossOperationTests` 补充六类 owner 的固定 seed 混合链：Move、双边 Resize、Humanize、数值 Point 调整、Ready 取消、旧 revision 拒绝、Undo/Redo、新分支、双向 Clipboard、修改态 Save/Copy/Open 与 canonical 对照。不以这条链替代其他工具专项。
- `MemoryStage6CleanupTests`、`MemoryStage6ContextCleanupTests` 等故障注入补充异常发生后的清理编排；报告应分别说明下层 owner 被调用、资源计账归零与对象弱引用释放，不能混为一谈。
- `eng/MemoryStage6WpfProbe` 使用真实模板/Surface/离屏 WPF 与 Desktop 后台会话；不调用 Show、不执行 computer-use，不能代替显示器输出、实际输入时序和设备听感验收。
- `eng/MemoryStage6Probe` 使用退出调用栈后的 WeakReference 与 VirtualQuery 区域计量核对编译持有链；受控 GC 只发生在诊断工具，产品正常关闭不增加强制 GC。
- 有 env 启动门的 Fact 在缺少样本时直接 return，其普通 TRX Passed **不计为真实大样本通过**。本轮大样本证据必须有显式路径、实际数量、guard 与阶段记录。
- 阶段 6 当时按用户决定暂缓 Int64；该历史决定已被后续独立实施取代，当前契约与验证见 §11。不得把当时 Int32 限制继续写成当前缺陷。
- 连续消费测试额外确认 [Pure MIDI 分页范围编译缺陷](Midora-Memory-Stage6-Pure-MIDI-Range-Blocker-2026-09-09.md)：提前终点缺 NoteOff/Reset、计数不一致，以及 Save/Open 后部分 canonical metadata 不等价。用户随后授权独立修复；两项原 oracle 未放松且现已通过。3157 / 3157 公共回归、9KX2 三轮前后实测及当前 WPF 大会话结果见 [修复报告](Midora-Pure-MIDI-Range-Correctness-Repair-Validation-2026-09-09.md)，工程阻塞已解除。2026-09-09 用户确认人工 MEM-C01～C07 全部验收通过。

## 11. 后续诊断与文档收尾

- `ICompilerDiagnosticSequence` 使用完整 Int64 count / ordinal / severity；计数超限明确失败。超 Int32 筛选结果用 4096 行分页，过滤、全局统计和 Error / Warning-as-error 不截断。
- README 输出前 1000 条 Warning/Info 和准确总数/省略数，取代 §9 当时“完整全行输出”的要求；小报告 golden、取消和原子发布保持。
- [实施验证](Midora-Int64-Diagnostics-and-Bounded-Readme-Verification.md)记录 3182 个去重通过用例；后续[滚轮验证](Midora-Diagnostics-Wheel-Requirement-Trace.md)记录 Desktop 434/434、32 次实际路由事件和有界 WPF 容器。不累加重复重跑，也不将离屏测试当作真实窗口输入验收。
- 2026-09-09 用户确认人工大体全部通过；逐项精细回归留到真实完整编曲。F4 / Shift+F4 明确暂缓，现有 Go to Source 不变。
