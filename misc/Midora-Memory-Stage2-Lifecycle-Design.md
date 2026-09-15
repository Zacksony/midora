# 内存优化阶段 2：Workspace、Surface 与瓦片消费者生命周期

日期：2026-09-08。状态：实施完成、工程门通过；[验证报告](Midora-Memory-Stage2-Validation-Report-2026-09-08.md)记录证据与人工验收 A。范围 M02～M05。

## 需求追踪与非目标

- 输入：当前 Workspace、不可变来源快照、共享 EditorSettings、瓦片 key、消费者 generation、已有 presentation 设置。
- 输出：原有画面、选择、快捷键与音乐内容不变；只改变运行时引用、订阅、取消与释放。
- 依据：SRS §17.2～17.4、§18.11、§20.1、INV-069、095、097～100、112、115～116，以及已批准执行计划 §7。
- 不改 Project Format、presentation schema、音频、编辑命令语义、缓存精度或预算；不通过清空有效位图/History、主动 GC 或强制空白来降低内存。
- 隐藏不是关闭。所有权错误及过期回调必须从引用链修复，而非仅验证 Working Set 降低。

## 所有权与状态

1. Controller 拥有打开的 Workspace。活动切换采用可逆 Suspend/Resume；永久关闭、对象删除和 Project 替换采用幂等 Dispose。共享设置的订阅必须具名并在 Dispose 解除。
2. Suspend 保留 viewport、selection、宽度、滚动、Lane/子页面、手选 Onion 来源与快捷模式；停止不可见 UI 的后台消费者。Resume 使用当前修订重新建立必要请求，不复活旧 generation。Dispose 清除来源派生图、索引、回调与可解除订阅。
3. Surface 的 Loaded/Unloaded 管理其自身消费者，不直接永久关闭 VM。Surface 的普通、选择、拖动、Onion 等各类异步工作都必须可取消。隐藏后的合法纯 Bitmap 继续由既有有界 LRU 管理。
4. MainWindow 最近命令/Alt 焦点目标使用弱引用，解析时复核当前且未关闭 Workspace 和可用 Surface；延迟焦点恢复也不得强留关闭对象或跳到旧 Tab。
5. Onion 禁用、透明度 0、来源为空或隐藏时取消请求并释放不再绘制的 source-derived graph；设置仍独立保留。All Tracks 暂时隐藏保留必要的最后成功 Logical 索引以满足 stale 契约，永久关闭释放所有结果、委托与请求。

## 共享瓦片并发协议

- PendingWork 拥有一个共享计算；消费者订阅独立，以 tile key + 稳定 consumer identity + generation token 去重。重复帧不得追加同一完成回调。
- 取消一个订阅立即解除它的 callback/Dispatcher 引用；其他有效消费者仍可完成同一个计算。最后一个消费者离开时取消排队/执行中计算，扫描定期检查 token。
- token 贯穿低缩放的 Presentation aggregate、Domain 树/页/overlay 和 Application bounded source；节点/页边界及最多 256 条记录检查一次。仅取消未发布结果；默认 token 的聚合数值、像素及预算不变。不承诺抢断当前不可分割的文件读取或解压调用。
- 工厂只能捕获计算必需的不可变快照/小型值，不得因原始请求者而强留整个 Surface。计算退出前保留它实际需要的来源，不能提前释放仍被共享计算读取的资源。
- 已排到 Dispatcher 的通知仍通过可撤销订阅执行，调用前检查有效性。旧 generation 不通知消费者、不改变当前帧；有效的内容 key 缓存与 UI generation 的发布权分开。
- 取消时在锁外 Abort 已排 DispatcherOperation，既断开旧来源图，也避免不可见 UI 的旧通知壳持续积累。All Tracks 进度每代最多一个排队通知，新的进度覆盖同代尚未显示的数值。
- 非共享瓦片的后台完成通知也服从同一取消边界：exact/ruler 预取、框选、Velocity/拖动预取、Conductor 标签/命中/刻度补算及指纹失败清理，使用共用 `CancelablePresentationDispatch`。Desktop 的 Workspace completion 转发复用它；正常交互排队不机械替换成工作任务取消。
- viewport/raster invalidation/warmup retry 三种合并信号仅弱持 Surface，不改变原队列标志或优先级。Conductor 语义 hit/ruler 查询采用独立 CTS，不得被首次绘制或单纯像素代重置误取消；其来源、查询 key、模式、卸载与旧布局回放校验负责失效。
- 永久关闭后的 selection presentation 使用空 ID、无 render index、指标不可用的专用终态，不能被迟到工作重新发布。正常活动视图的“空选择且指标已完成”含义不变。
- 卸载后的离屏重绘不得再次启动 Onion 消费者；只有重新 Loaded/接管才允许恢复请求。
- 锁内只更新计算与订阅状态；不在缓存锁内执行用户 callback、同步等待 Dispatcher 或等待正在取消的注册。取消/完成竞争必须恰好归还资源。

## 验证门

- 多种 Workspace 至少 100 次开关及跨 Project 的 handler/WeakReference 验证；隐藏恢复状态、关闭幂等、旧焦点无效。
- 少量慢 tile 上 10,000 次请求：订阅数随有效消费者而非帧数增长；共享消费者中途离开、最后取消、完成已排队后取消、重开和 revision 变化。
- 三种钢琴卷帘、Conductor、All Tracks、Onion modes/opacity/来源变化，真实 WPF STA 生命周期与快捷键链。
- 编辑/Undo/选择/Velocity/Event 的刷新、低缩放边框和 Segment 外空白滚动不退化；缓存完成/取消后能收敛，无长期空白与旧选择。
- 基准串行；分别报告自然内存、受控 GC 存活和合法 LRU；不得把离屏测试等同人工视觉验收。阶段 1＋2 的人工验收 A 已获用户粗略通过，精细编曲检查留到发布前。

## 验收后补修：All Tracks 激活焦点

- 依据：SRS §20.2.5、§20.12.3、§18.11.2。输入是用户打开/切回 All Tracks 或切换 Raw/Compiled；输出仅为安全的 Timeline 焦点，不改只读能力、选择、History、视口或投影。
- 初始化 Mode 绑定的 SelectionChanged 在 Loaded 前发生。阶段 2 加入生命周期保护后直接跳过它，却没有在真正 Loaded 时补做交接，使 Tab 默认焦点进入首个 ComboBox，命中主窗口的输入控件快捷键例外。
- 显式 Loaded、恢复可见及 DataContext 接管与 Mode 变更共用一个 Input 优先级的焦点请求；每次先取消前一个请求。回调只弱持 View/Workspace，执行前核对同一实例、Loaded/Visible/Enabled、未关闭/未 Suspend。卸载、隐藏和换 DataContext 撤销旧请求。
- 模式下拉仍打开时不转移焦点；DropDownClosed 再交回 Timeline，避免方向键浏览模式被打断，同时避免关闭后遗留输入焦点。
- 不放宽全局文本/下拉框/Modal 快捷键保护；不因背景编译、状态或像素更新抢焦点；不为只读 All Tracks 增加 Draw/Select/Erase 编辑能力。无 Project、旧/失效目标静默不交接焦点，无持久化和业务诊断变化。
