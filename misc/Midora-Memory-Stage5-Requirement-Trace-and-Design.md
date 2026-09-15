# 内存优化阶段 5：编译展开与跨命令资源治理

日期：2026-09-09。状态：本阶段实现与专项验证已完成；本文件为设计依据，实际通过项、实测与未解决边界见[验证报告](Midora-Memory-Stage5-Validation-Report-2026-09-09.md)，不代表人工验收 C。

依据：六阶段执行计划 §10（M12 / M13）、SRS §12.21 / §12.24 / §12.25、INV-009 / 010 / 015 / 020 / 095 / 096。用户已确认阶段 3＋4 的 B01～B09 通过，并明确授权阶段 5；阶段 4 尚未提交的修改原样保留。不自动进入阶段 6，不提交、推送或生成 dist。

## 1. Requirement trace

| 项 | 本阶段契约 |
| --- | --- |
| 输入 | 当前已提交 Project revision、不可变 source roots、正式选区、编译上下文与有效历史 |
| 输出 | 与改造前相同的 canonical 音乐序列、诊断、统计、来源及确定性 Undo / Redo；可解释的资源所有权与实測结果 |
| 边界 | Logical / SubVoice / Pure MIDI、跨类型剪贴板、长历史、Loop / Mapping / Pre-Roll、失败和取消修订 |
| 失败 | 准备失败/取消不发布部分音乐结果，不损坏当前源与旧有效历史；不得把 OOM 当正常资源策略 |
| 持久化 | 不改 Project Format 3、既有 Format 1/2 reader、Mapping ABI 或 presentation schema |
| 运行时归属 | Raw / canonical / diagnostic / compiler cache、历史和剪贴板 leases、临时外排归编译/编辑会话，不进入项目 |
| 非目标 | 不改声音/生命周期/碰撞/诊断语义，不裁剪有效 Undo，不关闭自动编译，不降低图形精度，不调整音频算法 |

## 2. 必测复现与测量分层

用户样本：`9KX2 18 Million Notes.mid`，`MIDI Out #23`，从 tick 168960 至末尾约 138 万条音符。粘贴到新 Logical Track 的 Segment（ProjectStart=168960，Length=24576）的 local tick 0；随后重复添加单音符及批量编辑。用户观察 WS 约 1.1→4.5→7→8→17 GB，同类 Pure MIDI 复制粘贴没有相同膨胀。

源码已确认：跨类型粘贴仍走有界值页；Logical 脏 Segment 重新展开全部实例；每实例/voice 会建立 Raw events 与来源；编译事务保留旧 cache 以便异常回滚；默认 Reject/SamePitch 的重叠校验逐对产生诊断。这些只是持有/分配路径证据，不能在堆测量前把全部增长归结为一个组件。

分别测：source/目录/历史、Raw、诊断、canonical/排序、当前与上次成功结果、取消与过期候选交接。Pure MIDI-only 是对照，不以其内存结果替代 Logical 结果。累计分配、post-GC 存活量、private bytes、working set、峰值和墙钟分别报告；诊断性 GC 不进入产品或性能计时。

## 3. 测试安全

重型负载严格串行，从小规模逐级上升。外部守护只拥有自己启动的测试进程树，默认在 private bytes 或 working set 到 8 GiB 时终止（提前于用户要求的约 9 GB），并监控系统剩余可用内存。探针内部检查为补充，不替代外部守护。记录阈值、进程 ID、触发时间、最后阶段和退出原因；超限是测试失败，不是通过，也不得继续自动升规模。

不得终止用户自行打开的 Midora 或其他进程。不得用系统耗尽/分页假死来测试上限。上述阈值仅属于测试安全，不添加为产品的新容量上限。

## 4. 实施和验证顺序

1. 冻结当前（含阶段 4）基线，建立受守护的小规模归因探针。
2. 按证据处理 Raw / diagnostic / canonical 重复存储和当前/旧修订持有；选择紧凑表示、共享不可变数据、分页/有界外排及及时释放。任何表示变化保留原正式枚举顺序与源定位。
3. M13 按底层唯一内容统计并共享历史/剪贴板目录和页，精确验证 Redo 分支丢弃、剪贴板替换、关闭与取消的资源归还。不能用删除历史制造平台。
4. 比较 Full / Incremental / 取消后重试 / 重复编译，包括失败诊断；覆盖 Loop、Pre-Roll、共享 Usage、逐音符隔离、范围恢复、同 tick 及 Pure MIDI 混合。
5. 受守护重跑真实复现与多修订，报告时间变化、独占/共享内存、未覆盖风险。实际结果单独写验证报告，不在本设计中预填通过。

具体表示改造在基线测量后补充于本记录或链接的独立设计记录，先于相应公共/并发模型变更实施。

## 5. Raw 编译缓存的表示决策

现有 `RawMidiEvent` 每条内嵌完整 `SourceReference`，即使 Reset/Initial State 在每个实例完全一致，也保留不同的完整数组。首先使用相对于实例的 tick / sequence / semantic-group 和来源上下文，保存紧凑 event pattern；同一 Segment 内完全相同的 pattern 通过有界 interner 共享。比较使用完整字段精确相等，hash 只用于候选查找；不按可听等价折叠或省略正式事件。来源的 Track / Segment / LogicalNote / Instrument / SubVoice / Usage 和原 tick 在读取时精确恢复，特殊值与 sentinel 也必须无损。

interner 仅负责有限大小的重复发现缓存，不限制合法音乐结果；达到预算后仍保留必要结果、停止加入 interner。源 pattern 持有本次 frozen revision，不与下一修订的可变模型混用。重复状态的共享不依赖是否启用 Mapping、Loop 或逐音符隔离；无法精确复用的 pattern 使用自身记录。该改造不把缓存命中变为语义依据。

同时，未启用 destructive overlap policy 时，不建立只供 CutPrevious/CutNewRejectNew 使用的完整排序与标志数组；正式非 destructive overlap 验证仍执行。canonical 已排序序列的同 tick 状态归并采用稳定的原地压缩，避免每轮创建同尺寸第二数组；其保留元素与顺序必须与旧反向扫描模型完全一致。

导出 / 音频任务结果共享完整不可变诊断序列，不再强制 `ToArray`。播放编译失败保留完整诊断在专用异常的 `Diagnostics` 属性中，仅异常消息限为前 32 行及剩余条数，避免错误展示为了拼接文字再次展开数百万条诊断；这不是编译器丢弃诊断，正式诊断计数、顺序和定位均不变。

范围处理独占消费当前编译阶段的 canonical list，以固定读取终点和不越过读取指针的写入位置原地保留范围内事件；完成扫描后才追加恢复 / cleanup。不再与源列表同时保留第二个同规模增长列表。取消丢弃本次局部列表，不影响已冻结 cache 或先前结果。合并初始状态、目标闭包和 tick-zero targets 仅依赖冻结 Instrument/SubVoice/Project defaults，按当前 Segment 编译池预计算一次并共享；不跨 revision 复用，触发音符相关 Mapping/Loop 输出仍逐实例求值。
