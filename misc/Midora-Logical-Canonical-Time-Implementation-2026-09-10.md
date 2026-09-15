# Logical canonical 时间回收：实施记录

日期：2026-09-10。状态：已实施，同日时间/内存对照、实际流式导出及完整自动回归通过；待集中人工验收。

本报告归档 **LC-T1/T2** 的实现与实测。其后用户通过隔离实验确认进一步收益，并批准 LC-T3 正式实施；当前紧凑热路径、加固与复测见[LC-T3 记录](Midora-Logical-Canonical-Compact-Implementation-2026-09-10.md)。下文未实施 LC-T3 的描述仅表示本报告交付当时，不覆盖后续状态；历史基线和数字不改写。

## 需求追溯与不变量

用户批准实施 [专项分析方案](Midora-Logical-Canonical-Time-Optimization-Analysis-and-Plan-2026-09-10.md)。输入仍为完整 Project/CompilationRequest，输出事件、Source、同 Tick 顺序、Range/FIFO/Reset、分配、诊断及所有音乐 fingerprint 不变；Full/Incremental 必须完全相同。依据 SRS §12.11～12、§12.17、§12.21～25 与 INV-010/015/020/025/066。

不改 Project Format、SRS、Mapping ABI、音频 DSP、编辑/UI、极端 Tick 或 SMF 导出边界。保留上一轮有限内存改造；不提交、推送、发布或使用 computer-use。

取消、预算不足、存储损坏或 I/O 失败仍明确失败，不发布部分 canonical，也不释放其他合法结果的 owner。私有页、页目录与音频 metadata 只属于运行时，写 ProgramRoot `.tmp/CompilerRuns`，不进入项目；音乐诊断及计数不变。性能遥测不进入正式结果身份。

## 私有页完整性决定（LC-T1）

采用 SHA-256(type identity || versioned descriptor || stored payload)，压缩页校验实际压缩字节，raw fallback 校验全部 raw。描述符绑定版本、record size/count、decoded/stored length、codec 和页 offset；descriptor 属于同进程 immutable 元数据，不增加可持久化格式。读取先验证描述符和预算，再读取、校验并精确解码。损坏/截断/非法解码明确失败，不跳页、不重试或发布部分结果。

此决定保护完整存储表示；不宣称校验能够检测 codec 自身错误地产生另一份同长度结果。codec 作为受信实现，通过独立往返、高熵/raw fallback、错误流/长短解码、完整旧 canonical oracle 检验。源 pack、项目、canonical/音频 fingerprint 的算法不变。

## 1. 已实施的时间回收

| 环节 | 实施 | 正确性与资源边界 |
| --- | --- | --- |
| 临时页校验 | 上述完整 stored SHA + 描述符 + exact decode | 每次实际 spill 重读都校验；raw fallback 不例外，损坏明确失败 |
| canonical 排序 | Role/Origin 改为底层整数比较，去除枚举装箱；canonical run 131,072、fan-in 32 | 原完整总序不变；Raw/一般 sorter 仍 32,768/16；所有读者、List 扩容容量计入原工作预算 |
| Range 中段 | 记录 immutable source 的 ordinal 窗口，不再建立完整 middle store；超出 endTick 即停止正序扫描 | 起点状态/FIFO/硬末端及 Direct 精确 NoteOff 保留；同 endTick 群内非 endpoint 仍筛除 |
| 最终索引 | 写出最终胜出事件时同步生成页 Tick/Unit 摘要与 NoteOn 计数 | 不为构造目录重新遍历/解压整份结果；目录受 metadata 预算约束 |
| 发布与预热 | 必要的统一升序 fingerprint 遍历同时按 resident 预算提升最终页 | 不分别 hash Pure/Logical 后拼接；不改字段与字节顺序；完全驻留才释放冗余 backing |
| FNV 原语 | 把整数高位连续零字节对应的乘法合并为预计算的模 `2^64` 幂 | 每个整数仍等价地贡献原来的 8 个 little-endian 字节，不省略字段、不改 hash 版本 |
| 音频 metadata | 精确归属字典 fast path 无闭包；fallback 单次枚举，不分配候选数组 | 保留唯一归属、多候选失败和原两遍 count-prefix fragment hash，不引入第二份完整事件图 |
| MIDI 导出准备 | 源 Track 已明确时不分配闭包；生成事件的 fallback 只扫描冻结 allocation，判断唯一 Track | 保留原区间/硬末端和歧义诊断；同 Track 多 allocation 不误报，多 Track 不擅自取第一项 |

### 与分析方案的实现差异

- LC-T1、LC-T2 的必要目标已落实。Range 没有改成另一套正序 writer，而是保留已验证的逆序 winner/boundary merge，用不可变源窗口去掉中间复制。最终 rangedStore 仍是独立结果，使短范围结果不会长期保活全曲 source。这是减少重复处理但保留既有音乐状态机的实现选择，不是删掉 Range 校验。
- 最终索引在写入时构建，正式 fingerprint 仍有一次必要升序遍历；不是宣称零扫描。元数据计数/哈希两遍也保留，避免改变已有消费者 identity。
- **T1/T2 交付时未实施条件项 LC-T3。** 当时两类大场景的同日三次中位数已达到旧数组版 1.25 倍以内目标，故将全链紧凑 canonical/source 重写暂缓，而非删掉该候选。全部 Source 字段仍完整保存；后续批准与实施见本文顶部链接。
- resident/working/metadata 仍为 **128/128/64 MiB**，spill 仍为 **16 GiB**，没有提高到 512 MiB 驻留换取成绩。

## 2. 测量方法与可复核基线

- Windows x64、同机 .NET Runtime 10.0.11、Release，重型构建/测试/探针串行。守护采样间隔 100 ms，Windows Job private commit 硬上限 8 GiB，系统可用余量 2 GiB。守护均未触发，不放宽上限追求更大样本。
- 旧数组基线：HEAD `c02e0bc` 的冻结 Compiler MVID `10fd65c7-51a0-4658-8295-4b9b4f67ae81`。
- 初次有限内存候选 6：Compiler MVID `331a2a8f-d5a4-4309-9a1b-63c1af7348b0`；其数字是历史单次，用于表明此前退化，不冒充同日三次统计。
- 本轮最终产品：Compiler MVID `11d67bd0-3b07-492c-9f59-3915fad63d64`，Playback MVID `69bf8379-92a1-4ea4-873e-6d139d101264`，MidiExport MVID `fba3c95d-6d1c-48e5-bc4c-590f69e4baec`。最后一项导出归属分配修正没有改变 Compiler/Playback，先前两类大编译基准及消费者基准仍对应最终产品。
- 旧数组版和本轮版的两组重点样本都各跑 3 个独立进程，每进程测首次 Full、单个源编辑后的 Incremental、独立 Compiler 的 fresh Full；同时验证旧结果在清缓存后仍可读。
- 首次 Full 包含冷 JIT/初始化；fresh Full 是热进程内新 Compiler，**不是清空操作系统文件缓存的磁盘冷启动**。三次只报告 min/median/max，不能推导 p95/p99、最坏耗时或跨硬件保证。
- 完整 oracle 摘要覆盖全部事件/来源/顺序、产品 fingerprint、上下文、分配、Conductor/SMF/opaque 描述符及诊断，不是抽样，也不是只比较 Note 数量。oracle 工作不计入 compile 计时，但计入进程峰值。
- 探针故意保留 Full/Incremental/fresh 三个合法结果，最后在强 owner 离开 helper 后受控 GC 测回收；测试 GC 不进入产品正常流程。Private/Working Set 不是单个结果的大小。

## 3. 大场景时间：旧数组与本轮同日三次对照

单位：秒；括号内为 **最小～最大**，括号前为中位数。

| 样本 | 阶段 | 旧数组 | 本轮 | 中位数相对旧数组 |
| --- | --- | ---: | ---: | ---: |
| 高展开：2,102,316 Events / 1,048,576 NoteOn | 首次 Full | 10.502（8.070～12.489） | 8.897（8.709～9.979） | −15.3% |
| 同上 | Incremental | 7.156（6.992～8.486） | 7.973（7.173～11.724） | +11.4% |
| 同上 | 热 fresh Full | 6.950（6.930～7.867） | 7.692（7.094～9.827） | +10.7% |
| Shared100k：1,200,013 Events | 首次 Full | 6.366（6.265～6.415） | 5.914（5.876～6.062） | −7.1% |
| 同上 | Incremental | 4.667（4.606～4.706） | 4.144（4.113～4.254） | −11.2% |
| 同上 | 热 fresh Full | 4.642（4.429～4.684） | 4.073（4.060～4.119） | −12.3% |

高展开第 2 个最终进程的 Incremental 为 11.724 秒，fresh 为 9.827 秒，明显高于另外两次；本轮不把它删作离群点，也不在缺乏系统级归因时全部归因于环境。**中位数达到目标不表示每次都能保证只慢 11%。** 首次 Full 旧版自身也较抖，不能把该列优势直接推广为所有冷启动都更快。

### 相较初次有限内存版的回收

下表左列为历史候选 6 单次，右列为本轮三次中位数，不是严格同日 A/B 统计：

| 样本 | 阶段 | 初次有限内存版 | 本轮 |
| --- | --- | ---: | ---: |
| 高展开 | Full / Incremental / fresh Full | 29.345 / 27.869 / 27.864 s | 8.897 / 7.973 / 7.692 s |
| Shared 100k | Full / Incremental / fresh Full | 16.056 / 14.442 / 14.386 s | 5.914 / 4.144 / 4.073 s |

此前约 3～4 倍的编译耗时不是有限内存必须付出的固定代价；本轮删除了重复处理、无效分配并优化有界排序。热 fresh Full 相比历史候选分别降低约 72.4% 和 71.7%，但衡量相对旧数组的剩余代价应使用上面的同日表。

## 4. 内存与读取成本

每组仍为三个进程的中位数；MiB 为 `2^20` bytes。分配是累计值，不是驻留峰值。

| 指标 | 高展开旧数组 | 高展开本轮 | Shared 100k 旧数组 | Shared 100k 本轮 |
| --- | ---: | ---: | ---: | ---: |
| 整个探针 Private 采样峰 | 5,174.6 MiB | 1,020.1 MiB | 2,441.3 MiB | 1,486.2 MiB |
| Full 累计分配 | 4,755.6 MB | 1,121.8 MB | 2,725.9 MB | 1,194.1 MB |
| fresh Full 累计分配 | 4,754.6 MB | 1,121.1 MB | 2,704.0 MB | 1,172.1 MB |
| 首次窗口查询 | 56.05 ms | 12.49 ms | 41.04 ms | 11.15 ms |
| 尾部窗口查询 | 38.64 ms | 0.53 ms | 23.71 ms | 0.66 ms |

高展开本轮 Private 峰的三次范围是 **985.1～1,600.5 MiB**，不是每次固定 1 GiB。各组同时保留三个结果并执行完整 oracle，不能用这个表声称真实 WPF 处理任意工程的内存上限。

高展开最终版的 tracked 峰为 resident 133,961,280、working 63,242,240、metadata 263,072 bytes，均在原预算内。一次 fresh Full 的 I/O 计数约读 131.4 MB、写 95.4 MB；这是进程 I/O 字节计数，不是物理磁盘吞吐。返回时最终/缓存保留 resident 约 133.7 MB、spill 约 29.5 MB，超出驻留预算部分仍在磁盘，不伪称全部已驻留。

源模型、实例/分配、活动 FIFO、诊断、合法旧 owner、CLR/WPF/原生内存不受上述三项预算统一封顶。这些保留原因与初次内存设计相同，本轮没有裁剪历史、缩短音符、截断来源或添加产品强制 GC。

## 5. 消费者准备：没有把编译成本藏到首次播放

同机独立消费者探针跑 3 次：16 instances × 512 template notes × 16 loops × 4 SubVoices，正式结果 **1,049,260 Events / 524,288 NoteOn / 64 allocations / 4 fragments**。

| 环节 | 本轮中位数 | 最小～最大 |
| --- | ---: | ---: |
| Compile | 5,456.03 ms | 5,274.13～5,909.14 ms |
| metadata 首次准备 | 1,591.18 ms | 1,583.31～1,625.96 ms |
| metadata 复用 | 0.1514 ms | 0.1360～0.2135 ms |
| Adapter 首次，metadata 已热 | 51.51 ms | 51.34～67.71 ms |
| Adapter 再建 | 0.7195 ms | 0.7165～0.8445 ms |
| 起点 4,800-frame 窗口 | 14.44 ms | 14.31～19.49 ms |
| 尾部 4,800-frame 窗口 | 0.5461 ms | 0.5261～1.1858 ms |

metadata 累计分配三次均为 **4,126,304 bytes**，历史候选 6 为 524,527,288 bytes；Adapter 首次累计分配 205,056 bytes。去掉的是 per-event 闭包/候选数组，不是正式音乐内容。`residentScheduled`、`fragmentScheduled` 仍均为 0，metadata 没有重新保存完整事件数组。尾部查询返回 0 个事件，起点查询返回 232 个事件；尾部指标证明空窗口定位成本，不代表密集窗口吞吐。

历史候选 6 单次 Compile 15.091 秒、metadata 首次 2.754 秒、Adapter 59.56 ms；本轮三项中位数相加约 **7.10 秒**，历史约 **17.91 秒**。这是 compiler → metadata → adapter 的准备路径，不包含真实 WPF、BASS preset 加载或声卡启动，不能当作完整点击播放延迟。当前 metadata 仍需约 1.59 秒，报告不会把 compiler-only 当成已准备完毕。

消费者最终进程 Private 采样峰约 288～300 MB、Working Set 323～337 MB，关闭后受控 GC 的 managed heap 约 0.903～0.911 MB；三个守护均未触发。

## 6. 补充样本与实际 SMF 写出

### 6.1 小/中规模与混合路径

以下是最终版每组 **单次** 完整对照，不是三次统计；旧列是已冻结历史数组版单次，不能用于百分比保证。单位 ms。

| 样本 | 正式 Events | 本轮 Full | 本轮 Incremental | 本轮 fresh Full | 历史旧数组 fresh Full |
| --- | ---: | ---: | ---: | ---: | ---: |
| Expansion32 | 525,612 | 3,315.48 | 1,895.23 | 1,666.23 | 1,716.04 |
| Shared10k | 120,013 | 1,903.04 | 641.63 | 576.02 | 626.49 |
| Mixed32 | 9,529 | 926.56 | 171.89 | 89.25 | 108.38 |
| Complex64 | 5,766 | 638.78 | 201.94 | 133.59 | 109.51 |

六类夹具均完成最终版与完整旧 oracle 的一致性校验；每个进程关闭检查 `alive=0 / owners=5`，没有触发内存守护。Complex64 仍有约 24 ms 的单次热编译差异和约 1.37 MB 的额外累计分配；不能概括成所有小场景都更快或内存都更少。非零短范围、远后缀、混合 Direct endpoint 等另由正式 Range/消费者测试验证。

### 6.2 真正流式写文件，而非只计准备 API 返回

使用与 §5 相同的百万事件 fixture、4 个 Logical Unit，CompilationPurpose=MidiExport。最终版运行 3 个独立进程，每个将同一冻结结果写出两次，使用正式内部 `MidiExportEncodingResult.WriteTo(Stream)` 和 `FileStream.Flush(true)`；不使用 `FileBytes` 或完整 MemoryStream。临时探针通过反射建立 typed delegate 访问既有内部方法，不扩大产品公开接口。

| 环节 | 中位数 | 最小～最大 | 累计分配 |
| --- | ---: | ---: | ---: |
| Compile | 5,307.84 ms | 5,252.74～5,354.27 ms | 约 486.6 MB |
| 导出准备/校验 | 492.03 ms | 487.09～498.62 ms | 每次 1,988,360 bytes |
| 首次流式写文件 + Flush | 772.27 ms | 749.26～809.22 ms | 每次 7,991,128 bytes |
| 再写同一冻结结果 + Flush | 677.41 ms | 665.45～698.70 ms | 每次 7,991,096 bytes |

输出每份 **4,197,281 bytes**，六次文件 SHA-256 均为 `0561babf9ec520808f8147df558877597e4c9911708e383b2b872db87acd3de9`。文件 hash 在计时后另算，不掺入写文件计时。Private 采样峰约 340～341 MB，guard 未触发。该表不包含多文件原子发布/README 工作；这些由正式导出任务测试覆盖。

首次实际导出补测发现 `ResolveTrackId` 的 lambda 捕获完整 CanonicalMidiEvent，使已有源 Track 的 fast path 也先分配闭包，百万事件准备累计分配 **271,022,936 bytes**。消除捕获并用一次冻结 allocation 遍历保留相同唯一 owner 判定后降到上述 1,988,360 bytes；同日修正前三次准备中位数为 535.49 ms，修正后 492.03 ms。原始六次文件 hash 与修正后的六次也完全一致。这是去除无效分配，不是取消校验或将它延迟到输出之后。

未冻结旧数组版的同版本 MidiExport DLL，因此**没有声称整份旧数组导出器与本轮版的性能 A/B**。旧/新物理 canonical 表示的 SMF 字节等价由 `LogicalPagedConsumerTests` 验证；百万事件文件 SHA 验证最终流式 writer 的重复确定性及上述归属优化前后一致性，证据范围分开。

## 7. 正确性回归与失败处理

最终 Release 自动套件：

| 测试项目 | 通过 / 总数 |
| --- | ---: |
| Compiler | 548 / 548 |
| Playback | 155 / 155 |
| MIDI Export | 62 / 62 |
| AudioRender | 37 / 37 |
| Application | 1,140 / 1,140 |
| Desktop | 434 / 434 |

去重合计 **2,376 项**；Desktop Onion/AllTracks 的 17 项过滤重跑已包含在 434 中，不重复累加。常规套件 Passed 不等于所有 opt-in 极端规模测试都运行，具体大规模实测以本报告的独立探针为准。所有正式测试保持原业务预期，没有用修改 expected 接受音乐差异。

最后的 MIDI 归属分配修正后，MIDI Export 62 项、Playback 155 项、Application 1140 项、Desktop 434 项均重新通过；Compiler/Playback/MidiExport 最终 DLL 的 MVID 已从本机构建产物直接复核，与被测基准一致。

测试收尾另为受控 finalizer gate 增加“提前失败后 gate 已 Dispose”的异常保护，避免测试自身的失败升级为测试进程崩溃；随后迁移与弱生命周期相关 13 项重新通过，包含在上述 Application 总数中。该保护仅在测试代码，正常断言、产品清理和等待行为未改变。

本轮重点新增/强化：

- 4095/4096/4097/8201 records、预建与扫描两种目录路径；小结果/分页及不满页。
- 描述符破坏、raw/高熵 fallback、payload/截断/checksum；重算有效 stored SHA 后的非法压缩流、过短/过长解码，确保不仅在 checksum 层提前失败。
- 短范围不读取已超出 endTick 的远后缀；同 endTick Direct endpoint 夹杂非 endpoint，保留真实 NoteOff velocity；冷启动、held preview 和硬边界。
- FNV 12 类边界及 100,000 个可复现随机组合，对原 8-byte 算法逐项校验；完整 canonical fingerprint 另有旧 oracle，不能仅靠原语门。
- 20 轮正常/编辑/取消/语义失败/恢复/关闭，共 100 个 Project/Compiler/result 弱引用均清空；另 20 轮 spill abort 释放配额及 owned 文件，合法旧 reader 始终可读。
- 旧 oracle 对照覆盖全部正式事件/来源；消费者测试比较完整 scheduled 流、fragment/hash、范围、SMF 字节，All Tracks 所有权及来源回归通过。
- MIDI 导出归属查询新增 10,000 次 fast-path 分配增长门，以及 1,000 组可复现边界/重复/歧义 allocation 对原排序去重算法的等价比较；错误码、消息和 Source 也保留。

### 中间失败没有省略

1. Compiler 中途一次全套出现既有 `GeneratorExpressionProgramTests.CandidateEvaluationDoesNotAllocatePerIteration` 的 712-byte 非零结果。立即单独重测通过，最终 548 项完整套件也通过。本轮没有修改生成器/expected；该单次额外分配的具体来源尚未证明，不能声称已定位并修复。
2. Application 首次完整套件 1138/1139，`BoundedLogicalParameterMigrationTests` 在业务断言完成后的临时父目录删除遇到仍打开的 owned lock。新增受控 finalizer gate 测试证明：短 WeakReference 在终结器运行前已清，Project.Close 不能取得该对象同步 Dispose，而已排队终结器仍持有文件。仅在测试 Fixture 删除自建父目录前 `GC.WaitForPendingFinalizers()`，不加产品 GC、重试或异常吞掉。最终 Application 1140/1140，包括该受控证明。此证据确定了可造成原收尾失败的时序，不把测试目录即时删除要求误作产品资源必须同步回收的契约。
3. Desktop 构建曾因用户正在运行的 `Midora (19600)` 暂时锁定输出 DLL 而产生 MSBuild 复制重试 Warning，随后构建成功，17 项相关门和 434 项全套均通过。本轮未强制终止用户进程。

## 8. 剩余边界与人工验收

- 高展开仍有约 11% 热编译中位数代价和更大的单次波动；未测其他 CPU/磁盘，不能保证所有工程相同。
- T1/T2 当时的 canonical 全字段大记录仍存在；该热点后由 LC-T3 紧凑存储处理，完整公共事件和来源契约未缩减。其他极端热点仍需依据新证据继续，不回退到无界数组。
- 多 Unit 的 MTrk 流式输出可能按 Unit 重扫共享页；本轮不额外永久保存完整 Unit 投影。后续若该成本成为实际阻塞，再做独立有界索引/spool 优化。
- 没有测试实际声卡/SF2，也没有操作真实 WPF 大型编曲界面；自动组件验证不冒充人工体验验收。

用户只需集中验收以下四条，不要求重跑所有功能：

1. **小型事件乐器**：创建音符、Loop/Mapping、编译、播放及中途播放，确认没有固定等待或听感变化。
2. **大型 Logical Segment**：从大型 MIDI 粘贴大量音符，再做小/大编辑与 Undo/Redo；观察后台编译完成和真正可播放时间。只打开原 Pure MIDI 项目不能覆盖此次 Logical 改造。
3. **Shared Logical + Pure MIDI 混合**：编辑前后编译、MIDI/音频导出及 All Tracks，之后关闭重开，确认内容与来源不变。
4. **取消与快速操作**：编译中取消、连续编辑/切换消费者，确认不发布旧结果冒充新结果，不出现持续增长的 owner/临时文件。

## 9. 修改位置与证据目录

产品集中在 `CompilerValueStore.cs`、`LogicalCanonicalEventSource.cs`、`MidoraCompiler.cs`、`CanonicalAudioUnitProjection.cs`、`CanonicalMidiFileExporter.cs`；测试新增存储/Range/FNV/归属/循环寿命及受控终结器门。当前工作树还包括此前未提交的 Logical 内存改造，不把所有文件都算成本轮新改动。

本机 ignored 原始材料位于 `.tmp/logical-time-implementation/`：

- `runs/baseline-current-{expansion128,shared100000}-{1,2,3}` 和 `runs/final-...`：同日 12 个完整编译对照进程。
- `runs/consumer-final-{1,2,3}`：完整消费者准备与窗口计时。
- `runs/final-small-*`：四个补充夹具及完整 oracle；`runs/export-final2-{1,2,3}`：最终百万事件实际 SMF 写出，早期 `export-final-*` 保留归属分配修正前反证。
- `tests/*/` 的 TRX、对应 `*-guard/` 的 stdout/stderr/guard-result：构建、失败与最终回归。
- `Run-Cases.ps1`、`Run-Consumer-Cases.ps1`、`Summarize.ps1`：逐级串行与统计脚本；正式可复用探针在 `eng/LogicalCompiledMemoryProbe` 和 `eng/LogicalConsumerMemoryProbe`。

历史分析/候选不覆盖；本报告归档关键结果，不要求把 ignored 大型临时测试产物提交。未读取或修改用户草稿，未提交、推送、发布或改变后续极端 Tick/SMF 工作顺序。

收尾静态检查：`git diff --check` 通过；本轮六份相关文档的 31 个本地链接目标均存在；15 个新增代码/报告文件未发现行尾空白。Git 的 LF/CRLF 工作区提示未改写为代码警告，也没有为消除提示改动仓库换行配置。
