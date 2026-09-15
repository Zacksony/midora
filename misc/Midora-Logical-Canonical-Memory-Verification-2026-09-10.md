# Logical 编译结果内存优化：验证与交付报告

日期：2026-09-10。状态：已实施，编译、旧版完整对照、消费者与桌面自动回归通过；待人工验收，已知时间回退见 §5。

**历史范围说明：** 本文记录初次有限内存实现（候选 6）及当时的明显编译时间退化。后续已实施时间回收，当前实现、同日三次对照和验收清单见[时间回收报告](Midora-Logical-Canonical-Time-Implementation-2026-09-10.md)。以下原始数字、预算描述及测试记录不追写为新版本结果。

## 1. 本轮范围

落实 [下一步台账 §3](Midora-Pre-Expansion-Closeout-and-Next-Step-2026-09-09.md) 的 Logical 编译结果内存工作。设计依据与接口边界见 [存储设计](Midora-Logical-Canonical-Memory-Design.md) 和 [消费者设计](Midora-Logical-Canonical-Consumers-Design.md)。

本轮不修改 SRS、Project Format、Mapping ABI、产品版本、编辑行为或 BASS 音频语义；不包含后续极端 Tick/SMF 编码修复，不使用 computer-use，不提交、推送或发布 dist。用户既有未跟踪草稿未读取或修改。

原问题不只是最终 `CanonicalMidiEvent[]` 大：Raw 展开、排序、范围结果、最终数组及音频/MIDI 消费者可能同时保存完整副本。因此本轮从发射到消费一起改造，而非最后包装一个分页接口。

## 2. 已实施路径

| 环节 | 处理方式 | 保持不变的行为 |
| --- | --- | --- |
| Raw 发射与 Mapping 状态序列 | 发射时转紧凑值；共享 pattern，超出驻留预算写分页；模板/状态时间线有界排序 | Context、Source、Loop、Pre-Roll、Mapping 和目标状态语义 |
| Canonical 排序与同 Tick 归约 | 固定大小 run、有限路数归并；跨页持续处理完整 semantic group | 原完整总序、胜出规则、Direct 重复记录、NoteOff FIFO |
| 编译范围 | 流式保留已排序中段；只排序新增的起点恢复/结束清理，再归并 | 冷启动不重触发旧 Note；起点状态和硬结束准确 |
| 最终 Logical 结果 | 小结果内联；大结果不可变页，Tick/Unit 页索引 | 完整事件、来源、fingerprint、统计、诊断，Full/Incremental 一致 |
| 播放/预览/音频导出 | 不再保存大 Logical 的第二份 fragment/scheduled 数组；小型音频元数据缓存及滚动页读取 | 采样映射、事件顺序、范围、音色 preset、Mute/Solo 语义 |
| MIDI 导出 | 准备仅累积有限 Unit/Bank 状态；按 Unit 流式写文件 | 正式 MIDI 字节、原子输出、取消、Pure Track 结构 |
| All Tracks | 读取 Logical 完整分页流；Pure 仍直接显示源音符 | 已定案的混合显示与来源顺序 |

`CanonicalCompiledResult.Events` 现在明确只表示 inline 部分，不得再当作全部内容。正式完整枚举走 `QueryEventPages`；仅分页后备查询和 Pure 专属查询保持区分，避免遗漏或重复发送。生产消费者已逐一审计。小型测试中显式返回完整 MIDI 字节的兼容入口仍会分配整份字节；正式文件输出不使用该入口。

音频元数据首次需要两遍 Logical 流读取，这是为了完整保留既有 fragment 指纹的“先记录数量、后记录内容”字节语义；后台 compilation session 在锁外预热，后续复用。sample-domain 缓存另外纳入 Tempo 映射与原点，避免空事件数组错误命中不同音频；旧缓存可能首次 miss，但不会误 reuse。

## 3. 预算、所有权和失败

- 同次工作共享 resident 页/Raw intern 预算 128 MiB，受计量工作缓冲 128 MiB，分页/排序索引 metadata 64 MiB，owned spill 16 GiB；页容量 4096 records，排序 run 32768 records，最多 16 路归并。索引扩容先预约新旧数组并存容量；避免高压缩率时仅靠物理磁盘配额无法约束页目录的问题。
- spill 页 Deflate Fastest 压缩，不能压小的页原样保存；固定容量编码缓冲不自动扩张，配额按实际文件字节收费。读者按独立文件偏移读取，校验解压长度及原始页 SHA-256。
- 中间排序/范围页显式释放；最终页利用释放后的预算缓存，完全驻留时删除冗余 backing。旧 result/cache/reader 仍合法时不能提前清理其文件。
- 没有有效 owner 后依赖受保护的终结清理；不承诺点击关闭的瞬间磁盘目录一定消失。崩溃遗留遵循既有 owned-temp 启动清理。没有生产强制 GC，也没有削减 Undo 历史。
- 新事务取消、配额或 I/O 失败时先回滚缓存、再释放新存储；清理某个对象失败不会跳过后续对象。若损坏的是旧结果自己的文件，旧读者只能明确失败，不能假装它仍可读；源项目不受影响，可重新编译。
- 以上不是全进程内存上限：源数据、实例/分配、有效旧修订、同时活动 Note 的来源 FIFO、诊断以及 CLR/WPF/原生资源仍另占内存。磁盘空间不足和预算失败应明确报告，而非删音、改顺序或发布部分结果。

## 4. 测量方法与反证

旧基线为 `c02e0bc`，冻结旧编译器 MVID `10fd65c7-51a0-4658-8295-4b9b4f67ae81`；最终候选 6 为 `331a2a8f-d5a4-4309-9a1b-63c1af7348b0`。新旧使用同一独立 Release 探针和 .NET 10.0.11。正式编译对照及最终消费者测量采用 Windows Job 8 GiB Private 进程树硬上限、系统至少 2 GiB 可用余量，串行运行；没有关掉守护来追求更大的样本。消费者早期候选 4 使用过更低的 4 GiB 上限/1 GiB 余量，历史参数保留在消费者记录中，后续已按 8 GiB/2 GiB 重测，不将早期记录冒充最终版本。

探针源码：[编译与完整 oracle](../eng/LogicalCompiledMemoryProbe/README.md)、[消费者准备探针](../eng/LogicalConsumerMemoryProbe/LogicalConsumerMemoryProbe.csproj)。每个完整 digest 都流式覆盖全部事件和 Source 字段、顺序、产品 fingerprint、context、allocation、Conductor、SMF descriptor、opaque 内容及诊断，不是抽样。小型混合 fixture 还比较完整消费者流与 SMF 字节。

编译计时与 oracle 计时分开，峰 Private/WS 涵盖完整进程运行。探针有意同时保留 Full、Incremental、fresh Full 的合法结果，检查旧读者在 cache 清理后仍可读，再在强 owner 离开非内联栈后以受控 GC 验证弱引用。这不是正在真实 WPF 中只保留一个结果时的内存数字，也不是 p95/p99。

阶段探针发现并否定了两个过度简化的解释：

1. **预算越小越好**不成立。32 MiB 候选减少峰内存，却在 525,612 事件样本中产生约 1.30 GB 中间读写。将已排序中段改为归并、预算提高到 128 MiB，明显降低了时间成本。
2. **读写字节减少就会加速**不成立。Deflate 将同样本约 395/385 MB 读/写降低到约 30/29 MB，但热 Full 从 5.31 秒变成 5.74 秒。说明该阶段不是简单的磁盘吞吐瓶颈；压缩保留的理由是显著减少磁盘与峰值内存，而不是伪称它让编译更快。
3. 最终候选将归并队列从搬运完整事件改为只搬 cursor 索引，保留原比较器，52.6 万事件样本相对候选 5 的编译墙钟改善约 8～11%，热 Full 的 materialization 从 2.343 秒降到 1.852 秒；但仍没有消除相对旧数组版的时间回退。这是整个流水线成本，不应再归咎于简单文件 I/O。

上述是各候选的单机实测，不是跨硬件保证。最终数据和回归结果如下，不用较早候选的更低峰值替换最终候选实际峰值。

## 5. 最终实测与回归

### 5.1 六组最终对照

时间单位为秒；内存为 **MiB**（1,048,576 bytes），均是“旧 → 最终候选 6”。Full 是该进程首次编译；Incremental 在一次源改动后编译；fresh Full 是随后用新 compiler 对改后源重新完整编译。它是热进程运行，不代表系统磁盘缓存完全冷或热。

| 场景 | Full 事件数 | 首次 Full | Incremental | 热 fresh Full | 进程 Private 采样峰 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Mixed32（Pure/Logical 混合） | 9,529 | 0.849 → 0.759 | 0.149 → 0.161 | 0.108 → 0.103 | 88.5 → 56.5 |
| Complex64（分页 Mapping/Curve/Loop/Pre-Roll） | 5,766 | 0.549 → 0.594 | 0.184 → 0.210 | 0.110 → 0.135 | 44.5 → 47.4 |
| Shared10k（共享 Usage、多触发） | 120,013 | 1.726 → 1.889 | 0.765 → 0.872 | 0.626 → 0.497 | 294.0 → 391.4 |
| Expansion32（32 × 4 SubVoice × 256 Note × 8 Loop） | 525,612 | 2.783 → 6.506 | 1.751 → 5.183 | 1.716 → 5.168 | 1,706.9 → 1,180.7 |
| Shared100k | 1,200,013 | 6.340 → 16.056 | 4.647 → 14.442 | 4.415 → 14.386 | 3,343.9 → 1,451.9 |
| Expansion128（128 × 4 × 256 × 8） | 2,102,316 | 8.140 → 29.345 | 6.821 → 27.869 | 7.456 → 27.864 | 5,692.8 → 1,338.9 |

结论与限制：

- 两个百万事件场景的采样峰分别降低约 **56.6% / 76.5%**，但热 fresh Full 约为原来的 **3.26 / 3.74 倍**；不能称编译时间基本不受影响。两个较小场景的峰值反而上升，Shared10k 增加约 97 MiB。
- 最大场景的 Full 累计分配从 4,535.3 MiB 降为 1,311.6 MiB；首次 Full 返回瞬间 Private 约 437 MiB。后续峰值包含多个合法结果与 oracle 临时序列化，高于单次编译返回瞬间是预期的测试过程，不能把累计分配、当前保留和进程峰值混称。
- 最大场景首次 Full 的 Raw 展开发生在约 1.825 秒内，materialization 12.569 秒、范围处理 8.640 秒、最终发布/索引/指纹 6.035 秒；总 CPU 30.234 秒，墙钟 29.345 秒。后半流水线 CPU/扫描是明显成本，不能只凭磁盘访问断言磁盘带宽瓶颈。
- 最大场景首/末 512 Tick 窗口完整查询分别从 **53.8 → 16.5 ms、38.0 → 0.39 ms**；索引有实际收益。窗口结果完整摘要相同，不将小窗口提速冒充整曲编译提速。
- 最大场景本次预算内 metadata 峰仅 263,072 bytes，返回时 132,080 bytes；metadata 上限有低额度失败测试，不靠这个样本未触顶推断绝无增长风险。
- 六组完整 oracle、窗口、Full/Incremental/fresh Full 和清 cache 后旧 reader 均一致；关闭后受控 GC 为 **0/5 owner 存活**。CLR Private/committed 高水位并非立即归零，生产中没有强制 GC。

原始记录位于 `.tmp/logical-compiled-memory/runs/`：`baseline-mixed32-v2`、`baseline-complex64-v4`、`baseline-shared10000-v2`、`baseline-expansion32-v3`、`baseline-shared100000-v4`、`baseline-expansion128-v4`，分别对照 `candidate6-mixed32`、`candidate6-complex64`、`candidate6-shared10000`、`candidate6-expansion32`、`candidate6-shared100000`、`candidate6-expansion128`。每组 `result.jsonl` 记录阶段、时间、CPU、IO、分配和所有 oracle，旁边的 `-guard` 目录记录守护与退出结果。二进制冻结清单是 `.tmp/logical-compiled-memory/candidate6/binary-snapshot.json`。这些本机测量产物不加入 Git；可复现工具已保留在 `eng/LogicalCompiledMemoryProbe/`。

### 5.2 消费者准备

最终候选 6 已在 8 GiB/2 GiB 守护下通过独立消费者探针，输入 16 个实例 × 512 个模板 Note × 16 Loop × 4 SubVoice，得到 1,049,260 Events / 524,288 NoteOn / 64 allocations。Compiler MVID 与 §5.1 相同，Playback MVID 为 `f2d8940b-35f6-45e3-8e16-9a02da0bf348`。详细证据见[消费者设计与验证](Midora-Logical-Canonical-Consumers-Design.md)。

| 阶段 | 最终候选 6 |
| --- | ---: |
| 编译 | 15.091 秒 |
| metadata 冷准备（两遍流） | 2.754 秒 |
| metadata 热复用 | 0.1317 毫秒 |
| adapter 首次（metadata 已热）/再次 | 59.563 / 0.863 毫秒 |
| 首/末 4,800-frame 查询 | 16.786 / 0.799 毫秒 |
| Private / Working Set 采样峰（十进制 MB） | 366.90 / 375.65 MB |
| 关闭后受控 GC 的 managed heap | 0.903 MB |

metadata 首次准备额外约占 compiler-only 时间的 18.3%，不能省略这部分来预测正式后台 session 发布延迟。两遍元数据累计分配约 524.5 MB，但并未保留整份事件图：准备前后 Private 约 359.61 / 359.54 MB，adapter 的 resident scheduled 及 fragment scheduled 数均为 0。热复用只保留 source 与小型描述符。记录路径：`.tmp/logical-compiled-memory/consumer-run-candidate6/`；历史候选 4/5 原始记录继续保留，不替换为最终数字。

本探针后另修正了 `ProjectCompilationSession.GetOrCreateRenderPlan` 锁外准备时的过期返回缺口，并补四种并发门；该公开 API 当前没有生产调用，本探针也不调用它。因此不冒称以上 Playback MVID 已包含该最后 API 修正，Compiler 与实际测量路径未变。最终消费者回归包含该修正，见下表。

### 5.3 自动验证与审查

| 验证门 | 结果与版本范围 |
| --- | --- |
| Compiler Release 全套 | 最终候选 6，**525/525**；含页正反序/随机范围、压缩及 raw fallback、损坏/截断、配额/metadata 扩容失败、取消、归并多 run、旧读者及完整旧版语义 oracle |
| Playback / MIDIExport / AudioRender | 候选 6 后补完 sample-plan API 并发修正，最终分别 **155/155、60/60、37/37**，不重复累加早前执行 |
| Desktop Debug 全套 | **434/434**；在候选 6 仅改归并堆前的消费者/桌面最终代码上执行，无钢琴卷帘/Arrangement 绘图代码修改 |
| Application 全套 | Raw/消费者改造期间 **1139/1139**；后续分页额度、codec/归并候选由上述最终 Compiler/消费者门覆盖，未将这次早期整套执行冒充最终二进制重跑 |
| 最终 Desktop Debug 构建 | 全部本轮代码及最后 session API 修正后，**0 警告、0 错误**；普通源码构建，没有运行发布脚本 |
| 独立最终只读审查 | Raw/store 所有权、失败回滚、metadata 配对释放、堆 head 不变量、范围归并与发布未发现新阻塞；主审发现的 sample-plan 锁外构建过期返回缺口已修正并通过可控并发门 |

桌面首次整套曾为 433/434，独立 Onion lifetime 测试也失败，反证已保留。排查确认 `OnionSnapshot` 已清空，但 async 测试帧里的 `Assert.NotNull` 临时参数仍保活投影；把同一非空检查移入现有 `NoInlining` 弱引用采集 helper，未修改产品、增加 GC 次数或放宽断言。复测 Onion 7/7、独立两次通过，最后桌面 434/434。Playback 旧 cleanup 测试也有 Debug async 帧未使用返回值保活，使用 void/NoInlining 边界移除测试临时引用后独立与整套通过，未削弱存活检查。

最终锁外 sample-plan API 会在回锁后复核 source revision、sample generation、已发布结果身份和当前编译状态；任一变化都明确失败且不缓存/返回旧计划，不静默重试。四类受控交错测试覆盖源编辑、sample 失效、只替换发布结果、source revision 不变的手动重编译，并验证下一次正常准备及复用；既有 Dispose 门也保持通过。音频 metadata 预热失败/取消后的 session 已有 Everything/full-recovery 路径，后续不继续使用部分同步的镜像 delta。

Compiler TRX：`.tmp/logical-compiled-memory/candidate6-tests/candidate6-compiler-full.trx`。桌面最终 TRX：`.tmp/logical-compiled-memory/test-results/desktop-logical-canonical-recheck.trx`；初次失败记录 `desktop-logical-canonical-final.trx` 仍保留。本节不把 opt-in 大样本入口在未启用时的普通 Passed 当作大样本实测；实际执行的六组规模见 §5.1。

文档收尾：同步本轮两份设计、交付报告、下一步台账及路线图；23 个本地 Markdown 文件链接可解析，0 个缺失；`git diff --check` 通过。SRS/格式/版本源、未跟踪用户草稿和 `dist` 不在本轮修改范围。

## 6. 已知代价与未覆盖部分

- 大型 Logical 编译相较原完整数组路径有时间代价；需要接受有限内存、分页、归并和校验成本，具体数字以 §5 为准。不要将整个改动描述成所有维度都更快。
- 音频元数据首次预热不是零成本；热重用很快。直接调用 compiler 的消费者首次创建音频计划仍需支付预热，不能只引用编译器计时预测首次播放准备。
- 页级 Unit mask 不是每 Unit 独立文件。若每页都交错包含大量 Unit，逐 Unit MIDI 导出可能重复解码同一批页；本轮未新增另一套逐 Unit spill 副本。
- 同时活动音符 FIFO 保留精确来源、实例及 allocation 仍与音乐规模有关，不能承诺任意输入全进程恒定内存。
- 本轮用合成有效 Logical/混合夹具覆盖展开与消费；没有把旧 9KX2 原样重新跑一次真实跨类型粘贴/WPF 录屏，也没有声称验证了真实声卡播放、全部 SF2 听感或编辑帧率。未触碰钢琴卷帘/Arrangement 的绘图和选择代码。

## 7. 建议人工验收

1. 用现有大项目将一批 Pure MIDI 音符粘到 Logical Segment；等待编译完成，观察内存、编译时间，以及再编辑一个 Note 后是否继续无限增长。
2. 使用含 Loop、Pre-Roll、Mapping/逻辑参数的事件乐器：从头和中途播放，确认音符与状态和改前一致；检查准确的 Notes/Events 统计。
3. Undo/Redo 后打开 All Tracks 的 Compiled 模式，并切换回普通视图；确认内容更新、不缺音、不出现旧修订混合。
4. 做一次混合 Pure/Logical 的 MIDI 整曲及分轨导出、一次短范围音频导出；取消一次准备任务后重新执行，确认可继续使用。
5. 关闭项目再打开另一个项目；等待空闲后观察内存与临时文件是否回落。Working Set 未立即归零不单独判定泄漏，重点看重复周期是否持续线性增长及旧文件是否长期被合法 owner 保留。

本轮人工验收结束后，下一项仍是独立的极端 Tick/SMF 编码防护，不自动进入新功能或本地发布。
