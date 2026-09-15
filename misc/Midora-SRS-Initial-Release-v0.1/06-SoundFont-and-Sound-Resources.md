# 第 6 章 SoundFont 与声音资源

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义程序级 SoundFont 列表、BASSMIDI 直接读取、无可用 SoundFont 状态，以及 SoundFont 与 Project、编译、缓存和消费者的边界。

## 6.1 系统归属

SoundFont 是 Application Preferences，而不是 Project Source Data。

`.midora` 不得保存：

```text
SF2/SFZ 文件字节
内嵌 SoundFont 资源
外部 SoundFont 引用
本机 SoundFont 绝对或相对路径
SoundFont 文件名、大小、SHA-256 或其他内容身份
SoundFont 列表顺序和启用状态
SoundFont 目标 Bank/Program 映射
```

Project Domain、Compiler 与 Canonical Compiled Result 不得包含、解析或验证 SoundFont。无 SoundFont 不影响 Project 新建、打开、保存、编译和 MIDI 导入/导出。

## 6.2 程序级有序列表

Application Preferences 必须保存一个有序 SoundFont 列表。每项只包含：

```text
本机 SF2 或 SFZ 绝对路径
Enabled / Disabled
可选的 Target Bank MSB、Target Bank LSB、Target Program 完整三元组
```

三元组字段只能整体存在或整体缺失，每项均为 `0..127`。SF2 允许缺失目标三元组；SFZ 必须提供目标三元组，Disabled SFZ 也不例外。目标三元组只控制 BASSMIDI SoundFont 路由，不修改 Project、canonical MIDI 事件或 MIDI 导出结果。

列表提供新增、删除、启用/禁用、上移和下移。不得保存重复的、按 Windows 路径语义指向同一位置的项。

列表从上到下是正式 BASSMIDI SoundFont 优先顺序。所有实时播放、全部预览和全部离线音频渲染使用同一次任务开始时冻结的相同启用列表和顺序；不得按 Project、Port、Track、Usage、Root 或 Event Instrument 选择不同列表。

Disabled 项保留在程序设置中，但不传给音频 Worker、不参与缓存身份，也不影响声音。

## 6.3 文件选择与校验边界

程序设置 Draft 和程序设置文件持久化只验证路径和映射字段的结构：路径必须是本机完全限定路径且扩展名为 `.sf2` 或 `.sfz`，目标三元组必须符合第 6.2 节。这一持久化阶段不得：

```text
复制 SoundFont 或 SFZ sample/include 依赖
读取完整 SoundFont
计算 SoundFont 内容 SHA-256
解析 SF2 内容、SFZ 文本、sample 路径或 #include
建立临时 SoundFont 或依赖副本
```

文件选择器可以要求选择时主文件存在，但这不构成持久身份或内容校验。若 SoundFont、目标映射或其他音频相关设置发生变化，完整 Application Preferences 成功持久化后必须立即进入运行时 Worker 重建与加载阶段；此阶段允许 BASS/BASSMIDI 直接打开原文件并由 BASSMIDI 自行解释 SFZ 引用，但 Midora 仍不得复制、预解析或完整读取资源来另做验证。路径、SFZ include/sample 或格式无法由 BASSMIDI 使用时，本次 `Saving Settings` 操作报告实际 BASSMIDI 初始化/预载失败；已经持久化的 Application Preferences 保持为用户提交值，Project 不变，后续重新 Apply 或播放可以显式重试。设置未发生音频相关变化时不得无理由重建 Worker。

Midora 不监控并自动接受运行中被替换的 SoundFont。SFZ sample/include 依赖完全属于用户维护的外部文件关系：Midora 不枚举、不快照、不校验、不监控，也不因依赖文件变化自动使缓存失效。修改 SoundFont 列表、顺序、Enabled 或目标映射只允许在播放停止且没有前台任务时提交；提交后必须销毁现有持久音频 Worker，并使依赖旧配置的 sample-domain 缓存代际失效。若用户在 Midora 外替换主文件或 SFZ 依赖，应自行承担错误或陈旧缓存风险，并显式重新应用设置、Reset Playback Engine 或清理缓存；Midora 不尝试推断该变化。

## 6.4 BASSMIDI 直接读取

音频 Worker 必须把每个启用项的原始绝对路径直接传给 `BASS_MIDI_FontInit`。SF2 固定使用 `BASS_MIDI_FONT_MMAP`；SFZ 不得使用该只适用于 SF2 的标志。不得为了播放、预览或音频渲染把主文件或 SFZ 依赖复制到 Project、缓存目录或临时目录。

持久实时 Worker 在音频相关 Application Preferences 成功持久化后立即重建，按列表顺序创建并持有全部 Font handle；同一 Worker 内的 Pitch Audition、Held Preview、普通 Preview 和主播放共用该冻结列表。无 Project 时也允许保留已加载 Worker，并由后续新建、打开或 MIDI 导入形成的 Project 会话接管；若接管前文件元数据已变化，则丢弃旧加载状态并按新身份重新加载。列表变化时销毁并重建 Worker，不在活动任务中热替换 handle。

任何新建 Project、打开 Project 或 `Open MIDI as New Project` 完成 Project 会话切换后，只要存在 Enabled SoundFont，就必须在对应前台任务结束前立即创建或接管持久 Worker并完成加载；不得把首次 Worker 创建推迟到用户按下 Play 或开始 Preview。为保持 Project 打开/创建的失败原子性，Project 会话先完成提交并释放旧会话 Worker，再预热新会话 Worker；预热失败不得撤销或伪装 Project 打开失败，而应保留已打开 Project、明确报告音频初始化错误并允许后续播放 Preparing 重试。`Reset Playback Engine` 完成清理后也必须立即重新探测/预热当前 Worker。

每个实际 Unit 的 1-channel BASSMIDI Stream 必须通过一次 `BASS_MIDI_StreamSetFonts` 和 `BASS_MIDI_FONTEX2` 接收完整有序 Font handle 配置。较早的列表项具有更高优先级；相同目标映射允许存在，并依靠该顺序确定声音。

未指定目标的 SF2 使用全部原始 presets/banks，保持原 Program/Bank MSB 并使用 Bank LSB 0。指定目标的 SF2 把 `source preset = target Program, source bank = target Bank MSB` 映射到完整目标三元组；若精确 source 不存在，继续使用 BASSMIDI 对 individual preset 的 first-preset fallback。SFZ 的 source 固定为其名义 `preset 0 / bank 0`，映射到必填目标三元组。

Preparing 从正式计划收集引用的 Bank/Program；未映射 SF2 按引用组合预载，映射 SF2 预载上述 source，SFZ 预载名义 source `0/0`。缺失 SF2 精确 preset 继续服从前述 fallback，不成为 Project/Compiler Error；SFZ sample/include 无法读取或格式不受 BASSMIDI/已加载 add-on 支持时属于音频任务 Error。

离线音频渲染同样直接读取冻结的原路径列表，不建立“冻结 SF2 文件副本”。任务期间文件被外部修改、删除或变得不可读造成的失败属于外部资源竞争；已有输出仍服从临时文件—校验—原子发布事务。

## 6.5 可用性

至少一个 Enabled 项时，播放、预览和音频渲染入口可以启动 Preparing。列表为空或所有项 Disabled 时：

```text
Project 编辑、保存、编译、MIDI 导入和 MIDI 导出可用
播放、Pitch Audition、Held Preview 和音频渲染不可用
状态栏显示 No Enabled SoundFonts
不产生编译诊断
```

入口可用不代表路径一定可加载；路径和 BASS 加载错误在音频 Preparing 阶段报告。

## 6.6 缓存身份

缓存不得重新读取完整 SF2 或计算 SF2 内容哈希。每次应用程序设置或建立音频任务快照时，可从以下冻结信息生成确定的运行时缓存指纹：

```text
启用项的有序规范绝对路径
每项是否指定目标映射及完整目标三元组
任务开始时可取得的文件长度
任务开始时可取得的 UTC 最后修改时间
```

该指纹可以使用 SHA-256 编码上述小型描述符，但它不是 SoundFont 内容校验，不得命名或展示为 SoundFont SHA-256。对于 SFZ，长度和修改时间只来自主 `.sfz` 文件，不包含 sample/include 依赖。任一启用主路径缺失或元数据不可读时不得建立可复用 PCM 命中；任务随后按正常 BASS Preparing 失败。

路径、顺序、Enabled、目标映射、主文件长度或主文件修改时间变化必须使相关 Unit PCM 和 playback-span 缓存失效。缓存命中不能证明 SoundFont 或 SFZ 依赖内容未变，不能替代 BASS 打开文件。

## 6.7 与 MIDI 和编译的关系

SoundFont 不改变：

```text
Event Instrument / Mapping / Lifecycle
Logical / Pure MIDI Track 与 Segment
Canonical MIDI 事件
Port / Channel / Unit 分配
MIDI 导入/导出
Program/Bank/CC/RPN/NRPN 语义
```

Midora 不假设 SoundFont 符合 GM，也不在 SoundFont 配置、Project 打开、编译、播放或渲染路径中隐式解析 preset 名称，更不根据当前列表验证 Program/Bank 是否存在。只有第 6.10 节规定的用户显式 `Scan Presets...` 可以读取 SF2 `phdr` 元数据并生成程序级名称快照。Program Change 与 Bank Select 始终是 Project MIDI 语义；列表或 Catalog 变化不得改写它们。

MIDI Export Readme 不记录程序级 SoundFont 路径、名称、列表或可用状态。MIDI 产物不依赖当前机器的 SoundFont 设置。

## 6.8 UI 与诊断

Application Preferences 的 SoundFonts 区域必须使用暗色基线样式展示有序列表。每项显示无文字标签的 Enabled 复选框、文件名、完整路径和目标映射控件；新增使用多选 SF2/SFZ 文件选择器，删除和排序只修改 Draft，用户按 Apply 后一次性提交。SF2 的 Target Mapping 可关闭；SFZ 必须保持开启。映射输入错误或 SFZ 缺少映射时禁止提交并给出字段级可理解错误。

Apply/Cancel 继续服从程序设置事务：Apply 成功后先保存完整列表；若 SoundFont、映射、实时音频或音频缓存配置变化，则显示不可取消的 `Saving Settings` 模态任务，销毁旧 Worker、重建音频服务并立即加载全部 Enabled SoundFont，成功后保留新 Worker 供后续音频操作直接复用。Cancel 不改变设置、Worker 或缓存。仅非音频设置变化时不得显示该加载任务。

以下属于任务级 Error，不进入 Compiler Diagnostics：

```text
没有 Enabled SoundFont
启用路径缺失或不可读
BASS_MIDI_FontInit / FontLoad / StreamSetFonts 失败
SFZ include/sample 缺失、不可读或格式不受支持
音频任务期间文件被外部替换或移除导致的读取失败
```

状态栏只展示程序级概括，例如 `2 SoundFonts Enabled` 或 `No Enabled SoundFonts`，不得把它描述为 Project SoundFont。

## 6.9 初版范围

初版只支持本机 `.sf2`、`.sfz` 文件和一个全局有序列表。不支持 DLS、VST、网络路径、Project 内嵌资源、Project 相对引用、每 Project 列表、每 Port/Track/Instrument 独立列表或运行中热切换。SFZ 仅采用 BASSMIDI 原生支持范围；Midora 不承诺实现独立 SFZ parser、依赖快照、依赖可移植性或附加 sample codec 自动加载。

SoundFont 授权仍由用户和分发者负责。Midora 仓库和 `.midora` 项目不包含用户 SF2/SFZ 或 sample 字节；这不构成对第三方 SoundFont 使用或分发权利的判断。

## 6.10 Instrument Catalogs

### 6.10.1 作用域与模型

Instrument Catalog 是程序级 Bank/Program 名称辅助数据，不是 Project 或音频资源验证。程序允许多个有序、可启用 Profile，来源分为：

```text
Built-in General MIDI
User Override
User Profile
Imported SF2 snapshot
```

每个条目以合法 MIDI `Bank MSB 0..127 + Bank LSB 0..127 + Program 0..127` 三元组为键。名称只影响 UI 展示；Project、canonical、MIDI Export 与 Audio Render 始终保留原数值。另一份 portable Midora 没有相同 Catalog 时，Project 仍必须完整打开并回退为数值名称。

Catalog 只写 `<ProgramRoot>\Data\Catalogs`，不进入 `.midora`、Project Undo/Redo、Project Modified、compiler fingerprint、音频缓存或 Worker 配置。

### 6.10.2 名称解析顺序

同一三元组的确定解析优先级固定为：

1. User Override；
2. 当前 Enabled SoundFont 列表正式顺序所绑定的 Imported SF2 Profile；
3. Enabled User Profile 的明确顺序；
4. 内置 General MIDI Profile；
5. `Bank MSB n / LSB n / Program n` 数值 fallback。

解析结果必须携带可见来源，例如 `Acoustic Grand Piano (General MIDI)`；UI 不展示任何内部 Profile ID 或 SoundFont Entry ID。Disabled SoundFont 对应 Profile 可浏览但不参与播放上下文名称解析；删除 SoundFont 后的 orphan Profile同理保留可浏览性。

### 6.10.3 SoundFont Entry 身份

每个程序级 SoundFont 项必须保存稳定、非空 GUID `SoundFontEntryId`，仅用于 Imported SF2 Profile 关联。重排、Enabled、路径或 target 修改必须保留该 ID；新加项分配新 ID。ID 不参与音频配置等价比较、Worker rebuild、SoundFont descriptor/cache identity，也不进入 Project 或导出。

读取缺少该字段的旧 Application Preferences 时必须确定性补齐并允许下一次 Apply 持久化；不得因此丢弃 SoundFont 配置。

### 6.10.4 显式 SF2 metadata scan

`Scan Presets...` 只允许由用户显式启动，并只读取 RIFF `sfbk` 中 `pdta/phdr` 的 38-byte records；sample、modulator 与其他 chunk 必须 seek 跳过，不得完整载入内存或调用 BASS。扫描必须校验 RIFF/chunk 边界、偶数字节 padding、唯一 `phdr`、record 整除与终止 `EOP`，并支持取消。

扫描快照保留 raw `ushort sf2Bank` 与 preset。raw bank `0..127` 默认预览为 `MSB=bank, LSB=0`；raw bank `128..65535` 必须在提交前显式映射，禁止截断、取模或自动解释。扫描失败只取消本次 Catalog 操作，不影响 Worker 继续使用同一文件；文件变化不自动 Rescan。

### 6.10.5 Store 与导入导出

Catalog store 和独立交换文件必须采用版本化、source-generated JSON，确定排序并明确限制文件字节、Profile、Bank 与 Program 数量。未知字段、重复 JSON property、重复 Profile ID 与 Profile 内重复 triple 必须拒绝。活动 store 损坏时保留原文件、回退到内置 GM 并显示 Application notice，禁止用默认值覆盖损坏字节。

编辑只作用于 draft；OK 通过临时文件、flush、校验和原子替换一次发布。导入只提供显式 `Replace Profile` 或 `Merge With Preview`；Merge 必须先显示新增、保持与覆盖摘要，确认前不得写活动状态。
