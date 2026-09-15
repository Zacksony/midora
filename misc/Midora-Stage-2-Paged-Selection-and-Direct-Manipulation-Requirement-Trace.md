# Midora 阶段 2：分页选择、编辑事务与直接操作需求追踪

- 日期：2026-08-31
- 状态：已于 2026-09-02 完成并经产品所有者验收
- 上位计划：`Midora-Major-Editing-and-Visualization-Expansion-Implementation-Plan-2026-08-31.md` 的 WP-01、WP-02、WP-11
- 规范依据：《Midora SRS》第 20 章、INV-095～INV-100
- 架构依据：`Midora-Paged-Selection-and-Edit-Transaction-Architecture-Decisions.md`

## 1. Requirement trace

| 输入 | 正式输出 / 可观察结果 |
|---|---|
| revision-bound Timeline source、stable IDs、owner、tick/key/lane 范围 | ordinal/page/range query、source trace、流式 stable-ID 解析 |
| Workspace 小型或百万级 Selection | compact stable-ID set，或绑定 owner revision 的 interval/bitmap + sparse exceptions descriptor |
| 大型编辑候选与工具变换 | detached staging、验证后的 immutable result root、精确 owner/range/page change set、一个 compact Undo |
| Timeline 右键 click/drag/double-click | 冻结 target/selection/tick 的确定性状态机；固定 `[0, 300 ms)` 与水平、垂直位移分别不超过 `6 DIP` 的自有双击判定；冷页查询不阻塞 UI |
| 同质 Selection 与现有命令 adapter | 默认 Follow 的矢量浮动工具；手动移动、Move、能力受限的 Ctrl Copy+Move、Note/Arrangement Segment 双边 Resize；所有规模复用普通直接操作预览并显示有效 delta |
| Preview Keyboard / Note creation / Resize / Velocity / Initial State / Modal | 本阶段批准的直接交互与焦点修复 |
| 当前已提交 Project revision | 每次 Playback Start 都包含刚创建但尚未 Save/Reopen 的 Instrument/Usage/Track/Segment/Note |

## 2. 数据、运行时与持久化归属

- stable ID 是唯一业务身份；ordinal 只属于 `owner + revision` 的运行时查询地址。
- Selection、浮动工具位置、Pin/Follow、pending right gesture 都是 Project Session UI State；本阶段没有新增 `.midora` 字段或 Project Format 变更。
- detached staging 与 spill 只属于单次运行；spill 只能进入 `<ProgramRoot>\.tmp\CompilerRuns` 的 owned directory。
- change set/source trace 是一次成功提交的运行时通知，不是第二套 Project 数据，也不持久化。
- Undo 保存 immutable old/new roots/pages 与必要的小型映射；Redo 不重新执行表达式、随机数或碰撞归并。

## 3. 边界与失败条件

| 边界 | 必须行为 |
|---|---|
| source revision 改变 | 旧 selection descriptor / edit transaction 明确失败，不把 ordinal 解释为新对象 |
| 取消 | 最多在当前 256-record 检查段后观察；不发布 partial root |
| working / resident / spill / record 预算超限 | 发布前失败，清理 owned run；不得自动转为无界内存 |
| staging build、结果验证或 root publish 失败 | owner root、revision 与 Undo stack 保持不变 |
| Right Drag 越过阈值 | 立即取消菜单候选并进入既有 trace，不等待冷页 exact hit |
| Right Click 未拖动 | 使用 Right Down 冻结 target/selection；首个 Right Up 后固定等待 300 ms，并等待冷页异步命中完成 |
| Right Double-click | 同一 Surface 合法内容区、首个 Right Up 至第二 Right Down 为 `[0, 300 ms)`，且 `|dx| <= 6 DIP`、`|dy| <= 6 DIP`；Draw↔Select，其他工具→Select，不打开单击菜单；不依赖系统双击设置或 WPF ClickCount |
| Escape / capture loss / Workspace 失效 | 取消 pending gesture，不修改 Project |
| Selection 异类或无共同直接编辑语义 | 不显示浮动工具，不静默处理兼容子集 |
| 任意规模 Selection Move/Resize | 显示 post-Snap/post-Clamp 有效 delta；小选择即时矢量、大选择 raster tile，浮动工具不得按数量隐藏预览；不得建立逐对象 WPF controls 或完整对象图 |
| Initial State 非法格式/无效 target | 拒绝；只有合法整数越界值 Clamp |

默认资源 profile：

| 项 | 值 |
|---|---:|
| page records | 4,096 |
| cancellation check interval | 256 records |
| per-page decoded/encoded working buffers | 64 MiB |
| resident staging bytes | 64 MiB |
| owned spill bytes | 16 GiB |
| candidate/result records | 100,000,000 |

## 4. 实现映射

| 能力 | 主要实现 |
|---|---|
| Common paged source contract | `Midora.Domain/TimelineObjectPaging.cs` |
| Logical/Template/Parameter persistent snapshots | `Midora.Domain/PagedTimelineCollections.cs`、`PersistentTimelineSequence.cs` |
| Direct MIDI Note/Event/Opaque source adapters | `Midora.Domain/PureMidiTimelineObjectSources.cs`、`PureMidiPagedContent.cs` |
| Detached staging、预算、change set、compact Undo | `Midora.Application/DetachedPagedEditTransaction.cs` |
| Initial State target clamp | `Midora.Application/MidiStateValueRules.cs`、`ProjectMidiStateEditCommands.cs` |
| Compact Workspace stable-ID set | `Midora.Desktop.Presentation/Interaction/CompressedMidoraIdSet.cs`、`WorkspaceState.cs` |
| Note delta Snap policy | `Midora.Desktop.Presentation/Interaction/TimelineToolPolicy.cs` |
| 浮动工具、右键状态机、resize preview、Velocity marker | `Midora.Desktop.Presentation/Controls/TimelineSurface.cs` |
| Preview Keyboard 黑键 velocity | `Midora.Desktop.Presentation/Controls/PianoKeyboardSurface.cs` |
| Modal focus restore / Initial State UI clamp | `Midora.Desktop/MainWindow.xaml.cs` |

## 5. 自动验证映射

| 风险 | 自动测试 |
|---|---|
| 1,000,000-object 流式 range selection、页数与取消延迟 | `PagedSelectionAndEditTransactionTests` |
| spill repeat read、预算拒绝、Dispose 清理 | `PagedSelectionAndEditTransactionTests` |
| canceled build、build failure、revision race、一次 root swap、compact undo | `PagedSelectionAndEditTransactionTests` |
| compact stable-ID add/remove/contains/enumeration | `CompressedMidoraIdSetTests` |
| Note 初始 49 + Snap 192 的 delta 结果 | `TimelineRenderingTests.NoteCreationSnapPreservesItsFrozenInitialLength` |
| 冷页右键不在 WPF thread 同步 exact hit、冻结 target | `TimelineRenderingTests.DelayedRightClickMenuNeverRunsAColdExactHitOnTheWpfThread`、`RightButtonDownFreezesItsColdContextTargetUntilMouseUp` |
| 浮动工具默认 Follow、双边 Resize、Ctrl Copy 能力门、所有规模 delta 与共享预览 | `TimelineRenderingTests` 的 `SelectionFloatingTool...` 与 drag-preview 回归用例 |
| 新建逻辑内容首次播放 | `PlaybackTests.FirstPlaybackIncludesLogicalTrackCreatedAfterPlaybackControllerConstruction` |
| Initial State clamp/target 边界 | `ProjectMidiStateEditCommandsTests` |

自动测试不能替代 18M 实际样本下的 WPF 手感、Pointer Capture、固定 300 ms 右键状态机和真实内存/临时磁盘观察；该人工验收已由产品所有者于 2026-09-02 完成。

## 6. 明确非目标

- 本阶段不实现 Instrument Catalog、快捷 Parameter Mapping、Humanize、Split/Join/Quantize、Batch Create、Conductor 重写、左侧 Timeline 对象列表、Onion 或 All Tracks；它们属于阶段 3～8。
- 本阶段不改变音乐语义、canonical event ordering、MIDI 导入/导出语义或音频后端。
- 本阶段不要求把所有既有高性能命令机械改写为同一个 transaction 类；但后续百万对象工具必须遵守共同 source、预算、原子发布和 compact Undo 边界。
- 本阶段不宣称通过自动测试完成了 18M 真实样本人工验收。

## 7. 自动验证证据

| 验证集 | 结果 |
|---|---:|
| `Midora.Desktop.Presentation.Tests` | 329 / 329 passed |
| `Midora.Desktop.Tests` | 181 / 181 passed |
| `Midora.Application.Tests` | 490 / 490 passed（其中 Stage 2 分页/事务专项 10 / 10） |
| `Midora.Compiler.Tests` | 338 / 338 passed |
| `Midora.Playback.Tests` | 114 / 114 passed |
| `Midora.Persistence.Tests` | 89 / 89 passed |
| `Midora.AudioRender.Tests` | 37 / 37 passed |
| `Midora.MidiExport.Tests` | 41 / 41 passed |
| `Midora.Common.Tests` | 79 / 79 passed |
| `Midora.Desktop` Debug build | 0 warnings / 0 errors |

合计 1,662 个自动测试通过。`git diff --check` 无 whitespace error；输出的 LF→CRLF 信息是仓库现有 Git 行尾转换提示，不是 diff error。
