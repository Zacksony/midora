# 阶段 6：Int64 诊断逻辑序列与有界消费者（历史暂缓记录）

> 2026-09-09 后续决定：用户已批准独立实施 Int64 完整诊断、条件分页及超限友好失败，并将 MIDI README 改为前 1000 条 Warning/Info + 精确省略数。当前契约见 [Int64 诊断与有界 MIDI README](Midora-Int64-Diagnostics-and-Bounded-Readme-Design.md) 与 SRS INV-117。以下保留阶段 6 当时的观察和暂缓决定；其中“完整 README 不得省略”等候选描述已被本次明确决定取代，不是当前要求。

日期：2026-09-09。性质：实现设计提案；不修改 SRS。当前只完成只读审计，尚未实施生产代码。

## 产品决定与本轮边界

用户已于本轮阶段 6 明确决定：**暂缓 Int64 诊断序列 / 分页 UI 迁移，保留当前已知 Int32 总数边界**。本文件继续保留后续实施所需的证据、候选方案和验证门；下述建议不是已批准的生产契约。

这是用户明确选择的阶段范围，不是阶段 6 遗漏项，也不能写成问题已解决：超过 `2,147,483,647` 条逻辑诊断仍会触发 checked overflow，无法发布完整新诊断结果。当前行为不截断、不聚合、不删除诊断；65,537 个同 key 全相交 Note 仍是可达到该边界的公式例证。全局 F4 / Shift+F4 的既有 SRS 要求只记录审计观察，不在本轮主动扩充。

后续重新授权实施时，需要一并决定公开 Long 序列与用户可见浏览工作流，不能只改 Compiler 内部计数或只增加 UI 页按钮。

## 1. Requirement trace

- 输入：正式 Compiler 已冻结的普通诊断，以及按 Usage、StartTick / SourceOrder 和 Same/Any Pitch 规则得到的 overlap sources/ranges。
- 正式输出：完整诊断逻辑序列，保持逐 pair 条数、重复、原 i→j 顺序、Severity、英文 Message、SourceReference、失败阶段及 Warning policy；Full/Incremental 相同输入继续完全等价。
- 依据：SRS §12.18.2、§12.19、§12.20、§12.21.2、INV-010/015/066；Diagnostics Workspace / Status Bar 遵循 §17.5/17.8，搜索与 F4 导航遵循 §20.9.7/20.12.9；执行计划 §11 明确要求评估 Int64/分页消费者，不允许截断收口。
- 边界：逻辑 ordinal 只属于当前不可变诊断序列，不是 stable ID，不持久化、不跨 revision 复用。物理 source/range 长度继续有界；增加的是精确可表达总数，不是逐诊断对象常驻。
- 失败：checked Int64 算术、非法 ordinal、取消和筛选失败不得发布部分结果；诊断数超过 Int32 不再导致编译阶段失败。保留已有业务错误与消费者拒绝失败结果的行为。
- 诊断自身不进入 Project、Undo、Modified、canonical 音乐 fingerprint 或 `.midora`；筛选、页 offset、选中行和滚动只属于 UI session。
- 非目标：不删除/聚合/降级诊断，不改变 overlap、音频、MIDI 或 persistence wire；不通过制造数十亿真实对象进行压力验收。

## 2. 已确认事实

1. `CompilerDiagnosticList.Builder._count`、`DiagnosticRange.EndExclusive`、severity totals 和公开 `IReadOnlyList.Count` 当前均为 Int32；65,537 个同 key 全相交 Note 产生 `65,537 × 65,536 / 2 = 2,147,516,416` 个 pair，构造 ranges 时发生 checked overflow。
2. 该例只需 65,537 个 compact source values 与 65,536 个 range descriptors，物理紧凑表示本身不需要数十亿 records。把逻辑前缀与统计提升为 Int64 即可保留全部 pairs。
3. CanonicalCompiledResult、CompilationRejectedException、MIDI/Audio task result 和 MIDI README projection 都将诊断暴露为 `IReadOnlyList`；仅改 compiler 内部计数会使下游继续溢出或截断。
4. Desktop 的 `VirtualDiagnosticRows` 已有最多 256 行缓存和按物理 source 筛选，但 WPF `IList.Count`/index 是 Int32；Workspace 当前将完整列表作为单个 ItemsSource。状态栏 Error/Warning 也是 Int32。
5. README 正式路径已逐行流式写入；其 projection/count 和 request 类型仍是 Int32。完整输出耗时与真实诊断正文长度成正比，不能以省略行或改短报告解决。
6. 本次只读搜索未找到现有全局 F4 / Shift+F4 Active Diagnostic 实现。它是既有 SRS 要求，不应把仅添加页按钮当作已经满足全局导航。

## 3. 建议的内部/公共契约

1. 使用显式 Int64 只读序列契约（`IEnumerable<T>` + `long Count` + `this[long ordinal]`），不能让正式消费者继续读取可能溢出的 Int32 Count。普通小数组可由零复制冻结适配器承载。诊断公共属性、异常、任务结果、README projection/request 一并迁移；测试或兼容入口需要 Int32 集合时必须显式请求有界 page，而不是 clamp 全量 count。
2. CompilerDiagnosticList 的累计 count、range EndExclusive、severity totals 改 long；每个物理 range 的 SourceIndex/Start/Count 保持 int。二分先定位 long ordinal 所在 range，再在验证后的范围内 checked 转换为 source-local int。
3. Filter 继续逐物理 source 求值、按 range 建立完整保序逻辑序列；累计数量用 long，取消粒度不弱化。枚举完整序列继续按 ranges 顺序逐条产生，不能使用 `Enumerable.Count()` 计数或为报告调用 `ToArray()`。
4. Page 契约使用 long startOrdinal + int maximumCount；返回最多请求数的只读局部视图，零复制共享 immutable root。不得逐页缓存完整历史。失效/关闭释放 root 与筛选取消源。
5. README 完整 Warning/Info projection 使用 long Count；仍由冻结 root 按原 Markdown / UTF-8 字节流式写入。取消和 I/O failure 使用既有事务回滚，异常短消息最多 32 行但保留完整 long sequence。

## 4. UI 影响与需要确认的取舍

WPF 原生 IList 不支持 Int64 项索引。建议保留正常规模现有连续虚拟列表；只有筛选后结果超过 Int32.MaxValue 时，切换为明确标示的有界窗口（每页 4,096 行），显示 `Rows A–B of N`，提供 Previous / Next / Go to page。筛选仍作用于全部 N 条，而非当前页，Summary 和 Status Bar 使用精确 Int64 总数；源导航仍使用原 SourceReference，不显示 stable ID。

该方案只在原来无法发布的 >Int32 边界新增 UI 浏览能力，但确实新增用户可见分页工作流。执行计划已经要求独立评估，不应默认为获准。实施前需要产品所有者确认该工作流，或明确选择其他全局 long-scroll 表面。

如批准分页：全局 Active Diagnostic cursor 使用 long ordinal、独立于 Workspace 搜索；F4 / Shift+F4 保持既有 SRS 的“全局活动诊断”语义。当前筛选隐藏的诊断不应被跳过，来源导航不依赖当前页。不同 revision 的行身份不得映射到旧源；页切换不能扫描全部 pair 寻找 selection。

备选是自绘连续 long-scroll 虚拟表面，以全局 long offset 映射有界局部 WPF rows；这需要重做列表滚动/键盘/可访问性行为与精度验收，改动和风险明显大于条件分页，不能仅改 scrollbar Maximum 宣称正确。

## 5. 验证门（待实施）

- 使用 65,537 compact sources /65,536 triangular ranges 直接覆盖 2,147,516,416 精确 Count、severity totals、0/Int32.MaxValue 前后/最后 ordinal；以整数三角前缀 oracle 检验 source identity，不枚举全部 pairs。
- 完整枚举小型多 source、多 ranges 和过滤夹具，与旧 pairwise reference 比较条数、顺序、Severity、来源；大夹具筛选最后一个 source，应精确得到 65,536 重复行。
- 覆盖插入普通 prefix/suffix 跨越 Int32 边界、分页边界和非法请求、空页/最后短页、取消、不同时刻旧页 root 释放；计账必须按物理表去重。
- Compiler 小型 Full/Incremental 全序列相等，边界夹具抽样与闭式 oracle 相等。若进行正式 65k 编译，放入既有受守护测试树，不要求生成完整 README 或执行数十亿次 digest。
- WPF 无 computer-use 单元测试验证分页 ItemsSource.Count 有界、最多 256 行缓存、选中/来源导航、精确总数、全源筛选、最新 revision/取消和弱引用释放。
- README 使用可在少量迭代后取消的巨大逻辑序列，证明准备不读取全部条目、取消不发布部分文件；小夹具完整旧字节 golden 保持一致。

## 6. 文件归属建议

诊断子任务可独占本设计、CompilerDiagnosticList、CompilationRejectedException、诊断相关 Compiler tests，以及任务结果/README long 契约。Desktop `PresentationModels.cs`、`DesktopSessionController.cs`、`MainWindow.xaml(.cs)` 为共享大文件，必须先与主任务协调，再实施精确区块；其他阶段 6 生命周期工作不得同时修改诊断区块。
