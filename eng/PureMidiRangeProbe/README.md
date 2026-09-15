# Pure MIDI 范围正确性 / 冷热性能探针

使用冻结的生产 DLL，读取真实 MIDI，不改输入文件，不打开物理音频设备，不生成 `dist`。输出目录必须为新的仓库内 `.tmp` 子目录。

```powershell
dotnet build eng/PureMidiRangeProbe/PureMidiRangeProbe.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:MemoryProbeReferenceDirectory=<冻结的 Application.Tests 或完整应用依赖目录> -o <新的探针目录>
dotnet <guard目录>/MemoryStage6Probe.dll guard <guard输出> 8192 2048 'C:\Program Files\dotnet\dotnet.exe' <探针目录>/PureMidiRangeProbe.dll 'D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid' <结果目录> verify
```

必须使用阶段 6/5 共用的进程树 guard：8 GiB private commit 硬上限、2 GiB 系统 available reserve。正式性能对比串行运行，不与 WPF/其他大样本探针同时跑。

`verify` 核验完整短范围的实际 NoteOn / event count 等于声明值；省略该参数可测量旧版已知错误结果，但不能把旧版记录算作正确性通过。`raw` 只导入并列出原始未配对 Note 消息的分布，用于调查，不运行后续性能流程。

默认流程覆盖：导入、全曲冷/热编译、开头窗口、密集窗口冷/热查询、中途至自然结束冷/热编译、短范围提前终点冷/热编译及完整查询、原始 Note 消息区域的冷/热截断、添加一个 Direct Note 后 Incremental/Full、ClearCache。只查询短范围，不把 18M 源全曲 canonical 物化。

`Run-Comparison.ps1` 接收 guard、两组冻结 DLL、MIDI 路径及新 `.tmp` 输出目录；为同一探针分别引用旧/新 DLL，默认串行交替各运行三次，候选使用 `verify`。任何构建/断言/守护失败立即终止后续流程，不覆盖历史证据。请在其他测试结束后运行。

日志包含生产 Compiler/Domain MVID、elapsed、累计 allocated、当时 Private/WS/managed、准确计数与事件摘要。guard 另记录进程树峰值。`cold` 表示该运行中首次该请求，不代表清空操作系统文件缓存；整段流程刻意保留多个结果以覆盖并存，峰值不能当作一次 Play 的最低内存。

Full/Incremental 的大型门比较 metadata/fingerprint；完整事件/source 的独立小数据 oracle 位于 Compiler/Application/MIDI Export 测试。不要只凭此探针的摘要值宣称全曲逐字节等价或音频听感已通过。

已执行的 2026-09-09 三轮串行前后对照、实际计数、额外时间成本和进程峰值见 [验证报告 §3](../../misc/Midora-Pure-MIDI-Range-Correctness-Repair-Validation-2026-09-09.md)。结果不是“全部场景更快”：完整终点读取、raw 交互判定和编辑后首次范围预备确有额外成本。
