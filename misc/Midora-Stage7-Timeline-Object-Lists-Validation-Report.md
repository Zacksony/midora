# 阶段 7：验证与性能报告

日期：2026-09-07。阶段 6 提交 `e753fc4` 已推送。阶段 7 功能整体验收通过，后续三项 UI 小调整单独复核；未发布 `dist`，未使用 computer-use。

## 实施与回归边界

- Logical Segment、MIDI Segment、SubVoice 使用一个虚拟列表控件和三种只读数据适配器；图形视图继续使用已验收的栅格缓存，不使用列表目录反推命中或音乐语义。
- 目录在后台以有界标量存储排序，首屏使用独立的前缀候选读取。隐藏、切 Tab、换 owner 和关闭工作区时取消查询并释放目录。冷行按冻结 ordinal 精确读取，不把尚未到达的行当空白。
- 普通单击替换选择；Ctrl、Shift、拖动、Ctrl+A、双击 Properties、Locate 共用正式选择集合。异步发布核对 source、owner、文档与选择修订；新手势和 Escape 取消待发布选择。
- 混合选择使用原子混合删除与类型子集命令，保持另一类型选择；Undo/Redo 保存完整选择书签。小规模旧命令补精确结果选择；大规模三种 note 命令复用既有有界计划，仅在明确请求时发布该计划已有的结果，不重复准备或改变普通命令。
- SubVoice 标记为非交互独立覆盖层。布局取整与 TimelineSurface 一致；Pre-Roll 暗区、Loop 单/双端在 piano、Velocity、Event Lane 对齐，顶部标签不重复。
- 显式菜单来源与列表键盘焦点用于恢复命令焦点；不由“曾从列表选择过对象”推断焦点来源。弹窗取消、准备取消、成功或失败均恢复正确来源；Locate 则明确聚焦图形。

## 主体交付自动测试

各专项已包含在所属全套中，不重复累计。

| 测试集 | 结果 | 证据 |
|---|---:|---|
| Application | 1019/1019 | `.tmp/stage7-validation/stage7-application-full-final.trx` |
| Compiler（含新增 Domain 前缀查询） | 409/409 | `.tmp/test-results/stage7-compiler/stage7-compiler-full.trx` |
| Presentation | 398/398 | `artifacts/validation/stage7/presentation-stage7-final.trx` |
| Desktop | 373/373 | `.tmp/test-results/stage7-desktop/stage7-desktop-final.trx` |
| 列表专项 | 19/19 | `.tmp/test-results/stage7-object-list/stage7-object-list-final.trx` |
| 混合命令专项 | 22/22 | `.tmp/stage7-validation/stage7-mixed-delete-final.trx` |

四组全套共 **2,199 项通过，0 失败、0 跳过**。最终 Desktop Release 构建为 0 警告、0 错误，Desktop 全套 1 分 41 秒；Application 全套 1 分 52 秒。Desktop 全套包含 19 项列表、16 项类型选择/事务测试，以及唯一 Application/STA 主题测试中的真实 MainWindow 集成路径。`git diff --check` 无空白错误。

专项覆盖：

- 三种 owner 的乱序原始内容、同 tick、隐藏范围、overlay、更换修订、取消、稀疏选择、百万行排序与范围选择。
- 冷页 context、Properties、范围终点；隐藏、换 source、改变选择、Escape 后不发布迟到结果；滚动终点和页面 overscan 分离。
- 混合删除、跨 lane 删除、orphan 参数删除、opaque 能力门；typed 变换、碰撞删除、剪切、粘贴、完整 Undo/Redo、取消与选择修订变化时拒绝发布。
- 三种 5,000-note 有界操作只准备一次，退出 typed 请求后普通命令不受影响。
- 真实 MainWindow BAML 与原 DataTemplate 的离屏 WPF 布局及事件处理：三种列表显隐、宽度、滚动、同类 Tab 重用、选择、Properties 数据和 Locate；实际标记层绑定和尺寸。测试不点击用户桌面，不打开可见 MainWindow，不初始化音频 Worker；设置写入独立测试目录并清理。
- 覆盖层单端/双端、零 Pre-Roll、范围外裁剪、极端缩放、100%/125%/150%/200% DPI；右键二维点命中及既有 Conductor/click/drag 回归。

## 验收后三项小调整复测

- Pre-Roll 使用共享主题的紫色语义画刷；真实 WPF GlyphRun 检查普通间距、Pre-Roll 位于合并标签中间、端点同像素三种情况，只有 Pre-Roll 文字为紫色，Loop 仍黄。
- 实际 Configuration TextBox/Binding/LostFocus 路径验证空字符串及空格静默归零、非零归零一次 History edit、Undo/Redo、已零时不新增 History；负数、超范围与错误格式仍拒绝并恢复最后合法值。
- 实际三种工作区 BAML 验证：SubVoice piano/Velocity/Event 全部关闭 Time Range 拖选，Logical/MIDI Segment 主 piano 仍启用；SubVoice 右键对应三个命令也已禁用。

| 本轮重新运行 | 结果 | 证据 |
|---|---:|---|
| Presentation 全套 | 401/401 | `.tmp/test-results/stage7-ui-polish/stage7-ui-polish-presentation-full.trx` |
| Desktop 全套 | 373/373 | `.tmp/test-results/stage7-ui-polish/stage7-ui-polish-desktop-full.trx` |
| WPF 布局/Configuration 定向（已含在 Desktop 全套，不重复累计） | 2/2 | `.tmp/test-results/stage7-ui-polish/stage7-ui-polish-focused.trx` |

本轮两组全套 **774 项通过，0 失败、0 跳过**。运行中的用户 Midora 占用默认构建目标，故使用各项目独立 `bin/Stage7UiPolish/` 输出：Desktop/Presentation Tests Release 构建均 0 警告、0 错误，未结束或重启用户进程，未修改 `dist`。本轮没有重新运行 Application/Compiler 全套，前表是主体交付记录，不冒充本轮复测。SRS §18.4.2、18.4.4 与 §20.1.3 已同步此次明确调整。

## 百万行实测

使用合成的一百万条 scalar 音符源；不是用户完整 9KX2 工程，也不是端到端创作性能宣称。

| 项目 | 本机一次观测 |
|---|---:|
| 完整排序目录建立 | 2,104.05 ms |
| 远页读取与百万范围选择合计 | 150.94 ms |
| 排序完成后目录保留的标量页 RAM | 0 bytes |
| 完整目录临时磁盘 | 104,000,000 bytes |

“0 bytes”仅指目录已完成后的标量页，不包括源模型、压缩选择集合、可见行、排序中工作区、页索引、WPF 或进程工作集。选择仍使用现有 `CompressedMidoraIdSet`：密集 ID 紧凑，极端稀疏 ID 的 Builder 成本与占用的 ID 页数相关，不应宣称所有选区恒定内存。

首次完整目录排序有 O(N) 临时存储和排序时间成本；相同 source 修订复用目录，关闭列表后释放，因此重新打开冷列表可能再次排序。目录构建全局限制为一项并发，使用现有临时存储预算；达到预算时明确报告失败，不改变 Project。

## 人工验收与未验证范围

参见 [14 项清单](Midora-Stage7-Timeline-Object-Lists-Acceptance-Checklist.md)。自动测试不能代替字体、菜单布局、真实鼠标手感和多显示器视觉验收。本轮未运行 18M/164M 文件的完整创作流程，未更改或重新验收音频实现；这些不应被写成已通过。

阶段 8 洋葱皮、All Tracks、presentation 持久化和发布前全量回归仍未实施。本阶段不更新项目文件格式。
