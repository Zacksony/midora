# 阶段 6：Conductor 验证记录

日期：2026-09-06。实现基于已推送的阶段 5 提交 `593dcdd`，未执行本地发布或 computer-use。

## 测量边界

- Windows x64；.NET SDK 10.0.400、.NET 10.0.11；逐项目 Release 构建。
- 功能测试覆盖正式 Domain → Application → Compiler/持久化/消费者，以及 Presentation 与实际 WPF BAML 实例化、布局、离屏渲染；未做桌面鼠标自动化，人工视觉/交互验收另列清单。
- 本轮大规模样本是专门构造的 Conductor 数据，不是把 9KX2 的数千万音符当作百万 Tempo。编辑、分页、栅格与虚拟列表分别测量，不混用彼此结果。
- 性能测试期间有其他测试负载，并非隔离实验；数值是当前机器观测，不是用户硬件承诺。

## 自动回归

最终逐项目记录保存在工作区 `.tmp/stage6-validation/`；该目录不提交到 Git。可重建测试源码保留在各测试项目。

| 项目 | 结果 | 记录 |
| --- | --- | --- |
| Application | 997/997 | `stage6-application-full-encoder-fix.trx` |
| Compiler | 406/406 | `stage6-compiler-final.trx` |
| Persistence | 89/89 | `stage6-persistence-final.trx` |
| MIDI Export | 41/41 | `stage6-midiexport-final.trx` |
| Playback | 114/114 | `stage6-playback-final.trx` |
| Audio Render | 37/37 | `stage6-audiorender-final.trx` |
| Desktop | 335/335 | `stage6-desktop-release-final.trx` |
| Presentation | 372/372 | `stage6-presentation-release-final.trx` |

合计 2391/2391，最终构建 0 warning / 0 error。Desktop 最后收尾包括 Meta 播放线、横轴对齐、列表滚动恢复、新 Axis 取消旧 Fit，以及未知总数任务的 processed 文本。

以上全套计数包含默认关闭的 opt-in 性能测试入口；百万规模是否实际执行以以下专门记录为准，不能由全套“通过”推断。重复运行的定向测试不叠加到独立测试总数。

覆盖重点：tick 0 必需状态、同 tick 碰撞、同 tick 多 Marker、Properties、复制/移动/删除、root Undo/Redo、取消与错误零发布、稳定 ID、大小预算、前驱恢复、Full/Incremental 等价、Project wire 往返；冷命中连续点击、捕获/视口/轴/修订改变、列表源更换、可见行选择与首屏读取。

## 真实批量命令基准

`ConductorEditPerformanceTests` 在 `MIDORA_CONDUCTOR_EDIT_BENCHMARK=1` 下运行，10,000 / 1,000,000 两例实际执行并通过。测试从空 Project 执行真正 Draw 准备与发布，再做 Move、单点修改、取消、Undo/Redo、局部正式编译，不使用“预先生成结果根再测 swap”的替代路径。

| 场景 | 10,000 状态 | 1,000,000 状态 |
| --- | ---: | ---: |
| Draw 准备（含同 tick 候选覆盖） | 0.039 s | 6.930 s |
| Move 准备 | 0.100 s | 13.641 s |
| 单点修改准备 | 0.0150 s | 0.1028 s |

百万例有 1,000,977 个输入候选，最终保留 1,000,000 个状态。Draw 正式发布 1.044 ms、Undo 0.347 ms、Redo 0.016 ms；Move 正式发布 0.027 ms、Undo 0.031 ms、Redo 0.002 ms。这里的发布/历史时间只计正式数据命令，不包含随后 UI、后台编译及渲染刷新。

在第 100,000 个候选取消的测试约 0.063 s 结束，断言正式根、稳定 ID 分配均未改变。局部 Full/Incremental 指纹与事件序列一致，并检查范围起点的前驱恢复。

资源观测：受预算 working 峰值 7.04 MiB、resident 63.99 MiB、spill 412.84 MiB；测试结束托管堆 128.38 MiB、Working Set 477.11 MiB、进程历史 Peak Working Set 533.26 MiB。整个脚本累计托管分配 **7663.05 MiB**，不是同时驻留内存，但说明准备阶段仍有较高分配/GC 成本，不能声称已消除。

同轮优化前 Move 21.835 s，改用 dense 归并和有界 ordinal 后观测为 13.641 s（约降低 37.5%）；并发负载存在，不能将两轮 Working Set、Draw 的小幅差异归因于单项优化。剩余分配来源尚未用 profiler 精确分摊。

该基准最终使用 console logger，没有生成 TRX；已将原始观测、命令和限制归档到 `.tmp/stage6-validation/stage6-conductor-edit-performance-observed.md`，此处保存核心结果供长期追踪。

## 栅格性能

`MIDORA_CONDUCTOR_RENDER_PERF=1`，`ConductorRenderingPerformanceTests` 实际执行 1/1 通过。采用真实分页 Domain 源各 100 万 Tempo / Marker；不含源构造、索引创建、磁盘冷读取或主窗口首次显示时间。

| 后台单瓦片 | 时间 | 累计托管分配 | 栅格 |
| --- | ---: | ---: | ---: |
| 整曲压入一个 Tempo tile，100 万候选 | 214.95 ms | 9,130,968 B | 291,600 B |
| 整曲压入一个 Marker tile，100 万候选 | 203.70 ms | 8,859,632 B | 62,640 B |

局部水平比例 0.001 / 0.01 / 0.1 时，Tempo 分别 29.92 / 4.21 / 1.66 ms（270k / 27k / 2700 候选）；Meta 分别 76.83 / 8.71 / 2.49 ms（384k / 38,400 / 3840 候选，包含稳定标签 cell 的邻接范围）。这些是后台计算时间，不是 UI frame time。最终专门性能运行只有 console 输出，无独立 TRX。

## 虚拟列表与冻结根

- 百万行虚拟列表探针保留 3907 个稀疏归并锚点；一次观测目录准备 125.66 ms，远处 40 行读取 0.42 ms、141 次源访问。该测试是计算型合成 reader，不包含磁盘延迟。
- 首屏额外用阻塞后台目录的测试验证：目录未完成时仍可返回首个 256 行窗口内的可见行。并非依赖目录“恰好很快”才显示首屏。
- Domain 的四类百万事件不可变源探针：冻结 snapshot 约 1824 B、0.010～0.026 ms、零源页读取；局部单点删除仅读一页，root Undo/Redo 均小于 0.02 ms。记录位于 `.tmp/CompilerRuns/stage6-conductor-benchmark/conductor-paged-content-peak.trx`。它不等于真实编辑完整准备成本，后者以前表为准。

## 全回归发现的既有导入竞态

一次 Application 全套出现 `PureMidiContentPackWriter.CommitOldestPage` 的 `TaskSchedulerException`，内层为已释放的 `ConcurrentExclusiveSchedulerPair` ThreadLocal。源码此前先 `Complete()` 调度器，再同步等待 pending task；等待的 inline 尝试与调度器收尾释放存在竞态。

现改为：按原确定顺序观察/drain 全部 pending 结果及释放缓冲，然后结束调度器。成功、未提交 Dispose、编码失败、取消均覆盖；不重试、不吞错、不改变编解码格式或并发预算。定向 writer/import 20/20，全套 Application 997/997；四个新增测试明确验证生命周期定序。

## 保留限制与人工验收

1. 百万点 Move 准备仍约 13.6 s；任务可取消，但不是瞬时完成。
2. 拍号派生小节前缀仍为 O(N) 数组；弱缓存与单槽 warm handoff 避免每条历史永久 pin 一份，但并非彻底有界的所有派生数据。
3. 保存 JSON DTO 与完整 canonical 的既有数组未在阶段 6 重构。编辑预算上限不代表整个 Midora 进程上限。
4. 列表目录每修订仍后台 O(N) 重建，首屏不等待；还不是局部增量目录。
5. WPF 实际屏幕观感、连续鼠标操作与真实音频听感未由这些探针代替。请按 `Midora-Stage6-Conductor-Acceptance-Checklist.md` 逐项验收。

阶段 7/8、版本升级、dist 发布不属于本轮。

## 功能验收后的六项 UI 调整（2026-09-06）

本轮没有改动正式数据、编译器、音频、密集点聚合或瓦片缓存算法。产品所有者已确认阶段 6 功能本身通过，后续只复核六项 UI 调整。

根因与修复：

- Meta 头原宽 130 DIP，文字裁剪又误扣 Arrangement 操作按钮的 52 DIP 留白。编辑器两图统一为 150 DIP；Meta 不再为不存在的按钮留空。Tempo 移除 78 DIP 外侧 margin，Surface、播放 overlay 和相同 tick 的原点一起对齐。其他视图的默认头宽不变。
- 普通 Tempo 点线及 Arrangement Tempo 源颜色使用既有主题绿色 `#58C487`，选择红色保持不变。
- Reset 同时还原工作区 BPM 轴和 Surface 内部纵向缩放/滚动，保留 StartTick、TickSpan、选择及 Project 值；取消尚未完成的旧 Fit，不允许回写旧范围。
- Tempo 借用 EventLanes 渲染器，但绑定 Conductor 主 EditorSettings。旧快捷键按渲染器类型误选 Segment 的 LaneEditorSettings；现在按工作区区分，Conductor 两图的 A 与工具栏共用 Snap。
- 密集点 cache-only 命中超过 256 候选后后台精确查询，完成时旧代码只重绘、不刷新 hover Cursor。无待重放点击的纯悬停因此保留 Wait。现在完成后走统一 hover 刷新；仍保留查询预算、后台执行、取消与修订检查。

验证：

| 测试 | 结果 | 记录（`.tmp/stage6-validation/`） |
| --- | --- | --- |
| Conductor Workspace 定向测试 | 11/11 | `conductor-ui-desktop-targeted.trx` |
| Conductor 交互/投影定向测试 | 36/36 | `conductor-ui-presentation-targeted.trx` |
| Desktop 全套 | 338/338 | `conductor-ui-desktop-full.trx` |
| Presentation 全套，启用百万渲染探针 | 376/376 | `conductor-ui-presentation-full.trx` |
| 单独运行百万渲染探针 | 1/1 | `conductor-ui-render-performance.trx` |

独立用例总数 714，定向重复运行不累加。两项目 Release 构建均为 0 warning / 0 error，`git diff --check` 通过。新增用例覆盖：实际 BAML 横轴/播放线对齐；Reset 在默认/自定义轴及纵向滚轮缩放后保持横轴、选择与音乐值；两图 Snap 数据绑定及普通 Segment/SubVoice 的独立设置；100 ticks/pixel、同点列不同 y 的后台 hover 恢复；实际离屏像素验证普通点与保持线为绿色，Arrangement 投影颜色相同。

单独渲染探针：100 万 Tempo 整曲 tile 220.05 ms、9,130,968 B 累计分配、291,600 B 栅格；100 万 Marker tile 164.60 ms、8,859,632 B 累计分配、62,640 B 栅格。局部尺度 0.001 / 0.01 / 0.1 的 Tempo 为 20.21 / 2.53 / 0.99 ms。和前次 Tempo 214.95 ms 属同一量级；非隔离硬件基准，不把小幅差异宣称为性能提升。与全套并行负载时观察到约 432 ms，不能混用为稳定帧耗时。

未使用 computer-use，未提交、推送或发布。实际屏幕样式及连续操作手感请按阶段 6 验收清单的 UI-01～06 复核。

2026-09-07：用户最终验收通过并授权提交、推送阶段 6，随后实施阶段 7；无本地发布授权。
