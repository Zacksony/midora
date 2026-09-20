# B2：工作区状态持久化与保存读取契约

初稿：2026-09-18；人工验收归档：2026-09-20。状态：**B2 已完成代码实施、自动验证与 UAT-B2-01～06 人工验收；B1 已获用户验收并已提交；B3 未实施** 。

本文件是 B2 的执行计划与实施记录，不替代用户在 [04 决策问答](../04-Decisions-and-Preparation.md) 中已经给出的原答。B2 只处理独立 presentation 的保存、读取、损坏隔离、Save/Save Copy 和运行期 Mute/Solo 恢复；B3 才接通全部 Tabs、子页和隐藏 Workspace 的自动激活恢复。

入口：[总执行计划](00-Execution-Plan.md)、[B1 与 B2/B3 边界](05-Workspace-State-and-Track-Navigation.md)、[B3 完整恢复计划](06b-Workspace-State-Restore-B3.md)、[R06/R07 源码与需求调查](../03-Multi-Instance-and-Workspace-State.md)、[presentation 历史 ADR](../../Midora-Project-Presentation-and-Format-3-Architecture-Decisions.md)。

## 1. 目标、输入、输出和非目标

### 1.1 目标

B2 应使 B1 已能在一个会话内记忆的纯值状态可以被显式保存并在重新打开同一 Project 后安全读取：

- Track profile：同 Track 共享的缩放、Grid/Snap、主工具、事件绘线形态、默认创建值、Lanes/List 布局；
- Segment 局部状态：世界坐标滚动位置、活动/隐藏/顺序 Lane、每 target 纵轴、对象 List 行位置；
- SubVoice 独立状态：Definition + SubVoice 身份下的 profile、局部 viewport、Lane 状态；
- 已有 Onion / All Tracks 状态，随 presentation schema 3 继续读写；schema 1/2 仍可读；
- Track 与独立 Usage/Root 的运行期 Mute/Solo 初值。

这些值进入独立 presentation revision，不进入 Project source、Undo/Redo、canonical、音频缓存、MIDI/WAV 正式输出或音乐 Modified。B2 读取后先交给 B1 registry；B3 负责把 Workspace 集合、Tab 顺序、活动子页等完整导航状态自动激活。

### 1.2 正式保存路径

B2 延续当前 `.midora` package 和外层 Project Format 4。已按 D01 将 `settings/project-presentation.json` writer 升为 presentation schema 3，保留 schema 1/2 reader；`workspaceState` 内的 profiles/views/lanes/monitoring 是 schema 3 的严格分区。B2 不把新字段追加到 schema 2，也不倒退 Project Format 4。

保存链保持：

```text
WorkspaceStateRegistry / runtime Mute-Solo
→ typed presentation snapshot（冻结 revision）
→ strict canonical JSON + manifest hash
→ package staging / self-validate / atomic publish
→ presentation baseline 更新
```

`Save Copy` 使用同一冻结 snapshot 写目标副本，但不改变当前 Project 的 source/presentation saved baseline。纯视图变化可以显式 Save，不增加音乐标题星号，也不把纯视图变化放进音乐 Undo；关闭时仍按已批准的“纯视图不新增关闭确认”规则处理。

### 1.3 明确不在 B2

- 不自动恢复 Tabs 的创建、顺序、活动页、Event Instrument 当前子页或所有隐藏 Workspace；这些属于 B3。
- 不恢复 Selection、Time Range、鼠标/拖动状态、浮动工具锚点、Undo/Redo、任务、播放/试听、编译结果、缓存位图、WPF VM 或后台任务。
- 不实现多实例、跨项目文件剪贴板、Tracks 侧栏、R30；这些分别属于 C/独立 TRACK。
- 不把 Mute/Solo 写入 Project source 或 canonical，也不为了保存它们修改音频编译和导出内容。
- 不改变 Format 1/2/3 音乐数据、旧格式迁移副本保护、SF2/SFZ、音频后端或 MIDI 编码边界。

## 2. 已批准的外部语义与必须保持的不变量

### 2.1 Save 与 revision

1. `Project source revision` 与 `presentation revision` 独立计数；只改变视图时仍可显式保存 presentation。
2. Save 开始时一次冻结 source 和 presentation snapshot。Save 期间发生的新视图变化不能被旧 snapshot 的成功结果清除：只有与 snapshot revision 相等的部分标记为 saved，较新的 revision 继续保持待保存。
3. Save Copy 携带同一冻结 presentation，但不更新当前文件路径、source baseline 或 presentation baseline。
4. 写入失败、取消、超限、验证失败或目标竞争不得部分替换 Project；旧文件继续可读，临时 staging 按故障策略保留或清理。
5. 纯 presentation 失败不能阻碍音乐 source 加载；恢复到可用的 presentation section 时必须给出独立 Warning，并留下 recovery-dirty 状态供下一次显式 Save 处理。

### 2.2 身份、顺序与校验

- 使用稳定 ID；名称、轨道位置、Tab 序号、Segment tick 不能代替身份。只有在同一冻结 Project revision 内，显式的 Tab ordinal/排序值才表示顺序。
- JSON 字段、数组顺序、浮点格式、section 顺序和 manifest entry 顺序必须确定；写入同一 source/presentation snapshot 两次得到相同字节。
- 未知字段、重复字段、错误类型、缺失必需字段、重复 owner、重复 target、非法枚举、非有限浮点、超出范围的整数和 section 之间的非法引用必须被 codec 拒绝或按 section 隔离，不能静默猜测。
- `schemaVersion` 必须同时存在于 JSON 和 manifest，并且两者一致；未支持版本不能按最新版本猜读。
- Save 前按当前 Project 过滤删除 owner 的状态；存活 owner 的 dormant Onion source ID 可以保留在会话中，但悬空引用不得写入文件。

### 2.3 恢复顺序

1. 先验证 manifest、source entry 和 Project candidate；候选 Project 成功后，再解析 presentation。
2. presentation 各 section 独立验证和导入。坏 section 回退该 section 的默认空值，其他合法 section 继续可用；不能因为一个无关的 Lane 状态损坏而丢失全部 Onion 状态。
3. presentation 值导入 B1 registry 时必须使用通知抑制和 owner/Project epoch 检查；旧回调、旧项目和旧 snapshot 不能写入新会话。
4. Project 首次恢复状态为 Stopped；Mute/Solo 在首个播放计划建立前接入运行期过滤。不得自动播放、试听、Seek 或触发编译全量重建。
5. B2 不主动创建 Tab/VM。用户之后手动打开对象时，B1 adapter 消费已恢复的 profile/local/Lane 值；B3 才负责把保存的 Workspace 描述懒激活成 Tab。

## 3. B2 持久化分区建议

以下分区已经冻结并写入 schema 3 的 JSON schema、source-generated DTO 和 golden/round-trip 测试；B3 的 futureWorkspace 不写入文件。

| 分区 | B2 保存内容 | 不保存 | 损坏时回退 |
|---|---|---|---|
| `onionAndAllTracks` | 现有 Track/SubVoice Onion 目标、来源、opacity、Custom/Previous/Next、All Tracks Raw/Compiled | 音符、编译索引、位图、后台任务 | 该分区的空状态，并提示 Warning |
| `trackProfiles` | Track ID、Piano/Event settings、zoom、ToolMode、ValueTraceShape、Lanes/List 布局、默认创建值 | TPQN/拍号解析图、实际像素缓存、VM | 该 Track 使用 B1 默认 profile |
| `segmentViews` | Segment ID、StartTick、FirstLane、FirstRow、活动 Lane 及 target 排序/显隐/纵轴 | Selection、source 页、行 VM、缓存 | 该 Segment 使用局部默认 |
| `subVoiceViews` | Event Instrument ID + SubVoice ID、同类 profile/local/Lane 值 | 预览 held keys、试听句柄、草稿 | 该 SubVoice 使用默认 |
| `monitoring` | Track 与独立 Usage/Root 的 Mute/Solo，按正式 owner ID 保存 | 成员状态改写、canonical 音乐过滤结果、播放位置 | 未静音/未独奏并提示 Warning |
| `futureWorkspace`（B3预留） | 不在 B2 写入未消费的 Tab/子页布局 | 伪造默认 Tab、未实现字段 | B2 不读取、不产生假 UI |

schema 3 已将 schema 2 的 Onion/All Tracks 语义无损搬入，并新增 B1 分区；B2 不写入尚未有恢复消费者的 B3 字段。

## 4. 细化工程任务

稳定编号沿用总计划的 T-STATE-04～06，不因拆分重编号。每项均需记录源码落点、测试文件、失败证据和未测边界。

### 4.1 T-STATE-04：codec、schema 与预算

| 子任务 | 内容 | 完成门 |
|---|---|---|
| T-STATE-04a | 依据 §2/§3 冻结 typed snapshot 与 section DTO；区分 B1 registry 值、B2 wire DTO、B3 未消费状态；保持稳定 ID 只在数据中出现，不在 UI 展示 | 领域/桌面 VM 不进入 persistence 引用图；DTO 无 View、Task、source factory、bitmap、CancellationToken |
| T-STATE-04b | 完成 presentation schema 新版本、manifest entry、source-generated JSON context、严格字段/重复字段/数组顺序/数值范围校验 | schema1/2 golden 仍能读；新 writer deterministic；未知版本/字段/重复 key 明确失败或 section 隔离 |
| T-STATE-04c | 建立每 section 与总包的字节预算实验：空状态、1/10/100/1000 Track、Segment、SubVoice、Lane target、最大合法 ID/排序、Onion source 列表、Mute/Solo 全量 | 冻结总字节、单 section 字节、单数组长度、单字符串/列表长度、解析时间和 peak allocation；不得用“正常项目很小”代替上限 |
| T-STATE-04d | 为过大/重复/深度异常/集合扩张建立拒绝策略和用户可读诊断；保存端不静默截断，读取端不把截断数据当完整状态 | 超限时按批准的“保存音乐及可用视图部分/取消”流程；既有状态不被隐式淘汰 |
| T-STATE-04e | 更新 SRS §16/§17、INV 与 presentation ADR，明确 schema 版本、section 恢复、预算及旧软件行为 | 规格、JSON schema、manifest、DTO、golden bytes 四者一致；无代码默认偷偷超越文档 |

### 4.2 T-STATE-05：Save、Save Copy 与原子发布

| 子任务 | 内容 | 完成门 |
|---|---|---|
| T-STATE-05a | 将 B1 registry、Onion presentation、Mute/Solo 组合为一个有界纯值 snapshot；Save 开始时冻结 source/presentation 各自 revision | 视图变化不会扫描音符或持有 VM；快照引用不可被后续编辑改变 |
| T-STATE-05b | 接入普通 Save：允许纯视图保存，正确更新 presentation baseline，不改变音乐 Modified/Undo；标题、Save 可用状态与关闭行为符合 §2.1 | 纯视图 Save、纯音乐 Save、两者同时改、无变化 Save 的状态转移 golden 全通过 |
| T-STATE-05c | 接入 Save Copy：使用同一冻结 snapshot，目标文件可独立打开，当前会话 baseline/path/legacy protection 不变 | Copy 成功/取消/覆盖拒绝/写入失败均不改变当前会话；源与副本字节/状态符合规则 |
| T-STATE-05d | 处理 Save 期间 view/source/presentation 并发变化；旧 snapshot 成功后只清理对应 revision | 用可控 barrier 在 package staging、hash、replace 前后修改状态；新 revision 仍要求再次 Save |
| T-STATE-05e | 复用 package staging、manifest hash、self-reopen、atomic replace；所有临时文件限于 `<ProgramRoot>\\.tmp` 并可取消清理 | 进程终止/取消/磁盘满/锁冲突/目标被替换/flush 失败不留下半个可发布 Project |
| T-STATE-05f | 接入 Format 1/2/3 detached migration 与现行 Format 4；升级时保留已确认的可见永久副本和来源 identity 保护 | 旧 source 仍逐字节备份，原路径覆盖只走已确认升级事务；新 presentation 不损坏旧音乐数据 |

### 4.3 T-STATE-06：读取、隔离恢复与监听初值

| 子任务 | 内容 | 完成门 |
|---|---|---|
| T-STATE-06a | Open candidate 成功后解析 presentation header/manifest；缺 entry 按空 presentation 处理，schema1/2 转成当前内部模型 | 缺失旧字段使用明确默认；不把 Format/Schema 数字混为一谈；旧 reader/golden 不回归 |
| T-STATE-06b | 实现 section-level strict validate/import：owner、target、排序、轴、Snap、Lane key、Mute/Solo 各自校验；任何坏 section 只回退自身并产生独立 Warning | 构造坏 JSON、重复 key、未知字段、错误 hash、dangling ID、NaN/Infinity、超限数组；音乐仍可打开、编译/导出不受影响 |
| T-STATE-06c | 将有效 section 原子导入 B1 registry；先导入纯值，再由 B3/手动 Workspace 消费；阻断迟到旧项目事件 | Open 失败保留旧 Project；Open 成功替换后旧 callback、旧 owner、旧 revision 均无效 |
| T-STATE-06d | 恢复 Track/Usage/Root Mute/Solo 到运行期过滤层，首个播放计划前生效；不改 source/canonical/MIDI/WAV | 逐层 mute、solo、组与成员组合、未保存改动、打开后首播/停止/重建 Worker 全部符合既有音频语义 |
| T-STATE-06e | 记录 recovery-dirty/diagnostic 生命周期；用户显式 Save 时可重新发布合法可用 section，不能在打开时自动改写文件 | 关闭/重新打开/Save Copy 后状态和提示确定；Warning 原文不丢失、不重复泛滥 |

### 4.4 T-STATE-07/08 的边界（本轮不实施）

T-STATE-07（B3）才把保存的 Workspace descriptor 懒创建为 Tabs、恢复顺序/活动项、Event Instrument 子页、Arrangement/Conductor/All Tracks 的 viewport 和焦点。T-STATE-08（B3）才做几十/数百隐藏页、长会话、懒加载并发、WPF/native/后台任务释放和真实冷启动时间门。本 B2 不通过“DTO 已含字段”宣称 B3 完成，也不为 B3 预建 VM 或索引。B3 的具体子任务、恢复时序、自动门、UAT 和进入实现前决策见 [06b-Workspace-State-Restore-B3.md](06b-Workspace-State-Restore-B3.md)。

## 5. 失败、损坏与资源边界

### 5.1 读取边界

- manifest 缺失/不一致、source 结构损坏：按既有 Project 打开失败，不得用 presentation 修复。
- presentation entry 缺失：音乐正常打开，使用空 presentation，可记录 Info/Warning（按现有缺失入口规则）。
- presentation hash/JSON/schema/section 错误：音乐正常打开，仅坏 section 回退，记录一条可定位的诊断并置 recovery-dirty。
- section 引用已删除 owner：删除自身 profile/局部状态；Onion 的 dormant source 只在会话规则允许时过滤，不能创建幽灵对象。
- 超过总/分区字节、数组、字符串或解析资源预算：拒绝该 section，不分配无限内存；已读合法 section 保留，不能静默截断。

### 5.2 保存边界

- 保存前对所有 section 做 canonicalize、大小预估和 checked arithmetic；预估失败不得开始替换原包。
- 所有写入使用 staging→flush→reopen/validate→atomic publish；目标冲突与迁移 source identity 竞争均失败闭环。
- 取消必须在 JSON 编码、压缩、hash、写包、flush、验证和发布各阶段可观测；取消后旧文件可读，临时文件按安全清理策略处理。
- `Save Copy` 失败不能清除当前 presentation dirty；普通 Save 失败不能清除 source 或 presentation dirty。

### 5.3 资源实验

B2 已采用独立的 presentation JSON 预算，不把它误认为进程总内存预算：单次编码上限为 64 MiB，单 section/profile/view/lane 条目上限为 131,072，单 owner 的 Lane target 总数上限为 65,536。以下资源实验仍是人工验收和 B3 前的补测项，不把静态上限当成完整真实 WPF 峰值：

| 实验 | 输入 | 记录 |
|---|---|---|
| Codec 密度 | 各 section 1/10/100/1000/上限 owner，稀疏与密集 Lane/Onion source | JSON 字节、压缩后字节、解析/序列化 p50/p95、managed allocation、private working set |
| 坏输入 | 深层 JSON、重复字段、超长字符串、巨大数组、dangling IDs、随机截断 | 首个失败阶段、CPU/内存上限、是否只影响对应 section |
| 保存并发 | 保存 barrier 前后修改视图/音乐、Save Copy 并行变化 | baseline/revision、最终文件、下次 Save 是否仍显示 dirty |
| 大工程 | 9KX2、1/3/7/18 million note 项目，仅改变视图状态 | 不扫描音符的证据、首个可用时间、内存增量、取消恢复 |

预算的整数来源是 B1 registry 的 64 MiB 保守 admission 与 schema 3 codec 的有界数组校验；如果后续实验表明批准预算无法覆盖合法用户状态，必须另行修改 D02/SRS/ADR，不以静默截断解决。

## 6. 自动验证与人工验收批次

### 6.1 自动验证门

1. **B2-CODEC** ：schema 旧/新 golden、deterministic bytes、strict unknown/duplicate/type/range、manifest hash、section fallback。
2. **B2-PERSIST** ：普通 Save、Save Copy、无音乐修改保存 presentation、Save 中修改、取消、写入/flush/replace 故障注入、旧格式 migration。
3. **B2-RESTORE** ：B1 registry 导入、owner/epoch、删除/Undo/duplicate 后 ID 对应、无自动 Tab 创建、重新打开手动消费。
4. **B2-MONITOR** ：Track/Usage/Root Mute/Solo 组合，首播前过滤，canonical/SMF/WAV/音频缓存指纹不变。
5. **B2-RESOURCE** ：section/total budget、坏输入峰值、9KX2 加载/视图保存、取消响应和临时文件回收。
6. **B2-COMPAT** ：Format 1/2/3 reader、当前 Format 4、schema1/2 presentation、旧软件打开新 presentation 时只丢新视图而不损坏音乐的实际边界。

每个自动测试要保留 source revision、presentation revision、manifest bytes、诊断数量、目标文件 hash 和资源峰值；不能只断言“打开成功”。

### 6.2 建议的人工验收批次

| 批次 | 操作 | 通过条件 |
|---|---|---|
| UAT-B2-01 基本往返 | 在 B1 验收工程改变 Track/Segment/SubVoice 状态、Onion、All Tracks，显式 Save，关闭重开后手动打开对应对象 | 状态精确恢复；选择/Undo/播放不恢复；音乐内容、标题星号与编译结果不被视图变化改变 |
| UAT-B2-02 Save/Copy | 只改视图后 Save；Save Copy；保存过程中再改视图；源文件和副本分别打开 | Save 不要求音乐 Modified；Copy 不清当前 dirty/path；保存中的新改动仍待下一次 Save |
| UAT-B2-03 Mute/Solo | 保存 Track、Shared Usage、Fixed/Auto Root 的独立 Mute/Solo 组合，关闭重开首播 | 首播前过滤正确；组与成员不互相改写；canonical、MIDI 导出、音频渲染正式内容不变 |
| UAT-B2-04 损坏隔离 | 使用测试生成的缺 entry、坏 hash、未知字段、重复字段、坏 section、dangling ID 包 | 音乐仍可打开；只有对应视图 section 回退；Warning 可读且不会重复弹出/静默吞掉 |
| UAT-B2-05 旧格式 | 打开旧 Format 1/2/3、完成迁移确认、保存/Save Copy，再重开 | 旧源备份和原路径升级规则不变；presentation 缺失只用默认，不破坏音乐和迁移保护 |
| UAT-B2-06 长期与边界 | 大项目保存/取消、超限提示、读写锁竞争、磁盘空间不足、重复 Save | 不出现系统级失控内存；旧文件始终可读；取消/失败后可重试，不能留下半包或错误清 dirty |

B2 人工验收不要求用户手工构造损坏 ZIP 或百万条 JSON；这些由 fault-injection/资源测试生成。B3 的“自动恢复几十个 Tabs/活动子页”不属于本表。

## 7. 交付物、源码落点与报告格式

### 7.1 已实施源码落点

- `src/midora-core/Midora.Persistence/ProjectPresentationCodecV4.cs`、`ProjectPresentationWorkspaceV4.cs`：schema 3 DTO、严格 codec、section recovery、Save preparation、64 MiB/条目预算。
- `src/midora-core/Midora.Persistence/ProjectPresentationV3.cs`、`PersistenceContractV3.cs`：schema 1/2 reader、schema 3 writer 分派，外层 Format 4 不变。
- `src/midora-core/Midora.Persistence/MidoraProjectPackageV1.cs`：Save/Save Copy 使用冻结的分区准备结果；省略 section 通过 `MIDORA-PERSIST-PRESENTATION-SECTION-OMITTED` 返回，不改变音乐 source 事务。
- `src/midora-core/Midora.Application/ProjectPresentationSessionV3.cs`：workspace snapshot 保持与 presentation revision 独立，保存时继续过滤 dormant 引用。
- `src/midora-desktop/Midora.Desktop/WorkspaceStateRegistry.cs`、`DesktopSessionController.WorkspaceState.cs`、`DesktopSessionController.cs`：B1 registry 捕获/恢复，Save 前合并 Mute/Solo，Open/Activate 前恢复并保持 Stopped；Save/Save Copy 会向桌面状态栏报告被省略的 presentation 分区。
- `src/midora-core/Midora.Persistence.Tests/ProjectPresentationSchema2Tests.cs`、`PersistenceContractV2Tests.cs`：schema 1/2/3、round-trip、section recovery、保存省略和 package hash 回归；`src/midora-desktop/Midora.Desktop.Tests/A4bWorkspaceTests.cs`：桌面 registry 回归。

`ProjectPresentationStateV3` 的历史类型名保留以避免无意义的 API 破坏；文档、manifest 和 writer 版本以 schema 3 / Project Format 4 为准，不将类型名当作 wire 版本。

### 7.2 每轮报告必须包含

- 已实施 T-ID、未实施/延期 T-ID、实际改动文件；
- wire schema/manifest 与 SRS/ADR 的逐项对应；
- golden、旧格式、坏包、Save/Copy、并发 revision、故障注入的实际命令与结果；
- section/total 字节预算、解析时间、峰值内存、取消响应和残留文件；
- 未测平台（尤其不同 DPI/磁盘/旧版本）以及人工 UAT 工程入口；
- 明确说明是否改变 Project Format、presentation schema、音乐 Modified、Undo/canonical、MIDI/WAV/音频语义。

## 8. 实施顺序与进入门

实施按四个工程切片完成；代码与自动验证已完成，用户人工验收仍按下表执行：

1. **B2a Codec/预算** ：T-STATE-04a～e；已完成 schema 3、JSON schema/hash、严格验证和保存预算。
2. **B2b Save/Copy** ：T-STATE-05a～f；已接入 Save/Save Copy/迁移保存的分区准备，不自动恢复 Tabs。
3. **B2c Read/Monitor** ：T-STATE-06a～e；已接入 B1 registry 和 Mute/Solo，完成 section recovery 与 recovery-dirty。
4. **B2d 回归收尾** ：自动 B2-CODEC/PERSIST/RESTORE/MONITOR 定向门已运行；RESOURCE/COMPAT 的人工边界已由 UAT-B2-01～06 验收通过；B3 仍未开始。

人工批次 UAT-B2-01～06 已全部通过；D01～D06 已确认，不再阻塞 B2 交付。B3 的自动 Tabs/活动页恢复仍需另行实施和验收；若后续发现 schema 或保存降级改变外部可见语义，应回到 §9/SRS/ADR 记录后再修复。

## 9. 需要用户确认的实施前决策

以下问题不是 B1 已回答的重复问题；它们只决定 B2 的 wire/工作流实现。六项已于本轮确认，实施采用下列选择。

### B2-D01：presentation schema 的承载方式

**问题：** B2 是否同意在同一 `settings/project-presentation.json` entry 上写新的 presentation schema 3，继续读取 schema 1/2，外层 Project Format 4 不变？

**推荐：** 选 A。这样保留单一 presentation 入口和既有 manifest/hash/Save 事务；schema 1/2 只读，未知新字段不会被旧 writer 静默覆盖。选 B（新增独立 `settings/workspace-state.json`）会增加 manifest entry、故障分区、旧软件行为和 Save Copy 组合测试。

**用户决定：** A。允许升级现有 presentation 格式，沿用 `settings/project-presentation.json`，升级为 schema 3，外层 Project Format 4 不变。

### B2-D02：保存超限时的默认按钮

**问题：** 当音乐 source 合法但某些 presentation section 超过预算，是否默认提供“保存音乐及可用视图部分”，并把超限 section 列在详情中，同时提供“取消保存”？

**推荐：** 选 A。符合 D-STATE02.c 的“明确提示后允许保存音乐及可用视图部分，或取消”，也避免视图预算问题阻止音乐保存；任何被省略 section 都必须明确显示，不得静默截断。

**用户决定：** A。保存音乐及可用视图部分，并明确列出被省略的 section；不得静默截断。

### B2-D03：损坏 section 的 recovery-dirty 行为

**问题：** 读取后某 section 损坏并回退默认时，是否只在 Diagnostics/状态区域提示，用户下一次显式 Save 时再把当前可用状态写回，而不在打开时自动改写文件？

**推荐：** 选 A。打开过程零写入、可重现、不会因读取坏包而改变用户文件；用户显式 Save 才完成修复发布。

**用户决定：** A。只提示并标记 recovery-dirty，下一次显式 Save 才修复，不在打开时自动改写文件。

### B2-D04：B2 是否保存但不自动恢复 B3 字段

**问题：** B2 是否只保存/读取 B1 profile、Segment/SubVoice 局部状态、Onion/All Tracks 和 Mute/Solo；Tab 顺序、活动 Tab、Instrument 当前子页等留到 B3，不在 B2 中写“未消费的假恢复”字段？

**推荐：** 选 A。B2 先形成可验证的 codec/保存边界，B3 再接完整导航和懒激活，避免“文件里有字段但用户看不到恢复效果”。

**用户决定：** A。B2 只持久化 B1 状态、局部视图、Onion/All Tracks、Mute/Solo；Tab 顺序、活动页和子页留到 B3。

### B2-D05：Mute/Solo 的 presentation baseline

**问题：** 保存 Mute/Solo 后，用户改变它们是否只增加 presentation dirty、允许显式 Save，但不显示音乐星号、不生成音乐 Undo、不触发 canonical 编译？

**推荐：** 选 A。这是已批准的运行期监听过滤语义；首播/重建 Worker 前应用，不影响正式导出和音频内容。

**用户决定：** A。Mute/Solo 只产生 presentation dirty，不进入音乐星号、Undo、canonical 或编译。

### B2-D06：旧软件打开新 presentation

**问题：** 旧版本只能识别 schema 1/2 时，打开 B2 新 schema 是否允许它加载音乐、丢弃不认识的 presentation 状态，并在该版本保存后按旧 schema 重写（用户已接受未知新字段可能丢失）？

**推荐：** 选 A，且在兼容性测试中明确记录“音乐可读”与“新视图字段丢失”是两个不同结果；不得把旧版本伪装成完整恢复。

**用户决定：** A。旧版本可读取音乐，但可能丢弃新 presentation 字段；兼容性测试必须明确记录这一边界。

## 10. B2 实施记录（2026-09-18）

### 10.1 已完成范围

- D01：`settings/project-presentation.json` writer 为 schema 3，继续读取 schema 1/2，外层 Project Format 4 不变。
- D02：Save preparation 按完整 section 校验；非法或超过 64 MiB presentation JSON budget 的 section 整体省略，不截断，并在 SaveResult diagnostics 明确列出省略项；音乐 source 仍继续原子保存。
- D03：Open 时 section recovery 只设置 recovery-dirty 和独立 Warning；不自动写回，下一次显式 Save 才修复。
- D04：B2 只持久化 B1 profile、Segment/SubVoice local view/Lane、Onion/All Tracks 和 Mute/Solo；Tabs/活动页/子页仍由 B3 处理。
- D05：Mute/Solo 只属于 presentation revision 和运行期过滤，不进入音乐星号、Undo、canonical、MIDI/WAV 或音频缓存。
- D06：旧软件兼容边界记录为“音乐可读，新 workspace 字段可能丢失”，不声称旧软件能完整恢复 B2 视图。

### 10.2 自动验证证据

已运行：

```text
dotnet build src/midora-core/Midora.Persistence/Midora.Persistence.csproj --no-restore -v:minimal
dotnet build src/midora-core/Midora.Application/Midora.Application.csproj --no-restore -v:minimal
dotnet build src/midora-desktop/Midora.Desktop/Midora.Desktop.csproj --no-restore -v:minimal
dotnet test src/midora-core/Midora.Persistence.Tests/Midora.Persistence.Tests.csproj --no-restore -v:minimal
dotnet test src/midora-core/Midora.Persistence.Tests/Midora.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~ProjectPresentationSchema2Tests" -v:minimal
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~A4bWorkspaceTests" -v:minimal
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj --no-restore -v:minimal
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj --no-restore --filter "A4bWorkspaceTests|Workspace" -v:minimal
```

结果：Persistence 全量 **242 通过**；Application 全量此前 **1255 通过**，另有新增保存 dirty 回归 **1 通过**；桌面 Workspace 定向 **87 通过**；三个项目构建 0 警告、0 错误。presentation 定向门此前为 **18 通过**，A4b workspace 门此前为 **2 通过**；新增覆盖 schema 3 workspace/monitoring 往返、坏 workspace section 隔离、保存时只省略坏 section 并返回 `MIDORA-PERSIST-PRESENTATION-SECTION-OMITTED`，以及省略 section 时保持 presentation dirty。

### 10.3 尚待后续验证/未声称完成

- UAT-B2-01～06 已于 2026-09-20 由用户全部验收通过；该结论不替代以下仍未完成的工程验证门。
- D02 的默认保存路径已实现为“保存音乐及可用完整分区并列出省略项”；当前普通桌面状态不会触发超限分支，尚未单独接入超限前的二选一取消对话。若后续需要在写入前取消，需增加同一冻结 snapshot 的 preflight/确认接口，不能在事务发布后补做取消。
- 尚未做旧版本可执行文件的真实打开/保存互操作；D06 当前只由 schema reader/格式边界和文档约束保证。
- 尚未做 9KX2 在真实 WPF 下的 64 MiB presentation 资源峰值、磁盘满/取消/锁冲突完整 fault-injection；B3 的自动 Tabs/活动页恢复完全未实施。
- 未本地发布、未使用 computer-use；提交/推送由本次验收归档操作完成。

### 10.4 用户人工验收归档（2026-09-20）

用户明确确认：**UAT-B2-01～UAT-B2-06 验收全部通过。记录、提交、推送。**

本次确认覆盖 B2 的基本往返、Save/Save Copy、Mute/Solo 恢复、损坏 presentation 分区隔离、旧 Format 1/2/3 迁移边界以及长期/资源/失败边界。自动验证、未执行的旧版本可执行文件互操作和真实故障注入限制仍按 §10.2～§10.3 保留，不把人工通过扩大为未运行测试已通过。
