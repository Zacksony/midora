# 新需求批次前：文档收尾、验收状态与下一步

日期：2026-09-09。性质：非规范性状态台账和工作顺序记录；不改变音乐语义、文件格式或产品版本。

2026-09-10 补充：新增 §4.1，记录用户已批准、尚未实施的 SMF 导出编码边界；本次同时获准更新 SRS，导出线格式兼容策略改变，但 Project/canonical 音乐语义及产品/持久化版本不变。其余验收与 §5 验证数字仍是 2026-09-09 的历史记录。

2026-09-10 后续实施更新：Logical 编译结果内存优化及其后续时间回收均已实施；LC-T3 后用户已确认验收通过并提交、推送。初次内存交付的明显时间回退保留在[历史报告](Midora-Logical-Canonical-Memory-Verification-2026-09-10.md)；同日对照、波动与消费者成本见[时间回收报告](Midora-Logical-Canonical-Time-Implementation-2026-09-10.md)。下文原文档轮次的“不改代码”与 §3.1～3.3 是当时的授权/计划，不代表此项仍未实施。极端 Tick UI 防护现已实施并完成自动验证，用户已确认验收通过并授权提交、推送，见 §4.2；SMF 编码仍未实施，单独安排下一轮。

## 1. 本轮授权与基线

- 用户要求本轮先完成文档收尾；不实施产品代码，不提交、推送或本地发布，不使用 computer-use。
- 已有产品基线为 `560082e9`，已与远端同步。产品版本 `1.0.0-dev`；当前 writer 为 Project Format 3 / manifest schema 3，独立 presentation schema 2；继续读取 Format 1/2 和 presentation v1。
- 用户明确确认目前人工验收**大体全部通过**。这是总体使用反馈，不伪造为所有清单已逐项、全规模、全硬件验证通过。已有明确的阶段验收结果继续保留。
- 待 Midora 的易用性达到用户预期后，通过真实完整作品创作再集中进行一轮 Bug 修复和查漏补缺；不要求本轮重复全部人工清单，也不据此放行正式版本。
- `misc/user/midora-plan-pre-v1.0.txt` 是用户既有未跟踪草稿，本轮不读取或修改。

## 2. 当前状态台账

| 项目 | 当前结论 | 后续处理 |
| --- | --- | --- |
| 八阶段编辑与可视化扩展，含阶段 8 后续菜单、模式持久化及导航修订 | 已完成，用户验收通过；原先等待验收的文字只代表当时状态 | 不重开旧阶段；精细体验回归留到真实编曲 |
| 六阶段内存优化及独立 Pure MIDI 范围正确性修复 | 已完成，MEM-A/B/C 的用户结论保留，原两项编译失败已修复 | 保留历史反证与实测，不把历史失败当作当前阻塞 |
| Int64 完整诊断、条件分页、MIDI README 前 1000 条及准确省略数 | 已实施并提交于 `560082e9`；纳入本次大体验收通过 | Int32 暂缓决定已被后续实施取代，不再列为当前未完成项 |
| 诊断列表滚轮步进 | 同一提交已实施，纳入本次大体验收通过 | 保留原始滚轮与虚拟化测试记录 |
| F4 / Shift+F4 活动诊断导航 | 既有规格缺项；用户本轮明确暂缓 | 不删除 SRS 要求，不阻塞下一项工作，保留待办 |
| 成功 Logical canonical 的深度分页与内存约束 | LC-T1/T2/T3 已完成，用户确认验收通过并提交、推送 | 保留 §3.4 与各轮对照记录，不重开旧阶段 |
| 接近 Int64 Tick 上界的网格溢出 | UI/网格/Snap/坐标/瓦片防护已实施，自动验证通过，用户确认验收通过 | 见 §4.2；SMF 导出边界下一轮独立实施 |
| 正式发布 | 本轮未进行发布验收或授权发布 | 使用更新后的发布清单，完整作品及发行包另行验收 |

2026-09-09 用户进一步确认后续顺序：**Logical 编译结果内存优化 → 极端 Tick 溢出防护 → 新一轮需求**。前两项完成相应工程验证及验收后，再进入新功能实施；本次只更新排期，不启动任一项产品代码修改。

自动证据来自已保存的记录，不是本轮重新运行：Int64 改动 11 套回归及最后补测去重后 3182 个通过用例；随后滚轮修复 Desktop 434/434 通过。不得把重跑用例再次累加，也不得把 opt-in 大样本测试未执行时的普通 Passed 算作实测。

关联：[大需求计划](Midora-Major-Editing-and-Visualization-Expansion-Implementation-Plan-2026-08-31.md)、[六阶段内存计划](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md)、[Int64 验证](Midora-Int64-Diagnostics-and-Bounded-Readme-Verification.md)、[滚轮验证](Midora-Diagnostics-Wheel-Requirement-Trace.md)、[发布清单](Midora-Release-Checklist.md)。

## 3. 下一步：Logical 编译结果内存优化

§3.1～3.3 保留 2026-09-09 的问题与实施计划，当前完成情况见 §3.4。

### 3.1 为什么应在新功能批次之前做

源 Note 的分页不等于 Logical 编译结果已经分页。当前 `MidoraCompiler` 的 Logical 范围输出仍形成完整 `CanonicalMidiEvent[]`，由 `CanonicalCompiledResult` 持有；Pure MIDI 的分页 source 是另一条已经存在的表示路径。

阶段 5 已减少重复 Raw pattern、诊断物化、历史及无用保活，但实例 × SubVoice × 模板 × Loop 产生的正式 Logical 事件和有效编译修订仍可很大。这不是六阶段未完成，也不能将其描述成任意规模的全进程内存上限。后续大量新消费者和编辑功能若继续依赖完整数组，会增加未来迁移成本。

因此将此项排在文档收尾之后、下一批大型功能实施之前是合适的。本轮只确定工作顺序与约束，**没有启动代码实施**；具体存储结构、预算、阶段划分及回退门仍需下一轮先调查并形成设计记录。

### 3.2 建议覆盖面

1. 分开计量 Raw 模板共享、实例/context/source、展开、排序/同 tick 处理、范围恢复/硬结束、最终 canonical、缓存与旧修订，避免只把最终数组换成分页但峰值仍留在构建阶段。
2. 优先评估不可变值页、紧凑来源引用、有界排序/归并和可复用范围索引；小型项目可以保留等价快速路径，但不得维护第二套音乐语义。
3. 梳理全部正式消费者：后台/手动编译、Full/Incremental、诊断与统计、All Tracks 逻辑展开、播放/预览准备、MIDI Export、Audio Render。消费者不得为适配新接口重新全量物化结果。
4. 旧 canonical、增量缓存、Undo/Redo 相关 owner、在途取消及 Project 关闭必须具有明确的页所有权和释放时机。不能释放仍有合法读者的内容，也不能以裁剪有效历史或强制 GC 达成指标。
5. 文件、磁盘或资源预算不足时明确失败并保留最后完整结果；禁止截断事件、改变 overlap / FIFO / Loop / Mapping / 范围语义，或发布部分成功。

不主动优化或重写 BASS / WASAPI / Limiter / 音频调度；若正式消费者必须适配分页接口，只调整消费表示，保留原时序、零分配和资源边界。Project Format 1/2/3、presentation、Mapping ABI 和用户编辑行为保持不变；不得以仍处于 dev 为由破坏已冻结格式。

### 3.3 工程验证与交付要求

- 先冻结当前源码和测试输出作为比较基线；小规模逐事件、来源、顺序、fingerprint 精确 oracle，大规模流式比较并报告其覆盖范围，不能用抽样冒充全量等价。
- 覆盖独立/共享 Usage、Per-Note Isolation、Loop/Pre-Roll、Mapping/Envelope、多 Segment、冷启动状态恢复、提前终点、Pure/Logical 混合、失败诊断、Full/Incremental、Undo/Redo、Save/Open 和导出。
- 对小型创作、9KX2 跨类型粘贴、合法高展开倍率分别测冷/热编译、范围读取、首次消费者准备、交互延迟、峰 Private/WS、GC live/committed、累计 allocation、spill 及关闭释放。
- 沿用受守护串行重测：优先复用既有 8 GiB 进程树私有提交硬上限及至少 2 GiB 系统余量；不得为完成极端测试放任涨至 9 GiB 以上或使系统假死。
- 常驻预算、构建预算、有效历史/旧读者必要持有、磁盘上限必须分别列出；不承诺数据总量任意大时全进程仍固定内存。
- 阶段数量按依赖与实测结果决定，不机械重开八阶段或六阶段。工程侧承担广覆盖自动门，用户只做少量代表性验收；真实完整编曲的精细查漏仍按 §1 后置。

### 3.4 实施状态（2026-09-10）

后续补充：LC-T1/T2 后又完成独立 LC-T3 实验，用户已批准正式接入 96-byte canonical 热记录与完整来源 sidecar，并确认本轮验收通过。下列旧数组对照保留为历史数字，最新加固、复测与人工范围见 [LC-T3 记录](Midora-Logical-Canonical-Compact-Implementation-2026-09-10.md)。LC-T3 轮次没有提前执行极端 Tick 或 SMF；后续单独授权的 Tick 实施见 §4.2。

- 已完成从 Raw 发射、共享 pattern、有限外排、范围归并到不可变 Logical canonical 页的贯穿改造；内联与分页的正式查询明确区分，播放准备、MIDI/音频导出、All Tracks 不再要求大型 Logical 结果全量数组。
- 预算分别约束驻留页、工作缓冲、索引和 spill；取消/失败回滚、合法旧读者、关闭后回收有自动门。没有改变 Project Format、Mapping ABI、SRS 或音频语义。
- 初次内存交付的约 210 万事件受守护对照中，Private 采样峰从约 5.97 GB 降到 1.40 GB，但热 fresh Full 从 7.46 秒增至 27.86 秒。这是历史候选，不再作为当前速度。后续时间回收移除中间复制/发布扫描、优化有界排序与完整性校验、删除消费者无效分配；不提高子系统预算、不改音乐 hash 或完整来源。
- 同日三次新旧对照：高展开热 fresh Full 为旧数组 6.950 秒、本轮 7.692 秒（+10.7%）；Shared100k 为 4.642 秒、4.073 秒（−12.3%）。高展开 Private 峰中位数由约 5.43 GB 到 1.07 GB；该峰含三个合法结果和 oracle，不等于单结果大小，也不承诺任意输入固定内存。所有 min/max、单次波动和剩余成本见[时间报告](Midora-Logical-Canonical-Time-Implementation-2026-09-10.md)。
- 六类最终旧版对照、Compiler 完整回归及播放/导出/应用/桌面消费者门通过。初次交付时的 525 项 Compiler 记录留在[历史报告](Midora-Logical-Canonical-Memory-Verification-2026-09-10.md)；最新测试数量和未执行的 WPF/真实声卡场景见时间报告，不把历史/重跑用例重复计数。
- 初次交付时仍需少量人工验收；后续 LC-T3 已获用户验收通过并提交、推送。这是历史轮次的完成记录，不再将 Logical 优化列为待验收；Tick 与 SMF 的当前状态分别见 §4.2、§4.1。

## 4. 极端 Tick 网格溢出：风险说明

以下为 2026-09-09 的历史源码风险分析；2026-09-10 已完成独立 UI 防护实施与自动测试，最新状态见 §4.2。不得将下列“未修复”文字视为当前实施状态。

排期已确认：作为 Logical 编译结果内存优化之后的独立防护工作，在新一轮需求之前完成。用户明确要求考虑高 TPQN 极端工程，不能仅凭正常项目难以触及而略过；以下风险说明为修复前证据。

- 已确认：`ProjectTimelineGrid.GetNextGridTick` 的 fixed-step 路径执行 `checked(tick + step)`；首格计算也可能执行 `checked(lower + step)`。结果超过 `long.MaxValue = 9,223,372,036,854,775,807` 时抛 `OverflowException`，不会回绕成负数。
- UI 路径：`TimelineSurface.OnRender → DrawGrid → TimelineGridQuantization → ProjectTimelineGrid`。现有 `tick == long.MaxValue` 检查不能保护 `tick < long.MaxValue` 但下一格已越界的情况。正常 Bar 路径另有拍号/网格加法，不能以 fixed-step 的 guard 代替其完整审计。
- 当前 `OnRender` 没有捕获这类异常；主应用也未注册用于恢复该异常的 `DispatcherUnhandledException`。`App.OnStartup` 的 try/catch 不覆盖程序运行期间后续的 Render Dispatcher 消息。
- 因此，**如果运行中的 UI 绘制命中此路径，存在未处理异常终止进程的风险**。不能保证用户再平移/缩放回合法范围就能恢复：进程可能已经结束。被测试工具主动 catch 后再次调用合法坐标能成功，不等于正式 UI 有恢复机制。
- 触发依赖时间坐标逼近数值上界，而非音符数量、NPS 或百万级选择。正常编曲和已测大型 MIDI 的常见时间范围远离此边界；本轮没有证明普通操作会自然到达它，也没有给出发生频率。
- 当前风险应归为低触达概率的异常防御缺口，而不是已确认安全、可暂时忽略的视觉问题。后续防护应使绘制安全结束、时间计算受控失败且不回绕、不吞音乐错误；不能只用全局 catch 隐藏异常。
- 验证范围纳入既有合法 TPQN 上界附近的值与极端 Tick 组合，检查网格/拍号、首格与下一格、坐标换算、平移缩放、范围及 Snap 运算；同时验证普通规模性能和显示不回退。高 TPQN 会提高同一音乐时长对应的 Tick 数，但不等同于必然触发 Int64 溢出，本排期不扩大既有 TPQN 合法范围。
- 必须区分可表示范围内的正常计算、显示范围到达数值端点，以及正式音乐运算无法表示三种情况：合法输入保持准确，显示在端点安全终止，非法音乐运算明确拒绝且零部分发布。修复后再验证视图返回普通范围可继续操作，不能把当前无恢复处理写成已具备该能力。

源码依据：[网格算术](../src/midora-core/Midora.Domain/ProjectTimelineGrid.cs)、[TimelineSurface](../src/midora-desktop/Midora.Desktop.Presentation/Controls/TimelineSurface.cs)、[应用异常入口](../src/midora-desktop/Midora.Desktop/App.xaml.cs)。历史边界记录见 [阶段 8 来源模式与导航](Midora-Stage8-Onion-Source-Modes-and-Navigation-2026-09-08.md)。

### 4.1 后续定案：SMF delta 与文件大小边界（2026-09-10）

用户确认并授权更新 SRS；本次仍只做文档，不启动产品代码实施。完整需求、ADR、当前源码缺口及验证矩阵见 [SMF 导出边界设计](Midora-SMF-Export-Timing-Padding-and-Size-Limits-Architecture-Decisions.md)。

- 超过 `0x0FFFFFFF` 的事件间隔仅在导出编码时以空 Text Meta `FF 01 00` 分段，保持原事件 Tick/顺序/EOT；覆盖所有 Track 和最后事件到 EOT 的尾段。Compiler 不增加间隔扫描，canonical/播放/音频不包含占位。
- 单个 MTrk 数据区硬上限为 `0xFFFFFFFF = 4,294,967,295` 字节，不含 8 字节头，不是整个文件的大小上限。用户明确决定不做 MTrk 拆分；超限只使本次 MIDI 导出原子失败，编译不感知。
- 其他 MIDI 硬限制保持拒绝；提前安全计算占位成本，避免巨大 Tick 间隔触发无界占位循环/写盘。所有导出路径须支持取消、有界编码、准确错误归属；成功填充只给导出级 Info/README 汇总。
- 此项纳入既定的第二步“极端 Tick 防护”，**不改变 Logical 编译结果内存优化先行的顺序**。UI 网格溢出与 SMF 编码分别验证；不得为规避二者给 Project 添加 VLQ 级绝对 Tick 上限，也不得用全局 catch 或静默截断替代。
- 新测试必须替换旧“超长 delta 无占位失败”预期，并独立保留 payload/MTrk 超限、分页延迟失败、取消与多文件原子性测试；未实施前旧测试只能证明旧代码行为。

### 4.2 极端 Tick UI 防护实施（2026-09-10）

用户在 LC-T3 验收后单独授权实施“极端 Tick 溢出防护”。本轮只实施 UI/网格/Snap/坐标/瓦片与编辑手势的算术保护，§4.1 的 SMF 编码变更保持下一轮独立实施；没有修改 SRS、版本或 Project Format。

- 时间图使用宽整数计算真实小节末端，网格 Try 查询在没有可表示的下一格时正常结束。正式音乐运算仍严格拒绝超限，不以显示裁剪截短音符、Segment 或事件。
- 共享坐标、平移缩放、滚动条、整数事件画线、瓦片查询及超长 Note 像素边界已加固；Arrangement、三种钢琴卷帘、Velocity/Event/Parameter、Conductor、All Tracks 共用修复。输入失败清理手势并明确提示，不用全局 catch 隐藏绘制错误。
- 以已复现的网格/坐标、非空瓦片、超长音符失败为回归基线。六套最终完整回归合计 2830 项通过、0 项失败；普通网格 10,000 组与旧版一致，普通钢琴瓦片 420 块 BGRA 字节与旧版一致。未重新测真实声卡或 9KX2 整体性能，未使用 computer-use。
- 自动验证完成，用户已确认本轮验收通过，并单独授权提交、推送；未授权本地发布。具体改动、测试证据、微基准波动、浮点栅格剩余边界及三条人工清单见[实施与验证记录](Midora-Extreme-Tick-Overflow-Protection-Implementation-2026-09-10.md)。

### 4.3 SMF 编码边界实施（2026-09-10）

用户在极端 Tick 防护验收后单独授权实施 MIDI 导出编码边界。本轮不更改 SRS、Compiler、音频、版本或 Project Format。

- 超长 delta 包括 EOT 尾段统一以空 Text Meta 分段；每个 MTrk 写记录前预算，超过 `0xFFFFFFFF` 数据字节原子失败，不拆分。
- resident/paged 均为正式流式编码，填充块 56 KiB，payload 写入块最多 64 KiB；UI 不承担同步编码。结构化错误、取消、清理和已有文件保护贯通全部模式。
- 填充汇总独立于编译诊断，README 关闭仍在结果窗口可见。自动回归与计数流 4 GiB 边界已通过；不能由此声称已验证真实 4 GiB 磁盘写入。
- 待本轮人工验收；未提交、推送或本地发布。人工清单、测试和时间性能范围见[SMF 实施记录](Midora-SMF-Encoding-Boundaries-Implementation-2026-09-10.md)。

## 5. 本轮文档验证（2026-09-09 历史记录）

- 同步 17 份既有 Markdown，新增本台账；没有修改 `src`、`eng`、脚本、schema/descriptor、版本源或发布产物。SRS 仅将 INV-091 的过期 presentation writer 版本同步到既有 INV-116，不新增语义。
- `git diff --check` 通过；新增/修改文字中的 37 个本地文件链接均可解析，0 个缺失目标。历史测试结果保留，不重新计数或改写失败记录。
- 本轮未构建、未运行产品测试、未复现真实窗口崩溃，也未提交、推送或发布。极端 Tick 结论的证据层级是源码调用链分析。
