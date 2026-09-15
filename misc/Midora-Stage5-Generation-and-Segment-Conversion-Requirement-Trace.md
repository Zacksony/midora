# 阶段 5：批量创建与 Segment 双向转换

日期：2026-09-06。状态：已实施，自动验证完成，用户已验收通过。授权：阶段 4 及有限内存修复验收通过后，用户明确要求继续下一阶段。

## 需求来源与边界

依据大型编辑扩展实施计划 §5.12、§5.13、阶段 5，以及已定案 EDIT-14～21、EDIT-30～31、GEN-01、SEG-01～06。本轮不进入阶段 6～8，不改音频语义、不发布 `dist`。

| 项目 | 输入 | 正式输出 | 失败与边界 |
|---|---|---|---|
| 批量创建音符 | 冻结 owner、Base Tick、4 字段表达式与 Initial、候选上限、可选相对起点上限 | Logical / Direct / Template Note 分页 root 与精确结果选择 | 受限表达式 DAG、finite、checked tick、正式字段 clamp、Note exact collision 后来者删除 |
| 批量创建事件 | 当前正式数值 lane、Value/Tick 表达式与 Initial、同样的生成预算 | Direct Channel Event / Logical Parameter Point / Template MIDI Event 分页 root | 不含 opaque / Conductor；target 不从 preset 推断；同 tick+target 后来者覆盖 |
| Segment 转换 | 不可变选择或 Clipboard、全局轨道相对偏移、目标 tick/track、损失确认 | 完整目标 Segment（含 hidden Notes）、一次 Undo、目标选择 | 原子验证 overlap / 绑定 / crop；全部非共同字段汇总确认；失败保留源 |

## 运行时与持久化归属

- 表达式仅在 detached staging 中执行，生成后只保留普通正式音符/事件，不保存 generator 到 Project。
- 新工具 profile 为 `midora.tool.generate-note/v1`、`midora.tool.generate-event/v1`；不改变 Batch Edit profile 或 Mapping Function ABI。
- `i` 零基，`*0` 是上一轮正规化结果（首轮 Initial），`*1` 是当前依赖计算结果，`tr` 是输入 `t0`。Initial 首对象开关默认关闭；开启时直接生成 candidate 0，后续表达式从 1 开始。
- Preset 属于 `<ProgramRoot>\Data\Presets`，保存表达式、Initial、首对象开关和上限，不保存 Base Tick、owner 或 lane 身份。
- 事务复用 SRS 20.4.11：4,096 records/page、周期取消、64 MiB working / 64 MiB resident、16 GiB owned spill。Generator candidates 最多 16,777,216，结果不得突破全局记录预算。
- 任意 Tick 倒退表达式采用有界排序归并；结果碰撞优先级来自冻结原顺序和 iteration，不来自新 ID 或执行线程顺序。
- Segment 同类型移动保留原对象、稳定 ID 与全部内容；同类型复制粘贴保持既有字段、hidden 内容及精确碰撞规则，本轮不改变其既有重复归并行为。跨类型只转换共同 Note 字段、Segment 起点/长度/content offset。Logical→Direct NoteOff velocity=0。Direct→Logical 非零 NoteOff velocity 与被折叠 exact duplicate 计入损失清单。
- 损失分析与目标提交基于同一冻结修订；确认后若来源已变更，必须拒绝过期计划。

## 验证门

1. 两种 expression profile：变量、前缀、空 identity、依赖拓扑/循环、非法 API、非 finite、资源边界。
2. 六种 owner/lane：创建、碰撞、倒退 Tick、Initial 首对象、停止条件、精确选择、Undo/Redo。
3. 取消、I/O/预算失败、旧 revision：零发布、旧数据完整、owned spill 回收。
4. Preset：当前 schema/profile 验证、撞名、损坏隔离、首对象开关、无目标/Base 泄漏。
5. Segment 双向 Move/Copy/Paste、混合选择、hidden 内容、全部损失类型、源保留、overlap、未绑定轨道、Undo/Redo。
6. 大规模候选的时间/内存/磁盘实测，报告实测数字与未测项；不以小样本替代百万级验证。

## 测试结果

五套自动测试共 2,104 项通过；三类音符的十万/百万/千万候选、三类百万事件点、Desktop 十万/百万生成，以及十万/百万 Segment 双向转换的专项基准均通过。性能计时、未测范围和确定性验证口径见 [阶段 5 验证与性能记录](Midora-Stage5-Validation-and-Performance-Report.md)。人工验收逐项清单见 [阶段 5 人工验收清单](Midora-Stage5-Manual-Acceptance-Checklist.md)，共 18 项。

## 主要改动定位

| 范围 | 主要文件 | 作用 |
|---|---|---|
| Expression | `Midora.Compiler/GeneratorExpressionProgram.cs`、`BoundedNumericExpressionCompiler.cs` | 独立生成 profile、依赖 DAG、受限委托；旧 Batch/Mapping profile 不变 |
| Generator | `Midora.Application/TimelineGenerationOptions.cs`、`ProjectTimelineGenerationCommands.cs` | 六类对象生成、规范化、碰撞、分页事务、结果选择、进度 |
| Segment 转换 | `Midora.Application/SegmentConversionService.cs`、`ProjectObjectClipboard.cs` | 完整内容冻结、一次损失确认、转换与原子 Move/Copy/Paste |
| Desktop 接入 | `MainWindow.TimelineGeneration.cs`、`MainWindow.SegmentConversion.cs`、`TimelineCreationEditCommand.cs`、`DesktopSessionController.cs` | 正式 owner/lane 路由、选择类型、进度、取消、焦点恢复 |
| Dialog / Help / Preset | `TimelineGenerationDialog*`、`TimelineGenerationHelpDialog*`、`TimelineGenerationPresetDialog*`、`TimelineGenerationPresetStore.cs` | 共享主题/编辑器、表达式帮助、有限生成上限与严格程序级预设 |
| 基础修复 | `BoundedEditStorage.cs`、`BoundedDirectMidiNoteEdits.cs`、`BoundedDirectMidiEventEdits.cs`、`MidoraOwnedTemporaryDirectoryLease.cs` | 小输入排序分配、全碰撞空结果、过期修订检查、临时目录创建与清理分离 |
| 验证 | 新增 Generation/Conversion/AdaptiveSort 用例及既有 WPF 等待测试 | 全套回归、Format 3/Full-Incremental、百万/千万性能与取消/资源回收 |

相对目录前缀分别为 `src/midora-core`、`src/midora-desktop/Midora.Desktop` 或 `src/midora-common/Midora.Common`；完整工作区变更以 Git diff 为准。未改音频消费者语义、Project Format、软件版本或发布包。
