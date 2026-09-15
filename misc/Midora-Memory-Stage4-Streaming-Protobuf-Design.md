# 阶段 4 / M08：冻结 protobuf wire 的流式读写

## 需求追踪与边界

- 输入：Event Instrument v1/v2、Logical Track v1 的正式源对象或包内未压缩 Stream；输出：原 Edition 2024 wire 或尚未加入 Project 的 detached 对象。
- 依据：SRS §16.3、§16.12～14、§16.17～20、§16.31～33，INV-088～092，以及内存执行计划 §9。schema、descriptor、字段号、presence、正式集合顺序、Format 1/2/3 golden 均不得改变。
- 未知字段、重复 singular 字段、错误 wire type、截断/越界 varint、非法 UTF-8、非 finite 浮点、缺必需字段必须拒绝。对象 header 冲突继续使用 `ProtobufObjectHeaderExceptionV1`，普通损坏使用 `InvalidDataException`，取消原样传播。
- 读取不得向活动 Project collection 发布部分对象。失败只丢弃 detached 结果；对象 hash/header 预检、损坏占位及最终 Project 原子提交仍由 package 层负责。
- 不改变音乐语义、稳定 ID、存储版本、SRS、持久化归属、应用会话状态；不涉及音频、UI、发布或阶段 5。

## 实现决策

1. 保留 byte[] Serialize / span Restore 兼容入口，正式 package 使用新增 `Serialize(value, Stream, CancellationToken)` / `Restore(project, Stream, CancellationToken)`。不要求 seek，不关闭调用方 Stream。
2. Writer 在输出前验证并计长；按冻结 value snapshot 的正式序列写出。只缓存容器消息长度与值快照，不缓存每记录 DTO 或完整中间 bytes。嵌套消息长度使用 checked Int32，输出顺序与冻结生成器完全相同。
3. Reader 用 descriptor 验证当前字段后读取；Logical Note、Template Event、Curve Point 直接变为 value struct，4,096-record 页完成后保留为最终 immutable source。成功后通过现有 AdoptSource 接管；不逐记录建立 Domain object，也不触发可选 Mapping 创建。
4. 小型消息沿用冻结的 generated DTO 与原字段值验证器，但重型 repeated 字段分离处理。V2 直接组合/读取 V1 definition，不再 V1 bytes → Parse → V2 bytes 或逆向往返。
5. 错误路径文本延后到失败时生成；成功路径不逐字段拼接完整路径。取消在每 256 个字段/记录及消息边界检查。

## 验证门

- 既有 descriptor/golden 与完整对象、package、Format 1/2/3 测试；不更新既有 golden。
- 新旧编码器 bytes 相等，非 seek/短读 stream，任意合法字段顺序，严格损坏与 presence，取消零发布。
- Logical/SubVoice 100k/1M 计时、累计分配、输出字节数及恢复记录/语义；明确区分必要最终 source pages、容器元数据、额外暂存与累计分配。

该记录只确定保持既有外部语义的内部实现，不产生新文件格式或可听选项。
