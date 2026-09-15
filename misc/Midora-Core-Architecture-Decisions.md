# Midora 领域、编译与播放核心架构决策

状态：核心候选已实现并进入回归验证（2026-08-05）；未决项与非合规过渡实现见一致性审计记录
适用范围：内存 Project 领域模型、语义验证、全量/增量编译、Canonical Compiled Result、播放计划缓存与无 UI 播放控制
上位规范：`Midora-SRS-Initial-Release-v0.1`。本文是实现决策，不是需求规范；冲突时以 SRS 为准。

## 1. Requirement trace

### 1.1 输入

- 正式输入是完整的内存 Project：TPQ、Conductor Track、Event Instrument Library、SubVoice、Mapping/Lifecycle、Global Defaults、Logical Track/Segment/Note/Parameter，以及 MIDI Channel Root、Pure MIDI Track、Midi Segment、Direct/Raw/Opaque MIDI Event。
- 每个正式对象用 Project 内稳定 ID 标识；显示名称、列表位置、tick、Port 和 Channel 不构成身份。
- 编译请求还包含 `[startTick, endTick)`、Track 选择、用途和 Warning-as-error 策略。
- SoundFont、输出设备、设备实际采样率、Master/Limiter 和实时 buffer 不改变 tick-domain canonical MIDI 语义。

### 1.2 正式输出

- `CanonicalCompiledResult` 是播放与本轮离线试听的唯一音乐语义输入。
- 结果冻结保存 Conductor 状态、按稳定顺序排列的 Channel 事件、Port/Channel Unit 分配、Root lifecycle、Execution Projection、SMF Track Projection、资源占用、来源追踪、诊断、统计、CompileContext、可消费性和 partial 标记。
- 音频适配层只把 canonical tick 事件经 Tempo Map 映射到绝对 sample-frame，并生成现有 `MidiRenderPlan`；它不得重新解释 Event Instrument、Mapping、Lifecycle、Segment 或资源分配。

### 1.3 边界

- tick 使用 `Int64`，TPQ 在 Project 创建后固定；所有范围使用 `[startTick, endTick)`。
- 单 Project 最多 16 Ports × 16 Channels；每个有输出的 Event Instrument Instance 按 SubVoice 数原子分配 Channel Group，低编号优先，允许跨 Port，不做 Voice Stealing。
- Logical/Event Instrument 获配的 Channel 10 为 melodic；Pure MIDI Root 的 Channel 10 服从显式 Melodic/Percussion mode。CC91/CC93 在 Event Instrument/SubVoice 路径为 Error，在 Pure MIDI 路径合法并进入 canonical/SMF 投影。
- Segment End、显式编译 end 和作为默认范围的 Project End Marker 是硬边界：立即精确 NoteOff，再 Reset，不允许 Release/Tail 越界。
- 范围起点恢复必要非 Note 状态，但不重触发范围前已经开始的 Note。

### 1.4 失败条件

- 结构、引用、范围、值域、Mapping、Lifecycle、Loop/Envelope、Segment 重叠或资源分配非法时，结果不可消费。
- 实际使用的 Mapping Function 编译失败、抛异常、返回 NaN/Infinity，或映射结果不能按显式策略合法化时，编译失败。
- 任一最终 Note、CC、Program、Bank、Pitch Bend、RPN/NRPN 或 Pitch Bend Range 超出合法范围且没有合法 Clamp 策略时，编译失败；Note number 不允许 Clamp。
- 超过 256 Channel Units 或 Channel Group 不能原子分配时为 Error；峰值 `>= 248` 且未超限时只产生 Info。
- 正式消费者只能消费与当前用途匹配的成功、完整结果；Full Project、Playback、Range 和 Preview 的成功结果都不是 partial。失败时若未来返回 partial 数据，它只能用于诊断且不可消费。无有效 SF2 或没有可用输出设备只阻止发声，不改变编译结果。

### 1.5 诊断

- 诊断级别为 Error、Warning、Info、Debug；Warning-as-error 只改变本次成功判定，不改写诊断级别。
- 诊断尽量携带 Root、Track、Segment、Direct/Logical Note/Event、Event Instrument、SubVoice、Mapping、tick、source MTrk/offset 和资源上下文；初版 Project 本身没有稳定 Project ID。
- 未绑定 Event Instrument 的非空 Logical Track 为 Info；断裂参数 Lane 为 Warning；Event Instrument 路径 CC91/CC93、非法实际输出、Root route 冲突和资源不足为 Error。合法 Pure MIDI CC91/CC93 不产生诊断。

### 1.6 持久化归属

- `.midora` v1 持久化契约不再包含 SoundFont settings 或 SF2 资源；旧 codec/schema 只属于已被 ADR-CORE-047 破坏性取代的开发历史。当前 package 只保存 Project 源数据。
- Project 源数据中的稳定 ID、显式顺序、opaque sRGB 颜色、C# Mapping 源码和版本化设置属于未来 `.midora` 内容；只有发布了对应结构性文件 schema 后才形成文件兼容承诺。
- Canonical result、编译 checkpoint、fingerprint、sample-domain 计划、PCM ring、播放状态、Mute/Solo 和诊断结果都是派生或运行时数据，不属于 Project 持久内容。

### 1.7 运行时归属

- `ProjectCompilationSession` 持有 Project、编译缓存、最后成功结果和 sample-domain 缓存；仅在 Stopped 状态提交编辑。
- 每次成功编辑形成明确的 `ProjectChangeSet`，立即执行增量编译并原子替换最后结果；失败结果保留诊断但不替换最后可消费结果。
- sample-domain 缓存键至少包含 canonical result 指纹、编译范围和实际采样率；Project 音乐语义、Tempo 或采样率变化时失效。
- 播放控制负责 Start、Stop、Seek、Mute/Solo 的运行时过滤和后端资源清理，不拥有 Project 语义。Mute/Solo 通过 source-index 控制命令在合成推进边界生效，不触发 canonical 重编译或后端冷启动；已进入音频子进程内 render-ahead PCM buffer 的音频仍受相应 buffer 延迟约束。

### 1.8 明确非目标

- 本轮不实现完整 `.midora` ZIP 打开/保存事务、其余顶层对象 schema、WPF UI、完整 MIDI 文件事务/多文件工作流、传统 MIDI OUT、Pause、Scrub、录音、多 SoundFont、MIDI 2.0、VST 或 Voice Stealing。
- 本轮不把 BASS handle、WASAPI 设备或 sample-frame 写入领域模型或 canonical result。

## 2. ADR-CORE-001：分层与冻结边界

决定：新增三个单向依赖层。

```text
Midora.Domain
    ↓
Midora.Compiler
    ↓
Midora.Playback
    ↓
现有 Midora.Audio / BASS / AudioDevice
```

领域模型保存可编辑源数据；编译器输出冻结连续数组；播放层只做范围过滤、tick→sample、缓存和后端生命周期。Canonical result 不引用 BASS、WASAPI 或 WPF。

## 3. ADR-CORE-002（候选）：确定性曲线离散化 v1

已实现候选：编译器按整数 tick 采样连续曲线和 Envelope，包含曲线有效区间端点；阶梯段保持前值，直线与自由手绘段按相邻控制点线性插值。每个目标在同 tick 只输出最终合成值，跨 tick 的相同状态值暂不折叠。

依据：Project 的最高时间精度就是 tick；按 tick 采样不会引入第二个隐藏时间网格，结果与输出设备采样率无关，且可通过连续数组和预估容量实现线性时间复杂度。

限制：这是实现 v1，不是 SRS 固定算法。若极长曲线的事件量或听感测试不能接受，必须以新 ADR 和 golden vectors 修改，不能静默改变。

## 4. ADR-CORE-003（已接受）：Segment 边界 Checkpoint、Dirty Range 与状态收敛

决定：Full Compile 在每个按 `ProjectStartTick`、稳定 ID 排序的 Segment 入口以及 Track 末尾生成运行时检查点。缓存单元是“已展开、尚未分配 Channel Unit”的 Segment 冻结片段；每个入口检查点保存 tick、下一实例 `SourceOrder`、完整展开上下文 fingerprint 和由这些字段确定生成的 state hash。状态等价判定同时比较 hash 与全部原始字段，不能只凭 hash 命中。

该边界成立的依据是：同一 Track 的 Segment 经语义验证后不得重叠；每个实例都被 Segment End 硬裁剪并在边界发出完整 NoteOff/Reset。因此进入下一 Segment 时，展开阶段不存在跨边界活动实例、生命周期、Logical Parameter 继承或待处理 Reset；唯一跨 Segment 的展开状态是用于稳定排序的下一 `SourceOrder`。Channel Group、Channel Unit、当前 MIDI 状态和资源分配器不进入此中间缓存，因为每次 Full/Incremental Compile 都对全部本次实例重新执行同一确定性全局分配、materialize、排序、范围恢复与硬裁剪。这样不会复用历史 Port/Channel 分配。

Dirty 起点按新旧有序 Segment source fingerprint 的首个差异确定；插入、删除和移动同时取新旧边界的较早 tick。Track/Event Instrument/全局上下文 fingerprint 变化时从 Track 首段回退。显式 `ProjectChangeSet` 标记但 fingerprint 未变化时，仍至少重编一个 Segment 以验证缓存覆盖面。编译器从 Dirty Segment 按 Full Compile 规则向后展开；只有新的检查点状态逐字段等于旧检查点，且其后的 Segment ID、source fingerprint 和顺序全部未变时，才复用冻结后缀。实例计数或过滤状态改变会改变 `SourceOrder`，从而阻止错误收敛并继续重编到上下文末尾。

Conductor、范围、Track/SubVoice 选择和 End Marker 不混入 Segment 缓存：Conductor 每次重新冻结，原始实例缓存保持全上下文，之后再按本次请求重做范围恢复、选择、硬边界与结果 fingerprint。所有语义验证和实际 Mapping Function 可用性检查也在缓存判定前重新执行；缓存只存在于当前 `MidoraCompiler`/Project 会话，切换 Project、显式清理或 Dispose 时释放，不持久化到 `.midora`。

验证门：Full Compile 继续作为 oracle；固定向量覆盖中段编辑、插入/删除空 Segment、实例计数改变、Event Instrument 上下文失效、集合乱序和显式失效，固定种子性质测试在连续合法编辑后逐字段比较 result、Conductor、事件、分配、诊断、统计与 fingerprint。当前粒度不会在单个 Segment 内建立 Note 级检查点，因此长 Segment 的最坏情况是重编整个 Segment；SRS 未规定更细粒度，后续只有在性能证据要求时才可增加内部检查点，且不得改变本 ADR 的状态等价与 Full oracle 门。

## 5. ADR-CORE-004：tick 到 sample-frame 映射候选 v1

已实现候选：音频适配器按完整 Tempo Map 积分得到相对所选范围起点的绝对秒数，再使用 decimal 算术计算 `seconds × sampleRate`，最终以 `MidpointRounding.AwayFromZero` 舍入到 `Int64` sample-frame。每个 Tempo 分段的累计秒数不在分段边界提前取整。

这是为本轮端到端试听采用的明确候选，不是 SRS 已规定规则。它替代“各调用点自行截断”的隐式行为，并保证相同输入、采样率和运行时下稳定、单调。正式发布前仍需用极端 Tempo、长时间累计和所有合法采样率的 ADR 测试向量确认或升级。

## 6. ADR-CORE-005（已接受，17A；已由 ADR-CORE-035 取代）：C# Mapping ABI v1

决定：初版 C# Mapping Function 使用版本化的 `Midora C# Mapping ABI v1`。Project 源数据只保存 `abiVersion = 1`、函数体源码和声明的 Context 字段；编译产物、程序集和缓存不进入 `.midora`。

ABI v1 固定以下持久兼容边界：

```csharp
double Transform(double value, in MappingContextV1 context)
```

- `MappingContextV1`、`MappingStableIdV1`、`MappingTargetParameterV1` 和 `MappingEventKindV1` 位于独立、只读且不引用 Midora Domain/Compiler 的契约程序集。
- 源码是上述固定方法的方法体，不是完整 compilation unit；包装类、方法名、参数名和类型由 ABI 固定。
- 语言版本固定为 C# 14，编译目标引用面固定为 `Microsoft.NETCore.App.Ref 10.0.10` 和 ABI v1 契约程序集。不得引用 Midora Domain/Compiler、WPF/WindowsDesktop 或第三方程序集。
- 引用白名单只用于持久兼容和依赖收敛，不是安全边界。SRS 9.6.2 的“自由 C#、无 sandbox”保持不变；`System.IO`、时间、随机数、反射和外部状态仍可经普通 .NET API 使用，Midora 不保证这类函数可复现或安全。
- Roslyn 固定为 `Microsoft.CodeAnalysis.CSharp 5.3.0`，并使用 Release、deterministic、允许 unsafe 的 C# 14 固定 profile；生成程序集名称由 ABI 版本与完整函数体 UTF-8 SHA-256 确定，不使用 GUID、时间或编译历史。允许 unsafe 是 SRS 9.6.2“完整自由 C#、不做 sandbox”的直接结果；`in MappingContextV1` 只提供普通 C# 语言层的只读调用约束，不是安全隔离。

Compiler 在 canonical 编译阶段执行 Mapping Function，活动音频线程不编译也不调用 Project 源码。每个打开 Project 的缓存按 `ABI version + compiler profile + 函数体精确 UTF-8 SHA-256` 索引；相同函数体可共享当前缓存项，声明字段不改变生成代码，因此不进入代码缓存键，但仍进入 Project/source fingerprint 和兼容性校验。

每个成功代码缓存项由独立 collectible `AssemblyLoadContext` 持有。缓存同步时只保留当前 Project 仍存在的源码修订；编辑、删除、切换 Project、显式清缓存或关闭 Project 后，旧项在没有在途编译调用时释放委托并调用 `Unload()`。缓存大小因此受当前 Project 中不同函数修订数约束，不随编辑历史无界增长。Project 编译会话和 Compiler 都提供显式释放入口；终结器只作为未正确释放时的兜底。

未来 ABI 变更必须增加 `abiVersion` 并保留旧 ABI 执行器或提供显式迁移；不得让 `LanguageVersion.Latest`、运行机器 TPA、应用内部程序集或当前进程已加载程序集静默改变旧 Project 的编译面。编译错误、未知 ABI、异常和非有限结果均转为可定位诊断，并按是否实际参与路径决定 Error/Warning。

## 7. ADR-CORE-006：动态 Mute / Solo 与进程边界

决定：sample-domain 计划携带去重后的 Track source table，事件只保存连续 `SourceIndex`。渲染器为每个 source 保存预分配 enabled 位；实时线程只读取位和固定容量的无锁命令批次，不读取领域模型，也不分配托管对象。

禁用 source 时，播放层按当前 render frontier 向该 source 仍占用的 Channel Unit 发送精确清理（All Notes Off、All Sound Off、Reset All Controllers 及 canonical reset defaults）；重新启用时先恢复该范围起点必要的非 Note 状态，再放行未来 canonical 事件，不重触发已经越过起点的 Note。内部音频子进程使用独立控制管道传递同一固定协议，Project 和编译器不进入子进程。

这是运行时消费过滤，不改变 Project、Canonical Compiled Result 或编译 fingerprint。可听响应时间仍包含音频子进程内已经生成的 render-ahead PCM 和设备 buffer；正式拓扑不跨进程传输实时 PCM。

## 8. 领域实现表示说明

- `MidoraColor` 已与 18A 文件兼容决定统一为三个 byte 分量的 opaque sRGB；不再保留 alpha/ARGB 内存入口，protobuf 和 JSON 表示分别服从 SRS 16.13.8。
- Event Instrument Library 的 Create / Rename / Duplicate / Delete 是内存领域服务；Duplicate 重建内部稳定 ID 和内部引用，Delete 在明确确认被引用对象后解除 Track binding 并保留 Track 内容。

## 9. ADR-CORE-007（已接受，18A/18.1A）：`.midora` v1 序列化兼容基线

决定：轻量结构性文件固定使用 JSON Schema Draft 2020-12 和内部版本化 `System.Text.Json` source-generated DTO；重对象固定使用 protobuf Edition 2024、Google.Protobuf 3.35.1 与 Grpc.Tools 2.83.0。`.proto` 和 runtime `FileDescriptorSet` SHA-256 基线提交到仓库，生成 C# 只存在于 `obj`。JSON 未知/重复属性和 protobuf descriptor 未知 tag 均在领域反序列化前拒绝。

Requirement trace：

- 输入：v1 manifest DTO、已发布 protobuf message、UTF-8 JSON/protobuf wire bytes，以及已确认的稳定 ID、文本、路径、颜色和 metadata 基础值。
- 正式输出：字段顺序和 LF 固定的 UTF-8 无 BOM JSON；固定 runtime/profile 下的 deterministic protobuf bytes；版本控制中的 Draft 2020-12 schema、`.proto`、descriptor hash 与 golden bytes。
- 边界：文本上限按 Unicode scalar；相对路径保留大小写和原 Unicode、不 normalization；颜色为 opaque sRGB；UTC 时间严格为七位小数秒 `Z`；总耗时为非负 int64 毫秒。
- 失败条件：BOM、JSON 重复/未知字段、未知 protobuf tag、错误 wire type、非法 UTF-8、越界标量、非 canonical hash/path/version 或 descriptor/golden 漂移均失败，不截断也不静默修复。
- 诊断：当前 codec 以 `JsonException` / `InvalidDataException` 保留失败类别；完整打开流程实现时再映射为 SRS 第 16.20 节的文件级正式诊断，不能把异常文本直接当 UI 诊断协议。
- 持久化归属：本 ADR 冻结通用值类型和 `manifest.json` v1；`metadata.json` 随 ADR-CORE-009 冻结。旧 `soundfont-settings.json` 决定已由 ADR-CORE-047 破坏性取代。
- 运行时归属：DTO、descriptor、codec 和校验属于 Preparing/open/save 路径，不进入编译器 canonical 语义或音频活动线程。
- 明确非目标：本增量不实现 ZIP 结构、hash 全包校验、迁移、损坏占位、Save/Save Copy 原子事务和完整 Project round-trip。

兼容规则：已发布 protobuf 字段号不得复用，删除字段必须 reserved。deterministic protobuf 不是跨 library/tool 版本的 canonical encoding；依赖升级必须显式评审 descriptor diff、golden bytes 和旧文件重开。受决定 21–22 影响的其余 v1 对象 / settings schema 只能在对应字段闭合后发布，不能用临时默认值提前冻结。

## 10. ADR-CORE-008（已被 ADR-CORE-047 取代）：Project SoundFont 可移植引用与内容身份

本节仅保留开发历史，不再构成当前需求或兼容承诺。

决定：Project 不保存绝对 `SoundFontPath`。领域源数据使用严格 External/Embedded union；实际解析出的绝对路径、验证中/缺失/歧义/hash mismatch/加载失败状态和验证缓存均属于运行时。`settings/soundfont-settings.json` v1 已冻结为 `schemaVersion`、`mode` 及模式对应的 `relativePath`/`resourceId`、`originalFileName`、`sha256`、`fileSizeBytes`。

Requirement trace：

- 输入：`.midora` 的完全限定路径、用户选择的完全限定 SF2 路径、Project SoundFont 源引用及当前外部文件字节。
- 正式输出：External 保存 `<file>.sf2` 或实际大小写的 `<soundfonts>/<file>.sf2`；Embedded 保存稳定 resource ID；二者都保存原始文件名、完整原始字节 SHA-256 和非负文件大小。
- 边界：只允许 Project 根目录或直属 `soundfonts/`；路径逐分量 ordinal 精确匹配优先，唯一 ordinal-ignore-case 候选可回退并 Warning，多个近似候选为歧义。根目录和 `soundfonts/` 的同名文件由完整相对路径区分。
- 失败条件：选择位置越界、嵌套目录、非 SF2、非法/非 canonical 路径或 hash、文件不可读、大小写歧义、Embedded resource ID 为零，以及 mode/payload 组合不一致均失败。
- 诊断：External 缺失、不可读、case fallback 和 hash mismatch 是资源状态，不是编译诊断；fallback/hash mismatch 为 Warning，缺失/不可读/歧义阻止发声消费者但不阻止 Project 打开、编译或 MIDI 导出。
- 持久化归属：External 的相对路径和最后明确接受的内容身份、Embedded 的 resource ID/内容身份属于 Project；普通保存或被动监控不得更新 External 身份。可访问/加载状态不持久化。
- 运行时归属：解析后的绝对路径、完整 hash 验证结果、文件身份/大小/mtime 缓存和文件监控只属于打开会话。`ProjectCompilationSession.EffectiveSoundFontPath` 是当前过渡运行时注入点，不进入 Project 或 compiler fingerprint。
- 明确非目标：本增量不实现 SF2 格式/BASSMIDI 可加载性验证、完整 package 资源复制、损坏 Embedded 保存策略、文件监控器、UI 接受变化命令或 ZIP 事务。

hash 使用 SHA-256 并流式读取。用户选择、替换、重新绑定或明确接受当前内容时才生成新的源引用；被动验证只返回当前 hash/状态，不修改原引用。打开流程未来必须在结构加载后异步完整验证，验证完成前禁用发声；首次音频任务只能使用仍有效的验证缓存，否则重新验证。

## 11. ADR-CORE-009（已接受，20A）：Project 工程总耗时单调会话累计

决定：工程总耗时从 Project 成功新建 / 打开并成为当前可信 Project 时开始，到关闭流程开始时暂停。空闲、最小化、失焦、模态 UI、保存、编译、播放、预览、Buffering、MIDI 导出与音频渲染全阶段均累计；系统睡眠 / 休眠和关闭流程不累计，关闭取消后从恢复打开状态时继续。自动累计不单独设置 Modified、不进入 Undo / Redo、不更新 `modifiedAtUtc`，也不参与 compiler fingerprint。

Requirement trace：

- 输入：已保存的非负 `totalEditingTimeMilliseconds`、单调 `TimeProvider` timestamp、系统 suspend/resume 和 Project begin/cancel-close 生命周期通知。
- 正式输出：当前内存 Project Metadata 的非递减 int64 整毫秒累计快照，以及严格 `metadata.json` v1 的 `totalEditingTimeMilliseconds`。
- 边界：成功建立 Project 前不计；活动打开会话全部计；任一 pause reason 存在时不计；关闭取消只恢复后续累计，不补计暂停区间。每个 Project 同时只能有一个累计会话。
- 失败条件：持久值为负、时间戳非 canonical、修改时间早于创建时间、同一 Project 重复打开累计 owner、注入时钟倒退或 metadata 字段无效均失败；累计超过 int64 表示范围时饱和到 `long.MaxValue`。
- 诊断：当前领域 / codec 以参数、状态和 `InvalidDataException` 分类；完整应用打开 / 保存流程实现时映射为生命周期或 metadata 文件诊断。
- 持久化归属：用户 metadata、UTC 创建 / 修改时间和已累计整毫秒属于 Project；当前 timestamp、sub-millisecond remainder、pause reasons 与 owner flag 不持久化。Save Copy 只序列化快照，不回写当前 Project 修改时间。
- 运行时归属：`ProjectEditingTimeSession` 使用单调 timestamp 并由当前 `ProjectCompilationSession` 过渡持有；UI 未来负责转发 Windows suspend/resume 和 begin/cancel-close 通知。计时不进入音频 Worker 或音频活动线程。
- 明确非目标：本增量不实现 WPF 电源事件接线、Project Modified/Undo 框架、完整 New/Open/Save/Close/Save Copy 事务、自动保存或崩溃恢复。

`metadata.json` v1 同时冻结项目名称、用户版本、作者/团队、原作、版权、备注、UTC 创建 / 修改时间和总耗时字段。会话内部保留 100 ns `TimeSpan` tick 余数，生成持久快照时向下取完整毫秒；重复取快照不会重复累计同一区间，系统墙钟校时不改变累计值。

## 12. ADR-CORE-010（已接受，21A/22A；Pure MIDI 拓扑与超长 delta 部分由后续决定取代）：SMF Type 1 兼容编码档

2026-09-10 修订：Pure MIDI 拓扑继续以 ADR-PMIDI-007 及后续 global order 决定为准；超长 delta 改由 [ADR-SMF-001～002](Midora-SMF-Export-Timing-Padding-and-Size-Limits-Architecture-Decisions.md) 规定：仅在导出时用空 Text Meta `FF 01 00` 分段，MTrk 数据区保留 `0xFFFFFFFF` 字节上限且绝不按大小拆分，超限只拒绝本次导出。Compiler 不新增间隔扫描或编码字节检查。新规则已进入 SRS/INV-118，产品代码与新测试待实施；下文历史垂直切片的完成范围不代表此项已实现。

决定：初版 `.mid` 编码固定使用 SMF Type 1 和 Project TPQ。Tempo 以十进制 `60,000,000 / BPM` 计算，并只对最终 microseconds-per-quarter-note 执行一次 `AwayFromZero`；舍入结果超出 `1..0xFFFFFF` 时整体失败。Time Signature 固定写 `cc=24`、`bb=8`。同 tick 的 Bank/Program 字节顺序固定为 CC0、CC32、Program Change。所有文本 Meta 使用严格 UTF-8；事件 Track 只写 Track Name 与 MIDI Port Meta，不写 Device Name / Program Name。每个 Channel Event 都显式写 status byte，不使用 Running Status。

Requirement trace：

- 输入：用途为 `MidiExport`、成功、完整、可消费且已冻结 SMF Track Projection 的 `CanonicalCompiledResult`。编码器不读取 Project、播放状态、SoundFont、设备或 Mute/Solo。
- 正式输出：范围起点重基为 MIDI tick 0 的确定性 SMF Type 1 字节；Track 0 为 Conductor，随后为 Pure MIDI ExportTrack MTrks，再为 Logical Unit MTrks。Logical Unit 仍一 Unit 一 Track、按 Port→Channel；Pure MIDI 一 Track 一 MTrk、按 Root/Track 顺序并使用自身 EOT。
- 边界：Channel Event 逐条保持 canonical 子序列和真实 NoteOff velocity 0；RPN/NRPN/Pitch Bend Range 使用 canonical 已展开的标准 CC；导出器不得折叠状态，不得在 canonical 外追加 All Notes Off、All Sound Off、Reset All Controllers 或其他 Channel 清理。Track Name 的最终可见字符串由上层工作流显式提供，编码器不隐藏选择命名模板。
- 失败条件：非 MidiExport 上下文、不可消费/partial 结果、非法 TPQ、24-bit Tempo 越界、非法 Time/Key Signature、未知 Channel Event、Event Instrument 路径非法 CC91/93、非法 Note 编码、路由/来源不一致、Track descriptor 缺失或自校验失败均整体失败且不发布 partial 产物。单条 payload 的 VLQ、ntrks 与 MTrk 字节超限仍拒绝；原“超出四字节 VLQ 的事件间隔一律失败”已被 ADR-SMF-001 的导出专属填充取代。合法 Pure MIDI CC91/93 和 NoteOff velocity `0..127` 必须可编码。
- 诊断：当前垂直切片区分 canonical consistency 与 encoding 两类结构化诊断；完整工作流实现时再接入统一任务/文件写入诊断，不把异常文本当持久协议。
- 持久化归属：SMF 是导出产物，不进入 `.midora`；Track 可见名称布局和输出路径是本次工作流快照。受文件命名决定影响的 Export Settings schema 仍未发布。
- 运行时归属：SMF 组织、字节编码和读取后自校验属于 MIDI 导出 Preparing/Encoding；不进入 compiler canonical 语义，也不进入音频 Worker。
- 明确非目标：本增量不实现按 Logical Track/按 Port 多文件模式、Compact Routing、Readme、临时目录原子发布、覆盖确认、取消/进度、最终文件命名模板和完整 WPF 工作流。

编码完成后必须重新解析并检查 MThd、MTrk 数量与长度、显式 status、可编码 delta、每个 MTrk 恰有一个最终 EOT、EOT 与各冻结 descriptor 一致和文件末尾无额外字节。低层 `StandardMidiFile` 提供 Type 1 writer/validator；正式消费者只接受 canonical 结果。

22A 已固定 Channel 10 melodic 兼容档。每个 Logical Channel 10 Unit MTrk 与 Melodic Channel 10 Pure MIDI MTrk，在相对 tick 0、Track Name/MIDI Port/结构 Meta 之后、全部 canonical Channel Event 之前，分别写一次 Roland GS Normal Part `F0 41 10 42 12 40 10 15 00 1B F7` 和 Yamaha XG Normal Part `F0 43 10 4C 08 09 07 00 F7`，顺序为 GS→XG。Percussion Root MTrk 不写；编码器不得发送 GS/XG/GM Reset 或替换 canonical Bank/Program。

这些消息采用厂商文档中的默认 Device ID / Device Number。接收方不识别 vendor SysEx 或使用不同设备编号时仍可能把 Channel 10 当鼓通道，Readme 必须说明该兼容边界。该选择依据 [Roland M-GS64 MIDI Implementation](https://cdn.roland.com/assets/media/pdf/M-GS64_OM.pdf) 的 `40 1x 15 USE FOR RHYTHM PART` 和 [Yamaha XG MIDI Data Format](https://uk.yamaha.com/en/download/files/2090960) 的 `08 nn 07 PART MODE`；外部资料用于确认 wire 定义，不替代 SRS。

## 13. ADR-CORE-011（已接受，23A/23.1A/23.2A）：共享输出命名边界与初版模板

决定：MIDI 导出和音频文件渲染不得各自实现不同的文件名策略。两个工作流共用一个确定性的 Windows 安全文件名合法化与冲突检测服务；它接收原始候选名称、扩展名预算和同一任务的候选集合，输出可预览、可诊断、可冻结的完整最终目标列表。任务开始后，编码器、渲染器和文件写入器不得再次解释或改变目标名称。

合法化只属于 Preparing / Review 输出规划，不修改 Project、Logical Track 或其他源名称，不进入 Undo / Redo，也不影响 canonical、MIDI 字节或音频样本。已有目标的覆盖授权仍由任务工作流在开始前一次性取得；合法化不能转化为静默覆盖权限。

Requirement trace：输入是源名称、导出模式、扩展名、父目录和同批候选集合；正式输出是合法化且内部唯一的冻结目标列表。边界包括 Windows 非法字符、保留设备名、尾部空格/句点、不可见字符、文件名部分长度、大小写和 Unicode 别名冲突。失败条件是公共算法无法形成唯一、合法、可表示的完整目标，或合法化后完整路径仍不可用；诊断归属文件系统/输出规划。持久化只允许保存 SRS 明确允许的有限命名偏好，不保存最终路径或合法化结果。明确非目标是修改源名称、基于父目录临时改变算法、运行中重命名、自动授权覆盖或把命名并入 canonical 内容。

23.1A 固定公共算法：候选 stem / 扩展名使用 NFC；Win32 保留字符、Unicode Control category 和 SRS 14.17.4 的固定不可见字符表按连续段替换为 `_`，同时保留 ZWNJ、ZWJ、Variation Selector 与 emoji tag；清除 stem 两端 ASCII 空格和尾部句点。Windows 设备保留名（包括 `CONIN$` / `CONOUT$` 以及 `COM¹` / `LPT¹` 等 superscript 形式）统一在 stem 前加 `_`。最终文件名部分最多 255 UTF-16 code unit，包含扩展名和后缀，并只在 .NET text-element 边界截断。

同一目录的冲突键是 NFC + `OrdinalIgnoreCase`。分配顺序由稳定源顺序和稳定源 key 固定，第一个无后缀，后续使用 ` (2)`、` (3)`……并重新预算；已有文件不参加后缀分配。公共实现 `Midora.OutputPlanning.WindowsOutputFileNamePlanner` 是纯 Preparing 组件，不读取文件系统；无合法 UTF-16、合法化后为空、扩展名契约错误、预算容不下一个完整文本元素或稳定 key 重复均原子失败。

23.2A 固定模板：整曲 MIDI / 音频分别为 `<ProjectStem>.mid` 与 `<ProjectStem>.wav`，ProjectStem 依 Project 名称、当前 `.midora` stem、模式固定 fallback 选择；分 Track 为 `<NN> - <LogicalTrackDisplayName>.mid/.wav`，NN 使用整个 Project 的一基手动顺序且至少两位；逐 Port MIDI 为 `Port <PP>.mid`；Readme 为 `README.md`。MIDI Conductor Track Name 固定 `Conductor`，事件 Track Name 固定为一基 `Port <P> / Channel <C>`，描述原始 Channel Unit；不经过文件名合法化。多文件模式让用户选择完整输出目录，不自动增加嵌套目录。公共实现 `Midora.OutputPlanning.InitialReleaseOutputNaming` 只生成并合法化候选，不读取文件系统或推断覆盖权限。

## 14. ADR-CORE-012（已接受）：`.midora` v1 基础 Project 包垂直切片

SoundFont settings、External/Embedded 输入及相关失败条件已被 ADR-CORE-047 破坏性取代；本节其余 package 事务和严格校验决定继续有效。

决定：首个完整 package 切片只冻结当前已有领域能力可以无损重建的 JSON 边界，并贯通“内存 Project → 完整固定目录 ZIP → 严格自校验 → 同目录原子发布 → 释放句柄后重开”。本切片发布 `project.json`、`conductor-track.json`、`project-settings.json`、`export-settings.json`、`playback-settings.json`、`audio-render-settings.json`、`global-reset-defaults.json` 与 `global-event-scope-defaults.json` 的 schema v1；既有 `manifest.json`、`metadata.json` 与 `soundfont-settings.json` v1 保持不变。

Requirement trace：

- 输入：当前内存 `MidoraProject` 的 Metadata、TPQ、Conductor、Event Instrument Library 文件夹、Global Initial / Reset、Playback、Audio Render、无或 External SoundFont 设置，以及目标路径、软件版本和保存时间快照。
- 正式输出：包含全部第 16.2 节固定核心文件的确定性 Zip package；manifest 索引所有写出 entry 的 kind、schemaVersion 与未压缩内容 SHA-256。
- 边界：稳定 ID 全局唯一且小于 `nextStableId`；路径使用 `/`；JSON 严格 UTF-8 无 BOM、LF、固定字段顺序；Zip entry 顺序与时间戳固定。保存时间在事务建立不可变快照时冻结，作为本次普通保存或 Save Copy 的文件修改时间。
- 失败条件：未知/重复字段、路径或 kind 错乱、hash/schema 不一致、非法稳定 ID、非法设置组合、非空 Event Instrument/Logical Track 集合、Embedded SoundFont、序列化、自校验或发布失败均原子失败。本切片不得写出无法重开的 partial package。
- 诊断：package/container、manifest/index/hash、structure/schema、serialization/self-validation、publish/cleanup 分阶段；未知或未索引 entry 只产生打开 Info，保存时不保留。
- 持久化归属：只写 Project 源数据；canonical、缓存、诊断、Undo/Redo、Modified、设备、Mute/Solo、播放位置、任务和 UI 状态均不写入。
- 运行时归属：目标绝对路径、事务 ID、临时/备份路径、打开诊断和 External SoundFont 解析状态仅属于打开/保存会话。
- 明确非目标：本切片不发布 Event Instrument / Logical Track protobuf schema，不实现 Damaged Placeholder、旧版本迁移、Embedded SF2 复制或 WPF Modified/Undo 接线。非空对象集合和 Embedded SF2 必须显式拒绝，不能静默丢弃；后续垂直切片在发布对应 `.proto`、descriptor 与 golden bytes 后解除限制。

`project.json` v1 预留并严格定义 Event Instrument / Logical Track 索引项结构，但本切片只接受空索引；这使后续对象 `.pb` 切片无需重新解释 Project 身份、顺序、路径和名称快照。`export-settings.json` v1 仅确认固定顶层设置对象存在，不提前选择 SRS 尚未固定的默认导出模式；Audio Render 与 Playback 只保存 SRS 已固定的字段和默认值。Audio Render 的有限命名偏好按已确认的固定分 Track 模板记录为 `project-order-number-and-track-name`，不重新开放“是否包含 Track 序号”的可选分支。

后续状态：ADR-CORE-013 已解除非空 Event Instrument / Logical Track 限制；ADR-CORE-014 已解除完整性正常的 Embedded SF2 限制。ADR-CORE-012 的拒绝规则只描述基础切片当时的兼容边界，不再代表当前实现能力。

## 15. ADR-CORE-013（已接受）：`.midora` v1 重对象与损坏占位切片

决定：Event Instrument 与 Logical Track 使用独立 protobuf Edition 2024 schema v1，并以 `project.json` 的显式索引作为身份、顺序、名称快照与路径入口。对象内部保存完整源对象图、稳定 ID 和映射源码；manifest/object header/project index 三层类型与 schema 必须一致。正常对象进入领域集合，无法信任的单个对象形成独立损坏占位，不把半反序列化对象交给编译器。

Requirement trace：

- 输入：完整 Event Instrument、SubVoice、Template Event、Curve、Envelope、Logical Parameter/Mapping/C# Function、Logical Track、Segment、Logical Note 和 Parameter Lane 源对象图，以及 `project.json` 索引和对象 `.pb` 字节。
- 正式输出：稳定路径 `event-instruments/ei_<id>.pb`、`logical-tracks/lt_<id>.pb`；确定性 wire bytes；已提交 `.proto`、descriptor SHA-256 与代表性 golden bytes；打开后的正常对象或保留 ID/名称快照/路径/错误/原位置的损坏占位。
- 边界：所有嵌套稳定 ID 全局唯一且小于 `nextStableId`；集合和 map 按固定顺序编码；公共 ABI 字段号发布后不得复用；Event Instrument 文件夹归属只在 `project.json` 保存。语义上非法但结构可表示的音乐数据仍交给 Semantic Validation，不由持久化层冒充业务诊断。
- 失败条件：对象类型/schema/path/kind 错乱使整个 Project 打开失败；对象 entry 缺失、hash 不匹配、wire/UTF-8/必需字段损坏或内部 ID 与索引不一致形成损坏占位；含占位的 Project 禁止保存，直到用户删除占位。孤立对象只产生 Info，保存时移除。
- 诊断：结构兼容失败属于 package Structure；单对象损坏使用稳定 FileDamage 诊断；删除损坏 Event Instrument 会解除 Track 绑定但保留名称，删除损坏 Track 同步维护 Audio Render 显式选择。删除与 Undo token 原子恢复原位置和引用。
- 持久化归属：只保存源对象、显式顺序、稳定身份和引用；损坏占位、错误文本、Undo token、descriptor runtime 对象和反序列化缓存不写入包。
- 运行时归属：占位承载、删除/撤销 token 和打开诊断只属于当前会话；未来应用命令栈负责把 token 接入统一 Modified/Undo/Redo 工作流。
- 明确非目标：本切片不实现全局应用 Undo 栈、旧版本迁移、自动修复对象字节或保留未知 protobuf tag。

## 16. ADR-CORE-014（已接受，Q-NUI-001）：Embedded SF2 流式资源租约

决定：Project/Domain 继续只保存 Embedded SF2 的稳定 resource ID、原始文件名、SHA-256 和大小，不保存绝对路径或字节数组。Persistence 为打开会话建立可释放的运行时资源租约：导入时先把用户选择文件流式复制到会话临时快照并计算身份，全部成功后才原子设置 Project 引用；打开 package 时把合法资源流式解压、计算实际 hash/size，并返回随 `MidoraProjectOpenResultV1` 释放的绝对临时路径。Save/Save Copy 必须显式接收与当前 Project 引用匹配的可用租约，并在 staging 再次边复制边校验。

Requirement trace：

- 输入：用户选择的完全限定 SF2 路径，或 package 中 `resources/soundfonts/<resourceId>.sf2` entry；当前 Embedded 引用；manifest 记录；保存事务目标。
- 正式输出：manifest kind `embedded-resource` 且无结构 schemaVersion；settings hash、manifest hash、未压缩实际字节与大小一致的 package；打开会话可供后续 BASS 验证/加载的只读语义运行时路径和资源状态。
- 边界：复制、hash、解压和写包使用固定有界缓冲，不把整个 SF2 读入单个托管数组；资源稳定 ID 参加 Project 全局 ID 唯一性校验；租约路径与临时目录不进入 Domain、compiler fingerprint 或 package。取消、替换或清空引用后的新包只写当前引用资源。
- 失败条件：导入源缺失/不可读时 Project 与 `nextStableId` 不变；保存缺少匹配可用租约、租约文件被改写或 hash/size 不一致时事务在发布前原子失败；manifest/Zip entry 缺失、kind 错误、size/hash 不一致或解压失败时 Project 仍打开，但资源状态不可用且发声消费者必须被阻止。
- 诊断：Embedded 结构/内容损坏产生 Resource Error，但不设置 Project Modified；未引用 Embedded entry 产生 Info 并在下次合法保存移除。SF2/BASSMIDI 可加载性仍由音频 Preparing 阶段报告，不由 ZIP 完整性检查假装完成。
- 持久化归属：只有 Embedded 引用与合法资源原始字节属于 package；实际路径、临时目录、当前实际 hash、可用状态和租约所有权只属于会话。
- 运行时归属：调用方必须在 Project 关闭、替换/清空资源或打开结果不再使用时释放租约；保存自校验会释放其内部重开租约，包句柄在返回前全部关闭。
- 明确非目标：本切片不加载 BASSMIDI、不验证 SF2 内部格式、不实现文件监控缓存，也不保存未由用户明确接受的损坏 Embedded 资源表示。

Q-NUI-001 已确认：损坏、缺失或与当前 Project 引用错配的租约不能直接用于 Save/Save Copy。持久化层返回结构化 `MidoraEmbeddedSoundFontRepairRequiredExceptionV1`，列出且只列出 `ReplaceOrRebind`、`ClearReference` 两个修复动作；Preflight 或 staging 失败均不得发布文件。用户完成明确修复编辑后重新保存，输出才重新满足 settings、manifest、实际字节三方一致。

## 17. ADR-CORE-015（已接受）：`.midora` 版本预检与故障注入事务门

决定：打开流程在 manifest 索引和任何内容 hash 之前读取最小版本头。`fileFormatVersion`、`minimumReadableVersion` 或 `manifestSchemaVersion` 任一高于当前支持值时，以结构化 `MidoraPackageVersionCompatibilityExceptionV1` 在 `VersionPreflight` 阶段拒绝，不按损坏 v1 处理。低版本成功迁移所需的来源契约由 Q-NUI-002 决定；在决定前不得猜测 v0 字段、默认值或 protobuf wire 语义。

保存事务设置内部、确定性的故障注入缝，覆盖 Backup、Staging、SelfValidation、Publish 和 Cleanup；打开覆盖 Container 与 Manifest I/O。注入器只供测试与内部组合使用，不进入公共产品配置、Project、package 或诊断协议。发布前失败必须保留原目标并清理本事务产物；进入发布尝试后失败必须保留原目标、已验证临时包和备份供恢复；发布成功后的清理失败只能产生稳定 Warning，不得把已经发布的保存反转成失败。

Requirement trace：输入为现有目标、冻结保存快照、manifest 最小版本头及可重复的阶段故障；正式输出为原子发布的新包或带精确阶段/恢复路径的失败。边界是原目标字节和 Project `modifiedAtUtc` 只在发布成功后改变，临时包必须通过严格重开和逐内容相等校验。I/O、权限、格式、自检和发布异常不得越过阶段包装；清理异常不得遮蔽主要结果。绝对事务路径、注入状态和保留恢复文件只属于保存会话，不持久化。明确非目标是自动回滚一个已经成功的原子替换、自动采用未来格式、或在 Q-NUI-002 前伪造旧格式迁移。

## 18. ADR-CORE-016（已接受，Q-NUI-003 局部暂停）：MIDI Export 冻结任务与多文件事务

决定：正式任务先以专用 `CompilationPurpose.MidiExport` 和显式 Track 集合生成单一 canonical 快照；Whole Project、Per Logical Track 与 Per Port 只在该 canonical 之上组织文件。对 Logical/Event Instrument 输出，每个文件内部严格按原始 Channel Unit 分组：一个实际有事件的 Unit 对应一个且仅一个 MIDI 事件 Track，每个 Track 只包含一个 Channel，按 Port→Channel 排序。Unit 被不同 Logical Track / Instance 在不重叠时段先后复用时仍合并进同一 Track。Per Logical Track 先按 Track owner 过滤再按 Unit 分组；无音乐输出的有效 Logical Track 仍生成 Conductor-only SMF。Per Port 只为有 canonical 事件的 Port 生成文件，文件内 MIDI Port Meta 固定归一化为 Port 1，Track Name/文件名/Readme 保留原始一基 Port。Pure MIDI Track 的 Whole Project 拓扑后来由 ADR-CORE-044 / ADR-PMIDI-007 扩展为一用户 Track 一 MTrk；本段的一 Unit 一 MTrk 约束不适用于该路径。

文件名经公共合法化器形成绝对路径并冻结，同时冻结目标存在状态和一次性覆盖授权。所有 `.mid` 与被请求的 `README.md` 先写入同卷 staging 并完成 SMF Type 1 自校验；缺失目标目录以目录 rename 整体发布，已有目录逐文件原子替换/移动并保留事务备份，任一中途失败按逆序恢复。Finalizing 前允许取消并清理；Finalizing 短暂不可取消。回滚失败保留 staging/backup 路径，发布成功后的清理失败只产生 Warning。

Requirement trace：输入为冻结 Project/Track/范围/Routing/Warning 参数、canonical、原始名称、Project/file metadata、软件版本、输出目录和覆盖授权；正式输出为固定模板 SMF Type 1 文件及可选 `README.md`，或不含 partial 成功文件的失败/取消报告。边界包括同 tick canonical 顺序、Logical/Conductor 统一 EOT、Pure MIDI 自身 EOT、按 mode 决定的 Channel 10 GS→XG、Per Port 文件级 Port 归一化、目标出现竞态和 Readme 同事务。编码、自校验、staging、publish、rollback、cleanup 均有独立阶段；任务状态、绝对路径、缓存、诊断和导出时间不进入 Project。明确非目标是读取播放 buffer/Mute/Solo、在导出器中重算语义、静默覆盖新出现目标或自动修改 Project Export Settings。

当前编译器的确定性 Channel Unit 分配本身从 Port 1/Channel 1 起使用最低空闲单元，因此 Compact 对当前 canonical 分配是同形映射；Preserve 保持该导出 CompileContext 的同一分配，二者均不由编码器重分配音乐事件。若未来 Project 引入可持久化显式路由，必须在编译上下文内实现并重新证明 Compact 等价，不能把语义分配下放给文件写入器。

Q-NUI-003 只暂停 Project `ExportProjectSettings` 正式字段、schema v2 和 v1→v2 设置迁移；一次性任务参数、三模式编码、Readme、输出规划和事务不依赖该默认值决定。

## 19. ADR-CORE-017（已接受）：Audio Render 冻结任务与逐文件事务

决定：音频文件渲染必须从专用 canonical 编译上下文开始。Whole Mix 使用一个 `AudioRender` 上下文；Per Logical Track 按 Project 手动顺序为每条已选且有效 Track 建立独立 `LogicalTrackAudioRender` 上下文。未选 Track 的内容和诊断不进入该上下文。默认自然范围先按各独立上下文的实际 Event Instrument Instance 输出、NoteOff、Release/Tail 与 Reset 求得，再冻结所有成功分轨共同使用的最大 `endTick` 并重新编译；显式范围直接冻结。空 SubVoice 合法、仍计入实例 Channel Unit 并产生 Info，因此已绑定但无 Note 输出的 Track 可生成同范围静音 WAV。

正式任务在启动前冻结：canonical 结果、8,000～192,000 Hz 整数采样率、离线每 Stream sample voice 上限、Playback Master Volume、正式 Limiter 算法、已验证 SF2 内容快照、公共命名服务生成的最终绝对路径、目标存在状态和覆盖授权。Limiter 当前由 ADR-AUDIO-002 v2 定义。External SF2 被动 hash 变化不能修改 Project；只有调用方明确接受变化时本次快照可继续，并产生 Warning。Embedded SF2 通过当前有效资源租约建立任务私有快照。任务结束释放快照，不把它写回 `.midora`。

每个输出先在目标目录写唯一临时 WAV，完整渲染后严格校验 RIFF/WAVE、float32 stereo、采样率、frame 数和文件长度，再以移动或带备份替换原子发布。冻结时不存在而发布前新出现的路径不得覆盖。Whole Mix 任一失败使任务失败；Per Track 的编译、渲染、校验和发布彼此独立，失败后继续，已成功文件保留。取消清理当前临时文件、不开始后续 Track并保留已发布文件；清理失败必须报告残留路径，不能把残留物视为有效输出。

Requirement trace：输入是 Project 源数据、专用 CompileContext、有效 SF2 运行时身份、Project 音量/渲染设置和冻结路径授权；正式输出是 canonical 唯一派生的普通 RIFF/WAVE 及逐输出任务报告。边界是 `[startTick,endTick)`、同一分轨最终 sample 长度、Mute/Solo 不参与、公共输出命名和普通 RIFF 上限全任务预检。任务级失败包括无有效目标、零范围、无有效 SF2、公共 Worker 准备失败、路径规划失败或任一文件超过 RIFF 上限。绝对路径、canonical、sample-domain plan、进度、诊断、临时/备份文件和结果只属于运行时，不持久化、不进入 Undo/Redo。明确非目标是 UI、并发多渲染任务、按 Port 音频、RF64、编码格式/位深/声道选择、tail、断点续渲和任务历史；应用级“同一时间单个音频任务及开始渲染前自动 Stop”由 NUI-10 任务协调器统一实现。

## 20. ADR-CORE-018（已接受，Q-NUI-004 待确认）：单一应用任务与本机偏好边界

决定：主应用使用唯一 `ApplicationTaskCoordinator` 仲裁播放、预览、显式编译、Project 生命周期、保存、MIDI Export 和 Audio Render。任务只允许直接 admission 或拒绝，不建立命令队列；只有 SRS 明确列出的命令可以自动 Stop。任务持有与操作对应的 UI 锁级别，并通过可计数 lease 持有 `ProjectCompilationSession` 编辑锁，避免嵌套任务或播放清理错误地解除其他所有者的锁。

Project 切换顺序固定为播放清理、Function Draft 处理、未保存处理、实际切换。Draft Apply 在取得编辑锁前执行；后续未保存处理和实际切换在锁内执行。Save/Save Copy 的保存事务不接受取消。播放清理失败时，Save 类命令继续并保留错误；其他会改变 Project/产物的命令返回与原命令绑定的一次性 continuation，只有调用方明确继续后才执行。

Application Preferences 是与 `.midora` 独立的当前 Windows 用户本机状态。非 UI 切片保存正式音频偏好和五类 picker 最近目录；音频偏好只能在 Stopped 且应用空闲时提交，实际变化清除 sample-domain cache 并通知应用 composition 重建实时后端。读写失败回到 SRS 安全默认值并生成非 Project notice，不设置 Modified，不进入 Undo/Redo。

Requirement trace：输入为当前应用/播放状态、一个任务请求、Project 切换决策回调和已验证偏好；正式输出为结构化任务结果、阶段/锁投影及本机偏好快照。边界是单活动任务、无队列、明确 auto-Stop 白名单、一次性风险继续、Stopped-only 音频设置和原子偏好发布。异常、取消、播放 cleanup、偏好 I/O/格式/版本失败分别保留类别；所有路径释放自身 lease。任务状态、绝对输出路径、设备运行状态和偏好均不进入 Project/canonical；冻结的导出/渲染请求仍由既有 canonical 消费链执行。明确非目标是 WPF 表面、通知展示、任务历史持久化、多 Project/多任务并发、Preference sync/profile/import/export 和纯 UI 布局偏好。

Q-NUI-004 只涉及 SRS 未固定的本机表示：当前实现使用 `%LOCALAPPDATA%\Midora\preferences-v1.json`、source-generated UTF-8 JSON v1、1 MiB 读取上限和同目录原子替换。该选择不影响 `.midora`、可听语义或跨机器文件兼容；产品所有者若选择其他本机存储，可替换 store 而不改变协调器或偏好领域契约。

## 21. ADR-CORE-019（已接受，Q-NUI-005 局部暂停；Folder/Unbound/独立排序部分已由 ADR-CORE-045 取代）：Project History 与编译事务

决定：初版使用每 Project 一个、跨编辑器统一的线性 History。正式 Project 编辑先只读 Prepare，再以 `Apply/Undo` 可逆动作和冻结 `ProjectChangeSet` 进入 `ProjectCompilationSession`；每次 Execute、Undo、Redo 都在同一 Project Edit Lock 边界内完成源变更和 Incremental Compile。语义错误可以形成不可消费 canonical 并进入 History；基础设施异常必须反向恢复源数据并 Full Compile 校验，不得留下“源已变但 History 未记录”的半事务。

Modified 不使用简单“Undo cursor 是否为零”。每个会话历史状态有不持久化的稳定 state ID，Save 成功把当前 ID 设为保存点；Undo/Redo 只有回到同一保存点才清除 Modified。Undo 后建立新分支会丢弃 redo entries，但不会让已经不可达的保存点与新分支错误等价。迁移/损坏回退等非普通命令变化以 external dirty reason 叠加，普通 Undo 不清除，成功 Save 才清除。

未保存新 Project 的初始构建不进入 History且可保持 `IsModified = false`，但因没有持久化来源，`NeedsSaveBeforeClose = true`。Save Copy 不调用保存点提交，不改变当前 Modified、History 或来源。History entry 在会话内保留准备好的反向数据；SRS 没有定义容量或合并策略，初版不设置会静默丢失旧 Undo 的固定条目上限，手势级合并由调用方形成单个 prepared command。

Requirement trace：输入为 Project、来源状态、可逆 command、保存成功和 external dirty reason；正式输出为全 Project History、操作名称、Modified/关闭保护和同步 canonical。边界是单线性分支、无操作不建历史、Project Edit Lock 排他和 command change-set 冻结。失败时恢复源并 Full Compile；rollback 再失败必须聚合报告。History/state ID/反向对象不持久化、不影响 canonical fingerprint；Project 源本身照常持久化。明确非目标是 Draft/文本本地 Undo、WPF focus routing、历史持久化、autosave/crash recovery，以及 Q-NUI-005 决定前所有会分配新稳定 ID 的 Undo 命令。

首批具体命令采用同一约束：Prepare 完成引用、名称、确认、时间范围、重叠和可恢复索引校验；Apply/Undo 复用原对象与原稳定 ID。此处原有的 Track/Instrument/Folder/Damaged Placeholder、显式取消绑定、Library 组织和独立排序命令矩阵只记录当时实现；当前 Project 所有权、父子移动、级联删除和排序以 ADR-CORE-045 为准，Folder、Unbound、独立 Library/global child order 均不再是正式模型。Last Known Instrument Name 的旧维护规则同样只保留为历史证据，不得据此恢复 Unbound 工作流。

Conductor 更新使用“同稳定 ID 的不可变记录替换”，Undo 恢复原记录对象；tick 0 Tempo/Time Signature、同 tick 唯一性及 SMF Tempo 可表示性在 Prepare 阶段阻止非法输入。本文原先记录的 Project Playback / Audio Render Settings History 命令已由 ADR-CORE-048 破坏性取代：Playback 属于 Application Preferences，Audio Render 参数只属于一次任务 Draft，二者均不再进入 Project History/Modified。

第三批命令覆盖不分配 ID 的 Logical Note、Logical Parameter Lane/Point 与 Event Instrument Description/Color/Root Note。Note 编辑不把裁剪区当作数据合法边界；Point 正常编辑必须能从当前绑定定义验证值域和类型，断裂 Lane 只保留、删除或走显式重绑定修复。重绑定的 Clamp/Discard 由调用方每次明确选择，目标 Enum 还强制调用方确认整数兼容不代表语义兼容；转换后的点沿用原稳定 ID，Undo 恢复原对象图。Q-NUI-007 待确认期间，整数中点暂按现有 Mapping `Round` 一致的 AwayFromZero，目标 Enum 的保留点转为 Step；该局部选择不得扩散成持久化或编译器的新隐式默认。

第四批命令覆盖 Event Instrument Template Length、Isolation、Overlap、Lifecycle、Loop 与 SubVoice 基础结构。Template Length 的应用层下界由 Note end、瞬时事件/Curve Point 的半开边界和 Loop End 共同决定；Initial State 与 Envelope 时长不参与。关闭 Isolation 保留现存不兼容数据并允许 canonical 产生正式诊断，不能借编辑命令删除数据；受限制的 Loop 仍允许显式禁用。删除 SubVoice 拒绝最后一条、对非空内容要求确认，并把指向该 Voice 的 Logical Parameter Mapping 作为同一可逆事务删除/恢复。Compiler 另外对四类公开策略枚举增加定义域诊断，防止损坏或未来未知数值落入 switch 默认路径。

第五批命令覆盖既有 Template Event 的 Note、CC、Bank、Program、Pitch Bend、RPN、NRPN、Pitch Bend Range 属性及删除。编辑保持事件和三个 Mapping Chain/Target Settings 的对象身份；Note end 或瞬时事件 `tick + 1` 超过当前 Template Length 时原子延长。编辑到同 tick/同状态目标时保留当前被编辑事件并删除冲突旧对象，Undo 按原索引恢复；Pitch Bend Range 与 RPN 0 作为同一状态目标处理。Bank MSB/LSB 的存在性与数值分离，存在活动 Mapping Step 的组件不得被移除。Compiler 同时拒绝未定义 Template Event Kind 与 Curve Interpolation，避免损坏/未来枚举值越过验证。

第六批命令覆盖既有 Value Curve 的 Target Settings、Point 更新/删除和整条 Curve 删除。Point 仍以稳定 ID 定位，更新使用同 ID 的不可变记录替换，移动超过 Template Length 时按 `tick + 1` 半开边界原子延长；Target Overflow 为 Fail 时拒绝超值域基础点，为 Clamp 时允许保存并由 canonical 归一化。删除 Curve 只移除曲线对象，不触碰同目标离散事件。Compiler 修正首点前语义：事件曲线在第一个点之前不输出隐式 0，点集在本次编译准备期排序一次后复用，最后一点之后仍由 MIDI Channel 状态自然保持。

第七批命令把 Project Initial、Project Reset、Event Instrument Initial 与 SubVoice Initial 的单目标更新统一到 `MidiValueTarget`。Null 表示删除该层 override；字典目标必须区分“缺失”与数值 0，Undo 恢复此前精确存在性和值。该 Initial/Reset/Mapping 目标面中的 CC91/CC93、Channel Mode、未知/不匹配 target identity 与原始值越界在 Prepare 阶段拒绝；此限制不适用于 Pure MIDI Track 的直接事件。Project 级变化使全部编译范围失效，Instrument/SubVoice 级变化只失效相关 Instrument。Initial State 不扩展 Template Length，Reset 仍只允许 Project 级。Compiler 同时把未定义 `MidiValueKind` 纳入 `MIDORA1260`，避免未知枚举以 number 0 绕过目标验证。

第八批命令覆盖既有 Envelope Preset 的全 ADSR-like 快照更新与删除。Isolation 关闭时按 SRS restricted data 规则拒绝普通编辑，但允许删除作为修复入口。删除引用判定覆盖事件参数和 Logical Parameter Mapping 中的全部 Step，不因 Chain/Step 禁用而忽略持久引用；被引用删除要求显式确认，且只移除 Envelope 对象，故 Step 保留原 Envelope ID 并形成可持久化的正式断裂引用。Undo 恢复同一 Envelope 对象、原索引和完整数值，不影响 Template Length 或稳定 ID 计数器。

第九批命令覆盖既有 C# Mapping Function 的名称、函数体和声明 Context 字段集合更新及删除。ABI 固定为 v1，不暴露编辑入口；函数体按精确文本保存，不 Trim，仅执行有效 Unicode 与 1,048,576 scalar 持久化上限，编译错误按 SRS 允许进入 Project 并由 canonical 诊断。声明字段按 Ordinal 集合冻结和排序，参与 source fingerprint/兼容性而不改变源码缓存键。任何 Step 持有该 Function ID 都视为引用，不受当前 Operation/Enable 状态影响；确认删除后保留断裂 ID，Undo 恢复原对象与引用。

第十批命令覆盖既有 Mapping Chain/Step 的启用状态、Step 全配置、手动排序、Step 删除和整链删除，统一从事件参数与 Logical Parameter Mapping 按稳定 Chain ID 定位。应用层允许 SRS 明确可保存的断裂引用、倒置/空范围和未完成配置，仅拒绝不能可靠持久化的非有限数、空引用 ID 与正常编辑器不应生成的未知枚举；只有启用 Chain 中启用 Step 的错误配置进入 compiler 诊断。该批最初以 enabled 空链 sentinel 表示整链删除并保留 Chain ID/Target Settings；这一开发期表示已由共享 Mapping owner 重构与 ADR-CORE-040 取代：Note owner 强制保留，非 Note Event/Logical Parameter owner 物理删除。Compiler 同时把活动 Step 的未知 Source/Operation/InputOverflow/DivideByZero 纳入 `MIDORA1270`，并让零宽 Remap 按该 Step 的明确 DivideByZero policy 产生目标最大值、目标默认值、0 或失败。直接矩阵测试逐项锁定 12 种内置 Source、除 Custom C# 专项外的 12 种内置 Operation、三种 Remap InputOverflow 和两向 Divide/四种 DivideByZero policy。非空链删除继续采用显式确认。

第十一批命令覆盖不需要批量迁移既有 Lane 的 Logical Parameter Definition 属性、引用保留删除、Logical Parameter Mapping 属性和事件参数 Target Settings。display range 只影响展示，仍进入 History/Modified，但从 Track source fingerprint 排除并使用空 change-set；名称可能影响 MappingContext，default/legal range、Mapping source/target/order/target policy 均失效相关 Instrument。相同 SubVoice/目标的 Logical Parameter Mapping 在显式策略编辑时作为一个共享组同步，Undo 为每个对象恢复原值；移动 Mapping 到现有组采用该组既有一致策略，损坏且不一致的组先拒绝普通移动。删除 Definition 或 Mapping 保留全部外部断裂 ID、内部 Chain/Step 与稳定身份。Compiler 防御未知 Parameter Type 及越界 Enum item。Q-NUI-009 决定前，不实现类型、Enum 模式/数值/顺序/删除或任何需要改写全 Project Lane Point 的 Definition 变更。

第十二批命令覆盖 Project Metadata 六个用户字段与 Logical Track 颜色覆盖。Metadata 作为一个原子快照更新，项目名称/用户版本按 256 scalar 单行文本，作者/原曲/版权按 4,096 scalar 单行文本，备注按 65,536 scalar 描述文本校验；所有字段保留原 Unicode scalar 序列和空白，不执行 normalization 或 Trim。创建时间、修改时间和工程总耗时不进入可编辑快照，活动单调计时 owner 不被打断。Track color override 只保存 nullable opaque sRGB。两者均进入 History/Modified 和 `.midora`，但不属于音乐语义，使用空 change-set、复用 Track 编译缓存并保持 canonical fingerprint。

## 22. ADR-CORE-020（已被 ADR-CORE-047 取代）：SoundFont 选择的两阶段验证与可撤销运行时切换

本节仅保留开发历史，不再构成当前需求或兼容承诺。

决定：不分配稳定 ID 的 External SF2 选择、替换、重新绑定和取消选择统一进入 `ProjectDocumentSession`。选择先在 Project 事务外完成允许目录解析、完整 SHA-256/大小计算，再经注入的 `ISoundFontLoadabilityValidator` 使用正式固定版本 BASS/BASSMIDI 执行 `BASS_MIDI_FontInit`、全 preset/sample `BASS_MIDI_FontLoad` 和受检释放；随后重新解析并完整复核内容身份，只有仍与准备结果完全一致时才提交。提交命令在一个 Project Edit Lock/History 事务内同步替换可持久化 SoundFont union 和 `ProjectCompilationSession.EffectiveSoundFontPath`；Undo/Redo 同步恢复两者。取消选择使用同一命令把两者清空。

SoundFont 不属于 canonical MIDI 语义，命令使用空 `ProjectChangeSet`；Incremental Compile 复用 Track，并清除会话 sample-domain cache。不存在、不可读、路径越界、加载失败、取消、验证期间内容变化或最终锁冲突都必须在建立 History entry 前失败，Project 引用、运行时路径、Modified 和 canonical 保持原状。相同引用与相同解析路径的重复选择以及已清空状态的重复取消是无操作。

Requirement trace：输入为当前 `.midora` 绝对路径、用户选择的绝对 SF2 路径、当前 Project/History、正式后端加载验证器和取消令牌；正式输出为 External 引用（相对路径、原文件名、SHA-256、大小）、本会话有效绝对路径及可撤销 History entry，或零源变更的结构化失败。边界是仅允许 Project 根或直属 `soundfonts/`、逐分量精确/唯一 ignore-case 解析、完整字节身份复核、Project Edit Lock 排他及 source/runtime 同步 Undo。绝对路径、BASS error、验证中间状态和 native handle 只属于运行时，不进入 `.midora` 或 canonical。明确非目标是文件监控/验证缓存、WPF 组合，以及 Q-NUI-005 决定前会分配 Embedded resource ID 的选择/替换；Embedded 旧/新资源租约跨 Undo 分支的所有权也必须在该分支实现时一并闭合。

## 23. ADR-CORE-021（已被 ADR-CORE-047 取代）：External SoundFont 完整验证缓存与文件监控失效

本节仅保留开发历史，不再构成当前需求或兼容承诺。

决定：External SF2 完整验证缓存只在当前打开会话内存在。缓存命中键同时包含解析后的绝对路径、当前 Project 引用的已保存 SHA-256/大小、Windows volume serial + file ID、当前文件大小和原始 FILETIME 最后写入值；缺少其中任一项都不得复用完整 hash 结果。完整读取使用不共享写入/删除的异步文件句柄，在同一句柄上读取前后各获取一次 `GetFileInformationByHandle`，身份、大小或时间发生变化时拒绝该结果。缓存仍允许调用方强制完整复核。

可读文件建立 `FileSystemWatcher` 只作为失效提示；Changed/Created/Deleted/Renamed/Error 都递增失效代次并使下一次验证重新执行完整 SHA-256。监控不能替代每次消费前对当前文件 stamp 的同步检查；验证期间收到失效时最多自动重试两次，持续变化则失败，不能发布一个已知过期的“可用”结果。被动验证只返回当前 hash/大小及 mismatch Warning，不修改 Project 引用或 Modified。

Requirement trace：输入为当前 `.midora` 绝对路径、External source reference、当前文件系统状态、强制复核标志和取消令牌；正式输出为精确/唯一 ignore-case/缺失/歧义/不可读解析状态、当前内容身份、hash match、文件 stamp 和会话缓存，或持续变化/取消失败。边界是 Windows 初版文件身份、完整原始字节 SHA-256、单 Project 会话 watcher 和缓存失效代次；Win32 handle、绝对路径、watcher、stamp 与缓存计数均只属于运行时。明确非目标是把监控结果持久化、用 mtime 代替首次 hash，以及尚未完成的 Project 打开/首次音频任务状态机接线。

## 24. ADR-CORE-022（已被 ADR-CORE-047 取代）：Project SoundFont 运行时可用状态与音频消费门

本节仅保留开发历史，不再构成当前需求或兼容承诺。

决定：每个打开 Project 建立一个非持久化 `ProjectSoundFontRuntimeSession`。打开后状态先为 NotVerified/Verifying；无引用进入 NoReference，External 必须经 ADR-CORE-021 完整 hash/cache 验证和正式 `ISoundFontLoadabilityValidator`，Embedded 必须持有与 source reference 完全匹配的可用资源租约并通过同一后端加载验证。缺失、歧义、不可读、格式/损坏、后端不可用和 Embedded 资源错配分别保留稳定状态，不阻止 Project 打开、编译或 MIDI Export。External hash mismatch 与唯一 ignore-case fallback 在加载成功时仍为 Available，但 `RequiresWarning=true`，绝不被动改写 source hash。

验证开始先在 `ProjectCompilationSession` 锁内按预期 source reference 清空 effective path；验证成功也只在 source reference 仍相同时提交路径，旧异步结果不能覆盖并发选择/Undo/Redo。任一 SoundFont source History 变化通过 CompilationChanged 立即把新引用标为 VerificationRequired 并清空选择命令临时设置的路径，要求运行时再验证后才可消费。watcher 失效同样清空路径；即使 Project Edit Lock 正由播放持有，资源失效也允许清空未来消费入口，并由 `ApplicationTaskCoordinator` 自动 Stop 当前播放/预览、释放编辑锁。旧资源 watcher 只绑定其已验证 reference，不能清除后来选择的新资源。

播放和预览 admission 在启动前调用同步 stamp gate：只有 runtime 状态 Available、External 缓存未失效且绝对路径/file ID/size/mtime 仍匹配，或 Embedded 租约快照路径仍存在，才允许进入 PlaybackController。stamp 不匹配时先失效并拒绝启动，完整复核由异步 Refresh 完成。Audio Render 仍使用其任务私有 SF2 冻结流程并执行完整内容复核，不复用实时路径替代冻结。

Requirement trace：输入为打开 Project 的 source reference、当前 `.midora` 路径、可选 Embedded 资源租约、验证缓存、正式后端验证器、编译会话和播放任务协调器；正式输出为结构化 runtime availability、有效绝对路径、Warning 标志和音频 admission 结果。边界是单 Project session、预期 reference 比较、Project Edit Lock、监控失效、启动前 stamp gate 与自动 Stop。失败只改变运行时可用状态，不改 Project/Modified/canonical；状态、绝对路径、watcher、cache、lease 和 BASS error 不持久化。明确非目标是 WPF 状态展示，以及 Q-NUI-005 决定前 Embedded 选择/替换 History 的 ID 与跨分支租约所有权。

## 25. ADR-CORE-023（已接受）：Canonical CompileContext 冻结摘要

决定：每个成功或失败的 `CanonicalCompiledResult` 都必须携带只读 `CompilationContextSummary`，由编译器从本次 `CompilationRequest` 和实际解析后的结束范围一次性冻结。摘要保存编译 Purpose、请求/解析范围、结束范围来源、Track/SubVoice 选择及 Warning-as-error 策略，并提供全项目、播放、预览、MIDI 导出准备和音频渲染准备的确定派生分类。调用方之后修改请求中的 `HashSet` 不能改变既有结果。

结束范围来源严格区分显式请求、Project End Marker 默认和自然内容结束；不能只保存最终数值后丢失来源。`IncludesAllTracks` / `IncludesAllSubVoices` 与空显式集合不同：前者表示未限制集合，后者表示调用方明确选择零项。成功、语义失败和后续 partial 失败使用同一摘要路径；Full/Incremental 对同一请求必须逐字段一致。

Requirement trace：输入为完整 `CompilationRequest`、当前 Project End Marker 和最终 resolved end tick；正式输出是 canonical 内不可变上下文摘要。边界是请求集合按稳定 ID 排序复制、nullable 显式 end 保留、消费者类别只由固定 Purpose 枚举推导。非法 Purpose 仍由 Semantic Validation 诊断，摘要原样保留失败请求以便定位。摘要、选择快照和失败策略属于本次编译运行时结果，不写入 `.midora`，不影响事件 fingerprint，也不允许消费者借此重新解释 Project 语义。明确非目标是 UI 诊断显示过滤、持久化 CompileContext、Mute/Solo 或设备/SoundFont 状态。

## 26. ADR-CORE-024（已接受）：范围内资源分配与结构化峰值统计

决定：Overlap 检查、Channel Unit 分配和资源峰值只接收与本次 `[startTick,endTick)` 相交的已展开实例；结束不晚于 start 或开始不早于 end 的实例不得因范围外重叠/资源不足阻止当前范围结果。相交实例仍保留完整原始事件供范围起点状态恢复和结束硬裁剪，故该过滤不改变范围内 Mapping、Reset 或 NoteOff 语义。

每条 `ChannelUnitAllocation` 同时保存单个 Logical Note instance ID 和共享 allocation group ID：前者用于定位具体触发，后者表达 Per-Note Isolation 关闭时多个实例共享同一 Channel Group。统计补充相交 Segment 数、参与 Instrument/SubVoice 数和实际 Port 数。首次资源不足冻结精确 shortage 区间、请求/可用 Channel Unit 数，以及当时占用或请求资源的 Track、Segment、Logical Note、Event Instrument、SubVoice 稳定 ID 集合；ID 集合排序、去重、只读。后续失败仍可产生诊断，但首个失败点作为确定性统计入口。

Requirement trace：输入为本次范围内 Raw Instance、确定排序的 allocation group 和 256-unit 位图；正式输出为 instance/group 双身份占用区间、资源统计或 `ResourceShortageDetails`。边界是最低可用 Unit 分配算法和 16×16 上限不变，空显式范围统计为零，超过范围的未来峰值不参与。资源不足仍使结果 partial/不可消费，诊断和结构化统计必须 Full/Incremental 等价。上述数据只属于 canonical 运行时结果，不持久化，不允许消费者重新分配 Port/Channel；明确非目标是语义级 Voice Stealing、自动合并 SubVoice 或提高初版资源上限。

## 27. ADR-CORE-025（已接受）：Canonical 细粒度来源链与生成来源

决定：`SourceReference` 在既有 Track/Segment/Logical Note/Event Instrument/SubVoice/Template Event 之外，保存 Logical Parameter Definition、Logical Parameter Mapping、Value Mapping Step、C# Mapping Function、Value Curve 和 Envelope 稳定 ID。Mapping 运行异常在 Mapping Engine 内捕获实际失败 Step 及其 Function/Parameter/Envelope 引用，向外层诊断传播；语义引用诊断直接绑定被验证对象。成功的模板/曲线/Logical Parameter canonical 事件也保存其确定性的最终 Mapping 来源。

由于单个 MIDI 事件可能由多条顺序 Mapping 共同合成，而当前 `SourceReference` 是一个主来源位置，成功事件记录最终生效 Mapping/Step；失败事件记录实际抛错 Step。它不声称替代未来的完整多来源图。`SourceOrigin` 另外区分 Template Event、Value Curve、Logical Parameter Mapping、合并 Initial State、Project Reset Defaults、Range Restore 和编译器硬边界清理；Role 继续表示同 tick 排序语义，Origin 只解释来源，不能改变排序。

Requirement trace：输入为编译展开时的源对象稳定 ID、实际 Mapping Step 异常和编译器生成事件原因；正式输出为可定位、可 fingerprint 的 canonical/diagnostic source trace。边界是缺少该层来源时保留 default ID，不以名称或集合位置替代身份；成功事件与失败诊断的新增来源字段进入 result fingerprint 和 Full/Incremental 逐字段 oracle。来源链、异常对象和生成原因不持久化，不进入 Mapping ABI，也不允许消费者重算 Mapping。明确非目标是持久化诊断、多来源有向图、UI 导航展示和修改同 tick 事件排序。

## 28. ADR-CORE-026（已接受）：范围硬边界活动 Note FIFO 与完全确定排序

决定：范围裁剪器按 `(Port, Channel, pitch)` 保存活动 NoteOn 的 FIFO 来源队列，而不是只保存活动数量。真实 NoteOff（包括兼容输入中的 velocity 0 NoteOn）只释放队首实例；范围硬结束为仍活动的每个实例逐一生成 NoteOff，并保留原 Logical Track、Segment、Logical Note、Event Instrument、SubVoice 和 Template Event 来源，只把 tick 与 Origin 改为本次硬边界清理。

所有活动音键固定按 Port→Channel→pitch 排序，队内按原 NoteOn 顺序释放；需要清理的 Channel 和污染目标也固定按 Port→Channel→target 排序。Canonical 比较器在系统 Role、Port/Channel 和显式 StableOrder 完全相同时，继续用来源稳定 ID、语义 target/group、MIDI packed value、source tick 与 Origin 建立完全顺序，不能依赖 `Dictionary`/`HashSet` 遍历或 `List.Sort` 对相等元素的内部行为。

空 SubVoice 的 `MIDORA1225 / Info` 在范围及显式 SubVoice 过滤之后生成：只有本次范围内确有实例且该 Voice 参与本次选择时才报告。该 Info 不进入 Segment 增量缓存，避免范围外实例或未选 Voice 的旧缓存诊断污染当前 CompileContext。是否保留此 Info 的 SRS 文字冲突另见 Q-NUI-010。

Requirement trace：输入为已分配 canonical 事件、范围前及范围内 NoteOn/NoteOff 流、资源占用、SubVoice 选择和 `[startTick,endTick)`；正式输出为逐实例配对、来源可定位且全序确定的硬边界 NoteOff/Reset，以及只针对实际参与 Voice 的空 Voice Info。边界是同 pitch FIFO、真实 NoteOff velocity 0、endTick 特殊补充事件和低号 Port/Channel 顺序；不同 pitch 的边界释放顺序不形成用户音乐优先级。状态和诊断只属于本次编译，不持久化；明确非目标是 Voice Stealing、补发范围前 NoteOn 或改变正常用户事件的显式顺序。

## 29. ADR-CORE-027（已接受）：CompileContext Debug 诊断收集门

决定：`CompilationRequest.CollectDebugDiagnostics` 是本次编译的显式、默认关闭的诊断收集策略，并冻结进入 `CompilationContextSummary`。开启时 canonical diagnostics 追加两个稳定 Debug：`MIDORA2900` 记录 Purpose、实际范围、Track/SubVoice 选择数量和 Warning 策略；`MIDORA2901` 记录成功或明确失败阶段及确定性的 Track/Segment/Instance/Instrument/SubVoice/Event/Channel Unit/Port 统计。成功、语义失败和后续 partial 失败都可返回 Debug。

Debug 不进入 result fingerprint，不改变事件、分配、统计、成功判定或 Warning 原级别；Info/Debug 永远不触发 Warning-as-error。正式 canonical diagnostics 中不写墙钟耗时、缓存命中数或增量重编次数，因为这些运行历史会破坏 Full/Incremental 逐字段等价；此类实现执行遥测继续只通过 `LastTelemetry` 暴露，不属于正式结果。

Requirement trace：输入为 CompileContext Debug 收集开关和本次确定性编译结果；正式输出为可选、来源 tick 可定位且 Full/Incremental 相等的 Debug 摘要。边界是默认不收集、显示级别仍由未来 UI 独立过滤、执行遥测与正式诊断分离。Debug 及遥测均不持久化，不进入 Project/Modified/Undo 或消费者语义；明确非目标是 UI 诊断面板过滤、持久化 Debug、把性能时长纳入 canonical，或用 Debug 改变失败政策。

## 30. ADR-CORE-028（已接受）：播放启动、停止与冷重启失败原子性

决定：主播放在修改光标/任务状态前预检非反向的显式及 Loop 有效范围和当前有效 SoundFont；预检失败保持 `Stopped/None`，不 Prepare、不取得编辑锁。进入 Preparing 后先取得 Project Edit Lock，再在锁内重新读取并检查 SoundFont 路径，冻结本次启动资源入口；避免预检与实际启动之间的资源切换竞态。Preview 使用相同的“锁前预检、锁内复核”。

Prepare、compile、plan 或 backend start 失败时进入 Error，同时清空 active canonical/plan/tempo、清除 ActiveTaskKind 并释放自身编辑锁。Stop 以及 Seek/Loop 冷重启的 backend stop 失败执行同一清理；主播放保留失败发生前的当前 tick，若旧 backend 已成功停止而新起点 Preparing 失败，则保留新起点。Error 后再次 Play 先 Reset backend、清 sample-domain cache 和旧错误，再重新 Preparing。进入 Stop/Seek 前即可发现的有效范围错误直接拒绝，不能先停止一个仍健康的播放任务。

Error 且 `ActiveTaskKind=None` 是无活动音频任务的静止运行时状态，允许用户设置非负 Playback Cursor；该操作只更新 Project session cursor，不调用 backend、不清除 `LastError`、不把状态伪装成 Stopped，也不绕过下一次 Play 必须先 Reset 的恢复门。Preparing、Stopping、Preview 活动任务或其他非静止状态仍拒绝这种定位。

Requirement trace：输入为当前播放状态、光标、显式/Loop 范围、有效 SF2、Project 编辑锁和 backend 生命周期结果；正式输出为唯一活动 Main/Preview 任务，或完全释放任务身份/派生引用/锁且位置可恢复的 Stopped/Error。边界是零长度范围仍可 Preparing 后立即 Stopped，缺失 SF2 属于启动 admission 失败而非不可恢复 backend Error，Stop 失败不允许残留“活动任务”。状态、错误、光标、锁和 sample plan 均只属于运行时，不持久化、不修改 Project/canonical；明确非目标是自动选择其他 SF2/设备、Pause/Scrub、抢占另一播放任务或吞掉清理异常。

## 31. ADR-CORE-029（已接受）：Mute/Solo 冷恢复重路由到活动 Channel Unit

决定：播放中重新启用 Track 仍使用当前 render frontier 的 Playback CompileContext 获取 RangeRestore 非 Note 状态，但不得直接使用该冷编译结果重新紧凑分配的 Port/Channel。每条恢复事件以 `TrackId + LogicalNote instance ID + SubVoiceId` 找到正在播放的原 canonical allocation，把 MIDI channel status 和 Port 改写为活动计划的 Channel Unit 后再与 EnableSource 同批发送。

冷结果只提供编译器正式解析的当前状态值和顺序，活动结果只提供已经冻结且 backend 正在消费的路由；播放层不重新解释 Mapping、Lifecycle 或分配规则。来源身份缺失、活动 allocation 不存在或恢复消息不是 channel voice 时，整批 monitoring commands 在调用 backend 前失败，控制器回滚本次 Mute/Solo 集合变更，不能向错误 Channel 发送部分恢复状态。

Requirement trace：输入为活动 canonical/plan、render frontier、重新启用 Track 集合和该 tick 的可消费 Range 编译结果；正式输出为 EnableSource 以及仅恢复必要非 Note 状态、路由到活动 Port/Channel 的原子命令批。边界是范围前 NoteOn 不补发、未来事件继续由原 plan source filter 放行、禁用清理只作用于该 Track 当前 allocation。监听集合、重路由表和命令只属于当前播放运行时，不修改 canonical、Project、MIDI 导出或音频渲染；明确非目标是重新分配活动 Stream、重触发错过的 Note、全局 All Sound Off 或用冷编译 Port/Channel 替换活动计划。

## 32. ADR-CORE-030（已接受）：Reset Playback Engine 两阶段尽最大努力清理

决定：显式 Reset 在非 Stopped 状态先执行普通 Stop 类清理；即使该阶段报告失败，仍必须继续调用后端 Reset，强制销毁或重建 Stream、连接和播放缓存。后端 Reset 成功即视为恢复完成：清空活动 canonical/plan/tempo、任务身份、编辑锁和 sample-domain 缓存，清除旧播放错误并进入 Stopped；先前 Stop 失败已被更强的完整 Reset 覆盖，不阻止恢复。只有 Reset 本身失败才保持 Error 并抛出；若 Stop 与 Reset 均失败，以 AggregateException 保留两项原因。Error 状态直接 Play 复用同一 Reset 路径，Reset 失败不得进入 Preparing。

Reset 不使 canonical compiled result 缓存失效，Stopped 状态也直接执行后端 Reset 且不额外 Stop。无论 Reset 成败，控制器自身派生引用、任务身份和编辑锁都在返回或抛出前释放；后端失败时 sample-domain 状态视为不可信并失效。Project 源数据、Modified 和 Undo/Redo 不受影响；活动主播放的光标仍按普通 Stop Cursor Behavior 处理，原本处于 Error 或 Stopped 时保留已有光标。

Requirement trace：输入为当前播放状态、活动任务、后端 Stop/Reset 结果和 sample-domain cache；正式输出为完整重置后的 Stopped，或不残留活动任务/编辑锁且保留全部失败原因的 Error。边界是 Stop 清理失败不能短路 Reset、Stopped 不调用 Stop、Error 后 Play 必须先恢复后端。Reset/错误/缓存均只属于播放运行时，不持久化、不修改 canonical 或 Project；明确非目标是吞掉 Reset 失败、重编译 Project、重置 Mute/Solo、自动换设备或在 MIDI Export/Audio Render 模态互斥之外排队命令。

## 33. ADR-CORE-031（已接受）：Preparing 发布与预览编译锁定顺序

决定：主播放和全部 Preview 在发布 `Preparing` 状态之前必须已经取得 Project Edit Lock；因为 `StateChanged` 是同步可重入通知，不能先通知 Preparing 再留下任何可编辑源数据的窗口。Event Instrument、SubVoice 和 Segment Preview 的 `PreviewCompiler` 读取及临时上下文构造也必须位于同一锁租约内，直至 backend 停止或启动失败，不能先编译未锁 Project 再锁定并消费旧快照。

Preview 仍在锁前检查当前任务互斥和有效 SoundFont，在锁内重新检查 SoundFont；编译抛出、返回不可消费结果、设备 Prepare 或 backend Start 失败均清空任务/派生结果、释放锁并进入 Error。Error 后的下一次 Preview 先走 ADR-CORE-030 的完整 Reset，再重新取得锁和编译；Preview 明确忽略 Mute/Solo，且不移动主播放光标。

Requirement trace：输入为 Project 源对象图、预览请求、SoundFont、播放互斥状态和同步状态订阅者；正式输出为锁定源快照派生的 canonical Preview 计划，或无活动任务/无锁泄漏的 Error。边界是锁必须先于 Preparing 通知可见、预览编译全程持锁、成功播放持续持锁，Stop 后释放。锁、临时 Preview Project、状态通知和计划只属于运行时，不持久化、不修改 Project/canonical cache；明确非目标是后台并行编译、预览抢占主播放、允许 Preparing 编辑或把 Preview 临时对象写回 Project。

## 34. ADR-CORE-032（已接受）：运行中关闭 Loop 的冷重启语义

决定：活动主播放关闭 Loop 时，旧计划已被裁到原 loopEnd，不能只清空 Loop 标志后继续消费。控制器必须在当前 tick 执行与 Seek/循环回绕相同的 Stop 清理，再按原始播放请求终点冷启动并恢复非 Note 状态；新计划完成后保持 Stopped，不再回绕。若当前 tick 已达到或超过原显式 endTick，关闭 Loop 立即按普通完成路径 Stop，不能构造反向范围；没有显式终点时交由 Playback CompileContext 重新解析 End Marker 或自然终点。重复设置完全相同的 Loop 值是运行时 no-op，不做无意义清理或重启。

改变为另一合法 Loop 范围仍从当前 tick 重建至新 loopEnd；当前 tick 已越过新 loopEnd 时从新 loopStart 冷启动。首次播放开始 tick 不因这些冷重启改变，因此后续 Stop Cursor Behavior 的 ReturnToPlaybackStart 仍回到原任务起点。Loop 设置只影响主播放运行时范围，不修改 Project、canonical cache、Mute/Solo、Preview、MIDI 导出或音频渲染。

Requirement trace：输入为活动计划、当前 tick、旧/新 Loop 和原请求 endTick；正式输出为匹配新 Loop 状态的唯一活动冷启动计划，或已越过原终点时的 Stopped。边界是关闭 Loop 必须丢弃旧裁剪计划、同值 no-op、反向重启禁止、回绕仍执行完整硬边界清理。Loop、当前 tick 和请求终点只属于播放会话，不持久化、不进入 Undo/Redo；明确非目标是 Pause、无清理热改计划、补发范围前 NoteOn 或改变 Project Default Range。

## 35. ADR-CORE-033（已接受，Q-NUI-017 待确认）：Project Switch 的关闭计时边界

决定：New、Open、Close 和 Exit 共用的 Project Switch Guard 在 Stop/cleanup、Function Draft 与未保存 Project 处理期间仍保持当前 Project 为打开状态并继续累计工程总耗时。只有所有 Guard 均通过、即将调用 `PerformProjectSwitchAsync` 实际替换、关闭或退出当前 Project 时，才调用 `BeginProjectClosing` 冻结累计值。实际切换成功后保持 Closing pause，交由旧会话释放；实际切换抛错或被取消时调用 `CancelProjectClosing`，从失败/取消完成后的单调时钟位置恢复累计，不补计实际切换尝试期间的暂停时间。

该顺序保持保存事务取得的累计快照包含关闭确认前的完整打开会话时间，也避免 Open/New 的候选构建或 Guard 对话一出现就提前停止旧 Project 计时。Guard 在进入实际切换前取消、Save unavailable 或保存失败时从未进入 Closing pause，旧 Project 继续保持打开。`BeginProjectClosing` 位于已有 Project Edit Lock 内；实际切换动作仍由应用 composition 负责原子接管新候选或释放旧 Project 资源，非 UI 协调器不预先丢弃当前 Project。

Requirement trace：输入为当前打开 `ProjectCompilationSession`、四类 Project Switch 命令、Draft/未保存决定及实际切换结果；正式输出为与当前 Project 开闭状态一致的单调工程时长 pause/resume 状态和既有结构化 Guard 结果。边界是 Guard/保存仍计时、真正切换入口开始暂停、失败恢复不回填、成功保持暂停。该计时状态不进入 Undo/Redo、不单独标记 Modified、不改变 metadata 修改时间或 canonical；明确非目标是 WPF 对话框、空状态页面、候选资源的 UI ownership 和应用进程 shutdown API。

## 36. ADR-CORE-034（已接受，Q-NUI-018 待确认）：Recent Projects 本机 MRU 边界

决定：Recent Projects 使用与 `preferences-v1.json` 分离的当前 Windows 用户本机 `recent-projects-v1.json`，避免为一个 SRS 未定义的列表字段修改已发布的严格 Preferences v1 表示。列表固定最多 10 个完全限定路径，最新成功激活的持久化 Project 位于首项；使用 Windows `OrdinalIgnoreCase` 身份去重，重复激活首项为 no-op。只有 Project 已经成功提交为当前打开 Project 后，应用 composition 才调用 `RecordSuccessfulProjectActivation`；候选验证、取消、失败打开、Save Copy 和未提交的新建结果都不得记录。

本机文件使用 source-generated UTF-8 JSON v1、未知/重复字段拒绝、1 MiB 读取上限及同目录 flush 后原子 move/replace。读取损坏时返回空列表和非 Project notice；写入失败保持内存及磁盘旧列表。不存在的路径仍保留并投影当前可用状态，以支持临时断开的可移动磁盘或网络位置；只在用户明确移除或清空时删除。路径不做大小写修正、存在性过滤、符号链接解析或 Project 内容探测。

Requirement trace：输入为成功激活的持久化 Project 绝对路径、现有本机 MRU 和显式移除/清空请求；正式输出为确定顺序、有界、原子发布的本机列表及当前 `File.Exists` 可用投影。边界是成功激活后才记录、大小写路径同一、最多 10 项、离线路径不自动丢弃。MRU、可用投影、notice 和绝对路径不进入 `.midora`、Project、Modified、Undo/Redo 或 canonical；明确非目标是云同步、跨用户/跨设备列表、时间戳、固定/分组、扫描式内容验证及 WPF 菜单展示。

## 37. ADR-CORE-035（稳定 ID 部分仍有效；Mapping 部分已由 ADR-CORE-052 取代，Q-NUI-024/Q-NUI-025）：单 `long` 稳定 ID 与历史 Mapping ABI v2

决定：Project 内所有对象类型共享一个以单个 C# `long` 为核心的 `MidoraId` 值类型和持久化单调分配器。合法值为 `1..long.MaxValue`；default/零和负值非法。分配器不补缺、不复用，现存对象全局唯一且小于 `NextStableId`；到达 `long.MaxValue` 时在分配前结构化失败。ID 只表示身份，不以数值推断用户顺序、创建顺序或同 tick 事件顺序。

开发期 `.midora` v1 契约直接修订：JSON ID 与 `nextStableId` 使用原始 token 匹配 `[1-9][0-9]*` 的十进制 integer；对象文件名使用相同值的 invariant 十进制 ASCII，无符号、无前导零；protobuf 删除嵌套 high/low `StableId`，在每个既有外层 ID 字段号上直接使用标量 `int64`。读取器拒绝零、负值、非 canonical token/文件名和溢出。旧 128-bit v1 未冻结、未发布，不提供迁移、兼容读取或双写分支；schema、descriptor 和 golden bytes 作为同一开发期 v1 资产重建。

历史 Mapping 决定：当时内部 ABI 从 v1 升级为 v2，签名为 `double Transform(double value, in MappingContextV2 context)`，`MappingStableIdV2` 改为单个 `long Value`。该自由 C# 执行语义、v2 新建默认和 collectible ALC 已由 ADR-CORE-052 破坏性取代，不再构成当前要求；单 `long` Context 身份表示仍有效，但 ABI v3 表达式不能读取 Stable ID。

Requirement trace：输入为 Project 分配状态、对象身份/引用、Mapping Context、JSON/protobuf/路径及开发期 v1 schema 资产；正式输出为无 Guid/UInt128/high-low 转换的单 `long` Domain/Compiler/Mapping/持久化全链。边界为正值、全局唯一、小于 `NextStableId`、分配耗尽、严格 token/wire/path 一致性、ABI v2 快照和确定性重开。稳定 ID 与分配器属于 Project 源数据；compiled source trace、Mapping 程序集/缓存及解析路径属于派生或运行时状态。明确非目标是跨 Project 全局身份、随机/分布式生成、128-bit 迁移、JavaScript Number 精度兼容层、根据 ID 数值建立业务优先级或同时运行 Mapping ABI v1/v2。

## 38. ADR-CORE-036（已接受，Q-NUI-023）：Project TPQ 与 SMF division 共用 15-bit 正整数范围

决定：初版 Project TPQ 的唯一合法范围为 `1..32767`，默认仍为 192，且只在创建 Project 时确定。Domain 构造、New Project admission、Semantic Validation、开发期 `project-settings-v1.schema.json` 与严格 JSON codec 使用同一边界。因为 v1 尚未冻结或发布，原先接受到 `Int32.MaxValue` 的开发资产直接修订，不建立 v2、高 TPQ 兼容读取或迁移分支。

MIDI Export 继续把 Project TPQ 原值写入 SMF Type 1 division，不缩放 tick、不重采样、不允许导出时替换 TPQ。越界输入在 Project 创建或打开阶段失败，因此正式内存 Project 不会进入“可播放但永久不可导出”的高 TPQ 状态。

Requirement trace：输入为新建请求和 `settings/project-settings.json` 的 TPQ；正式输出为可被 Compiler、Playback、MIDI Export 与 Audio Render 共同消费的固定 Project 时间基准。边界是 1/32767 接受，0/32768/`Int32.MaxValue` 拒绝，默认 192；失败发生在写文件、SoundFont 验证或建立活动 Project 之前。TPQ 属于 Project 源数据并持久化；换算缓存属于运行时。明确非目标是创建后修改 TPQ、自动 tick 重映射、SMPTE division 或旧高 TPQ v1 迁移。

## 39. ADR-CORE-037（已被 ADR-CORE-048 取代，Q-NUI-003）：Project MIDI Export Settings 开发期 v1 完整快照

本节只保留开发历史，不再构成当前需求或兼容承诺。`ExportProjectSettings` 及其 package/schema/History 已由 ADR-CORE-048 删除；MIDI Export 参数只属于一次任务 Draft。

原决定：`ExportProjectSettings` 保存 Mode、Range、Track Selection 策略、Routing、Include Readme 与 Treat Warnings As Errors。默认固定为 Whole Project、Project Default Range、All Valid Logical and Pure MIDI Tracks、Compact、`includeReadme=true`、`treatWarningsAsErrors=false`。Manual Range 才保存成对且满足 `0 <= startTick < endTick` 的边界。Explicit 策略只表示任务开始时要求显式选择，不持久化具体 Track ID；该集合与输出/覆盖路径继续只属于一次性冻结任务。

开发期 `export-settings.json` v1 直接增加必填字段，不创建 v2 或 v1→v2 迁移器。缺失、hash 不符或字段损坏仍服从 ordinary settings 恢复规则：用上述当前默认值恢复，产生 Error 并标记 Modified。设置变更由单个 History 命令原子执行和撤销，因其只影响未来 MIDI Export 请求而使用空 compilation change set，不改变当前 canonical fingerprint。

Requirement trace：输入为 Project 默认设置编辑及 v1 settings JSON；正式输出为可确定初始化一次性 MIDI Export 参数的 Project 源快照。边界是枚举严格、Manual 字段成对、默认值固定、具体 Track ID 与绝对路径禁止持久化。该对象属于 Project/Modified/Undo/Redo；冻结选择、输出路径、覆盖授权和运行进度属于任务运行时。明确非目标是把最近导出参数自动写回 Project、保存本机路径、持久化显式 Track 集合或从设置层重解释 canonical 内容。

## 40. ADR-CORE-038（已接受，Q-NUI-005）：分配型 History 命令的会话高水位与稳定 Redo 身份

决定：所有会分配稳定 ID 的正式创建、复制和 Split 命令在首次 Apply 时构造完整对象图；Undo 只移除或恢复对象图，不回退 `nextStableId`；Redo 重新挂接同一对象实例与相同 ID，不重新分配。撤销后建立新分支会丢弃旧 Redo entry，但 allocator 继续从更高值分配。构造或编译失败期间已经取得的 ID 同样烧掉，避免当前会话内身份复用；失败不留下可见对象或 History entry。

History 的保存点身份只比较可撤销 Project 状态，不把 allocator 的瞬态空洞单独视为 Modified。因此创建后 Undo 回保存点可以恢复 clean；以后发生其他编辑并保存时，当前高水位随 `project.json.nextStableId` 一并持久化。关闭 otherwise-clean Project 时，未持久化、无存活对象且无存活 redo history 的身份随会话消失；不另建 Project 外 allocator 存储。

Requirement trace：输入为当前 Project、高水位、创建/复制/Split 参数和 History cursor；正式输出为唯一的新对象图及可稳定 Undo/Redo 的身份。边界是 first Apply 分配、Undo 高水位不退、Redo 不分配、新分支只升高、失败不留对象、保存写当前高水位。对象与 allocator 属于 Project 源数据；History 栈不持久化。明确非目标是回收 ID、跨 Project 全局 allocator、把瞬态空洞单独强制保存或让创建绕过统一 History/编译事务。

## 41. ADR-CORE-039（已接受，Q-NUI-009）：Logical Parameter Definition 与引用 Lane 原子迁移

决定：任何会改变 Logical Parameter 类型、Enum 数值身份/显式模式/顺序/成员或使既有 default、Enum item、Lane Point 失效的 legal range 编辑，都使用完整目标 Definition 计划，并与全 Project 所有引用同一 Parameter ID 的 Lane 形成单个 History entry。调用方必须显式选择 `Clamp` 或 `DiscardInvalidValues`；目标为 Enum 时还必须确认整数接近不表示语义等价。普通重命名、display range、合法 default 与不失效的范围编辑继续使用轻量命令。

转换统一复用 Q-NUI-007：Double→Integer/Enum 按 Away From Zero；Clamp 先限 legal range，Enum 再选最近已定义值且等距选较小值；Discard 删除转换后不合法点；目标 Enum 的保留点强制 Step。Curve Point 和保留 Enum item 的稳定 ID 不变；新增 Enum item 仅首次 Apply 分配，Undo 高水位不退，Redo 恢复同一 item。Definition、Enum items、所有 Lane 与编译 change set 一次提交/回滚，不允许暴露中间非法对象图。

Requirement trace：输入为当前 Definition、完整目标 Definition/有序 Enum 集合、显式迁移策略、Enum 确认和全 Project Lane；正式输出为稳定 ID 可追踪且 Full/Incremental 等价的新源图。边界是目标 Enum 非空、名称/值/default 合法、existing item ID 属于当前 Definition、失败前尽量零分配、编译失败完整回滚。迁移结果属于 Project 源数据；策略与确认只属于一次命令，不持久化。明确非目标是按名称重绑、静默 Clamp、生成新 Point ID、逐 Lane 留下半迁移状态或保证 Enum 音乐语义等价。

## 42. ADR-CORE-040（已接受）：可选 Mapping owner、状态持有 Envelope 与生命周期硬边界

决定：Note Number / Velocity 的 `SubVoiceEventMapping` 继续作为强制共享目标；非 Note `SubVoiceEventMapping` 与 `LogicalParameterMapping` 是可删除的可选源对象。删除整条 Chain 时移除其 owner，非空删除仍要求一次明确确认，Undo 恢复同一 owner、Chain、Step、引用和顺序。显式新建非 Note 事件时可同时创建其初始可选 Mapping owner；后续点编辑、集合重新插入、编译和打开修复不得把用户已删除的可选 owner 静默重建。owner 不存在或 Chain 为空/禁用时均直接使用原始值。

非 Note MIDI 目标是状态型数据。直接事件 Mapping Chain 含 Envelope Step 时，`originalParameterValue` 使用当前实例内最近一次原始事件值；首个原始事件之前使用合并后的 Project / Event Instrument / SubVoice Initial State，仍无覆盖时使用目标类型默认值。Envelope 在实例及 Release 的左闭右开整数 tick 范围逐 tick 求值，完整链最终整数值不变时抑制重复输出；非零 Release 的最后一个有效 tick 必须已经达到 End Value，随后才允许实际 NoteOff。该规则与 Logical Parameter Mapping 的 `c` 状态继承一致。

Loop Start 前开始、其模板 Note 生命周期完整跨过 Loop End 的 Note 只发送一次 NoteOn，并保持到 Gate/Release/策略结束；Loop 迭代不重触发该 Note。普通 Gate End、Release End、Tail End 只执行精确 NoteOff，不发送目标 Reset 或 CC120 All Sound Off；目标 Reset 移到 lane 启用/非重叠复用起点，CC120 与最终 Reset 只允许出现在 Segment End 等明确硬裁剪边界及一次性消费者范围硬结束清理。Track 绑定及 Event Instrument 源修订必须进入 Full/Incremental 共同的依赖 fingerprint，不能依赖保存/重开使运行时缓存偶然失效。

Requirement trace：输入为可选/强制 Mapping owner、原始状态事件、Initial State、Envelope、Loop、Gate、Segment 边界、Track 绑定和源修订；正式输出为确定的 canonical MIDI 状态变化、Note 生命周期、Reset/CC120 边界及可逆 Project 源图。失败条件包括活动 Mapping 引用断裂、非法目标/值、映射运行失败、非法 Loop/Envelope 和资源不足；失败不得发布部分 canonical。Mapping owner、Step、Envelope、Loop、Initial/Reset 和绑定属于 Project 源数据；选择、Scenario Preview、canonical、fingerprint 与缓存属于派生/运行时状态。明确非目标是把 Reset Defaults 当作 Initial State、在 Gate End 用 CC120 修复尾音、让所有 Loop Note 自动无限保持、或为开发期旧空链 sentinel 增加兼容分支。

## 43. ADR-CORE-041（已接受）：MIDI Conductor Track Name 冻结为 Project Name

决定：MIDI 导出任务在准备阶段冻结 Project Metadata 中的 Project Name，并将其作为每个产物 Track 0 的 Track Name Meta Event；空或纯空白的防御性输入回退为 `Conductor`。任务准备后即使活动 Project 名称改变，本任务也继续使用冻结值。`Conductor / Meta Track` 仍是结构角色名称，不再作为正常项目的固定用户可见曲名。

Requirement trace：输入为任务准备时 Project Name 和 canonical 导出请求；正式输出为 SMF Type 1 Track 0 的确定 UTF-8 Track Name。边界是所有 Whole/Per Track/Per Port 产物使用同一冻结名称，事件 Track 继续使用 `Port <P> / Channel <C>`，名称不参与文件名合法化、路由或可听语义。Project Name 属于源数据；冻结任务快照属于运行时，不持久化、不回写 Project。明确非目标是把输出文件 stem、Logical Track 名称或结构角色 `Conductor` 重新解释为项目名称。

## 44. ADR-CORE-042（已接受）：Segment-owned Unit lane 与 SoundFont release 连续性

决定：普通 instance lifecycle end 只产生精确 NoteOff，不产生通用目标 Reset，也不能同时结束该 Segment 的音频 Unit fragment。对同一 Track / Event Instrument / Segment，非隔离 instance 共用一个 Segment-owned lane；隔离 instance 按生命周期重叠峰值做确定性 lane coloring，生命周期不重叠的 instance 复用同一 lane。每条实际启用 lane 从首个 instance 起持续到 Segment End；同 Segment 非重叠复用在新 instance 起点重新建立目标 Reset Defaults/Initial State，Segment End 才执行 CC120 与最终 Reset并允许跨 Segment 复用。自然编译范围取实际生成 instance 所属 Segment 的最晚 Segment End。

该选择使 BASSMIDI 在普通 NoteOff 后继续解码自然 sample release，且 miss/captured Segment PCM、cache hit、实时播放、离线渲染和 MIDI canonical 共用同一硬边界。它会把资源峰值从“同一 tick 活动 instance 数”提高到“同时处于已启用且尚未到 Segment End 的 lane 数”；这是维持尾音的 Segment/Track 归属、Mute/Solo 可寻址性和 Segment End CC120 安全性的必要代价，超过 256 仍按正式资源不足失败，不能通过提前 SoundOff 降级。

Requirement trace：输入为 instance lifecycle、Segment ID/End、隔离模式、SubVoice、canonical NoteOff/Reset；正式输出为确定的 Segment-owned allocation、覆盖 Segment release horizon 的 Unit fragment、lane 激活状态、Segment End CC120 和相同的 MIDI route。边界是新一轮非重叠复用的起点 Reset 先于 Initial/NoteOn、仍重叠的共享实例不重复初始化、重叠隔离 instance 仍分 lane、完全未生成 instance 的 Segment 不延长自然范围。Project 源模型不新增字段；allocation、PCM fragment、缓存与 lane coloring 均为编译/运行时派生。明确非目标是猜测 SF2 release 时长、固定增加若干秒 tail、在 Gate End 发 Reset/CC120，或把原生 sample release 解释为新的 Event Instrument 生命周期事件。

## 45. ADR-CORE-043（已接受）：状态 Reset 前移到 lane 激活边界

决定：普通 Gate/Release/Tail 或 instance lifecycle end 只执行精确 NoteOff，保留该 lane 的最终 CC、Bank、Program、Pitch Bend、RPN/NRPN 与 Pitch Bend Range 状态，不追加通用目标 Reset。编译器在 Segment-owned lane 首次启用，或该 lane 没有仍重叠 instance 后被新 instance 非重叠复用时，对该 SubVoice 的实际使用目标闭包先应用 Project Reset Defaults，再应用合并 Initial State、用户 tick 0 状态事件、Mapping 输出与 NoteOn；同目标同 tick 允许折叠为最终有效值。共享 lane 内加入仍重叠 cluster 的后续 Gate 不重复 Reset/Initial State，需要独立 Channel-Wide 起点状态的重叠实例必须使用 Channel Isolation。

Segment End、Project End Marker、显式消费者范围结束及其他正式硬边界仍执行精确 NoteOff、CC120 与最终目标 Reset。CC64 Sustain、CC66 Sostenuto、CC69 Hold 2 等持有型控制器也遵循状态保持规则：若作者希望它们在普通 instance 结束前解除，必须在 Event Instrument 生命周期内显式安排对应事件；编译器不在普通结束处静默插入控制器 Off。该选择让 SoundFont 原生 release 保持 instance 最终状态，避免末尾 Reset 改变仍在衰减的 sample，同时仍由下一次 lane 激活和硬边界保证确定性与安全释放。

Requirement trace：输入为 Reset Defaults、合并 Initial State、SubVoice 实际目标闭包、instance 生命周期/重叠、lane coloring 与硬边界；正式输出为 lane 激活 Reset、Initial/用户状态、精确 NoteOff、Segment/范围最终清理及 Full/Incremental 完全等价 canonical。边界是同 tick 顺序 `NoteOff → lane Reset → Initial/User State → NoteOn`、重叠 shared cluster 不重复初始化、显式范围起点恢复仍取 canonical 持有状态、持有型控制器没有隐式 ordinary-end Off。Project 源模型和持久化格式不新增字段；这些事件、来源、allocation 和缓存 fingerprint 都是编译派生。明确非目标是把 Reset Defaults 改成 Mapping 的原始值来源、在普通结束发送 CC120/CC121/CC123，或用 consumer/UI 状态重新解释 canonical。

## 46. ADR-CORE-044（已接受）：Pure MIDI Root、双 canonical 投影与 SMF 导入/拓扑保留

决定：采用 `MIDI Channel Root → Pure MIDI Track → Midi Segment → Direct MIDI Event` 作为与 Logical/Event Instrument 并列的正式源路径。Root 是 Channel Unit/Channel-wide state/lifecycle/cache 身份，Track 是编辑与 SMF MTrk 身份。Fixed Roots、含参与 Segment 内容的 Auto Roots、Logical Usage groups 按固定阶段分配；Root 活动连通区间结束才执行 Root 级 CC120/Reset。Canonical 从同一事件集冻结 Execution Projection 与 SMF Track Projection；SMF Import/Export、Root 合成/缓存、破坏性开发格式和共享 UI adapter 的完整决定见 `misc/Midora-Pure-MIDI-Tracks-and-SMF-Import-Architecture-Decisions.md` 的 ADR-PMIDI-001～008，global order/非空 owner 修订见 ADR-CORE-046。

本 ADR 明确限缩 ADR-CORE-010：Logical/Event Instrument 继续一 Unit 一 MTrk 并使用统一 endTick；Pure MIDI 则一用户 Track 一 MTrk，同 Root 多 MTrk 可共享 Port.Channel 并保留名称、顺序和自身 EOT。合法 Pure MIDI CC91/CC93 进入 canonical 与 SMF，`BASS_MIDI_NOFX` 音频投影不解释其效果。导入读取 Running Status，导出继续显式 status。

Requirement trace：输入为 Root/Track/Segment/direct/opaque 源数据、Logical 源数据、CompileContext、SMF import bytes 和 export request；正式输出为确定 Unit allocation、Root lifecycle、双 canonical 投影、原子新 Project 或保持 Track 拓扑的 SMF Type 1。边界和失败条件以 SRS 第 23 章及 INV-050～INV-057 为准；源对象进入 `.midora`，allocation/projection/checkpoint/PCM/import candidate 均不持久化。明确非目标是 Import into Current Project、Format 2、SMPTE division、字节级 round-trip、per-Track synth 或把 Pure MIDI 转换为 Event Instrument。

## 47. ADR-CORE-045（已被 ADR-CORE-046 取代）：Arrangement mixed parent union 是唯一外层所有权

决定：Project 不再分别保存可见 Event Instrument Library order、全局 Logical Track order 与 Root order，而是保存一个有序 tagged union：`Event Instrument | MIDI Channel Root`。每个 Event Instrument 直接拥有有序 Logical Track children；每个 Root 直接拥有有序 Pure MIDI Track children。Conductor 是固定唯一第一对象，不进入 union。Logical Track 不允许 Unbound；跨 Event Instrument 移动就是保留 Track stable ID/content 的原子 rebind。

父节点普通 Copy/Paste/Duplicate 深拷贝完整 subtree 并重映射全部新稳定 ID；Root 副本强制 Auto。Event Instrument 另提供只复制定义、child list 为空的 `Duplicate Instrument Only`。删除 non-empty parent 必须确认并原子级联 children；不得留下孤儿或未指定 Track。Root 分配/SMF 顺序从 mixed union 过滤 Root 得到；Logical 展示/命名辅助顺序按 parent 后 child 展平，不成为资源抢占优先级。

持久化使用 `project.json` parent tagged union、parent ordered child references 与 child parent ID 三方严格一致的模型。旧 Event Instrument Folder、Library manual order、global child order、Unbound Track 和 Project Panel 字段从开发格式中删除。单 parent 文件损坏但关系索引可信时保留原位置 Damaged Parent Placeholder；关系不可信时打开失败。产品/SRS 仍为 v0.1，内部 schema/descriptor/file-format 基线破坏性替换，不提供旧开发布局迁移或双写。

Requirement trace：输入为 mixed parent/child Project graph、稳定 ID allocator、copy/delete/rebind request 与 persistence index；正式输出为确定顺序、唯一 parent ownership、原子 command result 和严格可重开包。失败条件包括无/多 parent、kind 不匹配、引用未重映射、Fixed Root copy 冲突、未确认级联删除和无法可信恢复关系；失败不发布部分 Project。顺序、ownership 与源对象进入 `.midora`；selection、expand、clipboard、Mute/Solo 与 UI cache 不持久化。明确非目标是可见 Library Workspace、Folder、Unbound Track、按名称修复、旧开发格式兼容或让 UI 层另建所有权。

本历史决定建立的可见 parent/child 树、parent order、parent subtree clipboard 与 Logical Track 必须直接属于 Definition 的规则，已由 ADR-CORE-046 全部取代；不得据此恢复旧开发格式或旧 UI。

## 48. ADR-CORE-046（已接受）：平铺 Arrangement、Event Instrument Usage 与非空内部 Root

决定：Project 的唯一可见编曲顺序改为 Conductor 后的 global mixed `Logical Track | Pure MIDI Track` tagged order。Event Instrument Definition 作为独立有序资产保存；无名称、具稳定 ID 的 Event Instrument Usage 引用一个 Definition，并可由多条 Logical Track 共同引用。MIDI Channel Root 与 Usage 都是不可见的共享执行、状态、生命周期与缓存身份；最后一个成员离开时必须在同一原子命令中删除，Definition 不随 Track 删除。Logical Track 可以作为无内容的未绑定 shell 存在，但在取得 Usage 前不得保存 Segment 或其他音乐内容。

Fixed Root 的 Port.Channel 只在 Root 上有一份权威值，但 UI 将其表现为 Track 路由属性；同 Fixed Root 成员可在 global order 中分散。多成员 Auto Root 和多成员 Usage 必须各自连续，并作为可整体拖放的 Shared block。SMF 导入按源 MTrk 顺序建立 global order；SMF 导出按该 global order 过滤 Pure MIDI Tracks。Clipboard Copy/Cut 后 Paste 都创建新稳定 ID；只有正式 drag/move 重排与换组保留被移动对象的 ID。完整决定、候选方案和验证门见 `misc/Midora-Flat-Arrangement-and-Shared-Usage-Architecture-Decisions.md`。

2026-08-21 修订：Logical Track 普通 `Duplicate` 深拷贝 Track subtree，但创建引用同一 Definition 的新独立 Usage；显式 `Duplicate and Share State` 才保留源 Usage。普通独立副本位于完整源 Usage block 之后，共享副本位于源 Track 之后且留在 block 内。Event Instrument Definition Browser 的普通 Duplicate 继续只复制 Definition，Track/Usage 上下文不再提供 `Duplicate Instrument Only`。上述 owner 创建、global order 插入、stable ID remap 与 Undo/Redo 必须原子。

Requirement trace：输入为 Definition index、Usage/Root membership、global Track order、Segment 内容、独立/共享 Duplicate、路由/共享编辑和 SMF Track order；正式输出为唯一可见顺序、确定的共享 Unit/lifecycle、原子成员变更、确定 SMF 顺序和严格可重开包。失败条件包括空 Usage/Root、断裂引用、非连续 Shared Usage/Auto Root、错误的 Duplicate membership/插入位置、重复 Fixed route、带内容的未绑定 Logical Track、kind 不匹配与部分提交；失败不得发布部分 Project。Definition、Usage、Root、Track、global order 和 membership 属于 Project 源数据；selection、brace hover、drag target、Mute/Solo 和 tile cache 属于 runtime/session。明确非目标是空 Fixed Root UI、可见 parent 行、第二次命名 Usage、Track 上下文 Definition-only Duplicate、旧 `.midora` 兼容或按名称修复关系。

## 49. ADR-CORE-047（已接受）：SoundFont 脱离 Project，改为程序级有序 SF2/SFZ 列表、目标映射与原路径加载

决定：删除 Project Domain、`.midora` manifest/settings/resource 和 Project 工作流中的全部 SoundFont 状态。Application Preferences 保存唯一的有序 `{absoluteLocalPath, enabled, optional target Bank MSB/LSB/Program}` SF2/SFZ 列表；顺序是 BASSMIDI Font 优先顺序。三项 target 只能整体出现且均为 `0..127`；SF2 target 可省略，SFZ target 必填。列表编辑是程序设置事务，只允许在播放停止且无前台任务时 Apply；它不进入 Project Modified/Undo/Redo，不随 Project/MIDI 文件切换。

设置 Draft 与设置文件持久化只验证本机完全限定 `.sf2`/`.sfz` 路径和 target 结构，不复制、不读取完整文件、不计算内容 SHA-256，也不解析、快照或监控 SFZ sample/include 依赖。若 SoundFont、target、实时音频或音频缓存配置变化，设置成功持久化后进入独立 `Saving Settings` 运行时阶段：销毁旧持久 Worker，直接对 Enabled 原路径执行 `BASS_MIDI_FontInit`（仅 SF2 加 `BASS_MIDI_FONT_MMAP`），完成 BASSMIDI 加载和设备探测并保留新 Worker。该阶段是正式运行时加载而不是设置内容预验证；失败保持已保存的新设置、报告明确错误并令 Worker 不可用，后续 Apply 或音频 Preparing 可以重试。无 Project 时预热的后端由后续 Project 会话接管；接管前主文件元数据变化时丢弃旧加载状态。新建、打开、命令行打开或从 MIDI 导入形成的新 Project 会话在原子切换后、对应前台任务结束前接管或预热 Worker；显式 Reset Playback Engine 清理后同样立即预热。Project 已成功切换后的预热失败不回滚 Project，而是形成独立音频运行时错误并允许后续 Play/Preview 重试。纯 UI/最近目录设置变化不得重建 Worker。离线 Worker 同样直接读取原路径，并为每个 Unit Stream 一次设置完整有序 Font handle 配置。持久 Worker 冻结列表与 target 直到设置变化，不做活动任务热替换。没有 Enabled 项时，播放、预览和音频渲染不可用，但 Project 保存、编译和 MIDI 导入/导出不受影响。

Stream 配置统一使用 `BASS_MIDI_FONTEX2`。未映射 SF2 保持全部原始 preset/bank；映射 SF2 使用与目标 Program/Bank MSB 相同的 source，若不存在则服从 BASSMIDI first-preset fallback，再映射到目标三元组。SFZ 使用 BASSMIDI 定义的名义 source `preset 0 / bank 0` 映射到必填目标三元组。目标冲突由有序列表中较早项优先确定。实时、Pitch/Held/普通 Preview 与离线渲染不得采用不同配置解释。

Reusable PCM 仍必须隔离不同声音资源，但不得因此重新完整读取 SoundFont。缓存指纹由 Enabled 项的有序规范绝对路径、target、任务开始时主文件长度和 UTC 修改时间组成的小型描述符确定；可使用 SHA-256 编码该描述符，但它不是内容 hash 或验证。SFZ 依赖不进入身份；替换 sample/include 后产生的加载错误或陈旧缓存风险由用户承担，Midora 不探测或自动修复。任一主路径缺失/不可读时不建立可复用命中，并由实际 BASS Preparing 失败报告。音频文件渲染不再复制“冻结 SoundFont”，只冻结配置列表和主文件元数据指纹；外部进程在任务期间修改文件属于显式外部资源竞争。

本决定破坏性替换原 Project 单 SF2、Embedded/External、完整 SHA-256、打开验证、提取和 New Project SoundFont 工作流；开发期不提供旧 `.midora` SoundFont 兼容读取、迁移或双写。MIDI Export Readme 不再暴露本机 SoundFont 列表。SoundFont 授权责任不因不嵌入 Project 而改变。

Requirement trace：输入为 Application Preferences 的有序 Enabled SF2/SFZ 路径与 target、Project 会话激活/Reset 触发、实时音频与音频缓存设置、主文件元数据、canonical audio plan 与音频设备/渲染设置；正式输出为持久化设置、预热并保留的冻结 Font handle 配置、同配置实时/预览/离线声音和隔离的 sample-domain cache generation。边界是零 Enabled 禁止发声、主路径/BASS/SFZ 依赖失败保持已持久化设置或已提交 Project 但不假报 Loaded、音频配置变化只在 Stopped/Idle、Project 激活后立即预热、Worker 直接读源路径、Project/package/canonical 完全无 SoundFont。设置列表属于程序级持久化；Worker/Font handle、主文件元数据指纹和缓存属于本机运行时；明确非目标是内容完整性验证、SFZ 依赖解析/快照/监控、Project 可移植 SoundFont、每 Project/Port/Track 独立列表、运行中热切换或 DLS。

## 50. ADR-CORE-048（已接受）：Playback 归属 Application Preferences，Export/Render 参数仅为任务 Draft

决定：删除 Project Domain、Project History、`.midora` package/schema 与 Project Settings UI 中的 `PlaybackProjectSettings`、`ExportProjectSettings` 和 `AudioRenderProjectSettings`。Playback Master Volume、Limiter 与 Stop Cursor Behavior 迁移到 Application Preferences；它们和 Realtime Audio、Audio Cache 同属 `Audio` 页。Application Preferences 另设 `SoundFonts` 页和 `Appearance` 页；初版 Appearance 只持久化固定可选项 `Language = English`，不据此宣称已实现本地化。

MIDI Export 与 Audio Render Dialog 每次打开都从规格固定值创建独立 Draft。MIDI Export 初值为 Whole Project、Project Default Range、全部有效 Track、Compact、包含 Readme、Warning 不视为 Error；Audio Render 初值为 Whole Mix、48,000 Hz、每 Unit Stream 500 voices、Project Default Range、全部 Track。Dialog 的 Cancel 丢弃 Draft，Start 只冻结本次任务；不提供 `Save as Project Defaults`，最近目录仍可作为纯本机 File Picker 偏好保存。开发期旧 `.midora` 中三个 settings 文件不迁移、不兼容读取、不双写。

Playback Preferences 更新只允许在 Playback Stopped 时提交；Master/Limiter 立即应用到当前 Playback Controller，Stop Cursor Behavior 决定后续 Stop。只有 Realtime Audio、Audio Cache 或 SoundFont 列表变化才要求重建持久音频 Worker；Appearance、Playback 数值及纯 UI 偏好变化不得触发 Worker 重建。Audio Render 继续使用程序级 Playback Master Volume；Limiter 链仍服从第 15 章固定正式渲染语义，不由 Project 默认覆盖。

Requirement trace：输入为 Application Preferences Draft、MIDI Export/Audio Render Dialog Draft 和当前 Project/canonical；正式输出为程序级 Playback/Appearance 偏好及一次性冻结的输出任务参数。边界包括 Playback 活动时拒绝设置提交、非法 dB/枚举/Language 拒绝、Dialog Cancel 零持久变更、旧开发格式明确拒绝。Playback/Appearance 属于用户本机设置；Export/Render 参数、路径、覆盖授权和进度属于任务运行时；三类数据均不属于 Project、Undo/Redo、Modified 或 canonical。明确非目标是 Project 级输出模板、最近参数自动记忆、多语言资源切换、旧 settings 迁移或隐藏兼容字段。

## 51. ADR-CORE-049（已接受）：MIDI 导出事件 Track 固定关闭 Reverb/Chorus Send

决定：MIDI 导出器在每个实际输出的单 Channel 事件 MTrk 的相对 tick 0 固定追加 `CC91 = 0` 与 `CC93 = 0`，顺序为 CC91 后 CC93。该系统初始化只存在于 SMF 编码结果，不进入 Project、编译器或 Canonical Compiled Result。Conductor MTrk 没有 Channel，禁止写入这两条 Channel Event。

同 tick 顺序固定为 `Track Name / MIDI Port / Midora Pure MIDI 结构 Meta → Channel 10 melodic GS/XG（如适用）→ CC91=0 → CC93=0 → canonical/opaque 事件`。因此 Pure MIDI Track 在 canonical 中显式存在的 CC91/CC93 仍原样保留，并在系统零值初始化之后按冻结 tick/order 生效；导出器不得去重、替换或删除用户事件。Logical/Event Instrument canonical 继续禁止 CC91/CC93，导出器生成的两条兼容初始化不改变该领域约束。

Requirement trace：输入为冻结 MIDI Export canonical、SMF Track Projection、输出 Port 映射和每条事件 Track 的 Channel；正式输出为每条 Logical Unit/Pure MIDI MTrk 各一次确定的 tick 0 CC91/CC93 零值初始化及其后完整 canonical/opaque 数据。边界是 Conductor 不写、每个实际 MTrk 独立写、Per Track/Per Port/Whole Project 与分页/非分页路径完全一致、用户同 tick 事件后写并优先。编码失败仍原子失败且不发布部分文件。该初始化只属于导出产物，不持久化、不进入 Undo/Redo、编译统计、canonical fingerprint、播放或音频渲染；明确非目标是修改 Project 默认事件、改变 BASSMIDI `NOFX` 策略、清除其他 CC，或让 MIDI 导出器重新解释用户效果事件。

## 52. ADR-CORE-050（已接受）：会话临时存储以 manifest 与活动锁自动回收

决定：Audio Cache 与 Pure MIDI session backing content 使用两套独立的 session 所有权，不因路径接近而混为同一种缓存。Audio Cache 继续位于程序设置的 cache root 下，以 `session-<guid>`、`MIDORA_AUDIO_CACHE_SESSION_V1` manifest 和独占活动锁标识；Pure MIDI backing 固定在 `%LOCALAPPDATA%\Midora\SessionContent`，改用 `session-<guid>`、`MIDORA_SESSION_CONTENT_V1` manifest 和独占活动锁。主应用取得单实例所有权后同步执行一次 best-effort 清理；每次建立相应 session 前再次清理，以覆盖设置切换或先前删除失败。

删除权限严格限制为父 root 的直接子项。当前格式目录必须 manifest 内容与目录名精确匹配且活动锁可独占取得；reparse point、未知目录、未知文件、错误清单和仍活动目录全部保留。为了回收当前已存在的开发期残留，Pure MIDI backing 额外识别裸 32 hex GUID 目录，但只有空目录或全部普通文件严格命名为 `mt_<positive id>.mpk` 时才删除；不把该兼容规则扩展到任意 GUID 目录内容。删除逐项捕获 I/O/权限/竞争失败，不阻止启动、Project 原子切换或音频 session 建立；正常 Project/session Dispose 仍先释放 pack/cache/lock 再删除自己的目录。

Requirement trace：输入为程序级 Audio Cache Root、固定 SessionContent root、session manifest/name、独占 lock 和旧 `.mpk` 文件名结构；正式输出不是音乐数据，而是保留全部活动/未知内容并删除已确认 orphan 的本机运行时存储。边界包括异常退出、蓝屏、正常 Dispose、删除竞争、只读目录、未知子项、reparse point、旧裸 GUID 目录和同时活动 session。Project、canonical、`.midora`、Undo/Redo 与诊断均不变；目录/lock/manifest 只属于本机运行时。明确非目标是跨 session 复用、递归清空 root、按年龄猜测垃圾、删除无法确认所有权的内容或把 SessionContent 纳入 audio reusable quota。

## 53. ADR-CORE-051（已接受）：Pure MIDI Channel Mode SysEx 使用有类型的 canonical 音频特权

决定：不把“Pure MIDI 允许任意 SysEx”直接等同为“任意 SysEx 可以进入 BASSMIDI”。唯一音频特权固定为可确定映射单个 Channel 的 Roland GS DT1 Part Mode 与 Yamaha XG Part Mode，且只接受规格固定长度、设备编号、mode 范围及 GS checksum 合法的消息。SMF 导入根据 payload target Channel 把它归属对应派生 Pure MIDI Track；原 opaque event 继续原样保存在 Project 和 SMF Projection 中。

Compiler 从该 opaque source 派生独立 `CanonicalMidiChannelModeSystemExclusiveEvent`，冻结 source Track/Segment/object、absolute tick、SMF Track/event order、分配后的 Port.Channel 和 Direct/RangeRestore role。范围从 Root 活动连通区间中途开始时，在起点补该区间内最近一条先前 mode；Root 空闲边界阻断继承。Playback adapter、滚动 event file、Worker IPC、fragment/segment/playback PCM key 都显式携带该有类型 payload，保持与普通 Channel Event 的 canonical 同 tick 顺序和 Track 级 Monitoring 来源。Worker 发送规范化 SysEx 后，在同一事件顺序点同步调用 BASSMIDI 的显式 Unit mode；该镜像只落实已由 canonical 识别的语义，不扩张可发送 SysEx 范围，并消除不同 SoundFont 对厂商 SysEx preset remap 的差异。

完整 Project canonical 派生默认 Playback View 时必须连同有类型 Channel Mode SysEx 原样复制。该 View 只是同一 canonical 的播放用途包装，不得只保留普通 MIDI event/page source 而遗漏旁路的有类型事件集合；否则从 tick 0 的桌面播放会与直接消费 Full Compile 的离线/测试路径产生可听分歧。

正式 1-channel synth 将 target 归一化为 channel 0，并在 BASSMIDI RAW batch 中编码完整 F0…F7 消息。Root descriptor 的 Melodic/Percussion 初始化仍发生在 Stream 创建/复用阶段，privileged event 只表示时间线中的后续状态改变。该事件首次进入可听投影、以及修复默认 Playback View 遗漏该事件时，都必须提升 canonical Unit PCM、sample-domain Unit PCM、实时 Segment PCM 与 playback-span renderer cache generation；opaque source fingerprint 不能用于自然淘汰这些旧版静音 PCM。任意其他 SysEx、Meta、F7 continuation、GM/GS/XG Reset、无效 checksum/长度/mode 均不派生音频事件、不报兼容诊断，仍仅保留和导出。Worker 不允许直接读取 Project opaque bytes 或自行识别厂商消息。

Requirement trace：输入为 Pure MIDI Segment opaque source、SMF source Track/Port/channel buckets、Root 活动连通区间、CompileContext 与 Unit allocation；正式输出为不变的 opaque SMF projection及额外的有类型 canonical audio event，经非分页/分页计划、IPC、缓存 identity 和 BASS RAW submission 到达 Unit stream。边界包括多 Channel MTrk 归属、GS part-address 特殊映射、合法 checksum、同 tick order、range-start restore、Mute/Solo future replacement、Root 空闲断点与 channel-0 normalization。Project/persistence 格式不新增字段；canonical/计划/IPC/cache generation 属于派生与运行时。明确非目标是任意 SysEx 音频回放、外部硬件兼容层、Reset 消息、F7 continuation 合并、识别未知厂商 payload 或把 SysEx 语义加入 Event Instrument。

## 54. ADR-CORE-052（已接受）：Mapping Function 使用受限表达式 ABI v3

决定：破坏性停止执行 Project 内的自由 C# Mapping Function ABI v1/v2。当前 Mapping Function 固定为 ABI v3 单行 `double` 表达式；只允许版本化白名单中的字面量、`value`、批准的 `MappingContextV2` 数值/枚举字段、算术/比较/布尔/条件运算、批准的 Contract 枚举成员和纯数值 `System.Math` 成员。禁止语句、循环、赋值、lambda、对象/数组创建、任意 API、字符串、名称、Stable ID、反射、I/O、进程、网络、时间、随机数、异常构造和其他副作用。

Roslyn 仅执行 Expression 语法解析；Midora 自有 binder 逐节点检查并建立 `System.Linq.Expressions`。不得 Emit 或加载 Project 源码程序集，不得提供 .NET reference pack、unsafe 或运行机器 TPA。固定限制为 8,192 Unicode scalars、512 syntax nodes、64 depth。批准的 Context/Math/枚举集合由 ABI Contract 集中定义，并由正式编译、Draft 验证和 UI 补全共用。

Context 依赖由正式分析器推导；UI 不再允许手工声明。Apply 原子写入表达式与推导集合；正式编译重新推导并要求与持久化集合精确相等，以拒绝陈旧或篡改的依赖元数据。缓存键继续使用 ABI/profile/精确源码 UTF-8 SHA-256，只保留当前 Project 仍存在的修订并在关闭时释放；不再存在 collectible ALC，因为没有用户程序集。

旧 ABI v1/v2 可以被格式层读取以形成明确诊断，但绝不执行、不自动迁移、不保留兼容执行器；用户重新编辑并 Apply 后形成 ABI v3。该安全替换不改变 Mapping Chain 的累计 `value`、取整/越界、Source Trace、Full/Incremental 等价或 canonical consumers。Batch Edit 的表达式不属于 Project Source Data，不受本决定改变。

Requirement trace：输入为 Project Mapping Function expression/ABI/dependency metadata、Mapping Chain 当前值和只读 Context；正式输出为有界确定性委托及既有 canonical Mapping 结果。失败边界包括未知/旧 ABI、非法节点/API/字段、依赖不一致、超长/超节点/超深、非有限结果和目标类型/范围失败。表达式与推导依赖属于 Project；委托/cache/diagnostic 属于当前编译会话；明确非目标是通用脚本、完整 C#、沙箱进程、旧源码自动迁移、Batch Edit 语言变更或扩大 Context 对象图。

## 55. ADR-CORE-053（已接受）：产品 SemVer 单一来源与 Project Format 1 兼容冻结

决定：Midora 产品版本从 `1.0.0-dev` 开始采用 Semantic Versioning，并由仓库 `eng/Version.props` 唯一产生。1.x `AssemblyVersion` 固定 `1.0.0.0`，`FileVersion` 跟随产品三段版本，Informational Version 可追加 Git commit metadata。Desktop Project package、MIDI Export Readme 和未来 About/发布界面必须读取构建生成的同一版本，不得维护业务源码常量。

产品版本与 Project Format、component schema、Mapping ABI、Application Preferences/preset schema、Worker IPC、缓存 generation 和用户自由文本 Project Version 分离。`createdWithSoftwareVersion` / `lastSavedWithSoftwareVersion` 只用于诊断和支持，不决定兼容。正式 release 使用不可移动 annotated `v<version>` tag、Changelog、兼容矩阵和发布清单。

同日冻结当前 `.midora` Format 1，作为完整作品验收和 1.0.0 的持久化基线。V1 JSON schema set hash、protobuf descriptor hash、代表性 wire golden、strict reader、deterministic package 与事务测试构成自动门；后续 1.x 新软件必须读取此前有效 Format 1。无法由 V1 表示的新数据必须建立 V2 codec/schema/content-pack contract 和 detached V1 migration，不得修改 V1 字段/field number/wire 语义或通过更新 golden 掩盖破坏。缓存只提升 generation 并淘汰，不进入格式迁移。

Requirement trace：输入为单一产品版本源、Git commit/tag、Format 1 schema/descriptor/golden assets、既有 Project package 与各独立 ABI/protocol version；正式输出为一致软件标识、严格版本预检、持续 V1 读取和可审计 release。失败边界包括版本源/EXE/manifest/Readme 不一致、dirty/mismatched tag、V1 hash/golden/旧文件重开失败、未知未来格式和部分迁移；失败不得发布或部分提交 Project。产品版本/tag/changelog 属于发布契约，Format/schema/Project source 属于持久化，ABI/IPC 属于对应执行边界，cache generation 属于运行时；明确非目标是一个全局万能版本号、旧 reader 前向猜读、保存回旧格式或在 V1 中隐藏新字段。

## 56. ADR-CORE-054（已接受）：大规模时间线的内存分页、批量历史与范围局部栅格缓存

决定：Pure MIDI 保持 Format 1 Content Pack 的不可变外存分页与增量覆盖层；Logical Segment Note 与 SubVoice Template Event 在运行时改用固定 4,096 项的内存页集合。后两者的页是实现细节，只按稳定 ID、确定集合顺序和正式字段建立不可变值快照；页面首次变更采用 copy-on-write，连续批量编辑只标记受影响页，直到消费者请求快照时才重建脏页。该实现不得改变 `.midora` Format 1 的 JSON/protobuf 字段、顺序语义、stable ID、编译输入或 Undo/Redo 可观察结果，也不得把运行时页边界持久化。

批量 Paste/Duplicate/编辑的 Apply、Undo、Redo 与同 Tick 精确碰撞处理不得逐项在线性 List/overlay 上查找并移动尾部。正式集合提供按 stable ID HashSet 的一次稳定压缩、一次 generation 提升和可恢复的原页/overlay位置记录；Undo 必须恢复相同对象引用、原有全局顺序、原页边界和选择语义。Direct MIDI Note 仍遵循“保留原占位者、删除后来编辑者”；Logical/SubVoice Note 的相同策略由同一 targeted collision transaction 执行。失败或冲突检测必须在发布 History entry 前完成，不能留下部分删除。

钢琴卷帘、Velocity、Event Point 与 Arrangement Segment Preview 的栅格 identity 使用查询范围内的内容及选择局部指纹，不再把整个 Segment generation 或全局 selection revision 注入每个 tile。不可变 Pure MIDI Content Pack 的范围指纹只读取 page directory 与既有 page SHA-256，不解码 page payload；编辑 overlay、Logical Note 与 SubVoice Template Event 使用固定大小 block summary 的交换律聚合，只扫描范围首尾 block。WPF `OnRender` 只组合这些有界元数据和少量 materialized item，真正的分页查询、候选遍历与位图生成只在后台 raster worker 发生。远处编辑不能仅因 overlay 索引位移使未相交 tile 失效；Undo 恢复原内容时必须恢复原局部 fingerprint，从而直接重用编辑前缓存。

Arrangement Segment Preview 固定为两层：第一层是每个 Segment 唯一、与当前 viewport zoom 无关、最多四个 tile 的 coarse fallback，Project/Workspace 建立后即后台预热，并在任何缩放下直接缩放显示；第二层才按半八度量化的显示 LOD 分 tile 生成。某 detail tile 完成后只在其覆盖区替换 coarse，未完成区继续显示 coarse，不得叠加两层造成重影，也不得因进入新缩放级别同步重算整条 Segment。钢琴卷帘、Velocity 和 Event Point 继续使用可见世界 tile；UI thread 建 key 时不得查询大型数据源，空白内容右侧不得创建无意义 piano tile。

栅格调度固定为可见、邻域预取、全局预热三级优先级。可见 tile worker 数按逻辑处理器数量取 `clamp(processorCount / 4, 2, 4)`，从而在多核机器缩短冷视区收敛；在至少 8 个逻辑处理器的机器上，至少保留四分之三逻辑处理器给 UI、编译和音频。无论可见 worker 数多少，预取/预热最多只占一个 worker。64 项总在途上限中，非可见任务最多占 32 项，不能以后台暖缓存填满队列。Snapshot/Workspace/Surface Mode/精确投影修订后取消旧计划；已进入队列但已无有效消费者的请求在取得 worker 后必须跳过 factory；执行中的旧任务可以完成，但结果按 generation 丢弃且不得写入当前缓存。完成缓存继续受 256 MiB LRU 上限约束。

Requirement trace：输入为 Project 时间线对象、stable ID、编辑范围、History command、Workspace selection、可见/预热 tile 范围及 Project revision；正式输出为与原数据模型完全相同的编辑结果、精确可逆 History、范围局部不可变 presentation snapshot 和确定栅格。边界包括跨页编辑、整页删除、碰撞产生的后来者删除、Redo、跨 Segment 粘贴、极端空白视区、缩放/平移、旧任务取消、缓存上限与 Project Close。Project 源对象和 Format 1 持久化不变；页、overlay 索引、快照、指纹、tile、取消令牌和 WPF bitmap 只属于进程内运行时。失败条件包括 stable ID 重复/缺失、恢复位置不一致、算术溢出和损坏分页源；失败不得发布部分 Project 或错误缓存。明确非目标是把 Logical/SubVoice 改成新的磁盘格式、让页边界影响编译顺序、无限保留 WPF 位图、保证执行中原生 WPF 栅格可抢占，或以缓存命中改变正式语义。

## 57. ADR-CORE-055（已接受）：极端时间线统一修订、非阻塞消费与端到端性能门

决定：Direct MIDI Note/Event、Logical Note/Parameter Point 与 SubVoice Template Note/Event 的运行时表示收敛为按正式空间键排序的不可变分页修订。页采用结构共享 copy-on-write，固定保存 ID 索引、区间 `maxEnd`、tick/lane/category 摘要、局部内容 hash 与缩小时使用的有界 occupancy 摘要；编辑事务只替换受影响页并生成精确 range change set。Undo/Redo 保存页根或紧凑页 delta，不保存逐对象查找/插入脚本、每碰撞键一个 HashSet 或整份 owner 深拷贝。Project 的稳定 ID、顺序、碰撞、边界、选择结果和 canonical 语义不因页布局改变。

UI、Compiler 与后台 raster 只消费同一不可变 Project revision；发布新编辑不得等待后台编译复制或持有的大型 Project gate。后台编译和渲染都采用 latest-wins：旧 revision 可以在安全取消点终止，不能发布到新 revision。Desktop 每个 Dispatcher frame 最多合并一次模型刷新；普通 Note/Event 局部编辑不得重建无关 Workspace、Project Tree、全量 Properties 或无关诊断视图。Selection 使用可共享的持久 ID/page-local bitmap 与按页摘要；历史恢复不得在 UI 线程重新解析全部 ID 或解码冷页。

后台编译的不可变修订捕获固定为两阶段。第一阶段只能在 Project source mutation gate 内冻结 revision/generation、Project 标量、顶层稳定 ID/顺序、受影响 owner 的结构标量，以及集合已经发布的不可变分页快照引用：Logical Segment 保存 `LogicalNoteQuerySnapshot` 和各 Logical Parameter Lane 的 `CurvePointQuerySnapshot`；Event Instrument/SubVoice 保存 `TemplateEventQuerySnapshot`、Value Curve 的 `CurvePointQuerySnapshot` 及不可变 Mapping/Initial State 标量；Pure MIDI 继续保存 Content Pack/overlay 的不可变查询修订。第一阶段只允许 `O(top-level owners + segments/lanes + page-directory metadata)`，不得遍历或实例化百万级 Note/Event/Point，也不得把 live mutable object 引用交给 gate 外消费者。第二阶段在 gate 外按取消令牌把捕获修订物化为 compiler-owned mirror；每页及最多每 256 个值检查取消。所有受影响 branch 必须先形成独立候选图，只有候选图完整且 revision/generation 仍有效时才以一个非取消的短结构提交替换当前 mirror；取消不得留下部分更新的 compiler mirror，旧 revision 只可被丢弃、不可发布。每个 Project compilation session 同时最多保留当前已发布 mirror、一个正在物化的 capture 和一个 latest-wins 待处理 change set；取消/Project Close 后必须释放 page snapshot 引用，不能形成后台任务或旧修订队列。

现有 `MidoraCompiler` 继续消费 compiler-owned `MidoraProject` shell，但 Pure MIDI collection 不再把 editable overlay 物化为等量 `DirectMidiNote` / Event 对象。mutation gate 内冻结 Content Pack 引用、removed/replacement persistent roots 与分块 formal-added root；gate 外为 mirror 建立只读 compilation query snapshot 和空间 overlay index。Validation、stable-ID 检查、canonical range query、endpoint query 与 fingerprint 直接消费 value struct / `ReadOnlyMemory<byte>`，只有遗留 `IList` 消费者显式枚举时才惰性产生领域对象。该 shell 不可编辑、不得返回 live mutable source object；取消只能丢弃候选 shell。Logical/SubVoice 继续复用其不可变内存页捕获。后续可以将 compiler 入口进一步收敛为统一 immutable compilation graph，但不再把“重建超大 Pure MIDI 对象图”作为允许的过渡路径。无论后续是否继续收敛，同一源 revision 的 Full/Incremental 输出、诊断来源、正式集合顺序、同 tick 顺序和 fingerprint 必须完全一致，缓存或取消时机不得改变语义。

Arrangement 继续严格保留唯一 zoom-independent coarse fallback 与分级 detail 两层；detail 只局部替代 coarse。Piano Note + Selection 作为一个视觉提交单元，Velocity 与 Event/Parameter Point 各作为一个视觉提交单元：同一 `ProjectionKey`、同一精确水平/垂直投影和同一 DPI 下，新内容/选择修订的全部可见 tile 完整前保留上一完整单元，完整后一次切换；不得把旧选择层与新内容层混合，也不得跨缩放、LOD 或 DPI 缩放复用位图。新投影只显示该投影已完成的 tile。基础内容、选择和手势预览仍是独立计算层，但提交必须遵守上述一致性。少量 Note 的 Move/Copy/Resize 直接构造当前 viewport 的有界即时矢量预览；超过固定阈值后才使用 latest-only 的像素瓦片预览。大选区 Resize 只显示当前 signature 已完成的局部 tile 与当前 anchor，不得复用上一 delta、selection、DPI 或内容修订的手势预览。所有 range fingerprint 由相交页摘要组合，`OnRender`、hover 与 hit test 不允许读取文件、Brotli 解压、SHA-256、全量枚举或大型排序；hit test 使用专用有界邻近索引，不复用渲染范围查询，并须事务式暂存结果，Pending/异常/重入不得部分修改调用者集合。

低缩放 occupancy 不允许用“节点时间包围盒 × lane 并集”近似多个 device column，因为该笛卡尔积会生成并不存在的灰色矩形。聚合节点只有在其全部时间范围精确落入同一 device column、且 category 完全匹配时才可直接合并；否则递归到子节点、空间 block 或原值。聚合与逐项 raster 必须共用同一个以邻近 tick 为原点、边界执行 half-up rounding 的 `TimelineRasterColumnProjection`，选择层也必须用同一列映射，避免低缩放边缘出现 1～2 device pixel 错位。

`TimelineOverviewSurface` 的内容摘要 extent 与导航 extent 分离：内容摘要只依赖正式 content extent、fingerprint 和 overview 宽度；viewport 进入 Segment 右侧空白只扩大导航投影和 thumb/cursor，不得同步重扫内容。摘要在后台 latest-wins 构建，UI 始终保留上一完整摘要，发布时才整体替换；无 Project 时不得残留显示旧摘要。

Raster scheduler 以 `(surface, layer, world tile, viewport generation)` 合并请求，工厂接受协作取消；新 viewport/revision 使尚未运行的旧请求失效，运行中的旧请求不得发布。完成通知按 Dispatcher frame 合并为一次 invalidate；在途容量同时受任务数和字节预算限制。极端缩小时由 occupancy 摘要以输出像素为界生成结果，渲染成本不能继续随映射到同一像素的原始对象数线性增长。

Pure MIDI 导入的 Content Pack 写入采用有界、确定的页编码流水线：source page ordinal 与提交顺序仍由导入线程冻结，endpoint 排序、确定性 little-endian 序列化、decoded SHA-256 和现有 Brotli Fastest 编码在后台并行，文件写入与目录项发布严格按冻结页顺序进行。默认 worker 数为 `min(8, max(1, logicalProcessorCount - 2))`，至少为 UI/音频保留两个逻辑处理器（不足三个处理器的机器只能退化为单 worker）；pending task 另受 `2 × worker` 上限约束。主要内存门不是 task 数，而是 64 MiB 的 decoded/source-record 加 codec 最大输出 reservation；endpoint 页同时计入 detached record buffer 与 serialized decoded buffer。Complete、取消、fault 与 Dispose 都必须停止调度、drain 并观察全部后台任务；未成功重开验证的 pack 不发布并删除，导入主异常与 cleanup 异常并存时以 `AggregateException` 保留原异常为首项，不得被 Dispose 掩盖。页仍按原顺序写入且 codec 未变，因此调度竞争不能改变 pack 字节。

2026-08-26 对 9KX2 真实 2,808,250,692 decoded page bytes 的同 payload 诊断表明：Brotli Fastest 为 12.981 s / 395,535,799 bytes，Deflate Fastest 为 10.242 s / 657,098,821 bytes，无压缩计数 sink 为 0.002 s / 2,808,250,692 bytes；当前并行 Brotli 的前台 queue wait 只有 0.82～0.87 s，而原主线程 endpoint 排序/序列化才是明确瓶颈。Deflate 的 21% codec wall-time 改善不能转化为相称的端到端收益且 payload 增加 66%，无压缩则增加约 6.1 倍。因此本决定不新增 codec 字段、不提升 Content Pack 格式，也不以未经真实端到端数据证明的格式破坏换取推测性能。保存 pristine Pure MIDI pack 时只把 staging copy 与 whole-pack SHA 合并为一个流式 pass；save-owned 自校验仍完整读取并校验 Zip entry SHA，但直接以锁定的同一 staging pack 做目录/语义重开，避免再写一份 session extraction。普通 `.midora` Open 仍执行 Zip→session backing 的单次流式 copy+SHA，保持会话所有权、损坏隔离与 Project Dispose 清理不变。

正式性能门使用 Release WPF 进程与真实异步完成时间，而不是只计一次 `OnRender` 返回。千万级 MIDI 的连续 pan/zoom、冷区域 hover、60,000 Note Move/Copy/Resize、首次 Undo/Redo、跨 Track/远位置编辑及三种 Piano/Velocity/Event Lane 都必须覆盖；同时运行朴素参考模型或 golden/property tests 验证最终对象、顺序、碰撞、选择和 Undo/Redo 完全相同。性能测试分别记录 Dispatcher stall、后台完成延迟、managed/LOH、decoded page cache、raster completed/in-flight 与 WPF native working set，禁止用第二次热缓存结果代替首次冷路径结果。

Requirement trace：输入为 Project revision、空间分页对象、编辑事务、selection、viewport/LOD、后台编译与 raster 请求；正式输出为语义不变的 Project/canonical、精确 History、局部 change set 和非阻塞 UI presentation。边界包括空白 Segment 外区域、冷页、极端 zoom、跨页/跨 Track 编辑、碰撞、旧任务、关闭 Workspace/Project、取消与内存预算。Project 源数据是否需要新持久化格式由独立格式决定；本 ADR 不以 Format 1 兼容为性能实现前提，也不要求无收益地改变格式。明确非目标是改变任何既有编辑行为、用近似数据提交正式编辑、让缓存命中影响结果、在 UI 线程等待后台消费者，或以“平均/热路径够快”替代冷路径性能门。

## 58. ADR-CORE-056（已接受）：Event Instrument Pre-Roll anchor 与 Project Format 2

决定：Event Instrument Definition 新增持久 `long PreRollTicks`，默认 0，合法范围 `0..TemplateLengthTicks`。它只作用于 Logical Segment 中由 Logical Note 生成的正式实例。设 Logical Note absolute anchor 为 `A`、有效 Logical Gate Length 为 `L`、Pre-Roll 为 `O`，Compiler 冻结 Instance/template origin `I=A-O`，template tick `t` 映射为 `I+t`，Logical Gate Start/End 仍为 `A`/`A+L` 并服从 Segment End 硬裁剪，instance-local Gate horizon 为 `O+L`。`MappingContext.gateLength` 保持 `L`，短音/长音继续比较 `L` 与 Template Length；Initial State、Reset/用户 template tick 0 状态、实际 tick Logical Parameter、Overlap/Cut Previous、Usage 活动连通区间和 Unit occupancy 从 `I` 起算。由 Template Length 决定的 one-shot 边界仍从 template origin 计量，不能再额外加一次 `O`。

Instance origin 必须位于所属 Segment 当前有效窗口内。`I < SegmentStart`、负 Project tick 或任意 checked Int64 换算溢出均产生可定位 Error；禁止把 `I` clamp 到 Segment Start、裁掉前缀后继续、自动扩展/移动 Segment、跨相邻 Segment 的硬 Reset 边界或修改 Logical Note 源数据。修改 Pre-Roll、Template Length、Logical Note、Segment window 或 Definition binding 时，dirty range 从旧/新值的最早可能 origin 回退，直到 Usage 共享状态、资源分配和 canonical 后缀收敛。Definition 的修改使全部引用 Usage 的 canonical/播放/导出/渲染缓存失效；消费者不得回读 Definition 二次偏移。

Event Instrument/SubVoice standalone Preview、Segment Editor Pitch Ruler audition 及其他不创建 Logical Segment Instance 的试听入口按 `O=0`，不产生负 preview 时间或人为等待。项目中途播放、跳转、循环和 Segment 局部播放维持既有冷启动限制：origin 早于范围起点的 NoteOn 不补发，不为恢复 sample 相位执行范围前音频预滚；必要非 Note 状态仍按 canonical range restore 恢复。这一限制是统一消费者行为，不允许播放与离线输出各自选择不同补偿。

Pre-Roll 无法由冻结 Project Format 1 表达，因此 writer 提升为 Format 2、`minimumReadableVersion=2`。Format 2 的 Event Instrument component 使用 Edition 2024 `EventInstrumentV2` wrapper：field 1 schema version、field 2 object type、field 3 冻结 `EventInstrumentV1 definition`、必填 field 4 `int64 pre_roll_ticks`，5..max 保留。V1 schema/descriptor/golden/reader 完全不变；打开 V1 后在 detached candidate 中为每个 Definition 显式设置 0，保持 stable ID、顺序、Usage/Track/Segment 与 Pure MIDI pages，完整验证后一次提交并标记迁移未保存。普通 Save/Save Copy 只写 V2，不覆盖来源 V1、不保存回 V1。

Requirement trace：输入为 Event Instrument Definition、Logical Note anchor/gate、Segment content window、Logical Parameter timeline、Usage membership、CompileContext range 及冻结 Format 1 Project；正式输出为带提前 template origin、原 Gate anchor、确定来源/顺序/occupancy 的 canonical 结果，以及严格 Format 2 Project。边界覆盖 0/Template Length 上界、Segment 左边界等于/差 1 tick、Segment End gate clamp、跨 Track shared Usage、Overlap/Cut Previous、范围起点落在 origin 与 anchor 之间、checked tick 上下溢、Full/Incremental 等价及 V1 migration。Pre-Roll 与 Definition 属于 Project source；canonical、dirty/checkpoint、allocation、PCM 与 Preview override 属于派生或运行时。失败不得发布部分 Project/canonical、修改 V1 源包或静默采用 0。明确非目标是音频 latency compensation、隐藏预滚、跨 Segment 执行、Pure MIDI 时间偏移、修改 Logical Note 可见位置、给 Usage/Track 单独覆盖 Pre-Roll 或让 standalone Preview 等待 Pre-Roll。
