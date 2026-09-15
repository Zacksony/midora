# 第 18 章 编辑工作区与编辑器

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Arrangement、Segment、Event Instrument、SubVoice、Mapping、Lifecycle、Conductor、Settings 和 Diagnostics 等主要工作区的职责与布局。初版不再提供独立 Event Instrument Library Workspace。

## 18.1 Arrangement Workspace
### 18.1.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Arrangement Toolbar                                                  |
+----------------------+---------------------------------------------------+
| [B] Track Header Area| [C] Timeline Header                              |
+----------------------+---------------------------------------------------+
| [E] Track List       | [D] Ruler and Marker Area                        |
|                      +---------------------------------------------------+
|                      | [F] Arrangement Timeline                          |
|                      |                                                   |
+----------------------+---------------------------------------------------+
| [G] Horizontal Scroll and Overview                                       |
+--------------------------------------------------------------------------+
```
### 18.1.2 职责
Arrangement 只负责：
```text
Conductor-first global mixed Logical / Pure MIDI Track order
Event Instrument Usage / MIDI Channel Root shared execution membership
Track order, shared-block presentation, rebind and routing
Segment creation, movement, copy, crop, split and deletion
Project timeline navigation
Segment-level arrangement overview
Track Mute/Solo runtime controls
```
Logical Note/Parameter 与 Direct MIDI Note/Event 的细节进入对应 Segment Editor。
Tempo、Time Signature、Key Signature、Marker 和 Project End Marker 只显示概览；精确编辑进入 Conductor Track Editor。
### 18.1.3 Track Header
显示：
```text
Track display name
Event Instrument Definition or MIDI route summary
Pure MIDI Track 的 MIDI 类型标识
Track color
Mute
Solo
Track validation summary
```
Track 的 Mute / Solo 固定可见，属于播放期运行状态：
- 不保存；
- 不进入 Undo / Redo；
- 不标记 Project Modified；
- 不影响 MIDI Export；
- 不影响 Audio Render。
Track 高度属于 UI 状态；Track 正式顺序属于 Project Content。

Header 是独立交互目标。鼠标悬停时使用低强调高亮，按下时背景变暗，松开恢复；越过通用拖动阈值后才开始重排并显示准确插入线。全部 Logical / Pure MIDI Track 平铺且右侧承载 Segment。连续共享 Auto Root 或共享 Usage 的 Track 在 Header 左侧以 brace gutter 表示一个共享块；Fixed route 只作为 Pure MIDI Track 的路由属性呈现。完整 Header 字段、菜单、复制删除、拖动、加入/移出共享组和命中区见第 24.3～24.7 节。

Logical Track 可处于未绑定空壳状态，也可绑定独立或共享 Event Instrument Usage；Header 菜单提供选择 Definition、共享状态与拆分为独立 Usage。Pure MIDI Track 名称左侧必须显示 MIDI 图标；Header 菜单与拖放可改变 Auto 共享组或 Fixed route。Track Mute/Solo 不改变 Usage/Root 源数据。
### 18.1.4 Segment 显示
Segment 没有名称。矩形显示：
```text
Track color context
Content summary
Active crop window
Broken or validation state
Logical Note preview
或 Direct MIDI Note preview
```
初版必须在 Segment 矩形内显示简化 piano-roll Note Preview，表达音高、相对位置和长度。该预览只用于概览，不允许在 Arrangement 内直接精细编辑 Note。

Preview 使用固定 MIDI pitch `0..127` 的二维投影；Note 最小可见高度为 1 px，位置与边界执行布局取整，Segment 本地 tick 0 的 Note 不得因左边界或可见范围查询而遗漏。Preview 必须在 Arrangement 的手工渲染面内绘制，不得为每个 Note 创建 WPF Control。实现应按 Segment 稳定 ID 与 preview 相关内容指纹复用缓存；缩放、平移、选择或播放指针变化不得重建未变化 Segment 的 preview 内容。

Pure MIDI Segment 必须在 Note 上层绘制独立缓存的 non-Note event 线；Logical Segment 必须在同一层绘制 Logical Parameter point 线。两者统一使用 50% 透明度、最小 1 device pixel，并按对应正式值域归一化高度。详细 LOD、同列聚合、裁剪和独立失效规则见第 24.11 节。
### 18.1.5 Segment 重叠
正式规则：
```text
Same owning Track        -> Segment overlap is not allowed
Different Tracks         -> time overlap is allowed
Adjacent Segments       -> allowed; no automatic join
```
移动、复制、粘贴、绘制或调整边界若造成同一 Track Segment 重叠：
- 显示非法预览；
- 整体拒绝提交；
- 不自动缩短；
- 不移动邻近 Segment；
- 不寻找最近空位；
- 不自动创建 Track。
该规则只约束 Segment 容器，不等同于 Logical Note、Direct MIDI Note 或编译后 Event Instrument Instance 的重叠规则。
### 18.1.6 Segment 操作
```text
Drag body  -> move Segment
Drag edge  -> change active crop window
Ctrl+Drag  -> copy Segment when target is valid
Split Tool -> split at target tick
```
同类型移动保留全部 Note、Lane、Broken / Inapplicable 数据、direct/opaque 数据和 crop 外内容；不得按名称修复参数。Logical/MIDI 跨类型拖动、复制与粘贴遵循第 20.6.14 节的完整内容转换、一次损失确认与失败原子性；目标 Track 不得重叠。
### 18.1.7 Timeline
支持：
```text
Select
Draw
Split
Grid
Snap
Zoom
Playback Cursor
Edit Cursor
Time Range Selection
```
Project End Marker 后区域弱化，但仍显示并允许编辑。End Marker 不是右编辑边界。
无显式 End Marker 时可显示 Natural End 参考，但它不是 Project 对象。

Arrangement Toolbar 提供 Grid 开关，但不提供可见 Grid 粒度选择；启用时固定使用 `Bar`。操作粒度与 Snap 仍独立可选，并提供默认 Segment 创建长度（tick，输入即生效）；新建 Project / 重置编辑器时默认操作粒度为 `1/8`、Snap 开启、默认 Segment 创建长度为 `1 × TPQ`。小节边界使用主实线，每个分母拍的内部边界使用颜色更浅的低强调实线；拍线必须读取完整 Project Time Signature Map，因此 `3/4` 每小节显示 2 条四分音符间隔实线，`6/8` 每小节显示 5 条八分音符间隔实线，拍号变化 tick 立即作为新的主实线小节边界。极端水平缩小时必须按 device-pixel 最小间距聚合/跳过不可辨识的竖线，绘制成本不得随不可见的小节或拍数量线性增长。顶部 Timeline Ruler 显示一基小节号而非原始 tick。Draw 模式下，鼠标所在 Track 必须显示按当前操作粒度定位、按默认长度计算的虚线创建预览。空白处按下左键后进入 Segment 放置手势：未越过拖动阈值时按默认长度创建；向右拖动时按当前操作粒度实时调整结束 tick，松开后一次性提交。若请求长度超出当前可用间隙，创建命令静默缩短为从目标 tick 起可容纳的最大正长度；预览显示实际将提交的长度。不存在正长度空隙时预览为错误色并拒绝创建。该规则只适用于新建 Segment；已有 Segment 的移动和 Resize 仍不得因重叠而被静默缩短。

Arrangement 左侧 ruler header 在重置全部 Track Mute/Solo 按钮之前提供 Fluent `zoom_out` / `zoom_in` 纵向缩放按钮，并继续支持 Track header 上的 `Ctrl + Mouse Wheel` 纵向缩放。Conductor 固定行不是 Segment host；Draw 模式不得在其内容区显示 Segment 创建预览，也不得以该行作为 Segment 创建、框选或对象编辑手势起点。左键单击 Conductor 内容区仍必须按 Arrangement Snap 设置更新 Edit Cursor；双击不得因此创建对象。

Arrangement 中只有 Draw 模式允许拖动 Segment 主体或调整边缘；Draw 模式左键按下已有 Segment 时必须先清除其他 Segment 选择并只保留本次目标，随后再按命中区域进入 Move/Resize。Select 模式的单次左键按下始终发起框选，即使起点位于 Segment 上也不得先命中或单独选择该 Segment，并且不得直接移动、Resize 或双击创建 Segment。既有双击导航不受该单击规则影响。拖动和 Resize 期间必须显示位置与长度预览，并隐藏同位置的创建预览。工具互斥、指针和快捷键规则见第 20.1.6、20.12 节。

Draw 模式下，`Alt + Left Drag` 在 Segment 的任意命中位置强制执行 Move，即使指针位于左/右 Resize 边界；`Ctrl + Alt + Left Drag` 强制执行复制并移动。该替代手势仍服从当前 Snap 设置。操作类型及复制意图在按下时冻结，拖动途中改变修饰键不得在 Move、Copy 与 Resize 之间切换。

Draw 模式下在 Segment 主体执行 `Ctrl+Drag` 时，复制当前 Segment 选择集并以一个共同时间/Track delta 放置完整副本；原 Segment 不移动。复制成功后只选择副本，一次完整手势形成一个 Project Undo。
---
## 18.2 共享 Segment Editor
### 18.2.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Segment Toolbar                                                      |
+--------------------------------------------------------------------------+
| [B] Segment Toolbar Continuation                                         |
+-------------+------------------------------------------------------------+
| [D] Pitch   | [C] Local Timeline Header                                 |
|     Ruler   +------------------------------------------------------------+
|             | [E] Note Editor                                            |
+-------------+------------------------------------------------------------+
| [F] Lane List| [G] Logical Parameter Editor                              |
+-------------+------------------------------------------------------------+
| [H] Horizontal Scroll and Segment Range Overview                         |
+--------------------------------------------------------------------------+
```
### 18.2.2 时间坐标
Logical Segment Editor 与 Midi Segment Editor 均以 Segment local tick 作为编辑、命中和命令坐标；左上角不重复显示 `(MIDI) Segment: <Name> @ <Tick>` 摘要。Timeline Ruler 可以把 local tick 通过 `ProjectStartTick - ContentOffsetTick` 映射到正式 Project Time Signature Map，并显示对应的一基小节号，但不得改变 local tick 数据语义或把 local tick 本身伪装成 Project `Bar:Beat:Tick`。

Logical Segment、Midi Segment 与 SubVoice Piano Roll 的顶部工具栏必须显示当前指针的 `(local tick, MIDI Key Number)`，并在读数右侧以分割线隔开后续工具；读数使用 Primary text 前景色，与 Event/Parameter Lane 坐标读数一致。tick 使用当前 Operation Grid/Snap 的正式目标坐标，Key Number 使用 `0..127`。指针不在对应 Piano Roll 内容区，或落在 128 键之外的空白区时，分割线与读数整体隐藏。该读数是会话期 transient UI state，不进入 Project、Undo/Redo、编译、缓存或持久化。

用户从 Arrangement 显式打开 Logical 或 Pure MIDI Segment 时，若 Arrangement Edit Cursor 位于该 Segment 的 Project 范围 `[ProjectStartTick, ProjectStartTick + LengthTicks)`，Segment Editor 必须把它映射为 `ContentOffsetTick + (EditCursorTick - ProjectStartTick)` 的local edit cursor，并把该位置水平置于当前viewport中央；靠近local tick 0而无法严格居中时只允许把viewport起点clamp到0。该规则同样适用于显式重新打开已有Segment Tab；仅通过Tab切换返回既有编辑器时保留原viewport。Arrangement Edit Cursor不在Segment范围内时不得因此改动Segment Editor的既有viewport。
### 18.2.3 Pitch Ruler
- 使用完整白键底板与较短黑键叠层构成的真实横向钢琴键样式；
- MIDI Note 60 显示为 C4；
- 音名只在每个八度的 C 键显示，其他键不显示音名；
- 初版不在键位上写 MIDI Note 编号；若其他 UI 必须显示黑键音名，仍使用升号；
- 鼠标左键按下键位发起 held Preview，松开或取消结束 Gate；
- 该预览不创建 Project Note，并严格使用第 13.22.7、13.24.5 节的因果 Gate 与当前 Segment 绑定的 Event Instrument。
- Pitch Ruler 不提供右键语义；右键按下必须由 Ruler 自身消费，不显示空 Context Menu，也不得把事件冒泡到 Timeline Canvas。
### 18.2.4 Note Editor
使用共享 piano roll 编辑 Logical Note 或 Direct MIDI Note；数据访问、命令提交和诊断通过领域 adapter 区分，不得复制第三套渲染/命中测试/选择/手势实现。

新建单个 Logical Note 的放置手势不得启动声音预览。Draw 模式只显示当前 pitch、位置和默认长度的虚线视觉预览；该视觉预览不是 Project 数据。点击已有 Note 时，将该 Note 的长度复制为后续创建的默认 Note 长度，但不修改该 Note。

只有 Draw 模式允许拖动或 Resize Logical Note；Select 模式的单次左键按下始终发起框选，即使起点位于 Note 上也不得先命中或单独选择该 Note，并且不得直接移动、Resize 或双击创建 Note。移动和 Resize 期间必须显示位置与长度预览，并隐藏创建预览。

Draw 模式下在 Logical Note 主体执行 `Ctrl+Drag` 时，复制当前 Note 选择集并以一个共同时间/pitch delta 放置副本；原 Note 不移动，相对时间、音程、长度和 velocity 保持不变。复制成功后只选择副本，一次完整手势形成一个 Project Undo。边缘 `Ctrl+Drag` 仍按 Resize 处理，不隐式复制。

Draw 模式下，`Alt + Left Drag` 在 Logical Note 的任意命中位置强制执行 Move，`Ctrl + Alt + Left Drag` 强制执行复制并移动；这两种手势均仍服从当前 Snap。操作类型及复制意图在按下时冻结。SubVoice Template Note 复用同一规则。

Segment Toolbar 必须提供共享 piano-roll 的可见分割线粒度、操作粒度、Snap、默认 Note 长度（tick）与默认 velocity。默认长度和 velocity 独立于 Grid；默认长度允许小于操作粒度。

piano roll 左上角 ruler header 不显示 `BAR` 文本，而是提供紧凑的 Fluent `zoom_out` / `zoom_in` 按钮以逐 device-pixel 调整纵向 Key 高度。完整 `52 × 24` header 必须直接分成 `3 + 22 + 2 + 22 + 3` 水平列和 `3 + 18 + 3` 垂直行，两个按钮分别占用对称单元，不再使用嵌套容器的 Center 与布局取整推导位置。图标必须采用 Fluent 原生 `16 × 16` Geometry 和固定 `16 × 16` 设计画布，禁止按 Geometry 实际包围盒裁切后拉伸；按钮内容与设计画布均居中。由于 Segment 与 SubVoice 宿主的周边分割线造成不同光学中心，Logical/Pure MIDI Segment 共用 header 的整组按钮相对数学中心上移 2 device pixels，SubVoice 上移 1 device pixel。右侧与底部分割线必须作为不参与布局测量的 1 device-pixel 覆盖层绘制。新建或重置的 Segment piano roll 默认 `N = 15`（100% DPI），SubVoice piano roll 使用相同默认值。工具栏右侧的水平缩放按钮也使用相同 Fluent 图标，但不得与左上角纵向缩放命令混用。

共享 piano roll 的 Grid 与 Arrangement 一样只提供开关、不提供可见粒度选择；启用时固定使用 `Bar`，小节主线与每个分母拍的低强调子线必须通过上述 local→Project 映射与 Arrangement 精确对齐。SubVoice local tick 0 直接按 Project tick 0 的 Time Signature Map 显示。水平极端缩小时执行相同的 device-pixel 密度上限。顶部 Ruler 显示小节号。

piano roll 的每 Key 高度 `N` 固定为 device-pixel 整数，`N >= 3`；纵向缩放每一步只改变该整数。Note 的顶部 1 device pixel border 精确覆盖所在 Key 的上分割线，Note 总高度精确为 `N`，底部 border 停在下一条分割线前一 device pixel。左右 border、上下 border 与至少一 device pixel fill 在最小纵向缩放下均必须可辨；DPI 换算不得重新引入半像素高度或跨 Key 漂移。

active crop window 外内容：
- 保留；
- 可见但弱化；
- 可选择和编辑；
- 不因被编辑而自动进入编译范围；
- Snap 不强制把它拉回 active 区。
初版不支持 Legato，但保留统一 Logical Note 批量编辑入口，便于未来扩展。
### 18.2.5 Logical Parameter Lanes
Lane List 只允许添加当前绑定 Event Instrument 暴露的 Logical Parameters。
支持：
```text
Integer step points
Double step points
Enum step states
```
所有 Logical Parameter Point 都是离散突变点，不显示或编辑 interpolation；详细状态保持规则见第 9.8.3 节。
Broken Lane 保留数据并明确显示，不按名称自动重绑。
必须区分：
```text
Hide Lane
Delete Lane Data
```
Hide 不删除 Project 数据；Delete Lane Data 删除该 Lane 的用户内容，并按破坏性规则确认。

下部编辑区使用 target Tabs 和单个活动 Lane 编辑器，不得把所有参数 Lane 垂直压缩堆叠。该区域可隐藏、恢复和调整高度；显隐与高度只属于当前 Project Session UI State。高度分隔条必须位于“Piano Roll + Timeline Overview”整体上部区域与下部编辑区之间；拖动必须实际改变下部编辑区高度，不得只调整固定高度的 Timeline Overview。

三种宿主共用同一 Tab 行：`Vel.` 固定第一，MIDI Segment/SubVoice 的 `Inst.` 固定第二，二者不能隐藏；其余按正式 target 标识。首次发现普通 targets 使用确定顺序，新建追加，允许拖拽重排及滚轮水平滚动；关闭只隐藏，不删除点、Curve 或 Mapping owner。最右侧目录可搜索并虚拟化显示全部已有 targets，列出名称、精确数量、显隐及无点 Curve/Mapping 标记；未知数量明确显示统计中，失败显示不可用，不报假零。

右侧 `+` 在所有 target（包括 Inst.）统一执行 Add Lane，不因活动 Tab 改成创建包装。普通 Tab 的整个头部都能作为重排目标，不得要求命中狭小缝隙；基础 Tab 固定在排序前部，拖放只作用于当前 owner 的 Lane 会话，不冒泡为外层 Workspace 重排。

显式 Add/Locate/目录点击显示并激活目标，布局后焦点进入相应画布；普通刷新、选择变化、Properties、后台结果和 Undo 不自动显示已隐藏目标或抢回旧目标。切换不清除原选择。每个 target 独立记忆纵轴、共同水平时间轴；Piano Snap 与底部事件 Snap 独立，底部 targets 共用事件 Snap。顺序、显隐、活动目标和纵轴是 owner-local 会话状态，不写音乐 Source/Modified/Undo/canonical；本切片不新增 presentation 持久化字段。

目录摘要不得常驻第二份完整点数组。缺少摘要时仅允许一次有界、可取消后台补建，热读取按 target 数量工作，编辑/覆盖碰撞/Undo 的计数必须对应实际最终结果。隐藏项只保留轻量描述和状态，不各自建立画布；旧修订、卸载和关闭任务不能恢复旧选择或目标。

Velocity 视图按 Note start tick 绘制固定窄柱，高度表示 velocity；柱宽不表达 Note 长度，柱顶必须显示明显大于柱宽的方形 onset marker，以同时明确 Note start tick 和 velocity 顶点。同 tick 存在多个 pitch 时，按 pitch 从低到高绘制，使高 pitch 对应柱位于最上层；pitch 相同时按稳定 ID 确定顺序。

左键在空白处按下并拖动形成自由轨迹，右键拖动使用起止点直线轨迹；无选择时手势作用于轨迹经过的全部柱，存在选择时只作用于经过且已选择的柱。按住期间只显示轻量轨迹覆盖层，不逐柱重绘、不更新 Velocity tile，也不提交 Project；松开时根据完整轨迹一次性计算最终值、提交一次 Project Undo，并异步重建受影响 tile。单击而未移动仍以该点作为单点轨迹，包括 tick 0。Escape 或 mouse capture 丢失取消轨迹且不提交。

左键直接按住单柱或其 onset marker 上下拖动时，只调整命中的一个 Note，不显示轨迹；同 tick 重叠柱按上述最上层顺序命中。该单柱 transient 允许只覆盖一个柱，松开时提交。所有 Velocity 手势都不得改变 Note 的位置、长度或 pitch。

Logical/MIDI Segment 与 SubVoice 的 Velocity ruler 不建立 Time Range Selection；从 Velocity 内容区向 ruler 或视图外拖动时，当前 velocity 手势继续按 pointer capture 完成或取消，不得被 ruler 的时间范围手势截获。Segment Logical Parameter / Direct MIDI Event Lane 与 SubVoice Event Lane 的 ruler 服从同一“无 Time Range”规则。

`Alt + Left Drag` 必须强制使用自由轨迹手势：起点即使命中单柱或 onset marker，也不得进入单 Note 调整。该修饰键只覆盖 direct-hit 分流，不改变“存在选择时仅作用于已选择 Note”的过滤规则。

单个参数 Lane 编辑器左侧显示值标尺，右侧显示对应水平参考线；Lane 具有独立于 piano roll 的纵向缩放。参考值密度随纵向缩放调整。纵向缩放、滚轮平移、中键平移和右侧滚动条必须操作同一有界数值视口，标尺随视口更新，不得越过参数合法范围；顶部与底部标签保持在可视区域内。Integer 参数由指针纵坐标得到的值必须先按 `AwayFromZero` 取到最近整数，再执行合法范围验证。
### 18.2.6 同步
Segment 编辑后：
- Arrangement Note Preview 实时更新；
- 当前 Workspace 的 Selection 与已关闭后重新打开的属性投影按 Project revision 更新；已经打开的模态 Properties 目标与 Draft 保持冻结；
- 相关 Validation 和 Diagnostics 更新；
- 一次用户手势形成一次 Project Undo。

### 18.2.7 Logical 与 Pure MIDI 变体

Logical Segment 变体的下部 Lane 编辑 Logical Parameter，并通过 Track 绑定的 Event Instrument 解释 Note。Pure MIDI 变体的 Velocity 直接编辑 Direct MIDI NoteOn velocity，下部 Event Lane 直接编辑完整 Channel Voice Event；不显示 Logical Parameter 或 Event Instrument 绑定控件。

Pure MIDI `Add Lane` 使用与 SubVoice `Add Event` 一致的分步目标选择器，不得平铺数百个条目。Pure MIDI 允许选择 MIDI 1.0 的全部 CC `0..127`；已被 BASSMIDI 名称表识别者统一显示为 `CC <n> - <Name>`，未识别者显示为 `CC <n>`。活动 Event Lane Tab 与目录使用同一格式。

Pure MIDI 与 SubVoice 的 Pitch Bend Event Lane 统一使用 `−8192..8191` 显示标尺及指针坐标；中性刻度必须标为实际值 `0`，不能将非对称范围的算术中点 `−0.5` 标成 `−1`。Direct MIDI 正式 scalar 仍为 `0..16383`，显示值等于 scalar 减 8192；SubVoice 正式值仍为 `−8192..8191`。此显示转换不改变持久化、编码、编译结果或批量编辑的正式输入值域。

Pure MIDI 的 opaque SysEx/Meta 只在 Event List 中选择、移动和删除，并通过只读 `Properties...` 查看 payload 摘要；不提供自由 payload 编辑。两种变体必须共享 Grid/Snap/zoom/pan/scroll、tile cache、临时编辑覆盖层、批量选择与 Segment Content Window 行为；修复共享交互缺陷不得要求分别修改复制实现。

Pure MIDI 编辑器提交 Direct Note 创建、移动、复制、粘贴或其他位置变更时，若产生同 start tick + key exact collision，则静默保留原先/更早进入结果的 Note 并丢弃后来对象。提交 Direct Channel Event 创建、画线、移动、复制、粘贴或其他位置变更时，若产生同 tick + 同正式事件类型 exact collision，则后来编辑对象覆盖原对象。上述归并属于一次编辑事务并随 Undo 恢复；SMF 导入后尚未触及该 exact key 的重复 Direct 数据必须继续原样显示和保存。

Pure MIDI 变体打开 Segment 时不得把全部 Direct Note/Event 转换为 `TimelineRenderItem[]`，也不得建立全量 `ItemsById` dictionary 或全 Segment interval tree。共享 Timeline 必须接受 range-query provider：piano roll 按可见 tick/pitch、Velocity/Event Lane 按可见 tick/active target 请求 source pages，并只为可见 tile 与当前 selection/edit overlay创建瞬时值记录。选择大量对象允许使用 page-local bitmap/range selection descriptor；只有显式 Properties、剪贴板或实际 edit command 需要的对象才按内部身份读取。

缩放/平移只能改变查询窗口和 tile 组合；不得触发全 Segment 枚举或重新计算全内容 fingerprint。Page checksum + page-local generation 构成 tile fingerprint 输入，编辑只更新 overlay generation 并失效与修改 tick/pitch/lane 相交的 tiles。

Pure MIDI Segment 的水平 Overview 必须显示 Direct Note 时间密度。对于 paged content，Overview 只能使用页级 minimum/maximum tick、record count 和小型编辑增量聚合到有界 device columns；不得为了生成竖线概览解码或枚举全部 Direct Note。

Logical 与 Pure MIDI Segment Editor 的水平滚动 extent 必须同时覆盖暴露的 Segment content window、Note 范围以及所有 non-Note event / Logical Parameter point 的最晚 tick；专用 Overview source 的 event tick 不得只参与画线而被排除在滚动终点之外。Paged Pure MIDI 必须使用页级范围摘要取得该终点，不得为此全量枚举事件。

### 18.2.8 三种钢琴卷帘的 Timeline 对象列表

Logical Segment、MIDI Segment 与 SubVoice 共享左侧 owner-data 对象列表，开关位于 `Lanes` 左侧、默认隐藏。显隐、宽度与滚动位置仅属于 Workspace Session，不进入 Project、Undo、Modified 或文件格式。
新建列表状态的默认宽度为 400 DIP；用户已经调整的宽度在同一会话隐藏/重开时保留，不因新默认值被覆盖，继续使用既有 240～700 DIP 范围。

列表按 local tick 和确定性同 tick 顺序合并当前 owner 的所有音符和非音符事件：Logical 包含全部参数 Lane point，MIDI 包含 Channel Event 与 Opaque SysEx/Meta，SubVoice 包含 Template Note/MIDI Event。音符每个对象一行，不拆 NoteOn/NoteOff；展示 Tick、Gate/Length、Key、Velocity，内部 Stable ID 不显示。

完整显式 Instrument Change 在 List 合并为一个特殊行，不再重复列出其 raw 成员。行选择映射到全部实际成员，原始 Lane 仍可独立编辑成员；解组后剩余 raw 恢复普通行。包装数与成员消息数是不同口径，不相加虚增事件总数。混合 Note/Event/Instrument Change 选择使用显式类型子菜单，不能将包装伪装成单 scalar Event 批量编辑。
Value 列按正式事件类型显示数值，不拼接无意义的 `Number · Value` 前缀；CC、RPN/NRPN、Polyphonic Key Pressure 的目标编号必须在 Type/Target 中保留。Bank 的 MSB/LSB 存在性与 Pitch Bend Range 的复合值不得丢失。这里只调整格式，不改变 raw 值域或现行 Program 的显示编号。

列表与图形共用 Selection。单击、Ctrl toggle、Shift 冻结 ordinal 范围及拖动范围遵循 §20.3；双击只针对被双击对象打开 `Properties...`。`Locate` 定位到对象，事件必须自动打开 Lanes 并选择对应 Lane。右键目标在打开时冻结；无效/旧修订后台结果不能恢复旧选区。Opaque payload 仅可读，不得作为普通数值点执行表达式工具。

隐藏列表不得扫描或建立索引；显示时只创建可见行，排序/选区解析在可取消的有界后台任务完成。允许使用可回收的临时 scalar 排序目录，但不得建立百万项 WPF 控件、全量行字符串或第二份常驻对象数组。列表隐藏、Tab 卸载、owner 改变及 Project 关闭必须停止旧请求；图形编辑不得依赖列表目录是否已经生成。
---
## 18.3 Event Instrument Editor 总体框架
### 18.3.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Instrument Header                                                    |
+----------------------+---------------------------------------------------+
| [B] Section         | [D] Section Toolbar                              |
|     Navigation      +---------------------------------------------------+
|                     | [E] Active Section Editor                          |
| [C] Structure Panel|                                                   |
+----------------------+---------------------------------------------------+
| [F] Preview Panel                                                       |
+--------------------------------------------------------------------------+
```
Instrument Header 的标题与摘要使用同一水平行：标题在左、摘要在右或紧随其后；保留各自既有字体层级，不以两行增加固定 Header 高度。
内部固定分区及顺序：
```text
Configurations
SubVoice
```

`Configurations` 直接承载 General、Template、Routing / Isolation、Lifecycle、Loop、Overlap 与 Instrument Initial State；不设置无实际用途的 Overview。切换到该分区或重新装载其容器时，键盘焦点必须停留在安全的分区导航目标，不得自动进入 Name 或其他文本输入框，以保证 Space、`D`、`S`、`E` 等 Workspace / Global 快捷键继续自然可用。Logical Parameters、Parameter Mappings、MIDI output mapping chains、Mapping Steps、Envelope Presets 与 Mapping Function Presets 全部由左侧 Structure Panel 管理，不设置 Parameters 或 Properties Tab。结构对象双击或右键 `Properties...` 打开固定目标模态对话框。
### 18.3.2 Header
显示：
```text
Instrument name
Color
Usage count
Validation state
Preview state
```
一个 Event Instrument 对应一个 Workspace Tab。
### 18.3.3 Structure Panel
根据当前分区显示：
```text
SubVoice
Logical Parameters
Mappings
Mapping Function Presets
Envelope Presets
Lifecycle objects
```
### 18.3.4 Preview Panel
位于底部，可折叠。
预览模式：
```text
Full Instrument
Selected SubVoice
```
预览键盘与 Segment Editor Pitch Ruler 采用统一琴键规则，不显示 MIDI Note 编号。Preview Keyboard 的 pointer 纵向位置映射为越靠下 velocity 越大、越靠上 velocity 越小；该映射只影响 Held Preview 请求，不修改 Project Note 或 Mapping 数据。
Preview Mute / Solo 只影响预览任务，不属于 Project。

当用户从 Event Instrument Workspace 切换到其他顶层 Workspace（包括另一个 Event Instrument）或关闭当前 Workspace 时，如果底部 Preview Keyboard 启动的 Held Preview 仍在 Gate-open、Buffering、Playing 或 release-tail 状态，必须立即停止该键盘预览任务。该导航清理只按 Preview Keyboard 的任务所有权执行，不得停止主时间线播放、普通 Event Instrument / SubVoice Preview、Segment Preview、Pitch Ruler Preview 或其他音频任务。
### 18.3.5 引用更新
修改 Event Instrument 后：
- 所有引用它的 Logical Track 使用最新定义；
- Segment Logical Parameter Lane 状态立即更新；
- Broken / Incompatible 数据保留；
- 不按名称自动修复；
- 相关编译和播放缓存失效。
UI 不显示或允许用户指定固定 Port / Channel。
---
## 18.4 SubVoice Event Editor
### 18.4.1 布局
```text
+--------------------------------------------------------------------------+
| [A] SubVoice Context Header                                              |
+--------------------------------------------------------------------------+
| [B] View Switch and Event Toolbar                                        |
+-------------+------------------------------------------------------------+
| [C] Pitch   | [D] Note Timeline                                         |
|     Keyboard|                                                            |
+-------------+------------------------------------------------------------+
| [E] Target Lane Tabs + Tools                                            |
| [F] Single Active Velocity / Inst. / Event Lane                          |
+--------------------------------------------------------------------------+
| [G] Timeline Overview and Horizontal Scroll                              |
+--------------------------------------------------------------------------+
```
视图：
```text
Timeline
Initial State
```
### 18.4.2 Note 与事件 Lane
- Note 使用独立 piano roll；
- CC 和 Pitch Bend 使用连续点或曲线；
- Program、Bank、RPN、NRPN 和 Pitch Bend Range 使用离散高级事件；
- 不展开 RPN / NRPN 底层 CC 序列；
- Bank/Program 使用程序级 Catalog 名称与数值回退；不得隐式扫描 SoundFont；
- Program 统一显示 0～127，与正式 MIDI 值一致。

SubVoice Timeline 与 Segment Editor 共用当前 Project 会话的 piano-roll Grid / Snap、默认 Note 长度和默认 velocity。下部编辑区同样使用 Velocity 与单个活动事件/曲线 Lane 切换，不保留多 Lane 垂直堆叠模式。

从 Velocity 切换到事件 target Tab 后，键盘焦点必须在布局更新后进入对应 Timeline Surface，不得停留在 Tab 头、目录或下拉框；因此 `D` / `S` / `E`、Space 及其他焦点敏感 Workspace 快捷键必须立即可用。Inst. 将焦点交给自身包装编辑区。
SubVoice `Add Event` 成功后必须按稳定 MIDI target 显示并激活新 Lane、打开下部事件区，并在布局完成后把焦点交回对应 Surface；不得仅依赖列表下标，也不得在绑定列表尚未刷新时查找新 Lane 并放弃导航。此后 `A` 操作该事件视图的 Snap，不是上方 piano roll 的独立 Snap。取消、失败、旧 Workspace 或已失效导航不能改变当前 Lane/选择或抢回焦点。添加空 Lane 本身不延长模板。

SubVoice Note piano roll 复用第 18.2.3～18.2.4 节的 Pitch Ruler 琴键与 C 音名规则、Draw / Select 直接编辑边界、拖动预览和第 20 章的工具互斥、指针及快捷键规则。
SubVoice 不提供 Time Range 选择：主 piano ruler、Velocity ruler 与 Event Lane ruler 均不得开始时间范围拖选，右键菜单的 Time Range 命令禁用；对象框选、列表范围选择和直接编辑不受影响。
### 18.4.3 Initial State
Event Instrument 全局和 SubVoice Initial State 提供统一一行音色选择入口及高级 raw Bank/Program override 区。选择器在 draft 中联动 Bank/Program 名称列表与三个独立 0～127 数值框，保留独立继承；OK 原子提交，Cancel/关闭零音乐变更。无 SoundFont 仍可合法编辑。

MIDI Segment 和 SubVoice 的底部基础 Tab 常驻为 `Vel.`、`Inst.`，Logical Segment 只有 `Vel.`。Inst. 采用固定 y 的时间点与有限数量边框标签；Draw 单点创建、双击/Properties 复用上述选择器，不提供沿线连续创建。使用正式关联命中；目录/排序/分页读取放到有界、可取消后台任务，隐藏/关闭释放索引且迟到结果不得复活。完整包装编辑与 List 合并服从 §8.55.4 和 §18.2.8，不能破坏既有 raw 编辑维护。

Inst. Draw 悬停以当前 Snap 后的横坐标、事件行固定纵坐标显示轻量创建预览；离开内容区或开始其他手势时隐藏。框选使用与其他 Timeline 相同的蓝色信息色与虚线，不改变已选事件的选中色。中键拖动平移共享水平时间轴，固定 y 不随垂直鼠标位移变化；捕获支持出界完成，取消或失去捕获不编辑事件。

Initial State 与 tick 0 普通事件严格分离：
```text
Initial State has no tick.
Initial State does not affect Template Length.
A user event at tick 0 may override the corresponding Initial State.
```

同一 SubVoice Section 的 `Initial State` 子页还必须提供该 SubVoice 的 Name、Root Note inherited/override 与全部现有 Initial State target 的精确编辑。添加新的 CC/RPN/NRPN target 使用显式选择器；空值表示删除该 Initial State override。提交失败恢复最后合法值。
### 18.4.4 Template 与 Root Note
Template Length 属于 Event Instrument，不是每条 SubVoice 独立长度。
可视模板末端不是 SubVoice 普通事件的创建上界。单击、Shift 单点、自由绘线、右键直线/水平线、批量创建、粘贴、复制拖动，以及已有事件 Move/Properties 等正式入口，创建或移到模板外的点必须在同一原子命令中把 Template Length 扩至至少 `tick + 1`；原有 Value Curve 点创建遵循同一边界但不转换为离散事件。所有打开的同 Definition 视图随成功提交刷新；一次 Undo 同时恢复内容与旧模板长度，Redo 恢复结果。负 tick、溢出、取消、过期 owner/revision 或资源失败不得部分发布。不要为扩模板重建已删除的可选 Mapping；Initial State 无 tick，不参与扩长。Logical/Pure MIDI Segment 的 crop 和合法编辑边界不由此改变。
Configurations 的 Template 区必须提供 `Pre-Roll Ticks` 非负整数编辑，显示范围 `0..当前 Template Length`、默认 0。Configuration 输入留空或仅含空白时，提交前静默将输入框及待提交值归为 `0`；原正式值已为 `0` 时不产生 History edit。提交只在 `0 <= value <= Template Length` 时通过一个正式原子 Definition 命令生效；非空非法输入仍拒绝并恢复打开/提交前的合法值，不进行 Clamp。Properties Dialog 同时修改 Template Length、Pre-Roll Ticks、Loop 边界与 Per-Note Instance Isolation 时，必须按最终 draft 一次验证并作为一个 History edit 提交。帮助文本须说明 Logical Note start 是 Gate anchor、模板 origin 会提前，并提示实例 origin 越过 Segment 左边界将导致编译 Error。
SubVoice 显示 Root Note 的 inherited / override 状态和 Effective Value。
Loop 区域可以只读显示，但在 Lifecycle Editor 中编辑。

SubVoice piano roll、Velocity 与 Event Lane 使用同一 local tick 变换绘制只读语义覆盖层：`[0, Pre-Roll Ticks)` 压暗；Loop Start/End 各自为贯穿 panel 的黄色 device-pixel 对齐竖线。仅最上方 piano ruler 显示 Pre-Roll/Loop 的 Tick 标签和完整 Loop 范围带；Pre-Roll 文本为紫色，Loop 文本与范围带仍为黄色，低缩放合并标签时也必须保留各自颜色。单端 Loop 只画存在的端点，不虚构另一端或合法范围。覆盖层不截获输入，配置变化不得使 Note/Event 内容瓦片失效。
### 18.4.5 Lane 生命周期
空 Lane 不持久化。
必须区分：
```text
Hide Lane
Delete All Events
```
Mapping Chain 标记与 Logical Parameter Mapping 必须使用不同名称和视觉语义，不能混为同一种“Mapping”。
---
## 18.5 Logical Parameter、Mapping 与 Function Editor
### 18.5.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Parameter Section Toolbar                                            |
+----------------------+---------------------------------------------------+
| [B] Parameter       | [C] Editor Mode Tabs                              |
|     Structure       +---------------------------------------------------+
|                     | [D] Active Parameter Editor                        |
+----------------------+---------------------------------------------------+
| [E] Mapping Target Summary                                               |
+--------------------------------------------------------------------------+
| [F] Validation and Reference Panel                                       |
+--------------------------------------------------------------------------+
```
模式：
```text
Definition
Mapping
Function
```
### 18.5.2 Logical Parameter Definition
编辑：
```text
Name
Input type
Default value
Legal range
Display range
Enum items when applicable
```
Logical Parameter 名称在单个 Event Instrument 内必填且唯一，比较时去除首尾空白并忽略大小写。
### 18.5.3 Logical Parameter Mapping
初版使用有序列表，不做自由节点图。
目标选择路径：
```text
SubVoice
-> non-Note MIDI or advanced event object
-> target field
```
初版不允许 Logical Parameter Mapping 指向 Note 参数。
多个 Logical Parameter 指向同一目标时，必须显示正式执行顺序。
Broken source、target 或 function reference 均保留，不自动重绑。
Mapping Editor 提供单值测试；该测试不启动播放、不修改 Project 内容，也不进入 Undo / Redo。

Parameter Mapping 的 Source Parameter、Target SubVoice 与 MIDI target 必须使用对象/目标选择器，不要求手填内部 ID；创建与编辑都在同一个 Properties 对话框按顺序直接展示 Source、Target SubVoice、Target Event Kind、适用 Controller/RPN/NRPN 和其余设置，不使用分步流程。完整提交是一个原子 Project edit。Mapping 顺序提供明确的 Move Up / Move Down 入口。

Mapping Chain 与 Mapping Step 的完整属性由左侧结构列表的 `Properties...` 模态对话框编辑。Chain 至少公开 enabled、用户可理解的 owner/target、最终 rounding/overflow 和 step count，但不显示 Stable ID；Step 的 Parameter、Envelope 和 Function reference 使用显示名称的对象下拉框，并只提供当前 Chain 上下文合法的 Source/Function。文本、下拉和 Boolean 提交继续使用第 17.4.4 节的 Draft + OK/Cancel 规则。
### 18.5.4 Mapping Function Expression
Mapping Function 使用由 Event Instrument Structure Panel 打开的模态编辑对话框，不创建独立 Workspace/Tab。对话框同时编辑名称与受限单行表达式；表达式控件初始为单行高度，长表达式按宽度自动折行并随内容增高，必要时由对话框外层滚动。

编辑器只补全 ABI v3 白名单中的 `value`、批准的 `context` 字段、枚举成员和 `Math` 成员；不得补全或接受语句、类型构造、任意 .NET API、名称或 Stable ID。Context 依赖由 Validate/OK 自动推导，不提供手工声明控件。对话框必须提供 `Help`、`Validate`、中性/成功/失败结果栏、`OK` 与 `Cancel`；补全、括号配对与暗色语义配色同正式 Mapping 表达式编辑体验。

名称和表达式只存在于当前对话框本地缓冲。`Validate` 不修改 Project；`OK` 必须重新使用与 Full/Incremental Compile 相同的 ABI v3 语法、白名单、限制和依赖分析完成验证，成功后将名称、表达式及推导 Context 依赖作为一次原子 Project edit 提交，形成一次 Project Undo 并标记 Modified；正式命令失败时对话框保持打开并显示错误。`Cancel`、标题栏关闭、Project 切换或退出直接丢弃本地缓冲，不显示 Apply/Discard Draft 提示。Global Save Button、`File > Save Project` 与 `Ctrl+S` 始终保存 Project，不提交对话框缓冲。
### 18.5.5 Function 诊断
模态对话框的 Validate/OK 错误只对应其本地缓冲并显示在结果栏；Project Diagnostics 始终对应已成功提交的 Project 版本。诊断定位到 Mapping Function 时，应激活所属 Event Instrument、选中目标 Function，并打开同一模态编辑对话框；不得创建 Function Tab。
---
## 18.6 Lifecycle、Loop、Envelope 与 Overlap Editor
### 18.6.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Lifecycle Toolbar                                                    |
+----------------------+---------------------------------------------------+
| [B] Lifecycle       | [C] Lifecycle Context Summary                     |
|     Navigation      +---------------------------------------------------+
|                     | [D] Active Lifecycle Editor                        |
+----------------------+---------------------------------------------------+
| [E] Lifecycle Preview Timeline                                           |
+--------------------------------------------------------------------------+
| [F] Scenario Preview and Validation                                      |
+--------------------------------------------------------------------------+
```
页面：
```text
Strategies
Loop
Envelope Presets
Overlap
```
### 18.6.2 职责
集中编辑：
```text
Short Note Strategy
Long Note Strategy
Loop
Envelope Presets
Overlap Strategy
```
本界面不重新定义 第 10 章《实例生命周期、Loop、Envelope 与重叠》 已确定的生命周期语义。
### 18.6.3 Per-Note Instance Isolation
关闭后，依赖该能力的数据：
- 保留；
- 显示为 Incompatible；
- 不可编辑；
- 不生效；
- 重新启用后恢复。
不得显示为空集合或删除数据。
### 18.6.4 Loop
初版 Loop 是固定单一设置，不是多个对象。
关闭 Loop 时保留边界数据。
### 18.6.5 Envelope
初版 Envelope Preset 编辑器必须直接编辑第 10.11 节规定的固定 ADSR-like 结构：
```text
阶段时长：Delay / Attack / Hold / Decay / Release
可编辑值：Start Value / Peak Value / Sustain Level / End Value
```
本编辑器可以显示上述固定阶段的边界，但不得允许用户新增、删除或重排任意阶段，也不得把 Envelope 扩展为任意 Ordered Points 或 Curve Segments 数据模型。
本节只规定编辑器呈现与操作入口；阶段语义、输出值域、时长单位、插值和 Gate End 后行为均以第 10.11 节为准。
Release Start 是相对生命周期参考，不是固定 Project tick。

Envelope Preset 的 Name、Delay、Attack、Hold、Decay、Release、Start、Peak、Sustain 与 End Value 在 Event Instrument `Properties` Section 中完整编辑。关闭 Per-Note Instance Isolation 时保留并显示全部值，但输入 Disabled / Incompatible；重新启用后恢复可编辑状态。
### 18.6.6 Scenario Preview
支持输入：
```text
Gate Length
Pitch
Velocity
Optional hard boundary
```
Lifecycle Preview Timeline 和 Scenario Preview 只用于解释和测试，不创建 Project 事件。
声音预览与所有其他播放任务互斥。
---
## 18.7 Conductor Track Editor
### 18.7.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Conductor Toolbar                                                    |
+----------------------+---------------------------------------------------+
| [B] Virtual Event List| [C] Timeline Header                             |
+----------------------+---------------------------------------------------+
|                      | [D] Tempo state / step graph                     |
+----------------------+---------------------------------------------------+
|                      | [E] Time Signature / Key / Marker / End lanes    |
+--------------------------------------------------------------------------+
| [F] Timeline Overview and Horizontal Scroll                              |
+--------------------------------------------------------------------------+
```
Lane：
```text
Tempo
Time Signature
Key Signature
Markers
Project End Marker
```
### 18.7.2 Tempo
初版只支持离散 Tempo 事件，不支持 Tempo Ramp 或连续 Tempo Automation。
Tempo 大于 0，允许小数，不设置 20～300 等经验型限制。
Tempo 占独立较高区域，以水平保持线及变化 tick 的竖直跳变连接，不得画斜线暗示 Ramp。可见区域左界须恢复最近前驱状态。BPM 显示范围可缩放、平移或 Fit，不是合法值限制。自由绘线、右键直线、Shift+右键 `y=k`、Shift 固定 tick 改值及 Ctrl 复制均沿用 Event Lane 交互；Snap 开启按操作格点插点，关闭逐 tick 插点。所有生成值仍按第 4 章和 MIDI 表示边界验证。

密集 Tempo 按设备列保留 first/last/min/max 包络和末值保持；可分离点保持固定 device pixel 大小。LOD、阶梯缓存和标签只是展示，不替代正式对象命中或编译。其他正式事件 lane 同样使用有界密集点聚合和不重叠标签，不把任意 opaque Meta 提升为 Conductor 类型。
### 18.7.3 Time Signature 与 Key Signature
Time Signature 修改只更新网格和音乐位置显示，不移动任何内容 tick。
Key Signature 不自动转调或修改 Note 音高。
### 18.7.4 Marker
普通 Marker 名称可空、可重复，同一 tick 可以存在多个。编辑器必须按稳定 ID 维持各 Marker 的独立身份，并使同 tick 或密集 Marker 在选择和编辑时可区分。
Arrangement ruler 以只读、不可命中的浅灰圆角标签投影普通 Marker；标签左边界落在正式 tick，小节号保留在 ruler 底部刻度附近。该投影不替代 Conductor Editor 的正式选择和编辑入口。
Project End Marker 使用贯穿 Lane 的特殊竖线：
- 不可重命名；
- 最多一个；
- 后方区域弱化但仍可编辑；
- 不等同于普通 Marker。
无显式 End Marker 时可以显示 Natural End 只读参考。
### 18.7.5 Event List
Event List 与 Timeline Selection 同步。
播放期间允许查看和导航，不允许编辑 Conductor 事件。

Event List 双击或右键 `Properties...` 打开当前单个 Conductor event 的固定目标模态编辑器。Tempo、Time Signature、Key Signature、Marker 与 Project End Marker 的 tick 和类型专属字段在 `OK` 时通过一个正式 Project command 提交；不显示 Stable ID。列表位于左侧，可调整宽度；右侧 Tempo 与其余 lane 之间可调整高度并保留两侧硬最小值。列表采用修订绑定的分页/ordinal 查询及虚拟行，不得为百万事件建立完整 ObservableCollection 或 WPF 控件。单击、Ctrl 多选、Shift 连续范围与拖动范围选择与时间线同步；`Locate` 定位对应事件。异步读取或选择完成必须校验来源修订和当前选择修订，旧请求不得覆盖新选择。宽度、滚动和显示轴仅属于工作区会话状态。

### 18.7.6 Arrangement 概览

Conductor 在 Arrangement 第一行直接按 absolute Project tick 显示事件圆点，不使用 Segment。不同类型使用稳定不同颜色，Project End Marker 保持专用竖线。极端内容必须使用固定 device-size glyph、可视 tile、按 event type/device-pixel column 聚合和局部失效；完整规则见第 24.11 节。
---
## 18.8 Event Instruments 管理栏

初版不提供独立 Library Workspace、Folder、Unfiled 或 Card Library。Arrangement 左侧 ruler header 使用 Fluent guitar 单图标 toggle 切换左侧 Event Instruments 管理栏；该栏按独立 Definition order 浏览、创建、复制、粘贴、Duplicate、删除、重命名、编辑和排序 Event Instrument Definition，并提供 `Add Logical Track Using This Instrument`。Definition 可没有任何 Usage；删除仍被 Usage 引用的 Definition 必须拒绝。Event Instrument 详细内容在对象 Editor 中编辑，完整规则见第 24 章。
---
## 18.9 Project Settings Workspace
### 18.9.1 分类
Project Settings 只呈现 Project Source Data 与只读派生信息：
```text
General
Metadata
Reset Defaults
```
不得恢复 SoundFont、Playback、MIDI Export Defaults 或 Audio Render Defaults 页面。

### 18.9.2 General 与 Metadata
TPQ 创建 Project 后只读。Project Name 可空；Project Version 是用户自由文本，与 File Format Version 严格区分。

General 只读显示 `Notes`、`Events`、`Project Work Time` 与 TPQ。Notes 是最近一次可消费全 Project canonical 中正 velocity NoteOn 数；Events 是同一冻结序列的 MIDI channel event 总数；不可消费结果二者为 0。它们不持久化、不进入 Undo / Redo。

用户 Metadata 包含 Project Name、Project Version、Author or Team、Original Work 与 Copyright。File Information 只读显示创建/修改时间、创建/最后保存软件版本与文件格式版本。

### 18.9.3 Reset 与固定 Event Scope
Reset Defaults 编辑正式 Project 级 Reset 默认值。Global Event Scope Defaults 仅为持久化兼容空 marker，不提供 UI。

### 18.9.4 提交
Project-backed 字段必须通过正式 Project command 原子提交并进入 Undo / Redo；只读派生字段不得产生命令。
## 18.10 Diagnostics Workspace
### 18.10.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Diagnostics Toolbar                                                  |
+----------------------+---------------------------------------------------+
| [B] Filter Panel   | [C] Diagnostic List                               |
+----------------------+---------------------------------------------------+
| [D] Summary Panel                                                        |
+--------------------------------------------------------------------------+
| [E] Diagnostic Message and Source Path                                  |
+--------------------------------------------------------------------------+
```
### 18.10.2 严重级别
```text
Error
Warning
Information
```
Warning 即使因用户策略阻止当前操作，也仍显示为 Warning。
### 18.10.3 类别与上下文
至少区分：
```text
Validate
Compile
Runtime
Open
Save
Export
Audio Render
Resource
```
Compile Diagnostic 必须显示 CompileContext，例如：
```text
Whole Project Compile
Playback Compile
Segment Preview
Event Instrument Preview
MIDI Export
Audio Render
Logical Track Render
```
### 18.10.4 状态
```text
Active
Resolved
Runtime History
```
过期诊断必须明确显示它属于较早的 Project 状态，不得继续决定当前 Play、Export 或 Render 是否允许开始。
Diagnostics：
- 不属于 Project；
- 不保存；
- 不进入 Undo / Redo。
### 18.10.5 内容
每条诊断应显示：
```text
Severity
Category
Message
Source Object
Source Path
Context
Current or Historical Status
```
完整消息区可显示原因、相关值、可执行的下一步和诊断代码，但不默认暴露原始异常堆栈或内部类名。

正式诊断的 `Message` 使用英文。Status Bar 的 `N Errors, N Warnings` 必须在每次后台验证或编译完成事件中，以同一最后尝试结果立即刷新；不得滞后一轮或在当前失败时仍显示前一次计数。
### 18.10.6 来源导航
Source Path 使用稳定 ID 定位并显示最新名称。
`Go to Source`：
- 激活或打开对应 Workspace；
- 选择来源对象；
- 滚动到可见位置；
- 更新来源 Workspace 自己的 Selection 和所属编辑器投影；
- 不修改 Project。
来源已删除时显示 Last Known Path，不按名称寻找替代对象。
多来源问题显示 Primary Source 和 Related Sources。
来源对象包含非法位置或 pitch 时，导航必须使用安全的显示投影并仍尽量定位该稳定 ID；不得因构建 viewport、lane 或 Selection 而使应用崩溃。

### 18.10.6.1 Event Binding 快捷入口

Event Instrument 左侧 `LOGICAL PARAMETERS` 区域提供 `Add Event Binding...`。对话框在一个页面内展示 Name、Target Kind、适用 CC/RPN/NRPN、目标 SubVoice 多选/All、Integer source range、Override/Add/Multiply 及所需 target/offset/factor range，并在提交前展示 exact-target Append/Replace 冲突。不得拆为“先建 Parameter、再建 Mapping、再编辑 Step”的强制流程。

其中 `CONTROLLER` 选择器必须与 SubVoice `Add Event` 的 `CONTROLLER` 选择器复用同一份可编辑 BASSMIDI CC 目录、顺序和显示文本；不得在快捷绑定入口另行补入该目录未收录的 CC 或使用另一套名称格式。该 UI 约束不改变领域层对既有合法 Mapping 数据的读取与编译。

默认名称使用 MIDI target 可见名称，未知 CC 回退 `CC n`；Catalog 名称只作为 Bank/Program 辅助显示。OK 只调用一个原子 Project command，失败保持窗口和 draft；Cancel 不修改 Project。成功后焦点返回 Event Instrument Workspace。
### 18.10.7 自动修复
自动修复只允许同时满足：
```text
Unique result
Deterministic result
No change to music semantics
No deletion of user data
```
不得自动：
```text
Rebind by name
Delete Broken Lane
Clamp values without an explicit Clamp rule
Change Event Instrument
Change lifecycle strategy
Reduce isolation semantics
Steal voices or remove notes
Ignore errors and continue compiling
```
### 18.10.8 聚合
同一来源、同一问题、同一上下文可以聚合次数；不同来源且需要独立修复时必须保留可导航的具体项。
### 18.10.9 音频分轨结果
Per Logical Track Audio Render 诊断必须按 Track 显示独立结果，并区分：
```text
Completed
Completed With Errors
Failed
Cancelled
Cancelled With Completed Outputs
```

## 18.11 只读洋葱皮与 All Tracks

### 18.11.1 Track / SubVoice 洋葱皮

Logical Segment、MIDI Segment 与 SubVoice 的主钢琴卷帘在水平 Zoom In 右边、同组提供 `LayerDiagonalRegular` 图标 Button；尺寸与缩放按钮一致，启用时按 Toggle 外观高亮，但单击只在原地打开主题菜单，不直接切换。菜单分组为 Enable/Disable；Show Previous Track/SubVoice、Show Next Track/SubVoice、Select Tracks/SubVoices...；Settings...。前后项是无勾选态的命令，按当前正式顺序解析、不循环，边界项禁用；执行后启用且仅显示对应邻居，不改写手选来源列表或透明度。保存并重开仍恢复 previous/next 模式，按届时正式顺序解析邻居；重排/删除后没有邻居则显示为空，不自动切回 custom。Disable 保留模式和手选列表。

Settings 仅提供透明度，不改变启用状态、显示模式或来源。独立 Select Tracks/SubVoices 弹窗始终显示保留的手选来源，支持多选、全选和清空；OK 切回 custom 模式并启用，Cancel 保持原配置。两个弹窗均不提供 Enable 控件，显式启用/禁用由菜单承担。当前目标不得作为自身来源。目标 Track 的各 Segment 共用来源 Track 配置；每个目标 SubVoice 独立配置同一 Definition 内的来源 SubVoice。

来源按当前 Arrangement / SubVoice 正式顺序从下至上叠加，当前编辑音符、选择与拖动预览始终在其上。Track 使用自身正式显示色，包括 Logical Track color override。SubVoice 无独立音乐颜色属性，允许用既有柔和调色板区分只读来源。

Segment 映射为 target local tick → Project absolute tick → source Segment local tick，仅展示来源 Segment 的暴露范围，并精确裁剪跨边界音符；不得暴露来源被 crop/content offset 隐藏的内容。SubVoice 使用共同模板 Tick。洋葱皮不参与命中、选择、吸附、编辑、Note 计数、编译和音频，底部 Lanes 不叠加。

### 18.11.2 All Tracks

Arrangement 在水平 Zoom In 右边、同组提供相同 LayerDiagonalRegular 图标按钮，直接打开独立 `All Tracks` Tab，不弹菜单。其顶部控件沿用钢琴卷帘工具栏的样式和高度。只有只读钢琴卷帘与导航控件，没有 Lanes；不得改变任何编辑工作区的选择。

Raw 叠加 Project 暴露音符。按 2026-09-08 的用户更正，Compiled 是混合只读显示：Logical Track 从成功的完整 Canonical Compiled Result 展开；Pure MIDI Track 直接复用当前源音符快照、暴露范围和颜色，不为它重新建立整曲 FIFO 显示索引。Logical 展开仍按 Port/Channel/Key FIFO 配对 NoteOn/NoteOff，颜色使用 NoteOn 的正式 source Track identity，不从 Channel/名称反推。Pure MIDI 的源 Gate 不宣称等于跨轨道共享 Channel 的最终 MIDI 流配对时长；这只是显示投影，不改变 canonical、播放或导出。

必须包含起点在可视范围之前而 Gate 延续到范围内的 Note。编译失败或音乐修订过期时保留 Logical 的最后成功展开并显著标记 `Logical stale`；不得显示为 Current。尚无成功结果时明确 Logical 不可用，但 Pure MIDI 源音符仍显示。逻辑索引准备在后台执行，可取消、重试；旧结果保持到新结果完整提交。被删除 Logical 来源的旧 canonical 音符仍属于 stale 结果，保留已知来源色；首次打开旧结果且来源元数据已不存在时可用中性外观。Pure MIDI 来源删除立即反映到混合视图，不保留旧 canonical MIDI 层。

All Tracks 在 Raw/Compiled 下均显示绝对播放指针，并遵守全局 Follow Playback：明确中键/水平概览拖动期间暂停跟随，释放后立即恢复；跟随播放期间在底部概览上禁用滚轮横向移动。时间标尺或音符内容区左键单击设置绝对播放游标，Playing/Buffering 时复用第 13 章既有 Seek 和范围限制；不做隐藏 Snap 量化、不改变选择，不启用连续 scrub。左侧钢琴尺和右键不跳转。坐标定位不查询音符，指针更新不得重建音符投影或瓦片。

### 18.11.3 缓存与持久化

来源查询、分页读取、密集列聚合和 Logical compiled FIFO 索引均在有界后台执行；WPF 线程不得为绘制全量枚举音符。来源分块局部失效，洋葱皮使用独立缓存身份，不污染已验收的普通 Note/Selection/Velocity/Event 缓存。允许每 Key 一行的只读占用缓存，垂直缩放/滚动复用；不得使其成为领域数据或编辑命中来源。Raw/Compiled 可复用未改变的 Pure MIDI 来源快照与相同内容块缓存。缓存必须有明确预算及取消、Workspace/Project 关闭后的释放时机。

配置复用 §16.7.5 的 Format 3 presentation schema 2：目标、手选来源、sourceMode、enable、opacity、默认 Raw/Compiled 随显式 Save / Save Copy 保存，不增加 Undo、不设置音乐 Modified、不引发关闭保存提示。来源顺序在保存快照中按当前正式顺序过滤、排序。删除引用在会话中 dormant，Undo 恢复同身份时重新生效；保存过滤仍悬空的引用但不破坏会话内 Undo 恢复。Duplicate Track/SubVoice 复制该目标配置，Definition 深复制重映射内部 SubVoice 引用；视图配置本身不随音乐 Undo 回退。损坏隔离沿用 §16.33，不阻止音乐加载。
---
