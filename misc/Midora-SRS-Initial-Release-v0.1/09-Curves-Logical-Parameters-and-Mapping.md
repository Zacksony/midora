# 第 9 章 曲线、Logical Parameter 与映射

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Mapping Chain、Mapping Step、图形映射、Mapping Function Expression、MappingContext、Logical Parameter Definition/Lane/Mapping、值域处理和断裂引用行为。

## 9.1 映射系统总体规则
### 9.1.1 映射挂载位置
映射规则挂在具体事件参数上。
SubVoice 事件 Mapping 挂载到共享的精确标量目标，由以下内容定位：
```text
subVoiceId
eventKind
必要的 eventNumber（例如 CC / RPN / NRPN number）
parameterKey
```
不使用：
```text
tick + 事件类型 + 参数名
单个事件点 ID
数组索引
显示名称
UI 文本
```

同一 SubVoice 内相同精确标量目标的全部事件点共享一个 Mapping Chain；事件点稳定 ID仍用于 canonical 来源追踪和诊断。Logical Parameter Mapping 由其自身稳定 ID、源 Parameter、目标 SubVoice/精确标量目标及显式顺序定位。
### 9.1.2 parameterKey
`parameterKey` 使用强类型枚举 / 路径结构。
示例：
```text
Note.Number
Note.Velocity
CC.Value
PitchBend.Value
Program.Value
Bank.MSB
Bank.LSB
Rpn.Data
Nrpn.Data
PitchBendRange.Semitone
PitchBendRange.Cents
```
不使用字符串路径或 UI 显示文本作为持久身份。
### 9.1.3 每个参数最多一条映射链
每个事件参数最多一条 Mapping Chain。
规则：
```text
映射链内可包含多个 Mapping Step
映射链由用户手动排序
从上到下执行
```
不支持：
```text
同一参数多条映射链并列执行
同一参数只有一个 Mapping Step 的硬限制
```
### 9.1.4 空链与禁用
映射链存在但没有 Mapping Step：
```text
等同于无映射
使用原始值
```
允许删除的可选 Mapping owner 不存在时也等同于无映射并使用原始值。
映射链可整体启用 / 禁用。
禁用映射链：
```text
不参与编译
不触发 Per-Note Instance Isolation 不兼容失败
不运行其中 Mapping Step
保留配置
```
Mapping Step 也可以禁用。
禁用 Mapping Step：
```text
不参与运行
不导致编译失败
即使引用的 Mapping Function 已删除或编译错误，也不影响编译
```
### 9.1.5 映射链复制
初版支持在事件参数之间复制 / 粘贴映射链。
复制规则：
```text
允许复制到不兼容目标
目标参数按自身值域、整数性和语义合法性校验
不合法时编译失败
复制出的映射链获得新 ID
复制出的 Mapping Step 获得新 ID
引用的 Mapping Function 保持引用同一函数 ID
```
初版不做映射链命名预设。
初版支持：
```text
复制 / 粘贴映射链
复制单个 Mapping Step 可作为 UI 增强，但非本章硬性要求
```
### 9.1.6 删除映射链
删除 Mapping Chain 不删除 Mapping Function。
规则：
```text
Mapping Function 是 Event Instrument 级资源
Mapping Chain 只是引用它
删除链时仅解除引用
```

Note Number / Velocity 的共享 Mapping owner 是强制目标，不允许删除；可删除其中的 Step 或禁用 Chain。非 Note `SubVoiceEventMapping` 与 `LogicalParameterMapping` 是可选 owner，删除整条 Chain 时物理移除 owner；非空 Chain 删除必须由用户明确确认，Undo 恢复同一 owner、Chain、Step、引用和顺序。

删除单个事件点时：
```text
只删除该事件点
不自动删除同一精确目标的共享 Mapping
```

显式创建一种新的非 Note 事件目标时允许同时创建一次空的可选 Mapping owner；后续事件点编辑、删除/重新插入、编译或打开修复不得静默重建用户已经显式删除的可选 owner。owner 不存在时事件原始值直通。
---
## 9.2 映射目标范围
### 9.2.1 只允许值类参数映射
初版只允许映射值类参数。
不允许映射事件目标身份参数。
不允许映射：
```text
CC number
RPN 参数号
NRPN 参数号
事件类型
SubVoice 归属
Mapping Function 引用目标
曲线目标身份
```
理由：
```text
映射目标身份参数会改变事件目标本身
会显著增加冲突判定、排序、Reset 使用范围和诊断复杂度
初版不开放
```
### 9.2.2 允许映射的值类参数
初版允许映射：
```text
Note number
Note velocity
CC value
Pitch Bend value
Program value
Bank MSB / LSB
RPN / NRPN Data Entry
Pitch Bend Range semitone / cents
```
当 Event Instrument/SubVoice Mapping 的目标事件是 CC 时，CC number 仍由事件身份固定；CC91 与 CC93 不是该路径的合法事件或映射目标。发现对 CC91 / CC93 的 Mapping Chain 或 Logical Parameter Mapping 时，语义验证必须产生 Error。该限制不适用于第 23 章 Pure MIDI Track 中不经过 Mapping 的直接 CC 事件。
### 9.2.3 Program / Bank 映射边界
Program value 允许映射。
规则：
```text
结果必须为整数 0–127
默认越界策略 fail
```
Bank MSB / LSB 映射只作用于已存在值。
规则：
```text
不能通过映射生成空值
不能通过映射取消已有值
不能通过映射从空值生成数值
```
---
## 9.3 Mapping Step 与组合模式
### 9.3.1 映射链执行语义
映射链从事件参数原始值开始。
每个 Mapping Step 作用于当前累计值。
规则：
```text
currentValue = originalParameterValue
Step 1 作用于 currentValue
Step 2 作用于 Step 1 后的 currentValue
依次执行
```
不采用：
```text
每一步都只作用于原始值
所有 Step 先算结果再统一求和
最后取最后一步结果
```

对于 CC、Pitch Bend、Program、Bank、RPN/NRPN、Pitch Bend Range 等状态型非 Note 目标，如果 Mapping Chain 使用 Envelope 或其他连续时间源，`originalParameterValue` 不是“当前 tick 必须重新出现的事件点”，而是当前实例内最近一次原始目标值的持有状态；首个原始事件之前使用合并后的 Initial State，仍无覆盖时使用该目标类型默认值。连续 Mapping 不改变原始状态本身，只从该持有值计算当前输出。
### 9.3.2 中间值类型
映射链中间累计值可以是 double。
规则：
```text
整数参数不要求每一步后立即取整
最终输出到整数参数时再按该参数取整策略处理
```
避免多步映射中的重复量化误差。
### 9.3.3 组合模式
初版完整支持以下组合模式：
```text
Override
Add
Multiply
Remap Range
Clamp
Ignore
```
默认组合模式：
```text
Add
```
### 9.3.4 Ignore
`Ignore` 语义：
```text
该 Mapping Step 暂时禁用
不改变当前累计值
保留配置
```
Ignore 用于调试和临时旁路。
### 9.3.5 Clamp
`Clamp` 作为组合模式时：
```text
该 Step 提供 min / max
把当前累计值限制到该范围内
```
该 Clamp Step 与目标参数最终合法性校验不同。
```text
Clamp Step 是用户可控中间限制
目标参数合法性校验是最终兜底
```
### 9.3.6 Remap Range
`Remap Range` 语义：
```text
将当前累计值从输入范围线性映射到输出范围
可配置超范围策略
```
不只作用于原始值，也不只用于图形映射。
### 9.3.7 Multiply
`Multiply` 可用于整数参数。
中间结果允许为小数。
最终输出到整数参数时再按该参数取整策略处理。
---
## 9.4 映射结果取整、越界与合法性
### 9.4.1 整数参数取整
当映射链最终结果输出到整数参数时，取整策略可配置：
```text
Round
Floor
Ceil
```
默认：
```text
Round
```
`Round` 的 midpoint 规则固定为 Away From Zero：
```text
 62.5 ->  63
-62.5 -> -63
```
`Floor` 与 `Ceil` 使用标准数学定义。

取整策略属于整数目标参数配置，不属于单个 Mapping Step。事件参数 Mapping、连续曲线和 Logical Parameter Mapping 都必须在各自目标参数上保存该配置；复制或重排 Mapping Step 不得改变目标参数的取整策略。同一 SubVoice 内多个 Logical Parameter Mapping 作用于同一 MIDI 目标时，必须共享同一取整与最终越界配置。
适用目标包括但不限于：
```text
Note number
Note velocity
CC value
Pitch Bend value
Program value
Bank MSB / LSB
RPN / NRPN Data Entry
Pitch Bend Range semitone / cents
```
虽然 Pitch Bend 内部采用 `-8192..+8191`，它仍是整数目标。
### 9.4.2 越界策略
越界策略挂在事件参数映射目标上，而不是 Project 全局或每个 Mapping Step。
每个目标参数可配置：
```text
Clamp
Fail
```
默认：
```text
Fail
```
说明：
```text
Note number 越界本规格规定必须编译失败
其他参数可根据目标配置选择 clamp 或 fail
```
### 9.4.3 最终合法性校验
所有映射链最终结果必须通过目标参数的：
```text
值域校验
整数性校验
语义合法性校验
```
校验失败时：
```text
当前编译 / 播放 / 渲染 / 导出流程失败
```
除非该目标参数明确配置为 clamp 并可合法 clamp。
### 9.4.4 NaN / Infinity
如果 Mapping Function Expression 返回：
```text
NaN
Infinity
-Infinity
```
视为运行失败。
如果图形映射内部计算产生 NaN / Infinity：
```text
视为映射失败
```
处理结果：
```text
当前编译 / 播放 / 渲染 / 导出流程失败
```
不得自动转 0，不得静默 clamp，不得忽略 Step。
---
## 9.5 图形映射
### 9.5.1 图形映射是 Mapping Step
图形映射和 Mapping Function Expression 都是 Mapping Step。
规则：
```text
二者可在同一映射链中任意排序
二者由用户手动排序
从上到下执行
```
不固定“图形先执行”或“Expression 先执行”。
### 9.5.2 输入源
图形映射 X 轴输入源可选择 MappingContext 中的内置输入源。
允许输入源包括：
```text
当前原始值 / 当前值
triggerNote
triggerVelocity
gateLength
pitchDelta
templateTick
projectTick
templateNote
templateVelocity
其他后续确认的 MappingContext 字段
```
但输入源必须在当前事件语义下可用。
例如：
```text
非 Note 事件不得选择 templateNote
非 Note 事件不得选择 templateVelocity
```
### 9.5.3 输入源保存方式
图形 Mapping Step 的输入源保存为枚举 / 内置输入源 key。
不保存为：
```text
UI 显示文本
自由字符串
自由表达式文本
```
### 9.5.4 不允许引用其他事件参数
初版图形映射不允许选择“另一个事件参数”作为输入源。
例如不允许：
```text
用同一 tick 的 Pitch Bend 值映射 CC value
用另一个 CC 的当前值映射本 CC
```
理由：
```text
跨事件参数依赖会引入执行顺序、循环依赖和缓存复杂度
```
因此初版不存在跨参数映射循环引用问题。
### 9.5.5 图形映射输出
图形映射输出一个中间数值。
该中间数值再由当前 Mapping Step 的组合模式决定如何作用于当前累计值。
不采用：
```text
图形映射永远输出最终值
图形映射只输出 0–1
图形映射由目标类型自动决定语义
```
### 9.5.6 输入 / 输出范围与超范围策略
每个图形 Mapping Step 可配置：
```text
输入范围
输出范围
```
当输入超出范围时，可配置：
```text
Clamp
Extrapolate
Fail
```
默认：
```text
Clamp
```
图形 Step 输入源是枚举 key，因此系统可直接判断该 Step 是否依赖每音符上下文。
---
## 9.6 Mapping Function Expression
### 9.6.1 表达式模型
Mapping Function 当前固定使用受限表达式 ABI v3。其概念签名仍为：
```csharp
double Transform(double value, in MappingContextV2 context)
```
Project 保存的是一条返回 `double` 的单行表达式，不是方法体、语句块、完整 compilation unit、程序集或编译产物。`value` 是当前累计链值，`context` 是只读 Mapping Context。

每个 Mapping Function 必须保存：
```text
abiVersion = 3
单行表达式源码
由正式分析器推导并冻结的 Context 依赖集合
```

ABI v1/v2 的自由 C# 方法体属于已被安全原因破坏性取代的开发期格式。系统可以识别旧 ABI，但绝不执行；实际参与编译时必须明确失败，用户重新编辑并 Apply 后才升级为 ABI v3。不得保留隐藏的旧执行器或自动把旧方法体当作表达式执行。

### 9.6.2 允许语言与安全边界
ABI v3 只允许：
```text
数值字面量、true、false
value
批准的 context 数值字段与枚举字段
+ - * / %、比较、==、!=、&&、||、!
条件表达式 condition ? whenTrue : whenFalse
批准的 MappingEventKindV2 / MappingTargetParameterV2 枚举成员
批准的 System.Math 纯数值方法与 E / PI / Tau
```

批准的 `System.Math` 成员固定隐式导入：`Sin(x)` 与 `Math.Sin(x)`、`PI` 与 `Math.PI` 语义完全相同，`Math.` 前缀可选。隐式形式仍只能绑定到同一版本化方法/常量白名单；它不是 C# `using static`，不得解析用户定义符号、其他类型成员或任意静态方法。

禁止范围至少包括：
```text
任何语句、声明、循环、赋值、递增/递减
lambda、delegate、对象/数组创建、类型构造、dynamic
任意方法、属性、索引器或反射访问
文件、网络、进程、线程、任务、时间、随机数、环境变量
字符串、名称、稳定 ID、Project 对象图和可变全局状态
unsafe、指针、P/Invoke、异常构造与显式 throw
```

解析器必须先以 Expression 模式解析，随后按精确语法节点和符号白名单自行绑定为 `System.Linq.Expressions`。不得 Emit 或加载由 Project 源码生成的程序集，不得把运行机器的引用程序集或 API 面交给用户源码。

固定资源上限为：源码最多 8,192 Unicode scalars、语法节点最多 512、语法树深度最多 64。表达式编译和求值不得提供用户可构造的无界循环或递归路径。白名单必须由版本化 ABI 集中定义，UI 补全、Draft 验证与正式编译共用同一来源。
### 9.6.3 Mapping Function 稳定 ID 与名称
Mapping Function 需要稳定 ID。
引用基于稳定 ID。
名称仅用于显示。
名称规则：
```text
名称必填
同一 Event Instrument 内不可重复
唯一性比较大小写不敏感
唯一性比较去除首尾空白
```
重命名 Mapping Function：
```text
不破坏引用
不改变稳定 ID
进入全项目撤销 / 重做
使 Project 进入已修改状态
```
### 9.6.4 Mapping Function Context 依赖
正式表达式分析器必须精确推导实际引用的批准 `context` 字段。用户不手工编辑依赖集合；UI Apply 将推导结果与表达式作为一个 Project 编辑提交。

持久化依赖集合是确定性 fingerprint 与快速兼容性检查所需的派生源字段。正式编译必须重新推导并要求两者精确相等；不一致时失败，不能信任被篡改或陈旧的声明。Per-Note Instance Isolation 检查使用该已验证集合。
### 9.6.5 编译缓存边界
Mapping Function Expression 缓存键固定包含：
```text
ABI version
固定 compiler profile
单行表达式精确 UTF-8 SHA-256
```

推导的 Context 字段参与 Project/source fingerprint 和兼容性检查，但不改变表达式代码缓存键。

缓存属于当前打开 Project 的运行时编译会话，只保留当前 Project 中仍存在的不同源码修订；编辑、删除、切换或关闭 Project、显式清缓存后必须移除旧委托，不得随编辑历史无界累积。缓存和编译产物不得持久化。

未来改变语法、函数签名、Context 类型、白名单或编译 profile 时必须增加 ABI version。安全收缩可以明确拒绝旧 ABI，不要求保留会执行旧不安全源码的兼容分支。
### 9.6.6 Mapping Function 复制
初版支持在同一个 Event Instrument 内复制 Mapping Function。
复制规则：
```text
副本获得新 ID
副本获得唯一名称
副本与原函数后续独立编辑
复制不自动改绑现有映射链引用
现有映射链仍引用原函数
```
### 9.6.7 删除被引用 Mapping Function
删除被引用 Mapping Function 前必须确认。
确认时应显示受影响事件参数列表。
删除后：
```text
引用变为断裂状态
保留断裂引用定位信息
断裂引用参与保存
断裂引用参与编译时失败
```
打开项目时发现 Mapping Function 引用断裂：
```text
Project 允许打开
打开时给警告
断裂引用参与编译时失败
不自动删除断裂引用
不静默忽略
```
### 9.6.8 Mapping Function 诊断
规则：
```text
未被任何映射链引用的 Mapping Function 不产生诊断
未被引用但无法通过 ABI v3 验证或绑定的 Mapping Function 不阻止整曲编译，但在诊断中显示为警告
被实际参与编译的 Mapping Function 编译错误导致编译失败
被禁用 Mapping Step 引用的 Mapping Function 编译错误不影响编译
Mapping Function 运行时异常导致当前编译 / 播放 / 渲染 / 导出流程失败
Mapping Function 返回 NaN / Infinity 视为运行失败
```
---
## 9.7 MappingContext
### 9.7.1 初版字段范围
初版 Mapping Function Expression ABI v3 精确允许访问的数值字段为：
```text
CurrentValue
TriggerNote
TriggerVelocity
GateLength
PitchDelta
TemplateTick
ProjectTick
TemplateNote
TemplateVelocity
EffectiveRootNote
LogicalParameterValue
TargetOriginalValue
SegmentLocalTick
SubVoiceIndex
SubVoiceEffectiveRootNote
EventInstrumentRootNote
```

精确允许的枚举字段为 `CurrentParameter` 与 `CurrentEventKind`。ID、名称、对象引用与其他 MappingContext 成员不在 ABI v3 表达式可访问面中；扩展上述列表必须增加 ABI version。
### 9.7.2 字段命名
逻辑轨触发音 velocity 命名为：
```text
triggerVelocity
```
避免与当前事件参数 `value` 或模板 Note velocity 混淆。
### 9.7.3 pitch 字段
Pitch 相关字段包括：
```text
triggerNote
effectiveRootNote
pitchDelta
templateNote
```
其中：
```text
templateNote = 当前 Note 对象的原始 note number
```
### 9.7.4 templateVelocity
MappingContext 包含 `templateVelocity`。
规则：
```text
templateVelocity 仅当前映射目标属于 Note 对象时提供
非 Note 事件不可访问 templateVelocity
选择该输入源应被编辑器阻止
```
示例：
```text
映射 Note number 时，value 是 note number，但函数可能需要知道模板 velocity
映射 Note velocity 时，value 是 velocity，但函数可能需要知道模板 note number
```
### 9.7.5 当前事件信息
ABI v3 只通过 `CurrentEventKind` 与 `CurrentParameter` 两个只读枚举表达当前事件类型与目标参数类别。事件类型示例：
```text
Note
ControlChange
PitchBend
ProgramChange
BankSelect
RPN
NRPN
PitchBendRange
```
目的：
```text
便于诊断
便于同一个 Mapping Function 复用到多个目标参数
不暴露可写对象
```
### 9.7.6 projectTick 边界
`projectTick` 可作为只读上下文输入。
但 第 9 章《曲线、Logical Parameter 与映射》 不定义它如何由以下因素计算：
```text
生命周期策略
循环 / Envelope
Segment 裁剪
Project End Marker
播放起点
预渲染上下文
```
这些由 第 10 章《实例生命周期、Loop、Envelope 与重叠》、第 11 章《Logical Track、Segment 与编曲语义》、第 12 章《编译系统与 Canonical Compiled Result》、第 13 章《播放与预览》 继续细化。

Event Instrument Definition 的 `Pre-Roll Ticks` 大于 0 时，`projectTick` 和 `segmentLocalTick` 必须表示当前模板/派生事件提前后的实际时间位置，而不是 Logical Note 的可见 anchor tick。设 anchor 为 `A`、Pre-Roll 为 `O`，则 template tick `t` 对应 `projectTick = A - O + t`；`templateTick` 仍保持 `t`，不得因 Pre-Roll 重写模板局部坐标。
### 9.7.7 gateLength 与 held Preview

普通编译和固定长度预览中的 `gateLength` 必须是已知、合法的最终 Gate Length。

对于使用 `Pre-Roll Ticks` 的 Logical Segment 实例，`gateLength` 仍是从 Logical Gate Start 到有效 Gate End 的逻辑长度；它不得包含 Pre-Roll，也不得改为实例 origin 到 Gate End 的局部时间跨度。换言之，未被 Segment End 裁剪时：

```text
MappingContext.gateLength = Logical Note.length
instance-local Gate horizon = Pre-Roll Ticks + MappingContext.gateLength
```

发生 Segment End 裁剪时，`gateLength` 继续按既有有效 Logical Gate 长度缩短，instance-local Gate horizon 相应为 `Pre-Roll Ticks + effective gateLength`。

`Cut Previous` 的截断点使用新实例的提前后 origin。若该点早于旧实例的 Logical Gate Start，旧实例的截断专用 MappingContext 必须使用 `gateLength = 0`；这是 Overlap Policy 对既有实例的确定性截断哨兵，不表示 Project 中允许零长度 Logical Note，也不得扩展到普通编译输入。

只有第 13.22.7、13.24.5 节定义的 held Preview 因果 Gate 子上下文允许在 Gate End 尚未发生时使用：

```text
gateLength = Int64.MaxValue
```

该值是“Gate 尚未结束”的固定哨兵，不表示一个可持久化、可导出或可作为普通 Logical Note 长度使用的超长 Gate。Mapping Function 必须能够读取该值；编译器、预览消费者和 UI 不得把它夹取、换算或猜测为 `previewGateLength`。Gate End 后，只有尚未渲染的边界及后续 Release / Tail 使用冻结的实际 Gate Length；已经消费或已经缓冲的结果不回写，也不承诺与事后使用最终 Gate Length 执行一次固定长度编译完全等价。Segment/消费者硬边界的最终 Reset 仍使用已冻结的正式范围。

该哨兵只属于临时 Preview CompileContext，不进入 Project、`.midora`、MIDI 导出、音频文件渲染或普通 canonical compiled result。

### 9.7.8 Context 不暴露对象图
初版 MappingContext 不提供：
```text
完整 Project 对象图
完整 Event Instrument 对象
完整 SubVoice 对象
同一 SubVoice 的全部事件列表
其他事件参数读取 API
可写 API
```
---
### 9.7.9 Logical Parameter Mapping Context
Logical Parameter Mapping 的 Mapping Function Expression 仍只能使用 ABI v3 白名单。该场景的主要专用数值字段为：
```text
TargetOriginalValue
LogicalParameterValue
ProjectTick
SegmentLocalTick
TemplateTick
TriggerNote
TriggerVelocity
CurrentEventKind
CurrentParameter
```
含义：
```text
TargetOriginalValue = SubVoice 原始目标事件值的当前有效值
LogicalParameterValue = Logical Parameter 的当前有效值
```
Logical Parameter Mapping 计算应使用有效 `LogicalParameterValue` 与 `TargetOriginalValue`，而不是只使用用户点所在 tick 的瞬时值；表达式的 `value` / `context.CurrentValue` 另表示当前累计链值。
## 9.8 Logical Parameter 定义、Lane 与 Mapping
### 9.8.1 Logical Parameter 的系统级定义
Logical Parameter 是 Event Instrument 暴露给 Logical Track / Segment 的外部参数接口。
规则：
```text
Logical Parameter 定义属于 Event Instrument。
Logical Parameters 直接由 Event Instrument 持有，不额外设置 Group 层级。
Logical Track / Segment 只能编辑当前绑定 Event Instrument 暴露的 Logical Parameters。
Logical Parameter 不等同于裸 MIDI CC / Pitch Bend / RPN / NRPN。
Logical Parameter 通过 Logical Parameter Mapping 间接影响一个或多个 SubVoice 的非 Note MIDI / 高级事件参数。
```
### 9.8.2 Logical Parameter Definition
每个 Logical Parameter Definition 至少包含：
```text
稳定 ID
用户可见名称
input type
defaultValue
legal range
display range
Enum item 定义（仅 Enum 类型）
```
名称规则：
```text
名称不能为空
名称在单个 Event Instrument 内唯一
唯一性比较大小写不敏感
唯一性比较去除首尾空白
```
支持输入类型：
```text
Integer
Double
Enum
```
`defaultValue` 必须与 input type 和 legal range 兼容。
### 9.8.3 Logical Parameter Lane / Point
Logical Parameter Lane 是 Segment 内某个 Logical Parameter 的时间变化数据。
规则：
```text
Logical Parameter Lane 属于 Segment。
Lane 引用当前 Track 绑定 Event Instrument 中的 Logical Parameter 稳定 ID。
Logical Parameter Point 表示某 tick 上的参数值。
Point 必须拥有稳定 ID。
Lane 可为空，空 Lane 不产生输出效果。
```

Logical Parameter Lane 固定为离散突变点集。每个 Point 的值从该 tick 起保持，直到同 Lane 的下一个 Point；UI 不提供 Linear、Step 或其他自动插值选项。若当前持久化模型仍以通用 Curve/Point 容器承载该数据，则其中所有 Logical Parameter Point 的 interpolation 字段必须固定为 `Step`，语义验证必须拒绝其他值。该容器复用不得把 Logical Parameter Lane 解释为连续曲线，也不得影响 Envelope、SubVoice Value Curve 等其他正式曲线的插值能力。
Logical Parameter Lane 不是裸 MIDI Lane。
不采用：
```text
Segment 直接编辑 MIDI CC Lane
Segment 直接编辑 Pitch Bend Lane
Segment 直接编辑 RPN / NRPN Lane
Segment 直接编辑 Program / Bank Lane
```
### 9.8.4 稀疏状态继承
Logical Parameter Lane 是状态型时间数据。
解析规则：
```text
没有新值时继承上一个有效值。
没有上一个有效值时使用 Logical Parameter defaultValue。
```
因此，一个 Logical Parameter 不需要在每个 tick 都显式写点。
### 9.8.5 SubVoice 原始目标事件值 c 的状态继承
Logical Parameter Mapping 不只读取用户点所在 tick 的瞬时值。
SubVoice 原始目标事件值也按 MIDI 状态继承解析。
定义：
```text
c = SubVoice 原始目标事件值的当前有效值
x = Logical Parameter 的当前有效值
y = Logical Parameter Mapping 输出值
```
当目标参数在当前 tick 没有原始事件值时，`c` 使用该目标此前最近的有效状态；如果没有此前状态，则使用该目标类型的初始状态 / 默认状态，具体来源由 Initial State、Reset、编译上下文和相关专项章节共同细化。

上述原始目标状态继承同样适用于直接挂在共享非 Note Event Mapping 上的 Envelope/连续 Step。也就是说，仅在 tick 0 写入 CC11=127 后，后续 Envelope 系数 0.5 的基值仍是 127，而不是 0、默认重取值或“无事件”。
### 9.8.6 合成示例
示例：
```text
SubVoice 原始 Pitch Bend: [10 - - 20 - - 30 -]
Logical Parameter:        [- - 5 - - 8 9 -]
原始有效值: [10 10 10 20 20 20 30 30]
参数有效值: [0 0 5 5 5 8 9 9]
若 y = c + x，则输出:
[10 10 15 25 25 28 39 39]
```
该示例中，Logical Parameter 的 defaultValue 为 `0`。
### 9.8.7 Logical Parameter Mapping 目标范围
Logical Parameter Mapping 可以映射到一个或多个 SubVoice 的非 Note MIDI / 高级事件参数。
可作为目标的参数包括但不限于：
```text
CC value
Pitch Bend value
Program value
Bank MSB / LSB
RPN Data Entry
NRPN Data Entry
Pitch Bend Range semitone / cents
其他后续定义的非 Note 高级事件参数
```
其中 `CC value` 不包括 CC91 / CC93 的 value；初版不允许通过 Logical Parameter Mapping 间接生成 Reverb / Chorus Send。
初版不把 Logical Parameter Mapping 作为 Note number / Note velocity 的常规目标机制。Note 相关映射继续由 Note 对象自身映射链处理。
### 9.8.8 多 Mapping 作用同一目标的顺序
多个 Logical Parameter 可以映射到同一目标。
规则：
```text
同一目标上的多个 Logical Parameter Mapping 必须有显式顺序。
编译时按显式 Mapping 顺序依次计算。
后一个 Mapping 接收前一个 Mapping 的输出作为新的 c。
顺序必须稳定保存。
复制、撤销 / 重做、打开项目后顺序必须保持。
```
不采用：
```text
按参数名称排序
按对象 ID 排序
按 UI 临时显示顺序隐式决定
多个 Mapping 结果无序合并
```
### 9.8.9 除零、NaN / Infinity 与越界
Logical Parameter Mapping 中的除零、NaN、Infinity 和最终越界必须明确处理。
系统级规则：
```text
Mapping Function Expression 返回 NaN / Infinity 视为运行失败。
图形映射或内置映射产生 NaN / Infinity 视为编译失败。
最终输出值超出目标参数合法范围时，按 第 9 章《曲线、Logical Parameter 与映射》 既有最终合法性校验处理。
系统不得自动静默 clamp，除非用户显式设置 Clamp Step。
```
### 9.8.10 重绑定与断裂 Lane
Logical Track 改绑 Event Instrument 后：
```text
旧 Logical Parameter Lane 数据保留。
旧 Lane 可能变为断裂 / 不适用。
不自动按名称匹配新 Event Instrument 参数。
不自动删除旧 Lane。
不自动把旧 Lane 转换成裸 MIDI 事件。
```
如果用户后续需要迁移参数 Lane，应由显式转换 / 修复操作完成。具体 UI 与转换规则由 第 11 章《Logical Track、Segment 与编曲语义》 / 第 17～20 章的 UI 与交互规格 或实现设计阶段细化。

Logical Parameter Definition 自身发生类型、Enum 显式模式、Enum 数值/顺序/删除或会使既有数据失效的合法范围变化时，必须由调用方提交显式迁移计划，并与全 Project 所有引用该 Parameter ID 的 Lane 形成单个原子撤销 / 重做项。迁移策略必须显式选择 `Clamp` 或 `DiscardInvalidValues`；目标为 Enum 时还必须确认数值兼容不保证 Enum 语义等价。Double 转 Integer/Enum 使用 Away From Zero 中点规则，Enum Clamp 选择最近已定义值且等距取较小值，目标 Enum 的保留点统一转为 Step。点与保留 Enum item 的稳定 ID 不变；新增 Enum item 首次提交时分配新 ID。不得静默迁移、按名称重绑定或留下 Definition 已变而 Lane 尚未处理的中间状态。
### 9.8.11 保存、复制与撤销 / 重做
Logical Parameter Definition、Logical Parameter Lane、Point、Mapping 均属于项目内容。
规则：
```text
修改 Logical Parameter Definition 进入全项目撤销 / 重做。
修改 Logical Parameter Mapping 进入全项目撤销 / 重做。
编辑 Segment 中的 Logical Parameter Lane / Point 进入全项目撤销 / 重做。
复制 Event Instrument 时，Logical Parameter Definition 与 Mapping 深拷贝并生成新 ID。
复制 Segment 时，Logical Parameter Lane / Point 深拷贝并生成新 ID。
```
### 9.8.12 与相关专项章节的边界
本规格规定 Logical Parameter 的编辑、映射与状态继承语义。
以下内容由实现设计确定：
```text
第 11 章《Logical Track、Segment 与编曲语义》：Segment 中 Logical Parameter Lane 的裁剪、分割、连接、重绑定 UI 行为
第 12 章《编译系统与 Canonical Compiled Result》：Logical Parameter Mapping 的编译执行、离散化、缓存和同 tick 输出排序
第 13 章《播放与预览》：播放 / 预览中的 Logical Parameter 状态恢复
第 14 章《MIDI 导出》：MIDI 导出中的最终事件展开
第 16 章《.midora 文件格式与持久化》：.midora 文件字段与持久化结构
第 15 章《音频文件渲染》：诊断等级、错误码和定位信息
第 17～20 章的 UI 与交互规格：UI 编辑器与修复工具
```
## 9.9 Per-Note Instance Isolation 关闭时的映射兼容性
### 9.9.1 基本原则
Per-Note Instance Isolation 关闭时，只要映射依赖单个逻辑触发 Note 的上下文，就与实例隔离关闭不兼容。
不兼容输入包括：
```text
triggerNote
triggerVelocity
gateLength
pitchDelta
其他依赖单个逻辑触发 Note 的上下文字段
```
理由：
```text
实例隔离关闭时，多个逻辑音符可能共享同一个 Event Instrument Instance
此时“当前 triggerNote”不再是稳定的一对一上下文
```
### 9.9.2 Note 映射
Per-Note Instance Isolation 关闭时：
```text
Note → Event 映射整体不兼容
映射到 Note number 不允许
映射到 Note velocity 且依赖 triggerVelocity / triggerNote 等逻辑触发上下文不允许
```
不采用：
```text
取第一个音符
取平均音高
取平均 velocity
仅无重叠时允许
```
### 9.9.3 不依赖逻辑触发上下文的映射
Per-Note Instance Isolation 关闭时，不依赖逻辑触发上下文的映射允许继续使用。
例如：
```text
对模板固定 CC value 做 Multiply 0.8
对固定 Program value 做合法范围内的 Override
对固定 Pitch Bend value 做 Clamp
```
这类映射只是模板参数变换，不需要每音符独立状态。
### 9.9.4 Mapping Function Expression 兼容性
如果 Mapping Function Expression 经正式分析所得 Context 依赖包含每音符字段，则：
```text
当 Per-Note Instance Isolation 关闭且该 Step 参与编译时，编译失败
```
禁用 Mapping Chain 或禁用 Mapping Step 不参与此检查。
### 9.9.5 图形映射兼容性
图形 Mapping Step 的输入源是内置枚举 key。
因此系统可以直接判断该 Step 是否依赖每音符上下文。
如果依赖每音符上下文，且 Per-Note Instance Isolation 关闭，并且该 Step 参与编译：
```text
编译失败
```
---
## 9.10 删除、复制、断裂引用与打开项目
### 9.10.1 删除事件
删除事件时：
```text
事件对象删除
同一精确目标的共享 Mapping 保留
其他事件点继续复用该 Mapping
```
只有用户显式删除可选 Mapping owner 时才解除其中的 Mapping Function/Envelope/Logical Parameter 引用；资源对象本身仍保留。删除最后一个事件点也不隐式删除共享 Mapping，因为空 Event Lane 是允许的正式源状态。
如果该事件未来被其他结构引用，例如诊断书签、映射引用或其他对象引用：
```text
删除前确认
显示受影响引用
删除后引用变为断裂状态并保留定位信息
```
### 9.10.2 删除曲线
删除曲线时：
```text
只删除曲线对象
保留同一目标参数下的用户离散点
```
### 9.10.3 删除被引用 Mapping Function
删除被引用 Mapping Function 后：
```text
断裂引用参与保存
保存时不自动删除断裂引用
保存不失败
打开项目时不自动恢复
```
打开项目时发现 Mapping Function 引用断裂：
```text
Project 允许打开
打开时给警告
断裂引用参与编译时失败
```
### 9.10.4 事件编辑错误允许保存
事件编辑错误允许保存。
包括但不限于：
```text
映射链断裂
Mapping Function Expression 验证错误
曲线目标不合法
被禁用配置中存在错误引用
未完成映射配置
```
保存未完成工程是允许的。
但实际参与编译、播放、预览、渲染或导出时，错误应按对应规则失败或进入诊断。
---
## 9.11 与编译、播放、预览、渲染、导出的关系
### 9.11.1 编译关系
第 9 章《曲线、Logical Parameter 与映射》 定义的事件、曲线和映射是编译输入。
编译系统必须读取：
```text
SubVoice 内事件对象
曲线对象
用户离散点
事件参数映射链
Mapping Function
MappingContext 所需字段
Initial State 与普通事件冲突规则
同 tick 语义排序规则
```
以下由 第 12 章《编译系统与 Canonical Compiled Result》 继续细化：
```text
事件展开算法
曲线离散化的逐整数 tick 参考语义与重复值抑制
映射链执行缓存
Mapping Function Expression 验证/绑定委托缓存
Channel Group 分配
Reset 插入
无输出事件优化
资源占用计算
```
### 9.11.2 播放 / 预览 / 渲染关系
播放、预览、音频渲染过程中，如果映射运行失败、表达式求值失败、产生 NaN / Infinity 或映射结果非法：
```text
当前流程失败
播放应停止
相关 buffer 应清空
错误信息应尽量定位到 Event Instrument、SubVoice、事件、参数、Mapping Function、tick 和上下文摘要
```
具体播放停止行为、buffer 清理和 UI 错误呈现由 第 13 章《播放与预览》 / 第 15 章《音频文件渲染》 / 第 17～20 章的 UI 与交互规格 细化。
### 9.11.3 MIDI 导出关系
MIDI 导出必须基于编译后的离散 MIDI 事件流。
第 9 章《曲线、Logical Parameter 与映射》 不定义：
```text
最终 MIDI Track 结构
Port meta event 编码
MIDI 字节级排序
RPN / NRPN / Pitch Bend Range 精确展开序列
Readme 格式
```
这些由第 14 章《MIDI 导出》规定。
---
## 9.12 诊断原则
### 9.12.1 允许保存但参与编译失败
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
### 9.12.2 警告
以下情况不阻止整曲编译，但应在诊断中显示为警告：
```text
未被引用但无法通过 ABI v3 验证或绑定的 Mapping Function
打开项目时发现 Mapping Function 引用断裂
```
具体是否在打开时弹窗、诊断面板合并显示、是否支持跳转，由 第 15 章《音频文件渲染》 / 第 17～20 章的 UI 与交互规格 细化。
### 9.12.3 不产生诊断
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

## 9.13 快捷 Logical Parameter Event Binding

### 9.13.1 原子创建

Event Instrument Editor 的 `Add Event Binding...` 必须一次冻结并提交：一个 Integer Logical Parameter、一个合法 non-Note MIDI target、当前一个或多个 SubVoice、Operation/range 以及 exact-target 冲突策略。每个目标 SubVoice 获得一个正式 `LogicalParameterMapping`；缺少对应 `SubVoiceEventMapping` 时只创建空 owner，不得创建 tick 0 或其他 Template Event/Curve Point。

`All SubVoices` 只等于按下 OK 时存在的 SubVoice ID 集；未来新增 SubVoice 不自动绑定。任一目标无效、CC91/CC93、重复目标 ID、范围非法或冲突未决均令整个命令零提交；一次 Undo/Redo 必须恢复全部对象身份和原顺序。

### 9.13.2 冲突与顺序

同一 `(SubVoice,target)` 已有 Logical Parameter Mapping 时，UI 必须列出其执行顺序，并要求明确选择：

- Append：新 Mapping 放在该 exact target 最后一项之后；
- Replace：替换该 exact target 的全部既有 Mapping，新项占据原首项位置，其他 target 相对顺序不变；
- Cancel：不执行。

同 target 的 target rounding/overflow 设置继续共享。快捷创建固定使用 `Round` 与最终 `Clamp`；普通 Mapping 的 Fail/Clamp能力不变。

### 9.13.3 运算

- Override：以 Logical Parameter absolute value 覆盖 accumulator `c`；Parameter default 使用目标正式 reset/default；
- Add：`c + offset`；Parameter default 为 0，默认 range 为目标对应双极范围；
- Multiply：先把 Integer source range 线性映射到用户 factor range，再执行 `c × factor`；`default=1` 指映射后的 factor 必须精确为 1。factor range 必须包含 1，Parameter default 通过逆映射取得且必须是 source range 内的精确整数；否则快捷创建整批拒绝。

运算必须复用本章正式 accumulator、held raw target state、Mapping order、rounding 与 overflow，不得由 UI 自己计算 MIDI 结果。若现有图形 Step 不能精确表示 Multiply remap，实现可由同一原子命令建立一个受当前受限 Mapping Expression ABI约束的共享表达式资源；不得开放任意代码或不同执行器。

Override 是绝对覆盖：目标已有非默认 Initial State 或 raw event 时，它可以改变输出；UI 必须明确显示该含义。这里的 `default=target default` 只表示相对正式 reset/default 的中性值，不得声称对任意已有 target state 都是 no-op。
