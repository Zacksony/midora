# 第 3 章 Project 模型与应用生命周期

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Project 的组成、身份、单实例应用边界、默认状态、元数据、保存与打开入口、修改状态、撤销/重做以及与编译和输出系统的总体关系。文件包内部事务由第 16 章进一步规定。

## 3.1 Project 的定义
Midora Project 是用户在 Midora 中进行完整工作的最高层单位。
一个 Project 不是普通文件夹，也不是单个乐器定义，而是包含项目级设置、唯一 Conductor、有序 Event Instrument Definitions、内部非空 Usage / MIDI Channel Roots、混排的 Logical / Pure MIDI Arrangement Tracks、两类 Segment、全局音乐事件和 Reset 默认值的完整上下文。Playback、SoundFont 和本次导出/渲染任务参数均不属于 Project。
编译、播放、预览、渲染和 MIDI 导出均以 Project 为上下文。
单独的 Event Instrument、Logical Track、MIDI Channel Root、Pure MIDI Track 或 Segment 不构成完整项目上下文。
---
## 3.2 Project 的必要组成
每个有效 Midora Project 必须包含以下顶层对象或集合：
| 顶层对象 / 集合 | 是否必须存在 | 是否可删除 | 说明 |
|---|---:|---:|---|
| Project Settings | 是 | 否 | 项目级设置入口 |
| Project Metadata | 是 | 否 | 项目元数据 |
| Conductor Track | 是 | 否 | 固定全局音乐事件轨道 |
| Arrangement Track Order | 是 | 否 | Logical / Pure MIDI Track 的唯一混排 tagged 顺序，可为空 |
| Event Instrument Definition Index | 是 | 否 | Definition 的独立有序索引，可为空，不拥有 Track |
| Event Instrument Usage Index | 是 | 否 | 无名称共享执行身份；每个 Usage 非空并引用一个 Definition，可为空 |
| MIDI Channel Root Index | 是 | 否 | Pure MIDI 共享执行身份；每个 Root 非空，可为空 |
| Global Reset Defaults | 是 | 否 | 项目级 Reset 默认值 |
| Global Event Scope Defaults | 是 | 否 | 初版不可编辑的版本化空 marker；事件作用域由各正式事件语义固定 |
说明：
- 顶层对象必须存在，不代表其中必须已有用户内容。
- Arrangement Track Order 与 Definition Index 都可以为空。
- 每个 Pure MIDI Track 必须且只能引用一个 Root。Logical Track 可以是无内容、无 Usage 的待指定空壳；一旦包含 Segment/Note/Parameter 内容，就必须且只能引用一个 Usage。
- Definition 顺序、Usage/Root membership 与混排 Track 顺序彼此独立；Definition/Root 不保存第二套 child order。
- Conductor Track 必须存在，且创建新项目时至少包含默认 Tempo 与默认拍号。
---
## 3.3 单项目 / 单实例规则
初版 Midora 只允许同时打开一个 Project。
初版不支持：
```text
多项目同时打开
单窗口多项目标签页
多窗口分别打开不同项目
跨项目实时引用
跨项目资源锁定
跨项目播放上下文
多个可独立启动并打开 Project 的 Midora 应用实例同时运行
```
整个系统只允许一个用户可启动、可显示 UI、可打开 Project 的 Midora 应用实例。
当用户第二次启动 Midora 时，不应创建新的独立进程实例，而应将启动请求转发给已有进程。具体转发机制、文件参数处理和操作系统级互斥实现属于实现设计细则。

允许主应用实例启动一个内部音频后端子进程。该子进程：
```text
不构成第二个 Midora 应用实例
不能独立打开或持有 Project 源数据
不提供用户界面
只能接收主应用提供的正式编译结果、音频配置和控制命令
```
初版正式音频后端固定使用内部音频子进程；主应用不得保留另一套进程内正式消费路径，也不向用户提供拓扑切换设置。子进程按第 13.30 节独立 Native AOT 发布，具体 CPU RID 由发布架构决定。
用户在已有 Project 打开的情况下打开另一个项目时，必须先处理当前项目：
```text
保存
不保存
取消打开操作
```
---
## 3.4 新项目默认状态
创建新 Project 时，系统必须生成结构完整的项目。
新项目默认包含：
```text
Project Settings
Project Metadata
Conductor Track
空 Arrangement Track Order
空 Event Instrument Definition / Usage / MIDI Channel Root Index
Global Reset Defaults
Global Event Scope Defaults
```
### 3.4.1 Conductor Track 默认内容
新项目必须有一个 Conductor Track。
Conductor Track 默认包含：
```text
Tempo = 120 BPM
Time Signature = 4/4
```
Conductor Track 应允许用户自由插入 Tempo、Time Signature 等全局音乐事件，以实现变速和变拍。
Conductor Track 具体事件编辑规则由 第 4 章《时间、Conductor Track 与全局音乐事件》 细化。
### 3.4.2 Arrangement 与 Definition 默认状态
新项目创建空 Arrangement Track Order、空 Event Instrument Definition Index、空 Usage Index 与空 MIDI Channel Root Index。初版不创建默认 Event Instrument Definition、Usage 或 Root。
### 3.4.3 Track 默认状态
新项目默认无 Logical Track 或 Pure MIDI Track。用户可以直接创建未指定 Usage 的空 Logical Track、选择或新建 Definition 后创建独立 Usage + Logical Track，或通过 MIDI Track 创建流程原子建立非空 Root + 首条 Pure MIDI Track；不存在仅创建空 Root 的结果。
初版不创建默认 Track，也不创建默认 Segment。
### 3.4.4 程序级音频资源边界
新 Project 不保存或创建 SoundFont 设置。是否可发声取决于当前 Application Preferences 的 Enabled SoundFont 列表；列表为空时仍允许 Project 创建、编辑、编译、保存和 MIDI 导出，但禁止播放、预览和音频渲染。
## 3.5 空项目规则
空项目不作为特殊项目类型处理。
空项目只是普通 Project 的一种内容状态。
初版中，空项目允许：
```text
保存
编译
```
空项目编译应产生结构上有效的编译结果。
具体编译结果是否仅包含 Conductor Track、是否插入全局初始化事件、是否生成空事件流，由 第 12 章《编译系统与 Canonical Compiled Result》 和输出章节细化。
空项目在无 SF2 状态下仍不能播放、预览或音频渲染。
---
## 3.6 Project Metadata 与系统级文件信息
### 3.6.1 用户 Project Metadata
初版用户 Project Metadata 应包含以下内容：
| 元数据 | 初版要求 | 编辑性 |
|---|---|---|
| 项目名称 | 必须支持 | 用户可编辑 |
| 项目版本 | 必须支持 | 用户可编辑，自由文本 |
| 项目作者或团队 | 必须支持 | 用户可编辑 |
| Remix 原曲 / 原曲作者或团队 | 必须支持 | 用户可编辑，可为空 |
| 版权信息 | 必须支持 | 用户可编辑 |
| 创建时间 | 必须支持 | 只读，一次性，在创建项目瞬间写入 |
| 修改时间 | 必须支持 | 只读，普通保存或保存副本写出时按对应事务规则更新输出文件 |
| 工程总耗时 | 必须支持 | 只读，初版按项目打开累计时间自动累计 |

Project Settings 还必须显示只读的 `Notes` 与 `Events` 编译统计。对最近一次正式全 Project 编译的可消费 canonical compiled result，`Notes` 是正 velocity `NoteOn` MIDI 消息数量，`Events` 是 canonical MIDI channel event 序列中的全部事件数量；两者必须直接由同一已冻结事件序列计算，不得重新解释 Project Source Data。不可消费的编译结果没有可供统计的正式事件序列，因此两者均为 `0`。二者不是用户 Metadata，不持久化、不进入 Undo / Redo，也不影响 canonical musical result。
### 3.6.2 manifest 系统级文件信息
Midora 文件仍必须记录：
```text
createdWithSoftwareVersion
lastSavedWithSoftwareVersion
```
但这两个字段属于 `.midora` `manifest.json` 中的系统级文件信息，不属于用户可编辑 Project Metadata，也不保存于 `metadata.json`。
UI 可以在 Project Settings 的只读 `File Information` 区显示这些值。
### 3.6.3 名称区分
以下概念必须严格区分：
```text
Project Version
    用户可编辑自由文本，用于作品或工程版本说明。
File Format Version
    系统控制的持久化兼容版本，只读。
Created With / Last Saved With Midora
    manifest 系统信息，只读。
```
说明：
- 项目格式版本与用户可见的“项目版本”不是同一概念。
- 项目格式版本用于软件兼容性与迁移，应由系统管理。
- 用户可见的项目版本用于作品或工程版本记录。
### 3.6.4 工程总耗时累计规则
初版工程总耗时从新建 Project 成功提交到主窗口，或打开 Project 成功建立可信 Project Object Graph 时开始累计；New / Open 对话框、打开前验证和失败候选不累计。累计到 Project 开始关闭为止；关闭被取消时，从取消完成、Project 恢复打开状态后继续累计，不补计关闭流程中的暂停时间。

Project 处于打开状态时，以下时段全部累计：
```text
用户无输入的空闲时间
窗口最小化或应用失去焦点
模态对话框和任务窗口
编辑、保存和保存副本
编译、播放、预览和 Buffering
MIDI 导出
音频渲染的 Preparing、Rendering、Cancelling 和 Finalizing
```
系统进入睡眠或休眠后暂停累计，恢复后从恢复通知完成时继续；应用未运行期间不累计。当前会话必须使用不受系统墙钟校时影响的单调 elapsed-time 时钟，不能用 `DateTime.Now` 差值承担累计。

自动累计本身不进入 Undo / Redo、不改变 metadata 修改时间，也不使 Project 单独进入 Modified；普通保存或 Save Copy 只写入取保存快照时已经累计的非负整毫秒值。自动累计不影响音乐编译语义、canonical fingerprint 或任何音乐消费者缓存。
---
## 3.7 项目内对象名称与 ID 规则
### 3.7.1 内部稳定 ID
项目内所有需要被引用的对象都必须拥有内部稳定 ID。
内部引用必须基于稳定 ID，不得依赖用户可见名称。
具体 ID 格式、生成方式、迁移策略属于实现设计实现细则。
### 3.7.2 Event Instrument 名称
Event Instrument 名称在当前 Project 内必须唯一。
该限制的目的不是内部引用，而是迫使用户形成清晰命名习惯，降低项目维护成本。
内部仍然必须使用稳定 ID 维护对象身份。
### 3.7.3 Logical Track 名称
Logical Track 名称允许重复。
创建 Logical Track 时，如果用户未指定名称，默认使用当前指定的 Event Instrument 名称。
允许出现多个同名 Logical Track。
例如：
```text
Beautiful Pad
Beautiful Pad
```
这两个 Logical Track 可以引用同一个 Event Instrument，但它们的运行状态互不影响。
### 3.7.3A Arrangement 父节点与 Pure MIDI Track 名称
MIDI Channel Root 与 Pure MIDI Track 名称允许重复，均不构成身份。Event Instrument / Root 的混排父节点顺序、各 Event Instrument 内 Logical Track 顺序和各 Root 内 Pure MIDI Track 顺序属于正式源数据；Pure MIDI Track 的重排可能改变同 Root 的同 tick 事件顺序，因此属于音乐语义编辑。完整顺序见第 24 章。
### 3.7.4 Segment 名称
Segment 不持有名称。
Logical Segment 是 Logical Track 上一段可以放置 Logical Note 和 Logical Parameter 的有效时间范围；Midi Segment 是 Pure MIDI Track 上保存 Direct MIDI Note、Channel Event 与 opaque imported event 的有效范围。
在 Logical Track / Segment 层，初版“事件”主要指 Logical Parameter Lane / Point / Curve，而不是直接裸 MIDI CC / Pitch Bend / RPN / NRPN。
Segment 内的 Logical Parameter 数据属于 Project 内容。保存、复制、移动、分割、连接 Segment 时，Logical Parameter Lane / Point / Curve 应随 Segment 保留。
Segment 不是 FL Studio 中 Pattern 的等价概念。
Segment 直接存在于 Logical Track 的时间线上。
Segment 可以被用户进行以下操作：
```text
跨轨道复制
移动
向前扩张
向后扩张
缩短
分割成两段
两个相邻 Segment 连接成一段
```
Logical Segment 的关键语义是：
```text
每个 Segment 末尾都会将该 Segment 使用过的事件全部 Reset
```
Midi Segment 不能独立执行会影响同 Root sibling Track 的 Channel-wide Reset；它在 End 只关闭自身 Note，Root 活动连通区间结束才执行完整 Root Reset。两类 Segment 的详细编辑、裁剪与边界规则分别由第 11 章和第 23 章细化。
### 3.7.5 Envelope Preset 名称
Envelope Preset 名称可选，且允许重复。
Envelope Preset 的作用范围在 Event Instrument 内部。
用户通常不必为每个 Envelope Preset 命名。
### 3.7.6 Mapping Function 名称
Mapping Function 名称必填。
Mapping Function 名称在单个 Event Instrument 内不可重复。
Mapping Function 的具体内容、映射目标、编辑方式和编译行为由 第 9 章《曲线、Logical Parameter 与映射》 继续细化。
### 3.7.7 Export Preset
初版不做 Export Preset，也不保存 Project Export defaults。每次 MIDI Export / Audio Render 使用固定初始值和本次任务 Draft。
## 3.8 Logical Track、Usage 与 Definition
Logical Track 通过可空 Usage ID 间接引用 Event Instrument Definition。无 Usage 只允许作为没有任何音乐内容的待指定空壳；它不分配 Unit、不参与正式编译，也不产生“未绑定”诊断。非空 Logical Track 无 Usage 是结构 Error。

新建带乐器 Track 默认创建独立 Usage；用户显式执行 `Share Instrument State With...` 或拖入 Shared block 后，多个 Track 才共享同一 Usage。改绑 Definition、加入/离开 Usage、全局重排及空 Usage 清理必须作为一个失败原子 Undo，保留 Track 内容与稳定 ID。

Definition 可在没有任何 Usage/Track 时独立存在。删除 Track 或最后 Usage 绝不删除 Definition；删除仍被 Usage 引用的 Definition必须阻止。单个 Definition、Usage 或 Logical Track 文件损坏时的隔离、索引可信度与保存禁止规则由第 16、24 章规定；不得按名称静默修复引用。
---
## 3.9 项目保存、打开与关闭
### 3.9.1 保存
普通保存覆盖当前 `.midora` 的完整已提交 Project Source Data：Project Settings/Metadata、Conductor、Arrangement 与 Definition/Usage/Root indexes、Logical/Pure MIDI Tracks、两类 Segment、Event Instruments、Reset Defaults 与已 Apply 的 Mapping Function 源码。

Playback Preferences、SoundFont 列表、MIDI Export 参数、Audio Render 参数、最近目录、未提交的模态编辑缓冲、运行缓存和 UI 会话状态不属于 Project，不得写入包。安全事务、覆盖、临时文件、自校验和路径规则由第 16 章定义。
### 3.9.2 保存副本 / Save Copy
初版不提供传统 Save As。
Save Copy 把当前内存 Project 快照写出为新的 `.midora` 文件，但不改变：
```text
当前 Project 的工作路径
当前 Project 的 Modified 状态
当前 Project 的 Undo / Redo History
当前内存中的 metadata 修改时间
当前内存中的 lastSavedWithSoftwareVersion
```
Save Copy 成功不等于当前 Project 已保存。
如果当前 Project 在 Save Copy 前处于 Modified，Save Copy 后仍然处于 Modified。
Save Copy 不是创建新 Project。副本文件：
```text
保留原 Project 创建时间
保留 createdWithSoftwareVersion
使用当前内存 Project 的工程累计时间快照
把 Save Copy 完成时间写为副本 metadata 修改时间
把当前 Midora 版本写为副本 manifest 的 lastSavedWithSoftwareVersion
```
Save Copy 不更新当前打开 Project 的对应内存字段。
具体目标覆盖、安全替换、资源路径和事务规则由 第 16 章《.midora 文件格式与持久化》 细化。
### 3.9.3 打开
打开 `.midora` Project 时，系统应进行系统级检查：
```text
文件是否可读取
文件是否可识别为 Midora Project
文件格式版本是否可被当前软件版本处理
必要顶层对象是否存在
关键对象引用关系是否可解析
外部资源引用是否可用
Project 是否存在兼容性、迁移或损坏问题
```
初版不提供扫描式 Project Repair Mode，也不猜测或重建未知音乐语义。
### 3.9.4 关闭
关闭 Project 时，如果存在未保存修改，应提示用户处理：
```text
保存
不保存
取消关闭
```
Save Copy 不改变当前 Project 的保存状态，因此不能替代关闭流程中的普通保存。
### 3.9.5 损坏 Project 的分级处理
当 `manifest.json`、`project.json`、schema、文件 kind 或 Project 索引发生无法恢复的结构性不一致，无法建立可信 Project Object Graph 时：
```text
候选 Project 打开失败
```
初版不提供：
```text
扫描式修复
按名称猜测引用
只读修复模式
自动重建未知音乐语义
部分恢复导出
```
当 第 16 章《.midora 文件格式与持久化》 明确允许隔离的单个 Event Instrument 或 Logical Track 对象损坏时，Project 可以打开，并为该对象创建 `Damaged Placeholder`。
Damaged Placeholder 应保留：
```text
稳定 ID
名称快照
对象类型
Project 排序位置
来源文件路径
损坏或读取失败信息
```
Damaged Placeholder：
```text
不参与编译
不能进入普通编辑器
可以由用户明确删除
不等于已修复对象
```
只要 Project 中仍存在任何 Damaged Placeholder：
```text
Save Project 禁止
Save Copy 禁止
```
用户必须先删除全部 Damaged Placeholder，或放弃当前 Project 会话。
该行为属于有界降级打开，不等于 Project Repair Mode。
### 3.9.6 缺失与损坏文件的分级规则
`metadata.json` 缺失时可用空 Metadata 恢复并产生 Error/Modified；既有 metadata 损坏或 hash 不匹配则打开失败。当前三个正式 settings 文件（project/reset/event-scope）缺失或损坏时按第 16.18 节恢复默认、产生 Error 并标记 Modified。Conductor 缺失或损坏时恢复 tick 0 Tempo 120 BPM 与 4/4 并产生 Error/Modified。当前格式不存在 Playback/Export/Audio Render settings 文件。
### 3.9.7 未知与孤立包文件
Zip 包中存在但未被 `project.json` 纳入当前 Project 语义的未知或孤立文件：
```text
不形成 Project 对象
不创建 Damaged Placeholder
可以被忽略并产生 Information Diagnostic
本身不阻止 Save Project 或 Save Copy
```
再次保存时，系统从当前内存 Project 重新生成完整包，因此这些未知或孤立文件不会被保留。
初版不提供“保留未知包文件”选项。
---
## 3.10 项目修改状态
任何正式 Project Source Data 编辑都必须标记 Modified，包括 Metadata、Conductor、Event Instrument/Usage/Root/Track/Segment/Note/Event/Parameter、正式顺序与 Reset Defaults。播放位置、选择、缩放、试听、诊断筛选、Save Copy、Application Preferences，以及 MIDI Export / Audio Render Task Draft 不标记 Project Modified。
## 3.11 Project Presentation 与会话 UI 状态

Format 3 允许在 `.midora` 的独立 `settings/project-presentation.json` 中保存规格明确批准的 Project presentation 数据。该数据不是 Project Source Data，不进入 Project Domain、编译、canonical fingerprint、音频缓存或 Undo/Redo；presentation revision 与 Project Modified/save baseline 分离，presentation 的后台或显式保存不得令标题出现音乐内容修改星号，也不得触发关闭前保存提示。

首个 Project presentation schema 固定以下容器；正式 Onion / All Tracks UI 见 §18.11：

```text
Per-Track Onion source selection / enabled / opacity
Per-SubVoice Onion source selection / enabled / opacity
All-Tracks overlay mode: raw | compiled
```

引用已删除对象的 dormant presentation entry 在保存快照时过滤；presentation 文件损坏、引用失效或 schema 不合法时恢复默认 presentation 并报告 Warning，音乐 Project 仍按其正式源数据打开且不因此标记 Project Modified。

除上述显式白名单外，初版不把一般 UI 会话状态保存进 Project。包括但不限于：
```text
当前打开的编辑器
时间轴缩放
选中对象
面板宽度
当前播放位置
错误面板筛选条件
滚动位置
临时展开 / 折叠状态
```
这些状态不得影响编译、播放、预览、渲染或导出结果。
---
## 3.12 项目级撤销 / 重做
初版应提供完整的、全项目统一的撤销 / 重做框架。
要求：
```text
撤销 / 重做不应只局限于某个局部编辑器
每新增一个功能，都必须评估是否涉及撤销 / 重做
如果该功能属于可撤销编辑行为，应在实现该功能时同步纳入撤销 / 重做系统
```
本章只规定系统级要求，不定义具体命令栈结构、事务模型、合并规则或内存策略。
撤销 / 重做的具体 UI 行为由第 17～20 章规定。
撤销 / 重做的数据结构与实现规则由实现设计工程细则细化。

分配稳定 ID 的创建、复制、粘贴或分割命令遵守：当前打开会话中的 `nextStableId` 高水位永不因 Undo、失败尝试或丢弃 Redo 分支而回退。Redo 恢复同一个对象图及原稳定 ID；新分支只分配更高 ID。若 Undo 回到保存点且除此之外没有变化，单纯的瞬态 allocator 空洞不保持 Modified，也不单独要求保存；以后因其他编辑保存时，当前更高高水位随 Project 一并持久化。关闭 otherwise-clean Project 后丢弃的、从未持久化且不再被任何对象或 History 引用的瞬态身份不构成跨会话兼容承诺。
---
## 3.13 自动保存与崩溃恢复
初版不做自动保存。
初版不做崩溃恢复系统。
初版应优先保证：
```text
项目数据结构科学性
项目文件结构科学性
项目文件大小控制
运行时内存性能
序列化稳定性
反序列化稳定性
```
其中：
```text
项目文件结构优先考虑文件大小
项目数据结构优先考虑运行时内存性能
```
自动保存、临时备份、崩溃恢复、恢复提示和扫描式修复模式均不属于初版；未来版本如需支持，必须另行定义。
---
## 3.14 项目模板与跨项目导入
初版不提供项目模板。
初版不支持：
```text
内置项目模板
用户自定义项目模板
从已有项目保存为模板
模板库
```
初版不做跨项目导入 / 导出 Event Instrument。
初版不支持：
```text
从另一个 .midora 项目复制 Event Instrument
单独导出 Event Instrument 文件
单独导入 Event Instrument 文件
跨项目复制粘贴 Event Instrument
程序级全局 Event Instrument Library
```
未来可以扩展项目模板、事件乐器导入导出或项目间复制功能，但初版不得依赖这些功能成立。
---
## 3.15 项目与编译的关系
Project 是编译器完整输入。编译读取正式 Project Settings/Metadata（仅适用字段）、Conductor、Arrangement 与 Definition/Usage/Root indexes、Logical/Pure MIDI Track、两类 Segment、Direct/Opaque MIDI、Reset Defaults、Event Instrument、Mapping 与 Lifecycle/Overlap policies。编译不得读取 Application Playback/SoundFont Preferences 或当前 Export/Render Dialog Draft。

## 3.16 项目与播放、预览、音频渲染的关系
播放、预览与音频渲染消费 canonical，并额外冻结当前 Application Playback Preferences、程序级 Enabled SoundFont 配置和适用设备/任务参数。无 Enabled SoundFont 时阻止三类音频操作，但不修改 Project。Audio Render 模式、范围、Track 选择、采样率和离线复音上限只属于本次任务；执行、失败、取消均不修改 Project。

## 3.17 项目与 MIDI 导出的关系
MIDI 导出消费 canonical/SMF projection 及本次导出 Draft；不读取 SoundFont 或 Application Playback Preferences。导出模式、范围、Track 选择、Routing、Readme 与 Warning-as-error 开关不保存为 Project defaults。无 Enabled SoundFont 时仍允许 MIDI 导出。
## 3.18 规则、限制与失败条件
### 3.18.1 强制规则
1. 初版只允许同时打开一个 Project。
2. 初版只允许一个可独立启动、显示 UI 和打开 Project 的 Midora 应用实例；第二次启动应转发到已有实例。允许一个不提供 UI、不能独立打开 Project 的内部音频后端子进程。
3. 每个 Project 必须有且只有一个 Conductor Track。
4. Conductor Track 不可删除。
5. 新 Project 的 Conductor Track 默认 Tempo 为 120 BPM，默认拍号为 4/4。
6. 每个 Project 必须有且只有一个有序 Arrangement Track tagged 集合，以及彼此正交的有序 Definition / Usage / Root indexes。
7. 新 Project 的 Arrangement Track、Definition、Usage 与 Root indexes 默认均为空。
8. 新 Project 默认无 Event Instrument、Logical Track、MIDI Channel Root 或 Pure MIDI Track。
9. SoundFont 使用程序级有序 Enabled SF2/SFZ 列表，不属于 Project。
10. 无 Enabled SoundFont 时不能播放、预览或音频渲染，但允许 MIDI 导出。
14. 初版不提供项目模板。
15. 初版不做跨项目导入 / 导出 Event Instrument。
16. 初版不保存 UI 视图状态。
17. 初版不做自动保存。
18. 初版不做崩溃恢复。
19. 初版不提供扫描式 Project Repair Mode；无法建立可信 Project Object Graph 的结构性损坏必须打开失败，但第 16、23、24 章明确允许隔离的单个 Event Instrument / Logical Track / MIDI Channel Root / Pure MIDI Track 损坏可形成 Damaged Placeholder。
20. Event Instrument 名称在当前 Project 内必须唯一。
21. Logical Track、MIDI Channel Root 与 Pure MIDI Track 名称允许重复；global mixed Track order 与独立 Definition order 属于 Project 源数据。
22. Segment 不持有名称。
23. Mapping Function 名称必填，且在单个 Event Instrument 内不可重复。
24. 初版不做 Export Preset、Project MIDI Export defaults 或 Project Audio Render defaults。
25. 初版音频文件输出普通 RIFF/WAVE / Stereo / Interleaved IEEE 32-bit Float；本次任务采样率为 8,000–192,000 Hz 整数，Dialog 初始值 48,000 Hz。
26. Track Mute / Solo 不影响音频文件渲染成品。
27. 音频渲染产物、缓存和任务参数不属于 Project 源数据。
28. 内部引用必须基于稳定 ID，不得依赖名称。
29. 无内容 Logical Track 可以不引用 Usage；含音乐内容的 Logical Track 必须且只能引用一个有效 Usage，该 Usage 必须引用一个有效 Definition。
30. Usage 必须非空，最后成员离开时同事务删除；Definition 可以零 Usage，删除 Track/Usage 不得删除 Definition，被引用 Definition 不得删除。
31. Logical Track 的 Definition rebind、Usage share/independent 与 global order 变更必须保留内容和稳定 ID，并按第 24 章原子提交。
32. 初版应提供完整的全项目统一撤销 / 重做框架。
33. 初版不提供传统 Save As，只提供普通保存与 Save Copy。
34. Save Copy 不改变当前 Project 路径、Modified 状态或 Undo / Redo History。
35. 只要 Project 中存在 Damaged Placeholder，普通保存和 Save Copy 均必须禁止。
36. 未知或孤立包文件不形成 Project 对象，不创建 Damaged Placeholder，也不阻止保存；再次保存时不予保留。
37. Pure MIDI Track 必须属于一个 MIDI Channel Root；Root/Track/Segment/Direct Event 的完整语义按第 23 章执行。
38. 初版支持以 SMF Format 0 / 1、TPQN division 打开为新的未保存 Project，不支持导入当前 Project。
### 3.18.2 警告情况
以下情况应产生项目级或跨系统警告，具体严重程度由统一诊断规则确定：
1. 当前无 Enabled SoundFont 或 SoundFont 无法由 BASSMIDI 使用，且用户尝试播放、预览或音频渲染；
2. 项目格式版本较旧，需要迁移；
3. 项目中存在未来版本功能标记；
4. 项目中存在不会影响打开但会影响编译、播放、预览、渲染或导出的缺失资源；
`Channel Unit` 峰值达到 248 不属于 Warning；它按第 12.15.3 的专门规则产生 `Info`，不得因“Warning 视为 Error”而阻止编译。
### 3.18.3 失败条件
以下情况应导致相应操作失败：
| 场景 | 失败结果 |
|---|---|
| 文件无法识别为 Midora Project | 打开失败 |
| manifest、project.json、schema、文件 kind 或 Project 索引存在无法恢复的结构性不一致 | 打开失败 |
| metadata.json 存在但损坏、hash 不匹配或无法解析 | 打开失败 |
| 存在 Damaged Placeholder 时尝试普通保存或 Save Copy | 阻止保存 |
| 保存目标不可写 | 保存失败 |
| 无 Enabled SoundFont 时尝试播放 | 播放失败或阻止播放 |
| 无 Enabled SoundFont 时尝试预览 | 预览失败或阻止预览 |
| 无 Enabled SoundFont 时尝试音频渲染 | 渲染失败或阻止渲染 |
| 项目内部引用断裂且影响编译 | 编译失败 |
| C# 映射源码缺失或无法编译 | 编译失败 |
| 项目配置违反本规格强制边界 | 应阻止保存、阻止编译或标记为错误，具体场景由实现设计确定 |
---
