# 内存优化阶段 3：字节预算与活动所有权

状态：阶段 3 工程门通过；2,935 项公共回归及测量记录见 [验证报告](Midora-Memory-Stage3-Validation-Report-2026-09-08.md)。不进入阶段 4。

依据：六阶段实施/验收计划 §8；调查 M06、M07、M09、M10；SRS §12.24–25、§16.17–25/30/34、§20.3.15；INV-065～070、095～096。

## 输入、输出与非目标

- 输入仍是同一 Project、合法内容页、稳定 ID、编译范围和音频配置。输出的音乐事件、排序、状态恢复、文件字节、诊断定位与编辑/取消行为不得变化。
- 本阶段只管理可重建缓存及验证/编码临时资源。不得修改项目格式、对象身份、音频算法、UI 精度、历史保留策略，不新增拒绝合法大对象的容量上限。
- 缓存不持久化；spill 仅使用程序根目录下有明确 owner 的工作目录。IO 失败/取消仍执行原子失败，不改源文件。

## M06：范围与采样计划

- 合并三个历史字典的预算：范围 canonical、离线 sample plan、realtime base plan，共享一个字节计账 LRU。大于缓存预算的合法计划返回给请求者，但不保留于 LRU，不以缓存准入代替合法性判断。
- 预算固定为每 Project compilation session **128 MiB 的独占缓存存储、最多 256 条缓存项**。这是三族缓存共用的总额，不是每族各 128 MiB。工作集实测中 1,000 次范围切换的计账平台约 125～127 MiB；选择依据与冷热代价见验证报告。
- 计账显式枚举不可变 backing storage：数组按实际容量及元素尺寸计；共享数组按引用身份只计一次。对象/哈希目录使用保守元数据估算，报告须与实际 GC 堆区分。
- `LastAttempt` / `LastSuccessfulResult` 已由正式当前编译状态持有的 canonical 属于 baseline owner，不能再把与它共享的 backing 算成额外历史缓存。报告分列 `BaselineBytes / CachedBytes / ActiveBytes`，以及两两共享值；`TotalRetainedBytes` 是这些角色的存储并集，不是三数简单相加。baseline 替换后若旧内容变为 cache 独占，立即重新执行预算淘汰。
- 该计账不是整个进程的 heap walker：Project/immutable source/page cache、编译中临时数组、GC 高水位、BASS/native、WPF 不属于该 128 MiB。计划子树以共享外层数组折叠计账；重新包装外层数组但复用子对象的罕见视图可能保守重复计费、提前淘汰，不会少算导致任意留存。
- 保留已有不可变 canonical playback view、采样计划 monitoring view 共享机制，不为计账扫描全部分页源音符或读取 payload。
- 活动计划通过显式 lease 归属 Preparing/播放/消费调用者。缓存移除仅撤销缓存的引用，不 Dispose source、不修改数组；活动 lease 独立存活直到调用者结束。活动数据单列计账，不谎称单个合法大计划受缓存容量约束。
- 计账字典在批次结束、降至四分之一低水位时缩容，完全空时释放 backing；不在逐条 Dereference 中反复整理。弱描述表按源对象身份共享，源死亡后不会单独把它钉住。
- Session Dispose 先结束/取消其编译 worker，再清掉缓存、当前 canonical 强引用与 baseline lease，归还额外 background mirror 引用，但不 Dispose 被其他活动消费者共享的源页。外部仍持有的 active lease 继续有效，最后 reader 释放后才能回收；disposed session 的 `LastAttempt` 不再返回已经放弃的结果，而明确抛 `ObjectDisposedException`。公开 `Project` 属性仍按既有接口持有正式 Project，不宣称持有 disposed session 时整个 Project 都已回收。
- 预算逻辑只在准备/控制线程执行；不把计账、锁、分配或重建引入音频 callback/synthesis/mix 线程。

## M07：Opaque payload

- ID 缓存只保存稳定 ID 对应 ordinal/descriptor，不永久持有 payload；读取使用既有 source 索引，禁止用重扫全源抵消内存收益。
- descriptor 上限为每不可变源 65,536 项 / 8 MiB；payload LRU 为全进程 8 MiB / 8,192 项，两者不可混称全局同一上限。虚拟页 4,096 条 / 通常 4 MiB；具体超大项及实际借用规则见 [M07 设计](Midora-Memory-Stage3-Opaque-Payload-Design.md)。
- 页面同时限制记录数与 decoded payload bytes；单条超过普通页预算的合法事件独占一页/迭代步，不截断、不拒绝。
- 选择、剪贴板 reader 的 payload 借用按实际 backing storage 计账，标明与共享源/驻留页的归属。不可变 payload LRU 不额外复制同一字节数组。

## M09：Content Pack 编码

- 延续既有 64 MiB 编码预算，但覆盖 active/partial/pending 的实际数组容量、流容量及编码工作缓冲，而非仅已提交满页。
- 64 MiB 是每 writer 的编码数组预算，不是全部 writers 总额；还有每 writer 的 128 KiB FileStream buffer、与 Segment/page 数相关的元数据、Brotli 内部/native workspace。导入沿用 source-MTrk 结束后关闭 writer 的流程。实际租用长度、压缩数组扩容时 old+new 同存均计入，压力测试使用 9 MiB 仅验证算法，不作为新默认值。
- 尾页按需增长；endpoint 不分配无用字节流。压力下暂存未满页，不改变正式 Flush 时点或 Complete 的 Segment/Kind 顺序。
- owned staging 文件必须在 CompilerRuns 会话内；成功、取消、异常和 Dispose 释放；extent 可复用，不能因反复换入换出无限追加。

## M10：Stable ID 唯一性

- 稀疏 ID 使用紧凑容器，密集后转 bitmap，实际 capacity 与目录一起计账；超过工作预算进入 owned 有界外排。
- 默认每 validator 8 MiB，包含扩容并存、排序 chunk 与三个文件 buffer 的转换预留；dense 18M 正常不落盘。外排 live storage 上界是 `48 × 输入 ID 数` 加小型 owner 元数据，不因归并历史无限累积，亦不是固定磁盘大小承诺。
- 不重新编号，不遗漏跨对象/跨文件重复；重复诊断仍定位正式扫描顺序的首个重复来源。
- 极端合法 ID、范围溢出、重复、spill IO 失败及取消独立测试。新的缓存预算不是对象数量上限。

## 验证门与交付

- F08：1,000 范围/采样组合、冷/热命中、预算平台、共享计账、活动 lease 与失效/取消/关闭；状态恢复、Mute/Solo、Buffering、Start/Stop/Reset 回归。
- F05：MiB payload 的 ID/页/选择/复制/属性/保存与取消，含超页单对象。
- F06：1/100/1,000 小 Segment，active/partial/pending 计账；与冻结基线的 pack bytes/order 对比。
- 稳定 ID：dense/sparse/extreme、跨文件重复、外排失败与清理。
- 每项分别报告字节预算、实际峰值、累计分配、冷热用时，不混用 Working Set 与托管堆，不把未运行测试标为通过。
- 按既定协作计划，人工验收 B 与阶段 4 合并；本轮不自动提交、推送或发布。
