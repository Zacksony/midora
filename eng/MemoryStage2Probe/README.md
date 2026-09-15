# 阶段 2 WPF 生命周期探针

使用真实 `App` 资源、`MainWindow`、Workspace DataTemplate、`TimelineSurface`、绑定和离屏 WPF 绘制。不显示窗口、不使用 computer-use、不启动音频、不保存输入 Project、不发布 `dist`。

计时时串行运行测试、探针和构建。探针的 executable directory 是隔离的 ProgramRoot；配置与临时数据属于该目录，不使用已安装程序的数据目录。

## 复现

1. 修改/构建产品前，将完整 Release 输出复制到新的 `.tmp/memory-stage2/baseline/win-x64`，记录提交和 DLL hash。
2. 构建本项目，指定 `-p:MemoryProbeReferenceDirectory=<冻结目录绝对路径>`、独立 `--output <仓库>/.tmp/memory-stage2/probe-baseline`。
3. 运行 `dotnet <output>/Midora.Desktop.Tests.dll 100`。参数是**每 Project**循环次数，共两个 Project；每轮打开/绘制/关闭 Logical Segment、MIDI Segment、SubVoice、Conductor、All Tracks。
4. 同一探针对当前 Release 重复构建到 `probe-current`。大样本可用 `3 "<MIDI绝对路径>"`；只读导入到新会话。
5. 保存 stdout/stderr；小流程旧/新版本交替串行测三次。计时含模板首次构造和绘制，不是播放性能或稳态 FPS 测试。

旧/新版本调用同一既有 Controller API。只有窗口命令提示赋值通过反射适配旧字段/新弱属性；没有用新 Dispose 代替真实 Controller 的 Close 路径。使用 DispatcherSynchronizationContext，并将领域/VM局部放入 NoInlining helper；退栈后才做 Project 关闭存活检查。

## 解释边界

- 分开记录自然 managed/working/private 和受控 GC 后存活。GC 只用于探针，没有加到产品。
- 最终探针还记录 GC heap/fragmented/committed、累计托管分配、GC 次数、累计 CPU 时间和进程 Peak Working Set。GC heap 数据对应最近一次 GC，并非每次 Measure 都触发一次 GC；不能以 Working Set 减 managed 来估算 native 占用。
- MainWindow、Controller、共享 EditorSettings 保持存活，观察旧 VM/Surface/Snapshot 的弱引用；Snapshot 是登记次数，不是去重实例数。
- handlers 应与未关闭 owner 对应；合法冻结 bitmap 可以保留，但不能拖住旧 Workspace / Project。探针不主动 Clear LRU 来证明回收；旧版没有 RunningCount，不能用 InFlight=0证明全部扫描已退出。
- 离屏完整模板为 1200×760、96 DPI；手动触发 Surface Unloaded，不等同真实 Loaded、所有 DPI 或 OS 前台焦点。独立 HostedWorkspaceLifecycleTests 的隐藏 HwndSource 覆盖真实 Loaded/Unloaded 和逻辑焦点；人工验收清单覆盖实际快捷键与视觉体验。
