# 第 19 章 Project、文件、输出与任务工作流

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 New/Open/Save/Save Copy、Open MIDI as New Project、MIDI Export、Audio Render 的配置、进度、结果、确认层级和模态锁定交互。

## 19.1 New Project Dialog
### 19.1.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Dialog Header                                                        |
+----------------------+---------------------------------------------------+
| [B] Section        | [C] Active Configuration Page                     |
|     Navigation     |                                                   |
+----------------------+---------------------------------------------------+
| [D] Project Summary                                                      |
| [E] Validation and Creation Plan                                         |
+----------------------------------------------------------+---------------+
| [F] Cancel                                               | [G] Create   |
+----------------------------------------------------------+---------------+
```
分区：
```text
General
Metadata
Review
```
### 19.1.2 创建参数
主操作文案为 `Create`。窗口与保存路径区的 Browse 按钮必须为正式字体保留完整文本和可达操作空间，不能靠裁字容纳；沿用共享红色 Primary、Cancel、Enter/Escape 与 Owner 契约，不全局改变其他按钮尺寸。
创建时确定：
```text
Optional Project Name
TPQ
Metadata
Create Unsaved or Create and Save
```
默认 TPQ：
```text
192
```
合法输入范围：`1..32767`。
TPQ 创建后不可修改。
### 19.1.3 默认 Project 内容
```text
Tempo 120 at tick 0
Time Signature 4/4 at tick 0
Key Signature Unspecified
No Marker
No Project End Marker
Empty Arrangement Track order
No Logical Track
No MIDI Channel Root or Pure MIDI Track
No Segment
No default Event Instrument
```
新 Project 初始构建不进入 Undo History。
### 19.1.4 Project Name
Project Name 可空。
空名称的未保存 Project 显示：
```text
Untitled Project
```
该显示文本不是持久化名称。
### 19.1.5 SoundFont 边界
New Project 和 Open MIDI as New Project 不接收 SoundFont 参数。SoundFont 是程序级偏好，创建、保存或打开 Project 不得读取、复制、hash、嵌入或建立 SF2 引用；当前程序级 Enabled 列表只在后续播放、预览或 Audio Render Preparing 时使用。
### 19.1.6 Create Unsaved
创建后 Project 没有磁盘路径。即使用户未再编辑，关闭时仍需提示保存，因为该 Project 尚未持久化。
### 19.1.7 Create and Save
是单一事务：
```text
Validate creation input
Build Project in memory
Write first .midora package
Commit new Project to Main Window
```
失败时保留原 Project，不显示半创建 Project。
### 19.1.8 Project 切换前置流程
如果当前存在播放或预览：
```text
Stop
-> Complete cleanup
-> Resolve unsaved Project changes
-> Create new Project
```
后续被取消时不恢复播放。
创建成功后默认打开 Arrangement Workspace。
---
## 19.2 File 菜单与 Project 生命周期命令
### 19.2.1 File 菜单
```text
New Project
Open Project
Open MIDI as New Project
Recent Projects
Close Project
Save Project
Save Copy
Exit
```
初版没有传统 Save As。
### 19.2.2 通用 Project Switch Guard
用于 New、Open Project、Open MIDI as New Project、Close 和 Exit：
```text
1. Stop active playback or preview
2. Complete playback cleanup
3. Resolve unsaved Project changes
4. Perform the requested Project operation
```
未保存 Project 处理：
```text
Save Project
Close without Saving
Cancel
```
如果存在 Damaged Placeholder 导致保存被禁止，不显示注定失败的 Save 选项。
### 19.2.3 Save Project
播放期间触发 Save：
```text
Stop
-> Cleanup
-> Save applied Project state
-> Remain Stopped
```
Save 只保存已经通过正式 Project command 提交的状态；模态 Mapping Function 对话框的本地缓冲不属于 Project，因此不会被保存或自动提交。
### 19.2.4 Save Copy
Save Copy：
- 写出当前内存 Project 的副本；
- 不改变当前 Project path；
- 不改变当前 Modified 状态；
- 不改变当前 Undo History；
- 不改变当前内存修改时间和 `lastSavedWithSoftwareVersion`。
副本内容：
```text
Preserve original Project creation time
Use Save Copy completion time as copy modified time
Preserve createdWithSoftwareVersion
Write current Midora version as copy lastSavedWithSoftwareVersion
Use current in-memory accumulated project time snapshot
```
### 19.2.5 Close / Exit
无 Project 时，主窗口显示 New / Open 空状态。
主窗口关闭按钮表示 Exit Midora，并执行相同 Stop、Draft 和 Save Guard。
初版没有 autosave、crash recovery 或自动恢复。
---
## 19.3 Open Project 与 Open Progress
### 19.3.1 文件选择
使用 Windows 文件选择器。
可以尝试选择：
```text
.midora
.zip
```
实际是否为 Project 由包内 magic、manifest 和 schema 校验决定，不只依赖扩展名。
### 19.3.2 Open Progress
```text
+--------------------------------------------------------------------------+
| [A] Opening Project                                                      |
| [B] Candidate File Path                                                  |
| [C] Current Stage                                                        |
| [D] Validation Summary                                                   |
| [E] Stage Message                                                        |
| [F] Cancel Open                                                          |
+--------------------------------------------------------------------------+
```
Open Progress 是主窗口模态任务。
候选 Project 完整构建成功前：
- 当前 Project 保留在内存；
- 当前 Workspace 保持显示但不可操作；
- 不先清空主窗口；
- 不先关闭当前 Project。
取消或失败后保留原 Project，并保持 Stopped。
### 19.3.3 打开阶段
打开只负责：
```text
Read package
Validate manifest and schema
Deserialize Project source data
Build in-memory Project
Generate Open Diagnostics
Perform explicit version migration when supported
```
打开时不：
```text
Compile Mapping Functions
Compile the whole Project
Load SF2 into BASSMIDI
Create playback streams
Restore playback
Restore Mute or Solo
Restore previous application Workspace Tabs
```
成功后初始状态可以是：
```text
Compile State: Not Compiled
SoundFont State: Configured
Playback State: Stopped
```
### 19.3.4 整体结构损坏
以下情况拒绝打开：
```text
manifest.json missing or invalid
Invalid magic
Unsupported manifest schema
project.json missing or invalid
Project index bypasses manifest
Object schema newer than supported
Object type or file kind mismatch
Unrecoverable structural index inconsistency
metadata.json present but corrupted or hash-invalid
```
显示 Blocking Error，并允许查看 Open Diagnostics；不建立半加载 Project。
### 19.3.5 Metadata 缺失
`metadata.json` 缺失时：
- 使用空 Metadata 默认值；
- Project 可以打开；
- 生成 Error Diagnostic；
- Project 标记 Modified。
Metadata 存在但损坏时打开失败。
### 19.3.6 Settings 缺失或损坏
当前正式 Settings 缺失或损坏时：
- 使用当前默认值；
- 生成 Error Diagnostic；
- Project 标记 Modified。
### 19.3.7 Conductor Track 缺失或损坏
回退为：
```text
Tempo 120 at tick 0
Time Signature 4/4 at tick 0
```
同时生成 Error Diagnostic 并标记 Project Modified。
### 19.3.8 可隔离对象损坏
单个 Event Instrument 或 Logical Track 无法读取时，Project 可以打开，并创建 Damaged Placeholder。
Placeholder 保留：
```text
Stable ID
Name snapshot
Object type
Project order
Source file path
Failure information
```
Placeholder：
- 不参与编译；
- 不进入普通编辑器；
- 名称快照只读；
- 可查看诊断；
- 可由用户删除。
只要存在 Damaged Placeholder：
```text
Save Project -> Disabled
Save Copy    -> Disabled
```
### 19.3.9 未知或孤立文件
未被当前 Project 语义索引使用的未知文件：
- 被忽略；
- 产生 Information；
- 不创建 Damaged Placeholder；
- 不阻止保存；
- 下次保存时不保留。
### 19.3.10 SoundFont 问题
外部 SF2 缺失、hash 改变、不可读或加载失败不阻止 Project 源数据打开，但状态和诊断必须明确。
无可用 SF2 时仍允许编辑、编译和 MIDI Export；禁止播放、预览和 Audio Render。
### 19.3.11 迁移
支持的旧版本在打开时进行内存迁移：
- Project 标记 Modified；
- Global Notice 持续显示；
- 第一次普通 Save 进入 `Upgrade Project Format` 专用确认，显示来源绝对路径、来源 Format、目标 Format 3 与将创建/复用的永久原字节副本完整路径；
- 不支持保存回旧格式。
迁移后的 Save Copy 使用 Format 3，不改变 CurrentProjectPath、不清除当前迁移未保存状态，也不能以受保护旧来源路径作为目标。打开和用户取消升级时不得创建副本或改写来源。
---
## 19.4 Save 与 Save Copy Progress
### 19.4.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Task Title                                                           |
| [B] Current Stage                                                        |
| [C] Target Path                                                          |
| [D] Transaction Status                                                   |
| [E] Stage Message                                                        |
+--------------------------------------------------------------------------+
```
### 19.4.2 模态与取消
Save 和 Save Copy 是主窗口模态任务。
保存事务开始后初版不可取消：
```text
Escape -> No Action
Alt+F4 -> No Action
Window Close -> No Action
```
主窗口退出请求被禁止，直到事务得到成功或失败结果。
### 19.4.3 事务
界面应能表达：
```text
Serialize current Project
Build temporary package
Validate temporary package
Create or update transaction backup
Publish or replace target
Clean temporary files
```
具体底层算法由 第 16 章《.midora 文件格式与持久化》 决定。

旧格式原路径升级使用独立事务表面，必须在确认前显示冻结的 permanent original-byte backup path。确认后进入不可取消阶段；若该路径被不同内容抢占、来源 identity 改变或无法取得排他 lease，任务失败并要求用户重新确认，不得静默改名或覆盖未知文件。成功结果必须显示永久副本路径；发布失败但副本已完成时，失败详情必须同时说明旧来源仍未改变、永久副本仍有效且可在重试时复用。
### 19.4.4 Save 成功
- 当前 Project Modified 清除；
- 当前路径保持或首次建立；
- 当前内存保存信息更新；
- 已打开模态编辑对话框的本地缓冲不属于 Save 输入，状态不受 Save 影响。
### 19.4.5 Save Copy 成功
- 当前路径不变；
- 当前 Modified 状态不变；
- 当前 Undo History 不变；
- 当前内存保存信息不更新。
### 19.4.6 失败
必须显示：
```text
What failed
Target path
Original file state
Backup path and state
Temporary file path and state
Safe next actions
```
保存失败时 Project 保持 Modified。
保存成功但临时文件清理失败：
- 仍视为保存成功；
- Project 不再显示 Modified；
- 产生 Warning；
- 不错误显示为 Save Failed。
---
## 19.5 MIDI Export Configuration Dialog
### 19.5.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Dialog Header                                                        |
+----------------------+---------------------------------------------------+
| [B] Section        | [C] Active Configuration Page                     |
|     Navigation     |                                                   |
+----------------------+---------------------------------------------------+
| [D] Export Summary                                                       |
| [E] Validation and Output Plan                                           |
| [F] Default Settings                                                     |
+----------------------------------------------------------+---------------+
| [G] Cancel                                               | [H] Start    |
+----------------------------------------------------------+---------------+
```
分区：
```text
Mode
Range
Tracks
Routing
Output
Options
Review
```
### 19.5.2 模式
```text
Whole Project
Per Logical Track
Per Port
```
固定：
```text
SMF Type 1
Project TPQ
```
初版不提供 Type 0 或导出时 TPQ 修改。
### 19.5.3 Range
支持：
```text
Project Default Range
Manual Range
```
MIDI Export 允许零长度范围。
非零 startTick：
- 恢复必要非 Note 状态；
- 不补发范围前已经发生的 Note On。
### 19.5.4 Tracks
Whole Project 与 Per Port 默认选择所有有效 Logical Tracks 与 Pure MIDI Tracks；Per Logical Track 默认且只能选择 Logical Tracks。Pure MIDI Track 不得被复制进每个 Logical Track 输出文件；单独导出 Pure MIDI Track 使用 Whole Project + 显式选择。
Mute / Solo 被忽略。成品由显式 Track Selection 决定。
Damaged Definition/Usage/Track/Root Placeholder 不产生正式输出并阻止任务。无内容且无 Usage 的 Logical Track 是合法空壳但不属于有效输出目标；有内容却无有效 Usage/Definition 是结构 Error。
### 19.5.5 Routing
```text
Compact
Preserve
```
Compact 无法保证等价时导出失败，不自动切换 Preserve。
### 19.5.6 Output
输出路径是一次性参数，不保存进 Project。
整曲模式选择完整 `.mid` 目标路径；多文件模式选择完整输出目录，不自动在其下再建立模式或 Project 命名的嵌套文件夹。目录不存在时按用户所选精确路径创建。
支持 Readme，默认开启。Readme 失败时整体导出失败。
覆盖授权在任务开始前一次性完成，不逐文件循环询问。
MIDI 导出与音频渲染共用同一确定性安全文件名合法化服务。Review 必须显示合法化后的全部最终路径、内部重复目标、现有文件冲突和所需覆盖授权；源名称不得被回写。
Review 使用第 14.8.2、14.15.3、14.17.1 节的固定 MIDI 文件、Track Name 和 `README.md` 模板，不允许编码器或写入器另行命名。
### 19.5.7 Warning 策略
用户可以配置 Warning 阻止导出，但诊断严重级别仍是 Warning。
### 19.5.8 Task Defaults
Dialog 每次使用第 14.16.2 节固定初始值。修改只影响本次任务 Draft，不提供 `Save as Project Defaults`。
### 19.5.9 Start
Start Export 前执行预检查和 Review。存在阻止条件时 Start Disabled，并提供定位入口。
正常 Start 不额外弹“确定开始”。
---
## 19.6 MIDI Export Progress 与 Result
### 19.6.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Task Header                                                          |
| [B] Overall Progress                                                     |
| [C] Current Stage                                                        |
| [D] Output Item List                                                     |
| [E] Task Log and Diagnostics                                             |
| [F] Frozen Task Summary                                                  |
| [G] Cancel or Close                                                      |
+--------------------------------------------------------------------------+
```
### 19.6.2 输入冻结
正式开始后冻结：
```text
Project source snapshot
MIDI Export CompileContext
Mode
Range
Track selection
Routing
Output targets
Overwrite authorization
Readme option
Warning policy
```
任务中不读取新的 Project 编辑状态、播放 Buffer 或临时 Mute / Solo。
### 19.6.3 原子语义
多文件 MIDI Export 整体成功或整体失败。
Item 区分：
```text
Ready to Publish
Completed
Failed
Cleaned
Cleanup Failed
```
所有必要产物成功发布前，不显示最终 Completed。
### 19.6.4 Cancel
Cancel：
- 安全停止；
- 清理临时和 partial 文件；
- 不留下看似成功的 MIDI；
- 不修改 Project；
- 结果显示 Cancelled，不是 Error。
最终原子发布的短暂阶段可暂时禁止取消，并显示 `Finalizing output files`。
### 19.6.5 Failure
失败时显示：
```text
Failed stage
Per-output state
Publish state
Cleanup state
Remaining file paths
Diagnostics
```
### 19.6.6 Result
结果窗口可提供：
```text
Open Output
Open Folder
View Diagnostics
Return to Export Configuration
Close
```
`Return to Export Configuration` 返回冻结配置的可编辑副本，不自动重试，也不自动重新授权覆盖。
---
## 19.7 Audio Render Configuration Dialog
### 19.7.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Dialog Header                                                        |
+----------------------+---------------------------------------------------+
| [B] Section        | [C] Active Configuration Page                     |
|     Navigation     |                                                   |
+----------------------+---------------------------------------------------+
| [D] Render Summary                                                       |
| [E] Validation and Output Plan                                           |
| [F] Default Settings                                                     |
+----------------------------------------------------------+---------------+
| [G] Cancel                                               | [H] Start    |
+----------------------------------------------------------+---------------+
```
分区：
```text
Mode
Range
Tracks
Output
Review
```
### 19.7.2 模式
```text
Whole Mix
Per Logical Track
```
一次任务只能选择一种模式。
初版不支持按 Port 音频分轨。
### 19.7.3 输出格式与采样率
```text
RIFF/WAVE
Stereo
IEEE 32-bit floating point
Interleaved Left, Right
Little-endian
```
这些格式字段只读显示，不提供修改控件。

Sample Rate 提供常用选项：
```text
44,100
48,000
88,200
96,000
176,400
192,000 Hz
```
并允许手工输入 `8,000–192,000 Hz` 范围内的其他整数。每次打开初始为 48,000 Hz。

同一窗口提供 `Offline Maximum Sample Voices per Unit Stream` 整数输入，合法范围 `1–16,777,216`，每次打开初始为 `500`。该值与实时播放 Application Preference 分离；当前渲染任务的所有 Unit Stream 使用同一个冻结值。
### 19.7.4 Range
Audio Render 不允许零长度范围。
非零起点：
- 恢复必要非 Note 状态；
- 不补发范围前 Note On；
- 不重建范围前合成器或效果尾音。
endTick 是硬音频边界：
- 不写入边界后的普通事件；
- 不追加 Release / Tail；
- 必要 Note Off / Reset 不延长 WAV；
- 范围外输出为零。
### 19.7.5 Tracks
Whole Mix 默认选择全部有效 Logical Tracks 与 Pure MIDI Tracks；Per Logical Track 只输出被选择的 Logical Tracks，Pure MIDI 选择保持但不生成独立 WAV。
Mute / Solo 被忽略。
Whole Mix 使用单一整体 CompileContext。
Per Logical Track：
- 每条 Track 独立 CompileContext；
- 独立资源分配；
- 独立后端状态；
- 独立 WAV；
- 不要求保留整曲编译 Port / Channel 编号。
### 19.7.6 输出链
Output 页面必须明确显示：
```text
Playback Master Volume
Final Limiter
```
Limiter 不能单独 bypass。
初版不提供：
```text
Dither
Normalization
Automatic gain
Fade in or out
Post-render resampling
Automatic silence trimming
```
### 19.7.7 预检查
支持：
```text
Ready
Partially Ready
Blocked
```
不做磁盘空间预检查。
必须在 Start 前显示经公共文件名合法化和冲突检测后的全部最终 WAV 路径；不得只显示原始 Track 名称或模板。
Whole Mix 与 Per Logical Track 分别使用第 15.10 节固定的 `<ProjectStem>.wav` 与 `<NN> - <LogicalTrackDisplayName>.wav` 模板。
必须在创建任何临时输出前，按冻结的采样率、范围、最终 frame 数和 RIFF/WAVE 结构精确检查每个计划文件的 RIFF 大小上限。任一目标超过上限时，整个任务为 Blocked；不拆分、不回退 RF64、不自动降低采样率。
Per Logical Track 模式可以在开始前显示哪些 Track 当前可准备、哪些存在已知问题，但正式任务仍按独立 Track 语义执行。
### 19.7.8 Task-only Parameters
模式、范围、Track 选择、采样率、离线复音上限、输出位置与覆盖授权只属于本次任务 Draft。Dialog 每次使用第 15.20.2 节固定初始值；不提供 `Save as Project Defaults`，Cancel 不修改 Project 或 Application Preferences（最近目录除外）。
### 19.7.9 Start
点击 Start Rendering 并通过全部预检查后：
- 冻结全部输入；
- 进入 Audio Render Progress；
- 从 Preparing 开始完全锁定 Midora。
---
## 19.8 Audio Render Progress 与 Result
### 19.8.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Task Header                                                          |
| [B] Overall Progress                                                     |
| [C] Current Output and Stage                                             |
| [D] Output Item List                                                     |
| [E] Render Metrics and Task Log                                          |
| [F] Frozen Task Summary                                                  |
| [G] Cancel Rendering                                                     |
+--------------------------------------------------------------------------+
```
### 19.8.2 完全锁定
从 Preparing 开始，整个 Midora 进入 Full Application Task Lock。
运行期间唯一允许改变程序状态的用户操作是：
```text
Cancel Rendering
```
不允许：
```text
Save
Open
Exit
Compile
Play or Preview
MIDI Export
Project editing
Mute or Solo
Main Window search
Panel resize
Workspace switching
Application preference changes
```
### 19.8.3 Cancel
以下入口统一触发同一确认：
```text
Cancel Rendering button
Window Close
Alt+F4
Escape
```
确认必须说明：
- 已完成 WAV 保留；
- 当前输出临时文件删除；
- 后续 Track 不再开始；
- Cancel 不回滚已完成产物。
最终文件发布或替换的短暂阶段可以暂时禁止取消，并显示 `Finalizing...`。
### 19.8.4 Whole Mix
Whole Mix 失败则整体失败。
### 19.8.5 Per Logical Track
每条 Track 独立：
- 一条失败后继续下一条；
- 已完成 WAV 保留；
- 允许 `Completed With Errors`。
结果必须显示：
```text
Success count
Failure count
Successful outputs
Failed outputs
Per-track diagnostics
```
### 19.8.6 用户取消
取消后：
```text
Completed WAV files remain
Current temporary output is deleted
Later Tracks do not start
```
如果已经有完成产物，结果名称为：
```text
Cancelled With Completed Outputs
```
不得称为 Completed With Errors。
### 19.8.7 Cleanup Failure
Cleanup Failed 必须列出残留路径，并明确说明残留文件不是有效成品。
### 19.8.8 Result
进入最终 Result 后任务不再运行，但 Result Dialog 仍锁定主窗口，直到用户关闭。
可提供：
```text
Open Output
Open Folder
View Diagnostics
Return to Configuration
Close
```
---
## 19.9 Dialog and Confirmation Hierarchy
### 19.9.1 层级
```text
1. Inline Validation
2. Tooltip or Field Message
3. Global Notice Bar
4. Diagnostics Workspace or full Status Message
5. Read-only Properties
6. Non-destructive Modal Information
7. Confirmation Dialog
8. Blocking Error Dialog
9. Progress or Task Modal
```
优先使用最低干扰层级。
### 19.9.2 通用规则
- 标题、消息和按钮必须描述具体动作；
- 破坏性动作不得由裸 Enter 意外触发；
- 默认焦点放在 Cancel 或安全动作；
- Escape、Close 和 Alt+F4 通常等同 Cancel，但长任务服从专用规则；
- 初版禁止任意深度模态嵌套。
允许的有限嵌套：
```text
Configuration -> File Picker
Progress -> one Cancel Confirmation
```
### 19.9.3 何时确认
通常不确认：
```text
Small visible edits that are easily undoable
Ordinary Note deletion
Zoom or view changes
Normal Save command
Starting a configured Export or Render after Review
```
需要确认：
```text
Delete object containing substantial data
Delete a non-empty unreferenced Event Instrument Definition
Delete all Lane events
Replace Track Event Instrument binding
Close or switch with unsaved Project
Overwrite output files
Cancel Audio Rendering
Reset all UI preferences
```
初版不提供破坏性确认的 `Do Not Ask Again`。
### 19.9.4 删除 Event Instrument Definition
被任何 Usage 引用的 Definition 不允许删除；UI 必须列出引用 Usage/Track，用户须先改绑或删除相关 Track。删除未引用但内部含大量定义数据的 Definition 时，应显示内容摘要并以一个 Undo 原子删除。删除 Logical Track 或最后 Usage 绝不得连带删除 Definition。
### 19.9.5 Track Binding 替换
必须显示 Logical Parameter Lane 影响摘要。
### 19.9.6 任务错误
长任务错误集中显示于 Progress / Result，不连续弹多个错误框。
普通成功操作不弹 Success Dialog；只有需要查看产物或部分结果时显示 Result。
---
## 19.10 Modal Locking Matrix
### 19.10.1 锁定级别
```text
Level 0 - Normal
Level 1 - Local Interaction Lock
Level 2 - Project Edit Lock
Level 3 - Main Window Modal Lock
Level 4 - Full Application Task Lock
```
### 19.10.2 Level 0 — Normal
Stopped / Idle：允许编辑、Undo / Redo、播放、编译、保存、导出和渲染，受对象和 Project 合法性限制。
### 19.10.3 Level 1 — Local Interaction Lock
适用：
```text
Inline Rename
Field editing
Drag gesture
Marquee selection
Context menu
Dropdown
Modal Mapping Function editing
```
局部交互优先处理 Escape、文本快捷键和命令目标，不锁定整个 Project。
### 19.10.4 Level 2 — Project Edit Lock
适用：
```text
Playback Preparing
Playing
Buffering
Stopping
Preview Preparing
Preview Playing
Explicit Compile
```
允许：
```text
View
Scroll
Zoom
Navigate
Search
Inspect
Open Diagnostics
Copy
Resize view layout
Mute and Solo during playback
Stop or safe task cancellation
```
禁止：
```text
Create
Delete
Rename
Move or resize Project objects
Cut
Project Paste
Project Undo and Redo
Change Project Settings
Change Application Preferences
Start a second global task
```
### 19.10.5 Level 3 — Main Window Modal Lock
适用：
```text
New Project Dialog
MIDI Export Configuration
Audio Render Configuration
Confirmation Dialog
Blocking Error Dialog
Open Progress
MIDI Import Review / Progress
Save Progress
Save Copy Progress
MIDI Export Progress
Result Dialog
```
主窗口不接受鼠标、键盘、Context Menu、拖放或快捷键。
### 19.10.6 Level 4 — Full Application Task Lock
适用：
```text
Audio Render Preparing
Rendering
Cancelling
Finalizing
```
运行中唯一允许改变状态的操作是 Cancel Rendering，且服从确认和最终化不可取消规则。
### 19.10.7 总体矩阵
| 状态 | 查看导航 | Project 编辑 | Undo / Redo | Play / Preview | Save | Compile | MIDI Export | Audio Render | Close / Exit |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Stopped / Idle | 是 | 是 | 是 | 是 | 是 | 是 | 是 | 是 | 是 |
| Local Inline Edit | 有限 | 当前局部 | 本地 | 否 | 否 | 否 | 否 | 否 | 先结束局部编辑 |
| Playback Preparing | 只读 | 否 | 否 | Stop | 先 Stop | 否 | 先 Stop | 先 Stop | 先 Stop |
| Playing | 只读 | 否 | 否 | Stop | 先 Stop | 否 | 先 Stop | 先 Stop | 先 Stop |
| Buffering | 只读 | 否 | 否 | Stop | 先 Stop | 否 | 先 Stop | 先 Stop | 先 Stop |
| Stopping | 只读 | 否 | 否 | 否 | 否 | 否 | 否 | 否 | 否 |
| Explicit Compile | 只读 | 否 | 否 | 否 | 否 | 可安全取消 | 否 | 否 | 先取消 Compile |
| Configuration Modal | Dialog only | Dialog-local | Dialog-local | 否 | 否 | 否 | 否 | 否 | 先取消 Dialog |
| Open Progress | Task only | 否 | 否 | 否 | 否 | 否 | 否 | 否 | Cancel Open |
| Save / Save Copy | Task only | 否 | 否 | 否 | 否 | 否 | 否 | 否 | 否 |
| MIDI Export Progress | Task only | 否 | 否 | 否 | 否 | 否 | Task cancel | 否 | Task cancel |
| Audio Render Progress | Task only | 否 | 否 | 否 | 否 | 否 | 否 | Task cancel | Task cancel |
| Result Dialog | Dialog only | 否 | 否 | 否 | 否 | 否 | 否 | 否 | 先关闭 Result |
### 19.10.8 单一活动任务
同一时刻只允许一个：
```text
Main Playback
Segment Preview
Event Instrument Preview
SubVoice Preview
Explicit Compile
Open Project
Open MIDI as New Project
Save Project
Save Copy
MIDI Export
Audio Render
```
轻量 Field Validation、Object Validation、Search、Badge Refresh 和 Preference Save 不视为全局任务。
### 19.10.9 先 Stop 再继续
播放或预览期间允许触发：
```text
Save Project
Save Copy
New Project
Open Project
Open MIDI as New Project
Close Project
Exit
MIDI Export
Audio Render
```
统一流程：
```text
Request command
-> Stop current playback or preview
-> Complete cleanup
-> Continue requested flow
-> Remain Stopped
```
后续成功、失败或取消都不恢复播放。
触发另一个播放或预览任务时不自动抢占当前任务；用户必须先 Stop。
### 19.10.10 Stopping
Stopping 期间不接受或排队新命令。
### 19.10.11 Explicit Compile
Whole Project Compile 使用非模态任务呈现，主窗口可查看和导航，但 Project 编辑锁定。
允许安全取消时显示 Cancel；初版不允许编译时继续编辑 Project。
### 19.10.12 Save
事务开始后不可取消，主窗口关闭和 Alt+F4 无效。
### 19.10.13 MIDI Export
可安全取消；最终原子发布阶段短暂不可取消。
### 19.10.14 Audio Render
从 Preparing 开始完全锁定。Cancel 保留已完成 WAV，删除当前临时输出，停止后续 Track。
### 19.10.15 Playback Error
不可恢复播放错误完成清理后：
```text
Playback State -> Error
No active playback task remains
Project editing becomes available
```
用户可以修改 Project、Save、查看 Diagnostics，或重新 Play 进入 Preparing。
### 19.10.16 Reset Playback Engine
正式入口：
```text
Playback > Reset Playback Engine
```
规则：
- Stopped 和 Error 时可用；
- Preparing、Playing、Buffering 时立即终止任务并完整重置；
- 不修改 Project；
- 不进入 Undo / Redo；
- 不影响 canonical compiled result 缓存；
- 不需要确认；
- 完成后进入 Stopped；
- MIDI Export 或 Audio Render 模态期间不可用。
### 19.10.17 不排队
除“先 Stop 后继续”这一明确流程外，初版不排队命令。
---

## 19.11 Open MIDI as New Project

### 19.11.1 入口与范围

File 菜单的 `Open MIDI as New Project` 使用独立 `.mid` 文件选择器。它只接受第 23.11 节规定的 SMF Format 0/1、MIDI 1.0、TPQN 文件；不得把 `.mid` 交给 `.midora` Open Project 包读取器，也不提供 `Import MIDI into Current Project`。

### 19.11.2 事务流程

流程固定为：

```text
Common Project Switch Guard
→ parse all chunks/events into a detached bounded candidate
→ validate SMF structure and Running Status
→ demultiplex source MTrks by effective Port.Channel
→ build Conductor / Roots / Pure MIDI Tracks / Midi Segments
→ show Port Mapping / conflict review only when required
→ validate the complete candidate
→ atomically replace the active Project
```

在最后提交前，当前 Project 保持在内存中且不可操作；失败、取消、映射冲突未解决或用户关闭 Review 时保留原 Project 并保持 Stopped，不暴露 partial candidate，不产生 Undo entry。

### 19.11.3 Port Mapping Review

仅当源 Port 无法直接一一映射到 Midora 的 `0..15`、存在冲突或将超过 256 Roots 时显示 Review。Review 必须列出源 MTrk、源 Port、Channel、目标 Root/Port.Channel、Track 数和冲突原因；用户只能建立合法一对一映射或取消。不得 modulo、clamp、静默合并源 Port，也不得部分导入。

### 19.11.4 成功结果

成功后：

```text
Project TPQ = source TPQN exactly
Project Name = source file stem
Project has no .midora save path
Project is treated as an unsaved new Project
program-level SoundFont list remains unchanged
Arrangement opens as the permanent first workspace
all imported source data is one initial state, not Undo history
```

关闭或切换该 Project 时，即使用户未做额外编辑，也必须提示保存为 `.midora`。源 `.mid` 路径不成为 Project 保存路径；保存不得覆盖源 MIDI 文件。

### 19.11.5 进度、诊断与锁定

解析、候选构建、Review 和提交使用 Level 3 Main Window Modal Lock，并计入单一活动任务集合。第一遍流式扫描按已解析源字节/源文件总字节显示确定进度并同时显示已发现事件数；第一遍得到正式总事件数后，第二遍必须显示 `processed events / total events`。验证与原子采用占据同一确定进度的收尾区间，不得退回 indeterminate。结构 Error 必须包含源 MTrk index 和 byte offset。Running Status、Track 拆分、opaque event、Note 配对和 Warning 聚合细则见第 23.11、23.16 节。

导入兼容报告首次弹窗关闭后，其完整 Info/Warning 项、code、message 与 source location 必须继续由当前状态消息持有；状态栏只显示摘要，后续 `View` 必须重新显示与首次弹窗相同的全文，直到用户 Dismiss 或另一条状态消息明确取代它。
---
