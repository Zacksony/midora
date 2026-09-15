# 阶段 8 验收修订：工具栏、播放跟随与混合投影

日期：2026-09-08。2026-09-09 状态收尾：本修订及后续菜单/模式/导航均已完成并获用户验收、提交及推送；本报告保留当时验证证据。下述相邻来源菜单最初设计已经由[后续修订](Midora-Stage8-Onion-Source-Modes-and-Navigation-2026-09-08.md)取代为独占 Previous/Next、独立手选来源及模式持久化，不是当前待实现需求。未使用 computer-use；本轮文档收尾不发布。

## 需求追踪与决定

- 三种钢琴卷帘的 Onion 入口统一到横向缩放按钮右侧，使用同组 26 × 24 Button、13 × 13 Fluent Layer Diagonal Regular 图标。Arrangement 同位置图标直接打开 All Tracks。
- Onion 图标仍是普通 Button；启用时按共享 Toggle palette 高亮。按下弹出主题菜单：Enable/Disable、Show Previous/Next Track（SubVoice 中为 SubVoice）、Settings...。
- 相邻来源按当前正式顺序查找，不跨头尾、不包含自身或 Conductor；菜单项独立增删对应来源，保留其他手选来源，加入来源同时启用。配置仍属于现有 presentation，不改变音乐 Modified/Undo。
- All Tracks 控件高度与现有钢琴卷帘一致；显示绝对 Tick 播放指针，并复用既有 Follow Playback 的默认值、阈值、拖动暂停/松开恢复、底部滚轮阻止规则。只读内容不能被编辑。
- **产品所有者已明确选择混合显示**：Compiled 只展开逻辑轨道；MIDI 轨道直接叠加源音符。它不再承诺纯 MIDI 内容按最终跨 Track FIFO 流重新配对；UI/Help 必须明确两类来源。逻辑部分仍只消费完整成功 canonical，过期标 Stale；MIDI 部分显示当前 Project 源音符。
- 该变化仅影响只读视图，禁止改变 Compiler、canonical、Note 语义、音频或导出。Format 3 的 Compiled mode 字段继续使用，语义调整以本次明确需求为依据。

## 实现边界

当前编译器把逻辑展开写在 canonical.Events，Pure MIDI 页来自 PureMidiPagedCanonicalSource。新增 logical-only 索引 scope 跳过 Pure MIDI 页，并排除内存事件中的 Root 来源；不得先遍历全部 MIDI 再过滤。MIDI 源快照在 Raw/Compiled 间复用，重绘身份仍依据实际各来源内容。

Compiled 原来并未调用第二次编译；原瓶颈是遍历已有 canonical 流并建立整曲显示索引。本次删去的是 MIDI 显示索引的重建成本，不是绕过正式播放/导出的编译。

## 回归发现与修正

- 实际 App 主题探针捕获 Onion 高亮未生效：模板 DataTrigger 的来源不正确。改为针对 Button.Tag 的明确 Boolean 属性 Trigger，实际模板验证 false → true → false 的背景/前景变化和 26 × 24 尺寸，避免只检查 XAML 值。
- 第一轮 Desktop 全套（381 通过 / 1 失败）发现后台编译完成与 Tab 创建交错时，Onion 刷新枚举 ObservableCollection 导致 `Collection was modified`。异常在编译任务内暂存，关闭会话等待任务时暴露。现由 Tab 集合变更通知发布不可变引用数组，刷新只读该快照，并跳过已关闭 Workspace；不增加逐帧列表分配，不锁住 UI。补充刷新通知中关闭另一 Tab 的确定性测试，验证重入和后续刷新。
- Settings 菜单项关闭后延迟打开弹窗，避免两个焦点恢复请求竞争；菜单结束释放 Button.ContextMenu，保留窗口原有模态焦点恢复路径。

## 本机 18M 实测（2026-09-08）

Release / SDK 10.0.400 / win-x64；独立 STA 进程加载实际 App 资源，1280 × 800 离屏 WPF，未展示窗口、未启动音频。样本：`D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`。不代表其他硬件的延迟保证。

| 项目 | 结果 |
| --- | ---: |
| 导入源音符数 | 17,999,999（40 条来源轨道；另有保留的原始 MIDI NoteOn/Off 事件） |
| 导入用时 | 20.54 s |
| 当前完整 canonical 获取 | 0.03 s |
| 混合视图的 logical-only 索引 | 0 个音符，0.6 ms，0 spill |
| 已渲染 Raw → Compiled，含调度与首帧 | 116.9 ms |
| MIDI 快照复用 | 40 / 40，同一可视范围缺失瓦片 0 |
| 进程 Peak Working Set（含此前 100/200/400 轨与 DPI 测试） | 625.5 MiB |

上述 116.9 ms 是复用已渲染 MIDI 瓦片的切换测试，不是冷缓存整曲渲染。Raw 冷可视范围测得首帧 30.4～48.0 ms、瓦片全部就绪 0.31～3.23 s，本轮不宣称消除了原有后台渲染等待。旧版 149.28 s 是完整 18M canonical FIFO 显示索引的历史数据；现仅逻辑展开建立索引，因此纯 MIDI 不再承担该成本，逻辑输出很大时仍需有界后台准备。

主题探针另通过：13 px 图层/缩放图标、26 × 24 按钮、All Tracks 24 px Button/ComboBox、启用高亮、播放指针绑定不重建源快照、100/200/400 轨、3/9/15/32 DIP Key 与 96/120/144/192 DPI 离屏对齐。实际鼠标外观/菜单位置仍交由用户验收。

## 最终自动验证

所有结果位于 `.tmp/test-results/stage8-refinement/`，没有写入 dist。

| 测试项目 | 最终结果 | 结果文件 |
| --- | ---: | --- |
| Midora.Desktop.Tests 全套 | 383 / 383 | desktop-full-final.trx |
| Midora.Desktop.Presentation.Tests 全套 | 413 / 413 | presentation-full.trx |
| Midora.Application.Tests 全套 | 1,038 / 1,038 | application-full.trx |
| Onion + 选择来源专项（包含重入关闭） | 10 / 10 | onion-roster-targeted.trx |
| 完整/仅逻辑 compiled 索引专项 | 12 / 12 | logical-index-targeted.trx |

前三项合计 1,834 个用例全部通过；专项是其中的重复执行，不重复累计。App/WPF 探针最终重建 0 Warning / 0 Error，并再次通过主题、指针、400 轨等检查。`git diff --check` 通过。

本轮未改音频、Compiler、MIDI 导出或持久化协议，因此未重跑音频设备及全部后端回归；此前阶段 8 的历史回归与资源限制保留在原报告。本轮不能据此声称做过真实鼠标操作、实际多屏幕外观或音频听感验收。

## 主要改动位置与人工验收

- `MainWindow.xaml` / `MainWindow.Onion.cs`：三种 Onion 入口和 Arrangement All Tracks 入口、主题菜单、焦点。
- Style Gallery 共享 `Controls.xaml` / `FluentSystemIcons.xaml` / notice：公共小图标 Button、独立启用态、同基线 Layer Diagonal Regular。
- `DesktopSessionController.Onion.cs` / `IPlaybackTimelineWorkspace.cs` / `AllTracksView`：邻居配置、只读播放指针与跟随、稳定 Tab 引用快照。
- `AllTracksWorkspaceViewModel.cs` / `CompiledOnionNoteIndex.cs`：源 MIDI 快照复用、仅逻辑 canonical 索引、混合当前/过期状态。
- SRS 18.11、INV-116、文控、原扩展计划、Help、使用说明和验收表已同步。

请检查 [验收清单](Midora-Stage8-Acceptance-Checklist.md) 的前七项：三种入口的图标和位置、启用高亮/前后来源/Settings、All Tracks 工具栏高度、播放跟随和拖动暂停、混合显示。其余主功能已通过用户上一轮整体验收，清单继续保留用于必要回归。

## 验证计划

主题资源加载/实际模板尺寸与高亮；三种入口及位置；相邻边界、多来源、重排、SubVoice 独立、同 Track 多 Segment 同步；菜单关闭/Settings 的焦点。

All Tracks 初始/播放/停止/跳转/重开指针、跟随关闭/启用/拖动暂停/恢复、轻量 overlay 与源快照不随 Tick 重建。

Mixed 的 MIDI 源快照复用、无 canonical 时 MIDI 仍可见、逻辑展开/过期/取消/来源删除；logical-only 索引不读 Pure MIDI 页、内存/分页两类源，既有完整 canonical 索引测试保留以防影响其他语义。最后进行完整 Desktop/Presentation 与相关 Application 自动回归。
