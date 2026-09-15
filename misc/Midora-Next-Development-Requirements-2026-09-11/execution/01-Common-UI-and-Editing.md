# A1：通用 UI 与基础编辑执行计划

日期：2026-09-14。状态：**原 UAT-A1 及全部返修人工验收通过** 。产品与对应 SRS 已同步；证据及未运行项见 [A1 实施报告](../../Midora-A1-Common-UI-and-Editing-Implementation-2026-09-14.md)。本次按用户要求提交、推送，不本地发布、不使用 computer-use；未进入 A2/A3。

关联需求：P0 **R14、R16、R17、R23、R24、R25** ；关联 P1 **R05、R13、R26** 。执行与通用验证规则见 [执行总索引](00-Execution-Plan.md)。本主题有 9 个内部任务，合并交付 **一轮 UAT-A1** ；内部任务数不是用户人工验收次数。

## 1. 输入、决定与范围

原需求与源码调查见 [01 §1～2](../01-Interaction-and-Display.md)，优先级与批次见 [00 §2、§5 A1](../00-Overview-and-Delivery-Plan.md)。用户原答保留于 [04：D-UI03、D-UI08.a/b](../04-Decisions-and-Preparation.md)，归并导航见 [05 §2.1](../05-Decision-Summary-and-Q2-2026-09-14.md)；本文件不改写原答、不新增产品问卷。

| 追踪维度 | 执行合同 |
|---|---|
| 输入 | 当前正式 SubVoice／Logical Parameter／Direct MIDI 内容与 owner revision、冻结 Selection、已存在的设置和控件状态，以及本轮用户操作。 |
| 正式输出 | R14/R13 通过既有原子 Project command 发布合法源数据；R24 为成功后的正式 target 导航；其他项是显示、输入路由或既有设置入口，不产生第二套音乐事实。 |
| 边界 | SubVoice Template 属于 Definition；创建点须进入 `[0, TemplateLength)`，必要的 `tick+1` 使用安全算术。Logical/MIDI Segment 的 crop 与合法编辑边界不随之取消。R13 在正式 scalar 上应用整组共同受限 delta，再编码 MIDI。 |
| 失败 | 非法 target、tick 溢出、取消、旧 revision、发布失败零部分修改；Dialog Cancel 不改数据。不能以“不抛异常”替代最终值、选择、Undo 和输出正确。 |
| 诊断 | 继续现有验证／任务反馈。R13 合法 value 饱和不得误报数据字节越界；非法目标仍拒绝。R05 禁用要说明原因，不自动停正常播放。 |
| 持久化 | 沿用既有 source、Application Preferences 和 Session 归属，不在本主题扩 Project Format／presentation。列表 400 是新建默认，未来有效恢复值优先。 |
| 运行时 | 冻结目标、焦点、滚轮余量、列表宽度、导航 token 仍按各自会话/控件生命周期管理；不进入 canonical、音乐 Undo 或音频身份。 |
| 非目标 | 不实现新右键手势、完整 Lane Tabs、乐器变化点、CC 友好值域、完整视图保存；不改 Mapping ABI、音频后端或模板裁剪语义；不顺带选全局菜单图标。 |

SRS 依据：[§18.2.7～8、§18.4](../../Midora-SRS-Initial-Release-v0.1/18-Editing-Workspaces-and-Editors.md)，[§20.1.7、§20.2.5、§20.4、§20.15](../../Midora-SRS-Initial-Release-v0.1/20-Common-Interaction-Validation-and-UI-Acceptance.md)，[INV-014、031、047、075、077、095～096、100、112](../../Midora-SRS-Initial-Release-v0.1/22-Requirement-Locator-and-Cross-System-Invariants.md)。已批准的新 UI 要求已在本切片同步到 §17.7.3、§18.2.8、§18.4.2/4、§19.1.2、§20.4.5、§20.15.4；原 Q1/Q2 不改写。

### 1.1 依赖和交付边界

- A1 **不等待 A3 新手势** ：R14 先覆盖当前单击、自由线、右键直线／水平线等入口；A3 迁移为 Draw 子形态后复用同一 sampler/command 合同并回归，不重复实现扩模板逻辑。
- R24 先按稳定 target 激活现有 Lane；R28 上线后同一导航请求显示并激活目标 Tab，不能依赖暂时列表下标。
- R26 先建立类型明确的 formatter，R12 的显示域和 R27 的包装行以后接入；不要求等完整 R12/R27 才删除无意义前缀。
- R21 默认由 [A3](04-Gestures-and-Selection-Tools.md) 负责；若实施时独立修复可提前并入 A1，只调整执行索引与证据归属，不重复人工验收、不默称 A3 已完成。

## 2. 实施前调查记录（保留历史）

下面保留规划时的静态证据／调查入口，不将它们改写成运行结果。原始调查基线为 `0bb9670`；规划时抽核 sampler、滚轮路由、列表状态和 formatter，其余沿用原需求文档调查。正式 A1 实施基线为 `5deaaceb`，已经重新审查源码并运行验证，结论见实施报告 §1～3。

| 事项 | 证据入口 | 已确认／仍需验证 |
|---|---|---|
| 模板外事件 | `MainWindow.xaml` 的 SubVoice RangeEndTick；`TimelineSurface.cs`；`TimelineValueTraceSampler.cs`；`ProjectResourceCreationEditCommands.cs`、`ProjectSubVoiceEventLaneEditCommands.cs`、`ProjectBoundedTemplatePointCommands.cs` | sampler 使用半开 rangeEnd 过滤；下层已有扩模板路径。UI 提前过滤是否为全部入口的故障根因仍需逐入口验证。 |
| 数值批移 | `MainWindow.xaml.cs` 的 Direct MIDI/Logical/SubVoice 编辑适配；`ProjectPureMidiTimelineEditCommands.cs` | 原调查定位到 byte delta 路径及 preview/commit delta 可疑差异；并未证明三宿主均有相同 BUG。 |
| Catalog 滚轮 | `InstrumentCatalogDialog.xaml`；`ScrollViewerWheelRouter.cs` | 当前通用路由使用 `Delta/3` offset，Catalog 列表启用逻辑滚动。单位混用为候选原因，需记录实际 IScrollInfo/offset。 |
| 下拉悬停 | `StyleGallery/Themes/Controls.xaml`；`ComboBoxWheelSelectionGuard.cs` | 既有 guard 针对滚轮；悬停自滚触发点和特化模板覆盖面尚未运行确认。 |
| 创建／导航／列表 | `NewProjectDialog.xaml`；`MainWindow.xaml.cs`；`PresentationModels.cs`；`TimelineObjectListState.cs`、`TimelineObjectListSource.cs` | 默认宽度为 350，fallback value 为 `Number · Value`；其余具体控件和导航调查见原 01。最终布局与焦点需运行证据。 |
| 设置入口 | `MainWindow.xaml`、`MainWindow.xaml.cs`；`ApplicationPreferencesDialog.xaml/.cs` | 当前设置有 Audio/SoundFonts/Appearance，需接明确初始页而非伪造事件参数。播放／任务锁及试听所有权必须沿用。 |

已存在的测试扩展入口：`Midora.Desktop.Presentation.Tests/TimelineValueTraceSamplerTests.cs`、`TimelineRenderingTests.cs`，`Midora.Application.Tests/BoundedDirectMidiEventEditTests.cs`，`Midora.Desktop.Tests/TimelineObjectListTests.cs`；SubVoice 创建／资源／Lane 测试按实际入口补充。存在测试类不代表本需求已有完整覆盖。

## 3. 内部工程任务

T-UI-01～09 已完成本次代码/规格修改；下面保留原工作和验证合同，不把合同自动当作已测结果。实际证据、扩展性能与原生输入等未跑项见 §5 和实施报告；共同验证规则见 [通用验证门](00-Execution-Plan.md)。本批只交一轮 UAT，不要求逐个内部任务签字。

| ID | R／D 追踪 | 前置 | 工作与完成证据 | 自动正确性／性能验证 |
|---|---|---|---|---|
| T-UI-01 | A1 全部 R；D-UI03、D-UI08.a/b | 当前源码、SRS 与工作区只读复核 | 建立入口→宿主→adapter→command→测试矩阵，区分已复现与候选原因；记录变更条款和本切片验收数据。证据：复现记录、最小样本、源码入口清单、运行前基线。 | 先补能在旧代码稳定失败的测试；确认基线样本、冷暖条件、真实列表单位，不以静态审计代替运行测量。 |
| T-UI-02 | R14；原 01 §1 R14 | T-UI-01 | 区分可视 Template End 与 SubVoice 创建上界；修单击/Shift/绘线入口到正式扩模板 command 的路径，保留其他宿主边界。证据：各创建手势实际生成 tick、模板长度、单次 Undo，以及边界预览。 | Length−1/Length/Length+1、tick 0、Snap 开关、正反向 trace、`long.MaxValue` 溢出；Sampler 与 command 结果一致。采样按页/流处理，不能扩大无界 trace 或全量刷新。 |
| T-UI-03 | R14、R24；原 01 §1 | T-UI-02；T-UI-08 的导航接口可并行 | 补齐非绘线入口与扩展后的跨 SubVoice 刷新：上下文创建、Batch Create、Paste/Paste Here、Ctrl 复制拖动、原有曲线创建；核对 Move/Properties 已有扩展。证据：完整入口矩阵，内容与长度同事务/同 Undo，所有打开的同 Definition 视图即时可见。 | Add Event 仅空 owner、Initial State 不扩模板、Mapping 不复活、Value Curve 不转存点；取消/资源失败/revision race 零发布、Undo/Redo。大型生成跨页取消及局部失效计数，不在每帧扫描 Definition。 |
| T-UI-04 | R13；D-UI03（既有合同，不需重答） | T-UI-01 | 统一 scalar metric、整选区 extrema 与共同饱和 delta；Direct MIDI 先标量变换后编码字节，预览/提示/MouseUp 提交同一生效 delta。证据：三宿主数值前后表、正式编辑结果及导出断言。 | PB 127↔128、8191↔8192、Direct 0..16383 与 SubVoice −8192..8191；CC0/127、不同字节多点、Bank 复合字段、整数/double/enum；碰撞 later-wins、未触及重复保留、复制/取消/Undo。40万/百万选择冷热页的准备、预览、提交和内存按公共门。 |
| T-UI-05 | R16；D-UI08.b | T-UI-01 | 为逻辑项列表提供每标准刻度一项的路径与高精度余量；保留像素滚动路径，列表到边界也消费本手势。证据：Banks/Programs/Scan 各列表真实 offset 与输入增量记录，嵌套滚动隔离。 | 正负刻度、小 delta 累积/反向、顶部/底部/不足一屏、嵌套父级、选择不变；大量虚拟行滚动不全量创建、不增加无界控件或跨列表残留余量。 |
| T-UI-06 | R17；原 01 §1 R17 | T-UI-01；与 T-UI-05 可并行 | 生成产品下拉清单，按隐式/显式/keyed Style、editable、自定义 Popup 分类；定位悬停自滚来源并共享处理上下边缘。证据：受影响控件清单、特化例外及回归结果，不声称只改 wheel handler 就完成。 | 纯悬停 offset 不变；滚轮、拖柄、按钮按住、方向键/Page/Home/End、BringIntoView 保留；不穿透父级，菜单/补全单独判断。多次展开/关闭与虚拟化/事件解绑无泄漏，无永久轮询或冻结 offset。 |
| T-UI-07 | R23、R25；原 01 §1 | T-UI-01 | New Project 改 Create，按实际文本测量加宽 Browse 与窗口；三类列表共用新默认400，不覆盖已调宽度。证据：布局测量/视觉样本、Enter/Escape/Owner 行为、三宿主默认与调整状态。 | 100/125/150/200% DPI、长路径、最小可用窗口、按钮实际 desired/arranged size；列表400默认、240..700既有约束与会话调整保留。隐藏列表不加载数据；布局修改不引入全源查询。 |
| T-UI-08 | R24、R26；原 01 §1；D-LANE01.c 为未来导航衔接 | T-UI-01 | Add Event 成功后通过正式 target 激活新 Lane并恢复Surface焦点；建立按类型 value formatter，去无意义编号且保留 Type/Target 身份。证据：成功/取消/失败/迟到导航记录、逐类型显示样例及三宿主一致性。 | 不同RPN/NRPN/PolyPressure仍可区分；PB只有有意义数值；Bank/PitchBendRange保留复合信息；空/旧revision/重复target、手动切页后迟到响应、D/S/E焦点；只格式化可见行，隐藏/卸载取消请求。 |
| T-UI-09 | R05；D-UI08.a；A1交付集成 | T-UI-01；最终汇集 T-UI-02～08 证据 | 共用设置打开核心，菜单保持默认Audio，SoundFont状态直达SoundFonts；复用诊断文字hover/Hand，禁用有原因。证据：无/有Project、预览、播放、前台任务、Cancel及设置保存路径；汇总R覆盖/失败项后交UAT-A1。 | 只停止来源持有且需结束的试听；不因入口停正常播放或绕开任务锁。Cancel不保存、不重建Worker；实际保存沿用既有持久化→重建合同及失败报告。打开/关闭资源和订阅释放、无隐式SoundFont全读/hash。 |

### 3.1 R14 的完成定义

T-UI-02 是绘制链，T-UI-03 是入口完整性和事务链；两者都完成才可称 R14 完成。每一已支持入口必须列“适用／不适用＋理由”，不得漏掉单击而只测长线，也不得以覆盖图形入口替代 Paste/Batch Create。A3 未实施时记录旧绘线入口名称；A3 以后迁移入口仍须使用本次的边界 golden。

### 3.2 R13 的完成定义

饱和保持整组选中点形状：任一点先触边，整组停止继续纵移。不是逐点分别压平。Direct/SubVoice PB scalar 域差异显式转换，不在此项顺带改变显示契约。Enum 不支持相对 delta；CC91/93 的 SubVoice 禁入、target identity 和其他非法数据仍拒绝。自动测试须从预览有效 delta 一路检查 Project 结果、编码/正式输出及 Undo/Redo，不把“成功返回”当作正确性。

## 4. 自动验证与人工验收分工

自动验证须完成上表及 [公共正确性/性能门](00-Execution-Plan.md)，记录实际命令、环境、样本、结果与未跑项。性能先在同机器同样本建立基线与可接受退化，再实施比较；本计划不虚构毫秒或百分比门槛。DPI矩阵是本批规划的扩展检查，不声称现行 SRS 已承诺全部高 DPI 验收。

开发者先提供代表性可复用验收工程、完整自动报告和控件扫描清单，再进行下面一轮人工验收。人工仅判断布局、可发现性与操作手感；字节编码、巨大选择、溢出、竞态、失败注入、每个下拉模板的穷举归开发自动验证，不转嫁用户重复遍历全软件。

### UAT-A1：通用 UI 与基础编辑（一轮，已通过）

可直接使用 [实施报告 §4 的验收工程及具体操作](../../Midora-A1-Common-UI-and-Editing-Implementation-2026-09-14.md#4-一轮人工验收uat-a1)。下面保留稳定检查编号；用户已确认原清单、上轮返修及报告 §7 的 Segment 焦点补齐均通过。高 DPI 仅完成布局倍率自动检查，未提供真实多显示器截图，不把人工粗验扩大为未运行的验证。

- [x] **UAT-A1-01（R14）** 在准备好的 SubVoice 中，于旧模板末端和末端之外各创建事件，并用一种当前绘线方式跨过末端；事件立即可见、长度自动容纳，Undo一次同时恢复内容和旧长度，Redo恢复结果，无需切Tab刷新。
- [x] **UAT-A1-02（R13）** 在三宿主的代表性数值Lane上拖动一组选中点触及上下界；整组保持相对形状停在边界，预览/delta与松手结果一致，不出现数据字节越界提示。
- [x] **UAT-A1-03（R16）** 在Banks、Programs和Scan Presets结果中各滚一个标准刻度，确认每次一项；在一个嵌套列表到边界后继续滚动，外层不跟动，选择不意外改变。
- [x] **UAT-A1-04（R17）** 使用开发者指定的共享下拉和特化下拉代表项，把指针静置上/下边缘；列表不自滚，滚轮、拖柄及键盘选择仍可到达完整内容。其他位置以自动覆盖清单为依据。
- [x] **UAT-A1-05（R23）** 打开New Project，确认主按钮为Create，Browse和长路径区域不裁字、按钮可达；Enter创建、Escape/关闭取消遵循原工作流。四种布局倍率自动检查已通过；本轮请在实际使用DPI下审阅，真实多DPI截图尚未运行。
- [x] **UAT-A1-06（R24）** SubVoice当前停在旧Lane时Add Event；成功后直接显示新target且快捷键可用；取消不切页，也不改变旧选择。
- [x] **UAT-A1-07（R25）** 首次打开三类Piano对象List，默认宽度为400对应布局宽度；主动调宽、隐藏再显示后不被重置为400，音符画布仍可操作。
- [x] **UAT-A1-08（R26）** 查看含PB、CC、RPN/NRPN、PolyPressure、Bank的准备列表；value不再有无意义的`0 ·`等前缀，仍能从Type/Target等正式字段分辨不同目标，复合值不丢信息。
- [x] **UAT-A1-09（R05）** 点击底部SoundFont文字直达SoundFonts页，hover/Hand与诊断入口一致；Cancel无改动；正常播放或不可中断任务时入口禁用且原因清楚，点击不会停止播放。

## 5. 2026-09-14 收尾记录与未解决风险

| 任务 | 实际完成证据 |
|---|---|
| T-UI-01 | 当前基线/需求 trace、入口矩阵、三项根因和 Popup 路由实测；见实施报告 §1。 |
| T-UI-02～03 | SubVoice 创建上界、流式 command；单点/绘线/移动/复制/Properties/生成/粘贴/曲线及零提交回归；见 §1.1、§3。 |
| T-UI-04 | PB scalar 进位、共同 delta、预览/提交、Enum 保护；正式编译和保存重开事件比对，40万/百万及 ABBA；见 §1.2、§3.3。 |
| T-UI-05～06 | 真实虚拟 ListBox offset 和 Popup 路由测试；19 个产品 XAML 的共享/inline/keyed/editable 覆盖清单；见 §1.3、§3.2。 |
| T-UI-07 | Create/720/680/104、400 默认；真实 BAML 布局倍率及状态测试，非实机 DPI 视觉验收。 |
| T-UI-08 | 稳定 target 激活、受当前 Workspace/target 限制的焦点、typed formatter；VM/格式化/桌面全套回归。 |
| T-UI-09 | SoundFonts 初始页与共享设置流程；真实 BAML 默认页测试、SRS/报告/一轮 UAT 整合。试听/正常播放锁的实机手感仍归 UAT。 |

- Release build 0 warning/error；Presentation 480、Desktop 445、Application 相关157及追加23/5用例全部通过（批次有交集，不相加当独立总数）。具体命令、历史测试 fixture 修正及 TRX 见实施报告，未隐藏原失败。
- 本轮没有完整9KX2 UI冷热p50/p95/p99、多小时资源/真实音频或四种实机DPI截图证据；只报告合成40万/百万有界命令与小型真实WPF测试。扩展验证门不记已通过；未发现需重新决定的产品行为。
- 用户已确认原 UAT-A1、报告 §6 的返修/PB 显示与 §7 两种 Segment Add Lane 焦点补齐均通过，A1 人工验收关闭。原编号保留；验收工程放被忽略的 `artifacts/acceptance/a1`，测试可重新生成；不改用户原项目。
- Segment 焦点补齐：定向 6/6，桌面全套最终 449/449；首次全套有 1 条旧 Popup hover 测试失败，定向/完整复跑未重现，原记录及限制保留于报告 §7.1。
- 对R12/R27/R28及B状态专题只保留接口边界；本轮没有完整乐器包装、Lane Tabs、新手势或新视图持久化。下一建议批次为A2a，不在本轮执行。
