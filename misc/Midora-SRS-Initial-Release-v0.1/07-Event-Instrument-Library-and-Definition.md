# 第 7 章 Event Instrument Library 与 Event Instrument 定义

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章规定 Event Instrument Definition 本体与内部稳定 ID 索引，覆盖身份、命名、创建、复制、删除、最小合法结构、Root Note、Template Length、策略入口、描述和颜色等。Definition order、Event Instrument Usage、Logical Track 绑定和 UI 入口由第 24 章定义。

## 7.1 Event Instrument 索引与 Arrangement 归属
每个 Event Instrument 是当前 Project 的一个可复用 Definition。Project 维护唯一、有序的 `Event Instrument Definition Index`；旧称 `Event Instrument Library` 在本章仅指该内部索引和 Arrangement 内的 Definition Browser。

```text
Event Instrument Index 可为空；
Index 不可作为用户对象删除；
Index 拥有明确的 Definition 手动顺序，但不拥有 Folder；
Definition 不占用 Arrangement Track row；
Event Instrument 不直接占用 Port / Channel / Channel Unit；
只有引用其 Usage 的 Logical Track 有效触发才生成 Event Instrument Instance。
```

第 7 章中任何旧式 “Library” 表述均不得解释为可见 Project Panel、Library Workspace、文件夹分组或独立排序。
---
## 7.2 Event Instrument 定义的归属与身份
### 7.2.1 Project 内归属
Event Instrument 定义只属于当前 Project。
初版不支持：
```text
程序级全局 Event Instrument Library
跨项目引用 Event Instrument
跨项目复制粘贴 Event Instrument
单独导出 Event Instrument 文件
单独导入 Event Instrument 文件
从模板库创建 Event Instrument
```
未来可以扩展跨项目导入 / 导出、全局库或模板系统，但初版不得依赖这些能力成立。
### 7.2.2 稳定 ID
每个 Event Instrument 必须拥有内部稳定 ID。
内部引用必须基于稳定 ID。
用户可见名称不应作为内部引用依据。
这意味着：
```text
重命名 Event Instrument 不应破坏 Logical Track 绑定
复制 Event Instrument 应产生新的稳定 ID
删除 Event Instrument 后，原 ID 不应被另一个 Event Instrument 复用为同一身份
```
具体 ID 格式、生成方式和迁移策略属于实现设计实现细则。
### 7.2.3 用户可见名称
Event Instrument 名称在当前 Project 内必须唯一。
名称唯一性的目的：
```text
降低库管理混乱
便于 Logical Track 选择
便于诊断定位
便于用户维护工程
```
名称不是对象身份。
内部仍然必须使用稳定 ID 维护对象身份。
### 7.2.4 名称合法性与唯一性比较
初版 Event Instrument 名称规则：
```text
名称不能为空
名称比较时应去除首尾空白
名称唯一性比较大小写不敏感
```
因此以下名称应视为冲突：
```text
Kick
kick
 Kick
KICK
```
名称显示时是否保留用户输入的大小写由 UI 和实现层细化；但用于唯一性判断时，必须采用大小写不敏感并去除首尾空白后的结果。
---
## 7.3 新项目默认状态
新 Project 的 Arrangement Track Order、Usage/Root 集合和 Event Instrument Definition Index 均为空，不创建默认 Definition 或 Track。该状态可保存、编译和 MIDI 导出；无 SF2 时仍不能播放、预览或音频渲染。空 Index 不产生诊断。
---
## 7.4 Event Instrument 的创建
用户应能从 Arrangement Event Instruments pane、`New Logical Track with Instrument...` 或 Project 菜单创建新的 Event Instrument Definition。
初版不支持库层草稿 Event Instrument。
创建 Event Instrument 时，系统应自动生成一个最小合法 Event Instrument，后续再由用户编辑。
创建 Event Instrument 应满足：
```text
新 Event Instrument 追加到当前 Project 的 Definition order
新 Event Instrument 的 Usage count 为 0
新 Event Instrument 获得新的稳定 ID
新 Event Instrument 拥有唯一用户可见名称
新 Event Instrument 不是草稿对象
新 Event Instrument 在创建完成后可被 Logical Track 绑定
创建操作属于项目可撤销编辑行为
创建操作应使 Project 进入已修改状态
```
最小合法 Event Instrument 的内部结构由 第 7 章《Event Instrument Library 与 Event Instrument 定义》 继续细化。
本章只确认：初版不采用“可保存但不可绑定的草稿乐器”作为库层状态。
### 7.4.1 默认名称生成
新建 Event Instrument 时，系统应自动生成唯一名称。
默认命名可采用类似以下形式：
```text
Event Instrument 1
Event Instrument 2
Event Instrument 3
```
具体编号回收、跳号、国际化显示文本属于 UI 或实现层细节。
系统生成名称时必须满足本规格规定的名称唯一性规则。
---
## 7.5 Event Instrument 的重命名
用户应能重命名 Event Instrument。
重命名 Event Instrument 应满足：
```text
新名称不能为空
新名称在当前 Project 内仍必须唯一
唯一性比较大小写不敏感，并去除首尾空白
重命名不改变 Event Instrument 稳定 ID
重命名不破坏 Logical Track 绑定
重命名不改变编译语义；Mapping Function Expression ABI v3 不向表达式暴露名称
重命名属于项目可撤销编辑行为
重命名应使 Project 进入已修改状态
```
重命名后，正在引用该 Event Instrument 的 Logical Track 应仍然引用同一稳定 ID。
对于 UI 显示，系统级建议为：
```text
已绑定 Logical Track 显示当前 Event Instrument 名称
只有当 Event Instrument 被删除或引用断裂后，Logical Track 才显示最近一次绑定名称作为失效提示
```
初版不做历史名称、别名或重命名记录。
---
## 7.6 Event Instrument 的复制 / 创建副本
本规格已经确认：如果用户想做 Event Instrument 变体，应使用“创建副本”，然后在副本上修改。
初版必须支持在当前 Project 内复制 Event Instrument。
复制 Event Instrument 的系统级规则：
```text
复制产生新的 Event Instrument 定义
复制产生新的稳定 ID
复制必须是深拷贝
Logical Parameters 与 Logical Parameter Mapping 必须深拷贝，并生成新的稳定 ID
复制出的 Event Instrument 与原 Event Instrument 后续独立编辑
复制不共享会导致后续互相影响的内部可变定义对象
原有 Logical Track 继续引用原 Event Instrument
复制操作不自动改绑任何 Logical Track
复制出的名称必须在当前 Project 内唯一
复制操作属于项目可撤销编辑行为
复制操作应使 Project 进入已修改状态
```
“深拷贝”的系统级含义：
```text
复制后的 Event Instrument 是独立定义
修改副本不影响原 Event Instrument
修改原 Event Instrument 不影响副本
现有 Logical Track 的绑定不因复制而改变
现有 Event Instrument 的使用不因复制而改变
```
复制应复制哪些内部内容，如 SubVoice、事件曲线、Mapping Function、Logical Parameters、Logical Parameter Mapping、Envelope Preset、Initial State Defaults、显示颜色、库层组织信息等，由相关相关专项章节或实现层细化。
本章只确认复制的集合语义、深拷贝要求和引用语义。
---
## 7.7 Event Instrument 的删除
用户应能删除 Event Instrument。
没有 Usage 的 Event Instrument Definition 可直接删除。被一个或多个 Usage 引用时，删除必须被阻止并列出引用 Usage/Track；用户须先改绑或删除相关 Track。删除 Definition 不得级联删除 Track，删除最后一条 Track 也不得自动删除 Definition。
---
## 7.8 Logical Track 对 Event Instrument 的引用
Logical Track 通过可空 Event Instrument Usage ID 间接使用 Definition；Usage stable ID 与 Usage 的 Definition ID 共同形成正式绑定。
引用关系应满足：
```text
引用基于稳定 ID
名称变化不影响引用
多个 Logical Track 可以通过不同 Usage 独立引用同一个 Event Instrument
多个 Logical Track 也可以引用同一个 Usage 并共享运行状态
空壳 Logical Track 可以暂时没有 Usage，但有内容的 Track 必须有 Usage
替换绑定不会删除 Logical Track 内容
```
多个 Logical Track 只共享 Definition 时不共享运行状态；引用同一个 Usage 时共享运行状态和连通区间。
系统级区分：
```text
共享定义：不同 Usage 可以引用同一个 Event Instrument Definition
共享运行：同一 Usage 的多个 Logical Track 使用同一执行身份
运行隔离：不同 Usage 即使引用同一 Definition 也相互独立
```
这避免把“同一个定义”误解为“同一个 Channel Group / 同一个实例状态”。
---
## 7.9 修改 Event Instrument 定义后的引用更新
修改 Event Instrument 定义后，所有引用该 Event Instrument 的地方都会自动使用最新定义。
这包括 Event Instrument 的 Logical Parameters 与 Logical Parameter Mapping。修改 Event Instrument 的 Logical Parameters 后，所有引用该 Event Instrument 的 Logical Track / Segment 自动使用最新定义。
系统级含义：
```text
不创建每个 Logical Track 的定义副本
不创建每个 Segment 的定义快照
不做 Frozen / 冻结实例
不要求用户手动刷新引用处
不要求用户重新绑定 Logical Track
```
如果用户希望保留旧版本，应先复制 Event Instrument，再修改副本，并根据需要将 Logical Track 改绑到副本。
由于初版不做 Frozen / 冻结实例功能，以下行为不支持：
```text
某个 Logical Track 固定使用 Event Instrument 的旧版本
某个 Segment 固定使用 Event Instrument 的旧版本
某个已编译实例在项目数据中保存完整展开结果并保持不随定义变化
```
### 7.9.1 被引用状态显示
当一个 Event Instrument 被一个或多个 Logical Track 引用时，库或编辑器中应能显示：
```text
该 Event Instrument 被多少个 Logical Track 使用
具体被哪些 Logical Track 使用
```
该信息不通过频繁弹窗呈现。
初版不要求在每次编辑或保存修改时弹窗提醒“会影响所有引用处”。
理由：修改 Event Instrument 会影响所有引用处是 Midora 的核心语义；频繁弹窗会干扰正常编辑。
播放、预览、渲染或导出缓存是否因修改 Event Instrument 而失效，由 第 12 章《编译系统与 Canonical Compiled Result》、第 13 章《播放与预览》 和实现设计阶段细化。本章只确认：修改定义会影响后续编译结果。
---
## 7.10 未引用 Event Instrument 的系统行为
Event Instrument Library 可以包含未被任何 Logical Track 引用的 Event Instrument。
未引用 Event Instrument：
```text
作为项目数据保存
可被用户继续编辑、重命名、复制或删除
不产生 Event Instrument Instance
不占用 Channel Unit
不影响播放、预览、渲染或 MIDI 导出结果
```
未被任何 Logical Track 引用本身不产生诊断。
即：
```text
未引用 Event Instrument 是正常状态
不作为错误
不作为警告
不作为信息诊断
```
这避免把正常库管理行为污染为诊断噪声。
---
## 7.11 无效 Event Instrument 的系统行为
Event Instrument 可能因为以下原因处于无效状态：
```text
缺少 Event Instrument 必要内容
名称不合法或不唯一
内部事件值非法
SubVoice 结构非法
Mapping Function 缺失或命名冲突
Mapping Function Expression 无法通过 ABI v3 验证或绑定
生命周期策略不完整
Reset 配置非法
```
本章只定义库层原则：
```text
Event Instrument Library 应能定位到无效 Event Instrument
无效 Event Instrument 不应悄悄参与编译并产生不确定结果
项目可保存包含无效 Event Instrument 的未完成工程
```
初版采用以下编译影响规则：
```text
只有实际被编译使用的 Event Instrument 错误才导致编译失败
未引用 Event Instrument 的内部错误不阻止整曲编译
未引用但内部有错误的 Event Instrument 应在诊断中显示为警告
```
这里的“实际被编译使用”至少要求：
```text
存在 Logical Track 绑定该 Event Instrument
该 Logical Track 未被编译规则忽略
该 Logical Track 中存在参与当前编译范围的有效触发内容
```
具体“实际参与编译”的精确定义由 第 12 章《编译系统与 Canonical Compiled Result》 继续细化。
无效 Event Instrument 的错误分类、警告等级、定位和修复入口由 第 7 章《Event Instrument Library 与 Event Instrument 定义》、第 9 章《曲线、Logical Parameter 与映射》、第 12 章《编译系统与 Canonical Compiled Result》、第 15 章《音频文件渲染》 继续细化。
---
## 7.12 打开项目时的 Definition / Usage 损坏
打开 Project 时，ordered Definition/Usage indexes、Usage 的 Definition ID、Logical Track 的可空 Usage ID 与 global Arrangement Track index 必须一致。单个 Definition、Usage 或 Logical Track 对象文件损坏且索引仍能可信确定稳定 ID 和引用闭包时，系统可创建对应 Damaged Placeholder，保留可定位关系并禁止普通保存。

如果引用关系缺失、重复、kind 错误或互相矛盾，无法建立唯一 Project Object Graph，则 Project 打开失败。打开诊断必须列出 Definition/Usage/Track stable ID、损坏文件和受影响引用；不得按名称或显示位置自动重绑。
---
## 7.13 Event Instrument 组织与显示
Event Instrument Definition 使用独立有序 Definition Index，并由 Arrangement 内可开关的 Event Instruments pane 浏览；它不占 Arrangement Track row，不拥有 Track child list。初版不提供旧 Project Panel、Folder/Unfiled 分组、独立 Library Workspace 或程序级全局 Library。创建、复制、剪切、粘贴、Duplicate、重命名、编辑、排序、删除及 `Add Logical Track Using This Instrument` 均从该 pane 或等价通用入口执行，详见第 24 章。

### 7.13.1 Event Instrument 颜色
初版支持为 Event Instrument 设定颜色。
该颜色是 Event Instrument 的项目内显示主色，可在 Logical Track 等其他位置作为主色使用。
系统级规则：
```text
颜色属于项目内容
颜色不影响编译
颜色不影响播放、预览、渲染或 MIDI 导出语义
颜色不影响 Port / Channel / Channel Unit 分配
颜色可被 Logical Track 等 UI 位置读取为主色
修改颜色应进入撤销 / 重做
修改颜色应使 Project 进入已修改状态
```
具体颜色值格式、默认颜色、调色板、自动配色、与 Logical Track 自定义颜色的优先级，由 第 11 章《Logical Track、Segment 与编曲语义》、第 16 章《.midora 文件格式与持久化》、第 17～20 章的 UI 与交互规格 或实现设计阶段细化。
---
## 7.14 Event Instrument Library 与 SoundFont 的关系
Event Instrument Library 不管理 SF2。
Event Instrument 不绑定独立 SF2。
系统级规则：
```text
SoundFont 仅由第 6 章规定的程序级有序 Enabled SF2/SFZ 列表及目标映射管理，Project 不保存 SoundFont
所有 Event Instrument 使用任务冻结的同一程序级 SoundFont 配置进行播放、预览和音频渲染
Event Instrument 内部 Program Change / Bank Select 是 MIDI 事件语义
替换 SF2 不自动修改 Event Instrument 内容
不根据当前 SF2 校验 Event Instrument 中的 Bank / Program 是否存在
无 SF2 不影响 Event Instrument Library 编辑
无 SF2 不影响 MIDI 编译
无 SF2 不影响 MIDI 导出
```
Event Instrument Library 不直接读取 SF2；统一音色选择器可使用程序级 Instrument Catalog 中已存在的名称，不隐式 Scan Presets。
Program 显示编号规则由 第 6 章《SoundFont 与声音资源》 与后续事件编辑章节共同遵守：
```text
UI 显示 Program 0–127
MIDI 内部 Program Change value 使用 0–127
名称使用程序级 Catalog resolver；无名称时保留合法数值回退
只有显式 Scan Presets 可以读取 SF2 preset 元数据；音色选择器本身不读取 sample 或扫描文件
```
---
## 7.15 Event Instrument Library 与 Port / Channel 资源系统的关系
Event Instrument Library 本身不占用 Port / Channel / Channel Unit。
Event Instrument 定义也不因为存在于库中而占用 Channel Unit。
只有在以下条件满足时，才会进入资源分配：
```text
Logical Track 绑定了某个 Event Instrument
该 Logical Track 中存在有效 Segment 和触发内容
编译器根据该触发内容展开 Event Instrument Instance
Event Instrument Instance 根据 SubVoice 等规则需要 Channel Group
```
初版中：
```text
Event Instrument 不允许手动指定固定 Port
Event Instrument 不允许手动指定固定 Channel
SubVoice 不允许手动指定固定 Port / Channel
资源分配由编译器自动完成
```
一个 Event Instrument Definition 被多个 Track 使用不自动表示共享 Channel Group；是否共享由 Event Instrument Usage identity 决定。未启用逐音符隔离时，同 Usage 的 Track 按第 24.4 节共享 Channel Group。
---
## 7.16 Event Instrument Index 与编译系统的关系
编译器从 global Arrangement Track Order、Usage Index 和 Event Instrument Definition Index 解析：
```text
Logical Track 的 Usage 及该 Usage 唯一引用的 Definition
Event Instrument 稳定 ID
Event Instrument 用户可见名称，用于诊断定位
Event Instrument 内部定义内容，由 第 7 章《Event Instrument Library 与 Event Instrument 定义》 实现设计确定
```
空且未绑定的 Logical Track 不参与编译且不产生诊断；有内容但无 Usage、Usage 缺失/为空或 Usage 引用错误 Definition 是结构 Error。
如果被实际编译使用的 Event Instrument 无效，则编译失败。
如果没有 Usage/Track 触发参与当前编译的 Event Instrument 存在内部错误，则不阻止整曲编译，但应在诊断中显示为警告。
---
## 7.17 Event Instrument 与播放、预览、音频渲染的关系
播放、预览和音频渲染依赖编译结果。
Event Instrument 定义通过引用它的 Usage/Logical Track 编译结果间接影响播放、预览和音频渲染。
系统级规则：
```text
修改 Event Instrument 定义会影响后续播放、预览、渲染和 MIDI 导出结果
删除 Event Instrument 在仍被 Usage 引用时被阻止
没有 Usage 的 Event Instrument 不影响整曲播放、渲染和 MIDI 导出
无 SF2 状态下仍可编辑 Event Instrument，但不能播放、预览或音频渲染
```
Event Instrument 预览 / 试听能力属于播放系统和 UI 工作流的交叉内容。
要求：
```text
Event Instrument Editor / Arrangement Header 应提供 Event Instrument 预览或导航入口
具体预览行为由 第 13 章《播放与预览》 / 第 17～20 章的 UI 与交互规格 细化
预览必须仍遵守 SoundFont 可用性、编译规则、资源分配规则和 Mapping Function Expression 错误处理规则
```
具体预览触发方式、默认试听音高、试听长度、是否创建临时 Logical Track / Segment，由 第 13 章《播放与预览》 和 第 17～20 章的 UI 与交互规格 细化。
---
## 7.18 Event Instrument 与 MIDI 导出的关系
MIDI 导出不直接导出 Event Instrument 定义本身。
MIDI 导出导出的是：
```text
Event Instrument 被 Logical Track 触发后编译出的标准 MIDI 事件
```
未被使用的 Event Instrument 不应出现在 MIDI 事件流中。
是否在导出 Readme 中记录使用到的 Event Instrument 名称、版本、数量或未使用定义列表，由第 14 章《MIDI 导出》规定。
建议实现：
```text
MIDI 导出 Readme 可记录实际参与导出的 Event Instrument 名称列表。
但这不是 MIDI 事件语义的一部分。
```
---
## 7.19 Event Instrument 与撤销 / 重做、项目修改状态
Event Instrument Definition、Usage 绑定与定义顺序编辑属于项目可撤销编辑行为。
以下操作应进入全项目统一撤销 / 重做框架：
```text
创建 Event Instrument
删除 Event Instrument
重命名 Event Instrument
复制 Event Instrument
编辑 Event Instrument 定义
修改 Event Instrument 颜色
修改 Definition order
添加、删除或重排 Logical Track，改变其 Usage/Definition 绑定
批量删除 Event Instrument，如初版支持
```
以下操作应使 Project 进入已修改状态：
```text
Definition/Usage/Track 内容或引用发生变化
Event Instrument 定义发生变化
Event Instrument 名称发生变化
Event Instrument 颜色发生变化
Event Instrument 在 Definition order 中的位置发生变化
Event Instrument 与 Logical Track 绑定关系发生变化
```
以下操作不应使 Project 进入已修改状态：
```text
临时选择某个 Event Instrument
临时打开 / 关闭 Event Instruments pane
临时预览 / 试听
```
初版不保存 UI 视图状态。
因此必须区分：
```text
项目内容：Definition order、Usage membership、global Track order、定义与颜色，应保存并影响修改状态
UI 临时状态：pane 可见性、选中项和 viewport，不应保存，不影响修改状态
```
---
## 7.20 错误、警告与信息诊断
### 7.20.1 错误 / 操作失败
以下情况应作为错误或导致操作失败：
```text
Project 缺失或重复 Definition/Usage/global Track index
Usage 的 Definition 引用缺失/kind 错误，或非空 Logical Track 的 Usage 缺失/kind 错误
Event Instrument 名称为空
Event Instrument 名称与当前 Project 内其他 Event Instrument 冲突
Event Instrument 名称经过去除首尾空白后为空
被实际编译使用的 Event Instrument 定义非法
被实际编译使用的 Event Instrument 中 Mapping Function Expression 无法通过 ABI v3 验证或绑定
用户尝试跨项目导入 / 导出 Event Instrument，初版不支持
用户尝试创建程序级全局 Event Instrument Library，初版不支持
```
### 7.20.2 打开项目警告
单个 Event Instrument Definition/Usage/Logical Track 对象文件损坏、但索引与引用闭包仍可信并已创建 Damaged Placeholder 时产生打开 Warning。对象关系本身不可信时打开失败。
### 7.20.3 警告
以下情况可作为警告或后续由第 15 章《音频文件渲染》规定的诊断来源：
```text
删除未引用但内部 non-empty 的 Event Instrument Definition 的确认摘要
没有 Usage/Track 触发参与当前编译的 Event Instrument 存在内部错误
Event Instrument 内部问题可能影响后续绑定或预览
```
其中：
```text
Event Instrument 被多个 Logical Track 使用，不应默认作为警告
Event Instrument 未被任何 Logical Track 使用，不应默认作为诊断
```
被多个 Logical Track 使用的情况应在库或编辑器中显示数量和具体 Logical Track 列表，而不是作为警告或弹窗干扰。
### 7.20.4 信息
以下情况可作为信息或 UI 状态提示：
```text
Definition Index 中没有 Event Instrument
Event Instrument 是项目级定义，不是 SF2 preset
修改 Event Instrument 会影响引用它的全部 Usages/Logical Tracks
Event Instruments pane 中的 Duplicate 只复制 Definition，不复制 Track 或 Usage
Event Instrument 颜色只作为 UI 主色，不影响 MIDI 语义
```
---
## 7.21 规则、限制与失败条件
### 7.21.1 强制规则
1. 每个 Event Instrument Definition 必须在 Definition Index 中恰好出现一次，并拥有唯一稳定 ID。
2. Definition Index 拥有独立顺序和 Arrangement 内辅助 Browser，但不拥有 Folder 或 Track child order。
3. 新 Project 默认不创建 Event Instrument。
4. Event Instrument 定义只属于当前 Project；初版不做程序级全局 Library 或项目模板。
5. Event Instrument 名称必填、Project 内大小写不敏感唯一，并去除首尾空白后比较。
6. 创建 Event Instrument 时系统自动生成最小合法定义、唯一名称与 0 Usage 状态。
7. Definition Copy/Paste/Duplicate 只深拷贝 Definition 内部对象；不复制 Track/Usage，全部新对象使用新稳定 ID并重映射内部引用。
8. 被 Usage 引用的 Definition 不允许删除；不得级联删除 Track。
9. Logical Track 通过 Usage 绑定 Definition；空壳可无 Usage，有内容必须有 Usage。共享和独立规则见第 24 章。
10. 修改 Definition 后，引用它的全部 Usages/Tracks 自动使用最新定义；初版不做 Frozen 定义。
11. 0 Usage Definition 本身不产生诊断；其内部错误在未实际编译使用时为 Warning，实际使用时为 Error。
12. 单个 Event Instrument 文件损坏时使用 Damaged Definition Placeholder；引用关系无法可信确定时打开失败，不得按名称重绑。
13. 初版支持 Event Instrument 颜色；颜色可供引用该 Definition 的 Track/Segment UI 使用，但不影响编译、播放、预览、渲染、导出或资源分配。
14. Event Instrument 预览入口位于其编辑器/Arrangement 导航；具体行为由第 13、18、20、24 章定义。
15. Event Instrument 不绑定独立 SF2，不允许手动指定固定 Port/Channel，定义本身不占用 Channel Unit。
16. Event Instrument 只有在引用它的 Usage 中有有效 Track/Segment 触发参与编译时才进入资源分配。
### 7.21.2 警告情况
以下情况应产生警告或诊断入口：
```text
删除未引用且内部 non-empty 的 Event Instrument 前显示内容摘要（确认 UI，不是编译 Warning）
单个 Event Instrument Definition/Usage 对象文件损坏但已用对应 Damaged Placeholder 安全隔离
0 Usage 的 Event Instrument 存在内部错误但当前未参与编译
```
### 7.21.3 信息情况
以下情况可作为信息或 UI 状态提示：
```text
Definition Index 中没有 Event Instrument
Event Instrument 是项目级定义，不是 SF2 preset
修改 Event Instrument 会影响引用它的全部 Usages/Logical Tracks
Event Instruments pane 中的 Duplicate 只复制 Definition，不复制 Track 或 Usage
Event Instrument 颜色只作为 UI 主色，不影响 MIDI 语义
```
### 7.21.4 失败条件
以下情况应导致相应操作失败：
| 场景 | 失败结果 |
|---|---|
| Project 缺失或重复 Definition/Usage/global Track index | 打开失败或项目结构错误 |
| Usage 无 Definition、Root/Usage 为空，或非空 Logical Track 无 Usage | 打开失败或结构编辑失败 |
| 创建或重命名 Event Instrument 时名称为空 | 操作失败 |
| 创建或重命名 Event Instrument 时名称与现有名称冲突 | 操作失败 |
| 被实际编译使用的 Event Instrument 定义非法 | 编译失败 |
| 被实际编译使用的 Event Instrument 中 Mapping Function Expression 无法通过 ABI v3 验证或绑定 | 编译失败 |
| 用户尝试跨项目导入 / 导出 Event Instrument | 操作失败 |
| 用户尝试创建程序级全局 Event Instrument Library | 操作失败 |
| 删除仍被 Usage 引用的 Event Instrument | 操作失败，不修改 Project |
| 无有效 SF2 时尝试 Event Instrument 预览 | 预览失败或入口禁用 |
---
## 7.22 Event Instrument 定义本体
Event Instrument 定义是 Project 内的可复用 MIDI 事件模板。
系统级含义：
```text
Event Instrument 定义属于当前 Project
Event Instrument 定义存在于独立有序 Definition Index，并由 Arrangement 内 Event Instruments pane 查找
Event Instrument 定义不是 SF2 preset
Event Instrument 定义不是 SoundFont 资源
Event Instrument 定义不是 Logical Track
Event Instrument 定义不是 Event Instrument Instance
Event Instrument 定义不直接占用 Channel Unit
Event Instrument 定义只有被 Logical Track 有效绑定并参与编译时，才会生成 Event Instrument Instance
```
Event Instrument 定义应至少承担以下职责：
```text
描述模板内部有哪些 SubVoice
描述模板内部 MIDI 事件和高级事件的组织入口
提供 Root Note 和音高映射基础
提供 Template Length
提供 Pre-Roll Ticks
提供实例隔离策略
提供生命周期策略入口
提供重叠策略入口
提供 Initial State Defaults 入口
提供 Mapping Function 集合入口
提供 Envelope Preset 集合入口
提供诊断定位所需的定义级信息
提供备注 / 描述字段
提供定义级显示颜色
```
---
## 7.23 Event Instrument 定义的系统级组成
从系统级看，一个 Event Instrument 定义包含以下组成部分。
| 组成部分 | 是否属于 第 7 章《Event Instrument Library 与 Event Instrument 定义》 范围 | 说明 |
|---|---:|---|
| 稳定 ID | 否，已由 第 7 章《Event Instrument Library 与 Event Instrument 定义》 确认 | 本章只承接，不定义 ID 格式。 |
| 用户可见名称 | 否，已由 第 7 章《Event Instrument Library 与 Event Instrument 定义》 确认 | 本章只承接，不重新定义唯一性。 |
| 颜色 | 是 | 属于 Event Instrument 定义级显示元数据。 |
| 备注 / 描述 | 是 | 初版支持纯文本备注 / 描述。 |
| Root Note | 是 | 用于默认 Note → Event 音高映射。 |
| Template Length | 是 | 事件乐器模板自身长度。 |
| Pre-Roll Ticks | 是 | Logical Note 可听锚点相对模板 tick 0 的非负提前量；只作用于 Logical Segment 触发实例。 |
| SubVoice 集合 | 是，限系统级入口 | 具体 SubVoice 结构由 第 8 章《SubVoice 与 MIDI 事件编辑》 细化。 |
| MIDI / 高级事件集合 | 否，入口属于本章，细节属 第 9 章《曲线、Logical Parameter 与映射》 | 本章只确认 Event Instrument 可以包含这些事件。 |
| Mapping Function 集合 | 是，限归属与名称规则承接 | 具体签名和编辑方式由第 9 章《曲线、Logical Parameter 与映射》规定。 |
| Logical Parameters | 是，限定义入口和基础规则 | Event Instrument 暴露给 Logical Track / Segment 的外部参数接口集合。 |
| Logical Parameter Mapping | 是，限归属入口和基础规则 | 定义 Logical Parameter 如何映射到一个或多个 SubVoice 非 Note MIDI / 高级事件参数。 |
| Envelope Preset 集合 | 是，限归属入口 | 具体参数由 第 10 章《实例生命周期、Loop、Envelope 与重叠》 或实现设计阶段细化。 |
| Per-Note Instance Isolation | 是 | 本规格规定默认值、可编辑性和数据保留规则。 |
| 生命周期策略 | 是，限入口和默认策略 | 具体算法由第 10 章《实例生命周期、Loop、Envelope 与重叠》规定。 |
| Overlap 策略 | 是，限入口和默认策略 | 默认采用 `Reject`；用户可改为 `Warn` 或其他受支持策略，不使用 `Let Overlap` 作为默认值。 |
| Reset 策略 | 是，限入口和覆盖层级 | 具体插入顺序由 第 10 章《实例生命周期、Loop、Envelope 与重叠》 / 第 12 章《编译系统与 Canonical Compiled Result》 细化。 |
| 预览配置 | 否，原则入口可承接 | 具体预览音高、长度、行为由 第 13 章《播放与预览》 / 第 17～20 章的 UI 与交互规格 细化。 |
---
## 7.24 Event Instrument 定义、Index 与 Arrangement 的边界
`Event Instrument Definition Index` 处理：
```text
稳定 ID 查找
名称唯一性校验
Definition 显示顺序
诊断定位辅助
```
创建、删除、复制、重命名、Definition 排序、Usage 创建/删除和 Logical Track 绑定由第 24 章的 Definition Browser / Arrangement 命令处理；Index 不拥有 Folder、Track child order 或独立 Workspace。

`Event Instrument` 定义本体负责模板语义。
它处理：
```text
Root Note
Template Length
Pre-Roll Ticks
SubVoice 集合
事件内容入口
Mapping Function 集合
Envelope Preset 集合
Per-Note Instance Isolation
生命周期策略
重叠策略
Reset 策略
备注 / 描述
定义级颜色
定义级合法性
```
说明：
```text
修改 Event Instrument 定义后，所有引用处自动使用最新定义。
如果用户需要变体，应复制 Event Instrument 后修改副本。
初版不做 Frozen / 冻结实例。
```
---
## 7.25 最小合法 Event Instrument
第 7 章《Event Instrument Library 与 Event Instrument 定义》 已确认：创建 Event Instrument 时不允许草稿状态，系统应自动生成一个最小合法 Event Instrument。
要求：
```text
最小合法 Event Instrument 不自带默认 Note On / Note Off。
新建 Event Instrument 只生成必要容器和默认参数。
新建后结构合法，但可能不发声。
```
最小合法 Event Instrument 必须满足：
```text
拥有新的稳定 ID
拥有当前 Project 内唯一名称
拥有 Root Note
拥有 Template Length
拥有 Pre-Roll Ticks，默认 0
至少包含一条 SubVoice
拥有 Per-Note Instance Isolation 设置
拥有生命周期策略设置
拥有 Overlap 策略设置，默认采用 `Reject`
拥有 Initial State Defaults 入口
Mapping Function 集合可为空
Logical Parameters 集合可为空
Logical Parameter Mapping 集合可为空
Envelope Preset 集合可为空
事件内容可为空
备注 / 描述可为空
颜色必须有可显示状态，可为默认颜色或用户设置颜色
```
系统级结论：
```text
最小合法 Event Instrument 的“合法”指对象结构完整、可保存、可绑定、可复制、可重命名、可删除、可进入编辑器。
它不保证立即可发声。
它不要求包含任何默认 Note、Program、Bank、CC、Pitch Bend、RPN、NRPN 或 Pitch Bend Range 事件。
```
---
## 7.26 Root Note
Event Instrument 必须拥有 Root Note。
Root Note 用于默认 Note → Event 音高映射：
```text
noteDelta = targetNote - rootNote
```
系统级规则：
```text
Root Note 是 Event Instrument 定义的必填属性。
Root Note 使用 MIDI note number 语义，合法范围为 0–127。
默认 Root Note = MIDI note 60。
Root Note 的显示音名和八度编号属于 UI 表示规则，由实现设计确定。
修改 Root Note 不应自动重写模板内已有事件值，除非未来提供显式重映射命令。
```
说明：
```text
本章只固定 MIDI note 60。
本章不固定该音在 UI 中显示为 C3、C4 还是 C5。
八度编号显示规则由 第 17～20 章的 UI 与交互规格 或未来 UI 细化。
```
---
## 7.27 Template Length
Template Length 是 Event Instrument 模板自身长度。
要求：
```text
Template Length 是 Event Instrument 定义的必填属性。
Template Length 使用 tick 作为内部单位。
Template Length 必须大于 0。
Template Length 必须大于等于最后一个事件所在 tick。
当用户添加或移动事件导致最后事件位置超过当前 Template Length 时，系统自动扩展 Template Length 到合法范围。
```
默认值规则：
```text
如果 Event Instrument 内已有默认 Note / 事件，则 Template Length 默认取最后事件所在 tick，并仍必须大于 0。
如果没有事件，则 Template Length 默认 1 个四分音符，即当前 Project 的 TPQ ticks。
```
由于本规格规定新建 Event Instrument 不自带默认 Note 或其他显式事件，因此初版新建 Event Instrument 的实际默认 Template Length 为：
```text
当前 Project 的 TPQ ticks
```
说明：
```text
默认值不固定为 192 ticks。
因为 第 4 章《时间、Conductor Track 与全局音乐事件》 已确认 TPQ 可在创建 Project 时设置，创建后禁止修改。
所以默认 1 个四分音符应按当前 Project 的 TPQ 换算。
```

### 7.27.1 Pre-Roll Ticks

`Pre-Roll Ticks` 是 Event Instrument Definition 的持久时间锚点属性，用于让 Logical Segment 中的 Logical Note 表示模板中已提前播放若干 tick 后的逻辑 Gate 起点。

定义：

```text
O = Pre-Roll Ticks
0 <= O <= Template Length
默认 O = 0
```

它不是延迟、播放光标补偿或消费者专用设置。对于 Logical Note 的 Project absolute anchor tick `A`：

```text
Event Instrument Instance / template origin = A - O
template tick t 的 Project tick = A - O + t
Logical Gate Start = A
Logical Gate End = A + Logical Gate Length，随后服从 Segment End 等现有硬边界裁剪
```

因此 `O` 只把模板起点和实例占用起点提前，不改变 Logical Note 保存的 start、length、pitch 或 velocity，不把 `MappingContext.gateLength` 改为 `O + gateLength`，也不改变短音/长音按 Logical Gate Length 与 Template Length 比较的规则。

适用范围固定为：

```text
Logical Segment 中由 Logical Note 生成的正式 Event Instrument Instance
```

以下入口不应用该偏移，按等效 `O = 0` 处理：

```text
Event Instrument standalone Preview
SubVoice standalone Preview
Segment Editor Pitch Ruler / pitch audition held Preview
不创建 Logical Segment Instance 的其他试听入口
```

单独编辑 Template Length 时，如果新值小于保持不变的 `Pre-Roll Ticks`，编辑命令必须拒绝该修改并恢复原值；不得静默 Clamp、缩短 Pre-Roll 或改写模板事件。在 Properties Dialog 中同时编辑 Template Length、Pre-Roll Ticks、Loop 边界和 Per-Note Instance Isolation 时，必须只验证并原子提交完整的最终值组合，不得因旧字段值或固定命令顺序拒绝一个最终合法的组合。修改 Pre-Roll Ticks 必须进入 Undo / Redo、标记 Project 已修改，并影响所有引用该 Definition 的 Usage。
---
## 7.28 SubVoice 集合入口
Event Instrument 必须包含一个或多个 SubVoice。
要求：
```text
Event Instrument 至少包含一条 SubVoice。
创建 Event Instrument 时默认生成一条 SubVoice。
初版不允许删除最后一条 SubVoice。
SubVoice 数量上限推迟到 第 8 章《SubVoice 与 MIDI 事件编辑》 细化。
```
适用前提：
```text
初版每条 SubVoice 在一次 Event Instrument Instance 中占用一个 Channel Unit。
初版不允许多个 SubVoice 共享同一个 Channel Unit。
初版不做 SubVoice 自动合并。
```
系统级含义：
```text
SubVoice 是 Event Instrument 定义的必要组成入口。
SubVoice 内部事件轨道、命名、覆盖规则、数量上限、默认事件内容由 第 8 章《SubVoice 与 MIDI 事件编辑》 继续细化。
```
---
## 7.29 Event Instrument 是否允许无发声事件
要求：
```text
Event Instrument 允许没有任何 Note 或可发声事件。
这种 Event Instrument 结构上合法。
这种 Event Instrument 可保存、可编辑、可绑定、可复制、可删除。
这种 Event Instrument 可能无声。
```
允许的状态包括：
```text
有 SubVoice，但没有任何 Note On / Note Off
有控制事件，但没有 Note 事件
有 Mapping Function / Envelope Preset，但没有事件引用它们
完全没有用户显式事件
```
诊断原则：
```text
无发声事件本身不等于结构错误。
如果该 Event Instrument 被实际编译使用但不会产生任何 MIDI 输出，可作为信息或警告诊断来源。
具体诊断等级由第 15 章《音频文件渲染》规定。
```
如果 Event Instrument 内部存在非法事件值、非法映射、非法生命周期配置等问题，则按实际错误处理，不因“允许无发声事件”而放宽其他合法性要求。
---
## 7.30 无输出事件与 Channel Unit 占用
本章不固定无输出 Event Instrument 被实际编译使用时是否占用 Channel Unit。
规则：
```text
如果 Event Instrument 无任何输出事件但被实际编译使用，是否占用 Channel Unit，由编译系统优化决定。
该问题推迟到 第 12 章《编译系统与 Canonical Compiled Result》 编译系统细化。
```
本章只确认：
```text
无输出 Event Instrument 可以是结构合法定义。
无输出 Event Instrument 的诊断性质与资源占用口径不得在 第 7 章《Event Instrument Library 与 Event Instrument 定义》 定死。
```
原因：
```text
这涉及实例展开、无输出事件优化、资源分配、预览行为和诊断定位。
属于 第 12 章《编译系统与 Canonical Compiled Result》 编译系统与 第 15 章《音频文件渲染》 诊断系统的交叉问题。
```
---
## 7.31 Program / Bank / Pitch Bend Range / CC 默认内容
Event Instrument 可以包含 Program Change、Bank Select、Pitch Bend Range、CC、RPN、NRPN 等事件。
边界：
```text
Program Change / Bank Select 是 MIDI 事件语义，不属于程序级 SoundFont 列表配置。
Event Instrument 不绑定独立 SF2。
替换 SF2 不自动修改已有 Program / Bank 事件。
初版不根据当前 SF2 校验 Bank / Program 是否存在。
初版只显示 Program 编号。
```
要求：
```text
最小合法 Event Instrument 不自动生成 Program / Bank / CC / Pitch Bend Range / Pitch Bend 等显式事件。
最小合法 Event Instrument 不自动生成 Note On / Note Off。
这些事件应由用户主动添加，或由 Reset / 初始化规则在编译阶段根据实际使用范围处理。
```
理由：
```text
自动生成太多显式事件会扩大“使用过的事件”范围，从而影响 Reset、Channel-Wide 冲突、资源共享和诊断。
保持最小模板轻量，更符合“用户显式事件决定使用范围”的 Reset 原则。
```
---
## 7.32 Per-Note Instance Isolation
Event Instrument 必须拥有 `Per-Note Instance Isolation` 设置。
系统级语义：
```text
开启时：每个逻辑音符生成独立 Event Instrument Instance，并独占一组 Channel Group。
关闭时：同一 Event Instrument Usage 活动连通区间内、可跨多个成员 Logical Track 的重叠音符可以共用一组 Channel Group。
```
要求：
```text
Per-Note Instance Isolation 是 Event Instrument 级设置。
初版允许用户编辑该设置。
默认值为关闭。
用户可以开启。
```
关闭 Per-Note Instance Isolation 时，与每音符独立状态不兼容的功能不得被编辑。
不兼容功能包括但不限于：
```text
Note → Event 映射
事件循环段
每音符独立 Envelope
每音符独立 Loop Phase
每音符独立 Pitch Bend
每音符独立 CC
每音符独立 RPN / NRPN
任何依赖每个音符拥有独立 Channel-Wide 状态的功能
```
### 7.32.1 关闭实例隔离时的不兼容数据保留规则
本规格规定一项重要数据保留规则：
```text
如果某些不兼容数据在 Per-Note Instance Isolation 关闭前已经存在，系统不得删除这些数据。
关闭 Per-Note Instance Isolation 后，这些不兼容数据应被保留在 Event Instrument 定义中。
未来重新开启 Per-Note Instance Isolation 时，这些数据应恢复可编辑和可使用状态。
```
系统级含义：
```text
关闭实例隔离是语义开关，不是数据删除命令。
关闭实例隔离不得破坏用户已经创建的映射、Loop、Envelope 或其他依赖实例隔离的数据。
关闭实例隔离时，不兼容数据应进入不可编辑 / 暂不生效 / 受限制状态，具体呈现由 第 9 章《曲线、Logical Parameter 与映射》、第 10 章《实例生命周期、Loop、Envelope 与重叠》、第 15 章《音频文件渲染》、第 17～20 章的 UI 与交互规格 细化。
```
本章不定义：
```text
不可编辑控件的具体 UI 样式
不兼容数据是否在编译诊断中显示为信息、警告或错误
关闭实例隔离时已存在不兼容数据是否允许继续编译
不兼容数据在导出 / 预览时是否完全忽略或导致失败
```
这些问题由 第 9 章《曲线、Logical Parameter 与映射》、第 10 章《实例生命周期、Loop、Envelope 与重叠》、第 12 章《编译系统与 Canonical Compiled Result》、第 15 章《音频文件渲染》 和 第 17～20 章的 UI 与交互规格 共同细化。
但底线要求：
```text
不得因关闭 Per-Note Instance Isolation 自动丢弃既有不兼容数据。
```
---
## 7.33 Overlap 策略入口
Event Instrument 定义应包含同一 Event Instrument Usage 内实例重叠时的策略入口；Usage 可以拥有一个或多个 Logical Track 成员。
本规格已保留策略：
```text
Cut Previous
Let Overlap
Merge
Retrigger Same Channel
Reject
Warn
```
边界：
```text
Midora 不支持 Voice Steal。
Let Overlap 需要 Per-Note Instance Isolation 开启。
Merge 不在本规格中固定具体实现，初版可考虑不开放 UI。
Cut Previous 同一 tick 多个 Note On 会导致编译错误。
```
Per-Note Instance Isolation 默认关闭，允许用户开启；默认 Overlap 策略不得使用 Let Overlap。
因此，初版默认 Overlap 策略为：
```text
Reject
```
系统级含义：
```text
新建 Event Instrument 默认不采用 Let Overlap。
默认状态下，不自动为重叠实例分配新的独立 Channel Group。
默认状态下，不自动裁剪旧实例。
默认状态下，不自动合并实例。
默认状态下，不自动使用 Retrigger Same Channel。
当同一 Event Instrument Usage 内出现需要 Overlap 策略处理的重叠时，默认按 Reject 策略处理。
```
采用 `Reject` 作为默认值的理由：
```text
它与 Per-Note Instance Isolation 默认关闭兼容。
它不破坏 Let Overlap 必须依赖实例隔离开启的本规格规则。
它比 Cut Previous 更保守，不会在默认状态下自动裁剪用户已有内容。
它比 Retrigger Same Channel 和 Merge 更少依赖尚未细化的后续算法。
```
说明：
```text
用户仍可在 Event Instrument 中将 Overlap 策略改为其他受支持策略。
选择 Let Overlap 时，必须同时满足 Per-Note Instance Isolation 开启等本规格其他章节适用条件。
`Reject` 与 `Warn` 是两个独立策略；精确诊断与成功判定见 10.17.8 和 12.19.8。
```
---
## 7.34 生命周期策略入口
Event Instrument 定义应包含生命周期策略入口。
本章只确认这些策略属于 Event Instrument 定义，不展开算法。
系统级入口包括：
```text
Gate Length 小于 Template Length 时的策略
Gate Length 大于 Template Length 时的策略
Template Length 与 Loop / Envelope 的关系入口
Rendered Instance Length 的计算输入入口
```
本规格已保留的短音策略：
```text
Cut At Note Off
One-Shot / Ignore Note Off
Note Off With Tail Events
```
本规格已保留的长音策略：
```text
使用循环段 / 包络
Hold Last State Until Note Off
End At Template Length
```
要求：
```text
Event Instrument 必须拥有生命周期策略设置。
创建最小合法 Event Instrument 时系统提供默认生命周期策略。
生命周期策略的具体状态机、事件排序和 Reset 行为由 第 10 章《实例生命周期、Loop、Envelope 与重叠》 / 第 12 章《编译系统与 Canonical Compiled Result》 细化。
```
默认短音策略：
```text
Gate Length 小于 Template Length 时，默认使用 Cut At Note Off。
```
默认长音策略：
```text
Gate Length 大于 Template Length 时，如果配置 Loop / Envelope，则使用循环 / 包络规则；否则 Hold Last State Until Note Off。
```
Loop 并非长音专属开关：除短音 One-Shot 外，到 Loop End 且 Gate 尚未结束即可循环，包含短音和等长音；具体优先级及 Pre-Roll 模板时钟见 §10.9.5。
说明：
```text
Cut At Note Off 更接近普通键盘 / 旋律乐器直觉。
One-Shot / Ignore Note Off 更适合鼓和 FX，但不作为通用默认。
```
---
## 7.35 Initial State 与 Reset 目标闭包
Event Instrument 定义应包含 Initial State Defaults 入口；Project Reset Defaults 不属于 Event Instrument 定义。
适用前提：
```text
编译器从 Event Instrument / SubVoice 的 Initial State、显式事件、Mapping 和生命周期输出确定实际状态目标闭包。
Segment lane 首次激活或非重叠复用激活时，应先按 Project Reset Defaults 为该闭包建立确定基线。
随后才应用合并 Initial State、用户起点状态和 NoteOn。
普通 Gate/Release/Tail/instance/allocation-group 结束只执行必要的精确 NoteOff，不执行通用目标 Reset。
Segment End 和消费者范围结束等硬边界按同一目标闭包执行最终 Reset；需要时还执行 CC120。
```
要求：
```text
Event Instrument 与 SubVoice 均不得覆盖 Project Reset Defaults。
Event Instrument 与 SubVoice 可以定义各自的 Initial State Defaults。
Reset 值只来自 Project Reset Defaults 与软件内置默认值。
目标闭包、lane 激活和硬边界顺序由第 8、10、12 章细化。
```
本章不定义：
```text
Reset 插入顺序
Reset 与 Note Off 的同 tick 排序
Reset 与 Segment 裁剪的精确关系
Reset 与 Project End Marker 的关系
```
---
## 7.36 Mapping Function 集合入口
Event Instrument 定义应包含 Mapping Function 集合入口。
适用前提：
```text
Mapping Function 名称必填。
Mapping Function 名称在单个 Event Instrument 内不可重复。
初版新建项固定使用受限 Mapping Function Expression ABI v3。
表达式只能使用第 9.6 节的单行数值/枚举白名单；不允许自由 C#、语句、循环、对象创建或任意 API。
Context 为只读，且表达式只能访问 ABI v3 明确批准的数值/枚举字段。
单行表达式文本、`abiVersion = 3` 和经复核的 Context 依赖保存在 `.midora` 项目文件中。
正式路径只绑定 `System.Linq.Expressions` 委托，不 Emit 或加载 Project 源码程序集。
被实际使用的表达式无法通过 ABI v3 验证/绑定，或求值产生 NaN / Infinity / 非法结果时，当前编译 / 播放 / 渲染 / 导出流程失败。
自由 C# ABI v1/v2 只可识别并明确拒绝，绝不执行。
```
本章只确认归属：
```text
Mapping Function 属于 Event Instrument 定义内部资源。
Mapping Function 可被该 Event Instrument 内的事件值映射引用。
Mapping Function 不属于 Project 全局资源。
Mapping Function 不跨 Event Instrument 共享。
复制 Event Instrument 时，Mapping Function 应作为深拷贝内容复制。
```
具体内容推迟到 第 9 章《曲线、Logical Parameter 与映射》：
```text
函数签名
Context 结构
返回值类型
参数范围
编译缓存
错误定位
编辑器行为
```
---
## 7.37 Logical Parameters 与 Logical Parameter Mapping 集合入口
Event Instrument 定义应包含 Logical Parameters 集合入口与 Logical Parameter Mapping 集合入口。
Logical Parameter 是 Event Instrument 暴露给 Logical Track / Segment 的外部控制接口。
系统级规则：
```text
Event Instrument 可以定义多个 Logical Parameter。
Logical Parameters 直接属于 Event Instrument，不额外设置 Group 层级。
Logical Track / Segment 只能编辑当前绑定 Event Instrument 暴露的 Logical Parameters。
Logical Parameter 不等同于裸 MIDI 事件。
Logical Parameter 通过 Logical Parameter Mapping 间接影响一个或多个 SubVoice 的非 Note MIDI / 高级事件参数。
```
Logical Parameter 定义规则：
```text
Logical Parameter 必须拥有稳定 ID。
Logical Parameter 名称在单个 Event Instrument 内唯一。
名称唯一性比较大小写不敏感，并去除首尾空白。
Logical Parameter 支持 Integer、Double、Enum 输入类型。
Logical Parameter 必须拥有 defaultValue。
Logical Parameter 可定义 legal range 与 display range。
Enum 类型应定义可用枚举项。
```
Logical Parameter Mapping 规则：
```text
Logical Parameter Mapping 属于 Event Instrument 定义内部资源。
Logical Parameter Mapping 可以指向一个或多个 SubVoice 的非 Note MIDI / 高级事件参数。
Logical Parameter Mapping 不直接指向 Note number 或 Note velocity 这类 Note 对象属性，除非相关专项章节明确扩展。
多个 Logical Parameter 可以映射到同一目标。
多个 Mapping 作用于同一目标时，按显式 Mapping 顺序依次计算。
复制 Event Instrument 时，Logical Parameters 与 Logical Parameter Mapping 必须深拷贝，并生成新的稳定 ID。
```
本章只确认 Logical Parameters 与 Logical Parameter Mapping 的归属、必要性和基础合法性。以下内容由第 9 章《曲线、Logical Parameter 与映射》规定：
```text
Logical Parameter Lane / Point / Curve
Logical Parameter 稀疏状态继承
Logical Parameter Mapping 执行语义
Logical Parameter 与 SubVoice 原始事件值的合成方式
Mapping Function Expression 的 Logical Parameter Context
越界、除零、重绑定转换和诊断细则
```
## 7.38 Envelope Preset 集合入口
Event Instrument 定义应包含 Envelope Preset 集合入口。
适用前提：
```text
Envelope Preset 名称可选且允许重复。
Envelope Preset 作用范围在 Event Instrument 内部。
事件循环段和包络预设要求 Per-Note Instance Isolation 开启。
```
本章只确认归属：
```text
Envelope Preset 属于 Event Instrument 定义内部资源。
Envelope Preset 可被该 Event Instrument 内的事件曲线或事件值映射引用。
Envelope Preset 不属于 Project 全局资源。
Envelope Preset 不跨 Event Instrument 共享。
复制 Event Instrument 时，Envelope Preset 应作为深拷贝内容复制。
```
具体内容推迟到 第 10 章《实例生命周期、Loop、Envelope 与重叠》 或实现设计阶段：
```text
Delay / Attack / Hold / Decay / Release 参数
Loop Start / Loop End
时间缩放方式
Note Off 后行为
循环行为
Envelope 与 Template Length / Gate Length 的关系
```
---
## 7.39 Event Instrument 级设置与 SubVoice 级设置边界
本规格规定：多 SubVoice 默认同步 Event Instrument 全局设置，但允许 SubVoice 独立设置。
本章采用以下系统级分层：
```text
Event Instrument 级设置提供默认值和全局语义入口。
SubVoice 可在相关专项章节中对部分设置提供覆盖。
SubVoice 覆盖只影响该 SubVoice，不改变 Event Instrument 级默认值。
```
适合 Event Instrument 级默认入口的内容：
```text
Root Note
默认音高偏移规则
Per-Note Instance Isolation
生命周期策略
Overlap 策略
Initial State Defaults 入口
Template Length
Mapping Function 集合
Envelope Preset 集合
备注 / 描述
颜色
```
可能允许 SubVoice 覆盖的内容：
```text
Root Note 或音高偏移规则
特定事件值映射方式
特定初始状态或事件内容
```
具体哪些可覆盖，由 第 8 章《SubVoice 与 MIDI 事件编辑》、第 9 章《曲线、Logical Parameter 与映射》、第 10 章《实例生命周期、Loop、Envelope 与重叠》 继续细化。
---
## 7.40 备注 / 描述字段
初版支持 Event Instrument 备注 / 描述字段。
系统级规则：
```text
备注 / 描述为纯文本。
备注 / 描述属于 Event Instrument 定义的一部分。
备注 / 描述保存进 Project。
备注 / 描述进入撤销 / 重做。
修改备注 / 描述会使 Project 进入已修改状态。
备注 / 描述不影响编译、播放、预览、渲染或 MIDI 导出语义。
复制 Event Instrument 时，备注 / 描述随定义深拷贝。
```
备注 / 描述可用于记录：
```text
设计意图
Program / Bank 编号含义
映射函数说明
Loop / Envelope 注意事项
Reset 注意事项
与当前 SF2 的人工约定
```
本章不定义：
```text
最大长度
是否支持 Markdown
是否支持富文本
是否参与搜索
是否导出到 Readme
```
这些由 第 16 章《.midora 文件格式与持久化》、第 15 章《音频文件渲染》、第 17～20 章的 UI 与交互规格 或实现设计阶段细化。
---
## 7.41 颜色字段
初版支持为 Event Instrument 设置颜色。
颜色语义归属：
```text
颜色属于 Event Instrument 定义级显示元数据。
颜色保存进 Project。
颜色随 Event Instrument 复制而复制。
颜色可在 Logical Track 等其他位置作为主色使用。
颜色不影响编译、播放、预览、渲染或 MIDI 导出语义。
颜色不影响 Port / Channel / Channel Unit 分配。
修改颜色进入撤销 / 重做。
修改颜色使 Project 进入已修改状态。
```
说明：
```text
颜色虽然属于 Event Instrument 定义级显示元数据，但它不是音乐语义。
颜色不得被编译器当作影响 MIDI 输出的输入。
```
具体颜色值格式、默认颜色、调色板、自动配色、与 Logical Track 自定义颜色的优先级，由 第 11 章《Logical Track、Segment 与编曲语义》、第 16 章《.midora 文件格式与持久化》、第 17～20 章的 UI 与交互规格 或实现设计阶段细化。
---
## 7.42 Event Instrument 的有效性与无效状态
Event Instrument 可能处于以下状态：
```text
结构合法且可编译
结构合法但当前无发声内容
结构合法但包含未使用的内部资源
结构合法但包含内部诊断问题
结构非法
被实际编译使用时非法
未被引用但内部非法
```
要求：
```text
Project 可以保存包含无效 Event Instrument 的未完成工程。
Event Instrument Library 应能定位无效 Event Instrument。
未引用但内部有错误的 Event Instrument 不阻止整曲编译，但应在诊断中显示为警告。
被实际编译使用的 Event Instrument 错误导致编译失败。
Event Instrument 预览应按“该 Event Instrument 被实际使用”处理；如果该定义存在会阻止预览的错误，则预览失败。
```
结构非法来源包括但不限于：
```text
缺少必填 Root Note
Root Note 非法
缺少 Template Length
Template Length 小于等于 0
Template Length 小于最后事件时间
缺少必要 SubVoice
Mapping Function 名称冲突
Mapping Function Expression 无法通过 ABI v3 验证或绑定
Logical Parameter 名称冲突或缺失
Logical Parameter 类型、defaultValue 或合法范围非法
Logical Parameter Mapping 引用断裂、目标非法或顺序非法
事件值超出 MIDI / Midora 合法范围
Per-Note Instance Isolation 关闭却使用必须依赖实例隔离的功能，具体是否错误由诊断规则细化
生命周期策略组合非法
Reset 配置非法
```
具体诊断等级、错误码和定位方式由第 15 章《音频文件渲染》规定。
---
## 7.43 保存、撤销 / 重做与项目修改状态
Event Instrument 定义编辑属于项目可撤销编辑行为。
以下行为应进入全项目统一撤销 / 重做框架：
```text
修改 Root Note
修改 Template Length
修改 Pre-Roll Ticks
新增、删除、编辑 SubVoice
新增、删除、编辑事件内容
新增、删除、编辑 Mapping Function
新增、删除、编辑 Envelope Preset
修改 Per-Note Instance Isolation
修改生命周期策略
修改 Overlap 策略
修改 Reset 策略
修改备注 / 描述字段
修改颜色
```
以下行为应使 Project 进入已修改状态：
```text
Event Instrument 定义内容发生变化
任何会影响后续编译、播放、预览、渲染或 MIDI 导出的定义内容发生变化
任何被保存进 Project 的定义级显示元数据发生变化
```
以下行为不应使 Project 进入已修改状态：
```text
临时选择某个事件
临时缩放 Event Instrument 编辑器视图
临时播放预览
临时搜索或筛选
临时展开 / 折叠编辑器面板，如初版不保存该状态
```
---
## 7.44 与 SoundFont 的关系
Event Instrument 不绑定独立 SF2。
系统级规则：
```text
SoundFont 仅属于第 6 章规定的程序级有序 Enabled SF2/SFZ 列表及目标映射，Project 不保存 SoundFont。
所有 Event Instrument 在播放、预览和音频渲染时使用任务冻结的同一程序级 SoundFont 配置。
Event Instrument 内部 Program Change / Bank Select 是 MIDI 事件语义。
替换 SF2 不自动修改 Event Instrument 定义。
Event Instrument 定义不根据当前 SF2 校验 Program / Bank 是否存在。
无 SF2 不影响 Event Instrument 定义编辑。
无 SF2 不影响 MIDI 编译和 MIDI 导出。
无 SF2 会使 Event Instrument 预览不可用。
```
---
## 7.45 与 Port / Channel 资源系统的关系
Event Instrument 定义本身不占用 Channel Unit。
只有当 Event Instrument 被 Logical Track 有效绑定并参与编译时，才会生成 Event Instrument Instance 并进入资源分配。
系统级规则：
```text
Event Instrument 不允许手动指定固定 Port。
Event Instrument 不允许手动指定固定 Channel。
SubVoice 不允许手动指定固定 Port 或 Channel。
资源分配由编译器自动完成。
初版每条 SubVoice 在一次 Event Instrument Instance 中占用一个 Channel Unit。
Per-Note Instance Isolation 开启时，每个逻辑音符独占 Channel Group。
Per-Note Instance Isolation 关闭时，共享范围限于同一 Event Instrument Usage 的活动连通区间；同一 Usage 可以跨多个 Logical Track，不同 Usage 不共享。
```
---
## 7.46 与编译、播放、预览、渲染、MIDI 导出的关系
### 7.46.1 与编译的关系
编译系统必须读取 Event Instrument 定义。
编译至少依赖：
```text
Root Note
Template Length
Pre-Roll Ticks
SubVoice 集合
事件内容
Mapping Function
Envelope Preset
Per-Note Instance Isolation
生命周期策略
Overlap 策略
Reset 策略
```
被实际编译使用的 Event Instrument 如果存在错误：
```text
编译失败
```
未引用 Event Instrument 的内部错误：
```text
不阻止整曲编译，但应在诊断中显示为警告。
```
无输出 Event Instrument 是否生成实例、是否占用 Channel Unit、是否被资源系统优化掉，由第 12 章《编译系统与 Canonical Compiled Result》规定。
### 7.46.2 与播放、预览和音频渲染的关系
播放、预览和音频渲染依赖编译结果。
修改 Event Instrument 定义会影响后续播放、预览和音频渲染结果。
Event Instrument 预览入口已由 第 7 章《Event Instrument Library 与 Event Instrument 定义》 确认，本章仅补充：
```text
预览应使用当前 Event Instrument 定义。
无有效 SF2 时预览不可用。
Event Instrument 定义错误会导致预览失败或入口禁用。
具体预览音高、预览长度、临时编译范围和 UI 行为由 第 13 章《播放与预览》 / 第 17～20 章的 UI 与交互规格 细化。
```
### 7.46.3 与 MIDI 导出的关系
MIDI 导出不直接导出 Event Instrument 定义本体。
MIDI 导出导出的是：
```text
Event Instrument 被 Logical Track 触发后编译出的标准 MIDI 事件。
```
未被使用的 Event Instrument 不出现在 MIDI 事件流中。
是否在导出 Readme 中记录使用到的 Event Instrument 定义信息，由第 14 章《MIDI 导出》规定。
---
## 7.47 错误、警告与信息诊断
### 7.47.1 错误 / 操作失败
以下情况应作为错误或导致操作失败：
```text
Root Note 缺失或非法
Template Length 缺失或非法
Template Length 小于等于 0
Template Length 小于最后事件所在 tick
Pre-Roll Ticks 缺失或不在 0..Template Length 范围内
Event Instrument 缺少必要 SubVoice
Mapping Function 名称为空
Mapping Function 名称在单个 Event Instrument 内冲突
被实际编译使用的 Mapping Function 编译失败
被实际编译使用的 Event Instrument 内部事件值非法
被实际编译使用的生命周期策略组合非法
被实际编译使用的 Reset 配置非法
无有效 SF2 时尝试 Event Instrument 预览
```
以下情况的诊断等级尚未闭合，应由 第 9 章《曲线、Logical Parameter 与映射》、第 10 章《实例生命周期、Loop、Envelope 与重叠》、第 12 章《编译系统与 Canonical Compiled Result》、第 15 章《音频文件渲染》 继续细化：
```text
Per-Note Instance Isolation 关闭，但定义中保留了必须依赖实例隔离的不兼容数据
```
### 7.47.2 警告
以下情况可作为警告或后续由第 15 章《音频文件渲染》规定的诊断来源：
```text
未引用 Event Instrument 存在内部错误
Event Instrument 被绑定但当前编译范围内未实际产生输出
Event Instrument 没有任何可发声事件，但被 Logical Track 触发
Event Instrument 使用了可能显著增加 Channel Unit 占用的设置
关闭实例隔离导致某些功能不可用、暂不生效或被禁用
```
### 7.47.3 信息
以下情况可作为信息或 UI 状态提示：
```text
Event Instrument 是 MIDI 事件模板，不是 SF2 preset
Root Note 只作为默认映射基准，不自动改写已有事件
Template Length 是模板长度，不等同于 Gate Length
Pre-Roll Ticks 只提前 Logical Segment 实例的模板原点，不改变 Logical Note 的 Gate Length
最小合法 Event Instrument 默认不自带 Note，因此可能无声
修改 Event Instrument 会影响所有引用处
如需变体，应复制后修改
Event Instrument 不绑定独立 SF2
Event Instrument 不允许手动指定 Port / Channel
无 SF2 不影响编辑、编译和 MIDI 导出，但预览不可用
关闭 Per-Note Instance Isolation 不会删除既有不兼容数据
```
---
