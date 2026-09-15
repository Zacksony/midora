# 阶段 6：真实 WPF 会话内存探针

输入是只读 SMF 与已构建/冻结的 Desktop Release DLL；正式输出为独立测试目录里的 `.midora`、Save Copy、JSONL 测量和结果 JSON。源 MIDI、用户程序、`dist` 均不修改，不使用 computer-use。只调用 `App.InitializeComponent`，不调用 `App.Run`/正式启动流程或 `MainWindow.Show`；实际 WPF DataTemplate/Control/RenderTargetBitmap、DesktopSessionController、自然后台编译和 Project 命令仍执行。

需求追踪：执行计划 §11，SRS §18.11、§20.3.15/20.4.11、INV-009/010/012/065/069/095/096/115/116。选区固定 `MIDI Out #23` 的 Note start-in-range `[168960, Segment End)`；目标 Logical Segment `ProjectStartTick=168960 / LengthTicks=24576`、local paste 0。默认新空 Instrument、Reject 不改；失败 canonical 保留完整诊断和旧成功结果。复制/粘贴使用压缩 ID 集、正式分页 staging/选择投影，新增与 Undo/Redo 保持正常 75 ms 后台调度。

场景：完整导入 → Direct/Logical/Instrument/Conductor/Arrangement/All Tracks 真模板渲染 → 虚拟对象列表显隐 / Onion / Raw / Compiled / 边界缩放切换 → 跨类型 Paste → 连续新增与逐次 Undo → Paste Undo/Redo → 保留历史和视图保存/Save Copy → 关闭 → 分别重开两份文件/验证 → 关闭。分别在预取消及首次非零进度后取消 detached paste，验证 source revision 和目标 count 零发布；后者明确记录取消发生的阶段/工作量，不冒充所有失败点覆盖。不包含音频设备、播放/听感、屏幕输入延迟或人工视觉验收；离屏 `RenderTargetBitmap` 不能代替最终显示器输出和用户手感。

所有输出目录必须新建，禁止覆盖旧结果。构建先完成源码 Release，工具以 AssemblyName `Midora.Desktop.Tests` 复用仓库已有 InternalsVisibleTo，不修改生产公开接口：

离屏 Visual 不产生 HWND `IsVisible` 转换：工具显式触发真实控件 Loaded/Unloaded；对象列表另外激活真实 `TimelineObjectListState` 并读取最多 32 行窗口，关闭时解除活动状态。由此验证真实 provider/数据/生命周期，但不把它描述为列表 Pane 可见性调度器或屏幕滚动的测量。

```powershell
dotnet build eng/MemoryStage6WpfProbe/MemoryStage6WpfProbe.csproj -c Release -m:1 -nodeReuse:false -p:MemoryProbeReferenceDirectory=D:\Programing\midora\.tmp\memory-stage6\baseline -o .tmp/memory-stage6/wpf-probe
dotnet .tmp/memory-stage6/wpf-probe/Midora.Desktop.Tests.dll guard .tmp/memory-stage6/wpf-small-guard 8192 2048 'C:\Program Files\dotnet\dotnet.exe' .tmp/memory-stage6/wpf-probe/Midora.Desktop.Tests.dll run .tmp/memory-stage6/wpf-small .tmp/memory-stage5/small-repro.mid 2000000 2 2
```

候选构建须改为该次冻结的 `MemoryProbeReferenceDirectory` 和独立工具输出目录；运行结果与 guard 目录也分别新命名，不能覆盖 baseline 或前一候选。fixture 日志记录 Desktop / Application / Playback / Compiler assembly MVID，以实际载入的程序集区分不同候选。

`run OUTPUT MIDI MAX_SELECTION EDITS VIEW_CYCLES`：MAX_SELECTION `1..2000000`，EDITS `0..20`，VIEW_CYCLES `1..100`。真实完整样本用 `2000000 5 3`。安全上限只是测试参数，不是产品限制。重型测试必须由主任务串行协调，复用阶段 5 私有 Job guard，8 GiB commit 硬限制、Private/WS 采样限制和至少 2 GiB available RAM；不得直接绕过 guard 运行大样本。输入全文件流式 SHA-256 在计时外进行，计时内不建立完整诊断 digest；失败诊断最多固定 257 ordinal 抽样。

## 生命周期与归因口径

每个闭合场景是单独 `NoInlining` 方法；方法返回后才做受控 GC，消除它的局部变量/闭包强根。探针仅保留 WeakReference 和标量/字符串结果；记录 Project、Document、Compilation、Canonical、Workspace、Snapshot、Surface、Clipboard。关闭断言要求本场景全部弱引用归零；持续存活时失败，不能把 Dispose 当作回收证明。真实 MainWindow/Session 仍保持存活，验证后续 Project 会话而非只检查进程退出。

普通业务阶段不触发强制 GC。分开报告累计分配、live managed、最近 GC heap/fragmentation/committed、Private、WS、句柄/线程、进程 CPU 和 `Private - GC committed`。关闭后记录自然点、诊断 GC、3 秒 Dispatcher idle，以及 LOH CompactOnce + compacting GC。最后一步纯诊断，不进入产品或业务时间。

阶段 allocation 是整个测试进程累计计数的差值，包含同一窗口中并行运行的 WPF/Compiler 工作，不是该命令的独占分配归因。业务计时结束后才采集 gauge、VirtualQuery 和写测量日志；外部守护峰值仍包含这些仪器的成本。

schema 2 增加现成 PreparationStorage 与六项 Raster gauge；只通过弱引用访问存活的 Desktop session 取得值类型快照，不静态保留 Project/Compilation。关闭后等待在途/运行/订阅/完成队列清零，再断言 cache bytes/entries 为零。另比对重开前后全部 Pure MIDI 的 Root/Track/Segment/Note/Channel/Opaque 计数；这是结构/数量门，不冒充所有源字节的独立 semantic digest。旧 schema 1 日志没有这些计量，不回填、不伪造。

VirtualQuery 只扫描当前测试进程区域元数据，计量 MEM_COMMIT 的 MEM_PRIVATE/MEM_MAPPED/MEM_IMAGE 与 reserved；不能把 MEM_MAPPED 当成 Private，也不能把 `Private - GC committed` 直接称作 native 泄漏。两者采样时刻及 OS 记账口径不同，含 CLR/JIT/loader、WPF/native、线程栈、映射 COW 等未细分成本。弱引用归零证明列出的托管根不再持有，不证明所有 native heap 均已释放。结果保留未归因差额，不虚构精确所有者分摊。

已执行的 baseline、cleanup 候选小样本/100 次视图循环、两次完整大样本及关闭区间见 [RESULTS-2026-09-09.md](RESULTS-2026-09-09.md)。该 WPF 生命周期门不取代 Compiler 独立 oracle；已知 paged Pure MIDI consumer range 边界缺陷必须单独报告，不能因本探针 `passed=true` 而被掩盖。
