# 第 8 章 SubVoice 与 MIDI 事件编辑

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 SubVoice 的资源与事件归属，以及 Note、CC、Program、Bank、Pitch Bend、RPN/NRPN、曲线 Lane、Initial State 和同 tick 基础事件语义。

## 8.1 SubVoice 的系统级定义
SubVoice 是 `Event Instrument` 内部的独立子声部 / 子通道结构。
系统级规则：
```text
SubVoice 属于 Event Instrument 定义内部
SubVoice 不独立存在于 Event Instrument Library 中
SubVoice 不属于 Logical Track
SubVoice 不属于 Segment
SubVoice 不属于程序级 SoundFont 列表配置
SubVoice 不是 SF2 preset
SubVoice 不是 MIDI Track
SubVoice 接近于 Event Instrument 内部的一条原始 MIDI Channel 抽象
```
每条 SubVoice 可以拥有自己的事件内容入口。
这些事件在编译后只作用于该 SubVoice 被分配到的 Channel Unit。
---
## 8.2 SubVoice 与 Channel Unit 的关系
初版采用严格的一一对应资源模型：
```text
一条 SubVoice = 一次 Event Instrument Instance 中的一个 Channel Unit 需求
```
因此：
```text
Event Instrument 的 SubVoice 数量 = 单个 Event Instrument Instance 原则上所需 Channel Unit 数量
```
初版不允许：
```text
多个 SubVoice 共享同一个 Channel Unit
空 SubVoice 自动忽略资源需求
Note-only SubVoice 自动合并
用户手动标记 SubVoice 可共享
编译器基于事件内容自动压缩 SubVoice 数量
```
无输出整体优化是否存在，由 第 12 章《编译系统与 Canonical Compiled Result》 编译系统细化。
要求：
```text
只要 SubVoice 存在，系统级语义上原则计入 SubVoice 数量与单实例 Channel Unit 需求。
```
---
## 8.3 SubVoice 数量上限
初版在编辑阶段硬限制单个 Event Instrument 最多包含：
```text
256 条 SubVoice
```
理由：
```text
Midora 初版最多支持 256 个 Channel Units
一条 SubVoice 在一次实例中对应一个 Channel Unit
单个 Event Instrument Instance 不可能合法需要超过 256 个 Channel Units
```
当用户尝试创建第 257 条 SubVoice 时：
```text
操作失败，并提示已达到初版上限。
```
如果未来引入 SubVoice 共享、冻结、分层渲染或特殊优化，必须重新设计该限制。
---
## 8.4 SubVoice 稳定 ID
每条 SubVoice 必须拥有内部稳定 ID。
稳定 ID 用于：
```text
内部引用
诊断定位
复制
排序
事件归属
映射引用
Initial State 归属
后续可能的 Envelope / Loop / Reset 相关定位
```
系统不得只依赖数组顺序识别 SubVoice。
SubVoice 重排不应破坏内部引用。
复制 SubVoice 时必须生成新的稳定 ID。
---
## 8.5 SubVoice 名称
SubVoice 支持用户可见名称，但名称可选。
规则：
```text
SubVoice 名称可为空
SubVoice 名称允许用户编辑
SubVoice 名称不作为内部身份
SubVoice 内部身份基于稳定 ID
SubVoice 名称为空不产生诊断
```
当名称为空时，UI 可显示自动标签，例如：
```text
SubVoice 1
SubVoice 2
SubVoice 3
```
自动标签是显示辅助，不应作为持久身份依据。
本章不强制 SubVoice 名称在 Event Instrument 内唯一。
如果后续诊断或 UI 需要避免混淆，可以同时显示：
```text
用户名称 + 自动序号
```
---
## 8.6 创建 SubVoice
用户可以在 Event Instrument 内新增 SubVoice。
新增 SubVoice 时：
```text
创建新的 SubVoice 稳定 ID
默认名称为空
默认不生成任何 MIDI 事件
默认不生成 Program Change
默认不生成 Bank Select
默认不生成 Note On / Note Off
默认不生成 CC / Pitch Bend / RPN / NRPN / Pitch Bend Range
新增操作进入全项目撤销 / 重做
新增操作使 Project 进入已修改状态
```
新增空 SubVoice 是合法状态。
---
## 8.7 新建 Event Instrument 的默认 SubVoice
创建新的 Event Instrument 时，系统默认生成一条 SubVoice。
该默认 SubVoice 为空。
因此新建 Event Instrument 的默认状态仍符合本规格规则：
```text
结构合法
可保存
可绑定
可复制
可删除
可进入编辑器
但可能不发声
```
---
## 8.8 删除 SubVoice
用户可以删除 SubVoice，但初版不允许删除最后一条 SubVoice。
删除规则：
```text
允许删除任意非最后一条 SubVoice
如果 SubVoice 内包含事件或其他内部数据，删除前必须弹出确认
删除 SubVoice 时，其内部事件、映射引用、Initial State 设置等数据一并删除
删除操作进入全项目撤销 / 重做
撤销删除时应恢复该 SubVoice 及其内部数据
删除操作使 Project 进入已修改状态
```
不采用以下设计：
```text
删除 SubVoice 但保留孤立数据
只允许删除空 SubVoice
删除后保留隐藏恢复槽
```
---
## 8.9 复制 SubVoice
初版支持在同一个 Event Instrument 内复制 SubVoice。
复制规则：
```text
复制必须是深拷贝
复制出的 SubVoice 获得新的稳定 ID
复制出的 SubVoice 与原 SubVoice 后续独立编辑
复制不共享会导致互相影响的内部可变对象
复制操作进入全项目撤销 / 重做
复制操作使 Project 进入已修改状态
```
复制内容应包括该 SubVoice 内部的事件内容、显示相关设置、Initial State 设置，以及相关章节规定属于 SubVoice 的其他数据。
具体字段由 第 16 章《.midora 文件格式与持久化》 或实现层细化。
---
## 8.10 SubVoice 手动排序
初版支持用户手动排序 SubVoice。
规则：
```text
SubVoice 顺序属于项目内容
SubVoice 顺序保存进 Project
SubVoice 排序进入全项目撤销 / 重做
SubVoice 排序使 Project 进入已修改状态
```
SubVoice 顺序影响：
```text
编辑器展示顺序
诊断显示顺序
确定性事件排序中的辅助顺序
Channel Group 中 SubVoice 映射顺序
```
SubVoice 顺序不应直接成为资源分配优先级策略。
具体 Channel Unit 分配算法由第 12 章《编译系统与 Canonical Compiled Result》规定。
---
## 8.11 SubVoice 顺序与编译语义
SubVoice 顺序可以影响确定性输出中的排序稳定性，但不应改变事件乐器的音乐语义本身。
系统级规则：
```text
SubVoice 顺序可用于保证同 tick 事件输出顺序稳定
SubVoice 顺序可用于诊断和导出可读性
SubVoice 顺序不等于用户指定 Channel Unit 分配优先级
SubVoice 顺序不允许绕过编译器资源分配规则
```
---
## 8.12 Event Instrument Root Note 与 SubVoice Effective Root Note
Event Instrument 级仍保留 `Root Note`。
但 第 8 章《SubVoice 与 MIDI 事件编辑》 对本规格其他章节进行细化修正：
```text
Event Instrument 级不提供完整音高映射规则编辑入口。
Event Instrument 级只提供 Root Note，作为默认基准。
实际音高映射规则归属于 SubVoice。
```
每条 SubVoice 采用 `Effective Root Note` 概念。
规则：
```text
每条 SubVoice 都有 Effective Root Note
默认情况下，SubVoice Effective Root Note = Event Instrument Root Note
如果 SubVoice 独立设置了 Root Note，则 SubVoice Effective Root Note = SubVoice Root Note
```
当 SubVoice 使用独立 Root Note 时，界面上必须有明确的视觉标记，用于提示用户该 SubVoice 不再继承 Event Instrument Root Note。
---
## 8.13 Event Instrument Root Note 修改时的继承规则
当用户修改 Event Instrument Root Note 时：
```text
继承状态的 SubVoice 自动使用新的 Event Instrument Root Note
已独立设置 Root Note 的 SubVoice 不受影响
```
这要求 UI 明确区分：
```text
继承 Event Instrument Root Note 的 SubVoice
独立设置 Root Note 的 SubVoice
```
---
## 8.14 SubVoice Root Note 修改规则
当用户修改 SubVoice Root Note 时：
```text
不自动重写该 SubVoice 内已有 Note 事件
只改变之后编译时的 pitchDelta 计算
```
不采用：
```text
自动整体平移该 SubVoice 内所有 Note
自动保持实际输出音高不变
默认弹窗询问是否重写
```
如果未来需要“修改 Root Note 并重映射已有事件”的能力，应作为显式命令设计，而不是 Root Note 修改的默认副作用。
---
## 8.15 SubVoice 默认音高映射规则
实际音高映射规则归属于 SubVoice。
默认 pitchDelta 计算规则：
```text
pitchDelta = Logical Track 触发 Note - SubVoice Effective Root Note
```
默认 Note 编译规则：
```text
compiledNote = SubVoice 内模板 Note + pitchDelta
```
示例：
```text
Event Instrument Root Note = 60
SubVoice 继承 Root Note，因此 SubVoice Effective Root Note = 60
Logical Track 触发 Note = 64
pitchDelta = +4
SubVoice 内模板 Note = 36
编译后 Note = 40
```
如果 SubVoice 独立设置 Root Note：
```text
Event Instrument Root Note = 60
SubVoice Root Note = 72
SubVoice Effective Root Note = 72
Logical Track 触发 Note = 76
pitchDelta = +4
SubVoice 内模板 Note = 36
编译后 Note = 40
```
---
## 8.16 默认音高映射的作用范围
初版默认音高映射只作用于 SubVoice 内的 Note number。
规则：
```text
默认只平移 SubVoice 内选择跟随 pitchDelta 的 Note 事件
Pitch Bend / CC / RPN / NRPN / Program / Bank 等复杂映射不受默认 Note 平移规则影响
复杂映射由 第 9 章《曲线、Logical Parameter 与映射》 显式映射系统处理
```
默认规则应简单、可预测。
例如，用户可以在后续映射系统中设计：
```text
Logical Track 上 Note 音高改变
不改变 SubVoice 的 Note 音高
只改变 Pitch Bend
```
这种情况不应被当作 Note number 越界处理。
---
## 8.17 Note 事件是否跟随 pitchDelta
初版允许每个 Note 事件设置是否跟随 pitchDelta。
系统级规则：
```text
SubVoice 内的 Note 事件可以选择跟随 pitchDelta
SubVoice 内的 Note 事件也可以选择固定音高，不跟随 pitchDelta
```
用途：
```text
固定音高噪声层
固定打击层
固定触发音
只通过 Pitch Bend / CC / 其他事件表达音高变化的特殊事件乐器
```
具体 UI、默认值、批量设置方式和事件字段结构由 第 9 章《曲线、Logical Parameter 与映射》 / 第 17～20 章的 UI 与交互规格 细化。
---
## 8.18 Note 编译结果越界
如果默认 Note 平移或显式音高映射最终产生非法 MIDI note number：
```text
小于 0
大于 127
```
则编译失败，并尽量定位到：
```text
Logical Track
Segment
触发 Note
Event Instrument
SubVoice
模板 Note
映射规则
最终非法 Note 值
```
系统不得自动 clamp 到 0–127。
系统不得自动丢弃该 Note。
系统不得作为警告继续编译。
说明：
```text
该规则只针对最终实际要输出的 MIDI Note number 越界。
如果用户通过映射规则选择不改变 SubVoice 的 Note 音高，而是改用 Pitch Bend 等方式表达逻辑音高变化，则不适用本条 Note 越界规则。
```
Pitch Bend、CC、RPN、NRPN 等其他事件值的合法范围与越界处理由 第 9 章《曲线、Logical Parameter 与映射》 / 第 12 章《编译系统与 Canonical Compiled Result》 / 第 15 章《音频文件渲染》 细化。
---
## 8.19 音高映射归属澄清
正式规则如下：
```text
Event Instrument 级只保留 Root Note 作为默认基准。
实际音高映射规则归属于 SubVoice。
每条 SubVoice 使用自己的 Effective Root Note 计算 pitchDelta。
```
这不是删除 Root Note，也不是删除 Note → Event 映射能力，而是明确映射规则的实际归属层。
---
## 8.20 Template Length 与 SubVoice
SubVoice 不拥有独立 Template Length。
规则：
```text
Template Length 只属于 Event Instrument
SubVoice 内事件必须受 Event Instrument Template Length 约束
SubVoice 事件位置不得要求独立延长模板长度语义
```
如果用户在某条 SubVoice 中添加或移动事件，导致事件位置超过当前 Event Instrument Template Length，则按 Event Instrument Template Length 规则自动扩展。
不允许：
```text
每条 SubVoice 拥有自己的 Template Length
每条 SubVoice 拥有独立生命周期长度
SubVoice 局部长度影响 Rendered Instance Length
```
这样可以避免 Template Length、Gate Length、Rendered Instance Length 在生命周期系统中出现多层冲突。
---
## 8.21 生命周期策略与 SubVoice
初版不允许 SubVoice 覆盖 Event Instrument 的生命周期策略。
规则：
```text
短音策略属于 Event Instrument 级
长音策略属于 Event Instrument 级
Template Length 与 Gate Length 的关系属于 Event Instrument 级
Rendered Instance Length 的主规则属于 Event Instrument 级
```
SubVoice 不独立决定：
```text
Cut At Note Off
One-Shot / Ignore Note Off
Note Off With Tail Events
Hold Last State Until Note Off
End At Template Length
```
具体生命周期状态机由第 10 章《实例生命周期、Loop、Envelope 与重叠》规定。
---
## 8.22 Reset Defaults 与 Initial State Defaults 的概念拆分
本章修正本规格其他章节中关于 Event Instrument / SubVoice 级 Reset 覆盖的表述。
必须区分两个概念：
| 概念 | 归属 | 作用时机 | 作用 |
|---|---|---|---|
| Reset Defaults | Project / 系统级 | Segment 所属 lane 首次激活、非重叠复用激活，以及裁剪、Segment End、消费者范围结束等硬边界 | 在 lane 开始被消费前建立确定基线，并在硬边界完成安全清理 |
| Initial State Defaults | Event Instrument / SubVoice 可定义 | 实例开始时 | 建立该实例期望的初始 MIDI 状态 |
---
## 8.23 Lane 激活与硬边界 Reset 的归属
Reset 是 lane 激活基线或硬边界资源清理语义，不是事件乐器个性语义。
因此：
```text
Event Instrument 不覆盖 Project Reset Defaults
SubVoice 不覆盖 Project Reset Defaults
Segment 所属 lane 首次激活、非重叠复用激活，以及裁剪、Segment End 或消费者范围结束处的 Reset 目标值，由 Project 级 / 系统级 Reset Defaults 统一管理
普通 Gate/Release/Tail/instance/allocation-group 结束只执行必要的精确 NoteOff，不执行通用目标 Reset
```
理由：
```text
Channel Unit 是编译器临时分配的资源
Channel Unit 不属于某个 Event Instrument 或 SubVoice
Lane 激活前的确定基线与硬边界后的安全状态应由项目级规则统一决定
Event Instrument / SubVoice 不应决定 Channel Unit 应回到什么 CC / Pitch Bend / RPN / NRPN 基线
普通实例结束不清除仍可能被后续实例有意保持或继续使用的 Channel-Wide 状态
```
不采用以下模型：
```text
SubVoice 结束后把 Expression Reset 到 90
另一个 Event Instrument 结束后把 Expression Reset 到 120
Event Instrument 自己定义 Channel Unit 释放状态
```
这种设计会污染资源层职责，使 Channel Unit 的复用状态变得混乱。
---
## 8.24 Initial State Defaults
Event Instrument 和 SubVoice 初版均开放 Initial State Defaults 编辑。
Initial State Defaults 表示该实例开始时希望主动写入的默认 MIDI 状态。
例如：
```text
Expression = 80
Modulation = 0
Pitch Bend = center
Program = 12
Bank Select = 0
Pitch Bend Range = 12 semitones
```
Initial State Defaults 属于模板开始语义。
它不是结束后 Reset。
---
## 8.25 Initial State Defaults 的层级
Initial State Defaults 的优先级为：
```text
SubVoice Initial State Defaults
> Event Instrument Initial State Defaults
> Project Initial State Defaults
> 软件内置初始默认值
```
示例：
```text
Project Initial:
Expression = 127
Event Instrument Initial:
Expression = 80
SubVoice 2 Initial:
Expression = 90
```
则：
```text
SubVoice 1 实例开始时写入 Expression = 80
SubVoice 2 实例开始时写入 Expression = 90
其 Segment lane 首次激活或非重叠复用激活时，先 Reset 到 Project Reset Defaults
普通实例结束后不做通用目标 Reset
```
---
## 8.26 Initial State Defaults 支持的事件类型
初版 Initial State Defaults 支持以下类型：
```text
Control Change
Pitch Bend
Program Change
Bank Select
RPN
NRPN
Pitch Bend Range
```
具体每种类型的参数范围、编辑方式、排序和 MIDI 展开规则，由 第 9 章《曲线、Logical Parameter 与映射》 / 第 10 章《实例生命周期、Loop、Envelope 与重叠》 / 第 12 章《编译系统与 Canonical Compiled Result》 / 第 14 章《MIDI 导出》 细化。
---
## 8.27 Initial State Defaults 与“使用过的事件”
Initial State Defaults 写入的事件类型视为该实例使用过。
因此：
```text
如果 Initial State Defaults 写入某类 Channel-Wide 状态
则该目标进入 Segment lane 的已使用目标闭包
该 lane 激活时先按 Project Reset Defaults 建立该目标基线
Segment End 或消费者范围结束等硬边界再按 Project Reset Defaults 清理该目标
```
理由：
```text
开始时主动写入 Channel-Wide 状态，会改变 Channel Unit 状态
激活前若不建立基线，结果会依赖该 Channel Unit 的历史状态
硬边界若不清理，会让状态越过正式消费范围
普通实例结束不清理；后续非重叠复用由下一次 lane 激活建立新基线
```
---
## 8.28 Initial State 与用户 tick 0 手动事件冲突
如果 Initial State Defaults 与用户在该 SubVoice 的 tick 0 手动画的同类事件冲突：
```text
用户手动画的 tick 0 事件优先
Initial State 中同类事件不再插入
```
示例：
```text
SubVoice Initial State:
Expression = 80
用户在 SubVoice tick 0 手动画：
Expression = 100
```
编译时应采用：
```text
Expression = 100
```
而不是同时插入两个同 tick Expression 事件。
具体“同类事件”的判定规则由 第 9 章《曲线、Logical Parameter 与映射》 / 第 12 章《编译系统与 Canonical Compiled Result》 细化。
---
## 8.29 Initial State 插入位置
Initial State Defaults 应插入在该 Segment-owned lane 首次激活或非重叠复用激活的实例开始 tick，并且位于该 SubVoice 普通事件之前。
系统级语义：
```text
先按目标闭包建立 Project Reset Defaults 基线
再建立初始状态
再播放模板事件
```
如果用户在 tick 0 手动画了同类事件，则按上一节规则由用户事件优先，Initial State 同类事件不插入。

共享 lane 内仍有生命周期重叠的后续实例不重新执行 Reset Defaults 或 Initial State；需要独立 Channel-Wide 起点状态的重叠实例必须启用 Channel Isolation。
---
## 8.30 Initial State 与 Template Length
Initial State Defaults 不影响 Template Length。
规则：
```text
Initial State 是实例开始状态注入
Initial State 不算模板内部普通事件位置
Initial State 不导致 Template Length 自动扩展
Initial State 不决定 Template Length 默认值
```
Template Length 仍由 Event Instrument 普通模板事件与本规格其他章节 Template Length 规则决定。
---
## 8.31 Reset 与 Initial State 归属澄清
正式规则：
```text
Event Instrument / SubVoice 不覆盖 Project 级 Reset Defaults。
Event Instrument / SubVoice 可拥有实例开始时 Initial State Defaults 入口。
Reset Defaults 与 Initial State Defaults 是两个不同概念：
- Reset Defaults 用于 Segment lane 激活前建立确定基线，以及硬边界资源清理；
- Initial State Defaults 用于实例开始时建立该模板期望的初始 MIDI 状态。
- 普通 Gate/Release/Tail/instance/allocation-group 结束不执行通用目标 Reset。
```
---
## 8.32 SubVoice 内事件归属
SubVoice 内事件容器是 Event Instrument 事件内容的主要归属。
初版中，以下普通 MIDI / Midora 内置高级事件均归属于具体 SubVoice：
```text
Note On / Note Off
Control Change
Pitch Bend
Program Change
Bank Select
RPN
NRPN
Pitch Bend Range
其他后续明确支持的 SubVoice 级高级事件
```
系统级规则：
```text
所有普通 MIDI / 高级事件都必须归属于某条 SubVoice
Event Instrument 级不直接容纳普通 MIDI / 高级事件
SubVoice 内非 Note MIDI / 高级事件参数可以作为 Logical Parameter Mapping 的目标
```
---
### 8.32.1 与 Logical Parameter Mapping 的关系
Logical Parameter Mapping 的目标不直接是 SubVoice 本身，而是 SubVoice 内具体非 Note MIDI / 高级事件参数。
系统级规则：
```text
Logical Parameter Mapping 可以指向 SubVoice 内 CC、Pitch Bend、Program、Bank、RPN、NRPN、Pitch Bend Range 等非 Note MIDI / 高级事件参数。
Logical Parameter Mapping 不改变 SubVoice 与 Channel Unit 的一一对应资源模型。
Logical Parameter Mapping 不允许用户手动指定 SubVoice 使用固定 Port 或 Channel。
Logical Parameter Mapping 的执行语义、状态继承和诊断细节由 第 9 章《曲线、Logical Parameter 与映射》 / 第 11 章《Logical Track、Segment 与编曲语义》 / 第 12 章《编译系统与 Canonical Compiled Result》 继续细化。
```
## 8.33 Event Instrument 级普通事件禁止规则
初版不允许存在不归属于任何 SubVoice 的普通 MIDI / 高级事件。
不支持：
```text
Event Instrument 级全局 Note
Event Instrument 级全局 CC
Event Instrument 级全局 Pitch Bend
Event Instrument 级全局 Program Change
Event Instrument 级全局 Bank Select
Event Instrument 级全局 RPN / NRPN
Event Instrument 级全局 Pitch Bend Range
```
理由：
```text
SubVoice 接近 MIDI Channel 抽象
SubVoice 与 Channel Unit 一一对应
普通 MIDI Channel 事件必须有明确目标 Channel Unit
如果允许 Event Instrument 级普通事件，会破坏事件归属与 Channel Unit 对应关系
```
Event Instrument 级可以拥有 Root Note、Template Length、生命周期策略、Overlap 策略、Mapping Function 集合、Envelope Preset 集合、Initial State Defaults 等定义级设置，但不直接容纳普通 MIDI 事件。
---
## 8.34 SubVoice 内事件作用范围
SubVoice 内的 MIDI / 高级事件严格只作用于该 SubVoice 编译后获得的 Channel Unit。
规则：
```text
SubVoice 内事件不得指定作用于其他 SubVoice
SubVoice 内事件不得跨 SubVoice 写入
SubVoice 内事件不得直接指定固定 Port / Channel
SubVoice 内事件不得绕过 Channel Unit 分配系统
```
不支持：
```text
一个 SubVoice 内事件写入另一个 SubVoice 的 Channel Unit
跨 SubVoice 事件
SubVoice 手动目标 Channel
SubVoice 手动目标 Port
```
这保持 SubVoice 与 Channel Unit 的对应关系清晰。
---
## 8.35 Mapping Function 归属
Mapping Function 集合属于 Event Instrument 内部资源。
SubVoice 内事件可以引用 Event Instrument 级 Mapping Function。
规则：
```text
Mapping Function 不属于 Project 全局资源
Mapping Function 不属于单条 SubVoice 私有集合
Mapping Function 名称在单个 Event Instrument 内不可重复
SubVoice 内事件可以引用同一 Event Instrument 内的 Mapping Function
复制 Event Instrument 时，Mapping Function 作为 Event Instrument 内部资源被深拷贝
复制 SubVoice 时，应保持其内部事件对 Mapping Function 的引用语义
```
初版不为每条 SubVoice 建立独立 Mapping Function 集合。
理由：
```text
避免同名、复制、引用和诊断复杂化
符合本规格规定 Mapping Function 名称在单个 Event Instrument 内不可重复的规则
```
具体 Mapping Function 签名、Context、返回值、编译方式和错误处理由第 9 章《曲线、Logical Parameter 与映射》规定。
---
## 8.36 SubVoice 与 Port / Channel 的关系
SubVoice 不允许用户手动指定固定 Port 或 Channel。
规则：
```text
SubVoice 不持有固定 Port 设置
SubVoice 不持有固定 Channel 设置
SubVoice 不允许高级模式固定 Port / Channel
SubVoice 对应的 Channel Unit 完全由编译器分配
```
这承接本规格其他章节已经确认的资源系统规则。
---
## 8.37 SubVoice 与 SoundFont 的关系
SubVoice 不允许绑定独立 SF2。
SubVoice 不允许绑定 SF2 preset。
规则：
```text
SoundFont 是程序级有序 Enabled SF2/SFZ 列表及目标映射
所有实际使用 Unit 使用同一次任务冻结的相同列表和顺序
SubVoice 只包含 MIDI 事件和 Midora 内置高级事件
SubVoice 不管理声音资源
SubVoice 不显示或选择 SF2 preset
```
如果 SubVoice 内包含 Program Change / Bank Select，它们仍是 MIDI 事件，不是 SF2 资源绑定。
---
## 8.38 SubVoice 的 Program / Bank 默认值
新建 SubVoice 不自动创建 Program / Bank 事件。
规则：
```text
Program Change 必须由用户显式添加
Bank Select 必须由用户显式添加
SubVoice 不要求必须有 Program
SubVoice 不要求必须有 Bank
SubVoice 不从 SoundFont 自动选择 preset
```
这样可以保持“用户显式事件决定使用范围”的原则，避免自动事件扩大 Reset、Channel-Wide 冲突和诊断范围。
如果用户希望在实例开始时自动写入 Program / Bank，应通过 Initial State Defaults 明确设置。
---
## 8.39 SubVoice 级 Mute / Solo
初版支持 SubVoice 级 Mute / Solo，但仅限 Event Instrument 编辑器内部预览。
规则：
```text
Mute / Solo 只影响 Event Instrument 编辑器内部的局部试听 / 预览
Mute / Solo 不影响正式编译
Mute / Solo 不影响整曲播放
Mute / Solo 不影响 MIDI 导出
Mute / Solo 不影响音频渲染
Mute / Solo 不影响 Logical Track 中该 Event Instrument 的实际使用结果
```
系统级解释：
```text
Event Instrument 编辑器可以视为事件乐器内部的一层原始 MIDI 编辑器。
在这一层中，SubVoice 接近 MIDI Channel 的概念。
Mute / Solo 是该内部编辑器中的监听辅助状态，不是工程编译语义。
```
Mute / Solo 不保存进 Project。
Mute / Solo 是临时编辑器状态。
初版不保存 UI 视图状态，因此 Mute / Solo 不作为项目内容保存。
---
## 8.40 SubVoice Solo 作用范围
当某条 SubVoice Solo 时：
```text
只在当前 Event Instrument 编辑器预览中，仅播放 Solo 的 SubVoice。
```
Solo 不影响：
```text
所有引用该 Event Instrument 的 Logical Track 预览
整曲播放
正式编译
MIDI 导出
音频渲染
```
---
## 8.41 多条 SubVoice Solo
初版允许多条 SubVoice 同时 Solo。
预览时播放所有 Solo 的 SubVoice。
这更接近常见音频 / MIDI 编辑器行为。
---
## 8.42 Mute 与 Solo 的优先级
如果某条 SubVoice 既被 Mute，又处于 Solo 集合中：
```text
Mute 优先。
```
即该 SubVoice 在 Event Instrument 编辑器内部预览中不发声。
Mute 被视为硬静音。
---
## 8.43 Event Instrument 预览默认触发音高
在 Event Instrument 编辑器内部预览整个 Event Instrument 时，默认触发音高使用：
```text
Event Instrument Root Note
```
理由：
```text
预览整个 Event Instrument 时，应以 Event Instrument 的整体根音为默认基准
这能让继承 Root Note 的 SubVoice 以原始状态发声
也能让独立 Root Note 的 SubVoice 按其 Effective Root Note 语义正常计算 pitchDelta
```
具体预览音高是否允许用户临时修改，由 第 13 章《播放与预览》 / 第 17～20 章的 UI 与交互规格 细化。
---
## 8.44 单独预览某条 SubVoice 的默认触发音高
如果用户只预览某条 SubVoice，默认触发音高使用：
```text
该 SubVoice 的 Effective Root Note
```
理由：
```text
单独预览 SubVoice 时，应能直接听到该 SubVoice 在自己根音下的原始状态
```
---
## 8.45 SubVoice 颜色
初版不支持 SubVoice 独立颜色。
SubVoice 显示颜色可继承 Event Instrument 颜色，或由 UI 使用自动显示规则处理。
SubVoice 颜色不作为初版项目内容。
---
## 8.46 空 SubVoice
空 SubVoice 是合法状态。
允许存在以下情况：
```text
SubVoice 没有任何事件
SubVoice 没有 Note On / Note Off
SubVoice 只有 Initial State Defaults 但没有普通模板事件
SubVoice 只有后续可能的设置但没有 MIDI 输出事件
Event Instrument 中存在一条或多条空 SubVoice
```
空 SubVoice 本身不导致结构错误。
---
## 8.47 空 SubVoice 的诊断
如果空 SubVoice 所属的 Event Instrument 被实际编译使用，空 SubVoice 应产生信息级诊断。
理由：
```text
空 SubVoice 原则上仍计入 SubVoice 数量与单实例 Channel Unit 需求
这可能造成资源浪费
但空 SubVoice 是合法状态，不应导致警告或编译失败
```
诊断等级为信息，不是警告，也不是错误。
如果整个 Event Instrument 最终不会产生任何 MIDI 输出，其整体诊断等级由第 15 章《音频文件渲染》规定。
---
## 8.48 SubVoice 正式禁用状态
初版没有 SubVoice 正式禁用状态。
不允许：
```text
禁用某条 SubVoice，使其不参与正式编译
禁用某条 SubVoice，使其不参与整曲播放
禁用某条 SubVoice，使其不参与 MIDI 导出
禁用某条 SubVoice，使其不参与音频渲染
```
初版只支持 Event Instrument 编辑器内部预览用的 Mute / Solo。
Mute / Solo 不等同于正式 Disable。
---
## 8.49 SubVoice 诊断定位
诊断信息应尽量定位到具体 SubVoice。
涉及以下问题时，应尽量显示：
```text
Project
Event Instrument
SubVoice
事件位置
事件类型
Logical Track / Segment / Note，若该问题发生于实际编译使用路径
```
应尽量定位到 SubVoice 的问题包括：
```text
SubVoice 内非法事件值
SubVoice 内映射引用错误
SubVoice 内 Initial State 设置错误
SubVoice 资源需求相关问题
SubVoice 导致的 Channel Unit 占用问题
SubVoice 内事件与 Per-Note Instance Isolation 的兼容性问题
SubVoice 内事件导致的 Channel-Wide 状态污染风险
SubVoice 默认 Note 平移或显式映射导致的最终 MIDI Note 越界
空 SubVoice 被实际编译使用造成的信息级资源提示
```
只定位到 Event Instrument 不足以满足初版诊断需求。
---
## 8.50 SubVoice 内事件归属与 Lane 模型
### 8.50.1 事件归属
初版中，普通 MIDI 事件和 Midora 内置高级事件均归属于具体 `SubVoice`。
系统级规则：
```text
SubVoice 内事件只作用于该 SubVoice 在实例中被分配到的 Channel Unit
Event Instrument 级不直接持有普通 MIDI 时间线事件
Event Instrument 级持有 Mapping Function 集合等共享定义资源
Initial State Defaults 不作为普通时间线事件 Lane 处理
```
如果用户提到类似 `Cutoff`，本章不将其作为 MIDI 1.0 标准事件类型。
更准确的系统级表达是：
```text
滤波 Cutoff 等声音参数如果需要表达，应通过具体 MIDI 控制目标实现，例如 CC、NRPN、RPN 或后续定义的参数别名。
Cutoff 不作为初版独立标准事件类型。
```
### 8.50.2 Lane 组织语义
SubVoice 内事件按事件类型 / 目标参数组织为多条 Lane。
示例：
```text
Note Lane
CC11 Lane
Pitch Bend Lane
Program Change Lane
Bank Select Lane
RPN Lane
NRPN Lane
Pitch Bend Range Lane
```
Lane 是系统级组织语义和 UI 显示基础，但 Lane 本身不是独立用户对象。
规则：
```text
Lane 由事件类型 / 目标参数派生
Lane 不需要稳定 ID
空 Lane 不保存进 Project
空 Lane 可由事件类型 / 目标参数动态显示
```
如果 UI 提供“删除 Lane 内容”操作，其语义为：
```text
删除该目标参数下所有用户事件对象，包括曲线和离散点
该操作需要确认
该操作进入全项目撤销 / 重做
该操作使 Project 进入已修改状态
```
---
## 8.51 初版可直接编辑事件类型
初版 SubVoice 内可直接编辑以下事件类型：
```text
Note
Control Change
Pitch Bend
Program Change
Bank Select
RPN
NRPN
Pitch Bend Range
```
其中：
```text
RPN / NRPN 是 Midora 高级封装事件
Pitch Bend Range 可作为独立高级事件，也可作为 RPN 0 的快捷编辑入口
内部语义应统一
```
初版不开放以下事件作为普通用户事件：
```text
自由 SysEx
Channel Pressure
Poly Pressure
All Notes Off
All Sound Off
Reset All Controllers
其他 Channel Mode Message 作为普通 CC
```
其中 All Notes Off、All Sound Off、Reset All Controllers 等属于 Reset / 系统策略管理范围，不作为普通 SubVoice 时间线事件开放编辑。
---
## 8.52 用户可编辑对象稳定 ID 规则
### 8.52.1 哪些对象需要稳定 ID
以下用户可编辑对象必须拥有内部稳定 ID：
```text
Note 对象
CC 离散点
Pitch Bend 离散点
Program Change 事件
Bank Select 事件
RPN 事件
NRPN 事件
Pitch Bend Range 事件
曲线对象
曲线点
映射链 Mapping Chain
Mapping Step
Mapping Function
Logical Parameter Definition
Logical Parameter Lane
Logical Parameter Point
Logical Parameter Curve
Logical Parameter Mapping
```
稳定 ID 用于：
```text
撤销 / 重做
复制
诊断定位
引用保持
排序稳定
断裂引用定位
打开项目后的问题恢复
```
系统不得只依赖数组顺序、tick、事件类型或显示名称识别这些对象。
### 8.52.2 ID 模型的性能约束
稳定 ID 是系统语义需要，但不要求每个事件都是重量级引用对象。
系统级要求：
```text
稳定 ID 不应强制每个事件使用重量级 class 对象模型
初版设计应允许轻量值类型 ID
初版设计应允许紧凑事件存储、批量事件容器和编译期紧凑展开结构
```
建议：
```text
MidoraObjectId 应是轻量稳定身份值
核心值使用单个 signed 64-bit integer（C# long）
合法范围为 1..long.MaxValue；0 与负值保留为无效 / 未指定状态
ID 值本身只负责承载、比较和序列化身份
ID 不内置生成唯一 ID 的功能
```
说明：
```text
单个 long ID 裸数据为 8 bytes。
500 万个 Note 的 ID 裸数据约 38.1 MiB。
真正应避免的是每个 Note 都成为重量级堆对象、每个事件点自带复杂集合、每个事件点自带独立 Dictionary / List 等模型。
```
### 8.52.3 Project 级 ID 生成状态
唯一 ID 生成是 Project 的职责。
规则：
```text
Project 必须维护项目级 ID 生成状态
Project ID 生成状态必须持久化进 Project 文件
采用仅递增、不补缺的持久化正 long 计数器模型
Project 必须保证同一 Project 内生成的 ID 唯一
计数器达到 long.MaxValue 后必须结构化拒绝继续分配
```
不采用：
```text
ID 值类型内部 NewId(seed)
只靠 seed 生成唯一 ID
每次打开 Project 后随机初始化 ID 生成器
删除对象后补缺复用 ID
```
### 8.52.4 ID 不具备排序语义
对象 ID 只表示身份。
ID 不得用于推断：
```text
创建顺序
用户排序
事件排序
同 tick 输出顺序
诊断显示顺序
```
排序必须由显式顺序字段、UI 顺序或系统级事件排序规则决定。
### 8.52.5 ID 与复制 / 删除 / 撤销 / 重做
规则：
```text
复制对象时生成新 ID
删除后撤销，应恢复原 ID
新建对象后撤销，再重做，应恢复第一次创建时的原 ID
移动事件位置时 ID 保持不变
删除对象后，其 ID 不被未来新对象复用
```
初版不支持跨 SubVoice 直接移动事件。
跨 SubVoice “移动”可理解为：
```text
复制 / 粘贴到目标 SubVoice
删除原对象
```
跨 SubVoice 复制事件时：
```text
深拷贝事件及其映射链
事件对象获得新 ID
映射链对象获得新 ID
Mapping Step 获得新 ID
引用的 Mapping Function 保持引用同一个 Mapping Function ID
```
---
## 8.53 Note 对象系统
### 8.53.1 Note 编辑模型
UI 中 Note 表现为一个 Note 对象，而不是要求用户直接编辑独立 Note On / Note Off 事件。
Note 对象至少包含：
```text
start tick
length
note number
velocity
是否跟随 pitchDelta
参数映射链
稳定 ID
显式顺序 / UI 顺序
```
编译时，Note 对象展开为 Note On / Note Off。
### 8.53.2 Note 时间合法性
规则：
```text
Note start tick 必须 >= 0
Note length 必须 > 0
Note 不允许负时间
Note 不允许 0 长度
```
如果用户拉长 Note，导致：
```text
startTick + length > Event Instrument Template Length
```
系统应自动扩展 Template Length 到合法范围。
不采用：
```text
负 tick Note
0 长度 Note
自动裁剪 Note 到 Template Length
允许超出 Template Length 但编译失败
```
### 8.53.3 Note number
模板 Note number 基础合法范围：
```text
0–127
```
默认情况下，Note number 跟随 `pitchDelta`。
每个 Note 可选择：
```text
跟随 pitchDelta
不跟随 pitchDelta，固定模板音高
使用显式映射链
```
默认 `pitchDelta` 来自 第 8 章《SubVoice 与 MIDI 事件编辑》：
```text
pitchDelta = Logical Track 触发 Note - SubVoice Effective Root Note
compiledNote = SubVoice 内模板 Note + pitchDelta
```
如果默认 Note 平移或显式映射最终产生非法 MIDI note number：
```text
小于 0
大于 127
```
则编译失败。
系统不得：
```text
自动 clamp 到 0–127
自动丢弃该 Note
作为警告继续编译
```
### 8.53.4 Note velocity
模板 Note velocity 基础合法范围：
```text
1–127
```
Note On velocity = 0 不允许作为 Note 对象 velocity，因为在 MIDI 中常被解释为 Note Off。
初版 Note velocity 默认规则：
```text
默认不跟随 Logical Track 触发 Note velocity
默认使用模板固定 velocity
用户可手动开启跟随或配置自定义映射链
```
不定义系统级 `velocityDelta` 或 `velocityScale`。
理由：
```text
pitchDelta 有明确半音语义
velocity 没有同等自然的差值语义
velocity 更适合作为 triggerVelocity 输入给映射系统
```
### 8.53.5 Logical Track velocity 作为映射输入
Logical Track 触发 Note velocity 是 MappingContext 的核心输入之一。
命名为：
```text
triggerVelocity
```
允许以下映射：
```text
Logical Track Note velocity -> SubVoice Note velocity
Logical Track Note velocity -> CC Expression value
Logical Track Note velocity -> 任意 CC value
Logical Track Note velocity -> RPN / NRPN Data Entry
Logical Track Note velocity -> Pitch Bend value
Logical Track Note velocity -> Pitch Bend Range 参数
Logical Track Note velocity -> 多个目标事件参数
```
### 8.53.6 Note Off velocity
初版不开放 Note Off velocity 编辑。
规则：
```text
Note Off velocity 固定使用 0
Note Off velocity 不作为映射目标
Note Off velocity 不进入 Note 对象 UI 属性
```
这与 Note On velocity 不允许为 0 不矛盾，因为二者语义不同。
### 8.53.7 Note 重叠
同一 SubVoice 内模板 Note 允许重叠。
规则：
```text
同一 SubVoice 内不同 pitch Note 重叠允许
同一 SubVoice 内同 pitch Note 重叠允许
同一 SubVoice 内同 pitch 重叠 Note 不额外诊断
```
理由：
```text
SubVoice 接近一条 MIDI Channel 抽象
MIDI Channel 本身允许复音
同 pitch 重叠在某些播放器中可能有配对差异，但初版不额外警告或失败
```
### 8.53.8 Note 不作为曲线目标
Note number 不允许作为时间连续曲线。
Note velocity 不允许作为时间连续曲线。
规则：
```text
Note number 属于 Note 对象属性，不作为连续曲线目标
Note velocity 是 Note On 瞬时属性，不作为持续曲线目标
```
滑音、持续力度变化等应通过：
```text
Pitch Bend
CC Expression
其他 CC / RPN / NRPN
后续专门机制
```
---
## 8.54 Control Change 规则
### 8.54.1 CC 基础值域
Control Change 基础范围：
```text
CC number: 0–127
CC value: 0–127
```
但普通 SubVoice 用户事件编辑入口排除 Channel Mode Message、CC91 与 CC93。该限制只约束 Event Instrument / SubVoice 语义，不约束第 23 章定义的 Pure MIDI Track 直接 MIDI 事件。

### 8.54.2 Reverb / Chorus 边界

Event Instrument / SubVoice 不支持创建或映射到：
```text
CC91 / Reverb Send Level
CC93 / Chorus Send Level
```

它们不得出现在 Event Instrument 语义路径中的：
```text
SubVoice Initial State
SubVoice 时间线离散点或曲线
Logical Parameter Mapping 目标
SubVoice 源数据
```

向 SubVoice 输入或粘贴 CC91 / CC93 时必须拒绝该编辑，不得静默忽略、Clamp 或改写为其他 CC。SMF 导入不得把这类事件强制转换成 SubVoice 事件，而应按第 23 章将其保存在 Pure MIDI Track 中；Pure MIDI Track 的 CC91 / CC93 必须进入 canonical compiled result 并由 MIDI 导出原样写出。正式 BASSMIDI 音频路径仍启用 `BASS_MIDI_NOFX`，因此不得把这些控制器解释为 Midora 音频效果承诺。

### 8.54.3 Channel Mode Message 边界
CC 120–127 属于 Channel Mode Message 范围，例如：
```text
All Sound Off
Reset All Controllers
All Notes Off
Omni Mode Off / On
Mono Mode On
Poly Mode On
```
初版不允许作为普通 CC 编辑。
这些功能由：
```text
Reset 策略
系统安全状态管理
Channel Unit 释放语义
```
负责。
### 8.54.4 CC 编辑模型
正式 CC 值仍为 raw `0..127`。CC10／71～78 的外侧显示／数值工具采用 §18.2.9 的 `−64..63` 友好域；不得据此改变 Mapping、曲线底层值或 MIDI 编码。离散点的辅助阶梯线仅为 §18.2.10 的显示层。

CC 事件支持：
```text
离散点编辑
曲线编辑
```
底层统一为事件点序列语义。
同一 SubVoice、同一 CC number 可拥有：
```text
最多一条曲线
多个用户离散点
```
曲线离散化结果与用户离散点同 tick 冲突时：
```text
用户离散点优先
```
### 8.54.5 同 tick CC 冲突
同一 SubVoice、同一 tick：
```text
多个不同 CC number 允许并存
同一 CC number 多个值最终只保留一个，后者替换前者
```
---
## 8.55 Program Change 与 Bank Select
### 8.55.1 Program Change
Program Change 是离散事件，不允许曲线化。
规则：
```text
Program Change 可在不同 tick 放置多个离散事件
Program Change UI 显示 Program 0–127
内部 Program value 使用 0–127
名称由程序级 Instrument Catalog resolver 提供，缺失时数值回退
不得为展示名称隐式扫描 SoundFont 或读取 sample
```
Program value 允许映射。
映射规则：
```text
映射结果必须是整数 0–127
默认越界策略 fail
目标参数取整策略可配置
```
同一 SubVoice、同一 tick 多个 Program Change：
```text
最终只保留一个，后者替换前者
```
### 8.55.2 Bank Select
Bank Select 作为独立高级事件。
Bank Select 包含：
```text
MSB
LSB
```
允许：
```text
MSB 为空
LSB 为空
MSB 和 LSB 均存在
```
Bank Select 不允许曲线化，但允许在不同 tick 放置多个离散事件。
Bank MSB / LSB 允许映射，但仅作用于已存在值。
规则：
```text
映射不能生成空值
映射不能取消已有 MSB / LSB
结构存在性与数值变化分离
```
### 8.55.3 Bank 与 Program 的关系
同一 SubVoice、同一 tick 同时存在 Bank Select 与 Program Change 时：
```text
Bank Select 必须先于 Program Change 输出
```
Bank Select 同时包含 MSB 和 LSB 时：
```text
MSB 先于 LSB
```
Initial State 与 Bank / Program 的冲突判定：
```text
Bank Select 与 Bank Select 冲突
Program Change 与 Program Change 冲突
Bank Select 与 Program Change 彼此不冲突
```
---
### 8.55.4 显式 Instrument Change 编辑关联

用户可在 MIDI Segment/SubVoice 的 `Inst.` 显式创建乐器变化点。它只有一个稳定包装 ID 和同 owner 的成员 ID：Direct MIDI 为 CC0/CC32/Program 三条；SubVoice 为同时具有 MSB/LSB 的完整 Bank 与 Program 两条。Tick 和 MIDI 值仍只在 raw 成员中；包装不是新的音乐事件或 Mapping owner，Compiler 不重新解释它。

完整关联要求成员同 Tick、target 正确且每条成员只属于一个包装。raw 值编辑保留包装；正式原子事务最终态的部分删除/错位/目标改变/碰撞替换使包装解组，但不得删除幸存 raw 或补成员。整组移动按最终态校验，不因中间状态误解组；父对象复制必须重映射身份，Split 将完整关联交给实际成员所在 owner。一次 Undo/Redo 同时还原音乐成员、关联和稳定 ID。

新 Direct MIDI 组按 CC0→CC32→Program 插入本 Track 同 Tick NoteOn 之前；原同 Tick 消息之间保序，不改共享 Root 中较早 Track 的正式顺序。SubVoice 沿用 Bank→Program→Note 类别顺序。导入 MIDI 不发现或自动创建包装，原始 Bank/PC、部分 Bank 和可选 Mapping 入口仍保留。

关联是持久 source 编辑组织数据，严格保存到 §16.35 的 Format 4；不是可损坏丢弃的 presentation。Initial State 的统一选择器另按三个独立 override/inherit 位工作，不生成 Tick 0 点、不改变模板长度；显式选择完整 preset 才同时覆盖三个字段。

完整包装支持 Copy/Cut/Paste/Delete、水平 Move/Ctrl 复制拖动、水平 Flip、Scale、Quantize 和多选 Properties。所有成员在同一个有界、可取消事务中处理，不能按中间态拆组；结果选择为实际幸存成员，Undo 恢复原成员选择，混合选择中未处理对象保持。Properties 的 Tick、MSB、LSB、Program 使用显式 Mixed 统一赋值/还原；不提供纵移、移调、Note Split/Join 或普通单 Point Value 的 Batch 表达式。Move/复制拖动共同夹止于内容 Tick 0；Flip/Scale 产生同 target 同 Tick 碰撞时拒绝，其他适用编辑仍服从事件后来者覆盖规则。新复制包装才建立新的插入顺序，已有包装不因值编辑或移动而重建身份/正式顺序。

## 8.56 Pitch Bend 与 Pitch Bend Range
### 8.56.1 Pitch Bend
Pitch Bend 支持曲线编辑，并在编译 / 导出时离散化为 Pitch Bend 事件。
内部编辑值域：
```text
-8192 到 +8191
中心 0
```
UI 默认显示：
```text
-100% 到 +100%
```
说明：
```text
初版 UI 默认不要求显示 raw value 或 semitone
未来可提供高级显示模式
```
同一 SubVoice、同一 tick 多个 Pitch Bend 值：
```text
最终只保留一个，后者替换前者
```
### 8.56.2 Pitch Bend Range
Pitch Bend Range 可作为独立高级事件，也可作为 RPN 0 的快捷入口。
内部语义应统一。
合法范围：
```text
semitone: 0–127
cents: 0–99
```
Pitch Bend Range 允许作为曲线目标：
```text
Pitch Bend Range semitone
Pitch Bend Range cents
```
但具体离散化和 MIDI 展开顺序由 第 12 章《编译系统与 Canonical Compiled Result》 / 第 14 章《MIDI 导出》 细化。
### 8.56.3 Pitch Bend Range 与 Pitch Bend 同 tick
同一 SubVoice、同 tick 既设置 Pitch Bend Range 又设置 Pitch Bend value 时：
```text
Pitch Bend Range 先于 Pitch Bend value
```
理由：
```text
Pitch Bend Range 决定后续 Pitch Bend 的音乐解释
```
---
## 8.57 RPN / NRPN
### 8.57.1 编辑抽象
初版 RPN / NRPN 作为高级封装事件暴露。
用户不直接编辑底层：
```text
CC 101
CC 100
CC 99
CC 98
CC 6
CC 38
```
系统在编译 / 导出阶段展开。
### 8.57.2 参数号
RPN / NRPN 参数号支持：
```text
MSB 0–127 + LSB 0–127
合并 14-bit 参数号 0–16383
```
内部统一为 14-bit。
UI 可拆分显示 MSB / LSB。
参数号属于目标身份参数。
初版不允许通过映射改变：
```text
RPN 参数号
NRPN 参数号
```
### 8.57.3 Data Entry
RPN / NRPN Data Entry 支持：
```text
MSB 0–127 + LSB 0–127
7-bit 表示
14-bit 表示
```
内部统一为 14-bit。
Data Entry 是值类参数，允许映射。
### 8.57.4 Null Function
RPN / NRPN 输出后默认自动发送 Null Function。
目的：
```text
取消当前参数选择
避免后续 Data Entry 意外作用到之前选择的 RPN / NRPN 参数
```
具体底层 CC 展开顺序由第 14 章《MIDI 导出》规定。
### 8.57.5 同 tick 冲突
同一 SubVoice、同一 tick、同一 RPN / NRPN 参数重复赋值：
```text
最终只保留一个，后者替换前者
```
RPN / NRPN 高级事件输出语义顺序：
```text
参数选择 MSB / LSB
Data Entry MSB / LSB
Null Function
```
---
## 8.58 曲线系统
### 8.58.1 允许曲线的目标
初版允许以下事件目标使用曲线：
```text
CC value
Pitch Bend value
RPN / NRPN Data Entry
Pitch Bend Range semitone / cents
```
不允许曲线化：
```text
Program value
Bank Select MSB / LSB
Note number
Note velocity
事件目标身份参数
```
### 8.58.2 曲线数量
每个 SubVoice 的每个连续目标参数最多一条曲线。
例如：
```text
SubVoice 1 的 CC11 value 最多一条曲线
SubVoice 1 的 Pitch Bend value 最多一条曲线
```
同一目标参数允许：
```text
一条曲线
多个用户离散点
```
不允许：
```text
同一目标参数多条曲线叠加
多曲线命名叠加
```
### 8.58.3 曲线形态
初版事件曲线支持：
```text
点编辑
直线
阶梯线
自由手绘曲线
```
不要求初版支持：
```text
贝塞尔曲线
缓入缓出曲线段
复杂曲线节点类型
```
每段曲线的插值类型挂在每个点之后的段上。
规则：
```text
每段可独立设置直线 / 阶梯 / 自由曲线等插值类型
整条曲线不必统一一种插值
```
### 8.58.4 曲线点 tick 规则
曲线点 tick 必须满足：
```text
tick >= 0
```
曲线点可超过当前 Template Length。
如果添加或移动曲线点导致超过当前 Template Length：
```text
自动扩展 Event Instrument Template Length
```
### 8.58.5 曲线起止语义
曲线允许没有 tick 0 起点。
规则：
```text
曲线从第一个点开始产生事件
第一个点之前不产生该目标事件
曲线不应向前补事件
曲线最后一个点之后不继续生成事件
MIDI Channel 状态自然保持最后值，直到后续事件或 Reset
```
不采用：
```text
强制曲线从 tick 0 开始
自动在 tick 0 插入同值点
自动在 Template Length 插入末尾同值点
最后一个点后持续重复输出相同值
```
### 8.58.6 曲线与离散点合并
同一参数的曲线与离散点允许共存。
最终按 tick 合并。
冲突规则：
```text
用户离散点优先于曲线离散化事件
```
例如：
```text
曲线离散化在 tick 100 生成 CC11 = 80
用户离散点在 tick 100 为 CC11 = 90
最终采用用户离散点 CC11 = 90
```
### 8.58.7 曲线保存
项目保存原始曲线数据。
规则：
```text
保存可编辑曲线段数据
编译 / 导出时离散化
不把用户曲线不可逆降级为 MIDI 点
```
自由手绘曲线也保存为可编辑曲线段数据。
初版允许系统对手绘输入做点简化 / 平滑，但不得明显改变曲线语义。
### 8.58.8 曲线离散化边界
第 9 章《曲线、Logical Parameter 与映射》 只规定系统级语义：
```text
曲线最终必须离散化为 MIDI 事件
离散化需要可控且可预测
离散化允许去除连续重复值，但不得改变最终 MIDI 状态语义
```
以下内容由 第 12 章《编译系统与 Canonical Compiled Result》 / 第 14 章《MIDI 导出》 细化：
```text
逐整数 tick 的参考求值语义
最终整数值重复抑制
导出事件排序
同 tick 字节级排序
```
### 8.58.9 删除 / 复制曲线
曲线对象拥有稳定 ID。
曲线点拥有稳定 ID。
删除曲线时：
```text
只删除曲线对象
保留同一目标参数下的用户离散点
```
复制曲线到另一个支持曲线的目标参数时：
```text
允许复制
曲线对象获得新 ID
曲线点获得新 ID
目标参数按自身值域、取整和合法性校验
```
复制曲线到不支持曲线的目标时：
```text
阻止复制
```
不自动转成离散点，不自动生成映射链。
### 8.58.10 离散点复制
离散点复制到另一个目标参数时：
```text
允许复制
新事件对象获得新 ID
目标参数按自身值域、取整和合法性校验
```
---
## 8.59 Initial State 与普通事件
### 8.59.1 Initial State 不作为普通 Lane
Initial State Defaults 不作为普通事件 Lane 处理。
规则：
```text
Initial State 是实例开始状态默认值配置
Initial State 不算模板内部普通事件位置
Initial State 不影响 Template Length
Initial State 不混入普通时间线事件 Lane
```
第 9 章《曲线、Logical Parameter 与映射》 可说明其与普通事件的冲突规则，但不把 Initial State 作为 tick 0 普通事件保存。
### 8.59.2 Initial State 初版不支持映射
初版 Initial State 值不支持映射链。
规则：
```text
Initial State 只支持固定值
Initial State 不支持图形映射
Initial State 不支持 Mapping Function Expression
```
理由：
```text
Initial State 是实例开始状态注入
如果允许依赖 triggerNote / triggerVelocity，会与实例隔离关闭产生复杂兼容性问题
初版先保持简单
```
### 8.59.3 Initial State 支持类型
Initial State 支持的事件类型范围承接 第 8 章《SubVoice 与 MIDI 事件编辑》：
```text
Control Change
Pitch Bend
Program Change
Bank Select
RPN
NRPN
Pitch Bend Range
```
Initial State 不支持 Note。
因此：
```text
Initial State 与普通 tick 0 Note 不存在同类冲突
Note 不覆盖 Initial State
Initial State 不覆盖 Note
```
### 8.59.4 Initial State 与普通 tick 0 事件冲突
如果 Initial State Defaults 与用户在该 SubVoice tick 0 的普通事件冲突：
```text
用户普通事件优先
Initial State 中同类事件不再插入
```
普通事件包括：
```text
tick 0 离散事件
曲线 tick 0 控制点
```
曲线 tick 0 点视为用户普通事件。
### 8.59.5 同类事件判定
同类事件按精确目标判断。
示例：
```text
CC11 与 CC11 冲突
CC11 与 CC1 不冲突
Pitch Bend 与 Pitch Bend 冲突
Program Change 与 Program Change 冲突
Bank Select 与 Bank Select 冲突
Bank Select 与 Program Change 不冲突
RPN 同一参数与同一参数冲突
NRPN 同一参数与同一参数冲突
```
不按事件大类粗暴判断。
---
## 8.60 同 tick 事件语义排序
### 8.60.1 本章排序边界
第 9 章《曲线、Logical Parameter 与映射》 定义事件编辑层的同 tick 语义优先级。
最终 MIDI 字节级排序由 第 12 章《编译系统与 Canonical Compiled Result》 / 第 14 章《MIDI 导出》 继续细化。
### 8.60.2 基本语义顺序
同一 SubVoice、同一 tick 的基本语义顺序为：
```text
Note Off
Initial State
Bank Select
Program Change
Pitch Bend Range / RPN / NRPN
CC
Pitch Bend
Note On
```
### 8.60.3 Note Off 与 Note On
同 tick 出现 Note Off 和 Note On 时：
```text
Note Off 先于 Note On
```
理由：
```text
有利于同音高连续音重新触发
降低卡音风险
```
### 8.60.4 Note On 之间排序
同 tick 多个不同 pitch Note On：
```text
按 Note 对象显式顺序 / UI 顺序稳定输出
```
同 tick 同 pitch 多个 Note On：
```text
按 Note 对象显式顺序 / UI 顺序稳定输出
```
不得按 ID 排序。
不得按 pitch 自动排序。
### 8.60.5 Bank / Program / CC / Pitch Bend / Note On 相对顺序
规则：
```text
Bank Select 先于 Program Change
Program Change 先于普通 CC
Program Change 先于 Note On
CC / Pitch Bend 先于 Note On
Pitch Bend Range 先于 Pitch Bend value
```
Program Change 先于普通 CC 的理由：
```text
切换 Program 后再写 CC 状态，更符合建立目标音色初始控制状态的语义
```
### 8.60.6 不允许用户覆盖多类排序
初版不允许用户手动覆盖多类事件的同 tick 固定语义顺序。
不支持：
```text
高级用户手动排序所有同 tick 事件
手动让 Note On 先于 Program
手动让 Pitch Bend 先于 Pitch Bend Range
```
这样可以保持编译、诊断和导出稳定。
---
## 8.61 基础值非法与映射后非法
### 8.61.1 基础事件值
基础事件值指用户直接保存在事件对象中的原始值。
规则：
```text
基础事件值必须合法才能提交到 Project 数据
Project 持久数据不应保存非法基础值
```
例如：
```text
CC value = 200 不应作为已提交 Project 数据保存
Note velocity = 0 不应作为 Note 对象 velocity 保存
Program value 超出 0–127 不应保存
```
### 8.61.2 UI 临时非法输入
UI 可存在临时非法输入状态。
例如用户正在输入：
```text
CC value = 200
```
但在提交到项目数据前必须校验。
不采用：
```text
项目数据允许长期保存非法基础值
保存时自动 clamp
编译时再处理基础非法值
```
### 8.61.3 映射后非法值
映射配置本身可以保存。
映射运行结果非法时，按目标参数策略处理：
```text
如果目标允许 clamp 且配置为 clamp，则 clamp 到合法范围
如果目标配置为 fail 或无法合法 clamp，则当前编译 / 播放 / 渲染 / 导出流程失败
```
因此必须区分：
```text
基础事件值非法：不得提交为 Project 持久数据
映射后值非法：配置可保存，运行时诊断 / 失败
```
---
## 8.62 撤销 / 重做与项目修改状态
以下行为进入全项目统一撤销 / 重做框架，并使 Project 进入已修改状态：
```text
新增事件
删除事件
移动事件
修改事件值
修改 Note start / length / note number / velocity
修改 Note 是否跟随 pitchDelta
新增 / 删除 / 编辑曲线
新增 / 删除 / 移动 / 编辑曲线点
修改曲线段插值类型
新增 / 删除 / 编辑离散点
新增 / 删除 / 编辑映射链
新增 / 删除 / 编辑 Mapping Step
启用 / 禁用映射链
启用 / 禁用 Mapping Step
复制 / 粘贴事件
复制 / 粘贴曲线
复制 / 粘贴映射链
创建 / 删除 / 重命名 / 编辑 Mapping Function
复制 Mapping Function
删除被引用 Mapping Function 后保留断裂引用
修改图形映射输入 / 输出范围
修改图形映射超范围策略
修改目标参数取整策略
修改目标参数越界策略
```
临时 UI 状态不应标记项目已修改，例如：
```text
临时选择事件
临时框选
临时缩放曲线视图
临时展开 / 折叠 Lane
未提交的非法输入文本
临时预览
```
---
## 8.63 诊断原则
### 8.63.1 允许保存但参与编译失败
以下问题允许保存，但实际参与编译、播放、渲染或导出时失败：
```text
断裂 Mapping Function 引用
被实际使用的 Mapping Function 编译错误
Mapping Function 运行时异常
Mapping Function 返回 NaN / Infinity
图形映射内部产生 NaN / Infinity
映射链最终结果非法且不能按策略处理
Per-Note Instance Isolation 关闭时，启用映射依赖每音符上下文
启用 Mapping Step 引用不存在的 Mapping Function
曲线或映射配置与目标参数语义不兼容且参与编译
```
### 8.63.2 警告
以下情况不阻止整曲编译，但应在诊断中显示为警告：
```text
未被引用但无法通过 ABI v3 验证或绑定的 Mapping Function
打开项目时发现 Mapping Function 引用断裂
```
具体是否在打开时弹窗、诊断面板合并显示、是否支持跳转，由 第 15 章《音频文件渲染》 / 第 17～20 章的 UI 与交互规格 细化。
### 8.63.3 不产生诊断
以下情况不产生诊断：
```text
未被任何映射链引用的 Mapping Function 且自身无错误
未使用的曲线或事件配置处于合法状态
禁用映射链中的错误配置
禁用 Mapping Step 中的错误配置
同一 SubVoice 内同 pitch 重叠 Note
空映射链
未保存的临时 UI 输入错误
```
---
