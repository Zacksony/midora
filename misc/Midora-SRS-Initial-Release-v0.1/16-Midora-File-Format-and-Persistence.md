# 第 16 章 .midora 文件格式与持久化

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 `.midora` Zip package、JSON/protobuf 分工、manifest/project/object 索引、严格 schema、资源保存、损坏隔离、打开与保存事务、Save Copy、迁移和确定性写出。

## 16.1 文件格式核心原则
### 16.1.1 Zip package
`.midora` 初版本质上是 Zip package。
用户可以将 `.midora` 改名为 `.zip` 后解包检查。这属于诊断便利，不表示 Midora 正式支持用户手工编辑包内容。
手工修改后的项目必须仍通过严格格式校验，否则可能无法打开。
Midora 打开文件时不只依赖后缀名：
```text
只要 Zip 包内 magic 正确，即使文件后缀为 .zip，也允许作为 Midora Project 打开。
```
新建项目首次保存默认后缀为：
```text
.midora
```
保存副本默认后缀为：
```text
.midora
```
普通保存使用当前项目路径，不强行修改后缀。
### 16.1.2 不支持手工扩展包内容
初版不支持用户手工往 `.midora` 包内添加自定义内容。
规则：
```text
用户手工添加的文件不属于支持用法。
当前软件不认识的文件不参与 Project 语义。
未被 project.json 纳入 Project 语义索引的文件不参与 Project。
保存时从内存 Project 重建项目包，因此这些文件不会被写入新包。
```
### 16.1.3 保存时从内存重建完整包
普通保存和保存副本均遵循：
```text
从当前内存 Project 重新序列化生成完整 .midora Zip package。
不回读原 .midora 包。
不复制原包中的旧对象文件。
不复制原包中的未知文件。
不保留原包中的多余文件。
```
保存成功后的新包只包含当前版本明确写出的 Project 语义文件与资源文件。
### 16.1.4 不保存派生结果
`.midora` 不保存以下派生结果：
```text
canonical compiled result
编译缓存
编译器 cache fingerprint
MIDI 导出产物
音频渲染产物
音频渲染任务状态 / 进度 / 历史
音频渲染最终样本和中间样本缓存
播放预渲染 buffer
BASSMIDI Stream 状态
Mapping Function Expression 委托 / 编译缓存
诊断结果
Undo / Redo 栈
```
---
## 16.2 包内固定结构
初版 `.midora` 包内固定使用以下结构：
```text
.midora  // Zip package
├─ manifest.json
├─ project.json
├─ metadata.json
├─ conductor-track.json
├─ settings/
│  ├─ project-settings.json
│  ├─ global-reset-defaults.json
│  ├─ global-event-scope-defaults.json
│  └─ project-presentation.json        // Format 3+
├─ event-instruments/
│  ├─ ei_<id>.pb
│  └─ ...
├─ logical-tracks/
│  ├─ lt_<id>.pb
│  └─ ...
├─ midi-channel-roots/
│  ├─ mcr_<id>.pb
│  └─ ...
├─ midi-tracks/
│  ├─ mt_<id>.pb
│  └─ ...
├─ midi-content/
│  ├─ mt_<id>.mpk
│  └─ ...
```
固定文件名：
```text
manifest.json
project.json
metadata.json
conductor-track.json
settings/project-settings.json
settings/global-reset-defaults.json
settings/global-event-scope-defaults.json
settings/project-presentation.json
```
固定目录名：
```text
settings/
event-instruments/
logical-tracks/
midi-channel-roots/
midi-tracks/
midi-content/
```
Event Instrument 对象文件路径模板：
```text
event-instruments/ei_<id>.pb
```
Logical Track 对象文件路径模板：
```text
logical-tracks/lt_<id>.pb
```
MIDI Channel Root 与 Pure MIDI Track 对象文件路径模板：
```text
midi-channel-roots/mcr_<id>.pb
midi-tracks/mt_<id>.pb
midi-content/mt_<id>.mpk
```
`.pb` 扩展名固定表示 protobuf 对象文件；`.mpk` 表示第 16.30 节定义的、单 Pure MIDI Track 一个文件的版本化 immutable source page pack。它不是 canonical、编译缓存或音频缓存。
初版允许未来通过新的 `fileFormatVersion` / `schemaVersion` 增加新的顶层目录或结构性文件类型。但旧版软件遇到过新的版本时应拒绝打开，而不是忽略。
---
## 16.3 JSON 与 protobuf 持久化策略
### 16.3.1 JSON 使用范围
以下内容初版使用 JSON：
```text
manifest.json
project.json
metadata.json
conductor-track.json
settings/*.json
```
理由：
```text
这些内容通常较轻。
可读性有利于诊断。
全局入口和设置文件需要更透明。
```
### 16.3.2 protobuf 使用范围
以下内容初版使用 protobuf 二进制文件：
```text
Event Instrument 定义
SubVoice 数据
Event Instrument 内事件与曲线
Logical Parameters
Logical Parameter Mapping
Mapping Function 单行表达式 / 定义入口
Envelope Presets
生命周期 / Overlap / Initial State Defaults
Logical Track
Segment
Logical Note
Logical Parameter Lane / Point / Curve
MIDI Channel Root
Pure MIDI Track
Midi Segment
Direct MIDI Note / Channel Event
Opaque imported event payload
裁剪窗口
大量事件数据
大量曲线数据
```
protobuf 是初版重数据的默认二进制序列化格式。

Pure MIDI Track 的身份、Segment 元数据和 content-pack 路径仍使用 protobuf；page directory、aggregate count、tick / Stable ID bounds 与 Direct Note/Event/opaque payload 的大规模记录体固定放入 `midi-content/mt_<id>.mpk`。该例外用于流式打开、范围查询和有界内存，不能把 page pack 当作派生缓存或允许其脱离 Track protobuf 独立存在。
初版 protobuf 兼容基线固定为：
```text
protobuf Edition 2024
Google.Protobuf 3.35.1
Grpc.Tools 2.83.0
```
`.proto` 源文件和对应 descriptor SHA-256 基线必须纳入版本控制；生成的 C# 只进入 `obj`，不得作为手工维护源码提交。升级 protobuf runtime、代码生成器或 descriptor 基线前必须完成显式兼容性评审、descriptor diff、golden byte 回归和旧文件重开测试。
如果未来因兼容性、性能或迁移需要替换具体二进制编码，必须通过新的文件格式版本和迁移策略引入。
### 16.3.3 JSON schema 与代码生成
初版结构性 JSON 固定使用 JSON Schema Draft 2020-12。实现使用内部版本化 JSON DTO 和 `System.Text.Json` source generation；不得把领域对象的当前属性布局直接当作文件 schema，也不得依赖运行时反射自动扩张已发布字段面。

每个已发布 JSON schema 必须作为独立版本化文件纳入版本控制，并使用 `additionalProperties: false` 或等价严格约束。JSON 读取器必须拒绝重复属性、未知属性、注释、尾随逗号、UTF-8 BOM 和不符合当前 schema 的值。
### 16.3.4 Conductor Track 未来迁移口
初版 Conductor Track 使用：
```text
conductor-track.json
```
但如果未来 Conductor Track 数据量显著增大，允许通过新的文件格式版本迁移为二进制格式。
初版不得写死为“Conductor Track 永远 JSON”。
---
## 16.4 manifest.json
### 16.4.1 定位
`manifest.json` 是 `.midora` 包入口文件。
它负责：
```text
识别这是 Midora Project package
声明文件格式版本
声明最低可读版本
声明 manifest 自身 schemaVersion
记录创建 / 最新保存软件版本
记录包内文件索引
记录结构性文件 schemaVersion
记录文件 kind
记录文件 SHA-256
记录资源文件完整性信息
支持打开前版本预检
支持文件一致性校验
```
`manifest.json` 不负责：
```text
决定 Event Instrument Definition / Usage / Logical Track / MIDI Root / Pure MIDI Track 是否属于 Project
保存 Event Instrument Definition 独立管理顺序与 Arrangement global Track order
保存 Track→Usage / Track→Root 引用
保存 Project 级 ID 生成状态
保存用户作品元数据
保存具体 Project 语义对象索引
```
这些属于 `project.json` 或其他对应文件。
### 16.4.2 magic
manifest 必须包含固定 magic：
```text
midora-project
```
缺失或不匹配时：
```text
打开失败。
```
magic 已足够表达 package kind，初版不额外设置独立 `packageKind`。
### 16.4.3 版本字段
manifest 顶层至少包含以下语义字段：
```text
fileFormatVersion
minimumReadableVersion
manifestSchemaVersion
createdWithSoftwareVersion
lastSavedWithSoftwareVersion
```
含义：
| 字段 | 含义 |
|---|---|
| `fileFormatVersion` | 当前 `.midora` 包采用的文件格式版本。 |
| `minimumReadableVersion` | 读取该文件所需的最低格式读取能力。 |
| `manifestSchemaVersion` | `manifest.json` 自身结构版本。 |
| `createdWithSoftwareVersion` | 创建该 Project 文件时的 Midora 软件版本。 |
| `lastSavedWithSoftwareVersion` | 最近一次保存该 Project 文件时的 Midora 软件版本。 |
`createdWithSoftwareVersion` 与 `lastSavedWithSoftwareVersion` 用于诊断、迁移提示和用户支持，不替代文件格式兼容判断。
初版中：
```text
minimumReadableVersion 通常可以等于 fileFormatVersion。
```
如果 `minimumReadableVersion` 高于当前软件支持版本：
```text
打开失败。
```
如果 `manifestSchemaVersion` 高于当前软件支持版本：
```text
打开失败。
```
### 16.4.4 文件索引
manifest 必须记录所有当前 Project 包内写出的核心文件、settings 文件、对象文件和 Pure MIDI content pack。
每个文件索引至少具有以下系统级信息：
```text
path
file kind
schemaVersion
SHA-256 hash
```
file kind 至少应能区分：
```text
core-json
settings-json
conductor-json
event-instrument-pb
event-instrument-usage-pb
logical-track-pb
midi-channel-root-pb
pure-midi-track-pb
pure-midi-content-pack
```
初版不需要定义 `unknown-preserved` / `future-extension` 等保留类型。
如果 manifest 中出现当前软件不认识的 file kind：
```text
该文件不参与 Project。
打开时可忽略并产生 Info。
保存时不会写入新包。
```
### 16.4.5 manifest 不记录的内容
manifest 不记录：
```text
Zip entry 压缩方式
Zip entry 压缩等级
Zip entry 压缩后大小
Zip entry 未压缩大小
Zip entry 时间戳
Zip comment
```
压缩方式、大小等属于 Zip 容器元数据，不以 manifest 为唯一真相。
### 16.4.6 hash
manifest 对以下文件记录 SHA-256：
```text
核心 JSON 文件
settings JSON 文件
conductor-track.json
Event Instrument .pb 文件
Logical Track .pb 文件
内嵌资源文件，例如 SF2
```
SHA-256 计算对象为：
```text
包内文件未压缩原始内容
```
manifest 自身不通过额外 manifest hash 校验。manifest 自身依赖 Zip CRC、JSON 解析、magic、schemaVersion 和字段校验。
保存时必须重新计算所有写出文件的 hash 并写入 manifest。
---
## 16.5 project.json
### 16.5.1 定位
`project.json` 是 Project 语义入口。
它负责保存：
```text
Project 顶层结构关系
Project 级 ID 生成状态
Arrangement Track tagged union 的全局混排顺序
Event Instrument Definition 独立 Index 与管理顺序
Event Instrument ID 到文件路径映射
Event Instrument 名称快照
Event Instrument Usage ID 到文件路径映射及其 Definition ID
Logical Track ID 到文件路径映射
Logical Track 名称快照
MIDI Channel Root 索引、文件路径与名称快照；Root 顺序由最早成员 Track 的全局位置派生
Pure MIDI Track 集合索引、文件路径与名称快照
settings 文件引用
metadata 文件引用
conductor-track 文件引用
```
`project.json` 是决定哪些 Event Instrument Definition / Usage / Logical Track / MIDI Channel Root / Pure MIDI Track 属于当前 Project，以及 Definition order 与 global Arrangement Track order 的唯一顶层权威索引。Logical Track protobuf 的 Usage ID、Usage protobuf 的 Definition ID 与 Pure MIDI Track protobuf 的 Root ID 必须与索引完全一致；Definition、Usage 与 Root 文件不得保存第二套 child order。
`project.json` 不得保存 Playback、MIDI Export defaults 或 Audio Render defaults 的引用；这些对象不属于当前 Project 模型。
### 16.5.2 Project ID
初版不引入独立稳定 Project ID。
因此初版不定义：
```text
Project ID
projectFileId
保存副本时 Project ID 是否保留
```
但 Project 内对象仍然必须拥有稳定 ID。
### 16.5.3 Project 级 ID 生成状态
Project 级 ID 生成状态必须保存于：
```text
project.json 的 nextStableId 字段
```
`nextStableId` 使用 16.13.2 规定的 canonical 十进制 JSON integer。
初版采用系统级原则：
```text
持久化单调递增计数器
不补缺
不复用
Project 内所有稳定 ID 全局唯一
```
打开会话内的分配高水位不因 Undo、失败创建或 Redo 分支丢弃而回退；Redo 必须恢复原 ID。仅由已撤销瞬态分配形成的空洞不单独维持 Modified，但在任何后续保存中仍写出当前高水位。关闭 otherwise-clean 会话后，未持久化、无存活对象且无存活 History 引用的瞬态 ID 可以随会话丢弃。
不采用：
```text
打开时扫描最大 ID 后继续
每次打开随机初始化 ID 生成器
删除对象后复用 ID
同类型内唯一但跨类型可重复
```
### 16.5.4 名称快照
`project.json` 保存 Event Instrument Definition / Logical Track / MIDI Channel Root / Pure MIDI Track 的最小显示名快照。Usage 没有用户名称，只保存 stable ID、Definition ID 与对象路径。
用途：
```text
索引显示
损坏占位显示
诊断定位
```
真实名称权威来源仍是对象 `.pb` 内部字段。
如果对象正常读取，但对象内部名称与 `project.json` 名称快照不一致：
```text
以对象内部名称为准。
产生一致性警告。
保存时更新 project.json 快照。
```
---
## 16.6 metadata.json
### 16.6.1 定位
`metadata.json` 只保存用户作品元数据。
初版包含：
```text
项目名称
用户可见项目版本
项目作者或团队
Remix 原曲 / 原曲作者或团队
版权信息
创建时间
修改时间
工程总耗时
```
### 16.6.2 不保存内容
`metadata.json` 不保存：
```text
fileFormatVersion
minimumReadableVersion
createdWithSoftwareVersion
lastSavedWithSoftwareVersion
对象索引
软件运行期状态
编译派生的 Notes / Events 内容统计
```
用户可见项目版本与 `fileFormatVersion` 完全无关。
用户可见项目版本是自由文本。
### 16.6.3 保存副本时 metadata 行为
保存副本不是创建新 Project。
因此副本文件中：
```text
创建时间保持原 Project 创建时间。
工程总耗时使用当前内存 Project 快照，不因保存副本额外累计。
修改时间写为保存副本时间。
```
但当前打开的内存 Project：
```text
不因保存副本而更新修改时间。
不因保存副本而更新 lastSavedWithSoftwareVersion。
不因保存副本而改变修改状态。
```
普通保存时：
```text
更新 metadata 修改时间。
写入当前内存 Project 已累计的工程总耗时。
更新 manifest 中 lastSavedWithSoftwareVersion。
不更新 createdWithSoftwareVersion。
```
### 16.6.4 metadata.json v1 字段
初版 `metadata.json` schema v1 固定保存：
```text
schemaVersion = 1
projectName
projectVersion
authorOrTeam
originalWork
copyright
createdAtUtc
modifiedAtUtc
totalEditingTimeMilliseconds
```
上述字段全部必须存在；允许为空的用户文本仍写为空字符串，不使用 `null` 或缺字段替代。时间和累计值服从第 16.13.9 节；`modifiedAtUtc` 不得早于 `createdAtUtc`。未知字段、重复字段、非法文本、非 canonical 时间或负累计值使现有 `metadata.json` 无效，并按第 16.18.3 节导致打开失败。

保存事务获取 metadata 快照前，必须先把当前活动打开会话截至快照瞬间的单调 elapsed time 合并进 `totalEditingTimeMilliseconds`。读取累计值、保存、Save Copy、播放、Buffering、导出和渲染都不建立独立计时器，也不得重复累计同一时间区间。
---
## 16.7 settings 文件
### 16.7.1 拆分
当前开发格式只包含：
```text
settings/project-settings.json
settings/global-reset-defaults.json
settings/global-event-scope-defaults.json
```
以上文件都必须在有效包结构中存在。既有文件缺失或损坏时：
```text
Project 可打开。
对应设置回退当前软件版本定义的默认值。
产生错误诊断。
Project 标记为已修改。
保存时写出完整 settings 文件。
```
冻结 Format 1 不包含 `settings/export-settings.json`、`settings/playback-settings.json` 或 `settings/audio-render-settings.json`；Format 2 继续不包含这些模型。Format 1 冻结前的旧开发包不属于兼容基线；冻结后的 Format 1 不得通过隐藏兼容字段重新建立这些 Project 模型。

### 16.7.2 project-settings.json
保存 TPQ 与其他正式 Project 级、非 metadata、非 Reset 的系统设置。TPQ 是 Project 语义，不是文件格式语义；Format 1 固定只接受整数 `1..32767`。每个 Time Signature 仍必须满足 `4 × ticksPerQuarterNote % denominator == 0`，读取和保存均须校验。

### 16.7.3 global-reset-defaults.json
保存 Project 级 CC、Pitch Bend、RPN / NRPN、Program / Bank 等正式 Reset 默认值。

### 16.7.4 global-event-scope-defaults.json
保存不可编辑的版本化空 marker，只包含严格 schema 所需版本字段。未来增加字段必须发布新 schema 并定义迁移。

### 16.7.5 project-presentation.json（Format 3+）

`settings/project-presentation.json` 是 Format 3 新增的唯一 Project presentation entry。它不是 Project Source Data，不进入 Domain、编译、canonical fingerprint、音频缓存或 Undo/Redo；其 revision/save baseline 独立于音乐 Project Modified。

冻结的 v1 schema 保存：

```text
schemaVersion = 1
allTracksMode = raw | compiled
trackOnionPresets[] = targetTrackId + enabled + opacity + ordered sourceTrackIds
subVoiceOnionPresets[] = eventInstrumentId + targetSubVoiceId + enabled + opacity + ordered sourceSubVoiceIds
```

当前 writer 使用独立 presentation schema 3：保留 schema 2 的 Onion/All-Tracks 字段，并增加严格的 `workspaceState` 分区。该分区只包含 B1 已批准的 Track profile、Segment/SubVoice 局部视图与 Lane 记忆，以及 Track/Usage/Root 的 Mute/Solo 展示状态；Tab 顺序、活动页、子页和其他 B3 导航状态不写入。schema 2 reader 仍可读并以空 workspace 作为默认；schema 1 reader/DTO/schema 保留并显式转换为 custom，不将 sourceMode 反向写入旧 schema。

workspace 的稳定 ID 只作为 owner/reference 保存，绝不把 ViewModel、WPF 控件、选择、Undo、任务、位图、编译结果或缓存写入项目。当前 presentation JSON 总预算为 64 MiB，profiles/views/lanes 各最多 131,072 条，单 owner 的 Lane target 总数最多 65,536。writer 在预算内确定排序并输出，超出时必须保留音乐及仍可表达的 presentation section，并以 `MIDORA-PERSIST-PRESENTATION-SECTION-OMITTED` 明确记录被省略的完整 section，不得静默截断。

opacity 必须是有限 `0..1`。target 必须存在且唯一，source 必须存在、同类、唯一且不得等于 target。写出按 target stable ID 确定排序；source 顺序保存为当次正式 Arrangement / SubVoice 顺序过滤后的叠加顺序，而非另建用户可重排的顺序。运行时跟随正式轨道 / SubVoice 顺序，首项最底层。保存快照过滤已经删除的 target/source，不得为 presentation 引用复活 Project 对象。

该 entry 缺失、hash 不符、strict JSON/schema/根级引用验证失败、未知 presentation schema/模式或 manifest 与 payload schemaVersion 不一致时，只隔离 presentation、恢复 `allTracksMode=raw` 且 preset arrays 为空，并报告 `MIDORA-PERSIST-PRESENTATION-RECOVERED` Warning。schema 3 内部的 workspace section 读取和项目 owner 验证按 section 隔离：损坏 section 回退为空并报告 `MIDORA-PERSIST-PRESENTATION-SECTION-RECOVERED`，有效的 Onion/All-Tracks 或其他 workspace section 必须保留。恢复结果设置独立 presentation recovery-dirty，但打开不自动修复；只有用户显式 Save 才写回有效当前 presentation。音乐 Project source 仍可打开，`IsModified` 不因此成立。Mute/Solo 只属于 presentation dirty，不进入音乐 Modified、Undo、canonical、编译或导出。旧软件可读取音乐并丢弃 schema 3 的新增 workspace 字段；若旧软件随后保存，字段丢失是明确兼容边界。Format 1/2 没有该 entry，detached migration 使用同一默认 presentation。
## 16.8 conductor-track.json
### 16.8.1 内容
`conductor-track.json` 保存完整 Conductor Track 内容，包括：
```text
Tempo
Time Signature
Key Signature
Marker
Project End Marker
Conductor Track 内部事件稳定 ID
Conductor Track 事件顺序
```
Project End Marker 保存为 Conductor Track 中的特殊事件。
Project End Marker 可选。
### 16.8.2 事件稳定 ID
Conductor Track 中的可编辑事件必须拥有稳定 ID。
包括：
```text
Tempo
Time Signature
Key Signature
Marker
Project End Marker
```
稳定 ID 用于：
```text
撤销 / 重做
诊断定位
复制
排序稳定
打开后引用保持
```
### 16.8.3 缺失或损坏
如果 `conductor-track.json` 缺失或损坏：
```text
Project 可打开。
Conductor Track 回退为默认 Tempo 120 / Time Signature 4/4。
产生错误诊断。
Project 标记为已修改。
保存时写出回退后的 Conductor Track。
```
该回退可能改变项目语义。
这种风险由文件损坏状态本身导致，初版不尝试从损坏 JSON 中局部恢复。
损坏回退时：
```text
原事件无法反序列化。
不保留原事件 ID。
系统生成新的默认事件对象或使用新的内存默认对象。
```
### 16.8.4 语义错误
如果 Conductor Track 成功读取，但存在语义错误，例如：
```text
多个 Project End Marker
事件 tick < 0
Tempo BPM <= 0
非法 Time Signature
Time Signature Denominator 与 Project TPQ 不满足 `4 × TPQ % Denominator == 0`
非法 Key Signature
```
Project 可打开，但应产生错误诊断。
相关编译、播放、导出应禁止，直到用户修复。
这类问题不是文件结构损坏，而是 Project 语义错误。

Format 1 的 TPQ/Time Signature 整除约束是例外：它同时决定 `Bar:Beat:Tick` 能否按 v1 固定整数语义读取。若单个文件各自结构有效、但二者组合不满足 `4 × TPQ % Denominator == 0`，持久化读取必须拒绝该 Conductor 内容，并按第 16.18.5 节的 Conductor 损坏/缺失有界回退规则处理；不得以分数 tick、量化或静默改拍号继续。
---
## 16.9 Event Instrument protobuf 文件
### 16.9.1 文件粒度
初版采用：
```text
每个 Event Instrument 一个 .pb 文件。
```
不采用：
```text
整个 Event Instrument Library 一个 .pb
每个 SubVoice 一个 .pb
大 SubVoice 自动拆分
```
### 16.9.2 保存内容
Event Instrument `.pb` 保存该 Event Instrument 定义本体完整内容，包括：
```text
稳定 ID
对象类型
schemaVersion
名称
颜色
备注 / 描述
Root Note
Template Length
Pre-Roll Ticks
SubVoice 集合
SubVoice 顺序
SubVoice 内事件与曲线
Logical Parameters
Logical Parameter Mapping
Mapping Function 单行表达式 / 定义入口
Envelope Presets
Per-Note Instance Isolation
生命周期策略
Overlap 策略
Initial State Defaults
其他属于 Event Instrument 定义的内容
```
SubVoice 不拆独立文件。
Mapping Function 单行表达式 / 定义保存于对应 Event Instrument `.pb` 内。
每个 Mapping Function 定义必须保存 `abiVersion`、单行表达式文本和由正式分析器推导的 Context 字段集合。初版新建函数固定写 `abiVersion = 3`；未知 ABI 可以作为源数据打开和保留，但实际参与编译时必须明确失败。自由 C# ABI v1/v2 同样只可识别和保留，绝不执行或自动改写为 ABI v3。`System.Linq.Expressions` 委托、语法/绑定缓存及任何编译产物不得写入 `.midora`。
Logical Parameter Definition 保存于对应 Event Instrument `.pb` 内。
### 16.9.3 Event Instrument Definition 与 Usage Index
Event Instrument 的集合级信息保存于 `project.json`，包括：
```text
Definition 管理栏中的独立顺序
对象 ID 到文件路径映射
损坏占位需要的名称快照
```
Event Instrument protobuf 不保存 Usage 或 Logical Track 引用。每个 Event Instrument Usage 使用独立 protobuf，保存 stable ID 与唯一 Definition ID；Logical Track protobuf 保存可空 Usage ID。Definition 可以没有 Usage；Usage 必须至少被一个 Logical Track 引用，且删除最后一个成员时与 Usage 原子删除。初版不保存 Event Instrument Folder、Unfiled 或独立 Library Workspace 状态。

Event Instrument 颜色属于 Event Instrument 定义级元数据，保存于对应 `.pb` 内。
### 16.9.4 删除后保存
用户删除 Event Instrument 后保存：
```text
新包不再写出该 Event Instrument .pb。
不保留孤立文件。
不写删除标记。
```
---
## 16.10 Logical Track protobuf 文件
### 16.10.1 文件粒度
初版采用：
```text
每个 Logical Track 一个 .pb 文件。
```
不采用：
```text
所有 Logical Tracks 一个 .pb
每个 Segment 一个 .pb
大 Segment 自动拆分
```
### 16.10.2 保存内容
Logical Track `.pb` 保存该 Track 本体完整内容，包括：
```text
稳定 ID
对象类型
schemaVersion
Track 名称
颜色覆盖
可空 Event Instrument Usage ID
Segment 集合
Segment 内 Logical Note
Logical Parameter Lane / Point / Curve
Track 内部对象 ID
Segment 裁剪窗口
其他属于 Logical Track / Segment 的内容
```
Segment 不拆独立文件。
Logical Parameter Lane 属于 Segment / Logical Track 编曲内容，保存于对应 Logical Track `.pb` 内。
### 16.10.3 Track 改绑 / 断裂参数 Lane
Logical Track 跨 Event Instrument Definition / Usage 改绑后，Track 中已有 Logical Parameter Lane 可能变为断裂 / 不适用。
保存时：
```text
仍保存这些 Logical Parameter Lane 数据。
不自动删除。
不自动按名称迁移。
不移到 project.json。
```
后续由 UI / 诊断处理。
### 16.10.4 Global Track order 与 Usage 引用
Logical Track 集合级信息保存于 `project.json`，包括：
```text
Arrangement global Track order 中的位置与 kind
对象 ID 到文件路径映射
损坏占位需要的名称快照
```
Logical Track 颜色覆盖保存于对应 `.pb` 内。
可空 Usage ID 保存于对应 Logical Track `.pb` 内，并必须引用 `project.json` 中存在的 Usage；未绑定 Track 必须没有 Segment 或其他音乐内容。Usage/Definition 不反向保存 Track IDs。
### 16.10.5 删除后保存
用户删除 Logical Track 后保存：
```text
新包不再写出该 Logical Track .pb。
不保留孤立文件。
不写删除标记。
```
---
## 16.11 对象路径、ID 与一致性
### 16.11.1 文件名使用稳定 ID
对象文件名必须基于稳定 ID，而不是用户可见名称。
原因：
```text
Event Instrument 名称可重命名。
Logical Track 名称允许重复。
用户可见名称可能包含非法路径字符。
用户可见名称可能存在大小写问题。
内部引用不得依赖名称。
```
### 16.11.2 类型前缀
对象文件名必须包含对象类型前缀：
```text
ei_<id>.pb
lt_<id>.pb
mcr_<id>.pb
mt_<id>.pb
```
其中 `<id>` 必须使用 16.13.2 规定的无符号、无前导零十进制 ASCII。
### 16.11.3 对象内部 ID 与类型
对象 `.pb` 内部必须保存：
```text
自身稳定 ID
对象类型
schemaVersion
```
打开时必须校验：
```text
文件名 ID
project.json 索引 ID
manifest 记录
对象内部 ID
对象内部类型
所在目录 / 路径
```
之间的一致性。
### 16.11.4 project.json 中路径
即使对象文件路径可由 ID 和类型确定性派生，`project.json` 仍保存对象文件路径，用于显式索引和一致性校验。
如果路径派生结果与 `project.json` 路径不一致：
```text
整个 Project 打开失败。
```
### 16.11.5 三方一致性
对于 Project 语义对象文件：
```text
manifest 路径
project.json 路径
实际 Zip entry
```
必须一致。
不一致时按本章打开失败 / 损坏占位规则处理。
---
## 16.12 schemaVersion 与严格 schema 策略
### 16.12.1 双层 schemaVersion
每个结构性文件都必须拥有 schemaVersion。
结构性文件包括：
```text
project.json
metadata.json
conductor-track.json
settings/*.json
event-instruments/*.pb
logical-tracks/*.pb
midi-channel-roots/*.pb
midi-tracks/*.pb
未来新增结构性文件
```
schemaVersion 必须同时保存于：
```text
manifest.json 文件索引
结构性文件自身内部
```
用途区别：
```text
manifest 中的 schemaVersion：打开前预检与兼容性判断入口。
文件内部 schemaVersion：文件自描述与一致性校验依据。
```
打开文件后，必须校验文件内部 schemaVersion 与 manifest 记录完全一致。
不一致时：
```text
整个 Project 打开失败。
```
### 16.12.2 schemaVersion 与 fileFormatVersion
`fileFormatVersion` 管理包结构。
`schemaVersion` 管理具体结构性文件内容。
二者允许独立演进。
### 16.12.3 高版本 schema
如果任何 Project 需要读取的结构性文件 schemaVersion 高于当前软件支持版本：
```text
整个 Project 打开失败。
```
理由：
```text
初版采用严格 schema。
旧版软件不支持新版结构。
不得尝试忽略未知字段读取。
```
如果 manifest 已显示 schemaVersion 过新，不需要继续校验 hash，直接拒绝打开。
### 16.12.4 低版本 schema
如果某个结构性文件 schemaVersion 低于当前软件支持版本：
```text
使用迁移流程迁移到当前内存结构。
保存时写为当前 schema。
```
### 16.12.5 未知字段
结构性 JSON / protobuf 文件中出现当前 schema 不认识的字段：
```text
打开失败。
```
不采用：
```text
忽略未知字段
尽量保留未知字段
保存时原样写回未知字段
```
JSON 必须在反序列化前或反序列化过程中检查重复属性，并通过版本化 DTO 拒绝未知属性。protobuf 必须先以当前消息 descriptor 检查原始 wire 数据，再反序列化；任一未知 field tag、错误 wire type、非法 UTF-8、越界标量或畸形 wire 数据均导致对应结构性文件读取失败。不得依赖 protobuf runtime 默认保留或跳过 `UnknownFieldSet` 的行为。
### 16.12.6 schema 演进原则
已发布字段原则：
```text
不实际删除字段。
废弃字段应标记 deprecated。
protobuf 字段号永不复用。
protobuf 已删除字段的编号与名称必须 `reserved`；新增字段必须使用新的明确字段号。
JSON 已发布字段名不复用为不同语义。
已发布 enum 值不得改变语义。
```
开发阶段：
```text
正式 schemaVersion 不应因内部未冻结调整而频繁提升。
应在该格式阶段冻结后确定版本。
```
这要求项目数据结构设计保持严谨。
---
## 16.13 持久化数据类型规则
### 16.13.1 tick
tick 值持久化使用：
```text
64-bit signed integer
```
不得使用 floating point 保存 tick。
### 16.13.2 ID
Project 内稳定 ID 的语义核心固定为 signed 64-bit integer，合法范围统一为：
```text
1..9223372036854775807（long.MaxValue）
```
`0` 与负值非法；所有现存对象稳定 ID 还必须小于 `nextStableId`。

JSON 中的对象稳定 ID 与 `project.json.nextStableId` 必须使用十进制 JSON integer token。原始 UTF-8 token 必须匹配：
```text
[1-9][0-9]*
```
读取器必须拒绝字符串、小数、指数、正负号、前导零、超出 `long.MaxValue` 的 token 以及任何静默规范化。正式 .NET 读取器必须以 64-bit integer 精确处理；使用 JavaScript 等只能精确表示到 `2^53-1` 的第三方消费者必须自行使用任意精度整数解析。

对象文件名中的 `<id>` 使用同一数值的 invariant 十进制 ASCII：无正负号、无前导零，且必须落在相同合法范围。文件名、`project.json` 索引和对象内部 ID 必须数值一致。

Project 内稳定 ID 在 protobuf 中固定直接使用标量：
```proto
int64 <field_name> = <existing_outer_field_number>;
```
每个对象或引用保留其既有外层字段号；正值按 protobuf 标准 `int64` varint 编码。不得保留嵌套 `StableId` 消息，不得使用 `sint64`、`fixed64`、`bytes`、GUID 混合字节序或其他替代布局。读取器必须拒绝缺省得到的零、负值和超出合法语义范围的值。

该布局是首版冻结前对开发期 v1 的直接修订；旧 128-bit high/low 布局未发布，不提供迁移器或兼容读取分支。
### 16.13.3 enum
持久化 enum 必须使用稳定编号或稳定字符串。
JSON 中 enum 推荐保存为稳定字符串，例如：
```text
"embedded"
"external"
```
不得保存本地化 UI 显示文本。
### 16.13.4 Double
JSON Double 类型字段不允许：
```text
NaN
Infinity
-Infinity
非标准 JSON token
```
protobuf Double 类型字段也不允许持久化 NaN / Infinity。
保存前必须诊断失败或规范化。
### 16.13.5 路径
包内路径、相对资源路径在项目文件中保存为 UTF-8 相对路径字符串，并统一使用：
```text
/
```
不得使用 Windows `\`。保存和读取时保持原大小写与原 Unicode scalar 序列，不执行 Unicode normalization，不改写分隔符，也不静默修正路径。

路径必须包含 1–4096 个 Unicode scalar，并拒绝：
```text
空 segment
`.` 或 `..` segment
绝对路径
Windows 盘符路径
反斜杠
NUL 或控制字符
```
SoundFont 外部引用进一步限制为第 6.4.4 与 16.15 节的两种直接子路径；大小写回退、hash 不匹配处置与验证时机按这些章节执行。
### 16.13.6 字符串规范化
初版不强制 Unicode normalization。
用户可见字符串保持用户输入。
唯一性比较按对应章节规则处理，例如 Event Instrument 名称大小写不敏感并去除首尾空白。
### 16.13.7 文本长度
所有上限按 Unicode scalar 数量计算，不按 UTF-16 code unit 或 UTF-8 byte 数计算。初版固定上限为：

| 字段类别 | 最大 Unicode scalars |
|---|---:|
| 短名称、标签、用户可见版本、Marker 名称、名称快照 | 256 |
| 作者、版权及其他单行 metadata | 4,096 |
| 描述、备注 | 65,536 |
| 单个 Mapping Function 单行表达式 | 8,192 |
| 相对路径 | 4,096 |

超过上限必须拒绝，不得截断。短文本和单行 metadata 不允许控制字符；描述可包含 Tab、LF、CR，但不允许 NUL 或其他控制字符。

### 16.13.8 颜色
初版持久颜色固定为不透明 sRGB，不保存 alpha。protobuf 使用：

```proto
message RgbColor {
  uint32 red = 1;
  uint32 green = 2;
  uint32 blue = 3;
}
```

每个分量必须位于 `[0, 255]`。颜色若出现在 JSON 中，固定使用小写 `#rrggbb`；不得接受 shorthand、alpha、命名颜色或大写 canonical 输出。

### 16.13.9 时间与工程总耗时
`metadata.json` 的 `createdAtUtc` 与 `modifiedAtUtc` 固定使用 UTC、七位小数秒和 `Z` 后缀：

```text
yyyy-MM-ddTHH:mm:ss.fffffffZ
```

不得接受本地时间、时区偏移或不同小数位数作为 v1 canonical 表示。工程总耗时字段固定命名为 `totalEditingTimeMilliseconds`，使用非负 signed 64-bit integer；本节只固定存储表示，哪些运行阶段计入累计仍由独立工程总耗时决定固定。
---
## 16.14 JSON / protobuf 输出确定性
### 16.14.1 JSON
保存 JSON 时要求尽量确定性输出：
```text
稳定字段顺序
稳定缩进
稳定换行
UTF-8 无 BOM
标准 JSON
不允许注释
```
推荐格式：
```text
2 spaces
LF
```
具体字段顺序由实现层定义，但同一内容重复保存应尽量产生稳定输出。
### 16.14.2 protobuf
保存 protobuf 时要求 deterministic serialization，并使用当前已固定 runtime/codegen profile。protobuf 的 deterministic serialization 只保证同一 runtime/profile 下的稳定输出，不等同于跨实现、跨版本的 canonical encoding。
如果使用 map：
```text
不得让 map 顺序导致同内容多次保存输出不同。
```
并行序列化只影响执行计划，不得改变最终文件内容或 Zip entry 顺序。
每个已发布 protobuf schema 必须有 descriptor 基线和代表性 golden bytes；升级库、工具或生成 profile 时必须执行兼容性评审，不得仅因新版本仍声明 deterministic 就自动采用。
---
## 16.15 SoundFont 资源持久化
### 16.15.1 包内禁止项
SoundFont 已脱离 Project。`.midora` 不得包含 SF2 字节、Embedded/External 引用、本机路径、文件名、大小、内容 hash、列表顺序或 Enabled 状态；不得生成 `settings/soundfont-settings.json` 或 `resources/soundfonts/` entry。

### 16.15.2 打开与保存
Project 新建、打开、Save、Save Copy、自校验和确定性 Zip 写出均不得访问程序级 SF2 文件。程序级 SoundFont 列表变化不标记 Project Modified，不进入 Project Undo/Redo，也不改变相同 Project 源数据的 package bytes。

开发期格式破坏后不提供旧 Project SoundFont 字段、settings 或资源的兼容读取、迁移和双写；带旧字段的 source JSON 继续服从未知字段严格拒绝，旧未索引资源不提升为当前 Project 语义。
---
## 16.16 Zip 容器合法性
### 16.16.1 Zip64
`.midora` 允许使用 Zip64。
理由：长项目和大量事件数据仍可能超过传统 Zip 限制；SoundFont 不再是原因。
可能超过传统 Zip 限制。
### 16.16.2 根目录
`manifest.json` 必须位于 Zip 根目录。
不允许包内嵌套一层项目文件夹，例如：
```text
MyProject/manifest.json
```
这种结构打开失败。
### 16.16.3 路径规则
Zip entry 路径统一使用 `/`。
如果 Zip entry 使用 `\`：
```text
打开失败。
```
如果 Zip entry 路径包含：
```text
../
..\
绝对路径
Windows 盘符路径
```
打开失败。
### 16.16.4 重复路径
如果 Zip 中存在两个相同路径 entry：
```text
打开失败。
```
### 16.16.5 大小写冲突
包内路径大小写敏感，但初版不允许大小写近似冲突。
例如同时存在：
```text
project.json
Project.json
```
打开失败。
### 16.16.6 空目录 entry
Zip 中允许空目录 entry，但不参与 Project 语义。
保存时不写出空目录 entry。
### 16.16.7 非普通文件
如果 Zip 中存在符号链接、设备文件等非普通文件 entry：
```text
打开失败。
```
### 16.16.8 加密 Zip
初版不支持 Zip 加密或密码保护。
如果 `.midora` 是加密 Zip：
```text
打开失败。
```
### 16.16.9 Zip comment
初版不使用 Zip comment。
magic 不放 Zip comment，只放 `manifest.json`。
### 16.16.10 文件数量与大小
初版不设置：
```text
包内文件数量硬限制
包内单文件大小硬限制
解压总大小检测
解压膨胀比例检测
```
Midora `.midora` 是自身项目文件格式，不是通用安全沙箱文件格式。
初版不把抵御恶意构造 Zip bomb / 超大包攻击作为核心需求。
用户打开不可信来源 `.midora` 文件时，风险由文件来源本身带来。
---
## 16.17 打开流程
### 16.17.1 分阶段打开
打开 `.midora` 应按阶段处理：
```text
1. Zip 容器合法性检查
2. manifest.json 读取与版本预检
3. manifest 文件索引一致性检查
4. hash 校验
5. 核心 JSON / settings / conductor 读取
6. project.json 语义索引读取
7. Project 对象文件读取
8. 对象引用与一致性检查
9. 生成内存 Project
10. 生成打开诊断
```
打开失败时：
```text
不创建内存 Project。
不改变原 .midora 文件。
```
打开成功但存在诊断时：
```text
不改变原 .midora 文件。
```
### 16.17.2 文件占用
Project 成功打开并反序列化到内存后：
```text
不应长期占用原 .midora 文件。
打开完成后释放文件句柄。
```
Pure MIDI page pack 必须从 Zip entry 顺序流式复制到本次 Project session 私有的只读 backing file，再关闭原 `.midora`。不得为了满足本条而把完整 `.mpk` 解压到托管 byte array。session backing file 只承载已持久化 source bytes，Project 关闭时删除；它不属于 audio cache，也不改变 `.midora` 的唯一持久源身份。

session backing root 固定为 `<ProgramRoot>\.tmp\SessionContent`。当前版本每个 Project session 使用一个带版本 manifest 和独占活动锁的 `session-*` 直接子目录；正常关闭在释放 pack/page cache 后删除自身目录。主应用取得单实例所有权后，以及创建新 backing session 前，必须 best-effort 回收 manifest 可识别且未持有活动锁的异常退出残留。裸 GUID、未知目录、manifest 缺失/不匹配、活动目录、越界路径与 reparse-point 必须保留；不得探测或迁移旧 `%LOCALAPPDATA%\Midora\SessionContent`。单个目录无法读取或删除不得阻止打开/导入 Project。

打开 / 反序列化过程中可以短暂占用文件，以避免读到半修改状态。
打开后如果外部程序修改原 `.midora` 文件：
```text
初版不主动监控。
下次普通保存按当前内存 Project 覆盖目标路径。
```
### 16.17.3 多余文件
如果打开时发现 Zip 中存在未被 manifest 记录的额外文件：
```text
产生 Info，说明该文件不属于当前 Project，保存后会被移除。
不标记 Project 已修改。
不影响编译 / 播放 / 导出。
```
如果 manifest 记录了对象文件，但 `project.json` 未引用：
```text
产生 Info，说明该文件不属于当前 Project，保存后会被移除。
不标记 Project 已修改。
不影响编译 / 播放 / 导出。
```
保存后这些 Info 消失，因为新包不再包含这些文件。
### 16.17.4 打开非 Zip 或缺失 manifest
如果文件后缀为 `.midora`，但不是 Zip：
```text
打开失败，提示不是有效 Midora Zip package。
```
如果 Zip 可打开但缺少 `manifest.json`：
```text
打开失败，提示不是有效 Midora 项目包。
```
初版不扫描恢复、不创建默认 manifest。
---
## 16.18 打开失败与损坏处理
### 16.18.1 manifest 损坏
如果 `manifest.json` 缺失、无法解析、magic 不匹配、manifest schemaVersion 过新或包含未知字段：
```text
整个 Project 打开失败。
```
### 16.18.2 project.json 损坏
如果 `project.json` 缺失、损坏、hash 不匹配或无法解析：
```text
整个 Project 打开失败。
```
`project.json` 是 Project 语义入口，不进行扫描重建。
### 16.18.3 metadata.json 损坏或缺失
如果 `metadata.json` 缺失：
```text
Project 可打开。
Metadata 回退默认空值。
产生错误诊断。
Project 标记为已修改。
```
如果 `metadata.json` 损坏、hash 不匹配或无法解析：
```text
整个 Project 打开失败。
```
### 16.18.4 settings 损坏或缺失
settings JSON 缺失、损坏或 hash 不匹配：
```text
Project 可打开。
对应 settings 回退当前软件默认值。
产生错误诊断。
Project 标记为已修改。
保存时写出完整 settings。
```
### 16.18.5 conductor-track 损坏或缺失
`conductor-track.json` 缺失、损坏或无法解析：
```text
Project 可打开。
Conductor Track 回退默认 Tempo 120 / 4/4。
产生错误诊断。
Project 标记为已修改。
```
### 16.18.6 project.json 引用对象但 manifest 缺少记录
如果 `project.json` 引用某对象文件，但 manifest 没有该文件记录：
```text
整个 Project 打开失败。
```
原因：
```text
Project 语义索引与包文件索引不一致。
manifest 是结构性文件预检入口。
project.json 不应绕过 manifest。
```
### 16.18.7 project.json 引用对象且 manifest 记录存在但 Zip 文件缺失
如果 `project.json` 引用对象，manifest 也记录该文件，但 Zip entry 缺失：
```text
Project 可打开。
该对象形成损坏占位。
```
### 16.18.8 对象 hash 不匹配
如果 Project 语义对象 `.pb` hash 不匹配：
```text
Project 可打开。
该对象形成损坏占位。
```
hash 不匹配说明对象文件不可信，即使仍能反序列化也不得作为正常对象使用。
### 16.18.9 对象 schema 过新
如果 Project 语义对象的 schemaVersion 高于当前软件支持：
```text
整个 Project 打开失败。
```
### 16.18.10 manifest 与对象内部 schemaVersion 不一致
如果 manifest 记录的 schemaVersion 与对象文件内部 schemaVersion 不一致：
```text
整个 Project 打开失败。
```
### 16.18.11 对象 ID 不一致
如果对象文件可反序列化，但对象内部 ID 与 `project.json` 索引不一致：
```text
Project 可打开。
该对象形成损坏占位。
```
### 16.18.12 对象类型 / 路径错乱
如果对象文件路径、manifest file kind、`project.json` 类型、对象内部类型之间出现类型错乱：
```text
整个 Project 打开失败。
```
例如：
```text
project.json 认为是 Event Instrument，
但对象内部类型是 Logical Track。
```
---
## 16.19 损坏占位对象
### 16.19.1 Event Instrument 损坏占位
如果 Event Instrument `.pb` 无法作为正常对象加载，但 `project.json` 中存在索引：
```text
形成损坏占位对象。
保留 ID。
保留名称快照，如果 project.json 可提供。
保留文件路径。
保留错误信息。
不作为正常 Event Instrument。
不参与编译。
```
损坏占位保留在原排序位置，便于用户定位和删除。
### 16.19.2 Logical Track 损坏占位
如果 Logical Track `.pb` 无法作为正常对象加载，但 `project.json` 中存在索引：
```text
形成损坏占位 Track。
保留 ID。
保留名称快照。
保留文件路径。
保留错误信息。
不参与编译。
```
### 16.19.3 引用损坏 Definition / Usage
如果某个 Logical Track 的 Usage 或该 Usage 引用的 Definition 损坏：
```text
Track 保留 Usage ID；Usage 保留 Definition ID。
显示为绑定对象损坏 / 不可用。
该 Track 不参与编译。
产生错误诊断。
```
### 16.19.4 删除损坏占位
用户允许删除损坏 Event Instrument / Logical Track 占位。
删除规则：
```text
进入 Undo / Redo。
使 Project 进入已修改状态。
保存后从 project.json 索引移除。
保存后不写出原损坏 .pb 文件。
```
删除损坏 Event Instrument Definition 占位时，必须确认并原子删除引用它的 Usage/Usage 占位及其全部 Logical Track/Track 占位；这是损坏恢复操作，不得把可能含内容的 Track 降级为无 Usage 空壳。Undo 必须恢复全部原稳定 ID、引用与 global Track 位置。
删除损坏 Logical Track 占位后：
```text
从 Track 索引中移除。
保存后不再写出该损坏 Track 文件。
```
撤销删除损坏占位时：
```text
恢复损坏占位、ID、索引位置和错误信息。
仍不可用。
```
### 16.19.5 损坏占位与保存
只要 Project 中仍存在损坏 Event Instrument Definition / Usage / Logical Track / MIDI Channel Root / Pure MIDI Track 占位：
```text
禁止普通保存。
禁止保存副本。
```
损坏占位没有对应 `.pb` 写出格式。
---
## 16.20 诊断系统边界
### 16.20.1 打开诊断
打开诊断不保存进 Project。
诊断是本次打开 / 当前软件分析结果。
### 16.20.2 诊断来源分类
诊断应能区分：
```text
文件格式诊断
文件损坏诊断
版本不兼容诊断
项目语义诊断
资源缺失诊断
保存事务诊断
```
### 16.20.3 文件格式诊断与编译诊断
文件格式诊断不直接作为编译诊断。
但如果文件格式问题导致内存 Project 中对象缺失、对象损坏或对象不可用，则会间接影响编译。
### 16.20.4 源定位
打开失败或对象损坏诊断应尽量定位到：
```text
包内文件路径
对象 ID
对象名称快照
对象类型
```
示例：
```text
event-instruments/ei_x.pb
logical-tracks/lt_y.pb
project.json
settings/project-settings.json
```
### 16.20.5 保存诊断
保存前如果因以下原因禁止保存，应收集并显示保存诊断：
```text
损坏对象占位
Project 内部 ID 不唯一
Project 内存一致性错误
project.json 将引用不存在对象
对象序列化失败
保存前一致性检查失败
```
保存诊断不进入 Undo / Redo。
保存成功后：
```text
清除本次保存诊断。
仍然适用的项目诊断需要重新计算。
```
---
## 16.21 保存禁止条件
### 16.21.1 禁止保存
以下状态禁止普通保存和保存副本：
```text
存在损坏 Event Instrument 占位
存在损坏 Event Instrument Usage 占位
存在损坏 Logical Track 占位
存在损坏 MIDI Channel Root 占位
存在损坏 Pure MIDI Track 占位
Project 内部稳定 ID 不唯一
保存前一致性检查失败
对象序列化失败
project.json 将引用不存在对象
Project 内存结构无法生成合法包
```
### 16.21.2 不禁止保存
以下状态不禁止保存：
```text
settings 缺失 / 损坏并已回退默认值
conductor-track 缺失 / 损坏并已回退默认值
存在打开时 Info：保存后会移除多余文件
无内容 Logical Track 处于合法无 Usage 空壳状态
```
保存这些状态时，应写出当前内存 Project 表示。
---
## 16.22 普通保存事务
### 16.22.1 基本流程
普通保存必须采用安全保存事务。
总体流程：
```text
0. 将原项目文件复制一份，使用唯一名称作为本次保存事务备份。
1. 在目标 .midora 同目录创建隐藏临时文件夹。
2. 在临时文件夹中构建项目包内容，可并行序列化多个对象。
3. 将临时文件夹内容打包为临时 .midora / Zip 文件。
4. 重新打开并自校验临时 .midora。
5. 使用操作系统支持的安全替换 / 原子替换机制，将临时 .midora 替换目标项目文件。
6. 保存成功后清理备份文件、临时文件和临时目录。
```
### 16.22.2 临时位置
临时目录应创建在目标 `.midora` 文件同目录。
临时 `.midora` 文件也应创建在目标 `.midora` 文件同目录。
理由：
```text
更利于最终替换在同一磁盘卷内完成。
降低跨卷移动失败风险。
```
### 16.22.3 事务 ID
备份文件、临时 `.midora`、临时目录命名应带唯一事务 ID。
事务 ID 不写入 Project 内容。
只用于：
```text
临时文件命名
失败诊断提示
避免多个失败保存残留互相覆盖
```
### 16.22.4 自校验
生成临时 `.midora` 后、替换目标文件前，必须重新打开并校验临时文件。
至少校验：
```text
Zip 可打开
manifest 可解析
magic 正确
核心入口文件存在
schemaVersion 记录一致
对象索引与文件存在性一致
hash 一致
```
自校验失败时：
```text
保存失败。
不得替换目标文件。
```
### 16.22.5 失败处理
阶段 1 / 2 / 3 / 自校验阶段失败：
```text
尚未触碰目标项目文件。
清理备份文件、临时文件、临时目录。
报告保存失败。
Project 保持已修改状态。
```
最终替换阶段失败：
```text
删除临时目录。
保留备份文件。
保留已生成临时 .midora 文件。
告知用户错误原因、备份文件路径、临时文件路径。
提醒用户自行处理。
Project 保持已修改状态。
```
最终替换阶段失败后不自动尝试用备份文件恢复目标文件。
理由：
```text
这类失败常见原因可能是文件被占用、权限异常、磁盘状态异常、杀毒软件拦截等。
在该状态下继续自动写入目标文件可能再次失败，甚至扩大损坏风险。
```
### 16.22.6 清理失败
如果保存成功，但清理备份文件 / 临时文件 / 临时目录失败：
```text
保存仍视为成功。
Project 标记为未修改。
显示清理警告。
```
### 16.22.7 保存状态
普通保存失败：
```text
Project 仍保持已修改状态。
```
普通保存成功：
```text
Project 标记为未修改。
更新 metadata 修改时间。
更新 manifest lastSavedWithSoftwareVersion。
```
### 16.22.8 目标路径异常
如果保存时目标文件不存在：
```text
按原路径重新创建项目文件。
```
如果目标路径目录不存在或不可访问：
```text
保存失败，提示用户处理或保存副本。
```
如果目标文件被其他程序占用导致最终替换失败：
```text
按最终替换阶段失败规则处理。
```
### 16.22.9 只读文件
如果 `.midora` 文件位于只读位置或文件属性只读：
```text
允许打开并编辑内存 Project。
普通保存可能失败。
初版不在打开时额外提示只读。
保存失败时只显示错误，不额外提供保存副本入口。
```
用户可手动执行保存副本。
---
## 16.23 保存副本 / Save Copy As
### 16.23.1 不采用传统 Save As
初版不提供传统“另存为 / Save As”语义。
传统 Save As 通常意味着保存到新路径后，当前 Project 的工作路径切换到新文件。
Midora 初版不采用该语义。
### 16.23.2 保存副本语义
初版支持：
```text
保存副本 / Save Copy As
```
语义：
```text
将当前内存 Project 以完整 .midora 文件形式写出到用户指定的新路径。
不改变当前 Project 的原保存路径。
不改变当前打开 Project 与原文件之间的关系。
不把当前工作文件切换到副本路径。
不进入 Undo / Redo。
```
### 16.23.3 保存副本事务
保存副本使用与普通保存相同的安全写出流程：
```text
临时目录构建
临时 .midora 打包
自校验
最终写入目标副本路径
清理临时文件
```
如果目标副本文件不存在：
```text
无需创建原文件备份。
```
如果目标副本文件已存在：
```text
必须询问用户是否覆盖。
用户确认覆盖后，使用安全替换流程，并为目标文件创建临时备份。
```
### 16.23.4 保存副本与当前 Project 状态
保存副本成功后：
```text
当前 Project 路径不改变。
当前 Project 修改状态不改变。
当前内存 metadata 修改时间不改变。
当前内存 lastSavedWithSoftwareVersion 不改变。
```
副本文件中：
```text
metadata 修改时间写为保存副本时间。
manifest lastSavedWithSoftwareVersion 写为当前软件版本。
```
保存副本失败：
```text
不影响当前 Project 修改状态。
按与普通保存相同的阶段规则处理临时文件和备份文件。
```
### 16.23.5 旧版本迁移后的保存副本
旧版本项目打开后已在内存中迁移，但尚未普通保存时：
```text
允许保存副本。
副本使用当前格式。
不改变原路径。
```
Save Copy 始终写当前 Format 4；它不建立或切换 CurrentProjectPath，不清除 migration-dirty，也不替代第 16.24.2 节要求的旧来源原字节永久副本。目标若等于受保护旧来源路径，必须拒绝并要求使用普通 Save 的专用原路径升级流程。
---
## 16.24 版本迁移
### 16.24.1 打开旧版本项目
如果文件格式版本低于当前软件版本：
```text
打开时在内存中迁移。
不自动保存。
```
旧版本项目迁移成功后：
```text
提示用户该项目已在内存中迁移，保存后会变成当前格式。
Project 视为已修改 / 已迁移未保存状态。
```
Format 1 冻结前的开发格式曾破坏性删除 Playback、Export defaults、Audio Render defaults 并引入 Pure MIDI Track / SMF Import；这些未发布开发包不属于兼容基线，不提供自动迁移、兼容读取或兼容编译路径。

自第 16.31 节的 Format 1 冻结点起，上述开发期例外终止。未来任何不能由冻结 Format 1 表示的持久化变化必须提升 `fileFormatVersion`，发布独立 JSON schema / protobuf descriptor / content-pack contract，并建立显式旧格式 reader 与 detached migration；不得根据缺失字段猜测默认模型或修改 V1 codec 伪装成兼容升级。
### 16.24.2 保存旧版本项目
用户点击普通保存时，必须进入专用 `Upgrade Project In Place` 确认，而不是普通首次保存路径选择器。确认框在任何文件写入前冻结并显示来源绝对路径、来源 Format、目标 Format 4 与永久原字节副本最终路径。

确认后的固定事务顺序为：

```text
1. 复核打开时冻结的来源 identity（绝对路径、Format、长度、mtime、完整 SHA-256）并取得排他 source lease；
2. 从当前内存 Project + presentation snapshot 构建当前 Format 4 临时 package；
3. 使用正式 reader 严格重开、自校验并比较确定内容；
4. 在来源同目录创建或复用来源逐字节一致的可见永久副本；
5. 再确认预先显示的副本路径仍可用；
6. 以同卷原子替换把已验证当前 Format 4 package 发布到来源原路径；
7. 成功后才把原路径设为 CurrentProjectPath，清除迁移保护与 migration-dirty。
```

永久副本推荐名称为 `<ProjectStem> - Original Format <old> before Format 4.midora`，遵守 Windows 255 UTF-16 code-unit 文件名预算并按 text element 截断。撞名按稳定 `(2)`、`(3)` 后缀；候选内容与来源 identity 完全一致时可复用，不一致时绝不覆盖。确认后候选被不同内容抢占必须中止并要求重新确认，不得静默换成另一路径。

如果用户取消确认：
```text
不保存。
Project 仍保持已修改 / 已迁移未保存状态。
```
保存成功后：
```text
保存为当前最新格式。
清除需要迁移保存状态。
```
不支持保存回旧格式。

失败原子性：来源缺失/改变、临时包构建或自校验失败时不创建永久副本；永久副本复制、flush 或校验失败时来源不动；永久副本已安全存在但发布失败时保留该副本和旧来源，Project 仍 migration-dirty，重试可复用一致副本。原子发布成功但临时清理失败仍算保存成功，只报告 Warning。用户此前手工 Save Copy 不改变本流程。
### 16.24.3 迁移失败
如果旧版本项目在打开时迁移失败：
```text
打开失败。
原文件不修改。
不创建部分 Project。
不自动创建修复副本。
```
### 16.24.4 关闭迁移未保存项目
旧版本项目打开并完成内存迁移，但用户未保存就关闭：
```text
按已修改 Project 处理。
提示保存 / 不保存 / 取消。
```
---
## 16.25 Zip 写出策略
### 16.25.1 写出顺序
保存 `.midora` 时，Zip entry 写出顺序必须稳定。
建议顺序：
```text
manifest.json
project.json
metadata.json
conductor-track.json
settings/*.json
event-instruments/*.pb
logical-tracks/*.pb
midi-channel-roots/*.pb
midi-tracks/*.pb
midi-content/*.mpk
```
但由于 manifest 需要记录所有文件 hash，实际内容生成顺序为：
```text
先生成除 manifest 外的所有文件内容并计算 hash。
最后生成 manifest。
再按稳定 Zip entry 顺序打包。
```
Event Instrument / Logical Track / MIDI Channel Root / Pure MIDI Track 对象文件写出顺序：
```text
按 project.json 中各集合的显式顺序；Pure MIDI Track 按 Root 显式顺序，再按 Root 内 Track 显式顺序。
```
### 16.25.2 时间戳
Zip entry 时间戳应统一写固定值或规范化值，避免同内容多次保存产生无意义差异。
Project 修改时间属于 metadata。
Zip entry 时间戳只是容器元数据。
manifest 不记录 Zip entry 时间戳。
### 16.25.3 压缩
初版默认压缩包内所有文件，包括：
```text
JSON
protobuf
SF2
```
SF2 不应默认视为已压缩资源。大量 SF2 内部采样可能是原始 PCM，Zip 压缩可能获得显著体积收益。
压缩等级不作为用户设置，由实现选择合理默认值。
系统语义上不要求每个 entry 必须压缩；具体压缩方式属实现细节。
manifest 不记录压缩等级。
### 16.25.4 并行序列化
保存时允许并行序列化 Event Instrument / Logical Track / MIDI Channel Root / Pure MIDI Track 等对象到临时目录。
但并行只影响执行计划，不得影响：
```text
最终文件内容
Zip entry 顺序
hash 结果
对象排序
诊断结果
```
如果某个对象序列化失败：
```text
整个保存失败。
不写出 partial 项目文件。
```
### 16.25.5 保存前一致性检查
保存前必须执行 Project 内存一致性检查，例如：
```text
ID 全局唯一
必要顶层对象存在
对象引用合法
project.json 可生成合法索引
对象文件路径可确定
settings 可序列化
metadata 可序列化
conductor-track 可序列化
TPQ 与全部 Time Signature 分母满足开发期 v1 整除约束
```
一致性检查失败：
```text
禁止保存。
产生保存诊断。
不进入 Undo / Redo。
```
---
## 16.26 保存期间锁定
保存或保存副本期间：
```text
禁止编辑 Project。
禁止 Undo / Redo。
禁止播放 / 预览。
禁止并发保存。
禁止并发保存副本。
```
如果播放中触发保存：
```text
按 第 13 章《播放与预览》 规则先自动 Stop，完成清理后再保存。
保存完成后保持 Stopped。
不自动恢复播放。
```
保存期间禁止后台继续编辑，避免保存快照一致性复杂化。
---
## 16.27 关闭 Project 时的保存提示
### 16.27.1 未保存新项目
新建但从未保存过的 Project，关闭时如果已修改：
```text
提示保存 / 不保存 / 取消。
```
### 16.27.2 已保存项目
已有保存路径的 Project，关闭时如果已修改：
```text
提示保存 / 不保存 / 取消。
```
### 16.27.3 迁移或回退导致修改
以下状态按已修改 Project 处理：
```text
旧版本项目已内存迁移但未保存。
settings 缺失 / 损坏后回退默认值。
conductor-track 缺失 / 损坏后回退默认值。
删除损坏对象占位后未保存。
保存失败后 Project 仍已修改。
```
### 16.27.4 不视为修改
以下状态不视为 Project 已修改：
```text
仅程序级 SoundFont 路径缺失或加载失败。
仅存在多余文件保存后会移除 Info。
含损坏对象占位但用户未删除且没有其他编辑。
```
含损坏对象占位本身禁止保存，因此仅因该状态提示保存没有意义。
### 16.27.5 保存副本与关闭
如果当前 Project 已修改，用户保存副本成功后关闭：
```text
仍按已修改 Project 处理。
```
因为保存副本不改变当前 Project 保存状态。
保存副本失败不影响当前 Project 修改状态。
### 16.27.6 保存成功与关闭
普通保存成功后：
```text
Project 未修改。
关闭不再提示保存。
```
普通保存成功但清理临时文件失败：
```text
Project 仍视为未修改。
关闭不提示保存。
保留清理警告。
```
---
## 16.28 不保存内容清单
`.midora` 初版不保存：
```text
Undo / Redo 栈
Project 修改状态
诊断结果
诊断面板展开 / 过滤状态
当前选中对象
当前视图滚动 / 缩放
播放光标位置
最近播放位置
播放进度
Mute / Solo
播放设备选择
Render-Ahead Buffer
Device Buffer Request
设备实际采样率、实际 buffer 与 callback period
本机最近打开路径
本机最近导出路径
最近导出一次性参数
MIDI 导出产物
音频渲染产物
播放预渲染 buffer
canonical compiled result
编译缓存
编译器 fingerprint
Mapping Function Expression 编译产物
BASSMIDI Stream 状态
音频设备信息
窗口布局
```
如果未来需要恢复 UI 状态或本机偏好，应作为本机会话状态或用户偏好另行设计，不写入 Project 文件语义。
---
## 16.29 初版不支持内容
初版 `.midora` 文件格式 / 持久化系统不支持：
```text
自动保存
崩溃恢复文件
保存事务失败残留作为崩溃恢复机制
项目模板
跨项目导入 Event Instrument
单独导出 Event Instrument 文件
程序级全局 Event Instrument Library
传统 Save As
用户手工扩展包内容
插件 / modding 扩展文件
加密 Zip / 密码保护
读取未知字段并保留
保存未知文件
保存孤立对象文件
保存编译结果 / 播放缓存 / 导出产物
打开时自动编译 Mapping Function Expression
打开时将 SoundFont 加载到 BASSMIDI
```
打开项目只负责：
```text
读取并校验 .midora 包。
反序列化 Project 源数据。
生成内存 Project。
生成打开诊断。
```
Mapping Function Expression 验证与编译由后续诊断、编译、播放或导出流程触发。
SoundFont 实际加载由播放、预览或音频渲染触发。
打开时可以检查外部 SF2 文件存在性与 last known hash，但不加载到 BASSMIDI。
---

## 16.30 Pure MIDI 对象持久化

### 16.30.1 文件粒度

每个 MIDI Channel Root 使用一个 `midi-channel-roots/mcr_<id>.pb`。每个 Pure MIDI Track 使用一个小型 `midi-tracks/mt_<id>.pb` 和一个 `midi-content/mt_<id>.mpk`。Track protobuf 保存身份、父 Root、显示字段、Segment 元数据及 content-pack 路径；page-range descriptor 与 Direct MIDI Note/Event、raw Note message 和 opaque imported event 的 immutable source records 均由 page pack 保存。

不得按 Segment、page 或 event 建立独立 Zip entry；一个 Track 无论多少页都只有一个 `.mpk`，以控制本地/包内文件数量。无任何 source record 的 Track 仍写一个合法空 pack，使索引关系保持单一。

### 16.30.2 Root 文件

Root protobuf 至少保存：

```text
Stable ID / object kind / schemaVersion
Display Name
Routing Mode
Fixed Port / Channel when applicable
Channel Mode: Melodic | Percussion
不保存 child Track IDs 或 child order
```

`project.json` 保存 Arrangement global Track tagged union、Root path 与名称快照，不保存每个 Root 的 child Track 索引/顺序。Root 的正式相对顺序由最早成员 Track 的 global position 派生；Pure MIDI Track 的 parent Root ID 是唯一成员关系来源。Root 文件、Pure MIDI Track 的 Root ID 与 `project.json` 必须完全一致；空 Root、缺失/重复 Track、断裂 Root 引用或重复 Fixed Port.Channel 均按结构损坏处理，不能按名称、Port.Channel 或目录扫描修复。

### 16.30.3 Pure MIDI Track 文件

Pure MIDI Track protobuf 至少保存：

```text
Stable ID / object kind / schemaVersion
parent Root stable ID
Display Name / optional color
Midi Segment metadata collection
content-pack relative path
```

Pack format version、byte length、完整文件 SHA-256 不重复写入 Track protobuf：format version 位于 `.mpk` header；byte length 与完整 entry SHA-256 由 `manifest.json` 的严格文件清单冻结；per-Segment Note/Event/Opaque page ranges、aggregate counts 与 source bounds 位于 `.mpk` directory。三者必须交叉校验，不能以其中任一项覆盖另一个不一致项。

`.mpk` page record必须完整保存 Direct MIDI Note 的 NoteOn/NoteOff velocity 与 endpoint order、Direct MIDI Channel Event 的显式同 tick order、未配对 raw Note message，以及 opaque SysEx/Meta type/payload/tick/order。Source record page 按 kind 分流，并同时满足：

```text
maximum 65,536 records per page
maximum 4 MiB decoded page payload
per-page first/last tick and stable-ID bounds
per-page decoded length, stored length and SHA-256
deterministic internal compression
```

Format 1 pack 必须从相同正式 source records 派生并保存 NoteOn endpoint、NoteOff endpoint 与 Channel Event endpoint pages。Endpoint page 按正式 tick/order key 局部有序、每页最多 16,384 records，并保存足以做目录裁剪和 bounded k-way merge 的 tick bounds；NoteOn endpoint目录还必须保存页内最大Note end tick，使active-note恢复只解码`minimumStart < cursor < maximumEnd`的候选endpoint页而不读取普通Note页。原始 source page 的 65,536-record / 4 MiB 双重上限不因此改变。Reader 必须交叉验证 endpoint record 与所属 Segment/source record 的身份、范围、数量和 checksum；派生索引损坏时拒绝该 pack，不得退回每窗口全量扫描并继续正式播放。

带 endpoint index 的现行 pack version 是冻结 Format 1 的组成部分。冻结前旧 pack version 不属于兼容基线；冻结后的 reader 必须持续接受该版本。未来替换 pack wire 必须进入新 Project Format 与显式迁移，禁止在 Format 1 中静默重建后覆盖旧 Project。

Pack header/directory/footer、整数编码、compression profile、checksum coverage 和 entry order 必须版本化并有 golden bytes。完整 Track pack 的逻辑顺序由 Segment order、record kind 和各 page 内正式 record order定义；物理 page 边界不得成为 MIDI 语义边界。

源 MTrk chunk 布局、Running Status 是否被使用、原 delta-time 编码宽度和原始文件字节不持久化；这些不属于语义 round-trip 保证。

### 16.30.4 严格边界与损坏隔离

Track protobuf、manifest entry 与 pack directory 必须先完成 bounded validation，再打开或解码 record page。Direct/opaque payload 的事件类型、长度、MIDI data byte、tick、稳定 ID、page count、record count、stored/decoded bytes、offset arithmetic、checksum 和非重叠 extent 必须在分配 page buffer 前校验。未知 protobuf 字段仍按本章严格 schema 拒绝，不能借“opaque event”保存未知 Midora wire 字段。

打开成功后只保留 immutable pack descriptor、copy-on-write overlay 和默认最多 `64 MiB decoded bytes` 的共享 LRU page cache；禁止把所有 pages、所有 ID 或所有 records 建立第二份全量内存索引。Stable ID lookup 依靠有序 page ID bounds 与小型 overlay index。

Root 或 Pure MIDI Track 文件损坏时，可以按 Logical Track 的既有原则形成对应损坏占位并允许用户删除；只要任一此类占位仍存在就禁止覆盖保存，以免永久丢失无法读取的对象。删除 Root 占位必须同时显式处理所有引用其 ID 的正常/损坏 Track，并在 Undo 中恢复 global Track 位置；不得静默改挂到其他 Root。

### 16.30.5 不保存派生结果

以下内容不得写入 `.midora`：

```text
Auto Root 当前编译获配的 Port.Channel
Root 活动连通区间
Execution/SMF Track Projection
Root checkpoint / state hash
normalized MidiSegment fragment
Root PCM/cache pack
import preview/candidate and source-file absolute path
```

Fixed Root 的用户 Port.Channel、Root Channel Mode、global Arrangement Track order、Track EOT 所需 Segment 尾部范围和 opaque payload 是源数据，必须持久化。

### 16.30.6 保存与 copy-on-write overlay

编辑 loaded/imported Pure MIDI record 时，Project session 在 immutable base pages 上记录 replacement/new record 与 tombstone overlay；Undo/Redo 冻结 overlay generation，不复制 base pack。保存时按正式顺序流式合并 base pages 与 overlay，写入新的临时 `.mpk`，校验 Track protobuf、manifest hash、pack directory 与 page checksums 后再参与 §16.22 的 package 原子发布。保存成功后允许把新 pack 提升为 session 的只读 base generation；失败继续保留旧 base 与 overlay，不得部分提交。

Track Copy/Duplicate 可以在内存中共享 immutable base extents，但保存输出必须为副本自己的确定 `.mpk` 和新稳定 ID 记录；不得持久化跨 Track 文件引用。
---

## 16.31 Format 1 正式兼容冻结

### 16.31.1 冻结点

自 2026-08-25、Midora 产品版本 `1.0.0-dev` 起，当前 `.midora` Format 1 是第一首完整 Midora 作品和 1.0.0 的正式兼容基线。此前未发布开发包仍不属于兼容范围；此后由 Midora 写出的有效 Format 1 必须被所有后续 1.x 新版本可靠读取。

### 16.31.2 不可原地改变的契约

冻结内容包括：

```text
package 顶层结构、magic、file kind 与 entry 路径
全部 Format 1 JSON schema、字段名称、类型、必填性与语义
全部 Format 1 protobuf descriptor、字段号、wire type 与语义
Pure MIDI content-pack header/directory/page/footer 与 endpoint index 语义
稳定 ID、时间、文本、排序、checksum、压缩与 deterministic write 规则
Format 1 的读取、损坏隔离、保存事务和版本预检行为
```

删除 protobuf 字段必须保留 field number/name；不得复用。修复 codec 时不得令已有效的冻结文件失效，或用新语义解释既有字段。

### 16.31.3 自动兼容门

Format 1 的 JSON schema set SHA-256、protobuf descriptor SHA-256、代表性 deterministic wire golden bytes、严格 unknown-field 拒绝、确定性 package round-trip、版本预检与事务故障注入必须持续作为自动测试门。发布候选还必须使用冻结点之后保存的真实完整作品执行：

```text
Open → Compile → Edit → Save Copy → Reopen
```

不得仅通过重写旧 hash/golden 基线令失败测试转绿；任何基线变化必须先证明它不属于 Format 1 原地变化，否则建立新版本。

### 16.31.4 新格式与迁移

不能由 Format 1 无损表达的新源数据或 wire 语义必须使用单调递增的新 `fileFormatVersion`。新软件按文件版本分派独立 reader，把旧 Project detached 迁移到当前 Domain，完整验证后一次提交；打开阶段不得覆盖源文件。用户保存时明确告知升级，并只通过安全保存事务写当前格式。初版不提供保存回旧格式。

当前严格 reader 不提供前向兼容，因此 Format 1 固定：

```text
minimumReadableVersion = fileFormatVersion = 1
```

只有另行定义并验证前向兼容 reader 后，未来格式才可声明更低的 `minimumReadableVersion`。

---

## 16.32 Format 2：Event Instrument Pre-Roll Ticks

### 16.32.1 提升原因与版本字段

Event Instrument Definition 的 `Pre-Roll Ticks` 是 Format 1 无法表达的新持久化语义，因此 Format 2 writer 在该版本冻结时必须采用以下版本字段；当前 writer 已由第 16.35 节提升为 Format 4，但 Format 2 reader/schema/descriptor/golden 不得改变：

```text
fileFormatVersion = 2
minimumReadableVersion = 2
manifestSchemaVersion = 2
```

产品 SemVer、Project 用户版本与 Mapping ABI 不因本次格式提升自动改变。Format 2 保持 Format 1 的 package 顶层路径、manifest/file kind、除 Event Instrument 外的组件 schema 以及 Pure MIDI content-pack wire；“保持”不代表旧软件可前向读取，Format 1 reader 仍必须因版本预检拒绝 Format 2。

### 16.32.2 Event Instrument protobuf v2

Format 2 的 Event Instrument component schemaVersion 提升为 v2。Edition 2024 wire 使用显式 presence，并以冻结 V1 Definition payload 加必填 Pre-Roll 字段组成 V2 wrapper：

```proto
message EventInstrumentV2 {
  uint32 schema_version = 1;
  string object_type = 2;
  midora.persistence.v1.EventInstrumentV1 definition = 3;
  int64 pre_roll_ticks = 4;
  reserved 5 to max;
}
```

四个字段在格式层都必须存在；`definition` 必须继续通过冻结 V1 Event Instrument codec 的严格验证，wrapper 不允许覆盖或重新解释其字段。`pre_roll_ticks` 字段号 4 永久保留，字段语义固定为：

```text
0 <= pre_roll_ticks <= template_length_ticks
0 表示不提前，且与 Format 1 行为等价
```

V2 reader 必须要求字段存在并执行严格有符号 Int64、范围和 unknown/duplicate-field 校验；不得把缺失字段默认为 0。V2 deterministic wire golden 必须至少覆盖 0、Template Length 上界、负值、超上界、缺失字段、未知字段及字段顺序无关的确定性写出。

### 16.32.3 Format 1 detached migration

新软件必须继续使用冻结 V1 reader 打开有效 Format 1，然后执行 detached migration：

```text
1. 使用 V1 schema/descriptor/codec 完整读取并验证源包。
2. 在未发布的独立候选 Project 中复制全部源数据。
3. 为每个 Event Instrument Definition 显式设置 Pre-Roll Ticks = 0。
4. 保留全部 stable ID、Definition/Usage/Root/Track membership、顺序、Segment 内容、Pure MIDI page pack 语义和用户 metadata。
5. 对完整候选执行当前 Domain 与 Format 2 语义验证；全部成功后才一次提交为活动 Project。
```

迁移不得覆盖、重写或在原 Format 1 包中追加字段。迁移成功后 Project 处于“已迁移未保存”状态；当前软件的普通 Save 与 Save Copy 写 Format 4，Format 2 是该迁移链中的冻结中间契约而非当前输出。迁移失败必须保留当前 Project 与源文件不变，不得提交部分 Definition 或把缺失字段解释为损坏 V2。

### 16.32.4 当前格式测试门

除第 16.31.3 节持续保留的 Format 1 自动兼容门外，Format 2 必须增加：

```text
Format 2 JSON schema set 与 protobuf v2 descriptor hash
Format 2 deterministic package/wire golden
Format 1 -> detached Format 2 migration golden
V1 Pre-Roll=0 行为与迁移后 Full/Incremental/canonical 结果等价
Format 2 Pre-Roll 非零 round-trip、Save Copy、损坏隔离与事务故障注入
旧软件对 Format 2 的版本预检拒绝
```

不得通过修改 Format 1 schema、descriptor、golden 或 reader 来完成这些测试。

---

## 16.33 Format 3：独立 Project Presentation

### 16.33.1 提升原因与版本字段

跨会话 Onion/All-Tracks presentation 无法由冻结 Format 2 表达，又不得混入 Project Source Data，因此在 Format 3 冻结时 writer 提升为以下值（当前 Format 4 见 §16.35）：

```text
fileFormatVersion = 3
minimumReadableVersion = 3
manifestSchemaVersion = 3
```

Format 3 新增一个 `project-presentation-json` file kind，且必须在 canonical path `settings/project-presentation.json` 恰好出现一次。其 manifest schemaVersion 是正整数，由独立 presentation reader 分派（当前读 1/2/3、写 3）；未知版本交由 presentation 恢复边界处理，不阻止音乐加载。除该 entry 与 manifest v3 外，Format 3 逐项复用 Format 2 的 Project source JSON schemas、Event Instrument protobuf v2、其他 protobuf v1 与 Pure MIDI content-pack wire；复用不允许修改那些既有契约。当前外层 writer 仍为 Project Format 4，schema 3 只是该独立 presentation entry 的版本升级。

### 16.33.2 Reader、writer 与损坏边界

本节固定 Format 3 的读取契约，当前 writer 见 §16.35。Reader 按 manifest version 明确分派 Format 1、2、3、4，不以 entry 缺失或未知字段猜测版本。Format 3 manifest 和 presentation JSON 服从 Draft 2020-12、source-generated DTO、strict duplicate/unknown-field 拒绝、canonical UTF-8/LF、确定排序与 SHA-256 索引。

音乐 source entry 的损坏规则继续按本章既有规则执行；presentation entry 单独按第 16.7.5 节隔离，禁止创建 Damaged Project Object 或让 `MidoraProjectOpenResult.IsModified` 成立。presentation recovery dirty 只表示下次普通 Save 应重写该 entry。

### 16.33.3 Format 1/2 detached migration

Format 1 先按第 16.32.3 节显式补 `Pre-Roll Ticks=0`；Format 2 保留其完整 source 语义。两者随后都附加默认空 presentation，验证完整当前 Domain 后一次提交。打开阶段不得写来源、创建永久副本或改变 Recent entry。

迁移后的普通 Save 使用第 16.24.2 节的专用原路径升级事务并写当前 Format 4；Save Copy 写 Format 4 但不改变来源保护。Format 1/2 reader/schema/descriptor/golden 必须持续通过，不得被 Format 3 codec 替代。

### 16.33.4 自动测试门

Format 3 必须持续覆盖：

```text
manifest-v3 与 project-presentation-v1/v2 schema set hash
空/非空 presentation deterministic bytes 与 package golden
strict unknown/duplicate/type/range/reference rejection
presentation v1 -> custom / v2 mode + custom sources / v3 workspace, manifest/payload version agreement
presentation missing/hash/corruption isolation without Project Modified
schema 3 分区损坏只回退对应 section，显式 Save 才修复 recovery-dirty entry
workspace 条目/字节预算超限时保留音乐和可表达 section，并报告省略 section
Mute/Solo 只影响 presentation baseline，不改变 canonical、MIDI/WAV 或 Project Modified
Format 1/2 detached migration + default presentation
Format 1/2 -> confirmed in-place Format 3 upgrade + exact-byte permanent backup
source identity mutation, backup collision/reuse, staging/self-validation/backup/publish fault injection
Format 3 Save/Save Copy/reopen and canonical equivalence
```

不得通过改写 Format 1/2 golden 或隐藏双写完成 Format 3 测试。

---

## 16.34 ProgramRoot Portable Storage

### 16.34.1 权威根与目录

`ProgramRoot` 固定为规范化的 executable base directory，不使用 process current directory。Midora 自建路径固定为：

```text
<ProgramRoot>\Data\Preferences
<ProgramRoot>\Data\Recent
<ProgramRoot>\Data\Catalogs
<ProgramRoot>\Data\Presets
<ProgramRoot>\Data\Diagnostics
<ProgramRoot>\.tmp\AudioCache
<ProgramRoot>\.tmp\SessionContent
<ProgramRoot>\.tmp\CompilerRuns
<ProgramRoot>\.tmp\AudioWorkerExchange
```

`.tmp` 只是可见目录名，不得设置 Windows Hidden 属性。Project、MIDI、SoundFont 与用户输出继续使用用户明确选择的外部绝对路径，不复制到 ProgramRoot。

### 16.34.2 启动能力门与禁止 fallback

主窗口创建前必须确认 ProgramRoot 是 ready local fixed drive 上的普通非 reparse-point 目录，并验证上述树可创建，以及写入、flush-to-disk、同卷原子 replace、独占 file lock 和删除均可用。任一失败必须以明确启动错误 fail closed；不得 fallback 到 `%LOCALAPPDATA%`、`%TEMP%`、当前工作目录、用户 profile 或另一卷。

当前版本不探测、不读取、不迁移、不删除旧 `%LOCALAPPDATA%\Midora`。两份 ProgramRoot 的 `Data`、`.tmp` 与单实例 scope 相互隔离；scope 至少包含当前 Windows 用户和规范化 ProgramRoot identity。

### 16.34.3 `.tmp` owner/lease 与回收

每个可回收工作目录必须是其固定 root 的直接子项，带当前版本 owner manifest 与由进程持续持有的独占 active lock。正常释放先关闭使用者，再释放 lock 并 best-effort 删除。启动及新 lease 前只允许删除：路径仍是直接子项、非 reparse point、manifest magic/name/purpose 全部匹配且 active lock 已可排他取得的目录。

manifest 缺失/损坏、裸 GUID、未知文件、活动锁、越界或 reparse point 一律保留。逐项回收失败不得阻止其他合法 session，但 root 能力探测本身失败必须阻止启动。CompilerRuns 与 AudioWorkerExchange 使用同一通用 lease；AudioCache 与 SessionContent 可保留其专用内容协议，但必须满足同等 owner/lock/path 边界。

## 16.35 Format 4：Instrument Change 编辑关联

依据 2026-09-14 已确认 R27。当前 writer 固定 `fileFormatVersion=minimumReadableVersion=manifestSchemaVersion=4`；旧 1/2/3 独立 reader、schema、descriptor、golden 均冻结并持续可读。新 writer 不更改其音乐字段布局，复用 EI protobuf v2、其他 source v1、Pure MIDI content pack 和 presentation schema 3（读 schema 1/2）。

新增必需 `settings/instrument-changes.pb`，manifest kind 为 `instrument-changes-pb`、schemaVersion=1。它是正式 source，不走 presentation 损坏回退：缺失、hash 不符、未知/重复字段、坏身份/owner/成员/target/Tick/重复占用必须拒绝该新 source 的加载，不默默降成无包装。文件使用 protobuf Edition 2024 显式 presence；根字段为版本 `1` 与 repeated 关联 `2`，关联字段依次为包装 ID `1`、owner ID `2`、directMidi `3`、Bank成员 `4`、可选Direct LSB成员 `5`、Program成员 `6`；其余字段号保留。

只保存身份及关联，不重复保存 Tick、MSB/LSB/Program、名称、音频状态。关联 ID 与其他 Project ID 共用唯一正 int64 分配，NextStableId 必须严格超过全部 ID。Direct owner 先、SubVoice 后，owner ID/包装 ID 确定序列化；该文件排序不是音乐排序。读取按 owner 复用分页快照、仅解析关联的成员，不发现未关联 raw 组合。

Format 1/2 按既有迁移补默认 Pre-Roll/presentation；Format 3 保留 presentation；三者新增空关联，不把导入/旧文件的 raw Bank-PC 自动分组。打开只 detached 迁移，原来源不写。旧格式普通 Save 仍需确认，先创建或复用来源逐字节一致的可见永久副本，之后以 Format 4 临时包自检和原子替换；名称中的 `before Format` 使用当前目标 4。Save Copy 写 4，不清除来源保护。软件 SemVer、Mapping ABI、Worker ABI、presentation schema 不因这个文件格式版本一并升级。

验证须覆盖两种 owner 的确定性重开、旧 Format 3 package byte golden、坏关联严格拒绝、旧格式安全备份/冲突/来源身份变化、取消和 staging/publish 故障，以及关联存在/不存在时音乐 canonical 等价。
