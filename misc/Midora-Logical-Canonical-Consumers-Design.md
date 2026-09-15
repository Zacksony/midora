# Logical canonical 消费者有界存储设计

日期：2026-09-10。需求依据：SRS §12.17、§12.21、§12.23、§12.25、§13.1、§14.1、§15.1、§18.11；本记录不变更音乐语义。

后续状态：音频 metadata 归属查找的无效分配已去除；当前复测见[时间回收报告](Midora-Logical-Canonical-Time-Implementation-2026-09-10.md)。本文候选 4/5/6 的数字保留为初次内存交付的历史记录，不代表时间回收后的速度或分配量。

## 输入、输出与边界

- 输入：已按正式完整顺序冻结的 Logical canonical 事件、既有 inline 小结果、Pure MIDI 延迟 source 与同次编译的 allocation/descriptor。
- 输出：相同事件、来源、顺序、统计、fingerprint 与消费者行为；物理页面不是新的音乐边界。
- Logical source 与 Pure MIDI source 独立，不能把 `HasPagedEvents` 解释为 Pure MIDI 专属标记。
- Logical 查询只读取已编译结果，不重复映射、生命周期、恢复或 Reset。起点恢复仍在范围编译中完成。
- 所有查询只持有活动页，enumerator Dispose/取消释放 reader；结果与 playback view 共享不可变 source 所有权，不能在历史缓存逐出时提前关闭仍被消费者引用的 source。
- MIDI 导出逐 Unit 顺序枚举；音频用滚动 page provider，All Tracks 逻辑展开读取 Logical cursor，不触碰未请求的 Pure MIDI 数据。
- 无 Project Format、音频时序、UI、SMF delta/大小编码语义改动；本轮不提交、推送或本地发布。

## 接口与验证门

`ILogicalCanonicalEventSource` 提供准确计数、范围枚举与 retained-storage 去重统计。小结果保留 inline fast path；大结果不通过兼容 `Events` span 强制全量物化。

消费者需覆盖：整曲/范围播放与预览、Mute/Solo 恢复、MIDI 整曲/分轨/分 Port、音频文件、All Tracks FIFO 展开、统计、同输入 Full/Incremental。重点验证相同 tick 排序、EndTick 精确清理、shared Usage Track 来源、缓存指纹、取消及旧结果继续可读。

## 消费实现与所有权

- `Events` 只表示小型 inline 常驻记录，不是完整结果。完整语义枚举使用 `QueryEventPages`；播放滚动 provider 合并 Logical 与 Pure paged 数据，inline Port schedule 只包含 inline 数据，避免重复发送。
- `QueryMidiRenderEventPages` 与 `QueryPagedEventPages` 仍只返回物理分页部分；`HasPagedPureMidiEvents` 与 `HasPagedLogicalEvents` 独立。Pure SMF Track 查询不扫描 Logical store；Logical MTrk 使用 Unit 索引跳过无关页面，不按每条 Track 复制完整事件集合。
- 大型 Logical 的音频 fragment 只保留身份、范围、来源、preset 集合及流式指纹，不保留第二份 canonical / scheduled 事件图。为保持旧 fragment V1 字节语义，准备阶段做计数与哈希两遍不可变流；全部 fragment 哈希缓冲合计不超过 8 MiB（单个不超过 32 KiB），不构造完整序列化 MemoryStream。
- 时间回收保留两遍流和原 count-prefix 哈希格式；`ResolveBuilder` 每事件精确归属字典路径不再创建闭包，fallback 以一次遍历统计候选，不生成数组，仍保留唯一归属及多候选拒绝。没有以 FirstOrDefault 或删字段换速度。
- 元数据由结果自身拥有，默认播放视图共享同一个小型缓存；没有 static 强引用旧结果。retained-storage 统计包含已完成元数据，统计不等待进行中的 factory。取消或失败的 factory 会移除，不污染以后准备。
- 正式后台 compilation session 在发布前、UI/project 锁外预热元数据。直接使用 compiler 的导出/预览调用首次需要音频计划时懒准备，因此仍承担两次流式读取的成本；重复准备复用元数据，不重新扫描远处 Logical 内容。
- 分页 fragment 不含 scheduled 数组后，其 sample-domain PCM 指纹另显式包含 Tempo 映射与 Tick 原点，不能仅靠总 frame 数判断音频等价；canonical fragment fingerprint 不变。这会让首次采用新分页表示的旧 PCM 不命中，但不会错误复用不同 Tempo 的音频。
- Monitoring Note balance 按当前结果和前进 Tick 增量维护计数，不保存所有 Note 事件；首次/倒退才重扫前缀。计数游标仅弱引用 canonical，查询结束释放读页器。
- MIDI 导出准备只累计最多 256 Unit 的集合与 Bank/Program 校验状态；正式输出保留原子写事务，按 Unit 流式写，取消传播到准备和记录枚举。用于测试/明确请求完整字节的 `FileBytes` 兼容入口仍会物化整个 MIDI，不是正式文件写入路径。
- MIDI 导出准备的源归属 fast path 同样不分配捕获完整事件的闭包；无显式源 Track 的生成事件直接遍历冻结 allocation 判断唯一 Track，不复制/排序 allocation 数组。保持原半开范围及 endTick 特例，同 Track 多 allocation 只算一个 owner，缺失/歧义诊断和 Source 不变。
- Unit 页面索引降低无关页读取，但并非独立的逐 Unit 排序副本；若极端项目的每页都混合全部 256 Unit，逐 Unit MTrk 输出仍可能重复扫描同一批页。这是当前时间上界的已知取舍，不宣称逐 Unit 导出恒定一次扫描。
- 音频文件准备将取消 token 传入 Logical 元数据准备；session sample-plan 投影在锁外，回锁时复核 sample generation、source revision、已发布结果引用及当前编译请求完成状态。任一变化都明确拒绝该过期 plan，不缓存、不返回、不静默重试；下一次正常准备仍可成功。关闭 session 不等待消费者投影持锁。既有接受显式 canonical 的 realtime base-plan 路径未在这个 API 修正中改变。
- 后台 metadata 预热失败、取消或结果过期不会丢失镜像恢复信息：完成过 mirror materialization 的失败尝试保留 `Everything` pending changes 并标记 full recovery，下一次建立完整镜像、执行 Full Compile，不把已经前进的 compiler cache 误作旧 revision 的增量基线。
- All Tracks 的 Logical FIFO 展开使用上述完整 Logical cursor；Pure MIDI 源仍使用已定案的直接源音符展示，不改变混合视图定义。

## 验证记录

- 新增 `LogicalPagedConsumerTests`：同一冻结结果仅改变 Logical 物理表示，验证整曲与非零范围音频 scheduled 完整序列、137-frame 滚动窗口、source/preset、canonical fragment 指纹、整曲/单 Logical SMF 字节一致；混合 fixture 保留 Pure inline 与真实 NoteOff velocity。
- 覆盖总长度相同但内部 Tempo 不同的 PCM 身份，及取消元数据准备后可再次成功构建和复用。
- 最终候选6重新构建并运行消费者全套：Debug Playback 151/151、MIDI Export 60/60、AudioRender 37/37。随后补齐 sample-plan 锁外准备的过期结果拒绝：源编辑、sample generation 失效、仅已发布结果替换、source revision 不变的手动重编译共四个受控 blocking-source case；断言旧 plan 不入缓存、下一次准备正常，最终 Debug Playback 155/155，原 Dispose 门仍通过。AudioRender 在该 API 修正后也再跑 37/37（该消费者不调用此 API）；早前 Release cleanup 与新消费者门合计 9/9。Application 1139/1139（由并行审计执行，覆盖 CompiledOnionNoteIndex）。
- 既有 `MemoryStage6CleanupTests` 的 Debug 偶发失败来自测试自身丢弃 `ApplyReversibleEdit` 的 canonical 返回值，Debug async/JIT 可让这个未使用的临时值跨 await 保活。A/B 调查中四个已知 owner（session、worker Task、lease、exception）的受限临时反射遍历未发现强引用路径，Release 原门通过；仅把排队调用置于 `[NoInlining] void` helper 后，Debug 门及 151 项全套通过。没有更改产品释放行为、断言、GC 次数或等待阈值；临时反射诊断已删除。
- Desktop 全套发现 `OnionLifetimeTests.UndrawnTrackOnionReleasesProjectionWithoutChangingItsPresetOrMusic` 的类似弱引用断言失败，独立 Debug 运行也可复现。只把最初读取 snapshot 的非空断言移入既有 `[NoInlining] ProjectionReferences` helper，使该强引用和断言参数临时值离开 async 测试帧后再进行 GC，Onion lifetime 7/7 及另两次独立重跑均通过。没有修改产品 Onion 所有权、GC 次数或弱引用断言；最终 Desktop 全套结果由主报告记录。
- 后续完整套件、性能测量和未解决边界统一汇总至本轮主报告；本记录不把尚未运行的验证标记为通过。

## 初次内存交付的消费者预热实测（历史候选）

`eng/LogicalConsumerMemoryProbe` 重用冻结语义样本构造器，在独立进程测量 compiler → metadata → adapter。最终候选6复测使用 `guard OUTPUT 8192 2048 EXECUTABLE run OUTPUT ...`：8 GiB private 上限、至少 2 GiB 系统可用内存余量，100 ms 采样；没有触发 guard。不是 WPF 或实际 BASS 发声测试，不应解释为完整应用进程峰值。

最终候选6的 compiler MVID 为 `331a2a8f-d5a4-4309-9a1b-63c1af7348b0`，Playback MVID 为 `f2d8940b-35f6-45e3-8e16-9a02da0bf348`；resident/working 默认各 128 MiB、spill 16 GiB、独立 metadata 64 MiB。记录位于 `.tmp/logical-compiled-memory/consumer-run-candidate6/`。样本输入参数为 16 个实例 × 512 个模板 Note × 16 次 Loop × 4 个 SubVoice，正式结果 1,049,260 Events / 524,288 NoteOn / 64 allocations。

该冻结 probe 早于随后 `ProjectCompilationSession.GetOrCreateRenderPlan` 回锁拒绝过期结果的修正，记录的 Playback MVID 不代表该 API 修正后的程序集。Probe 直接调用 compiler、metadata 与 adapter，完全不调用该 session API；Compiler MVID 和被测路径未因此改变，原始测量仍适用。API 并发正确性另由上述最终 155 项 Playback 测试验证。

| 阶段 | 实测时间 |
| --- | ---: |
| 编译 | 15,091.171 ms |
| metadata 冷准备（两遍流） | 2,754.169 ms |
| metadata 热复用 | 0.1317 ms |
| adapter 首次（metadata 已热） | 59.563 ms |
| adapter 再建 | 0.863 ms |
| 起点 4,800-frame 页查询 | 16.786 ms |
| 尾部 4,800-frame 页查询 | 0.799 ms |

因此在这个单次样本中，首次预热约增加 18.3% 的 compiler-only 时间，不能把裸 compiler benchmark 当作最终 session 发布延迟；其收益是首次播放不再重新扫描远端 Logical 内容。两遍 metadata 累计分配约 524.5 MB，但不是保留占用：该阶段 private memory 从约 359.61 MB 到约 359.54 MB，事件图没有累计保留。全进程 guard 峰值 private 366.90 MB / Working Set 375.65 MB；关闭后强制 GC 的 managed heap 为 0.903 MB。编译存储 metadata 峰值 139,440 bytes、retained 73,920 bytes。Adapter 的 resident scheduled 及 fragment scheduled 均为 0，复用仍只保留分页 source 与小型描述符。

历史候选4记录仍保存在 `.tmp/logical-compiled-memory/consumer-run-final/`：compiler MVID `1ae46537-9247-436a-8d85-b88c221c8372`，当时真实 guard 为 4 GiB private 上限 / 1 GiB 系统余量，不能冒充 2 GiB。其同样本编译 15,682.191 ms、metadata 冷准备 2,759.374 ms、热复用 0.1155 ms、private 峰值 365.78 MB。历史候选5记录位于 `.tmp/logical-compiled-memory/consumer-run-budget-final/`，compiler MVID `810573f4-89aa-49e7-8239-220ed0801c26`，真实 guard 8 GiB / 2 GiB，编译 15,819.766 ms、metadata 冷准备 2,925.367 ms、private 峰值 386.09 MB。单次历史差异不作为统计性加速或倒退结论。
