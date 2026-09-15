# Midora Instrument Catalog, Quick Mapping and Track Color Architecture Decisions

状态：阶段 3 实施基线（2026-09-02）

上位需求：`Midora-Major-Editing-and-Visualization-Expansion-Implementation-Plan-2026-08-31.md` 的 `WP-04` 与 `WP-09` Track Color slice；《Midora SRS》§6、§9、§11、§17、§18、§20、§22～24。

## ADR-CATALOG-001：Catalog 是程序级辅助数据

Instrument Catalog 由多个有序、可启用 Profile 组成，只保存于 `<ProgramRoot>\Data\Catalogs`。Project、canonical、MIDI Export、Audio Render 与音频缓存只保存或消费 Bank MSB、Bank LSB、Program 数值，不引用 Profile、名称或 Catalog ID。缺失或损坏 Catalog 只令 UI 回退为数值名称，不得阻止 Project 打开、编译、播放或导出。

名称解析顺序固定为：User Overrides → 当前 Enabled SoundFont 正式顺序所绑定的 Imported SF2 Profile → Enabled User Profile 顺序 → 内置 General MIDI Profile → 数值 fallback。返回值同时携带可见来源标签；内部 ID 永不展示。

## ADR-CATALOG-002：SoundFont 列表项具有稳定程序级身份

每个 `ApplicationSoundFontPreference` 具有持久 GUID `SoundFontEntryId`。它只用于把 Imported SF2 Profile 稳定关联到列表项，不进入音频配置等价性、Worker 重建判定、SoundFont descriptor/cache identity、Project 或导出结果。重排、启用/禁用、修改 target 均保留 ID；删除后 Profile 可保留为 orphan 并继续浏览，但不参与当前播放上下文名称解析。

旧 Application Preferences schema 在读取时为既有列表项确定性补齐 ID；下一次成功 Apply 写入新 schema。迁移不得丢失整个 SoundFont 列表，也不得仅因 ID 变化重建 Worker。

## ADR-CATALOG-003：SF2 扫描是显式、轻量、只读操作

`Scan Presets...` 只顺序解析 RIFF `sfbk` 内 `pdta/phdr` 元数据，seek 跳过 sample 与其他 chunk；不调用 BASS、不加载 sample、不修改 SoundFont 设置、不在 Project 打开、Preferences Apply 或音频 Preparing 时隐式运行。扫描快照保留 raw `ushort bank`、raw preset 与名称；终止 `EOP` 记录不导入。

raw bank `0..127` 的默认预览为 `MSB=raw bank, LSB=0`。raw bank `128..65535` 不截断、不取模，也不自动解释为 percussion；导入前必须显式映射到合法 MIDI MSB/LSB。SF2 更新不自动刷新快照，只有用户显式 Rescan 才替换。

## ADR-CATALOG-004：Catalog 文件独立版本化且原子发布

Catalog store 与独立导入/导出文件使用 source-generated JSON、明确 schema/version、稳定排序、大小/条目数上限、重复 triple/重复 JSON property/未知字段拒绝。编辑与 Merge 全程作用于 draft；提交时使用临时文件、flush 和原子替换。损坏活动文件保留原字节并回退到 General MIDI，不以默认文件覆盖它。

导入只提供显式 `Replace Profile` 或 `Merge With Preview`。Merge preview 冻结新增、保持与覆盖 triple；用户确认前不写活动状态。

## ADR-MAP-QUICK-001：快捷绑定仍生成正式 Mapping 对象

`Add Event Binding...` 一次冻结 Event Instrument、目标 MIDI value target、当前目标 SubVoice ID 集、名称、Integer source range、operation/range 与冲突策略。一次 Project command 创建一个 Logical Parameter、每个目标 SubVoice 一个 `LogicalParameterMapping`，并在缺失时创建对应的空 `SubVoiceEventMapping` owner；不得创建 tick 0 或其他事件点。

`All SubVoices` 只展开为 OK 时存在的 SubVoice ID；以后新增 SubVoice 不自动绑定。任一目标无效、CC91/CC93、范围非法、冲突策略不完整或 owner revision 变化均整批失败，且不得消耗正式 stable ID 或留下部分对象。

同 `(SubVoice,target)` 冲突时：Append 放到该 target 现有 mappings 之后；Replace 原子替换全部 exact-target mappings并保留其他 target 的相对顺序；Cancel 不执行命令。Undo/Redo 恢复相同对象身份与顺序。

## ADR-MAP-QUICK-002：运算复用正式 accumulator

Override、Add、Multiply 继续在正式 target accumulator `c` 上执行，不建立 UI 私有计算器。快捷创建的最终 integer overflow 固定为 Clamp、rounding 固定为 Round：

- Override：absolute logical value 覆盖 `c`；Parameter default 使用目标正式 reset/default；
- Add：`c + offset`；Parameter default 为 0，默认合法范围使用目标对应的双极范围；
- Multiply：先把 Integer source range 线性映射到 factor range，再计算 `c × factor`；`default=1` 指映射后的 factor 必须精确为 1。命令通过逆映射计算 Integer Parameter default；factor range 不包含 1、source range 退化或逆映射结果不是精确整数时整批拒绝。

现有单个 Mapping Step 无法同时保留 `c` 并 remap source 时，实现允许由同一原子命令创建一个受 ABI v3 白名单、资源上限与依赖复核约束的共享表达式资源，并由 Mapping Step 正式引用；它不是任意 C# 或第二套运行时。该资源与快捷绑定同生同灭，进入同一次 Undo/Redo。

Override 的 target default 是对正式 reset/default 的中性值，不承诺保持用户已另行写入的 Initial State 或原始 target event；对话框必须将 absolute overwrite 语义写清楚，不能把它描述为 relative/no-op。

## ADR-COLOR-001：Pure MIDI Track 固定调色板

Pure MIDI Track 新建与 SMF 导入使用固定八色低饱和调色板，按最终 global Arrangement order 中此前出现的 Pure MIDI Track 数量轮换。中间插入只给新 Track 分配颜色，不重染既有 Track；删除、排序和 Root 变化也不重染。

Duplicate、Copy/Paste 继承源 Track 的 concrete color。旧数据若没有颜色，显示层使用固定 fallback，而不是根据当前顺序动态计算。颜色属于 Project presentation metadata，修改进入 Undo/Redo 和 Project Modified，但不得触发 canonical 或音频缓存失效。

Logical Track 继续使用 `ColorOverride ?? EventInstrumentDefinition.Color ?? fallback`；清除 override 恢复继承，不得把 override 写回共享 Definition。

## 验证门

- Preferences schema 迁移与 SoundFont ID 稳定性；ID 不触发 Worker rebuild；
- Catalog store 的严格 JSON、确定 round-trip、损坏隔离、原子 Replace/Merge；
- GM resolver、冲突优先级、Disabled/orphan、数值 fallback；
- SF2 RIFF padding、边界、EOP、raw bank 127/128/65535、取消与大 sample seek；
- 快捷 Mapping 的 current/multiple/all、Append/Replace/Cancel、空 owner/no tick0、失败原子、一次 Undo/Redo、保存重开、Full/Incremental 等价；
- Override/Add/Multiply endpoints、held `c`、范围、Clamp 与同 target order；
- 新建、普通/流式 MIDI 导入、Duplicate/Clipboard 的颜色序列；Logical override 回归；
- Catalog/颜色只影响 UI 名称/色彩，不改变 canonical fingerprint、MIDI bytes 或音频计划。
