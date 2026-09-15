# 阶段 6：关闭异常的完整资源清理

日期：2026-09-09。性质：实现设计；不修改 SRS、音乐语义或持久化格式。

## Requirement trace

- 输入：用户关闭 / 切换 Project 后不再接受工作的编译会话、Playback / Task / History / Project owner。
- 正式输出：取消并收敛后台工作；逐项释放本会话拥有的资源与强引用；完整保留外部读者的 immutable result / lease。
- 边界：SRS INV-010/012/015/018/065/066、§12.21、§16.30 及内存执行计划 §11 的慢任务取消、异常清理、关闭释放。此项不调整活动音频线程、不增加音频线程分配，也不改变现有锁顺序、worker等待方式或消费语义。
- 失败：某个取消回调、worker、backend 或下层 Dispose 抛出时，仍须尝试后续独立清理。只有一个失败时重新抛出同一个原异常并保留堆栈；多个失败按清理顺序以 AggregateException 报告，不能静默吞掉。
- 持久化与运行时：只修改 runtime ownership；不影响 Project、Undo 内容、Modified、canonical、音频、MIDI 或 `.midora` 字节。
- 非目标：不把 Private / GC committed 差额直接判作泄漏；不增加后台事件容错/重启策略、不改跨线程调用模型，不扩展暂缓的 Int64 诊断或 F4 工作流。

## 已确认问题与修复范围

1. `ProjectCompilationSession.Dispose` 在置 `_disposed=true` 后取消并等待 worker，但只捕获 OperationCanceledException。`CompilationChanged` 订阅者异常可以直接使后台 worker fault，等待重新抛出后，后续 cache / canonical lease / mirror / audio cache / editing-time / compiler / CTS / semaphore 清理全部跳过；第二次 Dispose 又因 `_disposed` 直接返回。
2. 同一个方法中的取消回调或较早资源 Dispose 异常也不能阻止后续 cleanup。保留 `_disposed` 入场门与原 worker wait，逐项捕获清理失败，退出前统一报告。
3. Desktop 私有 `ProjectContext.DisposeAsync` 原顺序为 Tasks → Playback → Document → Compilation → Project owner，任一项抛错会跳过其余 owner。保持该释放顺序，每项独立尝试，最后等待 owner，汇总异常；成功或失败完成的关闭均不可重复释放。
4. 关闭完成后释放 `_compileWorker` 和已失效后台失败 / pending change /事件引用，避免外部短期保留 disposed session 时连带保留后台状态或订阅者图。公共 `Project` 仍是既有契约，保留 source Project 引用，不对共享 immutable source pages 调用 Dispose。

### 同一故障链的最小补齐

- `ProjectDocumentSession` 先把 history list 从当前会话 detach，清空当前 cursor，再按原正序逐 entry Dispose；异常不能跳过后续 entry，不复制完整 history 对象图。再次 Dispose 只看到空 history，不重放失败清理。
- `BoundedEditResourceLease` 仍先按既有嵌套规则退出 ambient lease，再一次性交出 provisional resource list；`BoundedEditPublicationResources` 仍先 exchange 私有 list。两者保持原逆序释放，逐项收集异常。`Complete` / `MarkPublished` / `RetainForSourceLifetime` 已转交 ownership 的对象不由退役 lease 提前释放。
- `DesktopSessionController.CloseProjectAsync` 仍先 detach context、退订并等待当前 refresh 收敛，再依序清理 raster cache、每个 Workspace、会话投影与前台任务，最后始终等待原 ProjectContext.DisposeAsync。Workspace 或通知 handler 的单个异常不能使后续 Workspace / Project owner 永远跳过。各层清理错误最后原样或按顺序聚合，不增加关闭重试、后台恢复、或自动丢弃用户数据策略。
- 顶层 Desktop Dispose 的 prepared backend 也属于本次已结束 session 的独立 owner：即使 CloseProjectAsync 报错也应尝试释放，并先解除其字段引用，避免后续重复清理。
- Project 切换与关闭使用同一 ownership 边界：`ActivateAsync` 完成旧 context detach 后，旧 Workspace roster 必须逐项清理，旧 context 必须最终释放；一个旧 Workspace 的 cleanup failure 不应跳过其他旧视图或阻止可行的新 UI 初始化，但仍必须向调用者报告失败。新激活步骤自身失败继续报告原错误，不视作成功。
- `CreateProjectAsync` / `OpenProjectAsync` / `AdoptMidiImportAsNewProjectAsync` 的局部 `next` 只在尚未被 `_context` 接管时由 finally 销毁。`AttachProjectContextToRefreshes(next)` 成功是 runtime ownership 转交点；后续旧 cleanup 或新 UI 刷新异常不应把已经成为当前 Project 的 `next` 再次销毁。当前新 Project 保留给用户检查/关闭，不能以悄悄返回成功掩盖错误。

修复只负责这些 owner 的清理编排。具体下层 Dispose 若自身在异常后未完成其内部释放，必须由相应 owner 独立核对；本项不能把“已调用一次 Dispose”夸大为任意原生错误下全部资源已释放。

## 小型验证门

- 无大负载的 Background session，在编译通知注入异常使 worker fault，Dispose须报告同一异常但仍清空 baseline/result/mirror并结束 editing-time；第二次Dispose不再次失败。
- 取消回调注入失败，worker仍被取消并收敛，CTS / semaphore与后续资源均执行清理。
- 冻结外部 immutable lease，在同样的失败关闭后仍可读，最后一个lease释放后精确计账归零；不提前销毁共享数据。
- Desktop 以小型fake backend / async owner注入独立失败，验证依序尝试所有owner、History清空、Compilation清理、owner只释放一次；多个原异常按顺序完整保留。
- History 前项、provisional/publication resource 逆序前项和 Workspace 前项分别注入错误，确认后续项仍精确调用一次；已发布 / 显式移交 source lifetime 的 resource 不被旧 lease Dispose。
- Project 切换注入旧 Workspace / 旧 context 释放错误，以及新 UI 初始化错误：旧 ownership 全部退役，原异常被报告，当前已接管的 next 仍可用；Create/Open/Import 的真实入口不得在finally误销毁它。
- 现有 SessionPreparationLifecycleTests / WorkspaceLifecycleTests / Background tests 保持正常关闭、弱引用释放、取消和新旧reader语义；由主任务统一构建和运行，不进行大负载或并行build。
