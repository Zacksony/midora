# 阶段 5 / M13：共享历史根与 Clipboard 所有权

日期：2026-09-09。实现设计记录，不修改 SRS、音乐语义或文件格式。

## 需求追踪与范围

- 输入：冻结 Logical/SubVoice owner revision、分页 Clipboard、编辑与撤销/重做命令。
- 输出：精确的原子新根、来源/change set、完整 Undo/Redo 与选择；旧 revision 继续可读。
- 依据：SRS §3.10～3.12、§20.3.15、§20.4.10～11，INV-095～096；内存执行计划 §10 的 M13。
- 失败：取消、验证、I/O、revision race 只释放未发布 staging，不使仍由根/历史/读者持有的页失效。
- 归属：以下结构只属于 runtime source/history/clipboard，不持久化、不独立标记 Modified。
- 非目标：不裁剪历史，不引入历史总磁盘硬限，不重算 Undo/Redo 表达式或随机数；不修改 Compiler/Playback、已有阶段 4 文档或 Pure MIDI 编译布局。

## 已确认的成本与修改决定

1. `AdoptEditedSnapshot` 当前共享音乐叶，但每次用全部叶重建全部 sequence 分支。改为对替换/删除叶做 persistent path-copy，再 concat 追加根；未触及分支复用同一对象。空间目录与稳定 ID 规则保持不变。
2. `EditedLeafValueSource` 当前引用上次叶，长期编辑同一叶会形成依赖链。改成最多 128 个槽的平坦来源映射：来源表、source-index byte 与 ordinal int；新槽直接指向真实 backing source 或当前 edit source，覆盖后不再引用无用的中间叶。原叶的 scalar overlay 必须先精确纳入映射；不得将整 source 解码常驻以换取压平。
3. Note Clipboard 已完整构建独立 bounded owner-root，但通用 `KeepClipboardAlive` 仍先建立全 Project draft，再由完成后的历史持续持有原 Clipboard lease。仅为已审查的 Logical/Direct Note paste 增加内部独立准备标记：直接使用现有 detached owner-root command，准备期间保留 Clipboard lease，全部成功后 prepared root 不再保留原 Clipboard。其他结构粘贴维持旧流程，不以数据种类猜测独立性。

## 验证与计账

- 同时保留多个 revision，按对象 reference identity 区分唯一/共享 sequence 节点；统计 source 实际固定宽度内容字节、resident/spill 与独立读缓存。CLR 对象头不冒称精确 payload 字节；托管分配/GC 后存活量另记实测。
- 100k/1M roots 的单条、多条、删除、追加、旧 revision、深 Undo/Redo 与新分支；长期同叶编辑确认依赖深度不随历史线性增长。
- Logical→Logical、Direct→Logical、Direct→Direct note paste，Clipboard 替换后原 backing 可释放、有效 Undo/Redo 不失效；取消与旧 revision 发布拒绝不提前释放源。
- 每个测试进程都由外层守护监控 private bytes 与 working set，任一达到 8 GiB 即终止；重型构建/测试先取得 root 的串行运行槽，不无守护直接运行百万测试。

本记录先冻结方案，结果在实施及测试后补入；不把源码成本或用户报告数字冒充已测得的泄漏。

## 首轮实现证据（2026-09-09）

Release 构建 0 warning/error。`m13-targeted-results/m13-targeted.trx` 中 41/41 通过（12 个新 M13 case，另含共享根、剪贴板及跨类型回归）；实际 DLL 位于 `.tmp/memory-stage5/m13-tests/`。测试使用独立 Windows Job，8 GiB private commit 硬上限及 private/WS 100 ms 监控，系统可用内存保留 2 GiB；进程树峰值 private `549,597,184`、WS `669,364,224` bytes，无守护终止。

- 100k Logical/Template 源、64 次小编辑：65 revisions 的唯一 sequence 节点从 1,563 增到 2,249，仅新增 686；1M 从 15,625 增到 16,518，仅新增 893。节点按对象 reference identity 去重，不是“每个 revision 节点数相加”。百万 Logical 的 64 次小编辑本线程总分配 `1,836,160` bytes；这是实际 allocation，不是 GC 后 live heap 或全 history 成本。
- 小改仍访问 O(N/128) 叶以更新已定义的空间元数据，但 sequence 分配改为受影响路径的 O(log(N/128))，未触及音乐页、ID/空间目录仍共享；不以线性扫描替换稳定 ID 精确查询。
- 连续覆盖同一叶 512 次、只保留当前根后，前 511 个已无值被使用的变化源均可被 GC 回收；当前源和邻近普通 scalar overlay 保持正确。若有效 Undo/Redo 或旧读者仍持有历史根，旧源继续存活是必要行为，不作泄漏判定。
- 8,192 个 Logical Clipboard 的实际固定宽度源 spill 为 `196,608` bytes，Direct 为 `458,752` bytes（分别逐一断言 `count × Unsafe.SizeOf<record>()`，该层无 wire/header 字节）。完成 prepared 后释放 payload 与命令租约，源资源的 resident/spill/working 全为 0；Logical→Logical、Direct→Logical、Logical→Direct、Direct→Direct 四向均保留全部 note 值及 Undo/Redo，Direct→Direct 保留 NoteOff velocity，未触及的 Track/Segment 身份不变。
- 64 个正式小编辑命令逐个 Undo/Redo 恢复完全相同 owner root；新分支只移除已失效 Redo，仍被外部读者保留的旧 revision 继续可读。取消前不改变原 root、历史或 ID 高水位，取消后的同一 Clipboard 命令仍可再次准备。

独占与共享边界：本轮没有把所有 CLR 目录、delegate、runtime cache 或操作系统文件缓存量化为精确 payload bytes；每命令预算也不是历史持有成本。完整历史不裁剪，结构类 Clipboard 仍走原先 whole-Project detached 事务并保留必要源租约。此次只移除已证明冗余的 Note paste 所有权与 sequence/source 链，并未宣称 M13 单独解释用户报告的全部 GB 增长。输出存储完成后的 `Ready` 边界取消测试已补充，待下一轮串行验证；不计入上述 41 个已通过用例。

## 独立复查后的 splice 补充决定

源码确认 `AdoptSplicedSnapshot` 仍用全部叶重建分支，而且 `LeafValueSource(旧叶)` 即使只使用一段 fragment，也会保留整个旧叶的变化来源。交替 Split/插入碎片与小编辑时，未覆盖 fragment 可能继续持有已不再使用的旧 provider。这是原有路径的 M13 残余，不是上述实现引入的值语义回归；2026-09-09 已确认在本阶段继续收口。

- sequence 增加内部 `TransformLeafRuns`：每个被命中的旧叶产生零/一/多叶片段，原始 ordinal 仍按旧树计数；原未变子树直接共享，变化路径用既有 AVL concat 组合。新叶集合只覆盖真正变化内容，不重建全根；叶内巨大展开的最终分支构建也检查取消。
- splice fragment 发布时，只展开其实际保留槽的 backing 地址；原 leaf 的非选中 override 不保留，正常 base 直接指向 immutable source/array buffer，不再借整个旧 leaf 保活。音乐值不复制成结果规模数组，来源槽最多 128；有效旧 history/readers 继续持有旧 backing。
- 增加整叶/整根删除与重新追加、交替 splice/小编辑、fragment 边界 provider 释放、旧根恢复及模板命令准备/提交/Undo 验证。以上是后续实现与测试决定，不能计入首轮通过记录。

## 最终共享代码回归

上述 splice 补充及 Ready 取消案例已纳入最终 Application **1,128/1,128** 全套（M13 专属共 21 case）并通过，不再是待测试实现。TRX：`.tmp/memory-stage5/regression-final-v1/Midora.Application.Tests.trx`。同一共享 Domain 构建的 Compiler、Persistence、Playback、MidiExport、AudioRender 与 Desktop/Presentation 公共回归也全部通过；完整计数和限制见[阶段 5 验证报告](Midora-Memory-Stage5-Validation-Report-2026-09-09.md)。不把该自动测试结论替代阶段 6 长会话/人工验收 C。
