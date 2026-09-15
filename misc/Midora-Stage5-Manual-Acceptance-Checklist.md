# 阶段 5 人工验收清单

本轮：Note/Event Batch Create、Logical/MIDI Segment 双向转换。不包含后续阶段的 Conductor 重设计、对象列表和洋葱皮。

建议使用可丢弃测试项目。先创建一个事件乐器、绑定的 Logical Track、一个 MIDI Track，并各建一个 Segment。SubVoice、Logical Segment、MIDI Segment 三种音符目标，以及它们的一个数值事件 Lane 各测一次入口。大规模压力测试可只选一两种目标；自动测试已分别覆盖三种。

## 批量创建

| 完成 | 检查 | 操作与预期 |
|---|---|---|
| [ ] | 三种音符入口 | 在钢琴卷帘空白处右键，空选区也可用 `Batch Create Notes…`；打开时 Base Tick 是蓝色编辑指针的相对 Tick。 |
| [ ] | 音符基本生成 | Base=0，Maximum Candidates=8，Velocity `=64+i`，Key `=60+i`，Gate `=24`，Tick `=i*48`。产生 8 个音符，位置 0～336，每隔 48 Tick；结果全部选中。 |
| [ ] | 初始值开关 | Maximum=3，Initial Tick=10，Tick `=t0+20`。关闭首对象开关得到 30/50/70；开启得到 10/30/50。其它字段留空时保持 Initial。 |
| [ ] | 字段依赖与上限 | Key `=60+i`，Velocity `=k1+10`；可以正确依赖本轮 Key。把 Key 改成 `=v1` 后验证应拒绝循环。相对起点上限 96、Tick `=i*48` 时，只保留 0/48/96。 |
| [ ] | 越界与运行失败 | Base=0、Maximum=1，Tick `=-10`、Key `=200`、Velocity `=0`、Gate `=0` 应正规化为 Tick=0、Key=127、Velocity=1、Gate=1。`=Sqrt(-1)` 等非有限运行结果必须拒绝整个生成，不留下部分对象；非法 Maximum 在窗口中拒绝。语法验证成功不代表每轮运行一定成功。 |
| [ ] | 三种事件入口 | 在已有数值 Lane 空白右键打开 `Batch Create Events…`。Value `=i*16`，Tick `=i*48`，Maximum=8；在当前 Lane 生成 8 点，不误创建音符或修改其它 Lane。 |
| [ ] | 精确碰撞规则 | 同一个位置再次生成上述音符：原音符保留、不增加重复音符；事件再次生成则新值覆盖同 Tick 同 Lane 的原值。未命中的既有重复记录不变。 |
| [ ] | 表达式与窗口 | `=` 后补全包含 Math 函数及当前工具的变量；长行自动换行。验证错误红色、成功绿色、改值后灰色。Help、Enter/Create、Escape/Cancel 和窗口缩放无裁切。 |
| [ ] | 预设 | 保存包含 Initial、首对象开关、最大候选数和相对 Tick 上限的预设；改 Base 后加载，除 Base 外均恢复。Note/Event 列表分离、撞名提示，预览/删除可用。损坏或未知版本的预设应明确拒绝，不影响其它预设。 |
| [ ] | 大量生成与取消 | 可用 Tick `=i*2`、Key `=i%128`、Gate `=1`，Maximum=100000 或 1000000。进度可见；准备期间取消不留下部分结果，也不改变旧选择/Undo。 |
| [ ] | 结果路由与历史 | 创建后直接 Ctrl+E、Ctrl+Q、Properties、拖动、删除均作用于生成结果。Undo 恢复原内容和原选择；Redo 恢复生成结果和结果选择；不必重新单击才能用快捷键。 |

## Segment 双向转换

| 完成 | 检查 | 操作与预期 |
|---|---|---|
| [ ] | 双向拖动 | Logical Segment 拖到已建 MIDI Track；MIDI Segment 拖回绑定事件乐器的 Logical Track。共同 Note 字段、Segment 长度和裁剪窗口保留；不把 Event Instrument 展开成编译后音符。 |
| [ ] | 复制拖动与剪贴板 | Ctrl 拖动应保留源并选择副本；跨类型 Ctrl+C/V、Ctrl+X/V 可用。一次 Undo 恢复一次转换；剪切和后续粘贴仍是两个独立可撤销操作。 |
| [ ] | 丢失摘要与取消 | 先在源建参数 Lane/参数点或 CC/SysEx，再跨类型移动。显示对应种类和准确数量；空逻辑参数 Lane 也应列出。取消后源/目标不变；确认只转换共同音符字段。 |
| [ ] | MIDI 特有字段损失 | 用含非零 NoteOff Velocity 或同 start+key 重复 Note 的 MIDI Segment 转到 Logical。摘要应分别给出损失数量；重复 Note 按原 formal order 保留第一条。NoteOff Velocity 全为 0 时不单独提示该字段。没有这类测试素材时可标为未人工验证，相关自动测试已覆盖。 |
| [ ] | 隐藏内容 | 在 Segment 暴露范围外放 Note，再裁小 Segment 并转换。重新扩大后隐藏 Note 仍在；隐藏非 Note 数据同样计入丢失确认，不静默漏报。 |
| [ ] | 混合选择与目标失败 | 同选 Logical/MIDI Segments，移动/复制时保持全局 Track 相对间隔。落到重叠 Segment、Conductor、越界位置或未绑定 Logical Track 时，全部拒绝，不留下部分结果。 |
| [ ] | 既有操作不退化 | 同类型横/纵移动、左右边缘调整、普通复制、混合 Segment 左右边缘调整，仍保持原行为与性能；转换后可继续批量操作并正确 Undo/Redo。 |

视觉样式、实际鼠标拖动手感仍以人工验收为准；源码测试或 BAML 加载测试不能代替这些检查。本轮没有调用 computer-use，也没有生成本地发布包。
