# 第 10 章 实例生命周期、Loop、Envelope 与重叠

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Template Length、Gate Length、Rendered Instance Length、短音/长音策略、Release、Tail、Loop、Envelope、Overlap、Segment End 和 Reset 的统一生命周期语义。

## 10.1 生命周期核心术语
### 10.1.1 Template Length
`Template Length` 是 Event Instrument 模板自身长度。
系统级规则：
```text
Template Length 属于 Event Instrument
Template Length 使用 tick 作为内部单位
Template Length 必须大于 0
Template Length 不允许小于最后一个事件所在 tick
添加 / 移动事件超过 Template Length 时，自动扩展 Template Length
Initial State Defaults 不影响 Template Length
Envelope Release 不限制 Template Length
Envelope 阶段时长不要求落在 Template Length 内
```
Template Length 仍然在启用 Loop 后有效。
它继续定义：
```text
模板总长度
Tail 区域上限
默认完整播放边界
Loop 区间合法性上限
End At Template Length 的结束边界
```

### 10.1.1.1 Logical anchor、Pre-Roll 与 Instance Origin

对于 Logical Segment 中的 Event Instrument Instance，Logical Note start 是逻辑 Gate anchor，而 Event Instrument Definition 的 `Pre-Roll Ticks` 把模板与实例 origin 提前：

```text
A = Logical Note absolute start / Logical Gate Start
O = Event Instrument Pre-Roll Ticks
I = Event Instrument Instance / template origin = A - O
template tick t 的 absolute tick = I + t
```

`I` 是 Initial State、模板 tick 0 输出、Overlap 与资源占用的实例开始位置；`A` 仍是 Gate Start。Pre-Roll 不是负 Gate、额外 Gate Length 或播放延迟。只有 Logical Segment 正式实例使用 `O`；Event Instrument/SubVoice standalone Preview、Segment Pitch Ruler audition 等非 Logical Segment 实例按 `O = 0`。
### 10.1.2 Gate Length
`Gate Length` 等于 Logical Track 中触发 Note 的长度。
系统级定义：
```text
Gate Length = Logical Track 触发 Note 的 Note On 到 Note Off tick 长度
```
初版 第 10 章《实例生命周期、Loop、Envelope 与重叠》 只按 Note 触发定义生命周期。
未来可保留非 Note Trigger 概念，但初版不在本章定义无 Gate End Trigger 的生命周期。
Logical Track 触发 Note 的 Gate Length 不允许为 0。
```text
Gate Length 必须 > 0
```

Pre-Roll 不参与短音/长音分类，也不改变 Gate Length。未发生 Segment End 裁剪时，从 Instance Origin 到 Gate End 的实例局部 Gate horizon 为：

```text
Pre-Roll Ticks + Gate Length
```
### 10.1.3 Gate End
`Gate End` 是 Logical Track 触发 Note 的逻辑结束点。
关键规则：
```text
Gate End 不等于实际 MIDI Note Off
Gate End 通常触发 Midora Release 阶段
实际 MIDI Note Off 可以晚于 Gate End
```

设 Logical Note anchor 为 `A`、长度为 `L`，则未被硬边界裁剪时 `Gate End = A + L`。Segment End 早于该位置时仍按既有规则把有效 Gate End 裁剪到 Segment End；Pre-Roll 不把 Gate End 向前移动。
### 10.1.4 Release Start
`Release Start` 通常等于 Gate End。
当 Gate End 到达，且当前策略允许 Release 时：
```text
实例进入 Midora Release 阶段
```
### 10.1.5 实际 MIDI Note Off
`实际 MIDI Note Off` 是最终输出给 MIDI / BASSMIDI 的 Note Off 事件。
它可能发生在：
```text
Gate End
Release End
Template Length
Segment End 强制裁剪点
其他相关章节规定的强制裁剪点
```
具体时机由生命周期策略决定。
### 10.1.6 Rendered Instance Length
`Rendered Instance Length` 是编译后实例实际占用生命周期长度。
定义：
```text
从 Event Instrument Instance 起点到该实例最后一个有效输出事件、必要 Note Off、Envelope Release、Tail 完成之前的实际生命周期长度。普通 instance 结束后的 lane 状态保持与后续 lane 激活 Reset 不计入该实例的 Rendered Instance Length。
```
并且：
```text
Rendered Instance Length 不等于 Template Length
Rendered Instance Length 不等于 Gate Length
Rendered Instance Length 是 Channel Unit 资源占用判断的重要输入
Channel Unit 必须被占用到 Rendered Instance Length 结束
```
Segment/消费者硬边界的最终 Reset 属于 lane/范围清理，不属于普通 instance 的 Rendered Instance Length；Segment-owned lane 可以在该长度结束后继续保留到 Segment End。
---
## 10.2 短音与长音判定
### 10.2.1 短音
短音定义为：
```text
Gate Length < Template Length
```
### 10.2.2 长音
长音定义为：
```text
Gate Length > Template Length
```
### 10.2.3 Gate Length 等于 Template Length
当：
```text
Gate Length = Template Length
```
系统按完整 Template Length 播放，实例结束于 Template Length。
该情况不视为短音，也不视为长音。
---
## 10.3 生命周期策略归属与默认值
短音策略与长音策略均属于 Event Instrument 级设置。
初版不允许：
```text
SubVoice 覆盖短音策略
SubVoice 覆盖长音策略
SubVoice 拥有独立生命周期策略
```
初版允许用户编辑：
```text
Short Note Strategy
Long Note Strategy
```
新建 Event Instrument 的默认生命周期策略为：
```text
Short Note Strategy = Cut At Note Off
Long Note Strategy = Hold Last State Until Note Off
Loop = disabled
Envelope Preset 集合为空
```
修改生命周期设置后：
```text
所有引用该 Event Instrument 的 Logical Track 自动使用最新生命周期规则
相关编译、播放、预览、渲染缓存应失效
具体缓存失效范围与策略由 第 12 章《编译系统与 Canonical Compiled Result》 / 第 13 章《播放与预览》 细化
```
---
## 10.4 短音策略
初版支持以下短音策略：
```text
Cut At Note Off
One-Shot / Ignore Note Off
Note Off With Tail Events
```
其中 `Cut At Note Off` 是默认短音策略。
### 10.4.1 Cut At Note Off
`Cut At Note Off` 的系统级含义：
```text
到达 Gate End 时，停止该实例后续模板事件输出，并进入释放 / 结束流程。
```
在存在 Envelope Release 时，`Cut At Note Off` 不表示立刻输出实际 MIDI Note Off。
更准确流程为：
```text
Gate End
→ 停止模板后续事件输出
→ 进入 Midora Release
→ 已发声且尚未关闭的 Note 保持 Note On
→ Release 完成后输出实际 MIDI Note Off
→ Reset
→ Channel Unit 可释放
```
因此，当存在 Envelope Release 时，Rendered Instance Length 可以是：
```text
Gate Length
+ Midora Release Length
+ 实际 MIDI Note Off
+ 必要 Reset
```
如果没有 Release / Tail：
```text
实际 MIDI Note Off 发生在 Gate End
```
### 10.4.2 One-Shot / Ignore Note Off
`One-Shot / Ignore Note Off` 的系统级含义：
```text
无论 Gate Length 多短，实例都至少播放完整 Template Length。
```
规则：
```text
One-Shot 忽略 Gate End
不因短音松开提前 Note Off
不因 Gate End 进入 Envelope Release
按模板自身 Note 长度 / Template Length 生命周期结束
```
One-Shot 不因短音进入 Loop。
### 10.4.3 Note Off With Tail Events
`Note Off With Tail Events` 的系统级含义：
```text
Gate End 后，释放模板内持续音符的逻辑 Gate，但继续播放 Gate End 之后、Template Length 之内的非 Note 尾部事件，并允许 Envelope Release。
```
Tail 事件范围：
```text
Gate End 之后、Template Length 之内的非 Note 事件
```
Tail 不包括：
```text
Gate End 之后的新 Note On
```
当 `Note Off With Tail Events` 下同时存在 Tail 与 Envelope Release 时：
```text
Gate End 后 Tail 模板事件和 Envelope Release 映射输出可以同时存在
实例尾部应延长到 Tail 最后事件与最晚 Envelope Release 结束点中的较晚者
实际 MIDI Note Off 应延后到该较晚者之后
普通结束后不执行通用目标 Reset；lane 状态保持到下一次非重叠复用激活或硬边界
```
### 10.4.4 短音截断与必要 Note Off
当短音策略导致实例提前结束时，系统必须保证不遗留悬挂 Note。
规则：
```text
已经 Note On 且尚未 Note Off 的 Note，必须在生命周期结束流程中获得必要 MIDI Note Off
不得依赖 SoundFont、BASSMIDI 或 All Notes Off 偶然修复
```
---
## 10.5 长音策略
初版支持以下长音策略：
```text
Hold Last State Until Note Off
End At Template Length
```
默认长音策略为：
```text
如果配置 Loop / Envelope，则使用循环 / 包络规则；
否则 Hold Last State Until Note Off。
```
### 10.5.1 Hold Last State Until Note Off
`Hold Last State Until Note Off` 的系统级含义：
```text
Template Length 到达后，不再输出新模板事件；
保持该实例最后状态，直到 Gate End；
Gate End 时进入 Release / 结束流程。
```
不采用：
```text
Template Length 后持续重复最后一个 tick 的事件
Template Length 后自动拉伸所有曲线
Template Length 后自动插值到 Gate End
```
当存在 Envelope Release 时：
```text
保持最后状态到 Gate End
→ 进入 Midora Release
→ Release 完成后实际 MIDI Note Off
→ Reset
```
### 10.5.2 End At Template Length
`End At Template Length` 的系统级含义：
```text
Gate Length 再长，实例也在 Template Length 结束。
```
规则：
```text
实例在 Template Length 结束
不等待 Gate End
不因 Gate End 延后
不因 Envelope Release 超过 Template Length
实际 MIDI Note Off 在 Template Length 处发生
普通结束后 lane 状态保持；下一次非重叠复用由新一轮 lane 激活建立基线，硬边界再执行最终 Reset
```
该策略适合鼓、FX、短促事件乐器等 One-Shot 类长音行为。
---
## 10.6 Midora Release 与实际 MIDI Note Off
### 10.6.1 Release 与 Note Off 核心规则
关键语义：
```text
Gate End ≠ 实际 MIDI Note Off
```
Midora Release 定义为：
```text
Gate End 后、实际 MIDI Note Off 前的一段可控释放阶段。
```
Release 阶段中：
```text
实例中已经 Note On 且尚未 Note Off 的 Note 继续保持
Release 期间不允许产生新的 Note On
Release 期间允许 Tail / Envelope / Logical Parameter 映射输出 / 控制参数继续作用于保持中的 Note
Release 结束后，再输出实际 MIDI Note Off
普通结束后不执行通用目标 Reset，也不释放 Segment-owned lane；下一次非重叠复用在起点重新建立基线，或由硬边界执行最终 Reset / Channel Unit 释放
```
该设计的原因：
```text
如果 Gate End 立即输出 MIDI Note Off，则很多 SoundFont 在短 release 或 0 release 情况下声音已经结束。
此后再输出 CC / Pitch Bend / Expression / Filter 类控制变化，可能已没有可听意义。
因此 Midora Release 应发生在实际 MIDI Note Off 之前。
```
### 10.6.2 Gate End 时进入 Release 的 Note 范围
Gate End 到达时，只保持该实例中：
```text
已经 Note On
且尚未 Note Off
```
的 Note。
不保持：
```text
该实例曾经发出但已关闭的 Note
尚未发出的未来 Note
所有模板 Note
其他实例的 Note
```
### 10.6.3 Release 期间禁止新 Note On
Release 期间不允许产生新的 Note On。
规则：
```text
Release 只保持已有活动 Note
Release 不启动新 Note
Tail 也只允许非 Note 事件
```
### 10.6.4 Gate End 后模板 Note Off 的处理
如果某个 Note 在 Gate End 时仍处于 Note On 状态，而它原本的模板 Note Off 位于 Gate End 之后：
```text
抑制原模板 Note Off
由 Release End 统一输出实际 MIDI Note Off
```
不允许：
```text
原模板 Note Off 仍按原 tick 输出
原模板 Note Off 与 Release End Note Off 都输出
```
### 10.6.5 多个 Release 源
如果多个目标参数引用不同 Envelope Preset，且 Release 长度不同：
```text
实例尾部延长到最晚 Release 结束
实际 MIDI Note Off 延后到最晚 Release 结束后
```
### 10.6.6 Tail 与 Release 同时存在
当 Tail 与 Envelope Release 同时存在时：
```text
实际 MIDI Note Off 延后到 Tail 最后事件与最晚 Envelope Release 结束点中的较晚者之后
```
### 10.6.7 无 Release / 无 Tail
如果 Gate End 后没有 Envelope Release，也没有 Tail：
```text
实际 MIDI Note Off 发生在 Gate End
```
---
## 10.7 Tail 区域
Tail 区域定义为：
```text
Gate End 之后、Template Length 之内允许继续播放的非 Note 事件区域。
```
Tail 只用于 `Note Off With Tail Events` 等允许尾部的策略。
Tail 与 Release 是两个不同机制：
| 机制 | 含义 |
|---|---|
| Tail | 模板中 Gate End 后继续播放的非 Note 事件 |
| Release | Envelope 在 Gate End 后生成的释放变化 |
Tail 事件不允许超过 Template Length。
如果用户添加或移动 Tail 事件超过当前 Template Length：
```text
自动扩展 Template Length
```
或由编辑层阻止非法状态。
Tail 与 Envelope Release 可以时间重叠。
```text
Gate End 后，Tail 模板事件和 Envelope Release 映射输出可以同时存在，并由后续编译排序规则合并。
```
---
## 10.8 Release 期间允许与禁止的事件行为
### 10.8.1 Tail 非 Note 事件
Release 期间仍保持 Note On 时，Tail 中的非 Note 事件继续作用于这些保持中的 Note。
这正是 Tail / Release 在实际 MIDI Note Off 前发生的主要意义。
### 10.8.2 Logical Parameter 映射输出
Release 期间允许 Logical Parameter 继续影响非 Note MIDI / 高级事件输出。
规则：
```text
Logical Parameter 的有效值仍按状态继承解析。
如果实例在 Gate End 后进入 Release，Release 期间的参数映射输出应发生在实际 MIDI Note Off 前。
```
这保证 Expression、Pitch Bend、滤波类控制在实际 MIDI Note Off 前仍可能对保持中的 Note 产生意义。
### 10.8.3 Pitch Bend
Release 期间允许 Pitch Bend 继续变化，并影响保持中的 Note。
### 10.8.4 Program Change / Bank Select
Release 期间允许输出 Program Change / Bank Select。
但系统级说明：
```text
Program Change / Bank Select 对已经发声 Note 的影响依赖具体合成器或 SoundFont 播放后端。
Midora 不在本规格强行禁止用户显式事件。
后续诊断系统可提示此类事件的可移植性或可听效果不确定。
具体诊断等级由 第 15 章《音频文件渲染》 决定。
```
### 10.8.5 RPN / NRPN
Release 期间允许输出 RPN / NRPN。
### 10.8.6 Note velocity
Release 期间不允许改变已经发出 Note 的 velocity。
理由：
```text
MIDI 1.0 Note On velocity 是瞬时属性。
Release 期间不能改变已发 Note velocity。
```
不采用：
```text
重新发送同 pitch Note On 修改 velocity
使用 Note Off velocity 修改已发音 velocity
```
---
## 10.9 Loop 需求
### 10.9.1 Loop 归属
Loop 区域属于 Event Instrument 级生命周期设置。
初版不允许：
```text
SubVoice 拥有独立 Loop
Event Instrument 有默认 Loop 且 SubVoice 覆盖
多条 SubVoice 使用不同 Loop 区间
```
所有 SubVoice 共用同一 Loop 区间。
### 10.9.2 Loop 与 Per-Note Instance Isolation
使用 Loop 要求 `Per-Note Instance Isolation` 开启。
当 Event Instrument 已有 Loop 设置，用户关闭 `Per-Note Instance Isolation` 时：
```text
保留 Loop 数据
Loop 进入不可编辑 / 暂不生效 / 受限制状态
未来重新开启 Per-Note Instance Isolation 时恢复可编辑和可使用状态
```
关闭实例隔离不是数据删除命令。
### 10.9.3 Loop 默认状态
新建 Event Instrument 默认不启用 Loop。
当用户首次启用 Loop 时，默认 Loop 区间为：
```text
Loop Start = 0
Loop End = Template Length
```
### 10.9.4 Loop 区间合法性
完整 Loop 区间必须满足：
```text
0 <= Loop Start < Loop End <= Template Length
```
源数据允许暂时只填写 `Loop Start` 或 `Loop End`，以支持两个独立编辑控件逐项提交。该状态定义为“不完整 Loop Draft”：
```text
只存在 Loop Start 或只存在 Loop End
可以保存、打开、Undo / Redo
没有 Loop 执行语义
Full Compile 与 Incremental Compile 必须产生 Error MIDORA1212，结果不可消费
播放、预览、MIDI 导出和音频渲染不得接收或解释该不完整状态
```
两端都不存在仍表示 Loop disabled；两端都存在时才形成完整 Loop 区间。

每个已存在端点自身仍必须位于 Template Length 内：
```text
0 <= Loop Start < Template Length
0 < Loop End <= Template Length
```
如果 Template Length 缩短会导致现有 Loop End 超过 Template Length：
```text
不允许将 Template Length 缩短到小于 Loop End
除非用户先调整或禁用 Loop
```
如果当前只存在 Loop Start，则同样不允许把 Template Length 缩短到 `Loop Start` 或更小。
用户编辑 Loop Start / End 导致以下非法状态时：
```text
Loop Start < 0
完整 Loop 的 Loop Start >= Loop End
Loop End > Template Length
```
编辑操作不被接受或即时修正到合法范围。
除上述明确允许的不完整 Loop Draft 外，系统不保存非法 Loop 区间。
### 10.9.5 Loop 触发条件
Loop 的进入条件独立于 §10.2 的长 / 短 / 等长音分类；不得要求 `Gate Length > Template Length` 才启用循环。短音 `One-Shot / Ignore Note Off` 是明确例外，见 §10.9.7.2。
规则：
```text
完整有效 Loop，且模板时钟播放到 Loop End 时 Gate End 尚未到达，则跳回 Loop Start。
此后按 [Loop Start, Loop End) 重复，直到 Gate End 或已有生命周期 / Segment 硬结束。
```
Loop 不从实例开始立即循环。
Gate End 本身只负责退出循环，不触发循环；Gate 恰在首轮 Loop End 或之前结束不重复。

对 Logical Instance，模板时钟从 `Instance Start = Logical Gate Start - Pre-Roll` 开始，因此比较的是实例局部 Gate horizon（`Pre-Roll + effective Gate Length`），而不是仅 Logical Gate Length。Pre-Roll 仍不改变 Mapping 的 `gateLength` 或长 / 短 / 等长分类。

Loop 时间映射必须统一用于原始 Template Event、Value Curve、状态型 Event Mapping 的原始事件序列，以及所有 Mapping 的 `TemplateTick`。Envelope 阶段仍按实际实例经过时间及 Release 起点计算，不随 Loop 重启；Logical Parameter 仍按实际 Segment 内容 Tick 求值。

例如 Template=768、Loop=[192,384)、Pre-Roll=0，Loop 内 tick 192 / 288 各有一个事件：Gate=576 时输出在 192 / 288 / 384 / 480；Gate=768 时再输出 576 / 672。Gate=768 仍遵循等长音的模板结束规则，不因此启用长音延长策略；`End At Template Length` 的硬结束同样不因循环延后。
### 10.9.6 Gate End 发生在 Loop 中间
当 Gate End 发生在 Loop 区间中间：
```text
立即退出 Loop
进入 Release / 结束流程
```
不要求先播放到 Loop End。
### 10.9.7 Loop 与短音策略
#### 10.9.7.1 Cut At Note Off
当短音策略为 `Cut At Note Off`，且 Gate End 早于 Loop End：
```text
Gate End 到达时立即退出实例模板输出，并进入释放 / 结束流程。
```
Gate End 超过 Loop End 时也应正常重复 Loop，直到 Gate End 立即停止新的模板事件；已引用 Envelope 的 Release 按既有规则继续，不追加 Loop 后的模板 Tail。
#### 10.9.7.2 One-Shot / Ignore Note Off
当短音策略为 `One-Shot / Ignore Note Off`，且 Gate Length 小于 Template Length：
```text
One-Shot 忽略 Gate End
实例至少完整播放 Template Length
短音 One-Shot 不进入 Loop，只播放一次模板
```
#### 10.9.7.3 Note Off With Tail Events
当短音策略为 `Note Off With Tail Events`，且 Gate End 发生在 Loop 区间中：
```text
立即退出 Loop
保持 / 释放已发音 Note
已进入循环时，从 Gate End 起播放模板 [Loop End, Template Length) 内的非 Note 尾部事件
允许 Envelope Release
```
进入循环后的尾部事件 `templateTick` 映射到实例局部 `Gate horizon + (templateTick - Loop End)`；Tail 时长相应为 `Template Length - Loop End`，不得仍在原未循环模板结束点截断移位后的 Tail。未进入循环时保留普通短音 Tail 规则。无论哪种情况，Segment 硬边界始终优先，Gate End 后不得启动新的 Note。
### 10.9.8 Loop 区间内事件重复
Loop 区间内的事件每轮循环都重新输出。
包括：
```text
Note
CC
Pitch Bend
Program Change
Bank Select
RPN
NRPN
Pitch Bend Range
其他允许处于 Loop 区间内的非 Note 事件
```
Loop 区间内 CC / Pitch Bend / RPN / NRPN 等状态事件在每轮循环时按原相对 tick 重复输出。
### 10.9.9 Loop Start 前事件
Loop Start 之前的事件只在实例开始阶段播放一次。
它们可理解为 Attack / 前奏区。
### 10.9.10 Loop End 后到 Template Length 之间的事件
当 Loop 生效并进入循环后，Loop End 之后到 Template Length 之间的模板事件：
```text
退出 Loop 后才播放
如果 Gate End 导致直接进入 Release / 结束流程，则可能不播放
```
该区域可作为 Release / Tail 区域。
### 10.9.11 Loop 退出时状态补偿
退出 Loop 时，系统不自动把 CC / Pitch Bend 等状态补偿到“如果不循环而直接播放到退出点”的值。
规则：
```text
退出 Loop 后状态就是最后一轮实际输出后的状态
后续 Release / Tail / Reset 负责继续变化或释放
```
### 10.9.12 Loop 是否允许包含 Note
Loop 区间内允许包含 Note 事件。
Loop 会重复其中的 Note 与非 Note 事件。
### 10.9.13 Loop 边界处跨越 Note
#### 10.9.13.1 Note 在 Loop Start 前开始并延伸进 Loop 区间
允许存在。
规则：
```text
该 Note 的开始只在首次播放时发生
Loop 重复时不会重新触发该 Note On
除非 Note On 本身位于 Loop 区间内
```
#### 10.9.13.2 Note 在 Loop 区间内开始，但 Note Off 位于 Loop End 之后
该情况编译失败或编辑阶段禁止。
理由：
```text
该 Note 的 Note On 在 Loop 内重复
但 Note Off 不在同一重复区间内
容易产生悬挂或叠音
```
系统不得自动裁剪、自动移动或交给后端处理。
#### 10.9.13.3 Note 从 Loop Start 前开始，并在 Loop End 后结束，完整覆盖 Loop 区间
允许存在。
规则：
```text
该 Note 不参与每轮重触发
它作为进入 Loop 前已经发出的持续 Note
Gate End 或策略结束时必须正确 Note Off
```
### 10.9.14 Loop 数量与方向
初版每个 Event Instrument 最多一个 Loop 区间。
初版不支持：
```text
多个顺序 Loop 区间
嵌套 Loop
反向 Loop
乒乓 Loop
用户自定义 Loop 方向
最大 Loop 次数限制
按条件停止
```
Loop 持续到 Gate End 或生命周期策略结束。
---
## 10.10 Envelope Preset 需求
### 10.10.1 归属
Envelope Preset 属于单个 Event Instrument 内部集合。
初版不支持：
```text
Project 级共享 Envelope Preset
SubVoice 级 Envelope Preset 集合
程序级 Envelope Preset
Envelope Preset 文件导入 / 导出
生命周期策略 Preset 或全局生命周期模板
```
### 10.10.2 稳定 ID
Envelope Preset 必须拥有内部稳定 ID。
原因：
```text
Envelope Preset 名称可选且允许重复
不能用名称作为引用依据
不能用数组顺序作为稳定引用依据
```
### 10.10.3 名称规则
Envelope Preset 名称规则：
```text
可选
可为空
允许重复
```
未命名 Envelope Preset 不产生诊断。
### 10.10.4 是否直接产生 MIDI 事件
Envelope Preset 本身不直接产生 MIDI 事件。
它只是可被映射链或事件参数引用的调制形状。
必须绑定到具体目标参数后才有输出语义。
不采用：
```text
Envelope Preset 自动输出 CC11 Expression
Envelope Preset 自动作用于所有 Note velocity
Envelope Preset 自动输出到某个默认 CC
```
### 10.10.5 Envelope 与 Mapping Chain
Envelope 可作为映射源，接入 Mapping Chain，作用到 第 9 章《曲线、Logical Parameter 与映射》 已允许映射的事件值参数。
规则：
```text
Envelope Step 是普通 Mapping Step
Envelope Step 与 Mapping Function Expression Step、图形映射 Step 等一起按 Mapping Chain 顺序组合
Envelope 不拥有固定优先级
```
同一个 Envelope Preset 可被同一 Event Instrument 内多个事件参数引用。
修改 Envelope Preset 后：
```text
所有引用它的映射目标自动使用最新版本
```
### 10.10.6 Envelope 与 Per-Note Instance Isolation
使用 Envelope 要求 `Per-Note Instance Isolation` 开启。
如果 Event Instrument 已有 Envelope 数据，用户关闭 `Per-Note Instance Isolation`：
```text
保留 Envelope 数据
Envelope 数据进入不可编辑 / 暂不生效 / 受限制状态
重新开启 Per-Note Instance Isolation 后恢复可编辑和可使用状态
```
关闭实例隔离不是数据删除命令。
### 10.10.7 Envelope 时间基准
Envelope 时间以每个 Event Instrument Instance 的实例起点为 0。
```text
Envelope time 0 = 当前 Event Instrument Instance 起点
```
Envelope 不使用 Project 绝对 tick。
Envelope 不使用 SubVoice 独立时间。
### 10.10.8 Envelope 是否依赖 Gate Length
ADSR-like Envelope 必须能读取 Gate Length / Gate End。
原因：
```text
Sustain 阶段需要持续到 Gate End
Release 阶段由 Gate End 触发
```
---
## 10.11 Envelope 结构
初版 Envelope Preset 采用 ADSR-like 结构：
```text
Delay
Attack
Hold
Decay
Sustain Level
Release
```
并包含可编辑值：
```text
Start Value
Peak Value
Sustain Level
End Value
```
### 10.11.1 阶段语义
Envelope 阶段语义：
```text
Delay：实例开始后保持起始值
Attack：从起始值过渡到峰值
Hold：保持峰值
Decay：从峰值过渡到 Sustain Level
Sustain：保持 Sustain Level，直到 Gate End
Release：Gate End 后从当前值过渡到结束值
```
### 10.11.2 输出值域
Envelope Preset 标准输出值域为：
```text
0.0 – 1.0
```
Envelope 输出归一化值，再通过映射链作用到目标参数。
Envelope Preset 内的数值参数必须限制在合法范围内：
```text
Start Value: 0.0–1.0
Peak Value: 0.0–1.0
Sustain Level: 0.0–1.0
End Value: 0.0–1.0
```
不允许越界后再 clamp。
不允许越界保存后等编译时报错。
### 10.11.3 阶段时长单位
Envelope 阶段时长初版使用 tick。
合法性：
```text
Delay >= 0
Attack >= 0
Hold >= 0
Decay >= 0
Release >= 0
```
允许 0 时长阶段。
`Release = 0` 允许，表示 Gate End 后立即到达 End Value。
### 10.11.4 默认 Envelope 值
新建 Envelope Preset 的默认值为：
```text
Start Value = 0
Peak Value = 1
Sustain Level = 1
End Value = 0
Delay = 0
Attack = 0
Hold = 0
Decay = 0
Release = 0
```
### 10.11.5 阶段曲线形状
初版 Envelope 每个阶段至少支持线性曲线。
是否支持以下细节由实现设计确定：
```text
指数曲线
对数曲线
曲线强度
更复杂的曲线编辑方式
```
### 10.11.6 离散化
Envelope 的 Attack / Decay / Release 曲线最终如何变成 MIDI 事件，服从第 12.8.6 节统一规定的逐整数 tick 参考求值与最终整数值重复抑制语义；第 14 章《MIDI 导出》只消费 canonical compiled result，不得另行离散化。
第 10 章《实例生命周期、Loop、Envelope 与重叠》 不定义：
```text
曲线求值函数的内部数据结构
与逐 tick 参考结果完全等价的跳跃求值优化
离散事件的内部缓存结构
```
---
## 10.12 Envelope 阶段与 Gate End
### 10.12.1 Gate End 早于 Sustain
如果 Gate End 早于 Envelope 进入 Sustain 阶段：
```text
Gate End 发生时从当前 Envelope 值进入 Release
```
不要求先完成 Attack / Hold / Decay。
不视为错误。
### 10.12.2 Release Start 的值
Envelope Release 开始时的值为 Gate End 当刻 Envelope 的当前输出值。
### 10.12.3 Gate End 发生在 Delay 阶段
如果 Gate End 发生在 Delay 阶段：
```text
Release 从 Delay 当前保持值开始
```
### 10.12.4 Gate End 发生在 Attack 阶段
如果 Gate End 发生在 Attack 阶段：
```text
Release 从 Attack 当前插值值开始
```
### 10.12.5 Gate End 发生在 Decay 阶段
如果 Gate End 发生在 Decay 阶段：
```text
Release 从 Decay 当前插值值开始
```
### 10.12.6 Release End 后输出值
Envelope Release 结束后，该 Envelope 输出保持 End Value，直到 Reset 或后续状态覆盖。
Release End 后不强制持续重复输出 End Value。
系统只需保证目标参数达到并保持 End Value。
是否重复输出由编译优化决定。
### 10.12.7 Sustain 阶段输出
Envelope Sustain 阶段不强制重复输出持续事件。
系统只需保证目标参数在 Sustain 阶段保持 Sustain Level。
是否重复输出由编译优化决定。
### 10.12.8 Release 输出范围
Envelope Release 作为映射源时，只在：
```text
Release Start 到 Release End
```
之间输出映射结果。

该范围采用 `[Release Start, Release End)`。当 `Release > 0` 时，范围内最后一个整数 tick 的 Envelope 值必须已经达到 End Value；普通实际 MIDI Note Off 发生在 Release End，不追加通用目标 Reset，不得因右开边界导致最后一次输出仍高于 End Value。`Release = 0` 时 Gate End 当刻直接到达 End Value，并进入同 tick 的结束排序。
---
## 10.13 Envelope 与生命周期策略关系
### 10.13.1 Release 可延长 Rendered Instance Length
Gate End 后进入 Release 时，Release 尾部可以让 Rendered Instance Length 超过 Template Length。
Release 是实例实际长度的一部分。
### 10.13.2 Cut At Note Off
当短音策略为 `Cut At Note Off` 时：
```text
停止模板后续事件
但 Envelope Release 可继续生成已绑定目标的释放变化
已发声且尚未 Note Off 的 Note 保持到 Release End
Release End 后实际 MIDI Note Off
```
### 10.13.3 Note Off With Tail Events
当短音策略为 `Note Off With Tail Events` 时：
```text
Envelope Release 与非 Note Tail 事件可以并存
实例尾部延长到 Tail 最后事件和最晚 Envelope Release 结束点中的较晚者
实际 MIDI Note Off 延后到该较晚者之后
```
### 10.13.4 One-Shot / Ignore Note Off
当短音策略为 `One-Shot / Ignore Note Off` 时：
```text
One-Shot 忽略 Gate End
Envelope 不因 Gate End 进入 Release
实例至少按 Template Length 播放
```
### 10.13.5 Hold Last State Until Note Off
当长音策略为 `Hold Last State Until Note Off`，且存在 Envelope Release 时：
```text
保持最后状态到 Gate End
然后进入 Envelope Release / 结束流程
```
### 10.13.6 Loop
当实例使用 Loop，且 Gate End 在生命周期硬结束前到达时，按该实例策略：
```text
退出 Loop
进入 Envelope Release / 结束流程
```
不要求完成当前 Loop 到 Loop End。
### 10.13.7 End At Template Length
当长音策略为 `End At Template Length` 时：
```text
实例在 Template Length 结束
不进入因 Gate End 触发的延后 Release
不允许 Envelope Release 超过 Template Length
```
---
## 10.14 Envelope Preset 引用、删除与复制
### 10.14.1 删除被引用 Envelope Preset
删除正在被 Mapping Chain 引用的 Envelope Preset 时：
```text
删除前必须确认
删除后相关 Mapping Step 变为断裂引用
断裂引用导致使用该 Mapping Step 的 Event Instrument 无效
用户必须修复后该 Event Instrument 才能恢复有效
```
不采用：
```text
自动删除所有引用它的 Mapping Step
自动替换为默认 Envelope
阻止删除直到用户手动解除所有引用
```
### 10.14.2 未引用 Envelope Preset
未被任何 Mapping Step 引用的 Envelope Preset 不产生诊断。
这是正常状态。
### 10.14.3 Event Instrument 复制
复制 Event Instrument 时：
```text
其内部 Envelope Preset 深拷贝
复制出的 Envelope Preset 生成新的稳定 ID
复制后的引用应指向复制体内部的新 Envelope Preset
```
### 10.14.4 SubVoice 复制
同一 Event Instrument 内复制 SubVoice 时，如果其事件映射链引用 Event Instrument 内 Envelope Preset：
```text
Mapping Step 继续引用同一个 Event Instrument 内的 Envelope Preset
不复制 Envelope Preset
```
理由：
```text
Envelope Preset 属于 Event Instrument 级共享定义，不属于单条 SubVoice。
```
### 10.14.5 跨 Event Instrument 复制 SubVoice
初版不支持跨 Event Instrument 复制 SubVoice。
---
## 10.15 Rendered Instance Length 计算口径
Rendered Instance Length 由生命周期规则共同决定，而不是由单一长度决定。
可能影响因素包括：
```text
Gate Length
Template Length
短音策略
长音策略
Loop
Envelope Release
Tail
实际 MIDI Note Off
Reset
Segment 强制裁剪
Project End Marker 后续裁剪规则
Overlap 策略对旧实例的截断
```

本节全部 Rendered Instance Length 都从 Instance Origin 计量。对 Pre-Roll 为 `O` 的 Logical Segment 实例，任何由 Logical Gate End 决定的局部生命周期边界都位于 `O + effective Gate Length`；由 Template Length 自身决定的边界仍从模板 origin 计量，不再额外加一次 `O`。
### 10.15.1 Cut At Note Off
当短音策略为 `Cut At Note Off` 且存在 Envelope Release 时：
```text
Rendered Instance Length = Pre-Roll Ticks + effective Gate Length + Release Length + 必要 MIDI Note Off
```
如果存在多个 Release：
```text
取最晚 Release 结束点
```
### 10.15.2 Note Off With Tail Events
当短音策略为 `Note Off With Tail Events`：
```text
Rendered Instance Length 至少延长到以下较晚者：
- Tail 最后事件时间
- 最晚 Envelope Release 结束时间
然后加必要 MIDI Note Off 与 Reset
```
### 10.15.3 One-Shot / Ignore Note Off
当短音策略为 `One-Shot / Ignore Note Off`：
```text
Gate End 不触发提前结束
Gate End 不触发 Envelope Release
实例至少完整播放 Template Length
```
### 10.15.4 Loop
启用 Loop 后：
```text
Rendered Instance Length 由 Gate Length、Release、Tail、End At Template Length 等生命周期规则共同决定
```
不等于：
```text
Template Length
Gate Length
Loop End
```
### 10.15.5 Reset 与 Rendered Instance Length
lane 激活 Reset 发生在启用或非重叠复用实例的开始 tick，不延长 Rendered Instance Length；Segment/消费者硬边界的最终 Reset 属于 lane/范围清理，也不计入普通 instance 的 Rendered Instance Length。

产生过 Note 的 Segment-owned lane 仍须在普通 instance 生命周期结束后保留到 Segment End，并在最终 Reset 完成后才可释放。该额外占用是 lane/Segment 资源语义，不得反向并入单个 instance 的 Rendered Instance Length。
---
## 10.16 Initial State 与 Reset
### 10.16.1 Initial State Defaults
`Initial State Defaults` 表示 lane 激活实例开始时希望主动写入的默认 MIDI 状态。
规则：
```text
Initial State Defaults 属于 lane 激活实例的开始状态注入
Initial State Defaults 不影响 Template Length
Initial State Defaults 在 lane 首次启用或非重叠复用实例开始时输出，因此属于该实例生命周期内的有效输出
Initial State Defaults 与用户 tick 0 手动画的同类事件冲突时，用户事件优先
共享 lane 内仍重叠的后续实例不重复输出 Initial State Defaults
```

存在 Pre-Roll 时，“实例开始”指提前后的 Instance Origin `A - O`，不是 Logical Gate Start `A`。Reset Defaults、Initial State 和模板 tick 0 用户状态事件都按该实际 tick 排序；不得延迟到 anchor 后再建立状态。
### 10.16.2 Reset Defaults
`Reset Defaults` 表示 lane 启用/非重叠复用前建立确定性基线，以及裁剪、Segment 结束或释放 Channel Unit 前恢复安全状态的项目 / 系统级规则。
规则：
```text
lane 激活 Reset 与硬边界 Reset 都是 Project / 系统级语义
Event Instrument 不覆盖 Reset Defaults
SubVoice 不覆盖 Reset Defaults
lane 激活只重置该 SubVoice 实际可能使用的状态目标闭包
lane 激活 Reset 先于 Initial State、用户 tick 0 状态事件与 Note On
共享 lane 内仍有重叠 instance 时，后续 Gate Start 不重复执行 lane 激活 Reset
```
### 10.16.3 生命周期层面顺序
普通生命周期结束顺序为：
```text
Release / Tail 完成
→ 必要 MIDI Note Off
→ 不执行通用 CC / Pitch Bend / RPN / NRPN / Bank / Program Reset
→ SoundFont 原生 release 继续渲染到自然静音或 Segment End
→ 同一 Segment 内后续非重叠 instance 复用该 lane 时，先执行目标闭包 Reset Defaults
→ Initial State / 用户 tick 0 状态事件 / Note On
```
Segment End 强制裁剪时例外：
```text
Segment End
→ 立即 MIDI Note Off
→ CC120 All Sound Off
→ 最终目标 Reset
→ Channel Unit 可释放
```

产生过 Note 的 lane 在普通 instance 结束时不得因音频 fragment 结束而停止解码、停止混音或执行等价于 All Sound Off 的 Stream 冷重置。该 lane 的正式释放边界是 Segment End；这不把 SoundFont 原生 release 计入 Event Instrument 的 Rendered Instance Length，也不产生新的模板事件。
### 10.16.4 All Notes Off
实例结束时不允许用 All Notes Off 替代精确 Note Off。
规则：
```text
实例内已经发出的 Note 应优先使用精确 Note Off
All Notes Off 属于 Reset / 安全兜底
All Notes Off 不替代实例语义 Note Off
```
### 10.16.5 Reset 优化
如果编译器确认某个 lane 激活或硬边界没有相关 Channel-Wide 状态目标，目标 Reset 可以省略。同 tick 的 Reset Defaults、Initial State 与用户状态事件可折叠为唯一最终有效值。
但语义上必须保证：
```text
每次 lane 激活后的状态不依赖历史实例
Channel Unit 释放后处于项目定义的安全状态
```
具体规则由第 12 章《编译系统与 Canonical Compiled Result》规定。
---
## 10.17 生命周期与 Overlap 的接口
### 10.17.1 第 10 章《实例生命周期、Loop、Envelope 与重叠》 范围
第 10 章《实例生命周期、Loop、Envelope 与重叠》 只定义生命周期与 Overlap 的接口，不完整定义 Overlap 决策算法。
Overlap 的资源冲突与结果生成由第 12 章《编译系统与 Canonical Compiled Result》处理；`Reject` / `Warn` 的诊断等级与成功判定由 10.17.8 和 12.19.8 固定。
### 10.17.2 默认 Overlap 策略
承接 第 7 章《Event Instrument Library 与 Event Instrument 定义》，默认：
```text
Overlap Strategy = Reject
```
默认 Overlap 作用范围为：
```text
Same Pitch Only
```
### 10.17.3 Overlap 判断范围
初版 Overlap 策略只在同一：
```text
Event Instrument Usage
```
内判断；同一 Usage 的成员可以跨多个 Logical Track，因此跨成员 Track 的实例重叠仍属于同一个 Overlap 域。
不同 Usage 之间的资源冲突由 Channel Group 分配 / 编译系统处理，不由 Event Instrument Overlap 策略直接决定；不得仅因两个 Usage 引用同一 Definition 就合并 Overlap 域。
### 10.17.4 Overlap 判断长度
Overlap 判断应基于：
```text
Rendered Instance Length
```
而不是只基于 Gate Length 或 Template Length。
Overlap 必须考虑：
```text
Release
Tail
Loop
Reset
其他导致实例尚未结束的生命周期因素
```
Release 阶段视为旧实例尚未结束。
Reset 阶段至少在资源占用上视为尚未完全释放。

Overlap 区间起点必须使用提前后的 Instance Origin。Pre-Roll 因而可能使两个视觉上未重叠的 Logical Note 产生实例生命周期重叠；`Cut Previous` 的“新实例起点”同样指该 origin，而不是 Logical Gate anchor。
### 10.17.5 Per-Note Instance Isolation 开启
当 Per-Note Instance Isolation 开启时：
```text
同一 Usage 内重叠 Note 会生成多个独立实例
但仍需通过 Overlap 策略判断是否允许
```
实例隔离解决状态隔离，不自动代表音乐上允许重叠。
### 10.17.6 Per-Note Instance Isolation 关闭
当 Per-Note Instance Isolation 关闭时：
```text
同一 Usage 活动连通区间内的重叠 Note 可共享 Channel Group
但仍需遵守生命周期与功能限制
```
### 10.17.7 初版支持的 Overlap 策略
初版支持：
```text
Reject
Warn
Let Overlap
Cut Previous
Cut New / Reject New
```
### 10.17.8 Reject 与 Warn
`Reject` 和 `Warn` 都不自动截断旧实例，也不修改 Project 源数据。发生策略作用范围内的生命周期重叠时：
```text
Reject -> 产生 Error；当前编译结果不可消费
Warn   -> 产生 Warning；若不存在其他失败条件，重叠实例保留在 canonical 结果中
```
`Warn` 默认不导致失败；启用“强制 Warning 导致编译失败”后，结果不可消费，但该诊断仍保持 Warning，不升级为 Error。
### 10.17.9 Let Overlap
初版支持 `Let Overlap`，但默认不是 Let Overlap。
### 10.17.10 Cut Previous
`Cut Previous` 表示：
```text
新实例开始时截断旧实例，并让旧实例进入必要 Release / Note Off 流程；普通截断结束不追加通用目标 Reset。
```
截断点为：
```text
新实例起点
```
截断旧实例时：
```text
允许旧实例进入 Release
截断点作为旧实例新的 Gate End / Release Start
```
当使用 Pre-Roll 且新实例 origin 落在旧实例的 Logical Gate Start 之前时，旧实例在前缀内被截断。此时旧实例的截断专用 MappingContext `gateLength` 固定为 0；该值只表示 Overlap Policy 已在 Gate 开始前终止既有实例，不放宽普通 Logical Note 的 `Gate Length > 0` 约束。
如果 Cut Previous 后旧实例进入 Release，导致旧实例仍与新实例在时间上重叠：
```text
允许这种 Release 重叠
具体资源能否同时满足由 Channel Group 分配决定
如果资源不足则编译失败
```
### 10.17.11 Cut New / Reject New
`Cut New / Reject New` 表示：
```text
若新实例与旧实例冲突，则新实例不产生。
```
该行为不能静默丢弃用户触发内容。
应至少产生信息或警告诊断。
具体等级由 第 15 章《音频文件渲染》 决定。
### 10.17.12 Overlap 作用范围选项
初版支持以下作用范围选项：
```text
Same Pitch Only
Any Pitch
```
默认：
```text
Same Pitch Only
```
Same Pitch Only 的 pitch 比较使用：
```text
Logical Track 触发 Note pitch
```
而不是编译后 SubVoice Note pitch。
---
## 10.18 生命周期与 Segment 的接口
### 10.18.1 第 10 章《实例生命周期、Loop、Envelope 与重叠》 范围
第 10 章《实例生命周期、Loop、Envelope 与重叠》 只定义生命周期与 Segment 边界的接口，不完整定义 Segment 编辑 / 裁剪算法。
Segment 编辑、裁剪细则留给 第 11 章《Logical Track、Segment 与编曲语义》 / 第 12 章《编译系统与 Canonical Compiled Result》。
### 10.18.2 Segment End 作为强制裁剪边界
Segment End 可以成为 Event Instrument Instance 的强制裁剪边界。
具体默认是否裁剪、是否可配置由 第 11 章《Logical Track、Segment 与编曲语义》 / 第 12 章《编译系统与 Canonical Compiled Result》 细化。
### 10.18.3 Segment End 强制裁剪
如果 Segment End 强制裁剪一个尚未结束的实例：
```text
不允许该实例继续 Release
必须立即输出实际 MIDI Note Off
然后进入 Reset / Channel Unit 释放流程
```
Segment End 是硬边界。
它与普通 Gate End 不同：
| 边界 | 行为 |
|---|---|
| Gate End | 逻辑松开，可进入 Midora Release，实际 MIDI Note Off 可延后 |
| Segment End 强制裁剪 | 硬裁剪边界，不允许 Release 越界，立即 Note Off + Reset |
### 10.18.4 Segment End 与 Reset
Segment End 裁剪或结束实例后，必须进入必要 Reset / Channel Unit 释放流程。

CC120 All Sound Off 是硬清理，不是普通 Gate/Release/Tail 结束手段。普通生命周期结束只执行精确 NoteOff，不执行通用目标 Reset，也不发送 CC120；只有 Segment End 强制裁剪以及消费者显式范围结束等硬边界允许发送 CC120。不得因 Gate End 时“当前没有其他 Gate”而条件性插入 CC120 或目标 Reset。
普通实例结束不释放 Segment-owned lane；只有 lane 后续非重叠复用时执行新的起点 Reset，或在资源/范围硬边界进入最终 Reset 语义。
### 10.18.5 Segment 末尾 Reset
承接本规格其他章节大方向需求：
```text
每个 Segment 末尾都会将该 Segment 使用过的事件全部 Reset。
```
具体“使用过”的定义与 Reset 插入细节留给 第 11 章《Logical Track、Segment 与编曲语义》 / 第 12 章《编译系统与 Canonical Compiled Result》 / 第 15 章《音频文件渲染》。
---
## 10.19 Logical Parameter 与生命周期接口
### 10.19.1 基本关系
Logical Parameter 可影响生命周期内的非 Note MIDI / 高级事件输出。
规则：
```text
Logical Parameter 不直接产生 Event Instrument Instance。
Logical Parameter 不直接产生 Note On / Note Off。
Logical Parameter 通过 Event Instrument 内的 Mapping 影响 SubVoice 非 Note MIDI / 高级事件参数。
Logical Parameter 的有效值在实例生命周期内按状态继承解析。
```
### 10.19.2 没有活动实例时的参数状态
Logical Parameter 在没有活动实例时不直接输出 MIDI 事件。
但 Segment 内 Logical Parameter Lane 仍然需要维护参数有效状态。
规则：
```text
没有活动实例时，Logical Parameter Lane 继续按状态型时间数据解析。
没有新值时继承上一个有效值。
没有上一个有效值时使用 Logical Parameter defaultValue。
该状态可被后续新实例读取。
```
### 10.19.3 Note On 到达时的参数状态读取
当 Logical Note 触发 Event Instrument Instance 时：
```text
实例应从提前后的 Instance Origin 开始，按每个模板/派生事件的实际 absolute tick 读取 Logical Parameter 有效状态。
如果需要在同 tick 的 Note On 前设置目标状态，编译器应在 Note On 前插入对应参数映射输出事件。
```

不得把 Logical Gate anchor tick 的未来参数状态倒灌到 Pre-Roll 区间。用户若希望前缀使用某一参数状态，必须令该状态在 Instance Origin 已经有效。
这适用于例如：
```text
Note On 前先设置 Mod / Expression
Note On 前先设置 Pitch Bend 或 Pitch Bend Range
Note On 前先设置 NRPN / RPN 派生控制状态
```
具体同 tick 底层事件排序由 第 12 章《编译系统与 Canonical Compiled Result》 / 第 14 章《MIDI 导出》 细化。
### 10.19.4 Release / Tail 阶段
Release / Tail 阶段中：
```text
Logical Parameter 的有效值仍按状态继承解析。
Logical Parameter Mapping 输出可继续作用于保持中的 Note。
如果实例在 Gate End 后进入 Release，Release 期间的参数映射输出应发生在实际 MIDI Note Off 前。
```
该规则与本章“Gate End ≠ 实际 MIDI Note Off”的核心修正一致。
### 10.19.5 Segment End 强制裁剪
Segment End 是硬边界时：
```text
Segment End 后的 Logical Parameter 输出被裁剪。
不允许为了 Release / Tail 越界继续输出 Logical Parameter Mapping 结果。
除非相关专项章节明确允许非硬边界模式，否则 Segment End 后必须立即进入必要 Note Off 与 Reset 流程。
```
因此，Segment End 强制裁剪不仅裁剪模板事件，也裁剪 Logical Parameter 派生输出。
### 10.19.6 资源占用关系
Logical Parameter 本身不改变“一条 SubVoice = 一个 Channel Unit”的初版资源模型。
但 Logical Parameter 可能影响：
```text
实例生命周期内实际输出的非 Note 事件
Release / Tail 阶段输出内容
同 tick Note On 前需要注入的状态事件
最终 Reset 前 Channel Unit 是否仍需保持占用
```
具体资源占用扫描和优化由第 12 章《编译系统与 Canonical Compiled Result》规定。
## 10.20 生命周期与 Project End Marker 的接口
### 10.20.1 第 10 章《实例生命周期、Loop、Envelope 与重叠》 范围
第 10 章《实例生命周期、Loop、Envelope 与重叠》 只定义生命周期与 Project End Marker 的接口，不完整定义导出 / 渲染范围裁剪。
Project End Marker 具体裁剪行为留给 第 12 章《编译系统与 Canonical Compiled Result》 / 第 13 章《播放与预览》 / 第 14 章《MIDI 导出》。
### 10.20.2 Project End Marker 是否可截断实例
Project End Marker 作为显式项目结束位置时，可能截断尚未结束的实例。
但以下内容由实现设计确定：
```text
默认是否截断
如何截断
是否允许用户覆盖范围
导出 / 渲染是否一致
Project End Marker 之后内容的诊断等级
```
### 10.20.3 Project End Marker 截断与 Note Off / Reset
如果 Project End Marker 导致实例结束或导出 / 渲染范围结束：
```text
需要考虑必要 MIDI Note Off / Reset
不能留下悬挂 Note
不能留下未恢复 Channel-Wide 状态
```
### 10.20.4 Rendered Instance Length 与 Project End Marker
自然 Rendered Instance Length 先按生命周期规则计算。
Project End Marker 是后续范围 / 裁剪规则输入。
Project End Marker 不直接改写实例自然 Rendered Instance Length。
---
## 10.21 无输出实例生命周期
如果 Event Instrument 被触发但最终没有任何输出事件，仍需要生命周期计算。
规则：
```text
无输出实例可以有理论 Rendered Instance Length
理论生命周期存在
是否优化掉资源占用由 第 12 章《编译系统与 Canonical Compiled Result》 决定
是否导出任何事件由 第 12 章《编译系统与 Canonical Compiled Result》 / 第 14 章《MIDI 导出》 决定
```
不采用：
```text
无输出实例直接忽略
无输出实例编译失败
无输出实例固定长度为 0
```
---
## 10.22 保存、撤销 / 重做与缓存失效
### 10.22.1 撤销 / 重做
修改以下内容必须进入全项目撤销 / 重做：
```text
短音策略
长音策略
Pre-Roll Ticks
Loop 启用状态
Loop Start / Loop End
Envelope Preset 创建 / 删除 / 修改
Envelope Preset 引用关系
生命周期相关其他 Event Instrument 定义内容
```
### 10.22.2 Project 修改状态
修改生命周期相关设置应使 Project 进入已修改状态。
不以该 Event Instrument 是否已被 Logical Track 使用为条件。
### 10.22.3 保存
短音策略、长音策略、Pre-Roll Ticks、Loop 设置、Envelope Preset 必须保存进 `.midora` Project。
系统不得依赖运行时重新推断。
### 10.22.4 引用处更新
修改 Event Instrument 生命周期设置后：
```text
所有引用该 Event Instrument 的 Logical Track 自动使用最新生命周期规则
```
这承接 Event Instrument 定义共享语义。
### 10.22.5 缓存失效
修改生命周期设置后，应使相关编译、播放、预览、渲染缓存失效。
具体缓存失效范围与策略由 第 12 章《编译系统与 Canonical Compiled Result》 / 第 13 章《播放与预览》 细化。
### 10.22.6 预览
Event Instrument 预览 / 试听应遵守该 Event Instrument 的生命周期策略。
预览触发 Note、Gate Length、默认 velocity、默认 pitch 等由 第 13 章《播放与预览》 / 第 17～20 章的 UI 与交互规格 细化。
### 10.22.7 播放、导出、渲染一致性
Logical Track 播放、整曲播放、MIDI 导出、音频渲染应使用同一生命周期语义。
生命周期策略直接影响 MIDI 导出的事件内容，包括：
```text
实例展开
Pre-Roll 后的实例 / 模板 origin
Note Off
Tail
Release
Loop
Reset
```
---
## 10.23 生命周期诊断与有效性
### 10.23.1 第 10 章《实例生命周期、Loop、Envelope 与重叠》 范围
第 10 章《实例生命周期、Loop、Envelope 与重叠》 只列出生命周期相关诊断来源。
最终错误码、严重等级和 UI 呈现由第 15 章《音频文件渲染》规定。
### 10.23.2 非法生命周期配置
以下情况会使 Event Instrument 无效：
```text
非法 Loop 区间
非法 Envelope 参数
Envelope 值越界
Envelope 阶段时长为负
断裂 Envelope 引用
非法生命周期策略组合
Pre-Roll Ticks 不在 0..Template Length 范围内
Loop / Envelope 在 Per-Note Instance Isolation 关闭时被实际启用
Loop 边界处非法 Note 跨越
其他相关章节规定的生命周期非法状态
```
### 10.23.3 保存非法中间状态
如果用户正在编辑 Event Instrument，Project 允许保存包含非法生命周期配置的 Event Instrument。
规则：
```text
该 Event Instrument 变为无效
被实际编译使用时导致编译失败
未实际使用时按诊断规则处理
```
### 10.23.4 编辑阶段阻止显然非法配置
虽然允许保存无效 Event Instrument，UI / 编辑层仍应尽量阻止显然非法配置进入数据。
例如：
```text
非法 Loop 区间
负时长
Envelope 值越界
```
应即时阻止或修正。
### 10.23.5 未引用 Event Instrument 的生命周期错误
未被任何 Logical Track 引用的 Event Instrument 存在生命周期配置错误时：
```text
不阻止整曲编译
应在诊断中显示为警告或类似等级
具体由 第 15 章《音频文件渲染》 决定
```
### 10.23.6 被实际使用 Event Instrument 的生命周期错误
被实际编译使用的 Event Instrument 存在生命周期配置错误时：
```text
编译失败
```
不自动降级为默认生命周期。
---
