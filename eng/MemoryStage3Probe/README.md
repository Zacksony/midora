# 阶段 3：同一探针的旧 / 新实现对比

此工具不启动 WPF、Worker 或音频设备，不生成 `dist`。它分别引用冻结的旧、新程序集，避免把修改后的源码当作基线。原始 JSON 输出放在仓库忽略的 `.tmp/memory-stage3/`，总结保存在 `misc/Midora-Memory-Stage3-Validation-Report-2026-09-08.md`。

## 构建与运行

先在改动前将 Application 及依赖构建到独立 baseline；改动后构建到 candidate。**禁止将 candidate 覆盖 baseline**。例如在仓库根目录执行（PowerShell）：

```powershell
dotnet build src/midora-core/Midora.Application/Midora.Application.csproj -c Release -p:OutDir=D:\Programing\midora\.tmp\memory-stage3\candidate\ -p:CopyLocalLockFileAssemblies=true
dotnet build eng/MemoryStage3Probe/MemoryStage3Probe.csproj -c Release -p:MemoryProbeReferenceDirectory=D:\Programing\midora\.tmp\memory-stage3\candidate -p:OutDir=D:\Programing\midora\.tmp\memory-stage3\probe-new\
dotnet .tmp/memory-stage3/probe-new/MemoryStage3Probe.dll plans 1000 1000 .tmp/memory-stage3/plans-new-1000.json
dotnet .tmp/memory-stage3/probe-new/MemoryStage3Probe.dll opaque 96 1 .tmp/memory-stage3/opaque-new.json
dotnet .tmp/memory-stage3/probe-new/MemoryStage3Probe.dll ids 1000 sparse .tmp/memory-stage3/ids-new-sparse.json
dotnet .tmp/memory-stage3/probe-new/MemoryStage3Probe.dll ids 18000000 dense .tmp/memory-stage3/ids-new-dense.json
dotnet .tmp/memory-stage3/probe-new/MemoryStage3Probe.dll packs 1000 .tmp/memory-stage3/packs-new.json
```

旧版本将 reference/output 换为 baseline/probe-old，使用同一份探针源码。每次记录程序集位置与 hash，重型构建 / 测量串行运行。计时受 JIT、GC、OS page cache 影响；不能把单次结果当作稳定百分比承诺。

## 模式与口径

- `plans <ranges> <notes> <report> [midi-path]`：合成 Logical fixture，或显式提供 MIDI 文件（此时忽略 synthetic notes 参数）。每个范围冷访问后热访问，并比对 fingerprint / frame count；合成 fixture 另建立 100 个采样率计划，真实 MIDI 模式只测分页 realtime，避免隐式物化100份巨大离线计划。最后重访早期范围，验证淘汰后输出不变并记录重建代价。每32次强制GC后记录managed heap与所有权计账。强制GC只属于探针，单次冷/热耗时不含这些检查点。旧版先测128范围；不必为了证明线性增长跑到内存耗尽。
- `opaque <events> <payload-MiB> <report>`：三轮冷 / 热 ID 查找，模拟无自身 payload retention 的只读分页源，测 source 查询次数、payload 弱引用、累计分配及强制 GC 后残留。该压力源刻意不提供真实 pack 的 64 MiB decoded LRU；不能将它的热读退化直接当成普通小事件的开销。
- `packs <segments> <report> [notes-per-segment=1] [budget-MiB=64]`：记录未完成 tail 的 managed heap、实际容量、pending/reservation、spool 与最终文件 SHA-256。大于 1 条音符时混入 Channel / Opaque，按交错顺序写入以测试 spill 后正式顺序不变。对同一个 fixture 的旧 / 新 hash 必须相同。低于默认 64 MiB 的参数仅供压力测试。
- `ids <count> <dense|sparse> <report>`：三轮；反射仅建立入口委托，不逐条反射。`sparse` 的 ID 间隔是 2^20。**不要对旧实现运行 80k sparse**，其已知位图放大会占用约 10 GiB；旧 / 新对比用 1,000 sparse，新版另测 80k spill。

预算计账不是 heap walker；baseline canonical / Project source、活动消费者、可淘汰 cache 分开报告。合法超预算单对象不等于整项任务失败。Working Set、GC retained、累计 allocated、实际 rented capacity 是不同指标，不互相替代。

## 正确性回归

`./eng/MemoryStage3Probe/Run-Regression.ps1` 串行运行 10 个公共测试项目，加托管音频协议 / ring / limiter / WAV 写入的限定测试；每项使用独立输出目录，不覆盖用户正在运行的开发程序。它不执行依赖 native DLL、SoundFont、设备或 Native AOT 发布的集成测试。F05/F06/F08 及 Stable ID 专项测试已纳入相应测试项目。

结果目录必须是仓库内不存在的新目录。脚本遇到第一个失败即停止；修复后重跑或按报告列出明确的补跑记录，不将未执行的后续项目计为通过。
