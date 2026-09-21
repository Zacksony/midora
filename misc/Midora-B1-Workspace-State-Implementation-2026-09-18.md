# B1：会话内编辑器状态归属与轻量记忆

日期：2026-09-18。依据：已批准 D-STATE01/03 与 [B1 详细计划](Midora-Next-Development-Requirements-2026-09-11/execution/05-Workspace-State-and-Track-Navigation.md)。状态：已实施，待 UAT-B1-01～06 人工验收。B2/B3 未实施。

## 需求追踪与设计决定

- 输入：正式 Track/Segment/Definition/SubVoice 稳定身份及现有 UI 标量；输出：同轨通用设置、各 Segment 局部位置/Lane 状态、各 SubVoice 独立设置。
- 状态注册表的记忆项/快照只保留数值、枚举、稳定 ID 与小型 target 集合，不保留 Project、VM、选择、页、source factory、位图或任务。运行期通知会引用活动适配器，隐藏即退订，关闭随 VM 释放；注册表随成功替换 Project 释放，不能用订阅保留已关闭 VM。
- Track profile 不以 Usage/Root 为 key；仅共享偏好，不共享带 TPQN/拍号/参考位置的 `TimelineEditorSettings` 实例。每宿主保持独立时间解析上下文。
- 视图状态修改不产生音乐 Modified/Undo/编译修订，也不通知 Onion/All Tracks 重建。隐藏页激活时应用最新 profile，不能借同步扫描音乐对象。
- 普通关闭保留纯描述；删除 owner 清除自身描述，Undo 不自动重开或复活已删除描述。复制只消费正式 ID map，已存在目标不覆盖；转轨保留 Segment 局部位置并接入目标 Track profile。
- Lane 目录暂未完成不等于 target 删除；先保留恢复意图，权威目录到达后才过滤。Surface 轴按既有归一化范围捕获/恢复，不混淆原始和显示域。
- 显式 Arrangement 打开且编辑指针在 Segment 范围内时，定位优先于记忆；普通切页、关闭再开不强制归零。恢复不恢复选择和鼠标手势，不改变焦点合同。
- B1 不修改 Format 4 / presentation schema 2 或文件内容；冻结/导入纯描述接口供 B2 使用。B2/B3、Tracks 侧栏、多实例、逻辑参数独立点模型和音频后端不在本轮范围。
- 异常/资源准入失败必须明确提示，不能影响音乐保存、静默丢弃既有状态或把异常抛穿 WPF 回调。资源预算与实测、已跑/未跑验证及验收工程见下文。

## 验证记录

实施前基线为 `caf8cec`。现有 5 个规划文档的未提交修改予以保留。本轮不自动提交、推送、发布或使用 computer-use。

## 交付清单与主要落点

1. **Track 通用设置。** 两类 Segment 按正式 Track ID 共享横/纵缩放、Grid、Piano/Event 各自 Snap/subdivision、主工具/事件绘线形态、默认音长/力度、Lanes 与 List 布局；同 Usage/Root 的不同 Track 不共享。`PianoEditorDefaults` 与原初始化共用常量；每个编辑器保留自己的 TPQN/拍号上下文。
2. **局部状态。** 各 Segment 保留内容坐标位置、List 行位置、活动/隐藏/重排 Lane 和归一化纵轴。SubVoice 的上述字段以 Definition + SubVoice ID 独立。空的显式 MIDI Lane 只记 target，不创建 MIDI 事件。冷目录返回前保留待恢复 target，不把 `Counting…` 当成删除。
3. **生命周期。** 关闭 Tab 留轻量描述、释放 VM；删除 owner 释放其描述，Undo 不自动重开或复活该描述。Duplicate 消费正式 clone map；跨类型 Move 消费新 `SegmentIdentityTransfer`，只迁移通用局部位置/Velocity 轴；Copy 不继承。取消准备、失败提交不发布迁移通知。
4. **Onion 边界。** 删除目标自身清除其 preset；存活目标的 dormant 来源 ID 仍可随音乐 Undo 恢复。普通 Pan/Zoom 不修改 Onion revision 或重建 All Tracks 索引。
5. **会话边界。** 成功换 Project 才替换注册表；打开失败保留旧记忆；旧绑定、异步回调不能写入新项目。新增状态尚未保存到 `.midora`；只提供 B2 可用的 immutable freeze、原子验证导入、saved revision 边界。

主要源码：Desktop 的 `WorkspaceStateRegistry`、`EditorWorkspaceStateBinding`、`DesktopSessionController.WorkspaceState`、`PresentationModels`、`LaneTabSession/Header`、`TimelineObjectListState`；Application 只补身份对应通知及 detached/clipboard 包装传递。未改 Compiler、音频后端、SMF 导出、渲染器、分页音符模型或发布脚本。

## 冻结的会话资源预算

| 项目 | 限制/测量 |
|---|---|
| Registry 保守 resident 计费 | 64 MiB；这是新增状态预算，不是应用总内存预算 |
| 总条目 | 131,072；Track/SubVoice profile、local viewport 与 Lane partition 各算一项 |
| 单 owner Lane partition | 最多 65,536 个 target/显式 MIDI target 描述；导入前先验上限，再验证重复/数值 |
| 计费 | profile 768 B、local 192 B、Lane partition 192 B、每 target 128 B；含目录容量/描述开销的保守裕量 |
| 超限 | 拒绝新增/扩大的记忆项，明确状态提示；不淘汰已有描述，不拒绝打开/编辑/保存音乐。重复警告只提示一次；释放 owner 后可继续准入 |
| 活动订阅 | 只订阅当前 owner；隐藏页解除 profile 订阅，激活时读取最新值；关闭/项目释放回到零 |
| 热路径 | 单 key 更新；相同值无 revision/通知；仅显式 Freeze 才复制目录，不在滚动/缩放时复制整个 registry |

合成输入：1,000 个 Track profile、10,000 个局部 viewport、160,000 个 Lane 描述。Release 实测 retained managed **10,433,744 B（约 9.95 MiB）**，保守计费 **25,088,000 B（约 23.93 MiB）**。10,000 次 registry 热更新共 **8.503 ms**、**128 B** 新分配（包含测量对象/运行时噪声），恰好10,000次通知。这个结果与访问 owner/target 数量相关，不与歌曲音符总量相关。低预算拒绝、重复 Dispose、同值更新、非法快照原子拒绝均有测试；B2 文件编码字节预算、B3 恢复并发预算仍未冻结。

## 同机真实样本对照

样本：`9KX2 18 Million Notes.mid`，测试轨 `MIDI Out #23`，该 Segment 实际 **1,824,636** 个音符。原文件仅只读导入，无覆盖保存。开始时可用物理内存约19.96 GiB；探针在7 GiB private时提前失败，低于用户9 GiB警戒；本轮没有触及。两版使用相同新增只读探针、Release、相同输入；原版在 `.tmp/b1-baseline-caf8cec` detached worktree 构建，正式比较时不并行构建或运行其他测试。

| 指标 | `caf8cec` | B1 |
|---|---:|---:|
| 首次 OpenSegment（包括该进程首次初始化/JIT） | 4.978 ms | 21.864 ms |
| 10,000次 Pan/Zoom/高度更新 p50 | 0.0001 ms | 0.0029 ms |
| 同上 p95 / p99 | 0.0001 / 0.0002 ms | 0.0045 / 0.0047 ms |
| 同上当前线程总分配，含测量数组 | 80,024 B | 800,024 B |
| 20次关闭重开 p50 / p95 | 0.2382 / 0.3480 ms | 0.2735 / 0.4053 ms |
| 冷 WPF Arrangement：首帧/新区/缩放 | 34.1 / 25.2 / 16.5 ms | 37.0 / 23.4 / 16.7 ms |
| 冷 WPF Piano：首帧/新区/缩放 | 17.8 / 4.6 / 15.3 ms | 17.4 / 6.7 / 15.4 ms |
| 探针结束 managed / private | 138.13 / 339.91 MiB | 124.64 / 276.46 MiB |

解释与限制：

- 新增状态同步有真实开销：裸 VM 探针每轮新增约72 B通知开销，热更新中位数增加约2.8微秒；不把它表述为“零代价”。普通 WPF 本来就有绑定订阅，裸 VM 数据不等同于整个帧的新增分配。
- 第一次打开多了约17 ms；该值包含首次类型初始化/JIT，不能用单次数据断言所有冷打开都退化17 ms。后续重开p95差约0.057 ms。
- WPF 单帧值受线程调度/后台瓦片影响，表中不是统计显著性结论，也不声称这次改动提高了渲染速度。现有原门限均未放宽。
- 热更新期间 Snapshot 引用与 compilation source revision 必须不变，测试已断言；没有为同步重新扫描百万音符。最终 managed/private 较低不能归功于 B1 的内存节省，进程高水位/GC存在波动。
- 没测真实多显示器拖窗、所有显卡/DPI组合或设备播放；真实 WPF 模板用未显示的 HwndSource 自动验证，不使用 computer-use。

## 自动验证与复现入口

证据目录：`.tmp/verification/b1/`，TRX 含每项结果、失败明细和探针 stdout。普通全套中的 opt-in 探针在无环境变量时不读外部样本；下列9KX2两项另行显式启用并通过。

| 验证 | 结果 |
|---|---|
| 实施前现有定向基线 | 32通过 |
| Desktop Release 全套 | 最终531通过，2分29秒，`b1-desktop-final.trx`；包含 staged 转换、Definition clone、DPI与边界复查。先前530项运行也通过，独立保留 |
| Presentation Release 全套 | 581通过 |
| Application Release 全套 | 1,255通过 |
| Persistence Release 全套 | 239通过 |
| B1 + 生命周期 + Onion 最后定向 | 41通过 |
| 9KX2 基线/当前版显式探针 | 各2通过；warmup记录单独保留，不混入正式对照 |
| 实际 WPF 模板 | 三宿主关闭/重开、Lane轴、显隐、SubVoice行位置切换，以及100%/125%/150%/200% DPI往返不反写profile均通过；`b1-wpf-final.trx` |
| UAT 包生成与 OpenAsync重开 | 通过；17,503 B，Format 4 / presentation schema 2 |

覆盖要点：Fixed/Auto Root、Shared Logical Usage、Track副本独立；不同 TPQN（1/480/32767）、拍号、ContentOffset、近long.MaxValue；Snap的合法结果及既有越界拒绝保持一致；新 Segment不继承旧选择；空Lane/权威删除/冷目录；删除closed SubVoice/Undo；项目替换失败与迟到旧绑定；100轮Workspace释放；冻结快照隔离、低预算、只改视图后所有ZIP entry字节一致（固定保存时钟，避免把正常ModifiedAtUtc差异当BUG）。

调试中发现并处理的问题：批量注册表通知的 `Enum.HasFlag` 装箱分配已移除；跨类型转换身份元数据在剪贴板包装丢失，已补转发并覆盖Move/Copy/Undo/Redo；近最大Tick测试起初错误假定Snap必定成功，已改为与原路径的结果/拒绝一致；源码包字节测试起初未固定保存时钟，差异确认仅来自正常保存时间，后固定时钟验证全部entry。部分早期TRX曾被后续定向运行覆盖，因此不声称所有失败日志均独立存档；本段保留失败原因，最终运行使用独立文件名。以上没有靠放宽性能门或修改音乐语义解决。

最终文档检查：本轮修改/新增 Markdown 的109处本地链接文件目标存在；按仓库换行配置的 `git diff --check` 通过。用户前序的5份未提交规划改动保留并继续更新；没有修改Q1/Q2原回答。人工验收仍未执行，不将自动测试数量解释为全部用户操作组合都已经验证。

主要命令（在仓库根目录运行，不发布）：

```powershell
dotnet test src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj -c Release --no-restore
dotnet test src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj -c Release --no-restore
dotnet test src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj -c Release --no-restore
dotnet test src/midora-core/Midora.Persistence.Tests/Midora.Persistence.Tests.csproj -c Release --no-restore
```

真实样本专用过滤器：`WorkspaceStatePerformanceProbeTests|OptInLargeMidiMeasuresColdForegroundFrameLatency`；分别设置 `MIDORA_B1_SAMPLE` / `MIDORA_WPF_MEMORY_SAMPLE_PATH` 为上述授权文件路径。生成新UAT副本时设置 `MIDORA_B1_UAT_FILE` 为不存在的新目标文件，运行 `WorkspaceStateB1FixtureTests.OptInGenerateB1AcceptanceProject`；不会覆盖既有样例或用户项目。

## 人工验收交接

- 工程：[B1-Workspace-State.midora](../.tmp/uat/b1/B1-Workspace-State.midora)。
- 简明操作：[README](../.tmp/uat/b1/README.md)。
- 正式6项清单：[主题05 §8](Midora-Next-Development-Requirements-2026-09-11/execution/05-Workspace-State-and-Track-Navigation.md)。

需要人工确认：同轨/异轨设置、关闭重开与复制、Lane局部状态、SubVoice隔离、转轨/删除/Undo、显式定位和快捷键。全部仍为待验收，不把自动测试或样例生成当成用户已通过。不推进B2/B3，不提交/推送/本地发布。
