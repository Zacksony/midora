# Midora Software Requirements Specification — Initial Release Scope

> 中文引用名：**《Midora 软件需求规格说明书（初版范围）》**  
> 日常简称：**《Midora SRS》**  
> 规格版本：**v0.1**  
> 生成日期：**2026-07-15**  
> 最近修订日期：**2026-09-18**
> 文档形态：**按章节拆分的 Markdown 规格书**

## 文档定位

本规格定义 Midora 初版的产品范围、领域语义、编译与输出规则、持久化格式、UI 工作流、错误边界和明确不支持内容。它是初版实现、测试、评审和需求变更的产品基线。

本规格不固定最终 C# 类型、具体算法、第三方 API 调用、线程模型或控件实现；这些实现选择必须满足本规格的外部语义和不变量。

## 阅读顺序

首次阅读建议按章节顺序进行。开发中可通过第 22 章快速定位主题和跨系统不变量。

## 目录

1. [产品范围、定位与总体目标](01-Product-Scope-and-Positioning.md)
2. [系统模型、术语与符合性约定](02-System-Model-Terms-and-Conformance.md)
3. [Project 模型与应用生命周期](03-Project-Model-and-Application-Lifecycle.md)
4. [时间、Conductor Track 与全局音乐事件](04-Time-Conductor-and-Global-Musical-Events.md)
5. [Port、Channel 与资源模型](05-Port-Channel-and-Resource-Model.md)
6. [SoundFont 与声音资源](06-SoundFont-and-Sound-Resources.md)
7. [Event Instrument Library 与 Event Instrument 定义](07-Event-Instrument-Library-and-Definition.md)
8. [SubVoice 与 MIDI 事件编辑](08-SubVoice-and-MIDI-Event-Editing.md)
9. [曲线、Logical Parameter 与映射](09-Curves-Logical-Parameters-and-Mapping.md)
10. [实例生命周期、Loop、Envelope 与重叠](10-Instance-Lifecycle-Loop-Envelope-and-Overlap.md)
11. [Logical Track、Segment 与编曲语义](11-Logical-Tracks-Segments-and-Arrangement-Semantics.md)
12. [编译系统与 Canonical Compiled Result](12-Compilation-and-Canonical-Compiled-Result.md)
13. [播放与预览](13-Playback-and-Preview.md)
14. [MIDI 导出](14-MIDI-Export.md)
15. [音频文件渲染](15-Audio-File-Rendering.md)
16. [.midora 文件格式与持久化](16-Midora-File-Format-and-Persistence.md)
17. [UI 框架、导航与全局界面](17-UI-Framework-Navigation-and-Global-Surfaces.md)
18. [编辑工作区与编辑器](18-Editing-Workspaces-and-Editors.md)
19. [Project、文件、输出与任务工作流](19-Project-File-Output-and-Task-Workflows.md)
20. [通用交互、验证与 UI 验收边界](20-Common-Interaction-Validation-and-UI-Acceptance.md)
21. [初版范围边界、实现自由度与变更控制](21-Initial-Release-Scope-Boundaries-and-Change-Control.md)
22. [主题索引与跨系统不变量](22-Requirement-Locator-and-Cross-System-Invariants.md)
23. [Pure MIDI Track 与 Standard MIDI File 导入](23-Pure-MIDI-Tracks-and-SMF-Import.md)
24. [Arrangement 平铺轨道、共享执行组与概览渲染](24-Arrangement-Hierarchy-and-Preview.md)

## 文档版本规则

- **Initial Release Scope** 表示产品范围，不表示文档草稿序号。
- `v0.x` 表示整合和审查阶段；成为正式开发基线后可升级为 `v1.0`。
- 后续修订必须说明受影响章节，避免在实现中静默改变需求。

## 2026-09-18 修订摘要

- B1 按已确认 D-STATE01/03 实施会话内 Track profile、Segment/SubVoice 局部记忆及 owner 生命周期；同步 §17.2.3、§18.2/4、§20.1.4/6、INV-123。仅共享用户偏好，保留各 owner 的时间解析上下文；不增加 Format 4 / presentation schema 2 字段，不提前实施 B2/B3。设计及验证见 [B1 实施记录](../Midora-B1-Workspace-State-Implementation-2026-09-18.md)。

- 按用户明确追加需求，§18.4.4 在 SubVoice 模板尾手柄旁显示同色蓝字 `Template <Tick>`，拖动同步预览；复用既有标签合并，不改变命中、音乐数据或持久化。上一轮标记编辑与编译提示已获用户验收，记录见 [追加记录](../Midora-A4b-Marker-Editing-Followup-2026-09-17.md)。
- Template 标签及追加的 §18.4.4 四种手柄圆角矩形局部抗锯齿均已获用户明确验收；保持 Tick、命中及其他内容的像素规则，验证与验收记录沿用上述追加记录。

## 2026-09-17 修订摘要

- A4b 按已批准 R15/R22/R31/R32、D-UI05/06/07 落地 §18.4.4 模板长度手柄、§20.1.3 全局锚定小节标签、§17.7.1 编译阶段反馈与 §18.11.2 All Tracks 首次垂直适配。保持模板内容/Loop/Pre-Roll 下界、正式编译语义和格式不变；进度无可靠总量时只显示阶段，不额外全量计数。验证及人工验收入口见 [A4b 报告](../Midora-A4b-Template-Timeline-Feedback-2026-09-17.md)。
- 2026-09-17 用户确认 UAT-A4b 全部通过，并新增 §18.4.4 的 Loop/Pre-Roll 手柄及右键增删/精确设值、§17.7.1 的编译中旋转图标与黄色文案，同步 INV-113。只扩 UI 编辑入口，既有字段、合法范围、Undo 与编译语义不变；设计和新增验收见 [追加记录](../Midora-A4b-Marker-Editing-Followup-2026-09-17.md)。
- 按 A4a 验收后的明确需求改动，§18.2.10／INV-122 的辅助 Lines 扩大为三宿主除 Vel.／Inst. 外的所有 Lane，包含 Bank、Program、协议命令 CC 和 Imported Meta／SysEx；辅助线用于位置和自身前驱提示，不代表 MIDI 状态持续，也不解释 opaque payload。
- 删除逐 Lane 工具栏 Lines 开关，改为 §17.2.2 Application Preferences → Appearance 的全局 `Show event lane lines`，默认启用，保存后更新当前及后续 Lane；不进入 Project／Undo／Modified，不因此重建音频 Worker。原始数值、曲线插值和编译语义保持不变。实施与样例修正见 [验收后修订记录](../Midora-A4a-Acceptance-Followup-2026-09-17.md)。

## 2026-09-15 修订摘要

- A4a 按已确认 R12/R29、D-VAL01.a～d／D-STEP01～04 落地指定 CC 的外侧友好域与三宿主可关闭辅助阶梯线；Project/Mapping/raw/canonical/音频和 Format 4 不变。Event Batch/Generator profile 与 numeric contract 升至 v2，旧 Event Preset 不静默重解释。见 §8.54.4、§18.2.9/10、§20.4.13、INV-104/121/122 及 [A4a 实施记录](../Midora-A4a-Event-Display-Implementation-2026-09-15.md)。

- A3 后续用户明确修订：浮动图标 15 DIP 居中、27 DIP 按钮；Timeline 内容空白右击在有有效选区时提供完整选区菜单，无选择才使用容器菜单，混合类型仍显式分组。修复内容菜单与标尺原生菜单复用造成的坐标偏移。见 §20.3.7、§20.4.12、§20.7.3/4/9.1、INV-097/098、A3 记录 §8；不改变格式或音乐语义。

- A3 后续视觉反馈：共享菜单图标列扩至 28 DIP，确保图标本体空间与图文间距分离；浮动工具保留 16 DIP 图标与三行分组，按钮收紧至 28 DIP、行距 2 DIP、外边距 4 DIP，并隔离时间线 Aliased 设置以平滑绘制图标，不改变音符/网格或操作语义。见 §20.4.12、§20.15.4、INV-098 与 A3 记录 §7。

- A3 浮动工具视觉再次按用户要求修订：16 DIP 图标、优先同款原生 16px 资源，Pin 单一图标保留背景状态；左右边界改 ArrowExportRtl/Ltr，Move 改 ArrowMove；按钮区最多三行，Deselect All 移至第一行末尾。取代下述首次返修的较大图标与独立末行布局，编辑行为不变。见 §20.4.12、INV-098 与 ADR-UI-045 紧凑布局修订。

- A3 首轮验收后明确 Note Copy+Move 的 pitch 规则：不再整组 Clamp，越过 0–127 的副本丢弃，源音符保留；三种钢琴卷帘的普通/浮动 Ctrl / Ctrl+Alt 一致，事件 value Clamp 不变。浮动工具固定分组多行、较大图标、常驻即时名称栏，新增独立末行 Deselect All。见 §20.4.12、§20.5.5/6、INV-098 与 ADR-UI-045 验收修订。

- A3 按已确认 D-UI01/02/04/09 同步 §18.2/4/7、§20.1/3/4/5/7、INV-097/098：取消右键双击，右拖框选，未拖松开立即冻结目标菜单；绘线改为共享 Draw 下独立左键 Free/Line/Horizontal。增加 Ctrl+ruler 编辑指针、跨模式 13 命令浮动工具及捕获出界持续滚屏。原框选 Ctrl 表按 ADR-UI-023 更正为 Add，Ctrl+Alt 为 Toggle；音乐、编译、格式不变，工具 profile 持久化后置 B。实现/已跑与未跑证据见 [A3 记录](../Midora-A3-Gestures-Implementation-2026-09-15.md)。
- A2b 验收返修按用户明确要求补充 §18.2.5/§18.4.3：所有 Lane 的 `+` 统一 Add Lane，整块 Tab 头参与本 owner 重排；Inst. 补齐固定 y 创建预览、共用蓝色框选和中键水平平移。仅涉及会话 UI，不改变包装 source、编译或格式。
- A2b 按已确认 R27/R28、D-IN02、D-LANE01 同步 §8.55.4、§18.2.5/7/8、§18.4.2 和 INV-120：完整 Instrument Change 的水平批量编辑、List 合并与 raw 成员选择；三宿主 target Tabs、隐藏目录、精确摘要、独立纵轴和显式导航。Lane 状态只属于会话，不升级 Format 4 或 presentation schema，不改变编译/音频语义。实现及已跑/未跑验证见 [A2b 记录](../Midora-A2b-Instrument-Changes-and-Lane-Tabs-Implementation-2026-09-14.md)。

## 2026-09-14 修订摘要

- A2a 按已确认 R27 承接 §8.55/§13.31/§18.4：显式 Instrument Change 关联、Catalog 数值/名称选择器、Program 0～127、Initial State 独立继承和 owner 隔离试听。§16.35 采用 Format 4 保存关联 source，保持旧 1/2/3 读取及旧字节基线。完整包装批量 UI/List 仍为 A2b，不把本次切片当作 R27 完成。

- A1 验收返修：明确 Scan Presets 的源 SF2 选择与 bank 结果均为单项滚动；Add Event 不得在旧绑定列表中查询新 Lane 而跳过导航，`A` 属于新活动事件视图。按本轮明确要求统一 Direct/SubVoice PB 标尺为 −8192～8191、中性刻度 0，仅改变显示，不改变正式 scalar。
- 按已确认的下一批需求 A1 同步第 17～20、22 章：SubVoice 模板外创建点自动扩展同 Definition 模板并同事务撤销；现有 Add Event 成功后按稳定 target 导航并恢复 Surface 焦点。
- 明确三类数值事件批移采用整组选区共同 scalar delta 饱和；Direct Pitch Bend 必须先在 14-bit 标量上变换再编码，预览/提示/提交一致。Enum 继续只支持精确设值、不支持相对值 Delta。
- Catalog 的 Banks/Programs/Scan 列表每标准轮刻度一项、高精度余量按控件隔离；展开 ComboBox 禁止纯悬停引起的边缘自滚，保留显式导航和滚动。三类对象列表默认宽度 400 DIP，值列按事件类型格式化，不丢目标身份。
- New Project 主操作统一为 `Create`，Browse/布局不得裁字；SoundFont 状态入口直达程序设置 SoundFonts 页并服从既有播放/任务锁。只同步本切片，不提前改变乐器变化点、Lane Tabs、后续手势、软件版本或 Project/presentation 格式。实施证据与待人工清单见 [A1 报告](../Midora-A1-Common-UI-and-Editing-Implementation-2026-09-14.md)。

## 2026-09-10 修订摘要

- 用户定案：超出 `0x0FFFFFFF` 的 delta-time 只在 SMF 导出时以零长度 Text Meta `FF 01 00` 分段；原事件位置/顺序/EOT 不变，编译与增量编译不新增间隔检查，Project/canonical/音频不包含这些占位。
- 每个 MTrk 数据区最多 `0xFFFFFFFF = 4,294,967,295` 字节（4 GiB − 1 byte，不含 8 字节 chunk 头），明确不拆分；超限只使本次 MIDI 导出原子失败，编译不感知，也不把该限制误作整个 `.mid` 的大小上限。
- 保持 TPQ、Tempo、事件值、已解释 Meta、单条 payload 长度和 ntrks 等硬限制；新增填充字节预检、安全算术、有界写入/取消和导出级汇总 Info/README 要求。同步第 4、12、14、22、23 章与 INV-118。
- 本次为文档定案，代码尚未实施；取代 Q-NUI-021 / ADR-CORE-010 中“超长 delta 一律失败、不插占位”的旧决定。详细状态与后续验证见 [SMF 导出边界设计记录](../Midora-SMF-Export-Timing-Padding-and-Size-Limits-Architecture-Decisions.md)。不修改软件版本、Project Format 或 presentation schema。

## 2026-09-09 修订摘要

- 文档收尾：将 INV-091 中残留的 presentation schema v1 writer 描述同步为 2026-09-08 已批准的 schema v2、继续读取 v1 为 custom，与 INV-116 及 §16.7.5 / §16.33 一致；不是新增格式或改变语义。
- 用户批准取消诊断总数的 Int32 边界：完整诊断逻辑序列、ordinal 和严重程度统计使用非负 Int64；超过计数上限时明确失败，不截断、不发布不完整新结果。WPF 仅在筛选后数量超过 Int32.MaxValue 时分页，每页 4096 行，筛选与计数仍作用于全源。
- MIDI 导出 README 的 Warning / Info 文本最多写前 1000 条，并在超过时显示精确总数和省略数；此限制不适用于正式诊断或 Error / Warning-as-error 判定。同步 §12.19.10、§14.15.4、§17.5、INV-117；不改变 Project Format、presentation schema、音乐语义或软件版本。

## 2026-09-08 修订摘要

- 用户继续确认：洋葱皮 Settings 只保留透明度，手选来源单独弹窗；Previous/Next 为独占邻居显示命令，手选列表独立保留，快捷模式也随显式保存/重开恢复。独立 presentation schema 升为 2、继续读取 v1 为 custom，Project Format 仍为 3。All Tracks 标尺/内容单击复用既有播放 Seek。同步 §16.7.5、§16.33、§18.11、INV-116。
- 根据用户验收更正，Onion / All Tracks 入口统一为缩放同组的 LayerDiagonalRegular 图标；Onion 原地菜单支持启用、相邻来源与设置。All Tracks 支持播放指针和统一跟随规则。Compiled 明确为“逻辑 canonical 展开 + 当前 Pure MIDI 源音符”的混合显示，不重建 Pure MIDI 整曲 FIFO 索引。SRS 18.11、INV-116 同步；音乐编译、播放/导出、Format 3 / presentation schema 1 不变。

## 2026-09-07 修订摘要

- 阶段 8 落地已定案的 Track/SubVoice 只读洋葱皮、All Tracks Raw/Compiled、来源色与 FIFO 配对、过期显示和有界后台缓存；沿用 Format 3 / presentation schema 1，不改变音乐编辑、编译及音频语义。明确 Duplicate remap、删除引用 dormant/Undo、显式保存及正式叠加顺序。受影响章节：3、16、18、22；INV-115～116。
- 根据阻塞 BUG 复现澄清 Loop 进入条件：除短音 One-Shot 外，到 Loop End 且 Gate 尚未结束即循环，不再要求 Gate 超过 Template Length；统一原始事件、曲线与 Mapping 的模板时钟，保留既有硬结束并明确循环后 Tail 的时间映射。受影响章节：7、10、22，新增 INV-114；不改变 Project Format。
- 阶段 7 同步已批准的三种钢琴卷帘虚拟对象列表、显式 Note/Event 类型子菜单和完整选择 Undo/Redo；列表只属于 Session，隐藏停止后台读取，不引入新 Project 格式或音频语义。
- SubVoice 使用独立 Pre-Roll/Loop 覆盖层：半开前缀暗区、单端/双端黄线、顶部 Tick 标签；不使音符/事件缓存失效。受影响章节：18、20、22，新增 INV-112～113。
- 阶段 7 验收后的明确调整：Pre-Roll 标签为紫色；Configuration 的 Pre-Roll 空白输入静默归零；SubVoice 禁用 Time Range 拖选及对应菜单，不影响对象选择。受影响章节：18、20。

## 2026-09-06 修订摘要

- Conductor 编辑器改为左侧虚拟事件列表、右侧 Tempo 阶梯图及其他正式元事件 lane。Tempo 沿用 Event Lane 绘线与修饰键，显示轴与合法 BPM 范围分离；密集显示只做设备列 LOD，不丢正式事件。编辑采用可取消、有界准备及原子发布，五种音乐事件和持久化 wire 契约不变。
- 加入三类音符与三类数值事件的 Batch Create，冻结独立 generate-note/event profiles、递推变量、Initial 首对象开关、候选上限、相对 Tick、有限内存碰撞归并与程序级 Preset；旧 Batch Edit 和 Mapping ABI 不变。
- Logical/MIDI Segment 的拖动、复制、粘贴统一支持显式双向转换和多项混合选择；保留 crop 外音符，所有非共同数据（含空参数 Lane）汇总确认，失败不改源，一次 Undo 与目标选择。

## 2026-09-04 修订摘要

- 初版正式加入三类 piano roll 的 Note Humanize、Note Split / Join 以及 Note/Event Quantize。Humanize 固定只作用于 Tick/Gate/Velocity，提供可复现 seed；Split 使用 owner-local 全局刀线，Join 按 owner+key 合并；Quantize 复用正式 Time Signature/Grid 服务并固定碰撞与中点规则。
- 新增受限数值工具表达式 profile：`midora.tool.batch-note/v1`、`midora.tool.batch-event/v1`、`midora.tool.note-split/v1`。三者共用 8,192 scalar / 512 syntax node / 64 depth 的语法、API 和资源边界，但变量 schema 互相隔离；工具表达式与 Project 内 Mapping Function ABI v3 是两条独立的版本轴。
- Split 的 Expression 模式默认最多 65,535 刀；Fixed Piece Length / Maximum Piece Count 不受该可配置安全停止值截断，三种模式共用 16,777,216 刀硬上限。大型工具仍服从 detached paged transaction，结果记录最多 100,000,000、working/resident 各 64 MiB、owned spill 16 GiB。任何取消、超限、算术错误或 revision race 都必须零发布。
- Tool Preset 只保存于 `<ProgramRoot>\Data\Presets`，携带 schema/profile/tool/数值契约版本，每次加载必须按当前白名单与资源上限严格重验证。
- 清理第 7、8、9、11、16、21 章中尚未被取代的自由 C# / ABI v2 措辞：当前只有受限 Mapping Function Expression ABI v3，不 Emit/加载 Project 源码程序集；旧 ABI v1/v2 只可识别并明确拒绝。

## 2026-09-02 修订摘要

- Instrument Catalog 固定为 `<ProgramRoot>\Data\Catalogs` 中的程序级辅助数据：多个有序/可启用 Profile、General MIDI 内置表、用户 override、Imported SF2 快照及带来源名称解析。每个 SoundFont Preference 项新增只用于 Profile 关联的稳定 GUID；ID、Catalog 与名称不进入 Project、音频身份或 canonical。
- SF2 preset 读取只允许用户显式 `Scan Presets...`，流式读取 RIFF `sfbk/pdta/phdr` 并跳过 sample；raw bank >127 必须显式映射。Catalog store/import/export 使用严格版本化 JSON、资源上限、预览式 Replace/Merge与原子发布，损坏只回退名称。
- Event Instrument 新增一次完成的 `Add Event Binding...`：冻结当前目标 SubVoice、创建一个 Integer Logical Parameter、每目标一个正式 Mapping及缺失空 owner，不创建 tick 0 event；All 不动态扩展。Override/Add/Multiply复用 accumulator，Multiply精确支持 source→factor range，冲突显式 Append/Replace/Cancel并保持一次Undo/Redo。
- Pure MIDI Track 固定八色低饱和 palette，按最终 global Arrangement位置轮换；SMF导入和新建使用该规则，Duplicate/Copy/Paste继承，既有 Track 不因排序/删除重染。Logical Track保留现有 Definition继承和独立 ColorOverride。

## 2026-09-01 修订摘要

- Timeline 右键双击改用 Midora 自有固定判定：首个未拖动 `Right Up` 到第二个 `Right Down` 的时间差必须位于 `[0, 300 ms)`，两次位置的水平、垂直位移分别不得超过 `6 DIP`；不得依赖 WPF `ClickCount`、Windows 双击时间或系统双击空间范围。单击菜单使用同一 300 ms 窗口，冷页异步命中、冻结 target、右键 trace、取消与 revision gate 规则不变。
- Select 模式浮动工具默认处于 Follow；Note 与 Arrangement Segment 均提供左边界、右边界两个可区分的 Resize，Move 在既有 Copy Drag 能力明确支持时允许 `Ctrl` 触发 Copy+Move，否则手势 Invalid。普通直接操作与浮动工具的 Move/Resize 在所有选择规模下都显示最终 Snap/Clamp 后的 delta，并共用小选择矢量、大选择瓦片预览管线；不得因选择超过阈值而只留 delta、隐藏对象预览或建立逐对象 WPF 控件。

## 2026-08-31 修订摘要

- 大规模 Timeline Selection 新增 revision-bound ordinal/page/range query 契约：stable ID 仍是唯一业务身份；百万对象范围可使用 page interval/bitmap 与 sparse exceptions 表达并流式解析，禁止把全量 boxed ID、HashSet、WPF item 或全 source `ToArray()` 作为正常入口。
- 大型编辑新增 detached paged transaction 与 compact Undo 基线：默认 page 4,096 records、每 256 records 检查取消、单页 decoded/encoded working buffers 与 resident staging 各 64 MiB、owned spill 16 GiB、candidate/result 100,000,000 records；只有全部验证成功且 revision 未变化时一次 root swap，并向 UI、Compiler 与缓存发布同一个精确 source trace/change set。详见 `Midora-Paged-Selection-and-Edit-Transaction-Architecture-Decisions.md` 与 `Midora-Stage-2-Paged-Selection-and-Direct-Manipulation-Requirement-Trace.md`。
- Note 创建 Snap 改为量化 Pointer Down 后的长度 delta，不重写初始长度；Arrangement 多 Segment Resize 预览与提交使用同一向量规则；低缩放 Velocity onset marker 保持固定设备尺寸；Preview Keyboard 黑/白键 velocity 各按自身可见长度归一化。
- Timeline 发起的模态窗口关闭后恢复原 Surface 焦点；Event Instrument/SubVoice Initial State 的合法整数越界值按 target 值域 Clamp。每次 Playback Start 读取当前已提交 Project revision，新建 Instrument/Usage/Track/Segment/Note 不得依赖 Save/Reopen 才进入首次播放。
- 当前 `.midora` writer 提升为 Project Format 3，新增且只新增严格索引的 `settings/project-presentation.json`。该文件承载显式允许跨会话恢复的 Project presentation 数据；首个 schema 仅冻结 Track/SubVoice Onion 配置与 All-Tracks 显示模式的容器，不提前引入 Onion UI。presentation 不属于 Project Source Data，不参与编译、canonical fingerprint、音频缓存或 Undo/Redo；损坏时隔离为默认 presentation 并报告 Warning，不得损坏音乐内容或标记音乐 Project Modified。
- Format 1/2 打开继续 detached migration，打开阶段不写来源。迁移会话执行普通 Save 时，先显示并冻结原路径、来源/目标 Format 与永久旧版副本路径；确认后严格构建并重开 Format 3 临时包，创建或复用来源逐字节一致的可见副本，再原子替换原路径。取消、来源 identity 改变、备份或发布失败均不得改变来源；Save Copy 只写 Format 3 且不清除 migration-dirty。
- Midora 自建数据改为 Program-root portable storage：正式配置/预设只进入 `<ProgramRoot>\Data\...`，可重建 session/cache/交换数据只进入 `<ProgramRoot>\.tmp\...`。启动前验证本机固定卷、普通目录、写入、flush、原子替换、独占锁和删除能力；失败时阻止启动且不 fallback。当前版本不探测、不读取、不迁移旧 `%LOCALAPPDATA%\Midora`。
- `.tmp` 的 CompilerRuns 与 AudioWorkerExchange 同 AudioCache/SessionContent 一样使用有版本 owner manifest、直接子目录、独占活动锁与 reparse-point 防护；只 best-effort 回收可证明由 Midora 创建且已不活跃的残留。单实例身份由当前用户与规范化 ProgramRoot 共同决定，使不同 portable 副本互相隔离。

## 2026-08-30 修订摘要

- Event Instrument Definition 新增 `Pre-Roll Ticks`，默认 0、合法范围 `0..Template Length`。仅 Logical Segment 正式实例把 template/instance origin 从 Logical Note anchor `A` 提前到 `A-O`；Logical Gate Start/End、Mapping `gateLength` 和短/长音分类保持原语义。Initial State、模板事件实际 tick、Logical Parameter 状态、Overlap、Usage 生命周期与 Unit 占用使用提前后的 origin；origin 不得越过所属 Segment 有效起点，违反时编译 Error，禁止 Clamp、丢弃前缀、自动扩展或跨 Segment。
- Event Instrument/SubVoice standalone Preview 与 Segment Pitch Ruler audition 不应用 Pre-Roll。中途播放、跳转、循环及局部 Preview 继续使用统一冷启动：不补发范围前 NoteOn，也不执行隐藏音频预滚；播放、MIDI 导出与音频渲染只消费已经统一应用 Pre-Roll 的 canonical 结果。
- 由于 Format 1 已冻结且无法表示 Pre-Roll，当前 writer 提升为 Project Format 2；Event Instrument protobuf v2 wrapper 固定必填 `pre_roll_ticks` field 4。Format 1 reader/schema/descriptor/golden 保持不变；打开 V1 时 detached 迁移并为每个 Definition 显式设置 0，完整验证后一次提交，保存只写 Format 2。

## 2026-08-29 修订摘要

- Follow Playback 的程序级偏好默认值由 Enabled 改为 Disabled。缺失偏好或重置 UI 偏好时使用 Disabled；用户已显式保存的 Enabled/Disabled 选择继续原样读取。跟随播放的运行时交互、Project 数据、canonical 结果及消费者语义不变。
- SMF 导入的已建模文本兼容顺序固定为严格 UTF-8 后严格 Windows-31J/CP932。Windows-31J Track Name 与 Marker 正常转为 Project Unicode 文本并报告 `Info`；两种编码均无法解码的 Track Name 或 Marker 只丢弃该文本事件并报告，不再拒绝其余结构合法的 MIDI。opaque 文本 Meta 继续按原始 payload 保存，SMF 导出仍统一使用严格 UTF-8。

## 2026-08-26 修订摘要

- 澄清 Limiter v2 前瞻 attack 的正式约束：当前 frame 必须取 look-ahead 窗口内每个未来 frame 按距离形成的线性 required-gain 约束之最小值，不得只跟踪原始振幅最大的未来峰值。修正此前会忽略较近中等峰值、在峰值到达时骤降 gain 并产生宽带瞬态的实现错误；最终 playback-span 缓存提升代际，pre-Master/pre-Limiter raw PCM 缓存保持有效。

## 2026-08-25 修订摘要

- Midora 产品版本基线固定为 `1.0.0-dev`，由仓库单一 MSBuild 版本源产生 Assembly/File/Informational Version、Project manifest 和 MIDI Export Readme 版本；删除业务源码中的第二份版本常量。当前 `.midora` Project Format 1 自本修订起成为完整作品验收及 1.0.0 的冻结兼容基线：后续修复必须持续读取既有 Format 1，任何新持久化语义必须进入新格式及显式 detached migration，不得原地改写 v1 schema、protobuf wire 或 page-pack 语义。
- Mapping Function ABI v3 将批准的 `System.Math` 方法及 `E` / `PI` / `Tau` 固定为隐式导入；`Sin(x)` / `PI` 与既有 `Math.Sin(x)` / `Math.PI` 完全等价。无前缀名称仍由同一版本化白名单绑定，不开放真正的 C# `using static`、任意类型解析或额外 API 面。
- 打开的 ComboBox 下拉内容独占鼠标滚轮：无论当前内容是否产生可见滚动条，滚轮都不得传递给外层 Settings、Properties 或 Workspace ScrollViewer。Event Instrument 底部 Keyboard Held Preview 活动时，Global Primary Transport 的鼠标点击必须直接执行 Stop；预处理不得先结束 Preview、再把同一次点击重新解释为主时间线 Play。
- Mapping Function 不再打开独立 Workspace/Tab，也不保留跨窗口的 session Draft。创建与编辑统一使用 Event Instrument 所属的模态表达式对话框：名称与单行表达式只在对话框本地存在，提供同款自动折行增高编辑器、补全/括号高亮、Help、Validate 和结果栏；OK 必须先通过正式 ABI v3 校验，再以一次原子 Project command 提交名称、表达式与自动推导的 Context 依赖，Cancel/关闭直接丢弃本地内容。事件乐器底部 Keyboard Held Preview 在任何模态窗口、菜单或 Tab/Workspace 转场前同步停止，且该清理只识别键盘预览所有权，不得停止主时间线播放。
- Mapping Function 由可执行自由 C# ABI v2 破坏性收缩为受限表达式 ABI v3：只允许版本化白名单内的单行数值/枚举表达式，固定 8,192 scalar、512 syntax node、64 depth 上限，Context 依赖由正式分析器自动推导并复核。正式路径只绑定为 `System.Linq.Expressions` 委托，不 Emit/加载 Project 源码程序集；旧 ABI v1/v2 只可识别并明确拒绝，绝不执行。Batch Edit 表达式不是 Project 内容，不在本次变更范围。
- Pure MIDI Track 的音频投影新增严格限缩的 Channel Mode SysEx 特权：仅识别有效 Roland GS DT1 Part Mode 与 Yamaha XG Part Mode，导入按 payload Channel 归属，canonical 保留来源/顺序并支持 Root 活动区间中途的状态恢复，BASSMIDI Unit 统一重定向到 channel 0；Worker 在原样发送后于同一顺序点显式同步等价 Unit mode，避免依赖 SoundFont 相关的隐式 preset remap。其他 opaque SysEx/Meta 仍只保留、导出而不发送到音频后端；该可听语义变更同步提升 Unit PCM / playback-span renderer cache generation，禁止命中旧版忽略 SysEx 或未显式同步 mode 时生成的 PCM。
- 主应用取得单实例所有权后自动回收上次异常退出遗留的 session 音频缓存和 Pure MIDI session backing content。两类目录均采用版本清单、独占活动锁、直接子目录校验与逐目录 best-effort 删除；活动、未知、清单不匹配和重解析点目录必须保留。旧版裸 GUID `SessionContent` 目录只在名称及内容包结构均严格可识别时兼容回收。
- MIDI 导出的每个实际单 Channel 事件 MTrk 在相对 tick 0 固定写入 CC91 Reverb Send=0、CC93 Chorus Send=0；初始化只属于 SMF 编码结果，位于结构/Channel 10 初始化之后、canonical 事件之前，Conductor 不写，Pure MIDI 用户 CC91/CC93 仍原样保留。
- MIDI 导出 `README.md` 删除程序级 SoundFont 占位信息；`Notes` 改为本次冻结 Canonical Compiled Result 的准确 MIDI Note On 事件总数，并以单行 invariant 十进制字段输出。
- Arrangement Segment 与 Logical/Direct/Template Note 的边界 Resize 在 Snap 开启时以当前有效 Operation Subdivision 作为最小长度，在 Snap 关闭时以 `1 tick` 作为最小长度；批量 Resize 对每个对象独立饱和。打开手势前已经短于当前吸附步长的既有对象不得因约束被反向扩长，其本次最小长度保持原长度。拖动预览与正式提交必须一致。
- 离开或关闭 Event Instrument Workspace 时，自动停止其底部 Preview Keyboard 专有的活动 Held Preview，包括仍按住的 Gate 和已经松开但仍在播放的 release tail；该清理使用显式键盘预览所有权，不得停止主时间线播放或其他 Preview 类型。
- Event Instrument Loop 允许保存只填写 Start 或 End 的不完整编辑 Draft，使两个独立字段可以逐项提交；该状态可持久化和 Undo/Redo，但 Full/Incremental Compile 固定产生 `MIDORA1212` Error，任何正式消费者不得执行部分 Loop。完整 Loop 的范围规则保持不变。
- 以 Limiter v2 破坏性替换旧零前瞻 sample-peak 算法：固定 stereo-linked、5 ms look-ahead、4× 16-tap 插值峰值检测、线性 `0.8912509`（-1 dBFS）ceiling、10 ms hold、100 ms 指数 release 且无 makeup gain。实时在首帧输出前预取分析窗口，离线输出补偿内部前瞻并保持精确 frame 数；算法状态跨工作块连续，旧 playback-span 缓存代际失效。UI 仍只显示 `Limiter`，不展示算法版本号。

## 2026-08-24 修订摘要

- 三类 piano roll 左上角纵向缩放按钮必须在完整 `52 × 24` ruler header 内按 `3 + 22 + 2 + 22 + 3` 水平列和 `3 + 18 + 3` 垂直行直接定位，并根据实际宿主做向上的光学校正；按钮使用 Fluent 原生 `16 × 16` 设计画布，禁止再按 Geometry 实际边界二次拉伸。Global Command Bar 的 Project Settings 与 Application Preferences 都使用居中的 `20 → 16` 固定设计画布，并只在画布内部以中心点放大 10%；Application Preferences 使用与同排一致的 Fluent `wrench_screwdriver` Regular 图形。
- Event Instrument 底部 Preview Keyboard 的纵向力度方向改为向下增大；切换到 `Configurations` 后焦点停留在分区导航而非 Name 输入框。无 Project 欢迎页增加 `Open MIDI as New Project`；Global Command Bar 增加 Project Settings 与 Application Preferences 图标入口，并明确无 Project 与播放期间的禁用状态。右侧 Compile / Export 命令显式区分启用主文本色与禁用淡色；音频 Worker 预热细节只显示在前台 Task overlay，不回显到右下角瞬时消息。三类 piano roll 左上角纵向缩放按钮统一按等距 Grid 居中。
- Arrangement、Logical/MIDI Segment 与 SubVoice Timeline 工具栏改用 Fluent `pen`、`select_object`、`split_vertical`、`eraser` 及 `zoom_in/out` 图标；Tooltip 初次显示延迟统一为 250 ms。Arrangement、全部 piano roll 与全部 Event/Parameter Lane 的 Snap 按钮统一显示 `Enable/Disable Snap (A)`。piano roll 左上角不再显示 `BAR`，改为纵向缩放按钮，默认 Key 高度从 18 降为 15 个 100% DPI device pixels；Arrangement ruler header 也提供纵向缩放按钮。
- Logical/MIDI Segment 与 SubVoice 的 Velocity、Event/Parameter Lane 时间 ruler 不再建立 Time Range；Conductor 固定行在 Arrangement Draw 模式下既不显示 Segment 创建预览，也不接受 Segment 创建手势起点。
- 主窗口按焦点类型控制 Windows Input Method：实际文本编辑控件启用 IME，其他焦点目标禁用 IME，使无修饰键 `D` / `S` / `E` 不被中文输入法截获；该策略不得切换或持久修改用户的系统输入法状态。Arrangement 的 Split 图标使用 `16 × 16` 光学校正并保持按钮外部尺寸不变。
- 主菜单从独立行迁入 36-pixel 自定义标题栏，固定顺序为 `Application mark + Main Menu + Project display name + modified marker`；标题栏不显示额外 `MIDORA` 文本或菜单两侧竖向分割线。29-pixel 一级菜单容器作为整体纵向居中，Hover/Open 背景使用四角圆角。SubVoice 切换到 Event Lane 后焦点必须进入事件编辑 Surface，不得停留在 Lane 下拉框。Arrangement Conductor 行允许单击改变 Edit Cursor，但仍不是 Segment host。
- Global Command Bar 右侧 Compile/MIDI Export/Audio Export 在 Disabled 时使用显式 `#8A939F` 淡色文本；按钮内容自身与专用模板都固定该禁用色，不得被通用 Button Disabled 模板或前景色继承覆盖。标题栏 Project display name 与 Main Menu 保持 8-pixel 外间距，使用 12-pixel 字号；外框不固定高度且不设置 Padding，按居中文本 `8,2` Margin 与 1-pixel 边框自动测量，并分别使用 `Brush.Text.Tertiary` 与 `Brush.Border` 低对比度颜色。
- Application Preferences 按 `Audio | SoundFonts | Appearance` 分页；原 Project Playback Settings 迁移为程序级 Playback Preferences，Audio 页统一承载 Playback、Realtime Audio 与 Audio Cache。Appearance 页初版只保存 `Language = English` 占位值。
- 删除 Project MIDI Export Defaults 与 Audio Render Defaults：`.midora`、Domain、History 与损坏恢复均不再包含这些模型；MIDI Export / Audio Render Dialog 每次以规格固定初始值建立本次任务 Draft，用户修改不持久化为 Project 或程序默认。
- Arrangement 空白行外区域不得成为 Segment 创建或选择手势起点；选中 Track 的 Hover 使用略亮红色。Diagnostics Workspace 使用 Fluent `pulse` 图标。Segment 下部事件/参数视图的 ruler 不建立 Time Range，切换 Parameter Lane 后必须把键盘焦点恢复到编辑 surface。
- Pure MIDI Segment Split 使用独立的 MIDI Segment 命令，按 split tick 分割 Direct Notes、Channel Events 与 Opaque Events，并保持一次原子 Undo/Redo；不得把 MIDI Segment ID 交给 Logical Segment 命令。
- 程序级 SoundFont 列表扩展为本机 SF2/SFZ：每项保存 Enabled、原绝对路径和可选目标 Bank MSB/LSB/Program；SFZ 目标必填，SF2 可保持原映射。Midora 不解析、快照或监控 SFZ sample/include 依赖，直接把原 SFZ 路径交给 BASSMIDI。
- 目标映射使用 BASSMIDI `BASS_MIDI_FONTEX2`，三项必须整体出现且均为 0～127；配置、顺序与主文件元数据共同进入 sample-domain 缓存身份。SF2 继续使用 MMAP，SFZ 不使用只适用于 SF2 的 MMAP。
- New Project、Open Project、命令行打开、Open MIDI as New Project 和 Reset Playback Engine 在存在 Enabled SoundFont 时，必须在对应前台任务结束前接管或预热持久 Worker；不得把首次 Worker 创建推迟到 Play/Preview。Project 已成功切换后的预热失败保留 Project，并作为独立音频运行时错误报告。
- Application Preferences 的 SoundFont、实时音频或音频缓存配置变化在成功持久化后立即显示 `Saving Settings` 模态任务，销毁旧持久 Worker、直接加载 Enabled SF2/SFZ、探测设备并保留新 Worker供后续播放/预览复用；无 Project 时允许由后续 Project 会话接管预热后端。加载失败保持已保存设置并明确报告，不静默回退旧设置。
- 主菜单 `Help` 更名为 `Application`，`Application Preferences...` 从 `Edit` 移入该菜单；SoundFont 列表操作按钮移至列表顶部并采用较小的列表专用滚轮步进。
- SoundFont 破坏性脱离 Project：`.midora` 不再保存 SF2 字节、Embedded/External 引用或任何 SoundFont metadata，New/Open/Save/Save Copy 不访问 SF2。
- Application Preferences 改为有序 `{Enabled, absolute local .sf2/.sfz path, optional target}` 列表；所有实时播放、预览和离线渲染冻结同一 Enabled 顺序与映射，BASS Worker 直接打开原路径，不复制、不执行完整内容 hash 或预验证。
- SoundFont 列表变更只允许在 Stopped/Idle 提交，并重建持久音频 Worker、失效 sample-domain cache。缓存键使用有序路径与 length/last-write-time 小型元数据描述符；该描述符不是内容完整性验证。
- 本次为开发期格式破坏，旧 Project SoundFont 字段、settings 与内嵌资源不迁移、不兼容读取、不双写。受影响章节：2、3、6、8、12～23 及 ADR-CORE-047。

## 2026-08-23 修订摘要

- Arrangement 的 Event Instruments 管理栏为 Definition 项固定显示正式颜色竖线；该列表的选中态只改变背景，不得用通用红色左边框覆盖颜色语义。
- New Project Dialog 将 SoundFont 改为正式创建参数，明确提供 None、Embedded 与 External Relative；External 只在立即保存时开放。Application Preferences 的 Default Embedded SF2 以可见初始选择进入对话框，不再在关闭对话框后静默注入。
- Monitoring generation 的 `Seek` 下界必须持续作用于该 generation 后续追加的记录；Producer 从更早 rewind frame 渐进追赶时，Reader 静默跳过晚到但早于 audible frontier 的记录，禁止把它们重新送入 renderer。render-ahead fault 必须保留 source exception/invalid-result 原因并连同 renderer fault 上报。
- Arrangement Segment 概览的最高精度从错误的“每 Segment 固定 512 pixel”更正为 `96 pixels / quarter note`、256-pixel tile；较低精度只使用固定半八度 `1 / 2^(n/2)` LOD。Segment 长度、TPQN 与固定 LOD 决定 tile 数，精确 viewport zoom 不直接进入缓存身份。
- `Open MIDI as New Project` 的模态任务使用确定进度：第一遍以已解析源字节计量，第二遍显示已处理事件/总事件；成功兼容报告的完整 Info/Warning 文本必须与状态栏摘要共同保留，后续 `View` 仍显示首次报告全文。
- Arrangement Segment 概览在最多两个后台 worker 上为全部 Segment 预热完整且不超过四个 tile 的固定 LOD；缩放选择第一个不会向下采样 source pixel 的固定层，不产生任意 exact-scale cache generation，也不得遍历大量被压成亚像素的最高精度 tile。最终合成必须使用浮点目标宽度换算，并从一次 device-pixel-snapped 的完整 Segment 变换推导全部 tile 边界；禁止整数除法零宽和随 pan 改变最近邻采样相位。
- 当前 viewport 的 Segment 概览先原子发布同内容版本的完整粗略 fallback；该 fallback 比最多四个 tile 的后台预热层提高一倍水平分辨率，且自身最多八个 tile。fallback LOD 只由 Segment 长度与 TPQN 决定，不随 viewport 缩放改变；当前显示层更粗时允许临时缩放既有 fallback，禁止因缩小视图重新进入空白。全部可见 fallback 就绪后才启动新的当前 LOD 细化；目标 tile 就绪后独占其横向范围，禁止继续在该范围下绘制 fallback，但粗缓存仍保留供其余范围和后续视图复用。
- Monitoring generation 切换时，event Reader 对 published generation 与 generation-local loaded counters 的读取必须与 `Seek` 使用同一 feeder lock，禁止把旧 generation 的 committed count 与新 generation 的 loaded count 混合。
- 对象 `Properties...` 的 `Ctrl+P` 入口正式纳入焦点敏感快捷键；快捷键必须直接调用命令核心，不得构造无 `RoutedEvent` 的事件参数再调用 UI 事件处理器。
- 所有带确认提交的自定义模态对话框统一使用同宽度的红色 Primary 确定按钮与普通取消按钮；除焦点控件自身消费 Enter 的情况外，Enter 执行确定、Escape 执行取消，不再由每个对话框各自定义不一致行为。每窗只允许一个 Escape Cancel target；标题栏关闭按钮执行显式取消，但不得再次注册 `IsCancel`。
- Conductor 下部事件列表默认占可用编辑区的一半，并允许在硬性最小/最大高度内双向调整；Arrangement Draw 模式下，只有未形成移动/Resize 的 Segment 单击才在 MouseUp 替换选择，实际拖动不得在 MouseDown 清空多选。

## 2026-08-22 修订摘要

- Arrangement shared block 的 Track 拖放预览必须与最终 drop 语义使用同一目标：exterior strip 始终把插入线固定在 block 的真实外边界，不得短暂显示在成员间隙；同组成员明确脱离时使用更粗的强调边界线。该变更只涉及会话 UI 反馈，不改变 global Track order、Usage/Root membership 或持久化模型。
- 删除主窗口 Global Inspector、Bottom Panel、Details/Tasks Tab 及其 View/菜单入口。Diagnostics 只保留独立 Workspace；一次只显示当前前台任务的模态表面，不保存可见 Task History。
- 所有对象精确属性统一迁移到所属 Workspace 的显式 `Properties...` 模态对话框：打开时冻结目标，所有控件只编辑 Draft，`OK` 以一个原子 Project command 提交、`Cancel` 全量丢弃。多选 Mixed 字段必须先显式启用统一值，并可逐字段恢复原始 Same/Mixed 状态；Logical/Pure MIDI Segment、Logical/Direct MIDI Note/Event 和跨类型 Segment 选择均纳入统一包装。
- Event Instrument 删除 Properties/Parameters Tab；结构栏对象以双击或右键 `Properties...` 打开事务式编辑器。Parameter Mapping 的 Source、SubVoice、Target kind 与适用 CC/RPN/NRPN 在同一对话框创建或编辑，不再拆成 Route 与 Properties 两步。
- UI 不显示 Stable ID 或内部引用编号；Broken/引用选择只用可识别名称、对象类型和显式修复动作。Logical Parameter Lane 固定为离散 Step 点集；Direct MIDI 编辑时 exact point 碰撞采用后来编辑者覆盖，exact Note start/key 碰撞丢弃后来对象，同时保留未经相关编辑的导入重复数据。

## 2026-08-21 修订摘要

- Logical Track 的普通 `Duplicate` 改为深拷贝 Track/Segment/内容并创建引用同一 Event Instrument Definition 的新独立 Usage；显式 `Duplicate and Share State` 才保留源 Usage。普通副本位于源 Shared Usage block 之后，共享副本位于源 Track 之后且留在 block 内；两者均以一次原子 Undo 创建全部新稳定 ID。
- Event Instruments pane 中的 Definition `Duplicate` 继续只深拷贝 Definition 及其内部对象，不复制 Track/Usage；Track/Usage 上下文不再提供 `Duplicate Instrument Only`。Logical Track 菜单增加可直接打开有效 Definition 的 `Edit Event Instrument...`，未绑定 Track 不执行该命令。
- 第 5、7、10、11、12 章中旧的“Logical Track / Event Instrument Binding”共享与 Overlap 边界统一改为 Event Instrument Usage：同一 Usage 可以跨多个 Logical Track 共享状态、Overlap 域和活动连通区间；不同 Usage 即使引用同一 Definition 也保持隔离。
- 任一共享 Fixed Root 成员 Track 的 `MIDI Route Settings...` 均可修改 Root 唯一的 Channel Mode；多成员时必须明确提示影响范围并确认，确认后一次性更新全部成员，不要求用户寻找独立的 `Shared MIDI Route Settings` 入口。
- Arrangement ruler 增加从 Conductor Marker 派生的只读标签投影；Segment horizontal overview 以真实 NoteOn/GateStart tick 和 non-Note event/parameter tick 绘制独立缓存线，不得把持续范围、页摘要跨度或空洞错误填满。
- 本次是 `v0.1` 未发布开发期规格的全面同步，不改变产品/SRS 版本，也不恢复已被第 24 章取代的旧 parent/child Arrangement 或旧开发格式兼容路径。

## 2026-08-20 修订摘要

- Arrangement 从可见 parent/child 树替换为 `Conductor + global mixed Track order`。Event Instrument Definition 独立有序保存；新增无名称 Event Instrument Usage 作为可被多条 Logical Track 共享的执行/状态/生命周期身份。Shared Usage 与 Auto Root 以连续大括号 block 表现，Fixed Root 作为 Track route 属性表现且 members 可分散。
- 所有 Usage/Root 必须非空；最后成员 Track 离开时 owner 在同一个 Undo 中自动删除。Event Instrument Definition 可以零 Usage，删除 Track 永不删除 Definition。Fixed Root 不再提供空 Root 创建/预留入口。
- Pure MIDI SMF 投影和同 Root 同 tick 顺序改用 global Arrangement Track order；普通 SMF 导入保持源 MTrk 顺序。Logical shared Usage 在未启用逐音符隔离时按跨 Track Segment 活动连通区间共享 Channel state 与 Unit，成员 Segment End 不做 Usage 级 reset。
- Application Preferences 增加可清空的 Default Embedded SoundFont 本机路径，只用于 `New Project` 与 `Open MIDI as New Project`。有效文件按 Embedded snapshot 流程复制、哈希和验证；路径不进入 `.midora`，启动时缺失自动清空，任务开始时缺失按未设置处理。
- 播放期间 `Project` 一级菜单和 `Project Settings` 保持可用，只禁用受编辑锁约束的 `New Event Instrument` / `New Logical Track` / `New Logical Track with Instrument...` / `New Raw MIDI Track...` 等对象创建命令；Status Bar 的 `Playing` 使用绿色文本。
- Arrangement 与共享 piano roll 的可见 Grid 固定为 Bar/分母拍子线，Ruler 显示一基小节号；Segment local tick 通过 Project offset 对齐完整 Time Signature Map，极端水平缩小时按 device-pixel 密度上限跳过不可辨识竖线。
- 2026-08-19 的 parent-row 遮罩与折叠 UI 已被本次平铺 Arrangement 取代；Track 行直接承载内容，Shared Usage / Auto Root 只以 header gutter 大括号和显式 drop target 表现，不再占空白时间线行。
- piano roll 纵向缩放固定为不小于 3 的整数 device pixels/key，Note 顶边与 Key 上分割线重合且总高度等于 Key 高度。Pure MIDI Segment 的 Overview 通过 page summary 聚合音符密度，Add Lane 使用分步目标选择器并支持全部 CC 0..127 的统一名称格式。

## 2026-08-19 修订摘要

- Exact Root/Unit PCM 命中现在必须在 source range query 前建立 demand schedule；完整命中范围跳过对应 MIDI page 查询、排序和 IPC，混合命中仅生产 miss owner。Monitoring 使 cache bypass 后，rolling event stream 必须从实际可听 frame 发布新的 append-only generation，Worker 显式 Seek 后才切换到该 suffix；旧 generation 在切换完成前仍保持可读，并为本次 playback generation 单调保持 synthesis。
- Pure MIDI content pack 增加局部有序的 NoteOn、NoteOff 与 Channel endpoint pages，以及中途起播使用的 Channel-state checkpoint/active-note 查询；窗口消费使用有界 k-way merge，不再反复扫描巨型 Segment 的历史 Note pages。本次为开发期破坏性 pack version 替换。
- Reusable PCM miss 改为把16,384-frame blocks直接顺序追加到generation journal；完整 generation 原子提交，未完成 journal 保持不可命中并由重整回收，删除完整 sparse spool 到 Pack 的二次 payload 复制。
- 播放 UI 每个timer tick冻结一次CurrentTick，Tempo使用有序数组与递增/二分索引且只在实际变化时通知；播放指针移入独立轻量overlay，不再因30 Hz指针更新使Timeline内容层完整重绘。
- SoundFont 策略保持持久 `BASS_MIDI_FONT_MMAP` 与只预载计划引用 Preset；不把完整 SF2 复制到私有内存，也不默认解码全部 `.mpk` 到 RAM。
- Pure MIDI 极端规模基线改为 out-of-core page pack：Direct Note/Event/opaque source、canonical execution/SMF projection 与音频 sample-domain event plan 均不得要求整 Project 连续数组或按对象逐项常驻；页同时受 record count 与 decoded byte count 双重限制，页缓存必须有界。
- `Open MIDI as New Project` 改为两遍流式读取与事务性 page-pack 构建，不得先把完整 `.mid`、全部 parsed events、全部配对集合和完整 imported Project 的第二份深拷贝同时驻留内存。开发期 `.midora` Pure MIDI Track 格式改为小型 protobuf 元数据加单 Track page pack，不提供旧开发格式兼容读取。
- Canonical 保持一个正式事件集和两个冻结投影，但物理表示允许共享 immutable pages、descriptor/index 与去重来源表；Full/Incremental 等价比较的是逻辑事件序列、投影、诊断与 fingerprint，不要求连续数组或对象布局相同。
- 实时播放的 rolling preparation 前移到 canonical→sample 事件计划和 IPC：启动只发布当前光标恢复状态与 Startup 2 s 窗口，随后按 Low 0.75 s / Target High 6 s 水位请求页面；远处 Segment/事件数量不得决定启动等待。IPC 以有界 page generation 传输，删除整项目 MDAP event-count/file-size 作为正常项目容量上限的做法。
- Pure MIDI Arrangement preview、Piano Roll、Velocity 与 Event Lane 必须通过 source page range query/LOD 聚合读取可见范围；不得为打开视图建立全 Segment `TimelineRenderItem[]`、全 ID dictionary 或全量 interval tree。编辑使用 immutable base pages + copy-on-write overlay，并只失效相交页/tile。
- rolling event reader 在单个committed窗口超过固定ring时必须公布排他的partial safe frontier，使renderer可推进至最后已装载record frame并分批排空同frame事件；不得因reader等待ring空间、renderer等待完整窗口而形成永久Buffering。
- 从Arrangement显式打开Segment时，若Edit Cursor位于该Segment的Project范围内，Logical/Pure MIDI Segment Editor统一映射到local tick并将该位置水平居中；仅切换已有Tab时仍保留原viewport。
- `Open MIDI as New Project` 在 detached candidate 验证前增加确定性导入兼容归一化：缺失 tick 0 Tempo / Time Signature 时分别补齐 120 BPM / 4/4，同 tick 重复 Tempo 按源 MTrk 与事件顺序使用后来者。
- SMF Track Name 缺失、trim 后为空或非严格 UTF-8 不再使整个导入失败；非法名称事件被丢弃，需要的 Pure MIDI Track 获得确定性回退名称。所有兼容处理只进入一次性、可复制的导入报告，不放松 Project 内部不变量或导出的严格 UTF-8 要求。

## 2026-08-18 修订摘要（其中 Arrangement 树、parent subtree Duplicate 与 `Duplicate Instrument Only` 已由 2026-08-20～21 修订取代）

- Arrangement 改为 `Conductor → 混排的 Event Instrument / MIDI Channel Root → 各自 child Track` 两级正式结构；删除 Project Panel、可见 Event Instrument Library Workspace、Library Folder、独立全局 Track/Root 顺序和 Unbound Logical Track。Event Instrument / Root 的混排顺序及 parent/child 关系进入 Project、Undo/Redo 和严格持久化索引。
- Event Instrument / Root 支持携带完整 subtree 的复制、剪切、粘贴与 Duplicate；Root 副本强制改为 Auto。Event Instrument 另提供 `Duplicate Instrument Only`；删除 non-empty parent 必须确认并原子级联 child。Logical Track 跨 Event Instrument 继续执行 rebind 影响审查，Pure MIDI Track 可跨 Root 移动。
- 父节点与 child Track 各自拥有独立运行期 Mute/Solo；父 Solo 激活时忽略 child Solo，父/child Mute 始终生效。Logical Note 与 Direct MIDI Note 允许只按共同字段跨类型复制；Direct NoteOff Velocity 在 Direct 数据链和 SMF 中继续保留。
- Pure MIDI Segment 概览增加独立缓存的 non-Note event 线层：event 线位于 Note 图形上层、统一 50% 透明度、至少 1 device pixel，并按值归一化高度。Conductor 第一行直接显示按类型着色、固定设备尺寸的圆点概览；两者都采用可视分块缓存、空间索引与局部失效，不以 WPF Control 堆对象。
- 增加 `MIDI Channel Root → Pure MIDI Track → Midi Segment` 正式模型。一个 Root 固定表达一个共享 Channel Unit、Channel-wide 状态、Melodic/Percussion 模式和 Root 活动连通区间；子 Segment End 只关闭自身 Note，Root 连通区间结束才执行 CC120 与最终 Reset。
- 增加 Root `Auto` / `Fixed(Port, Channel)` 路由。Fixed Root 先预留、非空 Auto Root 后按显式 Root 顺序低号分配、Logical/Event Instrument 分配必须绕开全部 Root Unit；Root Units 与 Logical 峰值合计仍受 256 Unit 上限约束。
- Pure MIDI Track 使用直接 MIDI Note 与完整 Channel Voice Event；允许 CC91 / CC93、Channel Mode、Poly Pressure 与 Channel Pressure。Event Instrument SubVoice 的创建/Mapping 面保持受限；BASSMIDI 继续启用 `NOFX`，因此 CC91 / CC93 保留到 canonical/SMF，但不产生 Midora Reverb/Chorus 听感。
- 增加 SMF Format 0 / 1、TPQN division 的 `Open MIDI as New Project`。导入支持 Running Status、单 MTrk 多 Channel、MIDI Port 中途变化和 opaque SysEx/Meta 保留；按 effective Port.Channel 拆为 Root/Track，不提供导入当前 Project、Format 2 或 SMPTE division。
- SMF Type 1 导出改为同时保存 Pure MIDI Track 拓扑与 Logical Unit 拓扑：每个 Pure MIDI Track 独立单 Channel MTrk 并保留名称、顺序和自身 EOT；Logical 内容继续一 Unit 一 MTrk。Root 精确往返使用可忽略的版本化 Midora Sequencer-Specific Meta，跨 MTrk 同 tick 顺序风险在导出阶段汇总 Warning。
- Canonical Compiled Result 增加 Unit execution projection 与 SMF Track projection；音频与缓存按 Root 合并，禁止同 Root 子 Track 分别合成后求和。`.midora` 新增 Root/Track protobuf 对象文件，作为开发期破坏性格式修订，不提供旧布局迁移或兼容读取。

## 2026-08-16 修订摘要

- SubVoice 的 Note Number / Velocity Mapping 保持强制共享目标；非 Note Event Mapping 与 Logical Parameter Mapping 改为可物理删除的可选 owner。非 Note 事件点继续存在时，缺少 Mapping 表示原始值直通，普通事件编辑不得静默重建已删除 Mapping。
- 非 Note 状态目标上的 Envelope Mapping 以最近原始事件值或有效 Initial State/default 为持有基值，并按实例/Release 的整数 tick 连续求值；非零 Release 的最后一个有效 tick 达到 End Value 后才进入 NoteOff/Reset。
- Loop Start 前开始并完整跨越 Loop End 的模板 Note 在循环中保持发声且不重触发；普通 Gate/Release/Tail 结束不发送 CC120，CC120 只用于 Segment End 等明确硬边界。
- 修正普通 instance 结束后的 SoundFont release：发声 Segment 的 Unit lane 从首次使用持续保留到 Segment End；普通 NoteOff 后的原生 release 继续进入 Segment PCM，只有 Segment End/消费者范围硬边界可以硬裁剪。自然编译范围按实际生成实例所属 Segment End 结束。
- 将普通 instance 的通用状态清理从生命周期结束移动到 lane 启用/非重叠复用起点：先按实际目标闭包建立 Reset Defaults，再应用 Initial State、tick 0 用户状态和 NoteOn；普通结束只执行精确 NoteOff，状态保持到下一次 lane 激活或 Segment/消费者硬边界。共享 lane 内仍重叠的后续 Gate 不重复重置。
- MIDI Track 0 的 Track Name 改为任务准备时冻结的 Project Name，空白防御性输入回退为 `Conductor`；`Conductor` 仍是 Track 0 的结构角色名称。

## 2026-08-15 修订摘要

- MIDI 导出事件 Track 的组织从 `Logical Track × Port` 改为严格的 `Channel Unit (Port + Channel) × 1 MIDI Track`。同一文件内一个实际有事件的 Unit 只出现一次，每个事件 Track 只含一个 Channel；按原始 Port→Channel 排序，Track Name 固定显示一基 `Port <P> / Channel <C>`。该变化用于兼容不支持单 Track 多 Channel 的 MIDI 编辑器，不改变 canonical 路由或事件语义。

## 2026-08-12 修订摘要

- Arrangement 新建 Project / 重置编辑器的默认可见 Grid 改为 `Bar`、Snap 操作粒度改为 `1/8`；Bar Grid 按完整 Time Signature Map 以主实线绘制小节边界、以更浅的低强调实线绘制分母拍内部边界。Segment/SubVoice 钢琴卷帘不采用该拍内辅助线增强。
- Arrangement Draw 空白放置改为按下并向右拖动确定 Segment 长度，单击使用默认长度；Arrangement 默认 Segment 长度固定为 `1 × TPQ`，相邻 Segment 仍按可用间隙缩短或拒绝。
- Arrangement Track Header 曾增加独立 hover / pressed、拖动重排与 Bind / Unbind；其中交互反馈继续有效，层级、绑定与拖放语义先由 2026-08-18 两级模型取代，并最终由 2026-08-20 的 global flat Track order + Usage/Root shared block 模型再次取代。
- 普通 Logical / Template Note 多选移动使用共同 pitch delta，并删除结果 pitch 越出 `0..127` 的个别 Note；移动与删除属于一个 Undo。Note `Ctrl+Drag` 复制仍使用整组共同 clamp，不生成部分副本。
- 正式 Compiler Diagnostic message 统一为英文；Error / Warning 计数在每次编译完成时同步刷新（其显示入口已于 2026-08-18 从删除的 Project Panel 收敛到 Status Bar）。非法 pitch 来源的诊断导航使用安全 lane 投影，不得使应用崩溃。
- Velocity 视图改为每个 Note 在 start tick 对应一根固定窄柱，柱宽不再表达 Note 长度；顶部使用较大的方形 onset marker，同 tick 多音按高 pitch 覆盖低 pitch。
- Velocity 自由绘制与直线插值手势在按住期间只显示轻量轨迹，不逐柱重绘或提交；松开时一次性计算、提交并刷新 tile。直接按住单柱或其 marker 上下拖动仍只调整该 Note，并且不显示轨迹。
- `Alt + Left Drag` 统一为强制替代手势：Draw 模式的 Segment / Logical Note / Template Note 无视边界命中并强制 Move，`Ctrl + Alt` 强制 Copy+Move；Velocity 无视柱体 direct hit 并强制自由轨迹。操作类型在 Pointer Down 时冻结，Alt 不再绕过 Snap；已消费的 Alt KeyUp 不再激活主菜单并恢复来源 Timeline 焦点，普通 Alt 与 `Alt+F4` 不变。Draw 模式悬停可直接编辑对象时始终显示低强调 transient 外轮廓，不失效 raster tile。Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的 Select 模式仍从单次左键按下点发起框选，不再以单独左键点击命中对象。

## 2026-08-10 修订摘要

- 明确 Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的 Draw / Select / Split / Erase 互斥工具状态、直接编辑边界、对象命中指针以及移动/Resize transient 预览；Select 不再直接移动、Resize 或双击创建 Segment / Note。
- 将无修饰键 `D` / `S` / `E` 固定为活动 Timeline Workspace 的 Draw / Select / Erase 快捷键，并明确文本、代码、ComboBox、菜单、Popup、内联编辑和 Modal 的焦点例外；其余单字母工具快捷键仍不注册。
- 补充 Grid / Snap 显示同步、ComboBox 可编辑文本与 Fluent 下拉图标居中、显式垂直 ScrollBar、Diagnostics 筛选框和 Segment 标题布局验收；固定 Arrangement / Segment Piano Roll / Velocity 的蓝灰色层级与 Velocity onset marker、Segment/SubVoice 真实黑白 Pitch Ruler 与逐八度 C 标签、空 Timeline 无覆盖卡片，以及可复制的 Status Error 详情入口。
- 明确 Arrangement Segment Note Preview 使用固定 MIDI pitch `0..127`、最小 1 px Note 高度、布局取整和按 Segment 稳定 ID/内容指纹复用的手工渲染缓存；tick 0 Note 不得遗漏。
- 明确 Velocity 普通点击、自由拖动、右键直线插值、选择集过滤和单柱顶部边缘调整语义；所有手势在按下时立即生效并保持一次手势一次 Undo。
- 明确 Piano Roll 与数值 Lane 的纵向视口边界、右侧滚动条、随平移更新的标尺和边界标签可见性；显式滚动条 Thumb 按实际可见范围计算。Segment 下部编辑区的显隐与高度属于 Project Session UI State，其分隔条在完整上部 Timeline 区域与下部编辑区之间调整高度。
- 明确 Draw 模式下 Arrangement Segment、Segment Logical Note 与 SubVoice Template Note 的主体 `Ctrl+Drag` 使用原子“复制并拖拽”语义；复制集使用共同 delta、生成新稳定 ID、成功后只选择副本并只形成一个 Undo，普通 `Ctrl+Click` 与边缘 Resize 语义不变。
- 补充 Timeline 工具栏、状态栏已读、选择边框、键位明暗、Disabled Ghost Button 和顶部 Transport 信息的 WPF 验收要求。

## 2026-08-08 修订摘要

- 解决整数 Project tick 与 `Bar:Beat:Tick` 在部分 TPQ/拍号组合下不可逆的冲突：开发期 v1 中每个 Time Signature 必须满足 `4 × TPQ % denominator == 0`。Domain 创建/编辑、语义验证和持久化统一拒绝不兼容组合，不创建 v2 或迁移器。
- Time Signature 变化 tick 立即成为新小节 Beat 1；若该 tick 不是旧拍号下的自然小节边界，旧小节被截断并产生 Warning。统一的 Project Time Signature Map 负责可逆 `Bar:Beat:Tick`、自然小节/拍网格和 Snap 基础。
- Q-NUI-034～042 固定五层缓存：canonical range、Segment/Unit fragment、Unit raw PCM、playback span、短 Render-Ahead ring。相同完整 key 的 exact replay 命中不得重复语义编译或 BASSMIDI 合成；缓存只在 Project session 内有效，不进入 `.midora`。
- underrun 在失败位置锁存，按自然小节准备连续恢复区间；小节中途为“当前剩余 + 下一完整小节”，小节起点为当前完整小节，并以播放终点和 16 个四分音符裁剪。完整区间准备好之前不得短块断续推进。
- 音频在 canonical 成功后按抽象 Unit 使用 1-channel BASSMIDI Stream 语义和有界 stream pool。Realtime/Offline Maximum Sample Voices per Unit Stream 分别保存，默认均由 750 改为 500。
- Application Preferences 增加 session 音频缓存 root（默认 `%LOCALAPPDATA%\Midora\AudioCache`）和 reusable byte quota（默认 16 GiB，可为 0）。Transient recovery spool 独立于 quota；spool/RAM 均不可用时受控 Stop。

## 2026-08-07 修订摘要

- 明确 Segment Editor 钢琴卷帘的两种初版交互预览：左侧 Pitch Ruler 琴键按住预览，以及放置单个 Logical Note 时的草稿音符预览。两者统一复用 held Preview 的因果 Gate、`Int64.MaxValue` 未结束哨兵和未渲染 frontier 生效规则；预览不写入 Project，预览不可用或失败不得阻止合法音符编辑提交。
- Project 内稳定 ID 的核心与分配器统一改为单个正 `long`，合法范围为 `1..long.MaxValue`；JSON 使用 canonical 十进制 integer，对象文件名使用无符号、无前导零的十进制 ASCII，protobuf 在各既有外层字段号上直接使用标量 `int64`。这是尚未冻结的开发期 v1 直接修订，不提供旧 128-bit 布局迁移器。
- C# Mapping 内部 ABI 升级为 v2：签名改为 `double Transform(double value, in MappingContextV2 context)`，`MappingStableIdV2` 只承载单个正 `long`；语言、引用面、确定性编译、collectible AssemblyLoadContext 和非 sandbox 边界保持不变。

## 2026-08-06 修订摘要

- Midora 初版产品定位固定为免费、开源、非商业软件，Midora 自有源代码采用根目录 `LICENSE` 中未经自定义修改的标准 MIT License，版权署名为 `Copyright (c) 2026 Midora contributors`；项目自身非商业不限制下游商业使用。该定位不把 BASS/BASSMIDI/BASSWASAPI 纳入 Midora 的开源许可证；正式发布仍须按实际主体、收入方式、平台、分发方式和届时有效条款执行许可核验并提供第三方声明。
- MIDI 导出的 Channel 10 melodic 初始化固定为：每个 Logical Channel 10 Unit MTrk 与 Melodic Channel 10 Pure MIDI MTrk 在相对 tick 0 按 GS→XG 写入两条 Normal Part SysEx；Percussion Root MTrk 不写。初始化使用固定默认设备编号，不发送任何 GS/XG/GM Reset，也不改写 canonical Bank/Program；Readme 必须说明不识别 vendor SysEx 的兼容边界。
- MIDI 导出与音频文件渲染统一使用确定性 Windows 安全文件名合法化和冲突检测；精确算法固定为 NFC、固定不安全字符集合、设备保留名前缀 `_`、255 UTF-16 code unit、text-element 安全截断、NFC + OrdinalIgnoreCase 冲突键和稳定 ` (n)` 后缀。任务开始前必须预览并冻结全部最终路径，源名称不被修改，已有目标不参与后缀分配且仍需明确覆盖授权。整曲、分 Track、逐 Port、Readme 与 MIDI Track Name 模板已经固定，多文件模式不自动增加嵌套目录。
- MIDI 导出兼容档固定为 SMF Type 1、显式 status（不使用 Running Status）、严格 UTF-8 文本 Meta、Track Name + MIDI Port 最小组合、CC0→CC32→Program、Time Signature `cc=24` / `bb=8`、Tempo 十进制换算后一次 `AwayFromZero`，并禁止导出器在 canonical 之外追加 Channel 清理；Conductor/Logical Unit 使用统一 endTick，Pure MIDI Track 保留自身 EOT。SMF 导入单独支持合法 Running Status。
- 工程总耗时固定按 Project 成功打开后的完整会话时间累计，包含空闲、最小化、失焦、Buffering、导出和渲染；系统睡眠 / 休眠及关闭流程暂停。当前会话使用单调时钟，自动累计不单独标记 Project Modified，也不影响编译语义。
- Project 外部 SoundFont 固定为项目根目录或直属 `soundfonts/` 的相对 SF2；路径精确大小写优先、唯一 ignore-case 回退并警告、歧义拒绝。原始字节 SHA-256 只在用户明确绑定/接受时更新，被动变化不修改 Project；内嵌资源的 settings/manifest/hash 必须一致。
- 初版持久化兼容基线固定为 JSON Schema Draft 2020-12、内部版本化 `System.Text.Json` source-generated DTO、protobuf Edition 2024、Google.Protobuf 3.35.1 与 Grpc.Tools 2.83.0；严格拒绝重复/未知 JSON 属性和未知 protobuf tag，已发布 schema 以 descriptor/golden bytes 锁定。
- 持久化文本、相对路径、opaque sRGB 颜色、UTC 时间和工程总耗时的 v1 表示已固定；路径保留大小写与原 Unicode，不做 normalization，时间使用七位小数秒 UTC `Z` 格式，总耗时使用非负 int64 毫秒。
- 初版 C# Mapping 最初采用 ABI v1；该内部 ABI 已于 2026-08-07 因稳定 ID 改为单 `long` 而升级为 ABI v2，当前有效规则见同日修订摘要。版本化函数体、只读独立 MappingContext 契约、C# 14/`Microsoft.NETCore.App.Ref 10.0.10`，以及按 Project 当前源码修订管理的 collectible AssemblyLoadContext 缓存等边界保持不变。
- 初版产品发布架构固定为 `win-x64`；主应用、Native AOT 音频子进程和三项 BASS 原生库必须同为 x64，不发布 x86、Arm64 或 AnyCPU 正式产物。
- 初版正式原生基线固定为 BASS 2.4.18.3、BASSMIDI 2.4.16.0、BASSWASAPI 2.4.4.1 及三项 win-x64 DLL 的明确 SHA-256；仓库只保存 manifest，正式构建由操作员提供并校验二进制，vendor current/latest 只能生成开发候选。

## 2026-08-05 修订摘要

- 初版正式实时音频工作 block 固定为最多 `256 frames`；音频子进程内 Render-Ahead 使用单个有界 SPSC PCM ring，容量按 `ceil(actualSampleRate × RenderAheadMilliseconds / 1000)` 计算，不固定 ring block 数；实时 PCM 不跨进程。
- 初版 WASAPI 输出固定为 Shared Mode、event-driven、stereo interleaved float32；采样率采用端点初始化后的实际混音采样率，buffer 请求不等于实际值，period 与 callback frame 数由设备决定。
- 当时曾将 Limiter 版本 1 固定为 stereo-linked sample-peak 算法；该历史决定已由 2026-08-25 的 Limiter v2 修订破坏性取代，不再是当前正式语义。
- tick→sample frame 固定为完整 Tempo Map 的 decimal 区间积分乘采样率后只执行一次 `AwayFromZero`；实时播放、预览与音频渲染共用该语义，不得逐 Tempo 段取整。
- 连续值源离散化固定以每个整数 tick 的最终目标值为参考语义；canonical 输出首次有效值及后续整数变化值，任何跳跃求值或缓存优化都必须与逐 tick 参考结果完全一致。
- 整数目标参数默认使用 `Round / Away From Zero`，只在完整映射链最终输出时取整一次；取整与最终越界策略属于目标参数，不属于 Mapping Step。
- 新建 Event Instrument 的 Overlap 策略固定默认为 `Reject`；`Reject` 重叠产生 Error，`Warn` 重叠产生 Warning，并服从全局“强制 Warning 导致编译失败”策略而不改变诊断级别。
- 稳定 ID 文件布局最初采用 32 位小写十六进制文本和 protobuf high/low；该开发期布局已于 2026-08-07 在首版冻结前由单 `long` 契约直接取代，不构成已发布兼容承诺。
- 同一 tick 允许多个普通 Marker；它们不按名称或 tick 去重，以稳定 ID 区分，并在 canonical 结果中按稳定 ID 确定同 tick 顺序。
- 初版 Envelope Preset 统一为第 10.11 节规定的固定 ADSR-like 结构；第 18.6.5 节编辑器不得扩展为任意有序点或曲线段模型。
- `Channel Unit >= 248` 的诊断级别统一为 `Info`，不受“Warning 视为 Error”策略影响。
- Segment Split 必须为右侧 Segment 保留或生成必要参数起点状态，维持参数状态及相关曲线在分割前后的听感；不改变跨分割点 Logical Note 的提前结束规则。
- 正式 BASSMIDI 后端启用 `BASS_MIDI_NOFX`，初版不承诺 Reverb / Chorus 音频效果。Event Instrument/SubVoice 不允许创建或映射 CC91 / CC93；Pure MIDI Track 允许保留、编译和导出它们，音频投影确定性忽略其效果且不报错。
- 正式 BASSMIDI 后端启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠 Note 实例按 FIFO 逐个释放，Cut Previous 的释放重叠与硬边界 Reset 必须保持精确 NoteOff 配对。
- 正式 BASSMIDI Stream 固定使用 8-point sinc 和 CPU 属性 `0`，并在 Preparing 预加载计划引用的 SF2 presets；Realtime/Offline Maximum Sample Voices per Unit Stream 分别配置，默认均为 `500`，同一任务所有 Unit Stream 使用同一冻结值。
- 实时播放跟随所选输出设备的实际采样率；音频文件渲染使用用户选择的 `8,000–192,000 Hz` 整数采样率。
- 音频文件输出改为普通 RIFF/WAVE、Stereo、Interleaved IEEE 32-bit Float；超过 RIFF 大小上限时在 Preparing 阶段失败。
- 增加启用输出设备枚举、可调 buffer、约 200 ms 性能基准、音频活动线程零托管分配和固定内部音频子进程要求。
- 性能选择在满足正确性、确定性和资源上限的前提下优先时间性能，可用受控内存换取速度。

## 文件命名规则

文件名前两位数字是稳定阅读顺序。章节内标题采用 `章.节.小节` 编号，可用于 Issue、ADR、测试和代码评审引用。
