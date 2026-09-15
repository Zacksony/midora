# 有限内存批量编辑、进度与取消

状态：2026-09-06 实施及本轮自动化验证完成，等待产品所有者交互验收。未提交、未发布。

## 输入、输出与覆盖范围

输入为冻结的 Project 修订、正式 Selection、剪贴板和工具参数。输出为与既有命令语义一致的 Project source、精确变更范围及结果 Selection；每次用户操作形成一个 Undo。覆盖 Arrangement 同类/混合 Segment，以及 Logical、Direct MIDI、SubVoice 三类 Note 和对应数值 Event/Parameter Point 的水平/垂直翻转、Scale、Transpose、Batch Edit、Humanize、Split、Join、Quantize、Properties、Move、左右 Resize、Copy/Cut/Paste/Duplicate/Delete，并检查其键盘、菜单、浮动工具与直接拖动入口。

## 约束与失败条件

- 延续 SRS §20.4.11 的每命令 64 MiB working、64 MiB resident staging、16 GiB owned spill、100,000,000 candidate/result 预算；每 256 个记录或更短可中断边界检查取消。这些预算不等于整个程序 Working Set，也不等于 GC 累计分配。
- 稳定 ID、正式顺序、导入既有重复、Note later-loses、Event later-wins、越界处理、精确 Segment 边界、属性验证与既有编译语义保持。
- 开始命令后显示同款模态操作进度；准备期间可取消，进度节流。最终发布临界区先封闭取消再提交，不允许用户看到部分状态。拖动中原有预览保持；放开提交手势时进入任务。
- 取消、表达式/验证错误、预算超限、磁盘错误、过期修订均零 Project/Selection/Clipboard/Undo 发布。失败保持属性窗口草稿；成功恢复原 Timeline 焦点。
- Undo/Redo 使用冻结结果，不重新求值随机、表达式或网格；历史数据与剪贴板必须明确持有共享页及 owned spill 生命周期。

## ADR-BULK-001：共享数据页与有界准备

不可变 page/root 作为正式集合可接管的数据版本。Logical Note、Template Event、Curve Point 和 Direct MIDI 集合的克隆共享页；可变对象仅为按需解析的 owner-local facade，修改采用 COW，不能影响旧版本。正式准备、碰撞归并、变更通知与结果选择只流式传递固定大小 value records，不依靠全量对象图和多份 HashSet/Dictionary。

外部排序按批准正式顺序归并；排序 chunk、归并缓冲和页读取受同一个命令预算约束。固定切割步进可计算，不建立与刀数等大的数组。大结果页可以保留 owned spill backing，由 root/history 持有；取消的 detached 页立即释放，已发布页不得随 staging Dispose 删除。

## ADR-BULK-002：发布与消费者

所有可取消工作发生于 detached 准备。组合属性命令必须在 detached 版本上顺序求值，不得临时 Apply/Undo 正式 Project。提交通过同一修订门，包含 no-op 子项、allocator 与跨 owner 依赖；发布期间不可取消。UI、编译快照与缓存消费同一变更信息，不能在通知时再次全量扫描 owner。

不改变 Project Format 3 wire、Mapping Function ABI 或音频语义。准备缓存、spill、进度与资源计数只属于运行时；所有文件位于 ProgramRoot 的批准 owned 临时目录，不进入项目或系统临时目录。

### 发布后的所有权和历史内存

新建的正式值页、ID 索引和选择页在可取消准备结束前落盘；不能把每次操作的 64 MiB resident staging 永久留在 Undo 历史中。发布后的标量读取共用进程级 64 MiB page LRU，opaque payload 另有 8 MiB / 8,192 entries 上限。Undo 保留不可变根和磁盘 backing，而不是每次保留一套完整可变对象图；这些缓存上限不包含 Project 本身、选择集合、编译器及 WPF 各自已有的内存。

私有 Project 镜像只能用于准备。建立最终 root slots 后立即解除镜像对完整目录和 Conductor 列表的引用（替换目录引用而非清空即将发布的列表）。首次发布时显式接管运行资源；lazy facade 所持的镜像仅成为正式 Project 的 allocator/resource ownership bridge，不得继续独立分配稳定 ID。Undo/Redo 复用对应目录根，不重新计算音乐数据。

旧 Logical/SubVoice/Curve snapshot 的稳定 ID 可能与 formal order 高度交错。批量准备允许用共享外排 builder 建独立 ID→ordinal 索引，不能逐 ID 扫描全 owner。完整建好且原子装入旧 source 的 ID-only 缓存不属于尚未发布的音乐编辑；操作随后取消时允许保留它供重试复用，Project 关闭或 source 最后引用释放时回收。未完成索引及新音乐/选择 staging 仍须即时清理。

### 读取与渲染边界

spill-backed 音符、Velocity、Curve 与 Template Event projection 在 UI 线程只允许 cached-only 查询。缺页返回 Pending，并回滚本次部分结果，由后台真实预取后重绘；不得伪报已经预取，也不得在 UI 上计算需要磁盘读页的范围指纹。Direct 大选区使用顺序 ordinal 页扫描及已有选择集合判定成员，避免随机 ID 顺序造成两套磁盘索引反复换页。

### 额外纠正的复杂度路径

- Direct Split 按有界 target-key batch 查询原占位者并外排归并；切割端点顺序从实际刀点覆盖范围一次读取，不能每一刀重新扫描同一 source page。所有被碰撞删除的切割片仍保留原契约要求的端点 order reservation。
- Conductor 的 Tempo/Time Signature/Key Signature/Marker 是不可变 record；镜像共享 record，只复制独立目录，mutable End Marker 仍独立复制。
- 聚合多个小 Segment 也按累计音符/点数量选择有界路径，不能仅检查单 owner 是否超过阈值。
- Properties 的混合属性读取和命令构造也属于后台可取消工作，不能只在最终提交时才出现 Task。
- 整 Track 复制、单个/多个 Segment 复制、Segment Split/Join、向左扩展导致的内容平移均使用同一有界内容路径；空 Segment/Lane 目录也计入元数据准入。结构发布交换预建目录引用，不能在 Apply 中 Clear/AddRange 扩容。
- Arrangement 手势的目录筛选和 Scale 弹窗的选区跨度读取在后台执行；不能在 Task 出现前先创建多个 N 大小数组。结构长流按实际已处理/总记录报告进度。
- detached 发布同时处理正式目录和损坏对象占位目录；Undo 原选择按操作开始时的 Project 过滤，排除中间步骤创建的 ID。无音乐变化但有正式选择结果时，只更新选择，不新增历史或 Modified。
- Logical Parameter Definition 的类型/范围修改和 Lane 换绑也可能批量转换整个项目的参数点，不能因为入口是单个属性就绕开预算。聚合点数达到阈值后逐页生成稀疏修改，保留未改页、原目录顺序和已有 ID；新 Enum ID 只在发布时接管。Enum 元数据在冻结及验证分配前先做保守准入。

### 保存重开验证的身份范围

Full/Incremental 对同一修订仍严格比较 fingerprint 和完整 canonical 事件。保存重开则比较全部正式事件、来源与计数；Pure MIDI 的 source-aware cache fingerprint 按 SRS §12.25.3 包含 source pack 和 delta，保存将其合并成新 pack，因此不额外要求保存前后的该运行时缓存标记相等。不得把这个缓存标记差异误判为音乐数据丢失，也不能只比较驻内存事件而漏掉分页 canonical 事件。

## 验证与完成门

入口矩阵逐项检查：三类 Note、三类 Point、Arrangement 混合 Segment、Clipboard 和 Properties 的成功/无变化/取消/失败/Undo/Redo。对跨页、冷页、导入重复、碰撞、越界、revision race、读者旧新快照隔离、结果选择和保存重开做回归。资源测试分别报告累计分配、峰值存活内存、staging working/resident、spill bytes、历史保留量和取消清理；60k 回归不能代替 1M/10M 资源门。只有全部正式入口接通并通过相应验证后方可标记完成。

### 最终自动化结果

本机 Release 构建与下列测试通过，合计 2,134 项，失败 0、跳过 0：

| 测试项目 | 通过数 |
| --- | ---: |
| Application | 824 |
| Desktop | 227 |
| Desktop.Presentation | 336 |
| Compiler | 387 |
| Persistence | 89 |
| MIDI Export | 41 |
| Playback | 114 |
| Audio Render | 37 |
| Common | 79 |

最终 TRX 位于 `.tmp/TestResults/BoundedBulk/Final/`：`bounded-application-final.trx`、`bounded-desktop-final.trx`、`bounded-presentation-final.trx` 与 `bounded-final-complete-core_*.trx`。Application 最后复跑已包含只读索引桥、参数迁移及准备期间源定义/allocator 变化的拒绝发布；Desktop 最后复跑包含百万音符 WPF 选择几何、冷 metrics、Properties、取消、无变化选择发布及共享 Task 资源解析。早期试跑失败记录不作为通过证据。

新增的 Task 资源 smoke 复用既有主题测试的唯一 WPF Application/STA，验证真实 Properties BAML 与 MainWindow Task overlay 的实际 XAML 片段；不是完整主窗口人工验收。没有使用 computer-use，没有运行 `dist` 发布。音频后端行为未修改，此表中的播放/音频渲染回归不能替代完整原生设备和音色库发布门。

## 性能与内存证据

以下是本机 Release 自动化样本结果，不是新增的 9KX2 实机 GUI 操作计时。不同运行的结果不能用来声称严格 A/B 提升比例；Apply/Undo 的根发布时间不包含后续 UI 重绘和编译。

| 样本/操作 | 完整准备 | 发布后 staging resident | 说明 |
| --- | ---: | ---: | --- |
| 模拟千万 Logical owner，移动 60,000 Notes | 0.721 s | 0 | 含最终落盘 |
| 同 owner，修改 60,000 Notes 长度 | 0.308 s | 0 | 含最终落盘 |
| 同 owner，复制 60,000 Notes | 0.368 s | 0 | 含最终落盘 |
| 同 owner，修改 1,000,000 Notes Velocity | 2.051 s | 0 | staging 峰 64 MiB，working 峰 2.7 MiB，spill 峰 96.3 MiB |
| 真正冷 ContentPack，修改 1,000,000 Direct Notes 长度 | 22.367 s | 0 | staging 峰 64 MiB，working 峰 7.97 MiB，spill 峰 912.61 MiB |

最后一项说明：有界内存不是无代价的加速。百万级 Direct 准备仍有明显排序/索引/磁盘时间；不能只展示微秒级 Undo 来掩盖这段耗时。该项准备累计托管分配为 1,908.52 MiB，观测 managed heap 约 79.93→322.67 MiB；前者不是峰值存活内存，后者也不是整个 WPF 程序峰值 Working Set。

24 次连续历史根测试确认：所有新音乐页、ID 索引和结果选择页在发布前 spill，历史不累计每操作 64 MiB 的常驻暂存页。取消发生在已产生多个 provider、已部分 spill 的阶段，仍清零未发布存储。详见 `Midora-Bounded-Direct-MIDI-Verification.md` 与 `Midora-Bounded-Bulk-Desktop-Entry-Trace.md`。

## 用户验收清单

以下测试在项目副本上进行；每组分别尝试小选区、约 60,000 对象和可接受的更大选区。只测试原本支持该操作的对象类型，不新增原规格未提供的操作。

1. 三种钢琴卷帘：移动、Ctrl/Alt+Ctrl 复制拖动、左右边界调整；检查结果选择、原有预览、Undo/Redo 后内容与选择。
2. 音符工具：左右/上下翻转、Scale、Transpose、Batch Edit、Humanize、Split、Join、Quantize；检查碰撞、越界和随机结果在 Redo 时不重新计算。
3. 三类事件/参数视图：画线、移动、复制、属性、Scale、Batch Edit、Quantize、删除；检查同 tick 后来者覆盖，未编辑的导入重复仍保留。
4. Arrangement：同类和混合 Segment 批量移动/左右长度修改、内容变换、复制；特别测试左扩越过原内容起点、隐藏内容，以及整轨复制和 Segment Split/Join。
5. Copy/Cut/Paste：跨轨、跨音符类型、整 Segment/SubVoice/Instrument；中途取消后原内容、选择和剪贴板不变，成功后 Undo/Redo 正确。精确碰撞导致新音符全被丢弃时，结果选择应为空。
6. Properties：大选区打开和提交均可取消；提交取消保留草稿，Mixed 多字段组合只按最终字段值裁决碰撞，完成后快捷键继续可用。
7. 进度与取消：准备中尝试取消、取消后重试、完成后连续 Undo/Redo；确认不出现半成品、额外 Undo、误标记 Modified 或永久锁定。最终原子发布阶段取消按钮会短暂禁用。
8. 大数据编辑后拖动/缩放钢琴卷帘和 Arrangement、切换 Lanes、保存副本并重开；检查冷页能后台加载、最终图形和编译内容一致。

整个进程的内存仍包含正式项目、选择、编译器、WPF、声音库和 GC 高水位。上述每操作预算不构成整个进程固定内存承诺；正式外层目录超出保守临时预算时会在修改前明确拒绝，磁盘不足同样失败而不留下半次编辑。
