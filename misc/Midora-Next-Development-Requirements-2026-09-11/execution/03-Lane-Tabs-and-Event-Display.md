# 执行计划 03：Lane Tabs、显示值与事件阶梯线

日期：2026-09-15。近期：R28（P1），A2b；后续：R12／R29（均 P2），A4a。用户已确认全部人工验收及交互返修通过，含 LANE-02；扩大压力门见 §8。R12／R29 仍未开始。

总入口：[执行计划索引](00-Execution-Plan.md)。需求依据：[需求 02](../02-Instrument-Changes-and-Lanes.md)、[唯一问答入口 04](../04-Decisions-and-Preparation.md)、[决策汇总 05](../05-Decision-Summary-and-Q2-2026-09-14.md)。R27 包装见 [计划 02](02-Instrument-Changes.md)。本文件拆解已确认决定并记录阶段状态，不新增或改写用户问答；实际实现／SRS 承接／验证证据见 §8。

## 1. 边界、批次与 requirement trace

| 主题 | 输入与正式输出 | 状态归属／边界 | 非目标 |
|---|---|---|---|
| R28 Lane Tabs | 当前 owner＋正式 target、内容摘要、显示／隐藏／导航动作 → 单活动 Lane 及轻量 Tab 状态；显式编辑仍提交正式 Project command | Vel. 第一、适用 Inst. 第二且不可隐藏；普通 target 可隐藏；目录显示全部已有 targets 的名称、精确数量、显隐，含无点但有 Curve／Mapping owner；没有摘要时后台统计中 | 不全建 128 CC，不把隐藏等同删除，不通过共享 Track profile 在其他 Segment 创建 Lane，不建第二套音乐模型 |
| R12 友好显示值 | CC10／CC71～78 raw 0～127 → 外侧显示／数值编辑工具 −64～63 → 正式 raw 编码 | 编辑层转换 display=raw−64；Project Mapping Function、内置／图形 Step、共享 accumulator、Context、取整／ABI 保持 raw；Help 对照两层 | 不对其他 CC／Note／Logical Parameter／时间／目标身份偏移，不在普通编辑入口增加 raw 辅助值或切换开关，不放弃旧项目音乐读取 |
| R29 阶梯线 | 当前 Lane 自己的显式状态型标量点／Logical Parameter Step 点、同 owner 前驱及 formal order → 辅助显示线 | 有前驱接入可视左界，无前驱从首显式点起；默认显示、可关闭、线不命中；界外数据可视与实际发声范围区分 | 不解释跨 Track／Root 最终状态，不凭 Initial/default 造线，不改 Value Curve／Envelope，不把 Velocity／Inst.／Bank-PC／opaque／CC120～127 机械连线 |

共同约束：稳定 ID／正式 target 是身份，名称和 Tab 位置不是身份；`[start,end)`、正式同 Tick order、精确碰撞与 source trace 不变。任何编辑失败／取消／revision race 均零部分发布；派生显示失败不得改 Project。普通显示、Tab 重排、显隐、纵轴不进入 canonical／MIDI／音频，也不作为音乐 Modified 或 Undo。R28 先实现会话内稳定状态，未来 R06／R07 按已确认白名单接入独立 presentation，不序列化整个 Tab VM。

| 阶段 | 工程任务 | 人工批次／依赖 |
|---|---|---|
| A2b（近期 P1） | T-LANE-01～08；与 R27 后半段集成 | 与 R27 **合并一个 UAT-A2b** ；不等 R12／R29、完整工作区恢复或多实例 |
| A4a（后续 P2） | T-VAL-01～06、T-STEP-01～06 | 一个 UAT-A4a 显示域／阶梯线批次；两个主题可并行，只有共享呈现合同需要协调 |

A4a 只是总计划 A4 中 R12／R29 的可独立验收子集，不把 P2 升为 P0/P1，不成为 A2a／A2b 入口条件。本文件中的工程子任务数量不等于人工验收轮数；每个子任务完成均应自动检查，不让用户逐项遍历底层测试矩阵。

### 1.1 规格与源码承接

现行依据：SRS §8.50～52（Lane／身份）、§8.54～60（值域／Bank-PC／曲线／顺序）、§18.2.5～8／§18.4（宿主、列表、界外内容与范围查询）、§20.2／§20.3.15／§20.4.11／13／17～19（焦点、有界选择、工具表达式与事务）；INV-047／065／069／075～077／095／096／104／107／108／110／112。

实施时须同步已确认的 Tabs／隐藏目录／计数、指定 CC 外侧工具数值契约、阶梯显示规则；不得把本轮计划误当 SRS 已改。保留共享 Draw、Free／Line／Horizontal 数值绘线形态组及非 Draw 禁用的 D-UI01.d；A3 负责完整手势替换，本计划不恢复已撤回的三独立 Pen 或拆成两套主 ToolMode。

调查入口见需求 02 §2：`PresentationModels.cs` 的目标发现、`TimelineEventTargetIndex.cs`、`TimelineObjectListSource.cs`、`ProjectSubVoiceEventLaneEditCommands.cs`、`TimelineTempoTileRasterizer.cs`。当前静态阅读确认目标索引 Build 会全源分组并保留点数组／prefix 数组，同 Tick 使用 ID 排序，不能直接当作满足新正式 order 与百万级内存要求的实现。Tempo provider 可参考但绑定 Conductor，不直接复制其模型到所有事件。

## 2. R28：近期 Lane Tabs 工程任务

### T-LANE-01 — target 身份、状态所有权和性能基线

- 状态：功能已实施；性能实测和缺测见 §8。切片：A2b。依据：R28；D-LANE01.a～g、D-STATE03.a、D-UI01.d；关联 R24／R26。
- 依赖：R27 的 T-IN-01／02 稳定包装／raw 合同；可在 A2a 期间并行准备，不阻塞 R27 选择器。
- 工作：列三宿主 target 能力表、Tab 顺序／显隐／活动 target／纵轴／Snap／Selection 归属；定义 owner＋target 摘要接口、点数／曲线／Mapping owner 标记及包装数与成员数口径；重测现有目标发现、切 Lane、List／目录冷暖开销并制定可验证预算。
- 非目标：不序列化 VM、不提前实施 R06／R07；不以 Tab 文字或索引作身份，不把 Catalog 名称纳入缓存音乐身份。
- 完成证据：R/D→接口／控件／命令／测试追踪表、宿主能力表、状态所有权表、基线与预算记录、实施期规格更新清单。
- 门：正确性要求 owner／target 与音乐数据／显示状态分离；性能须列摘要／位图／队列／Surface 上限及释放点，不以“全后台”代替内存有界；失败须明确未知计数、旧 revision、owner 失效、target 被删的显示行为。

### T-LANE-02 — 构建精确 target 摘要与旧数据后台补建

- 状态：功能已实施；不可变源首次后台补建，热摘要与增量更新已测。切片：A2b。依据：R28；D-LANE01.g；INV-065／069／095。
- 依赖：T-LANE-01。
- 工作：在导入／source page 构建已经遍历时顺带统计 owner＋target 精确点数及曲线／Mapping owner 存在性；旧数据缺摘要时一次有界、可取消后台补建；摘要绑定正式 revision，目录读取 K 个 targets 而非 N 个事件。
- 非目标：不要求每次目录／刷新／切 Tab 全源扫描，不为 R27 导入 Inst. 执行 Bank-PC 前驱查询，不把未知计数假装 0。
- 完成证据：空／单点／无点曲线／无点 Mapping owner／同 Tick 重复／跨页摘要与独立参考计数比对；冷补建取消与重复请求去重测试。
- 门：正确性为精确总数、不丢合法空 owner、不把包装和成员重复相加；性能允许首次缺摘要实际 O(N) 后台成本，但 working／resident／队列有界，热目录按 K 工作；失败或旧 revision 不发布不完整摘要，期间明确“统计中”，关闭停止请求并释放资源。

### T-LANE-03 — 编辑、碰撞与 Undo 的增量摘要

- 状态：功能已实施；对应值改、换 target、碰撞及 Undo 回归通过。切片：A2b。依据：R28；D-LANE01.g；R27／D-IN02.a；INV-047／077／096。
- 依赖：T-LANE-01／02 与 T-IN-02。
- 工作：随正式 command 的最终结果及精确 change set 更新 old/new target 摘要；覆盖新建／删／移动／换 target、覆盖碰撞、曲线／Mapping owner、包装解组、父 owner 变更和 Undo／Redo；目录刷新只取修订摘要。
- 非目标：不以动作候选数当最终点数、不在 UI 自己猜 delta、不因只改值重扫全 owner，不通过摘要重建已删除 Mapping。
- 完成证据：随机命令序列对照完整参考摘要、碰撞幸存者计数、Undo／Redo old/new 摘要一致性、精确改动页访问探针。
- 门：正确性为碰撞后实际结果精确计数；性能改动成本受实际受影响 target／页约束，不扫描无关大 Track；失败为事务取消／验证错／资源耗尽／revision race 后音乐 root 与摘要均无部分新版本。

### T-LANE-04 — 每目标 Tab 壳、重排与单活动 Surface

- 状态：功能已实施；三宿主真实 WPF 与受控 DPI 布局已测。切片：A2b。依据：R28；D-LANE01.b/d/f、D-STATE03.a；需求 02 §5。
- 依赖：T-LANE-01／02；Inst. 内容接 T-IN-07／09。
- 工作：三宿主替换旧 LANE 标题／下拉与通用 Event/Parameter Tab；Vel. 固定第一，MIDI／SubVoice Inst. 第二，其余 target 各一 Tab。普通 target 首次确定排序，新建追加，保留用户拖拽顺序；头部滚轮水平滚动，工具与目录入口同一行最右；只挂载活动 Surface，隐藏项只留轻量状态。
- 非目标：不把全部 Lane 垂直堆叠、不为每隐藏 Tab 建索引／计时器／常驻编辑 Surface；Logical 不新增 Inst.，不预建全部 CC。
- 完成证据：三宿主 Tab 结构／固定顺序／初始与用户顺序测试、虚拟化与活动 Surface 数量探针、窄窗／长名称／DPI 布局记录。
- 门：正确性为重排不改变 source／Mapping order 或 canonical；性能切 Tab 不全源字符串化或预建所有 Surface；失败为布局／异步加载失效不恢复旧 target，短窗口仍有可达 Add／目录／工具，不能靠隐藏用户必需按钮解决溢出。

### T-LANE-05 — 全 target 目录、隐藏与重新打开

- 状态：功能已实施，用户验收通过。切片：A2b。依据：R28；D-LANE01.a/b/c/g。
- 依赖：T-LANE-02／03／04；包装计数口径接 T-IN-09。
- 工作：最右下箭头打开可搜索、虚拟化的全部已有 target 目录，名称／精确数量／显隐齐全，曲线与 Mapping owner 无点时明确标记；点击显式显示并激活；普通 Tab 关闭仅隐藏，基础 Tab 不可关闭；未知摘要显示统计中并异步更新。
- 非目标：不另建常驻重面板、不把隐藏当 Delete Data／Delete owner，不把 0 点误判为没有 owner，不把目录变成任意 target 新建入口。
- 完成证据：有内容隐藏后可发现／重开、目录搜索／键盘到达、空 owner 标记、数量与包装消息口径测试，热目录 source 访问计数。
- 门：正确性为所有已有 targets 可发现且显隐不改 Project；性能目录打开不解码全事件，行虚拟化、搜索与请求有界；失败为统计取消不报假 0，旧结果不覆盖新摘要，关闭目录后不留下持续扫描或抢焦点。

### T-LANE-06 — 显式导航、Selection、轴与工具接线

- 状态：功能已实施；显式 Add／Locate 与被动选择分离回归通过。切片：A2b。依据：R28；D-LANE01.c/e/f、D-STATE03.a、D-UI01.d；关联 R24／R26。
- 依赖：T-LANE-04／05；现有 Add／Locate／Properties 命令。
- 工作：显式 Add／Locate／目录点击显示并激活 target，焦点进有效 Surface；普通 Properties／刷新／后台／Undo 不抢回隐藏 Tab。切 Lane 保留原选择；混合选择用既有显式类型子菜单。主 Piano 与 Event Snap 独立，所有数值 targets 共用 Event Snap；各 Lane 纵轴独立、水平同步。为 A3 共享 Draw 与独立形态组接入稳定接口，不改变工具所有权。
- 非目标：不因工具栏合并合并两套 Snap，不创建两套主 ToolMode；Inst. 不沿线创建，数值形态不越权作用于音符／Segment。
- 完成证据：三宿主 Add／Locate／切换／Properties／隐藏 Undo 用例，键盘路由与焦点、独立轴／Snap 状态断言；混合选择前后集合对照。
- 门：正确性为显式与被动导航分离、旧选择不丢、target 轴不串；性能导航只准备活动范围，不同步解析大 Selection；失败为 owner 删除／revision race／捕获取消不激活旧目标，输入错误不留半次 Project 命令。

### T-LANE-07 — 局部失效、关闭释放与后续状态接口

- 状态：功能已实施；切 Workspace／隐藏卸载集成已测，长会话完整压力门未穷尽。切片：A2b。依据：R28；D-LANE01.c/f/g、D-STATE03.a/c；INV-069／095／112。
- 依赖：T-LANE-02～06。
- 工作：将显示 generation 与 source revision 分离；只失效改动 target／范围的点、线、标签与选择层；隐藏／卸载取消请求，owner 删除释放自身缓存／任务／订阅／lease，Project 关闭全部释放。按 Segment 保存会话内 Lane 纵轴／顺序／显隐／活动 target，SubVoice 独立，留显式 presentation 适配接口给 B 主题。
- 非目标：不在 A2b 搭载完整恢复；不把已删除 owner 对象保活，不让同 Track profile 在另一个 Segment 添加不存在的 target，不在每次切换复建全索引。
- 完成证据：局部编辑瓦片失效记录、快速切 Tab／隐藏／关 Workspace／删 owner／关 Project 的弱引用或资源计数测试、后续状态 DTO 接口说明。
- 门：正确性为新旧修订不混帧、旧任务不能复活已隐藏或删除视图；性能缓存／在途并发有界且长会话稳定释放；失败为加载错／取消保留合法已提交 source，不把显示缓存故障转换成音乐修改。

### T-LANE-08 — 跨宿主集成与合并验收门

- 状态：全部人工验收及返修通过，保留缺测门；切片：A2b。依据：R28 全部 D-LANE；R27 对接；[需求总表 §6](../00-Overview-and-Delivery-Plan.md#6-每轮交付和共同验收门)。
- 依赖：T-LANE-01～07、T-IN-08／09；与 T-IN-10 共同形成一次 A2b 交付包，不构成循环实现依赖。
- 工作：执行 AUTO-LANE 矩阵并整理 UAT-A2b-LANE 6 项，与 R27 的 6 项合并。记录三宿主和大样本冷暖分布、目录读取事件数／target 数、资源峰值及关闭释放，复核未改变 source／canonical／输出。
- 非目标：不把 R12／R29 未实施写成 R28 失败，不增加第二次 A2b 人工轮；不以静态“有虚拟化”替代实际测量。
- 完成证据：R/D／文件／自动报告映射、明确基线与改后对照、失败／未跑项、一次 UAT-A2b 实填结果。
- 门：正确性须通过以下全部适用自动项；性能按实施前测量定预算，报告 p50/p95/p99／UI 最长停顿／分配／Working Set／resident/spill／队列，不虚构阈值；失败或缺测必须保留并阻断受影响能力的完成声明。

## 3. R12：后续显示域工程任务（P2，A4a）

### T-VAL-01 — 冻结外侧数值契约及入口清单

- 状态：未开始。依据：R12；D-VAL01.a/b/c/d；SRS §20.4.13／19、INV-029／104。
- 依赖：无 P0/P1 前置工作要求；正式接入采用已稳定的 Lane target 接口。
- 工作：列出白名单 CC10／71～78、所有显示与数值工具输入输出、源编码差异、取整／Clamp 边界、工具 profile／Preset 数值版本待更新点；明确 Mapping 全链 raw 不变，保存既有音乐读取测试基线。
- 非目标：不重新提问 Mapping 是否改域，不提前写死新 profile 版本，不改变 Project Mapping ABI。
- 完成证据：入口与排除清单、raw／display 往返与工具／Mapping 对照契约、实施期规格／Help 变更清单。
- 门：正确性区分绝对值、delta、factor、Logical Parameter 自有范围；性能不额外全源预扫描；失败为不支持或语义不明确的 target 不机械减 64，影响已批语义的实际新冲突才回 04。

### T-VAL-02 — 共用 target 显示描述与无损反变换

- 状态：未开始。依据：R12；D-VAL01.a/d；SRS §8.54、§8.56。
- 依赖：T-VAL-01。
- 工作：建立集中描述供格式化、值轴与编辑转换使用，白名单 display=raw−64、inverse=display+64；分别处理 Direct 14-bit PB 与 SubVoice signed PB，保证原 PB 语义，不套 CC 偏移。
- 非目标：不改 CC 编号、Catalog 字段、Bank-PC、Note、时间或 Mapping 音乐值，不散落多个独立减 64 实现。
- 完成证据：全部 128 个合法 raw 值逐项往返、0／64／127→−64／0／63 golden、非白名单／PB 回归。
- 门：正确性为无损单次变换；性能为纯 target 描述查询，不分配每点包装对象；失败为 NaN／Infinity／非法输入在正式既有规则下拒绝或正规化，不产生非法 MIDI 字节或重复偏移。

### T-VAL-03 — 图形、List、Properties 与 Initial State 一致呈现

- 状态：未开始。依据：R12；D-VAL01.a/c/d。
- 依赖：T-VAL-02、已稳定的三宿主入口。
- 工作：接 ruler、坐标、点／条 Tooltip、List、Properties、适用 Initial State；白名单入口只显示友好值，没有 raw 辅助项／开关；入口编辑最后编码回正式 raw。
- 非目标：不改变合法音乐范围或以显示 viewport 充当合法范围；不把“完全不额外显示 raw”扩成隐藏 Program／Bank 合法数值。
- 完成证据：同数据跨入口显示／编辑 golden、继承字段未改动测试、白名单与排除目标快照。
- 门：正确性各入口结果相同且不越 owner 能力；性能仅对可见记录格式化、局部失效；失败为取消／无效 draft 不改数据，source revision 更新不覆盖冻结 Properties draft。

### T-VAL-04 — Batch／Generator 与 Preset 数值适配

- 状态：未开始。依据：R12；D-VAL01.b/d；SRS §20.4.11／13／19。
- 依赖：T-VAL-01／02。
- 工作：批改与生成器相应 point value、Initial、递推输入／输出使用显示域，最终一次转 raw；delta／factor 不做绝对偏移。更新工具数值契约版本和 Preset 验证，保留语法／API／资源安全上限，Undo／Redo 不重新求表达式。
- 非目标：不承诺旧工具表达式／预设结果兼容，不据此拒绝旧 `.midora` 音乐；不改变 Project Function、内置 Step、accumulator、Context 或 ABI。
- 完成证据：Batch 与 Generator display/raw golden、递推／DAG／碰撞／Clamp 边界、Preset 当前契约重验证与旧不兼容提示测试。
- 门：正确性 raw96／display32 经工具 `=p0*0.5` 得 display16／raw80；性能表达式一次编译、分页求值和有界 staging；失败为表达式／预算／I/O／revision race 零发布，不部分写入 raw。

### T-VAL-05 — Help 两层对照与 Mapping 不变回归

- 状态：未开始。依据：R12；D-VAL01.d 的明确 Help 要求；INV-029／104。
- 依赖：T-VAL-02～04。
- 工作：Help 解释外侧编辑／数值工具与数据／Mapping 的不同值域，以同一 CC10 raw96 样例对照：工具半值写 raw80，Mapping `value*0.5` 或内置 Multiply 0.5 仍输出 raw48／显示−16；明确仅旧工具结果兼容可破坏。测试映射 Function、内置／图形 Step、共享累计链及 Context 不变。
- 非目标：Help 的编码说明不恢复普通 Tooltip raw 值或全局开关；不在 Function 入口偷偷偏移。
- 完成证据：Help 页面与实际命令可复现例子、Mapping golden／ABI 快照、旧项目读取与 Full／Incremental 等价记录。
- 门：正确性 Help 数值和执行一致；性能 Mapping 不新增显示转换开销／全源求值；失败为两种结果混淆或 Mapping 声音改变即阻断 R12 交付，不用文字说明掩盖实现差异。

### T-VAL-06 — 三宿主回归、性能与 A4a 交付证据

- 状态：未开始。依据：R12 全部 D-VAL。
- 依赖：T-VAL-01～05。
- 工作：执行 AUTO-VAL 矩阵，覆盖三宿主适用 CC、无变化 target、Properties／List／图形／工具一致性与大样本；合并 UAT-A4a 中显示域 3 项。
- 非目标：不阻塞 R27／R28，不额外建立一轮人工数值底层测试。
- 完成证据：构建／自动测试命令、入口完整性表、冷暖格式化／编辑分布与释放记录、Help 验收项。
- 门：正确性为工具已改、Mapping 未改、旧音乐可读；性能按同机基线验证可见格式化与分页事务有界；失败／未测项保留，不报告未经验证的性能提升。

## 4. R29：后续阶梯线工程任务（P2，A4a）

### T-STEP-01 — 冻结可连线目标与自身记录语义

- 状态：未开始。依据：R29；D-STEP01～04；SRS §8.55／58、INV-076／110。
- 依赖：无 R27 的声音／格式前置；接稳定 target 能力表。
- 工作：逐类定义状态型标量／Logical Parameter Step 能力，排除 Velocity、Inst.、Bank-PC、opaque、CC120～127；明确只画当前 owner 显式点、前驱和正式同 Tick order，界外区间／模板边界、开关与不命中规则。
- 非目标：不把 Value Curve／Envelope 全改为 Step，不查询整个 Channel 最终状态、不用 Initial/default 虚构起线。
- 完成证据：三宿主 target 能力矩阵、首点／前驱／同 Tick／界外 golden 参考定义与实施期规格承接。
- 门：正确性线仅代表数据而非发声承诺；性能无强制全源 prefix；失败为能力不适用时不连线，不借 fallback 伪造状态。

### T-STEP-02 — 正式 order 的范围与前驱查询

- 状态：未开始。依据：R29；D-STEP02/03；INV-065／069／095。
- 依赖：T-STEP-01；复用 T-LANE-01／02 可用范围接口，不要求把两者做成同一索引。
- 工作：为活动 owner＋target 提供绑定 revision 的可见范围与最近前驱、页级聚合；同 Tick 首末严格依正式 order，不按稳定 ID；覆盖稀疏长间隔、极端 Tick 与无前驱。
- 非目标：不建立全事件数组／多份完整 prefix，不对所有 Track 做 channel-state 查询，不触发 UI 同步编译。
- 完成证据：范围查询与朴素参考模型对照、同 Tick 非 ID 顺序、跨页前驱／半开边界／极端 Tick 自动用例。
- 门：正确性为同 owner 最近前驱与首末精确；性能仅候选页和有界目录／聚合；失败为页错／revision race／取消不发布混修订数据，旧结果不能接入新线。

### T-STEP-03 — 阶梯 tile、LOD 与局部失效

- 状态：未开始。依据：R29；D-STEP01～03；SRS §18.2.7、INV-069／110。
- 依赖：T-STEP-01／02；如同批 R12 接入，取 T-VAL-02 显示描述，不因此前移 R12。
- 工作：参考 Tempo 的设备列 first／last／min／max 思路形成共享适配，画水平保持与变化 Tick 竖跳；左界前驱正确接入，界外内容弱化明确，不跨 owner 接线。点／选择／线层局部失效并保持像素对齐。
- 非目标：不删 LOD 合并像素内的正式记录、不线性插值声音，不为每点建 WPF Control，不直接复制 Conductor 模型。
- 完成证据：空／单点／重复 Tick／跨页／极低缩放／极端间隔 raster golden、改一个局部点的失效范围与缓存命中记录。
- 门：正确性 LOD 只改像素，点／命中／选择／编译序列不变；性能由设备列／活动 tiles 和有界后台工作控制；失败为旧瓦片不覆盖当前 revision、取消清理在途资源，不显示跨 owner 的假连接。

### T-STEP-04 — 开关、输入透明与编辑集成

- 状态：未开始。依据：R29；D-STEP04；D-LANE01.e、D-UI01.d。
- 依赖：T-STEP-03。
- 工作：默认显示线，提供关闭线视图选项；线不参与命中，所有拖拽仍以点和已有 source hit-test 执行；整合三宿主换 Lane、Snap、选择、Undo 与共享 Draw／事件形态，不改变采样密度和碰撞。
- 非目标：不增加点击线选前驱或修改持续状态的隐式操作，不把开关加入 Project 音乐 History；跨会话持久化归 B 白名单，不在此私加 schema。
- 完成证据：相同输入开启／关闭线的命中／命令结果对照、选择与 Undo 对照、只改变视图的断言。
- 门：正确性开启前后音乐与编辑结果完全相同；性能开关不复建 source 索引、切换取消旧请求；失败为关闭／owner 删除／捕获丢失无迟到编辑或多余 Undo。

### T-STEP-05 — 同 Tick、界外和非状态目标反例

- 状态：未开始。依据：R29；D-STEP01～03；INV-050／051／077。
- 依赖：T-STEP-02～04。
- 工作：构造共享 Root 另一 Track 改值、当前 Lane 无前驱、crop 外点、相邻 Segment、SubVoice 模板边界、PB、Bank-PC／命令／opaque、曲线＋离散点的反例；确保线只是对应数据源的辅助展示。
- 非目标：不为使线看似连续而补原始事件、不将界外线解释成音频持续，不覆盖 Value Curve 原绘制语义。
- 完成证据：反例截图／raster golden＋源序列／canonical／导出比较，隐式状态查询计数探针。
- 门：正确性禁止跨轨“借值”、默认值起线或非法目标连线；性能前驱查询不退化从 owner 起点扫描每次 pan；失败为边界／页错误受控反馈，不以近似音乐语义换取通过。

### T-STEP-06 — 大样本、关闭释放与 A4a 交付

- 状态：未开始。依据：R29 全部 D-STEP。
- 依赖：T-STEP-01～05。
- 工作：执行 AUTO-STEP 矩阵，测线开／关冷暖 pan／zoom／局部编辑／Undo／切 Tab／关 Project，整理 A4a 阶梯线 3 项，与 R12 合并交付。
- 非目标：不阻塞 P0/P1，不用单次均值或稀疏小样本宣称百万级通过，不新增一轮用户逐项查像素的测试。
- 完成证据：完整测量分布／环境／样本／内存与任务峰值／关闭释放、自动回归结果及人工实填清单。
- 门：正确性源与消费者结果不变；性能相同机器／内容／viewport 对比，并遵守冻结预算；失败、缺样本或未测环境明确记入限制，不能写整体通过。

## 5. 原定自动验证矩阵

此表是完整计划范围，原状态保留用于对照；A2b 实际已测子集、结果和未跑门见 §8，不把少量断言通过写成整行压力矩阵通过。R12／R29 尚未实施。

下表属于工程自动验证，不能转换成用户重复手工遍历任务。每次报告需列构建／测试命令、fixture、源版本、实际通过／失败／未跑，不能只贴“测试完成”。

| 编号 | 覆盖任务 | 自动场景 | 必须证据与失败门 | 状态 |
|---|---|---|---|---|
| AUTO-LANE-01 | LANE-01／04 | 三宿主；空／单 target／多 target；导入／Add／重排／重命名 | Vel.／适用 Inst. 常驻，Logical 无 Inst.；按正式 target 稳定识别；重排不改变 source／canonical／MIDI | 未开始 |
| AUTO-LANE-02 | LANE-02／05 | 同 Tick 重复、跨页、0 点曲线、Mapping owner、包装与成员 | 精确计数＋存在标记；未知显示统计中；无重复计数，独立参考计数一致 | 未开始 |
| AUTO-LANE-03 | LANE-03 | 新增／碰撞覆盖／移 target／删／解组／Undo／Redo 随机序列 | 按最终 root 增量更新，未命中重复不清洗；错误／取消／revision race 不发布新摘要 | 未开始 |
| AUTO-LANE-04 | LANE-04～06 | 普通 Tab 隐藏；目录搜索／重开；Add／Locate；普通刷新／Properties／Undo | 显式导航激活，被动更新不抢；选择保留；两套 Snap、各自纵轴与共享水平正确 | 未开始 |
| AUTO-LANE-05 | LANE-06／07 | 快速换 Tab／关目录／隐藏 List／删 owner／关 Project；冷页迟到 | 焦点／选择不倒退；旧 generation 不复活；任务／Surface／订阅／页 lease 全释放 | 未开始 |
| AUTO-LANE-06 | LANE-02／07／08 | 多 targets 少点、单 target 百万、所有 targets 大样本；冷摘要首次和热目录重复打开 | 初次后台有界、可取消；热目录读 K 摘要不扫 N 事件；工作集、队列和 Surface 峰值有界 | 未开始 |
| AUTO-VAL-01 | VAL-01～03 | 白名单每个 raw0～127；非白名单／Bank-PC／PB／Note／Logical Parameter | 往返无损，偏移恰好一次；指定所有入口一致，无普通 raw 辅助值／开关 | 未开始 |
| AUTO-VAL-02 | VAL-04 | Batch／Generator identity、Initial、递推、DAG、负／越界、碰撞、工具 Preset | 工具显示域→raw 正确，delta／factor 不偏移；安全上限／取消／I/O／revision race 零发布 | 未开始 |
| AUTO-VAL-03 | VAL-05／06 | 同一 raw96 的工具半值与 Mapping 半值；Function／内置 Step／accumulator／Context；旧项目 | 工具得 raw80，Mapping 得 raw48；Help 与执行一致，Mapping ABI／音乐 golden／Full-Incremental 不变 | 未开始 |
| AUTO-VAL-04 | VAL-03／04／06 | 三宿主 List／Properties／图形／Initial State／工具；冷暖大样本 | 可见格式化及分页编辑有界、局部失效；无第二完整对象图；原始未编辑数据不变 | 未开始 |
| AUTO-STEP-01 | STEP-01／02／05 | 空／单点／无前驱／跨左界前驱／同 Tick 非 ID 顺序／极端 Tick | 只取同 owner 自己的显式点，正式首末正确，无默认或其他 Track 虚构线 | 未开始 |
| AUTO-STEP-02 | STEP-01／03／05 | Logical Step、状态 CC／PB；Velocity、Inst.、Bank-PC、CC120～127、opaque、Value Curve／Envelope | 适用矩阵准确；界外／模板边界清楚；非目标不错误连线或改曲线语义 | 未开始 |
| AUTO-STEP-03 | STEP-03／04 | 密集设备列 LOD、切缩放、点拖拽／Snap／碰撞／Selection／Undo；线开关 | 线不开新命中，LOD 不删记录；source／canonical／导出不变，局部失效及新旧帧隔离正确 | 未开始 |
| AUTO-STEP-04 | STEP-02／03／06 | 稀疏长间隔、百万密集点、冷／热 pan／zoom、快速切 Lane／关闭 | 查询／tiles／任务有界，前驱不反复扫历史，取消／关闭资源释放；记录线开关性能对照 | 未开始 |

性能统一记录同机同样本的 p50／p95／p99、UI 最长停顿、首内容时间、分配量、GC heap、Working Set、resident/spill／队列／缓存峰值与关闭释放。实际预算在各主题实施前重测并记录，不在本计划伪造毫秒或百分比收益。大型样本按需求总表 §6，先核验 `9KX2 18 Million Notes.mid` 路径与 MIDI Out #23 密集区，包含 40 万选择／百万对象及 MIDI→Logical→SubVoice 路径；进程树内存接近 9 GiB 按既有警戒终止有失控趋势的探针。SRS §20.4.11 的既有有界事务上限仍适用，目录／图形摘要不能因为“可重建”就无界。

## 6. 人工验收清单

用户只核查自动测试不能替代的布局、手感和可理解性。开发方先给自动证据、固定样例与打开位置，并自行完成 DPI 100／125／150／200%、短窗／长名称／禁用状态矩阵；没有当前明确授权不得调用 computer-use。A2b 全部人工验收及返修通过，原表操作和预期不变；A4a 未开始。

### UAT-A2b — 与 R27 全编辑合并一次（本文件 6 项）

与 [计划 02 的 UAT-A2b-IN-01～06](02-Instrument-Changes.md)合计 12 项，是同一个 UAT-A2b；文件拆分不产生额外人工轮次。

| 检查 ID | 具体操作场景 | 预期 | 状态 |
|---|---|---|---|
| UAT-A2b-LANE-01 | 依次打开 MIDI Segment、Logical Segment、SubVoice 的 Lanes，并看空样例与多 target 样例 | 每 target 一个 Tab；Vel. 固定第一，MIDI／SubVoice Inst. 第二且不可关闭，Logical 无 Inst.；旧 LANE 下拉／第二行工具栏退出 | 用户验收通过 |
| UAT-A2b-LANE-02 | 用长名称多 targets 样例缩窄窗口，滚轮浏览 Tab 头并拖拽普通 Tab 重排 | 可水平滚动和重排，基础 Tab 位置不乱；最右 Add／目录／坐标／Snap 可达，文字和按钮无重叠裁切 | 用户验收通过（返修后） |
| UAT-A2b-LANE-03 | 隐藏有内容的普通 Tab，再开下箭头目录、搜索名称并点回；查看无点曲线与 Mapping owner | 目录同时看到显隐、名称、精确数量／明确存在标记；点击显示并激活；隐藏不删数据，没有内容的假 0 不掩盖统计中 | 用户验收通过 |
| UAT-A2b-LANE-04 | 改／删／覆盖一条事件，观察目录计数，再 Undo／Redo；隐藏一 Tab 后被动刷新，最后用 Add 或 List Locate | 数量随实际结果变化；被动刷新／Undo 不抢回隐藏 Tab；显式 Add／Locate 才打开并激活目标 | 用户验收通过 |
| UAT-A2b-LANE-05 | 选一 Lane 的点后切另一 Lane，分别调纵轴；调 Piano Snap 与 Event Snap、再切回 | 原选择不偷偷消失；各纵轴独立，水平同步；Piano/Event Snap 不串，各事件 targets 共用 Event Snap，操作焦点自然回编辑区 | 用户验收通过 |
| UAT-A2b-LANE-06 | 在给定大型样例快速切 Tab／开关目录与 List，再关 Workspace／项目；重新开一个小项目 | 冷加载反馈清楚，目录／切换没有明显冻结或旧内容闪回；隐藏后不再持续占用可见任务，关闭后操作恢复正常 | 用户验收通过 |

### UAT-A4a — 显示域与阶梯线（后续 P2，共 6 项）

仅 R12／R29 实施完适用自动门后安排，不阻塞 A2a／A2b；不是要求用户当前执行。

| 检查 ID | 具体操作场景 | 预期 | 状态 |
|---|---|---|---|
| UAT-A4a-VAL-01 | 打开白名单 CC10／71～78 样例，在图形标尺、坐标、Tooltip、List、Properties、适用 Initial State 查看并改边界值 | 统一 −64～63、中心 0，无额外 raw 辅助值／开关；其他 CC／Bank-PC／音符不被减 64 | 未开始 |
| UAT-A4a-VAL-02 | 对给定 CC10 显示32样例执行工具 `=p0*0.5`，再在独立对照样例查看 Project Mapping 半值效果与 Help | 工具结果显示16；Mapping 对照显示−16；Help 清楚解释外侧编辑层与数据层，无误导“所有表达式同值域” | 未开始 |
| UAT-A4a-VAL-03 | 从图形、List、Properties 分别做同一改值，试 Batch Create 并取消一次、Undo／Redo一次 | 各入口读数／结果一致，工具输入与界面同域；取消无内容、Undo／Redo 可理解，无原始字节越界弹错或隐藏数值跳变 | 未开始 |
| UAT-A4a-STEP-01 | 平移有前驱、无前驱、单点与同 Tick 密集点样例，观察三宿主适用 Lane | 水平保持、变化 Tick 竖跳；可见左界前驱连续，无前驱不从默认值画线；密集图仍可辨主要变化 | 未开始 |
| UAT-A4a-STEP-02 | 查看 crop 外点、相邻 Segment、SubVoice 模板边界，并切 Velocity／Inst.／Bank-PC／命令目标／已有曲线 | 界外线与可听区间区别明确、不跨 owner 接线；排除目标不乱连，Value Curve／Envelope 原样 | 未开始 |
| UAT-A4a-STEP-03 | 开关阶梯线；点击线段、拖点，缩放后再编辑／Undo；在大样例平移和切 Tab | 线只辅助、不抢命中；开关不改音乐或选择，拖点方式未变；局部更新自然，旧线不闪回，无明显新卡顿 | 未开始 |

## 7. 完成声明与后续依赖

- R28 需 T-LANE-01～08、AUTO-LANE 与同一 UAT-A2b 的实填证据；只替换 Tab Header、没有精确摘要／隐藏目录或关闭释放不算完成。
- R12 需全入口＋数值工具显示域＋Help＋Mapping 不变回归；只改文字不算完成，改变 Mapping 音乐域也不符合已确认决定。
- R29 需范围／前驱／同 Tick／LOD／不命中／界外语义及有界门；只有一条能画出的线不算完成。
- R06／R07 后续使用稳定 target 和 Segment 局部状态接口；R28 不替它提前私加 presentation 字段。A3 接共享主 ToolMode 与独立事件绘线形态；本计划不恢复 Q1 已撤回的独立 Pen 方案。
- 建立本规划时没有产品验证；最新 A2b 证据见 §8。没有新增 Q3。若发现必须改变已批准工作流的具体约束，再到 04 留原问题／建议／用户答复／确认状态。

## 8. A2b 实施记录（2026-09-14～15）

基线 `10afe2e9`。三宿主共用 LaneTabSession／LaneTabHost／LaneTabHeader、target 目录及单活动内容容器；删除旧 LANE 下拉工具栏。Direct 冷摘要按不可变源后台扫描一次，SubVoice 页元数据保存精确记录数，编辑后用实际 old/new root 更新；未知显示 Counting、读取失败明确 unavailable，不冒充 0。普通视图状态仅在会话中保持，Format 4 和音乐语义不变。

实际测试与预算详见 [A2b 报告](../../Midora-A2b-Instrument-Changes-and-Lane-Tabs-Implementation-2026-09-14.md)。AUTO-LANE-01／02／04 已有三宿主 Tab 结构、精确计数、独立轴、隐藏重开、显式导航、选择不隐式导航、受控 DPI 的自动证据。AUTO-LANE-03／05／06 已有部分更新／取消／卸载及数据层大样本证据；没有声称随机命令全部组合、真实 UI 帧时序 p95/p99、百万包装与全部 I/O 故障注入已完成。

人工状态：LANE-01～06 全部通过，含 LANE-02 及五项交互返修。根因、修复、精简复验和用户原答见 [A2b 报告 §8](../../Midora-A2b-Instrument-Changes-and-Lane-Tabs-Implementation-2026-09-14.md#8-2026-09-15-验收返修)。本次按用户要求记录、提交／推送；R12／R29 与 A3 没有提前实施，不本地发布。未穷尽的工程验证门不因人工通过而更改。
