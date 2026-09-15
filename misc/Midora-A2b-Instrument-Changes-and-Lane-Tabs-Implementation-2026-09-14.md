# A2b：Instrument Change 全编辑与 Lane Tabs

状态：A2b 全部人工验收及五项交互返修已获用户通过；扩大压力门未全部关闭。日期：2026-09-14～15。基线：`10afe2e9f5ad48bef7587eb075474d6b83166245`（开始时工作区干净）。实施轮未提交；本次按用户明确要求记录、提交／推送，不使用 computer-use，不生成 dist。

## 1. Requirement trace

本轮承接执行计划 02 的 T-IN-08～10 与执行计划 03 的 T-LANE-01～08；A2a 已验收的 source、Format 4、原始事件、试听与安全保存合同保持不变。

- 输入：冻结 owner revision、正式成员关联、正式 target、选择和会话 Lane 状态。
- 输出：完整包装的原子 raw 编辑与关联维护；普通 Lane 的展示、导航和准确摘要。
- 身份与边界：ID 是身份；同 Tick 使用正式顺序；导入不推断包装；隐藏 Lane 不删除任何数据或 Mapping。
- 失败：取消、旧修订、资源上限、非法值、溢出均不得部分发布 source、历史或选择。
- 持久化：包装仍属 Format 4 source；本轮 Lane 可见性、顺序、活动目标及坐标轴只属于会话，不修改音乐内容或 Modified。
- 运行时：只保留一个活动事件画布；后台摘要和投影可取消，旧结果不得改变活动目标或恢复旧选择。
- 非目标：A3 的新绘线手势、A4 的友好数值/阶梯线、不属于本轮的音频重构、自动提交或发布。

## 2. 工程设计记录

1. 三个宿主共用 Lane Tabs 和目录模型。固定 `Vel.`，MIDI Segment / SubVoice 另有固定 `Inst.`；其他 Tab 以正式 target 为键，而不是名称、索引或选择状态。
2. 目录摘要与绘图数据分离。目标发现不再为显示目录复制全部点；冷摘要后台建立、按不可变源修订复用，增量编辑利用修改范围更新。未完成时显示明确的计数中状态。
3. 普通切换只改变活动画布，不隐式清空其他选择。显式 Add / Locate / 目录定位可以显示并激活目标，普通刷新不得导航。
4. Instrument Change 编辑复用有界事务、raw 碰撞规则和成员最终态校验。包装不是新音乐事件；List 特殊行只是一种无重复的 source 投影。
5. 新建/修改的后台工作须有取消与修订检查；沿用既有工作内存、分页及 spill 预算。不得用百万 WPF 行、全量字符串或无界 HashSet 替代分页。

## 3. 实现定位与已修改项

| 范围 | 主要文件 | 本轮行为 |
|---|---|---|
| 包装完整编辑 | `ProjectInstrumentChangeBatchCommands.cs`、`ProjectObjectClipboardInstrumentChanges.cs`、`MainWindow.InstrumentChangeEditing.cs` | MIDI／SubVoice 的 Move、Ctrl-Duplicate、Copy／Cut／Paste／Delete、水平 Flip、Scale、Quantize、Mixed Properties；一份正式事务及一次 Undo。与其他类型混选时只由显式类型菜单处理包装子集，其余选择保留。 |
| 稳定关联及失败原子性 | `BoundedInstrumentChangeStorage.cs`、`ProjectTimelineOwnerChangeSetBuilder.cs`、`InstrumentChanges.cs` | ID／反向成员双索引、稳定压缩、页共享；完整事务最终态校验，局部破坏才解组，保留剩余 raw。取消不发布 source、NextStableId、选择或 History。 |
| 载入／父复制 | `BoundedInstrumentChangeStorageLoader.cs`、`InstrumentChangesProtobufCodecV1.cs`、`InstrumentChangeCopies.cs`、`MidoraProjectPackageV1.cs` | 严格 loader 将有效关联落入有界 source；Format 4 wire 不变，父复制重映射稳定 ID。未恢复旧格式执行或放宽错误关联。 |
| 顺序及碰撞查询 | `ProjectInstrumentChangeOrdering.cs`、`BoundedDirectMidiIndex.cs`、`BoundedDirectMidiEventSource.cs`、`BoundedTemplatePointPlan.cs` | 新组 Bank→Program 在本 Track 同 Tick NoteOn 前；旧成员值改／移动保留正式 order。精确碰撞从逐键扫描 leaf 改为排序 key 范围定位，仍保留全部实际候选。Flip／Scale 碰撞拒绝原子操作；普通事件编辑仍 later-wins。 |
| 包装图形／命中 | `InstrumentChangeLane.xaml(.cs)`、`InstrumentChangeLane.Interaction.cs`、`InstrumentChangeProjection.cs` | 框选／Ctrl 选择、移动及复制预览、坐标／delta、正式索引 hit-test；显示与选择各用有界像素投影。选中的点即使不是普通图层某像素的代表点，也不会消失。 |
| List | `TimelineObjectListSource.cs`、`TimelineObjectSelection.cs`、`MainWindow.TimelineObjectList.cs` | 一处完整包装为一行，不再重复列其 Bank／PC 成员；选择仍使用真实成员 ID。解组恢复 raw 行；Locate 显示并激活 Inst.。 |
| Lane Tabs | `LaneTabSession.cs`、`LaneTabHost.cs`、`LaneTabHeader.xaml(.cs)`、`MainWindow.LaneTabs.cs`、`MainWindow.xaml` | 三宿主共享；固定 Vel.／适用 Inst.，普通 target 可重排／隐藏；目录支持搜索、准确数量、Curve／Mapping 标记及显隐；旧 LANE 下拉／第二行工具栏移除。 |
| 摘要及局部更新 | `DirectMidiTargetSummary.cs`、`PureMidiPointQuerySnapshots.cs`、`PagedTimelineCollections.cs`、`PagedTimelineSharedRoots.cs`、`PagedTimelineSplicedRoots.cs`、`PresentationModels.cs` | Direct 缺摘要的不可变源首次后台扫描，只保留 target 计数；SubVoice 页摘要由“出现该 target 的页数”修正为精确事件数。增量 source 利用 old/new 差额、Undo 复用旧摘要。统计中不写 0，故障显示 unavailable。 |
| 焦点／生命周期 | `MainWindow.xaml.cs`、`DesktopSessionController.cs`、`LaneTabHeader.xaml.cs` | 显式 Add／Locate 激活；选择／Properties／Undo／后台发现不自动切 Lane。各 target 纵轴独立；事件 Snap 共用且与 Piano 分离；点击 Snap 后焦点回活动编辑区。隐藏／卸载取消读取、关闭菜单并释放旧投影快照。 |

本轮没有新音频模型、没有改变 canonical 音乐语义或 `.midora` 版本。A2a 的正式 source 关联不是可丢弃的视图缓存。后续工作区持久化仍留给 B 主题，本轮 Lane 顺序／隐藏／轴不写进 Project 文件。

## 4. 有界设计与实测

### 4.1 上限和释放

- 包装关联复用既有分页事务资源：默认每页 4096 条；工作内存上限 64 MiB、resident 页上限 64 MiB、spill 上限 16 GiB、单 store 记录预算 100,000,000。它们是事务内部预算，不是整个进程总内存上限；超限受控失败，不放宽资源预算换取通过。
- 包装 display index 与 selection index 各使用 4 MiB working＋4 MiB resident，有界排序／spill。显示最多取 16,384 个设备列；每帧标签最多 128 个。命中与框选查询正式排序索引，不读取显示 bitmap 判定身份。
- 每个 Inst. 控件至多一项执行中投影和一项覆盖式待处理请求；旧任务取消、owner／revision／selection revision 隔离，卸载释放索引、快照和事件订阅。它不会为每个 target 创建常驻画布。
- Lane 目录只存 K 个正式 targets 的轻量摘要，未知 Direct source 允许首次 O(N) 可取消后台扫描；热目录读 K，不保留 N 个事件的第二份点数组。首次补建不是“完全无扫描”。
- 近期校验缓存至多 4096 个已验证组，计入工作预算，避免一个包装的 2／3 个成员反复校验；缓存不跨 source revision。
- 实验定时检测 testhost private bytes，8 GiB 时取消，早于用户约 9 GiB 的安全警戒。以下均未触发内存警戒或系统分页假死。

### 4.2 40 万个包装：通过，但仍有明确准备时间不足

环境：Windows 11 Pro for Workstations `10.0.26200`，i7-10750H（6C／12T），物理内存约 31.9 GiB；SDK `10.0.400`，测试宿主 `.NET 10.0.11`，Debug 构建。每种 owner 400,000 个包装，即 MIDI 1,200,000 条 raw 成员／SubVoice 800,000 条成员。不是把“40 万音符”换了名称，也不等同于 UI 进程树测量。

探针 `InstrumentChangesA2bPerformanceTests.MeasureBoundedLargeWrapperOperations` 实际执行新增、Resolve、Move／Properties／Quantize／Delete 及每次 Undo；15 分钟总时限。选择 fixture 使用紧凑 ID 区间，不人为加入百万项 HashSet。结果：**13 分 54 秒完成，通过** 。

| 项目 | MIDI | SubVoice |
|---|---:|---:|
| 首次追加 400,000 包装 | 74.686 s | 26.517 s |
| 解析全部包装 | 12.031 s | 6.972 s |
| Move 准备 | 128.904 s | 68.282 s |
| Properties 准备 | 97.076 s | 72.866 s |
| Quantize 准备 | 108.869 s | 68.217 s |
| Delete 准备 | 109.517 s | 60.409 s |
| 已准备结果 Apply 最大值 | 0.35 ms | 0.09 ms |
| Undo 最大值 | 0.81 ms | 0.06 ms |
| 事务 working 峰值 | 8.3 MiB | 12.5 MiB |
| 事务 resident 页峰值 | 64.0 MiB | 64.0 MiB |
| 事务 spill 峰值 | 1,121.9 MiB | 890.7 MiB |

整个探针峰值 Working Set **560.2 MiB** ，退出前 private ** 322.9 MiB** 。预算计数与 Working Set 口径不同，不相加、不拿退出 private 当峰值。报告的 Apply／Undo 仅数据发布，不包含 WPF 后续刷新、自动编译或可听延迟。

首轮同规模实验在 SubVoice Move 中达到 15 分钟时限并安全取消。首轮 MIDI Move／Properties／Quantize／Delete 准备分别 194.939／128.528／159.359／165.242 秒；查明了重复成员解析、逐键全扫描及冗余关联校验，修正后如上。首轮存在其他构建／测试并行，不能把这些差值当严格隔离的性能收益比例。

10,000 包装的定位实验中，修正有序查询后 MIDI collision 阶段由约 6.3～6.6 秒降至 0.123～0.463 秒，Move 总准备由 10.111 秒降至 3.654 秒。这是定位证据，不是 UI p95/p99。

**仍未达成“几十万包装的准备也很快”** ：有界排序、多个正式 raw 索引更新和最终关联校验仍需一至两分钟。相关任务有进度、可取消，提交和 Undo 不再扫描整个选择，但不能将此写成无性能不足。后续若真实编曲使用如此多音色变更，需要针对该准备链另做时间优化；本轮没有用取消预算或省略正确性校验掩盖成本。

### 4.3 真实 9KX2 的摘要与单点

只读输入 `D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`，144,175,201 bytes；Application 探针导入得 **17,999,999 条音符** 。没有修改样本，不启动 WPF／BASS／Worker。

- 导入：60.249 秒。这里不是导入器优化对比。
- 全部 Channel targets 冷摘要：**28.64 ms** ，总计 ** 3,484 条** Channel Events。
- 对所有摘要重复读取 100 次并逐 owner 校验总数：**10.17 ms** 。热访问复用摘要，没有按 1800 万音符扫描。
- MIDI Out #23 在 Tick 168960 新增一处完整包装并 Undo：首次准备 685.24 ms；同位置之后四次 80.56／76.39／86.51／89.58 ms；Apply 最大 0.29 ms，Undo 最大 0.30 ms。
- 分配量首次 60.09 MiB，后四次 47.13～47.39 MiB；GC heap 92.11～135.98 MiB；峰值 Working Set **259.96 MiB** 。分配量是累计申请，不是同时驻留。

此样本证明超大音符项目的 Lane 目录不按音符总量重建，不证明百万 Channel targets／包装的 WPF 全交互均通过。

## 5. 自动验证

原始 TRX 位于 `.tmp/test-results/a2b/`，不进入 Git。正常测试不隐式读取用户 MIDI、不生成验收文件；两者由环境变量显式 opt-in。

| 项目 | 实际结果／记录 |
|---|---|
| Application 全套 | 最终 1215／1215 通过（6 分 14 秒），`a2b-application-final.trx`；首轮 1210／1210 也通过。 |
| Instrument Change 最终专项 | 33／33 通过；含两种 owner 全批改、精确碰撞、部分／完整结构维护、取消不发布、Ctrl 复制到 0、跨 owner 粘贴、bounded Format 4 保存重开、稠密选择独立投影。`a2b-instruments-final.trx` |
| Desktop 全套 | 最终 457／457 通过（2 分 28 秒），`a2b-desktop-final.trx`；含真实 MainWindow BAML／隐藏 HwndSource，非 computer-use。 |
| Presentation 全套 | 482／482 通过，`a2b-presentation.trx`。 |
| Persistence 全套 | 239／239 通过，`a2b-persistence.trx`。 |
| Compiler 全套 | 572／572 通过，`a2b-compiler.trx`。 |
| MIDI Export 全套 | 88／88 通过，`a2b-export.trx`。 |
| 400,000 包装 | 最终 1／1 通过；首轮超时见 §4.2。`a2b-400000-after.trx`。 |
| 真实 9KX2 | 1／1 通过，`a2b-9kx2.trx`。 |
| Desktop Release 构建 | `dotnet build src/midora-desktop/Midora.Desktop/Midora.Desktop.csproj -c Release --no-restore -v:q`；33.74 秒，0 警告、0 错误。只构建，不生成 dist。 |

真实 WPF 集成覆盖三个宿主、32px target header、一个活动可交互 Surface、保持选择、隐藏后被动刷新不恢复、独立纵轴恢复，以及受控 100／125／150／200% DPI、640px 宽长标签／极端坐标下 Snap／Subdivision／目录可达。受控 DPI 不是实际多显示器拖动验收。

回归过程中纠正过：新建 Logical Parameter 显式导航遗漏、旧测试错误要求“选到另一个 Lane 的点就自动导航”、旧 TabControl 静态结构断言、测试 fixture 在不可变 root 替换后仍读旧引用。一次并发构建撞到测试占用 DLL，停开同输出目录构建后重跑；不作为产品失败。新增验收 fixture 一次编译错误（Project 名称应经 Metadata）已修正并重跑通过。首轮失败／超时未从本报告删除。

复测入口：

```powershell
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj --no-restore --filter FullyQualifiedName~InstrumentChangeTests
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj --no-restore
$env:MIDORA_A2B_GROUPS = '400000'
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~MeasureBoundedLargeWrapperOperations
```

最后一条有明确 15 分钟／8 GiB 实验保护。请勿在同一输出目录的测试仍运行时重新构建，避免 Windows 文件锁干扰。

## 6. 人工验收：一轮 12 项

样例：`D:\Programing\midora\.tmp\uat\a2b\A2b-Lanes-and-Instruments.midora`。已用当前正式 writer 保存并重开校验。包含一个 MIDI Segment、一个 Logical Segment 和一个 Event Instrument/SubVoice；两个 Inst. 各有四点 Tick 0／768／1536／2304、Program 0／1／2／3，Bank 0.0；含 CC1／7／10／11／64、音符与长名逻辑参数。声音依赖程序已有 SoundFonts，这份工程不携带音色库。

先打开小样例，检查 01～05 及 Lane 项；大样例仅用于 IN-01／LANE-06。**不需要为了人工验收再手工创建 40 万音色包装** 。改坏小样例可重新打开原文件，建议使用 Save Copy，避免覆盖唯一基准。

| ID | 操作 | 应看到 |
|---|---|---|
| IN-01 | 导入一个含 Bank／PC 的 MIDI，打开 Inst. 和 raw tabs；随后显式新增一处包装 | 导入 Inst. 空，raw 可正常编辑；仅显式新建进入 Inst.。 |
| IN-02 | 小样例的 MIDI／SubVoice Inst. 多选后 Move、Ctrl 复制拖动、Copy／Cut／Paste／Delete，各试一次 Undo／Redo | 整组变化，一次 Undo；复制后选择副本；不提供纵移／音符专属工具。 |
| IN-03 | 多选四点，右键 Flip／Scale／Quantize／Properties；Mixed 启用、改值、恢复，再取消一次、确认一次 | 时间工具可用；未启用字段保留；取消不改数据，确认原子提交。 |
| IN-04 | 在 CC0／32／Program（SubVoice 为 Bank／Program）raw tab 改一个成员值，再单独移走它，Undo | 值改保留包装、标签更新；移走解组，raw 仍在，Undo 恢复完整关联。 |
| IN-05 | 打开 List，选择包装／Note／raw；包装双击 Properties 或 Locate；解组后 Undo | 一处包装仅一行，不重复成员；类型菜单正确；定位 Inst.，不会混选失效对象。 |
| IN-06 | Save Copy 后重开，查看两种包装并从头播放；已知旧格式项目再走确认／取消升级流程 | 关联和值保持；同 Tick Bank→Program 在该 Track 的 NoteOn 前；旧源永久副本保护未变。 |
| LANE-01 | 三类钢琴卷帘打开 Lanes | 每 target 一 Tab；固定第一 Vel.，MIDI／SubVoice 固定第二 Inst.，Logical 没有 Inst.。 |
| LANE-02 | 缩窄窗口、用滚轮横向浏览长名 Tabs，拖拽普通 Tab 重排 | 固定项不移动／隐藏，普通项重排；右侧坐标／Snap／Add／目录可用。 |
| LANE-03 | 关闭有内容的 CC11 Tab，开下箭头目录搜索再点击 | 关闭只隐藏；目录仍可见名称／数量／Hidden，点击重新显示并激活。 |
| LANE-04 | 改／删一条事件，观察目录数量并 Undo／Redo；隐藏后被动刷新；用 Add／List Locate | 数量随实际内容变化；普通刷新不抢回目标，显式 Add／Locate 才切到它。 |
| LANE-05 | 选一个 Lane 的点，切其他 Lane 并分别调纵轴；改上下两个 Snap，再按 A | 选择不暗中清掉；纵轴独立、水平共享；Piano/Event Snap 分离，A 操作当前事件区。 |
| LANE-06 | 9KX2 下切多个 target、开关目录／List，关 Workspace／Project，再开小样例 | 无旧选择或旧目标闪回；统计中有反馈；关闭后没有仍占用 UI 的过期任务。 |

完整稳定编号为 `UAT-A2b-IN-01～06` 和 `UAT-A2b-LANE-01～06`，与 [计划02](Midora-Next-Development-Requirements-2026-09-11/execution/02-Instrument-Changes.md)／[计划03](Midora-Next-Development-Requirements-2026-09-11/execution/03-Lane-Tabs-and-Event-Display.md) 同一清单。用户先确认除 LANE-02 外整体验收通过，随后确认 LANE-02 和五项交互返修通过；本清单 12 项均已通过，原反馈及最终归档见 §8。

## 7. 保留风险／未跑门

1. 四十万包装的准备耗时仍高，见 §4.2；不能以毫秒级 Apply／Undo 隐藏这一点。
2. 本轮没有实测完整 UI p50/p95/p99、鼠标捕获所有竞态、真实多显示器 DPI 切换、长会话全部弱引用释放及全部 native／磁盘故障组合；只记录真实已跑测试。
3. 真实 9KX2 有 1800 万音符，但仅 3484 条 Channel Events；百万包装＋全工具笛卡尔积和百万 Channel 目录 UI 压力仍未穷尽。400,000 包装测试含百万 raw，并不代替这些全部门。
4. 未重新做真实 SoundFont 听感／native 故障／音频热线程分配测试；本轮不改变 A2a audition 或音频后端。已有 Compiler／Persistence／Export 回归不能冒充听感验收。
5. Direct 冷摘要现采用一次后台补建，不在 MIDI 导入热循环里增加统计。后续若改为在导入／Content Pack 构造时顺带统计，应先实测导入耗时，不能为了目录预热倒退大型 MIDI 导入性能。

这些保留项限制完整工程门关闭，不缩小已批准功能矩阵，也不自动要求用户重复全部底层测试。

## 8. 2026-09-15 验收返修

用户反馈：除 LANE-02 外整体验收通过；普通 target Tab 只有缝隙附近可拖放，几乎无法排序。另外要求统一右上角 `+` 为 Add Lane、Inst. Draw 固定 y 创建预览、蓝色框选、中键平移。

本轮 requirement trace：输入为当前 owner 的 Lane 会话状态与鼠标手势；输出只改活动画布、横向 viewport、轻量 transient 和会话顺序。Vel./Inst. 不可重排；隐藏、选择、正式内容与 Undo 不因重排或平移改变。跨 owner/外部拖放、失效 owner、取消、失去 capture 不发布编辑。Project Format、canonical、编译与音频均不变。

根因与实现方向：Lane 头是虚拟化横向 ListBoxItem，而非普通 Button；缺少 DragOver 消费导致事件冒泡到外层 Workspace Tab 的另一种 payload 判定，且每次激活都重建 ItemsSource。沿用主 Tab 的整头目标判定方式，共用其启动阈值，按 owner 隔离拖放、拦截冒泡、保留虚拟化与单活动画布；不重写已验收的主 Tab 行为。Inst. 原有输入代码只处理左右键，没有中键路径；框选错误地硬编码 IndianRed，创建预览也没有绘制入口。

### 8.1 实现与自动证据

- `LaneTabHeader.xaml(.cs)`、`LaneTabHeader.Drag.cs`：`+` 单一路由到 Add Lane；DragEnter/DragOver/Drop 在本层判定并消费，整头、文字、缝隙、尾部均可作为目标；payload 带 Header/owner session，固定项和跨 owner 拒绝。复用主 Tab 的 `HasReachedUiReorderDragThreshold`，不改变主 Tab 的拖放行为；边缘拖拽可滚动，离开、取消、切 owner、卸载均停止计时器。普通激活不再替换相同 ItemsSource。
- `InstrumentChangeLane.xaml(.cs)`、`InstrumentChangeLane.Interaction.cs`：创建预览／框选／delta 使用独立 `InstrumentChangeGestureVisual`，不为悬停重查事件或重绘标签。固定 y 预览使用 Event Snap，框选采用 `Brush.Info` 蓝色虚线及同样的 22% 填充；选中点原有红色不变。中键共享 Workspace StartTick，复用安全 Tick 算术，不改变纵轴或选择；Escape、capture loss、卸载清理手势。
- 新增 `LaneTabInteractionRegressionTests.cs` 并扩展真实 WPF 集成：三宿主、DragEnter/DragOver 在文字宽度 5/25/50/75/95% 位置的真实冒泡路由、Drop 正式顺序、固定项、跨 owner/外部 payload、缝隙、相同 ItemsSource 保留；500 个长 target 的横向滚轮及少于 30 个 realized container。两种 Inst. 覆盖 Snap on/off、固定 y、界外不预览、蓝色及虚线实际 Drawing、平移和 0／Int64 frontier、Escape、选择不变。沿用受控 DPI 布局测试。
- Desktop 全套 **458/458** 通过，2 分 20 秒：`.tmp/test-results/a2b/a2b-ui-fixes-desktop-full.trx`。Presentation 全套 **482/482** 通过，15 秒：`a2b-ui-fixes-presentation.trx`。
- Desktop Release 构建通过（17.57 秒，0 警告、0 错误）；`git diff --check` 通过。只构建，不生成 dist。
- 首轮专项测试曾在未模拟中键按住的隐藏宿主中错误混用真实 capture 与数学断言，因 MouseMove 报告 Released 而得到 1024 而非 1124；随后拆开捕获入口和纯平移状态初始化，专项复跑通过（19 秒），最终全套也包含该检查。没有把首轮结果写为通过。
- 本轮未重跑 40 万包装数据压测或音频门；没有改变 raw 事务、投影索引、编译、格式或音频。交付时的真实鼠标手感、屏幕视觉复验清单如下；后续用户确认见 §8.3，不以隐藏 WPF 测试替代。

### 8.2 只复验本轮五项

仍使用 §6 的小样例，不必重复已通过的 11 项或创建大型包装选区。

| ID | 复验操作 | 预期 |
|---|---|---|
| FIX-01 | MIDI／SubVoice 分别在 Vel.、Inst.、普通 target 按右侧 `+`；取消一次 | 始终是 Add Lane，不弹添加 Inst. 包装的窗口；取消不改数据。 |
| FIX-02 | 两种 Inst. 的 Draw 模式，在不同鼠标高度横向移动并切换 Snap；移出内容区 | 预览跟随横向 Tick、纵坐标固定；Snap 生效，离开隐藏。 |
| FIX-03 | 两种 Inst. 的 Select 模式拖框 | 蓝色虚线及浅蓝填充，与其他视图一致；松开后的已选点颜色不变。 |
| FIX-04 | 两种 Inst. 中键左右拖动，包括拖出视图后释放及按 Escape | 时间轴与上方钢琴卷帘同步平移；无值／选择／历史改动，不会释放后继续拖动。 |
| FIX-05 / LANE-02 | 三宿主拖普通 Tab 到其他 Tab 的文字主体／左右半边；试固定项；多目标缩窄后滚轮和拖拽到边缘 | 不需命中狭窄缝隙，普通项可排序；Vel./Inst. 顺序固定、不能拖动；可滚动访问更多目标，原选择和各目标纵轴保留。 |

返修交付时尚未获得用户确认，因此当时未进入 A3，也未提交、推送或发布；后续确认如下。

### 8.3 人工验收归档（2026-09-15）

用户原答：

> 验收通过。记录、提交、推送。

- `FIX-01～05`（含 `UAT-A2b-LANE-02`）全部通过。结合此前确认，`UAT-A2b-IN-01～06`、`UAT-A2b-LANE-01～06` 均已获用户验收通过。
- 本次仅记录确认并按明确请求提交／推送 A2b 全部成果；不启动 A3，不本地发布。下一默认实施批次仍为 A3，等待后续指令。
- §4～5、§7～8.1 的实测结果、首轮失败及未跑工程门原样保留；人工通过不等于全部压力／平台／故障组合已验证。本次归档不重新运行产品测试，沿用返修后的实际证据。
