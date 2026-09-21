# A4a：事件友好数值与辅助阶梯线

状态：原 A4a 已获用户“大体全部验收通过”（2026-09-17）；后续 Lines 优化也已获明确验收通过，按用户新授权归档提交并推送。本文保留 2026-09-15 实施和测试记录；辅助线排除范围／会话开关已被后续明确修订取代，当前范围、程序级开关、样例修复与逻辑参数约束强化延期见 [验收后修订](Midora-A4a-Acceptance-Followup-2026-09-17.md)。实施基线提交 `59bf911`。依据：[任务分解](Midora-Next-Development-Requirements-2026-09-11/execution/03-Lane-Tabs-and-Event-Display.md) 的 T-VAL-01～06、T-STEP-01～06，以及 D-VAL01.a～d、D-STEP01～04 的原用户确认。

## Requirement trace

- 输入：当前 owner 的正式离散事件/参数点，不改变 Project Source Data 的值域。
- 显示域：只对 CC10、CC71～78 使用 `display = raw - 64`。图形、列表、Properties、适用 Initial State、Batch Edit/Batch Create 使用该显示域；编码回 raw 只执行一次。普通移动 delta、比例因子不做偏移。Logical Parameter、Pitch Bend 的既有各自契约不变。
- 正式输出：仍为原始 MIDI 值，Mapping ABI v3、Compiler、canonical、持久化、音频与 MIDI 导出均不改变。工具表达式与 Mapping 表达式明确是不同数值层。
- 辅助线：自身 lane 显式点的水平保持和竖跳，可恢复自身前驱；无前驱不补线，不合并相邻 Segment、共享 Root/Usage 或 Initial State。保持线不参与命中/选择/编辑。
- 边界：Velocity、Inst.、Bank/Program、opaque、CC120～127 与非状态命令不连线；既有 Value Curve/Envelope 不改。crop 外内容仍按已有弱化规则显示。
- 并发/资源：保留已有点/选择 raster；辅助线独立缓存，冷查询在有界后台任务内。线层不增加整 lane 点数组或第二份全量 ID 索引；同 Tick 顺序查询复用 owner 已有 ordinal directory，首次准备可取消。结果绑定 immutable revision，取消、换 owner、关 Project 后旧结果不得发布。
- 失败：非法输入或非有限表达式结果沿用失败原子性；旧工具数值契约 preset 不静默重解释。绘制失败不得变更音乐数据。
- 持久化：不改 Project Format 4；工具数值契约单独版本化。线开关仅 workspace session，默认打开，不进 Undo/Modified。
- 非目标：A4b、音频优化、Mapping 值域/ABI 变更、曲线插值、跨轨道状态推导、发布。

## 验证计划

1. 九个 CC 的全部 128 个值双向转换；非白名单完全不变。
2. 小/大选择 Batch、Generator 的 direct/delta/factor/expression、迭代反馈、碰撞、取消、Undo/Redo；Mapping raw 对照。
3. 三宿主 axis/list/Properties/Initial State；混合 CC 属性按各对象 target 转换，不以第一项值域替代全部对象。
4. 阶梯线 first/last/same-tick/order、前驱、空区、crop、模板边界、排除 target、局部编辑/Undo、toggle 与输入透明。
5. 后台队列/缓存上限、过期结果、密集事件和 DPI；记录实际运行证据，不用静态构建代替人工视觉验收。

## 1. 实施内容与代码入口

### 1.1 R12：编辑显示域

- `MidiEditingValueDomain` 集中定义 CC10、CC71～78 的偏移与反变换；全体 CC 的其他值域不变。图形纵轴/坐标、List、Properties、适用 Initial/Reset State 及工具读数统一。
- Batch Edit 的小选择与有界大选择路径都在显示域执行赋值、加法、比例和表达式。Generator 的初始值、前次反馈、本次值、Clamp 均处在显示域，写回正式事件时编码一次。
- Direct 多选 Properties 按每条记录的最终 CC target 编码，不以第一条记录的偏移套用全部对象。混合 CC 的共同值也按显示值判断；例如 CC11 raw32 与 CC74 raw96 都显示32。
- CC Value Curve 的点 Properties 输入/输出采用同一显示值转换，但 Curve 的插值、Envelope、Mapping Function、内置 Mapping Step、共享 accumulator 和 Context 仍沿用原音乐语义。
- Help 给出明确对照：CC10 raw96/display32，经工具 `=p0*0.5` 得 raw80/display16；经 Project Mapping 半值运算得 raw48/display−16。不额外在普通字段旁显示 raw 数字。

主要文件：`MidiEditingValueDomain.cs`、Application 下对应 Batch/Generator/Direct Properties commands；Desktop 的 `PresentationModels.cs`、`ObjectPropertiesProjection.cs`、`ObjectPropertiesBatchSummary.cs`、`TimelineObjectListSource.cs`、`MidiStateEntryDialog.xaml.cs` 和三个 Help。

### 1.2 工具预设的契约边界

- Event Batch / Event Generator 的 expression profile 和数值契约提升到 v2；Note Batch / Note Generator / Note Split 保持 v1。Project Mapping ABI v3 不变。
- Batch preset 新 writer 使用 schema2，补齐 profile 和 numeric contract。已有合法 Note schema1 仍经验证读取；旧 Event schema1/v1 不会按新语义静默执行。
- 不兼容、损坏或不可读取的预设文件保留原位，预设窗口明确显示本次未加载的数量；不删除用户文件。不自动承诺旧 Event 预设的结果兼容。
- 共用 `ToolPresetJson` 限制单文件256 KiB、JSON深度16，拒绝重复/未知字段及不完整数据，表达式重新验证；保存仍使用临时文件和原子发布。
- `.midora` Project Format 4、正式 raw 数据、旧项目读取和已有 protobuf/JSON 音乐合同均不升级。

### 1.3 R29：独立辅助阶梯线

- 三宿主共用 `EventStepSignalSources` → `TimelineStepSignalSource` → `TimelineStepSignalRasterizer` → `TimelineSurface.StepSignal`。保持水平线，在变化 Tick 画竖跳；线层在点层下方，不产生命中对象。
- 使用当前 owner/target 的显式记录与严格前驱，不查其他 Track、Root/Usage、Initial State 或 Reset Default。无前驱时首个显式点前留空。
- Direct 同 Tick 使用正式 Order；Template/Logical 使用正式集合 ordinal，不以稳定 ID 代替。高密度时按设备像素列聚合 first/last/min/max，仅缩减图形，不删除正式记录。
- CC0/32、CC6/38、CC96～101、CC120～127 这类 Bank/协议/命令目标不机械连线；Velocity、Inst.、Program、opaque 排除。SubVoice 的 typed RPN/NRPN、Pitch Bend Range 等标量状态仍可连线。已有 Curve/Envelope 绘制不被替换。
- Segment crop 外按既有弱化方式绘制，SubVoice 线裁到模板末尾；不跨不相关 owner 接线，也不把界外线当作实际音频承诺。
- 活动 Lane 顶部新增默认启用的 `Lines` 开关。仅对适用 Lane 出现；当前会话按 owner/target 保留开关，隐藏后重开 Lane 不重置；不进入 Project、Modified、Undo 或 presentation 文件。

### 1.4 后台与有限内存

| 部分 | 本轮边界 |
|---|---|
| 像素缓存 | 复用现有共享256 MiB硬上限，不新开无限位图池 |
| 栅格工作 | 复用现有有界调度器；点层先调度，线层为普通优先级 |
| 辅助线准备 | 单 Surface 待准备队列上限64，复用已有单 Surface 指纹准备并发门 |
| 共享摘要 | 上限16,384条；仅标量摘要、身份与指纹，不持有 Project/source delegate |
| 单 immutable source | 范围摘要最多1,024条，范围元数据指纹最多1,024条 |
| 栅格输入 | 每设备列有界摘要，不建立全 lane 点数组 |
| Direct overlay 查询 | 新增同顺序流式范围枚举，临时空间随树高，不再为线查询物化所有命中记录 |
| 关闭/换投影 | 取消独立线 token；清理本 Surface 在途键；投影和 target 身份校验后才允许完成回调刷新 |

范围指纹包括当前范围、必要前驱和影响像素的正式首末顺序。改变别处的点不以整个 Segment generation 无条件污染所有线瓦片；改变前驱则应更新后续保持线。

冷查询不是“恒定时间”：第一次需要扫描候选页建立摘要，稀疏区首次找前驱也可能读取相关前缀。后续请求复用有界摘要；同 Tick 密集数据可触发 owner 既有 ordinal directory 的冷准备。以上工作均安排在可取消后台，不宣称冷路径完全不读大数据。

## 2. 自动验证结果

环境：Windows x64、.NET SDK10.0.400 / runtime10.0.11，Release。以下 TRX 位于未跟踪的 `.tmp/test-results/a4a/`。测试中创建的项目均为独立 fixture；未修改用户 MIDI/项目，也未使用 computer-use 或本地发布。

| 测试 | 结果 | 证据 |
|---|---|---|
| Application 完整套件 | 1249/1249 | `a4a-application.trx` |
| 最终数值域专项 | 10/10 | `a4a-value-final.trx`；覆盖所有128 CC×128合法值、大小两条命令路径、Undo/Redo、Generator初值与反馈、混合CC Properties |
| Compiler 完整套件 | 573/573 | `a4a-compiler-final.trx`；包括 raw Mapping 对照、事件profile v2及既有确定性回归 |
| Persistence 完整套件 | 239/239 | `a4a-persistence.trx`；原格式/golden继续通过 |
| MIDI Export 完整套件 | 88/88 | `a4a-midi-export.trx` |
| Presentation 最终完整套件 | 545/545 | `a4a-presentation-final.trx`；包括线栅格、前驱、取消、缓存上限和正式顺序 |
| Desktop 完整套件 | 475/475 | `a4a-desktop-final.trx` |
| 最终显示/阶梯数据源专项 | 13/13 | `a4a-focused-final.trx`；包括最后补充的两个十万同Tick正式顺序用例 |
| 真实9KX2独立探针 | 1/1 | `a4a-9kx2.trx`；显式设置样本环境变量后运行，见§3.2 |

专项和完整套件有重复，不能简单相加成独立测试数。普通 Desktop 套件内的 opt-in 样本测试在未设置环境变量时提前返回；实际读取9KX2的证据仅为独立 `a4a-9kx2.trx`，不把普通运行中的绿色结果当作跑过大样本。

实际 WPF 接线由 `WpfInteractionRegressionTests.ComboAndScrollBarThemesProvideDedicatedTemplatesAndChevronGeometry` 的现有单 Application/STA 测试调用：加载 MainWindow BAML、实际 DataTemplate、主题和 handlers，使用不可见 HwndSource 检查三宿主 Lines 接线、默认/关闭/重开、选择不变和 Velocity 排除。最终该综合测试59.19秒通过。另以100/125/150/200%受控 DPI、640×32紧凑头部及长名称检查新增按钮不越界；这不是人工视觉评审或真实全屏帧率测试。

### 2.1 本轮发现并已处理的失败

1. Template/Logical 暖请求最初每次分配约2.19 MiB，未过2 MiB门：范围指纹反复建立页引用元数据列表。增加 immutable source 的有界范围指纹复用后，降到每次约0.32 MiB；没有放宽门。
2. 原 Properties 测试把 CC11 raw32 / CC74 raw96 视为 Mixed；新显示域二者都为32，因此更新该预期，并继续检查提交/Undo结果。
3. 新增 Curve Properties 测试最初读取旧 SubVoice root。多字段原子命令会替换根，测试改为读取当前 Project root；最终字段与 Undo 断言通过。
4. 实施中补齐线专用 target 投影身份、取消 token 和完成回调判定，避免换 Lane 后错误命中缓存，或已算完却不触发重新绘制。

### 2.2 可复现命令

从仓库根目录运行。完整套件的公共参数为 `-c Release --no-restore --logger "trx;LogFileName=<表中名称>" --results-directory .tmp/test-results/a4a --verbosity minimal`；项目分别为：

```text
src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj
src/midora-core/Midora.Compiler.Tests/Midora.Compiler.Tests.csproj
src/midora-core/Midora.Persistence.Tests/Midora.Persistence.Tests.csproj
src/midora-core/Midora.MidiExport.Tests/Midora.MidiExport.Tests.csproj
src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj
src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj
```

例如最终显示专项：

```powershell
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~EventStepSignalSourceTests|FullyQualifiedName~EventDisplayProjectionTests" --logger "trx;LogFileName=a4a-focused-final.trx" --results-directory .tmp/test-results/a4a --verbosity minimal
```

9KX2 探针需在该次测试进程设置 `MIDORA_A4A_SAMPLE_MIDI=D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`，过滤 `FullyQualifiedName~OptInImportedSampleKeepsEventsAndMillionNoteSelectionIndependent`，使用独立 TRX 名称。

## 3. 性能证据与解释

### 3.1 百万事件与同 Tick

以下为最终显示专项实测。每类创建100万个事件/点；一个256px瓦片覆盖全部，再做10次相同指纹＋栅格请求。暖测仍重新生成像素缓冲，不是已完成 bitmap cache hit。分配为当前线程计数；工作集包含 fixture/测试宿主，不能算作“线层独占内存”。构建/其他测试存在并行，数字是本机探针，不是发布性能承诺。

| 数据源 | 冷耗时 | 冷分配 | 暖p50 / 最大 | 暖每次分配 | 工作集 |
|---|---:|---:|---:|---:|---:|
| Direct MIDI | 634.87ms | 0.69MiB | 1.34 / 5.76ms | 0.32MiB | 716.6MiB |
| Template/SubVoice | 460.45ms | 9.78MiB | 3.59 / 4.45ms | 0.32MiB | 1399.4MiB |
| Logical Parameter | 311.40ms | 8.65MiB | 1.17 / 5.48ms | 0.32MiB | 1137.1MiB |

测试门：冷分配≤32MiB、暖每次≤2MiB、单冷请求≤30秒、工作集低于8GiB即在用户9GiB警戒线前停止。没有触发内存警戒。

另测10万个同 Tick 点：Template 集合顺序刻意与ID顺序相反；Template / Logical 的最终状态均正确。含既有 ordinal directory 冷准备，分别492.24 / 708.93ms，各分配12.69MiB；取消令牌设15秒。不能把该结果推断为任意规模同Tick数据的固定内存耗用。

### 3.2 真实样本9KX2

- 文件：`D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`，只读导入28.52秒。
- MIDI Out #23，Tick `[168816,193536)`、Key `[0,128)`，选中1,382,908个音符，范围物化1733.28ms。
- 在独立内存项目该 Segment 增加 Tick168960 CC10 raw96，然后工具 `=p0*0.5` 得raw80，Undo回raw96；新的辅助线源分别对应正确的新旧值。
- 两个修订各16次局部不同位置/缩放的 signal fingerprint＋raster：170.05 / 164.92ms；工作集390.7 / 390.2MiB。
- 未播放音频、未保存样本。这证明大音符选区与当前事件线源的独立性和正确修订更新，不证明真实 UI 在百万事件下的帧率。

## 4. 尚未穷尽的验证与交付边界

- 任务文档 AUTO-VAL/AUTO-STEP 的原矩阵还包含全部随机编辑序列、每种页I/O失败、revision竞争、长会话反复关开及完整缓存持有者归因；本轮没有穷尽，不把原矩阵一律标记通过。
- 未对真实可见 WPF 窗口采集冷/暖 pan、zoom、edit/Undo 的p95/p99、音频并行竞争、native bitmap/进程长期峰值与关闭后GC保留图；需要后续性能验收补充，不能由本轮栅格探针替代。
- 缓存上限、token取消与实际三宿主接线已自动测；所有target×全部DPI×实际鼠标交互的视觉组合仍由人工验收抽查。新辅助层不会承诺冷场景即时出现，点层保留独立工作路径。
- 正式音乐数据、编译、导出与Mapping边界回归已跑；未做音频听感回归，本轮不改变音频实现。
- 未实施A4b及其他后续主题；未提交、推送、本地发布。

## 5. 人工验收

沿用[任务文档§6的UAT-A4a六项](Midora-Next-Development-Requirements-2026-09-11/execution/03-Lane-Tabs-and-Event-Display.md)：

1. **VAL-01** 三宿主适用入口的CC10/71～78显示范围−64～63，其余target不变。
2. **VAL-02** 工具display32半值为16，Project Mapping对照为−16；Help能够解释差别。
3. **VAL-03** 图形/List/Properties/Batch Create数值一致，取消、Undo/Redo无跳值。
4. **STEP-01** 观察自身前驱、无前驱、单点、同Tick密集点的保持/竖跳。
5. **STEP-02** crop外弱化、模板结束及相邻owner不串线；Velocity/Inst./Bank-PC/命令/opaque和既有曲线不受影响。
6. **STEP-03** Lines开关不抢焦点/选择；点拖动、缩放、编辑、Undo/换Tab后线更新正确，大样例无明显新卡顿。
