# 阶段 8：11 GiB 峰值与全链路内存调查

日期：2026-09-08。调查基线：`11074bdee3bbaa60b473b435a92016d2d1aad999`。

状态：**调查、实测与解决方案，尚未实施修复。** 本文不修改 SRS，不把建议提升为已批准需求。本轮未修改产品源码，未提交、推送、本地发布或使用 computer-use；仅创建本报告及 `.tmp` 内的独立诊断探针、测试副本和结果。

## 1. 结论

阶段 8 原报告的 11,621 MiB 峰值，本次在同类 18M 编辑→保存→保存副本→重开流程中复现为 **11,688.68 MiB，即 11.42 GiB Working Set**。它不是洋葱皮位图本身的必要成本，也不是有限内存编辑必须付出的取舍。

主要根因已经定位：

1. **Pure MIDI 的只读对象枚举会填充三个没有容量上限的源记录索引。** 保存前校验和项目打开均走这条路径。18M 音符的三个表各达到 17,999,999 项；一次原本应流式的读取，把分页内容变成数 GiB 常驻托管索引。
2. **保存自校验会重开第二份 Project，并重复填充同样的索引。** 原项目与验证项目同时存活，再叠加连续 Save Copy/Open 的瞬态垃圾和 CLR 已提交内存，形成约 11.4 GiB 峰值。自校验本身有价值，应保留；问题是它使用的读取路径不再有界。
3. **另有真实的普通钢琴卷帘关闭泄漏。** 共享 EditorSettings 的匿名事件订阅保留已关闭 Workspace、Snapshot 和旧 Project。此项不是原无 WPF 测试的原因，但会让真实软件长期使用时保留前两项产生的大对象图。

此外，确认或识别了范围计划缓存、Opaque payload、分页写入 builder、其他类型持久化、中间对象和 UI 在途订阅等预算缺口。下文共列 **13 项**，区分实测问题与尚未测出峰值的结构性风险。

**不建议以清空 Undo、减少可见音符、降低预览质量、关闭严格验证、主动频繁 GC 或改回逐对象低速编辑来解决。** 优先修复所有权、只读访问与预算计账，保留已经验收的编辑速度、显示效果和语义。

## 2. 需求追踪、测量方法与边界

### 2.1 依据及非目标

- 需求依据：SRS 第 3 章的会话/生命周期，第 12 章的查询和编译资源约束，第 16 章的严格持久化与原子保存，第 18.11 节的洋葱皮/All Tracks，以及 INV-065/066/095/096/115/116。
- 输入：冻结修订的 Project、不可变分页源、选择与历史、当前只读显示和持久化流程。
- 本轮输出：峰值/存活量、容器数量、引用链、复现方法及修复建议；不是新的 Project 数据或 canonical 结果。
- 所有权：缓存、查询索引、UI 快照、历史和临时文件属于运行时；不能通过修改音乐数据、Modified 或 Undo 语义修复生命周期问题。
- 失败边界：测试只读取原 MIDI，保存至独立临时路径；不得覆盖用户项目。后续方案仍需保持取消原子性、旧修订不发布、保存失败不改变来源、严格损坏检测。
- 非目标：本轮没有改音频、文件格式、项目内容、用户工作流或已经验收的渲染行为；没有运行 SF2/设备播放测试。

### 2.2 环境与指标

- Windows x64，.NET SDK 10.0.400 / Runtime 10.0.11；机器 32 GiB RAM、12 个逻辑处理器。大文件探针为 Workstation GC。
- 样本：`D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`，144,175,201 bytes；40 个 Pure MIDI Track，17,999,999 个源 Note。历史报告中的 18,000,000 是编译后 NoteOn 数，不能与源 Note 数混用。
- 大文件、WPF、persistence-sizeprobe 探针引用已有 Stage8 Release DLL；`.tmp/domain-memory-audit` 使用当前源码的 Release ProjectReference 构建。没有产品源码补丁。WPF 测试实际加载 App 主题、STA Dispatcher、MainWindow/TimelineSurface，并离屏渲染；没有打开可见窗口、使用 computer-use 或注入鼠标键盘操作。
- 大文件进程每 100 ms 采样；记录 Working Set、Private Bytes、系统记录的 Peak Working Set、托管占用、GC heap/fragmentation/committed、累计分配、各代 GC 次数。另使用既有持久化故障注入检查点作只读观测，并统计私有索引数量。
- 大文件设置 14 GiB Private Bytes 诊断保护阈值；本轮未触发。大型测试串行执行，小探针限制规模，未尝试耗尽机器内存。

本文统一区分：

| 指标 | 含义 | 不能据此推断的内容 |
| --- | --- | --- |
| Working Set / WS | 当前驻留物理内存，包括托管、native、共享映射等 | 不能等同于存活托管对象或泄漏 |
| Private Bytes | 进程私有已提交虚拟内存 | 不能等同于当前物理驻留量 |
| Managed，未强制 GC | 测点托管分配占用，可能含尚未回收的垃圾 | 不能全部算成长期持有 |
| Post-GC managed | 在受控所有者仍存活/已释放条件下，强制回收后的托管占用 | 仍需区分合法缓存、诊断根和真实泄漏 |
| 累计分配 | 整段操作一共分配过多少字节 | 不是峰值，更不是常驻量 |
| 容器容量 / 像素预算 | 特定层拥有的内存估算 | 不是整个进程的资源上限 |

强制 GC 只用于归因，不是拟实施的产品修复；加入 GC 的流程不能与自然运行比较耗时。初版诊断 harness 在 async continuation 尚未完全退栈时做释放后 GC，可能继续持有局部变量；这些“释放后仍活”的数据**不作为泄漏证据**。后续独立进程改为同步外层、NoInlining 测试函数完全返回、WeakReference 和 GC 对照，已消除该测量误判。

没有采集完整进程 heap dump、GPU/native allocation stack，也没有把每一个峰值字节按类型精确分摊。下文容器计数与解除单一引用后的对照能证明相应根因，但不能声称穷尽所有潜在内存问题。

## 3. 18M 原流程复现与拆分

原记录见 [阶段 8 验证报告 §6](Midora-Stage8-Onion-Validation-Report.md)。原测试包含 60,000 Note 编辑、Undo/Redo、取消、presentation、Save、Save Copy、Open；未创建洋葱皮位图或 Compiled 显示索引。

### 3.1 自然 GC 的完整流程

本轮同类流程没有在工作阶段主动 GC。主探针的后台编译设置长 debounce，使诊断聚焦编辑/持久化，不把后台完整编译混入结果。

| 检查点 | 自启动秒数 | WS MiB | Private MiB | Managed MiB | 累计分配 MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| 导入返回 | 22.00 | 341.91 | 338.84 | 311.79 | 433.59 |
| 两次编辑与历史检查完成，开始保存 | 25.94 | 616.29 | 618.14 | 584.78 | 1,014.71 |
| 保存预检结束，即将写内容 | 85.25 | 4,694.65 | 4,838.68 | 3,810.13 | 10,616.09 |
| ZIP 完成，即将自校验 | 89.74 | 4,709.21 | 4,849.36 | 3,835.70 | 10,644.69 |
| 自校验结束，即将发布 | 140.89 | 9,154.29 | 9,477.05 | 8,233.09 | 20,315.21 |
| Save Copy 预检结束 | 165.31 | 10,492.89 | 10,852.05 | 10,607.38 | 25,852.00 |
| Save Copy 自校验期间的采样峰值 | 184.83 | 11,688.45 | 12,086.30 | 11,838.94 | — |
| Save Copy 返回 | 231.61 | 11,253.09 | 11,529.63 | 9,699.99 | 35,550.52 |
| 再次 Open 返回 | 295.23 | 10,987.58 | 11,232.52 | 8,292.42 | 45,221.99 |

系统记录的整个进程 Peak WS 为 **11,688.68 MiB / 11.42 GiB**，略高于 100 ms 采样点。

本次 Save 115.01 s，其中保存预检约 59.31 s、内容写入约 3.03 s、ZIP 约 1.45 s、自校验约 51.16 s；Save Copy 90.66 s，Open 63.61 s。这是当前机器的一次诊断测量，不是跨机器基准。它说明本样本不能把全部时间和内存增长归因于 ZIP 或磁盘文件复制。

**关键计数：** 导入、编辑和历史检查后，三个源索引各为 0；第一次 `BeforeContentWrite` 时，三个索引各为 **17,999,999**。重开的 Project 中同样各为 17,999,999。保存/重开后的源 Note 数仍为 17,999,999，Track 数 40，损坏 Track 数 0。

### 3.2 三个独立对照

| 独立测试 | 主要观测 | 能证明什么 |
| --- | --- | --- |
| 不编辑，导入后直接 Save | 导入后 post-GC 104.99 MiB；保存预检后 3,659.02 MiB；自校验项目同时存活时 7,215.86 MiB；Save 返回并退栈后原项目仍活，3,633.13 MiB | 不需要 Undo、选择或洋葱皮，也会出现单份/双份大索引 |
| 新进程只 Open 上一份 `.midora` | Open 57.71 s；峰值 WS 4,739.06 MiB；Project 存活时 post-GC **3,637.77 MiB**；三表各 17,999,999 | 打开本身就会取消分页在驻留内存方面的优势 |
| 导入后只通过 `QueryValues` 扫描全部 Note | 扫描 17,999,999 条用时 11.10 s；进程峰值 WS 589.54 MiB；扫描后 post-GC 176.89 MiB；三表始终为 0 | 同样读取全量源记录，并不必然建立常驻逐记录对象索引 |

最后一项只是读取/计数对照，**不是替代后的完整严格验证器，也不能据此宣布未来 Save 将变为 11 秒**。正式持久化需要完整 ID、字段、关联、范围及顺序验证；空间范围查询还不能直接代替正式 ordinal/wire 顺序游标。

独立 Open 的所有 Project 引用退出后，WeakReference 死亡、managed 降至约 **1.09 MiB**，但 WS 仍约 3.67 GiB。独立 Save 测试中 managed 最终约 **1.37 MiB**，WS 仍约 7.06 GiB，GC committed 约 7.27 GiB。后者说明 CLR/分配器高水位未立即归还 OS，**不能据此判定已关闭 Project 仍泄漏数 GiB**。这与第 4 节中真正存在的 WPF 事件订阅泄漏是两个不同现象。

## 4. 问题清单与根因

优先级为本报告的修复建议：P0 为首先处理的主要根因；P1 为可独立造成明显增长的缺口；P2 为生命周期/预算完整性及更长期的风险。它们不是已经批准的新需求。

本节源码定位使用项目名缩写：`Midora.Domain/`、`Midora.Application/`、`Midora.Compiler/`、`Midora.Playback/`、`Midora.Persistence/` 位于仓库 `src/midora-core/`；`Midora.Desktop/`、`Midora.Desktop.Presentation/` 位于 `src/midora-desktop/`。同段仅列文件名时沿用该项目目录；行号对应本次 HEAD。

### M01 / P0：只读 Pure MIDI 枚举建立全量常驻三表

**实测确认；11 GiB 的主要根因。**

源码链路：

```text
Save preflight / Open stable-ID validation
  → EnumeratePureMidiTrackIds
  → foreach (DirectMidiNote note in segment.Notes)
  → Materialize(sourceValue, retain: false)
  → source ID set + source value map + source ordinal map
```

- `Midora.Persistence/MidoraProjectPackageV1.cs:414,1092,2520`：Save 校验走对象枚举；`:833,2436`：Open 的全局 ID 校验也走同一链路。
- `Midora.Domain/PureMidiPagedContent.cs:2134,3059`：即使 `retain=false`，仍写 `_materializedSourceIds`、`_materializedSourceValues`、`_sourceIndices`；只是不长期保留 facade 对象本身。
- Channel Event 的 `:4515` 有同类路径；Opaque 的 `:5977` 保留 source value/ordinal，并在 facade 中复制 payload。不能只修 Note。
- `CloneTo:2791` 共享不可变表，不是深拷贝第二套，但会延长这些表的寿命。
- `PureMidiContentPackPersistenceV1.cs` 的非 pristine `WriteMergedPack` 也枚举对象：**只改保存前校验还不够。**

200k 不可变源微测：对象枚举后 post-GC 新增 41,689,168 bytes，约 **208.45 bytes/Note**；三个表各 200k，facade 常驻表为 0。100k / 400k / 1M 独立探针均呈相同线性趋势；value cursor 不填三表。18M 约 3.5 GiB 的单 Project 量级与大文件实测一致，精确容量随集合扩容和其他对象变化。

**方案：** 正式只读消费全部改走不改变 collection 状态的 value/page/ID/ordinal cursor；编辑 facade 与只读访问分离。编辑前值只为 dirty IDs 持有，未编辑值从不可变源恢复。不能简单删掉 `Materialize` 中的 bookkeeping 而不检查 setter、身份、删除、Undo、导入重复与覆盖层语义。

必须审计：保存校验、打开 ID 校验、merged writer、source clone、Selection/Clipboard、属性准备、编译/诊断等所有读取入口。保持严格自校验，而不是取消它来隐藏第二份索引。

### M02 / P0：共享 EditorSettings 保留已关闭 Workspace/Project

**实际 WPF 对象与解除单一引用的对照确认。**

引用链：`DesktopSessionController → shared EditorSettings → PropertyChanged delegate → TimelineWorkspaceViewModel → Snapshot/旧 Project`。

- `Midora.Desktop/DesktopSessionController.cs:349,2177,2190` 创建/传递长寿命设置。
- `Midora.Desktop/PresentationModels.cs:1038` 以捕获 VM 的匿名 lambda 订阅；`:466` 关闭清理未退订。
- `DesktopSessionController.cs:2252` 关闭 Tab 只取消后台、移出集合，不解除该设置订阅。

同 Segment 开关 12 次：handler 从 1 增至 12；12 个旧 VM 和 12 个 Snapshot **全部存活**。关闭 Project 后 session.Project 已为 null，24 个旧对象和 Project 仍活。仅在探针中退订这些 handler 后，旧对象和 Project 全部被回收。

**方案：** 具名 handler + 幂等的永久 Close/Dispose；Tab 切换/临时 Unloaded 与永久关闭分离。关闭应退订共享设置、取消后台消费者、释放来源和索引 lease。不要在普通切换 Tab 时破坏保持宽度、滚动、stale 图像等功能。

这是普通 Timeline 生命周期问题，不应归咎为“Onion 复制了每条音符”；但它会把 M01 污染后的大项目跨关闭留住。

### M03 / P2：MainWindow 最近命令 Surface 是未清理强引用

**实际 MainWindow 离屏测试确认。**

- `Midora.Desktop/MainWindow.xaml.cs:80` 的 `_lastTimelineCommandSurface`，在 `:1444,7483` 等路径更新，未发现关闭时置空。
- 记录真实 Surface 后关闭 Tab/Project、后台归零、清缓存并 GC，旧 Surface 仍活；仅把该字段清空后，Surface 死亡。Project/VM 还需解除 M02 的独立引用。

**方案：** 使用弱引用或明确在所属 Workspace 永久关闭/Project 替换时清空；执行菜单和恢复焦点前核验当前 Workspace 身份。它通常仅保留最后一个 Surface，不能描述成“每次必然累积一整棵视觉树”。

### M04 / P2：禁用 Onion 后仍保留上一份来源快照

**实际 TimelineSurface + 可控 payload 定位确认。**

- `Midora.Desktop.Presentation/Controls/TimelineSurface.Onion.cs:9,26` 的 DP 回调取消请求，但保留 `_onionSourceSnapshot/_onionDrawSnapshot`。
- `DrawOnion:40` 在 null/opacity=0 时提前返回，未到后面的替换逻辑。只有 `TimelineSurface.cs:838` 的 Unloaded 清理。

受控 source 仅额外持有 32 MiB payload：禁用、重绘、后台归零、清完成缓存、GC 后，source/snapshot/payload 仍活；真实 Unloaded 后释放。32 MiB 是实验定位载荷，**不是每个实际 Onion Snapshot 的固定大小**。

**方案：** 不再显示时取消并清除来源衍生引用；已完成纯 bitmap 继续按 LRU 保留，两者分开。模式/来源更新时释放旧 lease，但保留用户手选列表与已批准快捷模式持久化。

### M05 / P2：在途瓦片有上限，完成订阅却能无限追加

**受控慢 source + 实际 WPF 重绘确认。**

- `TimelineSurface.Onion.cs:87` 每次未命中重绘重新 Request。
- `Midora.Desktop.Presentation/Rendering/TimelineRasterCache.cs:2672,2727` 命中同一 pending work 仍追加 completion 和 cancellation registration，没有消费者去重。

始终只有 3 个 pending tile；额外重绘 1 / 50 / 200 帧后，完成订阅分别为 **9 / 159 / 759**。Unloaded 后正在执行的 source 与订阅保持到 source 退出，随后归零。这个测试证明预算缺口，不代表正常播放每帧都重绘内容。

**方案：** 按 `(tile key, consumer identity, request generation)` 去重；取消及时移除消费者；无消费者时撤销排队工作，执行中定期检查取消。计入 completion、registration、queued keys、factory/source 引用，不只限制 worker 数。

### M06 / P1：不同播放范围/采样率的计划缓存没有淘汰

**未访问音频设备的实际 ProjectCompilationSession 测试确认。**

- `Midora.Playback/ProjectCompilationSession.cs:32,825,881,958`：范围 canonical、sample plan、realtime plan 使用字典，没有条数或字节淘汰。影响编译的修改或重编译会清范围及计划缓存，sample-domain 失效、SoundFont 配置变化等路径另清计划缓存，Dispose 清除全部；不是所有编辑都清缓存。

1,000 Logical Notes，对不同 `[i,120000)` 范围准备 realtime plan：

| 状态 | Post-GC managed MiB |
| --- | ---: |
| 初始编译 | 6.07 |
| 10 个范围 | 41.92 |
| 50 个范围 | 185.23 |
| 100 个范围 | 364.39 |
| 另加 10 种采样率 plan | 372.65 |
| Session Dispose | 6.26 |

**方案：** 范围 view 共享不可变 canonical 存储；byte-accounted LRU，活动 reader/播放任务使用 pin/lease，淘汰非活动条目。只在 Preparing 等允许分配的阶段建计划，不在音频回调中加锁、分配或重算。缓存命中只能影响速度，不能影响状态恢复、排序、tick→sample 或可听结果。

这是长期多次跳转可能产生的独立增长；本次未修改或实测 native 音频性能。

### M07 / P1：Opaque 缓存与选择页按条数计账，漏算 payload

**96 条合法大 payload 的查询微测确认。**

- `Midora.Domain/PureMidiSourceIdResolutionCache.cs:15` 仅限制 65,536 IDs，Opaque match 在 `PureMidiPointQuerySnapshots.cs:454` 持有实际 payload。
- `Midora.Domain/PureMidiTimelineObjectSources.cs:92,420` 的 Opaque 页按记录数量建立；`Midora.Application/ProjectTimelineSelectionReader.cs:190` 仅计值结构大小，没有包含变长 payload。

96 条各 1 MiB 的 Opaque，单纯按 ID 读取后，缓存 96 项、payload 合计 **96 MiB**；post-GC managed 从 0.41 增至 96.43 MiB。即使“只有几十条事件”，也能突破按固定大小估算的 64 MiB 意图。没有进行理论极限的大 payload 分配。

**方案：** ID 缓存优先只存 ordinal/descriptor；payload 使用有 byte budget 的页/lease。分页同时受 record count 和 decoded bytes 限制；单条合法大事件需有明确的超页处理。选择、复制、属性与历史必须覆盖 payload 存活期，不能只计 struct，也不能多个缓存重复计费或都声称由对方拥有。

### M08 / P1：Logical/SubVoice/Conductor 持久化仍有全量中间对象

**源码确认；Logical/SubVoice 已做 100k 正式 codec 微测。不是 18M Pure MIDI 主因。**

- `Midora.Persistence/LogicalTrackProtobufCodecV1.cs:14,25,84,93` 建全量 DTO/RepeatedField；恢复再建全量 Domain。
- `EventInstrumentProtobufCodecV2.cs:15,35` 包装 V1 时经过 bytes→parse→再次 serialize，读回也有逆向往返。
- `StrictProtobufWireV1.cs:18` 的整段 bytes 和严格逐字段路径处理，以及 `Midora.Domain/PagedTimelineCollections.cs:537` 的 AddRange/ToArray，产生额外瞬态对象。
- `ConductorTrackCodecV1.cs:27`、StrictJson 的全量 JSON/DTO；package `:959,1927` 的非 mpk MemoryFiles/ReadEntry；保存自校验 `:540` 还会建立另一套用于比较。

| 孤立正式 codec 测试 | 100k Logical Notes | 100k SubVoice Notes |
| --- | ---: | ---: |
| 源对象图 post-GC | 20.633 MiB | 34.368 MiB |
| ToWire 新增常驻 DTO | 7.104 MiB | 7.866 MiB |
| Serialize 输出 bytes | 1,567,026 | 2,567,131 |
| Serialize 累计分配 / 用时 | 13.603 MiB / 0.101 s | 43.990 MiB / 0.416 s |
| Restore 累计分配 / 用时 | 117.580 MiB / 0.615 s | 463.380 MiB / 1.377 s |
| 源、输出 bytes、恢复对象同时存活 post-GC | 42.746 MiB | 71.143 MiB |
| 完整探针 Peak WS | 97.309 MiB | 149.461 MiB |

恢复记录数均为 100,000。SubVoice 用正式 V2 wrapper，ToWire 观察内部复用的 V1 DTO。**463.38 MiB 是累计分配，不是常驻或峰值。** 未进行百万 Conductor 完整 package 测量。

**方案：** 先去掉 V2/V1 多余 bytes 往返、逐字段成功路径的错误信息字符串构造、同时驻留的两套 MemoryFiles；随后把 Logical/SubVoice/Conductor codec 改为严格流式验证与逐页恢复。不能仅删除一两个 ToArray 就声称整条链已经有界。详见第 6 节。

### M09 / P1：多个小 Segment 的 PageBuilder 绕过编码预算

**每 Segment 仅 1 Note 即可实证。**

- `Midora.Domain/PureMidiContentPack.cs:103,212` 为每个 `(Segment, Kind)` 保留 builder；一个 Note 涉及 Note、NoteOnEndpoint、NoteOffEndpoint 三类。
- `:674,739` 每 builder 先建 64 KiB MemoryStream，endpoint builder 各租 16,384 × 56 bytes 数组。
- 满页通过 `EnsureRoom:855` 提前 Flush；未满尾页及 builder 缓冲保留至 `Complete:123` 统一处理。`:242` reservation 按实际记录数算，不按实际租用容量算。

| Segment / Note 数 | Builder 数 | Endpoint 数组容量 MiB | MemoryStream 容量 MiB | Post-GC 托管增量 MiB |
| --- | ---: | ---: | ---: | ---: |
| 1 | 3 | 1.750 | 0.188 | 2.073 |
| 10 | 30 | 17.500 | 1.875 | 19.522 |
| 50 | 150 | 87.500 | 9.375 | 97.077 |
| 100 | 300 | 175.000 | 18.750 | 194.021 |

上述检查点 `_pendingReservedBytes` 都为 0，尽管 encoder budget 为 64 MiB。完成后 endpoint 数组返回 ArrayPool；只要 writer 仍被持有，300 个 builder 和 MemoryStream buffer 仍存在。该探针峰值 WS 仅 52.23 MiB，大数组多数页面没有触碰；不能把 194 MiB 容量说成 194 MiB 物理驻留，也不能把返回池的数组直接叫泄漏。

**方案：** active/partial builder 一并计费；按需小容量增长，endpoint-only builder 不创建无用字节流；计账采用实际租用长度。很多未满尾页可转 owned staging spool。**不能未经验证直接提前 flush**：当前 Complete 按 SegmentId/Kind 写尾页，调整时间可能改变冻结的物理输出顺序，需保持确定性与 golden bytes。

### M10 / P1：稀疏 Stable ID 使去重位图出现巨大放大

**实际私有验证容器测试确认。**

`Midora.Persistence/MidoraProjectPackageV1.cs:2537` 的 `StableIdSetV1` 每个覆盖 2^20 ID 的 bucket 分配 128 KiB。1,000 个密集 ID 仅约 0.128 MiB；1,000 个间隔 2^20 的 ID 则建立 1,000 bucket，post-GC 增量约 **125.08 MiB**。最大 ID 仍只是 1,047,527,425，属于可表示范围。

18M 导入样本 ID 密集，该位图约数 MiB，**不是本次 11 GiB 主因**。但删除/导入/长期编辑形成稀疏身份时不能假定 ID 总是稠密。

**方案：** 自适应 sparse set→dense bitmap，或有限内存的分区/外部排序去重。保留完整 stable-ID 跨对象唯一性和错误定位；不能重排 ID、重编号或省略校验来省内存。阈值需按基数/密度及实际成本选择。

### M11 / P1：Logical/SubVoice 的首次快照、facade 与空间目录未完全有界

**部分实测，部分规模外推；与 Pure MIDI 三表问题不同。**

- 通过 AdoptSource 的 1M Logical/SubVoice 合成数据源，整个探针进程 post-GC managed 约 12.8 MB，clone/snapshot 只增加约 1 KB，确实共享；该总堆测量不是索引的精确类型大小。普通逐条枚举没有复现 Pure MIDI 每条约 208 bytes 的三表保留。
- 普通 AddRange 的全对象来源不同：100k Logical 首次 snapshot 前后 managed 约 20.64→33.39 MiB；SubVoice 约 34.37→50.18 MiB。`PagedTimelineCollections.cs:1475` 建整份 scalar sequence/FrozenDictionary，原对象 store 同时存在。
- `PagedTimelineSharedRoots.cs:438` 的 weak-facade 表只在后续注册时清理死项。100k source-backed Logical 的对象 OrderBy 后留下 100k 项，额外约 9.28 MB；`Midora.Compiler/MidoraCompiler.cs:921,1319` 正式编译存在这类对象排序，RawInstance.SourceNote (`:3987`) 可继续持有 facade。
- `PagedTimelineCollections.cs:2224`、`PagedTimelineSharedRoots.cs:75` 存在未纳入编辑数据页预算的空间顺序/fingerprint/leaf 目录。从合成来源的基线与 100k～1M 全堆测量观察，增量约 12.4 bytes/record；这些目录是源码确认的主要相关结构，但本轮没有按类型精确分摊。100M 的约 1.2 GB 是线性外推，**未测试 100M**。

**方案：** 三种编辑器继续统一值页模型；普通对象来源达到阈值后也采用不可变页/增量覆盖，避免首次快照保存双份全量表示。只读编译改值游标或一次有界排序；facade 用弱身份表并有维护上限；空间目录共享、分块或外置，纳入单独元数据预算。不能牺牲范围查询、选择命中和瓦片局部指纹速度换取表面小内存。

### M12 / P1：Logical 编译展开尚无完整字节准入/分阶段释放预算

**源码确认风险；本轮未测百万实例展开的真实峰值。**

`Midora.Compiler/MidoraCompiler.cs:167,347,896,1288` 等路径保留 RawInstance、RawSubVoice、RawMidiEvent、排序/合并及 canonical 数组。复杂模板 × 触发音符 × 循环次数有乘法放大；编辑页、Onion 索引的 16/32/64 MiB 预算都不覆盖它们。

**方案：** 先加入展开量与各阶段资源计账、可取消的准入检查，再考虑分块 canonical/共享缓存、稳定外部排序、阶段间及时释放。不能以跳过事件、改变 Loop/生命周期或 Full/Incremental 不一致换取容量。超过确实支持的资源上限应在发布前受控失败，而不是系统 OOM；新的用户可见限制需要另外确认。

### M13 / P2：History/Clipboard 缺少跨命令的总预算

**源码确认所有权缺口；不能把它直接说成当前每个命令保留 64 MiB RAM。**

- `Midora.Application/ProjectDocumentSession.cs:241,548` 历史持续追加，仅分支切断 redo 或 Dispose 清理，没有总量策略。
- `BulkEditPreparationContext.cs:30` 每命令独立 budget；并非会话总预算。
- 正常部分：`BoundedImmutableValueSource.cs:68` 发布前结果确实 spill；`:118` 读缓存为进程共享 64 MiB。取消/失败的 lease 有 finally 清理。

真实 100k 删除/历史探针中，函数内 Dispose 后仍见旧源；整个 NoInlining 调用返回后 managed 降约 0.40 MiB、旧源 WeakReference 死亡，**没有证明该路径关闭泄漏**。

**方案：** 统计跨 History、Clipboard、源页、query metadata 的独占/共享所有权，统一全局 RAM 与 owned spill 预算；优先将历史元数据分页并保留 Undo 能力。若未来必须裁剪历史或拒绝操作，需要用户/ADR 确认，不能本轮静默改工作流。无论是否裁剪，项目关闭、redo 分支丢弃和替换剪贴板均应及时释放不再拥有的数据。

## 5. 已确认正常、应保留的设计

| 机制 | 当前实际边界/测量 | 判断 |
| --- | --- | --- |
| 完成的 raster cache | 进程共享 256 MiB，不是每个 Tab 256 MiB | 有界速度缓存，不能为降 WS 一律清空 |
| Raster 工作 | 64 在途，普通后台最多 32；2～4 workers，普通后台同时执行最多 1 | 任务数有界；M05 补订阅/引用计账 |
| Onion 可见分组 | 单 Surface 估算 64 MiB，来源重分组只复制引用 | 不复制全体源音符；该预算不等于整个 source graph |
| Pure MIDI decoded cache | 同一导入/打开 Project 共享 64 MiB | 不是每 Track 或 OnionClip 各 64 MiB |
| 编辑 immutable value 读缓存 | 进程共享 64 MiB | 发布页 spill 的行为有效 |
| Logical Compiled 显示索引 | BuildGate=1，resident 16 MiB、working 32 MiB、spill 16 GiB | 目录/引用仍需补计账，不能当作整个编译器预算 |
| Compiled MIDI 显示 | 当前混合模式直接复用 Pure MIDI 源 | 不应重新为全部 MIDI 构建 FIFO 显示索引 |
| 旧成功 Logical 显示 | 新索引完整发布前保留旧索引，读者 lease 结束后释放 | 为避免空白和未完成数据必要，不能误当泄漏删除 |

WPF 对照：8 轮独立 AllTracks Raw/Compiled 模式及水平视口切换后关闭，Project/VM/View/Surface/Snapshot/source/canonical/index 共 64 个 WeakReference 全部死亡。完成位图保留 24 MiB/96 tiles，post-GC managed 约 4.8→4.9 MiB；清缓存后 pixel budget 归零，Private Bytes 未立即回到冷进程值不能据此判为泄漏，可能包括 CLR/native 分配器保留及 WPF 预热后的常驻资源，本轮未逐项分摊。另用真实 Controller 5 轮只开 All Tracks 再关项目，20 个弱引用全部死亡。

这些对照只证明相应 All Tracks 路径，不覆盖 M02 的普通 Segment Editor 订阅泄漏。

持久化中 Pure MIDI `.mpk` 并非整体放进 MemoryFiles；pristine 复制/校验使用流与有界 buffer。因而“将 pack 再复制一遍到 RAM”不是合理修复方向。保存保留原 Project 与 detached 自校验 Project、Undo 保留修改前版本、ArrayPool 保留可复用容量，都有正当用途；需要限制其额外状态，而不是取消用途。

## 6. 详细实施方案与顺序

以下是供下一轮批准的工程方案，不是本轮已经完成的改动。除涉及历史裁剪/新资源上限的选项外，优先选择不改音乐语义、不改用户操作、不升级文件格式的实现。

### A. 先恢复只读链路的有限内存属性（M01；复杂度中高）

1. 为 Note/Channel/Opaque 定义明确的只读值、stable-ID、正式 ordinal 游标，支持 immutable source + overlay + tombstone，不注册可编辑 facade。
2. 统一替换 package ID 校验、SupportedProject 校验、merged pack writer 的对象 foreach；检查未暴露 Segment 区域、导入重复记录、端点序列和排序稳定性。
3. 对剩余调用者分类：仅展示/序列化/查询用 value；确实需要修改时按命中 ID 获取编辑对象。三表只允许 dirty/显式编辑范围增长，不随一次完整只读扫描增长。
4. 维持整个 Save 事务：冻结来源→完整验证→staging→重开自校验→比较→原子发布；允许自校验 Project 存在，但它只能拥有有界读取缓存与必要元数据。
5. 增加容器计数和所有权诊断。先以本轮 18M 同工作流重测，再谈整体 RAM 目标；不能提前承诺“低于 1 GiB”。

主要风险：旧 facade 的 setter/身份契约、overlay ordinal 重建、碰撞规则、Untouched duplicate 保留。以功能对齐而非盲目删除表为原则。

### B. 统一永久关闭与暂时隐藏的生命周期（M02～M05；复杂度中）

1. Workspace 明确 `Suspend/Resume` 与幂等 `Close/Dispose`：前者保持用户视图状态，后者解除共享设置订阅、取消消费者、释放来源/索引 lease。
2. MainWindow 最近命令目标改弱引用/身份引用，关闭清理；继续保证快捷键归属与弹窗返回焦点。
3. Onion 禁用/来源清空及时释放 source-derived graph；保留纯 bitmap LRU 和用户配置。
4. pending tile 共享计算，消费者订阅去重、可独立取消；消费者归零取消排队，执行中扫描有取消检查。结果只向仍有效的 generation 发布。
5. 所有永久关闭路径做 WeakReference 测试；不要使用 `GC.Collect()` 作为产品关闭实现。

### C. 给所有遗漏层补 byte budget 与 owner（M06、M07、M09、M10；复杂度中高）

1. 范围 canonical/plan 实施 byte-accounted LRU 和活动 lease；底层不可变数据尽可能共享。分别统计条目独占字节与共享字节，避免每个 view 重复计账。
2. Opaque 使用 payload-aware 页和缓存，不在 ID cache 复制/固定保留大 payload。把当前按条数的 page capacity 变为双限制。
3. Writer 按实际数组长度计算所有 active/incomplete/pending builder；小页按需增长，不活跃尾页 spool，按冻结顺序输出。使用统一 owner 清理取消/失败/staging。
4. 稀疏 ID 去重采用自适应表示并有总预算；必要时外排，仍检测跨所有文件的重复身份。
5. 预算超出采用缓存淘汰/页 spill/等待后台容量，而不是静默漏对象。确需拒绝合法超大任务的硬限制应单独明确，不能藏在实现中。

这一步涉及并发、lease 和存储边界，正式实施前需 ADR 记录；音频活跃线程继续零托管分配、不能阻塞文件 IO。

### D. 逐步替换全量持久化中间表示（M08；完整版本复杂度高）

先做不改变 wire 的低风险部分：

- V2 直接复用 V1 的内部 DTO/Validate/FromWire，消除 bytes→Parse 的往返。
- 成功路径不构造每字段完整诊断字符串，错误发生时才格式化路径。
- 非 mpk 文件逐项 staging，用文件描述符和流比较，避免同时驻留两套 MemoryFiles。

再做真正有界的 codec：

- Writer 先冻结 value source，计长/验证，再按原 protobuf/JSON 规则流式输出；嵌套 length-delimited 用两遍或 owned spool，不建立全量 RepeatedField。
- Reader 用严格 streaming state machine，逐页建立 detached immutable source，完整成功后一次 adopt；禁止全量 DTO→AddRange→ToArray 重新物化。
- 保留未知/重复字段拒绝、presence、ID/关联、损坏隔离、取消、golden bytes、Format 1/2 迁移与 Format 3 写入规则。
- 以旧 codec 为小样本 oracle，输出逐字节比较；不能以放宽严格性获得速度。

时间取舍：必要的计长或外排会增加扫描/磁盘次数，但减少大量 GC、对象转换与重复 parse。实际收益需实测；优先保留已有 pack 流式捷径。

### E. 补齐 Logical 大数据与跨命令预算（M11～M13；复杂度高，分增量）

1. 补齐仍走普通对象/AddRange 的创建、恢复入口及长期小编辑积累路径，快照复用版本化目录，避免全对象 store 与完整 scalar 快照双份驻留。大型 Logical/SubVoice 粘贴、生成已经采用不可变分页 root（`BoundedClipboardNoteEdits.cs:103`、`BoundedClipboardStructure.cs:62`、`ProjectTimelineGenerationCommands.cs:55`），应复用现有实现，不能把这些已完成优化误判为尚未分页。
2. 编译用 value/ordinal，避免全对象 OrderBy 和通过 RawInstance 强留全部 facade；对 expanded Raw/canonical 和缓存建立独立预算与阶段释放计数。
3. 将空间目录、查询排序、历史元数据纳入计账。优先共享同修订目录和外置冷元数据，不重新引入线性查询、全 Segment 瓦片失效或首次 Undo 的平方级路径。
4. History/Clipboard 共享 source lease、跨命令汇总 RAM 与 spill；分支丢弃/关闭立即释放不再需要的 ownership。
5. 历史裁剪、磁盘硬上限后拒绝新命令等改变工作流的策略列为另行决策，不作为默认实施项。尽量先以有界元数据+持久 spill 保留现有 Undo 行为。

## 7. 后续修复必须通过的验证门

### 7.1 可量化的资源测试

| 测试 | 应断言的结果 |
| --- | --- |
| 1M/18M 导入、无编辑 Save、编辑 Save、Save Copy、Open | 所有只读 source 三表不随 N 增长；分阶段峰值和 post-GC 分开记录；保存/重开内容完全一致 |
| 同 Segment 开关 100 次，再跨 Project 开关 | 每份共享设置上，Target 为 TimelineWorkspaceViewModel 的 handler 数等于使用该设置的存活 Timeline Workspace 数；后台结束、调用栈退出后旧 Project/VM/Snapshot/Surface 弱引用死亡 |
| Onion 禁用/快捷模式/手选/透明度0/Tab切换 | 清来源时释放旧 source；临时隐藏不丢配置；stale index 按 lease 正确释放 |
| 固定 3 个慢 tile，重复请求 10,000 次 | completion/registration 不随帧数线性增长；取消后消费者清零，无旧 generation 发布 |
| 1,000 个不同范围、采样率、monitoring组合 | cache 在字节预算内达到平台；活动 plan 不被提前释放；不改 canonical/audio 语义 |
| 数十个 1～4 MiB Opaque | 查询/选择/剪贴板/属性/失败取消均计 payload；单条/单页/缓存预算可解释 |
| 1/100/1,000 小 Segment，同 pack | active builder + pending 按实际容量受限；输出 golden 顺序一致；取消清 owned staging |
| 密集/稀疏/极大 Stable ID | 内存随实际基数和预算受控，重复检测不丢失，不重编号 |
| Logical/SubVoice 100k/1M，普通来源与 adopted来源 | 首快照、编译、clone、恢复分别计源、DTO、facade、目录；不只看页数据64MiB |
| 长历史、反复复制替换、删除/Undo/Redo分支 | RAM、spill、metadata 所有者可追踪；无隐藏倍增；旧分支释放但有效Undo不丢 |

### 7.2 正确性与性能保护

- 三种钢琴卷帘全部覆盖：创建、删除、移动、Ctrl复制拖动、左右边界、碰撞、隐藏范围内容、Undo/Redo、Properties、所有批量编辑操作。不能只测同一种列表。
- 编译：Full/Incremental 完全等价，稳定排序/ID、同 tick、Loop/Pre-Roll/状态恢复/硬边界/来源追踪保持不变。
- 保存：原子替换、取消/磁盘满/校验失败、迁移副本保护、未知/重复/损坏、deterministic bytes、旧格式可读性不退化。
- UI：现有粗/细 Arrangement 预览、普通钢琴卷帘边框/选择高亮/编辑不闪烁、指纹局部失效不变；缓存淘汰只能增加后台重算，不应造成错误图像或交互。
- 时间性能：小项目冷启动、400k选择、60k移动/长度/Undo、18M冷范围查询分别比较；使用同机器交替运行基线/新实现，报告多次中位数与最慢值，而不是一次“热缓存最快”。
- 内存测试增加计数断言（source materialization、handlers、completions、cache bytes/leases），不能只靠可能波动的 WS 阈值。

本轮只做了调查探针和对照，没有运行修改后的上述全套回归；这些是下一轮修复的验证门，不是已经通过的交付声明。

## 8. 仍待确认的范围与不应承诺的内容

1. 已足够解释原 11 GiB 的主要增长链，但没有精确把峰值分摊成每一种 Dictionary node、GC 空闲段、WPF/native bitmap 等字节。不能给出虚假的精确总和。
2. 真正可见窗口的长时间人工编辑、多个大 Logical 模板的实际编译峰值、百万 Conductor package、音频 Worker/SF2 原生内存、GPU/DWM 未在本轮完整实测。32 MiB controlled source 和慢 source 仅用于引用/预算定位。
3. 修完 M01/M02 预期有很大收益，但本轮没有实现，**没有“修复后内存/速度提升”结果**。value-scan 是可行性对照而非全流程优化版。
4. 目前仍不适合宣布“任意千万音符工程最多只需 X GiB RAM”。音符数不能代表 Opaque 字节、Logical 展开倍数、Track/Segment 数、历史和用户 SoundFont 原生采样容量。修复后应按典型工作负载给推荐配置，并单独写明确的各层预算。
5. 没有证据需要为了这批问题升级 Project Format、改变音乐行为或取消现有功能。新的硬资源限制/历史策略若会影响用户，应另行确认。

## 9. 证据与复现入口

本节证据路径相对于仓库；第 4 节源码项目缩写按该节约定解析。`.tmp` 已被忽略，不会作为产品源码提交。原 MIDI 未修改，大文件保存副本保留在测试目录供复核。

| 证据 | 路径 |
| --- | --- |
| 18M 分阶段探针 | `.tmp/stage8-memory-audit/Program.cs`、`Audit.csproj` |
| 自然完整流程 CSV | `.tmp/stage8-memory-audit/results/roundtrip-natural/memory.csv` |
| 独立 Open / Save / value scan CSV | 同 results 下 `open-isolated/`、`save-isolated/`、`value-scan/` |
| WPF 生命周期报告 | `.tmp/stage8-memory-audit-wpf/Findings.md` |
| WPF JSON | 该探针 `bin/Release/net10.0-windows/win-x64/` 下的 `measurements.json`、`main-window-measurements.json`、`subscription-measurements.json` |
| Domain/计划/opaque/历史审计 | `.tmp/domain-memory-audit/AUDIT.md`、`Program.cs` |
| 稀疏ID/builder/codec微测 | `.tmp/persistence-sizeprobe/Program.cs`、`SizeProbe.csproj` |

示例命令（每次选择新的输出目录；不要指向用户项目）：

```powershell
dotnet build .tmp/stage8-memory-audit/Audit.csproj -c Release --no-restore
dotnet .tmp/stage8-memory-audit/bin/Release/net10.0/Midora.Persistence.Tests.dll roundtrip "D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid" .tmp/stage8-memory-audit/results/recheck-roundtrip
dotnet .tmp/stage8-memory-audit/bin/Release/net10.0/Midora.Persistence.Tests.dll open .tmp/stage8-memory-audit/results/roundtrip-natural/copy.midora .tmp/stage8-memory-audit/results/recheck-open --gc
dotnet .tmp/stage8-memory-audit/bin/Release/net10.0/Midora.Persistence.Tests.dll save "D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid" .tmp/stage8-memory-audit/results/recheck-save --gc
dotnet .tmp/stage8-memory-audit/bin/Release/net10.0/Midora.Persistence.Tests.dll value-scan "D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid" .tmp/stage8-memory-audit/results/recheck-values --gc

dotnet .tmp/stage8-memory-audit-wpf/bin/Release/net10.0-windows/win-x64/WpfAudit.dll
dotnet .tmp/stage8-memory-audit-wpf/bin/Release/net10.0-windows/win-x64/WpfAudit.dll --main-window
dotnet .tmp/stage8-memory-audit-wpf/bin/Release/net10.0-windows/win-x64/WpfAudit.dll --subscriptions

dotnet run --no-build --project .tmp/domain-memory-audit/DomainMemoryAudit.csproj -c Release -- 1000000
dotnet run --no-build --project .tmp/domain-memory-audit/DomainMemoryAudit.csproj -c Release -- 1000 range
dotnet run --no-build --project .tmp/domain-memory-audit/DomainMemoryAudit.csproj -c Release -- 96 opaque
dotnet run --no-build --project .tmp/domain-memory-audit/DomainMemoryAudit.csproj -c Release -- 100000 history
dotnet run --no-build --project .tmp/domain-memory-audit/DomainMemoryAudit.csproj -c Release -- 100000 logical
dotnet run --no-build --project .tmp/domain-memory-audit/DomainMemoryAudit.csproj -c Release -- 100000 subvoice
dotnet run --no-build --project .tmp/domain-memory-audit/DomainMemoryAudit.csproj -c Release -- 1000000 logical-adopt
dotnet run --no-build --project .tmp/domain-memory-audit/DomainMemoryAudit.csproj -c Release -- 1000000 subvoice-adopt
dotnet run --no-build --project .tmp/domain-memory-audit/DomainMemoryAudit.csproj -c Release -- 100000 logical-adopt-ordered

dotnet run --project .tmp/persistence-sizeprobe/SizeProbe.csproj -c Release --no-restore
dotnet run --project .tmp/persistence-sizeprobe/SizeProbe.csproj -c Release --no-restore -- sparse-ids
dotnet run --project .tmp/persistence-sizeprobe/SizeProbe.csproj -c Release --no-restore -- builders
dotnet run --project .tmp/persistence-sizeprobe/SizeProbe.csproj -c Release --no-restore -- codec-logical
dotnet run --project .tmp/persistence-sizeprobe/SizeProbe.csproj -c Release --no-restore -- codec-subvoice
```

大文件四个流程、WPF 三个分支及所列微测均完成；强制 GC/反射计数/受控取消仅在独立诊断进程中发生。当前 probe 的最终清理已使用 NoInlining 外层退栈方案；原自然 roundtrip CSV 的工作阶段有效，其早期清理方式不能用于泄漏判断，独立 Open/Save 与 WPF 对照为生命周期结论的依据。
