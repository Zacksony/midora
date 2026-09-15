# Midora 跨聊天续接提示词

用途：把下方整个 “text” 代码块原样复制到新的 Codex 聊天。它是详尽的接手索引，但不替代仓库中的 SRS、ADR、源码、测试和实时 Git 状态。

~~~text
你现在接续 Midora 项目的后续开发。仓库绝对路径：

D:\Programing\midora

默认使用简体中文。沟通必须严谨、直接、以事实为先。必须明确区分：

- SRS 已规定的正式需求；
- 产品所有者已经确认的变更决定；
- ADR 已接受的实现决定；
- 从当前源码、测试、构建和 Git 状态确认的实现事实；
- 合理设计推断；
- 尚未实现的能力；
- 尚待产品所有者确认的问题。

不得把路线图、历史记录、原型行为、“已经能发声”、合理猜测或本提示词本身冒充正式需求。

================================================================
一、接手后必须先做的检查
================================================================

1. 完整读取以下文件，不得只看摘要：

   D:\Programing\midora\AGENTS.md
   D:\Programing\midora\misc\Midora-SRS-Initial-Release-v0.1\00-Table-of-Contents-and-Document-Control.md
   D:\Programing\midora\misc\Midora-SRS-Initial-Release-v0.1\22-Requirement-Locator-and-Cross-System-Invariants.md
   D:\Programing\midora\misc\Midora-SRS-Initial-Release-v0.1\23-Pure-MIDI-Tracks-and-SMF-Import.md
   D:\Programing\midora\misc\Midora-SRS-Initial-Release-v0.1\24-Arrangement-Hierarchy-and-Preview.md
   D:\Programing\midora\misc\Midora-Pure-MIDI-Tracks-and-SMF-Import-Architecture-Decisions.md
   D:\Programing\midora\misc\Midora-Arrangement-Hierarchy-and-Preview-Requirement-Trace.md
   D:\Programing\midora\misc\Midora-Non-UI-Implementation-Tracker.md
   D:\Programing\midora\misc\Midora-Non-UI-Decision-Question-Library.md
   D:\Programing\midora\misc\Midora-Domain-Compiler-Conformance-Matrix.md
   D:\Programing\midora\misc\Midora-Segment-Unit-Audio-Cache-Requirement-Trace.md
   D:\Programing\midora\misc\Midora-SF2-Release-Gate-Matrix-2026-08-08.md
   D:\Programing\midora\misc\Midora-Core-Architecture-Decisions.md
   D:\Programing\midora\misc\Midora-Audio-Backend-Architecture-Decisions.md
   D:\Programing\midora\misc\Midora-Manual-Audio-Acceptance.md
   D:\Programing\midora\misc\Midora-Implementation-Roadmap.md

2. 再完整读取与用户新任务直接相关的 SRS 章节。若下一阶段是 WPF，至少读取第 17、18、20、24 章，以及第 3、13、19 章中与应用生命周期、播放、任务工作流有关的部分。

3. 需求和记录的权威顺序：

   - misc/Midora-SRS-Initial-Release-v0.1/ 是当前正式需求基线。
   - 产品所有者在 Midora-Non-UI-Decision-Question-Library.md 中已经明确批准、且目的就是修改 SRS 的决定，是有效变更授权。
   - AGENTS.md 是仓库工作约束和关键规则摘要。
   - Core/Audio Architecture Decisions 是已接受设计记录。
   - Non-UI Implementation Tracker 和各 Requirement Trace 记录实施与验证事实。
   - Implementation Roadmap 是历史路线图和建议，不是需求规范；其中旧的 ABI v1、每 Port Stream、默认 750 等历史文字不得覆盖当前 SRS/ADR/源码。
   - 本交接提示仅用于导航；冲突时必须回到正式文件、提交历史和源码核验。

4. 立即执行只读 Git 检查：

   git status --short --branch
   git log -8 --oneline --decorate
   git diff --stat
   git diff --cached --stat
   git rev-parse HEAD
   git rev-parse origin/main

5. 交接生成时的 Git 快照：

   - 分支：main
   - 非 UI 完整基线提交：4001981e69328c00a17573affd16309f6189cb14
   - 提交标题：feat: complete initial non-UI feature set
   - 该提交已推送到 origin/main。
   - 交接文档本身可能位于后续提交；必须以新聊天实时 HEAD 为准。
   - 交接生成前工作树已经干净；新聊天仍须重新检查，不得盲信。

6. 所有现有工作树修改先视为用户改动。不得 reset、checkout、覆盖、删除、移动、批量格式化或顺手提交无关文件。只有从任务、diff 和提交历史明确证明归属后才能纳入本轮。

================================================================
二、产品所有者要求的工作方式
================================================================

1. 原始范围是“完成初版所有 UI 以外的功能及完整自动测试”；该范围现已完成。不要重新实现已有非 UI 模块，也不要把持续加固误写成已知功能缺口。

2. 若后续发现需要产品决定：

   - 小决定，或不同选择影响范围很小：先按你的推荐方案实施，随后把问题、方案、影响、已实施选择和测试证据详尽记录到 Midora-Non-UI-Decision-Question-Library.md，交给产品所有者确认或修改。
   - 大决定，或不同选择会广泛影响可听语义、文件兼容性、确定性、公共接口、并发模型、用户工作流：暂停受影响部分，先把问题、SRS/源码矛盾、候选方案、推荐方案和影响写入问题库，再请求产品所有者决定。其他不依赖该决定的工作继续。
   - 以前已经确认的问题不得重复询问。

3. 不涉及人耳或物理设备的验证由你自行运行、修复并闭合。必须人耳试听或物理设备操作的项目集中列出准确命令、预期与观察点，让产品所有者逐条执行；失败时完整保存原始输出快照，本轮是否修复以用户指令为准。

4. 用户已经授权：完整、已验证且范围清晰的增量需要时可以提交并推送 Git。提交前必须检查 staged diff、禁止混入 SF2/BASS DLL/AOT/TRX/artifacts，并在推送后核对 HEAD、upstream 和工作树。

5. 修改前先写简短 requirement trace，至少包含输入、正式输出、范围/资源/文件边界、失败条件、诊断、持久化归属、运行时归属和明确非目标。涉及可听语义、持久化格式、公共接口或并发所有权时先更新 ADR/Requirement Trace。

================================================================
三、当前项目状态
================================================================

截至 2026-08-08，SRS 初版全部非 UI 源码已经闭合：

- Domain 与完整 Project 源模型；
- 统一 History、Undo/Redo、Modified、稳定 ID 分配；
- Semantic Validation；
- Full/Incremental Compiler 与 Canonical Compiled Result；
- Playback、全部 Preview、held Preview 因果 Gate；
- MIDI Export；
- 实时/离线 BASS 音频、WASAPI、Native AOT Worker；
- 五层编译/PCM/播放跨度缓存与 Buffering 恢复；
- Audio File Render；
- .midora JSON/protobuf/ZIP 持久化；
- Application Preferences、任务协调、New/Open/Save/Save Copy、Recent Projects、单实例 IPC；
- 输出规划、命名、覆盖与原子文件事务；
- 非 UI Clipboard 与 batch editing；
- 发布脚本、固定测试基线和验证文档。

当前没有已知的非 UI 功能缺口，也没有未回答的大决定。后续主要阶段是 WPF UI composition、交互和 UI 验收。

当前没有 WPF project、XAML 或 Desktop 入口文件；创建前必须先核对实时仓库，不能假定已有 UI 骨架。

================================================================
四、不可破坏的系统主线
================================================================

所有正式消费者必须遵循：

Project Source Data
→ Semantic Validation
→ Compilation
→ Canonical Compiled Result
→ Playback / Preview / MIDI Export / Audio Rendering

硬约束：

1. Project 是完整语义上下文；消费者和 UI 不得重新解释 Event Instrument、Mapping、Lifecycle、Logical Track、Segment 或资源分配。
2. Canonical Compiled Result 是播放、预览、MIDI 导出和音频渲染唯一正式音乐语义输入。
3. Full 与 Incremental 对同一输入必须语义和形式逐字段完全一致。
4. 稳定 ID 是身份；名称、列表位置、tick、MIDI Track/Port/Channel 不是身份。
5. 正式结果不得依赖集合枚举顺序、线程竞争、历史分配、随机数、缓存命中或当前 UI 状态。
6. 统一范围为 [startTick, endTick)。Segment、End Marker、播放和渲染硬边界必须精确 NoteOff、Reset、CC120 与资源清理。
7. Mute/Solo 只属于运行时消费过滤；不得修改 Project、canonical、MIDI 导出或离线正式内容。
8. .midora 只保存源数据；不得保存 canonical、编译缓存、PCM 缓存、导出结果、Undo/Redo 或会话 UI 状态。
9. UI 只能展示、编辑、导航正式模型与诊断；不得创建第二套业务语义。
10. 初版固定 Windows Desktop、.NET 10、WPF、MIDI 1.0、win-x64、单用户可启动实例、单 Project、单 Project SF2、最多 16 Port × 16 Channel Unit。
11. 不加入 MIDI 2.0、MPE、VST/DAW host、传统实时 MIDI OUT、录音、语义级 Voice Stealing、多 SoundFont、SFZ/DLS、Pause/Scrub、多 Project或其他 SRS 排除项。

================================================================
五、关键已确认决定与当前语义
================================================================

完整记录以问题库和 ADR 为准。以下是最容易被旧上下文误导的当前规则。

1. 稳定 ID：

   - MidoraId 核心是单个正 long，范围 1..long.MaxValue。
   - Project 持有单调 nextStableId。
   - JSON 使用 canonical 十进制 integer；对象文件名使用无符号、无前导零的十进制 ASCII；protobuf 使用标量 int64。
   - 开发期旧 Guid/UInt128/high-low 格式不兼容读取或迁移。

2. C# Mapping：

   - 当前正式内部契约是 Mapping ABI v2，不是 v1。
   - 固定签名为 double Transform(double value, in MappingContextV2 context)。
   - 稳定身份通过 MappingStableIdV2(long) 传递。
   - 固定 Roslyn 5.3.0、C# 14、Microsoft.NETCore.App.Ref 10.0.10、独立只读契约。
   - 每 Project 只缓存当前源码修订，collectible ALC 卸载；不是 sandbox。
   - ABI v1 已在开发期被 v2 取代，不提供并行回退。

3. 时间：

   - TPQ 范围 1..32767。
   - 每个 Time Signature 必须满足 4 × TPQ % denominator == 0。
   - Bar:Beat:Tick 为 1-based Bar/Beat、0-based Tick，必须可逆。
   - 中途 Time Signature 截断旧小节并从变化 tick 开始新 Bar，同时产生 Warning。
   - 自然拍网格/Snap 使用统一 ProjectTimeSignatureMap。

4. Segment Split：

   - 右侧 Segment 必须保留/生成 split tick 必要参数起点状态，使参数听感不变。
   - 普通 Segment 边界不做隐式跨 Segment 状态继承。
   - 跨 split tick Logical Note 仍按提前结束规则处理。

5. allocation group 清理：

   - 实际 group 结束时顺序为精确 NoteOff → CC120 All Sound Off → 已使用目标 Reset → Unit 释放/同 tick 复用。
   - CC120 已用真实 BASS/SF2 验证：可截断 SF2 release，4 ms 防爆音后稳定为零，且只作用于目标 Channel。
   - MIDI exporter 忠实导出 canonical 中的 CC120。

6. held Preview：

   - Event Instrument、SubVoice、钢琴卷帘左侧琴键、放置单 Note 草稿复用同一 held Preview 因果 Gate、Preview Compiler、正式音频链与 Worker 协议。
   - Gate Open 使用未知最终 Gate 的因果语义；Gate End 只作用于 producer 尚未渲染 frontier，不能回写已消费/device ring PCM。
   - 当前内部调度为 8 秒 causal window、剩余 4 秒时续接；这是 Q-NUI-031 的已实施待确认小决定。

7. 设备丢失：

   - 活动输出被拔掉/禁用时不得让进程崩溃。
   - Worker 断开输出并受控退出；主程序要求用户手动重新指定设备。
   - 不自动静默切换到默认或其他设备。
   - Console 验收路径用退出码 2 表示受控 DeviceLost，不是普通 fault。

8. MIDI：

   - SMF Type 1 导出与 SMF Format 0/1 导入的 Tempo/Time Signature/Bank/Program/UTF-8/Running Status/EOT/Track 拓扑规则见 AGENTS.md 与 SRS 第 23 章。
   - Logical Channel 10 必须 melodic；Pure MIDI Root 使用显式 Melodic/Percussion mode。仅 Logical Channel 10 与 Melodic Pure MIDI Channel 10 MTrk 在 tick 0 写 GS→XG Normal Part SysEx，不发送 Reset。
   - 2026-09-10 已定案、代码待实施：单 delta 超过 `0x0FFFFFFF` 时仅在 SMF 编码插入空 Text Meta `FF 01 00`，不修改 canonical、不加编译扫描。MTrk 数据区最多 `0xFFFFFFFF` 字节（不含 8 字节头），不拆分，超限仅导出失败。详见 SRS §14.12.2/8/9、INV-118 和 `misc/Midora-SMF-Export-Timing-Padding-and-Size-Limits-Architecture-Decisions.md`；旧严格 delta 拒绝测试不代表新规则已完成。

9. 持久化：

   - 当前仍是开发期 v1；Q-NUI-003/Q-NUI-023/Q-NUI-024/Q-NUI-025 的变化直接修订 v1，不虚构 v2 迁移。
   - 初版不存在受支持的旧格式成功迁移样本；未知来源版本严格失败。
   - JSON Schema Draft 2020-12、source-generated DTO、protobuf Edition 2024、严格重复/未知字段拒绝、确定性 ZIP 和安全事务均已实施。
   - External SF2 只允许 Project 根目录或直属 soundfonts/；Embedded SF2 使用资源 lease 和损坏修复门。

10. 输出与许可：

   - MIDI/Audio 共用 Windows 安全文件名合法化、冻结路径规划、稳定冲突后缀和独立覆盖授权。
   - Midora 自有代码采用标准 MIT，Copyright (c) 2026 Midora contributors。
   - 仓库不提交 BASS DLL。
   - 产品所有者已按当前免费、开源、非商业、GitHub Release、公开身份 Zacksony 的条件放行正式含 DLL 分发，但实际发布日仍须复核官方条款并把供应商原始许可文本与 notices 一并打包。

================================================================
六、五层缓存与 Buffering 恢复
================================================================

Q-NUI-034～Q-NUI-042 已确认并完整实施：

1. Project revision / canonical range cache。
2. 与物理 Port/Channel 无关的 Segment/抽象 Unit compiled fragment cache。
3. canonical 派生、位于 Mute/Solo/Master/Limiter 前的 raw Unit PCM cache。
4. 完成确定性求和、audible set、Master、Limiter 的 exact playback span cache。
5. 用户 Render-Ahead 对应的短 SPSC ring。

规则：

- 正式 canonical 仍保留全局 Port/Channel 分配和最多 256 Unit 检查；缓存不得绕过 canonical。
- canonical 成功后投影为抽象 Unit；每个实际 Unit 使用干净 1-channel BASSMIDI decode stream 语义，由有界 pool 复用，不为 Project 历史 Unit 永久保留 stream。
- raw Unit key 包含 Segment/Unit semantic fingerprint、局部 Tempo、SF2 SHA-256、固定 native baseline、采样率、格式、NOFX/NOTEOFF1、插值、CPU、Maximum Sample Voices 等全部声音环境。
- exact playback span 命中时完全绕过 BASSMIDI；raw Unit 命中时只重新混音/Master/Limiter。
- underrun 在 frame F 锁存，不推进音乐位置或 ring read position；先完整准备 [F,R) 再连续恢复。
- 若 F 在 Bar 起点，目标是当前完整 Bar；若在 Bar 中途，目标是当前 Bar 剩余 + 下一完整自然 Bar；最终受播放终点和 F + 16 个四分音符裁剪。
- reusable cache 是 session-scoped 磁盘后备 + 有界 RAM hot-set，不写入 .midora。
- Application Preferences 缓存 root 默认 %LOCALAPPDATA%\Midora\AudioCache；默认 reusable quota 16 GiB，允许用户选择本机绝对目录和设置 0。
- quota=0 表示长期缓存关闭、每次实时重渲染；普通长期缓存空间不足/写失败只报告状态 Warning，继续现渲染，不让播放失败。
- recovery transient spool 独立于 reusable quota；磁盘不可用时用 Preparing 预留的 unmanaged RAM；两者都不可用才在实际 underrun 时受控 Stop。
- 实时与离线 Maximum Sample Voices per Unit Stream 是两个独立用户值，范围 1..16,777,216，新安装/新 Project 默认均为 500。
- 修改任一 voice 值必须在无活动音频任务时提交，并轮换当前 Project/session 全部 PCM/audio cache generation；tick-domain compiler/canonical cache不失效。
- 设备或实际采样率变化使全部 sample-domain cache 失效；相同格式的设备可复用 PCM。
- Segment Preview 可复用 raw Unit cache；Event/SubVoice/琴键/草稿 held Preview 使用 transient generation，不把未知/未提交内容写入 Project cache。
- Audio Render 在 key 全同的情况下可复用 raw Unit cache，但自行构建 Track selection、Master/Limiter、范围和原子文件事务。

================================================================
七、音频后端强制边界
================================================================

1. BASS 是实现细节，不得泄漏进 Domain/Compiler。
2. 所有实际 Port 使用同一个有效 Project SF2。无有效 SF2 可打开、编译、MIDI 导出，但禁止播放、Preview、Audio Render。
3. 所有正式 stream 使用 BASS_MIDI_NOFX | BASS_MIDI_NOTEOFF1；Event Instrument/SubVoice 拒绝 CC91/CC93，Pure MIDI 保留它们且音频投影不解释其效果。
4. Logical Channel 10 显式 melodic；Pure MIDI Root 按 descriptor 建立 Melodic/Percussion；同 Port/Channel/pitch Note 按 FIFO 逐个 NoteOff。
5. 固定 BASS_ATTRIB_MIDI_SRC=1、BASS_ATTRIB_MIDI_CPU=0。
6. Preparing 用 BASS_MIDI_FontLoad 预加载引用 preset/fallback，不在实时事件 stream 调 StreamLoadSamples。
7. 正式链为 Unit/Port stereo 确定性求和 → Playback Master Volume → 单全局 Limiter → WASAPI；Preview 同链。离线语义相同但不依赖物理设备。
8. WASAPI callback 不编译、不分配、不阻塞、不等待锁、不做文件/网络 I/O；异常不得越过 native callback。
9. Playing、Buffering、Preview、Rendering 的 callback/render/mix/ring/文件 sample 写线程零托管堆分配。
10. buffer 协议以 frame 为单位，明确 sample rate/channel/format/frame count。
11. Render-Ahead 20–2000 ms 默认 100；Device Request 5–200 ms 默认 50；voice 默认 500。
12. 实时采样率跟随设备实际值；文件采样率 8,000–192,000 任意整数，默认 48,000。
13. 文件输出固定普通 RIFF/WAVE、stereo、interleaved IEEE float32 LE；超过 RIFF 上限 Preparing 原子失败，不 RF64。
14. Native AOT Worker 固定 self-contained win-x64。
15. 固定原生基线：

    BASS 2.4.18.3 / 0x02041203
    BASSMIDI 2.4.16.0 / 0x02041000
    BASSWASAPI 2.4.4.1 / 0x02040401

    SHA-256 以 src/midora-audio/bass-native-baseline.win-x64.json 为准。

================================================================
八、当前验证证据
================================================================

当前精确非 UI 自动基线为 1053 项：

- Midora.Common.Tests：68
- Midora.Compiler.Tests：250
- Midora.MidiExport.Tests：33
- Midora.Persistence.Tests：94
- Midora.AudioRender.Tests：36
- Midora.Application.Tests：274
- Midora.Playback.Tests：75
- Midora.Audio.Bass.Tests：170
- Midora.AudioDevice.BassWasapi.Tests：35
- Midora.Midi.Tests：18

八组真实 SF2 分别独立运行完整 Test-NonUIRelease.ps1：

1. D:\Soundfonts\sf2\sDetrimental Concert Grand Piano.sf2
2. D:\Soundfonts\sf2\SGM-V2.01.sf2
3. D:\Soundfonts\sf2\JV1080Ti.sf2
4. D:\Soundfonts\sf2\Ultima C7 Grand II.sf2
5. D:\Soundfonts\sf2\Z-Doc Acoustic Piano Fantasy Mode.sf2
6. D:\Soundfonts\sf2\Roland XP-80.sf2
7. D:\Soundfonts\sf2\Splendid_256.sf2
8. D:\Soundfonts\sf2\minecraft.sf2

结果：

- 每组 1053/1053、0 failure、0 skip；
- 累计 8424/8424；
- 80 个 TRX 已解析复核；
- 每组 6 solution CI Release build 均 0 warning / 0 error；
- 每组发布当前 win-x64 Native AOT Worker；
- 八套目录均包含 Worker exe、native-manifest.json、LICENSE、THIRD-PARTY-NOTICES.md；
- 八套目录均再次通过固定版本/SHA-256/架构/DLL 集合校验；
- 真实 cache miss→publish→exact hit PCM 逐样本等价；
- exact hit 路径 ChannelGetData synthesis frame 数严格为 0；
- 最大 256 个 1-channel Unit Stream、完整拉取、render 热路径 0 B、无 renderer fault；
- Native AOT 子进程集成通过。

完整矩阵：

D:\Programing\midora\misc\Midora-SF2-Release-Gate-Matrix-2026-08-08.md

证据目录：

D:\Programing\midora\artifacts\non-ui-release-gate-sf2-01-sdetrimental-20260808
至
D:\Programing\midora\artifacts\non-ui-release-gate-sf2-08-minecraft-20260808

artifacts 被 Git 忽略，不得提交。新聊天应先确认本机目录仍存在；不存在不代表测试历史失效，但不得声称重新运行。

人工音频验收 M-AUD-001～M-AUD-012 已全部通过。原始快照和复测结果位于 misc/Midora-Manual-Audio-Acceptance*.md。除 UI pointer/交互时延和未来新增物理设备场景外，不要重复要求既有清单。

不要把历史通过当作新修改后的验证结果。每次改动后只报告实际运行且成功的检查。

================================================================
九、已实施、待产品所有者确认的小决定
================================================================

这些问题不阻塞当前实现；已按推荐方案实施。若产品所有者在新聊天给出答复，先记录，再按答复保持或修改。不得把它们误报为未实现功能。

- Q-NUI-029：运行时临时 MIDI Render Plan 在稳定 ID 改为 long 时升级内部版本，而不是原地改变旧布局。该原则已实施；当前 MDAP 后续已继续升级到 v4。
- Q-NUI-031：held Preview 使用 8 秒 causal window、剩余 4 秒时续接。
- Q-NUI-032：Clipboard 使用不透明进程内 payload + Plain Text Summary + ProjectDocumentSession 身份；不支持重启/跨进程恢复内部对象。
- Q-NUI-033：Compatible ordered content 初版包含同 Event Instrument、exact target 的 ValueCurve points 和 MappingChain。
- Q-NUI-043：自然拍 Snap 完全等距时选择后一个边界。
- Q-NUI-044：内部 MDAP 直接升级 v4，加入确定性 Unit fragment 元数据，只接受 v4，不兼容旧临时文件。
- Q-NUI-045：共享 Worker ABI v4 使用既有 64-bit Payload 编码 BufferingRecoveryPrepare(endFrame)，不扩大 record/header。
- Q-NUI-046：raw cache hit 的活动 Unit 被 Mute/Solo 过滤时，从 producer frontier 做固定 4 ms 线性退场。
- Q-NUI-047：final span hit 后切换 Mute/Solo，在 producer frontier 失效旧 generation，冷启动底层、Limiter 重置，并作 4 ms 最终 PCM 交叉淡化。
- Q-NUI-048：专用低优先级缓存 I/O 线程；每 raw Unit 16,384-frame 读/写 hot-set，4,096-frame 刷盘；最多 256 Unit 时读写约 64 MiB；实时背压进入 Buffering，离线原 frame 等待。

问题全文、候选方案和影响在：

D:\Programing\midora\misc\Midora-Non-UI-Decision-Question-Library.md

当前没有未回答的大决定。

================================================================
十、后续真正剩余的工作
================================================================

1. WPF UI 阶段：

   - 创建 win-x64 .NET 10 WPF 主应用与 composition root；
   - 单实例启动、主窗口激活和 Project open request 接线；
   - Project Switch Guard、New/Open/Save/Save Copy、Recent Projects；
   - Arrangement、Event Instrument、SubVoice、Piano Roll、Conductor、Mapping 等工作区；
   - Transport、播放范围、Loop、Mute/Solo、Preview、设备选择；
   - 钢琴卷帘左键琴键 held preview、放置单 Note held preview；
   - Preferences：Render-Ahead、Device Request、Realtime voices、cache root/quota；
   - Audio Render task draft：Offline voices、采样率及本次任务范围；不持久化为 Project defaults；
   - MIDI/Audio 输出规划、路径预览、覆盖授权、Progress、取消和结果；
   - 诊断展示、Warning-as-error、损坏对象/资源修复工作流；
   - Clipboard、焦点、Selection、CanExecute、Pointer Capture、失焦释放；
   - DPI、键盘导航、可访问性、窗口生命周期和 UI 自动测试；
   - 人工交互时延、held Preview 和物理设备场景验收。

   UI 必须调用既有 Application/Domain/Compiler/Playback/Persistence 服务，不得复制业务语义。

2. 发布日外部门：

   - 按实际发布日 BASS 官方条款复核免费资格；
   - 纳入供应商原始许可文本和现有 notices；
   - 验收实际最终发行包；
   - 仓库仍不得提交 BASS DLL。

3. 持续加固但不是当前缺口：

   - 更大固定种子随机 Project；
   - 更多 SF2、设备、采样率、callback block、磁盘和硬件矩阵；
   - WPF 端到端/UI 性能测试。

================================================================
十一、关键代码与文档定位
================================================================

主要 solutions：

- src/midora-common/midora-common.slnx
- src/midora-core/midora-core.slnx
- src/midora-midi/midora-midi.slnx
- src/midora-native-interops/midora-native-interops.slnx
- src/midora-audio-device/midora-audio-device.slnx
- src/midora-audio/midora-audio.slnx

关键代码：

- Domain：src/midora-core/Midora.Domain/
- Application：src/midora-core/Midora.Application/
- Compiler：src/midora-core/Midora.Compiler/
- Mapping ABI v2：src/midora-core/Midora.Mapping.Contract.V2/
- Persistence：src/midora-core/Midora.Persistence/
- MIDI Export：src/midora-core/Midora.MidiExport/
- Playback：src/midora-core/Midora.Playback/
- Playback/BASS-WASAPI adapter：src/midora-core/Midora.Playback.BassWasapi/
- Audio Render：src/midora-core/Midora.AudioRender/
- MIDI primitives：src/midora-midi/Midora.Midi/
- Audio abstractions/cache：src/midora-audio/Midora.Audio/
- BASS renderer/worker client：src/midora-audio/Midora.Audio.Bass/
- Native AOT Worker：src/midora-audio/Midora.Audio.Bass.Worker/
- Audio device abstractions/cache I/O：src/midora-audio-device/Midora.AudioDevice/
- WASAPI：src/midora-audio-device/Midora.AudioDevice.BassWasapi/
- WAVE：src/midora-audio-device/Midora.AudioDevice.Wave/
- Native interop：src/midora-native-interops/
- 固定 native baseline：src/midora-audio/bass-native-baseline.win-x64.json
- 全量发布门：Test-NonUIRelease.ps1
- 精确测试基线：misc/Midora-Non-UI-Test-Baseline.json

关键 Requirement Trace：

- misc/Midora-Stable-Id-Long-Refactor-Requirement-Trace.md
- misc/Midora-Time-Signature-TPQ-Compatibility-Requirement-Trace.md
- misc/Midora-Held-Preview-Causal-Gate-Requirement-Trace.md
- misc/Midora-Segment-Compilation-and-Audio-Cache-Design-Discussion-2026-08-08.md
- misc/Midora-Segment-Unit-Audio-Cache-Requirement-Trace.md
- misc/Midora-Project-Clipboard-and-Batch-Editing-Requirement-Trace.md
- misc/Midora-Manual-Audio-Blocking-Fixes-Requirement-Trace.md

================================================================
十二、构建、测试和 Git 规则
================================================================

1. 仓库固定 .NET SDK 10.0.400；使用 locked restore。缺少固定 SDK/包时报告真实环境问题，不静默换版本。

2. 各 solution 共享跨目录 ProjectReference 和 obj/Release。构建必须串行，避免 CS2012 文件锁；不要并行 build 六个 solution。

3. 正式完整非 UI 门使用根目录 Test-NonUIRelease.ps1，并显式提供：

   - 固定 manifest 匹配的 BASS native directory；
   - 一个现存有效 SF2；
   - 新的 artifacts 输出目录。

   不得下载或采用 vendor current/latest 替代固定正式基线。

4. 修改 C# 后：

   - 先运行风险对应的定向测试；
   - 再运行相关 solution Release build；
   - 对改动文件运行 dotnet format；
   - 发布级变化运行完整零跳过门；
   - 报告每个项目实际测试计数，不只报告退出码。

5. 提交前：

   git status --short
   git diff --check
   git diff --cached --check

   检查 staged 文件，不得包含 .sf2、BASS DLL、.exe、.trx、.wav、.midora 或 artifacts。

6. 使用 Conventional Commits；完整、验证通过且范围清晰时提交当前实时分支并推送 upstream。推送后确认本地 HEAD 等于远端引用且工作树符合预期。

================================================================
十三、新聊天的首轮行为
================================================================

1. 如果用户只粘贴本交接提示、尚未给出具体新任务：

   - 只做上述只读接手检查；
   - 简洁报告已读取的规范/ADR、实时分支/HEAD/upstream/工作树；
   - 报告非 UI 已闭合、八组 SF2 门已通过；
   - 报告当前没有未回答的大决定；
   - 说明下一主要阶段是 WPF，但不要在用户指定任务前修改代码、SRS、Git 或外部系统；
   - 请用户给出下一项具体任务。

2. 如果用户同时要求开始 WPF：

   - 先读取 SRS 第 17、18、20 章及关联第 3、13、19 章；
   - 确认当前确实没有 WPF project/XAML；
   - 写 WPF composition requirement trace 和实施顺序；
   - 先建立只负责组合既有非 UI 服务的最小垂直切片；
   - 不重新发明 Project、编译、播放、Preview、持久化、缓存或任务语义；
   - 每个增量执行构建/测试并按用户授权提交推送。

3. 如果用户先回答 Q-NUI-029、031～033、043～048：

   - 先把答案原样记录到问题库；
   - 判断答案是否保持当前实现；
   - 保持则只更新状态/证据；修改则评估影响、更新 SRS/ADR/代码/测试；
   - 不重新询问已经回答的 Q-NUI-001～028、030、034～042。
~~~
