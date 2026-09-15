# 内存优化阶段 1：Pure MIDI 正式只读值枚举

日期：2026-09-08。状态：已实施并通过阶段 1 工程门，人工验收待阶段 2 后合并进行。依据：[执行计划阶段 1](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md)、[调查 M01](Midora-Stage8-Memory-Investigation-2026-09-08.md)。

## 1. Requirement trace

- 输入：已提交 revision 的 Pure MIDI immutable source、replacement、tombstone、formal added root。
- 输出：与正式集合次序一致的只读 value 序列，包括 crop 外内容、导入重复和原始 endpoint / event order；不按 tick 重排，不分配 ID。
- 不变：Project Format 1/2/3、严格损坏检测、全包 ID 唯一性、保存自校验及原子替换、编辑 / Undo / 选择语义。
- 运行时边界：只读遍历不注册可编辑对象，不填 source 三表，不建立空间索引或全量 ordinal 数组。工作空间为游标与底层已有有界页缓存。
- 失败：取消及时中断，非法持久化仍拒绝；未提交 batch 不允许被误当成完整快照；正常 owner 生命周期结束后不能继续读取其已释放 pack。
- 非目标：不处理阶段 2 的 WPF 泄漏、阶段 3 的稀疏 ID / payload / builder 总预算，不改变音频或批量工具行为。

## 2. 决定

三种 Pure MIDI collection 增加 `EnumerateValues(CancellationToken)`，在调用时冻结已经发布的 formal roots，随后按 base source ordinal（跳过 tombstone、原位 replacement）→ formal added 次序枚举。

不能使用 `QueryValues(0, long.MaxValue)` 替代：空间查询次序不是正式 IList 次序，范围条件还可能漏掉需要被严格校验的非法 tick。不能使用 `CreateObjectSource()` 作完整保存入口：它承担随机 ordinal / 空间查询目录准备，本场景不需要其成本。

返回的是值快照，不是连接到 Project 的可写 facade；修改值副本不提交 Project 编辑。Opaque payload 使用已有只读内存语义，不为扫描再次复制字节。读者不得把返回内存改写为音乐内容。快照保留已冻结根，后续编辑不改变该次枚举；仍须在所属 Project / source 生命周期内使用，不引入第二套 lease 协议。

未提交 `BeginBatchChange` 期间调用明确拒绝，避免静默读到旧 formal roots。普通 mutable IList / ID lookup / setter 保留原有编辑对象契约，不粗暴删除其登记表。逐一迁移正式只读入口，并通过容器计数测试防止退回对象枚举。

## 3. 首批消费者及验证

- Package 保存预检、打开 ID 检查：只枚举值中的 ID，继续全包唯一性 / 高水位检查。
- 非 pristine merged pack writer：写 value，保留所有字段、序列和既有端点生成。
- 小内容 canonical 路径及其他纯读取入口：按需迁移；大内容继续既有延迟 canonical / 范围查询，不建立新全量数组。
- 编辑入口、精确碰撞事务中的可变对象引用不是只读值，逐项审计，不机械替换。
- 测试覆盖 base / overlay / replacement / removed / clear / reordered、延迟开始枚举及多次枚举、取消、非法与隐藏内容、已取得 facade 的 setter 和 Undo。

实测、回归与未解决边界见 [阶段 1 验证报告](Midora-Memory-Stage1-Validation-Report-2026-09-08.md)；只读 / 可写调用点归类见 [操作覆盖索引](Midora-Memory-Optimization-Coverage.md)。不把 M01 工程门通过扩展为 WPF / 所有内存问题已经解决。
