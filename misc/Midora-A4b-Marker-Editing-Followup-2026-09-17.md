# A4b 验收后：模板时间标记编辑与编译状态提示

日期：2026-09-17；验收收尾：2026-09-18。状态：原 UAT-A4b-01～05、本篇 UAT-A4b-F01～05、Template 标签及四种手柄圆角柔化均已获用户明确验收通过。自动测试证据与未覆盖项分别保留，不将人工验收扩大为全部平台、性能门和长期压力测试均通过。

## 需求追踪与设计记录

| 编号 | 输入与正式输出 | 边界、失败、归属 |
|---|---|---|
| A4b-F01 | SubVoice 顶部 ruler 的 Template/Loop Start/Loop End/Pre-Roll 手柄；通过已有 Definition 命令更新对应 tick | 一个手势一次 Undo；冻结 owner/revision/Snap，只量化 delta；合法区间饱和；取消、过期零提交；不改内容或 Loop 编译语义 |
| A4b-F02 | 手柄右键 Delete / Set Value；单文本框初始化当前值并全选，OK 后正式验证 | Template Delete 禁用；Pre-Roll Delete 归零且隐藏；Loop Delete 只清该端，沿用不完整 Loop 可编辑、编译 Error 的既有规则；错误保留弹窗和输入 |
| A4b-F03 | SubVoice 顶部 ruler 空处 Add Loop Start+End / Add Pre-Roll Point | Loop 显式置为整个模板 [0,L]，不自动开启实例隔离；不支持时菜单仍可用并明确报错。Pre-Roll 使用右击的实际 local tick，不做隐式 Snap；越界取模板中点，1 tick 模板取 1。正式验证/异常均显示可读错误，不部分修改 |
| A4b-F04 | 当前 Compiling 状态 → 顶部 Compile 左侧旋转图标及黄色文字 | 复用 Buffering 的几何/转速与颜色；结束/取消/失败停止动画并恢复启用/禁用普通颜色；不增加编译计数/队列、音频逻辑或文件字段 |

依据：本轮用户明确请求、SRS §7.27/32、§18.4.4、§17.7.1、INV-075/078/081/089/113。

实现决定：

1. 共用一种时间标记手势和合法范围算法，模板长度仍复用上一轮修订摘要。Loop Start 范围 `0..min(L-1, End-1)`，End 范围 `max(1, Start+1)..L`；缺失另一端时按模板边界。Pre-Roll 为 `0..L`。精确设值拒绝非法输入，不 Clamp。
2. 预览不改 Domain：独立预览值同时驱动三面板语义覆盖层；提交后正式重建，取消恢复原覆盖层。零值 Pre-Roll 表示未启用，不引入新的持久化“存在”字段。
3. 四个同款手柄复用现有蓝/黄/紫颜色，位于 ruler 的标签下方；发生视觉重叠时仅错开 cap 的命中位置，连线仍指向真实 Tick，拖动用相对 delta 消除 cap 偏移影响。不把重叠对象变成只能命中最上层。
4. 右键复用正式 Timeline ContextMenu 样式、生命周期及模态预览停止/焦点恢复。菜单/弹窗冻结来源与文档修订，不在晚回调中操作新 owner。
5. 所有数据修改复用已有正式原子命令，保持 Format 4、Undo、canonical 与消费者唯一主线。本轮不触及延期的逻辑参数点约束。

## 验证与人工验收

主要改动：

- `TimelineSurface.TemplateMarkers.cs`：统一四种手柄、范围算法、重合 cap 布局、冻结手势和预览属性；`TimelineSurface.cs` 只接入现有输入/取消/绘制路径。未改 Note/Event 栅格缓存。
- `MainWindow.TemplateMarkers.cs` / `TemplateMarkerEditing.cs`：菜单与弹窗包装，调用既有 Definition 命令；过期菜单/owner 在命令构造前拒绝。`TextInputDialog` 新增可选提交验证回调，原三参数调用行为不变。
- `MainWindow.xaml` / `DesktopSessionController.cs`：三个 SubVoice panel 绑定同一组预览值；Compile 绑定当前编译状态，黄色覆盖普通禁用灰色，状态退出时移除动画。
- 本轮未修改编译器与音频后端；工作区中相应模块的既有差异来自已经验收的 A4b，不应误记为本轮新改动。

自动验证记录：

- Presentation 定向 29 项通过，随后全套 **574 项通过**；覆盖原模板尾回归、四个 cap 在重合/低缩放/视口边界的独立命中、Loop 单端/成对/极端 Tick 范围、各类拖动一次提交、冻结 Snap、禁用隔离、取消与修订过期。
- 新增菜单命令/参数验证及实际 WPF 模板定向 **26 项通过**。其中真实模板测试覆盖菜单生成、整个模板 Add Loop、拖动至视图外、上/下方语义覆盖层实时同步、Undo/Redo、删除 Pre-Roll、失效来源拒绝、输入全选、非法 OK 不关闭，以及黄字/旋转/结束恢复。WPF 使用不显示的测试 host，不使用 computer-use。
- 完整 Desktop 回归 **506 项通过**（3 分 14 秒）；Application 模板范围/事件乐器命令定向 **14 项通过**；Playback 编译进度生命周期 **1 项通过**。合计本轮最终四组 **1,095 项通过，0 失败、0 跳过**；上面的定向测试不重复计数。UI 用例包含未修改区域的全套回归，不表示每种手柄都有上千个独立场景。
- 最终 Desktop Release 构建通过，**0 Warning / 0 Error**；按仓库换行配置执行 `git diff --check` 通过。未运行发布脚本，没有写入 dist。

复跑入口（逐条执行）：

```powershell
dotnet test src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj --no-restore -v minimal
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj --no-restore -v minimal
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~ProjectEventInstrumentEditCommandsTests|FullyQualifiedName~TemplateLengthSummaryTests' -v minimal
dotnet test src/midora-core/Midora.Playback.Tests/Midora.Playback.Tests.csproj --no-restore --filter FullyQualifiedName~CompilationProgressLifetimeTests -v minimal
dotnet build src/midora-desktop/Midora.Desktop/Midora.Desktop.csproj -c Release --no-restore -v minimal
```

人工检查清单（2026-09-18 用户确认以下项目验收通过）：

| 编号 | 操作 | 预期 |
|---|---|---|
| UAT-A4b-F01 | 在有 Loop 和 Pre-Roll 的 SubVoice 左右拖三个新手柄，开启/关闭 Snap，拖出面板垂直范围再松开；Escape 取消一次。 | 黄/紫手柄与原标记同色；上下视图的边界/阴影同步预览；使用合法范围和 delta Snap；松开一次 Undo，Escape 不修改。 |
| UAT-A4b-F02 | 右键 ruler 空处 Add Loop Start+End，再缩小水平视图、拖动与蓝色模板尾重合的黄色 Loop End。关闭实例隔离后再次 Add Loop。 | 直接成为整个模板 Loop，无配置弹窗；重合手柄仍分别可操作；关闭隔离时菜单仍启用，但执行后清楚提示先开启隔离且不修改数据。 |
| UAT-A4b-F03 | 右键四种手柄，分别 Set Value；尝试空白、负数、超模板值、Start≥End，随后输入合法值。尝试 Delete。 | 当前值全选，非法值留在原弹窗并显示原因，合法值 OK 一次提交；模板 Delete 禁用；Pre-Roll Delete 归零，Loop Delete 只清该端（缺端时编译 Error 属于既有合同）。 |
| UAT-A4b-F04 | ruler 模板内空处 Add Pre-Roll Point，再在模板右方空白处添加，Undo/Redo。 | 模板内使用右击实际 Tick（不额外吸附）；模板外使用模板中点，未因失败留下半状态；操作后所有关联视图更新。tick 0 仍表示未启用。 |
| UAT-A4b-F05 | 较大项目手动 Compile，编辑后观察后台编译，再检查有错误的编译结束状态。 | Compile 左侧旋转，文字为 Buffering 同款黄色；结束/失败后图标消失，文字恢复通常状态。很短的编译不保证肉眼看见中间状态，不为动画人为延迟。 |

自动测试未覆盖：真实多显示器/DPI 移动、9KX2 全流程长时间 soak 或音频听感回归。上述人工清单已获用户明确验收；不将此结论扩大为所有未覆盖场景均已验证。

本轮不提交、推送、本地发布，不使用 computer-use。

## 2026-09-18：Template 手柄标签

- A4b-F05（用户明确追加）：SubVoice 顶部 ruler 在 Template 手柄旁显示 `Template <Tick>`，颜色与蓝色手柄一致；拖动时显示实际预览 Tick，取消后还原。只属于显示层，不新增持久化、Undo、诊断或编译语义。
- 复用现有 Loop / Pre-Roll 标签绘制与低缩放合并逻辑，合并后各标签仍保持原色；仅主钢琴卷帘显示，不扩展至 Velocity / Event ruler，不改变手柄命中范围。
- 修改范围：`TimelineSemanticOverlay` 统一四色标签及模板端点可见性；`TimelineSurface.TemplateMarkers` / `TimelineSurface` 暴露只读预览值；`MainWindow.xaml` 只在 SubVoice 主 ruler 绑定。未改变 Note/Event 瓦片、正式手柄提交命令、音乐数据或音频。
- 自动验证：定向 43 项通过，随后完整 Presentation **575 项通过**（定向项不重复计数）；实际主题/主窗口模板集成入口 `ComboAndScrollBarThemesProvideDedicatedTemplatesAndChevronGeometry` **1 项通过**，包含 SubVoice 标签绑定、正式模板长度命令与 Undo 后立即更新的断言。最终共 **576 项通过、0 失败、0 跳过**；Release 构建 **0 Warning / 0 Error**，`git diff --check` 通过。未运行全套 Desktop 或本地发布。
- 2026-09-18 用户已确认 Template 标签验收通过；不扩大为未测平台均已验证。

## 2026-09-18：四种手柄圆角抗锯齿

- A4b-F06（用户明确追加）：Template、Loop Start、Loop End、Pre-Roll 的圆角 cap 柔化，避免随位置变化出现硬边像素缺失。输入仍为同一手柄布局和颜色，输出仅为显示；Tick、命中范围、拖动、Snap、Undo、标签和竖线不变，不增加音乐数据、诊断或持久化。
- 源码原因：`TimelineSurface` 使用 `EdgeMode.Aliased`；cap 直接在该画布 `DrawRoundedRectangle`，没有独立的抗锯齿路径。不能为了 cap 改变整张音符/事件画布的像素绘制合同。
- 实现方向：参照已验收浮动工具图标的独立 WPF 抗锯齿栅格化，小图按当前 DPI 缓存；只对最终位图整体定位，不逐边/逐角取整。缓存最多四项、DPI/尺寸/画刷变化替换、卸载释放；不按 tick/滚动/缩放位置累积。
- 定向 49 项通过：100/125/150/200% DPI、四种 cap、五种分数位置逐 alpha 桶对比，新路径保留全部非透明覆盖，原直接绘制路径确实没有半透明边缘。缓存热读复用、DPI/尺寸/画刷失效和卸载释放、实际 Timeline 保留 Aliased 设置及原命中范围均通过。常用 100%～200% DPI 的四项像素总量小于 8 KiB；10,000 次热读无栅格重建，分配不超过测试门限 1 KiB。
- Release 构建通过，0 Warning / 0 Error，`git diff --check` 通过。第一次完整绘图回归 580 通过 / 1 失败：既有纯音符栅格器计时测试 `SixtyThousandNoteContinuousResizeFramesRemainBounded(LogicalNote)` 的 p95 为 787.71 ms，超过 250 ms 门限；分配 38,465,664 bytes，在原上限内。该测试不调用 Timeline/手柄缓存；随后三个 Note 类型单独复测全部通过。首次运行曾与 Release 构建并行，但尚不能据此证明计时波动的唯一原因；不修改测试门限或音符路径。
- 第二次完整绘图回归（无并行构建）580 通过 / 1 失败：上一条音符计时已通过，但未改动的 `GlyphCacheIsBoundedAcrossDpiChangesAndWarmReadsDoNotRasterizeOrAllocate` 分配门限出现 3,096 bytes（门限 1,024）。独立复测通过，18,000 次热读 7.49 ms、0 bytes。两轮失败项不同且独立复测均通过，记录为尚未定位的全套性能断言波动，不声称完整回归单次全绿，也不扩大本轮修改范围。
- 修改文件为 `TimelineTemplateMarkerCapCache.cs`、手柄绘制及卸载处、新增 `TimelineTemplateMarkerRenderingTests.cs` 和本轮规格/验收记录；不修改普通图标缓存或音符栅格器。该实施轮未跑全套 Desktop 或音频测试、未提交/推送/发布。人工检查为拖动/平移观察四种 cap 边缘连续性。
- 2026-09-18 用户明确回复“验收通过。记录、提交、推送。”，确认四种手柄圆角柔化通过；此前 Template 标签及原标记交互的验收结论保持。

## 2026-09-18：A4b 验收归档与提交收尾

- 收录自 `87ba50ab` 之后的 A4b 正式实现、测试、规格同步和上述追加需求；本轮只更新验收状态并执行 Git 检查、提交、推送，不新增产品行为。
- 保留每次实际运行的自动验证结果：圆角柔化最后定向 49 项通过；两轮完整 Presentation 各有一项不同的性能断言波动，独立复测通过，但原因未完全定位。不得把本次人工验收或 Git 检查写成完整回归单次全绿；本次归档不重跑产品测试。
- 后续 B/C 批次未启动，`DEFER-LPARAM-01` 继续延期；不改变项目格式、音频语义或发布版本，不生成本地发布产物，不使用 computer-use。
