# 交互、显示与常规 UI 任务

关联：[总表与执行边界](00-Overview-and-Delivery-Plan.md)、[决策与问答主文档](04-Decisions-and-Preparation.md)。基线 `0bb9670`；以下“原因”未注明运行证据时均为源码判断，本轮不实施、不运行产品测试。

2026-09-14 决策归并：`04` 的 95 项 Q1 与 9 项 Q2 均已收到用户回答；本文按最新原答同步本专题的已确认行为，保留源码审计、技术候选和验证方案，不另建第二份用户答复记录。Q1／Q2 原问题、原推荐及用户原答仍由 `04` 保留；Q2 明确修订优先，不能继续沿用已撤回的三独立 Pen 或旧连续跟随范围。本专题目前无必须追加的 Q3 产品决定。已确认行为不等于已修改 SRS、已实施或已验收；本轮仅整理文档，不改产品代码、SRS 或发布物，不追加提交／推送，未运行性能或 UI 实测。

## 1. P0/P1 常规 UI 与事件创建

### R05：SoundFont 状态入口（P1）

用户要求：状态栏 SoundFont 文本采用诊断状态文字同款 hover / Hand / 点击行为，直接打开 Application Preferences 的 SoundFonts 页。

源码确认：

- `src/midora-desktop/Midora.Desktop/MainWindow.xaml:1370` 附近诊断状态具有点击与悬停样式；`:1389` SoundFontState 仍只是 Caption 文本。
- `MainWindow.xaml.cs:367` 的设置入口先做 `PrepareForModalSurface`、前台任务许可检查，保存后可能重建 Worker。
- `ApplicationPreferencesDialog.xaml:21` 是 Audio / SoundFonts / Appearance TabControl，当前没有供该状态入口指定初始页的完整导航路径。

建议：抽统一 `OpenApplicationPreferences(initialTab)` 核心方法；现有菜单仍默认 Audio，新入口指定 SoundFonts。不要构造缺失 RoutedEvent 的事件参数再调用事件处理器。打开前停止由当前编辑器持有的试听，不误停正常项目播放。

D-UI08.a 已确认：正常播放 / 不可中断前台任务期间遵守现有设置编辑禁用规则，不因点击入口自动停止正常项目播放，不新增播放中的只读设置浏览模式。禁用原因应有可理解的 tooltip，不绕过设置保存门。

验收：无项目、有项目、零 / 多 SoundFont、预览中、正常播放中、正在任务中；初始页正确；Cancel 无改动；设置保存 / Worker 重建行为不退化。

### R14：SubVoice 事件创建扩模板（P0）

源码确认与候选原因：

- `MainWindow.xaml:955` 将 SubVoice Event Surface 的 `RangeEndTick` 绑定 TemplateLengthTicks。
- 共享 `Midora.Desktop.Presentation/Controls/TimelineSurface.cs:3725/7014` 把该范围传入绘线采样；`Interaction/TimelineValueTraceSampler.cs:52/162` 过滤 `tick >= rangeEnd`。
- 但 Application `ProjectResourceCreationEditCommands.cs:733–747` 创建事件已扩大模板；`ProjectSubVoiceEventLaneEditCommands.cs:98–102` 的移动 / 复制亦有扩展；`ProjectBoundedTemplatePointCommands.cs:122` 分页发布同样支持。

因此优先追查“显示模板范围被 UI 误作创建硬上界”，不重新创建一套扩模板领域规则。Logical / MIDI Segment 的边界规则不同，不能全局取消采样器上界。

预期实现合同：

- SubVoice 的创建允许非负 Tick；成功编辑按既有正式 required boundary 同事务扩模板，事件位于模板末端时必须使它进入半开区间。对点通常需容纳 `tick+1`，必须 checked；`long.MaxValue` 不能通过溢出伪装成合法长度。
- 覆盖单击创建、Shift 锁 Tick、自由线、直线、水平线、上下文创建、Batch Create、粘贴 / Paste Here、Ctrl 复制拖动，以及已支持的曲线点创建。检查移动 / Properties 改 Tick 的既有扩展路径，不任意扩展不支持的操作。
- Add Event 只建空 owner，不凭空创建 tick 0 点；Initial State 不扩模板；已删除的 Mapping owner 不因普通编辑静默复活。
- 若有 Value Curve，仅检查其原有创建路径；不得把曲线展开结果改存离散点，不改变可曲线化目标范围。
- 内容 + 模板长度一次 Undo；取消、revision race、非法目标、溢出零发布；预览和提交共享同一结果。

测试门：`Length-1 / Length / Length+1`、Snap on/off、正反向绘线、跨很多页、大型生成取消、模板包含 Loop/Pre-Roll、Undo/Redo、其他两类 Segment 仍遵守自身范围。延长后立即刷新标尺、音符 / 事件可见范围，不依赖切 Tab。

### R16：Catalog 列表滚轮（P0）

`InstrumentCatalogDialog.xaml:106/137/247` 的 BankList、ProgramList、ScanBankList 采用 `CanContentScroll=True` 与虚拟化；没有专用滚轮处理。通用 `ScrollViewerWheelRouter.cs` 把 `Delta/3` 直接传给 ScrollToVerticalOffset。若该 ScrollViewer 的 offset 是项单位，普通 120 wheel delta 就可能成为 40 项，而非 40px。这是需运行确认的直接候选原因。

建议按逻辑 / 像素滚动模型处理，不对项列表硬套 24px 或假设行高恒定。D-UI08.b 已确认：每个标准滚轮刻度滚动一项，累积高分辨率滚轮不足一项的余量；嵌套列表及边界消费本次列表滚动，不能同时滚父级。

验收包含 Banks / Programs / Scan 预览的每个列表、长列表虚拟化、无滚动范围、到顶 / 到底、触控板小 delta、选中项不因滚动改变；确认没有破坏已有事件乐器内层列表的滚动隔离。

### R17：禁止展开下拉框悬停自滚（P0）

当前共享主题 `Midora.Desktop.StyleGallery/Themes/Controls.xaml:748/836` 提供 ComboBox template 和 `ComboBoxWheelSelectionGuard.UseSingleStepDropDownWheel`；后者只拦截滚轮，没有覆盖 hover 自动滚动。尚未通过运行堆栈确定该行为来自 WPF 默认 ComboBox 还是特化模板，实施前先复现记录，不能只改 wheel handler 声称已解决。

用户要求覆盖整个产品的展开下拉内容；建议上下边缘都禁止纯悬停触发连续滚动。保留：滚轮、拖滚动条、滚动条按钮按住、方向键 / PageUp/Down / Home/End、键盘选择的 BringIntoView。禁止滚轮穿透到父级的现有合同不变；不以冻结 ScrollOffset 的方式同时破坏键盘导航。

实施清单必须以源码扫描生成，包含隐式 ComboBox、显式 / keyed Style、自定义下拉 Popup、可编辑 ComboBox及测试宿主；菜单 / 补全列表不是 ComboBox，应标明是否具有同类行为，不能不加区分禁用所有 Popup 的交互。交付时单列未走共享模板的实际位置，由用户验收，不在本次预编造一份完整名单。

### R23 / R25 / R26：裁切、默认宽度与数据列表文案

| 要求 | 源码确认 | 修改合同 |
|---|---|---|
| R23，P0 | `NewProjectDialog.xaml:5/36/42`：宽620、Browse宽76、确认文案 Create Project | 改 `Create`；同时增加 Browse 和窗口可用宽度，保持共享确定按钮高度 / 颜色 / Enter / Esc，不全局扩大所有按钮 |
| R25，P0 | `TimelineObjectListState.cs:10/33`：默认350、约束240..700 | 默认改400；三类主 Piano 的共用列表状态同步。仅默认，不覆盖当前用户拖出的宽度；未来恢复值优先 |
| R26，P1 | `TimelineObjectListSource.cs:373/384` fallback 为 `Number · Value` | 按正式事件类型格式化 value；PitchBend仅数值，不做全局字符串替换。RPN/NRPN、PolyPressure 等身份移至 Type/Target 或保留必要独立列，不能丢失区分能力 |

R23 的具体增宽数值尚未由用户给定；建议依据 Sora 字体、100/125/150/200% DPI、最小窗口尺寸和按钮实测宽度确定，不靠随机改 margin。R26 与 R12 的 raw/display formatter 和 R27 特殊行共用呈现接口，避免再次三处独立拼字符串。

### R24：SubVoice Add Event 后导航（P0）

`MainWindow.xaml.cs:8549–8585` 成功路径只提交创建 Lane；`PresentationModels.cs:3605` 周边刷新优先保留旧 target，能解释“新增后仍停旧 Lane”。

成功后以正式 target 身份激活刚创建 Lane，并恢复焦点到目标 Surface；不能用显示名称或暂时数组下标。Cancel / 失败保留原页 / 选择。已有 target 重复创建入口应按既有规则定位 / 拒绝，不能制造重复 owner。异步刷新迟到不能把用户已切换的页抢回；R28 上线后同一导航命令打开目标 Tab。

## 2. R13：事件批移的 scalar 值与 Clamp（P1）

### 2.1 已发现的具体路径

`MainWindow.xaml.cs:7984–8039` 的 Direct MIDI 编辑路径读取选择指标时 value 写为0，只按 anchor Clamp；Application `ProjectPureMidiTimelineEditCommands.cs:836–856` 将相同 Data1/Data2 byte delta 套给每个事件再严格验证。

这不只可能在 value 上下限出错。Pitch Bend 的 14-bit 值127→128，对 anchor 计算成低字节−127、高字节+1；直接套到另一个原值128的事件，会得到负低字节。应在**正式 scalar 值** 上做变换，再编码字节，不能把两个 MIDI 数据字节当独立坐标轴。

Logical Parameter（`MainWindow.xaml.cs:8174–8217`）与 SubVoice（`:8424–8446`）已有基于整选区 min/max 的共同 delta Clamp。它们不是已确认相同 BUG，但必须一并验证。共享 Surface 的预览用受限 delta，MouseUp 某路径仍提交原 pointer delta（`TimelineSurface.cs:3824/8485`），需核对 preview=commit。

### 2.2 建议的修复边界

- 统一以 target 的 scalar 值域表达编辑 / 预览 / delta 提示；PB遵守各adapter现有14-bit scalar合同：Direct当前使用0..16383，SubVoice使用−8192..8191，二者以明确偏移转换。CC / Pressure 等遵守各自合法值；编号和身份不参与Clamp，不借本修复静默改变UI或持久值域。
- SRS §20.1.7已规定共同delta饱和，先保持该合同并修复Direct编码不一致，无需用户重新批准。D-UI03的逐点饱和仅作为可另行选择的新交互变更，不阻塞本次小修。
- Value 保护不能放宽非法事件 target、CC91/93 在 SubVoice 的禁入或枚举合法性；混合非法类型应按现有显式子集规则处理。
- Small / paged、普通 / Ctrl 复制拖动、公用 Properties / 批处理若复用此转换层均检查；不要同时重写与本项无关的 Batch 数值契约。
- 操作后精确同 Tick 碰撞继续 later-wins；Tick 轴既有边界与 Shift 锁 Tick 保持。

最低测试：PB 127↔128、8191↔8192 raw 边界与−8192/8191端点、多点不同高低字节、CC0/127、同 target 不同值、逻辑整数 / double / enum、SubVoice复合Bank目标、四边出视图、选择页冷热、取消 / Undo / Redo。必须比较最终图形与实际导出值，不能只断言“不抛异常”。

## 3. R18 / R19 / R20 / R21：共享鼠标交互重构

### 3.1 右键行为与 Draw 绘线子工具（R18，P1；Q1／Q2 已确认）

用户已决定取消300ms双击切工具；D-UI01.a～c 已确认新的内容区手势。实施状态机按以下行为建立：

```text
Right Down：冻结 owner / revision / target / selection / 起点
未过阈值 Right Up：打开原上下文菜单，不人为等300ms
超过阈值：框选，取消菜单候选
框选 Right Up：提交选择，不补弹菜单
Escape / 失捕获 / 切Tab / owner失效 / source revision变化：取消手势
```

新手势取代旧手势或Surface卸载也须取消pending exact-hit、菜单和框选；迟到结果不得恢复旧Selection或覆盖当前菜单。这与既有选择修订隔离共用同一token。

命中冷页仍须后台 / 有界；“无延迟”是没有双击识别等待，不是要求 UI 同步解压冷页。D-UI01.c 已确认：菜单目标未就绪时立即显示不可执行的“定位中…”框架，就绪后按冻结对象填充；禁止沿用上回旧 target 开菜单。

Q2 D-UI01.d 明确撤回 Q1 的“删除 Draw、三个独立 Pen”：继续保留共享的 Draw／Select／Split／Erase 主工具，在适用事件绘线区域额外提供 Free／Line／Horizontal 三个纯图标按钮组成的子工具组；非 Draw 模式时禁用这三个按钮。旧右键直线和 Shift+右键水平线改由 Draw 下当前子形态的左拖执行，覆盖事件、参数、Velocity 和 Conductor Tempo，不漏掉既有绘线能力。Free 沿用原 Draw 图标，Line 使用 `LineFilled`，Horizontal 使用 `SubtractFilled`。普通左键命中已有点仍用于移动，`Alt+左拖` 强制执行当前绘线形态；Shift+左保留锁 Tick／单点创建，不改作水平线。子形态不改变 Note／Segment 的普通创建、移动、Resize，也不向固定 y 的 Inst. 点或其他不适用区域新增画线能力。

源码确认：主 Piano 与底部 Lane 当前共用 ToolMode（`MainWindow.xaml:419/488/701`，SubVoice 为 `:869/884/950`）；`TimelineToolPolicy.cs:181` 当前按 Draw＋事件区＋Shift右键识别水平绘线，`MainWindow.xaml.cs:10019–10033` 将 D/S/E 映射为 Draw/Select/Erase。后续保留共享主 ToolMode，用独立绘线形态字段迁移右键绘线判断，不拆成主区／事件区两套独立 Draw/Select。D 继续选择 Draw，不暗中把记忆的 Line／Horizontal 重置成 Free。

Q2 D-STATE03.e 已批准工具状态进入 Track 通用 profile；其中“按 D-UI01.d 最终划分”按上述最终结构落实：共享主工具与事件绘线子形态分别保存，同 Track 同步、不同 Track 隔离；SubVoice 各自独立，Arrangement／Conductor 保留各自状态。选择非 Draw 只禁用子工具，不借禁用删除其已记忆形态。状态变更仍不修改音乐数据、不进入音乐 Undo；具体字段和通知实现按 B1 状态设计与验证落实。

| 表面 | 已确认新右拖范围 / 必须保留 |
|---|---|
| Arrangement 实际 Segment 内容 | 框选正式 Segment；头部 / brace / 空轨外区域不借此创建或编辑 Segment |
| 三种 Piano | 框选 Note；左键继续共享 Draw / Select / Split / Erase 的既有适用行为，事件绘线子形态不改变音符操作 |
| MIDI / Parameter 数值 lane、Tempo | 框选点；旧绘线迁移后保留完整采样密度与 Shift 行为 |
| Velocity | D-UI01.b 已确认纳入，框选的是对应 Note，不画新的 velocity；须验证列表与 Piano 选择同步 |
| 时间尺 | 不当作普通对象区；保留现行定位例外；底部 / SubVoice 禁 Time Range 的规则不恢复 |
| Piano 左侧琴键尺 | 仍无右键菜单，不产生空菜单或穿透框选 |
| 浮动工具 / grip | 有独立命中优先级，不能穿透到背景启动第二手势 |
| All Tracks | 仍只读，不擅自增加对象选择 / 编辑菜单 |

框选修饰键按 D-UI01.b 沿用当前 Replace / Add / Remove / Toggle 规则；drag threshold 与冷页选择仍复用既有政策。中键 Pan 保留；已有 capture 时另一键不能建立竞争手势。

新增验证：Draw 三形态的左拖／Alt 强制绘线与原采样结果一致；非 Draw 子按钮禁用；D 返回 Draw 保留子形态；主区和事件区共享主模式而不互造第二模式；同 Track 同步、跨 Track 隔离及显式保存恢复。上述仅为测试计划，本轮未运行。

### 3.2 设置编辑指针而不清选（R19，P1，D-UI02 已确认）

不能回答“点顶部尺子就行”：`MainWindow.xaml.cs:6267` 标尺更新的是 **Playback Cursor** ，`:7330–7345` Select 单击清选后改的是 **Edit Cursor** 。

D-UI02 已确认：保留 plain Select 内容区左单击的清选 + 设置编辑指针，新增 **Ctrl+左单击顶部时间尺** 只设置 Edit Cursor 并保留选择；普通顶部时间尺单击仍设置 Playback Cursor。这里明确取代旧的“Ctrl+左单击内容区”建议：内容区 Ctrl 单击 / 拖动已承担多选和复制拖动，不能被新定位操作占用。新手势已获批但尚未实施；底部禁止 Time Range 的规则不因此恢复。

不采用 plain 左键只设指针、改由 Esc 清选的旧备选。新增手势应有 tooltip / 帮助说明，避免成为隐藏操作。

### 3.3 浮动工具（R20，P2；R21，P1）

用户已确认新增 Copy / Cut / Delete 和多种批量编辑入口；存在有效且兼容的选区时，浮动工具跨全部左键编辑模式显示，不再限于 Select。

D-UI04 保留 Move、ResizeStart、ResizeEnd、Follow/Pin、grip 和全规模 delta 提示；用户追加的以下 13 项均作为新增常驻工具，不再沿用“只常驻前三项、其余全进菜单”的旧推荐。具体排布需验证小窗口和遮挡，但不能据此静默删减已指定按钮。菜单与快捷键使用同一能力判断；不能因选择不支持 Move 就隐藏可用的 Delete，不兼容操作不得借新增入口扩大类型能力。

| 新增常驻工具 | 用户指定图标 |
|---|---|
| Copy | `CopyRegular` |
| Cut | `CutRegular` |
| Delete | `DeleteRegular` |
| 左右翻转 | `FlipHorizontalRegular` |
| 上下翻转 | `FlipVerticalRegular` |
| Scale | `ScaleFitRegular` |
| 移调 | `ArrowMaximizeVerticalRegular` |
| Batch Edit | `DocumentSettingsRegular` |
| 类人化 | `AnimalPawPrintRegular` |
| Split | `SplitVerticalRegular` |
| Join | `SquareDovetailJointRegular` |
| 量化 | `TableMoveLeftRegular` |
| Properties | `SettingsCogMultipleRegular` |

同一命令的相关菜单入口复用这些指定图标；该局部图标决定不代表独立 R08 全局图标清单已完成。

混合选择只提供共同兼容操作 / 显式类型子菜单，不静默忽略一部分。复制拖动、Selection 后态、Undo前态与有限内存事务保持。

R21 候选原因：`TimelineSurface.cs:10213/3449` 用 viewport `YToLane`，`TimelineRenderModel.cs:1320–1328` 将 y 先夹到当前可见 viewport；使鼠标离开后无法表达连续世界坐标 delta。现有 `AutoScrollEditGesture(:3527)` 只在 MouseMove 驱动，不能据此保证鼠标静止在边缘外也持续滚动。

建议冻结 pointer-down 的世界坐标，capture 期间继续将外部位置映射成 delta；视觉裁切与数据约束分开。D-UI09 已确认鼠标静止于边缘外时也持续自动滚屏并更新 delta，直到回到视图内或结束手势。自动滚动与普通直接拖动共用，不建立浮动工具专用低性能预览。MIDI key硬边界及普通 Move / Copy 的既有删除或 Clamp 规则保持；不要一律 Clamp所有Note并改变既有行为。

测试：三类Note、事件/Segment适用分支、1/10/40万/百万选择；上下出界、停留、返回、四角、Ctrl变化、失捕获、Escape、切Tab、鼠标静止边缘；预览、delta、最终结果一致，内存 / 首次冷区不退化。

## 4. R15：模板末端手柄（P2）

用户要求在 SubVoice 顶部时间线的 Template Length 位置显示窄手柄，拖动时显示新长度和 delta；Snap只量化delta。

当前 `ProjectEventInstrumentEditCommands.cs:7–22/385–414` 不允许模板短于 SubVoice Note尾、事件、曲线、Loop端点及 Pre-Roll 下界。**模板不是可任意 crop 的 Segment** ；不得默认“缩短后隐藏保留”也可成立。

D-UI05 已确认复用现有合法下界：拖到最短合法长度时停止缩小，显示实际受限 delta，不因继续向左拖而报错。冻结原长度、Snap和最小长度；最小长度摘要需 revision-bound，不每 MouseMove 全扫内容。preview显示实际受限长度 / delta，抬起一次正式command，取消零变更；长度改变应刷新同Definition所有SubVoice视图并进入既有编译失效路径。不得移动Loop/Pre-Roll迁就手柄。

测试：内容尾 / 末端事件 / Loop单端与双端 / PreRoll、模板很长、长整型溢出、跨多个打开SubVoice、Snap非整倍原长度、Cancel / Undo / Redo。是否允许未来隐藏模板外内容另开语义需求，不搭便车实现。

## 5. R22 / R32 / R31：标尺、首开 Fit 与进度

### R22：全局锚定跳标（P3）

`TimelineGridPresentation.cs:54` Bar选择从可见 `startTick` 开始计算下一可标位置；`TimelineSurface.cs:9097–9107` 普通Tick ruler则已有 major倍数0锚定。应修有问题的公共Bar抽样，不盲目重写正确分支。

建议用全局小节序号的稳定抽样相位；文字仍为一基Bar。Segment通过ProjectTickOffset映射Project拍号图；不是在每个Segment重新“第1小节”。变拍号 / 截断小节 / 负content偏移 / 极端Tick均检查；Pan只能让标签进入或离开视口，不能让仍可见标签换成另一套序列。仅改绘制密度，不改Snap/音乐时间语义。

### R32：All Tracks 首开垂直缩放（P2）

当前 `AllTracksWorkspaceViewModel.cs:32–33` 默认 FirstLane48、LaneHeight15；`AllTracksView.xaml.cs:62` 手动Fit用全控件高度除128，没有扣ruler。新增需求应在**首次有效布局** 测量内容设备像素，不在每次激活 / resize重置。

D-UI07 明确修正为 `N ≥ 3 pixels/key`，不是原要求的严格大于3或旧推荐的至少4。实现候选相应为 `N=max(3,floor(contentDeviceHeight/128))`，以尽量显示 key0..127 为目标；N为整数，按扣除标尺后的内容设备像素计算，DPI转换只做一次。内容不足384 device pixels时保留至少3 pixels/key并允许滚动。未来恢复保存的有效视图时，恢复值优先于首次默认。

### R31：Compile 百分比（P2）

`DesktopSessionController.cs:217` 只公开 CompilationState 字样；`MainWindow.xaml:1289` Compile 文案固定。`Midora.Playback/ProjectCompilationSession.cs:639–685` 包含snapshot capture/materialize、Full/Incremental与结果投影，当前没有能直接绑定的完整进度计数合同。`Midora.Compiler/Contracts.cs` 的CompilationRequest也不包含该计数。

建议增加旁路运行期进度，按已有实际工作量边处理边计数；UI节流约每100ms一次，任务 / revision token隔离迟到更新。缓存命中、早期失败、取消、增量退回Full、后处理都包含，不能“30%忽然完成”或提前100%。

D-UI06 已确认仅有可靠 total 时显示百分比，否则显示阶段名称；不要求全程估算百分比。不能为了精确 total 先扫描或展开巨大逻辑编译结果。用户另明确：若实测编译速度仍倒退过多而不可接受，可以不实现本需求；先记录性能对比，不预先编造收益或可接受数值。进度不进入 Project/canonical，不改编译正式顺序。

## 6. 低优先级 / 后置性能和外观专题

### R10：连续滚屏（P3，先实验）

当前 `TimelinePlaybackFollowPolicy.cs:26–35` 是越出10%..90%区域后跳到约20%的窗口跟随；`MainWindow.xaml.cs:153/697/725` 使用33ms UI刷新。固定指针会持续改变世界坐标可视范围，比只画一条cursor成本高。

D-VIS02.a～e / D-VIS03 已确认：顶部全局 Follow 按钮按“关闭 → 跳跃跟随 → 连续跟随 → 关闭”三态循环，默认关闭；连续锚点允许 0～100%，默认最左侧0%，提供靠左／居中快捷值，模式和锚点属于程序偏好。不显示负Tick；开始时不足以到达非零锚点则先让指针向锚点走，再固定。手动水平缩放保留 Follow，按播放指针锚点处理缩放。

D-VIS02.d 明确保留现有临时暂停行为，不能改成关闭 Follow 或取消按钮高亮。源码 `MainWindow.xaml.cs:744–783` 与 SRS §18.11.2 已明确：中键／水平概览拖动期间暂停，松开或失去捕获后立即恢复跟随；Follow 期间底部概览滚轮横移仍禁用。这是沿用现状，不必重新询问恢复时机。

Q2 D-VIS02.f 已明确扩大本批连续跟随范围：两类 Segment Piano、All Tracks、Arrangement、Conductor 均支持。若其他既有或未来视图支持跟随但尚无连续能力，保留全局连续选择，在该视图采用原有跳跃行为，切回连续视图无需重选；不向原本不支持跟随的视图自动增加能力。该兼容分支已获批，不是运行中按性能偷偷降级；本批明确支持连续的五类视图仍须通过连续能力验收。

源码确认：`PresentationModels.cs:1036/1154–1156/1533` 的 TimelineWorkspaceViewModel 覆盖 Segment／Arrangement／Conductor 并实现统一播放指针契约；`AllTracksWorkspaceViewModel.cs:11` 为另一 IPlaybackTimelineWorkspace 实现。`MainWindow.xaml.cs:718–736` 经该接口执行跟随，`MainWindow.Conductor.cs:30–37` 已接入 Conductor 的暂停／恢复交互。此次范围扩充可沿既有入口实现，但当前跟随策略仍只有布尔开关与跳跃计算，不能写成连续效果已完成。

不改音频时钟。建议固定世界坐标tile、热tile平移复用、边缘后台预取、隐藏Tab停工，不随每像素滚动重建音符索引。性能 / OS能力门仍须实测；用户批准行为不等于已经证明可以上线。D-VIS03 已确认先优化，仍不合格则暂缓，不牺牲已有编辑／播放流畅性或运行中偷偷切换显示语义。

### R11：播放琴键色（P3，先实验 / D-VIS01）

R11 范围仍仅两类 Segment Piano 和 All Tracks；Q2 对 R10 连续跟随新增 Arrangement／Conductor，不同时扩大琴键染色范围。

当前 `TimelineSurface.cs:9155–9240` 琴键底图缓存与单个HighlightedPitch不是播放多音高状态。建议独立最多128key的轻量显示层，按明确来源和范围增量计数，不每帧扫所有Note、不修改音符tile、不让音频callback分配UI对象。

D-VIS01.a～d 已确认按当前视图所显示音符的 Gate 判断：原始逻辑视图按原始音符，Compiled 按该模式显示的音符，不追踪 BASS 真实 voice／原生 release。Logical Mapping、Loop/Pre-Roll 与 All Tracks 混合 Compiled 的来源差异仍保留；不能将琴键色解释为最终实际发声状态。不按 Mute/Solo 过滤；当前编辑轨道优先，其余按洋葱皮叠放顺序取最上层来源，All Tracks 则后面的 Track 优先。Buffering 冻结、Stop 清空、Seek/Loop 按新 Tick 更新；染色提供开关，性能通过后默认开启。

D-VIS01.a/c 的读图设想属于技术问答，不是撤回上述语义。去掉边框只能消除一种颜色污染；当前指针下1px仍可能覆盖多个Tick，且受LOD、抗锯齿、透明叠加、冷瓦片就绪、选区与预览覆盖影响，不能直接保证精确半开 Gate 或声称整体只有固定扫描成本。MIDI共有128个Key；扫描128行也不等于图像生成、同步、取数没有成本。可研究从正式范围查询生成、与边框等装饰分离的有界每Key占用／来源色缓存（SRS §18.11.3 允许只读每Key占用缓存），但需实测，且不得将其作为领域或编辑命中来源。即使采用独立缓存，生成时仍须执行已批准的来源颜色优先级；若未来要改成近似像素语义，需另行决定，不能以优化名义自动批准。

R10/11性能实验共同输出：相同镜头轨迹下cursor-only与连续滚屏 / 键色的p95/p99帧耗时、首次冷tile显示时间、后台队列 / 取消、内存 / 分配、underrun；稀疏、密集、长Gate、大量Onion来源全部覆盖。R10 另覆盖新增 Arrangement／Conductor 的大规模预览、Tempo／Meta 密集区及切换视图；R11 保持自身三类视图矩阵。未通过前不承诺可以上线；琴键染色的默认开启以性能通过为前提，Follow 总开关仍默认关闭。本轮未运行这些性能实验。

### R30：Segment Tracks 左栏（P3）

顶部 `Tracks` Toggle 位于List左侧；展开面板位于对象List更左。复用Arrangement的轨头presenter、命令、唯一排序、group brace、runtimeMuteSolo，不能复制业务处理器形成第三套顺序。

D-TRACK01.a～d 已确认：默认隐藏；复用 Properties／改名／颜色／绑定／路由／复制剪贴板／排序／共享组／MuteSolo 等适用轨道头操作，不加入 Segment 内容绘制。单击沿用 Arrangement 的轨道选择／取消选择，不立即跳 Tab；双击优先打开蓝色编辑指针所在 Segment，空隙取最近且等距取前，轨道完全没有 Segment 时什么也不做。删除当前 owner 沿用关闭相关 Tab，轨道操作不无故清除 Piano 的音符／事件选择。

用户另要求高亮“当前 Segment 所在轨道”。该 owner 高亮与 Arrangement／侧栏共用的轨道操作选择分别表达，不能因为单击其他轨道就把当前 Segment owner 标识误改成选择目标。折叠即停止不必要订阅 / 查询；持久化只包含 D-STATE 批准白名单。

### R03：原生窗口效果（P3，建议发布前单轮）

源码大量使用 `WindowStyle=None`、WindowChrome与自绘边框（例如NewProject/About/ApplicationPreferences）。此轮未做Windows运行验证，不能声称只改CornerRadius就能获得原生阴影 / 动画。

D-WIN01 已确认主窗口与自有弹窗按系统能力处理：Win11 原生圆角／阴影、Win10 方角／阴影，遵守系统动画设置；能力不可用时回退可靠现有行为，不能破坏 DPI、最大化、拖动和命中。用户明确该需求并非必须完成，可暂缓或不实现，不作为必达的原生效果发布门。

ENV-WIN10 已回答：用户不能提供 Win10 实机，但可提供虚拟机环境；具体 VM 配置与实测仍待后续准备，不应继续标为环境未回答。若推进本项，先做独立小探针验证产品现有 Chrome 下的最小化／最大化／恢复、Snap 布局、各系统角形与阴影、DPI／多屏／最大化工作区／resize 命中。支持能力以运行 OS 与窗口实际配置为准，VM 结果不得冒充实机验收；不为效果引入透明大窗口造成渲染回退。本次未验证任何平台 API 效果。

### R08 / R09 / R02：图标、快捷键与发布后动效

- R08（P3）：独立全局图标任务仍按用户指定清单推进，不在本轮新增图标问题或代选。D-UI01.a 和 D-UI04 已给定的局部图标必须保留并复用于同命令入口，但不代表 R08 全局清单齐备。禁用态／对齐／shortcut 栏继续共同检查。
- R09（P3）：D-KEY01 已授权实施者为高频新增命令安排无冲突键位，用户额外允许给以前已有但尚无快捷键的操作补键；保留既有键位，不将补键授权扩大为重新绑定旧键。D-KEY02 已确认本批不做可配置快捷键系统，也不要求全部低频菜单项配键。实施前做源码冲突检查，最终键表同步菜单提示与说明，并覆盖输入框、IME、代码补全、模态、Tab 和只读视图。D-UI01.d 已确认保留共享 Draw；D 返回 Draw，不重置绘线子形态，不再沿用旧 Q2 推荐的“D 选 Free”。
- R02（P4）：仍在正式发布且稳定后实施。D-FX01.a～d 已确认菜单／弹窗等以短淡入、轻微过渡为主，不抢注意力，并允许彻底关闭动画；亚克力独立开关默认关闭，不支持或性能不合格时回退不透明主题。瓦片从无内容到新进入区域首次内容就绪时可播放出现动效，提供独立开关且默认开启；编辑／Undo／Selection／热缓存复用不反复动画。Q2 D-FX01.e 明确：尚无缓存时静态展示颜色明显区别于对象内容的棋盘占位，不闪烁；已有粗概览保留，粗→细过程不播放任何动效，不以替换 LOD 重播首次出现动画。所有这些设置放在现有 Application Preferences 的 Appearance Tab；总关闭和系统减少动画设置优先，繁忙时跳过装饰，不为播完动画延迟内容。仅动画独立视觉层，不删粗缓存、不使已显示内容再次空白、不改变命中／选择。

R02 源码确认与实现建议：`TimelineSurface.cs:5654–5669/5705–5723` 已在目标细瓦片尚未覆盖的区域保留 fallback，并直接绘制已就绪细瓦片；不要为这条粗→细路径新增动画。静态棋盘只表达尚无可用内容的等待状态，不能覆盖已就绪粗图，也不能把已确认无对象的正常空白长期显示为加载中（现有 Segment preview 在 `:5609` 已判断无 Note/Event 内容）。棋盘尺寸、颜色与有界绘制方式由既有样式和实际验证确定，不新增产品选择题。后续验证无缓存→首次内容、粗→细、已知空内容、冷／热缓存、取消／旧修订替换、总动效关闭和减少动画；本轮未运行 UI／性能测试。

## 7. 现有自动测试扩展落点

共享Presentation测试：`TimelineValueTraceSamplerTests`、`TimelineEventContextGestureTests`、`TimelineRenderingTests`、`TimelinePlaybackFollowPolicyTests`。Application测试：`BoundedDirectMidiEventEditTests` 与SubVoice资源 / Lane命令测试。Desktop测试：`TimelineObjectListTests`、Catalog / Properties投影和workspace生命周期测试。

这些只是已存在的测试入口，不代表当前覆盖完整或本轮已运行。每个实现批次应增加能稳定失败于旧代码的回归测试，再对共享三宿主和性能预算验收。
