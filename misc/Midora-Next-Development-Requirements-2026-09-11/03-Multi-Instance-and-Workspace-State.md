# 多实例、跨项目剪贴板与工作区恢复

覆盖R01、R06、R07（均P2）以及文件打开R04（P3）。这两个大型专题应分别分阶段，不与几处UI小修合并成不可验收的大提交。本文区分 Q1/Q2 已决定的产品方向和仍待验证的实现设计；产品实施及 SRS 同步尚未开始。

共同入口：[需求总表](00-Overview-and-Delivery-Plan.md)、[决策与问答主文档](04-Decisions-and-Preparation.md)。源码行号基于 `0bb9670`。

2026-09-14 Q1/Q2 答复归并：用户已经逐项填写 Q1/Q2，并明确“同意”表示同意对应推荐；原回答保留在 `04`，本文不改写、不另建第二套回答。Q2 的用户修正优先于此前推荐，当前工具按 D-UI01.d 的最终共享 Draw 方案解释。相关产品边界已定，不再以旧 Q2 待答项阻塞实施规划；方向获批不等于协议/schema 已冻结、SRS 已更新、代码已实施或测试已通过。原答与前轮归并已提交推送至 `7227090`；本轮仅归并文档，不提交／推送，不运行发布。

## 1. 多实例：现状不是“只有一个 Mutex”

| 来源（仓库相对路径） | 已确认事实 |
|---|---|
| `src/midora-desktop/Midora.Desktop/App.xaml.cs:23` | 启动能力检查后StartOrForwardAsync；次实例转发请求后退出，主实例回收残留并创建窗口 |
| `src/midora-common/Midora.Common/MidoraProgramDataPaths.cs:30` | scope含用户与规范化ProgramRoot |
| `src/midora-core/Midora.Application/SingleApplicationInstanceCoordinator.cs:92/319/329` | 有版本协议、1MiB参数payload、256参数、64pending请求、5秒超时；CurrentUserOnly pipe及WindowsSession区分 |
| `ProjectPersistenceCoordinator.cs:179` | 现有工作路径普通Save自动允许覆盖，不是跨进程冲突协调 |
| `ApplicationPreferencesStore.cs:206` / `RecentProjectsService.cs:112` | 临时文件+原子Replace；没有等价的跨进程合并/CAS合同 |

SRS §3.3/3.18.1/21.2、INV-019限制单实例；§16.17.2明确普通源文件打开后不长期加锁、不监控外部改动，下一次Save按内存覆盖。§16.34和INV-086/094还使启动回收依赖单实例scope。

因此移除启动互斥就会引入同文件覆盖、共享偏好丢更新、一个实例回收另一个工作目录等风险。原子Replace只能防半文件，不能防两个用户会话互相覆盖。

### 1.1 Q1 已决定的多实例基线（D-MI01/02）

- 每个进程仍单Project；不顺带实现一个进程多个音乐Project Workspace。
- 每实例独立canonical、Selection/Undo、音频Worker、进程树与会话临时目录；不共享native handle。
- 同一Project文件只允许一个可写Midora会话，第二次打开定位已有实例；独立编辑应先 Save Copy，再打开副本，不新增第二窗口只读或传统 Save As。须覆盖路径别名、文件identity、首次Save、原路径Save、Save Copy和旧格式升级。独立ownership lease / 协调器不一定长期锁原.midora；只有长期锁源文件的方案才改变现行“不长锁”条款。普通Save检测外部identity变化；冲突时可取消、Save Copy，或明确确认后先备份磁盘当前版本再覆盖，不能把现有保存描述为已防冲突。
- 程序级Preferences/Catalog/Presets用短写锁+版本比较或明确的冲突处理，不以最后写入者静默获胜。Recent可在锁内按既定去重顺序合并；配置与用户draft不要无提示合并数值冲突。
- 一实例更新音频配置时，其他实例正在运行的任务仍用已冻结配置；对方Stopped/Idle后接收并按需重建Worker，不中途拆活动Worker。已打开的设置draft不静默覆盖，保存前提示冲突。
- 同ProgramRoot的reusable cache共用总quota，不能按实例数乘以16GiB。临时文件、Clipboard和后台任务也须有预算与释放机制；具体数值由实验冻结。清理只针对可验证owned目录且满足对应用途的回收条件；进程PID不是唯一存活 / 身份凭据，必须防PID复用和清理竞态。持久Clipboard不沿用“进程已退出即回收”的会话目录规则。
- 仍只在ProgramRoot授权的Data/.tmp树内存储，不fallback LocalAppData/TEMP；新增传输目录应作为明确路径白名单变更。

D-MI01.a～d / D-MI02.a～c 已决定：无参exe开新空窗口，并提供 New Window；窗口内New/Open仍替换本窗口；多实例可同时播放/试听，互不停止。同ProgramRoot共享Preferences、SoundFonts、Catalog、Presets和Recent；配置竞争须明确处理，Recent合并。D-MI02.d 的用户修正限定音乐Clipboard只在同一ProgramRoot、同一Windows用户的多实例之间通过专用目录文件共享；不同ProgramRoot的配置、缓存和音乐Clipboard完全隔离，不提供跨目录、跨exe、跨用户或远程传输。

目录隔离不等于允许两个安装副本覆盖同一Project文件，文件写入所有权仍须处理同文件与别名。并发发声时各实例Limiter不等于Windows混音后的全局峰值保证；内存/CPU按实例累加。这些是已决定目标，不是现有已实现行为。

## 2. 跨项目 / 跨实例剪贴板

### 2.1 已有能力和不足

| 来源 | 现状 |
|---|---|
| `Midora.Application/ProjectObjectClipboard.cs:31/270` | payload持有进程内SourceSessionIdentity，Paste使用ReferenceEquals限定同会话 |
| `Midora.Desktop/MainWindow.xaml.cs:10226` | OS Clipboard只写PlainTextSummary，正式数据留私有_projectClipboard |
| 同文件`:10049/196` | 项目替换和窗口关闭Dispose正式payload |
| `Midora.Application/ProjectClipboardStorage.cs:7` | 已有分页snapshot引用计数ownership，最后lease释放才回收 |
| `ProjectClipboardTransfer.cs:44` | Copy/Cut有可取消detached准备、revision冻结与发布复核 |
| `DetachedPagedEditTransaction.cs:10` | 默认4096记录页、每256记录检查取消；working/resident各64MiB、spill16GiB、candidate100,000,000 |
| `BoundedEditStorage.cs:137` | 现有spill是process-local scratch，不是跨版本可交换协议 |
| `ProjectObjectClipboard.cs:81` / `ProjectObjectClipboardPureMidi.cs:7` | Logical Track snapshot引用源Definition；Pure Track有route snapshot，跨Project需要明确闭包 / 新ID |

允许另一进程看到内存对象、复制source session token、或把完整百万对象JSON塞进OS Clipboard都不是合适方案。应复用有界分页事务理念，但为传输设计明确版本、租约和安全边界。

### 2.2 文件型共享边界与候选架构

**已决定：** 音乐payload通过同ProgramRoot专用文件目录共享，不将完整内容塞入内存或Windows Clipboard，不依赖源进程持续存活。允许有明确上限的分块I/O缓冲，不把“文件型”误解为禁止必要的有限内存。新存储位置必须对用户可见、可说明，不写程序目录之外，也不借用音频缓存或Project session backing的生命周期。

以下是满足已决定方向的工程流程；持久槽、目录及清理政策已由 D-MI06.a 确认，具体协议与并发实现仍须验证：

```text
冻结源Selection / revision
→ 有界准备不可变传输快照（独立于源可变Project）
→ 在专用目录原子发布完整文件快照
→ 同ProgramRoot、同用户接收方验证协议 / 预算，分块读取并保护在用快照
→ 目标detached准备：新ID、闭包、时间、碰撞、损失确认
→ 验证目标revision后一次发布 → 本地Undo/Redo
```

旧的“OS Clipboard短描述符／登录会话rendezvous／跨目录IPC音乐传输”候选已被用户修正取代，不能继续作为当前实现方案。D-MI06.a 已确认音乐视图 Paste 读取独立音乐槽，文本框仍使用 Windows 文本剪贴板，其他软件复制文字不影响音乐槽。不能用某个外部文本或任意本地路径冒充可信音乐快照，不使用反射对象反序列化，不复用1MiB启动参数通道承载音乐内容。

**Q2 已确认（D-MI06.a）：** 只保留最近一次成功 Midora Copy/Cut 的单个持久当前槽，不做历史库；完整快照和小型 current 索引放在 `<ProgramRoot>\Data\SharedClipboard`，未完成准备放在 `<ProgramRoot>\.tmp\SharedClipboard`，仅同 ProgramRoot、同用户共享。下次成功 Copy/Cut 原子替换当前槽，或由显式 Clear Clipboard 清除；失败 Copy 不改变旧当前项。已开始的 Paste 由在用 lease 保护，替换或清除后只有非当前且接收 lease 全释放的旧快照才可回收。当前槽不按年龄自动删除；资源预算或磁盘不足时保留旧项并让新 Copy/Cut 安全失败。两个新目录已获用户批准，但尚不在当前 SRS 路径白名单内，实施前须按定案同步；不能复用普通 session 清理器误删需跨重启保留的内容。

文件协议至少约束：版本/feature标识、长度/记录数/嵌套/依赖数量、总字节、分块完整性、压缩解码预算、取消、名称/表达式验证、路径与用户scope校验及错误提示。版本标识和严格验证是工程责任，不表示批准跨exe互通；同目录原地升级后的旧文件处理政策见 D-MI06.b。旧自由C# Mapping内容不能因剪贴板交换重新获得执行权。

### 2.3 对象范围不能由“共享剪贴板”一词推定（D-MI03）

D-MI03.a 已批准先把Note Art基础对象跑通，再扩展依赖闭包；**本批最终覆盖现有全部可复制对象类型，不能把首轮音符/事件误写成最终完成范围。** 不凭“全部”新增原先不存在的Copy入口；对象矩阵按现有能力与已批准新增编辑对象逐项落实：

| 类型 | 阶段安排 / 边界 | 额外合同 |
|---|---|---|
| 三类Note | 同TPQN基础支持，复用共同字段转换 / Direct NoteOff velocity规则 | 目标分配新ID、精确碰撞、未保留字段确认、合法owner范围 |
| 数值MIDI Event | 同正式target可表达时支持 | CC/压力/PB值、目标lane存在性、SubVoice禁入目标、同Tick覆盖 |
| Logical Parameter Point | 不能直接把源Parameter ID当目标ID | 显式选择目标参数或复制已批准定义闭包；禁止按名称猜绑定 |
| Segment | 后续闭包阶段 | crop/hidden内容、位置/长度、事件owner、跨类型损失确认 |
| Track / Root / Usage | 后续闭包阶段 | 所复制子集共享关系、Fixed Port.Channel冲突、新Root生命周期、Track顺序 |
| Definition/SubVoice/Mapping/Curve | 后续闭包阶段，现有可复制类型纳入本批最终范围 | 稳定ID闭包重映射、受限表达式ABI、名称冲突、外部引用、空owner；不凭“全部”新增原来不存在的Copy入口 |

支持范围逐项对Copy/Cut/Paste菜单给出可用性或精确错误，不静默只复制一部分。名称不是身份，不同项目相同数字ID没有关联；所有新对象使用目标allocator，闭包内部引用重映射，不保留源进程的活引用。

D-MI03.b～f 已批准：完整对象携带必要依赖，一次payload内共用依赖只clone一次、不同次Paste默认独立，不按名称自动覆盖/绑定；粘贴进既有Track/Segment不偷偷换其Usage/Root/Instrument，参数须显式映射。复制Track/Root到另一项目时保留所复制子集内部共享关系和Channel Mode，但新Root默认Auto，不自动合入目标同Port.Channel；这不顺带修改现有同项目Paste的Fixed复用行为。不复制SoundFonts或整个源Project环境，因此不同目标默认状态/绑定/音色库下不承诺相同听感。

跨实例Cut先可靠发布文件快照，再以源端一条Undo命令删除源；目标Paste是另一条独立command，不做跨进程共同Undo或分布式原子移动。源Cut取消/失败不删内容，目标Paste失败不自动撤销源Cut，但仍可重试完整快照。

### 2.4 TPQN（D-MI04）

同TPQN仍需检查owner起点/合法范围与精确碰撞。不同TPQN不能只缩放NoteStart：还有Gate、Segmentcrop/hidden、TemplateLength、PreRoll、Loop、Envelope、Curve、Mapping的时间Context。

表达式可能含用户写死的Tick常量，自动改源代码不存在通用保真算法。D-MI04.a～c 已批准首个切片仅同TPQN，但**本批最终支持基础Note/可表达Event及不含复杂逻辑依赖的Pure MIDI Segment/Track跨TPQN粘贴** 。默认保节拍比例，可明确选保原Tick；节拍换算不保证相同秒数，因为Tempo可能不同。采用精确有理数、起止边界单次 `AwayFromZero` 取整，Gate由边界得出并执行既有最小长度、溢出和精确碰撞规则，不重复取整。

携带Instrument/SubVoice/Loop/PreRoll/Envelope/Mapping闭包的复杂对象，本批仍限同TPQN；裸逻辑音符不因此受限。复杂依赖跨TPQN不在本次已批准范围，不能仅对显式Tick字段乘比例却声称保持表达式含义。以上是已决定范围，不表示跨TPQN代码已经存在。

### 2.5 已决定的持久生命周期与兼容边界（D-MI05/06）

应分别验证：源Project关闭、源实例正常退出/崩溃、音乐Clipboard被替换、接收已开始、接收未开始、目标取消、Windows注销及重启。

D-MI05.a/b 结合 D-MI02.d 的用户修正：完整文件快照发布后，不依赖来源Project或实例继续打开；只要所需文件完整有效，Windows注销、重启也不应自动使音乐Clipboard失效。不能沿用“仅当前登录会话有效”的旧推荐，也不能把“目录还在”误写成文件丢失、损坏或协议不兼容时仍可恢复内容。

替换或显式清除后已开始的Paste不得依赖仍为“当前槽”或源进程仍在；不可变快照和在用保护必须使读取与当前槽变更隔离。D-MI06.a 已确认单个当前槽、独立于外部文本Clipboard、显式清空、原子替换及非当前且无接收 lease 后回收的边界，不增加历史库、云同步或永久素材库。持久Clipboard也不是恢复整个Project的自动保存／崩溃快照系统。

**Q2 已确认（D-MI06.b）：** 用户在 D-MI05.c 排除跨目录／跨exe互通，但同目录原地升级或回退仍可能遇到旧文件快照。文件须有独立协议／数值契约／能力版本及严格边界校验，明确兼容时继续可用，不兼容时明确拒绝并提示重新复制，不自动删除未知文件。程序版本不等于文件协议版本；不能仅因同路径硬读旧字节，也不承诺任意历史版本迁移或扩大跨目录共享。

### 2.6 多实例验证门

真实2 / 4实例并发启动、同文件同路径 / 别名打开和保存、配置冲突、Recent合并、关闭一个实例不杀另一个Worker / 删除另一个临时目录；源退出/崩溃/文件发布中断/Clipboard替换/目标取消；注销重启后有效文件继续粘贴、不同ProgramRoot及不同用户拒绝互通；恶意文件头/版本不兼容/超限长度/路径注入；百万Note Copy/Cut/Paste与Undo、各种Root/Usage闭包、同TPQN/获批的基础跨TPQN换算/复杂依赖拒绝差异、源/目标revision race。按 D-MI06 定案覆盖槽替换、显式清空、外部文字复制不影响音乐槽、接收 lease、预算／磁盘不足保留旧项、原地升级兼容／拒绝及未知文件不删除矩阵。按切片实际能力报告，不把首切片拒绝跨TPQN等同于最终范围完成。

同时播放和设备丢失须真实测试，不能从WASAPI Shared Mode推定所有隔离成立。记录全进程树资源而不是只主进程；全局quota必须对并发实例仍成立。

## 3. R06：完整视图与Mute/Solo保存

### 3.1 当前基础

- `Midora.Application/ProjectPresentationSessionV3.cs:9` 已有独立revision、saved revision、recovery dirty。
- `ProjectPersistenceCoordinator.cs:214/259` 普通Save / Save Copy已冻结presentation；Save成功才更新对应baseline，Copy不清原会话状态。
- `Midora.Persistence/ProjectPresentationV3.cs:34` 当前只承载Onion和All Tracks模式；Format3独立presentation读1/2、写2。
- `Midora.Desktop/DesktopSessionController.cs:849` 四套Track / group Mute/Solo集合属于runtime，换项目清空；`:2455` 打开后只OpenArrangement，未恢复Tabs / viewport。

R06明确替代“普通UI和Mute/Solo不保存”的旧规定，但保留以下用户确认：只随显式Save保存，不进Undo/Redo，不标记音乐Modified。**Mute/Solo会改变运行期监听，不能写成完全不影响播放；它不改变源音乐、canonical、SMF/WAV成品。**

### 3.2 已批准的轻量状态白名单（D-STATE01）

用户已批准 `04` 附表 A/B；本表是对应归并，不重新开放全部类别。字段细化服从批准的所有权和资源边界；Q2 D-STATE03.e 已确认当前工具纳入同Track共享 profile，并继承 D-UI01.d 最终共享 Draw 与事件绘线形态的划分；D-STATE03.d 已确认存活 owner 仅保留受预算约束的 dormant 来源 ID。

| 分类 | 已批准保存 | 不包含 |
|---|---|---|
| Workspace集合 | 打开的已提交对象Tab、顺序、活动Tab；已关闭对象视图的轻量状态也保留；Arrangement唯一且固定 | 模态窗口、未提交draft、任务窗口、上下文菜单 |
| Piano / Arrangement / Conductor | 缩放、独立滚动、Grid/Snap、工具模式、显示面板与尺寸、活动子页；具体字段以owner表为准 | Selection全部ID、拖动候选、鼠标位置、预览音符 |
| Instrument Workspace | 当前子页/SubVoice、左栏宽度、滚动/折叠、预览键盘的可恢复显示设置 | 正在按住的键、音频任务、草稿表达式 |
| Lane Tabs | 已显示target及顺序、活动target、每lane纵轴、lane区高度；存在性仍受正式数据控制 | 数据索引 / bitmap / 后台source leases |
| 对象List / Tracks面板 | 开关、宽度、合法列宽/排序或筛选等明确获批字段 | 百万行对象VM、全量选区、过期ordinal |
| Diagnostics / Settings Workspace | Diagnostics筛选、Project Settings当前页等轻量导航 | 一份复制诊断报告、模态Properties草稿 |
| 导航指针 | Edit/Playback Cursor静止位置，恢复时Clamp到有效范围 | Playing/Buffering/Preview继续运行、任务续跑 |
| 监听过滤 | Track和独立Usage/Root组的Mute/Solo，两级分别保存 | 改写成员状态、改变canonical / 导出选择 |

这不是允许序列化整个ViewModel。字段应有类型、合法范围、默认值、owner身份和未知/损坏恢复策略；不保存内部ID到可见UI，但序列化引用仍使用Stable ID。

B1进入编码前还须通过资源实验冻结可执行的数值预算：presentation总字节、Tab/profile/lane条目数、单文本长度、恢复在途任务数和后台缓存上限。不能仅以“轻量/几十个Tab”替代输入上限。超过恢复预算时隔离对应presentation区并说明，不影响音乐加载；保存端不得静默截断用户视图。D-STATE02.c 已批准明确提示后允许保存音乐及可用视图部分，或取消；对应处理应落实到schema/工作流。预算数值由实验固定，不要求用户猜字节数。

### 3.3 保存与恢复合同

1. 音乐无Modified时，用户显式Save也能刷新presentation；标题不新增音乐星号，关闭不为单纯视图变化新增保存提示。相应Save可用性必须检查，不能仍因音乐未改而不写。
2. 开始Save冻结Project revision与presentation快照；保存期间新视图变化不能被误清为已保存。Save Copy携带同份冻结状态但不重置原保存基线。
3. 只保存已提交的视图描述，不自动写磁盘每次Pan/Zoom；内存profile轻量即时更新。
4. 恢复后Stopped，不自动开始音乐/试听；监听过滤在首个播放计划前生效。底层canonical仍包含未过滤音乐。
5. 恢复Tabs先轻量描述符，活动Tab按需加载；不把几十个后台workspace一口气同步解码或构建百万索引。
6. 删除owner时立即释放其自身视图状态，不短期保留供Undo恢复，也不因Undo自动重开Tab；普通关闭Tab但owner仍存活时继续保留轻量状态。Arrangement固定第一且唯一。D-STATE03.d 已确认其他存活owner可保留指向已删Onion来源的轻量dormant ID，供音乐Undo恢复来源选择；它不保留已删owner的profile或资源，保存时过滤悬空引用，关闭Project释放。
7. presentation损坏/未知版本继续不阻碍音乐加载；D-STATE02.b 已批准尽可能独立section回退，而非任意一个字段使全部布局丢失，同时维持严格JSON/版本检查和可理解Warning。
8. 新presentation schema不原地修改v1/v2，保留reader/golden。D-STATE02.d 已接受旧软件重写后可能丢失未知新视图字段，但不能损坏音乐；不承诺旧软件也完整恢复新字段。

只扩presentation通常可继续用外层Format3的独立schema分派；若音乐source同时增加不可表达内容，另评估Project Format。保留旧Format1/2 detached migration与确认后备份再原路径保存规则。

## 4. R07：Track通用profile与Segment独立状态

### 4.1 现状差异要讲清楚

`DesktopSessionController.cs:349/2187` 当前所有Segment主Piano共享一个controller级 `PianoRollEditorSettings`；`PresentationModels.cs:815` 包含Grid/Snap/默认音长力度。其他zoom、scroll、下部面板多在每Workspace，`:1055/1153` 的LaneEditorSettings也独立；关闭Tab（`DesktopSessionController.cs:2251`）销毁VM。

所以新要求不仅是“多记几个值”，还会将部分**跨所有Track共享** 缩为**同Track共享** 。D-STATE03.a 已批准这一所有权变化，实施时应避免某些设置仍全局串改。

### 4.2 Q1/Q2 已批准的所有权

| Owner | 已批准字段 | 同步 / 生命周期 |
|---|---|---|
| Project presentation registry | Tab顺序、活动Workspace、已批准的项目视图布局 / 静止指针 | 小型typed registry，独立revision |
| Track profile | 水平/垂直缩放、Grid；主Piano Snap与底部Event Snap两套独立配置及各自Snap值；共享主ToolMode和事件绘线形态（Free/Line/Horizontal）；默认Note长度/Velocity、Lanes/List/Tracks开关与通用面板尺寸 | 同Track即时同步；不同Track独立；与Usage/Root共享状态无关；主区与事件区不拆成独立ToolMode，不因工具栏合并而把Piano/Event Snap合并 |
| Segment view state | 各自horizontal start、vertical origin、活动lane、该owner实际Tab显隐/顺序与局部布局 | 关闭Tab保留轻量字段；删除owner即释放；不保留VM/source缓存 |
| Segment + lane target state | 每target纵轴、显隐/顺序、实际局部状态 | Segment独立而非Track默认联动；不在其他Segment创建正式Lane / Mapping |
| SubVoice view state | 自己的编辑profile、lane状态与局部viewport | 独立于Logical Track共享profile；不因绑定或Usage改变而串改 |

用户调zoom时写一份Track profile，当前可见Tab即时更新、隐藏Tab在激活时读取同一revision；不互相广播复制VM属性制造反馈环。共享缩放不共享鼠标锚点或滚动起点，另一Tab保留自己的世界坐标观察位置。每次写入应与受影响Tab数成比例，不与音符数成比例。

关闭Tab不删除仍存活owner的profile，关闭Project释放整个会话registry。D-STATE03.a～c 已决定：profile归Track ID；Duplicate Track复制轻量偏好、之后独立；迁入Segment使用目标Track通用profile但保留/校准自己合法滚动位置；加入共享Usage/Root不合并各Track profile。用户在 D-STATE03.c 明确修正：删除owner即释放其自身视图状态，不保留短期dormant profile，Undo也不自动重开Tab。

**Q2 已确认（D-STATE03.d）：** 删除 Track B 立即清除 B 自身的 profile、Tab/viewport 状态和缓存；存活 Track A 的 Onion 配置可保留一条受条目预算约束、仅有稳定 ID 的 dormant 来源引用，以便音乐 Undo 恢复 B 后重新显示。该 ID 不持有 B 的对象、数据页、位图、任务或资源 lease；保存仍过滤悬空引用，关闭 Project 释放。此规则沿用 SRS §18.11.3 的来源引用恢复体验，不恢复 B 自身的 dormant profile 或自动重开 Tab。

**Q2 已确认（D-STATE03.e，继承 D-UI01.d 最终回答）：** 当前工具纳入 Track 通用 profile；保留主区与事件区共享的 ToolMode（含 Draw），事件视图另有 Free/Line/Horizontal 三种绘线形态，不再创建三个独立 Pen 或拆分两套主工具。事件绘线形态使用纯图标按钮，非 Draw 模式禁用这些按钮；同 Track 的 ToolMode 与事件绘线形态同步，不同 Track 隔离。SubVoice 各自独立，Arrangement/Conductor 保留各自工具；不支持的工具使用明确合法默认值。此归纳依推荐中“按 D-UI01.d 最终区域划分”的引用承接用户修正，不重新开放白名单，也不改变各 lane 纵轴、顺序及 Piano/Event Snap 的独立配置。

现有从Arrangement显式打开Segment会在编辑指针位于Segment时将其置中（`DesktopSessionController.cs:2201`、SRS §18.2）。D-STATE03.b 已批准保留这一优先级：从Arrangement显式打开且编辑指针在Segment内时定位到编辑指针；普通切Tab / 项目恢复遵守已记忆viewport。

### 4.3 状态专题验收

同Track多个Segment同步（含共享ToolMode与事件绘线形态）、不同Track隔离、主区/事件区共用Draw且非Draw禁用形态按钮、独立滚动、关闭重开、跨Track移动/复制、删除Undo、重命名；删除B释放其自身状态而A仅保留受预算约束的dormant来源ID，Undo恢复来源显示但不重开B的Tab，保存过滤悬空引用；R28重排/隐藏/每lane纵轴；只变视图Save、Save中再变视图、Save Copy、损坏section、未知version、旧格式迁移；几十个隐藏Tab首次打开/释放内存、无额外编译、canonical/SMF/WAV等价。

恢复大量Tabs不能显著增加首次可用时间，也不能使所有隐藏Onion/All Tracks索引常驻。自动化检查订阅计数、关闭后引用释放和缓存上限，人工只验恢复准确与焦点。

## 5. R04：外部文件拖入 / Open With

源码确认：`MainWindow.xaml.cs:163` 已接收第一个`.midora`启动参数、相对请求WorkingDirectory解析，接入打开流程。`:4706`菜单MIDI导入有Port复审、可取消任务、原子会话替换、完整报告和Worker预热；未发现FileDrop处理或Open With注册路径。存在内部AllowDrop不等于外部文件打开已实现。

建议先抽统一“请求打开一个外部文件”协调器，让菜单、命令行、转发和拖入共同使用：扩展名/路径→停止与未保存确认→detached打开/导入→一次会话提交→报告/Recent/预热。DragOver只判断可接受类型，不读取MIDI或构建项目。

D-OPEN01.a～d 已批准：接受 `.midora/.mid/.midi`，每次一个文件，MIDI复用现有导入范围；多文件明确拒绝，不偷偷只取第一个，也不为每个文件分别开实例。拖入/窗口内Open替换当前并守未保存确认；多实例完成后系统Open With/命令行带文件开新窗口，同文件已有可写实例则定位它。模态/前台长任务期间明确拒绝，不暗排迟到打开队列。

Open With应支持用户手工选Midora.exe时正确处理带空格/Unicode路径；不自动注册默认关联、不修改默认应用，保持最小系统侵入。安装/portable发布的显式关联入口不在本次范围。ENV-WIN10 已说明用户可提供Win10虚拟机、不能提供实机；可以安排虚拟机验证，报告须如实区分虚拟机与实机，不把尚未执行的验证写成已通过。

测试：干净/Modified/从未保存项目、确认取消、打开失败保留旧项目、非法扩展、文件夹/多文件、冷启动/已有进程、忙碌/模态、MIDI Port复审、导入Warning完整报告、预热失败保留已打开项目。多实例上线后相同入口不能发生双重打开或覆盖。

## 6. 后续实现分阶段建议

见[总计划B/C](00-Overview-and-Delivery-Plan.md)。两专题不是“做完一个功能才想状态”：C先管共享写入及文件型Clipboard安全，B依已批准白名单和所有权推进；D-MI06.a/b及D-STATE03.d/e 已全部回答，没有本专题已知的必须 Q3 项，可进入实施规划，不重新开放 Q1 已定范围。协议/schema、数值预算、锁与 lease、崩溃回收方案须在各自进入实现前冻结并正式同步 SRS；若后续验证暴露新的用户可见取舍，再单独记录具体问题，不把工程参数未定当作整轮问答未完成。各轮仍需详细自动测试+小型人工清单，不要求用户手工验证所有多进程竞争或损坏文件组合。
