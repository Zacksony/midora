# 阶段 5 安全测量探针

依据：内存执行计划 §10、SRS §12.21/12.24/12.25、INV-009/010/015/020/095/096。工具不修改生产规则、Project 格式或持久化来源，不运行 WPF、SoundFont、音频或 `dist` 发布。

需求追踪：输入为冻结 DLL 与确定夹具；输出为独立结果目录中的计时、内存、完整 formal digest 或明确的巨大结果抽样。范围统一为半开区间。失败/取消保留日志，不把被守护终止或 instrumentation 异常算通过。工具自身选区/次数上限仅用于测试安全，不是产品限制。

## 冻结与构建

先从已验证的 Release 输出冻结完整依赖，禁止在 baseline 冻结后重新覆盖它：

```powershell
./eng/MemoryStage5Probe/Freeze-Binaries.ps1 -SourceDirectory src/midora-core/Midora.Application.Tests/bin/Release/net10.0 -DestinationDirectory .tmp/memory-stage5/baseline -Label pre-stage5
dotnet build eng/MemoryStage5Probe/MemoryStage5Probe.csproj -c Release -m:1 -nodeReuse:false -o .tmp/memory-stage5/probe-baseline
# 同一探针源针对冻结 candidate DLL 构建，不需要改变生产项目：
dotnet build eng/MemoryStage5Probe/MemoryStage5Probe.csproj -c Release -m:1 -nodeReuse:false -p:MemoryProbeReferenceDirectory=D:\Programing\midora\.tmp\memory-stage5\candidate -o .tmp/memory-stage5/probe-candidate
```

`binary-snapshot.json` 记录源目录、时间、label 和逐文件 SHA-256；每份结果另记 Domain/Compiler/Application MVID。完整源 revision/dirty identity 由阶段报告负责，工具不会运行 Git。

## 强制外部守护

重型程序只能串行运行，并使用独立新目录：

```powershell
dotnet .tmp/memory-stage5/probe-baseline/MemoryStage5Probe.dll guard .tmp/memory-stage5/runs/baseline-example 8192 2048 'C:\Program Files\dotnet\dotnet.exe' .tmp/memory-stage5/probe-baseline/MemoryStage5Probe.dll compile .tmp/memory-stage5/runs/baseline-example 10000 1 0 1 5 Reject isolation=shared spacing=4 varied=false
```

守护通过 `CreateProcess(CREATE_SUSPENDED | CREATE_NO_WINDOW)` 创建子进程，在其第一条指令之前加入独立 Windows Job，然后 Resume。Job 带 8 GiB（约 8.59 GB）Private commit 硬限制与 kill-on-close；不允许通过工具参数提高到 8 GiB 以上。全部后代自动属于该 Job。每 100 ms 采样并合计该 Job 的 Private Bytes / Working Set，同时检查系统 available RAM 默认保留 2 GiB。任一软阈值达到则 `TerminateJobObject`，只终止自己的 Job，不按进程名批量 kill、不触及用户应用。

Job 硬限制防止两次采样之间的瞬时 Private commit 激增；Working Set 为采样阈值，会有一个采样间隔的轻微超调。硬上限可能首先使 CLR/native allocation 失败，而不是先得到一个恰好达上限的采样；此时子程序非零退出与 stderr 是证据，不应声称它完成了测试。守护异常/外部中断关闭自己的 Job 时仍清理其后代。保留所有 `guard.jsonl`、`guard-result.json`、`child.stdout.log`、`child.stderr.log` 和不完整 phase 日志。

低阈值功能测试用最多 128 MiB 人工夹具，无需耗尽真实机器：

```powershell
dotnet .tmp/memory-stage5/probe-baseline/MemoryStage5Probe.dll guard .tmp/memory-stage5/runs/guard-selftest 96 2048 'C:\Program Files\dotnet\dotnet.exe' .tmp/memory-stage5/probe-baseline/MemoryStage5Probe.dll guard-fixture 128
```

预期退出 124 且日志明确 `guard-stop`；它不是产品测试失败。守护也可以包装 `dotnet build/test`，参数按原样传入，测试结果仍建议同时输出 TRX。

## 编译梯度

`compile OUTPUT COUNT VOICES TEMPLATE_NOTES LOOPS REVISIONS POLICY [isolation=shared|isolated] [spacing=TICKS] [varied=true|false]`

- 新空 instrument：`10000 1 0 1 5 Reject isolation=shared spacing=4`。这会产生合法源的 overlap 编译失败，保留全部诊断；失败结果本身应与 baseline 完全一致。
- 单模板音符成功路径：`50000 1 1 1 10 Reject isolation=shared spacing=512 varied=true`。gate 默认 480，与相邻 Trigger 不重叠。
- 独立 Unit 重叠：`50000 1 1 1 10 LetOverlap isolation=isolated spacing=4`，约 120 个同时活动实例。LetOverlap+shared 被正式 validator 拒绝，不能作为成功基线。
- VOICES / TEMPLATE_NOTES / LOOPS 分别控制 SubVoice、模板 Note 数和 Loop 倍率。Loop 必须使用 `isolation=isolated`（否则 MIDORA1213）；合法倍率例为 `1000 4 8 4 3 Reject isolation=isolated spacing=2048 varied=true`，gate1920，同一时刻最多4 Unit。禁止盲目运行所有维度笛卡尔积。

每轮改变单一 Logical Note velocity，保留一个 MidoraCompiler 的增量缓存，再单独计时 Full oracle。schema 2 将有序全部事件、来源、诊断、context、statistics、allocations、SMF descriptor、Conductor 写入 SHA-256；诊断用 64 KiB 二进制缓冲避免每条巨大 JSON 干扰。此 hash 是探针版本化编码，不是产品 fingerprint。Raw census 用反射在计时后报告缓存 instances/voices/events/unique event stores，只是身份/数量，不能称为精确独占字节。编译阶段计时不含 hash/census，但守护峰值包含全部工具成本。

普通阶段不主动 GC；仅最后 `release-controlled-gc` 运行诊断 GC。累计 allocation、自然 managed、GC heap/committed/fragmentation、Private 与 WS 分开记录。`full-oracle` 特意允许 current 与 oracle 同时存活，属于对照峰值，不冒充产品必然峰值。

`Compare-Results.ps1` 拒绝 schema/fixture 不同、缺结果或守护停止的记录，再逐阶段验证 exact digest 并报告时间/分配/Private，不将三次样本推导成 p99。

## History 与真实样本

`history OUTPUT NOTE_COUNT COMMAND_COUNT` 使用正式 CopySegments / Paste、深层 Undo/Redo、Clipboard 替换和新分支，并验证每个 Segment 的内容数量。它显式延迟后台编译一小时以归因 M13；不是自然后台编译、WPF、完整可视响应验收。结果不会把 History 条数乘以 64 MiB 冒充常驻。

`midi-logical OUTPUT MIDI_FILE MAX_SELECTION TEMPLATE_NOTE(0|1) REVISIONS count-only|compile` 从正式 ImportFile 打开独立测试 Project，定位唯一 `MIDI Out #23` 的唯一 Segment，冻结 Project Tick 168960 到 Segment End 的 **Note start-in-range** 选择（不是 any-intersection），最多选指定条数。源 MIDI 始终只读。`count-only` 用最多 2M 条紧凑选区元数据/按 key 活动队列计算原始 gate overlap pair 数并报告是否越 int32；它在目标 exact-duplicate reduction/hard clipping 之前，只是保守源区间 oracle，不替代正式 compiler。`compile` 经正式 Direct→Logical Clipboard Paste，目标严格为 Project Start 168960 / Length 24576，记录 exact duplicate 折叠数量。默认保持新空 instrument/Reject；每 revision 在范围查询证明未占用的 start/key 通过正式 CreateLogicalNote command 新增单 Note，随后 incremental。独立 Full 验证有限抽样；先逐次 Undo 新增并重编译恢复原抽样，再 Undo/Redo 原 Paste。

真实巨大诊断只按 Count 与最多 257 个固定 ordinal 抽样，不遍历数亿项 JSON/hash；完整排序和 Full/Incremental oracle 由小样本 exact fixture 提供。真实导入与最大选区只在协调的独占重型槽、低梯度成功后运行；不运行 1.38M 原版 baseline 来证明它会耗尽内存。

最后一参也支持 `background`：不更改产品默认75ms debounce，通过正式 ProjectCompilationSession 自然后台编译，每次命令后等待当前 SourceRevision 对应 CompiledRevision 发布；记录失败尝试与 LastSuccessfulResult 保留，新增和 Undo/Redo 均等待并核对。等待不会调用可强制立即调度的 EnsureCurrentCompilationAsync（仅已结束的内部异常用该接口取错误）。`compile` 仍显式延迟后台一小时、使用独立编译器，用于分离归因。两模式均为无 WPF 的正式领域/会话工作流，不冒充完整 UI 响应验收；后台模式的 `full` 阶段名表示首次 Paste 后等待，不能解释为独立 CompileFull 计时。

真实选区的保守源pair数若超过`int.MaxValue`，compile/background在Paste之前主动报错停止并保留selection日志；不尝试物化数十亿诊断、不截断诊断或伪称通过。这是对现有`IReadOnlyList.Count`表示边界的探针护栏，不是新增产品语义。65,537个同key、不同start、全部互相重叠的Note会有2,147,516,416个pair，理论上已超过该边界。后台等待使用100ms有界轮询，避免工具事件退订/Dispose与后台完成回调竞态；真实关闭会话/Project后单独记录诊断GC，普通阶段不强制GC。
