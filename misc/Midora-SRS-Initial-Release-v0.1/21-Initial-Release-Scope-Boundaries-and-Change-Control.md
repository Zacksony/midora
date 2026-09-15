# 第 21 章 初版范围边界、实现自由度与变更控制

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章汇总产品级范围，区分正式需求与实现设计自由度，并规定实现验证导致需求变化时的处理方式。

## 21.1 初版正式包含的能力
初版必须形成以下完整闭环：
```text
创建和打开 Project
设计 Event Instrument 与 SubVoice MIDI 事件
在 Logical Track / Segment 中编写 Logical Notes 与 Logical Parameters
创建 MIDI Channel Root，并在 Pure MIDI Track / Midi Segment 中直接编辑 MIDI Notes 与 Channel Events
把 SMF 1.0 Format 0/1、TPQN `.mid` 原子打开为新的未保存 Project
执行确定性编译与资源分配
使用程序级有序多 SF2/SFZ 列表及目标 Bank/Program 映射进行播放、预览和音频渲染
导出标准 MIDI 1.0 SMF Type 1
渲染普通 RIFF/WAVE、Stereo、IEEE 32-bit Float，并允许选择 8,000–192,000 Hz 整数采样率
保存、Save Copy 并重新打开 .midora Project
通过统一 UI、诊断和任务状态完成上述工作
```
## 21.2 产品级明确排除项
初版不支持：
```text
macOS、Linux、Web 或移动端
x86 Windows、Arm64 Windows 或 AnyCPU 发布包
MIDI 2.0
DAW、音频录制、音频轨、VST/VSTi 宿主
MPE 或 MIDI 2.0 per-note controllers
自由 SysEx / 任意 Meta payload 创建与字节编辑；不包括从 SMF 导入后按 opaque payload 保存、移动、删除和重新导出
程序级全局 Event Instrument Library
跨 Project Event Instrument 导入、导出或实时引用
多 Project 同时打开
多个可独立启动、显示 UI 或打开 Project 的 Midora 应用实例；不包括由唯一主实例管理的内部音频后端子进程
传统 Save As、自动保存或崩溃恢复
Pause、Scrubbing、Recording、Count-in、Legato、Tempo Ramp
由 Compiler、Overlap 或 Channel Group 实施的语义级 Voice Steal；不包括第 13.12.9、15.7.2 节明确允许的 BASSMIDI sample voice 资源上限
每 Project/Port/Track/Instrument 独立 SoundFont 列表
DLS、Kontakt、LV2 等 SF2/SFZ 以外的声音资源
纯键盘完整工作流、屏幕阅读器和 Access Keys
多主题、高对比度主题、额外交互音效
非 100% DPI 的专项验收承诺
SMF Format 2、SMPTE division、Import MIDI into Current Project、字节级 MIDI round-trip
```
各专项章节列出的更细禁止项同样属于正式范围，不因未在本章重复而失效。
## 21.3 实现设计自由度
本规格有意不固定以下实现选择：
```text
最终 C# namespace、class、record、struct 与字段布局
稳定 ID 值类型的具体 C# 声明与内部比较优化，但核心值必须为单个正 signed 64-bit integer，并服从 16.5.3 的分配语义和 16.13.2 的持久化布局
集合、索引和缓存的具体数据结构
Channel Group 分配的具体扫描算法
曲线离散化的内部优化算法，但结果必须与第 12.8.6 节逐整数 tick 参考语义完全一致
Tempo Map 的查找、索引和缓存结构，但 tick→sample 结果必须与第 4.1.4 节 decimal 积分和单次 Away From Zero 取整语义完全一致
Canonical Compiled Result 的最终内存布局
BASSMIDI / BASSWASAPI 的具体封装与安全调用顺序和音频线程模型，但 WASAPI 模式、格式与 period 请求必须符合第 13.14.7 节，正式实时工作 block 与 ring 容量必须符合第 13.19.2、13.19.9 节
内部音频子进程的类型拆分、轮询细节和共享内存字段打包，但不得改变第 13.30 节固定的完整子进程所有权、Native AOT、二进制 ABI 与零分配约束
Limiter 的循环展开、SIMD 和状态存储实现，但算法与参数必须符合第 13.17.6 节版本 2 语义
Roslyn Expression-mode 解析与 `System.Linq.Expressions` 绑定/缓存的具体类型拆分；但 Mapping Function ABI v3 的白名单、资源上限、Context 依赖和“不 Emit/加载 Project 源码程序集”不属于实现自由度
JSON Schema、protobuf .proto 的其他最终字段名、字段号与代码生成方式；16.5.3 已固定的 `nextStableId`、16.13.2 已固定的稳定 ID 表示和既有外层 ID 字段号除外
具体 WPF 控件、Visual Tree、MVVM 类型和 Timeline 虚拟化实现
错误码编号和自动化测试框架
```
这些内容可以通过技术原型、ADR、性能基准和实现测试确定，但不得改变本规格已经明确的用户语义、数据所有权、失败原子性、输出一致性和兼容边界。

实现选择同时遵守以下性能优先级：
```text
正确性、确定性、失败原子性和资源上限是硬约束
满足硬约束的候选中优先时间性能
时间与空间冲突时允许使用更多但有明确上限的内存换取速度
音频活动线程在 Playing / Buffering / Preview Playing / Rendering 中不得产生托管堆分配
约 200 ms 端到端实时延迟是性能测试基准，不是播放成败逻辑
```
## 21.4 范围变更规则
实现中发现技术不可行、成本异常或第三方能力不符合预期时，不得静默弱化需求。必须：
1. 记录验证事实和失败条件；
2. 明确受影响的 SRS 条款；
3. 提出替代方案和用户可见影响；
4. 修订 SRS 后再将变化视为正式范围；
5. 保持历史版本可追踪。
## 21.5 初版完成判定
产品达到初版范围至少要求：
```text
所有强制 Project 对象和编辑流程可用
Pure MIDI Root/Track/Segment 可编辑、可保存、可编译、可播放并保持独立 SMF MTrk 拓扑导出
Format 0/1 + TPQN、Running Status、多 Channel MTrk、MIDI Port 与 opaque event 导入通过兼容/畸形输入测试
核心对象可保存并无损重新打开
全量编译在相同输入下确定一致
播放、预览、MIDI 导出、音频渲染共享同一编译语义
资源不足、断裂引用、无效值、损坏资源和文件事务失败均有明确诊断
正式输出不受临时 Mute/Solo 或 UI 会话状态污染
长任务支持规定的锁定、取消和结果状态
启用的音频输出设备可以完整列出和选择，实时采样率跟随设备实际值
音频 buffer 设置可调且实际值可观察
音频性能验收覆盖线程零分配、underrun、IPC 延迟和约 200 ms 端到端基准
所有“初版不支持”能力不会以不完整正式功能暴露
```

## 21.6 免费开源定位与第三方许可发布门

Midora 初版固定为免费、开源、非商业软件。该产品决定不等于 BASS 已转为开源依赖，也不等于任何免费发布方式都自动满足 BASS 许可条件。

Midora 自有源代码固定采用标准 MIT License。该许可证允许使用、复制、修改、合并、发布、分发、再许可和销售 Midora 自有代码副本；“Midora 官方初版非商业”是项目自身发布定位，不得通过修改 MIT 正文变成对下游商业领域的限制。根目录 `LICENSE` 使用标准全文，版权署名固定为 `Copyright (c) 2026 Midora contributors`。

根据 2026-08-06 核验的 Un4seen 官方 BASS 授权页，免费使用条件同时涉及发布主体的非商业性质，以及产品不通过销售、广告等方式产生收入。正式发布前必须冻结并核验：

```text
实际发布主体及其商业 / 非商业性质
Midora 是否通过销售、广告、订阅、付费分发或其他产品收入获利
实际发布平台和分发方式
发布时有效的 BASS 官方条款及随包许可文件
第三方 notices 是否完整、准确并随产物提供
```

若任一条件不明确、实际发布方式发生变化或产品开始产生收入，不得继续沿用“免费非商业使用”结论；必须在分发 BASS 二进制前联系权利人确认或取得适用许可证。技术测试通过不代表该发布门已经通过。

Midora 仓库不提交 BASS DLL。Midora 的开源许可证不得覆盖或重新许可 BASS、BASSMIDI、BASSWASAPI；第三方声明必须明确商业使用者或商业 fork 需要自行核验并取得适用授权。

官方核验入口：

- [BASS 产品与授权](https://www.un4seen.com/bass.html)
- [Un4seen 关于 GitHub 免费开源发布与商业再使用的答复](https://www.un4seen.com/forum/?topic=20159.0)
- [OSI 批准的 MIT License 标准文本](https://opensource.org/license/mit)

## 21.7 产品版本、发布标识与规格基线

Midora 产品版本使用 `MAJOR.MINOR.PATCH[-prerelease]`。首个产品版本从 `1.0.0-dev` 开始，经 `1.0.0-rc.N` 进入 `1.0.0`；产品 SemVer 与 Project `fileFormatVersion`、component schema、Mapping ABI、Worker IPC、缓存 generation 和用户自由文本 Project Version 均为独立契约，不得用一个数字替代。

仓库必须只有一个产品版本源，并由它生成 Assembly/File/Informational Version、Project manifest 和 MIDI Export Readme 的软件版本。1.x 的 CLR `AssemblyVersion` 固定为 `1.0.0.0`；Windows `FileVersion` 使用产品三段版本加数值第四段。开发构建允许在 Informational Version 中追加 Git commit build metadata，但不得改变 Product Version。

正式版本使用不可移动的 annotated Git tag `v<version>`。发布 commit 必须工作区干净、通过版本一致性门、Format 兼容门、自动/人工发布门，并更新 Changelog、兼容说明、许可证与第三方 notices。RC 后不得加入未完成的大型能力；正式 tag 不得重打、移动或复用。

新 1.x 软件必须持续读取本产品线此前正式发布的 Project Format；旧软件不承诺读取未来格式。Project Format 是否提升只由持久化表示和语义变化决定，不由产品 MAJOR/MINOR/PATCH 机械决定。用户填写的 Project Version 只属于作品 metadata，不参与任何兼容判断。

当前 writer 为 Project Format 4。Format 1 是首个冻结兼容基线，Format 2 冻结 Event Instrument Pre-Roll 表示，Format 3 新增独立 Project presentation entry，Format 4 新增严格的 Instrument Change 编辑关联 source component（§16.35）；四个 reader/schema/golden 必须并存。Format 1/2/3 通过 detached migration 打开，普通 Save 只在用户确认、精确旧字节永久副本已安全存在后才可原路径升级；Save Copy 只写 Format 4 且不改变迁移状态。产品仍处于 `1.0.0-dev` 不构成破坏既有冻结格式的许可。

Midora 的程序级持久化采用 ProgramRoot portable layout；这属于部署/数据归属契约，不与产品 SemVer 或 Project Format 共用版本号。不同 ProgramRoot 必须互相隔离，且不得用隐式用户目录 fallback 掩盖不可写安装位置。
