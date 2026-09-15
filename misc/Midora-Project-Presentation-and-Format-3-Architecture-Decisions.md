# Midora Project Presentation and Format 3 Architecture Decisions

状态：Accepted and implemented in stage 1（2026-08-31）；Onion UI 尚未实现

上位规范：SRS §3.11、§16.7.5、§16.24、§16.33、INV-088、INV-090～092。本文是实现决策；冲突时以 SRS 为准。

## ADR-FORMAT3-001：只增加 presentation entry

决定：当前 writer 使用 Project Format 3 / manifest schema v3。相对 Format 2，唯一新的 package payload 是 `settings/project-presentation.json`，manifest kind 为 `project-presentation-json`、schemaVersion=1。

Format 3 复用 Format 2 的 Event Instrument protobuf v2，以及 Format 1/2 已冻结的其余 JSON/protobuf/content-pack。V1/V2 reader、schema、descriptor 与 golden 不修改、不双写、不由 V3 codec 猜测替代。

## ADR-FORMAT3-002：presentation 与音乐 Source 分离

决定：Application 层的 `ProjectPresentationSessionV3` 独立拥有 state、revision、saved revision 和 recovery dirty。它不进入 `MidoraProject`、Project edit command、Undo/Redo、compile change set、canonical fingerprint 或音频缓存 identity。

普通 Save 同时冻结 Project source 与 presentation snapshot；成功后分别提交各自 baseline。保存期间产生的新 presentation revision 不能被旧 snapshot 错误清除。Save Copy 写出冻结 presentation，但不更新当前 presentation baseline。

首个 state 只冻结后续 Onion 所需结构：Track targets/sources、SubVoice targets/sources、enabled、opacity 与 All-Tracks raw/compiled mode。Stage 1 不提供编辑 UI。

## ADR-FORMAT3-003：presentation 损坏隔离

决定：manifest 本体仍是结构关键文件；但 presentation entry 的缺失、hash、strict JSON、schema、range 或 dangling reference 错误都只恢复 `ProjectPresentationStateV3.Empty`，产生独立 Warning，并令 presentation recovery dirty。音乐 Project `IsModified`、Damaged Placeholder 与 canonical 均不受影响。

保存 snapshot 会丢弃 target 已删除的 preset，并从 source list 过滤已删除、自引用和重复项；保存后的 JSON 再通过严格 codec 完整验证和确定排序。

## ADR-FORMAT3-004：Format 1/2 detached migration

决定：Open 先按 manifest header 分派冻结 reader。Format 1 通过既有明确规则补 `Pre-Roll Ticks=0`；Format 2 保留原值；两者都附加空 presentation。候选完整验证后才提交活动会话。Open 不写来源、不创建备份。

迁移会话不建立 CurrentProjectPath，而是保存 protected source path 与冻结 identity：absolute path、source format、length、last-write UTC 和完整 SHA-256。普通 Save/Save Copy 不得绕过该保护去覆盖来源。

## ADR-FORMAT3-005：确认后原路径升级

决定：普通 Save 先调用 prepare，确定并展示永久副本路径；用户确认后把同一 plan 传回事务。执行顺序是：

```text
verify and lease legacy source
build Format 3 staging content
write deterministic zip
strict reopen/self-compare
create or reuse exact-byte permanent backup at confirmed path
atomic replace original path
commit document + presentation baselines
```

永久副本与来源同目录，名称保留明确的 source/target Format；使用 Unicode text-element 安全截断和稳定 `(n)`。候选已存在且 SHA-256/length 与来源相同则复用；内容不同则 prepare 选择下一候选。确认后候选被抢占时事务失败并要求重新确认，不能静默改名。

发布前 source stream 以拒绝 write/delete sharing 的方式持有；length、mtime 和 SHA-256 必须匹配打开 identity。副本写入使用 write-through、flush-to-disk、完整 SHA-256 校验后无覆盖发布。副本完成后才释放 source lease并立即执行同卷 `File.Replace`。

## 失败原子性

- prepare/cancel：零写入；
- source identity/lease 失败：Preflight，零副本、零覆盖；
- staging/self-validation 失败：清理 staging，来源不动；
- backup 失败：来源不动；
- publish 失败：来源仍为旧格式，永久副本与验证过的 temporary package 保留供诊断/重试；
- publish 成功、cleanup 失败：保存成功并报告 Warning；
- 会话状态只在磁盘发布成功后切换为 persisted Format 3。

## 验证门

- V1/V2/V3 reader 与 deterministic V3 package golden；
- presentation non-empty round-trip、strict rejection、damage isolation；
- V1/V2 migration 的 source/canonical 等价；
- source mutation、backup collision/reuse、confirmed-path race；
- backup、staging、self-validation、publish、cleanup fault injection；
- Save Copy 不改变 migration/document/presentation baseline；
- Desktop 普通 Save 路径显示 backup 并完成原路径升级。
