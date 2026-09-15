# 剪贴板结果选择的批量操作路由修复

## 需求追踪与边界

- 依据：SRS §20.3.1、§20.3.4、§20.3.12、§20.3.15、§20.4.18、§20.6.4、§20.6.10；INV-095、INV-096。
- 输入：Copy/Cut 的不可变剪贴板快照、实际 Paste 目标，以及 detached edit 产生的精确存活结果 ID。
- 输出：粘贴结果保持选择，且对应类型的批量菜单、快捷键能立即使用；选择类型及 owner/lane 必须对应目标，而不是剪切前的选择或当前显示的 lane。
- 边界：Logical / Direct Note 双向转换、SubVoice Note/Event、参数点、Segment、全碰撞空结果、多 lane 事件和不支持批量变换的 opaque/结构对象。
- 失败与取消：准备阶段取消不改变 Project 或当前选择；项目和 Workspace 的既有发布门不变。Undo/Redo 同时恢复稳定 ID 选择和对应路由元数据。
- 归属：元数据只属于运行期剪贴板命令和 Workspace Selection，不进入 Project、持久化、canonical 或音频缓存。
- 非目标：不修改音符/事件碰撞、批量变换行为、音频、格式或渲染机制，不发布 dist。

## 根因与实现

有限内存编辑已在后台冻结结果 ID，并通过 `AdoptPrepared` 一次发布。原来的投影构造只沿用旧选择的类型信息：Cut 清空选择后，Paste 因而得到无类型的选区；旧选区类型不同时还可能继承错误的 owner/lane。后置的“选中新对象”路径为了避免重新扫描大数据而跳过，无法修复该信息。渲染、Copy/Delete 依赖 ID，批量入口则要求类型匹配，因此表现不一致。

修复在正式剪贴板命令建立时记录真实粘贴目标，并在后台准备期间从不可变 payload 获取有界类型摘要。Desktop 以此创建显式结果投影，包括合法的跨 lane Quantize scope；空结果及不支持的混合类型显式清除旧路由。发布仍为固定成本，菜单/快捷键不扫描 Project、不从位图推断对象。

SubVoice Bank / Pitch Bend Range 的单个物理事件可以投影到多个数值 lane。UI 冻结本次目标 lane，但只有所有 payload 事件都支持该目标时才采用；无效提示不得把混合事件伪装成单 lane。两类 Segment Paste 在私有 draft 应用后冻结新 Segment 的顶层 ID，不把其内部音符/事件误选为同级对象。

Note 分类是常数成本；Direct/SubVoice 事件分类只流式读取不可变 payload、周期检查取消，并缓存常数大小的摘要。没有新增全音符/事件数组、全 ID 字典或 UI 线程扫描。旧选区、菜单状态和当前视图不是正式对象身份的替代品。

## 验证

- Application 全套：878 项通过，0 失败、0 跳过。覆盖剪贴板存储生命周期、两类 Segment 结果选择、目标转换、事件 lane 摘要、取消、碰撞及批量编辑回归。
- Presentation 全套：339 项通过，0 失败、0 跳过。包含显式目标替换旧路由、历史选择恢复、空/未知/跨 lane 结果及取消零发布。
- Desktop 全套：256 项通过，0 失败、0 跳过。真实 `PrepareProjectEdit → ExecutePreparedPreservingWorkspaceSelection` 路径覆盖 Logical/Direct Cut+Paste 四种组合、SubVoice 跨 owner、参数范围、Segment、Bank/Pitch Bend Range lane、多 lane/混合类型、碰撞及 Undo/Redo；并执行粘贴后的真实批量翻转，验证只修改新对象。

三套共 1,473 项全部通过。本轮新增 25 项 Application、21 项 Desktop、3 项 Presentation 用例。初轮全碰撞测试额外假设“不新增历史”不符合现有稳定 ID 预留行为，已移除该不属于本次需求的断言，保留无新音符、空选择及元数据验证；未修改稳定 ID / Undo 业务规则。

## 主要改动文件

- `Midora.Application/ProjectClipboardPasteTarget.cs`、`ProjectClipboardStorage.cs`：显式目标、后台有界分类及缓存。
- `Midora.Application/ProjectClipboardCreatedSelection.cs`、各 `ProjectObjectClipboard*.cs`：目标传递、两类 Segment 正式结果选择。
- `Midora.Desktop/ClipboardSelectionProjection.cs`、`DesktopSessionController.cs`、`MainWindow.xaml.cs`：后台投影构造及 SubVoice lane 提示。
- `Midora.Desktop.Presentation/Interaction/WorkspaceState.cs`：不继承旧选择的显式投影入口，维持固定成本发布和历史路由恢复。
- `ProjectClipboardPasteTargetTests.cs`、`ClipboardSelectionRoutingTests.cs`、`WorkspaceTimelineSelectionContextTests.cs`：对应分层回归。

手工验收：剪切→粘贴后不重新选择，直接打开右键批量菜单并使用 `Ctrl+Q / Ctrl+T / Ctrl+E`；检查三种钢琴卷帘、Arrangement Segment 和事件/参数点各自适用的操作。全程未使用 computer-use，未发布 dist。
