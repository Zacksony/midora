# 阶段 7 人工验收清单

状态：阶段 7 功能整体验收通过。本轮仅复核下方三项小调整，不需重复整张清单。本阶段不包含洋葱皮或所有轨道叠加视图。

本轮复核：

1. SubVoice 顶部 `Pre-Roll XXX` 为紫色，Loop Start/End 仍为黄色；缩小至相邻标签合并时颜色仍各自正确。
2. Configuration 的 PRE-ROLL TICKS 留空或只填空格后失焦，静默显示 `0`；非零值归零仍能 Undo/Redo，已为零时清空不增加撤销项。
3. SubVoice 主钢琴卷帘和底部各 Lane 的 ruler 均不再拖出 Time Range，右键对应命令禁用；对象框选仍正常，Logical/MIDI Segment 与 Arrangement 的主 ruler Time Range 保持可用。

建议准备一个 Logical Segment、一个含 Note/CC/SysEx 的 MIDI Segment，以及有两条 SubVoice、Loop/Pre-Roll 的 Event Instrument。列表相关第 1～7 项在三种钢琴卷帘分别检查。

| Check | 操作 | 预期 |
|---|---|---|
| 1 | 初次打开编辑器，在 `Lanes` 左侧点 `List`，拖列表右侧分隔条 | 默认隐藏；打开后有按 Tick 排序的音符和非音符列表；宽度可调整；一个 Note 一行，无内部 ID |
| 2 | 单击、Ctrl 点击、Shift 跨页选择；按住拖出可见页 | 列表和图形选择一致；可跨页范围选择；Ctrl toggle；未完成/已取消查询不恢复旧选择 |
| 3 | Ctrl+A；关闭 List；切换 Tab 再返回；编辑后重新打开 List | 列表全选全部 owner 对象；关闭后图形选择保持；显隐/宽度/滚动在会话内保持；显示当前数据 |
| 4 | 列表双击一个音符/事件，修改 Properties，再 Cancel 或 OK | 只针对该对象；Cancel 不改变 Project，OK 一次 Undo；payload 摘要只读 |
| 5 | 列表右键 `Locate`，分别定位音符、非当前 Lane 的事件与 Imported Meta/SysEx | 时间位置正确；音符 Key 可见；事件自动打开 Lanes 并选择对应 Lane；快捷键仍可用 |
| 6 | 选择 Note+Event，检查 Ctrl+C/X/E/Q/T 与 Delete | 普通类型专属快捷键不偷偷只处理一类；Delete 一次删除完整选区；Undo 完整恢复 |
| 7 | 混合选择右键，分别用 `For Notes` 与 `For Events` 的 Flip/Scale/Transpose/Batch/Quantize/Properties/Copy/Cut/Delete（按可用项） | 只处理明确子集；另一类保持选择；Undo/Redo 恢复完整前后选区；跨 Lane 或含 Opaque 时不开放错误的数值操作 |
| 8 | 包含复合 Bank、Pitch Bend Range、不同 CC 的多选 | 完整属性仍可查看；不把复合事件误当一个数值；时间量化/删除能力与菜单一致 |
| 9 | 大型 MIDI 中打开列表、远跳滚动、选择数十万对象；查询时切 Tab/关闭列表 | 主视图可操作；无百万 WPF 行；取消后的结果不回写；不无限增长内存或遗留在途任务 |
| 10 | 调整 Pre-Roll、Loop Start/End（包含仅填一端） | Pre-Roll 左侧暗区；顶部 Tick 文本准确；单端只画自身；双端显示范围与两条黄线 |
| 11 | SubVoice 切换 Velocity/Event Lane，平移/缩放并改变 DPI | 黄色竖线贯穿对齐；只有顶层 ruler 有 Tick 标签；标记不阻止音符/事件编辑，不导致全内容重渲染 |
| 12 | Event Draw 中右键点、Shift+右拖、右拖线、右键双击；重点在很低/很高值及水平缩小后检查 | 单击正确点菜单；确定拖动后不弹；Shift 保持水平线；双击仍按 300 ms 切模式；不在同 x 任意 y 错误命中 |
| 13 | 播放中选择对象、打开菜单/Properties | 可选择和只读查看；Project 修改命令禁用；Locate 不改变音乐内容 |
| 14 | 完成本轮后再检查 Arrangement 概览、三种 piano pan/zoom、Conductor Tempo 编辑 | 已验收的渲染、选择、编辑行为不退化 |

列表布局仅是本次会话状态，不会因操作列表本身标记 Project Modified，也不保存到 `.midora`。大型列表的第一次时间排序需要后台建立临时目录；完成前会显示读取状态，不等于主界面无响应。
