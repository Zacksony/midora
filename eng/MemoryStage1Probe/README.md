# M01 可重复只读内存 / 保存探针

这是测试工具，不进入产品发布，不启动 WPF 或音频 Worker。与 xUnit 正确性门配套使用，不能替代完整 UI 响应时间、后台编译或音频测试。

## 构建与运行

从仓库根目录在 PowerShell 执行：

```powershell
dotnet build src/midora-core/Midora.Application/Midora.Application.csproj -c Release -p:CopyLocalLockFileAssemblies=true
dotnet build eng/MemoryStage1Probe/MemoryStage1Probe.csproj -c Release -t:Rebuild -o .tmp/memory-probe/current
dotnet .tmp/memory-probe/current/Midora.Persistence.Tests.dll edited '<sample.mid>' '.tmp/memory-probe/run-01'
```

每次必须传入**不存在的输出目录**；源 MIDI / 项目只读，不覆盖原件。探针产生 memory.csv、saved.midora、copy.midora；stdout 请另存日志。包 / pack 较大，不提交 Git。正式基线应从具体 Git commit 导出到独立目录构建，不要 checkout / reset 当前工作区；只需导出 `src/`、`eng/`、`Directory.Build.props`、`.editorconfig`、`global.json`，不依赖未提交改动或 `dist/`。

对比构建通过 `-p:MemoryProbeReferenceDirectory=<absolute-baseline-Application-bin-directory>` 指定。该目录同样须以 `CopyLocalLockFileAssemblies=true` 构建，以携带 Protobuf 等运行依赖。`-o` 使用不同目录，保证 baseline 和 current DLL 不互相覆盖。切换 reference directory 时使用 `-t:Rebuild`，避免共享 obj 中的引用解析缓存沿用上一构建的依赖图。测试工具借用 Persistence.Tests friend assembly identity 只为事务阶段诊断；不是产品程序集，也不引用 xUnit。

## 工作负载

| 模式 | 操作 |
| --- | --- |
| `save` | 导入后不编辑 → 普通 Save（包含重开自校验） |
| `roundtrip` | 60k Note 右 Resize / Move → Prepare / Commit / Undo / Redo / Undo → 取消一次准备 → presentation 设置 → Save → Save Copy → Open |
| `edited` | 同 roundtrip，但最后再正式提交一次 Move 并保留修改，强制覆盖 edited content pack 写入路径 |
| `open` | 打开给定 `.midora`，检查已恢复对象，记录活项目和退出后的内存 |

小于 60k 的轨道取实际可用数量。roundtrip / edited 使用音符最多的 Segment，确定性选择其查询顺序前 60k 条。具体操作正确性与裁剪外内容由对应 xUnit / 包字节对照测试校验；大样本探针另外断言保存重开后 Note / Channel / Opaque / Track / Segment 数量、ID 高水位与无损坏诊断，不能把这些数量断言说成逐事件等价验证。

## 测量口径

- 首行记录运行时、Workstation / Server GC、逻辑处理器数、系统、样本路径 / 长度 / SHA-256，以及实际 Domain / Persistence / Application DLL 路径和 SHA-256。样本哈希在 import-start 之前，不计入导入时间。
- memory.csv 每约 100 ms 采集 WS、Private Bytes、Peak WS、当前托管估计、最近 GC heap / fragmentation / committed、累计分配和 GC 次数。结束时间还包括显式释放验证，不与产品操作时间混算。
- 所有正式阶段输出三种 record collection 的 facade 表计数。只读扫描不得令这些计数随源记录总数增长。
- 保存阶段：save-start → BeforeContentWrite 是 preflight；随后到 BeforeZipWrite 是内容构建；到 BeforeSelfValidation 是 ZIP 写入；到 BeforePublish 是严格重开自校验；到 save-return 为发布与清理。Save Copy 同理。
- 背景编译 debounce 固定 1 小时以隔离 M01。没有播放、WPF、渲染瓦片、音色库或真实 UI 输入；本探针的 Undo 返回时间只覆盖 document command，不代表屏幕刷新完成。
- 默认正式操作期间不强制 GC；全部 owner 释放后才做受控 GC 和 WeakReference 检查。`--gc` 会在事务钩子插入诊断 GC，**只能用于存活量归因，不可混进正式耗时结果**。
- Private Bytes 超过 14 GiB 时请求取消，避免基线继续扩大；这不是产品预算。样本或平台不支持及时取消的某段仍可能晚些退出。失败 / 被取消运行不得混入成功均值。
- Before / After 交替、串行运行；不要同时跑构建、大测试、音频或另一个性能探针。每类至少 3 次，报告中位数和范围；首次进程与同位置重复都保留。首次进程不等于冷文件系统缓存，本工具不清系统 standby cache。

逐包正式内容比较可运行同目录 `Compare-Content.ps1`（PowerShell 7）；它流式比较解压后的包 entry SHA-256。metadata 仅排除每次新导入自然不同的创建 / 修改时间及会话时长，其他属性也须相同；忽略引用其 checksum 的 manifest，不忽略 Project / Track / content pack / presentation 内容。Manifest 的严格性和格式由产品自校验及持久化 golden / fault tests 独立验证。

将每次 stdout 命名为 `<1m|18m>-<roundtrip|edited|save>-<before|after>-<repeat>.log`，与去掉 `.log` 的同名输出目录放在一起，即可用 `Summarize-Results.ps1 -ResultsDirectory <directory>` 提取各次结果、中位数与范围。未完成、失败或释放后仍保留 Project 的运行会使汇总失败，不静默混入成功统计。Private / managed peak 是 100 ms 采样峰值；Peak WS 是操作系统报告的进程高水位，二者测量口径不同。

`Run-Workloads.ps1` 可自动串行、交替执行该命名的矩阵。传入两个 probe build directory、`OneMillionMidi`、`EighteenMillionMidi` 和仓库 `.tmp` 下不存在的 `ResultsDirectory`；默认各模式 3 次，可用 `-Modes save -Repeats 1` 补测。每次启动前检查目标盘余量，单进程默认 15 分钟超时；只终止自己启动的超时测试树，保留部分结果并停止整批，不清理其他进程或目录。超时运行不计成功。

## 阶段界限

此工具固定阶段 1 持久化与 readonly 基线。完整操作索引见 `misc/Midora-Memory-Optimization-Coverage.md`。后续阶段的 WPF 生命周期、payload 字节预算、builder、音频计划、历史和 Compiler 展开内存还需各自工程门，不能从 M01 的较低峰值推论它们已修复。

`Run-Regression.ps1` 串行运行阶段 1 公共正确性门并保留逐套件 TRX；依赖须先按仓库常规步骤 restore。该脚本不配置外部样本、WPF opt-in 或 BASS 环境，也不发布 Worker / `dist`。需要真实样本的 opt-in 测试是否执行，须在报告另行列明，不能只看套件 passed 总数。性能探针与回归脚本不要同时运行。
