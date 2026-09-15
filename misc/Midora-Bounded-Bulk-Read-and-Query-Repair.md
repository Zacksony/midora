# 有界批量编辑：读取、查询与进度修复

状态：2026-09-06，依据上一轮只读调查完成实施及自动化验证。不改变 SRS 音乐语义、持久化格式或用户操作规则。

## Requirement trace

- 依据：SRS §18.2.4～18.2.7、§20.3.15、§20.4.11、§20.4.14～18、§20.6；INV-020、065、069、077、078、095、096。
- 输入：冻结的 Project/owner source revision、正式稳定 ID 选区及既有工具参数。
- 输出：与原命令一致的音乐数据、正式顺序、Same/Mixed 属性、选择范围、剪贴板、Undo/Redo；查询结果必须逐值等价。
- 边界：继续执行每命令 working/resident 各 64 MiB、页 4,096 records、每 256 records 取消检查及 owned spill 上限。不得用全量对象数组或无界 ID 字典换取速度。
- 失败：过期修订、未知 ID、取消、I/O 或预算错误不得发布 Project/Clipboard/Selection 半成品。渲染冷页是 Pending，不是空结果或同步磁盘读取。
- 归属：读取索引、分页排除掩码、进度与统计只属于运行时；不写入 `.midora`、canonical 或音频缓存身份。
- 非目标：不修改已验收的瓦片绘制/概览层、音频、碰撞归并规则、随机序列、选区恢复及存储预算。

## ADR-BULK-003：共享有界选择读取

Copy/Cut、Scale 和 Properties 不再分别逐 ID 扫描 ContentPack。共享 reader 在冻结 source 上解析 `{formal ordinal, value}`，Direct 使用批量 ID 查询；其余分页源通过有界 ordinal 索引及页序读取。需要正式序时使用有界外排，不要求完整选区常驻。属性校验与全部 Same/Mixed 字段在一次值流中汇总；完整且身份一致的选择摘要才可复用。

Clipboard 捕获必须保持原有 formal order 和各 payload 的指定排序。读操作不得把原本一次取消事务拆成可观察的部分结果。

## ADR-BULK-004：保留稀疏补丁的缓存查询

Humanize/Quantize 可以形成稀疏修改与删除，不能为了加速查询而改写未变化音符。缓存专用查询必须获取与普通查询等价的 ordinal 排除掩码；所需索引页缺失时返回 Pending，并由后台预取，禁止退化成对整个候选音符页逐 ID 二分。

删除后的 live ordinal 定位使用紧凑删除 rank/select 元数据，避免每个值再次在整个物理 ordinal 域上嵌套二分。空间查询以页为单位访问缓存，不对同一页的每条记录重复获取缓存锁。新增目录/位图须计入既有资源预算并由不可变 root/history 管理生命周期。

具体实现：删除 ordinal 是可落盘的 4-byte 整数页，复用共享有界读取缓存；只保留按 leaf 规模的目录。准备中的四个目录数组共同计入事务 working admission，直到取消/发布时释放 admission；发布后的不可变目录仍由 source/history 持有。这里限制的是准备工作集与分页缓存，不声称整个进程、全部历史元数据及 WPF 原生资源合计只有 64 MiB。未额外加入完整 changed-ordinal 常驻数组，也未改写未变化音符以人为消除稀疏补丁。

## ADR-BULK-005：进度表示实际阶段工作

读取、排序、碰撞处理、建索引与最终落盘分别计量，不再将 Properties 的总工作猜成 `N × 16`。父任务显式分配子任务区间，Copy 与 Cut 删除准备不能重用一条从零开始的进度。阶段有可靠 total 才显示确定进度；未知工作量明确使用不确定状态。进度节流只减少 UI 通知，不跳过取消检查。Ready 只在全部准备和最终落盘完成后报告，不通过定时假进度或额外等待伪造平滑。

## 验证计划

1. 原始 ContentPack、编辑后稀疏 root、Logical/SubVoice/Curve：批量读取、顺序、未知/重复 ID、取消及预算测试。
2. Properties 所有 Same/Mixed 字段单遍汇总；Scale 摘要仅在修订/owner/selection 匹配时复用。
3. 稀疏 Velocity 改动、量化碰撞删除、冷页、热页、Undo/Redo：普通与 cached-only 查询逐值一致，冷页不在 UI 线程 I/O。
4. 阶段进度真实 total、单调父进度、完成/取消及 final spill。
5. `9KX2 18 Million Notes.mid`、`MIDI Out #23`、400,000 音符进行实测；分别报告读取、Copy、准备、查询，不把查询耗时冒充完整 WPF 帧时间。

## 实测结果（2026-09-06）

本机 Release / .NET 10.0.11，原始样本只读导入。目标 Track 原有 1,824,636 音符；在 tick `[168816, 193536)`、key 0～127 的查询结果中取前 400,000 个，ID 总和为 6,601,035,800,000，最早 start 168960，最晚 end 182306。读取、复制及查询的性能测量互不并行，不启动音频 Worker，不使用 computer-use，不覆盖用户样本。

| 路径 | 上一轮基线 | 修复后 |
|---|---:|---:|
| 选择范围读取，尚无可复用摘要 | 90.882 s（逐 ID） | 0.814 s（有界批 ID），1.253 s（密集内存选区流） |
| 实际 Copy 准备 | 78.173 s | 1.438 / 1.414 s |
| 实际 Properties 全部五字段读取 | 未单独计时，不估算倍数 | 首次 1.526 s，再次 1.437 s |
| Desktop 选择范围/字段摘要读取 | — | 1.507 s |
| Humanize 后十个窗口的热缓存查询 | 594.4 ms | 24.4～30.2 ms |
| Humanize 后 GetNote × 2,000 | 66.2 ms | 约 4.38 ms |

- Cut 包括 Copy 与删除准备，共 9.119 s；正式发布 58.9 ms，Undo 37.2 ms；剩余 1,424,636 个音符，Undo 恢复原 Segment root。该时间包含正式 Document 路径，不能与下述数据 root 探针的亚毫秒 Undo 混为同一指标。
- 稀疏补丁复测使用 Velocity 加法 [-1, 1]、seed 123。结果仍为 272,693 个变更、16,719 个删除，与基线一致。这里的删除继续遵循既有编辑碰撞规则，并非为了加速查询额外删除。
- 此项 Humanize 准备耗时为 41.37～42.46 s，基线约 37.00 s：本轮没有使该操作的准备更快，不把查询提速描述成整个编辑提速。
- 十窗口均为 `[168960 + i×64, 169216 + i×64)`、key 52～76，共 17,928 个命中。正式查询与 cached-only 查询完整字段相等；主动逐出后，冷查询在约 3～4 ms 原子返回 Pending 且不追加结果，预取后完成。normal/selection-exclusion 交替三轮全部 ready，没有反复 Pending；排除查询 47.5～54.0 ms，零命中与原始选区覆盖范围一致。
- Humanize 准备的计量峰值 working 约 7.97 MiB、resident staging 不超过 64 MiB；完成 publication preparation 后两者计量均回到 0。不可变 root 页随后由共享有界读取缓存、spill 与 Undo/Redo 生命周期承载，而不是仍留在 staging。
- Copy/Cut/Undo 隔离进程末尾 managed 378.81 MiB、Working Set 630.69 MiB。Properties 首次/再次 managed 分别约 357.32 / 225.25 MiB；累计 allocated 459.21 / 519.93 MiB 是流式页读与回收期间累计分配，**不是同时存活内存或峰值**。这些数据不能代替真实 WPF 主程序的总内存验收。
- 上述“首次读取”发生于导入和选区查询之后，不能称为操作系统/文件缓存绝对冷态。没有把数据查询时间或后台读取时间标注为 GUI 帧率、弹窗全链延迟。

## 自动验证与验收边界

- Application 全套 850 项通过；随后共享 reader 的 14 项复测通过，其中新增 3 项检查真实 Domain 索引安装前取消、安装后取消及提前结束枚举后的继续读取。
- Desktop 全套 235 项通过；Presentation 全套 336 项通过；Compiler 387 项、Persistence 89 项通过。
- 消费链回归：Playback 114 项、MIDI Export 41 项、Audio Render 37 项通过；本轮未修改音频或导出语义。
- 真实 Properties 本地探针单独启用样本路径，1 项通过。普通回归不自动读取用户的 MIDI。
- 覆盖有界读取顺序、错误/重复身份、Same/Mixed、错误 owner hint 回退、摘要修订失效、连续整页/尾部/全部删除的 rank/select、追加与恢复、预算超限原子性、冷缓存 Pending、最终落盘取消和选择投影完成前不报告 Ready。
- 桌面编译检查中发现并修正了新文件缺少命名空间导入及新测试缺少必填 Name；最终成功运行的回归已包含修正。新增 reader 的早期默认 page 空值问题也已修正并复测，不将中途失败算作最终通过。
- UI 仍需使用者复验：40 万选区的 Copy/Cut、首次与重复 Scale/Properties，Humanize/Quantize 后保持/取消/重建选区并滚动缩放，三种钢琴卷帘的共同读路径，以及各准备阶段 Cancel 后的音乐数据和选区。未进行 computer-use 人工验收，也未发布到 `dist`。
