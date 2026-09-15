# 阶段 5：Logical Overlap 诊断的紧凑共享与虚拟投影

日期：2026-09-09。性质：实现设计；不修改 SRS、音乐语义或 Project Format。

## Requirement trace

- 输入：正式语义验证、实例展开及生命周期裁剪后的 Logical instances。
- 正式输出：保持原有 MIDORA2201 每个重叠 pair 的诊断条数、i→j 顺序、级别、文本和 SourceReference；Reject / Warn / Warning policy 的失败阶段不变。
- 边界：SRS §10.17、§12.19、§12.20、§12.25、INV-009～010；同 Usage、Same Pitch / Any Pitch、Pre-Roll origin、半开范围和 rendered end 均继续由 compiler 决定。
- 失败：取消、checked 计数溢出或准备错误不能发布半成品；不静默截断或聚合诊断。
- 持久化：无变更。列表、筛选索引和可见行只属于编译结果或 UI runtime，不进入 Project、Undo、canonical 音乐 fingerprint 或 `.midora`。
- 非目标：不改 Reject 默认值，不隐藏 Warning，不把 UI 筛选当作诊断收集策略，不为巨大诊断增加新业务上限。

## 已确认的放大链

旧 ValidateOverlap 对所有 overlap pairs 各分配一个约 208-byte Diagnostic（含完整 SourceReference），放进 List，再冻结为数组。DesktopSessionController 随后为每项构造 DiagnosticRow 和定位字符串，再拷贝到 ObservableCollection；Diagnostics Workspace 再持有全量及筛选集合。WPF 容器 virtualization 并不阻止这些数据对象的全量构造。

旧诊断只记录右实例 j，因此同一 j 被多个左实例命中时，诊断值完全相同；相同值仍必须在正式列表中出现原次数。Same Pitch 模式中属于同 pitch 的右实例形成独立有序序列，任一左实例的命中是该序列的连续切片。该性质允许保存一次 compact source table 及按原左实例顺序的切片目录，不必保存每个 pair 的对象或引用。

## 实现决定

1. 公开结果继续提供 `IReadOnlyList<CompilerDiagnostic>`，内部不再强制数组；新增只读诊断集合与切片组合服务。普通诊断保留冻结数组，overlap 使用紧凑来源 value 与 range descriptor。Count / index / 顺序枚举仍展开全部逻辑诊断，既有结果比较仍适用。
2. 同组按 StartTick / SourceOrder 稳定排序。LetOverlap / Cut 策略提前退出；Same Pitch 索引仅查询相同 pitch。每个左实例以二分确定 endExclusive，按原 i 顺序追加右实例切片。新实现不保留 RawInstance / Project 图到最终诊断结果。
3. 完整列表拼接顺序为原有 prefix → overlap → allocation/debug suffix。成功判定使用同一完整诊断集合，不重新降级或改写级别。
4. 紧凑列表的筛选先对唯一 source 求值，再映射所有 range，保留重复数和相对顺序；取消按有限粒度检查。筛选不会先展开所有 pairs。
5. Desktop 使用 `IList` 只读虚拟行投影，使 WPF 走索引式 virtualization；行和定位字符串仅在索引访问时创建，缓存有界。无筛选时共享完整列表。大型筛选后台准备后按版本原子发布，Workspace 关闭时取消工作；旧筛选不得覆盖新筛选。
6. UI 计数从紧凑集合统计，不为了状态栏遍历每个 pair。存储归因按底层表 / range 唯一计数，不按逻辑重复次数计 RAM。

### MIDI README 正式消费补充

SRS §14.15 要求请求的 README 属于完整导出事务，记录 Warning / Info，不能因为诊断数量大而静默省略行。旧 factory 把完整诊断投影成第二个数组，builder 再生成全长 StringBuilder、string 与 UTF-8 byte[]，artifact 在事务开始前保留整份字节。

正式路径改为只读 lazy 诊断投影及 `WriteTo(Stream, ..., CancellationToken)`：冻结诊断源后保持全部重复行和原顺序，使用无 BOM 严格 UTF-8、原有 LF 与 Markdown escape 规则分块写入事务临时文件。每条诊断在写入时验证；非法诊断、编码错误、取消或 I/O 故障由既有输出事务回滚，不发布部分 README。普通字段仍在开始写入前验证。小型 `Build` 兼容 API 继续返回 byte[]，只作为显式物化包装器；正式 artifact 使用 WriteTo 委托。合法相同请求的字节必须与旧 builder 完全一致。

### 既有表示边界

`IReadOnlyList.Count` 仍是 Int32，诊断逻辑总数以 checked 累加；超过该既有列表表示能力时受控失败，绝不截断。没有新增更小的诊断业务上限。将正式公共计数提升为 long 及对应 WPF 导航是独立后续契约工作。

## 验证门

- 与小型旧 i→j reference 比较完整诊断序列（含重复），覆盖 Reject / Warn、Same / Any Pitch、同 tick、不同 gate、范围、共享 Usage、Full / Incremental。
- 压缩列表随机索引、枚举、计数、过滤、取消与存储归因；索引构造规模与 source/descriptor 数相关，不与 pair 数相关。
- UI 虚拟投影只创建请求行，过滤保序与完整重复数，后台结果 revision gate / 取消。
- 大型执行只在进程内存守护下运行；接近用户指定 9 GB 边界必须停止，不通过无界测试证明改进。
