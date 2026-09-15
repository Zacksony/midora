# 第 13 章 播放与预览

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义主时间线播放、Event Instrument/SubVoice/Segment 预览、播放状态机、运行锁定、中途状态恢复、Logical 与 Pure MIDI 两类 Track 的监听过滤、BASSMIDI Stream、输出设备、设备采样率、Master Volume、Limiter、可调 buffer 和实时性能边界。

## 13.1 播放系统核心原则
### 13.1.1 只消费 canonical compiled result
播放系统必须消费 `canonical compiled result`。
规则：
```text
播放系统不得直接读取 Logical Track / Pure MIDI Track / Segment / Event Instrument 并自行解释音乐语义。
播放系统不得重新计算 Logical Parameter Mapping。
播放系统不得重新决定生命周期、Reset、Channel Group 或 Port / Channel Unit 分配语义。
播放系统不得为了性能绕过编译系统。
```
播放系统可以在 compiled result 之上构建：
```text
播放调度队列
预渲染 buffer
运行期状态机
Track Mute / Solo 过滤状态
BASSMIDI Stream 缓存
后端清理状态
输出链状态
```
但这些结构都是消费者缓存或运行期状态，不得成为另一套语义来源。
### 13.1.2 播放前必须有可播放 compiled result
用户点击播放时，如果当前播放范围 / Track 范围对应的播放编译失败：
```text
禁止进入 Playing。
显示编译失败诊断。
不产生 partial 播放结果。
```
如果用户开启“Warning 导致编译失败”，则播放编译同样遵守该策略。
### 13.1.3 播放使用专用 CompileContext
主播放、Segment 预览、Event Instrument 预览都应使用对应的 CompileContext。
初版至少存在：
```text
Playback CompileContext
Segment Preview CompileContext
Event Instrument Preview CompileContext
SubVoice Preview CompileContext
```
这些上下文只改变：
```text
范围
临时输入
预览对象
Track / Segment 选择
输出用途
```
不得改变：
```text
事件展开语义
Logical Parameter 状态继承语义
Mapping 计算语义
生命周期语义
Reset 语义
Port / Channel 分配语义
同 tick 语义排序
```
---
## 13.2 初版播放任务类型
初版只允许一个活动播放任务。
播放任务包括：
```text
主时间线播放
Segment 预览
Event Instrument 预览
SubVoice 预览
Event Instrument 虚拟键盘预览
```
同一时刻：
```text
只能存在一个活动播放任务。
主播放、Segment 预览、Event Instrument 预览、SubVoice 预览互斥。
```
如果已有活动播放任务，用户触发另一个播放 / 预览任务：
```text
禁止新任务。
提示需要先 Stop。
```
保存 / 打开 / 关闭 / 导出属于项目级操作，可以自动 Stop 当前任务后继续。新的播放或预览任务不自动抢占旧播放任务。
---
## 13.3 播放状态机
### 13.3.1 顶层状态
初版播放系统只定义以下顶层状态：
```text
Stopped
Preparing
Playing
Stopping
Error
```
初版不包含：
```text
Paused
Resuming
Scrubbing
Recording
```
初版不支持 Pause。
### 13.3.2 Preparing
`Preparing` 表示：
```text
正在执行播放前编译 / 缓存检查
正在检查 SoundFont 和播放设备
正在创建或复用 BASSMIDI Stream
正在加载当前 SoundFont 配置
正在执行 Channel 10 melodic 初始化
正在恢复播放起点状态
正在准备初始预渲染 buffer
```
Preparing 期间：
```text
禁止再次点击 Play 触发并发播放。
允许点击 Stop 取消准备。
禁止编辑 Project。
禁止修改程序级 SoundFont 列表。
禁止修改播放设备。
禁止 Undo / Redo。
```
### 13.3.3 Playing
`Playing` 表示播放任务处于活动状态。
Playing 可包含内部子状态：
```text
Playing.Normal
Playing.Buffering
```
`Playing.Buffering` 用于表示 underrun 后等待预渲染恢复。
### 13.3.4 Stopping
`Stopping` 表示系统正在执行 Stop 清理。
清理包括：
```text
停止调度后续事件
关闭活动 Note
必要时发送 All Notes Off / All Sound Off / Reset All Controllers 等兜底事件
清理活动实例状态
清理或重置 Channel-Wide 状态
清空播放状态机
```
### 13.3.5 Error
发生不可恢复播放错误时，系统应：
```text
立即停止当前播放任务
执行 Stop 类清理
进入 Error 状态
保留播放位置
显示错误信息
不修改 Project 内容
```
用户修复问题后，可以从 Error 状态重新点击 Play，系统重新进入 Preparing。
---
## 13.4 播放期间操作锁定
### 13.4.1 禁止编辑 Project
Playing / Preparing / Buffering 期间禁止所有会改变 Project 内容或播放语义的操作，包括：
```text
编辑 Conductor Track
编辑 Logical Track / Pure MIDI Track / Segment / Logical Note / Direct MIDI Event
编辑 Logical Parameter Lane
编辑 Event Instrument / SubVoice / Mapping / Lifecycle
创建 / 删除 / 重命名 / 复制 Event Instrument
创建 / 删除 / 排序 Logical Track
修改 Project Settings 中影响编译或播放的设置
修改程序级 SoundFont 列表
修改播放设备
Undo / Redo
```
### 13.4.2 允许纯 UI 操作
Playing 期间允许不改变 Project 内容、不改变播放语义的纯 UI 操作，例如：
```text
滚动时间线
缩放视图
选择对象
打开信息面板
查看诊断
切换非持久化显示过滤
移动播放视图焦点
```
### 13.4.3 Mute / Solo 是允许的监听状态操作
Mute / Solo 都属于临时播放监听状态，不属于 Project 内容。
因此播放期间允许切换 Mute / Solo。
规则：
```text
Mute / Solo 不保存进 Project。
Mute / Solo 不进入 Undo / Redo。
Mute / Solo 不标记 Project 已修改。
Mute / Solo 切换不属于编辑 Project。
```
---
## 13.5 保存、打开、关闭与导出期间的自动 Stop
### 13.5.1 保存 Project
播放期间用户可以触发保存：
```text
保存按钮
Ctrl + S
```
执行顺序必须是：
```text
1. 自动 Stop 当前播放任务；
2. 执行 Stop 清理；
3. 播放光标位置按 Stop Cursor Behavior 设置处理；
4. 开始保存 Project；
5. 保存完成后保持 Stopped；
6. 不自动恢复播放。
```
保存 Project 本身不改变 compiled result 语义，也不应单独使编译缓存失效。
### 13.5.2 打开 / 关闭 Project
播放期间打开其他 Project 或关闭当前 Project 时：
```text
自动 Stop 当前播放任务；
完成 Stop 清理后，继续执行打开 / 关闭流程。
```
自动 Stop 不跳过未保存修改提示。
仍应按项目系统规则处理：
```text
保存
不保存
取消打开 / 关闭操作
```
### 13.5.3 MIDI 导出 / 音频文件渲染
播放期间触发 MIDI 导出或音频文件渲染时：
```text
1. 自动 Stop 当前播放任务；
2. 执行 Stop 清理；
3. 播放光标位置按 Stop Cursor Behavior 设置处理；
4. 进入导出 / 渲染流程；
5. 导出 / 渲染完成后保持 Stopped；
6. 不自动恢复播放。
```
导出 / 渲染流程必须使用对应专用 CompileContext 重新检查或复用等价缓存。
不得直接复用运行中的播放 buffer 作为导出 / 渲染结果。
### 13.5.4 Preparing / Buffering 期间触发保存
如果处于：
```text
Preparing
Playing.Buffering
```
用户触发保存 / 打开 / 关闭 / 导出，同样按自动 Stop 流程处理：
```text
取消准备 / 等待
执行清理
再继续项目级操作
```
### 13.5.5 自动 Stop 清理失败
如果自动 Stop 过程中后端返回错误，例如 Stream 清理失败：
```text
尽最大努力清理。
保存允许继续。
打开 / 关闭 / 导出需提示风险并由用户确认后继续。
```
---
## 13.6 播放范围与播放光标
### 13.6.1 默认播放起点
Stopped 状态点击主播放 Play 时：
```text
startTick = 当前播放光标 tick
```
用户可以在 Stopped 状态下把播放光标放到任意合法 tick，再从该位置播放。
播放起点允许为任意 tick，不要求对齐小节、拍线或 Segment 边界。
播放起点必须满足：
```text
startTick >= 0
```
否则禁止播放。
### 13.6.2 默认播放终点
用户从主时间线点击播放时，默认播放范围为：
```text
startTick = 当前播放光标 tick
endTick = Project End Marker tick；若无 End Marker，则由有效内容自然结束
```
如果 Project End Marker 早于后续内容：
```text
主播放默认到 End Marker 为止。
后续内容保留，但默认不播放。
```
用户显式指定播放范围超过 Project End Marker 时：
```text
允许播放超过 End Marker 的内容。
Project End Marker 只是默认范围依据，不是播放硬限制。
```
### 13.6.3 零长度播放范围
如果播放范围为：
```text
[startTick, startTick)
```
允许进入 Preparing，但随后立即完成 Stop 清理流程，不产生声音输出。
如果：
```text
endTick < startTick
```
属于非法播放范围，禁止播放。
### 13.6.4 播放光标显示
播放光标位置以当前播放 tick 为主。
播放系统不要求完全由音频设备 sample clock 反推播放光标。
Buffering 等待期间：
```text
播放光标停在当前 tick。
直到恢复播放后继续移动。
```
### 13.6.5 Stop Cursor Behavior
Stop 后播放光标位置由用户设置决定：
```text
Stop Cursor Behavior:
1. 回到本次播放开始 tick（默认）
2. 停在当前停止 tick
```
播放途中用户跳转到新 tick 时：
```text
“本次播放开始 tick”保持不变。
```
因此，如果用户从 tick 100 开始播放，播放中跳到 tick 500，再按 Stop：
```text
若设置为“回到本次播放开始 tick”：回到 tick 100
若设置为“停在当前停止 tick”：停在 Stop 发生时的当前 tick
```
当播放自然到达 endTick 时：
```text
进入 Stop 清理流程。
完成清理后，播放光标位置仍受 Stop Cursor Behavior 控制。
```
---
## 13.7 播放跳转与循环
### 13.7.1 播放中跳转
播放中用户跳转到新 tick 时，视为：
```text
停止当前播放状态
清理旧状态
从新 tick 冷启动
恢复非 Note 状态
不补发范围前 Note On
继续播放
```
播放中跳转不会改变 Stop Cursor Behavior 中的“本次播放开始 tick”。
### 13.7.2 循环播放
初版支持时间范围循环播放。
循环范围必须满足：
```text
loopStart >= 0
loopEnd > loopStart
```
到达 loopEnd 时：
```text
停止调度当前循环尾部后续事件
发送必要 Note Off / Reset / All Notes Off / All Sound Off
清理活动实例状态
从 loopStart 冷启动恢复非 Note 状态
继续播放
```
循环播放时，播放光标到达 loopEnd 后跳回 loopStart，并继续显示循环内当前位置。
---
## 13.8 从中途播放的状态恢复
### 13.8.1 不补发范围前 Note On
如果某个 Note On 在播放起点之前发生，播放起点之后仍在持续：
```text
播放系统不补发该 Note On。
```
这是中途冷启动的明确限制。

对于 Pre-Roll 实例，如果播放/跳转/循环起点位于 Instance Origin 之后，系统同样不补发 origin 之前的模板 Note On，也不在输出起点前隐藏渲染并丢弃 PCM 来恢复 sample 相位。即使 Logical Gate anchor 尚未到达，该实例的范围前 Note On 仍属于已经错过的历史。非 Note 状态继续按 13.8.2 恢复。
### 13.8.2 必须恢复非 Note 状态
从中途播放时，必须在播放起点恢复当前有效的非 Note 状态，包括：
```text
Program
Bank Select
Pitch Bend
Pitch Bend Range
CC
RPN
NRPN
Logical Parameter Mapping 后影响到的非 Note 状态
Tempo / Time Signature / Key Signature 的播放上下文状态
```
恢复状态不得依赖复用 Stream 的当前状态。
每次进入 Playing 前，本次使用的 Stream 必须处于清洁状态，然后基于 compiled result 发送范围起点状态恢复事件。
### 13.8.3 恢复事件排序
如果播放起点 tick 上既有恢复事件，又有用户原始事件：
```text
恢复事件先发送。
同 tick 用户原始事件随后发送。
用户事件优先覆盖恢复状态。
```
范围起点状态恢复事件应作为播放调度队列中的事件处理，并标记来源：
```text
范围起点状态恢复
```
不应散落为不可追踪的后端初始化副作用。
### 13.8.4 tick 0 播放
从 tick 0 播放时，仍需要发送必要系统初始化与 tick 0 用户事件，包括：
```text
Tempo / Time Signature 默认状态
Channel 10 melodic 初始化
tick 0 Program / Bank / CC / Pitch Bend / RPN / NRPN / Note 等事件
```
---
## 13.9 Conductor Track 播放语义
Conductor Track 永远参与播放上下文。
Mute / Solo 永远不影响 Conductor Track。
### 13.9.1 Tempo
实时播放速度完全由 Conductor Track 的 Tempo Map 决定。
初版不提供额外 playback speed multiplier。
播放系统必须支持播放过程中遇到 Tempo 事件后即时改变 tick-to-seconds 换算。
每个 canonical 事件的 sample frame 以及播放范围的 sample 长度必须按第 4.1.4 节计算：使用本次实际设备采样率，对相对播放起点的完整 Tempo Map 区间做 decimal 积分，乘采样率后仅执行一次 `AwayFromZero`。不得为实时播放采用与预览或文件渲染不同的取整规则。
### 13.9.2 Time Signature
Time Signature 只影响：
```text
小节 / 拍显示
播放光标的小节坐标
Buffering 恢复目标的小节长度
循环范围显示辅助
```
Time Signature 不直接改变声音事件 tick 位置。
### 13.9.3 Key Signature
Key Signature 只作为状态型全局音乐元数据和显示 / 导出信息。
Key Signature 不直接影响播放音高。
### 13.9.4 Marker
普通 Marker 不影响播放声音。
Marker 只影响：
```text
定位
显示
UI 辅助
```
---
## 13.10 Track Mute / Solo 临时监听状态
### 13.10.1 归属
初版支持 Track Mute / Solo，但二者均为临时播放监听状态。
规则：
```text
Mute 不属于 Project 内容。
Solo 不属于 Project 内容。
Mute / Solo 不保存进 Project。
Mute / Solo 不进入 Undo / Redo。
Mute / Solo 切换不标记 Project 已修改。
关闭 Project 或打开新 Project 后，Mute / Solo 状态全部丢弃。
新建或打开 Project 后，所有 Logical Track 与 Pure MIDI Track 默认 Mute = false，Solo = false。
```
### 13.10.2 播放过滤规则
主时间线播放默认包含全部结构有效的 Logical Track 与 Pure MIDI Track。
初版主播放不提供独立于 Mute / Solo 的显式 Track 选择。

平铺 Arrangement 只为实际 Track 提供 Mute / Solo；不可见的 Event Instrument Usage 与 MIDI Channel Root 不再拥有第二套运行时开关。候选集合固定按以下规则计算：

1. 至少一个 Track Solo 时，在全 Project 只保留 `Track Solo && !Track Mute` 的 Track。
2. 否则，保留全部未 Mute 的结构有效 Track。

Mute 始终胜过 Solo；多个 Track 可以同时 Solo。Conductor Track 不参与该集合。

每次主播放任务在取得 Project edit lock、确认当前 canonical 可消费之后，必须根据**当前** Project Track 集合和当前 Mute/Solo 状态重新冻结候选集合，再生成 realtime plan。候选集合不得只在 Playback Controller、Project Session 或窗口创建时计算一次；播放控制器存活期间新建、删除、改绑或改变共享组归属的 Track 必须从下一次播放开始正确参与。
### 13.10.3 Mute / Solo 不改变 compiled result
Mute / Solo 只作为播放消费者层实时过滤。
规则：
```text
Track Mute / Solo 不改变 canonical compiled result。
Mute / Solo 不影响 MIDI 导出。
Mute / Solo 不影响音频文件渲染。
成品输出由导出 / 渲染设置中的 Track 选择决定。
```
### 13.10.4 Mute / Solo 过滤不只过滤 Note
被过滤 Track 的以下事件不得发送：
```text
Note
Program
Bank Select
Pitch Bend
Pitch Bend Range
CC
RPN
NRPN
Logical Parameter Mapping 后影响到的非 Note 状态
范围起点状态恢复事件
Pure MIDI direct/opaque 可播放事件
```
这用于避免被过滤 Track 的状态污染其分配到的 Channel Unit。
### 13.10.5 播放中打开 Mute
播放中将某 Track Mute 打开时：
```text
立即生效。
立即截断并清理该 Track 当前活动声音。
不等待 Release / Tail 自然结束。
停止发送该 Track 后续事件。
```
### 13.10.6 播放中开启 Solo
播放中开启 Solo 后：
```text
所有不在 Solo 集合且当前正在发声的 Track 立即清理。
只清理新被过滤掉的 Track。
不影响仍应发声的 Track。
```
### 13.10.7 解除 Mute / Solo
播放中解除 Mute 或取消 Solo 过滤后，重新进入候选集合的 Track：
```text
从当前 tick 冷启动恢复必要非 Note 状态。
不补发之前因过滤错过的 Note On。
恢复事件在当前 tick 后续普通事件之前发送。
```
### 13.10.8 Mute / Solo 清理范围
Mute / Solo 导致 Track 被过滤时：
```text
清理只作用于该 Track 关联的 Channel Unit / Stream 范围。
不得全局 All Sound Off 影响仍应发声的 Track。
```
允许使用强兜底清理事件，例如：
```text
All Notes Off
All Sound Off
Reset All Controllers
```
但必须限制在该 Track 当前关联的 Channel Unit / Stream 范围内。
### 13.10.9 来源追踪要求
为了支持 Mute / Solo，播放事件至少需要能追踪到：
```text
Logical Track ID
Event Instrument Usage ID
Event Instrument Definition ID
Pure MIDI Track ID
MIDI Channel Root ID
Segment ID
Event Instrument Instance ID 或临时实例来源
SubVoice ID
Port / Channel Unit
```

### 13.10.10 共享执行组上的 Track 过滤

同一 MIDI Channel Root 的多个 Pure MIDI Track 共享一个 Channel Unit。播放层不得通过清空整个 Unit 来实现单个子 Track 的 Mute/Solo，因为这会杀死 sibling Track 的 Note 并破坏其共享状态。

过滤变化必须在稳定 producer frontier 原子替换未来 suffix，并基于 canonical 来源追踪：精确释放刚被过滤 Track 拥有的活动 Note；保留仍可听 Track 的活动 Note；随后从当前 tick 重建该 Root 由仍可听 Track 决定的必要 Channel 状态。若当前 canonical/source tracing 不能证明该恢复安全，必须进入受控 Buffering 并从当前 tick 冷启动该 Root，不能继续输出旧 suffix、卡在 Playing 或静默清空整个 Root。

多个 Logical Track 共享同一 Event Instrument Usage 时适用同一原则：不得为静音单个成员而清空整个 Usage Unit；必须以 Track 来源追踪精确释放 Note，并从当前 tick 重建该 Usage 的必要映射后 Channel 状态。
---
## 13.11 SoundFont 与播放
### 13.11.1 播放需要有效 SoundFont 配置
点击播放时，如果 Application Preferences 的有序 SoundFont 列表：
```text
没有任何 Enabled 项
任一 Enabled 路径缺失或不可读取
任一 Enabled SF2/SFZ 格式不受 BASSMIDI 支持
任一 Enabled SoundFont、SFZ include/sample 或目标映射加载失败
```
则：
```text
不进入 Playing。
显示不可播放原因。
不修改 Project 内容。
```
Event Instrument 预览、SubVoice 预览、Segment 预览与主播放一样需要有效 SoundFont 配置。
### 13.11.2 播放期间禁止修改 SoundFont
Playing、Buffering、实时 Preview 或文件 Rendering 期间禁止提交程序级 SoundFont 列表变更。
包括：
```text
新增或删除路径
启用或禁用条目
调整顺序
```
列表只在 Stopped / Idle 提交；提交后销毁持久音频 Worker，使相关 sample-domain 缓存 generation 失效，并在 `Saving Settings` 模态任务中立即创建新 Worker、加载冻结的 Enabled SF2/SFZ 列表及目标映射并探测输出设备。该预热不编译 Project、不创建播放计划、不进入 Playing；成功后的 Worker 必须保留并供下一次播放或预览复用。实时音频参数或音频缓存配置变化采用同一重建规则；纯 UI/最近目录等非音频设置变化不得重建 Worker。Project、canonical compiled result 和 Modified 状态不得因此变化。

若 Application Preferences 已成功持久化、但 Worker 创建、SoundFont/SFZ 依赖加载或映射应用失败，则设置保持为已保存的新值，Worker 进入不可用状态并报告具体失败；后续重新 Apply 或音频任务 Preparing 可以重试。不得静默恢复旧设置、假报已加载或保留指向旧列表的 Worker。

新建、打开或从 MIDI 导入形成的新 Project 会话，以及显式 `Reset Playback Engine`，都必须在其前台操作完成前调用同一 Worker 预热路径。该路径只建立/探测持久实时 Worker与冻结 Font handles，不执行 canonical 编译或播放计划生成。Project 会话切换已经成功后发生的预热失败只属于音频运行时失败：Project 保持打开，操作结果不得误报为 Project 打开/创建失败，后续 Play/Preview Preparing 仍可重试。
---
## 13.12 BASSMIDI Stream 生命周期
### 13.12.1 Canonical Unit 音频投影
正式 Canonical Compiled Result 仍必须完成全局 Port / Channel 分配，并保留其资源上限、诊断与 MIDI 导出语义。只有 canonical 成功后，音频消费者才可以确定性派生不依赖物理 Port / Channel 编号的抽象 Channel Unit 音频投影。

每个抽象 Unit 使用一个干净的 1-channel BASSMIDI decode stream 语义。音频投影不得绕过 canonical、合并两个不同 Unit，或改变 canonical 同 tick 顺序。

### 13.12.2 Stream pool 与 Channel Mode 初始化
实际 native Stream 由有明确上限、按需创建的可复用 pool 提供，不为 Project 中每个 Unit 永久保留 Stream，也不按历史最大 Unit 数预建 Stream。

每次创建、重建或复用 Unit Stream 时，必须先清除前一任务的 mode/state，再按 canonical Unit descriptor 建立唯一 channel 0：Logical/Event Instrument Unit 与 Melodic Pure MIDI Root 显式建立为 melodic；Percussion Pure MIDI Root 显式建立为 percussion/drum。物理 Channel 10 的默认 BASSMIDI 行为不得替代该 descriptor。

Pure MIDI Root 的 canonical audio projection 可以额外携带第 23.13.2 节限定的 GS/XG Part Mode privileged SysEx。该事件必须经过正式 canonical 顺序、range-start restore、Mute/Solo 来源过滤、paged event stream、IPC 与 PCM/cache identity 后，重定向并发送到抽象 Unit 的 channel 0；Worker 在发送完整规范化 SysEx 后还必须在同一正式顺序点通过 BASSMIDI 的显式 Unit mode 建立等价 Melodic/Percussion 状态，避免后端对厂商 SysEx 的 preset remap 因 SoundFont 而产生不一致。不得由 Worker 回读 opaque Project 数据或把任意 SysEx 直接送入后端。

### 13.12.3 Stream 复用
Stream 复用只能是性能优化。复用前必须重新确认：
```text
SoundFont handle/mapping 列表与本次任务冻结的程序级 Enabled SF2/SFZ 有序配置完全一致
采样率、格式和 Maximum Sample Voices per Unit Stream 匹配本次任务
Stream 处于清洁状态
channel 0 的 Melodic/Percussion mode 与本次 Unit descriptor 一致
无残留 Note
无残留 CC / Pitch Bend / Program / RPN / NRPN 等状态
```
不得继承上次 Unit 或上次播放残留状态。Stop / Reset Playback Engine 必须释放或清洁 pool 中的全部已使用 Stream。

### 13.12.4 Unit 使用集合变化
下一次播放的 Unit 集合变化时，pool 只按本次实际并发需求取得、清洁并复用 Stream；不允许把物理 Port/Channel 历史分配作为 PCM 缓存身份。
### 13.12.5 Stop 清理范围
Stop 清理应覆盖本次播放任务实际使用过的所有 Channel Unit，而不仅是当前仍有活动 Note 的 Channel Unit。
Stop 是立即停止，安全优先，不追求音乐性尾音。
规则：
```text
Stop 不等待 Release / Tail 自然结束。
必须尽快关闭所有活动声音。
必要时可使用 All Notes Off / All Sound Off / Reset All Controllers。
```

### 13.12.6 Reverb / Chorus 禁用

所有正式播放和预览 BASSMIDI Stream 必须启用：
```text
BASS_MIDI_NOFX
```

初版不提供 BASSMIDI Reverb / Chorus 音频效果，Event Instrument/SubVoice 路径不得生成 CC91 / CC93。Pure MIDI Track 可以合法保存、编译和导出 CC91 / CC93；音频执行投影遇到这两个控制器时必须保持事件顺序与来源追踪，但在启用 `BASS_MIDI_NOFX` 的正式 synth 路径中不把它们解释为可听效果，也不得把合法 Pure MIDI 事件报告为 compiled-result/IPC 一致性 Error。

`BASS_MIDI_NOFX` 是正式后端配置，不是用户可关闭的音色选项。

### 13.12.7 同音高重叠 Note 的释放

所有正式播放、预览和音频文件渲染使用的 BASSMIDI Stream 必须启用：
```text
BASS_MIDI_NOTEOFF1
```

当同一 Port、同一 Channel、同一 pitch 同时存在多个活动 Note 实例时，每个 NoteOff（包括 velocity `0`）只释放最早开始且尚未释放的一个实例，即 FIFO 配对。不得允许调用方切换为“一次释放全部同 pitch 实例”。

Canonical Compiled Result 仍必须为每个 Logical Note 和成功配对的 Direct MIDI Note 保留独立 NoteOff；硬边界存在多个同 pitch 活动实例时，必须按配对数量发出 NoteOff，再执行规定的控制器状态重置。Cut Previous 的旧实例 Release 与新实例重叠时，旧实例后续 NoteOff 必须释放最早开始的旧实例，不得误杀替换实例。

该规则不改变第 8.53.7 节允许同音高重叠 Note 的源数据语义，也不允许播放后端自行重建或猜测 Project 级 Note 身份。

`BASS_MIDI_NOTEOFF1` 是正式后端配置，不是用户可调选项。

### 13.12.8 Stream 采样率

实时播放和预览的每个 Unit BASSMIDI Stream 必须直接按当前所选输出设备初始化后报告的实际输出采样率生成音频。

```text
所有实际 Unit 使用同一实时采样率
不先固定生成 48 kHz 再做最终重采样
设备或其实际采样率变化时，旧 Stream 和所有 sample-domain 缓存失效
```

SoundFont 内部样本插值或合成器内部采样处理不属于这里禁止的“固定输出采样率后再重采样”。

### 13.12.9 BASSMIDI 合成性能档与实时复音上限

所有正式 BASSMIDI Stream 固定使用：
```text
BASS_ATTRIB_MIDI_SRC = 1      // 8-point sinc
BASS_ATTRIB_MIDI_CPU = 0      // automatic
```

`CPU = 0` 的 BASSMIDI 官方语义是 automatic，不得在通用说明中误写为无条件“不限制”。Midora 的正式 Stream 是由自身渲染线程主动拉取的 decode Stream，不由 BASS update thread 播放；在该拓扑中 `0` 表示不启用 BASSMIDI CPU shedding，不因 CPU 属性杀 voice。若后续改变处理拓扑，必须重新验证，不能沿用这一推论。

初版只支持 `win-x64`，x64 的 SSE2 基线满足 8-point sinc 的处理器前提。若未来增加其他 CPU 架构，必须重新验证 BASSMIDI 对应架构的 sinc 支持和逐采样回归，不得静默降低为 linear interpolation。

Preparing 必须从冻结的 sample-domain 计划收集实际会被 Note On 使用的 Bank MSB / Program 组合，并在进入 Playing / Preview Playing 前通过 `BASS_MIDI_FontLoad` 预加载对应 SoundFont source。未映射 SF2 按引用组合预载；映射 SF2 按映射 source 预载；SFZ 按名义 source `0/0` 预载。实时事件 Stream 不得调用只适用于 MIDI 文件/序列 Stream 的 `BASS_MIDI_StreamLoadSamples`。若引用的 SF2 精确组合不存在，不得把它提升为 Project 或编译错误；后端必须保持第 6.4 节允许的 BASSMIDI fallback 语义，并确保 fallback 所需样本也在 Preparing 完成加载。SFZ sample/include 加载错误属于音频任务错误。

Application Preferences 提供用户可编辑的 `Realtime Maximum Sample Voices per Unit Stream`：
```text
合法范围：1–16,777,216 的整数
默认值：500
生效单位：每个抽象 Channel Unit 的 1-channel BASSMIDI Stream
```

同一次播放或预览任务的所有 Unit Stream 必须使用同一个冻结值。该值是单个 Unit Stream 内 BASSMIDI 同时活动 sample voice 数上限，不是 MIDI Note 数；一个 Note 可以因 SF2 分层占用多个 sample voices。只允许在 Stopped 修改，修改后使实时 native Stream 与全部实时音频 PCM/cache generations 失效，但不使 tick-domain canonical compiled result 失效。

达到上限时允许 BASSMIDI 按其固定 voice-limit 行为终止 voice。这是用户配置的后端资源上限，不是 Compiler、Overlap 或 Channel Group 的语义级 Voice Stealing，不能修改 Canonical Compiled Result。需要音频逐采样或不同 block 完美一致的测试，前提必须包含实际活动 sample voices 未达到配置上限。
---
## 13.13 Reset Playback Engine
初版需要提供 `Reset Playback Engine` 命令。
用途：
```text
播放后端异常恢复
悬挂音处理
Stream 状态残留清理
Error 后端恢复
```
规则：
```text
Reset Playback Engine 不改变 Project 内容。
Reset Playback Engine 不进入 Undo / Redo。
Reset Playback Engine 不影响 compiled result 缓存。
Reset Playback Engine 在 Stopped 状态也允许执行。
Reset Playback Engine 不需要用户确认，直接执行。
成功后只做轻量状态提示，不弹窗。
```
如果当前处于：
```text
Playing
Preparing
Playing.Buffering
Error
```
用户执行 Reset Playback Engine 时：
```text
先终止当前播放任务。
执行完整后端清理。
销毁或重建所有 BASSMIDI Stream / 后端连接缓存。
进入 Stopped。
```
Reset Playback Engine 应强制清空后端播放缓存，而不是仅发送 All Notes Off。
---
## 13.14 播放输出设备
### 13.14.1 设置归属
播放输出设备选择属于软件全局 / 用户环境设置，不保存进 Project。
理由：
```text
Project 文件跨机器移动时，设备名 / 驱动 ID 容易失效。
输出设备是本机环境状态，不是音乐内容。
```
### 13.14.2 Application Playback Preferences
Application Preferences 的 Audio 页包含：
```text
Playback Master Volume
Playback Limiter 开关
Stop Cursor Behavior
输出设备选择
音频后端偏好
Render-Ahead Buffer
Device Buffer Request
Realtime Maximum Sample Voices per Unit Stream
Maximum Reusable Audio Cache Bytes
```
以上均为当前 ProgramRoot portable 偏好，不进入 Project、`.midora`、Project Undo / Redo 或 canonical 编译结果。Audio Cache Root 固定为 `<ProgramRoot>\.tmp\AudioCache`，不作为偏好保存。初版不提供 Project 级 Playback override。
初版不提供 WASAPI Shared / Exclusive 模式选择；正式 BASSWASAPI 后端固定使用第 13.14.7 节策略。

### 13.14.3 设备枚举与选择

设置界面必须列出当前系统中全部：
```text
输出方向
处于 enabled / ready 状态
可由当前 BASSWASAPI 后端枚举
```
的音频输出端点。

不得把录音输入、loopback input、disabled、unplugged 或 not-present 端点作为可选择输出设备列出。系统默认输出设备必须有明确标记。

每个条目至少显示：
```text
设备友好名称
System Default 标记（如适用）
稳定设备 ID 对应的当前选择状态
```

设备列表在打开设置页时刷新，并响应后端设备变化通知。设备 ID 只保存为本机 Application Preference，不进入 Project。

### 13.14.4 设备不可用
如果当前选择的输出设备不可用，点击播放时：
```text
不进入 Playing。
显示设备不可用原因。
允许用户切换设备。
不修改 Project 内容。
```
如果用户没有手动选择输出设备：
```text
使用系统默认音频输出设备。
```
如果系统当前没有任何 enabled output device：
```text
设备列表显示空状态。
不进入 Playing。
允许 Project 打开、编辑、编译、保存、MIDI 导出和音频文件渲染。
```

### 13.14.5 修改设备或设备采样率
Stopped 状态下修改播放设备时：
```text
所有播放后端缓存失效。
所有按旧设备采样率建立的调度、PCM 和 Stream 缓存失效。若新旧设备的实际 sample format（含采样率、声道数、样本格式）完全相同，已完成且不绑定设备连接的 Unit raw PCM 可以保留；device-bound generation 与连接仍必须重建。
下一次播放重新创建后端连接。
```
播放期间禁止修改播放设备。

如果操作系统或驱动在运行期改变当前设备的实际采样率，或当前选择映射到另一个物理端点：
```text
停止当前播放或预览。
完成后端清理。
丢弃所有 sample-domain 缓存和 BASSMIDI Stream。
显示非 Project 的设备变化提示。
下一次播放按新实际采样率重新 Preparing。
```

### 13.14.6 可调 buffer

Application Preferences 提供以下整数毫秒设置：

| 设置 | 合法范围 | 默认值 | 生效范围 |
|---|---:|---:|---|
| Render-Ahead Buffer | 20–2000 ms | 100 ms | 已渲染实时 PCM 队列目标容量 |
| Device Buffer Request | 5–200 ms | 50 ms | 向 WASAPI 请求的设备 buffer 时长 |

规则：
```text
只能在 Stopped 状态提交修改
非法值不提交、不静默 Clamp
修改后使实时 PCM、调度和设备连接相关缓存失效
Device Buffer Request 只是请求值；设备可以按自身能力调整实际 buffer
请求值被设备调整本身不算错误
只有后端或设备初始化失败才阻止播放
```

UI 必须只读显示后端初始化后的：
```text
设备实际采样率
设备实际 buffer 时长 / frame 数
实际 callback period
最近 callback frame 数或范围（如后端可取得）
```

设备决定的 callback period、callback frame 数和内部固定工作 block 不作为用户可调设置。
### 13.14.7 WASAPI 初版输出策略
正式 BASSWASAPI 输出固定使用：
```text
WASAPI Shared Mode
event-driven callback
stereo
interleaved IEEE float32
所选端点初始化后报告的实际混音采样率
```

初始化请求必须使用 `BASS_WASAPI_EVENT`，不得设置 `BASS_WASAPI_EXCLUSIVE`、`BASS_WASAPI_AUTOFORMAT`、`BASS_WASAPI_BUFFER` 或 `BASS_WASAPI_ASYNC`。采样率请求使用端点实际混音采样率语义，声道数固定请求 `2`；Device Buffer Request 传入设备 buffer 请求，period 请求为 `0`，由设备决定实际 callback period 和 callback frame 数。

初始化后必须立即读取并验证实际信息：
```text
仍为 Shared Mode
仍为 event-driven
sampleRate > 0 且可安全表示
channelCount = 2
sampleFormat = float32
```
实际采样率、实际 buffer frame 数、观察到的 callback frame 数 / period 是 Derived / Runtime Data，只读报告，不写入 Project。实际采样率与 Preparing 使用的 sample-domain 计划不一致时，本次 Preparing 失败并清理；下一次播放必须重新枚举设备、按新采样率重建计划，不得重采样旧计划。

初版不得静默回退到 Exclusive Mode、轮询 / push 模式、整数 sample format、mono / 多声道或另一采样率。设备不支持正式策略时，显示初始化失败并允许用户选择其他输出设备；不得影响 Project 打开、编辑、编译、保存、MIDI 导出或音频文件渲染。
---
## 13.15 实时播放输出链
### 13.15.1 输出格式
初版实时播放只定义 stereo 输出。

实时播放采样率不是 Project 固定值，也不写死为 48 kHz。它等于所选输出设备初始化后正式报告的实际采样率。所有 Unit Stream、stereo mix、Master Volume、Limiter 和实时 PCM buffer 使用该采样率。

初版不支持：
```text
mono / stereo 可选
多声道实时输出
按 Port 输出到不同物理通道
按 Logical Track 输出到不同物理通道
```
### 13.15.2 Unit 混音
多个抽象 Unit 的 BASSMIDI Stream 音频输出按稳定 Unit key 顺序混合到同一个 stereo bus。
实时播放输出链为：
```text
Compiled Result
→ Canonical Segment / Unit 音频投影
→ Unit raw PCM / 播放 span 缓存
→ 确定性多 Unit stereo mix
→ Playback Master Volume
→ Limiter
→ Render-Ahead ring
→ 输出设备
```
### 13.15.3 内部精度
播放内部混音应使用足够避免明显累积削波的浮点音频表示。
第 13 章《播放与预览》 不固定具体使用：
```text
float32
float64
其他内部浮点格式
```
无论内部精度如何，WASAPI callback 边界按 BASSWASAPI 要求使用 interleaved float32 frame。
---
## 13.16 Playback Master Volume
初版需要定义项目播放总音量 / 监听音量。
规则：
```text
Playback Master Volume 属于 Application Playback Preferences。
Playback Master Volume 不保存进 Project。
Playback Master Volume 不改变 MIDI 编译结果。
Playback Master Volume 不写入 MIDI 导出。
Playback Master Volume 影响实时播放、预览和音频文件渲染的最终音频输出。
音频文件渲染不拥有独立导出音量 / 输出增益，也不提供 Master Volume bypass。
```
默认值：
```text
Playback Master Volume = -0.1 dB
```
播放总音量位于 Limiter 之前。
---
## 13.17 Limiter
### 13.17.1 存在原因
Midora 允许最多 256 个 Channel Unit 同时输出。
由于软件定位和事件乐器工作流，最终播放或音频文件渲染时可能出现总线音量过大、削波或爆音风险。
因此初版提供一个最简易内置 Limiter。
### 13.17.2 默认状态
```text
播放实时输出链路默认启用 Limiter。
音频文件渲染输出链路默认启用同一个或语义等价的 Limiter。
```
实时播放的 Limiter 开关由 Application Playback Preferences 管理。
音频文件渲染使用同一算法 / 语义和正式生效的 Limiter 参数，但初版渲染链强制包含最终 Limiter：
```text
不提供音频渲染专用 Limiter 开关。
不提供音频渲染 Limiter bypass。
Limiter 无法初始化时，音频渲染准备失败。
```
### 13.17.3 语义边界
Limiter：
```text
不改变 MIDI 编译结果。
不影响 MIDI 导出。
不转换为 MIDI CC7 / CC11。
只属于最终音频输出保护处理。
```
Limiter 只是最简易保护，不保证专业混音质量，也不替代用户后期分轨混音。
### 13.17.4 参数暴露
初版 Limiter 只提供开关。
初版不提供：
```text
threshold
release
lookahead
ceiling
ratio
knee
完整母带处理参数
```
这些项目不向用户暴露不表示其值未定义；初版正式值与算法固定于第 13.17.6 节。
### 13.17.5 处理位置
Limiter 位于：
```text
所有 Unit 的确定性 stereo mix 之后
Playback Master Volume 之后
输出设备 / 音频文件写入之前
```
Event Instrument 预览、SubVoice 预览、Segment 预览也默认经过播放 Limiter。
所有实时发声都走同一播放输出链。
### 13.17.6 初版固定算法
初版 Limiter 算法版本为 `2`，固定为 stereo-linked、look-ahead、4× inter-sample peak limiter。用户界面只显示 `Limiter`，不得显示 `v2` 或其他内部版本文本。

固定参数：
```text
ceiling = 0.8912509 linear（-1 dBFS）
lookAhead = 5 ms
hold = 10 ms
release = 100 ms 单极指数时间常数
oversampling = 4×
interpolation = 固定 16-tap、a=8 的归一化 Lanczos-windowed sinc
makeup gain = 0 dB（禁用）
```

每个实际 sampleRate 都必须把 look-ahead 与 hold 毫秒数分别向上取整为完整 frame。检测器对原始 sample frame 及其间的 `1/4`、`1/2`、`3/4` 相位重建值取左右声道共同绝对峰值。当前 frame 的允许 gain 同时受当前 inter-sample peak 与未来 look-ahead 窗口内**每一个** frame 的约束；不得只选择窗口内原始振幅最大的 frame，因为峰值距离同样决定当前所需的 attack gain。设当前 frame 为 `i`、未来 frame 为 `j`、look-ahead frame 数为 `L`、`requiredGain(j) = min(1, ceiling / peak(j))`，则该未来 frame 对当前 frame 的线性约束固定为 `requiredGain(j) + (1 - requiredGain(j)) * (j - i) / L`，当前允许 gain 取全部有效约束与 unity 的最小值。这样使每个峰值分别从 unity 建立线性前瞻 attack，并在各自峰值 frame 前到达所需值。gain 降低时重新开始 hold；hold 到期后按 100 ms 指数系数恢复，且恢复结果不得越过当前允许 gain。

输入 `left` / `right` 已经过所有 Unit 确定性求和与 Playback Master Volume。实时渲染器必须先生成当前输出 frame 后完整的分析窗口，再发布当前 frame；该预取只增加 Preparing / producer 内部前方量，不得在设备输出前插入静音或改变 Project 时间。离线渲染必须以同一方式预取，在硬结束处只用零值补足检测上下文，不输出补足值，最终 frame 数必须与正式范围完全一致。

规则：
```text
左右声道共享同一 detector、attack、hold、release 与 gain，不得分别限制。
不得在 Limiter 后追加硬削波、归一化或 makeup gain。
audio block 边界不得重置 detector history、look-ahead、hold 或 gain；不同 callback / 工作 block 大小必须产生相同连续处理语义。
新播放、预览或渲染任务以及 Stop / Reset 后必须把 gain 重置为 1.0；硬结束后不输出 release tail。
Held Preview / monitoring 的未来计划替换只能发生在已经预取的 Limiter raw frontier 之后；cold start 必须丢弃旧预取并重置 Limiter，再由既定短淡入处理切换边界。
任一输入或输出样本为 NaN / Infinity 时，当前音频任务按一致性错误失败，不得静默钳位或继续。
```

这是确定性输出保护而非响度母带器，不承诺专业母带质量；不得静默替换成另一套未版本化算法。
---
## 13.18 clipping 与 limiter activity
如果 Limiter 关闭且实时输出可能或已经发生削波：
```text
播放系统允许显示运行期 clipping 指示。
```
如果 Limiter 开启且发生明显限制处理：
```text
播放系统允许显示运行期 limiter activity 指示。
```
这些提示：
```text
属于实时播放运行期状态提示。
不进入 Project 编译诊断。
不标记 Project 已修改。
不保存进 Project 文件。
```
初版音频文件渲染不检测或报告 clipping / limiter activity；其强制启用的版本 2 Limiter 必须按第 15 章《音频文件渲染》输出范围规则处理最终样本。
---
## 13.19 预渲染 buffer 与 Buffering
### 13.19.1 基本策略
初版播放采用前方预渲染 / 预调度 buffer。
不采用：
```text
每个音频 buffer 回调时即时编译高层语义
完整预渲染整个播放范围后才允许播放
```
播放前必须准备足够安全的初始 buffer。
实时 PCM Render-Ahead Buffer 的用户可调目标由 §13.14.6 规定。
### 13.19.2 buffer 描述单位
播放预渲染 buffer 的系统级描述以音乐时间为主：
```text
tick
小节
四分音符
```
播放系统内部可以根据 Tempo Map 将预渲染音乐长度转换为实际秒数 / sample 范围，但语义要求仍以音乐时间范围为准。
初版正式实时合成与 Render-Ahead producer 的最大工作 block 固定为 `256 frames`。最终短块、事件边界前的短块和任务结束前的短块允许小于 256 frames，不得为凑满 block 越过事件或硬结束边界。

需要区分：
```text
音乐语义预调度范围：tick / 小节 / 四分音符
Render-Ahead PCM 容量：毫秒，按设备实际采样率换算为 frame
Device Buffer Request：毫秒，最终实际值由设备决定
内部固定工作 block：256 frames，不向用户暴露
```

Render-Ahead ring 容量不是固定 block 数，而是根据用户设置按以下规则换算：
```text
capacityFrames = ceil(actualSampleRate × bufferMilliseconds / 1000)
```
换算必须使用可检查溢出的整数运算；不得向下取整到短于请求时长，也不得为了对齐 256 frames 而静默扩大或缩小用户请求。
如果合法的低采样率与最小 buffer 设置使 ring 容量小于 256 frames，则 producer 本次实际工作块等于 ring 容量；`256 frames` 是正式最大值，不得以固定块为由拒绝合法 buffer 设置。
### 13.19.3 compiled result 与播放 buffer
播放 buffer 是 compiled result 之上的消费者缓存。
规则：
```text
compiled result 变更会使相关播放 buffer 失效。
播放 buffer 不是 compiled result 本身。
播放 buffer 不得独立保持旧语义直到用户手动刷新。
```
### 13.19.4 buffer underrun
播放中如果预渲染 / 调度 buffer 发生 underrun：
```text
不直接停止播放。
状态栏显示性能 / buffer 警告。
播放光标与声音输出在当前位置等待。
后台继续预渲染。
必须等到足够的音乐时间范围预渲染完成后，才恢复播放。
```
等待期间主状态仍为 `Playing`，内部子状态为：
```text
Playing.Buffering
```
Buffering 期间：
```text
允许 Stop。
允许跳转到新 tick。
跳转视为停止当前等待并从新 tick 重新冷启动播放。
保持音频静音，不循环最后一个 audio buffer。
恢复播放时从等待发生的同一 tick 继续，不按墙钟时间跳过音乐。
不改变“本次播放开始 tick”。
```
### 13.19.5 Buffering 恢复目标
underrun 在失败音乐位置 `F` 锁存。恢复终点 `R` 按自然小节计算：
```text
F 位于自然小节起点：候选区间是当前完整自然小节。
F 位于自然小节中途：候选区间是当前小节剩余部分加下一个完整自然小节。
```

候选区间随后以实际播放终点和从 `F` 起 `16` 个四分音符裁剪；`16` 个四分音符上限优先。Time Signature 中途变化立即开始的新自然小节必须由第 4 章统一小节映射提供，播放系统不得另建不一致算法。

只有完整 `[F, R)` 已经生成、校验并作为同一 generation 连续可读后才恢复播放。不得每获得约一个 Render-Ahead 窗口就恢复一次，也不得形成“渲染短块—播放短块—再次静音”的不规则断续。
### 13.19.6 连续 underrun
短时间内连续发生 underrun 时：
```text
仍继续采用等待预渲染再播放。
不自动进入 Error。
```
### 13.19.7 可等待性能不足与不可恢复错误
预渲染失败应区分：
```text
可等待的性能不足：进入 Buffering，等待后继续
不可恢复错误：进入 Error，Stop 清理
```
可等待性能不足例子：
```text
CPU 暂时跟不上导致 buffer underrun
后台预渲染队列暂时落后
短时间磁盘 / 系统调度抖动
```
不可恢复播放错误例子：
```text
Mapping Function Expression 求值失败
BASSMIDI Stream 创建失败
SoundFont 或 SFZ 依赖加载失败
输出设备丢失且无法恢复
compiled result 与播放缓存一致性校验失败
```
### 13.19.8 Buffering 与工程耗时
工程总耗时细则不在 第 13 章《播放与预览》 展开。
按第 3.6.4 节的初版累计规则：
```text
播放 Buffering 期间，Project 仍处于打开状态。
Buffering 时间计入工程总耗时。
```

### 13.19.9 活动音频线程零托管分配

Preparing 阶段必须完成正式播放所需的 buffer、队列、事件批次和工作区分配。进入 Playing、Preview Playing 或 Buffering 后，以下音频活动线程和热路径不得产生托管堆分配：
```text
设备 callback
实时事件调度
BASSMIDI 拉取 / 合成协调
多 Port 混音
Master Volume / Limiter
Render-Ahead buffer 搬运
跨进程命令 / 状态共享内存热路径
Buffering 补充路径
实时预览的对应音频路径
```

固定工作区与 ring 必须在 Preparing 一次分配并重复复用。音频子进程内部使用一个有界 SPSC PCM ring，ring 的 frame 容量服从第 13.19.2 节的毫秒换算，不另设固定 block 数；256-frame producer 工作区独立于 ring 容量。实时 PCM 不跨进程传输。主进程与子进程间的运行时命令和状态使用固定版本、固定布局、有界的二进制共享内存协议；命令生产和消费热路径不得分配托管对象，不得使用 JSON、文本协议或逐消息对象反序列化。所有内存必须有明确上限、所有权和释放时机。

Preparing、Stop 清理和 Finalizing 可以产生托管分配。与音频后端同进程的 UI 或其他非音频线程允许分配并触发进程级 GC；该 GC 本身不构成“音频活动线程产生托管分配”的验收失败，但 callback deadline miss、underrun 或爆音仍按运行期性能问题记录。

### 13.19.10 Segment/Unit/Root 音频缓存层

初版正式缓存分为：
```text
Project revision / canonical range cache
Compiler-internal Segment / Unit compiled fragment cache
Segment / Unit raw PCM tile cache
Pure MIDI Root merged fragment/checkpoint cache
Pure MIDI Root raw PCM tile cache
Playback span cache
Render-Ahead ring
```

Segment/Usage/Root raw PCM 位于 Mute/Solo、Playback Master Volume 与 Limiter 之前。Playback span 对当前 audible Track 集合做确定性求和，再统一应用 Master 与一个全局 Limiter。Limiter 不得分别作用于每个 Unit 或 Segment。

同一 Pure MIDI Root 的 Track，以及同一 Event Instrument Usage 的 Logical Track，必须分别先按 global Arrangement order 与 canonical execution order 合并，再由一个共享 Unit synth stream 生成 PCM；严禁按成员 Track 分别合成后相加。共享组 PCM 同样位于 Mute/Solo 后缀重建结果与全局 Master/Limiter 之前；Pure MIDI 的详细 key、dirty range 与 checkpoint 收敛规则见第 23.10 节，Logical Usage 使用同构的 group key 与连通区间规则。

相同 Project semantic revision、CompileContext、范围和完整 cache key 的 exact replay 命中时，不得再次进行语义编译或 BASSMIDI 合成。Pure MIDI Root/Segment 的正式可听内容 identity 必须确定性覆盖实际 Direct Note / Channel Event 字段、分页源 fingerprint 与 copy-on-write 删除/替换/新增 delta；集合 `Generation`、编辑次数或仅 stable ID 不得代替内容 identity。跨范围复用必须把范围冷启动上下文纳入 key；不得把含范围前持续 Note 的连续 PCM 切片冒充从中途冷启动的结果。

精确 Root/Unit PCM 命中还必须在 rolling event producer 查询 canonical/source pages 之前形成 source demand schedule。完整由 exact PCM 覆盖的 owner 在相应 frame range 内不得查询、排序或通过 IPC 发送其 MIDI events；混合 hit/miss 只为 miss owner 生产事件。Mute/Solo/monitoring 一旦使某个 cached owner 需要实时重建，必须从命令生效时的实际可听 frame 重建事件 suffix，并在本次 playback generation 后续保持 synthesis bypass；不得因过早丢弃事件而产生不可恢复的未来缺口。

上述重建不得截断正在被 Worker 读取的 event stream。Producer 必须把新 suffix 追加为新的 event generation，并原子发布 generation ID、base record offset、committed count 与 through frame；旧 generation 在 Worker 显式 Seek 到新 generation 前继续可读。Reader 发现 generation 已变化时必须停止装载旧 suffix 的新增记录，只有在 Seek 获得同一代的稳定快照后才能清空 ring 并切换。这样 Monitoring 即使发生在 producer 已因 exact PCM 命中而跳过数秒事件之后，也必须从可听 frame 恢复完整 MIDI 需求，不能把已跳过区间留成静音缺口。

Reader feeder 读取 published generation、committed count 和 generation-local loaded count 时，必须与 `Seek` 切换 active generation、base offset、loaded count 和 ring cursor 使用同一互斥域。不得在线程锁外冻结旧 published snapshot、等待 `Seek` 完成后再把该旧 snapshot 与新 generation 计数混算；这种竞争必须由结构消除，而不是在 fault 后重试或吞掉错误。

Monitoring generation 可以在 rewind frame 处先发布空 prefix，再由 Producer 渐进生成到实际 audible frontier。Reader 的 `Seek(sampleFrame)` 因此不能只二分 Seek 当时已经 published 的记录；`sampleFrame` 必须成为该 active generation 的持续下界。之后才 appended、但 frame 仍小于该下界的记录必须计入已读取 file prefix、不得进入 reader ring；到达下界后的记录才允许发布给 renderer。否则 renderer 会在当前 render position 收到过去事件并 fault。该过滤不允许删除等于下界的事件，也不能推进越过尚未完整读取的同 frame suffix。

render-ahead worker 的故障路径必须保留 source 抛出的异常，或区分 source `Fault`、invalid pull result 与 destination write rejection；Worker 报错必须同时包含该原因和正式 renderer fault。不得仅报告通用 ring fault，从而丢失可定位信息。

缓存失效至少服从：
```text
Segment 内容：从最早可证明 causal dirty tick 起；无法证明时从 Segment 有效起点起。
Pure MIDI 数据：只使对应 normalized MidiSegment fragment 和所属 Root 从最早受影响 tick 起 dirty；其他 Root 与 Logical Unit 不得连带失效。
Event Instrument / Mapping / Lifecycle：失效所有引用的 Segment/Unit。
Tempo：失效局部 Tempo 投影改变的 sample-domain entry 与时间放置。
SoundFont 列表缓存身份、采样率/格式、native baseline、voice policy：失效全部相关 Unit PCM。
Mute/Solo：保留 Unit PCM，建立新 playback span generation。
Master/Limiter：保留 Unit PCM，失效相关 playback span。
设备变化且实际格式相同：保留 device-independent Unit raw PCM；重建设备连接和 device-bound generation。
```

### 13.19.11 Session cache 存储与配额

音频缓存不进入 `.midora`。初版缓存只在当前 Project 打开 session 内有效，不跨会话复用；Project 关闭时删除本 session 的已知缓存条目。已完成条目在 Project 打开期间不做 LRU 驱逐。

Application Preferences 包含：
```text
Maximum Reusable Audio Cache Bytes：默认 16 GiB，范围 0..Int64.MaxValue
```

Audio Cache Root 不再是可编辑 Preference，固定为 `<ProgramRoot>\.tmp\AudioCache`。ProgramRoot 启动能力探测统一拒绝相对路径、UNC、网络/可移动卷、reparse-point root 与缺少必要事务能力的目录。程序只能管理 root 下由当前版本 manifest 标识的 `session-*` 子目录；不得递归删除 root 或未知文件。

主应用成功取得单实例所有权后，以及新 audio-cache session 激活前，必须自动扫描并 best-effort 删除上次异常退出遗留的、当前版本 manifest 可识别且未持有活动独占锁的 `session-*` 直接子目录。删除失败不得阻止应用启动或建立新 session，后续 session 激活必须重试；仍活动、manifest 缺失/不匹配、路径不是 root 直接子项或属于 reparse point 的目录一律保留。自动清理不得扫描、删除或重建 root 本身，也不得把未知内容当作 Midora 缓存。

quota 为 `0` 时不保留 reusable entry，cache miss 每次实时渲染。配额满、空间不足或普通 reusable 写入失败时：
```text
显示 AudioCacheRetentionDisabled Warning。
停止创建新的 reusable entry。
已完成 entry 继续可读。
cache miss 现渲染，不直接使播放失败。
```

Buffering 完整恢复区间使用独立于 reusable quota 的 transient recovery spool；消费或 Stop 后删除。系统必须分别报告 reusable 当前占用/上限、transient 当前/峰值和 retention Warning。若 spool 不可用且预留 RAM 也不足以容纳完整恢复区间，在 `F` 受控 Stop 并报告 `AudioRecoveryStorageUnavailable`，不得退化为短块断续播放。

完整 tile 写完并校验 checksum/generation 后才原子发布。WASAPI callback、BASSMIDI render/mix、ring 搬运线程不得做 cache 文件 I/O。损坏或半写 entry 必须隔离并重建，不能作为命中。

realtime plan 必须为全部 Pure MIDI Track（无论其内容由内存集合还是分页源承载）建立 `Track source → Root cache owner` 绑定。若某 Source 在播放开始时已被 Mute/Solo 过滤，或运行期 Monitoring 使其 owner 进入 synthesis/cache bypass，则本次 generation 中未被完整写出的 owner entry 不得列入 completed-key 集合、不得发布、也不得因此禁用整个 session 的 reusable retention。缓存发布队列发现 retention 已因另一项明确原因禁用时只跳过新条目，不得把原有 write-failure/preference 状态覆盖成 quota-full Warning。

缓存 miss 的 PCM 以 16,384-frame block 直接顺序追加到当前 reusable generation journal；不得先形成完整随机写 sparse spool，再为发布复制一遍完整 payload。每个 block 在追加前计算并写入 checksum，journal 结构、block index/count/length 与 completed-key集合经校验后，完整 generation 通过同卷原子移动和索引提交变为可命中 Pack。未完成 generation、缺失/重复 block、任务取消或崩溃留下的文件只是不在索引内的 dead/orphan bytes，由后续重整回收；任何部分 block 均不得单独命中或被解释为可恢复 BASS voice state。

播放期间允许 journal writer 对专用非音频线程施加有界背压以保证稳定产生缓存；WASAPI callback与BASS native decode线程仍不得直接做磁盘 I/O。必须分别记录 journal 已提交 entry/live bytes、cache read wait 与 cache write wait，使事件生产落后、缓存读取等待和缓存写入背压可区分。

### 13.19.12 约 200 ms 性能基准

实时播放和预览的端到端延迟以约 200 ms 作为性能测试基准。测试路径尽量覆盖：
```text
事件在视觉 / 调度语义上应生效或交互预览触发被接受
必要编译与准备
共享内存控制 IPC
事件调度与渲染
Render-Ahead 和设备 buffer
对应样本提交到 WASAPI 输出端点
```

外部 DAC、蓝牙设备自身额外缓冲、功放和声学传播不属于可稳定自动测量的软件边界，可以在专项人工测试中另行记录。

该 200 ms 是性能回归与架构选择基准：
```text
不是用户可调 Target Latency
不决定 Preparing / Playing 成败
超过基准不自动阻止播放
应记录测试失败、性能回归或运行期性能诊断
```

### 13.19.13 滚动事件准备与 IPC 水位

§13.19.1 的“预调度”包括 canonical range cursor、tick→sample 投影和跨进程滚动事件流，不只包括最终 PCM。实时播放不得先建立、hash、写出或让 Worker 读取整个播放范围的 sample-domain event plan 后才开始填充 Render-Ahead PCM。

固定水位为：

```text
Startup window     2.00 seconds from the current playback cursor
Low watermark      0.75 seconds of verified future event coverage
Resume watermark   2.00 seconds
Target High        6.00 seconds
```

Preparing 只等待：

```text
the range-start non-Note state restore/checkpoint
every active Unit/Root committed event record needed through Startup
the existing Startup PCM requirement
```

随后 canonical range reader、sample projection、rolling event producer、Worker event consumer、BASSMIDI renderer、Pack writer与设备消费并行滚动。一个超长Segment只需要其开头窗口；距离当前光标超过Target High的Segment数量、Note数量、event数量和source page bytes不得决定启动等待。

初版 rolling event transport固定为每个session一个append-only data file和一个named memory-mapped control block。Data record固定24 bytes：

```text
Int64 sampleFrame
UInt32 packed MIDI message
Int32 monitoring source index
Byte zero-based Port
7 reserved zero bytes
```

主进程每次只查询250 ms sample窗口，以最多16,384 records的writer batch顺序追加；写完并flush当前窗口后，才通过control block的single-writer seqlock原子发布`committedRecordCount / committedThroughFrame / state`。偶数sequence表示稳定snapshot，奇数表示发布中；Reader只接受前后相同的偶数sequence。Worker不得读取`committedRecordCount`以外的文件后缀，也不得把`committedThroughFrame`与旧record count组合。

Worker reader使用262,144-record固定ring，并以最多16,384-record batch填充。只有已加载全部当前committed prefix时，reader才能把`SafeThroughFrame`推进到published through frame；若当前committed prefix大于ring可用容量，reader必须把最后一条已装载record的frame作为单调递增的partial safe frontier。该partial frontier是排他的PCM边界：它只证明更早frame的全部事件已装载；renderer可以推进到该frame并在Buffering期间分批提交该frame当前已装载的事件以释放ring，但在完整同frame后缀装载且safe frontier继续前进前，不得生成该frame及其后的PCM。该规则也适用于单个250 ms committed窗口超过整个ring的情况，reader和renderer不得因等待完整窗口与等待ring空间而互锁。缺失/截断record、倒序frame、非法Port/message/source、损坏control snapshot或producer fault是结构化任务故障。

Data file只属于session临时存储，允许随实际访问/播放过的事件范围线性增长，但文件数固定为一；停止/释放session后删除。磁盘空间或I/O失败不得静默丢事件。Seek在已提交prefix按frame二分定位；未访问的远处Project事件不提前写入。该transport不把整Project事件数写入MDAP，也不把固定整文件byte/event上限作为Project容量限制。

当事件生产暂时落后时，producer必须在尚未消费的安全frontier等待并按既有Buffering规则处理。离线音频渲染和MIDI导出可以顺序跑完整范围，但同样必须通过range cursor与有界外部merge消费，不得回退到整项目数组。

rolling producer 必须记录 queried window、emitted record、完全被 exact PCM 抑制的 window/source 数、produced/requested through frame 与 producer lag。指标只用于运行期诊断，不进入 Project、canonical fingerprint 或缓存 key。
---
## 13.20 播放运行期提示与资源概要
以下内容属于播放运行期状态 / 性能提示，不进入 Project 编译诊断：
```text
buffer underrun
clipping
limiter activity
输出设备临时不可用
后端初始化失败
BASSMIDI runtime error
```
播放错误、underrun、clipping、Limiter activity 均不标记 Project 已修改。
播放运行期日志不保存进 Project 文件。
点击播放前 / Preparing 阶段，可以显示本次播放 compiled result 的资源概要，例如：
```text
使用 Port 数
峰值 Channel Unit 数
播放范围
Track 选择
是否启用 Limiter
```
资源概要只是播放信息，不作为 Project 诊断。
播放前不强制弹出资源概要窗口，可在面板 / 状态栏显示。
---
## 13.21 Event Instrument 预览
### 13.21.1 复用编译与播放管线
Event Instrument 预览必须走：
```text
临时 Event Instrument Preview CompileContext
→ canonical compiled result
→ 播放系统消费
```
不得直接手写 MIDI 事件发给 BASSMIDI。
Event Instrument 预览产生的 Channel Group 只属于本次预览任务。
规则：
```text
不写回 Project。
不影响正式编译资源缓存。
不保留分配结果供正式播放复用。
```
### 13.21.2 预览参数
Event Instrument 预览面板初版允许临时设置：
```text
previewPitch
previewVelocity
previewGateLength
previewTempo
```
这些值：
```text
不写入 Project。
不进入 Undo / Redo。
不标记 Project 已修改。
只作为本次预览 CompileContext 的临时输入。
```
默认值：
```text
previewPitch = Event Instrument Root Note
previewVelocity = 100
previewGateLength = Event Instrument Template Length
previewTempo = 当前播放光标所在 tick 的有效 Tempo
```
### 13.21.3 previewTempo
Event Instrument 预览不绑定项目时间线范围，但可以借用当前播放光标处 Tempo 作为默认试听速度。
规则：
```text
打开预览面板时，previewTempo 默认取当前播放光标处有效 Tempo。
用户手动修改 previewTempo 后，后续预览使用用户设置值。
用户可通过“重置为当前光标 Tempo”之类操作恢复跟随当前播放光标 Tempo。
```
Event Instrument 预览只借用当前光标 Tempo，不借用当前光标 Time Signature。
初版 Event Instrument 预览只使用固定 previewTempo，不支持预览过程中 Tempo Map 变化。
### 13.21.4 预览与 Conductor Track
Event Instrument Library 直接预览某个 Event Instrument 时：
```text
不绑定项目时间线范围。
不使用 Project End Marker。
不使用普通 Marker。
不使用 Segment 位置。
不使用 Track 上下文。
默认 Tempo 可取当前播放光标处 Tempo。
```
更准确地说：
```text
Event Instrument 预览不参与项目时间线编译；
但可以借用当前播放光标处 Tempo 作为默认试听速度。
```

Event Instrument standalone Preview 不应用 Definition 的 `Pre-Roll Ticks`；预览任务把模板 tick 0 直接放在自身 preview origin，等效使用 `Pre-Roll Ticks = 0`。该规则避免一个不绑定 Logical Segment/Logical Note anchor 的试听入口虚构负时间或额外等待。SubVoice standalone Preview 同理。
---
## 13.22 Event Instrument 虚拟键盘预览
### 13.22.1 键盘显示
Event Instrument 预览面板需要提供一排虚拟钢琴键盘。
系统级要求：
```text
键盘使用简单全高黑白键即可。
键盘高亮标记当前 Event Instrument Root Note。
键盘可以选取显示范围。
键盘可以缩放。
```
显示范围 / 缩放只影响 UI。
合法可预览 pitch 仍为：
```text
0–127
```
### 13.22.2 鼠标触发语义
在某个键上：
```text
鼠标左键按下 = Gate Start
鼠标左键松开 = Gate End
```
鼠标按下时立即开始发声。
按住期间视为 Gate 仍未结束。
松开时向预览任务发送 Gate End，之后按生命周期规则进入：
```text
Release
Tail
Reset
预览任务结束
```
### 13.22.3 previewGateLength 的作用
`previewGateLength` 作为普通 Preview 按钮 / 一次性预览的默认 Gate Length。
虚拟键盘按住预览时：
```text
Gate Length 由实际按住时长决定。
previewGateLength 不强制键盘按住预览的 Gate End。
```
### 13.22.4 普通 Preview 按钮
除了虚拟键盘按住预览，面板还应允许一个普通 Preview 按钮。
普通 Preview 按钮使用当前：
```text
previewPitch
previewVelocity
previewGateLength
previewTempo
```
执行一次固定长度预览。
### 13.22.5 单键限制
初版虚拟键盘只允许单键预览。
不支持多个键同时按下并同时预览多个 Event Instrument Instance。
当已有键盘预览活动时，按下新键前必须释放旧键，或新键替换旧键并清理旧预览。
具体 UI 行为由 第 17～20 章的 UI 与交互规格 或实现设计阶段细化。
### 13.22.6 velocity 规则
面板提供复选框：
```text
固定 velocity 预览
```
默认：
```text
固定 velocity 预览 = 开启
```
开启时：
```text
鼠标垂直位置不影响 velocity。
预览使用面板上的 previewVelocity 值。
```
关闭时：
```text
鼠标按下位置越靠近键盘顶部，velocity 越低。
鼠标按下位置越靠近键盘底部，velocity 越高。
键顶部 = velocity 1。
键底部 = velocity 127。
线性映射并四舍五入到整数。
```
velocity 在 Gate Start 固定。
鼠标按下后在同一键上上下移动，不会改变已触发 velocity。

### 13.22.7 held Preview 的因果 Gate

以下初版交互统一属于 held Preview：

```text
Event Instrument / SubVoice 虚拟键盘按住预览
Segment Editor 左侧 Pitch Ruler 琴键按住预览
```

它们必须复用同一套 Gate Start / Gate End 控制、Preview CompileContext、canonical 生成与正式实时音频链，不得为钢琴卷帘另写裸 MIDI 试听路径。

Gate Start 时最终 Gate Length 尚未知，因此：

```text
MappingContext.gateLength = Int64.MaxValue
```

`Int64.MaxValue` 只表示 Gate 尚未结束。Gate End 到达后冻结实际 Gate Length，并固定从 producer 尚未渲染的第一个 sample frame 起影响后续输出；不得回写已消费或已进入 Render-Ahead ring 的 PCM，不得用 `previewGateLength` 或预测值冒充最终值。保持用户当前 Render-Ahead 设置，不为 held Preview 静默改用更小的专用缓冲。

从 Gate End 输入到对应 Release 生效的延迟必须计入并报告预览交互延迟；最坏情况允许包含当前 Render-Ahead。该因果结果不承诺与事后使用最终 Gate Length 执行一次固定长度预览完全等价。

Pointer capture 丢失、窗口失焦、手势取消、预览错误、Stop 或预览对象失效时，必须进入确定的 Gate End / Reset 清理，不能遗留活动 Note、Channel 状态或预览任务。
---
## 13.23 SubVoice 预览
单独预览某条 SubVoice 时：
```text
默认触发音高使用该 SubVoice 的 Effective Root Note。
虚拟键盘高亮该 SubVoice 的 Effective Root Note。
```
SubVoice 预览仍以所属 Event Instrument 为上下文。
规则：
```text
不允许 SubVoice 脱离 Event Instrument 单独编译。
不允许直接裸发 SubVoice MIDI 事件。
仍走 Event Instrument 生命周期、Mapping、Preview CompileContext 和临时 Channel Unit 分配。
只是预览输出过滤到该 SubVoice。
```
---
## 13.24 Segment 预览
### 13.24.1 范围
Segment 预览默认范围为该 Segment 当前有效裁剪窗口。
不预览裁剪窗口外隐藏内容。
Segment 预览只播放被预览 Segment 所在 Track / Segment。
不播放同时间范围内其他 Track。
### 13.24.2 与 Mute / Solo 的关系
当用户明确预览某个 Segment 时：
```text
忽略当前 Track Mute / Solo 状态。
显式预览对象应发声。
```
但以下情况仍不能预览：
```text
Track 未绑定 Event Instrument Usage，或 Usage / Definition 引用断裂
Segment 预览编译失败
无有效 SoundFont
播放后端错误
```
### 13.24.3 项目时间绑定
时间线上的 Segment 预览应绑定项目时间上下文。
Segment 预览绑定项目时间时：
```text
包含 Conductor Track 上下文。
使用实际 Tempo Map。
包括 Segment 范围内 Tempo 变化。
恢复起点前全局状态与非 Note 状态。
```
Segment 预览不提供临时 Tempo 覆盖。
Segment 预览始终使用项目实际 Tempo Map。
### 13.24.4 预览光标
Event Instrument 预览不改变主时间线播放光标。
Segment 预览也不改变主时间线播放光标。
Segment 预览播放时，UI 可以显示独立的 Segment 预览光标，但不改变主播放光标状态。

### 13.24.5 Segment Editor 钢琴卷帘交互预览

Segment Editor 初版必须提供：

```text
左侧 Pitch Ruler 琴键按住预览
```

Pitch Ruler 预览使用当前 Segment 所属 Logical Track 绑定的 Event Instrument；绑定缺失、损坏、不兼容或无法编译时，按普通 Preview 失败规则报告，不得改用通用钢琴音色、裸 SoundFont preset 或其他 Event Instrument 代替。

Pitch Ruler 预览：

```text
鼠标左键按下目标琴键 = Gate Start
鼠标左键松开或交互取消 = Gate End
pitch = 被按下琴键的 MIDI note number
velocity = 当前 Event Instrument 预览 velocity
实际 Gate Length = 在本次固定 previewTempo 下由按住时长换算的 tick 长度
不创建或修改 Project Note
```

Pitch Ruler audition 不创建 Logical Segment Instance，因此不应用 Event Instrument `Pre-Roll Ticks`，按等效值 0 从预览 Gate Start 展开模板。它仍使用当前绑定 Definition 的其他正式生命周期与 Mapping 语义。

单音符放置手势不启动声音 Preview；只使用第 18、20 章规定的虚线视觉草稿。该视觉草稿不创建额外 Project 对象，不单独进入 Undo / Redo。Pitch Ruler 点击没有 Project 编辑副作用，只报告预览不可用。
---
## 13.25 空项目播放
空项目在有有效 SoundFont 的情况下允许点击播放。
行为：
```text
进入播放流程。
无音乐输出。
可按 Conductor Track 时间状态运行。
如果默认范围为零长度，则立即进入 Stop 清理流程。
```
无 Enabled SoundFont 时仍不能播放。
---
## 13.26 初版明确不支持的功能
初版播放系统不支持：
```text
Pause
Metronome / Click
Count-in
Scrubbing
实时 MIDI 输入录制
播放倍率 / playback speed multiplier
多播放任务混合
多声道实时输出
分轨实时监听输出
主播放显式 Track 选择
```
说明：
```text
Count-in 指正式播放开始前的预备拍。
Scrubbing 指拖动播放头时实时搓音 / 试听。
```
这些功能若未来加入，必须单独定义与 compiled result、状态恢复、输出链、Mute / Solo、预渲染 buffer 和播放任务互斥的关系。
---
## 13.27 与音频文件渲染的边界
完整音频文件渲染规则由 第 15 章《音频文件渲染》 定义。
第 13 章《播放与预览》 只确认与播放输出链共享的系统语义：
```text
音频文件渲染不得复用运行中播放 buffer、活动 BASSMIDI 状态或播放设备状态作为结果。
音频文件渲染使用 Audio Render CompileContext。
音频文件渲染使用 Project 当前正式生效的 Playback Master Volume。
音频文件渲染不拥有独立输出增益，也不提供 Master Volume bypass。
音频文件渲染使用与播放一致语义的最终 Limiter。
初版音频文件渲染不提供独立 Limiter 开关或 bypass。
多 Port 音频先混合为 stereo，再经过 Master Volume 与最终 Limiter。
Track Mute / Solo 不影响音频文件渲染成品。
离线渲染不依赖实时播放设备、Windows 音量或设备 DSP。
```
整曲、按 Logical Track 分轨、普通 RIFF/WAVE、自定义文件采样率、范围、文件事务、取消和诊断均由 第 15 章《音频文件渲染》 定义，不在本章复制。
---
## 13.28 与 MIDI 导出的边界
播放系统不定义 MIDI 文件结构。
本章只确认：
```text
播放输出链、Playback Master Volume、Limiter、输出设备、Mute / Solo 均不影响 MIDI 导出数据。
MIDI 导出必须使用导出专用 CompileContext。
MIDI 导出不得复用运行中播放 buffer。
```
---
## 13.29 与 UI 章节的边界
第 13 章《播放与预览》 只定义播放系统级交互语义。
不定义：
```text
播放按钮布局
状态栏具体样式
虚拟键盘绘制细节
资源概要面板布局
Limiter activity 指示器样式
clipping 指示器样式
播放设备设置面板布局
buffer 设置和实际值面板布局
预览面板完整 UI
```
这些内容由 第 17～20 章的 UI 与交互规格 或实现设计阶段细化。
---

## 13.30 音频后端进程拓扑

初版正式音频后端固定为由主应用管理的单个内部音频子进程。主应用拥有 Project、Compiler、Canonical Compiled Result、UI 和任务协调；音频子进程独占 BASS、BASSMIDI、Limiter、Render-Ahead、BASSWASAPI、设备 callback 和文件专用 OutputDevice。主应用不得加载或持有这些正式音频后端的原生全局状态与 handle。

子进程必须遵守：
```text
它不提供 UI。
它不能独立打开或解释 .midora Project。
它只接收冻结的 canonical compiled result、已解析音频设置、必要 SoundFont 配置和控制命令。
它不得重新解释 Event Instrument、Mapping、Lifecycle、Segment 或资源分配语义。
实时 PCM 只在子进程内部的 Render-Ahead ring 与 WASAPI callback 之间流动，不跨进程传输。
运行时命令与状态使用固定版本、固定布局、有界的二进制共享内存 ABI；禁止 JSON、文本协议和逐消息对象反序列化。
Playing、Preview Playing、Buffering 与 Rendering 的命令 / 状态 IPC 热路径不得产生托管堆分配。
IPC 延迟和吞吐量计入 §13.19.12 的约 200 ms 性能基准。
子进程异常退出时，当前播放 / 预览进入 Error 并完成主进程侧资源清理；允许通过 Reset Playback Engine 重建子进程。
```

“接收冻结的 canonical compiled result”允许通过 §13.19.13 的 committed append-only event stream按窗口传输。MDAP整计划文件只允许用于有界预览、测试或兼容的小计划；实时整项目播放不得把一个固定整文件byte/event limit当作Project容量上限，也不得在每次播放前重复序列化并读取全部远处事件。持久Worker只消费当前session已提交prefix；主进程从当前光标窗口按需继续查询canonical range source。

共享控制 ABI v2 引入、当前 ABI v4 保持的 Worker 状态快照采用单 Writer seqlock。固定 header offset 68 的对齐 `Int32 statusSequence` 是状态发布代号：稳定状态必须为偶数；Writer 在发布 State、Fault、Position、Render Position、Underrun、callback/render-thread allocation、设备变化标志及 held preview plan generation 的完整集合前，以原子 compare-exchange 把当前偶数改为奇数，全部字段写完后再以 release 语义发布下一偶数。并发 Writer 或前一次发布中断形成的奇数序列属于协议故障，不能继续覆盖。

Reader 只能在第一次读到偶数序列时复制完整字段，并在第二次读到相同偶数序列后接受快照；否则无分配重试，最多 1024 次，耗尽后作为 IPC 一致性错误使当前任务失败。序列按 32-bit two's-complement 位模式自然 wrap，相等与奇偶判断不得改成有符号大小比较。ABI v3 在 offset 88 保存 held preview plan generation，并新增 producer pause、应用 generation 计划及 resume 命令；暂停期间 WASAPI 仍可消费既有 Render-Ahead PCM，Worker 只能在 producer 尚未渲染的 frontier 替换后缀计划。ABI v4 保持 header 与 16-byte command record 布局，并新增携带正 sample-domain `endFrame` 的 `BufferingRecoveryPrepare` 命令；Worker 不得从 tick/拍号自行推导恢复终点。Create 与 Open 只生成/接受当前 ABI v4，主进程和 Worker 不提供 v1～v3 混合版本回退。

初版音频子进程必须以 `win-x64` 独立 Native AOT、自包含发布，不允许在正式运行时依赖 JIT 编译，也不得生成或接受 `win-x86`、`win-arm64` 或 AnyCPU Worker 作为正式产物。Native AOT 不替代零分配、callback deadline、underrun、故障恢复和确定性验收。

初版正式 BASS 原生基线固定如下；版本码是各模块 `GetVersion` 返回的完整 32-bit 值，不只是 API 主版本：

| 文件 | 完整版本 | 版本码 | win-x64 DLL SHA-256 |
|---|---:|---:|---|
| `bass.dll` | 2.4.18.3 | `0x02041203` | `febb2cf1882d554c3a958280777da0b69f07de6e262df271de11c56e4a54afd4` |
| `bassmidi.dll` | 2.4.16.0 | `0x02041000` | `e04e334ca35dce657b11eb9dacc7561b9c1365c337c1c3abac90a36336405ee6` |
| `basswasapi.dll` | 2.4.4.1 | `0x02040401` | `6f0869c11431e01f759fbe1cd6080299c833c519eb8ab1feae12106907b1fbd1` |

仓库只保存正式 manifest，不保存这些 DLL。正式构建必须由操作员提供官方二进制目录，先逐文件匹配正式 manifest，再把三项 DLL 和 manifest 纳入 `win-x64` Worker 发布目录；缺文件、多文件、架构不符、任一 hash 不符或任一运行时完整版本不符都必须失败。机器上偶然存在的 DLL、PATH 搜索结果、供应商可变的 current/latest URL 或只匹配 `2.4` API 主版本均不得成为正式输入。

供应商 current-package URL 只允许在显式确认后生成 `releaseBaseline=false` 的本地开发候选。正式基线升级必须作为独立变更提交：固定新版本码与 SHA-256，重跑 Native interop、音频语义、逐采样确定性、实时/离线、性能和发布测试；不得自动跟随最新版。

进程内后端或“子进程合成、主进程 WASAPI”的混合链只允许作为开发期对照测试，不是正式消费者，不得由产品运行时回退或切换进入。

## 13.31 统一音色选择器试听

音色选择器试听使用独立干净 Project/受控编译上下文，生成正式 canonical 后进入已有 Preview→Master→Limiter 链。Bank/Program、目标模式和音符只取当前 draft；不套当前编曲的 Expression、Pitch Bend、Mapping 或共享通道状态，不改变项目、Root mode、主播放指针或当前音乐。

自动试听首次默认启用、Key=60、Velocity=100、Gate=500ms；这些字段保存为程序偏好。有限 Gate 通过正式 tick/sample 时序结束，不能由 UI Timer/Thread.Sleep 发 NoteOff。手按键使用 held-gate 预览窗口，松键结束 Gate。模式仅供本次试听，初值取可知目标模式；名称不保证该 preset 一定存在或发声。

每个弹窗具有显式试听 owner；最新选择取代旧请求，至多一个执行请求和一个可替换待处理请求。编辑提交、确认、取消和关闭先等待停止自有试听，禁止迟到结果重启。不能为了试听抢停普通 Project 播放。无 Enabled SoundFont、设备/加载错误只报告试听不可用，合法 draft 仍可提交；清理失败必须明确报告，不把未完成的 Stop 当作成功。
---
