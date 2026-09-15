# 阶段 5 架构决定

日期：2026-09-06。依据：已批准的大型编辑扩展计划阶段 5。状态：已实施，自动验证完成，用户已验收通过。

## ADR-01：生成器是独立工具 profile，不是工程脚本

使用版本化 `generate-note/v1` / `generate-event/v1` schema 与既有受限数值表达式编译器。每个字段编译一次，由 DAG 解决当前结果依赖；在 staging 循环执行。旧 Batch Edit 与 Mapping Function 的变量和含义不变。生成器 preset 只保存工具输入，Project 保存结果对象。

## ADR-02：统一有界生成与碰撞归并

候选最大值约束迭代而非保留对象数；非单调 Tick 也必须终止。生成流进入现有分页临时存储和有界排序，按正式 exact-key 与 iteration 归并。既有 Note 优先，生成 Note 中较早 iteration 优先；生成事件中较晚 iteration 覆盖命中的既有事件。不得为方便结果选择而重新建立完整逐对象图。

发布、取消和 Undo/Redo 使用既有 detached root transaction。结果选择同时携带正式 owner/type/lane metadata，创建后可直接继续 Properties、变换与快捷键操作。

## ADR-03：统一 Segment 转换计划

跨类型拖动、复制与粘贴共享同一 Application 服务。先在后台读取冻结源并汇总不可转换字段数量，再由 UI 一次确认；不在分析阶段修改 Project。确认的计划有 revision gate，构建完整 detached target 后才允许一次原子发布，Move 的源删除属于同一事务。

跨类型转换不编译 Event Instrument，也不把逻辑音符展开成底层 MIDI；它只转换共同编曲字段。完整 hidden Notes 纳入转换。同类型移动保留原对象及全部内容，同类型复制粘贴保留既有字段、hidden 内容及精确碰撞规则，不额外承诺既有重复记录逐条复制。目标 owner 的约束与同轨 Segment 不重叠规则不因转换放宽。

## ADR-04：显示与接入边界

批量创建入口属于当前可创建对象的 Piano Roll / 数值 Event Lane，与 Selection 是否为空无关；无有效 owner/lane 或编辑被锁时禁用。属性/表达式弹窗只改 draft，OK 后交给可取消任务。关闭弹窗、成功、失败和取消均恢复原 Timeline 焦点。

不新增后台音频解释、不升级 Project Format、不触碰后续阶段的 Conductor、对象列表或洋葱皮。

## ADR-05：小批量排序按需分配，大批量保留原归并边界

阶段 5 补充的多小 Segment 测试暴露了共享排序器每次固定分配 65,536-record 数组的问题。对能廉价获知长度的源按实际长度分配；其它源从小缓冲开始，按需增长到原 run 上限。增长必须先预算旧、新数组同时存活的内存，失败/取消回收自己持有的 reservation。run 分割、比较器、排序及 16-way merge 规则不变，不改变碰撞胜者或已完成的大数据编辑语义。

Segment 转换的原对象目录一次扫描建立，只保存选中对象；按受影响 Track 批量删除源并核对数量，不再为每个 Segment 重扫整个 Project 或反复压缩列表。普通同类型拖动仍走原快速路径。

## ADR-06：临时文件创建与过期目录清理分离

多小 Segment 测试进一步发现，每个 spill store 新建 lease 都会扫描整个 CompilerRuns 目录；同时活跃的 store 越多，累积文件检查和 active-lock 失败异常接近平方增长。创建一个自有临时目录不应顺便清理所有其它目录。

lease 创建只负责创建并锁定自身，Dispose 继续删除自身。过期清理保留在程序启动验证和 Application session cleanup 的既有显式入口。manifest、direct-child、reparse-point 和 active-lock 保护不变，不加静默重试、定时扫描或无限增长的已扫描 root 缓存。该调整不改变 Project/音频语义或编辑事务资源上限。
