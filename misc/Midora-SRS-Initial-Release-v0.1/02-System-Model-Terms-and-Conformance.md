# 第 2 章 系统模型、术语与符合性约定

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章给出整套规格的阅读约定、跨系统不变量和正式术语。各专项章节在此基础上定义完整细节。

## 2.1 系统架构概览
Midora 的核心数据流为：
```text
Project Source Data
    -> Semantic Validation
    -> Compilation
    -> Canonical Compiled Result
    -> Playback / Preview / MIDI Export / Audio Rendering
```
Project 是唯一完整音乐语义工作上下文。Conductor Track、有序 Event Instrument Definitions、内部 Event Instrument Usages / MIDI Channel Roots、混排的 Logical / Pure MIDI Arrangement Tracks 和各类 Project Settings 均属于 Project。程序级 SoundFont 列表只属于本机音频消费者设置，不属于 Project。Definition 顺序、Usage/Root membership 与 Arrangement Track 顺序彼此正交；任何消费者不得把其中一项重新解释为另一项。播放、预览、MIDI 导出与音频渲染不得各自重新解释 Project 语义。
## 2.2 核心对象关系
```text
Project
├─ Project Settings and Metadata
├─ Conductor Track
├─ Event Instrument Definitions (explicit independent order)
│  └─ Event Instrument
│     ├─ SubVoice
│     ├─ Logical Parameters / Mappings
│     ├─ Mapping Functions / Envelope Presets
│     └─ Lifecycle / Overlap / Isolation policies
├─ Event Instrument Usages (internal, non-empty, each references one Definition)
├─ MIDI Channel Roots (internal, non-empty shared Unit identities)
├─ Arrangement Track Order (explicit mixed tagged order)
│  ├─ Logical Track → optional Usage ID
│  │  └─ Logical Segment
│  │     ├─ Logical Notes
│  │     └─ Logical Parameter Lanes
│  └─ Pure MIDI Track → Root ID
│     └─ Midi Segment
│        ├─ Direct MIDI Notes
│        ├─ Direct MIDI Channel Events
│        └─ Opaque Imported Events
├─ Global Reset Defaults
└─ Global Event Scope Defaults
```
## 2.3 规范性措辞
- **必须 / 不得**：强制要求；不满足即不符合本规格。
- **应 / 不应**：除非有明确且记录在案的技术理由，否则必须遵循。
- **可以 / 允许**：被规格许可但不强制的行为。
- **初版不支持**：不得以隐藏、实验或不完整形式作为正式功能暴露。
- **由实现设计确定**：本规格不固定具体算法、数据结构或第三方 API，但实现结果仍必须满足所有外部语义和不变量。
## 2.4 用户编号与内部编码
除明确使用 MIDI raw 值的 Program / Bank 外，面向用户的顺序编号从 1 开始显示：
```text
Port 1–16
Channel 1–16
Arrangement Track 1–N
Event Instrument Definition 1–N
Pure MIDI Track 1–N
SubVoice 1–N
Program 0–127
```
MIDI 数据内部按 MIDI 1.0 标准使用 0-based 编码。UI 不得暴露 0-based Channel 或 Port 编号。2026-09-14 已确认 R27：Program 数值入口统一为 0～127，可附 Catalog 名称；Bank MSB / LSB 同样为 0～127，显示名称不改变 raw 值。
## 2.5 稳定身份
所有需要被引用、撤销、复制、诊断定位或跨保存恢复的 Project 对象必须拥有稳定 ID。名称、数组顺序、tick、显示序号或文件路径不得替代稳定身份。
稳定 ID 的内存承载类型由实现设计确定，持久化兼容表示固定见 16.13.2。稳定 ID 必须：
```text
在对象生命周期内保持不变
复制对象时生成新身份
重命名和排序不改变身份
保存和打开后保持身份
不得因删除其他对象而复用为同一身份
支持紧凑存储和大量事件场景
```
## 2.6 确定性与单一语义来源
同一 Project 内容、同一操作上下文和同一有效资源状态必须产生稳定一致的语义结果。实现不得让以下因素改变编译或正式输出：
```text
随机数
不稳定集合遍历顺序
线程竞态
对象创建历史
编辑历史
缓存历史
上一次编译或播放结果
```
缓存、增量编译、播放调度、导出编码和离线渲染均是执行策略，不得形成第二套音乐语义。

### 2.6.1 性能优先级

在所有候选实现都满足本规格正式语义、确定性、失败原子性和资源上限时，优先选择时间性能更高的实现。

```text
时间性能优先于最小内存占用
允许预计算、索引、缓存、池化和多缓冲
允许用更多但有明确上限且可回收的内存换取速度
不得用未受控内存增长换取速度
不得因性能优化改变事件、边界、顺序、诊断或输出内容
```
## 2.7 Project 内容、应用偏好与运行状态
系统必须区分：
1. **Project Content**：进入 `.midora`，影响作品、编译或默认输出语义；
2. **Application Preferences**：当前 Windows 用户本机偏好，不进入 Project，例如 Playback Master Volume、Limiter、Stop Cursor Behavior、播放输出设备、可调音频 buffer、程序级 SoundFont 列表和 UI 偏好；
3. **Project Session UI State**：当前打开会话的导航、选择、缩放和滚动状态；
4. **Transient Interaction State**：拖动预览、未提交文本和临时草稿等瞬时状态；
5. **Derived / Runtime Data**：编译结果、播放 buffer、设备实际采样率、设备实际 buffer / callback period、诊断结果、任务历史、IPC 状态和后端状态。
只有第一类属于正式 Project 源数据。
## 2.8 正式术语
| 中文名 | 英文名 | 含义 |
|---|---|---|
| 项目 | Project | Midora 的完整创作、编译、播放、输出和持久化上下文 |
| 指挥轨道 | Conductor Track | 固定存在的全局音乐事件轨道 |
| 事件乐器 | Event Instrument Definition | 可复用、独立排序、可零 Usage 的带时间维度 MIDI 事件模板 |
| 事件乐器用法 | Event Instrument Usage | 无名称、非空的共享执行身份；引用一个 Definition，并可被多条 Logical Track 共享 |
| 事件乐器实例 | Event Instrument Instance | Logical Note 触发后生成的编译期实例 |
| 事件乐器索引 | Event Instrument Definition Index | Project 内 Definition 的独立有序索引；由 Arrangement 内辅助 Browser 展示，不拥有 Track |
| 子声部 | SubVoice | Event Instrument 内具有独立事件归属和 Channel Unit 资源语义的结构 |
| 逻辑轨道 | Logical Track | Arrangement 中的高层编曲轨道；空壳状态可暂不指定 Usage，有内容时必须引用一个 Usage |
| MIDI 通道根 | MIDI Channel Root | 不占 Arrangement 行、至少有一个成员 Track 的共享 Channel Unit、状态、路由、模式与硬边界身份 |
| 纯 MIDI 轨道 | Pure MIDI Track | 直接保存 MIDI Note / Channel Event、必须属于一个 MIDI Channel Root 的编曲轨道 |
| 逻辑片段 | Logical Segment | Logical Track 时间线上的有效范围容器 |
| MIDI 片段 | Midi Segment | Pure MIDI Track 时间线上的直接 MIDI 内容有效范围容器 |
| 直接 MIDI 音符 | Direct MIDI Note | 一对可编辑 NoteOn/NoteOff 及其 start、gate、key、on/off velocity |
| Opaque 导入事件 | Opaque Imported Event | 从 SMF 保留、初版不解释 payload、可随 Track/Segment 重新导出的 SysEx 或 Meta 事件 |
| 逻辑音符 | Logical Note | 触发 Event Instrument Instance 的高层 Note |
| 逻辑参数 | Logical Parameter | Event Instrument 暴露给 Segment 的外部控制接口 |
| 逻辑参数映射 | Logical Parameter Mapping | Logical Parameter 到 SubVoice 非 Note 事件参数的映射线路 |
| 通道单元 | Channel Unit | 唯一的 Port + Channel 组合 |
| 通道组 | Channel Group | 一个实例占用的一组 Channel Unit |
| 规范编译结果 | Canonical Compiled Result | 所有正式输出消费者共享的唯一编译语义结果 |
旧称“事件音色”一律视为“事件乐器”，正式文档和 UI 应使用 Event Instrument。
## 2.9 章节优先级与冲突处理
本 SRS 各章共同构成同一规格。若出现表述冲突，应按以下原则解释：
```text
更具体的专项规则优先于宽泛概述
明确的禁止项优先于一般允许项
数据所有权章节优先决定持久化归属
编译章节优先决定音乐语义
输出章节只能组织产物，不得改写编译语义
UI 章节只能呈现和操作既有语义，不得创造第二套语义
```
发现真实冲突时必须修订 SRS，不得由实现静默选择其中一条。
