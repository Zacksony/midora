# 阶段 5 验证与性能记录

日期：2026-09-06。状态：本轮自动验证完成，用户已确认功能验收通过。环境：Windows x64、.NET 10.0.11、SDK 10.0.400、Release。未使用 computer-use，未生成 `dist`。

## 正确性与集成

- Compiler 最终全套：406 通过。
- Presentation 最终全套：339 通过。
- Application 最终全套：961 通过（3 分 14 秒），包括后续的转换定位/Redo 分支、六类生成结果 Format 3 往返以及自适应排序缓冲用例。
- Common 最终全套：81 通过，含临时 lease 创建不扫其它目录、显式清理及并发安全回归。
- Desktop 最终全套：317 通过（1 分 55 秒），包含实际共享 WPF Application 下的生成器/Help/Preset BAML 初始化、draft 应用与窗口资源加载。
- 六类生成目标分别经过实际 Desktop command factory → detached preparation → 压缩结果选择投影 → 发布 → Undo/Redo。取消保持旧内容与选择；全部 Note 候选被碰撞丢弃时不生成空历史记录。空生成结果仍校验来源修订。
- Segment 转换覆盖 hidden 内容、空 Lane、各种非共同字段、精确重复音符、混合 Track、取消、预算失败、过期计划与一次 Undo/Redo。

这些测试不等于人工鼠标/视觉验收，也不包含本轮未修改的真实音频设备链回归。

确定性以六类小样本的精确字段/正式顺序断言、Format 3 保存重开及 Full/Incremental 比对验证。下面的大规模样本为每规模、每 owner 单次生成加 Undo/Redo，不代表已经做过千万候选的独立重复运行逐条对比；也没有用这些合成空 owner 基准声称覆盖所有真实大型工程的混合历史与渲染负载。

一次全套并行运行出现了既有跨进程启动测试的 30 秒 deadline 超时（960 通过、1 失败）。该测试组单独重跑 14 项全部通过；新增高 I/O/大排序测试加入非并行 scalability collection 后，完整 961 项全部通过。未修改 IPC 产品逻辑、测试 deadline 或断言，也未排除该测试。先前为定位多小 Segment 热点而主动中断的两次专项不计作通过。

771 个小 Segment 的整场转换/源顺序/Undo/Redo 回归耗时 23.91 秒；这是带 fixture、验证、释放的测试总时长，不是单独发布耗时。1,000,000 条逆序排序回归约 0.63 秒，1,000 次小 owner 排序约 0.011 秒；分配和资源释放断言均通过。另以 150,000 条同键记录对比旧 run 分割/归并顺序，输出精确一致。

Desktop 复跑期间，旧选区测试也暴露了等待条件缺陷：仅以“位图不同于 baseline”判断完成，会把中间帧变化当作正式高亮完成，在 selection tile 尚未提交时提前退出。测试改为同时等待 committed selection tile；原 5 秒 deadline 与高亮断言不变。另一个百万选区初始索引测试在全套中触及 10 秒 deadline，单独测试通过；其旧 materialized-selection 路径未被本轮修改。共享 WPF/栅格测试 collection 现隔离于其它 collection，避免进程级 worker/缓存与时限测试争用。最终重新构建、完整运行 317 项全部通过，未排除这两个用例，也未为此改动产品渲染代码。

## 生成基准（首轮记录，最终复测另列）

独立测试进程依次测 Direct MIDI、Logical Segment、SubVoice；每种创建一个空 owner，表达式 `Tick = i*2`、`Key = i%128`、`Velocity = 64+i%64`，无碰撞。计时包含 `ProjectDocumentSession.PrepareEdit` 的分页准备、历史资源冻结与 flush，以及正式发布/Undo/Redo。后台编译使用长 debounce 隔离，不把并行编译掺入编辑计时；本表尚不含 Desktop 选择投影和实际绘制。

| 候选数 | 目标 | 准备 s | 发布 ms | Undo ms | Redo ms | working 峰值 MiB | resident 峰值 MiB | owned spill 峰值 MiB | 完成时进程 WS MiB |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 100,000 | Direct MIDI | 3.372 | 6.604 | 0.989 | 0.457 | 6.38 | 52.64 | 36.62 | 230.14 |
| 100,000 | Logical | 1.450 | 0.102 | 0.041 | 0.007 | 2.81 | 15.26 | 9.92 | 229.54 |
| 100,000 | SubVoice | 1.860 | 0.610 | 0.170 | 0.033 | 9.16 | 64.00 | 18.88 | 186.30 |
| 1,000,000 | Direct MIDI | 20.205 | 6.573 | 0.771 | 0.332 | 6.38 | 64.00 | 714.24 | 431.25 |
| 1,000,000 | Logical | 6.650 | 0.086 | 0.023 | 0.007 | 2.81 | 63.99 | 111.49 | 343.48 |
| 1,000,000 | SubVoice | 9.736 | 0.581 | 0.162 | 0.017 | 9.16 | 64.00 | 759.97 | 335.88 |
| 10,000,000 | Direct MIDI | 221.854 | 6.537 | 0.875 | 0.372 | 6.38 | 64.00 | 7718.02 | 376.39 |
| 10,000,000 | Logical | 87.506 | 0.093 | 0.036 | 0.008 | 2.81 | 64.00 | 1690.77 | 560.84 |
| 10,000,000 | SubVoice | 134.264 | 0.743 | 0.168 | 0.017 | 9.16 | 64.00 | 8175.75 | 515.36 |

`working` / `resident` / `spill` 是有限内存事务账本的分类峰值，不是整个进程的内存上限；进程还包含运行时、查询/页缓存、GC 高水位等。累计 allocated 也不是同时驻留量，例如百万 Direct 累计分配约 1,992 MiB、完成时 WS 约 431 MiB，不能混为同一个指标。以上是一次实测而非延迟保证。

## 生成核心最终代码复测

下表为自适应排序缓冲与临时目录扫描修复之后的 Release 结果。最终机器运行条件下绝对时间长于首轮；没有在等同系统负载/文件缓存条件下做受控 A/B，因此**不据两轮差值声称加速或准确量化回退**。交付的等待时间预期应优先看本表与 Desktop 表。源码上消除了固定小输入大数组及每 lease 全目录扫描，其正确性由专项测试验证。

| 候选数 | 目标 | prepare s | publish ms | Undo ms | Redo ms | working MiB | resident MiB | spill MiB | 完成 WS MiB |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 100,000 | Direct MIDI | 12.564 | 26.517 | 2.577 | 0.713 | 6.47 | 52.64 | 36.62 | 250.74 |
| 100,000 | Logical | 3.872 | 0.161 | 0.073 | 0.010 | 3.91 | 15.26 | 9.92 | 296.20 |
| 100,000 | SubVoice | 5.270 | 10.261 | 0.246 | 0.064 | 12.44 | 64.00 | 18.88 | 331.02 |
| 1,000,000 | Direct MIDI | 61.085 | 10.085 | 1.323 | 0.564 | 6.48 | 64.00 | 714.24 | 423.89 |
| 1,000,000 | Logical | 18.824 | 0.103 | 0.031 | 0.007 | 3.91 | 63.99 | 111.49 | 381.38 |
| 1,000,000 | SubVoice | 31.319 | 1.309 | 0.289 | 0.025 | 12.44 | 64.00 | 759.97 | 313.51 |
| 10,000,000 | Direct MIDI | 559.627 | 41.283 | 1.479 | 3.289 | 6.54 | 64.00 | 7718.02 | 321.64 |
| 10,000,000 | Logical | 153.575 | 0.098 | 1.055 | 0.013 | 3.91 | 64.00 | 1690.77 | 554.12 |
| 10,000,000 | SubVoice | 308.686 | 1.081 | 0.262 | 0.026 | 12.44 | 64.00 | 8175.75 | 689.12 |

最终千万级三类合计约 17 分钟，TRX `2026-09-06_21_18_37`。发布前源数量为 0、发布/Redo 后为 10,000,000、Undo 后为 0，结果选择数量与三项资源预算断言全部通过。千万 SubVoice 累计分配约 19,509 MiB，但完成时 managed 约 329 MiB、WS 约 689 MiB；累计分配不是同时占用。千万候选仍是分钟级可取消任务，不能称为即时生成。

## 百万事件点最终代码复测

三类事件分别在空数值 Lane 生成一百万点，`Value = i%128`、`Tick = i*2`；包含正式准备、发布和 Undo/Redo。每种核对发布前数量 0、发布/Redo 后数量 1,000,000、Undo 后数量 0、结果选择数量和全部事务资源预算。

| 候选数 | 目标 | prepare s | publish ms | Undo ms | Redo ms | working MiB | resident MiB | spill MiB | 完成 WS MiB |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1,000,000 | Direct MIDI Event | 51.897 | 31.300 | 1.823 | 2.889 | 4.84 | 64.00 | 389.96 | 484.66 |
| 1,000,000 | Logical Parameter | 20.321 | 0.269 | 0.117 | 0.020 | 8.00 | 64.00 | 347.99 | 468.98 |
| 1,000,000 | SubVoice Event | 28.801 | 0.863 | 0.154 | 0.021 | 12.44 | 64.00 | 759.97 | 338.98 |

全部通过，最终 TRX `2026-09-06_21_20_30`。该百万基准覆盖普通数值 Lane；复合 Bank/RPN、稀疏 Enum、碰撞、非单调 Tick 和运行错误由小规模专项验证，不以此表声称逐类型百万压力覆盖。

## Desktop 生成与结果选择复测

运行实际 `DesktopSessionController` 创建项目/Workspace，使用正式 command factory、`PrepareProjectEdit`、压缩结果选择和正式发布/Undo/Redo；不是完整显示窗口和绘制测量，也未声称其包含真实音频链。每个规模独立测试进程，表中进程峰值包含该进程 fixture 初始化。

| Direct MIDI 候选数 | 准备+选择投影 s | 发布 ms | Undo ms | Redo ms | 完成 managed MiB | 完成 WS MiB | 进程峰值 WS MiB |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100,000 | 14.711 | 89.09 | 10.99 | 12.06 | 101.7 | 245.0 | 256.6 |
| 1,000,000 | 67.965 | 31.54 | 9.88 | 11.72 | 280.3 | 478.6 | 482.3 |

验证结果数量、正式选择类型和 Undo/Redo 选择恢复均通过。这条路径包含桌面会话处理，不与上面的隔离核心准备计时混用；百万生成仍是可取消的长任务，不宣称瞬时完成。

## Segment 双向转换最终基准

同一测试进程顺序执行四个样本。源使用不保留每音符对象的不可变分页值；后台编译先在空 Project 建立并长 debounce 隔离。完整遍历核对转换后所有 Note 字段、选中结果、源删除、Undo/Redo 和预算。`review` 包含冻结剪贴板及丢失分析，`prepare` 包含 detached 转换和 flush；不含确认窗口等待和实际绘制。

| 音符数 | 方向 | review s | prepare s | 发布 ms | Undo ms | Redo ms | working MiB | resident MiB | spill MiB | 完成 WS MiB |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 100,000 | Logical→MIDI | 0.696 | 5.073 | 11.487 | 1.525 | 0.541 | 6.74 | 37.38 | 33.57 | 158.49 |
| 100,000 | MIDI→Logical | 0.774 | 2.287 | 0.125 | 0.062 | 0.016 | 6.24 | 15.26 | 9.92 | 115.66 |
| 1,000,000 | Logical→MIDI | 1.196 | 20.004 | 0.067 | 0.070 | 0.072 | 6.74 | 63.99 | 587.38 | 312.95 |
| 1,000,000 | MIDI→Logical | 1.481 | 6.407 | 0.155 | 0.056 | 0.012 | 6.24 | 63.96 | 157.43 | 327.00 |

四个样本共用进程的最高 Working Set 为 344.11 MiB；该数不等于整个 Midora 带大型 Project/渲染/历史记录时的内存上限。

## 交付边界

- 本轮声明的自动验证和专项基准均已完成；没有待运行的测试进程。
- 视觉样式、实际鼠标操作手感由 18 项人工清单提供验收依据；用户已明确确认本阶段验收通过。该确认不替代报告中未测范围的说明。
- 没有改动 Project Format、软件版本或音频消费者语义；没有本地发布，也没有进入阶段 6～8。
- 已验收的前一轮修复提交 `3b6b0ee` 已推送。用户现已授权提交、推送阶段 5 成果并进入阶段 6；阶段 5 不单独发布。

## 重现方式

`TimelineGenerationPerformanceTests.MeasureOptInBoundedGeneration`：设置 `MIDORA_GENERATOR_BENCHMARK_COUNT=100000/1000000/10000000`，`MIDORA_GENERATOR_BENCHMARK_KINDS=direct,logical,template`，运行 Application.Tests，使用 console detailed logger 获取实测输出。

`TimelineGenerationIntegrationTests.OptInLargeGenerationIncludesDesktopProjectionPublicationAndHistory`：设置 `MIDORA_GENERATOR_DESKTOP_BENCHMARK_COUNT` 后运行 Desktop.Tests 对应用例。默认未设置环境变量时只跳过基准 body；默认测试通过不能作为性能证据。

事件点使用同一个 `TimelineGenerationPerformanceTests`，设 `MIDORA_GENERATOR_BENCHMARK_COUNT=1000000`、`MIDORA_GENERATOR_BENCHMARK_KINDS=direct-event,logical-event,template-event`。每类使用 `Value = i%128`、`Tick = i*2`，目标为有效的 CC11 或整数逻辑参数 Lane。

`SegmentConversionPerformanceTests.MeasureOptInLargeCrossTypeSegmentTransfers`：设置 `MIDORA_SEGMENT_CONVERSION_BENCHMARK_COUNT=all`，顺序运行十万、百万双向转换（也可直接填一个数量）；详细输出中保留 review 与 prepare 分段计时，不能将二者混写为单独发布耗时。

所有最终 TRX 位于工作区忽略目录 `.tmp/stage5-final-tests/`。本报告保留可重现命令入口和实测表；这些日志不是产品数据，也不随发布包分发。
