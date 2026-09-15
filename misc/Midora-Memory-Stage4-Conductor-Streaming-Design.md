# 内存优化阶段 4：Conductor 严格流式 JSON

状态：实施完成；最终 Conductor 专属 68 个测试及 Package transaction / content stream 联合门共 84/84 通过，等待阶段 4 整体交付汇总。

## 需求追踪与边界

- 依据：阶段实施计划 §9 / M08；SRS §16.3.3、16.8、16.12～16.14、16.18.5、16.31～16.33；INV-030、040、088、091、110～111。
- 输入与正式输出：相同 Format 1 Conductor JSON schema（由 Format 2/3 复用）和稳定事件 ID、数值、文本、事件顺序。不得修改 schema、golden 或文件版本。
- 新接口：`Serialize(MidoraProject, Stream, CancellationToken)` 和 `Restore(MidoraProject, Stream, CancellationToken)`；原 byte[] / DTO 接口继续保留供原调用者使用。
- 运行时归属：输入/输出流归调用方，codec 不关闭。JSON 缓冲和校验器只在调用期间存活；恢复成功后的不可变分页记录归 Conductor source。
- 失败、取消：Reader 在独立 Conductor 中准备，完整 JSON、schema、ID、tick、文本、TPQ 兼容性及 EOF 校验成功后才替换 Project.Conductor；失败不留下部分事件。Package 保持已有 Conductor 损坏回退边界。
- 非目标：不改声音、编译、编辑语义、Conductor 视觉、Format 1/2/3 wire 或公共 StrictJsonV1 的其他消费者。

## 实现方案

1. Writer 冻结现有只读 source，先流式验证再写出。按冻结的 V1 DTO schema 显式写入字段，不反射 Domain、不逐事件创建 DTO。使用与原 source-generated writer 相同的 Utf8JsonWriter 数值/字符串编码，维持字段顺序、两空格缩进、LF、无 BOM 和最终 LF；旧 source-generated DTO writer 保留为测试的逐字节 oracle，schema 和格式不变。Writer 每 256 条检查取消与待写字节，达到 64 KiB 后分块 flush，避免逐 record JsonSerializer 调用的流 flush；不会构造整套 DTO 数组或完整 JSON byte[]。
2. Reader 使用可短读、可取消的逐 token 增量 UTF-8 JSON 状态机。顶层最多六个属性用 bit mask 检查重复/必填；单条事件的属性立即检查未知/重复，空白随读取丢弃，不因一条记录的空白很多而保留整块原始 JSON。必要 scalar 的原始 lexical bytes 写入复用的紧凑单-record buffer，再交同一 source-generated item DTO 反序列化，保留 ID/整数/decimal/字符串的既有严格判断。DTO 立即转为不可变 Domain 记录，不保留全数组 DTO 或全文件 JsonDocument。非法 UTF-8 或转义孤高代理 property name 统一落为 JsonException，不让 Utf8JsonReader.ValueTextEquals 的 InvalidOperationException 泄露出 package recovery 边界。
3. 正常文件按 tick/ID 有序，直接建立 4,096-record 最终源页并 adopt，不经过逐项可编辑 Add 的全量目录。旧 reader 接受非排序数组并在 Domain 恢复时排序，新的 reader 继续接受：每页排序后 k-way 合并引用，事件对象不重复创建。乱序路径在交接时会暂时共存新旧两套引用页（约每记录额外一个对象引用），不是复制两套事件对象；页目录与归并队列按页数量增长。
4. ID 去重采用阶段 3 的有界校验器；合法 `long.MaxValue` 的单 ID 单独标记，不把 nextStableId 校验错误地移入 codec。真正的全包 ID 高水位/跨文件检查仍由 package 执行。
5. 校验用内存与 source 记录分开计账。必要的 Domain 记录/页目录随事件数量增长；流式 codec 不承诺整个 Conductor 变成固定 RAM 大小。一般输入缓冲 64 KiB、紧凑 record buffer 512 bytes；16 MiB 的单记录额外空白不会增加二者。合法单 scalar 仍可能很长，例如 decimal 的大量尾零；为保持旧兼容性，允许输入 token buffer 与紧凑 record buffer 为此增长，不新增长度拒绝条件。因此准确边界是 O(必要单 scalar / 紧凑字段 + 固定输入窗口)，不是所有合法 JSON 严格只有 64 KiB。两个实际 capacity 分别计入 metrics，不把紧凑 record 副本漏算。

## 已知规格与现状差异

SRS §16.8.4 以语义错误描述若干可诊断事件，但当前 codec 的 `Validate` 会对非法 BPM、tick、重复状态等直接失败，package 因此回退 Conductor。本阶段只优化内存，保留既有判定及回退，不擅自扩展损坏内容的加载能力。

## 验证门

- 新旧 source-generated deterministic bytes 逐字节比较，涵盖全部事件、End Marker、decimal 尾零、Unicode/转义字符串、空数组、非排序输入。
- 重复/未知/缺失字段、null、非法数值、ID、控制字符、UTF-8、BOM、尾随内容、截断、注释、尾逗号和深度边界；每个失败均验证原 Conductor 未变。
- 非 seekable / 每次 1～7 bytes short read、输入/输出异常注入、取消、leave-open。
- 100k / 1M Conductor，以及较大合法 Marker 字段的混合事件；分别记录耗时、累计分配、codec 缓冲与最终必需 source 留存，不混淆峰值和累计分配。
- 完整 package 保存/重开/自校验、损坏回退和 Format 1/2/3 回归由阶段 4 集成测试覆盖。

## 已执行证据与口径

最终专属/联合门：2026-09-09，Conductor 68 例 + `MemoryStage4PackageTransactionTests` / `PackageContentStreamTests`，共 **84/84** 通过（12 秒）。TRX：`.tmp/memory-stage4/conductor-token-final/conductor-token-final.trx`。包含全部旧合法输入、严格 malformed 异常类型、恢复后不保留输入 byte[] / Stream 的弱引用检查，以及失败/取消不替换旧 Conductor。完整仓库回归由阶段 4 主报告统一记录。

新增可执行预算证据：

- 20,001 条记录 / 1,902,960 bytes，逐 record source-generated serializer 路径产生 **40,004** 次非空 `Stream.Write`；冻结字段 writer 为 **28** 次；两者与原全 DTO writer 输出逐字节完全相同。覆盖 decimal 最小正值、最大值及尾零 scale。
- 合法单记录附加 **16 MiB 空白**：input buffer **65,536 bytes**，compact record buffer **512 bytes**。空白不形成额外驻留 JSON record。
- 合法 `120.` 后接 **100,000 个 0**：旧 reader 接受，新 reader 接受且值和再序列化 bytes 等价。input capacity **131,072 bytes**，compact record capacity **100,517 bytes**。这属于为保留原合法 scalar lexical 表示而承担的单项成本，不能宣传为全输入严格固定 64 KiB。

最终候选孤立 codec 单轮（不是新旧正式 benchmark）：

| Tempo 数量 | JSON bytes | 写入 | 写累计分配 | 恢复 | 恢复累计分配 | input / compact record | ID validator peak |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100,000 | 8,055,750 | 0.188 s | 2,982,304 bytes | 0.862 s | 36,824,592 bytes | 65,536 / 512 bytes | 75,032 bytes |
| 1,000,000 | 82,555,752 | 0.624 s | 27,903,896 bytes | 2.632 s | 373,897,672 bytes | 65,536 / 512 bytes | 190,984 bytes |

累计分配包含必要 Domain / 页目录，以及短命 DTO 和 ID 校验输入，不代表常驻或峰值。写计时包含首次 mutable→immutable 准备。正式前后时间/内存对比只采用主报告最后联编、同条件、独立进程的结果。

### 历史中间态（不得用于最终性能结论）

2026-09-09：`ConductorStreamingCodecTests` 首轮 **62/62** 通过（20 秒）。TRX：`.tmp/memory-stage4/conductor-tests/conductor-initial.trx`。包含全部事件/Unicode/decimal 与冻结 DTO writer 逐字节相等、1/2/7 bytes 非 seekable short-read、乱序跨页、BOM/重复/未知/缺失/非法数值/UTF-8、取消/IO 故障原子性、100k 和 1M Tempo。JSON 的 100,000 个额外空格单条合法记录也接受，不新增单条 byte 长度拒绝条件。

首轮孤立 codec 单轮记录（包括一个必需 Time Signature，不包含 source 创建耗时）：

| Tempo 数量 | JSON bytes | 写入 | 写累计分配 | 恢复 | 恢复累计分配 | JSON buffer | ID validator peak |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100,000 | 8,055,750 | 1.316 s | 7.62 MiB | 0.954 s | 35.53 MiB | 64 KiB | 73,912 bytes |
| 1,000,000 | 82,555,752 | 8.340 s | 76.46 MiB | 2.380 s | 360.75 MiB | 64 KiB | 189,864 bytes |

这是候选实现的单轮测试，不是新旧性能对比。写入计时包含首次 `CloneFrozen` / Conductor mutable→immutable 目录准备；不能据此推断纯 JSON 编码的热性能。累计分配包含逐条短命 DTO、stable-ID token、必需 Domain 记录及分页索引，不是常驻或峰值。完整 package 同负载前后对比以阶段 4 主报告为准。

首轮后的严格异常边界、输入释放、chunk writer、token reader 与空白预算加强项均已在上述最终 84/84 联合门确认。此历史表仅保留用于追溯为什么进一步移除了逐 record flush，不代表交付实现。
