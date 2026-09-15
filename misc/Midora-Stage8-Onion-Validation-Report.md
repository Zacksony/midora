# 阶段 8：洋葱皮、All Tracks 验证报告

日期：2026-09-07。源码基线：`aa9fd2e` 加当时工作区改动。本报告记录阶段 8 最初实现及验证，不使用 computer-use、不发布 `dist`。2026-09-09 状态收尾：阶段 8 与后续细节均已验收并提交、推送；精细体验留到真实完整编曲，不再列为待整体验收。

**2026-09-08 补充**：用户已验收阶段 8 主功能，并更正 Compiled 为“逻辑展开 + MIDI 源音符”的混合显示。本报告保留原实现的历史测量（包括 18M 全流 FIFO 索引 149.28 s），不代表当前混合路径耗时；新测试和当前行为见 [验收修订报告](Midora-Stage8-Onion-Refinement-2026-09-08.md)。

关联：[需求与实现决策](Midora-Stage-8-Onion-Requirement-Trace-and-Decisions.md)、[使用说明](Midora-Stage8-Onion-User-Guide.md)、[一次合并验收清单](Midora-Stage8-Acceptance-Checklist.md)。

## 1. 交付范围

- Logical/MIDI Segment 和 SubVoice 的 Raw 洋葱皮：来源筛选、透明度、时间变换、源暴露范围裁剪、正式顺序与颜色、独立只读层。
- All Tracks 独立只读 Tab：Raw / Compiled、来源颜色、完整 canonical FIFO Note 配对、Current / Stale / Unavailable、后台准备进度、取消及刷新。
- 现有 Format 3 presentation 的显式保存、Save Copy、删除来源后休眠、Undo 恢复、Duplicate 复制及内部 SubVoice 引用重映射。配置不进入音乐 Modified、Undo、编译或音频。
- 独立有界缓存与编译音符索引；没有把洋葱皮塞进正式可编辑音符集合，没有重写普通钢琴卷帘音符样式。

## 2. 验证环境与计量含义

Windows x64，.NET SDK 10.0.400，12 个逻辑处理器，32 GiB RAM，Release 构建。样本为已指定的 I'm So Happy、ATAAI4、dogsong、9KX2；最后一个约 1,800 万音符。测试会受到文件系统热缓存、JIT、GC 和其他进程影响，以下是本机实测，不是跨设备延迟保证。

WPF 探针在独立 STA 进程加载实际 App 资源，再做 Measure / Arrange / RenderTargetBitmap 与 Dispatcher 调度；不创建可见窗口，不通过鼠标键盘控制软件，不开启音频设备。1280 × 800 的完整离屏绘制耗时不是交互 FPS。96/120/144/192 DPI 的图像尺寸检查也不等于真实多显示器人工验收。

`GC.GetTotalMemory(false)` 包含尚未回收对象；累计分配量不是常驻内存；Working Set 不是 GC Heap。不同测试进程的峰值不能相加。

## 3. 8A 专项正确性

新增自动测试覆盖：

- 跨类型来源、Content Offset、绝对/相对 Tick、长音符裁剪、自身排除、SubVoice 模板时间。
- 图层顺序、透明度、同一来源重叠不重复累积透明度、局部缓存指纹、单 Key 行位置、零时长 Note 的最小像素。
- canonical 内存/分页输入顺序；同 Root、跨 Track、同 Key 的 FIFO；冗余 NoteOff、未闭合 NoteOn、硬结束、NoteOn 来源追踪。
- 索引取消、超限、读取 lease 与释放；未完成/过期索引不发布；相同 canonical 不重复建索引；编辑失败保留 Stale；关闭 Tab/Project 清理。
- 无关轨道改动不改变未编辑 compiled track 的局部内容指纹及范围。
- presentation round-trip、损坏隔离、保存期间新配置不丢失、dormant 引用、Logical/MIDI Track / SubVoice / Event Instrument Duplicate 与 Undo/Redo。
- 实际 App 主题解析、嵌入字体、100/200/400 Track 的 WPF 后台完成和 3/9/15/32 DIP Key 高度。

9KX2 中确实存在一个以原始 Channel Event 保留的同 Tick NoteOn/NoteOff：MIDI Out #17、Key 27、Tick 181286。正式结果共有 18,000,000 个 NoteOn，而源音符对象为 17,999,999 个。本轮保留这个零时长配对，绘制最小 1 pixel；不把它改成 1 Tick，不改 canonical / MIDI / 音频。此前丢弃零时长配对的实现已由失败回归用例和真实样本验证更正。

## 4. 8B 完整自动回归

| 测试项目 | 通过数 | 结果文件（相对 `.tmp/test-results/stage8/`） |
| --- | ---: | --- |
| Common | 81 | `Midora.Common.Tests-full.trx` |
| MIDI | 29 | `Midora.Midi.Tests-full.trx` |
| Application | 1,038 | `application-releasegate.trx` |
| Compiler | 437 | `Midora.Compiler.Tests-full.trx` |
| Persistence | 89 | `Midora.Persistence.Tests-full.trx` |
| Playback | 115 | `Midora.Playback.Tests-full.trx` |
| MIDI Export | 43 | `Midora.MidiExport.Tests-full.trx` |
| Audio Render | 37 | `Midora.AudioRender.Tests-full.trx` |
| BASS | 257 | `bass-full.trx` |
| WASAPI | 38 | `wasapi-full.trx` |
| Desktop Presentation | 413 | `presentation-releasegate.trx` |
| Desktop | 378 | `desktop-releasegate-final.trx` |

最终上述 12 个测试项目合计 2,955 项通过、0 失败。期间失败的原始 TRX 留作调查证据，不用它们替代最后通过的结果。

源码构建使用独立 `bin/Stage8` 输出。音频集成测试使用经既有 manifest 校验的 BASS 基线与本轮 Native AOT Worker（仅 `.tmp/stage8-worker`），明确提供 Platinum Grand III 和 i2D4BM-NoFx 两个音色库。没有运行本地发布脚本；也没有把旧测试计数基线的 `Test-NonUIRelease.ps1` 宣称为通过。

上述全套包含通过环境变量显式启用的性能用例；没有启用时这些用例只验证入口返回，不能据此声称运行过大型数据。实际启用的性能/真实文件用例另列如下。

### 全套运行额外暴露并修正的回归点

1. Undo 期间提前到达的 Compilation 通知可能抢先消耗选择恢复书签。现在只有提交后的 Content / History 通知消费书签，增加可控制通知顺序的回归测试。
2. 关闭 Project 与洋葱皮刷新交错时重复读取可空会话，可能解引用已关闭上下文。改为一次捕获上下文并核对身份。
3. 选择上下文 record 自动 `ToString()` 经过计算属性递归，可能 StackOverflow；改成只描述对象类型的安全文本。
4. Conductor 合成输入测试误读宿主物理键盘修饰键。回放路径使用已经冻结的修饰键；正常真实输入行为不变。
5. Conductor Fit 的过期请求检查与结果发布之间存在竞态，能覆盖较新的显式轴范围。两步现在原子完成；扫描和等待不持锁，取消、关闭、重建共用请求门。原有并发用例增强为 128 次交错请求。
6. 字体验证原先只接受 `StaticResource` 文本写法，错误拒绝等价的主题 `DynamicResource`。测试同时接受两者，实际 App 探针仍核验嵌入 Sora；不是允许系统字体回退。

## 5. 洋葱皮与 compiled 索引性能

四个 Raw 查询视窗 `(start, span)`：`(0, 3072)`、`(168816, 24720)`、`(168816, 98880)`、`(50000, 24720)`。下表给出同一样本四个视窗的范围，不把空查询视窗当成密集数据性能。

| 样本 | 导入 | Raw 冷前台完整绘制 | Raw 后台收敛 | 首次 compiled 索引 | 索引进程峰值 WS | 峰值临时索引数据 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000,000 | 4.53 s | 34.8–42.0 ms | 0.10–0.24 s | 9.29 s | 646 MiB | 137 MiB |
| 3,170,668 | 8.66 s | 35.9–42.4 ms | 0.09–0.14 s | 28.41 s | 561 MiB | 468 MiB |
| 7,335,699 | 10.64 s | 34.3–44.6 ms | 0.08–0.16 s | 51.62 s | 705 MiB | 1,103 MiB |
| 17,999,999 源音符 / 18,000,000 compiled | 19.60 s | 34.9–45.0 ms | 0.21–2.91 s | 149.28 s | 937 MiB | 2,731 MiB |

结果：`probes/{1m,3m,7m}-release.txt`、`probes/18m-final-release.txt`。前三份是同轮 Release 测量；最终 18M 另包含零时长配对和 per-track 指纹复核。首次 Compiled 遍历/排序整曲，确实仍有分钟级准备成本；它后台执行且可取消，不是瞬时完成。索引就绪后的固定窗口中，非空示例 479 / 651 个配对查询分别约 0.3 / 0.2 ms。

100/200/400 Track、每轨 100 音符的实际主题 WPF 探针：前台布局约 111.6 / 37.6 / 61.1 ms，后台收敛约 1,176 / 237 / 494 ms。首例包含字体/JIT 冷开销；不能据此认为 200 Track 比 100 Track 天生更快。

最终主题和 Conductor 并发修复构建再次运行不带 MIDI 参数的 App 探针通过：100/200/400 Track 前台约 121.1 / 42.8 / 39.8 ms，收敛约 1,392 / 232 / 448 ms；显式验证嵌入字体与 All Tracks 状态文字前景资源。该复核不重新计入大型索引表。

### 有界实现与调整证据

- 旧原型按垂直瓦片反复解码同一密集页，18M 收敛曾约 45 s；本轮改为每 512 columns 保存全部 128 Keys、每 Key 一行，垂直缩放复用。当前测得最重窗口约 2.91 s。普通可编辑音符瓦片未改成这种表示。
- 旧索引原型调用整曲事件查询，会展开巨型数组，探针峰值曾超过 20 GiB；已改成有界 canonical render-window 流与外部排序。最终同类探针峰值 937 MiB。只用于解释本轮实现取舍，不作为其他机器的加速承诺。
- 索引驻留预算 16 MiB、排序 working 预算 32 MiB、spill 上限 16 GiB；本组峰值分别为 16 / 约 3.8 / 2,731 MiB。它们仅约束索引拥有的资源，不涵盖项目源、页缓存、WPF 或 CLR。
- 可见洋葱皮分组按 64 MiB 预算计算，服从全局 256 MiB 栅格缓存；共享既有有界后台执行器。Compiled 使用逐轨指纹/范围，未编辑轨道不因整曲 fingerprint 或结束位置变化而全部失效。

## 6. 原有编辑、保存与渲染回归

### 真实 MIDI：60,000 音符编辑、取消、Format 3 保存与重开

对每个样本最大音符 Track：读取 60,000 个选中对象，准备并提交长度修改与移动，Undo / Redo，检查对象数及原值；取消必须保留来源/历史。再配置 presentation，Save、Save Copy、重开并核对音符数和配置。

| 源规模 | Read selection | 长度修改准备 / Undo | 移动准备 / Undo | Save / Save Copy / Open | 完整测试进程峰值 WS |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1M | 106 ms | 2.56 s / 0.6 ms | 1.64 s / <0.1 ms | 7.1 / 4.3 / 3.1 s | 1,090 MiB |
| 3M | 102 ms | 2.22 s / 0.9 ms | 1.30 s / <0.1 ms | 19.1 / 14.5 / 7.8 s | 2,774 MiB |
| 7M | 102 ms | 2.07 s / 0.6 ms | 1.56 s / <0.1 ms | 40.2 / 32.2 / 18.8 s | 6,336 MiB |
| 18M | 108 ms | 2.25 s / 0.7 ms | 2.03 s / <0.1 ms | 102.7 / 84.1 / 52.9 s | 11,621 MiB |

结果：`roundtrip-{1m,3m,7m,18m}.trx`。发布事务约 0.5–6.6 ms；取消约 1.8–2.8 ms；预算检查通过。这里的准备/Undo 是数据层事务，不包括人眼看到整幅视图完成重绘的时间。

**已知风险：18M 保存/副本/重开整条测试路径峰值约 11.35 GiB。** 此进程没有创建洋葱皮位图或 Compiled 索引，因此不能归因于新增洋葱皮；但测试同时保留原项目、历史及重开的项目，没有分阶段 GC Heap 分析，不能声称是 Save 单步耗用、泄漏或已经定位某个单一根因。本轮没有重写通用持久化流水线，不宣称整个程序具备固定低内存上限。这是发布前需要继续关注的资源风险。

### 合成与原有 WPF 专项

- 1M 批量生成六类对象：Direct / Logical / Template Note、Direct / Logical / Template Event；准备约 21.2 / 7.4 / 10.4 / 16.5 / 7.8 / 8.2 s，Undo 均小于 1.3 ms；Resident 均不超过 64 MiB，最大 spill 760 MiB。结果 `generation-1m-six-types.trx`。
- Logical owner 1M / 10M、60K Move / Resize / Duplicate 与 1M Velocity 修改；取消、Undo/Redo 和预算全部通过，Resident 不超过 64 MiB。结果 `logical-owner-1m-10m.trx`。这些运行有先后热身，不能用较大数据后跑更快推断超线性加速。
- 9KX2 MIDI Out #23 的 400K 选择：Properties 首次 1.63 s、重复 1.70 s、metrics 1.67 s；累计分配 459–520 MiB，但测点 managed 185–426 MiB。结果 `selection-400k-release.trx`，累计分配不可当作保留内存。
- 原钢琴卷帘连续跨 Segment 右边界滚动/缩放：完整离屏绘制 p50 / p95 / max 40.6 / 50.3 / 68.5 ms；其中 content phase 0.35 / 1.02 / 3.70 ms，总 OnRender phase 2.43 / 4.05 / 7.18 ms。共享栅格约 149 MiB。
- 原 Arrangement 冷前台 initial / 新区域 / zoom 约 19.5 / 13.2 / 24.9 ms；钢琴卷帘约 22.9 / 44.4 / 8.0 ms。独立 WPF 内存场景测得 500 MiB WS、161 MiB managed、4 MiB 已完成栅格且 0 在途请求。以上三项结果 `wpf-memory-and-boundary-release.trx`，并非真实 GUI FPS 或全部历史操作后的峰值。

## 7. 可复现入口与交付限制

- 普通回归：`dotnet test <project.csproj> -c Release -p:BaseOutputPath=bin/Stage8/ --no-restore`。
- 实际主题/洋葱皮探针：`dotnet run --project src/midora-desktop/Midora.Desktop.OnionProbe -c Release -p:BaseOutputPath=bin/Stage8/ --no-restore -- [MIDI paths]`；不带路径仅跑主题、行坐标与 100/200/400 Track。
- 真实文件编辑与重开：设置 `MIDORA_STAGE8_MIDI` 后过滤 `Stage8LargeProjectRegressionTests`。
- 合成生成：`MIDORA_GENERATOR_BENCHMARK_COUNT=1000000` 与 `MIDORA_GENERATOR_BENCHMARK_KINDS=direct,logical,template,direct-event,logical-event,template-event`。
- Logical 大型 owner：`MIDORA_BOUNDED_NOTE_PERF=1`。其他旧探针的环境变量以各自测试源码为准，不默认启动昂贵测试。

测试输出位于被忽略的 `.tmp/test-results/stage8/`；探针源代码与结论保留在仓库，用户 MIDI、音色库、位图和临时索引不提交。尚未做实际人机视觉验收、长时间真实创作回归、真实多显示器 DPI 迁移或故意进程惨重崩溃测试；不得把本报告解释为这些已经通过。人工只需按一次合并清单逐项验收。
