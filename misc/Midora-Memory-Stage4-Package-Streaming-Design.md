# 阶段 4：严格流式持久化与数据入口设计

日期：2026-09-09。基线：`4a4beb63a11514e1f9042dc0dd739e143d120559`。状态：工程门通过；完整结果见[阶段 4 验证报告](Midora-Memory-Stage4-Validation-Report-2026-09-09.md)，等待人工验收 B。

## 范围与需求追踪

- 本轮仅 M08/M11；依据六阶段计划 §9、SRS §16.12～16.14、§16.17～16.25、§16.31～16.34、INV-009/010/020/065/066/088～096。
- 输入为锁定的 Project revision、现有 immutable value pages、普通构建集合及合法 Format 1/2/3 package；正式编辑、选择、诊断、canonical 和文件内容保持不变。
- 不改 schema/descriptor/golden、音频算法、渲染精度、碰撞、Undo 保留规则；阶段 5 的展开结果/History 总预算不提前实施。
- 取消、流损坏、关联错误、磁盘失败均不得提交部分 Project 或替换旧目标；现有损坏对象隔离和旧格式备份保护不变。

## 包级中间表示

原保存同时持有所有非 MPK 的 `Dictionary<string, byte[]>`，严格重开后再次构建另一份字节字典。改为逐文件写入既有同卷事务 staging 目录，内存只保留路径、长度、SHA-256 等必要文件索引。大型 Logical/SubVoice protobuf 和 Conductor JSON 直接写 Stream；小型配置 JSON/对象 metadata 的既有 codec 单文件临时 byte array 用毕即释放。

自校验仍完整打开新 ZIP、严格恢复 detached Project，并重新序列化全部结构文件；第二次序列化写向逐字节比较流，对照第一遍 staging 文件，检查每一字节、长度和文件集合。不用仅比较 hash 代替原字节等价门，也不为验证生成第二套暂存文件。MPK 继续沿用已验证的不可变 staged pack 和原有自校验路径。

每个串行结构文件输出使用 64 KiB 聚合缓冲、SHA 状态和一个文件流；比较使用有界读缓冲。预算不包含合法 Project、本来必要的文件索引、codec 的有界页和 CLR/文件系统缓存；不存在“整个包固定 64 KiB”的承诺。所有 staging 都是既有保存事务 owner，原取消/失败清理与最终替换协议不变。

staging 是可丢弃的事务输入，不是崩溃恢复文件；每个条目只 flush managed/file 缓冲，使后续 ZIP 和自校验能读到完整内容，不逐条额外要求 durable flush。最终 ZIP 及永久旧版副本的落盘/发布规则仍沿用原事务，不削弱来源安全。

## 打开和恢复

大文件先在 Zip entry 上流式校验 SHA，再重新打开同一锁定 archive entry 交给严格 reader；不先 `ReadEntry→MemoryStream→ToArray`。这会多次顺序解压，须独立测时间，不能删除 hash、wire 验证来避免读取。若双遍代价超阈值，比较使用 owned spool 的替代，但不能长期占用用户源包。

Logical/SubVoice reader 直接建立 detached value pages，完成 presence、未知/重复字段、值域、头部及关联验证后 adopt。Conductor 必须完整成功后才替换目标集合，失败回退默认时不得残留已读取的一半事件。旧公共 byte[] codec 入口为兼容保留，不是大包生产路径。

## 资源及失败证据

专项测试覆盖 short-read、非 seek stream、字段乱序、重复/未知/presence、截断、Unicode、取消、EOF/尾字节差异、staging IO/self-validation/publish 故障；冻结 golden 不更新。100k/1M Logical/SubVoice 普通/adopt 构建及百万 Conductor，测完整保存/重开、快照/clone/小编辑以及旧新字节等价。

重型回归/测量串行运行；基线已独立构建到 `.tmp/memory-stage4/baseline/`。最终结果和时间取舍另记验证报告；人工验收合并阶段 3＋4 为 B，通过前不进入阶段 5。本轮不提交、推送或本地发布。
