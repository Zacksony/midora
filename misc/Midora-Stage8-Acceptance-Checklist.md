# 阶段 8 合并验收清单

阶段 8 主功能及后续独立 Settings/来源弹窗、快捷命令、模式持久化、All Tracks 导航均已获用户验收并提交、推送。2026-09-09 用户再次确认目前人工大体全部通过；精细体验、完整作品 Bug 修复及查漏后置到真实编曲。细节见 [最终修订记录](Midora-Stage8-Onion-Source-Modes-and-Navigation-2026-09-08.md)，操作入口见 [使用说明](Midora-Stage8-Onion-User-Guide.md)。

下列空框保留为后续可复用回归模板，**不代表阶段 8 仍未验收**，也不将总体反馈伪造成每一项、每一规模已独立测量。自动测试不代替人工外观验收。

| Check | 场景 | 预期 |
| --- | --- | --- |
| ☐ | Logical/MIDI Segment 与 SubVoice 的水平缩放右侧 | LayerDiagonalRegular 图标与缩放同尺寸同组；启用时高亮；单击原地弹主题菜单。 |
| ☐ | 三种钢琴卷帘的图层菜单 Enable/Disable / 前一项 / 后一项 | 前后无对勾，只显示对应邻居；Disable 保留模式和手选列表；边界不循环，SubVoice 不跨乐器。 |
| ☐ | 手选多个来源 → Previous → Next → 再打开 Select Tracks/SubVoices | 原手选勾选完整保留；Cancel 保持快捷模式，OK 恢复手选显示。 |
| ☐ | Settings... 的 OK/Cancel/Esc；在快捷模式和 Disable 时调整透明度 | 只有透明度，没有来源/Enable；OK 不改模式或启用状态；关闭后快捷键可继续。 |
| ☐ | 分别在 Custom/Previous/Next、Enable/Disable 下 Save/Save Copy 后重开 | 模式、手选列表、透明度、启用状态全部保留；旧项目的原有列表仍可用。 |
| ☐ | Previous/Next 下重排轨道，删除邻居，Undo | 显示当前正式顺序的邻居，无邻居则为空；手选列表不被覆盖。 |
| ☐ | All Tracks Raw/Compiled 停止时单击标尺/内容区，再在 Playing/Buffering 中单击 | 正确定位/Seek；仍能停止；不选择/修改音符、不建立时间选区；中键仍平移。 |
| ☐ | Arrangement 缩放右侧的同款图层图标 | 直接进入 All Tracks，不弹菜单；工具栏控件高度与钢琴卷帘一致。 |
| ☐ | All Tracks Raw/Compiled 播放及切 Tab | 两模式均显示正确的绝对播放指针；跟随开关生效。 |
| ☐ | All Tracks 开启跟随，播放时中键或底部概览拖动 | 拖动暂停跟随，松开回到指针；底部概览滚轮被阻止；关闭跟随后正常滚动。 |
| ☐ | 大型纯 MIDI 项目切 Raw/Compiled，再用映射/Loop 的逻辑轨道混合检查 | MIDI 始终保留源 Gate，不等待整曲 FIFO 索引；只有逻辑层展开编译结果。 |
| ☐ | Logical Segment → 图层菜单 Select Tracks...，选择另一条 Logical 与一条 MIDI Track | 两类来源均显示，当前音符在最上层；取消弹窗不改配置。 |
| ☐ | MIDI Segment 使用同样入口 | 行为一致；只显示来源音符，不影响事件 Lane。 |
| ☐ | 同轨打开两个不同位置/Content Offset 的 Segment | 来源配置共用，但时间按各自绝对位置正确对齐。 |
| ☐ | 来源有多个 Segment、裁剪边界、跨边界长音符 | 只显示来源暴露范围；隐藏部分不泄露。 |
| ☐ | 来源弹窗 Select All / Clear、菜单 Enable/Disable、Settings 透明度 0、30、100% | 显示及时更新；自身不可作为来源。 |
| ☐ | 调整轨道顺序、颜色及 Logical Track 颜色覆盖 | 洋葱皮颜色/上下层次跟随；后面的轨道在上。 |
| ☐ | 在洋葱皮上点选、框选、拖动，执行复制或 Delete | 不选中/修改来源；正常命中当前内容。 |
| ☐ | SubVoice A 参照 B/C，再切到 B | A/B 配置独立，模板时间对齐；不同乐器不串配置。 |
| ☐ | 打开 All Tracks，Raw 模式 | 全部轨道按色叠加；没有 Lanes，不能选择或编辑音符。 |
| ☐ | All Tracks 滚轮、中键、缩放、Fit | 可以导航，不改变其他编辑 Tab 的视图和选择。 |
| ☐ | 简单事件乐器含映射/Loop，切 Raw/Compiled | Compiled 展示实际编译出的音高、数量和时长，Raw 保留源逻辑音符。 |
| ☐ | 同 Root 多轨道同 Key 交叠 | Compiled 的 MIDI 部分与 Raw 一致，不做最终流的跨轨道 FIFO 重配对；各轨颜色保留。 |
| ☐ | 修改音乐、制造编译失败 | 旧逻辑展开标 Logical stale；无成功结果时只缺逻辑层，当前 MIDI 源音符仍可查看。 |
| ☐ | 大型项目 Compiled 准备中 Cancel / Refresh / 关闭 Tab / 关闭项目 | 状态明确，操作可用，不发布半成品或过期结果，不导致崩溃。 |
| ☐ | 删除洋葱皮来源，再 Undo/Redo | 删除后消失，Undo 恢复；选择状态不被改写。 |
| ☐ | Duplicate Logical/MIDI Track、SubVoice、整个事件乐器 | 复制目标配置；整个乐器副本的 SubVoice 引用只指向其内部对应副本。 |
| ☐ | 仅改视图配置 | 没有音乐 Modified 星号和 Undo 记录；不触发音乐编译/音频重建。 |
| ☐ | 显式 Save / Save Copy 后重开 | 来源、启用、透明度和 All Tracks 模式保留；缩放/滚动不作为本轮新增持久化。 |
| ☐ | 仅改配置后直接关闭 | 不额外提示保存；未显式保存的视图配置允许丢失。 |
| ☐ | 100/200/400 轨道或大型 MIDI，开洋葱皮后快速移动/缩放 | 后台逐步显示，UI 可操作；不会修改普通音符的边框、颜色和选中渲染。 |
| ☐ | Source 单轨编辑、Undo，再切换各 Tab | 来源更新，远处/其他分组缓存可复用，旧后台结果不覆盖新结果。 |
| ☐ | 原有小规模创作流程：创建、编辑、多选、Properties、批量操作、Undo/Redo | 已验收功能没有入口、焦点或选择状态回退。 |
| ☐ | 原有大型 MIDI 流程：选择、剪切粘贴、批量操作、取消、保存重开 | 内存和交互无明显回退；长操作仍有安全取消和正确选择恢复。 |
| ☐ | 编译、播放、Mute/Solo、停止、MIDI 导出、音频导出 | 洋葱皮开关和模式不影响可听内容、导出内容或资源分配。 |
| ☐ | 新弹窗与 All Tracks 的主题、长轨道名、滚轮、Enter/Esc、关闭后快捷键 | 无裁字/默认白底/焦点异常；Help 入口可用。 |
