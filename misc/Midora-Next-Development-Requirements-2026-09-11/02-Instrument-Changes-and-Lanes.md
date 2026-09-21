# 乐器变化点、Lane Tabs 与事件呈现

覆盖 R27（P0）、R28（P1）、R12/R29（P2）；关联 R24/26、R06/07。源码审计基线 `0bb9670`；本文同步[决策与问答主文档](04-Decisions-and-Preparation.md)中的 Q1／Q2 用户回答，尚未修改规格和产品代码。

2026-09-14 Q1／Q2 归并：`04` 是本专题唯一问答入口，集中保存原问题、推荐、用户原回答、补充问答及确认状态。本文保留需求、审计事实与技术推导，不另开答复记录。D-LANE01.g 已确认普通 Tab 隐藏及全部 target 目录，D-VAL01.d 已确认显示域仅作用于编辑显示／数值编辑工具，Mapping 保持原语义且须在 Help 中说明区别；D-UI01.d 已撤回删除 Draw 的方案。上述问题不再作为待答项，本专题无新增 Q3。文档归并不代表产品已经实施，也不自动修改 SRS。

## 1. 目标与不能误改的底层能力

核心目标是让已存在的 Instrument Catalog 真正帮助选音色：在 MIDI Segment / SubVoice 直接按 Bank、Program名称选取并试听，而不是只在全局配置名称后仍四处填数字。

用户明确要求保留 Pure MIDI 独立 CC0、CC32、Program Change 的原始编辑和导入兼容。Q1 已确定新“乐器变化点”为显式创建的持久编辑包装；MIDI 导入不创建包装，也不建立相邻 Bank／PC 的只读投影，导入形成的项目中 Inst. 内容为空。导入事件仍在各自原始 Lane 编辑，用户之后可以显式添加包装。

不变事项：

- Project / canonical / MIDI 输出仍用正式数值与顺序；Catalog名称不成为资源身份或编译输入。
- 不依赖某个用户安装的Catalog / SoundFont才能打开项目；缺失只数值回退。
- 非音符事件精确碰撞仍遵守正式编辑合同；导入已有重复在未被相关编辑命中时保留。
- Bank MSB/LSB、Program以及SubVoice精确Mapping目标的既有能力不能在合并UI时丢掉。
- 新试听必须有独立所有权，不阻碍编辑、不错误停止正常项目播放。

## 2. 源码现状与可复用边界

下表路径相对仓库根；行号冻结于审计基线。

| 能力 | 来源 | 已确认现状 / 限制 |
|---|---|---|
| Bank/Program名称 | `src/midora-core/Midora.Application/InstrumentCatalogResolver.cs:108/155` | 有正式数值→名称及来源resolver；没有统一可选 / 试听弹窗 |
| Catalog规模 | `InstrumentCatalog.cs:405` | 上限256 Profile、每Profile16,384 Bank、每Bank128 Program、文件64MiB；不能穷举所有三元组生成WPF项 |
| 现有Initial State | `src/midora-desktop/Midora.Desktop/PresentationModels.cs:3727/3749`；`ObjectPropertiesProjection.cs:1821/1866` | 三个独立数值字段，部分Properties有辅助只读名称；需要完整三值才解析 |
| 旧验收前提 | `Midora.Desktop.Tests/InstrumentCatalogPropertiesProjectionTests.cs:88` | 有SubVoice Program Properties只保留数值的测试，新需求应有意识替换该前提 |
| 琴键表面 | `Midora.Desktop.Presentation/Controls/PianoKeyboardSurface.cs:17/59/71` | 可复用Key/Velocity事件及键范围；滚动宿主、音色选择、试听调度不包含在内 |
| 原始列表 | `Midora.Desktop/TimelineObjectListSource.cs:11/110/135` | 一正式源对象一行；已有稳定ID、formal order、分页目录；包装行需新投影与选择映射 |
| 现有Lane | `MainWindow.xaml:684/939`；`PresentationModels.cs:2397/2484` | LANE下拉切目标；内容发现带后台revision gate，默认kind/number排序，并非用户Tab顺序 |
| SubVoice Lane命令 | `src/midora-core/Midora.Application/ProjectSubVoiceEventLaneEditCommands.cs:7/161` | 创建可能建正式SubVoiceEventMapping owner；删除可能删数据和owner，不等于关闭Tab |
| Tempo阶梯参考 | `Midora.Desktop.Presentation/Rendering/TimelineTempoTileRasterizer.cs:10/43/90` | 有前驱、first/last/min/max、水平线、竖跳和取消；绑定Conductor source，不能不改接口直接用于全部事件 |

### 2.1 两个新增工作前必须核实的技术风险

**事件索引风险。** `TimelineEventTargetIndex.cs:31–57/65–112` 会按修订全源扫描、建立目标List、每点数组、多组prefix数组和树；同Tick按ID排序，point未携带正式order。因此不是可直接宣布“百万级有界且语义正确”的状态索引。新投影应使用分页摘要 / 有界范围索引，保留formal order，不能为阶梯线再常驻一份全量事件图。

**试听风险。** `DesktopSessionController.cs:971` / `Midora.Playback/PlaybackController.cs:334` 的 BeginPitchAudition只收pitch/velocity。`Midora.Audio.Bass.Worker/Program.cs:1713–1754/1812–1938` 的裸音高路径直接SOUNDOFF/NOTE、voices有固定500值，其render source中未见Master/Limiter处理。这里是静态审计风险，不是本轮已证实的可听BUG；不能把它描述为“只加三条事件即可安全复用”的完整方案。实施R27前需核对完整调用链，并对新的试听路线做正式安全验证。

## 3. R27：已确认数据边界与实现设计

### 3.1 数据唯一来源与包装身份（D-IN01.a/b 已确认）

**用户决定：** 保留 raw 事件作为唯一音乐事实，显式创建的组合拥有稳定关联；UI 展示包装点。不新增由 Compiler 解释的 `InstrumentChange` 音乐事件。

| Owner | 新完整包装所关联的正式成员 | 不改变的能力 |
|---|---|---|
| Pure MIDI Segment | CC0、CC32、PC三个稳定事件ID | 原始事件类型、正式order、独立编辑、opaque、导入重复 |
| SubVoice | 一个具有MSB/LSB的Bank Event、一个Program Event的稳定ID | Bank.MSB、Bank.LSB、Program.Value精确Mapping目标及既有Loop行为 |

不能用列表起始ordinal、连续长度、Tick或名称充当身份。分页、重排、碰撞和Undo均可能改变序列位置；成员ID可稳定重新解析，wrapper自身也需稳定身份以支持选中 / Properties。

此关联不复制音符 / 事件内容，不建立第二份可独立修改的音乐数值。创建 / 变换 / 删除包装只通过正式事务处理成员，保留source trace。小列表投影也不能绕开这一事务。

**持久化决定（D-IN01.a/b）：** 包装自身拥有稳定 ID，成员关联作为严格版本化的持久编辑组织数据，随正式编辑进入 Undo/Redo 并保存；不是允许随 presentation 丢失的临时分组。用户接受必要的编辑组织数据／Project Format 扩展，新版本持续读取旧项目，迁移保存沿用确认与永久旧版副本流程；不承诺旧软件能读取新增正式源格式。具体版本号、schema 与 source trace 契约仍需实施前设计，不得直接塞入冻结的旧 schema。仅建立或展示关联不改变 raw 播放顺序，正式编辑则按获批事务改变成员。

### 3.2 导入、同Tick与共享Root的底线

- D-IN01.c 已明确否决导入 PC 显示投影。导入不转换 Bank／PC、不读取其前驱状态生成 Inst. 内容，也不因同 Tick 邻接就建立关联。Inst. 只显示用户在 Midora 中显式创建的包装，浏览导入数据零源变更。
- D-IN01.d 已不适用：不存在“编辑／删除导入 PC 投影”的新入口，导入 Bank／PC 继续经原始 Lane 独立编辑。任意 raw 选择的显式组合也不是本轮已批准操作。新 Tab 不负责导入发现或投影，不等于正式创建包装时可以忽略与已有 raw 的精确碰撞。
- 组合创建按正式 Bank→PC 顺序原子发布，精确命中键执行既有 later-wins 覆盖；D-IN02.b 已确认新建组合位于本 Track 同 Tick 对应 NoteOn 之前，原始 Lane 编辑导入 PC 保留其原顺序位置。同 Root 较早 Track 同 Tick 的 Note 不受后面 Track 新消息追溯影响。同 Tick 多个包装被操作时，使用冻结 formal order，不能按 HashSet 或 ID 排序决定结果。
- D-IN02.a 已确认仅改数值、同 owner／同 Tick／正确 target 关系仍完整时保留包装并刷新名称；部分移动、删除、改 target 或覆盖使最终结构不完整时才解组，保留剩余 raw 且不补回成员。必须按整个原子事务的最终态判断，整组移动过程不能误解组。
- List的包装行和raw行需避免对同一成员重复显示/重复选中；原raw Lane仍可访问。选中wrapper映射到成员ID集合，数量提示需区分“一处乐器变化”与“三条MIDI消息”。

跨Track状态是另一个必须验证的边界。SRS §23.4.2顺序为绝对Tick→全局Track顺序→Track内事件顺序，同Root子Segment结束不清共享Bank/Program。

已有 `PureMidiPagedCanonicalSource.cs:734/810` 及 `PureMidiRangeBoundaries.cs:29–60` 能按共享生命周期恢复目标末值，但没有直接提供“待生效Bank”和“最近PC实际采用Bank”的双状态接口。需测试：

```text
Bank A → PC P → Bank B（尚未下一次PC）→ 从中途开始 / 查询名称
```

不能简单把三个“最后数值”拼成“当前发声音色”。新 UI 只显示该包装自己写入的数值与名称，本批不增加导入 PC 或“此刻有效音色”的状态投影。以上序列仍作为既有状态恢复的技术验证场景，不因此扩大 R27 的显示范围；这里没有新证据允许本轮直接宣布范围播放错误，更不能借 UI 重新解释音频。

### 3.3 包装编辑覆盖矩阵

实施前将下表固定到ADR，避免只做一个好看的点：

| 动作 | 必须处理 |
|---|---|
| 创建 / Properties / 双击 | draft中选Bank/PC，OK一次发布全部成员；Cancel不分配正式内容 |
| 水平移动 / Ctrl复制 | 整个包装同Tick移动；目标碰撞按批准的成员覆盖策略原子归并 |
| 删除 / 剪切 / 粘贴 | 包装与成员关系同步，有限内存、有取消、目标选择、Undo完整还原 |
| raw成员单独修改 | 按D-IN02.a最终结构决定保留或解除；禁止悬空关联、重复显示、静默复活成员 |
| Split / owner转换 / 删除owner | 重映射或解除关联的规则明确；不能指向别的Segment或保留源ID悬空 |
| 多选 / 混合对象 | 既有类型子菜单机制；D-IN02.c 已确认 Copy/Cut/Paste/Delete/水平 Move 及复制/水平翻转/Scale/量化/多选 Properties，不提供纵移、移调、Note Split/Join；普通单字段 Point Value Batch 不直接套三元组，不新增三字段表达式批改 |
| Save / Reopen / 旧项目 | 不丢旧Bank缺分量、独立PC、Mapping；raw声音 / 顺序持续等价 |

不要求这张表所有动作都在A2a首个切片交付，但A2b整体验收前必须每项有实现或用户明确批准的限制。

## 4. R27：交互与弹窗

### 4.1 Instrument Changes 视图

- 仅 MIDI Segment / SubVoice；Logical Segment 没有这个 Tab。Tab 头固定为 `Inst.`，位于 `Vel.` 之后且不可关闭；无包装时仍保留空视图，MIDI 导入不向其中填入任何投影。
- 一个包装点对应一次乐器变化；y 固定中线，x=Tick。Draw 单击创建／选中，不通过拖线生成很多乐器弹窗。D-UI01.d 已确认保留共享 Draw 工具及原图标，Free／Line／Horizontal 是事件数值区额外的绘制形态按钮组，不替换 Draw，也不使 Inst. 产生沿线创建行为。
- 在点的上或下方择有空间一侧显示带边框标签，包含Bank、PC值和名称；密集点使用有限标签布局 / LOD，命中依据正式数据，不从bitmap反推对象。
- 对象List合并为特殊行；双击行 / 点、右键Properties复用同一个选择器。远处数据按页准备，不整表构建字符串。
- R18实施后右拖负责框选，不能再为此Tab保留暗含的右拖创建。

### 4.2 统一音色选择器

用户已指定：

- Bank / Program两个列表，每行包含值和名称；支持虚拟化，不展开所有组合。
- 独立可输入的Bank MSB、Bank LSB、Program数值框；列表选择与有效数值即时同步，无名称时仍可正常指定数值。
- 可水平滚动的钢琴键盘，按键按其自身Key/Velocity试听；默认自动试听Key60、Velocity100，可修改。
- 列表选项变化触发自动试听。试听先停旧音，任何编辑/确认/取消/关闭/跳转都先终止该弹窗拥有的试听，不让用户重复操作一次才能编辑。
- 点 / List / Initial State共同使用该选择器；draft、Validate / 输入错误、OK / Cancel使用现有主题与统一按钮 / Enter / Esc / 焦点规则。

D-IN05.a～e 已确认：自动试听默认 **500ms** 短单音，可关闭，快速连续选择只保留最新请求；手按琴键走 held 生命周期，释放即结束 gate。自动试听开关、Key、Velocity 和时长保存为程序偏好，不进入 Project；首次 Key60／Velocity100。Bank／Program 列表优先展示现有 resolver 解析出的有效 Catalog，缺名称仍允许输入任意合法值。无 SoundFont 仍允许编辑，明确试听不可用。

试听必须冻结当前 Enabled SoundFonts、Bank 映射、Channel Mode、voices 配置，完成所需 preset 预载；不能用 Catalog 名称或 Profile 索引作为声音身份。D-IN05.b 已确认干净控制器状态的独立 preset audition，不套用当前时间线的 Expression、Pitch Bend、Mapping 等状态，也不声称完整复现编曲听感。D-IN05.c 已确认提供仅影响试听的 Melodic／Percussion 选择，以已知目标初始模式为初值，不改 Root 或写项目 SysEx；Root 初始 Mode 不能冒充后续 SysEx 改变后的活动 Mode。实施前明确临时受控 canonical 试听计划及 audition 合同，不得让项目文件提供自由 raw 命令绕过白名单。Master→Limiter、NoteOff、抢占、关闭、原生错误和工作线程资源门都要覆盖，正常项目播放不被试听抢停。

SRS §8.55.1 仍有 Program 显示 1–128、内部 0–127 的旧口径。D-IN04 已确认所有相关数值入口统一采用 0–127，标明 `Program (0–127)` 并配名称；实施前核对全部入口并同步更正该规格，不能只改新弹窗而继续混用一基 PC 与内部字节。本次只记录决定，未修改 SRS。

### 4.3 Initial State 与旧SubVoice

D-IN03.c 已确认范围仅为 Event Instrument 全局与 SubVoice Initial State，**不扩大到 Project Global Initial State** 。

用一行Instrument selector替代三行裸数值，但初始状态每字段的空值 / override / 继承是正式语义：

- 打开弹窗、只浏览Catalog再取消不得补0或显式固定继承值。
- 若只修改一个原有override，必须有方法保留其余继承状态；选择一个完整preset可以明确一次覆盖三个字段。
- 旧项目独立 Bank／PC、只有 MSB 或 LSB、scalar Mapping 依然可查看与编辑。D-IN03.a 已确认 SubVoice 普通新增以完整包装为主，保留高级 raw 入口；D-IN03.b 已确认高级区域保留逐字段继承／覆盖，仅选择完整音色时才覆盖三字段。
- 不删除 SubVoice 独立 Bank／PC 的维护能力，不能以“底层还在”为由让已有合法数据失去编辑入口。

## 5. R28：每目标一个 Lane Tab

用户已确定布局：原LANE标题、下拉框及通用Event/Parameter Tab退出；每个实际事件/参数一个Tab，名称作为Tab头。坐标、Snap、Snap值、Add入口搬到Tab头同一行的最右侧，内容区不再占第二行工具栏。按 D-UI01.d 保留 Piano／事件区域共享的 Draw 工具，在支持数值绘线的事件区域额外设置 Free／Line／Horizontal 三个纯图标按钮；非 Draw 模式下整组禁用，不拆成三个独立顶层工具。原 Draw 及已指定的形态图标继续保留，完整手势与工具状态归属见 `01`／`04`。

Tab行为：

1. `Vel.` 固定第一；适用的 `Inst.` 固定第二；两者常驻且不可关闭，即使没有内容。其他按用户视图顺序。
2. 头部溢出支持鼠标滚轮水平滚动、拖拽重排；最右下箭头按 D-LANE01.g 打开可搜索、虚拟化的全部 target 目录，包含已显示与已隐藏项。活动 Tab 键盘可达、长名称不裁按钮。工具区和溢出按钮宽度不足时需有最小布局规则。
3. Add 成功激活目标 Tab；导入只为实际涉及的普通 target 创建，不预建全 128 CC，也不生成 Instrument Changes 包装。
4. 每Tab独立纵向缩放 / value viewport；横向时间轴仍与同Workspace的Piano/Velocity同步。
5. 以owner+正式target身份识别，不用Tab文本或数组位置；重命名不丢状态，视图重排不改MIDI事件顺序/Mapping顺序。
6. 不为每个隐藏Tab常驻一个活跃Surface、全事件索引或定时器；只保留轻量状态并懒加载可见面板，快速切换丢弃旧revision任务。

### 5.1 常驻基础 Tab、普通 Tab 隐藏与目录（D-LANE01）

**已确认：** Q1 的 D-LANE01.a 仅先确认“删除数据／删除 owner 是独立命令”；Q2 的 D-LANE01.g 已进一步确认普通 Tab 可隐藏，关闭只改变视图，不删除正式数据／owner。D-LANE01.b 的 `Vel.`／`Inst.` 常驻不可关闭规则保持不变。

D-LANE01.c～f 已确认：显式 Add／Locate 才显示并激活目标，普通刷新／后台／Undo 不抢回隐藏 Tab，普通 Properties 不无故切换当前 Tab；首次普通 targets 按确定顺序排列，新建追加末尾并保留用户重排；切 Lane 不清旧选择；Piano 与底部 Event Snap 保持独立，各事件 target 共用 Event Snap、各自记忆纵轴、共享水平时间轴。目录点击显示并激活目标属于 D-LANE01.g 已确认的显式导航。

**用户疑问与已确认方案：** 用户担心隐藏后忘记有内容的 target，提出显示名称及事件数量的目录，并询问是否会增加昂贵的 O(N) 遍历。Q2 已同意按 owner＋target 维护派生计数／曲线存在摘要，在导入或页构建时统计，正式编辑按最终结果更新，Undo／Redo 同步还原；目录打开读取 K 个 target 摘要，而不是 N 条事件。没有现成摘要的旧数据首次仍可能需要有界、可取消的后台统计，未完成时显示“统计中”，不能伪装为 0。当前 §2.1 所述索引确实全源构建，实施时仍须改造，不能声称该方案已经实现或性能已经验证。

**目录行为（D-LANE01.g 已确认）：** 复用最右下箭头作为可搜索、虚拟化的全部已有事件／参数 target 目录，显示名称、精确数量及显隐状态，点击显示并激活目标；不另建常驻重面板。无点但有 Value Curve／Mapping owner 的目标也列出并使用明确标记，避免遗漏。包装数与成员消息数应区分，不能将不同视图的重复投影加成事件总数。“没有可见 Tab”不等于“没有正式数据”。

### 5.2 与状态持久化衔接

R07按Track共享的是通用编辑profile；**Lane存在性仍由该Segment内容/owner决定** ，不能在另一个Segment里凭共享Tab创建新的参数或事件。

D-STATE03.a 已确认 **Lane 纵轴、显隐、排列与活动 target 按 Segment 独立** ，不因同 Track 其他 Segment 实际内容不同而联动重置；基础 Tab 不存在隐藏状态。Track 只共享附表 B 明确的通用 profile；其中主 Piano Snap 和底部 Event Snap 是两套独立设置，底部数值 targets 共用 Event Snap，不因工具栏合并而变成一套。SubVoice 仍独立。R28 先实现会话内稳定状态，后续独立 schema 保存，不序列化整个 Tab VM；完整状态白名单以 `04` 为准。

## 6. R12：友好CC显示

R12 保持正式 MIDI 源数据、canonical 和导出的原始值域不变；Q1 另已要求编辑表达式也采用显示值，因此不再描述为“只改文字、不改工具输入输出”。推荐中央 `MidiValueDisplayDescriptor` 定义正式 target→显示变换、逆变换、单位、格式与原合法范围；各视图和编辑工具不能各自减 64。

Pan示例为 `display = raw - 64`，边界raw0/64/127显示−64/0/63；Cutoff应精确指具体CC（如CC74），不暗示“0”是任何设备共同的物理Hz或声学中性点。

D-VAL01.a～c 已明确：白名单为 **CC10、CC71～78** ，统一显示 −64～63；覆盖 ruler、鼠标坐标、点／条 Tooltip、List、Properties 及适用 Initial State。相关入口完全不额外显示 raw，包括 Tooltip／辅助信息，也不增加 raw／display 开关。这一决定针对本组友好 CC 值，不取消 D-IN04 已确认的 Program 0～127 等其他数值入口。

D-VAL01.b 已要求编辑表达式采用显示值，并明确不要求兼容旧表达式／预设结果；Batch Edit、Batch Create／Generator 等编辑工具的相关值输入和输出应与界面一致，再按目标规则转换为正式 raw 值。Note、Logical Parameter 自有值域、时间、目标编号等不属于这些 CC 的偏移对象，不能机械减 64。该答复不等于取消所有旧 `.midora` 的读取能力。

**编辑层／数据层边界（D-VAL01.d 已确认）：** 显示域限定到编辑显示及数值编辑工具，不扩大到 Project Mapping Function 或整条音乐 Mapping 链。目标 CC 的属性、坐标、列表及 Batch Edit／Batch Create 等工具使用显示值，再转换为正式 MIDI 数值；Project Mapping 的 Function、内置／图形 Step、共享 accumulator、相关 Context、取整与 ABI 保持既有 raw 契约，不在函数入口减 64。加法 delta、乘法 factor、Envelope 因子及 Logical Parameter 自有范围不能被机械偏移。该边界已经定案，实施前只需落实编辑工具数值契约与必要的版本记录，不重新开放 Mapping 改域选项。

**Help 要求：** 必须明确说明“外侧编辑层”与“数据层”的区别，以及工具表达式和 Project Mapping 使用不同的数值契约。例：CC10 raw96 在界面显示 32，编辑工具的 `=p0*0.5` 得到显示 16、写入 raw80；Project Mapping 的 `value*0.5` 或内置 Multiply 0.5 仍按 raw96 计算，输出 raw48、界面显示 −16。Help 可解释这种编码关系，但不恢复普通编辑入口的 raw Tooltip／辅助值或全局 raw 显示开关；也不因此放弃旧 `.midora` 音乐读取。

测试raw↔display全边界、往返无损、Clamp、不适用目标不减64；分别识别Direct MIDI的14-bit raw与SubVoice signed PitchBend，按源编码显式转换、禁止重复偏移；Catalog字段与事件身份不转换。同操作由列表/图形/Properties进入结果一致。

## 7. R29：事件点 + 阶梯线

2026-09-17 后续明确修订：[D-STEP05](04-Decisions-and-Preparation.md#d-step05--全-lane-位置辅助线与程序级开关) 取代下面原 Q1 的适用范围／开关位置。现在除 Vel.／Inst. 外全部 Lane 均有位置辅助线（含 Bank／PC、命令 CC、opaque），opaque 使用既有固定 y、不解释 payload；开关统一放在程序级 Appearance、默认开启。不跨 owner、不补默认值、曲线不改等边界保留。下面内容保留原需求收敛的历史上下文；当前实施／复验见 [跟进记录](../Midora-A4a-Acceptance-Followup-2026-09-17.md)。

视觉形式为点、前值水平保持、变化Tick竖跳；不允许在两点间做线性声音插值。建议抽共享阶梯tile provider，复用Tempo的设备列first/last/min/max、前驱查询与后台取消思想，但不复制其特定Conductor模型。

范围与限制：

- Logical Parameter离散Step、MIDI/SubVoice状态型标量点适用。
- Velocity仍是NoteOn力度柱，不变成持续状态；InstrumentChanges固定y点；opaque Meta/SysEx不是数值线。
- D-STEP01 已确认 Bank／PC、CC120～127 等命令／结构目标不套普通数值保持线。SubVoice 既有 Value Curve／Envelope 不被本项强制变 Step；它们在正式绘制模式下保留原语义。
- D-STEP02 已确认仅表达当前 Lane 自己的显式记录：可视左边界查询同 owner 最近前驱，无前驱时从首显式点起线，不凭 Initial/default 或其他轨道构造线，不增加整个 Channel 的有效状态投影。D-STEP03 已确认 Segment 界外既有点仍可见／编辑，界外线与可听区间明确区分，不代表那里实际发声，不跨不相关 owner 接线；SubVoice 按模板边界处理。
- Pure MIDI同Tick允许多事件，首末取正式order；可视LOD合并只改像素、不删记录、不改变命中 / selection / export。
- 只画线，不为每点建WPF控件；线与点/选择层局部失效；全源扫描、全量prefix数组不作为默认百万级实现。
- D-STEP04 已确认线只辅助、不命中，仍拖点编辑；默认显示并提供关闭线的视图选项。D-STEP01～04 产品行为均已确认，共享 provider、索引和缓存的实现仍须验证。

验收：左边界前驱、无点/单点/同Tick多个点、跨Track共享状态标签不误导、半开边界、PB值域、稀疏长间隔、百万密集点、局部编辑Undo与缓存修订；原编辑采样密度/碰撞/快捷键不变。若状态基线需要更大canonical查询能力，先分阶段增加，不在UI线程临时编译。

## 8. 专题实施 / 验证门

建议A2a先做“已确认包装契约→统一选择器→Initial State / 单点创建→试听生命周期”的垂直切片；A2b覆盖全编辑 / List / Tabs / 冷热缓存。R12/R29可在A4接入同一target呈现基础。

自动门至少包含：

- raw 导入→浏览→保存→编译→导出序列保持；导入后 Inst. 为空且不触发 Bank／PC 关联投影；重复同 Tick、Bank/PC 缺分量、跨 Track 同 Root、NoteOn 夹杂、crop/相邻 Segment。
- wrapper创建/移动/删除、raw成员改变、同键碰撞、复制剪贴板、Split/转换/删除Owner、Undo/Redo、失效修订，均零部分提交。
- Initial State不改变未编辑override/继承，旧SubVoice的Bank/PC/Mapping可继续操作；新旧格式golden与source trace。
- Catalog缺失/损坏/disabled/orphan/override、数值fallback、配置热更新；仅浏览不扫描SF2、更不读sample或改Modified。
- 试听默认 500ms、程序偏好重开、快速换选、按住/释放、无 SoundFont、Percussion、关闭/取消/编辑抢占、旧任务迟到、音量/Limiter/预载/voices 一致。
- CC10／CC71～78 的图形、List、Properties、Initial State 与编辑工具显示域一致；不额外显示 raw；非白名单及目标身份不偏移。按 D-VAL01.d 回归 Project Mapping Function／内置 Step／ABI 的既有 raw 结果，并覆盖 Help 中两类表达式的差异示例，不能将已确认需求写成已通过验证。
- 普通 Tab 隐藏／目录重开、名称／精确数量／显隐、无点曲线与 Mapping owner、编辑碰撞及 Undo／Redo 后计数、冷摘要“统计中”／取消、关闭释放均须验证；确认目录不重复全事件扫描。Draw 与原图标保留、数值区三形态按钮组在非 Draw 模式禁用，Inst. 仍只单点创建。
- 目标很多但单目标很少、单目标百万、所有目标百万、长名称、多同Tick、首次冷页、切Tab/隐藏/关闭释放；阶段开始前固定可测性能预算。

本专题不得以“新点能发声”代替上述顺序、兼容性与资源测试。本次仅同步 Q1／Q2 已确认文档结论，未实施产品、未修改 SRS，也未运行构建、自动测试或 UI／音频验收；无新增 Q3。
