# 诊断列表滚轮步进修复

日期：2026-09-09。

当前状态：修复已随 `560082e9` 提交、推送；用户在本次文档收尾中确认人工大体全部验收通过。后续真实编曲再做细致手感与查漏；下列自动测量仍与该总体反馈分别记录。

## 需求与边界

- 输入：诊断列表内的鼠标滚轮。
- 输出：沿用已有列表专用滚动器，每个普通滚轮刻度移动一行；方向与滚轮一致。
- 依据：本轮用户反馈、SRS §17.5（虚拟诊断列表）、§17.2.3（滚动为会话状态）、§20.15.3（局部内容滚动）。
- 边界：空列表、无需滚动、首尾位置均安全消费手势；不转交通用像素步进处理器二次滚动。
- 非目标：不改诊断内容、计数、筛选、分页、选择、导航、焦点、通用样式或其他列表；不改 Project/Undo/持久化，不新增诊断或设置。

## 已确认原因与实现

共享 `ScrollViewerWheelRouter` 对一次 `Delta=-120` 使用 `VerticalOffset + 40`。诊断列表启用了虚拟化，offset 的单位是行而非像素，因此实际跳过 40 行。

诊断列表在 Preview 阶段复用现有 `ListBoxWheelScroll.ScrollOneItemPerNotch`，先于内部 ScrollViewer 消费手势。显式固定 `CanContentScroll=True` 与 `ScrollUnit=Item`，保留 recycling 虚拟化与超大诊断的分页接口。共用滚动器及其他消费者不变。

## 验证

- 修复前：使用已冻结旧 Desktop 程序集与真实 BAML 模板重现，`Delta=-120` 后 offset 从 0 变为 **40**，单步断言失败。记录：`.tmp/diagnostics-wheel/baseline-guard/child.stderr.log`。
- 修复后：同一探针通过。每个普通刻度移动 **1 行**；覆盖 500 条普通列表、2,147,516,416 条诊断对应的 4096 行分页、单行、空列表，总计 32 次真实路由滚轮事件。连续正反、多刻度、零 delta、首尾边界均符合预期，选择/来源/页码/计数不变。
- 超大分页实际只生成 18 个 WPF 行容器；滚动后的容器数仍有界。探针峰值私有提交约 200 MiB，内存守护未触发。记录：`.tmp/diagnostics-wheel/fixed-result/result.json` 与 `fixed-guard/guard-result.json`。
- Desktop Release 构建及 **434/434** 自动测试通过，无失败、无跳过；包含新增 XAML 连接/按项滚动/虚拟化回归。记录：`.tmp/diagnostics-wheel/test-results/desktop.trx`。
- 实施时 `git diff --check` 通过，未使用 computer-use 或生成 dist。后续提交和人工大体验收状态见文首；本轮文档收尾不运行产品测试、不提交、推送或发布。
