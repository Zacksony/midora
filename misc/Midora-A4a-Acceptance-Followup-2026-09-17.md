# A4a 验收后修订：全 Lane 辅助线与程序级开关

状态：已实施；用户已确认 Lines 优化验收通过，原 UAT-A4a 大体验收结论保留。原样例修正版已有自动验证，未单独收到人工结果。逻辑参数底层写入约束强化明确延期，见 §5。本轮明确取代 R29 原状态型 Lane 白名单和会话内逐 Lane 开关。原问卷回答保留，新增决定为 D-STEP05，不追溯改写历史答复。

## Requirement trace / 设计记录

- 输入：MIDI Segment、Logical Segment、SubVoice 当前 Lane 自身显式记录。
- 新范围：除了 Vel.、Inst.，全部 Lane 可显示辅助 Lines，包含 Bank、Program、命令 CC、Imported Meta / SysEx；它们是位置/前驱提示，不声明命令会保持发声状态。无数值 opaque 记录沿用现有点的固定 y，不解释 payload。
- 正式输出：不改源事件、Logical Parameter Step 约束、Mapping、canonical、播放或导出；既有 Value Curve/Envelope 插值不变。无自身前驱不补线，不跨 owner。
- UI：删除 Lane 头部 Lines 按钮；Application Preferences → Appearance 新增 `Show event lane lines`，默认启用，Apply 后更新所有已打开及以后打开的 Lane。
- 持久化：程序级 Appearance 独立布尔设置；沿用 Application Preferences schema3 的可选字段兼容方式，旧设置缺字段等于 true。不是 Project presentation，不进入 Undo/Modified，不因此重建 Worker。取消/保存失败不得发布新值。
- 资源：复用 A4a 独立有界后台线缓存和取消机制；新增 opaque 适配不得物化整 lane 或复制 payload。关闭后停止该辅助层任务，点/选择层不受影响。
- MIDORA1316：先只读核对用户指定 A2b 样例，定位非法 interpolation 的创建来源。不得为修样例放宽编译器或静默正规化任意项目；若确认样例构建错误，修构建器并加编译回归。修正版文件另存，不覆盖用户原件。
- 非目标：A4b、音频改动、全局改变 CurvePoint 默认插值、自动项目迁移、提交/推送/发布、computer-use。

## 1. 实施结果

- 三宿主共用 `EventStepSignalSources`：Direct/Template 不再按“状态型 CC”过滤，Bank/Program、命令 CC 同样有辅助线。Opaque 使用其既有点坐标 `y=1`，仅传递 Tick/order/scalar，不解释或额外持有 payload。Vel./Inst. 不增加辅助线。不扩大 SubVoice 允许创建的事件种类，既有 CC91/93 限制等音乐约束不变。
- `AppearancePreferences.ShowEventLaneLines` 默认 true，经正式 Application Preferences Store 保存；没有新字段的旧设置默认 true。`LaneTabHeader` 删除 Lines 按钮，经同一 Session 属性将全局值绑定到三个宿主的活动 Surface；已绑定的隐藏 Surface 及以后新建/切回的 Surface 使用同一值。
- 正常设置 Draft/OK/Cancel/恢复默认流程不分叉。只改此项时 `RequiresAudioWorkerRebuild=false`；不提交 Project command，不更改 Selection 或 Lane 纵轴。关闭仍复用辅助层取消机制，不取消原点/选择层。
- Opaque snapshot 的 overlay 范围查询与范围 fingerprint 改用现有不可变树的流式遍历，避免为了辅助线先建立整段匹配记录 List；同 Tick/半开范围/不可变修订不变。

### 主要文件

| 归属 | 文件与变化 |
|---|---|
| 设置模型/持久化 | `ApplicationPreferences.cs`、`ApplicationPreferencesStore.cs`：可选 bool、默认值、round-trip |
| 设置及三宿主接线 | `ApplicationPreferencesDialog.xaml/.cs`、`DesktopSessionController.cs`、`MainWindow.LaneTabs.cs`、`LaneTabHeader.xaml.cs`；Lane Header 的最终 XAML 不保留新 Lines 按钮 |
| 辅助数据源 | `EventStepSignalSources.cs`、`PresentationModels.cs`、`PureMidiPointQuerySnapshots.cs`：全 target/opaque、流式范围遍历 |
| 样例与自动回归 | `InstrumentChangeTests.A2b.cs`、`A4aSampleAuditTests.cs`、`ApplicationPreferencesStoreTests.cs`、`EventStepSignalSourceTests.cs`、`LaneTabWpfIntegrationTests.cs` |
| 规格/决定 | SRS §17.2.2/§18.2.10/INV-122、D-STEP05、专题/执行总表及原 A4a 报告的历史状态 |

## 2. MIDORA1316 的根因与安全修正

已只读打开用户指定文件 `.tmp/uat/a2b/A2b-Lanes-and-Instruments.midora` 并完整编译。8 条 Error 一一对应 `Logical — parameter lanes` 的 8 个参数点：Tick 为 0、384、768、1152、1536、1920、2304、2688，值为 30～100（每次 +10），它们全都保存为 **Linear**。

根因是 A2b 样例生成器直接调用 `new CurvePoint(project, tick, value)`，漏传 `CurveInterpolation.Step`。通用 CurvePoint 同时用于允许插值的 Value Curve/Envelope，其默认值是 Linear；Logical Parameter Lane 则早已只允许 Step，所以 **MIDORA1316 是正确诊断；坏的是样例生成器，不是 A4a 渲染或编译验证**。

修正内容：

1. 样例构造显式指定 Step，并在普通测试中无条件编译、断言 IsConsumable/所有参数点 Step；不再只有设置生成环境变量后才检查样例。
2. 不修改通用 CurvePoint 默认值、不放宽编译器、不对任意旧项目静默修正。已复核相关 Draw/批量/量化逻辑点路径的显式 Step，不改变 Value Curve/Envelope。
3. 原文件逐字节保留；同目录另存 **`A2b-Lanes-and-Instruments-step-fixed.midora`**。只将这 8 个逻辑参数点的 interpolation 改为 Step；重新打开后 ID/Tick/value 全部一致，编译结果可消费、诊断 **0**，保存前后 corrected canonical fingerprint 一致。
4. ZIP 解压 entry 对照只有 `logical-tracks/lt_10.pb`、`metadata.json`、`manifest.json` 变化。metadata 只更新正常的保存 UTC；其他音乐对象、presentation、工程总耗时等保持不变。已有修正版不自动覆盖。

以后使用修正版即可；原件保留供核对。本轮没有直接覆盖用户原项目。

## 3. 自动验证证据

环境：Windows / .NET SDK 10.0.400、runtime 10.0.11、Release；不使用 computer-use，不启动发布脚本。证据目录 `.tmp/test-results/a4a-followup/`。以下为本轮实际重新运行的结果，不复用上一轮计数。

| 验证 | 结果 | 证据 |
|---|---|---|
| Desktop 全量 | 479/479 通过，3分07秒 | `desktop-all.trx` |
| Presentation 全量 | 545/545 通过，30秒 | `presentation-all.trx` |
| Application 全量 | 1251/1251 通过，3分23秒 | `application-all.trx` |
| Persistence 全量 | 239/239 通过，22秒 | `persistence-all.trx` |
| 原样例只读调查 | 1/1；确认 8 条 MIDORA1316，原件不变 | `sample-audit.trx` |
| 实际样例另存/重开及设置/生成器回归 | 31/31；修正版 0 诊断，原件不变 | `sample-repair-and-preferences.trx` |
| 新 Lines 数据源与显示域定向回归 | 15/15 通过 | `lines-followup.trx` |
| 最终编译及数据源/三宿主 WPF 定向复验 | 11/11 通过，2分18秒 | `final-focused.trx` |
| 最终修正版只读打开、生成器和偏好 round-trip | 3/3 通过，1秒 | `final-sample-and-preferences.trx` |

完整四套共 **2514 项通过、0 失败**。三宿主隐藏 HwndSource/WPF 集成验证了：Appearance 草稿/模拟保存失败不提前应用、恢复默认、全局切换后已打开/新建/隐藏重开视图一致、选择及纵轴不变、Vel. 无辅助源、按钮移除后的 100/125/150/200% DPI 紧凑布局。该方式是程序内自动测试，不是 computer-use。

数据源测试覆盖 128 种 Direct CC、Bank/Program、协议命令、固定 y opaque、owner/target 隔离、同 Tick 正式顺序、冻结修订与百万点范围。四种百万点辅助请求实测如下（全套测试进程，单次冷测；不是端到端 UI 帧时间）：

| 数据源 | 冷 fingerprint+raster | 冷分配 | 热 p50 / 最大值 | 每次热分配 |
|---|---:|---:|---:|---:|
| Direct | 258.35 ms | 0.69 MiB | 0.54 / 0.89 ms | 0.32 MiB |
| Logical | 189.39 ms | 8.65 MiB | 1.19 / 2.22 ms | 0.32 MiB |
| SubVoice | 311.07 ms | 9.65 MiB | 2.13 / 2.49 ms | 0.32 MiB |
| Opaque | 419.06 ms | 0.69 MiB | 0.19 / 0.27 ms | 0.32 MiB |

各探针仍执行冷分配32MiB、热分配2MiB、耗时30秒及进程Working Set 8GiB上限断言；不把整套测试进程 Working Set 当成辅助层独占内存。缓存/队列上限、正式点与选择优先保持原 A4a 实现。

保留限制：未重新进行真实应用长会话 pan/zoom 帧时序分布、全目标逐像素人工外观检查或音频听感测试；本轮没有音频逻辑变动。先前报告中未穷尽的工程矩阵不因本次通过而自动关闭。最终 `git diff --check` 通过。

## 4. 局部人工复验（不要求重做整份 UAT-A4a）

| ID | 检查 | 预期 | 状态 |
|---|---|---|---|
| UAT-A4a-R01 | 在三宿主看普通参数/CC，并在 MIDI/SubVoice 看可用 Bank、Program；MIDI 看命令 CC 和 Imported Meta / SysEx。把首点移到可见左侧之外 | 除 Vel./Inst. 外都显示辅助线；有自身前驱才接入左边，不跨轨/Segment 借值；点仍可照常编辑 | 用户确认 Lines 优化验收通过 |
| UAT-A4a-R02 | Application Preferences → Appearance 关闭/启用 `Show event lane lines`，切 Tab、隐藏重开 Lane、新开 Segment；试 Cancel，保存后重启 | 右上不再有 Lines 按钮；当前/后续适用 Lane 一致，Cancel 不应用，保存后保持，Vel./Inst. 不受影响 | 用户确认 Lines 优化验收通过 |
| UAT-A4a-R03 | 打开同目录 `A2b-Lanes-and-Instruments-step-fixed.midora`，Compile 并查看逻辑参数点 | 0 诊断；原点位置和值不变，原文件仍在 | 自动验证通过；未单独报告人工结果 |

上述实施轮未提交、推送或本地发布；本次验收归档按用户新授权提交并推送，不本地发布、不进入 A4b/B/C。

## 5. 验收归档与延期决定（2026-09-17）

**用户原话：** “先延期这个逻辑参数约束问题。刚才对Line的优化验收通过，请记录、提交、推送。”

- Lines 的新增 Lane 范围及 Appearance 全局开关已获用户验收通过；保留 §3 的自动证据和未穷尽工程门，不将人工通过扩大为全部压力矩阵完成。
- `DEFER-LPARAM-01`：逻辑参数专用类型／统一创建修改入口／集合写入边界的进一步约束强化延期，尚未选择具体实现，不在本次夹带实施。当前逻辑参数仍只允许 Step，MIDORA1316 校验、已经修正的样例生成器及其回归测试保留；延期不代表重新允许 Linear，也不撤回此次样例修复。
- 本次仅更新验收／延期文档并归档已完成的 A4a 代码，不新增产品行为。没有重新运行产品测试；沿用 §3 已记录的实际结果，提交前另外检查差异空白和文档本地链接。`.tmp` 样例与测试产物保持忽略，不加入 Git。
