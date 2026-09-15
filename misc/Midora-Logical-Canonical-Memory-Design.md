# Logical 编译结果内存优化：实现设计与验证门

日期：2026-09-10。状态：内存改造及后续时间回收均已实施。初次交付的历史数据见[内存报告](Midora-Logical-Canonical-Memory-Verification-2026-09-10.md)；当前实现、时间/内存复测及待人工验收项以[时间回收报告](Midora-Logical-Canonical-Time-Implementation-2026-09-10.md)为准。

LC-T3 后续更新：完整 SourceReference 不再在每个排序/范围/最终页记录中重复搬运；使用 96-byte 热记录与有界、独立来源 sidecar，在公开 cursor 还原所有字段。预算与音乐语义不变，细节及最新验证见[LC-T3 实施记录](Midora-Logical-Canonical-Compact-Implementation-2026-09-10.md)。T1/T2 时间报告继续作为此前实现的历史证据。

## 需求追踪

- 输入：当前 Project、冻结 CompilationRequest、正式 source revision；沿用 Semantic Validation、Mapping ABI v3、Loop/Pre-Roll、Usage 分配。
- 输出：与旧实现逐事件一致的 canonical、SourceReference、正式总序、fingerprint、统计和诊断；Full/Incremental 一致。
- 边界：SRS §12.4、§12.10～12.12、§12.17、§12.21～12.25、INV-009/010/015/020/066；本轮只改变物理存储/执行计划，不修改 SRS、Project Format、版本或音乐语义。
- 失败：取消、临时 I/O、预算或排序失败不发布部分结果；已有完整结果和可读旧修订仍有效。不得删音、折叠跨 Tick 用户事件、截断诊断或强制 GC。
- 持久化：运行时分页仅位于 ProgramRoot `.tmp/CompilerRuns` 的 owned directory，不进入 `.midora`。未跟踪用户草稿不读取、不修改。
- 非目标：极端 Tick/SMF delta 改造、BASS/WASAPI/Limiter 改造、UI 新功能、提交/推送、本地发布。

## ADR-LCM-001：构建和最终结果都分页

原实现峰值来自完整 Raw 展开、Canonical List、排序/范围结果数组及消费者的第二份数组。不能只替换最终数组。

采用共享预算的不可变 unmanaged 值页、临时排序 runs 和顺序流式折叠/范围处理。小型内容保留内存快速路径，大型内容达到预算后写 owned spill；页/排序边界不进入音乐 identity。SourceReference 完整保留，Raw 继续使用已验证的相对来源/pattern 共享。

LC-T3 的 canonical 排序、状态扫描、FIFO 和最终物理页分别使用紧凑事件/来源句柄。来源表最多 4096 项发现索引，超出仍完整分页保存；随机来源 reader 独立缓存最多四页，并直接保活 finalizable table owner。完整比较器的来源 tie-breaker、统一升序 fingerprint 与消费者公共事件保持不变。来源发现预约涵盖扩容新旧数组；内部 ordinal 仅为物理定位，不形成正式顺序。

临时排序的 run 大小有界；一般/Raw 路径保持 32,768 records、16 路归并，canonical materialization 使用 131,072 records、32 路归并。所有读者和工作数组都计入同一工作预算，不因增大 run 而增大总预算。同 Tick 可以跨任意页/run，必须保持完整语义 group，不可在页边界重置状态。范围恢复与硬结束沿用同一规则，不另建简化编译器。

归并队列只移动读者索引，完整当前事件保存在固定 head 数组中，比较仍调用原完整总序。只有某读者出堆后才更新其 head 并重新入堆，避免修改仍在堆中的比较键；减少大事件结构在每次堆调整中的复制，不简化排序键。

实际流水线：发射时压缩 Raw → 有界排序 canonical 总序 → 逆序按原规则保留胜出 semantic group → 顺序恢复/裁剪范围 → **只排序新增边界事件**，与已排序中段归并 → 再按同一规则折叠 → 不可变最终页。不得对已排序中段重复全量外排序。最终物理页按降序存储，以反向页 cursor 提供正式升序；物理方向不会暴露给消费者。

时间回收后，范围处理不再写出第二份完整 middle store：正序状态扫描记录 immutable source 的 ordinal 窗口，遇到超出 endTick 的首条事件即停止；最终归并从该窗口反向读取，仍精确筛选夹在同 Tick 群内的 Direct NoteOff 终点。最终结果独立写页，不长期借用全 source 保活范围外数据。最终 winner 写出时同时收集页 Tick/Unit 目录与 NoteOn 计数；发布只在必要的统一升序 fingerprint 遍历中顺带缓存可驻留页，混合 Pure/Logical 不分别 hash 后拼接。

最终页索引保存 Tick min/max 和 256-bit Unit mask。窗口先二分定位页，按 Unit 查询跳过不含该 Unit 的页；连续命中页共用一个解码 cursor，不为每一事件随机读取整个页。最终 Logical 输出不超过 4,096 条时仍以内联数组交付，使用相同编译/范围算法。

## ADR-LCM-002：消费者不得反向全量物化

Logical paged source 与 Pure MIDI lazy source 独立标识；不能把 `HasPagedEvents` 当成 Pure MIDI 标记。结果提供统一流式枚举和范围查询。播放/导出/All Tracks 必须消费分页；Logical fragment/preset 元数据在完整成功编译时准备并共享，避免首次播放重新物化整曲。

## 预算与生命周期

共享 resident 页及 Raw intern 预算 **128 MiB**；单页最多 4,096 records；sort run/fan-in 按上述路径区分。受计量的构建/读取工作缓冲上限 128 MiB；页目录、最终 Tick/Unit 索引、sort run 目录与归并堆数组另共享 **64 MiB metadata 预算**；一个编译工作预算的 owned spill 上限 16 GiB，取消通常每 256 records 检查。固定大小 sort 内的取消在该 run 排序完成时检查；无输出的逐 Tick Mapping 循环也有检查。排序工作预约按 List 的向上取整容量计入扩容时新旧数组同时存在的保守峰值。

metadata 数组扩容前先计入新旧数组同时存在的保守峰值，失败不能改变已有目录；最终索引一次分配，不先形成第二份完整 List。这样不会因页面压缩率过高、物理 spill 尚小而让页目录无限增长。该额度只针对列明的分页/排序索引，不能声称覆盖下文列出的所有语义元数据。

32 MiB 是首轮实验参数，而非必须守住的产品要求：同一 52.6 万事件样本虽然显著降低内存，却产生约 1.30 GB 中间读写和数倍时间回退。因此按时间优先原则改测有明确上限的 128 MiB。中间页释放后，最终 fingerprint 的必要遍历会利用此时空出的 resident 预算缓存最终页，避免预算空置而消费者重复读盘；全部页都已驻留时可关闭并清理冗余 spill 文件。超过预算的最终页仍保留磁盘 backing，不按任意输入总量继续扩张内存。

临时页采用当前进程的 unmanaged 值表示，不是持久格式，也不是新的 Project 身份哈希。spill 页使用 Deflate Fastest；固定容量编码缓冲装不下、或编码后不比原始页小时，原样存储该页，不扩大编码缓冲。配额按实际写入的字节计量。时间回收后，每次实际 spill 读取必须先验证描述符，再验证 `SHA-256(record identity || versioned descriptor || stored payload)`；descriptor 绑定版本、record size/count、decoded/stored length、codec 和 offset，raw fallback 同样校验完整 raw 字节。游标复用解码页和压缩输入缓冲，解码长度必须精确匹配；不完整/损坏的页不能当作音乐事件继续执行。此校验保护存储表示，codec 自身错误地产生同长度结果不是 stored SHA 可检测的故障，改由 codec 往返/非法流和完整 canonical oracle 测试覆盖，详见时间报告的保护边界。写端额外预约 512 KiB、读端 64 KiB 的 codec scratch 余量；这是保守计量，不把托管/原生运行库和整个进程冒充受同一硬上限约束。

临时排序和正常构建中间页显式 Dispose；异常先回滚编译缓存，再通过弱注册表释放本次未发布页，不能释放其他预算下仍合法的旧结果。注册表在每次 store Dispose 时 O(1) 移除记录，不累计全部临时对象的弱引用。若磁盘故障直接损坏的是旧结果自己的 backing，则其读者应明确失败，而非继续输出损坏事件；“新事务失败不影响旧结果”不表示磁盘损坏后旧页仍有冗余容错副本。

存储在仍有有效结果、缓存或 reader 引用时保持可读；最终不可变页由合法所有者共同保活，无 owner 后通过受保护的终结清理回收，**不承诺引用移除瞬间就执行磁盘删除**。进程异常退出的遗留目录继续交由已有 owned-temp 启动清理规则处理。不以强制 GC、关闭旧合法 reader 或裁剪有效 Undo 历史制造内存指标。

这些是相应事件存储的预算，不是全进程预算。实例、分配、源页、合法活动 Note/FIFO 状态、诊断和同时仍被使用的旧结果须单独计量，不能宣称任意输入下固定总内存。

`LastLogicalStorageTelemetry` 仅供运行时测量：Raw 扩展、materialization、范围处理、结果索引/指纹发布耗时；预算内峰 resident/working/spill/metadata 与返回时 resident/spill/metadata。它不进入 canonical identity、诊断计数或文件。进程 Private/WS、GC allocation、IO 由独立守护探针另行采集，不能把 tracked buffer bytes 冒充进程总内存。

## 验证

先冻结旧 DLL 和逐事件全字段 oracle；覆盖小型复杂映射/Loop/Pre-Roll/共享状态/范围、合法高展开、Logical 大触发、Pure/Logical 混合。逐事件摘要覆盖全部记录而非抽样，另外比较 MIDI 字节、消费者、Full/Incremental/旧修订/cancel/资源失败。性能探针使用 8 GiB 进程树上限及至少 2 GiB 系统余量，串行逐级扩大；未执行的项不能记为通过。

初次内存交付及其时间退化保留在[历史报告](Midora-Logical-Canonical-Memory-Verification-2026-09-10.md)；后续回收效果、当前验证与真实窗口/声卡未覆盖项见[时间报告](Midora-Logical-Canonical-Time-Implementation-2026-09-10.md)。本设计不意味着所有规模的内存都下降，也不表示所有输入下编译时间都没有代价。
