# Midora 极端时间线性能重构 Requirement Trace

状态：本轮实施完成

依据：SRS 第 18、24 章，INV-065～INV-069、INV-077，ADR-CORE-054、ADR-CORE-055，以及产品所有者 2026-08-26 的千万级编辑性能验收要求。

## 1. 正式输入与输出

- 输入：Direct MIDI、Logical Segment、SubVoice 的 Note/Event/Parameter 数据；Arrangement Segment；Workspace Selection；编辑命令；Undo/Redo；viewport、DPI、LOD 与后台编译/栅格任务。
- 正式输出：语义不变的 Project Source Data、稳定 ID、确定顺序、碰撞结果、Selection 结果、Undo/Redo 状态与 Canonical Compiled Result。
- 运行时输出：不可变分页修订、range change set、选择摘要、当前投影 tile、Arrangement coarse/detail preview、可取消后台任务和有界缓存。
- 失败原子性：任何验证、算术、分页、碰撞、编译或取消失败都不得留下部分 Project 编辑、错误 History entry 或属于旧 revision 的可见 tile。
- 持久化：性能实现不得把 selection、History、页边界、tile、编译结果或运行缓存写入 `.midora`。只有在现有 Project 数据契约确实无法表达正式结果时，才另立格式升级决定。

## 2. 强制非目标

- 不改变任何现有编辑手势、键盘修饰、碰撞、clamp、删除越界对象、选择恢复或 Segment 暴露范围语义。
- 不用近似/聚合数据提交正式编辑；occupancy/LOD 只服务视觉渲染。
- 不以第二次热缓存结果代替首次冷路径，不以平均值掩盖最大 Dispatcher stall。
- 不在本轮主动改变音频语义、缓存语义或 BASS 时序。

## 3. 编辑覆盖矩阵

每个适用数据类型都要验证 Apply、Undo、Redo、选择结果、碰撞结果、局部指纹恢复与 Full/Incremental 等价。

| 操作 | Arrangement Segment | Direct MIDI Note | Logical Note | SubVoice Note | Direct MIDI Event/Opaque | Logical Parameter | SubVoice Event |
|---|---:|---:|---:|---:|---:|---:|---:|
| Create/Draw/line | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Delete/Erase | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Move | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Copy-drag/Paste/Duplicate | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Resize left/right | ✓ | ✓ | ✓ | ✓ | N/A | N/A | N/A |
| Velocity/value paint | N/A | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Flip/Scale/Transpose/Batch | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Split/window/left expansion | ✓ | N/A | N/A | N/A | N/A | N/A | N/A |
| Mixed Logical + MIDI Segment | ✓ | N/A | N/A | N/A | N/A | N/A | N/A |
| Marquee/add/remove/toggle selection | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

额外边界：同 tick/key Note 后来编辑者删除；同 tick/lane Event 后来编辑者覆盖；导入既有重复在未命中编辑时保留；左右 Resize 每个对象独立 clamp；跨 key/tick/Segment/Track；Segment 外隐藏对象不得丢失；Alt/Ctrl/Shift 组合；Snap 开/关；冷/热页。

## 4. Presentation 覆盖矩阵

- Arrangement：唯一 coarse fallback、detail LOD 局部替代、Conductor、Segment Note/Event preview、scrollbar overview、hover/edge hit、parent/blank 区域。
- 三种 Piano：Direct MIDI Segment、Logical Segment、SubVoice 共用 base/selection/gesture pipeline；pan、horizontal/vertical zoom、冷区域、Segment 右侧空白、first/last Note、tile seam、不同 DPI。
- 三种 Velocity：Direct/Logical/SubVoice 的显示、single edit、trace、selection、move/copy、Undo。
- 三种 Event/Parameter Lane：Direct MIDI Event/Opaque、Logical Parameter、SubVoice Event 的点、线、Shift 水平线、选择、覆盖碰撞、缩放与滚动。
- 辅助：primary outline、编辑/播放 cursor、后台编译切换 revision、关闭 Tab/Project 后取消、极端缩小时 occupancy 路径。

## 5. 性能测量规则

- 配置：Release、win-x64、真实 WPF Dispatcher；数据层测试与 UI 端到端测试分开报告。
- 主要样本：`9KX2 18 Million Notes.mid`；补充使用 1M、3M、6.7M、7M、32M 样本和确定性合成数据。164M 只在最终阶段运行。
- 大选区：60,000 与 100,000 Note；同位置、近处、远处、跨 Track；首次冷 Apply/Undo/Redo 与第二次热路径分别记录。
- UI 指标：每次 Dispatcher 工作时长、连续 gesture p50/p95/max、可见 tile 首次完成延迟、旧任务数、completion 合并次数。
- 内存指标：managed heap、LOH、GC 次数、decoded page cache、completed raster bytes、in-flight captured revisions、WPF/native Working Set；观察内存是否随 gesture 帧数线性增长。
- 正确性 oracle：小规模随机输入与朴素模型逐对象比较；大规模比较排序后的正式字段、对象计数/ID、碰撞 survivor、History 状态、local fingerprint 和 canonical fingerprint。

## 6. 当前验收门

以下是重构门，不是改变产品语义的需求：

- UI 线程不得执行 Content Pack 文件读取、Brotli、SHA-256、全量排序或按候选数无界的 hit test。
- 60,000 Note Resize pointer-down/连续预览不得创建逐 Note WPF Geometry；预览成本由可见像素/tile 数限制，内存不得随鼠标帧数线性增长。
- Arrangement 连续 zoom 只切换 detail LOD，始终保留唯一 zoom-independent coarse fallback。Piano Note + Selection、Velocity、Event Point 分别按视觉单元原子提交：同一精确投影/DPI 下，替代修订尚未完整时保留上一完整单元，完成后一次替换；绝不跨缩放/LOD/DPI 复用，也不得把旧选择像素与新内容像素混画。可见冷 tile 必须优先调度，过时投影必须取消，不能以 UI 线程同步扫描大型数据换取即时显示。
- 低缩放聚合不得用跨多个 device column 的时间包围盒与 lane 并集生成近似矩形；聚合和详细 raster 必须共用 half-up device-column 投影。Selection 也必须复用相同列边界，低缩放边缘不得暴露未选颜色。
- Segment overview 的 content extent 与 viewport/navigation extent 必须分离；进入/离开右侧空白只改变投影，不得同步重建或扫描完整 Segment 摘要。
- 首次 Undo 不得等待正在运行的后台编译复制；冷/热差异必须可由明确、有界的页预取解释。
- 每次文档 revision 在单个 Dispatcher frame 内最多触发一次合并刷新；局部 Note/Event 编辑不得重建无关 Workspace、Project Tree 和无关属性视图。
- 过时后台 raster/compile 结果不得发布；Project/Workspace Close 后不得继续持有可释放的大型 revision。
- 对已有大选区执行小范围 Add/Remove/Toggle 不得为了检测 Revision 变化而复制整个选择集合；属性投影只能按所选 Stable ID 定向解析，不得枚举整个千万级 Segment 来确认对象类型。
- Select All、Invert Selection、Properties 等可能解析海量对象的显式功能必须保持精确语义，但其 Content Pack 读取、解码和全量 ID 收集不得阻塞 WPF Dispatcher；完成前不得把 Pending 误判为空选择。

## 7. 真实 MIDI 导入与持久化专项（2026-08-26）

- 输入：只读真实样本 `9KX2 18 Million Notes.mid`（144,175,201 bytes）、SMF Format 0/1 + TPQN 流、40 个派生 Pure MIDI Track pack、取消令牌和 save-owned staging content。
- 正式输出不变：17,999,999 Note、3,484 其他 direct/opaque event、2,565 page、395,898,739 pack bytes；page ordinal、endpoint 顺序、decoded SHA-256、目录顺序和 Brotli Fastest payload 均不依赖 worker 完成竞争。两次最终独立进程运行的 40-pack 合并 SHA-256 均为 `1cdf2b4ccab25b2aad1023b0648a132a8f19d456a6f9268cfb50bf3cfdc7ed28`。
- 基线（同机 Release）：pass1 3.021 s、pass2 47.208 s、总导入 50.897 s。最终两次运行分别为 `2.834 / 12.745 / 16.306 s` 与 `2.814 / 12.387 / 15.897 s`（pass1 / pass2 / import）；pack bytes 与合并 SHA 均一致。
- 最终两次内存：采样 peak managed 为 228,008,304 / 219,083,440 bytes，process peak working set 为 297,181,184 / 297,086,976 bytes；有界管线观测到的最大逻辑 in-flight reservation 均为 50,183,828 bytes，低于固定 64 MiB 门。reservation 对普通页计 decoded + codec 最大输出，对 endpoint 页额外计 detached source-record buffer；pending page 同时受 `2 × worker` 限制。
- 分项：最终 worker aggregate 为 endpoint sort 7.83～7.89 s、endpoint serialize 0.39～0.44 s、SHA-256 15.24～15.57 s、Brotli 15.01～15.60 s；这些值跨 worker 相加，不能与 pass2 串行相加。导入线程实际 queue wait 0.82～0.87 s、顺序 write 0.35～0.39 s、finalize 0.63～0.65 s。根因是旧路径把 endpoint sort/serialize/SHA/Brotli/写盘全部逐页串行执行，不是 Zip 或编译。
- codec 同 decoded payload 临时基准：Brotli Fastest `12.981 s / 395,535,799 bytes`；Deflate Fastest `10.242 s / 657,098,821 bytes`；无压缩计数 sink `0.002 s / 2,808,250,692 bytes`。该基准不含真实无压缩磁盘写入；结合最终 Brotli queue wait 小于 0.9 s，没有证据支持为 codec 破坏格式，因此保留当前格式与 Brotli。
- 失败/取消：Complete、Dispose、取消和 encoder fault 都 drain/观察后台任务；取消或 fault 的未发布 `.mpk` 测试均确认无残留。导入 cleanup 逐 writer 尝试并继续释放 Project；cleanup fault 与原 import fault 同时发生时保留原 fault 为聚合异常第一项。
- `.midora` 审计：普通 Open 只做一次 Zip entry→session backing 的有界 copy+SHA，随后只读取 pack header/directory/footer，不会全量 materialize page。pristine Save 原路径会 staging copy、再整包 SHA scan、写 Zip、随后自校验再次 extraction；现在 staging copy 与 SHA 合为一个 pass，自校验仍完整验证 Zip entry SHA，但复用 save-owned、已锁定的 staging pack 读取目录/语义，取消第二份大 pack 写入。普通 Open、dirty pack merge、Zip hash、严格 reopen、临时文件发布与失败清理语义不变。
- 验证：`PureMidiContentPackTests` 10/10（含 concurrency 1/4 全字节一致、roundtrip、取消删除、后台 fault 可观察且删除）；`Midora.Persistence.Tests` 78/78；真实导入两次均完成 compile/plan/startup window 正确性门。未触碰音频、`dist/`、commit 或 push。

## 8. 最终编辑、WPF 与回归门（2026-08-27）

- 真实 `9KX2 18 Million Notes.mid`、最大 Segment 1,824,636 Note、60,000 Note 选择的完整 Desktop 路径：Resize `908.6 ms`、Move `1,501.9 ms`、首次 Undo `713.8 ms`、Redo `723.0 ms`；已有 60,000 个编辑覆盖项时，再创建单 Note 为 `183.4 ms`。这些数值包含正式命令、History、后台编译失效、Workspace 合并刷新和 Selection 发布，不是只测 Domain 集合。
- 本轮曾发现并拒绝过一个“测试通过但性能失败”的中间状态：同步跨 Note/Velocity/Event 投影解析 60,000 个 Selection ID，使完整编辑路径退化到 `6.5～11.3 s`。最终实现以前台 O(1) 发布持久 Selection ID root、100 ms 防抖后的后台预取/指标汇总取代该路径；结果按 selection revision、presentation scope、request generation 校验后才发布。后台指标到达时，正在进行的拖动预览会从 anchor-only 安全升级为完整选区预览，不以删减功能换性能。
- Pure MIDI 分页源 ID 解析由 live collection 与不可变 query snapshot 共享只读 source match cache；缓存只保存不可变 source index/raw value，overlay replacement/removal/exclusion 仍按各自 revision 独立判定。Note、Channel Event、Opaque Event 均覆盖；切换 source 时重建 cache。
- 冷指纹准备不再使用进程级单槽队列；每个 `TimelineSurface` 拥有独立有界单槽，关闭或失效 Workspace 的非协作式慢 source 不会形成跨 Tab 队头阻塞。大批量 Direct Note 精确碰撞查询也由逐 key AVL 查询改成自适应批量有序遍历。
- 2026-08-27 当前实现的真实 `9KX2` 60,000 Note 数据门：Duplicate prepare/apply/first Undo/warm Undo 为 `357/611/289/145 ms`（60,000 请求，52,141 实际创建）；Resize prepare/apply/Undo 为 `224/233/128 ms`；Move prepare/apply/Undo 为 `72/487/194 ms`。首次 Undo 不再出现 8 秒冷路径。
- 同一轮 compiler revision 门：合成 60,000 Pure MIDI overlay 的 mutation-gate capture 为 `0.39 ms / 6,528 bytes`；只读 compilation mirror 建立为 `134.51 ms / 30,075,008 bytes`；一百万 formal-root 引用捕获为 `0.000 ms / 40 bytes`。真实 9KX2 先编辑 60,000 Note 后，capture / mirror / full compile 为 `8.53 / 282.84 / 527.56 ms`；同样本 pristine full compile 为 `48.79 ms`。正式编译不再重建 60,000 个领域对象。
- 真实 WPF 右侧空白边界连续 pan/zoom：cold convergence `98.0 ms`；测试外层软件位图合成/Dispatcher p50/p95/max `42.85/50.73/73.30 ms`，其中 `OnRender` content p50/p95/max `0.33/0.87/3.46 ms`、total `2.37/5.20/6.74 ms`。测试反复让 Segment 右侧空白进入/离开视区，未出现约 0.7 秒同步摘要重建。
- 冷 WPF 前台帧：Arrangement initial/new-region/zoom 为 `27.1/15.1/18.3 ms`，Piano 为 `21.0/21.1/15.1 ms`；Piano `OnRender` content 分别为 `3.56/1.87/2.33 ms`。同轮内存探针：导入后 managed `130 MiB`、GC heap `318 MiB`、private `407 MiB`、working set `451 MiB`；Arrangement rebuild `111.0 ms`、Segment rebuild `0.5 ms`；冻结 Piano tile + `RenderTargetBitmap` 后 managed `161 MiB`、heap `320 MiB`、private `413 MiB`、working set `454 MiB`、completed raster `4 MiB/15 entries`。未观察到此前 2～11 GiB 的手势线性增长。
- 视觉正确性门固定：低缩放聚合只有在完整节点落入同一 device column 时才可摘要，否则下钻；详细与聚合、基础与选择共用同一 half-up 列投影。Piano Note/Selection、Velocity、Event Point 各自保留同投影的上一完整视觉单元，替代单元准备完毕后原子提交；不同缩放、LOD 或 DPI 的像素不得复用。少量 Note Move/Resize 使用当前 viewport 的即时矢量预览；大选区 Resize 不复用上一 delta/selection/DPI 的手势预览。hit-test 查询采用事务式暂存，Pending、异常或重入替换 Snapshot 不得修改调用者列表。
- 当前自动回归：Core solution `1,104/1,104`、Desktop `161/161`、Presentation `232/232`；真实 9KX2 大选区编辑与 edited compile `3/3`、WPF 连续边界与冷帧/内存 `3/3`、真实导入/编译 `1/1`。本轮未使用 computer-use，仍需产品所有者进行实际鼠标操作和视觉验收。
