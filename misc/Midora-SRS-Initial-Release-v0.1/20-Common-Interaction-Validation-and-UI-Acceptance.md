# 第 20 章 通用交互、验证与 UI 验收边界

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Timeline、焦点、选择、批量编辑、拖放、剪贴板、Context Menu、命名、搜索、状态呈现、快捷键、语言格式、偏好保存、窗口边界和初版基础可用性。

## 20.1 Shared Timeline Interaction Rules
### 20.1.1 时间上下文
必须区分：
```text
Project Timeline
Segment Local Timeline
Event Instrument Template Timeline
Envelope or non-tick axis
```
Project Timeline 使用 absolute tick；Segment Editor 使用 local tick；Template Editor 使用 template tick；Envelope 等非 tick 横轴不得伪装为 Project tick。
### 20.1.2 Cursor 与 Time Range
必须区分：
```text
Playback Cursor
Edit Cursor
Time Range Selection
```
三者可以共存，不能使用同一种视觉标记。
### 20.1.3 Ruler
Stopped 时：
```text
Click ruler -> set Playback Cursor
Ctrl+Click ruler -> set Edit Cursor, preserve Selection
Drag ruler  -> create Time Range Selection
```
Playing 时，单击 Ruler 按播放系统规则跳转。
Ctrl+左单击只改变蓝色 Edit Cursor，不 Seek、不清选；tick 使用所在 Project / Segment local / SubVoice template 上下文并按当前操作 Snap 定位。Ctrl 按下和指针起点在 Pointer Down 冻结；移动超过既有 drag threshold 时不作为该单击，也不降级为 Time Range。只读 All Tracks 保留其独立导航规则，不获得编辑游标。
初版不支持 Scrubbing；拖动 Ruler 不连续试听。
上述 Time Range 拖选不适用于 SubVoice 的任一 ruler，也不适用于 Logical/MIDI Segment 的底部 Velocity/Event/Parameter ruler；这些位置不创建时间范围。SubVoice 的右键 Time Range 命令亦禁用，对象框选和列表范围选择不受影响。

低水平缩放下，时间标尺标签必须以全局 0 / 全局一基小节序号确定抽样相位，不得从当前可视左边界重新贪心抽样。保持缩放平移时，仍在可视范围内的标签不能换成另一套序列。Segment 使用 Project tick offset 与正式拍号图，包含变拍号和截短小节；极密拍号变化允许进一步按全局锚定位置限密。只改变标签显示密度，不改变 Snap、音乐时间、网格语义或已经正确的普通 Tick 尺分支；极端 Tick 运算有界且不得按所有隐藏小节逐个遍历。
### 20.1.4 Grid 与 Snap
每个 tick 时间线同时具有相互独立的：
```text
Display Grid Subdivision
Operation Subdivision
Snap Enabled
```
Arrangement 使用独立设置。Logical/MIDI Segment 的通用编辑偏好按正式 Track ID 共享，不按 Usage/Root 或全 Project 共享；每个 SubVoice 按 Definition ID + SubVoice ID 独立。只共享设置值，不共享 TPQN、有效拍号、参考时间等解析上下文。Piano 与底部事件区的 Snap enabled/subdivision 各自独立。现行默认 Grid 为 Bar-only 且显示，Segment/SubVoice 的 Operation 为 `1/16`、Snap enabled，默认 Note 长度为 TPQN、Velocity 为 100。Arrangement 的 Operation 初值继续为 `1/8`。

B1 会话记忆还包括 Track 的横向缩放、纵向 key 高度、主 ToolMode、独立 ValueTraceShape、Lanes 显隐/最后有效高度及 List 显隐/宽度；每个 Segment 独立记忆滚动位置、活动 Lane、Lane 顺序/显隐/纵轴和 List 行位置。SubVoice 独立拥有上述全部适用状态。关闭 Tab 释放 VM/资源而保留纯描述；删除 owner 清除其自身描述，Undo 不自动重开或恢复已删除描述。普通切页/重开保留位置，显式导航按 §18.2.2 优先。纯状态不进入音乐 Modified/Undo/编译或 canonical，不触发全源重建。B1 不新增文件字段；关闭或替换 Project 清除新增记忆，后续 B2/B3 的跨重启扩展另行实施。

分数相对于全音符：`1/1 = 4 × TPQ`、`1/2 = 2 × TPQ`、`1/4 = TPQ`。实际 tick 步长统一为：
```text
max(1, Ceiling(TPQ * 4 * numerator / denominator))
```
用户可输入任意正整数分母的 `1/n`。预置至少包括：Bar、`1/1`、`1/2`、`1/3`（三连二分）、`1/4`、`1/6`（三连四分）、`3/16`（附点八分）、`1/8`、`1/12`（三连八分）、`3/32`（附点十六分）、`1/16`、`1/24`（三连十六分）、`3/64`（附点三十二分）、`1/32`、`1/48`（三连三十二分）、`1/64`、`1/128`、`1/256`。`Bar` 按目标 tick 的有效 Time Signature 计算当前小节长度。

Display Grid 只控制分割线。Snap 应用 Operation Subdivision；Snap Disabled 时有效操作步长为 1 tick。改变任一设置不修改或重新量化已有对象。

Arrangement、所有 Segment/SubVoice piano roll，以及所有 MIDI Event / Logical Parameter Lane 的 Snap ToggleButton 必须显示统一 Tooltip：`Enable/Disable Snap (A)`。

有效操作步长用于 Marquee 与 Time Range 的开始和长度、Edit Cursor 与 Playback Cursor 定位、Segment / Note 放置位置、Segment / Note Resize 的 delta（而不是最终总长度），以及 Segment / Note Move 的共享 delta（而不是最终绝对位置）。

创建 Note 时必须在 Pointer Down 冻结本次初始长度；Snap 只量化随后 Pointer Move 相对按下点产生的 delta，再把该 delta 加到冻结初始长度。未越过创建拖动阈值时保持原初始长度；向左缩短不得把结果突然压成 `1 tick`，而是在本次允许的最小长度处保持。例：初始长度 49 ticks、有效步长 192 ticks 时，未形成有效 delta 的结果仍为 49，第一格向右结果为 241。

Arrangement Segment、Logical Note、Direct MIDI Note 与 Template Note 的左/右边界 Resize 还必须使用本次手势开始时的有效操作步长作为最小结果长度；Snap Disabled 时该最小值为 `1 tick`。批量 Resize 继续对每个对象独立饱和，不得让最短对象限制其他对象的缩短量。若某个既有对象在手势开始前已经短于当前有效操作步长，则该对象本次最小长度保持其原长度，不得为了满足新吸附设置而反向扩长。拖动预览与正式命令提交必须使用相同最小值。

Segment、Logical Note、Direct MIDI Note 与 Template Note 的 `Alt + Left Drag` 是强制 Move 手势，仍使用当前有效操作步长。需要逐 tick 编辑时关闭 Snap；Alt 不再临时绕过 Snap。
### 20.1.5 Zoom 与 Pan
```text
Ctrl + Mouse Wheel  -> horizontal zoom around pointer time
Shift + Mouse Wheel -> horizontal scroll
Middle-button Drag  -> pan
```
不使用 `Space + Drag` 平移，因为 Space 在 Timeline Context 中用于 Play / Stop。
同一 Workspace 中共享时间坐标的 Ruler、Lane 和 Canvas 必须同步横向缩放和滚动。
在 piano roll 左侧 Pitch Ruler 上使用 `Ctrl + Mouse Wheel` 时只改变垂直音高缩放，并以指针下音高为锚点；不得同时改变横向缩放。

Piano Roll 的纵向视口必须限制在 MIDI pitch `0..127`，不得滚动到范围外。Velocity、Parameter 和 Event Lane 的纵向缩放与平移必须限制在其数值范围内；缩放后的滚轮步长按当前可见范围计算，不能使用固定大跨度。上述编辑器右侧必须提供与当前纵向视口双向同步的 Scrollbar。
### 20.1.6 工具模式
通用工具：
```text
Select
Draw
Split
Erase
```
只在支持的编辑器中显示。
Current Tool 在 B1 不跨应用重启保存；尚无会话记忆的 owner 默认 Select，有记忆时使用 Track/SubVoice profile，不因关闭重开 Tab 重置。主区与底部仍共享主 ToolMode，绘线形态独立记忆。

Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的工具按钮必须互斥，且始终恰有一个激活；再次点击当前工具不得清空工具状态。上述直接编辑视图中的工具语义固定为：

- `Draw`：空白处创建；已有 Segment 或 Note 的主体可移动、左右边缘可调整长度或 active crop window；
- `Select`：在 Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 中，单次左键按下始终从当前位置发起框选，即使起点位于对象上也不执行单对象点击选择；不得直接移动、Resize 或双击创建对象；既有双击导航不受该单击规则影响；
- `Split`：只在支持的对象上执行分割；
- `Erase`：删除命中的可删除对象。

上述工具按钮使用 Fluent System Icons：Draw=`pen`、Select=`select_object`、Split=`split_vertical`、Erase=`eraser`；Tooltip 分别为 `Draw (D)`、`Select (S)`、`Split`、`Erase (E)`。Arrangement 的 Split 图标固定为 `16 × 16`、在原按钮内水平和垂直居中，按钮外部尺寸不得改变。水平与纵向缩放按钮均使用 `zoom_out` / `zoom_in`，但必须通过所在位置和 Tooltip 明确区分缩放轴。

`Draw` 在空白处使用默认指针，在可直接编辑对象的主体和边缘分别使用移动与水平 Resize 指针；`Select` 使用十字指针，但命中对象时不改变为移动或 Resize 指针；`Split` 命中 Segment 时始终使用文本选择形 I-beam 指针。`Draw` 悬停在可直接编辑的 Segment、Logical Note 或 Template Note 上时，始终绘制低强调 transient 外轮廓，不要求按住 Alt；该轮廓不得重建或失效基础 raster tile。`Draw` 命中已有对象或执行移动/Resize 时隐藏创建预览；移动/Resize 的 transient 预览必须显示提交后的位置与长度。
### 20.1.7 Drag Preview
拖动必须显示：
```text
Original position
Preview position
Delta
Length
Snap target
Target Lane or Track
Validity
Important semantic impact
```
Pointer Up 一次提交；Escape 取消且不产生 Undo。
拖到视图边缘时自动滚动。
Draw 模式在可创建 Segment 或 Note 的空白位置悬停时，也必须显示包含位置和默认长度的虚线创建预览；非法 Segment overlap 预览使用错误色。

所有普通直接操作及 Selection 浮动工具发起的 Move/Resize，在手势激活后的每一帧都必须显示当前正式规则计算出的有效 delta：先应用 time lock、Snap、最小长度、Tick 0、Key/Value/Lane 边界及共同 delta 饱和，再格式化显示；不得显示尚未 Clamp 的原始 pointer delta。Note Move 显示 `Keys + Ticks`，Event/Parameter Point Move 显示 `Value + Ticks`，Arrangement Move 显示 `Lanes + Ticks`，左右边界 Resize 显示该边界的 `Ticks`；Copy+Move 也必须显示同一有效 delta。
### 20.1.8 边界
```text
Tick 0            -> hard left boundary
Project End Marker -> not an edit boundary
```
active crop window 外内容仍可查看、选择和编辑。
### 20.1.9 Follow Playback
Follow Playback 可由用户手动滚动或缩放临时中断。
### 20.1.10 播放期间
允许：
```text
Scroll
Pan
Zoom
Selection for inspection
Project Ruler jump
```
禁止：
```text
Draw
Move
Resize
Split
Delete
Cut
Paste Project objects
```
---
## 20.2 Global Focus Rules
### 20.2.1 核心概念
严格区分：
```text
Keyboard Focus
Selection
Primary Selection
Active Workspace
```
Active Workspace 由活动 Tab 决定，不因焦点进入 Workspace 内的 Properties 对话框而改变。
### 20.2.2 命令路由优先级
```text
1. Full Application Task Lock
2. Active Modal Dialog
3. Active Popup, Menu or Inline Editing Session
4. Focused Text Field or Mapping Expression Editor
5. Focused Workspace or Panel
6. Active Workspace
7. Global Project Command
8. Application Command
```
命令被前一级接收后不得继续传播。
例如：
```text
Delete in an object Properties text field
    -> delete text
    -> do not delete an Arrangement Segment
```
### 20.2.3 F6 区域导航
```text
F6       -> next main UI region
Shift+F6 -> previous main UI region
```
顺序：
```text
Global Toolbar
Active Workspace
```
隐藏或折叠区域跳过。
### 20.2.4 Tab
Tab / Shift+Tab 只在当前焦点范围内导航普通控件。海量 Timeline 对象不逐个成为 Tab Stop。
Mapping Expression Editor 仍是单行语义，Tab / Shift+Tab 用于离开控件并导航模态对话框，不插入缩进字符。
### 20.2.5 焦点恢复
Workspace 记住当前 Project 会话中的最近焦点位置。目标失效时回退到最近有效父区域。
后台验证、编译、播放、任务和 Status Bar 更新不得抢夺焦点。
模态窗口关闭后恢复到：
1. 启动 Dialog 的安全控件；
2. 原 Active Workspace；
3. 主窗口安全默认区域。
不得恢复到已删除或失效对象。

由 Timeline Surface 发起的模态编辑在关闭后，若原 Surface、Workspace 与 Project revision 仍有效，必须优先把键盘焦点恢复到该 Surface，使 `D`、`S`、`E`、`A`、`Ctrl+E` 等焦点敏感命令可继续使用；不得把焦点遗留在已经关闭的 Dialog、Lane ComboBox 或无关 Toolbar 控件。
---
## 20.3 Selection Model
### 20.3.1 Workspace Selection
每个 Workspace 独立保存：
```text
Object Selection Set
Primary Selection
Selection Anchor
Time Range Selection
Active Selection Scope
```
Active Workspace Selection 只属于该 Workspace。它可以驱动同一 Workspace 内的渲染、命令和显式打开的所属属性编辑器，但不得持续驱动一个跨 Workspace 的全局属性面板。
Diagnostics 和其他辅助列表保留自己的选择，不与 Workspace Selection 混合。
### 20.3.2 Context Object
Workspace Context Object 与子对象选择分离。
无子对象选择时，所属 Workspace 可以把 Context Object 用作 `Properties...` 的显式目标；不得因此建立或改变任何跨 Workspace 属性目标。
### 20.3.3 祖先与后代
同一 Selection Set 中不允许同时包含祖先对象与其后代。
### 20.3.4 异类对象
允许异类对象同时选择，但普通命令只有所有对象共同支持且语义一致的操作可用。Timeline 对象列表的显式类型子菜单按 §20.7.7 冻结子集；不得静默忽略不兼容对象。
### 20.3.5 单击
```text
Click unselected object -> replace Selection
Click selected object   -> preserve multi-selection and make it Primary
```
### 20.3.6 修饰键
Timeline Canvas：
```text
Ctrl+Click  -> toggle item
Shift+Click -> add item when range semantics are not defined
```
一维有序列表使用标准连续范围选择。
### 20.3.7 空白区域
```text
Left-click empty canvas  -> clear Object Selection only
Right-click empty canvas -> preserve Selection; full selection menu if non-empty, otherwise container/position menu
```
Time Range 不因空白左键自动清除。
### 20.3.8 Marquee
初版支持矩形框选，不要求 Lasso。
命中规则：与矩形相交即命中。
启用 Snap 时，框选的 Pointer Down 时间端点独立吸附并在整个手势中保持固定，只对 Pointer Move 端点继续吸附；向左和向右框选必须镜像一致，不得通过“变化中的起点 + 吸附长度”反算固定端点。移动端尚未越过相邻操作边界时，沿拖动方向覆盖至少一个完整有效操作步长。视觉矩形与最终命中查询必须共用该方向化范围。
```text
No modifier -> replace
Shift       -> add
Ctrl        -> add
Alt         -> remove
Ctrl+Alt    -> toggle
```
该框选修饰键表沿用 ADR-UI-023；不能套用一维列表的 Ctrl+Click toggle。右拖框选按 §20.7.9.1 在各主工具下可用，Velocity 框选对应正式 Note 而非新建 Velocity 对象。
### 20.3.9 Select All
`Ctrl+A` 只选择当前 Active Selection Scope，不存在“选择整个 Project”。
### 20.3.10 不可见对象
| 不可见原因 | Selection 行为 |
|---|---|
| Off-screen | 保留 |
| Collapsed parent | 保留 |
| Collection Search 隐藏 | 保留并提示 |
| Editor Content Filter 隐藏 | 从 Active Selection 移除 |
| 对象真正删除 | Selection 失效 |
active crop window 外对象保持可选择和编辑。
### 20.3.11 重叠对象
重叠对象默认选顶层。
```text
Alt+Click -> cycle overlapping objects
```
Context Menu 可以列出重叠对象供用户选择。
### 20.3.12 创建与删除后的 Selection
Create、Duplicate 和 Paste 成功后选择新对象。
Timeline Delete 后默认清空当前 Object Selection。
### 20.3.13 Object Selection 与 Time Range
二者独立共存，并提供显式相互转换命令；不得隐式互相覆盖。
### 20.3.14 播放期间
允许完整选择、框选和查看，但不允许修改 Project。
所有 Selection 状态均属于 Project Session UI State。
### 20.3.15 大规模 Selection 的地址与解析
稳定 ID 始终是对象业务身份。对于百万级 Timeline source，Selection 可以使用绑定到 `owner + source revision` 的 ordinal page interval/bitmap 与 sparse include/exclude 表达，不要求为每个对象建立 boxed ID、独立 WPF item 或完整 HashSet；少量选择可以继续使用 compact inline stable-ID path。

ordinal 只在被冻结的 owner revision 内有效，不得持久化、跨 revision 复用或代替 stable ID。命令、Properties、渲染和 Selection 恢复按需分页、流式解析 stable ID；revision 已变化时必须拒绝旧 descriptor 或重新建立选择，不得把旧 ordinal 静默解释为新对象。范围解析必须有界并周期检查取消，不能因框选、Shift 范围或 `Ctrl+A` 同步物化整个 source。
---
## 20.4 Multi-selection 与 Batch Editing
### 20.4.1 原子原则
```text
One user gesture
-> one valid batch result
-> one Project Undo operation
```
任一对象不兼容或结果非法时整体拒绝，不允许部分成功；第 20.4.4 节的 Note pitch 越界删除、第 20.4.14 节的 Humanize Tick 越 owner 硬边界删除，以及第 20.4.14～20.4.17 节明确规定的 exact-collision reducer，都属于相应命令的正式批量结果，不视为静默跳过或部分失败。
### 20.4.2 Primary Selection
Primary Selection 是 Snap、对齐和直接拖动的参考对象。
批量 Snap：
```text
Snap Primary Selection
-> apply one shared delta to all selected objects
```
不逐对象独立吸附。
### 20.4.3 时间移动
同一时间坐标系对象可统一移动。
异类选择通常需要显式 `Move in Time`。Arrangement 中混合选择的 Logical Segment 与 Midi Segment 共用 absolute Project tick，允许统一时间/Track delta 的移动与复制，以及统一边缘长度调整。跨类型 Track 的放置必须经过第 20.6.14 节的统一转换与损失确认，不得由视图隐式丢弃内容。

Arrangement 多 Segment 边界 Resize 的 transient preview 必须对全部选择应用与正式提交完全相同的 shared edge delta、逐对象最小长度、边界与 overlap 规则；不得只预览指针命中的一个 Segment，也不得以逐帧修改 Project 模型代替矢量预览。
### 20.4.4 Piano Roll Notes
多选移动保持：
```text
Relative time
Pitch intervals
Length
Velocity
```
时间、Track/Lane 与所属容器边界仍使用整组共同合法边界。普通移动 Logical Note、Direct MIDI Note 或 Template Note 时，pitch 使用用户请求的共同 delta；结果 pitch 小于 0 或大于 127 的 Note 直接从 Project 删除，其余 Note 保持共同 delta 和相对关系继续移动。该删除与移动构成一个原子 Project command 和一个 Undo；Undo 必须按原顺序恢复被删除 Note。不得把越界 Note 存入模型，也不得弹出逐 Note 错误 Dialog。

Note 的 `Ctrl+Drag` 复制仍按第 20.5.6.1 节使用选择集共同 pitch clamp，不删除源对象或生成部分副本。其他可移动 Timeline 选择若请求 delta 越过时间、Track/Lane 或所属容器硬边界，使用共同 clamp 后的 delta 使整个选择贴合边界。
多选边缘调整采用同一请求 Edge Delta；初版不做比例时间伸缩。缩短时每个对象独立在 `1 tick` 最小长度处饱和：某个较短对象先达到最小长度，不得限制其他较长对象继续应用同一请求 delta。调整左边缘时对象的右边缘保持不变，调整右边缘时对象的左边缘保持不变；时间、内容窗口和 Segment 不重叠等其他硬约束仍须满足，整批提交保持原子性并只产生一个 Undo。
### 20.4.5 数值编辑
必须区分：
```text
Exact Set
Relative Adjust
```
Enum 只支持统一设值，不支持相对数值 Delta。
Parameter Point 只有同 Definition、同类型、同值域和同语义时才能共同纵向调整。
SubVoice 数值 MIDI 点、Logical 数值参数点、Pure MIDI Channel Event 的纵向批移/复制批移必须保持整组形状：根据完整选区的最小/最大值求一个共同合法 delta，任一点先触边时整组停止，不逐点分别压平。预览、delta 提示和 Pointer Up 提交使用同一有效 delta。Direct Pitch Bend 使用 `0..16383` scalar、SubVoice Pitch Bend 使用 `-8192..8191`；必须在对应标量域调整后再编码，不能把主点高/低字节的增量套到其他点。目标 selector、Bank/Range 的未编辑分量、精确碰撞规则与 Undo/Redo 均保留。Enum 的相对纵向调整禁用，水平移动和精确设值仍可用。
### 20.4.6 Segment 跨 Track
保持 Track 相对间距，并保留全部内容和 crop 外数据。
同类型跨 Track 保留全部源内容，Midi Segment 可改变 parent Root。跨类型与混合选择的跨 Track 移动/复制遵循第 20.6.14 节；未确认不可转换数据损失时不得提交。
可能断裂的 Lane：
- 不自动修复；
- 不按名称匹配；
- 不删除；
- 在预览中显示影响。
### 20.4.7 对齐命令
初版有限支持：
```text
Align Starts to Primary
Align Ends to Primary
Set Same Length as Primary
```
初版不做：
```text
General Batch Rename
Proportional Stretch
Normalize
Randomize
Independent Snap
```
### 20.4.8 混合 Segment Properties 与变换

Arrangement 同时选择 Logical Segment 与 Midi Segment 时，`Properties...` 必须展示两类 Segment 语义一致的共同字段；Mixed 字段按第 17.4.3 节显式开始统一值并可还原。一次 `OK` 对全部目标形成一个原子 Project command。

同一混合选择还必须支持 Horizontal Flip 的“仅暴露内容”和“内容 + Segment”、Vertical Flip、Scale、Transpose 与 Batch Edit。Logical Segment 适配 Logical Note/Parameter Point，Midi Segment 适配 Direct MIDI Note/Event；命令只处理各自正式模型中与该操作语义共同的字段，不得把两类子对象互相转换。Segment overlap、范围、碰撞归并和越界删除仍按各专项规则验证；任一不兼容或非法结果使整批失败，成功只形成一个 Undo 并保持选择。

### 20.4.9 Delete
批量 Delete 只弹一次汇总确认。任何不可删除对象都使整体命令 Disabled。
### 20.4.10 Undo
一次批量手势只形成一个 Undo。Undo 恢复每个对象各自原状态；Redo 不根据当前 Grid 或 Snap 重新计算。
### 20.4.11 分页 detached edit transaction
百万对象编辑必须从 revision-bound source 按 page/chunk 流式读取，在与正式 Project 集合分离的 staging 中计算、验证、碰撞归并并建立结果 root。只有全部步骤成功且 owner revision 仍等于事务基线时，才允许用一次原子 root swap 发布；取消、表达式错误、资源上限、I/O 错误、revision race 或结果验证失败都只丢弃 staging，不得发布部分结果。

正式发布必须同时产生精确 owner/range/page change set 与 source trace，供 UI、selection projection、Full/Incremental Compiler 和缓存失效共同观察；各消费者不得通过重新扫描整个 owner 猜测修改范围。Undo/Redo 优先保存 immutable old/new root/page reference 与必要的小型映射，不保存两份完整逐对象图，也不重新执行随机数或表达式。

大型编辑默认执行预算为：page 4,096 records；每 256 records 检查取消；单页 decoded/encoded working buffers 合计 64 MiB；resident staging 64 MiB；owned spill 16 GiB；candidate/result 100,000,000 records。预算属于运行时 profile，不持久化；spill 只能写入 `<ProgramRoot>\.tmp\CompilerRuns` 的 owned run，失败、取消和 Dispose 必须回收本次 run。实现可采用更低的具体工具上限，但不得静默突破这些全局上限或退化为无界内存。
### 20.4.12 Selection 浮动工具
Draw / Select / Split / Erase 下，有效非空 Selection 均显示一个常量大小的浮动工具框。工具框保留 Pin/Follow、grip 手动拖位与 Move；Note 和 Arrangement Segment 还分别显示左边界 Resize 与右边界 Resize，Point 不显示 Resize。两种 Resize 复用既有 `ResizeStart` / `ResizeEnd` command adapter，与普通直接拖动共用 Snap、最小长度、Tick 0、隐藏内容、碰撞和一次 Undo，不建立第二套编辑规则。

Copy、Cut、Delete、Flip Horizontal、Flip Vertical、Scale、Transpose、Batch Edit、Humanize、Split、Join、Quantize、Properties 及 Deselect All 共 14 项常驻，逐项使用现有菜单命令的正式能力和相同图标语义。不适用项禁用；混合类型若支持类型子命令，应显式展示 `For Notes` / `For Events` / `For Instrument Changes`，不得静默丢弃其他选中类型。不能 Move 不代表必须隐藏可用 Delete。工具按钮区最多显示三行；按钮命中格为 27 DIP（15 DIP 图标四周各留 6 DIP），行间距 2 DIP、按钮区外边距 4 DIP；图标为 15 DIP，居中于按钮，并使用与菜单一致的平滑抗锯齿，不能继承时间线关闭抗锯齿的图形模式，优先使用既有 Fluent revision 的原生 16px Regular 资源，没有时缩放同款现有资源。Pin 两种状态均使用 Pin 图标，以背景区分；左/右边界分别使用 ArrowExportRtlRegular / ArrowExportLtrRegular，Move 使用 ArrowMoveRegular。第一行依次为 Pin、适用的左右边界、Move、Properties、Deselect All；第二行为 Copy/Cut/Delete/Flip Horizontal/Flip Vertical/Scale；第三行为 Transpose/Batch Edit/Humanize/Split/Join/Quantize。顶部内置常驻名称栏，hover 任一按钮立即显示名称，禁用按钮也可读，不依赖 ToolTip。Deselect All 使用 SelectObjectSkewDismissRegular，清空整个 Workspace 选择且不进入 Undo，播放/只读时仍可用。小窗口必要时内部换行/滚动，但可见按钮区仍不超过三行，名称栏与取消选择保持可达；禁止按对象数量创建控件或在每次 hover 全遍历选择。Inst. 浮动工具共用此尺寸与分组，保持其已有动作适用性。

播放/任务锁定沿用既有能力：允许选择、复制及适用的只读 Properties，禁止修改音乐。命令执行前再次验证 frozen owner / source / Selection revision。工具按钮或 grip 独占命中，不穿透为背景手势。

Follow 是每个新建 Timeline Surface 的默认状态：工具框跟随 Selection 包围框并贴边保持在 viewport 内可达；切换为 Pin 后，工具框锚定 Selection 的世界坐标，Selection 离开 viewport 时允许随之离开。顶部明确的 grip 在两种状态下都允许手动移动工具框。该状态在当前 Surface 生命周期内保持，但只属于 Session UI State，不进入 Project、Undo、canonical 或 `.midora`。

从浮动工具 Move 按钮开始拖动时，若 `Ctrl` 已按下且当前 Surface / 对象类型已有明确 Copy Drag 能力，则执行与普通直接操作相同的 Copy+Move，并按该类型既有规则选择幸存副本；Resize 不响应 Copy。没有既有 Copy Drag 能力的类型在 `Ctrl` 下为 Invalid，不得静默退化成 Move。Conductor 的 Tempo 复制已由第 18.7 节明确批准，服从下述专门边界；不得据此复制单例 Project End Marker。

混合 Selection 的 Move/Resize 仍需共同兼容；不兼容只禁用该命令。所有选择规模都显示第 20.1.7 节的有效 delta 与完整 Selection 变换预览：小选择复用普通 Draw 的即时矢量路径，大选择复用同一平移或 Resize raster tile。不得因超过即时矢量阈值而只显示 delta、隐藏预览或重新物化完整 Selection。

#### 20.4.12.1 Conductor 事件交互

Conductor Tempo 沿用 Event Lane 的左键 Free / Line / Horizontal 绘制形态、Shift 固定 tick 只改 BPM、Ctrl 复制拖动与右拖框选。Snap 开启按操作格点采样，关闭逐 tick；绘图是逐 tick 离散状态，不是连续 ramp。Time Signature、Key Signature、Marker 和 Project End 在各自 lane 上命中正式对象，不能拖动改变事件类型。Tempo、拍号、调号同 tick 后来编辑者覆盖，Marker 不归并；tick 0 Tempo/拍号不可删除或移动，但可原位改值。所有数值按正式语义校验，非法手势整体失败，不静默更改 BPM 的合法范围。

左侧列表与右侧两图共享选择。单击、Ctrl 切换、Shift 连续范围及拖动范围选择支持冷页后台解析；取消、源修订变化或新选择优先于旧请求。列表双击只打开该行对象的 Properties；Locate 定位选中对象。Ctrl+A 选择 Conductor 中全部正式事件；受保护初始状态及 End 单例规则仍有效。五种事件的复制、剪切、粘贴与删除统一服从已有专门命令，批量操作使用有界准备、进度和取消，一次成功发布对应一次 Undo。

#### 20.4.12.2 数值绘制形态

保留 Piano 与下部 Lane 共享的 Draw / Select / Split / Erase 主 ToolMode。在适用的 Event / Logical Parameter / Velocity / Tempo 区增加 Free、Line、Horizontal 纯图标形态组（PenRegular、LineFilled、SubtractFilled），非 Draw 禁用。左拖执行当前形态；普通命中已有点/柱优先原直接编辑，Alt+左拖强制当前形态，原 Shift 锁 tick / 单点行为不变。Line 在原点与终点之间采样；Horizontal 以原点 y 固定。切 D/S/E 不清除形态，D 仅回到 Draw。Note / Segment 不获得沿线创建，Inst. 固定 y 包装不获得数值绘线。

采样密度、选区过滤、tick/value 合法范围、碰撞、取消和单 Undo 继续使用同一正式操作。形态本身是会话显示/工具状态，不是 Music Source；Track profile 的共享及显式保存按后续已批准状态方案整合，A3 不私写既有 presentation schema。

### 20.4.13 受限数值工具表达式与 Preset

Timeline 批量工具使用独立于 Project Mapping Function 的受限数值表达式系统。非空工具表达式必须以 `=` 开头；8,192 scalar 上限作用于去掉该前缀并按工具规则 Trim 后交给受限编译器的 expression body。每个 profile 只暴露自己明确的变量 schema，不得访问 Project 对象图、文件、网络、进程、线程、环境、时钟或非确定随机源。

所有工具 profile 共用以下固定安全边界：

```text
Maximum source length: 8,192 Unicode scalars
Maximum syntax nodes: 512
Maximum syntax depth: 64
Result type: finite double
```

语法只允许数值字面量、profile 变量、批准的算术/比较/布尔运算符、条件表达式以及固定快照的纯数值 `System.Math` 成员。必须拒绝声明、赋值、递增/递减、lambda、delegate、对象/数组创建、索引器、语句、循环、递归、任意 API 与反射。多字段工具的 result-variable 依赖必须形成无环图；每条表达式只编译一次，对象循环中重复调用已建立的委托。

当前固定 profile ID 为：

```text
midora.tool.batch-note/v1
midora.tool.batch-event/v2
midora.tool.note-split/v1
midora.tool.generate-note/v1
midora.tool.generate-event/v2
```

变量 schema 固定为：

```text
midora.tool.batch-note/v1  : v0/v1, k0/k1, g0/g1, t0/t1, tr
midora.tool.batch-event/v2 : p0/p1, t0/t1, tr
midora.tool.note-split/v1  : i, tr
midora.tool.generate-note/v1  : i, v0/v1, k0/k1, g0/g1, t0/t1, tr
midora.tool.generate-event/v2 : i, p0/p1, t0/t1, tr
```

Batch profile 中 `*0` 表示该字段编辑前的值，`*1` 表示同一对象中经依赖图求得的新值，`tr` 是相对本次冻结 Selection 最小 tick 的编辑前相对位置。Note Split 中 `i` 和 `tr` 只有第 20.4.15 节定义的刀序号/上一刀位置语义，不得引入 Batch Edit 字段或其他隐式变量。

Profile ID/version、变量 schema、取整/值域契约与白名单是一个整体兼容边界；任一部分改变必须使用新 profile version。这些 profile 不是 Mapping Function ABI v3，也不进入 Project 或 `.midora`。

Tool Preset 保存在 `<ProgramRoot>\Data\Presets` 的对应工具分类中，每个文件必须包含 `presetSchemaVersion`、`expressionProfileId/version`、`toolKind` 和数值格式契约。加载本机或外部 Preset 时必须用当前 profile 重新执行完整语法、API、依赖和资源上限验证；不得因为 JSON 可成功反序列化就直接执行。Preset 不进入 Project、Undo/Redo 或 canonical fingerprint。

Event Batch／Generator 当前 profile 与 numeric contract 均为 v2；变量名称不变，指定 CC 的 p0/p1、Initial 与递推结果改用 §18.2.9 显示域。Note／Split profile 与 numeric contract 仍为 v1，Mapping ABI v3 不变。旧 Event Preset 不承诺结果兼容，加载时不能静默作为 v2 执行；应保留文件、提示不兼容并要求按新域重新确认／保存。当前 Batch Preset schema 2 包含 profile 与 numeric contract，旧 Note Preset 可在完整重验证后读取。恶意、损坏、重复／未知字段、超资源预算的文件不得绕过当前校验。

### 20.4.14 Note Humanize

Humanize 只对 Logical Note、Direct MIDI Note 和 Template Note 的 Tick、Gate、Velocity 生效，不包含 Key。Seed 支持 `Auto` 与显式整数值；命令提交时冻结实际 seed 和已生成结果，Undo/Redo 不重新抽样。

每个字段独立选择：

```text
Disabled
Add       old + Uniform(min, max)
Multiply  old * Uniform(min, max)
Override  Uniform(min, max)
```

Add/Override 对整数属性使用闭区间均匀整数；Multiply 抽取连续 `double`，最终使用 `AwayFromZero`。`min/max` 必须 finite 且 `min <= max`；Add 可以使用负数范围，Multiply factor 必须大于等于 0（可为 0）。必须先为 Tick/Gate/Velocity 生成全部 raw result，再按固定顺序联合归一化，不得使 UI 字段排列改变结果。

Velocity 夹取到 `1..127`。Gate 最小 1 tick，并以归一化后的 start 执行 checked `start + gate`；无法表示任何正 Gate 时整批失败。Segment 的 crop/当前暴露窗口不是 Note 模型硬边界，仍在正式 owner 模型内但落到 crop 外的结果必须保留。Tick raw result 越过 owner 正式硬边界时删除该 Note；不得扩展 Segment 暴露窗口或 SubVoice Template Length。Gate/Velocity 越界只按各自规则夹取，不因此删除 Note。

抽样必须由 `(seed, owner stable identity, frozen formal ordinal, field kind)` 通过 Midora 版本化、确定性的 PRNG 独立派生；启用或禁用另一字段不得改变已启用字段的抽样。不得依赖 `.NET System.Random` 将来版本的序列。最后按 Note exact start+key 的 later-loses 规则归并。

### 20.4.15 Note Split

Split 只作用于三类 Note。每个 owner 独立计算 `selectionLeft=min(start)` 和 `selectionRight=max(end)`，并在该 owner 中建立严格递增的全局刀线；一条刀线只切开真正穿过该 tick 的已选 Note，落在空隙的刀线仍消耗一次 cut 配额。多个 owner 的结果仍只形成一个 Project Undo。

必须提供：

1. `Fixed Piece Length`：从左边界起每 N ticks 一刀；
2. `Maximum Piece Count`：将选区 span 分成最多 N 个平衡区间，内部边界使用 `floor(j*span/blockCount)`，去除重复和两端边界；
3. `Expression`：使用 `midora.tool.note-split/v1`，`i` 为从 0 开始的当前刀序号，`tr` 为上一刀相对 selectionLeft 的 tick（第一刀前为 0）；表达式返回相对上一刀的长度，finite 后 `AwayFromZero` 并夹到最少 1 tick。下一刀到达或越过 selectionRight 时停止，不在右端创建零长度片段。

`Maximum Cuts` 只属于 `Expression` 模式，默认 65,535、可配置范围 `1..16,777,216`；每次表达式求值和每条空隙刀线都计入该安全停止配额。`Fixed Piece Length` 与 `Maximum Piece Count` 不读取、也不得被该字段静默截断，必须按各自参数完整规划刀线；实现须在分配刀线或结果对象前计算理论刀数，若其超过所有模式共用的 16,777,216 刀硬上限，则整批明确失败并保持零发布。刀数不能代替结果记录、working/resident 字节、spill 字节和磁盘剩余空间门限；结果记录硬上限 100,000,000，working 和 resident staging 分别最多 64 MiB，owned spill 最多 16 GiB。

分割算法必须使用单调刀线与按时间排序 Note 的 active-interval sweep，可使用 128 个 key bucket 与有序端点结构；目标复杂度为 `O((N+K) log N + producedFragments)`，禁止每条 Note 重新遍历全部刀线的 `O(N*K)`。

所有片段继承 Key、NoteOn velocity 和共同属性；Direct MIDI Note 的每个片段都继承源 NoteOff velocity。按时间最早的第一片保留源 Stable ID，后续片获得新 ID；NoteOn/NoteOff formal order 由源 order 与片段序号确定，不得依赖新 ID。

### 20.4.16 Join Notes

Join 只处理已选 Note。在每个 `owner + key` 中按 start/formal order 排序，然后使用非负 `Maximum Gap Ticks`（默认 0）做 sweep；当 `next.start - current.end <= MaximumGap` 时并入当前 run。所有 end、差值和最终 length 使用 checked 算术，溢出时整批失败。

合并结果为 `[first.start, max(end))`；NoteOn velocity 取 run 的第一条，Direct MIDI NoteOff velocity 取 run 的最后一条。第一条保留 Stable ID，其余删除。跨过未选同 key Note 时不得删除或改写未选对象；不同 start tick 的重叠继续交给现有 Event Instrument overlap/编译诊断语义。不同 owner 绝不合并。

### 20.4.17 Note / Event Quantize

Quantize UI 复用 Snap 的拍值列表并额外提供 `Custom Ticks`；初版不提供 `Bar` 或 Strength，固定为 100%。计算必须调用同一正式 Grid/Time Signature 服务，不得重新实现近似网格。恰好在两格正中时选择较早格点。

Segment Note 必须先将 content-local tick 经 `ProjectStartTick` / `ContentOffsetTick` 转为 Project absolute tick，在 Conductor Time Signature Map 中选择格点，再确定性投影回内容坐标。SubVoice 没有 Project Conductor 上下文，以 template tick 0 为原点，只使用 TPQN 分数或 Custom Ticks。

Note 必须提供 `Start only`（默认）与 `Start and End`。后者分别吸附 start/end；若 `end <= start`，将 end 饱和为 `start+1`。Gate 最终至少 1 tick。同 start+key 的命中 Note 按冻结的原 formal order later-loses；未被命中的导入重复保持原样。

Event Quantize 只改 Tick，不改 Value、lane target、formal payload 或其他属性。初版范围仅包含 Direct MIDI Channel Event、Logical Parameter Point 和 SubVoice MIDI Event；不包含 opaque SysEx/Meta 或 Conductor 事件。不同 lane 可以在一个命令中处理，但碰撞键必须按各自正式 lane target 隔离。同 tick+同 target 的被命中事件按冻结原 formal order later-wins；该 exact key 中被命中的既有导入重复全部进入本次 reducer，未命中的重复保留。全部 tick 算术必须 checked。

### 20.4.18 命令、取消与选择结果

Humanize、Split、Join 和 Quantize 必须以可取消前台任务准备 detached 结果，并遵循第 20.4.11 节的分页、资源有界、revision gate 和一次 root swap 规则。表达式和 PRNG 只在 staging 中求值；取消、验证错误、资源超限、I/O 错误或 revision race 必须零发布、零 Undo，且清理本次 owned spill。

成功后，Humanize 和 Quantize 选择所有幸存的本次处理结果；Split 选择所有幸存片段；Join 选择所有合并结果。碰撞或越界 reducer 删除的对象不出现在新选择中。Undo 恢复命令前的完整对象与 Selection，Redo 恢复已冻结结果和结果 Selection，不重新计算随机、表达式、Grid 或当前拍号。一次跨 owner 操作仍只形成一个 Undo。

只有当前 Selection 的所有对象属于工具批准类型且共同可编辑时，对应 Context Menu 命令才启用。Note 菜单提供 `Humanize...`、`Split...`、`Join...`、`Quantize...`；正式数值 Event/Parameter Point 菜单只提供 `Quantize...`。不兼容的混合 Selection 必须禁用对应命令，不得静默只处理其中一部分。对话框 Cancel 不改 Project，关闭后按第 20.2 节恢复来源 Timeline 焦点。

### 20.4.19 Batch Create Notes / Events

三类 Piano Roll 提供 `Batch Create Notes...`；Direct MIDI Channel Event、Logical Parameter Point 与 SubVoice MIDI Event 的当前有效数值 lane 提供 `Batch Create Events...`。这些入口不依赖非空 Selection，但无有效 owner/target 或 Project 编辑被锁时不可执行；opaque、Conductor 不属于本工具。

Note 输入为 Velocity / Key / Gate / Tick 的表达式与 Initial（默认 1 / 0 / 1 / 0）；Event 为 Point Value / Tick（默认正式 lane minimum / 0）。Base Tick 默认当前编辑线、允许自定义，Tick 表达式输出是相对 Base 的位置。空表达式是 identity，非空必须以 `=` 开头。

Generator 使用独立 profile。`i` 从 0 开始；`*0` 是上一轮正规化后的结果，首轮来自按正式值域正规化的 Initial；`*1` 是本轮依赖 DAG 算出的新值，全部字段求值后才联合正规化并作为下一轮输入。`tr` 固定等于输入 `t0`，不等于当前输出 `t1`。`Create first object from initial values` 默认关闭：关闭时第 0 轮先执行表达式；开启时 Initial 直接创建 candidate 0、消耗一个候选名额，表达式从 `i=1` 开始。

Maximum Candidates 默认 65,535，范围 `1..16,777,216`，计迭代/候选而非最终保留数量；可选 Maximum Relative Start Tick。每轮先检查候选上限、求值、finite/checked 算术，然后在相对起点超过上限时停止且不创建该候选。负相对 Tick 夹到 0；整数值使用 AwayFromZero 后夹取正式值域，Velocity `1..127`、Key `0..127`、Gate 最少 1 tick，终点不得超过可表示范围。Logical Parameter 仍按实际 Definition 的 Double/Integer/Enum 值域与既有正规化规则处理，不把显示缩放范围当合法值域。

Tick 可倒退。必须使用有界候选页与排序归并，不得以无限集合收集全部结果。Note exact start+key 保留既有对象，候选间保留最小 iteration；Event exact tick+target 用最大 iteration 覆盖该键全部被命中的既有事件，未命中的导入重复保持原样。Direct endpoint/event order 来自冻结 source order 和 iteration，不依赖新 ID 或线程完成顺序。

生成、归并、验证与结果选择均属于第 20.4.11 节可取消事务；错误、预算、磁盘失败或 revision race 零发布。成功选择全部幸存新对象，Undo/Redo 恢复完整 old/new root 与选择，不重跑表达式。进度显示候选数和保留数，归并前尚未知的保留数不得伪造。

Note/Event Generator Preset 分别保存于 `Data\Presets\NoteGenerationPresets` / `EventGenerationPresets`，包含表达式、Initial、首对象复选框、Maximum Candidates、可选 max relative tick 和严格版本元数据。不保存 Base Tick、owner 或 lane target；应用到当前目标并重新验证。Help 必须解释上述递推、DAG、停止及碰撞规则并提供实用示例。
---
## 20.5 Drag and Drop Conventions
### 20.5.1 分类
```text
Direct Edit Drag
Reorder Drag
Move Drag
Copy Drag
Semantic Drop
External File Drop
View-only Drag
```
### 20.5.2 默认语义
普通内部对象：
```text
Drag      -> Move
Ctrl+Drag -> Copy only when target explicitly supports Copy
Alt+Drag  -> force the approved alternate gesture for the target
```
在 Draw 模式的 Segment、Logical Note 与 Template Note 上，`Alt+Drag` 强制 Move，`Ctrl+Alt+Drag` 强制 Copy+Move；在 Velocity 上，`Alt+Left Drag` 强制自由轨迹。Alt 本身不承担复制或引用语义，且不绕过 Snap。
### 20.5.3 Feedback
拖动反馈显示：
```text
Object count
Operation type
Target
Position or order
Validity
Important semantic impact
```
### 20.5.4 原子性
多对象拖放原子完成，不允许：
```text
Partial success
Skipping incompatible objects
Automatically finding a nearby legal position
Automatically repairing references
```
Escape、Pointer Capture 丢失、源对象失效等取消拖动并完整恢复，不产生 Undo。
### 20.5.5 自动滚动
Timeline 和列表支持边缘自动滚动。Folder 可停留后临时展开。
普通及浮动 Move / Resize 共用捕获状态下的世界坐标 delta；viewport 只控制显示裁切，不截断 pointer tick / key / value。捕获后鼠标停在边缘外、没有新 MouseMove 时仍需持续滚屏及更新有效 delta；回到内部、松开、Escape、失捕获、卸载、owner/source/selection 失效或编辑锁定时停止。Snap、共同饱和与预览/提交一致；三种 Note Move/Copy 均不 clamp pitch delta，越界目标删除（Copy 只丢弃副本，源对象不变）。事件点 value 的共同 Clamp 不变。该 UI 调度不承担音乐时序。
初版不通过悬停 Workspace Tab 自动切页，也不直接跨 Workspace 拖动内部内容。
### 20.5.6 具体对象
#### 20.5.6.1 Segment 与 Piano Roll Note
```text
Arrangement Segment body Ctrl+Drag -> copy complete selected Segments
Segment Note body Ctrl+Drag       -> copy selected Logical Notes
Pure MIDI Note body Ctrl+Drag     -> copy selected Direct MIDI Notes
SubVoice Note body Ctrl+Drag      -> copy selected Template Notes
```
Segment 全部子对象获得新稳定 ID；Logical Note 获得新稳定 ID；Template Note 及其 Mapping Chain/Step 获得新稳定 ID。跨 Track 不自动重绑或删除 Lane。Note 副本使用一个共同时间/pitch delta，并保持相对时间、音程、长度、velocity、Mapping 与目标设置。成功后只选择副本，一次完整手势只形成一个 Project Undo。

复制意图在 Draw 模式的主体拖动越过阈值时确认；未越过阈值的 `Ctrl+Click` 仍按选择切换处理，边缘 `Ctrl+Drag` 仍是 Resize。时间与 Track 越界请求使用选择集共同 clamp；Logical/Direct/Template Note 的目标 key 越过 0–127 时，只丢弃该副本，保留源对象，不报错、不 clamp 音高。合法副本仍按同 tick+key 后来者丢弃规则归并，只选择幸存副本；全部越界或碰撞时清空选择且无新增。普通直接操作和浮动工具的 Ctrl / Ctrl+Alt 行为一致，小集合与有界批量路径一致。Escape、Pointer Capture 丢失、Segment overlap、选择集不兼容或其他非法结果时整体取消；越界副本丢弃属于明确编辑规则，而不是失败后的部分提交。

在上述三类对象上，`Alt+Left Drag` 无视 Body / Resize 边界命中分区并强制 Move；`Ctrl+Alt+Left Drag` 强制 Copy+Move。操作类型、修饰键及复制意图在 Pointer Down 时冻结，拖动途中按下或松开修饰键不得切换语义。强制手势仍按当前 Snap 执行；Resize 继续通过不按 Alt 的普通边界拖动访问。
#### 20.5.6.2 Event Instrument Definition Browser
Event Instruments pane 内的 Definition 越过通用阈值后只重排独立 Definition order，不移动任何 Track/Usage。拖 Definition 到 Arrangement 全局 gap 会原子创建独立 Usage + Logical Track；拖动取消或失败不创建对象。Definition Copy/Paste/Duplicate 只复制定义本体。
#### 20.5.6.3 Arrangement Track 与 Shared block
Track Header 越过通用阈值后在唯一 global mixed Track order 中拖动；初版不使用 Ctrl+Drag Track 复制。Logical 与 Pure MIDI Track 不能互相加入共享组。

Shared Usage / Auto Root block 的 brace 是整体移动 hit target；Track body、上下约 8 DIP 外部插入区、singleton Instrument/Auto chip 与 Fixed route chip 的加入/脱离语义、hysteresis、失败原子性按第 24.7 节执行。不同 Definition 的 Logical 加入显示不同颜色预览并执行 rebind 审查。Fixed Root 不显示 brace且成员可分散。hover、pressed、join outline 与插入线只属于 transient UI state。
#### 20.5.6.4 SubVoice、Mapping 等有序结构
使用插入线重排，保持稳定 ID。
#### 20.5.6.5 Workspace Tab
只在主窗口内重排，不支持拖出。
### 20.5.7 外部文件
```text
.midora or candidate .zip dropped on Main Window -> Open Project flow
SF2 or SFZ dropped on Application Preferences SoundFont list -> Add local path entry; SFZ still requires a target mapping before Apply
```
初版不通过拖放导入：
```text
MIDI
Audio
Code
Arbitrary files
Folder
Standalone Event Instrument file
```
初版不支持把 Project 对象拖出到 Windows Explorer。
### 20.5.8 播放期间
只允许 View-only Drag。所有 Project 修改拖放 Disabled。
重要拖放必须有可见按钮、菜单或其他正常键鼠入口，但不要求纯键盘完整替代。
---
## 20.6 Clipboard Object Formats
### 20.6.1 Clipboard 上下文
```text
Text Clipboard Context
Code Clipboard Context
Project Object Clipboard Context
Read-only Information Clipboard Context
```
### 20.6.2 Project Object Payload
Project 对象 Copy 可以同时写入：
```text
Midora Internal Object Payload
Plain Text Summary
```
初版只使用当前 Windows Clipboard，不提供内部历史或多槽。
### 20.6.3 Project 会话有效性
Project Object Payload 只在当前来源 Project 会话有效。
关闭或替换 Project 后失效，不支持跨 Project Paste。
### 20.6.4 快照与 ID
Clipboard 是 Copy 时的不可变快照。
每次 Paste：
- 生成新的稳定 ID；
- 内部引用重映射到新副本；
- 允许的外部引用保留原稳定 ID；
- Context Reference 改为目标上下文；
- Broken Reference 保留原 ID 和 Last Known Name。
不按名称自动修复。

上述规则同样适用于 Cut 先写入的快照；Cut 后 Paste 创建新稳定 ID。只有 Header/brace 拖动这种单一原子 Move 保留原 stable ID；Cut、Paste 各自是独立 Undo，且 Undo Cut 后仍然 Paste 时不得制造 ID 冲突。
### 20.6.5 初版支持的普通对象
```text
Segment
Logical Note
Direct MIDI Note / Channel Event / opaque imported event
Event Instrument Definition
Logical Track（含 Segment 内容及 Usage/Definition binding snapshot）
Pure MIDI Track（含 Segment 内容及 route snapshot）
Logical Parameter Lane, Point and Curve content
SubVoice timeline events
Ordinary Conductor events
Compatible ordered content
```
以下高层定义使用显式 Duplicate 或专用命令，不进入普通 Clipboard：
```text
SubVoice Definition
Logical Parameter Definition
Mapping Function Definition
Project Settings
SoundFont
```

Logical Note 与 Direct MIDI Note 允许跨类型 Paste，只转换 relative tick、gate、key 和 NoteOn/instance velocity；Logical→Direct 的 NoteOff velocity 为 0，Direct→Logical 丢弃 NoteOff velocity。Segment 使用第 20.6.14 节的正式转换；独立 Parameter/Event 不做跨类型猜测转换。
### 20.6.6 Segment Payload
包含：
```text
Active crop window
All Logical Notes
All Logical Parameter Lanes, Points and Curves
Hidden content outside the crop window
Broken and inapplicable data
```
### 20.6.7 Arrangement Paste
```text
Earliest source Segment -> align to Edit Cursor
Primary source Track    -> map to Active Target Track
Other source Tracks     -> preserve relative track offsets
```
任一目标 Track 不存在或 Segment 约束冲突时整体失败，不自动找空位。
### 20.6.8 Logical Parameter 内容
不按名称、类型或显示范围自动匹配。
独立 Parameter 内容只有 exact target 有效时才可 Paste。
必须区分：
```text
Whole Lane
Lane Content
```
### 20.6.9 SubVoice
跨 SubVoice Paste 初版仅限同一个 Event Instrument，并保持合法 Mapping Function 外部引用。
### 20.6.10 Cut
Cut = 成功写入 Clipboard 后删除源对象。
Cut + Paste 是 snapshot + Delete + Create，不保持对象身份；Definition/Logical Track/Pure MIDI Track 也遵循该规则。Header/brace Drag Move 才保持原对象及 owner stable IDs，并在一个失败原子 Undo 中完成；失败时保留原位置。
Cut 和 Paste 分别形成独立 Undo；Clipboard 本身不受 Undo 影响。
### 20.6.11 Timeline 对齐
Timeline Payload 最早 tick 对齐 Edit Cursor；`Paste Here` 使用右键位置。
Paste 后 Clipboard 保留；不自动增加 Grid Step 偏移。
### 20.6.12 Code Editor
C# Code Editor 只粘贴 Plain Text 到 Draft Buffer，不自动 Apply。
外部文本、文件和未知格式不被 Workspace 猜测转换成 Midora 对象。
### 20.6.13 播放期间
允许 Copy；禁止 Cut 和 Project Paste。

### 20.6.14 Logical / MIDI Segment 双向转换

Arrangement 的拖动、Ctrl 复制拖动和 Segment Clipboard Paste 共用一个正式转换流程，允许多项 Logical/MIDI 混合选择。保持全局 Arrangement Track 相对偏移；Paste 使最早源 Segment 对齐编辑线，主源 Track 对齐目标 Track。同类型移动保留原对象及全部内容；同类型复制粘贴保持既有字段、hidden 内容及精确碰撞规则，不按名称修复参数引用。跨类型转换 Segment Project start、length、content offset、全部 Note start/gate/key/NoteOn velocity，包含 crop 外 hidden Notes。它不是编译/渲染 Event Instrument，不保证跨类型后音色或听感相同。

跨类型时按类型统计整个内容中的 Logical Parameter Lane/Point、Direct Channel Event、opaque Meta/SysEx，以及 MIDI→Logical 的非零 NoteOff velocity。空参数 Lane 仍是会丢失的内容。任何损失必须一次汇总确认；全零 NoteOff velocity 不额外警告。Logical→MIDI NoteOff velocity 固定 0。转换的 Note exact start+key 碰撞按冻结 source formal order 保留最早者，并把丢弃数量列入确认；不同 start 的同 key overlap 不折叠。

先在后台冻结来源修订并分析，用户确认后才建立完整 detached target。非法目标 Track、未绑定的 Logical Track、Segment overlap、range/结构错误、取消或修订失效均保持源不变。Move 的源删除与目标发布属于同一事务，不能先删后尝试。成功只产生一个 Undo，选择转换后的目标；Copy/Paste 使用新 ID，Move 保持可保留对象身份并提供确定结果映射，Undo 完整恢复旧模型和选择。
---
## 20.7 Context Menus
### 20.7.1 定位
Context Menu 是现有命令的辅助入口，不建立第二套命令语义。
```text
Context Menu Command
-> Existing Routed Command
-> Same Validation
-> Same Undo / Redo semantics
-> Same locking rules
```
### 20.7.2 统一分组
```text
[A] Primary Object Actions
[B] Edit and Clipboard Actions
[C] Navigation and Inspection
[D] Destructive Actions
```
Delete 等危险命令固定放在底部并与普通编辑命令分组隔开。
### 20.7.3 右键与 Selection
```text
Right-click selected object   -> preserve current multi-selection
Right-click unselected object -> replace with single clicked object
Right-click empty area        -> preserve Selection; full selection menu if non-empty, otherwise container/position menu
```
Timeline 内容空白区域右击时，若当前有有效选区，提供与选中对象上右击相同的完整选区菜单；无选区才只提供容器/位置命令。混合选择仍显式按类型分组，播放/只读等禁用规则保持；不得因此改变轨道头、brace 或琴键区域的专用菜单规则。
### 20.7.4 固定目标
菜单打开时固定：
```text
Clicked object or Selection
Clicked container
Clicked timeline position
Active Workspace
```
Hover、所属属性投影刷新或后台诊断更新不得改变命令目标。
时间位置命令使用打开菜单时记录的 tick。
Timeline 内容、标尺与轨头复用菜单时，弹出位置必须属于本次点击；局部坐标只能相对所在 Surface 换算一次，不得把上次内容菜单偏移叠加到原生鼠标/键盘锚点上。屏幕边缘按 WPF 可用工作区避让。
### 20.7.5 Hide 与 Disabled
语义完全无关的命令隐藏。
通常存在但因播放、锁定、剪贴板或对象状态暂时不可用的命令保持可见并 Disabled，必要时说明原因。
Conductor 固定行不显示 Delete / Rename；Arrangement 仅有 Logical/Pure MIDI Track 行。Definition 的重命名/删除位于 Event Instruments pane；Usage/Root 没有独立 Rename/Delete UI。
### 20.7.6 非唯一入口
重要功能不能只存在于 Context Menu，例如：
```text
Save Project
Compile
MIDI Export
Audio Render
Create Event Instrument
Create Logical Track
Project Settings
Open Diagnostics
Resolve Broken Reference
```
### 20.7.7 多选
只显示对全部对象都合法的共同命令。
初版不采用“处理能执行的对象并跳过其他对象”的隐式部分成功模式。

三种钢琴卷帘的混合 Note/Event 选择提供显式 `For Notes >` 与 `For Events >` 子菜单；这些命令只处理打开菜单时冻结的对应类型子集，不是隐式部分成功。普通 Copy/Cut 及类型专属 Ctrl+E/Q/T 禁用；Delete 对全部选中对象一次原子删除。非音符内部再按共同合法能力开放命令；混合 Lane/数值域或含 Opaque 时不得套用错误的单值解释。

显式类型操作成功后，未处理类型保持选中；结果对象按既有碰撞/删除规则选择。Undo 恢复操作前完整选择，Redo 恢复结果与未处理子集。菜单准备、Properties、复制/剪切、编辑与选择投影都必须在 source/selection 修订边界内，取消或失效时零发布；播放/任务锁下只允许选择、Locate 和只读 Properties。
### 20.7.8 Clipboard
Paste 是否可用由以下共同决定：
```text
Clipboard object type
Target container
Target timeline position
Current Project state
Task and playback lock
```
不提供目标不可预测的通用 Paste。
### 20.7.9 危险文案
使用具体名称：
```text
Delete Event Instrument
Change Instrument / Make Independent
Delete All Lane Events
Reset Property to Inherited
Clear Runtime History
```
不得用模糊 `Remove / Clear / Reset` 混淆不同语义。
### 20.7.9.1 Timeline 右键单击与框选
三种 Piano Roll、Event/Parameter/Velocity、Tempo/Conductor、Inst. 固定 y 点及 Arrangement 可编辑内容区使用同一冻结合同：

1. Right Down 冻结 hit target、原 Selection、container、tick 与修饰键；此时不改变正式 Selection，不打开菜单，也不启动 trace；冷页 exact hit 必须异步完成，不能阻塞 WPF UI thread。
2. 移动越过既有系统 drag threshold 时，立即取消菜单候选与冷命中请求并进入矩形框选；在各主工具下均如此，不改当前 ToolMode、不等待冷查询。Velocity 选择对应 Note；右键不再绘线。
3. 未拖动 Right Up 立即打开冻结目标菜单，不故意等待双击窗口。冷命中未就绪时立即显示禁用 `Locating…`，就绪后只填充最初 owner/位置的菜单并按 §20.7.3 应用选择；已选保持多选、未选替换为目标、空白保留选择并提供该有效选区的完整菜单，无选区才使用容器菜单。
4. 完全取消右键双击切 Draw/Select，不使用 WPF ClickCount、系统设置或自建 300 ms 窗口。连续右击分别按单击或拖动处理。
5. Escape、失捕获、切页/卸载、Workspace/Project/owner 失效、source/selection revision 改变或新手势必须取消旧 pending 请求，迟到结果不得打开菜单或恢复旧选择。菜单 IsOpen 变为 false 时立即取消，不能等关闭动画结束才处理。

Piano Pitch Ruler、轨头、brace、ruler 不纳入内容右拖；只读 All Tracks 不获得选择/编辑。Arrangement 固定 Conductor/非内容区仍服从自身命中边界。中键 Pan 与已有 capture 互斥，不建立第二个竞争手势。
### 20.7.10 主要菜单边界
#### 20.7.10.1 Event Instrument
```text
Open
Preview
Add Logical Track Using This Instrument
Copy / Cut / Paste
Duplicate
Rename
Show References
Properties
Move Up / Move Down
Delete
```
#### 20.7.10.2 Logical Track
```text
Edit Event Instrument...
Copy / Cut / Paste
Duplicate
Duplicate and Share State
Rename
Change Instrument
Share Instrument State With
Make Independent
Move Up / Move Down
Delete
```
`Edit Event Instrument...` 打开当前 Usage 引用的 Definition；未绑定 Track 没有目标，因此该命令 Disabled 或隐藏。`Duplicate` 创建引用同一 Definition 的新独立 Usage；`Duplicate and Share State` 仅对已绑定 Track 可用，并保留源 Usage。Definition-only 复制只由 Event Instruments pane 的普通 Definition `Duplicate` 提供，Track/Usage 上下文不再提供 `Duplicate Instrument Only`。

`Change Instrument` 原子修改当前 Usage 的 Definition；未绑定空壳可通过该入口建立独立 Usage。`Share Instrument State With` 与 `Make Independent` 显式改变 Usage membership；不提供独立可见 Usage 管理器。Duplicate 的插入位置、稳定 ID 和 Undo 规则见第 24.8 节。
#### 20.7.10.2.1 Pure MIDI Track / Shared group
Pure MIDI Track 菜单至少提供 Copy/Cut/Paste、Duplicate、Rename、MIDI Route Settings、Share MIDI Channel With、Make Independent、Move Up/Down、Delete；不得显示 Event Instrument binding 命令。任一共享 Fixed Root 成员的 MIDI Route Settings 都可编辑唯一 Root 的 Channel Mode；多成员时必须先明确提示影响范围并确认，不要求转到另一个命名为 `Shared MIDI Route Settings` 的入口。共享 brace 菜单按类型提供 group Mute/Solo、Route/Instrument Settings、Move Shared Group 与 Make All Tracks Independent。Root 不显示独立菜单。
#### 20.7.10.3 Segment
```text
Open in Segment Editor
Cut
Copy
Duplicate
Split at Cursor
Properties
Delete
```
Segment 无 `Rename`。
#### 20.7.10.4 Logical Note
```text
Cut
Copy
Duplicate
Properties
Delete
```
不显示 Legato 命令。
#### 20.7.10.5 Logical Parameter Lane
```text
Show Parameter Definition
Show Mapping
Hide Lane
Delete Lane Data
Parameter Definition Properties
```
#### 20.7.10.6 Broken Lane
```text
Show Broken Reference
Choose Replacement
Go to Last Known Instrument
Delete Lane Data
```
不得按名称自动修复。
#### 20.7.10.7 SubVoice
```text
Open
Preview Selected SubVoice
Duplicate
Rename
Show References
Properties
Delete
```
最后一条 SubVoice 的 Delete 不显示。
#### 20.7.10.8 Mapping Function
```text
Properties
Duplicate
Show References
Delete
```
`Properties` 使用第 18.5.4 节的模态名称+单行表达式编辑器。没有独立 Function Workspace、Apply Draft 或 Discard Draft 命令。
#### 20.7.10.9 Diagnostic
```text
Go to Source
Copy Message
Copy Source Path
Mark as Reviewed
```
初版不提供永久 Suppress 或 Delete Diagnostic。
#### 20.7.10.10 Workspace Tab
```text
Close Tab
Close Other Tabs
```
关闭 Tab 只关闭界面。
### 20.7.11 播放与任务锁定
播放期间允许 Open、只读 Properties、Go to Source、Copy 和纯查看命令；Project 编辑命令 Disabled，Properties 内全部 Project-backed 输入 Disabled。
模态任务和 Audio Render 完全锁定期间，主窗口 Context Menu 不可用。
---
## 20.8 Naming and Inline Rename
### 20.8.1 Inline Rename
```text
+--------------------------------------+
| [A] Object Icon                      |
| [B] Inline Name Field                |
| [C] Validation Indicator             |
+--------------------------------------+
```
入口：
```text
F2
Context Menu > Rename
Owning Workspace Name Field
Visible Rename command
```
双击保留给 Open，不用于 Rename；不使用慢速第二次单击进入 Rename。
多选时 Rename Disabled。
### 20.8.2 本地缓冲
```text
Begin Rename
-> edit local text buffer
-> validate
-> commit or cancel
```
输入过程中不实时写入 Project。
```text
Enter  -> validate and commit
Escape -> cancel and restore
Valid focus loss   -> commit
Invalid focus loss -> restore old name
```
无实际变化不创建 Undo，也不标记 Modified。
### 20.8.3 字符规则
提交时去除首尾空白；内部连续空格保留。
允许 Unicode。
对象名称不受 Windows 文件名字符规则限制。
不允许：
```text
Newline
NUL
Control characters that break single-line or persistence semantics
```
第 17～20 章的 UI 与交互规格 不任意规定最大长度；若最终 schema 有上限，所有入口统一验证并禁止静默截断。
### 20.8.4 名称表
| 对象 | 是否可空 | 唯一性 | Rename |
|---|---:|---|---:|
| Project Name | 是 | 不适用 | Project Settings |
| Event Instrument | 否 | Project 内唯一 | 是 |
| Logical Track | 是 | 允许重复 | 是 |
| MIDI Channel Root | 是 | 允许重复 | 是 |
| Pure MIDI Track | 是 | 允许重复 | 是 |
| Segment | 无名称 | 不适用 | 否 |
| SubVoice | 是 | 允许重复 | 是 |
| Logical Parameter | 否 | Event Instrument 内唯一 | 是 |
| Mapping Function | 否 | Event Instrument 内唯一 | 是 |
| Envelope Preset | 是 | 允许重复 | 是 |
| Marker | 是 | 允许重复 | 是 |
| Project End Marker | 固定名称 | 不适用 | 否 |
| Fixed top-level node | 固定名称 | 不适用 | 否 |
| Damaged Placeholder | 名称快照只读 | 不适用 | 否 |
| Broken Last Known Name | 只读提示 | 不适用 | 否 |
要求唯一的名称比较：
```text
Trimmed
Case-insensitive
```
### 20.8.5 自动显示标签
Logical Track 名称为空：
```text
Logical Track <Display Index>
```
MIDI Channel Root / Pure MIDI Track 名称为空时分别显示 `MIDI Root <Display Index>` / `MIDI Track <Display Index>`；显示 fallback 不回写源名称。
SubVoice 名称为空：
```text
SubVoice <Display Index>
```
Envelope 名称为空：
```text
Envelope <Display Index>
```
Marker 名称为空可显示位置摘要。
自动标签不持久化，不作为身份，顺序变化时可更新。
### 20.8.6 唯一冲突
冲突时：
- 不提交；
- 保持 Inline Rename；
- 显示具体作用域；
- 不自动追加数字；
- 不覆盖、交换或合并对象。
### 20.8.7 Duplicate 名称
要求唯一的对象 Duplicate 时自动生成合法名称，例如：
```text
Kick
Kick Copy
Kick Copy 2
```
自动名称与 Duplicate 属于同一个 Project Undo。
允许重名的对象可以保留原名称。
### 20.8.8 全局同步
Rename 后同步更新：
```text
Workspace Tab
Editor Header
Owning Workspace property editor
Reference List
Diagnostics Source Path
Search Index
Picker
Export and Render name preview
```
引用继续基于稳定 ID。
临时 Name Sort 下对象可以移动到新显示位置，但正式手动顺序不变。
任务开始后使用冻结名称快照；任务中途不改变输出名。
### 20.8.9 Undo
一次成功 Rename 形成一次 Project Undo。
Inline 字段有焦点时 Ctrl+Z / Ctrl+Y 操作本地文本；提交后由 Project Undo 撤销名称。
---
## 20.9 Search Behavior
### 20.9.1 初版搜索类型
```text
Collection Filter
Diagnostics Search
```
初版不提供：
```text
Global Project Search
Full-text Project Search
Search all MIDI events
Search Notes by pitch or velocity
Search Segments by content
Search Logical Parameter Points
Find in all Mapping Functions
Saved Search
Fuzzy Search
Regular Expression Project Search
Project-wide Replace
```
### 20.9.2 Collection Search
统一规则：
```text
Case-insensitive
Literal substring match
Trim query leading and trailing whitespace
Preserve current view order
No relevance reordering
```
不解释通配符、逻辑运算符、字段语法或正则表达式。
支持 Unicode。
### 20.9.3 状态归属
搜索词和筛选属于 Project Session UI State：
- 不保存进 `.midora`；
- 不进入 Undo / Redo；
- 不标记 Modified；
- 不跨应用重启；
- 不保存搜索历史或建议。
### 20.9.4 Selection
搜索不自动选择第一项，不自动打开 Workspace，不改变播放光标。
已选对象被 Collection Search 隐藏时：
- 通过稳定 ID 保持 Selection；
- 不自动选择其他结果；
- Workspace Selection 继续按稳定 ID 保留；所属编辑器可显示 Hidden Selection Notice；
- 显示 Hidden Selection Notice；
- `Show Selected` 可清除文字查询以重新显示对象。
### 20.9.5 键盘
```text
Ctrl+F in Function Editor -> Find in Draft Buffer
Ctrl+F in searchable collection -> focus local Search field
Ctrl+F in Diagnostics -> focus Diagnostics Search
Other context -> No Action
```
Search Field：
```text
Escape with query -> clear text query
Enter with selected result -> open or activate
Enter with no selected result -> no automatic open
Down -> first visible result
Up   -> last visible result
```
### 20.9.6 Arrangement / Definition 搜索
初版不提供 Project Panel。Event Instruments pane 当前可不提供搜索；未来加入本地筛选时，只能过滤独立 Definition order 的视觉投影，不得改变 global Arrangement Track order、Usage/Root membership 或正式拖放目标。筛选文本只属于 session state。
### 20.9.7 Diagnostics Search
匹配：
```text
Diagnostic Message
Diagnostic Code
Source Object Name
Source Path
Last Known Source Name
```
与 Severity、Source 和 Status Filter 取交集。
Global Status Bar 的 Whole Project 计数不受搜索影响。
### 20.9.8 Object Picker
Picker 搜索只作用于当前合法对象类型。
名称可能重复时显示足够上下文，例如 Track 顺序、绑定 Instrument 或 Envelope 所属 Instrument。
多选 Picker 中，被搜索隐藏的已勾选对象保持选中，并显示总选择数量。
### 20.9.9 Function Find
作用于当前 Draft Buffer：
```text
Ctrl+F       -> open Find
Enter        -> next match
Shift+Enter  -> previous match
Escape       -> close Find
```
初版至少支持 Plain Text、Case-sensitive Toggle 和 Whole Word Toggle，不要求 Regex。
### 20.9.10 播放与任务
播放期间允许搜索和导航结果，但 Project 编辑命令仍 Disabled。
模态任务期间主窗口搜索不可用；Dialog 可以拥有自己的本地搜索。
Audio Render 完全锁定期间不允许主窗口搜索。
---
## 20.10 Empty、Disabled、Loading、Error 与 Damaged States
### 20.10.1 状态分类
必须严格区分：
| 状态 | 含义 |
|---|---|
| Empty | 合法对象或集合当前无内容 |
| Disabled | 功能存在但当前上下文不可操作 |
| Loading | 正在读取、编译或准备 |
| Error | 操作或资源失败 |
| Damaged | 持久化内容无法正常读取 |
| No Results | 搜索或筛选无匹配 |
同一空白画面不得同时表达这些不同状态。
### 20.10.2 Empty State 统一结构
```text
+------------------------------------------------------------+
| [A] Empty State Title                                      |
| [B] Explanation                                            |
| [C] Primary Action                                         |
| [D] Secondary Navigation                                   |
+------------------------------------------------------------+
```
窄面板使用紧凑提示，不强制大面积居中。
### 20.10.3 全局规则
- 不自动创建 Event Instrument、Track、Segment、Note、Lane、Envelope 或 Function；
- 不使用虚假示例内容；
- Primary Action 调用正式创建命令，保持相同默认值、验证、Undo 和锁定规则；
- 播放锁定时按钮保持可见但 Disabled，并说明原因；
- Empty State 不是 Project 对象，不进入 Selection、显式 Properties 目标或 Undo。
### 20.10.4 无 Project
```text
No Project Open
Create a new project or open an existing one.
[ Create Project ] [ Open Project ] [ Open MIDI as New Project ]
```
三个入口必须位于同一水平命令行；窗口宽度不足时由外层 Welcome surface 承担布局约束，不得把 MIDI 导入入口单独降为第二行的次级操作。

Transport、Compile、Save、Export、Render 和 Undo / Redo Disabled；`File > Close Project`、`View > Arrangement` 与 `View > Diagnostics` 同样 Disabled。Application Preferences 仍可用。
### 20.10.5 无 Workspace
Project 打开期间 Arrangement 常驻且不可关闭，因此不存在“无 Workspace”状态。
### 20.10.6 Arrangement 无 Track
```text
No Tracks
Create a Logical or MIDI Track to begin.
[ New Logical Track ]
[ New Logical Track with Instrument ]
[ New MIDI Track ]
```
Logical Track 可以作为无内容、无 Usage 的空壳创建，也可与独立 Usage/Definition 原子创建。Pure MIDI Track 创建时必须同时创建或加入一个非空 Root；没有只创建空 Root 的入口。
### 20.10.7 没有 Event Instrument
```text
No Event Instruments
Create an event instrument to define reusable events.
[ Create Event Instrument ]
```
创建最小合法 Event Instrument：唯一名称、一条空 SubVoice、不自动生成 MIDI 事件。
### 20.10.8 Track 无 Segment
Track Timeline 内显示紧凑提示，不覆盖整个 Arrangement。
```text
No segments
Double-click or use Draw to create
```
无内容、无 Usage 的 Logical Track 显示可操作的 `No Event Instrument` 空状态，并禁止创建/粘贴音乐内容，直到指定 Definition。若已有内容却无有效 Usage/Definition，则显示可定位的结构 Error 并禁止普通编辑。
### 20.10.9 Segment 无内容
Note 区与 Parameter 区分别显示自己的空状态。
如果 Event Instrument 无 Logical Parameter：
```text
No logical parameters are exposed by this event instrument.
```
空 Segment 仍是合法对象，不自动删除。
### 20.10.10 Event Instrument 可选集合为空
分别显示：
```text
No logical parameters
No mapping functions
No envelope presets
No mappings
```
空集合不是 Warning。
Incompatible 数据不是 Empty，必须保留并显示 Disabled / Incompatible。
### 20.10.11 Diagnostics 无项目
当前集合为空：
```text
No diagnostics
No issues are currently reported.
```
筛选无结果：
```text
No matching diagnostics
[ Clear Filters ]
```
不得用绝对语句保证 Project 一定可编译。
### 20.10.12 无启用 SoundFont
```text
No SoundFonts Enabled
MIDI editing, compilation, and MIDI export remain usable.
Playback, preview, and audio rendering are unavailable.
[ Open Application Preferences ]
```
这是合法应用状态，不是 Project Error，不使用 Error 图标。
### 20.10.13 无启用音频输出设备
```text
No Enabled Audio Output Device
Playback and preview are unavailable. Audio file rendering remains available.
[ Refresh Devices ]
```
这是本机运行环境状态，不是 Project Error，不影响编辑、编译、保存、MIDI 导出或文件音频渲染。

### 20.10.15 Search 无结果
```text
No results for "query"
[ Clear Search ]
```
不自动放宽条件或模糊替换。
### 20.10.16 Damaged Content
```text
Content Cannot Be Opened
This object could not be loaded as a normal project item.
[ Open Diagnostics ]
Remove Object
```
不得显示普通空编辑器，也不允许在 Placeholder 中创建正常子对象。
---
## 20.11 Error and Validation Presentation
### 20.11.1 呈现层级
```text
[A] Field Validation
[B] Object Status
[C] Workspace Validation Summary
[D] Global Notice Bar
[E] Global Status Bar
[F] Diagnostics Panel or Workspace
[G] Task Result Presentation
[H] Blocking Dialog
```
原则：使用最小充分层级，不对普通字段错误滥用弹窗。

Global Status Bar 允许为布局而省略长文本，但 Error 状态必须同时提供详情入口。详情窗显示该状态消息的完整原文，文本可选择并可通过 `Ctrl+C` 或显式 Copy 操作复制；打开详情不清除状态消息，也不改变 Project。
### 20.11.2 Field Validation
未提交输入错误：
- 显示在字段附近；
- 保留输入供修正；
- 不写入 Project；
- 不产生 Undo；
- 不生成 Whole Project Diagnostic；
- 不弹 Blocking Dialog。
提交失败必须说明具体约束，例如：
```text
Value must be between 0 and 127.
End Tick must be greater than Start Tick.
An event instrument named "Kick" already exists.
```
跨字段关系错误同时标记直接相关字段，并显示一条统一说明。
### 20.11.3 Object Status
对象状态至少区分：
```text
Error
Warning
Information
Broken Reference
Incompatible
Damaged
Unused
Draft
```
不得只依赖颜色；至少使用图标、短标签和 Tooltip / Properties。
父对象可聚合子对象问题，但不复制所有完整消息。
Timeline 对象的问题不能用整块纯红色遮挡内容、Selection 或曲线形状。
### 20.11.4 Workspace Summary
复杂 Workspace 提供当前对象范围的验证摘要：
```text
2 Errors   1 Warning
[ Open Diagnostics ]
```
无问题时使用：
```text
No issues are currently reported.
```
不得保证“对象完全正确”或“必然编译成功”。
### 20.11.5 Severity 与 Outcome
严重级别：
```text
Error
Warning
Information
```
任务结果：
```text
Succeeded
Succeeded with Warnings
Blocked
Failed
Cancelled
Completed With Errors
```
两者严格分离。
例如 Warning 因策略阻止编译时：
```text
Severity: Warning
Outcome: Compile Blocked by Policy
```
### 20.11.6 诊断过期
Project 发生相关修改后，旧结果必须显示为 Outdated、Resolved、Previous Compile 或 Runtime History，不能继续伪装为当前结果。
旧结果不得决定当前 Play、Export 或 Render 是否允许开始。
### 20.11.7 C# Draft 与 Applied Version
Draft 本地错误显示在 Code Editor，并标注 Draft。
Project Diagnostics 对应 Applied Version。
未引用 Function 的代码错误不阻止整曲编译；实际编译路径使用的 Function 错误按编译规则成为 Error；运行时异常中止当前流程并进入 Runtime History。
### 20.11.8 Broken、Incompatible 与 Damaged
#### 20.11.8.1 Broken Reference
目标不存在；显示 Last Known Name 和预期类型，可选择 Replacement 或删除 Broken Data。
#### 20.11.8.2 Incompatible
数据存在，但当前设置使其不可用；保留并 Disabled，兼容设置恢复后继续使用。
#### 20.11.8.3 Damaged
持久化内容无法读取；使用专用 Damaged 页面，不能显示为空对象。
### 20.11.9 Task Validation
配置阶段：
- 字段错误就地显示；
- Review 汇总阻止开始的问题；
- Start Disabled；
- 提供 Go to Setting；
- 不连续弹多条错误 Dialog。
正式任务阶段：问题进入 Task Log、Task Diagnostics 和 Output Item Status。
### 20.11.10 Blocking Dialog
仅用于无法安全继续且用户必须确认或选择的情况，例如：
```text
Project cannot be opened
Save failed
Current project cannot be closed safely
File format is newer than this Midora version
An unrecoverable playback error stopped playback
```
按钮使用具体动作：
```text
Retry Save
Choose Another Location
Open Diagnostics
Return to Project
Close
```
不使用模糊 `Yes / No / Continue`。
### 20.11.11 Runtime Presentation
性能和状态提示：
```text
Buffer Underrun
Clipping
Limiter Activity
Temporary device issue
Audio cache retention disabled
Audio cache entry corrupt and rebuilt
```
显示于 Status Bar、Bottom Runtime Panel 或 Runtime History；不自动成为 Project Error。
不可恢复错误执行 Stop 类清理，保留来源和上下文，不修改 Project。
Buffering 是可恢复播放状态，不弹 Error Dialog。
### 20.11.12 非干扰
后台验证、编译完成和诊断刷新不得：
```text
Steal keyboard focus
Open Diagnostics Workspace automatically
Change Active Workspace
Change Selection
Scroll current editor
Expand every error node
```
### 20.11.13 声音与 Toast
初版：
```text
No error sounds
No success sounds
No diagnostic audio cues
No celebration animations
No general-purpose toast system requirement
```
短暂成功信息使用 Status Bar；持续状态使用 Notice Bar。
---
## 20.12 Shortcut Conflict Resolution
### 20.12.1 初版快捷键表
| 快捷键 | 命令 | 作用范围 |
|---|---|---|
| `Ctrl+N` | New Project | Global |
| `Ctrl+O` | Open Project | Global |
| `Ctrl+S` | Save Project | Global Project context |
| `Ctrl+Shift+S` | Save Copy | Global |
| `Ctrl+Z` | Undo | Focus-sensitive |
| `Ctrl+Y` | Redo | Focus-sensitive |
| `Ctrl+X` | Cut | Focus-sensitive |
| `Ctrl+C` | Copy | Focus-sensitive |
| `Ctrl+V` | Paste | Focus-sensitive |
| `Ctrl+A` | Select All | Focus-sensitive |
| `Ctrl+D` | Duplicate Selection | Focused object scope |
| `Ctrl+F` | Local Search / Find | Focus-sensitive |
| `Ctrl+P` | Open Properties for the active supported target | Focus-sensitive |
| `Ctrl+Tab` | Next Workspace Tab | Main Window |
| `Ctrl+Shift+Tab` | Previous Workspace Tab | Main Window |
| `F2` | Rename Focused Object | Focused object scope |
| `Delete` | Delete Focused Selection | Focused object scope |
| `F4` | Next Active Diagnostic | Main Window |
| `Shift+F4` | Previous Active Diagnostic | Main Window |
| `F6` | Next Main UI Region | Main Window |
| `Shift+F6` | Previous Main UI Region | Main Window |
| `D` | Draw Tool | Active Arrangement / Segment / SubVoice Timeline Workspace |
| `S` | Select Tool | Active Arrangement / Segment / SubVoice Timeline Workspace |
| `E` | Erase Tool | Active Arrangement / Segment / SubVoice Timeline Workspace |
| `Space` | Play / Stop | Explicit Timeline Context only |
| `Escape` | Cancel innermost temporary state | Context-sensitive |
| `Alt+F4` | Close active window / Exit request | Application or active Dialog |
初版不增加表外的其他默认全局快捷键。
### 20.12.2 Ctrl+S
`Ctrl+S`、Global Save Button 和 `File > Save Project` 始终保存 Project。Mapping Function 模态对话框只由其 `OK` 提交本地编辑缓冲；对话框内不得把 `Ctrl+S` 重新解释为 Apply。
### 20.12.3 Undo / Redo
```text
Mapping Function expression editor -> dialog-local text Undo / Redo
Ordinary text field  -> field-local Undo / Redo
Other Project area   -> Project Undo / Redo
```
本地 Undo Stack 为空时不向 Project Undo 回退。
Global Undo / Redo 按钮只操作 Project History，并显示当前操作名称。
### 20.12.4 Clipboard 与 Ctrl+A
Text / Code 焦点下操作文本缓冲，不得误操作背景 Project Selection。
Project 对象区域使用 Project Clipboard 命令。
`Ctrl+A` 只选择当前 Active Selection Scope；无明确定义时 No Action。
### 20.12.5 Ctrl+D、Delete、F2
只针对当前焦点区域。
Text / Code Editor 不触发 Project Duplicate、Delete 或 Rename。
F2 在 Segment、Project End Marker、Conductor 固定行、Damaged Placeholder、多选或文本编辑器中 No Action。Event Instruments pane 的 Definition 与 Arrangement Logical/Pure MIDI Track 支持 F2 Rename；Usage/Root 不可直接重命名。

`Ctrl+D` 作用于 Logical Track 时执行普通独立 `Duplicate`，不得隐式加入源 Usage。`Duplicate and Share State` 是显式 Track 命令，初版不为其注册默认全局快捷键。
### 20.12.6 Space
主窗口没有活动 Modal、Popup、Menu 或本地编辑会话，且焦点不在 TextBox、PasswordBox、RichTextBox 或代码编辑器时：
```text
Stopped                    -> Play
Preparing / Playing / Buffering -> Stop
```
Button、Checkbox、Tree、List、非文本 Properties 表面、Diagnostics、Settings 与 Status Bar 不再优先消费 Space；这些区域的 Space 执行全局 Play / Stop。文本输入、代码输入、打开的菜单/Popup 和 Modal Dialog 仍优先处理 Space。
初版没有 Pause。
### 20.12.7 Escape
优先顺序：
```text
1. Close popup or context menu
2. Cancel drag, marquee, numeric drag or inline gesture
3. Cancel Inline Rename or ordinary field edit
4. Close local Search / Find or clear active search text
5. Cancel or close active cancellable Dialog
6. No Action
```
Escape 不默认：
```text
Clear stable Selection
Close Workspace
Close Project
Exit application
Stop playback
Cancel Save transaction
```
Audio Render 中 Escape 打开 Cancel Rendering Confirmation。
### 20.12.8 Enter 与 Tab
```text
Text field      -> validate and commit
Inline Rename   -> validate and commit
List or tree    -> open or activate focused item
Mapping Function expression editor -> enabled default OK action（补全列表打开时先提交补全项）
Dialog          -> enabled default action when focus control has no own Enter behavior
```
Tab / Shift+Tab 在当前焦点范围内移动；单行 Mapping Function 表达式不得插入换行。
### 20.12.9 Diagnostics
```text
F4       -> Next Active Diagnostic
Shift+F4 -> Previous Active Diagnostic
```
使用 Global Active Diagnostics，不受 Diagnostics Workspace 当前搜索隐藏影响。
### 20.12.10 Alt
```text
Alt+Drag on Segment / Note -> force Move regardless of Body / Resize hit region
Ctrl+Alt+Drag              -> force Copy+Move where Copy Drag is supported
Alt+Left Drag in Velocity  -> force free trace regardless of direct bar hit
Alt+F4                     -> system close or task-specific close request
```
Alt 不绕过 Timeline Snap。需要 1 tick 操作粒度时关闭 Snap。
Alt 已实际参与 Timeline 强制手势时，主窗口必须仅消费该次 Alt KeyUp，并把键盘焦点恢复到发起手势的 Timeline；不得让主菜单因该次释放进入访问模式。未参与 Timeline 手势的普通 Alt、`Alt+F4` 和窗口失焦清理仍按原规则处理。
初版不定义 Alt-letter Access Keys，也不注册 `Alt+Left / Alt+Right` 导航历史快捷键。
### 20.12.11 鼠标修饰键
```text
Click       -> replace Selection
Ctrl+Click  -> toggle
Shift+Click -> range or add where defined
Normal Drag -> move or reorder
Ctrl+Drag   -> copy only where explicitly supported
Alt+Drag    -> force the target's approved alternate gesture
```
未支持的复杂组合不猜测用户意图；无法安全解释时 Drop Invalid。
### 20.12.12 Backspace
Text / Code 中删除前一字符；其他上下文 No Action。
Backspace 不删除 Project 对象、不返回导航、不关闭 Tab。
### 20.12.13 明确不注册
```text
Ctrl+W
Ctrl+Shift+W
Ctrl+Q
Ctrl+R
Ctrl+E
Ctrl+M
Ctrl+Shift+F
Alt+Left
Alt+Right
Other letter-only tool shortcuts
Number-key tool shortcuts
Global compile shortcut
Global MIDI Export shortcut
Global Audio Render shortcut
Global Reset Playback Engine shortcut
```
特别是 `Ctrl+W` 在任何 Workspace、Dialog 或 Project 状态都完全不注册。

`D`、`S`、`E` 是上一表批准的唯一 letter-only tool shortcut。它们只在无修饰键、主窗口无活动 Modal / Popup / Menu / Inline Editing Session，且焦点不在 TextBox、PasswordBox、RichTextBox、ComboBox 或代码编辑器时生效；否则按焦点控件的文本输入或本地交互处理，不得切换背景 Workspace 工具。

主窗口必须按当前键盘焦点控制 Windows Input Method：`TextBox`、`RichTextBox`、`PasswordBox`、可编辑 `ComboBox` 及其内部编辑元素启用 IME，其他焦点目标禁用 IME。焦点进入或离开真实文本编辑控件时必须立即恢复对应状态，使中文输入法不能截获非文本 Workspace 的 `D` / `S` / `E`，同时不妨碍文本输入。该规则只设置当前 WPF 焦点目标的 `InputMethod.IsInputMethodEnabled`，不得切换、关闭或持久修改用户的系统输入法、Preferred IME State 或输入语言。
### 20.12.14 无效快捷键反馈
可预期的 No Action 不弹窗、不播放声音。
持续锁定导致命令不可用时，可在 Status Bar 短暂显示原因。
---
## 20.13 UI Language and Numeric Formatting
### 20.13.1 界面语言
初版固定 English UI：
- 不提供语言选择；
- 不提供语言包；
- 不支持运行时切换。
用户名称和 Metadata 允许完整 Unicode，不自动翻译、转写、大小写或全半角转换。
### 20.13.2 数值输入
固定使用：
```text
Decimal point: .
Negative sign: -
Digits: ASCII 0-9
Thousands separator: none
```
不跟随 Windows Locale，不接受逗号小数。
普通数值字段不支持：
```text
Expressions
Fractions
Scientific notation
Unit suffixes
NaN
Infinity
```
Integer 和 Double 是明确字段类型；Integer 不接受 `1.0` 并静默转整数。
非法输入不提交、不 Clamp、不创建 Undo；失焦恢复最后合法值。

唯一已批准例外是 Event Instrument 与 SubVoice 的 Initial State MIDI 数值：输入必须先满足整数语法；语法正确但超出该正式 MIDI target 值域时，在提交前自动 Clamp 到目标下/上界。格式错误、目标类型不支持或引用失效仍拒绝；Clamp 后与原值相同时不创建 Undo，发生实际变化时整个 Dialog 仍只提交一个原子命令。

Double 显示去除无意义尾随零并避免浮点噪声，但显示格式不得修改内部值。
单位显示在字段外。
### 20.13.3 时间格式
Project 音乐位置：
```text
Bar:Beat:Tick
```
- Bar 1-based；
- Beat 1-based；
- Tick offset 0-based；
- Project 起点 `1:1:0`。
Beat 按当前 Time Signature 分母单位计算；初版不推断复合拍大拍。
每个 Time Signature 必须满足 `4 × TPQ % denominator == 0`，因此 Beat 长度和 Tick offset 均使用整数 Project tick。Time Signature 变化 tick 立即显示为新 Bar 的 `Beat 1:Tick 0`；若旧小节被截断，不存在的旧小节尾部坐标不得解析或吸附。
Segment local time 和 Template time 使用独立 tick。
长度和 Delta 使用 tick，不使用绝对位置格式。
### 20.13.4 音高与编号
```text
MIDI Note 60 -> C4
Black keys   -> sharps
Port         -> 1-16
Channel      -> 1-16
Program      -> 0-127
```
用户侧 Track 和 SubVoice 显示顺序编号使用 1-based。
### 20.13.5 Enum
主要显示 item name；显式整数值可在对象所属 Properties 或 Tooltip 中补充。
### 20.13.6 日期时间
固定：
```text
yyyy-MM-dd HH:mm:ss
```
使用本机时间和 24 小时制。
### 20.13.7 Copy 文本
Copy 出的文本使用同一固定数值和音乐位置格式。
Mixed、Inherited、Default 和 Override 使用状态文字，不用特殊数值冒充。
### 20.13.8 排序
数值列按真实数值排序；音乐位置按 absolute tick 排序。
UI 格式与 `.midora` 序列化格式严格分离。
---
## 20.14 UI Preference Persistence
### 20.14.1 保存位置
Application Preferences 自动保存于 `<ProgramRoot>\Data\Preferences`；Recent、Catalogs、Presets 与 Diagnostics 分别保存于 `<ProgramRoot>\Data\Recent|Catalogs|Presets|Diagnostics`。Midora 不探测、不读取、不迁移旧 `%LOCALAPPDATA%\Midora`，也不在写失败时 fallback 到用户目录或系统临时目录。
Preference 自动保存不等于 Project Autosave。
### 20.14.2 持久化内容
```text
Window position and maximized state
Major splitters
Follow Playback preference
Default lane height
Recent directories by picker purpose
Selected playback output device ID or System Default choice
Playback Master Volume
Playback Limiter
Stop Cursor Behavior
Render-Ahead Buffer
Device Buffer Request
Realtime Maximum Sample Voices per Unit Stream
Maximum Reusable Audio Cache Bytes
Ordered SoundFont list and target mappings
Appearance Language
```
### 20.14.3 不持久化内容

以下内容不进入 Application Preferences；除第 3.11、16.33 节显式允许的 Project presentation 外，也不进入 `.midora`：
```text
Current Tool
Zoom
Scroll
Playback Cursor
Edit Cursor
Time Range
Curve and Tempo view range
Workspace Tabs
Active Workspace
Navigation History
Selection
Tree expansion
Search and filter
List scroll
Mute and Solo
Playback state
Task History
Runtime Diagnostics
Modal Mapping Function edit buffer
Arrangement Grid / Snap and default creation values
Shared piano-roll Grid / Snap and default creation values
当前设备枚举结果
设备实际采样率
设备实际 buffer 和 callback period
IPC 连接与队列运行状态
Audio cache reusable 当前占用
Audio cache transient 当前与峰值占用
Audio cache session 路径、retention 状态和 Warning
```
### 20.14.4 Recent Directories
分别保存：
```text
Open Project
Save and Save Copy
SoundFont
MIDI Export
Audio Render
```
仅作为 File Picker 起始位置，不是 Project 默认输出路径。
### 20.14.5 禁止持久化的决定
以下决定绝不保存：
```text
Overwrite authorization
Close without Saving
Discard modal edit buffer
Delete confirmation
Cancel rendering confirmation
```
### 20.14.6 Preference 失败
Preference 读写失败：
- 已经成功启动后的单项 JSON 损坏不影响 Project，使用安全默认值并显示非模态 Notice；
- ProgramRoot 或 `Data/.tmp` 启动能力探测失败时必须在主窗口创建前明确阻止启动；
- 不得 fallback 到 `%LOCALAPPDATA%`、`%TEMP%`、当前工作目录或其他目录；
- 不标记 Project Modified。
Preference 版本与 `.midora` File Format 版本独立。
### 20.14.7 Reset 命令
```text
View > Reset Layout
View > Reset Editor View Preferences
View > Reset All UI Preferences
```
Reset Layout：
- 不关闭 Workspace；
- 不清除 Selection；
- 不修改 Project。
Reset All UI Preferences 需要简短确认。
### 20.14.8 初版不提供
```text
Preference Import or Export
Preference Sync
Preference Profiles
Theme preference
Accessibility preference
DPI override
```
### 20.14.9 初版默认值
```text
Follow Playback: Disabled
Current Tool: Select
Playback Output Device: System Default
Render-Ahead Buffer: 100 ms
Device Buffer Request: 50 ms
Playback Master Volume: -0.1 dB
Playback Limiter: Enabled
Stop Cursor Behavior: Return to Playback Start
Language: English
```
---
## 20.15 Window Sizing and 100% DPI Boundary
### 20.15.1 正式验收环境
```text
Windows Desktop
100% Display Scaling
Midora Built-in Theme
```
### 20.15.2 主窗口行为
支持标准：
```text
Move
Resize
Minimize
Maximize
Restore
Close
```
首次启动使用安全普通尺寸，不默认最大化。具体像素值在原型阶段决定。
必须有正式最小尺寸；达到最小时仍保证：
```text
Main Menu
Core Transport
At least one Workspace Tab
Usable Active Workspace area
```
### 20.15.3 滚动与面板
整个应用不出现全局二维 Scrollbar；滚动只存在于具体内容区域。
Active Workspace 及其内部可调侧栏/下部编辑区均有各自的最小可用尺寸。
窗口缩小时不自动永久改变用户折叠偏好。
### 20.15.4 Toolbar 与 Tabs
Toolbar 宽度不足时使用 Overflow。
Workspace Tabs 单行，使用滚动和 Tab List。

共用右键菜单及顶栏下拉菜单的图标列为 28 DIP，包含完整 20 DIP 图标空间和 8 DIP 图文间距，支持现有 16/20 DIP 图标；间距不得侵占图标可用宽度造成右侧裁切。Checked 状态的对勾使用同一列并与正文分开；主菜单顶级标题仍隐藏此列。菜单图标保持现有抗锯齿，不为了时间线像素对齐而全局关闭。

Timeline Toolbar 的 Grid / Snap 选择框只显示 `Bar` 或简写分数（例如 `1/8`），选择后显示文本必须立即更新并与实际生效值一致；不得因可编辑文本与选择项绑定冲突而显示额外错误色块、空选择或完整说明文字。Arrangement、Segment 和 SubVoice 的顺序统一为 `Grid + 下拉 | Snap + 下拉 | Length [Vel] | zoom_out zoom_in | 工具图标`，其中 Arrangement 不显示不适用的 Vel；各组之间显示分割线。

应用内 Tooltip 的初次显示延迟统一为 250 ms；该设置只影响提示出现时机，不改变 Hover、焦点、Pressed 或命令触发语义。

主菜单置于标题栏时，固定 29-pixel 高的一级菜单容器必须作为整体在 36-pixel 标题栏内纵向居中；标题栏不显示额外 `MIDORA` 文本或 Main Menu 两侧竖向分割线，Application mark、Main Menu 与 Project display name 通过留白分组。Project display name 与 Main Menu 保持 8-pixel 外间距并使用 12-pixel 字号；外框不固定高度且不设置 Padding，按水平居中的文本 `8,2` Margin 与 1-pixel 边框自动测量，整体垂直居中，文本和圆角外框分别使用 `Brush.Text.Tertiary` 与 `Brush.Border`。Hover、键盘高亮和子菜单打开状态使用完整四角圆角背景。菜单和窗口控制按钮必须标记为 WindowChrome 交互区域，点击不得触发窗口 DragMove；标题栏其余空白仍可拖动。

Arrangement Segment 使用较深的低饱和蓝灰色；选中 Segment 使用同色系强调边框和更深背景，Note Preview 使用高亮但低饱和的蓝灰色。Segment Piano Roll 的 active range 保留基础键位底色，界外范围进一步压暗；未选中 Note 使用高亮蓝灰色，选中 Note 的红色填充与红色边框保持不变。Velocity 未选中柱使用相同蓝灰色，选中 Note 对应柱使用红色；每个 Note 只在 start tick 显示固定窄柱，柱顶显示明显更宽的方形 onset marker，柱宽不得随 Note 长度变化。Piano Roll 白键行使用较亮底色、黑键行使用较暗底色；Segment 与 SubVoice Pitch Ruler 使用完整白键和较短黑键的钢琴外观，并且只在每个八度 C 键显示符合 MIDI 60 = C4 的音名。空 Timeline 不显示覆盖画布的 `No timeline content` 卡片。Disabled Ghost Button 不保留背景或边框。Transport 的位置与 BPM 使用亮色并以竖向分割线分隔；Play 图标不得裁切。Parameter / Event Lane 不显示额外白色外框。数值标尺顶部和底部标签不得被视口裁切。

Velocity 低水平缩放时，onset marker 仍保持固定 device-size、不得因 Note 长度压缩或相邻柱合并而选择性消失。Event Instrument 底部 Preview Keyboard 的 velocity 必须按实际命中的键自身可见长度归一化；黑键不能复用白键全高计算，否则黑键底端无法达到最大 velocity。

ComboBox 的可编辑文本和下拉指示必须分别在内容区与按钮区垂直居中；下拉指示使用同一 Fluent 图标体系，不得使用字体符号代替。显式垂直 ScrollBar 的 Track 必须完整铺满可用高度；Thumb 长度必须按当前可见范围相对完整有界范围的比例计算，不得使用与视口无关的固定值。Diagnostics Workspace 的筛选 ComboBox 和 Segment Piano Roll 顶部左侧文本不得裁切或偏离垂直中心。Timeline 和 piano roll 的显式垂直 ScrollBar 必须始终占据其布局位置；无可滚动范围时只 Disabled，不得 Collapsed 或以透明 Disabled 样式消失。

ComboBox 下拉内容打开时，鼠标滚轮必须由该下拉表面消费；即使当前项数不足以显示垂直 ScrollBar 或滚动位置已经到达边界，也不得把同一滚轮手势路由到外层 ScrollViewer。该规则由共享 ComboBox template 统一提供；非 ComboBox 的代码补全、Menu 与自定义 Popup 按各自交互规格处理。
展开 ComboBox 的指针仅停留在上下边缘时不得自动滚动，包括悬停焦点触发的 BringIntoView；必须保留实际滚轮、滚动条拖柄/按钮、键盘导航和显式定位请求，不能冻结全部 offset。隐式、显式、keyed、可编辑样式必须共用此规则，不引入永久轮询。

Instrument Catalog 的 Banks、Programs、Scan Presets 的源 SF2 选择列表及 bank 结果列表使用逻辑项滚动：每个标准 wheel 刻度移动一项，高精度不足一刻度的余量由每个控件独立累计，反向时清除相反方向余量。列表到顶/底或不足一屏时仍消费该手势，外层表单不得一起滚动；不改变 Selection，不取消虚拟化。普通像素滚动表面不套用逻辑项单位。
### 20.15.5 Resize 语义
Resize 只改变视图，不：
```text
Move Project objects
Scale Project data
Fit content automatically
Change Selection
Switch Workspace
Commit field edits
```
Pointer Capture 因 Resize 丢失时，取消当前编辑手势并恢复原状态，不创建 Undo。

Segment 下部 Lane 编辑区的分隔条调整下部编辑区与“Piano Roll + Timeline Overview”整体上部区域之间的高度分配；不得把 Timeline Overview 单独作为相邻 Resize 目标。该操作只改变 Project Session UI State，不修改 Project 内容或 Timeline zoom。
### 20.15.6 窗口状态持久化
保存 Normal Window Bounds 和 Maximized State，不恢复 Minimized State。
显示器或工作区变化时，恢复必须保证 Title Bar 和主窗口可见、可操作。
最大化不改变 Panel、Zoom、Selection 或 Tabs。
最小化不自动停止播放、Preview、Compile 或长任务。
### 20.15.7 Dialog
复杂 Configuration、Progress 和 Result Dialog 可调整尺寸；普通 Confirmation 通常内容自适应且不可调。
所有模态 Dialog 必须有明确 Owner。
Dialog 主操作始终可达，长内容内部滚动。
带确认提交的 Dialog 使用共享、统一宽度的红色 Primary 主按钮和共享 Cancel 按钮；主按钮为 Enter 默认动作，Cancel 为 Escape 取消动作，不得由各 Dialog 分别复制样式和键盘契约。
每个 Dialog 只允许注册一个 Escape Cancel target。标题栏关闭按钮必须调用同一取消语义，但不得同时设置 `IsCancel` 与底部 Cancel 争用 Escape；因此 Escape 在 TextBox、List、Button 或普通空白焦点下均产生相同取消结果。打开的 ComboBox / Popup 等内层临时状态仍按第 20.12.7 节先消费第一次 Escape。
Dialog 尺寸和位置不跨重启持久化。
Context Menu、Dropdown、Popup、Tooltip 和 Drag Tooltip 必须保持在当前工作区内。
### 20.15.8 DPI
初版只正式验收 100% DPI。
WPF 自然缩放能力可以保留，但初版不承诺：
```text
Per-monitor DPI
Mixed DPI
Non-100% DPI validation
Special layout tuning for scaled displays
```
---
## 20.16 初版基础交互可用性边界
初版正式范围：
```text
Mouse
Keyboard and mouse combination
Clear keyboard focus visual
100% DPI
One Midora built-in theme
English UI
```
键盘焦点必须与以下状态明确区分：
```text
Selection
Primary Selection
Active Workspace
Hover
Read-only
Disabled
```
常用鼠标目标必须有合理命中区域；极端精度需求通过对象所属的精确属性字段或显式 `Properties...` 完成。
无效操作必须提供明确视觉反馈，但不需要语音或自定义音效。
初版明确不承诺：
```text
Complete keyboard-only workflow
Screen reader support
Accessibility announcements
High Contrast theme support
Access Keys or mnemonics
Custom UI sound effects
Reduce Motion setting
Large Text mode
Non-100% DPI validation
Per-monitor DPI handling
Touch-first interaction
Voice control
Braille or sonification support
```
现有 F6、Tab、常规快捷键和清晰焦点属于基础桌面交互能力，不代表完整辅助功能支持。
---
## 20.17 初版明确不支持的 UI 与工作流能力
```text
Multiple open Projects
Multiple Midora processes
Multiple Main Windows
Floating or detachable Workspaces
Docking system
Traditional Save As
Autosave
Crash recovery
Project repair scanner
Global Project Search
Project-wide Replace
Project object clipboard across Projects
Workspace restoration across application restart
Pause
Scrubbing
Count-in
Legato editing
Tempo Ramp editor
Take Lanes
Segment names
Same-Track Segment overlap
Full keyboard-only operation
Screen reader-specific UI
High Contrast and theme system
Access Keys
Custom UI sounds
Non-100% DPI acceptance commitment
Touch-first controls
Drag-out export to Windows Explorer
MIDI, audio or arbitrary-file import by generic drag-and-drop
```
---
## 20.18 跨界面一致性规则
### 20.18.1 Segment
- 同一所属 Logical Track 或 Pure MIDI Track 内不允许 Segment 重叠；
- 不同 Track 可处于相同时间；
- Segment 没有名称；
- active crop window 外内容保留并可编辑；
- Arrangement 显示 Note Preview；
- 详细内容只在 Segment Editor 编辑。
### 20.18.2 Mapping Function Modal Edit
- 名称与表达式缓冲只属于当前模态对话框；
- Validate 不修改 Project；
- OK 在正式 ABI v3 验证成功后以一次原子命令提交；
- Cancel/关闭直接丢弃，不保留跨窗口 Draft；
- Ctrl+S 与 Global Save 只保存 Project；
- 本地缓冲不进入 `.midora`。
### 20.18.3 Save
- 初版只有 Save 和 Save Copy；
- Save Copy 不切换当前路径；
- Save / Save Copy 在事务开始后不可取消；
- Damaged Placeholder 存在时两者均禁止。
### 20.18.4 Mute / Solo
- 只属于播放运行期；
- 不保存；
- 不进入 Undo / Redo；
- 不影响 MIDI Export 或 Audio Render；
- 正式输出只服从显式 Track Selection。
### 20.18.5 Search 与 Selection
Collection Search 隐藏对象时保留稳定 Selection；Editor Content Filter 隐藏对象时可从 Active Selection 移除。
### 20.18.6 Broken / Incompatible / Damaged
三者不得合并：
```text
Broken      -> target reference no longer exists
Incompatible -> data exists but current setting disables its semantics
Damaged     -> persistent source content could not be loaded
```
### 20.18.7 SoundFont
```text
No Enabled SoundFont -> legal application state
Configured           -> enabled local paths are stored; latest eager Worker initialization failed or has not yet completed
Loading              -> Saving Settings or Project session activation is rebuilding/adopting the Worker and loading the frozen enabled list
Loaded               -> retained current backend loaded the frozen enabled list successfully
Missing or Failed    -> playback, preview and render unavailable
```
Compile 和 MIDI Export 不依赖 SoundFont 加载。

新建、打开、命令行打开或从 MIDI 导入形成 Project 会话时，只要存在 Enabled SoundFont，前台任务必须在 Project 会话提交后立即尝试预热 Worker。验收必须区分两种结果：Project 会话失败时保持原会话；Project 会话已经成功但 Worker 预热失败时保留新会话、显示可查看全文的音频初始化错误，并令后续 Play/Preview 可以重试。不得把后者包装成 Project 打开失败，也不得要求用户先按一次 Play 才创建 Worker。Reset Playback Engine 清理后同样立即预热。
### 20.18.8 English UI 与 Unicode
内置标签和消息为 English；用户作品文本允许 Unicode。
### 20.18.9 自动修复
不得通过 UI 自动改变音乐语义、删除数据或按名称重绑。
---
## 20.19 实现与验收边界
本章要求实现时至少保证：
1. 主要 Workspace 的职责不重叠；
2. 同一对象重复打开时激活已有 Workspace；
3. Project 编辑、UI Preference、Session State 和 Draft 状态严格分离；
4. 播放、编译、保存、导出和渲染遵守锁定矩阵；
5. 焦点敏感快捷键不会误操作背景 Selection；
6. 右键、工具栏、菜单和快捷键调用同一命令语义；
7. 批量编辑、Paste 和 Drag 的 Project 修改保持原子；
8. Broken 和 Damaged 数据不被静默删除或按名称修复；
9. MIDI Export 和 Audio Render 显示各自不同的结果原子性；
10. 无 SF2、空 Definition Index、空 Arrangement Track order、合法无 Usage 空壳 Track、空 Track 和空 Segment 均显示为合法空状态；
11. 状态和错误不只依赖颜色；
12. 后台更新不抢焦点或改变 Selection；
13. `.midora` 只保存第 3.11、16.33 节显式版本化的 Project presentation 白名单，不保存其他 UI View State；
14. 初版所有正式 UI 文案使用 English；
15. 初版正式尺寸与布局验收以 Windows 100% DPI 为准；
16. Segment Editor 左侧 Pitch Ruler 按下即开始、松开即结束单键 held Preview，且不创建 Project Note；
17. 新建单个 Logical Note 的放置手势只提供虚线视觉预览，不启动音频 Preview；
18. Pitch Ruler 与 Event Instrument / SubVoice 虚拟键盘复用同一因果 Gate、`Int64.MaxValue` 哨兵、未渲染 frontier、互斥、零分配和清理规则，不存在独立裸 MIDI 路径；
19. 无 SF2、已有播放任务或输出不可用时，合法单音符放置仍可提交，并且只形成一个 Project Undo。
20. Instrument Catalog Editor 不显示内部 ID，Cancel 零写入，Replace/Merge 在一次确认后原子保存；SF2 scan 不读取 sample payload、不重建 Worker。
21. `Add Event Binding...` 对 current/multiple/all SubVoice 一次提交；缺 owner只建空入口、不建 tick 0 event，任一失败无 partial Project edit。
22. Pure MIDI Track 新建/导入颜色按固定 palette 确定轮换，Duplicate继承；Logical Track override 继续只影响本 Track。
具体像素、控件类、颜色值、动画参数和内部实现应在 UI 原型、实现设计或实现设计继续确定，但不得改变本章已经明确的交互语义和系统边界。
