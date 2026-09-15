# 内存优化阶段 2：UI 生命周期与瓦片订阅验证报告

日期：2026-09-08。状态：**阶段 2 工程门通过；用户已确认阶段 1＋2 的 A01～A08 粗略验收通过，All Tracks 焦点补修也已验收通过。用户另行授权本次提交、推送，不进入阶段 3，不本地发布。**

范围：[六阶段计划](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md) §7 的 M02～M05；实现协议见 [生命周期设计](Midora-Memory-Stage2-Lifecycle-Design.md)。不推进阶段 3，不修改音乐语义、音频、文件版本或图像精度。实施及验收补修期间未提交、推送；验收通过后用户另行授权本次 Git 交付，不发布 `dist`。

## 1. 修改清单与确认的根因

| 问题 | 根因与改动 |
| --- | --- |
| M02：关闭 Tab 后旧 Workspace 继续存活 | Timeline VM 向共享 EditorSettings 注册的匿名实例 handler 没有解除。改为具名订阅和幂等 Dispose；Controller 的关闭、对象删除和 Project 替换统一释放，同时清理诊断 scope、导航和来源工厂。 |
| M03：最后操作的旧 Surface 被窗口保留 | MainWindow 的最近命令 / Alt 焦点提示原来强持 Surface。现在只持弱引用，解析时复核 Workspace 实例、DataContext、活动状态和关闭状态；延迟焦点返回同样不强留旧对象。 |
| M04：不可见 Onion/All Tracks 仍留来源图 | 禁用、透明度 0、空来源和隐藏时释放不再绘制的源投影。设置、手选列表、Previous/Next 模式不丢失；All Tracks 隐藏保留契约要求的最后成功 Logical 索引供 stale 显示，关闭才释放。后台构建弱持 VM，进度每代最多一个排队通知。 |
| M05：完成订阅按重绘次数增长 | 原 PendingWork 每次 Request 追加回调。现在共享计算与消费者订阅分离，以 consumer/key/token-generation 去重；一个消费者离开不会取消其他消费者，最后一个离开才取消计算。已经排队的通知也能撤销、断开捕获图并 Abort。 |

### 隐藏与关闭不再混用

Suspend 保留视口、选区、侧栏宽度/滚动、Lane、Onion 配置；停止不可见消费者。Resume 重新接管当前修订。Dispose 解除订阅并清除快照、列表工厂、来源索引、后台委托。三种普通钢琴卷帘、Conductor 和 All Tracks 都覆盖；不把视觉树临时 Unloaded 当作永久关闭 Workspace。

选区预取、延迟 selection metrics、MIDI/SubVoice Lane discovery 的 Dispatcher 完成通知统一可撤销。关闭后的旧通知不能重新回填 snapshot，也不能改变新 Tab 的命令归属。

### 取消深入实际扫描

低缩放聚合此前没有接收 token，外层“取消任务”不能阻止其继续扫页。本轮将可选 token 沿 Presentation → Domain 树/页/overlay → Application bounded source 传递，节点/页边界及最多 256 条记录检查一次；未取消的列内容、work count 和最终像素保持不变。

这不是音频或领域语义重构，也不改变保存格式。单次不可分割 IO/解压不承诺抢断。合法的 256 MiB 完成位图 LRU 保持原预算；未加入产品级强制 GC，也不以清空缓存/History 换取内存数字。

## 2. 实测方法

- 基线为阶段 1 提交 `ff5c19c79e0f81c5be4aea83ab74a0ffa856d459` 工作区开始时的 Release 产物；修改前完整冻结到 `.tmp/memory-stage2/baseline/win-x64`。没有切换/覆盖工作区或重建 `dist`。
- 基线 `Midora.dll` SHA-256：`7AAF036628595E705922E5B9E04151DE522154AE0CAC43EB80816F8A3AA470CA`；Presentation：`60D1F4C9621212CEFD111871463F203D97DD635BB0D52B4517916F9D465EF4D8`。
- 阶段 2 原验收候选 `Midora.dll` SHA-256：`66E6619555EF97AD7BE213E39F8B68F22F0B9D522FFFEBE08CEA44FCC2FABFF1`；Presentation：`C1447E66E8E99B247526AE979562DAC1E23F5C92DC2F35581EA94ADCDB0C2F16`。对应以下性能探针使用的 DLL，不是已发布版本标识，也不包含 §7 的验收后焦点补修。
- 同一个 `eng/MemoryStage2Probe/Program.cs` 分别引用旧、新 DLL。真实 App 资源、MainWindow、BAML DataTemplate、绑定、TimelineSurface 和 1200×760 / 96 DPI 离屏 WPF 绘制；不显示窗口、不启动音频、不使用 computer-use。
- 与阶段 1 同机：Release/x64、SDK 10.0.400、.NET 10.0.11、Windows 10.0.26200、12 个逻辑处理器、Workstation GC。计时期间不并行运行其他测试/构建；不清系统文件缓存，新进程不等于磁盘冷缓存。
- 每轮分别打开/绘制/关闭 Logical Segment、MIDI Segment、SubVoice、Conductor、All Tracks。MainWindow、Controller 和共享 settings 保持存活；跨两个独立 Project。
- 长循环为每 Project 100 轮，即每种 Workspace 共 200 次；另以相同短流程交替串行运行三对用于计时。小样本用于 UI 模板与持有链，不把它当作密集音符渲染吞吐测试。
- 大样本只读导入 `D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`，选择音符最多的 MIDI Segment，每 Project 各开关三轮，跨两次独立导入；不写用户原件。
- 自然 managed/working/private 与受控 GC 后存活分别报告。领域/VM 局部变量放在 NoInlining helper 中，关闭 Project 并退栈后再检查弱引用，避免把探针自己的局部引用误算作泄漏。
- Snapshot 项是登记的弱引用条目数，不是去重实例数。旧版没有真实 RunningCount；不能把 InFlight=0 宣称为后台执行已经全部退出。
- 离屏批量探针不等于完整窗口焦点验收。另有不带 WS_VISIBLE 的真实 HwndSource 集成测试，验证 Loaded/Unloaded、同模板 Surface 复用、Properties/Batch 返回的逻辑焦点和关闭目标失效；前台键盘体验仍交人工验收 A。

初版大样本探针遗漏 DispatcherSynchronizationContext，导致 CollectionView 跨线程拒绝。已修正探针并重新运行；失败的 `baseline-large.log` 不计入产品结果。最终数据使用 `*-valid.log` / `paired-*.log` / `current-*.log`；早期 smoke 仅作诊断。

## 3. 持有链结果

每 Project 100 轮、两个 Project 完全关闭、控制 GC 后：

| 观察项 | 基线 | 阶段 2 |
| --- | ---: | ---: |
| 已关闭 Timeline Workspace 存活数 | 600 | 0 |
| 旧 Surface 存活数 | 1 | 0 |
| 旧 Snapshot 弱引用仍存活的登记数 | 1,200 | 0 |
| 旧 Project 存活数 | 2 | 0 |
| PianoRoll settings 剩余 Workspace handlers | 400 | 0 |
| Arrangement settings 剩余 Workspace handlers | 202 | 0 |

旧版另外两个 Arrangement owner 属于每个 Project 常驻 Tab，包含在 202 个 handler 中；并不是 600 个被逐轮记录的 Timeline Workspace 的一部分。新版本关闭后 Instrument / All Tracks 登记也都为 0。

基线自然采样随轮数增长；新版本未 GC 时仍可看到刚关闭的对象，GC 后归零。这是正常的回收时机差别，不能要求每次 Close 后立即看见 Working Set 降低。

### 3.1 短流程交替计时与长循环

三个独立旧/新进程对，顺序均为旧→新，每个进程两个 Project、每 Project 10 轮。以下是各进程所有完整模板首次构造/离屏绘制样本的统计，并非稳态帧耗时：

| 对次 | 总流程秒：旧→新 | 单次模板中位 ms：旧→新 | 单次模板 P95 ms：旧→新 |
| --- | ---: | ---: | ---: |
| 1 | 15.194→14.798 | 99.09→95.77 | 232.66→215.04 |
| 2 | 14.782→14.779 | 95.70→93.22 | 214.55→226.55 |
| 3 | 15.602→14.696 | 100.99→93.34 | 239.00→226.62 |

三次总流程中位数约快 2.7%，单次模板中位数的三次中位约快 5.8%，P95 的三次中位约快 2.6%。第二对 P95 约慢 5.6%，不能宣称每次每项都改善。这个小流程没有观察到一致的整体时间退化，但不证明所有真实手势都绝无变化。

100 轮长流程总耗时 82.774→78.778 秒，模板中位 61.90→60.27 ms、P95 122.97→119.90 ms；最大值 414.33→419.79 ms，不能将该混合首帧指标解释成连续操作的最坏延迟。关闭并 GC 后 managed 23.1→7.1 MiB、Working Set 208.9→185.6 MiB、Private 110.1→86.8 MiB。旧 VM/Project/handler 计数见 §3，最终构建重复确认全部应释放项为 0。

三次短流程关闭后的 managed 为旧版 8.9、新版 6.8 MiB；Working Set 分别为 171.2～178.0、177.5～178.3 MiB，二者并不随 managed 同方向变化。

最终三对证据：`.tmp/memory-stage2/final-paired-{baseline,current}-{1,2,3}.log`；长循环为 `baseline-100-01.log`、`current-100-final.log`。早期 paired/current 日志仅作阶段内诊断，不计入上表。旧/新短流程采用相同最终探针；长流程用于增长斜率和持有链，不能将不同流程样本混为同一个时间分布。

### 3.2 大样本与进程内存限制

9KX2 含 17,999,999 个源 Note；选中的最大 Segment 有 1,824,636 个 Note。每次流程两次独立导入，各执行三轮五类 Workspace 开关。

最终旧/新各一个独立进程，采用相同增加 GC 计数的探针：

| 指标 | 基线 | 最终阶段 2 |
| --- | ---: | ---: |
| 整个流程耗时（秒） | 39.919 | 40.016 |
| 模板中位 / P95（ms） | 94.09 / 273.46 | 91.93 / 266.17 |
| 最后一个 Project 关闭前自然 managed（MiB） | 428.3 | 254.8 |
| 同时自然 Working Set（MiB） | 630.5 | 579.4 |
| 两个 Project 关闭后 GC managed（MiB） | 149.1 | 127.6 |
| 关闭后 Working Set / Private（MiB） | 536.5 / 462.8 | 538.8 / 460.5 |
| 关闭后 GC heap / fragmented（MiB） | 341.9 / 192.9 | 286.6 / 159.0 |
| 关闭后 GC committed（MiB） | 374.5 | 371.4 |
| 进程 Peak Working Set（MiB） | 709.0 | 609.1 |
| 进程累计托管分配（MiB） | 2,451.4 | 2,526.6 |

总时间约增加 0.24%，这一次峰值 WS 较低，但累计托管分配约增加 3.1%。因此不宣称“每种分配都减少”，也不把单对峰值当成稳定减幅。最终关闭后旧版仍保留 18 个 Timeline VM、36 个 snapshot 弱引用条目、2 个 Project；新版这些计数均为 0，settings Workspace handlers 也全部清零。

**解除旧 UI / Project 强持有不等于进程工作集立即下降。** 最终关闭后的 Working Set 基本持平，而 GC committed 仍约 371 MiB、碎片约 159 MiB，明显不同于约 128 MiB 的 managed 数字。已确认存在 GC 承诺空间/碎片的高水位；不能将 Working Set 减 managed 的差直接算成 WPF native 占用，也没有完成每个 native allocation 的对象级归因。

工程中间候选的另一次旧/新流程曾测到关闭后 WS 469.9→550.3 MiB；其根对象也已经释放。这是保留的反向证据，不并入最终构建统计，不据此认定全部差额合理或全部是泄漏。证据分别为 `baseline-large-valid.log` / `current-large.log`；最终为 `.tmp/memory-stage2/baseline-large-gc-final.log` / `current-large-gc-final.log`。没有为了压低这些数值而在产品中加入 GC、清池或减少 cache/History。

## 4. 自动验证与回归处置

### 4.1 生命周期与并发断言

- 两个消费者 × 三个慢 tile × 10,000 帧，共 60,000 次 Request，只保留 6 个订阅、执行 3 次共享计算；取消一个消费者后另一消费者仍完整收到 3 个完成通知。
- 取消已经排队的完成回调后，无需运行该 Dispatcher 即能释放捕获对象；旧 generation 不覆盖同消费者的新 generation。
- 连续 10,000 次排队 generation 取消、1,000 次完成后取消，调度存储不随次数无限增长；Project Clear 取消运行和排队代，后续同 key 可以重新请求。
- 普通、选择、拖动、Onion 家族在 Unloaded/重新接管时正确取消；三种钢琴卷帘的未取消聚合与像素结果不变。
- 实际 Logical / SubVoice / Direct overlay / content pack 扫描接受取消；包括被过滤掉的记录，最多再检查 256 条即观察 token，不只在生成输出列时检查。
- 真实隐藏 HwndSource 加载原 MainWindow 视觉树，检查 Loaded/Unloaded、DataTemplate Surface 复用、Properties/Batch 的逻辑焦点返回及关闭目标失效。不开可见窗口，不代替人工前台快捷键验收。

### 4.2 未隐去的回归失败

第一次公共回归中，`VisibleFallbackBarrierDefersDetailUntilEverySegmentHasACoarseFrame` 失败。确认是测试设施与缓存完整取消协议不一致：测试在 Surface 已提交请求后才 Clear，取消了它刚登记的请求；此外，粗瓦片计数器通过区间长度猜测粗细，漏算窄尾瓦片，过去重复扫描意外补足计数。

修正为 Clear 在 Surface 创建前执行，测试 source 按实际粗瓦片区间去重计数。**未修改产品绘制、原 barrier 断言或等待上限**。同一测试独立运行三次通过，完整 Presentation 438 项通过，随后公共重跑也通过 Presentation。

证据：`.tmp/memory-stage2/fallback-barrier/` 内 `fallback-barrier-pre-fix.trx`、`fallback-barrier-fixed-{1,2,3}.trx`、`presentation-full-fixed.trx`。

第二次公共回归的 Desktop 有一条 `ClosingWorkspaceCancelsDeferredSelectionMetricsBeforePublication` 失败。确认旧的 Cancel-only 关闭改为 Dispose 后，同步清空选择生成了“指标已完成”的普通空快照；并不是后台取消失败或旧任务真的重新发布。现在使用专门的已释放终态（空 ID、无 render index、metrics 不可用），保留普通活动空选择的 complete 含义。原 `Assert.False` 保留，并在 worker 释放前捕获关闭终态、释放后断言仍为同一快照；另补普通空选择与重复 Dispose 回归。定向 12 项通过，`.tmp/memory-stage2/selection-lifecycle-final.trx`。最终整合回归另列。

终审另外补齐 All Tracks 取消 B 后回到已成功 A 的状态文案、Onion 卸载后离屏重绘不重启、非瓦片后台完成通知的取消持有链。它们不是用户音乐编辑行为的更改。

补强后完整 Presentation 暴露了一个真实整合回归：Conductor 三个 dense-hover 用例超时。查询 CTS 原来链接 raster CTS，首次/缩放绘制重置像素代时会取消语义查询，却保留它的 pending key；原完成通知仍会执行一些清理，新可撤销通知不再执行，因而暴露错误的生命周期耦合。已将 hit/ruler 查询 CTS 独立到 source/query 身份，补 source/mode 更换取消入口；Unload、替换 key 及旧布局手势回放校验继续保留。未放宽原等待时限。定向 33 项通过，含原三个用例和明确验证 raster-only reset 不再误取消语义查询的新测试；证据 `.tmp/memory-stage2/conductor-epoch/`。最终两套 UI 全回归另列，不能把定向通过当作全套通过。

新增 ruler 来源替换用例还捕获了一次取消回调挂到错误 metadata 的中间实现；已改到准确的 `RulerSnapshotProperty` 并撤掉无关修改。最终全套 Presentation 445/445、Desktop 403/403 通过，既有失败全部有明确处置，没有遗留“已知无关失败”。

### 4.3 最终公共回归

全部 Release / `--no-restore`，串行执行：

| 套件 | 通过 | 失败 / 跳过 |
| --- | ---: | --- |
| Application | 1,055 | 0 / 0 |
| Compiler | 437 | 0 / 0 |
| Persistence | 111 | 0 / 0 |
| MIDI Export | 43 | 0 / 0 |
| Playback | 117 | 0 / 0 |
| Audio Render | 37 | 0 / 0 |
| Desktop Presentation | 445 | 0 / 0 |
| Desktop | 403 | 0 / 0 |
| **总计** | **2,648** | **0 / 0** |

前六套最终证据在 `.tmp/memory-stage2/regression-verified/`；之后的改动仅涉及 Desktop/Presentation，已分别完整重跑，最终证据为 `.tmp/memory-stage2/conductor-epoch/presentation-full-conductor-fixed-final.trx`、`.tmp/memory-stage2/desktop-final-verified/Midora.Desktop.Tests.trx`。本表不将定向测试重复加到总数里，也不把这些测试等同所有 opt-in 大样本门或真实音频设备实测。

复现命令为 `eng/MemoryStage1Probe/Run-Regression.ps1 -ResultsDirectory <新目录>`；两套 UI 单独重跑使用 `dotnet test <对应 csproj> -c Release --no-restore --logger "trx;LogFileName=..." --results-directory <新目录>`。原始中间失败报告保留，不以覆盖 TRX 的方式抹去失败过程。

### 4.4 当前大样本绘制与混合视图

运行 `dotnet run --project src/midora-desktop/Midora.Desktop.OnionProbe/Midora.Desktop.OnionProbe.csproj -c Release -- "D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid"`，完整真实 App 主题/离屏 WPF 探针通过：

- 图标/工具栏主题与尺寸；3/9/15/32 DIP key × 96/120/144/192 DPI RenderTarget 的 ghost row 像素对齐全部通过。不是四台实际显示器的人工验收。
- 100/200/400 Track（各 100 Note）均收敛；含首次主题/代码预热的第一个为 1,212 ms，其后 292/383 ms，不把该顺序当作轨道越多越快的结论。
- 9KX2 Raw 窗口 `start/span` 为 0/3072、168816/24720、168816/98880、50000/24720；首帧分别约 37/40/52/36 ms，全部瓦片完成分别约 213/2,631/2,716/1,283 ms。
- Canonical 可消费；Compiled 混合视图复用 40 个 Raw MIDI Track 来源，缺失瓦片为 0，含 Dispatcher/首帧约 101 ms。该样本只有 Pure MIDI，因此 Logical-only index 为 0 Note 是预期结果，不代表漏音符；Logical stale/新索引交接另由自动测试覆盖。

证据 `.tmp/memory-stage2/onion-final.log`。这项是**最终构建的收敛/功能实测**，没有用同探针对旧版做渲染吞吐基准，不能据此宣称密集视图加速。原有灰阶/边框、选择、Velocity/Event、框选/拖动/缩放和概览 barrier 的精确行为由 445 项 Presentation 回归覆盖；真实手感仍列入验收 A。

## 5. 验收范围与限制

本阶段证明的是 M02～M05 的所有权与取消问题得到修复，不宣称解决整个原 11 GB 报告。后续预算、source facade、Undo/Clipboard 和全工作流问题仍按阶段 3～6推进。

阶段 1 的已测只读入口收益仍作为本次综合验收的组成部分：18M edited 隔离流程 Peak WS 中位 11.60→1.15 GiB、Save 中位 95.44→31.19 秒；这些是[阶段 1 报告](Midora-Memory-Stage1-Validation-Report-2026-09-08.md)中的历史实测，**不是本轮重新运行的完整 WPF/Worker 峰值**。本轮验证视图生命周期及相关回归，没有把两种不同流程的内存相加或互换。

以下保留原 **MEM-A01～MEM-A08** 操作卡。用户现已确认全部粗略通过：功能正常，未发现性能问题；更精细检查及体验优化留到正式发布前真实编曲阶段收集。不要求重做全部操作卡。

| 编号 | 操作卡 | 预期 |
| --- | --- | --- |
| A01 | 打开/导入大样本，编辑一次，Save、Save Copy，再打开副本 | 内容完整，无异常保存停顿；阶段 1＋2 综合链路 |
| A02 | 三种钢琴卷帘分别打开→切走→返回→关闭→重开；Conductor 也切换一次 | 隐藏保留视口/选区/Lane/侧栏状态，重开后可正常编辑 |
| A03 | 少量和密集音符处拖动/缩放，越过 Segment 右边界 | 无新卡顿、长期空白、旧图或低缩放边框错误 |
| A04 | 选择→编辑→取消选择→Undo，并切换 Velocity/Event Lane | 内容与高亮及时一致，不重新选回已取消对象 |
| A05 | Onion Previous/Next/手选、Disable、透明度 0 再恢复，切走再返回 | 手选列表/模式保留，叠层与启用状态正确 |
| A06 | All Tracks Raw/Compiled 往返，编辑 Logical 后返回；如构建较慢，取消后再 Refresh | Pure MIDI 源层、Logical stale/current 与取消重试状态正确 |
| A07 | Properties/Batch 确认或取消后继续 D/S/A/Ctrl+E 等快捷键 | 焦点留在正确视图，不操作旧 Tab |
| A08 | 关闭 Project，打开另一个 Project，做简短编辑和视图切换 | 无旧项目对象/选择混入，任务和快捷键正常 |

以上全部标记为**用户粗略验收通过**，不等同完整发布验收。后续 All Tracks 激活快捷键补修也已通过用户验收，并另行获准提交、推送；阶段 3 仍未开始。

## 6. 文件归属与交付状态

- Workspace/Controller：`PresentationModels.cs`、`DesktopSessionController*.cs`、`ConductorWorkspacePresentation.cs`、`WorkspacePresentationDispatch.cs`。
- 命令/焦点：`TimelineCommandTarget.cs`、`MainWindow.xaml.cs`、`MainWindow.TimelineObjectList.cs`、`AllTracksView.xaml.cs`。
- Onion/异步绘制：`AllTracksWorkspaceViewModel.cs`、`TimelineSurface*.cs`、`TimelineRasterCache.cs`、`TimelineRasterFactory.cs`、`CancelablePresentationDispatch.cs`、`TimelineRenderModel.cs` / `TimelineOnionSnapshot.cs`。
- 扫描取消传递：Presentation source adapters；Domain 的 `PagedTimelineCollections`、`PureMidiPagedContent`、`PureMidiContentPack`、`DirectMidiNoteOverlayIndex`；Application 的 `BoundedDirectMidiNoteSource` / `BoundedDirectMidiIndex`。只改变运行时取消协作，不改音符/事件结果、COW 编辑语义、格式或音频链。
- 工程证据：新增 `eng/MemoryStage2Probe/`、上述生命周期/取消测试，更新现有 Conductor/Onion/Rendering/Selection 回归和 OnionProbe 的永久清理路径；原始输出留在 gitignored `.tmp/memory-stage2/`。
- 文档：本报告、[生命周期设计](Midora-Memory-Stage2-Lifecycle-Design.md)、[执行计划](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md)、[覆盖表](Midora-Memory-Optimization-Coverage.md)。未修改 SRS、版本或 Project schema。

构建、自动验证、串行探针及 `git diff --check` 已完成；没有运行可见应用自动操作、提交、推送或发布 `dist`。阶段 2 的根持有/订阅问题已达到工程出口，后续预算与完整进程内存问题仍按原六阶段计划处理。

## 7. A 验收后补修：All Tracks 激活后的快捷键

用户确认 A01～A08 粗略通过后另报：切到 All Tracks，Ctrl+Z/Y 要先点击视图才恢复。该补修不构成阶段 3 启动。

### 根因与修改范围

阶段 2 给 Mode SelectionChanged 增加 `IsLoaded/IsVisible` 保护，但初始化绑定在 Loaded 之前触发，早退后没有 Loaded 补发，丢失 Tab 的焦点交接。Raw/Compiled ComboBox 是该视图首个可聚焦控件；焦点在 ComboBox 时，MainWindow 的 `IsTextEditingFocus()` 在 Ctrl 命令 switch 前返回。这会拦截 Ctrl+Z/Y，也会影响同一 guard 后的 Ctrl+S 等命令。Space 使用另一个判断，关闭的 ComboBox 不属于其输入阻断条件；Ctrl+Tab 的判断也在早退之前，不能笼统宣称所有快捷键均失效。

产品修改仅为 `AllTracksView.xaml.cs` 和该视图 XAML 中模式下拉的名称/关闭通知：首次 Loaded、恢复可见、DataContext 接管、Mode 变更统一安排可取消的弱引用焦点交接。真正执行前复核对象身份与活动生命周期；模式下拉打开时保留其焦点，关闭后再交回 Timeline。后台 rebuild 不触发持续抢焦点。没有改全局输入例外、渲染、音频或任何编辑命令，也没有给只读 All Tracks 开放 D/S/E/A 编辑工具。

### 验证方式与边界

扩展原 `HostedWorkspaceLifecycleTests` 的真实 MainWindow/BAML、不可见 HwndSource 测试，覆盖首次打开、三次 Tab 往返、模式变更、已加载视图重新可见、后台更新不抢焦点、旧回调隐藏/关闭后失效，并断言原工作区选择、Project publication revision、History 和 All Tracks 只读能力不变。

模式下拉测试使用不含 PART_Popup 的专用空模板，不显示原生 Popup 窗口；打开/关闭状态仍使用真实 ComboBox，关闭时显式发送正常由 Popup.Closed 产生的控件通知。这验证本地输入保护与交接逻辑，不声称测试了操作系统前台键盘或 Popup 动画。早期测试漏发此通知导致一次失败，已修正测试设施；原断言不变。

修前定向测试已失败（焦点目标为 null，未建立 Timeline logical focus）：`.tmp/memory-stage2/focus-followup/alltracks-focus-before.trx`。补齐激活后，同一定向测试及 Desktop 403/403 全套通过：`alltracks-focus-after-isolated.trx`、`desktop-focus-full.trx`。模式下拉无 Popup 测试漏发关闭通知的中间失败保留在 `desktop-focus-final.trx`。

最终补修构建重新运行完整 Desktop：**403/403 通过，0 失败、0 跳过**，包含增加的焦点断言（扩展既有 fixture，不重复增加用例计数）。证据：`.tmp/memory-stage2/focus-followup/desktop-focus-verified.trx`；对应隔离输出 `Midora.dll` SHA-256 为 `AD58931E0B43558BDF83B70D64683FAB8925DC8C492E51FB8F419FD1DB4CA815`。`git diff --check` 通过。

常规 Release 构建遇到用户正在运行的 `Midora.exe` 文件锁，未终止用户进程。后续通过 `-p:OutDir=D:\Programing\midora\.tmp\memory-stage2\focus-followup\build\` 在仓库隔离目录构建/测试；未运行发布脚本或触碰 `dist`。本补修没有重新运行大型性能探针，因此 §2～4 的性能数字继续只对应原验收候选。

定向人工复核为：从其他 Tab 切到 All Tracks 后不点内容，直接 Ctrl+Z/Y；再切换 Raw/Compiled、关闭下拉后继续使用快捷键。用户已确认本次补修验收通过并授权提交、推送，不需要重做已粗略通过的全部 A 项；此确认不把离屏自动测试变成操作系统前台键盘测试。
