# Logical canonical 内存探针

测试范围：Logical 成功输出的完整 formal sequence、来源、顺序、fingerprint、Full/Incremental、范围与旧结果持有；不修改产品语义，不使用外部 MIDI、WPF 或音频。本探针复用阶段 5 Windows Job 守护，重型运行只允许串行，8 GiB 进程树 Private 硬上限、至少 2 GiB 系统余量。

先用 `eng/MemoryStage5Probe/Freeze-Binaries.ps1` 将前/后完整 compiler 依赖分别冻结。默认依赖 `.tmp/logical-compiled-memory/baseline`。同一源码针对候选依赖重新构建，不能覆盖旧依赖或结果。

```powershell
dotnet build eng/LogicalCompiledMemoryProbe/LogicalCompiledMemoryProbe.csproj -c Release -m:1 -nodeReuse:false -o .tmp/logical-compiled-memory/probe-baseline
dotnet .tmp/logical-compiled-memory/probe-baseline/LogicalCompiledMemoryProbe.dll guard .tmp/logical-compiled-memory/runs/baseline-small-guard 8192 2048 'C:\Program Files\dotnet\dotnet.exe' .tmp/logical-compiled-memory/probe-baseline/LogicalCompiledMemoryProbe.dll run .tmp/logical-compiled-memory/runs/baseline-small complex 8
```

场景：`shared`（无隔离、多 Track 同 Usage）、`isolated`（多个 SubVoice/Loop）、`complex`（Pre-Roll、Loop、Pitch Bend、CC Mapping、Value Curve、跨 Track、范围冷启动）、`invalid`（完整 overlap 失败诊断）、`expansion`（显式合法展开倍率）、`mixed`（Logical 高展开 + inline Pure MIDI Fixed Root、跨 Track CC/Pitch Bend、opaque Meta/SysEx）。参数为 `run OUTPUT SCENARIO [COUNT [TEMPLATE_NOTES LOOPS VOICES]]`。工具限制最多四百万预期 NoteOn 候选，仅为测试安全，不是产品容量。

LC-T3 增加 `segmented`：沿用 expansion 的单音触发，分别置于互不重叠的独立 Segment，检验冷来源种类超过 4096-entry intern 上限后的路径。例如 `run OUTPUT segmented 5000 64 2 1`。它与巨大单 Segment 是不同负载，不能互相替代。

`cold OUTPUT SCENARIO ...` 只执行一次 Full，不跑完整序列 oracle，也不同时保留三个结果；用于分开观察单次编译进程峰与原 `run` 的验证/多结果峰。cold 不清操作系统缓存，包含进程首次 JIT，不能称为磁盘冷启动。它只核对成功、计数/fingerprint 和关闭 owner，**不能单独证明完整来源/语义等价**；同一夹具必须另用 run 或正式 golden 门验证。两个模式均在编译计时之外记录 retained storage 与受控 GC，产品正常流程不执行这些 GC。

`run` 的 `retained-storage` 分别列出清 cache 后三份结果的显式可达存储估算及按身份去重合计；cold 记录 before/after-clear。它们由 `RetainedStorageCollector` 计量，不是 heap dump、进程 Private 或全局泄漏证明。只对支持相关内部布局的候选记录 sourceCount，旧 DLL 上缺失表示不可用而非零。

`LogicalCompiledMemoryOracleTests.cs` 同时编入工具。schema 1 SHA-256 流式覆盖全部正式事件字段和 SourceReference、诊断、分配、context、Conductor、SMF descriptors及产品fingerprint；不是抽样。无完整事件/JSON数组。编译和digest计时分开，但峰值涵盖两者；旧/新结果会有意同时保留，以验证真实合法读者不被清理。所有强owner离开非内联栈后才做诊断GC/弱引用；生产代码不增加GC。

结果分别记录累计 allocation、managed、GC committed、Private与Working Set，不能相互混称；单轮不宣称p99。新功能的语义/golden结果必须从冻结旧DLL产生，不可由候选实现自行更新期待值。测试不涵盖物理音频或真实窗口最终绘制延迟。

峰值是整个受守护子进程的采样峰值，包含三个同时存活的合法编译结果以及逐字段 oracle 序列化的临时分配，不等同于单个正式结果的 retained size；测试末尾 `closed-controlled-gc` 只验证所有强 owner 离栈后的可回收性。`alive=0` 且 managed 回落、但 Private/GC committed 仍高，可能是 CLR 高水位，不得直接判作对象泄漏。正式产品不因此增加强制 GC。

建议最终阶梯按 `mixed 32` → `shared 10000` → `expansion 32 256 8 4` → `shared 100000` → `expansion 128 256 8 4` 分别独立运行；较大两组区分多触发与少触发/高展开，不能只用同一种规模外推。旧版本触发守护即保留失败记录并停止，不允许提高 8 GiB 上限来补齐对比。所有结果同时报告时间回退，不以降低峰值为由省略变慢的阶段。

`mixed` 另生成完整消费者 oracle，覆盖 render、SMF channel/opaque、Pure audio metadata及Channel Mode。这里必须将 inline `Events` 与仅分页后备的 `QueryMidiRenderEventPages` 按正式 render 总序合并，不能直接把后者误当完整输出。该小型消费者检查明确限制十万事件，允许局部排序数组；大型事件 oracle 则仍逐页流式处理。`full-consumer.jsonl` / `range-consumer.jsonl` 保留逐字段输出以定位差异。

复杂语义分两档覆盖：`complex 8` 的完整输出 838 条，属于 inline 回归；`complex 64` 的完整输出 5,766 条，明确要求 `HasPagedLogicalEvents`，固定旧版完整与范围 hash，同时断言真实 CC11 Add 映射结果、CC1 连续曲线的中间值及三个非空查询范围，不能拿空 inline 集合互相比较来代替分页正确性。

新运行的编译阶段另记录 process CPU 毫秒（所有线程之和，可大于墙钟）及 `GetProcessIoCounters` 的 read/write bytes 与 call 差值，包含 Windows 文件/设备 I/O 计数，不等于物理磁盘读写或磁盘 busy 时间。不能仅因记录了大量 I/O 字节就判断等待磁盘是主要耗时，应结合 CPU、端到端对照和压缩前后结果。

`compression OUTPUT` 是独立 CPU 微基准，只对冻结旧 DLL 使用。读取 `expansion32` 实际 canonical 首/中/尾各一个 4096-record 页，并编译一个含 8,192 同 Tick、不同 Source ID Direct Note 的小夹具读取两页，对 Brotli quality 0/1 与 Deflate Fastest 各重复 40 次压缩和解压，验证还原字节完全相同。该测试包含不同 Tick、重复模板及同 Tick 来源变化，不读取外部样本、不修改产品编译规则。编码计时中 Deflate 包含 stream/缓冲分配而 Brotli 使用预留数组；这是实现级候选筛选，不是纯 codec 等条件排行榜。比率和 CPU 时间不能代替完整编译的 I/O/内存测量。入口仍必须通过同一 `guard ... 8192 2048 ... compression OUTPUT` 调用。

`first-window` / `last-window` 在 Full 后、全流 digest 前测量 512-tick 查询，记录完整窗口 digest 和事件数；不是取样全结果正确性。`Compare-Results.ps1 -BaselineDirectory ... -CandidateDirectory ...` 核对同夹具/运行时、所有完整与窗口 digest、守护成功及关闭弱引用，再分别列时间和累计分配。守护目录必须是结果目录追加 `-guard`。冻结旧 golden 来自编译器 MVID `10fd65c7-51a0-4658-8295-4b9b4f67ae81`，对应提交 `c02e0bc`。
