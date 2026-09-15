# Int64 诊断与 MIDI README 上限：实施及验证

日期：2026-09-09。状态：实现与自动验证已完成，已随 `560082e9` 提交、推送；用户在本次文档收尾中确认人工大体全部验收通过。此结论不是 D01～D05 已逐项测量的记录，精细体验留到真实完整编曲。需求依据见 [设计记录](Midora-Int64-Diagnostics-and-Bounded-Readme-Design.md) 和 SRS INV-117。本轮只更新文档，不提交、推送、本地发布或使用 computer-use。

## 已实现

1. 正式诊断改为显式 `ICompilerDiagnosticSequence`：总数、ordinal、各级别计数为 Int64，紧凑 sources/ranges 保留全部逻辑条目。Canonical、编译拒绝异常、MIDI/Audio task result 和状态栏同步迁移；不得通过旧 Int32 Count 伪装完整列表。
2. 超过 Int64 的逻辑计数产生专用容量错误；前台消费和后台状态保留准确原因。只针对这个边界，不把其他溢出/OOM 误报为诊断超限；失败不替换最后完整结果，旧结果不算当前成功结果。
3. Diagnostics 在筛选后数量超过 Int32.MaxValue 时提供每页 4096 行的 Previous / Next / Go。筛选针对全源，状态栏计数不受分页影响；非法页码保留当前页，换页不沿用旧行，未改变筛选/修订的 Tab 切换保留页码。
4. MIDI README 按原顺序合计最多列出前 1000 条 Warning/Info，附精确总数和省略数。准备、冻结、写入都只保留有界文本前缀，不先构建完整报告。Error 与 Warning-as-error 仍使用完整诊断。

正常小报告保持旧字节 golden；不改变音乐事件、音频、工程格式或产品版本。

## 测试范围与证据

全部测试日志位于工作区 `.tmp/int64-diagnostics/`，不提交生成物。重型测试串行，8 GiB 进程树私有提交硬限制、2 GiB 系统可用内存余量；达到限制即中止，不静默重试。

- `compiler-run.trx`：15 项针对性测试通过。65,537 个相交源产生 **2,147,516,416** 条诊断，闭式三角前缀 oracle 检查跨 Int32 ordinal、末项、插入前后缀和筛选顺序。真实 Compiler 的 Reject、Warn、Warning-as-error 三种分支 Full/Incremental 等价。
- `export.trx`：最初 59 项 MIDI Export 测试通过；覆盖 0/999/1000/1001/Int32.MaxValue、超过 Int32 的紧凑总数、准备/冻结不读取尾部、取消、旧 golden、错误前缀计数。最终补测另覆盖 Int64.MaxValue 和禁止覆盖冻结 projection 的真实总数。
- `desktop-focused.trx`：10 项诊断数据层/STA WPF 测试通过，覆盖 Int32 两侧及 Int64.MaxValue 页码、末页、错误跳转、旧页行身份、缓存上限和释放。
- 实际 `MainWindow` Diagnostics DataTemplate 离屏加载、绑定、布局、RoutedEvent 按钮和全源筛选由 `eng/DiagnosticPagingProbe` 验证；不依赖纯源码断言，也不枚举几十亿逻辑诊断。
- Playback 故障注入测试确认：容量错误保留明确消息与最后完整结果；无关 OverflowException 不误报为容量错误；重新编译恢复后清除故障状态。

`regression-final/regression-summary.json`：11 套完整回归 **3181/3181 通过，0 失败、0 跳过**。最终 `export-final.trx` 为 MIDI Export **60/60 通过**，含最后新增的 Int64.MaxValue 用例和冻结总数一致性校验；去重后本次覆盖 **3182 个通过用例**，不重复累计重跑项。

| 测试套件 | 完整回归通过数 |
|---|---:|
| Common | 81 |
| MIDI | 29 |
| Application | 1139 |
| Compiler | 475 |
| Persistence | 228 |
| MIDI Export | 59 |
| Playback | 145 |
| Audio Render | 37 |
| Desktop Presentation | 445 |
| Desktop | 433 |
| BASS managed gates | 110 |

完整回归进程树最高 Private commit 约 **2.36 GiB**、Working Set 约 **2.55 GiB**（Application suite）；所有守护 `stopReason = null`，未触发内存停止阈值。

### 实际 WPF 验证

最终证据：`.tmp/int64-diagnostics/wpf-final-result/result.json` 与 `wpf-final-guard/guard-result.json`。

| 指标 | 实测 |
|---|---:|
| 源音符 | 65,537 |
| 完整诊断逻辑条数 | 2,147,516,416 |
| 诊断 sources/ranges 计账 | 4,194,560 bytes，约 4 MiB |
| 实际生成的可见 WPF 行控件 | 18 |
| Full Compile | 3.73 s |
| 一次全源搜索并重新布局 | 0.84 s |
| WPF 探针进程树峰值 Private commit | 约 200.6 MiB |
| WPF 探针进程树峰值 Working Set | 约 265.6 MiB |

计账只是诊断数据自身；峰值包含应用初始化、测试 Project、编译、WPF 和验收包写入，二者不可混为一谈。耗时为本机单次检查，不是与旧版的相同输入 A/B 性能承诺；旧版该输入本来就因 Int32 overflow 失败。

已检查离屏图 `diagnostic-page.png`：页码栏位于摘要下面，不改变 Go to Source 区；普通规模隐藏分页栏。初次探针用没有 HWND 的 `IsVisible` 测可见性导致断言失败，随后改为检查实际模板祖先的 `Visibility` 链；这属于测试夹具修正，没有绕过生产断言。早期守护启动把目标写成裸 `dotnet` 也曾失败，改为完整可执行路径后正常启动；失败日志保留，未计作通过。

### 生成的验收项目

- [1275 条 Warning：README 上限](../.tmp/int64-diagnostics/wpf-final-result/diagnostics-readme-1275.midora)：51 个测试音符，文件约 3.5 KiB。
- [超 Int32 诊断分页](../.tmp/int64-diagnostics/wpf-final-result/diagnostics-int64.midora)：65,537 个测试音符，文件约 288 KiB。

两个文件通过正式 Format 3 Save Copy 写入及 writer 自校验。只包含合成测试内容，没有读取或修改用户的音乐项目。

## 人工验收状态与后续回归清单

2026-09-09 用户明确确认目前人工大体全部通过，不要求本轮重新执行这些条目。下表保留为真实编曲或正式发布前的定向回归清单；不把总体反馈伪造成每一项、每一种规模都有独立实测证据。复验时使用包含本轮实现的源码构建；本轮未更新 `dist`。

| 编号 | 操作 | 预期 |
|---|---|---|
| D01 | 普通项目打开 Diagnostics、搜索/筛选，再切换 Tab | 小列表没有分页栏；诊断和全局计数正确，切回不自动聚焦搜索框 |
| D02 | 打开生成的 `diagnostics-readme-1275.midora`，不启用 Warning-as-error，导出 MIDI 和 README 到新的测试目录 | Diagnostics 区恰好 1000 条，明确总共 1275 条、省略 275 条；软件内仍有完整 1275 条 |
| D03 | 同一项目启用 MIDI Export 的 Warning-as-error | 仍被完整 Warning 判定阻止，不因报告截断而成功 |
| D04 | 打开生成的 `diagnostics-int64.midora`，进入 Diagnostics | 准确显示 2,147,516,416 条 Error；4096 行分页，能跳到第 524289 页及末页；不要尝试播放，这个夹具故意违反 overlap policy |
| D05 | 在 D04 输入非法页码、切页、搜索、切换 Tab，再选中一行使用 Go to Source | 非法输入不改变当前页；筛选不是只搜当前页；未改变筛选时 Tab 切换保留页码；定位真实来源，不使用旧页选择 |

## 边界与未验证项

- Int64 扩容不意味着任意规模都能在任何机器上完成；物理来源、range 和编译工作仍受资源限制。这里只解决逻辑诊断总数不能表达的问题，不创建数十亿常驻对象。
- README 截断不节省音乐编译本身的成本；它避免报告文字与文件输出随全部逻辑诊断增长。没有承诺所有项目的编译时间零变化。
- 离屏 WPF 测试没有 HWND，不等价于真人键盘/鼠标焦点验收。未进行本轮实际 BASS/WASAPI 发声或设备测试；完整回归中的音频项为既有 managed gates。
- 全局 F4/Shift+F4 是既有规格缺项；用户于本次收尾明确暂缓，不阻塞下一项工作。现有 Go to Source 保留，SRS 要求不删除。
- `misc/user/midora-plan-pre-v1.0.txt` 是已有无关用户文件，未修改。

## 改动位置

- `Midora.Compiler`：`ICompilerDiagnosticSequence.cs`、`CompilerDiagnosticList.cs`、`DiagnosticCapacityExceededException.cs`、`CompilationRejectedException.cs`、`Contracts.cs`。
- `Midora.MidiExport`：README factory/builder，以及 compilation/task result 的完整诊断契约；`Midora.AudioRender` 同步两个结果入口，不改变渲染实现。
- `Midora.Playback/ProjectCompilationSession.cs`：专用失败原因传递。
- `Midora.Desktop`：`VirtualDiagnosticRows.cs`、`PresentationModels.cs`、`DesktopSessionController.cs`、`MainWindow.xaml(.cs)`。
- 新增/扩展 Compiler、MIDI Export、Playback、Desktop 回归测试和 `eng/DiagnosticPagingProbe`；SRS 第 0、12、14、17、22 章及阶段 6 历史设计的后续决定链接同步更新。
