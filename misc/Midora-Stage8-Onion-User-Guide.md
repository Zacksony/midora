# 阶段 8：洋葱皮与 All Tracks 使用说明

2026-09-08 更新。对应 SRS 18.11、INV-115～116。此文说明已实现功能，不替代正式规格。

## 当前 Segment：参照其他轨道

1. 打开 Logical 或 MIDI Segment 钢琴卷帘，点击水平缩放按钮右侧的**图层图标**，选择 **Select Tracks...**。
2. 勾选来源轨道，或使用 **Select All / Clear**。
3. **OK** 应用视图配置；**Cancel / Esc** 不应用。来源音符出现在当前可编辑音符下方。

该设置属于目标 Track，同轨的多个 Segment 共用。自身不会出现在来源列表；Logical 与 MIDI Track 可相互参照。来源颜色使用轨道当前显示色，包括 Logical Track 的颜色覆盖。

图层菜单另提供 **Enable/Disable**（保留来源和模式）、**Show Previous Track / Show Next Track**（仅显示该邻居）以及 **Settings...**（只有透明度）。两个弹窗均没有 Enable 复选框。Previous/Next 是普通命令，不是勾选项；按当前 Arrangement 顺序，不循环。启用时按钮高亮。

手选来源与当前快捷模式独立：先在 Select Tracks 选择 B/C，随后 Show Previous 只显示上一轨；再次打开 Select Tracks，B/C 仍被勾选，直接 OK 即恢复手选模式并启用。Cancel 不切换模式。修改 Settings 的透明度也不会退出快捷模式或改变启用状态。保存后重开会恢复 Custom/Previous/Next 模式；前后模式始终按正式顺序解析，重排后跟随新邻居。

时间通过 Arrangement 的绝对 Tick 对齐，而不是把两个 Segment 的开头强行对齐。来源 Segment 未暴露的内容不显示；跨来源边界的长音符只显示暴露部分。目标 Segment 的内容偏移也纳入映射。

洋葱皮不参与点击命中、选择、移动、吸附、复制或统计。当前音符、选择高亮和编辑预览仍使用原来的正式编辑层。事件 Lane 不叠加洋葱皮。

## SubVoice：参照同一事件乐器的其他 SubVoice

在 SubVoice 钢琴卷帘的同组**图层图标**菜单选择 **Select SubVoices...**；前后命令改为 **Show Previous SubVoice / Show Next SubVoice**。行为与 Track 相同，手选列表也独立保留。每个目标 SubVoice 独立保存配置，自身排除；不同事件乐器之间不混用。来源按同一模板 Tick 对齐，使用柔和的区分色。

## 图层顺序

来源按当前 Arrangement / SubVoice 顺序叠加：第一项最底层、最后一项最上层。当前编辑内容在全部来源上方。调整轨道顺序或颜色会更新显示。

## All Tracks：整曲只读视图

在 Arrangement 工具栏点击水平缩放右侧的**图层图标**（Tooltip：All Tracks），直接打开独立 Tab：

| 模式 | 显示内容 |
| --- | --- |
| Raw | 全部轨道暴露范围内的源音符。逻辑音符尚未展开为事件乐器的 MIDI 输出。 |
| Compiled | 逻辑轨道使用最近成功编译的展开结果（映射音高、Loop、输出时长），MIDI 轨道直接显示当前源音符。 |

这是混合显示，目的是查看逻辑乐器展开效果，同时避免对大型 Pure MIDI 再建立整曲 FIFO 音符索引。MIDI 轨道的 Gate 与 Raw 相同；跨轨道共享 Channel 的最终 MIDI 流可能有不同的 FIFO 配对长度，本视图不展示该差异。播放和导出不变。

- **Current**：逻辑展开对应当前成功编译。
- **Logical stale**：逻辑层是上一次成功结果；MIDI 层仍为当前源音符。
- 没有成功结果时明确说明逻辑层不可用，MIDI 音符仍然显示。
- 准备在后台执行，显示当前阶段及阶段进度，可 **Cancel**；**Refresh** 用最后成功的 canonical 重试。索引就绪后才整体切换，不发布半成品。
- 只有逻辑展开需要构建显示索引；用时取决于逻辑编译输出规模。新索引构建期间保留旧的成功逻辑层，MIDI 层不等待此准备。

All Tracks 没有 Lanes、音符选择和编辑。滚动、缩放、Fit 只改变这个 Tab 的视图，不改变项目选择。

导航沿用钢琴卷帘：中键拖动平移；滚轮滚动音高；Shift+滚轮横向滚动；Ctrl+滚轮在内容处横向缩放、在左侧钢琴尺处垂直缩放。工具栏另有水平 Zoom Out/In 与 Fit。

Raw 和 Compiled 都显示播放指针，遵守全局 **Follow Playback**。中键平移或拖动底部概览期间暂停跟随，松开恢复；启用跟随且播放时，底部概览的滚轮滚动被禁用。

**左键单击时间标尺或音符内容区**设置绝对播放位置；播放/Buffering 中则直接 Seek。沿用普通播放的有效范围限制和冷启动规则，不开启连续 scrub，不选择音符或时间范围。左侧钢琴尺、右键不会跳转。

## 保存与对象生命周期

- 手选来源、Custom/Previous/Next 显示模式、启用状态、透明度及默认 Raw/Compiled 写入 Format 3 的独立 presentation schema 2；音乐文件格式仍为 3，软件版本不变。旧 presentation v1 继续可读，来源按 Custom 恢复。
- 这些设置不进入 Undo、不产生音乐 Modified 星号、不主动弹未保存提示。**需要保留时请显式 Save / Save Copy。**
- 关闭未保存的项目可能直接丢弃本次视图配置。Save Copy 不改变音乐保存语义。
- 删除来源后引用在会话内休眠；Undo 恢复相同对象时洋葱皮也恢复。保存副本时过滤仍不存在的对象，不把悬空引用写进文件。
- Duplicate Track/SubVoice 复制目标配置；Duplicate Event Instrument 会把内部 SubVoice 引用映射到副本，而不是串回源乐器。Undo 删除副本、Redo 恢复时保持此配置。
- presentation 损坏不应阻止音乐内容打开；沿用 Format 3 的隔离诊断与回退。

## 性能边界

洋葱皮使用独立只读栅格层，不改变已验收的普通钢琴卷帘渲染。每条横向条带缓存全 128 Key 的占用，按视图垂直缩放；因此垂直滚动/缩放可复用缓存。横向缩放按当前 device-pixel 精度缓存，密集音符聚合，不逐音符创建 WPF 控件。

当前可见洋葱皮条带的分组预算为 64 MiB，服从全局 256 MiB 栅格缓存；后台共用现有有界执行队列。预算不是整个应用进程的内存上限。Compiled 索引使用有界外部排序和分页存储，会使用程序根目录 `.tmp` 空间，关闭后释放；磁盘或预算不足时明确失败，不回退整曲常驻对象数组。

参见同阶段验证报告中的实测条件和限制；不要把单次实测当作所有设备上的延迟保证。
