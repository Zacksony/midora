# 阶段 6：40 项操作覆盖审计

日期：2026-09-09。本文是源码入口和证据层级审计，不修改 SRS；本文件建立时新增测试尚未执行，最终运行结果以阶段 6 验证报告及 TRX 为准。不得把本表的“存在自动入口”改述为全部规模、所有手势或人工验收已通过。

后续状态：本表及 §6 的历史失败证据保留。用户已授权独立 Pure MIDI 修复，最新 3157 / 3157 公共回归、三轮真实 MIDI 前后对照和当前 WPF 大会话均通过，见 [修复报告](Midora-Pure-MIDI-Range-Correctness-Repair-Validation-2026-09-09.md)。2026-09-09 用户确认 MEM-C01～C07 全部验收通过；不把人工通过扩展为表中未执行的自动测试。

## 1. Requirement trace

- 输入：冻结 Project revision、六类 Note/数值 Point owner、正式 Selection、Clipboard 和完整有效历史。
- 输出：保持源字段、稳定 ID、formal order、碰撞规则、canonical、Format 3 修改态保存/重开；不新增产品能力。
- 边界：SRS §20.3.15、§20.4.1/11/14～19、§20.6、INV-095～109。取消、旧修订、I/O/预算失败零发布；Note later-loses 与 Event later-wins 只作用于命中键；未命中 imported duplicate/opaque 不变。
- 持久化：Project Source Data 按既有 Format 3 严格 writer；History、Clipboard、测试 seed 不写入 Project。
- 运行时：prepared source、资源 lease 与 History 独立所有权；关闭之后只要求本测试独占存储归零，不以 WS 马上下降为 oracle。
- 非目标：不是完整 WPF 自动化、真实设备/听感验收、任意历史 RAM/磁盘上限，也不改变 SRS、格式、音频或 History 裁剪策略。

## 2. 审计发现与处置

1. **已确认索引错配**：原 O28 引用 `Stage5ArrangementRoutingTests`，它的五个测试只覆盖 Segment conversion/生成入口可用性，不能为 Track/Root/Usage 创建、排序、改绑、删除背书。实际结构测试是 `ProjectFlatArrangementEditCommandsTests`：共享组移入/移出、空 Usage/Root 原子删除与 Undo、Fixed 分散成员、shared rebind、拒绝拆散 block 等。现已补入主索引 O28，并注明原入口的证据边界。
2. **已确认大样本启动门**：`RealMidiSelectionReadPerformanceTests`、`WpfInteractionRegressionTests.OptInImportedPagedMidiRendersThroughTheWpfTimelineSurface`、`DesktopSessionControllerTests.OptInImportedSampleMeasuresCompletePagedSelectionEditPath`/`ProvidedProjectTimelineEditPerformanceProbe` 缺少对应 env 时直接 `return`，普通 TRX 的 Passed 不能证明真实 MIDI/外部 Project 已运行。分别检查 `MIDORA_REAL_SELECTION_READ_MIDI`、`MIDORA_UI_SAMPLE_MIDI_PATH`、`MIDORA_SCALE_MIDI_PATH`、`MIDORA_TEST_UI_PERF_PROJECT` 的显式运行记录。
3. **已确认跨操作缺口**：Stage 5 的 M13 单族 root/Clipboard 生命周期测试不能单独证明 F10。新增 `MemoryStage6CrossOperationTests` 以固定 seed `0x6D696436` 打乱 18 步独立命令顺序，覆盖三类 Note 的 Move/双边 Resize/Humanize、三类数值 Point 的反复调整，随后串联 Ready 取消、完整 Undo/Redo、新分支、双向 Note Clipboard、修改态 Save/Save Copy/Open、canonical 对照、导入重复/opaque 保留和 owned resource 最后释放。31 与 4,101 条/owner 分别进入小对象及分页路径。该测试只补这些明确动作，不代表 Split/Join/转换/UI 的额外场景。
4. **UI 证据必须分层**：`TimelineRenderingTests` 的真实 Surface/图像/手势测试、`Stage4DesktopSelectionIntegrationTests` 的 controller/adapter 集成、纯 Application 命令测试不同层次。前者已有三种 Note kind 的参数化呈现断言，但不能由其推导所有 Ctrl/Alt/Shift 与所有菜单都做过真实输入。最终人工 C 保留响应、焦点、正确图像及时出现等检查。
5. **时间链仍需独立实测**：单个命令、root swap 或 `EnsureCurrentCompilation` 的时间不等于输入→选择解析→提交→UI 可用→可见内容收敛→编译 current 的全过程。实际长会话探针负责 §5.3/§11 的并存、冷热与释放证据；本新增数据测试不声称完成该门。
6. **新增夹具修正，不是产品延迟结论**：首轮 Application 构建成功后，F10 的测试专用 `backgroundDebounce=10 min` 使后台 Worker 先进入长延迟，后续 immediate Ensure 无法打断这段已开始的等待；主任务已停止该未完成运行，不能计为通过或产品存在十分钟延迟。夹具恢复正式默认 75 ms，并为 Ensure 设置 60 s `WaitAsync` deadline。连续消费链另修正分页 oracle：canonical 使用完整 `QueryEventPages`，音频计划合并 resident Ports 与 `EventPageProvider`；分页 frame query 半开终点不要求返回终点消息，改由完整 canonical NoteOff 与 Unit fragment 精确 EndFrame 分别验证。修正后已重新构建并实测：F10 两例在 `application-stage6-r3` 通过；连续消费链在 `consumer-chain-r4` 的第一份保存文件重开后 full canonical 比较失败，精确失败及未执行边界见 §6，不将其记为整链通过。

## 3. 40 项适用边界、真实入口与自动证据

符号：N = Logical/Direct/Template Note；P = Logical Parameter/Direct Channel/SubVoice Event 数值点；S = Logical/MIDI Segment。每行列出自身自动入口，不能用其他行共享底层代替。本轮结果统一由阶段 6 验证报告关联到这些 suite；未开启 opt-in、未实测的 UI 全链路和设备验收必须另列。

| 项 | 适用 / 明确不适用 | 用户入口 → 自动证据 | 尚不能由该入口推出的结论 |
| --- | --- | --- | --- |
| O01 | N/P/S；Ctrl+A 限当前 scope | Canvas click/marquee/modifiers、Ctrl+A → TimelineObjectSelectionTests、WorkspaceSelectionScalabilityTests、SelectionUiFailureReproductionTests | 所有大源/缩放/修饰组合已实机验收 |
| O02 | 三类对象列表；异类只由显式类型子菜单处理 | 对象列表行/范围/类型菜单 → TimelineObjectListIntegrationTests、TimelineTypedSelectionSourceMappingTests | 完整 UI 长会话输入延迟 |
| O03 | N/P/S 的合法 owner/revision；opaque 只读其允许属性 | Scale/Properties 准备 → ProjectTimelineSelectionReaderTests、SelectionReadOptimizationTests、RealMidiSelectionReadPerformanceTests | 最后者未设 env 时不计 400k 真实样本 |
| O04 | N/P 创建删除；非任意 opaque 生成 | Draw/Delete → BoundedDirectMidiNoteEditTests、BoundedDirectMidiEventEditTests、BoundedPointCommandTests、ExactTimelineCollisionPolicyTests | 所有入口都有独立持久化性能测量 |
| O05 | N/P/S；异类必须共同兼容 | Draw drag/Select浮动Move → ProjectBatchTimelineEditCommandsTests、BoundedLogicalNoteCommandTests、WpfInteractionRegressionTests；新增 F10 | F10 不测试鼠标/浮动层，只测六 owner 编辑链 |
| O06 | 已支持 Copy Drag 的 N/S/P；Resize 不响应 Ctrl Copy | Ctrl drag、Alt force Move、Shift lock → ProjectObjectClipboardPureMidiTests、Stage4DesktopSelectionIntegrationTests、TimelineRenderingTests | adapter 通过不等于各 view 的每个实际修饰手势组合 |
| O07 | N/S；P 不支持 Resize | 左边缘/浮动ResizeStart → BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、TimelineRenderingTests；新增 F10 | F10 的双边调整不代替全部 Snap 最小长度手势 |
| O08 | N/S；P 不支持 Resize | 右边缘/浮动ResizeEnd → BoundedDirectMidiNoteEditTests、BoundedLogicalNoteCommandTests、TimelineRenderingTests；新增 F10 | 同上 |
| O09 | 所有支持对应手势的时间/值入口 | Snap按钮/快捷键、创建/视图外drag → TimelineNavigationTests、TimelineEventContextGestureTests、TimelineValueTraceSamplerTests | 没有覆盖到的 native input 时序 |
| O10 | N/P；依各类已有水平变换能力 | 类型菜单/变换 → ProjectSelectionTransformEditCommandsTests、ProjectAdvancedTimelineEditCommandsTests | 不扩大为 opaque 任意值变换 |
| O11 | S exposed content 与 content+window 两模式 | Arrangement Horizontal Flip → BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、ProjectMultiOwnerAdvancedEditCommandsTests | 不能只测 visible Notes 后宣称 hidden/参数已覆盖 |
| O12 | N/S；数值P不解释为pitch | Vertical Flip → ProjectAdvancedTimelineEditCommandsTests、ProjectAdvancedTimelineRootSwapProductionTests | 不将 Event value 当作 Key |
| O13 | N/P/S 的已批准Scale字段；混合S共同行为 | Scale dialog → ProjectSelectionTransformEditCommandsTests、ProjectMultiOwnerAdvancedEditCommandsTests、SelectionReadOptimizationTests | 字段读取性能不代替结果或实际图像收敛 |
| O14 | N/S；越界Note删除而不是整体clamp | Transpose → ProjectAdvancedTimelineEditCommandsTests、ProjectAdvancedTimelineEditIntegrationTests、DesktopSessionControllerTests.SelectionTransformUndoRestoresIdsOfNotesDeletedByTheTransform | 单测不替代视图立即更新 |
| O15 | N/P/S 既有数值字段 | Batch Edit dialog → ProjectBatchTimelineEditCommandsTests、BatchEditExpressionProgramTests、BoundedEditRoundTripTests | 不扩展 Mapping ABI / Batch profile |
| O16 | N 的 Tick/Gate/Velocity；Key不适用 | Humanize dialog → ProjectAdvancedTimelineEditCommandsTests、BoundedDirectMidiQueryRegressionTests；新增 F10 | F10只随机化Velocity，其他字段由专项覆盖 |
| O17 | N 三模式；表达式Maximum Cuts不限制其他模式 | Split dialog/Preset → BoundedDirectMidiSplitJoinTests、BoundedLogicalNoteCommandTests、NoteSplitPresetStoreTests | 大规模全模式 UI 已实机跑过 |
| O18 | N owner+key；不合并未选对象/跨owner | Join dialog → BoundedDirectMidiSplitJoinTests、BoundedLogicalNoteCommandTests | 不用单 owner 结果代替 mixed owner |
| O19 | N与三类P；opaque/Conductor不适用 | Quantize dialog → BoundedDirectMidiQueryRegressionTests、BoundedPointCommandTests、ProjectLogicalParameterQuantizeTests | 不新增 opaque/Conductor 工具能力 |
| O20 | N/当前合法P lane；opaque/Conductor不适用 | Batch Create Notes/Events + Preset → TimelineGenerationIntegrationTests、GeneratorExpressionProgramTests、TimelineGenerationPresetStoreTests | 生成器表达式测试不代表所有菜单上下文 |
| O21 | 对象已有共同属性；Mixed需显式赋值 | Properties/Ctrl+P/OK/Cancel → AdvancedEditDialogsTests、Stage4DesktopSelectionIntegrationTests、DesktopSessionControllerTests 多属性事务测试 | F10不打开Dialog；Cancel UI焦点仍由WPF门覆盖 |
| O22 | S/N/P与已支持ordered content；跨Project Paste不适用 | Copy/Cut/Paste、Clipboard替换 → ProjectClipboardStorageTests、ProjectClipboardPasteTargetTests、ClipboardSelectionRoutingTests；新增 F10 | F10双向Note Paste不包含Cut或所有structure payload |
| O23 | 既有Duplicate对象；Logical普通副本独立Usage，显式share才共享 | Duplicate/Ctrl+D/Share → ProjectObjectClipboardTests、BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests | 不允许恢复Duplicate Instrument Only旧入口 |
| O24 | Logical↔MIDI S；需冻结损失并确认 | 跨类型drag/copy/paste → SegmentConversionServiceTests、SegmentConversionPlacementAndHistoryTests、SegmentConversionRoutingTests、Stage5ArrangementRoutingTests | 类型转换不保证音色一致；禁止绕过损失确认 |
| O25 | P与Tempo的已有draw；非opaque | 自由/直线/Shift固定值tick → BoundedPointCommandTests、TimelineValueTraceSamplerTests、TimelineEventContextGestureTests、ConductorInteractionTests | F10值调整不模拟画线采样 |
| O26 | 合法lane/parameter owner；不按名称自动重建mapping | Add/Switch/Delete Lane、定义编辑 → BoundedPointCommandTests、MappingEditingPolicyTests、LogicalParameterEventBindingDialogTests、DesktopSessionControllerTests lane selection测试 | 删除mapping不得被普通编辑隐式恢复 |
| O27 | Logical/MIDI S；Split参数起点及hidden内容保留 | Segment Split/window crop → BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、BoundaryCleanupTests | Note Split不是Segment Split证据 |
| O28 | Track与shared owner；Root/Usage必须非空 | Header/brace drag、route/rebind/create/delete → **ProjectFlatArrangementEditCommandsTests**、ProjectObjectClipboardTests、DesktopSessionControllerTests | Stage5ArrangementRoutingTests不能单独覆盖该项 |
| O29 | Definition/SubVoice/Mapping/Loop/Pre-Roll | Workspace/properties/dialog → MappingEditingPolicyTests、LogicalParameterEventBindingCompilerTests、LoopEntryCompilationTests、PreRollCompilationTests | 与历史共存的性能还需阶段6整体探针 |
| O30 | 全部有效Project命令；Clipboard本身不Undo | Undo/Redo/新分支 → PagedSelectionAndEditTransactionTests、PureMidiCowRootTests、MemoryStage5HistoryOwnershipTests、WpfInteractionRegressionTests跨Workspace selection；新增 F10 | F10源值历史不包含UI完整selection bookmark |
| O31 | Arrangement、三种Piano、Velocity/P lane | 打开/平移/缩放/选择/编辑刷新 → TimelineRenderingTests、PagedTimelineCacheOnlyTests、PureMidiPagedPresentationTests、PagedLogicalPresentationTests | opt-in未开启不计真实18M；无computer-use不妨碍离屏WPF测试 |
| O32 | 五类Conductor；tick0必需状态保护 | 左列表、Tempo图/各lane → ConductorInteractionTests、ConductorProjectionTests、ConductorRenderingPerformanceTests、ConductorWorkspaceTests | 点LOD不改变正式事件；UI实感留人工C |
| O33 | Onion/All Tracks只读；Compiled混合源规则 | Layer图标/Raw/Compiled/来源切换 → OnionWorkspaceTests、TimelineOnionTests、OnionPresentationLifecycleTests、CompiledOnionNoteIndexTests | 不把只读层当编辑目标或Pure MIDI canonical gate |
| O34 | 全Project/正式CompileContext | 后台/Full/Incremental → PureMidiCompilationTests、PagedPureMidiCompilationTests、IncrementalCompilationTests、SourceTraceTests；新增 F10 | F10只在最终修改态比较一次Full/Incremental |
| O35 | canonical成功且SoundFont/设备条件满足 | Play/Preview/Seek/MuteSolo → PagedPureMidiAudioPlanTests、PreviewCompilerTests、Playback全套 | 托管计划测试不等于原生设备/听感/实际deadline |
| O36 | 正式MIDI/Audio任务模式；运行Mute/Solo不改成品 | Export MIDI/Render Audio → PureMidiExportTests、MidiExport/AudioRender全套 | 波形托管oracle不等于任何SF2/SFZ音质声明 |
| O37 | 新建/导入/打开/Save/Copy/Close | File菜单/项目会话 → PureMidiReadOnlyPersistenceTests、ProjectPersistenceCoordinatorTests、真实M01探针；新增 F10 | F10只补修改态保存/拷贝/重开，不模拟所有菜单 |
| O38 | Format1/2只读迁移、Format3当前writer | Open旧格式/确认Upgrade/损坏处理 → PersistenceContractV1/V2Tests、ProjectPresentationSchema2Tests、MidoraProjectPackageFaultInjectionV1Tests、FormatMigrationDesktopTests | 新F10不替代旧格式永久副本或磁盘故障注入 |
| O39 | Workspace/session/backend任务生命周期 | Tab隐藏关闭、Project替换、Cancel → SelectionPresentationLifecycleTests、OnionPresentationLifecycleTests、WpfMemoryProbeTests、WorkspaceLifecycleTests | 数据链资源归零不等于真实视觉树弱引用已释放 |
| O40 | imported Meta/SysEx仅既有允许字段/操作 | Properties/Copy/Cut/Paste/Delete/Segment变换 → ProjectObjectClipboardPureMidiTests.ImportedMidiEventsCanMoveCopyDeleteAndRoundTripThroughClipboard、MixedTimelineSelectionCommandsTests、BoundedDirectMidiSegmentTransformTests、OpaquePayloadBudgetTests；新增F10保留断言 | 不量化/任意生成opaque，不将小payload当M07大payload证据 |

## 4. 执行与结果边界

新增专项建议命令（由主任务串行调度，不与其他构建/压力并行）：

```powershell
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj -c Release --filter FullyQualifiedName~MemoryStage6CrossOperationTests
```

测试本身生成的目录只位于对应测试可执行目录的 `.tmp/memory-stage6-cross-<guid>`，所有Project写入测试副本。保存取消检查已存在副本的完整字节未变；清理只删除本测试直接创建的精确目录。小数据snapshot/数组是oracle，不是生产全量读取入口。

最终应同时保留：专项实际结果、完整公共回归、opt-in明确开关、受守护大样本/真实WPF并存数据、未测项和用户MEM-C01～C07结论。上述几种证据不能互相替代。

## 5. 冷/热缓存、右界与消费链的确定入口

下表逐一读取了测试体及启动门；“无 opt-in”表示它会执行自己的合成数据/离屏 WPF 路径，不表示本轮已经执行。测试结果与耗时仍以主任务串行产生的 TRX 为准。

| 边界 | 确认入口（类名.方法名） | 实际 oracle / 限制 |
| --- | --- | --- |
| 冷 overview | `TimelineRenderingTests.ColdRulerOverviewSchedulesPrefetchWithoutForegroundDecode` | 真 STA Surface + 阻塞源；前台零 decode，prefetch 已启动，整体 < 1 s / 内容阶段 < 250 ms；无 opt-in |
| Piano 越过右界 | `TimelineRenderingTests.PianoViewportRightOfActiveRangeStillSchedulesSourceContent` | active [0,1000)，viewport [2000,3000)，仍发起异步范围 fingerprint；内容阶段 < 100 ms；不是最终像素 oracle |
| 右界出现/退出临界 | `TimelineRenderingTests.SegmentRangeBoundaryNeverPerformsColdPrimarySelectionLookup` | viewport 起点从 1000 进入 999；selected primary 不进行阻塞 ID lookup，进入渲染 < 250 ms；不等于反复进出后的最终图像已验证 |
| Velocity / Event lane 越过右界 | `TimelineRenderingTests.LaneViewportRightOfActiveRangeStillSchedulesSourceContent` | 两个 mode 各一例；范围调度与 < 100 ms；无 opt-in，同样不是最终像素 oracle |
| 三 mode 冷 fingerprint | `TimelineRenderingTests.ColdTileFingerprintNeverBlocksForegroundRender` | PianoRoll / Velocity / EventLanes 三例；前台零同步 Query，后台 fingerprint 已启动，前台 < 500 ms |
| 三 mode 冷页→图像 | `TimelineRenderingTests.ColdVisibleRegionConvergesToItsExactPixelsWithoutAFingerprintRoundTrip` | 三例清空缓存、与空白像素比较并 pump 到内容出现，最长 2 s；Piano 例使用 DirectMidiNote，不覆盖三种 Note 的实际 provider 与全套右界往返组合 |
| 热 LOD / 精确投影失效 | `TimelineRenderingTests.QuantizedPianoLodIsSharedByAllThreePianoNoteKinds`、`PianoZoomUsesExactProjectionAndCancelsObsoleteScaleWork` | Logical / Direct / Template 三 kind 的共享量化 LOD；同一 LOD 内缩放仍更新精确投影并取消旧任务；后者不是速度压力曲线 |
| 六类真实分页适配器 cache-only | `PagedTimelineCacheOnlyTests.ColdPagesArePendingWithoutReadingAndBecomeReadyAfterPrefetch` | logical、logical-velocity、template、template-velocity、parameter、event 六例；1024 条冷页 pending 时零读取，prefetch 后精确 ID，混合冷热查询不能发布部分结果；本 suite 没有 Direct 专例 |
| 三种实际 Note provider | `PagedLogicalPresentationTests.DensePianoLowLodRasterWorkIsBoundedByPixels`、`PagedPianoSourcesKeepAdjacentNoteStartsVisibleAtLowLod` | logical/direct/subvoice 各三例；真实 provider 的像素工作预算/相邻起点保留；不是完整冷启动 Surface 时间链 |
| Direct 空白/隐藏内容 | `PureMidiPagedPresentationTests.DirectMidiNoteSnapshotRejectsBlankRangePastContentWithoutSourceQuery`、`DirectMidiOverviewExtentIncludesEventsBeyondTheExposedSegmentRange` | maxEnd=840 的源请求 [1000,9000) 不访问源；overview 保留 exposed range 之外的事件 extent；无 opt-in |
| Onion 缓存/裁剪 | `TimelineOnionTests.ClipsHiddenContentAndMapsAbsoluteTimeWithoutSelectionState`、`BlockFingerprintIsIndependentAndFarRangesDoNotReadSources`、`ManyTracksUseBoundedVisibleBlocksAndDeviceBuffers` | 只读层内容裁剪、远范围不读源，100/200/400 Track 有界 block；不能为可编辑层手势背书 |
| canonical 冷/热与失效 | `PlaybackTests.ExactPlaybackRangeReplayReusesCanonicalResultUntilSourceChanges`、`SampleDomainInvalidationDoesNotDiscardTickDomainPlaybackRangeCache`、`RepeatedRealtimePlaybackReusesSamplePlanAndOnlyClonesMonitoringState` | 同范围对象复用；编辑后失效；sample-domain 失效不丢 tick-domain；Mute/Solo 仅 clone monitoring 并共享事件存储；无原生设备 |
| active reader + LRU | `PreparationStorageCacheTests.SharedBackingIsChargedOnceAndActiveReaderSurvivesEvictionAndClear`、`ActualCanonicalAndMonitoringViewsShareStorageWithoutReinterpretingEvents`、`ControllerLeasesAreEstablishedBeforeBackendStartAndReleasedOnFailureOrStop` | 实际 canonical/plan 的共享存储与 active lease；小预算 1000 次准入后的 eviction/clear；fake backend 正常/失败的 lease 时机；不能推导 native handle / PCM 已释放 |
| seek / Loop / Shared / NoFX | `PlaybackTests.StartStopSeekAreColdStartsAndLockEdits`、`CanonicalAudioUnitProjectionTests.ShortGateLoopReachesAudioPlanAndInvalidatesPreviouslyUnloopedPcm`、`SharedMidiRootProducesOneChannelStateFragmentAcrossChildTracks`、`PureMidiEffectsControllersRemainCanonicalButAreIgnoredByNoFxAudioProjection` | controller 的 fake backend Start/Seek/Stop，以及四种数据/计划语义 oracle；无设备或真实 SoundFont 听感判断 |
| 本轮连续小链（新增） | `MemoryStage6ConsumerChainTests.MixedEditsReachColdWarmSeekPlansMidiExportAndEditedPackageRoundTrip` | 同一 4 Note 小工程含 Loop、Pre-Roll、映射、共享 Logical Usage / Pure Root；设计链为 3 正式编辑→Full/Incremental→[300,600) 状态恢复且不重触旧 Note→44,100 / 48,000 / 50,123 Hz 的 realtime/offline 计划→Mute 隔离→sample 失效与 260 范围 LRU 驱逐重访→独立 MIDI-export context/字节 oracle→修改态 Save/Copy/Open→Undo/Redo。前半段精确断言前移起点、loop pitch、映射 CC11=42、编辑后 Direct CC11=55、7/16 s frame 数、Direct NoteOff velocity 与 opaque/CC91。当前 `consumer-chain-r4` **Failed**：原工程计划与 MIDI oracle、Save 与 Save Copy 写入、第一份文件 Open 及源属性断言已执行通过，随后 full canonical 对照失败；该文件重开后的 MIDI 字节/range/plan、第二份文件 Open、本测试尾部 Undo/Redo 均未执行，不能视为已通过。详见 §6。只做托管计划，不写/听 PCM，不初始化 native 或设备 |

`PagedPureMidiAudioPlanTests.PagedRootCarriesCacheOwnershipPresetSummaryAndBoundedIpcMetadata` 另补真实小 pack 的 plan ownership/preset summary/计划文件往返；`AudioRenderTaskRunnerTests.WholeMixPublishesValidatedFloatWaveAtEveryRequiredSampleRate` 使用 fake worker 覆盖 8,000 / 44,100 / 48,000 / 50,123 / 192,000 Hz 的 WAV 发布。两者不应误写成真实原生合成通过。

### 5.1 显式 opt-in 与不得冒认的 Passed

这些测试缺少环境变量时直接返回，因而常规 TRX 中的 Passed 不能证明样本路径执行。必须同时保留冻结样本路径/哈希、env、测试输出和 TRX。只对本任务已授权样本设置开关，不搜寻项目外素材。

| 环境变量 | 测试入口 |
| --- | --- |
| `MIDORA_REAL_SELECTION_READ_MIDI` | `RealMidiSelectionReadPerformanceTests.MeasureFourHundredThousandActualMidiNoteProperties` |
| `MIDORA_UI_SAMPLE_MIDI_PATH` | `WpfInteractionRegressionTests.OptInImportedPagedMidiRendersThroughTheWpfTimelineSurface`；`PureMidiPagedPresentationTests.OptInImportedSampleKeepsSmallSelectionBoundedAfterLargeFlip`、`OptInImportedSampleStreamsExactLargeMarqueeBeyondDecodedCache`、`OptInImportedSampleProvidesArrangementAndEditorItemsFromPagedContent` |
| `MIDORA_SCALE_MIDI_PATH`，可选 `MIDORA_SCALE_EDIT_COUNT` | `DesktopSessionControllerTests.OptInImportedSampleMeasuresCompletePagedSelectionEditPath` |
| `MIDORA_TEST_UI_PERF_PROJECT` | `DesktopSessionControllerTests.ProvidedProjectTimelineEditPerformanceProbe` |

原生测试另有 `MIDORA_BASS_NATIVE_DIR` / `MIDORA_TEST_SOUNDFONT_PATH` 等前提。已确认 `Midora.Audio.Bass.Tests/NativeAudioIntegrationEnvironment.cs` 在 native dir env 为空时仍有旧 `%LOCALAPPDATA%/Midora/Native/BASS/win-x64` fallback；该旧 fixture 与当前仓库“不得探测旧目录”的约束冲突，本轮审计不调用它、不读取旧路径，也不改生产/测试基础设施。最小回归明确排除 native fixture；普通 managed 计划结果不能替代 native 验收。

### 5.2 最小串行补测建议

在主任务已完成相同 revision、Release 配置及同输出目录构建后，依次执行以下三组；如果主任务使用隔离 OutDir，应针对那份已冻结测试 DLL 使用等价 `dotnet vstest /TestCaseFilter:...`，不得混用旧默认 bin。以下 `--no-build` 只是避免重复构建，不代表已有构建证据。每组必须生成独立 TRX，且不与 WPF/大型 MIDI/其他 build 并行。

```powershell
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~MemoryStage6CrossOperationTests|FullyQualifiedName~MemoryStage6ConsumerChainTests|FullyQualifiedName~PagedPureMidiAudioPlanTests" --logger "trx;LogFileName=stage6-consumers.trx"
dotnet test src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ColdRulerOverviewSchedulesPrefetchWithoutForegroundDecode|FullyQualifiedName~PianoViewportRightOfActiveRangeStillSchedulesSourceContent|FullyQualifiedName~SegmentRangeBoundaryNeverPerformsColdPrimarySelectionLookup|FullyQualifiedName~LaneViewportRightOfActiveRangeStillSchedulesSourceContent|FullyQualifiedName~ColdTileFingerprintNeverBlocksForegroundRender|FullyQualifiedName~ColdVisibleRegionConvergesToItsExactPixelsWithoutAFingerprintRoundTrip|FullyQualifiedName~PianoZoomUsesExactProjectionAndCancelsObsoleteScaleWork|FullyQualifiedName~QuantizedPianoLodIsSharedByAllThreePianoNoteKinds" --logger "trx;LogFileName=stage6-cold-right-boundary.trx"
dotnet test src/midora-core/Midora.Playback.Tests/Midora.Playback.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~PreparationStorageCacheTests|FullyQualifiedName~SessionPreparationLifecycleTests|FullyQualifiedName~ExactPlaybackRangeReplayReusesCanonicalResultUntilSourceChanges|FullyQualifiedName~SampleDomainInvalidationDoesNotDiscardTickDomainPlaybackRangeCache|FullyQualifiedName~RepeatedRealtimePlaybackReusesSamplePlanAndOnlyClonesMonitoringState|FullyQualifiedName~StartStopSeekAreColdStartsAndLockEdits|FullyQualifiedName~ShortGateLoopReachesAudioPlanAndInvalidatesPreviouslyUnloopedPcm|FullyQualifiedName~SharedMidiRootProducesOneChannelStateFragmentAcrossChildTracks|FullyQualifiedName~PureMidiEffectsControllersRemainCanonicalButAreIgnoredByNoFxAudioProjection" --logger "trx;LogFileName=stage6-audio-plan-storage.trx"
```

需单独补实际 provider/cache-only 而非 Surface 调度时，再串行运行 Desktop.Tests 的 `PagedTimelineCacheOnlyTests`，及上表 `PagedLogicalPresentationTests` 的六个参数化 case 与两个 Direct 非 opt-in 方法。真实大样本 WPF 探针、长会话 WS/Private/自然 GC/退出释放、右界完整往返最终图像及用户 MEM-C 结论仍单独记账；这组三命令不能替代它们。

## 6. 连续消费链暴露的 Pure Root 范围投影缺口：设计与追踪

本节是 2026-09-09 的源码审计和候选修复设计，不修改 SRS、不新增音频规则，也不将候选方案记为已实现。

独立阻塞记录与当时索引评估见 [Pure MIDI Range Blocker](Midora-Memory-Stage6-Pure-MIDI-Range-Blocker-2026-09-09.md)。独立反证已实测确认真实 hard-boundary 缺失，不再仅是源码推断；本节描述的是获准修复前 Compiler 冻结的历史阶段。实际后续算法、界限与结果以上述修复报告为准，不将以下候选设计直接当作最终实现。

### 6.1 已执行证据与事实

- `application-stage6-r3.trx`：F10 两例及 cleanup 八例通过，ConsumerChain 失败于测试错误地使用 `CanonicalEventRole.NoteOn` 搜索 Direct Note。既有编译器明确将源 Direct NoteOn/NoteOff 标为 `DirectMidi`；测试现按 `MidiMessageType.NoteOn` 查找，并额外断言角色、来源、tick=360、Port=1、Channel=9、key=38、velocity=90，没有放宽音乐事件预期。
- 独立 `consumer-chain-r4`：受守护 Release 构建成功，唯一测试执行约 2 s 后失败。此前已走过范围恢复、三个采样率、监控隔离、260 范围驱逐/重访和原工程 MIDI 导出。守护正常结束、exit=1、无 stop reason，构建+测试进程树峰值 Private=606,621,696 bytes、WS=761,311,232 bytes。
- 失败位于修改态 Save/Open 后 full canonical 对照：63 项中前 60 项相等；最后三个 tick=1920 的 Pure `RootBoundaryCleanup` 消息、顺序、Source 和 SMF 归属一致，但 `StableOrder` / `SemanticGroup` 不同。materialized 的 order 从 `4611686018427387915` 开始，paged 从 `2305843009213695871` 开始。TRX：`.tmp/memory-stage6/consumer-chain-r4/results/consumer-chain-r4.trx`。该轮未越过断言执行重开后的 MIDI 编码与范围对照，不能宣称它们通过。
- 独立 `compiler-range-proof-r1`：Release 构建成功，`PureMidiRangeLayoutEquivalenceTests.EarlyConsumerEndClosesSharedRootNotesAndResetsStateInBothSourceLayouts` 唯一用例失败，汇总 927 ms。相同 source 的 `[300,600)` 完整分页查询：materialized 声明/查询 11 个事件、声明/实际 1 个 NoteOn；paged 声明 12 个事件/2 个 NoteOn，但只查询到 6 个事件/1 个 NoteOn。缺 tick=600 的 NoteOff36/0、NoteOff38/0、CC120=0、CC11=23、CC91=0、CC121=0 整组六条，起点又多一条 CC11 默认值并有 restore metadata 差异。materialized 独立预期通过、paged 独立 Note 序列预期失败，不受 full metadata 差异阻挡。守护 exit=1、无 stop，峰 Private=263,970,816 bytes / WS=420,859,904 bytes；TRX：`.tmp/memory-stage6/compiler-range-proof-r1/results/compiler-range-proof-r1.trx`。详细输入、契约、逐项输出、索引代价和未决阶段划分见上述独立阻塞记录。

### 6.2 正式契约与根因

输入是内容、稳定 ID、Arrangement 顺序、routing 和 CompileContext 相同、仅内存/分页 backing 不同的 Project。输出要求完整 canonical 事件及 provenance/顺序相同，并由该结果进入 MIDI 与音频计划。依据 SRS §12.1.2、§12.4、§12.10.7、§12.11.7、§12.21.2，§23.6～23.7，以及 INV-009/010/015/025；执行计划 §5/§11 另明确不同页边界下形式和语义一致。

`StableOrder` / `SemanticGroup` 不是可在测试中静默删掉的磁盘 offset：它们是公开 `CanonicalMidiEvent` 字段，参与 canonical 总序、state group 恢复/折叠及 result fingerprint。SRS 未规定其数值编码方式，但选定实现必须在相同正式输入下确定；因此本轮保留完整字段对照，不用归一化来掩盖差异。

已确认两条生产路径不一致：

1. materialized Pure 先创建 Root interval end，再被通用 `ApplyRange` 在 endTick 排除，按所有 Unit 共用的 `long.MaxValue / 2` sequence 重建。`AssignPureMidiRangeBoundaryOwnership` 只重新赋 Role/Source/SMF owner，没有统一 order/group。
2. paged Pure 不经该 `ApplyRange`，`AppendLifecycleRange` 直接使用 `long.MaxValue / 4 + interval.EndTick` 的 Root-local sequence。
3. 更重要的是，Root interval 只按 consumer range 过滤而不裁断；paged lifecycle 仅在自然 interval.EndTick 进入 query 时输出终点。RootEnd=1920、消费者结束=600 时，该路径没有对应的 range-end Pure NoteOff/CC120/final Reset；Note endpoint 查询也只返回自然或子 Segment 截断端点。因此不能只统一上述数字后宣布修复完成。

现有 `PagedPureMidiCompilationTests.MaterializedAndPagedDirectEventsShareOverflowSafeCanonicalOrdering` 的 oracle 只取 `Role=DirectMidi && DirectMidiObjectId!=default`；`NonzeroRangeDoesNotRetriggerActiveDirectNoteInEitherStorageMode` 也只比较带 Direct object identity 的自然端点。这些断言天然遗漏 Root 生命周期，不能反证本次缺陷。

对已经实测的 full 尾部 numeric 差异，SMF channel projection 不携带这两个字段，普通 audio fragment hash 也不写它们；预计仅该差异不改变本夹具的 MIDI 字节/声音。此为源码推断，不是重开导出实测。materialized/paged 的正式 Pure plan fingerprint 还分别使用普通 fragment 编码与分页 source descriptor 编码，不能把跨 backing 的 cache-key 差异全部归因于两个 numeric 字段。真正缺失的 range hard boundary 则会改变正式事件，必须独立修复和验收。

### 6.3 候选共享切片与不得绕过的 FIFO 索引条件

拟采用一个 Pure Root 范围投影实现；Logical 的正式 `ApplyRange` 不扩改。小对象源继续保留既有小型 materialized canonical 形态，不强制建 pack；分页源继续通过 endpoint/state checkpoint、paged render/SMF provider 查询，不做完整 pack→对象列表回退。

共享范围投影应同时负责：

1. interval-start 初始化与 nonzero range-start 非 Note 状态恢复；不重触范围前 NoteOn。
2. 原始 Direct event 的同 tick explicit order、稳定 identity、NoteOff velocity 和 opaque 原样投影。
3. 子 Segment、Root interval、Project/consumer end 的单次精确清理；在 consumer end 已存在自然 Direct NoteOff 时保留其原始 velocity，不重复补发。Root final cleanup 的 SMF owner 按 §23.7.5 的最低顺序参与 Track 冻结。
4. 同一套角色、order/group、Source 和 SMF metadata 编码；故障/取消不发布半成品。

**当前不能采用“只查询原始 gate 跨越边界的 Notes”来替代 FIFO 活动来源。** 例：同 Root/key 的 A=[0,100)、B=[10,20)，在 tick=20 的 B 原始 NoteOff 按 FIFO 关闭 A；tick=30 的存活来源是 B，而 `QueryActiveValues(30)` 只返回 A。此时计数相同，但来源不同；cold start 旧 NoteOff 与范围内新同 key Note 的组合也必须保留原正式行为。

已确认 `IPureMidiPlaybackEndpointSource` 只有 starts/ends/原始 gate-active/ordered channel 查询，没有 Root/pitch prefix count、reverse/select ordinal 或 Note FIFO checkpoint。现有 `ChannelStateCheckpoint` 只存 controller 状态，不能用来恢复 FIFO。朴素从 Root 起点重播到每个 seek 会扫描完整前缀，不能作为本轮隐藏回退。

索引候选是在修订冻结的编译准备阶段，用有界流式构建 Root/pitch 的 NoteOn ordinal 与 FIFO 消耗 checkpoint（包括 raw Note 和 Segment hard boundary），按最近 checkpoint 重播被请求窗口，并将该索引纳入 compiler/session 明确 ownership、失效和关闭释放；需要额外一次构建与临时索引容量/生命周期验证。它只索引既有 SRS 语义，不改变 Project 格式、听感、冷启动规则或音频算法。实现前必须明确该索引的共享/预算/释放与小源快路径，不得以一个无界 `Queue<SourceReference>` 或全曲 seek 扫描冒充有界方案。

待补独立 oracle：一个 Root 的全范围与 [30,60) 提前结束、shared Root 已结束 sibling 的 CC 状态、A/B 同 key 交错端点、范围内新同 key Note、自然端点恰等于 endTick 的 NoteOff velocity、同 tick 次序和 opaque；分别运行 materialized/paged、改变 page 边界、repeat/Full/Incremental 和 Save/Open。预期音乐消息/来源应显式写出，不能仅互相比较两个同样错误的实现。
