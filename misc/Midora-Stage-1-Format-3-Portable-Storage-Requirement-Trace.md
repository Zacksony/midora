# Midora 阶段 1：Format 3、原路径升级与 Portable Storage Requirement Trace

状态：源码实施与自动回归已完成，等待产品所有者人工验收

日期：2026-08-31

上位规范：SRS §3、§16、§17、§19、§20、§22；`Midora-Major-Editing-and-Visualization-Expansion-Implementation-Plan-2026-08-31.md` 的 `WP-00`、`WP-03` 与 `WP-09` Format 3 slice。

## 1. 输入与正式输出

- 输入：Format 1/2/3 `.midora` package、当前内存 Project source、当前 Project presentation snapshot、当前文件身份、显式 Save/Save Copy 请求，以及启动时冻结的规范化 `ProgramRoot = AppContext.BaseDirectory`。
- Format 3 正式输出：继续使用既有音乐 source 文件，并新增严格、确定的 `settings/project-presentation.json`；presentation 不参与编译、canonical fingerprint、音频缓存或 Undo/Redo。
- 旧格式打开输出：detached 的当前内存 Project + 默认 presentation，源文件在打开阶段保持逐字节不变，Project 保留原来源路径作为专用升级目标并标记需要保存。
- 旧格式普通 Save 输出：经明确确认后，先建立与原来源逐字节相同的可见永久副本，再以已严格重开的 Format 3 package 原子替换原路径；成功后会话转为普通已持久化 Format 3 会话。
- Portable storage 输出：Midora 自建正式程序数据只写 `<ProgramRoot>\Data\...`，可重建工作数据只写 `<ProgramRoot>\.tmp\...`；不探测、不读取、不迁移旧 `%LOCALAPPDATA%\Midora`。

## 2. 边界、不变量与失败条件

- Format 1/2 schema、descriptor 与 golden bytes 冻结；当前 writer 只写 Format 3。
- Project presentation 损坏只隔离该文件、恢复默认 presentation 并产生 Warning；音乐 Project 仍可打开且不因该恢复标记音乐 source Modified。
- Save 开始时冻结音乐 source 与 presentation revision；保存期间的新 presentation revision 保持 pending，不得被错误视为已保存。
- 迁移普通 Save 必须按顺序完成：冻结来源 identity → 构建当前格式临时 package → 严格重开 → 建立/复用精确原字节永久副本 → 复核来源 identity → 原子发布。任一步失败都不得改变原来源或会话路径/保存基线。
- 永久副本名称为 `<ProjectStem> - Original Format <old> before Format <current>.midora`；撞名使用稳定 `(2)`、`(3)` 后缀，已存在且字节完全相同的候选可复用。
- Save Copy 始终写 Format 3，不改变当前路径、migration-dirty、保护状态或保存基线；指向受保护来源路径时拒绝并引导使用普通 Save。
- `ProgramRoot` 必须是本机可写固定卷；`Data`/`.tmp` 的创建、写入、flush、原子替换、独占锁与删除能力在主窗口创建前探测。失败时明确阻止启动，不 fallback。
- `.tmp` session 目录必须有 owner manifest、独占 lease、直接子目录与 reparse-point 防护；只回收可证明由当前版本拥有且不活跃的目录。
- 单实例身份包含当前用户与规范化 ProgramRoot；不同 portable root 互相隔离。

## 3. 诊断、持久化与运行时归属

- 音乐 source validation/recovery 继续使用 package Diagnostics；presentation recovery 使用独立 Warning code，不创建 Damaged Placeholder。
- Project presentation 属于 Project package 的 presentation 数据，不属于 Project source；其 dirty/revision 独立于标题星号和关闭保存提示。
- Preferences、Recent、Catalogs、Presets、Diagnostics 属于 `Data`；AudioCache、SessionContent、CompilerRuns、AudioWorkerExchange 属于 `.tmp`。
- SoundFont、Project、MIDI 和导出目标是用户选择的外部文件，不复制到 portable data root。

## 4. 明确非目标

- 本阶段不实现 Onion Skin UI、All Tracks overlay 或其他新 presentation 编辑入口；只冻结容器、默认值、严格 reader/writer、损坏隔离和会话承载。
- 不实现 autosave、crash recovery、后台 Project rewrite、旧 `%LOCALAPPDATA%` 数据迁移、可切换 Data Root、UNC/network/removable storage、只读 portable 模式或传统 Save As。
- 不改变音乐 source、编译、canonical、播放、MIDI Export 或 Audio Render 语义。

## 5. 验证门

- Format 1/2/3 reader、deterministic Format 3 save、schema hash/golden、presentation strict JSON/dangling-reference/corruption isolation。
- 原路径升级的取消、成功、重复 Save、Save Copy、backup collision/reuse、来源 identity 改变、backup/publish/self-validation fault injection 与精确原字节副本。
- 普通当前格式 Save/Save Copy、metadata 时间、Undo/save baseline 与 package 原子事务不回归。
- ProgramRoot 路径、不可写/非固定卷/reparse point、能力探测、两份 root 的实例隔离、`.tmp` 活动 lease 与 stale cleanup。
- Preferences、Recent、Presets、SessionContent、AudioCache、Worker exchange 均不访问旧 `%LOCALAPPDATA%\Midora` 或 `%TEMP%\Midora`。
- Format 2 → 3 前后同一 Project 的 Full/Incremental canonical 结果保持一致。
