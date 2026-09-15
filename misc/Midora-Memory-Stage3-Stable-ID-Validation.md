# 阶段 3 / M10：有界 Stable ID 校验

状态：实现完成，专项测试 18/18、Persistence 完整回归 129/129 通过，独立基线对比完成；跨模块结果由阶段 3 总报告收口。不是格式或 ID 语义变更。

## 需求追踪

- 输入：保存预检 / 打开严格校验按原顺序枚举的全包 Stable ID，以及 `nextStableId`、原来源类别。
- 输出：所有 ID 保持正值、低于 allocator 高水位且全包唯一；重复时沿用原来源类别和原错误文本。跨 Track、跨文件、隐藏内容仍被校验。
- 依据：SRS §16.5.3、§16.13.2、§16.17～18，INV-040、065、088、093；六阶段计划 §8 / M10。
- 失败：取消、重复、非法 ID、外排 I/O / extent / 排序错误均阻止发布。正常 Save 的目标字节不变；取消不转为损坏诊断。
- 归属：只属于一次持久化校验的可释放工作状态，不进入 Project、Undo、canonical、presentation 或文件格式。
- 非目标：不改变 Stable ID、高水位、不省略严格校验，不修改冻结 Format 1/2/3 wire，不引入对象数量拒绝上限。

## 表示与预算

原实现每覆盖 `2^20` 个可能 ID 就分配 `128 KiB` 位图，即使 bucket 内仅有一条。新实现按 `2^16` 范围分块：

1. 少量 ID 以排序 `ushort[]` 存储，相邻递增分配走 append 快路；数组由 4 项按需倍增。
2. 超过 4,096 个唯一项时转为 `ulong[1024]` 位图。此处 sparse capacity 与 bitmap 都为 8 KiB，不再因单个远距离 ID 分配整幅位图。
3. block 目录使用可计 capacity 的开放寻址数组；连续输入缓存上一次 block。
4. 默认每个 validator 的预算为 **8 MiB**。所有数据数组按实际 capacity / x64 element size 计账，包含扩容时 old+new 同时存活；固定对象与最多 64 个 run 的路径/元数据使用保守控制额度。
5. 预算预留 `32,768 × 24-byte` 排序记录区、最多三个 64 KiB 文件 buffer 和控制额度。预留使 sparse → spill 的转换也不临时越过同一预算。`AccountedResidentBytes` / `PeakResidentBytes` 是分配结构计账，不是整个 GC heap 或 Working Set。

8 MiB 不限制可校验的对象数；超出只切换算法。18M 密集 ID 测试确认保持内存路径，不落盘；实际计账与 GC retained 见下表。

## 外排、错误来源与生命周期

- 达到预算后将已验证的 resident prefix 和后续输入写入有序 runs；固定记录为 `{Int64 ID, Int64 scanOrdinal, Int64 sourceCategory}`。resident prefix 已完成在线查重，其 ordinal 为 `-1`，无需保存第一处出现的完整对象 / 字符串。
- 每个完整 chunk 排序并去重，通过 64 槽二进制归并层合并，至多两个 reader 和一个 writer 同时打开。不建立随 run 数增长的文件目录集合。
- 重复记录保留最早 occurrence；额外记录的 scanOrdinal 用于选择原输入扫描顺序上最早的第二次 occurrence。因此不会因 ID 排序而改报“数值最小的重复”。遇到后续非法 ID，先完成此前已收集 ID 的重复校验，使更早重复仍优先。
- `Complete()` 必须在持久化预检 / loaded validation 返回前执行；最终 run 再顺序验证 extent、单调 ID、ordinal 和来源范围。
- 外排只在 `<ProgramRoot>\.tmp\CompilerRuns\stable-ids-*` 创建 owned directory。使用既有 manifest / active lock 机制，校验方法的 `using` 在成功、异常、取消时释放并删除自身目录；异常退出的残留按既有启动回收规则清理。
- `LiveSpillBytes`、`PeakSpillBytes` 和累计写量单独计账。当前源 run 加正在产生的 output 至多保留每个已接收 ID 的两份 24-byte record（不含小型 manifest/锁文件），即有效记录存储峰值不超过 `48 × inputCount`。旧 input 只在完整 merge 输出成功后删除。磁盘不是固定大小缓存，但其活跃占用有输入相关明确上界，不会因归并历史无限累积。
- 排序块前后、枚举及 merge 每最多 256 条检查取消。I/O 在保存/打开准备线程执行，不进入音频活动线程。

## 验证覆盖

专项测试包括 1,000 sparse / 18M dense / 80,000 sparse，固定 8 MiB 与缩小测试预算；极大 ID、跨 run / prefix 重复、原扫描来源优先、零 / allocator 边界、排序/extent 损坏、spill root I/O 失败、取消和幂等释放。

集成测试对两个 Track 的重复 ID 检查 Save 预检与 loaded validation 两条正式入口，覆盖普通小对象和 80,000 稀疏分页 Note；校验失败不能改写已存在目标文件。与既有持久化 frozen descriptor / golden / Save Copy / migration / 损坏隔离用例合跑后，Persistence 全套 129 例通过（`.tmp/memory-stage3/regression-final/Midora.Persistence.Tests.trx`）。

## 专项实测（2026-09-08）

命令：`dotnet test src/midora-core/Midora.Persistence.Tests/Midora.Persistence.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~StableIdValidatorV1Tests`。18 例通过、0 失败、0 跳过；TRX 位于 `.tmp/memory-stage3/ids-tests/stable-ids.trx`。

| 输入 | 校验用时 | 计账 resident 峰值 | 外排 |
|---|---:|---:|---|
| 1,000 稀疏 ID，间距 `2^20` | 未单独计时 | 165,040 B | 无 |
| 18,000,000 密集 ID | 751.76 ms | 2,341,280 B | 无 |
| 80,000 稀疏 ID，间距 `2^20` | 128.29 ms | 8,388,536 B | live 峰值 3,840,000 B；累计写 5,412,864 B |

8 MiB 为 8,388,608 B；80,000 稀疏例在转换、排序、归并全程的计账峰值仍低于预算，其 disk live 峰值恰为 `48 × 80,000`。正常密集样本保留纯内存 bitmap 路径。

这些是单次专项测试的 validator 用时，不是完整项目 Save/Open 时间，也不能直接作为旧/新时间提升比。统一探针 `eng/MemoryStage3Probe` 已增加 `ids <count> <dense|sparse> <report.json>`，同一 harness 分别链接冻结旧/新程序集，每组 3 轮报告中位数、post-GC retained 与累计分配。调用旧私有容器和新 validator 都使用 typed delegate，不以每 ID reflection Invoke 污染计时。旧实现不得用 80,000 稀疏样本强行验证，以免已知约 10 GiB 位图分配影响工作机。

## 冻结基线对比（2026-09-08）

统一探针读取冻结基线和新实现，分别运行同一输入各 3 轮；原始证据是 `.tmp/memory-stage3/ids-{old,new}-{dense,sparse}.json` 与 `ids-new-spill.json`。以下时间为每组中位数；GC retained 列为完成后、释放 validator 前的托管存活量，不是 Working Set。

| 输入 | 旧时间 | 新时间 | 旧 GC retained | 新 GC retained |
|---|---:|---:|---:|---:|
| 18M 密集 ID | 225.13 ms | 280.70 ms | 2,360,920 B | 2,280,208 B |
| 1,000 稀疏 ID | 21.13 ms | 0.63 ms | 131,150,944 B | 107,512 B |
| 80,000 稀疏 ID | 不运行已知超大分配 | 172.18 ms | 不测 | 788,944 B |

密集路径存在约 **55.57 ms / 24.7%** 的该步骤时间增加：自适应容器和预算检查不是零成本，且块晋升使累计分配从约 2.36 MB 增加到 6.87 MB，但这些临时数组不会全程驻留。该数字不能套用为整个保存/打开流程增加 24.7%。稀疏路径则消除了每个孤立 ID 配置 128 KiB 的放大问题。80,000 稀疏例的 resident 计账峰值为 8,388,536 B、disk live 峰值 3,840,000 B，仍在上述预算内；不同探针目录长度使保守路径控制额度略有不同。
