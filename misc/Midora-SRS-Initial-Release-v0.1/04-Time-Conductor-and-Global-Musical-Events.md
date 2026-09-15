# 第 4 章 时间、Conductor Track 与全局音乐事件

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 tick、TPQ、Tempo Map、拍号、调号、Marker、Project End Marker 及其对时间显示、编译、播放和输出范围的语义。

## 4.1 时间系统基础规则
### 4.1.1 项目时间基准
Midora 项目内部应以 `tick` 作为主要离散时间位置单位。
所有需要在项目时间线上定位的内容，至少应能够映射到 tick 位置，包括：
```text
Conductor Track 全局事件
Logical Track 中的 Segment
Segment 内的音符 / 触发器
Event Instrument Instance 编译后的事件位置
Reset 事件位置
导出 MIDI 事件位置
播放与渲染调度位置
```
### 4.1.2 TPQ
TPQ 表示每个四分音符包含多少 tick。
默认：
```text
TPQ = 192
```
初版合法范围固定为：
```text
1..32767
```
该上限与 SMF TPQ division 的 15-bit 正整数表示一致；开发期 v1 中不接受更高 TPQ。
初版允许用户在创建 Project 时设置 TPQ。
创建 Project 后，TPQ 禁止修改。
这意味着：
```text
TPQ 是项目创建时确定的项目级时间精度
TPQ 影响四分音符位置与 tick 位置之间的换算
项目一旦创建，已有内容不需要因为 TPQ 修改而重算
初版不提供“修改 TPQ 并重映射现有内容”的功能
```
如果未来支持创建后修改 TPQ，必须另行设计现有内容重算、坐标显示、导出一致性和迁移策略。
### 4.1.3 四分音符与 tick 的关系
四分音符位置到 tick 的换算遵循：
```text
ticks = quarterNotePosition × TPQ
```
BPM 不参与该换算。
这意味着：
```text
改变 Tempo 不应改变任何音符、Segment 或事件在 tick 时间线上的位置
改变 Tempo 只改变播放、预览和音频渲染时对应的实际秒数
```
### 4.1.4 tick 与秒的关系
tick 到秒的换算由当前 Tempo Map 决定。
系统必须支持同一项目中存在多个 Tempo 事件，以形成变速。
中途播放、预览和音频渲染必须基于当前 tick 位置之前最近有效的 Tempo 状态计算秒时间。

对于 `originTick <= targetTick`，系统必须按 Tempo 事件的生效区间，对 `[originTick, targetTick)` 的每一段执行：
```text
segmentSeconds = segmentTickCount × 60 / (BPM × TPQ)
durationSeconds = 所有 segmentSeconds 的 decimal 累加值
```
Tempo Map 的内部索引和查找结构属于实现设计，但不得在 Tempo 段边界把时间或 sample 提前取整。

正式 tick→sample frame 映射为：
```text
sampleFrame = RoundAwayFromZero(durationSeconds × sampleRate)
```
只允许在完整区间积分并乘采样率后执行这一次整数取整。不得分别取整 Tempo 段，不得先取整两个绝对 sample 位置再相减。`originTick` 必须精确映射到 sample frame 0。实时播放、预览和音频文件渲染必须共用该语义；若 decimal 运算或最终整数结果溢出可安全表示范围，当前任务明确失败。
---
## 4.2 Conductor Track 的系统级定义
### 4.2.1 固定存在
每个 Project 必须有且只有一个 `Conductor Track`。
Conductor Track：
```text
固定存在
不可删除
不可静音
不可禁用
不绑定 Event Instrument
不属于 Logical Track 集合
不参与 Channel Unit 分配
不产生 Event Instrument Instance
不受 Segment 边界裁剪
不受事件乐器生命周期策略影响
```
不允许多个 Conductor Track。
不允许 Logical Track 局部覆盖 Tempo、Time Signature 或 Key Signature。
### 4.2.2 职责
Conductor Track 管理对整个 Project 生效的全局音乐事件。
初版支持以下 Conductor Track 事件：
```text
Tempo
Time Signature
Key Signature
Marker
Project End Marker
```
初版不支持以下 Conductor Track 可编辑事件：
```text
Text
Copyright
Cue Point
Lyric
复杂文本类 Meta Event
用户自定义全局 Meta Event
```
说明：
- Marker 是初版额外支持的全局定位事件。
- Project End Marker 是项目结束位置标记，不等同于普通 Marker。
- Track Name、Port Name、Readme 等导出相关信息由 MIDI 导出章节处理，不在本章作为 Conductor Track 可编辑事件展开。
### 4.2.3 默认内容
新 Project 的 Conductor Track 默认必须包含：
```text
Tick 0: Tempo = 120 BPM
Tick 0: Time Signature = 4/4
```
新 Project 默认不指定 Key Signature。
默认不写显式 Key Signature。
新 Project 默认不包含普通 Marker。
新 Project 默认不强制包含 Project End Marker。
### 4.2.4 全局作用域
Conductor Track 中的全局音乐事件对整个 Project 生效。
它们影响：
```text
时间轴显示
小节 / 拍号网格
Marker 列表与定位
项目结束位置判定
播放位置换算
音频渲染时长计算
MIDI 导出中的全局 Meta Event
中途播放时全局状态解析
```
它们不应直接影响：
```text
Note 音高
Event Instrument 的根音
Pitch Bend 映射
Channel Unit 分配
SoundFont Program 选择
```
除非未来另有明确需求。
---
## 4.3 Tempo 事件需求
### 4.3.1 Tempo 的语义
Tempo 表示每分钟多少个四分音符，用户侧显示为 BPM。
Tempo 用于：
```text
tick / quarter note 到 seconds 的换算
播放速度
音频渲染时长
时间轴秒时间显示
导出 MIDI 中的 Tempo 信息
```
Tempo 不用于：
```text
改变 tick 位置
改变事件乐器内部四分音符位置
改变 Note 音高
改变事件值映射
```
### 4.3.2 变速
Conductor Track 应允许在不同 tick 插入多个 Tempo 事件。
从某个 Tempo 事件所在 tick 开始，直到下一个 Tempo 事件之前，该 Tempo 生效。
初版只支持离散 Tempo 事件。
初版不支持：
```text
线性 Tempo Ramp
曲线 Tempo Automation
连续 Tempo 曲线
```
如果未来支持 Tempo Ramp 或曲线变速，必须另行定义插值、离散化、导出等价性、编辑器显示与缓存失效规则。
### 4.3.3 Tempo 取值
Tempo 使用 BPM 作为用户可见单位。
初版 Tempo 取值规则：
```text
BPM 必须大于 0
BPM 允许小数
BPM 不限制到传统音乐经验范围
BPM 不限制到 20–300 或 1–999 等固定人工范围
BPM 不因为极端偏低或偏高而产生警告
BPM 必须能在 Midora 支持的 MIDI 1.0 导出语义下得到有效表示
```
说明：
- “不作硬限制”在本章中解释为：不设置传统音乐范围限制，不设置经验性上限或下限，不对极端值给出警告。
- 但 `BPM <= 0` 不是有效 Tempo。
- 如果某个 BPM 无法转换为 Midora 支持的 MIDI 1.0 Tempo 表示，则不能作为有效导出数据。
- 具体 MIDI Tempo 表示边界、舍入规则和精度处理推迟到 第 14 章《MIDI 导出》 / 实现设计细化。
### 4.3.4 Tempo 显示范围
Tempo 编辑器或 BPM 曲线视图必须允许用户设定显示用的 Y 轴范围。
该 Y 轴范围属于显示和编辑辅助，不改变 Tempo 数据本身的合法范围。
Y 轴范围至少应支持：
```text
移动显示范围
缩放显示范围
适应当前可见 Tempo 数据
```
具体 UI 控件、鼠标交互、滚轮缩放、输入框格式由 第 17～20 章的 UI 与交互规格 或实现设计阶段细化。
---
## 4.4 Time Signature 事件需求
### 4.4.1 Time Signature 的语义
Time Signature 表示项目从某个 tick 开始使用的全局拍号。
它影响：
```text
小节线位置
拍网格显示
小节 / 拍 / tick 坐标换算
导出 MIDI 中的 Time Signature 信息
中途播放时当前全局拍号状态
```
它不影响：
```text
tick 到四分音符的基础换算
Tempo 的 BPM 定义
Note 实际 tick 位置
Segment 实际 tick 边界
事件乐器内部事件位置
```
### 4.4.2 变拍
Conductor Track 应允许在不同 tick 插入多个 Time Signature 事件，以实现变拍。
从某个 Time Signature 事件所在 tick 开始，直到下一个 Time Signature 事件之前，该拍号生效。
### 4.4.3 拍号取值
Time Signature 必须包含：
```text
分子 Numerator
分母 Denominator
```
初版允许范围：
```text
Numerator: 1–99
Denominator: 1, 2, 4, 8, 16, 32, 64
```
此外，每个 Time Signature 与当前 Project TPQ 必须满足：
```text
4 × TPQ % Denominator == 0
```
因此一个分母拍始终包含正整数个 Project tick，`Bar:Beat:Tick`、自然拍网格和 Snap 可以保持整数、唯一、可逆且无累计漂移。该约束是尚未冻结的开发期 v1 直接修订；不为不兼容组合创建迁移或分数 tick 表示。
不允许：
```text
Numerator <= 0
不在允许集合内的 Denominator
与 Project TPQ 不满足上述整除约束的 Denominator
```
### 4.4.4 Additive Meter
初版不支持 additive meter 显示。
例如，初版不支持将 `7/8` 显示或编辑为：
```text
3+2+2/8
```
初版只支持普通分子 / 分母形式。
additive meter、拍内分组、重音网格显示等内容可作为未来 UI 和编辑器增强功能。
---
## 4.5 Key Signature 事件需求
### 4.5.1 Key Signature 的语义
Key Signature 表示从某个 tick 开始生效的全局调号信息。
初版中，Key Signature 只作为：
```text
全局音乐元数据
时间轴 / Conductor Track 显示信息
MIDI 导出 Meta Event 信息
给用户阅读和组织工程的信息
```
Key Signature 不应自动改变：
```text
已有 Note 音高
新输入 Note 音高
Event Instrument 根音
音高映射结果
Pitch Bend 映射结果
Scale Quantize
转调行为
```
### 4.5.2 默认状态
新 Project 默认 Key Signature 为“不指定”。
默认不写显式 Key Signature。
系统不应默认项目为 C major 或 A minor。
### 4.5.3 编辑范围
初版开放 Key Signature 编辑。
初版至少应支持标准 Major / Minor 调号。
初版不要求支持：
```text
复杂调式
非十二平均律调号
用户自由文本调号
临时调性标签
Key Signature 对输入或映射的自动影响
```
Key Signature 开放编辑的目的，是帮助用户形成更良好的工程组织习惯，而不是参与 Midora 的自动音高语义。
---
## 4.6 Marker 需求
### 4.6.1 Marker 的语义
Marker 是 Conductor Track 中的全局定位事件。
Marker 用于帮助用户标记项目结构位置，例如：
```text
Intro
Verse
Drop
Break
Outro
```
Marker 不产生声音。
Marker 不参与 Event Instrument Instance 展开。
Marker 不参与 Channel Unit 分配。
Marker 不参与 Segment 裁剪。
### 4.6.2 Marker 初版范围
初版支持普通 Marker。
初版 Marker 至少应包含：
```text
tick 位置
用户可见名称 / 文本
```
具体字段、长度限制、文本格式、内部 ID、序列化结构由实现设计阶段细化。
初版不支持：
```text
Lyric
Cue Point
通用 Text Event 编辑
Copyright Event 编辑
复杂章节层级
嵌套 Marker
Marker 颜色规则
```
### 4.6.3 Marker 列表与定位
初版 UI 需要支持 Marker 列表和定位能力。
系统级要求：
```text
用户应能看到当前项目的 Marker 列表
用户应能通过 Marker 快速定位到对应 tick
Marker 列表应与 Conductor Track 中的 Marker 数据一致
```
具体 UI 布局、排序方式、搜索、快捷键、面板位置和交互细节由第 17～20 章规定。
---
## 4.7 Project End Marker 需求
### 4.7.1 Project End Marker 的语义
Project End Marker 是可选的项目结束位置标记。
当 Project End Marker 存在时，系统使用它作为项目的显式结束位置。
当 Project End Marker 不存在时，系统由项目内容自然决定项目结束位置。
### 4.7.2 数量限制
一个 Project 最多只能有一个 Project End Marker。
不允许多个 Project End Marker 同时存在。
### 4.7.3 与普通 Marker 的区别
Project End Marker 不等同于普通 Marker。
区别：
```text
普通 Marker 用于结构定位
Project End Marker 用于项目结束位置判定
普通 Marker 可以有多个
Project End Marker 最多一个
普通 Marker 不决定项目长度
Project End Marker 存在时决定项目显式结束位置
```
### 4.7.4 与自然项目长度的关系
如果 Project End Marker 不存在，项目自然结束位置可由以下内容共同决定：
```text
Logical Track / Segment 内容
Event Instrument Instance 编译结果
尾巴与 Reset
Conductor Track 中影响导出或定位的全局事件
其他相关章节规定的项目内容
```
如果 Project End Marker 存在，则系统以其 tick 位置作为显式项目结束位置。
如果 Project End Marker 早于已有内容，项目内容仍然保留，不因 End Marker 自动删除、销毁或强制改写。
在这种情况下，Project End Marker 作为导出 / 渲染等操作的默认项目范围依据；End Marker 之后仍存在的内容应通过诊断信息或警告提示用户。
具体哪些操作默认使用 End Marker 范围、是否提供显式覆盖范围、End Marker 之后内容在特定操作中是否被排除，由播放、渲染、导出和诊断章节继续细化。
### 4.7.5 与导出、播放、渲染的关系
Project End Marker 直接影响音频渲染和 MIDI 导出等操作的默认项目范围。
播放与预览系统应能读取 Project End Marker 作为范围信息，但是否默认在 End Marker 处停止、是否允许用户继续播放 End Marker 之后的内容，由 第 13 章《播放与预览》 继续细化。
但以下细节推迟到相关专项章节：
```text
End Marker 是否裁剪其后的 MIDI 事件
End Marker 之前尚未结束的实例如何处理
End Marker 是否触发全局 Reset
End Marker 是否导出为普通 MIDI Marker
End Marker 早于现有内容时具体诊断等级为信息还是警告
End Marker 范围是否可被具体导出 / 渲染操作显式覆盖
```
这些内容分别由 第 12 章《编译系统与 Canonical Compiled Result》、第 13 章《播放与预览》、第 14 章《MIDI 导出》、第 15 章《音频文件渲染》 和实现设计阶段细化。
---
## 4.8 Conductor Track 编辑系统级规则
### 4.8.1 Tick 0 初始状态
Project 必须始终能在 Tick 0 得到有效的初始 Tempo 与 Time Signature。
Tick 0 的 Tempo 和 Time Signature 事件不可删除，只能修改值。
这意味着：
```text
Tick 0 必须有显式 Tempo
Tick 0 必须有显式 Time Signature
不存在“看不见但生效”的隐式 Tempo
不存在“看不见但生效”的隐式 Time Signature
```
### 4.8.2 同 tick 冲突
同一 tick 的 Tempo、Time Signature 和 Key Signature 各自只允许一个；普通 Marker 是例外，可以存在多个。
规则：
```text
同一 tick 只能有一个 Tempo
同一 tick 只能有一个 Time Signature
同一 tick 只能有一个 Key Signature
同一 tick 可以有零个或多个普通 Marker
同一 Project 最多只有一个 Project End Marker
```
普通 Marker 不按 tick 或名称去重。名称为空、名称重复、tick 相同的 Marker 仍是具有不同稳定 ID 的独立对象。Canonical Compiled Result 中同 tick 普通 Marker 按稳定 ID 升序排列，不得依赖源集合遍历顺序。
对于 Tempo、Time Signature、Key Signature：
```text
UI 层应直接替换旧事件
同一 tick 最终只保留一个同类型事件
```
多个不同类型全局事件可以处于同一 tick。
例如，以下情况允许：
```text
Tick 0: Tempo = 120 BPM
Tick 0: Time Signature = 4/4
Tick 0: Marker = Intro
```
### 4.8.3 负时间
Conductor Track 不允许在负 tick 插入事件。
Project 时间线起点为 Tick 0。
### 4.8.4 与撤销 / 重做的关系
Conductor Track 编辑属于项目可撤销编辑行为。
以下行为应进入全项目统一撤销 / 重做框架：
```text
新增全局事件
删除全局事件
移动全局事件
修改全局事件值
新增、删除、移动、修改 Marker
新增、删除、移动 Project End Marker
批量移动或删除全局事件
```
具体撤销命令结构属于实现设计阶段。
---
## 4.9 小节 / 拍 / tick 坐标
### 4.9.1 坐标显示
Midora UI 需要能够把 tick 位置显示为：
```text
小节:拍:tick
```
具体显示格式由 UI 章节细化。
### 4.9.2 拍号变化影响
Time Signature 变化会影响后续小节线与拍网格显示。
它不应自动改变已有事件的绝对 tick 位置。
因此，当用户修改前方 Time Signature 时，后方内容的：
```text
绝对 tick 位置不变
实际播放秒时间不变，除非 Tempo 也变化
小节:拍:tick 显示坐标可能变化
网格线位置可能变化
```
### 4.9.3 不做拍号驱动重排
初版修改 Time Signature 时，不提供自动重排后方内容的行为。
即：
```text
修改拍号不重排后方内容
所有内容保持绝对 tick 不变
拍号不应影响音符位置
```
如果未来提供“保持小节:拍位置不变并重算 tick”的高级编辑功能，必须作为显式批量编辑命令设计，不应作为拍号修改的默认行为。

### 4.9.4 拍号变化处的小节边界
每个 Time Signature 变化 tick 都立即开启新小节，并成为该小节的 Beat 1；变化前后的任何 Project 内容保持原 absolute tick。

如果变化 tick 不是前一个拍号段起点之后的自然完整小节边界：
```text
旧小节在变化 tick 被截断
变化 tick 开启下一个 1-based Bar
产生一条 Warning，定位到该 Time Signature 的稳定 ID 和 tick
```
如果变化恰好位于自然小节边界，则不产生截断 Warning。后续 Bar 编号、Beat 网格和 `Bar:Beat:Tick` 均从变化 tick 按新拍号重新计算。
---
## 4.10 Conductor Track 事件与项目范围
### 4.10.1 超出最后音乐内容的全局事件
Conductor Track 允许存在超过最后音乐内容的事件。
原因：
```text
用户可能先规划全局结构
用户可能先放置后续段落的 Tempo / 拍号 / Marker
空项目也允许编译
Conductor Track 是全局规划轨道，不应受当前 Logical Track 内容限制
```
是否在导出时保留尾部空白、是否裁掉尾部全局事件、是否允许具体操作覆盖 Project End Marker 默认范围，由 第 14 章《MIDI 导出》 和实现设计阶段细化。
### 4.10.2 不参与 Segment 裁剪
Conductor Track 事件不参与 Segment 裁剪。
Segment 是 Logical Track 的有效时间范围容器。
Conductor Track 是项目级全局轨道。
两者语义不同，不应混同。
---
## 4.11 与编译系统的关系
编译器必须读取 Conductor Track，并把其全局音乐事件纳入编译上下文。
Conductor Track 应参与：
```text
Tempo Map 生成
Time Signature Map 生成
Key Signature Map 生成
Marker 数据整理
Project End Marker 读取
中途播放上下文解析
MIDI 导出全局事件生成
错误 / 警告 / 信息诊断
```
Conductor Track 不应参与：
```text
Event Instrument Instance 展开
Channel Group 分配
Channel-Wide 状态污染检测
Segment 裁剪
事件乐器生命周期策略
```
除非某些后续系统明确需要读取全局音乐状态。
---
## 4.12 与播放、预览、音频渲染的关系
播放、预览和音频渲染必须使用 Conductor Track 的 Tempo 信息计算实际时间。
中途播放时，系统必须能从当前起播 tick 解析：
```text
当前 Tempo
当前 Time Signature
当前 Key Signature 状态
当前 Marker / End Marker 相关范围信息
必要的全局状态摘要
```
对于实际发声结果：
```text
Tempo 影响播放速度和渲染时长
Time Signature 通常不影响声音本身，但影响时间轴与定位
Key Signature 初版不影响声音本身
普通 Marker 不影响声音本身
Project End Marker 直接影响导出 / 渲染等操作的默认范围；播放 / 预览是否默认受其限制、以及具体裁剪 / Reset / 停止行为由实现设计确定
```
中途播放上下文解析的具体算法属于实现设计阶段。
---
## 4.13 与 MIDI 导出的关系
MIDI 导出必须包含 Conductor Track 的全局音乐事件。
已确定导出模式均应包含 Conductor Track 或其副本：
```text
整曲 MIDI：包含 Conductor Track
按 Logical Track 导出：每个文件包含 Conductor Track 副本
按 Port 导出：每个文件包含 Conductor Track 副本
```
Conductor Track 副本应保证分开导出后仍能保留一致的 Tempo、拍号、调号、Marker 等全局信息。
具体导出事件顺序、Meta Event 字节编码、Project End Marker 是否导出为普通 Marker、文件结构由 第 14 章《MIDI 导出》 / 实现设计阶段细化。

SMF 的四字节 delta-time VLQ 上限不限制 Project/canonical 的绝对 Tick、Gate 或事件间隔。超长间隔仅在导出编码时按 §14.12.2 插入零长度 Text Meta；MTrk 的 `0xFFFFFFFF` 字节上限仅在导出时检查，超限不拆分、不作为编译错误。TPQ、Tempo、拍号/调号及 Int64 Tick 运算的既有合法性要求不变。
---
## 4.14 错误、警告与诊断
### 4.14.1 错误
以下情况应作为错误：
```text
Project 缺失 Conductor Track
Project 存在多个 Conductor Track
Conductor Track 缺失 Tick 0 Tempo
Conductor Track 缺失 Tick 0 Time Signature
Tempo 值小于等于 0
Tempo 值无法在 Midora 支持的 MIDI 1.0 导出语义下表示
Time Signature 值非法
Time Signature Denominator 与 Project TPQ 不满足 `4 × TPQ % Denominator == 0`
Key Signature 值非法
Conductor Track 事件位于负 tick
同一 tick 存在多个冲突的同类型全局事件，且 UI 未提前替换或阻止
TPQ 非法
创建 Project 后尝试修改 TPQ
Project 中存在多个 Project End Marker
```
### 4.14.2 警告
初版不因为 Tempo 极端偏低或偏高而产生警告。
每个发生在旧小节中途并截断旧小节的 Time Signature 变化必须产生 Warning；该 Warning 不修改事件 tick，且服从编译请求的 Warning-as-error 策略。
以下情况是否作为警告，后续由 第 15 章《音频文件渲染》 统一细化：
```text
拍号极端复杂，可能导致网格显示难以阅读
Conductor Track 存在超过实际音乐内容很远的尾部全局事件
Project End Marker 早于实际内容范围
导出目标环境可能忽略部分全局 Meta Event
```
### 4.14.3 信息
以下情况可作为信息：
```text
Key Signature 仅作为元数据，不会自动转调
Time Signature 改变只影响小节 / 拍网格，不改变已有事件 tick 位置
Tempo 改变只影响秒时间，不改变 tick 位置
Marker 仅用于定位，不影响声音
```
---
## 4.15 规则、限制与失败条件
### 4.15.1 强制规则
1. 每个 Project 必须有且只有一个 Conductor Track。
2. Conductor Track 固定存在，不可删除。
3. Conductor Track 不支持静音或禁用。
4. 不允许多个 Conductor Track。
5. 不允许 Logical Track 局部覆盖 Tempo、Time Signature 或 Key Signature。
6. 新 Project 默认 Tempo = 120 BPM。
7. 新 Project 默认 Time Signature = 4/4。
8. 新 Project 默认不指定 Key Signature。
9. Tick 0 必须存在显式 Tempo。
10. Tick 0 必须存在显式 Time Signature。
11. Tick 0 的 Tempo 和 Time Signature 不可删除，只能修改值。
12. 初版允许创建项目时设置 TPQ。
13. Project 创建后 TPQ 禁止修改。
14. BPM 不影响四分音符到 tick 的换算。
15. Tempo 只影响 tick / quarter note 到 seconds 的换算。
16. BPM 必须大于 0。
17. BPM 允许小数。
18. BPM 不限制到传统音乐经验范围。
19. BPM 极端偏低或偏高不产生警告。
20. BPM 必须能在 Midora 支持的 MIDI 1.0 导出语义下得到有效表示。
21. 初版只支持离散 Tempo 事件。
22. 初版不支持 Tempo Ramp 或曲线 Tempo Automation。
23. Time Signature Numerator 允许范围为 1–99。
24. Time Signature Denominator 允许值为 1、2、4、8、16、32、64。
25. 每个 Time Signature 必须满足 `4 × TPQ % Denominator == 0`。
26. 每个 Time Signature 变化 tick 立即开启新小节；若它截断旧小节，必须产生 Warning。
27. 初版不支持 additive meter。
28. Time Signature 改变不重排任何已有内容。
29. 拍号不应影响音符位置。
30. Key Signature 初版开放编辑。
31. Key Signature 只作为元数据、显示信息和导出信息。
32. Key Signature 不影响自动音高、转调、Scale Quantize 或映射。
33. 初版支持普通 Marker。
34. 初版 UI 应支持 Marker 列表和定位能力。
35. 初版不支持通用 Text / Lyric / Cue Point / Copyright 等复杂文本类 Meta Event 编辑。
36. 同一 tick 的同类型全局事件由 UI 层替换，最终只保留一个。
37. Conductor Track 事件不参与 Segment 裁剪。
38. Conductor Track 允许存在超过最后音乐内容的事件。
39. Project End Marker 可选。
40. Project End Marker 存在时，系统使用其作为显式项目结束位置。
41. Project End Marker 不存在时，由内容自然决定项目长度。
42. 一个 Project 最多只能有一个 Project End Marker。
43. Project End Marker 早于已有内容时，项目内容仍然保留。
44. Project End Marker 早于已有内容时，其作为导出 / 渲染等操作的默认范围依据，不自动销毁其后内容。
45. Project End Marker 早于已有内容时，系统应给出信息或警告，具体诊断等级由第 15 章《音频文件渲染》规定。
### 4.15.2 失败条件
以下情况应导致相应操作失败或进入错误状态：
| 场景 | 结果 |
|---|---|
| Project 缺失 Conductor Track | 打开失败或项目结构错误 |
| Project 存在多个 Conductor Track | 打开失败或项目结构错误 |
| Tick 0 缺失 Tempo | 项目结构错误 / 编译失败 |
| Tick 0 缺失 Time Signature | 项目结构错误 / 编译失败 |
| Tempo <= 0 | 阻止输入或编译失败 |
| Tempo 无法表示为 Midora 支持的 MIDI 1.0 导出语义 | 阻止输入、编译失败或导出失败，具体处理阶段由相关专项规则确定 |
| Time Signature Numerator 非法 | 阻止输入或编译失败 |
| Time Signature Denominator 非法 | 阻止输入或编译失败 |
| Time Signature Denominator 与 Project TPQ 不满足整除约束 | 阻止输入、持久化读取/保存拒绝或编译失败 |
| Key Signature 非法 | 阻止输入或编译失败 |
| 创建后尝试修改 TPQ | 阻止操作 |
| Conductor Track 事件位于负 tick | 阻止输入或编译失败 |
| 一个 Project 中存在多个 Project End Marker | 阻止输入或项目结构错误 |
---
