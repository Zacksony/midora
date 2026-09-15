# 阶段 6：关闭后内存归因探针

纯编译（不含 WPF/音频）的退栈后生命周期对照。读取冻结的 DLL，不修改产品、不发布 `dist`。

`retention OUTPUT TRIGGERS VOICES TEMPLATE_NOTES CYCLES` 使用固定 4 轮 Loop，真实 Full/Incremental，加独立 Full 的完整事件/来源/诊断对照。每轮 Project/Compiler/Canonical 在独立 `NoInlining` 栈帧中创建和释放，只返回弱引用。退栈后专门诊断 GC，不混入自然编译时间，也不改产品 GC 策略。

记录 Private/WS、GC live/committed/fragmentation 与 VirtualQuery 的 committed private/mapped/image 分类。该分类不是逐模块所有者归因，也不把 reserve 算成物理内存。多轮新 Project 检查关闭持有与内存平台；失败保持非零退出。

构建使用 `-p:MemoryProbeReferenceDirectory=<冻结目录>`，默认 `.tmp/memory-stage6/baseline-build`。新输出目录运行，必须经 Stage5 共用 guard，8 GiB private commit 硬上限、2 GiB 系统 available reserve，大型流程串行。

```powershell
dotnet build eng/MemoryStage6Probe/MemoryStage6Probe.csproj -c Release -m:1 -nodeReuse:false -o .tmp/memory-stage6/retention-probe
dotnet .tmp/memory-stage6/retention-probe/MemoryStage6Probe.dll guard .tmp/memory-stage6/retention-1000 8192 2048 'C:\Program Files\dotnet\dotnet.exe' .tmp/memory-stage6/retention-probe/MemoryStage6Probe.dll retention .tmp/memory-stage6/retention-1000 1000 4 8 3
```

本工具完整 oracle 故意使新旧成功结果同时存活；峰值不能当作一般用户必然开销。原始 JSONL/守护日志只在 `.tmp`，不提交大型产物。

`Run-Regression.ps1` 串行构建并执行 11 个公共测试项目，BASS 仅运行无物理设备的托管 gate。每个项目都有独立 Job/日志/TRX；已发现的断言失败不会阻止后续独立测试集，但最终命令仍返回失败，绝不将其当作通过。守护中止、未产生 TRX 或未发现测试时立即停止，不继续压力运行。无 opt-in 环境变量时自行 return 的测试，不能计作真实样本路径已测。

```powershell
& eng/MemoryStage6Probe/Run-Regression.ps1 -GuardAssembly .tmp/memory-stage6/retention-probe/MemoryStage6Probe.dll -ResultsDirectory .tmp/memory-stage6/regression-final
```
