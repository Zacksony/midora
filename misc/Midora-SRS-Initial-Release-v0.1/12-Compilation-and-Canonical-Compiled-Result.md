# 第 12 章 编译系统与 Canonical Compiled Result

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义高层 Project 语义到标准 MIDI 1.0 事件语义的唯一转换管线，涵盖 Logical/Event Instrument 展开、Pure MIDI 直接事件归一化、CompileContext、范围恢复、资源分配、排序、诊断、缓存、增量编译和消费者边界。Pure MIDI Track 的专项模型和 SMF Track 投影由第 23 章细化。

## 12.1 编译系统核心原则
### 12.1.1 Canonical compiled result
Midora 初版应以 `canonical compiled result` 作为编译系统的标准输出。
规则：
```text
编译系统生成统一 canonical compiled result。
播放、预览、音频渲染和 MIDI 导出都以该结果为语义基础。
不同消费者可以在 compiled result 基础上做各自需要的结构化整理或调度，但不得重新解释 Project 语义。
```
例如：
```text
播放系统可以基于 canonical compiled result 生成播放 buffer、调度队列和实时状态机。
MIDI 导出器可以基于 canonical compiled result 拆分 MIDI Track、写 Port meta、编码字节流。
音频渲染器可以基于 canonical compiled result 做离线 buffer 渲染。
```
但它们不得改变：
```text
Event Instrument 展开语义
Logical Parameter Mapping 结果
生命周期裁剪语义
Reset 语义
Port / Channel Unit 分配语义
同 tick 语义排序
```
### 12.1.2 编译函数语义
系统级可抽象为：
```text
FullCompile(Project, CompileContext) -> CanonicalCompiledResult
```
同一输入下：
```text
同一 Project 内容
同一 CompileContext
同一 Source 状态
```
编译结果必须稳定一致。
不得因以下因素导致结果变化：
```text
随机数
不稳定 Dictionary / HashSet 遍历顺序
线程竞态
上一次编译结果
编辑历史
缓存历史
对象创建历史
```
### 12.1.3 增量编译不是独立语义
增量编译只是确定性全量编译函数的缓存执行计划。
规则：
```text
IncrementalCompile(Project, CompileContext, Cache)
```
对外输出必须等价于：
```text
FullCompile(Project, CompileContext)
```
增量编译不得为了保留旧缓存而保留旧 Port / Channel 分配。
### 12.1.4 无 SF2 不影响编译
SoundFont 不影响：
```text
MIDI 编译
语义验证
Logical Parameter Mapping
Channel Unit 资源分配
canonical compiled result
MIDI 导出语义
```
无 SF2 只影响：
```text
播放
预览
音频渲染
实际发声结果
```
因此无 SF2 状态下仍允许生成 canonical compiled result。
---
## 12.2 编译上下文
### 12.2.1 CompileContext 定义
`CompileContext` 表示一次编译请求的上下文。
它至少应包含以下系统级信息：
```text
编译类型
tick 范围
Logical Track / Pure MIDI Track 选择集合
是否全项目编译
是否播放编译
是否预览编译
是否导出准备编译
是否音频渲染准备编译
是否使用 Project End Marker 作为默认结束依据
是否启用 Warning 导致编译失败
诊断收集 / 显示策略
```
具体字段结构由 第 16 章《.midora 文件格式与持久化》 或实现设计细化。
### 12.2.2 编译上下文类型
初版至少应区分：
```text
全项目编译
范围编译
播放编译
Segment 预览编译
Event Instrument 预览编译
MIDI 导出准备编译
音频渲染准备编译
```
### 12.2.3 不同上下文共享核心语义
不同 CompileContext 只改变：
```text
范围
Track 选择
消费者需求
临时预览输入
输出用途
```
不得改变：
```text
Event Instrument 展开语义
Logical Parameter 状态继承语义
Mapping 计算语义
生命周期语义
Reset 语义
Channel Unit 分配语义
同 tick 排序语义
错误 / 警告 / 信息诊断语义
```
### 12.2.4 预览上下文
Segment 预览和 Event Instrument 预览应复用编译系统。
预览上下文允许提供临时对象，例如：
```text
临时 Logical Note pitch
临时 velocity
临时 Gate Length
临时 Segment
临时 Track 绑定语义
临时 startTick
```
这些临时对象：
```text
不写入 Project
不改变 Project 修改状态
不进入撤销 / 重做
只存在于本次 CompileContext
```
Event Instrument 预览时，临时 Logical Note 的 pitch、velocity、Gate Length 应进入 MappingContext，以尽量模拟正式 Logical Track 触发上下文。
Segment 预览如果绑定到项目时间位置，应按该位置解析 Tempo、Time Signature、Key Signature 等全局上下文。
预览编译不占用 Project 正式编译资源，但仍应模拟 Channel Group 需求。如果单实例需求超过系统上限，应报错。
Segment 预览应遵守 Segment End 硬边界和 Reset 规则。

第 13.22.7、13.24.5 节定义的 held Preview 是唯一允许 Gate Length 在 Gate Start 时尚未确定的 Preview CompileContext。它必须复用同一 Event Instrument 展开、Mapping、生命周期、资源分配、同 tick 排序和 canonical 消费管线，但采用因果增量语义：

```text
Gate Start 至 Gate End 前：MappingContext.gateLength = Int64.MaxValue
Gate End：冻结该交互入口定义的实际 Gate Length
生效边界：producer 尚未渲染的第一个 sample frame
禁止：回写已消费或已缓冲 PCM、猜测最终 Gate Length、绕过 canonical 直接发送 MIDI
```

因果 Gate 的前缀结果不要求与 Gate End 后使用最终 Gate Length 重新执行一次固定长度编译字节等价；相同 Gate Start 输入、Gate End 输入、Tempo / sample 映射、Render-Ahead 和有效资源状态仍必须产生确定一致的因果结果。
---
## 12.3 编译 tick 范围
### 12.3.1 左闭右开区间
所有编译 tick 范围统一采用左闭右开区间：
```text
[startTick, endTick)
```
含义：
```text
startTick 包含
endTick 不包含
length = endTick - startTick
```
相邻范围示例：
```text
[0, 100)
[100, 200)
```
两个范围无重叠、无空洞，tick 100 只属于第二段。
### 12.3.2 范围合法性
显式范围编译规则：
```text
startTick < 0 非法，编译失败。
endTick < startTick 非法，编译失败。
endTick == startTick 允许作为零长度范围编译。
```
零长度范围不产生音乐输出，但可以用于诊断或状态查询。
### 12.3.3 全项目默认 startTick
全项目编译默认从：
```text
tick 0
```
开始。
### 12.3.4 全项目默认 endTick
如果 CompileContext 未显式指定 endTick，且 Project End Marker 存在：
```text
默认 endTick = Project End Marker tick
```
如果不存在 Project End Marker，且存在有效音乐内容：
```text
默认 endTick 由有效音乐内容自然决定。
```
自然结束位置应考虑：
```text
参与编译的 Event Instrument Instance
实例实际输出
必要 Note Off
Release / Tail 被允许范围内的结果
Reset 完成后的最大 tick
```
Conductor Track 中超过音乐内容的普通 Marker / Meta Event 不强制延长默认结束位置。
如果没有 Project End Marker、没有 Logical Track 输出，但存在 Conductor tick 0 默认 Tempo / Time Signature：
```text
允许生成零长度或无音乐内容的结构有效编译结果。
Conductor tick 0 状态仍作为全局上下文存在。
```
### 12.3.5 隐藏内容不影响默认结束位置
隐藏在 Segment 裁剪窗口外的内容：
```text
保留在 Project 中
不参与当前编译
不决定默认结束位置
```
---
## 12.4 范围起点上下文恢复
### 12.4.1 必须解析范围前上下文
当编译范围从项目中途开始时，编译系统必须解析范围起点之前的必要上下文状态。
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
Logical Parameter 有效状态
Channel Unit 当前状态
活动 Event Instrument Instance
活动实例生命周期阶段
活动实例 Channel Group 分配
```
### 12.4.2 不补发范围前 Note On
如果某个 Note On 已经在范围开始前发生：
```text
不得在 startTick 伪造或补发 Note On。
```
即使该音在范围内理论上仍持续发声，也不在范围起点重新发声。
这是范围冷启动的明确限制：
```text
从中途冷启动播放时，范围前已经开始发声的持续音不会被重新触发。
```

该限制同样适用于 Pre-Roll 实例：当实例/模板 origin 早于 `startTick` 时，不因其 Logical Gate anchor 位于范围内而补发范围前模板 Note On，也不在 canonical、播放或渲染消费者中执行隐藏音频预滚以恢复 sample 相位。
### 12.4.3 非音符状态恢复
范围起点应恢复必要非音符状态。
包括：
```text
Program
Bank Select
Pitch Bend
Pitch Bend Range
CC
RPN
NRPN
Logical Parameter Mapping 后影响到的非 Note 状态
Conductor Track 当前状态
```
恢复事件放在：
```text
startTick
```
并在语义排序上早于该 tick 的普通用户事件与 Note 事件。
如果恢复状态与 startTick 上用户原始事件冲突：
```text
startTick 用户原始事件优先。
恢复状态只提供进入该 tick 前的上下文。
```
普通 Marker 不是状态型事件，不在范围起点恢复；范围编译只包含范围内 Marker。
Key Signature 是全局状态型 Meta Event，范围编译应在起点恢复当前有效 Key Signature。
### 12.4.4 范围前活动实例
如果某个 Event Instrument Instance 在范围开始前触发，但生命周期与范围相交：
```text
该实例应参与范围内状态解析、非 Note 输出、必要 Note Off、Reset 与资源占用判断。
```
范围前已经 Note On、范围内发生原始 Note Off：
```text
输出该 Note Off。
```
范围前已经 Note On、范围内没有 Note Off、范围结束时仍跨界：
```text
范围结束作为硬裁剪边界，需要补充 Note Off。
```
如果范围起点正好等于某个 Note On 的 tick：
```text
输出该 Note On。
```
如果范围终点正好等于某个 Note Off 的 tick：
```text
不输出该 Note Off，因为 endTick 不包含在 [startTick, endTick) 内。
```
除非该 Note Off 是由范围硬裁剪边界生成的补充事件。
### 12.4.5 范围恢复事件身份
范围恢复事件应进入 canonical compiled result。
它们应标记为：
```text
编译器生成事件
来源：范围起点状态恢复
```
全项目从 tick 0 开始编译时，不需要额外生成范围恢复事件。
---
## 12.5 Conductor Track 编译
### 12.5.1 永远进入编译结果
只要生成 compiled result，Conductor Track 编译数据就应进入结果。
即使没有任何 Logical Track 输出，Conductor Track 的默认状态仍是全局上下文。
### 12.5.2 范围编译状态恢复
范围起点之前最近有效的以下状态应作为范围起点上下文进入结果：
```text
Tempo
Time Signature
Key Signature
```
### 12.5.3 Marker
普通 Marker：
```text
只包含范围内 Marker。
不恢复范围前最近 Marker。
```
Project End Marker：
```text
可作为默认编译范围结束依据。
作为范围结束边界时，跨界实例按硬边界裁剪。
```
Project End Marker 不删除 Project 内容。
---
## 12.6 Track 与 Segment 过滤
### 12.6.1 Logical Track Usage 验证
空且未绑定的 Logical Track 不参与编译且不产生诊断。有 Segment/Note/Parameter 内容的 Logical Track 必须且只能引用一个有效、非空 Event Instrument Usage；Usage 必须引用一个有效 Definition。缺少/重复 owner、Usage 为空、kind 错误或索引不一致是结构 Error，不能生成 Canonical Compiled Result。
### 12.6.2 Mute / Solo
canonical compiled result 默认忽略 Mute / Solo 状态。
规则：
```text
Mute / Solo 不改变成品输出语义。
全项目 canonical compiled result 默认包含所有有效参与编译的 Track。
```
播放上下文可以在 canonical 语义基础上按当前 Mute / Solo 生成播放用过滤结果。
MIDI 导出和音频文件渲染必须使用显式 Track 选择，而不是隐式使用 Mute / Solo。
### 12.6.3 Track 选择集合
当 CompileContext 显式选择 Track 集合时：
```text
本次编译只为被选择 Logical Track / Pure MIDI Track 生成实例、事件和资源占用。
未选择 Track 不参与本次编译诊断。
全项目诊断仍应检查全部 Track。
Conductor Track 仍进入结果。
```
选择同一 Root 的部分 Pure MIDI Track 时，本次执行投影只合并被选择 Track 的事件，并根据这些 Track 的 Segment 重新建立本次上下文的 Root 活动连通区间；不得从未选择 Track 偷取状态事件。全项目正式编译仍包含全部有效 Track。
### 12.6.4 Segment 重叠
同一 Logical Track 内 Logical Segment、同一 Pure MIDI Track 内 Midi Segment 均不允许重叠。
如果打开 Project 或编译时发现同一 Track 内 Segment 重叠：
```text
编译失败，并定位到冲突 Segment。
```
不同 Track 的 Segment 可以重叠并正常编译，包括同一 MIDI Channel Root 的不同 Pure MIDI Track。
同一 Event Instrument Usage 的不同 Logical Track Segment 也可以重叠；它们的并集决定 Usage 活动连通区间，Overlap Policy 在整个 Usage 内验证。
### 12.6.5 Segment 裁剪窗口
Logical Note start 在 Segment 有效裁剪窗口外：
```text
不生成实例。
隐藏内容保留，但不参与当前 Segment 编译。
```
Logical Note start 在 Segment 内，但 length 超过 Segment End：
```text
允许存在。
编译时按 Segment End 硬裁剪生命周期。
不自动修改 Project 数据。
```

Logical Note anchor 在 Segment 内，但其 Definition 的 `Pre-Roll Ticks` 使 `anchor - Pre-Roll Ticks` 早于 Segment 有效起点：

```text
编译 Error，并定位 Logical Note、Segment 与 Event Instrument Definition。
不得 Clamp、丢弃前缀、自动扩张 Segment 或跨 Segment 边界生成实例。
```

全部 `anchor - Pre-Roll Ticks`、`origin + templateTick` 和 local/absolute tick 换算必须使用 checked `Int64` 算术；下溢、上溢或得到负 Project tick 均为 Error，不得环绕或饱和。
Logical Note length <= 0：
```text
非法，编译失败，并定位到 Logical Note。
```

Pure MIDI Track 的 Midi Segment 使用相同 Content Window 与裁剪规则，但内部对象是 Direct MIDI Note / Direct MIDI Event；其直接编译、未配对 raw Note 和 opaque event 规则以第 23.5～23.9 节为准。
---
## 12.7 Event Instrument Instance 生成
### 12.7.1 触发源
初版中，Segment 内 `Logical Note` 是生成 Event Instrument Instance 的唯一触发源。
Logical Parameter Point / Curve 不单独触发实例。

设 Logical Note absolute anchor 为 `A`、Definition `Pre-Roll Ticks` 为 `O`、有效 Logical Gate Length 为 `L`，则编译器必须冻结：

```text
Instance / template origin I = A - O
template tick t -> absolute tick I + t
Logical Gate Start = A
Logical Gate End = A + L（随后服从 Segment End / consumer end 硬裁剪）
instance-local Gate horizon = O + L
```

`O` 不进入 Logical Note 源字段，不改变 `MappingContext.gateLength`，也不用于 Pure MIDI、Event Instrument standalone Preview、SubVoice standalone Preview 或 Segment Pitch Ruler audition。
### 12.7.2 MappingContext 基础输入
Logical Note 的以下信息应进入 MappingContext：
```text
triggerNote / triggerPitch
triggerVelocity
pitchDelta
Gate Length
Logical Track 信息
Segment 信息
Segment absolute startTick
segmentLocalTick
projectTick
templateTick
Event Instrument 信息
SubVoice 信息
Logical Parameter 信息
目标事件类型
目标参数 key
```
具体字段名称和类型由实现设计阶段细化。

其中 `projectTick` / `segmentLocalTick` 使用提前后的实际事件位置，`templateTick` 保持模板局部位置，`Gate Length` 保持有效 Logical Gate Length而不包含 Pre-Roll。
### 12.7.3 无输出实例
被触发的 Event Instrument Instance 如果最终完全不产生任何 MIDI / 高级事件输出：
```text
不分配 Channel Group
不占用 Channel Unit
```
但如果实例包含以下任意可能输出内容：
```text
Initial State
Reset
Program Change
Bank Select
CC
Pitch Bend
RPN
NRPN
Pitch Bend Range
Logical Parameter Mapping 输出
其他非 Note MIDI / 高级事件
```
则仍可作为有效输出实例，并需要分配 Channel Unit。
### 12.7.4 空 SubVoice
初版中，只要 Event Instrument Instance 需要分配 Channel Group：
```text
Event Instrument 内所有 SubVoice 都计入 Channel Unit 需求。
```
空 SubVoice 不被编译器自动忽略。
空 SubVoice 是合法状态，不产生诊断。
---
## 12.8 Logical Parameter 编译
### 12.8.1 由编译器统一解析
Logical Parameter Lane 的状态解析由编译器统一负责。
包括：
```text
稀疏状态继承
defaultValue
Segment 裁剪窗口
断裂 Lane
Mapping 合成
曲线离散化
与 SubVoice 原始事件状态的合成
```
编辑器不应在保存 Project 时预先展开成完整参数曲线。
播放系统也不应绕过编译系统直接解释 Lane 数据。
### 12.8.2 状态继承
Logical Parameter 有效值按状态继承：
```text
没有新值时继承上一个有效值。
没有上一个有效值时使用 Logical Parameter defaultValue。
```
没有 Lane 或没有有效点时：
```text
使用 defaultValue。
```
### 12.8.3 Segment 内继承边界
Logical Parameter 状态不跨 Segment 继承。
规则：
```text
Segment 是 Reset 边界。
Logical Parameter Lane 的状态继承只在当前 Segment 有效裁剪窗口内解析。
```
Segment 连接后：
```text
连接后的 Segment 作为一个 Segment 解析。
连接点前的有效参数状态可影响连接点后的状态继承。
```
Segment 分割后：
```text
Split 命令需要计算分割 tick 的 Logical Parameter 有效状态。
为了保持参数状态及相关曲线在分割前后的听感一致，右侧 Segment 应把必要的起点状态保留或生成为显式源数据。
```
具体 UI 与数据写入方式由 第 17～20 章的 UI 与交互规格 / 第 16 章《.midora 文件格式与持久化》 或实现设计阶段细化。
### 12.8.4 裁剪窗口外参数点
Segment 裁剪窗口外的 Logical Parameter 点：
```text
不参与当前 Segment 编译。
隐藏数据保留。
```
Segment 裁剪窗口开始处：
```text
需要解析裁剪窗口前的最近有效点，用于确定裁剪窗口开始处的参数有效状态。
该点本身不作为范围内用户点输出。
```
### 12.8.5 断裂 Lane
如果 Logical Parameter Definition 被删除或重绑后无法解析，Segment 中旧 Lane 数据保留。
编译时：
```text
断裂 Lane 不参与输出。
断裂 Lane 不参与状态继承。
有效 Track 中的断裂 Lane 产生 Warning。
```
如果用户启用 Warning 导致编译失败，则本次编译失败。
### 12.8.6 曲线离散化
SubVoice Value Curve、Logical Parameter Lane 与 Mapping、Envelope Mapping 等连续值源的离散化属于编译系统职责。

正式参考语义为：
```text
在该连续值源的有效左闭右开 tick 范围内，对每个整数 tick 求值。
在完整映射链结束后，按照目标参数配置执行唯一一次最终取整与越界处理。
输出该连续值源在范围内的第一个有效最终整数值。
此后仅当最终整数值相对该连续值源上一次输出发生变化时才输出新事件。
```
因此，线性、指数或其他连续曲线跨越整数目标中点时，变化事件必须落在逐整数 tick 求值后首次得到新最终整数值的 tick。该规则适用于实时播放、预览、MIDI 导出与音频渲染共同消费的 canonical compiled result。

实现允许使用分段分析、跳跃求值、缓存或其他优化，前提是其 canonical 事件、tick、最终整数值、来源追踪和诊断与上述逐整数 tick 参考算法完全一致；误差阈值、自适应采样或其他近似算法不得改变正式结果。

直接 Event Mapping 的 Envelope/连续值求值必须以目标原始 MIDI 状态为持有基值：最近一个原始事件值持续有效，首个原始事件之前从合并 Initial State/default 取得。连续派生输出不能反向覆盖这份原始状态。Release 的最后一个有效整数 tick 必须达到 End Value，之后才进入普通 NoteOff 排序；普通 instance 结束不追加通用目标 Reset。
### 12.8.7 多 Mapping 作用同一目标
多个 Logical Parameter Mapping 作用同一 SubVoice 目标参数时：
```text
按 Event Instrument 内显式 Mapping 顺序依次计算。
同 tick / 同目标最终只输出最终值。
```
Logical Parameter Mapping 输出非法值：
```text
默认编译失败，并定位到 Logical Parameter、Mapping、目标 SubVoice、目标参数和触发上下文。
```
除非 Mapping Step 显式配置 Clamp 等合法策略。
---
## 12.9 Mapping Function 与数值处理
### 12.9.1 Mapping Function 编译错误
如果 Mapping Function 编译错误，且该函数被实际编译路径使用：
```text
Error
```
如果未被实际编译使用，但属于无效或未引用 Event Instrument：
```text
可按既有规则诊断为 Warning。
```
未知 `abiVersion`、旧自由 C# ABI v1/v2、ABI v3 表达式超出语法/API/资源白名单、或持久化 Context 依赖与正式推导不一致，均按 Mapping Function 编译错误处理。
### 12.9.2 运行时异常
Mapping Function Expression 求值失败：
```text
当前编译失败
产生 Error
定位到函数、Mapping、目标事件、触发上下文
```
不允许捕获后使用默认值继续。
### 12.9.3 只读 Context
MappingContext 默认只读。
Mapping Function Expression 没有可修改 Project 或外部状态的语言能力。
### 12.9.4 确定性要求
ABI v3 只允许确定性的数值/枚举输入和纯数值运算。随机数、时间、I/O、网络、进程、反射和全局可变状态无法从该语言到达。表达式解析、白名单、限制、绑定规则和源码 hash identity 必须固定；不得依赖运行机器 API 面、缓存历史或集合遍历顺序改变结果。

每个 Project 的 Mapping 表达式缓存只保留当前源码修订。该缓存只影响性能，不得改变 Full/Incremental 输出或诊断。
### 12.9.5 非法返回值
Mapping Function Expression 返回以下结果时：
```text
NaN
Infinity
类型不匹配目标参数
```
视为非法映射结果，编译失败。
### 12.9.6 整数目标取整
映射输出到整数 MIDI 参数时：
```text
必须有明确取整策略。
```
系统默认取整策略固定为 `Round`，midpoint 使用 Away From Zero；用户可在整数目标参数上显式选择 `Round`、`Floor` 或 `Ceil`。取整只在完整映射链得到最终输出后执行一次，不得挂在 Mapping Step 上或在每一步后重复量化。
如果 Multiply / Divide 等映射结果产生小数，但目标为整数：
```text
使用该目标参数配置的取整策略。
若未显式配置，则使用系统默认 `Round / Away From Zero`。
```
### 12.9.7 越界与 Clamp
映射输出越界时：
```text
默认 Error。
```
除非目标参数的最终越界策略显式配置为 Clamp。
Mapping Step 中的 Clamp 操作只限制映射链中间值，不代替目标参数的最终越界策略；Remap Range 的输入越界策略仍属于该 Mapping Step。
目标参数 Clamp 是用户显式选择的合法最终处理，不产生诊断。
Remap Range 输入范围为 0：
```text
编译失败，除非该 Mapping Step 明确配置了除零 fallback。
```
---
## 12.10 Initial State、Reset 与裁剪
### 12.10.1 Initial State 注入
Initial State Defaults 由编译器注入。
它不作为普通时间线事件保存。
Initial State 注入在：
```text
实例开始 tick
```

对 Pre-Roll 实例，该 tick 是提前后的 Instance Origin `A - O`，不是 Logical Gate Start `A`。Reset Defaults、Initial State、模板 tick 0 用户状态与参数映射必须在这个实际时间点按正式优先级建立；不得读取并倒灌 anchor 时刻才生效的未来 Logical Parameter 状态。
并在语义排序上早于该 tick 的模板用户事件。
如果 Initial State 与同 tick 用户事件冲突：
```text
用户事件优先。
```
如果范围起点恢复状态与实例 Initial State 冲突：
```text
Initial State 优先。
```
### 12.10.2 Segment End 硬边界
Segment End 是硬裁剪边界。
规则：
```text
实例不得越过 Segment End。
Release / Tail 不允许越过 Segment End。
越界时应在 Segment End 处插入必要实际 MIDI Note Off，然后 Reset。
```
Segment End 裁剪是正常语义，不产生 Warning。

发声 Segment 的 canonical allocation/audio Unit fragment 必须覆盖到 Segment End，使普通 instance NoteOff 之后的 SoundFont 原生 release 仍能进入实时、离线与缓存 PCM。不得把 instance lifecycle end 误作 PCM 硬结束。自然编译范围使用实际生成 instance 所属 Segment 的 Segment End；完全位于 Content Window 外而未生成 instance 的 Segment 不延长范围。
### 12.10.3 Project End Marker 与范围结束硬边界
当 Project End Marker 作为编译范围结束时：
```text
Project End Marker 不删除 Project 内容。
范围外输出不进入本次 compiled result。
跨界实例按硬边界裁剪。
```
Project End Marker 裁剪是正常语义，不产生 Warning。
用户显式选择范围编译时，范围结束裁剪跨界实例也是预期行为，不产生 Warning。
### 12.10.4 Reset 插入
Reset 由编译器统一插入。
Reset 基于：
```text
Project / Global Reset Defaults
实例实际使用过的状态
可能污染的状态集合
Segment 边界
lane 首次启用或非重叠复用
范围硬边界
```
用户不需要手动画 Reset。
Reset 只作用于该 Segment / 实例实际使用或污染过的状态集合，不应无脑重置所有 CC / RPN / NRPN。
Segment End Reset 只作用于该 Segment 使用过 / 污染过的 Channel Unit 状态，不重置整个 Project。

Project / Global Reset Defaults 定义 lane 激活和硬边界的目标基线。lane 激活时，编译器对该 SubVoice 实际可能使用的状态目标闭包先应用 Reset Defaults，再应用合并 Initial State、用户 tick 0 状态事件和 NoteOn；同目标同 tick 可折叠为唯一最终值。普通 Gate/Release/Tail 结束只依赖精确 NoteOff，不追加通用目标 Reset 或 CC120。CC120 只允许用于 Segment End、Project End Marker/显式范围结束等硬裁剪边界。
### 12.10.5 Reset 与 Channel Unit 释放
Reset 计入 Channel Unit 占用时间。
规则：
```text
Reset 完成前 Channel Unit 不可释放。
产生过 Note 的 Segment-owned lane 在普通 instance 结束后保持当前状态并保留到 Segment End，以承载原生 release、保证 Segment PCM/Track 运行时归属，并在硬边界安全执行 CC120。
```
同一 Segment 的 lane 在没有仍重叠 instance 后被另一个 instance 非重叠复用：
```text
允许复用同一 Channel Unit。
新实例起点必须按 Reset Defaults → Initial State → 用户事件 → Note On 建立状态。
```
共享 lane 内仍有重叠 instance 时，后续 Gate Start 不重复执行 lane 激活 Reset/Initial State；它只能输出自身正式模板、Mapping 与 Note 事件。需要独立 Channel-Wide 起点状态的重叠实例必须使用 Channel Isolation。
上述同 tick 普通复用只适用于同一 Segment 的保留 lane；跨 Segment 的物理 Unit 复用必须等待前一 Segment End 硬清理完成。
### 12.10.6 编译器生成事件标记
以下事件应标记为编译器生成事件：
```text
范围恢复事件
Initial State 注入事件
Reset 事件
补充 Note Off
裁剪生成事件
```
这些事件应尽量保留来源说明，例如：
```text
范围起点状态恢复
Initial State Defaults
Global Reset Defaults
Segment End 裁剪
Project End Marker 裁剪
显式范围结束裁剪
```
因硬裁剪生成的补充 Note Off 应标记为裁剪生成。

### 12.10.7 Pure MIDI Root 生命周期

第 12.10.1～12.10.5 节描述 Logical/Event Instrument lane 的 Initial State、Reset 与释放。Pure MIDI 路径不得在每个子 Midi Segment End 独立执行 Channel-wide Reset。

Pure MIDI 编译必须先对同一 Root 的全部已选择 Segment 求活动连通区间：子 Segment End 只精确关闭该 Segment 拥有的活动 Note，不重置共享 CC/Bank/Program/Pitch/RPN/NRPN，也不杀死 sibling Track 的 Note；只有 Root 活动连通区间结束、Project End Marker 或消费者范围结束才执行 Root 级精确 NoteOff、CC120、最终 Reset 和 Unit 释放。完整规则见第 23.7 节。

### 12.10.8 Event Instrument Usage 生命周期

未启用逐音符隔离时，编译器必须对同一 Usage 的全部已选择 Logical Segment 求活动连通区间，并按 SubVoice 共享 Channel Unit。子 Segment End 只精确关闭该 Segment 的 Instance/Note；Usage 连通区间结束才做 Usage 级 CC120、最终 Reset 和释放。启用逐音符隔离时 Unit 仍按实例分配，但 Overlap、来源与 dirty owner 继续属于 Usage。

Usage 活动连通区间、Overlap 扫描和 lane 激活起点必须包含 Pre-Roll 提前后的 Instance Origin；不得以 Logical Note anchor 代替起点。Pre-Roll 可以把原本分离的实例区间连接起来并提高同一编译上下文中的资源峰值。
---
## 12.11 同 tick 语义排序
### 12.11.1 系统级排序原则
第 12 章《编译系统与 Canonical Compiled Result》 定义系统级同 tick 语义排序原则，不进入 MIDI 字节级排序表。
完整 MIDI 字节级排序由 第 14 章《MIDI 导出》 或实现设计阶段细化。
总体分层：
```text
状态准备
音符事件
释放 / Reset
```
### 12.11.2 状态先于 Note On
同 tick 上，以下状态事件应早于依赖它们的 Note On：
```text
Bank Select
Program Change
Pitch Bend Range
Pitch Bend
CC
RPN
NRPN
Logical Parameter Mapping 输出的状态事件
Initial State
范围恢复状态
```
### 12.11.3 特定状态顺序
Bank Select 与 Program Change 同 tick：
```text
Bank Select 先于 Program Change。
```
Pitch Bend Range 与 Pitch Bend 同 tick：
```text
Pitch Bend Range 先于 Pitch Bend。
```
RPN / NRPN 高级事件封装由编译器展开为正确语义顺序。
### 12.11.4 Note Off、Note On 与 Reset
同 tick 上 Note Off 与同 pitch / 同 Channel Unit 的 Note On 同时存在：
```text
Note Off 先，Note On 后。
```
同 tick 上 Note Off 与 Reset 同时存在：
```text
Note Off 先，Reset 后。
```
同 tick 上旧实例 Note Off、lane 激活 Reset 与新实例 Initial State 同时存在：
```text
旧实例 Note Off 先，lane 激活 Reset 次之，新实例 Initial State 后。
```
### 12.11.5 同目标参数冲突优先级
同 tick 上同一 Channel Unit、同一目标参数出现多个最终值时：
```text
编译器折叠为最终有效值，只输出一个。
```
优先级规则：
```text
范围起点恢复状态 < Initial State < 用户模板事件 < Logical Parameter Mapping 输出
```
说明：
```text
范围恢复状态只提供进入 startTick 前的上下文。
Initial State 是实例默认起始状态。
用户模板事件是模板内显式事件。
Logical Parameter Mapping 表示编曲层对模板原始值的外部控制结果。
```
多个 Logical Parameter Mapping 作用同一目标时：
```text
按 Event Instrument 内显式 Mapping 顺序依次计算。
最后一个结果成为该 tick 最终输出。
```
### 12.11.6 Note 事件不按状态事件折叠
Note On / Note Off 不允许像 CC 一样按同目标最终值折叠。
原因：
```text
Note 事件具有配对和生命周期语义。
不能按普通状态事件处理。
```

### 12.11.7 Pure MIDI 原始事件顺序

第 12.11.1～12.11.5 节的状态优先级和同目标折叠适用于 Logical/Event Instrument 展开结果与编译器生成事件，不得用于重排或折叠 Pure MIDI 原始事件。

同一 Root 的 Pure MIDI 原始事件总顺序固定为 `absolute tick → global Arrangement Track order → event explicit order`。同 tick、同类型、同控制器或互相冲突的重复事件均须保留；编译器生成的 Root 初始化/硬边界事件按明确 canonical role 插入。见第 23.4.2、23.6.3 和 23.9.3 节。

同一 Event Instrument Usage 的 Logical 事件在 canonical role/sequence 规则内，以 global Arrangement Track order 作为跨 Track 稳定次序；不得依赖集合遍历或后台完成顺序。
---
## 12.12 跨 tick 事件折叠优化边界
第 12.8.6 节规定的连续值源重复值抑制属于离散化定义的一部分：未变化的逐 tick 候选值不会生成 canonical 事件，不属于本节所称的跨 tick 事件折叠。

除该离散化规则外，初版不对已经生成的跨 tick 连续相同状态事件做全局折叠。
即：
```text
来自显式源事件或不同来源的连续相同 CC / Pitch Bend / Program / Bank / RPN / NRPN 输出，即使理论上可压缩，初版也应保留编译生成结果。
```
原因：
```text
实时播放 / 渲染链路中做该优化会增加复杂度。
性能收益相对有限。
初版性能优化重点应放在编译过程、缓存策略、播放 / 渲染主路径，尤其是音频渲染。
避免优化行为意外影响调试、诊断、来源追踪和事件可解释性。
```
后续版本允许增加安全的事件折叠优化，但必须证明：
```text
不改变语义
不破坏诊断来源
不破坏播放 / 渲染 / 导出一致性
```
注意：
```text
同 tick 同目标多值冲突仍必须折叠。
连续值源内部按第 12.8.6 节抑制未变化的最终整数值。
已经生成的跨 tick 连续相同值初版不做全局折叠。
```
---
## 12.13 Channel Unit 占用
### 12.13.1 占用区间
Channel Unit 占用区间采用左闭右开：
```text
[occupyStartTick, occupyEndTick)
```
occupyEndTick 应包含必要 Release、Tail、Note Off 和 Reset 完成后的释放边界。

Logical/Event Instrument 实例的 `occupyStartTick` 使用 Pre-Roll 后的 Instance Origin。Gate End 仍由 Logical Note anchor 与 effective Gate Length 决定，因此未裁剪时 origin 到 Gate End 的局部跨度为 `Pre-Roll Ticks + Gate Length`。
### 12.13.2 资源占用与输出事件分离
某个实例在某段范围内没有恰好输出事件，不代表它不占用资源。
如果该实例生命周期仍占用 Channel Unit：
```text
必须记录占用。
```
范围前已触发实例如果在范围内没有任何输出，但 Channel Unit 理论上仍处于占用期：
```text
仍需参与资源占用判断。
```
---
## 12.14 Port / Channel / Channel Unit 分配策略
### 12.14.1 本章固定核心思路，不锁死实现细节
本章确定初版 Port / Channel 分配的核心思路。
未来实际实现时，可以在保证以下核心思路不变的前提下调整细节：
```text
确定性
低号优先
上下文内紧凑
不依赖历史缓存
不依赖随机或线程竞态
增量编译对外结果等价全量编译
资源不足不降级语义
```
也就是说，本章不是实现设计的最终算法说明。
### 12.14.2 Channel Unit 全局顺序
初版采用确定性的低号优先 Channel Unit 分配策略。
Channel Unit 全局顺序为：
```text
Port 1 / Channel 1
Port 1 / Channel 2
...
Port 1 / Channel 16
Port 2 / Channel 1
...
Port 16 / Channel 16
```
Channel 10 参与同一 Unit 编号顺序。Logical/Event Instrument 使用它时必须为 melodic；Pure MIDI Root 使用它时由 Root 的 Melodic/Percussion 模式决定。
### 12.14.3 分配基本规则
编译器必须先按第 23.8 节完成 Pure MIDI Root 分配，再分配 Logical/Event Instrument Channel Group：
```text
验证全部非空 Fixed Root 的精确 Unit；结构上不允许空 Root。
按最早成员 global Arrangement Track order 把每个含参与 Segment 内容的 Auto Root 分配到最低未保留 Unit。
一个 Root 在本次 CompileContext 内固定占用同一 Unit，不与 Logical instance 做时间复用。
然后按编译时间推进 Logical Event Instrument Usage 分配。
释放所有已到达 Segment/消费者硬边界并完成最终 Reset 的 Channel Unit。
从当前可用 Channel Unit 中选择编号最低的一组。
按 Event Instrument 内显式 SubVoice 顺序映射到这些 Channel Unit。
根据 Rendered Instance Length 做共享/隔离 lane coloring；产生过 Note 的实际 lane 从首次启用占用到 Segment End 最终 Reset 完成。
```
### 12.14.4 对外 Port 编号与固定路由
没有 Fixed Root 时，对外可见的自动分配结果不应保留无意义 Port 空洞。
例如：
```text
如果本次编译上下文最多只需要 1 个 Port，则只使用 Port 1。
如果最多需要 2 个 Port，则使用 Port 1–2。
如果最多需要 N 个 Port，则使用 Port 1–N。
```
不应出现无意义布局：
```text
Port 1 使用
Port 2 空
Port 3 使用
```
Fixed Root 是初版正式功能；它可以有意保留 Port / Channel 空洞。任何 Compact Routing 都不得移动 Fixed Root，Auto Root 与 Logical allocation 必须绕开固定 Unit。以下项目仍属于初版外功能：
```text
外部设备路由
每 Project/Port/Track/Instrument 独立 SoundFont 列表
```
### 12.14.5 不保留历史稳定分配
初版不采用“历史稳定分配”。
不采用：
```text
为了稳定，尽量保留之前已经分配过的 Port / Channel。
即使当前项目重新全量编译会得到更紧凑结果，也保留旧布局。
```
原因：
```text
同一 Project 内容可能因为编辑历史不同导出不同 Port / Channel 结果。
重新打开项目后全量编译可能和编辑过程中的增量编译结果不同。
调试、诊断、导出复现都会复杂化。
Port 空洞会长期积累且难以解释。
```
### 12.14.6 输入决定结果
Port / Channel 分配结果应由以下内容唯一决定：
```text
当前 Project 内容
当前 CompileContext
当前 Source 状态
确定性排序规则
```
不得由以下内容决定：
```text
历史缓存
上一次编译结果
编辑顺序
随机数
线程竞态
不稳定容器遍历顺序
```
### 12.14.7 同 tick 多实例分配顺序
同 tick 上多个实例需要分配 Channel Group 时，应使用显式用户结构顺序。
建议系统级排序：
```text
tick
→ Track 显示 / 项目保存顺序
→ Segment 时间与显式顺序
→ Logical Note 显式顺序 / UI 顺序
→ Event Instrument 内 SubVoice 顺序
```
对象 ID 只表示身份，不应作为用户可感知音乐优先级。
如果未来实现中需要最终 tie-breaker，可以使用稳定 ID 作为完全不可区分对象的确定性兜底，但不得把 ID 顺序写成用户可感知的资源优先级语义。
### 12.14.8 Track 顺序不是抢资源语义
Track 顺序可用于：
```text
UI 显示
诊断显示
确定性辅助顺序
```
但不应成为用户解决资源不足的主要语义手段。
不采用：
```text
Track 越靠前越优先抢资源。
后面 Track 因资源不足被牺牲。
```
Midora 不支持 Voice Steal，因此不存在“抢占后面 Track”的语义。
---
## 12.15 资源不足与资源诊断
### 12.15.1 不允许降级语义
资源不足时，不允许编译器自动降级语义以继续编译。
禁止：
```text
Voice Steal
删音
截尾
自动合并 SubVoice
自动忽略 SubVoice
降低 Per-Note Instance Isolation 语义
降低 Channel-Wide 隔离语义
```
### 12.15.2 硬失败条件
以下情况为 Error：
```text
两个 Fixed Root 指向同一 Unit。
Root 分配与 Logical/Event Instrument 峰值组合后超过 256 Units。
单个 Event Instrument Instance 所需 Channel Unit 超过 256。
任一 tick 同时占用 Channel Unit 超过 256。
无法为某个 Channel Group 原子分配完整 Channel Unit。
```
### 12.15.3 峰值使用量 Info
当 Channel Unit 峰值使用量达到：
```text
248
```
诊断级别为：
```text
Info
```
不是 Warning。
原因：
```text
248 尚未达到 256 硬限制。
如果用户启用“Warning 导致编译失败”，不应因 248 仍未达到硬限制而阻止编译。
```
### 12.15.4 单实例 SubVoice 接近上限
单个 Event Instrument 的 SubVoice 数量接近 256 不额外产生 Warning。
规则：
```text
只有实际资源峰值达到 248 时产生 Info。
单实例所需 Channel Unit 超过 256 时才产生 Error。
```
### 12.15.5 资源统计
compiled result 应记录：
```text
Channel Unit 峰值使用量
Fixed / Auto Root 保留与分配数量
Logical/Event Instrument 峰值及二者 combined peak
Port 使用数量
每个 Event Instrument Instance 的 Channel Group 占用区间
每个 Channel Unit 的占用区间
资源不足失败 tick / 范围
相关 MIDI Channel Root / Pure MIDI Track / Midi Segment / Direct Event，以及 Logical Track / Segment / Logical Note / Event Instrument / SubVoice 数量
```
具体字段结构由实现设计确定。
资源不足 Error 应尽量定位到导致峰值或失败的实例集合，而不是只提示“资源不足”。
---
## 12.16 Per-Note Instance Isolation 关闭时的编译语义
### 12.16.1 重叠 Note 仍各自触发
Per-Note Instance Isolation 关闭时，同一 Event Instrument Usage 内、可跨多个成员 Logical Track 的重叠 Logical Note：
```text
仍按各自 Logical Note 触发模板事件。
但共享同一 Channel Group / Channel-Wide 状态。
```
不自动合并为一个触发。
### 12.16.2 不兼容功能检查
编译器仍需检查不兼容功能。
如果某功能依赖每音符独立 Channel-Wide 状态，例如 Loop / Envelope 等要求实例隔离的功能，并且在实际编译路径使用：
```text
编译失败。
```
应定位到：
```text
Event Instrument
相关功能
Logical Track
Segment
Logical Note
触发上下文
```
### 12.16.3 Channel-Wide 状态冲突
Per-Note Instance Isolation 关闭时，多个重叠 Note 共享 Channel Group 后：
```text
不能表达每个重叠音符独立的 Logical Parameter 状态。
```
如果产生互相冲突的 Channel-Wide 状态：
```text
若属于已定义为不兼容的每音符独立状态需求，则编译失败。
普通同 Channel 状态覆盖按明确排序处理。
```
### 12.16.4 不兼容数据保留
Per-Note Instance Isolation 关闭时，不兼容数据仍保留在 Project 中。
规则：
```text
不自动删除。
当前关闭状态下不可编辑或不参与编译。
未来重新开启 Per-Note Instance Isolation 后应恢复可用。
```
---
## 12.17 Canonical compiled result 结构要求
canonical compiled result 至少应能表达：
```text
编译上下文
全局事件
Channel Unit 事件
Port / Channel Unit 归属
事件来源追踪
资源占用区间
诊断集合
资源统计
是否可消费
是否 partial result
Execution Projection
冻结的 SMF Track Projection / descriptor
```
### 12.17.1 按 Port / Channel Unit 组织
最终结果应能明确定位每个 MIDI / 高级事件属于哪个：
```text
Port
Channel Unit
```
### 12.17.2 全局事件与 Channel 事件区分
编译结果应区分：
```text
全局事件：Conductor Track / Meta Event
Channel 事件：Note / CC / Pitch Bend / Program / Bank / RPN / NRPN 等
```
### 12.17.3 来源追踪
每个可诊断事件应尽量保留来源，例如：
```text
Logical Track
Segment
Logical Note
Event Instrument
SubVoice
模板事件
Logical Parameter
Logical Parameter Mapping
Mapping Function
Initial State Defaults
Global Reset Defaults
MIDI Channel Root
Pure MIDI Track
Midi Segment
Direct MIDI Note / Event
Opaque imported event
ExportTrackId
编译器生成原因
```
来源追踪用于：
```text
诊断定位
Debug
事件解释
后续可视化
问题排查
```
具体来源字段结构由实现设计确定。
### 12.17.4 资源占用区间
编译结果应记录资源占用区间，用于：
```text
诊断
资源可视化
播放
渲染
增量编译
Debug
```
### 12.17.5 编译上下文记录
编译结果应记录本次 CompileContext 的系统级摘要，例如：
```text
范围
Track 选择
是否全项目
是否播放
是否预览
是否导出准备
是否音频渲染准备
Warning 失败策略
```

### 12.17.6 执行投影与 SMF Track 投影

同一 canonical 事件集必须冻结两个一致投影：按 Channel Unit/Root 合并并形成总序的 Execution Projection，供播放和音频渲染消费；按 `ExportTrackId` 保留 Pure MIDI Track 拓扑的 SMF Track Projection，供 MIDI 导出消费。SMF Track descriptor 至少冻结 Track Name、顺序、Port/Channel、Root mode、EOT 与事件归属；导出器不得回读 Project 重新推断。详见第 23.9.2 节。
---
## 12.18 语义验证阶段
### 12.18.1 先验证再展开
编译前应先执行语义验证阶段。
目的：
```text
尽量收集结构、引用、值域、范围、兼容性错误。
避免展开到一半才发现基础结构非法。
```
### 12.18.2 尽量收集多个诊断
即使最终编译失败，也应尽量返回可定位的诊断集合。
不采用：
```text
遇到第一个错误立即停止并只返回一个错误。
```
但某些后续阶段依赖前置结果时，可以因无法继续而停止后续展开。
### 12.18.3 常见语义错误
以下情况可能导致 Error：
```text
Logical Note length <= 0
Event Instrument Pre-Roll Ticks 不在 0..Template Length 范围内
Logical Note 的 Pre-Roll Instance Origin 早于所属 Segment 有效起点或产生 tick 算术溢出
同一 Track 内 Segment 重叠
实际输出 MIDI Note number 越界
基础 MIDI 参数非法
Event Instrument/SubVoice 数据或 Mapping 目标中出现该路径明确不支持的 CC91 / CC93
映射后值非法且未配置合法处理策略
Mapping Function 编译错误并被实际使用
Mapping Function 运行时异常
NaN / Infinity 映射结果
类型不匹配目标参数
资源不足
Per-Note Instance Isolation 关闭时使用不兼容功能
Time Signature Denominator 与 Project TPQ 不满足 `4 × TPQ % Denominator == 0`
```
---
## 12.19 诊断系统
### 12.19.1 诊断级别
初版诊断级别为：
```text
Error
Warning
Info
Debug
```
### 12.19.2 Error
Error 的系统级含义：
```text
阻止当前编译结果生成可用输出。
```
出现 Error 时：
```text
播放 / 导出 / 渲染消费者必须拒绝消费失败结果。
```
### 12.19.3 Warning
Warning 的系统级含义：
```text
编译可成功，但结果存在用户应注意的风险或非理想状态。
```
Warning 默认不导致编译失败。
### 12.19.4 Info
Info 的系统级含义：
```text
不影响编译成功，只提示用户项目状态或被忽略内容。
```
例如：
```text
Damaged Parent Placeholder 下的内容未进入正式编译。
Channel Unit 峰值达到 248。
```
### 12.19.5 Debug
Debug 用于记录编译过程中对开发、排错、性能分析、资源分配分析有用的信息。
包括但不限于：
```text
资源分配过程
缓存命中 / 失效
曲线离散化统计
编译耗时
状态收敛判断
增量重编范围
```
Debug 默认不显示，不影响编译成功。
启用 Debug 显示不得改变事件输出、资源分配和成功判定。
Debug 诊断收集允许带来额外性能成本，但应可控；具体采集策略由实现设计确定。
### 12.19.6 诊断显示级别
用户可以设置诊断面板显示到哪一级别。
默认：
```text
显示到 Info
```
最少：
```text
必须显示 Error
```
最多：
```text
显示到 Debug
```
显示级别只影响 UI 展示，不影响诊断实际收集和编译成功判定。
### 12.19.7 Warning 导致编译失败
用户可以启用：
```text
强制 Warning 导致编译失败
```
默认关闭。
启用后：
```text
只要本次编译产生任何 Warning，本次编译视为失败。
```
但：
```text
Warning 本身仍是 Warning，不被改写为 Error。
Project 数据不改变。
Info / Debug 永远不导致失败。
```
### 12.19.8 Overlap Reject 与 Warn
同一 Event Instrument Usage 内发生第 10.17 节定义的策略范围内重叠时：
```text
Overlap Strategy = Reject -> Error
Overlap Strategy = Warn   -> Warning
```
`Reject` 必须使当前编译结果不可消费。`Warn` 默认保留两个实例并允许结果被消费；如果本次 CompileContext 启用“强制 Warning 导致编译失败”，则结果不可消费，但 Overlap 诊断的级别仍为 Warning。

### 12.19.9 Time Signature 截断 Warning
Time Signature 变化 tick 立即开启新小节。如果该 tick 不是前一个拍号段起点之后的自然完整小节边界，编译器必须产生一条 Warning，定位到该 Time Signature 稳定 ID 和 tick。Warning 默认不阻止消费；启用 Warning-as-error 时按第 12.19.7 节使结果不可消费，但诊断级别仍为 Warning。
---
### 12.19.10 完整诊断容量与有界展示

正式诊断的逻辑总数、ordinal 和各严重程度计数使用非负 Int64，最多 `9,223,372,036,854,775,807` 条。同一输入的顺序、重复次数、SourceReference 和失败策略保持不变；不得在 Int32 边界截断、聚合、降级或丢弃诊断。ordinal 只属于本次冻结结果，不是业务 stable ID，不跨修订复用。

允许用共享来源和紧凑 range 表示大量重复诊断，按需物化单行；Canonical、异常与消费者不得通过 Int32 Count 接口静默截断完整总数。物理来源、range、索引和筛选工作仍受实际资源约束，Int64 不是无限内存承诺。

逻辑计数超过 Int64 时必须以专用、明确的诊断容量错误中止本次编译，不发布半份新诊断或可消费结果；之前完整结果仅保留为旧结果，不得冒充当前成功结果。不得把无关算术溢出或内存不足误报成诊断计数超限。

Diagnostics 条件分页见 §17.5；MIDI README 的前 1000 条文本上限见 §14.15.4。两种展示边界都不得改变完整诊断及 §12.19.7 的 Warning-as-error 判定。

---
## 12.20 编译成功判定与失败结果
### 12.20.1 成功判定
当没有 Error，且 Warning 未被用户策略强制失败时：
```text
编译成功。
```
Info / Debug 不影响成功。
Debug 永远不影响成功。
### 12.20.2 失败时返回信息
编译失败时仍应尽量返回：
```text
诊断集合
失败阶段
相关对象定位
可能的部分资源统计
不可消费的 partial compiled result
```
partial compiled result 必须明确标记为：
```text
不可播放
不可导出
不可渲染
仅用于诊断和调试
```
消费者不得消费失败结果。
---
## 12.21 缓存与增量编译
### 12.21.1 缓存是必要能力
初版必须把编译缓存与增量编译作为必要的性能能力。
原因：
```text
播放随机定位需要快速获得上下文。
局部编辑后不应每次全项目完整重算。
Logical Parameter 曲线、生命周期、资源分配、Reset 展开都可能较重。
音频渲染和播放主路径需要稳定性能。
```
### 12.21.2 增量编译语义等价
增量编译结果必须与全量编译结果语义一致。
规则：
```text
增量编译只是性能优化。
不得改变音乐语义。
不得改变诊断结果。
不得改变 Port / Channel 分配结果。
不得改变同 tick 排序结果。
不得改变 canonical compiled result 对外内容。
```
### 12.21.3 Checkpoint + Dirty Range + State Hash 收敛模型
本规格规定增量编译的系统级策略：
```text
1. 编译器按确定性规则全量编译时，生成若干编译检查点 Checkpoint。
2. 每个 Checkpoint 记录该 tick 边界处的完整编译状态摘要。
3. 当 Project 局部修改后，编译器找到最早受影响位置。
4. 回退到该位置之前最近的可用 Checkpoint。
5. 从该 Checkpoint 开始按全量编译规则重新编译。
6. 如果重新编译到后续某个 Checkpoint 时，新的编译状态与旧 Checkpoint 完全等价，且后续 Source 未变，则可以复用旧结果后缀。
7. 如果不能证明状态等价，就继续向后重编。
8. 最坏情况下重编到本次编译上下文结束。
```
这是系统级模型，不规定具体数据结构。
### 12.21.4 Checkpoint 状态摘要
Checkpoint 至少应能判断：
```text
从这里继续编译，是否一定会得到和全量编译一样的后续结果。
```
状态摘要可包含但不限于：
```text
当前 tick 边界
当前有效 Conductor 状态
当前活动 Event Instrument Instance 集合
每个活动实例生命周期阶段
每个活动实例 Channel Group 分配
每个 Channel Unit 占用状态
每个 Channel Unit 当前 MIDI 状态
当前 Logical Parameter 有效状态
当前 Reset / Release / Tail 待处理状态
当前资源分配器状态
当前 CompileContext
相关 Source 依赖 fingerprint
```
具体字段结构、hash、fingerprint、cache key 由实现设计阶段细化。
### 12.21.5 状态收敛
低号优先分配意味着：
```text
前面实例的变化可能导致后面很多实例的 Port / Channel 重新分配。
```
因此增量编译不得简单保留旧分配。
正确规则：
```text
从受影响点重新按全量编译规则分配。
只有当新的编译状态与旧 Checkpoint 状态可证明等价时，才可以复用后续旧结果。
```
如果不能证明状态等价：
```text
必须继续向后重编。
```
### 12.21.6 Dirty 起点
Dirty 起点不能只看用户编辑位置。
应根据修改类型回退。
示例：
#### 12.21.6.1 修改 Logical Note
通常应回退到：
```text
该 Logical Note 所在 Segment 的有效范围起点
```
因为它可能影响：
```text
实例生成
生命周期
Segment End 裁剪
Reset
Channel Unit 占用
后续资源分配
```
#### 12.21.6.2 修改 Logical Parameter Lane / Point / Curve
由于 Logical Parameter 是状态继承，Dirty 起点至少应回退到：
```text
该 Segment 有效裁剪窗口起点
```
或回退到能证明参数有效状态一致的最近 Checkpoint。
#### 12.21.6.3 修改 Event Instrument 定义
Dirty 起点应是：
```text
所有实际使用该 Event Instrument 的触发实例中，最早可能受影响的实例起点
```

这里的实例起点必须使用 `Logical Note anchor - Pre-Roll Ticks`。修改 Pre-Roll Ticks 时，dirty 起点至少取旧值与新值各自能够产生的最早 origin；不得只从可见 Logical Note anchor 向后重编。
如果修改影响生命周期、Mapping、Reset、SubVoice 数量、资源需求，则需要从该起点向后重编直到状态收敛或编译上下文结束。
#### 12.21.6.4 修改 Conductor Track
Conductor Track 修改需要分情况处理：
```text
Tempo 改变不改变 tick 上 MIDI 事件位置，但影响 seconds 换算、播放 / 渲染时间轴缓存。
Time Signature 改变可能影响小节显示和范围上下文，但不改变 tick 事件位置。
Key Signature 是全局 Meta 状态，不影响音高。
Marker 一般不影响音乐事件编译。
Project End Marker 会影响默认编译范围和硬裁剪边界。
```
因此：
```text
Conductor Track 修改必须使相关全局状态、范围上下文、播放 / 渲染时间换算缓存失效。
Time Signature 修改还必须使 Project `Bar:Beat:Tick`、自然小节/拍网格和 Snap 派生映射失效，并重新计算中途截断 Warning。
是否导致 Channel Unit 事件流整体重编，取决于修改是否影响本次编译范围、硬边界或输出 Meta Event。
```

#### 12.21.6.5 修改 Pure MIDI 数据

修改 Direct MIDI Note/Event、opaque payload、Midi Segment 或 global Pure MIDI Track 顺序时，Dirty 起点至少回退到该 Root 中最早可能受影响的 Segment/Root checkpoint。修改 Root routing/mode 或 Track 归属时，必须重建该 Root 分配和生命周期，并在需要时使后续 Logical allocation 重编，直到完整状态收敛。

修改 Logical Track/Segment、Usage membership 或其 Definition 时，Dirty 起点至少回退到该 Usage 最早可能受影响的 checkpoint；只有共享状态和活动 Note 集收敛后才能复用后缀。不得无条件使其他 Usage/Root 失效。
### 12.21.7 增量编译不保证局部不变
用户只改了前面一个 Note，后面很远的 Port / Channel 分配也可能变化。
这不是 bug。
正确目标是：
```text
增量编译少重算。
但允许后续结果因确定性全量规则而变化。
```
不承诺：
```text
局部编辑后后方输出字节不变。
```
### 12.21.8 范围编译与增量编译区分
范围编译和增量编译不是同一概念。
```text
范围编译 = 用户 / 消费者只请求某个 tick 范围或 Track 集合。
增量编译 = 为了性能，只重算受修改影响的部分。
```
范围编译仍有完整上下文：
```text
[startTick, endTick)
Track selection
Conductor 状态
范围前活动实例
范围起点非音符状态恢复
范围结束硬裁剪
```
增量编译只是加速这个范围编译结果的生成。
### 12.21.9 Debug 一致性校验
Debug 模式可以提供：
```text
增量编译结果与全量编译结果一致性校验
```
用于开发和排错。
该校验不是用户正常编译路径的必要步骤。
---
## 12.22 缓存失效边界
### 12.22.1 Logical Note 修改
修改 Logical Note 的：
```text
start
length
pitch
velocity
```
应失效相关 Segment / Track 编译缓存。
原因：
```text
影响实例生成
生命周期
MappingContext
资源占用
最终事件流
```
### 12.22.2 Segment 操作
移动、拉伸、分割、连接 Segment 应失效相关编译缓存。
原因：
```text
Segment 裁剪窗口变化
Segment 边界 Reset 变化
Logical Parameter 状态继承变化
实例范围变化
资源占用变化
```
### 12.22.3 Logical Parameter 修改
修改 Logical Parameter Lane / Point / Curve 应失效相关编译缓存。
原因：
```text
影响 Mapping 输出
状态事件
最终事件流
后续状态继承
```
### 12.22.4 Event Instrument 定义修改
修改 Event Instrument 的以下内容：
```text
SubVoice
模板事件
Mapping
Logical Parameter Definition
Logical Parameter Mapping
生命周期
Reset
Overlap
Per-Note Instance Isolation
Mapping Function
Envelope / Loop
Pre-Roll Ticks
```
应使所有引用该 Event Instrument 的相关编译、播放、预览、渲染缓存失效。
### 12.22.5 Global Defaults 修改
修改以下内容应失效相关编译缓存：
```text
Global Reset Defaults
```
原因：
```text
可能改变 Reset 输出、资源释放和事件排序结果。
```
初版 `Global Event Scope Defaults` 是不可编辑的版本化空 marker，不参与 canonical fingerprint，也没有独立缓存失效路径。Note 与 Channel-Wide 状态的作用域继续由第 5 章及各事件专项规则固定；未来版本若增加可配置作用域，必须同步定义其缓存失效语义。
### 12.22.6 Conductor Track 修改
修改 Conductor Track 应失效相关全局状态、范围上下文、时间换算、播放和渲染缓存。
Project End Marker 修改可能改变默认编译范围和硬裁剪边界。
### 12.22.7 SoundFont 修改
选择、替换、取消 SF2：
```text
不影响 canonical compiled result。
```
但应使以下缓存失效：
```text
播放缓存
预览缓存
音频渲染缓存
```
### 12.22.8 Project Metadata 修改
Project Metadata 不影响音乐编译语义。
修改 Project Metadata 不应失效 canonical compiled result。
但它可能影响：
```text
导出 Readme
文件元信息
用户显示信息
```
### 12.22.9 Track 名称、颜色、排序
Track 名称 / 颜色不影响音乐语义，但 Pure MIDI Track 名称影响冻结 SMF Track descriptor 与导出结果。
Logical Track 排序通常不改变音乐语义，但可能影响：
```text
确定性输出辅助顺序
诊断显示顺序
同 tick tie-breaker
```
因此是否失效 canonical compiled result 由实现设计细化。

Pure MIDI Track 排序是 Root 内同 tick 合并顺序的正式语义，必须使该 Root 的 canonical、SMF 投影和相关音频缓存失效。Direct MIDI Note/Event、Midi Segment 和 Root mode/routing 的缓存失效、checkpoint 收敛与 Root PCM key 以第 23.10 节为准。
---
## 12.23 输出消费者边界

Pre-Roll 已在 canonical 编译阶段表现为提前后的正式事件 tick、实例生命周期与 Unit 占用。播放、MIDI 导出和音频渲染不得回读 Event Instrument Definition 再次应用偏移，也不得只在某个消费者中忽略、Clamp 或补偿它。

### 12.23.1 播放系统
播放系统可以在 canonical compiled result 基础上做：
```text
播放专用 buffer
调度状态机
实时过滤
Mute / Solo 过滤
播放起点调度
```
但不得改变编译语义。
### 12.23.2 MIDI 导出器
MIDI 导出器可以在 canonical compiled result 基础上做：
```text
按冻结 SMF Track Projection 组织 MIDI Track
写 Port meta event
Track 命名
文件命名
字节编码
导出格式整理
```
但不得改变编译语义，也不得回读 Project 重新推断 Pure MIDI Track 拓扑、Root 归属、Track EOT 或同 tick 顺序。

SMF delta-time 填充与文件编码限制属于导出器，见 §14.12.2、§14.12.8～9。Compiler 不为超长 delta 扫描相邻事件、不计算 MTrk 字节长度、不产生占位 Meta，也不因 MTrk 超过 `0xFFFFFFFF` 字节而拒绝编译；Full/Incremental 的音乐结果、诊断和 fingerprint 不受影响。MTrk 不因超限拆分，只让该次 MIDI 导出失败。TPQ、Tempo、事件值和安全 Tick 算术等既有语义检查不取消。
### 12.23.3 音频渲染器
音频渲染应使用与播放一致的 compiled result 语义。
播放与音频渲染的差异只应体现在：
```text
输出设备
离线 buffer
渲染范围
Track 选择
性能策略
```
不得体现在 Project 语义解释差异。
---
## 12.24 初版性能优化重点
初版性能优化重点应放在：
```text
编译过程本身
缓存策略
增量编译
播放主路径
渲染主路径
尤其是音频渲染
```
不应把初版优化重点放在小收益的跨 tick 连续相同状态事件折叠上。

在不改变正式结果、确定性和失败语义的前提下：
```text
时间性能优先于最小空间占用
允许通过预计算、紧凑索引、池化、缓存和预分配换取速度
所有额外内存必须有明确所有权、上限、失效键和释放时机
不得用无界缓存或依赖历史缓存的结果换取速度
```

## 12.25 极端规模 Canonical 分页契约

### 12.25.1 逻辑结果与物理布局分离

Canonical Compiled Result 继续表示一个确定的正式事件集，以及从该事件集冻结的 Execution Projection 与 SMF Track Projection。该契约不要求：

```text
all events in one contiguous managed array
one full SourceReference value copied into every event record
one duplicated event array per projection / Port / Unit / fragment
random access by materializing the complete result
```

Pure MIDI 极端内容必须使用延迟 canonical range source。成功编译冻结 source pack identity、overlay generation、Root allocation/lifecycle、aggregate count、SMF Track descriptor、音频 fragment descriptor和 preset summary；正式 consumer在请求范围时才生成有界 event pages。实现允许在 consumer page内保存紧凑来源身份，而不复制完整 Project 对象图。逻辑枚举结果、来源定位和同 tick顺序必须与逐事件 Canonical 参考模型完全相同。

Logical/Event Instrument 内容可以继续使用紧凑内存数组，但与 paged Pure MIDI 合并时必须通过确定性 merge形成一个逻辑总序，不能把 Pure MIDI 全量复制回连续数组。Execution、SMF与UI可以使用针对各自所需字段的不同紧凑 consumer record；它们必须来自同一正式 range source，不能重新解释语义。

### 12.25.2 Page 边界与有界内存

Pure MIDI consumer page 与有界排序必须满足：

```text
maximum 16,384 emitted event records per consumer page
maximum 131,072 compact fixed records per in-memory sort run
maximum 64 input runs in one merge pass
monotonic formal event order in emitted pages
```

排序 run超过内存边界时必须写入 session私有临时文件，并通过最多64路的多轮归并得到正式顺序；不得为一个极密时间窗创建无限数量的同时打开 reader buffer。临时文件是运行时工作区，不是 canonical持久化，不得进入 `.midora`。取消、截断、extent溢出或临时I/O失败必须终止当前 consumer枚举并清理工作区。

Immutable source pages共用第16.30节的每Project `64 MiB decoded` LRU。初版不建立第二份全Project canonical decoded LRU；consumer page、sort run buffer、最多64个reader batch和输出页在枚举结束后释放。cache miss或sort spill只能改变耗时，不能改变fingerprint、诊断或事件顺序。

### 12.25.3 流式编译与 fingerprint

Pure MIDI source pages提供按Segment/kind/tick bounds查询的value cursor；source物理顺序不作为canonical顺序。编译器使用有界cursor、固定sort run与多轮外部merge完成crop、Root merge、range restore、lifecycle boundary、Execution/SMF projection和consumer freeze；不得创建与总Note/Event数等长的pending list、paired-order set、第二份排序数组或per-event builder lookup。

用于播放的 `.mpk` 派生索引必须分别保存按正式端点顺序局部有序的 NoteOn、NoteOff 与 Channel Event pages；每个 endpoint page 最多 16,384 records。Track 内查询通过页目录裁剪并对相交的局部有序 page runs 做有界 k-way merge，跨 Track/Root 再按 canonical key 做有界 merge；不得为每个 250 ms 播放窗口重新扫描全部“可能与窗口相交”的原始 Note pages。该索引只改变物理查询路径，不改变 source record、same-tick order、FIFO NoteOff 或 canonical fingerprint。

中途起播的状态恢复必须使用有界 Channel-state checkpoint suffix 与按端点索引查询的 active-note集合。NoteOn endpoint目录必须携带页内最大Note end tick；active-note查询只解码满足`minimumStart < cursor < maximumEnd`的候选endpoint页，并在局部有序页内二分start边界，不得读取普通Note页。Checkpoint 间隔不得超过 16,384 个 Channel Event；恢复成本可与候选endpoint页、实际仍活动 Note 数及最后一个 checkpoint 后的事件数相关，但不得与 Segment 起点到光标之间的全部历史 Note/Event 数线性相关。

Canonical/source-aware fingerprint必须增量写入hash state，禁止先把完整事件流写入`MemoryStream`或byte array。它必须包含编译范围、Tempo map、Root/Track/Segment身份与顺序、source pack content fingerprint、content window、overlay generation/tombstone及正式reset defaults。Source page checksum只能用于已经通过pack验证的immutable source；排序run/page边界不得进入音乐语义或使相同内容产生不同结果。

### 12.25.4 Snapshot 与增量编译

Project compilation snapshot 对 immutable Pure MIDI base pages只复制 page-root descriptor/ref-count，不复制记录。编辑采用 copy-on-write overlay；snapshot 冻结当时 base generation、tombstone 与 overlay generation。改变一个对象不得深拷贝所属 Track 的其余 pages。

Checkpoint、dirty range 与 state-hash 收敛继续服从 §12.21。Page 是物理重算/复用单元，不是新的音乐边界；跨 page 的 Note FIFO、Channel state、Root lifecycle 与同 tick order 必须连续。

### 12.25.5 Consumer range API

Canonical 必须提供只读范围 cursor，至少能按以下条件组合查询：

```text
[startTick, endTick)
Execution Unit / Root
SMF ExportTrackId
source Track filter used by runtime Mute/Solo
```

范围 cursor 在起点前读取由 checkpoint 指明的最小状态上下文，并在 endTick 停止。播放、MIDI 导出和音频渲染不得以调用全结果 `ToArray()` 作为准备步骤；需要整曲顺序输出的消费者应逐页顺序枚举。
---
