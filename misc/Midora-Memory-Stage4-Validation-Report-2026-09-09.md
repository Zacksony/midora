# 内存优化阶段 4：M08 / M11 验证报告

日期：2026-09-09。阶段开始时 HEAD：`4a4beb63a11514e1f9042dc0dd739e143d120559`，工作区干净。

状态：**阶段 4 工程门通过，等待阶段 3＋4 合并人工验收 B。**不进入阶段 5，不提交、推送或生成 `dist`。

## 1. 本阶段改动与不变项

依据：[六阶段执行计划 §9](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md)、SRS §16 和 INV-088～096。本轮不修改 SRS、软件/文件版本、schema、descriptor 或 golden。

| 范围 | 已实施 | 保持不变 |
| --- | --- | --- |
| Logical / SubVoice / CurvePoint 普通入口 | 4,096 条晋升到不可变值根；普通追加、小编辑与已有分页 adopt 共享底层 | 稳定 ID、对象身份、正式顺序、全部字段、旧快照 |
| ID / 空间目录 | 顺序页共享范围树；乱序/交错 ID 使用精确分页地址树；小追加 path-copy | 不用全源扫描换内存；旧 revision 查询与局部 fingerprint |
| Compiler 只读输入 | LogicalNote / TemplateEvent / CurvePoint 改读值，RawInstance 不再持有音符 facade | 编译排序、诊断、Full/Incremental、来源和 canonical；全展开预算留阶段 5 |
| Event Instrument v1/v2、Logical Track v1 protobuf | 计长后流式编码、直接读入最终值页；去除 V2↔V1 bytes→Parse 中转 | 冻结 wire、presence、未知/重复/非法字段拒绝及错误分类 |
| Conductor JSON | 分块写出；逐 token 校验/读取，完成后原子接管 | 字段顺序、UTF-8、decimal、换行、损坏回退与旧合法乱序输入 |
| `.midora` 包保存/打开 | 单文件流式 staging + 长度/hash 目录；自校验再次编码到逐字节比较流 | 完整严格重开、自校验、来源保护、迁移永久副本、原子替换 |

具体设计见 [值根](Midora-Memory-Stage4-M11-Value-Roots-Design.md)、[protobuf](Midora-Memory-Stage4-Streaming-Protobuf-Design.md)、[Conductor](Midora-Memory-Stage4-Conductor-Streaming-Design.md)、[包事务](Midora-Memory-Stage4-Package-Streaming-Design.md)。

生产代码未改变 Arrangement / Piano Roll / Velocity / Event / Onion 的绘制、缓存精度、编辑交互或音频算法；公共测试仍须覆盖共享 Domain 改动的间接影响。

## 2. 资源边界，不等于整机常量内存

- 普通追加尾部最多 4,095 个编辑对象，读取/发布时转为值页；仍被 UI/调用者持有的 facade 保留身份，但集合通过弱引用关联，不再额外强持完整对象图。
- 同修订快照、clone 及历史引用共享不可变源。每次读取后再追加的长期小编辑会填满 128-record sequence/ordinal 尾叶，不退化成每对象一个页。
- 普通 source 的必要值、空间索引和 ID 地址元数据仍是 O(N)。乱序 ID 精确叶约 12 bytes/record 加树头；首次排序约 16 bytes/address 工作数组。它不是固定大小缓存，也未强制所有普通源 spill。
- 流式 protobuf 的最终源页为 4,096 条；计长缓存按容器/快照，而非每条 Note/Event DTO。已有 int32 消息长度边界不变。
- 包层不再保留所有文件的 byte[] 字典，验证也不再构建第二套字典。临时文件沿用原保存事务 owner，串行 64 KiB 聚合缓冲；必要路径/hash/长度目录随文件数增长。暂存文件仅 flush 到可读状态，正式 ZIP/永久副本的 durable publish 规则不变。
- Conductor 正常输入窗口 64 KiB、紧凑 record buffer 512 bytes。16 MiB 单记录空白测试没有扩大二者。原来合法的超长单 scalar 仍接受，缓冲可能随该 scalar 增长；不以暗加长度拒绝规则伪装为硬常量内存。
- Conductor 最终记录、不可变源、打开自校验时共存的 detached Project、旧快照仍需真实内存。History 跨命令总预算与大 Logical 编译展开不是本阶段完成项。

## 3. 正确性与故障检查

### 3.1 新增专项

- 普通和 adopted Logical/SubVoice 各 100k/1M；CurvePoint、头部/中部插入、百万源首次/重复 snapshot、clone、编辑旧值隔离、弱 facade 释放。
- 随机、倒序、跨页交错 ID 精确命中；100k 根之后追加 1,024 地址/单音符的分配边界，避免首次新位置扫描和累计小编辑平方退化。
- 后台冻结 snapshot 准备 external ID index 与 live owner 追加并行；在追加前、索引扩展中、空间页创建中控制交接，确保新旧源内容完整。最终建树/发布阶段取消不发布半个缓存，可重试。
- 新旧 protobuf/JSON bytes 等价；短读/非 seek、字段乱序、Unicode、presence、重复/未知、截断、非法数值、取消、IO 异常、调用方流所有权。
- 保存前/ZIP 后取消不改原目标；仅破坏 staging 而新 ZIP/hash 完好时，二次字节自校验仍拒绝发布；重开分页 Logical/SubVoice 后局部编辑、再保存和再打开保留值与 Loop/Pre-Roll。

### 3.2 测试中发现并收口的问题

1. Conductor 初版 writer 每条 DTO 触发 flush，20,001 记录产生 40,004 次非空写入。改成冻结 schema 的分块 writer 后为 28 次，输出 1,902,960 bytes 完全相同；未放宽校验。
2. 初轮公共回归的一个 malformed JSON 用例中，转义孤高代理属性名使 `ValueTextEquals` 抛 `InvalidOperationException`，未进入原 package 损坏回退。已统一为 `JsonException`，并补强 null/UTF-8/大空白/合法大 scalar 边界。
3. 普通非尾部 Insert 漏晋升、随机 ID 页范围可能线性扫描、external index 发布与 live Append 竞态、精确树最终分配阶段漏取消，均在交付前审查中补测修复。
4. 桌面全套的 Conductor 属性测试在新的冷 ID 路径上没有等待已有异步 presentation 完成：正式 Selection 和属性对象正确，但 `SelectedConductorEvent` 要经 Dispatcher 回调更新。测试补充正式 Primary 立即断言及完成信号/Dispatcher 等待，不修改产品选择语义，也不跳过原断言。这与本轮冷缓存路径有关，不能称为完全无关的既有失败。

### 3.3 最终公共回归：3,060 / 3,060

| 测试工程 | 通过 |
| --- | ---: |
| Common | 81 |
| MIDI | 29 |
| Application | 1,107 |
| Compiler | 437 |
| Persistence | 228 |
| MIDI Export | 43 |
| Playback | 140 |
| Audio Render | 37 |
| Desktop Presentation | 445 |
| Desktop | 403 |
| Audio.Bass 托管过滤 | 110 |
| **合计** | **3,060** |

Release 构建成功。TRX：`.tmp/memory-stage4/regression-final/`；Desktop 最终采用 `.tmp/memory-stage4/regression-desktop-final/Midora.Desktop.Tests.trx`，原失败文件保留，不覆盖证据。M11 的 66 项定向与 Compiler 437 项、Desktop 定向 1 项/Controller 54 项也通过，但与全套重叠，不重复加入总数。

Audio.Bass 仅运行 ring、共享控制协议、Limiter、PCM 缓存/写出、WAV、MIDI plan/分页流等托管门；不是原生声卡/SoundFont/设备移除实机验收。需要显式环境开关的现有大样本测试没有自动开启；本轮真实大样本证据以单独 9KX2 探针为准。

### 3.4 真实 9KX2 往返回归

样本 `D:\MIDI\Huge MIDIs\9KX2 18 Million Notes.mid`，144,175,201 bytes，SHA-256 `948fdddfff2f050c1b4e70f240b6138e5ba10939030a82286e149ddcb93e6249`。使用最终生产 DLL 的 `eng/MemoryStage1Probe roundtrip`，Windows 10.0.26200、12 logical processors、Workstation GC、.NET 10.0.11。数据写在 `.tmp/memory-stage4/18m-roundtrip/`，未修改用户 MIDI/项目。

- 导入 **17,999,999 源 Note、40 Track**；重开零 damaged Track、零恢复诊断，数量及 NextStableId 相等。
- 在最大 Segment 对 60k 音符执行右边界扩长和移动，各自 Prepare→Commit→Undo→Redo→Undo；另做取消准备和 presentation 保存。Prepare 分别 2.079 / 1.554 s，Commit 约 13 / 6 ms，首次及重复 Undo / Redo 均约 4 ms。这是无 WPF 数据路径单轮，不是手感承诺或首次可见区域的完整 UI 时延。
- Import 18.148 s；Save 29.950 s；Save Copy 28.771 s；重开 13.929 s。未对这条真实流程另跑修改前后的多轮计时，所以不把单轮数字宣传为 Stage 4 加速。
- 本轮 Peak WS **892.73 MiB**，最后诊断 GC 后 managed **4.90 MiB**，WS 仍为 **465.26 MiB**；两个 Project 弱引用均已失效。WS 不立即回到初值不等于 Project 泄漏。
- 全程 **38 次 source facade census 均为 0**，未恢复只读扫描物化全量 Note/Event/Opaque ID/对象表的问题。
- 与阶段 3 留存 `9kx2-new/saved.midora` 比较，以及本轮 Save 与 Save Copy 比较，**各 101 项内容一致**。该跨运行比较仅归一化创建/修改/工作耗时时间，忽略受其影响的 manifest；音乐、MPK、结构及 presentation 字节均比较。百万合成夹具使用固定时钟，比较则包含 manifest 和全部条目，不混淆两个口径。

日志：`.tmp/memory-stage4/18m-roundtrip.log`、`memory.csv`。没有 WPF、真实音频后端或自动后台编译；跨链路长期总预算仍由阶段 5/6 收口。

## 4. 时间和内存测量口径

探针：`eng/MemoryStage4Probe/`，同一源码针对修改前冻结 DLL 与当前 DLL 各自构建；Release、.NET 10.0.11、独立新进程，重型测试严格串行。旧版 DLL 在改动前保存于 `.tmp/memory-stage4/baseline/`。

普通构建使用逐条 `Add`，而非用已优化的 bulk adopt 掩盖剩余入口；首次快照包含正式枚举验证。必要时单列构建+首次快照总和，不能把索引成本前移描述为凭空消失。保存包括 ZIP、严格重开、再序列化和原子发布，未用更少验证工作量换速度。

计时不含阶段后的诊断强制 GC；post-GC live、Peak Working Set、累计分配分别记录。探针没有 WPF、Worker、SoundFont、后台自动编译，因此**不是整机/真实 UI 总内存上限或最低配置结论**。预热实验用于区分首次准备与后续耗时，不能与冷进程峰值混算。

完整结果保存在 `.tmp/memory-stage4/runs/`。每次 Save→Open→Resave 检查全部包条目 hash；旧/新对照也逐条检查。同名音乐对象、stable ID、epoch 和字段完全一致。`mixed` 是大型持久化压力夹具，不能当作音乐工程在自动编译 UI 播放，其 Logical×Template 展开乘积属于阶段 5。

### 4.1 百万级：最终三轮中位数

| 独立负载 | Peak WS：旧→新 | Save Copy：旧→新 | Open：旧→新 | 重开后 Resave：旧→新 |
| --- | ---: | ---: | ---: | ---: |
| 1M Logical Note | 1,162.01→213.60 MiB | 3.426→2.334 s | 1.520→0.732 s | 3.241→1.718 s |
| 1M SubVoice Event | 2,006.35→263.39 MiB | 7.439→3.023 s | 4.026→1.146 s | 7.048→2.354 s |
| 1M Conductor Tempo＋默认起点 | 2,047.14→295.69 MiB | 13.755→8.153 s | 7.772→2.963 s | 11.151→5.248 s |

上述独立流程峰值降低约 82% / 87% / 86%；保存中位时间减少约 32% / 59% / 41%，打开减少约 52% / 72% / 62%。不外推为任意工程或 WPF 进程相同幅度。

| 百万级其他指标 | 旧 | 新 | 解释 |
| --- | ---: | ---: | --- |
| Logical 普通逐条构建 | 852.57 ms | 1,574.72 ms | 更早发布值页/索引，单看 Add 阶段确实更慢 |
| Logical 首次快照 | 744.41 ms | 134.38 ms | 复用已准备的值根，不再构建另一套全量对象图 |
| Logical 构建＋首次快照（每轮先求和，再取中位） | 1,606.38 ms | 1,698.57 ms | **增加 92.20 ms，约 5.7%**；不能省略 |
| SubVoice 普通逐条构建 | 2,209.52 ms | 2,485.89 ms | 增加约 12.5%，同样属于成本前移 |
| SubVoice 首次快照 | 1,003.67 ms | 123.25 ms | 同修订共享 |
| SubVoice 构建＋首次快照 | 3,181.09 ms | 2,607.89 ms | 减少约 18.0% |
| Logical 首次快照后 live managed | 318.37 MiB | 47.26 MiB | 包含当前源和基础运行时，非仅 Note 字段 |
| SubVoice 首次快照后 live managed | 486.20 MiB | 62.50 MiB | 同口径 |
| Logical 全流程累计分配 | 5,923.79 MiB | 752.15 MiB | 不是同时占用 |
| SubVoice 全流程累计分配 | 21,830.11 MiB | 2,292.78 MiB | 去除 DTO/嵌套 parse/重复索引与验证字符串 |
| Conductor 全流程累计分配 | 24,787.71 MiB | 5,563.55 MiB | 必要源、页目录、短命 scalar DTO 仍存在 |

取舍：普通大量逐项构建承担值根/地址准备和弱 facade 管理，而不是允许集合永久保留所有可编辑对象，随后再造全量快照。已有大批编辑/adopt 主路径不经过这些逐项 Add。恢复全对象 store 虽可让 Add 看起来更快，却恢复本阶段要消除的长期双份对象图，因此未采用。普通百万 Logical 的约 92 ms 合计代价保留，并由 B02/B03 检查实际操作手感；不能以平均数据覆盖可感知卡顿。

### 4.2 十万级、冷准备与时间回退调查

| 负载/阶段 | 旧中位 | 新中位 | 新版三轮范围 |
| --- | ---: | ---: | ---: |
| 100k Logical 冷 Save Copy | 925.44 ms | 858.78 ms | 856.60～1,119.15 ms |
| 100k Logical 冷 Open | 203.16 ms | 210.35 ms | 202.94～224.98 ms |
| 100k Logical 冷 Resave | 572.36 ms | 397.96 ms | 390.35～423.94 ms |
| 100k Conductor 冷 Save Copy | 2,042.61 ms | 1,824.93 ms | 1,739.19～1,877.96 ms |
| 100k Conductor 冷 Open | 1,091.49 ms | 840.80 ms | 837.31～864.15 ms |
| 100k Conductor 冷 Resave | 970.95 ms | 981.90 ms | 822.00～1,123.91 ms |

十万级 Logical 的普通构建从约 164 ms 到 350 ms，但首次快照从约 218 ms 到 35 ms；合计基本持平。冷 Open 中位约增加 7 ms，Conductor 冷 Resave 约增加 11 ms，均处于这次三轮波动范围内，不宣称它们加速，也不足以证明稳定退化。

中间候选曾出现 100k Logical 冷 Open 203→360 ms、Save Copy 925→1,039 ms，以及 100k Conductor Resave 971→1,070 ms。按 ≥10% 门槛追加了三轮冷进程和三轮同进程预热对照，并审查实在成本：

- 新恢复提前建立 immutable sequence、fingerprint、空间页和地址目录；单调 ID 样本没有进入乱序 exact-index 排序。
- 新大条目先校验 hash，再打开条目恢复，确实多一遍顺序解压；没有跳过校验。替代方案是边解析边 hash，或 owned spool；前者会改变 hash/header/内容损坏的错误优先级，后者增加磁盘副本和清理协议。当前百万规模已更快，未为小规模噪声再引入另一套路径。
- 去掉新写入代码中不必要的逐暂存文件 `Flush(true)`，保留最终 ZIP/备份持久化及每字节自校验；不是用降低正式保存安全性换速度。
- 最终预热 Logical：read 261→193 ms、Save Copy 555→464 ms、Open 213→213 ms、Resave 410→326 ms；Conductor 预热 Save Copy 996→732 ms、Open 495→332 ms、Resave 808→726 ms。预热更快支持冷准备/分层编译等一次成本影响较大的判断，但**没有把全部差异精确归因于 JIT 或上述 flush 修改**；预热结果仅作时间调查，不作峰值内存对照。

100k SubVoice 与 `mixed` 大字段另各跑一轮最终烟测：Peak WS 分别 283.90→85.23 MiB、557.81→133.08 MiB；Save Copy 分别 1,534.57→923.50 ms、3,549.17→2,623.17 ms。单轮不宣传稳定加速倍数。相同输入的所有包条目必须一致。

### 4.3 可追溯原始结果

- 最终生产 DLL：Domain MVID `75f1345c-41c9-4377-9c3b-14ecec4384ae`；Persistence MVID `558d6b21-1d68-47cb-8118-4325751bff13`。与最终公共回归所用 DLL 的 SHA-256 相同。
- 旧版百万：`old-logical-1m-r2/r3/r4`、`old-subvoice-1m-r2/r3/r4`、`old-conductor-1m` 及 `-r2/-r3`。
- 最终百万：`new-<logical|subvoice|conductor>-1m-delivery-1/2/3`。最终十万：`new-<kind>-100k-delivery-1/2/3`，SubVoice/mixed 为 `-1`。
- 旧版十万：Logical `old-logical-100k-r2` 及 `old-logical-100k-repeat-3/4`；Conductor `old-conductor-100k` 及 `-repeat-3/4`。
- 最终预热：`<old|new>-<logical|conductor>-100k-warm-delivery-1/2/3`。
- 每个目录都有 `result.json`、输入编码及 `fixture.midora / resaved.midora`。`Compare-Results.ps1` 可逐对验证 hash 和阶段数据。`final`、`final-r2/r3` 等较早命名属于中间候选，不与交付 DLL 的 `delivery` 结果混为同一矩阵。

## 5. 阶段 3＋4 合并人工验收 B

自动回归覆盖各批量命令及关键组合，用户不必在每种百万对象工程重跑所有工具。请用已有可发声工程及 9KX2 的副本，重点检查以下九项手感和工作流；不要使用上面的 `mixed` 压力夹具编曲。

| 卡号 | 请操作 | 通过标准 |
| --- | --- | --- |
| B01 | Pure MIDI 大选区复制到新位置→Undo；同位置再做一次，再换远处 | 首次/重复无异常长尾，内容和选择准确 |
| B02 | Logical Segment 大选区移动、左边界/右边界改长度，Undo/Redo | 预览、delta、最终长度和隐藏内容正确 |
| B03 | SubVoice 重复上述编辑，保存重开后再改 | 没有漏同步，Loop/Pre-Roll 与事件保留 |
| B04 | 三种钢琴卷帘各挑一两项翻转/Scale/Batch/类人化/量化 | 菜单和快捷键可用，完成后选择正确，Undo 后可继续操作 |
| B05 | Event/Parameter 画线，属性编辑，切 Lane，复制/删除/Undo | 点、高亮、覆盖规则和焦点正常，无嵌套批次报错 |
| B06 | 在较长编辑/保存的准备过程中取消，再继续操作 | 没有部分修改；磁盘原项目仍可打开 |
| B07 | 不同位置播放/Stop/Seek，多次切 Mute/Solo | 无永久 Buffering、旧状态错用或恢复无声 |
| B08 | 含 Logical/SubVoice/Conductor 的工程保存重开，再编辑 | 内容、颜色、映射、Loop、Pre-Roll、Tempo/Marker 保留 |
| B09 | 打开旧格式副本，确认自动备份后原路径保存；另试 Save Copy | 原有升级与副本保护不变，不意外覆盖 |

若遇阻塞问题，留在 B 修复；B 未通过前不进入阶段 5。前两阶段已通过的 A 不要求全部重验。
