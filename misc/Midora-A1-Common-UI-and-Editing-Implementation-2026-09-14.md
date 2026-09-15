# A1 通用 UI 与基础编辑：实施与验证

日期：2026-09-14。状态：**A1 原清单、§6 返修及 §7 Segment Add Lane 焦点补齐均已由用户验收通过** 。本次按用户明确要求提交、推送，不本地发布，不使用 computer-use。初轮源码起点为干净的 `main@5deaaceb91d2bc2d690e2837a1d9372e826963a9`；本次归档完整 A1 实施与返修成果。

## 需求追踪与设计记录

范围严格限于 [A1 执行计划](Midora-Next-Development-Requirements-2026-09-11/execution/01-Common-UI-and-Editing.md) 的 R14、R13、R16、R17、R23、R24、R25、R26、R05。原 Q1/Q2 回答不改写；不提前实施 A2/A3。

- 输入：正式 owner/revision、冻结选区、当前 Lane target、输入手势及现有设置。
- 输出：R14/R13 经既有原子 Project command 提交；其余仅改变输入路由、显示或 Workspace 导航。
- 边界：SubVoice 可视模板末端不等于创建上界；新点必须能被 `TemplateLength >= tick + 1` 容纳。其他宿主现有边界不变。Initial State 无 tick，不扩模板。
- 数值：先在正式 scalar 域求整组共同受限 delta，再逐点编码；不对 MIDI 的高低字节分别平移。预览、delta 提示和提交使用相同饱和规则。
- 失败：取消、非法 target、溢出、过期修订零部分提交；扩模板与内容共享 Undo/Redo。不得复活用户删除的 Mapping。
- 持久化：不变更 Format、presentation、Mapping ABI、canonical 或音频身份。列表宽度、滚轮余量、焦点仅为控件/会话状态。
- 性能：继续流式采样、有界批量准备、虚拟列表；不为显示或滚轮扫描全项目。新设置入口不扫描 SoundFont，不绕过任务/播放锁。

规格同步：SRS §17.7.3、§18.2.8、§18.4.2/4、§19.1.2、§20.4.5、§20.15.4 及第 00/22 章导航。没有新增 Project 格式、ABI 或音频语义；没有改写 Q1/Q2 原问题与原答。

## 1. 实际改动与根因

### A1 验收返修范围（2026-09-14）

用户确认其余 A1 项通过；本次只修 Scan Presets 的单项滚动、Add Event 后的新 Lane 激活与 A/Snap 焦点，以及新提出的 Pitch Bend 标尺显示。

- 输入：实际 Catalog Scan 结果模板内的滚轮、SubVoice 新建 target 的已提交事务、Direct/SubVoice PB 正式 scalar。
- 输出：每标准轮刻度一项且不滚外层；新建成功后的 CC7 等 Lane 与键盘焦点一致；两类 PB 标尺显示 −8192～8191，标出实际中性值 0。
- 正式语义不变：Direct PB 原始 scalar 仍为 0～16383，8192 为中性值；SubVoice 仍为 −8192～8191。只转换显示坐标，不改变事件字节、编译、保存、碰撞、选择集合或 Undo。
- 失败/取消/过期 Workspace 不导航；必须覆盖真实 WPF 模板以及有 Dispatcher 的延迟刷新，不再以手动先 Rebuild 的测试替代实际创建顺序。
- 非目标：不进入 A2/A3、不重新测试要求用户已通过的整批、不提交/推送/发布，不触及音频与项目格式。

| R-ID | 已实施内容 | 根因／边界 |
|---|---|---|
| R14 | SubVoice 单击、自由线、直线、水平线及 Shift 单点的采样可超过旧模板末端；有界流式 command 一次提交内容与模板新长度。 | 原 sampler 将可视 `RangeEndTick` 当创建过滤上界，在 command 前丢掉界外点；不是缺少编译层的自动扩展。只对 SubVoice 开放，不取消 Logical/MIDI Segment crop。 |
| R13 | 三宿主数字事件使用整组选区共同受限 delta，预览、提示和 MouseUp 一致；Direct MIDI PB 先改完整 14-bit scalar，再编码高低字节。 | 原 Direct adapter 将主点高低字节的差分别加到其他点，跨 127/128 等进位时不成立；Surface 提交原始指针 delta，可能与已饱和预览不同。 |
| R16 | Catalog 的 Banks、Programs、Scan Presets 结果每标准轮刻度一项，保留高精度余量、反向清余量，到边界/不足一屏也不穿透父级。 | 列表 `IScrollInfo` 的 offset 是项，却套用 `Delta / 3` 像素公式。其他像素滚动控件不改。 |
| R17 | 共享 ComboBox Popup 拦截纯悬停触发的自动 BringIntoView；显式点击、键盘选择和滚动继续可用。 | WPF 悬停会 Focus 部分可见的 ComboBoxItem，继而滚动露出下一项，形成持续自滚。必须在 item 路由阶段拦截，父 Grid 在 ScrollViewer 之后处理已太晚。 |
| R23 | New Project 主按钮改 `Create`；窗口宽 720 DIP、最小宽 680，Browse 最小宽 104。 | 保留默认/取消按钮行为，只增空间；不改变保存或新建项目流程。 |
| R24 | SubVoice Add Event 成功后按稳定 target 激活新 Lane，打开底部事件区，延迟焦点检查后聚焦 Surface。 | 原流程只新建 owner，没有发出明确 Lane 导航；旧点选择可能仍把视图留在旧 Lane。迟到焦点请求须仍属于当前 Workspace/target。 |
| R25 | 三种 Piano 对象 List 共用 400 DIP 新默认。 | 保留已调宽度及现行 240～700 范围，不在每次显示时强制改回默认。 |
| R26 | Value 去掉无意义的 `0 ·` 等编号前缀；RPN/NRPN、Poly Pressure 等编号留在 Type/Target。 | Bank、Pitch Bend Range 的复合值不删；Program 现行一基显示不在 A1 改动。 |
| R05 | 底部 SoundFont 状态可点击，直接进入 Application Preferences / SoundFonts，提供 Hand/hover/禁用原因。 | 与菜单共用设置打开/保存入口，保留正常播放和前台任务锁、试听停止归属、保存后 Worker 重建；不额外读/hash SoundFont。 |

### 1.1 R14 入口矩阵与事务

| 入口 | 本轮处理／验证 |
|---|---|
| 单击、Shift 单点、自由线、右键直线、Shift 右键水平线 | 同一 trace/sampler；可视范围不立即改变，创建采样不再用旧模板末端截断；新 command 后台消费流式结果，避免在 UI 线程扩成巨型字典。覆盖 Snap 1/4、正反向、末端前/上/后。 |
| 命令级单点创建 | 原 CreateTemplateEvent 已扩模板；将“已有 target 发现”从逐事件扫描改为 snapshot DiscoveryKeys，保持用户删除的可选 Mapping 不被复活。 |
| Paste / Paste Here | 共用 clipboard 目标 tick 和 bounded append；既有测试将源 tick 10 粘到目标 200，模板 100→201，单 Undo 回 100，并验证 Full/Incremental 一致。 |
| Move / Ctrl 复制拖动 | 原 AdjustSubVoiceEventPoints 路径保留；新增测试移/复制到 tick 99，模板 48→100，Undo/Redo 同时恢复内容和长度。 |
| Properties / Batch Create | 原 UpdateTemplateControlChange、GenerateTemplateEventPoints 路径保留；补 tick 99、同事务长度断言。 |
| 既有 Value Curve 创建 | CreateValueCurvePoint 自身扩模板的合同保留；仍是曲线点，不转存为 Template Event。 |
| Add Event 空 Lane | 没有新 tick，不扩大 Template；只导航。 |
| Initial State | 没有 tick，不适用模板扩展；本轮未改该数据/命令。 |

新流式绘制按 tick 和输入顺序稳定排序；重复 sample 同 tick 取最后值，仍沿用正式事件目标及 later-wins 规则。原 collection API 对重复输入的拒绝合同保留。内容、必要 Mapping、稳定 ID 和 TemplateLength 由既有 root replacement 事务发布；取消、非法 tick/value/CC91、`long.MaxValue` 和旧 owner revision 均有零部分提交断言。单一 Definition 的 TemplateLength 未复制成各个 SubVoice 的独立事实。

### 1.2 R13 的数值与兼容边界

- 初轮 Direct PB 正式 scalar 是 0～16383、SubVoice PB 为 −8192～8191。后续本轮验收明确要求统一显示，返修仅使 Direct 显示轴减 8192；正式 scalar 与批量输入值域保持不变。
- 共同 delta 由整个实际目标选区的 min/max 限定，不是各点各自压平。Direct command 独立拒绝越界 scalar，不能靠 `& 127` 静默截断非法值。
- CC、Program、Channel Pressure、Poly Pressure 保持 selector 不动；Bank/Pitch Bend Range 的另一半字段不被此次值移动覆盖。
- 继续“事件后来者覆盖，仅处理编辑命中的键；无关导入重复保留”；音符的相反碰撞规则不变。
- 审查发现 Enum 相对纵向拖动的现状与既有 SRS §20.4.5 不符：本轮关闭 Enum 纵向 delta，并在小集合/有界 command 双重防护；水平移动、复制和精确设置枚举项仍可用。这是落实既定边界，不是新增 Enum 语义。

### 1.3 下拉控件覆盖清单

产品完整 ComboBox Popup 模板只有 `Midora.Desktop.StyleGallery/Themes/Controls.xaml` 的共享模板，Presentation 链接该资源。按产品 XAML 扫描，19 个含 ComboBox 的文件为：

`AllTracksView`、`ApplicationPreferencesDialog`、`AudioRenderDialog`、`DirectMidiLaneTargetDialog`、`HumanizeSelectionDialog`、`InstrumentCatalogDialog`、`LogicalParameterDefinitionDialog`、`LogicalParameterEventBindingDialog`、`MainWindow`、`MidiChannelRootSettingsDialog`、`MidiExportDialog`、`MidiStateEntryDialog`、`MidiTargetDialog`、`NewRawMidiTrackDialog`、`ObjectPropertiesDialog`、`ParameterMappingPropertiesDialog`、`QuantizeSelectionDialog`、`ScaleSelectionDialog`、`SplitNotesDialog`。

特化情况：MainWindow 的属性编辑 inline style、`InstrumentProjectComboBox` keyed style、ObjectPropertiesDialog inline style、LogicalParameterEventBindingDialog item style，以及 MainWindow 的四处 editable Operation 下拉，均沿用该共享 Popup，不需要分别复制处理代码。没有发现产品另一个完整 ComboBox Popup 模板。普通菜单、代码补全列表不是 ComboBox，不在本次 hover 修改范围；独立 Style Gallery 演示程序也不作为产品实例穷举。

实际修复注册 ComboBoxItem 的 RequestBringIntoView class handler，但仅在已 opt-in 的 Midora Popup 内生效；没有永久定时器、offset 回弹或隐藏滚动条。键盘/显式选择的导航许可只持续当前 dispatcher 周期。WPF 行为参考其 [ComboBox 源码](https://raw.githubusercontent.com/dotnet/wpf/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ComboBox.cs)。

## 2. 改动文件归属

- Application：`ProjectPureMidiTimelineEditCommands`（scalar 值编辑）、`ProjectBoundedTemplatePointDrawCommands`（流式绘制）、`ProjectResourceCreationEditCommands`（target 发现），以及 `ProjectBatchPointAndDeleteCommands` / `ProjectBoundedLogicalPointCommands` 的既定 Enum 防护。
- Presentation：`TimelineSurface`（创建上界与实际 delta）、`ComboBoxWheelSelectionGuard`、`ScrollViewerWheelRouter`。
- Desktop：`MainWindow.xaml/.cs`、`PresentationModels`、`InstrumentCatalogDialog.xaml`、`ApplicationPreferencesDialog.xaml/.cs`、`NewProjectDialog.xaml`、`TimelineObjectListSource` / `TimelineObjectListState`。
- 测试：新增 `CommonEditingA1Tests`、`CommonEditingA1SurfaceTests`、`WpfInteractionRegressionTests.A1`；将原 WPF 测试类拆 partial，在已存在的唯一 Application/主题测试中集成真实 BAML/Popup 验证。
- 未修改生产 Compiler、canonical、Persistence、音频后端、版本或 dist 产物。

## 3. 自动验证证据

环境：Windows x64，.NET SDK 10.0.400 / Runtime 10.0.11，Release。以下为实际运行结果，不代表人工验收；测试生成内容仅写被忽略的 artifacts 或测试输出临时目录。

| 验证 | 结果 | 证据 |
|---|---:|---|
| Desktop Release build | 成功，0 warning / 0 error | `dotnet build src/midora-desktop/Midora.Desktop/Midora.Desktop.csproj -c Release --no-restore` |
| Presentation 全套 | 480/480，0 skipped | `artifacts/test-results/a1/a1-presentation.trx` |
| Desktop 全套 | 445/445，0 skipped | `artifacts/test-results/a1/a1-desktop.trx` |
| Application 相关回归（含 40万/百万压力与 ABBA） | 157/157，0 skipped | `artifacts/test-results/a1/a1-core.trx` |
| 模板/逻辑参数边界及最终验收工程 | 23/23，0 skipped | `artifacts/test-results/a1/a1-final-boundaries-and-sample.trx` |
| 流式非法输入及旧修订零提交 | 5/5，0 skipped | `artifacts/test-results/a1/a1-stream-rejection.trx` |

相关批次有交集，不把数字相加当作互不重复的总用例数。没有运行整个仓库所有项目的全套测试。

### 3.1 可重跑命令

```powershell
dotnet test src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=a1-presentation.trx" --results-directory artifacts/test-results/a1
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=a1-desktop.trx" --results-directory artifacts/test-results/a1
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CommonEditingA1Tests|FullyQualifiedName~BoundedDirectMidiEventEditTests|FullyQualifiedName~ExactTimelineCollisionPolicyTests|FullyQualifiedName~ProjectClipboardPasteTargetTests|FullyQualifiedName~ProjectLogicalParameterEditCommandsTests|FullyQualifiedName~ProjectObjectClipboardTests|FullyQualifiedName~ProjectTemplateEventEditCommandsTests|FullyQualifiedName~TimelineGenerationCommandTests|FullyQualifiedName~TimelineGenerationPerformanceTests|FullyQualifiedName~TimelineGenerationRoundTripTests" --logger "trx;LogFileName=a1-core.trx" --results-directory artifacts/test-results/a1
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CommonEditingA1Tests.AcceptanceProjectRoundTripsAndCompiles|FullyQualifiedName~BoundedPointCommandTests|FullyQualifiedName~ProjectLogicalParameterPointEditCommandsTests|FullyQualifiedName~BoundedLogicalParameterMigrationTests" --logger "trx;LogFileName=a1-final-boundaries-and-sample.trx" --results-directory artifacts/test-results/a1
```

最后新增 5 个非法 stream/旧修订用例以单独 filter 运行并通过；重新运行上面的 CommonEditingA1 全类 filter 时，用例数会从 157 增至 162。复跑需要保留验收工程时，先在当前测试 shell 设置 `MIDORA_A1_ACCEPTANCE_DIRECTORY` 为仓库 `artifacts/acceptance/a1` 的绝对路径；否则该测试会删除自己生成的文件，不留下持久用户数据。

### 3.2 真实 WPF 与测试限制

- 以真实 BAML 加载、主题模板 Measure/Arrange 验证 New Project 和设置初始页，不只是读取 XAML 字符串。
- 隐藏 HwndSource 上创建真实 ComboBox Popup/ScrollViewer，测试仅在进程内设置 WPF hover flag 并发出路由事件。旧的“在 Popup 父 Grid 处理”确实拦不住，改为 item class handler 后 offset 保持不变；显式导航/非 hover 的 BringIntoView 仍能滚动。没有注入系统鼠标输入、操纵用户窗口或使用 computer-use。
- 真实虚拟 ListBox 使用 1 项和 50,000 项数据，验证 item offset、±120、−30 累积、方向反转、边界消费、外层 offset 不变和少量可视 container。
- Surface 用真实布局及手势事件检查 sampler tick 与最终饱和 delta。焦点导航补了 VM target 保持、当前 Workspace/target/可见性检查；未把这些自动检查说成实机鼠标/输入法验收。
- 100/125/150/200% 采用 WPF LayoutTransform 的布局倍率测试，**不是四种真实显示器 DPI 截图** 。没有实机高 DPI 的视觉样本，交人工确认实际字体/间距。

测试过程中曾有 fixture 自身的错误，已修正并重跑：事件碰撞测试起初绕过 Document 的 collision wrapper；验收工程起初比较了仅 resident 的 `Events` 和受存储身份影响的 fingerprint，现改为正式 `QueryEventPages` 全事件与 allocations。旧 `a1-sample-and-comparison.trx` 保留历史失败，不能作为最终总通过证据；其性能单项通过可独立使用。未通过的产品断言未被隐藏或改为跳过。

### 3.3 有界性能实测

合成 Direct PB 事件，准备前已构造源数据/ID 集；计时范围是 command Prepare、Apply、Undo，**不包含完整选择读取、真实窗口刷新、编译或音频** 。Prepare 继续允许 spill，不追求把整个事件集常驻内存。

| 规模 | Prepare | Apply / Undo（各） | 编辑资源峰值 | 测试进程 Peak Working Set |
|---|---:|---:|---:|---:|
| 400,000 | 18.15 s | 均 <0.05 ms | 67,104,768 bytes | 738,324,480 bytes |
| 1,000,000 | 48.51 s | 均 <0.05 ms | 67,107,840 bytes | 1,340,153,856 bytes |

上表来自先前独立核心批次。最终全套与其他测试并行的时间不用于覆盖这份对照；进程峰值含测试源对象等，不等同于软件总内存，也不等同于编辑专用预算。未接近约 9 GiB 的实验安全警戒。

同 400,000 输入、语义完全相同的 PB +128，按旧 byte API / 新 scalar API / 新 scalar API / 旧 byte API（ABBA）顺序执行，Prepare 依次为 **23.35 / 19.33 / 15.89 / 14.09 s** ，四次编辑资源峰值均为 67,104,768 bytes。旧 API 是本代码中保留的兼容入口，不是完整旧产品二进制；明显存在冷热影响，样本只有每组两次。结论仅是本次 scalar 修复未观察到量级退化，**不能据此宣称提速百分比或可靠 p95/p99** 。

没有重新运行 9KX2 整项目 UI、真实声音/underrun、多小时资源释放或严格 p50/p95/p99 实验；这部分扩展性能门仍无本轮证据。生产修改没有改 Piano 栅格缓存、音频或大型 Note 编辑的算法，不能把该事实当作这些路径已完成新一轮性能验收。

## 4. 一轮人工验收：UAT-A1

以下保留初轮验收清单。用户已确认 01、02、04、05、07、08、09 通过；03/06 的遗漏及新增 PB 显示只按 §6 小范围复验，自动通过不代签。可复用工程：

[a1-common-editing-806b61ced9f945de9d8e278b3efc8e61.midora](../artifacts/acceptance/a1/a1-common-editing-806b61ced9f945de9d8e278b3efc8e61.midora)

工程包括 `A1 MIDI`、`A1 Logical`、`A1 Template - end at 96`，后者含 `A1 Events` 和 `A1 Shared Template Length` 两个 SubVoice；Logical Segment 含 Integer/Double 两 Lane。SoundFont 仍只使用用户原程序设置，不随工程打包。该文件在被忽略的 artifacts 内；清理后可按 §3.1 的测试重新生成，不依赖这一次 GUID 文件名。

| ID | 操作 | 预期 |
|---|---|---|
| UAT-A1-01 | 打开 `A1 Events`，选 CC11，Snap 关闭，在 tick 96/其右侧单击，再绘一条跨模板末端的线；Undo/Redo。切另一个 SubVoice 再看。 | 新点不被旧末端吞掉；模板扩到最后点+1；一次 Undo 同时回退该次内容与长度；同 Definition 的范围同步。 |
| UAT-A1-02 | 在 MIDI PB、SubVoice PB、Logical Integer/Double 中分别框两点，上下拖过值域边界，含 Shift 固定 tick；再 Undo。 | 整组保持间距停在边界，提示/预览/松手一致；PB 跨进位无报错。无需百万选择做此人工检查。 |
| UAT-A1-03 | Catalog 的 Banks、Programs、Scan Presets 结果各滚一轮刻度，并在边界继续滚。 | 每刻度一项，外层不跟动；选项不因滚轮改变。Scan 仅在用户显式操作时读取 SF2 元数据，本轮不自动扫描用户音色库。 |
| UAT-A1-04 | 代表项：SubVoice Add Event / Controller（共享）、多选 Properties 的下拉（inline 特化）、Piano 顶部可输入 `1/n` 的操作细分下拉（editable）。指针静置下拉上/下边缘，再滚轮、拖柄及用键盘导航。 | 静置不自滚；显式操作仍可访问完整列表；其余 ComboBox 已由同模板扫描/测试覆盖，不要求逐个窗口重复验收。 |
| UAT-A1-05 | 打开 New Project，选择立即保存、浏览长路径，检查最小窗口宽度及 Create/Cancel。 | `Create` 文案正确，Browse 不裁字，路径和按钮可达；Enter/Escape 保持确定/取消。使用自己的实际 DPI 看一次即可；自动布局倍率不替代本条。 |
| UAT-A1-06 | `A1 Events` 停在已有 PB Lane 时 Add Event（选尚无的 CC7），确定后按 D/S/E；再次 Add Event 后取消。 | 成功直接进入 CC7、焦点回视图；取消不切 Lane，不产生新内容。 |
| UAT-A1-07 | 分别打开 MIDI Segment、Logical Segment、SubVoice 的 List，再调整宽度、隐藏/重显。 | 新列表默认 400 DIP；已手调宽度不被重置，隐藏时不额外全量加载。 |
| UAT-A1-08 | 看验收工程事件 List 的 Value/Type：PB、CC、RPN/NRPN、Poly Pressure、Bank。 | Value 不带无意义编号；目标仍可区分，Bank 等复合值不丢。Program 显示基数本轮不变。 |
| UAT-A1-09 | 点击底部 SoundFont 状态；Cancel；正常播放中 hover/点击该入口。 | 直达 SoundFonts；取消不保存；正常播放时禁用并解释原因，不意外停止播放。仅模板试听时允许沿用既有先停试听再打开设置流程。 |

## 5. 收尾与后续边界

- 本轮没有未解决的已运行自动测试失败；人工视觉、原生键盘/鼠标手感和上述扩展性能门仍待验证。
- A1 九项已落地，执行计划保留原 UAT 编号和历史调查；不将未测项标为通过，也不声称 A2/R27 的乐器包装、R28 Lane Tabs、A3 新手势已完成。
- 若本轮验收通过，下一默认实施批次为 A2a；提交、推送、本地发布仍须用户当前明确要求。

## 6. 验收返修：实际原因、验证与小范围复验

### 6.1 已确认根因与修复

1. **Scan Presets 不是只有扫描结果一个列表。** 有多个配置的 SF2 时，还会先弹出 `SelectionDialog` 选择源文件。初轮只覆盖 Bank 结果列表，遗漏了这个源列表；真实主题/模板测试测得单次 `Delta=-120` 的 offset 增量为 **40 项**（预期 1）。只给 Catalog 此入口启用已有的单项滚动模式，不改变其他 SelectionDialog 的行为；源选择与 Bank 结果模板均纳入测试，含结果行内 TextBox、分数轮刻度、反向、边界、选择与虚拟化保留。
2. **Add Event 导航早于绑定列表刷新。** DesktopSession 在 Dispatcher Render 优先级合并刷新；命令提交后 `RenderLanes` 尚无 CC7，旧逻辑立即 `TryActivateEventLane` 返回 false，并跳过事件视图焦点恢复。初轮测试手动先 Rebuild，掩盖了该真实顺序。改为在验证已提交的 owner/target 后记录稳定导航意图，再重建一次当前 Workspace，最后打开目标 Lane；旧事件选择保留且不覆盖显式导航。Input 优先级焦点仍在布局后执行，并检查当前 Workspace、SubVoice、target 和可见 Surface。
3. **Direct PB 的原始值域被直接用于标尺。** 将显示轴与批量命令原始范围分开：轴显示 `scalar−8192`，原始范围与字节换算保持 `0..16383`。无需重建事件数据；切回 CC 等 Lane 恢复原显示范围。
4. **SubVoice 中点 −1 来自 −0.5 的 AwayFromZero。** 对跨零整数标尺，当刻度距中性值不超过半个整数单位且 0 确实在当前可视范围内，改画/标实际中性刻度。PB 的 0 坐标是 `8192/16383`，不是硬把所有点或编辑舍入规则改掉。Label 与 Grid 线共用同一刻度坐标；连续参数轴及不含 0 的缩放窗口不受影响。

### 6.2 验证记录

- `artifacts/test-results/a1-fix/a1-fix-reproduced.trx`：修复前真实模板和带 Dispatcher 的命令顺序 **2 个预期失败**，分别为 40≠1 和新 Lane 激活 false。
- `a1-fix-before.trx`：初轮结果 Bank 模板（含编辑框）测试通过，证明问题在另一入口，不需要重写已正常的结果列表。
- `a1-fix-targeted.trx`：两处返修后 2/2 通过。
- `a1-fix-targeted-final.trx`：补绑定集成测试时有 2 个 fixture 失败：没有 presentation source 的 Tab Content 不可见，以及尚未经过异步发现的 PB target 未在测试内激活。补隐藏 HwndSource 与显式激活 target 后，`a1-fix-bindings.trx` **3/3 通过**。没有因此更改生产语义。
- 独立测试目录使用 `--artifacts-path artifacts/build/a1-fix`，避免覆盖运行中 Midora 锁定的 Release apphost。初次普通目录构建遇到 MSB3027/3021 锁定；没有关闭用户进程，不把该次记为成功构建。
- Desktop 完整回归 **447/447、0 skipped**：`artifacts/test-results/a1-fix/a1-fix-desktop.trx`；Presentation 完整回归 **482/482、0 skipped**：`artifacts/test-results/a1-fix/a1-fix-presentation.trx`。所有最新产品代码与测试均以 Release 编译；本轮没有运行整个仓库全套测试。Application/Compiler/音频生产代码没有新增修改，初轮核心层证据仍见 §3，不冒充本轮重新运行。
- 上述返修集成测试未使用 computer-use、未真实扫描用户音色库、未启动音频 Worker，不宣称已人工按 A 或完成实机视觉验收。绑定测试用不显示的 HwndSource 验证真实 Tab/Combo 状态与 Dispatcher 顺序；实际用户焦点和观感仍需下表复验。

可重跑的本轮完整回归（从源码重新构建，使用隔离目录）：

```powershell
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj -c Release --artifacts-path artifacts/build/a1-fix --logger "trx;LogFileName=a1-fix-desktop.trx" --results-directory artifacts/test-results/a1-fix
dotnet test src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj -c Release --artifacts-path artifacts/build/a1-fix --logger "trx;LogFileName=a1-fix-presentation.trx" --results-directory artifacts/test-results/a1-fix
```

### 6.3 本轮只需复验四项

| ID | 检查 | 预期 |
|---|---|---|
| UAT-A1-03（返修） | 配置多个 SF2 时，Catalog → Scan Presets 的源文件列表滚一格；再查看扫描结果 Bank 列表。 | 两个列表都是一格一项；结果行内输入框上滚动仍正确；边界不滚外层。 |
| UAT-A1-06（返修） | SubVoice 原先 PB/CC11 Lane 有选择时 Add Event → CC7，确定后不额外点击，按 A，再按 D/S/E。 | 直接显示 CC7；A 只切下方 Event Lane Snap，上方 piano Snap 不变；D/S/E 仍正常。取消 Add Event 不切 Lane。 |
| UAT-A1-F03 | MIDI Segment Pitch Bend 上看最小/最大/中性点，再切 CC7 后切回。 | PB 标尺 −8192～8191，中性 0；CC7 为 0～127；既有事件位置和值未被改写。 |
| UAT-A1-F04 | SubVoice PB 看全值域中性标记；纵向缩放/滚动后恢复。 | 全值域中间标出实际 0，不再为 −1；缩放不把视图外的 0 强行塞进视图。 |

## 7. Segment Add Lane 焦点遗漏补齐

用户已确认 §6 的其他修复通过；本轮只处理 Logical/MIDI Segment 在 Add Lane 成功后，`A` 仍切换上部 Piano Roll Snap 的遗漏，不进入 A2。

- 需求依据：SRS §18.2.5/7、§20.2.5；输入为当前 Segment 的成功 Add Lane 结果，输出为对应 Event/Parameter Surface 的键盘焦点。
- 已确认根因：两个 Segment 创建分支均未发出成功后的 Surface 焦点请求，通用模态关闭逻辑恢复到了之前的 Piano Roll。D/S/E 共用 Workspace 工具模式，所以这几个按键正常不能证明 `A` 的独立 Lane Snap 路由正确。
- 修复边界：只在成功分支把事件视图排为最终焦点目标；排在旧模态来源恢复之后、布局/数据绑定完成之后执行。取消/失败仍恢复原来源；Workspace 关闭/切换、Surface 卸载/隐藏/重新绑定后不恢复到失效目标。
- 正式 Selection、Lane 数据、Undo/Redo、编译、音频、持久化与已验收的 SubVoice 路径均不变；不增加内容枚举或后台任务。
- 验证计划：覆盖两种 Segment 的创建/绑定、Dispatcher 恢复顺序、独立 Snap 路由及失效目标保护；只需各一次 Add Lane 成功后不额外点击直接按 A 的人工复验。

### 7.1 实际修改与验证

- 产品修改仅在 `MainWindow.xaml.cs`：两个成功分支复用原模态焦点恢复队列，明确把 `ParameterLanes` Surface 排在原来源之后；Logical 创建失败不排新请求。原队列提取为可测试的内部静态方法，优先级、弱引用和有效性检查不变。
- `WpfInteractionRegressionTests.A1.cs` 新增两种 Segment 的 WPF 集成回归：真实 Lane ComboBox 绑定、正式创建命令和 Dispatcher 延迟刷新；隐藏 HwndSource 中检查逻辑焦点的最终目标，再验证该 Surface 对应的 `A` 设置路由只改变 Lane Snap。另覆盖取消/失败只恢复来源、目标隐藏、重新绑定和 Workspace 切换。没有注入系统按键/鼠标或打开真实主窗口，不以逻辑焦点测试冒充实机键盘验收。
- Release 定向构建/测试：**6/6、0 skipped**，含新增 2 项和既有 `TimelineCommandTargetTests` 4 项；记录 `artifacts/test-results/a1-fix/a1-segment-focus-targeted.trx`。
- 首次桌面全套：**448/449**，唯一失败是原 Popup hover 测试的 `handledAtItem` 断言（`a1-segment-focus-desktop.trx`）；Segment 新测试通过。没有改下拉框产品代码，仅为该旧测试增加开关/hover/鼠标键/显式导航状态的失败信息。带新信息的定向复跑 **3/3**（`a1-segment-focus-and-theme.trx`），未重现旧失败；不能据此断言其根因已确定。
- 最终桌面全套复跑：**449/449、0 skipped**（`artifacts/test-results/a1-fix/a1-segment-focus-desktop-final.trx`，测试报告持续时间 1 分 32 秒）。首次失败记录保留，不把一次复跑成功描述为已修复该旧测试的全部不稳定性；本轮新增的两项 Segment 测试在各次运行均通过。`git diff --check` 通过。
- 本轮不重跑未修改的 Application/Compiler/音频和 Presentation 全套；上轮证据仍见 §6。没有新增 Project command、UI 数据集合或内容扫描。

### 7.2 本轮只需复验

| ID | 检查 | 预期 |
|---|---|---|
| UAT-A1-F05 | 分别在 MIDI Segment 和 Logical Segment 中 Add Lane，确定后不再点击视图，直接按 A。 | 只切换下方事件/参数视图的 Snap，上方 Piano Roll Snap 不变；再按 A 可恢复。取消 Add Lane 保留原 Lane 与来源焦点。 |

### 7.3 验收与归档

2026-09-14，用户确认上述补齐验收通过，A1 本批人工验收关闭。按本次要求归档源码、回归测试、SRS 同步和本报告；不进入 A2a，不本地发布。此次提交只更新验收记录，不再次修改产品代码；沿用并核对已有 TRX，不声称重新运行测试，也不扩大 §3/6/7 已说明的验证范围。
