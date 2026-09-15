# 阶段 8：只读洋葱皮与 All Tracks

日期：2026-09-07。状态：实施完成，8A 专项与 8B 自动回归通过，等待一次合并人工验收。依据：已定案扩展计划 §5.15、ONION-01～09、VIEW-01～03；SRS 3.11、16.7.5、16.33、18.11、INV-091、INV-115～116。阶段 7 和 Loop 修复已经验收；本轮授权阶段 8，不授权本地发布。

**2026-09-08 更正**：阶段 8 主功能已整体验收。下文保留首次实现决策及实测记录；其中全轨 Compiled FIFO 索引、旧文本入口和导航限制已由用户最新决定取代。当前基线见 [验收修订](Midora-Stage8-Onion-Refinement-2026-09-08.md) 和 SRS 18.11：Compiled = 逻辑 canonical 展开 + 当前 Pure MIDI 源音符；播放/导出不变。

## 需求追踪

| 项目 | 正式边界 |
| --- | --- |
| 输入 | Raw 使用 Project 的不可变音符查询快照；Compiled 只使用成功的完整 Canonical Compiled Result |
| 输出 | 当前 Segment / SubVoice 音符下方的只读叠加层；独立 All Tracks 只读钢琴卷帘 |
| 时间 | Segment local → Project absolute → source Segment exposed content；裁剪源 Segment 隐藏内容 |
| 顺序 | 当前 Arrangement / SubVoice 顺序，首项最底层；当前编辑内容始终最上层；排除自身 |
| 交互 | 洋葱皮不参与命中、选择、编辑、吸附、统计、编译和音频 |
| 持久化 | 复用 Format 3 presentation schema 1；配置不进入 Undo / Modified；仅显式 Save / Save Copy 保存 |
| 引用 | 会话内保留 dormant 引用，Undo 恢复同一身份；保存快照过滤悬空引用；Duplicate 复制并 remap 内部引用 |
| 失败 | 旧后台结果不得覆盖新请求；Compiled 失败保留最后成功的图像数据并显著标记 Stale；无成功结果明确为空 |
| 非目标 | 不改变音乐语义、音频链、已有编辑和普通钢琴卷帘渲染；不新增 Format / 软件版本，不生成 dist |

## 实现决策

1. 采用独立只读投影与独立 raster layer key，不向当前 TimelineRenderSnapshot 注入洋葱皮对象。默认每 8 个来源分块，局部范围指纹；当来源很多或高 DPI 使可见块的总位图超过 64 MiB 预算时增大块的来源数，避免可见帧大于缓存上限而反复淘汰。分块依据窗口设备尺寸，不随平移/缩放跳变；更改单一轨道不失效其他块。共享现有有界后台 raster executor，避免额外争抢音频线程。
2. 瓦片查询、分页读取和像素聚合均在后台。每条横向条带保存 512 device columns × 全部 128 Keys，每 Key 一行，以 nearest-neighbor 垂直映射；普通可编辑音符的渲染不变。这样避免同一密集页被多个垂直瓦片重复读取，垂直缩放/滚动也可复用。WPF 线程只创建小型来源描述、请求与绘制已有位图；Raw 密集来源复用已有不可变层级列聚合。当前编辑器洋葱皮为后台 Normal 优先级，All Tracks 主要图像为 Visible 优先级，共用既有有界执行器。
3. Compiled 音符按 canonical 的 Port / Channel / Key FIFO 配对。固定地址计数器、有界外部排序和不可变分页索引替代全量活动音符对象队列。输入使用 canonical 的有界 MIDI render projection（按 TPQN × 16 Tick 窗口查询、关闭起点状态补发），与 canonical 的内存事件按相同正式排序合并；不得调用会把整曲页展开为一个巨型集合的查询。索引本体 resident 16 MiB、working 32 MiB、spill 16 GiB 上限，并按阶段限频汇报进度。NoteOn 的 Source 决定轨道颜色，不从 Channel 或名称猜测。保留零时长原始配对，视觉最小 1 pixel 不改变正式 Tick。
4. All Tracks 的 Raw / Compiled 切换不改变任何 Project selection。编译索引建设可取消、旧索引保留到新索引完整发布；关闭 Workspace / Project 时取消建设并释放自有资源。
5. 配置选择器按实际轨道顺序列出来源，支持全选/清空、启用与透明度；保存顺序字段保持 schema 契约，绘制随当前正式轨道顺序。SubVoice 无独立颜色属性，使用同一既有柔和轨道调色板区分来源，不引入音乐属性。
6. Compiled 索引在写入最终分页时一并累计每条 Track 的音符内容指纹和显示范围；不使用整曲 fingerprint / EndTick 作为各轨栅格块的统一内容身份。未修改 Track 的有效缓存可在新索引发布后复用；颜色或当前正式顺序变化仍正常使对应块失效。

## 验证门

8A：跨类型 Raw、ContentOffset/裁剪/长音符、顺序/颜色、自身排除、SubVoice；Compiled FIFO/跨轨道同 Channel/编译过期；独立选择；取消/关闭/释放；presentation round-trip、损坏隔离、dormant、Duplicate。

8B：专项通过后执行构建及完整自动回归；100/200/400 Tracks 和百万级数据的冷查询/栅格/取消/内存测试。报告必须区分自动证据和未进行的人工视觉验收，列出一次合并的用户验收清单。

结果：12 个项目共 2,955 项自动测试通过，另显式运行 1M/3M/7M/18M 真实 MIDI、六类百万对象生成、10M Logical owner、400K 选择、实际 App 主题与 WPF 栅格/内存探针。首次 18M Compiled 索引约 149 s；普通保存/副本/重开链路测到高内存水位，未把它归因于洋葱皮，也不宣称全程序固定低内存。详见 [验证报告](Midora-Stage8-Onion-Validation-Report.md)。

交付：[使用说明](Midora-Stage8-Onion-User-Guide.md)、[25 项一次合并验收清单](Midora-Stage8-Acceptance-Checklist.md)。没有人工操作验收、提交/推送或 `dist` 发布。
