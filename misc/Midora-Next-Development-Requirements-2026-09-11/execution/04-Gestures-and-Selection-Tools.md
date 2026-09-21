# A3：统一手势、编辑指针与 Selection 浮动工具

日期：2026-09-14；2026-09-15 实施、返修及最终验收。状态：**UAT-A3-01～10、R01～R06 全部已获用户验收通过** 。实际自动验证、规模测量与未跑项见 [A3 实施记录](../../Midora-A3-Gestures-Implementation-2026-09-15.md)，最终原答见其 §9；本次按用户要求记录、提交和推送，不本地发布或启动下一阶段。

主题：P1 **R18、R19、R21** ，关联 P2 **R20** 。执行与共同验证规则见 [执行总索引](00-Execution-Plan.md)。以下 9 项是内部工程分工，整合后只交 **一轮 UAT-A3** ，不把内部任务数变成人工验收轮数。

## 1. 原需求、已定规则与 trace

原需求及调查见 [01 §3](../01-Interaction-and-Display.md)；原答见 [04：D-UI01.a/b/c/d、D-UI02、D-UI04、D-UI09、D-STATE03.e](../04-Decisions-and-Preparation.md)，当前归并见 [05 §2.1及Q2汇总](../05-Decision-Summary-and-Q2-2026-09-14.md)。Q2已明确保留Draw，不能按被撤回的Q1文字删除Draw或拆两套主工具；当前没有必须追加Q3。

| 维度 | 执行合同 |
|---|---|
| 输入 | frozen owner/source revision/Selection revision/target/container/tick、Pointer Down世界坐标、原有修饰键规则与已批准的主ToolMode/绘线形态。 |
| 输出 | 框选与指针只变会话状态；菜单/浮动按钮复用既有command；正式数据编辑经验证、编译、canonical再供消费者，不因新UI重解释对象语义。 |
| 边界 | 可编辑内容区与ruler、Pitch Ruler、header/brace、只读All Tracks分离；Snap、time lock、对象合法值、collision、隐藏内容和protected object规则保持。 |
| 失败 | Escape、失捕获、切Tab/卸载、owner失效、source/selection revision失效、新手势替代，必须取消旧请求/预览/菜单候选；迟到结果零发布。 |
| 诊断/反馈 | Right Up不故意等双击窗口；冷命中未就绪立即出现不可执行“定位中…”菜单框架，不能用上次target。Invalid操作明确禁用/提示，不降级成另一命令。 |
| 持久化 | A3不私写旧schema。共享主ToolMode与独立事件形态接入B的Track profile/显式保存：同Track同步，不同Track隔离；SubVoice独立，Arrangement/Conductor各自状态。未交付B前的会话范围和未恢复项须明确报告。 |
| 运行时 | capture、pending命中、框选、自动滚屏、浮动位置与预览均有明确owner和终止条件；不进入音乐Undo、canonical或音频缓存身份。 |
| 非目标 | 不新增沿线画Note/Segment、不使Inst.固定y点可画线、不扩只读All Tracks编辑能力、不恢复被禁Time Range、不重写工具表达式/音乐命令，不实施R08全局图标或R09完整键表。 |

SRS依据：[§20.1～4、§20.5、§20.7、§20.12、§20.15](../../Midora-SRS-Initial-Release-v0.1/20-Common-Interaction-Validation-and-UI-Acceptance.md)、[§18.2、§18.4、§18.7](../../Midora-SRS-Initial-Release-v0.1/18-Editing-Workspaces-and-Editors.md)、[INV-075、077、083、095～100、104～112](../../Midora-SRS-Initial-Release-v0.1/22-Requirement-Locator-and-Cross-System-Invariants.md)。2026-09-15 已同步 §20.7.9.1/INV-097（取消固定300ms右双击）、§20.4.12/INV-098（跨模式浮动工具）和 §18.2.5/§18.7/§20.4.12.1（左键绘线子形态）；新增 §20.4.12.2 与 ADR-UI-045。旧记录只作为历史保留。

### 1.1 固定的交互表

| 表面/入口 | 目标行为 | 不得丢失的既有能力 |
|---|---|---|
| Arrangement Segment内容、三类Piano、数值事件/参数/Tempo | 右拖超过既有drag threshold即框选；未拖动Right Up打开冻结target菜单，无故意双击延迟 | 框选方向/Snap/修饰键、空白与已选/未选对象菜单目标、有限后台命中 |
| Velocity | 同一右拖框选，但选择对象是对应Note | Piano/List选择同步；左键命中单柱只改单Note；数值绘线不改变Note Tick/Gate/Key |
| 适用事件/参数/Velocity/Tempo绘制 | 共享Draw下，独立Free/Line/Horizontal纯图标组控制左拖；非Draw禁用子组 | 原采样密度、选区过滤、碰撞、Shift锁Tick/单点、Alt强制当前形态；不是把Logical参数/Tempo变成连续曲线 |
| Piano/Segment普通内容 | 保留共享Draw/Select/Split/Erase主工具 | 事件形态不改变创建/Move/左右Resize；D进Draw但不重置Line/Horizontal |
| 顶部时间尺 | Ctrl+左单击只设蓝色Edit Cursor并保留选择；plain单击仍设Playback Cursor | 内容区Ctrl多选/Copy Drag不变；不在SubVoice及底部ruler恢复Time Range |
| 有效兼容Selection | 浮动工具跨模式显示，逐命令检查能力 | Move、左右Resize、Follow/Pin/grip、全规模有效delta与对象预览；不能因不能Move就隐藏可用Delete |
| 捕获中的Move/Resize | 视口外继续按世界坐标计算；静止在边缘外也持续滚屏/更新delta | Snap/边界后的预览=提交；Note Move 删除越界对象、Copy 只丢弃越界副本且保留原对象，事件值域仍共同 Clamp（2026-09-15 用户返修要求） |
| 排除区 | header/brace/琴键/ruler/All Tracks保持各自命中规则；浮动框独占本次命中 | Pitch Ruler无右键菜单、不向下穿透；中键Pan保留，已有capture时不建立竞争手势 |

图标固定：Free沿用原Draw图标、Line=`LineFilled`、Horizontal=`SubtractFilled`。完整新增浮动命令/图标见§3.1，不重新交用户选型。

### 1.2 依赖与并行方式

- [A1](01-Common-UI-and-Editing.md)不被本主题阻塞；A3迁移绘线时回归R14模板外创建与R13 scalar共同饱和，不复制两套采样/提交实现。
- R20按钮只适配已存在命令能力；R27包装上线后按其已批准对象能力加入矩阵，不因A3提前开放包装不支持的移调/纵移/Note Split等操作。
- 工具状态所有权与B复用同一字段合同；B的格式/恢复验证可后置，不将“所有工具已保存恢复”混入A3局部交付结论，也不让A3偷偷新增presentation字段。
- R21若提前由A1完成，T-GEST-07复用该证据并只补A3集成差异，不要求用户重复验收同一修复。

## 2. 源码基线与已核实的实施差异

源码调查原始路径见原01；本轮以干净 `e4db96b` 为基线。以下区分实施前原因与当前实现：

- 原 drag 使用 viewport 限幅 lane，且滚屏主要由 MouseMove 驱动；现在普通/浮动操作共享世界 tick/lane/value 差值与 capture 内 33ms 边缘调度。真正 HwndSource/capture 测试已验证鼠标静止仍更新、选择失效立即停止。
- Piano 与下部 Lane 继续共用主 ToolMode，新增 Free/Line/Horizontal 会话形态；旧右键绘线和右双击等待已移除。D/S/E 语义不变。
- 顶部 ruler 已新增 Ctrl+左单击 Edit Cursor 分支；plain 保留 Playback Cursor，下部 ruler/SubVoice 不恢复 Time Range。
- 框选键表差异已消歧：**ADR-UI-023** 历史决定明确为无修饰 Replace、Ctrl/Shift Add、Alt Remove、Ctrl+Alt Toggle；调用路径与此一致。已修正 SRS 旧简表，没有另选新键义，没有改写用户原答。
- 新增 `TimelineA3GestureTests`、`A3GestureIntegrationTests`，并重跑 Presentation/Desktop 全套。实际计数、命令和失败修正过程见实施记录；没有将 9KX2 整机压力或实机多 DPI 写成已通过。

## 3. 内部工程任务

T-GEST-01～07 的实现已完成；T-GEST-08～09 的集成、自动回归、报告与 UAT 包已交付并获用户验收通过。表中的完整压力/平台组合仍是验证目标，**不是全部已跑** ；逐项实际证据及缺测以实施记录为准，人工通过不覆盖未跑工程门。通用验证要求见 [执行总索引](00-Execution-Plan.md)。

| ID | R／D | 前置 | 工作与完成证据 | 自动正确性／性能验证 |
|---|---|---|---|---|
| T-GEST-01 | R18～21；D-UI01.a～d/02/04/09 | SRS、原答、实际源码/测试/工作区复核 | 建立表面×按钮×修饰键×模式×目标能力表、取消状态图与规格替换清单；先核对上述框选键表文码差异的宿主调用及后续专项，不能以源码代替已批准合同。证据：现状对照、差异处理依据、回归样本及测试映射；未消歧键位不得作为既定新语义实施。 | 给旧右双击/右绘线及command能力建立可比较基线，区分“锁现状”与“验证规格”的测试；逐表确认无重复capture、无UI同步冷页读、无新业务规则。 |
| T-GEST-02 | R18；D-UI01.b/c | T-GEST-01 | 统一Right Down冻结、未拖Right Up菜单、过阈值取消菜单的状态机；移除双击切工具及人为等待；冷命中显示禁用框架并只填冻结target。证据：按事件顺序的状态/目标trace及取消结果。 | 已选/未选/空白、冷/热页、连续右击、第二手势、菜单关闭、Escape/失捕获/切Tab/owner删除/revision变化；晚命中不能重开旧菜单或改选择。测冷命中UI停顿和后台队列/取消，不追求同步完成冷页。 |
| T-GEST-03 | R18；D-UI01.b | T-GEST-02 | 右拖接入各可编辑内容区框选，Velocity映射Note；复用现有Replace/Add/Remove/Toggle与方向化Snap端点；排除区消费或保持自身处理。证据：各宿主命中/选择与原选择结果对照。 | 双向拖、刚过/未过阈值、修饰键及组合、跨页/空白/密集重复、播放仅选择；ruler/Pitch Ruler/header/brace/浮动工具不穿透，All Tracks不变为可编辑。百万选择只用revision-bound分页地址，不物化全量ID。 |
| T-GEST-04 | R18；D-UI01.a/d、D-STATE03.e | T-GEST-01；与T-GEST-02/03可并行 | 保留共享主ToolMode，新增独立事件绘线形态组；迁移旧直线/水平线到当前形态左拖，非Draw禁用但记忆形态。证据：toolbar绑定/图标、D/S/E返回状态、每形态采样与既有结果等价。 | Free/Line/Horizontal、Alt强制、Shift锁Tick/单点、单柱direct-hit、选择过滤、Snap开关与逐tick、正反向；Note/Segment/Inst.不新增绘线；R14模板外创建/R13Clamp回归。大型trace保持有界准备与一次Undo。 |
| T-GEST-05 | R19；D-UI02 | T-GEST-01/02 | 顶部时间尺Ctrl+左单击设置Edit Cursor且保持Selection，plain定位Playback Cursor；补Tooltip/Help和焦点安全路由。证据：三类坐标上下文的指针前后及选择不变记录。 | local/project/template映射、Snap/tick0、点击与拖动阈值、内容区Ctrl不被抢、Time Range禁区不恢复；输入框/IME/菜单/Modal/只读场景、播放/任务锁、模态关闭焦点。只改指针overlay，不重建音符tile或全源扫描。 |
| T-GEST-06 | R20；D-UI04 | T-GEST-01 | 建立菜单/快捷键/浮动按钮共用能力adapter，新增全部13项常驻按钮与指定图标，跨模式显示；按同质/共同兼容/显式类型子菜单处理混合选择。证据：完整命令×对象类型×锁定状态矩阵与布局样本。 | 逐按钮对照既有入口结果/一次Undo/选择后态；不支持Move但支持Delete仍显示可用动作；Opaque/enum/Conductor受保护事件和混合Note/Event不静默跳过。能力查询有界，不为每次hover遍历整个Selection。 |
| T-GEST-07 | R21及R20；D-UI09/04 | T-GEST-01；复用A1若已提前完成 | capture期间用冻结世界坐标计算delta；独立于MouseMove的持续边缘滚屏更新，回到内部/结束/取消立即停止；普通与浮动Move/Resize共用服务。证据：鼠标静止边缘外的viewport、有效delta、预览与最终结果trace。 | 上下左右/四角/返回、不同zoom/DPI、Ctrl在Down与中途变化、time lock、Snap、不同长度；Note普通Move越界删除/Copy只丢弃越界副本、Point值域Clamp/Segment适用边界。停止/失捕获/owner失效后无残留更新，冷区首内容/分配/队列/关闭释放按公共门。 |
| T-GEST-08 | R18/20/21；D-UI01.b/04/09 | T-GEST-03/04/06/07 | 串接同一Selection冻结、Move/Resize预览和正式command；保留Follow/Pin/grip及左右Resize。证据：小规模矢量与大规模raster预览同结果、实际生效delta、混合选择子菜单完整前后选择。 | 1/10/40万/百万对象，普通/浮动/复制、Snap/Clamp、碰撞与未触及重复、cancel/revision/I/O失败零发布；Properties/工具Dialog关闭后焦点恢复。测冷热预览、提交、p50/p95/p99、UI最长停顿/内存/GC和资源上限，不能退化仅delta或逐对象WPF。 |
| T-GEST-09 | R18～21；上述全部D | T-GEST-02～08；相关A1接口可用 | 汇总全宿主回归、规格/帮助/图标notices、目标能力和取消证据；给出A3与B的会话/保存边界、遗留风险及一轮UAT包。证据：R/D覆盖表、运行命令/结果、性能基线比较、未跑项与UAT-A3记录。 | 重新跑公共构建和相关自动集；禁用/播放/模态/高DPI/小窗口矩阵；确认没有新音乐状态、隐藏后台扫描、format变更或已取消右双击残留。未完成B时不虚报跨重启恢复通过。 |

### 3.1 全部新增常驻浮动工具

保留Move、ResizeStart、ResizeEnd、Follow/Pin与grip；Point仍不提供Resize。以下14项均常驻（首次交付13项，2026-09-15 返修增补 Deselect All），不退回“只保留前三项，其余全藏更多菜单”。不适用项按正式能力禁用或使用明确类型子菜单；小窗口排布必须保持可达，不以遮挡为由静默删项。

| 新增命令 | 固定图标 |
|---|---|
| Copy | `CopyRegular` |
| Cut | `CutRegular` |
| Delete | `DeleteRegular` |
| 左右翻转 | `FlipHorizontalRegular` |
| 上下翻转 | `FlipVerticalRegular` |
| Scale | `ScaleFitRegular` |
| 移调 | `ArrowMaximizeVerticalRegular` |
| Batch Edit | `DocumentSettingsRegular` |
| Humanize／类人化 | `AnimalPawPrintRegular` |
| Split | `SplitVerticalRegular` |
| Join | `SquareDovetailJointRegular` |
| Quantize／量化 | `TableMoveLeftRegular` |
| Properties | `SettingsCogMultipleRegular` |
| Deselect All（2026-09-15 增补） | `SelectObjectSkewDismissRegular` |

同一命令菜单入口复用指定图标；这是局部定案，不是R08全局图标准备完成。新Geometry继续沿用已批准Fluent体系，并在实际新增图标时更新既有notices。

### 3.2 目标冻结、修饰键与取消门

每个异步命中/菜单/框选/编辑必须携带来源owner、source/selection revision和请求身份；新手势、target失效、菜单关闭或Surface卸载后的旧结果不得重新夺取目标。菜单不能从hover或后台投影读一个变化中的目标。

复制意图及操作类型按Pointer Down冻结，拖动中按下/释放Ctrl不得临时从Move切Copy；Ctrl+Move只有既有Copy Drag能力时合法，Resize不隐式复制。框选按已批准修饰键政策建立测试，§2文码差异已依据历史决定核清；不同手势不能把同一个modifier误解释为另一命令。中键Pan与已有capture互斥，浮动grip/按钮不能穿透建立背景框选。

有效delta必须经过Snap、time lock、最小长度、值域/合法Tick以及正式对象规则；viewport只控制画面裁切，不能成为pointer世界delta的错误上界。鼠标静止于边缘外的滚屏更新属于UI交互调度，不承担任何音频/MIDI时序。

## 4. 统一验证与一轮人工验收

开发自动覆盖Arrangement、Logical/MIDI/SubVoice三种Piano、Velocity、数值Event/Parameter、Tempo及其他Conductor lane的适用/禁用分支；R18不扩大All Tracks。实现前在同机器/样本建立性能基线与可接受退化，按 [公共验证门](00-Execution-Plan.md) 记录冷暖耗时分布、UI停顿、预览就绪、内存/GC、队列和取消/释放。本计划不预设未经测量的毫秒或百分比收益。

人工使用开发者准备的少量代表场景和录制/截图证据，只确认手感、菜单可发现性、布局和操作反馈。全modifier排列、百万对象、竞态、边界溢出、每个按钮的精确命令等价性由自动矩阵承担，不要求用户逐一穷举。B尚未完成时，保存重开的联合验证留在B对应轮，不重复阻塞本轮。

### UAT-A3：手势与浮动工具

可继续打开 `.tmp/uat/a2b/A2b-Lanes-and-Instruments.midora`：已有 MIDI/Logical Segment、SubVoice、CC1/7/10/11/64 与 Inst. 点，无需重建工程。Tempo 检查可在其 Conductor 中加两三个点，或使用自己的带 Tempo 项目；大型选区手感使用已有 9KX2。以下为观察清单，不要求逐个排列全部修饰键。

- [x] **UAT-A3-01（R18）** 对已选、未选和空白对象各右单击，松开即出现正确菜单；连续右击不再切工具。冷数据演示先显示不可执行“定位中…”，随后仍针对最初点击对象，不误用旧目标。
- [x] **UAT-A3-02（R18）** 在预备工程的Arrangement、三类Piano及数值Lane代表场景右拖框选；松开只选对象不弹菜单。Velocity框选同步选择对应Note，Shift追加/Alt减选等沿用已有手感，琴键尺/轨头不穿透框选。
- [x] **UAT-A3-03（R18）** 在事件/参数/Velocity/Tempo代表区域选择Free、Line、Horizontal后左拖，三形态效果可分辨；已有点/柱仍可直接编辑，Alt可强制当前形态，Shift锁Tick仍有效，不产生斜线Tempo语义。
- [x] **UAT-A3-04（R18）** 选Line后切Select，形态组禁用；按D回Draw仍为Line。主Piano与下部Lane共享主工具，事件形态不改变上方Note/Segment创建或Resize。
- [x] **UAT-A3-05（R19）** 保留一组选中对象，Ctrl+左单击顶部尺，只移动蓝色编辑指针且选择保留；plain尺单击仍移动播放指针；Select内容区普通左单击仍可清选。Tooltip能说明新定位入口。
- [x] **UAT-A3-06（R20）** 有效选择下切换Draw/Select等模式，浮动工具持续可见；全部13个新增按钮及指定图标齐全，现有Move/两端Resize/Follow/Pin/grip仍在，短窗口中不裁切或变得不可达。
- [x] **UAT-A3-07（R20）** 使用一项复制/删除、一项批量工具和Properties代表命令，结果与原入口一致；混合Note/Event选择时无静默跳过，不支持的按钮状态明确；取消Dialog无改动，关闭后键盘焦点回原Surface。
- [x] **UAT-A3-08（R21）** 用浮动Move拖到钢琴卷帘上/下边缘外并保持鼠标不动，画面继续滚动且delta更新；返回内部后行为连续，松开结果与预览一致，一次Undo恢复。开发证据同时展示普通拖动共用路径。
- [x] **UAT-A3-09（R20/21）** 比较普通Move与Ctrl+Move，并试左右Resize、Follow/Pin及grip；复制不改源、Resize不复制，工具不意外启动背景手势；大选择演示仍有对象预览及生效delta，不仅剩一行数字。
- [x] **UAT-A3-10（R18～21）** 拖动/框选中按Escape或切Tab后没有迟到提交、遗留菜单或持续滚屏；播放时仍可选择查看但编辑按钮禁用，文本框/菜单/Modal中的D/S/E不切换背景工具。

## 5. 收尾与风险记录（2026-09-15）

### 5.1 首轮人工验收及返修

用户原答：`除了 UAT-A3-09 外，其他均验收通过。`

首轮据此确认 UAT-A3-01～08、10 通过，09 转入返修。最终用户以 `验收通过。记录、提交、推送。` 确认返修及后续局部调整通过，因此下列 R01～R06 与补验 09 一并勾选；原始反馈及各轮自动验证仍保留，不改写历史结果。
用户本轮明确修改 Note Copy 边界：与普通 Move 共用未 clamp 的 pitch delta，丢弃越过 0–127 的目标副本，不删除源音符，不改变事件 value Clamp。T-GEST-07 旧 Copy 共同 Clamp 的 pitch 部分由此取代；时间和 Track 边界不变。

- [x] **UAT-A3-R01** 在三种钢琴卷帘、Arrangement、普通事件与 Inst. 选择对象，检查紧凑的 27 DIP 按钮、居中且平滑的 15 DIP 图标、最多三行按钮区；Pin 始终为 Pin 图标且保留背景切换，左右边界为 ArrowExportRtl/Ltr、Move 为 ArrowMove；窄/矮视图可内部滚动且全部工具可达。右键菜单的图标右侧不再裁切，图标与正文有明确间距。
- [x] **UAT-A3-R02** 逐个 hover 按钮（包括灰色禁用按钮），工具顶部立即稳定显示名称；不出现浮动 ToolTip，不移动按钮布局。第一行最后的 Deselect All 清空全部选择，不删除数据、不增加 Undo。
- [x] **UAT-A3-R03（补验 09）** 三类 Note 均选择接近 Key 0/127 的多个音符，以 Ctrl+Move 和 Ctrl+Alt+Move 向外拖：源音符保留，越界副本消失，合法幸存副本选中；全部越界不报错且清选。再检查 Undo/Redo、左右 Resize、Follow/Pin/grip 不穿透。
- [x] **UAT-A3-R04** 在普通事件/逻辑参数/SubVoice 事件上 Ctrl+Move 越过值域时仍整体 Clamp，不丢弃点；原已验收绘线、框选和大选择预览不回退。

返修按局部清单复核，没有重新要求整套 A3 人工验收。视觉要求先将原 22 DIP / 独立末行改为 16 DIP / 最多三行、Deselect All 放第一行末尾；随后收紧按钮留白、修正菜单图标裁切和浮动图标抗锯齿；最终定为 15 DIP 图标，并修订空白/标尺菜单。各轮自动证据见实施报告 §5～8，最终人工验收见 §9。

### 5.2 首次交付的工程记录

以下 R05/R06 为后续新增局部检查，现已随最终反馈确认通过（不改变之前用户原答或历史结果）：

- [x] **UAT-A3-R05** 在三种 Piano / 数值事件、Arrangement 与 Inst. 选择对象后，右击内容空白，完整批量菜单仍可用；取消选择后空白菜单回到容器命令。混合 Note/Event 仍使用类型子菜单，专用轨头/琴键语义不变。
- [x] **UAT-A3-R06** 连续在内容、顶部 Timeline 标尺、内容右击，菜单每次都靠近本次点击，不再向右下重复偏移；主 Piano 和下部 Velocity/Event 均检查一次。图标 15px 居中归 R01。

下列记录保留首次实现的范围；当前人工状态及返修差异以 §5.1 和实施报告 §9 为准。

- [实施记录](../../Midora-A3-Gestures-Implementation-2026-09-15.md) 已记录实际改动、自动测试、资源测量与缺测。
- R21 同时处理坐标限幅、capture 生命周期及持续边缘滚屏；冷菜单关闭使用 IsOpen 立即取消，避免 Closed 动画延迟期间迟到结果改选。
- 13 个命令全部保留，小窗口换行/内部滚动；真实主窗口模板已检查三种宿主、混合选择分组和空白菜单隔离，未将无 GUI 断言视为实机视觉验收。
- 工具profile的保存恢复归B；本主题负责清楚的字段和会话接口，不替B冻结schema版本。新UI不得把捕获、当前选择或工具预览变成音乐源数据。
