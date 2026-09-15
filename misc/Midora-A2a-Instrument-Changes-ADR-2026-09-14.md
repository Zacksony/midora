# A2a：Instrument Change source 与事务设计

依据：已批准 R27／D-IN01～05；执行计划 T-IN-01～07。状态：A2a 已接入并完成记录中的定向自动验证，用户已于 2026-09-14 整体确认 UAT-A2a-IN-01～06 通过；完整门、未跑项与 A2b 边界见同日期实施记录。

## 1. 正式数据和兼容性

- MIDI Segment、SubVoice 持有不可变关联根。一个关联仅包含自身 Stable ID 和成员 Stable ID；不复制 Tick 或任何 MIDI 数值。反向成员索引只加速编辑查询，音乐事实仍为原始事件。
- 新 writer 为 Project Format 4，Manifest Schema 4；唯一必需 `settings/instrument-changes.pb` 使用独立 protobuf Edition 2024 schema 1。旧 Format 1／2／3 reader、schema、descriptor 和 golden 不变。无变化点也写带版本头的空组件。
- 关联文件是严格 source：缺失、未知／重复字段、无效成员、成员跨 owner、Tick／target 不完整、重复 ID 必须拒绝加载，不使用 presentation 的可丢弃恢复策略。
- 关联记录逐条流式写／读，不建立 protobuf 全量 repeated 对象图；包装与 raw ID 一并通过已有有界全包身份检查。原子保存、自校验、迁移确认和来源永久副本机制继续复用。
- 包装不产生 canonical 音乐事件；import 不推导包装。编译仍从 raw 生成音乐结果。

## 2. 命令与顺序

- 选择窗口使用本地 draft。确认才准备一次 detached 原子命令；取消、准备失败、旧 revision 都不向源分配对象或提交部分成员。
- 值编辑保留身份与原始 Order；完整组的结构有效性在整个事务最终态决定。部分成员移走／删除／改 target 后解组，剩余 raw 不丢失；Undo 恢复原始关联。
- 精确 planner 通过成员反向索引验证受影响组，并为连续子命令保留明确的临时解组回执；最终态才消除回执。回执和 validated raw revision 不持久化。旧原位兼容路径保留按显式关联读取成员的安全复核，不扫描未关联 raw；其海量包装时间门仍需 A2b 跟进。
- 新 MIDI 包装先为同 Tick 旧消息保序留出三个位置，然后提交 CC0→CC32→PC。同 Tick NoteOn 位于其后，其他 Track 的正式全局顺序不变；绝不以 Stable ID 代替事件 Order。密集 Tick 的重排通过既有有界值 patch 处理，不建立全量事件数组。
- SubVoice 的完整 Bank 与 Program 保持两个 raw 对象，使用现有编译类别顺序。高级部分 Bank 和独立 Program／Mapping 入口不删除。
- 父 owner 克隆、复制、Split、转换必须共享不可变根或按成员新 ID 明确 remap；不可借 Tick 重新发现关联。

## 3. Initial State 和试听边界

- Initial State 三字段覆盖值与继承后有效显示值分离。单字段修改不固定其他继承字段；显式选完整 preset 才覆盖三字段。不创建 Tick 0 原始事件。
- 试听建立独立受控 Project/CompileContext，经 canonical 与现有 Preview 链；只读取冻结程序音频配置，不套用当前编曲的控制器、Mapping 或通道状态。
- 自动试听采用采样时序的有限 Gate；手按采用既有 held Gate 语义。窗口拥有试听会话，latest-wins 队列至多一个当前请求和一个待替换请求；退出、提交、取消先停止自有试听，不停止普通 Project 播放。
- 无可用音色库／设备只使试听不可用，不能阻止合法音色编辑。

## 4. 验证和未完成门

新格式和旧 reader/golden、原子事务关联、父复制/分割、UI BAML/布局/Enter/Esc、试听 owner/队列已按实施报告验证；真实音频和全量性能分布不是本轮已有证据。源关联为不可变树、显示索引可 spill，不能将后者的 4 MiB 预算误称为整个 source 包装索引的内存上限。A2b 的完整包装批量 UI、List 投影与海量包装 source 预算仍属下一切片。
