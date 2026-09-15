# Int64 Diagnostics：真实 WPF 离屏探针

范围：正式 Compiler 的 65,537 个相交 Logical Note、2,147,516,416 条完整 Error diagnostics，真实 `MainWindow` Diagnostics DataTemplate、虚拟行、绑定和 RoutedEvent Button handlers。不显示或自动操作用户窗口，不创建音频 Worker。

先构建 Desktop Release；探针默认引用该构建，也可用 `DiagnosticProbeReferenceDirectory` 指向已冻结的完整 Desktop 输出。必须把探针输出放在新的、无 SoundFont 配置的工作区目录，避免接触用户配置；不会生成 `dist`。

```powershell
dotnet build src/midora-desktop/Midora.Desktop/Midora.Desktop.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet build eng/DiagnosticPagingProbe/DiagnosticPagingProbe.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:OutDir=D:/Programing/midora/.tmp/diagnostic-probe-bin/
$probeDotnet = (Get-Command dotnet -CommandType Application).Source
& $probeDotnet .tmp/memory-stage5/probe-tools-v7/MemoryStage5Probe.dll guard .tmp/diagnostic-probe-guard 8192 2048 $probeDotnet .tmp/diagnostic-probe-bin/Midora.Desktop.Tests.dll .tmp/diagnostic-probe-result
```

守护程序集应先按 `eng/MemoryStage5Probe/README.md` 构建；上面的路径是当前工作区已构建版本，不是仓库自带二进制。守护启动目标必须是完整可执行路径，不要传裸 `dotnet`。输出目录必须尚不存在；守护超过 8 GiB 私有提交或系统可用内存小于 2 GiB 时中止后代，不重试。

探针检查：初始分页、Next、页码输入超过 Int32、非法页码、末页来源、Tab suspend/resume、全源过滤、普通小列表恢复；输出 `result.json`、`diagnostic-page.png` 以及两个合法 Format 3 验收项目：`diagnostics-int64.midora`（超过 Int32 的 Error 列表）和 `diagnostics-readme-1275.midora`（51 个音符、1275 条 Warning，供导出 README 验收）。这些生成物只在忽略的输出目录中，不进入 Git。无 HWND 离屏树的 `IsVisible` 恒为 false，因此模板显示判定检查祖先 `Visibility`，不伪称真实桌面可见性或键盘焦点已验收。快捷键/鼠标手感仍需人工验收。

探针还通过真实诊断行 TextBlock / ListBox 路由 PreviewMouseWheel，验证普通列表、Int64 分页列表、单行和空列表的单刻度一行、正反/连续刻度、首尾消费，以及不改变选择、来源、页码和计数。此检查经过 MainWindow 正式 BAML handler 与共享 ScrollViewer 样式，不只测试独立滚动帮助函数。

预期 JSON 包含 `passed: true`、`wheelPassed: true`、完整 Count、实际生成行数、编译/筛选加布局耗时、内存采样和 assembly MVID；`guard-result.json` 另含整个进程树的峰值。不要把 JSON 终点内存当成峰值。
