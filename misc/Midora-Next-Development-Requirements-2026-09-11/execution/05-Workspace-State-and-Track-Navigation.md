# 工作区恢复、同轨道共享与 Tracks 导航：执行边界

日期：2026-09-14。状态：**未开始；远期轮廓，进入专题前再细化实现与人工清单** 。

入口：[执行总计划](00-Execution-Plan.md)。需求／源码依据：[03](../03-Multi-Instance-and-Workspace-State.md)、[01 的 R30](../01-Interaction-and-Display.md)；唯一决定与原答：[04](../04-Decisions-and-Preparation.md) D-STATE01～03、D-TRACK01，包含 Q2 D-STATE03.d/e。本文件不重复问答，不改变已经批准的白名单。

## 1. 追踪、范围与依赖

| 维度 | 约束 |
|---|---|
| 需求／优先级 | R06、R07=P2；R30=P3，保持独立，不以新增导航面板阻塞状态专题 |
| 输入／输出 | 当前音乐 owner 身份及轻量视图描述 → 同 Track profile／局部状态 → 显式 Save 冻结 presentation；不序列化 ViewModel |
| 持久化／运行期 | 不进入音乐 Modified／Undo／canonical；监听 Mute/Solo 可保存初值但仍只过滤运行期，不影响成品输出 |
| 失败 | presentation 分区损坏只回退该区并提示；音乐仍加载。预算超限明确告知，可保存音乐和可用视图部分或取消，不静默丢失 |
| 释放 | 删除 owner 立即释放自身状态；存活 owner 可留预算内 dormant Onion 来源 ID，不持有被删对象、页、位图、任务或 lease |
| 非目标 | 不恢复 Selection／Undo 栈、任务、音频状态、缓存、自动保存或崩溃快照；不提前加入白名单外的未来字段 |

B1 需要 R28 的 target 身份／显隐／纵轴归属先定并可验证；不要求 R30 已实施。共享主 ToolMode 与事件绘线形态继承 A3 最终合同；如果 A3 尚未完成，先明确接口，不伪造另一个工具模式系统。B 与多实例 C 的保存冲突接口需共同审查，但无须让两专题整体互等。

现行 SRS §16.28、§16.33、§17.2、§18.2／18.11、§20.1.4／20.1.6及第21章、INV-011/060/091/112/116 仍有更窄的持久化范围。进入实现前以 Q1/Q2 决定正式修订相关条款／ADR；本轮没有修改它们，也不提前固定新 schema 或 Project Format 版本号。

## 2. 工程子任务（当前均未开始）

| ID | 对应决定／前置 | 交付边界 | 必须提供的完成证据 | 批次 |
|---|---|---|---|---|
| T-STATE-01 | D-STATE01、D-STATE03；R28身份合同 | 将04表A/B展开成字段→owner→默认→保存→恢复→释放矩阵；区分Track共用与Segment局部 | 无字段漏项、无普通selection/cache进入；新旧规格准确追踪；预算实验方案 | B1 |
| T-STATE-02 | T-STATE-01；A3工具合同 | 同Track profile实时同步，不同Track隔离；主ToolMode共享，事件形态另存；Piano/Event Snap保持独立 | 双Segment同轨／异轨、重入通知、关闭重开、切Tab焦点的自动测试 | B1 |
| T-STATE-03 | T-STATE-02；D-STATE03.c/d | Duplicate／转轨状态、删除owner释放、存活对象dormant来源ID与Undo恢复 | 删除后无VM/source lease残留；Undo不自动重开Tab；保存过滤悬空而不破坏会话内恢复 | B1 |
| T-STATE-04 | T-STATE-01；D-STATE02 | 独立版本化presentation及分区验证；冻结可执行的条目／字节／恢复资源预算 | 旧音乐格式读取、新旧presentation golden、损坏/未知字段/超限隔离；预算实测，不凭经验猜上限 | B2 |
| T-STATE-05 | T-STATE-04；D-STATE02.a/c | Save/Save Copy冻结轻量描述；纯视图变化可显式保存、不加星、不产生关闭确认；超限按已定选择处理 | 保存期间新视图变化、取消、写入失败、旧格式升级副本和源identity竞争；无部分覆盖 | B2 |
| T-STATE-06 | T-STATE-05；D-STATE01.b/c | 恢复静止指针、Track/group监听初值；永远Stopped，不自动播放 | 首次播放过滤正确、两级Mute/Solo独立；canonical与SMF/WAV不受presentation污染 | B2 |
| T-STATE-07 | T-STATE-02～06 | Tabs顺序、活动子页／owner、局部viewport、面板状态懒恢复；显式Arrangement打开保留定位编辑指针优先级 | 隐藏Tab不提前扫描／编译；活动Compiled正常后台准备；关闭/切换不串状态 | B3 |
| T-STATE-08 | T-STATE-07 | 长会话、批量打开关闭及音乐编辑后的状态回归 | 订阅数、managed/native/WPF及任务持有释放证据；同音乐Full/Incremental与成品等价 | B3 |
| T-TRACK-01 | R30、D-TRACK01.a～d | 两类Segment左侧Tracks默认隐藏，复用Arrangement头及命令，不创建第二套轨道顺序 | 当前owner与操作选中态可区分；各命令路由及音符选择不被误清 | TRACK |
| T-TRACK-02 | T-TRACK-01 | 双击含编辑指针的Segment，否则最近者等距取前；无Segment不创建；接入轻量状态 | 转轨／删除当前owner／共享组／MuteSolo／关闭面板的自动回归；B已交付时补profile扩展 | TRACK |

## 3. 人工验收批次（轮廓，临近实施冻结）

内部任务不会各要求一次人工验收。B1/B2/B3按用户可观察的完整结果划分；若实现体量适中且全部工程门已过，可合并相邻交付，但不得省掉对应证据。以下仅为未来清单骨架，尚未执行：

- [ ] UAT-B1-01：同轨打开两个Segment修改缩放、主工具和绘线形态，相互同步；另轨不变，各Segment滚动位置／Lane轴保持独立。
- [ ] UAT-B1-02：关闭Tab重开仍记忆；Duplicate后独立；删除Onion来源再Undo可恢复来源显示，但被删来源自己的Tab不自动重开。
- [ ] UAT-B2-01：只改变视图及Track/group Mute/Solo，不出现音乐修改星；显式Save后重开恢复，且始终Stopped；未保存的纯视图改变关闭不提示音乐保存。
- [ ] UAT-B2-02：使用提供的损坏presentation测试副本，只有相应区域回默认并有提示，音乐完整可用；用户不手工构造损坏包。
- [ ] UAT-B3-01：多个Tab、子页、活动Lane、侧栏和viewport重开位置符合记忆；切Tab后快捷键可用，后台Tab不导致长时间不可操作。
- [ ] UAT-TRACK-01：两类Segment打开Tracks栏，单击选择／取消，双击按约定导航；空轨双击不新建Segment。
- [ ] UAT-TRACK-02：在Tracks栏执行几项代表性改名／颜色／绑定路由／排序，Arrangement同步且音符选择不被意外清除。

故障注入、跨格式包、所有字段组合、订阅泄漏、完整命令矩阵和百万级恢复成本由自动测试承担，不让用户手工遍历全部组合。

## 4. 进入下一阶段与证据记录

遵守[共同验证门](00-Execution-Plan.md)。B1通过才能冻结B2存储；B2不以“能打开”代替坏分区／保存原子性；B3不以热缓存响应代替冷恢复。记录基线和完成commit、测试命令／输出、资源预算、失败项、人工批次结果；本轮仅规划，无任何产品性能或验收结论。
