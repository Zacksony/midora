# 第 11 章 Logical Track、Segment 与编曲语义

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义高层编曲对象、Event Instrument Usage 绑定、Segment 裁剪窗口、Logical Note、Logical Parameter Lane、Segment 操作、Mute/Solo 运行状态和局部预览入口。全局平铺顺序与共享 Usage 以第 24 章为准。

## 11.1 Logical Track 系统级定义
Logical Track 是用户编曲时间线上的高层轨道。
系统级规则：
```text
Logical Track 属于 Project
Logical Track 必须在 global Arrangement Track Order 中恰好出现一次
Logical Track 不属于 Conductor Track
Logical Track 不直接代表 MIDI Track
Logical Track 不直接代表 MIDI Channel
Logical Track 不直接代表 MIDI Port
Logical Track 通过 Event Instrument Usage 间接引用 Definition 并解释 Segment 内容
空壳 Logical Track 允许暂时未指定 Usage；有内容时必须绑定 Usage
```
Logical Track 负责承载：
```text
Segment
Track 显示名称
Track 显示颜色 / 颜色覆盖
Track 排序
可空的唯一 Event Instrument Usage stable ID
```
Logical Track 本身不直接持有 Note / Trigger。
Track Mute / Solo 不属于 Project 持久内容。它们是 第 13 章《播放与预览》 播放系统确认的临时播放监听状态，由运行期会话持有，不保存进 Project，不进入 Undo / Redo，不标记 Project 已修改。
初版中：
```text
Logical Note / Logical Parameter Lane 必须存在于 Segment 内
Track 级直接 Note 或直接参数 Lane 不在初版范围内
```
---
## 11.2 Logical Track 身份、名称与颜色
### 11.2.1 稳定 ID
每个 Logical Track 必须拥有内部稳定 ID。
稳定 ID 用于：
```text
内部引用
排序
撤销 / 重做
复制
删除恢复
诊断定位
播放 / 编译缓存关联
```
系统不得只依赖数组顺序或用户可见名称识别 Logical Track。
### 11.2.2 名称
Logical Track 名称允许重复。
创建 Logical Track 时：
```text
普通 New Logical Track 创建名为 `Logical Track` 的未绑定空壳；New Logical Track with Instrument 创建独立 Usage 并绑定所选 Definition
```
Track 名称不是内部身份。
### 11.2.3 颜色
Logical Track 默认继承绑定 Event Instrument 的颜色。
Logical Track 允许覆盖颜色。
颜色规则：
```text
Event Instrument 颜色可作为 Logical Track 默认主色
Logical Track 可设置自己的显示颜色覆盖
颜色不影响编译、播放、渲染、MIDI 导出语义
```
---
## 11.3 Logical Track 与 Event Instrument 绑定
Logical Track 通过一个可空 Usage ID 表达绑定；Usage 唯一引用 Event Instrument Definition。多个 Track 可以引用同一 Usage并共享状态，不同 Usage 即使引用同一 Definition 也相互独立。
### 11.3.1 创建时绑定
当用户使用 `New Logical Track with Instrument...` 或 `Add Logical Track Using This Instrument` 创建 Track：
```text
创建新的无名称独立 Usage并绑定所选 Event Instrument Definition
```
`New Logical Track` 可以创建未绑定空壳，但指定 Usage 前禁止创建/粘贴 Segment 内容。
### 11.3.2 改绑
当用户把已有内容的 Logical Track 改绑到另一个 Event Instrument：
```text
保留 Track 内所有 Segment / Logical Note / Logical Parameter Lane 数据
后续编译使用新 Event Instrument 解释这些内容
旧 Logical Parameter Lane 可能变为断裂 / 不适用
不自动清空内容
不自动按名称匹配新 Event Instrument 参数
```
### 11.3.3 跨 Event Instrument 改绑
Logical Track 可以 `Make Independent`、`Share Instrument State With...` 或更换 Definition。它们执行第 24.3、24.7 节的影响审查和原子 Usage 变更；保留全部 Track/Segment 内容与稳定 ID，失败时保持原 Usage。
---
## 11.4 Logical Track 创建、删除、复制、排序
### 11.4.1 空 Track
Logical Track 允许没有任何 Segment。
空 Logical Track 是合法状态。
### 11.4.2 删除
删除 Logical Track 时：
```text
删除该 Track 及其全部 Segment / Logical Note / Logical Parameter Lane 数据
删除操作进入全项目撤销 / 重做
删除操作使 Project 进入已修改状态
```
如果 Track 非空：
```text
删除前必须确认
```
如果 Track 为空：
```text
不需要确认，可直接删除
```
### 11.4.3 复制
复制 Logical Track 时：
```text
深拷贝 Track 内 Segment、Logical Note、Logical Parameter Lane、裁剪窗口与其他 Track 内容
新 Track 获得新的稳定 ID
Segment、Logical Note、Lane、点、曲线等对象生成新的稳定 ID
Event Instrument 定义不复制
```
普通 `Duplicate`：
```text
源 Track 已绑定时，创建引用同一 Event Instrument Definition 的新独立 Usage
源 Track 属于 Shared Usage block 时，将新 singleton Track 插入整个源 block 之后
源 Track 为 singleton 或未绑定空壳时，将副本插入源 Track 之后
未绑定空壳的副本仍未绑定，并继续满足无内容约束
```
显式 `Duplicate and Share State`：
```text
只对已绑定 Logical Track 可用
保留源 Usage，并把副本紧邻插入源 Track 之后、留在同一 Shared block 内
源 Usage 原为 singleton 时，该命令建立一个两成员 Shared block
```
两种命令都只产生一个 Project Undo，并原子创建/恢复 Track、全部内容、必要 Usage、global order 和 membership。普通空白 Paste 继续创建引用同一 Definition 的独立 Usage；Paste 到明确 Usage target 时加入该 Usage。完整规则见第 24.8 节。
复制后名称：
```text
系统可自动生成便于识别的名称
但 Logical Track 名称本身不强制唯一
```
### 11.4.4 排序
初版支持在混合 Logical/Pure MIDI 的 global Arrangement Track Order 中手动排序 Logical Track，并按第 24 章通过拖放改变 Usage。
排序规则：
```text
Track 顺序保存进 Project
排序进入全项目撤销 / 重做
排序使 Project 进入已修改状态
```
global Track order 决定可见顺序和同 Usage 跨 Track 同 tick 次序，但不作为资源抢占优先级。
Track 顺序可用于：
```text
UI 显示
诊断显示
确定性输出的辅助顺序
```
Track 顺序不应直接成为资源分配优先级策略。
---
## 11.5 Mute / Solo 播放状态
### 11.5.1 状态归属
初版支持 Track Mute / Solo，但二者均为临时播放监听状态。
规则：
```text
Mute 不属于 Project 内容。
Solo 不属于 Project 内容。
Mute / Solo 不保存进 Project。
Mute / Solo 不进入 Undo / Redo。
Mute / Solo 切换不标记 Project 已修改。
Mute / Solo 切换不属于编辑 Project。
关闭 Project 或打开新 Project 后，Mute / Solo 状态全部丢弃。
新建或打开 Project 后，所有 Track 与共享 Usage/Root group 默认 Mute = false，Solo = false。
```
因此，第 11 章《Logical Track、Segment 与编曲语义》 中 Logical Track 的持久数据不应包含 Mute / Solo 状态。
### 11.5.2 播放监听语义
Logical Track 与共享 Usage/Root block 可以拥有独立 Mute/Solo，且 group 开关不得改写成员 Track 开关。核心过滤与来源精确恢复按第 24.9 节执行；Mute 始终胜过同层 Solo。
具体播放中切换 Mute / Solo 时的状态恢复、活动声音清理、恢复事件过滤、Channel Unit 清理范围等由 第 13 章《播放与预览》定义。
### 11.5.3 与 Project 保存、撤销和诊断的关系
Mute / Solo 不属于 Project 内容，因此：
```text
切换 Mute / Solo 不使 Project 进入已修改状态。
切换 Mute / Solo 不进入 Undo / Redo。
保存 Project 不保存当前 Mute / Solo 状态。
重新打开 Project 后不恢复上次 Mute / Solo 状态。
```
Mute / Solo 不删除或隐藏 Project 数据。
全项目诊断仍应检查所有 Project 内容，包括当前被临时 Mute 过滤的 Track 内部数据。
### 11.5.4 与成品输出的边界
Mute / Solo 是实时监听状态，不是成品输出选择。
规则：
```text
Mute / Solo 不影响 MIDI 导出。
Mute / Solo 不影响音频文件渲染。
Mute / Solo 不影响保存后的项目内容。
MIDI 导出和音频文件渲染仍由导出 / 渲染设置中的显式 Track 选择决定。
```
因此：
```text
当前被 Mute 的 Track 仍可被用户显式选择参与 MIDI 导出或音频文件渲染。
当前未 Solo 的 Track 仍可被用户显式选择参与 MIDI 导出或音频文件渲染。
```
---
## 11.6 Segment 系统级定义
Segment 是 Logical Track 上一段有效时间范围容器。
系统级规则：
```text
Segment 属于 Logical Track
Segment 不持有名称
Segment 位于 Logical Track 时间线上
Segment 包含 Logical Note
Segment 包含 Logical Parameter Lane
Segment 保存完整内容与当前有效裁剪窗口
Segment 末尾形成 Reset 边界
Segment 有效起点是 Pre-Roll 后实例 origin 的硬左边界
Segment 不直接绑定 Event Instrument；它通过所在 Logical Track 的绑定被解释
```
Segment 不是 Pattern。
Segment 不独立存在于 Project 全局库中。
Segment 跨 Track 复制或移动时，内容由目标 Track 的 Event Instrument 绑定重新解释。
---
## 11.7 Segment 身份与时间范围
### 11.7.1 稳定 ID
每个 Segment 必须拥有内部稳定 ID。
稳定 ID 用于：
```text
复制
移动
分割
连接
撤销 / 重做
诊断定位
引用保持
```
### 11.7.2 时间合法性
Segment 的当前有效范围必须满足：
```text
start tick >= 0
length > 0
```
不允许：
```text
负 start tick
0 长度 Segment
负长度 Segment
```
### 11.7.3 重叠与相邻
同一 Logical Track 内多个 Segment：
```text
不允许重叠
允许相邻
相邻不自动连接
```
不同 Logical Track 的 Segment：
```text
允许重叠
引用不同 Event Instrument Usage 时运行状态彼此独立
引用同一 Usage 时按第 24.4 节共享 Channel 状态、Overlap 域和活动连通区间
```
移动或拉伸 Segment 导致同一 Track 内重叠时：
```text
应阻止该操作
```
### 11.7.4 Segment 坐标模型
Segment 采用：
```text
完整内容坐标 + 当前有效裁剪窗口
```
模型。
这意味着：
```text
Segment 保存完整内容
Segment 保存当前有效裁剪窗口
缩短 Segment 只改变裁剪窗口
缩短不物理删除内容
被裁剪内容仍保存在 Project 中
```
Segment 在主时间线上显示的 start / end：
```text
表示当前有效裁剪窗口的绝对 start / end
不表示隐藏内容完整范围
```
---
## 11.8 Segment 裁剪窗口与隐藏内容
### 11.8.1 基本原则
Segment 缩短后：
```text
Segment 原始内容完整保留
当前有效范围变小
有效范围外内容不参与当前编译输出
有效范围外内容在 Segment 编辑视图中仍可见
有效范围外内容应以裁剪区 / 非活动区视觉样式区分
保存文件保留完整内容与裁剪窗口
```
### 11.8.2 缩短区域不包含事件
如果被裁剪区域不包含任何事件或有效内容：
```text
正常缩短
不产生额外诊断
不产生额外编译行为
```
### 11.8.3 缩短区域仅包含非 Note 输出
如果被裁剪区域只包含非 Note 输出，例如：
```text
Logical Parameter 点 / 曲线
参数映射产生的 CC / Pitch Bend / RPN / NRPN 等非 Note 输出
生命周期 Release / Tail 中的非 Note 控制输出
```
则：
```text
这些输出在当前编译中被裁剪
原始数据不被删除
编辑视图中仍可见
```
### 11.8.4 后部缩短裁掉 Note End / Gate End
如果后部缩短导致某个已开始 Logical Note 的结束点被裁掉，但该 Note 起点仍在有效范围内：
```text
编译时在裁剪点视为该 Note 提前结束
等价于在裁剪点插入必要 Gate End / 实际 MIDI Note Off
随后执行必要 Reset
Segment 内原 Note 数据不被修改
```
### 11.8.5 前部缩短裁掉 Note Start
如果前部缩短导致某个 Logical Note 的起点被裁掉：
```text
该 Logical Note 在当前编译结果中完全不产生
即使该 Note 的一部分持续区间落在新的有效范围内，也不自动生成新的 Note On
```
系统不做：
```text
从中途进入 Note
自动续接 Note
自动在新 Segment Start 处生成 Note On
```

如果 Logical Note anchor 位于保留区，但其 Event Instrument `Pre-Roll Ticks` 使 Instance Origin 落在 Segment 有效起点之前：

```text
当前 Segment 编译产生 Error
不得 Clamp 到 Segment Start
不得丢弃前缀后继续生成
不得自动向左扩张或移动 Segment
不得跨前一个 Segment 的硬边界执行
```

这是源数据仍可保存、但该 Definition/Segment 使用组合不可正式消费的语义错误。用户必须显式向左扩张 Segment 的有效窗口、向右移动 Logical Note，或降低 Event Instrument 的 Pre-Roll Ticks。
### 11.8.6 多 Note 混合裁剪
如果裁剪区同时影响多个 Logical Note：
```text
逐个 Logical Note 独立判断
```
例如：
```text
Note A 起点在保留区，结束点在裁剪区：Note A 提前结束
Note B 起点在裁剪区：Note B 不产生
```
### 11.8.7 裁剪区与参数继承
位于裁剪区内的 Logical Parameter 点 / 曲线：
```text
数据保留
可见但非活动
不参与当前编译
不影响有效区内的参数状态继承
```
如果 Segment 前部被裁剪，裁剪区内曾有参数值，新的有效起点之后没有新参数值：
```text
不继承裁剪区最后值
使用 Logical Parameter defaultValue
```
---
## 11.9 Segment 扩张、移动与复制
### 11.9.1 前部重新扩张
如果 Segment 前部曾被缩短，之后用户向前扩张：
```text
优先重新显露此前被裁剪但仍保留的内容
扩张超过原保留内容范围时，新区域为空
```
### 11.9.2 后部重新扩张
如果 Segment 后部曾被缩短，之后用户向后扩张：
```text
优先重新显露此前被裁剪但仍保留的内容
扩张超过原保留内容范围时，新区域为空
```
### 11.9.3 整体移动
整体移动 Segment 时：
```text
完整内容、当前有效裁剪窗口、隐藏裁剪区一起移动
内部相对位置不变
```
Segment 移动到负 tick：
```text
应阻止
```
### 11.9.4 跨 Track 移动
Segment 允许跨 Logical Track 移动。
移动后：
```text
由目标 Track 的 Event Instrument 绑定解释 Segment 内容
```
如果目标 Track 绑定不同 Event Instrument：
```text
Logical Note 数据保留
Logical Parameter Lane 可能变为断裂 / 不适用
允许用户后续重绑定参数 Lane
```
### 11.9.5 复制
复制 Segment 时：
```text
深拷贝 Segment 完整内容
深拷贝当前有效裁剪窗口
深拷贝隐藏裁剪区数据
深拷贝 Logical Note
深拷贝 Logical Parameter Lane、点、曲线、控制点
生成新的稳定 ID
```
跨 Track 复制时：
```text
目标 Track 绑定同一个 Event Instrument：参数 Lane 正常复制并保持引用
目标 Track 绑定不同 Event Instrument：参数 Lane 数据保留为断裂 / 不适用
```
---
## 11.10 Segment 创建、删除、连接、分割
### 11.10.1 创建
新建 Segment 时：
```text
创建空 Segment
不自动创建 Logical Note
不自动创建 Logical Parameter Lane
```
如果用户通过“新建 Segment”按钮而非拖拽创建，默认长度为：
```text
1 小节
```
该 1 小节按 Segment 起点处的 Time Signature 与 Project TPQ 换算。
### 11.10.2 删除
删除 Segment 时：
```text
删除该 Segment 及其内部全部内容
删除操作进入全项目撤销 / 重做
删除操作使 Project 进入已修改状态
```
如果 Segment 非空：
```text
删除前必须确认
```
如果 Segment 为空：
```text
不需要确认
```
### 11.10.3 相邻 Segment
两个 Segment 相邻时：
```text
不自动连接
相邻只是相邻
中间仍存在 Segment 边界与 Reset 语义
```
### 11.10.4 手动连接相邻 Segment
同一 Track 上两个相邻 Segment 可以手动连接。
连接后：
```text
成为一个 Segment
移除中间 Reset 边界
内部内容绝对 tick 位置不变
两侧完整内容与裁剪区都保留
同一 Logical Parameter 的 Lane 合并成一条
不同参数 Lane 并存
同 tick 冲突点由右侧 Segment 点覆盖左侧点
```
### 11.10.5 手动连接非相邻 Segment
同一 Track 上两个非相邻 Segment 允许手动连接。
连接后：
```text
中间空隙被纳入新 Segment 的有效范围
连接结果是一个更大的 Segment
原两个 Segment 之间的 Reset 边界被移除
中间空隙不自动生成 Logical Note 或 Logical Parameter 数据
内部内容绝对 tick 位置不变
参数状态可在连接后的单一 Segment 内连续继承
```
### 11.10.6 分割
用户可以在 Segment 内某个 tick 分割 Segment。
分割后：
```text
形成左右两个 Segment
内部内容按相对时间 / 裁剪窗口拆分
原 Segment 中当前有效范围外的裁剪区内容按原始绝对位置归入对应新 Segment 的保存内容 / 裁剪区
```
如果分割点穿过一个 Logical Note：
```text
该 Note 归入左 Segment
左 Segment 编译时在分割点提前结束
右 Segment 不自动生成续接 Note
```
如果 Logical Note 完整起始于分割点右侧：
```text
归入右 Segment
保持其相对时间位置
```
如果 Logical Note 完整结束于分割点左侧：
```text
归入左 Segment
```
参数 Lane 分割规则：
```text
参数 Lane 按分割点拆成左右两部分
跨越分割点的曲线由编辑系统按语义拆分
计算原 Segment 在分割 tick 的每个 Logical Parameter 有效值
右 Segment 保留或生成必要的起点状态数据
跨越分割点的曲线需要保留右侧曲线形状和分割点有效值
分割完成后，左右 Segment 仍各自构成独立 Reset 边界
```
右 Segment 的起点状态是 Split 命令显式写入或保留的数据，不是编译时跨 Segment 隐式继承。目标是让参数状态及相关曲线的听感不因分割而变化，同时保持一般 Segment 边界不继承状态的规则；本条不改变跨分割点 Logical Note 的提前结束规则。
---
## 11.11 Logical Note 系统
### 11.11.1 定义
Logical Note 是 Segment 内触发 Event Instrument Instance 的高层 Note 对象。
Logical Note 不等同于 SubVoice 内部模板 Note。
Logical Note start 是 Logical Gate anchor；当 Event Instrument `Pre-Roll Ticks > 0` 时，它不等于模板/实例 origin。
Logical Note 至少包含：
```text
稳定 ID
local start tick
length
pitch
velocity
显式顺序 / UI 顺序
```
### 11.11.2 稳定 ID
Logical Note 必须拥有稳定 ID。
稳定 ID 用于：
```text
编辑
复制
移动
撤销 / 重做
诊断定位
编译错误定位
```
### 11.11.3 时间坐标
Logical Note 的时间位置保存为：
```text
Segment local tick
```
由 Segment 映射到 Project absolute tick。
Segment 移动时不需要改写所有 Note 坐标。
### 11.11.4 合法性
Logical Note 合法性规则：
```text
local start tick >= 0
length > 0
pitch 为 MIDI note 0–127
velocity 为 1–127
```
不允许：
```text
负 start tick
0 长度 Note
负长度 Note
pitch 超出 0–127
velocity = 0
velocity 超出 1–127
```
Logical Note 的 pitch 合法，不代表最终 SubVoice 输出 Note 一定合法。
Event Instrument / SubVoice 映射后产生的实际 MIDI Note 越界，仍按 第 8 章《SubVoice 与 MIDI 事件编辑》 / 第 9 章《曲线、Logical Parameter 与映射》 规则导致编译失败。
### 11.11.5 重叠
同一 Segment 内：
```text
允许同 pitch Logical Note 重叠
允许不同 pitch Logical Note 重叠
不额外诊断
```
不同 Logical Note 可以生成不同 Event Instrument Instance，不能用普通 MIDI Note 配对直觉限制。
### 11.11.6 与 Segment 有效范围的关系
Logical Note 允许跨出 Segment 当前有效裁剪窗口。
规则：
```text
数据允许保存
超出部分按裁剪规则不参与编译或提前结束
```
用户允许主动在 Segment 编辑器中把 Logical Note 画进当前非活动 / 裁剪区。
该内容：
```text
保存
当前不参与编译
UI 必须明确显示其处于非活动裁剪区
```
拖长 Logical Note 超出 Segment 有效范围时：
```text
允许拖长
超出部分进入裁剪区
当前不参与编译
UI 必须清楚显示超出部分非活动
```

Logical Note 的 Pre-Roll 前缀不是隐藏裁剪内容。只有当 `anchor - Pre-Roll Ticks` 仍位于同一 Segment 当前有效窗口内时，该 Note 才可参与正式编译；Segment 左边界不提供隐式前滚空间。
---
## 11.12 Logical Parameter 系统入口
### 11.12.1 Logical Parameter 编曲控制要求
Segment 不得被限制为仅编辑 Logical Note。系统要求：
```text
Segment / Logical Track 初版必须支持非 Note 编曲控制。
但这些控制不应是直接 MIDI CC / Pitch Bend / RPN / NRPN 编辑，
而应通过 Event Instrument 暴露的 Logical Parameters 间接控制 SubVoice 内的非 Note MIDI 事件。
```
这是 Midora 除 Event Instrument 外最核心的功能之一。
### 11.12.2 Logical Parameters 归属
Event Instrument 直接包含 Logical Parameters 集合。
不额外设置 Logical Parameter Group 层级。
系统级规则：
```text
Logical Parameter 属于 Event Instrument 定义
Logical Parameter 是 Event Instrument 暴露给 Logical Track / Segment 的外部控制接口
Logical Track / Segment 只能编辑当前绑定 Event Instrument 暴露的 Logical Parameter
Logical Parameter 不等同于 MIDI CC / Pitch Bend / RPN / NRPN 本身
Logical Parameter 通过 Mapping 影响一个或多个 SubVoice 的非 Note MIDI / 高级事件参数
```
### 11.12.3 名称与 ID
Logical Parameter 必须拥有稳定 ID。
Logical Parameter 名称：
```text
在单个 Event Instrument 内唯一
大小写不敏感
比较时去除首尾空白
不能为空
```
Segment 中的 Logical Parameter Lane 引用参数稳定 ID，不依赖名称。
重命名 Logical Parameter：
```text
不破坏 Segment Lane 绑定
```
### 11.12.4 删除与断裂引用
删除已被 Segment 使用的 Logical Parameter 后：
```text
Segment 中对应参数 Lane 变为断裂 / 不适用
数据保留
不参与编译
诊断提示
```
不自动删除 Lane。
不按名称自动匹配其他参数。
### 11.12.5 复制 Event Instrument
复制 Event Instrument 时：
```text
深拷贝 Logical Parameters 与 Mapping
生成新的稳定 ID
复制出的 Event Instrument 与原定义独立
```
---
## 11.13 Logical Parameter 输入类型
初版支持三种 Logical Parameter 输入类型：
```text
Integer
Double
Enum
```
每个 Logical Parameter 必须有：
```text
稳定 ID
名称
输入类型
合法输入范围
默认显示范围
defaultValue
```
### 11.13.1 Integer
Integer 参数：
```text
内部建议使用 int
合法输入范围自定义
默认显示范围自定义
允许点、线段、曲线
曲线插值得到非整数时，四舍五入到最近整数
```
### 11.13.2 Double
Double 参数：
```text
内部使用 Double 浮点数
合法输入范围自定义
默认显示范围自定义
常用于 0.0–1.0 等连续控制
允许点、线段、曲线
```
### 11.13.3 Enum
Enum 参数：
```text
内部为整数值，建议使用 ushort 或可兼容整数表示
Enum item 名称在单个 Enum 参数内唯一
名称比较大小写不敏感，去除首尾空白
默认从 0 开始隐式递增
允许启用显式整数值
显式模式下每个枚举项必须填写整数值
显式值允许不连续
显式值不允许重复
```
Enum 参数在 Segment 上：
```text
只允许离散阶梯变化
不允许连续曲线插值
```
### 11.13.4 defaultValue
每个 Logical Parameter 必须拥有 `defaultValue`。
规则：
```text
defaultValue 必须落在该参数合法输入范围内
当 Segment 有效区没有上一个参数值时，使用 defaultValue
```
---
## 11.14 Logical Parameter Lane
### 11.14.1 定义
Logical Parameter Lane 是 Segment 内某个 Logical Parameter 的时间变化数据。
同一 Segment 内，对同一个 Logical Parameter：
```text
最多一条 Lane
重复创建时进入同一 Lane
```
### 11.14.2 稳定 ID
Segment 内每条 Logical Parameter Lane 必须拥有稳定 ID。
Lane 内以下对象也必须拥有稳定 ID：
```text
参数点
曲线对象
曲线控制点
```
稳定 ID 用于：
```text
撤销 / 重做
复制
诊断
断裂引用修复
排序稳定
```
### 11.14.3 时间坐标
Logical Parameter 点 / 曲线位置使用：
```text
Segment local tick
```
### 11.14.4 显示来源
Segment 编辑器中可编辑的参数 Lane 列表来自：
```text
当前 Logical Track 绑定的 Event Instrument 的 Logical Parameters 集合
```
当绑定 Event Instrument 新增 Logical Parameter：
```text
已绑定该 Event Instrument 的 Track / Segment 可立即看到新参数 Lane 入口
但不自动创建 Lane 数据
```
### 11.14.5 Definition / Usage 损坏或未指定
无内容 Logical Track 可以没有 Usage；此时没有参数 Lane 创建入口。已有内容的 Track 必须拥有有效 Usage 与 Definition。Definition/Usage 使用 Damaged Placeholder 时，已有 Lane 数据以只读损坏状态保留并禁止正式编译；引用索引不可信时 Project 打开失败。
---
## 11.15 Logical Parameter Lane 状态继承
### 11.15.1 稀疏状态继承规则
Logical Parameter Lane 是状态型时间数据，不是孤立瞬时事件。
规则：
```text
某 tick 没有新值时，继承上一个有效值
没有上一个有效值时，使用 Logical Parameter defaultValue
```
`-` 表示该 tick 没有新值，不表示值为 0。
例如：
```text
Logical Parameter Lane:
[- - 5 - - 8 9 -]
```
若 defaultValue = 0，则有效值为：
```text
[0 0 5 5 5 8 9 9]
```
### 11.15.2 与 SubVoice 原始事件状态继承
SubVoice 内目标非 Note MIDI 事件值 `c` 也采用状态继承：
```text
某 tick 没有新事件时，继承上一个有效值
没有上一个值时，使用该 MIDI 参数的 Initial State Defaults / 系统默认值
```
具体默认表由 第 12 章《编译系统与 Canonical Compiled Result》 / 第 15 章《音频文件渲染》 细化。
### 11.15.3 典型合成示例
SubVoice 原始 Pitch Bend：
```text
[10 - - 20 - - 30 -]
```
其有效值为：
```text
[10 10 10 20 20 20 30 30]
```
Logical Parameter `PitchBend_AddValue`：
```text
[- - 5 - - 8 9 -]
```
若 defaultValue = 0，则有效值为：
```text
[0 0 5 5 5 8 9 9]
```
若 Mapping 为：
```text
y = c + x
```
最终期望结果为：
```text
[10 10 15 25 25 28 39 39]
```
不是：
```text
[10 10 15 20 20 28 39 30]
```
也不是把缺省 tick 当作 0 重新写入。
### 11.15.4 Segment 边界
Segment 边界清除参数状态。
相邻 Segment 之间：
```text
不继承参数状态
后一个 Segment 从 Logical Parameter defaultValue 开始
```
连接 Segment 后：
```text
连接结果成为一个 Segment
原中间 Reset 边界消失
参数状态可在连接后的单一 Segment 内连续继承
```
---
## 11.16 Logical Parameter Mapping
### 11.16.1 定义
Logical Parameter Mapping 是：
```text
一个 Logical Parameter 到一个 SubVoice 非 Note MIDI / 高级事件参数的映射线路
```
一个 Logical Parameter 可以映射到多个目标。
多个 Logical Parameter 也可以映射到同一个目标。
### 11.16.2 目标范围
初版 Logical Parameter Mapping 目标只允许：
```text
非 Note MIDI / 高级事件参数
```
例如：
```text
CC value
Pitch Bend value
Program value
Bank Select MSB / LSB
RPN / NRPN Data Entry
Pitch Bend Range semitone / cents
其他后续定义的非 Note 高级事件参数
```
初版不允许 Logical Parameter Mapping 直接映射到：
```text
Note number
Note velocity
Note Off velocity
```
### 11.16.3 映射到不存在的目标事件
允许 Logical Parameter Mapping 指向 SubVoice 中当前不存在的目标事件。
此时：
```text
c 使用该目标 MIDI 参数的默认值
Mapping 可生成该目标事件流
```
### 11.16.4 数值映射类型
对于数值目标，设：
```text
c = SubVoice 目标事件该 tick 的原始有效值
x = Logical Parameter 该 tick 的有效输入值
y = 最终输出值
```
初版支持：
```text
y = x
y = c + x
y = c * x
y = c - x
y = x - c
y = c / x
y = x / c
y = f(c, x, context)
```
### 11.16.5 Enum 映射
对于 Enum 参数：
```text
每个 Enum 值可映射为对应目标 MIDI 事件参数的具体值
```
Enum 内部整数值参与重绑定和转换，但用户需要理解 Enum 的语义可能不等同于普通数值。
### 11.16.6 除零策略
除零策略归属于每条 Logical Parameter Mapping。
默认除零策略：
```text
输出该目标 MIDI 事件参数的最大值
```
可选除零策略：
```text
输出该 MIDI 事件参数最大值
输出该 MIDI 事件参数默认值
输出 0
编译失败
```
“MIDI 事件参数最大值”指该目标参数合法范围上界，例如：
```text
CC value 最大值 = 127
Pitch Bend 最大值 = +8191
```
如果除零策略选择“输出 0”，但目标参数合法范围不包含 0：
```text
先输出 0
再按该 Mapping 的越界规则处理
```
### 11.16.7 越界规则
每条 Logical Parameter Mapping 必须设置输出值越界规则。
初版至少支持：
```text
Clamp 到目标 MIDI 参数合法范围
编译失败
```
快捷新增参数时，越界规则默认 Clamp。
### 11.16.8 多参数作用同一目标
多个 Logical Parameter Mapping 作用到同一 SubVoice、同一目标事件参数时：
```text
按 Event Instrument 内显式 Mapping 顺序依次计算
用户可手动排序 Mapping
前一条输出作为后一条的 c
```
如果后一条 Mapping 使用 Mapping Function Expression：
```text
其 value 与 context.CurrentValue 是前一条 Mapping 的输出结果；context.TargetOriginalValue 仍是本次目标的原始有效值
```
### 11.16.9 同一参数映射多个目标
同一个 Logical Parameter 映射到多个目标时：
```text
每条 Mapping 独立计算
Mapping 有显式顺序
不同目标之间顺序通常只用于确定性输出
```
### 11.16.10 Mapping Function Expression 映射
Logical Parameter 的受限表达式映射概念形式为：
```text
y = f(value, logicalParameterValue, context)
```
ABI v3 中 `value` / `context.CurrentValue` 是当前累计链值，`context.TargetOriginalValue` 是目标原始有效值，`context.LogicalParameterValue` 是当前 Logical Parameter 有效值；可访问的 Context 只能来自第 9.6～9.7 节的精确数值/枚举白名单，包括：
```text
ProjectTick
SegmentLocalTick
TemplateTick
TargetOriginalValue
LogicalParameterValue
CurrentEventKind
CurrentParameter
TriggerNote / TriggerVelocity（当场景允许每音符 Context 时）
```
具体签名、白名单、依赖推导和 Mapping Function 复用方式由第 9 章规定。Logical Parameter Mapping 引用 Event Instrument 内的 Mapping Function；不得另存一份自由 C# 方法体或使用不同执行器。
表达式无法通过 ABI v3 验证或绑定：
```text
Event Instrument 定义错误
被实际使用时编译失败
```
表达式求值失败或产生非法结果：
```text
立即中止当前播放 / 渲染 / 导出流程
保留位置
弹出错误信息
```
### 11.16.11 快捷新增逻辑参数
初版允许用户快捷新增最简单参数。
快捷新增时：
```text
创建一个 Logical Parameter
创建一条 Logical Parameter Mapping
输入类型 = Integer
合法输入范围默认等于目标事件值范围
默认显示范围默认等于目标事件值范围
defaultValue 等于目标事件默认值
映射类型 = y = x
越界规则默认 Clamp
除零策略按 Mapping 默认策略
```
---
## 11.17 Logical Parameter 与实例作用范围
### 11.17.1 Track / Segment 层状态
Logical Parameter 是 Track / Segment 层状态。
规则：
```text
它按时间影响该 Track / Segment 内由 Event Instrument 产生的相关 SubVoice 输出
不是每个 Logical Note 单独存一套参数曲线
```
### 11.17.2 重叠 Note 共享参数 Lane
同一 Segment 内多个重叠 Logical Note：
```text
共享同一参数 Lane
同一时间读取同一参数有效值
```
### 11.17.3 Per-Note Instance Isolation 开启
开启 Per-Note Instance Isolation 时：
```text
每个实例独占 Channel Group
同一 Track / Segment 的参数 Lane 仍按时间作用到这些实例对应 SubVoice 的目标事件
```
### 11.17.4 Per-Note Instance Isolation 关闭
关闭 Per-Note Instance Isolation 时：
```text
参数作用于该 Event Instrument Usage 活动连通区间的共享 Channel Group
```
同一 Usage 的其他成员 Logical Track 若在同一时刻使用该 Channel Group，也会观察到按正式同 tick 顺序合并后的 Channel-Wide 状态；不同 Usage 不受影响。这适合需要跨编曲线共享 Mod / Expression 等状态的持续乐器。
### 11.17.5 无活动实例时
Logical Parameter 在没有活动 Note / 实例时：
```text
不直接输出 MIDI 事件
但仍计算并维护 Segment 内参数有效状态
```
“不输出”不等于“不计算状态”。
如果参数点发生在所有实例结束之后：
```text
只更新 Segment 内参数状态
不立即输出 MIDI
之后若有新实例，读取该状态
```
### 11.17.6 Note On 时读取当前参数状态
如果 Instance Origin 前已有参数有效值，而该实际起点处没有新参数点：
```text
实例应从 Instance Origin 读取该参数当前有效值
用于初始输出 / 后续状态
```

对于 Pre-Roll 实例，后续每个模板/派生事件按其实际 absolute tick 读取参数状态；Logical Note anchor 之后才生效的值不得倒灌到前缀。
如果需要在同一 tick 的 Note On 前设置 Mod / Exp / Pitch Bend 等目标状态：
```text
编译器应在该 Note On 前插入对应参数映射输出事件
```
### 11.17.7 活动实例期间参数变化
如果参数点发生在活动实例期间：
```text
输出对应目标 MIDI 事件变化
作用于当前活动实例 / 共享 Channel Group
```
---
## 11.18 同 tick 系统级排序入口
本章只定义系统级语义排序入口，不定义最终 MIDI 字节级排序表。
### 11.18.1 参数映射输出与 Note On
同一 tick 同时存在：
```text
Logical Parameter 有效状态变化
Logical Note start
```
编译输出应保证：
```text
参数映射生成的目标非 Note 事件先于 Note On 输出
```
### 11.18.2 参数映射输出与 Note Off
同一 tick 同时存在：
```text
Logical Parameter 变化
Logical Note end / Gate End
```
普通非 Release 场景下：
```text
参数映射输出先于实际 Note Off
```
使该 tick 的释放前状态可生效。
### 11.18.3 参数映射与 Initial State Defaults
实例开始 tick，如果 Initial State Defaults 与参数映射都输出同一目标：
```text
Initial State 先建立基础状态
参数映射后应用
参数映射可覆盖 / 调制 Initial State
```
### 11.18.4 参数映射与 SubVoice 原始事件
同一 tick，SubVoice 原始事件与逻辑参数映射输出同一目标时：
```text
先解析 SubVoice 原始有效值 c
再按 Mapping 顺序计算最终 y
最终 MIDI 输出只保留合成后的 y
```
不应先输出原始事件再输出 y 形成不必要重复事件。
### 11.18.5 同 tick 多个参数点
同一 Segment、同一 Logical Parameter、同一 tick：
```text
最终只保留一个值
后者替换前者
```
---
## 11.19 Logical Parameter 重绑定与修复
### 11.19.1 Track 改绑后的断裂数据
Logical Track 改绑到新 Event Instrument 后：
```text
旧参数 Lane 数据保留
旧 Lane 变为断裂 / 不适用
不按名称自动匹配
不自动删除
```
这些数据本质上是：
```text
time-value 点集 / 曲线数据
```
不应称为 `y-x` 点集，以避免与映射公式中的 `x` 混淆。
### 11.19.2 允许用户重新绑定
断裂后，允许用户重新选择新 Event Instrument 上的其他 Logical Parameter。
当新 Logical Parameter 的类型、范围等与现有数据不匹配时，系统询问用户选择：
```text
Clamp
丢弃范围外的值
```
如果新参数类型是 Enum，也按整数值处理，但必须警告用户：
```text
Enum 只是整数值层面的兼容
逻辑含义可能已经不匹配
重绑定后可能产生非预期结果
```
### 11.19.3 Clamp 规则
Enum Clamp：
```text
Clamp 到新 Enum 合法整数值范围后，再匹配最近的已定义 Enum 值
如果距离相等，选较小值
```
显式 Enum 值允许不连续。
### 11.19.4 丢弃范围外值
选择“丢弃范围外的值”时：
```text
删除不合法点
合法点保留
如果曲线控制点被删除，曲线形状相应改变
```
### 11.19.5 类型转换
Double 转 Integer：
```text
按目标 Integer 的取整规则转换
再应用 Clamp / 丢弃规则
```
Integer / Double 转 Enum：
```text
先得到整数值
再按 Enum 已定义值匹配
不匹配时按 Clamp / 丢弃规则处理
```
Enum 转 Integer / Double：
```text
使用 Enum 内部整数值作为旧值
再按目标参数规则处理
```
### 11.19.6 Definition 自身变化的全 Project 原子迁移
当同一 Logical Parameter Definition 的类型、合法范围或 Enum 结构变化会改变既有值的合法性或数值身份时，以上转换规则同样适用于全 Project 所有引用该稳定 Parameter ID 的 Lane。该操作必须一次性冻结完整目标 Definition 和有序 Enum item 集合，并由调用方显式选择 `Clamp` 或“丢弃不合法值”；目标 Enum 还必须确认语义警告。

统一规则：
```text
Double 转 Integer / Enum 的中点固定 Away From Zero
Enum Clamp 等距时选较小已定义值
目标 Enum 的所有保留点使用 Step
保留点与保留 Enum item 的稳定 ID 不变
新增 Enum item 在首次提交时分配新 ID
Definition、Enum items 和全部引用 Lane 形成一个撤销 / 重做项
失败时不得留下部分迁移结果
```
---
## 11.20 Logical Parameter 与 Segment 操作
### 11.20.1 复制 Segment
复制 Segment 时：
```text
深拷贝全部参数 Lane、点、曲线、裁剪区数据
生成新稳定 ID
引用同一 Event Instrument 参数 ID
```
### 11.20.2 跨 Track 复制
目标 Track 绑定同一个 Event Instrument：
```text
参数 Lane 正常复制并保持引用
```
目标 Track 绑定不同 Event Instrument：
```text
参数 Lane 数据保留为断裂 / 不适用
允许用户后续重绑定到目标 Event Instrument 的参数
```
### 11.20.3 移动到不同 Event Instrument 的 Track
Segment 移动到不同 Event Instrument 的 Track：
```text
参数 Lane 保留为断裂 / 不适用
允许用户后续重绑定
```
### 11.20.4 连接 Segment
连接 Segment 时：
```text
同一 Logical Parameter 的 Lane 合并成一条
不同参数 Lane 并存
冲突点按右侧 Segment 点覆盖同 tick 左侧点
```
### 11.20.5 分割 Segment
分割 Segment 时：
```text
参数 Lane 按分割点拆成左右两部分
跨越分割点的曲线由编辑系统按语义拆分
右侧保留或生成分割 tick 所需的起点状态数据
分割前后的有效参数值和右侧曲线形状保持一致
```
---
## 11.21 Project End Marker 与 Segment
如果 Project End Marker 早于某些 Segment 或 Logical Note：
```text
内容保留
Project End Marker 不改变 Segment 本体
Project End Marker 不自动裁剪 Segment
默认导出 / 音频文件渲染范围按 End Marker 截断
诊断提示 End Marker 后存在内容
```
如果 End Marker 截断仍在发声的实例：
```text
操作范围内编译 / 渲染 / 导出应插入必要 Note Off 与 Reset
具体排序由 第 12 章《编译系统与 Canonical Compiled Result》 / 第 14 章《MIDI 导出》 细化
```
处于临时 Mute / Solo 监听过滤状态的 Track 中，位于 End Marker 后的内容：
```text
仍可作为诊断提示来源
因为内容实际存在
```
Mute / Solo 是临时监听状态，不保存进 Project，不影响 End Marker 后内容判断。
---
## 11.22 Segment 预览与局部播放入口
### 11.22.1 Segment 预览范围
Segment 预览默认只播放：
```text
当前 Segment 的有效裁剪窗口范围
```
不播放完整隐藏裁剪区。
### 11.22.2 Segment 预览状态
Segment 预览应像从该 Segment 起点开始编译一样处理：
```text
Logical Parameter defaultValue
Initial State Defaults
Segment 起点 Reset 边界语义
```
默认只预览当前 Segment / 当前 Track。
其他 Track 默认不播放。
### 11.22.3 从 Segment 中间开始预览
如果用户从 Segment 有效范围中间开始预览：
```text
播放系统必须解析该点的 Logical Parameter 有效状态
解析必要 Initial State
解析活动实例上下文
```
具体算法由第 13 章《播放与预览》规定。

该入口继续服从范围冷启动：若某实例的 Pre-Roll origin 早于预览起点，则不补发其范围前 Note On，也不为重建 sample 相位执行隐藏音频预滚。
---
## 11.23 诊断入口
### 11.23.1 Logical Track Usage / Definition 错误
有内容 Logical Track 无 Usage、Usage/Definition 引用缺失或 kind 错误属于结构 Error，打开或结构编辑必须失败；无内容且无 Usage 的合法空壳不产生诊断。
### 11.23.2 临时 Mute / Solo 过滤状态下的 Track 内部错误
处于临时 Mute / Solo 监听过滤状态的 Track 内部错误：
```text
仍显示在全项目诊断中
Mute / Solo 不隐藏项目数据错误
```
### 11.23.3 裁剪区隐藏非法数据
被 Segment 裁剪区隐藏的非法 Logical Note / Logical Parameter 数据：
```text
仍诊断
因为数据保存在项目中
```

Logical Note anchor 虽在有效窗口内、但 `anchor - Pre-Roll Ticks` 早于该 Segment 有效起点时，不属于“隐藏内容不参与”的普通裁剪情况；它必须产生可定位到 Logical Note、Segment 与 Event Instrument Definition 的编译 Error。
### 11.23.4 断裂参数 Lane
断裂参数 Lane：
```text
诊断等级：警告
该 Lane 不参与编译
Project 可保存
```
### 11.23.5 参数 Lane 值超出范围
正常编辑状态下不允许产生超范围参数值。
如果打开旧项目或重绑定后发现超范围值：
```text
诊断为警告
要求修复或按规则转换
```
### 11.23.6 Mapping 目标断裂或非法
Logical Parameter Mapping 目标 SubVoice 不存在：
```text
Event Instrument 定义无效
被实际使用时编译失败
未使用时按未引用错误规则诊断
```
Logical Parameter Mapping 目标事件参数非法：
```text
Event Instrument 定义无效
被实际使用时编译失败
```
### 11.23.7 Mapping Function Expression 错误
Logical Parameter Mapping 引用的表达式无法通过 ABI v3 验证或绑定：
```text
Event Instrument 定义错误
被实际使用时编译失败
```
表达式求值失败或产生 NaN / Infinity / 非法结果：
```text
立即中止当前播放 / 渲染 / 导出流程
保留位置
弹出错误信息
```
---
## 11.24 与编译、播放、导出、渲染的边界
### 11.24.1 编译
本章定义高层语义。
具体编译系统应负责：
```text
Segment 有效窗口裁剪
Logical Note 触发 Event Instrument Instance
Pre-Roll 后 Instance Origin 计算与 Segment 左边界验证
Logical Parameter 有效状态解析
Logical Parameter 与 SubVoice 原始事件合成
同 tick 排序
必要 Note Off 与 Reset 插入
Project End Marker 范围裁剪
诊断定位
缓存失效
```
由 第 12 章《编译系统与 Canonical Compiled Result》 继续细化。
### 11.24.2 播放
播放系统应支持：
```text
Mute / Solo 临时监听过滤
Segment 预览
从 Segment 中间开始预览
随机位置播放时参数状态和实例上下文恢复
```
其中 Mute / Solo 的运行期状态、播放中切换、清理与恢复语义由第 13 章《播放与预览》规定。
由 第 13 章《播放与预览》 继续细化。
### 11.24.3 MIDI 导出
MIDI 导出不应被当前 Mute / Solo 状态隐式影响。
导出哪些 Track / 范围应由 MIDI 导出设置显式决定。
由 第 14 章《MIDI 导出》 继续细化。
### 11.24.4 音频文件渲染
音频文件渲染不应被当前 Mute / Solo 状态隐式影响。
渲染哪些 Track / 范围应由音频渲染设置显式决定。
实时播放中的“实时渲染”则遵守 Mute / Solo。
音频文件渲染系统由 第 13 章《播放与预览》 / 第 14 章《MIDI 导出》 或实现设计确定。
---
## 11.25 与 Pure MIDI Track / MIDI Segment 的边界

本章未加限定词的 `Track`、`Segment`、`Note` 与 Parameter Lane 均指 Logical Track 路径中的 Logical Track、Logical Segment、Logical Note 与 Logical Parameter Lane。

第 23 章定义的 Pure MIDI Track 使用独立的 MIDI Segment 源模型。MIDI Segment 必须复用本章以下通用编曲行为：

```text
稳定 ID、显示顺序与颜色
[startTick, endTick) 时间范围
同一所属 Track 内禁止 Segment 重叠、允许相邻
Content Window、隐藏内容、前后裁剪、恢复扩张
移动、复制、删除、连接、分割
Segment End 和消费者范围结束的硬边界
Mute / Solo 的运行时归属
撤销 / 重做、诊断定位与缓存失效的通用规则
```

但两类 Segment 的内容语义不得混淆：

```text
Logical Segment 包含 Logical Note 与 Logical Parameter Lane，并通过 Track 绑定的 Event Instrument 展开。
MIDI Segment 包含 Direct MIDI Note 与 Direct MIDI Event，不绑定 Event Instrument，也不经过 Mapping / Lifecycle 展开。
跨类型 Track 的 Segment 移动、复制或粘贴必须经过第 20.6.14 节的统一显式转换；完整保留共同 Note 字段与隐藏音符，丢失非共同数据前必须一次汇总确认。
```

MIDI Segment 的 Root 生命周期、跨 Pure MIDI Track 合并、直接事件排序、SMF 导入/导出及缓存归属以第 23 章为准；本章不得被解释为允许消费者把 Pure MIDI Track 强制转换成 Logical Track。
---
