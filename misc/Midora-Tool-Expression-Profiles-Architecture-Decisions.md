# Midora Timeline Tool Expression Profiles Architecture Decisions

状态：Stage 4 已验收；Stage 5 已获授权并实施中。本文冻结工具表达式的兼容、安全与资源边界（2026-09-06）。

上位需求：《Midora SRS》§20.4.11、§20.4.13～20.4.18、INV-095～107，以及 `Midora-Major-Editing-and-Visualization-Expansion-Implementation-Plan-2026-08-31.md` 的 WP-07。

## 背景

Batch Edit 已经提供以 `=` 开头的数值表达式；Note Split 的 Expression 模式也需要受限表达式。两者需要共享解析、安全审计、依赖分析和委托建立能力，但不能因此扩大 Project Mapping Function 的 ABI，也不能把 Project 数据当作可执行 C#。

本文只冻结 Timeline 工具表达式。Mapping Function Expression ABI v3 继续由 SRS 第 7、9、11、16、21 章规定；两套表达式系统可以复用底层实现，但 profile、变量、缓存键、版本和持久化边界必须分离。

## ADR-TOOL-EXPR-001：固定 profile 身份与变量 schema

当前正式 profile：

| Profile ID | 可用变量 |
|---|---|
| `midora.tool.batch-note/v1` | `v0`, `v1`, `k0`, `k1`, `g0`, `g1`, `t0`, `t1`, `tr` |
| `midora.tool.batch-event/v1` | `p0`, `p1`, `t0`, `t1`, `tr` |
| `midora.tool.note-split/v1` | `i`, `tr` |
| `midora.tool.generate-note/v1` | `i`, `v0`, `v1`, `k0`, `k1`, `g0`, `g1`, `t0`, `t1`, `tr` |
| `midora.tool.generate-event/v1` | `i`, `p0`, `p1`, `t0`, `t1`, `tr` |

Batch profile 中，后缀 `0` 表示当前对象编辑前的冻结值，后缀 `1` 表示该字段在本次对象求值中的结果。`tr` 是当前对象编辑前 Tick 减去本次冻结 Selection 的最小 Tick。结果字段可以依赖其他结果字段，但依赖图必须无环；直接或间接依赖自身结果必须拒绝。

Note Split 中，`i` 是从 0 开始的当前刀序号，`tr` 是上一刀相对 owner 内 `selectionLeft` 的 Tick，第一刀前为 0。Split profile 不隐式提供 Note、Batch Edit 或 Project Context 变量。

Generator 中 `i` 是零基候选序号，`*0` 为上一轮正规化结果（首轮为 Initial），`*1` 为本轮 DAG 结果，`tr` 为输入 `t0`。首对象开关开启时 Initial 是计入 Maximum 的 candidate 0，表达式从 i=1 开始；关闭时 candidate 0 也先执行表达式。空输入 identity，Tick 是相对 Base 的偏移；见 SRS 20.4.19。Generator 不改变 Batch profile 的变量或含义。

Profile ID、版本、变量集合、变量语义、运算符/函数白名单、结果类型和数值归一化契约共同构成兼容边界。任一边界改变都必须发布新的 profile version，不得静默改变 `/v1`。

## ADR-TOOL-EXPR-002：单一受限语法与安全边界

用户输入的非空表达式必须以 `=` 开头；`=` 后必须是单一数值表达式。调用方去掉前缀并按工具规则 Trim 后，expression body 才进入受限编译器，8,192 scalar 上限作用于该 body。Batch Edit 原有的直接数字、百分比和单步常量运算属于调用方输入形式，不属于该表达式语法。

所有 profile 共用固定上限：

```text
Maximum source length: 8,192 Unicode scalars
Maximum syntax nodes: 512
Maximum syntax depth: 64
Result type: finite double
```

只允许数值字面量、当前 profile 变量、批准的算术/比较/布尔运算符、条件表达式和版本化纯数值 `System.Math` 白名单。必须拒绝声明、赋值、递增/递减、lambda、delegate、对象或数组创建、索引器、语句、循环、递归、任意 API、反射、I/O、环境、线程、时钟和非确定随机源。

正式执行只建立 `System.Linq.Expressions` 委托；不得 Emit 或加载用户源码程序集。解析、语法节点计数、深度检查、名称绑定、API 白名单、类型检查和 finite-result 检查都必须在正式执行路径中生效，不能只依赖 UI Validate。

## ADR-TOOL-EXPR-003：确定求值与字段依赖

每条表达式在一次命令准备中只编译一次，随后对候选记录重复调用已建立委托。Batch 多字段求值先从语法树提取所有 `*1` 依赖，建立确定的拓扑序，再按该顺序对每个对象求值；循环、未知结果变量或同字段自引用在读取任何正式 Project 数据前拒绝。

表达式结果必须是 finite `double`。转换为整数 Tick/Key/Gate/Velocity/Point Value 或刀长时使用工具正式值域规则和一次 `MidpointRounding.AwayFromZero`；不能依赖当前区域性、分页返回顺序、线程调度、缓存命中或 Stable ID 的字典枚举顺序。

表达式自身没有循环且 AST 有硬上限，因此不设置“每条记录 10 秒”超时。长任务由候选/结果数量、分页资源预算、周期性取消检查和 Project revision gate 约束。

## ADR-TOOL-EXPR-004：Preset 是程序级不可信输入

工具 Preset 只保存在 `<ProgramRoot>\Data\Presets` 下的对应工具分类；不读取、迁移或删除旧 `%LOCALAPPDATA%\Midora\Presets`。Preset 不属于 Application Preferences 或 Project，不进入 `.midora`、Project Modified、Undo/Redo、compiler、canonical fingerprint 或音频缓存身份。

每个 Preset 文件必须至少携带独立的 `presetSchemaVersion`、完整 `expressionProfileId`、`toolKind` 和数值格式契约。加载本机、复制或外部导入的 Preset 时必须把它当作不可信输入，按当前 profile 重新执行长度、语法、节点、深度、名称、API、类型、依赖图、finite 和工具值域验证；成功反序列化不构成可执行授权。

Batch Note、Batch Event 和 Note Split 的 Preset 互不混用。Humanize、Join 与 Quantize 在 Stage 4 不因共用大型事务而获得隐式表达式或 Preset 能力。

## ADR-TOOL-EXPR-005：Split 采用独立的有界结果预算

Note Split 的 Maximum Cuts 默认值为 65,535，用户可配置值的硬上限为 16,777,216。每次表达式求值以及落在空隙中的刀线都消耗 cut 配额；刀数不能代表结果片段数。

一次 Split 的结果记录上限为 100,000,000，decoded/encoded working bytes 上限为 64 MiB，resident staging 上限为 64 MiB，owned spill 上限为 16 GiB。算法必须使用严格递增刀线和 active-interval sweep，禁止每条 Note 遍历全部刀线。预算、表达式或磁盘检查失败时丢弃 detached staging，不发布任何部分结果。

Owned spill 只能位于 `<ProgramRoot>\.tmp\CompilerRuns` 的本次 lease 下，并在成功提交、取消、失败或 Dispose 后按所有权规则清理。Preset 可以保存用户配置的 Maximum Cuts，但不能提高上述硬上限。

## ADR-TOOL-EXPR-006：缓存、生命周期与兼容失败

表达式缓存键至少包含完整 profile ID/version、规范化源码、变量/结果字段身份、白名单版本和数值契约版本。缓存只属于当前进程运行时；不得写入 Project 或 Preset，也不得把一个 profile 的委托复用于另一个 profile。

未知 profile、未知版本、未知变量、旧数值契约或超限表达式必须明确验证失败，不做最佳努力降级。Project Mapping Function ABI v1/v2 的识别与拒绝规则不适用于这些工具 profile；反之，工具表达式也不得被 Mapping Function ABI v3 执行器接受。

## 验证门

- 每个 profile 的允许变量、未知变量、大小写与跨 profile 泄漏 golden；
- 8,192/8,193 scalar、512/513 node、64/65 depth 边界；
- 允许的算术、比较、条件和 Math 成员，以及赋值、lambda、对象创建、索引、语句、循环、任意 API 与反射攻击样本；
- Batch result dependency DAG 的无依赖、线性依赖、菱形依赖、自环和多字段环；
- NaN、Infinity、溢出、AwayFromZero 与区域性无关性；
- Split `i/tr` 序列、compile-once、多次委托调用及 cut/result/working/resident/spill 各上限；
- Preset profile/tool/schema/version 不匹配、损坏 JSON、外部恶意表达式与加载时严格重验证；
- 同输入重复执行、冷/热缓存、不同分页和不同线程调度得到完全一致的结果。
