# Midora 阶段 4：Humanize、Split、Join 与 Quantize Requirement Trace

状态：功能与 UI 已于 2026-09-06 经产品所有者整体验收通过并授权提交；百万对象资源门仍未关闭，阶段 4 尚未完整完成

日期：2026-09-04

上位规范：《Midora SRS》§20.3.15、§20.4.11～20.4.18、§22 的 INV-095～107；`Midora-Major-Editing-and-Visualization-Expansion-Implementation-Plan-2026-08-31.md` 的 WP-07；`Midora-Paged-Selection-and-Edit-Transaction-Architecture-Decisions.md`；`Midora-Tool-Expression-Profiles-Architecture-Decisions.md`。

## 1. 输入与正式输出

- Humanize 输入：当前冻结的 Logical Note、Direct MIDI Note 或 SubVoice Template Note Selection；Tick/Gate/Velocity 各自的 Disabled/Add/Multiply/Override 配置；Auto 或显式 seed。
- Split 输入：当前冻结的同类 Note Selection；每个 owner 独立的全局选区范围；Fixed Piece Length、Maximum Piece Count 或 `midora.tool.note-split/v1` Expression；Maximum Cuts。
- Join 输入：当前冻结的同类 Note Selection和非负 Maximum Gap Ticks。
- Quantize 输入：当前冻结的 Note 或正式数值 Event/Parameter Point Selection；Snap 分数或 Custom Ticks；Note 的 Start only / Start and End 模式。
- 正式输出：既有 Note/Event Project source 的新 immutable page/root、压缩 result-selection mapping、精确 source/range/page change set，以及一个原子 Project Undo。工具本身不产生新的持久模型。

## 2. Humanize 语义

- 只修改 Tick、Gate、Velocity，不修改 Key。
- Add/Override 对整数范围做闭区间均匀整数抽样；Multiply 抽取连续 finite `double` factor，要求 `factor >= 0`，最终 `AwayFromZero`。
- Velocity Clamp 到 `1..127`；Gate 最小 1 tick，并以归一化 start 做 checked end 运算。Tick raw result 越过 owner 正式硬边界时删除 Note；不得扩展 Segment 暴露范围、内容 owner 或 SubVoice Template Length。
- 抽样按 `(frozen seed, owner stable identity, frozen formal ordinal, field kind)` 独立派生。另一字段的启用、禁用或 UI 顺序不能改变该字段结果；不得依赖 `System.Random` 的版本序列。
- 最终 exact start+key 碰撞按冻结 formal order later-loses。命令提交时冻结 seed 与结果，Undo/Redo 不重新抽样。

## 3. Note Split 语义

- 每个 owner 分别取 `selectionLeft=min(start)`、`selectionRight=max(end)`，建立一组严格递增的全局刀线。刀只切穿过它的已选 Note；空隙刀仍计入 Maximum Cuts。
- Fixed Piece Length 每 N ticks 一刀；Maximum Piece Count 使用 `floor(j*span/blockCount)` 形成最多 N 个平衡区间并去除重复/端点；Expression 只使用 `i,tr`，返回相对上一刀的正长度。
- Expression 的 `i` 从 0 开始；第一刀前 `tr=0`。finite 结果 `AwayFromZero` 后至少为 1 tick；到达或越过右端即停止，不产生右端零长度片段。
- Maximum Cuts 只用于 Expression，默认 65,535、可配置到 16,777,216；Fixed Piece Length / Maximum Piece Count 必须完整规划且不得读取该字段。三种模式共用 16,777,216 刀硬上限；Fixed / Maximum Pieces 的理论刀数须在分配前检查，超限时明确失败并零发布。结果记录最多 100,000,000，working/resident staging 各最多 64 MiB，owned spill 最多 16 GiB。
- 实现使用单调刀线和 active-interval sweep，不能采用每条 Note 遍历全部刀线的 `O(N*K)` 路径。
- 片段继承 Key、NoteOn velocity 和共同字段；Direct MIDI 的每片都继承源 NoteOff velocity。第一片保留源 Stable ID，后续片使用新 ID；formal order 由源 order 和片序确定。

### ADR-STAGE4-001：Direct MIDI Split 内部端点顺序

Direct MIDI Note Split 在命令 Prepare 阶段冻结每个片段的两个既有 `long` explicit-order 字段，不新增身份字段：第一片的 NoteOn 保留源 `NoteOnOrder`，最后一片的 NoteOff 保留源 `NoteOffOrder`；每个内部 cut 为左片 NoteOff、右片 NoteOn 分配两个连续顺序值。

Prepare 先冻结全部 cut tick，并从覆盖这些 cut 的最小 tick 范围查询 Direct NoteOn/NoteOff endpoint、Direct Channel Event 与 opaque event；只有恰好落在 cut tick 的记录才进入对应 tick 的 order high-water。生成边界按 `(源 frozen formal ordinal, 右片 fragment ordinal)` 排序，从 high-water 后依次分配 `Off, On`。因此同一 cut 必定先释放左片、再启动右片；多个源在同 tick 被切时不依赖后续片段的新 Stable ID；既有同 tick 数据的相对顺序完全不改，所有本次生成端点位于其后。

order 加法全部使用 checked 算术。high-water 后无法容纳完整 `Off, On` 对时，Prepare 失败，整批零发布、零 Undo；不得回绕、压缩既有 order 或退化到 Stable ID tie-break。该决定只赋值既有 `NoteOnOrder` / `NoteOffOrder`，不改变 Direct MIDI Note 模型、content-pack record、protobuf 或 Project Format。

## 4. Join 语义

- 只处理已选 Note；按 `owner + key`、start、formal order 分组扫描，不跨 owner 或 key。
- Maximum Gap Ticks 是非负 int64，默认 0；`next.start-current.end <= gap` 时进入同一 run。所有 end、差值和结果 length 使用 checked 算术。
- 结果为 `[first.start, max(end))`；NoteOn velocity 取 run 第一条，Direct MIDI NoteOff velocity 取 run 最后一条；第一条保留 Stable ID。
- 未选 Note 不得被删除或改写；结果跨过未选同 key Note 时仍保留该 Note，并继续服从既有 overlap/编译诊断规则。

## 5. Note / Event Quantize 语义

- 复用正式 Snap/Grid/Time Signature 服务，提供 Snap 分数和 Custom Ticks；固定 100%，不提供 Strength 或 Bar。正中 tie 选择较早格点。
- Segment 对象先从 content-local tick 投影到 Project absolute tick，按 Conductor Time Signature Map 取格，再投影回内容坐标。SubVoice 以 template tick 0 为原点，仅使用 TPQN 分数或 Custom Ticks。
- Note 默认 Start only；也提供 Start and End。后者分别吸附两端，若 `end <= start`，将 end 饱和为 `start+1`。exact start+key 按冻结 formal order later-loses。
- Event 只覆盖 Direct MIDI Channel Event、Logical Parameter Point 和 SubVoice MIDI Event，只改 Tick。Opaque SysEx/Meta 与 Conductor 事件不在本阶段范围。
- Event exact tick+正式 lane target 按冻结 formal order later-wins；被命中的既有重复进入本次 reducer，未命中的导入重复保持不变。所有 Tick 算术 checked。

## 6. 表达式、Preset、事务与选择结果

- 表达式 profile 固定为 `midora.tool.batch-note/v1`、`midora.tool.batch-event/v1`、`midora.tool.note-split/v1`；Stage 4 只直接新增 Split profile 的消费。精确变量和安全边界见独立 Expression Profiles ADR。
- 工具表达式与 Project Mapping Function ABI v3 相互独立；正式路径只建立受限 `System.Linq.Expressions` 委托，不编译或加载 Project 源码程序集。
- Preset 只位于 `<ProgramRoot>\Data\Presets` 的工具分类，保存 schema/profile/tool/数值契约版本；每次加载严格重验证，不进入 Project、Undo/Redo 或 canonical。
- Humanize、Split、Join、Quantize 在 detached paged staging 中准备。取消、验证错误、资源超限、I/O 错误或 revision race 必须零发布、零 Undo，并清理本次 owned spill；成功时一次 root swap、一次 Undo。
- Humanize/Quantize 选择所有幸存的处理结果，Split 选择所有幸存片段，Join 选择所有合并结果；被越界或碰撞 reducer 删除者不进入结果 Selection。Undo 恢复原对象和原 Selection，Redo 恢复冻结结果和结果 Selection。

## 7. UI、诊断与失败条件

- Note 同质 Selection 提供 `Humanize...`、`Split...`、`Join...`、`Quantize...`；正式数值 Event/Parameter Point 同质 Selection 只提供 `Quantize...`。异类或语义不兼容 Selection 必须禁用命令，不得静默只处理子集。
- 对话框以 draft 编辑；Validate/OK 显示字段、表达式、范围和预算错误。Cancel/关闭不改 Project；关闭后来源 Timeline 仍有效时恢复焦点。
- 非 finite 范围/表达式、Multiply 负 factor、依赖环、无效 grid、非正 piece length、负 gap、checked overflow、cuts/results/内存/spill/磁盘超限、取消或 revision race 都必须明确失败且无部分结果。
- 进度必须基于实际候选/刀/结果阶段，不能用无界 UI 更新；百万对象准备不能阻塞 UI 或为每个对象建立 WPF 控件。

## 8. 持久化与运行时归属

- Humanize、Split、Join、Quantize 的提交结果继续由既有 Project Format 3 Note/Event source 表达；无需新 Project 语义字段。
- Direct MIDI Split 的内部 cut order 仍由既有每 Note 两个 int64 endpoint-order 字段完整表达；Format 3 保存重开必须保留分配结果，不能在读取或 canonical/SMF 投影时重新按 Stable ID 推导。
- Auto seed 的实际值、detached staging、compiled delegates、selection mapping、进度和 spill lease 属于命令运行时/Undo payload，不作为普通 Project source 持久化。
- Preset 是 ProgramRoot 程序级可携数据；工具结果自身按正常 Project Save 写入。缓存、分页和线程调度不得影响保存字节或 canonical 结果。

## 9. 明确非目标

- Humanize 不包含 Key，不自动扩展容器，不修改拍号或 Grid。
- Split 不提供任意 C#、逐 Note 独立重启表达式或无上限刀数/结果；Join 不修改未选 Note。
- Quantize 不包含 Strength、Bar、Opaque SysEx/Meta 或 Conductor；不为不同视图另写近似网格。
- Stage 4 不包含 Note/Event Batch Create，不为 Humanize/Join/Quantize新增表达式或 Preset，不改变现有碰撞和导入重复的全局契约。
- 不设置逐对象 10 秒表达式 timeout；安全性由受限 AST、资源预算、取消和 revision gate保证。

## 10. 自动验证门

- 三种 Note source 的 Humanize/Split/Join/Quantize scalar oracle 与 bulk 结果一致，三种 point source 的 Event Quantize 一致；
- 相同 Humanize seed 重现、字段独立抽样、Auto seed 冻结、Undo/Redo 不重抽样；
- Split 三模式、空隙刀、端点、active overlap、第一片身份、Direct NoteOff velocity、cut/result/byte/spill 上限；
- Join gap 边界、overlap/touch、不同 owner/key、未选穿越 Note、checked overflow；
- Quantize 拍号变化、content/absolute tick 往返、tie、Start only/Start and End、event lane 隔离与 imported duplicate 保留；
- note later-loses / event later-wins、选择结果、Undo/Redo、取消、revision race、owned spill 清理；
- 1M Note、1M/10M point 下的线性/分页行为、取消延迟、峰值 managed/native/working set 和 UI 响应；
- Full/Incremental compiler、canonical、保存重开与冷/热缓存结果完全一致。

### 10.1 当前实施证据与尚未关闭的门

当前实现已经覆盖 Humanize、Split、Join、Note/Event Quantize 的三类 Note source、三类正式数值 point source、跨 owner 聚合、结果 Selection、Undo/Redo、对话框、Split Help/Preset、进度、取消和 revision race；功能与回归测试可作为本轮人工验收的基础。

2026-09-06 产品所有者确认功能整体验收通过。上一轮完整回归为 Core 1,455/1,455、Desktop 547/547；本次验收不关闭以下百万对象资源门。60,000 条对象探针的 `GC.GetAllocatedBytesForCurrentThread` 表示准备线程的累计分配，不是峰值存活内存或进程 Working Set，不能直接与 64 MiB resident staging 上限比较；资源门仍需按实际 staging 所有权、峰值及释放行为独立验证。

但当前生产模型仍存在以下已确认缺口，因此不得把阶段 4 标记为完成，也不得把 60,000 条对象测试解释为第 10 节要求的百万对象资源验收：

- Logical Segment 与 SubVoice 的 prepared result 仍会复制完整 owner object graph；Direct MIDI prepared result 仍会逐项重建 editable overlay；
- 生产命令仍会同时保留全量 selection、plan/edit、collision 与 replacement 结构，尚未接入可转移 owned-spill root；
- Undo/Redo 已是固定成本 root/owner replacement，结果 Selection 也已压缩，但历史仍持有 old/new 完整 owner，而不是只持 immutable changed-page roots；
- 跨 owner 操作已经形成单个 Project history entry、统一 publication gate 和失败回滚，但当前 Project 的可变嵌套列表结构尚不能提供字面意义上的单一 Project-root CAS；
- 提交后的 Logical Track / Event Instrument 编译捕获仍可能重新物化完整 owner，尚未证明 1M/10M 下的 64 MiB resident staging 门。

关闭该门需要把 Logical Note、Curve Point、Template Event 与 Direct MIDI editable overlay 改成权威 immutable COW page roots，令 staging spill lease 可转移并由 Project/history 引用计数持有，让 Compiler 直接消费同一 roots，并由 Project 级 root table 一次发布跨 owner replacement。上述改造完成前，本轮只能验收功能语义、UI 和中等规模正确性，不能验收第 10 节的最终规模指标。

## 11. 产品所有者验收清单

- Humanize：三个字段四种模式、显式 seed 重现、Tick 越界删除、Gate/Velocity Clamp、结果选择与 Undo/Redo；
- Split：Fixed / Maximum Pieces / Expression、Help/Validate、Preset 保存加载、Maximum Cuts 与片段身份；
- Join：默认 0 gap、正 gap、不同 key/owner、未选 Note 保留、Direct NoteOff velocity；
- Quantize：Note 两种模式、Event 三种来源、分数/Custom Ticks、拍号变化、tie 与碰撞；
- 三种钢琴卷帘的入口、禁用门、Dialog OK/Cancel、焦点恢复、进度/取消和错误文本；
- 大型 Selection 的准备、取消、内存/磁盘上限、一次 Undo，以及保存重开后的音乐与选择相关行为。
