# Midora 阶段 3：Instrument Catalog、快捷 Parameter Mapping 与 Track Color Requirement Trace

状态：已实施并于 2026-09-04 经产品所有者验收

日期：2026-09-02

上位规范：《Midora SRS》§6、§9、§11、§17、§18、§20、§22～24；`Midora-Major-Editing-and-Visualization-Expansion-Implementation-Plan-2026-08-31.md` 的 `WP-04` 与 `WP-09` Track Color slice；`Midora-Instrument-Catalog-Quick-Mapping-and-Track-Color-Architecture-Decisions.md`。

## 1. 输入与正式输出

- Catalog 输入：内置 GM Profile、用户创建/导入的 Profile、用户显式扫描的已配置 SF2 `phdr` 元数据，以及当前程序级 SoundFont 顺序和 Enabled 状态。
- Catalog 输出：`<ProgramRoot>\Data\Catalogs` 下的严格版本化程序级数据，以及带来源的 UI 名称解析；Project 和 canonical 仍只保存数值 Bank/Program。
- 快捷 Mapping 输入：当前 Event Instrument revision、Logical Parameter 名称、一个合法 non-Note MIDI target、当前冻结的一个或多个 SubVoice、Integer source/range、Override/Add/Multiply 与 Append/Replace 决定。
- 快捷 Mapping 输出：一个 Logical Parameter、每个目标 SubVoice 一个正式 Mapping、必要的空 event owner，以及精确的一次 Project Undo；不创建任何事件点。
- Track Color 输出：每个新建/导入 Pure MIDI Track 的 concrete palette color、Duplicate 继承、Properties 原子更新；Logical Track 继续保留 Definition 继承与可选 override。

## 2. 边界、失败条件与确定性

- Catalog、名称、Profile 与 SoundFontEntryId 不进入 `.midora`、Project Modified、Undo/Redo、compiler、canonical、音频计划或缓存身份。
- Catalog 文件损坏、SF2 扫描失败或绑定 SoundFont 缺失只影响 Catalog 操作/名称解析；不得阻止音乐工作流。
- SF2 raw bank >127 未显式映射时禁止提交对应条目；绝不截断、取模或猜测。
- `All SubVoices` 只冻结当前列表；未来 SubVoice 不自动获得 Mapping。
- 快捷 Mapping 任一目标失败则零提交；Append/Replace 的 exact target 顺序稳定，其他 targets 顺序不变。
- CC91/CC93 继续不允许出现在 Event Instrument/SubVoice；Pure MIDI 规则不变。
- Pure MIDI palette 只由最终 global Arrangement 位置之前的 Pure MIDI Track 数量决定；不依赖 Root、随机数、hash 或枚举顺序。

## 3. 诊断、持久化与运行时归属

- Catalog store/Import/Scan 错误属于 Application operation message，不进入 Compiler Diagnostics。
- Mapping 配置成为 Project source，继续由 semantic validation、Full/Incremental compiler 和 source trace 诊断。
- Track color 成为 Project display metadata并沿用现有 Format 3字段；Catalog 不要求 Project Format 变更。
- Catalog Editor、Preferences SoundFont target 显示和 Properties 通过可替换 resolver snapshot消费名称，不使用 Domain 静态全局。

## 4. 明确非目标

- 不让 Catalog 验证 Program 是否存在，不自动切换 Bank/Program，不改变 BASSMIDI SoundFont 选择。
- 不在 Preferences Apply、Project Open 或播放 Preparing 时扫描 SF2，不解析 sample/modulator/SFZ 依赖。
- 不新增 Double/Enum quick binding，不创建动态 All-SubVoices group，不在 tick 0 写默认事件。
- 不删除 Logical Track ColorOverride，不按 palette 重染已有 Track，不让颜色触发编译或音频失效。

## 5. 用户验收重点

- Catalog 手工编辑、排序、启用、导入/导出、Replace/Merge preview、显式 SF2 Scan/Rescan与名称来源；
- SoundFont 重排/Enabled 后名称优先级即时刷新，Catalog-only 修改不重建 Worker；
- 快捷 Mapping 的当前/多选/全部 SubVoice、Override/Add/Multiply、Append/Replace、缺 Lane 自动空建、无 tick0、一次 Undo/Redo、保存重开；
- All 后新增 SubVoice 不自动绑定；非法任一目标不留下 Parameter/Mapping/Lane；
- MIDI Track 新建/导入的八色轮换、Duplicate/Copy/Paste 继承、Properties 改色；
- Logical Track override 只影响本 Track，清除后恢复 Definition 色，不影响共享 Definition 的其他 Track。

## 6. 实施结果与验证证据

- Instrument Catalog 已实现严格版本化 store、内置 GM / 用户 / SF2 Profile、手工编辑与排序、Import/Export、Replace/Merge preview、显式 SF2 `phdr` Scan/Rescan，以及按冻结优先级解析的 UI 名称来源。
- Application Preferences schema 已升级并为 SoundFont 条目保存稳定 `SoundFontEntryId`；旧 schema 只根据规范化路径生成迁移 ID，不读取或散列 SoundFont 文件内容。Catalog 修改不触发 Audio Worker 重建。
- 快捷 Logical Parameter Event Binding 已实现 Current / Selected / All SubVoices、Override / Add / Multiply、Append / Replace、缺失 Lane owner 空建、一次原子 Undo/Redo，以及失败前精确 Stable ID 容量预检；任何 Apply 失败不留下 Parameter、Mapping、Step、Lane 或 ID 消耗。
- Pure MIDI Track 已实现确定性的八色 palette、标准/流式 MIDI 导入轮换、新建轮换、Duplicate/Copy/Paste 继承与原子 Properties；Logical Track 保留 `ColorOverride ?? Definition.Color` 语义。
- 最终无还原构建：`midora-desktop.slnx` 成功，0 Warning、0 Error。
- 最终自动回归：Application 553/553、Compiler 346/346、Persistence 89/89、Desktop 190/190、Desktop Presentation 329/329、MIDI Export 41/41、Audio Render 37/37、Playback 114/114、MIDI 22/22、Common 79/79，合计 1800/1800 通过。
- Desktop 第一轮在并行测试负载下曾有一个既有瓦片时序用例超时；该用例单独复跑通过，随后在无并行测试干扰下完整 Desktop 190/190 通过。没有用偶发单测复跑替代最终全套结果。
- 2026-09-04 首轮人工验收发现 `Add Event Binding...` 在 BAML 初始化期间由 `RpnBox.TextChanged` 提前进入尚未完整连接 named controls 的刷新路径。修复使用统一构造初始化屏障，并在 `InitializeComponent` 前建立 choice 数据；所有变化事件和内部刷新 helper 在初始化结束前均为 no-op。真实 STA、生产 Application resources 与真实 Dialog BAML 构造测试现覆盖“存在当前 SubVoice”和“无 SubVoice/无 current”两条路径；修复后完整 Desktop 190/190 通过。
- 2026-09-04 UI 复验修正：`Existing Target Mappings` 的局部 item style 现在显式继承公共 `ComboBoxItem` 暗色主题；SubVoice 选择器改为非 `Selector` 的虚拟化 `ItemsControl`，只允许 CheckBox 表达选择，禁用时继续使用透明主题背景。真实 BAML 回归锁定主题继承、无行选择、透明禁用表面与 Recycling 虚拟化配置；修复后 `midora-desktop.slnx` 以 0 Warning / 0 Error 构建，完整 Desktop 190/190 通过。
- 2026-09-04 `Add Event Binding...` 的 `CONTROLLER` 下拉框删除独立生成的 118 项列表，直接复用 SubVoice `Add Event` 的 `MidiControlChangeCatalog.EditableControllers`。两处现共享同一 28 项范围、顺序与 `<number> - <name>` 显示文本；该 UI 收窄不改变已有合法 Mapping 的领域读取或编译能力。
