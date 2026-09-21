# 第 22 章 主题索引与跨系统不变量

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章提供快速定位表与跨章节不变量，便于开发、讨论、测试和评审时稳定引用。

## 22.1 跨系统不变量
| 编号 | 不变量 |
|---|---|
| INV-001 | Project 是编译、MIDI 导出与所有音乐语义的完整上下文；播放、预览和音频渲染在 canonical 成功后额外冻结程序级音频设备与 SoundFont 列表设置，消费者不得据此改写 Project 语义。 |
| INV-002 | Conductor Track 固定存在且不参与 Channel Unit 分配。 |
| INV-003 | Event Instrument Definition、Event Instrument Usage、Logical Track、MIDI Channel Root、Pure MIDI Track 与 Segment 均以稳定 ID 构成身份和引用；名称、顺序、tick、Port.Channel 与源 MTrk index 均不得替代身份。 |
| INV-004 | Logical Track 通过可空 Usage ID 间接引用 Event Instrument Definition；有内容的 Logical Track 必须拥有 Usage。Pure MIDI Track 必须属于一个非空 MIDI Channel Root，并由该 Root 代表单一 Channel Unit。 |
| INV-005 | SubVoice 在单个 Event Instrument Instance 中原则上需要独立 Channel Unit。 |
| INV-006 | 初版最多 16 Ports × 16 Channels = 256 Channel Units。 |
| INV-007 | Logical/Event Instrument 获配的 Channel 10 始终为 melodic；Pure MIDI Root 的 Channel 10 由 Root 的 Melodic/Percussion 模式决定，SMF 导入的 Channel 10 Root 默认 Percussion。 |
| INV-008 | SoundFont 是程序级有序启停列表，不属于 Project；它不改变编译和 MIDI 导出语义，只影响本机实际发声。 |
| INV-009 | Canonical Compiled Result 是所有正式输出消费者的唯一音乐语义来源。 |
| INV-010 | 增量编译结果必须等价于同一上下文的确定性全量编译。 |
| INV-011 | Mute/Solo 是运行期监听状态，不属于 Project，也不影响成品输出。 |
| INV-012 | `.midora` 保存 Project 源数据，不保存编译结果、播放缓存、输出产物或 Undo/Redo 历史。 |
| INV-013 | 保存从当前内存 Project 重建完整 package，不保留未知或孤立文件。 |
| INV-014 | UI 只呈现和操作系统语义，不得重新解释编译、生命周期、资源或输出规则。 |
| INV-015 | 同一输入、上下文与有效资源状态必须产生确定一致的正式结果。 |
| INV-016 | 正式 BASSMIDI Stream 启用 `BASS_MIDI_NOFX`，Midora 不承诺 Reverb/Chorus 音频效果；Event Instrument/SubVoice 不得创建或映射 CC91/CC93，Pure MIDI Track 则必须允许、编译并在 SMF 中原样导出，音频投影确定性忽略其效果且不报错。 |
| INV-017 | 实时播放按所选输出设备的实际采样率生成音频；文件渲染按本次选择的 8,000–192,000 Hz 整数采样率生成音频。 |
| INV-018 | 音频活动线程在 Playing、Buffering、Preview Playing 和文件 Rendering 阶段不得产生托管堆分配；Preparing / Finalizing 不受此限制。 |
| INV-019 | 初版只允许一个用户可启动并打开 Project 的应用实例；正式音频后端固定为由该实例管理的无 UI、不能独立打开或解释 Project 的 Native AOT 内部音频子进程。 |
| INV-020 | 满足正确性、确定性和资源上限的候选实现中，时间性能优先于最小空间占用。 |
| INV-021 | 实时播放、预览和音频渲染的 tick→sample 映射使用完整 Tempo Map 的 decimal 区间积分，乘采样率后只执行一次 `AwayFromZero`，不得逐 Tempo 段取整。 |
| INV-022 | 初版 Limiter 版本 2 固定为 stereo-linked、5 ms look-ahead、4× 16-tap inter-sample peak detector、线性 ceiling `0.8912509`（-1 dBFS）、10 ms hold、100 ms 单极指数 release、无 makeup gain；状态跨 block 连续。实时首帧前预取，离线补偿前瞻并保持精确 frame 数；实时与离线使用同一算法，UI 只显示 `Limiter`。 |
| INV-023 | 初版正式 WASAPI 输出固定为 Shared Mode、event-driven、stereo interleaved float32；采样率、实际 buffer 与 callback period 由端点初始化结果决定，不得静默回退到其他模式或格式。 |
| INV-024 | 初版正式实时音频工作 block 固定为最多 256 frames；子进程内 Render-Ahead PCM ring 容量按实际采样率和用户毫秒设置向上取整为 frame，不固定 ring block 数；实时 PCM 不跨进程，运行时控制 IPC 使用固定版本二进制共享内存且热路径零分配。 |
| INV-025 | 正式 BASSMIDI Stream 启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠实例按 FIFO 与逐个 NoteOff 配对，硬边界必须按活动实例数完整释放。 |
| INV-026 | 正式 BASSMIDI Stream 固定 8-point sinc 和 CPU 属性 0；音频在 canonical 成功后按抽象 Unit 使用 1-channel Stream 语义和有界复用 pool。实时/离线分别配置 Maximum Sample Voices per Unit Stream，默认均为 500，同一任务所有 Unit 使用同一冻结值。完美音频一致性测试以未触顶为前提。 |
| INV-027 | Midora 初版只发布 `win-x64`；主应用、Native AOT 音频子进程及 BASS/BASSMIDI/BASSWASAPI 必须同为 x64，不发布 x86、Arm64 或 AnyCPU 正式产物。 |
| INV-028 | 初版正式 BASS 原生基线固定为 BASS 2.4.18.3、BASSMIDI 2.4.16.0、BASSWASAPI 2.4.4.1 及第 13.30 节列出的 win-x64 DLL SHA-256；正式构建和运行时必须分别校验文件 hash 与完整版本码，不得自动跟随 vendor current/latest。 |
| INV-029 | Mapping Function 固定使用受限表达式 ABI v3：单行 `double` 表达式、8,192 scalar / 512 syntax node / 64 depth 上限、精确语法/Context/枚举/Math 白名单、自动推导并复核 Context 依赖。正式路径只绑定为 `System.Linq.Expressions`，不得 Emit/加载 Project 源码程序集；自由 C# ABI v1/v2 必须明确拒绝且绝不执行。缓存只保留当前源码修订，编译产物不得持久化。UI 只允许在 Event Instrument 所属模态对话框内创建/编辑，Validate 不改 Project，OK 原子提交，Cancel 丢弃；不得恢复独立 Function Workspace 或跨窗口 Draft。 |
| INV-030 | 初版持久化兼容基线固定为 JSON Schema Draft 2020-12 与 protobuf Edition 2024；结构性 JSON/protobuf 严格拒绝未知字段，JSON 还拒绝重复属性。已发布 `.proto` 字段号、descriptor、golden bytes 与固定 runtime/codegen profile 属于兼容承诺。 |
| INV-031 | `.midora`、Project Domain 与 canonical 不得保存或引用 SoundFont。Application Preferences 只保存有序 `{absolute local .sf2/.sfz path, enabled, optional target Bank MSB/LSB/Program}` 列表；target 三项整体出现且均为 0～127，SFZ target 必填。Draft/持久化阶段不复制、不完整读取、不计算内容 hash，也不解析/快照/监控 SFZ 依赖。音频相关设置持久化后必须在 `Saving Settings` 运行时阶段销毁旧 Worker、直接由 BASSMIDI 加载原路径并保留新 Worker；失败保持已保存设置并明确报告。 |
| INV-032 | 工程总耗时按 Project 成功打开后的完整会话单调累计，包含空闲、最小化、失焦、Buffering、MIDI 导出与音频渲染；系统睡眠 / 休眠和关闭流程暂停。自动累计不单独标记 Modified，也不影响 canonical 语义。 |
| INV-033 | 初版 SMF Type 1 导出兼容档固定：Tempo 用 `60,000,000 / BPM` 后一次 `AwayFromZero`；拍号 `cc=24`、`bb=8`；Bank 顺序为 CC0→CC32→Program；文本 Meta 为严格 UTF-8；每个 Channel Event 显式 status；导出器不在 canonical 之外追加 Channel 清理。Conductor/Logical Unit MTrk 的 EOT 使用统一 endTick；Pure MIDI MTrk 使用冻结的自身 EOT。 |
| INV-034 | MIDI 导出中，Logical Channel 10 Unit MTrk 与 Melodic Channel 10 Pure MIDI MTrk 在相对 tick 0、Track Name/Port/结构 Meta 后、canonical Channel Event 前固定写一次 GS Normal Part 与一次 XG Normal Part SysEx，顺序 GS→XG；Percussion Root、不相关 MTrk 与 Conductor 不写；不得发送 GS/XG/GM Reset 或改变 canonical Bank/Program。 |
| INV-035 | MIDI 导出与音频文件渲染必须共用同一确定性 Windows 安全文件名合法化和冲突检测服务：NFC、固定不安全字符集合、设备保留名前缀 `_`、文件名部分最多 255 UTF-16 code unit、text-element 安全截断、NFC + OrdinalIgnoreCase 冲突键和稳定 ` (n)` 后缀。合法化后的完整最终路径必须在任务开始前预览并冻结；不修改 Project 源名称，已有目标不参与后缀分配且仍需明确覆盖授权。 |
| INV-036 | 初版输出模板固定：整曲 MIDI / 音频为 `<ProjectStem>.mid/.wav`，来源依次为 Project 名称、当前 `.midora` stem、模式 fallback；分 Logical Track 为 `<NN> - <LogicalTrackDisplayName>.mid/.wav`，逐 Port MIDI 为 `Port <PP>.mid`，Readme 为 `README.md`。MIDI Conductor Track Name 使用冻结 Project Name；Logical Unit MTrk 为 `Port <P> / Channel <C>`，Pure MIDI MTrk 保持冻结用户 Track Name。多文件模式选择完整输出目录，不自动增加嵌套目录。 |
| INV-037 | Midora 初版定位为免费、开源、非商业软件，但 BASS/BASSMIDI/BASSWASAPI 不属于 Midora 的开源许可范围。正式分发第三方二进制前必须按实际发布主体、收入方式、平台、分发方式和发布时有效条款完成许可核验并提供 notices；条件不明或商业化时不得沿用免费非商业结论。 |
| INV-038 | Midora 自有源代码固定使用根目录 `LICENSE` 中未经自定义修改的标准 MIT License，版权署名为 `Copyright (c) 2026 Midora contributors`；项目自身的非商业发布定位不得转化为限制下游商业使用的附加许可条件。 |
| INV-039 | Event Instrument / SubVoice 虚拟键盘、Segment Editor Pitch Ruler 和单个 Logical/Direct MIDI Note 放置预览必须复用正式预览管线；Event Instrument held Preview 使用因果 Gate：Gate End 前 `MappingContext.gateLength = Int64.MaxValue`，Gate End 从 producer 尚未渲染的第一个 frame 起生效，不回写已消费或已缓冲 PCM。 |
| INV-040 | Project 内全部稳定 ID 共享一个持久化单调正 `long` 分配器，合法范围为 `1..long.MaxValue`，不补缺、不复用且不具业务排序语义；JSON 使用 canonical 十进制 integer，对象文件名使用无符号无前导零十进制 ASCII，protobuf 在既有外层字段号上使用标量 `int64`。 |
| INV-041 | 开发期 v1 中每个 Time Signature 必须满足 `4 × TPQ % denominator == 0`。变化 tick 立即开启新 Bar；若截断旧小节则产生 Warning。Domain、编译、持久化、`Bar:Beat:Tick` 与自然拍网格必须共用该整数、可逆语义。 |
| INV-042 | 音频缓存分为 canonical range、Logical Segment/Unit fragment、Pure MidiSegment normalized fragment、Root merged checkpoint、Unit/Root raw PCM、playback span 与短 Render-Ahead ring；exact replay 的完整命中不得重复语义编译或 BASSMIDI 合成。Pure MIDI 可听内容 identity 必须覆盖实际 Direct Note/Channel Event、分页源 fingerprint 与 COW delta，不得以集合 Generation、编辑次数或仅 stable ID 代替。 |
| INV-043 | underrun 在失败位置锁存，完整准备“当前自然小节剩余 + 下一完整小节”（若位于小节起点则当前完整小节），并以播放终点与 16 个四分音符裁剪后才恢复；不得短块断续推进。 |
| INV-044 | session 音频缓存不进入 `.midora`，root 固定为 `<ProgramRoot>\.tmp\AudioCache`、reusable quota 16 GiB 且允许 0；root 不再是可编辑 Preference，也不得 fallback 到用户目录或系统 temp。quota 满只停止新 reusable retention，既有命中继续可读且 miss 必须现场合成，不得静音或阻止播放；初始/运行期 Monitoring bypass 产生的不完整 entry 不得发布或禁用 retention，既有 failure state 不得被后续队列伪装成 quota-full；transient recovery spool 独立，无法取得 spool/RAM 时受控 Stop。 |
| INV-045 | MIDI 导出中，Logical/Event Instrument 的每个实际有事件 Unit 在同一文件内严格对应一个单 Channel MTrk；Pure MIDI 的每个被选择 Track 对应一个独立单 Channel MTrk，同一 Root 的多个 MTrk 可以共享 Port.Channel，名称、global Arrangement Track 顺序和自身 EOT 必须保留。 |
| INV-046 | 状态型非 Note Event Mapping 的原始值按最近原始事件或有效 Initial State/default 持有；Envelope/连续源在实例与 Release 的整数 tick 上从该值求值，非零 Release 的最后有效 tick 达到 End Value。普通 Gate/Release/Tail 结束不发送 CC120；CC120 只用于 Segment/消费者范围硬边界。 |
| INV-047 | Note Number/Velocity Mapping 是强制共享目标；非 Note Event Mapping 与 Logical Parameter Mapping 是可删除 owner。缺少可选 Mapping 表示原始值直通，普通事件编辑和打开修复不得静默重建已删除 owner。 |
| INV-048 | 发声 Segment 的 Channel Unit lane/audio fragment 从首次使用持续到 Segment End；同 Segment 的非重叠 instance 可复用 lane，但跨 Segment 不得提前复用。普通 instance NoteOff 后的 SoundFont 原生 release 必须进入实时、离线和缓存 PCM，只有 Segment/消费者范围硬边界可以硬裁剪。 |
| INV-049 | 普通 Gate/Release/Tail 结束只执行精确 NoteOff，不执行通用目标 Reset。lane 首次启用或无重叠 instance 后被非重叠复用时，按实际目标闭包执行 Reset Defaults → Initial State/用户状态 → NoteOn；共享 lane 内仍重叠的后续 Gate 不重复初始化。Segment/消费者范围硬边界仍执行 CC120 与最终目标 Reset。 |
| INV-050 | 一个 MIDI Channel Root 是一个持久 Channel Unit 身份；同 Root Pure MIDI Track 的正式同 tick 合并语义固定为 absolute tick → global Arrangement Track order → event explicit order。重复状态事件、重叠 Note 和跨 Track 冲突不得折叠或拒绝。 |
| INV-051 | Pure MIDI Root 的活动连通区间是真正 Channel 生命周期：子 Segment End 只关闭该 Segment 拥有的 Note，不重置共享状态或 sibling Note；Root 连通区间/Project/消费者硬边界才执行 Root 级精确 NoteOff、CC120、最终 Reset 与释放。 |
| INV-052 | Unit 分配顺序固定为：验证本次上下文全部非空 Fixed Roots → 按最早成员 global Track order 低号分配含参与 Segment 内容的 Auto Roots → 在剩余 Units 分配 Logical Usage groups。任何 Root/Usage 均不得为空；Root 不与 Logical instance 做时间复用，combined peak 不得超过 256。 |
| INV-053 | Canonical Compiled Result 必须从同一事件集冻结 Execution Projection 与 SMF Track Projection；消费者不得回读 Project 重建 Pure MIDI Track Name、Root membership、EOT、事件归属或顺序。 |
| INV-054 | `Open MIDI as New Project` 只接受 SMF 1.0 Format 0/1 + TPQN，支持合法 Running Status、MIDI Port 和源 MTrk 多 Channel 拆分；导入边界在验证前对缺失 tick 0 Tempo/Time Signature、同 tick 重复 Tempo 和不可用 Track Name 执行第 23.11.5 节的确定性兼容归一化；候选必须 detached 且原子提交，失败保留当前 Project，初版不支持 Import into Current Project。 |
| INV-055 | SMF 导出固定 Type 1 且不使用 Running Status；Track 顺序为 Conductor → global Arrangement Track order 过滤出的 Pure MIDI Tracks → Logical Unit Tracks（Port/Channel 顺序）。同 Root 跨 Pure MTrk 的顺序敏感同 tick 组合产生汇总 Warning，不得为兼容性改 tick、合并 Track 或改序。 |
| INV-056 | `.midora` 分别以 `midi-channel-roots/mcr_<id>.pb`、`midi-tracks/mt_<id>.pb` 与单 Track `midi-content/mt_<id>.mpk` 保存 Root/Track metadata 和 Direct/Opaque source pages；Auto 分配、canonical 投影、运行期 checkpoint 和 PCM 不持久化。本次未发布开发格式为破坏性替换，不提供旧开发格式迁移或双写。 |
| INV-057 | SMF 多 Channel/Port MTrk 拆分时，每个 opaque SysEx/Meta 必须恰好归属一个同 source MTrk/effective Port 的派生 Track，不得复制或丢弃；无 Channel bucket 但必须保留 opaque/空结构时创建确定的 structure-only Track，纯 Conductor 的 Format 1 MTrk 0 除外。 |
| INV-058 | Arrangement 的正式结构固定为唯一 Conductor 第一行与一个混排 Logical/Pure MIDI Track 的 global tagged order。Event Instrument Definition order、Usage/Root membership 与 Track order 正交；Arrangement 不显示 Event Instrument/Root 空白 parent row。 |
| INV-059 | Event Instrument Definition 的 Copy/Paste/Duplicate 只深拷贝定义及内部对象，不复制 Track/Usage。Logical Track 普通 Duplicate 深拷贝 Track subtree、创建引用同一 Definition 的新独立 Usage并插入完整源 block 之后；只有显式 `Duplicate and Share State` 保留源 Usage并紧邻源 Track 插入。Pure MIDI Track Duplicate 按第 24.8.3 节保留 route membership。Copy/Paste 与 Clipboard Cut 后 Paste 的新对象使用新稳定 ID；只有单命令 Header/brace Drag Move 保持 ID。owner 自动创建/删除与对应 Track 变更必须是一个 Undo。 |
| INV-060 | Track 与共享 Usage/Root block 的 Mute/Solo 仅属运行期且相互独立；过滤必须按来源精确释放和恢复，不得因单 Track 变化向整个共享 Unit 发送 CC120/Reset、杀死 sibling Note 或改变 Project/canonical。 |
| INV-061 | Logical Note 与 Direct MIDI Note 跨类型剪贴板只转换 relative tick、gate、key 和 NoteOn/instance velocity；Logical→Direct 的 NoteOff velocity 为 0，Direct→Logical 丢弃 NoteOff velocity，Direct→Direct 以及 Project/canonical SMF/export 必须保留原 Direct NoteOff velocity。 |
| INV-062 | `.midora` 分别保存 ordered Definition/Usage/Root indexes 与唯一 ordered tagged Arrangement Track index；Track 保存唯一 owner ID，Definition/Root 不再保存第二套 child order。Usage/Root 无成员、Track order 缺失/重复或 owner 不一致时打开失败。 |
| INV-063 | Pure MIDI Segment 概览的 non-Note event 线与 Logical Segment 概览的 Logical Parameter point 线固定绘制在 Note 上层、透明度 50%、最小宽度 1 device pixel，并按各自正式值域归一化高度。Arrangement Segment 概览最高精度使用 `96 pixels / quarter note` 固定基准、256-pixel tile、64-pixel 高度；较低精度只允许固定半八度 `1 / 2^(n/2)` LOD，精确 viewport zoom 不得直接形成 cache identity。正式目标显示层必须选择不向下采样 source pixel 的固定层，全部 tile 从同一 device-pixel-snapped 完整 Segment 变换派生。最多两个后台 worker 为每个 Segment 预热完整且不超过 4 tile 的固定 LOD；可见 Segment fallback 比该预热层提高一倍水平分辨率且最多 8 tile，必须整帧发布，其 LOD 不得依赖 viewport。当前显示层更粗时允许缩放复用该 fallback，禁止缩小视图时清空概览。全部可见 fallback 就绪后才准入新的当前 LOD 请求；目标 tile 就绪后独占其横向范围，禁止在其下叠画 fallback，粗缓存不得因此删除。缓存不得成为命中、编译或导出语义来源。 |
| INV-064 | Conductor 在 Arrangement 固定第一行直接显示按事件类型着色、固定 device-size 的圆点概览，不使用 Segment；极端内容必须用可视 tile、按类型/像素列聚合与局部失效，Project End Marker 仍是专用竖线。 |
| INV-065 | Pure MIDI source 使用 immutable out-of-core pages 与 copy-on-write edit overlay；正常打开、编译、播放准备和 UI 浏览的常驻内存由活动页/可见范围决定，而不得与 Project 总 Direct Note/Event 数线性增长。页必须同时受 record count 与 decoded bytes 双重上限约束，并有明确 owner、checksum、generation、LRU 上限和释放时机。 |
| INV-066 | Canonical Compiled Result 的“一个正式事件集、Execution/SMF 两个冻结投影”是逻辑契约，不是连续数组契约。Pure MIDI 实现必须允许共享 immutable source pages、延迟 canonical range source、紧凑 consumer pages 与有界外部归并；任何正式消费者不得以全量 `ToArray()` 或第二套逐事件对象图作为入口。 |
| INV-067 | 实时播放的滚动准备必须覆盖 source/canonical range query、tick→sample event batches、IPC committed-prefix publication 与 PCM 水位。启动只等待当前光标的状态恢复和 Startup 2 s 连续窗口；超过 Target High 6 s 的远处事件不得被完整物化、hash、复制或传给 Worker 后才允许播放。 |
| INV-068 | 音频 IPC 的容量边界由 24-byte 追加事件记录、最多 16,384-record producer/reader batch、262,144-record Worker ring 和单调 committed prefix 定义，不按整 Project event count 或整计划文件大小定义。合法超大 Project 通过一个 session 私有事件文件与 seqlock control 增量传输；committed prefix大于ring时必须以最后已装载record frame公布排他的partial safe frontier，使renderer可推进并释放ring但不得越过不完整同frame后缀。截断、倒序、非法 committed snapshot、越界或 producer fault必须显式失败；reader/renderer不得互锁，也不能静默截断或提高无界上限。 |
| INV-069 | Pure MIDI Arrangement/Piano Roll/Velocity/Event Lane 的正式 UI 数据入口是按 tick/pitch/lane 的范围查询与 LOD 聚合。打开视图、平移或缩放不得建立全 Segment render item array、全量 ID dictionary 或全量 interval index；命中与编辑必须查询 source/index，不能从 bitmap 反推语义。 |
| INV-070 | Exact Root/Unit PCM 命中必须在 canonical/source range query 前抑制该 owner 覆盖范围内的 MIDI event demand；混合命中只生产 miss owner。Monitoring 使缓存失效时须从实际可听 frame 追加并原子发布新的 event generation，旧 generation 在 Worker 显式 Seek 前保持可读；Reader 的 published snapshot 与 generation-local counters 必须在 `Seek` 的同一 feeder lock 内读取，禁止跨 generation 混算。`Seek` frame 还是新 generation 后续 append 的持续下界，晚到但更早的 rewind-prefix 记录不得进入 ring。切换后对本次 playback generation 单调保持 synthesis bypass，不得留下已跳过事件的未来缺口，也不得改变 Mute/Solo 或可听语义。 |
| INV-071 | Pure MIDI source pack 的播放端点索引由局部有序、最多 16,384-record 的 NoteOn/NoteOff/Channel pages 构成；NoteOn目录携带页内最大end tick。窗口查询使用目录裁剪、有界 k-way merge、Channel-state checkpoint和只读取候选endpoint页的active-note查询，不得反复扫描从Segment起点到光标的全部历史或全部相交原始Note page。索引是 source 的确定性派生物，不能改变 canonical fingerprint与事件顺序。 |
| INV-072 | Reusable PCM miss 必须以16,384-frame block直接顺序写入generation journal；仅完整、结构校验通过的generation可原子进入可命中索引。未完成/损坏journal只形成不可命中的dead bytes并由重整回收，不得将部分PCM block当作可恢复的SoundFont/BASS voice状态。 |
| INV-073 | Event Instrument Usage 是无用户名称的持久共享执行身份。Definition 可以零 Usage；Usage 必须至少一个 Logical Track。未启用逐音符隔离时，Usage 的跨 Track Segment 活动连通区间是真正 Channel Group 生命周期，成员 Segment End 不重置 sibling 状态。 |
| INV-074 | 所有 MIDI Channel Root 必须非空。Fixed Root 没有独立 UI 生命周期，route 只在 Root 保存一份但作为 Track 属性编辑；最后 Track 离开时同事务删除 Root，Undo 恢复原 ID。Fixed members 可分散，Auto shared members 必须连续。 |
| INV-075 | 主窗口不得设置 Global Inspector、Bottom Panel、Details/Tasks Tab 或可见 Task History。Project-backed 属性只由对象所属 Workspace 或固定 `Properties...` 模态对话框呈现；对话框必须编辑 Draft，`OK` 以一个正式原子 Project command 提交，`Cancel` 不改变 Project。所有确认对话框复用同宽度红色 Primary / Cancel action contract，Enter / Escape 分别执行确定 / 取消（焦点控件自身消费 Enter 时除外）。多选 Mixed 字段默认禁用，只有显式开始统一值后才能编辑，并可逐字段还原；UI 不显示 Stable ID 或内部引用编号。 |
| INV-076 | Logical Parameter Lane 是离散 Step 点集：点值自该 tick 起保持到下一点，不存在 Linear/Step 用户选择。创建、复制、粘贴、变换、持久化校验、Full/Incremental Compile 与所有消费者必须保持该语义。Value Curve、Envelope 等其他正式曲线不受此规则替代。 |
| INV-077 | 用户编辑造成 exact collision 时：Logical/Direct/Template Note 的同 start tick + key 后来对象静默丢弃；Logical Parameter、Direct MIDI Channel Event 与 Template MIDI Event 的同 tick + 同正式事件类型由后来编辑对象覆盖原对象。未触及该 exact key 的导入重复 Direct MIDI 数据必须原样保留；碰撞归并属于编辑命令事务并可 Undo，不得由打开、浏览或编译静默改写源数据。 |
| INV-078 | 一次只允许一个可见前台任务表面；主窗口不保留历史任务列表。任务进度只有在有可靠 current/total 时才使用 determinate；MIDI 导入第一遍以源字节、第二遍以 processed/total events 计量。后台编译以当前任务/修订的阶段及可靠阶段百分比反馈，不为计数预扫描/展开，不伪装全程进度，UI 节流合并，结束清理且迟到隔离。取消仅在任务仍处于安全可取消阶段时可用。导入完整兼容报告必须保留到 Dismiss/替换。该运行时状态不持久化、不进入 Undo/Redo 或 canonical/cache 身份。 |
| INV-079 | 所有实时/预览/离线音频任务使用任务开始时冻结的同一程序级 Enabled SF2/SFZ 有序配置；Worker 直接打开原绝对路径（仅 SF2 使用 `BASS_MIDI_FONT_MMAP`），并用 `BASS_MIDI_FONTEX2` 对每个 Unit 一次性设置完整 Font handle 与目标映射。列表、target 或其他音频配置变化只允许在 Stopped/Idle 提交，并在持久化后立即销毁旧 Worker、失效相关 sample-domain 缓存、重建并预热新 Worker；新建、打开、命令行打开、MIDI 导入形成 Project 会话和 Reset Playback Engine 也必须在其前台任务结束前接管或预热 Worker，不得推迟到首次 Play/Preview。成功后的 Worker 必须保留供后续音频操作复用；Project 已提交后的预热失败保留 Project 并作为独立音频运行时错误报告。缓存身份只可基于有序配置与主文件元数据的小型描述符；SFZ 依赖不进入身份，不得重新读取完整 SoundFont 或把该指纹描述为内容校验。 |
| INV-080 | Playback Master Volume、Limiter 与 Stop Cursor Behavior 是 Application Preferences；MIDI Export 与 Audio Render 的模式、范围、选择及输出参数只属于当前任务 Draft。`.midora` 与 Project Domain 不得保存 Playback、Export defaults 或 Audio Render defaults；当前开发格式不包含对应三个 settings 文件，导出/渲染对话框每次使用规格固定初始值。 |
| INV-081 | Event Instrument 可以持久化只存在 Loop Start 或 Loop End 的不完整 Loop Draft，以支持独立字段逐项编辑；该状态必须可 Undo/Redo 和确定性重开，但 Full/Incremental Compile 都必须产生 Error MIDORA1212 且结果不可消费，所有正式消费者不得解释它。两端都空表示禁用；两端齐全时必须满足 `0 <= Start < End <= Template Length`，单个已存在端点也必须位于 Template Length 内。 |
| INV-082 | 离开或关闭 Event Instrument Workspace 时，只自动停止由该 Workspace 底部 Preview Keyboard 启动且仍活动的 Held Preview（包括 Gate-open 与 release-tail）；判定必须基于该键盘预览的显式任务所有权，不得按宽泛 Playback/Preview 状态停止主时间线播放、普通 Preview、Segment/Pitch Ruler Preview 或其他音频任务。该行为是 Runtime/UI 清理，不修改 Project、canonical 或播放光标。 |
| INV-083 | Arrangement Segment 与 Logical/Direct/Template Note 的边界 Resize 在 Snap 开启时以当前有效 Operation Subdivision、Snap 关闭时以 `1 tick` 作为最小长度；批量对象逐项独立饱和。手势前已短于有效步长的对象以原长度为本次最小值，不能被约束反向扩长。交互预览与原子编辑命令必须采用同一最小长度。 |
| INV-084 | MIDI 导出 `README.md` 不记录程序级 SoundFont；其 `Notes` 单行字段必须直接使用本次冻结 Canonical Compiled Result 的准确 MIDI Note On 事件总数，并以 invariant 十进制输出，不得重新统计源对象、估算或使用 Markdown 引用块。 |
| INV-085 | MIDI 导出的每个实际单 Channel 事件 MTrk 都必须在相对 tick 0、结构 Meta 和可选 Channel 10 GS/XG 初始化之后、canonical/opaque 事件之前，依次写入 CC91=0、CC93=0；Conductor 不写。该初始化只属于 SMF 编码结果，不进入 Project/canonical，且不得删除或覆盖随后按冻结顺序写出的 Pure MIDI 用户 CC91/CC93。 |
| INV-086 | 主应用取得 ProgramRoot-scoped 单实例所有权后自动回收 `<ProgramRoot>\.tmp` 下异常退出遗留的 AudioCache、SessionContent、CompilerRuns 与 AudioWorkerExchange owned directory；候选必须同时满足固定 root 的直接子项、当前版本 owner manifest、非 reparse point 和活动独占锁已释放才可删除。裸 GUID、活动、未知、清单不匹配或越界路径必须保留；不得扫描旧 `%LOCALAPPDATA%\Midora`。逐项回收失败不得阻止新 Project/session，但 ProgramRoot 启动能力探测失败必须阻止启动。 |
| INV-087 | Pure MIDI opaque SysEx 的唯一音频特权是可识别且校验有效的 Roland GS DT1 Part Mode 与 Yamaha XG Part Mode。导入必须按 payload target Channel 归属派生 Track；Compiler 保留原 opaque/SMF 数据并额外产生有类型、带来源和正式顺序的 canonical audio event，范围中途起播恢复 Root 当前活动连通区间内最近状态；音频投影重定向到 1-channel Unit channel 0 并以完整规范化 SysEx 发送，随后在同一顺序点显式建立 BASSMIDI Unit 的等价 Melodic/Percussion mode。任意其他 SysEx/Meta、Reset、无效校验和及 continuation 仍不进入音频后端。 |
| INV-088 | Midora 产品 SemVer、Project file format、component schema、Mapping ABI、IPC/cache generation 与用户 Project Version 是独立版本轴。产品版本只有一个构建源。自 `1.0.0-dev` 冻结点起，后续新 1.x 软件必须持续读取有效 Format 1；Format 1 的 JSON/protobuf/content-pack wire 与字段语义不得原地改变，不能表示的新持久化语义必须进入新格式、独立 reader 和 detached migration。 |
| INV-089 | Event Instrument Definition 的 `Pre-Roll Ticks` 固定为 `0..Template Length`、默认 0。仅 Logical Segment Instance 使用：对 Logical Note anchor `A`、偏移 `O`，Instance/template origin=`A-O`，Logical Gate Start/End 仍为 `A`/`A+effective Gate Length`，Mapping `gateLength` 不含 `O`。Initial State、实际 tick 参数、Overlap、Usage 连通区间和 Unit 占用从 origin 起算；origin 早于所属 Segment 有效起点或发生 tick 溢出必须 Error，不得 Clamp、丢弃前缀、自动扩展或跨 Segment。standalone Instrument/SubVoice Preview 与 Pitch Ruler audition 按 `O=0`；中途冷启动不补发范围前 NoteOn，也不做音频预滚。全部正式消费者只消费已应用该语义的 canonical。 |
| INV-090 | Event Instrument Pre-Roll 是 Format 1 无法表达的新语义，冻结 Project Format 2 使用 manifest schema v2 与 Event Instrument protobuf v2 wrapper，其中 `pre_roll_ticks` 为必填 field 4。冻结 V1 reader/descriptor/golden 不变；V1 打开必须 detached 迁移并为每个 Definition 显式设置 0。Format 3/4 writer 复用该 protobuf v2 语义，不得修改 Format 1/2 wire。 |
| INV-091 | Format 3/4 均要求唯一 `settings/project-presentation.json` / `project-presentation-json`；历史 schema 3 reader 保留，当前 writer 使用 schema 4，继续读取 schema 1/2/3。schema 4 在 Onion/All-Tracks、B1 workspace profile、Segment/SubVoice 局部视图/Lane 记忆和 Track/Usage/Root Mute/Solo 外增加 B3 `workspaceNavigation`；presentation 不属于 Project Source Data，不参与编译、canonical fingerprint、音频缓存或音乐 Project Modified；Mute/Solo 和导航只形成 presentation dirty。根级损坏隔离为默认，分区损坏只回退对应 section，打开不自动修复，显式 Save 才写回并独立 Warning。旧软件读音乐但可丢弃 schema 4 新字段。Format 1/2/3 reader/schema/descriptor/golden 必须持续保留。 |
| INV-092 | Format 1/2 打开使用 detached migration，打开阶段绝不写来源。迁移会话普通 Save 必须先向用户显示并冻结来源/目标 Format、来源 identity 与可见永久原字节副本路径；只有严格重开验证的 Format 3 临时包及逐字节一致副本均安全后才可原子替换原路径。取消、identity 改变、确认路径被不同内容抢占或任意前置失败均不得改变来源；Save Copy 写 Format 3 但不清除 migration-dirty。 |
| INV-093 | Midora 自建正式数据只写 `<ProgramRoot>\Data\{Preferences,Recent,Catalogs,Presets,Diagnostics}`，可重建工作数据只写 `<ProgramRoot>\.tmp\{AudioCache,SessionContent,CompilerRuns,AudioWorkerExchange}`。ProgramRoot 是 executable base directory，必须是本机 ready fixed drive 上的普通非 reparse-point 可写目录，并在主窗口创建前通过 create/write/flush/atomic-replace/exclusive-lock/delete 能力探测；失败时 fail closed，不得 fallback 或探测/迁移旧 `%LOCALAPPDATA%\Midora`。 |
| INV-094 | ProgramRoot portable 副本以 `current user + normalized ProgramRoot` 形成独立单实例 scope。`.tmp` 不设置 Hidden；只删除具有当前版本 owner manifest、root 直接子项、非 reparse point 且活动 lock 已释放的目录。未知或无法证明所有权的内容必须保留。Project/MIDI/SoundFont/导出文件仍是用户显式选择的外部路径，不复制进 portable data tree。 |
| INV-095 | 所有百万级 Timeline source 必须提供 revision-bound ordinal/page/range query 与 stable-ID 流式解析；stable ID 仍是唯一业务身份，ordinal 不得持久化或跨 revision 使用。大 Selection 可以使用 page interval/bitmap + sparse include/exclude，不得要求全量 boxed ID、全量 HashSet、逐对象 WPF item 或同步全 source 物化。 |
| INV-096 | 大型编辑必须 detached、分页、资源有界并原子发布：默认 page 4,096 records、每 256 records 检查取消、working 64 MiB、resident staging 64 MiB、owned spill 16 GiB、candidate/result 100,000,000 records。只有全部验证成功且 owner revision 未变化时才能一次 root swap，并发布精确 source trace/change set；Undo/Redo 保存 immutable old/new root/page reference 与小型映射，不保存两份完整对象图。spill 只能位于 `<ProgramRoot>\.tmp\CompilerRuns` owned run。 |
| INV-097 | Timeline Right Down 冻结 owner/source/selection/target/container/tick/修饰键，不同步解码冷页或立即改选择；超过既有 drag threshold 即框选并取消菜单。未拖 Right Up 立即按冻结目标开菜单，冷命中先显示禁用 Locating…；取消右键双击切工具及其等待窗口，右键不再绘线。内容空白保持选择，有效非空选择提供完整选区菜单，混合类型仍显式分类；无选择才使用容器菜单。Escape、失捕获、卸载、revision/owner 失效、新手势或菜单 IsOpen=false 立即取消，晚查询零发布；不得等关闭动画结束。 |
| INV-098 | 有效非空 Selection 的浮动工具跨主模式显示，14 个常驻命令复用现有菜单能力；27 DIP 命中格、居中的 15 DIP 平滑抗锯齿图标，按钮区最多三行，内置即时名称栏，第一行最后的 Deselect All 只清 Workspace 选择；普通与 Inst. 共用分组，窄/矮视图内部滚动。不适用禁用，混合类型显式子菜单，不能 Move 不隐藏可用 Delete。Follow 默认，Pin 锚定世界坐标；Pin 图标不随状态切换，保留背景区别，grip 均可手动移动；Note/Arrangement Segment 两端 Resize、Point 无 Resize，Ctrl+Move 仅走既有 Copy 能力；三种 Note 的 key 越界副本丢弃且源音符保留，不整组 pitch Clamp，事件 value Clamp 不变。普通/浮动 Move/Resize 共用世界 delta、静止边缘自动滚屏、小选择矢量/大选择 raster 预览与全规模有效 delta；不得 viewport clamp、逐对象 WPF 或全选择物化。结束/取消/修订失效停止滚屏。共享主 ToolMode 与独立数值绘线形态不进入音乐 Project/Undo/canonical；后续 profile 持久化按独立 presentation 方案整合。 |
| INV-099 | Note 创建 Snap 量化 Pointer Down 后的长度 delta，不重写冻结初始长度；Arrangement 多 Segment Resize 的矢量预览与提交使用同一 shared delta/最小长度/边界；Velocity onset marker 在低缩放保持固定 device-size；Preview Keyboard velocity 按命中白键/黑键自身可见长度归一化。 |
| INV-100 | Timeline 发起的模态窗口关闭后，在来源仍有效时恢复到原 Timeline Surface。Event Instrument/SubVoice Initial State 的合法整数越界值按正式 MIDI target 值域 Clamp，格式或目标无效仍拒绝。每次 Playback Start 必须针对当前已提交 Project revision 建立计划，新建 Instrument/Usage/Track/Segment/Note 不得依赖 Save/Reopen 才进入首次播放。 |
| INV-101 | Instrument Catalog 是 `<ProgramRoot>\Data\Catalogs` 中独立版本化的程序级名称辅助数据；解析优先级固定为 User Override→Enabled SoundFont 顺序绑定的 Imported Profile→Enabled User Profile 顺序→General MIDI→数值 fallback。Catalog/Profile/名称与 SoundFontEntryId 不进入 Project、canonical、导出、音频配置等价性或缓存身份；损坏只回退名称。 |
| INV-102 | `Add Event Binding...` 冻结当前目标 SubVoice IDs，并以一个失败原子的 Project command创建一个 Integer Logical Parameter、每 SubVoice 一个正式 Mapping及缺失的空 event owner；不得创建 tick 0 event。All 不动态包含以后新增者；CC91/93拒绝；Append/Replace顺序、Override/Add/Multiply accumulator语义、Round/Clamp及一次Undo/Redo必须确定。 |
| INV-103 | Pure MIDI Track 新建/SMF导入按最终 global Arrangement位置使用固定八色 palette轮换；Duplicate/Copy/Paste继承，既有 Track 不因排序/删除重染。Logical Track继续使用独立 ColorOverride→Definition color；颜色只发布 presentation change，不得改变 canonical、导出或音频缓存。 |
| INV-104 | Timeline 工具表达式固定使用独立 batch-note v1、batch-event v2、note-split v1、generate-note v1、generate-event v2 profile；精确变量 schema 见 §20.4.13。非空表达式必须以 `=` 开头，共用 8,192 scalar / 512 syntax node / 64 depth 上限与固定纯数值 Math 白名单；依赖必须无环，结果必须 finite。Generator 不能扩大旧 Batch profile，工具与 Project Mapping ABI v3 互相独立。Preset 只位于 `<ProgramRoot>\Data\Presets`，带 schema/profile/tool/数值契约版本并在每次加载时严格重验证；不进入 Project、Undo 或 canonical。 |
| INV-105 | Humanize 只作用于三类 Note 的 Tick/Gate/Velocity，不改 Key。同一显式 seed 必须依据 owner identity、冻结 formal ordinal 和 field kind 得到稳定结果；Undo/Redo 不重抽样。Tick 越 owner 硬边界删除 Note 且不扩展容器，Velocity/Gate 分别 Clamp 到 `1..127` / 最小 1 tick，最后执行 Note later-loses exact-collision reducer。 |
| INV-106 | Note Split 必须按 owner 的全局选区刀线以 active-interval sweep 生成，提供 Fixed Piece Length、Maximum Piece Count 和受限 Expression；只有 Expression 读取可配置的 Maximum Cuts（默认 65,535），Fixed / Maximum Pieces 必须完整规划且不得被它截断，三种模式共用 16,777,216 刀硬上限。结果记录上限 100,000,000，working/resident 各 64 MiB，owned spill 16 GiB。第一片保留源 ID，Direct MIDI 全片继承 NoteOff velocity。Join 按 owner+key 以非负 Maximum Gap（默认 0）合并，使用第一条 NoteOn velocity、Direct run 最后一条 NoteOff velocity，不改未选 Note。 |
| INV-107 | Note/Event Quantize 复用正式 Snap/Grid/Time Signature 服务，固定 100%、不提供 Bar，中点选早格。Note 提供 Start only 与 Start+End，后者 `end<=start` 时饱和为 `start+1`，exact start+key 按冻结 formal order later-loses。Event 只覆盖 Direct MIDI Channel Event、Logical Parameter Point 和 SubVoice MIDI Event，只改 Tick，exact tick+target 按冻结 formal order later-wins；未命中的导入重复必须保留。全部命令以 detached paged transaction 可取消准备、零部分发布，成功后形成一次 Undo 和确定选择结果。 |
| INV-108 | Batch Create 只在有效 Note owner / 数值 Event lane 上生成正式对象。Generator 的 i 为零基，*0 是上一轮正规化结果/Initial，*1 是本轮 DAG 结果，tr=input t0；Initial 首对象开关默认关闭，开启时首对象计 candidate 0。Maximum Candidates 默认 65,535、硬上限 16,777,216，计迭代而非保留对象。负相对 tick Clamp 0，finite/checked 失败零发布，Note 既有/较早候选优先，Event 较晚候选覆盖命中键；任意 Tick 倒退也必须有界归并与安全取消，Undo/Redo 不重新求值。Preset 不保存 Base/owner/lane。 |
| INV-109 | Logical/MIDI Segment 双向拖动、复制、粘贴共用完整内容转换；保持全局 Track 相对偏移、crop 和 hidden Notes。非共同数据（含空参数 Lane、非零 NoteOff velocity、折叠 exact duplicate）必须冻结类型/数量并一次确认，不按名称推断。Move 的源删除与验证完成的目标属于一个 detached 原子事务；失败、取消、revision race 不改源。成功选择目标并形成一次 Undo，同类型保留全部数据。 |
| INV-110 | Conductor Tempo 只允许离散保持状态，图形为水平保持及变化 tick 的竖直跳变。绘线只生成正式离散点；设备列 first/last/min/max 与标签 LOD 不改变源记录、命中、选择、编译或导出。可见左界恢复前驱值，显示轴不得成为隐式 BPM 合法范围。 |
| INV-111 | Conductor 的虚拟列表、范围命中与编辑使用不可变修订和有界后台准备；不在 UI 线程整表物化百万事件。Tempo/拍号/调号同 tick 后来编辑者覆盖，Marker 保留同 tick 多项；tick 0 必需 Tempo/拍号不可移动或删除。取消、失败及旧修订结果零发布，Undo/Redo 与列表/时间线共享稳定 ID 选择。 |
| INV-112 | Logical/MIDI Segment 与 SubVoice 对象列表只创建可见行，排序及范围选择采用冻结修订、有界可取消后台准备。隐藏/卸载停止请求；列表布局不持久化。Note/Event 混合选择仅由显式类型子菜单处理冻结子集，未处理选择保留，Undo/Redo 恢复完整前后选择；普通类型专属快捷键不得隐式跳过对象。 |
| INV-113 | SubVoice Pre-Roll/Loop 只读覆盖层与三面板共用 tick/device-pixel 变换。Pre-Roll 前缀为半开暗区；Loop 单端只画存在的端点，完整范围只在两端齐全时显示。覆盖层不截获输入；顶部 ruler 的独立 Template/Loop/Pre-Roll 手柄负责冻结修订的预览及一次原子提交，菜单精确输入错误不关闭弹窗。重合 cap 可错开而真实 Tick 不动；不改变编译语义或内容瓦片身份。 |
| INV-114 | Event Instrument Loop 的时间映射独立于长 / 短 / 等长音分类：除短音 One-Shot 外，实际实例局部 Gate horizon 超过 Loop End 即允许跳回并重复半开 Loop，不得以 `Gate Length > Template Length` 为前提。原始事件、Value Curve、状态型映射与 Mapping TemplateTick 必须一致；Envelope 不随 Loop 重启，Logical Parameter 按实际内容 Tick 求值。等长音 / EndAtTemplate / Segment 结束仍优先；已循环的短音 Tail 从 Gate End 接模板 `[Loop End, Template Length)`，持续 Note 不重触发。 |
| INV-115 | Track/SubVoice 洋葱皮是独立只读投影：按 source 暴露范围与正式层顺序映射，目标音符始终在上；不参与选择/命中/编辑/编译/音频，也不污染普通编辑瓦片。后台查询、位图、在途任务和 compiled 索引必须有界、可取消并随会话释放。 |
| INV-116 | All Tracks Compiled 为混合只读显示：Logical 只从完整成功 canonical 按 Port/Channel/Key FIFO 展开并用正式 NoteOn source Track 着色；Pure MIDI 复用当前源音符，不建立整曲 FIFO 索引、不宣称源 Gate 等于最终流配对。保留跨可视起点的 Note，旧 Logical 标为 stale。播放指针/跟随不重建音符缓存；标尺/内容单击仅复用既有 Seek。Onion 的手选列表与 custom/previous/next 显示模式分别保存，快捷命令不得改写手选列表；独立 presentation schema 3 只随显式保存写入，v1/2 仍可读且 v1 读为 custom。Duplicate remap、dormant/Undo 与损坏隔离按 §18.11/§16.7.5 执行，不改变音乐 Modified/Undo、canonical、播放和导出语义。 |
| INV-117 | 完整诊断逻辑序列、ordinal 与严重程度统计使用非负 Int64，保持顺序、重复、来源与失败策略；计数超限明确失败，不发布不完整新结果。WPF 仅对超过 Int32.MaxValue 的筛选结果使用 4096 行分页，筛选和状态统计仍针对全源。MIDI README 仅输出前 1000 条 Warning/Info 文本及精确总数/省略数，不截断正式诊断，不改变音乐语义、Warning-as-error 或 Project 持久化。 |
| INV-118 | SMF 超长 delta 仅在导出编码时用零长度 Text Meta `FF 01 00` 分段，保持原事件 Tick、顺序和 Track/EOT，不进入 Project/canonical/编译诊断、统计或增量检查。每个 MTrk 数据区硬上限为 `0xFFFFFFFF` 字节（不含 8 字节 chunk 头），不因大小拆分，超限只使本次 MIDI 导出原子失败，编译不感知该字节限制。填充成本和字节计数须安全预检、有界流式写入且可取消；其他 MIDI 值域、单条 payload 和 ntrks 硬限制不放宽。成功填充只输出导出级汇总 Info/README 摘要，不逐条列占位。 |
| INV-119 | Instrument Change 是显式创建的持久编辑关联，不是新的音乐事件；MIDI Segment 关联同 Tick CC0/CC32/PC，SubVoice 关联完整 Bank/Program，只保存自身及成员 Stable ID。值改保留、结构破坏按完整事务最终态解组，剩余 raw 保留，Undo 恢复；导入不自动发现包装。新组 Bank→PC 位于本 Track 同 Tick NoteOn 前，不改变较早其他 Track 顺序。Format 4 独立严格关联组件不得以 presentation 回退丢弃；旧 1/2/3 reader/golden 冻结。统一选择器 Program 0～127，Initial State 三字段独立继承，preset audition 经干净 canonical/现有 Master→Limiter，仅停止自身 owner，不抢停普通播放。 |
| INV-120 | 三类钢琴卷帘的事件编辑器按正式 target 展示 Lane Tabs；Vel. 固定第一，MIDI Segment/SubVoice 的 Inst. 固定第二。隐藏或重排只改会话视图，不删除数据、Mapping 或选择；被动刷新/Undo/选择不隐式导航，显式 Add/Locate/目录才显示并激活目标。目录计数按冻结实际 source 建立有界、可取消、修订隔离的后台摘要；未知不能显示为 0。各 target 的纵轴独立、水平共享，Piano/Event Snap 分离而事件 targets 共用 Event Snap；只保留一个活动画布。Instrument Change 的 List 行替代其成员行，选择仍使用真实成员 ID，所有包装批改经完整原子 raw 事务与最终态关联校验。 |

| INV-121 | CC10／71～78 的外侧图形、列表、Properties、适用 Initial/Reset State 和 Batch/Generator 使用 raw−64 的显示域，提交一次反变换；delta/factor 不偏移，Generator 反馈保持显示域。Project/Mapping 全链/Context/canonical/文件/音频仍 raw；其他 target 及 PB 既有契约不变。Event 工具 profile/numeric contract v2，旧 Event Preset 不静默重解释，保留原文件并明确不兼容。 |
| INV-122 | 三宿主除 Vel./Inst. 外全部 Lane 的辅助阶梯线，由程序级 Appearance 全局开关控制、默认启用。只连接自身 owner/target 显式点与自身前驱，正式同 Tick order 不以 ID 替代；不补 Initial/default、不跨 owner，不参与命中或编辑。包含 Bank-PC/协议命令/opaque，opaque 沿用点的固定 y、不解释 payload，线不声明 MIDI 状态持续；Value Curve/Envelope 不变。crop 外弱化，SubVoice 线止于模板末尾；设备列聚合与范围缓存必须有界、异步、可取消，迟到任务不得串目标或覆盖新修订，点/选择优先。不设逐 Lane 开关，不进入 Project/Undo/Modified，不因此重建 Worker。 |

| INV-123 | B1 编辑器会话状态按正式 Track profile 与 Segment 局部描述划分；SubVoice 以 Definition + SubVoice ID 独立，不与 Usage/Root/其他 Track 共享。只共享偏好，不共享 TPQN/拍号/参考时间解析上下文。关闭页签只留有界纯值，不留 VM/音乐页/选择/任务；删除 owner 释放其自身描述，Undo 不自动复活或重开。新状态不进入音乐 Modified/Undo/canonical、Onion revision 或现行文件 schema；显式 Arrangement 导航优先于局部位置恢复。迟到 Lane 目录须校验 owner/代次，未知不能当删除，普通状态同步不扫描音乐对象。 |
| INV-124 | B3 当前 `settings/project-presentation.json` writer 使用 schema 4，继续读取 schema 1/2/3；`workspaceNavigation` 只保存有序 Workspace key、ActiveTab 和批准的页面/viewport 纯值，不保存 VM、选择、Undo/Redo、任务、焦点、菜单、缓存、编译结果或音频资源。Arrangement 始终首位；失效 owner、重复/非法 key 或失效 ActiveTab 不创建幽灵 Workspace，活动项回退 Arrangement 并给出独立状态提示。打开只物化 Arrangement 与 Active Workspace，其余 Tab 以有限轻量描述懒激活；隐藏页不预热大型内容，Active Compiled 只沿既有可取消后台链路准备。导航 section 参加 64 MiB presentation 总预算，超限完整省略而不静默截断。导航恢复不改变音乐 source、canonical、Project Modified、Undo、MIDI/WAV 或音频语义。 |

## 22.2 常用主题定位
| 需要查找的主题 | 主要章节 |
|---|---|
| 软件定位、技术边界 | 第 1 章 |
| 术语、编号、身份、确定性 | 第 2 章 |
| Project、保存入口、修改状态 | 第 3 章 |
| Project Format 1/2/3/4、presentation、旧格式原路径升级、ProgramRoot portable storage | 第 3、16、17、19～21 章；关联组件见 §16.35 |
| tick、TPQ、Tempo、拍号、Marker | 第 4 章 |
| Port、Channel Unit、资源不足 | 第 5 章 |
| 程序级多 SF2/SFZ 列表、目标 Bank/Program 映射、Instrument Catalog、显式 SF2 preset scan、无 Enabled SoundFont、BASS 直接读取与缓存身份 | 第 6、13、15、17、20 章；INV-101 |
| Event Instrument 定义、Pre-Roll Ticks 与内部索引 | 第 7、9～13、16、18、24 章 |
| SubVoice、Note/CC/RPN 等事件 | 第 8 章 |
| A2a：显式音色变更关联、统一选择器、Initial State 与独立 preset audition | §8.55、§13.31、§16.35、§18.4；INV-119 |
| A2b：包装全编辑、List 投影、Lane Tabs、目录与独立纵轴 | §8.55.4、§18.2.5/7/8、§18.4.2；INV-119～120 |
| Logical Parameter、映射、快捷 Event Binding 和受限 Mapping Function | 第 9、18、20 章；INV-102 |
| Release、Loop、Envelope、Overlap | 第 10 章；Loop 进入条件见 §10.9.5、INV-114 |
| Logical Track、Logical Segment、裁剪与 Logical Note | 第 11 章 |
| CompileContext、资源分配、Compiled Result | 第 12 章 |
| Int64 完整诊断、超限失败、有界 WPF 分页、README 前 1000 条 | §12.19.10、§14.15.4、§17.5；INV-117 |
| 播放、预览、held Preview 因果 Gate、BASSMIDI、程序级 Playback Preferences、输出设备、采样率、buffer、Limiter | 第 9、12、13、17、20 章 |
| MIDI 文件结构与导出 | 第 14、23 章 |
| 超长 delta 填充、单个 MTrk 字节上限、不拆 Track、导出级诊断 | §4.13、§12.23.2、§14.12.2/8/9、§14.15.7、§14.19.8、§23.12；INV-118 |
| 普通 RIFF/WAVE、自定义采样率与离线渲染 | 第 15 章 |
| `.midora` package、schema、损坏与事务 | 第 16 章 |
| 产品 SemVer、Project Format 冻结、兼容迁移、Git tag 与发布门 | 第 16、21 章 |
| 主窗口、导航、对象所属属性编辑器和全局面板 | 第 17、24 章 |
| 各编辑器工作区、Timeline 精确属性与事务式 Properties | 第 17、18、20、24 章 |
| Conductor 虚拟列表、Tempo 阶梯图、绘线、密集元事件与原子批量编辑 | 第 4、18、20 章；INV-110～111 |
| 三种钢琴卷帘虚拟对象列表、混合选择子菜单、SubVoice Loop/Pre-Roll 覆盖层 | 第 18、20 章；INV-112～113 |
| A1：模板外事件创建、共同 scalar 饱和、PB 显示坐标、Catalog/Combo 滚动、Add Event 导航、对象列表与设置入口 | 第 17.7.3、18.2.7/8、18.4.2/4、19.1.2、20.4.5、20.15.4 节；INV-031、047、075、077、095～096、112 |
| Track/SubVoice 洋葱皮、All Tracks Raw/Compiled、有界只读缓存、来源色和 Stale | 第 3、16、18 章；INV-115～116 |
| New/Open/Open MIDI as New Project/Save/Export/Render 工作流 | 第 17、19、23 章 |
| 选择、分页 ordinal/range query、detached edit、浮动工具、拖放、验证、快捷键和 UI 验收 | 第 18、20、23、24 章；INV-095～100 |
| 指定 CC 友好数值、事件辅助阶梯线 | §8.54.4、§18.2.9/10、§20.4.13；INV-121/122 |
| 工具表达式 profile、Preset、Humanize、Note Split/Join、Note/Event Quantize、Batch Create、Segment 双向转换 | 第 18、20、23 章；INV-104～109 |
| 初版排除项、实现自由度和变更控制 | 第 21 章 |
| MIDI Channel Root、Pure MIDI Track、Track Color、Midi Segment、SMF 导入、Running Status、Pure MIDI 导出拓扑 | 第 23、24 章；INV-103 |
| Arrangement 平铺 Track order、Event Instrument Usage、隐式 Root、独立/共享 Duplicate、共享块、跨类型 Note 剪贴板、Pure MIDI/Conductor 概览缓存 | 第 24 章 |
| 极端 Pure MIDI page pack、分页 canonical、滚动事件 IPC、范围查询 UI | 第 12、13、16、18、23、24 章 |
## 22.3 推荐引用方式
在讨论、设计记录、Issue 和代码评审中，应使用：
```text
《Midora SRS》第 12 章“编译系统与 Canonical Compiled Result”
《Midora SRS》§16.21“打开流程”
《Midora SRS》INV-009
```
章节号和小节标题共同构成引用。仅引用标题而不引用章节号时，应避免使用容易重复的泛化名称。
