# Midora Portable ProgramRoot Architecture Decisions

状态：Accepted and implemented in stage 1（2026-08-31）

上位规范：SRS §16.34、§17.2、§20.14、INV-044、INV-086、INV-093～094。本文是实现决策；冲突时以 SRS 为准。

## ADR-PORTABLE-001：唯一 ProgramRoot

决定：`ProgramRoot` 是规范化的 `AppContext.BaseDirectory`，不使用 process current directory，不允许用户另选 Data Root。Midora 自建正式数据只进入 `Data`，可重建数据只进入可见的 `.tmp`；`.tmp` 不设置 Hidden 属性。

```text
Data/Preferences
Data/Recent
Data/Catalogs
Data/Presets
Data/Diagnostics
.tmp/AudioCache
.tmp/SessionContent
.tmp/CompilerRuns
.tmp/AudioWorkerExchange
```

原因：portable 副本的容量、移动、备份和清理边界对用户可见；不会静默消耗其他盘符的用户 profile 配额。

## ADR-PORTABLE-002：启动 fail closed

决定：主窗口创建前验证 ProgramRoot 位于 ready local fixed drive，ProgramRoot 与 owned subtree 均不是 reparse point；随后在 `Data` 与 `.tmp` 分别执行 create、write-through、flush-to-disk、same-volume atomic replace、exclusive lock 和 recursive owned-probe cleanup。

任一能力失败即明确阻止启动。禁止 fallback 到 `%LOCALAPPDATA%`、`%TEMP%`、current directory、用户 profile 或另一卷；禁止尝试读取或迁移旧 `%LOCALAPPDATA%\Midora`。

## ADR-PORTABLE-003：临时目录 owner lease

决定：CompilerRuns 与 AudioWorkerExchange 使用共用 `MidoraOwnedTemporaryDirectoryLease`：

- 每次任务只创建固定 root 的一个直接子目录；
- 目录名包含受限 purpose 与随机 GUID；
- UTF-8 manifest 同时绑定 magic、完整目录名和 purpose；
- 创建后持有 `FileShare.None` active lock 到任务释放；
- dispose 先释放 lock，再 best-effort 删除本目录；
- 启动/新 lease 只删除 manifest 完整匹配且 lock 已释放的普通目录。

SessionContent 与 AudioCache 可保留内容专用 manifest，但执行同样的 direct-child、active-lock、reparse 和 unknown-preservation 规则。裸 GUID 不再作为兼容垃圾回收目标。

## ADR-PORTABLE-004：路径注入与测试

决定：生产代码通过 `MidoraProgramData.Current` 获取唯一路径图；测试可以用显式 root/file path 注入，不修改进程环境或用户目录。Application Preferences schema v2 不序列化 Audio Cache Root；读取旧字段属于 unknown-field 错误而不是隐式迁移。

单实例 scope 使用 `SHA-256(current user + normalized uppercase ProgramRoot)`。因此同一用户的不同 portable 副本可并存；同一 root 的第二进程仍被拒绝。

## 失败与恢复边界

- Root capability probe 失败：不创建主窗口，不继续使用半可用配置。
- 单个 stale child 删除失败：保留并在下一次启动/lease 重试，不阻止其他合法 session。
- unknown/reparse/active child：永远保留，不猜测所有权。
- 用户选择的 Project、MIDI、SoundFont、导出文件：不属于 owned tree，不被清理器触碰。

## 验证门

- 路径图与两个 root 的 scope 隔离；
- `.tmp` 不带 Hidden；
- probe 不遗留文件；
- active lease、stale lease、unknown manifest、裸 GUID 和 reparse point；
- Preferences/Recent/Presets/SessionContent/AudioCache/Compiler/Worker 的默认路径扫描；
- 生产源码不得重新引入 `LocalApplicationData` 或 `GetTempPath()` 作为 Midora-owned store。
