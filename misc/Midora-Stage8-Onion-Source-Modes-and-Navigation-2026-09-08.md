# Stage 8：洋葱皮显示模式、独立设置与 All Tracks 导航

2026-09-09 状态收尾：本修订已完成并获用户验收，代码已提交、推送；目前人工大体验收通过，精细检查留到真实完整编曲。下文保留实施时的验证证据和极端 Tick 边界，不表示仍待实施或待整体验收。当前下一步见 [收尾台账](Midora-Pre-Expansion-Closeout-and-Next-Step-2026-09-09.md)。

## 本轮需求与决策

- 用户确认 Previous / Next 模式也随项目保存、重开恢复。为每个 Track / SubVoice preset 分别保存 `sourceMode = custom | previous | next` 和原有手选来源列表；快捷命令不得改写手选列表。
- Previous / Next 是普通命令，启用洋葱皮并仅显示当前正式顺序的相邻来源，不循环。重排、删除后按最新正式顺序解析；位于边界时为空，不偷偷改回 custom。SubVoice 不跨 Event Instrument。
- `Select Tracks...` / `Select SubVoices...` 与 Previous / Next 为同一菜单组；弹窗总是显示保留的手选来源。OK 切回 custom 并启用；Cancel 不变。Settings 仅编辑透明度，不改来源、显示模式或启用状态。两个弹窗都不提供 Enable 控件。
- All Tracks 时间标尺及音符内容区的普通左键单击跳转绝对播放位置，使用现有 `SetPlaybackCursor` / Seek；中键平移及 Follow Playback 不变。左侧钢琴尺、右键不跳转，按住左键移动不进行连续 scrub。视图仍完全只读。

## Requirement trace / 边界

输入是菜单命令、对话框 draft 和视图坐标。正式输出仅为 presentation 修改与运行时播放位置；不改变 Project Source Data、音符选择、Undo/Redo、Modified、canonical 或音频/导出语义。模态窗口取消不修改配置，提交后恢复所属编辑器焦点。目标在菜单打开期间失效时不误改其他目标。

独立 `project-presentation` schema 升为 2，Project Format 仍为 3。v1 DTO/schema 原样保留并显式读取为 custom；v2 每个 preset 必须携带 sourceMode。manifest 的 presentation 组件版本改为正整数分派；不支持的 presentation 版本或 manifest/payload 版本不一致仅隔离视图配置，告警后仍允许加载音乐。其他组件的版本/未知字段/损坏规则不变。

显示模式按既有 presentation revision 失效，source 列表只涉及 Track/SubVoice 元数据。点击定位仅 O(1) 坐标换算，不查询音符、建立显示索引或触发额外编译。旧项目、Save / Save Copy、Duplicate、删除后 Undo 的模式和手选列表必须保持一致。

## 验证计划

- Track 和 SubVoice：custom → previous → next → 取消选择弹窗 → custom 恢复；启用/透明度独立、无邻居边界、重排和删除后恢复。
- v1/v2 package round-trip、确定字节、manifest/payload 不一致、未知版本/模式、缺失/重复/未知字段隔离；音乐内容和 Modified 不受影响。
- 实际 WPF 主题下构造并布局两个弹窗及菜单；无复选快捷项、无重复 Enable；不使用 computer-use。
- All Tracks Raw/Compiled 停止时定位、播放/Buffering 中 Seek、超出有效内容区忽略、中键与只读行为保持；定位不重建音符快照。
- 构建与风险相称的 Persistence / Application / Desktop / Presentation 自动测试；不自动发布 dist。

## 验证中发现的既有持久化边界

空 unbound Logical Track 的 `sharedGroupId` 是既有 schema 中必填但允许 null 的成员。writer 使用忽略 null 的默认配置，导致漏写；strict reopen 随后以缺失必填字段拒绝自身文件。已确认 HEAD 也有该问题。本轮为该成员显式保留 null，并加保存/重开测试，不修改冻结的 source JSON schema 或音乐语义。

## 完成与自动验证

已实现上述菜单、两个独立 draft 弹窗、Track/SubVoice sourceMode 解析/保存和 All Tracks 导航。源码改动集中于 `MainWindow.Onion`、`DesktopSessionController.Onion`、`OnionSettingsDialog` / `OnionSourcesDialog`、`AllTracksView`、独立 `TimelineSurface.Navigation`、presentation codec / manifest 分派；未修改音频生产实现或普通 Note/Velocity/Event 栅格缓存。

构建：Release，`.NET SDK 10.0.400`，独立 `bin/Stage8/` 输出，无构建错误。五套全量测试结果：

| 测试集 | 通过 / 总数 |
| --- | ---: |
| Persistence | 104 / 104 |
| Application | 1,038 / 1,038 |
| Desktop | 384 / 384 |
| Desktop.Presentation | 417 / 417 |
| Playback | 117 / 117 |
| 合计 | 2,060 / 2,060 |

结果文件位于 `.tmp/test-results/stage8-source-navigation/*-full.trx`，0 failed、0 skipped。新增覆盖包括 schema 1/v2 与模式 round-trip、Save Copy、未知/缺失/重复/null 模式、未知版本与 manifest/payload 不一致的音乐隔离；手选来源保留、快捷独占、目标独立、Duplicate/dormant 恢复；All Tracks 两模式中的真实桌面 cursor command（替换为测试 backend）、无隐藏 Snap/选择修改/快照重建；Playing/Buffering Seek 后 Stop（测试 backend）。

实际 App WPF 主题探针通过：构造并布局两个新弹窗，验证嵌入字体、图层按钮启用/禁用外观；Ghost row 在 3/9/15/32 DIP 与 96/120/144/192 DPI 下通过。100/200/400 轨合成探针均完成渲染收敛，未显示窗口、未使用 computer-use。该探针不是独占环境性能对比；本轮未重跑 18M 真实 MIDI 或实际音频听感测试。

测试边界：新增 int64 上界测试只验证导航坐标换算，不扩展既有网格渲染的数值支持范围。在直接要求旧网格渲染器绘制接近 `long.MaxValue` 的 Tick 时，发现既有 `ProjectTimelineGrid.GetNextGridTick` checked overflow；此极端范围的网格行为不在本轮重构范围，未修改。普通范围显示层全套回归通过。

人工回归模板集中见 `Midora-Stage8-Acceptance-Checklist.md`。实施当时未提交、推送或生成 dist；后续已提交、推送并验收，见文首。极端 Tick 边界仍未修复，本次源码风险分析见 [收尾台账 §4](Midora-Pre-Expansion-Closeout-and-Next-Step-2026-09-09.md#4-极端-tick-网格溢出风险说明)。
