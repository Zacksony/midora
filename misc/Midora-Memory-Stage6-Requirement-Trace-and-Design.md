# 内存优化阶段 6：全链路收口与验证设计

日期：2026-09-09。开始基线：`e12b6c254a02624bb14b999154ebac80f5492bd9`，工作区干净。

当前状态：四项内存清理已完成；原批次公共回归 3,140 通过 / 2 失败。用户随后授权独立编译修复，最新 3157 / 3157 公共回归及 WPF 大会话通过，工程阻塞解除。2026-09-09 用户确认 MEM-C01～C07 全部验收通过，并另行授权提交、推送；不发布 `dist`。历史实测见 [阶段 6 报告](Midora-Memory-Stage6-Validation-Report-2026-09-09.md)，最新实现与代价见 [编译修复报告](Midora-Pure-MIDI-Range-Correctness-Repair-Validation-2026-09-09.md)。

全链路曾发现的阻塞：普通/Paged Pure MIDI canonical 在保存重开、非零起点/提前终点下存在正式输出差异，详见保留的 [范围编译反证记录](Midora-Memory-Stage6-Pure-MIDI-Range-Blocker-2026-09-09.md)。此项没有归入用户暂缓的 Int64 诊断边界，而是在独立授权后修复；原失败 oracle 保留且通过。Int64 诊断/分页仍明确暂缓，不能因编译修复而写成已解决。

## 1. 范围与需求追踪

依据：[六阶段计划 §11](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md)、SRS §3.9～3.12、§12.19～12.25、§16 严格保存事务、§18.10～18.11、§20 分页编辑与选择，以及 INV-009/010/015/020/065/066/069/088/095/096/112/115/116。

| 项目 | 约束 |
| --- | --- |
| 输入 | 当前正式 Project、immutable sources、编译修订、真实 Workspace/Surface、History/Clipboard 与活动消费者 |
| 输出 | 原音乐对象、选择、完整诊断与来源、canonical、保存/导出结果；新增内部测量和回归证据 |
| 边界 | 半开时间范围、精确碰撞、隐藏内容、Loop/Pre-Roll、Root/Usage 状态、格式与 ABI 均不改变 |
| 失败 | 取消/过期修订/预算或 I/O 失败不得发布部分结果、丢失有效历史或破坏旧文件 |
| 生命周期 | 关闭后不保留旧 Project/Workspace；活动消费者仍持有的页不可提前释放 |
| 持久化 | Format 1/2 reader、Format 3 writer 与 presentation schema 不变；测量不是 Project 数据 |
| 非目标 | 音频算法重构、用户级 RAM/历史硬限、自动 GC/清历史、自动快照、产品版本或发布 |

## 2. 工程拆分

1. 补充固定 seed 跨类型/跨工具序列 oracle；逐项对齐 O01～O40，不以单个共用函数证明所有入口。
2. 真实 WPF STA/主题/DataTemplate/Controller/Surface 与默认后台编译并存；导入、编辑、Undo/Redo、视图切换、Onion/All Tracks、Save/Save Copy、关闭和重开形成连续链。
3. 复查阶段 5 明确交接的诊断 Int32 总数边界。扩展前单独记录序列、筛选、UI 分页和消费契约；不能通过截断或聚合删除原诊断。既有普通诊断界面不应被无谓改动。
4. 测量关闭后 Private/Working Set、GC live/committed/fragmentation、OS region 分类，并用退栈后 WeakReference 证明托管 owner 是否释放。分类证据不足时保留不确定性，不把差额武断叫作泄漏或合理缓存。
5. 全套正确性、真实大样本、冷/热/驱逐后查询及长会话回归；发现残余问题后按证据修复并补独立测试。

2026-09-09 决策：用户明确选择本轮暂缓 Int64 诊断/极端分页界面，保留既有 Int32 总数边界。第 3 项本轮只保留调查与方案，不实施诊断截断或 UI 改造，也不宣称该边界已解决。

## 3. 测量与安全

- 大型测试串行运行，仅使用本轮拥有的测试进程；复用 `eng/MemoryStage5Probe/MemoryGuard.cs`，在子程序首条指令前置入独立 Windows Job。
- Private commit 硬限制不超过 8 GiB（约 8.59 GB），同时采样 Working Set；系统 available RAM 低于 2 GiB 时终止本轮测试树。不得为验证而制造系统 OOM。
- 真实源 MIDI 只读，输出在仓库 `.tmp/memory-stage6/` 独立目录；测试不修改用户工程、设置或 `dist`。
- 大场景先小梯度、后完整选区；未经修复的已知高风险原版不强行运行到系统耗尽。
- 新进程冷启动不等于 OS 文件缓存冷。自然 GC 工作流与专门强制 GC 归因分开；产品不新增强制 GC。
- 运行时基线、源提交/MVID、阶段耗时、自然/受控内存、退出/守护结果与完整/抽样 oracle 均记录。离屏 WPF 不冒充用户视觉或物理音频验收。

## 4. 交付门

本阶段以实际证据填写修改、测试、性能及未完成边界；工程门之后交付 MEM-C01～C07 精简人工卡。阶段 6 完成不等于 C 已通过，更不等于自动正式发布。
