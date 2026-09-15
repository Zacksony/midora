# A2a：Instrument Changes 基础与统一乐器选择器

状态：A2a 产品切片及 UAT-A2a-IN-01～06 已获用户整体验收通过，见 §8。未跑的完整性能／原生音频门仍见 §5，不表示全部工程门通过。A2b 尚未实施，不能据此宣称 R27 全部完成。实施轮没有提交／推送；本次按用户要求记录验收并提交／推送，不发布、不使用 computer-use。

## 需求追踪与边界

依据：[执行计划](Midora-Next-Development-Requirements-2026-09-11/execution/02-Instrument-Changes.md) 的 T-IN-01～07，以及已确认的 D-IN-01～05、Q2；原始回答不改写。

| 项目 | 本轮契约 |
|---|---|
| 输入 | 冻结 owner/revision、原始 MIDI/Template Event、程序级 Catalog、选择器 draft |
| 正式结果 | 有独立稳定 ID、只引用成员 ID 的 Instrument Change；音乐值仍只存在于原始事件 |
| MIDI 成员 | 同 owner、同 Tick 的 CC0、CC32、Program Change |
| SubVoice 成员 | 同 owner、同 Tick 的完整 Bank Select 与 Program Change |
| 导入 | 不自动发现、创建或投影 Instrument Change，Inst. 初始为空 |
| 提交 | 最终事务状态校验；值编辑保留关联，部分删除、错位、目标改变或成员被替换则拆除关联，保留剩余原始事件 |
| 顺序 | 新组 Bank→Program 在本 Track 同 Tick NoteOn 前；不改早先其他 Track 的正式顺序；原始 PC 编辑保留原顺序 |
| 撤销 | 音乐成员、关联、ID 分配及选择结果属于同一次原子事务；失败、取消、旧修订零发布 |
| 存储 | 严格版本化源数据，不放入可损坏回退的 presentation；保留旧 Format reader、schema 和 golden |
| 选择器 | Catalog 名称辅助；MSB/LSB/Program 均 0～127，未知数值有效；不隐式扫描 SoundFont |
| Initial State | Event Instrument/SubVoice 三个独立继承位保留；不修改 Project 全局 Initial State；不生成 Tick 0 事件 |
| 试听 | 独立干净状态、正式 canonical 预览；自动默认 500 ms，手动按住发声；latest-wins、显式任务所有权、Master→Limiter |
| UI | MIDI Segment/SubVoice 的 Vel. 后增加 Inst.；Logical Segment 不加；只创建可见点/标签 |
| 非目标 | A2b 包装批量命令及完整 Lane Tabs、后续手势、多实例、发布与 Git 提交 |

## 实施前审计

- 现有 Direct MIDI/Template 大型编辑采用 detached owner root replacement；关联生命周期必须覆盖 root replacement 和旧原位命令，不能只给新增按钮添加逻辑。
- Direct MIDI 同 Tick 使用显式 Order；新组不能使用稳定 ID 代替事件顺序。
- 现有 Catalog resolver 可复用优先级。旧 SRS §8.55.1、§18.4.2 的无名称/Program 1～128 展示规则由已批准 R27 取代，正式 raw 范围不变。
- Initial State 三字段可分别为空，选择器必须区分浏览、单项 override 和完整 preset 确认。
- 现有裸 Pitch Audition 只接收 pitch/velocity，不能直接当作满足本轮干净 preset 试听契约的实现。

## 1. 已接入的产品入口

1. MIDI Segment 与 SubVoice：下栏 `Vel. → Inst. → 原事件页`；Logical Segment 不出现 Inst.。Inst. 使用固定 y 的点、数值／Catalog 标签、独立 Snap；Draw 单击或 `Add Instrument…` 打开选择器，拖动不连续创建弹窗。
2. 点的双击、右键 `Properties…`／Ctrl+P 复用同一选择器；提交后选中新点，关闭后焦点返回 Inst.，A 切换本事件编辑区的 Snap。
3. Event Instrument `Configurations` 和 SubVoice `Initial State` 提供统一 Instrument 行。高级区仍保留三字段独立数值／继承；只修改一项不固定其余继承值，选完整 preset 才显式覆盖三项。
4. Bank／Program 列表只用现有 Catalog resolver；未知 preset 保留数值输入。不扫描 SF2，不读取 sample。所有相关 Program UI（含 raw Properties、多选 Properties、Initial State、List）统一为 0～127。
5. 自动试听默认启用、Key 60、Velocity 100、Gate 500 ms；这四项保存为程序偏好。手按键盘使用自身 Key／Velocity，释放结束 gate；Melodic/Percussion 仅影响试听。
6. 新建／编辑／取消／关窗使用明确的试听 owner 和停止完成确认；没有 SoundFont 不阻止合法提交，也不停止普通项目播放。

### 本轮未开放的新入口

包装的 Copy/Cut/Paste/Delete、批量水平移动／Ctrl 复制拖动、Flip、Scale、Quantize、多选 Properties、List 合并行及完整 Lane Tabs 仍属于 A2b。Inst. 不把这些快捷键误作用到隐藏的 Note 选择。既有 raw Lane 和父 owner 的合法编辑、复制、Split、Undo/Redo 不因该切片而禁用。

## 2. 跨层实现定位

| 任务 | 主要实现／证据 |
|---|---|
| T-IN-01/02 | Domain `InstrumentChanges.cs`、`InstrumentChangeResolver.cs`；Application `InstrumentChangeMaintenance.cs`、`ProjectTimelineOwnerChangeSetBuilder.cs`、`SequentialProjectEditCommand.cs` |
| T-IN-02/07 | `ProjectInstrumentSelectionCommands.cs`、`ProjectInstrumentChangeOrdering.cs`；原始值仍在原有分页事件集合中。顺序超出 Int64 可加余量时，只对同 Tick 原 formal order 做保序稠密重编号 |
| 父对象维护 | `InstrumentChangeCopies.cs`、`BoundedClipboardStructure.cs`、MIDI/Template clipboard 与 owner clone/Split 路径；按成员新 ID remap，不能按 coincident Tick 重新发现包装 |
| T-IN-03 | `PersistenceContractV4`、`ManifestCodecV4`、`manifest-v4.schema.json`、`midora-instrument-changes-v1.proto`、`InstrumentChangesProtobufCodecV1`、`MidoraProjectPackageV1` |
| T-IN-04/05 | `InstrumentSelectionDialog`、`MainWindow.InstrumentSelection.cs`、`ObjectPropertiesProjection/BatchSummary`、`PresentationModels` |
| T-IN-06 | `InstrumentPresetPreviewCompiler`、`PlaybackController`、`ApplicationTaskCoordinator`、`DesktopSessionController`、`InstrumentPresetAuditionSession` |
| T-IN-07 | `InstrumentChangeLane`、`InstrumentChangeProjection`、MainWindow 的两宿主页签与焦点入口 |

普通有界 raw 编辑用成员反向索引验证实际修改的关联。若连续子命令临时错开成员，只保留明确的待复核关联，在最终事务状态恢复或解组；该暂存回执不序列化，不从 raw 自动推导。没有精确回执的旧原位路径仍有保守安全检查：只读已有包装所引用的 2/3 个成员，不枚举整条 raw 轨道。值域、精确碰撞、source publication gate、原子失败和 Undo 继续复用原有正式命令机制。

Inst. 单点选择按不可变关联根保留弱引用书签，撤销／重做创建或解组时可恢复单点选择；书签不强持有旧根／raw 对象、不持久化、不修改 Project。Properties 打开时重新读取现行成员及 Tick，不能因后台显示索引暂未刷新而把旧 Tick 写回。

## 3. 兼容性与文件边界

- 当前新 writer 为 **Project Format 4**，应用 SemVer 仍为 `1.0.0-dev`。Format 1/2/3 独立 reader/schema/golden 保留；旧项目不凭 raw 内容生成关联。
- 新增唯一必需 `settings/instrument-changes.pb`，使用独立 Edition 2024 schema 1；空关联也写版本头。只存 wrapper ID、owner ID、种类及成员 ID，不重复 Tick 或音乐值。
- 失效 owner／成员、重复／未知字段、缺字段、错位／错误 target、全局 ID 冲突仍是严格 source 错误，不采取 presentation 的丢弃回退。
- 保存关联流式处理，不建立全量 protobuf repeated 对象图；无关联的巨型 MIDI owner 不创建 raw 查询快照。
- 旧项目原路径保存继续先确认、保留可见永久逐字节副本、原子替换；Save Copy 不修改受保护来源。旧版软件不能打开 Format 4，请保留自动备份。
- 新 descriptor SHA-256：`8e9cf959c566adf8c8ff4f1cd1580645bf76bf0f0d276861e1e4a5ecbc1182eb`。新 schema/golden 测试独立，未改写旧 descriptor。
- 冻结 Format 3 小样包 SHA-256 仍为 `44eef7db01bab5d1ad57a94916a2ff362b4c8486ec56726606adaa8d72405e99`，本轮重建其原字节并成功重开；不能把当前 writer 测试期望升级误作修改旧格式基线。

## 4. 性能与资源边界

| 资源 | 当前实现／释放 |
|---|---|
| raw 内容 | 沿用原有不可变分页源与有界编辑；包装不复制音乐对象 |
| source 关联 | 两个不可变树索引，按显式包装数量增长；快照共享根，点改只复制树路径。它不是磁盘分页 source；A2b 大规模包装工作仍须验证其预算，不能由本轮 20,000 包装测试推断百万包装内存结论 |
| Inst. 显示索引 | 后台、可取消、有界排序；working 4 MiB、resident 4 MiB，超出则使用既有 spill 机制。raw revision/root 变化后重建该包装索引，不扫描未关联 raw |
| 画面 | 每设备列至多一个常规点，返回上限 16,384 条；每帧最多 128 个标签，单标签最多 240 DIP；没有每事件一个 WPF Control |
| 在途请求 | 一个当前读取和一个可替换 pending；旧读取取消。隐藏／卸载停止读取并释放索引和来源快照，旧结果不恢复旧选择 |
| 选择器 | Bank 地址集合最多 128×128，虚拟化行的名称按需解析；每个已选 Bank 只建立 128 个 Program 项，不展开全部三元组 |
| 试听 | 单串行生产者、一个 pending、一个 owner；停止／关闭是不能被换选覆盖的确认屏障。既有后端的 frozen SoundFonts、preset 预载、voices、Master→Limiter 与 stream 生命周期继续复用 |

### 9KX2 实测

源码基线起点 `7b4866d0d3b511b2389f5984d191c7a33a1e3747`；SDK 10.0.400 / .NET 10.0.11，Windows x64。只读样本 `D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`，导入 17,999,999 个音符，选 MIDI Out #23，于 Tick 168960 创建并撤销音色变更，重复 5 次。

| 次数 | 准备 | 发布 | Undo | 本操作累计托管分配 |
|---|---:|---:|---:|---:|
| 首次 | 180.88 ms | 0.01 ms | 0.01 ms | 57.99 MiB |
| 第二次 | 86.23 ms | <0.01 ms | <0.01 ms | 45.14 MiB |
| 第三次 | 79.56 ms | 0.01 ms | <0.01 ms | 45.05 MiB |
| 第四次 | 92.18 ms | 0.01 ms | <0.01 ms | 45.42 MiB |
| 第五次 | 81.83 ms | <0.01 ms | <0.01 ms | 45.14 MiB |

导入耗时 73.261 秒；记录到的 GC heap 为 123.68～177.32 MiB，测试进程峰值 Working Set 259.35 MiB。准备中包含同 Tick 原事件保序重排和既有分页事务开销；累计分配不等于常驻内存。探针每 500 ms 检查内存，private bytes 达到 8 GiB 即取消，最长 8 分钟。本次未触发警戒。

这是 **Application 数据层／无实际 WPF、无音频 Worker** 的测量，不是完整 UI 进程树内存、渲染帧率或延迟 p99，也不是对上一版的性能提升百分比。未进行前后同机统计基线；5 次不足以估计 p95/p99。本次可验证“没有把 1,800 万 raw 内容物化成第二份对象图”，不能据此声明 T-IN-10 全部性能门通过。真实测量完成后又补了关联局部复核；这个样本初始无包装，未用于衡量该补丁对海量既有包装的收益。

## 5. 自动验证记录

报告在 `artifacts/test-results/a2a/`，构建输出单独在 `artifacts/build/a2a/`；未生成 dist。执行形式：

```powershell
dotnet test <test-project.csproj> --artifacts-path artifacts/build/a2a --filter <listed suite> --logger 'trx;LogFileName=<report>.trx' --results-directory artifacts/test-results/a2a --verbosity quiet
```

| 实际运行 | 最新结果／报告 |
|---|---|
| 新关联命令、order 极值、成员部分／完整修改、Split、父 clipboard ID remap、Undo/Redo、Catalog source 隔离、投影边界 | 16/16，`a2a-associations-final.trx` |
| Application 相关原有 clipboard／事件乐器编辑／偏好／旧格式打开／批量编辑重开回归 | 119/119，`a2a-command-regression.trx`（与上一行有重复，不累加冒充独立总数） |
| Persistence 全套，含 Format 4 strict component、deterministic round-trip、旧 reader/golden、保存故障与取消、验收包构造 | 239/239，`a2a-persistence-complete.trx` |
| Playback 全套，含 preset owner、500ms canonical Gate、held release、无 SF 后编辑锁释放、不误停普通播放 | 158/158，`a2a-playback-final.trx`；后端为自动测试替身，不代表真机听感 |
| WPF 专项、A1 两种焦点导航回归、试听请求队列 | 6/6，`a2a-wpf-final.trx`；一项包含多个真实 BAML/布局/模态子断言 |
| 实际 9KX2 探针与关联专项／单实例队列隔离复测 | 15/15，`a2a-real-midi.trx`；上述 5 次性能记录来自该显式开启的样本测试 |
| 全套 Desktop | 454/454，`a2a-desktop-release-gate.trx`（Debug 测试程序集；文件名不是 Release 配置声明） |
| 收口补丁：单点选择书签、嵌入字体、Program List、模态及队列复测 | 29/29，`a2a-desktop-last-changes.trx`；已包含在随后全套 Desktop 中 |
| 小型人工验收包生成＋两份 package 重开 | 1/1，`a2a-acceptance-fixtures.trx`；产物位于 `artifacts/uat/a2a/` |

最终桌面 **Release 构建** 另行通过：`dotnet build src/midora-desktop/Midora.Desktop/Midora.Desktop.csproj --configuration Release --no-restore --artifacts-path artifacts/build/a2a --nologo --verbosity quiet`，0 Warning / 0 Error，30.80 秒。它是构建，不是发布，没有改写 dist。

WPF 证据包括：真实主题/BAML 构造、730×650 窄内容布局、100%/125%/150%/200% 受控 DPI 的按钮可达与列表高度、长名称、独立继承状态、程序项 127、隐藏屏幕外实际模态窗口的 Enter/Esc 提交与取消。未操控用户桌面，不是四台真实显示器的人工视觉评审。

试听队列以受控 target 测试 Preparing 阻塞时 127 次快速替换只留下最新请求、关闭清除当前 owner、停止确认不被新 Play 覆盖、停止异常向调用方报告且可重试。普通播放不被抢停由 Playback controller 替身测试覆盖；设备丢失、真实预载失败、连续真实音频资源与热线程分配未在本轮重复测量。

### 测试过程中遇到的问题与处理

- 当前 writer 已升级，若干原测试仍断言 Format 3；仅修正“当前 writer”断言／降级 fixture 过滤，旧版本黄金字节保留。
- SubVoice 增加 Inst. 后 Event 下栏索引从 1 变成 2；补齐旧测试的三页夹具，同时实测 queued refresh／Add Event 焦点。
- A1 隐藏 popup 测试原先假设系统鼠标左键未按下；后台执行时读到了实际 Pressed。测试现在分别验证“悬停抑制”和“真实按住允许滚动”，未修改公用控件行为或操控鼠标。
- 完整 Desktop 首次 449/452 通过：新 Inst. 备用字体误用系统字体，已改成共享嵌入字体；List 测试旧 Program 1 基断言已更新；跨表面选择测试读了操作者实时 Ctrl/Shift，隔离复测通过后将该“无修饰键 Replace”测试输入固定。两轮完整复测分别 452/452、追加选择书签后 454/454；未改产品选择手势。
- 初次 Application 全套运行未完成，主动终止了本轮自己的 testhost：当时 1176 通过、11 失败；10 个为旧当前格式断言，1 个为单实例队列 timeout，分别定向复测通过。其报告 `a2a-application-full.trx` 保留，**不写成全套 Application 通过**。后续采用上述受影响范围回归。
- 开发方自动验证未重建 Native AOT Worker，未执行真实 SF2/SFZ 发声、全 UI 帧时序/GC/ETW、百万显式包装／所有操作的全组合压力测量。用户后续整体确认通过覆盖本轮人工清单，不将这些未跑的专项改写为已完成。

## 6. 人工验收（只需本轮 6 项）

2026-09-14 用户整体确认本轮验收通过；下列 6 项统一记录为通过，依据与原答见 §8，不虚构逐项测量或用户没有提供的环境细节。检查步骤保留，便于后续回归；旧项目原路径保存会自动提出 Format 4 升级确认及永久旧版副本位置。

已提供 [当前 Format 4 样例](../artifacts/uat/a2a/A2a-Instrument-Changes.midora)、[旧 Format 3 样例](../artifacts/uat/a2a/A2a-Legacy-Format-3.midora)及[样例说明](../artifacts/uat/a2a/README.md)。二者均已自动重开验证，产物在 gitignore 的 artifacts 内、不随 Git 提交。MIDI 轨道 `MIDI` 的 Inst. 在 Tick 120，事件乐器 `Instrument` → `Voice` 的 Inst. 在 Tick 240；旧样例没有包装，另有独立 MSB／LSB／Program。试听需选择程序中实际配置的可用 preset；样例不携带 SoundFont。

| ID | 检查动作 | 预期 |
|---|---|---|
| UAT-A2a-IN-01 | MIDI Segment、SubVoice 下栏分别打开 Inst.；Draw 单击或 Add，切换 Bank/Program，改数值，先取消再确认 | 列表/数值同步，0/127 与未知数值有效；取消无点，确认一个点。Logical Segment 不增加 Inst. |
| UAT-A2a-IN-02 | 双击点／右键 Properties；缩窄窗口、检查长名称和键盘；确认后不点别处直接按 A/D/S/E，再 Undo/Redo、保存重开 | 控件可达、焦点正确、A 控制事件区；包装及成员一次撤销/恢复，重开保留 |
| UAT-A2a-IN-03 | Configurations 与 SubVoice Initial State：只改一个 override，再选完整 preset；另一次 Esc | 独立继承保留；完整选择才覆盖三字段，Esc 零提交，不产生 Tick 0 raw 点 |
| UAT-A2a-IN-04 | SubVoice 的旧 raw Bank/Program Lane：仅 MSB/仅 LSB/独立 Program 与对应 Mapping，修改后 Undo；也可部分移走一个包装成员 | 原入口和 Mapping 仍在；仅值改保留包装，结构不完整则解组但 raw 留下，Undo 恢复 |
| UAT-A2a-IN-05 | 配置实际 SF 后自动换选、快速连续换选、手按琴键并松开；切换仅试听的 Melodic/Percussion | 默认 Gate 500ms；最后一次音色生效；释放结束 gate，模式不改项目，音量/安全链正常 |
| UAT-A2a-IN-06 | 试听未结束就确认/取消/关窗并马上编辑；禁用全部 SF 再选音色；普通项目播放时使用仍可进入的入口 | 停止自身试听后完成动作，无卡音/二次点击要求；无 SF 仍能编辑且提示不能试听，不抢停普通播放 |

## 7. 后续界线

- A2a 已验收通过，下一切片为 A2b，与 R28 Lane Tabs 合并验收；新增包装全编辑／List 汇总／百万包装预算仍按原计划执行，不擅自缩小矩阵。本次不启动 A2b。
- 原位兼容路径仍会按已有包装个数进行安全复核；新有界 planner 使用 affected-member 回执。海量显式包装的 source 元数据和旧路径性能需要在 A2b 强化，不能用“raw 已分页”代替这项验证。
- 未跑门已逐项注明；UAT-A2a 通过不代表 R27 全部完成。后续更新保留本轮的实际测量口径和失败记录。

## 8. 人工验收归档（2026-09-14）

用户原答：

> 验收通过，记录并提交、推送。

| 检查 ID | 结果 |
|---|---|
| UAT-A2a-IN-01 | 通过（用户整体确认） |
| UAT-A2a-IN-02 | 通过（用户整体确认） |
| UAT-A2a-IN-03 | 通过（用户整体确认） |
| UAT-A2a-IN-04 | 通过（用户整体确认） |
| UAT-A2a-IN-05 | 通过（用户整体确认） |
| UAT-A2a-IN-06 | 通过（用户整体确认） |

本次只同步验收状态并提交／推送既有 A2a 成果，没有继续修改产品实现、没有重跑产品测试，也没有本地发布。自动证据沿用 §5 的实际报告；原生故障注入、百万包装资源门等缺测保持明确，不因人工通过自动关闭。下一阶段仍为 A2b，待用户另行要求实施。
