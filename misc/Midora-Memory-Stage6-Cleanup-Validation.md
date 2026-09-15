# 阶段 6：异常清理专项与冻结基线反证

日期：2026-09-09。范围见 `Midora-Memory-Stage6-Cleanup-Design.md`。

## 候选验证

| 测试程序集 / 类 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| Playback / MemoryStage6CleanupTests | 3 | 0 | 0 |
| Application / MemoryStage6CleanupTests | 8 | 0 | 0 |
| Desktop / MemoryStage6ContextCleanupTests | 20 | 0 | 0 |
| 合计 | 31 | 0 | 0 |

- Playback 与 Desktop 本次使用 `--no-restore -c Release -p:BuildProjectReferences=false`，OutDir 分别为现有 `playback-targeted/`、`desktop-targeted/`，只重新构建测试程序集。日志没有生产项目构建；输出目录中 14 / 21 个生产 DLL 的 SHA-256、长度和最后写入时间在构建前后完全一致。
- Application 的 8 项结果来自主任务已执行的 `application-stage6-r3.trx`，逐例确认通过；这里不把该 TRX 中其他专项的结果计入本表。
- 外部 reader 的 Playback 弱引用测试，最初只有 canonical 对象未回收；将 `LastAttempt` 临时读取隔离到 NoInlining helper 后通过，生产缓存没有为此修改。最终测试还验证 disposed lease 的 owner/description 置空，并故意保留 disposed lease 对象至 GC 之后；活跃 reader 在会话异常关闭后仍可读取正式结果。
- Desktop 测试补齐 fake backend 的 RenderPositionFrames、package 的显式 softwareVersion，以及复用 Workspace.Header 避免主构造参数重复捕获；中间编译失败不计为测试通过。

候选证据（仓库相对路径）：

- `.tmp/memory-stage6/cleanup-verification-r1/results/playback-candidate.trx`
- `.tmp/memory-stage6/targeted-results/application-stage6-r3.trx`
- `.tmp/memory-stage6/cleanup-verification-r3/results/desktop-candidate.trx`
- 上述 cleanup-verification 目录中的 `*-production-before.json`、`*-production-after.json` 与 `*-candidate-guard/`。

## 冻结基线反证

从最终候选的完整测试输出复制三套全新隔离执行目录，仅替换本轮生产修改对应的 DLL：Playback 替换 Midora.Playback；Application 替换 Midora.Playback / Midora.Application；Desktop 再替换 Midora。替换来源为 `.tmp/memory-stage6/baseline/`，三个 DLL 的 SHA-256 与 `binary-snapshot.json` 一致。测试 DLL、其他依赖保持候选版本；不改写任一输入目录，不构建生产项目。

对每套目录独立、串行执行 `dotnet vstest`，仅筛选对应 MemoryStage6CleanupTests / MemoryStage6ContextCleanupTests。测试不依赖本轮新增的生产方法或字段，旧程序集全部成功加载并完整发现预期用例。

| 冻结基线组 | 通过 | 失败 | 跳过 | 主要反证 |
| --- | ---: | ---: | ---: | --- |
| Playback | 0 | 3 | 0 | 取消或 worker 故障后，LastAttempt 仍可访问，未完成会话清理 |
| Application | 2 | 6 | 0 | 逆序资源列表只释放 `[2]`，而非 `[2,1,0]`；多错误未聚合；history 未脱离 |
| Desktop | 0 | 20 | 0 | 首 Workspace / backend 故障跳过后续 owner；新 Project 已被接管后仍被误销毁；重复释放 owner |
| 合计 | 2 | 29 | 0 | 失败均为测试断言 / 注入清理错误，不是加载或发现失败 |

解释边界：

- Application 两个已发布 / source lifetime 转交控制用例在旧版也通过，确认其原有 ownership 语义未改变。
- Desktop 的无故障激活 / context 用例还断言第二次 Dispose 不重复调用 owner；旧版在该断言失败，不代表正常首次激活原来失败。
- History 多故障用例的旧版收尾可能再次抛出注入异常；逆序资源列表的精确顺序断言、Desktop 跳过 owner、以及已接管 Project 的 ObjectDisposedException 是更直接的独立反证。
- 这不是任意多线程同时关闭的验证。当前生产 ProjectContext 的两个真实 owner 都同步释放 Project 并返回已完成 ValueTask；不据测试 fake 的 Task.Yield 扩展关闭并发模型。

反证证据位于 `.tmp/memory-stage6/cleanup-counterproof-r1/{playback,application,desktop}/`：每组有 `binary-substitution.json`、`counterproof-result.json`、`results/*.trx` 与 `guard/`。三组 vstest 均按预期 exit 1；guard 的 stopReason 均为 null。

全部本次构建 / 测试按进程树 8 GiB 上限、系统可用内存至少 2 GiB 的 guard 串行执行，没有正式音频、物理设备、大样本、发布或 dist 操作。Playback 首组使用已有 Stage5 probe-tools-v7 guard，后续统一使用主任务指定的 Stage6 retention-probe guard；两者运行日志均记录硬 Job private commit 上限与上述 reserve。
