# 极端 Tick 溢出防护：实施与验证记录

日期：2026-09-10。状态：代码与自动验证完成，用户已确认验收通过；验收后单独授权提交、推送，不包含本地发布。

## 需求追踪与边界

- 输入：既有合法 TPQN `1..32767`、Conductor 拍号图、非负 Int64 Project/owner Tick、视口与鼠标手势；包含接近 `long.MaxValue`、负 Project offset 的 Segment 隐藏内容及可返回普通范围的导航。
- 正式输出：既有整数网格、可逆 Bar:Beat:Tick、相同 Snap 中点规则、失败原子的编辑；纯显示在数值域末端安全停止，不回绕、不虚构格点、不修改音乐数据。
- 依据：SRS §4.1/4.4/4.9、§20.1/20.3/20.4/20.13，INV-014/020/041/083/095/096/099/107，以及下一步台账 §4。
- 无法表示的正式音乐结果仍明确失败，不能靠饱和运算截短 Note/Gate/Segment；失败不新增 Undo 或部分 Project 内容。显示与鼠标导航的有限域裁剪不是音乐运算的合法化。
- 不修改 Project Format、SRS、软件版本、音频 DSP、MIDI 编码或导出策略。本轮不实施 SMF delta 填充/MTrk 预算，不提交、推送或本地发布，不使用 computer-use，不读取或修改用户草稿。

## ADR-TICK-001：精确端点、可终止查询与显示域分离

1. 小节查询先在宽整数域计算真实末端，再根据用途投影。严格 Int64 API 仍拒绝无法表示的结果；新增明确的宽端点查询供网格/Snap 比较及显示，不能将 `long.MaxValue` 冒充自然小节线。
2. 网格迭代提供 Try 查询：没有可表示的下一格时返回无结果；参数非法仍是错误。绘图正常结束，不用全局 catch 或异常驱动逐格循环。
3. 坐标转换先计算相对视口的 delta，再在整数域加到 origin，避免把大 absolute Tick 转 double 后丢失整个可见跨度或将 `long.MaxValue` 舍入为 `2^63`。显示裁剪和 finite 检查集中复用。
4. 所有共享 Timeline 类型均纳入：Arrangement、Logical/MIDI Segment、SubVoice、Velocity/Event/Parameter、Conductor 与 All Tracks。导航只改变 Session 状态，正式编辑仍走既有原子命令。
5. 网格密度限制和可见查询保持有界；极端值不以逐 tick/逐小节追赶，普通范围保留快速路径与原始语义。

## 验证计划

- 宽整数参考 oracle：合法 TPQN/拍号、自然/截断小节、端点、正负 Snap、中点、可逆坐标及普通随机范围。
- 实际 WPF STA 自动测试：Measure/Arrange/Render、网格/标尺/内容、端点来回导航、选择/预览及返回普通范围，不以只调用坐标 helper 冒充渲染验证。
- 正式编辑超限零发布、Undo 不变，普通 Snap/时间工具回归。
- 普通规模网格/坐标性能对照，相关 Domain/Compiler/Presentation/Desktop/Application 回归；只记录实际执行结果。

## 结果

### 已复现的根因

1. `GetNextGridTick`/首格向上取整直接加到 Int64 之外；`GetBarContaining` 在比较候选之前先计算 Int64 小节末端。WPF 的网格/标尺路径因此可以直接抛出未处理异常。
2. 鼠标坐标与事件画线先将 absolute Tick 转成 `double`，在上界附近丢失相邻 Tick；`long.MaxValue` 转成 double 实际为 `2^63`，再转回 Int64 也会失败。基线坐标测试已观测到 23 Tick 的误差。
3. 非空瓦片在端点计算 `start + 1` 时回绕，向区间索引传入非法范围；超长 Note 的离屏像素边界转 `Int32` 也会溢出，后台任务无法产出位图。这不是只修背景网格就能覆盖的问题。
4. 创建/拖动、缩放按钮、概览滚动条还有独立的候选坐标运算；异步桌面回调不能依赖调用者的同步 catch。标尺单击还会不必要地先计算一个非空时间范围。

### 实施内容

- `ProjectMusicalTime`：`ProjectBarBounds` 保留真实 Int128 数学末端，直接按 tick 定位拍号；严格 `GetBarContaining` 仍拒绝无法表示的 Int64 末端。拍号截断先比较后窄化，Bar/Beat/Tick 规则不变。
- `ProjectTimelineGrid`：Try 首格、下一格和 Snap 分清“没有可表示的结果”与非法参数；正式 Snap/范围 API 保留明确失败。修正负 delta 在 Int64 端点的幅值与中点运算，普通值保留快路径。
- `TimelineGridPresentation`：网格和小节标尺共用有限范围生成器；跳过不可分辨的密集网格，不逐小节追赶极长区间。不会把数值域末端伪装成自然小节线。支持正/负 Segment Project offset。
- `TimelineTickMath`/viewport/overview：先算相对量再与整数 origin 相加；导航/像素查询使用有限域裁剪，正式音符/Segment 的长度不使用该裁剪来合法化。包含标题栏缩放入口与浮动工具的导航坐标。
- 事件画线的 `TimelineValueTracePoint.Tick` 改为整数；插值用整数差值，保留逐 Tick 结果。开始输出前校验所有手势端点，超限不留下部分 draft；顺序采样、后写覆盖、半开范围和取消规则不变。没有 Project/persistence/public wire 格式变化。
- 共享瓦片范围、包含上界的 tile 遍历、离屏像素边界统一防回绕；三种钢琴卷帘、Velocity/Event、Conductor 与 Arrangement 概览使用同一规则。仍是原缓存模型，没有增加 LOD 或改选区/命中语义。
- UI 在输入/draft 阶段拒绝超限操作并清理手势、预览和捕获；通过冒泡事件显示英文错误，不依靠全局异常处理器恢复进程。桌面异步背景入口单独保护。Snap 选择零 delta 时不因未选中的原始候选越界而错误拒绝；标尺单击不再构造虚假的长度。

### 自动验证

基线为 `6d5bbce60a1aad52e820c4c59568a1a2c9f58f69`。Windows x64、SDK 10.0.400、运行时 10.0.11、Release。没有调用 computer-use；WPF 用 STA 上的实际 `Measure`/`Arrange`/`RenderTargetBitmap.Render` 和合成路由事件测试。

- 最初 7 个新增测试全部失败（6 个实际 WPF 网格/标尺溢出、1 个坐标精度错误）；加入非空内容后，又复现 6 个瓦片非法区间错误；单独补测超长音符时，三类 Note 均复现像素 Int32 转换溢出。各基线 TRX 保存在 `.tmp/extreme-tick/`，没有删除或改写为成功记录。
- 新增永久用例：`ExtremeTickGridTests` 8 项、`ExtremeTickTimelineTests` 26 项。覆盖 TPQN 1/192/32767、合法 99 拍及 denominator 64、自然/截断小节、Int64 正负端点、约 7000 个随机 delta、宽整数 oracle、正负 Project offset、极密网格有界输出、三类 Note、事件/Velocity、超长 Segment 与超长 Note、有效端点导航、越界创建/移动/右边界调整零提交、返回普通范围继续编辑。
- 正式命令零部分发布的覆盖还包括既有 Application `TimelineGenerationCommandTests.ArithmeticFailureIsAtomicForAllOwners`、`CheckedOverflowPrecedesMaximumTickEarlyStop`、量化与高级编辑异常原子性测试；本轮不另写音乐编辑器或 Undo 机制。

| 最终完整回归套件 | 通过 | 失败 |
| --- | ---: | ---: |
| Compiler | 568 | 0 |
| Desktop.Presentation | 471 | 0 |
| Desktop | 434 | 0 |
| Application | 1140 | 0 |
| Playback | 155 | 0 |
| MidiExport | 62 | 0 |
| 合计（不累计中途重跑） | 2830 | 0 |

Desktop/测试项目 Release 构建通过；`git diff --check` 通过。完整 MIDI 导出回归在此仅证明旧导出行为未被改变，**不代表下一轮 SMF 填充需求已通过**。没有进行新的真实声卡或 9KX2 整体性能测试；普通 Passed 不冒充 opt-in 大样本实测。

### 普通范围对照与时间性能

临时 probe 使用上述提交的原始时间图、网格算法与钢琴瓦片代码，独立命名空间编译，与当前实现同进程比较。没有改变产品的可见性/程序集设置；仅 probe 使用既有测试友元身份访问内部渲染入口。脚本/源码留在已忽略的 `.tmp/extreme-tick/probe/`，不加入正式产品。

- 普通网格位置/类型逐项对照：10,000/10,000 通过；含 1000 次拍号变化。
- 三种音符、7 个缩放、每组 20 个瓦片的 BGRA 字节级对照：420/420 完全一致。它证明所测普通范围像素未改变，不是完整 UI 视觉验收。
- 预热后交替执行新旧各 7 次，使用中位数：

| 工作量 | 旧版 | 本轮 | 说明 |
| --- | ---: | ---: | --- |
| 100,000 次普通网格范围生成 | 142.435 ms | 73.118 ms | 约减少 48.7%；拍号定位减少重复查询 |
| 2,000,000 次普通 delta Snap | 50.520 ms | 51.957 ms | 约 +2.8%，总差约 1.44 ms；前一次重复测量为 +0.7% |

最终 7 次 min/max：网格旧 140.607–144.452 ms、新 72.095–75.759 ms；Snap 旧 50.004–56.083 ms、新 50.867–59.749 ms。两段复用缓冲的稳态托管分配都为 0 bytes。它们是算术/网格微基准，不等于整条编辑、WPF 帧率或编译时间；未观察到需要回退的普通范围计算退化。

### 仍保留的边界

- 本轮不扩大合法 TPQN、Project Tick、音频 sample frame、MIDI payload 等正式数值范围；不能表示的音乐结果仍是失败，而不是“尽量播/尽量保存”。
- 极大世界坐标的栅格仍使用既有浮点像素模型；有限域查询与裁剪防止回绕/异常，不能承诺在任意绝对 Tick 和缩放下逐 Tick 都有独立可分辨像素。本轮没有重构整个栅格坐标系，也不把图像用作命中或音乐数据。
- SMF 超长 delta 空 Text Meta 填充、MTrk 字节预算及导出级 Info/README 汇总仍未实施，下一轮独立完成。

### 建议人工验收（只需代表性检查）

1. 普通项目与一个常用大型项目：Arrangement、三种钢琴卷帘平移/缩放，检查网格、标尺、音符边框和选择颜色与原来一致。
2. 三种钢琴卷帘各做一次创建、移动、左右改长度及 Undo；事件/参数/Tempo Lane 画自由线、直线，检查 Snap 开关前后行为。
3. All Tracks / 普通钢琴卷帘的滚动条、缩放按钮、标尺跳转；检查返回普通位置后仍可继续操作。

接近 `long.MaxValue` 的危险构造已由自动测试覆盖，不要求用户在真实作品上手工冒险制造。用户随后明确确认本轮验收通过；保留上述清单供后续回归使用，不将该反馈扩大为任意硬件、任意数据的全覆盖验证。
