# 有界批量编辑：Desktop 入口与发布责任

本记录补充 `Midora-Bounded-Bulk-Editing-Requirement-Trace.md`，不定义新的音乐语义。

## 统一流程

1. Dispatcher 只冻结当前 Workspace、稳定 ID 选择根、操作参数和来源视图；不得在这里复制全量音符对象。
2. 同款前台 Task 阻止 Project 编辑，但保留取消操作。进度来自真实准备工作，最多约每 100 ms 刷新 UI。
3. `ProjectDocumentSession.PrepareEdit` 在后台执行 detached planning。所有子操作共享取消 token 和存储预算。
4. 完成后先验证来源修订，并封闭取消窗口，再在 Dispatcher 原子交换正式根。
5. 只有发布成功才更新选择、剪贴板和历史；取消保留全部原状态。Properties 窗口保留草稿且不关闭。
6. 鼠标手势的已有高性能预览路径保持不变。松开后的正式命令才进入 Task；浮动工具和普通拖动共用正式入口。

## 入口矩阵

| 操作 | 来源 | Desktop 入口/责任 |
| --- | --- | --- |
| 水平/垂直翻转、Scale、Transpose、Batch Edit | Arrangement 混合 Segment；三个钢琴卷帘；支持的事件点 | `ExecuteStagedProjectOperationAsync`；目标分类和碰撞规则仍由正式命令决定 |
| Humanize、Split Notes、Join、Quantize | Stage 4 支持的音符/事件选区 | 原 Stage 4 runner 转接同一通用 runner |
| Move、Ctrl/Alt+Ctrl duplicate-drag、左右边界 Resize | Arrangement、Logical/Direct/Template Notes、事件点 | `OnTimelineItemEditCompleted` 及各 owner helper；未成功提交不清选择 |
| Event point draw、Velocity paint | Direct/Logical/SubVoice lane | 异步命令准备；只发布完整画线结果 |
| Properties | 现有支持的同质/混合 Segment、Direct/Logical 音符/事件、Value Curve 等 | 多选读取先冻结纯数据 context 并显示可取消 Task；专用 Properties 内嵌同款 Task overlay；取消只取消准备，保留 Properties 草稿 |
| Copy/Cut | Workspace 选区、Event Instrument 浏览器、轨道头、旧导航入口 | `PrepareTransfer` 冻结 payload 和可选删除；系统剪贴板成功后正式删除与私有 payload 同次发布 |
| Paste/Duplicate | 上述各来源和结构对象 | 同一异步 runner；clipboard leases 延续到已准备命令和 Undo/Redo 历史结束 |
| Delete | 时间线对象选区、结构对象确认后的删除 | 同一异步 runner；用户取消保留选择与历史 |
| Undo/Redo | 菜单、快捷键 | 冻结目标 history revision；取消发生在 root publication 前；不得后台直接调用会通知 UI 的 Undo/Redo |
| Shared block 移动、加入/脱离、整组独立、整组换绑 | Arrangement brace 与轨道头 | 先 detached 准备完整目录，再由同一 Task 原子发布；快捷键目标仅在成功后更新 |

## 剪贴板存储与原子性

- 固定宽度音符/事件快照使用值记录及共享预算分页存储；跨 MIDI/Logical 类型转换为惰性只读投影，不新建 N 大小转换数组。
- Opaque payload 使用分页 byte blob + 固定记录，只在使用一个事件时重建该事件的 byte payload。
- Copy 独立捕获，不保留可变 Project facade；来源 session/revision 冻结。Cut 在 payload 与删除都成功准备后才触碰系统剪贴板。
- 系统剪贴板写入失败不得删除 Project 对象。正式删除失败恢复原系统剪贴板；私有 payload 仍保持原对象。
- 旧 clipboard 被替换只释放自己的 lease；已经创建/准备/进入历史的 Paste 仍持有 lease。取消、无改动、历史丢弃和 Project 关闭清理对应存储。
- 音符、通道事件、opaque 事件、Value Curve 与 Conductor 文本均采用分页捕获；整 Segment、SubVoice、Definition 复制复用不可变源，并按标量流分配新 ID，不物化整份可变音符子树。
- `PrepareEdit` 的资源账本同时跟踪规划器及 Domain 内二级索引创建的 provider；失败/取消立即清理，成功但未发布的 Staged Edit 被释放时也立即清理。正式发布后由根/历史/快照拥有 provider。
- 同步 `Execute` 与后台 `PrepareEdit` 复用同一取消感知准备 dispatch 及资源账本，测试/单对象入口不会绕开失败清理。
- 剪贴板标量页在返回 payload 前落盘，不把每次捕获的 resident 页永久挂在历史上；输入本来是压缩集合时直接复用，普通 ID 序列的去重校验采用外排而不是 N 大小 HashSet。
- 多 Segment/整 Track 粘贴按稳定 StartTick/ID 批量合并，保留原目录次序；范围碰撞通过有序扫描裁决，避免逐条插入和对原目录反复扫描的平方开销。
- Copy/Paste 返回的精确存活选择在后台预压缩；已有准备选择的发布不再在 Dispatcher 枚举新对象或分配完整 ID 数组。遗留创建入口的回退查询使用 scalar Stable ID lookup 与压缩选择。
- Properties 不再由菜单、Ctrl+P 与 Dialog 构造函数重复扫描大选区。后台只读任务接收 immutable ID root 与 workspace kind/mode/object ID，不跨线程访问可变视图控件；字段值读取可取消。确认时多选字段构建推迟到正式 Prepare，单字段不再绕回 UI 全选区验证；多字段仍按最终组合一次裁决碰撞。
- 正式准备结果即使没有音乐内容变更，仍可发布其精确结果选择（包含空集合）；这种纯选择更新不新增 Undo 项、不标记 Modified。取消与失败不发布选择。
- Arrangement 窗口属性与删除在构造选择 HashSet/目录数组之前先准入临时元数据；大目录使用 detached 根交换，删除为一次稳定压缩，不再逐 Segment 查找和移动列表。
- 最后入口审计移除了 Arrangement 拖动提交前的 Logical/MIDI 分类数组、Segment 对象数组及重复 ID 数组；新的 `ArrangementGestureEditCommand` 在 Task 已打开后可取消地扫描目录，只保留计数、最小 Tick 与惰性 ID 投影，再交给原正式命令。Scale 打开配置框之前的范围读取也改为独立可取消后台 Task，不在 Dispatcher 同步读取百万选区。未使用的同步 `ExecuteSelectionOperation` 入口删除，避免未来误接。
- 冷选区尚无几何 metrics 时，三种音符 Move/Resize，以及事件点、Value Curve、Ctrl+D、Delete 的范围/owner 读取均改为可取消后台读取；字段极值由一次 scalar 流读取，Delete 只计匹配数而不建立各候选 owner 的临时 ID 集。已缓存音符 metrics 的快速分支不变。Logical Parameter Definition Properties 的全项目引用统计不再生成 Lane 数组，正式类型/范围迁移同样进入 Task。

## 冷选择读取修复

- Scale 优先复用精确且完整的现有 Note/Segment 选择摘要，必须同时满足当前 Document publication revision、Workspace 完成 Rebuild 后的 publication 戳、同一 immutable ID root、选择修订、类型及 owner。只凭 count 或选择 revision 不足以证明摘要有效。点的 Scale 仍使用 `max(Tick)-min(Tick)`，不能包含渲染所需的虚拟 1-tick 宽度。
- 无可复用摘要时，Scale、冷手势 metrics、Delete owner 检查均通过 Application 的有界 selection reader；Direct 按批解析、密集集合按正式页顺序读取，不重复执行每 ID 的页内线性查找。
- 多选 Properties 用固定数量的 scalar accumulator，在一次正式值流中同时验证归属并计算全部 Same/Mixed 字段；不再为每个字段重新扫描选区。既有同质来源仅用于候选 owner 路由，最终仍要求全部 stable ID 匹配。
- `Read Selection` / `Read Properties` 与正式 Prepare 使用真实工作阶段及计数，未知总量显示 indeterminate；取消、修订变化或归属不全不发布草稿/选择。UI 仍以 100 ms 合并进度更新，不逐记录触发 Dispatcher。
- 新增 `SelectionReadOptimizationTests` 验证 dense 单遍读取、无多余 ID 索引、部分归属拒绝、摘要修订门和精确点跨度。测试是否通过以本轮最终运行记录为准，不将源码检查当作测试通过。
- 2026-09-06 后续回归：Desktop 全套 235 项通过；新增真实 9KX2 / 40 万音符只读探针验证 Properties 首次 1.526 s、再次 1.437 s，选择摘要读取 1.507 s。完整预算、取消、查询与结果边界见 `Midora-Bounded-Bulk-Read-and-Query-Repair.md`，不与下方上一轮阶段统计重复累计。

## 存储边界

批量中间结果与剪贴板音符/点 payload 有界，并不意味着整个 Project 的进程内存与对象数量无关。Conductor 正式列表、外层 Track/Segment/Mapping 等目录仍是正式模型对象；本次 Conductor Paste 不再另外保留 N 大小副本数组，但最终列表仍随正式事件数增长。音频、WPF 与 Compiler 缓存各自的预算仍独立管理。

剪贴板目录快照的保守准入为 Segment 每项 2,048 bytes、Logical Lane 每项 1,024 bytes、Mapping/依赖每项 512 bytes、enum/initial-state 字典每项 128 bytes；包括同时存活的排序数组/依赖表/引用开销，不是声称 CLR 对象精确尺寸如此。费用随 payload lease 释放；Paste 额外目录准备也先准入。Sequential 的完整私有镜像采用独立目录估算，超预算在分配前明确失败，不能清剪贴板、改音乐内容或新增 Undo。Conductor 不变事件 record 共享，只复制独立 List 引用目录，避免每次音符粘贴也重新构造全套 Tempo/Marker 对象。

## 验证责任

Application 测试覆盖正常 Copy/Paste、跨类型字段转换、深快照、精确碰撞、取消/异常/过期发布、Undo/Redo、spill 清理与 lease 释放。
Desktop 测试覆盖 Task cancel gate、Properties 保留草稿、selection publication、键盘及菜单入口一致性；不使用 computer-use。
性能结果必须分别记录准备耗时、发布耗时与峰值内存，不把仅创建 Task 或后台迁移视为已经完成有界内存优化。

补充回归覆盖：10 万条剪贴板记录在 1 KiB resident 预算下 spill/lease 清理；取消发生在二级 provider 已创建之后时立即回收文件；5 千音符 Paste/Undo/Redo 的旧/新根引用及旧快照隔离；4 千 Unicode Marker 同 Tick 稳定顺序；4,100 音符 Properties 同时改变 Tick+Key 时只对最终字段组合裁决碰撞。

本轮阶段证据：Application clipboard/Document/ValueCurve/mixed Segment/snap 专项 80/80 通过；Desktop 全集排除百万音符 selection geometry 专项后 219/219 通过（约 28 秒）；最终 Properties 冻结读取/取消与选择集成 9/9 通过。先前完整 Desktop 运行的另外两处失败确认为断言仍读取被替换的旧 owner，已改用稳定 ID 查询当前正式根，并额外验证旧根没有被修改、Undo/Redo 恢复对应原根。百万音符 geometry 的实际 I/O 问题由共享 Direct source 路径修复，独立 Release 回归已通过（整个测试约 31.9 秒，保留原 10 秒几何准备门限），未延长超时。新增 5,000 Segment 属性/删除及 Undo/Redo 原根测试，和分配前目录预算拒绝后零残留测试。

现有能力边界：SubVoice Template 音符/事件仍可单对象 Properties；此前不提供共同字段的多对象组合不会借本次性能修改静默新增新属性语义。所有既有可用的批量属性入口均走上述事务。Formal Conductor/目录本身依旧随内容增长；超出临时目录准入预算会明确失败，不声称整个进程恒定内存。

最后审计新增的 Arrangement gesture Move/Resize、目录扫描途中取消、no-change 结果选择与 stale publication gate 回归已纳入最终验证。最终 Application 824/824、Desktop 227/227、Presentation 336/336 通过，无失败、无跳过；前述专项是过程证据，不重复累计。已确认 Conductor Copy/Cut 不在 Task 前展开事件数组，结构剪贴板按不可变选择根在 `PrepareTransfer` 后台捕获。

新增 WPF 资源 smoke 作为现有 theme 测试的私有 helper，复用其唯一 Application 与所属 STA；先按实际顺序安装 Palette、icons、Controls，再只载入 App.xaml 本地资源项。它使用真实 `ObjectPropertiesDialog` BAML 和从当前 `MainWindow.xaml` 提取的 Task overlay markup，实际生成共享 ProgressBar 模板，不启动音频、不显示窗口；MainWindow 部分仅验证实际 overlay 及资源解析，不宣称整个 MainWindow BAML 已实例化验收。

最后的冷读取补充：metrics、Scale、Delete owner 判定以及 Properties 的值流，通过只读 Application bridge 预备有界 Stable ID 地址缓存，避免普通共享页根第一次查 ID 时建立未受预算限制的全量地址数组。缓存完整安装后属于 source，可跨读取复用；取消只回收未安装页，不发布 Project、选择或历史。普通全部命中的压缩选择直接复用原引用，不重建 Builder；确需筛选时按实际 4096-ID 页数保守预估临时 Builder 开销并先准入，不能按音符数粗估而错误拒绝大型密集选择。只读桥专项 4/4 通过（Release，包含排序途中取消、建完索引但尚未安装时取消、父 token、复用和 Project 关闭释放）；新增 Desktop 密集/稀疏筛选与冷 Properties 已通过最终统一回归。
