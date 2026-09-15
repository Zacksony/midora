# 第 14 章 MIDI 导出

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义从 Canonical Compiled Result 生成 SMF Type 1 产物的规则，包括导出模式、范围、Routing、Track/Meta 组织、Channel 10 初始化、Readme、文件事务和兼容边界。

## 14.1 MIDI 导出核心原则
### 14.1.1 只消费 canonical compiled result
MIDI 导出系统必须消费 `canonical compiled result`。
规则：
```text
MIDI 导出系统不得直接读取 Logical Track / Pure MIDI Track / Segment / Event Instrument 并自行解释音乐语义或 Track 拓扑。
MIDI 导出系统不得重新计算 Logical Parameter Mapping。
MIDI 导出系统不得重新决定生命周期、Reset、Channel Group 或 Port / Channel Unit 分配语义。
MIDI 导出系统不得为了性能绕过编译系统。
```
MIDI 导出系统可以在 compiled result 之上构建：
```text
MIDI 文件结构
按冻结 SMF Track Projection 组织 MIDI Track（不得因 MTrk 字节超限再拆分）
Port / Device / Track Name 信息
Readme 信息
文件命名
编码缓存
导出诊断
```
但这些结构都是导出产物组织或辅助说明，不得成为另一套音乐语义来源。
### 14.1.2 使用 MIDI Export CompileContext
MIDI 导出必须使用专用：
```text
MIDI Export CompileContext
```
导出流程可以复用等价缓存，但必须保证输出等价于在同一 Project 内容、同一 Source 状态和同一本次导出参数下重新执行该导出上下文编译。
不得直接复用以下内容作为导出语义来源：
```text
最近一次播放 compiled result
最近一次播放 buffer
运行中的 BASSMIDI Stream 状态
当前播放设备状态
当前播放光标
当前临时 Mute / Solo 状态
```
除非用户明确把播放光标等信息作为导出范围参数。
### 14.1.3 编译失败时不导出 partial MIDI
如果 MIDI Export CompileContext 编译失败：
```text
整个导出失败。
不生成 partial .mid。
不生成成功 Readme。
```
如果用户开启“Warning 导致编译失败”：
```text
导出准备编译中的 Warning 也会导致导出失败。
```
### 14.1.4 多文件导出原子性
多文件导出中，如果任意一个文件导出失败：
```text
整体导出失败。
不留下不完整文件。
已经生成的临时或 partial 文件应尽力清理。
如果无法清理，应进入文件写入诊断并提示用户。
```
允许并建议实现层采用：
```text
临时目录写入
编码和自校验完成后再原子替换或移动到目标目录
```
具体实现策略实现设计阶段定义。
---
## 14.2 Standard MIDI File 类型
### 14.2.1 初版统一使用 SMF Type 1
Midora 初版所有 `.mid` 导出统一使用：
```text
SMF Type 1
```
初版不提供 Type 0 / Type 1 用户选择。
理由：
```text
Midora 需要保留 Conductor Track / Meta Track。
Midora 需要表达 Conductor 与实际 Channel Unit 的 Track 拆分关系。
SMF Type 1 更适合多 Track 结构与全局 Meta Track。
```
### 14.2.2 MIDI division 使用 Project TPQ
导出 MIDI 文件的 ticks-per-quarter-note 使用：
```text
Project TPQ
```
初版不允许在 MIDI 导出时修改 TPQ。
不支持：
```text
导出时重新指定 TPQ
导出时升采样 / 降采样
只修改 MIDI 文件头但不重算事件
```
---
## 14.3 导出模式
初版至少支持以下 MIDI 导出模式：
```text
整曲单 MIDI 导出
按 Logical Track 导出
按 Port 导出
```
### 14.3.1 整曲单 MIDI 导出
整曲导出生成一个 `.mid` 文件。
Track 组织规则：
```text
Track 0 = Conductor / Meta Track
随后 = 每个被选择 Pure MIDI Track 的独立单 Channel MTrk
最后 = Logical/Event Instrument 实际有 Channel Event 的 Channel Unit MTrk
```
整曲事件 Track 排序固定为：
```text
Conductor
→ Pure MIDI Root explicit order
→ Root 内 Pure MIDI Track explicit order
→ Logical Unit 原始 Port
→ Logical Unit Channel
```
同一 Pure MIDI Root 的多个 Track 可以共享一个 Port.Channel，但必须保持为多个 MTrk；每个 Pure MIDI MTrk 只包含其 canonical `ExportTrackId` 的事件。Logical/Event Instrument 路径继续保持同一文件内每个实际 Unit 一个 MTrk；一个 Logical Unit 被不同 Logical Track / Instance 先后复用时不得按来源拆分。

被选择且结构有效的空 Pure MIDI Track 可以生成只含结构 Meta 与 EOT 的 MTrk，以保留用户 Track 结构；Logical Unit 不生成完全无 Channel Event 的 MTrk。
Conductor Track 必须存在。
### 14.3.2 按 Logical Track 导出
按 Logical Track 导出时：
```text
每个被选择且有效参与导出的 Logical Track 生成一个 .mid 文件。
每个 .mid 文件都包含 Track 0 Conductor / Meta Track。
该文件内部可包含一个或多个事件 Track。
如果该 Logical Track 使用多个 Port，仍然是一个 .mid 文件，而不是每个 Port 一个文件。
该文件内部按实际使用的 Channel Unit 拆分事件 Track，每个 Unit 一个 Track。
```
该单 Track 文件内部事件 Track 排序：
```text
Track 0 = Conductor / Meta Track
后续事件 Track 按原始 Port、再按原始 Channel 编号排序
```
如果某个有效 Track 没有产生任何音乐输出：
```text
仍生成结构有效的空音乐内容 MIDI。
Readme / 诊断中说明该 Track 无音乐输出。
```

`Per Logical Track` 是既有 Logical/Event Instrument 专用模式：Pure MIDI Track 不在该模式中生成文件，也不得被复制进每个 Logical Track 文件。需要单独导出某条 Pure MIDI Track 时，使用 `Whole Project` 并显式只选择所需 Pure MIDI Track；该单文件仍保持标准的 Conductor + 独立 Pure MIDI MTrk 结构。
### 14.3.3 按 Port 导出
按 Port 导出时：
```text
每个本次导出上下文实际包含 Channel Event 或被选择 Pure MIDI Track descriptor 的 Port 生成一个 .mid 文件。
不固定生成 16 个文件。
不为未使用 Port 生成空文件。
```
每个 Port 文件内部：
```text
Track 0 = Conductor / Meta Track
随后 = 该原始 Port 内的 Pure MIDI Track，按 global Arrangement Track Order 过滤顺序
最后 = 该原始 Port 内 Logical/Event Instrument 实际有事件的 Unit，按原始 Channel 编号
```
按 Port 导出时不在事件 Track 层保留 Logical Track 拆分；同一 Logical Unit 即使被多个 Logical Track 先后复用，也只生成一个事件 Track。Pure MIDI Track 拆分、名称和相对顺序必须保留。
不生成完全无 Channel Event 的 Unit Track。
每个单 Port 文件内部按独立 MIDI 文件处理：
```text
事件写入 Port 1 语义。
原始 Midora Port 编号体现在文件名、MIDI Track Name、Readme 中。
```
这是一种文件级归一化，不等同于改变原 compiled result 的项目语义。
### 14.3.4 多文件导出输出组织
按 Logical Track / 按 Port 导出时：
```text
用户选择完整输出目录；该选择不是“父目录 + 系统自动生成的嵌套文件夹名”。
所选目录不存在时，由任务按该精确路径创建；不得再自动增加一层目录。
文件夹中包含多个 .mid 文件。
默认生成一个 Readme。
```
Readme 允许用户关闭；若用户关闭 Readme，则不存在 Readme 写入失败问题。
---
## 14.4 导出范围
### 14.4.1 默认导出范围
MIDI 导出默认范围：
```text
startTick = 0
endTick = Project End Marker tick，若存在 Project End Marker
endTick = 有效内容自然结束，若不存在 Project End Marker
```
如果 Project End Marker 早于后续内容：
```text
默认整曲导出不包含 Project End Marker 后内容。
Project End Marker 后内容仍保留在 Project 中。
```
普通 Marker / 超出音乐内容的 Conductor 事件不强制延长默认结束位置。
### 14.4.2 手动导出范围
初版允许用户手动选择导出范围。
范围统一采用左闭右开区间：
```text
[startTick, endTick)
```
合法性规则：
```text
startTick >= 0
endTick >= startTick
endTick == startTick 允许，表示零长度导出
```
不要求：
```text
startTick 必须为 0
范围必须对齐小节线
范围必须落在 Project End Marker 之前
范围必须包含有效音乐内容
```
### 14.4.3 手动范围与 Project End Marker
Project End Marker 只影响默认范围。
显式手动范围优先于 Project End Marker。
因此允许：
```text
startTick > Project End Marker tick
endTick > Project End Marker tick
手动范围跨过 Project End Marker
```
只要范围合法，显式手动范围内 Project End Marker 后的内容应参与导出。
### 14.4.4 零长度导出
当：
```text
startTick == endTick
```
允许生成结构有效 MIDI。
零长度导出应至少包含：
```text
必要 Conductor / 状态信息
End Of Track
```
End Of Track 写在：
```text
startTick / endTick
```
### 14.4.5 导出范围不受当前播放光标和选中对象影响
默认 MIDI 导出范围不受以下内容影响：
```text
当前播放光标
当前选中 Segment
当前选中 Track
当前视图范围
当前 Mute / Solo 状态
```
除非用户明确选择相应导出范围，例如“从播放光标开始导出”。
---
## 14.5 范围导出状态恢复
### 14.5.1 范围起点必须写入必要非 Note 状态
当导出范围从项目中途开始时，导出结果应包含范围起点必要非 Note 状态恢复事件。
包括但不限于：
```text
Tempo
Time Signature
Key Signature
Program
Bank Select
Pitch Bend
Pitch Bend Range
CC
RPN
NRPN
Logical Parameter Mapping 后影响到的非 Note 状态
```
恢复事件放在：
```text
startTick
```
并在语义排序上早于该 tick 的普通用户事件。
如果恢复状态与 startTick 上用户原始事件冲突：
```text
startTick 用户原始事件优先。
恢复事件只提供进入该 tick 前的上下文。
```
### 14.5.2 不补发范围前 Note On
如果某个 Note On 已经在范围开始前发生：
```text
不得在 startTick 伪造或补发 Note On。
```
即使该音在范围内理论上仍持续发声，也不在范围起点重新发声。
范围前已经 Note On、范围内发生原始 Note Off：
```text
应输出该 Note Off。
```
范围前已经 Note On、范围内没有 Note Off、范围结束时仍跨界：
```text
范围结束作为硬裁剪边界，需要补充必要 Note Off / Reset。
```
### 14.5.3 范围内 Conductor 事件
范围导出中的 Conductor Track 规则：
```text
在范围起点写入当前有效 Tempo / Time Signature / Key Signature 状态。
范围内普通 Marker 正常导出。
范围前普通 Marker 不恢复。
```
普通 Marker 不是状态型事件。
Key Signature 是全局状态型 Meta Event，因此范围起点应恢复当前有效 Key Signature。
---
## 14.6 Track 选择
### 14.6.1 默认 Track 选择
`Whole Project` 与 `Per Port` 默认选择：
```text
所有有效 Logical Track
所有有效 Pure MIDI Track
```
`Per Logical Track` 默认且只能选择所有有效 Logical Track；Pure MIDI Track 不属于该模式的候选集合。
Damaged Definition/Usage/Track/Root Placeholder 不参与导出并阻止正式任务。无内容且无 Usage 的 Logical Track 是合法空壳但不是有效导出目标；有内容却无有效 Usage/Definition 是结构 Error。
### 14.6.2 显式选择 Track
`Whole Project` 与 `Per Port` 允许用户勾选要导出的 Logical Track 与 Pure MIDI Track；`Per Logical Track` 只允许勾选 Logical Track。切换模式时可以保留不适用于当前模式的临时勾选状态以便切回，但当前任务的冻结 Track 集合不得包含该模式不支持的类型。
用户取消勾选某个 Track 后：
```text
该 Track 不参与本次导出编译。
该 Track 不生成 Event Instrument Instance。
该 Track 不占用本次导出上下文资源。
不因“未导出”本身产生诊断。
Readme 记录即可。
```
Track 选择集合会影响本次导出 CompileContext 的资源需求和 Port / Channel Unit 分配。

选择同一 Root 的部分 Pure MIDI Track 不得自动带入未选择 sibling Track；Root 生命周期与状态按本次被选择集合重新建立。Fixed Root 的路由仍必须保留。
### 14.6.3 Logical Track Usage / Definition 错误
有内容 Logical Track 无唯一有效 Usage/Definition 是结构 Error，导出准备失败。Damaged Placeholder 不得生成空 Track 或把 Logical Note 直接当作普通 MIDI Note 导出。
### 14.6.4 Mute / Solo 不影响导出
MIDI 导出不受当前临时 Mute / Solo 状态影响。
Mute / Solo 是实时监听状态，不是成品输出选择。
---
## 14.7 Routing 策略
初版导出支持两种 Routing 策略：
```text
Compact Routing
Preserve Routing
```
默认：
```text
按 Logical Track 导出默认使用 Compact Routing。
用户可切换 Preserve Routing。
```
按 Port 导出也需要支持 Routing 选项，但其语义主要影响本次导出上下文如何决定实际使用 Port；单 Port 文件内部仍归一化为独立 Port 1。
### 14.7.1 Compact Routing
Compact Routing 的系统级语义：
```text
在不改变音乐语义的前提下，对本次导出内容重新分配 Port / Channel。
从 Port 1 / Channel 1 开始尽量紧凑。
如果不能保证语义等价，则导出失败。
```
Compact Routing 允许：
```text
改变 Port / Channel 分配
把原本不同 Port 的内容压到同一 Port
重新映射 Port / Channel Unit
```
前提：
```text
仍满足 Channel Unit 隔离
仍满足 Channel-Wide 状态污染边界
仍满足资源上限
仍保证 canonical compiled result 语义等价
```
Compact Routing 不允许：
```text
移动或重映射 Fixed MIDI Channel Root
把同一 Pure MIDI Root 拆到多个 Unit
合并两个不同 Root
改变导出模式要求的文件 / Track 组织结构
改变 Logical Track 输出顺序
改变生命周期、Reset、Overlap 或同 tick 语义排序
通过丢弃事件换取更紧凑路由
```
Compact Routing 失败时：
```text
导出失败。
提示用户可改用 Preserve Routing。
不自动退回 Preserve Routing。
```
Readme 必须记录：
```text
原 compiled routing
compact routing
新旧路由映射关系
```
### 14.7.2 Preserve Routing
Preserve Routing 的系统级语义：
```text
保持对应导出上下文中编译得到的 Port / Channel Unit 分配。
不为导出重新压缩。
```
在 Track 子集导出时：
```text
Preserve Routing 保持该导出上下文编译得到的 Port / Channel 分配。
不保证与全项目完整导出完全相同。
```
Preserve Routing 允许为了文件可读性重命名 Track。
Track Name 可读性不属于 Port / Channel 分配语义，但不得隐瞒原始 Port 信息。
Preserve Routing 不要求：
```text
输出未使用 Port 的空文件
输出未使用 Channel Unit 的空 Track
输出全部 16 Ports
输出全部 256 Channel Units
```
---
## 14.8 MIDI Track 与 Meta Track 组织
### 14.8.1 Conductor / Meta Track
每个导出的 MIDI 文件都必须包含：
```text
Track 0 = Conductor / Meta Track
```
Conductor Track 中允许写入：
```text
Tempo
Time Signature
Key Signature
Marker
Copyright Meta Event
简短非语义 Text Meta Event
Project 名称等项目信息
Midora 生成工具信息
End Of Track
```
Conductor Track 中不允许写入：
```text
Note
CC
Pitch Bend
Program Change
Bank Select
RPN / NRPN 展开事件
Pitch Bend Range 展开事件
Channel 10 melodic 初始化 Channel Event / SysEx 初始化事件
普通 Channel Event
```
Channel Event 必须写入对应事件 Track。
### 14.8.2 Track Name
导出文件应写入 Track Name Meta Event。
规则：
```text
Conductor Track 使用 MIDI 导出任务准备时冻结的 Project Name；Project Name 为空或纯空白时防御性回退为 `Conductor`。
每个 Logical/Event Instrument Unit Track 固定为 `Port <P> / Channel <C>`。
每个 Pure MIDI 事件 Track 使用 canonical descriptor 中冻结的用户 Track Name；为空或纯空白时回退为 `MIDI Track <N>`。
上述 Unit Track 名称中的 P 是原始 Midora 一基 Port 编号 1–16，C 是一基 Channel 编号 1–16，均不补零；按 Port 导出内部 Port Meta 归一化为 Port 1 时仍写原始 P/C。
```

Project Name、Pure MIDI Track Name 和 Unit Track Name 都按严格 UTF-8 写入，不经过文件名合法化，也不回写 Project/Track 名称。任务准备后即使活动 Project Name 改变，本任务仍使用冻结值。Per Logical Track 输出文件名和 Readme 仍使用 `LogicalTrackDisplayName`；一个 Logical Unit 在 Whole Project / Per Port 中可能先后承载不同 Logical Track，因此该 Unit Track Name 不得冒充单一 Logical Track owner。`Conductor / Meta Track` 是 Track 0 的结构角色名称，不再是正常项目固定写入的用户可见曲名。
### 14.8.3 Project 名称、版权和软件标识
MIDI 文件内部可以写入：
```text
Project 名称：Track 0 文本类 Meta Event
版权信息：Copyright Meta Event
Midora 软件标识：非语义 Text Meta Event
```
这些信息不作为播放语义。
Readme 也应记录 Project Metadata 中可用的版权信息和软件版本信息。
### 14.8.4 不写 Lyric / Cue Point
初版不写：
```text
Lyric Meta Event
Cue Point Meta Event
```
不将普通 Marker 写为 Cue Point。
不将 Project End Marker 写为 Cue Point。
### 14.8.5 Text Meta Event
初版允许写入简短非语义 Text Meta Event，例如：
```text
Project 名称
Midora 生成信息
必要的简短说明
```
不将完整 Readme 内容同步写入 MIDI Text Meta Event。
Readme 可能较长，MIDI Text 只写必要简短非语义信息。

### 14.8.6 Midora Pure MIDI 结构 Meta

Pure MIDI MTrk 可在 tick 0 写版本化 Sequencer-Specific Meta，以保存 Root/Track Stable ID、global Arrangement Track position、Root membership、Routing Mode 和 Channel Mode。它必须有固定 magic/version、有界长度和严格校验，不得影响播放，也不得替代标准 Track Name、MIDI Port 与 Channel status。标准 Track Name 只写 Pure MIDI Track 名称，不写内部 Root 名称。

其他软件可以安全忽略或删除该 Meta。重新导入时仅在 payload 合法且与标准事件结构一致时采用；否则按 Port.Channel 与 MTrk 顺序退化重建，不得因此拒绝原本合法的 SMF。SMF 没有标准 Root/folder 层级，Midora 只保证平级 Pure MIDI MTrk 的名称、顺序与独立性。
---
## 14.9 Conductor 事件导出
### 14.9.1 Tempo
Tempo 事件导出为 MIDI Set Tempo。
第 14 章《MIDI 导出》 只规定：
```text
Tempo BPM 到 MIDI Set Tempo 的转换必须确定。
转换必须可重复。
转换错误必须可诊断。
```
初版固定换算规则：
```text
microsecondsPerQuarterNote = 60,000,000 / BPM
```
使用十进制计算，并只对最终商执行一次 `Round / Away From Zero`。舍入后的值必须位于 MIDI Set Tempo 的 24-bit 有效范围 `1..0xFFFFFF`；不得逐步取整、截断或 clamp。
如果 Tempo 无法表示为 Midora 支持的 MIDI 1.0 有效 Set Tempo：
```text
导出失败。
```
不自动 clamp，不删除该 Tempo 事件，不作为 Warning 继续。
### 14.9.2 Time Signature
Time Signature 应导出为 MIDI Time Signature Meta Event。
MIDI metronome / 32nd-notes 字段：
```text
clocks per metronome click = 24
notated 32nd notes per MIDI quarter note = 8
```
这两个字段不是用户设置。
### 14.9.3 Key Signature
如果 Project 没有显式 Key Signature：
```text
导出时不强行写入 C major。
```
如果存在 Key Signature，则按范围和状态恢复规则导出。
### 14.9.4 Marker 与 Project End Marker
普通 Marker：
```text
导出为 MIDI Marker Meta Event。
范围导出时只导出范围内 Marker。
范围前普通 Marker 不恢复。
```
Project End Marker：
```text
默认不作为普通 Marker 导出。
它是 Midora 内部结束边界。
它影响默认导出范围。
它不等同于普通 Marker。
```
---
## 14.10 Port / Device 信息
### 14.10.1 整曲导出与按 Track 导出
整曲导出和按 Track 导出中，导出器应写入必要 Port 信息，例如：
```text
Port Number
Device Name
Port Name
Track Name 中的 Port 信息
```
初版兼容档固定为：
```text
每个事件 Track 写 Track Name Meta Event
每个事件 Track 写 MIDI Port Meta Event
每个事件 Track 只写一个原始 Channel Unit 的 canonical Channel Event；同一 Root 的多个 Pure MIDI Track 可共享该 Unit
不写 Device Name Meta Event
不写 Program Name Meta Event
文本类 Meta Event 使用严格 UTF-8
```
Track Name 最终可见字符串和输出文件命名模板仍由导出工作流提供，不得由编码器隐藏生成。
### 14.10.2 按 Port 导出
按 Port 导出时，每个单 Port 文件内部按独立 MIDI 文件处理：
```text
内部 Port 语义归一化为 Port 1。
原始 Midora Port 编号记录在文件名、Track Name 和 Readme 中。
```
### 14.10.3 兼容边界
Port / Device 信息属于提高第三方环境可读性和兼容性的辅助信息。
Midora 不承诺所有第三方播放器完全理解：
```text
Port meta event
Device / Port Name
多 Port 结构
Channel 10 melodic 初始化
SoundFont 相关说明
```
---
## 14.11 Channel 10 模式初始化
### 14.11.1 必须写入必要初始化
MIDI 导出应在需要 melodic Channel 10 的事件 Track 中写入必要的 Midora 内置 Channel 10 melodic 初始化事件。
该初始化属于系统生成内容，不表示用户可以自由编辑 SysEx。
初版仍然不开放用户自由 SysEx。

初版固定同时写入以下两个 vendor Normal Part 初始化，顺序不得改变：
```text
Roland GS: F0 41 10 42 12 40 10 15 00 1B F7
Yamaha XG: F0 43 10 4C 08 09 07 00 F7
```
规则：
```text
GS 在前，XG 在后
使用上述固定默认 Device ID / Device Number 字节
不得发送 GS Reset、XG System On / Reset 或 GM Reset
不得借初始化改写 canonical Bank / Program
只对实际包含 Channel 10 canonical Channel Event 的 Logical Unit Track，或 Melodic Pure MIDI Root Track 写入
每个相关事件 Track 各写一次 GS 和 XG 初始化
Percussion Pure MIDI Root Track 不写入这些 Normal Part SysEx
不使用 Channel 10 的事件 Track 不写入这些 SysEx
Conductor Track 永远不写入这些 SysEx
```
无法识别某个 vendor SysEx 的接收方可以忽略该消息；两类消息均无法识别时，接收方仍可能按其默认鼓通道处理 Channel 10。
### 14.11.2 写入位置
Channel 10 melodic 初始化事件应写在：
```text
相对导出 tick 0
Track Name Meta 之后
MIDI Port Meta 之后
全部 canonical Channel Event 之前
```
不得写入 Conductor Track。
目标是保证该 Port 的 Channel 10 在普通事件前已完成 melodic 初始化。
### 14.11.3 Readme 说明
Readme 应说明：
```text
Midora 会写入必要初始化以提高 Channel 10 melodic 行为一致性。
初版同时写入 GS 与 XG Normal Part 消息，但不发送任何 GS/XG/GM Reset。
初始化使用固定默认 Device ID / Device Number；接收方配置不同设备编号时可能忽略。
该初始化不承诺被所有播放器完全支持。
```
---
## 14.12 MIDI Channel Event 与高级事件导出
### 14.12.1 Note Off 编码形式
Note Off 统一使用真正的 MIDI Note Off status：
```text
Note Off velocity = 0
```
不使用 Note On velocity = 0 作为普通 Note Off 表达。
Note On velocity = 0 不作为普通 Note On 输出。
### 14.12.2 Absolute tick 到 delta time
`canonical compiled result` 内部事件以绝对 tick 表示。
导出阶段先按导出范围起点重基，再按每条 MIDI Track 内的最终编码顺序转换为：
```text
delta time
```
每个实际写入 SMF 的 delta time 必须位于 `0..0x0FFFFFFF`（四字节 VLQ，最大 268,435,455）。该限制不是 Project/canonical 的绝对 Tick、总时长、Note Gate 或相邻事件间隔上限。

对于相邻输出事件之间大于该上限的非负间隔，导出器必须插入零长度 Text Meta `FF 01 00`，将其分成可编码的 delta；原事件的绝对位置、同 tick 相对顺序、payload 与 Track 归属保持不变。具体规则：

- 每个 MTrk 独立处理，包括 Conductor、Pure MIDI 和 Logical Unit Track；覆盖首个事件之前、事件之间，以及最后一个事件到 EOT 的间隔。不能借其他 Track 的事件替本 Track 累计时间。
- 设间隔为 `D`、上限为 `M = 0x0FFFFFFF`。`D=0` 不插入；`D>0` 时插入 `N = floor((D-1)/M)` 个占位事件，每个前置 delta 固定为 `M`，原事件前置 delta 为 `D-N×M`。因此 `D=M` 不插入，`D=2M` 只插入一个，不能额外生成零间隔占位。
- 占位只存在于 SMF 编码产物，不写回源 Project，不进入 canonical、编译诊断/统计、fingerprint、增量编译 dirty range、播放、预览或音频渲染。不得为了该策略增加全量或增量编译的事件间隔扫描。
- 不得用 Note、CC、Tempo、Port、Track Name、EOT 或其他有状态/结构含义的事件充当占位，也不得改变 TPQ、移动原事件、提前 EOT、缩短 Gate 或删减内容来规避上限。
- 这是 **delta-time 编码** 的特例，不适用于 Meta/SysEx 的 payload 长度 VLQ；后者超限仍严格失败，见 §14.12.8。
- 插入前必须按 §14.12.9 检查占位成本及当前 MTrk 字节预算；若因此超限，导出失败，不能无限填充，也不能拆分 MTrk。

成功填充产生导出级汇总 Info，按 §14.15.7 展示。第三方编辑器可能显示这些空文本；重新导入时仍按合法 opaque Meta 的既有保留规则处理，不能把所有空 Text Meta 当作 Midora 占位而删除，也不新增隐藏私有识别协议。
### 14.12.3 Bank Select 与 Program Change
同 tick 下：
```text
Bank Select 应早于 Program Change。
```
固定顺序为：
```text
CC0 Bank Select MSB
→ CC32 Bank Select LSB
→ Program Change
```
缺少 MSB 或 LSB 时只省略不存在的部分，不改变其余相对顺序。
### 14.12.4 RPN / NRPN / Pitch Bend Range
RPN / NRPN / Pitch Bend Range 等 Midora 高级事件必须展开为标准 MIDI 1.0 CC 序列。
规则：
```text
展开顺序必须稳定。
不得写为自由 SysEx。
不得写为 Text Meta Event 让播放器解释。
不得在初版跳过导出。
```
具体 CC 展开字节序列由实现设计阶段定义。
### 14.12.5 CC91 / CC93

Event Instrument/SubVoice 路径不得产生 CC91 或 CC93。Pure MIDI Track 的 canonical 事件允许包含 CC91 / CC93，MIDI 导出必须按冻结 Track/tick/order 原样写出，不得因为正式 BASSMIDI 音频路径启用 `BASS_MIDI_NOFX` 而删除或报错。

### 14.12.6 SysEx 与 opaque Meta 边界
初版不允许用户创建或任意编辑自由 SysEx payload。
但系统可写入内置必要初始化事件；从 SMF 导入并保存为 Pure MIDI opaque event 的合法 SysEx/Meta 必须按 canonical descriptor 原样重新导出。导出器不得解释其业务含义。具体范围见第 23.6.5、23.12.6 节。
### 14.12.7 Reverb / Chorus Send 零值初始化

MIDI 导出器必须在每个实际输出的单 Channel 事件 MTrk 的相对 tick 0 各写一次：

```text
CC91 Reverb Send = 0
CC93 Chorus Send = 0
```

规则：

```text
顺序固定为 CC91 后 CC93
Logical Unit Track 与 Pure MIDI Track 都写入
Whole Project、Per Logical Track、Per Port 以及分页/非分页编码路径完全一致
Conductor Track 没有 Channel，永远不写入
该初始化只属于 MIDI 导出编码结果，不进入 Project 或 Canonical Compiled Result
```

同 tick 顺序固定为：

```text
Track Name / MIDI Port / Midora Pure MIDI 结构 Meta
→ Channel 10 melodic GS/XG 初始化（如适用）
→ CC91=0
→ CC93=0
→ canonical / opaque 事件
```

Pure MIDI canonical 中显式存在的 CC91/CC93 必须继续按冻结 tick/order 原样写出；若也位于相对 tick 0，则它们在系统零值初始化后生效。导出器不得借此删除、覆盖或折叠用户事件。Event Instrument/SubVoice canonical 仍服从第 14.12.5 节的禁止规则。

### 14.12.8 SMF 硬限制与检查归属

以下限制必须明确拒绝，不能通过占位、截断、取模、clamp、拆分记录/Track 或改变原事件语义绕过：

| 对象 | 限制 | 检查归属 |
| --- | --- | --- |
| TPQ division | `1..32767`；不改为 SMPTE、不自动降低 TPQ | 保持 Project/语义验证的既有约束，导出再次防御检查 |
| Tempo | 按 §14.9.1 十进制计算并仅一次 AwayFromZero 后，microseconds-per-quarter-note 必须为 `1..0xFFFFFF` | 保持语义验证，导出再次防御检查；不能夹到端点 |
| Channel Event 与已解释 Meta | 保持各正式类型的值域、固定 payload 长度、拍号/调号及 TPQ 组合约束 | 保持既有语义/结构验证；编码阶段发现仍为 Error，不改变 opaque 未知事件的保留边界 |
| 单个 Meta/SysEx 记录的 payload | 长度 VLQ 为 `0..0x0FFFFFFF` 字节，且仍需满足该类型的结构约束；SysEx 按线格式 length 字段实际覆盖的字节计数 | 导出编码硬检查；不拆记录，不把长度超限误报为 delta 超限 |
| 单个 SMF 的 MTrk 数量 | `1..65535`，包含 Conductor 和全部实际输出的 Track | 只属于导出；写入文件前检查，不合并或删除 Track |
| 单个 MTrk 数据区 | 最多 `0xFFFFFFFF = 4,294,967,295` 字节，即 **4 GiB − 1 byte** | **只属于 MIDI 导出，不作为编译错误或编译预检条件**；按 §14.12.9 检查 |

MTrk 数据区包括全部 delta、状态/事件字节、结构与初始化 Meta/SysEx、占位事件和最终 EOT；不包括该 chunk 自身的 4 字节 `MTrk` 标识和 4 字节 length 字段。这不是整个 `.mid` 文件的 4 GiB 上限：由多个合法 MTrk 组成的文件可以更大，但仍受文件系统、空间和实现中明确检查的安全计数边界约束。

**MTrk 不因大小而拆分**：Pure MIDI 仍一用户 Track 一个 MTrk，Logical/Event Instrument 仍一实际 Unit 一个 MTrk，Conductor 仍一个 MTrk。任意 MTrk 超限时，本次 MIDI 导出按原有原子事务整体失败，不自动拆成多个 Track/文件、不改为其他格式、不发布截断产物。既有由用户显式选择的导出模式不受改变，但不得作为超限后的静默 fallback。

编译不计算 MTrk 编码大小，不因其超限改变成功判定、canonical 或 Full/Incremental 结果。Project 音乐语义、Int64 Tick 算术及消费者各自的既有资源限制仍独立成立；SMF 导出失败不使原本合法的播放/音频渲染变为失败。

### 14.12.9 有界编码、取消与失败原子性

- 编码器必须在写入下一个事件/占位批次之前检查精确字节增量与当前 Track 剩余预算；EOT 本身及其 delta 也计入，不能写完整个超大 MTrk 后才检查 `uint` 长度，更不能在长度字段回填时截断高位。未确认事件枚举结束时，不能把尚未读取的剩余时间视作 EOT 前空白，避免重复计费或误报超限。
- 对可由冻结 descriptor 或当前间隔直接证明不可能装入合法 MTrk 的情形应立即失败。一次超长间隔的占位成本用整数算术计算，不得先循环构造或写出巨量占位才发现失败；不要求为此在编译阶段或导出前额外遍历完整 canonical。
- Track 累计字节、文件位置、占位数量/增量、Tick 差与累计必须使用可安全表示的计数并在运算前防溢出。接近 Int64 Tick 上界的合法输入不能引起回绕、无界分配或失控写盘；若填充最低成本已超 MTrk 上限，直接报告 MTrk 大小 Error。
- 正常与超长间隔路径共用有界流式写入、进度和取消；不把整条 Track 或全部占位物化到内存，也不让巨大占位循环长时间不检查取消。具体表示允许优化，但输出字节、原事件顺序和失败边界必须等价。
- 分页、非分页、延迟枚举写入路径均须把编码硬限制失败归为可读的导出编码 Error，指出目标文件、Track 和超限项；有来源时附带来源。不得只显示笼统的 staging 文件写入失败或使异常越过 UI 边界。
- 任意编码、校验、写入失败或取消均不发布本任务的 partial 文件，保留原有目标和 Project/canonical；临时输出清理及多文件发布遵循 §14.1.4、§14.18。
---
## 14.13 同 tick 排序与编码优化边界
### 14.13.1 状态恢复事件与用户事件
范围起点恢复事件应早于该 tick 用户事件。
如果同类状态冲突：
```text
用户事件优先。
```
### 14.13.2 Reset / 安全清理与普通事件
Reset / 安全清理事件必须排在对应生命周期结束或范围结束的清理阶段。
不得抢在仍应输出的普通事件之前。
具体同 tick 细表由实现设计阶段定义。
### 14.13.3 跨 MIDI Track 顺序
SMF 不提供可移植的跨 MIDI Track 严格音乐总顺序。Midora canonical 对同一 Root 的 Pure MIDI 原始事件仍具有确定执行顺序，但第三方播放器未必按同一跨 MTrk tie 顺序消费。
需要严格先后关系的事件应：
```text
位于同一 MIDI Track
或由编译排序保证在同一资源语义内成立
```
导出器不负责把所有同 tick 跨 Track 事件全局线性化为单一顺序。
Logical/Event Instrument 同一 Unit 的事件不会被拆到不同 MIDI Track。Pure MIDI Root 可以有多个共享 Unit 的 MTrk；导出预检查必须按第 23.12.7 节检测跨 MTrk 同 tick 的顺序敏感组合，并按 Root 产生汇总 Warning，不得移动 tick、合并 Track 或静默改序。
### 14.13.4 Running status
初版兼容档不使用 running status。每个 Channel Event 都必须显式写入 status byte。
该规则不得改变：
```text
tick
事件语义
Note On / Off
CC / Pitch Bend / Program
Port / Channel 分配
同 tick 语义排序
播放结果
```
读取后自校验发现依赖 running status 的 Midora 输出时，编码整体失败。
### 14.13.5 不做冗余状态事件折叠
初版 MIDI 导出不做冗余状态事件折叠。
规则：
```text
导出器必须输出 compiled result 中应输出的事件。
导出器不得为了减小文件体积删除状态事件。
```
因此不讨论跨 Note On / Note Off 的折叠安全条件。
### 14.13.6 不允许为了文件大小改变语义排序
导出器不得为了压缩或优化文件大小改变 `canonical compiled result` 的语义排序。
---
## 14.14 End Of Track 与导出末尾清理
### 14.14.1 End Of Track 位置
Conductor Track 与 Logical Unit Track 的 End Of Track 写到统一导出 `endTick`。Pure MIDI Track 使用其冻结 Track descriptor 的自身结束位置，以保留 Midi Segment 尾部空白；非零范围导出时先相对 `startTick` 重基并 clamp 到请求范围。

文件总长度由全部 MTrk 的最大 EOT 决定。结构有效的空 Pure MIDI Track 可以在 tick 0 写 EOT；不要求所有 Track 的 EOT tick 一致。
如果：
```text
endTick == startTick
```
End Of Track 写到同一个 tick。
### 14.14.2 endTick 不包含普通原始事件
由于导出范围采用：
```text
[startTick, endTick)
```
普通原始事件在 `endTick` 不导出。
### 14.14.3 范围硬裁剪补充事件
虽然 endTick 不包含普通原始事件，但范围硬裁剪需要的补充清理事件可以写在：
```text
endTick
或文件结束清理位置
```
例如：
```text
必要 Note Off
Reset
All Notes Off
All Sound Off
Reset All Controllers
```
这些属于编译器 / 导出器生成清理事件，不是范围内普通用户事件。
### 14.14.4 文件末尾不追加安全 Reset
初版导出器不得在 canonical compiled result 之外追加 Channel 清理事件。
规则：
```text
范围硬裁剪所需 Note Off / Reset 必须已经存在于 canonical compiled result
导出器只编码 canonical Channel Event
导出器额外生成的文件结构事件仅限本章规定的 Meta / 初始化内容与 End Of Track
```
如果 canonical 结果缺少所需清理，这是 compiled result 一致性错误，不得由导出器静默补救。
---
## 14.15 Readme
### 14.15.1 默认生成，但允许关闭
MIDI 导出默认生成 sidecar Readme。
规则：
```text
单文件导出：Readme 与 .mid 同目录。
多文件导出：Readme 位于导出文件夹根目录。
```
用户允许关闭 Readme 生成。
如果用户关闭 Readme：
```text
不存在 Readme 写入失败问题。
不存在因 Readme 写入失败导致导出失败的情况。
```
### 14.15.2 Readme 是被请求时的导出产物
当用户开启 Readme 生成时，Readme 是本次导出产物的一部分。
因此：
```text
.mid 写入成功但 Readme 写入失败，整体导出失败。
单文件导出时 Readme 写入失败，也视为整体失败。
如果已写出 .mid，应清理或提示未能清理。
```
### 14.15.3 Readme 格式
初版 Readme 固定使用 Markdown，文件名固定为：
```text
README.md
```
内容模板可以演进，但不得在输出规划冻结后由写入器改名。
### 14.15.4 Readme 内容
Readme 应记录以下信息：
```text
Project Metadata：项目名称、项目版本、作者、Remix 信息、版权信息等可用元数据
导出模式
导出范围：startTick、endTick、范围来源、是否 Project End Marker / 自然结束 / 手动范围
Track 选择集合：实际导出的 Track、被排除的 Track
Routing 信息：Compact / Preserve Routing、Port 映射、原始 Midora Port 对应关系
按 Port 导出时的原始 Port 与文件映射
TPQ
Tempo / Time Signature / Key Signature 事件摘要或数量
Channel 10 melodic 初始化说明
第三方播放器兼容性说明
导出相关 Warning / Info 摘要
文件清单
创建软件版本、最新保存软件版本、导出时软件版本
导出时间
```
SoundFont 是程序级本机音频设置，不属于 Project 或 MIDI 导出语义；Readme 不得记录程序级 SoundFont 路径、顺序、启用状态或推荐信息。
Project Metadata 的 `Notes` 字段必须是本次冻结 MIDI 导出编译结果中的准确 MIDI Note On 事件总数，来源固定为 Canonical Compiled Result；以 invariant 十进制单行字段输出，不得使用估算值、源对象数量或 Markdown 引用块格式。
Error 导致导出失败时：
```text
不生成成功 Readme。
```
Readme 的 Diagnostics 区按本次冻结编译结果的既有确定顺序，合计最多列出前 **1000 条 Warning / Info**。不按严重程度重排，也不聚合重复诊断。超过时在正文前明确显示准确总数、已展示数和省略数，并提示到 Midora 查看完整诊断；上述计数使用 Int64。无诊断、恰好 1000 条及更少时不得误报省略。

这个上限仅限制 README 文本；正式编译诊断仍完整保留，Error / Warning-as-error 判定使用全部诊断。准备、冻结和写入不得先展开整份超大报告再截断；允许保留有界文本前缀及精确总数。取消、编码或文件写入失败仍服从原有原子输出事务。

### 14.15.5 Readme 不影响 MIDI 语义
Readme 是辅助说明。
规则：
```text
Readme 不参与 MIDI 播放语义。
播放器不需要读取 Readme 才能播放 MIDI。
Readme 不用于还原路由语义。
```
### 14.15.6 文件哈希
初版不要求 Readme 记录文件哈希。
文件哈希可作为未来增强。

### 14.15.7 超长间隔填充摘要

若本次成功导出插入了 §14.12.2 的占位 Text Meta，README 必须在独立的兼容性摘要中记录：新增占位事件总数、涉及的 MTrk 数和输出文件数，并说明它们只用于编码超长 delta、原事件 Tick 与音乐数据未改变。只写汇总，不列出每个占位或间隔，不生成与占位数等长的诊断集合。

该摘要属于导出级 Info，不受 Warning-as-error 阻止，也不挤占 §14.15.4 的前 1000 条编译诊断展示名额；它不得改写 canonical 的诊断总数或 `Notes` 数。未插入时不显示多余提示。README 关闭时，导出结果摘要仍须提供这项汇总；导出失败不生成成功 README。
---
## 14.16 MIDI 导出任务参数
### 14.16.1 归属
MIDI 导出模式、范围、Track 选择、Routing、Readme 与 Warning-as-error 开关只属于当前导出任务 Draft。它们不是 Project Source Data，不进入 `.midora`、Project Undo / Redo、编译 fingerprint 或 Application Preferences。

### 14.16.2 初始值
每次打开 MIDI Export Dialog 时使用固定产品初始值：
```text
Mode = Whole Project
Range = Project Default Range
Track Selection = All Valid Logical and Pure MIDI Tracks
Routing = Compact
Include Readme = true
Treat Warnings as Errors = false
```
用户在对话框内的修改只在按 Start 后冻结给本次任务；Cancel 不产生任何持久状态。初版不提供 `Save as Project Defaults`。

### 14.16.3 本机路径
最近导出目录可以按用途保存为 Application Preference；它不是导出语义，不进入 Project，也不得改变最终 MIDI 字节。
## 14.17 文件命名与文件系统行为
### 14.17.1 文件名来源
初版固定命名模板：
```text
整曲导出：<ProjectStem>.mid
按 Logical Track 导出：<NN> - <LogicalTrackDisplayName>.mid
按 Port 导出：Port <PP>.mid
```

`ProjectStem` 按 Project 名称、当前 `.midora` 文件名 stem、固定 `Midora MIDI Export` 的顺序选择第一个非空、非仅空白且按第 14.17.4 节合法化后非空的候选。Windows 保留字符等可合法化内容不是跳过候选的理由；非法 UTF-16 仍使规划失败，不静默改用后续候选。

`NN` 使用该 Track 在整个 Project 当前手动排序中的一基显示序号；不按本次选择重编号，未选 Track 仍占序号，允许跳号。宽度至少两位，并按整个 Project Logical Track 总数的十进制位数增长。`LogicalTrackDisplayName` 优先使用原始 Logical Track 名称；名称为空、仅空白或按第 14.17.4 节合法化后为空时，固定使用 `Logical Track <Project 当前一基显示序号>`。`PP` 是原始一基 Port 编号，固定两位 `01`–`16`。

以上是进入第 14.17.4 节公共合法化器之前的候选 stem 模板；扩展名为固定系统输入。模板不修改 Project 源名称。
### 14.17.2 名称重复
如果 Logical Track 名称重复，按 Track 导出时：
```text
系统自动生成唯一文件名，必要时追加序号。
不要求 Track 名称唯一。
不自动重命名 Project 中的 Track。
```
### 14.17.3 文件名冲突
MIDI 导出必须与音频文件渲染共用同一套确定性安全文件名合法化与冲突检测规则。Review 必须在任务开始前显示合法化后的完整最终目标列表；不得只显示 Project / Track 原始名称或尚未展开的模板。

如果目标文件已存在：
```text
必须得到用户明确覆盖确认。
未确认不得覆盖。
```
多文件导出目标文件夹已存在时：
```text
若可能覆盖已有文件，必须提示用户确认。
用户取消则导出取消。
```
### 14.17.4 非法文件名字符
初版规则：
```text
Project / Logical Track 等源名称保持不变。
输出规划器从原始候选名称生成确定性的 Windows 安全文件名。
必须处理非法字符、保留设备名、尾部空格和句点、控制或不适合作为文件名的不可见字符、文件名部分过长、大小写不敏感冲突和 Unicode 规范化别名。
允许 Unicode 文件名，不强制转为 ASCII 或拼音。
合法化结果只属于本次冻结输出计划，不回写 Project，不重命名 Logical Track，也不进入 Undo / Redo。
```

MIDI 与音频必须调用同一公共合法化算法；相同候选文件名、扩展名预算和冲突上下文必须得到相同文件名部分。初版算法固定如下：

```text
候选 stem 与扩展名先转换为 Unicode NFC；非法 UTF-16 使输出规划失败
Win32 保留字符 < > : " / \ | ? *、Unicode General Category=Control，以及下列固定 code point / 范围均视为不安全：
U+00AD、U+061C、U+180E、U+200B、U+200E–U+200F、U+2028–U+2029、U+202A–U+202E、U+2060–U+206F、U+FEFF、U+FFF9–U+FFFB
每段连续不安全 code point 替换为单个 ASCII `_`；源字符串中原有的安全 `_` 不参与折叠
保留 U+200C ZWNJ、U+200D ZWJ、Variation Selector 和 emoji tag characters
删除 stem 首尾 ASCII Space U+0020，并删除 stem 尾部 ASCII Period U+002E；长度截断后再次执行尾部删除
```

Windows 设备保留名按大小写不敏感处理，检查 stem 第一个 `.` 之前的部分：

```text
CON、CONIN$、CONOUT$、PRN、AUX、NUL
COM1–COM9、COM¹、COM²、COM³
LPT1–LPT9、LPT¹、LPT²、LPT³
```

命中时在 stem 前固定增加 `_`。`COM0`、`COM10`、`LPT0` 等不属于该集合。扩展名是系统提供的固定输入，必须以 `.` 开头且自身已满足同一 NFC / 安全字符约束；扩展名不做猜测或静默修复。

同一目标目录内，在完成上述合法化和长度预算后，以 NFC + `OrdinalIgnoreCase` 判断内部目标冲突。候选必须先按调用方提供的稳定源顺序、再按稳定源 key 的 `Ordinal` 顺序分配：第一个保留无后缀名称，后续依次尝试 ` (2)`、` (3)`……；每次为当前后缀重新预算和截断 stem。若某个源名称本身占用了计划后缀名称，继续递增直到得到未使用名称。不得依赖输入集合枚举顺序。

已有文件系统目标不参加该序号分配，不得通过追加序号绕过覆盖确认。公共文件名算法不读取文件系统；现有目标检查和完整路径可用性检查在冻结文件名计划之后单独执行。

若安全合法化后仍无法形成唯一、合法、可表示的完整目标列表，预检查必须阻止任务并产生文件系统诊断。
### 14.17.5 路径过长
最终文件名部分固定最多 `255` 个 UTF-16 code unit，包括 stem、冲突后缀、分隔点和扩展名。算法按当前扩展名与当前冲突后缀预留预算，并只在 Unicode extended grapheme cluster / .NET text element 边界截断 stem；不得拆分 surrogate pair、组合序列、ZWJ 序列或其他文本元素。若预算无法容纳一个完整 stem 文本元素，则输出规划失败。

该合法化不得根据用户选择的父目录深度临时改变同一候选名称的结果。

如果合法化后的完整目标路径仍然过长：
```text
预检查阻止任务。
进入文件系统诊断。
不自动更换父目录，不根据完整路径临时再次缩短文件名，也不绕过失败继续部分导出。
```
### 14.17.6 Readme 文件名
Readme 候选文件名固定为 `README.md`，属于同一冻结输出目标列表并服从第 14.17.4 节；不得由写入器在规划后另行改名。
---
## 14.18 导出流程
### 14.18.1 播放期间触发导出
如果播放期间触发 MIDI 导出：
```text
1. 自动 Stop 当前播放任务；
2. 执行 Stop 清理；
3. 播放光标位置按 Stop Cursor Behavior 处理；
4. 进入 MIDI 导出流程；
5. 导出完成后保持 Stopped；
6. 不自动恢复播放。
```
Preparing / Buffering 期间触发导出：
```text
同样自动 Stop / 取消准备或 Buffering，清理后进入导出。
```
自动 Stop 清理失败时：
```text
提示风险并由用户确认后继续。
```
这是项目级导出操作的例外路径。
### 14.18.2 导出预检查
导出流程内部必须先完成预检查。
预检查至少覆盖：
```text
导出专用编译
编码可行性
文件名 / 路径风险
覆盖风险
Readme 写入风险
目标目录可用性
```
UI 可表现为正式导出前的预检查阶段。
预检查成功后到实际写入之间：
```text
不允许修改会影响导出语义的 Project 内容。
```
### 14.18.3 导出期间 Project 锁定
导出进行中禁止编辑会影响导出语义的 Project 内容。
包括但不限于：
```text
Conductor Track
Logical Track / Segment / Logical Note
MIDI Channel Root / Pure MIDI Track / Midi Segment / Direct MIDI Event
Logical Parameter Lane
Event Instrument / SubVoice / Mapping / Lifecycle
本次导出 Task Draft
```
允许查看已有诊断面板。
不允许执行会改变导出语义的编辑操作。
### 14.18.4 导出取消
导出过程中允许用户取消。
取消后：
```text
尽力停止导出。
清理临时文件。
不留下 partial 输出。
用户取消不算 Error。
可记录为用户取消的状态提示。
不修改 Project 内容。
不标记 Project 已修改。
```
### 14.18.5 导出自校验
MIDI 编码完成后需要基本自校验。
至少应检查：
```text
文件头
Track 数
MThd 声明的 Track 数必须可由 unsigned 16-bit `ntrks` 表示；超出时任务在写文件前失败
每个 delta time 在 0..0x0FFFFFFF 内；占位与原事件累计 Tick 不溢出，且原事件和 EOT 位置不变
事件可编码
每个 Track 恰有一个合法 End Of Track
Conductor/Logical Unit EOT 与统一 endTick 一致，Pure MIDI EOT 与冻结 descriptor 一致
每个 Channel Event 都有显式 status byte
每个事件 Track 的全部 Channel Event 使用同一个 Channel
Logical/Event Instrument 同一实际 Unit 只对应一个事件 Track
每个 Pure MIDI ExportTrackId 恰对应一个 MTrk；共享 Root Unit 的多个 MTrk 保持独立
每个 Track 数据区不超过 0xFFFFFFFF 字节，声明的 chunk 长度与实际字节一致，且未按大小拆分 Track
文件末尾不存在未声明字节
```
具体校验项实现设计阶段定义。
自校验失败：
```text
导出失败。
不写出最终文件。
进入导出编码诊断。
```
### 14.18.6 导出成功提示
导出成功后显示轻量摘要，例如：
```text
生成文件数量
导出范围
是否生成 Readme
超长间隔填充汇总（仅实际发生时）
```
导出成功后不自动打开输出文件夹。
可提供按钮让用户打开。
导出成功后不自动播放 MIDI，不自动打开 Readme，不自动保存 Project。
---
## 14.19 导出诊断
### 14.19.1 进入统一诊断系统
MIDI 导出诊断进入统一诊断系统。
导出诊断应区分：
```text
编译诊断
导出编码诊断
文件写入诊断
用户取消状态
```
### 14.19.2 诊断类别
导出编码错误来源至少分为：
```text
项目语义导致不可编码
编译器生成异常
导出器编码异常
文件系统异常
未知 compiled event 类型
```
### 14.19.3 源对象定位
导出诊断应尽量定位到 Project 源对象，例如：
```text
Logical Track
Segment
Logical Note
Logical Parameter
Event Instrument
SubVoice
Mapping Function
Conductor Track 事件
```
文件写入类错误则定位到：
```text
路径
文件
目录
```
### 14.19.4 compiled result 来源信息
`canonical compiled result` 应保留足够来源信息，使导出失败能追溯到：
```text
Project 源对象
编译器生成事件来源
范围起点状态恢复
范围结束硬裁剪清理
导出器安全清理
```
不得只保存 MIDI 字节而失去诊断定位能力。
### 14.19.5 MIDI 值域非法
MIDI 值域非法应优先在编译 / 语义验证阶段失败。
如果导出编码阶段仍发现非法值：
```text
也必须失败。
产生导出编码诊断。
```
不得自动 clamp、忽略或交给播放器处理。
### 14.19.6 未知 compiled event 类型
导出时遇到未知 compiled event 类型：
```text
导出失败。
说明导出器不支持该 compiled event 类型。
```
不忽略，不写为 Text Meta Event，不自动转换为 CC。
### 14.19.7 导出失败与播放状态机
MIDI 导出失败属于导出流程诊断。
规则：
```text
不进入播放 Error 状态。
不破坏已有有效编译缓存。
与失败导出上下文相关的无效缓存可丢弃。
```
导出失败后，用户可在诊断面板查看编译 / 编码 / 文件写入错误。

### 14.19.8 SMF 边界诊断

满足 §14.12.2 填充规则且字节预算可容纳的超长 delta 不产生 Error/Warning，只产生汇总 Info。MTrk 大小、MTrk 数量、单条 payload 编码超限等必须是明确的导出编码 Error，不是“编译失败”或普通文件 I/O 错误。信息至少说明限制名称、上限、实际值或已经足以证明超限的字节下界，以及可用的文件/Track/事件位置；不能为了报告完整预计大小而继续扫描已确定失败的整条 Track。

错误归属必须贯穿立即编码与分页延迟写入两条路径。语义验证已拒绝的非法 TPQ、Tempo、事件值等仍为原编译/语义诊断，不因这次文件编码兼容而放宽。具体诊断代码由实施时统一定义，不得把异常类名或堆栈作为用户唯一说明。
---
## 14.20 缓存、确定性与进度
### 14.20.1 编码缓存
导出后可以缓存编码结果。
规则：
```text
缓存不得成为语义来源。
Project 或本次导出参数变化后必须失效。
```
初版不要求导出缓存跨软件版本复用。
软件版本变化应保守失效。
### 14.20.2 确定性
在相同条件下：
```text
相同 Project
相同导出设置
相同软件版本
```
MIDI 事件语义和排序必须确定。
但如果 Readme 包含导出时间：
```text
Readme 不要求字节级完全一致。
```
### 14.20.3 导出进度
长时间导出应提供进度状态。
至少区分：
```text
编译
编码
写文件
```
导出进度是运行期 UI 状态：
```text
不保存进 Project。
不进入 Undo / Redo。
不写入 Readme。
```
---
## 14.21 Project 内容、副作用与 Undo / Redo
### 14.21.1 导出动作本身不修改 Project
MIDI 导出动作本身：
```text
不修改 Project 内容。
不标记 Project 已修改。
不进入 Undo / Redo。
不自动保存 Project。
不更新 Project 修改时间。
```
本次导出 Task Draft 不属于 Project 内容。
### 14.21.2 导出参数修改
Dialog 内修改只影响本次任务 Draft，不进入 Undo / Redo、不标记 Project Modified；Cancel 丢弃 Draft。
### 14.21.3 工程总耗时
按第 3.6.4 节的初版累计规则：
```text
MIDI 导出的配置、Preparing、执行、取消和结果阶段均正常累计工程总耗时。
```
但导出本身不是 Project 内容修改。
### 14.21.4 最近导出记录
MIDI 导出可以更新软件级最近导出记录。
该记录：
```text
不属于 Project 内容。
不进入 Undo / Redo。
不影响 Project 修改状态。
```
---
## 14.22 第三方播放器兼容性边界
Midora 导出标准 MIDI 1.0 数据。
但 Midora 不承诺所有第三方播放器完全一致复现，尤其涉及：
```text
多 Port 解释
Port Number / Device / Port Name Meta Event
Channel 10 melodic 初始化
系统写入的初始化事件
SoundFont 选择
非 GM SF2
播放器自身对 RPN / NRPN / Pitch Bend Range 的解释
不同播放器的同 pitch 重叠 Note 配对策略
同一 Pure MIDI Root 跨 MTrk 的同 tick 消费顺序
第三方软件是否保留 Midora Sequencer-Specific Meta
```
Readme 应明确说明该兼容边界。
MIDI 导出不依赖：
```text
当前播放设备
当前 BASSMIDI Stream 状态
当前播放 buffer
```
---
