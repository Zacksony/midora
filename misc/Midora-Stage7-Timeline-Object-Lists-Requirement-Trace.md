# 阶段 7：Timeline 对象列表与 SubVoice 标记

2026-09-07：阶段 6 已验收并于 `e753fc4` 提交、推送。本轮仅实施已批准计划阶段 7（WP-06 与 WP-10 的 Loop/Pre-Roll slice），不实施阶段 8、不发布 dist、不使用 computer-use。

## 需求依据及边界

- 已批准大需求计划 §5.6、§5.16、阶段 7、UI-04～09；SRS §18.2、18.4、20.7.7～9，INV-014、075、077、095～100。
- 输入是当前 Logical Segment、MIDI Segment 或 SubVoice 的正式不可变修订；Note 一对象一行，合并其全部参数/Channel/Opaque/Template MIDI Event，按本地 tick 与确定同 tick 顺序显示。
- 列表默认隐藏，宽度/显隐/滚动只属于 workspace/session；隐藏时不建立目录或读取页。稳定 ID 是身份，ordinal 仅对冻结修订有效。
- 列表/图形共用选择，支持单选、Ctrl、Shift、拖动范围、Properties 与 Locate。事件 Locate 自动打开 Lanes 并选定目标。异步结果发布前核对 owner、source、selection revision；过期结果不得重选旧对象。
- 混合 Note/Event 选择的 Delete 是一个原子 Undo；普通 Copy/Cut/类型专属工具快捷键禁用。菜单显式 For Notes / For Events 使用打开时冻结子集，未处理类型保留选择，Undo 恢复完整选择。Opaque 只开放共同合法能力，不提供数值表达式或 payload 编辑。
- SubVoice overlay 仅展示正式 Pre-Roll、Loop endpoints：前缀暗区、顶部 tick 标签和范围带、贯穿三个 panel 的黄色竖线；单端 loop 只显示单端，不解释为合法完整 Loop，不改变编译或缓存。
- 失败、取消、锁定与修订失效必须零部分提交。不得增加格式、音频或音乐语义，不得修改已验收的 Conductor/Note/Event raster 算法。

## 实现记录（先行设计）

1. 共用 owner-data 列表和三类不可变源 adapter。既有 formal ordinal 并不保证 tick 有序，不能直接复用 Conductor 的有序归并；仅在显式激活后，复用 bounded edit storage 建立可落盘的紧凑 scalar 排序目录。首屏有界 top-K/范围读取独立于完整目录；常驻只保留小页，后台排序并发受限。目录有 O(N) 临时存储与首轮排序成本，隐藏/卸载取消并回收，禁止逐对象 WPF 控件、全量常驻行数组和全量字符串字典。
2. 选择命令冻结完整选择与显式类型子集，经过现有 bounded detached edit transaction；不另建一套编辑语义。使用共享 selection revision 防止旧异步结果回写。
3. Pre-Roll/Loop 使用独立非交互轻量 overlay，与各 Surface 使用相同 local tick→device pixel 变换；配置改变仅使 overlay 重绘。
4. Event Draw 右键继续使用已批准 300 ms click/double-click/drag 状态机。冷页命中保留后台查询，确定画线后不弹菜单。
5. 小规模旧编辑命令若不提供精确选择结果，在私有共享根 draft 上按正式碰撞策略求结果；大规模 bounded note 命令只在显式 owner-scoped 请求时复用已规划的结果选择，不重复准备。作用域在异常/取消时同样恢复。完整选择书签与 typed Cut 仍通过现有 detached publication 提交。
6. 同类 Tab 的 ContentPresenter 可仅更换 DataContext 而不触发 Unloaded；因此 DataContext、可见性及卸载都显式管理旧 source 的取消和新 source 的激活。命令焦点按本次真实列表/菜单来源恢复，列表快捷键在编辑/Undo 后后台重新解析失效的类型缓存。

## 验证门

- 三个 owner 的确定排序、hidden content、same-tick duplicate、分页、首屏、远处随机页、百万范围与稀疏选择；隐藏零读取、取消和源切换。
- 混合删除/typed 操作、未处理类型选择保留、Undo/Redo、Opaque capability、任务取消和失败原子性。
- 真实 WPF BAML/layout/事件处理、Properties/Locate/焦点、右键 click/drag、Loop 单端/双端、不同 DPI/zoom 的贯穿对齐与缓存不失效。
- Desktop/Presentation/Application 定向与全套回归，列出真实大样本性能观测和未验证范围；自动测试不替代人工视觉验收。

## 验收后小调整（2026-09-07）

产品所有者确认阶段 7 功能整体验收通过，本轮仅调整三项 UI：

- Pre-Roll 顶部标签使用紫色；即使低缩放合并相邻标签，也只给 Pre-Roll 文本着紫色，Loop 标签、范围和竖线仍为黄色。
- Configuration 的 Pre-Roll 空白输入在提交时静默归为 `0`，使用原正式命令，仍可 Undo/Redo；非空非法数值的验证不变。原值已为 `0` 时恢复框内 `0`，不产生 History edit。
- SubVoice 主 piano ruler 不再开始 Time Range 拖选，其三个 panel 的右键 Time Range 命令均禁用；Segment/Arrangement 的 Time Range、SubVoice 对象框选与编辑不变。

以上均无新持久化字段、音频语义或并发模型；本轮不进入阶段 8、不提交、不发布。

## 本轮交付

阶段 7 主体交付时四组全套 2,199 项通过，现已获功能整体验收；详细计数、合成百万对象测量及本轮小调整的复测记录见 [验证报告](Midora-Stage7-Timeline-Object-Lists-Validation-Report.md)。本轮只需复核 [清单开头的三项小调整](Midora-Stage7-Timeline-Object-Lists-Acceptance-Checklist.md)，不自动进入阶段 8。
