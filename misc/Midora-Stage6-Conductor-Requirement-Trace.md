# 阶段 6：Conductor 编辑器需求追踪

状态：阶段 6 功能与六项 Conductor UI 调整已于 2026-09-07 全部通过用户验收，获准提交、推送，随后实施阶段 7；不发布 `dist`。

## 功能验收后的 UI 调整

1. Time Signature / Key Signature 的头部加宽，移除不存在的轨道按钮所占的文字裁剪留白。
2. Tempo 和 Meta 共用完整左头宽度与时间原点，取消 Tempo 外侧空白；播放指针同步对齐。
3. 普通 Tempo 点、阶梯线以及 Arrangement Tempo 预览使用主题绿色；选择色不变。
4. Fit 右侧提供 Reset，显示轴恢复 0～240，并清除纵向缩放/平移；不得改变水平位置、水平缩放、音乐内容或 Undo。
5. Conductor 图内的 A 必须切换正在使用的主 EditorSettings Snap，而非 Segment 专用 LaneEditorSettings。
6. 密集点的异步精确命中结束后立即刷新悬停指针；仍保留 UI 查询预算、后台查询与当前修订校验，不改瓦片渲染机制。

## 依据

- `Midora-Major-Editing-and-Visualization-Expansion-Implementation-Plan-2026-08-31.md` §5.5、WP-05、阶段 6、UI-01～03/08 的已确认决定。
- SRS 第 4 章、18.7、20.1～20.4、22 章跨系统不变量。
- 用户要求继续下一阶段；不使用 computer-use。

## 输入、输出与边界

| 项目 | 约束 |
| --- | --- |
| 正式输入 | Project Conductor 的 Tempo、Time Signature、Key Signature、Marker、可选 Project End；不接纳新的 opaque Meta 类型 |
| 正式输出 | 可取消的原子 Project 编辑及精确 Undo/Redo；编译、播放、导出仍只消费正式 canonical |
| 视图 | 左侧虚拟事件列表；右侧高 Tempo 阶梯图及其他正式事件 lane；列表与时间线共享选择 |
| Tempo | decimal BPM，正数且 MIDI 可表示；离散保持，不引入 ramp；tile 左界恢复前驱状态 |
| 编辑 | 自由线、右键直线、Shift+右键水平线、Shift 固定 tick、Ctrl 复制；复用现有 Event Lane 手势与 Snap |
| 碰撞 | Tempo/拍号/调号同 tick 后来编辑者覆盖；Marker 可同 tick、同名或空名 |
| Tick 0 | Tempo/拍号必需初始状态不可删除或移动，只允许修改值 |
| 持久化 | 保持现有 Format 1/2/3 Conductor wire 契约；列表宽度、轴显示范围仅为工作区会话状态 |
| 有界性 | 分页查询、稀疏列表索引、可取消 detached staging；不创建百万行 WPF 控件、ObservableCollection 或渲染数组 |
| 失败 | 格式/数值非法、取消、资源超限、I/O 或 revision race 均零发布；既有 Project 和 Undo 不受影响 |
| 非目标 | 三种钢琴卷帘事件列表、Onion Skin、音频重构、版本升级、Conductor Batch Create/Quantize |

## 验证门

1. 小数据五种事件、tick 0、同 tick 覆盖与 Marker 并存、批量复制/移动/删除、取消、Undo/Redo。
2. 左界前驱、密集阶梯包络、固定像素点、源数据命中、列表与图形选择同步、焦点恢复。
3. 10k/1M 事件范围/ordinal/ID 查询、编辑与快照、滚动/缩放、后台失效；报告真实测量和未测范围。
4. 编译重复确定性、Full/Incremental、范围起点状态恢复、既有持久化往返；三种钢琴卷帘回归。

详细取舍见 `Midora-Stage6-Conductor-Architecture-Decisions.md`；自动验证、实测及保留限制见 `Midora-Stage6-Conductor-Validation-Report.md`，人工检查见 `Midora-Stage6-Conductor-Acceptance-Checklist.md`。自动通过不代表人工视觉和交互验收通过。
