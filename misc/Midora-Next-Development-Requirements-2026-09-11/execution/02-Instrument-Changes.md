# 执行计划 02：乐器变化点与统一音色选择器

日期：2026-09-15。范围：R27（P0）；交付切片 A2a／A2b。当前状态：**A2a、A2b 全编辑／List 及 Inst. 交互返修人工通过** 。完整工程门尚未全部关闭，最新已跑、失败后复测及未跑项见 [A2b 实施记录](../../Midora-A2b-Instrument-Changes-and-Lane-Tabs-Implementation-2026-09-14.md)，不得将本状态理解为全部压力矩阵已通过。

总入口：[执行计划索引](00-Execution-Plan.md)。需求依据：[需求 02](../02-Instrument-Changes-and-Lanes.md)、[唯一问答入口 04](../04-Decisions-and-Preparation.md)、[Q1／Q2 汇总 05](../05-Decision-Summary-and-Q2-2026-09-14.md)。本文件是已确认需求的工程拆解和阶段状态记录，不是新的答卷、SRS 或 ADR；不改变用户原答。实施证据分别在 §7／8 和对应报告，不以规划取代验证。

## 1. 交付边界与 requirement trace

| 维度 | 已确认边界 |
|---|---|
| 输入 | MIDI Segment／SubVoice 正式 raw 事件、冻结 owner revision／formal order、显式创建动作、有效 Catalog resolver、音色选择器本地 draft、程序级音频配置 |
| 正式输出 | raw 成员的一次原子 Project command；稳定包装及成员关联是持久编辑组织数据；只有 canonical 是正式消费者音乐输入 |
| 身份／顺序 | 包装与成员都用稳定 ID；Pure MIDI 包装关联 CC0／CC32／PC 三条，SubVoice 包装关联完整 Bank Event／Program Event 两条；同 Tick 创建按 Bank→PC，在本 Track 对应 NoteOn 前；不追溯影响共享 Root 中正式顺序更早的 Track |
| 导入 | MIDI 导入不转换、发现或投影包装，Inst. 初始为空；Bank／PC 仍经 raw Lane 编辑。用户随后显式添加包装可以命中既有 raw，仍须执行精确碰撞归并 |
| 持久化 | 不复制第二份音乐数值、不新增 Compiler 的 InstrumentChange 音乐事件、不放入可丢弃 presentation；必要格式扩展已获原则批准，具体版本／schema 在实施时设计，不能塞入冻结旧 schema |
| 运行时 | Catalog 名称、试听状态、请求所有权不进入 Project／canonical；自动试听开关、Key、Velocity、时长属于程序偏好；试听 Channel Mode 只影响试听 |
| 失败／诊断 | 取消、非法目标、失效 revision、关联不合法、资源或写入失败均零部分发布；名称缺失数值回退；无 SoundFont 只禁试听，不禁止合法编辑 |
| 非目标 | 不做导入 PC 投影、任意 raw 选择自动组合、当前有效音色跨轨状态显示、Project Global Initial State 选择器、三字段表达式批改、多实例剪贴板或音频后端整体重写 |

需求已确认不等于现行 SRS 已同步。实施前 T-IN-01 必须核对并正式承接：SRS §8.55.1／§18.4.2 的旧“无名称、Program 1～128”口径；§8.50.2／§18.4.5 的派生 Lane 与可选 Mapping owner 边界；稳定编辑分组的 source／持久化契约。沿用 §8.25／§8.55.2／§8.59 的字段继承和独立 raw 语义、§8.52 的身份、§18.2.7～8 的分页与列表、§20.2／§20.3.15／§20.4.11 的焦点／选择／事务规则，以及 INV-009／010／018／021～026／030／047／050／075／079／088／092／095／096／101。若实际设计确实改变已批准工作流或兼容承诺，再回 04 记录具体问题；不要为内部类名、页长或版本号重新开问卷。

## 2. 切片与工程依赖

| 切片 | 工程范围 | 人工批次 | 不能据此声称完成的范围 |
|---|---|---|---|
| A2a | T-IN-01～07；T-IN-10 中适用的首次集成门 | UAT-A2a | 未交付全包装编辑、List 与大规模包装投影，不能称 R27 完成 |
| A2b | T-IN-08～10，并与 R28 集成 | UAT-A2b，和 Lane Tabs 合并一轮 | 不要求为文件或内部子任务再安排一轮人工验收 |

A2a 是功能面较小的垂直切片，不是暂时允许破坏 source 的版本。从首次开放创建开始，所有仍可到达的 raw 编辑、父 owner 编辑／复制／删除／Split／转换及 Undo／Redo 都必须维护关联，不能先发布悬空包装、静默丢数据，再等 A2b 补救。可以把尚未完成的新增包装专属批量入口留到 A2b，但不能削去既有合法 raw 编辑能力以规避维护责任。

R28 的新 Tab 壳和摘要可与本专题后段并行；A2a 不依赖 R12／R29（P2）、R06／R07（P2）或 R01（P2）。A2b 使用 [计划 03](03-Lane-Tabs-and-Event-Display.md)的稳定 target／摘要合同。R18 的右键替换留给 A3，不将它偷升为 A2a 前置；接口应能接入已确认共享 Draw＋数值绘线形态组，Inst. 始终不沿线批量创建。

### 2.1 进入实施时重新核实的代码入口

以下是原需求文档的调查定位，不是可直接复用或已测试通过的承诺：`InstrumentCatalogResolver.cs`、`InstrumentCatalog.cs`、`ObjectPropertiesProjection.cs`、`PresentationModels.cs`、`ProjectSubVoiceEventLaneEditCommands.cs`、`TimelineObjectListSource.cs`、`PianoKeyboardSurface.cs`、`DesktopSessionController.cs`、`PlaybackController.cs`、音频 Worker 的 `Program.cs`。基线与完整路径见需求 02 §2。

已确认的静态风险是目标索引可能全源分组并按 ID 排同 Tick，以及既有裸音高 audition 入口未直接表达完整 preset 与安全链；都不能凭接口名称认定符合新要求。正式动手前核对当前源码、测试与 `git status`，保留用户已有改动。

## 3. 稳定工程任务

各任务的“完成证据”均为未来必须产出的材料；当前未产生。每项完成须同时满足自动正确性、有界性能、失败门，不能只勾 UI 可用。

### T-IN-01 — 冻结 source、顺序、格式与试听合同

- 状态：合同／ADR／SRS 已接入；性能门部分验证，见 §7；切片：A2a。依据：R27；D-IN01.a/b/c、D-IN02.a/b/c、D-IN03.a/b/c、D-IN04、D-IN05.a～e。
- 依赖：无；读取上述需求／SRS、当前相关源码和测试后开始。
- 工作：形成实现期 requirement trace／ADR／规格承接清单；列完整包装、成员关系、最终态校验、碰撞、同 Tick order、格式兼容、试听 owner 与受控 canonical 计划合同；冻结源码调查和性能基线场景。
- 非目标：不重新开放导入投影或 Mapping 音乐事件方案；不提前在本计划写死新 Project Format／协议版本。
- 完成证据：R/D→模型／命令／持久化／消费者／测试定位表，格式可表达性结论，试听停止状态表，基线测量记录与预算依据。
- 门：正确性须无第二份 Bank／PC 音乐事实；性能须明确包装、页、标签、在途请求、试听资源的上限与释放点，不能仅写“高性能”；失败须逐项列出取消／revision race／磁盘与原生失败的零发布路径。任一契约缺口未解释不得开放正式创建。

### T-IN-02 — 稳定关联与全事务最终态维护

- 状态：实现及关联专项已接入，完整覆盖边界见 §7；切片：A2a。依据：R27；D-IN01.a、D-IN02.a/b；SRS §8.52、INV-047／077／096。
- 依赖：T-IN-01。
- 工作：建立包装稳定 ID 与成员 ID 关联；把关联维护放进正式原子事务／change set，覆盖所有已开放 raw 和 owner 命令。值变且同 owner／同 Tick／target 完整时保留；最终结构破坏才解组，剩余 raw 保留，不补成员。整组移动不能中途解组；复制／转换／Split／删除 owner 不留跨 owner 或悬空关联。
- 非目标：不靠 tick、ordinal、Catalog 名称、ID 排序恢复包装；不在显示刷新时修复 source；不新增从任意 raw 选择组合的入口。
- 完成证据：两种 owner 的不变量测试、既有命令拦截点清单、结构破坏／完整保留／关联 remap golden、一次 Undo 与 source trace 对照。
- 门：正确性覆盖碰撞成员被覆盖、值改、部分移删、整体变换最终态及冻结 formal order；性能只查询实际修改键与相关关联，不扫描无关巨型 owner；失败注入任一成员验证错误／旧 revision 后 raw 与包装均不发布。此项不得移到 A2b 才补。

### T-IN-03 — 持久编辑组织数据与安全重开

- 状态：Format 4 已接入，Persistence 全套通过，A2a 人工通过；切片：A2a。依据：R27；D-IN01.a/b/c、D-IN03.a；INV-030／088／092。
- 依赖：T-IN-01／02。
- 工作：按已完成格式设计建立严格 DTO／schema／descriptor／reader／writer／migration；保存包装身份与成员关联，不保存重复音乐值；保留既有 Format 1／2／3 音乐读取。旧项目无包装时维持无包装；普通 Save／Save Copy 接入既有确认、永久原字节副本和原子发布机制。
- 非目标：不承诺旧软件读新 source；不以 presentation 损坏回退策略默默丢正式编辑包装；不原地篡改旧 schema／golden。
- 完成证据：新旧格式 golden／重开对照、未知／重复字段和失效成员拒绝测试、保存前后 raw／包装 ID／顺序对照、Save Copy 与迁移保护证据。
- 门：正确性为完整 round-trip 与 deterministic 保存，导入 raw 未改；性能为分页／流式保存，不能建立第二份完整事件图；失败覆盖取消、备份冲突、来源 identity 改变、磁盘不足、临时包验证失败，来源和原会话保持安全。

### T-IN-04 — 统一虚拟化音色选择器

- 状态：实现及 WPF 专项已接入，A2a 人工通过；切片：A2a。依据：R27；D-IN04、D-IN05.e；INV-075／101。
- 依赖：T-IN-01；提交适配接 T-IN-02／03。
- 工作：一个可复用 draft 选择器提供 Bank／Program 值＋名称列表、MSB／LSB／Program 独立数值框、键盘和试听控件；沿用 resolver 优先级与数值 fallback。所有相关 Program 数值入口明确为 0～127；列表选择与合法数值即时同步，统一 OK／Cancel／Enter／Esc／焦点恢复。
- 非目标：不穷举全部三元组，不自动 Scan Presets、不读 sample、不把 Catalog 名称当 preset 一定可发声的保证。
- 完成证据：列表／输入／resolver 同步测试、无名称及 Catalog 缺失／损坏／disabled／orphan／override 用例、控件复用及全部 Program 入口清单。
- 门：正确性覆盖 0／127、合法未知值和 cancel draft；性能测冷／热目录、长名称、快速搜索／换选，只创建可见行且无所有组合的字符串数组；失败为旧 Catalog 请求不可覆盖新选择、非法 draft 不提交、浏览与取消不改 Project Modified。

### T-IN-05 — Initial State 合并入口与旧 raw 能力保全

- 状态：实现及继承／raw 回归已接入，A2a 人工通过；切片：A2a。依据：R27；D-IN03.a/b/c、D-IN04；SRS §8.25、§8.55.2、§8.59、§18.4.3。
- 依赖：T-IN-02／04。
- 工作：Event Instrument 全局及 SubVoice Initial State 统一一行 selector；高级区保留三字段继承／覆盖，仅选完整 preset 明确覆盖三字段。保留 SubVoice 高级 raw Bank／PC、部分字段、Bank.MSB／Bank.LSB／Program.Value Mapping 维护入口。
- 非目标：不改 Project Global Initial State；不生成 tick 0 点、不改 Template Length；不以打开窗口补 0，也不静默重建被删除的 Mapping owner。
- 完成证据：部分继承／override 的前后 DTO 对照、单字段修改和全 preset 提交测试、旧独立 Bank／PC／Mapping 项目可编辑用例、一次 Undo 证据。
- 门：正确性覆盖仅 MSB／仅 LSB／仅 PC、混合继承及未编辑字段原样；性能不得为显示一行扫描全模板或查询整曲活动音色；失败为无效输入／Cancel／owner 删除／旧 revision 不改变原 override，试听失败不阻断合法编辑。

### T-IN-06 — 独立 preset audition 与停止所有权

- 状态：实现、canonical／所有权／队列测试已接入，A2a 人工通过；开发方真实后端专项未跑。切片：A2a。依据：R27；D-IN05.a～e；INV-009／018／021～026／079／082。
- 依赖：T-IN-01／04；音频配置与现有预览合同核实完成。
- 工作：通过受控正式 canonical 试听计划建立干净控制器状态；自动默认 500ms、首次 Key60／Velocity100、可关、快速换选 latest-wins；手按键使用 held gate，释放结束 gate。开关／Key／Velocity／时长记程序偏好；Melodic／Percussion 只改试听，以已知目标初始模式为初值。冻结 SoundFonts／映射／voices，Preparing 预载，经 Master→Limiter；显式 owner 管理停止和释放。
- 非目标：不套当前时间线 Expression／PB／Mapping、不显示“当前有效音色”、不写 Root mode／项目 SysEx；不靠 UI 定时器或 Thread.Sleep 承担正式声音时序，不抢停正常 Project 播放。
- 完成证据：请求 generation／owner 状态表、500ms 调度与 held gate 自动测试、冻结配置／预载／Master-Limiter 路径、偏好重开、原生失败与资源释放记录；真实声音另列人工。
- 门：正确性覆盖快速切换、手按释放、旧完成迟到、选择／编辑／确认／取消／关闭／导航先停止本弹窗试听；性能检查活动音频线程零托管分配、有界在途请求／stream／buffer 和长时间重复开启关闭；失败覆盖无 SoundFont、设备／预载／原生错误，显示试听不可用并允许合法编辑，其他任务所有权不受影响。

### T-IN-07 — 单点 Inst. 与统一 Properties 垂直切片

- 状态：两种宿主单点入口已接入，A2a 人工通过；切片：A2a。依据：R27；D-IN01.c、D-IN02.b、D-IN04、D-LANE01.b、D-UI01.d。
- 依赖：T-IN-02～06；T-IN-03 重开门必须完成后才交付持久创建。
- 工作：MIDI Segment／SubVoice 的 Vel. 后提供常驻 Inst.，固定 y 点、Tick 横轴、带边框值／名称标签；Logical Segment 不新增 Inst.。Draw 单点创建／选择，双击与 Properties 复用选择器；OK 原子创建或编辑成员并选择结果，Cancel 不分配正式内容。提供有界标签布局及正式 source 命中。
- 非目标：不做沿线生成大量选择器、不画数值阶梯；不因没有 R28 完整普通 Tabs 而推迟 P0 selector／Initial State；未实现的新增包装批量入口不冒充可用。
- 完成证据：两宿主单点创建／编辑／取消／Undo／重开场景，导入 Inst. 空视图，原始 Lane 可访问，本 Track 同 Tick NoteOn 与跨 Track 顺序 golden。
- 门：正确性为 wrapper 与 raw 同一最终事务、标签非音乐事实；性能为可见范围／有限标签，不建每点 WPF Control；失败覆盖窗体关闭／目标失效／源 revision 改变，焦点回有效来源、没有半组或后台复活。A2a 开放的全部入口仍须满足 T-IN-02。

### T-IN-08 — 包装全编辑能力与既有工具集成

- 状态：功能已实施、专项回归通过，待人工验收；扩大压力门见 §8。切片：A2b。依据：R27；D-IN02.a/b/c；SRS §20.3.15、§20.4.11／17／18。
- 依赖：T-IN-02／03／07；使用现有正式工具，不依赖 A3 新浮动按钮完成。
- 工作：覆盖 Copy／Cut／Paste／Delete、水平 Move／Ctrl 复制拖动、水平 Flip、Scale、Quantize、多选 Properties；冻结完整包装选择和 formal order，成员碰撞按正式 target 归并。多选 Mixed 字段显式启用并可还原；混合对象走显式类型子菜单，保留未处理选择与完整 Undo／Redo。补齐父 owner Split／转换／复制路径的端到端 UI 证据。
- 非目标：不做纵移／移调／Note Split-Join，不把单 Point Value Batch 直接套三元组，不新增三字段表达式；父 Segment Split 正确性与“不支持包装 Note Split”不是同一概念。
- 完成证据：每个批准动作×两种 owner 的覆盖表、跨页／大选择 golden、碰撞幸存者与选择对照、旧 raw 能力回归。
- 门：正确性为整组事务最终态、来源 trace 和确定性结果；性能沿用有界 detached transaction／compact Undo，不全量 HashSet 或复制两份对象图；失败覆盖取消／checked Tick 溢出／非法 target／资源超限／revision race，源、选择及 History 零部分发布。

### T-IN-09 — 包装 List 投影、选择去重与密集显示

- 状态：功能已实施、List／真实 WPF 集成通过，待人工验收。切片：A2b。依据：R27；D-IN01.a/c、D-IN02.a/c、D-LANE01.g；SRS §18.2.8、INV-095／112。
- 依赖：T-IN-02／07／08；与 T-LANE-02／03／05 协调计数接口。
- 工作：List 中完整包装合并为特殊行，避免同一成员又作为 raw 行重复展示／计选；原 raw Lane 仍可独立访问。显示“一处乐器变化”和成员消息数的区别；双击／Locate 到正确包装，解组后恢复幸存 raw 行。可见分页、密集标签 LOD、选择层与 source 命中分离。
- 非目标：不为导入 PC 补包装行或关联扫描；不让目录／List 两种投影计数相加虚增正式事件总数，不从 bitmap 选对象。
- 完成证据：包装／raw／Note／opaque 混合 List golden、选择映射与去重、解组及 Undo 行切换、冷页 Locate 和密集 viewport 记录。
- 门：正确性覆盖 raw 与包装入口选择同一成员、不按 ID 重排同 Tick；性能仅可见行生成字符串，查询／缓存／在途任务有界，隐藏列表停止读取；失败覆盖旧修订／隐藏／owner 删除／关闭，迟到结果不得恢复旧选择或持有已释放资源。

### T-IN-10 — 跨层集成、性能门与验收包

- 状态：进行中，A2a 首次证据见 §7；并非全部工程门通过。切片：A2a 提供首次证据，A2b 完整收口。依据：R27 全部 D-IN；关联 R28／D-LANE01.g；[需求总表 §6](../00-Overview-and-Delivery-Plan.md#6-每轮交付和共同验收门)。
- 依赖：A2a 门依赖 T-IN-01～07；最终门依赖 T-IN-08／09 与 T-LANE-08。
- 工作：执行下列自动矩阵；记录构建／测试命令、fixture、版本及结果，分别整理 A2a 与 A2b 人工检查入口。实测 source→canonical→MIDI／音频消费等价、配置隔离和关闭释放；性能预算从同机基线制定，不填写猜测耗时。
- 非目标：不以“能发声”替代正确性，不以 A2a 通过宣布 R27 完成；不运行未经当前请求授权的 dist 发布或 computer-use。
- 完成证据：变更文件＋R/D 映射、自动报告、冷暖性能对照、未跑／失败清单及人工实填结果。T-IN-10 在完整 A2b 门完成前保持未完成。
- 门：正确性必须满足矩阵全部适用用例；性能报告 p50/p95/p99、UI 最长停顿、首内容时间、分配、GC heap、Working Set、resident/spill／队列峰值和关闭释放；失败或缺测不得写“整体通过”，阻断其受影响切片并说明精确范围。

## 4. 自动验证矩阵（不交给用户逐条手工执行）

下表保留原定完整门，状态列反映当前已测范围；**本轮实际覆盖以 §7 逐项对照为准** 。没有整行矩阵的完整证据时，不用某几个测试通过将该整行改成通过。

| 稳定编号 | 切片／任务 | 数据与操作 | 通过证据／失败门 | 状态 |
|---|---|---|---|---|
| AUTO-IN-01 | A2a；01／02／07 | 两种 owner；新建完整组；Bank 0／127；同 Tick raw、NoteOn、多个包装；共享 Root 较早／较晚 Track | 正式 order／精确命中 later-wins／source trace golden；未命中导入重复原样；无半组 | 部分已测；见 §7 |
| AUTO-IN-02 | A2a；02 | raw 只改值、部分移动／删除／改 target／覆盖、整组移动；所有既有 owner 命令 | 按事务最终态保留或解组；剩余 raw 不丢／不复活；Undo／Redo 恢复 ID、成员、顺序 | 部分已测；见 §7 |
| AUTO-IN-03 | A2a；03 | Format 1／2／3 旧 fixture、新格式包装、坏字段／坏关联；Save／Save Copy | 旧音乐持续可读；严格拒绝不合法新组织数据；迁移确认及永久副本、来源 identity／写入失败原子性 | Persistence 全套已测；见 §7 |
| AUTO-IN-04 | A2a；04／05 | Catalog 各 fallback；部分 Bank、独立 PC、scalar Mapping、三字段继承／override；浏览／取消 | 数值／名称同步、Program 0～127；未修改字段不补值；不隐式 scan/sample I/O；不重建 Mapping | 专项已测；完整性能统计未跑 |
| AUTO-IN-05 | A2a；06 | 500ms／Key60／Velocity100 默认、快速换选、held release、模式切换、偏好重开 | 采样域时序；最新请求生效；干净状态、冻结配置、预载、Master→Limiter；不改变项目模式 | 受控测试已测；真实后端未跑 |
| AUTO-IN-06 | A2a；06／07 | 试听中编辑／OK／Cancel／关闭／导航；正常播放；无 SoundFont／设备与原生失败／迟到结果 | 仅停止弹窗所有者；合法编辑可提交；零音频热线程分配、有界 stream 与请求、关闭释放 | 所有权／停止队列已测；原生专项未跑 |
| AUTO-IN-07 | A2b；08 | 全动作×MIDI／SubVoice；跨页／40万选择／百万对象；混合选择与 Mixed | 正式碰撞／选择／Undo 一致；不支持动作准确禁用；取消、超限、I/O／revision race 零发布 | 未开始 |
| AUTO-IN-08 | A2b；09＋Lane | 包装／raw／Note／opaque List；解组；隐藏／目录／Locate；同 Tick 密集 | 无重复行／计选；包装数和 raw 数分清；Count 随事务和 Undo 更新，不重扫全源 | 未开始 |
| AUTO-IN-09 | A2a／A2b；10 | 导入→浏览→保存→编译→导出；Bank A→PC P→Bank B（无下一 PC）；非零范围／crop／相邻 Segment | Inst. 不导入投影；仅 UI 改动不变 raw/canonical/输出；Full／Incremental／重复编译相同，不用三个末值伪造已采用音色 | 部分已测；完整消费者组合未跑 |
| AUTO-IN-10 | A2a／A2b；04／06／09／10 | 多 target 少事件、单 target 百万、多 target 大样本；冷／热打开／换选／切 Tab／关项目 | 同机基线与改后完整分布、资源上限及释放；不做整源 WPF／字符串／第二对象图；出现失控趋势停止探针 | 部分已测；见 §7 的测量口径 |

大型样本沿用总表 §6 的 `9KX2 18 Million Notes.mid`、MIDI Out #23 密集区（先核验实际可用路径）；不得只测小样本，MIDI→Logical→SubVoice 相关路径分别覆盖。已有全局大型事务上限沿用 SRS §20.4.11；新 UI／包装索引预算按测量设计并在实施记录写明，不凭本文虚构性能数。进程树内存接近 9 GiB 作为既有探针警戒，发生失控趋势立即终止，不用系统分页假死测试耐受性。

## 5. 人工验收：只核布局、手感与声音

开发方先交自动证据、固定样例和打开位置。A2a、A2b 及交互返修均已获用户通过，原答见对应实施报告；不因写出清单就算验收。DPI 100／125／150／200%、短窗、长名称与禁用态由开发方完成统一布局矩阵，用户只需检查交付指定代表环境；没有当前明确授权不得由 agent 调用 computer-use。

### UAT-A2a — 选择器／Initial State／500ms 试听（同一轮，6 项）

| 检查 ID | 具体操作场景 | 预期 | 状态 |
|---|---|---|---|
| UAT-A2a-IN-01 | 分别在 MIDI Segment 与 SubVoice 打开 Inst.，选 Bank／Program 名称后修改三个数值框，再取消一次、确认一次 | 列表／值同步，Program 明确 0～127，未知名称可输入；取消无点，确认出现一个整体点；Logical Segment 无 Inst. | 通过（用户整体确认） |
| UAT-A2a-IN-02 | 双击已有点打开选择器，观察长名称、窄窗口、水平键盘及输入／OK／Cancel；关闭后直接继续编辑 | 值与名称可辨，按钮可达、文本不裁；标签不遮住全部点，焦点回原编辑区，一次操作即可继续 | 通过（用户整体确认） |
| UAT-A2a-IN-03 | 在 Event Instrument 与 SubVoice Initial State 分别只改一个 override；另一次选完整 preset；浏览后 Esc 退出 | 高级区能区分继承／覆盖；只改一项不固定其他继承，完整 preset 才改三项，Esc 不补 0；Project Global 不被扩大 | 通过（用户整体确认） |
| UAT-A2a-IN-04 | 打开提供的旧 SubVoice 样例，查看仅 MSB／仅 LSB／独立 PC 和其 Mapping，从高级 raw 入口改值再 Undo | 旧合法数据仍可找到和编辑，Mapping 目标不消失；Undo 恢复，不能只有“底层保存但无入口” | 通过（用户整体确认） |
| UAT-A2a-IN-05 | 开启自动试听后换选，快速连续换选，再按住键盘后松开；切换试听 Melodic／Percussion | 默认短音为 500ms，快速换选仅最新音色继续；手按持续到松开结束 gate；试听模式变化不改项目，试听不宣称编曲完整听感 | 通过（用户整体确认） |
| UAT-A2a-IN-06 | 试听尚活动时直接确认／取消／关窗并继续编辑；查看无 SoundFont 情况；已有项目播放时打开允许的相关入口 | 本弹窗试听清理，无卡音或需再点一次；无 SoundFont 可编辑但明确不能试听；不为试听错误抢停正常项目播放 | 通过（用户整体确认） |

### UAT-A2b — R27 全编辑＋R28 Tabs 合并一轮

本节 6 项与 [计划 03 的 UAT-A2b-LANE-01～06](03-Lane-Tabs-and-Event-Display.md)合计 12 项，属于**同一个 UAT-A2b 批次** ，不是两轮；无需重复 A2a 全表，仅定点回归实际受影响项。

| 检查 ID | 具体操作场景 | 预期 | 状态 |
|---|---|---|---|
| UAT-A2b-IN-01 | 导入含独立／缺分量／同 Tick Bank-PC 的样例，再浏览 Inst. 与 raw Lanes；随后显式新增一个包装 | 导入 Inst. 为空且不出现只读投影；原消息可编辑；只有显式新增对象出现在 Inst. | 用户验收通过 |
| UAT-A2b-IN-02 | 在 MIDI／SubVoice 分别对多个包装做水平 Move、Ctrl 复制拖动、Copy／Cut／Paste／Delete，再 Undo／Redo | 整组移动／复制，不拆成碎片；只一次 Undo；原件／副本选择可理解，不提供纵移或音符专属操作 | 用户验收通过 |
| UAT-A2b-IN-03 | 对包装做水平 Flip、Scale、Quantize 及多选 Properties，先启用一个 Mixed 字段再恢复原值 | 时间工具可用且点仍为整体；未启用字段不被改写，恢复原值有效；不出现普通单值 Batch 冒充三字段编辑 | 用户验收通过 |
| UAT-A2b-IN-04 | 从 raw Lane 改包装一个成员的值，再单独移走或删除该成员；随后 Undo | 只改值时标签刷新且保留包装；破坏结构才解组，剩余 raw 可见可编辑，Undo 还原，没有自动补回成员 | 用户验收通过 |
| UAT-A2b-IN-05 | 打开密集包装样例，切换图形／List，选包装后 Locate，再解组／Undo 并滚动到远处 | List 不重复列／选成员，数量区分包装与消息；Locate 正确，密集标签可读并能选点，冷数据有合理反馈 | 用户验收通过 |
| UAT-A2b-IN-06 | 保存带包装项目并重开；旧格式样例走交付指定的安全迁移保存／取消路径；播放同 Tick 音色变化样例 | 包装关系及标签仍在，旧源保护提示清楚；取消不覆盖旧文件；可听变化与预先给出的正式顺序说明一致，无停音／卡音异常 | 用户验收通过 |

## 6. 收口与风险记录

- R27 完成必须有 T-IN-01～10、全部适用自动门和 UAT-A2a／UAT-A2b 的实填证据；A2a 只能报告部分能力交付。
- A2a 暂未开放的新增包装专属命令应在交付说明逐项列出，不得影响已存在 raw／owner 操作的 source 正确性。A2b 全编辑未完成或没有用户新批准的限制，不得将原批准矩阵悄悄缩小。
- 建立原规划时，具体格式号／数据布局、audition 适配和性能预算均是实施工程门，当时未运行构建或实验。A2a 已接入部分的当前情况见 §7，不以这段历史记录代替实际进度；没有要求用户重答 Q1/Q2 或新建 Q3。
- 每次交付补充改动文件、需求依据、测试命令／报告、失败／未跑项、限制及受影响人工检查 ID；保持未验证项可见，不使用笼统“全部通过”。

## 7. A2a 实施记录（2026-09-14）

基线 `7b4866d0`，实施轮按“实施下一阶段”开始 A2a，当时不提交／推送，不生成 dist，不使用 computer-use。设计见 [A2a ADR](../../Midora-A2a-Instrument-Changes-ADR-2026-09-14.md)，文件定位、实际命令、测试数字、失败／未跑项及 6 项可操作检查见 [实施与验收记录](../../Midora-A2a-Instrument-Changes-Implementation-2026-09-14.md)。原问题与用户原回答不改写。

| 完整门 | 本轮实际证据／保留项 |
|---|---|
| AUTO-IN-01／02 | 两种 owner、创建／值改／部分与整组移动、最终态解组、碰撞、极端 order、父复制与 Split、Undo/Redo 专项已运行；相关既有 clipboard／事件乐器命令回归已运行。全命令×海量包装的笛卡尔积压力矩阵未跑，不称全覆盖。 |
| AUTO-IN-03 | 新 Format 4 严格关联 component、旧 1/2/3 reader／golden、保存及故障回归已运行；当前 writer 的预期更新不修改冻结 Format 3 字节。旧项目原路径 Save 继续确认＋永久原字节备份＋原子替换。 |
| AUTO-IN-04 | Catalog／未知值、Program 0～127、三字段独立继承、raw Mapping 不重建及真实 BAML／模态／受控 DPI 布局已运行。完整 Catalog 冷热分位统计未跑。 |
| AUTO-IN-05／06 | 干净 canonical 计划、默认 Gate、held release、偏好、独立 owner、latest-wins、停止屏障与故障重试已用受控测试验证。真实 BASS/SF2/SFZ 听感、native 故障注入与音频热线程分配未复测。 |
| AUTO-IN-07／08 | A2b 未实施。现有 raw／owner 路径不能以此为由产生悬空关联。 |
| AUTO-IN-09 | 移除组织关联后的 canonical 一致性、正式 Bank→Program→本 Track NoteOn 顺序、持久化／导入回归已测；完整 Full／Incremental／导出范围组合未穷尽。 |
| AUTO-IN-10 | 20,000 显式包装的有界显示查询及 9KX2 实际导入＋单点命令已测。9KX2 是无 WPF/Worker 的 Application 探针；没有 UI 帧率、进程树、前后基线或 p95/p99。海量 source 关联预算及旧原位路径性能仍须收口。 |

本轮只开放单点包装及统一选择器。新 source 只存稳定关联、不复制 raw；显示排序／读取在后台有界执行。source 关联本身暂为共享不可变树，**不等于已完成百万包装的磁盘分页和内存预算验收** 。这一限制和其他缺测门显式留在 T-IN-10，不用小范围功能通过替代。

人工状态：UAT-A2a-IN-01～06 **通过（用户整体确认）** ，具体步骤保留在实施记录 §6；2026-09-14 用户原答“验收通过，记录并提交、推送。”归档于该记录 §8。本次记录验收并提交／推送，不启动 A2b，不本地发布。下一切片 A2b 的范围和原自动门不变。

## 8. A2b 实施记录（2026-09-14～15）

基线 `10afe2e9`；本轮已开放完整包装的 Move／Ctrl-Duplicate、Copy／Cut／Paste／Delete、水平 Flip／Scale／Quantize／Mixed Properties，并将包装合并进 List 的一行与真实成员选择。新建、批改、复制及载入的关联使用有界双索引与分页，Undo 复用不可变根；不改 Format 4 wire、不把显示行为放入 canonical。

实际命令覆盖、自动回归、40 万包装与真实 MIDI 数据层实验、失败后修正及剩余压力门均以 [A2b 报告](../../Midora-A2b-Instrument-Changes-and-Lane-Tabs-Implementation-2026-09-14.md) 为准。AUTO-IN-07／08 已有两种 owner 的命令、碰撞、选择、取消、跨类型粘贴、保存重开与 List 集成证据；并非所有百万级组合、native 故障和完整 UI 尾延迟均已测。T-IN-10 保留未穷尽门，不据此改写 A2a 原记录。

人工状态：UAT-A2b-IN-01～06 **已通过** ；Inst. Add Lane／创建预览／蓝色框选／中键平移及 Lane Tab 重排返修也已获用户确认，不要求重测整套批改。原答和完整归档见 [报告 §8.3](../../Midora-A2b-Instrument-Changes-and-Lane-Tabs-Implementation-2026-09-14.md#83-人工验收归档2026-09-15)。本次按用户要求记录、提交／推送，不发布，不进入 A3；未跑工程门保持原记录。
